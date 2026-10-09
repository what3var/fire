using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using fire.Projects;

namespace fire.Editor
{
    /// <summary>What a <see cref="TemplateDialog"/> makes.</summary>
    internal enum TemplateDialogMode
    {
        /// <summary>A new solution (with a project after a template): name, folder of the solution, template.</summary>
        Solution,
        /// <summary>A new project: name, location (the project gets a folder of its own there), template.</summary>
        Project,
        /// <summary>A new file after a code template, chosen from the list: name (the extension is shown behind it).</summary>
        Code,
        /// <summary>A new file after one given code template (Fire Class, FXML Window, ...): only the name.</summary>
        Named,
        /// <summary>Choose a template (code and project templates together) without making anything: to open or copy it. No name, no place.</summary>
        Pick,
    }

    /// <summary>
    /// "New Solution", "New Project" and "New File" (docs/TEMPLATES.md): the templates in ONE flat list (what ships with fire, the user's own and those of packages side by side) with a
    /// search bar; each shows an icon, its title, a short description and where it comes from ("Local" or "From Package 1.2.3"). Below: the name - for a file the extension is shown behind
    /// it and added automatically - and, for a project or a solution, the place. The result is in <see cref="Template"/>, <see cref="Name"/> and <see cref="Location"/>.
    /// </summary>
    internal sealed class TemplateDialog : Window
    {
        private readonly TemplateDialogMode _mode;
        private readonly IReadOnlyList<FireTemplate> _all;
        private readonly TextBox _search = new() { Watermark = "Search templates", HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly ListBox _list = new() { MinHeight = 250, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBox _name = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock _extension = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = EditorTheme.TextDim, Margin = new Thickness(4, 0, 0, 0) };
        private readonly TextBox _location = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.TextDim, FontSize = 11 };
        private readonly TextBlock _error = new() { Foreground = EditorTheme.ErrorMark, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        private bool _solution => _mode == TemplateDialogMode.Solution;
        private bool _file => _mode is TemplateDialogMode.Code or TemplateDialogMode.Named;
        private bool _pick => _mode == TemplateDialogMode.Pick;
        private bool _nameEdited;
        private bool _settingName;
        private string _lastName;
        private FireTemplate? _fixed;

        public FireTemplate? Template { get; private set; }
        /// <summary>The name as typed (for a file without the extension).</summary>
        public string Name { get; private set; } = "";
        /// <summary>The place: the folder of the solution, or the folder that gets the folder of the project (not used for a file).</summary>
        public string Location { get; private set; } = "";

        /// <param name="catalog">where the templates come from.</param>
        /// <param name="location">for a project: the folder that is proposed (the solution folder, or null for `$HOME/spark`); for a solution: the folder that holds the folders of solutions.</param>
        /// <param name="preselect">the title of the template that is selected at first.</param>
        /// <param name="template">for <see cref="TemplateDialogMode.Named"/>: the template.</param>
        /// <param name="title">for <see cref="TemplateDialogMode.Pick"/>: the title of the window and the text of the button (`Open`).</param>
        public TemplateDialog(TemplateDialogMode mode, TemplateCatalog catalog, string? location = null, string? preselect = null, FireTemplate? template = null, string? title = null, string? button = null)
        {
            _mode = mode;
            _fixed = template;
            _all = mode switch
            {
                TemplateDialogMode.Solution => catalog.Projects(includeEmpty: true).ToList(),
                TemplateDialogMode.Project => catalog.Projects(includeEmpty: false).ToList(),
                TemplateDialogMode.Code => catalog.Code.ToList(),
                TemplateDialogMode.Pick => catalog.All.ToList(),
                _ => new[] { template ?? throw new ArgumentNullException(nameof(template)) },
            };
            Title = mode switch { TemplateDialogMode.Solution => "New Solution", TemplateDialogMode.Project => "New Project", TemplateDialogMode.Code => "New File", TemplateDialogMode.Pick => title ?? "Choose a Template", _ => "New " + template!.Title };
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            if (mode == TemplateDialogMode.Named) { SizeToContent = SizeToContent.WidthAndHeight; CanResize = false; MinWidth = 420; }
            else { Width = 620; Height = _pick ? 520 : _file ? 560 : 660; MinWidth = 460; MinHeight = 400; }

            string parent = location ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "spark");
            _lastName = _solution ? "MySolution" : mode == TemplateDialogMode.Project ? "MyProject" : "";
            if (!_file && !_pick)
            {
                _name.Text = _lastName;
                _location.Text = _solution ? Path.Combine(parent, _lastName) : parent;
            }

            _list.ItemsSource = _all;
            _list.ItemTemplate = new FuncDataTemplate<FireTemplate>((t, _) => t == null ? new TextBlock() : Row(t));
            var start = (preselect != null ? _all.FirstOrDefault(t => string.Equals(t.Title, preselect, StringComparison.OrdinalIgnoreCase)) : null)
                ?? (_solution ? _all.FirstOrDefault(t => string.Equals(t.Title, "Terminal", StringComparison.OrdinalIgnoreCase)) : null) ?? _all.FirstOrDefault();
            _list.SelectedItem = start;
            ApplyDefaultName();
            _list.SelectionChanged += (_, _) => { ApplyDefaultName(); UpdateText(); };
            _search.TextChanged += (_, _) => Filter();
            _search.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Down) { _list.Focus(); e.Handled = true; }
                else if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            };
            _list.DoubleTapped += (_, _) => Accept();
            _name.TextChanged += (_, _) => OnNameChanged();
            _name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
            _location.TextChanged += (_, _) => UpdateText();

            var ok = new Button { Content = _pick ? (button ?? "Open") : "Create", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Classes.Add("accent");
            ok.Click += (_, _) => Accept();
            var cancel = new Button { Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => Close(false);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { ok, cancel } };

            // the name: for a file the extension stands right behind the box
            var nameRow = new DockPanel();
            if (_file && !string.IsNullOrEmpty(StartExtension)) { DockPanel.SetDock(_extension, Avalonia.Controls.Dock.Right); nameRow.Children.Add(_extension); }
            nameRow.Children.Add(_name);

            var top = new StackPanel { Spacing = 8 };
            if (mode == TemplateDialogMode.Named) top.Children.Add(new TextBlock { Text = template!.Description ?? template.Title, Foreground = EditorTheme.TextDim, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
            else top.Children.Add(_search);

            var bottom = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            if (!_pick) bottom.Children.Add(Labeled("Name", nameRow));
            if (!_file && !_pick)
            {
                var browse = new Button { Content = "Browse...", Margin = new Thickness(6, 0, 0, 0) };
                browse.Click += async (_, _) => await Browse();
                var locationRow = new DockPanel();
                DockPanel.SetDock(browse, Avalonia.Controls.Dock.Right);
                locationRow.Children.Add(browse);
                locationRow.Children.Add(_location);
                bottom.Children.Add(Labeled(_solution ? "Solution folder" : "Location (the project gets a folder of its own here)", locationRow));
            }
            bottom.Children.Add(_preview);
            bottom.Children.Add(_error);
            bottom.Children.Add(buttons);

            var root = new DockPanel { Margin = new Thickness(14) };
            DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
            DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
            root.Children.Add(top);
            root.Children.Add(bottom);
            if (mode != TemplateDialogMode.Named) { _list.Margin = new Thickness(0, 8, 0, 0); root.Children.Add(_list); }
            Content = root;
            Opened += (_, _) => { if (mode == TemplateDialogMode.Named || _file) { _name.Focus(); _name.SelectAll(); } else _search.Focus(); };
            if (_pick) bottom.Children.Remove(_preview);
            UpdateText();
        }

        private string StartExtension => (_list.SelectedItem as FireTemplate ?? _fixed)?.Extension ?? "";

        private static Control Labeled(string label, Control control) =>
            new StackPanel { Spacing = 2, Children = { new TextBlock { Text = label, Foreground = EditorTheme.TextDim }, control } };

        /// <summary>One entry of the list: the icon, the title, the description (if there is one) and the source.</summary>
        private static Control Row(FireTemplate t)
        {
            var text = new StackPanel { Spacing = 1, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = t.Title, FontWeight = FontWeight.SemiBold });
            if (t.Description != null) text.Children.Add(new TextBlock { Text = t.Description, TextWrapping = TextWrapping.Wrap, Foreground = EditorTheme.TextDim, FontSize = 12 });
            text.Children.Add(new TextBlock { Text = t.Source, Foreground = EditorTheme.Purple, FontSize = 11 });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(4, 5) };
            var icon = TemplateIcon.Create(t, 34);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 0, 0);
            grid.Children.Add(icon);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            return grid;
        }

        /// <summary>The search: every word has to be in the title, the description or the source.</summary>
        private void Filter()
        {
            string? search = _search.Text;
            var selected = _list.SelectedItem as FireTemplate;
            var shown = _all.Where(t => t.Matches(search)).ToList();
            _list.ItemsSource = shown;
            _list.SelectedItem = selected != null && shown.Contains(selected) ? selected : shown.FirstOrDefault();
        }

        private FireTemplate? Selected => _fixed ?? _list.SelectedItem as FireTemplate;

        /// <summary>A file proposes the name of its template as long as the user has not typed one.</summary>
        private void ApplyDefaultName()
        {
            if (!_file) return;
            var t = Selected;
            _extension.Text = t?.Extension ?? "";
            if (_nameEdited || t == null) return;
            _settingName = true;
            _name.Text = t.DefaultName;
            _settingName = false;
        }

        /// <summary>The folder of a solution (and its default) follows the name as long as the last part of the path is the name.</summary>
        private void OnNameChanged()
        {
            if (_settingName) return;
            _nameEdited = true;
            string name = (_name.Text ?? "").Trim();
            if (_solution && !string.IsNullOrEmpty(_lastName) && (_location.Text ?? "").TrimEnd('/', '\\') is { Length: > 0 } text && Path.GetFileName(text) == _lastName)
            {
                string? dir = Path.GetDirectoryName(text);
                if (dir != null && name.Length > 0 && ProjectTemplates.CheckName(name) == null) _location.Text = Path.Combine(dir, name);
            }
            _lastName = name;
            UpdateText();
        }

        /// <summary>The name without an extension that the template adds (a typed `.script` is not doubled).</summary>
        private string NameOnly(FireTemplate? t)
        {
            string name = (_name.Text ?? "").Trim();
            string ext = t?.Extension ?? "";
            if (_file && ext.Length > 0 && name.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && name.Length > ext.Length) name = name.Substring(0, name.Length - ext.Length);
            return name;
        }

        private void UpdateText()
        {
            var t = Selected;
            string name = NameOnly(t);
            if (name.Length == 0) { _preview.Text = ""; return; }
            if (_file) { _preview.Text = t == null ? "" : string.Join("\n", t.Files.Take(6).Select(f => new TemplateValues(name).Expand(f))) + (t.Files.Count > 6 ? "\n..." : ""); return; }
            string location = (_location.Text ?? "").Trim();
            if (location.Length == 0) { _preview.Text = ""; return; }
            _preview.Text = _solution
                ? $"{Path.Combine(location, name + FireSolution.Extension)}" + (t is { Empty: false } ? $"\n{Path.Combine(location, name, name + FireProject.Extension)}" : "")
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
            var template = Selected;
            if (_pick)
            {
                if (template == null) { _error.Text = "Choose a template."; _error.IsVisible = true; return; }
                Template = template;
                Close(true);
                return;
            }
            string name = NameOnly(template);
            string location = (_location.Text ?? "").Trim();
            string? problem = template == null ? "Choose a template." : ProjectTemplates.CheckName(name);
            if (problem == null && !_file && location.Length == 0) problem = "The location is empty.";
            if (problem == null && !_file)
            {
                try { location = Path.GetFullPath(location); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { problem = "The location is not a path: " + ex.Message; }
            }
            if (problem == null && !_file)
            {
                string target = _solution ? Path.Combine(location, name + FireSolution.Extension) : Path.Combine(location, name, name + FireProject.Extension);
                if (File.Exists(target)) problem = $"'{target}' exists already.";
                else if (_solution && template is { Empty: false } && File.Exists(Path.Combine(location, name, name + FireProject.Extension))) problem = $"'{Path.Combine(location, name, name + FireProject.Extension)}' exists already.";
            }
            if (problem != null) { _error.Text = problem; _error.IsVisible = true; return; }
            Name = name; Location = location; Template = template;
            Close(true);
        }
    }
}
