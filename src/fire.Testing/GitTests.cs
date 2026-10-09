using fire.Git;

/// <summary>Git for the editor (docs/GIT.md): status, commit, history, diff, branches, and the server (a local folder serves as the server).</summary>
static class GitTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Git: {title}" : $"FEHLER: Git: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    private static string Write(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    private static bool Throws(Action a) { try { a(); return false; } catch (GitException) { return true; } }
    private static string Message(Action a) { try { a(); return ""; } catch (GitException ex) { return ex.Message; } }

    public static void Run()
    {
        Console.WriteLine("=== Git ===");
        string root = Path.Combine(Path.GetTempPath(), "fire-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { RunIn(root); }
        catch (Exception ex) { _failures++; Console.WriteLine("FEHLER: Git: unerwartete Ausnahme: " + ex); }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { try { File.SetAttributes(f, FileAttributes.Normal); } catch (IOException) { } }
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine(_failures == 0 ? "Alle Git-Pruefungen bestanden." : $"FEHLER: {_failures} Git-Pruefung(en) fehlgeschlagen.");
    }

    private static void RunIn(string root)
    {
        string work = Path.Combine(root, "solution");
        Check("Discover: ausserhalb eines Repositorys nichts", GitRepository.Discover(work) == null);
        using var repo = GitRepository.Init(work);
        Check("Init: Ordner, Repository und .gitignore", GitRepository.Discover(work) == Path.GetFullPath(work) && File.ReadAllText(Path.Combine(work, ".gitignore")).Contains("bin/") && !repo.HasCommits);
        Check("Discover: eine Datei im Unterordner findet das Repository", GitRepository.Discover(Path.Combine(work, "a", "b", "c.script")) == Path.GetFullPath(work));

        string main = Write(work, "App/main.script", "print(1)\n");
        Write(work, "App/bin/out.exe", "binary");
        var status = repo.Status();
        Check("Status: neue Dateien sind 'untracked', Ignoriertes (bin/) fehlt", status.Any(c => c.Path == "App/main.script" && c.Unstaged == GitFileState.Untracked) && !status.Any(c => c.Path.Contains("bin/")), string.Join(",", status.Select(c => c.Path)));

        repo.Stage(new[] { main });
        if (repo.Identity().Name == null)   // (a machine with a configured git user has an identity already)
            Check("Commit: ohne Name und Adresse eine verstaendliche Meldung", Message(() => repo.Commit("x")).Contains("name"), Message(() => repo.Commit("x")));
        repo.SetIdentity("Tester", "tester@example.com");
        Check("Identitaet: wird im Repository gemerkt", repo.Identity() == ("Tester", "tester@example.com"));
        Check("Discover: der Ordner ohne Schraegstrich am Ende", !repo.RootPath.EndsWith(Path.DirectorySeparatorChar));
        repo.StageAll();
        Check("Stage: nach dem Vormerken steht die Datei als 'added'", repo.Status().Any(c => c.Path == "App/main.script" && c.Staged == GitFileState.Added));
        var first = repo.Commit("first commit\n\nwith a body");
        Check("Commit: der erste Commit, der Status ist danach leer", first.Summary == "first commit" && first.Author == "Tester" && repo.HasCommits && repo.Status().Count == 0);
        Check("Commit: nichts vorgemerkt ist ein Fehler", Throws(() => repo.Commit("again")));
        Check("Commit: eine leere Nachricht ist ein Fehler", Throws(() => repo.Commit("  ")));

        File.WriteAllText(main, "print(1)\nprint(2)\n");
        Check("Status: eine geaenderte Datei", repo.Status().Single().Unstaged == GitFileState.Modified);
        string diff = repo.Diff(main, staged: false);
        Check("Diff: die neue Zeile steht mit + im Unterschied", diff.Contains("+print(2)") && !diff.Contains("-print(1)"), diff);
        repo.Stage(new[] { main });
        Check("Diff: vorgemerkt gegen den letzten Commit", repo.Diff(main, staged: true).Contains("+print(2)") && repo.Diff(main, staged: false) == "");
        string fresh = Write(work, "App/new.script", "print(3)\n");
        Check("Diff: eine unversionierte Datei erscheint als neu", repo.Diff(fresh, staged: false).Contains("+print(3)"));
        var second = repo.Commit("second");
        var log = repo.Log();
        Check("Log: neueste zuerst", log.Count == 2 && log[0].Summary == "second" && log[1].Summary == "first commit");
        Check("Log: die Historie einer Datei", repo.Log(path: main).Count == 2 && repo.Log(path: fresh).Count == 0);
        Check("DiffCommit: was ein Commit geaendert hat", repo.DiffCommit(second.Sha).Contains("+print(2)") && repo.DiffCommit(first.Sha).Contains("+print(1)"));

        File.WriteAllText(main, "broken\n");
        repo.Discard(new[] { main });
        Check("Discard: die Datei ist wieder wie im Commit", File.ReadAllText(main).Replace("\r\n", "\n") == "print(1)\nprint(2)\n" && repo.Status().Count == 1);
        repo.Discard(new[] { fresh });
        Check("Discard: eine unversionierte Datei (new.script) wird geloescht", !File.Exists(fresh));
        string added = Write(work, "added.txt", "a");
        repo.Stage(new[] { added }); repo.Discard(new[] { added });
        Check("Discard: eine nur vorgemerkte Datei bleibt, ist aber nicht mehr vorgemerkt", File.Exists(added) && repo.Status().Single(c => c.Path == "added.txt").Unstaged == GitFileState.Untracked);
        File.Delete(added);
        string untracked = Write(work, "temp.txt", "t");
        repo.Discard(new[] { untracked });
        Check("Discard: eine unversionierte Datei wird geloescht", !File.Exists(untracked));

        // branches
        string defaultBranch = repo.BranchName;
        repo.CreateBranch("feature");
        Check("Zweig: neu und gewechselt", repo.BranchName == "feature" && repo.Branches().Count(b => b.IsCurrent) == 1 && repo.Branches().Any(b => b.Name == defaultBranch));
        File.WriteAllText(main, "print(1)\nprint(2)\nprint(\"feature\")\n");
        repo.Stage(new[] { main }); repo.Commit("on feature");
        repo.Checkout(defaultBranch);
        Check("Zweig: zurueckwechseln stellt die Dateien des Zweigs her", repo.BranchName == defaultBranch && !File.ReadAllText(main).Contains("feature"));
        Check("Zweig: ein ungueltiger Name und ein unbekannter Zweig", Throws(() => repo.CreateBranch("bad name")) && Throws(() => repo.Checkout("nowhere")));
        File.WriteAllText(main, "local change\n");
        Check("Zweig: Wechsel mit Aenderungen, die verloren gingen, ist ein Fehler mit Erklaerung", Message(() => repo.Checkout("feature")).Contains("overwritten"), Message(() => repo.Checkout("feature")));
        repo.Discard(new[] { main });

        // the server: a bare repository in a folder
        string serverPath = Path.Combine(root, "server.git");
        LibGit2Sharp.Repository.Init(serverPath, isBare: true);
        Check("Server: ssh-Adressen werden mit Erklaerung abgelehnt", Message(() => repo.AddRemote("origin", "git@github.com:me/x.git")).Contains("https"));
        Check("Push: ohne Server eine Meldung", Message(() => repo.Push()).Contains("server"));
        repo.AddRemote("origin", serverPath);
        Check("Server: der Server steht in der Liste", repo.Remotes().Single() == ("origin", serverPath));
        repo.Push();
        Check("Push: der Zweig folgt danach dem Server", repo.Branches().Single(b => b.IsCurrent).HasUpstream);

        // a second copy: clone, change, push; the first pulls
        string other = Path.Combine(root, "other");
        using (var clone = GitRepository.Clone(serverPath, other))
        {
            clone.SetIdentity("Other", "other@example.com");
            Check("Clone: die Dateien und die Historie sind da", File.Exists(Path.Combine(other, "App", "main.script")) && clone.Log().Count == 2);
            Write(other, "App/other.script", "print(\"from other\")\n");
            clone.StageAll(); clone.Commit("from the other copy");
            clone.Push();
        }
        Check("Pull: vorher ist der Server voraus (nach Fetch)", Run(() => repo.Fetch()) && repo.Branches().Single(b => b.IsCurrent).Behind == 1);
        var pulled = repo.Pull();
        Check("Pull: schnelle Zusammenfuehrung bringt die Datei", pulled == GitPullResult.FastForward && File.Exists(Path.Combine(work, "App", "other.script")) && repo.Log()[0].Summary == "from the other copy");
        Check("Pull: nochmal ist nichts neu", repo.Pull() == GitPullResult.UpToDate);

        // a conflict: both change the same line
        File.WriteAllText(main, "print(1)\nprint(\"mine\")\n"); repo.Stage(new[] { main }); repo.Commit("mine");
        using (var clone = GitRepository.Open(other))
        {
            clone.Pull();
            File.WriteAllText(Path.Combine(other, "App", "main.script"), "print(1)\nprint(\"theirs\")\n"); clone.StageAll(); clone.Commit("theirs"); clone.Push();
        }
        Check("Push: ein Server mit neuen Aenderungen weist ab, mit Erklaerung", Message(() => repo.Push()).Contains("pull"), Message(() => repo.Push()));
        var conflict = repo.Pull();
        Check("Pull: dieselbe Zeile auf beiden Seiten ist ein Konflikt", conflict == GitPullResult.Conflicts && repo.IsMerging && repo.Status().Any(c => c.Path == "App/main.script" && c.IsConflicted) && File.ReadAllText(main).Contains("<<<<<<<"));
        File.WriteAllText(main, "print(1)\nprint(\"both\")\n");
        repo.Stage(new[] { main });
        var merge = repo.Commit("merged");
        Check("Pull: nach dem Loesen und Commit ist der Konflikt vorbei", !repo.IsMerging && merge.Summary == "merged" && repo.Status().Count == 0);
        repo.Push();
        Check("Push: nach dem Zusammenfuehren geht es", repo.Branches().Single(b => b.IsCurrent).Ahead == 0);

        var map = repo.StateMap();
        File.AppendAllText(main, "x\n");
        Check("StateMap: geaenderte Dateien nach vollem Pfad", repo.StateMap().TryGetValue(Path.GetFullPath(main), out var st) && st == GitFileState.Modified && map.Count == 0);
    }

    private static bool Run(Action a) { a(); return true; }
}
