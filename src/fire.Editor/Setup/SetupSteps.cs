using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using fire.Native;

namespace fire.Editor.Setup
{
    /// <summary>The real steps of the setup: the portable toolchains (<see cref="ToolchainSetup"/>, <see cref="WatcomSetup"/>), a model of ai.models, the standard packages
    /// (<c>StandardSources.EnsureInstalled</c>, its progress as the status bar shows it: <see cref="BackgroundWork"/>).</summary>
    public static class SetupSteps
    {
        /// <summary>The steps of a plan of the wizard.</summary>
        /// <summary>Set when spark's start has installed the standard packages (MainWindow.OnStandardPackagesReady): the package step of spark waits for it.</summary>
        public static readonly ManualResetEvent StartupInstalled = new(false);

        public static IReadOnlyList<ISetupStep> For(IEnumerable<SetupPlanItem> plan, IReadOnlyList<SetupModelInfo> catalog, string? modelFolder = null, WaitHandle? startup = null) =>
            plan.Select(p => p.Kind switch
            {
                SetupPlanKind.Toolchain => Toolchain(p),
                SetupPlanKind.Model => Model(p.Title, ModelCatalog.Find(catalog, p.Id) ?? throw new ArgumentException("No model " + p.Id), modelFolder),
                _ => Packages(p.Title, startup: startup),
            }).ToList();

        private static ISetupStep Toolchain(SetupPlanItem item) => new SetupStep(item.Title, ctx =>
        {
            ctx.Phase("Looking for the download");
            Action<string> log = line =>
            {
                if (line.StartsWith("Downloading... ", StringComparison.Ordinal)) return;   // (the bytes show that)
                if (line.StartsWith("Downloading ", StringComparison.Ordinal)) ctx.Phase("Downloading");
                else if (line.StartsWith("Unpacking", StringComparison.Ordinal)) ctx.Phase("Unpacking");
                ctx.Log(line);
            };
            if (item.Id == "watcom") WatcomSetup.Install(log, ctx.Bytes, ctx.Token);
            else if (item.Id == "tcc")
            {
                TinyccSetup.Install(line => { if (line.StartsWith("Checking", StringComparison.Ordinal)) ctx.Phase("Checking the SHA-256"); log(line); }, ctx.Bytes, ctx.Token);
            }
            else ToolchainSetup.InstallW64devkit(log, ctx.Bytes, ctx.Token);
        });

        /// <summary>Downloads and checks the model; the assistant then uses it (<see cref="RememberModel"/>).</summary>
        public static ISetupStep Model(string title, SetupModelInfo model, string? folder = null) => new SetupStep(title, ctx =>
        {
            string path = ModelDownload.Run(model, ctx, folder);
            RememberModel(model, folder);
            ctx.Log($"{model.Name} is ready: {path}");
        });

        /// <summary>The file of the assistant's settings (packages/assistant, Settings.File): assistant.json beside spark's settings.</summary>
        public static string AssistantSettingsFile => Path.Combine(ModelCatalog.DataFolder(), "fire", "assistant.json");

        /// <summary>Tells the assistant which model to take ("model" in assistant.json). Without that file the assistant takes the first model that is there - written then only
        /// when another one is there too (a new file would also skip the assistant's introduction at its first start).</summary>
        public static void RememberModel(SetupModelInfo model, string? folder = null, string? file = null, IReadOnlyList<SetupModelInfo>? catalog = null)
        {
            file ??= AssistantSettingsFile;
            try
            {
                JsonObject json;
                if (File.Exists(file)) json = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject();
                else
                {
                    bool others = (catalog ?? ModelCatalog.Load()).Any(m => m.Id != model.Id && ModelCatalog.Installed(m, folder));
                    if (!others) return;
                    json = new JsonObject { ["character"] = "sparky", ["muted"] = "", ["session"] = "" };
                }
                json["model"] = model.Id;
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, json.ToJsonString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }

        /// <summary>The first build of the standard packages: <c>StandardSources.EnsureInstalled</c> (spark started it already; this waits for it and then finds nothing to do -
        /// or does what the start could not, e.g. now that there is a toolchain). The progress is the one of the status bar: "Installing the package io (3/40)".</summary>
        public static ISetupStep Packages(string title, Func<Action<string>, IReadOnlyList<string>>? install = null, WaitHandle? startup = null) => new SetupStep(title, ctx =>
        {
            install ??= log => fire.Compiler.StandardSources.EnsureInstalled(log);
            ctx.Phase(startup != null ? "Waiting for the installation that spark started" : "Checking the standard packages");
            var problems = new List<string>();
            void Changed()
            {
                // (the package installation's own item; "Waiting for another program" is this step waiting for spark's start)
                var items = BackgroundWork.Items;
                var work = items.LastOrDefault(i => i.Text.StartsWith("Installing the package", StringComparison.Ordinal));
                if (work.Text != null)
                {
                    string text = work.Text;
                    int paren = text.LastIndexOf(" (", StringComparison.Ordinal);
                    ctx.Count(work.Done, work.Total, paren > 0 ? text.Substring(0, paren) : text);
                }
                else
                {
                    var building = items.LastOrDefault(i => i.Text.StartsWith("Building", StringComparison.Ordinal));
                    if (building.Text != null) ctx.Phase(building.Text);
                }
            }
            BackgroundWork.Changed += Changed;
            try
            {
                // spark's start installs them already (StartupInstalled): its progress is shown until it is through, then the check finds what is left
                if (startup != null && WaitHandle.WaitAny(new[] { startup, ctx.Token.WaitHandle }) == 1) ctx.Token.ThrowIfCancellationRequested();
                ctx.Phase("Checking the standard packages");
                var task = Task.Run(() => install(line =>
                {
                    ctx.Log(line);
                    if (line.Contains("could not be", StringComparison.Ordinal)) lock (problems) problems.Add(line);
                }));
                try { task.Wait(ctx.Token); }
                catch (OperationCanceledException) { throw; }   // (the installation goes on in the background: spark's start needs it anyway)
                catch (AggregateException ex) { throw ex.InnerException ?? ex; }
                var installed = task.Result;
                ctx.Log(installed.Count == 0 ? "The standard packages are up to date." : $"Installed: {string.Join(", ", installed)}.");
            }
            finally { BackgroundWork.Changed -= Changed; }
            lock (problems)
                if (problems.Count > 0) throw new InvalidOperationException(problems.Count == 1 ? problems[0] : $"{problems.Count} packages could not be installed (see the log): {problems[0]}");
        });
    }
}
