using System;
using System.Collections.Generic;
using System.Linq;
using fire.Tooling;
namespace fire.Editor.Setup
{
    /// <summary>A row of the compiler grid: a toolchain that is there (with its path and version) or one fire can download (TinyCC and w64devkit on Windows).</summary>
    public sealed class CompilerRow
    {
        /// <summary>The kind (<c>tcc</c>, <c>gcc</c>, <c>clang</c>, <c>msvc</c>): what toolchain.json keeps.</summary>
        public required string Kind { get; init; }
        public required string Name { get; init; }
        /// <summary>"fast, barely optimised - C only".</summary>
        public required string Traits { get; init; }
        public string? Path { get; init; }
        public string? Version { get; init; }
        public bool Installed { get; init; }
        /// <summary>The download of the setup that brings it (<c>tcc</c>, <c>w64devkit</c>; a <see cref="SetupPlanItem.Id"/>), when it can be downloaded on this machine.</summary>
        public string? DownloadId { get; init; }
        public long DownloadBytes { get; init; }
        /// <summary>How to get it when fire cannot download it ("sudo apt install tcc").</summary>
        public string? InstallHint { get; init; }
        public bool CompilesCpp => Kind != "tcc";
        public bool Installable => !Installed && DownloadId != null;
        public string SizeText => "about " + SetupFormat.Size(DownloadBytes);
        /// <summary>The line below the name: the path and version, "not installed - download (about 1 MB)", or how to get it.</summary>
        public string StateText => Installed ? (Version != null ? $"{Version} - {Path}" : Path ?? "installed")
            : Installable ? $"not installed - download ({SizeText})"
            : "not installed" + (InstallHint != null ? " - " + InstallHint : "");
    }

    /// <summary>
    /// The compilers of this machine for building and publishing (Tools > Options > Compilers, and the toolchain page of the setup wizard): a grid with one row per toolchain and
    /// two columns, Build (spark's Build, Run, Debug, Build Solution, `forge build`) and Publish (Publish, `forge publish`), one choice per column, and "Automatic" (the
    /// recommended one when it is there, else the best one there). Recommended: TinyCC for Build (fast, barely optimised), GCC for Publish (slow, proven, optimised; on Windows
    /// w64devkit). With <see cref="DownloadMissing"/> (on by default) the chosen ones that fire can download (Windows: TinyCC, w64devkit) are downloaded. A project's own
    /// toolchain goes before this choice (<see cref="MachineToolchains"/>).
    /// </summary>
    public sealed class CompilerChoiceModel
    {
        public CompilerChoiceModel(SetupEnvironment env, MachineToolchains? current = null)
        {
            Environment = env;
            Current = current ?? MachineToolchains.Default;
            Build = Current.Build;
            Publish = Current.Publish;
            var rows = new List<CompilerRow>();
            DetectedToolchain? Found(string kind) => env.Detected.FirstOrDefault(d => d.Kind == kind);

            var tcc = Found("tcc");
            bool tccThere = tcc != null || env.TinyccThere;
            rows.Add(new CompilerRow
            {
                Kind = "tcc", Name = "TinyCC", Traits = "fast, barely optimised - C only",
                Path = tcc?.Path ?? (env.TinyccThere ? TinyccSetup.Compiler : null), Version = tcc?.Version, Installed = tccThere,
                DownloadId = env.IsWindows && env.TinyccPinned && !tccThere ? "tcc" : null, DownloadBytes = TinyccSetup.DownloadBytes,
                InstallHint = env.IsWindows ? "put tcc.exe on the PATH" : env.IsMacOS ? "brew install tcc" : "e.g. sudo apt install tcc",
            });
            var gcc = Found("gcc");
            bool gccThere = gcc != null || env.W64devkitThere;
            rows.Add(new CompilerRow
            {
                Kind = "gcc", Name = env.IsWindows ? "GCC (w64devkit)" : "GCC", Traits = "slow, proven, optimised - C and C++",
                Path = gcc?.Path ?? (env.W64devkitThere ? System.IO.Path.Combine(ToolchainSetup.W64devkitDirectory, "bin", "g++.exe") : null), Version = gcc?.Version, Installed = gccThere,
                DownloadId = env.IsWindows && env.IsX64 && !gccThere ? "w64devkit" : null, DownloadBytes = ToolchainSetup.DownloadBytes,
                InstallHint = env.IsWindows ? "w64devkit, MSYS2 or MinGW" : env.IsMacOS ? "xcode-select --install (clang)" : "e.g. sudo apt install g++ (dnf install gcc-c++, pacman -S gcc)",
            });
            if (Found("clang") is { } clang)
                rows.Add(new CompilerRow { Kind = "clang", Name = "Clang", Traits = "optimised - C and C++", Path = clang.Path, Version = clang.Version, Installed = true });
            if (Found("msvc") is { } msvc)
                rows.Add(new CompilerRow { Kind = "msvc", Name = "Visual C++ (MSVC)", Traits = "optimised - C and C++, Windows only", Path = msvc.Path, Version = msvc.Version, Installed = true });
            Rows = rows;
            // (a choice of a compiler that is not a row - e.g. clang, uninstalled since: shown as automatic until it is there again)
            if (Build != MachineToolchains.Automatic && Row(Build) == null) Build = MachineToolchains.Automatic;
            if (Publish != MachineToolchains.Automatic && Row(Publish) == null) Publish = MachineToolchains.Automatic;
        }

        public SetupEnvironment Environment { get; }
        /// <summary>What the machine had chosen before (its toolchain stays in the file).</summary>
        public MachineToolchains Current { get; }
        public IReadOnlyList<CompilerRow> Rows { get; }

        /// <summary>The choice of the Build column: a <see cref="CompilerRow.Kind"/> or <see cref="MachineToolchains.Automatic"/>.</summary>
        public string Build { get; set; }
        /// <summary>The choice of the Publish column.</summary>
        public string Publish { get; set; }
        /// <summary>"Download missing toolchains (TCC/GCC)": the chosen ones that are not there are downloaded (on by default).</summary>
        public bool DownloadMissing { get; set; } = true;

        public string Choice(ToolchainPurpose purpose) => purpose == ToolchainPurpose.Build ? Build : Publish;

        public void Choose(ToolchainPurpose purpose, string choice)
        {
            if (choice != MachineToolchains.Automatic && Row(choice) is not { } row) return;
            if (purpose == ToolchainPurpose.Build) Build = choice; else Publish = choice;
        }

        public CompilerRow? Row(string kind) => Rows.FirstOrDefault(r => r.Kind == kind);

        /// <summary>Does this machine offer something to download at all (the checkbox is shown)?</summary>
        public bool OffersDownloads => Rows.Any(r => r.Installable);

        /// <summary>Can a row be chosen: it is there, or it can be downloaded.</summary>
        public static bool CanChoose(CompilerRow row) => row.Installed || row.Installable;

        /// <summary>The recommended one of a column: TinyCC for Build; GCC for Publish (on Windows without GCC to download but with MSVC: MSVC).</summary>
        public string Recommended(ToolchainPurpose purpose)
        {
            if (purpose == ToolchainPurpose.Build) return "tcc";
            if (Row("gcc") is { } g && CanChoose(g)) return "gcc";
            return Environment.IsWindows && Row("msvc") is { Installed: true } ? "msvc" : "gcc";
        }

        /// <summary>Is the toolchain there after Apply (installed, or downloaded because of the checkbox)?</summary>
        private bool Available(string kind) => Row(kind) is { } r && (r.Installed || (DownloadMissing && r.Installable));

        /// <summary>The compiler a column comes to, and why ("automatic, recommended"); Kind null: none.</summary>
        public (string? Kind, string Source) Effective(ToolchainPurpose purpose)
        {
            string choice = Choice(purpose);
            string recommended = Recommended(purpose);
            if (choice == MachineToolchains.Automatic && Available(recommended)) return (recommended, "automatic, recommended");
            return MachineToolchains.Pick(choice, purpose, Available, Environment.IsWindows);
        }

        /// <summary>The downloads Apply (or Start) does: the effective compilers of the two columns that are not there yet - only with <see cref="DownloadMissing"/>.</summary>
        public IReadOnlyList<CompilerRow> Downloads()
        {
            if (!DownloadMissing) return Array.Empty<CompilerRow>();
            var kinds = new[] { Effective(ToolchainPurpose.Build).Kind, Effective(ToolchainPurpose.Publish).Kind };
            return Rows.Where(r => r.Installable && kinds.Contains(r.Kind)).ToList();
        }

        public IReadOnlyList<SetupPlanItem> PlanItems() =>
            Downloads().Select(r => new SetupPlanItem(SetupPlanKind.Toolchain, r.DownloadId!, $"Install {r.Name}", r.DownloadBytes)).ToList();

        /// <summary>What is saved to toolchain.json (the machine's toolchain in the file stays).</summary>
        public MachineToolchains Result() => Current with { Build = Build, Publish = Publish };

        public void Save(string? file = null) => Result().Save(file);

        /// <summary>"Build: TinyCC (automatic, recommended)" / "Publish: GCC (w64devkit) - downloaded (about 80 MB)" / "Publish: none - install a compiler".</summary>
        public string Summary(ToolchainPurpose purpose)
        {
            string label = purpose == ToolchainPurpose.Build ? "Build" : "Publish";
            var (kind, source) = Effective(purpose);
            if (kind == null || Row(kind) is not { } row) return $"{label}: no compiler - install one (see the rows)";
            string extra = row.Installed ? "" : ", downloaded first";
            return $"{label}: {row.Name} ({source}{extra})";
        }
    }
}
