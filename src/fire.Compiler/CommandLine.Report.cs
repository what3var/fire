using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using fire.Native;
using fire.Projects;
using fire.Runtime;

namespace fire.Compiler
{
    // `forge build|publish --report json` (docs/tools/forge.md, "The report for tools"): the same builds as for people, told as JSON lines (ForgeReport) - the way spark builds
    // (docs/ROADMAP_C_PORT.md, L4). A build that reports can be cancelled: the line "cancel" on its input or SIGTERM/SIGINT end the tools it runs (their process trees), the
    // in-process parts stop at their next checkpoint, the temporary folders go (the `finally` of the builders) and the report ends with "cancelled". `--all` (spark's Build
    // Solution) goes this way also without `--report`, as text.
    public static partial class CommandLineRunner
    {
        private static ForgeReportWriter? s_report;
        private static readonly CancellationTokenSource s_cancel = new();
        private static readonly List<PosixSignalRegistration> s_signals = new();

        /// <summary>Does the command line ask for the report (`build`/`publish` with `--report json`)?</summary>
        public static bool WantsReport(IReadOnlyList<string> args)
        {
            if (args.Count == 0 || args[0].ToLowerInvariant() is not ("build" or "publish" or "bytecode")) return false;
            for (int i = 1; i < args.Count; i++)
            {
                if (args[i] == "--") return false;
                if (string.Equals(args[i], "--report=json", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(args[i], "--report", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count && string.Equals(args[i + 1], "json", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Program.cs, first thing: with `--report json` the report goes onto stdout (everything else that would be written there becomes a line of its log), the work
        /// without a build window (installing the standard packages) is reported, and cancelling is listened for. Returns where the messages of that work go (null: no report).</summary>
        public static Action<string>? BeginReport(IReadOnlyList<string> args)
        {
            if (!WantsReport(args)) return null;
            var report = new ForgeReportWriter(Console.Out);
            s_report = report;
            Console.SetOut(report.LogWriter());
            BackgroundWork.Changed += () => report.Background(BackgroundWork.Latest);
            ToolchainProvider.Log = report.Log;
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT })
            {
                try { s_signals.Add(PosixSignalRegistration.Create(signal, context => { context.Cancel = true; s_cancel.Cancel(); })); }
                catch (Exception ex) when (ex is PlatformNotSupportedException or IOException) { }
            }
            if (Console.IsInputRedirected)
                new Thread(() =>
                {
                    try
                    {
                        string? line;
                        while ((line = Console.In.ReadLine()) != null)
                            if (line.Trim() == ForgeReport.CancelLine) { s_cancel.Cancel(); return; }
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                }) { IsBackground = true, Name = "forge report input" }.Start();
            return report.Log;
        }

        /// <summary>The result of a build that reports: its exit code, and for the end of the report what it did and the first error.</summary>
        private readonly record struct Outcome(int Exit, string? Summary = null, string? Error = null);

        /// <summary>The text of the source <paramref name="path"/> from an overlay (`--overlay`), else null (the file is read).</summary>
        private static Func<string, string?> TextOf(CommandLineOptions options) =>
            path => options.Overlays.Count > 0 && options.Overlays.TryGetValue(Path.GetFullPath(path), out var file) ? File.ReadAllText(file) : null;

        /// <summary>The text of a source file given on the command line (an overlay goes first); null when it does not exist.</summary>
        private static string? ReadSource(CommandLineOptions options, string file) =>
            TextOf(options)(file) ?? (File.Exists(file) ? File.ReadAllText(file) : null);

        /// <summary>`build`/`publish` with `--report json` or `build --all`: runs the command and reports it (JSON or text) to its end.</summary>
        private static int RunReported(CommandLineOptions options, TextWriter stdout, TextWriter stderr)
        {
            ForgeReporter report = options.Report == "json" ? s_report ?? new ForgeReportWriter(stdout) : new ForgeTextReporter(stdout, stderr);
            var token = options.Report == "json" ? s_cancel.Token : CancellationToken.None;
            var monitor = new BuildMonitor(token, report.Progress, report.Log);
            var collector = new DiagnosticCollector { OnReport = report.Diagnostic };
            using var collecting = collector.Begin();
            using var watching = BuildMonitor.Use(monitor);
            report.Start(options.Command == CommandKind.Publish ? "publish" : options.Command == CommandKind.Bytecode ? "bytecode" : "build", string.Join(" ", options.Files.Select(Path.GetFileName)));
            Outcome outcome;
            try
            {
                outcome = options.Command switch
                {
                    CommandKind.Publish => ReportPublish(options, report, monitor),
                    CommandKind.Bytecode => ReportBytecode(options, report),
                    _ => ReportBuild(options, report, monitor),
                };
            }
            catch (OperationCanceledException)
            {
                report.Project = null;
                report.End(false, ForgeReport.ExitCancelled, cancelled: true, summary: "Build cancelled.");
                return ForgeReport.ExitCancelled;
            }
            if (token.IsCancellationRequested)
            {
                report.Project = null;
                report.End(false, ForgeReport.ExitCancelled, cancelled: true, summary: "Build cancelled.");
                return ForgeReport.ExitCancelled;
            }
            report.Project = null;
            report.End(outcome.Exit == ExitOk, outcome.Exit, summary: outcome.Summary, error: outcome.Error);
            return outcome.Exit;
        }

        private static Outcome Failure(ForgeReporter report, int exit, string message)
        {
            report.Log("error: " + message.TrimEnd());
            return new Outcome(exit, Error: message);
        }

        private static Outcome CompileFailure(ForgeReporter report, Exception ex)
        {
            var diagnostics = CompileDiagnostics.Of(ex);
            foreach (var d in diagnostics) report.Diagnostic(d);
            return new Outcome(ExitScriptError, Error: diagnostics.FirstOrDefault()?.ToLogLine() ?? ex.Message);
        }

        // ------------------------------------------------------------------------------------------------------------------
        // build
        // ------------------------------------------------------------------------------------------------------------------

        private static Outcome ReportBuild(CommandLineOptions options, ForgeReporter report, BuildMonitor monitor)
        {
            try
            {
                if (options.Files.Count == 1 && IsProjectFile(options.Files[0])) return ReportBuildProject(options, report, monitor);
                if (options.All) return Failure(report, ExitUsage, "--all builds a solution or a project file.");
                return ReportBuildFiles(options, report, monitor);
            }
            catch (NativeConfigException ex)
            {
                report.Diagnostic(new Diagnostic(0, ex.Message, DiagnosticSeverity.Error, ErrorCatalog.NativeConfiguration));
                return new Outcome(ExitUsage, Error: ex.Message);
            }
            catch (NativeNotSupportedException ex) { return CompileFailure(report, ex); }
            catch (ProjectException ex) { return Failure(report, ExitScriptError, ex.Message); }
            catch (Package.Manager.PackageException ex) { return Failure(report, ExitScriptError, ex.Message); }
            catch (Exception ex) when (IsCompileError(ex)) { return CompileFailure(report, ex); }
        }

        // ------------------------------------------------------------------------------------------------------------------
        // bytecode (spark's Run/Debug on the VM in C, docs/ROADMAP_C_PORT.md phase B step 9)
        // ------------------------------------------------------------------------------------------------------------------

        /// <summary>`bytecode --report json`: the program file of the files or of a project (with the overlays), and what `fire` needs to run it - the libraries of
        /// its packages (built now, outputs "library": their folders go into FIRE_PKG_PATH) and the VM of its widths (output "vm", CVm.Locate).</summary>
        private static Outcome ReportBytecode(CommandLineOptions options, ForgeReporter report)
        {
            try
            {
                BuildPlan? plan = null;
                if (options.Files.Any(IsProjectFile))
                {
                    if (options.Files.Count != 1) return Failure(report, ExitUsage, "bytecode takes source files or one project.");
                    if (!File.Exists(options.Files[0])) return Failure(report, ExitUsage, $"File not found: {options.Files[0]}");
                    var workspace = Workspace.Open(options.Files[0]);
                    plan = ProjectBuilder.PlanOrThrow(workspace, ProjectBuilder.SelectProject(workspace, options.ProjectName), TextOf(options));
                    if (plan.Type == OutputType.Library) return Failure(report, ExitUsage, $"'{plan.Name}' is a library: it is no program by itself (a program that imports it carries it).");
                }
                var sources = new List<string>();
                if (plan != null) sources.AddRange(plan.SourceTexts);
                else
                    foreach (var file in options.Files)
                    {
                        if (ReadSource(options, file) is not { } text) return Failure(report, ExitUsage, $"File not found: {file}");
                        sources.Add(text);
                    }
                BuildMonitor.Step(BuildPhase.CompileFire, $"Compiling {plan?.Name ?? string.Join(" ", options.Files.Select(Path.GetFileName))}");
                var linker = plan != null
                    ? new Linker { Defines = options.Defines, Plan = plan, SourcePaths = plan.SourcePaths.Cast<string?>().ToList(), PackageLibraries = true }
                    : new Linker { Defines = options.Defines, BasePath = BaseDirectory(options), SourcePaths = SourceFiles(options), PackageLibraries = true };
                var program = linker.CompileAndLink(sources, null, null, options.Mode, options.FloatWidth);
                byte[] bytes = fire.Bytecode.ProgramFile.Write(program);
                string full = Path.GetFullPath(options.OutputFile);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, bytes);
                report.Log($"{full} ({bytes.Length} bytes)");
                report.Output("program", full);
                foreach (string library in program.PackageLibraryFiles ?? new List<string>()) report.Output("library", Path.GetFullPath(library));
                try { report.Output("vm", CVm.Locate(program.IntWidth, program.FloatWidth, report.Log)); }
                catch (InvalidOperationException ex) { report.Log("warning: " + ex.Message); }
                return new Outcome(ExitOk, Summary: full);
            }
            catch (ProjectException ex) { return Failure(report, ExitUsage, ex.Message); }
            catch (Package.Manager.PackageException ex) { return Failure(report, ExitScriptError, ex.Message); }
            catch (Exception ex) when (IsCompileError(ex)) { return CompileFailure(report, ex); }
        }

        /// <summary>Source files: a native program (BuildSafe, as spark built it), else one that carries the VM.</summary>
        private static Outcome ReportBuildFiles(CommandLineOptions options, ForgeReporter report, BuildMonitor monitor)
        {
            var sources = new List<string>();
            foreach (var file in options.Files)
            {
                if (ReadSource(options, file) is not { } text) return Failure(report, ExitUsage, $"File not found: {file}");
                sources.Add(text);
            }
            var config = LoadConfig(options);
            var engineProblem = new StringWriter();
            string? engine = ChooseEngine(options.Engine, config.Engine, options.TargetName, config, engineProblem);
            if (engine == null) return Failure(report, ExitUsage, engineProblem.ToString().Trim());
            if (engine == "native")
            {
                var target = config.ResolveTarget(options.TargetName);
                var toolchain = ToolchainFor(options, config, target, null);
                string output = options.OutputGiven ? options.OutputFile : NativeBuilder.DefaultOutput(target);
                var result = NativeBuilder.BuildSafe(sources, config, target, toolchain, output, options.Mode, options.FloatWidth, options.KeepSources, BaseDirectory(options), options.Defines, null, monitor);
                return NativeOutcome(report, result, target, toolchain, null, null);
            }
            new Linker { Defines = options.Defines, BasePath = BaseDirectory(options), SourcePaths = SourceFiles(options) }.CompileAndLink(sources, null, options.OutputFile, options.Mode, options.FloatWidth);
            string full = Path.GetFullPath(options.OutputFile);
            report.Log($"{full} ({new FileInfo(full).Length} bytes)");
            report.Output("program", full);
            return new Outcome(ExitOk, Summary: full);
        }

        /// <summary>The toolchain of a native build: `--toolchain`, the project's, the configuration's or the machine's Build compiler - with `--debug-info` the one for the debugger.</summary>
        private static ToolchainDef ToolchainFor(CommandLineOptions options, NativeConfig config, TargetProfile target, BuildPlan? plan)
        {
            var toolchain = config.ResolveToolchain(target, options.ToolchainName ?? plan?.Settings.Toolchain, ToolchainPurpose.Build);
            return options.DebugInfo ? Debugging.NativeLaunch.DebugToolchain(toolchain) : toolchain;
        }

        /// <summary>The end of a native build: its log (what did not come as it ran), its errors and warnings, what it wrote (and the files with the build action Copy next to it).</summary>
        private static Outcome NativeOutcome(ForgeReporter report, NativeBuildResult result, TargetProfile target, ToolchainDef toolchain, BuildPlan? plan, string? project)
        {
            report.LogBlock(result.Log);
            if (result.Cancelled) throw new OperationCanceledException();
            foreach (var d in result.Diagnostics ?? Array.Empty<Diagnostic>()) report.Diagnostic(d);
            if (!result.Ok)
            {
                string error = (result.Diagnostics ?? Array.Empty<Diagnostic>()).FirstOrDefault(d => d.IsError)?.ToLogLine() ?? $"The native build for {target.Name} failed.";
                report.Log($"{(project != null ? project + ": " : "")}The native build for {target.Name} failed.");
                return new Outcome(ExitScriptError, Error: error);
            }
            bool files = toolchain.EffectiveKind == "files";
            if (plan != null) ReportCopyContent(report, plan, files ? result.Output : Path.GetDirectoryName(Path.GetFullPath(result.Output))!);
            report.Log($"{result.Output} (native, target {target.Name}, toolchain {toolchain.EffectiveKind})");
            report.Output(files ? "folder" : "program", result.Output);
            return new Outcome(ExitOk, Summary: result.Output);
        }

        /// <summary>The files with the build action Copy next to the program (docs/tools/projects.md, "Build actions").</summary>
        private static void ReportCopyContent(ForgeReporter report, BuildPlan plan, string folder)
        {
            var items = ProjectContent.ItemsOf(plan);
            if (items.Count == 0) return;
            var copied = ProjectContent.CopyTo(items, folder);
            foreach (var p in copied.Problems) report.Log($"{plan.Name}: warning: could not copy {p}");
            report.Log($"{plan.Name}: {copied.Copied.Count} file{(copied.Copied.Count == 1 ? "" : "s")} (build action Copy) copied next to the program{(copied.UpToDate > 0 ? $", {copied.UpToDate} up to date" : "")}.");
        }

        private static NativeConfig ConfigFor(CommandLineOptions options, BuildPlan plan) =>
            KeepAll(options.ConfigFile != null ? NativeConfig.Load(options.ConfigFile) : NativeConfig.FindFor(plan.Project.FilePath), options);

        private static Outcome ReportBuildProject(CommandLineOptions options, ForgeReporter report, BuildMonitor monitor)
        {
            string file = options.Files[0];
            if (!File.Exists(file)) return Failure(report, ExitUsage, $"File not found: {file}");
            var workspace = Workspace.Open(file);
            if (options.All) return ReportBuildAll(options, workspace, report, monitor);
            var project = ProjectBuilder.SelectProject(workspace, options.ProjectName);
            var plan = ProjectBuilder.PlanOrThrow(workspace, project, TextOf(options));
            if (plan.Type == OutputType.Library)
            {
                if (options.CheckOnly)
                {
                    var config = ConfigFor(options, plan);
                    var target = options.TargetName != null || plan.Settings.Target != null ? config.ResolveTarget(options.TargetName ?? plan.Settings.Target) : null;
                    ProjectBuilder.Check(plan, target);
                    report.Log($"{plan.Name}: compiled{(target != null ? $" for {target.Name}" : "")} (a library: its code goes into the programs that import it).");
                    return new Outcome(ExitOk);
                }
                string package = ProjectBuilder.PackLibrary(plan, options.OutputGiven ? options.OutputFile : null, monitor);
                report.Log($"{package} (package, import \"{plan.Project.Project.ImportName}\")");
                report.Output("package", package);
                return new Outcome(ExitOk, Summary: package);
            }
            return BuildProgram(options, plan, options.OutputGiven ? options.OutputFile : plan.Settings.Output, report, monitor, forSolution: false);
        }

        /// <summary>A program of a project: native (BuildSafe) or one that carries the VM - into <paramref name="output"/>, else &lt;project&gt;/bin/&lt;target&gt;-&lt;mode&gt;/.</summary>
        private static Outcome BuildProgram(CommandLineOptions options, BuildPlan plan, string? output, ForgeReporter report, BuildMonitor monitor, bool forSolution)
        {
            var config = ConfigFor(options, plan);
            var engineProblem = new StringWriter();
            string? engine = ChooseEngine(options.Engine, plan.Settings.Engine ?? config.Engine, options.TargetName ?? plan.Settings.Target, config, engineProblem);
            if (engine == null) return Failure(report, ExitUsage, engineProblem.ToString().Trim());
            string ModeName(TargetProfile? target) => options.Mode?.ToString().ToLowerInvariant() ?? EffectiveModeName(options, plan, target);
            if (engine == "native")
            {
                var target = config.ResolveTarget(options.TargetName ?? plan.Settings.Target);
                var toolchain = ToolchainFor(options, config, target, plan);
                bool files = toolchain.EffectiveKind == "files";
                if (options.Linkage == null && plan.Settings.Linkage != null) config.Linkage = plan.Settings.Linkage;   // (the command line, then the project, then fire.native.json)
                // Build Solution (as spark did it): the project's `output` is not a folder for the files
                if (forSolution && files) output = null;
                string to = output ?? (files
                    ? ProjectOutput.Folder(plan.Project.Directory, target.Name, ModeName(target))
                    : ProjectOutput.ProgramPath(plan.Project.Directory, plan.Name, target.Name, ModeName(target), target.ProgramExtension));
                Directory.CreateDirectory(files ? to : Path.GetDirectoryName(Path.GetFullPath(to))!);
                config.Stats = config.Stats || options.Stats;
                var result = NativeBuilder.BuildSafe(plan.SourceTexts, config, target, toolchain, to, options.Mode, options.FloatWidth, options.KeepSources, null, options.Defines, plan, monitor);
                var outcome = NativeOutcome(report, result, target, toolchain, plan, forSolution ? plan.Name : null);
                if (forSolution && outcome.Exit == ExitOk) report.Log($"{plan.Name}: built {result.Output} ({target.Name}).");
                return outcome;
            }
            string program = output ?? ProjectOutput.ProgramPath(plan.Project.Directory, plan.Name, ProjectOutput.VmTarget, ModeName(null), OperatingSystem.IsWindows());
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(program))!);
            BuildMonitor.Step(BuildPhase.CompileFire, $"Compiling {plan.Name}");
            new Linker { Defines = options.Defines, Plan = plan, SourcePaths = plan.SourcePaths.Cast<string?>().ToList() }.CompileAndLink(plan.SourceTexts, null, program, options.Mode, options.FloatWidth);
            string full = Path.GetFullPath(program);
            ReportCopyContent(report, plan, Path.GetDirectoryName(full)!);
            report.Log($"{full} ({new FileInfo(full).Length} bytes)");
            report.Output("program", full);
            return new Outcome(ExitOk, Summary: full);
        }

        /// <summary>`--all` (spark's Build Solution): every project in the order of the references (Workspace.BuildOrder) - a library compiled for the target (what a program that
        /// imports it gets), a program built; a project whose library failed is skipped. One line per project in the log, one <c>project</c> per project in the report.</summary>
        private static Outcome ReportBuildAll(CommandLineOptions options, Workspace workspace, ForgeReporter report, BuildMonitor monitor)
        {
            var order = workspace.BuildOrder();
            if (order.Count == 0) return Failure(report, ExitUsage, "The solution has no project to build.");
            var failed = new HashSet<LoadedProject>();
            int succeeded = 0, skipped = 0;
            string? firstError = null;
            void Fail(LoadedProject project, string message)
            {
                failed.Add(project);
                firstError ??= $"{project.Name}: {message}";
                report.Log($"{project.Name}: error: {message.TrimEnd()}");
                report.ProjectResult(project.Name, false, error: message);
            }
            for (int i = 0; i < order.Count; i++)
            {
                monitor.Token.ThrowIfCancellationRequested();
                var project = order[i];
                report.Project = project.Name;
                report.Log($"------ {i + 1}/{order.Count} {project.Name} ({(project.Project.Type == OutputType.Library ? "library" : "program")}) ------");
                if (workspace.ReferencedProjects(project).FirstOrDefault(failed.Contains) is { } broken)
                {
                    failed.Add(project);
                    skipped++;
                    report.Log($"{project.Name}: skipped - the library {broken.Name} it references did not build.");
                    report.ProjectResult(project.Name, false, skipped: true);
                    continue;
                }
                BuildPlan plan;
                try { plan = ProjectBuilder.PlanOrThrow(workspace, project, TextOf(options)); }
                catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException) { Fail(project, ex.Message); continue; }
                try
                {
                    if (plan.Type == OutputType.Library)
                    {
                        var config = ConfigFor(options, plan);
                        string? engine = ChooseEngine(options.Engine, plan.Settings.Engine ?? config.Engine, options.TargetName ?? plan.Settings.Target, config, TextWriter.Null);
                        var target = engine == "native" ? config.ResolveTarget(options.TargetName ?? plan.Settings.Target) : null;
                        try { ProjectBuilder.Check(plan, target); }
                        catch (Exception ex) when (ex is ProjectException or Package.Manager.PackageException || IsCompileError(ex))
                        {
                            var errors = CompileDiagnostics.Of(ex);
                            foreach (var d in errors) report.Diagnostic(d);
                            Fail(project, errors.FirstOrDefault()?.Message ?? "it does not compile");
                            continue;
                        }
                        succeeded++;
                        report.Log($"{project.Name}: compiled{(target != null ? $" for {target.Name}" : "")} (a library: its code goes into the programs that import it).");
                        report.ProjectResult(project.Name, true);
                        continue;
                    }
                    var outcome = BuildProgram(options, plan, plan.Settings.Output, report, monitor, forSolution: true);
                    if (outcome.Exit == ExitOk) { succeeded++; report.ProjectResult(project.Name, true); }
                    else Fail(project, outcome.Error ?? "the build failed");
                }
                catch (NativeConfigException ex) { Fail(project, ex.Message); }
                catch (NativeNotSupportedException ex) { foreach (var d in CompileDiagnostics.Of(ex)) report.Diagnostic(d); Fail(project, ex.Message); }
                catch (Package.Manager.PackageException ex) { Fail(project, ex.Message); }
                catch (Exception ex) when (IsCompileError(ex))
                {
                    var errors = CompileDiagnostics.Of(ex);
                    foreach (var d in errors) report.Diagnostic(d);
                    Fail(project, errors.FirstOrDefault()?.Message ?? ex.Message);
                }
            }
            report.Project = null;
            int failedBuilds = failed.Count - skipped;
            string summary = $"Projects: {succeeded} succeeded, {failedBuilds} failed, {skipped} skipped.";
            return new Outcome(failed.Count == 0 ? ExitOk : ExitScriptError, summary, firstError);
        }

        // ------------------------------------------------------------------------------------------------------------------
        // publish
        // ------------------------------------------------------------------------------------------------------------------

        private static Outcome ReportPublish(CommandLineOptions options, ForgeReporter report, BuildMonitor monitor)
        {
            if (options.Files.Count != 1 || !IsProjectFile(options.Files[0])) return Failure(report, ExitUsage, "publish takes one solution (.firesln) or project (.fireproj).");
            if (!File.Exists(options.Files[0])) return Failure(report, ExitUsage, $"File not found: {options.Files[0]}");
            Workspace workspace;
            IReadOnlyList<LoadedProject>? projects = null;
            try
            {
                workspace = Workspace.Open(options.Files[0]);
                if (options.ProjectName != null) projects = new[] { ProjectBuilder.SelectProject(workspace, options.ProjectName) };
                else if (workspace.Solution == null) projects = workspace.Projects.ToList();
            }
            catch (ProjectException ex) { return Failure(report, ExitUsage, ex.Message); }
            var result = ProjectPublisher.Publish(workspace, projects, new PublishOptions
            {
                Monitor = monitor,
                IncludeDisabled = projects != null,
                Toolchain = options.ToolchainName,
                TextOf = TextOf(options),
                Quick = options.Quick,
            });
            if (result.Cancelled) throw new OperationCanceledException();
            foreach (var d in result.Diagnostics) report.Diagnostic(d);
            foreach (var p in result.Projects)
            {
                report.Project = p.Project.Name;
                foreach (var folder in p.Folders) report.Output("publish", folder);
                report.ProjectResult(p.Project.Name, p.Ok, p.Skipped, p.UpToDate, p.Error);
            }
            report.Project = null;
            var first = result.Projects.FirstOrDefault(p => !p.Ok && p.Error != null);
            return new Outcome(result.Ok ? ExitOk : ExitScriptError, result.Summary, first != null ? $"{first.Project.Name}: {first.Error}" : result.Ok ? null : result.Summary);
        }
    }
}
