using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace fire.Editor
{
    /// <summary>A row of the thread list.</summary>
    public sealed record ThreadRow(string Marker, string Name, string Status, string Line, string Depth, DebugThreadContext Context);

    /// <summary>All threads of the debug session as a list (active thread marked, with state, current line and
    /// call depth). Another entry makes that thread the active one. Works on ONE DebugSession (see
    /// AttachSession), otherwise knows nothing about the editor.</summary>
    public partial class ThreadsPanelControl : UserControl
    {
        private DebugSession? _session;

        /// <summary>Fires when the user selects a thread OTHER than the previously active one - the control has already
        /// called DebugSession.SelectThread, the host usually reacts with editor highlighting and
        /// a status message.</summary>
        public event Action<DebugThreadContext>? ThreadSelected;

        private List<ThreadRow> _rows = new();

        public ThreadsPanelControl()
        {
            InitializeComponent();
        }

        public void AttachSession(DebugSession session) => _session = session;

        /// <summary>Rebuilds the list; `breakpointDescriptions` are ready-formatted entries for display.</summary>
        public void Refresh(IEnumerable<string> breakpointDescriptions)
        {
            var descriptions = breakpointDescriptions.ToList();
            BreakpointsText.Text = descriptions.Count == 0
                ? "Breakpoints: (none - press F9 on the cursor line)"
                : "Breakpoints: " + string.Join(", ", descriptions);

            var active = _session?.ActiveThread;
            _rows = (_session?.Threads.ToList() ?? new List<DebugThreadContext>()).Select(t =>
            {
                string status = t.RuntimeError != null ? $"Error: {t.RuntimeError}"
                    : t.IsFinished ? "finished"
                    : t.IsMain ? "paused" : "running/paused";
                string line = t.IsFinished ? "-" : t.Vm.CurrentLine.ToString();
                string depth = t.IsFinished ? "-" : t.Vm.DebugCallDepth.ToString();
                return new ThreadRow(ReferenceEquals(t, active) ? "\u25B6" : "", t.Name, status, line, depth, t);
            }).ToList();

            ThreadsList.ItemsSource = _rows;
            int activeIndex = active != null ? _rows.FindIndex(r => ReferenceEquals(r.Context, active)) : -1;
            if (activeIndex >= 0) ThreadsList.SelectedIndex = activeIndex;
        }

        private void ThreadsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ThreadsList.SelectedItem is not ThreadRow row || _session == null) return;
            if (ReferenceEquals(row.Context, _session.ActiveThread)) return;

            _session.SelectThread(row.Context);
            ThreadSelected?.Invoke(row.Context);
        }

        private void PauseThread_Click(object? sender, RoutedEventArgs e) => _session?.PauseActiveThread();
    }
}
