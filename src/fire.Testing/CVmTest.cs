using System.Diagnostics;
using System.Text;
using fire.Compiler;
using fire.Runtime;

/// <summary>
/// A fire program of a check run in the VM in C (`fire`, docs/ROADMAP_C_PORT.md M1): what the checks did with `RuntimeSession.Build(...).Run()` in the C# VM - the
/// sources linked here (the Linker with the libraries of their packages, compile errors are thrown as before), written as a program file and run as
/// `fire run x.fbc` with FIRE_PRINT_RECORDS (one entry per `print`, also one of several lines). The output of the program is what it printed; an exception
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
        public string? Unhandled => CVmRun.UnhandledLine(Errors);
        /// <summary>The report without the stack trace (the lines of `fire` on stderr that are not frames of a trace) - what the C# VM's UnhandledReport said
        /// for a program without line tables; null without an exception.</summary>
        public string? UnhandledMessage => Unhandled == null ? null
            : string.Join("\n", Errors.TrimEnd('\n').Split('\n').Where(l => !l.StartsWith("   at ") && !l.StartsWith("   ... ") && l != "   --- awaited at ---" && !l.StartsWith("   in thread '")));
        /// <summary>The printed lines, each ended by a line feed (as a case's `.expected`).</summary>
        public string Text => string.Concat(Printed.Select(p => p + "\n"));
    }

    /// <summary>Compiles and runs <paramref name="sources"/> in `fire`. <paramref name="input"/>: its standard input (else none); <paramref name="environment"/>: more variables;
    /// <paramref name="display"/>: its windows go to a display file of the host that nobody shows (FIRE_DISPLAY_SHM).</summary>
    public static Result Run(IReadOnlyList<string> sources, VmExecutionMode? mode = null, fire.Projects.BuildPlan? plan = null, string? basePath = null,
        IReadOnlyList<string?>? sourcePaths = null, IReadOnlyList<string>? defines = null, int? floatWidth = null, IReadOnlyDictionary<string, string>? environment = null,
        string? input = null, IEnumerable<string>? arguments = null, int timeoutMs = 300000, string? workingDirectory = null, bool display = false)
    {
        var linked = new Linker { BasePath = basePath, Defines = defines, Plan = plan, SourcePaths = plan?.SourcePaths.Cast<string?>().ToList() ?? sourcePaths, PackageLibraries = true }
            .CompileAndLink(sources, null, null, mode, floatWidth);
        if (!display) return Run(linked, environment, input, arguments, timeoutMs, workingDirectory ?? basePath);
        // (a window that shows nothing: the host's display file, nobody looks at it - FIRE_DISPLAY_SHM, HostDisplayFile)
        using var file = HostDisplayFile.Create();
        var env = environment?.ToDictionary(e => e.Key, e => e.Value) ?? new Dictionary<string, string>();
        env[HostDisplayFile.EnvironmentVariable] = file.Path;
        return Run(linked, env, input, arguments, timeoutMs, workingDirectory ?? basePath);
    }

    /// <summary>Runs a linked program in `fire`.</summary>
    public static Result Run(LinkedProgram linked, IReadOnlyDictionary<string, string>? environment = null, string? input = null, IEnumerable<string>? arguments = null,
        int timeoutMs = 300000, string? workingDirectory = null)
    {
        var (folder, file) = CVmRun.Write(linked, "fire-check-");
        var result = new Result();
        try
        {
            var extra = new Dictionary<string, string> { ["FIRE_PRINT_RECORDS"] = "1" };
            if (environment != null) foreach (var (k, v) in environment) extra[k] = v;
            var info = CVmRun.StartInfo(linked, file, arguments ?? Array.Empty<string>(), redirect: true, extra,
                workingDirectory != null && Directory.Exists(workingDirectory) ? workingDirectory : Directory.GetCurrentDirectory());
            using var process = new Process { StartInfo = info };
            var errors = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (result.Printed) result.Printed.Add(e.Data.Replace('\x1F', '\n')); };   // (a line break of a print came as \x1F)
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.Append(e.Data).Append('\n'); };
            process.Start();
            process.BeginOutputReadLine();
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
            result.ExitCode = process.ExitCode;
            lock (errors) result.Errors = errors.ToString();
            return result;
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The printed lines of a run in `fire` joined by line feeds (what the checks' `Vm(source)` helpers returned); <paramref name="ended"/>: with a line feed after the last.</summary>
    public static string Output(string source, VmExecutionMode? mode = VmExecutionMode.Release, bool ended = false)
    {
        var r = Run(new[] { source }, mode);
        return string.Join("\n", r.Printed) + (ended && r.Printed.Count > 0 ? "\n" : "");
    }

    /// <summary>A program under the debugger in `fire` (what spark does for Debug: `fire run x.fbc` with FIRE_DEBUG, a NativeDebugTarget with engine "vm"), connected and
    /// paused before its first line. The program file goes into <paramref name="folder"/>.</summary>
    public static fire.Debugging.NativeDebugTarget Debug(IReadOnlyList<string> sources, VmExecutionMode mode, string folder, string? workingDirectory = null,
        IReadOnlyList<string?>? sourcePaths = null)
    {
        var linked = new Linker { SourcePaths = sourcePaths, PackageLibraries = true }.CompileAndLink(sources, null, null, mode, null);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "debug-" + Guid.NewGuid().ToString("N") + ".fbc");
        File.WriteAllBytes(file, fire.Bytecode.ProgramFile.Write(linked));
        string vm = CVm.Locate(linked.IntWidth, linked.FloatWidth);
        return fire.Debugging.CVmLaunch.LaunchDebug(vm, file, Array.Empty<string>(), workingDirectory ?? folder, TimeSpan.FromSeconds(30),
            fire.Debugging.CVmLaunch.Environment(linked.PackageLibraryFiles ?? new List<string>()));
    }
}

/// <summary>
/// The checks of the C# VM itself (fire.Runtime: its VM, its natives, its threads, the bridges to fire.Terminal, VmDebugTarget, the .NET packer) - they go away with
/// fire.Runtime (docs/ROADMAP_C_PORT.md, M1). The default run does not run them (it does not need the C# VM); FIRE_TESTS_DOTNET_VM=1 runs them as before,
/// as long as the project is there.
/// </summary>
static class DotnetVm
{
    public const string Variable = "FIRE_TESTS_DOTNET_VM";

    /// <summary>FIRE_TESTS_DOTNET_VM=1: the checks of the C# VM run too.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable(Variable) == "1";

    /// <summary>true when the checks of the C# VM named <paramref name="what"/> are left out (the default): says so in one line.</summary>
    public static bool Skip(string what)
    {
        if (Enabled) return false;
        Console.WriteLine($"(C#-VM: {what} - goes away with fire.Runtime, not in the default run; {Variable}=1 runs it)");
        return true;
    }
}
