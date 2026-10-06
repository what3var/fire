using fire.Native;
using fire.Package.Manager;

namespace fire.Compiler
{
    /// <summary>
    /// Builds the packages of the standard bridges (<see cref="StandardPackages"/>): for each bridge a <c>fire-name-version.fpk</c> with the prelude (fire source) and the C++ sources of the
    /// bridge (native/bridges/). The build of the solution runs <c>fire.Compiler bridge-packages &lt;folder&gt;</c> after the compiler is built and puts the files into the folder
    /// <c>PackageSource</c> of the output.
    /// </summary>
    public static class StandardBridgePackages
    {
        /// <summary>The C++ files of a bridge (resource names below `bridges/`): its header, and the files in the folder of the same name that it includes.</summary>
        private static IEnumerable<string> NativeResources(string bridge)
        {
            string header = $"bridges/fire_bridge_{bridge}.hpp";
            if (NativeRuntimeFiles.ReadText(header) == null) yield break;
            yield return header;
            foreach (string name in NativeRuntimeFiles.ResourceNames($"bridges/{bridge}/")) yield return name;
        }

        /// <summary>The version of the standard packages: the version of this compiler.</summary>
        public static string Version
        {
            get
            {
                var v = typeof(StandardBridgePackages).Assembly.GetName().Version ?? new Version(1, 0, 0);
                return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
            }
        }

        private static IEnumerable<string> RequiresOf(string bridge) => bridge switch
        {
            "windows" => new[] { "graphics" },
            "ui" => new[] { "graphics", "windows" },
            "linq" => new[] { "reflection" },
            _ => Array.Empty<string>(),
        };

        /// <summary>Writes the package files into <paramref name="outputFolder"/> (replacing older ones) and returns their paths.</summary>
        public static IReadOnlyList<string> Build(string outputFolder)
        {
            Directory.CreateDirectory(outputFolder);
            var result = new List<string>();
            string work = Path.Combine(Path.GetTempPath(), "fire-bridgepkg-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (string bridge in StandardPackages.Bridges)
                {
                    string dir = Path.Combine(work, bridge);
                    Directory.CreateDirectory(dir);
                    var import = new PackageImport { Name = bridge, Standard = true, Requires = RequiresOf(bridge).ToList() };
                    string? prelude = ImportedPreludes.TrySourceFor(bridge);
                    if (prelude != null)
                    {
                        string file = Path.Combine(dir, bridge + ".fire");
                        File.WriteAllText(file, prelude);
                        import.Prelude = file;
                    }
                    var native = new PackageNative();
                    foreach (string resource in NativeResources(bridge))
                    {
                        string file = Path.Combine(dir, Path.GetFileName(resource));
                        if (File.Exists(file)) continue;
                        File.WriteAllText(file, NativeRuntimeFiles.ReadText(resource)!);
                        native.Sources.Add(file);
                    }
                    if (native.Sources.Count > 0) import.Native = native;
                    var manifest = new PackageManifest
                    {
                        Name = StandardPackages.PackageNameOf(bridge), Version = Version, Author = "fire",
                        Description = $"The standard bridge '{bridge}' of fire: prelude" + (import.Native != null ? " and C++ sources." : "."),
                        License = "fire", Imports = { import },
                        Dependencies = RequiresOf(bridge).Select(StandardPackages.PackageNameOf).ToList(),
                    };
                    string forge = Path.Combine(dir, "package.json");
                    manifest.Save(forge);
                    var forged = Fpk.Forge(forge, Path.Combine(dir, "build"));
                    foreach (var old in Directory.GetFiles(outputFolder, $"{manifest.Name}-*{Fpk.Extension}")) File.Delete(old);
                    string target = Path.Combine(outputFolder, Path.GetFileName(forged.PackagePath));
                    File.Copy(forged.PackagePath, target, overwrite: true);
                    result.Add(target);
                }
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (IOException) { }
            }
            return result;
        }
    }
}
