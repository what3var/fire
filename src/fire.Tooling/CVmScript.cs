using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace fire.Tooling
{
    /// <summary>A script of spark did not compile: the message names its errors, one per line.</summary>
    public sealed class ScriptCompileException : Exception
    {
        public IReadOnlyList<Diagnostic> Diagnostics { get; }
        public ScriptCompileException(string message, IReadOnlyList<Diagnostic> diagnostics) : base(message) { Diagnostics = diagnostics; }
    }

    /// <summary>What a script printed and how it ended in `fire` (<see cref="CVmScript.Run"/>).</summary>
    public sealed class CVmScriptResult
    {
        /// <summary>One entry per `print` (FIRE_PRINT_RECORDS: also a print of several lines is one entry).</summary>
        public List<string> Printed { get; } = new();
        public string Errors { get; set; } = "";
        public int ExitCode { get; set; }
        /// <summary>It took longer than allowed or was cancelled (and was ended).</summary>
        public bool Killed { get; set; }
        /// <summary>Milliseconds: compiling (forge), then the process from its start to its end.</summary>
        public double CompileMs { get; set; }
        /// <summary>Of <see cref="CompileMs"/>: compiling and linking (the rest writes the program file).</summary>
        public double LinkMs { get; set; }
        public double RunMs { get; set; }
        /// <summary>Why it did not end well (null: exit code 0): stderr of `fire` (an unhandled exception), or the exit code.</summary>
        public string? Failure => Killed ? "The script took too long and was ended." : ExitCode == 0 ? null : Errors.Trim().Length > 0 ? Errors.Trim() : $"The script ended with exit code {ExitCode}.";
    }

    /// <summary>
    /// A script of spark that runs to its end in `fire` (the VM in C) - the design view of markup (<see cref="UiPreview"/>) and the preview of block models (docs/spec/VM_C.md,
    /// "spark auf der C-VM"): compiled by forge into a program file in a folder of its own (<see cref="Forge.Compile"/>: with the libraries of its packages, like
    /// <see cref="LiveSession"/>), run as `fire run x.fbc` with FIRE_PRINT_RECORDS on stdout; a window of the script goes to the host's display file when one is given
    /// (FIRE_DISPLAY_SHM, <see cref="HostDisplayFile"/>).
    /// </summary>
    public static class CVmScript
    {
        /// <summary>spark's previews run in `fire`: there is one (or one could be built).</summary>
        public static bool Available => Locate() != null;

        /// <summary>Why there is no `fire` (the message of <see cref="Forge.LocateVm"/>), when <see cref="Available"/> is false.</summary>
        public static string? Unavailable { get; private set; }

        /// <summary>The last run (its times: the tests report the latency of the previews).</summary>
        public static CVmScriptResult? Last { get; private set; }

        private static string? _fire;
        private static readonly object FireLock = new();

        /// <summary>The `fire` of 64-bit widths (null: none can be had - no C compiler).</summary>
        public static string? Locate()
        {
            lock (FireLock)
            {
                if (_fire != null && File.Exists(_fire)) return _fire;
                try { _fire = Forge.LocateVm(64, 64); Unavailable = null; }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { _fire = null; Unavailable = ex.Message; }
                return _fire;
            }
        }

        /// <summary>A folder for one run (in /dev/shm when there is one: the program file is read once and goes away).</summary>
        public static string NewFolder(string prefix)
        {
            string root = OperatingSystem.IsLinux() && Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath();
            string folder = Path.Combine(root, $"{prefix}-{Environment.ProcessId}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>Compiles `script` with forge (`basePath`: its `#include`s and its working folder); a <see cref="ScriptCompileException"/> when it does not compile.</summary>
        public static CompiledScript Compile(string script, string? basePath, string folder, CancellationToken cancel = default)
        {
            var compiled = Forge.Compile(script, basePath, folder, cancel: cancel);
            if (!compiled.Ok || compiled.Program == null) throw new ScriptCompileException(compiled.Errors, compiled.Diagnostics);
            return compiled;
        }

        /// <summary>Compiles `script` (`basePath`: its `#include`s and its working folder) and runs it in `fire` to its end. Compile errors are thrown
        /// (<see cref="ScriptCompileException"/>). `display`: the file of a host display for its windows; `timeoutMs`/`cancel`: the process is ended then.</summary>
        public static CVmScriptResult Run(string script, string? basePath, HostDisplayFile? display = null, int timeoutMs = 30000, CancellationToken cancel = default)
        {
            string vm = Locate() ?? throw new InvalidOperationException("There is no `fire` (the VM in C) to run the script in.");
            var result = new CVmScriptResult();
            string folder = NewFolder("fire-script");
            try
            {
                var compiled = Compile(script, basePath, folder, cancel);
                result.CompileMs = result.LinkMs = compiled.Milliseconds;
                var clock = Stopwatch.StartNew();
                string programFile = compiled.Program!;
                var extra = new Dictionary<string, string> { ["FIRE_PRINT_RECORDS"] = "1" };
                if (display != null) extra[HostDisplayFile.EnvironmentVariable] = display.Path;
                var info = new ProcessStartInfo(vm)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
                    WorkingDirectory = basePath != null && Directory.Exists(basePath) ? basePath : folder,
                };
                foreach (var a in fire.Debugging.CVmLaunch.Arguments(programFile, Array.Empty<string>())) info.ArgumentList.Add(a);
                foreach (var (k, v) in fire.Debugging.CVmLaunch.Environment(compiled.Libraries, extra)) info.Environment[k] = v;
                fire.Debugging.ChildProcesses.Prepare(info);
                var errors = new System.Text.StringBuilder();
                using var process = new Process { StartInfo = info };
                process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (result.Printed) result.Printed.Add(e.Data.Replace('\x1F', '\n')); };   // (a line break of a print came as \x1F)
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
                process.Start();
                fire.Debugging.ChildProcesses.Add(process);
                process.StandardInput.Close();   // (a script of spark reads nothing)
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                using (cancel.Register(() => Kill(process)))
                {
                    if (!process.WaitForExit(timeoutMs)) { Kill(process); result.Killed = true; }
                    process.WaitForExit();   // (and its output to the end)
                }
                if (cancel.IsCancellationRequested) result.Killed = true;
                result.RunMs = clock.Elapsed.TotalMilliseconds;
                result.ExitCode = process.ExitCode;
                Last = result;
                lock (errors) result.Errors = errors.ToString();
                return result;
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        private static void Kill(Process process)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
    }
}
