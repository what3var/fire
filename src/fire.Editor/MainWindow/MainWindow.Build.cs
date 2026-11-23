using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using fire.Tooling;
using fire.Projects;
namespace fire.Editor
{
    // Build settings, native build, package manager, toolchain questions.
    public partial class MainWindow
    {
        private async void BuildSettings_Click(object? sender, RoutedEventArgs e)
        {
            // a file of a project: the settings are those of the project (they go before the tags in the source), not tags in this file
            if (ContextProject() is { } project)
            {
                await ShowProjectProperties(project, tab: 1);
                return;
            }
            if (ActiveScript is not { } script)
            {
                UpdateStatus("Build settings are only available for script tabs.");
                return;
            }

            var buildSettings = new AssemblyInfoDialog();
            var model = AssemblyInfo.FromSource(script.GetText());   // (the directives of the text; the build takes what forge reads)
            buildSettings.DataContext = model;

            if (await buildSettings.ShowDialog<bool?>(this) != true) return;
            model.CopyTo(_scriptAssemblyInfo);

            var directives = new List<(string, string?)>
            {
                ("noconsole", model.Subsystem == SubsystemType.GUI ? "" : null),
            };

            if (model.ExecutionMode == VmExecutionMode.Debug)
            {
                directives.Add(("debug", ""));
                directives.Add(("performance", null));
            }
            else if (model.ExecutionMode == VmExecutionMode.Performance)
            {
                directives.Add(("debug", null));
                directives.Add(("performance", ""));
            }
            else
            {
                directives.Add(("debug", null));
                directives.Add(("performance", null));
            }

            directives.Add(("name", model.ProductName));
            directives.Add(("codename", model.InternalName));
            directives.Add(("description", model.FileDescription));
            directives.Add(("author", model.CompanyName));
            directives.Add(("comments", model.Comments));
            directives.Add(("icon", model.IconPath));
            directives.Add(("version", model.ProductVersion));
            directives.Add(("fileversion", model.FileVersion));

            var formattedDirectives = new List<(string, string?)>();
            foreach (var directive in directives)
            {
                if (!string.IsNullOrEmpty(directive.Item2))
                {
                    formattedDirectives.Add((directive.Item1, $"\"{ValueUtils.EscapeString(directive.Item2)}\""));
                    continue;
                }
                formattedDirectives.Add(directive);
            }

            EnsureScriptHasDirectives(formattedDirectives);
        }

        private void EnsureScriptHasDirectives(IEnumerable<(string, string?)> directives)
        {
            if (ActiveScript is not { } script) return;
            var text = script.GetText();

            var textNew = new StringBuilder();
            var directivesAfter = directives.ToList();

            foreach (var line in text.AsSpan().EnumerateLines())
            {
                var match = false;
                foreach (var dir in directivesAfter.ToList())
                {
                    if (line.StartsWith($"#{dir.Item1}"))
                    {
                        match = true;
                        if (dir.Item2 != null)
                        {
                            if (dir.Item2.Length == 0)
                                textNew.AppendLine($"#{dir.Item1}");
                            else
                                textNew.AppendLine($"#{dir.Item1} {dir.Item2}");
                        }
                        directivesAfter.Remove(dir);
                        break;
                    }
                }
                if (!match)
                {
                    textNew.AppendLine(line.ToString());
                }
            }

            foreach (var dir in directivesAfter.Reverse<(string, string?)>())
            {
                if (dir.Item2 != null)
                {
                    if (dir.Item2.Length == 0)
                        textNew.Insert(0, $"#{dir.Item1}{Environment.NewLine}");
                    else
                        textNew.Insert(0, $"#{dir.Item1} {dir.Item2}{Environment.NewLine}");
                }
            }

            script.SetText(textNew.ToString());
        }

        /// <summary>The native build configuration that applies to `project` (null: the project of the active document, else the active script) - the nearest fire.native.json, else the
        /// defaults - and where it is saved.</summary>
        private fire.Tooling.NativeConfig LoadNativeConfig(out string savePath, LoadedProject? project = null)
        {
            string? file = (project ?? ContextProject())?.FilePath ?? ActiveScript?.FilePath;
            if (file == null)
            {
                savePath = Path.Combine(Directory.GetCurrentDirectory(), fire.Tooling.NativeConfig.FileName);
                return new fire.Tooling.NativeConfig();
            }
            var config = fire.Tooling.NativeConfig.FindFor(file);
            savePath = config.Path ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, fire.Tooling.NativeConfig.FileName);
            return config;
        }

        /// <summary>The package manager (ember): packages bring imports (`#import "name"`); after a change the open scripts are checked again (an import may have become available or gone).</summary>
        private async void PackageManager_Click(object? sender, RoutedEventArgs e)
        {
            var dialog = new PackageManagerDialog();
            await dialog.ShowDialog(this);
            if (dialog.Changed)
            {
                foreach (var doc in _documents) doc.Script?.Revalidate();
                LiveDiagnostics.Invalidate();
                ScheduleAllProjectChecks(TimeSpan.FromSeconds(1));   // (the files that are not open, too)
            }
        }

        private async void NativeBuildSettings_Click(object? sender, RoutedEventArgs e) => await ShowNativeBuildSettings(toolchainPage: false);

        /// <summary>Shows the native build settings (on the toolchain page when a toolchain is needed) and saves them; true when they were saved. A toolchain that works is also
        /// remembered for the whole machine (the natives of packages are compiled with it, too).</summary>
        private async Task<bool> ShowNativeBuildSettings(bool toolchainPage, Window? owner = null)
        {
            fire.Tooling.NativeConfig config;
            string savePath;
            try { config = LoadNativeConfig(out savePath); }
            catch (fire.Tooling.NativeConfigException ex) { await Dialogs.Message(owner ?? this, ex.Message, "Toolchain Settings"); return false; }
            var dialog = new NativeBuildDialog(config, savePath, _settings.IdfPath);
            if (toolchainPage) dialog.SelectToolchainTab();
            if (await dialog.ShowDialog<bool?>(owner ?? this) != true) return false;
            if (dialog.IdfPath != _settings.IdfPath)
            {
                _settings.IdfPath = dialog.IdfPath;   // (spark-settings.json `idfPath`: the ESP-IDF folder for every project)
                _settings.Save();
            }
            try
            {
                var chosen = dialog.Result.ResolveToolchain(dialog.Result.ResolveTarget());
                if (chosen.EffectiveKind is not ("files" or "custom") && fire.Tooling.ToolchainDetector.Find(chosen) is { } compilerPath)
                    fire.Tooling.ToolchainSetup.SaveMachineToolchain(new fire.Tooling.ToolchainDef { Extends = chosen.EffectiveKind, Kind = chosen.EffectiveKind, Compiler = compilerPath, Std = chosen.Std, Optimization = chosen.Optimization, Args = chosen.Args });
                dialog.Result.Save(savePath);
                RefreshTargetCombo();
                foreach (var doc in _documents) doc.Script?.InvalidateConditionalSymbols();   // the `#if` branches that are greyed out depend on the engine, the target and the defines
                UpdateStatus($"Saved {savePath}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Dialogs.Message(owner ?? this, ex.Message, "Toolchain Settings");
                return false;
            }
        }

        /// <summary>Runs `work` on the interface thread and returns its result to the caller, whichever thread that is. The questions of the toolchain provider come from any thread -
        /// also from the interface thread itself (compiling a script that imports a package with native code), where the dialog must run in a nested loop instead of blocking.</summary>
        private static T OnUiThread<T>(Func<Task<T>> work)
        {
            if (!Dispatcher.UIThread.CheckAccess())
                return Dispatcher.UIThread.InvokeAsync(work).GetAwaiter().GetResult();

            var task = work();
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
            Dispatcher.UIThread.PushFrame(frame);
            return task.GetAwaiter().GetResult();
        }

        /// <summary>The questions of the toolchain provider (a native build or a package with native code needs a C++ toolchain and there is none): they come from any thread, the
        /// answers are given on the interface thread. The text says why a toolchain is needed, so that a download or a compiler is no surprise.</summary>
        private void RegisterToolchainPrompts()
        {
            fire.Tooling.ToolchainProvider.Ask = request => OnUiThread(async () =>
            {
                var dialog = new ToolchainPromptDialog(request);
                await dialog.ShowDialog(this);
                return dialog.Choice;
            });
            fire.Tooling.ToolchainProvider.ChangeToolchain = () => OnUiThread(() => ShowNativeBuildSettings(toolchainPage: true));
            fire.Tooling.ToolchainProvider.RunInstall = work => OnUiThread(async () =>
            {
                var dialog = new ToolchainProgressDialog(work);
                await dialog.ShowDialog(this);
                return dialog.Succeeded;
            });
            fire.Tooling.ToolchainProvider.Log = message => Dispatcher.UIThread.Post(() => { UpdateStatus(message); _buildLog.AppendLine(message); });
        }

        // -----------------------------------------------------------
        // The target of a native build (the combo box of the build tool strip)
        // -----------------------------------------------------------

        /// <summary>The target chosen in the tool strip (null: the target of the project or of the native build settings).</summary>
        private string? _nativeTargetChoice;
        private bool _updatingTargetUi;

        /// <summary>Fills the combo box with the targets that the configuration of the active file knows (the built-in ones and those of its fire.native.json) and selects the chosen one.
        /// The first entry is the default and says which target that is.</summary>
        private void RefreshTargetCombo()
        {
            _updatingTargetUi = true;
            try
            {
                fire.Tooling.NativeConfig config;
                try { config = LoadNativeConfig(out _); }
                catch (fire.Tooling.NativeConfigException) { config = new fire.Tooling.NativeConfig(); }
                string defaultName;
                try { defaultName = config.ResolveTarget(ProjectTargetSetting()).Name; }
                catch (fire.Tooling.NativeConfigException) { defaultName = "?"; }
                var items = new List<ComboBoxItem> { new() { Content = $"Default ({TargetTitle(config, defaultName)})", Tag = "" } };
                items.AddRange(TargetTreeItems(config));
                cmbTarget.ItemsSource = items;
                int index = _nativeTargetChoice == null ? 0 : items.FindIndex(i => string.Equals((string?)i.Tag, _nativeTargetChoice, StringComparison.OrdinalIgnoreCase));
                cmbTarget.SelectedIndex = Math.Max(0, index);
            }
            finally { _updatingTargetUi = false; }
        }

        /// <summary>The targets as a tree: a heading per group (OS, Native, Embedded; not selectable), under it the targets with their display names (the name of a target
        /// as its tooltip). Tag: the name of the target (null for a heading).</summary>
        internal static IEnumerable<ComboBoxItem> TargetTreeItems(fire.Tooling.NativeConfig config)
        {
            var targets = config.TargetProfiles.ToList();
            foreach (var group in new[] { (fire.Tooling.TargetCategory.Os, "OS"), (fire.Tooling.TargetCategory.Native, "Native"), (fire.Tooling.TargetCategory.Embedded, "Embedded") })
            {
                var members = targets.Where(t => t.Category == group.Item1).ToList();
                if (members.Count == 0) continue;
                yield return new ComboBoxItem { Content = group.Item2, IsEnabled = false, FontWeight = Avalonia.Media.FontWeight.SemiBold, Tag = null };
                foreach (var t in members)
                    yield return new ComboBoxItem
                    {
                        Content = t.Title, Tag = t.Name, Padding = new Avalonia.Thickness(22, 3, 8, 3),
                        [ToolTip.TipProperty] = t.DisplayName != null ? t.Name : null,
                    };
            }
        }

        /// <summary>The display name of a target (its name when it has none or is unknown).</summary>
        internal static string TargetTitle(fire.Tooling.NativeConfig config, string name)
        {
            try { return config.ResolveTarget(name).Title; }
            catch (fire.Tooling.NativeConfigException) { return name; }
        }

        /// <summary>The target that the settings of the project of the active file name (null: none).</summary>
        private string? ProjectTargetSetting() =>
            ContextProject() is { } project ? ProjectSettings.Merge(project.Project.Settings, _workspace.Solution?.Settings).Target : null;

        private void cmbTarget_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_updatingTargetUi || cmbTarget.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            _nativeTargetChoice = tag.Length == 0 ? null : tag;
            _settings.NativeTarget = _nativeTargetChoice;
            _settings.Save();
            UpdateStatus(_nativeTargetChoice == null ? "Native target: the default." : $"Native target: {(cmbTarget.SelectedItem is ComboBoxItem { Content: string title } ? title : _nativeTargetChoice)}.");
        }

        // -----------------------------------------------------------
        // The build log and the list of errors and warnings of a build
        // -----------------------------------------------------------

        /// <summary>A new build: the log starts again (what the last build left in the error list stays until this one is done).</summary>
        private void BeginBuildLog(string what)
        {
            _buildLog.Clear();
            _buildLog.AppendLine($"------ Build started: {what} ------");
        }

        /// <summary>The end of a build: its errors and warnings go into the log, and into the error list, where they stay until the next build replaces them. `defaultFile` is the file
        /// of an error that the compiler could not place (the script that was built).</summary>
        private void FinishBuild(string what, bool ok, IReadOnlyList<Diagnostic> found, string? defaultFile, string? defaultPath)
        {
            var diagnostics = found.OrderBy(d => d.File ?? defaultFile ?? "", StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Line).ToList();   // (by place; errors and warnings of a line keep their order)
            foreach (var d in diagnostics) _buildLog.AppendLine((d.File == null ? d with { File = defaultFile } : d).ToLogLine());
            int errors = diagnostics.Count(d => d.IsError), warnings = diagnostics.Count(d => d.IsWarning);
            _buildLog.AppendLine($"========== {what}: {(ok ? "succeeded" : "failed")}, {errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")} ==========");
            _buildItems = diagnostics.Select(d => ToItem(d, defaultFile, defaultPath)).ToList();
            _buildItemsSource = $"Last build, {DateTime.Now:HH:mm:ss}";
            UpdateErrorPanel();
            McpBuildFinished(what, ok, errors, warnings);
        }

        /// <summary>Build (Run menu, the Build tool strip): always native - the project of the active document (a library is packed), else the active script, translated to C++ for
        /// the configured target and built with the configured toolchain (or the files of a project written).</summary>
        private async void BuildNative_Click(object? sender, RoutedEventArgs e) => await BuildProjectNative(ContextProject());

        /// <summary>A native build of `project` (null: the active script); see BuildNative_Click.</summary>
        private async Task BuildProjectNative(LoadedProject? project)
        {
            if (BuildRunning()) return;
            BuildPlan? plan = null;
            ScriptEditorControl? script = null;
            if (project != null)
            {
                if (project.Project.Type == OutputType.Library) { await PackLibrary(project); return; }
                plan = CreatePlan(project);
                if (!plan.IsValid) { await Dialogs.Message(this, string.Join(Environment.NewLine, plan.Errors), "Project problems"); return; }
            }
            else if (ActiveScript is not { } active)
            {
                UpdateStatus("A native build needs a script tab.");
                return;
            }
            else script = active;
            fire.Tooling.NativeConfig config;
            fire.Tooling.TargetProfile target;
            fire.Tooling.ToolchainDef toolchain;
            try
            {
                config = LoadNativeConfig(out _, project);
                target = config.ResolveTarget(_nativeTargetChoice ?? plan?.Settings.Target);   // the tool strip goes before the settings of the project, which go before the configuration file
                toolchain = config.ResolveToolchain(target, plan?.Settings.Toolchain, fire.Tooling.ToolchainPurpose.Build);
            }
            catch (fire.Tooling.NativeConfigException ex)
            {
                await Dialogs.Message(this, ex.Message, "Build Native");
                return;
            }

            string output;
            if (plan?.Settings.Output is { } fixedOutput && toolchain.EffectiveKind != "files") output = fixedOutput;
            else if (project != null)
            {
                // a project builds into its folder, without asking: bin/<target>-<mode>/ (a toolchain that only writes files: that folder)
                string folder = ProjectOutput.Folder(project.Directory, target.Name, RunModeName);
                Directory.CreateDirectory(folder);
                output = toolchain.EffectiveKind == "files" ? folder : ProjectOutput.ProgramPath(project.Directory, project.Name, target.Name, RunModeName, target.ProgramExtension);
            }
            else if (toolchain.EffectiveKind == "files")
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Folder for the project files ({target.Name})" });
                if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } folder) return;
                output = folder;
            }
            else
            {
                // (the extension of the target - `.exe` also for Windows 3.x and DOS; a program for this machine on Windows has one too)
                string extension = target.ProgramExtension.Length > 0 ? target.ProgramExtension : OperatingSystem.IsWindows() && target.Name == fire.Tooling.TargetProfile.Host.Name ? ".exe" : "";
                var path = await PickSavePath("Build Native", "program" + extension, extension.TrimStart('.'));
                if (path == null) return;
                output = path;
            }

            var mode = _session.ExecutionMode;
            string what = $"{(plan != null ? plan.Name : script!.FilePath != null ? Path.GetFileName(script.FilePath) : "script")} for {target.Name} ({toolchain.EffectiveKind})";
            UpdateStatus($"Building {what}...");
            BeginBuildLog(what);
            RevealTool("buildlog");
            string? defaultFile = plan != null ? null : ActiveDocument?.DisplayName;
            string? defaultPath = plan != null ? null : FullPathOf(ActiveDocument);
            if (BuildRunning()) return;   // (one may have started while the folder or file was asked for)
            var job = BeginBuildJob(what);
            // forge builds it (the same configuration, target and mode; the text of the editor as overlays): `forge build ... --engine native --report json`
            var overlays = OverlaysOfOpenDocuments();
            var args = new List<string> { "build" };
            args.AddRange(project != null ? ForgeProjectArgs(project) : new List<string> { ForgeScriptSource(ActiveDocument!, overlays) });
            args.AddRange(new[] { "--engine", "native", "-t", target.Name, "-m", ForgeMode(mode), "-o", output });
            if (config.Path != null) args.AddRange(new[] { "--config", config.Path });
            if (_settings.BuildStats) args.Add("--stats");   // (the Statistics switch: the report next to the program, shown below)
            AddQuickBuild(args);
            ForgeOutcome result;
            try
            {
                await EnsureToolchainAsync(toolchain);
                result = await RunForgeAsync(job, args, overlays);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                EndBuildJob(job, BuildOutcome.Failed, ex.Message);
                UpdateStatus("The native build failed: " + ex.Message);
                return;
            }
            job.Feed.Drain();
            if (result.Cancelled)
            {
                EndBuildJob(job, BuildOutcome.Cancelled);
                return;
            }
            // what the tools said (the C compiler with its stdout and stderr) is in the log already; then the errors and warnings of the fire compiler
            FinishBuild("Build", result.Ok, ForgeDiagnostics(result, overlays), defaultFile, defaultPath);
            if (result.Ok && result.Output("program", "folder") is { } built)
            {
                EndBuildJob(job, BuildOutcome.Succeeded);
                UpdateStatus($"Built {built} (native, {target.Name}{QuickBuildNote}).");
                if (_settings.BuildStats) ShowBuildStats(built + ".stats.json");
                return;
            }
            EndBuildJob(job, BuildOutcome.Failed, BuildErrorText(result));
            UpdateStatus("The native build failed.");
            if (!FailureShown(job)) await Dialogs.Message(this, BuildErrorText(result), "Build Native");
        }

        /// <summary>The hare of the build tool strip (and Run > Quick Build): every native build and publish spark starts is a quick build for tests (`forge --quick`; kept in
        /// the settings of spark). It glows while it is on - a quick build is no release.</summary>
        private void QuickBuild_Click(object? sender, RoutedEventArgs e)
        {
            _settings.QuickBuild = !_settings.QuickBuild;
            _settings.Save();
            ShowQuickBuild();
            UpdateStatus(_settings.QuickBuild
                ? $"Quick builds for tests: native builds and publishes with -O{_settings.QuickBuildLevel}, the program in units built side by side - not for releases."
                : "Normal builds: native builds and publishes with the toolchain's optimisation.");
        }

        /// <summary>The hare and the menu item as the setting is.</summary>
        private void ShowQuickBuild()
        {
            tbQuickBuild.IconData ??= ToolIcons.Geometry("Hare");
            tbQuickBuild.IsChecked = _settings.QuickBuild;
            mnuQuickBuild.IsChecked = _settings.QuickBuild;
            mnuQuickBuild.Header = $"_Quick Build (for tests: -O{(_settings.QuickBuildLevel == 1 ? 1 : 0)}, split)";
        }

        /// <summary>`--quick` (with the level of Tools > Options > Compilers) when the hare is on: for forge build (native) and forge publish.</summary>
        private void AddQuickBuild(List<string> args)
        {
            if (_settings.QuickBuild) args.Add(_settings.QuickBuildLevel == 1 ? "--quick=1" : "--quick");
        }

        /// <summary>", Schnellbuild -O0" in the status of a build while the hare is on.</summary>
        private string QuickBuildNote => _settings.QuickBuild ? $", Schnellbuild -O{_settings.QuickBuildLevel}" : "";

        /// <summary>The Statistics switch of the build tool strip: native builds write their statistics and show them in a tab (kept in the settings of spark).</summary>
        private void BuildStats_Click(object? sender, RoutedEventArgs e)
        {
            _settings.BuildStats = !_settings.BuildStats;
            tbBuildStats.IsChecked = _settings.BuildStats;
            _settings.Save();
            UpdateStatus(_settings.BuildStats ? "Native builds show their statistics." : "Native builds without statistics.");
        }

        /// <summary>The statistics of the build just made: its tab, opened or shown again with the new numbers.</summary>
        private void ShowBuildStats(string statsPath)
        {
            if (!File.Exists(statsPath))
            {
                UpdateStatus("The build wrote no statistics (the toolchain gives no map of the program, or linkage static with another target).");
                return;
            }
            var open = _documents.FirstOrDefault(d => d.Kind == DocumentKind.BuildStats && d.View.FilePath != null
                && string.Equals(Path.GetFullPath(d.View.FilePath), Path.GetFullPath(statsPath), StringComparison.OrdinalIgnoreCase));
            if (open != null) { open.View.ResetTo("", statsPath); Activate(open); }
            else OpenFile(statsPath, forceKind: DocumentKind.BuildStats);
        }

        /// <summary>The name of the mode of the run (`debug`, `release`, `performance`).</summary>
        private string RunModeName => _session.ExecutionMode.ToString().ToLowerInvariant();

        /// <summary>Build virtual: a program that carries the virtual machine (whatever the engine is) - the project of the active document into bin/vm-&lt;mode&gt;/, a single script where
        /// the user says. A library is packed.</summary>
        private async void Build_Click(object? sender, RoutedEventArgs e)
        {
            if (BuildRunning()) return;
            var project = ContextProject();
            // a library is not a program: building it means packing it as a package
            if (project?.Project.Type == OutputType.Library) { await PackLibrary(project); return; }

            bool windows = OperatingSystem.IsWindows();
            string? output = null;
            if (project != null && ProjectSettings.Merge(project.Project.Settings, _workspace.Solution?.Settings).Output is { } configured)
                output = Path.GetFullPath(configured, _workspace.Solution?.Directory ?? project.Directory);
            // a project builds into its folder, without asking: bin/vm-<mode>/ (the program carries the virtual machine); a single file asks where to
            else if (project != null) output = ProjectOutput.ProgramPath(project.Directory, project.Name, ProjectOutput.VmTarget, RunModeName, windows);
            output ??= await PickSavePath("Build virtual", "program" + (windows ? ".exe" : ""), windows ? "exe" : "");
            if (output == null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            await BuildVirtualAsync(project, output);
        }

        /// <summary>Build virtual through forge: `forge build ... --engine vm -o output` - the project (its files and libraries, the files with the build action Copy next to the
        /// program) or the active script, with the text of the editor.</summary>
        private async Task BuildVirtualAsync(LoadedProject? project, string output)
        {
            if (BuildRunning()) return;
            var doc = ActiveDocument;
            if (project != null)
            {
                var plan = CreatePlan(project);
                if (!plan.IsValid)
                {
                    UpdateStatus($"The project {project.Name} has problems: {plan.Errors[0]}");
                    await Dialogs.Message(this, string.Join(Environment.NewLine, plan.Errors), "Project problems");
                    return;
                }
            }
            else if (doc?.Script == null)
            {
                UpdateStatus(doc == null ? "No document is open." : "The active document is not a script - select a script tab to build.");
                return;
            }
            string what = $"{project?.Name ?? doc!.DisplayName} (virtual, {RunModeName})";
            UpdateStatus($"Building {what}...");
            BeginBuildLog(what);
            RevealTool("buildlog");
            var job = BeginBuildJob(what);
            var overlays = OverlaysOfOpenDocuments();
            var args = new List<string> { "build" };
            args.AddRange(project != null ? ForgeProjectArgs(project) : new List<string> { ForgeScriptSource(doc!, overlays) });
            args.AddRange(new[] { "--engine", "vm", "-m", ForgeMode(_session.ExecutionMode), "-o", output });
            ForgeOutcome result;
            try { result = await RunForgeAsync(job, args, overlays); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                EndBuildJob(job, BuildOutcome.Failed, ex.Message);
                UpdateStatus("Build virtual failed: " + ex.Message);
                return;
            }
            job.Feed.Drain();
            if (result.Cancelled) { EndBuildJob(job, BuildOutcome.Cancelled); return; }
            FinishBuild("Build virtual", result.Ok, ForgeDiagnostics(result, overlays), project != null ? null : doc?.DisplayName, project != null ? null : FullPathOf(doc));
            if (result.Ok)
            {
                EndBuildJob(job, BuildOutcome.Succeeded);
                UpdateStatus($"Built {result.Output("program") ?? output} (virtual, {RunModeName}).");
                return;
            }
            EndBuildJob(job, BuildOutcome.Failed, BuildErrorText(result));
            UpdateStatus("Build virtual failed (see the build log and the error list).");
            if (!FailureShown(job) && !_quietCompile) await Dialogs.Message(this, BuildErrorText(result), "Build virtual");
        }
    }
}
