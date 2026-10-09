using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using fire.Git;
using fire.Projects;

namespace fire.Editor
{
    internal enum ExplorerKind { Solution, SolutionFolder, Project, References, Reference, Folder, File, Content }

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
        /// <summary>The git state of a file (null: none), and for a folder, project or solution whether something below it is changed.</summary>
        public GitFileState? GitState { get; set; }
        public bool GitChangedBelow { get; set; }
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
    }

    /// <summary>
    /// The solution explorer (docs/PROJECTS.md): the open solution (or the single project) with its projects, their references and files. A double click opens a file; the context menus
    /// ask the window for the commands (new file, add reference, startup project, properties, ...). The window refreshes it when the workspace or the active document changes.
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

        /// <summary>A file was double clicked (or Enter was pressed on it).</summary>
        public event Action<string>? FileOpenRequested;

        /// <summary>A command of a context menu or of the empty page: its id and the row it concerns (null: none).</summary>
        public event Action<string, ExplorerNode?>? CommandRequested;

        public SolutionExplorerControl()
        {
            _tree.ItemsSource = _roots;
            _tree.ItemTemplate = new FuncTreeDataTemplate<ExplorerNode>((node, _) => Row(node), node => node.Children);
            _tree.Styles.Add(new Style(x => x.OfType<TreeViewItem>())
            {
                Setters = { new Setter(TreeViewItem.IsExpandedProperty, new Binding("IsExpanded") { Mode = BindingMode.TwoWay }) },
            });
            _tree.ContextMenu = _menu;
            _menu.Opening += (_, e) => { if (!FillMenu()) e.Cancel = true; };
            _tree.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            _tree.DoubleTapped += (_, e) => { if (_tree.SelectedItem is ExplorerNode { Kind: ExplorerKind.File or ExplorerKind.Content, Path: { } p }) { FileOpenRequested?.Invoke(p); e.Handled = true; } };
            _tree.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && _tree.SelectedItem is ExplorerNode { Kind: ExplorerKind.File or ExplorerKind.Content, Path: { } p }) { FileOpenRequested?.Invoke(p); e.Handled = true; }
                else if (e.Key == Key.Delete && _tree.SelectedItem is ExplorerNode node) { CommandRequested?.Invoke("remove", node); e.Handled = true; }
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

            Content = new Grid { Children = { _tree, _empty } };
            Show(false);
        }

        private void Show(bool hasContent)
        {
            _tree.IsVisible = hasContent;
            _empty.IsVisible = !hasContent;
        }

        /// <summary>The row that is selected.</summary>
        public ExplorerNode? Selected => _tree.SelectedItem as ExplorerNode;

        private static Control Row(ExplorerNode node)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            panel.Children.Add(new TextBlock
            {
                Text = node.Text,
                FontWeight = node.IsBold ? FontWeight.Bold : FontWeight.Normal,
                Foreground = node.IsActive ? EditorTheme.Solid(EditorTheme.AccentTextColor) : node.Kind == ExplorerKind.References ? EditorTheme.TextDim : EditorTheme.Text,
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (node.GitState is { } gs)
                panel.Children.Add(new TextBlock { Text = GitMark(gs), Foreground = GitBrush(gs), FontWeight = FontWeight.Bold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            else if (node.GitChangedBelow)
                panel.Children.Add(new TextBlock { Text = "●", Foreground = GitBrush(GitFileState.Modified), FontSize = 9, VerticalAlignment = VerticalAlignment.Center });
            if (node.Suffix.Length > 0)
                panel.Children.Add(new TextBlock { Text = node.Suffix, Foreground = EditorTheme.TextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        private static string GitMark(GitFileState s) => s switch
        {
            GitFileState.Modified => "M", GitFileState.Added => "A", GitFileState.Untracked => "U", GitFileState.Deleted => "D", GitFileState.Renamed => "R", GitFileState.Conflicted => "!", _ => "",
        };

        private static readonly IBrush GitModified = new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x40));
        private static readonly IBrush GitNew = new SolidColorBrush(Color.FromRgb(0x7F, 0xD9, 0x7F));
        private static readonly IBrush GitBad = new SolidColorBrush(Color.FromRgb(0xE8, 0x6A, 0x6A));

        private static IBrush GitBrush(GitFileState s) => s switch
        {
            GitFileState.Added or GitFileState.Untracked => GitNew, GitFileState.Deleted or GitFileState.Conflicted => GitBad, _ => GitModified,
        };

        // ---- building the tree ----------------------------------------------------------------------------------------------------------------
        /// <summary>Shows the workspace; `activeFile` is the file of the active document (its project is marked).</summary>
        public void Refresh(Workspace workspace, string? activeFile, IReadOnlyDictionary<string, GitFileState>? git = null, string? branch = null)
        {
            _git = git;
            _branch = branch;
            RememberCollapsed(_roots);
            _roots.Clear();
            if (!workspace.IsOpen) { Show(false); return; }
            Show(true);

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
                Kind = ExplorerKind.Project, Text = project.Name, Suffix = kind + (isStartup ? ", startup" : "") + (project.Problems.Count > 0 ? ", problems" : ""),
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
            ExplorerNode FolderOf(string[] parts, int count)
            {
                var parent = node;
                string accumulated = "";
                for (int i = 0; i < count; i++)
                {
                    accumulated += parts[i] + "/";
                    string folderKey = project.FilePath + "|" + accumulated;
                    if (!folders.TryGetValue(folderKey, out var folder))
                    {
                        folder = new ExplorerNode { Kind = ExplorerKind.Folder, Text = parts[i], Project = project, Key = "dir:" + folderKey, Path = Path.Combine(project.Directory, accumulated.TrimEnd('/')) };
                        folders[folderKey] = folder;
                        parent.Children.Add(folder);
                    }
                    parent = folder;
                }
                return parent;
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
            SortProjectChildren(node);
            return node;
        }

        /// <summary>Puts the git state on the files, and a mark on every folder, project and solution that has a changed file below it.</summary>
        private bool MarkGit(IEnumerable<ExplorerNode> nodes)
        {
            bool any = false;
            foreach (var n in nodes)
            {
                if (n.Kind is ExplorerKind.File or ExplorerKind.Content && n.Path != null && _git!.TryGetValue(System.IO.Path.GetFullPath(n.Path), out var state)) { n.GitState = state; any = true; }
                if (MarkGit(n.Children)) { n.GitChangedBelow = true; any = true; }
            }
            return any;
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
        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
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
            foreach (var entry in NewFileKinds)
            {
                if (entry == null) menu.Items.Add(new Separator());
                else menu.Items.Add(Item(entry.Value.Title, "new:" + entry.Value.Kind, node));
            }
            return menu;
        }

        /// <summary>What can be added as a new file: the title in the menu and the kind; null is a separator.</summary>
        public static readonly (string Title, string Kind)?[] NewFileKinds =
        {
            ("Script...", "dialog"),
            null,
            ("Fire Class", "t:Fire Class"),
            ("FXML Window", "t:FXML Window"),
            ("FXML View", "t:FXML View"),
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
                    if (node.Project!.Project.Type == OutputType.Exe) Add("Set as Startup Project", "set-startup");
                    if (node.Project.Project.Type == OutputType.Library) Add("Pack as Package (.fpk)...", "pack");
                    Add("Build / Run This Project", "run-project");
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
