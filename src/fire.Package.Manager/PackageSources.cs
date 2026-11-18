using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace fire.Package.Manager
{
    /// <summary>One version of a package that a source offers. <paramref name="Yanked"/>: withdrawn (a broken release) - never chosen as the newest, but still installable by its
    /// exact version (`name@1.2.0`). <paramref name="Size"/>: the size of the file in bytes, when the index gives it (checked after the download).</summary>
    public sealed record PackageVersion(string Version, string Url, string? Sha256 = null, bool Yanked = false, long? Size = null);

    /// <summary>What a source says about a package: the metadata of its package.json and where the files of the versions are.</summary>
    public sealed record PackageListing(string Name, string Author, string Description, IReadOnlyList<PackageVersion> Versions, string Source, string License = "", string Homepage = "")
    {
        /// <summary>The newest version that is not yanked (null: there is none).</summary>
        public PackageVersion? Latest
        {
            get
            {
                var offered = Versions.Where(v => !v.Yanked).ToList();
                return offered.Select(v => (v, ok: SemanticVersion.TryParse(v.Version, out var s), s))
                    .Where(x => x.ok).OrderByDescending(x => x.s).Select(x => x.v).FirstOrDefault() ?? offered.LastOrDefault();
            }
        }
    }

    /// <summary>Where packages come from: a folder (<c>PackageSource</c> in the folder of the compiler) or an index on the web (the package page of the website).</summary>
    public interface IPackageSource
    {
        string Name { get; }
        IReadOnlyList<PackageListing> List();
        /// <summary>Gets the package file of a version into <paramref name="directory"/> and returns its path.</summary>
        string Fetch(PackageVersion version, string directory);
    }

    /// <summary>The index of a source: `{ "format": 1, "packages": [ { "name", "author", "description", "license", "homepage", "versions": [ { "version", "url", "sha256", "size", "yanked" } ] } ] }`.
    /// A relative `url` is relative to the index. Other fields (the package page of the website writes more: dependencies, imports, dates) are ignored.</summary>
    public static class PackageIndex
    {
        /// <summary>The newest format of the index that this version of ember reads.</summary>
        public const int Format = 1;

        private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        private static readonly JsonSerializerOptions WriteOptions = new(Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        private sealed class IndexFile { public int Format { get; set; } = 1; public List<IndexPackage> Packages { get; set; } = new(); }
        private sealed class IndexPackage
        {
            public string Name { get; set; } = "";
            public string Author { get; set; } = "";
            public string Description { get; set; } = "";
            public string? License { get; set; }
            public string? Homepage { get; set; }
            public List<IndexVersion> Versions { get; set; } = new();
        }
        private sealed class IndexVersion
        {
            public string Version { get; set; } = "";
            public string Url { get; set; } = "";
            public string? Sha256 { get; set; }
            public long? Size { get; set; }
            public bool? Yanked { get; set; }
        }

        public static IReadOnlyList<PackageListing> Parse(string json, string sourceName, Func<string, string> resolveUrl)
        {
            IndexFile file;
            try { file = JsonSerializer.Deserialize<IndexFile>(json, Options) ?? new IndexFile(); }
            catch (JsonException ex) { throw new PackageException($"The index of '{sourceName}' is not valid: {ex.Message}"); }
            if (file.Format > Format) throw new PackageException($"The index of '{sourceName}' has format {file.Format}; this version of ember reads format {Format} - a newer ember is needed.");
            return (file.Packages ?? new()).Where(p => !string.IsNullOrWhiteSpace(p.Name)).Select(p => new PackageListing(
                p.Name, p.Author ?? "", p.Description ?? "",
                (p.Versions ?? new()).Where(v => !string.IsNullOrWhiteSpace(v.Url)).Select(v => new PackageVersion(v.Version, resolveUrl(v.Url), v.Sha256, v.Yanked == true, v.Size)).ToList(),
                sourceName, p.License ?? "", p.Homepage ?? "")).ToList();
        }

        /// <summary>The index of the package files in a folder (for publishing: put the files and this index on a web page; <paramref name="baseUrl"/> is where the files will be).</summary>
        public static string Build(string folder, string? baseUrl)
        {
            var packages = new Dictionary<string, IndexPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var fpk in Directory.GetFiles(folder, "*" + Fpk.Extension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var m = Fpk.ReadManifest(fpk);
                if (!packages.TryGetValue(m.Name, out var entry)) packages[m.Name] = entry = new IndexPackage { Name = m.Name };
                // the newest file wins (they are sorted by name)
                entry.Author = m.Author; entry.Description = m.Description;
                entry.License = string.IsNullOrWhiteSpace(m.License) ? null : m.License;
                entry.Homepage = string.IsNullOrWhiteSpace(m.Homepage) ? null : m.Homepage;
                string file = Path.GetFileName(fpk);
                entry.Versions.Add(new IndexVersion
                {
                    Version = m.Version, Url = string.IsNullOrEmpty(baseUrl) ? file : baseUrl.TrimEnd('/') + "/" + file,
                    Sha256 = PackageDownloads.Sha256Of(fpk), Size = new FileInfo(fpk).Length,
                });
            }
            return JsonSerializer.Serialize(new IndexFile { Packages = packages.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList() }, WriteOptions);
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
            foreach (var (_, (m, versions)) in byName) result.Add(new PackageListing(m.Name, m.Author, m.Description, versions, Name, m.License ?? "", m.Homepage ?? ""));
            string index = Path.Combine(Folder, "index.json");
            if (File.Exists(index))
                foreach (var l in PackageIndex.Parse(File.ReadAllText(index), Name, url => Path.IsPathRooted(url) || url.Contains("://") ? url : Path.GetFullPath(Path.Combine(Folder, url))))
                    if (!result.Any(r => string.Equals(r.Name, l.Name, StringComparison.OrdinalIgnoreCase))) result.Add(l);
            return result;
        }

        public string Fetch(PackageVersion version, string directory) => PackageDownloads.Fetch(version, directory);
    }

    /// <summary>An index on the web (https; the package page of the website), or the path or `file:` address of one.</summary>
    public sealed class IndexSource : IPackageSource
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
        public string Location { get; }
        public string Name => Location;
        public IndexSource(string location) { Location = location; }

        private bool IsRemote => PackageDownloads.IsWeb(Location);

        public IReadOnlyList<PackageListing> List()
        {
            string json;
            try
            {
                if (IsRemote)
                {
                    PackageDownloads.CheckSecure(Location);
                    json = Http.GetStringAsync(Location).GetAwaiter().GetResult();
                }
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

    /// <summary>Getting a package file - from the web (https; plain http only from this machine), a `file:` address or a path - and checking it against the index.</summary>
    public static class PackageDownloads
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

        public static bool IsWeb(string location) =>
            location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        /// <summary>An address of the web must be https: a package is code that runs on the machine. Plain http is allowed for this machine only (`localhost`, 127.0.0.1, ::1:
        /// a test server, a mirror on the own computer).</summary>
        public static void CheckSecure(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new PackageException($"'{url}' is not an address.");
            if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
                throw new PackageException($"'{url}' is plain http: ember reads packages and indexes from the web only over https (http only from this machine, localhost).");
        }

        /// <summary>The SHA-256 of a file, lower-case hex (as the index writes it).</summary>
        public static string Sha256Of(string file)
        {
            using var stream = File.OpenRead(file);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        public static string Fetch(PackageVersion version, string directory)
        {
            Directory.CreateDirectory(directory);
            string url = version.Url;
            string target;
            if (IsWeb(url))
            {
                CheckSecure(url);
                // a file from the web is only installed when the index says which file it is: the checksum finds a changed or broken file
                if (string.IsNullOrWhiteSpace(version.Sha256))
                    throw new PackageException($"The index gives no sha256 for '{url}' - ember installs a package from the web only when it can check the file.");
                string file = Path.GetFileName(new Uri(url).LocalPath);
                target = Path.Combine(directory, string.IsNullOrEmpty(file) ? "package" + Fpk.Extension : file);
                try
                {
                    using var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                    response.EnsureSuccessStatusCode();
                    using var body = response.Content.ReadAsStream();
                    using var output = File.Create(target);
                    body.CopyTo(output);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { throw new PackageException($"Cannot download '{url}': {ex.Message}"); }
            }
            else
            {
                string source = url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url;
                if (!File.Exists(source)) throw new PackageException($"The package file '{source}' does not exist.");
                target = Path.Combine(directory, Path.GetFileName(source));
                File.Copy(source, target, overwrite: true);
            }
            long actualSize = new FileInfo(target).Length;
            if (version.Size is long size && actualSize != size)
                throw new PackageException($"'{url}' has {actualSize} bytes, the index says {size} - the file is not the one that was published.");
            if (!string.IsNullOrWhiteSpace(version.Sha256) && !string.Equals(Sha256Of(target), version.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new PackageException($"The checksum of '{url}' does not match the index - the file is not the one that was published.");
            return target;
        }
    }
}
