using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

        // The script that is compiled/paused in the debugger right now (stays so even when the tab is changed).
        private OpenDocument? _debugDocument;

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

        /// <summary>The breakpoints of the script `doc` are mere line numbers, linked here with the currently valid source index (see DebugSession.FirstUserSourceIndex) before they go
        /// to the file-aware DebugSession API. Before the first successful Compile() FirstUserSourceIndex is still 0 - 1 is assumed then (the prelude is always at 0, the first
        /// own source normally at 1). There is always only ONE script as the source, so this one index is enough.</summary>
        private HashSet<(int SourceIndex, int Line)> BreakpointLocations(OpenDocument? doc) =>
            (doc?.Script?.Breakpoints ?? (IReadOnlySet<int>)new HashSet<int>())
                .Select(l => (_session.FirstUserSourceIndex == 0 ? 1 : _session.FirstUserSourceIndex, l))
                .ToHashSet();

        /// <summary>For the debugger panels: the (numerically sorted) breakpoint lines of the script in the debugger, else of the active script.</summary>
        private List<string> BreakpointDescriptions() =>
            (_debugDocument ?? ActiveDocument)?.Script?.Breakpoints.OrderBy(l => l).Select(l => l.ToString()).ToList()
            ?? new List<string>();

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

        /// <summary>Shows the paused line in the tab of the script that is in the debugger (and brings the tab to the front); null = remove the highlight.</summary>
        private void ShowDebugLine(int? line)
        {
            var script = _debugDocument?.Script;
            if (script == null) return;
            script.HighlightedLine = line;
            if (line != null)
            {
                Activate(_debugDocument!);
                script.ScrollToLine(line.Value);
            }
        }

        /// <summary>F5 like in Visual Studio: compiles if necessary (nothing compiled yet, stopped or the program has ended) and then runs to the next breakpoint. Every further F5
        /// continues the execution to the next breakpoint; when the program has ended, F5 starts it again.</summary>
        private void Run_Click(object? sender, RoutedEventArgs e)
        {
            if (_isBusy) return; // running right now - there is nothing to continue
            if ((_session.Vm == null || _session.IsFinished) && !CompileAndPrepare(null)) return;
            if (_session.Vm == null) return;
            BeginStep();
            _session.Continue(BreakpointLocations(_debugDocument));
        }

        /// <summary>Ctrl+F5: ALWAYS compiles again (also in the middle of a session - a running program is ended) and then starts like F5 up to the first breakpoint.</summary>
        private void Restart_Click(object? sender, RoutedEventArgs e)
        {
            if (!CompileAndPrepare(null) || _session.Vm == null) return;
            BeginStep();
            _session.Continue(BreakpointLocations(_debugDocument));
        }

        /// <summary>Compiles the active script (discards the previous session). false on an error or when no script is active.</summary>
        private bool CompileAndPrepare(string? filename)
        {
            // Only the ACTIVE document is compiled (several files: include them with #include).
            var doc = ActiveDocument;
            if (doc?.Script is not { } script)
            {
                UpdateStatus(doc == null ? "No document is open." : "The active document is not a script - select a script tab to run.");
                return false;
            }

            _output.Clear();
            while (_pendingOutput.TryDequeue(out _)) { } // discard what is left of a previous run that has not flowed off yet
            ShowDebugLine(null);
            _debugDocument = doc;
            _isBusy = false;
            string source = script.GetText();
            _session.UpdateBreakpoints(BreakpointLocations(doc));

            if (!_session.Compile(new[] { source }, filename, script.BaseDirectory))
            {
                UpdateStatus($"Compile error: {_session.CompileError}");
                _ = Dialogs.Message(this, _session.CompileError, "Compile error");
                return false;
            }

            UpdateStatus("Compiled - ready to step, continue or run to the end.");
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
            ShowDebugLine(null);
            _debugger.Refresh(BreakpointDescriptions());
            UpdateStatus("Stopped.");
        }

        private void AfterStep(bool more)
        {
            FlushPendingOutput(); // visible at once, not only at the next timer tick
            if (!more)
            {
                ShowDebugLine(null);
                UpdateStatus(_session.RuntimeError != null
                    ? $"Runtime error: {_session.RuntimeError}"
                    : "Program finished.");
            }
            else
            {
                int line = _session.Vm!.CurrentLine;
                ShowDebugLine(line);
                UpdateStatus($"Paused at line {line}.");
            }
            _debugger.Refresh(BreakpointDescriptions());
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
