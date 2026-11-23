using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using fire.Compiler;
using fire.Debugging;
using fire.Projects;

namespace fire.Editor
{
    // spark on the VM in C (docs/ROADMAP_C_PORT.md, phase B step 9 - L4): a run of the engine "vm" is a child process. forge compiles the program to a program file
    // (`forge bytecode ... --report json`, with the text of the editor as overlays; its report names the libraries of the packages and the `fire` of the program's widths),
    // then spark starts `fire run x.fbc` - Release/Performance: its output in the Output panel, a line below it is its input, the exit code in the status bar, Stop kills it;
    // Debug: with FIRE_DEBUG (TCP; its input still through the pipe), debugged by NativeDebugTarget as a native program is (the same panels, without the operand stack; its hello says engine
    // "vm"). (M1: the C# VM in spark's process - FIRE_SPARK_VM=dotnet, DebugSession.CompileAsync, VmDebugTarget - is gone; every run is a child process.)
    public partial class MainWindow
    {
        /// <summary>The plain run in _nativeRun is a program of the VM (not a native build).</summary>
        private bool _nativeRunIsVm;

        /// <summary>Where the program file of a run of the VM goes: a project into bin/vm-&lt;mode&gt;/&lt;name&gt;.fbc, a saved script into bin/vm-&lt;mode&gt; next to it, an
        /// unsaved one into the temporary folder.</summary>
        private string CVmRunOutput(RunInput input, string mode)
        {
            if (input.Project is { } project) return ProjectOutput.ProgramPath(project.Directory, project.Name, ProjectOutput.VmTarget, mode, ".fbc");
            string? path = FullPathOf(input.Document);
            string directory = path != null ? Path.GetDirectoryName(path)! : Path.Combine(Path.GetTempPath(), "spark-vm");
            string name = path != null ? Path.GetFileNameWithoutExtension(path) : "program";
            return ProjectOutput.ProgramPath(directory, name, ProjectOutput.VmTarget, mode, ".fbc");
        }

        /// <summary>The tool `check` of the MCP server: what Start would run is compiled - by forge, as for a run (`forge bytecode`, or `forge build` for the native engine)
        /// - into the build log and the error list, without running it. false: it did not compile (or there was nothing to compile).</summary>
        private async Task<bool> CheckAsync()
        {
            if (BuildRunning()) return false;
            StopNativeRun();
            var mode = _session.ExecutionMode;
            string modeName = mode.ToString().ToLowerInvariant();
            if (ResolveRunInput(null) is not { } input) return false;
            string? defaultFile = input.Project != null ? null : input.Document?.DisplayName;
            string? defaultPath = input.Project != null ? null : FullPathOf(input.Document);
            string output = Path.Combine(Path.GetTempPath(), "spark-check-" + Guid.NewGuid().ToString("N") + ".fbc");
            BeginRun(input);
            var job = BeginBuildJob($"{input.What} (check, {modeName})");
            try
            {
                var overlays = OverlaysOfOpenDocuments();
                var args = new System.Collections.Generic.List<string> { "bytecode" };
                args.AddRange(input.Project != null ? ForgeProjectArgs(input.Project) : new System.Collections.Generic.List<string> { ForgeScriptSource(input.Document!, overlays) });
                args.AddRange(new[] { "-m", ForgeMode(mode), "-o", output });
                var result = await RunForgeAsync(job, args, overlays);
                job.Feed.Drain();
                if (result.Cancelled) { EndBuildJob(job, BuildOutcome.Cancelled); return false; }
                FinishBuild("Compile", result.Ok, ForgeDiagnostics(result, overlays), defaultFile, defaultPath);
                if (!result.Ok)
                {
                    string error = BuildErrorText(result);
                    EndBuildJob(job, BuildOutcome.Failed, error);
                    UpdateStatus("Compile error: " + error.Split('\n')[0]);
                    return false;
                }
                EndBuildJob(job, BuildOutcome.Succeeded);
                UpdateStatus($"Compiled {input.What} - no errors.");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                EndBuildJob(job, BuildOutcome.Failed, ex.Message);
                UpdateStatus("The check failed: " + ex.Message);
                return false;
            }
            finally { try { File.Delete(output); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        }

        /// <summary>F5/F10/F11/Run to End with the VM in C: forge compiles the program file, then - Debug - `fire` runs it under the debugger and does the first action, or -
        /// Release/Performance - runs it with its output (and its input) in the Output panel.</summary>
        private async Task StartCVmAsync(NativeFirstAction first)
        {
            var mode = _session.ExecutionMode;
            bool debug = mode == Runtime.VmExecutionMode.Debug;
            string modeName = mode.ToString().ToLowerInvariant();
            if (ResolveRunInput(null) is not { } input) return;
            string output = CVmRunOutput(input, modeName);
            string? defaultFile = input.Project != null ? null : input.Document?.DisplayName;
            string? defaultPath = input.Project != null ? null : FullPathOf(input.Document);

            _nativeStarting = true;
            BeginRun(input);
            _isBusy = true;
            var job = BeginBuildJob($"{input.What} (VM, {modeName})");
            try
            {
                UpdateStatus($"Compiling {input.What} for the VM ({modeName})...");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                var overlays = OverlaysOfOpenDocuments();
                var args = new System.Collections.Generic.List<string> { "bytecode" };
                args.AddRange(input.Project != null ? ForgeProjectArgs(input.Project) : new System.Collections.Generic.List<string> { ForgeScriptSource(input.Document!, overlays) });
                args.AddRange(new[] { "-m", ForgeMode(mode), "-o", output });
                var result = await RunForgeAsync(job, args, overlays);
                job.Feed.Drain();
                if (result.Cancelled)
                {
                    _isBusy = false;
                    EndBuildJob(job, BuildOutcome.Cancelled);
                    return;
                }
                FinishBuild("Compile", result.Ok, ForgeDiagnostics(result, overlays), defaultFile, defaultPath);
                if (!result.Ok || result.Output("program") is not { } built)
                {
                    _isBusy = false;
                    string error = BuildErrorText(result);
                    EndBuildJob(job, BuildOutcome.Failed, error);
                    UpdateStatus("Compile error: " + error.Split('\n')[0]);
                    if (!FailureShown(job) && !_quietCompile) _ = Dialogs.Message(this, error, "Compile error");
                    return;
                }
                EndBuildJob(job, BuildOutcome.Succeeded);

                string vm = result.Output("vm") ?? await Task.Run(() => CVm.Locate(64, 64));
                // (the devices of spark - the shared manager, its drivers and packet trace - for the program in `fire`: FIRE_DEVICE_HOST, docs/spec/VM_C.md "Das geteilte Gerät")
                var environment = CVmLaunch.Environment(result.Outputs.Where(o => o.Kind == "library").Select(o => o.Path),
                    new Dictionary<string, string> { [fire.Device.Manager.DeviceManager.DeviceHostServer.EnvironmentVariable] = _devices.ProgramHost.EnvironmentValue });
                var arguments = RunArguments(input);
                string workingDirectory = input.Project?.Directory ?? input.BaseDirectory ?? Path.GetDirectoryName(defaultPath ?? built)!;
                if (!debug)
                {
                    // a plain run: the output into the Output panel, a line below it to its input, the end in the status bar
                    var started = DateTime.Now;
                    System.Diagnostics.Process? run = null;
                    run = CVmLaunch.Start(vm, built, arguments, workingDirectory, OnScriptOutput, code => Dispatcher.UIThread.Post(() =>
                    {
                        FlushPendingOutput();
                        UpdateStatus($"Program finished (exit code {code}, {(DateTime.Now - started).TotalSeconds:0.0} s).");
                        if (ReferenceEquals(_nativeRun, run)) _output.InputTarget = null;
                        _nativePlainRunning = false;
                        UpdateRunButtons();
                    }), environment);
                    _nativeRun = run;
                    _nativeRunIsVm = true;
                    _output.InputTarget = line =>
                    {
                        if (run.HasExited) return false;
                        OnScriptOutput(line);   // (like a terminal shows what was typed - before what the program answers)
                        return CVmLaunch.WriteInput(run, line);
                    };
                    _nativePlainRunning = true;   // (it runs without the debugger: Start and the steps are grey until it ends)
                    _isBusy = false;
                    UpdateRunButtons();
                    UpdateStatus($"Running {input.What} in the VM ({modeName}) - Stop (Shift+F5) ends it.");
                    RevealTool("output");
                    return;
                }

                UpdateStatus($"Starting {input.What} in the VM under the debugger...");
                NativeDebugTarget debugTarget;
                try { debugTarget = await Task.Run(() => CVmLaunch.LaunchDebug(vm, built, arguments, workingDirectory, TimeSpan.FromSeconds(30), environment)); }
                catch (Exception ex) when (ex is NativeDebugException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
                {
                    _isBusy = false;
                    UpdateStatus("The program could not be debugged: " + ex.Message.Split('\n')[0]);
                    await Dialogs.Message(this, ex.Message, "Debugging");
                    return;
                }
                AttachNativeDebugTarget(debugTarget, input, first);
                // (over TCP the program's stdin is still the pipe: a line below the Output panel goes there)
                if (!debugTarget.CarriesConsole && debugTarget.Process is { } process)
                    _output.InputTarget = line =>
                    {
                        if (process.HasExited) return false;
                        OnScriptOutput(line);
                        return CVmLaunch.WriteInput(process, line);
                    };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _isBusy = false;
                EndBuildJob(job, BuildOutcome.Failed, ex.Message);
                UpdateStatus("The run failed: " + ex.Message);
            }
            finally
            {
                _nativeStarting = false;
                EndBuildJob(job, job.Cancelling ? BuildOutcome.Cancelled : BuildOutcome.Failed);   // (when nothing above ended it)
            }
        }
    }
}
