using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using fire.Git;

namespace fire.Editor
{
    /// <summary>
    /// The Git panel (docs/tools/git.md), a tab next to the solution explorer: the branch and how far it is from the server's (↑ to push, ↓ to pull), Fetch / Pull / Push, the message and Commit
    /// (and Commit &amp; Push), and the changed files in three lists - staged, changes, untracked - with their letter. A file is staged with +, unstaged with −, its changes are thrown away with ↶
    /// (the window asks first); a click shows its changes, a double click opens it. The window reads the repository and does the commands (MainWindow.GitPanel.cs) - the control only shows
    /// a <see cref="GitOverview"/> and asks for them.
    /// </summary>
    internal sealed class GitPanelControl : UserControl
    {
        private readonly TextBlock _branch = new() { FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        private readonly TextBlock _aheadBehind = new() { Foreground = EditorTheme.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        private readonly TextBox _message = new()
        {
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 54, MaxHeight = 140, FontSize = 12,
            Watermark = "Message (Ctrl+Enter commits)",
        };
        private readonly Button _commit, _commitPush, _fetch, _pull, _push, _refresh;
        private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        private readonly StackPanel _lists = new() { Spacing = 2, Margin = new Thickness(0, 4, 14, 4) };   // (room on the right for the scroll bar, which lies over the content)
        private readonly Grid _content;
        private readonly StackPanel _empty;
        private readonly TextBlock _emptyText = new() { TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.TextDim };
        private readonly Button _init;
        private GitOverview? _overview;
        private string? _selected;   // the full path of the selected row (with its list: "s|" or "w|")
        private bool _busy;

        public event Action? FetchRequested, PullRequested, PushRequested, RefreshRequested, InitRequested;
        public event Action<string, bool>? CommitRequested;
        public event Action<IReadOnlyList<GitOverviewEntry>>? StageRequested, UnstageRequested, DiscardRequested;
        /// <summary>A row was clicked: show its changes.</summary>
        public event Action<GitOverviewEntry>? DiffRequested;
        /// <summary>A row was double clicked: open the file.</summary>
        public event Action<GitOverviewEntry>? OpenRequested;

        public GitPanelControl()
        {
            _fetch = ToolButton("⇣ Fetch", "Get the server's commits without joining them", () => FetchRequested?.Invoke());
            _pull = ToolButton("↓ Pull", "Fetch and join the server's changes into this branch", () => PullRequested?.Invoke());
            _push = ToolButton("↑ Push", "Send the commits of this branch to the server", () => PushRequested?.Invoke());
            _refresh = ToolButton("⟳", "Read the state of the repository again", () => RefreshRequested?.Invoke());

            _commit = new Button { Content = "Commit", HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
            _commit.Classes.Add("accent");
            ToolTip.SetTip(_commit, "Commit what is staged (nothing staged: you are asked whether all changes go in)");
            _commit.Click += (_, _) => Commit(false);
            _commitPush = new Button { Content = "Commit & Push", HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(_commitPush, "Commit, then send it to the server");
            _commitPush.Click += (_, _) => Commit(true);
            _message.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { Commit(false); e.Handled = true; }
            };

            var branchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var branchIcon = new TextBlock { Text = "⎇", FontSize = 14, Foreground = EditorTheme.Purple, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            DockPanel.SetDock(branchIcon, Avalonia.Controls.Dock.Left);
            DockPanel.SetDock(_aheadBehind, Avalonia.Controls.Dock.Right);
            branchRow.Children.Add(branchIcon);
            branchRow.Children.Add(_aheadBehind);
            branchRow.Children.Add(_branch);

            var bar = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var b in new[] { _fetch, _pull, _push, _refresh }) { b.Margin = new Thickness(0, 0, 2, 2); bar.Children.Add(b); }

            var commitRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,6,*"), Margin = new Thickness(0, 4, 0, 0) };
            commitRow.Children.Add(_commit);
            Grid.SetColumn(_commitPush, 2);
            commitRow.Children.Add(_commitPush);

            var top = new StackPanel { Margin = new Thickness(6, 4, 6, 6) };
            top.Children.Add(branchRow);
            top.Children.Add(bar);
            top.Children.Add(_message);
            top.Children.Add(commitRow);
            top.Children.Add(_notice);
            var topBorder = new Border { BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = top };

            var scroll = new ScrollViewer { Content = _lists, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            _content = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            _content.Children.Add(topBorder);
            Grid.SetRow(scroll, 1);
            _content.Children.Add(scroll);

            _init = new Button { Content = "Create Repository...", HorizontalAlignment = HorizontalAlignment.Left };
            _init.Click += (_, _) => InitRequested?.Invoke();
            _empty = new StackPanel { Margin = new Thickness(12), Spacing = 8, VerticalAlignment = VerticalAlignment.Top, Children = { _emptyText, _init } };

            Content = new Grid { Children = { _content, _empty } };
            ShowNone("Open a solution, a project or a file in a git repository to see its changes here.", canInit: false);
        }

        private static Button ToolButton(string text, string tip, Action click)
        {
            var b = new Button { Content = new TextBlock { Text = text, FontSize = 12 }, Padding = new Thickness(6, 2), MinWidth = 26 };
            b.Classes.Add("tool");
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }

        // ---- what is shown ------------------------------------------------------------------------------------------------------------------

        /// <summary>The message the user typed (kept across refreshes).</summary>
        public string Message { get => _message.Text ?? ""; set => _message.Text = value; }

        /// <summary>The overview in the panel (null: none shown).</summary>
        public GitOverview? Overview => _overview;

        /// <summary>No repository: a text, and the button that makes one when there is something to make it for.</summary>
        public void ShowNone(string text, bool canInit)
        {
            _overview = null;
            _emptyText.Text = text;
            _init.IsVisible = canInit;
            _empty.IsVisible = true;
            _content.IsVisible = false;
        }

        /// <summary>A command runs (Pull, Push, ...): the buttons are grey until it is done.</summary>
        public void SetBusy(bool busy)
        {
            _busy = busy;
            foreach (var b in new[] { _commit, _commitPush, _fetch, _pull, _push }) b.IsEnabled = !busy;
            UpdateButtons();
        }

        /// <summary>A line under the commit buttons (an error in red, else dim); null hides it.</summary>
        public void ShowNotice(string? text, bool error = false)
        {
            _notice.Text = text ?? "";
            _notice.IsVisible = !string.IsNullOrEmpty(text);
            _notice.Foreground = error ? EditorTheme.ErrorMark : EditorTheme.TextDim;
        }

        public void Show(GitOverview overview)
        {
            _overview = overview;
            _empty.IsVisible = false;
            _content.IsVisible = true;
            _branch.Text = overview.Branch;
            _aheadBehind.Text = overview.AheadBehindText;
            ToolTip.SetTip(_aheadBehind, $"{overview.Ahead} commit(s) to push, {overview.Behind} to pull (as of the last fetch)");
            _lists.Children.Clear();
            if (overview.IsMerging)
                _lists.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x40, 0xE8, 0x6A, 0x6A)), Padding = new Thickness(8, 4), Margin = new Thickness(4, 0, 4, 4), CornerRadius = new CornerRadius(3),
                    Child = new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap, FontSize = 12,
                        Text = overview.HasConflicts ? "A merge is not finished: solve the conflicts (!), stage the files and commit." : "A merge is not finished: commit to finish it.",
                    },
                });
            AddSection("Staged Changes", overview.Staged, staged: true);
            AddSection("Changes", overview.Changes, staged: false);
            AddSection("Untracked", overview.Untracked, staged: false);
            if (overview.Count == 0)
                _lists.Children.Add(new TextBlock { Text = "No changes since the last commit.", Foreground = EditorTheme.TextDim, Margin = new Thickness(10, 6), TextWrapping = TextWrapping.Wrap });
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            if (_overview == null) return;
            bool canCommit = !_busy && (_overview.Count > 0 || _overview.IsMerging);
            _commit.IsEnabled = _commitPush.IsEnabled = canCommit;
            _push.IsEnabled = !_busy && (_overview.Ahead > 0 || !_overview.HasUpstream);
            _pull.IsEnabled = _fetch.IsEnabled = !_busy && _overview.HasRemote;
        }

        private void Commit(bool push)
        {
            if (_busy || _overview == null) return;
            CommitRequested?.Invoke(_message.Text ?? "", push);
        }

        private void AddSection(string title, IReadOnlyList<GitOverviewEntry> entries, bool staged)
        {
            if (entries.Count == 0) return;
            var header = new DockPanel { Margin = new Thickness(6, 6, 4, 2) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (staged) buttons.Children.Add(RowButton("−", "Unstage all", () => UnstageRequested?.Invoke(entries)));
            else
            {
                buttons.Children.Add(RowButton("↶", "Discard all of these changes", () => DiscardRequested?.Invoke(entries)));
                buttons.Children.Add(RowButton("+", "Stage all", () => StageRequested?.Invoke(entries)));
            }
            DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
            header.Children.Add(buttons);
            header.Children.Add(new TextBlock
            {
                Text = $"{title.ToUpperInvariant()}  {entries.Count}", FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center,
            });
            _lists.Children.Add(header);
            foreach (var entry in entries) _lists.Children.Add(Row(entry));
        }

        private static readonly IBrush Hover = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xB7, 0x00, 0x52));

        private static IBrush LetterBrush(GitFileState s) => s switch
        {
            GitFileState.Added or GitFileState.Untracked => new SolidColorBrush(Color.FromRgb(0x7F, 0xD9, 0x7F)),
            GitFileState.Deleted or GitFileState.Conflicted => new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x6A)),
            GitFileState.Renamed => EditorTheme.Purple,
            _ => new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x40)),
        };

        private static string KeyOf(GitOverviewEntry e) => (e.IsStaged ? "s|" : "w|") + e.FullPath;

        private Control Row(GitOverviewEntry entry)
        {
            var name = new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (entry.State == GitFileState.Deleted) name.TextDecorations = TextDecorations.Strikethrough;
            var folder = new TextBlock { Text = entry.Folder, FontSize = 11, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var letter = new TextBlock { Text = entry.Letter, FontWeight = FontWeight.Bold, FontSize = 12, Foreground = LetterBrush(entry.State), VerticalAlignment = VerticalAlignment.Center, Width = 16, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            ToolTip.SetTip(letter, entry.State switch
            {
                GitFileState.Modified => "changed", GitFileState.Added => "added", GitFileState.Deleted => "deleted", GitFileState.Renamed => "renamed", GitFileState.Untracked => "new (untracked)",
                GitFileState.Conflicted => "conflict: changed on both sides", _ => "",
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.35 };
            var one = new[] { entry };
            if (entry.IsStaged) buttons.Children.Add(RowButton("−", "Unstage", () => UnstageRequested?.Invoke(one)));
            else
            {
                buttons.Children.Add(RowButton("↶", entry.State == GitFileState.Untracked ? "Delete this new file" : "Discard the changes", () => DiscardRequested?.Invoke(one)));
                buttons.Children.Add(RowButton("+", entry.State == GitFileState.Conflicted ? "Mark as solved (stage)" : "Stage", () => StageRequested?.Invoke(one)));
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(8, 0, 4, 0), MinHeight = 22 };
            grid.Children.Add(letter);
            Grid.SetColumn(name, 1); grid.Children.Add(name);
            Grid.SetColumn(folder, 2); grid.Children.Add(folder);
            Grid.SetColumn(buttons, 3); grid.Children.Add(buttons);

            string key = KeyOf(entry);
            var row = new Border { Child = grid, Background = key == _selected ? SelectedBrush : Brushes.Transparent, Tag = key, Cursor = new Cursor(StandardCursorType.Hand) };
            ToolTip.SetTip(row, entry.Path);
            row.PointerEntered += (_, _) => { buttons.Opacity = 1; if ((string?)row.Tag != _selected) row.Background = Hover; };
            row.PointerExited += (_, _) => { buttons.Opacity = 0.35; row.Background = (string?)row.Tag == _selected ? SelectedBrush : Brushes.Transparent; };
            row.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
                if ((e.Source as Visual)?.FindAncestorOfType<Button>() != null) return;
                Select(key);
                if (e.ClickCount >= 2) OpenRequested?.Invoke(entry);
                else DiffRequested?.Invoke(entry);
                e.Handled = true;
            };
            return row;
        }

        private void Select(string key)
        {
            _selected = key;
            foreach (var row in _lists.Children.OfType<Border>().Where(b => b.Tag is string))
                row.Background = (string)row.Tag! == key ? SelectedBrush : Brushes.Transparent;
        }

        private static Button RowButton(string text, string tip, Action click)
        {
            var b = new Button { Content = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.Bold }, Padding = new Thickness(5, 0), MinWidth = 20, MinHeight = 18 };
            b.Classes.Add("tool");
            ToolTip.SetTip(b, tip);
            b.Click += (_, e) => { click(); e.Handled = true; };
            return b;
        }
    }
}
