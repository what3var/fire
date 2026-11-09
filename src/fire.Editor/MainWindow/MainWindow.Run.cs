using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using fire.Projects;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace fire.Editor
{
    // Run and debug.
    public partial class MainWindow
    {
        // Output queue (see OnScriptOutput) - thread safe, as ANY thread (main or a fire thread) may write into it at the same time; _outputFlushTimer fetches it regularly,
        // BUNDLED on the UI thread, instead of updating for every single print().
        private readonly ConcurrentQueue<string> _pendingOutput = new();
        private readonly DispatcherTimer _outputFlushTimer;

        // Prevents overlapping step requests on the same thread (see the doc of DebugThreadContext.RequestStep: fire-and-forget, a second call while the first is still
        // running could overwrite its result) - set when requesting, reset as soon as ThreadPaused fires for the thread concerned.
        /// <summary>The program runs (between a start or a step and the next pause or its end): Start and the steps are greyed out, Stop is not.</summary>
        private bool _isBusy { get => _isBusyValue; set { if (_isBusyValue == value) return; _isBusyValue = value; UpdateRunButtons(); } }
        private bool _isBusyValue;

        /// <summary>Start, Run to End and the steps only when nothing runs (no program, a paused one, an ended one) and no build; Stop when something runs or is paused.</summary>
        private void UpdateRunButtons()
        {
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) { Avalonia.Threading.Dispatcher.UIThread.Post(UpdateRunButtons); return; }
            bool idle = !_isBusyValue && _buildJob == null && !_nativePlainRunning;
            foreach (var c in new Control?[] { tbRun, tbRunToEnd, tbStep, tbStepInto, tbStepOut, miRun, miStep, miStepInto, miStepOut, miContinue, miRunToEnd })
                if (c != null) c.IsEnabled = idle;
            if (tbStop != null) tbStop.IsEnabled = _isBusyValue || _buildJob != null || _nativeRun != null || (_session.HasProgram && !_session.IsFinished);
            UpdateRuntimePanel();
        }

        // The Runtime panel (Panels/RuntimePanelControl.cs) watches a run: it shows itself when a program starts and hides again a few seconds after it ended, when
        // it was hidden before (a panel the user keeps open stays).
        private bool _runtimeActive;
        private bool _runtimeAutoShown;
        private System.Diagnostics.Process? _runtimeProcess;
        private DispatcherTimer? _runtimeHide;

        private void UpdateRuntimePanel()
        {
            bool active = _nativeRun != null || (_session.HasProgram && _session.IsStarted && !_session.IsFinished);
            // (a profiled run that ended or was stopped - also one too short to have been seen running)
            if (_profile != null && !active && (!_session.HasProgram || _session.IsFinished)) FinishProfile();
            if (active == _runtimeActive) return;
            _runtimeActive = active;
            _tools.TryGetValue("runtime", out var tool);
            if (active)
            {
                _runtimeHide?.Stop();
                // (a native program: its own process - also while it is debugged; on a board over its serial line there is none here)
                var nativeTarget = _session.Target as fire.Debugging.NativeDebugTarget;
                _runtimeProcess = _nativeRun ?? nativeTarget?.Process;
                bool remote = _nativeRun == null && nativeTarget != null && _runtimeProcess == null;
                string what = _runtimeProcess != null || nativeTarget != null ? $"native, {_session.ActiveExecutionMode ?? _session.ExecutionMode}" : $"VM, {_session.ActiveExecutionMode ?? _session.ExecutionMode}";
                _runtimePanel.Start(_runtimeProcess, what, remote);
                if (tool != null && !IsToolVisible(tool))
                {
                    _runtimeAutoShown = true;
                    _factory.RestoreDockable(tool);
                }
                return;
            }
            int? exitCode = null;
            try { if (_runtimeProcess is { HasExited: true } p) exitCode = p.ExitCode; }
            catch (InvalidOperationException) { }
            _runtimePanel.Stop(exitCode);
            _runtimeProcess = null;
            FinishProfile();
            if (!_runtimeAutoShown || tool == null) return;
            _runtimeHide ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _runtimeHide.Tick -= RuntimeHideTick;
            _runtimeHide.Tick += RuntimeHideTick;
            _runtimeHide.Start();
        }

        // The profiler (Profile switch, fire.Runtime.LineProfile): a run in the VM counts its lines; when it has ended, the Profile tab shows them (ProfileReport).
        private fire.Runtime.LineProfile? _profile;
        private string _profileWhat = "";
        private System.Diagnostics.Stopwatch _profileClock = new();

        private void Profile_Click(object? sender, RoutedEventArgs e)
        {
            _settings.Profile = !_settings.Profile;
            tbProfile.IsChecked = _settings.Profile;
            _settings.Save();
            UpdateStatus(_settings.Profile ? "Runs in the VM are profiled: the Profile tab opens when the program has ended." : "Runs without profiling.");
        }

        /// <summary>A program for the VM is compiled: with the Profile switch on, its run counts into a new profile.</summary>
        private void BeginProfile(string what)
        {
            fire.Runtime.LineProfile.Active = _profile = _settings.Profile && _session.Target is fire.Debugging.VmDebugTarget ? new fire.Runtime.LineProfile() : null;
            _profileWhat = what;
            _profileClock = System.Diagnostics.Stopwatch.StartNew();
        }

        /// <summary>The profiled run has ended: its report in the Profile tab.</summary>
        private void FinishProfile()
        {
            var profile = _profile;
            if (profile == null) return;
            _profile = null;
            if (ReferenceEquals(fire.Runtime.LineProfile.Active, profile)) fire.Runtime.LineProfile.Active = null;
            if ((_session.Target as fire.Debugging.VmDebugTarget)?.Session?.CompiledProgram is not { } program) return;
            var files = _session.SourceFiles;
            var report = ProfileReport.Build(_profileWhat, _profileClock.Elapsed.TotalSeconds, profile.Snapshot(), fire.Runtime.LineProfile.ExecutableLines(program),
                _session.FirstUserSourceIndex, i =>
                {
                    string? path = i < files.Count ? files[i] : null;
                    if (path != null && _documents.FirstOrDefault(d => d.View.FilePath != null && string.Equals(Path.GetFullPath(d.View.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) is { } open)
                        return (path, open.View.GetText());
                    if (path != null && File.Exists(path))
                    {
                        try { return (path, File.ReadAllText(path)); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (path, null); }
                    }
                    return (path, path == null && i == _session.FirstUserSourceIndex ? _debugDocument?.View.GetText() : null);
                });
            string json = report.ToJson();
            var tab = _documents.FirstOrDefault(d => d.Kind == DocumentKind.Profile);
            if (tab != null) { tab.View.ResetTo(json, null); Activate(tab); }
            else CreateDocument(DocumentKind.Profile, json, null, untitledName: "Profile");
        }

        /// <summary>A line in the Profile tab was clicked: the file (or the program's unsaved document) at that line.</summary>
        private void OpenProfileLine(string? path, int line)
        {
            var doc = path != null ? OpenFile(path) : _debugDocument;
            if (doc == null) return;
            Activate(doc);
            Dispatcher.UIThread.Post(() => doc.View.GoToLine(line), DispatcherPriority.Loaded);
        }

        private void RuntimeHideTick(object? sender, EventArgs e)
        {
            _runtimeHide?.Stop();
            if (_runtimeActive || !_tools.TryGetValue("runtime", out var tool)) return;
            _runtimeAutoShown = false;
            if (IsToolVisible(tool) && (RootDock == null || !_factory.IsDockablePinned(tool, RootDock))) _factory.HideDockable(tool);
        }

        // The script that is compiled/paused in the debugger right now (stays so even when the tab is changed): a single file's document. null for a run of a project
        // (its files are the sources of the run, see _session.SourceFiles).
        private OpenDocument? _debugDocument;

        // true while the run is a project (the files of the project are its sources, the documents of them take part in the debugging)
        private bool _debugIsProject;

        // The document that shows the highlighted (paused) line.
        private OpenDocument? _highlightedDoc;

        private void UpdateExecutionModeSelection(DebugSession session)
        {
            mnuRunDebug.IsChecked = session.ExecutionMode == Runtime.VmExecutionMode.Debug;
            mnuRunRelease.IsChecked = session.ExecutionMode == Runtime.VmExecutionMode.Release;
            mnuRunPerformance.IsChecked = session.ExecutionMode == Runtime.VmExecutionMode.Performance;

            _updatingModeUi = true;
            cmbMode.SelectedIndex = session.ExecutionMode switch
            {
                Runtime.VmExecutionMode.Debug => 0,
                Runtime.VmExecutionMode.Release => 1,
                _ => 2,
            };
            _updatingModeUi = false;
        }

        // Prevents the programmatic setting of the selection (UpdateExecutionModeSelection) from setting the mode again.
        private bool _updatingModeUi;

        private void cmbMode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_updatingModeUi || cmbMode.SelectedItem is not ComboBoxItem { Tag: string tag }) return;

            _session.ExecutionMode = Enum.Parse<Runtime.VmExecutionMode>(tag);
            UpdateExecutionModeSelection(_session);
        }

        /// <summary>The source index of the program of the run for a document: its file in the sources (a project, its libraries), or - for the single file that was compiled - the one source
        /// of the program (the prelude is always at 0, the first own source normally at 1 before the first compile). -1: the document is not part of the run.</summary>
        private int SourceIndexOf(OpenDocument doc)
        {
            string? path = FullPathOf(doc);
            var files = _session.SourceFiles;
            if (path != null)
                for (int i = 0; i < files.Count; i++)
                    if (files[i] != null && ProjectFiles.PathComparer.Equals(files[i]!, path)) return i;
            if (!_debugIsProject && ReferenceEquals(doc, _debugDocument)) return _session.FirstUserSourceIndex == 0 ? 1 : _session.FirstUserSourceIndex;
            return -1;
        }

        /// <summary>Does the document take part in the run (a breakpoint in it counts, its lines can be shown as the paused line)?</summary>
        private bool TakesPartInRun(OpenDocument doc) => SourceIndexOf(doc) >= 0;

        /// <summary>The breakpoints of all open scripts that take part in the run: (source index, line), linked with the source index of the current program (see DebugSession.SourceFiles).</summary>
        private HashSet<(int SourceIndex, int Line)> BreakpointLocations()
        {
            var result = new HashSet<(int, int)>();
            // (by file too: a plugin the program loads later has sources of its own - the debug target applies these in them, see IDebugTarget.SetFileBreakpoints)
            _session.Target?.SetFileBreakpoints(_documents.Where(d => d.Script is { Breakpoints.Count: > 0 } && FullPathOf(d) != null)
                .SelectMany(d => d.Script!.Breakpoints.Select(line => (FullPathOf(d)!, line))).ToList());
            foreach (var doc in _documents)
            {
                if (doc.Script is not { } script || script.Breakpoints.Count == 0) continue;
                int index = SourceIndexOf(doc);
                if (index < 0) continue;
                foreach (int line in script.Breakpoints) result.Add((index, line));
            }
            return result;
        }

        /// <summary>For the debugger panels: the (numerically sorted) breakpoint lines of the script in the debugger, else of the active script; in a project each with its file.</summary>
        private List<string> BreakpointDescriptions()
        {
            if (_debugIsProject)
                return _documents.Where(d => d.Script is { Breakpoints.Count: > 0 } && TakesPartInRun(d))
                    .SelectMany(d => d.Script!.Breakpoints.OrderBy(l => l).Select(l => $"{d.DisplayName}:{l}")).ToList();
            return (_debugDocument ?? ActiveDocument)?.Script?.Breakpoints.OrderBy(l => l).Select(l => l.ToString()).ToList()
                ?? new List<string>();
        }

        private void OnScriptOutput(string text)
        {
            // Deliberately ONLY queue, NO UI interaction here - this method can fire very often and very fast from a fire thread (e.g. in a loop with hundreds of print() calls).
            // _outputFlushTimer (see the constructor) fetches the whole queue in bundles, at fixed intervals, in ONE append.
            _pendingOutput.Enqueue(text);
            _runtimePanel.OutputLine();
        }

        private void FlushPendingOutput()
        {
            if (_pendingOutput.IsEmpty) return;

            var batch = new System.Text.StringBuilder();
            while (_pendingOutput.TryDequeue(out var line))
                batch.Append(line).Append(Environment.NewLine);

            _output.Append(batch.ToString());
        }

        /// <summary>Shows the paused line in the tab of the file it is in (opened and brought to the front when needed; a line in the prelude or in a library without a file shows nothing); null =
        /// remove the highlight.</summary>
        private void ShowDebugLocation((int SourceIndex, int Line)? location)
        {
            if (_highlightedDoc?.Script is { } old) old.HighlightedLine = null;
            _highlightedDoc = null;
            if (location == null) return;
            var doc = DocumentOfSource(location.Value.SourceIndex);
            if (doc?.Script is not { } script) return;
            script.HighlightedLine = location.Value.Line;
            _highlightedDoc = doc;
            Activate(doc);
            script.ScrollToLine(location.Value.Line);
        }

        private OpenDocument? DocumentOfSource(int sourceIndex)
        {
            var files = _session.SourceFiles;
            if (sourceIndex >= 0 && sourceIndex < files.Count && files[sourceIndex] is { } path)
            {
                var open = _documents.FirstOrDefault(d => FullPathOf(d) is { } p && ProjectFiles.PathComparer.Equals(p, path));
                return open ?? OpenFile(path);
            }
            // the single file that was compiled: its source is the first of the program that is not a prelude
            if (!_debugIsProject && _debugDocument != null && sourceIndex == (_session.FirstUserSourceIndex == 0 ? 1 : _session.FirstUserSourceIndex)) return _debugDocument;
            return null;
        }

        /// <summary>F5 like in Visual Studio: compiles if necessary (nothing compiled yet, stopped or the program has ended) and then runs to the next breakpoint. Every further F5
        /// continues the execution to the next breakpoint; when the program has ended, F5 starts it again.</summary>
        private async void Run_Click(object? sender, RoutedEventArgs e)
        {
            if (_isBusy || BuildRunning()) return; // running right now - there is nothing to continue
            if ((!_session.HasProgram || _session.IsFinished) && UsesNativeEngine()) { _ = StartNativeAsync(NativeFirstAction.Continue); return; }
            if ((!_session.HasProgram || _session.IsFinished) && !await CompileAndPrepareAsync(null)) return;
            if (!_session.HasProgram) return;
            BeginStep();
            _session.Continue(BreakpointLocations());
        }

        /// <summary>Ctrl+F5: ALWAYS compiles again (also in the middle of a session - a running program is ended) and then starts like F5 up to the first breakpoint.</summary>
        private async void Restart_Click(object? sender, RoutedEventArgs e)
        {
            if (BuildRunning()) return;
            if (UsesNativeEngine()) { _ = StartNativeAsync(NativeFirstAction.Continue); return; }
            if (!await CompileAndPrepareAsync(null) || !_session.HasProgram) return;
            BeginStep();
            _session.Continue(BreakpointLocations());
        }

        /// <summary>What a run is made of: the project (with its plan) or the single script, the source texts, the folder of a single script.</summary>
        private sealed record RunInput(LoadedProject? Project, BuildPlan? Plan, string[] Sources, string? BaseDirectory, OpenDocument? Document)
        {
            public string What => Project != null ? Project.Name : Document?.DisplayName ?? "script";
        }

        /// <summary>What the active document belongs to: the project (all its files and libraries, with the text of the open documents and the settings of the project) when the
        /// document is part of one, else the single script. null (with a message) on a problem or when there is nothing to run.</summary>
        private RunInput? ResolveRunInput(string? filename)
        {
            var doc = ActiveDocument;
            var project = ContextProject();
            // a library cannot run itself: Start (F5) on one of its files runs the startup project of the solution (Build packs the library)
            if (project?.Project.Type == OutputType.Library && filename == null && _workspace.Startup is { } startup && startup.Project.Type == OutputType.Exe)
            {
                project = startup;
                UpdateStatus($"{ContextProject()!.Name} is a library: running the startup project {startup.Name}.");
            }

            if (project != null)
            {
                var plan = CreatePlan(project);
                if (!plan.IsValid)
                {
                    string errors = string.Join(Environment.NewLine, plan.Errors);
                    UpdateStatus($"The project {project.Name} has problems: {plan.Errors[0]}");
                    _ = Dialogs.Message(this, errors, "Project problems");
                    return null;
                }
                if (plan.Type == OutputType.Library && filename == null)
                {
                    UpdateStatus($"{project.Name} is a library: it has no entry point to run (Project > Pack Library, or run a program that references it).");
                    return null;
                }
                return new RunInput(project, plan, plan.SourceTexts.ToArray(), null, doc);
            }
            if (doc?.Script is not { } script)
            {
                UpdateStatus(doc == null ? "No document is open." : "The active document is not a script - select a script tab to run.");
                return null;
            }
            return new RunInput(null, null, new[] { script.GetText() }, script.BaseDirectory, doc);
        }

        /// <summary>A new run begins: the output and the build log start again, the documents of the run are noted (breakpoints, the paused line).</summary>
        private void BeginRun(RunInput input)
        {
            _output.Clear();
            BeginBuildLog(input.What);
            while (_pendingOutput.TryDequeue(out _)) { } // discard what is left of a previous run that has not flowed off yet
            ShowDebugLocation(null);
            _debugIsProject = input.Project != null;
            _debugDocument = input.Project != null ? null : input.Document;
            _isBusy = false;
            _session.UpdateBreakpoints(BreakpointLocations());
        }

        /// <summary>The arguments the program of a run gets (Environment.Args): the project's settings.arguments, else those of the single script.</summary>
        private List<string> RunArguments(RunInput input) =>
            input.Project != null ? input.Plan!.Settings.Arguments ?? new List<string>() : ScriptArguments(input.Document);

        /// <summary>Compiles what the active document belongs to (discards the previous session; see ResolveRunInput) for the virtual machine - on a thread of the pool, as a
        /// build job (its window appears when the compile takes longer than half a second; Cancel stops it). false on an error, when it was cancelled or when there is
        /// nothing to build.</summary>
        private async Task<bool> CompileAndPrepareAsync(string? filename)
        {
            if (BuildRunning()) return false;
            StopNativeRun();
            if (ResolveRunInput(filename) is not { } input) return false;
            var project = input.Project;
            var plan = input.Plan;
            var doc = input.Document;
            string? buildFile = project != null ? null : doc?.DisplayName;
            string? buildPath = project != null ? null : FullPathOf(doc);
            BeginRun(input);

            var environment = new fire.Standard.ProgramEnvironment
            {
                Args = RunArguments(input),
                ProgramPath = project != null ? plan!.SourcePaths.LastOrDefault() ?? "" : FullPathOf(doc) ?? "",
            };
            var job = BeginBuildJob(filename != null ? $"{input.What} (standalone, {RunModeName})" : $"{input.What} (VM, {RunModeName})");
            bool compiled;
            try { compiled = await _session.CompileAsync(input.Sources, filename, input.BaseDirectory, plan, environment, job.Monitor); }
            catch (OperationCanceledException)
            {
                EndBuildJob(job, BuildOutcome.Cancelled);
                return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                EndBuildJob(job, BuildOutcome.Failed, ex.Message);
                UpdateStatus("The compile failed: " + ex.Message);
                return false;
            }
            // the errors and warnings of the compile: into the build log and the error list (until the next build)
            FinishBuild("Compile", compiled, _session.LastDiagnostics, buildFile, buildPath);
            if (!compiled)
            {
                EndBuildJob(job, BuildOutcome.Failed, _session.CompileError ?? "");
                UpdateStatus($"Compile error: {_session.CompileError}");
                if (!FailureShown(job)) _ = Dialogs.Message(this, _session.CompileError ?? "", "Compile error");
                return false;
            }
            EndBuildJob(job, BuildOutcome.Succeeded);

            _session.UpdateBreakpoints(BreakpointLocations());   // now with the source indexes of this program
            BeginProfile(input.What);
            UpdateStatus(project != null ? $"Compiled {project.Name} ({plan!.Sources.Count} file{(plan.Sources.Count == 1 ? "" : "s")}) - ready to step, continue or run to the end." : "Compiled - ready to step, continue or run to the end.");
            _debugger.Refresh(BreakpointDescriptions());
            return true;
        }

        private async void Step_Click(object? sender, RoutedEventArgs e)
        {
            if (BuildRunning()) return;
            if (!_session.HasProgram && UsesNativeEngine()) { _ = StartNativeAsync(NativeFirstAction.StepLine); return; }
            if (!_session.HasProgram) await CompileAndPrepareAsync(null);
            if (!_session.HasProgram || _isBusy) return;
            BeginStep();
            _session.StepLine();
        }

        private async void StepInto_Click(object? sender, RoutedEventArgs e)
        {
            if (BuildRunning()) return;
            if (!_session.HasProgram && UsesNativeEngine()) { _ = StartNativeAsync(NativeFirstAction.StepInto); return; }
            if (!_session.HasProgram) await CompileAndPrepareAsync(null);
            if (!_session.HasProgram || _isBusy) return;
            BeginStep();
            _session.StepInto();
        }

        private void StepOut_Click(object? sender, RoutedEventArgs e)
        {
            if (!_session.HasProgram || _isBusy) return; // "step out" without a running function makes no sense
            if (_session.ActiveThread is { IsFinished: false } active && active.Thread.CallDepth == 0)
            {
                UpdateStatus("Step Out: the program is at its top level - there is no function to leave.");
                return;
            }
            BeginStep();
            _session.StepOut();
        }

        /// <summary>F8: the same as F5 (continue to the next breakpoint).</summary>
        private void Continue_Click(object? sender, RoutedEventArgs e) => Run_Click(sender, e);

        /// <summary>After an unhandled error stopped the program at its `throw`: ignore it and go on right after the `throw` (like `resume()` in a `catch`).</summary>
        private void IgnoreError_Click(object? sender, RoutedEventArgs e)
        {
            if (_isBusy) return;
            if (!_session.CanIgnoreError || !_session.IgnoreError())
            {
                UpdateStatus("There is no stopped error to ignore.");
                return;
            }
            BeginStep();
            _session.Continue(BreakpointLocations());
        }

        /// <summary>The threads whose current error was shown already (a thread that ignores its error and fails again is shown again).</summary>
        private readonly HashSet<DebugThreadContext> _reportedErrors = new();

        /// <summary>An error that nothing catches (in the main program or in a thread) stops that part of the program: the dialog shows the error and where it happened and offers to go on after it
        /// (like `resume()` in a `catch`) or to stop the program.</summary>
        private async Task ReportRuntimeErrorAsync(DebugThreadContext ctx)
        {
            if (ctx.RuntimeError is not { } error || !_reportedErrors.Add(ctx)) return;
            var text = new System.Text.StringBuilder();
            text.Append(ctx.IsMain ? "The program stopped at an error that nothing catches.\n\n" : $"The thread '{ctx.Name}' stopped at an error that nothing catches.\n\n");
            text.Append(error).Append("\n");
            if (ctx.ErrorTrace is { Count: > 0 } trace)
            {
                text.Append("\nStack (innermost first):\n");
                foreach (var loc in trace) text.Append($"   at {DocumentOfSourceName(loc.SourceIndex)}line {loc.Line}\n");
            }
            bool canContinue = ctx.CanIgnoreError;
            if (canContinue) text.Append("\n'Continue' ignores the error and goes on right after the failing statement.");
            var answer = canContinue
                ? await Dialogs.Ask(this, text.ToString(), "Error", ("Continue", Dialogs.Answer.Yes), ("Stop program", Dialogs.Answer.No), ("Keep stopped", Dialogs.Answer.Cancel))
                : await Dialogs.Ask(this, text.ToString(), "Error", ("Stop program", Dialogs.Answer.No), ("Keep stopped", Dialogs.Answer.Cancel));
            if (answer == Dialogs.Answer.Yes && ctx.IgnoreError())
            {
                _reportedErrors.Remove(ctx);
                if (ReferenceEquals(ctx, _session.ActiveThread)) BeginStep();
                _session.ContinueThread(ctx, BreakpointLocations());
            }
            else if (answer == Dialogs.Answer.No) Stop_Click(this, new RoutedEventArgs());
        }

        private async void RunToEnd_Click(object? sender, RoutedEventArgs e)
        {
            if (BuildRunning()) return;
            if (!_session.HasProgram && UsesNativeEngine()) { _ = StartNativeAsync(NativeFirstAction.RunToEnd); return; }
            if (!_session.HasProgram) await CompileAndPrepareAsync(null);
            if (!_session.HasProgram || _isBusy) return;
            BeginStep();
            _session.RunToCompletion();
        }

        /// <summary>All step requests are FIRE-AND-FORGET (see DebugThreadContext - every VM runs on its OWN background thread so that the UI stays responsive during the
        /// execution) - here only the immediate feedback "it is running"; the actual update comes asynchronously through the ThreadPaused event (see the constructor).</summary>
        private void BeginStep()
        {
            _isBusy = true;
            UpdateStatus("Running...");
        }

        private void Stop_Click(object? sender, RoutedEventArgs e)
        {
            // Shift+F5 while a build runs: cancels it (the program of the last run, if any, stays as it is)
            if (_buildJob != null) { CancelBuild(); return; }
            StopNativeRun();
            _session.Reset();
            _reportedErrors.Clear();
            _isBusy = false;
            UpdateRunButtons();
            ShowDebugLocation(null);
            _debugger.Refresh(BreakpointDescriptions());
            UpdateStatus("Stopped.");
        }

        private void AfterStep(bool more)
        {
            UpdateRunButtons();
            FlushPendingOutput(); // visible at once, not only at the next timer tick
            if (!more)
            {
                if (_session.RuntimeError is { } error)
                {
                    // Stop at the failing line and show the stack trace (the VM has unwound by then, so the frames are not inspectable).
                    var trace = _session.ErrorTrace;
                    ShowDebugLocation(trace is { Count: > 0 } ? trace[0] : null);
                    var text = new System.Text.StringBuilder($"Runtime error: {error}\n");
                    if (trace != null)
                        foreach (var loc in trace)
                            text.Append($"   at {DocumentOfSourceName(loc.SourceIndex)}line {loc.Line}\n");
                    if (_session.CanIgnoreError) text.Append("   (Run > Ignore Error and Continue, Shift+F8, goes on after the throw)\n");
                    _output.Append(text.ToString());
                    string where = trace is { Count: > 0 } ? $" ({DocumentOfSourceName(trace[0].SourceIndex)}line {trace[0].Line})" : "";
                    UpdateStatus($"Runtime error{where}: {error}" + (_session.CanIgnoreError ? "  -  Shift+F8 ignores it and continues" : ""));
                }
                else
                {
                    ShowDebugLocation(null);
                    UpdateStatus("Program finished.");
                }
            }
            else
            {
                var location = _session.ActiveThread!.Thread.CurrentLocation;
                ShowDebugLocation(location);
                string where = DocumentOfSourceName(location.SourceIndex);
                UpdateStatus($"Paused at {where}line {location.Line}.");
            }
            _debugger.Refresh(BreakpointDescriptions());
        }

        /// <summary>"file.script, " for the status bar when the run has several files, else "".</summary>
        private string DocumentOfSourceName(int sourceIndex)
        {
            var files = _session.SourceFiles;
            return sourceIndex >= 0 && sourceIndex < files.Count && files[sourceIndex] is { } path && (_debugIsProject || files.Count(f => f != null) > 1) ? Path.GetFileName(path) + ", " : "";
        }

        private void ToggleBreakpoint_Click(object? sender, RoutedEventArgs e)
        {
            if (ActiveScript is { } script) script.ToggleBreakpointAtCaret();
            else UpdateStatus("Breakpoints are only available in script tabs.");
        }

        /// <summary>The arguments a single script gets when it runs (Environment.Args), by its path (or its tab name while unsaved); a project keeps them in settings.arguments.</summary>
        private readonly Dictionary<string, List<string>> _scriptArguments = new();

        private List<string> ScriptArguments(OpenDocument? doc) =>
            doc != null && _scriptArguments.TryGetValue(FullPathOf(doc) ?? doc.DisplayName, out var args) ? args : new List<string>();

        /// <summary>Run > Program Arguments: what the program gets as Environment.Args - for a project its settings.arguments (saved with the project), for a single script until spark ends.</summary>
        private async void ProgramArguments_Click(object? sender, RoutedEventArgs e)
        {
            var project = ContextProject();
            var doc = ActiveDocument;
            if (project == null && doc == null) { UpdateStatus("No document is open."); return; }
            string current = project != null ? ArgumentText.Join(project.Project.Settings?.Arguments) : ArgumentText.Join(ScriptArguments(doc));
            string what = project != null ? $"the project {project.Name}" : doc!.DisplayName;
            var text = await Dialogs.Input(this, $"The arguments of {what} (separated by blanks, \"...\" around one with blanks):", "Program Arguments", current);
            if (text == null) return;
            var args = ArgumentText.Split(text);
            if (project != null)
            {
                project.Project.Settings ??= new ProjectSettings();
                project.Project.Settings.Arguments = args.Count > 0 ? args : null;
                try
                {
                    _workspace.SaveProject(project);
                    UpdateStatus($"Arguments of {project.Name}: {(args.Count == 0 ? "none" : ArgumentText.Join(args))} (saved in {Path.GetFileName(project.FilePath)})");
                }
                catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { await Dialogs.Message(this, ex.Message, "Program Arguments"); }
            }
            else
            {
                _scriptArguments[FullPathOf(doc) ?? doc!.DisplayName] = args;
                UpdateStatus($"Arguments of {doc!.DisplayName}: {(args.Count == 0 ? "none" : ArgumentText.Join(args))}");
            }
        }

        private void mnuRunDebug_Click(object? sender, RoutedEventArgs e) => SetMode(Runtime.VmExecutionMode.Debug);
        private void mnuRunRelease_Click(object? sender, RoutedEventArgs e) => SetMode(Runtime.VmExecutionMode.Release);
        private void mnuRunPerformance_Click(object? sender, RoutedEventArgs e) => SetMode(Runtime.VmExecutionMode.Performance);

        private void SetMode(Runtime.VmExecutionMode mode)
        {
            _session.ExecutionMode = mode;
            UpdateExecutionModeSelection(_session);
        }
    }
}
