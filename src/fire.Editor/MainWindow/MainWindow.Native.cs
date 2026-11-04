using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using fire.Compiler;
using fire.Debugging;
using fire.Projects;

namespace fire.Editor
{
    // The engine of a run (ROADMAP 8.7): the virtual machine in spark, or a native build of the program. The engine box next to the mode box chooses it
    // (saved in the project's settings.engine, for a single script in spark's settings). Native + Debug: built with -O0 -g into bin/<target>-dbg, started
    // with FIRE_DEBUG and debugged over the protocol (NativeDebugTarget) - the same panels, without the operand stack. Native + Release/Performance: a
    // plain run of the program, its output in the Output panel. Target esp32: the program becomes an ESP-IDF project in bin/esp32-<mode> (its component
    // in main/), `idf.py build flash` puts it on the board, and spark opens the board's serial port itself - Debug: the debug agent of the debug build
    // over its console UART (framed, SerialFramedTransport), Release/Performance: the console in the Output panel, a line below it is the board's input.
    public partial class MainWindow
    {
        /// <summary>What a native run does first, once the program is built and connected: the button that started it.</summary>
        private enum NativeFirstAction { Continue, StepLine, StepInto, RunToEnd }

        /// <summary>A plain native run (Release/Performance) that is going on (null: none).</summary>
        private Process? _nativeRun;

        /// <summary>The serial port of a plain run on a board (its console in the Output panel; null: none).</summary>
        private SerialFramedTransport? _boardRun;

        /// <summary>A native run is being built/started right now (the steps wait for it).</summary>
        private bool _nativeStarting;

        /// <summary>true while the stack panel is hidden because the engine is native (it comes back for the VM).</summary>
        private bool _stackHiddenForNative;

        private bool _updatingEngineUi;

        /// <summary>The engine of the active project/script: the project's settings (over the solution's), else spark's choice for single scripts, else the native build
        /// configuration, else the VM.</summary>
        private string CurrentEngine()
        {
            string? engine = null;
            if (ContextProject() is { } project) engine = ProjectSettings.Merge(project.Project.Settings, _workspace.Solution?.Settings).Engine;
            engine ??= _settings.Engine;
            if (engine == null)
                try { engine = LoadNativeConfig(out _).Engine; }
                catch (fire.Native.NativeConfigException) { }
            return string.Equals(engine, "native", StringComparison.OrdinalIgnoreCase) ? "native" : "vm";
        }

        private bool UsesNativeEngine() => CurrentEngine() == "native";

        /// <summary>Shows the engine of the active project/script in the box and the Run menu, and hides the stack panel for native.</summary>
        private void RefreshEngineCombo()
        {
            bool native = UsesNativeEngine();
            _updatingEngineUi = true;
            cmbEngine.SelectedIndex = native ? 1 : 0;
            _updatingEngineUi = false;
            mnuEngineVm.IsChecked = !native;
            mnuEngineNative.IsChecked = native;
            ApplyEngineToPanels(native);
        }

        /// <summary>The stack panel shows the operand stack of the VM - a native program has none: hidden while the engine is native, back for the VM
        /// (only when it was hidden for that reason).</summary>
        private void ApplyEngineToPanels(bool native)
        {
            if (!_tools.TryGetValue("stack", out var stack)) return;
            if (native && IsToolVisible(stack))
            {
                _factory.HideDockable(stack);
                _stackHiddenForNative = true;
            }
            else if (!native && _stackHiddenForNative)
            {
                _stackHiddenForNative = false;
                if (!IsToolVisible(stack)) _factory.RestoreDockable(stack);
            }
        }

        private void cmbEngine_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_updatingEngineUi || cmbEngine.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            _ = SetEngine(tag);
        }

        private void mnuEngineVm_Click(object? sender, RoutedEventArgs e) => _ = SetEngine("vm");
        private void mnuEngineNative_Click(object? sender, RoutedEventArgs e) => _ = SetEngine("native");

        /// <summary>Chooses the engine: for a project it goes into its settings.engine (saved), for a single script into spark's settings. A run of the other engine ends.</summary>
        private async Task SetEngine(string engine)
        {
            if (CurrentEngine() != engine && (_session.HasProgram || _nativeRun != null)) Stop_Click(this, new RoutedEventArgs());
            if (ContextProject() is { } project)
            {
                project.Project.Settings ??= new ProjectSettings();
                project.Project.Settings.Engine = engine;
                try
                {
                    _workspace.SaveProject(project);
                    UpdateStatus($"Engine of {project.Name}: {(engine == "native" ? "native" : "VM")} (saved in {Path.GetFileName(project.FilePath)}).");
                }
                catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "Engine"); }
            }
            else
            {
                _settings.Engine = engine;
                _settings.Save();
                UpdateStatus($"Engine: {(engine == "native" ? "native" : "VM")}.");
            }
            RefreshEngineCombo();
        }

        /// <summary>Ends a plain native run that is still going on.</summary>
        private void StopNativeRun()
        {
            _output.InputTarget = null;
            var board = _boardRun;
            _boardRun = null;
            board?.Dispose();
            var run = _nativeRun;
            _nativeRun = null;
            if (run == null) return;
            try { if (!run.HasExited) run.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            run.Dispose();
        }

        /// <summary>Where the program of a native run goes: a project into bin/&lt;target&gt;-&lt;mode&gt; of its folder, a saved script into bin/&lt;target&gt;-&lt;mode&gt;
        /// next to it, an unsaved one into the temporary folder.</summary>
        private string NativeRunOutput(RunInput input, string target, string mode)
        {
            bool windows = target == "windows" || OperatingSystem.IsWindows();
            if (input.Project is { } project) return ProjectOutput.ProgramPath(project.Directory, project.Name, target, mode, windows);
            string? path = FullPathOf(input.Document);
            string directory = path != null ? Path.GetDirectoryName(path)! : Path.Combine(Path.GetTempPath(), "spark-native");
            string name = path != null ? Path.GetFileNameWithoutExtension(path) : "program";
            return ProjectOutput.ProgramPath(directory, name, target, mode, windows);
        }

        /// <summary>F5/F10/F11/Run to End with the native engine: builds the program for the chosen target (Debug: -O0 -g), then - Debug - starts it under the
        /// debugger and does the first action, or - Release/Performance - runs it with its output in the Output panel.</summary>
        private async Task StartNativeAsync(NativeFirstAction first)
        {
            if (_nativeStarting || BuildRunning()) return;
            if (_nativeRun is { HasExited: false })
            {
                UpdateStatus("The native program is running - Stop (Shift+F5) ends it.");
                return;
            }
            StopNativeRun();
            _session.Reset();
            _reportedErrors.Clear();
            if (ResolveRunInput(null) is not { } input) return;

            fire.Native.NativeConfig config;
            fire.Runtime.TargetProfile target;
            fire.Native.ToolchainDef toolchain;
            try
            {
                config = LoadNativeConfig(out _);
                target = config.ResolveTarget(_nativeTargetChoice ?? input.Plan?.Settings.Target);
                toolchain = config.ResolveToolchain(target, input.Plan?.Settings.Toolchain);
            }
            catch (fire.Native.NativeConfigException ex)
            {
                await Dialogs.Message(this, ex.Message, "Native run");
                return;
            }
            if (target.Native.Platform == "esp32")
            {
                await StartBoardAsync(input, config, target, first);
                return;
            }
            if (!NativeLaunch.CanRunHere(target, toolchain, out string why))
            {
                UpdateStatus($"Cannot run natively: {why}. Choose a target of this machine (Build > Target) or the VM engine.");
                return;
            }

            var mode = _session.ExecutionMode;
            bool debug = mode == Runtime.VmExecutionMode.Debug;
            string modeName = mode.ToString().ToLowerInvariant();
            string output = NativeRunOutput(input, target.Name, modeName);
            var buildToolchain = debug ? NativeLaunch.DebugToolchain(toolchain) : toolchain;
            string? defaultFile = input.Project != null ? null : input.Document?.DisplayName;
            string? defaultPath = input.Project != null ? null : FullPathOf(input.Document);

            _nativeStarting = true;
            BeginRun(input);
            _isBusy = true;
            var job = BeginBuildJob($"{input.What} (native, {target.Name}, {modeName})");
            try
            {
                UpdateStatus($"Building {input.What} natively for {target.Name} ({modeName})...");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                var result = await Task.Run(() => NativeBuilder.BuildSafe(input.Sources, config, target, buildToolchain, output, mode, null, false, input.BaseDirectory, null, input.Plan, job.Monitor));
                if (!string.IsNullOrWhiteSpace(result.Log)) _buildLog.Append(result.Log.EndsWith('\n') ? result.Log : result.Log + Environment.NewLine);
                if (result.Cancelled)
                {
                    _isBusy = false;
                    EndBuildJob(job, BuildOutcome.Cancelled);
                    return;
                }
                FinishBuild("Native build", result.Ok, result.Diagnostics ?? Array.Empty<Diagnostic>(), defaultFile, defaultPath);
                if (!result.Ok)
                {
                    _isBusy = false;
                    EndBuildJob(job, BuildOutcome.Failed, BuildErrorText(result));
                    UpdateStatus("The native build failed (see the build log).");
                    RevealTool("buildlog");
                    return;
                }
                EndBuildJob(job, BuildOutcome.Succeeded);

                var arguments = RunArguments(input);
                string workingDirectory = input.Project?.Directory ?? input.BaseDirectory ?? Path.GetDirectoryName(result.Output)!;
                if (!debug)
                {
                    // a plain run: the output into the Output panel, the end in the status bar
                    var started = DateTime.Now;
                    _nativeRun = NativeLaunch.Start(result.Output, arguments, workingDirectory, OnScriptOutput, code => Dispatcher.UIThread.Post(() =>
                    {
                        FlushPendingOutput();
                        UpdateStatus($"Native program finished (exit code {code}, {(DateTime.Now - started).TotalSeconds:0.0} s).");
                    }));
                    _nativeRun.StandardInput.Close();
                    _isBusy = false;
                    UpdateStatus($"Running {Path.GetFileName(result.Output)} natively ({modeName}) - Stop (Shift+F5) ends it.");
                    RevealTool("output");
                    return;
                }

                UpdateStatus($"Starting {Path.GetFileName(result.Output)} under the debugger...");
                NativeDebugTarget debugTarget;
                try { debugTarget = await Task.Run(() => NativeLaunch.LaunchDebug(result.Output, arguments, workingDirectory, TimeSpan.FromSeconds(30))); }
                catch (Exception ex) when (ex is NativeDebugException or System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
                {
                    _isBusy = false;
                    UpdateStatus("The native program could not be debugged: " + ex.Message.Split('\n')[0]);
                    await Dialogs.Message(this, ex.Message, "Native debugging");
                    return;
                }
                AttachNativeDebugTarget(debugTarget, input, first);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _isBusy = false;
                EndBuildJob(job, BuildOutcome.Failed, ex.Message);
                UpdateStatus("The native run failed: " + ex.Message);
            }
            finally
            {
                _nativeStarting = false;
                EndBuildJob(job, job.Cancelling ? BuildOutcome.Cancelled : BuildOutcome.Failed);   // (when nothing above ended it)
            }
        }

        /// <summary>The errors of a failed native build for its window: the errors of the compiler, else the end of the log (what the C compiler said last).</summary>
        private static string BuildErrorText(NativeBuildResult result)
        {
            var errors = (result.Diagnostics ?? Array.Empty<Diagnostic>()).Where(d => d.IsError).Select(d => d.ToLogLine()).Take(6).ToList();
            var lines = result.Log.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var tail = lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(4).ToList();
            if (tail.Count == 0) tail = lines.TakeLast(4).ToList();
            return string.Join(Environment.NewLine, errors.Concat(tail).Distinct());
        }
        /// <summary>A native program under the debugger, connected and at its entry: the session takes it, then the first action.</summary>
        private void AttachNativeDebugTarget(NativeDebugTarget debugTarget, RunInput input, NativeFirstAction first)
        {
            _session.Attach(debugTarget);
            _session.UpdateBreakpoints(BreakpointLocations());   // now with the source indexes of this program
            var stale = debugTarget.SourcesNotMatching(input.Sources);
            if (stale.Count > 0)
                _output.Append($"Warning: the program was built from other texts of {string.Join(", ", stale.Select(i => debugTarget.SourceFiles[i] is { } f ? Path.GetFileName(f) : $"source {i}"))} than the editor shows - lines may not match.{Environment.NewLine}");
            if (debugTarget.CarriesConsole)
                _output.InputTarget = line =>
                {
                    if (!debugTarget.WriteInput(line)) return false;
                    OnScriptOutput(line);   // (like a terminal shows what was typed)
                    return true;
                };
            _debugger.Refresh(BreakpointDescriptions());
            _isBusy = false;
            BeginStep();
            switch (first)
            {
                case NativeFirstAction.StepLine: _session.StepLine(); break;
                case NativeFirstAction.StepInto: _session.StepInto(); break;
                case NativeFirstAction.RunToEnd: _session.RunToCompletion(); break;
                default: _session.Continue(BreakpointLocations()); break;
            }
        }

        /// <summary>Run/Debug on an ESP32: builds the ESP-IDF project (bin/esp32-&lt;mode&gt;), `idf.py build flash`, then opens the board's serial port (the
        /// project's/target's `port` and `baud`, else the only USB serial port, else the one esptool used) and restarts the board - Debug: the debug agent of
        /// the debug build over the console UART; Release/Performance: the console in the Output panel.</summary>
        private async Task StartBoardAsync(RunInput input, fire.Native.NativeConfig config, fire.Runtime.TargetProfile target, NativeFirstAction first)
        {
            if (BuildRunning()) return;
            string? idf = Esp32Launch.FindIdf(_settings.IdfPath);
            if (idf == null)
            {
                await Dialogs.Message(this, Esp32Launch.MissingIdfMessage, "ESP32");
                return;
            }
            var mode = _session.ExecutionMode;
            bool debug = mode == Runtime.VmExecutionMode.Debug;
            string modeName = mode.ToString().ToLowerInvariant();
            string programPath = NativeRunOutput(input, target.Name, modeName);
            string projectDir = Path.GetDirectoryName(Path.GetFullPath(programPath))!;
            var settings = input.Plan?.Settings;
            string? port = Esp32Launch.ResolvePort(settings?.Port ?? target.Native.Port, out string portProblem);
            int baud = settings?.Baud ?? target.Native.Baud ?? Esp32Launch.DefaultBaud;
            string? defaultFile = input.Project != null ? null : input.Document?.DisplayName;
            string? defaultPath = input.Project != null ? null : FullPathOf(input.Document);

            _nativeStarting = true;
            BeginRun(input);
            _isBusy = true;
            var job = BeginBuildJob($"{input.What} (ESP32, {modeName})");
            try
            {
                UpdateStatus($"Building {input.What} for the ESP32 ({modeName})...");
                var toolchain = fire.Native.ToolchainDef.BuiltIn["esp-idf"];   // (the component; spark runs idf.py itself)
                var result = await Task.Run(() => NativeBuilder.BuildSafe(input.Sources, config, target, toolchain, Path.Combine(projectDir, "main"), mode, null, false, input.BaseDirectory, null, input.Plan, job.Monitor));
                if (!string.IsNullOrWhiteSpace(result.Log)) _buildLog.Append(result.Log.EndsWith('\n') ? result.Log : result.Log + Environment.NewLine);
                if (result.Cancelled)
                {
                    _isBusy = false;
                    EndBuildJob(job, BuildOutcome.Cancelled);
                    return;
                }
                FinishBuild("Native build", result.Ok, result.Diagnostics ?? Array.Empty<Diagnostic>(), defaultFile, defaultPath);
                if (!result.Ok)
                {
                    _isBusy = false;
                    EndBuildJob(job, BuildOutcome.Failed, BuildErrorText(result));
                    UpdateStatus("The native build failed (see the build log).");
                    RevealTool("buildlog");
                    return;
                }
                Esp32Launch.WriteProject(projectDir, Path.GetFileName(programPath));
                UpdateStatus($"idf.py build flash{(port != null ? " on " + port : "")} (the first build of ESP-IDF takes minutes)...");
                RevealTool("buildlog");
                bool flashed;
                string? usedPort;
                try
                {
                    (flashed, usedPort) = await Task.Run(() => Esp32Launch.BuildAndFlash(idf, projectDir, port, true, line =>
                    {
                        job.Monitor.Line(line);
                        Dispatcher.UIThread.Post(() => _buildLog.AppendLine(line));
                    }, job.Token, job.Monitor));
                }
                catch (OperationCanceledException)
                {
                    _isBusy = false;
                    EndBuildJob(job, BuildOutcome.Cancelled);
                    return;
                }
                if (!flashed)
                {
                    _isBusy = false;
                    EndBuildJob(job, BuildOutcome.Failed, "idf.py build flash failed (see the log above and the build log).");
                    UpdateStatus("idf.py build flash failed (see the build log).");
                    return;
                }
                EndBuildJob(job, BuildOutcome.Succeeded);
                if (usedPort == null)
                {
                    _isBusy = false;
                    await Dialogs.Message(this, $"The program is on the board, but spark does not know its serial port ({portProblem}). Set \"port\" in the project settings or the target of fire.native.json.", "ESP32");
                    return;
                }
                SerialFramedTransport transport;
                try { transport = SerialFramedTransport.Open(usedPort, baud, reset: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
                {
                    _isBusy = false;
                    await Dialogs.Message(this, $"The serial port {usedPort} could not be opened: {ex.Message}", "ESP32");
                    return;
                }
                RevealTool("output");
                if (!debug)
                {
                    // plain Run: the board's console in the Output panel, its input below it
                    _boardRun = transport;
                    Esp32Launch.Monitor(transport, OnScriptOutput, () => Dispatcher.UIThread.Post(() =>
                    {
                        if (_boardRun != transport) return;
                        FlushPendingOutput();
                        UpdateStatus($"The serial port {usedPort} closed.");
                    }));
                    _output.InputTarget = line =>
                    {
                        if (!transport.WriteInput(line)) return false;
                        OnScriptOutput(line);
                        return true;
                    };
                    _isBusy = false;
                    UpdateStatus($"Running on the ESP32 at {usedPort} ({baud} baud) - Stop (Shift+F5) closes the port.");
                    return;
                }
                UpdateStatus($"Waiting for the debug build on the ESP32 at {usedPort}...");
                NativeDebugTarget debugTarget;
                try { debugTarget = await Task.Run(() => NativeDebugTarget.OpenFramed(transport, TimeSpan.FromSeconds(20), OnScriptOutput)); }
                catch (NativeDebugException ex)
                {
                    _isBusy = false;
                    UpdateStatus("The program on the ESP32 could not be debugged: " + ex.Message.Split('\n')[0]);
                    await Dialogs.Message(this, ex.Message, "Debugging on the ESP32");
                    return;
                }
                if (debugTarget.SourcesNotMatching(input.Sources).Count > 0)
                {
                    debugTarget.Dispose();
                    _isBusy = false;
                    UpdateStatus("The board runs another program.");
                    await Dialogs.Message(this, $"The board at {usedPort} runs another program than the one spark built (its sources differ) - did flashing reach this board? Check \"port\" in the settings.", "Debugging on the ESP32");
                    return;
                }
                AttachNativeDebugTarget(debugTarget, input, first);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _isBusy = false;
                EndBuildJob(job, BuildOutcome.Failed, ex.Message);
                UpdateStatus("The run on the ESP32 failed: " + ex.Message);
            }
            finally
            {
                _nativeStarting = false;
                EndBuildJob(job, job.Cancelling ? BuildOutcome.Cancelled : BuildOutcome.Failed);   // (when nothing above ended it)
            }
        }
    }
}
