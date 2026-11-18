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
    // Publish (docs/tools/spark.md, "Publish"; docs/tools/projects.md, "Publish"): the projects that have publishing on are built again where something changed and copied with what they
    // need into their publish folders (ProjectPublisher) - one build job: the build log, the error list, Cancel.
    public partial class MainWindow
    {
        /// <summary>After a build of a project: the files with the build action Copy (of the project and its libraries) next to the program - `output` is the program, or the folder of a
        /// toolchain that only writes files (docs/tools/projects.md, "Build actions"). What it copied goes into the build log.</summary>
        private void CopyContentNextTo(BuildPlan? plan, string? output)
        {
            if (plan == null || output == null) return;
            var items = ProjectContent.ItemsOf(plan);
            if (items.Count == 0) return;
            string folder = Directory.Exists(output) ? output : Path.GetDirectoryName(Path.GetFullPath(output))!;
            var result = ProjectContent.CopyTo(items, folder);
            foreach (var p in result.Problems) _buildLog.AppendLine($"{plan.Name}: warning: could not copy {p}");
            _buildLog.AppendLine($"{plan.Name}: {result.Copied.Count} file{(result.Copied.Count == 1 ? "" : "s")} (build action Copy) copied next to the program{(result.UpToDate > 0 ? $", {result.UpToDate} up to date" : "")}.");
        }

        /// <summary>Run > Publish: the project of the active document (else the start project).</summary>
        private async void Publish_Click(object? sender, RoutedEventArgs e) => await PublishProject(ContextProject() ?? _workspace.Startup);

        /// <summary>Run > Publish Solution, the Publish button of the Build tool strip: every project of the solution with publishing on.</summary>
        private async void PublishSolution_Click(object? sender, RoutedEventArgs e) => await PublishSolutionAsync();

        /// <summary>Publish of one project (the context menu of a project, Run > Publish). A project with publishing off is not published: the status bar says where to turn it on.</summary>
        private async Task PublishProject(LoadedProject? project)
        {
            if (project == null) { UpdateStatus("Publish needs a project: open a solution or a project (a single script is built with Build)."); return; }
            if (project.Project.Publish?.Enabled != true)
            {
                UpdateStatus($"Publishing is off for {project.Name}: turn it on in Project Properties > Publish.");
                await ShowProjectProperties(project, page: "publish");
                if (project.Project.Publish?.Enabled != true) return;
            }
            await RunPublish(new[] { project }, $"Publish {project.Name}");
        }

        private async Task PublishSolutionAsync()
        {
            if (!_workspace.IsOpen) { UpdateStatus("Publish Solution needs an open solution or project."); return; }
            var projects = ProjectPublisher.EnabledProjects(_workspace);
            if (projects.Count == 0)
            {
                UpdateStatus("No project of the solution has publishing on: turn it on in Project Properties > Publish.");
                if (_workspace.Projects.Count == 1) await PublishProject(_workspace.Projects[0]);
                return;
            }
            await RunPublish(projects, $"Publish Solution {_workspace.Name} ({projects.Count} project{(projects.Count == 1 ? "" : "s")})");
        }

        /// <summary>The publish as a build job: the open documents' text goes in (as for Build), the log into the build log, errors and warnings into the error list.</summary>
        private async Task RunPublish(IReadOnlyList<LoadedProject> projects, string what)
        {
            if (BuildRunning()) return;
            if (_nativeRun is { HasExited: false } || _nativePlainRunning) { UpdateStatus("A native program is running from its build folder - stop it first (Shift+F5)."); return; }
            UpdateStatus($"{what}...");
            BeginBuildLog(what);
            RevealTool("buildlog");
            var job = BeginBuildJob(what);
            // (the text of open documents is read on the interface thread, before the build runs elsewhere)
            var texts = new Dictionary<string, string>(ProjectFiles.PathComparer);
            foreach (var doc in _documents)
                if (doc.Script != null && FullPathOf(doc) is { } path) texts[path] = doc.Script.GetText();
            PublishResult result;
            try
            {
                result = await Task.Run(() => ProjectPublisher.Publish(_workspace, projects, new PublishOptions
                {
                    Monitor = job.Monitor,
                    TextOf = p => texts.TryGetValue(Path.GetFullPath(p), out var t) ? t : null,
                }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                EndBuildJob(job, BuildOutcome.Failed, ex.Message);
                UpdateStatus("Publish failed: " + ex.Message);
                return;
            }
            job.Feed.Drain();
            if (result.Cancelled || job.Cancelling)
            {
                EndBuildJob(job, BuildOutcome.Cancelled);
                return;
            }
            FinishBuild("Publish", result.Ok, result.Diagnostics.ToList(), null, null);
            if (result.Ok)
            {
                EndBuildJob(job, BuildOutcome.Succeeded);
                UpdateStatus(result.Summary);
                return;
            }
            var first = result.Projects.FirstOrDefault(p => !p.Ok && p.Error != null);
            EndBuildJob(job, BuildOutcome.Failed, first != null ? $"{first.Project.Name}: {first.Error}" : result.Summary);
            UpdateStatus($"Publish: {result.Summary} (see the build log and the error list).");
        }
    }
}
