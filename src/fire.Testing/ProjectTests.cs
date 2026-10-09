using System.Text;
using fire.Compiler;
using fire.Projects;
using fire.Runtime;
using fire.Values;

/// <summary>Projects and solutions (docs/PROJECTS.md): the model, the files, the workspace, the build plan, the settings precedence, libraries and their packing.</summary>
static class ProjectTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Projekte: {title}" : $"FEHLER: Projekte: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static string Write(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private static string RunPlan(BuildPlan plan, string? textFor = null)
    {
        var output = new StringBuilder();
        var session = RuntimeSession.Build(plan.SourceTexts, null, args => { output.AppendLine(args.Length > 0 ? args[0].ToString() : ""); return Value.MakeUndefined(); }, plan: plan);
        session.Run();
        Value.SingleFloats = false;
        return output.ToString().Replace("\r\n", "\n");
    }

    private static string Describe(Exception ex) => ex is ProjectException ? ex.Message : CompileErrors.Describe(ex);

    public static void Run()
    {
        Console.WriteLine("=== Projekte und Projektmappen ===");
        string root = Path.Combine(Path.GetTempPath(), "fire-projects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { RunIn(root); }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        Console.WriteLine(_failures == 0 ? "Alle Projekt-Pruefungen bestanden." : $"FEHLER: {_failures} Projekt-Pruefung(en) fehlgeschlagen.");
    }

    private static void RunIn(string root)
    {
        // ---- settings: the order of precedence ------------------------------------------------------------------------------------------
        var high = new ProjectSettings { Mode = "debug", Name = "P", Defines = new() { "A", "B" } };
        var low = new ProjectSettings { Mode = "release", FloatWidth = 32, Name = "S", Author = "me", Defines = new() { "B", "C" } };
        var merged = ProjectSettings.Merge(high, low);
        Check("Einstellungen: was das Projekt setzt, gilt; sonst die Mappe", merged.Mode == "debug" && merged.Name == "P" && merged.FloatWidth == 32 && merged.Author == "me");
        Check("Einstellungen: die Symbole beider Ebenen werden vereint", string.Join(",", merged.Defines!) == "A,B,C");
        Check("Einstellungen: ungueltige Werte werden gemeldet", new ProjectSettings { Mode = "fast", FloatWidth = 16, Subsystem = "x", Engine = "y" }.Validate().Count == 4);
        Check("Einstellungen: Pfade werden relativ zur Datei absolut", new ProjectSettings { Icon = "a/b.ico" }.WithAbsolutePaths(root).Icon == Path.GetFullPath("a/b.ico", root));

        // ---- the project file ------------------------------------------------------------------------------------------------------------
        var proj = new FireProject { Name = "Round", Type = OutputType.Library, Entry = null };
        proj.Settings.Mode = "performance";
        proj.References.Add(new ProjectReference { Package = "fire-http", Version = "1.0" });
        string projPath = Path.Combine(root, "round", "Round.fireproj");
        proj.Save(projPath);
        string json = File.ReadAllText(projPath);
        var back = FireProject.Load(projPath);
        Check("Projektdatei: speichern und laden", back.Name == "Round" && back.Type == OutputType.Library && back.Settings.Mode == "performance" && back.References.Count == 1 && back.References[0].Package == "fire-http");
        Check("Projektdatei: was leer ist, steht nicht in der Datei", !json.Contains("\"files\"") && !json.Contains("\"exclude\"") && !json.Contains("floatWidth") && json.Contains("\"type\": \"library\""), json);
        Check("Projektdatei: Importname aus dem Namen", new FireProject { Name = "my-lib 2" }.ImportName == "my_lib_2" && new FireProject { Name = "3d" }.ImportName == "L3d" && new FireProject { Name = "x", Import = "Y" }.ImportName == "Y");
        Check("Projektdatei: ein reservierter Importname wird gemeldet", new FireProject { Name = "net", Type = OutputType.Library }.Validate().Any(p => p.Contains("belongs to the compiler")));
        Check("Projektdatei: ungueltiges JSON ist ein ProjectException", Throws(() => FireProject.Parse("{ not json")));

        // ---- the files ---------------------------------------------------------------------------------------------------------------------
        string a = Path.Combine(root, "files");
        Write(a, "b.script", ""); Write(a, "a.script", ""); Write(a, "main.script", ""); Write(a, "sub/z.fi", ""); Write(a, "sub/deep/y.fic", ""); Write(a, "notes.md", "");
        Write(a, "bin/skip.script", ""); Write(a, "obj/skip.script", ""); Write(a, "other/o.script", ""); Write(a, "other/Other.fireproj", "{}");
        var def = new FireProject { Name = "Files", FilePath = Path.Combine(a, "Files.fireproj") };
        var defFiles = ProjectFiles.Resolve(def).Select(f => ProjectFiles.Relative(a, f)).ToList();
        Check("Dateien: ohne Angabe alle Quelldateien unterhalb, nach Namen; bin, obj und fremde Projekte fehlen", string.Join(",", defFiles) == "a.script,b.script,main.script,sub/deep/y.fic,sub/z.fi", string.Join(",", defFiles));
        def.Entry = "a.script";
        Check("Dateien: die Einsprungdatei steht zuletzt", ProjectFiles.Relative(a, ProjectFiles.Resolve(def).Last()) == "a.script");
        def.Entry = null;
        def.Files = new() { "main.script", "sub/**", "*.script" }; def.Exclude = new() { "b.script", "sub/deep/*" };
        var listed = ProjectFiles.Resolve(def).Select(f => ProjectFiles.Relative(a, f)).ToList();
        Check("Dateien: die Reihenfolge der Angaben gilt, Muster nach Namen, exclude nimmt heraus", string.Join(",", listed) == "main.script,sub/z.fi,a.script", string.Join(",", listed));
        var problems = new List<string>();
        def.Files = new() { "main.script", "nope.script" };
        ProjectFiles.Resolve(def, problems);
        Check("Dateien: eine genannte Datei, die fehlt, wird gemeldet", problems.Count == 1 && problems[0].Contains("nope.script"));
        def.Files = new() { "../shared/s.script" };
        Write(root, "shared/s.script", "");
        Check("Dateien: auch eine Datei ausserhalb des Ordners kann genannt werden", ProjectFiles.Resolve(def).Count == 1);

        // ---- the solution and the workspace ------------------------------------------------------------------------------------------------
        string s = Path.Combine(root, "sln");
        Write(s, "Core/Core.fireproj", """{ "name": "Core", "type": "library", "settings": { "version": "2.1.0.0", "author": "core team" } }""");
        Write(s, "Core/greeter.script", "namespace Core {\n    class Greeter {\n        static string Hello(string name) { return \"Hello, \" + name }\n    }\n}\n");
        Write(s, "Util/Util.fireproj", """{ "name": "Util", "type": "library", "references": [ { "project": "../Core/Core.fireproj" } ] }""");
        Write(s, "Util/util.script", "#import \"Core\"\nnamespace Util {\n    class Shout {\n        static string Loud(string t) { return Core.Greeter.Hello(t) + \"!\" }\n    }\n}\n");
        Write(s, "App/App.fireproj", """{ "name": "App", "references": [ { "project": "../Util/Util.fireproj" } ], "settings": { "floatWidth": 32, "defines": ["PRJ"] } }""");
        Write(s, "App/helper.script", "class Helper { static int Twice(int x) { return x * 2 } }\n");
        Write(s, "App/main.script", "#import \"Util\"\n#floatwidth 64\nprint(Util.Shout.Loud(\"projects\"))\nprint(Helper.Twice(21))\nprint(0.1 + 0.2)\n#if PRJ\nprint(\"PRJ\")\n#endif\n");
        Write(s, "Tool/Tool.fireproj", """{ "name": "Tool" }""");
        Write(s, "Tool/tool.script", "print(\"tool\")\n");
        string slnPath = Write(s, "All.firesln", """{ "name": "All", "projects": ["Core/Core.fireproj", "Util/Util.fireproj", "App/App.fireproj", "Tool/Tool.fireproj"], "startup": "App", "settings": { "author": "everyone", "mode": "release" } }""");
        var ws = Workspace.Open(slnPath);
        Check("Mappe: oeffnen laedt die Projekte in der Reihenfolge der Datei", ws.Solution != null && string.Join(",", ws.Projects.Select(p => p.Name)) == "Core,Util,App,Tool" && ws.Name == "All");
        Check("Mappe: das Startprojekt ist das genannte", ws.Startup?.Name == "App");
        Check("Mappe: ohne startup das erste Programm", Workspace.Open(Write(s, "NoStartup.firesln", """{ "projects": ["Core/Core.fireproj", "Tool/Tool.fireproj"] }""")).Startup?.Name == "Tool");
        var app = ws.FindByName("App")!;
        Check("Zuordnung: eine Datei gehoert zu dem Projekt, das sie enthaelt", ws.FindProjectOf(Path.Combine(s, "App", "helper.script"))?.Name == "App" && ws.FindProjectOf(Path.Combine(s, "Core", "greeter.script"))?.Name == "Core");
        Check("Zuordnung: eine Datei ausserhalb aller Projekte gehoert zu keinem", ws.FindProjectOf(Path.Combine(root, "files", "a.script")) == null && ws.FindProjectOf("") == null);
        Check("Zuordnung: andere Dateitypen und Dateien in bin gehoeren nicht dazu", ws.FindProjectOf(Path.Combine(s, "App", "notes.md")) == null);
        var single = Workspace.Open(Path.Combine(s, "Tool", "Tool.fireproj"));
        Check("Einzelnes Projekt: ohne Mappe geoeffnet, Zuordnung funktioniert", single.Solution == null && single.Projects.Count == 1 && single.FindProjectOf(Path.Combine(s, "Tool", "tool.script"))?.Name == "Tool" && single.Name == "Tool");

        // ---- the plan ---------------------------------------------------------------------------------------------------------------------
        var plan = BuildPlan.Create(ws, app);
        Check("Plan: Dateien, Bibliotheken (auch die der Bibliothek) und Einstellungen", plan.IsValid && plan.Sources.Count == 2 && plan.Libraries.ContainsKey("Util") && plan.Libraries.ContainsKey("Core") && plan.Libraries["Util"].Requires.Contains("Core"), string.Join("; ", plan.Errors));
        Check("Plan: die Einstellungen des Projekts vor denen der Mappe", plan.Settings.FloatWidth == 32 && plan.Settings.Author == "everyone" && plan.Settings.Mode == "release" && plan.Settings.Defines!.Contains("PRJ"));
        Check("Plan: die Reihenfolge der Bibliotheken - was andere brauchen zuerst", string.Join(",", plan.LibraryOrder(new[] { "Util" }).Select(l => l.ImportName)) == "Core,Util");
        var unsaved = BuildPlan.Create(ws, app, path => path.EndsWith("helper.script") ? "class Helper { static int Twice(int x) { return x * 3 } }\n" : null);
        Check("Plan: der Text eines ungespeicherten Dokuments gilt", unsaved.Sources.Any(f => f.Text.Contains("x * 3")));

        // ---- compile and run ---------------------------------------------------------------------------------------------------------------
        string ran = RunPlan(plan);
        Check("Lauf: Projekt mit zwei Dateien und zwei Bibliotheken; Einstellungen des Projekts vor den Tags im Quelltext", ran == "Hello, projects!\n42\n0.3\nPRJ\n", ran);
        var tagsOnly = BuildPlan.Create(ws, ws.FindByName("Tool")!);
        Check("Lauf: ein Projekt ohne Einstellungen folgt den Tags", RunPlan(tagsOnly) == "tool\n");
        var info = Linker.ExtractAssemblyInfo(new[] { "#debug\n#name \"FromTag\"\n#author \"tag\"\n" }, null, new ProjectSettings { Name = "FromProject", Mode = "performance" });
        Check("Einstellungen: im Dialog gilt, was das Projekt festlegt, sonst das Tag", info.ProductName == "FromProject" && info.ExecutionMode == VmExecutionMode.Performance && info.CompanyName == "tag");
        var noTagLib = BuildPlan.Create(ws, ws.FindByName("Util")!);
        Check("Bibliothek: sie uebersetzt, wenn sie nur Deklarationen hat", Ok(() => ProjectBuilder.Check(noTagLib)), "");

        // ---- errors -------------------------------------------------------------------------------------------------------------------------
        Write(s, "Bad/Bad.fireproj", """{ "name": "Bad", "type": "library" }""");
        Write(s, "Bad/bad.script", "class A { }\nprint(\"top level\")\nvar x = 1\n");
        var bad = Workspace.Open(Write(s, "Bad.firesln", """{ "projects": ["Bad/Bad.fireproj"] }"""));
        string badMessage = Catch(() => ProjectBuilder.Check(BuildPlan.Create(bad, bad.Projects[0])));
        Check("Bibliothek: Anweisungen auf oberster Ebene sind ein Fehler mit Datei und Zeile", badMessage.Contains("bad.script:2") && badMessage.Contains("bad.script:3") && badMessage.Contains("no entry point"), badMessage);

        Write(s, "Syntax/Syntax.fireproj", """{ "name": "Syntax" }""");
        Write(s, "Syntax/a.script", "print(1)\n");
        Write(s, "Syntax/b.script", "class Broken {\n");
        var syn = Workspace.Open(Path.Combine(s, "Syntax", "Syntax.fireproj"));
        string synMessage = Catch(() => ProjectBuilder.Check(BuildPlan.Create(syn, syn.Projects[0])));
        Check("Fehler: ein Syntaxfehler nennt die Datei", synMessage.Contains("b.script"), synMessage);

        Write(s, "Prog/Prog.fireproj", """{ "name": "Prog" }""");
        Write(s, "Prog/p.script", "print(1)\n");
        Write(s, "Uses/Uses.fireproj", """{ "name": "Uses", "references": [ { "project": "../Prog/Prog.fireproj" } ] }""");
        Write(s, "Uses/u.script", "print(2)\n");
        var uses = Workspace.Open(Path.Combine(s, "Uses", "Uses.fireproj"));
        Check("Verweis: ein Programm kann nicht referenziert werden", BuildPlan.Create(uses, uses.Projects[0]).Errors.Any(e => e.Contains("is a program")));
        Write(s, "CycA/CycA.fireproj", """{ "name": "CycA", "type": "library", "references": [ { "project": "../CycB/CycB.fireproj" } ] }""");
        Write(s, "CycA/a.script", "class CA { }\n");
        Write(s, "CycB/CycB.fireproj", """{ "name": "CycB", "type": "library", "references": [ { "project": "../CycA/CycA.fireproj" } ] }""");
        Write(s, "CycB/b.script", "class CB { }\n");
        var cyc = Workspace.Open(Path.Combine(s, "CycA", "CycA.fireproj"));
        Check("Verweis: ein Kreis wird erkannt", BuildPlan.Create(cyc, cyc.Projects[0]).Errors.Any(e => e.Contains("in a circle")));
        Write(s, "Gone/Gone.fireproj", """{ "name": "Gone", "references": [ { "project": "../Missing/Missing.fireproj" } ] }""");
        Write(s, "Gone/g.script", "print(1)\n");
        var gone = Workspace.Open(Path.Combine(s, "Gone", "Gone.fireproj"));
        Check("Verweis: ein fehlendes Projekt wird gemeldet", BuildPlan.Create(gone, gone.Projects[0]).Errors.Any(e => e.Contains("does not exist")));
        Write(s, "Pkg/Pkg.fireproj", """{ "name": "Pkg", "references": [ { "package": "no-such-package-xyz" } ] }""");
        Write(s, "Pkg/p.script", "print(1)\n");
        var pkg = Workspace.Open(Path.Combine(s, "Pkg", "Pkg.fireproj"));
        string pkgMessage = Catch(() => ProjectBuilder.Check(BuildPlan.Create(pkg, pkg.Projects[0])));
        Check("Verweis: ein Paket, das nicht installiert ist, wird gemeldet", pkgMessage.Contains("no-such-package-xyz") && pkgMessage.Contains("ember install"), pkgMessage);
        Write(s, "NoRef/NoRef.fireproj", """{ "name": "NoRef" }""");
        Write(s, "NoRef/n.script", "#import \"Core\"\nprint(Core.Greeter.Hello(\"x\"))\n");
        var noref = Workspace.Open(Path.Combine(s, "NoRef", "NoRef.fireproj"));
        Check("Import: ohne Verweis ist eine Bibliothek des Projekts unbekannt", Catch(() => ProjectBuilder.Check(BuildPlan.Create(noref, noref.Projects[0]))).Contains("not a known extension"));

        // ---- changes through the workspace --------------------------------------------------------------------------------------------------
        string c = Path.Combine(root, "changes");
        var created = Workspace.CreateSolution(Path.Combine(c, "New.firesln"));
        var lib = created.CreateProject(Path.Combine(c, "Lib", "Lib.fireproj"), OutputType.Library);
        var prog = created.CreateProject(Path.Combine(c, "Prog", "Prog.fireproj"), OutputType.Exe);
        Check("Aenderungen: neue Projekte stehen in der Mappe und auf der Platte", created.Projects.Count == 2 && File.Exists(Path.Combine(c, "Lib", "lib.script")) && File.Exists(Path.Combine(c, "Prog", "main.script")) && File.ReadAllText(Path.Combine(c, "New.firesln")).Contains("Lib/Lib.fireproj"));
        created.AddReference(prog, lib);
        created.SetStartup(prog);
        var reopened = Workspace.Open(Path.Combine(c, "New.firesln"));
        Check("Aenderungen: Verweis und Startprojekt sind gespeichert", reopened.Startup?.Name == "Prog" && reopened.FindByName("Prog")!.Project.References.Count == 1);
        Check("Aenderungen: die neue Bibliothek und das neue Programm laufen zusammen", Ok(() => ProjectBuilder.Check(BuildPlan.Create(reopened, reopened.FindByName("Prog")!))));
        string extra = Write(root, "elsewhere/extra.script", "class Extra { }\n");
        created.AddFile(prog, extra);
        Check("Aenderungen: eine Datei ausserhalb wird namentlich aufgenommen, die Muster bleiben", prog.Contains(extra) && prog.Contains(Path.Combine(c, "Prog", "main.script")));
        created.RemoveFile(prog, Path.Combine(c, "Prog", "main.script"));
        Check("Aenderungen: eine Datei, die ein Muster nimmt, wird ausgeschlossen", !prog.Contains(Path.Combine(c, "Prog", "main.script")) && prog.Project.Exclude.Count == 1 && File.Exists(Path.Combine(c, "Prog", "main.script")));
        created.RemoveProject(lib);
        Check("Aenderungen: ein Projekt aus der Mappe nehmen (die Dateien bleiben)", created.Projects.Count == 1 && File.Exists(Path.Combine(c, "Lib", "Lib.fireproj")) && !File.ReadAllText(Path.Combine(c, "New.firesln")).Contains("Lib.fireproj"));
        int changed = 0; created.Changed += () => changed++;
        created.SetStartup(prog);
        Check("Aenderungen: Changed wird ausgeloest", changed == 1);

        // ---- packing a library -----------------------------------------------------------------------------------------------------------------
        string outDir = Path.Combine(root, "packed");
        string fpk = ProjectBuilder.PackLibrary(BuildPlan.Create(ws, ws.FindByName("Util")!), outDir);
        var manifest = fire.Package.Manager.Fpk.ReadManifest(fpk);
        Check("Pack: eine Bibliothek wird ein Paket mit Importname, Version und Abhaengigkeiten", Path.GetFileName(fpk) == "Util-1.0.0.fpk" && manifest.Name == "Util" && manifest.Imports[0].Name == "Util" && manifest.Dependencies.Contains("Core") && manifest.Imports[0].Requires.Contains("Core"), fpk);
        string corePack = ProjectBuilder.PackLibrary(BuildPlan.Create(ws, ws.FindByName("Core")!), outDir);
        var coreManifest = fire.Package.Manager.Fpk.ReadManifest(corePack);
        Check("Pack: Version und Autor kommen aus den Einstellungen des Projekts (vor denen der Mappe)", Path.GetFileName(corePack) == "Core-2.1.0.fpk" && coreManifest.Version == "2.1.0" && coreManifest.Author == "core team", corePack + " " + coreManifest.Author);
        Check("Pack: ein Programm kann nicht gepackt werden", Throws(() => ProjectBuilder.PackLibrary(plan, outDir)));
        Check("Pack: eine Bibliothek mit Anweisungen oben wird nicht gepackt", Throws(() => ProjectBuilder.PackLibrary(BuildPlan.Create(bad, bad.Projects[0]), outDir)));
        Write(s, "Inc/Inc.fireproj", """{ "name": "Inc", "type": "library", "files": ["inc.script"] }""");
        Write(s, "Inc/inc.script", "#include \"part.script\"\nclass Whole { static int Two() { return Part.One() + 1 } }\n");
        Write(s, "Inc/part.script", "class Part { static int One() { return 1 } }\n");
        var inc = Workspace.Open(Path.Combine(s, "Inc", "Inc.fireproj"));
        string incPack = ProjectBuilder.PackLibrary(BuildPlan.Create(inc, inc.Projects[0]), outDir);
        string extracted = Path.Combine(root, "extracted");
        fire.Package.Manager.Fpk.Extract(incPack, extracted);
        string prelude = Directory.EnumerateFiles(extracted, "*.fire", SearchOption.AllDirectories).First();
        string preludeText = File.ReadAllText(prelude);
        Check("Pack: ein #include wird beim Packen eingesetzt", !preludeText.Contains("#include") && preludeText.Contains("class Part") && preludeText.Contains("class Whole"), preludeText);
        Check("Pack: der Befehl der Befehlszeile baut Programm und Bibliothek", CommandLine(slnPath, outDir));
        TemplatesAndMarkup(root);
    }

    private static void TemplatesAndMarkup(string root)
    {
        string m0;
        // ---- versions of packages ---------------------------------------------------------------------------------------------------------
        Check("Version: der naechste Patch, Minor und Major", ProjectBuilder.BumpVersion("1.2.3") == "1.2.4" && ProjectBuilder.BumpVersion("1.2.3", "minor") == "1.3.0" && ProjectBuilder.BumpVersion("1.2.3", "major") == "2.0.0" && ProjectBuilder.BumpVersion(null) == "1.0.1" && ProjectBuilder.BumpVersion("2.5.0.0") == "2.5.1");
        Check("Version: ein Paket hat drei Zahlen", ProjectBuilder.IsPackageVersion("1.0.0") && !ProjectBuilder.IsPackageVersion("1.0") && !ProjectBuilder.IsPackageVersion("1.x.0") && !ProjectBuilder.IsPackageVersion(""));
        string licDir = Path.Combine(root, "lic");
        Write(licDir, "Lic/Lic.fireproj", """{ "name": "Lic", "type": "library", "settings": { "version": "3.1.4", "license": "MIT", "description": "d", "author": "a" } }""");
        Write(licDir, "Lic/lic.script", "namespace Lic { class A { static int One() { return 1 } } }\n");
        var lic = Workspace.Open(Path.Combine(licDir, "Lic", "Lic.fireproj"));
        var licPlan = BuildPlan.Create(lic, lic.Projects[0]);
        string licPack = ProjectBuilder.PackLibrary(licPlan, Path.Combine(licDir, "out"));
        Check("Pack: der Dateiname kommt aus Name und Version", licPack == ProjectBuilder.PackagePathFor(licPlan, Path.Combine(licDir, "out")) && Path.GetFileName(licPack) == "Lic-3.1.4.fpk", licPack);
        var licManifest = fire.Package.Manager.Fpk.ReadManifest(licPack);
        Check("Pack: Lizenz, Beschreibung, Autor und Version stehen im Manifest", licManifest.License == "MIT" && licManifest.Description == "d" && licManifest.Author == "a" && licManifest.Version == "3.1.4", licManifest.License);

        // ---- templates: a solution of its own folder, the project in a folder of its own ------------------------------------------------
        string t = Path.Combine(root, "templates");
        // the templates that ship with fire (the Templates folder next to the program), without the user's own and without installed packages
        var catalog = TemplateCatalog.Load(userRoot: Path.Combine(root, "no-user-templates"), store: new fire.Package.Manager.PackageStore(Path.Combine(root, "no-packages")));
        FireTemplate Tpl(TemplateScope scope, string title) => catalog.Find(scope, title) ?? throw new InvalidOperationException("no template " + title);
        Check("Vorlagen: Namen werden geprueft", ProjectTemplates.CheckName("Ok Name") == null && ProjectTemplates.CheckName("") != null && ProjectTemplates.CheckName("a/b") != null && ProjectTemplates.CheckName(".x") != null);
        Check("Vorlagen: der vorgeschlagene Ordner ist $HOME/spark/{Name}", ProjectTemplates.DefaultSolutionFolder("Demo") == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "spark", "Demo"));
        Check("Vorlagen: fuer die Mappe mit 'Empty', fuer ein Projekt ohne", catalog.Projects().Count() == 5 && catalog.Projects().First().Empty && catalog.Projects(includeEmpty: false).Count() == 4 && catalog.Projects().All(x => x.Source == "Local"), string.Join(",", catalog.Projects().Select(x => x.Display)));

        var empty = Workspace.CreateSolution(Tpl(TemplateScope.Project, "Empty"), Path.Combine(t, "E"), "E");
        Check("Vorlagen: leer = eine Mappe ohne Projekt", empty.Solution != null && empty.Projects.Count == 0 && File.Exists(Path.Combine(t, "E", "E.firesln")) && !Directory.Exists(Path.Combine(t, "E", "E")));

        var terminal = Workspace.CreateSolution(Tpl(TemplateScope.Project, "Terminal"), Path.Combine(t, "T"), "T");
        Check("Vorlagen: Terminal = Mappe im eigenen Ordner, das Projekt im Unterordner gleichen Namens",
            File.Exists(Path.Combine(t, "T", "T.firesln")) && File.Exists(Path.Combine(t, "T", "T", "T.fireproj")) && File.Exists(Path.Combine(t, "T", "T", "main.script")) && terminal.Projects.Count == 1 && terminal.Projects[0].Name == "T");
        Check("Vorlagen: Terminal gibt Hello, World! aus", RunPlan(BuildPlan.Create(terminal, terminal.Projects[0])).Trim() == "Hello, World!");

        var desktop = Workspace.CreateSolution(Tpl(TemplateScope.Project, "Desktop"), Path.Combine(t, "D"), "D");
        var desktopPlan = BuildPlan.Create(desktop, desktop.Projects[0]);
        Check("Vorlagen: Desktop ist ein Programm ohne Konsole und uebersetzt", desktop.Projects[0].Project.Settings.Subsystem == "gui" && desktopPlan.IsValid && Ok(() => ProjectBuilder.Check(desktopPlan)), string.Join("\n", desktopPlan.Errors) + Catch(() => ProjectBuilder.Check(desktopPlan)));

        var library = Workspace.CreateSolution(Tpl(TemplateScope.Project, "Library"), Path.Combine(t, "L"), "L");
        var libraryPlan = BuildPlan.Create(library, library.Projects[0]);
        Check("Vorlagen: Library ist eine Bibliothek ohne Einsprung und uebersetzt", library.Projects[0].Project.Type == OutputType.Library && libraryPlan.IsValid && Ok(() => ProjectBuilder.Check(libraryPlan)), Catch(() => ProjectBuilder.Check(libraryPlan)));

        var nativeLib = Workspace.CreateSolution(Tpl(TemplateScope.Project, "Native Library"), Path.Combine(t, "N"), "N");
        Check("Vorlagen: Native Library legt das C++ in den Ordner native/", nativeLib.Projects[0].Project.Native != null && File.Exists(Path.Combine(t, "N", "N", "native", "n.hpp")) && File.ReadAllText(Path.Combine(t, "N", "N", "N.fireproj")).Contains("\"native\""));

        // natives: the functions are found in the C++, an application uses them through the library, in the virtual machine (a shared library is built from the C++ now)
        string nativeApp = Path.Combine(t, "N");
        var nativeSolution = Workspace.Open(Path.Combine(nativeApp, "N.firesln"));
        string header = Path.Combine(nativeApp, "N", "native", "n.hpp");
        File.AppendAllText(header, "\nnamespace fire {\ninline Value n_twice(Value a) { return Int(a.i * 2); }\ninline Value n_text(OwnList* list) { Str* s = allocStr(3, list); strChars(s)[0] = 'a'; strChars(s)[1] = 'b'; strChars(s)[2] = 'c'; return StrV(s); }\n}\n");
        File.WriteAllText(Path.Combine(nativeApp, "N", "n.script"), "namespace N {\n    class Native {\n        static int Add(int a, int b) { return __n_add(a, b) }\n        static int Twice(int a) { return __n_twice(a) }\n        static string Text() { return __n_text() }\n    }\n}\n");
        var found = ProjectNatives.Resolve(nativeSolution.Projects[0])!;
        Check("Natives: die Funktionen stehen im C++ und werden gefunden", found.Native.Functions.Select(f => f.Name).OrderBy(n => n).SequenceEqual(new[] { "__n_add", "__n_text", "__n_twice" }) && found.Native.Functions.First(f => f.Name == "__n_text").NeedsList && found.Native.Functions.First(f => f.Name == "__n_add").Arguments == 2, string.Join(",", found.Native.Functions.Select(f => f.Name + "/" + f.Arguments)));
        Check("Natives: auch in Kommentaren steht nichts", ProjectNatives.Discover("// inline Value nope(Value a) { return a; }\n/* inline Value nor(Value a) */\ninline Value yes(Value a) { return a; }\ninline int helper(int x) { return x; }\n").Select(f => f.Cpp).SequenceEqual(new[] { "yes" }));

        var appDir = Path.Combine(t, "N", "App");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(Path.Combine(appDir, "App.fireproj"), """{ "name": "App", "references": [ { "project": "../N/N.fireproj" } ] }""");
        File.WriteAllText(Path.Combine(appDir, "main.script"), "#import \"N\"\nprint(N.Native.Add(2, 3))\nprint(N.Native.Twice(21))\nprint(N.Native.Text())\n");
        nativeSolution.AddProject(Path.Combine(appDir, "App.fireproj"));
        var nativeAppPlan = BuildPlan.Create(nativeSolution, nativeSolution.FindByName("App")!);
        Check("Natives: der Plan kennt die Natives der Bibliothek", nativeAppPlan.IsValid && nativeAppPlan.NativeParts.Count() == 1, string.Join("\n", nativeAppPlan.Errors));
        string nativeOut = "";
        string nativeErr = Catch(() => nativeOut = RunPlan(nativeAppPlan));
        Check("Natives: das Programm ruft das C++ der Bibliothek in der VM", nativeOut.Trim() == "5\n42\nabc", nativeErr + nativeOut);
        // the same program as a native build: the C++ of the library goes into the generated file
        string nativeExe = Path.Combine(t, "N", "app-native");
        var nativeBuildOut = new StringWriter(); var nativeBuildErr = new StringWriter();
        int nativeBuildCode = CommandLineRunner.Run(new[] { "build", Path.Combine(nativeApp, "N.firesln"), "-p", "App", "--engine", "native", "-o", nativeExe }, nativeBuildOut, nativeBuildErr);
        string nativeRun = "";
        if (nativeBuildCode == 0 && File.Exists(nativeExe))
        {
            var psi = new System.Diagnostics.ProcessStartInfo(nativeExe) { RedirectStandardOutput = true, UseShellExecute = false };
            using var proc = System.Diagnostics.Process.Start(psi)!;
            nativeRun = proc.StandardOutput.ReadToEnd().Replace("\r\n", "\n"); proc.WaitForExit();
        }
        Check("Natives: ein nativer Build uebernimmt das C++ der Bibliothek", nativeRun.Trim() == "5\n42\nabc", nativeBuildErr.ToString() + nativeBuildOut + nativeRun);
        var nativeLibPlan = BuildPlan.Create(nativeSolution, nativeSolution.FindByName("N")!);
        Check("Natives: die Bibliothek selbst uebersetzt (die Natives sind bekannt)", Ok(() => ProjectBuilder.Check(nativeLibPlan)), Catch(() => ProjectBuilder.Check(nativeLibPlan)));
        string nativePack = ProjectBuilder.PackLibrary(nativeLibPlan, Path.Combine(t, "N", "out"));
        var nativeManifest = fire.Package.Manager.Fpk.ReadManifest(nativePack);
        var nativeImport = nativeManifest.Imports[0];
        Check("Natives: das Paket enthaelt das C++ und die Funktionen", nativeImport.Native != null && nativeImport.Native.Functions.Count == 3 && nativeImport.Native.Sources.Count == 1 && nativeImport.Prelude != null, nativeImport.Native?.Sources.Count.ToString());
        var noSources = new FireProject { Name = "Empty", Type = OutputType.Library, Native = new ProjectNative(), FilePath = Path.Combine(t, "Empty", "Empty.fireproj") };
        Directory.CreateDirectory(Path.Combine(t, "Empty")); noSources.Save();
        var emptyWs = Workspace.Open(noSources.FilePath!);
        Check("Natives: ohne C++-Dateien meldet der Plan es", BuildPlan.Create(emptyWs, emptyWs.Projects[0]).Errors.Any(e => e.Contains("no C++ files")));

        // a project made in a folder of the solution lives in a subfolder of it; the folder is kept in the solution file
        var inFolder = Workspace.CreateSolution(Tpl(TemplateScope.Project, "Terminal"), Path.Combine(t, "F"), "F");
        string libsFolder = inFolder.AddFolder(Path.Combine(t, "F", "libs"));
        var made = inFolder.CreateProject(Tpl(TemplateScope.Project, "Library"), libsFolder, "Util");
        Check("Vorlagen: ein Projekt in einem Ordner der Mappe liegt in dessen Unterordner", made.FilePath == Path.Combine(t, "F", "libs", "Util", "Util.fireproj") && inFolder.Projects.Count == 2 && File.Exists(made.FilePath));
        var reopened = Workspace.Open(Path.Combine(t, "F", "F.firesln"));
        Check("Vorlagen: Ordner und Projekte stehen in der Mappendatei", reopened.Solution!.Folders.Count == 1 && reopened.Solution.Folders[0] == "libs" && reopened.Projects.Count == 2 && reopened.FindByName("Util") != null);
        Check("Vorlagen: ein Ordner ausserhalb der Mappe wird abgelehnt", Throws(() => inFolder.AddFolder(Path.Combine(t, "elsewhere"))));
        Check("Vorlagen: ein vorhandenes Projekt wird nicht ueberschrieben", Throws(() => TemplateInstaller.CreateProject(Tpl(TemplateScope.Project, "Terminal"), Path.Combine(t, "T"), "T")));
        var standalone = new Workspace();
        string alone = TemplateInstaller.CreateProject(Tpl(TemplateScope.Project, "Terminal"), Path.Combine(t, "alone"), "Solo");
        standalone.Load(alone);
        Check("Vorlagen: ein Projekt ohne Mappe bekommt ebenfalls seinen Ordner", alone == Path.Combine(t, "alone", "Solo", "Solo.fireproj") && standalone.Solution == null && standalone.Projects.Count == 1);

        // ---- templates as folders (docs/TEMPLATES.md): code templates, placeholders in names and text, the user's folder, packages ---------------------------
        var codeTitles = catalog.Code.Select(x => x.Title).ToList();
        Check("Vorlagen: die eingebauten Code-Vorlagen", codeTitles.SequenceEqual(new[] { "Script", "Fire Class", "FXML Window", "FXML View" }) && catalog.Code.All(x => x.Source == "Local" && x.Description != null && x.Scope == TemplateScope.Code), string.Join(",", codeTitles));
        Check("Vorlagen: die Erweiterung kommt aus der Datei mit $name$ im Namen", Tpl(TemplateScope.Code, "Script").Extension == ".script" && Tpl(TemplateScope.Code, "Fire Class").Extension == ".script" && Tpl(TemplateScope.Code, "FXML Window").Extension == ".fxml" && Tpl(TemplateScope.Code, "FXML View").Extension == ".fxml");
        Check("Vorlagen: ein vorgeschlagener Name", Tpl(TemplateScope.Code, "Fire Class").DefaultName == "MyClass" && Tpl(TemplateScope.Code, "FXML Window").DefaultName == "MainWindow");

        string codeDir = Path.Combine(t, "code");
        var made1 = TemplateInstaller.Instantiate(Tpl(TemplateScope.Code, "Fire Class"), codeDir, new TemplateValues("Greeter", "Demo"));
        Check("Vorlagen: eine Klasse folgt dem Dateinamen", made1.Count == 1 && Path.GetFileName(made1[0]) == "Greeter.script" && File.ReadAllText(made1[0]).Contains("class Greeter {"), made1.Count > 0 ? File.ReadAllText(made1[0]) : "");
        var made2 = TemplateInstaller.Instantiate(Tpl(TemplateScope.Code, "Fire Class"), codeDir, new TemplateValues("my-thing"));
        Check("Vorlagen: ein Name wird fuer die Klasse zum Namen mit Grossbuchstaben", File.ReadAllText(made2[0]).Contains("class My_thing {") && Path.GetFileName(made2[0]) == "my-thing.script");
        Check("Vorlagen: eine vorhandene Datei wird nicht ueberschrieben", Throws(() => TemplateInstaller.Instantiate(Tpl(TemplateScope.Code, "Fire Class"), codeDir, new TemplateValues("Greeter"))));
        Check("Vorlagen: ein Name, der den Ordner verlaesst, wird abgelehnt", Throws(() => TemplateInstaller.Instantiate(Tpl(TemplateScope.Code, "Script"), codeDir, new TemplateValues("../escape"))) && !File.Exists(Path.Combine(t, "escape.script")));

        // the markup windows and views compile inside a project
        var winProject = terminal.Projects[0];
        var winFiles = TemplateInstaller.Instantiate(Tpl(TemplateScope.Code, "FXML Window"), winProject.Directory, new TemplateValues("Main", winProject.Name));
        var viewFiles = TemplateInstaller.Instantiate(Tpl(TemplateScope.Code, "FXML View"), winProject.Directory, new TemplateValues("SettingsView", winProject.Name));
        terminal.AddFile(winProject, winFiles[0]);
        terminal.AddFile(winProject, viewFiles[0]);
        var winPlan = BuildPlan.Create(terminal, terminal.Projects[0]);
        Check("Vorlagen: ein FXML-Fenster und eine FXML-View uebersetzen im Projekt", winPlan.IsValid && Ok(() => ProjectBuilder.Check(winPlan)) && File.ReadAllText(winFiles[0]).Contains("<Window class=\"Main\"") && File.ReadAllText(viewFiles[0]).Contains("<View class=\"SettingsView\""), string.Join("\n", winPlan.Errors) + Catch(() => ProjectBuilder.Check(winPlan)));

        // the user's folder: with and without template.json, placeholders in paths (in both spellings) and in the text, binary files as they are
        string userRoot = Path.Combine(t, "user-templates");
        Write(userRoot, "Code/Plain Thing/$name$.txt", "hello");                                                   // no template.json
        Write(userRoot, "Code/Many/template.json", """{ "title": "Many Files", "description": "Two files in a folder", "icon": "class", "defaultName": "Thing", "extension": ".mine", "order": 5, "open": [ "$name$/main.txt" ] }""");
        Write(userRoot, "Code/Many/$name$/main.txt", "name=$name$ ident=$ident$ class=$class$ lower=$identlower$ project=$project$ pi=$projectident$ unknown=$unknown$ init=__init__ again=__name__");
        Write(userRoot, "Code/Many/__name___notes.txt", "second");
        File.WriteAllBytes(Path.Combine(userRoot, "Code", "Many", "$name$", "blob.bin"), new byte[] { 0, 1, 2, 36, 110, 97, 109, 101, 36, 255 });   // $name$ inside, but binary
        Write(userRoot, "Code/Hidden/template.json", """{ "title": "Nope", "hidden": true }""");
        Write(userRoot, "Code/Hidden/$name$.txt", "x");
        Write(userRoot, "Project/Bare/main.script", "print(\"$name$\")\n");                                       // a project template without a project file
        Write(userRoot, "Project/Lib/template.json", """{ "type": "library" }""");
        Write(userRoot, "Project/Lib/lib.script", "namespace $ident$ { class A { } }\n");
        var userCatalog = TemplateCatalog.Load(userRoot: userRoot, store: new fire.Package.Manager.PackageStore(Path.Combine(root, "no-packages")));
        var plain = userCatalog.Find(TemplateScope.Code, "Plain Thing")!;
        Check("Vorlagen: ohne template.json gilt der Ordnername, ein Standardsymbol, kein Text", plain.Description == null && plain.IconPath == null && plain.IconKey == "file" && plain.Extension == ".txt" && plain.DefaultName == "PlainThing" && plain.Source == "Local");
        var many = userCatalog.Find(TemplateScope.Code, "Many Files")!;
        Check("Vorlagen: template.json gibt Titel, Beschreibung, Symbol, Name und Erweiterung", many.Description == "Two files in a folder" && many.IconKey == "class" && many.DefaultName == "Thing" && many.Extension == ".mine" && many.Order == 5 && many.Files.Count == 3 && !many.Files.Contains("template.json"), string.Join(",", many.Files));
        Check("Vorlagen: ein verborgenes Template erscheint nicht, die eingebauten bleiben daneben", userCatalog.Find(TemplateScope.Code, "Nope") == null && userCatalog.Find(TemplateScope.Code, "Fire Class") != null && userCatalog.Code.Count() == codeTitles.Count + 2);
        string manyDir = Path.Combine(t, "many");
        var manyValues = new TemplateValues("Big Name", "My-Proj");
        var manyMade = TemplateInstaller.Instantiate(many, manyDir, manyValues);
        string mainTxt = Path.Combine(manyDir, "Big Name", "main.txt");
        Check("Vorlagen: $name$ und __name__ im Pfad, Platzhalter im Text, Unbekanntes bleibt", manyMade.Count == 3 && File.Exists(mainTxt) && File.Exists(Path.Combine(manyDir, "Big Name_notes.txt")) &&
            File.ReadAllText(mainTxt) == "name=Big Name ident=Big_Name class=Big_Name lower=big_name project=My-Proj pi=My_Proj unknown=$unknown$ init=__init__ again=Big Name", File.ReadAllText(mainTxt));
        Check("Vorlagen: eine Binaerdatei wird unveraendert kopiert", File.ReadAllBytes(Path.Combine(manyDir, "Big Name", "blob.bin")).SequenceEqual(new byte[] { 0, 1, 2, 36, 110, 97, 109, 101, 36, 255 }));
        Check("Vorlagen: die Dateien, die danach geoeffnet werden", TemplateInstaller.FilesToOpen(many, manyDir, manyValues, manyMade).SequenceEqual(new[] { mainTxt }) && TemplateInstaller.FilesToOpen(plain, manyDir, manyValues, new[] { "x" }).SequenceEqual(new[] { "x" }));
        string barePath = TemplateInstaller.CreateProject(userCatalog.Find(TemplateScope.Project, "Bare")!, Path.Combine(t, "bare"), "Bare1");
        var bare = FireProject.Load(barePath);
        Check("Vorlagen: ohne Projektdatei im Template wird eine gemacht", barePath == Path.Combine(t, "bare", "Bare1", "Bare1.fireproj") && bare.Name == "Bare1" && bare.Type == OutputType.Exe && File.ReadAllText(Path.Combine(t, "bare", "Bare1", "main.script")).Contains("print(\"Bare1\")"));
        var libPath = TemplateInstaller.CreateProject(userCatalog.Find(TemplateScope.Project, "Lib")!, Path.Combine(t, "bare"), "Lib1");
        Check("Vorlagen: \"type\": \"library\" macht eine Bibliothek", FireProject.Load(libPath).Type == OutputType.Library);

        // folders `Templates/Package/Name_1.2.3.4/{Code,Project|Projekt}` and the same with `Projekt`; names of folders are found without regard to case
        string pkgRoot = Path.Combine(t, "pkg-root");
        Write(pkgRoot, "package/Demo_1.2.3.4/code/Gadget/$name$.script", "class $class$ { }\n");             // the case of the folder names does not matter
        Write(pkgRoot, "package/Demo_1.2.3.4/Projekt/Starter/main.script", "print(1)\n");
        var pkgCatalog = TemplateCatalog.Load(builtinRoot: pkgRoot, userRoot: Path.Combine(root, "nope"), store: new fire.Package.Manager.PackageStore(Path.Combine(root, "no-packages")));
        var gadget = pkgCatalog.Find(TemplateScope.Code, "Gadget");
        var starter = pkgCatalog.Find(TemplateScope.Project, "Starter");
        Check("Vorlagen: Templates/Package/Name_Version/... - Quelle und Version stehen am Template", gadget != null && gadget.Source == "From Demo 1.2.3.4" && gadget.PackageName == "Demo" && gadget.PackageVersion == "1.2.3.4" && starter != null && starter.Source == "From Demo 1.2.3.4" && pkgCatalog.All.Count == 2, string.Join(",", pkgCatalog.All.Select(x => x.Display)));
        Check("Vorlagen: die Suche findet nach Titel, Beschreibung und Quelle", gadget!.Matches("gad") && gadget.Matches("demo 1.2") && gadget.Matches("") && !gadget.Matches("nothing") && Tpl(TemplateScope.Code, "FXML Window").Matches("label button") && !Tpl(TemplateScope.Code, "Script").Matches("window"));
        Check("Vorlagen: Name_Version wird zerlegt", TemplateCatalog.ParsePackageFolder("My_Pkg_2.0.1") == ("My_Pkg", "2.0.1") && TemplateCatalog.ParsePackageFolder("plain") == ("plain", null) && TemplateCatalog.ParsePackageFolder("a_b") == ("a_b", null));

        // a package that is forged with a templates/ folder brings it along; an installed package's templates are in the list, and a project made from one references the package
        string forgeDir = Path.Combine(t, "forge");
        Write(forgeDir, "tpkg.fire", "class TpkgThing { }\n");
        Write(forgeDir, "package.json", """{ "name": "tpkg", "version": "1.0.0", "imports": [ { "name": "tpkg", "prelude": "tpkg.fire" } ] }""");
        Write(forgeDir, "templates/Project/Starter App/template.json", """{ "title": "Starter App", "description": "From the package", "open": [ "main.script" ] }""");
        Write(forgeDir, "templates/Project/Starter App/main.script", "print(\"$name$ starts\")\n");
        Write(forgeDir, "templates/Code/Tpkg Thing/$name$.script", "class $class$ : TpkgThing { }\n");
        var forged = fire.Package.Manager.Fpk.Forge(Path.Combine(forgeDir, "package.json"), Path.Combine(forgeDir, "out"));
        Check("Vorlagen: ein Paket nimmt den Ordner templates/ mit", forged.Manifest.Templates == "templates" && System.IO.Compression.ZipFile.OpenRead(forged.PackagePath).Entries.Select(e => e.FullName).Contains("templates/Project/Starter App/main.script"));
        var store = new fire.Package.Manager.PackageStore(Path.Combine(t, "store"));
        store.Install(forged.PackagePath);
        var installedCatalog = TemplateCatalog.Load(builtinRoot: Path.Combine(root, "nope1"), userRoot: Path.Combine(root, "nope2"), store: store);
        var starterApp = installedCatalog.Find(TemplateScope.Project, "Starter App");
        Check("Vorlagen: die Templates eines installierten Pakets stehen in der Liste", starterApp != null && starterApp.Source == "From tpkg 1.0.0" && starterApp.AddPackageReference && installedCatalog.Find(TemplateScope.Code, "Tpkg Thing") != null && installedCatalog.All.Count == 2, string.Join(",", installedCatalog.All.Select(x => x.Display)));
        string starterProject = TemplateInstaller.CreateProject(starterApp!, Path.Combine(t, "from-package"), "Fresh");
        var fresh = FireProject.Load(starterProject);
        Check("Vorlagen: ein Projekt aus dem Template eines Pakets verweist auf das Paket", fresh.References.Count == 1 && fresh.References[0].Package == "tpkg" && fresh.References[0].Version == "1.0.0" && File.ReadAllText(Path.Combine(t, "from-package", "Fresh", "main.script")).Contains("Fresh starts"));

        // a library project that has the folder templates/ packs it, and the templates are no code of the library
        string tlibDir = Path.Combine(t, "tlib");
        Write(tlibDir, "TLib/TLib.fireproj", """{ "name": "TLib", "type": "library", "settings": { "version": "2.0.0" } }""");
        Write(tlibDir, "TLib/tlib.script", "namespace TLib { class A { } }\n");
        Write(tlibDir, "TLib/templates/Code/Tl Class/$name$.script", "class $class$ { not valid fire code at all }\n");
        var tlibWs = Workspace.Open(Path.Combine(tlibDir, "TLib", "TLib.fireproj"));
        var tlibPlan = BuildPlan.Create(tlibWs, tlibWs.Projects[0]);
        Check("Vorlagen: der Ordner templates/ eines Projekts ist kein Code des Projekts", tlibPlan.SourcePaths.Count == 1 && Ok(() => ProjectBuilder.Check(tlibPlan)) && tlibWs.Projects[0].ContentFiles.Any(f => f.EndsWith("$name$.script")), Catch(() => ProjectBuilder.Check(tlibPlan)));
        string tlibPack = ProjectBuilder.PackLibrary(tlibPlan, Path.Combine(tlibDir, "out"));
        Check("Vorlagen: das gepackte Projekt bringt seine Templates mit", fire.Package.Manager.Fpk.ReadManifest(tlibPack).Templates == "templates" && System.IO.Compression.ZipFile.OpenRead(tlibPack).Entries.Select(e => e.FullName).Contains("templates/Code/Tl Class/$name$.script"));

        // content of a project: resources are copied into a folder of it, natives get the folder native/
        string pic = Write(root, "outside/logo.png", "not really a picture");
        var content = terminal.Projects[0];
        string copied = terminal.AddContentFile(content, pic, Path.Combine(content.Directory, "resources"));
        Check("Inhalt: eine Ressource wird in den Unterordner des Projekts kopiert und gehoert nicht zum Code", copied == Path.Combine(content.Directory, "resources", "logo.png") && File.Exists(copied) && content.ContentFiles.Contains(copied) && !content.Files.Contains(copied) && content.Folders.Contains("resources"));
        Check("Inhalt: eine vorhandene Datei wird nicht ueberschrieben", Throws(() => terminal.AddContentFile(content, pic, Path.Combine(content.Directory, "resources"))));
        Check("Inhalt: ein Ordner ausserhalb des Projekts wird abgelehnt", Throws(() => terminal.AddContentFile(content, pic, Path.Combine(t, "elsewhere"))));
        string nativeHeaderPath = terminal.AddNative(content);
        Check("Inhalt: Natives bekommen den Ordner native/ und eine Kopfdatei", nativeHeaderPath == Path.Combine(content.Directory, "native", "t.hpp") && File.Exists(nativeHeaderPath) && content.Project.Native != null && File.ReadAllText(content.FilePath).Contains("\"native\"") && content.Folders.Contains("native"));
        Check("Inhalt: die Funktion der Kopfdatei wird gefunden", ProjectNatives.Resolve(content)!.Native.Functions.Any(f => f.Name == "__t_add"));
        Check("Inhalt: Notizen und Kopfdateien sind keine Quelldateien", content.Files.All(f => FireProject.IsSourceFile(f)) && !FireProject.IsSourceFile("native/t.hpp") && FireProject.IsSourceFile("a.fxml"));

        // a resource is compiled in when the code asks for it: the path is relative to the file that writes it
        Write(m0 = Path.Combine(root, "res"), "R/R.fireproj", """{ "name": "R" }""");
        Write(m0, "R/data/hello.txt", "hello from a resource");
        Write(m0, "R/code/main.script", "print(new Resource(\"../data/hello.txt\").Text())\n");
        var resWs = Workspace.Open(Path.Combine(m0, "R", "R.fireproj"));
        Check("Inhalt: new Resource(...) holt die Datei aus dem Projektordner (Pfad relativ zur Quelldatei)", RunPlan(BuildPlan.Create(resWs, resWs.Projects[0])).Trim() == "hello from a resource" && !resWs.Projects[0].Files.Any(f => f.EndsWith("hello.txt")));

        // ---- the code of a project is included on its own: no #include, markup files too ---------------------------------------------------
        string m = Path.Combine(root, "markup");
        Write(m, "Ui/Ui.fireproj", """{ "name": "Ui" }""");
        Write(m, "Ui/win.fxml", "<Window class=\"MainWindow\" title=\"x\" width=\"100\" height=\"100\">\n  <Label text=\"Hi\"/>\n</Window>\n");
        Write(m, "Ui/helper.script", "class Helper { static int Two() { return 2 } }\n");
        Write(m, "Ui/main.script", "#include \"helper.script\"\n#include \"win.fxml\"\nprint(Helper.Two())\n");
        var ui = Workspace.Open(Path.Combine(m, "Ui", "Ui.fireproj"));
        var uiPlan = BuildPlan.Create(ui, ui.Projects[0]);
        Check("Projekt: eine .fxml-Datei gehoert zu den Dateien und wird als erzeugtes Skript uebersetzt", ui.Projects[0].Files.Any(f => f.EndsWith("win.fxml")) && uiPlan.IsValid && uiPlan.Sources.First(s => s.FileName == "win.fxml").Text.Contains("class MainWindow"), string.Join("\n", uiPlan.Errors));
        Check("Projekt: Markup steht vor den Skripten", Path.GetFileName(ui.Projects[0].Files[0]) == "win.fxml");
        Check("Projekt: ein #include einer Datei des Projekts fuegt nichts doppelt ein", RunPlan(uiPlan).Trim() == "2", Catch(() => RunPlan(uiPlan)));
        Write(m, "Ui/broken.fxml", "<Window");
        ui.Projects[0].Refresh();
        var brokenPlan = BuildPlan.Create(ui, ui.Projects[0]);
        Check("Projekt: ungueltiges Markup ist ein Fehler mit dem Dateinamen", !brokenPlan.IsValid && brokenPlan.Errors.Any(e => e.Contains("broken.fxml")), string.Join("\n", brokenPlan.Errors));
        File.Delete(Path.Combine(m, "Ui", "broken.fxml"));
        ui.Projects[0].Refresh();
        string data = Write(m, "Ui/data.txt", "just data");
        ui.AddFile(ui.Projects[0], data);
        Check("Projekt: eine Datei, die kein Quelltext ist, wird nicht mitkompiliert", !ui.Projects[0].Files.Any(f => f.EndsWith("data.txt")) && !ui.Projects[0].Project.Files.Any(f => f.EndsWith("data.txt")));
    }

    private static bool CommandLine(string slnPath, string outDir)
    {
        var o = new StringWriter(); var e = new StringWriter();
        int code = CommandLineRunner.Run(new[] { "build", slnPath, "-p", "Core", "-o", outDir }, o, e);
        if (code != 0 || !o.ToString().Contains("Core-2.1.0.fpk")) return false;
        var o2 = new StringWriter(); var e2 = new StringWriter();
        int code2 = CommandLineRunner.Run(new[] { "build", slnPath, "-o", Path.Combine(outDir, "app.out") }, o2, e2);
        return code2 == 0 && File.Exists(Path.Combine(outDir, "app.out"));
    }

    private static bool Ok(Action action) { try { action(); return true; } catch (Exception) { return false; } }
    private static bool Throws(Action action) { try { action(); return false; } catch (Exception) { return true; } }
    private static string Catch(Action action) { try { action(); return ""; } catch (Exception ex) { return Describe(ex); } }
}
