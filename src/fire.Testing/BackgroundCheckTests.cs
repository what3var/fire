using System.Collections.Concurrent;
using fire.Tooling;
using fire.Editor;
using fire.Projects;

/// <summary>How spark checks the documents in the background while the user types (docs/tools/spark.md, "Live errors"): the debounce and the cancellation of BackgroundCheck (the newest
/// request wins, a stale result never overwrites a newer one, the final text is always checked), the priorities of the gate the analyses share (CheckGate), the places carried
/// through the changes of the text (EditLog), what the other files see of a file (DeclarationShape) and the remembered results of LiveDiagnostics. No window, no clock: the
/// waits are a fake clock, the UI thread a queue the test empties. Alone: FIRE_TESTS_ONLY=editorchecks (seconds); FIRE_TESTS_FILTER narrows it by the title of a check.</summary>
static class BackgroundCheckTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        string? filter = Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER");
        if (!string.IsNullOrWhiteSpace(filter) && !filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(f => title.Contains(f, StringComparison.OrdinalIgnoreCase))) return;
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Editor checks: {title}" : $"FEHLER: Editor checks: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    /// <summary>The UI thread of the tests: what BackgroundCheck posts waits here until the test runs it.</summary>
    private sealed class FakeUi
    {
        private readonly BlockingCollection<Action> _queue = new();
        public void Post(Action action) => _queue.Add(action);

        /// <summary>Runs what was posted until `until` holds (or the time is up): false when it never held.</summary>
        public bool RunUntil(Func<bool> until, int timeoutMs = 5000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!until())
            {
                var left = end - DateTime.UtcNow;
                if (left <= TimeSpan.Zero) return false;
                if (_queue.TryTake(out var action, left < TimeSpan.FromMilliseconds(20) ? left : TimeSpan.FromMilliseconds(20))) action();
            }
            return true;
        }

        /// <summary>Runs what is posted for a moment (to see that nothing more comes).</summary>
        public void Drain(int ms = 150) => RunUntil(() => false, ms);
    }

    /// <summary>The waits of the debounce: they end when the test says so (Advance), or when their token is cancelled.</summary>
    private sealed class FakeClock
    {
        private readonly List<TaskCompletionSource<bool>> _waits = new();
        public int Started;

        public Task Delay(TimeSpan span, CancellationToken token)
        {
            var wait = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_waits) { _waits.Add(wait); Started++; }
            token.Register(() => wait.TrySetCanceled(token));
            return wait.Task;
        }

        /// <summary>Every wait that runs now ends.</summary>
        public void Advance()
        {
            List<TaskCompletionSource<bool>> now;
            lock (_waits) { now = _waits.ToList(); _waits.Clear(); }
            foreach (var w in now) w.TrySetResult(true);
        }
    }

    public static int Run()
    {
        Console.WriteLine("=== Editor checks: debounce, cancellation, stale results, gate, shape ===");
        _failures = 0;
        DebounceChecks();
        CancellationChecks();
        StaleResultChecks();
        FailureChecks();
        PrepareChecks();
        GateChecks();
        EditLogChecks();
        ShapeChecks();
        RememberedChecks();
        ProjectChecks();
        Console.WriteLine(_failures == 0 ? "Alle Editor-Check-Pruefungen bestanden." : $"FEHLER: {_failures} Editor-Check-Pruefung(en) fehlgeschlagen.");
        return _failures;
    }

    private static void DebounceChecks()
    {
        var ui = new FakeUi();
        var clock = new FakeClock();
        var check = new BackgroundCheck<string>(ui.Post, delay: clock.Delay);
        var delivered = new List<(string Result, long Generation)>();
        check.Completed += (r, g) => delivered.Add((r, g));
        int runs = 0;
        // five keystrokes in a row: only the last request does the work
        for (int i = 1; i <= 5; i++) { int n = i; check.Request(_ => { Interlocked.Increment(ref runs); return "text " + n; }, TimeSpan.FromMilliseconds(400)); }
        Check("debounce: nothing runs while the requests come", runs == 0 && delivered.Count == 0 && check.IsBusy);
        clock.Advance();
        bool came = ui.RunUntil(() => delivered.Count > 0);
        ui.Drain();
        Check("debounce: only the newest request runs, once", came && runs == 1 && delivered.Count == 1 && delivered[0].Result == "text 5" && delivered[0].Generation == 5,
            $"runs {runs}, delivered {string.Join(", ", delivered)}");
        Check("debounce: not busy after the delivery", !check.IsBusy && check.Delivered == 5 && check.Requested == 5);

        // a request after the delivery is checked again (the final text always is)
        check.Request(_ => "text 6", TimeSpan.FromMilliseconds(400));
        clock.Advance();
        ui.RunUntil(() => delivered.Count > 1);
        Check("debounce: a later request delivers again", delivered.Count == 2 && delivered[1].Result == "text 6");
    }

    private static void CancellationChecks()
    {
        var ui = new FakeUi();
        var clock = new FakeClock();
        var check = new BackgroundCheck<string>(ui.Post, delay: clock.Delay);
        var delivered = new List<string>();
        check.Completed += (r, _) => delivered.Add(r);
        using var started = new ManualResetEventSlim();
        var seenCancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // the first check runs and waits until it sees its token cancelled - a newer request does that
        check.Request(token =>
        {
            started.Set();
            bool cancelled = token.WaitHandle.WaitOne(5000);
            seenCancel.TrySetResult(cancelled);
            token.ThrowIfCancellationRequested();
            return "old";
        }, TimeSpan.FromMilliseconds(400));
        clock.Advance();
        Check("cancel: the first check runs", started.Wait(5000));
        check.Request(_ => "new", TimeSpan.FromMilliseconds(400));
        Check("cancel: a newer request cancels the running check", seenCancel.Task.Wait(5000) && seenCancel.Task.Result);
        clock.Advance();
        ui.RunUntil(() => delivered.Count > 0);
        ui.Drain();
        Check("cancel: the cancelled check delivers nothing, the newer one its result", delivered.SequenceEqual(new[] { "new" }), string.Join(",", delivered));

        // Cancel(): the requests made so far deliver nothing (the document was closed)
        int runs = 0;
        check.Request(_ => { Interlocked.Increment(ref runs); return "closed"; }, TimeSpan.FromMilliseconds(400));
        check.Cancel();
        clock.Advance();
        ui.Drain();
        Check("cancel: nothing of a cancelled request runs or is delivered", runs == 0 && delivered.Count == 1 && !check.IsBusy);
    }

    private static void StaleResultChecks()
    {
        // the older check is slow (it ignores its token), the newer one is fast: the newer result is shown, the older one, coming after it, is dropped
        var ui = new FakeUi();
        var check = new BackgroundCheck<string>(ui.Post);   // (no gate: both run at the same time)
        var delivered = new List<(string, long)>();
        check.Completed += (r, g) => delivered.Add((r, g));
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        check.Request(_ => { started.Set(); release.Wait(5000); return "slow old"; }, TimeSpan.Zero);
        started.Wait(5000);
        check.Request(_ => "fast new", TimeSpan.Zero);
        ui.RunUntil(() => delivered.Count > 0);
        release.Set();
        ui.Drain(300);
        Check("stale: a result older than the one shown is never delivered", delivered.Count == 1 && delivered[0].Item1 == "fast new" && delivered[0].Item2 == 2,
            string.Join(",", delivered));

        // the older check finishes first: with DeliverSuperseded its result shows until the newer one comes (errors of a moment ago are better than none), then the newer one
        var check2 = new BackgroundCheck<string>(ui.Post) { DeliverSuperseded = true };
        var delivered2 = new List<string>();
        check2.Completed += (r, _) => delivered2.Add(r);
        using var releaseNew = new ManualResetEventSlim();
        using var startedOld = new ManualResetEventSlim();
        using var releaseOld = new ManualResetEventSlim();
        check2.Request(_ => { startedOld.Set(); releaseOld.Wait(5000); return "old"; }, TimeSpan.Zero);
        startedOld.Wait(5000);
        check2.Request(_ => { releaseNew.Wait(5000); return "new"; }, TimeSpan.Zero);
        releaseOld.Set();
        ui.RunUntil(() => delivered2.Count > 0);
        releaseNew.Set();
        ui.RunUntil(() => delivered2.Count > 1);
        Check("stale: a superseded result shows until the newer one, in order", delivered2.SequenceEqual(new[] { "old", "new" }), string.Join(",", delivered2));

        var check3 = new BackgroundCheck<string>(ui.Post) { DeliverSuperseded = false };
        var delivered3 = new List<string>();
        check3.Completed += (r, _) => delivered3.Add(r);
        using var startedOld3 = new ManualResetEventSlim();
        using var releaseOld3 = new ManualResetEventSlim();
        using var releaseNew3 = new ManualResetEventSlim();
        check3.Request(_ => { startedOld3.Set(); releaseOld3.Wait(5000); return "old"; }, TimeSpan.Zero);
        startedOld3.Wait(5000);
        check3.Request(_ => { releaseNew3.Wait(5000); return "new"; }, TimeSpan.Zero);
        releaseOld3.Set();
        ui.Drain(200);
        releaseNew3.Set();
        ui.RunUntil(() => delivered3.Count > 0);
        ui.Drain();
        Check("stale: without DeliverSuperseded only the newest result is delivered", delivered3.SequenceEqual(new[] { "new" }), string.Join(",", delivered3));

        // a request made after the work ended but before the delivery ran on the UI thread: the older result is not shown over it (checked once more on the UI thread)
        var check4 = new BackgroundCheck<string>(ui.Post) { DeliverSuperseded = false };
        var delivered4 = new List<string>();
        check4.Completed += (r, _) => delivered4.Add(r);
        check4.Request(_ => "first", TimeSpan.Zero);
        Thread.Sleep(200);   // (the work is done, its delivery waits in the queue of the UI)
        check4.Request(_ => "second", TimeSpan.Zero);
        ui.RunUntil(() => delivered4.Count > 0);
        ui.Drain();
        Check("stale: a request made while the result waits for the UI thread wins", delivered4.SequenceEqual(new[] { "second" }), string.Join(",", delivered4));
    }

    private static void FailureChecks()
    {
        var ui = new FakeUi();
        var check = new BackgroundCheck<string>(ui.Post);
        Exception? failed = null;
        var delivered = new List<string>();
        check.Completed += (r, _) => delivered.Add(r);
        check.Failed += (ex, _) => failed = ex;
        check.Request(_ => throw new InvalidOperationException("broken analysis"), TimeSpan.Zero);
        ui.RunUntil(() => failed != null);
        Check("failure: an exception of the work is reported, not delivered as a result", failed?.Message == "broken analysis" && delivered.Count == 0 && !check.IsBusy);
        check.Request(_ => "fine", TimeSpan.Zero);
        ui.RunUntil(() => delivered.Count > 0);
        Check("failure: the next request works again", delivered.SequenceEqual(new[] { "fine" }));
    }

    private static void PrepareChecks()
    {
        // the snapshot is taken on the UI thread once per pause, not per request
        var ui = new FakeUi();
        var clock = new FakeClock();
        var check = new BackgroundCheck<string>(ui.Post, delay: clock.Delay);
        var delivered = new List<string>();
        check.Completed += (r, _) => delivered.Add(r);
        int prepared = 0, uiThread = Environment.CurrentManagedThreadId;
        bool onUi = true;
        string text = "a";
        for (int i = 0; i < 4; i++)
        {
            text += "b";
            check.Request(() => { prepared++; onUi &= Environment.CurrentManagedThreadId == uiThread; string snapshot = text; return _ => snapshot; }, TimeSpan.FromMilliseconds(400));
        }
        clock.Advance();
        ui.RunUntil(() => delivered.Count > 0);
        Check("prepare: the snapshot is taken once, on the UI thread, with the newest text", prepared == 1 && onUi && delivered.SequenceEqual(new[] { "abbbb" }),
            $"prepared {prepared}, on UI {onUi}, {string.Join(",", delivered)}");
        check.Request(() => null, TimeSpan.Zero);
        ui.Drain();
        Check("prepare: nothing to do (null) delivers nothing, and the check is not busy any more", delivered.Count == 1 && !check.IsBusy);
    }

    private static void GateChecks()
    {
        var gate = new CheckGate();
        var order = new ConcurrentQueue<string>();
        gate.EnterAsync(0, CancellationToken.None).Wait();   // (held: the others wait)
        var project = gate.EnterAsync(LiveDiagnostics.ProjectPriority, CancellationToken.None).ContinueWith(_ => { order.Enqueue("project"); gate.Leave(); });
        var other = gate.EnterAsync(LiveDiagnostics.OpenPriority, CancellationToken.None).ContinueWith(_ => { order.Enqueue("other"); gate.Leave(); });
        using var cancel = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(LiveDiagnostics.ActivePriority, cancel.Token);
        var active = gate.EnterAsync(LiveDiagnostics.ActivePriority, CancellationToken.None).ContinueWith(_ => { order.Enqueue("active"); gate.Leave(); });
        Check("gate: the waiting are queued", gate.Waiting == 4 && gate.IsBusy);
        cancel.Cancel();
        bool left = false;
        try { cancelled.Wait(2000); } catch (AggregateException ex) when (ex.InnerException is OperationCanceledException) { left = true; }
        Check("gate: a cancelled waiter leaves the queue", left && gate.Waiting == 3);
        gate.Leave();
        Task.WaitAll(new[] { project, other, active }, 5000);
        Check("gate: the active document first, then the other open ones, then the projects", order.SequenceEqual(new[] { "active", "other", "project" }), string.Join(",", order));
        Check("gate: free again afterwards", !gate.IsBusy && gate.Waiting == 0);

        // BackgroundCheck with a gate: two checks never run at the same time
        var ui = new FakeUi();
        var shared = new CheckGate();
        int running = 0, most = 0, done = 0;
        string Work(string name)
        {
            int now = Interlocked.Increment(ref running);
            lock (order) most = Math.Max(most, now);
            Thread.Sleep(50);
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref done);
            return name;
        }
        var checks = Enumerable.Range(0, 4).Select(i => new BackgroundCheck<string>(ui.Post, shared) { Priority = () => i }).ToList();
        foreach (var c in checks) c.Request(_ => Work("x"), TimeSpan.Zero);
        ui.RunUntil(() => Volatile.Read(ref done) == 4 && !shared.IsBusy);
        Check("gate: the checks of four documents run one after the other", done == 4 && most == 1, $"done {done}, at once {most}");
    }

    private static void EditLogChecks()
    {
        var log = new EditLog();
        // "hello world": insert "big " at 6, then remove "hello " (0..6)
        log.Add(new TextEdit(6, 0, 4));
        log.Add(new TextEdit(0, 6, 0));
        Check("edit log: a place after the changes moves with them", log.MapOffset(7, 0) == 5 && log.MapOffset(10, 0) == 8, $"{log.MapOffset(7, 0)} {log.MapOffset(10, 0)}");
        Check("edit log: text inserted right at a place comes after it", log.MapOffset(6, 0) == 0 && new EditLog().MapOffset(3, 0) == 3);
        Check("edit log: a place in removed text goes to where the removal was", log.MapOffset(3, 0) == 0);
        Check("edit log: versions", log.Version == 2 && log.CanMap(0) && !log.CanMap(3));
        log.Reset();
        Check("edit log: after the text was replaced older places cannot be carried", !log.CanMap(2) && log.CanMap(log.Version));

        // the in-place shift of the spans (every keystroke) gives what the copying one gives
        var random = new Random(7);
        bool same = true;
        for (int round = 0; round < 300 && same; round++)
        {
            var spans = new List<HighlightSpan>();
            int at = 0;
            for (int i = 0; i < 40; i++) { at += random.Next(0, 4); int len = random.Next(1, 6); spans.Add(new HighlightSpan(at, len, HighlightCategory.Keyword)); at += len; }
            var edit = new TextEdit(random.Next(0, at), random.Next(0, 8), random.Next(0, 8));
            var copied = EditLog.Shift(spans, edit);
            var inPlace = new List<HighlightSpan>(spans);
            EditLog.ShiftInPlace(inPlace, edit);
            same = copied.Count == inPlace.Count && copied.Zip(inPlace).All(p => p.First.Start == p.Second.Start && p.First.Length == p.Second.Length);
        }
        Check("edit log: the spans shifted in place are the spans shifted into a copy", same);
    }

    private static void ShapeChecks()
    {
        const string before = """
            #import "linq"
            class Car {
                int speed
                func Drive(int ms) {
                    var x = ms * 2
                    if (x > 3) { print(x) }
                }
                construct(int s) { this.speed = s }
            }
            func Helper(string name) { return name + "!" }
            """;
        string body = before.Replace("var x = ms * 2", "var x = ms * 3 + speed").Replace("print(x)", "print(\"fast\")");
        string comment = before.Replace("int speed", "int speed   // m/s");
        string signature = before.Replace("func Drive(int ms)", "func Drive(int ms, bool fast)");
        string field = before.Replace("int speed", "int speed\n    float weight");
        string import = before.Replace("#import \"linq\"", "#import \"linq\"\n#import \"json\"");
        string open = before.Replace("print(x)", "print(\"x)");
        string? shape = DeclarationShape.Of(before);
        Check("shape: a change in the body of a function leaves the shape", shape != null && shape == DeclarationShape.Of(body));
        Check("shape: a comment leaves the shape", shape == DeclarationShape.Of(comment));
        Check("shape: a new parameter changes it", shape != DeclarationShape.Of(signature));
        Check("shape: a new field changes it", shape != DeclarationShape.Of(field));
        Check("shape: a new #import changes it", shape != DeclarationShape.Of(import));
        Check("shape: a text that cannot be read as tokens has none", DeclarationShape.Of(open) == null);
        string generic = "class Box<T> where T : new() {\n    T item\n}\n";
        Check("shape: the block after new() is no body (a field in it counts)", DeclarationShape.Of(generic) != DeclarationShape.Of(generic.Replace("T item", "T item\n    T other")));
    }

    private static void RememberedChecks()
    {
        int computed = 0;
        List<Diagnostic> Compute() { computed++; return new List<Diagnostic> { new(3, "x", DiagnosticSeverity.Error, "E1") }; }
        var inputs = new List<(string, string)> { ("a.fire", "class A {}"), ("b.fire", "class B {}") };
        string key = "test:" + Guid.NewGuid();
        var first = LiveDiagnostics.Remembered(key, "source", inputs, "lax", Compute);
        var again = LiveDiagnostics.Remembered(key, "source", new List<(string, string)>(inputs), "lax", Compute);
        Check("remembered: the same texts are analysed once", computed == 1 && again.Count == 1 && again[0].Line == 3);
        again.Clear();
        Check("remembered: the caller gets a copy (clearing it does not clear the remembered result)", LiveDiagnostics.Remembered(key, "source", inputs, "lax", Compute).Count == 1 && computed == 1);
        LiveDiagnostics.Remembered(key, "source", new List<(string, string)> { ("a.fire", "class A {}"), ("b.fire", "class B { int x }") }, "lax", Compute);
        Check("remembered: another text of another file is analysed again", computed == 2);
        LiveDiagnostics.Remembered(key, "source", new List<(string, string)> { ("a.fire", "class A {}"), ("b.fire", "class B { int x }") }, "strict", Compute);
        Check("remembered: other settings are analysed again", computed == 3);
        LiveDiagnostics.Invalidate();
        LiveDiagnostics.Remembered(key, "source", new List<(string, string)> { ("a.fire", "class A {}"), ("b.fire", "class B { int x }") }, "strict", Compute);
        Check("remembered: after Invalidate (the packages changed) analysed again", computed == 4 && first.Count == 1);
    }

    private static void ProjectChecks()
    {
        // a library (Core) and a program (App) that imports it: what the check of a document and the check of a whole project report, and with which file
        string root = Path.Combine(Path.GetTempPath(), "fire-editorchecks-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string Write(string relative, string text)
            {
                string path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
                return path;
            }
            Write("Core/Core.fireproj", """{ "name": "Core", "type": "library" }""");
            string greeter = Write("Core/greeter.fire", "namespace Core {\n    class Greeter {\n        static string Hello(string name) { return \"Hello, \" + name }\n    }\n}\n");
            Write("App/App.fireproj", """{ "name": "App", "references": [ { "project": "../Core/Core.fireproj" } ] }""");
            string main = Write("App/main.fire", "#import \"Core\"\nprint(Core.Greeter.Hello(\"x\"))\nprint(Helper.Twice(2))\n");
            string helper = Write("App/helper.fire", "class Helper {\n    static int Twice(int x) { return x * 2 }\n}\n");
            var ws = Workspace.Open(Write("All.firesln", """{ "name": "All", "projects": ["Core/Core.fireproj", "App/App.fireproj"] }"""));
            var app = ws.FindByName("App")!;
            List<(string, string)> OthersOf(BuildPlan plan, string self) => plan.Sources.Where(f => !ProjectFiles.PathComparer.Equals(Path.GetFullPath(f.Path), self)).Select(f => (f.Path, f.Text)).ToList();

            var plan = BuildPlan.Create(ws, app, path => FileTexts.Read(path));
            var clean = ServeDocuments.AnalyzeInProject(main, File.ReadAllText(main), Path.GetDirectoryName(main), plan, OthersOf(plan, main)) ?? new List<Diagnostic> { new(0, "no forge serve") };
            Check("project: a document that uses another file and a library has no errors", clean.All(d => !d.IsError), string.Join("; ", clean));

            // the library is broken (a syntax error): the error of the document's check carries the PATH of the library file, not "Core/greeter.fire"
            var broken = BuildPlan.Create(ws, app, path => ProjectFiles.PathComparer.Equals(Path.GetFullPath(path), greeter) ? "namespace Core {\n    class Greeter {\n        static string Hello(string name { return name }\n    }\n}\n" : null);
            var found = ServeDocuments.AnalyzeInProject(main, File.ReadAllText(main), Path.GetDirectoryName(main), broken, OthersOf(broken, main)) ?? new List<Diagnostic>();
            Check("project: an error in a library file is reported with the path of the file", found.Any(d => d.IsError && d.File != null && ProjectFiles.PathComparer.Equals(d.File, greeter) && d.Line == 3),
                string.Join("; ", found.Select(d => $"{d.File}:{d.Line} {d.Message}")));

            // the check of the whole project: an error in a file that is not open, with its file
            var helperBroken = BuildPlan.Create(ws, app, path => ProjectFiles.PathComparer.Equals(Path.GetFullPath(path), helper) ? "class Helper {\n    static int Twice(int x) { return x * notThere }\n}\n" : null);
            var files = helperBroken.Sources.Select(f => (Path.GetFullPath(f.Path), f.Text)).ToList();
            var all = LiveDiagnostics.AnalyzeProjectFiles(app.FilePath, files) ?? new List<Diagnostic>();   // (forge bytecode of the project, the open texts as overlays)
            Check("project: the check of a project finds the error in a file that is not open, with its file and line",
                all.Any(d => d.IsError && d.File != null && ProjectFiles.PathComparer.Equals(d.File, helper) && d.Line == 2 && d.Message.Contains("notThere")),
                string.Join("; ", all.Select(d => $"{d.File}:{d.Line} {d.Message}")));
            Check("project: ... and nothing in the files without errors", !all.Any(d => d.File != null && ProjectFiles.PathComparer.Equals(d.File, main)));

            // the texts of the files on the disk are read once while they do not change
            int reads = FileTexts.Reads;
            FileTexts.Read(helper);
            FileTexts.Read(helper);
            Check("file texts: an unchanged file is not read again", FileTexts.Reads == reads);
            File.WriteAllText(helper, "class Helper {\n    static int Twice(int x) { return x + x }\n}\n// longer now\n");
            Check("file texts: a changed file is read again", FileTexts.Read(helper).Contains("x + x") && FileTexts.Reads == reads + 1);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { }
        }
    }
}
