using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace fire.Runtime
{
    /// <summary>
    /// Bestimmt, welche DLLs ein gepacktes Programm mitbekommt: immer der Kern (fire.dll samt Abhängigkeiten wie
    /// MemoryPack), dazu je `#import` nur die zugehörige Bridge samt allem, was sie braucht - Bridges, die das Programm
    /// nicht einbindet, kosten also keinen Platz in der fertigen Datei.
    ///
    /// Abhängigkeiten werden nicht von Hand gepflegt, sondern aus den Assembly-Verweisen der DLLs (Metadaten) ermittelt:
    /// jede Assembly, die als Datei neben dem Compiler liegt und nicht zum .NET-Framework gehört, kommt mit. Von Hand
    /// stehen hier nur die Einstiegspunkte je Import sowie die nativen Bibliotheken (die aus den Metadaten nicht
    /// hervorgehen).
    /// </summary>
    public sealed class PackagePlan
    {
        /// <summary>Was ein Import mitbringt: verwaltete Einstiegs-Assemblies (der Rest folgt aus den Verweisen) und native
        /// Bibliotheken (Dateinamen je Betriebssystem; was es auf dieser Plattform nicht gibt, entfällt).</summary>
        private sealed record ImportPackage(string[] Assemblies, string[] Natives);

        private static readonly string[] CoreAssemblies = { "fire" };

        private static readonly Dictionary<string, ImportPackage> Imports = new()
        {
            [NativeImports.Print] = new(Array.Empty<string>(), Array.Empty<string>()),
            [NativeImports.Graphics] = new(
                new[] { "fire.Terminal.Bridge", "fire.Terminal.Windows", "fire.Terminal.Sdl" },
                new[] { "SDL3.dll", "libSDL3.so.0", "libSDL3.dylib" }),
            [NativeImports.Devices] = new(
                new[] { "fire.Device.Bridge", "fire.Device.Manager" },
                new[] { "libSystem.IO.Ports.Native.so", "libSystem.IO.Ports.Native.dylib" }),
            [NativeImports.IO] = new(new[] { "fire.IO.Bridge" }, Array.Empty<string>()),
            // reiner fire-Quelltext (im Programm selbst), braucht keine DLL - `graphics` kommt über den Import selbst dazu
            [NativeImports.Ui] = new(Array.Empty<string>(), Array.Empty<string>()),
            // reiner fire-Quelltext, braucht keine DLL
            [NativeImports.Linq] = new(Array.Empty<string>(), Array.Empty<string>()),
            [NativeImports.Reflection] = new(Array.Empty<string>(), Array.Empty<string>()),
            [NativeImports.Time] = new(Array.Empty<string>(), Array.Empty<string>()),
        };

        /// <summary>Verwaltete DLLs: Assembly-Name -> Pfad.</summary>
        public SortedDictionary<string, string> Assemblies { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Native Bibliotheken: Dateiname -> Pfad.</summary>
        public SortedDictionary<string, string> Natives { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Verweise, die weder als Datei neben dem Compiler liegen noch zum Framework gehören (sollte leer sein;
        /// sonst fehlt der fertigen Datei zur Laufzeit etwas).</summary>
        public SortedSet<string> Unresolved { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static PackagePlan Create(IEnumerable<string> nativeImports, string baseDir)
        {
            var plan = new PackagePlan();
            var searchDirs = SearchDirectories(baseDir);
            var frameworkDir = RuntimeEnvironment.GetRuntimeDirectory();

            var queue = new Queue<string>(CoreAssemblies);
            foreach (var import in nativeImports)
            {
                if (!Imports.TryGetValue(import, out var package))
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
                    // Gehört es zum .NET-Framework, kommt es zur Laufzeit von dort - alles andere fehlt wirklich.
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

        /// <summary>Plattformspezifische Unterordner (runtimes/win/lib/..., runtimes/unix/lib/...) vor dem Hauptordner -
        /// dasselbe, was die deps.json dem Host vorgibt (z.B. liefert System.IO.Ports im Hauptordner nur eine Attrappe,
        /// die echte Assembly liegt unter runtimes/win bzw. runtimes/unix).</summary>
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
