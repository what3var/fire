using System.Text.Json;

namespace fire.Package.Manager
{
    /// <summary>The sources and the default places of ember: `ember.json` in the folder of the compiler lists them (`{ "sources": [ "PackageSource", "https://.../index.json" ] }`);
    /// without the file these are the folder `PackageSource` next to the compiler and <see cref="DefaultIndexUrl"/>.</summary>
    public static class EmberSettings
    {
        public const string FileName = "ember.json";
        /// <summary>The index of the published packages on the website (https://what3var.github.io/packages/, built from the registry of the website repository).</summary>
        public const string DefaultIndexUrl = "https://what3var.github.io/packages/index.json";
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

        /// <summary>The settings for packages straight from GitHub: `"github": { "api": "https://api.github.com", "token": "..." }` in ember.json, under the environment
        /// (`FIRE_GITHUB_API`, `FIRE_GITHUB_TOKEN`, `GITHUB_TOKEN`), see <see cref="GitHubSettings.Resolve"/>.</summary>
        public static GitHubSettings LoadGitHub(string? baseDirectory = null)
        {
            string file = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, FileName);
            string? api = null, token = null;
            if (File.Exists(file))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    if (doc.RootElement.TryGetProperty("github", out var gh) && gh.ValueKind == JsonValueKind.Object)
                    {
                        if (gh.TryGetProperty("api", out var a) && a.ValueKind == JsonValueKind.String) api = a.GetString();
                        if (gh.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String) token = t.GetString();
                    }
                }
                catch (JsonException ex) { throw new PackageException($"{file} is not valid: {ex.Message}"); }
            }
            return GitHubSettings.Resolve(api, token);
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

        private readonly GitHubSettings? _gitHubSettings;
        private GitHubSource? _gitHub;
        /// <summary>Packages straight from GitHub (`github:owner/repo[@ref]`): the API and the token (ember.json and the environment; tests give their own).</summary>
        public GitHubSource GitHub => _gitHub ??= new GitHubSource(_gitHubSettings ?? EmberSettings.LoadGitHub());

        public PackageManagerService(PackageStore? store = null, IReadOnlyList<IPackageSource>? sources = null, GitHubSettings? gitHub = null)
        {
            Store = store ?? PackageStore.Default;
            Sources = sources ?? EmberSettings.LoadSources();
            _gitHubSettings = gitHub;
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
                    g.SelectMany(x => x.Versions).GroupBy(v => v.Version).Select(v => v.First()).ToList(), string.Join(", ", g.Select(x => x.Source).Distinct()),
                    g.Select(x => x.License).FirstOrDefault(s => s.Length > 0) ?? "", g.Select(x => x.Homepage).FirstOrDefault(s => s.Length > 0) ?? "")).ToList();
            query = (query ?? "").Trim();
            return merged
                .Where(l => query.Length == 0 || l.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || l.Description.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(l => string.Equals(l.Name, query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Installs `name`, `name@1.2.0`, a package file (`path.fpk`) or a package straight from GitHub (`github:owner/repo[@ref][#name]`) with the packages it depends on.
        /// Returns what was installed. <paramref name="force"/>: install again what is installed already, and let a package from GitHub replace an installed one of the same name
        /// that came from elsewhere.</summary>
        public IReadOnlyList<InstalledPackage> Install(string spec, Action<string>? log = null, bool force = false)
        {
            var installed = new List<InstalledPackage>();
            InstallCore(spec, log, force, installed, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return installed;
        }

        /// <summary>The lock file of the store (the packages from GitHub, their commits and checksums).</summary>
        public PackageLock Lock() => PackageLock.Load(Store);

        private void InstallCore(string spec, Action<string>? log, bool force, List<InstalledPackage> installed, HashSet<string> inProgress, bool update = false)
        {
            string temp = Path.Combine(Path.GetTempPath(), "ember-" + Guid.NewGuid().ToString("N"));
            try
            {
                string fpk;
                GitHubLockEntry? locked = null;
                if (GitHubSpec.IsGitHub(spec))
                {
                    var gh = GitHubSpec.Parse(spec);
                    var lockFile = Lock();
                    var pinned = lockFile.ForRepository(gh.Repository);
                    var fetched = GitHub.Fetch(gh, pinned, update, temp, log);
                    var m = Fpk.ReadManifest(fetched.PackagePath);
                    CheckGitHubPackage(gh, m, lockFile, force);
                    fetched.Entry.Package = m.Name;
                    fetched.Entry.Version = m.Version;
                    if (!force && pinned != null && Store.Find(m.Name) is { } same && same.Version == m.Version && pinned.Commit == fetched.Entry.Commit
                        && (pinned.Kind == "source" || pinned.Sha256 == fetched.Entry.Sha256))
                    {
                        log?.Invoke($"{m.Name} {m.Version} from {fetched.Entry.Origin} is already installed.");
                        return;
                    }
                    log?.Invoke($"{m.Name} {m.Version} comes from {gh.WebUrl} ({fetched.Entry.Origin}).");
                    fpk = fetched.PackagePath;
                    locked = fetched.Entry;
                }
                else if (spec.EndsWith(Fpk.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(spec)) fpk = Path.GetFullPath(spec);
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
                    if (version == null && wanted.Length == 0)
                        throw new PackageException($"Every version of '{name}' was withdrawn (yanked); name one to install it anyway: {string.Join(", ", listing.Versions.Select(v => $"{name}@{v.Version}"))}.");
                    if (version == null) throw new PackageException($"The package '{name}' has no version '{wanted}' (available: {string.Join(", ", listing.Versions.Select(v => v.Version))}).");
                    if (version.Yanked) log?.Invoke($"Note: {listing.Name} {version.Version} was withdrawn (yanked) by its source.");
                    if (!force && Store.Find(listing.Name) is { } already && already.Version == version.Version) { log?.Invoke($"{listing.Name} {version.Version} is already installed."); return; }
                    log?.Invoke($"Fetching {listing.Name} {version.Version}...");
                    fpk = PackageDownloads.Fetch(version, temp);
                }

                var manifest = Fpk.ReadManifest(fpk);
                if (!inProgress.Add(manifest.Name)) return;   // a cycle of dependencies
                foreach (var dependency in manifest.Dependencies)
                    if (Store.FindBySpec(dependency) == null) { log?.Invoke($"{manifest.Name} needs {dependency}."); InstallCore(dependency, log, false, installed, inProgress); }
                var package = Store.Install(fpk);
                installed.Add(package);
                // the lock file: a package from GitHub with its commit and checksum; one of the same name from elsewhere is no longer from GitHub
                var lockNow = Lock();
                if (locked != null) { lockNow.Set(locked); lockNow.Save(); }
                else if (lockNow.RemovePackage(package.Name)) lockNow.Save();
                log?.Invoke($"Installed {package.Name} {package.Version} (imports: {string.Join(", ", package.Manifest.Imports.Select(i => i.Name))}).");
            }
            finally
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
            }
        }

        /// <summary>A package from GitHub must be named like its repository (or as `#name` says), must not take a reserved name (`fire-...`, an import of the compiler as a
        /// "standard" import) and replaces an installed package of the same name from elsewhere only with <paramref name="force"/>.</summary>
        private void CheckGitHubPackage(GitHubSpec spec, PackageManifest m, PackageLock lockFile, bool force)
        {
            if (spec.PackageName != null && !string.Equals(spec.PackageName, m.Name, StringComparison.OrdinalIgnoreCase))
                throw new PackageException($"The package in {spec.Repository} is named '{m.Name}', not '{spec.PackageName}'.");
            if (spec.PackageName == null && !string.Equals(spec.Repo, m.Name, StringComparison.OrdinalIgnoreCase))
                throw new PackageException($"The package in {spec.Repository} is named '{m.Name}', not like the repository. Accept the name with {spec.Text}#{m.Name} (or ember install ... --name {m.Name}).");
            if (m.Name.StartsWith(StandardPackages.Prefix, StringComparison.OrdinalIgnoreCase) || m.Imports.Any(i => i.Standard))
                throw new PackageException($"The package in {spec.Repository} takes the name or an import of a standard library of fire ('{m.Name}'): not installed from GitHub.");
            if (!force && Store.Find(m.Name) is { } other && !string.Equals(lockFile.ForPackage(m.Name)?.Repository, spec.Repository, StringComparison.OrdinalIgnoreCase))
                throw new PackageException($"A package '{m.Name}' is installed already from {(lockFile.ForPackage(m.Name) is { } e ? "github:" + e.Repository : "another source")} ({other.Version}); --force replaces it with the one of {spec.Repository}.");
        }

        /// <summary>One package that <see cref="Update"/> looked at: its name, the version before and after (equal: it was up to date) and where it comes from now.</summary>
        public sealed record UpdateResult(string Name, string OldVersion, string NewVersion, string Origin);

        /// <summary>`ember update`: a package from GitHub installed as the newest release moves to the newest release, one from a branch to the newest commit of the branch; one pinned
        /// to a tag or a commit stays - unless <paramref name="spec"/> names it, then it moves to the newest release too (`github:owner/repo@ref` moves it to that ref). The packages
        /// of the sources move to their newest version. <paramref name="spec"/>: only that package (a name or `github:owner/repo`); null: all (not the standard libraries).</summary>
        public IReadOnlyList<UpdateResult> Update(string? spec = null, Action<string>? log = null)
        {
            var results = new List<UpdateResult>();
            var lockFile = Lock();
            GitHubSpec? named = spec != null && GitHubSpec.IsGitHub(spec) ? GitHubSpec.Parse(spec) : null;
            var entries = lockFile.GitHub.Where(e => spec == null || named != null && string.Equals(e.Repository, named.Repository, StringComparison.OrdinalIgnoreCase)
                || named == null && string.Equals(e.Package, spec, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var entry in entries)
            {
                string old = Store.Find(entry.Package)?.Version ?? "";
                bool explicitly = spec != null;
                bool commit = entry.Ref.Length > 0 && entry.Commit.StartsWith(entry.Ref, StringComparison.OrdinalIgnoreCase);
                bool tag = entry.Ref.Length > 0 && entry.Tag != null;
                string target;
                if (named != null && named.Ref.Length > 0) target = named.Text;
                else if ((commit || tag) && !explicitly) { results.Add(new UpdateResult(entry.Package, old, old, entry.Origin)); continue; }
                else target = "github:" + entry.Repository + (tag || commit || entry.Ref.Length == 0 ? "" : "@" + entry.Ref);
                if (!string.Equals(entry.Package, entry.Repository.Split('/')[1], StringComparison.OrdinalIgnoreCase)) target += "#" + entry.Package;
                InstallCore(target, log, false, new List<InstalledPackage>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), update: true);
                var now = Lock().ForRepository(entry.Repository);
                results.Add(new UpdateResult(entry.Package, old, Store.Find(entry.Package)?.Version ?? old, now?.Origin ?? entry.Origin));
            }
            if (named == null)
            {
                var fromGitHub = new HashSet<string>(lockFile.GitHub.Select(e => e.Package), StringComparer.OrdinalIgnoreCase);
                var candidates = Store.Installed().Where(p => !fromGitHub.Contains(p.Name) && !p.Name.StartsWith(StandardPackages.Prefix, StringComparison.OrdinalIgnoreCase)
                    && (spec == null || string.Equals(p.Name, spec, StringComparison.OrdinalIgnoreCase))).ToList();
                if (candidates.Count > 0)
                {
                    var warnings = new List<string>();
                    var listings = Find("", warnings);
                    foreach (var w in warnings) log?.Invoke("Note: " + w);
                    foreach (var p in candidates)
                    {
                        var latest = listings.FirstOrDefault(l => string.Equals(l.Name, p.Name, StringComparison.OrdinalIgnoreCase))?.Latest;
                        if (latest == null) continue;
                        bool newer = SemanticVersion.TryParse(latest.Version, out var lv) && SemanticVersion.TryParse(p.Version, out var pv) && lv.CompareTo(pv) > 0;
                        if (newer) Install($"{p.Name}@{latest.Version}", log);
                        results.Add(new UpdateResult(p.Name, p.Version, newer ? latest.Version : p.Version, "sources"));
                    }
                }
            }
            if (spec != null && results.Count == 0) throw new PackageException($"'{spec}' is not installed from GitHub or a package source.");
            return results;
        }

        /// <summary>Removes an installed package (and its entry in the lock file); another installed package that depends on it makes it an error (unless <paramref name="force"/>).
        /// <paramref name="name"/> may be `github:owner/repo`: the package installed from that repository.</summary>
        public void Remove(string name, bool force = false)
        {
            var package = Store.FindBySpec(name) ?? throw new PackageException($"The package '{name}' is not installed.");
            var dependents = Store.DependentsOf(package.Name);
            if (dependents.Count > 0 && !force)
                throw new PackageException($"'{package.Name}' is needed by {string.Join(", ", dependents.Select(d => d.Name))}.");
            Store.Remove(package.Name);
            var lockFile = Lock();
            if (lockFile.RemovePackage(package.Name)) lockFile.Save();
        }
    }
}
