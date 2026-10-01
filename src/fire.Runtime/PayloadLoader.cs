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
    /// Lädt DLLs zur Laufzeit aus der eigenen Datei (Ersatz für Costura/Fody). Die ausführbare Datei enthält nur das,
    /// was das Programm braucht (siehe Packer/PackagePlan); alles außer dem winzigen Start-Stück (fire.Runtime.dll)
    /// liegt als Payload hinter dem .NET-Bundle (siehe PayloadFile).
    ///
    /// - Verwaltete DLLs (fire.dll, MemoryPack, die benötigten Bridges): <see cref="AssemblyLoadContext.Resolving"/>
    ///   wird erst ausgelöst, wenn der Standard-Kontext eine Assembly nicht findet - also genau dann, wenn Code sie
    ///   zum ersten Mal wirklich braucht. Eine nicht eingebundene Bridge wird so nie angefasst.
    /// - Native Bibliotheken (SDL3): Windows kann eine DLL nicht aus dem Speicher laden, deshalb wird sie beim ersten
    ///   Zugriff einmalig in einen Cache-Ordner im Temp-Verzeichnis geschrieben (Unterordner = Prüfsumme, also je Version
    ///   genau einmal, auch über mehrere Programmstarts hinweg) und von dort geladen.
    ///
    /// Muss installiert sein, BEVOR irgendein Typ aus fire.dll berührt wird (siehe Program.cs/Bootstrap).
    /// </summary>
    public static class PayloadLoader
    {
        private static PayloadReader? _reader;
        private static readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, IntPtr> _nativeLoaded = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lock = new();

        /// <summary>Öffnet den Payload der eigenen Datei und hängt die Lader ein. false = diese Datei trägt keinen Payload
        /// (z.B. die nackte Runtime aus dem Build-Ordner).</summary>
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

        /// <summary>Das serialisierte Programm (MemoryPack) aus dem Payload; null, wenn keins da oder beschädigt.</summary>
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
                    throw new BadImageFormatException($"'{name.Name}' in der Programmdatei ist beschädigt (Prüfsumme stimmt nicht).");

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
        /// kommen mit oder ohne Endung.</summary>
        private static bool NameMatches(string fileName, string requested)
        {
            if (string.Equals(fileName, requested, StringComparison.OrdinalIgnoreCase)) return true;
            return string.Equals(Path.GetFileNameWithoutExtension(fileName), requested, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Schreibt die native Bibliothek in den Cache-Ordner (falls dort noch nicht vorhanden) und gibt den
        /// Pfad zurück.</summary>
        private static string? Extract(PayloadEntry entry)
        {
            var dir = Path.Combine(Path.GetTempPath(), "fire-native", Convert.ToHexString(entry.Sha256, 0, 8));
            var path = Path.Combine(dir, entry.Name);
            if (File.Exists(path) && new FileInfo(path).Length == entry.RawLength)
                return path;

            var bytes = _reader!.Read(entry);
            if (bytes == null) return null;

            Directory.CreateDirectory(dir);
            // Erst in eine eindeutige Zwischendatei, dann umbenennen: zwei gleichzeitig startende Programme sehen nie eine
            // halb geschriebene DLL.
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            try { File.Move(tmp, path, overwrite: true); }
            catch (IOException)
            {
                // Ein anderer Prozess hat sie gerade geladen/ersetzt - deren Kopie ist identisch.
                try { File.Delete(tmp); } catch (IOException) { }
            }
            return path;
        }
    }
}
