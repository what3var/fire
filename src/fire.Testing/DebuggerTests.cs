using System.Collections.Concurrent;
using fire.Debugging;
using fire.Runtime;
using fire.Values;

/// <summary>
/// The debugger without a window (FIRE_TESTS_ONLY=debugger; also part of the full run): the stepping loops of the VM (the run of F5), the
/// engine-independent debug target (fire.Debugging: IDebugTarget, VmDebugTarget, NativeDebugTarget against a fake agent) and spark's
/// DebugSession/DebugThreadContext (linked in from fire.Editor) on top of it.
/// </summary>
static partial class DebuggerTests
{
    public static void Run()
    {
        VmRun();
        TargetRun();
        SessionRun();
        NativeRun();
    }

    /// <summary>The run of the editor's debugger on the VM: VM.RunUntilBreakpoint/RunUntilEnd (F5), line table (binary search), native forwarding.</summary>
    public static void VmRun()
    {
        Console.WriteLine();
        Console.WriteLine("=== Debugger-Lauf ===");
        int dbgFailures = 0;
        void DbgCheck(bool ok, string title, string? detail = null)
        {
            if (!ok) dbgFailures++;
            Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}{(detail != null ? "\n  " + detail : "")}");
        }

        (fire.Compiler.RuntimeSession Session, List<string> Lines) BuildDbg(string script, VmExecutionMode mode)
        {
            var lines = new List<string>();
            var session = fire.Compiler.RuntimeSession.Build(new[] { script }, mode, args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
            return (session, lines);
        }

        foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
        {
            // Loops (for/while/foreach): a breakpoint in the last line of the body hits exactly once per pass - not once more
            // when leaving the loop. In addition the earlier loop as a reference (StepInstruction + CurrentLocation per instruction):
            // RunUntilBreakpoint must stop at the same places.
            foreach (var (kind, loopScript) in new[]
            {
                ("for", "var total = 0\nfor (var i = 0; i < 3; i = i + 1) {\n    total = total + i\n}\nprint(\"fertig \" + total)\n"),
                ("while", "var total = 0\nvar i = 0\nwhile (i < 3) {\n    total = total + i\n    i = i + 1\n}\nprint(\"fertig \" + total)\n"),
                ("foreach", "var total = 0\nvar items = [0, 1, 2]\nforeach (x in items) {\n    total = total + x\n}\nprint(\"fertig \" + total)\n"),
            })
            {
                int bpLine = kind == "for" ? 3 : kind == "while" ? 5 : 4;
                VM.ResetTerminateForTests();
                var (refSession, refLines) = BuildDbg(loopScript, mode);
                var refVm = refSession.VirtualMachine!;
                var refBps = new HashSet<(int SourceIndex, int Line)> { (refSession.FirstUserSourceIndex, bpLine) };
                int refHits = 0;
                {
                    var lastLocation = refVm.CurrentLocation;
                    while (refVm.StepInstruction())
                    {
                        var location = refVm.CurrentLocation;
                        if (location != lastLocation) { lastLocation = location; if (refBps.Contains(location)) refHits++; }
                    }
                }
                VM.ResetTerminateForTests();

                VM.ResetTerminateForTests();
                var (session, lines) = BuildDbg(loopScript, mode);
                var vm = session.VirtualMachine!;
                var bps = new HashSet<(int SourceIndex, int Line)> { (session.FirstUserSourceIndex, bpLine) };
                int hits = 0;
                var lineAtHit = new List<int>();
                while (vm.RunUntilBreakpoint(bps, () => false))
                {
                    hits++;
                    lineAtHit.Add(vm.CurrentLine);
                    if (hits > 10) break;
                }
                DbgCheck(hits == 3 && hits == refHits && lineAtHit.All(l => l == bpLine) && lines.SequenceEqual(new[] { "fertig 3" }) && refLines.SequenceEqual(lines),
                    $"RunUntilBreakpoint ({kind}): Haltepunkt im Body trifft genau einmal je Durchlauf (wie die Einzelschritt-Schleife), danach laeuft das Programm zu Ende [{mode}]",
                    $"Treffer {hits} (Referenz {refHits}), Zeilen {string.Join(",", lineAtHit)}, Ausgabe {string.Join("|", lines)}");
                VM.ResetTerminateForTests();
            }
        }

        {
            // A breakpoint in the FIRST line stops at the start (like other debuggers: the prelude runs first, then the program enters line 1), nothing has run there yet;
            // going on from there does not stop on the same line again, the program runs to the end
            VM.ResetTerminateForTests();
            var (session, lines) = BuildDbg("print(\"a\")\nprint(\"b\")\n", VmExecutionMode.Debug);
            var vm = session.VirtualMachine!;
            var firstLine = new HashSet<(int, int)> { (session.FirstUserSourceIndex, 1) };
            bool stopped = vm.RunUntilBreakpoint(firstLine, () => false);
            bool atStart = stopped && vm.CurrentLine == 1 && lines.Count == 0;
            bool again = vm.RunUntilBreakpoint(firstLine, () => false);
            DbgCheck(atStart && !again && lines.SequenceEqual(new[] { "a", "b" }), "RunUntilBreakpoint: ein Haltepunkt auf der ersten Zeile haelt beim Start an (vor ihr), weiter laeuft das Programm zu Ende",
                $"angehalten {stopped}, Zeile {vm.CurrentLine}, nochmal {again}, Ausgabe {string.Join("|", lines)}");
            VM.ResetTerminateForTests();
        }

        {
            // A pause request interrupts an infinite loop (RunUntilEnd and RunUntilBreakpoint)
            foreach (var useBreakpointRun in new[] { false, true })
            {
                VM.ResetTerminateForTests();
                var (session, _) = BuildDbg("var n = 0\nwhile (true) { n = n + 1 }\n", VmExecutionMode.Release);
                var vm = session.VirtualMachine!;
                volatile_bool pause = new();
                var timer = Task.Run(() => { Thread.Sleep(100); pause.Value = true; });
                var run = Task.Run(() => useBreakpointRun ? vm.RunUntilBreakpoint(new HashSet<(int, int)>(), () => pause.Value) : vm.RunUntilEnd(() => pause.Value));
                bool finishedInTime = run.Wait(TimeSpan.FromSeconds(20));
                DbgCheck(finishedInTime && run.Result, $"Pause-Anforderung haelt eine Endlosschleife an ({(useBreakpointRun ? "RunUntilBreakpoint" : "RunUntilEnd")})");
                VM.ResetTerminateForTests();
            }
        }

        {
            // Line table: binary search yields the same places as the line of each instruction suggests
            VM.ResetTerminateForTests();
            var (session, _) = BuildDbg("var a = 1\nvar b = 2\n\nvar c = a + b\nprint(c)\n", VmExecutionMode.Debug);
            var chunk = session.CompiledProgram.TopLevel;
            var seen = new List<int>();
            int ip = 0, lastLine = -1;
            for (; ip < chunk.Code.Count; ip++)
            {
                var (src, line) = chunk.GetLocation(ip);
                if (src == session.FirstUserSourceIndex && line != lastLine) { seen.Add(line); lastLine = line; }
            }
            DbgCheck(seen.SequenceEqual(new[] { 1, 2, 4, 5 }), "Chunk.GetLocation (binaere Suche): die Zeilen des Programms in Reihenfolge", string.Join(",", seen));
            var (s0, l0) = chunk.GetLocationRange(0, out int rs, out int re);
            DbgCheck(rs == 0 && re > 0 && re < chunk.Code.Count, "Chunk.GetLocationRange: Bereich der ersten Stelle beginnt bei 0 und endet vor dem Chunk-Ende");
        }

        {
            // Forwarding to native functions (methods of the bridge preludes): same result as the direct call, also with a return value
            foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release, VmExecutionMode.Performance })
            {
                VM.ResetTerminateForTests();
                var (session, lines) = BuildDbg("""
                    #import "graphics"
                    var fb = new Framebuffer(16, 16)
                    var con = new Renderer(fb)
                    con.AlphaBlending = false
                    var viaMethod = 0
                    var direct = 0
                    for (var i = 0; i < 5; i = i + 1) {
                        con.FillRect(i, 0, 1, 1, new SolidBrush(256 + i))
                        viaMethod = viaMethod + con.GetPixel(i, 0) + con.CellWidth()
                        direct = direct + __GRPHRndGetPixel(con.id, i, 0) + __GRPHRndCellWidth(con.id)
                    }
                    print(viaMethod == direct)
                    print(viaMethod)
                    """, mode);
                session.Run();
                DbgCheck(lines.SequenceEqual(new[] { "True", "1330" }), $"Methoden der Grafik-Bruecke (Weiterleitung an native Funktionen) liefern dasselbe wie der direkte Aufruf [{mode}]", string.Join("|", lines));
                VM.ResetTerminateForTests();
            }
        }

        Console.WriteLine(dbgFailures == 0 ? "Alle Debugger-Lauf-Pruefungen bestanden." : $"FEHLER: {dbgFailures} Debugger-Lauf-Pruefung(en) fehlgeschlagen.");
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

    /// <summary>VmDebugTarget: the members of IDebugTarget/IDebugThread over the VM - location, trace, depth, this, scopes, globals, children, set,
    /// unhandled exceptions and threads.</summary>
    public static void TargetRun()
    {
        Console.WriteLine();
        Console.WriteLine("=== Debug-Ziel (IDebugTarget, VM) ===");
        _failures = 0;

        foreach (var mode in new[] { VmExecutionMode.Debug, VmExecutionMode.Release })
        {
            VM.ResetTerminateForTests();
            var output = new List<string>();
            using var target = VmDebugTarget.Build(new[] { TargetScript }, mode);
            target.Output += (_, text) => { lock (output) output.Add(text); };
            var main = target.MainThread;
            int src = target.FirstUserSourceIndex;
            Check(target.Engine == "vm" && target.HasOperandStack && main.IsMain && main.Name == "Main" && target.Threads.Count == 1 && main.CallDepth == 0 && output.Count == 0,
                $"VmDebugTarget.Build: Hauptthread steht vor der ersten Zeile, nichts ist gelaufen [{mode}]");

            bool stopped = main.RunUntilBreakpoint(new HashSet<(int, int)> { (src, 10) }, () => false);
            var trace = main.CurrentTrace();
            Check(stopped && main.CurrentLocation == (src, 10) && main.CallDepth == 1 && trace.Count == 2 && trace[0] == (src, 10) && trace[1] == (src, 17),
                $"Haltepunkt in einer Methode: Ort, Tiefe und Aufrufkette [{mode}]", $"{stopped} {main.CurrentLocation} depth {main.CallDepth} trace {string.Join(" ", trace)}");

            var self = main.This;
            var fields = self?.Children() ?? Array.Empty<DebugValue>();
            Check(self != null && self.Kind == DebugValueKind.Object && self.Display.StartsWith("Point (#") && self.HasChildren && self.Id != 0
                && Show(fields).Contains("x=1") && Show(fields).Contains("name=p") && fields.All(f => f.CanSet),
                $"this: Objekt mit Feldern als Kinder [{mode}]", self == null ? "kein this" : $"{self.Display}: {Show(fields)}");

            var scopes = main.Scopes();
            var locals = scopes.SelectMany(s => s.Variables).ToList();
            Check(scopes.Count >= 1 && scopes[^1].IsFunctionTopLevel && Show(locals).Contains("a=2") && Show(locals).Contains("b=3") && Show(locals).Contains("s=5"),
                $"Scopes: Parameter und Locals mit Namen [{mode}]", string.Join(" | ", scopes.Select(s => $"{s.Depth}{(s.IsFunctionTopLevel ? "F" : "")}: {Show(s.Variables)}")));

            var globals = main.Globals();
            var arr = globals.FirstOrDefault(v => v.Name == "arr");
            var pGlobal = globals.FirstOrDefault(v => v.Name == "p");
            Check(Show(globals).Contains("g=5") && arr is { Kind: DebugValueKind.Array, Display: "Array[3]", HasChildren: true } && Show(arr.Children()) == "[0]=1,[1]=2,[2]=3"
                && pGlobal != null && self != null && pGlobal.Id == self.Id,
                $"Globals: Zahl, Array mit Elementen, dasselbe Objekt hat dieselbe Id wie this [{mode}]", Show(globals));

            var stack = main.OperandStack();
            Check(stack != null && stack.All(v => v.TypeName.Length > 0), $"Operandenstapel der VM [{mode}]");

            // set: a local, a global, an element (directly) and a field (like an assignment: the probe fires)
            var s = locals.First(v => v.Name == "s");
            bool badRefused = !main.TrySetValue(s, "abc", out string badError) && badError.Length > 0;
            bool setLocal = main.TrySetValue(s, "40", out string e1);
            bool setGlobal = main.TrySetValue(globals.First(v => v.Name == "g"), "9", out string e2);
            bool setElement = main.TrySetValue(arr!.Children()[1], "20", out string e3);
            bool setField = main.TrySetValue(fields.First(f => f.Name == "x"), "10", out string e4);
            bool notSettable = !main.TrySetValue(self!, "1", out _);
            var after = main.Scopes().SelectMany(l => l.Variables).ToList();
            Check(badRefused && setLocal && setGlobal && setElement && setField && notSettable && Show(after).Contains("s=40") && Show(main.This!.Children()).Contains("x=10"),
                $"Werte setzen: Local, Global, Element, Feld; falscher Text und ein Objekt werden abgelehnt [{mode}]", $"{badError} | {e1} {e2} {e3} {e4} | {Show(after)}");

            bool more = main.RunUntilEnd(() => false);
            lock (output)
                Check(!more && output.SequenceEqual(new[] { "probe 1 -> 10", "r 50", "g 9 20" }),
                    $"nach dem Setzen laeuft das Programm mit den neuen Werten zu Ende, der Probe des Felds feuert [{mode}]", string.Join(" | ", output));
            VM.ResetTerminateForTests();
        }

        {
            // an unhandled exception stops the thread at the throw site; resume goes on after it
            VM.ResetTerminateForTests();
            var output = new List<string>();
            using var target = VmDebugTarget.Build(new[] { "var a = 1\nthrow new Exception(\"boom\")\nprint(\"after \" + a)\n" }, VmExecutionMode.Debug);
            target.Output += (_, text) => { lock (output) output.Add(text); };
            var main = target.MainThread;
            bool more = main.RunUntilBreakpoint(new HashSet<(int, int)>(), () => false);
            var trace = main.UnhandledTrace;
            Check(!more && main.IsPausedOnUnhandled && main.UnhandledError?.Contains("boom") == true && trace is { Count: > 0 } && trace[0] == (target.FirstUserSourceIndex, 2)
                && main.CurrentLocation.Line == 2,
                "unbehandelte Ausnahme: der Thread steht am throw, Meldung und Ort", $"{more} {main.CurrentLocation} {main.IsPausedOnUnhandled} {main.UnhandledError} {(trace == null ? "-" : string.Join(" ", trace))}");
            bool resumed = main.TryResumeUnhandled();
            bool end = main.RunUntilEnd(() => false);
            Check(resumed && !end && main.UnhandledError == null && !main.IsPausedOnUnhandled && output.SequenceEqual(new[] { "after 1" }),
                "TryResumeUnhandled: das Programm geht nach dem throw weiter", string.Join(" | ", output));
            VM.ResetTerminateForTests();
        }

        {
            // threads: `fire` announces a thread of its own (it runs only when the subscriber drives it); its output names it
            VM.ResetTerminateForTests();
            var output = new ConcurrentQueue<(IDebugThread? Thread, string Text)>();
            var started = new ConcurrentQueue<IDebugThread>();
            var runs = new ConcurrentQueue<Task<bool>>();
            using var target = VmDebugTarget.Build(new[] { "fire {\n    print(\"im thread\")\n}\nprint(\"main\")\n" }, VmExecutionMode.Debug);
            int src = target.FirstUserSourceIndex;
            target.Output += (t, text) => output.Enqueue((t, text));
            var startLocations = new ConcurrentQueue<(int, int)>();
            // (the run is queued before the thread is announced to the test: it waits for "started", then for the runs)
            target.ThreadStarted += t => { startLocations.Enqueue(t.CurrentLocation); runs.Enqueue(Task.Run(() => t.RunUntilEnd(() => false))); started.Enqueue(t); };
            // the main program waits at its last line (otherwise its end could stop the thread before it printed)
            bool mainPaused = target.MainThread.RunUntilBreakpoint(new HashSet<(int, int)> { (src, 4) }, () => false);
            for (int i = 0; i < 400 && started.IsEmpty; i++) Thread.Sleep(50); // `fire` starts its thread asynchronously
            bool threadDone = runs.All(r => r.Wait(TimeSpan.FromSeconds(20)) && !r.Result);
            bool mainMore = target.MainThread.RunUntilEnd(() => false);
            var fireThread = started.FirstOrDefault();
            Check(mainPaused && !mainMore && threadDone && started.Count == 1 && fireThread is { IsMain: false, Name: "Fire #1" } && target.Threads.Count == 2
                && output.Any(o => o.Text == "im thread" && ReferenceEquals(o.Thread, fireThread)) && output.Any(o => o.Text == "main" && ReferenceEquals(o.Thread, target.MainThread)),
                "Threads: fire meldet einen neuen Thread, seine Ausgabe traegt ihn", $"{mainMore} {threadDone} {started.Count} {fireThread?.Name} {target.Threads.Count} " + string.Join(" | ", output.Select(o => $"{o.Thread?.Name}: {o.Text}")));
            Check(startLocations.SequenceEqual(new[] { (src, 2) }), "die Zeilen eines fire-Blocks gehoeren zu seiner Quelle (ein Haltepunkt darin kann treffen)", string.Join(" ", startLocations));
            target.Dispose();
            Check(FireRuntime.ThreadBodyInterceptor == null, "Dispose meldet die Uebernahme neuer Threads ab");
            VM.ResetTerminateForTests();
        }

        Console.WriteLine(_failures == 0 ? "Alle Debug-Ziel-Pruefungen bestanden." : $"FEHLER: {_failures} Debug-Ziel-Pruefung(en) fehlgeschlagen.");
    }

    /// <summary>spark's DebugSession and DebugThreadContext (linked in from fire.Editor) over the VM target: fire-and-forget steps on threads of
    /// their own, the paused event, set through the context, threads that start in continue mode, the output named by thread.</summary>
    public static void SessionRun()
    {
        Console.WriteLine();
        Console.WriteLine("=== Debug-Sitzung (spark ohne Fenster) ===");
        _failures = 0;
        VM.ResetTerminateForTests();
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
            // spark's build window: CompileAsync compiles on a thread of the pool and reports the steps of the fire compiler in order; cancelled, it keeps no program
            // (a session of its own: the events of its programs stay away from the checks below)
            var asyncSession = new fire.Editor.DebugSession();
            var steps = new List<fire.Native.BuildProgress>();
            bool asyncCompiled = asyncSession.CompileAsync(new[] { TargetScript }, null, null, null, null, new fire.Native.BuildMonitor(default, p => steps.Add(p))).GetAwaiter().GetResult();
            Check(asyncCompiled && asyncSession.HasProgram && steps.Count > 0 && steps.All(x => x.Phase == fire.Native.BuildPhase.CompileFire && x.Total == 4) && steps.Select(x => x.Done).SequenceEqual(new[] { 0, 1, 2, 3 }),
                "DebugSession.CompileAsync: uebersetzt neben dem Fenster, die Schritte des fire-Compilers kommen in Reihenfolge (0..3 von 4)", string.Join(" | ", steps.Select(x => $"{x.Phase} {x.Done}/{x.Total} {x.Text}")));
            using var cancel = new CancellationTokenSource();
            cancel.Cancel();
            bool stopped = false;
            try { asyncSession.CompileAsync(new[] { TargetScript }, null, null, null, null, new fire.Native.BuildMonitor(cancel.Token)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { stopped = true; }
            Check(stopped && !asyncSession.HasProgram, "DebugSession.CompileAsync: abgebrochen (OperationCanceledException) bleibt kein Programm", $"{stopped} {asyncSession.HasProgram}");
            asyncSession.Reset();
        }
        bool compiled = session.Compile(new[] { TargetScript });
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
        Check(ended && output.Contains("r 101"), "RunToCompletion: das Programm endet mit dem gesetzten Wert", string.Join(" | ", output));

        // a thread of the program starts in continue mode and stops at a breakpoint; its output carries its name
        VM.ResetTerminateForTests();
        while (paused.TryTake(out _)) { }
        output.Clear();
        var added = new ConcurrentQueue<fire.Editor.DebugThreadContext>();
        session.ThreadAdded += added.Enqueue;
        compiled = session.Compile(new[] { "fire {\n    var t = 1\n    print(\"in thread \" + t)\n}\nvar m = 2\nprint(\"main \" + m)\n" });
        src = session.FirstUserSourceIndex;
        session.UpdateBreakpoints(new[] { (src, 3) });
        session.Continue(new HashSet<(int, int)> { (src, 6) }); // the main program waits at its last line
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
            Check(threadEnded && output.Contains("[Fire #1] in thread 1"), "die Ausgabe eines Threads traegt seinen Namen", string.Join(" | ", output));
            session.SelectThread(session.Threads.First(t => t.IsMain));
            session.RunToCompletion();
            bool mainDone = false;
            while (paused.TryTake(out var c, TimeSpan.FromSeconds(20))) if (c.IsMain && c.IsFinished) { mainDone = true; break; }
            Check(mainDone && output.Contains("main 2"), "danach endet das Hauptprogramm", string.Join(" | ", output));
        }
        session.Reset();
        Check(FireRuntime.ThreadBodyInterceptor == null && !session.HasProgram, "Reset gibt das Ziel frei");
        VM.ResetTerminateForTests();
        Console.WriteLine(_failures == 0 ? "Alle Debug-Sitzungs-Pruefungen bestanden." : $"FEHLER: {_failures} Debug-Sitzungs-Pruefung(en) fehlgeschlagen.");
    }
}
