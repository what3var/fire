using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using fire.Git;

namespace fire.Editor
{
    /// <summary>
    /// The Git panel (docs/tools/git.md), a tab next to the solution explorer: the branch and how far it is from the server's (↑ to push, ↓ to pull), Fetch / Pull / Push, the message and Commit
    /// (and Commit &amp; Push), and the changed files in three lists - staged, changes, untracked - with their letter. A file is staged with +, unstaged with −, its changes are thrown away with ↶
    /// (the window asks first); a click shows its changes, a double click opens it; a folder that is untracked as a whole is one row that opens with a click. The list is virtualized (a ListBox of
    /// <see cref="GitRow"/>s: only the rows on screen have controls - 20000 changed files cost nothing) and can be filtered. The window reads the repository and does the commands
    /// (MainWindow.GitPanel.cs) - the control only shows a <see cref="GitOverview"/> and asks for them.
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
        private readonly Button _commit, _commitPush, _fetch, _pull, _push, _refresh, _repository;
        private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent, SelectionMode = SelectionMode.Single };
        private readonly TextBox _filter = new() { Watermark = "Filter the files", FontSize = 12, Margin = new Thickness(6, 4, 6, 2), MinHeight = 24, Padding = new Thickness(6, 2) };
        private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (IReadOnlyList<GitOverviewEntry> Files, int Total)> _children = new(StringComparer.Ordinal);
        private bool _selecting;
        private readonly Grid _content;
        private readonly StackPanel _empty;
        private readonly TextBlock _emptyText = new() { TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.TextDim };
        private readonly Button _init;
        private GitOverview? _overview;
        private string? _selected;   // the key of the selected row (GitRow.Key)
        private bool _busy;

        public event Action? FetchRequested, PullRequested, PushRequested, RefreshRequested, InitRequested, RepositoryRequested;
        public event Action<string, bool>? CommitRequested;
        public event Action<IReadOnlyList<GitOverviewEntry>>? StageRequested, UnstageRequested, DiscardRequested;
        /// <summary>A row was clicked: show its changes.</summary>
        public event Action<GitOverviewEntry>? DiffRequested;
        /// <summary>A row was double clicked: open the file.</summary>
        public event Action<GitOverviewEntry>? OpenRequested;
        /// <summary>An untracked folder was opened: the window lists its files (<see cref="SetFolderFiles"/>).</summary>
        public event Action<GitOverviewEntry>? FolderFilesRequested;
        /// <summary>"Add to .gitignore" of an untracked file or folder.</summary>
        public event Action<GitOverviewEntry>? IgnoreRequested;

        public GitPanelControl()
        {
            _fetch = ToolButton("⇣ Fetch", "Get the server's commits without joining them", () => FetchRequested?.Invoke());
            _pull = ToolButton("↓ Pull", "Fetch and join the server's changes into this branch", () => PullRequested?.Invoke());
            _push = ToolButton("↑ Push", "Send the commits of this branch to the server", () => PushRequested?.Invoke());
            _refresh = ToolButton("⟳", "Read the state of the repository again", () => RefreshRequested?.Invoke());
            _repository = ToolButton("⎇ Repository", "The Repository view: branches, tags, stashes, the commit graph - checkout, merge, rebase, cherry-pick, reset", () => RepositoryRequested?.Invoke());

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
            foreach (var b in new[] { _fetch, _pull, _push, _refresh, _repository }) { b.Margin = new Thickness(0, 0, 2, 2); bar.Children.Add(b); }

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

            _list.ItemTemplate = new FuncDataTemplate<GitRow>((row, _) => row == null ? new Panel() : Build(row), supportsRecycling: false);   // (null: a container that is cleared)
            _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
            {
                Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0, 0, 14, 0)), new Setter(MinHeightProperty, 0.0) },   // (room on the right for the scroll bar, which lies over the rows)
            });
            Avalonia.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            _list.SelectionChanged += (_, _) =>
            {
                if (_selecting) return;
                if (_list.SelectedItem is GitRow { Kind: GitRowKind.Entry } row) _selected = row.Key;
                else if (_list.SelectedItem != null) { _selecting = true; _list.SelectedItem = null; _selecting = false; }
            };
            _list.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && _list.SelectedItem is GitRow { Entry: { } entry }) { Activate(entry, open: true); e.Handled = true; }
            };
            _filter.TextChanged += (_, _) => Rebuild();
            _content = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
            _content.Children.Add(topBorder);
            Grid.SetRow(_filter, 1);
            _content.Children.Add(_filter);
            Grid.SetRow(_list, 2);
            _content.Children.Add(_list);

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
            // the opened folders that are still there: their files are read again
            var folders = overview.Untracked.Where(e => e.IsDirectory).Select(e => e.FullPath).ToHashSet(StringComparer.Ordinal);
            _expanded.IntersectWith(folders);
            foreach (var gone in _children.Keys.Where(k => !folders.Contains(k)).ToList()) _children.Remove(gone);
            Rebuild();
            foreach (var e in overview.Untracked.Where(e => e.IsDirectory && _expanded.Contains(e.FullPath))) FolderFilesRequested?.Invoke(e);
            UpdateButtons();
        }

        /// <summary>The files of an opened untracked folder (at most <see cref="GitRows.FolderLimit"/>, `total` in all).</summary>
        public void SetFolderFiles(string folder, IReadOnlyList<GitOverviewEntry> files, int total)
        {
            _children[folder] = (files, total);
            if (_expanded.Contains(folder)) Rebuild();
        }

        /// <summary>The rows as now shown (for tests and the window).</summary>
        public IReadOnlyList<GitRow> Rows => (_list.ItemsSource as IReadOnlyList<GitRow>) ?? Array.Empty<GitRow>();

        private void Rebuild()
        {
            if (_overview == null) return;
            var rows = GitRows.Build(_overview, _filter.Text, _expanded, _children);
            _selecting = true;
            _list.ItemsSource = rows;
            _list.SelectedItem = _selected == null ? null : rows.FirstOrDefault(r => r.Kind == GitRowKind.Entry && r.Key == _selected);
            _selecting = false;
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

        private Control Build(GitRow row) => row.Kind switch
        {
            GitRowKind.Header => Header(row),
            GitRowKind.Entry => Row(row),
            GitRowKind.Banner => new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0xE8, 0x6A, 0x6A)), Padding = new Thickness(8, 4), Margin = new Thickness(4, 2, 4, 4), CornerRadius = new CornerRadius(3),
                Child = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Text = row.Text },
            },
            _ => new TextBlock { Text = row.Text, Foreground = EditorTheme.TextDim, Margin = new Thickness(row.Kind == GitRowKind.More ? 34 : 10, 4), TextWrapping = TextWrapping.Wrap, FontSize = 12, FontStyle = row.Kind == GitRowKind.More ? FontStyle.Italic : FontStyle.Normal },
        };

        private Control Header(GitRow row)
        {
            var entries = row.Section ?? Array.Empty<GitOverviewEntry>();
            var header = new DockPanel { Margin = new Thickness(6, 6, 4, 2) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (row.StagedSection) buttons.Children.Add(RowButton("−", "Unstage all of these", () => UnstageRequested?.Invoke(entries)));
            else
            {
                buttons.Children.Add(RowButton("↶", "Discard all of these changes", () => DiscardRequested?.Invoke(entries)));
                buttons.Children.Add(RowButton("+", "Stage all of these", () => StageRequested?.Invoke(entries)));
            }
            DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
            header.Children.Add(buttons);
            header.Children.Add(new TextBlock { Text = row.Text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center });
            return header;
        }

        private static IBrush LetterBrush(GitFileState s) => s switch
        {
            GitFileState.Added or GitFileState.Untracked => NewBrush,
            GitFileState.Deleted or GitFileState.Conflicted => BadBrush,
            GitFileState.Renamed => EditorTheme.Purple,
            _ => ChangedBrush,
        };
        private static readonly IBrush NewBrush = new SolidColorBrush(Color.FromRgb(0x7F, 0xD9, 0x7F));
        private static readonly IBrush BadBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x6A));
        private static readonly IBrush ChangedBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x40));

        /// <summary>A click: a folder opens or closes, a file shows its changes; `open` (a double click, Enter): the file is opened.</summary>
        private void Activate(GitOverviewEntry entry, bool open)
        {
            if (entry.IsDirectory && !entry.IsStaged)
            {
                if (!_expanded.Remove(entry.FullPath)) { _expanded.Add(entry.FullPath); FolderFilesRequested?.Invoke(entry); }
                Rebuild();
                return;
            }
            if (open) OpenRequested?.Invoke(entry);
            else DiffRequested?.Invoke(entry);
        }

        private Control Row(GitRow gitRow)
        {
            var entry = gitRow.Entry!;
            string label = gitRow.Depth > 0 ? gitRow.Text : entry.Name;   // (a file of an opened folder: its path in the folder)
            var name = new TextBlock { Text = entry.IsDirectory ? label + "/" : label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            if (entry.State == GitFileState.Deleted) name.TextDecorations = TextDecorations.Strikethrough;
            string folderText = gitRow.Depth > 0 ? "" : entry.Folder;
            var folder = new TextBlock { Text = folderText, FontSize = 11, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var letter = new TextBlock { Text = entry.IsDirectory ? (gitRow.IsExpanded ? "▾" : "▸") : entry.Letter, FontWeight = FontWeight.Bold, FontSize = 12, Foreground = LetterBrush(entry.State), VerticalAlignment = VerticalAlignment.Center, Width = 16, TextAlignment = TextAlignment.Center, Margin = new Thickness(gitRow.Depth * 18, 0, 4, 0) };
            ToolTip.SetTip(letter, entry.IsDirectory ? "a new folder (untracked): click to see its files" : entry.State switch
            {
                GitFileState.Modified => "changed", GitFileState.Added => "added", GitFileState.Deleted => "deleted", GitFileState.Renamed => "renamed", GitFileState.Untracked => "new (untracked)",
                GitFileState.Conflicted => "conflict: changed on both sides", _ => "",
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.35 };
            var one = new[] { entry };
            if (entry.IsStaged) buttons.Children.Add(RowButton("−", "Unstage", () => UnstageRequested?.Invoke(one)));
            else
            {
                buttons.Children.Add(RowButton("↶", entry.IsDirectory ? "Delete the new files of this folder" : entry.State == GitFileState.Untracked ? "Delete this new file" : "Discard the changes", () => DiscardRequested?.Invoke(one)));
                buttons.Children.Add(RowButton("+", entry.State == GitFileState.Conflicted ? "Mark as solved (stage)" : "Stage", () => StageRequested?.Invoke(one)));
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(8, 0, 4, 0), MinHeight = 22, Background = Brushes.Transparent };
            grid.Children.Add(letter);
            Grid.SetColumn(name, 1); grid.Children.Add(name);
            Grid.SetColumn(folder, 2); grid.Children.Add(folder);
            Grid.SetColumn(buttons, 3); grid.Children.Add(buttons);
            ToolTip.SetTip(grid, entry.Path + (entry.IsDirectory ? "/" : ""));
            grid.PointerEntered += (_, _) => buttons.Opacity = 1;
            grid.PointerExited += (_, _) => buttons.Opacity = 0.35;
            grid.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
                if ((e.Source as Visual)?.FindAncestorOfType<Button>() != null) return;
                _selected = gitRow.Key;
                _selecting = true; _list.SelectedItem = gitRow; _selecting = false;
                Activate(entry, open: e.ClickCount >= 2);
                e.Handled = true;
            };

            var menu = new ContextMenu();
            if (!entry.IsDirectory) menu.Items.Add(MenuItem("Open", () => OpenRequested?.Invoke(entry)));
            menu.Items.Add(MenuItem(entry.IsDirectory ? "Show Files" : "Show Changes", () => DiffRequested?.Invoke(entry)));
            if (entry.IsStaged) menu.Items.Add(MenuItem("Unstage", () => UnstageRequested?.Invoke(one)));
            else
            {
                menu.Items.Add(MenuItem("Stage", () => StageRequested?.Invoke(one)));
                menu.Items.Add(MenuItem(entry.State == GitFileState.Untracked ? "Delete..." : "Discard Changes...", () => DiscardRequested?.Invoke(one)));
            }
            if (entry.State == GitFileState.Untracked && !entry.IsStaged)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuItem(entry.IsDirectory ? "Add Folder to .gitignore" : "Add to .gitignore", () => IgnoreRequested?.Invoke(entry)));
                if (!entry.IsDirectory && entry.Folder.Length > 0)
                {
                    var parent = new GitOverviewEntry(entry.Folder, System.IO.Path.GetDirectoryName(entry.FullPath)!, GitFileState.Untracked, false, IsDirectory: true);
                    menu.Items.Add(MenuItem($"Add Folder '{entry.Folder}/' to .gitignore", () => IgnoreRequested?.Invoke(parent)));
                }
            }
            grid.ContextMenu = menu;
            return grid;
        }

        private static MenuItem MenuItem(string header, Action click)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => click();
            return item;
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
