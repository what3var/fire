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
using fire.Projects;

namespace fire.Editor
{
    internal enum ExplorerKind { Solution, Project, References, Reference, Folder, File }

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
            _tree.DoubleTapped += (_, e) => { if (_tree.SelectedItem is ExplorerNode { Kind: ExplorerKind.File, Path: { } p }) { FileOpenRequested?.Invoke(p); e.Handled = true; } };
            _tree.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && _tree.SelectedItem is ExplorerNode { Kind: ExplorerKind.File, Path: { } p }) { FileOpenRequested?.Invoke(p); e.Handled = true; }
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
            if (node.Suffix.Length > 0)
                panel.Children.Add(new TextBlock { Text = node.Suffix, Foreground = EditorTheme.TextDim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }

        // ---- building the tree ----------------------------------------------------------------------------------------------------------------
        /// <summary>Shows the workspace; `activeFile` is the file of the active document (its project is marked).</summary>
        public void Refresh(Workspace workspace, string? activeFile)
        {
            RememberCollapsed(_roots);
            _roots.Clear();
            if (!workspace.IsOpen) { Show(false); return; }
            Show(true);

            var activeProject = activeFile != null ? workspace.FindProjectOf(activeFile) : null;
            var startup = workspace.Startup;
            ExplorerNode? solutionNode = null;
            if (workspace.Solution != null)
            {
                solutionNode = new ExplorerNode { Kind = ExplorerKind.Solution, Text = $"Solution '{workspace.Solution.Name}'", Suffix = $"{workspace.Projects.Count} project{(workspace.Projects.Count == 1 ? "" : "s")}", Path = workspace.Solution.FilePath, Key = "sln", IsBold = true };
                _roots.Add(solutionNode);
            }
            foreach (var project in workspace.Projects)
            {
                var node = ProjectNode(workspace, project, ReferenceEquals(project, startup) && workspace.Solution != null, ReferenceEquals(project, activeProject));
                if (solutionNode != null) solutionNode.Children.Add(node); else _roots.Add(node);
            }
            ApplyCollapsed(_roots);
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
            // the files as a tree of folders below the project folder (files from elsewhere under "..")
            var folders = new Dictionary<string, ExplorerNode>(StringComparer.Ordinal);
            foreach (var file in project.Files)
            {
                string rel = ProjectFiles.Relative(project.Directory, file);
                var parent = node;
                string[] parts = rel.Split('/');
                string accumulated = "";
                for (int i = 0; i < parts.Length - 1; i++)
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
                bool isEntry = project.Project.Type == OutputType.Exe && ProjectFiles.PathComparer.Equals(file, project.Files.LastOrDefault());
                parent.Children.Add(new ExplorerNode { Kind = ExplorerKind.File, Text = parts[^1], Suffix = isEntry && project.Files.Count > 1 ? "entry" : "", Path = file, Project = project, Key = "file:" + file });
            }
            return node;
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

        private bool FillMenu()
        {
            _menu.Items.Clear();
            var node = Selected;
            if (node == null) return false;
            void Add(string header, string command) => _menu.Items.Add(Item(header, command, node));
            switch (node.Kind)
            {
                case ExplorerKind.Solution:
                    Add("Add New Project...", "add-new-project");
                    Add("Add Existing Project...", "add-existing-project");
                    _menu.Items.Add(new Separator());
                    Add("Open Solution Folder", "reveal");
                    Add("Solution Properties...", "solution-properties");
                    _menu.Items.Add(new Separator());
                    Add("Close Solution", "close-workspace");
                    break;
                case ExplorerKind.Project:
                    Add("Add New File...", "add-new-file");
                    Add("Add Existing File...", "add-existing-file");
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
                    Add("Add New File Here...", "add-new-file");
                    Add("Open Folder", "reveal");
                    break;
                case ExplorerKind.File:
                    Add("Open", "open");
                    Add("Remove from Project", "remove");
                    Add("Show in Folder", "reveal");
                    break;
            }
            return _menu.Items.Count > 0;
        }
    }
}
