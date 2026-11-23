using System.Diagnostics;
using fire.Tooling;
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

        Devices(dir, forge.Value);
        Debug(dir, forge.Value, source, env);
        if (OperatingSystem.IsLinux()) Ending(dir, forge.Value);
    }

    // ---- the children of spark end with it (ChildProcesses): SIGTERM kills them, and fire also ends when spark is killed (PR_SET_PDEATHSIG) ----
    /// <summary>The stand-in for spark (FIRE_TESTS_ONLY=cvmrun-child, args: fire, the program file, FIRE_PKG_PATH): ties its children to itself as spark does, starts
    /// `fire run` and a plain program (`sleep`, as a native build of spark's Run) from a thread that ends at once (the kernel's parent-death signal follows the
    /// thread that started a child), then says "children &lt;fire&gt; &lt;sleep&gt;" and waits.</summary>
    public static void ChildHost(string[] args)
    {
        ChildProcesses.TieToThisProcess();
        int firePid = 0, sleepPid = 0;
        var starter = new Thread(() =>
        {
            firePid = CVmLaunch.Start(args[0], args[1], Array.Empty<string>(), Path.GetDirectoryName(args[1]), _ => { }, null, new Dictionary<string, string> { ["FIRE_PKG_PATH"] = args[2] }).Id;
            sleepPid = NativeLaunch.Start("sleep", new[] { "300" }, null, _ => { }).Id;
        });
        starter.Start();
        starter.Join();
        Thread.Sleep(1000);   // (the thread is gone: its end must not end fire)
        Console.WriteLine($"children {firePid} {sleepPid}");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
    }

    /// <summary>Whether the process runs (not ended, not a zombie nobody reaps - in a container the orphans' new parent may not).</summary>
    private static bool Alive(int pid)
    {
        try
        {
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int close = stat.LastIndexOf(')');
            return close > 0 && close + 2 < stat.Length && stat[close + 2] != 'Z' && stat[close + 2] != 'X';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool EndsWithin(int pid, int ms)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (Alive(pid)) { if (DateTime.UtcNow > deadline) return false; Thread.Sleep(20); }
        return true;
    }

    private static void KillIfAlive(int pid)
    {
        if (pid <= 0 || !Alive(pid)) return;
        try { using var p = Process.GetProcessById(pid); p.Kill(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static void Ending(string dir, (string Program, IReadOnlyList<string> Prefix) forge)
    {
        string source = Path.Combine(dir, "long.fire");
        File.WriteAllText(source, "#import \"time\"\nvar i = 0\nwhile (i < 600) {\n    Sleep(100)\n    i = i + 1\n}\nprint(\"end\")\n");
        string fbc = Path.Combine(dir, "bin", "long.fbc");
        var built = ForgeProcess.Run(new[] { "bytecode", source, "-m", "RELEASE", "-o", fbc }, new ForgeSink(), CancellationToken.None, dir, forge);
        if (!built.Ok || built.Output("vm") is not { } vm) { Check("Ende mit spark: Programmdatei", false, built.Error + "\n" + built.Log); return; }
        string pkgPath = CVmLaunch.Environment(built.Outputs.Where(o => o.Kind == "library").Select(o => o.Path).ToList()).GetValueOrDefault("FIRE_PKG_PATH", "");
        string self = Environment.ProcessPath!;
        foreach (bool hard in new[] { false, true })
        {
            var info = new ProcessStartInfo(self) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(self) == "dotnet") info.ArgumentList.Add(typeof(CVmRunTests).Assembly.Location);
            foreach (string a in new[] { vm, fbc, pkgPath }) info.ArgumentList.Add(a);
            info.Environment["FIRE_TESTS_ONLY"] = "cvmrun-child";
            using var host = Process.Start(info)!;
            var said = host.StandardOutput.ReadLineAsync();
            int firePid = 0, sleepPid = 0;
            if (said.Wait(60000) && said.Result is { } line && line.StartsWith("children ", StringComparison.Ordinal))
            {
                var parts = line.Split(' ');
                firePid = int.Parse(parts[1]);
                sleepPid = int.Parse(parts[2]);
            }
            try
            {
                if (firePid == 0) { Check("Ende mit spark: der Stellvertreter startet fire und ein Programm", false, said.IsCompleted ? said.Result : "(nichts)"); return; }
                bool survived = Alive(firePid) && Alive(sleepPid);
                if (!hard)
                {
                    // spark gets SIGTERM: ProcessExit/the handler of the signal kill its children (fire and the plain program)
                    Process.Start("kill", new[] { "-TERM", host.Id.ToString() })!.WaitForExit();
                    bool fireEnded = EndsWithin(firePid, 10000), sleepEnded = EndsWithin(sleepPid, 10000);
                    Check("Ende mit spark: SIGTERM beendet spark und seine Kinder (fire, ein natives Programm); das Ende des startenden Threads beendet fire nicht",
                        survived && fireEnded && sleepEnded && host.WaitForExit(10000), $"vorher {survived}, fire {fireEnded}, Programm {sleepEnded}");
                }
                else
                {
                    // spark is killed (nothing of it runs any more): fire ends by itself (FIRE_END_WITH_PARENT, PR_SET_PDEATHSIG)
                    host.Kill();
                    bool fireEnded = EndsWithin(firePid, 10000);
                    Check("Ende mit spark: wird spark getoetet (SIGKILL), endet fire von selbst (PR_SET_PDEATHSIG)", survived && fireEnded, $"vorher {survived}, fire {fireEnded}");
                }
            }
            finally
            {
                try { if (!host.HasExited) host.Kill(); } catch (InvalidOperationException) { }
                KillIfAlive(firePid);
                KillIfAlive(sleepPid);
            }
        }

        // a grandchild that inherits the variable (fire started by a program, by forge) does not take it: its parent is another process
        var grand = new ProcessStartInfo("sh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        grand.ArgumentList.Add("-c");
        grand.ArgumentList.Add("\"$0\" run \"$1\" & echo $!; wait");
        grand.ArgumentList.Add(vm);
        grand.ArgumentList.Add(fbc);
        grand.Environment["FIRE_PKG_PATH"] = pkgPath;
        grand.Environment[ChildProcesses.EnvironmentVariable] = Environment.ProcessId.ToString();
        using (var sh = Process.Start(grand)!)
        {
            int pid = int.TryParse(sh.StandardOutput.ReadLine(), out int p) ? p : 0;
            Thread.Sleep(500);
            bool runs = pid > 0 && Alive(pid);
            try { sh.Kill(); } catch (InvalidOperationException) { }
            Thread.Sleep(300);
            Check("Ende mit spark: ein Enkel mit geerbter Variable (Eltern ein anderer Prozess) laeuft weiter", runs && Alive(pid), $"pid {pid}");
            KillIfAlive(pid);
        }
    }

    // ---- the shared device: a program in fire uses the devices of spark's manager (FIRE_DEVICE_HOST, DeviceHostServer, native/vm/vm_devhost.c) ----
    private static void Devices(string dir, (string Program, IReadOnlyList<string> Prefix) forge)
    {
        string source = Path.Combine(dir, "devices.fire");
        File.WriteAllText(source, """
            #import "devices"
            print("shared " + DeviceManager.IsShared())
            print("default " + DeviceManager.Host.DefaultIdentifier())
            var d = DeviceManager.Get("loopback:echo")
            print("connect " + d.Connect() + " " + d.Connected())
            print("write " + d.WriteString("ping from fire"))
            print("echo " + d.WaitForString("ping from fire", 5000))
            print("command " + d.DoCommand("line"))
            print("echo2 " + d.WaitForString("line", 5000))
            """);
        string fbc = Path.Combine(dir, "bin", "devices.fbc");
        var built = ForgeProcess.Run(new[] { "bytecode", source, "-m", "RELEASE", "-o", fbc }, new ForgeSink(), CancellationToken.None, dir, forge);
        if (!built.Ok || built.Output("vm") is not { } vm) { Check("Geraete: Programmdatei", false, built.Error + "\n" + built.Log); return; }
        var libraries = built.Outputs.Where(o => o.Kind == "library").Select(o => o.Path).ToList();
        (List<string> Lines, int? Code) RunIt(Dictionary<string, string>? extra)
        {
            var lines = new List<string>();
            int? code = null;
            var ended = new ManualResetEventSlim();
            var p = CVmLaunch.Start(vm, fbc, Array.Empty<string>(), dir, l => { lock (lines) lines.Add(l); }, c => { code = c; ended.Set(); }, CVmLaunch.Environment(libraries, extra));
            if (!ended.Wait(30000)) try { p.Kill(true); } catch (InvalidOperationException) { }
            return (lines, code);
        }

        // spark's manager: shared, with the loopback driver and a default device; what the program sends shows in its packet trace
        using var manager = fire.Device.Manager.DeviceManager.DeviceManager.CreateDefault(isShared: true);
        manager.RegisterDriver(new fire.Device.Manager.Drivers.Loopback.LoopbackDriver());
        manager.RefreshDevices(true);
        manager.DefaultIdentifier = "loopback:echo";
        var traced = new List<string>();
        manager.PacketCaptured += r => { lock (traced) traced.Add(r.Direction + " " + System.Text.Encoding.UTF8.GetString(r.Data).Trim()); };
        using var server = new fire.Device.Manager.DeviceManager.DeviceHostServer(manager);
        var watch = Stopwatch.StartNew();
        var (lines, code) = RunIt(new Dictionary<string, string> { [fire.Device.Manager.DeviceManager.DeviceHostServer.EnvironmentVariable] = server.EnvironmentValue });
        Console.WriteLine($"    Geraete ueber FIRE_DEVICE_HOST: {watch.ElapsedMilliseconds} ms fuer das Programm");
        string all = Text(lines);
        Check("Geraete: das Programm in fire nimmt die Geraete von spark (geteilt, das Standardgeraet, verbunden, Echo ueber die Verbindung)",
            code == 0 && lines.Contains("shared True") && lines.Contains("default loopback:echo") && lines.Contains("connect True True") && lines.Contains("write True")
            && lines.Contains("echo True") && lines.Contains("command True") && lines.Contains("echo2 True"), $"code {code}: {all}");
        string trace;
        lock (traced) trace = string.Join(" | ", traced);
        Check("Geraete: was das Programm sendet und empfaengt, steht im Paketmitschnitt von spark", traced.Any(t => t.Contains("ping from fire")) && traced.Count >= 2, trace);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (server.Connected > 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Check("Geraete: das Ende des Programms schliesst seine Verbindung; das Geraet bleibt beim Manager von spark",
            server.Connected == 0 && server.ConnectionsSoFar == 1 && manager.GetHandleByIdentifier("loopback:echo") != null, $"{server.Connected} / {server.ConnectionsSoFar}");

        // a wrong token: the host does not answer, the program takes the devices of the machine (the serial ports - the library of the VM has no loopback device:
        // spark's `loopback:echo` is not there)
        var (wrongLines, _) = RunIt(new Dictionary<string, string> { [fire.Device.Manager.DeviceManager.DeviceHostServer.EnvironmentVariable] = $"tcp:127.0.0.1:{server.Port};token=nope" });
        Check("Geraete: ein falsches Token bekommt die Geraete von spark nicht (das Programm nimmt die der Maschine)",
            wrongLines.Contains("shared False") && wrongLines.Contains("default ") && wrongLines.Any(l => l.Contains("No device with identifier 'loopback:echo'")) && server.ConnectionsSoFar == 2,
            Text(wrongLines));
        var (aloneLines, _) = RunIt(null);
        Check("Geraete: ohne FIRE_DEVICE_HOST wie bisher die Geraete der Maschine", aloneLines.Contains("shared False") && aloneLines.Any(l => l.Contains("No device with identifier 'loopback:echo'")) && server.ConnectionsSoFar == 2, Text(aloneLines));
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
            // (spark's panels ask at once, at the entry: the main thread waited there with the GIL, the agent for it - forever)
            bool entryRead;
            try { entryRead = main.Scopes() != null && main.Globals() != null && main.CurrentTrace().Count >= 1; }
            catch (NativeDebugException ex) { entryRead = false; Console.WriteLine("    " + ex.Message); }
            Check("Debuggen: Scopes, Globals und Aufrufstapel schon am Einstieg", entryRead);
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
