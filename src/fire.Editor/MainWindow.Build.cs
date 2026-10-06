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
using fire.Compiler;
using fire.Utilities;

namespace fire.Editor
{
    // Build settings, native build, package manager, toolchain questions.
    public partial class MainWindow
    {
        private async void BuildSettings_Click(object? sender, RoutedEventArgs e)
        {
            if (ActiveScript is not { } script)
            {
                UpdateStatus("Build settings are only available for script tabs.");
                return;
            }

            var buildSettings = new AssemblyInfoDialog();
            var model = Linker.ExtractAssemblyInfo(new[] { script.GetText() });
            buildSettings.DataContext = model;

            if (await buildSettings.ShowDialog<bool?>(this) != true) return;
            model.CopyTo(_scriptAssemblyInfo);

            var directives = new List<(string, string?)>
            {
                ("noconsole", model.Subsystem == SubsystemType.GUI ? "" : null),
            };

            if (model.ExecutionMode == Runtime.VmExecutionMode.Debug)
            {
                directives.Add(("debug", ""));
                directives.Add(("performance", null));
            }
            else if (model.ExecutionMode == Runtime.VmExecutionMode.Performance)
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

        /// <summary>The native build configuration that applies to the active script (the nearest fire.native.json, else the defaults) and where it is saved.</summary>
        private fire.Native.NativeConfig LoadNativeConfig(out string savePath)
        {
            string? file = ActiveScript?.FilePath;
            if (file == null)
            {
                savePath = Path.Combine(Directory.GetCurrentDirectory(), fire.Native.NativeConfig.FileName);
                return new fire.Native.NativeConfig();
            }
            var config = fire.Native.NativeConfig.FindFor(file);
            savePath = config.Path ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, fire.Native.NativeConfig.FileName);
            return config;
        }

        /// <summary>The package manager (ember): packages bring imports (`#import "name"`); after a change the open scripts are checked again (an import may have become available or gone).</summary>
        private async void PackageManager_Click(object? sender, RoutedEventArgs e)
        {
            var dialog = new PackageManagerDialog();
            await dialog.ShowDialog(this);
            if (dialog.Changed)
                foreach (var doc in _documents) doc.Script?.Revalidate();
        }

        private async void NativeBuildSettings_Click(object? sender, RoutedEventArgs e) => await ShowNativeBuildSettings(toolchainPage: false);

        /// <summary>Shows the native build settings (on the toolchain page when a toolchain is needed) and saves them; true when they were saved. A toolchain that works is also
        /// remembered for the whole machine (the natives of packages are compiled with it, too).</summary>
        private async Task<bool> ShowNativeBuildSettings(bool toolchainPage)
        {
            fire.Native.NativeConfig config;
            string savePath;
            try { config = LoadNativeConfig(out savePath); }
            catch (fire.Native.NativeConfigException ex) { await Dialogs.Message(this, ex.Message, "Native Build Settings"); return false; }
            var dialog = new NativeBuildDialog(config, savePath);
            if (toolchainPage) dialog.SelectToolchainTab();
            if (await dialog.ShowDialog<bool?>(this) != true) return false;
            try
            {
                var chosen = dialog.Result.ResolveToolchain(dialog.Result.ResolveTarget());
                if (chosen.EffectiveKind is not ("files" or "custom") && fire.Native.ToolchainDetector.Find(chosen) is { } compilerPath)
                    fire.Native.ToolchainSetup.SaveMachineToolchain(new fire.Native.ToolchainDef { Extends = chosen.EffectiveKind, Kind = chosen.EffectiveKind, Compiler = compilerPath, Std = chosen.Std, Optimization = chosen.Optimization, Args = chosen.Args });
                dialog.Result.Save(savePath);
                foreach (var doc in _documents) doc.Script?.InvalidateConditionalSymbols();   // the `#if` branches that are greyed out depend on the engine, the target and the defines
                UpdateStatus($"Saved {savePath}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Dialogs.Message(this, ex.Message, "Native Build Settings");
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
            fire.Native.ToolchainProvider.Ask = request => OnUiThread(async () =>
            {
                var dialog = new ToolchainPromptDialog(request);
                await dialog.ShowDialog(this);
                return dialog.Choice;
            });
            fire.Native.ToolchainProvider.ChangeToolchain = () => OnUiThread(() => ShowNativeBuildSettings(toolchainPage: true));
            fire.Native.ToolchainProvider.RunInstall = work => OnUiThread(async () =>
            {
                var dialog = new ToolchainProgressDialog(work);
                await dialog.ShowDialog(this);
                return dialog.Succeeded;
            });
            fire.Native.ToolchainProvider.Log = message => Dispatcher.UIThread.Post(() => UpdateStatus(message));
        }

        /// <summary>Translates the active script to C++ for the configured target and builds it with the configured toolchain (or writes the files of a project).</summary>
        private async void BuildNative_Click(object? sender, RoutedEventArgs e)
        {
            if (ActiveScript is not { } script)
            {
                UpdateStatus("A native build needs a script tab.");
                return;
            }
            fire.Native.NativeConfig config;
            fire.Runtime.TargetProfile target;
            fire.Native.ToolchainDef toolchain;
            try
            {
                config = LoadNativeConfig(out _);
                target = config.ResolveTarget();
                toolchain = config.ResolveToolchain(target);
            }
            catch (fire.Native.NativeConfigException ex)
            {
                await Dialogs.Message(this, ex.Message, "Build Native");
                return;
            }

            string output;
            if (toolchain.EffectiveKind == "files")
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Folder for the project files ({target.Name})" });
                if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } folder) return;
                output = folder;
            }
            else
            {
                bool windows = target.Name == "windows" || OperatingSystem.IsWindows();
                var path = await PickSavePath("Build Native", windows ? "program.exe" : "program", windows ? "exe" : "");
                if (path == null) return;
                output = path;
            }

            string source = script.GetText();
            string? baseDirectory = script.BaseDirectory;
            var mode = _session.ExecutionMode;
            UpdateStatus($"Building for {target.Name} ({toolchain.EffectiveKind})...");
            var result = await Task.Run(() =>
                fire.Compiler.NativeBuilder.BuildSafe(new[] { source }, config, target, toolchain, output, mode, null, false, baseDirectory));
            if (result.Ok)
            {
                UpdateStatus($"Built {result.Output} (native, {target.Name}).");
                return;
            }
            UpdateStatus("The native build failed.");
            await Dialogs.Message(this, result.Log, "Build Native");
        }

        private async void Build_Click(object? sender, RoutedEventArgs e)
        {
            // "Build" follows the configuration: with the native engine it builds natively
            try
            {
                if (string.Equals(LoadNativeConfig(out _).Engine, "native", StringComparison.OrdinalIgnoreCase)) { BuildNative_Click(sender, e); return; }
            }
            catch (fire.Native.NativeConfigException) { }

            bool windows = OperatingSystem.IsWindows();
            var path = await PickSavePath("Build standalone", windows ? "program.exe" : "program", windows ? "exe" : "");
            if (path == null) return;
            CompileAndPrepare(path);
        }
    }
}
