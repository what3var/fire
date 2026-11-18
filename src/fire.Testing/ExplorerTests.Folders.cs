using fire.Compiler;
using fire.Editor;
using fire.Projects;

/// <summary>The folders of a project (docs/tools/projects.md, "Folders"): included (named or with code), undecided (neither), excluded (named, below an excluded folder, ignored by a .gitignore -
/// wins over everything); `includedDirectories`/`excludedDirectories`/`syncGitignore` read and written; builds pass over excluded folders and the content of undecided ones; "Sync .gitignore"
/// adds and removes its lines without duplicates. Alone: FIRE_TESTS_ONLY=explorer FIRE_TESTS_FILTER=Ordner.</summary>
static partial class ExplorerTests
{
    private static void FolderChecks()
    {
        string root = Path.Combine(Path.GetTempPath(), "fire-folders-" + Guid.NewGuid().ToString("N"));
        string Write(string rel, string text = "x") { string full = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text); return full; }
        try
        {
            FolderFormat();
            FolderRules(root, Write);
            FolderBuild(root, Write);
            FolderGitignore(root, Write);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static void FolderFormat()
    {
        var old = FireProject.Parse("""{ "name": "Old", "exclude": [] }""");
        string oldJson = old.ToJson();
        Check("Ordner: ein altes Projekt schreibt keine der neuen Eintraege", !oldJson.Contains("includedDirectories") && !oldJson.Contains("excludedDirectories") && !oldJson.Contains("syncGitignore") && old.SyncsGitignore, oldJson);
        var p = FireProject.Parse("""{ "name": "P", "includedDirectories": ["assets", "docs/"], "excludedDirectories": ["out", "**/generated"], "syncGitignore": false }""");
        var back = FireProject.Parse(p.ToJson());
        Check("Ordner: includedDirectories/excludedDirectories/syncGitignore hin und zurueck",
            back.IncludedDirectories!.SequenceEqual(new[] { "assets", "docs/" }) && back.ExcludedDirectories!.SequenceEqual(new[] { "out", "**/generated" }) && back.SyncGitignore == false && !back.SyncsGitignore, p.ToJson());
        p.SyncGitignore = true;
        p.IncludedDirectories = new List<string>();
        string json = p.ToJson();
        Check("Ordner: syncGitignore an (Vorgabe) und leere Listen werden nicht geschrieben", !json.Contains("syncGitignore") && !json.Contains("includedDirectories") && json.Contains("excludedDirectories"), json);

        var e = new FireProject { Name = "E", IncludedDirectories = new() { "assets" } };
        Check("Ordner: Exclude nimmt den Ordner aus includedDirectories und traegt ihn einmal ein",
            ProjectFolderEdits.Exclude(e, "assets/") && !ProjectFolderEdits.Exclude(e, "./assets") && e.IncludedDirectories == null && e.ExcludedDirectories!.SequenceEqual(new[] { "assets" }));
        Check("Ordner: Include hebt die eigene Ausnahme auf", ProjectFolderEdits.Include(e, "assets") && e.ExcludedDirectories == null && e.IncludedDirectories!.SequenceEqual(new[] { "assets" }));
        ProjectFolderEdits.Exclude(e, "out");
        Check("Ordner: RemoveExclusion nimmt nur den eigenen Eintrag", ProjectFolderEdits.RemoveExclusion(e, "out") && !ProjectFolderEdits.RemoveExclusion(e, "out") && e.ExcludedDirectories == null);
    }

    private static void FolderRules(string root, Func<string, string, string> write)
    {
        string dir = Path.Combine(root, "rules");
        string W(string rel, string text = "x") => write(Path.Combine("rules", rel), text);
        W("App.fireproj", """{ "name": "App", "includedDirectories": ["assets/icons", "listed"], "excludedDirectories": ["src/old", "**/generated", "listed"], "syncGitignore": false }""");
        W("main.fire", "print(1)\n");
        W("src/a.fire");
        W("src/images/logo.png");          // below an included folder, without code: undecided
        W("src/old/legacy.fire");          // code in an excluded folder: excluded wins
        W("src/old/deeper/x.fire");
        W("lib/deep/down/b.fxml");         // code far down: every folder on the way is included
        W("assets/readme.txt");            // no code, not named: undecided
        W("assets/icons/a.png");           // named
        W("assets/icons/small/b.png");     // below a named folder
        W("docs/guide.md");
        W("ui/generated/g.fire");          // a pattern excludes it
        W("listed/x.txt");                 // named in both lists: excluded
        W("node_modules/pkg/index.js");
        W("Other/Other.fireproj");
        W(".gitignore", "docs/\n");        // sync off: not read
        Directory.CreateDirectory(Path.Combine(dir, "empty"));
        var project = new LoadedProject(FireProject.Load(Path.Combine(dir, "App.fireproj")), false);
        var map = project.FolderMap;
        string S(string rel) { var f = map.StateOf(rel); return $"{f.State}/{f.Reason}"; }
        Check("Ordner: das Projekt selbst ist enthalten", S("") == "Included/Root");
        Check("Ordner: ein Ordner mit Code ist von selbst enthalten", S("src") == "Included/Code", S("src"));
        Check("Ordner: Code weit unten nimmt jeden Ordner auf dem Weg auf", S("lib") == "Included/Code" && S("lib/deep") == "Included/Code" && S("lib/deep/down") == "Included/Code", S("lib") + S("lib/deep/down"));
        Check("Ordner: ohne Code und ungenannt ist er unentschieden", S("docs") == "Undecided/None" && S("empty") == "Undecided/None" && S("src/images") == "Undecided/None", S("docs") + " " + S("src/images"));
        Check("Ordner: ein genannter Ordner ist enthalten, sein Elternordner auch", S("assets/icons") == "Included/Listed" && S("assets").StartsWith("Included"), S("assets") + " " + S("assets/icons"));
        Check("Ordner: unter einem genannten Ordner ist alles enthalten", S("assets/icons/small") == "Included/InsideListed", S("assets/icons/small"));
        Check("Ordner: ausgeschlossen schlaegt Code", S("src/old") == "Excluded/Listed" && !project.Files.Any(f => f.Contains("legacy")) && !project.Files.Any(f => f.EndsWith("x.fire")), S("src/old"));
        Check("Ordner: unter einem ausgeschlossenen Ordner ist alles ausgeschlossen (und wird nicht betreten)", S("src/old/deeper") == "Excluded/InsideListed" && !map.Folders.Any(f => f.Path == "src/old/deeper"), S("src/old/deeper"));
        Check("Ordner: ein Muster schliesst aus", S("ui/generated") == "Excluded/Listed" && map.StateOf("ui/generated").Entry == "**/generated" && !map.StateOf("ui/generated").IsOwnExclusion, S("ui/generated"));
        Check("Ordner: in beiden Listen gewinnt der Ausschluss", S("listed") == "Excluded/Listed" && map.StateOf("listed").IsOwnExclusion, S("listed"));
        Check("Ordner: bin, node_modules und Punkt-Ordner bleiben ausgeschlossen", S("node_modules") == "Excluded/Hidden" && S("bin") == "Excluded/Hidden" && S(".cache") == "Excluded/Hidden");
        Check("Ordner: ohne Sync .gitignore zaehlt die .gitignore nicht", S("docs") == "Undecided/None");
        Check("Ordner: der Ordner eines anderen Projekts gehoert nicht dazu", !map.Folders.Any(f => f.Path.StartsWith("Other")));
        Check("Ordner: Folders nennt die ausgeschlossenen nicht, die unentschiedenen schon", !project.Folders.Contains("src/old") && !project.Folders.Contains("listed") && project.Folders.Contains("docs") && project.Folders.Contains("empty"), string.Join(",", project.Folders));
        Check("Ordner: Inhalt aus ausgeschlossenen Ordnern gibt es nicht, aus unentschiedenen schon", !project.ContentFiles.Any(f => f.Contains("listed")) && project.ContentFiles.Any(f => f.EndsWith("guide.md")));
        Check("Ordner: Label sagt warum", map.StateOf("src/old").Label == "excluded" && map.StateOf("docs").Label == "undecided" && map.StateOf("src").Label == "");
        var hidden = ProjectFiles.HiddenEntries(dir, excluded: map.ExcludedFolders.Select(f => f.Path).ToList());
        Check("Ordner: Show All Files zeigt die ausgeschlossenen Ordner mit Inhalt", hidden.Folders.Contains("src/old") && hidden.Folders.Contains("src/old/deeper") && hidden.Files.Contains("src/old/legacy.fire") && hidden.Folders.Contains("listed"),
            string.Join(",", hidden.Folders));

        var ex = WatchExclusions.Of(new[] { project }, new[] { Path.Combine(dir, "listed", "x.txt") });
        Check("Ordner: der Watcher uebergeht ausgeschlossene Ordner", ex.Skips(Path.Combine(dir, "src", "old", "legacy.fire")) && ex.Skips(Path.Combine(dir, "ui", "generated", "new.fire")));
        Check("Ordner: der Watcher uebergeht Inhalt unentschiedener Ordner, aber nicht ihre Quellen", ex.Skips(Path.Combine(dir, "docs", "guide.md")) && !ex.Skips(Path.Combine(dir, "docs", "new.fire")) && !ex.Skips(Path.Combine(dir, "src", "a.fire")));
        Check("Ordner: eine offene Datei meldet der Watcher immer", !ex.Skips(Path.Combine(dir, "listed", "x.txt")));
    }

    private static void FolderBuild(string root, Func<string, string, string> write)
    {
        string dir = Path.Combine(root, "build");
        string W(string rel, string text = "x") => write(Path.Combine("build", rel), text);
        W("B.fireproj", """{ "name": "B", "excludedDirectories": ["broken"], "buildActions": { "assets/keep.txt": "copy", "broken/data.txt": "copy" }, "syncGitignore": false }""");
        string main = W("main.fire", "print(\"main\")\n");
        string broken = W("broken/bad.fire", "this is not fire code ((\n");
        W("broken/data.txt", "never copied");
        W("assets/keep.txt", "copied");
        W("assets/other.txt", "undecided");
        string named = W("named/n.fire", "print(1)\n");
        var ws = Workspace.Open(Path.Combine(dir, "B.fireproj"));
        var project = ws.Projects[0];
        Check("Ordner/Build: eine Quelle im ausgeschlossenen Ordner wird nicht kompiliert", project.Files.Contains(main) && !project.Files.Contains(broken), string.Join(",", project.Files));
        var plan = BuildPlan.Create(ws, project);
        bool compiles;
        try { ProjectBuilder.Check(plan); compiles = true; } catch (Exception e) { compiles = false; Console.WriteLine(e.Message); }
        Check("Ordner/Build: das Projekt kompiliert ohne den ausgeschlossenen (kaputten) Ordner", plan.IsValid && compiles, string.Join("\n", plan.Errors));
        var copies = ProjectContent.ItemsOf(plan).Select(c => c.RelativePath).ToList();
        Check("Ordner/Build: Copy im unentschiedenen Ordner zaehlt, im ausgeschlossenen nie", copies.Contains("assets/keep.txt") && !copies.Contains("broken/data.txt"), string.Join(",", copies));
        Check("Ordner/Build: der Inhalt unentschiedener Ordner zaehlt fuer den Build nicht", !project.BuildContentFiles.Any(f => f.EndsWith("other.txt")) && project.ContentFiles.Any(f => f.EndsWith("other.txt")));
        // a file the project names in an excluded folder: excluded wins
        File.WriteAllText(Path.Combine(dir, "B.fireproj"), """{ "name": "B", "files": ["named/n.fire", "main.fire"], "excludedDirectories": ["named"], "syncGitignore": false }""");
        var again = new LoadedProject(FireProject.Load(Path.Combine(dir, "B.fireproj")), false);
        Check("Ordner/Build: auch eine genannte Datei im ausgeschlossenen Ordner wird nicht kompiliert", !again.Files.Contains(named) && again.Files.Contains(main), string.Join(",", again.Files));
    }

    private static void FolderGitignore(string root, Func<string, string, string> write)
    {
        // a repository with the project in a subfolder
        string repo = Path.Combine(root, "repo");
        string W(string rel, string text = "x") => write(Path.Combine("repo", rel), text);
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        W(".gitignore", "*.fpk\n/App/out/\ncache/\n!/App/keep/cache/\n");
        W("App/App.fireproj", """{ "name": "App" }""");
        W("App/main.fire");
        W("App/out/o.txt");
        W("App/src/cache/c.fire");
        W("App/keep/cache/k.txt");
        W("App/raw/r.png");
        W("App/sub/.gitignore", "local/\n");
        W("App/sub/local/l.txt");
        W("App/sub/s.fire");
        string app = Path.Combine(repo, "App");
        var project = new LoadedProject(FireProject.Load(Path.Combine(app, "App.fireproj")), false);
        var map = project.FolderMap;
        Check("Ordner/.gitignore: was die .gitignore ignoriert, ist mit Sync ausgeschlossen", map.StateOf("out") is { State: FolderState.Excluded, Reason: FolderReason.Gitignore, Entry: "/App/out/" }, map.StateOf("out").ToString());
        Check("Ordner/.gitignore: ein Muster ohne / gilt in jeder Tiefe, auch gegen Code", map.StateOf("src/cache").Reason == FolderReason.Gitignore && !project.Files.Any(f => f.EndsWith("c.fire")));
        Check("Ordner/.gitignore: ! nimmt einen Ordner wieder herein", map.StateOf("keep/cache").State != FolderState.Excluded, map.StateOf("keep/cache").ToString());
        Check("Ordner/.gitignore: eine .gitignore in einem Unterordner zaehlt dort", map.StateOf("sub/local").Reason == FolderReason.Gitignore && map.StateOf("sub").State == FolderState.Included);
        Check("Ordner/.gitignore: Label 'ignored by .gitignore'", map.StateOf("out").Label == "ignored by .gitignore");
        var g = GitIgnore.Parse(repo, "/a/b/\n**/x\n*.log\ndoc[0-9]/\n");
        Check("Ordner/.gitignore: verankert, ** und Zeichenklassen", g.IsIgnored(Path.Combine(repo, "a", "b"), true) && !g.IsIgnored(Path.Combine(repo, "c", "a", "b"), true) && g.IsIgnored(Path.Combine(repo, "q", "r", "x"), true)
            && g.IsIgnored(Path.Combine(repo, "z.log"), false) && g.IsIgnored(Path.Combine(repo, "doc1"), true) && !g.IsIgnored(Path.Combine(repo, "docs"), true));

        // sync: excluding writes the line (once), removing takes out only spark's line
        string gi = Path.Combine(repo, ".gitignore");
        Check("Ordner/.gitignore: die naechste .gitignore ist die oben im Repository (das Projekt hat keine)", GitignoreSync.FileFor(app) == gi);
        var (file, line, added) = GitignoreSync.Add(project.Project, Path.Combine(app, "raw"));
        Check("Ordner/.gitignore: Exclude schreibt /App/raw/ mit Marke", file == gi && line == "/App/raw/" && added && File.ReadAllText(gi).Contains(GitignoreSync.MarkerFor(project.Project) + "\n/App/raw/\n"), File.ReadAllText(gi));
        var second = GitignoreSync.Add(project.Project, Path.Combine(app, "raw"));
        Check("Ordner/.gitignore: kein zweites Mal", !second.Added && File.ReadAllText(gi).Split('\n').Count(l => l == "/App/raw/") == 1);
        Check("Ordner/.gitignore: eine Zeile, die es schon gibt (nicht von spark), wird nicht verdoppelt", !GitignoreSync.Add(project.Project, Path.Combine(app, "out")).Added && File.ReadAllText(gi).Split('\n').Count(l => l.Contains("App/out")) == 1);
        project.Refresh();
        Check("Ordner/.gitignore: danach ist der Ordner ausgeschlossen", project.FolderMap.StateOf("raw").Reason == FolderReason.Gitignore);
        var removed = GitignoreSync.Remove(project.Project, Path.Combine(app, "raw"));
        Check("Ordner/.gitignore: Remove nimmt die Zeile und ihre Marke heraus", removed.File == gi && !File.ReadAllText(gi).Contains("/App/raw/") && !File.ReadAllText(gi).Contains(GitignoreSync.Marker) && File.ReadAllText(gi).StartsWith("*.fpk\n/App/out/\n"), File.ReadAllText(gi));
        var foreign = GitignoreSync.Remove(project.Project, Path.Combine(app, "out"));
        Check("Ordner/.gitignore: eine Zeile, die spark nicht schrieb, bleibt", foreign.File == null && foreign.Foreign == "/App/out/" && File.ReadAllText(gi).Contains("/App/out/"));
        // the project's own .gitignore is nearer
        W("App/.gitignore", "tmp/\n");
        var near = GitignoreSync.Add(project.Project, Path.Combine(app, "raw"));
        Check("Ordner/.gitignore: hat das Projekt eine .gitignore, kommt die Zeile dorthin (relativ zu ihr)", near.File == Path.Combine(app, ".gitignore") && near.Line == "/raw/" && File.ReadAllText(near.File).EndsWith("/raw/\n"));
        GitignoreSync.Remove(project.Project, Path.Combine(app, "raw"));
        Check("Ordner/.gitignore: und wieder heraus", File.ReadAllText(Path.Combine(app, ".gitignore")) == "tmp/\n", File.ReadAllText(Path.Combine(app, ".gitignore")));
    }
}
