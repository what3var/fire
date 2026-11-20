using System.Diagnostics;
using fire.Compiler;
using fire.Projects;

/// <summary>Build actions and Publish (docs/tools/projects.md, "Build actions" and "Publish"): `buildActions` and `publish` in the project file read and written (old files without them stay
/// as they were), Ignore takes a source out of the compile, Copy copies a file next to the program (keeping its folder) and into the publish folders, a resource that the code names is still
/// embedded, Publish builds only what is not up to date, copies into its folders without touching other files there (unless `clean`), skips projects that have it off and puts a library
/// before the programs that use it; the quick build (`--quick`, spark's hare): the command line, the flags and units by toolchain, the key of the program cache, a quick
/// publish marked and with its own up-to-date state. A quick run: FIRE_TESTS_ONLY=publish (FIRE_TESTS_FILTER narrows it by the title of a check). The native publish runs when a C compiler is there.</summary>
static class PublishTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        string? filter = Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER");
        if (!string.IsNullOrWhiteSpace(filter) && !filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(f => title.Contains(f, StringComparison.OrdinalIgnoreCase))) return;
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Publish: {title}" : $"FEHLER: Publish: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static string Write(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public static int Run()
    {
        Console.WriteLine("=== Build-Aktionen und Publish ===");
        string root = Path.Combine(Path.GetTempPath(), "fire-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Format(root);
            Actions(root);
            PublishVm(root);
            PublishNative(root);
            QuickBuild(root);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        Console.WriteLine(_failures == 0 ? "Alle Publish-Pruefungen bestanden." : $"FEHLER: {_failures} Publish-Pruefung(en) fehlgeschlagen.");
        return _failures;
    }

    private static string Log(PublishResult r) => string.Join("\n", r.Projects.Select(p => $"{p.Project.Name}: ok={p.Ok} skipped={p.Skipped} upToDate={p.UpToDate} error={p.Error}"));

    // ---- the format ----------------------------------------------------------------------------------------------------------------------
    private static void Format(string root)
    {
        var old = FireProject.Parse("""{ "format": 1, "name": "Old", "exclude": [] }""");
        string oldJson = old.ToJson();
        Check("altes Projekt ohne buildActions/publish: nichts davon wird geschrieben", !oldJson.Contains("buildActions") && !oldJson.Contains("publish"), oldJson);

        var p = FireProject.Parse("""
            { "name": "App", "buildActions": { "data/**": "copy", "old.fire": "ignore", "big.bin": "copyAlways", "keep.fire": "build" },
              "publish": { "enabled": true, "targets": ["../dist/App", "/tmp/x"], "engine": "vm", "mode": "performance", "target": "linux", "clean": true } }
            """);
        var back = FireProject.Parse(p.ToJson());
        Check("buildActions hin und zurueck", back.BuildActions is { Count: 4 } b && b["data/**"] == "copy" && b["old.fire"] == "ignore" && b["big.bin"] == "copyAlways" && b["keep.fire"] == "build", p.ToJson());
        Check("publish hin und zurueck", back.Publish is { Enabled: true, Engine: "vm", Mode: "performance", Target: "linux", Clean: true } pub && pub.Targets!.SequenceEqual(new[] { "../dist/App", "/tmp/x" }), p.ToJson());
        Check("publish: die Reihenfolge der Muster bleibt erhalten", back.BuildActions!.Keys.SequenceEqual(new[] { "data/**", "old.fire", "big.bin", "keep.fire" }));
        Check("unbekannte Build-Aktion ist ein Problem", FireProject.Parse("""{ "name": "X", "buildActions": { "a.txt": "zip" } }""").Validate().Any(v => v.Contains("buildActions")));
        Check("unbekannte publish.engine ist ein Problem", FireProject.Parse("""{ "name": "X", "publish": { "engine": "jvm" } }""").Validate().Any(v => v.Contains("publish.engine")));
        Check("ein leeres publish wird nicht geschrieben", !new FireProject { Name = "E", Publish = new PublishSettings() }.ToJson().Contains("publish"));
        Check("Namen: copy-always und copyIfNewer werden gelesen", FileBuildActions.Parse("copy-always") == FileBuildAction.CopyAlways && FileBuildActions.Parse("copyIfNewer") == FileBuildAction.Copy && FileBuildActions.Parse("nope") == null);

        // resolving: the file's own entry before a pattern, the last pattern that matches
        var q = new FireProject { Name = "Q", BuildActions = new() { ["assets/**"] = "copy", ["assets/raw/**"] = "ignore", ["assets/raw/keep.png"] = "copyAlways" } };
        Check("Muster: das letzte passende entscheidet", FileBuildActions.Of(q, "assets/raw/x.png") == FileBuildAction.Ignore && FileBuildActions.Of(q, "assets/a.png") == FileBuildAction.Copy);
        Check("der eigene Eintrag geht vor jedes Muster", FileBuildActions.Of(q, "assets/raw/keep.png") == FileBuildAction.CopyAlways);
        Check("ohne Eintrag: Build", FileBuildActions.Of(q, "main.fire", out bool explicitly) == FileBuildAction.Build && !explicitly);
        FileBuildActions.Set(q, "assets/raw/keep.png", FileBuildAction.Ignore);
        Check("Set auf den Wert des Musters nimmt den eigenen Eintrag heraus", !q.BuildActions!.ContainsKey("assets/raw/keep.png") && FileBuildActions.Of(q, "assets/raw/keep.png") == FileBuildAction.Ignore);
        FileBuildActions.Set(q, "assets/raw/y.png", FileBuildAction.Build);
        Check("Set Build gegen ein Muster schreibt \"build\" fuer die Datei", q.BuildActions!.TryGetValue("assets/raw/y.png", out var v) && v == "build");
        var r = new FireProject { Name = "R" };
        FileBuildActions.Set(r, "a.txt", FileBuildAction.Copy);
        FileBuildActions.Set(r, "a.txt", FileBuildAction.Build);
        Check("Set Build ohne Muster: buildActions faellt ganz weg", r.BuildActions == null && !r.ToJson().Contains("buildActions"), r.ToJson());
    }

    // ---- Ignore and Copy in the files of a project ---------------------------------------------------------------------------------------------
    private static void Actions(string root)
    {
        string dir = Path.Combine(root, "act");
        Write(dir, "Act.fireproj", """{ "name": "Act", "buildActions": { "legacy/**": "ignore", "scripts/plugin.fire": "copy", "data/**": "copy", "data/huge.bin": "ignore" } }""");
        string main = Write(dir, "main.fire", "print(\"main\")\n");
        string legacy = Write(dir, "legacy/old.fire", "this is not fire code ((\n");
        string plugin = Write(dir, "scripts/plugin.fire", "print(\"plugin\")\n");
        string levels = Write(dir, "data/levels/one.txt", "level 1\n");
        string huge = Write(dir, "data/huge.bin", "big\n");
        string readme = Write(dir, "README.md", "# act\n");
        var project = new LoadedProject(FireProject.Load(Path.Combine(dir, "Act.fireproj")), false);
        Check("Ignore: die Quelle wird nicht kompiliert", !project.Files.Contains(legacy) && project.Files.Contains(main), string.Join(", ", project.Files));
        Check("Copy: eine Quelle mit Copy wird nicht kompiliert", !project.Files.Contains(plugin));
        Check("Ignore: die Datei bleibt Inhalt des Projekts (der Explorer zeigt sie blass)", project.ContentFiles.Contains(legacy) && project.IgnoredFiles.Contains(legacy));
        var copies = project.CopyFiles.ToDictionary(c => c.RelativePath, c => c);
        Check("Copy: die Dateien mit ihrem Pfad relativ zum Projekt", copies.ContainsKey("data/levels/one.txt") && copies.ContainsKey("scripts/plugin.fire") && !copies.ContainsKey("data/huge.bin") && !copies.ContainsKey("README.md"),
            string.Join(", ", copies.Keys));
        Check("BuildActionOf einer Datei", project.BuildActionOf(huge) == FileBuildAction.Ignore && project.BuildActionOf(readme) == FileBuildAction.Build && project.BuildActionOf(levels) == FileBuildAction.Copy);
        var ws = Workspace.Open(Path.Combine(dir, "Act.fireproj"));
        var plan = BuildPlan.Create(ws, ws.Projects[0]);
        Check("der Plan hat die ignorierte Datei nicht (sie kompilierte nicht)", plan.IsValid && plan.Sources.All(s => s.Path != legacy), string.Join("\n", plan.Errors));
        bool compiles;
        try { ProjectBuilder.Check(plan); compiles = true; } catch (Exception ex) { compiles = false; Console.WriteLine(ex.Message); }
        Check("das Projekt kompiliert ohne die ignorierte (kaputte) Datei", compiles);
        string folder = Path.Combine(dir, "bin", "vm-rel");
        var copied = ProjectContent.CopyTo(ProjectContent.ItemsOf(plan), folder);
        Check("CopyTo: die Dateien landen mit ihren Ordnern neben dem Programm", File.Exists(Path.Combine(folder, "data", "levels", "one.txt")) && File.Exists(Path.Combine(folder, "scripts", "plugin.fire")) && copied.Copied.Count == 2);
        var again = ProjectContent.CopyTo(ProjectContent.ItemsOf(plan), folder);
        Check("CopyTo: unveraenderte Dateien werden nicht noch einmal kopiert", again.Copied.Count == 0 && again.UpToDate == 2);
        File.WriteAllText(levels, "level 1, changed\n");
        File.SetLastWriteTimeUtc(levels, DateTime.UtcNow.AddSeconds(5));
        var newer = ProjectContent.CopyTo(ProjectContent.ItemsOf(plan), folder);
        Check("CopyTo (if newer): eine neuere Datei wird kopiert", newer.Copied.Count == 1 && File.ReadAllText(Path.Combine(folder, "data", "levels", "one.txt")).Contains("changed"));
        // a publish folder inside the project is no part of it
        Write(dir, "dist/.firepublish", "x");
        Write(dir, "dist/data/levels/one.txt", "copy");
        project.Refresh();
        Check("ein Publish-Ordner im Projekt gehoert nicht zu seinen Dateien", !project.ContentFiles.Any(f => f.Contains(Path.DirectorySeparatorChar + "dist" + Path.DirectorySeparatorChar)) && !project.Folders.Contains("dist"));
    }

    // ---- Publish with the virtual machine ---------------------------------------------------------------------------------------------------
    private static void PublishVm(string root)
    {
        string s = Path.Combine(root, "sln");
        Write(s, "Lib/Lib.fireproj", """{ "name": "Lib", "type": "library", "settings": { "version": "1.2.0" }, "publish": { "enabled": true, "targets": ["out/packages"] } }""");
        Write(s, "Lib/lib.fire", "namespace Lib {\n    class G { static string Hi() { return \"hi from lib\" } }\n}\n");
        Write(s, "App/App.fireproj", """
            { "name": "App", "references": [ { "project": "../Lib/Lib.fireproj" } ], "buildActions": { "data/**": "copy", "notes.txt": "copyAlways" },
              "publish": { "enabled": true, "targets": ["out/App", "out/App2"], "engine": "vm" } }
            """);
        string main = Write(s, "App/main.fire", "#import \"Lib\"\nprint(Lib.G.Hi())\nprint(new Resource(\"data/info.txt\").Text())\n");
        string info = Write(s, "App/data/info.txt", "embedded and copied");
        Write(s, "App/notes.txt", "always");
        Write(s, "Off/Off.fireproj", """{ "name": "Off" }""");
        Write(s, "Off/main.fire", "print(\"off\")\n");
        string sln = Write(s, "All.firesln", """{ "name": "All", "projects": ["App/App.fireproj", "Lib/Lib.fireproj", "Off/Off.fireproj"], "startup": "App" }""");
        string target = Path.Combine(s, "out", "App");
        Directory.CreateDirectory(target);
        string unrelated = Write(target, "someone-elses.txt", "stay");

        var ws = Workspace.Open(sln);
        var enabled = ProjectPublisher.EnabledProjects(ws);
        Check("Publish Solution nimmt nur Projekte mit publish.enabled, die Bibliothek zuerst", enabled.Select(p => p.Name).SequenceEqual(new[] { "Lib", "App" }), string.Join(", ", enabled.Select(p => p.Name)));

        var result = ProjectPublisher.Publish(ws);
        Check("Publish: beide Projekte veroeffentlicht", result.Ok && result.Published == 2, Log(result) + "\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToLogLine())));
        string program = Path.Combine(target, "App" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Check("Publish: das Programm liegt im Zielordner", File.Exists(program));
        Check("Publish: auch im zweiten Zielordner", File.Exists(Path.Combine(s, "out", "App2", Path.GetFileName(program))));
        Check("Publish: Copy-Dateien mit ihrem Ordner im Ziel", File.Exists(Path.Combine(target, "data", "info.txt")) && File.Exists(Path.Combine(target, "notes.txt")));
        Check("Publish: Copy-Dateien auch neben dem Programm im Build-Ordner", File.Exists(Path.Combine(s, "App", "bin", "vm-rel", "data", "info.txt")));
        Check("Publish: fremde Dateien im Ziel bleiben", File.Exists(unrelated));
        Check("Publish: der Ordner ist als Publish-Ordner markiert", File.Exists(Path.Combine(target, ProjectFiles.PublishMarker)));
        Check("Publish: die Bibliothek als Paket", File.Exists(Path.Combine(s, "out", "packages", "Lib-1.2.0.fpk")), string.Join(", ", Directory.Exists(Path.Combine(s, "out", "packages")) ? Directory.GetFiles(Path.Combine(s, "out", "packages")) : Array.Empty<string>()));
        Check("Publish: ein Projekt mit publishing off bekommt keinen Ordner", !Directory.Exists(Path.Combine(s, "publish", "Off")) && result.Projects.All(p => p.Project.Name != "Off"));
        Check("Publish: Zusammenfassung nennt die Ziele", result.Summary.StartsWith("2 projects published to ", StringComparison.Ordinal) && result.Summary.Contains(target), result.Summary);
        if (File.Exists(program) && !OperatingSystem.IsWindows())
        {
            var (code, output) = RunProgram(program, Path.GetTempPath());
            Check("das veroeffentlichte Programm laeuft und hat die Ressource eingebettet", code == 0 && output.Contains("hi from lib") && output.Contains("embedded and copied"), $"{code}: {output}");
        }

        // the second publish: nothing changed - nothing is built
        var time = File.GetLastWriteTimeUtc(Path.Combine(s, "App", "bin", "vm-rel", "App" + (OperatingSystem.IsWindows() ? ".exe" : "")));
        var second = ProjectPublisher.Publish(Workspace.Open(sln));
        var app = second.Projects.First(p => p.Project.Name == "App");
        Check("Publish (aktuell): nichts wird neu gebaut", second.Ok && app.UpToDate && second.Projects.First(p => p.Project.Name == "Lib").UpToDate, Log(second));
        Check("Publish (aktuell): das Programm ist dasselbe", File.GetLastWriteTimeUtc(Path.Combine(s, "App", "bin", "vm-rel", "App" + (OperatingSystem.IsWindows() ? ".exe" : ""))) == time);
        Check("Publish (aktuell): nur copyAlways wird kopiert (zwei Ziele)", app.FilesCopied == 2, $"{app.FilesCopied}");

        // a Copy file changes: copied again, no rebuild (it is not compiled)
        File.WriteAllText(Path.Combine(s, "App", "data", "extra.txt"), "new data");
        var third = ProjectPublisher.Publish(Workspace.Open(sln));
        Check("Publish: eine neue Copy-Datei wird kopiert, ohne neu zu bauen", third.Projects.First(p => p.Project.Name == "App").UpToDate && File.Exists(Path.Combine(target, "data", "extra.txt")), Log(third));

        // a source changes: built again
        File.WriteAllText(main, "#import \"Lib\"\nprint(Lib.G.Hi() + \"!\")\n");
        var fourth = ProjectPublisher.Publish(Workspace.Open(sln));
        Check("Publish: eine geaenderte Quelle baut neu", fourth.Ok && !fourth.Projects.First(p => p.Project.Name == "App").UpToDate, Log(fourth));
        // the text of an open document that is not saved counts
        var fifth = ProjectPublisher.Publish(Workspace.Open(sln), null, new PublishOptions { TextOf = p => p == main ? "#import \"Lib\"\nprint(\"unsaved\")\n" : null });
        Check("Publish: ungespeicherter Text einer offenen Datei baut neu", fifth.Ok && !fifth.Projects.First(p => p.Project.Name == "App").UpToDate, Log(fifth));
        // a setting changes: built again
        ws = Workspace.Open(sln);
        var appProject = ws.FindByName("App")!;
        appProject.Project.Publish!.Mode = "debug";
        ws.SaveProject(appProject);
        var sixth = ProjectPublisher.Publish(Workspace.Open(sln));
        Check("Publish: ein anderer Modus baut (in bin/vm-dbg)", sixth.Ok && !sixth.Projects.First(p => p.Project.Name == "App").UpToDate && File.Exists(Path.Combine(s, "App", "bin", "vm-dbg", Path.GetFileName(program))), Log(sixth));

        // disabled: asked for by itself, still not published
        ws = Workspace.Open(sln);
        var off = ProjectPublisher.Publish(ws, new[] { ws.FindByName("Off")! });
        Check("Publish eines Projekts mit publishing off: uebersprungen, nichts geschrieben", off.Projects.Single().Skipped && off.Published == 0 && !Directory.Exists(Path.Combine(s, "publish")), Log(off));
        // the default folder: publish/<name> next to the solution
        var forced = ProjectPublisher.Publish(ws, new[] { ws.FindByName("Off")! }, new PublishOptions { IncludeDisabled = true });
        Check("ohne targets: publish/<Name> neben der Mappe", forced.Ok && File.Exists(Path.Combine(s, "publish", "Off", "Off" + (OperatingSystem.IsWindows() ? ".exe" : ""))), Log(forced));

        // clean: the folder is emptied first - but never one with a project in it
        ws = Workspace.Open(sln);
        appProject = ws.FindByName("App")!;
        appProject.Project.Publish!.Clean = true;
        ws.SaveProject(appProject);
        var cleaned = ProjectPublisher.Publish(Workspace.Open(sln), null, new PublishOptions());
        Check("clean: der Zielordner wird vorher geleert", cleaned.Ok && !File.Exists(unrelated) && File.Exists(program), Log(cleaned));
        Check("clean: ein Ordner mit einem Projekt darin wird nie geleert", ProjectPublisher.CleanProblem(ws, s) != null && ProjectPublisher.CleanProblem(ws, Path.Combine(s, "App")) != null && ProjectPublisher.CleanProblem(ws, target) == null);

        // a broken program: the error, no copy
        File.WriteAllText(main, "print(undefinedThing.x(\n");
        var broken = ProjectPublisher.Publish(Workspace.Open(sln));
        Check("Publish: ein Fehler im Programm schlaegt fehl, mit Fehlermeldung", !broken.Ok && broken.Failed == 1 && broken.Diagnostics.Any(d => d.IsError), Log(broken));
    }

    // ---- Publish natively (when there is a C compiler) -------------------------------------------------------------------------------------
    private static void PublishNative(string root)
    {
        if (fire.Native.ToolchainSetup.FindProgram("gcc") == null && fire.Native.ToolchainSetup.FindProgram("cc") == null)
        {
            Console.WriteLine("(Publish nativ: kein C-Compiler - uebersprungen)");
            return;
        }
        string s = Path.Combine(root, "native");
        Write(s, "N/N.fireproj", """{ "name": "N", "buildActions": { "config.ini": "copy" }, "publish": { "enabled": true, "targets": ["../dist"] } }""");
        Write(s, "N/main.fire", "print(\"native \" + (6 * 7))\n");
        Write(s, "N/config.ini", "a=1\n");
        var ws = Workspace.Open(Path.Combine(s, "N", "N.fireproj"));
        var result = ProjectPublisher.Publish(ws);
        var n = result.Projects.SingleOrDefault();
        Check("Publish nativ (Standard): gebaut fuer diese Maschine, release", result.Ok && n is { Engine: "native", Mode: "release" }, Log(result) + "\n" + string.Join("\n", result.Diagnostics.Select(d => d.ToLogLine())));
        string program = Path.Combine(s, "dist", "N" + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Check("Publish nativ: Programm und Copy-Datei im Ziel (relativ zum Projekt ohne Mappe)", File.Exists(program) && File.Exists(Path.Combine(s, "dist", "config.ini")));
        if (File.Exists(program))
        {
            var (code, output) = RunProgram(program, s);
            Check("das nativ veroeffentlichte Programm laeuft", code == 0 && output.Contains("native 42"), $"{code}: {output}");
        }
        var again = ProjectPublisher.Publish(Workspace.Open(Path.Combine(s, "N", "N.fireproj")));
        Check("Publish nativ (aktuell): nicht neu gebaut", again.Ok && again.Projects.Single().UpToDate, Log(again));
    }

    // ---- the quick build (`--quick`, spark's hare; docs/spec/NATIVE_SPLIT.md "Schnellbuild") ------------------------------------------------------
    private static void QuickBuild(string root)
    {
        // the command line
        Check("Schnellbuild: --quick ist -O0", CommandLineParser.Parse(new[] { "build", "a.fire", "--quick" }) is { Error: null, Quick: 0 });
        Check("Schnellbuild: --quick=1 und --quick=O1 sind -O1", CommandLineParser.Parse(new[] { "build", "a.fire", "--quick=1" }) is { Error: null, Quick: 1 }
            && CommandLineParser.Parse(new[] { "publish", "App.firesln", "--quick=O1" }) is { Error: null, Quick: 1 } && CommandLineParser.Parse(new[] { "build", "a.fire", "--quick=-O0" }) is { Quick: 0 });
        Check("Schnellbuild: ohne --quick ein normaler Build", CommandLineParser.Parse(new[] { "build", "a.fire" }) is { Error: null, Quick: null });
        Check("Schnellbuild: --quick=2 und --quick bei run/native/test/bytecode sind Fehler", CommandLineParser.Parse(new[] { "build", "a.fire", "--quick=2" }).Error != null
            && new[] { "run", "native", "test", "bytecode" }.All(c => CommandLineParser.Parse(new[] { c, "a.fire", "--quick" }).Error != null));
        // what it changes: the flag and the units by toolchain, nothing for TinyCC, Open Watcom, files
        var q = NativeBuilder.QuickBuild.Apply(new fire.Native.ToolchainDef { Kind = "gcc", Compiler = "g++", Optimization = "-O2" }, 0);
        Check("Schnellbuild: gcc -O0 / -O1, msvc /Od, TinyCC und Watcom wie sie sind", q.Optimization == "-O0" && NativeBuilder.QuickBuild.Flag("clang", 1) == "-O1" && NativeBuilder.QuickBuild.Flag("msvc", 0) == "/Od"
            && NativeBuilder.QuickBuild.Flag("tcc", 0) == null && NativeBuilder.QuickBuild.Flag("watcom", 0) == null && NativeBuilder.QuickBuild.Flag("files", 0) == null, q.Optimization);
        var off = new fire.Native.NativeConfig { Split = "off", Quick = 0 };
        Check("Schnellbuild: geteilt mit gcc, clang, Emscripten (auch bei \"split\": \"off\"), eine Datei mit TinyCC und msvc",
            NativeBuilder.QuickBuild.SplitOf(off, "gcc") == fire.Native.SplitMode.On && NativeBuilder.QuickBuild.SplitOf(off, "emscripten") == fire.Native.SplitMode.On
            && NativeBuilder.QuickBuild.SplitOf(off, "tcc") == fire.Native.SplitMode.Off && NativeBuilder.QuickBuild.SplitOf(off, "msvc") == fire.Native.SplitMode.Off
            && NativeBuilder.QuickBuild.SplitOf(new fire.Native.NativeConfig { Split = "off" }, "gcc") == fire.Native.SplitMode.Off);
        Check("Schnellbuild: die Marke", NativeBuilder.QuickBuild.Label(0) == "Schnellbuild (-O0, geteilt)" && NativeBuilder.QuickBuild.Label(1, "gcc") == "Schnellbuild (-O1, geteilt)"
            && NativeBuilder.QuickBuild.Label(0, "msvc") == "Schnellbuild (/Od, eine Datei)");

        if (fire.Native.ToolchainSetup.FindProgram("gcc") == null) { Console.WriteLine("(Schnellbuild nativ: kein gcc - uebersprungen)"); return; }
        string s = Path.Combine(root, "quick");
        string? cacheBefore = Environment.GetEnvironmentVariable("FIRE_NATIVE_CACHE");
        string? programCacheBefore = Environment.GetEnvironmentVariable("FIRE_PROGRAM_CACHE");
        Environment.SetEnvironmentVariable("FIRE_NATIVE_CACHE", Path.Combine(s, "cache"));   // (the cache of programs of this test alone)
        Environment.SetEnvironmentVariable("FIRE_PROGRAM_CACHE", null);
        try
        {
            // the program cache: a quick build is its own program - not the normal one from the cache, and the other way round
            string src = Write(s, "p/main.fire", "class Box { int v }\nvar b = new Box()\nb.v = 6 * 7\nprint(\"quick \" + b.v)\n");
            var target = fire.Runtime.TargetProfile.Host;
            NativeBuildResult Build(int? quick, string exe)
            {
                var config = new fire.Native.NativeConfig { Quick = quick };
                var toolchain = config.ResolveToolchain(target, "gcc");
                return NativeBuilder.BuildSafe(new[] { File.ReadAllText(src) }, config, target, toolchain, Path.Combine(s, exe), null, null, false, Path.GetDirectoryName(src), null, null);
            }
            bool FromCache(NativeBuildResult r) => r.Log.Contains("taken from the cache", StringComparison.Ordinal);
            var normal = Build(null, "normal");
            var quick = Build(0, "quick");
            Check("Schnellbuild nativ: gebaut mit -O0, in Einheiten, die Zeile im Log", quick.Ok && quick.Log.Contains("Schnellbuild (-O0, geteilt)") && quick.Log.Contains(" -O0 ")
                && quick.Log.Contains("fire_program_1.c") && !quick.Log.Contains(" -O2 "), quick.Log);
            Check("Schnellbuild nativ: nicht das normale Programm aus dem Cache", normal.Ok && !FromCache(normal) && !FromCache(quick), quick.Log);
            if (quick.Ok)
            {
                var (code, output) = RunProgram(quick.Output, s);
                Check("Schnellbuild nativ: das Programm laeuft", code == 0 && output.Contains("quick 42"), $"{code}: {output}");
            }
            var quickAgain = Build(0, "quick2");
            var quickO1 = Build(1, "quick3");
            var normalAgain = Build(null, "normal2");
            Check("Schnellbuild nativ: der Schluessel des Programm-Caches - derselbe Schnellbuild aus dem Cache, -O1 und der normale Build je fuer sich",
                FromCache(quickAgain) && !FromCache(quickO1) && quickO1.Log.Contains(" -O1 ") && FromCache(normalAgain) && !normalAgain.Log.Contains("Schnellbuild"),
                quickAgain.Log + "\n----\n" + quickO1.Log + "\n----\n" + normalAgain.Log);

            // publish: marked, its own up-to-date state
            Write(s, "Q/Q.fireproj", """{ "name": "Q", "publish": { "enabled": true, "targets": ["../qdist"] } }""");
            Write(s, "Q/main.fire", "print(\"quick publish \" + (6 * 7))\n");
            string proj = Path.Combine(s, "Q", "Q.fireproj");
            PublishResult Publish(int? level) => ProjectPublisher.Publish(Workspace.Open(proj), null, new PublishOptions { Toolchain = "gcc", Quick = level });
            var first = Publish(null);
            var qp = Publish(0);
            string marker = Path.Combine(s, "qdist", ProjectFiles.PublishMarker);
            Check("Schnellbuild Publish: neu gebaut (nicht aktuell nach dem normalen), die Marke in der Zusammenfassung und im Ziel",
                first.Ok && qp.Ok && !qp.Projects.Single().UpToDate && qp.Summary.Contains("Schnellbuild (-O0, geteilt)") && File.Exists(marker) && File.ReadAllText(marker).Contains("Schnellbuild (-O0, geteilt)"),
                Log(qp) + "\n" + qp.Summary);
            string program = Path.Combine(s, "qdist", "Q" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (File.Exists(program))
            {
                var (code, output) = RunProgram(program, s);
                Check("Schnellbuild Publish: das Programm laeuft", code == 0 && output.Contains("quick publish 42"), $"{code}: {output}");
            }
            var qp2 = Publish(0);
            var qp1 = Publish(1);
            var back = Publish(null);
            Check("Schnellbuild Publish: derselbe Schnellbuild aktuell, -O1 und danach der normale neu gebaut, ohne Marke",
                qp2.Ok && qp2.Projects.Single().UpToDate && qp1.Ok && !qp1.Projects.Single().UpToDate && back.Ok && !back.Projects.Single().UpToDate
                && !back.Summary.Contains("Schnellbuild") && !File.ReadAllText(marker).Contains("Schnellbuild"),
                Log(qp2) + "\n" + Log(qp1) + "\n" + Log(back) + "\n" + back.Summary);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FIRE_NATIVE_CACHE", cacheBefore);
            Environment.SetEnvironmentVariable("FIRE_PROGRAM_CACHE", programCacheBefore);
        }
    }

    private static (int Code, string Output) RunProgram(string program, string workingDirectory)
    {
        var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workingDirectory };
        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60000)) { try { process.Kill(true); } catch (Exception) { } return (-1, output + " (timeout)"); }
        return (process.ExitCode, output);
    }
}
