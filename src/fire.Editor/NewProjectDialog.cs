using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using fire.Projects;

namespace fire.Editor
{
    /// <summary>
    /// "New Solution" and "New Project" (docs/PROJECTS.md): a name, a place and a template from a list.
    ///
    /// A solution lives in its own folder (proposed: `$HOME/spark/{name}`) and a template other than "Empty" makes a project of the same name in `{folder}/{name}`.
    /// A project is made in a folder of its own, `{location}/{name}`: the location is the solution folder (proposed), a folder of the solution, or - without a solution - `$HOME/spark`.
    /// The result is in <see cref="Name"/>, <see cref="Location"/> (the solution folder, or the folder that gets the project's folder) and <see cref="Template"/>.
    /// </summary>
    internal sealed class NewProjectDialog : Window
    {
        private readonly bool _solution;
        private readonly TextBox _name = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBox _location = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ListBox _templates = new() { MinHeight = 150 };
        private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.TextDim, FontSize = 12 };
        private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.TextDim, FontSize = 11 };
        private readonly TextBlock _error = new() { Foreground = EditorTheme.ErrorMark, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        private readonly IReadOnlyList<ProjectTemplate> _list;
        private string _lastName;

        public string Name { get; private set; } = "";
        public string Location { get; private set; } = "";
        public ProjectTemplate? Template { get; private set; }

        /// <param name="solution">true: a new solution (the location is its folder); false: a new project.</param>
        /// <param name="location">for a project: the folder that is proposed (the solution folder, or null for `$HOME/spark`); for a solution: the folder that holds the folders of solutions.</param>
        public NewProjectDialog(bool solution, string? location = null)
        {
            _solution = solution;
            _list = solution ? ProjectTemplates.ForSolution : ProjectTemplates.ForProject;
            Title = solution ? "New Solution" : "New Project";
            Width = 560; Height = 560;
            MinWidth = 440; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            string parent = location ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "spark");
            _lastName = solution ? "MySolution" : "MyProject";
            _name.Text = _lastName;
            _location.Text = solution ? Path.Combine(parent, _lastName) : parent;

            _templates.ItemsSource = _list.Select(t => t.Name).ToList();
            _templates.SelectedIndex = solution ? 1 : 0;   // a solution starts with a terminal program: the usual thing
            _templates.SelectionChanged += (_, _) => UpdateText();
            _name.TextChanged += (_, _) => OnNameChanged();
            _location.TextChanged += (_, _) => UpdateText();

            var browse = new Button { Content = "Browse..." };
            browse.Click += async (_, _) => await Browse();
            var locationRow = new DockPanel();
            DockPanel.SetDock(browse, Avalonia.Controls.Dock.Right);
            browse.Margin = new Thickness(6, 0, 0, 0);
            locationRow.Children.Add(browse);
            locationRow.Children.Add(_location);

            var ok = new Button { Content = "Create", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Classes.Add("accent");
            ok.Click += (_, _) => Accept();
            var cancel = new Button { Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => Close(false);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { ok, cancel } };

            var top = new StackPanel { Spacing = 8 };
            top.Children.Add(Labeled("Name", _name));
            top.Children.Add(Labeled(solution ? "Solution folder" : "Location (the project gets a folder of its own here)", locationRow));
            top.Children.Add(new TextBlock { Text = solution ? "Template of the first project" : "Template", Foreground = EditorTheme.TextDim, Margin = new Thickness(0, 6, 0, 0) });

            var bottom = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            bottom.Children.Add(_description);
            bottom.Children.Add(_preview);
            bottom.Children.Add(_error);
            bottom.Children.Add(buttons);

            var root = new DockPanel { Margin = new Thickness(14) };
            DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
            root.Children.Add(top);
            root.Children.Add(bottom);
            root.Children.Add(_templates);
            Content = root;
            Opened += (_, _) => { _name.Focus(); _name.SelectAll(); };
            UpdateText();
        }

        private static Control Labeled(string label, Control control) =>
            new StackPanel { Spacing = 2, Children = { new TextBlock { Text = label, Foreground = EditorTheme.TextDim }, control } };

        /// <summary>The solution folder (and its default) follows the name as long as the last part of the path is the name.</summary>
        private void OnNameChanged()
        {
            string name = (_name.Text ?? "").Trim();
            if (_solution && !string.IsNullOrEmpty(_lastName) && (_location.Text ?? "").TrimEnd('/', '\\') is { Length: > 0 } text && Path.GetFileName(text) == _lastName)
            {
                string? dir = Path.GetDirectoryName(text);
                if (dir != null && name.Length > 0 && ProjectTemplates.CheckName(name) == null) _location.Text = Path.Combine(dir, name);
            }
            _lastName = name;
            UpdateText();
        }

        private ProjectTemplate? Selected => _templates.SelectedIndex >= 0 ? _list[_templates.SelectedIndex] : null;

        private void UpdateText()
        {
            var t = Selected;
            _description.Text = t?.Description ?? "";
            string name = (_name.Text ?? "").Trim();
            string location = (_location.Text ?? "").Trim();
            if (location.Length == 0 || name.Length == 0) { _preview.Text = ""; return; }
            _preview.Text = _solution
                ? $"{Path.Combine(location, name + FireSolution.Extension)}" + (t is { MakesProject: true } ? $"\n{Path.Combine(location, name, name + FireProject.Extension)}" : "")
                : Path.Combine(location, name, name + FireProject.Extension);
        }

        private async Task Browse()
        {
            string start = (_location.Text ?? "").Trim();
            var startFolder = start.Length > 0 && Directory.Exists(start) ? await StorageProvider.TryGetFolderFromPathAsync(start)
                : Directory.GetParent(start) is { Exists: true } parent ? await StorageProvider.TryGetFolderFromPathAsync(parent.FullName) : null;
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = _solution ? "Folder that holds the solution folder" : "Location of the project", SuggestedStartLocation = startFolder });
            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
            // a solution gets a folder of its own below the chosen one; a project's folder is made below the chosen location
            _location.Text = _solution ? Path.Combine(path, (_name.Text ?? "").Trim()) : path;
        }

        private void Accept()
        {
            string name = (_name.Text ?? "").Trim();
            string location = (_location.Text ?? "").Trim();
            string? problem = ProjectTemplates.CheckName(name);
            if (problem == null && location.Length == 0) problem = "The location is empty.";
            if (problem == null && Selected == null) problem = "Choose a template.";
            if (problem == null)
            {
                try { location = Path.GetFullPath(location); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { problem = "The location is not a path: " + ex.Message; }
            }
            if (problem == null)
            {
                string target = _solution ? Path.Combine(location, name + FireSolution.Extension) : Path.Combine(location, name, name + FireProject.Extension);
                if (File.Exists(target)) problem = $"'{target}' exists already.";
                else if (_solution && Selected is { MakesProject: true } && File.Exists(Path.Combine(location, name, name + FireProject.Extension))) problem = $"'{Path.Combine(location, name, name + FireProject.Extension)}' exists already.";
            }
            if (problem != null) { _error.Text = problem; _error.IsVisible = true; return; }
            Name = name; Location = location; Template = Selected;
            Close(true);
        }
    }
}
