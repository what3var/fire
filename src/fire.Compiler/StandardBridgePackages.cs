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

        /// <summary>The prelude of a bridge: for a bridge that already runs only as a package, its source in the compiler; for the others the built-in one.</summary>
        private static string? PreludeOf(string bridge) => bridge switch
        {
            "time" => fire.Standard.TimePrelude.Source,
            _ => ImportedPreludes.TrySourceFor(bridge),
        };

        /// <summary>The natives a bridge brings as a package (those the compiler does not know itself yet): the fire name, the number of arguments and the C++ function of the bridge
        /// (see native/bridges/). `list`: the function allocates its result in the list of the scope; `host`: the VM runs it itself.</summary>
        private static IEnumerable<PackageNativeFunction> FunctionsOf(string bridge)
        {
            static PackageNativeFunction F(string name, int arguments, string cpp, bool list = false, bool host = false) =>
                new() { Name = name, Arguments = arguments, Cpp = cpp, NeedsList = list, ReturnsReference = list, Host = host };
            if (bridge == "time")
            {
                yield return F("Sleep", 1, "sleepNative", host: true);
                yield return F("__time_now", 0, "tm_now");
                yield return F("__time_local_offset", 1, "tm_localOffset");
                yield return F("__time_parts", 1, "tm_parts", list: true);
                yield return F("__time_make", 7, "tm_make");
                yield return F("__time_parse", 1, "tm_parse");
                yield return F("__time_format", 2, "tm_format", list: true);
                yield return F("__time_add_months", 2, "tm_addMonths");
                yield return F("__time_days_in_month", 2, "tm_daysInMonth");
                yield return F("__time_to_ticks", 2, "tm_toTicks");
                yield return F("__time_unit_ticks", 1, "tm_unitTicks", host: true);
                yield return F("__time_span_text", 1, "tm_spanText", list: true);
            }
        }

        /// <summary>The exception classes of the prelude that the natives of a bridge throw.</summary>
        private static IEnumerable<string> ExceptionsOf(string bridge) => bridge == "time" ? new[] { "TimeException" } : Array.Empty<string>();

        /// <summary>Builds the library of the natives of a bridge for this machine (from the package just forged, installed into a store of its own) with a C++ compiler if there is one; the path of the
        /// file in <paramref name="libFolder"/>, or null (no compiler here: nothing is asked in a build).</summary>
        private static string? PrebuiltLibrary(string bridge, string fpk, string libFolder)
        {
            try
            {
                var store = new PackageStore(Path.Combine(Path.GetDirectoryName(libFolder)!, "store"));
                store.Install(fpk);
                var installed = store.FindImport(bridge);
                if (installed == null) return null;
                string built = PackageLibrary.Ensure(installed);
                string dest = Path.Combine(libFolder, PackageLibrary.Rid, Path.GetFileName(built));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(built, dest, overwrite: true);
                return dest;
            }
            catch (PackageException ex)
            {
                Console.Error.WriteLine($"fire-{bridge}: no prebuilt library for {PackageLibrary.Rid} ({ex.Message.Split('\n')[0]})");
                return null;
            }
        }

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
                    string? prelude = PreludeOf(bridge);
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
                    native.Functions.AddRange(FunctionsOf(bridge));
                    native.Exceptions.AddRange(ExceptionsOf(bridge));
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
                    if (import.Native != null && import.Native.Functions.Any(f => !f.Host) && PrebuiltLibrary(bridge, forged.PackagePath, Path.Combine(dir, "lib")) is { } library)
                    {
                        // the library for this machine goes into the package: the users of this machine need no C++ compiler (the other systems build it from the C++ source when they first use it)
                        import.Native.Libraries[PackageLibrary.Rid] = library;
                        manifest.Save(forge);
                        forged = Fpk.Forge(forge, Path.Combine(dir, "build"));
                    }
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
