using System.Diagnostics;
using fire.Compiler;
using fire.Editor;
using fire.Native;

/// <summary>spark builds through forge (docs/ROADMAP_C_PORT.md, L4; docs/tools/forge.md, "The report for tools"): the report of `forge build|publish --report json` written and read
/// (every type of event, escaping, unknown types and fields, the log of a step without the lines that came already), and spark's side of it (ForgeProcess) against a fake forge - a
/// shell script: the events into the sink and the outcome, the arguments it gets (`--report json`, the overlays), a forge that crashes, and Cancel (the line "cancel" on its input,
/// SIGTERM, after the grace the whole process tree killed). With a forge beside the tests also the real one: an error in an overlay with its file and line, a library checked.
/// Alone: FIRE_TESTS_ONLY=forgereport (seconds; FIRE_TESTS_FILTER narrows it by the title of a check).</summary>
static class ForgeReportTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        string? filter = Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER");
        if (!string.IsNullOrWhiteSpace(filter) && !filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(f => title.Contains(f, StringComparison.OrdinalIgnoreCase))) return;
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Forge-Bericht: {title}" : $"FEHLER: Forge-Bericht: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    public static int Run()
    {
        Console.WriteLine("=== Bauen ueber forge: der Bericht (--report json) und spark ===");
        string root = Path.Combine(Path.GetTempPath(), "fire-forgereport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Format();
            Overlays();
            if (OperatingSystem.IsWindows()) Console.WriteLine("(the fake forge is a shell script: skipped on Windows)");
            else FakeForge(root);
            RealForge(root);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        Console.WriteLine(_failures == 0 ? "Alle Forge-Bericht-Pruefungen bestanden." : $"FEHLER: {_failures} Forge-Bericht-Pruefung(en) fehlgeschlagen.");
        return _failures;
    }

    private static List<ForgeEvent> ParseAll(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => ForgeReport.TryParse(l, out var e) ? e : null).Where(e => e != null).Cast<ForgeEvent>().ToList();

    // ---- the format ------------------------------------------------------------------------------------------------------------------------
    private static void Format()
    {
        var text = new StringWriter();
        var w = new ForgeReportWriter(text);
        w.Start("build", "App.firesln");
        w.Progress(new BuildProgress(BuildPhase.CompileC, "Compiling 3 modules", 2, 3));
        w.Background(new BackgroundWork.Item(7, "Installing the package fire-io (5/53)", 4, 53));
        w.Background(null);
        w.Log("gcc -O2 \"a b.c\" -o x\twith a tab and ünïcode");
        w.Project = "App";
        w.Diagnostic(new Diagnostic(12, "Unknown identifier 'y'", DiagnosticSeverity.Error, "E4022", "/src/App/main.fire", 5));
        w.Diagnostic(new Diagnostic(3, "obsolete", DiagnosticSeverity.Warning, "W204", null, 0));
        w.Output("program", "/src/App/bin/linux-rel/App");
        w.ProjectResult("App", false, error: "it does not compile");
        w.Project = null;
        w.End(false, 1, summary: "Projects: 0 succeeded, 1 failed, 0 skipped.", error: "App: it does not compile");
        w.Log("after the end");   // (nothing after the end)
        var lines = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var events = ParseAll(text.ToString());
        Check("Format: ein Objekt je Zeile, jede Zeile lesbar, nichts nach end", lines.Length == 10 && events.Count == 10, text.ToString());
        Check("Format: start mit Version, Befehl und pid", events[0] is { Type: "start", Command: "build", FormatVersion: ForgeReport.Version, Text: "App.firesln" }, lines[0]);
        Check("Format: progress mit Phase, Text, Zaehler", events[1].Progress is { Phase: BuildPhase.CompileC, Done: 2, Total: 3, Text: "Compiling 3 modules" }, lines[1]);
        Check("Format: background mit Text und Zaehler, ohne Text = vorbei", events[2] is { Type: "background", Text: "Installing the package fire-io (5/53)", Done: 4, Total: 53 } && events[3] is { Type: "background", Text: null }, lines[2] + "\n" + lines[3]);
        Check("Format: log mit Anfuehrungszeichen, Tab und Umlauten", events[4].Text == "gcc -O2 \"a b.c\" -o x\twith a tab and ünïcode", lines[4]);
        Check("Format: diagnostic (Datei, Zeile, Spalte, Code, Schwere, Text, Projekt)",
            events[5].Diagnostic == new Diagnostic(12, "Unknown identifier 'y'", DiagnosticSeverity.Error, "E4022", "/src/App/main.fire", 5) && events[5].Project == "App", lines[5]);
        Check("Format: Warnung ohne Datei", events[6].Diagnostic is { IsWarning: true, File: null, Code: "W204", Line: 3 }, lines[6]);
        Check("Format: output mit Art, Pfad, Projekt", events[7] is { Type: "output", Kind: "program", Path: "/src/App/bin/linux-rel/App", Project: "App" }, lines[7]);
        Check("Format: project", events[8] is { Type: "project", Project: "App", Ok: false, Skipped: false, Error: "it does not compile" }, lines[8]);
        Check("Format: end mit ok, exitCode, cancelled, summary, error", events[9] is { Type: "end", Ok: false, ExitCode: 1, Cancelled: false, Summary: "Projects: 0 succeeded, 1 failed, 0 skipped.", Error: "App: it does not compile" }, lines[9]);

        Check("Lesen: keine JSON-Zeile ist kein Ereignis", !ForgeReport.TryParse("gcc: error: x.c: No such file", out _) && !ForgeReport.TryParse("{ kaputt", out _) && !ForgeReport.TryParse("{\"no\":\"type\"}", out _) && !ForgeReport.TryParse("", out _));
        Check("Lesen: unbekannte Arten und Felder stoeren nicht", ForgeReport.TryParse("""{"type":"later","x":[1,2],"text":"t"}""", out var later) && later is { Type: "later", Text: "t" }
            && ForgeReport.TryParse("""{"type":"log","text":"a","new":{"deep":true}}""", out var log) && log.Text == "a");
        Check("Lesen: Phasen hin und zurueck", Enum.GetValues<BuildPhase>().All(p => ForgeReport.ParsePhase(ForgeReport.PhaseName(p)) == p) && ForgeReport.ParsePhase("unknown") == null);
        Check("Lesen: progress einer unbekannten Phase ist kein Fortschritt", ForgeReport.TryParse("""{"type":"progress","phase":"warp","text":"x"}""", out var warp) && warp.Progress == null);

        // the log of a step at its end: what came already as it ran is not repeated (the same line twice counts twice)
        var t2 = new StringWriter();
        var w2 = new ForgeReportWriter(t2);
        w2.Log("gcc a.c");
        w2.Log("same");
        w2.LogBlock("gcc a.c\r\nsame\nsame\nlinked\n");
        var logged = ParseAll(t2.ToString()).Select(e => e.Text).ToList();
        Check("LogBlock: nur was noch nicht kam", logged.SequenceEqual(new[] { "gcc a.c", "same", "same", "linked" }), string.Join(" | ", logged));

        // the text for people (forge build --all without --report)
        var o = new StringWriter();
        var e2 = new StringWriter();
        var human = new ForgeTextReporter(o, e2);
        human.Log("------ 1/2 Lib (library) ------");
        human.Diagnostic(new Diagnostic(2, "Unknown identifier 'y'", DiagnosticSeverity.Error, "E4022", "/x/a.fire"));
        human.End(false, 1, summary: "Projects: 0 succeeded, 1 failed, 0 skipped.", error: "Lib: it does not compile");
        Check("Text: Log auf stdout, Fehler auf stderr", o.ToString().Contains("------ 1/2 Lib") && o.ToString().Contains("Projects: 0 succeeded")
            && e2.ToString().Contains("/x/a.fire(2): error E4022: Unknown identifier 'y'") && e2.ToString().Contains("Lib: it does not compile"), o + "\n" + e2);

        // the command line
        var parsed = CommandLineParser.Parse(new[] { "build", "App.firesln", "--all", "--report", "json", "--overlay", "/a/x.fire=/tmp/1-x.fire", "--debug-info", "--check" });
        Check("Befehlszeile: --report json, --overlay, --debug-info, --check, --all", parsed.Error == null && parsed.Report == "json" && parsed.All && parsed.DebugInfo && parsed.CheckOnly
            && parsed.Overlays.TryGetValue(Path.GetFullPath("/a/x.fire"), out var of) && of == Path.GetFullPath("/tmp/1-x.fire"), parsed.Error);
        Check("Befehlszeile: --report nur json, nur bei build/publish", CommandLineParser.Parse(new[] { "build", "a.fire", "--report", "xml" }).Error != null && CommandLineParser.Parse(new[] { "run", "a.fire", "--report", "json" }).Error != null);
        Check("Befehlszeile: --overlay braucht Quelle=Datei", CommandLineParser.Parse(new[] { "build", "a.fire", "--overlay", "nothing" }).Error != null);
        Check("Befehlszeile: WantsReport", CommandLineRunner.WantsReport(new[] { "build", "a.fire", "--report", "json" }) && CommandLineRunner.WantsReport(new[] { "publish", "a.firesln", "--report=json" })
            && !CommandLineRunner.WantsReport(new[] { "run", "a.fire", "--report", "json" }) && !CommandLineRunner.WantsReport(new[] { "build", "a.fire" }));
    }

    // ---- the text of the editor --------------------------------------------------------------------------------------------------------
    private static void Overlays()
    {
        string folder;
        using (var o = new ForgeOverlays())
        {
            o.Add("/proj/App/main.fire", "print(1)");
            string untitled = o.AddUntitled("Untitled 1.fire", "print(2)");
            Check("Overlays: --overlay Quelle=Kopie mit dem Text", o.Args.Count == 2 && o.Args[0] == "--overlay" && o.Args[1].StartsWith(Path.GetFullPath("/proj/App/main.fire") + "=", StringComparison.Ordinal)
                && File.ReadAllText(o.Args[1].Substring(o.Args[1].IndexOf('=') + 1)) == "print(1)", string.Join(" ", o.Args));
            Check("Overlays: ein ungespeichertes Skript als Quelle", File.ReadAllText(untitled) == "print(2)" && o.Untitled.Contains(untitled));
            folder = Path.GetDirectoryName(untitled)!;
        }
        Check("Overlays: nach dem Bauen geloescht", !Directory.Exists(folder));
    }

    // ---- spark's side against a fake forge -----------------------------------------------------------------------------------------------
    private static string Script(string root, string name, string body)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static void FakeForge(string root)
    {
        string argsFile = Path.Combine(root, "args.txt");
        string ok = Script(root, "forge-ok.sh", $$"""
            for a in "$@"; do echo "$a"; done > '{{argsFile}}'
            echo '{"type":"start","version":1,"command":"build","pid":1}'
            echo '{"type":"background","text":"Installing the package fire-io (5/53)","done":4,"total":53}'
            echo '{"type":"background"}'
            echo '{"type":"progress","phase":"compile-fire","text":"Compiling fire","done":1,"total":4}'
            echo 'a line that is no JSON'
            echo 'on the error output' >&2
            echo '{"type":"log","text":"gcc -c x.c"}'
            echo '{"type":"diagnostic","severity":"warning","code":"W1","file":"/p/a.fire","line":3,"column":2,"message":"careful"}'
            echo '{"type":"diagnostic","severity":"error","code":"E4022","file":null,"line":7,"column":0,"message":"Unknown identifier"}'
            echo '{"type":"output","kind":"program","path":"/p/bin/linux-rel/App","project":"App"}'
            echo '{"type":"project","name":"App","ok":true,"skipped":false,"upToDate":false}'
            echo '{"type":"end","ok":true,"exitCode":0,"cancelled":false,"summary":"done"}'
            exit 0
            """);
        var logs = new List<string>();
        var progress = new List<BuildProgress>();
        var background = new List<BackgroundWork.Item?>();
        var diagnostics = new List<Diagnostic>();
        var sink = new ForgeSink
        {
            Log = l => { lock (logs) logs.Add(l); },
            Progress = progress.Add,
            Background = background.Add,
            Diagnostic = diagnostics.Add,
        };
        var outcome = ForgeProcess.Run(new[] { "build", "/p/App.firesln", "-p", "App", "--overlay", "/p/a.fire=/tmp/x" }, sink, CancellationToken.None, root, (ok, Array.Empty<string>()));
        var args = File.Exists(argsFile) ? File.ReadAllLines(argsFile) : Array.Empty<string>();
        Check("Laeufer: die Argumente, --report json angehaengt", args.SequenceEqual(new[] { "build", "/p/App.firesln", "-p", "App", "--overlay", "/p/a.fire=/tmp/x", "--report", "json" }), string.Join(" ", args));
        Check("Laeufer: ok, Ende gelesen, Zusammenfassung", outcome is { Ok: true, Ended: true, Cancelled: false, ExitCode: 0, Summary: "done" }, $"{outcome.Ok} {outcome.Ended} {outcome.ExitCode} {outcome.Error}");
        Check("Laeufer: Fehler und Warnungen mit Ort", outcome.Diagnostics.Count == 2 && diagnostics.Count == 2
            && outcome.Diagnostics[0] == new Diagnostic(3, "careful", DiagnosticSeverity.Warning, "W1", "/p/a.fire", 2) && outcome.Diagnostics[1] is { IsError: true, File: null, Line: 7, Code: "E4022" });
        Check("Laeufer: Ausgabe und Projekt", outcome.Output("program") == "/p/bin/linux-rel/App" && outcome.Outputs[0].Project == "App" && outcome.Projects is [{ Project: "App", Ok: true }]);
        Check("Laeufer: Fortschritt und Hintergrundarbeit", progress is [{ Phase: BuildPhase.CompileFire, Done: 1, Total: 4 }] && background.Count == 2 && background[0] is { Text: "Installing the package fire-io (5/53)", Total: 53 } && background[1] == null);
        Check("Laeufer: Log mit den Zeilen ohne JSON und der Fehlerausgabe", logs.Contains("gcc -c x.c") && logs.Contains("a line that is no JSON") && logs.Contains("on the error output")
            && outcome.Log.Contains("gcc -c x.c\n"), string.Join(" | ", logs));

        // a forge that crashes: no end of the report
        string crash = Script(root, "forge-crash.sh", """
            echo '{"type":"start","version":1,"command":"build","pid":1}'
            echo 'Unhandled exception. System.NullReferenceException' >&2
            exit 134
            """);
        var crashed = ForgeProcess.Run(new[] { "build", "x.fire" }, new ForgeSink(), CancellationToken.None, root, (crash, Array.Empty<string>()));
        Check("Laeufer: ein abgestuerztes forge (kein end)", crashed is { Ok: false, Ended: false, Cancelled: false, ExitCode: 134 } && crashed.Error != null && crashed.Log.Contains("NullReferenceException"), crashed.Error);
        var missing = ForgeProcess.Run(new[] { "build" }, new ForgeSink(), CancellationToken.None, root, (Path.Combine(root, "no-forge"), Array.Empty<string>()));
        Check("Laeufer: kein forge da", missing is { Ok: false, ExitCode: -1 } && missing.Error != null && missing.Error.Contains("could not be started"), missing.Error);

        // Cancel, the polite way: forge reads "cancel" on its input, ends its tools, reports the end as cancelled
        string marker = Path.Combine(root, "cancelled.txt");
        string polite = Script(root, "forge-cancel.sh", $$"""
            trap '' TERM
            echo '{"type":"start","version":1,"command":"build","pid":1}'
            echo '{"type":"progress","phase":"compile-c","text":"Compiling","done":0,"total":1}'
            while read line; do
              if [ "$line" = "cancel" ]; then
                echo got-cancel > '{{marker}}'
                echo '{"type":"log","text":"Build cancelled."}'
                echo '{"type":"end","ok":false,"exitCode":3,"cancelled":true,"summary":"Build cancelled."}'
                exit 3
              fi
            done
            exit 1
            """);
        using (var cts = new CancellationTokenSource())
        {
            var started = new ManualResetEventSlim();
            var sw = Stopwatch.StartNew();
            var cancelSink = new ForgeSink { Progress = _ => started.Set() };
            var task = Task.Run(() => ForgeProcess.Run(new[] { "build", "x.fire" }, cancelSink, cts.Token, root, (polite, Array.Empty<string>())));
            bool began = started.Wait(TimeSpan.FromSeconds(20));
            cts.Cancel();
            var cancelled = task.Wait(TimeSpan.FromSeconds(30)) ? task.Result : null;
            Check("Abbrechen: \"cancel\" auf der Eingabe, forge meldet abgebrochen", began && cancelled is { Cancelled: true, Ok: false, Ended: true, ExitCode: 3 } && File.Exists(marker) && sw.Elapsed < TimeSpan.FromSeconds(9),
                $"{began} {cancelled?.Cancelled} {cancelled?.ExitCode} {File.Exists(marker)} {sw.Elapsed}");
        }

        // Cancel, by SIGTERM (what a forge that does not read its input gets): the trap ends it
        string termMarker = Path.Combine(root, "term.txt");
        string term = Script(root, "forge-term.sh", $$"""
            on_term() {
              echo term > '{{termMarker}}'
              echo '{"type":"end","ok":false,"exitCode":3,"cancelled":true}'
              exit 3
            }
            trap on_term TERM
            echo '{"type":"progress","phase":"compile-c","text":"Compiling","done":0,"total":1}'
            while true; do sleep 0.1; done
            """);
        using (var cts = new CancellationTokenSource())
        {
            var started = new ManualResetEventSlim();
            var task = Task.Run(() => ForgeProcess.Run(new[] { "build", "x.fire" }, new ForgeSink { Progress = _ => started.Set() }, cts.Token, root, (term, Array.Empty<string>())));
            bool began = started.Wait(TimeSpan.FromSeconds(20));
            cts.Cancel();
            var r = task.Wait(TimeSpan.FromSeconds(30)) ? task.Result : null;
            Check("Abbrechen: SIGTERM an forge", OperatingSystem.IsWindows() || began && r is { Cancelled: true } && File.Exists(termMarker), $"{began} {r?.Cancelled} {File.Exists(termMarker)} {r?.Log}");
        }

        // Cancel, the hard way: a forge that ignores both is killed after the grace - with the tools it started (the process tree)
        string childPid = Path.Combine(root, "child.pid");
        string stubborn = Script(root, "forge-stubborn.sh", $$"""
            trap '' TERM
            sleep 1000 &
            echo $! > '{{childPid}}'
            echo '{"type":"progress","phase":"compile-c","text":"Compiling","done":0,"total":1}'
            while true; do sleep 0.1; done
            """);
        var grace = ForgeProcess.CancelGrace;
        ForgeProcess.CancelGrace = TimeSpan.FromSeconds(1);
        try
        {
            using var cts = new CancellationTokenSource();
            var started = new ManualResetEventSlim();
            var task = Task.Run(() => ForgeProcess.Run(new[] { "build", "x.fire" }, new ForgeSink { Progress = _ => started.Set() }, cts.Token, root, (stubborn, Array.Empty<string>())));
            bool began = started.Wait(TimeSpan.FromSeconds(20));
            cts.Cancel();
            var r = task.Wait(TimeSpan.FromSeconds(30)) ? task.Result : null;
            bool childGone = true;
            if (File.Exists(childPid) && int.TryParse(File.ReadAllText(childPid).Trim(), out int pid))
            {
                for (int i = 0; i < 40 && Alive(pid); i++) Thread.Sleep(50);
                childGone = !Alive(pid);
            }
            Check("Abbrechen: nach der Frist getoetet, mit dem Prozessbaum", began && r is { Cancelled: true, Ended: false } && File.Exists(childPid) && childGone, $"{began} {r?.Cancelled} {childGone}");
        }
        finally { ForgeProcess.CancelGrace = grace; }
    }

    private static bool Alive(int pid)
    {
        try { string stat = File.ReadAllText($"/proc/{pid}/stat"); return !stat.Contains(") Z ", StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }

    // ---- the real forge (beside the tests) ----------------------------------------------------------------------------------------------
    private static void RealForge(string root)
    {
        var forge = ForgeProcess.Locate(AppContext.BaseDirectory);
        if (forge == null) { Console.WriteLine("(no forge beside the tests: the checks with the real forge are skipped)"); return; }
        string dir = Path.Combine(root, "real");
        Directory.CreateDirectory(Path.Combine(dir, "Lib"));
        File.WriteAllText(Path.Combine(dir, "Lib", "Lib.fireproj"), """{ "name": "Lib", "type": "library" }""");
        string libSource = Path.Combine(dir, "Lib", "lib.fire");
        File.WriteAllText(libSource, "class Greeter\n{\n    static string Hello() { return \"hi\" }\n}\n");
        string proj = Path.Combine(dir, "Lib", "Lib.fireproj");

        var ok = ForgeProcess.Run(new[] { "build", proj, "--check" }, new ForgeSink(), CancellationToken.None, dir, forge);
        Check("Echtes forge: eine Bibliothek gepruft (--check)", ok is { Ok: true, Ended: true } && ok.Log.Contains("Lib: compiled"), ok.Error + "\n" + ok.Log);

        // the editor's text (an overlay) has an error that the file on the disk has not
        using var overlays = new ForgeOverlays();
        overlays.Add(libSource, "class Greeter\n{\n    static string Hello() { return missing }\n}\n");
        var bad = ForgeProcess.Run(new[] { "build", proj, "--check" }.Concat(overlays.Args).ToList(), new ForgeSink(), CancellationToken.None, dir, forge);
        var error = bad.Diagnostics.FirstOrDefault(d => d.IsError);
        Check("Echtes forge: der Fehler im Overlay mit Code und Zeile", bad is { Ok: false, Ended: true, ExitCode: 1 } && error is { Line: 3 } && error.Code.StartsWith("E", StringComparison.Ordinal),
            $"{bad.ExitCode} {error?.ToLogLine()}\n{bad.Log}");
        Check("Echtes forge: die Datei auf der Platte unveraendert", File.ReadAllText(libSource).Contains("return \"hi\""));
    }
}
