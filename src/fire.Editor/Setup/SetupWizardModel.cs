using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using fire.Tooling;
namespace fire.Editor.Setup
{
    public enum SetupPage { Welcome, Toolchains, Model, Summary }

    /// <summary>A toolchain that was found on this machine.</summary>
    public sealed record DetectedToolchain(string Kind, string Name, string Path, string? Version = null)
    {
        /// <summary>Does it compile C++ (TinyCC compiles only C)?</summary>
        public bool CompilesCpp => Kind is "gcc" or "clang" or "msvc";
    }

    /// <summary>What the wizard looks at: the system, the machine, the assistant and the toolchains that are there. <see cref="Current"/> for the real machine; the tests make their own.</summary>
    public sealed record SetupEnvironment(bool IsWindows, bool IsMacOS, bool IsX64, MachineInfo Machine, bool AssistantAvailable, bool AssistantEnabled,
        IReadOnlyList<DetectedToolchain> Detected, bool W64devkitThere = false, bool WatcomThere = false, string? ModelFolder = null,
        bool TinyccPinned = false, bool TinyccThere = false)
    {
        public bool IsLinux => !IsWindows && !IsMacOS;

        /// <summary>This machine: the toolchains fire finds (as a build looks for them), whether the assistant came with spark (forge and Assistant/assistant.fire).</summary>
        public static SetupEnvironment Current(bool assistantEnabled)
        {
            var detected = new List<DetectedToolchain>();
            foreach (var (kind, name) in new[] { ("gcc", "GCC (g++)"), ("clang", "Clang (clang++)"), ("msvc", "Visual C++ (cl)"), ("tcc", "TinyCC (C only)"), ("watcom", "Open Watcom v2"), ("emscripten", "Emscripten (WebAssembly)") })
            {
                try
                {
                    if (ToolchainDef.BuiltIn.TryGetValue(kind, out var def) && ToolchainDetector.Find(def) is { } path)
                        detected.Add(new DetectedToolchain(kind, name, path, MachineToolchains.Kinds.Contains(kind) ? MachineToolchains.VersionOf(path, kind) : null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
            string assistant = Path.Combine(AppContext.BaseDirectory, "Assistant", "assistant.fire");
            string forge = Forge.Locate(out _) ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "forge.exe" : "forge");
            return new SetupEnvironment(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64,
                MachineInfo.Current(), File.Exists(assistant) && File.Exists(forge), assistantEnabled, detected,
                W64devkitThere: File.Exists(Path.Combine(ToolchainSetup.W64devkitDirectory, "bin", "g++.exe")),
                WatcomThere: Directory.Exists(WatcomSetup.Directory),
                TinyccPinned: TinyccSetup.Download.Sha256.Length == 64, TinyccThere: TinyccSetup.Installed);
        }
    }

    /// <summary>A toolchain fire can download (the portable ones, kept next to spark in Toolchain\).</summary>
    public sealed class ToolchainOption
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required long DownloadBytes { get; init; }
        public required string Folder { get; init; }
        /// <summary>Can it be downloaded on this machine?</summary>
        public bool Installable { get; init; }
        /// <summary>Is it there already (then it is not offered again)?</summary>
        public bool There { get; init; }
        public bool Recommended { get; init; }
        public bool Selected { get; set; }
        public string SizeText => "about " + SetupFormat.Size(DownloadBytes);
    }

    /// <summary>A model of the catalog as the wizard offers it.</summary>
    public sealed class ModelOption
    {
        public required SetupModelInfo Info { get; init; }
        public bool Fits { get; init; }
        public bool Installed { get; init; }
        public bool Recommended { get; init; }
        /// <summary>The stronger model that this machine is good enough for (ai.models' Stronger).</summary>
        public bool Stronger { get; init; }
        /// <summary>"needs 2 GB memory, good with 4 GB".</summary>
        public string MemoryText => $"needs {Gb(Info.MinMemoryMB)} memory, good with {Gb(Info.GoodMemoryMB)}";
        private static string Gb(int mb) => mb % 1024 == 0 ? $"{mb / 1024} GB" : $"{mb / 1024.0:0.#} GB";
    }

    /// <summary>What the setup does after the wizard (the items of the progress window).</summary>
    public enum SetupPlanKind { Toolchain, Model, Packages }

    public sealed record SetupPlanItem(SetupPlanKind Kind, string Id, string Title, long DownloadBytes = 0);

    /// <summary>
    /// The setup wizard of spark at its first start (and Help > Setup Wizard): the pages welcome, toolchains (Windows: the portable toolchains to download, the recommended one ticked;
    /// Linux and macOS: what was found and how to get the rest), the model of the assistant (only when the assistant is switched on: the models of ai.models' catalog with size and
    /// memory, the one for this machine ticked, or none) and the summary. <see cref="Plan"/> is what the progress window then does.
    /// </summary>
    public sealed class SetupWizardModel
    {
        /// <param name="compilers">The compilers the machine chose before (toolchain.json; null: automatic for both).</param>
        public SetupWizardModel(SetupEnvironment env, IReadOnlyList<SetupModelInfo> catalog, MachineToolchains? compilers = null)
        {
            Environment = env;
            Catalog = catalog;
            var pages = new List<SetupPage> { SetupPage.Welcome, SetupPage.Toolchains };
            if (env.AssistantEnabled && env.AssistantAvailable && catalog.Count > 0) pages.Add(SetupPage.Model);
            pages.Add(SetupPage.Summary);
            Pages = pages;

            Compilers = new CompilerChoiceModel(env, compilers);
            bool watcomThere = env.WatcomThere || env.Detected.Any(d => d.Kind == "watcom");
            Watcom = new ToolchainOption
            {
                Id = "watcom", Name = "Open Watcom v2",
                Description = "Only for the old PC targets: DOS, Windows 3.x and BIOS. Portable as well; spark offers it again when such a target is built.",
                DownloadBytes = WatcomSetup.DownloadBytes, Folder = WatcomSetup.Directory,
                Installable = env.IsWindows && env.IsX64 && !watcomThere, There = watcomThere,
            };

            var recommended = ModelCatalog.Recommended(catalog, env.Machine);
            var stronger = ModelCatalog.Stronger(catalog, env.Machine);
            Models = catalog.Select(m => new ModelOption
            {
                Info = m, Fits = ModelCatalog.Fits(m, env.Machine), Installed = ModelCatalog.Installed(m, env.ModelFolder),
                Recommended = m == recommended, Stronger = m == stronger && m != recommended,
            }).ToList();
            // ticked: a model that is there already (nothing to download), else the one for this machine
            SelectedModelId = Models.FirstOrDefault(m => m.Installed)?.Info.Id ?? recommended?.Id ?? "";
        }

        public SetupEnvironment Environment { get; }
        public IReadOnlyList<SetupModelInfo> Catalog { get; }
        public IReadOnlyList<SetupPage> Pages { get; }
        /// <summary>The compilers for Build and Publish (the grid of the toolchain page, the same as Tools > Options > Compilers) with "Download missing toolchains".</summary>
        public CompilerChoiceModel Compilers { get; }
        /// <summary>Open Watcom (the old PC targets): optional, below the grid.</summary>
        public ToolchainOption Watcom { get; }
        public IReadOnlyList<ModelOption> Models { get; }

        /// <summary>The model to download ("" none: the assistant asks later).</summary>
        public string SelectedModelId
        {
            get => _model;
            set
            {
                if (value != "" && Models.FirstOrDefault(m => m.Info.Id == value) is not { Fits: true }) return;   // (unknown, or too big for this machine)
                _model = value;
            }
        }
        private string _model = "";

        public ModelOption? SelectedModel => Models.FirstOrDefault(m => m.Info.Id == _model);

        // ---- the pages ----

        public int PageIndex { get; private set; }
        public SetupPage Page => Pages[PageIndex];
        public bool CanBack => PageIndex > 0;
        public bool IsLast => PageIndex == Pages.Count - 1;
        public string PageTitle => Page switch
        {
            SetupPage.Welcome => "Welcome to spark",
            SetupPage.Toolchains => "Compilers for Build and Publish",
            SetupPage.Model => "The model of the assistant",
            _ => "Summary",
        };
        /// <summary>"Step 2 of 4".</summary>
        public string StepText => $"Step {PageIndex + 1} of {Pages.Count}";

        public bool Next() { if (IsLast) return false; PageIndex++; return true; }
        public bool Back() { if (!CanBack) return false; PageIndex--; return true; }
        public void GoTo(SetupPage page) { int i = Pages.ToList().IndexOf(page); if (i >= 0) PageIndex = i; }

        // ---- the toolchain page ----

        /// <summary>Is there a toolchain to download on this machine (Windows), or only hints?</summary>
        public bool OffersToolchains => Compilers.OffersDownloads || Watcom.Installable;

        /// <summary>What was found and how to get the rest (every system; on Linux and macOS this is the whole page).</summary>
        public IReadOnlyList<string> ToolchainHints
        {
            get
            {
                var env = Environment;
                var hints = new List<string>();
                bool cpp = env.Detected.Any(d => d.CompilesCpp) || env.W64devkitThere;
                if (cpp) hints.Add("A C++ compiler is there: native builds and the natives of packages work without anything more.");
                else if (env.IsWindows && Compilers.OffersDownloads) hints.Add("No C++ compiler was found. Native builds and packages with natives in C++ need one: w64devkit (GCC) is the simplest choice.");
                else if (env.IsWindows) hints.Add("No C++ compiler was found. Install one (w64devkit, MSYS2, LLVM or Visual Studio) to build natively.");
                else if (env.IsMacOS) hints.Add("No C++ compiler was found. Install Apple's command line tools: xcode-select --install");
                else hints.Add("No C++ compiler was found. Install g++ or clang++ with the package manager, e.g. sudo apt install g++ (Debian, Ubuntu), sudo dnf install gcc-c++ (Fedora), sudo pacman -S gcc (Arch).");
                bool tcc = env.TinyccThere || env.Detected.Any(d => d.Kind == "tcc");
                if (!tcc && Compilers.Row("tcc") is { Installable: false } t)
                    hints.Add("TinyCC (tcc) compiles C very quickly, C only - the recommended compiler for Build (" + t.InstallHint + ").");
                if (!env.IsWindows && !env.Detected.Any(d => d.Kind == "watcom") && !env.WatcomThere)
                    hints.Add("For the DOS, Windows 3.x and BIOS targets spark downloads Open Watcom when such a target is built" + (env.IsLinux ? "." : " (Windows and Linux)."));
                if (!env.Detected.Any(d => d.Kind == "emscripten")) hints.Add("For the web target (WebAssembly): Emscripten (emsdk) on the PATH.");
                hints.Add("The compilers can be changed any time in Tools > Options > Compilers; a project can name its own (Project Properties > Native & Toolchain).");
                return hints;
            }
        }

        // ---- the summary and the plan ----

        /// <summary>What the setup will do, step by step (the first build of the standard packages always comes last).</summary>
        public IReadOnlyList<SetupPlanItem> Plan()
        {
            var plan = new List<SetupPlanItem>();
            plan.AddRange(Compilers.PlanItems());
            if (Watcom is { Selected: true, Installable: true }) plan.Add(new SetupPlanItem(SetupPlanKind.Toolchain, Watcom.Id, $"Install {Watcom.Name}", Watcom.DownloadBytes));
            if (Pages.Contains(SetupPage.Model) && SelectedModel is { Installed: false } m)
                plan.Add(new SetupPlanItem(SetupPlanKind.Model, m.Info.Id, $"Download the model {m.Info.Name}", m.Info.Size));
            plan.Add(new SetupPlanItem(SetupPlanKind.Packages, "standard", "Build the standard packages"));
            return plan;
        }

        public long TotalDownloadBytes => Plan().Sum(p => p.DownloadBytes);

        /// <summary>Start on the summary page: the plan to run (null on any other page - Next goes on there).</summary>
        public IReadOnlyList<SetupPlanItem>? Finish() => IsLast ? Plan() : null;

        /// <summary>Starts the run of a plan: its steps (<paramref name="steps"/>: the real ones, <see cref="SetupSteps.For"/>, or the tests'), started at once. Null when there is
        /// nothing to do - the caller says so instead of doing nothing.</summary>
        public static SetupRun? Launch(IReadOnlyList<SetupPlanItem>? plan, Func<IReadOnlyList<SetupPlanItem>, IReadOnlyList<ISetupStep>> steps)
        {
            if (plan == null || plan.Count == 0) return null;
            var list = steps(plan);
            if (list.Count == 0) return null;
            var run = new SetupRun(list);
            _ = run.Start();
            return run;
        }

        /// <summary>The lines of the summary page.</summary>
        public IReadOnlyList<string> SummaryLines
        {
            get
            {
                var lines = new List<string>();
                foreach (var r in Compilers.Downloads())
                    lines.Add($"Download {r.Name} ({r.SizeText}) and unpack it next to spark (Toolchain{System.IO.Path.DirectorySeparatorChar}).");
                if (Watcom is { Selected: true, Installable: true }) lines.Add($"Download {Watcom.Name} ({Watcom.SizeText}) and unpack it into {Watcom.Folder}.");
                lines.Add(Compilers.Summary(ToolchainPurpose.Build) + ".");
                lines.Add(Compilers.Summary(ToolchainPurpose.Publish) + ".");
                if (Pages.Contains(SetupPage.Model))
                {
                    if (SelectedModel is { } m)
                        lines.Add(m.Installed ? $"Assistant: {m.Info.Name} is there already."
                                              : $"Assistant: download {m.Info.Name} ({m.Info.SizeText}, {m.Info.License}) from Hugging Face and check its SHA-256.");
                    else lines.Add("Assistant: no model now - the assistant asks again when it is poked.");
                }
                lines.Add("Build the standard packages for this machine (only the first time takes a while).");
                long total = TotalDownloadBytes;
                lines.Add(total > 0 ? $"Downloads together: about {SetupFormat.Size(total)}." : "Nothing is downloaded.");
                return lines;
            }
        }
    }
}
