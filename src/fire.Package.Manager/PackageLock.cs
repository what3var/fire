using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace fire.Package.Manager
{
    /// <summary>One package installed straight from a GitHub repository (docs/packages/README.md "Straight from GitHub"): what was asked for, what it resolved to and the checksum
    /// that was installed. The next install of the same repository takes the same tag and commit and must find the same file; `ember update` moves it.</summary>
    public sealed class GitHubLockEntry
    {
        /// <summary>`owner/repo` as it was written.</summary>
        public string Repository { get; set; } = "";
        /// <summary>The ref that was asked for: empty for the newest release, else the tag, branch or commit as written (`v1.2.0`, `main`, `3f2c1a0`).</summary>
        public string Ref { get; set; } = "";
        /// <summary>`release` (a .fpk asset of a release) or `source` (built from the source of the commit).</summary>
        public string Kind { get; set; } = "";
        /// <summary>The tag of the release (null for a branch or a commit).</summary>
        public string? Tag { get; set; }
        /// <summary>The commit the tag, branch or commit resolved to (40 hex digits).</summary>
        public string Commit { get; set; } = "";
        /// <summary>The asset that was installed (release), or null (source).</summary>
        public string? Asset { get; set; }
        /// <summary>The SHA-256 of the package file that was installed.</summary>
        public string Sha256 { get; set; } = "";
        /// <summary>Where the checksum came from: `asset digest` (the digest the API gives), `sha256 asset`, `SHA256SUMS` (joined with " and " when several agree), `first use` (not checked upstream: recorded here and
        /// checked from now on) or `source` (built here from the pinned commit).</summary>
        public string Verified { get; set; } = "";
        /// <summary>The name and version of the package in its package.json.</summary>
        public string Package { get; set; } = "";
        public string Version { get; set; } = "";
        public DateTime Installed { get; set; }

        /// <summary>The spec this entry stands for: `github:owner/repo` (newest release) or `github:owner/repo@ref`.</summary>
        [JsonIgnore] public string Spec => "github:" + Repository + (Ref.Length > 0 ? "@" + Ref : "");
        /// <summary>What was installed, for `ember list`: `github:owner/repo@v1.2.0 (commit 3f2c1a0)`.</summary>
        [JsonIgnore] public string Origin => "github:" + Repository + "@" + (Tag ?? (Ref.Length > 0 ? Ref : Commit)) + (Commit.Length >= 7 ? $" (commit {Commit[..7]})" : "");
    }

    /// <summary>The lock file of the store, `ember-lock.json` in its root (next to the package folders): the packages installed from GitHub with their pinned commit and
    /// checksum. `{ "format": 1, "github": [ { "repository", "ref", "kind", "tag", "commit", "asset", "sha256", "verified", "package", "version", "installed" } ] }`.</summary>
    public sealed class PackageLock
    {
        public const string FileName = "ember-lock.json";
        public int Format { get; set; } = 1;
        public List<GitHubLockEntry> GitHub { get; set; } = new();

        [JsonIgnore] public string Path { get; private set; } = "";

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static string PathFor(PackageStore store) => System.IO.Path.Combine(store.Root, FileName);

        public static PackageLock Load(PackageStore store)
        {
            string path = PathFor(store);
            PackageLock result;
            if (!File.Exists(path)) result = new PackageLock();
            else
            {
                try { result = JsonSerializer.Deserialize<PackageLock>(File.ReadAllText(path), Options) ?? new PackageLock(); }
                catch (JsonException ex) { throw new PackageException($"{path} is not valid: {ex.Message}"); }
                if (result.Format > 1) throw new PackageException($"{path} has format {result.Format}; this version of ember reads format 1 - a newer ember is needed.");
                result.GitHub ??= new();
            }
            result.Path = path;
            return result;
        }

        public void Save()
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            string temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, Path, overwrite: true);
        }

        public GitHubLockEntry? ForRepository(string repository) =>
            GitHub.FirstOrDefault(e => string.Equals(e.Repository, repository, StringComparison.OrdinalIgnoreCase));

        public GitHubLockEntry? ForPackage(string package) =>
            GitHub.FirstOrDefault(e => string.Equals(e.Package, package, StringComparison.OrdinalIgnoreCase));

        /// <summary>Puts the entry in place of the one of the same repository (and of one that had the same package from another repository).</summary>
        public void Set(GitHubLockEntry entry)
        {
            GitHub.RemoveAll(e => string.Equals(e.Repository, entry.Repository, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Package, entry.Package, StringComparison.OrdinalIgnoreCase));
            GitHub.Add(entry);
            GitHub.Sort((a, b) => string.Compare(a.Repository, b.Repository, StringComparison.OrdinalIgnoreCase));
        }

        public bool RemovePackage(string package) => GitHub.RemoveAll(e => string.Equals(e.Package, package, StringComparison.OrdinalIgnoreCase)) > 0;
    }
}
