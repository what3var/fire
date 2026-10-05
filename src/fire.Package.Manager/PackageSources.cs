using System.Security.Cryptography;
using System.Text.Json;

namespace fire.Package.Manager
{
    /// <summary>One version of a package that a source offers.</summary>
    public sealed record PackageVersion(string Version, string Url, string? Sha256 = null);

    /// <summary>What a source says about a package: the metadata of its package.json and where the files of the versions are.</summary>
    public sealed record PackageListing(string Name, string Author, string Description, IReadOnlyList<PackageVersion> Versions, string Source)
    {
        public PackageVersion? Latest => Versions
            .Select(v => (v, ok: SemanticVersion.TryParse(v.Version, out var s), s))
            .Where(x => x.ok).OrderByDescending(x => x.s).Select(x => x.v).FirstOrDefault() ?? Versions.LastOrDefault();
    }

    /// <summary>Where packages come from: a folder (<c>PackageSource</c> in the folder of the compiler) or an index on the web (a GitHub page).</summary>
    public interface IPackageSource
    {
        string Name { get; }
        IReadOnlyList<PackageListing> List();
        /// <summary>Gets the package file of a version into <paramref name="directory"/> and returns its path.</summary>
        string Fetch(PackageVersion version, string directory);
    }

    /// <summary>The index of a source: `{ "packages": [ { "name", "author", "description", "versions": [ { "version", "url", "sha256" } ] } ] }`. A relative `url` is relative to the index.</summary>
    public static class PackageIndex
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

        private sealed class IndexFile { public List<IndexPackage> Packages { get; set; } = new(); }
        private sealed class IndexPackage
        {
            public string Name { get; set; } = "";
            public string Author { get; set; } = "";
            public string Description { get; set; } = "";
            public List<IndexVersion> Versions { get; set; } = new();
        }
        private sealed class IndexVersion { public string Version { get; set; } = ""; public string Url { get; set; } = ""; public string? Sha256 { get; set; } }

        public static IReadOnlyList<PackageListing> Parse(string json, string sourceName, Func<string, string> resolveUrl)
        {
            IndexFile file;
            try { file = JsonSerializer.Deserialize<IndexFile>(json, Options) ?? new IndexFile(); }
            catch (JsonException ex) { throw new PackageException($"The index of '{sourceName}' is not valid: {ex.Message}"); }
            return file.Packages.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Select(p => new PackageListing(
                p.Name, p.Author, p.Description,
                p.Versions.Where(v => !string.IsNullOrWhiteSpace(v.Url)).Select(v => new PackageVersion(v.Version, resolveUrl(v.Url), v.Sha256)).ToList(), sourceName)).ToList();
        }

        /// <summary>The index of the package files in a folder (for publishing: put the files and this index on a web page; <paramref name="baseUrl"/> is where the files will be).</summary>
        public static string Build(string folder, string? baseUrl)
        {
            var packages = new Dictionary<string, IndexPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var fpk in Directory.GetFiles(folder, "*" + Fpk.Extension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var m = Fpk.ReadManifest(fpk);
                if (!packages.TryGetValue(m.Name, out var entry)) packages[m.Name] = entry = new IndexPackage { Name = m.Name, Author = m.Author, Description = m.Description };
                entry.Author = m.Author; entry.Description = m.Description;   // the newest file wins (they are sorted by name)
                string file = Path.GetFileName(fpk);
                using var sha = SHA256.Create();
                string hash = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(fpk))).ToLowerInvariant();
                entry.Versions.Add(new IndexVersion { Version = m.Version, Url = string.IsNullOrEmpty(baseUrl) ? file : baseUrl.TrimEnd('/') + "/" + file, Sha256 = hash });
            }
            return JsonSerializer.Serialize(new IndexFile { Packages = packages.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList() }, Options);
        }
    }

    /// <summary>A folder with .fpk files and/or an `index.json`.</summary>
    public sealed class FolderSource : IPackageSource
    {
        public string Folder { get; }
        public string Name => Folder;
        public FolderSource(string folder) { Folder = Path.GetFullPath(folder); }

        public IReadOnlyList<PackageListing> List()
        {
            var result = new List<PackageListing>();
            if (!Directory.Exists(Folder)) return result;
            var byName = new Dictionary<string, (PackageManifest Manifest, List<PackageVersion> Versions)>(StringComparer.OrdinalIgnoreCase);
            foreach (var fpk in Directory.GetFiles(Folder, "*" + Fpk.Extension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                PackageManifest m;
                try { m = Fpk.ReadManifest(fpk); } catch (PackageException) { continue; }   // a file that is not a usable package is not offered
                if (!byName.TryGetValue(m.Name, out var entry)) byName[m.Name] = entry = (m, new List<PackageVersion>());
                entry.Versions.Add(new PackageVersion(m.Version, fpk));
            }
            foreach (var (_, (m, versions)) in byName) result.Add(new PackageListing(m.Name, m.Author, m.Description, versions, Name));
            string index = Path.Combine(Folder, "index.json");
            if (File.Exists(index))
                foreach (var l in PackageIndex.Parse(File.ReadAllText(index), Name, url => Path.IsPathRooted(url) || url.Contains("://") ? url : Path.GetFullPath(Path.Combine(Folder, url))))
                    if (!result.Any(r => string.Equals(r.Name, l.Name, StringComparison.OrdinalIgnoreCase))) result.Add(l);
            return result;
        }

        public string Fetch(PackageVersion version, string directory) => PackageDownloads.Fetch(version, directory);
    }

    /// <summary>An index on the web (the page of GitHub), or the path or `file:` address of one.</summary>
    public sealed class IndexSource : IPackageSource
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
        public string Location { get; }
        public string Name => Location;
        public IndexSource(string location) { Location = location; }

        private bool IsRemote => Location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || Location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<PackageListing> List()
        {
            string json;
            try
            {
                if (IsRemote) json = Http.GetStringAsync(Location).GetAwaiter().GetResult();
                else
                {
                    string path = Location.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(Location).LocalPath : Location;
                    if (!File.Exists(path)) return Array.Empty<PackageListing>();
                    json = File.ReadAllText(path);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                throw new PackageException($"The package source '{Location}' cannot be read: {ex.Message}");
            }
            return PackageIndex.Parse(json, Name, url => ResolveUrl(url));
        }

        private string ResolveUrl(string url)
        {
            if (url.Contains("://") || Path.IsPathRooted(url)) return url;
            if (IsRemote) return new Uri(new Uri(Location), url).ToString();
            string baseDir = Path.GetDirectoryName(Location.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(Location).LocalPath : Path.GetFullPath(Location))!;
            return Path.GetFullPath(Path.Combine(baseDir, url));
        }

        public string Fetch(PackageVersion version, string directory) => PackageDownloads.Fetch(version, directory);
    }

    internal static class PackageDownloads
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

        public static string Fetch(PackageVersion version, string directory)
        {
            Directory.CreateDirectory(directory);
            string url = version.Url;
            string target;
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                string file = Path.GetFileName(new Uri(url).LocalPath);
                target = Path.Combine(directory, string.IsNullOrEmpty(file) ? "package" + Fpk.Extension : file);
                try { File.WriteAllBytes(target, Http.GetByteArrayAsync(url).GetAwaiter().GetResult()); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { throw new PackageException($"Cannot download '{url}': {ex.Message}"); }
            }
            else
            {
                string source = url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url;
                if (!File.Exists(source)) throw new PackageException($"The package file '{source}' does not exist.");
                target = Path.Combine(directory, Path.GetFileName(source));
                File.Copy(source, target, overwrite: true);
            }
            if (!string.IsNullOrWhiteSpace(version.Sha256))
            {
                using var sha = SHA256.Create();
                string actual = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(target)));
                if (!string.Equals(actual, version.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new PackageException($"The checksum of '{url}' does not match the index - the file is not the one that was published.");
            }
            return target;
        }
    }
}
