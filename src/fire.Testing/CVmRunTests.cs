using System.Diagnostics;
using fire.Compiler;
using fire.Debugging;
using fire.Editor;

/// <summary>
/// spark on the VM in C (docs/ROADMAP_C_PORT.md, phase B step 9): a program of the VM runs as a child process `fire run x.fbc` (CVmLaunch) - against a fake `fire`
/// (the command line, the output of both streams, the input, the exit code, Stop, a program that ends without the debugger), then with the real forge and `fire`
/// beside the tests: `forge bytecode --report json` (the program file, the libraries of the packages, the `fire` of its widths, an overlay, an error), a console
/// program (input, arguments, exit code of terminate), a window program without a screen, and a run under the debugger (engine vm, a breakpoint, variables,
/// a step, threads, the input over the pipe, the end with its exit code). FIRE_TESTS_ONLY=cvmrun.
/// </summary>
static class CVmRunTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        Console.WriteLine($"[{(ok ? "OK" : "FEHLER")}] spark auf der C-VM: {title}");
        if (!ok)
        {
            _failures++;
            if (detail != null) Console.WriteLine("    " + detail.Replace("\n", "\n    "));
        }
    }

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "fire-cvmrun-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            if (!OperatingSystem.IsWindows()) Fake(root);
            Real(root);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        Console.WriteLine(_failures == 0 ? "Alle Pruefungen von spark auf der C-VM bestanden." : $"FEHLER: {_failures} Pruefung(en) von spark auf der C-VM fehlgeschlagen.");
        return _failures;
    }

    private static string Script(string root, string name, string body)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>Waits until <paramref name="lines"/> has a line that <paramref name="match"/> takes (or the time is up).</summary>
    private static bool WaitFor(List<string> lines, Func<string, bool> match, int ms = 20000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < deadline)
        {
            lock (lines) if (lines.Any(match)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static string Text(List<string> lines) { lock (lines) return string.Join(" | ", lines); }

    // ---- against a fake fire ------------------------------------------------------------------------------------------------------------
    private static void Fake(string root)
    {
        Check("Schalter: FIRE_SPARK_VM=dotnet behaelt die C#-VM", Probe(null) == (Environment.GetEnvironmentVariable(CVmLaunch.SwitchVariable)?.Trim().ToLowerInvariant() is "dotnet" or ".net" or "net" or "cs"));
        Check("Befehlszeile: run <fbc> -- <argumente>, ohne Argumente kein --",
            CVmLaunch.Arguments("/p/x.fbc", new[] { "a", "b c" }).SequenceEqual(new[] { "run", "/p/x.fbc", "--", "a", "b c" }) && CVmLaunch.Arguments("/p/x.fbc", Array.Empty<string>()).SequenceEqual(new[] { "run", "/p/x.fbc" }));
        var env = CVmLaunch.Environment(new[] { "/a/lib/libfire_pkg_io_io.so", "/a/lib/libfire_pkg_time_time.so", "/b/libfire_gfx.so" }, new Dictionary<string, string> { ["X"] = "1" });
        Check("Umgebung: FIRE_PKG_PATH mit den Ordnern der Bibliotheken (jeder einmal)", env.TryGetValue("FIRE_PKG_PATH", out var pkg) && pkg.StartsWith("/a/lib" + Path.PathSeparator + "/b") && env["X"] == "1", pkg);

        string fake = Script(root, "fake-fire.sh", """
            echo "args: $*"
            echo "pkg: $FIRE_PKG_PATH"
            read line
            echo "got: $line"
            echo "err line" >&2
            exit 3
            """);
        var lines = new List<string>();
        int? code = null;
        var ended = new ManualResetEventSlim();
        var p = CVmLaunch.Start(fake, "/p/x.fbc", new[] { "a", "b c" }, root, l => { lock (lines) lines.Add(l); }, c => { code = c; ended.Set(); }, env);
        bool wrote = CVmLaunch.WriteInput(p, "hello from spark");
        bool done = ended.Wait(10000);
        Check("Falsches fire: Befehlszeile, Umgebung, Eingabe, stdout und stderr, Exitcode", done && wrote && code == 3 && WaitFor(lines, l => l == "err line", 2000)
            && lines.Contains("args: run /p/x.fbc -- a b c") && lines.Contains("got: hello from spark") && lines.Any(l => l.StartsWith("pkg: /a/lib")), $"code {code}, {Text(lines)}");
        Check("Falsches fire: Eingabe nach dem Ende geht nicht", !CVmLaunch.WriteInput(p, "late"));

        string sleeper = Script(root, "fake-sleep.sh", "echo started\nexec sleep 60\n");
        var sleepLines = new List<string>();
        var sp = CVmLaunch.Start(sleeper, "/p/x.fbc", Array.Empty<string>(), root, l => { lock (sleepLines) sleepLines.Add(l); }, null, null);
        WaitFor(sleepLines, l => l == "started", 5000);
        var watch = Stopwatch.StartNew();
        sp.Kill(entireProcessTree: true);   // (Stop: StopNativeRun)
        bool killed = sp.WaitForExit(5000);
        Check("Falsches fire: Stop beendet das Programm", killed && watch.ElapsedMilliseconds < 5000, $"{watch.ElapsedMilliseconds} ms");

        string quitter = Script(root, "fake-nodebug.sh", "echo 'no debugger here'\nexit 2\n");
        string? why = null;
        watch.Restart();
        try { CVmLaunch.LaunchDebug(quitter, "/p/x.fbc", Array.Empty<string>(), root, TimeSpan.FromSeconds(20), null).Dispose(); }
        catch (NativeDebugException ex) { why = ex.Message; }
        Check("Falsches fire: ein Programm, das sich nicht verbindet, ist ein Fehler mit seiner Ausgabe (ohne die Zeit abzuwarten)",
            why != null && why.Contains("exit code 2") && why.Contains("no debugger here") && watch.ElapsedMilliseconds < 15000, why);
    }

    private static bool Probe(bool? value)
    {
        CVmLaunch.DotnetOverride = value;
        try { return CVmLaunch.UseDotnet; }
        finally { CVmLaunch.DotnetOverride = null; }
    }

    // ---- the real forge and fire ----------------------------------------------------------------------------------------------------------
    private const string Console1 = """
        #import "io"
        var name = IO.Stdio.ReadLine()
        print("hello " + name + " " + Environment.Args.length)
        var total = 0
        for (var i = 1; i <= 3; i = i + 1) {
            total = total + i
        }
        print("total " + total)
        terminate(4)
        """;

    private static void Real(string root)
    {
        var forge = ForgeProcess.Locate(AppContext.BaseDirectory);
        if (forge == null) { Check("forge neben den Tests", false, AppContext.BaseDirectory); return; }
        string dir = Path.Combine(root, "real");
        Directory.CreateDirectory(dir);
        string source = Path.Combine(dir, "console.fire");
        File.WriteAllText(source, Console1);

        // the program file through the report
        string release = Path.Combine(dir, "bin", "vm-rel", "console.fbc");
        var built = ForgeProcess.Run(new[] { "bytecode", source, "-m", "RELEASE", "-o", release }, new ForgeSink(), CancellationToken.None, dir, forge);
        string? vm = built.Output("vm");
        var libraries = built.Outputs.Where(o => o.Kind == "library").Select(o => o.Path).ToList();
        Check("forge bytecode --report json: Programmdatei, fire der Breiten, die Bibliothek von io", built is { Ok: true, Ended: true } && built.Output("program") == Path.GetFullPath(release)
            && File.Exists(release) && vm != null && File.Exists(vm) && libraries.Any(l => Path.GetFileName(l).Contains("io") && File.Exists(l)),
            $"{built.Error}\n{string.Join("\n", built.Outputs)}\n{built.Log}");
        if (vm == null || !built.Ok) return;
        var env = CVmLaunch.Environment(libraries);

        // a console program: its input, its arguments, the exit code of terminate
        var lines = new List<string>();
        int? code = null;
        var ended = new ManualResetEventSlim();
        var p = CVmLaunch.Start(vm, release, new[] { "x", "y" }, dir, l => { lock (lines) lines.Add(l); }, c => { code = c; ended.Set(); }, env);
        CVmLaunch.WriteInput(p, "spark");
        bool done = ended.Wait(30000);
        Check("Konsolenprogramm: Eingabe, Argumente, Ausgabe, Exitcode von terminate", done && code == 4 && lines.Contains("hello spark 2") && lines.Contains("total 6"), $"code {code}: {Text(lines)}");

        // the text of the editor (an overlay) goes before the file; an error comes as a diagnostic with its line
        using (var overlays = new ForgeOverlays())
        {
            overlays.Add(source, Console1.Replace("\"total \"", "\"sum \""));
            string over = Path.Combine(dir, "bin", "over.fbc");
            var withOverlay = ForgeProcess.Run(new[] { "bytecode", source, "-m", "RELEASE", "-o", over }.Concat(overlays.Args).ToList(), new ForgeSink(), CancellationToken.None, dir, forge);
            var olines = new List<string>();
            var oended = new ManualResetEventSlim();
            if (withOverlay.Ok)
            {
                var op = CVmLaunch.Start(vm, over, Array.Empty<string>(), dir, l => { lock (olines) olines.Add(l); }, _ => oended.Set(), env);
                CVmLaunch.WriteInput(op, "o");
                oended.Wait(30000);
            }
            Check("forge bytecode: der Text des Editors (Overlay) statt der Datei", withOverlay.Ok && olines.Contains("sum 6"), $"{withOverlay.Error} {Text(olines)}");
        }
        using (var overlays = new ForgeOverlays())
        {
            overlays.Add(source, Console1.Replace("total = total + i", "total = total + missing"));
            var bad = ForgeProcess.Run(new[] { "bytecode", source, "-o", Path.Combine(dir, "bin", "bad.fbc") }.Concat(overlays.Args).ToList(), new ForgeSink(), CancellationToken.None, dir, forge);
            var error = bad.Diagnostics.FirstOrDefault(d => d.IsError);
            Check("forge bytecode: ein Fehler mit Zeile und Code, kein Programm", bad is { Ok: false, Ended: true } && error is { Line: 6 } && bad.Output("program") == null, $"{bad.ExitCode} {error?.ToLogLine()}\n{bad.Log}");
        }

        // a window program without a screen (libfire_gfx from the report, FIRE_DISPLAY=headless)
        string windowSource = Path.Combine(dir, "window.fire");
        File.WriteAllText(windowSource, """
            #import "ui"
            var fb = new Framebuffer(120, 60)
            var win = new Window(fb, "run")
            var ui = new UI.Root(fb, win)
            ui.Add(new UI.Button("OK", 10, 10))
            var n = 0
            while (n < 5 && ui.Tick()) { n = n + 1 }
            print("frames " + n)
            win.Close()
            """);
        string windowFbc = Path.Combine(dir, "bin", "window.fbc");
        var wbuilt = ForgeProcess.Run(new[] { "bytecode", windowSource, "-m", "RELEASE", "-o", windowFbc }, new ForgeSink(), CancellationToken.None, dir, forge);
        var wlibs = wbuilt.Outputs.Where(o => o.Kind == "library").Select(o => o.Path).ToList();
        var wlines = new List<string>();
        int? wcode = null;
        var wended = new ManualResetEventSlim();
        if (wbuilt.Ok)
        {
            var wp = CVmLaunch.Start(wbuilt.Output("vm") ?? vm, windowFbc, Array.Empty<string>(), dir, l => { lock (wlines) wlines.Add(l); }, c => { wcode = c; wended.Set(); },
                CVmLaunch.Environment(wlibs, new Dictionary<string, string> { ["FIRE_DISPLAY"] = "headless" }));
            if (!wended.Wait(30000)) try { wp.Kill(true); } catch (InvalidOperationException) { }
        }
        Check("Fensterprogramm ohne Bildschirm: libfire_gfx aus dem Bericht, 5 Bilder, Ende", wbuilt.Ok && wlibs.Any(l => Path.GetFileName(l).Contains("fire_gfx")) && wcode == 0 && wlines.Contains("frames 5"),
            $"{wbuilt.Error} libs {string.Join(", ", wlibs)} code {wcode}: {Text(wlines)}\n{(wbuilt.Ok ? "" : wbuilt.Log)}");

        Debug(dir, forge.Value, source, env);
    }

    private static void Debug(string dir, (string Program, IReadOnlyList<string> Prefix) forge, string source, Dictionary<string, string> env)
    {
        string debugFbc = Path.Combine(dir, "bin", "vm-dbg", "console.fbc");
        var built = ForgeProcess.Run(new[] { "bytecode", source, "-m", "DEBUG", "-o", debugFbc }, new ForgeSink(), CancellationToken.None, dir, forge);
        if (!built.Ok || built.Output("vm") is not { } vm) { Check("Debuggen: Programmdatei (Debug)", false, built.Error + "\n" + built.Log); return; }
        NativeDebugTarget target;
        try { target = CVmLaunch.LaunchDebug(vm, debugFbc, new[] { "d" }, dir, TimeSpan.FromSeconds(30), env); }
        catch (NativeDebugException ex) { Check("Debuggen: fire verbindet sich", false, ex.Message); return; }
        using (target)
        {
            var output = new List<string>();
            target.Output += (_, l) => { lock (output) output.Add(l); };
            int src = Enumerable.Range(0, target.SourceFiles.Count).FirstOrDefault(i => target.SourceFiles[i] is { } f && Path.GetFullPath(f) == Path.GetFullPath(source), -1);
            Check("Debuggen: hello mit engine vm, Quellen mit der Datei, ohne Operandenstapel", target.Engine == "vm" && !target.HasOperandStack && src >= 0 && target.SourcesNotMatching(new[] { Console1 }).Count == 0,
                $"engine {target.Engine}, sources {string.Join(", ", target.SourceFiles)}, src {src}");
            if (src < 0) return;
            var main = target.MainThread;
            // (the program reads its input first: the pipe, as in spark's Output panel)
            Check("Debuggen: Eingabe ueber die Pipe", target.Process is { } process && CVmLaunch.WriteInput(process, "dbg"));
            bool paused = main.RunUntilBreakpoint(new HashSet<(int, int)> { (src, 6) }, () => false);
            var at = main.CurrentLocation;
            var vars = main.Scopes().SelectMany(s => s.Variables).Concat(main.Globals()).ToList();
            string? Var(string n) => vars.FirstOrDefault(v => v.Name == n)?.Display;
            Check("Debuggen: Haltepunkt in der Schleife, Variablen", paused && at == (src, 6) && Var("total") == "0" && Var("i") == "1" && Var("name") is { } nm && nm.Contains("dbg"),
                $"paused {paused} at {at}, {string.Join(", ", vars.Select(v => v.Name + "=" + v.Display))}");
            Check("Debuggen: Ausgabe bis zum Halt", WaitFor(output, l => l == "hello dbg 1", 5000), Text(output));
            bool again = main.RunUntilBreakpoint(new HashSet<(int, int)> { (src, 6) }, () => false);
            vars = main.Scopes().SelectMany(s => s.Variables).Concat(main.Globals()).ToList();
            Check("Debuggen: weiter bis zum naechsten Halt (i = 2, total = 1)", again && main.CurrentLocation == (src, 6) && Var("i") == "2" && Var("total") == "1",
                string.Join(", ", vars.Select(v => v.Name + "=" + v.Display)));
            bool stepped = main.StepLine();
            Check("Debuggen: Schritt", stepped && main.CurrentLocation.Line is 5 or 6 && main.CurrentLocation.SourceIndex == src, $"{stepped} {main.CurrentLocation}");
            Check("Debuggen: Threads", target.Threads.Count >= 1 && target.Threads.Any(t => t.IsMain));
            bool more = main.RunUntilEnd(() => false);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!target.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(20);
            Check("Debuggen: bis zum Ende, Exitcode von terminate, Ausgabe", !more && target.HasExited && WaitFor(output, l => l == "total 6", 5000) && (target.ExitCode == 4 || target.Process?.ExitCode == 4),
                $"more {more}, exited {target.HasExited}, code {target.ExitCode}, {Text(output)}");
        }
    }
}
