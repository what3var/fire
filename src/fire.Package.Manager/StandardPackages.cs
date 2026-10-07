namespace fire.Package.Manager
{
    /// <summary>
    /// The standard bridges of fire as packages (docs/PACKAGES.md): one package per bridge (<c>fire-io</c>, <c>fire-time</c>, ...) with its prelude and its C++ sources. The build of the solution
    /// writes them into the folder <c>PackageSource</c> next to the compiler (<c>fire.Compiler bridge-packages</c>); when spark, forge or ember start, <see cref="EnsureInstalled"/> installs
    /// the ones that are missing (or older). The compiler still resolves these names to its built-in bridges first.
    /// </summary>
    public static class StandardPackages
    {
        public const string Prefix = "fire-";

        /// <summary>The bridges that are packaged: the names of their imports.</summary>
        public static readonly IReadOnlyList<string> Bridges = new[] { "graphics", "windows", "devices", "io", "ui", "linq", "reflection", "time", "random", "net", "tls", "http", "gpio", "i2c", "spi" };

        public static string PackageNameOf(string bridge) => Prefix + bridge;

        private static string StampFile => Path.Combine(PackageStore.Default.Root, ".standard-stamp");

        /// <summary>Checks that the standard packages are installed and installs what is missing or older than the files in <paramref name="sourceFolder"/> (default: <c>PackageSource</c> next to the
        /// program). Quick when nothing changed: it only compares the age of the package files with the time of the last installation. Returns what was installed; problems are reported to
        /// <paramref name="log"/>, never thrown (a program must start without them).</summary>
        public static IReadOnlyList<string> EnsureInstalled(Action<string>? log = null, string? sourceFolder = null)
        {
            var installed = new List<string>();
            try
            {
                string folder = sourceFolder ?? EmberSettings.DefaultSourceFolder;
                if (!Directory.Exists(folder)) return installed;
                var files = Directory.GetFiles(folder, Prefix + "*" + Fpk.Extension);
                if (files.Length == 0) return installed;

                var store = PackageStore.Default;
                bool allThere = Bridges.All(b => store.Find(PackageNameOf(b)) != null);
                DateTime newest = files.Max(File.GetLastWriteTimeUtc);
                DateTime stamp = File.Exists(StampFile) ? File.GetLastWriteTimeUtc(StampFile) : DateTime.MinValue;
                if (allThere && stamp >= newest) return installed;

                var service = new PackageManagerService(store, new IPackageSource[] { new FolderSource(folder) });
                foreach (var bridge in Bridges)
                {
                    try
                    {
                        string name = PackageNameOf(bridge);
                        var listing = service.Find(name).FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (listing?.Latest == null) continue;
                        // a package file newer than the last check was built again (the version of a build is the same): it is installed again
                        string? file = files.FirstOrDefault(f => Path.GetFileName(f).StartsWith(name + "-", StringComparison.OrdinalIgnoreCase));
                        bool rebuilt = file != null && File.GetLastWriteTimeUtc(file) > stamp;
                        if (!rebuilt && store.Find(name) is { } have && SemanticVersion.TryParse(have.Version, out var h) && SemanticVersion.TryParse(listing.Latest.Version, out var l) && h.CompareTo(l) >= 0) continue;
                        log?.Invoke($"Installing the standard package {name} {listing.Latest.Version}...");
                        service.Install(name + "@" + listing.Latest.Version, log, force: rebuilt);
                        installed.Add(name);
                    }
                    catch (PackageException ex) { log?.Invoke(ex.Message); }
                }
                Directory.CreateDirectory(store.Root);
                File.WriteAllText(StampFile, DateTime.UtcNow.ToString("O"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PackageException)
            {
                log?.Invoke("The standard packages could not be checked: " + ex.Message);
            }
            return installed;
        }
    }
}
