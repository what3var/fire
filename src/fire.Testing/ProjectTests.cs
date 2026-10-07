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
