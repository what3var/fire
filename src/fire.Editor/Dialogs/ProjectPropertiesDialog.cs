using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using fire.Projects;
using static fire.Editor.SettingsLayout;

namespace fire.Editor
{
    /// <summary>
    /// The properties of a project (docs/tools/projects.md), on pages chosen at the left: General (what most projects need), Version &amp; Info, Build, Native &amp; Toolchain, Run &amp; Debug,
    /// References, Notes - and, when a solution is open, the solution. A field that is empty is not set at this level: it shows in grey what the solution or the default says.
    /// The dialog works on copies; Save checks them and puts them into the project (and the solution) - the caller writes the files. `ShowDialog` returns true on Save.
    /// </summary>
    internal sealed class ProjectPropertiesDialog : Window
    {

        /// <summary>What a setting is when nobody sets it.</summary>
        private static readonly Dictionary<string, string> Defaults = new()
        {
            ["subsystem"] = "console", ["mode"] = "debug", ["floatWidth"] = "64", ["intWidth"] = "64", ["engine"] = "vm", ["typeChecking"] = "lax", ["globalJobs"] = "safepoints",
        };

        private readonly Workspace _workspace;
        private readonly LoadedProject _project;
        private readonly ProjectSettings _settings;
        private readonly ProjectSettings? _solutionSettings;
        private readonly ProjectSettings? _inherited;   // what the solution says (null: no solution)
        private readonly List<ProjectReference> _referenceList;
        private readonly ListBox _references = new() { MinHeight = 180 };
        private readonly TextBlock _error = new() { Foreground = EditorTheme.ErrorMark, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        private readonly SettingsNav _nav = new();
        private fire.Native.NativeConfig _native;

        private string _name;
        private OutputType _type;
        private string? _import;
        private string? _entry;
        private string? _notes;
        private string? _solutionNotes;
        private string? _startup;
        private readonly List<Action> _refresh = new();   // fields that show what other fields decide (program or library)

        /// <summary>Opens the native settings (targets and toolchains, fire.native.json); set by the main window.</summary>
        /// <summary>Opens the native build settings (targets and toolchains) over this dialog; true when they were saved. Set by the main window.</summary>
        public Func<Window, Task<bool>>? EditToolchains { get; set; }

        public ProjectPropertiesDialog(Workspace workspace, LoadedProject project)
        {
            _workspace = workspace;
            _project = project;
            var p = project.Project;
            _settings = (p.Settings ?? new ProjectSettings()).Clone();
            _solutionSettings = workspace.Solution?.Settings?.Clone() ?? (workspace.Solution != null ? new ProjectSettings() : null);
            _inherited = workspace.Solution?.Settings;
            _referenceList = p.References.Select(r => new ProjectReference { Project = r.Project, Package = r.Package, Version = r.Version }).ToList();
            _name = p.Name;
            _type = p.Type;
            _import = p.Import;
            _entry = p.Entry;
            _notes = p.Notes;
            _solutionNotes = workspace.Solution?.Notes;
            _startup = workspace.Solution?.Startup;
            try { _native = fire.Native.NativeConfig.FindFor(project.FilePath); }
            catch (fire.Native.NativeConfigException) { _native = new fire.Native.NativeConfig(); }

            Title = $"Project Properties - {project.Name}";
            Width = 900; Height = 640; MinWidth = 760; MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            // the pages and the list at the left
            _nav.AddScope("Project");
            _nav.Add("general", NavIcons.Sliders, "General", GeneralPage());
            _nav.Add("version", NavIcons.Document, "Version & Info", VersionPage());
            _nav.Add("build", NavIcons.Cube, "Build", BuildPage());
            _nav.Add("native", NavIcons.Chip, "Native & Toolchain", NativePage());
            _nav.Add("run", NavIcons.Play, "Run & Debug", RunPage());
            _nav.Add("references", NavIcons.Arrows, "References", ReferencesPage());
            _nav.Add("notes", NavIcons.Pencil, "Notes", NotesPage());
            if (workspace.Solution != null)
            {
                _nav.AddSeparator();
                _nav.AddScope("Solution");
                _nav.Add("solution", NavIcons.Grid, $"{workspace.Solution.Name}", SolutionPage());
            }

            // Save / Cancel, and which files are written
            var save = DialogButton("Save", accent: true);
            save.Click += (_, _) => Accept();
            var cancel = DialogButton("Cancel");
            cancel.Click += (_, _) => Close(false);
            string files = Path.GetFileName(project.FilePath) + (workspace.Solution?.FilePath is { } s ? " and " + Path.GetFileName(s) : "");
            var hint = Hint($"Grey values come from the solution or the defaults. Save writes {files}.");
            Content = Frame(_nav, new StackPanel { Children = { _error, hint }, VerticalAlignment = VerticalAlignment.Center }, cancel, save);
            _nav.Show("general");
            foreach (var r in _refresh) r();

            KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(false); e.Handled = true; } };
        }

        // ---------------------------------------------------------------- pages

        /// <summary>Starts on a page (0 general, 1 build, 2 references, 3 solution) - the numbers of the old tabs.</summary>
        public void SelectTab(int index) => _nav.Show(index switch { 1 => "build", 2 => "references", 3 => "solution", _ => "general" });

        /// <summary>Starts on the page of the solution.</summary>
        public void SelectSolutionTab() => _nav.Show("solution");

        // ---------------------------------------------------------------- fields

        /// <summary>A text field: empty is "not set here"; the grey text shows what is inherited (or `placeholder`).</summary>
        private static TextBox Text(Func<string?> get, Action<string?> set, string? inherited, string? from, string? placeholder = null)
        {
            var box = new TextBox
            {
                Text = get(), Background = Field, HorizontalAlignment = HorizontalAlignment.Stretch,
                Watermark = inherited != null ? $"{inherited}   ({from})" : placeholder,
            };
            box.TextChanged += (_, _) => set(string.IsNullOrWhiteSpace(box.Text) ? null : box.Text!.Trim());
            return box;
        }

        /// <summary>
        /// A choice of a few values as buttons side by side. Not set here, the inherited value is shown pale with where it comes from; a click sets it here, "reset" takes it out again.
        /// </summary>
        private static Control Choice((string Label, string Value)[] options, Func<string?> get, Action<string?> set, string? inherited, string from, string? hint = null)
        {
            var buttons = new List<Button>();
            var source = new TextBlock { Foreground = EditorTheme.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var reset = new Button { Content = "reset", Padding = new Thickness(6, 1), FontSize = 12, Background = Brushes.Transparent, Foreground = AccentText, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(reset, $"Not set here: take it from the {from}");
            void Update()
            {
                string? own = get();
                string? shown = own ?? inherited;
                for (int i = 0; i < options.Length; i++)
                {
                    bool on = options[i].Value == shown;
                    PaintSegment(buttons[i], on);
                    buttons[i].Opacity = on && own == null ? 0.6 : 1;
                }
                source.Text = own == null && inherited != null ? $"({from})" : "";
                reset.IsVisible = own != null;
            }
            var segments = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < options.Length; i++)
            {
                var (label, value) = options[i];
                var button = new Button
                {
                    Content = label, Padding = new Thickness(14, 5), CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
                    BorderBrush = EditorTheme.Border, MinWidth = 64, HorizontalContentAlignment = HorizontalAlignment.Center,
                };
                button.Click += (_, _) => { set(value); Update(); };
                buttons.Add(button);
                segments.Children.Add(button);
            }
            reset.Click += (_, _) => { set(null); Update(); };
            Update();
            var frame = new Border { BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = segments };
            frame.Styles.AddRange(SegmentStyles());
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { frame, source, reset } };
            if (hint != null) row.Children.Add(new TextBlock { Text = hint, Foreground = EditorTheme.TextDim, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
            return row;
        }

        /// <summary>A list of names to choose from; the first entry is "not set here" and says what is inherited.</summary>
        private static ComboBox Pick(IEnumerable<string> names, Func<string?> get, Action<string?> set, string inheritedText)
        {
            var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Field };
            box.Items.Add(inheritedText);
            foreach (var n in names) box.Items.Add(n);
            string? value = get();
            if (value != null && !box.Items.Contains(value)) box.Items.Add(value);   // a name that is not known here: keep it
            box.SelectedItem = value ?? inheritedText;
            box.SelectionChanged += (_, _) => set(box.SelectedIndex <= 0 ? null : box.SelectedItem as string);
            return box;
        }

        /// <summary>Puts new names into a box of Pick (the first item, the inherited value, stays) and selects `value` again.</summary>
        private static void Refill(ComboBox box, IEnumerable<string> names, string? value)
        {
            var first = box.Items[0];
            box.Items.Clear();
            box.Items.Add(first);
            foreach (var n in names) box.Items.Add(n);
            if (value != null && !box.Items.Contains(value)) box.Items.Add(value);
            box.SelectedItem = value ?? first;
        }

        private string? InheritedOf(Func<ProjectSettings, string?> field, string? defaultKey, out string from)
        {
            if (_inherited != null && field(_inherited) is { } v) { from = "from solution"; return v; }
            from = "default";
            return defaultKey != null && Defaults.TryGetValue(defaultKey, out var d) ? d : null;
        }

        // ---------------------------------------------------------------- the pages

        private Control GeneralPage()
        {
            var s = _settings;
            string? authorInh = InheritedOf(x => x.Author, null, out string authorFrom);
            string? versionInh = InheritedOf(x => x.Version, null, out string versionFrom);
            var identity = Group("Identity",
                Row("Name", Text(() => _name, v => _name = v ?? "", null, null, "the name of the project"), 84),
                Row("Version", Text(() => s.Version, v => s.Version = v, versionInh, versionFrom, "1.0.0"), 84),
                Row("Author", Text(() => s.Author, v => s.Author = v, authorInh, authorFrom), 84),
                Row("Description", Text(() => s.Description, v => s.Description = v, InheritedOf(x => x.Description, null, out string dFrom), dFrom), 84));

            var icon = Group("Icon", IconEditor());

            string? subsystemInh = InheritedOf(x => x.Subsystem, "subsystem", out string subsystemFrom);
            string? typeInh = InheritedOf(x => x.TypeChecking, "typeChecking", out string typeFrom);
            string? jobsInh = InheritedOf(x => x.GlobalJobs, "globalJobs", out string jobsFrom);
            string? engineInh = InheritedOf(x => x.Engine, "engine", out string engineFrom);
            var entry = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Field };
            entry.Items.Add("(the last file)");
            foreach (var f in _project.Files) entry.Items.Add(ProjectFiles.Relative(_project.Directory, f));
            entry.SelectedItem = _entry != null && entry.Items.Contains(_entry.Replace('\\', '/')) ? _entry.Replace('\\', '/') : entry.Items[0];
            entry.SelectionChanged += (_, _) => _entry = entry.SelectedIndex > 0 ? entry.SelectedItem as string : null;
            var import = Text(() => _import, v => _import = v, null, null, _project.Project.ImportName);
            var entryRow = Row("Entry file", entry);
            var importRow = Row("Import name", import);
            _refresh.Add(() => { entryRow.IsVisible = _type == OutputType.Exe; importRow.IsVisible = _type == OutputType.Library; });

            var program = Group("Program",
                Row("Output type", TypeChoice()),
                entryRow,
                importRow,
                Row("Application", Choice(new[] { ("Desktop", "gui"), ("Console", "console") }, () => s.Subsystem, v => s.Subsystem = v, subsystemInh, subsystemFrom, "Desktop: no console window")),
                Row("Type checking", Choice(new[] { ("Lax", "lax"), ("Strict", "strict") }, () => s.TypeChecking, v => s.TypeChecking = v, typeInh, typeFrom, "Strict: a type conflict is an error")),
                Row("Global jobs", Choice(new[] { ("Safe points", "safepoints"), ("Sleep", "sleep"), ("Sync", "sync") }, () => s.GlobalJobs, v => s.GlobalJobs = v, jobsInh, jobsFrom, "When the main program runs `fire global` jobs by itself")),
                Row("Runs as", Choice(new[] { ("Virtual machine", "vm"), ("Native", "native") }, () => s.Engine, v => s.Engine = v, engineInh, engineFrom, "Native: see Native & Toolchain")));

            var content = new List<Control> { Columns(identity, icon), program };
            if (_workspace.Solution != null)
            {
                var startup = new CheckBox { Content = "The startup project of the solution (it runs on Start)", IsChecked = string.Equals(_workspace.Solution.Startup, _project.Name, StringComparison.OrdinalIgnoreCase) || ReferenceEquals(_workspace.Startup, _project) };
                startup.IsCheckedChanged += (_, _) =>
                {
                    if (startup.IsChecked == true) _startup = _name;
                    else if (string.Equals(_startup, _project.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(_startup, _name, StringComparison.OrdinalIgnoreCase)) _startup = null;
                };
                _refresh.Add(() => startup.IsEnabled = _type == OutputType.Exe);
                content.Add(startup);
            }
            content.Add(Hint(_project.FilePath));
            return Page("General", "The settings most projects need.", content.ToArray());
        }

        private Control TypeChoice()
        {
            var options = new[] { ("Program", OutputType.Exe), ("Library", OutputType.Library) };
            var buttons = new List<Button>();
            void Update()
            {
                for (int i = 0; i < options.Length; i++)
                {
                    bool on = options[i].Item2 == _type;
                    buttons[i].Background = on ? Accent : Brushes.Transparent;
                    buttons[i].Foreground = on ? Brushes.White : EditorTheme.TextDim;
                }
                foreach (var r in _refresh) r();
            }
            var segments = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < options.Length; i++)
            {
                var (label, type) = options[i];
                var b = new Button { Content = label, Padding = new Thickness(14, 5), CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(i == 0 ? 0 : 1, 0, 0, 0), BorderBrush = EditorTheme.Border, MinWidth = 64, HorizontalContentAlignment = HorizontalAlignment.Center };
                b.Click += (_, _) => { _type = type; Update(); };
                buttons.Add(b);
                segments.Children.Add(b);
            }
            for (int i = 0; i < options.Length; i++)
            {
                bool on = options[i].Item2 == _type;
                buttons[i].Background = on ? Accent : Brushes.Transparent;
                buttons[i].Foreground = on ? Brushes.White : EditorTheme.TextDim;
            }
            var frame = new Border { BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = segments, HorizontalAlignment = HorizontalAlignment.Left };
            return frame;
        }

        /// <summary>The icon: a preview, its path (relative to the project), Choose... and Remove.</summary>
        private Control IconEditor()
        {
            var preview = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(8), BorderBrush = EditorTheme.Border, BorderThickness = new Thickness(1), Background = Field, ClipToBounds = true };
            var path = new TextBox { Background = Field, HorizontalAlignment = HorizontalAlignment.Stretch };
            string? inherited = InheritedOf(x => x.Icon, null, out string from);
            path.Watermark = inherited != null ? $"{inherited}   ({from})" : "no icon";
            void ShowPreview()
            {
                string? rel = _settings.Icon ?? inherited;
                preview.Child = null;
                if (rel == null) { preview.Child = new TextBlock { Text = "none", FontSize = 11, Foreground = EditorTheme.TextDim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; return; }
                string full = Path.GetFullPath(rel, _settings.Icon != null ? _project.Directory : _workspace.Solution?.Directory ?? _project.Directory);
                try { preview.Child = new Image { Source = new Bitmap(full), Stretch = Stretch.Uniform }; }
                catch (Exception) { preview.Child = new TextBlock { Text = File.Exists(full) ? Path.GetExtension(full) : "missing", FontSize = 11, Foreground = EditorTheme.TextDim, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; }
            }
            path.Text = _settings.Icon;
            path.TextChanged += (_, _) => { _settings.Icon = string.IsNullOrWhiteSpace(path.Text) ? null : path.Text!.Trim(); ShowPreview(); };
            var choose = new Button { Content = "Choose..." };
            choose.Click += async (_, _) =>
            {
                var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "The icon of the program",
                    FileTypeFilter = new List<FilePickerFileType> { new("Icons") { Patterns = new[] { "*.ico", "*.png" } } },
                });
                if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } file) path.Text = ProjectFiles.Relative(_project.Directory, file);
            };
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => path.Text = "";
            ShowPreview();
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var right = new StackPanel { Spacing = 6, Margin = new Thickness(10, 0, 0, 0), Children = { path, Hint(".ico or .png - the title bar and the file of the program") } };
            Grid.SetColumn(right, 1);
            grid.Children.Add(preview);
            grid.Children.Add(right);
            return new StackPanel { Spacing = 8, Children = { grid, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { choose, remove } } } };
        }

        private Control VersionPage()
        {
            var s = _settings;
            Control T(string label, Func<string?> get, Action<string?> set, Func<ProjectSettings, string?> field, string? placeholder = null) =>
                Row(label, Text(get, set, InheritedOf(field, null, out string from), from, placeholder));
            return Page("Version & Info", "What the program file says about itself, and what a packed library carries.",
                Group("Product",
                    T("Product name", () => s.Name, v => s.Name = v, x => x.Name, _name),
                    T("Internal name", () => s.Codename, v => s.Codename = v, x => x.Codename),
                    T("Version", () => s.Version, v => s.Version = v, x => x.Version, "1.0.0"),
                    T("File version", () => s.FileVersion, v => s.FileVersion = v, x => x.FileVersion, "1.0.0.0")),
                Group("Legal",
                    T("Author / company", () => s.Author, v => s.Author = v, x => x.Author),
                    T("License", () => s.License, v => s.License = v, x => x.License, "MIT, ... (a packed library)"),
                    T("Comments", () => s.Comments, v => s.Comments = v, x => x.Comments)));
        }

        private Control BuildPage()
        {
            var s = _settings;
            string? modeInh = InheritedOf(x => x.Mode, "mode", out string modeFrom);
            string? floatInh = InheritedOf(x => x.FloatWidth?.ToString(), "floatWidth", out string floatFrom);
            string? intInh = InheritedOf(x => x.IntWidth?.ToString(), "intWidth", out string intFrom);
            string defines = s.Defines == null ? "" : string.Join(", ", s.Defines);
            var definesBox = new TextBox { Text = defines, Background = Field, Watermark = _inherited?.Defines is { Count: > 0 } d ? $"{string.Join(", ", d)}   (from solution, added)" : "names, separated by commas" };
            definesBox.TextChanged += (_, _) =>
            {
                var list = (definesBox.Text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
                s.Defines = list.Count > 0 ? list : null;
            };
            return Page("Build", "How the program is compiled - in the virtual machine and native.",
                Group("Compiling",
                    Row("Configuration", Choice(new[] { ("Debug", "debug"), ("Release", "release"), ("Performance", "performance") }, () => s.Mode, v => s.Mode = v, modeInh, modeFrom)),
                    Row("Float precision", Choice(new[] { ("32 bit", "32"), ("64 bit", "64") }, () => s.FloatWidth?.ToString(), v => s.FloatWidth = v == null ? null : int.Parse(v), floatInh, floatFrom)),
                    Row("Int width", Choice(new[] { ("32 bit", "32"), ("64 bit", "64") }, () => s.IntWidth?.ToString(), v => s.IntWidth = v == null ? null : int.Parse(v), intInh, intFrom)),
                    Row("Symbols for #if", definesBox)),
                Group("Output",
                    Row("Output folder", Text(() => s.Output, v => s.Output = v, InheritedOf(x => x.Output, null, out string oFrom), oFrom, "bin/<target>-<mode>/")),
                    Hint("Relative to the project file. A packed library goes into this folder, too.")));
        }

        private Control NativePage()
        {
            var s = _settings;
            string defaultTarget;
            try { defaultTarget = _native.ResolveTarget(_inherited?.Target).Name; }
            catch (fire.Native.NativeConfigException) { defaultTarget = "?"; }
            var target = Pick(_native.TargetNames, () => s.Target, v => s.Target = v, _inherited?.Target != null ? $"(from solution: {_inherited.Target})" : $"(default: {defaultTarget})");
            var toolchain = Pick(_native.ToolchainNames, () => s.Toolchain, v => s.Toolchain = v, _inherited?.Toolchain != null ? $"(from solution: {_inherited.Toolchain})" : "(the toolchain of the target, or the one found on this machine)");
            var edit = new Button { Content = "Edit targets and toolchains..." };
            edit.Click += async (_, _) =>
            {
                if (EditToolchains == null || !await EditToolchains(this)) return;
                try { _native = fire.Native.NativeConfig.FindFor(_project.FilePath); }   // the lists may have new names now
                catch (fire.Native.NativeConfigException) { return; }
                Refill(target, _native.TargetNames, s.Target);
                Refill(toolchain, _native.ToolchainNames, s.Toolchain);
            };
            var natives = _project.Project.Native != null
                ? Hint("This project has C++ natives in native/ (docs/packages/natives.md); they are built with the toolchain, too.")
                : Hint("This project has no C++ natives of its own.");
            // (the stripping level as its number - the file may name it: "methods" is 2)
            static string? Level(string? v) => v != null && ProjectSettings.Strippings.ToList().IndexOf(v.ToLowerInvariant()) is int i and >= 0 ? i.ToString(System.Globalization.CultureInfo.InvariantCulture) : v;
            string? strippingInh = Level(InheritedOf(x => x.Stripping, null, out string strippingFrom));
            var stripping = Choice(new[] { ("0 none", "0"), ("1 protected", "1"), ("2 methods", "2"), ("3 all", "3") }, () => Level(s.Stripping), v => s.Stripping = v,
                strippingInh, strippingFrom, "default: 3");
            return Page("Native & Toolchain", "The target a native build runs on and the C++ toolchain that compiles it. Used when General > Runs as is Native.",
                Group("Target", Row("Target", target), Hint("The built-in targets and those of fire.native.json.")),
                Group("Toolchain", Row("Toolchain", toolchain), Row("", edit)),
                Group("Board (ESP32)",
                    Row("Serial port", BoardFields.Port(s.Port, _inherited?.Port, v => s.Port = v)),
                    Row("Baud rate", BoardFields.Baud(s.Baud, _inherited?.Baud, v => s.Baud = v)),
                    Hint("Run and Debug on an ESP32 flash the board on this port and talk to its console there (settings \"port\" and \"baud\"). auto: the port of the target "
                        + "in fire.native.json, else ESPPORT, else the only USB serial port of this machine, else the one esptool uses; the baud rate is 115200 unless set."),
                    BoardFields.IdfStatus(fire.Projects.EditorSettings.Load().IdfPath)),
                Group("Size", Row("Stripping", stripping),
                    Hint("What a native build leaves out of what the program does not reach: 0 nothing (like the VM), 1 private and protected members nothing uses, 2 also methods "
                        + "nothing uses, 3 also classes the program never makes (the default). Names given as literals to reflection and [Keep] always stay; a debug build keeps everything.")),
                Group("Natives", natives));
        }

        private Control RunPage()
        {
            var s = _settings;
            string? inh = _inherited?.Arguments is { } a ? ArgumentText.Join(a) : null;
            var args = new TextBox
            {
                Text = s.Arguments == null ? "" : ArgumentText.Join(s.Arguments), Background = Field,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace"),
                Watermark = inh != null ? $"{inh}   (from solution)" : "a b \"with blanks\"",
            };
            args.TextChanged += (_, _) => s.Arguments = string.IsNullOrWhiteSpace(args.Text) ? null : ArgumentText.Split(args.Text!);
            return Page("Run & Debug", "What the program gets when it runs (Start, F5).",
                Group("Start",
                    Row("Arguments", args),
                    Hint("The program reads them as Environment.Args; quotes keep words with blanks together. On the command line, arguments after -- replace them for one run (forge run app.fireproj -- a b).")));
        }

        private Control ReferencesPage()
        {
            RefreshReferences();
            var addProject = new Button { Content = "Add project..." };
            addProject.Click += async (_, _) => await AddProjectReference();
            var addPackage = new Button { Content = "Add package..." };
            addPackage.Click += async (_, _) => await AddPackageReference();
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => { if (_references.SelectedIndex >= 0) { _referenceList.RemoveAt(_references.SelectedIndex); RefreshReferences(); } };
            return Page("References", "The libraries of the solution and the packages this project uses; #import \"Name\" in the source turns one on.",
                Group("Libraries and packages", _references, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { addProject, addPackage, remove } }));
        }

        private Control NotesPage()
        {
            string current = ProjectNotes.Folder(_project.FilePath, null);
            return Page("Notes", "The todo list and the overview of the project live in a folder of their own (docs/tools/notes.md).",
                Group("Folder",
                    Row("Notes folder", Text(() => _notes, v => _notes = v, null, null, $"{ProjectNotes.DefaultFolder}   (default)")),
                    Hint($"Relative to the project file. Without a value: {Path.GetRelativePath(_project.Directory, current)}/ next to it.")));
        }

        private Control SolutionPage()
        {
            var solution = _workspace.Solution!;
            var s = _solutionSettings!;
            string? D(string key) => Defaults.TryGetValue(key, out var v) ? v : null;
            var startup = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, Background = Field };
            startup.Items.Add("(the first program)");
            foreach (var p in _workspace.Projects.Where(p => p.Project.Type == OutputType.Exe)) startup.Items.Add(p.Name);
            startup.SelectedItem = _startup != null && startup.Items.Contains(_startup) ? _startup : startup.Items[0];
            startup.SelectionChanged += (_, _) => _startup = startup.SelectedIndex > 0 ? startup.SelectedItem as string : null;
            Control T(string label, Func<string?> get, Action<string?> set, string? placeholder = null) => Row(label, Text(get, set, null, null, placeholder));
            return Page($"Solution '{solution.Name}'", "Settings for all projects of the solution; a project's own settings go before them.",
                Group("Solution",
                    Row("Startup project", startup),
                    Row("Notes folder", Text(() => _solutionNotes, v => _solutionNotes = v, null, null, $"{ProjectNotes.DefaultFolder}   (default)"))),
                Group("Identity",
                    T("Author / company", () => s.Author, v => s.Author = v),
                    T("Version", () => s.Version, v => s.Version = v, "1.0.0"),
                    T("License", () => s.License, v => s.License = v)),
                Group("Program",
                    Row("Application", Choice(new[] { ("Desktop", "gui"), ("Console", "console") }, () => s.Subsystem, v => s.Subsystem = v, D("subsystem"), "default")),
                    Row("Type checking", Choice(new[] { ("Lax", "lax"), ("Strict", "strict") }, () => s.TypeChecking, v => s.TypeChecking = v, D("typeChecking"), "default")),
                    Row("Global jobs", Choice(new[] { ("Safe points", "safepoints"), ("Sleep", "sleep"), ("Sync", "sync") }, () => s.GlobalJobs, v => s.GlobalJobs = v, D("globalJobs"), "default")),
                    Row("Runs as", Choice(new[] { ("Virtual machine", "vm"), ("Native", "native") }, () => s.Engine, v => s.Engine = v, D("engine"), "default")),
                    Row("Configuration", Choice(new[] { ("Debug", "debug"), ("Release", "release"), ("Performance", "performance") }, () => s.Mode, v => s.Mode = v, D("mode"), "default"))),
                Group("Native",
                    Row("Target", Pick(_native.TargetNames, () => s.Target, v => s.Target = v, "(default)")),
                    Row("Toolchain", Pick(_native.ToolchainNames, () => s.Toolchain, v => s.Toolchain = v, "(default)")),
                    T("Output folder", () => s.Output, v => s.Output = v, "bin/<target>-<mode>/")));
        }

        // ---------------------------------------------------------------- references

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

        // ---------------------------------------------------------------- save

        private void Accept()
        {
            // work on a copy so that a mistake leaves the project as it was
            var copy = new FireProject
            {
                Format = _project.Project.Format, Name = _name.Trim(), Type = _type, Import = string.IsNullOrWhiteSpace(_import) ? null : _import.Trim(),
                Files = _project.Project.Files, Exclude = _project.Project.Exclude, Entry = _type == OutputType.Exe ? _entry : null, References = _referenceList,
                Settings = _settings, FilePath = _project.FilePath,
            };
            var problems = copy.Validate().ToList();
            if (_solutionSettings != null) problems.AddRange(_solutionSettings.Validate());
            if (problems.Count > 0) { Show(string.Join("\n", problems)); return; }

            var p = _project.Project;
            string oldName = p.Name;
            p.Name = copy.Name; p.Type = copy.Type; p.Import = copy.Import; p.Entry = copy.Entry; p.References = copy.References; p.Settings = copy.Settings;
            p.Notes = string.IsNullOrWhiteSpace(_notes) ? null : _notes.Trim();
            if (_workspace.Solution is { } solution && _solutionSettings != null)
            {
                solution.Settings = _solutionSettings;
                solution.Notes = string.IsNullOrWhiteSpace(_solutionNotes) ? null : _solutionNotes.Trim();
                // the startup project goes with a renamed project
                solution.Startup = string.Equals(_startup, oldName, StringComparison.OrdinalIgnoreCase) && p.Type == OutputType.Exe ? p.Name : _startup;
            }
            Close(true);
        }
    }
}
