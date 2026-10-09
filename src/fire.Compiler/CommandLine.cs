using fire.Bytecode;
using fire.Runtime;
using fire.Values;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace fire.Compiler
{
    /// <summary>The kind of invocation on the command line.</summary>
    public enum CommandKind { Run, Build, Native, Help }

    /// <summary>Result of evaluating the command line (see <see cref="CommandLineParser.Parse"/>).</summary>
    public sealed class CommandLineOptions
    {
        public CommandKind Command { get; init; }
        /// <summary>Source files in the given order (paths, as entered).</summary>
        public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
        /// <summary>`-m`: execution mode; null = the one in the script (`#debug`/`#performance`) or release.</summary>
        public VmExecutionMode? Mode { get; init; }
        /// <summary>`-f`: precision of `float` (32 or 64); null = the one in the script (`#floatwidth`) or 64.</summary>
        public int? FloatWidth { get; init; }

        /// <summary>`-D name` (repeatable): extra symbols for `#if`.</summary>
        public IReadOnlyList<string> Defines { get; init; } = Array.Empty<string>();
        /// <summary>`-t`: target profile of `native` (see TargetProfile); null = the machine the compiler runs on.</summary>
        public TargetProfile? Target { get; init; }
        /// <summary>`-o`: output file of `build` (or the C++ file of `native`).</summary>
        public string OutputFile { get; init; } = CommandLineParser.DefaultOutputFile;
        /// <summary>`-o` was given (otherwise the default depends on what is built).</summary>
        public bool OutputGiven { get; init; }
        /// <summary>`-t`: the name of the target, also for targets that only the configuration (fire.native.json) defines; resolved when the command runs.</summary>
        public string? TargetName { get; init; }
        /// <summary>`--engine`: `vm` (a self-contained file with the VM) or `native` (translate to C++ and build); null = the configuration's, else `vm`.</summary>
        public string? Engine { get; init; }
        /// <summary>`--toolchain`: the toolchain of a native build; null = the configuration's.</summary>
        public string? ToolchainName { get; init; }
        /// <summary>`--config`: the native build configuration; null = the nearest fire.native.json next to the first file.</summary>
        public string? ConfigFile { get; init; }
        /// <summary>`--keep`: keep the generated C++ next to the program.</summary>
        public bool KeepSources { get; init; }
        /// <summary>`-p`: the project of a solution to run or build (default: the startup project).</summary>
        public string? ProjectName { get; init; }
        /// <summary>Set if the command line is invalid (message for the user).</summary>
        public string? Error { get; init; }
    }

    /// <summary>
    /// Command line of the compiler:
    ///
    ///   fire.Compiler run   file1 [file2 ...] [-m DEBUG|RELEASE|PERFORMANCE]
    ///   fire.Compiler build file1 [file2 ...] [-o target.exe] [-m DEBUG|RELEASE|PERFORMANCE]
    ///
    /// `run` compiles and executes the files (joined into ONE program in this order) immediately, `build`
    /// produces a self-contained file from them (default `out.exe`). File names without spaces need no
    /// quotation marks; with spaces the shell puts them in quotation marks as usual - the quotation marks
    /// themselves never arrive in the argument. If they do (e.g. through a shell that passes them through), enclosing
    /// quotation marks are removed. Options may stand at any position; `-m` is case-insensitive
    /// (also `-m=DEBUG`, `--mode DEBUG`), likewise `-o`/`--out`.
    /// </summary>
    public static class CommandLineParser
    {
        public const string DefaultOutputFile = "out.exe";
        public const string DefaultNativeOutputFile = "out.cpp";

        public static string Usage =>
            """
            Usage:
              fire.Compiler run   <file>... [-m DEBUG|RELEASE|PERFORMANCE] [-f 32|64]
              fire.Compiler build <file>... [-o <target.exe>] [-m DEBUG|RELEASE|PERFORMANCE] [-f 32|64] [--engine vm|native] [-t <target>] [--toolchain <name>] [--config <file>] [--keep]
              fire.Compiler native <file>... [-o <target.cpp>] [-t <target>] [-f 32|64] [--config <file>]
              fire.Compiler ui    <file.fxml> [-o <file>]
              fire.Compiler run|build <project.fireproj | solution.firesln> [-p <project>] [...]

            run     compiles the files into one program and runs it.
            build   turns them into a program: a self-contained executable that carries the VM (default: out.exe), or - with --engine native, or "engine": "native" in
                    fire.native.json - C++ built with the toolchain of the configuration for the target (-o is the program; for a toolchain that only writes files, a folder).
            -m      Execution mode (default: whatever the script sets with #debug/#performance, otherwise RELEASE).
            native  translates the files into C++ (written next to fire_rt.hpp, the runtime it includes; default: out.cpp)
                    - an experimental ahead-of-time backend, see docs/NATIVE_BACKEND.md.
            -t      Target of a native build: windows, linux, macos, esp32, freertos or one that fire.native.json defines (default: this machine). The target decides the
                    default precision of float, which libraries can be imported, which platform package of the runtime is used and how the program starts.
            --config  The native build configuration (default: the nearest fire.native.json next to the first file, else built-in defaults): engine, target,
                    toolchain (which C++ compiler), own targets. The editor edits the same file.
            -f      Precision of float in bits: 32 or 64 (default: whatever the script sets with #floatwidth, otherwise 64).
            -D name Defines the symbol "name" for #if (repeatable, also --define name or -Dname).
            -o      Name of the file produced by build or native.
            ui      writes the script generated from the markup of a user interface (docs/UI_MARKUP.md) - not needed to build: `#include "x.fxml"` does it on the fly.

            A project (`.fireproj`) or a solution (`.firesln`, docs/PROJECTS.md) instead of the files: run runs the project (of a solution: the startup project, or -p <name>), build builds it -
            a program like above (the settings of the project decide: engine, target, mode, output), a library as a package (`name-version.fpk`, in the folder of -o, the project's `output` or `bin`).
            The settings of the project go before the tags in the source; the options of the command line go before both.

            File names without spaces do not need quotation marks.
            """;

        public static CommandLineOptions Parse(IReadOnlyList<string> args)
        {
            if (args.Count == 0 || IsHelp(args[0]))
                return new CommandLineOptions { Command = CommandKind.Help };

            CommandKind command;
            switch (args[0].ToLowerInvariant())
            {
                case "run": command = CommandKind.Run; break;
                case "build": command = CommandKind.Build; break;
                case "native": command = CommandKind.Native; break;
                default:
                    return Fail(CommandKind.Help, $"Unknown command '{args[0]}' (expected: run, build or native).");
            }

            var files = new List<string>();
            VmExecutionMode? mode = null;
            int? floatWidth = null;
            TargetProfile? target = null;
            string? targetName = null, engine = null, toolchainName = null, configFile = null, projectName = null;
            bool keep = false;
            string? output = null;
            var defines = new List<string>();

            for (int i = 1; i < args.Count; i++)
            {
                string arg = args[i];

                if (TryOption(arg, "-m", "--mode", out var modeInline))
                {
                    string? value = modeInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (value == null) return Fail(command, "-m must be followed by the mode (DEBUG, RELEASE or PERFORMANCE).");
                    if (!TryParseMode(Unquote(value), out var parsed))
                        return Fail(command, $"Unknown mode '{value}' (allowed: DEBUG, RELEASE, PERFORMANCE).");
                    mode = parsed;
                }
                else if (TryOption(arg, "-t", "--target", out var targetInline))
                {
                    if (command is not (CommandKind.Native or CommandKind.Build)) return Fail(command, "-t is only available with build and native.");
                    string? value = targetInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (value == null) return Fail(command, "-t must be followed by the target name.");
                    targetName = Unquote(value);
                    // a name the configuration defines is checked when the command runs
                    if (TargetProfile.TryGet(targetName, out var found)) target = found;
                }
                else if (TryOption(arg, "--engine", "--engine", out var engineInline))
                {
                    if (command != CommandKind.Build) return Fail(command, "--engine is only available with build.");
                    string? value = engineInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (value == null || Unquote(value).ToLowerInvariant() is not ("vm" or "native")) return Fail(command, "--engine must be followed by vm or native.");
                    engine = Unquote(value).ToLowerInvariant();
                }
                else if (TryOption(arg, "--toolchain", "--toolchain", out var toolchainInline))
                {
                    if (command != CommandKind.Build) return Fail(command, "--toolchain is only available with build.");
                    string? value = toolchainInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value)) return Fail(command, "--toolchain must be followed by the toolchain name.");
                    toolchainName = Unquote(value);
                }
                else if (TryOption(arg, "--config", "--config", out var configInline))
                {
                    if (command is not (CommandKind.Native or CommandKind.Build)) return Fail(command, "--config is only available with build and native.");
                    string? value = configInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value)) return Fail(command, "--config must be followed by the file name.");
                    configFile = Unquote(value);
                }
                else if (TryOption(arg, "-p", "--project", out var projectInline))
                {
                    string? value = projectInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value)) return Fail(command, "-p must be followed by the name of a project.");
                    projectName = Unquote(value);
                }
                else if (string.Equals(arg, "--keep", StringComparison.OrdinalIgnoreCase))
                {
                    if (command != CommandKind.Build) return Fail(command, "--keep is only available with build.");
                    keep = true;
                }
                else if (TryOption(arg, "-f", "--float", out var floatInline))
                {
                    string? value = floatInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (value == null) return Fail(command, "-f must be followed by the float precision (32 or 64).");
                    if (Unquote(value) is not ("32" or "64"))
                        return Fail(command, $"Unknown float precision '{value}' (allowed: 32, 64).");
                    floatWidth = int.Parse(Unquote(value));
                }
                else if (TryOption(arg, "-D", "--define", out var defineInline) || (arg.Length > 2 && arg.StartsWith("-D", StringComparison.Ordinal) && (defineInline = arg.Substring(2)) != null))
                {
                    string? value = defineInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (value == null || !System.Text.RegularExpressions.Regex.IsMatch(Unquote(value), "^[A-Za-z_][A-Za-z0-9_]*$"))
                        return Fail(command, "-D must be followed by a symbol name (letters, digits, underscore).");
                    defines.Add(Unquote(value));
                }
                else if (TryOption(arg, "-o", "--out", out var outInline))
                {
                    if (command is not (CommandKind.Build or CommandKind.Native)) return Fail(command, "-o is only available with build and native.");
                    string? value = outInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value)) return Fail(command, "-o must be followed by the file name.");
                    output = Unquote(value);
                }
                else if (arg.StartsWith("-") && arg.Length > 1)
                {
                    return Fail(command, $"Unknown option '{arg}'.");
                }
                else
                {
                    var file = Unquote(arg);
                    if (file.Length > 0) files.Add(file);
                }
            }

            if (files.Count == 0)
                return Fail(command, "No source file was specified.");

            return new CommandLineOptions
            {
                Command = command,
                Files = files,
                Mode = mode,
                FloatWidth = floatWidth,
                Defines = defines,
                Target = target,
                TargetName = targetName,
                Engine = engine,
                ToolchainName = toolchainName,
                ConfigFile = configFile,
                KeepSources = keep,
                ProjectName = projectName,
                OutputGiven = output != null,
                OutputFile = output ?? (command == CommandKind.Native ? DefaultNativeOutputFile : DefaultOutputFile),
            };
        }

        private static bool IsHelp(string arg) =>
            arg is "-h" or "--help" or "/?" or "help" or "-?";

        private static CommandLineOptions Fail(CommandKind command, string message) =>
            new() { Command = command, Error = message };

        /// <summary>Recognises `-m`/`--mode` (value in the next argument) and `-m=X`/`--mode=X` (value inline).</summary>
        private static bool TryOption(string arg, string shortName, string longName, out string? inlineValue)
        {
            inlineValue = null;
            foreach (var name in new[] { shortName, longName })
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
                if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    inlineValue = arg.Substring(name.Length + 1);
                    return true;
                }
            }
            return false;
        }

        private static bool TryParseMode(string text, out VmExecutionMode mode)
        {
            switch (text.Trim().ToUpperInvariant())
            {
                case "DEBUG": mode = VmExecutionMode.Debug; return true;
                case "RELEASE": mode = VmExecutionMode.Release; return true;
                case "PERFORMANCE": mode = VmExecutionMode.Performance; return true;
                default: mode = default; return false;
            }
        }

        /// <summary>Removes enclosing quotation marks (`"a b.script"` -> `a b.script`).</summary>
        private static string Unquote(string text)
        {
            text = text.Trim();
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
                return text.Substring(1, text.Length - 2);
            return text;
        }
    }

    /// <summary>Executes the commands of the command line (see <see cref="CommandLineParser"/>).</summary>
    public static class CommandLineRunner
    {
        public const int ExitOk = 0;
        /// <summary>Compile or runtime error of the script.</summary>
        public const int ExitScriptError = 1;
        /// <summary>Invalid command line or missing file.</summary>
        public const int ExitUsage = 2;

        public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
        {
            var options = CommandLineParser.Parse(args);

            if (options.Error != null)
            {
                stderr.WriteLine(options.Error);
                stderr.WriteLine();
                stderr.WriteLine(CommandLineParser.Usage);
                return ExitUsage;
            }
            if (options.Command == CommandKind.Help)
            {
                stdout.WriteLine(CommandLineParser.Usage);
                return ExitOk;
            }

            if (options.Files.Count == 1 && IsProjectFile(options.Files[0])) return RunProject(options, stdout, stderr);

            var sources = new List<string>();
            foreach (var file in options.Files)
            {
                if (!File.Exists(file))
                {
                    stderr.WriteLine($"File not found: {file}");
                    return ExitUsage;
                }
                sources.Add(File.ReadAllText(file));
            }

            return options.Command switch
            {
                CommandKind.Build => Build(options, sources, stdout, stderr),
                CommandKind.Native => Native(options, sources, stdout, stderr),
                _ => Execute(options, sources, stderr),
            };
        }

        private static int Build(CommandLineOptions options, List<string> sources, TextWriter stdout, TextWriter stderr)
        {
            fire.Native.NativeConfig config;
            try { config = LoadConfig(options); }
            catch (fire.Native.NativeConfigException ex) { stderr.WriteLine(ex.Message); return ExitUsage; }
            string engine = options.Engine ?? config.Engine?.ToLowerInvariant() ?? "vm";
            if (engine == "native") return NativeBuild(options, config, sources, stdout, stderr);
            try
            {
                new Linker { Defines = options.Defines }.CompileAndLink(sources, null, options.OutputFile, options.Mode, options.FloatWidth);
            }
            catch (Exception ex) when (IsCompileError(ex))
            {
                stderr.WriteLine(CompileErrors.Describe(ex));
                return ExitScriptError;
            }
            stdout.WriteLine($"{Path.GetFullPath(options.OutputFile)} ({new FileInfo(options.OutputFile).Length} bytes)");
            return ExitOk;
        }

        private static fire.Native.NativeConfig LoadConfig(CommandLineOptions options)
        {
            if (options.ConfigFile != null)
            {
                if (!File.Exists(options.ConfigFile)) throw new fire.Native.NativeConfigException($"Configuration not found: {options.ConfigFile}");
                return fire.Native.NativeConfig.Load(options.ConfigFile);
            }
            return fire.Native.NativeConfig.FindFor(options.Files[0]);
        }

        /// <summary>`build` with the native engine: translate for the target and let the toolchain build the program (or write the files of a project).</summary>
        private static int NativeBuild(CommandLineOptions options, fire.Native.NativeConfig config, List<string> sources, TextWriter stdout, TextWriter stderr)
        {
            try
            {
                var target = config.ResolveTarget(options.TargetName);
                var toolchain = config.ResolveToolchain(target, options.ToolchainName);
                string output = options.OutputGiven ? options.OutputFile : NativeBuilder.DefaultOutput(target);
                var result = NativeBuilder.Build(sources, config, target, toolchain, output, options.Mode, options.FloatWidth, options.KeepSources, defines: options.Defines);
                stdout.Write(result.Log);
                if (!result.Ok) { stderr.WriteLine($"The native build for {target.Name} failed."); return ExitScriptError; }
                stdout.WriteLine($"{result.Output} (native, target {target.Name}, toolchain {toolchain.EffectiveKind})");
                return ExitOk;
            }
            catch (fire.Native.NativeConfigException ex) { stderr.WriteLine(ex.Message); return ExitUsage; }
            catch (Exception ex) when (IsCompileError(ex)) { stderr.WriteLine(CompileErrors.Describe(ex)); return ExitScriptError; }
            catch (fire.Native.NativeNotSupportedException ex) { stderr.WriteLine("Not supported by the native backend yet: " + ex.Message); return ExitScriptError; }
        }

        private static int Native(CommandLineOptions options, List<string> sources, TextWriter stdout, TextWriter stderr)
        {
            try
            {
                var config = LoadConfig(options);
                var target = config.ResolveTarget(options.TargetName);
                string cpp = NativeBuilder.Generate(sources, target, options.Mode, options.FloatWidth, defines: options.Defines);
                string full = Path.GetFullPath(options.OutputFile);
                NativeBuilder.WriteFiles(Path.GetDirectoryName(full)!, cpp, target, Path.GetFileName(full), config.Path == null ? null : Path.GetDirectoryName(config.Path));
                stdout.WriteLine(target.IsEmbedded || target.Native.Entry.Kind == fire.Runtime.EntryKind.Function
                    ? $"{full} ({cpp.Length} characters) for {target.Name} - add it, fire_rt.hpp and platform/{target.Native.Platform}/ to your project (the entry point is {target.Native.Entry.Name})"
                    : $"{full} ({cpp.Length} characters) - compile with: c++ -std=c++17 -O2 {string.Join(" ", target.Native.CompileArgs)} \"{full}\" -o program");
            }
            catch (fire.Native.NativeConfigException ex)
            {
                stderr.WriteLine(ex.Message);
                return ExitUsage;
            }
            catch (Exception ex) when (IsCompileError(ex))
            {
                stderr.WriteLine(CompileErrors.Describe(ex));
                return ExitScriptError;
            }
            catch (fire.Native.NativeNotSupportedException ex)
            {
                stderr.WriteLine("Not supported by the native backend yet: " + ex.Message);
                return ExitScriptError;
            }
            return ExitOk;
        }

        private static bool IsProjectFile(string path) =>
            string.Equals(Path.GetExtension(path), fire.Projects.FireProject.Extension, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(path), fire.Projects.FireSolution.Extension, StringComparison.OrdinalIgnoreCase);

        /// <summary>`run` and `build` of a project or a solution (docs/PROJECTS.md).</summary>
        private static int RunProject(CommandLineOptions options, TextWriter stdout, TextWriter stderr)
        {
            string file = options.Files[0];
            if (!File.Exists(file)) { stderr.WriteLine($"File not found: {file}"); return ExitUsage; }
            fire.Projects.BuildPlan plan;
            try
            {
                var workspace = fire.Projects.Workspace.Open(file);
                var project = ProjectBuilder.SelectProject(workspace, options.ProjectName);
                plan = ProjectBuilder.PlanOrThrow(workspace, project);
            }
            catch (fire.Projects.ProjectException ex) { stderr.WriteLine(ex.Message); return ExitUsage; }

            if (options.Command == CommandKind.Native) { stderr.WriteLine("`native` takes source files; build a project with `build` (set \"engine\": \"native\" in the project or use --engine native)."); return ExitUsage; }
            if (options.Command == CommandKind.Run)
            {
                if (plan.Type == fire.Projects.OutputType.Library) { stderr.WriteLine($"'{plan.Name}' is a library: it has no entry point to run."); return ExitScriptError; }
                return ExecutePlan(options, plan, stderr);
            }
            return BuildPlanned(options, plan, stdout, stderr);
        }

        private static int ExecutePlan(CommandLineOptions options, fire.Projects.BuildPlan plan, TextWriter stderr)
        {
            RuntimeSession session;
            try
            {
                session = RuntimeSession.Build(plan.SourceTexts, options.Mode, args =>
                {
                    if (args.Length > 0) Console.WriteLine(args[0].ToString());
                    return Value.MakeUndefined();
                }, floatWidth: options.FloatWidth, defines: options.Defines, plan: plan);
            }
            catch (Exception ex) when (IsCompileError(ex))
            {
                stderr.WriteLine(CompileErrors.Describe(ex));
                return ExitScriptError;
            }
            session.Run();
            var vm = session.VirtualMachine!;
            if (vm.UnhandledException != null)
            {
                stderr.WriteLine(new UncaughtScriptException(vm.UnhandledException).Message);
                return ExitScriptError;
            }
            var exit = VM.ExitValue;
            return exit.Kind == ValueKind.Int ? (int)exit.AsInt() : ExitOk;
        }

        private static int BuildPlanned(CommandLineOptions options, fire.Projects.BuildPlan plan, TextWriter stdout, TextWriter stderr)
        {
            try
            {
                if (plan.Type == fire.Projects.OutputType.Library)
                {
                    string package = ProjectBuilder.PackLibrary(plan, options.OutputGiven ? options.OutputFile : null);
                    stdout.WriteLine($"{package} (package, import \"{plan.Project.Project.ImportName}\")");
                    return ExitOk;
                }
                fire.Native.NativeConfig config = options.ConfigFile != null ? fire.Native.NativeConfig.Load(options.ConfigFile) : fire.Native.NativeConfig.FindFor(plan.Project.FilePath);
                string engine = options.Engine ?? plan.Settings.Engine ?? config.Engine?.ToLowerInvariant() ?? "vm";
                string? output = options.OutputGiven ? options.OutputFile : plan.Settings.Output;
                if (engine == "native")
                {
                    var target = config.ResolveTarget(options.TargetName ?? plan.Settings.Target);
                    var toolchain = config.ResolveToolchain(target, options.ToolchainName ?? plan.Settings.Toolchain);
                    string out2 = output ?? NativeBuilder.DefaultOutput(target);
                    var result = NativeBuilder.Build(plan.SourceTexts, config, target, toolchain, out2, options.Mode, options.FloatWidth, options.KeepSources, defines: options.Defines, plan: plan);
                    stdout.Write(result.Log);
                    if (!result.Ok) { stderr.WriteLine($"The native build for {target.Name} failed."); return ExitScriptError; }
                    stdout.WriteLine($"{result.Output} (native, target {target.Name}, toolchain {toolchain.EffectiveKind})");
                    return ExitOk;
                }
                output ??= Path.Combine(plan.Project.Directory, "bin", plan.Name + (OperatingSystem.IsWindows() ? ".exe" : ""));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                new Linker { Defines = options.Defines, Plan = plan, SourcePaths = plan.SourcePaths.Cast<string?>().ToList() }.CompileAndLink(plan.SourceTexts, null, output, options.Mode, options.FloatWidth);
                stdout.WriteLine($"{Path.GetFullPath(output)} ({new FileInfo(output).Length} bytes)");
                return ExitOk;
            }
            catch (fire.Native.NativeConfigException ex) { stderr.WriteLine(ex.Message); return ExitUsage; }
            catch (fire.Native.NativeNotSupportedException ex) { stderr.WriteLine("Not supported by the native backend yet: " + ex.Message); return ExitScriptError; }
            catch (fire.Projects.ProjectException ex) { stderr.WriteLine(ex.Message); return ExitScriptError; }
            catch (Package.Manager.PackageException ex) { stderr.WriteLine(ex.Message); return ExitScriptError; }
            catch (Exception ex) when (IsCompileError(ex)) { stderr.WriteLine(CompileErrors.Describe(ex)); return ExitScriptError; }
        }

        private static int Execute(CommandLineOptions options, List<string> sources, TextWriter stderr)
        {
            RuntimeSession session;
            try
            {
                // print() goes directly to the console, IO.Stdio likewise (default of RuntimeSession.Build).
                session = RuntimeSession.Build(sources, options.Mode, args =>
                {
                    if (args.Length > 0) Console.WriteLine(args[0].ToString());
                    return Value.MakeUndefined();
                }, floatWidth: options.FloatWidth, defines: options.Defines);
            }
            catch (Exception ex) when (IsCompileError(ex))
            {
                stderr.WriteLine(CompileErrors.Describe(ex));
                return ExitScriptError;
            }

            session.Run();

            var vm = session.VirtualMachine!;
            if (vm.UnhandledException != null)
            {
                stderr.WriteLine(new UncaughtScriptException(vm.UnhandledException).Message);
                return ExitScriptError;
            }

            // `terminate(value)`: an integer value is the exit code of the process.
            var exit = VM.ExitValue;
            return exit.Kind == ValueKind.Int ? (int)exit.AsInt() : ExitOk;
        }

        /// <summary>Errors that the script itself causes (parser, resolver, compiler, preprocessor) - everything
        /// else is an error in the tool and is to stay visible with its stack trace.</summary>
        public static bool IsCompileErrorForEditor(Exception ex) => IsCompileError(ex);

        internal static bool IsCompileError(Exception ex) =>
            ex is ParseException or ResolverException or CompilerException
                or NotSupportedException or PreprocessorException or LibraryEntryPointException or fire.Projects.ProjectException;
    }
}
