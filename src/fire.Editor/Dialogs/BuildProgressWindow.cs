using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using fire.Native;

namespace fire.Editor
{
    /// <summary>
    /// The window of a build that takes a while (docs: the help page of spark, "Builds"): what is built, the phase with its progress (a count where the build has one - the library
    /// modules that are compiled side by side, the steps of ESP-IDF - else indeterminate), the log of the commands and what they said as they run, and two buttons:
    /// Hide (Escape; the build goes on, the status bar shows it, a click there brings the window back) and Cancel (Ctrl+Break, Shift+F5). After a failure it stays open with the
    /// error and the buttons become Close. spark's <see cref="MainWindow"/> owns the build; the window only shows it.
    /// </summary>
    internal sealed class BuildProgressWindow : Window
    {
        private readonly TextBlock _phase = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _detail = new() { Foreground = EditorTheme.TextDim, FontSize = 12 };
        private readonly ProgressBar _bar = new() { Height = 14, Minimum = 0, Maximum = 1, IsIndeterminate = true };
        private readonly TextBlock _error = new() { Foreground = EditorTheme.ErrorMark, TextWrapping = TextWrapping.Wrap, IsVisible = false, MaxHeight = 120 };
        private readonly BuildLogPanelControl _log = new() { Height = 280 };
        private readonly Button _hide = new() { Content = "_Hide", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        private readonly Button _cancel = new() { Content = "_Cancel", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        private bool _ended;
        private bool _closing;

        /// <summary>Hide, Escape or the close box of the window while the build runs: the build goes on.</summary>
        public event Action? HideRequested;
        /// <summary>Cancel (Ctrl+Break, Shift+F5) while the build runs.</summary>
        public event Action? CancelRequested;
        /// <summary>Close after the build ended (failed): the window goes away.</summary>
        public event Action? Dismissed;

        public BuildProgressWindow(string what)
        {
            Title = $"Building {what}";
            Width = 720;
            SizeToContent = SizeToContent.Height;
            MinWidth = 420;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            CanResize = true;
            Background = EditorTheme.Background;

            _phase.Text = "Starting...";
            _hide.Click += (_, _) => HideRequested?.Invoke();
            _cancel.Click += (_, _) => { if (_ended) Dismiss(); else CancelRequested?.Invoke(); };
            ToolTip.SetTip(_hide, "The build goes on; the status bar shows it - a click there brings this window back (Escape)");
            ToolTip.SetTip(_cancel, "Stops the build: the compilers are ended, nothing half-built is kept (Ctrl+Break, Shift+F5)");

            var logBorder = new Border { BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(1), Child = _log };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { _hide, _cancel } };
            var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = what, Foreground = EditorTheme.TextDim, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(_phase);
            panel.Children.Add(_bar);
            panel.Children.Add(_detail);
            panel.Children.Add(logBorder);
            panel.Children.Add(_error);
            panel.Children.Add(buttons);
            Content = panel;

            AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            Closing += (_, e) =>
            {
                if (_closing) return;
                // the close box: like Hide while the build runs; after it ended, and when spark itself closes (it cancels the build first), the window goes
                if (_ended || e.CloseReason != WindowCloseReason.WindowClosing)
                {
                    _closing = true;
                    Dismissed?.Invoke();
                    return;
                }
                e.Cancel = true;
                HideRequested?.Invoke();
            };
        }

        /// <summary>Is the key the shortcut that cancels a build (Ctrl+Break - also as Ctrl+Pause or the Cancel key it produces on some systems)?</summary>
        public static bool IsCancelGesture(KeyEventArgs e) =>
            e.Key == Key.Cancel || (e.Key == Key.Pause && (e.KeyModifiers & KeyModifiers.Control) != 0);

        private void OnKey(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_ended) Dismiss(); else HideRequested?.Invoke();
                e.Handled = true;
            }
            else if (!_ended && (IsCancelGesture(e) || (e.Key == Key.F5 && (e.KeyModifiers & KeyModifiers.Shift) != 0)))
            {
                CancelRequested?.Invoke();
                e.Handled = true;
            }
        }

        /// <summary>The phase and its progress, and how long the build runs.</summary>
        public void ShowProgress(BuildProgress? progress, TimeSpan elapsed, bool cancelling)
        {
            if (_ended) return;
            if (cancelling)
            {
                _phase.Text = "Cancelling...";
                _bar.IsIndeterminate = true;
            }
            else if (progress is { } p)
            {
                _phase.Text = p.Text;
                _bar.IsIndeterminate = p.Indeterminate;
                if (!p.Indeterminate)
                {
                    _bar.Maximum = p.Total;
                    _bar.Value = p.Done;
                }
            }
            _detail.Text = $"{PhaseName(progress?.Phase)}{elapsed.TotalSeconds:0} s";
            _cancel.IsEnabled = !cancelling;
        }

        private static string PhaseName(BuildPhase? phase) => phase switch
        {
            BuildPhase.CompileFire => "Phase: compile fire  ·  ",
            BuildPhase.GenerateC => "Phase: generate C  ·  ",
            BuildPhase.CompileC => "Phase: compile C  ·  ",
            BuildPhase.Link => "Phase: link  ·  ",
            BuildPhase.Flash => "Phase: build and flash the board  ·  ",
            BuildPhase.Pack => "Phase: pack  ·  ",
            _ => "",
        };

        public void AppendLog(IEnumerable<string> lines)
        {
            var text = new System.Text.StringBuilder();
            foreach (var line in lines) text.Append(line).Append(Environment.NewLine);
            _log.Append(text.ToString());
        }

        /// <summary>The build failed: the error under the log, the bar stops, the buttons become Close.</summary>
        public void ShowFailure(string message)
        {
            _ended = true;
            _phase.Text = "The build failed.";
            _bar.IsIndeterminate = false;
            _bar.Maximum = 1;
            _bar.Value = 0;
            _error.Text = message;
            _error.IsVisible = message.Length > 0;
            _hide.IsVisible = false;
            _cancel.Content = "_Close";
            _cancel.IsEnabled = true;
            ToolTip.SetTip(_cancel, null);
            Title = Title + " - failed";
            _cancel.Focus();
        }

        /// <summary>Closes the window for good (the build ended).</summary>
        public void CloseForGood()
        {
            _closing = true;
            Close();
        }

        private void Dismiss()
        {
            Dismissed?.Invoke();
            CloseForGood();
        }
    }
}
