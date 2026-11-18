using fire.Git;

/// <summary>
/// The Repository view of git (docs/tools/git.md): the lanes of the commit graph, the log with filters and pages, the branches/tags/stashes, and the operations - rebase (without and with a
/// conflict: continue, skip, abort), reset (soft, mixed, hard), cherry-pick, revert, merge, the checkout of a server's branch (a local branch that follows it), stash, tags. In temporary
/// repositories; a bare repository in a folder is the server. FIRE_TESTS_ONLY=git runs them (with GitTests and GitSyncTests).
/// </summary>
static class GitRepositoryViewTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Git-Ansicht: {title}" : $"FEHLER: Git-Ansicht: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static string Message(Action a) { try { a(); return ""; } catch (GitException ex) { return ex.Message; } }

    private static string Write(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine(root, rel)).Replace("\r\n", "\n");

    private static GitCommitInfo CommitFile(GitRepository repo, string rel, string text, string message)
    {
        repo.Stage(new[] { Write(repo.RootPath, rel, text) });
        return repo.Commit(message);
    }

    public static void Run()
    {
        Console.WriteLine("=== Git-Ansicht (Repository) ===");
        string root = Path.Combine(Path.GetTempPath(), "fire-gitview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Graph();
            Section("Rebase", () => Rebase(Path.Combine(root, "rebase")));
            Section("Reset", () => Reset(Path.Combine(root, "reset")));
            Section("Cherry-pick, Revert, Merge", () => Apply(Path.Combine(root, "apply")));
            Section("Server-Zweig", () => RemoteCheckout(root));
            Section("Stash, Tags, Zweige", () => StashTagsBranches(Path.Combine(root, "stash")));
            Section("Log", () => Log(Path.Combine(root, "log")));
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { } }
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine(_failures == 0 ? "Alle Pruefungen der Git-Ansicht bestanden." : $"FEHLER: {_failures} Pruefung(en) der Git-Ansicht fehlgeschlagen.");
    }

    private static readonly string? Filter = Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER");

    private static void Section(string name, Action body)
    {
        if (!string.IsNullOrEmpty(Filter) && !name.Contains(Filter, StringComparison.OrdinalIgnoreCase) && !"Git-Ansicht".Contains(Filter, StringComparison.OrdinalIgnoreCase)) return;
        try { body(); }
        catch (Exception ex) { _failures++; Console.WriteLine($"FEHLER: Git-Ansicht: {name}: unerwartete Ausnahme: {ex}"); }
    }

    private static GitRepository NewRepo(string folder)
    {
        var repo = GitRepository.Init(folder, writeIgnore: false);
        repo.SetIdentity("Tester", "tester@example.com");
        return repo;
    }

    // ---- the graph -------------------------------------------------------------------------------------------------------------------------

    private static IReadOnlyList<string> P(params string[] parents) => parents;

    private static void Graph()
    {
        var linear = new GitGraphLayout();
        linear.Add(new[] { ("c", P("b")), ("b", P("a")), ("a", P()) });
        Check("Graph: eine gerade Linie bleibt in Spur 0", linear.Rows.All(r => r.Lane == 0) && linear.Rows[0].Down.Single() == new GitGraphEdge(0, 0, linear.Rows[0].Color) && linear.Rows[2].Down.Count == 0
            && linear.Rows[1].Up.Count == 1 && linear.Rows.All(r => r.Width == 1));

        // m merges c1 (a branch) into b1; both come from a
        var merge = new GitGraphLayout();
        merge.Add(new[] { ("m", P("b1", "c1")), ("c1", P("a")), ("b1", P("a")), ("a", P()) });
        var rows = merge.Rows;
        Check("Graph: die Zusammenfuehrung ist ein Ring mit zwei Linien nach unten", rows[0].IsMerge && rows[0].Lane == 0 && rows[0].Down.Any(e => e.From == 0 && e.To == 0) && rows[0].Down.Any(e => e.From == 0 && e.To == 1),
            string.Join(" ", rows[0].Down));
        Check("Graph: der Zweig hat eine eigene Spur", rows[1].Lane == 1 && rows[2].Lane == 0 && rows[3].Lane == 0, string.Join(",", rows.Select(r => r.Lane)));
        Check("Graph: die Spur des Zweigs endet im gemeinsamen Vorgaenger", rows[3].Up.Any(e => e.From == 1 && e.To == 0) && rows[3].Up.Any(e => e.From == 0 && e.To == 0) && rows[3].Down.Count == 0,
            string.Join(" ", rows[3].Up));
        Check("Graph: die Linie, die an einer Zeile vorbeigeht, bleibt in ihrer Spur", rows[2].Up.Any(e => e.From == 1 && e.To == 1) && rows[1].Down.Any(e => e == rows[2].Up.First(u => u.From == 1)));
        Check("Graph: die Farbe des Zweigs ist eine andere", rows[1].Color != rows[0].Color);

        // two branch tips that are not merged: two lanes from the top
        var tips = new GitGraphLayout();
        tips.Add(new[] { ("x", P("base")), ("y", P("base")), ("base", P()) });
        Check("Graph: zwei Spitzen ohne Zusammenfuehrung, zwei Spuren", tips.Rows[0].Lane == 0 && tips.Rows[1].Lane == 1 && tips.Rows[2].Lane == 0 && tips.Rows[2].Up.Any(e => e.From == 1 && e.To == 0));

        // in pages: the same rows as in one go
        var commits = new[] { ("m", P("b1", "c1")), ("c1", P("c0")), ("b1", P("b0")), ("c0", P("a")), ("b0", P("a")), ("a", P()) };
        var whole = new GitGraphLayout(); whole.Add(commits);
        var paged = new GitGraphLayout(); paged.Add(commits.Take(3)); var second = paged.Add(commits.Skip(3));
        static string Shape(GitGraphLayout l) => string.Join("|", l.Rows.Select(r => $"{r.Sha}:{r.Lane}:{string.Join(",", r.Up.Select(e => $"{e.From}>{e.To}"))}:{string.Join(",", r.Down.Select(e => $"{e.From}>{e.To}"))}"));
        Check("Graph: seitenweise ergibt dasselbe wie auf einmal", Shape(whole) == Shape(paged), Shape(whole) + "\n" + Shape(paged));
        Check("Graph: die zweite Seite liefert auch die letzte Zeile der ersten (ihre Linien nach unten)", second.Count == 4 && second[0].Sha == "b1");
    }

    // ---- rebase ----------------------------------------------------------------------------------------------------------------------------

    private static void Rebase(string folder)
    {
        using var repo = NewRepo(folder);
        CommitFile(repo, "shared.txt", "one\ntwo\nthree\n", "base");
        string main = repo.BranchName;
        repo.CreateBranch("feature");
        var f1 = CommitFile(repo, "feature.txt", "feature\n", "feature work");
        CommitFile(repo, "feature2.txt", "more\n", "more feature work");
        repo.CheckoutBranch(main);
        var m1 = CommitFile(repo, "main.txt", "main\n", "main work");
        repo.CheckoutBranch("feature");

        var result = repo.Rebase(main);
        var log = repo.History().Entries;
        var head = log.First(e => e.Refs.Contains("feature"));
        var parentOfFirst = log.First(e => e.Commit.Summary == "feature work").Parents[0];
        Check("Rebase: ohne Konflikt fertig, beide Commits stehen auf dem anderen Zweig", result.Completed && result.Applied == 2 && repo.BranchName == "feature" && parentOfFirst == m1.Sha && repo.Operation == GitOperationKind.None,
            $"{result} parent={parentOfFirst} m1={m1.Sha}");
        Check("Rebase: die Dateien beider Zweige sind da, die Commits sind neu", File.Exists(Path.Combine(folder, "main.txt")) && File.Exists(Path.Combine(folder, "feature2.txt")) && head.Commit.Sha != f1.Sha
            && repo.Refs().Branches.Single(b => b.Name == main).Sha == m1.Sha);
        Check("Rebase: schon auf dem Stand ist nichts zu tun", repo.Rebase(main) is { Completed: true, Steps: 0 });

        // a conflict: both change the same line
        repo.CheckoutBranch(main);
        CommitFile(repo, "shared.txt", "one\nTWO on main\nthree\n", "main changes two");
        repo.CheckoutBranch("feature");
        var before = CommitFile(repo, "shared.txt", "one\ntwo on feature\nthree\n", "feature changes two");
        CommitFile(repo, "after.txt", "after\n", "after the conflict");
        string featureTip = repo.HeadSha!;

        Write(folder, "feature.txt", "dirty\n");
        Check("Rebase: mit Aenderungen in den Dateien abgelehnt, mit Erklaerung", Message(() => repo.Rebase(main)).Contains("clean"), Message(() => repo.Rebase(main)));
        repo.Discard(new[] { Path.Combine(folder, "feature.txt") });

        var stopped = repo.Rebase(main);
        Check("Rebase: haelt beim Konflikt an (Schritt, Commit)", stopped.Stopped && repo.Operation == GitOperationKind.Rebase && stopped.StoppedAt != null && stopped.StoppedAt.Contains("feature changes two")
            && Read(folder, "shared.txt").Contains("<<<<<<<") && repo.Status().Any(c => c.IsConflicted), stopped.ToString());
        var refs = repo.Refs();
        Check("Rebase: der Zustand nennt Schritt und Commit", refs.Operation == GitOperationKind.Rebase && refs.RebaseSteps >= 2 && refs.RebaseStopped != null, $"{refs.RebaseStep}/{refs.RebaseSteps} {refs.RebaseStopped}");
        Check("Rebase: Weiter mit ungeloesten Konflikten ist ein Fehler", Message(() => repo.RebaseContinue()).Contains("conflict"));
        Check("Rebase: ein anderer Rebase, ein Merge waehrenddessen abgelehnt", Message(() => repo.Rebase(main)).Contains("not finished") && Message(() => repo.Merge(main)).Contains("not finished"));
        repo.RebaseAbort();
        Check("Rebase: Abbrechen stellt den Zweig wieder her", repo.Operation == GitOperationKind.None && repo.BranchName == "feature" && repo.HeadSha == featureTip && Read(folder, "shared.txt") == "one\ntwo on feature\nthree\n" && repo.Status().Count == 0,
            repo.HeadSha + " " + repo.BranchName);

        repo.Rebase(main);
        Write(folder, "shared.txt", "one\ntwo on both\nthree\n");
        repo.Stage(new[] { Path.Combine(folder, "shared.txt") });
        var done = repo.RebaseContinue();
        Check("Rebase: geloest, vorgemerkt, Weiter - fertig", done.Completed && repo.Operation == GitOperationKind.None && repo.BranchName == "feature" && Read(folder, "shared.txt") == "one\ntwo on both\nthree\n"
            && File.Exists(Path.Combine(folder, "after.txt")) && Summaries(repo, 5).SequenceEqual(new[] { "after the conflict", "feature changes two", "more feature work", "feature work", "main changes two" }),
            string.Join(" / ", Summaries(repo, 6)));

        // skip: the commit of the step is left out
        repo.CheckoutBranch(main);
        CommitFile(repo, "skip.txt", "from main\n", "main adds skip");
        repo.CheckoutBranch("feature");
        CommitFile(repo, "skip.txt", "from feature\n", "feature adds skip");
        CommitFile(repo, "last.txt", "last\n", "last one");
        var again = repo.Rebase(main);
        Check("Rebase (Ueberspringen): haelt an", again.Stopped && again.StoppedAt != null && again.StoppedAt.Contains("feature adds skip"), again.ToString());
        var skipped = again.Stopped ? repo.RebaseSkip() : again;
        var summaries = Summaries(repo, 4);
        Check("Rebase: Ueberspringen laesst den Commit weg und macht weiter", skipped.Completed && repo.Operation == GitOperationKind.None && summaries[0] == "last one" && !summaries.Contains("feature adds skip")
            && Read(folder, "skip.txt") == "from main\n" && repo.Status().Count == 0, string.Join(" / ", summaries) + " | " + string.Join(",", repo.Status().Select(c => c.Path)));

        // onto a commit (not a branch): no branch of the editor is left behind
        var target = repo.Log(10).First(c => c.Summary == "main work");
        repo.CheckoutBranch(main);
        repo.CreateBranch("small");
        CommitFile(repo, "small.txt", "s\n", "small");
        var onto = repo.Rebase(target.Sha);
        Check("Rebase: auf einen Commit (ohne Zweig) - fertig, kein Hilfszweig bleibt", onto.Completed && !repo.Refs().Branches.Any(b => b.Name.StartsWith("fire-rebase")), onto.ToString());
    }

    /// <summary>The first messages of the current branch, newest first (each commit before its parents).</summary>
    private static List<string> Summaries(GitRepository repo, int n) => repo.Log(n).Select(c => c.Summary).ToList();

    // ---- reset -----------------------------------------------------------------------------------------------------------------------------

    private static void Reset(string folder)
    {
        using var repo = NewRepo(folder);
        var c1 = CommitFile(repo, "a.txt", "1\n", "one");
        var c2 = CommitFile(repo, "a.txt", "2\n", "two");
        var c3 = CommitFile(repo, "b.txt", "b\n", "three");

        var preview = repo.PreviewReset(c1.Sha);
        Check("Reset: die Vorschau nennt die Commits, die den Zweig verlassen", preview.Leaving.Count == 2 && preview.Leaving[0].Commit.Sha == c3.Sha && preview.Leaving.All(l => !l.StillElsewhere) && preview.ChangedFiles.Count == 0,
            string.Join(",", preview.Leaving.Select(l => l.Commit.Summary)));
        repo.CreateTag("keep", c3.Sha);
        Check("Reset: ein Commit mit Tag ist 'noch anderswo'", repo.PreviewReset(c1.Sha).Leaving.All(l => l.StillElsewhere));
        repo.DeleteTag("keep");

        repo.Reset(c2.Sha, GitResetMode.Soft);
        var s = repo.Status();
        Check("Reset soft: der Zweig steht auf dem Commit, die Aenderung ist vorgemerkt", repo.HeadSha == c2.Sha && s.Single().Path == "b.txt" && s.Single().Staged == GitFileState.Added && File.Exists(Path.Combine(folder, "b.txt")));

        repo.Commit("three again");
        repo.Reset(c2.Sha, GitResetMode.Mixed);
        s = repo.Status();
        Check("Reset mixed: die Datei bleibt, nichts ist vorgemerkt", repo.HeadSha == c2.Sha && s.Single().Path == "b.txt" && s.Single().Staged == GitFileState.Unmodified && s.Single().Unstaged == GitFileState.Untracked);

        repo.Stage(new[] { Path.Combine(folder, "b.txt") }); repo.Commit("three once more");
        Write(folder, "a.txt", "changed, not committed\n");
        Write(folder, "untracked.txt", "u\n");
        var hardPreview = repo.PreviewReset(c1.Sha);
        Check("Reset: die Vorschau nennt die geaenderten Dateien (die hart verloren gehen)", hardPreview.ChangedFiles.SequenceEqual(new[] { "a.txt" }) && hardPreview.Leaving.Count == 2, string.Join(",", hardPreview.ChangedFiles));
        repo.Reset(c1.Sha, GitResetMode.Hard);
        Check("Reset hard: die Dateien sind wie im Commit, Unversioniertes bleibt", repo.HeadSha == c1.Sha && Read(folder, "a.txt") == "1\n" && !File.Exists(Path.Combine(folder, "b.txt")) && File.Exists(Path.Combine(folder, "untracked.txt")));
        repo.Reset(c3.Sha, GitResetMode.Hard);
        Check("Reset: auch nach vorn (zu einem Commit, den der Zweig nicht hat)", repo.HeadSha == c3.Sha && File.Exists(Path.Combine(folder, "b.txt")));
    }

    // ---- cherry-pick, revert, merge --------------------------------------------------------------------------------------------------------

    private static void Apply(string folder)
    {
        using var repo = NewRepo(folder);
        CommitFile(repo, "text.txt", "a\nb\nc\n", "base");
        string main = repo.BranchName;
        repo.CreateBranch("other");
        var pick = CommitFile(repo, "picked.txt", "picked\n", "a commit to pick");
        var clash = CommitFile(repo, "text.txt", "a\nB from other\nc\n", "other changes b");
        repo.CheckoutBranch(main);
        CommitFile(repo, "text.txt", "a\nB from main\nc\n", "main changes b");

        var r = repo.CherryPick(pick.Sha);
        Check("Cherry-pick: ein neuer Commit mit den Aenderungen", r == GitApplyResult.Committed && File.Exists(Path.Combine(folder, "picked.txt")) && repo.Log(1)[0].Summary == "a commit to pick" && repo.HeadSha != pick.Sha);
        var conflict = repo.CherryPick(clash.Sha);
        Check("Cherry-pick: ein Konflikt haelt an", conflict == GitApplyResult.Conflicts && repo.Operation == GitOperationKind.CherryPick && repo.Status().Any(c => c.IsConflicted) && repo.PreparedMessage()!.Contains("other changes b"));
        repo.AbortOperation();
        Check("Cherry-pick: Abbrechen stellt den Stand wieder her", repo.Operation == GitOperationKind.None && repo.Status().Count == 0 && Read(folder, "text.txt") == "a\nB from main\nc\n");
        repo.CherryPick(clash.Sha);
        Write(folder, "text.txt", "a\nB from both\nc\n");
        repo.Stage(new[] { Path.Combine(folder, "text.txt") });
        var finished = repo.FinishOperation();
        Check("Cherry-pick: geloest und abgeschlossen, mit der vorbereiteten Nachricht", repo.Operation == GitOperationKind.None && finished.Summary == "other changes b" && repo.Status().Count == 0, finished.Summary);

        var head = repo.HeadSha;
        var revert = repo.Revert(repo.Log(10).First(c => c.Summary == "a commit to pick").Sha);
        Check("Revert: ein Commit, der die Aenderungen zuruecknimmt", revert == GitApplyResult.Committed && !File.Exists(Path.Combine(folder, "picked.txt")) && repo.Log(1)[0].Summary.StartsWith("Revert") && repo.Log(2)[1].Sha == head);

        repo.CheckoutBranch("other");
        CommitFile(repo, "other-only.txt", "o\n", "only on other");
        repo.CheckoutBranch(main);
        var merged = repo.Merge("other");
        Check("Merge: Konflikt in text.txt haelt an", merged == GitApplyResult.Conflicts && repo.Operation == GitOperationKind.Merge && repo.IsMerging);
        Write(folder, "text.txt", "a\nmerged\nc\n");
        repo.Stage(new[] { Path.Combine(folder, "text.txt") });
        var m = repo.FinishOperation();
        Check("Merge: abgeschlossen, ein Commit mit zwei Eltern", repo.Operation == GitOperationKind.None && repo.CommitDetails(m.Sha).Parents.Count == 2 && File.Exists(Path.Combine(folder, "other-only.txt")) && m.Summary.Contains("other"), m.Summary);
        repo.CreateBranch("ff", checkout: false);
        repo.CheckoutBranch("ff");
        CommitFile(repo, "ff.txt", "ff\n", "fast");
        repo.CheckoutBranch(main);
        Check("Merge: vorspulen, wenn es geht", repo.Merge("ff") == GitApplyResult.FastForward && File.Exists(Path.Combine(folder, "ff.txt")) && repo.Merge("ff") == GitApplyResult.UpToDate);
        repo.CreateBranch("side", checkout: true);
        CommitFile(repo, "side.txt", "side\n", "side work");
        repo.CheckoutBranch(main);
        CommitFile(repo, "main2.txt", "m\n", "main work 2");
        Check("Merge: ohne Konflikt ein Commit mit Nachricht", repo.Merge("side") == GitApplyResult.Committed && repo.Log(1)[0].Summary == $"Merge branch 'side' into {main}", repo.Log(1)[0].Summary);
        var details = repo.CommitDetails(repo.Log(10).First(c => c.Summary == "side work").Sha);
        Check("Details: die Dateien eines Commits und der Unterschied einer Datei", details.Files.Single().Path == "side.txt" && details.Files.Single().State == GitFileState.Added && details.Refs.Contains("side")
            && repo.DiffCommitFile(details.Commit.Sha, "side.txt").Contains("+side"));
    }

    // ---- a server's branch -----------------------------------------------------------------------------------------------------------------

    private static void RemoteCheckout(string root)
    {
        string server = Path.Combine(root, "server.git");
        LibGit2Sharp.Repository.Init(server, isBare: true);
        using (var first = NewRepo(Path.Combine(root, "first")))
        {
            CommitFile(first, "readme.txt", "hello\n", "first");
            first.AddRemote("origin", server);
            first.Push();
            first.CreateBranch("topic");
            CommitFile(first, "topic.txt", "topic\n", "on topic");
            first.Push();
        }
        using var second = GitRepository.Clone(server, Path.Combine(root, "second"));
        second.SetIdentity("Second", "second@example.com");
        var refs = second.Refs();
        Check("Server-Zweige: nach Server gruppiert", refs.Remotes.ContainsKey("origin") && refs.Remotes["origin"].Any(b => b.Name == "origin/topic" && b.ShortName == "topic") && !refs.Branches.Any(b => b.Name == "topic"),
            string.Join(",", refs.Remotes.SelectMany(kv => kv.Value.Select(b => b.Name))));
        string local = second.CheckoutRemoteBranch("origin/topic");
        refs = second.Refs();
        var topic = refs.Branches.Single(b => b.Name == "topic");
        Check("Server-Zweig auschecken: ein lokaler Zweig, der ihm folgt", local == "topic" && second.BranchName == "topic" && topic.IsCurrent && topic.Upstream == "origin/topic" && File.Exists(Path.Combine(root, "second", "topic.txt")));
        string main = refs.Branches.Single(b => b.Name != "topic").Name;
        second.CheckoutBranch(main);
        Check("Server-Zweig auschecken: nochmal wechselt zum vorhandenen lokalen Zweig", second.CheckoutBranch("origin/topic") == "topic" && second.Refs().Branches.Count == 2 && second.BranchName == "topic");
        second.CheckoutBranch(main);
        second.CreateBranch("clash", checkout: false);
        Check("Server-Zweig: ein lokaler Zweig gleichen Namens, der etwas anderem folgt, ist ein Fehler mit Erklaerung",
            Message(() => second.CheckoutRemoteBranch("origin/topic", "clash")).Contains("does not follow"));
        Check("Server-Zweig: ein anderer Name fuer den lokalen Zweig", second.CheckoutRemoteBranch("origin/topic", "topic2") == "topic2" && second.Refs().Branches.Single(b => b.Name == "topic2").Upstream == "origin/topic");

        // ahead / behind, reset to the server's branch
        CommitFile(second, "local.txt", "l\n", "local only");
        var t2 = second.Refs().Branches.Single(b => b.Name == "topic2");
        Check("Zweige: voraus/zurueck gegenueber dem Server-Zweig", t2.Ahead == 1 && t2.Behind == 0 && second.Upstream == "origin/topic");
        second.Reset(second.Upstream!, GitResetMode.Hard);
        Check("Reset auf den Server-Zweig", second.Refs().Branches.Single(b => b.Name == "topic2").Ahead == 0 && !File.Exists(Path.Combine(root, "second", "local.txt")));

        // delete on the server
        second.CheckoutBranch(main);
        second.DeleteRemoteBranch("origin/topic");
        second.FetchAll();
        Check("Server-Zweig loeschen: weg auf dem Server und hier", !second.Refs().Remotes["origin"].Any(b => b.Name == "origin/topic"));
    }

    // ---- stash, tags, branches -------------------------------------------------------------------------------------------------------------

    private static void StashTagsBranches(string folder)
    {
        using var repo = NewRepo(folder);
        var c1 = CommitFile(repo, "a.txt", "1\n", "one");
        Write(folder, "a.txt", "changed\n");
        Write(folder, "new.txt", "new\n");
        Check("Stash: speichern raeumt die Dateien auf (auch neue)", repo.StashSave("my work") && repo.Status().Count == 0 && Read(folder, "a.txt") == "1\n" && !File.Exists(Path.Combine(folder, "new.txt")));
        Check("Stash: nichts zu speichern", !repo.StashSave());
        var stashes = repo.Refs().Stashes;
        Check("Stash: in der Liste, mit Nachricht", stashes.Count == 1 && stashes[0].Message.Contains("my work") && repo.DiffStash(0).Contains("+changed"));
        Check("Stash: anwenden bringt es zurueck, der Stash bleibt", repo.StashApply(0) == GitStashResult.Applied && Read(folder, "a.txt") == "changed\n" && File.Exists(Path.Combine(folder, "new.txt")) && repo.Stashes().Count == 1);
        repo.Discard(repo.Status().Select(c => c.FullPath));
        Check("Stash: pop bringt es zurueck und nimmt es weg", repo.StashPop(0) == GitStashResult.Applied && Read(folder, "a.txt") == "changed\n" && repo.Stashes().Count == 0);
        repo.StashSave("again");
        repo.StashDrop(0);
        Check("Stash: verwerfen", repo.Stashes().Count == 0 && Message(() => repo.StashDrop(0)).Contains("no stash"));

        var c2 = CommitFile(repo, "a.txt", "2\n", "two");
        repo.CreateTag("v1", c1.Sha);
        repo.CreateTag("v2", c2.Sha, "release two");
        var tags = repo.Refs().Tags;
        Check("Tags: leicht und mit Nachricht", tags.Count == 2 && tags[0].Sha == c1.Sha && tags[1].Message == "release two" && Message(() => repo.CreateTag("v1", c2.Sha)).Contains("already") && Message(() => repo.CreateTag("bad name", c2.Sha)).Length > 0);
        Check("Tags: im Log an ihrem Commit", repo.History().Entries.Single(e => e.Commit.Sha == c1.Sha).Refs.Contains("tag: v1"));
        repo.DeleteTag("v1");
        Check("Tags: loeschen", repo.Refs().Tags.Single().Name == "v2");

        string main = repo.BranchName;
        repo.CreateBranchAt("old", c1.Sha, checkout: true);
        Check("Zweig an einem Commit, gewechselt", repo.BranchName == "old" && repo.HeadSha == c1.Sha && Read(folder, "a.txt") == "1\n");
        CommitFile(repo, "only-old.txt", "x\n", "only on old");
        repo.CheckoutBranch(main);
        Check("Zweig loeschen: nicht zusammengefuehrt abgelehnt, mit Erklaerung", Message(() => repo.DeleteBranch("old")).Contains("lost") && Message(() => repo.DeleteBranch(main)).Contains("current"));
        repo.DeleteBranch("old", force: true);
        Check("Zweig loeschen: erzwungen", !repo.Refs().Branches.Any(b => b.Name == "old"));
        repo.CreateBranchAt("merged", c1.Sha, checkout: false);
        repo.DeleteBranch("merged");
        Check("Zweig loeschen: ein zusammengefuehrter ohne Frage", !repo.Refs().Branches.Any(b => b.Name == "merged"));

        repo.CheckoutCommit(c1.Sha);
        var refs = repo.Refs();
        Check("Commit auschecken: losgeloester Kopf", repo.IsDetached && refs.IsDetached && refs.HeadSha == c1.Sha && repo.History().Entries.Single(e => e.Commit.Sha == c1.Sha).Refs.Contains("HEAD"));
        repo.CheckoutBranch(main);
        Check("Zurueck auf den Zweig", !repo.IsDetached && repo.BranchName == main);
    }

    // ---- the log ---------------------------------------------------------------------------------------------------------------------------

    private static void Log(string folder)
    {
        using var repo = NewRepo(folder);
        for (int i = 0; i < 25; i++) CommitFile(repo, i % 5 == 0 ? "src/five.txt" : "other.txt", $"{i}\n", i % 2 == 0 ? $"even {i}" : $"odd {i}");
        var page1 = repo.History(take: 10);
        var page2 = repo.History(start: page1.Next!.Value, take: 10);
        var page3 = repo.History(start: page2.Next!.Value, take: 10);
        Check("Log: seitenweise, ohne Luecke und ohne Doppel", page1.Entries.Count == 10 && page2.Entries.Count == 10 && page3.Entries.Count == 5 && page3.Next == null
            && page1.Entries.Concat(page2.Entries).Concat(page3.Entries).Select(e => e.Commit.Sha).Distinct().Count() == 25 && page1.Entries[0].Commit.Summary == "even 24");
        Check("Log: Filter nach Text", repo.History(new GitLogFilter(Text: "odd")).Entries.Count == 12);
        Check("Log: Filter nach Autor", repo.History(new GitLogFilter(Author: "tester@")).Entries.Count == 25 && repo.History(new GitLogFilter(Author: "nobody")).Entries.Count == 0);
        int file = repo.History(new GitLogFilter(Path: "src/five.txt")).Entries.Count, dir = repo.History(new GitLogFilter(Path: "src")).Entries.Count, full = repo.History(new GitLogFilter(Path: Path.Combine(folder, "src"))).Entries.Count;
        Check("Log: Filter nach Pfad (Datei und Ordner)", file == 5 && dir == 5 && full == 5, $"{file} {dir} {full}");
        var filtered = repo.History(new GitLogFilter(Text: "even"), take: 5);
        var rest = repo.History(new GitLogFilter(Text: "even"), start: filtered.Next!.Value, take: 50);
        Check("Log: gefiltert und seitenweise", filtered.Entries.Count == 5 && rest.Entries.Count == 8 && rest.Next == null);
    }
}
