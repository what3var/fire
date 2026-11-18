using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using fire.Git;

namespace fire.Editor
{
    /// <summary>What the Repository view asks the window to do (the window asks the user to confirm and runs it, then reads everything again).</summary>
    internal enum GitViewAction
    {
        CheckoutBranch, CheckoutCommit, CreateBranch, CreateTag, DeleteBranch, DeleteRemoteBranch, DeleteTag, PushTag,
        Merge, Rebase, CherryPick, Revert, Reset, ResetToUpstream,
        StashSave, StashApply, StashPop, StashDrop,
        Fetch, Pull, Push, Continue, Abort, Skip, CopySha,
    }

    /// <summary>A command of the Repository view: `Target` a branch name, a sha, a tag; `Label` how to name it to the user; `Mode` of a reset; `Stash` the index of a stash.</summary>
    internal sealed record GitViewRequest(GitViewAction Action, string Target = "", string? Label = null, GitResetMode Mode = GitResetMode.Mixed, int Stash = -1);

    /// <summary>One row of the log: the entry, its lanes, whether it is the current commit.</summary>
    internal sealed record GitLogRow(GitLogEntry? Entry, GitGraphRow? Graph, bool IsHead, bool IsMore = false, string? Text = null);

    /// <summary>
    /// The Repository view (docs/tools/git.md), a document tab (Git > Repository, or ⎇ in the Git panel): on the left the branches, the servers' branches (by server), the tags and the stashes;
    /// in the middle the commits of all of them as a graph (lanes for the branches and merges; virtualized, in pages of 300 that are read when the end is scrolled to; filtered by text, author,
    /// path; the current commit is marked); on the right the selected commit - message, author, date, the files it changed, and the changes of the file that is clicked. The context menus ask
    /// the window for the operations (<see cref="CommandRequested"/>): checkout, branch and tag here, merge, rebase, cherry-pick, revert, reset, stash, delete. A rebase or merge that stopped
    /// shows a banner with Continue / Skip / Abort. The view reads the repository in the background; <see cref="Reload"/> after every git command.
    /// </summary>
    internal sealed class GitRepositoryView : UserControl, IDocumentView
    {
        private const int PageSize = 300;
        private const double RowHeight = 24, LaneWidth = 14;

        private readonly TreeView _refs = new() { Background = Brushes.Transparent };
        private readonly ListBox _log = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), SelectionMode = SelectionMode.Single };
        private readonly ObservableCollection<GitLogRow> _rows = new();
        private readonly TextBox _text = new() { PlaceholderText = "Message or id", MinWidth = 160, FontSize = 12, Padding = new Thickness(6, 2), MinHeight = 24 };
        private readonly TextBox _author = new() { PlaceholderText = "Author", MinWidth = 110, FontSize = 12, Padding = new Thickness(6, 2), MinHeight = 24 };
        private readonly TextBox _path = new() { PlaceholderText = "Path (file or folder)", MinWidth = 150, FontSize = 12, Padding = new Thickness(6, 2), MinHeight = 24 };
        private readonly TextBlock _info = new() { Foreground = EditorTheme.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        private readonly Border _banner = new() { IsVisible = false, Padding = new Thickness(10, 6), Background = new SolidColorBrush(Color.FromArgb(0x50, 0xE8, 0x6A, 0x6A)) };
        private readonly TextBlock _bannerText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        private readonly Button _continue, _skip, _abort;
        private readonly TextBlock _notice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false, Margin = new Thickness(8, 2) };
        private readonly SelectableTextBlock _details = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(8, 6) };
        private readonly ListBox _files = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        private readonly DiffView _diff = new();
        private readonly Control _graphHeader;

        private string? _root;
        private GitRefs? _refsData;
        private GitGraphLayout _layout = new();
        private int? _next;
        private bool _loading;
        private int _generation;
        private string? _signature;
        private string? _selectedSha;
        private GitLogFilter _filter = new();
        private double _graphWidth = LaneWidth * 2;
        private readonly DispatcherTimer _filterTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };

        public event Action<GitViewRequest>? CommandRequested;

        public GitRepositoryView()
        {
            Focusable = true;
            // ---- the bar: the commands of the servers, stash, the filter ----
            var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 4, 4, 2) };
            foreach (var b in new[]
            {
                ToolButton("⇣ Fetch", "Get the commits of all servers (without joining them)", () => Ask(new GitViewRequest(GitViewAction.Fetch))),
                ToolButton("↓ Pull", "Fetch and join the server's changes into the current branch", () => Ask(new GitViewRequest(GitViewAction.Pull))),
                ToolButton("↑ Push", "Send the commits of the current branch to the server", () => Ask(new GitViewRequest(GitViewAction.Push))),
                ToolButton("Stash…", "Put the changes of the files away (onto the stash): the files are as in the last commit afterwards", () => Ask(new GitViewRequest(GitViewAction.StashSave))),
                ToolButton("⟳", "Read the repository again", () => Reload(force: true)),
            }) { b.Margin = new Thickness(0, 0, 2, 2); bar.Children.Add(b); }
            bar.Children.Add(new TextBlock { Text = "Filter:", Foreground = EditorTheme.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 2) });
            foreach (var box in new[] { _text, _author, _path })
            {
                box.Margin = new Thickness(0, 0, 4, 2);
                box.TextChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
                bar.Children.Add(box);
            }
            _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); ApplyFilter(); };
            bar.Children.Add(_info);

            // ---- the banner of an operation that is not finished ----
            _continue = BannerButton("Continue", "Go on: the solved and staged files are committed, then the next steps", GitViewAction.Continue);
            _continue.Classes.Add("accent");
            _skip = BannerButton("Skip", "Leave this commit out (its changes are thrown away) and go on", GitViewAction.Skip);
            _abort = BannerButton("Abort", "Stop it: the branch and the files are as before", GitViewAction.Abort);
            var bannerButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 0, 0, 0), Children = { _continue, _skip, _abort } };
            var bannerDock = new DockPanel();
            DockPanel.SetDock(bannerButtons, Avalonia.Controls.Dock.Right);
            bannerDock.Children.Add(bannerButtons);
            bannerDock.Children.Add(_bannerText);
            _banner.Child = bannerDock;

            // ---- the left: refs ----
            _refs.SelectionChanged += (_, _) =>
            {
                if (!_fillingRefs && _refs.SelectedItem is TreeViewItem { Tag: string sha }) SelectCommit(sha);
            };
            var left = new DockPanel();
            var refsTitle = SectionTitle("REFERENCES");
            DockPanel.SetDock(refsTitle, Avalonia.Controls.Dock.Top);
            left.Children.Add(refsTitle);
            left.Children.Add(_refs);

            // ---- the middle: the log ----
            _log.ItemsSource = _rows;
            _log.ItemTemplate = new FuncDataTemplate<GitLogRow>((row, _) => row == null ? new Panel() : BuildRow(row), supportsRecycling: false);
            _log.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
            {
                Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(0)), new Setter(MinHeightProperty, 0.0), new Setter(HeightProperty, RowHeight) },
            });
            ScrollViewer.SetHorizontalScrollBarVisibility(_log, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
            _log.SelectionChanged += (_, _) =>
            {
                if (_log.SelectedItem is GitLogRow { Entry: { } e }) { _selectedSha = e.Commit.Sha; _ = ShowDetails(e.Commit.Sha); }
            };
            _log.ContainerPrepared += (_, e) =>
            {
                if (e.Container is ListBoxItem { Content: GitLogRow { IsMore: true } } || (e.Index >= 0 && e.Index < _rows.Count && _rows[e.Index].IsMore)) Dispatcher.UIThread.Post(() => _ = LoadPage(), DispatcherPriority.Background);
            };
            _graphHeader = LogHeader();
            var middle = new DockPanel();
            DockPanel.SetDock(_graphHeader, Avalonia.Controls.Dock.Top);
            middle.Children.Add(_graphHeader);
            middle.Children.Add(_log);

            // ---- the right: the commit ----
            _files.ItemTemplate = new FuncDataTemplate<GitCommitFile>((f, _) => f == null ? new Panel() : FileRow(f), supportsRecycling: false);
            _files.SelectionChanged += (_, _) =>
            {
                if (_files.SelectedItem is GitCommitFile f && _selectedSha is { } sha) _ = ShowFileDiff(sha, f);
            };
            var detailsScroll = new ScrollViewer { Content = _details, MaxHeight = 260 };
            var filesTitle = SectionTitle("CHANGED FILES");
            var right = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,5,2*") };
            right.Children.Add(detailsScroll);
            Grid.SetRow(filesTitle, 1); right.Children.Add(filesTitle);
            Grid.SetRow(_files, 2); right.Children.Add(_files);
            var hsplit = new GridSplitter { ResizeDirection = GridResizeDirection.Rows, Background = EditorTheme.Border };
            Grid.SetRow(hsplit, 3); right.Children.Add(hsplit);
            Grid.SetRow(_diff, 4); right.Children.Add(_diff);

            var main = new Grid { ColumnDefinitions = new ColumnDefinitions("230,5,*,5,400") };
            main.Children.Add(new Border { BorderBrush = EditorTheme.Border, Child = left });
            var s1 = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = EditorTheme.Border };
            Grid.SetColumn(s1, 1); main.Children.Add(s1);
            Grid.SetColumn(middle, 2); main.Children.Add(middle);
            var s2 = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = EditorTheme.Border };
            Grid.SetColumn(s2, 3); main.Children.Add(s2);
            Grid.SetColumn(right, 4); main.Children.Add(right);

            var top = new StackPanel { Children = { bar, _banner, _notice } };
            var root = new DockPanel { Background = EditorTheme.Background };
            DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
            root.Children.Add(new Border { [DockPanel.DockProperty] = Avalonia.Controls.Dock.Top, BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = top });
            root.Children.Add(main);
            Content = root;
            _diff.SetDiff("");
        }

        // ---- building blocks --------------------------------------------------------------------------------------------------------------

        private static Button ToolButton(string text, string tip, Action click)
        {
            var b = new Button { Content = new TextBlock { Text = text, FontSize = 12 }, Padding = new Thickness(8, 2), MinWidth = 26, MinHeight = 24 };
            b.Classes.Add("tool");
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }

        private Button BannerButton(string text, string tip, GitViewAction action)
        {
            var b = new Button { Content = text, Padding = new Thickness(10, 2), MinHeight = 24 };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => Ask(new GitViewRequest(action));
            return b;
        }

        private static Control SectionTitle(string text) =>
            new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = EditorTheme.TextDim, Margin = new Thickness(8, 6, 8, 4) };

        private Control LogHeader()
        {
            var grid = LogGrid();
            grid.Margin = new Thickness(0, 4, 0, 2);
            void Add(string text, int column) { var t = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = EditorTheme.TextDim, Margin = new Thickness(6, 0) }; Grid.SetColumn(t, column); grid.Children.Add(t); }
            Add("GRAPH", 0); Add("MESSAGE", 1); Add("AUTHOR", 2); Add("DATE", 3); Add("COMMIT", 4);
            return grid;
        }

        private Grid LogGrid() => new() { ColumnDefinitions = new ColumnDefinitions($"{Math.Max(_graphWidth, 50)},*,140,120,70") };

        private void Ask(GitViewRequest request) => CommandRequested?.Invoke(request);

        private static MenuItem Item(string header, Action click, bool enabled = true, string? tip = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (tip != null) ToolTip.SetTip(item, tip);
            item.Click += (_, _) => click();
            return item;
        }

        // ---- the data ---------------------------------------------------------------------------------------------------------------------

        /// <summary>The repository that is shown (its working folder), null: none.</summary>
        public string? Root => _root;

        /// <summary>The refs as shown last (for the window: the current branch, the upstream, the operation).</summary>
        public GitRefs? Refs => _refsData;

        /// <summary>Shows the repository at `root`: everything is read again.</summary>
        public void ShowRepository(string? root)
        {
            if (root == _root && _refsData != null) { Reload(); return; }
            _root = root;
            _signature = null;
            Reload(force: true);
        }

        /// <summary>
        /// Reads the refs again (in the background) and, when anything changed (a commit, a branch, a tag, the head, an operation) or `force`, the log from the first page (the selected commit stays
        /// selected when it is still there).
        /// </summary>
        public void Reload(bool force = false)
        {
            if (_root is not { } root) { ShowEmpty("Open a solution, a project or a file in a git repository to see it here."); return; }
            int generation = ++_generation;
            var filter = _filter;
            _ = Task.Run(() =>
            {
                try
                {
                    using var repo = GitRepository.Open(root);
                    var refs = repo.Refs();
                    string signature = Signature(refs);
                    GitLogPage? page = null;
                    if (force || signature != _signature) page = repo.History(filter, 0, PageSize);
                    return (refs, signature, page, (string?)null);
                }
                catch (GitException ex) { return ((GitRefs?)null, "", (GitLogPage?)null, ex.Message); }
            }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                if (generation != _generation) return;   // (a newer reload is on its way)
                var (refs, signature, page, error) = t.Result;
                if (error != null || refs == null) { ShowEmpty(error ?? "The repository could not be read."); return; }
                _refsData = refs;
                ShowRefs(refs);
                ShowBanner(refs);
                if (page != null)
                {
                    _signature = signature;
                    ShowFirstPage(page, refs);
                }
            }));
        }

        private static string Signature(GitRefs r) =>
            string.Join(";", new[] { r.Head, r.HeadSha ?? "", r.Operation.ToString(), r.RebaseStep.ToString() }
                .Concat(r.Branches.Select(b => b.Name + "=" + b.Sha)).Concat(r.Remotes.SelectMany(kv => kv.Value).Select(b => b.Name + "=" + b.Sha)).Concat(r.Tags.Select(t => t.Name + "=" + t.Sha))
                .Concat(r.Stashes.Select(s => s.Sha)));

        private void ShowEmpty(string text)
        {
            _refsData = null;
            _rows.Clear();
            _refs.ItemsSource = null;
            _refs.Items.Clear();
            _details.Text = text;
            _files.ItemsSource = null;
            _diff.SetDiff("");
            _banner.IsVisible = false;
            _info.Text = "";
        }

        private void ApplyFilter()
        {
            _filter = new GitLogFilter(_text.Text, _author.Text, _path.Text);
            Reload(force: true);
        }

        private void ShowFirstPage(GitLogPage page, GitRefs refs)
        {
            _layout = new GitGraphLayout();
            _next = page.Next;
            _rows.Clear();
            AppendPage(page, refs);
            var keep = _selectedSha != null ? _rows.FirstOrDefault(r => r.Entry?.Commit.Sha == _selectedSha) : null;
            keep ??= _rows.FirstOrDefault(r => r.IsHead) ?? _rows.FirstOrDefault(r => r.Entry != null);
            if (keep != null) { _log.SelectedItem = keep; _log.ScrollIntoView(keep); }
            else { _details.Text = _filter.IsEmpty ? "No commits yet." : "No commit matches the filter."; _files.ItemsSource = null; _diff.SetDiff(""); }
        }

        private void AppendPage(GitLogPage page, GitRefs refs)
        {
            if (_rows.Count > 0 && _rows[^1].IsMore) _rows.RemoveAt(_rows.Count - 1);
            bool graph = _filter.IsEmpty;   // (filtered, the lines between the commits that are left would be wrong: only dots)
            IReadOnlyList<GitGraphRow> laid = graph ? _layout.Add(page.Entries.Select(e => (e.Commit.Sha, e.Parents))) : Array.Empty<GitGraphRow>();
            var bySha = laid.ToDictionary(r => r.Sha, StringComparer.Ordinal);
            // the last row of the page before got its lines to this page: it is shown anew
            if (graph && _rows.Count > 0 && _rows[^1].Entry is { } last && bySha.TryGetValue(last.Commit.Sha, out var lastGraph))
                _rows[^1] = _rows[^1] with { Graph = lastGraph };
            foreach (var e in page.Entries)
                _rows.Add(new GitLogRow(e, bySha.TryGetValue(e.Commit.Sha, out var g) ? g : null, e.Commit.Sha == refs.HeadSha));
            if (page.Next != null) _rows.Add(new GitLogRow(null, null, false, IsMore: true, Text: "reading more commits…"));
            int width = graph ? _layout.Rows.Count == 0 ? 1 : Math.Min(24, _layout.Rows.Max(r => r.Width)) : 1;
            double newWidth = width * LaneWidth + 8;
            if (Math.Abs(newWidth - _graphWidth) > 0.5)
            {
                _graphWidth = newWidth;
                if (_graphHeader is Grid header) header.ColumnDefinitions[0].Width = new GridLength(Math.Max(_graphWidth, 50));
                // (the rows are made again with the new width)
                var all = _rows.ToList(); _rows.Clear(); foreach (var r in all) _rows.Add(r);
            }
            int count = _rows.Count(r => r.Entry != null);
            _info.Text = page.Next == null ? $"{count} commit{(count == 1 ? "" : "s")}" : $"{count}+ commits";
            if (page.Warning != null) { _info.Text += " - history incomplete"; ToolTip.SetTip(_info, page.Warning); }
        }

        private async Task LoadPage()
        {
            if (_loading || _next is not { } start || _root is not { } root || _refsData is not { } refs) return;
            _loading = true;
            int generation = _generation;
            var filter = _filter;
            try
            {
                var page = await Task.Run(() => { using var repo = GitRepository.Open(root); return repo.History(filter, start, PageSize); });
                if (generation != _generation) return;
                _next = page.Next;
                AppendPage(page, refs);
            }
            catch (GitException ex) { ShowNotice(ex.Message, error: true); }
            finally { _loading = false; }
        }

        /// <summary>A line above the view (an error in red); null hides it.</summary>
        public void ShowNotice(string? text, bool error = false)
        {
            _notice.Text = text ?? "";
            _notice.IsVisible = !string.IsNullOrEmpty(text);
            _notice.Foreground = error ? EditorTheme.ErrorMark : EditorTheme.TextDim;
        }

        private void ShowBanner(GitRefs refs)
        {
            _banner.IsVisible = refs.Operation != GitOperationKind.None;
            if (!_banner.IsVisible) return;
            string what = GitRepository.OperationName(refs.Operation);
            _bannerText.Text = refs.Operation == GitOperationKind.Rebase
                ? $"A rebase stopped{(refs.RebaseSteps > 0 ? $" at step {refs.RebaseStep} of {refs.RebaseSteps}" : "")}{(refs.RebaseStopped != null ? $" ({refs.RebaseStopped})" : "")}: solve the conflicts (!), stage the files in the Git panel (+), then Continue - or Skip this commit, or Abort the rebase."
                : $"A {what} is not finished: solve the conflicts (!), stage the files in the Git panel (+), then Continue (commits it) - or Abort it.";
            _skip.IsVisible = refs.Operation == GitOperationKind.Rebase;
        }

        // ---- the refs tree ------------------------------------------------------------------------------------------------------------------

        private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
        private bool _fillingRefs;   // (the selection that is put back after a reload does not move the log)

        private TreeViewItem Group(string key, string title, IEnumerable<TreeViewItem> children, bool expandedByDefault = true)
        {
            var item = new TreeViewItem { Header = new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = 12 }, IsExpanded = expandedByDefault ? !_collapsed.Contains(key) : _collapsed.Contains("+" + key) };
            foreach (var c in children) item.Items.Add(c);
            item.PropertyChanged += (_, e) =>
            {
                if (e.Property != TreeViewItem.IsExpandedProperty) return;
                if (expandedByDefault) { if (item.IsExpanded) _collapsed.Remove(key); else _collapsed.Add(key); }
                else { if (item.IsExpanded) _collapsed.Add("+" + key); else _collapsed.Remove("+" + key); }
            };
            return item;
        }

        private TreeViewItem RefItem(string text, string? sha, IBrush? color = null, bool bold = false, string? tip = null, ContextMenu? menu = null, string? suffix = null, Action? doubleClick = null)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal, Foreground = color ?? EditorTheme.Text });
            if (!string.IsNullOrEmpty(suffix)) panel.Children.Add(new TextBlock { Text = suffix, FontSize = 11, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center });
            var item = new TreeViewItem { Header = panel, Tag = sha, ContextMenu = menu };
            if (tip != null) ToolTip.SetTip(item, tip);
            if (doubleClick != null) item.DoubleTapped += (_, e) => { doubleClick(); e.Handled = true; };
            return item;
        }

        private void ShowRefs(GitRefs refs)
        {
            string? selected = (_refs.SelectedItem as TreeViewItem)?.Name;
            var items = new List<TreeViewItem>();
            string head = refs.IsDetached ? $"HEAD (detached at {refs.HeadSha?.Substring(0, 7)})" : refs.Head;
            items.Add(RefItem("● " + head, refs.HeadSha, EditorTheme.Magenta, bold: true, tip: refs.IsDetached ? "No branch is checked out: new commits belong to no branch until you make one (Create Branch Here...)." : "The current branch"));
            items.Add(Group("branches", $"Branches  {refs.Branches.Count}", refs.Branches.Select(b =>
            {
                string sync = b.Upstream == null ? "" : b.Ahead == 0 && b.Behind == 0 ? "" : string.Join(" ", new[] { b.Ahead > 0 ? $"↑{b.Ahead}" : "", b.Behind > 0 ? $"↓{b.Behind}" : "" }.Where(s => s.Length > 0));
                var item = RefItem((b.IsCurrent ? "● " : "") + b.Name, b.Sha, b.IsCurrent ? EditorTheme.Magenta : null, bold: b.IsCurrent,
                    tip: (b.Upstream != null ? $"Follows {b.Upstream}" + (sync.Length > 0 ? $": {b.Ahead} to push, {b.Behind} to pull" : ", up to date") : "Follows no branch of a server") + "\nDouble click: switch to it",
                    menu: BranchMenu(b, refs), suffix: sync, doubleClick: b.IsCurrent ? null : () => Ask(new GitViewRequest(GitViewAction.CheckoutBranch, b.Name, b.Name)));
                item.Name = "b:" + b.Name;
                return item;
            })));
            foreach (var (remote, branches) in refs.Remotes.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                items.Add(Group("remote:" + remote, $"Server {remote}  {branches.Count}", branches.Select(b =>
                {
                    var item = RefItem(b.ShortName, b.Sha, EditorTheme.Orange, tip: $"{b.Name}: the branch of the server as of the last fetch\nDouble click: check it out (a local branch that follows it)",
                        menu: RemoteMenu(b, refs), doubleClick: () => Ask(new GitViewRequest(GitViewAction.CheckoutBranch, b.Name, b.Name)));
                    item.Name = "r:" + b.Name;
                    return item;
                })));
            items.Add(Group("tags", $"Tags  {refs.Tags.Count}", refs.Tags.Select(t =>
            {
                var item = RefItem(t.Name, t.Sha, EditorTheme.Yellow, tip: t.Message ?? "A tag (lightweight)", menu: TagMenu(t));
                item.Name = "t:" + t.Name;
                return item;
            }), expandedByDefault: refs.Tags.Count <= 30));
            items.Add(Group("stashes", $"Stashes  {refs.Stashes.Count}", refs.Stashes.Select(s =>
            {
                var item = RefItem($"stash@{{{s.Index}}}", s.Sha, EditorTheme.Purple, tip: $"{s.Message}\n{s.When.LocalDateTime:yyyy-MM-dd HH:mm}", menu: StashMenu(s), suffix: s.Message);
                item.Name = "s:" + s.Index;
                return item;
            })));
            _fillingRefs = true;
            _refs.ItemsSource = items;
            if (selected != null)
                foreach (var group in items)
                {
                    var hit = group.Name == selected ? group : group.Items.OfType<TreeViewItem>().FirstOrDefault(i => i.Name == selected);
                    if (hit != null) { _refs.SelectedItem = hit; break; }
                }
            _fillingRefs = false;
        }

        private ContextMenu BranchMenu(GitRefInfo b, GitRefs refs)
        {
            var menu = new ContextMenu();
            string current = refs.IsDetached ? "the detached HEAD" : refs.Head;
            menu.Items.Add(Item($"Switch to '{b.Name}'", () => Ask(new GitViewRequest(GitViewAction.CheckoutBranch, b.Name, b.Name)), !b.IsCurrent));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Merge '{b.Name}' into '{current}'...", () => Ask(new GitViewRequest(GitViewAction.Merge, b.Name, $"'{b.Name}'")), !b.IsCurrent));
            menu.Items.Add(Item($"Rebase '{current}' onto '{b.Name}'...", () => Ask(new GitViewRequest(GitViewAction.Rebase, b.Name, $"'{b.Name}'")), !b.IsCurrent));
            menu.Items.Add(Item($"Reset '{current}' to '{b.Name}'...", () => Ask(new GitViewRequest(GitViewAction.Reset, b.Sha, $"'{b.Name}' ({b.Sha.Substring(0, 7)})")), !b.IsCurrent));
            if (b.IsCurrent && b.Upstream != null)
                menu.Items.Add(Item($"Reset '{b.Name}' to '{b.Upstream}' (the server's)...", () => Ask(new GitViewRequest(GitViewAction.ResetToUpstream, b.Upstream, b.Upstream))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Create Branch Here...", () => Ask(new GitViewRequest(GitViewAction.CreateBranch, b.Sha, b.Name))));
            menu.Items.Add(Item("Create Tag Here...", () => Ask(new GitViewRequest(GitViewAction.CreateTag, b.Sha, b.Name))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Delete '{b.Name}'...", () => Ask(new GitViewRequest(GitViewAction.DeleteBranch, b.Name, b.Name)), !b.IsCurrent, b.IsCurrent ? "The current branch cannot be deleted" : null));
            return menu;
        }

        private ContextMenu RemoteMenu(GitRefInfo b, GitRefs refs)
        {
            var menu = new ContextMenu();
            string current = refs.IsDetached ? "the detached HEAD" : refs.Head;
            menu.Items.Add(Item($"Check Out (a local branch '{b.ShortName}' that follows it)", () => Ask(new GitViewRequest(GitViewAction.CheckoutBranch, b.Name, b.Name))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Merge '{b.Name}' into '{current}'...", () => Ask(new GitViewRequest(GitViewAction.Merge, b.Name, $"'{b.Name}'"))));
            menu.Items.Add(Item($"Rebase '{current}' onto '{b.Name}'...", () => Ask(new GitViewRequest(GitViewAction.Rebase, b.Name, $"'{b.Name}'"))));
            menu.Items.Add(Item($"Reset '{current}' to '{b.Name}'...", () => Ask(new GitViewRequest(GitViewAction.Reset, b.Sha, $"'{b.Name}' ({b.Sha.Substring(0, 7)})"))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Create Branch Here...", () => Ask(new GitViewRequest(GitViewAction.CreateBranch, b.Sha, b.Name))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Delete '{b.ShortName}' on the Server '{b.Remote}'...", () => Ask(new GitViewRequest(GitViewAction.DeleteRemoteBranch, b.Name, b.Name))));
            return menu;
        }

        private ContextMenu TagMenu(GitRefInfo t)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Check Out (detached HEAD)...", () => Ask(new GitViewRequest(GitViewAction.CheckoutCommit, t.Sha, $"tag '{t.Name}'"))));
            menu.Items.Add(Item("Create Branch Here...", () => Ask(new GitViewRequest(GitViewAction.CreateBranch, t.Sha, t.Name))));
            menu.Items.Add(Item("Push the Tag to the Server", () => Ask(new GitViewRequest(GitViewAction.PushTag, t.Name, t.Name))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Delete Tag '{t.Name}'...", () => Ask(new GitViewRequest(GitViewAction.DeleteTag, t.Name, t.Name))));
            return menu;
        }

        private ContextMenu StashMenu(GitStashInfo s)
        {
            var menu = new ContextMenu();
            menu.Items.Add(Item("Apply (the stash stays)", () => Ask(new GitViewRequest(GitViewAction.StashApply, s.Sha, s.Message, Stash: s.Index))));
            menu.Items.Add(Item("Pop (apply and remove it)", () => Ask(new GitViewRequest(GitViewAction.StashPop, s.Sha, s.Message, Stash: s.Index))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Drop (delete it)...", () => Ask(new GitViewRequest(GitViewAction.StashDrop, s.Sha, s.Message, Stash: s.Index))));
            return menu;
        }

        private ContextMenu CommitMenu(GitLogEntry e)
        {
            var c = e.Commit;
            var refs = _refsData;
            string current = refs == null || refs.IsDetached ? "the detached HEAD" : refs.Head;
            bool isHead = c.Sha == refs?.HeadSha;
            string label = $"{c.ShortSha} \"{Shorten(c.Summary, 40)}\"";
            var menu = new ContextMenu();
            menu.Items.Add(Item("Check Out This Commit (detached HEAD)...", () => Ask(new GitViewRequest(GitViewAction.CheckoutCommit, c.Sha, label)), !isHead));
            foreach (var name in e.Refs.Where(r => !r.StartsWith("tag: ", StringComparison.Ordinal) && r != "HEAD" && r != refs?.Head))
            {
                string branch = name;
                menu.Items.Add(Item($"Switch to '{branch}'", () => Ask(new GitViewRequest(GitViewAction.CheckoutBranch, branch, branch))));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Create Branch Here...", () => Ask(new GitViewRequest(GitViewAction.CreateBranch, c.Sha, c.ShortSha))));
            menu.Items.Add(Item("Create Tag Here...", () => Ask(new GitViewRequest(GitViewAction.CreateTag, c.Sha, c.ShortSha))));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Cherry-Pick into '{current}'...", () => Ask(new GitViewRequest(GitViewAction.CherryPick, c.Sha, label)), !isHead, "A new commit on the current branch with the changes of this one"));
            menu.Items.Add(Item("Revert (a commit that undoes it)...", () => Ask(new GitViewRequest(GitViewAction.Revert, c.Sha, label)), tip: "A new commit on the current branch that takes the changes of this one back"));
            menu.Items.Add(Item($"Merge into '{current}'...", () => Ask(new GitViewRequest(GitViewAction.Merge, c.Sha, label)), !isHead));
            menu.Items.Add(Item($"Rebase '{current}' onto This Commit...", () => Ask(new GitViewRequest(GitViewAction.Rebase, c.Sha, label)), !isHead));
            menu.Items.Add(new Separator());
            var reset = new MenuItem { Header = $"Reset '{current}' to Here" };
            reset.Items.Add(Item("Soft - keep the files and stage the changes...", () => Ask(new GitViewRequest(GitViewAction.Reset, c.Sha, label, GitResetMode.Soft))));
            reset.Items.Add(Item("Mixed - keep the files, nothing staged...", () => Ask(new GitViewRequest(GitViewAction.Reset, c.Sha, label, GitResetMode.Mixed))));
            reset.Items.Add(Item("Hard - throw away all changes...", () => Ask(new GitViewRequest(GitViewAction.Reset, c.Sha, label, GitResetMode.Hard))));
            menu.Items.Add(reset);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Copy Commit Id", () => Ask(new GitViewRequest(GitViewAction.CopySha, c.Sha))));
            return menu;
        }

        private static string Shorten(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 1) + "…";

        // ---- the rows of the log ------------------------------------------------------------------------------------------------------------

        private static readonly IBrush[] LaneBrushes =
        {
            EditorTheme.Magenta, EditorTheme.Solid(Color.FromRgb(0x60, 0xA5, 0xFA)), EditorTheme.Orange, EditorTheme.Solid(Color.FromRgb(0x7F, 0xD9, 0x7F)),
            EditorTheme.Purple, EditorTheme.Yellow, EditorTheme.Solid(Color.FromRgb(0x4F, 0xD1, 0xC5)), EditorTheme.WineRed,
        };

        internal static IBrush LaneBrush(int color) => LaneBrushes[((color % LaneBrushes.Length) + LaneBrushes.Length) % LaneBrushes.Length];

        /// <summary>The lanes of one row: the lower halves of the lines from the row above, the upper halves of its own lines down, and the dot.</summary>
        private sealed class GraphCell : Control
        {
            private readonly GitGraphRow? _row;
            private readonly bool _head;

            public GraphCell(GitGraphRow? row, bool head) { _row = row; _head = head; ClipToBounds = true; }

            private static double X(int lane) => 7 + lane * LaneWidth;

            public override void Render(DrawingContext context)
            {
                double h = Bounds.Height, mid = h / 2;
                if (_row == null)
                {
                    context.DrawEllipse(_head ? EditorTheme.Magenta : EditorTheme.TextDim, null, new Point(X(0), mid), 4, 4);
                    return;
                }
                foreach (var e in _row.Up)
                {
                    // from the middle of the row above (x of From) to the middle of this one (x of To): the half below the border of the rows
                    var pen = new Pen(LaneBrush(e.Color), 2);
                    double x0 = X(e.From), x1 = X(e.To);
                    double xm = (x0 + x1) / 2;
                    context.DrawLine(pen, new Point(xm, 0), new Point(x1, mid));
                }
                foreach (var e in _row.Down)
                {
                    var pen = new Pen(LaneBrush(e.Color), 2);
                    double x0 = X(e.From), x1 = X(e.To);
                    double xm = (x0 + x1) / 2;
                    context.DrawLine(pen, new Point(x0, mid), new Point(xm, h));
                }
                var brush = LaneBrush(_row.Color);
                var center = new Point(X(_row.Lane), mid);
                if (_head) context.DrawEllipse(EditorTheme.Background, new Pen(EditorTheme.Text, 2), center, 6, 6);
                if (_row.IsMerge) context.DrawEllipse(EditorTheme.Background, new Pen(brush, 2), center, 4, 4);
                else context.DrawEllipse(brush, null, center, 4, 4);
            }
        }

        private Control BuildRow(GitLogRow row)
        {
            if (row.IsMore || row.Entry == null)
                return new TextBlock { Text = row.Text ?? "", Foreground = EditorTheme.TextDim, FontStyle = FontStyle.Italic, FontSize = 12, Margin = new Thickness(_graphWidth + 6, 4, 0, 0) };
            var e = row.Entry;
            var grid = LogGrid();
            grid.Background = Brushes.Transparent;   // (the whole row takes the right click)
            grid.Children.Add(new GraphCell(row.Graph, row.IsHead) { Height = RowHeight });
            var message = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };
            if (row.IsHead && !e.Refs.Contains("HEAD")) message.Children.Add(Badge("HEAD", EditorTheme.Magenta, bold: true));
            foreach (var r in e.Refs)
            {
                bool tag = r.StartsWith("tag: ", StringComparison.Ordinal);
                bool remote = !tag && r != "HEAD" && _refsData?.Remotes.Values.Any(l => l.Any(b => b.Name == r)) == true;
                bool current = r == _refsData?.Head && !(_refsData?.IsDetached ?? false);
                message.Children.Add(Badge(tag ? "🏷 " + r.Substring(5) : current ? "● " + r : r, tag ? EditorTheme.Yellow : remote ? EditorTheme.Orange : r == "HEAD" || current ? EditorTheme.Magenta : EditorTheme.Solid(Color.FromRgb(0x60, 0xA5, 0xFA)), bold: current || r == "HEAD"));
            }
            message.Children.Add(new TextBlock { Text = e.Commit.Summary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, FontWeight = row.IsHead ? FontWeight.Bold : FontWeight.Normal, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(message, 1); grid.Children.Add(message);
            var author = new TextBlock { Text = e.Commit.Author, FontSize = 12, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(6, 0) };
            ToolTip.SetTip(author, $"{e.Commit.Author} <{e.Email}>");
            Grid.SetColumn(author, 2); grid.Children.Add(author);
            var date = new TextBlock { Text = e.Commit.When.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), FontSize = 12, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
            Grid.SetColumn(date, 3); grid.Children.Add(date);
            var sha = new TextBlock { Text = e.Commit.ShortSha, FontSize = 12, FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace"), Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) };
            Grid.SetColumn(sha, 4); grid.Children.Add(sha);
            grid.ContextMenu = CommitMenu(e);
            grid.DoubleTapped += (_, ev) => { ev.Handled = true; };
            return grid;
        }

        private static Control Badge(string text, IBrush color, bool bold = false) => new Border
        {
            BorderBrush = color, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = color, FontWeight = bold ? FontWeight.Bold : FontWeight.Normal },
        };

        // ---- the selected commit ------------------------------------------------------------------------------------------------------------

        /// <summary>Selects the commit in the log (it is read page by page until it is there, at most 20 pages more).</summary>
        public async void SelectCommit(string sha)
        {
            for (int i = 0; i < 20; i++)
            {
                var row = _rows.FirstOrDefault(r => r.Entry?.Commit.Sha == sha);
                if (row != null) { _log.SelectedItem = row; _log.ScrollIntoView(row); return; }
                if (_next == null) break;
                await LoadPage();
            }
            _selectedSha = sha;
            await ShowDetails(sha);   // (not in the log as filtered: its details all the same)
        }

        private int _detailsGeneration;

        private async Task ShowDetails(string sha)
        {
            if (_root is not { } root) return;
            int generation = ++_detailsGeneration;
            try
            {
                var details = await Task.Run(() => { using var repo = GitRepository.Open(root); return repo.CommitDetails(sha); });
                if (generation != _detailsGeneration) return;
                var c = details.Commit;
                string parents = details.Parents.Count == 0 ? "(none: the first commit)" : string.Join(", ", details.Parents.Select(p => p.Substring(0, 7)));
                string committer = details.Committer != c.Author || details.CommitterEmail != details.AuthorEmail ? $"\nCommitter: {details.Committer} <{details.CommitterEmail}>, {details.CommitterWhen.LocalDateTime:yyyy-MM-dd HH:mm}" : "";
                string refs = details.Refs.Count > 0 ? $"\nRefs:      {string.Join(", ", details.Refs)}" : "";
                _details.Text = $"{c.Message}\n\nCommit:    {c.Sha}\nAuthor:    {c.Author} <{details.AuthorEmail}>\nDate:      {c.When.LocalDateTime:yyyy-MM-dd HH:mm:ss zzz}{committer}\nParents:   {parents}{refs}\nFiles:     {details.Files.Count} changed";
                _files.ItemsSource = details.Files;
                if (details.Files.Count > 0) _files.SelectedIndex = 0;
                else _diff.SetDiff(details.Parents.Count > 1 ? "(a merge: the files it changed against its first parent are listed - none)" : "");
            }
            catch (GitException ex) { _details.Text = ex.Message; _files.ItemsSource = null; _diff.SetDiff(""); }
        }

        private async Task ShowFileDiff(string sha, GitCommitFile file)
        {
            if (_root is not { } root) return;
            int generation = _detailsGeneration;
            try
            {
                string diff = await Task.Run(() => { using var repo = GitRepository.Open(root); return repo.DiffCommitFile(sha, file.Path, file.OldPath); });
                if (generation == _detailsGeneration) _diff.SetDiff(diff);
            }
            catch (GitException ex) { _diff.SetDiff(ex.Message); }
        }

        private static Control FileRow(GitCommitFile f)
        {
            var letter = GitOverview.Letter(f.State);
            IBrush color = f.State switch { GitFileState.Added => EditorTheme.Solid(Color.FromRgb(0x7F, 0xD9, 0x7F)), GitFileState.Deleted => EditorTheme.ErrorMark, GitFileState.Renamed => EditorTheme.Purple, _ => EditorTheme.Orange };
            int slash = f.Path.LastIndexOf('/');
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 1) };
            panel.Children.Add(new TextBlock { Text = letter, Width = 12, FontWeight = FontWeight.Bold, Foreground = color, FontSize = 12 });
            panel.Children.Add(new TextBlock { Text = slash < 0 ? f.Path : f.Path.Substring(slash + 1), FontSize = 12 });
            string folder = (slash < 0 ? "" : f.Path.Substring(0, slash)) + (f.OldPath != null ? $"  (from {f.OldPath})" : "");
            if (folder.Length > 0) panel.Children.Add(new TextBlock { Text = folder, FontSize = 11, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center });
            ToolTip.SetTip(panel, f.Path);
            return panel;
        }

        // ---- IDocumentView: a view to look at ------------------------------------------------------------------------------------------------

        public string? FilePath { get; set; }
        public bool IsReadOnly => true;
        public bool IsModified => false;
        public event Action? ModifiedChanged { add { } remove { } }
        public event Action<int>? CaretLineChanged { add { } remove { } }
        public int GetCaretLine() => 1;
        public string GetText() => "";
        public void ResetTo(string text, string? filePath) { FilePath = null; }
        public void MarkSaved() { }
        public void FocusEditor() => _log.Focus();
        public bool CanUndo => false;
        public bool CanRedo => false;
        public bool HasSelection => false;
        public void Undo() { }
        public void Redo() { }
        public void Cut() { }
        public void Copy() { }
        public void Paste() { }
        public void Delete() { }
        public void SelectAll() { }
        public void Find() => _text.Focus();
        public void FindNext() { }
        public void FindPrevious() { }
        public int LineCount => 1;
        public void GoToLine(int line) { }
    }
}
