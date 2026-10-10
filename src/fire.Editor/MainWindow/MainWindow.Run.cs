using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        private bool _isBusy;

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
        private void Run_Click(object? sender, RoutedEventArgs e)
        {
            if (_isBusy) return; // running right now - there is nothing to continue
            if ((_session.Vm == null || _session.IsFinished) && !CompileAndPrepare(null)) return;
            if (_session.Vm == null) return;
            BeginStep();
            _session.Continue(BreakpointLocations());
        }

        /// <summary>Ctrl+F5: ALWAYS compiles again (also in the middle of a session - a running program is ended) and then starts like F5 up to the first breakpoint.</summary>
        private void Restart_Click(object? sender, RoutedEventArgs e)
        {
            if (!CompileAndPrepare(null) || _session.Vm == null) return;
            BeginStep();
            _session.Continue(BreakpointLocations());
        }

        /// <summary>Compiles what the active document belongs to (discards the previous session): the project (all its files and libraries, with the text of the open documents and the settings
        /// of the project) when the document is part of one, else the single script. false on an error or when there is nothing to build.</summary>
        private bool CompileAndPrepare(string? filename)
        {
            var doc = ActiveDocument;
            var project = ContextProject();
            // a library cannot run itself: Start (F5) on one of its files runs the startup project of the solution (Build packs the library)
            if (project?.Project.Type == OutputType.Library && filename == null && _workspace.Startup is { } startup && startup.Project.Type == OutputType.Exe)
            {
                project = startup;
                UpdateStatus($"{ContextProject()!.Name} is a library: running the startup project {startup.Name}.");
            }
            BuildPlan? plan = null;
            string[] sources;
            string? baseDirectory = null;

            if (project != null)
            {
                plan = CreatePlan(project);
                if (!plan.IsValid)
                {
                    string errors = string.Join(Environment.NewLine, plan.Errors);
                    UpdateStatus($"The project {project.Name} has problems: {plan.Errors[0]}");
                    _ = Dialogs.Message(this, errors, "Project problems");
                    return false;
                }
                if (plan.Type == OutputType.Library && filename == null)
                {
                    UpdateStatus($"{project.Name} is a library: it has no entry point to run (Project > Pack Library, or run a program that references it).");
                    return false;
                }
                sources = plan.SourceTexts.ToArray();
            }
            else
            {
                if (doc?.Script is not { } script)
                {
                    UpdateStatus(doc == null ? "No document is open." : "The active document is not a script - select a script tab to run.");
                    return false;
                }
                sources = new[] { script.GetText() };
                baseDirectory = script.BaseDirectory;
            }

            _output.Clear();
            while (_pendingOutput.TryDequeue(out _)) { } // discard what is left of a previous run that has not flowed off yet
            ShowDebugLocation(null);
            _debugIsProject = project != null;
            _debugDocument = project != null ? null : doc;
            _isBusy = false;
            _session.UpdateBreakpoints(BreakpointLocations());

            if (!_session.Compile(sources, filename, baseDirectory, plan))
            {
                UpdateStatus($"Compile error: {_session.CompileError}");
                _ = Dialogs.Message(this, _session.CompileError, "Compile error");
                return false;
            }

            _session.UpdateBreakpoints(BreakpointLocations());   // now with the source indexes of this program
            UpdateStatus(project != null ? $"Compiled {project.Name} ({plan!.Sources.Count} file{(plan.Sources.Count == 1 ? "" : "s")}) - ready to step, continue or run to the end." : "Compiled - ready to step, continue or run to the end.");
            _debugger.Refresh(BreakpointDescriptions());
            return true;
        }

        private void Step_Click(object? sender, RoutedEventArgs e)
        {
            if (_session.Vm == null) CompileAndPrepare(null);
            if (_session.Vm == null || _isBusy) return;
            BeginStep();
            _session.StepLine();
        }

        private void StepInto_Click(object? sender, RoutedEventArgs e)
        {
            if (_session.Vm == null) CompileAndPrepare(null);
            if (_session.Vm == null || _isBusy) return;
            BeginStep();
            _session.StepInto();
        }

        private void StepOut_Click(object? sender, RoutedEventArgs e)
        {
            if (_session.Vm == null || _isBusy) return; // "step out" without a running function makes no sense
            BeginStep();
            _session.StepOut();
        }

        /// <summary>F8: the same as F5 (continue to the next breakpoint).</summary>
        private void Continue_Click(object? sender, RoutedEventArgs e) => Run_Click(sender, e);

        private void RunToEnd_Click(object? sender, RoutedEventArgs e)
        {
            if (_session.Vm == null) CompileAndPrepare(null);
            if (_session.Vm == null || _isBusy) return;
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
            _session.Reset();
            _isBusy = false;
            ShowDebugLocation(null);
            _debugger.Refresh(BreakpointDescriptions());
            UpdateStatus("Stopped.");
        }

        private void AfterStep(bool more)
        {
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
                    _output.Append(text.ToString());
                    string where = trace is { Count: > 0 } ? $" ({DocumentOfSourceName(trace[0].SourceIndex)}line {trace[0].Line})" : "";
                    UpdateStatus($"Runtime error{where}: {error}");
                }
                else
                {
                    ShowDebugLocation(null);
                    UpdateStatus("Program finished.");
                }
            }
            else
            {
                var location = _session.Vm!.CurrentLocation;
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
