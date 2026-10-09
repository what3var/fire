using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using fire.Projects;

namespace fire.Editor
{
    /// <summary>What the user picked in the <see cref="WelcomeWindow"/>.</summary>
    internal enum WelcomeAction { None, NewSolution, NewProject, OpenWorkspace, OpenFile, NewScript, OpenRecent, FirstSteps }

    /// <summary>
    /// The start window: the projects and solutions that were opened last (a click opens one; the cross takes it out of the list) on the left, on the right what to do: make a new solution or
    /// project, open one, open a file, a new script, the first steps. The result is in <see cref="Action"/> (and <see cref="Path"/> for a recent one); <see cref="ShowAtStartup"/> is the check box.
    /// </summary>
    internal sealed class WelcomeWindow : Window
    {
        private readonly RecentWorkspaces _recent;
        private readonly StackPanel _recentList = new() { Spacing = 2 };

        public WelcomeAction Action { get; private set; } = WelcomeAction.None;
        public string? Path { get; private set; }
        public bool ShowAtStartup => _show.IsChecked == true;
        private readonly CheckBox _show = new() { Content = "Show this window when the editor starts" };

        public WelcomeWindow(RecentWorkspaces recent, bool showAtStartup)
        {
            _recent = recent;
            _show.IsChecked = showAtStartup;
            Title = "Welcome to spark";
            Width = 900;
            Height = 580;
            MinWidth = 700;
            MinHeight = 440;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            // left: the recent projects
            var recentHeader = new TextBlock { Text = "Open recent", FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
            var left = new DockPanel { Margin = new Thickness(0, 0, 18, 0) };
            DockPanel.SetDock(recentHeader, Avalonia.Controls.Dock.Top);
            left.Children.Add(recentHeader);
            left.Children.Add(new ScrollViewer { Content = _recentList, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            FillRecent();

            // right: what to do
            var right = new StackPanel { Spacing = 6 };
            right.Children.Add(new TextBlock { Text = "Get started", FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
            right.Children.Add(Card("solution", "Create a new solution", "A solution with a project made from a template", WelcomeAction.NewSolution));
            right.Children.Add(Card("project", "Create a new project", "Added to the open solution, or on its own", WelcomeAction.NewProject));
            right.Children.Add(Card("open", "Open a project or solution", "Browse for a .fireproj or .firesln", WelcomeAction.OpenWorkspace));
            right.Children.Add(Card("file", "Open a file", "A script, Markdown, a picture - any file", WelcomeAction.OpenFile));
            right.Children.Add(Card("script", "New script", "Start with an empty file", WelcomeAction.NewScript));
            right.Children.Add(Card("help", "First steps", "A short tour of the editor and the language", WelcomeAction.FirstSteps));

            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,360") };
            columns.Children.Add(left);
            Grid.SetColumn(right, 1);
            columns.Children.Add(right);

            var close = new Button { Content = "Continue without code", HorizontalAlignment = HorizontalAlignment.Right };
            close.Click += (_, _) => Close(false);
            var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
            DockPanel.SetDock(close, Avalonia.Controls.Dock.Right);
            footer.Children.Add(close);
            footer.Children.Add(_show);

            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 0, 0, 16) };
            title.Children.Add(new TextBlock { Text = "spark", FontSize = 30, FontWeight = FontWeight.SemiBold, Foreground = EditorTheme.Magenta });
            title.Children.Add(new TextBlock { Text = "the editor of the fire language", FontSize = 14, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 5) });

            var root = new DockPanel { Margin = new Thickness(24) };
            DockPanel.SetDock(title, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(footer, Avalonia.Controls.Dock.Bottom);
            root.Children.Add(title);
            root.Children.Add(footer);
            root.Children.Add(columns);
            Content = root;
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(false); };
        }

        /// <summary>A big button: the symbol, the title and a line below it.</summary>
        private Control Card(string icon, string title, string description, WelcomeAction action)
        {
            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold });
            text.Children.Add(new TextBlock { Text = description, Foreground = EditorTheme.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { TemplateIcon.Create(icon, 32), text }, Margin = new Thickness(2, 4) };
            var button = new Button { Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent };
            button.Click += (_, _) => { Action = action; Close(true); };
            return button;
        }

        private void FillRecent()
        {
            _recentList.Children.Clear();
            if (_recent.Entries.Count == 0)
            {
                _recentList.Children.Add(new TextBlock { Text = "Nothing opened yet. Make a new solution or open one on the right: it will be listed here.", Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap });
                return;
            }
            foreach (var entry in _recent.Entries) _recentList.Children.Add(Row(entry));
        }

        private Control Row(RecentEntry entry)
        {
            var dim = entry.Exists ? EditorTheme.TextDim : EditorTheme.ErrorMark;
            var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = entry.Name + (entry.IsSolution ? "  (solution)" : ""), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = entry.Exists ? EditorTheme.Text : EditorTheme.TextDim });
            text.Children.Add(new TextBlock { Text = entry.Exists ? entry.Folder : entry.Folder + "  - not found", Foreground = dim, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            var date = new TextBlock { Text = When(entry.LastOpened), Foreground = EditorTheme.TextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
            var remove = new Button { Content = "×", Padding = new Thickness(7, 0), Background = Brushes.Transparent, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(remove, "Take it out of this list");
            remove.Click += (_, e) => { _recent.Remove(entry.Path); FillRecent(); e.Handled = true; };

            var open = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            var inner = new DockPanel { Margin = new Thickness(2, 3) };
            var icon = TemplateIcon.Create(entry.IsSolution ? "solution" : "project", 28);
            DockPanel.SetDock(icon, Avalonia.Controls.Dock.Left);
            DockPanel.SetDock(date, Avalonia.Controls.Dock.Right);
            inner.Children.Add(icon);
            inner.Children.Add(date);
            inner.Children.Add(text);
            open.Content = inner;
            ToolTip.SetTip(open, entry.Path);
            open.Click += (_, _) => { Action = WelcomeAction.OpenRecent; Path = entry.Path; Close(true); };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(open);
            Grid.SetColumn(remove, 1);
            grid.Children.Add(remove);
            return grid;
        }

        /// <summary>"Today", "Yesterday", "3 days ago", else the date.</summary>
        internal static string When(DateTime time, DateTime? now = null)
        {
            var today = (now ?? DateTime.Now).Date;
            int days = (int)(today - time.Date).TotalDays;
            return days switch { <= 0 => "Today", 1 => "Yesterday", < 7 => $"{days} days ago", _ => time.ToString("yyyy-MM-dd") };
        }
    }
}
