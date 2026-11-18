using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using fire.Package.Manager;

/// <summary>Packages straight from GitHub (docs/packages/README.md "Straight from GitHub") against a fake GitHub on this machine: one HTTP server for the REST API, another for the
/// downloads (the storage host the API redirects to). No real network. `FIRE_TESTS_ONLY=registry FIRE_TESTS_FILTER=github` runs only these.</summary>
static partial class RegistryTests
{
    private sealed class FakeAsset { public int Id; public string Name = ""; public byte[] Data = Array.Empty<byte>(); public string? Digest; public long? Size; }
    private sealed class FakeRelease { public string Tag = ""; public List<FakeAsset> Assets = new(); }
    private sealed class FakeRepo
    {
        public Dictionary<string, string> Commits = new();          // ref (tag, branch, sha) -> commit
        public List<FakeRelease> Releases = new();                  // the last one is the newest
        public Dictionary<string, byte[]> Tarballs = new();         // commit -> .tar.gz
        public bool RateLimited;
    }

    /// <summary>The fake GitHub: `/repos/{o}/{r}/releases/latest`, `/releases/tags/{tag}`, `/commits/{ref}`, `/tarball/{sha}` (302 to the download host), `/releases/assets/{id}`
    /// (302 to the download host); the download host serves `/download/{id}` and `/codeload/{o}/{r}/{sha}`. Every request is recorded with its port and Authorization header.</summary>
    private sealed class FakeGitHub : IDisposable
    {
        public readonly Dictionary<string, FakeRepo> Repos = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(int Port, string Path, string? Auth)> Requests = new();
        private readonly HttpListener _api = new(), _files = new();
        public int ApiPort, FilesPort;
        private int _nextId = 1;
        private readonly List<Task> _loops = new();

        public string Api => $"http://127.0.0.1:{ApiPort}";
        public string Files => $"http://127.0.0.1:{FilesPort}";

        public FakeGitHub()
        {
            ApiPort = Start(_api);
            FilesPort = Start(_files);
            _loops.Add(Task.Run(() => Loop(_api, ApiPort, Api)));
            _loops.Add(Task.Run(() => Loop(_files, FilesPort, Files)));
        }

        private static int Start(HttpListener listener)
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                int candidate = Random.Shared.Next(20000, 60000);
                try { listener.Prefixes.Clear(); listener.Prefixes.Add($"http://127.0.0.1:{candidate}/"); listener.Start(); return candidate; }
                catch (HttpListenerException) { }
            }
            throw new InvalidOperationException("no port for the fake GitHub");
        }

        public FakeAsset Asset(string name, byte[] data, bool digest = true)
        {
            var a = new FakeAsset { Id = _nextId++, Name = name, Data = data };
            if (digest) a.Digest = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
            return a;
        }

        private string AssetJson(string repo, FakeAsset a) =>
            $$"""{ "id": {{a.Id}}, "name": "{{a.Name}}", "size": {{a.Size ?? a.Data.Length}}, {{(a.Digest != null ? $"\"digest\": \"{a.Digest}\"," : "")}} "url": "{{Api}}/repos/{{repo}}/releases/assets/{{a.Id}}", "browser_download_url": "{{Files}}/download/{{a.Id}}" }""";

        private string ReleaseJson(string repo, FakeRelease r) =>
            $$"""{ "tag_name": "{{r.Tag}}", "name": "{{r.Tag}}", "draft": false, "prerelease": false, "assets": [ {{string.Join(", ", r.Assets.Select(a => AssetJson(repo, a)))}} ] }""";

        private void Loop(HttpListener listener, int port, string self)
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = listener.GetContext(); } catch (Exception) { return; }
                try { Serve(context, port); }
                catch (Exception ex) { try { context.Response.StatusCode = 500; Write(context, ex.Message); } catch (Exception) { } }
                try { context.Response.Close(); } catch (Exception) { }
            }
        }

        private static void Write(HttpListenerContext c, string text, string type = "application/json") => Write(c, Encoding.UTF8.GetBytes(text), type);
        private static void Write(HttpListenerContext c, byte[] bytes, string type = "application/octet-stream")
        {
            c.Response.ContentType = type;
            c.Response.ContentLength64 = bytes.Length;
            c.Response.OutputStream.Write(bytes);
        }

        private void Serve(HttpListenerContext c, int port)
        {
            string path = c.Request.Url!.AbsolutePath;
            lock (Requests) Requests.Add((port, path, c.Request.Headers["Authorization"]));
            var parts = path.Trim('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
            void NotFound() { c.Response.StatusCode = 404; Write(c, """{ "message": "Not Found" }"""); }
            if (port == FilesPort)
            {
                string? id = parts.Length == 2 && parts[0] is "download" or "objects" ? parts[1] : null;
                if (id != null)
                {
                    var asset = Repos.Values.SelectMany(r => r.Releases).SelectMany(r => r.Assets).FirstOrDefault(a => a.Id.ToString() == id);
                    if (asset == null) { NotFound(); return; }
                    Write(c, asset.Data);
                }
                else if (parts is ["codeload", var o, var r, var sha] && Repos.TryGetValue(o + "/" + r, out var cr) && cr.Tarballs.TryGetValue(sha, out var tar)) Write(c, tar, "application/x-gzip");
                else NotFound();
                return;
            }
            if (parts.Length < 4 || parts[0] != "repos" || !Repos.TryGetValue(parts[1] + "/" + parts[2], out var repo)) { NotFound(); return; }
            string name = parts[1] + "/" + parts[2];
            if (repo.RateLimited)
            {
                c.Response.StatusCode = 403;
                c.Response.Headers["x-ratelimit-remaining"] = "0";
                c.Response.Headers["x-ratelimit-reset"] = DateTimeOffset.UtcNow.AddMinutes(17).ToUnixTimeSeconds().ToString();
                Write(c, """{ "message": "API rate limit exceeded" }""");
                return;
            }
            string rest = string.Join("/", parts.Skip(3));
            if (rest == "releases/latest") { if (repo.Releases.Count == 0) NotFound(); else Write(c, ReleaseJson(name, repo.Releases[^1])); }
            else if (rest.StartsWith("releases/tags/")) { var rel = repo.Releases.FirstOrDefault(r => r.Tag == rest.Substring(14)); if (rel == null) NotFound(); else Write(c, ReleaseJson(name, rel)); }
            else if (rest.StartsWith("releases/assets/"))
            {
                c.Response.StatusCode = 302;
                c.Response.RedirectLocation = $"{Files}/objects/{rest.Substring(16)}";
            }
            else if (rest.StartsWith("commits/"))
            {
                string reference = rest.Substring(8);
                string? sha = repo.Commits.TryGetValue(reference, out var s) ? s : repo.Commits.Values.Concat(repo.Tarballs.Keys).FirstOrDefault(v => reference.Length >= 7 && v.StartsWith(reference));
                if (sha == null) NotFound(); else Write(c, $$"""{ "sha": "{{sha}}", "commit": { "message": "x" } }""");
            }
            else if (rest.StartsWith("tarball/"))
            {
                c.Response.StatusCode = 302;
                c.Response.RedirectLocation = $"{Files}/codeload/{name}/{rest.Substring(8)}";
            }
            else NotFound();
        }

        public void Dispose()
        {
            _api.Stop(); _files.Stop();
            Task.WaitAll(_loops.ToArray(), TimeSpan.FromSeconds(5));
        }
    }

    private static string Sha40(char c) => new string(c, 40);

    /// <summary>A GitHub tarball: the files below one folder `owner-repo-sha/`.</summary>
    private static byte[] Tarball(string top, Dictionary<string, string> files)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, TarEntryFormat.Pax, leaveOpen: true))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/"));
            foreach (var (path, text) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, top + "/" + path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, top + "/link-to-passwd") { LinkName = "/etc/passwd" });
        }
        return ms.ToArray();
    }

    private static Dictionary<string, string> SourceFiles(string name, string version, string? prelude = null) => new()
    {
        ["package.json"] = $$"""{ "name": "{{name}}", "version": "{{version}}", "author": "tester", "license": "MIT", "imports": [ { "name": "{{name}}", "prelude": "{{prelude ?? "src/" + name + ".fire"}}" } ] }""",
        ["src/" + name + ".fire"] = $"class {name}Src {{ }}\n",
        ["README.md"] = "# " + name + "\n",
    };

    private static void RunGitHub(string dir)
    {
        // ---- the spec ----
        var spec = GitHubSpec.Parse("github:Jane/sensorkit@v1.2.0#kit");
        Check("GitHub: github:owner/repo@ref#name wird gelesen", spec.Owner == "Jane" && spec.Repo == "sensorkit" && spec.Ref == "v1.2.0" && spec.PackageName == "kit" && spec.Text == "github:Jane/sensorkit@v1.2.0");
        Check("GitHub: ohne @ die neueste Release, ein Commit wird erkannt", GitHubSpec.Parse("github:jane/kit").Ref == "" && GitHubSpec.Parse("github:jane/kit@3f2c1a0").RefIsCommit && !GitHubSpec.Parse("github:jane/kit@main").RefIsCommit);
        Check("GitHub: ungueltige Angaben werden abgelehnt", !GitHubSpec.TryParse("github:jane", out _) && !GitHubSpec.TryParse("github:jane/kit/x", out _) && !GitHubSpec.TryParse("github:jane/kit@../x", out _)
            && !GitHubSpec.TryParse("github:jane/..", out _) && !GitHubSpec.TryParse("github:-x/kit", out _));
        Check("GitHub: die API nur ueber https (http nur von diesem Rechner)", Throws(() => new GitHubSource(new GitHubSettings { ApiBase = "http://api.example.com" }), "https"));

        using var gh = new FakeGitHub();
        string built = Path.Combine(dir, "gh-built");
        byte[] Pack(string name, string version, string? dependency = null) => File.ReadAllBytes(Forge(Path.Combine(dir, "gh-src"), name, version, dependency));

        // ghkit: two releases with a .fpk and its digest (v1.0.0, later v1.1.0)
        var kit = new FakeRepo();
        kit.Commits["v1.0.0"] = Sha40('a');
        kit.Releases.Add(new FakeRelease { Tag = "v1.0.0", Assets = { gh.Asset("ghkit-1.0.0.fpk", Pack("ghkit", "1.0.0")), gh.Asset("notes.txt", Encoding.UTF8.GetBytes("x")) } });
        gh.Repos["jane/ghkit"] = kit;

        var settings = new GitHubSettings { ApiBase = gh.Api };
        PackageManagerService Service(string store, GitHubSettings? s = null) => new(new PackageStore(Path.Combine(dir, store)), Array.Empty<IPackageSource>(), s ?? settings);

        // ---- the newest release with a .fpk and a digest ----
        var service = Service("GH1");
        var log = new List<string>();
        service.Install("github:jane/ghkit", log.Add);
        var entry = service.Lock().ForRepository("jane/ghkit");
        Check("GitHub: die neueste Release mit .fpk und Digest wird installiert, geprueft und im Lockfile festgehalten",
            service.Store.Find("ghkit")?.Version == "1.0.0" && entry is { Kind: "release", Tag: "v1.0.0", Asset: "ghkit-1.0.0.fpk" } && entry.Commit == Sha40('a') && entry.Verified == "asset digest"
            && entry.Sha256 == kit.Releases[0].Assets[0].Digest!.Substring(7) && File.Exists(PackageLock.PathFor(service.Store)), string.Join("\n", log));
        Check("GitHub: die Installation nennt das Repository und die Pruefung", log.Any(l => l.Contains("https://github.com/jane/ghkit")) && log.Any(l => l.StartsWith("Verified")), string.Join("\n", log));
        Check("GitHub: ohne Token kommt die Datei von browser_download_url", gh.Requests.Any(r => r.Port == gh.FilesPort && r.Path == $"/download/{kit.Releases[0].Assets[0].Id}"));
        Check("GitHub: FindBySpec findet das Paket ueber das Lockfile (fuer Abhaengigkeiten und Projektverweise)",
            service.Store.FindBySpec("github:jane/ghkit")?.Name == "ghkit" && service.Store.FindBySpec("github:jane/ghkit@v1.0.0")?.Name == "ghkit" && service.Store.FindBySpec("github:jane/other") == null);

        // ---- pinning: a newer release does not change an install of the same spec; ember update moves it ----
        kit.Commits["v1.1.0"] = Sha40('b');
        kit.Releases.Add(new FakeRelease { Tag = "v1.1.0", Assets = { gh.Asset("ghkit-1.1.0.fpk", Pack("ghkit", "1.1.0")) } });
        log.Clear();
        service.Install("github:jane/ghkit", log.Add);
        Check("GitHub: das Lockfile haelt die installierte Release fest (eine neuere wird ohne update nicht genommen)",
            service.Store.Find("ghkit")?.Version == "1.0.0" && log.Any(l => l.Contains("pinned")) && log.Any(l => l.Contains("already installed")), string.Join("\n", log));
        var updated = service.Update(null, log.Add);
        Check("GitHub: ember update geht zur neuesten Release und schreibt das Lockfile neu",
            service.Store.Find("ghkit")?.Version == "1.1.0" && service.Lock().ForRepository("jane/ghkit") is { Tag: "v1.1.0" } e2 && e2.Commit == Sha40('b')
            && updated.Any(u => u.Name == "ghkit" && u.OldVersion == "1.0.0" && u.NewVersion == "1.1.0"), string.Join("\n", log));
        Check("GitHub: ember update ohne Neues meldet den Stand", service.Update("ghkit").Single().NewVersion == "1.1.0");

        // ---- a tag, with or without the v; a pinned tag stays on update ----
        var tagged = Service("GH2");
        tagged.Install("github:jane/ghkit@1.0.0");
        Check("GitHub: ein Tag (auch ohne v) installiert diese Release", tagged.Store.Find("ghkit")?.Version == "1.0.0" && tagged.Lock().ForRepository("jane/ghkit") is { Ref: "1.0.0", Tag: "v1.0.0" });
        tagged.Update();
        Check("GitHub: ein festgelegter Tag bleibt bei ember update", tagged.Store.Find("ghkit")?.Version == "1.0.0");
        tagged.Update("github:jane/ghkit");
        Check("GitHub: ember update github:owner/repo nimmt auch einen festgelegten auf die neueste Release", tagged.Store.Find("ghkit")?.Version == "1.1.0");
        tagged.Remove("github:jane/ghkit");
        Check("GitHub: ember remove github:owner/repo entfernt Paket und Lockeintrag", tagged.Store.Find("ghkit") == null && tagged.Lock().GitHub.Count == 0);

        // ---- a moved tag and a replaced asset are refused ----
        kit.Commits["v1.1.0"] = Sha40('c');
        Check("GitHub: ein verschobener Tag wird abgelehnt", Throws(() => service.Install("github:jane/ghkit@v1.1.0", force: true), "moved"));
        kit.Commits["v1.1.0"] = Sha40('b');

        // ---- checksums: a wrong digest, a .sha256 asset, SHA256SUMS, none (trust on first use) ----
        var bad = new FakeRepo();
        bad.Commits["v1.0.0"] = Sha40('d');
        var badAsset = gh.Asset("ghbad-1.0.0.fpk", Pack("ghbad", "1.0.0"));
        badAsset.Digest = "sha256:" + new string('0', 64);
        bad.Releases.Add(new FakeRelease { Tag = "v1.0.0", Assets = { badAsset } });
        gh.Repos["jane/ghbad"] = bad;
        var s3 = Service("GH3");
        Check("GitHub: eine falsche Pruefsumme (Digest) wird abgelehnt, nichts installiert", Throws(() => s3.Install("github:jane/ghbad"), "checksum") && s3.Store.Find("ghbad") == null && s3.Lock().GitHub.Count == 0);

        var sums = new FakeRepo();
        sums.Commits["v2.0.0"] = Sha40('e');
        byte[] sumsFpk = Pack("ghsums", "2.0.0");
        string sumsSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sumsFpk)).ToLowerInvariant();
        sums.Releases.Add(new FakeRelease { Tag = "v2.0.0", Assets = { gh.Asset("ghsums-2.0.0.fpk", sumsFpk, digest: false), gh.Asset("SHA256SUMS", Encoding.UTF8.GetBytes($"{new string('1', 64)}  other.fpk\n{sumsSha}  ghsums-2.0.0.fpk\n"), digest: false),
            gh.Asset("ghsums-2.0.0.fpk.sha256", Encoding.UTF8.GetBytes(sumsSha + "  ghsums-2.0.0.fpk\n"), digest: false) } });
        gh.Repos["jane/ghsums"] = sums;
        s3.Install("github:jane/ghsums");
        Check("GitHub: .sha256-Asset und SHA256SUMS pruefen die Datei", s3.Lock().ForRepository("jane/ghsums")?.Verified == "sha256 asset and SHA256SUMS", s3.Lock().ForRepository("jane/ghsums")?.Verified);
        var sumsWrong = new FakeRepo();
        sumsWrong.Commits["v1.0.0"] = Sha40('f');
        sumsWrong.Releases.Add(new FakeRelease { Tag = "v1.0.0", Assets = { gh.Asset("ghsw.fpk", Pack("ghsw", "1.0.0"), digest: false), gh.Asset("ghsw.fpk.sha256", Encoding.UTF8.GetBytes(new string('2', 64)), digest: false) } });
        gh.Repos["jane/ghsw"] = sumsWrong;
        Check("GitHub: ein .sha256-Asset, das nicht passt, wird abgelehnt", Throws(() => s3.Install("github:jane/ghsw"), "checksum") && s3.Store.Find("ghsw") == null);

        var tofu = new FakeRepo();
        tofu.Commits["v1.0.0"] = Sha40('1');
        var tofuAsset = gh.Asset("ghtofu.fpk", Pack("ghtofu", "1.0.0"), digest: false);
        tofu.Releases.Add(new FakeRelease { Tag = "v1.0.0", Assets = { tofuAsset } });
        gh.Repos["jane/ghtofu"] = tofu;
        log.Clear();
        s3.Install("github:jane/ghtofu", log.Add);
        Check("GitHub: ohne Pruefsumme der Release: deutlich als nicht geprueft gemeldet, die SHA-256 kommt ins Lockfile (trust on first use)",
            s3.Store.Find("ghtofu") != null && s3.Lock().ForRepository("jane/ghtofu") is { Verified: "first use" } t && t.Sha256.Length == 64 && log.Any(l => l.Contains("NOT verified upstream")), string.Join("\n", log));
        tofuAsset.Data = Pack("ghtofu", "1.0.1");
        Check("GitHub: danach wird gegen das Lockfile geprueft - eine ersetzte Datei unter demselben Tag wird abgelehnt",
            Throws(() => s3.Install("github:jane/ghtofu@v1.0.0", force: true), "replaced") && s3.Store.Find("ghtofu")?.Version == "1.0.0");
        var big = new FakeRepo();
        big.Commits["v1.0.0"] = Sha40('2');
        var bigAsset = gh.Asset("ghbig.fpk", Pack("ghbig", "1.0.0"));
        bigAsset.Size = PackageDownloads.MaxPackageBytes + 1;
        big.Releases.Add(new FakeRelease { Tag = "v1.0.0", Assets = { bigAsset } });
        gh.Repos["jane/ghbig"] = big;
        Check("GitHub: eine zu grosse Datei wird nicht geladen", Throws(() => s3.Install("github:jane/ghbig"), "more than ember takes"));

        // ---- no .fpk asset: the package is built from the source of the commit (package.json), a branch, a library project ----
        var src = new FakeRepo();
        src.Commits["v0.3.0"] = Sha40('3');
        src.Commits["main"] = Sha40('4');
        src.Tarballs[Sha40('3')] = Tarball("jane-ghsrc-3333333", SourceFiles("ghsrc", "0.3.0"));
        src.Tarballs[Sha40('4')] = Tarball("jane-ghsrc-4444444", SourceFiles("ghsrc", "0.4.0"));
        src.Releases.Add(new FakeRelease { Tag = "v0.3.0" });
        gh.Repos["jane/ghsrc"] = src;
        var s4 = Service("GH4");
        log.Clear();
        s4.Install("github:jane/ghsrc", log.Add);
        var srcEntry = s4.Lock().ForRepository("jane/ghsrc");
        var srcInstalled = s4.Store.Find("ghsrc");
        Check("GitHub: eine Release ohne .fpk: aus dem Quelltext des Commits gebaut, der Commit festgehalten",
            srcInstalled?.Version == "0.3.0" && srcEntry is { Kind: "source", Tag: "v0.3.0" } && srcEntry.Commit == Sha40('3') && File.Exists(Path.Combine(srcInstalled!.Directory, "README.md"))
            && !File.Exists(Path.Combine(srcInstalled.Directory, "link-to-passwd")) && gh.Requests.Any(r => r.Port == gh.FilesPort && r.Path.StartsWith("/codeload/jane/ghsrc/" + Sha40('3'))), string.Join("\n", log));
        s4.Install("github:jane/ghsrc@main");
        Check("GitHub: ein Zweig wird aus dem Quelltext gebaut und auf seinen Commit festgelegt", s4.Store.Find("ghsrc")?.Version == "0.4.0" && s4.Lock().ForRepository("jane/ghsrc") is { Ref: "main", Tag: null } b && b.Commit == Sha40('4'));
        src.Commits["main"] = Sha40('5');
        src.Tarballs[Sha40('5')] = Tarball("jane-ghsrc-5555555", SourceFiles("ghsrc", "0.5.0"));
        s4.Install("github:jane/ghsrc@main", force: true);
        Check("GitHub: ein Zweig bleibt auf dem festgehaltenen Commit, bis ember update", s4.Store.Find("ghsrc")?.Version == "0.4.0" && s4.Lock().ForRepository("jane/ghsrc")!.Commit == Sha40('4'));
        s4.Update("ghsrc");
        Check("GitHub: ember update nimmt den neuesten Commit des Zweigs", s4.Store.Find("ghsrc")?.Version == "0.5.0" && s4.Lock().ForRepository("jane/ghsrc")!.Commit == Sha40('5'));
        s4.Install("github:jane/ghsrc@4444444");
        Check("GitHub: ein Commit (kurz) wird aus dem Quelltext gebaut", s4.Store.Find("ghsrc")?.Version == "0.4.0" && s4.Lock().ForRepository("jane/ghsrc") is { Ref: "4444444" } c4 && c4.Commit == Sha40('4'));

        var evil = new FakeRepo();
        evil.Commits["main"] = Sha40('6');
        evil.Tarballs[Sha40('6')] = Tarball("jane-ghevil-6666666", SourceFiles("ghevil", "1.0.0", prelude: "../../../../etc/hostname"));
        gh.Repos["jane/ghevil"] = evil;
        Check("GitHub: ein Quelltext, der Dateien ausserhalb nennt, wird nicht gepackt", Throws(() => s4.Install("github:jane/ghevil@main"), "outside") && s4.Store.Find("ghevil") == null);

        var proj = new FakeRepo();
        proj.Commits["main"] = Sha40('7');
        proj.Tarballs[Sha40('7')] = Tarball("jane-ghproj-7777777", new()
        {
            ["ghproj.fireproj"] = """{ "name": "ghproj", "type": "library", "settings": { "version": "2.3.4", "license": "MIT" } }""",
            ["Lib.fire"] = "class GhProjThing { }\n",
            ["LICENSE"] = "MIT\n",
        });
        gh.Repos["jane/ghproj"] = proj;
        var packer = GitHubSource.ProjectPacker;
        GitHubSource.ProjectPacker = (file, output, _) =>
        {
            var ws = fire.Projects.Workspace.Open(file);
            return fire.Compiler.ProjectBuilder.PackLibrary(fire.Projects.BuildPlan.Create(ws, ws.Projects[0]), output);
        };
        try
        {
            s4.Install("github:jane/ghproj@main");
            Check("GitHub: ein Bibliotheksprojekt (.fireproj) im Quelltext wird wie Pack Library gepackt", s4.Store.Find("ghproj") is { Version: "2.3.4" } p && p.Manifest.Imports[0].Name == "ghproj");
        }
        finally { GitHubSource.ProjectPacker = packer; }

        // ---- the name of the package: like the repository, or accepted with #name ----
        var other = new FakeRepo();
        other.Commits["v1.0.0"] = Sha40('8');
        other.Releases.Add(new FakeRelease { Tag = "v1.0.0", Assets = { gh.Asset("ghnamed.fpk", Pack("ghnamed", "1.0.0")) } });
        gh.Repos["jane/fire-named"] = other;
        var s5 = Service("GH5");
        Check("GitHub: ein Paket, das anders heisst als das Repository, braucht #name", Throws(() => s5.Install("github:jane/fire-named"), "#ghnamed") && s5.Store.Find("ghnamed") == null);
        s5.Install("github:jane/fire-named#ghnamed");
        Check("GitHub: mit #name wird es angenommen", s5.Store.Find("ghnamed") != null);
        s5.Install(Path.Combine(dir, "gh-src", "built", "ghkit-1.0.0.fpk"));
        Check("GitHub: ein Paket desselben Namens von anderswo wird nur mit --force ersetzt", Throws(() => s5.Install("github:jane/ghkit"), "--force") && s5.Store.Find("ghkit")?.Version == "1.0.0");

        // ---- a dependency on github:owner/repo in a package.json ----
        var app = new FakeRepo();
        app.Commits["v1.0.0"] = Sha40('9');
        app.Releases.Add(new FakeRelease { Tag = "v1.0.0", Assets = { gh.Asset("ghapp.fpk", Pack("ghapp", "1.0.0", dependency: "github:jane/ghsums@v2.0.0")) } });
        gh.Repos["jane/ghapp"] = app;
        var s6 = Service("GH6");
        var both = s6.Install("github:jane/ghapp");
        Check("GitHub: eine Abhaengigkeit github:owner/repo@tag wird mit installiert (und gilt danach als vorhanden)",
            both.Count == 2 && s6.Store.Find("ghsums") != null && s6.Lock().GitHub.Count == 2 && s6.Store.DependentsOf("ghsums").Any(d => d.Name == "ghapp"));
        Check("GitHub: eine Abhaengigkeit verhindert das Entfernen", Throws(() => s6.Remove("ghsums"), "needed by"));

        // ---- the token: only to the API host; the redirect to the download host gets none ----
        lock (gh.Requests) gh.Requests.Clear();
        var s7 = Service("GH7", new GitHubSettings { ApiBase = gh.Api, Token = "secret-token", TokenSource = "test" });
        s7.Install("github:jane/ghkit");
        s7.Install("github:jane/ghsrc@main");
        List<(int Port, string Path, string? Auth)> seen;
        lock (gh.Requests) seen = gh.Requests.ToList();
        Check("GitHub: der Token geht an den API-Host, nie an den Download-Host (auch nicht nach der Weiterleitung)",
            seen.Where(r => r.Port == gh.ApiPort).All(r => r.Auth == "Bearer secret-token") && seen.Any(r => r.Port == gh.ApiPort && r.Path.Contains("/releases/assets/"))
            && seen.Where(r => r.Port == gh.FilesPort).All(r => r.Auth == null) && seen.Any(r => r.Port == gh.FilesPort && r.Path.StartsWith("/objects/")) && seen.Any(r => r.Port == gh.FilesPort && r.Path.StartsWith("/codeload/")),
            string.Join("\n", seen.Select(r => $"{r.Port} {r.Path} {r.Auth}")));

        // ---- the rate limit, a repository that is not there ----
        gh.Repos["jane/ghlimit"] = new FakeRepo { RateLimited = true };
        Check("GitHub: das Rate-Limit wird klar gemeldet (wann es zurueckgesetzt wird, mit dem Hinweis auf den Token)",
            Throws(() => Service("GH8").Install("github:jane/ghlimit"), "rate limit is used up") && Throws(() => Service("GH8").Install("github:jane/ghlimit"), "resets at")
            && Throws(() => Service("GH8").Install("github:jane/ghlimit"), "GITHUB_TOKEN"));
        Check("GitHub: ein Repository ohne Release verlangt einen Ref", Throws(() => Service("GH8").Install("github:jane/nothere"), "has no release"));
        Check("GitHub: ein unbekannter Ref wird gemeldet", Throws(() => Service("GH8").Install("github:jane/ghkit@nope"), "no release, tag, branch or commit"));
    }
}
