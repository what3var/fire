using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace fire.Runtime
{
    /// <summary>
    /// Determines which DLLs a packed program gets: always the core (fire.dll together with dependencies like
    /// MemoryPack), plus per `#import` only the associated bridge with everything it needs - bridges that the program
    /// does not include thus cost no space in the finished file.
    ///
    /// Dependencies are not maintained by hand, but determined from the assembly references of the DLLs (metadata):
    /// every assembly that lies as a file next to the compiler and does not belong to the .NET framework comes along. By hand
    /// only the entry points per import are stated here, as well as the native libraries (which do not
    /// emerge from the metadata).
    /// </summary>
    public sealed class PackagePlan
    {
        /// <summary>What an import brings along: managed entry assemblies (the rest follows from the references) and native
        /// libraries (file names per operating system; what does not exist on this platform is dropped).</summary>
        private sealed record ImportPackage(string[] Assemblies, string[] Natives);

        private static readonly string[] CoreAssemblies = { "fire" };

        private static readonly Dictionary<string, ImportPackage> Imports = new()
        {
            [NativeImports.Print] = new(Array.Empty<string>(), Array.Empty<string>()),
            [NativeImports.Graphics] = new(
                new[] { "fire.Terminal.Bridge" }, Array.Empty<string>()),
            // the SDL window: own assembly together with SDL (`graphics` alone gets by without)
            [NativeImports.Windows] = new(
                new[] { "fire.Windows.Bridge", "fire.Terminal.Windows", "fire.Terminal.Sdl" },
                new[] { "SDL3.dll", "libSDL3.so.0", "libSDL3.dylib" }),
            // `devices` is a package (C++ in a library) that works with the device manager of the host: the manager and its serial driver come along
            [NativeImports.Devices] = new(
                new[] { "fire.Device.Manager" },
                new[] { "libSystem.IO.Ports.Native.so", "libSystem.IO.Ports.Native.dylib" }),
            // pure fire source (in the program itself), needs no DLL - `graphics` comes in via the import itself
            [NativeImports.Ui] = new(Array.Empty<string>(), Array.Empty<string>()),
            // pure fire source, needs no DLL
            [NativeImports.Linq] = new(Array.Empty<string>(), Array.Empty<string>()),
            [NativeImports.Reflection] = new(Array.Empty<string>(), Array.Empty<string>()),
        };

        /// <summary>Verwaltete DLLs: Assembly-Name -> Pfad.</summary>
        public SortedDictionary<string, string> Assemblies { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Native Bibliotheken: Dateiname -> Pfad.</summary>
        public SortedDictionary<string, string> Natives { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>References that neither lie as a file next to the compiler nor belong to the framework (should be empty;
        /// otherwise something is missing from the finished file at runtime).</summary>
        public SortedSet<string> Unresolved { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static PackagePlan Create(IEnumerable<string> nativeImports, string baseDir, IEnumerable<string>? extraNativeFiles = null)
        {
            var plan = new PackagePlan();
            var searchDirs = SearchDirectories(baseDir);
            var frameworkDir = RuntimeEnvironment.GetRuntimeDirectory();

            // the shared libraries of the natives of packages travel in the payload like the native libraries of the bridges
            if (extraNativeFiles != null)
                foreach (var file in extraNativeFiles)
                    if (File.Exists(file)) plan.Natives[Path.GetFileName(file)] = file;

            var queue = new Queue<string>(CoreAssemblies);
            foreach (var import in nativeImports)
            {
                // an import of a package: its prelude is part of the program, its natives are C++ in a library (which travels as a native file); only the standard packages that work with
                // a part of the host bring DLLs along (`devices`: the device manager)
                string importName = import.StartsWith("pkg:", StringComparison.Ordinal) ? import.Substring(4) : import;
                if (import.StartsWith("pkg:", StringComparison.Ordinal) && importName != NativeImports.Devices) continue;
                if (!Imports.TryGetValue(importName, out var package))
                    throw new InvalidOperationException($"Unknown import '{import}' - the packer does not know which DLLs it needs.");
                foreach (var asm in package.Assemblies) queue.Enqueue(asm);
                foreach (var native in package.Natives)
                {
                    var path = FindNative(native, baseDir);
                    if (path != null) plan.Natives[native] = path;
                }
            }

            while (queue.Count > 0)
            {
                var name = queue.Dequeue();
                if (plan.Assemblies.ContainsKey(name)) continue;

                var path = FindAssembly(name, searchDirs);
                if (path == null)
                {
                    // If it belongs to the .NET framework, it comes from there at runtime - everything else is really missing.
                    if (!File.Exists(Path.Combine(frameworkDir, name + ".dll")))
                        plan.Unresolved.Add(name);
                    continue;
                }
                if (File.Exists(Path.Combine(frameworkDir, name + ".dll")))
                    continue;

                plan.Assemblies[name] = path;
                foreach (var reference in ReadReferences(path))
                    queue.Enqueue(reference);
            }
            return plan;
        }

        /// <summary>Platform-specific subfolders (runtimes/win/lib/..., runtimes/unix/lib/...) before the main folder -
        /// the same as the deps.json tells the host (e.g. System.IO.Ports in the main folder supplies only a dummy,
        /// the real assembly lies under runtimes/win or runtimes/unix).</summary>
        private static List<string> SearchDirectories(string baseDir)
        {
            var dirs = new List<string>();
            foreach (var rid in RuntimeIdentifiers())
            {
                var lib = Path.Combine(baseDir, "runtimes", rid, "lib");
                if (!Directory.Exists(lib)) continue;
                var tfms = Directory.GetDirectories(lib);
                Array.Sort(tfms, StringComparer.OrdinalIgnoreCase);
                Array.Reverse(tfms); // neuestes Framework zuerst
                dirs.AddRange(tfms);
            }
            dirs.Add(baseDir);
            return dirs;
        }

        private static string? FindAssembly(string name, List<string> dirs)
        {
            foreach (var dir in dirs)
            {
                var path = Path.Combine(dir, name + ".dll");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private static string? FindNative(string fileName, string baseDir)
        {
            foreach (var rid in RuntimeIdentifiers())
            {
                var path = Path.Combine(baseDir, "runtimes", rid, "native", fileName);
                if (File.Exists(path)) return path;
            }
            var direct = Path.Combine(baseDir, fileName);
            return File.Exists(direct) ? direct : null;
        }

        /// <summary>Runtime-Identifier der Plattform, in Suchreihenfolge (spezifisch vor allgemein).</summary>
        private static IEnumerable<string> RuntimeIdentifiers()
        {
            string arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            if (OperatingSystem.IsWindows()) { yield return "win-" + arch; yield return "win"; }
            else if (OperatingSystem.IsMacOS()) { yield return "osx-" + arch; yield return "osx"; yield return "unix"; }
            else { yield return "linux-" + arch; yield return "linux"; yield return "unix"; }
        }

        private static IEnumerable<string> ReadReferences(string assemblyPath)
        {
            using var fs = File.OpenRead(assemblyPath);
            using var pe = new PEReader(fs);
            var md = pe.GetMetadataReader();
            foreach (var handle in md.AssemblyReferences)
                yield return md.GetString(md.GetAssemblyReference(handle).Name);
        }
    }
}
