using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using fire.Native;
using fire.Runtime;
using static fire.Editor.SettingsLayout;

namespace fire.Editor
{
    /// <summary>
    /// Edits the native build configuration (<c>fire.native.json</c>, see <see cref="NativeConfig"/>) on pages chosen at the left (<see cref="SettingsLayout"/>): what Build does (VM or native), the target (platform package, includes,
    /// definitions, entry point) and the toolchain (which C++ compiler with which arguments). The dialog works on a copy; <see cref="Result"/> is the changed configuration.
    /// </summary>
    public sealed class NativeBuildDialog : Window
    {
        private readonly NativeConfig _config;
        private string? _currentTarget, _currentToolchain;
        private string? _targetSnapshot, _toolchainSnapshot;
        private bool _loading;
        private readonly SettingsNav _nav = new();

        // build
        private readonly Segmented segEngine = new("Virtual machine", "Native");
        // (the stripping levels, docs/spec/NATIVE_BACKEND.md "Größe des Programms"; the first entry: not set - the default, 3)
        private static readonly string[] StrippingChoices =
        {
            "Default (3 - all)", "0 - none: everything stays (like the VM)", "1 - protected: every class and public member stays",
            "2 - methods: every class with constructors, fields and properties stays", "3 - all: only what the program reaches",
        };
        private readonly ComboBox cmbStripping = Combo();
        // target
        private readonly ComboBox cmbTarget = Combo();
        private readonly TextBlock txtTargetHint = Hint("");
        private readonly AutoCompleteBox cmbPlatform = new() { MinimumPrefixLength = 0, Background = Field, HorizontalAlignment = HorizontalAlignment.Stretch };
        private readonly TextBox txtPlatformPath = TextField("(the platform package of the runtime)");
        private readonly Segmented segFloat = new("32 bit", "64 bit");
        private readonly TextBox txtStack = TextField(width: 120);
        private readonly CheckBox chkThreads = new() { Content = "The platform package implements fire threads" };
        private readonly CheckBox chkEmbedded = new() { Content = "A board without an operating system shell" };
        private readonly TextBox txtEntryName = TextField(mono: true);
        private readonly Segmented segEntryKind = new("Process", "Function");
        private readonly CheckBox chkExternC = new() { Content = "extern \"C\" (the board's startup code is C)" };
        private readonly TextBox txtIncludes = LinesField("freertos/FreeRTOS.h");
        private readonly TextBox txtDefines = LinesField("NAME or NAME=VALUE");
        private readonly TextBox txtTargetArgs = LinesField("-pthread", 50);
        private readonly TextBox txtTargetLibs = LinesField(null, 50);
        // the board of the target (ESP32: its serial port and the baud rate of its console - `port`/`baud` of the target), refilled for every target
        private readonly ContentControl boardHost = new();
        private string? _targetPort;
        private int? _targetBaud;
        // toolchain
        private static readonly string[] Kinds = { "gcc", "clang", "msvc", "custom", "files" };
        private readonly ComboBox cmbToolchain = Combo();
        private readonly TextBlock txtToolchainHint = Hint("");
        private readonly Segmented segKind = new(Kinds);
        private readonly TextBox txtCompiler = TextField("g++, clang++, cl or a full path", mono: true);
        private readonly TextBox txtStd = TextField("c++17", 120, mono: true);
        private readonly TextBox txtOptimization = TextField("-O2", 120, mono: true);
        private readonly CheckBox chkStrip = new() { Content = "Strip symbols" };
        private readonly Segmented segLayout = new("flat", "idf-component");
        private readonly TextBox txtBuildCommand = TextField("idf.py build", mono: true);
        private readonly TextBox txtCommand = TextField("{cpp} {dir} {out} {args} {libs}", mono: true);
        private readonly TextBox txtToolArgs = LinesField("-Wall");
        private readonly TextBox txtToolLibs = LinesField(null, 50);
        private readonly TextBox txtToolIncludes = LinesField(null, 50);

        public NativeConfig Result => _config;

        /// <summary>The ESP-IDF folder of spark (spark-settings.json `idfPath`; null: IDF_PATH) as the dialog leaves it - the caller saves it.</summary>
        public string? IdfPath { get; private set; }

        public NativeBuildDialog(NativeConfig config, string saveTo, string? idfPath = null)
        {
            IdfPath = idfPath;
            _config = NativeConfig.Parse(config.ToJson());   // a copy
            _config.Path = config.Path ?? saveTo;

            Title = "Toolchain Settings";
            Width = 840; Height = 600; MinWidth = 720; MinHeight = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;

            _nav.AddScope("Native build");
            _nav.Add("build", NavIcons.Cube, "Build", BuildPage());
            _nav.Add("target", NavIcons.Target, "Target", TargetPage());
            _nav.Add("toolchain", NavIcons.Wrench, "Toolchain", ToolchainPage());
            _nav.AddSeparator();
            _nav.AddScope("spark");
            _nav.Add("esp32", NavIcons.Chip, "ESP32 Board", Esp32Page());

            var save = DialogButton("Save", accent: true);
            save.IsDefault = true;
            save.Click += Save_Click;
            var cancel = DialogButton("Cancel");
            cancel.IsCancel = true;
            cancel.Click += Cancel_Click;
            bool exists = System.IO.File.Exists(_config.Path);
            var where = Hint($"Save writes {System.IO.Path.GetFileName(_config.Path)}{(exists ? "" : " (new)")} - next to the project, for all scripts and projects in that folder and below.");
            where.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(where, _config.Path);
            Content = Frame(_nav, where, cancel, save);
            _nav.Show("build");

            segEngine.SelectedIndex = _config.Engine?.ToLowerInvariant() == "native" ? 1 : 0;
            cmbStripping.ItemsSource = StrippingChoices;
            cmbStripping.SelectedIndex = _config.Stripping is { } level ? (int)level + 1 : 0;
            segKind.SelectionChanged += Kind_SelectionChanged;
            cmbTarget.SelectionChanged += Target_SelectionChanged;
            cmbToolchain.SelectionChanged += Toolchain_SelectionChanged;

            cmbPlatform.ItemsSource = NativeRuntimeFiles.AllPackages().ToList();
            FillTargets(_config.Target ?? TargetProfile.Host.Name);

            cmbToolchain.ItemsSource = _config.ToolchainNames.ToList();
            string? wanted = _config.Toolchain ?? _config.ResolveTarget(_config.Target).Native.Toolchain;
            cmbToolchain.SelectedItem = _config.ToolchainNames.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase))
                ?? _config.ToolchainNames.FirstOrDefault(n => ToolchainDetector.Find(ToolchainDef.BuiltIn.GetValueOrDefault(n) ?? new ToolchainDef()) != null) ?? "gcc";

            KeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(false); e.Handled = true; } };
        }

        // -------------------------------------------------------------------------------------------------------------
        // the pages
        // -------------------------------------------------------------------------------------------------------------
        private Control BuildPage() => Page("Build", "What Build and Start make of a program.",
            Group("Engine",
                Row("Default engine", segEngine),
                Hint("Virtual machine: one self-contained file with the interpreter. Native: C++ for the target, compiled with the toolchain. "
                    + "A project decides for itself in Project Properties > General > Runs as; this is the engine of the scripts and projects that do not.")),
            Group("Size",
                Row("Stripping", cmbStripping),
                Hint("What a native build leaves out of what the program does not reach (\"stripping\" in fire.native.json; a project can decide for itself, the command line "
                    + "with --stripping). Names given as literals to reflection always stay, [Keep] keeps a class or member; a debug build keeps everything.")),
            Group("File",
                Row("Settings file", new SelectableTextBlock { Text = _config.Path, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }),
                Hint("The targets and toolchains below are kept in this file (fire.native.json); the built-in ones need no file.")));

        private Control TargetPage()
        {
            var newTarget = new Button { Content = "New..." };
            newTarget.Click += NewTarget_Click;
            var browse = new Button { Content = "..." , Width = 32, HorizontalContentAlignment = HorizontalAlignment.Center };
            browse.Click += BrowsePlatform_Click;
            ToolTip.SetTip(cmbPlatform, "The folder native/platform/<name> of the runtime: everything that depends on the operating system or the board");
            ToolTip.SetTip(txtPlatformPath, "A folder with your own fire_platform.hpp; it is copied to platform/<name>/ next to the generated file");
            return Page("Target", "The system a native build runs on: the platform package of the runtime, the entry point and what the compiler gets for it.",
                Group("Target", Row("Target", WithButton(cmbTarget, newTarget)), txtTargetHint),
                Group("Platform",
                    Row("Platform package", cmbPlatform),
                    Row("Own platform folder", WithButton(txtPlatformPath, browse)),
                    Row("Float precision", segFloat),
                    Row("Thread stack (bytes)", txtStack),
                    Row("", chkThreads),
                    Row("", chkEmbedded)),
                Group("Entry point",
                    Row("Function name", txtEntryName),
                    Row("Starts as", segEntryKind),
                    Row("", Hint("Process: int main and an exit code. Function: the board calls it from a task.")),
                    Row("", chkExternC)),
                Group("Board", boardHost,
                    Hint("Run and Debug on an ESP32 flash the board on this serial port and talk to its console there (\"port\" and \"baud\" of the target; a project can set its own "
                        + "in Project Properties > Native & Toolchain). auto: the only USB serial port of this machine, else the one esptool uses.")),
                Group("Compiler input",
                    Columns(Block("Includes before the platform package", txtIncludes), Block("Definitions", txtDefines)),
                    Columns(Block("Compiler arguments", txtTargetArgs), Block("Libraries to link", txtTargetLibs)),
                    Hint("One entry per line. The arguments and libraries are the ones this platform always needs, whatever the toolchain.")));
        }

        private Control Esp32Page() => Page("ESP32 Board", "What spark needs to build, flash and debug a program on an ESP32 (the target esp32).",
            Group("ESP-IDF",
                Row("ESP-IDF folder", BoardFields.IdfFolder(IdfPath, v => IdfPath = v, () => this)),
                Hint("The folder of ESP-IDF (with export.sh / export.bat and tools/idf.py): F5 runs idf.py build flash with it. Kept in spark's settings (spark-settings.json), "
                    + "for every project; empty: the environment variable IDF_PATH.")),
            Group("Serial port",
                Hint("The serial port and the baud rate belong to the target (Target > Board) or to a project (Project Properties > Native & Toolchain).")));

        private Control ToolchainPage()
        {
            var newToolchain = new Button { Content = "New..." };
            newToolchain.Click += NewToolchain_Click;
            var detect = new Button { Content = "Detect" };
            detect.Click += Detect_Click;
            ToolTip.SetTip(detect, "Looks for a C++ toolchain on this machine (the portable w64devkit of fire first)");
            var test = new Button { Content = "Test" };
            test.Click += Test_Click;
            ToolTip.SetTip(test, "Compiles and runs a small program with the toolchain as it is set up here");
            var browse = new Button { Content = "...", Width = 32, HorizontalContentAlignment = HorizontalAlignment.Center };
            browse.Click += BrowseCompiler_Click;
            ToolTip.SetTip(txtOptimization, "-O2 (default), -O3, -O0 ... or 'size': a small program (-Os, the linker drops unused code)");
            ToolTip.SetTip(chkStrip, "Remove symbols and debug information (about half the size); switch off to debug the program");
            chkStrip.VerticalAlignment = VerticalAlignment.Center;
            return Page("Toolchain", "The C++ compiler that turns the generated code into a program, and what it gets.",
                Group("Toolchain", Row("Toolchain", WithButton(cmbToolchain, newToolchain)), Row("", txtToolchainHint), Row("", Line(detect, test))),
                Group("Compiler",
                    Row("Kind", segKind),
                    Row("Compiler", WithButton(txtCompiler, browse)),
                    Row("C++ standard", txtStd),
                    Row("Optimization", Line(txtOptimization, chkStrip))),
                Group("Output",
                    Row("Project layout", segLayout),
                    Row("Build command", txtBuildCommand),
                    Hint("Runs in the output folder after the files are written (e.g. idf.py build). With the layout idf-component a CMakeLists.txt for an ESP-IDF component is written, too."),
                    Row("Custom command", txtCommand),
                    Hint("For the kind custom: {cpp}, {dir}, {out}, {args} and {libs} are replaced.")),
                Group("Arguments",
                    Block("Compiler arguments", txtToolArgs),
                    Columns(Block("Libraries and linker arguments", txtToolLibs), Block("Include folders", txtToolIncludes)),
                    Hint("One entry per line.")));
        }

        private static Control WithButton(Control field, Button button)
        {
            var dock = new DockPanel();
            button.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(button, Avalonia.Controls.Dock.Right);
            dock.Children.Add(button);
            dock.Children.Add(field);
            return dock;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Target
        // -------------------------------------------------------------------------------------------------------------
        private void Target_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            StoreTarget();
            _currentTarget = SelectedTargetName();
            if (_currentTarget == null) return;
            LoadTarget(_config.ResolveTarget(_currentTarget));
        }

        /// <summary>Fills the choice of targets as a tree (OS, Native, Embedded; MainWindow.TargetTreeItems) and selects the target <paramref name="select"/>.</summary>
        private void FillTargets(string select)
        {
            var items = MainWindow.TargetTreeItems(_config).ToList();
            cmbTarget.ItemsSource = items;
            cmbTarget.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Tag as string, select, StringComparison.OrdinalIgnoreCase))
                ?? items.FirstOrDefault(i => string.Equals(i.Tag as string, TargetProfile.Host.Name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The name of the chosen target (null: none).</summary>
        private string? SelectedTargetName() => (cmbTarget.SelectedItem as ComboBoxItem)?.Tag as string;

        private void LoadTarget(TargetProfile t)
        {
            _loading = true;
            cmbPlatform.Text = t.Native.Platform;
            txtPlatformPath.Text = t.Native.PlatformPath ?? "";
            segFloat.SelectedIndex = t.FloatWidth == 32 ? 0 : 1;
            txtStack.Text = t.DefaultStackBytes.ToString();
            chkThreads.IsChecked = t.Native.SupportsThreads;
            chkEmbedded.IsChecked = t.IsEmbedded;
            txtEntryName.Text = t.Native.Entry.Name;
            segEntryKind.SelectedIndex = t.Native.Entry.Kind == EntryKind.Function ? 1 : 0;
            chkExternC.IsChecked = t.Native.Entry.ExternC;
            txtIncludes.Text = string.Join("\r\n", t.Native.Includes);
            txtDefines.Text = string.Join("\r\n", t.Native.Defines);
            txtTargetArgs.Text = string.Join("\r\n", t.Native.CompileArgs);
            txtTargetLibs.Text = string.Join("\r\n", t.Native.LinkLibs);
            txtTargetHint.Text = TargetHint(t);
            _targetPort = t.Native.Port;
            _targetBaud = t.Native.Baud;
            boardHost.Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Row("Serial port", BoardFields.Port(_targetPort, null, v => _targetPort = v)),
                    Row("Baud rate", BoardFields.Baud(_targetBaud, null, v => _targetBaud = v)),
                },
            };
            _loading = false;
            _targetSnapshot = TargetFromFields(t).ToJsonText();
        }

        private static string TargetHint(TargetProfile t) => t.Native.Platform switch
        {
            "freertos" or "esp32" => "FreeRTOS: fire threads are tasks. Definitions the platform package knows: FIRE_THREAD_STACK_BYTES, FIRE_THREAD_PRIORITY, FIRE_THREAD_CORE, FIRE_TLS_INDEX, FIRE_FREERTOS_STACK_BYTES (the stack is given in bytes). "
                + "The entry point is a function that your board code calls from a task.",
            "posix" => "Linux and macOS: threads are std::thread; the program needs -pthread (a compiler argument of the target).",
            "windows" => "Windows: threads are std::thread.",
            _ => "A platform package of your own: it needs a fire_platform.hpp (see native/platform/*/ for how the shipped ones look).",
        };

        private TargetDef TargetFromFields(TargetProfile current)
        {
            var def = new TargetDef
            {
                Platform = (cmbPlatform.Text ?? "").Trim(),
                PlatformPath = OrNull(txtPlatformPath.Text),
                FloatWidth = segFloat.SelectedIndex == 0 ? 32 : 64,
                SupportsThreads = chkThreads.IsChecked == true,
                Embedded = chkEmbedded.IsChecked == true,
                Entry = new EntryDef { Name = (txtEntryName.Text ?? "").Trim(), Kind = segEntryKind.SelectedIndex == 1 ? EntryKind.Function : EntryKind.Process, ExternC = chkExternC.IsChecked == true },
                Includes = Lines(txtIncludes.Text),
                Defines = Lines(txtDefines.Text),
                CompileArgs = Lines(txtTargetArgs.Text),
                LinkLibs = Lines(txtTargetLibs.Text),
                Toolchain = current.Native.Toolchain,
                Sdl = current.Native.Sdl,
                LineDirectives = current.Native.LineDirectives ? null : false,
                Port = _targetPort,
                Baud = _targetBaud,
            };
            if (int.TryParse((txtStack.Text ?? "").Trim(), out int stack) && stack > 0) def.StackBytes = stack;
            return def;
        }

        /// <summary>The fields of the target that is shown go into the configuration - when they were changed.</summary>
        private void StoreTarget()
        {
            if (_currentTarget == null || _targetSnapshot == null) return;
            var def = TargetFromFields(_config.ResolveTarget(_currentTarget));
            if (def.ToJsonText() == _targetSnapshot) return;
            if (_config.Targets.TryGetValue(_currentTarget, out var existing)) { def.Extends = existing.Extends; def.Symbols = existing.Symbols; def.Imports = existing.Imports; }
            _config.Targets[_currentTarget] = def;
            _targetSnapshot = def.ToJsonText();
        }

        private async void NewTarget_Click(object? sender, RoutedEventArgs e)
        {
            var asked = await AskNameAndBase("New target", "Name of the target", "Based on", _config.TargetNames.ToList(), _currentTarget);
            if (asked == null) return;
            var (name, baseName) = asked.Value;
            if (_config.TargetNames.Contains(name, StringComparer.OrdinalIgnoreCase)) { await Dialogs.Message(this, $"There is a target '{name}' already.", "New target"); return; }
            StoreTarget();
            _config.Targets[name] = new TargetDef { Extends = baseName };
            _loading = true;
            FillTargets(name);
            _loading = false;
        }

        private async void BrowsePlatform_Click(object? sender, RoutedEventArgs e)
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder with your platform package (fire_platform.hpp)", AllowMultiple = false });
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) txtPlatformPath.Text = path;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Toolchain
        // -------------------------------------------------------------------------------------------------------------
        private void Toolchain_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            StoreToolchain();
            _currentToolchain = cmbToolchain.SelectedItem as string;
            if (_currentToolchain == null) return;
            LoadToolchain(EffectiveToolchain(_currentToolchain));
        }

        private ToolchainDef EffectiveToolchain(string name)
        {
            var target = _config.ResolveTarget(_currentTarget);
            return _config.ResolveToolchain(target, name);
        }

        private void LoadToolchain(ToolchainDef t)
        {
            _loading = true;
            segKind.SelectedIndex = Math.Max(0, Array.IndexOf(Kinds, t.EffectiveKind));
            txtCompiler.Text = t.Compiler ?? "";
            txtStd.Text = t.Std ?? "";
            txtOptimization.Text = t.Optimization ?? "";
            chkStrip.IsChecked = t.Strip != false;
            segLayout.SelectedIndex = t.Layout == "idf-component" ? 1 : 0;
            txtBuildCommand.Text = t.BuildCommand ?? "";
            txtCommand.Text = t.Command ?? "";
            txtToolArgs.Text = string.Join("\r\n", t.Args ?? new List<string>());
            txtToolLibs.Text = string.Join("\r\n", t.Libs ?? new List<string>());
            txtToolIncludes.Text = string.Join("\r\n", t.IncludeDirs ?? new List<string>());
            _loading = false;
            UpdateToolchainHint();
            _toolchainSnapshot = ToolchainFromFields().ToJsonText();
        }

        private ToolchainDef ToolchainFromFields() => new()
        {
            Kind = segKind.SelectedLabel ?? "gcc",
            Compiler = OrNull(txtCompiler.Text), Std = OrNull(txtStd.Text), Optimization = OrNull(txtOptimization.Text),
            Strip = chkStrip.IsChecked == false ? false : null,
            Layout = segLayout.SelectedIndex == 1 ? "idf-component" : "flat",
            BuildCommand = OrNull(txtBuildCommand.Text), Command = OrNull(txtCommand.Text),
            Args = Lines(txtToolArgs.Text), Libs = Lines(txtToolLibs.Text), IncludeDirs = Lines(txtToolIncludes.Text),
        };

        private void StoreToolchain()
        {
            if (_currentToolchain == null || _toolchainSnapshot == null) return;
            var def = ToolchainFromFields();
            if (def.ToJsonText() == _toolchainSnapshot) return;
            _config.Toolchains[_currentToolchain] = def;
            _toolchainSnapshot = def.ToJsonText();
        }

        private void Kind_SelectionChanged(object? sender, EventArgs e) { if (!_loading) UpdateToolchainHint(); }

        private void UpdateToolchainHint()
        {
            var t = ToolchainFromFields();
            string kind = t.EffectiveKind;
            txtToolchainHint.Text = kind switch
            {
                "files" => "Only writes the C++ sources (and, with the layout idf-component, a CMakeLists.txt for an ESP-IDF component); a build command can run afterwards.",
                "custom" => "Runs your command; {cpp}, {dir}, {out}, {args} and {libs} are replaced.",
                _ => ToolchainDetector.Find(t) is { } path ? $"Found: {path}" : $"'{t.EffectiveCompiler}' was not found on this machine - install it or give the full path of a compiler.",
            };
            txtToolchainHint.Foreground = kind is not ("files" or "custom") && !txtToolchainHint.Text!.StartsWith("Found") ? EditorTheme.ErrorMark : EditorTheme.TextDim;
        }

        /// <summary>Opens the dialog on the toolchain page (when a toolchain is needed).</summary>
        public void SelectToolchainTab() => _nav.Show("toolchain");

        /// <summary>"Detect": looks for a toolchain on this machine - the portable w64devkit first - and fills in the kind and the compiler.</summary>
        private async void Detect_Click(object? sender, RoutedEventArgs e)
        {
            var found = ToolchainSetup.Detect();
            if (found == null)
            {
                await Dialogs.Message(this, ToolchainSetup.CanInstall
                    ? "No C++ toolchain was found on this machine. It can be installed automatically the next time a native build or a package with native code needs it (the portable w64devkit is downloaded)."
                    : "No C++ toolchain was found on this machine. Install g++ or clang++ with the package manager of your system.", "Detect toolchain");
                return;
            }
            string kind = found.Value.Toolchain.EffectiveKind;
            int k = Array.FindIndex(Kinds, n => string.Equals(n, kind, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) segKind.SelectedIndex = k;
            txtCompiler.Text = found.Value.Path;
            UpdateToolchainHint();
            await Dialogs.Message(this, $"Found: {found.Value.Path}", "Detect toolchain");
        }

        /// <summary>"Test": compiles and runs a small program with the toolchain as it is set up in the fields.</summary>
        private async void Test_Click(object? sender, RoutedEventArgs e)
        {
            var def = ToolchainFromFields();
            Cursor = new Cursor(StandardCursorType.Wait);
            fire.Compiler.NativeBuildResult result;
            try { result = await Task.Run(() => fire.Compiler.NativeBuilder.TestToolchain(def)); }
            finally { Cursor = null; }
            await Dialogs.Message(this, (result.Ok ? "The toolchain works.\n\n" : "The toolchain does not work.\n\n") + result.Log, "Test toolchain");
        }

        private async void NewToolchain_Click(object? sender, RoutedEventArgs e)
        {
            var asked = await AskNameAndBase("New toolchain", "Name of the toolchain", "Based on", _config.ToolchainNames.ToList(), _currentToolchain);
            if (asked == null) return;
            var (name, baseName) = asked.Value;
            if (_config.ToolchainNames.Contains(name, StringComparer.OrdinalIgnoreCase)) { await Dialogs.Message(this, $"There is a toolchain '{name}' already.", "New toolchain"); return; }
            StoreToolchain();
            _config.Toolchains[name] = new ToolchainDef { Extends = baseName };
            _loading = true;
            cmbToolchain.ItemsSource = _config.ToolchainNames.ToList();
            _loading = false;
            cmbToolchain.SelectedItem = name;
        }

        private async void BrowseCompiler_Click(object? sender, RoutedEventArgs e)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "C++ compiler", AllowMultiple = false });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) txtCompiler.Text = path;
        }

        // -------------------------------------------------------------------------------------------------------------
        private void Save_Click(object? sender, RoutedEventArgs e)
        {
            StoreTarget();
            StoreToolchain();
            _config.Engine = segEngine.SelectedIndex == 1 ? "native" : "vm";
            _config.Stripping = cmbStripping.SelectedIndex > 0 ? (StrippingLevel)(cmbStripping.SelectedIndex - 1) : null;
            _config.Target = SelectedTargetName();
            _config.Toolchain = cmbToolchain.SelectedItem as string;
            Close(true);
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

        private static List<string> Lines(string? text) =>
            (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        private static string? OrNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        /// <summary>A small question: a name and the entry it is based on.</summary>
        private async Task<(string Name, string Base)?> AskNameAndBase(string title, string nameLabel, string baseLabel, List<string> bases, string? selectedBase)
        {
            var name = new TextBox { Margin = new Thickness(0, 2, 0, 8) };
            var baseBox = new ComboBox { ItemsSource = bases, SelectedItem = selectedBase ?? bases.FirstOrDefault(), Margin = new Thickness(0, 2, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch };
            var ok = new Button { Content = "OK", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 8, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
            ok.Classes.Add("accent");
            var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(10) };
            panel.Children.Add(new TextBlock { Text = nameLabel });
            panel.Children.Add(name);
            panel.Children.Add(new TextBlock { Text = baseLabel });
            panel.Children.Add(baseBox);
            panel.Children.Add(buttons);
            var window = new Window { Title = title, Content = panel, Width = 340, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, CanResize = false };
            ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text)) window.Close(true); };
            cancel.Click += (_, _) => window.Close(false);
            if (await window.ShowDialog<bool>(this) != true) return null;
            return (name.Text!.Trim(), baseBox.SelectedItem as string ?? "");
        }
    }

    internal static class DefJson
    {
        private static readonly System.Text.Json.JsonSerializerOptions Options = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        public static string ToJsonText(this TargetDef def) => System.Text.Json.JsonSerializer.Serialize(def, Options);
        public static string ToJsonText(this ToolchainDef def) => System.Text.Json.JsonSerializer.Serialize(def, Options);
    }
}
