using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using fire.Tooling;
using fire.Projects;

namespace fire.Editor
{
    // Publish (docs/tools/spark.md, "Publish"; docs/tools/projects.md, "Publish"): the projects that have publishing on are built again where something changed and copied with what they
    // need into their publish folders (ProjectPublisher, run by `forge publish`) - one build job: the build log, the error list, Cancel. (The files with the build action Copy go next to
    // a program built by Build, too: forge copies them.)
    public partial class MainWindow
    {
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
            await RunPublish(new[] { project }, $"Publish {project.Name}", single: project);
        }

        private async Task PublishSolutionAsync()
        {
            if (!_workspace.IsOpen) { UpdateStatus("Publish Solution needs an open solution or project."); return; }
            var projects = ProjectBuilder.EnabledProjects(_workspace);
            if (projects.Count == 0)
            {
                UpdateStatus("No project of the solution has publishing on: turn it on in Project Properties > Publish.");
                if (_workspace.Projects.Count == 1) await PublishProject(_workspace.Projects[0]);
                return;
            }
            await RunPublish(projects, $"Publish Solution {_workspace.Name} ({projects.Count} project{(projects.Count == 1 ? "" : "s")})", single: null);
        }

        /// <summary>The publish as a build job, through forge (`forge publish <solution> [-p name] --report json`): the open documents' text goes in (as for Build), the log into the
        /// build log, errors and warnings into the error list. <paramref name="single"/>: one project (also when its publishing is off), else the projects with publishing on.</summary>
        private async Task RunPublish(IReadOnlyList<LoadedProject> projects, string what, LoadedProject? single)
        {
            if (BuildRunning()) return;
            if (_nativeRun is { HasExited: false } || _nativePlainRunning) { UpdateStatus("A native program is running from its build folder - stop it first (Shift+F5)."); return; }
            UpdateStatus($"{what}...");
            BeginBuildLog(what);
            RevealTool("buildlog");
            var job = BeginBuildJob(what);
            var overlays = OverlaysOfOpenDocuments();
            var args = new List<string> { "publish" };
            if (single != null) args.AddRange(ForgeProjectArgs(single));
            else args.Add(_workspace.FilePath ?? projects[0].FilePath);
            AddQuickBuild(args);   // (the hare: a quick publish - forge marks it in the log, the summary and the publish folder)
            ForgeOutcome result;
            try { result = await RunForgeAsync(job, args, overlays); }
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
            FinishBuild("Publish", result.Ok, ForgeDiagnostics(result, overlays), null, null);
            string summary = result.Summary ?? result.Error ?? "Publish failed.";
            if (result.Ok)
            {
                EndBuildJob(job, BuildOutcome.Succeeded);
                UpdateStatus(summary);
                return;
            }
            EndBuildJob(job, BuildOutcome.Failed, result.Error ?? summary);
            UpdateStatus($"Publish: {summary} (see the build log and the error list).");
        }
    }
}
