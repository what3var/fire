using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace fire.Runtime
{
    /// <summary>
    /// Loads DLLs at runtime from its own file (replacement for Costura/Fody). The executable file contains only what
    /// the program needs (see Packer/PackagePlan); everything except the tiny start piece (fire.Runtime.dll)
    /// lies as a payload behind the .NET bundle (see PayloadFile).
    ///
    /// - Managed DLLs (fire.dll, MemoryPack, the required bridges): <see cref="AssemblyLoadContext.Resolving"/>
    ///   is only raised when the default context does not find an assembly - that is, exactly when code
    ///   really needs it for the first time. A bridge that is not included is thus never touched.
    /// - Native libraries (SDL3): Windows cannot load a DLL from memory, so on first
    ///   access it is written once into a cache folder in the temp directory (subfolder = checksum, i.e. exactly once per version,
    ///   also across several program starts) and loaded from there.
    ///
    /// Must be installed BEFORE any type from fire.dll is touched (see Program.cs/Bootstrap).
    /// </summary>
    public static class PayloadLoader
    {
        private static PayloadReader? _reader;
        private static readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, IntPtr> _nativeLoaded = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

        /// <summary>Opens the payload of its own file and hooks in the loaders. false = this file carries no payload
        /// (e.g. the bare runtime from the build folder).</summary>
        public static bool Install(string? exePath = null)
        {
            exePath ??= Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return false;

            _reader = PayloadFile.Open(exePath);
            if (_reader == null) return false;

            AssemblyLoadContext.Default.Resolving += OnResolveManaged;
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolveNative;
            return true;
        }

        /// <summary>The serialised program (MemoryPack) from the payload; null if there is none or it is damaged.</summary>
        public static byte[]? ReadProgram()
        {
            var entry = _reader?.Find(PayloadKind.Program, "program");
            return entry == null ? null : _reader!.Read(entry);
        }

        private static Assembly? OnResolveManaged(AssemblyLoadContext context, AssemblyName name)
        {
            if (_reader == null || name.Name == null) return null;
            lock (_lock)
            {
                if (_loaded.TryGetValue(name.Name, out var done)) return done;

                var entry = _reader.Find(PayloadKind.Assembly, name.Name);
                if (entry == null) return null;
                var bytes = _reader.Read(entry);
                if (bytes == null)
                    throw new BadImageFormatException($"'{name.Name}' in the program file is corrupt (checksum mismatch).");

                var assembly = context.LoadFromStream(new MemoryStream(bytes));
                _loaded[name.Name] = assembly;
                return assembly;
            }
        }

        private static IntPtr OnResolveNative(Assembly requester, string libraryName)
        {
            if (_reader == null) return IntPtr.Zero;
            lock (_lock)
            {
                if (_nativeLoaded.TryGetValue(libraryName, out var done)) return done;

                foreach (var entry in _reader.Entries)
                {
                    if (entry.Kind != PayloadKind.Native || !NameMatches(entry.Name, libraryName)) continue;

                    var path = Extract(entry);
                    if (path == null) return IntPtr.Zero;
                    var handle = NativeLibrary.Load(path);
                    _nativeLoaded[libraryName] = handle;
                    return handle;
                }
                return IntPtr.Zero;
            }
        }

        /// <summary>"SDL3" trifft "SDL3.dll", "libSystem.IO.Ports.Native" trifft "libSystem.IO.Ports.Native.so" - DllImport-Namen
        /// come with or without an extension.</summary>
        private static bool NameMatches(string fileName, string requested)
        {
            if (string.Equals(fileName, requested, StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(Path.GetFileNameWithoutExtension(fileName), requested, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Writes the native library into the cache folder (if not already there) and returns the
        /// path.</summary>
        private static string? Extract(PayloadEntry entry)
        {
            var dir = Path.Combine(Path.GetTempPath(), "fire-native", Convert.ToHexString(entry.Sha256, 0, 8));
            var path = Path.Combine(dir, entry.Name);
            if (File.Exists(path) && new FileInfo(path).Length == entry.RawLength)
                return path;

            var bytes = _reader!.Read(entry);
            if (bytes == null) return null;

            Directory.CreateDirectory(dir);
            // First into a unique intermediate file, then rename: two programs starting at the same time never see a
            // half-written DLL.
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            try { File.Move(tmp, path, overwrite: true); }
            catch (IOException)
            {
                // Another process has just loaded/replaced it - its copy is identical.
                try { File.Delete(tmp); } catch (IOException) { }
            }
            return path;
        }
    }
}
