using fire.Editor;
using fire.Git;
using fire.Projects;

/// <summary>
/// Files that change under the editor and the Git panel (docs/tools/git.md): what the editor remembers of its files (DiskBaselines), the bundling of watcher events (ChangeBatcher, WatchFilter),
/// the plan of what to load again (ReloadPlanner) after a switch of the branch and a pull in a temporary repository, loading a project and a solution again (Workspace), the lists of the
/// Git panel (GitOverview) and the differences of two texts (TextDiff). Alone: FIRE_TESTS_ONLY=git (with GitTests); FIRE_TESTS_FILTER narrows the checks by their title.
/// </summary>
static class GitSyncTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        string name = "GitSync: " + title;
        if (!Selected(name)) return;
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: {name}" : $"FEHLER: {name}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static bool Selected(string name)
    {
        string? filter = Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER");
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
    }

    private static string Write(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public static void Run()
    {
        Console.WriteLine("=== Git: Dateien, die sich auf der Platte aendern, und das Git-Panel ===");
        string root = Path.Combine(Path.GetTempPath(), "fire-gitsync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TextDiffChecks();
            BatcherChecks();
            FilterChecks();
            BaselineChecks(Path.Combine(root, "baselines"));
            if (GitRepository.UnavailableReason != null) Console.WriteLine("(git is not available here: " + GitRepository.UnavailableReason + ")");
            else
            {
                OverviewChecks(Path.Combine(root, "overview"));
                BranchSwitchChecks(Path.Combine(root, "switch"));
                PullChecks(root);
            }
        }
        catch (Exception ex) { _failures++; Console.WriteLine("FEHLER: GitSync: unerwartete Ausnahme: " + ex); }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { } }
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine(_failures == 0 ? "Alle GitSync-Pruefungen bestanden." : $"FEHLER: {_failures} GitSync-Pruefung(en) fehlgeschlagen.");
    }

    // ---- TextDiff ---------------------------------------------------------------------------------------------------------------------------

    private static void TextDiffChecks()
    {
        Check("TextDiff: gleiche Texte (auch \\r\\n gegen \\n) ergeben nichts", TextDiff.Unified("a\nb\n", "a\r\nb\r\n") == "" && TextDiff.Unified("", "") == "");
        string one = TextDiff.Unified("a\nb\nc\n", "a\nB\nc\n", "old", "new");
        Check("TextDiff: eine geaenderte Zeile mit Kopf und Hunk", one == "--- old\n+++ new\n@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n", one);
        string added = TextDiff.Unified("x\n", "x\ny\nz\n");
        Check("TextDiff: angehaengte Zeilen", added.Contains("@@ -1,1 +1,3 @@") && added.Contains("+y\n+z\n") && !added.Contains("-x"), added);
        string fromEmpty = TextDiff.Unified("", "new\n");
        Check("TextDiff: aus einer leeren Datei", fromEmpty.Contains("@@ -0,0 +1,1 @@") && fromEmpty.Contains("+new"), fromEmpty);
        var lines = Enumerable.Range(1, 40).Select(i => "line " + i).ToList();
        var changed = lines.ToList(); changed[2] = "LINE 3"; changed[35] = "LINE 36"; changed.Insert(20, "inserted");
        string far = TextDiff.Unified(string.Join("\n", lines), string.Join("\n", changed));
        Check("TextDiff: weit auseinander liegende Aenderungen sind getrennte Hunks mit 3 Zeilen Kontext",
            far.Split('\n').Count(l => l.StartsWith("@@")) == 3 && far.Contains("-line 3\n+LINE 3") && far.Contains("+inserted") && far.Contains("-line 36\n+LINE 36") && !far.Contains(" line 10\n"), far);
        string swapped = TextDiff.Unified("a\nb\nc\nd\n", "d\nc\nb\na\n");
        int minus = swapped.Split('\n').Count(l => l.StartsWith('-') && !l.StartsWith("---")), plus = swapped.Split('\n').Count(l => l.StartsWith('+') && !l.StartsWith("+++"));
        Check("TextDiff: das kuerzeste Editierskript (vertauschte Zeilen)", minus == 3 && plus == 3, swapped);
    }

    // ---- ChangeBatcher, WatchFilter ---------------------------------------------------------------------------------------------------------

    private static void BatcherChecks()
    {
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var batcher = new ChangeBatcher(TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(3));
        string file = Path.Combine(Path.GetTempPath(), "x", "a.fire");
        Check("ChangeBatcher: leer gibt nichts", !batcher.TryTake(t0, out _) && !batcher.HasPending);
        batcher.Add(file, false, t0);
        batcher.Add(file, true, t0.AddMilliseconds(100));
        Check("ChangeBatcher: waehrend des Bursts noch nichts", !batcher.TryTake(t0.AddMilliseconds(300), out _));
        Check("ChangeBatcher: nach der Ruhezeit ein Batch, doppelte Pfade einmal", batcher.TryTake(t0.AddMilliseconds(600), out var b) && b.Paths.Count == 1 && b.Appeared.Contains(Path.GetFullPath(file)) && !batcher.HasPending);
        // a burst that does not stop: at the latest after maxDelay
        int deliveredAt = -1, deliveredCount = 0;
        for (int i = 0; i < 40 && deliveredAt < 0; i++)
        {
            var now = t0.AddSeconds(10).AddMilliseconds(i * 100);
            batcher.Add(file + i, false, now);
            if (batcher.TryTake(now, out var burst)) { deliveredAt = i; deliveredCount = burst.Paths.Count; }
        }
        Check("ChangeBatcher: ein Dauerfeuer wird spaetestens nach maxDelay ausgeliefert", deliveredAt == 30 && deliveredCount == 31, $"{deliveredAt}, {deliveredCount}");
        batcher.Hold();
        batcher.AddGitState(t0.AddSeconds(20));
        Check("ChangeBatcher: gehalten (ein Git-Befehl laeuft) gibt nichts", !batcher.TryTake(t0.AddSeconds(30), out _));
        batcher.Release();
        Check("ChangeBatcher: danach der Git-Zustand", batcher.TryTake(t0.AddSeconds(30), out var g) && g.GitStateChanged && g.Paths.Count == 0);
        batcher.Add(file, false, t0.AddSeconds(40)); batcher.Clear();
        Check("ChangeBatcher: Clear verwirft", !batcher.TryTake(t0.AddSeconds(50), out _));
    }

    private static void FilterChecks()
    {
        string r = Path.Combine(Path.GetTempPath(), "repo");
        string P(string rel) => Path.Combine(r, rel.Replace('/', Path.DirectorySeparatorChar));
        Check("WatchFilter: Quelltexte zaehlen, bin/obj nicht", WatchFilter.Classify(P("App/main.fire")) == WatchFilter.Kind.WorkingTree && WatchFilter.Classify(P("App/bin/x.exe")) == WatchFilter.Kind.Ignore && WatchFilter.Classify(P("App/obj/a.json")) == WatchFilter.Kind.Ignore);
        Check("WatchFilter: .git/index und .git/HEAD sind Git-Zustand, der Rest von .git nicht", WatchFilter.Classify(P(".git/index")) == WatchFilter.Kind.GitState && WatchFilter.Classify(P(".git/HEAD")) == WatchFilter.Kind.GitState
            && WatchFilter.Classify(P(".git/objects/ab/cdef")) == WatchFilter.Kind.Ignore && WatchFilter.Classify(P(".git/index.lock")) == WatchFilter.Kind.Ignore);
        Check("WatchFilter: Temporaeres von Editoren nicht", WatchFilter.Classify(P("a.fire~")) == WatchFilter.Kind.Ignore && WatchFilter.Classify(P(".a.fire.swp")) == WatchFilter.Kind.Ignore);
        var roots = WatchFilter.Roots(new[] { P("a/b"), P("a"), P("a/b/c"), P("ab"), P("x/y") });
        Check("WatchFilter: die wenigsten Ordner (ab liegt nicht in a)", roots.Count == 3 && roots.Contains(P("a")) && roots.Contains(P("ab")) && roots.Contains(P("x/y")), string.Join(", ", roots));
        Check("WatchFilter: IsInside", WatchFilter.IsInside(P("a/b.fire"), P("a")) && WatchFilter.IsInside(P("a"), P("a")) && !WatchFilter.IsInside(P("ab/c"), P("a")));
    }

    // ---- DiskBaselines -----------------------------------------------------------------------------------------------------------------------

    private static void BaselineChecks(string dir)
    {
        var baselines = new DiskBaselines();
        string file = Write(dir, "doc.fire", "print(1)\n");
        Check("DiskBaselines: Unbekanntes ist Unknown", baselines.Check(file) == DiskChange.Unknown);
        baselines.Remember(file);
        Check("DiskBaselines: unveraendert", baselines.Check(file) == DiskChange.Unchanged);
        File.WriteAllText(file, "print(2)\n");   // same length, maybe the same time stamp: the content counts
        Check("DiskBaselines: gleiche Laenge, anderer Inhalt ist geaendert", baselines.Check(file) == DiskChange.Changed);
        baselines.Remember(file);
        File.Delete(file);
        Check("DiskBaselines: geloescht", baselines.Check(file) == DiskChange.Deleted);
        baselines.Remember(file);
        Check("DiskBaselines: als fehlend gemerkt ist unveraendert", baselines.Check(file) == DiskChange.Unchanged);
        File.WriteAllText(file, "back\n");
        Check("DiskBaselines: wieder da ist Created", baselines.Check(file) == DiskChange.Created);
        baselines.Forget(file);
        Check("DiskBaselines: vergessen", !baselines.Knows(file) && baselines.Check(file) == DiskChange.Unknown);
    }

    // ---- GitOverview -------------------------------------------------------------------------------------------------------------------------

    private static void OverviewChecks(string work)
    {
        using var repo = GitRepository.Init(work);
        repo.SetIdentity("Tester", "tester@example.com");
        string a = Write(work, "src/a.fire", "a\n"), b = Write(work, "src/b.fire", "b\n"), c = Write(work, "c.fire", "c\n");
        repo.Stage(new[] { a });
        var first = repo.Overview();
        Check("Overview: ohne Commit - vorgemerkt und unversioniert", first.Staged.Single().Path == "src/a.fire" && first.Staged[0].Letter == "A" && first.Untracked.Count == 3 && first.Changes.Count == 0 && first.AheadBehindText == "no server",
            string.Join(",", first.Untracked.Select(e => e.Path)));
        repo.Unstage(new[] { a });
        Check("Unstage: ohne Commit wird die Datei wieder unversioniert", repo.Overview().Staged.Count == 0 && repo.Overview().Untracked.Any(e => e.Path == "src/a.fire"));
        repo.StageAll();
        repo.Commit("first");
        File.WriteAllText(a, "a2\n");
        repo.Stage(new[] { a });
        File.WriteAllText(a, "a3\n");
        File.Delete(b);
        string d = Write(work, "src/new/d.fire", "d\n");
        var o = repo.Overview();
        Check("Overview: vorgemerkt und danach wieder geaendert steht in beiden Listen", o.Staged.Single().Path == "src/a.fire" && o.Staged[0].IsStaged && o.Changes.Any(e => e.Path == "src/a.fire" && !e.IsStaged && e.Letter == "M"));
        Check("Overview: geloescht (D) und unversioniert (U)", o.Changes.Any(e => e.Path == "src/b.fire" && e.Letter == "D") && o.Untracked.Single().Path == "src/new/d.fire" && o.Untracked[0].Letter == "U" && o.Count == 4);
        Check("Overview: Name und Ordner einer Zeile", o.Untracked[0].Name == "d.fire" && o.Untracked[0].Folder == "src/new" && o.Changes.First(e => e.Path == "src/b.fire").FullPath == Path.GetFullPath(b));
        Check("Overview: der Zweig", o.Branch == repo.BranchName && !o.IsMerging && !o.HasConflicts);
        repo.Stage(new[] { b, d });
        repo.UnstageAll();
        var after = repo.Overview();
        Check("UnstageAll: nichts mehr vorgemerkt, die Dateien bleiben wie sie sind", after.Staged.Count == 0 && File.ReadAllText(a) == "a3\n" && after.Changes.Count == 2 && after.Untracked.Count == 1);

        // reading the state must not write the index (the watcher of .git/index would read the state again and again)
        string index = Path.Combine(work, ".git", "index");
        var before = (File.GetLastWriteTimeUtc(index), FileStamp.Of(index));
        for (int i = 0; i < 3; i++) { repo.Overview(); repo.Status(); }
        Check("Status: liest den Index, schreibt ihn nicht", before == (File.GetLastWriteTimeUtc(index), FileStamp.Of(index)));
        Check("Overview: ahead/behind als Text", GitOverview.Build("main", 2, 1, true, true, false, Array.Empty<GitChange>()).AheadBehindText == "↑2 ↓1"
            && GitOverview.Build("main", 0, 0, true, true, false, Array.Empty<GitChange>()).AheadBehindText == "up to date"
            && GitOverview.Build("main", 0, 0, false, true, false, Array.Empty<GitChange>()).AheadBehindText == "not on the server yet");
        var conflict = GitOverview.Build("main", 0, 0, true, true, true, new[] { new GitChange("x.fire", "/x.fire", GitFileState.Conflicted, GitFileState.Conflicted) });
        Check("Overview: ein Konflikt steht einmal unter Changes, mit !", conflict.Changes.Single().Letter == "!" && conflict.Staged.Count == 0 && conflict.HasConflicts);
    }

    // ---- a switch of the branch: the solution, a project, documents --------------------------------------------------------------------------

    private static ReloadPlan Plan(DiskBaselines baselines, Workspace ws, IEnumerable<OpenFileInfo> docs, ChangeBatch? batch = null) =>
        ReloadPlanner.Plan(baselines, ws.FilePath, ws.AllProjects.Select(p => p.FilePath), ws.AllProjects.Select(p => p.Directory), docs, batch);

    private static void Remember(DiskBaselines baselines, Workspace ws, params string[] docs)
    {
        if (ws.FilePath != null) baselines.Remember(ws.FilePath);
        foreach (var p in ws.AllProjects) baselines.Remember(p.FilePath);
        foreach (var d in docs) baselines.Remember(d);
    }

    private static void BranchSwitchChecks(string work)
    {
        Directory.CreateDirectory(work);
        var ws = Workspace.CreateSolution(Path.Combine(work, "Demo.firesln"));
        var app = ws.CreateProject(Path.Combine(work, "App", "App.fireproj"), OutputType.Exe);
        var lib = ws.CreateProject(Path.Combine(work, "Lib", "Lib.fireproj"), OutputType.Library);
        string main = Path.Combine(work, "App", "main.fire");
        string helper = Write(work, "App/helper.fire", "func helper() { }\n");
        string notes = Write(work, "App/notes.fire", "// notes\n");
        using var repo = GitRepository.Init(work);
        repo.SetIdentity("Tester", "tester@example.com");
        repo.StageAll(); repo.Commit("start");
        string start = repo.BranchName;

        // the branch "feature": the project App is a library there, main.fire is changed, helper.fire is gone, extra.fire is new
        repo.CreateBranch("feature");
        var appModel = FireProject.Load(app.FilePath); appModel.Type = OutputType.Library; appModel.Save(app.FilePath);
        File.WriteAllText(main, "print(\"feature\")\n");
        File.Delete(helper);
        Write(work, "App/extra.fire", "func extra() { }\n");
        repo.StageAll(); repo.Commit("feature work");
        repo.Checkout(start);
        ws.Reload();
        Check("Workspace.Reload: zurueck auf dem ersten Zweig ist App ein Programm", ws.FindByName("App")!.Project.Type == OutputType.Exe && ws.Projects.Count == 2);

        var baselines = new DiskBaselines();
        Remember(baselines, ws, main, helper, notes);
        var docs = new[] { new OpenFileInfo(main, false), new OpenFileInfo(helper, false), new OpenFileInfo(notes, true) };
        Check("ReloadPlanner: nichts geaendert, nichts zu tun (ausser die Ordner nach einem Git-Befehl)", Plan(baselines, ws, docs) is { ReloadWorkspace: false, ProjectsToReload.Count: 0, Documents.Count: 0, RefreshFolders: true }
            && Plan(baselines, ws, docs, new ChangeBatch()).IsEmpty);

        repo.Checkout("feature");
        var plan = Plan(baselines, ws, docs);
        Check("ReloadPlanner: nach dem Wechsel wird nur das geaenderte Projekt neu geladen", !plan.ReloadWorkspace && !plan.CloseWorkspace && plan.ProjectsToReload.SequenceEqual(new[] { Path.GetFullPath(app.FilePath) }), string.Join(",", plan.ProjectsToReload));
        Check("ReloadPlanner: ein unveraendertes Dokument still neu, ein geloeschtes markiert, ein unberuehrtes nicht",
            plan.Documents.Contains((main, DocumentReload.Silently)) && plan.Documents.Contains((helper, DocumentReload.MarkDeleted)) && plan.Documents.Count == 2 && plan.RefreshFolders,
            string.Join(", ", plan.Documents));
        var oldApp = ws.FindByName("App");
        Check("Workspace.ReloadProject: das Projekt neu von der Platte, an seiner Stelle, mit den neuen Dateien",
            ws.ReloadProject(app.FilePath) && ws.FindByName("App") is { } fresh && !ReferenceEquals(fresh, oldApp) && fresh.Project.Type == OutputType.Library && ws.Projects[0] == fresh
            && fresh.Files.Any(f => f.EndsWith("extra.fire")) && !fresh.Files.Any(f => f.EndsWith("helper.fire")) && ws.FindProjectOf(Path.Combine(work, "App", "extra.fire")) == fresh,
            string.Join(",", ws.FindByName("App")!.Files));
        Check("Workspace.ReloadProject: ein fremdes Projekt nicht", !ws.ReloadProject(Path.Combine(work, "Nope", "Nope.fireproj")));

        // the same with a document that has changes of its own: the user is asked
        Remember(baselines, ws, main, helper, notes);
        repo.Checkout(start);
        var mine = Plan(baselines, ws, new[] { new OpenFileInfo(main, true), new OpenFileInfo(helper, false, IsMarkedDeleted: true) });
        Check("ReloadPlanner: geaendert auf der Platte und im Editor wird gefragt; ein zurueckgekehrtes Dokument still neu",
            mine.Documents.Contains((main, DocumentReload.Ask)) && mine.Documents.Contains((helper, DocumentReload.Silently)), string.Join(", ", mine.Documents));

        // the watcher: only what it reported counts; a new file in a project folder reads the folders again; a change of a file nobody holds does nothing
        Remember(baselines, ws, main, helper, notes);
        ws.ReloadProject(app.FilePath);
        File.WriteAllText(notes, "// changed outside\n");
        string fresh2 = Write(work, "Lib/more.fire", "func more() { }\n");
        var batch = new ChangeBatch();
        batch.Paths.Add(Path.GetFullPath(notes)); batch.Paths.Add(Path.GetFullPath(fresh2)); batch.Appeared.Add(Path.GetFullPath(fresh2));
        var watched = Plan(baselines, ws, docs, batch);
        Check("ReloadPlanner (Watcher): eine neue Datei in einem Projektordner liest die Ordner neu", watched.RefreshFolders && watched.ProjectsToReload.Count == 0 && !watched.ReloadWorkspace);
        Check("ReloadPlanner (Watcher): das geaenderte Dokument mit eigenen Aenderungen wird gefragt", watched.Documents.SequenceEqual(new[] { (notes, DocumentReload.Ask) }), string.Join(", ", watched.Documents));
        var quiet = new ChangeBatch(); quiet.Paths.Add(Path.Combine(work, "README.md"));
        Check("ReloadPlanner (Watcher): eine geaenderte Datei, die niemand haelt, tut nichts", Plan(baselines, ws, docs, quiet).IsEmpty);
        var outside = new ChangeBatch(); outside.Paths.Add(Path.Combine(work, "loose.txt")); outside.Appeared.Add(Path.Combine(work, "loose.txt"));
        Check("ReloadPlanner (Watcher): eine neue Datei ausserhalb der Projekte liest keine Ordner", !Plan(baselines, ws, docs, outside).RefreshFolders);
        baselines.Remember(notes);

        // the solution file: changed -> load again (the projects with it); deleted -> close
        var sln = FireSolution.Load(ws.FilePath!); sln.Startup = "Lib"; sln.Save(ws.FilePath!);
        var slnPlan = Plan(baselines, ws, docs);
        Check("ReloadPlanner: eine geaenderte Loesung wird ganz neu geladen (die Projekte damit)", slnPlan.ReloadWorkspace && slnPlan.ProjectsToReload.Count == 0 && !slnPlan.RefreshFolders);
        ws.Reload();
        Check("Workspace.Reload: die neue Startprojekt-Einstellung gilt", ws.Startup?.Name == "Lib");
        Remember(baselines, ws);
        File.Delete(lib.FilePath);
        Check("ReloadPlanner: ein geloeschtes Projekt laedt die Loesung neu", Plan(baselines, ws, docs).ReloadWorkspace);
        File.Delete(ws.FilePath!);
        Check("ReloadPlanner: eine geloeschte Loesung wird geschlossen", Plan(baselines, ws, docs) is { CloseWorkspace: true, ReloadWorkspace: false });
    }

    // ---- a pull: the other copy changed the solution and a document -----------------------------------------------------------------------------

    private static void PullChecks(string root)
    {
        string work = Path.Combine(root, "pull-mine"), other = Path.Combine(root, "pull-other"), server = Path.Combine(root, "pull-server.git");
        Directory.CreateDirectory(work);
        var ws = Workspace.CreateSolution(Path.Combine(work, "P.firesln"));
        ws.CreateProject(Path.Combine(work, "App", "App.fireproj"), OutputType.Exe);
        string main = Path.Combine(work, "App", "main.fire");
        string readme = Write(work, "App/readme.md", "# App\n");
        LibGit2Sharp.Repository.Init(server, isBare: true);
        using var repo = GitRepository.Init(work);
        repo.SetIdentity("Mine", "mine@example.com");
        repo.StageAll(); repo.Commit("start");
        repo.AddRemote("origin", server); repo.Push();

        using (var clone = GitRepository.Clone(server, other))
        {
            clone.SetIdentity("Other", "other@example.com");
            var s = FireSolution.Load(Path.Combine(other, "P.firesln")); s.Folders.Add("tools"); s.Save(Path.Combine(other, "P.firesln"));
            File.WriteAllText(Path.Combine(other, "App", "main.fire"), "print(\"from the other copy\")\n");
            File.WriteAllText(Path.Combine(other, "App", "readme.md"), "# App\n\nchanged by the other copy\n");
            Write(other, "App/added.fire", "func added() { }\n");
            clone.StageAll(); clone.Commit("other"); clone.Push();
        }

        var baselines = new DiskBaselines();
        Remember(baselines, ws, main, readme);
        string head = repo.HeadSha!;
        var result = repo.Pull();
        Check("Pull: die Aenderungen der anderen Kopie sind da", result == GitPullResult.FastForward && repo.HeadSha != head && File.Exists(Path.Combine(work, "App", "added.fire")));
        var plan = Plan(baselines, ws, new[] { new OpenFileInfo(main, false), new OpenFileInfo(readme, true) });
        Check("ReloadPlanner nach Pull: die geaenderte Loesung wird neu geladen", plan.ReloadWorkspace && !plan.CloseWorkspace);
        Check("ReloadPlanner nach Pull: main.fire still, die geaenderte readme.md mit eigenen Aenderungen wird gefragt",
            plan.Documents.Contains((main, DocumentReload.Silently)) && plan.Documents.Contains((readme, DocumentReload.Ask)), string.Join(", ", plan.Documents));
        ws.Reload();
        Check("Workspace.Reload nach Pull: die neue Datei und der neue Ordner der Loesung", ws.Solution!.Folders.Contains("tools") && ws.Projects[0].Files.Any(f => f.EndsWith("added.fire")));
        Remember(baselines, ws, main, readme);
        Check("ReloadPlanner: danach ist alles bekannt", Plan(baselines, ws, new[] { new OpenFileInfo(main, false), new OpenFileInfo(readme, true) }, new ChangeBatch { Overflow = false }).IsEmpty);
    }
}
