using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using fire.Compiler;
using fire.Projects;

namespace fire.Editor
{
    // Build Solution and Clean (docs/tools/spark.md, "Build, Build Solution and Clean"): every project of the solution built natively in the order of its references, one build
    // job (the build log, the error list, Cancel); Clean removes the build folders bin/ and obj/ of a project (ProjectOutput.Clean), never a source.
    public partial class MainWindow
    {
        private async void BuildSolution_Click(object? sender, RoutedEventArgs e) => await BuildSolutionAsync();

        /// <summary>Build Solution (Ctrl+Shift+B): the projects of the solution (or the single project) in the order of Workspace.BuildOrder - a library before whoever references it -
        /// each built natively for the chosen target: a program into bin/&lt;target&gt;-&lt;mode&gt;/ (or its `output`), a library compiled for that target (it is checked; its code goes
        /// into the programs that import it). A project whose library failed is skipped. The log goes into the build log, the errors and warnings of all projects into the error list;
        /// Cancel (Ctrl+Break, Shift+F5) stops the build after the tool that runs.</summary>
        private async Task BuildSolutionAsync()
        {
            if (BuildRunning()) return;
            if (!_workspace.IsOpen)
            {
                // without a solution Build Solution is Build (the active script)
                await BuildProjectNative(null);
                return;
            }
            var order = _workspace.BuildOrder();
            if (order.Count == 0) { UpdateStatus("The solution has no project to build."); return; }
            string name = _workspace.Name;
            string modeName = RunModeName;
            var mode = _session.ExecutionMode;
            string what = $"Solution {name} ({order.Count} project{(order.Count == 1 ? "" : "s")}, native, {modeName})";
            UpdateStatus($"Building {what}...");
            BeginBuildLog(what);
            RevealTool("buildlog");
            var job = BeginBuildJob(what);
            var diagnostics = new List<Diagnostic>();
            var failed = new HashSet<LoadedProject>();
            int succeeded = 0, skipped = 0;
            bool cancelled = false;
            string? firstError = null;
            void Fail(LoadedProject project, string message)
            {
                failed.Add(project);
                firstError ??= $"{project.Name}: {message}";
                _buildLog.AppendLine($"{project.Name}: error: {message.TrimEnd()}");
            }
            try
            {
                for (int i = 0; i < order.Count; i++)
                {
                    var project = order[i];
                    if (job.Cancelling) { cancelled = true; break; }
                    _buildLog.AppendLine($"------ {i + 1}/{order.Count} {project.Name} ({(project.Project.Type == OutputType.Library ? "library" : "program")}) ------");
                    if (_workspace.ReferencedProjects(project).FirstOrDefault(failed.Contains) is { } broken)
                    {
                        failed.Add(project);
                        skipped++;
                        _buildLog.AppendLine($"{project.Name}: skipped - the library {broken.Name} it references did not build.");
                        continue;
                    }
                    BuildPlan plan;
                    try { plan = CreatePlan(project); }
                    catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { Fail(project, ex.Message); continue; }
                    if (!plan.IsValid) { Fail(project, string.Join(Environment.NewLine, plan.Errors)); continue; }

                    fire.Native.NativeConfig config;
                    fire.Runtime.TargetProfile target;
                    fire.Native.ToolchainDef toolchain;
                    try
                    {
                        config = LoadNativeConfig(out _, project);
                        target = config.ResolveTarget(_nativeTargetChoice ?? plan.Settings.Target);
                        toolchain = config.ResolveToolchain(target, plan.Settings.Toolchain);
                    }
                    catch (fire.Native.NativeConfigException ex) { Fail(project, ex.Message); continue; }

                    if (project.Project.Type == OutputType.Library)
                    {
                        // a library has no program of its own: it is compiled for the target (what a program that imports it gets)
                        IReadOnlyList<Diagnostic>? errors = null;
                        await Task.Run(() =>
                        {
                            try { ProjectBuilder.Check(plan, target); }
                            catch (Exception ex) when (ex is ProjectException or fire.Package.Manager.PackageException or fire.Native.NativeConfigException || CommandLineRunner.IsCompileErrorForEditor(ex))
                            { errors = CompileDiagnostics.Of(ex); }
                        });
                        if (errors != null)
                        {
                            diagnostics.AddRange(errors);
                            Fail(project, errors.FirstOrDefault()?.Message ?? "it does not compile");
                        }
                        else { succeeded++; _buildLog.AppendLine($"{project.Name}: compiled for {target.Name} (a library: its code goes into the programs that import it)."); }
                        continue;
                    }

                    string output;
                    if (plan.Settings.Output is { } fixedOutput && toolchain.EffectiveKind != "files") output = fixedOutput;
                    else
                    {
                        string folder = ProjectOutput.Folder(project.Directory, target.Name, modeName);
                        Directory.CreateDirectory(folder);
                        output = toolchain.EffectiveKind == "files" ? folder : ProjectOutput.ProgramPath(project.Directory, project.Name, target.Name, modeName, target.ProgramExtension);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                    config.Stats = _settings.BuildStats;
                    var result = await Task.Run(() => NativeBuilder.BuildSafe(plan.SourceTexts, config, target, toolchain, output, mode, null, false, null, null, plan, job.Monitor));
                    AppendBuildResultLog(job, result.Log);
                    if (result.Cancelled || job.Cancelling) { cancelled = true; break; }
                    diagnostics.AddRange(result.Diagnostics ?? Array.Empty<Diagnostic>());
                    if (result.Ok) { succeeded++; _buildLog.AppendLine($"{project.Name}: built {result.Output} ({target.Name})."); }
                    else Fail(project, BuildErrorText(result));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                EndBuildJob(job, BuildOutcome.Failed, ex.Message);
                UpdateStatus("Build Solution failed: " + ex.Message);
                return;
            }
            if (cancelled)
            {
                EndBuildJob(job, BuildOutcome.Cancelled);
                return;
            }
            int failedBuilds = failed.Count - skipped;
            _buildLog.AppendLine($"Projects: {succeeded} succeeded, {failedBuilds} failed, {skipped} skipped.");
            FinishBuild("Build Solution", failed.Count == 0, diagnostics, null, null);
            if (failed.Count == 0)
            {
                EndBuildJob(job, BuildOutcome.Succeeded);
                UpdateStatus($"Built the solution {name}: {succeeded} project{(succeeded == 1 ? "" : "s")}.");
                return;
            }
            EndBuildJob(job, BuildOutcome.Failed, firstError);
            UpdateStatus($"Build Solution: {failedBuilds} project{(failedBuilds == 1 ? "" : "s")} failed{(skipped > 0 ? $", {skipped} skipped" : "")} (see the build log and the error list).");
        }

        /// <summary>Clean: the build folders of the project of the active document (else the start project).</summary>
        private async void Clean_Click(object? sender, RoutedEventArgs e)
        {
            var project = ContextProject() ?? _workspace.Startup;
            if (project == null) { UpdateStatus("Clean needs a project: a single script has no build folders (its programs are where they were saved)."); return; }
            await CleanProjects(new[] { project }, project.Name);
        }

        /// <summary>Clean Solution: the build folders of every project of the solution.</summary>
        private async void CleanSolution_Click(object? sender, RoutedEventArgs e)
        {
            if (!_workspace.IsOpen) { UpdateStatus("Clean Solution needs an open solution or project."); return; }
            await CleanProjects(_workspace.Projects.ToList(), $"Solution {_workspace.Name}");
        }

        /// <summary>Removes the build folders (bin/, obj/) of the projects - not while a build or a native run uses them. The log says what went and what stayed.</summary>
        private async Task CleanProjects(IReadOnlyList<LoadedProject> projects, string what)
        {
            if (projects.Count == 0 || BuildRunning()) return;
            if (_nativeRun is { HasExited: false } || _nativePlainRunning) { UpdateStatus("A native program is running from its build folder - stop it first (Shift+F5)."); return; }
            _buildLog.Clear();
            _buildLog.AppendLine($"------ Clean started: {what} ------");
            RevealTool("buildlog");
            var results = await Task.Run(() => projects.Select(p => (Project: p, Result: ProjectOutput.Clean(p))).ToList());
            int removed = 0, kept = 0;
            foreach (var (project, result) in results)
            {
                foreach (var folder in result.Removed) { _buildLog.AppendLine($"{project.Name}: removed {ProjectFiles.Relative(project.Directory, folder)}/"); removed++; }
                foreach (var reason in result.Kept) { _buildLog.AppendLine($"{project.Name}: kept {reason}"); kept++; }
                if (result.Removed.Count == 0 && result.Kept.Count == 0) _buildLog.AppendLine($"{project.Name}: nothing to clean.");
            }
            _buildLog.AppendLine($"========== Clean: {removed} folder{(removed == 1 ? "" : "s")} removed{(kept > 0 ? $", {kept} kept" : "")} ==========");
            UpdateStatus(kept > 0 ? $"Cleaned {what}: {removed} folder(s) removed, {kept} kept (see the build log)." : $"Cleaned {what}: {removed} build folder{(removed == 1 ? "" : "s")} removed.");
        }
    }
}
