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
    /// <summary>The fields of the build settings (docs/PROJECTS.md): each is "(inherit)" or empty - not set at this level - or a value. `inherited` shows what the level below says, as a hint.</summary>
    internal sealed class SettingsEditor
    {
        private const string Inherit = "(not set)";
        private readonly ComboBox _subsystem = Choice(ProjectSettings.Subsystems);
        private readonly ComboBox _mode = Choice(ProjectSettings.Modes);
        private readonly ComboBox _floatWidth = Choice(new[] { "32", "64" });
        private readonly ComboBox _engine = Choice(ProjectSettings.Engines);
        private readonly TextBox _name = Box(), _codename = Box(), _description = Box(), _author = Box(), _license = Box(), _comments = Box(), _icon = Box(), _version = Box(), _fileVersion = Box();
        private readonly TextBox _defines = Box(), _target = Box(), _toolchain = Box(), _output = Box();

        public Control View { get; }

        private static ComboBox Choice(IEnumerable<string> values)
        {
            var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
            box.Items.Add(Inherit);
            foreach (var v in values) box.Items.Add(v);
            return box;
        }

        private static TextBox Box() => new() { HorizontalAlignment = HorizontalAlignment.Stretch };

        public SettingsEditor(ProjectSettings settings, ProjectSettings? inherited)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), Margin = new Thickness(0, 6, 0, 0) };
            int row = 0;
            void Add(string label, Control control, string? hint = null)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var text = new TextBlock { Text = label, Foreground = EditorTheme.TextDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
                Grid.SetRow(text, row); Grid.SetColumn(text, 0);
                Grid.SetRow(control, row); Grid.SetColumn(control, 1);
                control.Margin = new Thickness(0, 2);
                if (hint != null && control is TextBox tb && string.IsNullOrEmpty(tb.Text)) tb.Watermark = hint;
                grid.Children.Add(text); grid.Children.Add(control);
                row++;
            }
            string? Hint(string? v) => v == null ? null : "from the solution: " + v;
            Add("Subsystem", _subsystem);
            Add("Mode", _mode);
            Add("Precision of float", _floatWidth);
            Add("Product name", _name, Hint(inherited?.Name));
            Add("Internal name", _codename, Hint(inherited?.Codename));
            Add("Description", _description, Hint(inherited?.Description));
            Add("Author / company", _author, Hint(inherited?.Author));
            Add("License (package)", _license, Hint(inherited?.License) ?? "MIT, ...");
            Add("Comments", _comments, Hint(inherited?.Comments));
            Add("Icon", _icon, Hint(inherited?.Icon));
            Add("Version", _version, Hint(inherited?.Version) ?? "1.0.0.0");
            Add("File version", _fileVersion, Hint(inherited?.FileVersion) ?? "1.0.0.0");
            Add("Symbols for #if", _defines, "names separated by commas");
            Add("Engine", _engine);
            Add("Target (native)", _target, Hint(inherited?.Target));
            Add("Toolchain (native)", _toolchain, Hint(inherited?.Toolchain));
            Add("Output", _output, Hint(inherited?.Output) ?? "bin/...");
            View = new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            Show(settings);
        }

        private static void Select(ComboBox box, string? value) => box.SelectedItem = value != null && box.Items.Contains(value) ? value : Inherit;
        private static string? Read(ComboBox box) => box.SelectedItem is string s && s != Inherit ? s : null;
        private static string? Read(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : box.Text!.Trim();

        private void Show(ProjectSettings s)
        {
            Select(_subsystem, s.Subsystem); Select(_mode, s.Mode); Select(_floatWidth, s.FloatWidth?.ToString()); Select(_engine, s.Engine);
            _name.Text = s.Name; _codename.Text = s.Codename; _description.Text = s.Description; _author.Text = s.Author; _license.Text = s.License; _comments.Text = s.Comments; _icon.Text = s.Icon;
            _version.Text = s.Version; _fileVersion.Text = s.FileVersion; _defines.Text = s.Defines == null ? "" : string.Join(", ", s.Defines);
            _target.Text = s.Target; _toolchain.Text = s.Toolchain; _output.Text = s.Output;
        }

        /// <summary>The settings as they are in the fields now.</summary>
        public ProjectSettings Read()
        {
            var defines = (_defines.Text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
            return new ProjectSettings
            {
                Subsystem = Read(_subsystem), Mode = Read(_mode), FloatWidth = int.TryParse(Read(_floatWidth), out int w) ? w : null, Engine = Read(_engine),
                Name = Read(_name), Codename = Read(_codename), Description = Read(_description), Author = Read(_author), License = Read(_license), Comments = Read(_comments), Icon = Read(_icon),
                Version = Read(_version), FileVersion = Read(_fileVersion), Defines = defines.Count > 0 ? defines : null,
                Target = Read(_target), Toolchain = Read(_toolchain), Output = Read(_output),
            };
        }
    }

    /// <summary>
    /// The properties of a project (docs/PROJECTS.md): name, program or library, entry file, build settings (the ones that a solution fixes are shown as hints), references - and, when
    /// a solution is open, its settings and startup project. `ShowDialog` returns true when the user pressed OK; the changes are then in the project (and the solution) but not saved
    /// - the caller saves.
    /// </summary>
    internal sealed class ProjectPropertiesDialog : Window
    {
        private readonly Workspace _workspace;
        private readonly LoadedProject _project;
        private readonly TextBox _name = new();
        private readonly ComboBox _type = new();
        private readonly TextBox _import = new();
        private readonly ComboBox _entry = new();
        private readonly SettingsEditor _settings;
        private readonly SettingsEditor? _solutionSettings;
        private readonly ComboBox? _startup;
        private readonly ListBox _references = new();
        private readonly List<ProjectReference> _referenceList;
        private readonly TextBlock _error = new() { Foreground = EditorTheme.ErrorMark, TextWrapping = TextWrapping.Wrap, IsVisible = false };

        public ProjectPropertiesDialog(Workspace workspace, LoadedProject project)
        {
            _workspace = workspace;
            _project = project;
            Title = $"Properties of {project.Name}";
            Width = 620; Height = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            var p = project.Project;
            _referenceList = p.References.Select(r => new ProjectReference { Project = r.Project, Package = r.Package, Version = r.Version }).ToList();

            // --- project
            _name.Text = p.Name;
            _type.Items.Add("Program"); _type.Items.Add("Library (no entry point)");
            _type.SelectedIndex = p.Type == OutputType.Library ? 1 : 0;
            _import.Text = p.Import; _import.Watermark = p.ImportName;
            _entry.Items.Add("(the last file)");
            foreach (var f in project.Files) _entry.Items.Add(ProjectFiles.Relative(project.Directory, f));
            _entry.SelectedItem = p.Entry != null && _entry.Items.Contains(p.Entry.Replace('\\', '/')) ? p.Entry.Replace('\\', '/') : _entry.Items[0];
            _type.SelectionChanged += (_, _) => UpdateEnabled();
            var general = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Spacing = 6 };
            general.Children.Add(Labeled("Name", _name));
            general.Children.Add(Labeled("Type", _type));
            general.Children.Add(Labeled("Import name (library): #import \"...\"", _import));
            general.Children.Add(Labeled("Entry file (program): runs last", _entry));
            general.Children.Add(new TextBlock { Text = project.FilePath, Foreground = EditorTheme.TextDim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
            if (workspace.Solution != null && project.Project.Type == OutputType.Exe)
            {
                var startup = new CheckBox { Content = "Startup project of the solution", IsChecked = ReferenceEquals(workspace.Startup, project), Margin = new Thickness(0, 6, 0, 0) };
                _startupCheck = startup;
                general.Children.Add(startup);
            }

            // --- settings
            _settings = new SettingsEditor(p.Settings ?? new ProjectSettings(), workspace.Solution?.Settings);

            // --- references
            RefreshReferences();
            var addProject = new Button { Content = "Add project..." };
            addProject.Click += async (_, _) => await AddProjectReference();
            var addPackage = new Button { Content = "Add package..." };
            addPackage.Click += async (_, _) => await AddPackageReference();
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => { if (_references.SelectedIndex >= 0) { _referenceList.RemoveAt(_references.SelectedIndex); RefreshReferences(); } };
            var referenceButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { addProject, addPackage, remove } };
            var references = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(referenceButtons, Avalonia.Controls.Dock.Bottom);
            references.Children.Add(referenceButtons);
            references.Children.Add(new DockPanel { Children = { _references } });
            var hint = new TextBlock { Text = "A reference makes a library of the solution (or a package) available; #import \"Name\" in the source turns it on.", Foreground = EditorTheme.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(hint, Avalonia.Controls.Dock.Top);
            references.Children.Insert(0, hint);

            var tabs = new TabControl();
            _tabs = tabs;
            tabs.Items.Add(new TabItem { Header = "Project", Content = general });
            tabs.Items.Add(new TabItem { Header = "Build settings", Content = _settings.View });
            tabs.Items.Add(new TabItem { Header = "References", Content = references });
            if (workspace.Solution != null)
            {
                _solutionSettings = new SettingsEditor(workspace.Solution.Settings ?? new ProjectSettings(), null);
                var solution = new DockPanel();
                var sHint = new TextBlock { Text = $"Solution '{workspace.Solution.Name}': settings for all of its projects (a project's own settings go before them).", Foreground = EditorTheme.TextDim, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
                DockPanel.SetDock(sHint, Avalonia.Controls.Dock.Top);
                solution.Children.Add(sHint);
                solution.Children.Add(_solutionSettings.View);
                tabs.Items.Add(new TabItem { Header = "Solution", Content = solution });
                _startup = null;
            }

            var ok = new Button { Content = "OK", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Classes.Add("accent");
            ok.Click += (_, _) => Accept();
            var cancel = new Button { Content = "Cancel", MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            cancel.Click += (_, _) => Close(false);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { ok, cancel } };

            var root = new DockPanel { Margin = new Thickness(12) };
            DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Bottom);
            DockPanel.SetDock(_error, Avalonia.Controls.Dock.Bottom);
            root.Children.Add(buttons);
            root.Children.Add(_error);
            root.Children.Add(tabs);
            Content = root;
            UpdateEnabled();
        }

        private CheckBox? _startupCheck;
        private TabControl? _tabs;

        /// <summary>Starts on a page (0 project, 1 build settings, 2 references, 3 solution).</summary>
        public void SelectTab(int index) { if (_tabs != null && index < _tabs.ItemCount) _tabs.SelectedIndex = index; }

        /// <summary>Starts on the page of the solution settings.</summary>
        public void SelectSolutionTab() { if (_tabs != null && _tabs.ItemCount > 3) _tabs.SelectedIndex = 3; }

        private static Control Labeled(string label, Control control) =>
            new StackPanel { Spacing = 2, Children = { new TextBlock { Text = label, Foreground = EditorTheme.TextDim }, control } };

        private void UpdateEnabled()
        {
            bool library = _type.SelectedIndex == 1;
            _import.IsEnabled = library;
            _entry.IsEnabled = !library;
            if (_startupCheck != null) _startupCheck.IsEnabled = !library;
        }

        private void RefreshReferences()
        {
            _references.ItemsSource = _referenceList.Select(r => r.IsProject ? $"{Path.GetFileNameWithoutExtension(r.Project!)}  (project: {r.Project})" : $"{r.Package}{(r.Version != null ? " " + r.Version : "")}  (package)").ToList();
        }

        private async Task AddProjectReference()
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Reference a library project",
                FileTypeFilter = new List<FilePickerFileType> { new("fire projects") { Patterns = new[] { "*" + FireProject.Extension } } },
            });
            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
            try
            {
                var library = _workspace.LoadProject(path);
                if (library.Project.Type != OutputType.Library) { Show("'" + library.Name + "' is a program: only a library can be referenced."); return; }
                if (ProjectFiles.PathComparer.Equals(library.FilePath, _project.FilePath)) { Show("A project cannot reference itself."); return; }
                string rel = ProjectFiles.Relative(_project.Directory, library.FilePath);
                if (!_referenceList.Any(r => r.IsProject && r.Project == rel)) _referenceList.Add(new ProjectReference { Project = rel });
                RefreshReferences();
                _error.IsVisible = false;
            }
            catch (ProjectException ex) { Show(ex.Message); }
        }

        private async Task AddPackageReference()
        {
            string? name = await Dialogs.Input(this, "Name of the package (installed with ember):", "Add package reference");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (!_referenceList.Any(r => string.Equals(r.Package, name, StringComparison.OrdinalIgnoreCase))) _referenceList.Add(new ProjectReference { Package = name });
            RefreshReferences();
        }

        private void Show(string message) { _error.Text = message; _error.IsVisible = true; }

        private void Accept()
        {
            // work on a copy so that a mistake leaves the project as it was
            var copy = new FireProject
            {
                Format = _project.Project.Format, Name = (_name.Text ?? "").Trim(), Type = _type.SelectedIndex == 1 ? OutputType.Library : OutputType.Exe,
                Import = string.IsNullOrWhiteSpace(_import.Text) ? null : _import.Text!.Trim(), Files = _project.Project.Files, Exclude = _project.Project.Exclude,
                Entry = _entry.SelectedIndex > 0 ? _entry.SelectedItem as string : null, References = _referenceList, Settings = _settings.Read(), FilePath = _project.FilePath,
            };
            var problems = copy.Validate();
            if (_solutionSettings != null) problems = problems.Concat(_solutionSettings.Read().Validate()).ToList();
            if (problems.Count > 0) { Show(string.Join("\n", problems)); return; }

            var p = _project.Project;
            p.Name = copy.Name; p.Type = copy.Type; p.Import = copy.Import; p.Entry = copy.Entry; p.References = copy.References; p.Settings = copy.Settings;
            if (_solutionSettings != null && _workspace.Solution != null)
            {
                _workspace.Solution.Settings = _solutionSettings.Read();
                if (_startupCheck?.IsChecked == true) _workspace.Solution.Startup = p.Name;
                else if (string.Equals(_workspace.Solution.Startup, _project.Name, StringComparison.OrdinalIgnoreCase) && _startupCheck != null) _workspace.Solution.Startup = null;
            }
            Close(true);
        }
    }
}
