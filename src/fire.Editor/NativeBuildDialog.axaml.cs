using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using fire.Native;
using fire.Runtime;

namespace fire.Editor
{
    /// <summary>
    /// Edits the native build configuration (<c>fire.native.json</c>, see <see cref="NativeConfig"/>): what Build does (VM or native), the target (platform package, includes,
    /// definitions, entry point) and the toolchain (which C++ compiler with which arguments). The dialog works on a copy; <see cref="Result"/> is the changed configuration.
    /// </summary>
    public partial class NativeBuildDialog : Window
    {
        private readonly NativeConfig _config;
        private string? _currentTarget, _currentToolchain;
        private string? _targetSnapshot, _toolchainSnapshot;
        private bool _loading;

        public NativeConfig Result => _config;

        public NativeBuildDialog(NativeConfig config, string saveTo)
        {
            InitializeComponent();
            _config = NativeConfig.Parse(config.ToJson());   // a copy
            _config.Path = config.Path ?? saveTo;
            txtConfigPath.Text = _config.Path + (System.IO.File.Exists(_config.Path) ? "" : "  (new)");

            (_config.Engine?.ToLowerInvariant() == "native" ? rbEngineNative : rbEngineVm).IsChecked = true;

            cmbPlatform.ItemsSource = NativeRuntimeFiles.AllPackages().ToList();
            cmbTarget.ItemsSource = _config.TargetNames.ToList();
            cmbTarget.SelectedItem = _config.TargetNames.FirstOrDefault(n => string.Equals(n, _config.Target ?? TargetProfile.Host.Name, StringComparison.OrdinalIgnoreCase)) ?? TargetProfile.Host.Name;

            cmbToolchain.ItemsSource = _config.ToolchainNames.ToList();
            string? wanted = _config.Toolchain ?? _config.ResolveTarget(_config.Target).Native.Toolchain;
            cmbToolchain.SelectedItem = _config.ToolchainNames.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase))
                ?? _config.ToolchainNames.FirstOrDefault(n => ToolchainDetector.Find(ToolchainDef.BuiltIn.GetValueOrDefault(n) ?? new ToolchainDef()) != null) ?? "gcc";
        }

        // -------------------------------------------------------------------------------------------------------------
        // Target
        // -------------------------------------------------------------------------------------------------------------
        private void Target_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            StoreTarget();
            _currentTarget = cmbTarget.SelectedItem as string;
            if (_currentTarget == null) return;
            LoadTarget(_config.ResolveTarget(_currentTarget));
        }

        private void LoadTarget(TargetProfile t)
        {
            _loading = true;
            cmbPlatform.Text = t.Native.Platform;
            txtPlatformPath.Text = t.Native.PlatformPath ?? "";
            cmbFloat.SelectedIndex = t.FloatWidth == 32 ? 0 : 1;
            txtStack.Text = t.DefaultStackBytes.ToString();
            chkThreads.IsChecked = t.Native.SupportsThreads;
            chkEmbedded.IsChecked = t.IsEmbedded;
            txtEntryName.Text = t.Native.Entry.Name;
            cmbEntryKind.SelectedIndex = t.Native.Entry.Kind == EntryKind.Function ? 1 : 0;
            chkExternC.IsChecked = t.Native.Entry.ExternC;
            txtIncludes.Text = string.Join("\r\n", t.Native.Includes);
            txtDefines.Text = string.Join("\r\n", t.Native.Defines);
            txtTargetArgs.Text = string.Join("\r\n", t.Native.CompileArgs);
            txtTargetLibs.Text = string.Join("\r\n", t.Native.LinkLibs);
            txtTargetHint.Text = TargetHint(t);
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
                Platform = cmbPlatform.Text.Trim(),
                PlatformPath = string.IsNullOrWhiteSpace(txtPlatformPath.Text) ? null : txtPlatformPath.Text.Trim(),
                FloatWidth = cmbFloat.SelectedIndex == 0 ? 32 : 64,
                SupportsThreads = chkThreads.IsChecked == true,
                Embedded = chkEmbedded.IsChecked == true,
                Entry = new EntryDef { Name = txtEntryName.Text.Trim(), Kind = cmbEntryKind.SelectedIndex == 1 ? EntryKind.Function : EntryKind.Process, ExternC = chkExternC.IsChecked == true },
                Includes = Lines(txtIncludes.Text),
                Defines = Lines(txtDefines.Text),
                CompileArgs = Lines(txtTargetArgs.Text),
                LinkLibs = Lines(txtTargetLibs.Text),
                Toolchain = current.Native.Toolchain,
            };
            if (int.TryParse(txtStack.Text.Trim(), out int stack) && stack > 0) def.StackBytes = stack;
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
            cmbTarget.ItemsSource = _config.TargetNames.ToList();
            _loading = false;
            cmbTarget.SelectedItem = name;
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
            cmbKind.SelectedIndex = Math.Max(0, new[] { "gcc", "clang", "msvc", "custom", "files" }.ToList().IndexOf(t.EffectiveKind));
            txtCompiler.Text = t.Compiler ?? "";
            txtStd.Text = t.Std ?? "";
            txtOptimization.Text = t.Optimization ?? "";
            cmbLayout.SelectedIndex = t.Layout == "idf-component" ? 1 : 0;
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
            Kind = (cmbKind.SelectedItem as ComboBoxItem)?.Content as string ?? "gcc",
            Compiler = OrNull(txtCompiler.Text), Std = OrNull(txtStd.Text), Optimization = OrNull(txtOptimization.Text),
            Layout = cmbLayout.SelectedIndex == 1 ? "idf-component" : "flat",
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

        private void Kind_SelectionChanged(object? sender, SelectionChangedEventArgs e) { if (!_loading) UpdateToolchainHint(); }

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
        }

        /// <summary>Opens the dialog on the toolchain page (when a toolchain is needed).</summary>
        public void SelectToolchainTab() => tabs.SelectedIndex = 1;

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
            for (int i = 0; i < cmbKind.Items.Count; i++)
                if (cmbKind.Items[i] is ComboBoxItem item && string.Equals(item.Content as string, kind, StringComparison.OrdinalIgnoreCase)) cmbKind.SelectedIndex = i;
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
            _config.Engine = rbEngineNative.IsChecked == true ? "native" : "vm";
            _config.Target = cmbTarget.SelectedItem as string;
            _config.Toolchain = cmbToolchain.SelectedItem as string;
            Close(true);
        }

        private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);

        private static List<string> Lines(string text) =>
            text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        private static string? OrNull(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

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
