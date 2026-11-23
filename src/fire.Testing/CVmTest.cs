using System.Diagnostics;
using System.Text;
using fire.Tooling;
/// <summary>
/// A fire program of a check run in the VM in C (`fire`, docs/ROADMAP_C_PORT.md M1, M3): the
/// sources compiled by forge (`forge bytecode ... --report json`: the program file and the libraries of their packages; compile errors are thrown as a
/// <see cref="ScriptCompileException"/>), run as `fire run x.fbc` with FIRE_PRINT_RECORDS (one entry per `print`, also one of several lines). The output of the program is what it printed; an exception
/// nobody caught is its report on stderr (the line `Unhandled exception of type ...` and the stack trace) and exit code 1.
/// </summary>
static class CVmTest
{
    public sealed class Result
    {
        /// <summary>One entry per `print`.</summary>
        public List<string> Printed { get; } = new();
        /// <summary>What `fire` wrote to stderr (an exception nobody caught: its report).</summary>
        public string Errors { get; set; } = "";
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        /// <summary>The program ended with an exception nobody caught: the first line of its report ("Unhandled exception of type 'X': message"), else null.</summary>
        public string? Unhandled => fire.Debugging.CVmLaunch.UnhandledLine(Errors);
        /// <summary>The report without the stack trace (the lines of `fire` on stderr that are not frames of a trace) - what the C# VM's UnhandledReport said
        /// for a program without line tables; null without an exception.</summary>
        public string? UnhandledMessage => Unhandled == null ? null
            : string.Join("\n", Errors.TrimEnd('\n').Split('\n').Where(l => !l.StartsWith("   at ") && !l.StartsWith("   ... ") && l != "   --- awaited at ---" && !l.StartsWith("   in thread '")));
        /// <summary>The printed lines, each ended by a line feed (as a case's `.expected`).</summary>
        public string Text => string.Concat(Printed.Select(p => p + "\n"));
    }

    /// <summary>Compiles and runs <paramref name="sources"/> (texts) in `fire` - or the project <paramref name="project"/> when one is named (the sources are then not used).
    /// <paramref name="input"/>: its standard input (else none); <paramref name="environment"/>: more variables; <paramref name="display"/>: its windows go to a display file
    /// of the host that nobody shows (FIRE_DISPLAY_SHM).</summary>
    public static Result Run(IReadOnlyList<string> sources, VmExecutionMode? mode = null, string? project = null, string? basePath = null,
        IReadOnlyDictionary<string, string>? environment = null, string? input = null, IEnumerable<string>? arguments = null, int timeoutMs = 300000,
        string? workingDirectory = null, bool display = false)
    {
        string folder = Path.Combine(Path.GetTempPath(), "fire-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var compiled = Compile(sources, mode, project, basePath, folder);
            if (!display) return Run(compiled, environment, input, arguments, timeoutMs, workingDirectory ?? basePath);
            // (a window that shows nothing: the host's display file, nobody looks at it - FIRE_DISPLAY_SHM, HostDisplayFile)
            using var file = HostDisplayFile.Create();
            var env = environment?.ToDictionary(e => e.Key, e => e.Value) ?? new Dictionary<string, string>();
            env[HostDisplayFile.EnvironmentVariable] = file.Path;
            return Run(compiled, env, input, arguments, timeoutMs, workingDirectory ?? basePath);
        }
        finally { try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>Compiles the sources (each written as a file of <paramref name="basePath"/> or of the folder) or the project with forge; a <see cref="ScriptCompileException"/>
    /// when they do not compile.</summary>
    public static CompiledScript Compile(IReadOnlyList<string> sources, VmExecutionMode? mode, string? project, string? basePath, string folder)
    {
        var inputs = new List<string>();
        var written = new List<string>();
        try
        {
            if (project != null) inputs.Add(project);
            else
            {
                string dir = basePath != null && Directory.Exists(basePath) ? basePath : folder;
                for (int i = 0; i < sources.Count; i++)
                {
                    string file = Path.Combine(dir, $".fire-check-{Environment.ProcessId}-{Guid.NewGuid():N}-{i}.fire");
                    File.WriteAllText(file, sources[i]);
                    written.Add(file);
                    inputs.Add(file);
                }
            }
            var compiled = Forge.CompileFiles(inputs, Path.Combine(folder, "check.fbc"), mode?.ToString().ToUpperInvariant());
            if (!compiled.Ok) throw new ScriptCompileException(compiled.Errors, compiled.Diagnostics);
            return compiled;
        }
        finally { foreach (var f in written) try { File.Delete(f); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    /// <summary>Runs a program forge compiled in `fire` (the VM forge named, else the one next to it).</summary>
    public static Result Run(CompiledScript compiled, IReadOnlyDictionary<string, string>? environment = null, string? input = null, IEnumerable<string>? arguments = null,
        int timeoutMs = 300000, string? workingDirectory = null)
    {
        var result = new Result();
        var extra = new Dictionary<string, string> { ["FIRE_PRINT_RECORDS"] = "1" };
        if (environment != null) foreach (var (k, v) in environment) extra[k] = v;
        var info = new ProcessStartInfo(compiled.Vm ?? Forge.LocateVm())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDirectory != null && Directory.Exists(workingDirectory) ? workingDirectory : Directory.GetCurrentDirectory(),
        };
        foreach (var a in fire.Debugging.CVmLaunch.Arguments(compiled.Program!, arguments ?? Array.Empty<string>())) info.ArgumentList.Add(a);
        foreach (var (k, v) in fire.Debugging.CVmLaunch.Environment(compiled.Libraries, extra)) info.Environment[k] = v;
        using var process = new Process { StartInfo = info };
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.Append(e.Data).Append('\n'); };
        process.Start();
        // (the records split at '\n' only: a '\r' in a print stays in it; a line break of a print came as \x1F)
        var reader = System.Threading.Tasks.Task.Run(() =>
        {
            var line = new StringBuilder();
            int ch;
            while ((ch = process.StandardOutput.Read()) >= 0)
            {
                if (ch == '\n') { lock (result.Printed) result.Printed.Add(line.ToString().Replace('\x1F', '\n')); line.Clear(); }
                else line.Append((char)ch);
            }
            if (line.Length > 0) lock (result.Printed) result.Printed.Add(line.ToString().Replace('\x1F', '\n'));
        });
        process.BeginErrorReadLine();
        try
        {
            if (input != null) process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        catch (IOException) { }   // (it ended before it read)
        if (!process.WaitForExit(timeoutMs))
        {
            result.TimedOut = true;
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
        }
        process.WaitForExit();
        reader.Wait();
        result.ExitCode = process.ExitCode;
        lock (errors) result.Errors = errors.ToString();
        return result;
    }

    /// <summary>The printed lines of a run in `fire` joined by line feeds (what the checks' `Vm(source)` helpers returned); <paramref name="ended"/>: with a line feed after the last.</summary>
    public static string Output(string source, VmExecutionMode? mode = VmExecutionMode.Release, bool ended = false)
    {
        var r = Run(new[] { source }, mode);
        return string.Join("\n", r.Printed) + (ended && r.Printed.Count > 0 ? "\n" : "");
    }

    /// <summary>A program under the debugger in `fire` (what spark does for Debug: `fire run x.fbc` with FIRE_DEBUG, a NativeDebugTarget with engine "vm"), connected and
    /// paused before its first line. The program file goes into <paramref name="folder"/>; the sources are written there as files (<paramref name="names"/>: their names).</summary>
    public static fire.Debugging.NativeDebugTarget Debug(IReadOnlyList<string> sources, VmExecutionMode mode, string folder, string? workingDirectory = null,
        IReadOnlyList<string>? names = null)
    {
        Directory.CreateDirectory(folder);
        var inputs = new List<string>();
        for (int i = 0; i < sources.Count; i++)
        {
            string file = Path.Combine(folder, names != null && i < names.Count ? names[i] : $"debug{i}.fire");
            File.WriteAllText(file, sources[i]);
            inputs.Add(file);
        }
        var compiled = Forge.CompileFiles(inputs, Path.Combine(folder, "debug-" + Guid.NewGuid().ToString("N") + ".fbc"), mode.ToString().ToUpperInvariant());
        if (!compiled.Ok) throw new ScriptCompileException(compiled.Errors, compiled.Diagnostics);
        string vm = compiled.Vm ?? Forge.LocateVm();
        return fire.Debugging.CVmLaunch.LaunchDebug(vm, compiled.Program!, Array.Empty<string>(), workingDirectory ?? folder, TimeSpan.FromSeconds(30),
            fire.Debugging.CVmLaunch.Environment(compiled.Libraries));
    }
}
