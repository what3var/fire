using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace fire.Editor
{
    /// <summary>Eine Zeile der Thread-Liste.</summary>
    public sealed record ThreadRow(string Marker, string Name, string Status, string Line, string Depth, DebugThreadContext Context);

    /// <summary>Alle Threads der Debug-Sitzung als Liste (aktiver Thread markiert, mit Zustand, aktueller Zeile und
    /// Aufruftiefe). Ein anderer Eintrag macht diesen Thread zum aktiven. Arbeitet auf EINER DebugSession (siehe
    /// AttachSession), kennt sonst nichts vom Editor.</summary>
    public partial class ThreadsPanelControl : UserControl
    {
        private DebugSession? _session;

        /// <summary>Feuert, wenn der Nutzer einen ANDEREN als den bisher aktiven Thread auswählt - das Control hat dabei
        /// bereits DebugSession.SelectThread aufgerufen, der Host reagiert i.d.R. mit Editor-Hervorhebung und
        /// Statusmeldung.</summary>
        public event Action<DebugThreadContext>? ThreadSelected;

        private List<ThreadRow> _rows = new();

        public ThreadsPanelControl()
        {
            InitializeComponent();
        }

        public void AttachSession(DebugSession session) => _session = session;

        /// <summary>Baut die Liste neu auf; `breakpointDescriptions` sind fertig formatierte Einträge für die Anzeige.</summary>
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

        private void ThreadsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThreadsList.SelectedItem is not ThreadRow row || _session == null) return;
            if (ReferenceEquals(row.Context, _session.ActiveThread)) return;

            _session.SelectThread(row.Context);
            ThreadSelected?.Invoke(row.Context);
        }

        private void PauseThread_Click(object sender, RoutedEventArgs e) => _session?.PauseActiveThread();

        /// <summary>Die kleine Symbolleiste braucht den "Überlauf"-Pfeil nicht.</summary>
        private void ToolBar_HideOverflow(object sender, RoutedEventArgs e)
        {
            if (sender is not ToolBar toolBar) return;
            if (toolBar.Template.FindName("OverflowGrid", toolBar) is FrameworkElement overflow)
                overflow.Visibility = Visibility.Collapsed;
            if (toolBar.Template.FindName("MainPanelBorder", toolBar) is FrameworkElement border)
                border.Margin = new Thickness(0);
        }
    }
}
