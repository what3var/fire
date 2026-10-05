using System.Text.Json;

namespace fire.Package.Manager
{
    /// <summary>The sources and the default places of ember: `ember.json` in the folder of the compiler lists them (`{ "sources": [ "PackageSource", "https://.../index.json" ] }`);
    /// without the file these are the folder `PackageSource` next to the compiler and <see cref="DefaultIndexUrl"/>.</summary>
    public static class EmberSettings
    {
        public const string FileName = "ember.json";
        /// <summary>The page with the index of the published packages.</summary>
        public const string DefaultIndexUrl = "https://what3var.github.io/fire-packages/index.json";
        public static string DefaultSourceFolder => Path.Combine(AppContext.BaseDirectory, "PackageSource");

        public static IReadOnlyList<IPackageSource> LoadSources(string? baseDirectory = null)
        {
            string baseDir = baseDirectory ?? AppContext.BaseDirectory;
            var specs = new List<string>();
            string file = Path.Combine(baseDir, FileName);
            if (File.Exists(file))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (doc.RootElement.TryGetProperty("sources", out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var item in list.EnumerateArray()) if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s) specs.Add(s);
                }
                catch (JsonException ex) { throw new PackageException($"{file} is not valid: {ex.Message}"); }
            }
            else
            {
                specs.Add("PackageSource");
                specs.Add(DefaultIndexUrl);
            }
            return specs.Select(s => Create(s, baseDir)).ToList();
        }

        public static IPackageSource Create(string spec, string baseDirectory)
        {
            bool web = spec.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || spec.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
            if (web || spec.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return new IndexSource(web ? spec : Path.GetFullPath(Path.Combine(baseDirectory, spec)));
            return new FolderSource(Path.GetFullPath(Path.Combine(baseDirectory, spec)));
        }
    }

    /// <summary>find, install and remove: what the command line and the editor do.</summary>
    public sealed class PackageManagerService
    {
        public PackageStore Store { get; }
        public IReadOnlyList<IPackageSource> Sources { get; }

        public PackageManagerService(PackageStore? store = null, IReadOnlyList<IPackageSource>? sources = null)
        {
            Store = store ?? PackageStore.Default;
            Sources = sources ?? EmberSettings.LoadSources();
        }

        /// <summary>The packages of all sources that match <paramref name="query"/> (part of the name or of the description; empty: all). A source that cannot be read adds a line to
        /// <paramref name="warnings"/> and is skipped. Exact name matches come first.</summary>
        public IReadOnlyList<PackageListing> Find(string query, ICollection<string>? warnings = null)
        {
            var all = new List<PackageListing>();
            foreach (var source in Sources)
            {
                try { all.AddRange(source.List()); }
                catch (PackageException ex) { warnings?.Add(ex.Message); }
            }
            // the same package from several sources: the versions are put together
            var merged = all.GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Count() == 1 ? g.First()
                : new PackageListing(g.First().Name, g.First().Author, g.First().Description,
                    g.SelectMany(x => x.Versions).GroupBy(v => v.Version).Select(v => v.First()).ToList(), string.Join(", ", g.Select(x => x.Source).Distinct()))).ToList();
            query = (query ?? "").Trim();
            return merged
                .Where(l => query.Length == 0 || l.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || l.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => string.Equals(l.Name, query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Installs `name`, `name@1.2.0` or a package file (`path.fpk`) with the packages it depends on. Returns what was installed.</summary>
        public IReadOnlyList<InstalledPackage> Install(string spec, Action<string>? log = null, bool force = false)
        {
            var installed = new List<InstalledPackage>();
            InstallCore(spec, log, force, installed, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return installed;
        }

        private void InstallCore(string spec, Action<string>? log, bool force, List<InstalledPackage> installed, HashSet<string> inProgress)
        {
            string temp = Path.Combine(Path.GetTempPath(), "ember-" + Guid.NewGuid().ToString("N"));
            try
            {
                string fpk;
                if (spec.EndsWith(Fpk.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(spec)) fpk = Path.GetFullPath(spec);
                else
                {
                    string name = spec, wanted = "";
                    int at = spec.IndexOf('@');
                    if (at > 0) { name = spec.Substring(0, at); wanted = spec.Substring(at + 1); }
                    var warnings = new List<string>();
                    var listing = Find(name, warnings).FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (listing == null)
                        throw new PackageException($"The package '{name}' was not found" + (warnings.Count > 0 ? ":\n  " + string.Join("\n  ", warnings) : "."));
                    var version = wanted.Length == 0 ? listing.Latest : listing.Versions.FirstOrDefault(v => v.Version == wanted);
                    if (version == null) throw new PackageException($"The package '{name}' has no version '{wanted}' (available: {string.Join(", ", listing.Versions.Select(v => v.Version))}).");
                    if (!force && Store.Find(listing.Name) is { } already && already.Version == version.Version) { log?.Invoke($"{listing.Name} {version.Version} is already installed."); return; }
                    log?.Invoke($"Fetching {listing.Name} {version.Version}...");
                    fpk = PackageDownloads.Fetch(version, temp);
                }

                var manifest = Fpk.ReadManifest(fpk);
                if (!inProgress.Add(manifest.Name)) return;   // a cycle of dependencies
                foreach (var dependency in manifest.Dependencies)
                    if (Store.Find(dependency) == null) { log?.Invoke($"{manifest.Name} needs {dependency}."); InstallCore(dependency, log, false, installed, inProgress); }
                var package = Store.Install(fpk);
                installed.Add(package);
                log?.Invoke($"Installed {package.Name} {package.Version} (imports: {string.Join(", ", package.Manifest.Imports.Select(i => i.Name))}).");
            }
            finally
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            }
        }

        /// <summary>Removes an installed package; another installed package that depends on it makes it an error (unless <paramref name="force"/>).</summary>
        public void Remove(string name, bool force = false)
        {
            var package = Store.Find(name) ?? throw new PackageException($"The package '{name}' is not installed.");
            var dependents = Store.DependentsOf(package.Name);
            if (dependents.Count > 0 && !force)
                throw new PackageException($"'{package.Name}' is needed by {string.Join(", ", dependents.Select(d => d.Name))}.");
            Store.Remove(package.Name);
        }
    }
}
