using fire.Runtime;

/// <summary>
/// The cases of tests/cases that ran in the C# VM inside this program (smoke and the families vm-*: CheckExt, CheckArr, CheckPerf, CheckClone, CheckLife,
/// CheckShutdown, CheckGl, CheckLq, CheckRf, CheckSc, ...) in the VM in C (docs/ROADMAP_C_PORT.md, M1): every case compiled here and run by `fire` (CVmTest),
/// by the rules of tests/run-cases.sh - the lines of `.expected`, an exception nobody caught as the last line `UNBEHANDELT: <message>` (without its stack trace),
/// `.error`: the case does not compile and the message has that text; the modes of `//# modes:` (else the mode the program asks for).
/// </summary>
static class TestCasesVm
{
    public static int Run(params string[] groups)
    {
        int failures = 0;
        foreach (string group in groups)
        {
            var cases = TestCases.Load(group);
            int groupFailures = 0;
            foreach (var c in cases)
            {
                if (c.Varies) continue;
                string title = c.Name;
                foreach (var m in c.Modes?.Select(x => (VmExecutionMode?)x).ToArray() ?? new VmExecutionMode?[] { null })
                {
                    string tag = m == null ? "" : $" [{m}]";
                    string? problem = One(c, m);
                    if (problem != null) { groupFailures++; Console.WriteLine($"FEHLER: {group}: {title}{tag} ({c.Id})\n{problem}"); }
                    else Console.WriteLine($"OK: {group}: {title}{tag}");
                }
            }
            Console.WriteLine(groupFailures == 0 ? $"Alle {cases.Count} Faelle von {group} in der C-VM bestanden." : $"FEHLER: {groupFailures} Fall/Faelle von {group} in der C-VM fehlgeschlagen.");
            failures += groupFailures;
        }
        return failures;
    }

    /// <summary>null: the case is right in this mode; else what is wrong.</summary>
    private static string? One(TestCases.Case c, VmExecutionMode? mode)
    {
        CVmTest.Result run;
        try
        {
            run = CVmTest.Run(new[] { c.Source }, mode, sourcePaths: new string?[] { c.File }, workingDirectory: Path.GetDirectoryName(c.File), timeoutMs: 120000);
        }
        catch (Exception ex) when (fire.Compiler.CompileDiagnostics.Of(ex).Count > 0)
        {
            string errors = string.Join("\n", fire.Compiler.CompileDiagnostics.Of(ex).Select(d => d.ToLogLine()));
            if (c.Error != null) return errors.Contains(c.Error) ? null : $"  erwartet im Fehler: {c.Error}\n  erhalten: {errors}";
            return "  WIRD NICHT UEBERSETZT: " + errors;
        }
        if (c.Error != null) return $"  erwartet: ein Fehler beim Uebersetzen ({c.Error}), das Programm lief: {run.Text}";
        if (run.TimedOut) return "  das Programm endete nicht (120 s)";
        string actual = run.Text + (run.ExitCode != 0 && run.UnhandledMessage is { } m ? "UNBEHANDELT: " + m + "\n" : "");
        if (run.ExitCode != 0 && run.UnhandledMessage == null && run.Errors.Trim().Length > 0) actual += "UNBEHANDELT: " + run.Errors.Trim() + "\n";
        return actual == (c.Expected ?? "") ? null : TestCases.Difference(c.Expected ?? "", actual);
    }
}
