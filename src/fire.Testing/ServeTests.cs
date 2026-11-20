using System.Collections.Concurrent;
using fire.Compiler;
using fire.Editor;

/// <summary>The language services over the protocol (docs/spec/FORGE_SERVE.md; `forge-c serve`, native/forge/serve*.c, the client ServeClient): the scheduling of the
/// diagnostics on the server (debounce, latest wins, versions in order, the stale flag, a result remembered for the same texts), the documents (whole texts, edits, a resync),
/// the context of a project (the other files and their overlay by open documents), the errors of the protocol, and the diagnostics compared with LiveDiagnostics of spark's own
/// process. The completion runs over the protocol in CompletionTests (FIRE_TESTS_ONLY=completion), the markup completion in the markup checks. Alone: FIRE_TESTS_ONLY=serve;
/// the scheduling part also with FIRE_TESTS_ONLY=editorchecks.</summary>
static class ServeTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: forge serve: {title}" : $"FEHLER: forge serve: {title}" + (detail == null ? "" : $"\n  {detail}"));
    }

    private static string Folder => Path.Combine(Path.GetTempPath(), $"fire-serve-tests-{Environment.ProcessId}");

    /// <summary>A server of its own (the debounce given), its pushes collected.</summary>
    private sealed class Session : IDisposable
    {
        public readonly ServeClient Client;
        public readonly BlockingCollection<ServeDiagnostics> Pushed = new();
        public readonly ConcurrentQueue<string> Resyncs = new();

        public Session(string exe, int debounce)
        {
            Client = ServeClient.Start(exe, debounceMs: debounce);
            Client.Diagnostics += d => Pushed.Add(d);
            Client.Resync += p => Resyncs.Enqueue(p);
        }

        /// <summary>The pushes for `path` until one for `version` came (or the time is up).</summary>
        public List<ServeDiagnostics> Until(string path, int version, int timeoutMs = 20000)
        {
            var got = new List<ServeDiagnostics>();
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < end)
            {
                if (!Pushed.TryTake(out var d, 50)) continue;
                if (d.Path != path) continue;
                got.Add(d);
                if (d.Version >= version) break;
            }
            return got;
        }

        /// <summary>What comes for `path` within `ms` (to see that nothing more comes).</summary>
        public List<ServeDiagnostics> During(string path, int ms)
        {
            var got = new List<ServeDiagnostics>();
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end) if (Pushed.TryTake(out var d, 20) && d.Path == path) got.Add(d);
            return got;
        }

        public void Dispose() => Client.Dispose();
    }

    private static string? Exe(out string error)
    {
        error = "";
        if (ServeClient.InProcess) { error = "FIRE_SPARK_LANG=inproc"; return null; }
        return ServeClient.FindExecutable(out error);
    }

    /// <summary>The scheduling of the diagnostics and the documents (no packages needed).</summary>
    public static int RunProtocol()
    {
        Console.WriteLine("=== forge serve: Protokoll (Entprellen, latest wins, Versionen, Dokumente, Projekt) ===");
        _failures = 0;
        var exe = Exe(out string why);
        if (exe == null) { Check("forge-c (native/forge) gefunden oder gebaut", ServeClient.InProcess, why); return _failures; }
        Directory.CreateDirectory(Folder);
        try
        {
            using var s = new Session(exe, 150);
            Check("initialize: die Faehigkeiten", s.Client.Capabilities.Contains("diagnostics") && s.Client.Capabilities.Contains("completion"), string.Join(",", s.Client.Capabilities));
            Scheduling(s);
            Documents(s);
            Projects(s);
            Errors(s);
        }
        catch (Exception ex) { Check("Protokoll", false, ex.ToString()); }
        Console.WriteLine(_failures == 0 ? "Alle Pruefungen von forge serve bestanden." : $"FEHLER: {_failures} Pruefung(en) von forge serve fehlgeschlagen.");
        return _failures;
    }

    private static void Scheduling(Session s)
    {
        string path = Path.Combine(Folder, "debounce.fire");
        s.Client.Open(path, 1, "var a = 1\n");
        for (int v = 2; v <= 6; v++) s.Client.ChangeText(path, v, $"var a = 1\nvar b{v} = missing{v}\n");
        var got = s.Until(path, 6);
        Check("Entprellen: schnelle Aenderungen - nur die neueste wird geprueft und geschickt", got.Count == 1 && got[0].Version == 6 && !got[0].Stale,
            string.Join(" | ", got.Select(g => $"v{g.Version} stale={g.Stale}")));
        Check("Entprellen: die Diagnose gehoert zum Text dieser Version", got.Count == 1 && got[0].Items.Count == 1 && got[0].Items[0].Message.Contains("missing6") && got[0].Items[0].Line == 2,
            got.Count == 0 ? "nichts" : string.Join(" | ", got[0].Items.Select(i => $"{i.Line}: {i.Message}")));
        Check("Entprellen: danach kommt nichts mehr", s.During(path, 500).Count == 0);

        // a change after the push: the next version comes, versions only grow
        s.Client.ChangeText(path, 7, "var a = 1\n");
        var after = s.Until(path, 7);
        Check("eine spaetere Aenderung wird wieder geprueft (ohne Fehler)", after.Count == 1 && after[0].Version == 7 && after[0].Items.Count == 0,
            string.Join(" | ", after.Select(g => $"v{g.Version}: {g.Items.Count}")));

        // the same text again: remembered (an undo back to a checked text), still pushed with its version
        s.Client.ChangeText(path, 8, $"var a = 1\nvar b6 = missing6\n");
        var undo = s.Until(path, 8);
        Check("derselbe Text wie vorher: das gemerkte Ergebnis mit der neuen Version", undo.Count == 1 && undo[0].Version == 8 && undo[0].Items.Count == 1,
            string.Join(" | ", undo.Select(g => $"v{g.Version}: {g.Items.Count}")));

        // a slow analysis overtaken while it runs: its result still comes (stale), then the newest - in the order of the versions
        string big = string.Concat(Enumerable.Range(0, 1500).Select(i => $"class C{i} {{ int f{i}\n M{i}(int x) {{ return x + f{i} }} }}\n"));
        string slow = Path.Combine(Folder, "slow.fire");
        s.Client.Open(slow, 1, big + "var z = nothing1\n");
        Thread.Sleep(400);   // (past the debounce: the analysis runs)
        s.Client.ChangeText(slow, 2, big + "var z = nothing2\n");
        var both = s.Until(slow, 2, 60000);
        bool ordered = both.Zip(both.Skip(1)).All(p => p.First.Version < p.Second.Version);
        Check("eine waehrend ihres Laufs ueberholte Analyse: Versionen in Reihenfolge, die letzte nicht stale",
            both.Count >= 1 && ordered && both[^1].Version == 2 && !both[^1].Stale && both[^1].Items.Any(i => i.Message.Contains("nothing2")),
            string.Join(" | ", both.Select(g => $"v{g.Version} stale={g.Stale} {g.Milliseconds} ms")));
        Check("die ueberholte Analyse ist als stale markiert", both.Count < 2 || (both[0].Version == 1 && both[0].Stale),
            string.Join(" | ", both.Select(g => $"v{g.Version} stale={g.Stale}")));
        s.Client.Close(slow);
        s.Client.Close(path);
    }

    private static void Documents(Session s)
    {
        string path = Path.Combine(Folder, "edits.fire");
        string text = "class Box { int width\n Grow() { } }\nvar b = new Box()\nb.";
        s.Client.Open(path, 1, text);
        // the edits of the editor, applied in order
        s.Client.ChangeEdits(path, 2, new[] { (text.Length, 0, "w"), (0, 0, "// head\n") });
        text = "// head\n" + text + "w";
        var items = s.Client.Completion(path, text.Length, out int start);
        Check("Aenderungen als Edits: der Text des Servers ist der des Editors", items.Select(i => i.Text).SequenceEqual(new[] { "width" }) && start == text.Length - 1,
            $"{string.Join(",", items.Select(i => i.Text))} ab {start}");
        var (version, found) = s.Client.Diagnose(path);
        Check("diagnose: sofort, mit der Version", version == 2, $"Version {version}, {found.Count} Diagnosen");
        // an edit that does not fit the text: the server asks for the whole text
        s.Client.ChangeEdits(path, 3, new[] { (100000, 5, "x") });
        var end = DateTime.UtcNow.AddSeconds(5);
        while (s.Resyncs.IsEmpty && DateTime.UtcNow < end) Thread.Sleep(10);
        Check("ein Edit, der nicht passt: resync", s.Resyncs.TryDequeue(out var resync) && resync == path);
        s.Client.Close(path);
        try { s.Client.Completion(path, 0, out _); Check("ein geschlossenes Dokument: Fehler 'document'", false); }
        catch (ServeException ex) { Check("ein geschlossenes Dokument: Fehler 'document'", ex.Code == "document", ex.Message); }
    }

    private static void Projects(Session s)
    {
        string helper = Path.Combine(Folder, "proj", "helper.fire");
        string main = Path.Combine(Folder, "proj", "main.fire");
        Directory.CreateDirectory(Path.GetDirectoryName(helper)!);
        File.WriteAllText(helper, "class Helper { int Size() { return 1 } }\n");
        s.Client.Project("proj", new[] { new ServeFile(helper, null), new ServeFile(main, null) });
        s.Client.Open(main, 1, "var h = new Helper()\nprint(h.Size())\n", project: "proj");
        var first = s.Until(main, 1);
        Check("Projekt: die andere Datei (vom Server gelesen) ist bekannt", first.Count == 1 && first[0].Items.Count == 0,
            string.Join(" | ", first.SelectMany(f => f.Items).Select(i => i.Message)));
        // the other file opened with another text: its overlay counts, and the document is checked again
        s.Client.Open(helper, 1, "class Helper2 { }\n", project: "proj");
        var again = s.Until(main, 1, 5000);
        Check("Projekt: die geoeffnete andere Datei gilt mit ihrem Text (Overlay), das Dokument wird neu geprueft",
            again.Count >= 1 && again[^1].Items.Any(i => i.Message.Contains("Helper")), string.Join(" | ", again.SelectMany(f => f.Items).Select(i => i.Message)));
        var completion = s.Client.Completion(main, "var h = new H".Length, out _);
        Check("Projekt: die Vervollstaendigung kennt die andere Datei mit ihrem offenen Text", completion.Any(i => i.Text == "Helper2") && !completion.Any(i => i.Text == "Helper"),
            string.Join(",", completion.Take(20).Select(i => i.Text)));
        s.Client.Close(helper);
        s.Client.Close(main);
        s.Client.DropProject("proj");
    }

    private static void Errors(Session s)
    {
        try { s.Client.Request("nonsense", null); Check("unbekannte Methode: Fehler 'method'", false); }
        catch (ServeException ex) { Check("unbekannte Methode: Fehler 'method'", ex.Code == "method", ex.Message); }
        try { s.Client.Request("completion", new System.Text.Json.Nodes.JsonObject { ["path"] = "/nowhere.fire", ["offset"] = 0 }); Check("unbekanntes Dokument: Fehler 'document'", false); }
        catch (ServeException ex) { Check("unbekanntes Dokument: Fehler 'document'", ex.Code == "document", ex.Message); }
    }

    /// <summary>Everything (with the installed packages): the protocol, and the diagnostics compared with LiveDiagnostics.</summary>
    public static int Run()
    {
        int failures = RunProtocol();
        _failures = 0;
        Console.WriteLine("=== forge serve: Diagnosen wie LiveDiagnostics im eigenen Prozess ===");
        var exe = Exe(out string why);
        if (exe == null) { Check("forge-c", ServeClient.InProcess, why); return failures + _failures; }
        try
        {
            using var s = new Session(exe, 50);
            var cases = new (string Title, string Source)[]
            {
                ("ohne Fehler", "var a = 1\nprint(a)\n"),
                ("unbekannter Name", "var a = missing + 1\n"),
                ("Syntaxfehler", "class A {\n  int x = \n}\nvar = 3\n"),
                ("Fehler des Resolvers, mehrere", "var a = b\nvar c = d\nnew Nope()\n"),
                ("Typkonflikt (lax: Warnung)", "int n = \"text\"\n"),
                ("strikt per #strict", "#strict\nint n = \"text\"\n"),
                ("#import ui", "#import \"ui\"\nvar b = new UI.Button(\"x\")\nb.Text = \"y\"\nb.Nope()\n"),
                ("unbekannter Import", "#import \"nothing_like_this\"\nvar a = 1\n"),
                ("Fehler des Compilers", "class A { M() { break } }\n"),
                ("Warnung des Codes", "warn 77: \"careful\"\nvar a = 1\n"),
            };
            int n = 0;
            foreach (var (title, source) in cases)
            {
                string path = Path.Combine(Folder, $"diag{++n}.fire");
                File.WriteAllText(path, source);
                s.Client.Open(path, 1, source);
                var (_, served) = s.Client.Diagnose(path);
                var own = LiveDiagnostics.Analyze(source, Folder);
                string Show(IEnumerable<Diagnostic> l) => string.Join(" | ", l.Select(d => $"{d.Line}:{d.Severity}:{d.Code}:{d.Message}{(d.File != null ? " @" + d.File : "")}"));
                Check($"Diagnosen: {title}", Show(served) == Show(own), $"Protokoll: {Show(served)}\n  eigener Prozess: {Show(own)}");
                s.Client.Close(path);
            }
        }
        catch (Exception ex) { Check("Diagnosen", false, ex.ToString()); }
        Console.WriteLine(_failures == 0 ? "Alle Diagnose-Pruefungen von forge serve bestanden." : $"FEHLER: {_failures} Diagnose-Pruefung(en) von forge serve fehlgeschlagen.");
        return failures + _failures;
    }
}
