using System.Collections.Concurrent;
using fire.Debugging;
using fire.Tooling;

/// <summary>
/// The debugger without a window (FIRE_TESTS_ONLY=debugger; also part of the full run): the stepping loops of the VM (the run of F5), the
/// engine-independent debug target (fire.Debugging: IDebugTarget, NativeDebugTarget against a fake agent and `fire`) and spark's
/// DebugSession/DebugThreadContext (linked in from fire.Editor) on top of it.
/// </summary>
static partial class DebuggerTests
{
    public static void Run()
    {
        SessionRun();
        NativeRun();
        ModuleRun();
    }

    private static int _failures;

    private static void Check(bool ok, string title, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Debug-Ziel: {title}" : $"FEHLER: Debug-Ziel: {title}{(detail != null ? "\n  " + detail : "")}");
    }

    /// <summary>A program for the target checks: an object with fields, a property and a probe, a method with locals, globals and an array.</summary>
    internal const string TargetScript = """
        class Point {
            int x = 1
            string name = "p"
            int Twice {
                get { return this.x * 2 }
                set { this.x = value / 2 }
            }
            int Sum(int a, int b) {
                var s = a + b
                return s + this.x
            }
        }
        var g = 5
        var p = new Point()
        var arr = [1, 2, 3]
        probe p.x changed { print("probe " + old + " -> " + value) }
        var r = p.Sum(2, 3)
        print("r " + r)
        print("g " + g + " " + arr[1])
        """;

    private static string Show(IEnumerable<DebugValue> values) => string.Join(",", values.Select(v => v.Name + "=" + v.Display));

    /// <summary>The output of `fire` comes through its pipe, a moment after the event of its end: true when <paramref name="seen"/> within 5 s.</summary>
    private static bool Soon(Func<bool> seen)
    {
        for (int i = 0; i < 100 && !seen(); i++) Thread.Sleep(50);
        return seen();
    }

    /// <summary>What spark does for F5 in Debug: the program under the debugger in `fire`, taken over by the session (false: it did not compile or start).</summary>
    private static bool Attach(fire.Editor.DebugSession session, string[] sources, string folder)
    {
        session.Reset();
        try { session.Attach(CVmTest.Debug(sources, VmExecutionMode.Debug, folder)); return true; }
        catch (Exception ex) { Console.WriteLine("  (" + ex.Message.Split('\n')[0] + ")"); return false; }
    }

    /// <summary>spark's DebugSession and DebugThreadContext (linked in from fire.Editor) over the VM target - `fire`, the VM in C, under the debugger, as spark runs
    /// it (CVmTest.Debug): fire-and-forget steps on threads of their own, the paused event, set through the context, threads that start in continue mode, the output
    /// named by thread.</summary>
    public static void SessionRun()
    {
        Console.WriteLine();
        Console.WriteLine("=== Debug-Sitzung (spark ohne Fenster) ===");
        _failures = 0;
        string folder = Path.Combine(Path.GetTempPath(), "fire-dbgsession-" + Guid.NewGuid().ToString("N"));
        var session = new fire.Editor.DebugSession();
        var output = new ConcurrentQueue<string>();
        session.OutputWritten += output.Enqueue;
        var paused = new BlockingCollection<fire.Editor.DebugThreadContext>();
        session.ThreadPaused += paused.Add;
        bool WaitMain(out fire.Editor.DebugThreadContext? ctx)
        {
            ctx = null;
            while (paused.TryTake(out var c, TimeSpan.FromSeconds(20)))
                if (c.IsMain) { ctx = c; return true; }
            return false;
        }

        {
            // spark's build log: every line of a build (its phases, the commands, the compiler's output, the errors) reaches the build log panel as it comes - the build
            // window only mirrors it, so it can be read and copied after the window is gone; the whole log of the result adds only what had not come yet
            var panel = new List<string>();
            var feed = new fire.Editor.BuildLogFeed(panel.Add);
            var feedMonitor = new fire.Tooling.BuildMonitor(default, feed.Progress, feed.Line);
            bool feedCompiled = false;
            System.Threading.Tasks.Task.Run(() =>
            {
                using var use = fire.Tooling.BuildMonitor.Use(feedMonitor);
                for (int i = 0; i < 4; i++) fire.Tooling.BuildMonitor.Step(fire.Tooling.BuildPhase.CompileFire, "Compiling", i, 4);   // (the steps forge reports)
                feedCompiled = true;
                fire.Tooling.BuildMonitor.Step(fire.Tooling.BuildPhase.PackageNatives, "Building the natives of packages");
                fire.Tooling.BuildMonitor.LogLine("gcc.exe -std=c11 -O2 -shared wrapper.c -o fire_gfx.dll");
                fire.Tooling.BuildMonitor.LogLine("wrapper.c:(.text+0x2c93c): undefined reference to `className'");
            }).Wait();
            var drained = feed.Drain();
            var rest = feed.Missing("gcc.exe -std=c11 -O2 -shared wrapper.c -o fire_gfx.dll\nwrapper.c:(.text+0x2c93c): undefined reference to `className'\nBuild failed.\n");
            Check(feedCompiled && panel.SequenceEqual(drained) && panel.Count >= 4 && panel[0].StartsWith("--- ", StringComparison.Ordinal)
                    && panel.Contains("--- Building the natives of packages ---") && panel[^1].Contains("undefined reference") && feed.Drain().Count == 0 && rest.SequenceEqual(new[] { "Build failed." }),
                "spark: das Protokoll eines Builds (Phasen, Befehle, Ausgabe des Compilers) kommt in die Build-Ausgabe, das Build-Fenster spiegelt sie; das Protokoll am Ende fuegt nur Neues an",
                string.Join(" | ", panel) + " || " + string.Join(" | ", rest));
        }
        bool compiled = Attach(session, new[] { TargetScript }, folder);
        int src = session.FirstUserSourceIndex;
        session.UpdateBreakpoints(new[] { (src, 10) });
        session.Continue(new HashSet<(int, int)> { (src, 10) });
        bool hit = WaitMain(out var mainCtx);
        var where = mainCtx?.Thread.CurrentLocation;
        Check(compiled && session.HasProgram && session.Target?.Engine == "vm" && hit && where == (src, 10) && !mainCtx!.IsFinished,
            "DebugSession: Continue haelt am Haltepunkt (Ereignis ThreadPaused)", $"{compiled} {session.CompileError} {hit} {where}");

        var s = mainCtx!.Thread.Scopes().SelectMany(l => l.Variables).First(v => v.Name == "s");
        string? setResult = null;
        bool requested = mainCtx.RequestSetValue(s, "100", (ok, error) => setResult = ok ? "ok" : error);
        bool afterSet = WaitMain(out _);
        Check(requested && afterSet && setResult == "ok" && Show(mainCtx.Thread.Scopes().SelectMany(l => l.Variables)).Contains("s=100"),
            "DebugThreadContext.RequestSetValue: setzt auf dem Thread des Ziels, danach feuert ThreadPaused", setResult);

        session.RunToCompletion();
        bool ended = WaitMain(out var endCtx) && endCtx!.IsFinished && session.IsFinished && session.RuntimeError == null;
        Check(ended && Soon(() => output.Contains("r 101")), "RunToCompletion: das Programm endet mit dem gesetzten Wert", string.Join(" | ", output));

        // a thread of the program starts in continue mode and stops at a breakpoint; its output carries its name
        while (paused.TryTake(out _)) { }
        output.Clear();
        var added = new ConcurrentQueue<fire.Editor.DebugThreadContext>();
        session.ThreadAdded += added.Enqueue;
        compiled = Attach(session, new[] { "fire {\n    var t = 1\n    print(\"in thread \" + t)\n}\nvar m = 2\nprint(\"main \" + m)\n" }, folder);
        src = session.FirstUserSourceIndex;
        session.UpdateBreakpoints(new[] { (src, 3), (src, 6) });
        session.Continue(new HashSet<(int, int)> { (src, 3), (src, 6) }); // the main program waits at its last line (the breakpoints of a program in `fire` are the same for all its threads, as in spark)
        fire.Editor.DebugThreadContext? threadCtx = null;
        bool threadStopped = false, mainEnded = false;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !(threadStopped && mainEnded) && paused.TryTake(out var c, TimeSpan.FromSeconds(20)))
        {
            if (c.IsMain) mainEnded = !c.IsFinished && c.Thread.CurrentLocation == (src, 6);
            else if (!c.IsFinished && c.Thread.CurrentLocation == (src, 3)) { threadStopped = true; threadCtx = c; }
        }
        Check(compiled && threadStopped && added.Count == 1 && threadCtx!.Name == "Fire #1" && session.Threads.Count == 2,
            "ein Thread des Programms laeuft von selbst bis zum Haltepunkt (das Hauptprogramm haelt an seinem)", $"{threadStopped} {mainEnded} {added.Count}");
        if (threadCtx != null)
        {
            session.SelectThread(threadCtx);
            session.RunToCompletion();
            bool threadEnded = false;
            while (paused.TryTake(out var c, TimeSpan.FromSeconds(20))) if (ReferenceEquals(c, threadCtx) && c.IsFinished) { threadEnded = true; break; }
            // (the output of `fire` comes over its stdout, not by thread: the name of the thread is not in front of it - an open point of M1, docs/ROADMAP_C_PORT.md)
            Check(threadEnded && Soon(() => output.Contains("[Fire #1] in thread 1") || output.Contains("in thread 1")), "die Ausgabe eines Threads kommt an", string.Join(" | ", output));
            session.SelectThread(session.Threads.First(t => t.IsMain));
            session.RunToCompletion();
            bool mainDone = false;
            while (paused.TryTake(out var c, TimeSpan.FromSeconds(20))) if (c.IsMain && c.IsFinished) { mainDone = true; break; }
            Check(mainDone && Soon(() => output.Contains("main 2")), "danach endet das Hauptprogramm", string.Join(" | ", output));
        }
        session.Reset();
        Check(!session.HasProgram, "Reset gibt das Ziel frei");
        try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Console.WriteLine(_failures == 0 ? "Alle Debug-Sitzungs-Pruefungen bestanden." : $"FEHLER: {_failures} Debug-Sitzungs-Pruefung(en) fehlgeschlagen.");
    }
}
