using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using fire.Editor.Setup;

namespace fire.Editor
{
    /// <summary>
    /// The progress of the setup after the wizard (<see cref="SetupRun"/>): one row per step with its state, phase, bytes and speed (downloads), the SHA-256 check, the
    /// unpacking of a toolchain, the packages of the first build; Retry on a row that failed or was cancelled; the log of the steps below. Run in background (Escape, the
    /// close box while it runs) hides it - the status bar goes on, a click there brings it back; Cancel stops the step that runs and the ones that wait.
    /// </summary>
    internal sealed class SetupProgressWindow : Window
    {
        private readonly SetupRun _run;
        private readonly ProgressBar _overall = new() { Height = 8, Minimum = 0, Maximum = 1 };
        private readonly TextBlock _headline = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };
        private readonly StackPanel _rows = new() { Spacing = 10 };
        private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Cascadia Mono,Consolas,DejaVu Sans Mono,monospace"), FontSize = 11, Height = 130 };
        private readonly Button _background = new() { Content = "Run in _background", HorizontalContentAlignment = HorizontalAlignment.Center };
        private readonly Button _cancel = new() { Content = "_Cancel", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        private readonly Button _retry = new() { Content = "_Retry failed", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        private readonly Button _close = new() { Content = "Close", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        private readonly List<Row> _rowViews = new();
        private readonly DispatcherTimer _timer;
        private int _logVersion = -1;
        private bool _forGood;

        /// <summary>Run in background (or Escape, or the close box while it runs).</summary>
        public event Action? HideRequested;

        private sealed class Row
        {
            public required SetupItem Item;
            public required TextBlock State, Title, Status;
            public required ProgressBar Bar;
            public required Button Retry;
            public int Version = -1;
        }

        public SetupProgressWindow(SetupRun run)
        {
            _run = run;
            Title = "spark setup";
            Width = 680;
            SizeToContent = SizeToContent.Height;
            MinWidth = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = EditorTheme.Background;

            foreach (var item in run.Items) _rows.Children.Add(MakeRow(item));

            _background.Click += (_, _) => HideRequested?.Invoke();
            _cancel.Click += (_, _) => { _run.Cancel(); Refresh(); };
            _retry.Click += (_, _) => { _run.Retry(); Refresh(); };
            _close.Click += (_, _) => CloseForGood();
            ToolTip.SetTip(_background, "The setup goes on; the status bar shows it - a click there brings this window back");
            ToolTip.SetTip(_cancel, "Stops the step that runs and the ones that wait (a download goes on from where it stopped at a retry)");
            KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; if (_run.Running) HideRequested?.Invoke(); else CloseForGood(); } };
            Closing += (_, e) =>
            {
                if (_forGood) return;
                // the close box while it runs: to the background; afterwards: closed
                if (_run.Running) { e.Cancel = true; HideRequested?.Invoke(); }
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _background, _retry, _cancel, _close } };
            var panel = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
            panel.Children.Add(_headline);
            panel.Children.Add(_overall);
            panel.Children.Add(new Border { Height = 4 });
            panel.Children.Add(_rows);
            panel.Children.Add(new TextBlock { Text = "Log", Foreground = EditorTheme.TextDim, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(new Border { BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(1), Child = _log });
            panel.Children.Add(buttons);
            Content = panel;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _timer.Tick += (_, _) => Refresh();
            Opened += (_, _) => { _timer.Start(); Refresh(); };
            Closed += (_, _) => _timer.Stop();
            Refresh();
        }

        private Control MakeRow(SetupItem item)
        {
            var row = new Row
            {
                Item = item,
                State = new TextBlock { Width = 22, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Top },
                Title = new TextBlock { FontWeight = FontWeight.SemiBold, Text = item.Title },
                Status = new TextBlock { Foreground = EditorTheme.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                Bar = new ProgressBar { Height = 6, MinHeight = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 4, 0, 0) },
                Retry = new Button { Content = "Retry", IsVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) },
            };
            row.Retry.Click += (_, _) => { _run.Retry(item); Refresh(); };
            ToolTip.SetTip(row.Retry, "Tries this step again (a download goes on from where it stopped)");
            _rowViews.Add(row);
            var text = new StackPanel { Children = { row.Title, row.Status, row.Bar } };
            var dock = new DockPanel();
            DockPanel.SetDock(row.State, Avalonia.Controls.Dock.Left);
            DockPanel.SetDock(row.Retry, Avalonia.Controls.Dock.Right);
            dock.Children.Add(row.State);
            dock.Children.Add(row.Retry);
            dock.Children.Add(text);
            return dock;
        }

        /// <summary>The rows, the buttons and the log as the run is now (the timer; also right after a button).</summary>
        public void Refresh()
        {
            bool running = _run.Running;
            foreach (var row in _rowViews)
            {
                var item = row.Item;
                int version = item.Version;
                // (the speed changes without a new version: a running row is redrawn each time)
                if (version == row.Version && item.State != SetupItemState.Running) continue;
                row.Version = version;
                var state = item.State;
                (row.State.Text, row.State.Foreground) = state switch
                {
                    SetupItemState.Done => ("✓", EditorTheme.Magenta),
                    SetupItemState.Failed => ("✗", EditorTheme.ErrorMark),
                    SetupItemState.Cancelled => ("–", EditorTheme.Orange),
                    SetupItemState.Running => ("▶", EditorTheme.Yellow),
                    _ => ("•", EditorTheme.TextDim),
                };
                row.Status.Text = item.StatusText;
                row.Status.Foreground = state == SetupItemState.Failed ? EditorTheme.ErrorMark : EditorTheme.TextDim;
                double? f = item.Fraction;
                row.Bar.IsVisible = state is SetupItemState.Running or SetupItemState.Done;
                row.Bar.IsIndeterminate = state == SetupItemState.Running && f == null;
                row.Bar.Value = f ?? 0;
                row.Retry.IsVisible = !running && state is SetupItemState.Failed or SetupItemState.Cancelled;
            }
            _overall.Value = _run.Fraction;
            _headline.Text = running ? _run.Summary : _run.Succeeded ? "The setup is done." : _run.Summary + ".";
            _background.IsVisible = running;
            _cancel.IsVisible = running;
            _retry.IsVisible = _run.CanRetry;
            _close.IsVisible = !running;

            int logVersion = _run.Items.Sum(i => i.Log.Count);
            if (logVersion != _logVersion)
            {
                _logVersion = logVersion;
                _log.Text = string.Join(Environment.NewLine, _run.Items.SelectMany(i => i.Log.Select(l => $"[{i.Title}] {l}")));
                _log.CaretIndex = _log.Text?.Length ?? 0;
            }
        }

        public void CloseForGood()
        {
            _forGood = true;
            Close();
        }
    }
}
