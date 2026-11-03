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
    // plain run of the program, its output in the Output panel.
    public partial class MainWindow
    {
        /// <summary>What a native run does first, once the program is built and connected: the button that started it.</summary>
        private enum NativeFirstAction { Continue, StepLine, StepInto, RunToEnd }

        /// <summary>A plain native run (Release/Performance) that is going on (null: none).</summary>
        private Process? _nativeRun;

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
            if (_nativeStarting) return;
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
            try
            {
                UpdateStatus($"Building {input.What} natively for {target.Name} ({modeName})...");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                var result = await Task.Run(() => NativeBuilder.BuildSafe(input.Sources, config, target, buildToolchain, output, mode, null, false, input.BaseDirectory, null, input.Plan));
                if (!string.IsNullOrWhiteSpace(result.Log)) _buildLog.Append(result.Log.EndsWith('\n') ? result.Log : result.Log + Environment.NewLine);
                FinishBuild("Native build", result.Ok, result.Diagnostics ?? Array.Empty<Diagnostic>(), defaultFile, defaultPath);
                if (!result.Ok)
                {
                    _isBusy = false;
                    UpdateStatus("The native build failed (see the build log).");
                    RevealTool("buildlog");
                    return;
                }

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
                _session.Attach(debugTarget);
                _session.UpdateBreakpoints(BreakpointLocations());   // now with the source indexes of this program
                var stale = debugTarget.SourcesNotMatching(input.Sources);
                if (stale.Count > 0)
                    _output.Append($"Warning: the program was built from other texts of {string.Join(", ", stale.Select(i => debugTarget.SourceFiles[i] is { } f ? Path.GetFileName(f) : $"source {i}"))} than the editor shows - lines may not match.{Environment.NewLine}");
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _isBusy = false;
                UpdateStatus("The native run failed: " + ex.Message);
            }
            finally
            {
                _nativeStarting = false;
            }
        }
    }
}
