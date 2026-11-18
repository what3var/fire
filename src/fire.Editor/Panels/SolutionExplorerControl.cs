using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using fire.Git;
using fire.Projects;

namespace fire.Editor
{
    internal enum ExplorerKind { Solution, SolutionFolder, Project, References, Reference, Folder, File, Content, Designer }

    /// <summary>A row of the solution explorer.</summary>
    internal sealed class ExplorerNode : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public required ExplorerKind Kind { get; init; }
        public required string Text { get; init; }
        /// <summary>A second, dimmer text behind the name ("program", "library", "startup").</summary>
        public string Suffix { get; init; } = "";
        /// <summary>The path of a file, the project file of a project.</summary>
        public string? Path { get; init; }
        public LoadedProject? Project { get; init; }
        public ProjectReference? Reference { get; init; }
        public bool IsBold { get; init; }
        /// <summary>The notes folder of a project (docs/tools/notes.md): a double click opens its overview.</summary>
        public bool IsNotes { get; init; }
        /// <summary>A row for something that does not exist yet (the notes folder before its first use): shown pale, made when it is used.</summary>
        public bool IsVirtual { get; init; }
        /// <summary>Something the project leaves out (bin, obj, a file or folder whose name starts with a dot): only with "Show All Files", pale.</summary>
        public bool IsHidden { get; init; }
        /// <summary>The git state of a file (null: none), and for a folder, project or solution whether something below it is changed. A change updates the row (the marks of git are
        /// put on the tree in place when only they changed: SolutionExplorerControl.UpdateGit).</summary>
        public GitFileState? GitState
        {
            get => _gitState;
            set { if (_gitState != value) { _gitState = value; NotifyGit(); } }
        }
        private GitFileState? _gitState;
        public bool GitChangedBelow
        {
            get => _gitBelow;
            set { if (_gitBelow != value) { _gitBelow = value; NotifyGit(); } }
        }
        private bool _gitBelow;

        /// <summary>What the row shows of git: the letter of the file, a dot for a folder with changes below, nothing.</summary>
        public string GitMarkText => GitState is { } s ? SolutionExplorerControl.GitMark(s) : GitChangedBelow ? "●" : "";
        public IBrush GitMarkBrush => SolutionExplorerControl.GitBrush(GitState ?? GitFileState.Modified);
        public double GitMarkSize => GitState != null ? 11 : 9;

        private void NotifyGit()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GitMarkText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GitMarkBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GitMarkSize)));
        }
        /// <summary>The project of the active document is shown in the accent color.</summary>
        public bool IsActive { get; init; }
        /// <summary>The identity of a row across refreshes (for the expanded state).</summary>
        public required string Key { get; init; }
        public ObservableCollection<ExplorerNode> Children { get; } = new();

        private bool _expanded = true;
        public bool IsExpanded
        {
            get => _expanded;
            set { if (_expanded != value) { _expanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); } }
        }

        private bool _shown = true;
        /// <summary>false: the filter of the tool bar hides the row.</summary>
        public bool IsShown
        {
            get => _shown;
            set { if (_shown != value) { _shown = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsShown))); } }
        }
    }

    /// <summary>
    /// The solution explorer (docs/tools/projects.md): the open solution (or the single project) with its projects, their references and files. A double click opens a file; the context menus
    /// ask the window for the commands (new file, add reference, startup project, properties, ...). The window refreshes it when the workspace or the active document changes.
    /// Above the tree a tool bar (docs/tools/spark.md, "The tool bar of the solution explorer"): Collapse All, Expand All, Sync with Active Document, Track Active Document, Refresh,
    /// Show All Files, New File, New Folder, Properties - what does not fit into a narrow panel goes into "…" - and below it a box that filters the tree by name (Esc clears it).
    /// </summary>
    internal sealed class SolutionExplorerControl : UserControl
    {
        private readonly TreeView _tree = new() { BorderThickness = new Thickness(0) };
        private readonly StackPanel _empty;
        private readonly ObservableCollection<ExplorerNode> _roots = new();
        private readonly HashSet<string> _collapsed = new();
        private readonly ContextMenu _menu = new();
        private IReadOnlyDictionary<string, GitFileState>? _git;
        private string? _branch;
        private bool _hasSolution;   // (a start project is chosen only in a solution)
        // what the last Refresh showed (Show All Files builds the tree again from it) and the active file it was given
        private Workspace? _workspace;
        private string? _activeFile;

        // the tool bar: the buttons in their order (what does not fit goes into the menu of "…"), the filter box under it
        private sealed record BarItem(Control Button, string Title, Action Run, Func<bool>? IsChecked = null, Func<ExplorerNode?, IEnumerable<Control>>? SubItems = null);
        private readonly List<BarItem> _barItems = new();
        private readonly StackPanel _barButtons = new() { Orientation = Orientation.Horizontal, Spacing = 2, ClipToBounds = true };
        private readonly Button _more;
        private readonly ToggleButton _track, _showAll;
        private readonly TextBox _filter = new() { Watermark = "Filter by name (Esc clears)", FontSize = 12, MinHeight = 24, Padding = new Thickness(4, 2), VerticalContentAlignment = VerticalAlignment.Center };
        private readonly TextBlock _noMatches = new() { Text = "Nothing matches the filter.", Foreground = EditorTheme.TextDim, Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        private readonly Border _top;
        private bool _filtering;   // the filter shows a part of the tree: the folding the user chose (_collapsed) is kept as it was before and comes back when the box is empty
        private const double BarButtonWidth = 26;

        /// <summary>A file was double clicked (or Enter was pressed on it).</summary>
        public event Action<string>? FileOpenRequested;

        /// <summary>A command of a context menu, of the tool bar or of the empty page: its id and the row it concerns (null: none).</summary>
        public event Action<string, ExplorerNode?>? CommandRequested;

        /// <summary>Another row was selected by the user (null: none): the property window shows the properties of a file.</summary>
        public event Action<ExplorerNode?>? SelectionChanged;

        /// <summary>Track Active Document or Show All Files was switched (the window keeps them in the settings).</summary>
        public event Action? OptionsChanged;

        public SolutionExplorerControl()
        {
            _tree.ItemsSource = _roots;
            _tree.ItemTemplate = new FuncTreeDataTemplate<ExplorerNode>((node, _) => Row(node), node => node.Children);
            _tree.Styles.Add(new Style(x => x.OfType<TreeViewItem>())
            {
                Setters =
                {
                    new Setter(TreeViewItem.IsExpandedProperty, new Binding("IsExpanded") { Mode = BindingMode.TwoWay }),
                    new Setter(Visual.IsVisibleProperty, new Binding("IsShown")),
                },
            });
            _tree.ContextMenu = _menu;
            _tree.SelectionChanged += (_, _) => { if (!_refreshing) SelectionChanged?.Invoke(_tree.SelectedItem as ExplorerNode); };
            _menu.Opening += (_, e) => { if (!FillMenu()) e.Cancel = true; };
            _tree.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            // a double click opens: a file, the designer of a markup (instead of folding it), the properties of a project or the solution, the overview of the notes;
            // the tree has already folded the row when the event arrives, so the row gets back the state it had at the first click
            _tree.AddHandler(DoubleTappedEvent, (_, e) =>
            {
                if ((e.Source as Visual)?.FindAncestorOfType<ToggleButton>() != null) return;   // the arrow of the row: folds, as always
                if (_tree.SelectedItem is not ExplorerNode node || !Opens(node)) return;
                if (_expandedAtPress is { } press && ReferenceEquals(press.Node, node)) node.IsExpanded = press.Expanded;   // before opening: that may build the tree anew
                Open(node);
                e.Handled = true;
            }, RoutingStrategies.Bubble, handledEventsToo: true);
            _tree.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && _tree.SelectedItem is ExplorerNode selected && Open(selected)) e.Handled = true;
                else if (e.Key == Key.Delete && _tree.SelectedItem is ExplorerNode { Kind: not ExplorerKind.Designer } node) { CommandRequested?.Invoke("remove", node); e.Handled = true; }
                else if (e.Key == Key.Escape && _filtering) { _filter.Text = ""; e.Handled = true; }
            };

            _empty = new StackPanel { Margin = new Thickness(12), Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
            _empty.Children.Add(new TextBlock { Text = "No solution or project is open.", TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.TextDim });
            _empty.Children.Add(new TextBlock { Text = "Single files work as always: open them with File > Open.", TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.TextDim, FontSize = 12 });
            var newProject = new Button { Content = "New Project...", HorizontalAlignment = HorizontalAlignment.Left };
            newProject.Click += (_, _) => CommandRequested?.Invoke("new-project", null);
            var open = new Button { Content = "Open Project or Solution...", HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (_, _) => CommandRequested?.Invoke("open-workspace", null);
            _empty.Children.Add(newProject);
            _empty.Children.Add(open);

            // ---- the tool bar ----
            Button BarButton(string icon, string title, string tip, Action run)
            {
                var b = ToolIcons.Button(icon, tip, run);
                b.Width = BarButtonWidth;
                _barItems.Add(new BarItem(b, title, run));
                return b;
            }
            ToggleButton BarToggle(string icon, string title, string tip, Action<bool> switched)
            {
                var b = ToolIcons.Toggle(icon, tip);
                b.Width = BarButtonWidth;
                b.IsCheckedChanged += (_, _) => { if (!_settingOptions) { switched(b.IsChecked == true); OptionsChanged?.Invoke(); } };
                _barItems.Add(new BarItem(b, title, () => b.IsChecked = b.IsChecked != true, () => b.IsChecked == true));
                return b;
            }
            BarButton("CollapseAll", "Collapse All", "Collapse All: close every project and folder (the solution stays open)", CollapseAll);
            BarButton("ExpandAll", "Expand All", "Expand All: open every project and folder", ExpandAll);
            BarButton("Locate", "Sync with Active Document", "Sync with Active Document: select the file of the active document in the tree", () => RevealFile(_activeFile));
            _track = BarToggle("Track", "Track Active Document", "Track Active Document: select the file of the active document whenever another one becomes active", on => { if (on) RevealFile(_activeFile); });
            BarButton("Reload", "Refresh", "Refresh: read the folders of the projects again from the disk", () => CommandRequested?.Invoke("refresh", null));
            _showAll = BarToggle("ShowAll", "Show All Files", "Show All Files: also show what the projects leave out - bin, obj, hidden files and folders (pale)", _ => RefreshAgain());
            Button? newFile = null;
            newFile = BarButton("NewFile", "New File", "New File in the selected folder (or project): script, class, window, markdown, image, ...", () => ShowNewFileMenu(newFile!));
            _barItems[^1] = _barItems[^1] with { SubItems = NewFileItems };
            BarButton("NewFolder", "New Folder...", "New Folder in the selected folder (or project; in a solution: a solution folder)", NewFolder);
            BarButton("Properties", "Properties", "Properties of the selected row (a file in the Properties panel, a project or the solution in its dialog)", ShowProperties);
            _more = ToolIcons.Button("More", "More commands of the tool bar", ShowMoreMenu);
            _more.Width = BarButtonWidth;
            _more.IsVisible = false;
            foreach (var item in _barItems) _barButtons.Children.Add(item.Button);
            var bar = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(_more, Avalonia.Controls.Dock.Right);
            bar.Children.Add(_more);
            bar.Children.Add(_barButtons);
            bar.SizeChanged += (_, e) => LayoutBar(e.NewSize.Width);

            var clear = ToolIcons.Button("Close", "Clear the filter (Esc)", () => { _filter.Text = ""; _filter.Focus(); });
            clear.Padding = new Thickness(2, 0);
            clear.Content = ToolIcons.Create("Close", 12);
            clear.IsVisible = false;
            _filter.InnerLeftContent = new PathIcon { Data = ToolIcons.Geometry("Find"), Width = 12, Height = 12, Foreground = EditorTheme.TextDim, Margin = new Thickness(2, 0, 0, 0) };
            _filter.InnerRightContent = clear;
            ToolTip.SetTip(_filter, "Shows only the rows whose name contains the text (and the folders they are in). Enter or Down: into the tree. Esc: clears it.");
            _filter.TextChanged += (_, _) => { clear.IsVisible = !string.IsNullOrEmpty(_filter.Text); ApplyFilter(); };
            _filter.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape && !string.IsNullOrEmpty(_filter.Text)) { _filter.Text = ""; e.Handled = true; }
                else if (e.Key is Key.Enter or Key.Down && _roots.Count > 0)
                {
                    // into the tree, on the first match (no filter: the selected row, else the first one)
                    var first = _filtering ? ExplorerTree.PathTo(_roots, n => n.Children, n => n.IsShown && ExplorerTree.Matches(FilterName(n), _filter.Text) && FilterName(n).Length > 0)?.LastOrDefault() : null;
                    if (first != null) Select(first, raise: true);
                    else if (Selected == null) Select(_roots[0], raise: true);
                    _tree.Focus();
                    e.Handled = true;
                }
            };

            var top = new StackPanel { Margin = new Thickness(4, 3, 4, 4), Spacing = 3, Children = { bar, _filter } };
            _top = new Border { BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = top };
            var body = new Grid { Children = { _tree, _noMatches } };
            var content = new DockPanel();
            DockPanel.SetDock(_top, Avalonia.Controls.Dock.Top);
            content.Children.Add(_top);
            content.Children.Add(body);
            Content = new Grid { Children = { content, _empty } };
            Show(false);
        }

        private void Show(bool hasContent)
        {
            _tree.IsVisible = hasContent;
            _top.IsVisible = hasContent;
            _empty.IsVisible = !hasContent;
            if (!hasContent) _noMatches.IsVisible = false;
        }

        // ---- the tool bar ----------------------------------------------------------------------------------------------------------------------
        private bool _settingOptions;

        /// <summary>Track Active Document (kept in the settings by the window).</summary>
        public bool TrackActive
        {
            get => _track.IsChecked == true;
            set { _settingOptions = true; try { _track.IsChecked = value; } finally { _settingOptions = false; } }
        }

        /// <summary>Show All Files (kept in the settings by the window).</summary>
        public bool ShowAllFiles
        {
            get => _showAll.IsChecked == true;
            set { if (ShowAllFiles == value) return; _settingOptions = true; try { _showAll.IsChecked = value; } finally { _settingOptions = false; } RefreshAgain(); }
        }

        /// <summary>The buttons that fit stay in the bar, the others go into the menu of "…".</summary>
        private void LayoutBar(double width)
        {
            double step = BarButtonWidth + _barButtons.Spacing;
            int fit = _barItems.Count * step - _barButtons.Spacing <= width ? _barItems.Count : Math.Max(0, (int)((width - BarButtonWidth - 2) / step));
            for (int i = 0; i < _barItems.Count; i++) _barItems[i].Button.IsVisible = i < fit;
            _more.IsVisible = fit < _barItems.Count;
        }

        private void ShowMoreMenu()
        {
            var flyout = new MenuFlyout();
            foreach (var item in _barItems.Where(i => !i.Button.IsVisible))
            {
                var entry = new MenuItem { Header = item.Title, IsEnabled = item.Button.IsEnabled };
                if (item.SubItems != null) foreach (var sub in item.SubItems(NewFileTarget())) entry.Items.Add(sub);
                else
                {
                    if (item.IsChecked != null) { entry.ToggleType = MenuItemToggleType.CheckBox; entry.IsChecked = item.IsChecked(); }
                    entry.Click += (_, _) => item.Run();
                }
                flyout.Items.Add(entry);
            }
            flyout.ShowAt(_more);
        }

        private void ShowNewFileMenu(Control button)
        {
            var flyout = new MenuFlyout();
            foreach (var item in NewFileItems(NewFileTarget())) flyout.Items.Add(item);
            flyout.ShowAt(button);
        }

        private static IEnumerable<ExplorerNode> All(IEnumerable<ExplorerNode> nodes) => nodes.SelectMany(n => All(n.Children).Prepend(n));

        private List<ExplorerNode>? PathToNode(ExplorerNode node) => ExplorerTree.PathTo(_roots, n => n.Children, n => ReferenceEquals(n, node));

        /// <summary>Closes everything below the solution (a single project: the project stays open, its folders close).</summary>
        private void CollapseAll()
        {
            foreach (var n in All(_roots)) n.IsExpanded = false;
            foreach (var root in _roots.Where(r => r.Kind == ExplorerKind.Solution || _roots.Count == 1)) root.IsExpanded = true;
            // the selection goes up to the row that can still be seen
            if (Selected is { } selected && PathToNode(selected) is { } chain)
                Select(chain.FirstOrDefault(n => !n.IsExpanded) ?? chain[^1], raise: false);
        }

        private void ExpandAll()
        {
            foreach (var n in All(_roots)) if (n.Children.Count > 0) n.IsExpanded = true;
        }

        /// <summary>Selects the file in the tree (its folders are opened, the tree scrolls to it); false: it is in no project shown here. The selection does not change the property window
        /// (the active document keeps it).</summary>
        public bool RevealFile(string? path)
        {
            if (path == null || _roots.Count == 0) return false;
            string full = Path.GetFullPath(path);
            bool IsTarget(ExplorerNode n) => n.Kind is ExplorerKind.File or ExplorerKind.Content && n.Path != null && ProjectFiles.PathComparer.Equals(Path.GetFullPath(n.Path), full);
            var chain = ExplorerTree.PathTo(_roots, n => n.Children, IsTarget);
            if (chain == null) return false;
            if (!chain[^1].IsShown) _filter.Text = "";   // the filter hides it: the whole tree again
            foreach (var n in chain.Take(chain.Count - 1)) n.IsExpanded = true;
            Select(chain[^1], raise: false);
            return true;
        }

        private void Select(ExplorerNode node, bool raise)
        {
            if (raise) _tree.SelectedItem = node;
            else
            {
                bool was = _refreshing;
                _refreshing = true;
                try { _tree.SelectedItem = node; } finally { _refreshing = was; }
            }
            // (only the row itself and from its left edge: a long name must not scroll the tree sideways)
            Dispatcher.UIThread.Post(() => { if (_tree.TreeContainerFromItem(node) is Control c) c.BringIntoView(new Rect(0, 0, 1, Math.Min(c.Bounds.Height, 24))); }, DispatcherPriority.Background);
        }

        /// <summary>The row a new file or folder goes into: the selected folder (of a file: its folder), else its project; a solution or a solution folder for themselves; null: nothing selected.</summary>
        private ExplorerNode? CreateTarget()
        {
            if (Selected is not { } selected || PathToNode(selected) is not { } chain) return null;
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                var n = chain[i];
                if (n.Kind == ExplorerKind.Folder && !n.IsHidden && !n.IsVirtual) return n;
                if (n.Kind is ExplorerKind.Project or ExplorerKind.SolutionFolder or ExplorerKind.Solution) return n;
            }
            return null;
        }

        /// <summary>For a new file a solution is no place: the window takes the project of the active document then (null).</summary>
        private ExplorerNode? NewFileTarget() => CreateTarget() is { Kind: ExplorerKind.Folder or ExplorerKind.Project } n ? n : null;

        private void NewFolder()
        {
            var target = CreateTarget();
            if (target?.Kind is ExplorerKind.Solution or ExplorerKind.SolutionFolder) CommandRequested?.Invoke("new-folder", target);
            else CommandRequested?.Invoke("new-project-folder", target);
        }

        /// <summary>The commands that a double click and the context menus use: a file in the Properties panel, a project's dialog, the solution's.</summary>
        private void ShowProperties()
        {
            var node = Selected;
            switch (node?.Kind)
            {
                case ExplorerKind.Solution or ExplorerKind.SolutionFolder: CommandRequested?.Invoke("solution-properties", node); break;
                case ExplorerKind.Project or ExplorerKind.Folder or ExplorerKind.References or ExplorerKind.Reference when node.Project != null: CommandRequested?.Invoke("properties", node); break;
                default: CommandRequested?.Invoke("show-properties", node); break;   // a file (or nothing): the Properties panel
            }
        }

        // ---- the filter ------------------------------------------------------------------------------------------------------------------------
        /// <summary>The name the filter looks at: the text of the row; the solution row has none (else every letter of "Solution" would match it).</summary>
        private static string FilterName(ExplorerNode node) => node.Kind == ExplorerKind.Solution ? "" : node.Text;

        /// <summary>The text of the filter box (empty: no filter).</summary>
        public string FilterText { get => _filter.Text ?? ""; set => _filter.Text = value; }

        private void ApplyFilter()
        {
            var result = ExplorerTree.Filter(_roots, n => n.Children, FilterName, _filter.Text);
            if (result == null)
            {
                foreach (var n in All(_roots)) n.IsShown = true;
                if (_filtering)
                {
                    _filtering = false;
                    ApplyCollapsed(_roots);
                    // the selected row stays in sight
                    if (Selected is { } selected && PathToNode(selected) is { } chain)
                    {
                        foreach (var n in chain.Take(chain.Count - 1)) n.IsExpanded = true;
                        Select(selected, raise: false);
                    }
                }
                _noMatches.IsVisible = false;
                return;
            }
            if (!_filtering) { RememberCollapsed(_roots); _filtering = true; }
            foreach (var n in All(_roots))
            {
                n.IsShown = result.Shown.Contains(n);
                n.IsExpanded = result.Expanded.Contains(n) || !_collapsed.Contains(n.Key);
            }
            _noMatches.IsVisible = result.Matches.Count == 0;
        }

        // (the tree is built anew: the selection it loses is no choice of the user)
        private bool _refreshing;

        /// <summary>The row that is selected.</summary>
        public ExplorerNode? Selected => _tree.SelectedItem as ExplorerNode;

        private static Control Row(ExplorerNode node)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Opacity = node.IsVirtual || node.IsHidden ? 0.55 : 1 };
            panel.Children.Add(TreeIcons.Image(TreeIcons.NameFor(node)));
            panel.Children.Add(new TextBlock
            {
                Text = node.Text,
                FontStyle = node.IsVirtual ? FontStyle.Italic : FontStyle.Normal,
                FontWeight = node.IsBold ? FontWeight.Bold : FontWeight.Normal,
                Foreground = node.IsActive ? EditorTheme.Solid(EditorTheme.AccentTextColor) : node.Kind == ExplorerKind.References ? EditorTheme.TextDim : EditorTheme.Text,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var mark = new TextBlock { FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
            mark.Bind(TextBlock.TextProperty, new Binding(nameof(ExplorerNode.GitMarkText)) { Source = node });
            mark.Bind(TextBlock.ForegroundProperty, new Binding(nameof(ExplorerNode.GitMarkBrush)) { Source = node });
            mark.Bind(TextBlock.FontSizeProperty, new Binding(nameof(ExplorerNode.GitMarkSize)) { Source = node });
            panel.Children.Add(mark);
            if (node.Suffix.Length > 0)
                panel.Children.Add(new TextBlock { Text = node.Suffix, Foreground = EditorTheme.TextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        internal static string GitMark(GitFileState s) => s switch
        {
            GitFileState.Modified => "M", GitFileState.Added => "A", GitFileState.Untracked => "U", GitFileState.Deleted => "D", GitFileState.Renamed => "R", GitFileState.Conflicted => "!", _ => "",
        };

        private static readonly IBrush GitModified = new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x40));
        private static readonly IBrush GitNew = new SolidColorBrush(Color.FromRgb(0x7F, 0xD9, 0x7F));
        private static readonly IBrush GitBad = new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x6A));

        internal static IBrush GitBrush(GitFileState s) => s switch
        {
            GitFileState.Added or GitFileState.Untracked => GitNew, GitFileState.Deleted or GitFileState.Conflicted => GitBad, _ => GitModified,
        };

        // ---- building the tree ----------------------------------------------------------------------------------------------------------------
        /// <summary>Shows the workspace; `activeFile` is the file of the active document (its project is marked).</summary>
        public void Refresh(Workspace workspace, string? activeFile, IReadOnlyDictionary<string, GitFileState>? git = null, string? branch = null)
        {
            bool activeChanged = !string.Equals(activeFile, _activeFile, ProjectFiles.PathComparison);
            _workspace = workspace;
            _activeFile = activeFile;
            _git = git;
            _branch = branch;
            _hasSolution = workspace.Solution != null;
            if (!_filtering) RememberCollapsed(_roots);   // (while the filter is on, the folding before it is kept)
            string? selectedKey = Selected?.Key;
            _refreshing = true;
            try { _roots.Clear(); } finally { _refreshing = false; }
            if (!workspace.IsOpen) { Show(false); return; }
            Show(true);
            Build(workspace, activeFile);
            if (_filtering || !string.IsNullOrWhiteSpace(_filter.Text)) ApplyFilter();
            // the selected row is selected again in the new tree; with Track Active Document the file of a document that just became active
            if (TrackActive && activeChanged && RevealFile(activeFile)) return;
            if (selectedKey != null && All(_roots).FirstOrDefault(n => n.Key == selectedKey) is { } again)
            {
                _refreshing = true;
                try { _tree.SelectedItem = again; } finally { _refreshing = false; }
            }
        }

        /// <summary>The tree again from what the last Refresh got (Show All Files was switched).</summary>
        private void RefreshAgain()
        {
            if (_workspace != null) Refresh(_workspace, _activeFile, _git, _branch);
        }

        private void Build(Workspace workspace, string? activeFile)
        {

            var activeProject = activeFile != null ? workspace.FindProjectOf(activeFile) : null;
            var startup = workspace.Startup;
            ExplorerNode? solutionNode = null;
            if (workspace.Solution != null)
            {
                solutionNode = new ExplorerNode { Kind = ExplorerKind.Solution, Text = $"Solution '{workspace.Solution.Name}'", Suffix = $"{workspace.Projects.Count} project{(workspace.Projects.Count == 1 ? "" : "s")}" + (_branch != null ? ", git: " + _branch : ""), Path = workspace.Solution.FilePath, Key = "sln", IsBold = true };
                _roots.Add(solutionNode);
            }
            var folderNodes = new Dictionary<string, ExplorerNode>(ProjectFiles.PathComparer);
            // the folders of the solution: those that hold a project (a project made in a folder lives in a subfolder of it) and the ones that are listed (empty ones)
            ExplorerNode FolderNode(string fullDir)
            {
                string solutionDir = workspace.Solution!.Directory!;
                string rel = ProjectFiles.Relative(solutionDir, fullDir);
                if (rel == "." || rel.StartsWith("..") || Path.IsPathRooted(rel)) return solutionNode!;
                if (folderNodes.TryGetValue(fullDir, out var existing)) return existing;
                var parent = FolderNode(Path.GetDirectoryName(fullDir)!);
                var folder = new ExplorerNode { Kind = ExplorerKind.SolutionFolder, Text = Path.GetFileName(fullDir), Path = fullDir, Key = "sdir:" + fullDir };
                folderNodes[fullDir] = folder;
                parent.Children.Add(folder);
                return folder;
            }
            if (solutionNode != null)
                foreach (var listed in workspace.Solution!.Folders.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    FolderNode(Path.GetFullPath(listed.Replace('\\', '/'), workspace.Solution.Directory!));
            foreach (var project in workspace.Projects)
            {
                var node = ProjectNode(workspace, project, ReferenceEquals(project, startup) && workspace.Solution != null, ReferenceEquals(project, activeProject));
                if (solutionNode != null) FolderNode(Path.GetDirectoryName(project.Directory)!).Children.Add(node); else _roots.Add(node);
            }
            if (solutionNode != null) SortFolders(solutionNode);
            if (_git != null) MarkGit(_roots);
            ApplyCollapsed(_roots);
        }

        /// <summary>In a project: References first, then folders by name, then the files (sources, in compile order; then the content files by name).</summary>
        private static void SortProjectChildren(ExplorerNode node)
        {
            var references = node.Children.Where(c => c.Kind == ExplorerKind.References).ToList();
            var folders = node.Children.Where(c => c.Kind == ExplorerKind.Folder).OrderBy(c => c.Text, StringComparer.OrdinalIgnoreCase).ToList();
            var sources = node.Children.Where(c => c.Kind == ExplorerKind.File).ToList();
            var content = node.Children.Where(c => c.Kind == ExplorerKind.Content).OrderBy(c => c.Text, StringComparer.OrdinalIgnoreCase).ToList();
            node.Children.Clear();
            foreach (var n in references) node.Children.Add(n);
            foreach (var f in folders) { SortProjectChildren(f); node.Children.Add(f); }
            foreach (var n in sources.Concat(content)) node.Children.Add(n);
        }

        /// <summary>Folders before projects (each by name), projects in the order of the solution.</summary>
        private static void SortFolders(ExplorerNode node)
        {
            var folders = node.Children.Where(c => c.Kind == ExplorerKind.SolutionFolder).OrderBy(c => c.Text, StringComparer.OrdinalIgnoreCase).ToList();
            var rest = node.Children.Where(c => c.Kind != ExplorerKind.SolutionFolder).ToList();
            node.Children.Clear();
            foreach (var f in folders) { SortFolders(f); node.Children.Add(f); }
            foreach (var r in rest) node.Children.Add(r);
        }

        private ExplorerNode ProjectNode(Workspace workspace, LoadedProject project, bool isStartup, bool isActive)
        {
            string kind = project.Project.Type == OutputType.Library ? "library" : "program";
            var node = new ExplorerNode
            {
                Kind = ExplorerKind.Project, Text = project.Name, Suffix = kind + (isStartup ? ", start project" : "") + (project.Problems.Count > 0 ? ", problems" : ""),
                Path = project.FilePath, Project = project, IsBold = isStartup, IsActive = isActive, Key = "prj:" + project.FilePath,
            };
            if (project.Project.References.Count > 0)
            {
                var references = new ExplorerNode { Kind = ExplorerKind.References, Text = "References", Project = project, Key = "ref:" + project.FilePath };
                foreach (var r in project.Project.References)
                {
                    string text = r.IsProject ? Path.GetFileNameWithoutExtension(r.Project!) : r.Package!;
                    references.Children.Add(new ExplorerNode { Kind = ExplorerKind.Reference, Text = text, Suffix = r.IsProject ? "project" : "package" + (r.Version != null ? " " + r.Version : ""), Project = project, Reference = r, Key = $"ref:{project.FilePath}:{text}" });
                }
                node.Children.Add(references);
            }
            // the files as a tree of folders below the project folder (files from elsewhere under ".."); the content files (resources, C++ sources, notes) next to them, empty folders too
            var folders = new Dictionary<string, ExplorerNode>(StringComparer.Ordinal);
            ExplorerNode FolderOf(string[] parts, int count, HashSet<string>? hidden = null)
            {
                var parent = node;
                string accumulated = "";
                for (int i = 0; i < count; i++)
                {
                    accumulated += parts[i] + "/";
                    string folderKey = project.FilePath + "|" + accumulated;
                    if (!folders.TryGetValue(folderKey, out var folder))
                    {
                        folder = new ExplorerNode
                        {
                            Kind = ExplorerKind.Folder, Text = parts[i], Project = project, Key = "dir:" + folderKey, Path = Path.Combine(project.Directory, accumulated.TrimEnd('/')),
                            IsHidden = hidden?.Contains(accumulated.TrimEnd('/')) == true,
                        };
                        folders[folderKey] = folder;
                        parent.Children.Add(folder);
                    }
                    parent = folder;
                }
                return parent;
            }
            // the notes folder is always there: before its first use as a pale row (a double click makes it, with the overview)
            string notesDir = ProjectNotes.Folder(project.Project);
            string notesRel = ProjectFiles.Relative(project.Directory, notesDir);
            if (!notesRel.StartsWith("..") && !Path.IsPathRooted(notesRel) && notesRel != ".")
            {
                var parts = notesRel.Split('/');
                FolderOf(parts, parts.Length - 1);
                string notesKey = project.FilePath + "|" + notesRel + "/";
                var parent = parts.Length == 1 ? node : folders[project.FilePath + "|" + string.Join("/", parts[..^1]) + "/"];
                var notes = new ExplorerNode
                {
                    Kind = ExplorerKind.Folder, Text = parts[^1], Project = project, Key = "dir:" + notesKey, Path = notesDir, IsNotes = true, IsVirtual = !Directory.Exists(notesDir),
                };
                folders[notesKey] = notes;
                parent.Children.Add(notes);
            }
            foreach (var folder in project.Folders) { var parts = folder.Split('/'); FolderOf(parts, parts.Length); }
            foreach (var file in project.Files)
            {
                string rel = ProjectFiles.Relative(project.Directory, file);
                string[] parts = rel.Split('/');
                bool isEntry = project.Project.Type == OutputType.Exe && ProjectFiles.PathComparer.Equals(file, project.Files.LastOrDefault());
                FolderOf(parts, parts.Length - 1).Children.Add(new ExplorerNode { Kind = ExplorerKind.File, Text = parts[^1], Suffix = isEntry && project.Files.Count > 1 ? "entry" : "", Path = file, Project = project, Key = "file:" + file });
            }
            foreach (var file in project.ContentFiles)
            {
                string rel = ProjectFiles.Relative(project.Directory, file);
                string[] parts = rel.Split('/');
                FolderOf(parts, parts.Length - 1).Children.Add(new ExplorerNode { Kind = ExplorerKind.Content, Text = parts[^1], Path = file, Project = project, Key = "content:" + file });
            }
            // Show All Files: what the project leaves out (bin, obj, hidden files), pale
            if (ShowAllFiles)
            {
                var (hiddenFolders, hiddenFiles) = HiddenOf(project);
                var hidden = new HashSet<string>(hiddenFolders, StringComparer.Ordinal);
                foreach (var folder in hiddenFolders) { var parts = folder.Split('/'); FolderOf(parts, parts.Length, hidden); }
                foreach (var rel in hiddenFiles)
                {
                    string[] parts = rel.Split('/');
                    string file = Path.GetFullPath(rel, project.Directory);
                    FolderOf(parts, parts.Length - 1, hidden).Children.Add(new ExplorerNode { Kind = ExplorerKind.Content, Text = parts[^1], Path = file, Project = project, Key = "hidden:" + file, IsHidden = true });
                }
            }
            SortProjectChildren(node);
            NestMarkup(node);
            return node;
        }

        /// <summary>The markup of an interface (`X.fxml`) opens like a folder: its first row is "Designer" (opens the markup editor, as the file does), below it
        /// the code-behind `X.fire` of the same folder if there is one.</summary>
        private void NestMarkup(ExplorerNode parent)
        {
            foreach (var folder in parent.Children.Where(c => c.Kind == ExplorerKind.Folder).ToList()) NestMarkup(folder);
            foreach (var markup in parent.Children.Where(c => c.Kind == ExplorerKind.File && c.Path != null && FireProject.IsMarkupFile(c.Path)).ToList())
            {
                markup.Children.Add(new ExplorerNode { Kind = ExplorerKind.Designer, Text = "Designer", Path = markup.Path, Project = markup.Project, Key = "designer:" + markup.Path });
                string codeBehind = System.IO.Path.ChangeExtension(markup.Path!, ".fire");
                var code = parent.Children.FirstOrDefault(c => c.Kind == ExplorerKind.File && c.Path != null && ProjectFiles.PathComparer.Equals(c.Path, codeBehind));
                if (code != null)
                {
                    parent.Children.Remove(code);
                    markup.Children.Add(code);
                }
                // closed the first time it is seen
                if (_seenMarkup.Add(markup.Key)) _collapsed.Add(markup.Key);
            }
        }

        private readonly HashSet<string> _seenMarkup = new();

        // what a project leaves out, read once per reading of its folder (LoadedProject.Refresh makes a new list of folders)
        private readonly ConditionalWeakTable<LoadedProject, Tuple<IReadOnlyList<string>, (List<string> Folders, List<string> Files)>> _hidden = new();

        private (List<string> Folders, List<string> Files) HiddenOf(LoadedProject project)
        {
            if (_hidden.TryGetValue(project, out var known) && ReferenceEquals(known.Item1, project.Folders)) return known.Item2;
            var entries = ProjectFiles.HiddenEntries(project.Directory);
            _hidden.AddOrUpdate(project, Tuple.Create(project.Folders, entries));
            return entries;
        }

        /// <summary>Puts the git state on the files, and a mark on every folder, project and solution that has a changed file below it (and takes away the marks of what is no longer changed).</summary>
        private bool MarkGit(IEnumerable<ExplorerNode> nodes)
        {
            bool any = false;
            foreach (var n in nodes)
            {
                var state = _git != null && n.Kind is ExplorerKind.File or ExplorerKind.Content && n.Path != null ? GitRepository.StateIn(_git, System.IO.Path.GetFullPath(n.Path)) : null;
                n.GitState = state;
                bool below = MarkGit(n.Children);
                n.GitChangedBelow = below;
                any |= below || state != null;
            }
            return any;
        }

        /// <summary>
        /// Only the state of git changed (a refresh of git, not of the workspace): the marks are put on the rows that are there, without building the tree anew - a project with thousands of
        /// files would take seconds for that. Another branch (the solution's row shows it) or no tree yet: the whole tree.
        /// </summary>
        public void UpdateGit(Workspace workspace, string? activeFile, IReadOnlyDictionary<string, GitFileState>? git, string? branch)
        {
            if (!ReferenceEquals(workspace, _workspace) || branch != _branch || (git == null) != (_git == null) || !string.Equals(activeFile, _activeFile, ProjectFiles.PathComparison) || _roots.Count == 0)
            {
                Refresh(workspace, activeFile, git, branch);
                return;
            }
            _git = git;
            MarkGit(_roots);
        }

        private void RememberCollapsed(IEnumerable<ExplorerNode> nodes)
        {
            foreach (var n in nodes)
            {
                if (!n.IsExpanded) _collapsed.Add(n.Key); else _collapsed.Remove(n.Key);
                RememberCollapsed(n.Children);
            }
        }

        private void ApplyCollapsed(IEnumerable<ExplorerNode> nodes)
        {
            foreach (var n in nodes)
            {
                n.IsExpanded = !_collapsed.Contains(n.Key);
                ApplyCollapsed(n.Children);
            }
        }

        // ---- context menu ----------------------------------------------------------------------------------------------------------------------
        private (ExplorerNode Node, bool Expanded)? _expandedAtPress;

        /// <summary>Whether a double click opens the row (else it only folds: a folder, the references).</summary>
        private static bool Opens(ExplorerNode node) =>
            node is { Kind: ExplorerKind.File or ExplorerKind.Content or ExplorerKind.Designer, Path: not null } or { Kind: ExplorerKind.Project or ExplorerKind.Solution } or { IsNotes: true };

        /// <summary>Opens what the row stands for; false: the row only folds (a folder, the references).</summary>
        private bool Open(ExplorerNode node)
        {
            switch (node)
            {
                case { Kind: ExplorerKind.File or ExplorerKind.Content or ExplorerKind.Designer, Path: { } path }: FileOpenRequested?.Invoke(path); return true;
                case { Kind: ExplorerKind.Project }: CommandRequested?.Invoke("properties", node); return true;
                case { Kind: ExplorerKind.Solution }: CommandRequested?.Invoke("solution-properties", node); return true;
                case { IsNotes: true }: CommandRequested?.Invoke("open-notes", node); return true;
                default: return false;
            }
        }

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (e.ClickCount == 1 && (e.Source as Visual)?.FindAncestorOfType<TreeViewItem>()?.DataContext is ExplorerNode pressed) _expandedAtPress = (pressed, pressed.IsExpanded);
            if (!e.GetCurrentPoint(_tree).Properties.IsRightButtonPressed) return;
            if ((e.Source as Visual)?.FindAncestorOfType<TreeViewItem>()?.DataContext is ExplorerNode node) _tree.SelectedItem = node;
        }

        private MenuItem Item(string header, string command, ExplorerNode? node)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => CommandRequested?.Invoke(command, node);
            return item;
        }

        /// <summary>The submenu "Add New": the commands are "new:" + kind (see MainWindow.AddNewFile): `dialog` (the list of code templates), `t:Title` (one template, only the name is asked), markdown, image, file.</summary>
        private MenuItem AddNewMenu(ExplorerNode node, string header = "New")
        {
            var menu = new MenuItem { Header = header };
            foreach (var item in NewFileItems(node)) menu.Items.Add(item);
            return menu;
        }

        /// <summary>The entries of "New" (the context menu and the New File button of the tool bar) for the folder or project `node` (null: the window's project).</summary>
        private IEnumerable<Control> NewFileItems(ExplorerNode? node) =>
            NewFileKinds.Select(entry => entry == null ? (Control)new Separator() : Item(entry.Value.Title, "new:" + entry.Value.Kind, node));

        /// <summary>What can be added as a new file: the title in the menu and the kind; null is a separator.</summary>
        public static readonly (string Title, string Kind)?[] NewFileKinds =
        {
            ("Script...", "dialog"),
            null,
            ("Fire Class", "t:Fire Class"),
            ("FXML Window", "t:FXML Window"),
            ("FXML View", "t:FXML View"),
            ("Circuit", "t:Circuit"),
            null,
            ("Markdown Document...", "markdown"),
            ("Raster Image...", "image"),
            ("Other File...", "file"),
        };

        private void AddGitFileItems(ExplorerNode node)
        {
            if (node.GitState != null)
            {
                _menu.Items.Add(Item("Git: Show Changes", "git-diff", node));
                _menu.Items.Add(Item("Git: Commit This File...", "git-commit", node));
                _menu.Items.Add(Item("Git: Discard Changes...", "git-discard", node));
            }
            _menu.Items.Add(Item("Git: History of This File", "git-history-file", node));
            _menu.Items.Add(new Separator());
        }

        private bool FillMenu()
        {
            _menu.Items.Clear();
            var node = Selected;
            if (node == null) return false;
            void Add(string header, string command) => _menu.Items.Add(Item(header, command, node));
            switch (node.Kind)
            {
                case ExplorerKind.Solution:
                    if (_git != null) { Add("Git: Commit...", "git-commit"); Add("Git: Pull", "git-pull"); Add("Git: Push", "git-push"); _menu.Items.Add(new Separator()); }
                    else Add("Git: Create Repository...", "git-init");
                    Add("New Project...", "add-new-project");
                    Add("Add Existing Project...", "add-existing-project");
                    Add("New Folder...", "new-folder");
                    _menu.Items.Add(new Separator());
                    Add("Build Solution", "build-solution");
                    Add("Clean Solution", "clean-solution");
                    _menu.Items.Add(new Separator());
                    Add("Open Solution Folder", "reveal");
                    Add("Solution Properties...", "solution-properties");
                    _menu.Items.Add(new Separator());
                    Add("Close Solution", "close-workspace");
                    break;
                case ExplorerKind.SolutionFolder:
                    Add("New Project Here...", "add-new-project");
                    Add("Add Existing Project...", "add-existing-project");
                    Add("New Folder...", "new-folder");
                    _menu.Items.Add(new Separator());
                    Add("Open Folder", "reveal");
                    Add("Remove Folder from Solution", "remove-folder");
                    break;
                case ExplorerKind.Project:
                    _menu.Items.Add(AddNewMenu(node));
                    Add("Add Existing File...", "add-existing-file");
                    Add("Add Resource (copy a file in)...", "add-resource");
                    Add("New Folder...", "new-project-folder");
                    Add(node.Project!.Project.Native == null ? "Add Native Code (C++)" : "Add C++ File...", "add-native");
                    Add("Add Reference...", "add-reference");
                    _menu.Items.Add(new Separator());
                    if (node.Project!.Project.Type == OutputType.Exe)
                    {
                        Add("Start Debugging", "run-project");
                        if (_hasSolution && !node.IsBold) Add("Set as Start Project", "set-startup");
                    }
                    Add("Build", "build-project");
                    Add("Clean", "clean-project");
                    if (node.Project.Project.Type == OutputType.Library) { Add("Pack as Package (.fpk)...", "pack"); Add("Install (pack and install)", "install-library"); }
                    _menu.Items.Add(new Separator());
                    Add("Reload (read the folder again)", "refresh");
                    Add("Open Project Folder", "reveal");
                    Add("Remove from Solution", "remove");
                    Add("Properties...", "properties");
                    break;
                case ExplorerKind.References:
                    Add("Add Reference...", "add-reference");
                    break;
                case ExplorerKind.Reference:
                    Add("Remove Reference", "remove");
                    break;
                case ExplorerKind.Folder when node.IsHidden:
                    Add("Open Folder", "reveal");
                    break;
                case ExplorerKind.Folder when node.IsNotes:
                    Add("Open Overview", "open-notes");
                    Add("Todo List", "open-todo");
                    if (node.IsVirtual) break;
                    _menu.Items.Add(new Separator());
                    _menu.Items.Add(AddNewMenu(node));
                    Add("Open Folder", "reveal");
                    break;
                case ExplorerKind.Folder:
                    _menu.Items.Add(AddNewMenu(node));
                    Add("Add Resource Here (copy a file in)...", "add-resource");
                    Add("New Folder...", "new-project-folder");
                    Add("Open Folder", "reveal");
                    break;
                case ExplorerKind.Content:
                    if (_git != null) AddGitFileItems(node);
                    Add("Open", "open");
                    if (node.Path != null && new[] { ".png", ".bmp", ".gif" }.Contains(System.IO.Path.GetExtension(node.Path).ToLowerInvariant())) Add("Edit Pixels", "open-pixel");
                    Add("Open as Text", "open-text");
                    Add("Open as Hex", "open-hex");
                    Add("Show in Folder", "reveal");
                    Add("Delete File...", "delete-file");
                    break;
                case ExplorerKind.Designer:
                    Add("Open Designer", "open");
                    break;
                case ExplorerKind.File:
                    if (_git != null) AddGitFileItems(node);
                    Add("Open", "open");
                    Add("Open as Hex", "open-hex");
                    Add("Remove from Project", "remove");
                    Add("Show in Folder", "reveal");
                    break;
            }
            return _menu.Items.Count > 0;
        }
    }
}
