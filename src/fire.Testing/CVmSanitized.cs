using System.Diagnostics;
using fire.Compiler;

/// <summary>
/// The VM in C (native/vm/fire.c, the sources forge carries: CVm.WriteSources) built with AddressSanitizer - the check that a process which runs several programs
/// one after the other (`fire a.fbc b.fbc`, the driver agent, an embedding) frees what each of them had: LeakSanitizer reports at the end of the process what
/// nothing reaches any more (docs/spec/VM_C.md "Das Programm fire"). Linux with gcc or clang only; elsewhere <see cref="Locate"/> says why not.
/// </summary>
static class CVmSanitized
{
    private static string? _exe, _error;

    /// <summary>The VM with ASan (built once per test run into <paramref name="folder"/>), or null with the reason.</summary>
    public static string? Locate(string folder, out string error)
    {
        error = _error ?? "";
        if (_exe != null || _error != null) return _exe;
        if (!OperatingSystem.IsLinux()) { error = _error = "AddressSanitizer: only on Linux"; return null; }
        string dir = Path.Combine(folder, "cvm-asan");
        string exe = Path.Combine(dir, "fire-asan");
        try
        {
            string main = CVm.WriteSources(Path.Combine(dir, "src"));
            // (with libstdc++ loaded from the start: the natives in C++ of a package or plugin throw C++ exceptions, and ASan's __cxa_throw needs the real one)
            foreach (var (cc, cxx) in new[] { ("gcc", true), ("clang", true), ("cc", true), ("gcc", false), ("cc", false) })
            {
                var psi = new ProcessStartInfo(cc) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in new[] { "-g", "-O1", "-fsanitize=address", "-fno-omit-frame-pointer", "-w", main, "-o", exe, "-lm", "-lpthread", "-ldl" }) psi.ArgumentList.Add(a);
                if (cxx) { psi.ArgumentList.Add("-Wl,--no-as-needed"); psi.ArgumentList.Add("-lstdc++"); }
                Process? p;
                try { p = Process.Start(psi); }
                catch (System.ComponentModel.Win32Exception) { continue; }
                if (p == null) continue;
                using (p)
                {
                    var err = p.StandardError.ReadToEndAsync();
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode == 0 && File.Exists(exe)) return _exe = exe;
                    error = cc + ": " + err.Result;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { error = ex.Message; }
        _error = "AddressSanitizer: the VM could not be built - " + (error.Length > 0 ? error : "no C compiler");
        error = _error;
        return null;
    }

    /// <summary>Runs `fire-asan` with the arguments (with LeakSanitizer); stdout, stderr and the exit code. The environment of this process plus <paramref name="env"/>.</summary>
    public static (string Out, string Err, int Code) Run(string exe, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null, int timeoutMs = 300000)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        psi.Environment["ASAN_OPTIONS"] = "detect_leaks=1:abort_on_error=0";
        psi.Environment["FIRE_SYSTEM_THEME"] = "off";
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (InvalidOperationException) { } return (output, "(timeout) " + err.Result, -1); }
        return (output.Replace("\r\n", "\n"), err.Result, p.ExitCode);
    }

    /// <summary>What ASan/LSan reported (the first lines of each report), "" when nothing.</summary>
    public static string Reports(string stderr)
    {
        var lines = stderr.Split('\n');
        var found = new List<string>();
        for (int i = 0; i < lines.Length && found.Count < 40; i++)
            if (lines[i].Contains("Sanitizer", StringComparison.Ordinal) || lines[i].StartsWith("Direct leak", StringComparison.Ordinal) || lines[i].StartsWith("Indirect leak", StringComparison.Ordinal))
                for (int k = i; k < Math.Min(lines.Length, i + 8); k++) found.Add(lines[k]);
        return string.Join("\n", found);
    }
}
