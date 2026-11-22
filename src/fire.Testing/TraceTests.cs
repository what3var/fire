using fire.Compiler;
using fire.Runtime;
using fire.Values;

/// <summary>
/// Stack traces of exceptions (docs/spec/STACKTRACE.md): the cases of tests/cases/trace in the VM in C# (as `forge run <case>.fire` runs them: the file names the
/// frames, the report of an unhandled exception is `UNBEHANDELT: ` and the whole text of stderr), the format (ExceptionTrace: limits, sections, `calls`), the
/// names of the sources in a trace, and the clickable lines of spark's output (OutputLocations). The VM in C runs the same files: tests/run-cases-vm.sh trace.
/// </summary>
static class TraceTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string detail = "")
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: {title}" : $"FEHLER: {title}\n{detail}");
    }

    public static int Run()
    {
        _failures = 0;
        Console.WriteLine();
        Console.WriteLine("=== Stack-Traces (tests/cases/trace) ===");
        foreach (var c in TestCases.Load("trace"))
            foreach (var mode in c.Modes ?? new[] { VmExecutionMode.Release })
            {
                string actual = RunCase(c, mode);
                Check($"{c.Name} [{mode}]", actual == (c.Expected ?? ""), TestCases.Difference(c.Expected ?? "", actual));
            }

        // the format: more than 64 frames, the sections, without lines
        var deep = ExceptionTrace.Of(100, i => new ExceptionTrace.Frame("F" + i, i == 99 ? null : "a.fire", i + 1), "in thread 'Fire #1', started at a.fire:3");
        string text = deep.Format(StackTraceMode.Lines);
        var lines = text.TrimEnd('\n').Split('\n');
        Check("Format: 48 innerste, '... 36 more frames ...', 16 aeusserste, die Zeile des Threads", lines.Length == 48 + 1 + 16 + 1
            && lines[0] == "   at F0 (a.fire:1)" && lines[47] == "   at F47 (a.fire:48)" && lines[48] == "   ... 36 more frames ..." && lines[49] == "   at F84 (a.fire:85)"
            && lines[64] == "   at F99 (prelude)" && lines[65] == "   in thread 'Fire #1', started at a.fire:3", text);
        var awaited = new ExceptionTrace(new[] { new ExceptionTrace.Frame("A", "a.fire", 2) }).Append(new ExceptionTrace(new[] { new ExceptionTrace.Frame("<main>", "a.fire", 9) }));
        Check("Format: der Abschnitt des Wartenden", awaited.Format(StackTraceMode.Lines) == "   at A (a.fire:2)\n   --- awaited at ---\n   at <main> (a.fire:9)\n", awaited.Format(StackTraceMode.Lines));
        Check("Format: calls ohne Zeilen, off leer", awaited.Format(StackTraceMode.Calls) == "   at A (a.fire)\n   --- awaited at ---\n   at <main> (a.fire)\n" && awaited.Format(StackTraceMode.Off) == "");
        var many = new ExceptionTrace(new[] { new ExceptionTrace.Frame("A", "a.fire", 1) });
        for (int i = 0; i < 6; i++) many = many.Append(new ExceptionTrace(new[] { new ExceptionTrace.Frame("W" + i, "a.fire", 1) }));
        int sections = 0; for (var t = many; t != null; t = t.Next) sections++;
        Check("Format: hoechstens 4 Abschnitte", sections == ExceptionTrace.MaxSections);

        // the names of the sources: relative to the folder of the first, the prelude none, a source without file fire:source/N, a library its file name
        string root = OperatingSystem.IsWindows() ? @"C:\p\" : "/p/";
        var names = ExceptionTrace.DisplayFiles(new string?[] { null, root + "lib" + Path.DirectorySeparatorChar + "pkg.fire", null, root + "app" + Path.DirectorySeparatorChar + "main.fire",
            root + "app" + Path.DirectorySeparatorChar + "sub" + Path.DirectorySeparatorChar + "u.fire", root + "other.fire", null, null }, 3, 4);
        Check("Dateinamen im Trace: relativ, Prelude ohne, Bibliothek mit ihrem Namen, eine Quelle ohne Datei", names[0] == null && names[1] == "pkg.fire" && names[2] == null
            && names[3] == "main.fire" && names[4] == "sub/u.fire" && names[5] == "../other.fire" && names[6] == "fire:source/3" && names[7] == null, string.Join(" | ", names));

        var withModules = ExceptionTrace.DisplayFiles(new string?[] { null, root + "lib" + Path.DirectorySeparatorChar + "pkg.fire", root + "app" + Path.DirectorySeparatorChar + "main.fire" }, 2, 1,
            new string?[] { null, "Lib", null });
        Check("Dateinamen im Trace: eine Bibliothek mit dem Namen ihres Moduls (Lib:pkg.fire)", withModules[1] == "Lib:pkg.fire" && withModules[2] == "main.fire", string.Join(" | ", withModules));

        // spark's output: a line of a trace is a place to jump to (relative to the folder of the program), a frame of the prelude is not
        Check("Output-Panel: '   at Store.Find (main.fire:16)' ist klickbar", fire.Compiler.OutputLocations.TraceFrame("   at Store.Find (main.fire:16)") is ("main.fire", 16));
        Check("Output-Panel: ein Unterordner, ein Frame des Preludes nicht, ohne Zeile nicht", fire.Compiler.OutputLocations.TraceFrame("   at <lambda> (lib/util.fire:3)") is ("lib/util.fire", 3)
            && fire.Compiler.OutputLocations.TraceFrame("   at Dictionary.Add (prelude)") == null && fire.Compiler.OutputLocations.TraceFrame("   at S.F (main.fire)") == null);
        Check("forge test: die Frames des Treibers und von Testing.Assert fallen weg", fire.Compiler.OutputLocations.WithoutTestDriver(
            "   at Assert.Equal (prelude)\n   at MathTests.Adds (tests.fire:7)\n   at __Tests.Run0Body (prelude)\n   at <main> (prelude)\n") == "   at MathTests.Adds (tests.fire:7)\n",
            fire.Compiler.OutputLocations.WithoutTestDriver("   at Assert.Equal (prelude)\n   at MathTests.Adds (tests.fire:7)\n   at __Tests.Run0Body (prelude)\n   at <main> (prelude)\n"));

        Console.WriteLine(_failures == 0 ? "Alle Stack-Trace-Pruefungen bestanden." : $"FEHLER: {_failures} Stack-Trace-Pruefung(en) fehlgeschlagen.");
        return _failures;
    }

    /// <summary>What `forge run <case>.fire -m <mode>` prints, with the report of an unhandled exception behind `UNBEHANDELT: ` (tests/run-cases.sh, group trace).</summary>
    private static string RunCase(TestCases.Case c, VmExecutionMode mode)
    {
        var lines = new List<string>();
        string? unhandled = null;
        try
        {
            VM.ResetTerminateForTests();
            var session = RuntimeSession.Build(new[] { c.Source }, mode, args => { lock (lines) lines.Add(args.Length > 0 ? args[0].ToString()! : ""); return Value.MakeUndefined(); },
                basePath: Path.GetDirectoryName(c.File), sourcePaths: new string?[] { c.File });
            session.Run();
            if (session.VirtualMachine?.UnhandledException is { } e) unhandled = session.VirtualMachine.UnhandledReport(e);
        }
        catch (Exception ex) when (CompileDiagnostics.Of(ex).Count > 0) { unhandled = "WIRD NICHT UEBERSETZT: " + string.Join(" | ", CompileDiagnostics.Of(ex).Select(d => d.Code + " " + d.Message)); }
        finally { VM.ResetTerminateForTests(); Value.SingleFloats = false; Value.Ints32 = false; Value.Ints16 = false; }
        return string.Concat(lines.Select(l => l + "\n")) + (unhandled == null ? "" : "UNBEHANDELT: " + unhandled + "\n");
    }
}
