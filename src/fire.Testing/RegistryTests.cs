using System.IO.Compression;
using System.Net;
using fire.Package.Manager;

/// <summary>The package sources of ember against a registry on the web (docs/packages/README.md "Sources", "Publishing"): the index the website writes (extra fields, yanked
/// versions, sizes), https only (http for this machine), no download without a checksum, a whole install from a local HTTP server, the documents of a package.
/// `FIRE_TESTS_ONLY=registry` - quick, no compiler, no network.</summary>
static class RegistryTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Registry: {title}" : $"FEHLER: Registry: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static bool Throws(Action action, string? containing = null)
    {
        try { action(); return false; }
        catch (PackageException ex) { return containing == null || ex.Message.Contains(containing); }
    }

    public static void Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fire-registry-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { RunIn(dir); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        Console.WriteLine(_failures == 0 ? "Alle Registry-Pruefungen bestanden." : $"FEHLER: {_failures} Registry-Pruefung(en) fehlgeschlagen.");
        if (_failures > 0) Environment.ExitCode = 1;
    }

    // a package in a folder of its own; `files` are written next to the forge file (README.md, LICENSE, ...)
    private static string Forge(string root, string name, string version, string? dependency = null, string license = "MIT", Dictionary<string, string>? files = null)
    {
        string src = Path.Combine(root, "src-" + name + "-" + version);
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, name + ".fire"), $"class {name}V{version.Replace('.', '_')} {{ }}\n");
        foreach (var (file, text) in files ?? new()) File.WriteAllText(Path.Combine(src, file), text);
        var m = new PackageManifest { Name = name, Version = version, Author = "tester", Description = "registry test " + name, License = license, Imports = { new PackageImport { Name = name, Prelude = name + ".fire" } } };
        if (dependency != null) m.Dependencies.Add(dependency);
        string forge = Path.Combine(src, "package.json");
        m.Save(forge);
        return Fpk.Forge(forge, Path.Combine(root, "built")).PackagePath;
    }

    private static void RunIn(string dir)
    {
        // ---- the documents of a package: README/LICENSE next to the forge file go into the root of the package ----
        string withDocs = Forge(dir, "docpkg", "1.0.0", files: new() { ["README.md"] = "# docpkg\n", ["LICENSE"] = "MIT License\n", ["notes.txt"] = "not a document" });
        using (var zip = ZipFile.OpenRead(withDocs))
        {
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();
            var manifest = Fpk.ReadManifest(withDocs);
            Check("forge legt README und LICENSE in die Wurzel des Pakets, andere Dateien nicht",
                names.Contains("README.md") && names.Contains("LICENSE") && !names.Contains("notes.txt") && manifest.Documents is { Count: 2 } d && d.Contains("README.md") && d.Contains("LICENSE"),
                string.Join(", ", names));
        }
        Check("ein Paket ohne Dokumente hat keine (documents fehlt)", Fpk.ReadManifest(Forge(dir, "nodocs", "1.0.0")).Documents == null);

        // ---- the index: license, size, yanked; an index of a newer format is refused ----
        string site = Path.Combine(dir, "site", "packages");
        string files = Path.Combine(site, "files");
        Directory.CreateDirectory(files);
        foreach (var (n, v, dep) in new[] { ("regbase", "1.0.0", (string?)null), ("regtop", "1.0.0", "regbase"), ("regtop", "2.0.0", "regbase") })
            File.Copy(Forge(dir, n, v, dep, n == "regtop" ? "Apache-2.0" : "MIT"), Path.Combine(files, $"{n}-{v}.fpk"), true);
        string built = PackageIndex.Build(files, null);
        var fromBuild = PackageIndex.Parse(built, "built", u => u);
        Check("ember index schreibt Lizenz, Groesse und Pruefsumme",
            fromBuild.First(l => l.Name == "regtop").License == "Apache-2.0" && fromBuild.SelectMany(l => l.Versions).All(v => v.Size > 0 && v.Sha256 is { Length: 64 }), built);
        Check("ein Index eines neueren Formats wird abgelehnt", Throws(() => PackageIndex.Parse("{ \"format\": 2, \"packages\": [] }", "x", u => u), "newer ember"));

        // the index as the website writes it (more fields than ember reads; the urls relative to the index; 2.0.0 withdrawn)
        string Sha(string file) => PackageDownloads.Sha256Of(Path.Combine(files, file));
        long Size(string file) => new FileInfo(Path.Combine(files, file)).Length;
        string siteIndex = $$"""
            {
              "format": 1, "generated": "2026-11-17T12:00:00Z", "registry": "test",
              "packages": [
                { "name": "regbase", "author": "tester", "description": "base", "license": "MIT", "homepage": "https://example.com", "page": "regbase.html", "keywords": ["x"],
                  "versions": [ { "version": "1.0.0", "url": "files/regbase-1.0.0.fpk", "sha256": "{{Sha("regbase-1.0.0.fpk")}}", "size": {{Size("regbase-1.0.0.fpk")}}, "published": "2026-11-17", "dependencies": [], "imports": [ { "name": "regbase" } ] } ] },
                { "name": "regtop", "author": "tester", "description": "top", "license": "Apache-2.0",
                  "versions": [
                    { "version": "1.0.0", "url": "files/regtop-1.0.0.fpk", "sha256": "{{Sha("regtop-1.0.0.fpk")}}", "size": {{Size("regtop-1.0.0.fpk")}}, "dependencies": ["regbase"] },
                    { "version": "2.0.0", "url": "files/regtop-2.0.0.fpk", "sha256": "{{Sha("regtop-2.0.0.fpk")}}", "size": {{Size("regtop-2.0.0.fpk")}}, "yanked": true, "yankedReason": "broken" }
                  ] },
                { "name": "regbroken", "description": "a file that is not the published one",
                  "versions": [ { "version": "1.0.0", "url": "files/regtop-1.0.0.fpk", "sha256": "{{new string('0', 64)}}" } ] },
                { "name": "regsize", "description": "the size does not match",
                  "versions": [ { "version": "1.0.0", "url": "files/regbase-1.0.0.fpk", "sha256": "{{Sha("regbase-1.0.0.fpk")}}", "size": 3 } ] },
                { "name": "regnosha", "versions": [ { "version": "1.0.0", "url": "files/regbase-1.0.0.fpk" } ] }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(site, "index.json"), siteIndex);
        var listed = new IndexSource(Path.Combine(site, "index.json")).List();
        var top = listed.First(l => l.Name == "regtop");
        Check("der Index der Webseite: zusaetzliche Felder stoeren nicht, Lizenz und Homepage werden gelesen",
            listed.Count == 5 && top.License == "Apache-2.0" && listed.First(l => l.Name == "regbase").Homepage == "https://example.com");
        Check("eine zurueckgezogene Version (yanked) ist nie die neueste", top.Versions.Count == 2 && top.Versions.Single(v => v.Version == "2.0.0").Yanked && top.Latest?.Version == "1.0.0");
        var allYanked = new PackageListing("y", "", "", new[] { new PackageVersion("1.0.0", "a", null, Yanked: true) }, "s");
        Check("sind alle Versionen zurueckgezogen, gibt es keine neueste", allYanked.Latest == null);

        // ---- the web: https only (http for this machine), no download without a checksum ----
        Check("http von einem anderen Rechner wird abgelehnt (Index und Paket)",
            Throws(() => new IndexSource("http://example.com/packages/index.json").List(), "https")
            && Throws(() => PackageDownloads.Fetch(new PackageVersion("1.0.0", "http://example.com/a.fpk", new string('0', 64)), Path.Combine(dir, "dl")), "https"));
        Check("ein Paket aus dem Web ohne sha256 im Index wird nicht geladen",
            Throws(() => PackageDownloads.Fetch(new PackageVersion("1.0.0", "https://example.invalid/a.fpk"), Path.Combine(dir, "dl")), "sha256"));
        Check("die Standardquelle ist der Index der Webseite", EmberSettings.DefaultIndexUrl == "https://what3var.github.io/packages/index.json"
            && EmberSettings.LoadSources(Path.Combine(dir, "nosettings")).Any(s => s.Name == EmberSettings.DefaultIndexUrl));
        string settingsDir = Path.Combine(dir, "settings");
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(Path.Combine(settingsDir, EmberSettings.FileName), "{ \"sources\": [ \"https://mirror.example.org/packages/index.json\", \"local\" ] }");
        var configured = EmberSettings.LoadSources(settingsDir);
        Check("ember.json ersetzt die Quellen (eine Adresse, ein Ordner)", configured.Count == 2 && configured[0] is IndexSource { Location: "https://mirror.example.org/packages/index.json" } && configured[1] is FolderSource);

        // ---- a whole install over HTTP: the site folder served on this machine ----
        using var server = new HttpListener();
        int port = 0;
        for (int attempt = 0; attempt < 20 && port == 0; attempt++)
        {
            int candidate = Random.Shared.Next(20000, 60000);
            try { server.Prefixes.Clear(); server.Prefixes.Add($"http://127.0.0.1:{candidate}/"); server.Start(); port = candidate; }
            catch (HttpListenerException) { }
        }
        if (port == 0) { Check("ein HTTP-Server auf 127.0.0.1 laesst sich starten", false); return; }
        var requests = new List<string>();
        var serving = Task.Run(() =>
        {
            while (server.IsListening)
            {
                HttpListenerContext context;
                try { context = server.GetContext(); } catch (Exception) { return; }
                string path = Uri.UnescapeDataString(context.Request.Url!.AbsolutePath).TrimStart('/');
                lock (requests) requests.Add(path);
                string file = Path.GetFullPath(Path.Combine(dir, "site", path));
                if (file.StartsWith(Path.Combine(dir, "site")) && File.Exists(file))
                {
                    byte[] bytes = File.ReadAllBytes(file);
                    context.Response.ContentLength64 = bytes.Length;
                    context.Response.OutputStream.Write(bytes);
                }
                else context.Response.StatusCode = 404;
                context.Response.Close();
            }
        });
        try
        {
            string url = $"http://127.0.0.1:{port}/packages/index.json";
            var service = new PackageManagerService(new PackageStore(Path.Combine(dir, "Packages")), new IPackageSource[] { new IndexSource(url) });
            var log = new List<string>();
            var installed = service.Install("regtop", log.Add);
            Check("install ueber HTTP: die neueste nicht zurueckgezogene Version samt Abhaengigkeit, die Adressen relativ zum Index",
                service.Store.Find("regtop")?.Version == "1.0.0" && service.Store.Find("regbase") != null && installed.Count == 2
                && requests.Contains("packages/index.json") && requests.Contains("packages/files/regtop-1.0.0.fpk") && requests.Contains("packages/files/regbase-1.0.0.fpk"), string.Join("\n", log.Concat(requests)));
            service.Install("regtop@2.0.0", log.Add);
            Check("eine zurueckgezogene Version laesst sich mit name@version noch installieren (mit Hinweis)", service.Store.Find("regtop")?.Version == "2.0.0" && log.Any(l => l.Contains("yanked")));
            Check("eine falsche Pruefsumme, eine falsche Groesse, eine fehlende Pruefsumme: nichts wird installiert",
                Throws(() => service.Install("regbroken"), "checksum") && Throws(() => service.Install("regsize"), "bytes") && Throws(() => service.Install("regnosha"), "sha256")
                && service.Store.Find("regbroken") == null && service.Store.Find("regsize") == null && service.Store.Find("regnosha") == null);
            var gone = new PackageManagerService(new PackageStore(Path.Combine(dir, "P2")), new IPackageSource[] { new IndexSource($"http://127.0.0.1:{port}/nothere/index.json") });
            var warnings = new List<string>();
            Check("eine Quelle, die nicht zu lesen ist, wird gemeldet und uebersprungen", gone.Find("", warnings).Count == 0 && warnings.Count == 1 && warnings[0].Contains("cannot be read"), string.Join("\n", warnings));
        }
        finally
        {
            server.Stop();
            serving.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
