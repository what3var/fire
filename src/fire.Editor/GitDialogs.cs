using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using fire.Git;

namespace fire.Editor
{
    /// <summary>A read-only view of a unified diff: added lines green, removed lines red, the markers of places purple, the header dim.</summary>
    internal sealed class DiffView : UserControl
    {
        private sealed class DiffColorizer : DocumentColorizingTransformer
        {
            private static readonly IBrush Added = new SolidColorBrush(Color.FromRgb(0x7F, 0xD9, 0x7F));
            private static readonly IBrush Removed = new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x6A));

            protected override void ColorizeLine(DocumentLine line)
            {
                string text = CurrentContext.Document.GetText(line);
                IBrush? brush = null;
                if (text.StartsWith("+++") || text.StartsWith("---") || text.StartsWith("diff ") || text.StartsWith("index ") || text.StartsWith("new file") || text.StartsWith("deleted file")) brush = EditorTheme.TextDim;
                else if (text.StartsWith('+')) brush = Added;
                else if (text.StartsWith('-')) brush = Removed;
                else if (text.StartsWith("@@")) brush = EditorTheme.Purple;
                if (brush != null && line.Length > 0) ChangeLinePart(line.Offset, line.EndOffset, el => el.TextRunProperties.SetForegroundBrush(brush));
            }
        }

        private readonly TextEditor _editor = new()
        {
            IsReadOnly = true, ShowLineNumbers = false, WordWrap = false, FontSize = 13, Padding = new Thickness(6, 4),
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };

        public DiffView()
        {
            _editor.TextArea.TextView.LineTransformers.Add(new DiffColorizer());
            EditorTheme.Apply(_editor);
            Content = _editor;
        }

        public void SetDiff(string text) => _editor.Text = string.IsNullOrEmpty(text) ? "(no differences)" : text.Replace("\r\n", "\n");
    }

    /// <summary>The changes of one file or commit, to look at.</summary>
    internal sealed class GitDiffDialog : Window
    {
        public GitDiffDialog(string title, string diff)
        {
            Title = title;
            Width = 820; Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            var view = new DiffView();
            view.SetDiff(diff);
            var close = new Button { Content = "Close", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            close.Click += (_, _) => Close();
            var root = new DockPanel { Margin = new Thickness(10) };
            DockPanel.SetDock(close, Avalonia.Controls.Dock.Bottom);
            root.Children.Add(close);
            root.Children.Add(view);
            Content = root;
        }
    }

    /// <summary>Asks for a user name and a password (for GitHub and most servers: a token) of a server.</summary>
    internal sealed class GitCredentialsDialog : Window
    {
        private readonly TextBox _user = new(), _password = new() { PasswordChar = '•' };
        private readonly CheckBox _remember = new() { Content = "Remember until the editor is closed", IsChecked = true };
        public GitCredentials? Result { get; private set; }
        public bool Remember => _remember.IsChecked == true;

        public GitCredentialsDialog(string url)
        {
            Title = "Git: sign in";
            Width = 460; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            CanResize = false; ShowInTaskbar = false;
            var ok = new Button { Content = "OK", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Classes.Add("accent");
            ok.Click += (_, _) => { if (_user.Text?.Length > 0) { Result = new GitCredentials(_user.Text!.Trim(), _password.Text ?? ""); Close(); } };
            var cancel = new Button { Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => Close();
            Content = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"The server {url} asks who you are.", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = "User name", Foreground = EditorTheme.TextDim }, _user,
                    new TextBlock { Text = "Password - for GitHub and most servers a personal access token", Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap }, _password,
                    _remember,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } },
                },
            };
            Opened += (_, _) => _user.Focus();
        }

        public static async Task<(GitCredentials? Credentials, bool Remember)> Ask(Window? owner, string url)
        {
            var dialog = new GitCredentialsDialog(url);
            if (owner != null) await dialog.ShowDialog(owner); else dialog.Show();
            return (dialog.Result, dialog.Remember);
        }
    }

    /// <summary>
    /// The commit dialog: the changed files with a check box each (checked: in this commit), the changes of the selected one, the message - and the name and e-mail address when git has none yet.
    /// "Commit" does it (what is checked is staged, the rest is not); "Commit and push" sends it to the server afterwards (the caller does that, see <see cref="PushAfter"/>).
    /// </summary>
    internal sealed class GitCommitDialog : Window
    {
        private readonly GitRepository _repo;
        private readonly ListBox _files = new();
        private readonly DiffView _diff = new();
        private readonly TextBox _message = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, Watermark = "Message: what was changed and why" };
        private readonly TextBox _name = new() { Watermark = "Your name" }, _email = new() { Watermark = "you@example.com" };
        private readonly TextBlock _error = new() { Foreground = EditorTheme.ErrorMark, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        private readonly Border _identityBox;
        private IReadOnlyList<GitChange> _changes = Array.Empty<GitChange>();

        public bool PushAfter { get; private set; }
        public GitCommitInfo? Committed { get; private set; }

        public GitCommitDialog(GitRepository repo, IEnumerable<string>? preselected = null)
        {
            _repo = repo;
            Title = $"Commit ({repo.BranchName})";
            Width = 980; Height = 640; MinWidth = 700; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            _changes = repo.Status();
            var chosen = preselected?.Select(p => System.IO.Path.GetFullPath(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var change in _changes)
            {
                var box = new CheckBox { Content = $"{Mark(change)}   {change.Path}", Tag = change, IsChecked = (chosen == null || chosen.Contains(change.FullPath)) && !change.IsConflicted };
                _files.Items.Add(box);
            }
            _files.SelectionChanged += (_, _) => ShowDiff();
            if (_files.ItemCount > 0) _files.SelectedIndex = 0;

            var (name, email) = repo.Identity();
            _name.Text = name; _email.Text = email;
            _identityBox = new Border
            {
                IsVisible = string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email),
                Child = new StackPanel
                {
                    Spacing = 4, Margin = new Thickness(0, 6, 0, 0),
                    Children =
                    {
                        new TextBlock { Text = "Git does not know who you are yet. This is kept for this repository:", Foreground = EditorTheme.TextDim, FontSize = 12 },
                        new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*"), Children = { _name, Place(_email, 2) } },
                    },
                },
            };

            var commit = new Button { Content = "Commit", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            commit.Classes.Add("accent");
            commit.Click += (_, _) => Commit(false);
            var commitPush = new Button { Content = "Commit and push", MinWidth = 120, HorizontalContentAlignment = HorizontalAlignment.Center };
            commitPush.Click += (_, _) => Commit(true);
            var cancel = new Button { Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => Close();
            var all = new Button { Content = "All" }; all.Click += (_, _) => SetAll(true);
            var none = new Button { Content = "None" }; none.Click += (_, _) => SetAll(false);

            var left = new DockPanel { Margin = new Thickness(0, 0, 8, 0) };
            var leftBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 6), Children = { new TextBlock { Text = $"{_changes.Count} changed file{(_changes.Count == 1 ? "" : "s")}", VerticalAlignment = VerticalAlignment.Center, Foreground = EditorTheme.TextDim }, all, none } };
            DockPanel.SetDock(leftBar, Avalonia.Controls.Dock.Top);
            left.Children.Add(leftBar);
            left.Children.Add(_files);

            var split = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*") };
            split.Children.Add(left);
            Grid.SetColumn(_diff, 1);
            split.Children.Add(_diff);

            var bottom = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            bottom.Children.Add(_message);
            bottom.Children.Add(_identityBox);
            bottom.Children.Add(_error);
            bottom.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { commit, commitPush, cancel } });

            var root = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
            root.Children.Add(bottom);
            root.Children.Add(split);
            Content = root;
            Opened += (_, _) => _message.Focus();
        }

        private static Control Place(Control control, int column) { Grid.SetColumn(control, column); return control; }

        internal static string Mark(GitChange c) => c.Combined switch
        {
            GitFileState.Modified => "M", GitFileState.Added => "A", GitFileState.Untracked => "U", GitFileState.Deleted => "D", GitFileState.Renamed => "R", GitFileState.Conflicted => "!", _ => " ",
        };

        private void SetAll(bool value) { foreach (CheckBox box in _files.Items) box.IsChecked = value && ((GitChange)box.Tag!).IsConflicted == false; }

        private void ShowDiff()
        {
            if (_files.SelectedItem is not CheckBox { Tag: GitChange change }) { _diff.SetDiff(""); return; }
            try
            {
                string staged = change.Staged != GitFileState.Unmodified ? _repo.Diff(change.FullPath, staged: true) : "";
                string unstaged = change.Unstaged != GitFileState.Unmodified ? _repo.Diff(change.FullPath, staged: false) : "";
                _diff.SetDiff(staged + (staged.Length > 0 && unstaged.Length > 0 ? "\n" : "") + unstaged);
            }
            catch (GitException ex) { _diff.SetDiff(ex.Message); }
        }

        private void Show(string message) { _error.Text = message; _error.IsVisible = true; }

        private void Commit(bool push)
        {
            var selected = _files.Items.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (GitChange)b.Tag!).ToList();
            var rest = _files.Items.OfType<CheckBox>().Where(b => b.IsChecked != true).Select(b => (GitChange)b.Tag!).ToList();
            if (selected.Count == 0 && !_repo.IsMerging) { Show("Check the files that belong to this commit."); return; }
            if (string.IsNullOrWhiteSpace(_message.Text)) { Show("Write a message for the commit."); return; }
            try
            {
                if (_identityBox.IsVisible)
                {
                    if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_email.Text)) { Show("Give your name and your e-mail address."); return; }
                    _repo.SetIdentity(_name.Text!.Trim(), _email.Text!.Trim());
                }
                var unstageable = rest.Where(c => c.Staged != GitFileState.Unmodified).Select(c => c.FullPath).ToList();
                if (unstageable.Count > 0 && _repo.HasCommits) _repo.Unstage(unstageable);
                if (selected.Count > 0) _repo.Stage(selected.Select(c => c.FullPath));
                Committed = _repo.Commit(_message.Text!);
                PushAfter = push;
                Close();
            }
            catch (GitException ex) { Show(ex.Message); }
        }
    }

    /// <summary>The commits of the current branch (or of one file), newest first, with what the selected one changed.</summary>
    internal sealed class GitHistoryDialog : Window
    {
        public GitHistoryDialog(GitRepository repo, string? path)
        {
            Title = path == null ? $"History ({repo.BranchName})" : $"History of {System.IO.Path.GetFileName(path)}";
            Width = 1000; Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            IReadOnlyList<GitCommitInfo> log;
            try { log = repo.Log(300, path); }
            catch (GitException ex) { log = Array.Empty<GitCommitInfo>(); Title += " - " + ex.Message; }
            var list = new ListBox();
            foreach (var c in log)
                list.Items.Add(new StackPanel
                {
                    Tag = c, Margin = new Thickness(2),
                    Children =
                    {
                        new TextBlock { Text = c.Summary, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock { Text = $"{c.ShortSha}   {c.Author}   {c.When.LocalDateTime:yyyy-MM-dd HH:mm}", Foreground = EditorTheme.TextDim, FontSize = 11 },
                    },
                });
            var diff = new DiffView();
            list.SelectionChanged += (_, _) =>
            {
                if (list.SelectedItem is not StackPanel { Tag: GitCommitInfo c }) { diff.SetDiff(""); return; }
                try
                {
                    string text = repo.DiffCommit(c.Sha);
                    diff.SetDiff($"commit {c.Sha}\nAuthor: {c.Author}\nDate:   {c.When.LocalDateTime:yyyy-MM-dd HH:mm}\n\n    {c.Message.Replace("\n", "\n    ")}\n\n{text}");
                }
                catch (GitException ex) { diff.SetDiff(ex.Message); }
            };
            if (list.ItemCount > 0) list.SelectedIndex = 0;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("340,8,*"), Margin = new Thickness(10) };
            grid.Children.Add(list);
            Grid.SetColumn(diff, 2);
            grid.Children.Add(diff);
            Content = grid;
        }
    }

    /// <summary>The branches: switch to one, make a new one at the current commit.</summary>
    internal sealed class GitBranchesDialog : Window
    {
        private readonly GitRepository _repo;
        private readonly ListBox _list = new();
        private readonly TextBlock _error = new() { Foreground = EditorTheme.ErrorMark, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        public bool Changed { get; private set; }

        public GitBranchesDialog(GitRepository repo)
        {
            _repo = repo;
            Title = "Branches";
            Width = 460; Height = 460;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            var switchTo = new Button { Content = "Switch to" }; switchTo.Classes.Add("accent");
            switchTo.Click += (_, _) => Switch();
            var create = new Button { Content = "New branch..." };
            create.Click += async (_, _) => await Create();
            var close = new Button { Content = "Close", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            close.Click += (_, _) => Close();
            _list.DoubleTapped += (_, _) => Switch();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { switchTo, create, close } };
            var root = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Bottom);
            DockPanel.SetDock(_error, Avalonia.Controls.Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(_error);
            root.Children.Add(_list);
            Content = root;
            Fill();
        }

        private void Fill()
        {
            _list.Items.Clear();
            foreach (var b in _repo.Branches())
            {
                string sync = b.IsRemote ? "" : b.HasUpstream ? (b.Ahead > 0 || b.Behind > 0 ? $"   ↑{b.Ahead} ↓{b.Behind}" : "   up to date") : "";
                _list.Items.Add(new TextBlock { Text = (b.IsCurrent ? "● " : "   ") + (b.IsRemote ? "server: " : "") + b.Name + sync, Tag = b, FontWeight = b.IsCurrent ? FontWeight.Bold : FontWeight.Normal });
            }
        }

        private void Switch()
        {
            if (_list.SelectedItem is not TextBlock { Tag: GitBranchInfo b } || b.IsCurrent) return;
            try { _repo.Checkout(b.Name); Changed = true; _error.IsVisible = false; Fill(); }
            catch (GitException ex) { _error.Text = ex.Message; _error.IsVisible = true; }
        }

        private async Task Create()
        {
            string? name = await Dialogs.Input(this, "Name of the new branch (made at the current commit):", "New Branch");
            if (string.IsNullOrWhiteSpace(name)) return;
            try { _repo.CreateBranch(name.Trim()); Changed = true; _error.IsVisible = false; Fill(); }
            catch (GitException ex) { _error.Text = ex.Message; _error.IsVisible = true; }
        }
    }
}
