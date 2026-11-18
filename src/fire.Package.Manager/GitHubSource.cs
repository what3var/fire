using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace fire.Package.Manager
{
    /// <summary>A package straight from a GitHub repository (docs/packages/README.md "Straight from GitHub"): `github:owner/repo` (the newest release), `github:owner/repo@v1.2.0`
    /// (a tag or release; `@1.2.0` finds `v1.2.0` too), `github:owner/repo@main` (a branch) or `github:owner/repo@3f2c1a0` (a commit). `#name` at the end accepts a package
    /// whose name is not the name of the repository (`github:jane/fire-sensors#sensorkit`).</summary>
    public sealed record GitHubSpec(string Owner, string Repo, string Ref, string? PackageName)
    {
        public const string Prefix = "github:";
        private static readonly Regex OwnerPattern = new(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$", RegexOptions.Compiled);
        private static readonly Regex RepoPattern = new(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled);
        private static readonly Regex RefPattern = new(@"^[A-Za-z0-9._+-][A-Za-z0-9._/+-]{0,199}$", RegexOptions.Compiled);
        private static readonly Regex CommitPattern = new(@"^[0-9a-fA-F]{7,40}$", RegexOptions.Compiled);

        public string Repository => Owner + "/" + Repo;
        /// <summary>The spec as ember writes it (without the name, which is not part of what is resolved).</summary>
        public string Text => Prefix + Repository + (Ref.Length > 0 ? "@" + Ref : "");
        public string WebUrl => $"https://github.com/{Repository}";
        /// <summary>The ref looks like a commit (7 to 40 hex digits).</summary>
        public bool RefIsCommit => CommitPattern.IsMatch(Ref);

        public static bool IsGitHub(string? spec) => spec != null && spec.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

        public static bool TryParse(string? spec, out GitHubSpec result)
        {
            result = null!;
            try { result = Parse(spec ?? ""); return true; }
            catch (PackageException) { return false; }
        }

        public static GitHubSpec Parse(string spec)
        {
            if (!IsGitHub(spec)) throw new PackageException($"'{spec}' is not a GitHub package (github:owner/repo[@ref][#name]).");
            string rest = spec.Substring(Prefix.Length).Trim();
            string? name = null;
            int hash = rest.IndexOf('#');
            if (hash >= 0) { name = rest.Substring(hash + 1); rest = rest.Substring(0, hash); if (!PackageManifest.NamePattern.IsMatch(name)) throw new PackageException($"'{name}' in '{spec}' is not a package name."); }
            string reference = "";
            int at = rest.IndexOf('@');
            if (at >= 0) { reference = rest.Substring(at + 1); rest = rest.Substring(0, at); }
            var parts = rest.Split('/');
            if (parts.Length != 2 || !OwnerPattern.IsMatch(parts[0]) || !RepoPattern.IsMatch(parts[1]) || parts[1] is "." or "..")
                throw new PackageException($"'{spec}' does not name a repository: github:owner/repo[@ref][#name].");
            string repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
            if (at >= 0 && (!RefPattern.IsMatch(reference) || reference.Contains("..") || reference.EndsWith('/') || reference.Contains("//")))
                throw new PackageException($"'{reference}' in '{spec}' is not a tag, branch or commit.");
            return new GitHubSpec(parts[0], repo, reference, name);
        }
    }

    /// <summary>Where the API of GitHub is and the token for it: the environment (`FIRE_GITHUB_API`, `FIRE_GITHUB_TOKEN`, `GITHUB_TOKEN`) before `"github": { "api", "token" }`
    /// in ember.json. The token goes only to the host (and port) of the API - never to a download that a redirect sends elsewhere.</summary>
    public sealed class GitHubSettings
    {
        public const string DefaultApi = "https://api.github.com";
        public string ApiBase { get; init; } = DefaultApi;
        public string? Token { get; init; }
        /// <summary>Where the token came from (for `ember sources`): an environment variable, ember.json, or null.</summary>
        public string? TokenSource { get; init; }

        public static GitHubSettings Resolve(string? configApi = null, string? configToken = null)
        {
            string api = Environment.GetEnvironmentVariable("FIRE_GITHUB_API") is { Length: > 0 } envApi ? envApi : configApi is { Length: > 0 } ? configApi : DefaultApi;
            foreach (var name in new[] { "FIRE_GITHUB_TOKEN", "GITHUB_TOKEN" })
                if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } t) return new GitHubSettings { ApiBase = api.TrimEnd('/'), Token = t.Trim(), TokenSource = name };
            return new GitHubSettings { ApiBase = api.TrimEnd('/'), Token = string.IsNullOrWhiteSpace(configToken) ? null : configToken.Trim(), TokenSource = string.IsNullOrWhiteSpace(configToken) ? null : EmberSettings.FileName };
        }
    }

    /// <summary>What a GitHub spec resolved to and was installed from; becomes the entry of the lock file.</summary>
    public sealed record GitHubFetchResult(string PackagePath, GitHubLockEntry Entry);

    /// <summary>Resolves a <see cref="GitHubSpec"/> with the REST API of GitHub and gets the package: the `.fpk` asset of the release (checked against the asset digest, a `.sha256`
    /// asset or `SHA256SUMS`; else trusted on first use and pinned in the lock file), or - a release without one, a branch, a commit - the package built from the source at the commit.</summary>
    public sealed class GitHubSource
    {
        /// <summary>The largest source snapshot (tarball) ember downloads, and the most it unpacks from one.</summary>
        public const long MaxTarballBytes = 100L * 1024 * 1024;
        public const long MaxUnpackedBytes = 400L * 1024 * 1024;
        public const int MaxEntries = 20000;

        private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = TimeSpan.FromMinutes(2) };

        /// <summary>Builds the package of a library project (`.fireproj`) found in a source snapshot: (project file, output folder, log) -> the path of the `.fpk`. ember has no compiler:
        /// the default runs `forge build project -o folder` next to the program; forge and the tests put the compiler's `ProjectBuilder.PackLibrary` here.</summary>
        public static Func<string, string, Action<string>?, string> ProjectPacker { get; set; } = PackWithForge;

        public GitHubSettings Settings { get; }
        private readonly Uri _api;

        public GitHubSource(GitHubSettings settings)
        {
            Settings = settings;
            PackageDownloads.CheckSecure(settings.ApiBase);
            _api = new Uri(settings.ApiBase.TrimEnd('/') + "/");
        }

        // ---- HTTP: the token only for the API's origin, redirects followed by hand (each hop https or this machine), the rate limit as a clear message ----

        private bool IsApiOrigin(Uri uri) => uri.Scheme == _api.Scheme && string.Equals(uri.Host, _api.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == _api.Port;

        private HttpResponseMessage Send(Uri uri, string accept)
        {
            for (int hop = 0; ; hop++)
            {
                PackageDownloads.CheckSecure(uri.ToString());
                var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ember", "1"));
                request.Headers.Accept.ParseAdd(accept);
                if (IsApiOrigin(uri))
                {
                    request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                    if (Settings.Token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.Token);
                }
                HttpResponseMessage response;
                try { response = Http.Send(request, HttpCompletionOption.ResponseHeadersRead); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { throw new PackageException($"GitHub cannot be reached ({uri.Host}): {ex.Message}"); }
                int status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    response.Dispose();
                    if (hop >= 5) throw new PackageException($"Too many redirects from '{uri}'.");
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }
                return response;
            }
        }

        /// <summary>A failed answer as a message: the rate limit (403/429 with no requests left, or `Retry-After`) with the time it resets, a token that is not accepted, other errors.</summary>
        private Exception Failure(HttpResponseMessage response, string what)
        {
            int status = (int)response.StatusCode;
            string? Header(string name) => response.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;
            bool exhausted = Header("x-ratelimit-remaining") == "0";
            string? retryAfter = Header("retry-after");
            if (status is 403 or 429 && (exhausted || retryAfter != null))
            {
                string when = "";
                if (long.TryParse(Header("x-ratelimit-reset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long reset))
                {
                    var at = DateTimeOffset.FromUnixTimeSeconds(reset);
                    int minutes = Math.Max(0, (int)Math.Ceiling((at - DateTimeOffset.UtcNow).TotalMinutes));
                    when = $" It resets at {at.UtcDateTime:HH:mm} UTC (in {minutes} minute{(minutes == 1 ? "" : "s")}).";
                }
                else if (int.TryParse(retryAfter, out int seconds)) when = $" Try again in {seconds} seconds.";
                string auth = Settings.Token != null ? " (with the token)" : " - without a token GitHub allows 60 requests an hour; set GITHUB_TOKEN or FIRE_GITHUB_TOKEN for more";
                return new PackageException($"GitHub's rate limit is used up{auth}.{when}");
            }
            if (status == 401) return new PackageException($"GitHub does not accept the token ({Settings.TokenSource}) for {what}: check or remove it.");
            if (status == 404) return new PackageException($"{what} was not found on GitHub" + (Settings.Token == null ? " (a private repository needs a token: GITHUB_TOKEN or FIRE_GITHUB_TOKEN)." : "."));
            return new PackageException($"GitHub answered {status} {response.ReasonPhrase} for {what}.");
        }

        /// <summary>A JSON answer of the API (`repos/...`); null for 404.</summary>
        private JsonElement? GetJson(string path, string what)
        {
            using var response = Send(new Uri(_api, path), "application/vnd.github+json");
            if (response.StatusCode == HttpStatusCode.NotFound && !(response.Headers.TryGetValues("x-ratelimit-remaining", out var r) && r.FirstOrDefault() == "0")) return null;
            if (!response.IsSuccessStatusCode) throw Failure(response, what);
            if (response.Content.Headers.ContentLength is > 8 * 1024 * 1024) throw new PackageException($"The answer of GitHub for {what} is too big.");
            try
            {
                using var stream = response.Content.ReadAsStream();
                using var doc = JsonDocument.Parse(stream);
                return doc.RootElement.Clone();
            }
            catch (JsonException ex) { throw new PackageException($"The answer of GitHub for {what} is not valid JSON: {ex.Message}"); }
        }

        private void Download(string url, string target, long limit, string accept, string what)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new PackageException($"'{url}' is not an address.");
            using var response = Send(uri, accept);
            if (!response.IsSuccessStatusCode) throw Failure(response, what);
            try { PackageDownloads.SaveLimited(response, target, limit, url); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { throw new PackageException($"Cannot download '{url}': {ex.Message}"); }
        }

        private static string Segment(string text) => Uri.EscapeDataString(text).Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

        // ---- resolving ----

        private JsonElement? Release(GitHubSpec spec, string reference)
        {
            string repo = $"repos/{spec.Owner}/{spec.Repo}";
            if (reference.Length == 0) return GetJson($"{repo}/releases/latest", $"the newest release of {spec.Repository}");
            if (GetJson($"{repo}/releases/tags/{Segment(reference)}", $"the release {reference} of {spec.Repository}") is { } found) return found;
            // semver tags are found with or without the `v`
            string other = reference.StartsWith('v') || reference.StartsWith('V') ? reference.Substring(1) : "v" + reference;
            if (SemanticVersion.TryParse(other.TrimStart('v', 'V'), out _) && char.IsDigit(other.TrimStart('v', 'V').FirstOrDefault()))
                return GetJson($"{repo}/releases/tags/{Segment(other)}", $"the release {other} of {spec.Repository}");
            return null;
        }

        private string? CommitOf(GitHubSpec spec, string reference)
        {
            var commit = GetJson($"repos/{spec.Owner}/{spec.Repo}/commits/{Segment(reference)}", $"the commit of '{reference}' in {spec.Repository}");
            if (commit is not { } c) return null;
            string sha = c.TryGetProperty("sha", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
            if (!Regex.IsMatch(sha, "^[0-9a-f]{40}$")) throw new PackageException($"GitHub gave no commit for '{reference}' in {spec.Repository}.");
            return sha;
        }

        private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// <summary>Gets the package of <paramref name="spec"/> into <paramref name="directory"/>. <paramref name="pinned"/>: the entry of the lock file for the repository - without
        /// <paramref name="update"/> the same tag/commit is taken again and the file must have the recorded checksum; with it the spec is resolved anew (the newest release) and only a
        /// changed file under the same tag or a moved tag is an error.</summary>
        public GitHubFetchResult Fetch(GitHubSpec spec, GitHubLockEntry? pinned, bool update, string directory, Action<string>? log)
        {
            Directory.CreateDirectory(directory);
            if (pinned != null && !string.Equals(pinned.Repository, spec.Repository, StringComparison.OrdinalIgnoreCase)) pinned = null;
            // the lock file pins what an install takes again: the newest release stays the one that was installed, a branch stays at its commit
            string reference = spec.Ref;
            bool pinnedSource = false;
            if (pinned != null && !update && (spec.Ref.Length == 0 || string.Equals(spec.Ref, pinned.Ref, StringComparison.Ordinal)))
            {
                if (pinned.Kind == "source" && pinned.Tag == null) { reference = pinned.Commit; pinnedSource = true; }
                else if (pinned.Tag != null) reference = pinned.Tag;
                if (spec.Ref.Length == 0 || pinnedSource) log?.Invoke($"{spec.Text} is pinned by {PackageLock.FileName} to {(pinned.Tag ?? pinned.Commit[..7])} (`ember update` moves it).");
            }
            else if (pinned != null && string.Equals(pinned.Ref, spec.Ref, StringComparison.Ordinal) && spec.RefIsCommit) reference = pinned.Commit;   // (a commit never moves)

            JsonElement? release = pinnedSource || (spec.RefIsCommit && reference == spec.Ref) ? null : Release(spec, reference);
            if (release == null && reference.Length == 0)
                throw new PackageException($"{spec.Repository} has no release (or it was not found). Name a tag, branch or commit to build the package from the source: {spec.Text}@main.");

            GitHubLockEntry entry;
            string fpk;
            if (release is { } rel)
            {
                string tag = Str(rel, "tag_name") ?? throw new PackageException($"The release of {spec.Repository} has no tag.");
                string commit = CommitOf(spec, tag) ?? throw new PackageException($"The tag {tag} of {spec.Repository} has no commit.");
                CheckPinnedCommit(spec, pinned, tag, commit);
                var assets = rel.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : new List<JsonElement>();
                var asset = ChooseAsset(spec, tag, assets);
                if (asset is { } chosen)
                {
                    string name = Str(chosen, "name")!;
                    log?.Invoke($"Fetching {name} of the release {tag} of {spec.WebUrl} (commit {commit[..7]})...");
                    var (sha, verified) = UpstreamChecksum(spec, name, chosen, assets, directory);
                    string target = Path.Combine(directory, Path.GetFileName(name));
                    long? size = chosen.TryGetProperty("size", out var sz) && sz.TryGetInt64(out long n) ? n : null;
                    if (size > PackageDownloads.MaxPackageBytes) throw new PackageException($"{name} has {size} bytes - more than ember takes ({PackageDownloads.MaxPackageBytes} bytes).");
                    // with a token the asset is fetched through the API (works for private repositories; the redirect to the storage host gets no token), else from the download address
                    string? apiUrl = Str(chosen, "url"), browserUrl = Str(chosen, "browser_download_url");
                    if (Settings.Token != null && apiUrl != null) Download(apiUrl, target, size ?? PackageDownloads.MaxPackageBytes, "application/octet-stream", name);
                    else Download(browserUrl ?? apiUrl ?? throw new PackageException($"The asset {name} has no address."), target, size ?? PackageDownloads.MaxPackageBytes, "application/octet-stream", name);
                    long actual = new FileInfo(target).Length;
                    if (size is long expected && actual != expected) throw new PackageException($"{name} has {actual} bytes, GitHub says {expected} - the file is not the one that was published.");
                    string have = PackageDownloads.Sha256Of(target);
                    if (sha != null && !string.Equals(have, sha, StringComparison.OrdinalIgnoreCase))
                        throw new PackageException($"The checksum of {name} does not match its {verified} - the file is not the one that was published (expected {sha}, got {have}).");
                    if (pinned != null && pinned.Kind == "release" && pinned.Tag == tag && !string.Equals(pinned.Sha256, have, StringComparison.OrdinalIgnoreCase))
                        throw new PackageException($"{name} of {spec.Repository} {tag} is not the file that was installed before (its SHA-256 is {have}, {PackageLock.FileName} has {pinned.Sha256}): the asset was replaced. " +
                            "Remove the package (`ember remove`) and install it again only if you trust the new file.");
                    if (sha != null) log?.Invoke($"Verified: the SHA-256 matches the {verified} of the release.");
                    else if (pinned != null && pinned.Tag == tag) { verified = pinned.Verified; log?.Invoke($"Verified: the SHA-256 matches the one recorded in {PackageLock.FileName} on first use (not verified upstream)."); }
                    else
                    {
                        verified = "first use";
                        log?.Invoke($"Warning: the release gives no checksum for {name} (no asset digest, no {name}.sha256, no SHA256SUMS) - it was NOT verified upstream. " +
                            $"Its SHA-256 {have} is recorded in {PackageLock.FileName} and checked from now on.");
                    }
                    entry = new GitHubLockEntry { Kind = "release", Tag = tag, Commit = commit, Asset = name, Sha256 = have, Verified = verified! };
                    fpk = target;
                }
                else
                {
                    log?.Invoke($"The release {tag} of {spec.WebUrl} has no .fpk asset: building the package from the source at commit {commit[..7]}...");
                    fpk = BuildFromSource(spec, commit, directory, log);
                    entry = new GitHubLockEntry { Kind = "source", Tag = tag, Commit = commit, Sha256 = PackageDownloads.Sha256Of(fpk), Verified = "source" };
                }
            }
            else
            {
                string commit = CommitOf(spec, reference) ?? throw new PackageException($"{spec.Repository} has no release, tag, branch or commit '{spec.Ref}'.");
                if (pinned != null && spec.RefIsCommit && !pinned.Commit.StartsWith(spec.Ref, StringComparison.OrdinalIgnoreCase)) pinned = null;
                log?.Invoke($"Building the package from the source of {spec.WebUrl} at {(spec.RefIsCommit ? "commit" : "'" + spec.Ref + "', commit")} {commit[..7]}...");
                fpk = BuildFromSource(spec, commit, directory, log);
                entry = new GitHubLockEntry { Kind = "source", Commit = commit, Sha256 = PackageDownloads.Sha256Of(fpk), Verified = "source" };
            }
            entry.Repository = spec.Repository;
            entry.Ref = spec.Ref.Length == 0 && pinned != null && !update ? pinned.Ref : spec.Ref;
            entry.Installed = DateTime.UtcNow;
            return new GitHubFetchResult(fpk, entry);
        }

        private static void CheckPinnedCommit(GitHubSpec spec, GitHubLockEntry? pinned, string tag, string commit)
        {
            if (pinned?.Tag == tag && !string.Equals(pinned.Commit, commit, StringComparison.OrdinalIgnoreCase))
                throw new PackageException($"The tag {tag} of {spec.Repository} moved: it is commit {commit[..7]} now, {PackageLock.FileName} pinned {pinned.Commit[..Math.Min(7, pinned.Commit.Length)]}. " +
                    "Remove the package (`ember remove`) and install it again only if you trust the new commit.");
        }

        /// <summary>The `.fpk` asset of a release: the only one, or - with several - the one named like the package or the repository (`name.fpk`, `name-1.2.0.fpk`). None: null.</summary>
        private static JsonElement? ChooseAsset(GitHubSpec spec, string tag, List<JsonElement> assets)
        {
            var fpks = assets.Where(a => Str(a, "name") is { } n && n.EndsWith(Fpk.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
            if (fpks.Count <= 1) return fpks.Count == 1 ? fpks[0] : null;
            string wanted = spec.PackageName ?? spec.Repo;
            bool Named(JsonElement a)
            {
                string stem = Path.GetFileNameWithoutExtension(Str(a, "name")!);
                return string.Equals(stem, wanted, StringComparison.OrdinalIgnoreCase)
                    || stem.StartsWith(wanted + "-", StringComparison.OrdinalIgnoreCase) && SemanticVersion.TryParse(stem.Substring(wanted.Length + 1).TrimStart('v'), out _);
            }
            var named = fpks.Where(Named).ToList();
            if (named.Count == 1) return named[0];
            throw new PackageException($"The release {tag} of {spec.Repository} has several .fpk files ({string.Join(", ", fpks.Select(a => Str(a, "name")))}) and none is named after '{wanted}': " +
                $"name the package with {spec.Text}#name.");
        }

        /// <summary>The checksum the release gives for the asset: its `digest` (sha256:..., the API has it for newer assets), an asset `name.sha256`, or the line of `SHA256SUMS`.
        /// Sources that disagree are an error; none: (null, null).</summary>
        private (string? Sha, string? Verified) UpstreamChecksum(GitHubSpec spec, string name, JsonElement asset, List<JsonElement> assets, string directory)
        {
            var found = new List<(string Sha, string From)>();
            if (Str(asset, "digest") is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                found.Add((digest.Substring(7).Trim().ToLowerInvariant(), "asset digest"));
            string? Text(JsonElement a, long limit)
            {
                string target = Path.Combine(directory, "checksum-" + Guid.NewGuid().ToString("N"));
                string? apiUrl = Str(a, "url"), browserUrl = Str(a, "browser_download_url");
                string url = Settings.Token != null && apiUrl != null ? apiUrl : browserUrl ?? apiUrl ?? "";
                Download(url, target, limit, "application/octet-stream", Str(a, "name")!);
                try { return File.ReadAllText(target); } finally { File.Delete(target); }
            }
            var shaAsset = assets.FirstOrDefault(a => string.Equals(Str(a, "name"), name + ".sha256", StringComparison.OrdinalIgnoreCase));
            if (shaAsset.ValueKind == JsonValueKind.Object && Text(shaAsset, 4096) is { } shaText)
            {
                var m = Regex.Match(shaText, @"\b[0-9a-fA-F]{64}\b");
                if (!m.Success) throw new PackageException($"{name}.sha256 of {spec.Repository} holds no SHA-256.");
                found.Add((m.Value.ToLowerInvariant(), "sha256 asset"));
            }
            var sums = assets.FirstOrDefault(a => Str(a, "name") is { } n && n.ToUpperInvariant() is "SHA256SUMS" or "SHA256SUMS.TXT");
            if (sums.ValueKind == JsonValueKind.Object && Text(sums, 1024 * 1024) is { } sumsText)
                foreach (var line in sumsText.Split('\n'))
                {
                    var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(.+)$");
                    if (m.Success && string.Equals(Path.GetFileName(m.Groups[2].Value.Trim()), name, StringComparison.Ordinal)) { found.Add((m.Groups[1].Value.ToLowerInvariant(), "SHA256SUMS")); break; }
                }
            if (found.Count == 0) return (null, null);
            if (found.Select(f => f.Sha).Distinct().Count() > 1)
                throw new PackageException($"The checksums of {name} in the release of {spec.Repository} disagree ({string.Join(", ", found.Select(f => f.From + " " + f.Sha))}) - nothing is installed.");
            return (found[0].Sha, string.Join(" and ", found.Select(f => f.From)));
        }

        // ---- the source: the tarball of the commit, unpacked with care, packed like `ember forge` (package.json) or Pack Library (.fireproj) ----

        private string BuildFromSource(GitHubSpec spec, string commit, string directory, Action<string>? log)
        {
            string tarball = Path.Combine(directory, "source.tar.gz");
            Download(new Uri(_api, $"repos/{spec.Owner}/{spec.Repo}/tarball/{commit}").ToString(), tarball, MaxTarballBytes, "application/vnd.github+json", $"the source of {spec.Repository} at {commit[..7]}");
            string root = Path.Combine(directory, "source");
            Unpack(tarball, root);
            File.Delete(tarball);
            string output = Path.Combine(directory, "built");
            return BuildPackage(root, output, log);
        }

        /// <summary>Unpacks a `.tar.gz` of GitHub (one folder `owner-repo-sha/` at the top, which is left out) into <paramref name="root"/>: regular files and folders only
        /// (no links), no path out of the folder, at most <see cref="MaxEntries"/> entries and <see cref="MaxUnpackedBytes"/> bytes.</summary>
        public static void Unpack(string tarGz, string root)
        {
            Directory.CreateDirectory(root);
            string full = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
            long total = 0;
            int count = 0;
            try
            {
                using var file = File.OpenRead(tarGz);
                using var gzip = new GZipStream(file, CompressionMode.Decompress);
                using var reader = new TarReader(gzip);
                while (reader.GetNextEntry() is { } entry)
                {
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory)) continue;   // links, global headers: not taken
                    if (++count > MaxEntries) throw new PackageException($"The source has more than {MaxEntries} files.");
                    string name = entry.Name.Replace('\\', '/');
                    int slash = name.IndexOf('/');
                    if (slash < 0) continue;
                    name = name.Substring(slash + 1);
                    if (name.Length == 0) continue;
                    string target = Path.GetFullPath(Path.Combine(root, name));
                    if (!target.StartsWith(full, StringComparison.Ordinal)) throw new PackageException($"The source contains a path that leaves its folder: {entry.Name}");
                    if (entry.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
                    total += entry.Length;
                    if (total > MaxUnpackedBytes) throw new PackageException($"The source is bigger than {MaxUnpackedBytes} bytes unpacked.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var output = File.Create(target);
                    entry.DataStream?.CopyTo(output);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException) { throw new PackageException($"The source snapshot is not a valid .tar.gz: {ex.Message}"); }
        }

        /// <summary>Builds the package of an unpacked source: a `package.json` (a forge description, as for `ember forge`) in the root, else the one library project (`.fireproj`)
        /// of the root (or the only one of the tree) through <see cref="ProjectPacker"/>. Every path the description names must stay below <paramref name="root"/>.</summary>
        public static string BuildPackage(string root, string output, Action<string>? log)
        {
            root = Path.GetFullPath(root);
            string description = Path.Combine(root, Fpk.ManifestEntry);
            if (File.Exists(description))
            {
                var manifest = PackageManifest.Load(description);
                CheckInside(root, root, ManifestPaths(manifest));
                log?.Invoke($"Packing {Fpk.ManifestEntry} of the source ({manifest.Name} {manifest.Version})...");
                return Fpk.Forge(description, output).PackagePath;
            }
            var projects = Directory.GetFiles(root, "*.fireproj");
            if (projects.Length == 0) projects = Directory.EnumerateFiles(root, "*.fireproj", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 }).ToArray();
            if (projects.Length == 0) throw new PackageException($"The source has no {Fpk.ManifestEntry} (a package description) and no library project (.fireproj) to build a package from.");
            var libraries = projects.Where(IsLibraryProject).ToList();
            if (libraries.Count != 1)
                throw new PackageException(libraries.Count == 0 ? $"The project of the source ({string.Join(", ", projects.Select(p => Path.GetRelativePath(root, p)))}) is not a library: only a library becomes a package."
                    : $"The source has several library projects ({string.Join(", ", libraries.Select(p => Path.GetRelativePath(root, p)))}); put a {Fpk.ManifestEntry} into its root or publish a release with the .fpk.");
            string project = libraries[0];
            CheckInside(root, Path.GetDirectoryName(project)!, JsonStrings(File.ReadAllText(project)));
            log?.Invoke($"Packing the library project {Path.GetRelativePath(root, project)} of the source...");
            string fpk = ProjectPacker(project, output, log);
            if (!File.Exists(fpk)) throw new PackageException($"Packing {Path.GetFileName(project)} made no package.");
            return fpk;
        }

        private static bool IsLibraryProject(string file)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && string.Equals(t.GetString(), "library", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is JsonException or IOException) { throw new PackageException($"{Path.GetFileName(file)} of the source is not valid: {ex.Message}"); }
        }

        private static IEnumerable<string> ManifestPaths(PackageManifest m)
        {
            if (m.Templates != null) yield return m.Templates;
            foreach (var d in m.Documents ?? new List<string>()) yield return d;
            foreach (var i in m.Imports)
            {
                if (i.Prelude != null) yield return i.Prelude;
                foreach (var r in i.Resources?.Values ?? Enumerable.Empty<string>()) yield return r;
                if (i.Native is { } n)
                    foreach (var f in n.Sources.Concat(n.PlatformSources.Values.SelectMany(l => l)).Concat(n.Libraries.Values).Concat(n.Headers)) yield return f;
            }
        }

        /// <summary>Every string of a JSON file (the project file: its paths, patterns and other values).</summary>
        private static IEnumerable<string> JsonStrings(string json)
        {
            var result = new List<string>();
            void Walk(JsonElement e)
            {
                switch (e.ValueKind)
                {
                    case JsonValueKind.String: result.Add(e.GetString()!); break;
                    case JsonValueKind.Array: foreach (var x in e.EnumerateArray()) Walk(x); break;
                    case JsonValueKind.Object: foreach (var p in e.EnumerateObject()) Walk(p.Value); break;
                }
            }
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            Walk(doc.RootElement);
            return result;
        }

        /// <summary>A path of the source that is absolute or leads out of it (`..`) would pack a file of this machine into the package: refused.</summary>
        private static void CheckInside(string root, string baseDir, IEnumerable<string> paths)
        {
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || path.Contains("://")) continue;
                string p = path.Replace('\\', '/');
                bool rooted = Path.IsPathRooted(p) || p.StartsWith('~') || Regex.IsMatch(p, "^[A-Za-z]:");
                bool leaves = !rooted && p.Contains("..") && !Path.GetFullPath(Path.Combine(baseDir, p)).StartsWith(prefix, StringComparison.Ordinal);
                if (rooted || leaves) throw new PackageException($"The source names a file outside of it ('{path}'): ember builds a package only from the files of the repository.");
            }
        }

        private static string PackWithForge(string project, string output, Action<string>? log)
        {
            string dir = AppContext.BaseDirectory;
            string? forge = new[] { "forge.exe", "forge" }.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
            ProcessStartInfo psi;
            if (forge != null) psi = new ProcessStartInfo(forge);
            else if (File.Exists(Path.Combine(dir, "forge.dll"))) { psi = new ProcessStartInfo("dotnet"); psi.ArgumentList.Add(Path.Combine(dir, "forge.dll")); }
            else throw new PackageException("The source is a library project; packing it needs forge (the compiler), which is not next to ember.");
            foreach (var arg in new[] { "build", project, "-o", output }) psi.ArgumentList.Add(arg);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using var process = Process.Start(psi) ?? throw new PackageException("forge could not be started.");
            var lines = new List<string>();
            process.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) lock (lines) lines.Add(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) lock (lines) lines.Add(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();
            var built = Directory.Exists(output) ? Directory.GetFiles(output, "*" + Fpk.Extension) : Array.Empty<string>();
            if (process.ExitCode != 0 || built.Length != 1)
                throw new PackageException($"forge could not pack {Path.GetFileName(project)}:\n  " + string.Join("\n  ", lines.TakeLast(20)));
            return built[0];
        }
    }
}
