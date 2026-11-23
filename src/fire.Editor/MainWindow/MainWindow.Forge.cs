using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using fire.Tooling;
using fire.Projects;

namespace fire.Editor
{
    // spark builds through forge (docs/ROADMAP_C_PORT.md, L4; docs/tools/spark.md, "Builds"): Build, Build Solution, Build virtual, the build of a native run, Publish and Pack
    // Library run `forge build|publish ... --report json` as a child process (ForgeProcess) - its report goes into the build job (the build log, the window, the status bar), its
    // errors and warnings into the error list, Cancel stops it. The text of the editor that is not saved goes with it as overlays (ForgeOverlays), as spark always built what
    // the editor shows. What spark decides itself stays here: the target of the tool strip, the mode of the run, where the program goes, the toolchain questions.
    public partial class MainWindow
    {
        /// <summary>What forge builds a project from: the solution with -p (its settings apply), else the project file.</summary>
        private List<string> ForgeProjectArgs(LoadedProject project) =>
            _workspace.Solution?.FilePath is { } solution ? new List<string> { solution, "-p", project.Name } : new List<string> { project.FilePath };

        /// <summary>The changed documents (their text in the editor) for a build.</summary>
        private ForgeOverlays OverlaysOfOpenDocuments()
        {
            var overlays = new ForgeOverlays();
            foreach (var doc in _documents)
                if (doc.Script != null && doc.View.IsModified && FullPathOf(doc) is { } path) overlays.Add(path, doc.Script.GetText());
            return overlays;
        }

        /// <summary>The source of a single script for forge: its file (a change goes with the overlays), or - never saved - a copy in the folder of the overlays.</summary>
        private static string ForgeScriptSource(OpenDocument doc, ForgeOverlays overlays)
        {
            if (FullPathOf(doc) is { } path) return path;
            string name = doc.DisplayName;
            if (Path.GetExtension(name).Length == 0) name += ".fire";
            return overlays.AddUntitled(name, doc.Script!.GetText());
        }

        /// <summary>`-m` for the mode of the run.</summary>
        private static string ForgeMode(VmExecutionMode mode) => mode.ToString().ToUpperInvariant();

        /// <summary>Runs forge for <paramref name="job"/>: its log into the job's feed (the build log, the window), its progress into the job (the window, the status bar), its work
        /// without a window (the standard packages) into the status bar; the job's Cancel stops it. The overlays are removed afterwards.</summary>
        private async Task<ForgeOutcome> RunForgeAsync(BuildJob job, IEnumerable<string> args, ForgeOverlays overlays)
        {
            var all = args.Concat(overlays.Args).ToList();
            var sink = new ForgeSink
            {
                Log = job.Feed.Line,
                Progress = p => job.Monitor.Report(p),
                Background = item => { _childWork = item; Dispatcher.UIThread.Post(UpdateWorkStatus); },
            };
            try { return await Task.Run(() => ForgeProcess.Run(all, sink, job.Token)); }
            finally
            {
                overlays.Dispose();
                ChildWorkEnded();
            }
        }

        /// <summary>The errors and warnings of a build through forge - those in a script that was never saved belong to its document (no file: the default file of the build).</summary>
        private static List<Diagnostic> ForgeDiagnostics(ForgeOutcome outcome, ForgeOverlays overlays) =>
            outcome.Diagnostics.Select(d => d.File != null && overlays.Untitled.Contains(Path.GetFullPath(d.File)) ? d with { File = null } : d).ToList();

        /// <summary>Before a native build: when its toolchain is not on this machine, the questions of spark (install, change, cancel) - forge asks nobody. Whatever the answer,
        /// forge builds then (with what was installed or chosen, or it reports that there is no compiler).</summary>
        private static async Task EnsureToolchainAsync(fire.Tooling.ToolchainDef toolchain)
        {
            if (toolchain.EffectiveKind is "files" or "custom" or "esp-idf") return;
            bool there = await Task.Run(() =>
            {
                try { return fire.Tooling.ToolchainDetector.Find(toolchain) != null || fire.Tooling.ToolchainSetup.Detect() != null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { return true; }
            });
            if (!there) await Task.Run(() => fire.Tooling.ToolchainProvider.Require(toolchain, "A native build translates the program to C and compiles it with a C toolchain."));
        }

        /// <summary>The errors of a failed build for its window: the errors of the compiler, else the lines of the log that say error (what the C compiler said), else its end.</summary>
        private static string BuildErrorText(ForgeOutcome outcome)
        {
            var errors = outcome.Diagnostics.Where(d => d.IsError).Select(d => d.ToLogLine()).Take(6).ToList();
            var lines = outcome.Log.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var tail = lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(4).ToList();
            if (tail.Count == 0) tail = lines.TakeLast(4).ToList();
            var text = string.Join(Environment.NewLine, errors.Concat(tail).Distinct());
            return text.Length > 0 ? text : outcome.Error ?? "The build failed.";
        }
    }
}
