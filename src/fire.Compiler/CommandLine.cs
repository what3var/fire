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
    public enum CommandKind { Run, Build, Native, Test, Help }

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
        /// <summary>`--types lax|strict`: how a type conflict is reported (docs/TYPES.md); null = the project's setting, else lax.</summary>
        public TypeCheckMode? TypeChecking { get; init; }

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
        /// <summary>`forge test --filter`: the tests that run.</summary>
        public string? TestFilter { get; init; }
        /// <summary>`--config`: the native build configuration; null = the nearest fire.native.json next to the first file.</summary>
        public string? ConfigFile { get; init; }
        /// <summary>`forge run app.fire -- a b`: what follows `--` - the arguments of the program (Environment.Args); null without `--`.</summary>
        public IReadOnlyList<string>? ProgramArguments { get; init; }
        /// <summary>`--keep`: keep the generated C++ next to the program.</summary>
        public bool KeepSources { get; init; }
        /// <summary>`--keep-all`: a native build keeps every class and member, also what the program does not reach (`--stripping 0`).</summary>
        public bool KeepAll { get; init; }
        /// <summary>`--stripping 0-3|none|protected|methods|all`: how much a native build leaves out (`"stripping"` in fire.native.json, the project's setting; docs/NATIVE_BACKEND.md).</summary>
        public fire.Native.StrippingLevel? Stripping { get; init; }
        /// <summary>`--linkage source|static|shared`: how a native build links the libraries (`"linkage"` in fire.native.json, the project's setting; ROADMAP 9).</summary>
        public string? Linkage { get; init; }
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
        public const string DefaultNativeOutputFile = "out.c";

        public static string Usage =>
            """
            Usage:
              fire.Compiler run   <file>... [-m DEBUG|RELEASE|PERFORMANCE] [-f 32|64] [-- <arguments of the program>...]
              fire.Compiler build <file>... [-o <target.exe>] [-m DEBUG|RELEASE|PERFORMANCE] [-f 32|64] [--engine vm|native] [-t <target>] [--toolchain <name>] [--config <file>] [--keep] [--stripping 0-3] [--keep-all] [--linkage source|static|shared]
              fire.Compiler native <file>... [-o <target.cpp>] [-t <target>] [-f 32|64] [--config <file>] [--stripping 0-3] [--keep-all]
              fire.Compiler ui    <file.fxml> [-o <file>]
              fire.Compiler biscuit <file.biscuit> [-o <file>]
              fire.Compiler falstad <circuit.txt> [-o <file.biscuit>]
              fire.Compiler run|build <project.fireproj | solution.firesln> [-p <project>] [...]
              fire.Compiler test  [<file>... | <project.fireproj> | <solution.firesln>] [-p <project>] [--engine vm|native] [-m ...] [-f 32|64]

            run     compiles the files into one program and runs it. What follows `--` goes to the program (Environment.Args; for a project instead of its settings.arguments).
            build   turns them into a program (a project without -o or "output": <project>/bin/<target>-<mode>/<name>, target vm or the native one, mode dbg/rel/perf):
                    a self-contained executable that carries the VM (default: out.exe), or - with --engine native, or "engine": "native" in
                    fire.native.json - C (and the natives in C++) built with the toolchain of the configuration for the target (-o is the program; for a toolchain that only writes files, a folder).
                    A target other than this machine (-t esp32, wasm, another system) is always native: the VM exists only for this machine.
            -m      Execution mode (default: whatever the script sets with #debug/#performance, otherwise RELEASE).
            native  translates the files into C (written next to fire_rt.h, the runtime it includes; default: out.c - and fire_natives.cpp with natives in C++)
                    - an experimental ahead-of-time backend, see docs/NATIVE_BACKEND.md.
            -t      Target of a native build: windows, linux, macos, esp32, freertos, wasm (a page for the browser, needs Emscripten) or one that fire.native.json defines (default: this machine). The target decides the
                    default precision of float, which libraries can be imported, which platform package of the runtime is used and how the program starts.
            --config  The native build configuration (default: the nearest fire.native.json next to the first file, else built-in defaults): engine, target,
                    toolchain (which C++ compiler), own targets. The editor edits the same file.
            --stripping 0-3  How much a native build leaves out of what the program does not reach (docs/NATIVE_BACKEND.md; "stripping" in fire.native.json or the project):
                    0 none (everything stays, as in the VM), 1 protected (every class and public member stays), 2 methods (every class with its constructors, fields
                    and properties stays, a method only when it is used), 3 all (the default: only what the program reaches). Names work as well (--stripping methods).
                    Names given as literals to reflection (Reflect.New("Dog")) always stay; a debug build always keeps everything.
            --keep-all  The same as --stripping 0.
            --linkage source|static|shared  How a native build links the libraries and packages: source (the default) puts everything into one C file; static builds each
                    library once as a static library in the user-wide cache (ember cache clean) and links it; shared (Linux, macOS) builds them as shared libraries put next to the program ("linkage" in fire.native.json or the project).
            -f      Precision of float in bits: 32 or 64 (default: whatever the script sets with #floatwidth, otherwise 64).
            --types lax|strict  How a type conflict (assigning a string to an int, a wrong argument type) is reported: a warning (lax, the default) or an error (strict). A project setting
                    typeChecking does the same, #lax / #strict in a file decides for that file (docs/TYPES.md).
            -D name Defines the symbol "name" for #if (repeatable, also --define name or -Dname).
            -o      Name of the file produced by build or native.
            test    runs the tests: the methods with [Test] (docs/TESTING.md), in the VM or with --engine native as a native program for this machine. Without a file: the solution or the
                    project in the current folder (all projects of a solution, or -p <name>). The files in testing/ of a project belong to its tests; the top level of a program does not run.
                    --filter A.B,C runs only these tests (Class.Method, or a class for all its tests; * for any text). Exit code 0 when every test passed, 1 otherwise.
            ui      writes the script generated from the markup of a user interface (docs/UI_MARKUP.md) - not needed to build: `#include "x.fxml"` does it on the fly.
            biscuit writes the script generated from the schematic of a circuit (docs/CIRCUITS.md) - `#include "x.biscuit"` does it on the fly as well.
            falstad converts a circuit of Paul Falstad's simulator (File > Export As Text) into a schematic (.biscuit); what the package circuits has not got is named.

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
                case "test": command = CommandKind.Test; break;
                default:
                    return Fail(CommandKind.Help, $"Unknown command '{args[0]}' (expected: run, build, native or test).");
            }

            var files = new List<string>();
            VmExecutionMode? mode = null;
            int? floatWidth = null;
            TypeCheckMode? typeChecking = null;
            TargetProfile? target = null;
            string? targetName = null, engine = null, toolchainName = null, configFile = null, projectName = null, testFilter = null;
            bool keep = false, keepAll = false;
            fire.Native.StrippingLevel? stripping = null;
            string? linkage = null;
            string? output = null;
            var defines = new List<string>();
            List<string>? programArguments = null;

            for (int i = 1; i < args.Count; i++)
            {
                string arg = args[i];

                if (arg == "--")
                {
                    // everything after `--` belongs to the program that runs
                    if (command != CommandKind.Run) return Fail(command, "Arguments for the program (after --) are only available with run.");
                    programArguments = args.Skip(i + 1).ToList();
                    break;
                }

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
                    if (command is not (CommandKind.Build or CommandKind.Test)) return Fail(command, "--engine is only available with build and test.");
                    string? value = engineInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (value == null || Unquote(value).ToLowerInvariant() is not ("vm" or "native")) return Fail(command, "--engine must be followed by vm or native.");
                    engine = Unquote(value).ToLowerInvariant();
                }
                else if (TryOption(arg, "--toolchain", "--toolchain", out var toolchainInline))
                {
                    if (command is not (CommandKind.Build or CommandKind.Test)) return Fail(command, "--toolchain is only available with build and test.");
                    string? value = toolchainInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value)) return Fail(command, "--toolchain must be followed by the toolchain name.");
                    toolchainName = Unquote(value);
                }
                else if (TryOption(arg, "--filter", "--filter", out var filterInline))
                {
                    if (command != CommandKind.Test) return Fail(command, "--filter is only available with test.");
                    string? value = filterInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value)) return Fail(command, "--filter must be followed by the tests (Class.Method or Class, separated by commas).");
                    testFilter = Unquote(value);
                }
                else if (TryOption(arg, "--config", "--config", out var configInline))
                {
                    if (command is not (CommandKind.Native or CommandKind.Build or CommandKind.Test)) return Fail(command, "--config is only available with build, native and test.");
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
                else if (string.Equals(arg, "--linkage", StringComparison.OrdinalIgnoreCase))
                {
                    if (command is not (CommandKind.Build or CommandKind.Native or CommandKind.Test)) return Fail(command, "--linkage is only available with build, native and test.");
                    string? value = i + 1 < args.Count ? args[++i].ToLowerInvariant() : null;
                    if (value is not ("source" or "static" or "shared")) return Fail(command, "--linkage must be followed by source, static or shared.");
                    linkage = value;
                }
                else if (string.Equals(arg, "--stripping", StringComparison.OrdinalIgnoreCase))
                {
                    if (command is not (CommandKind.Build or CommandKind.Native or CommandKind.Test)) return Fail(command, "--stripping is only available with build, native and test.");
                    string? value = i + 1 < args.Count ? args[++i] : null;
                    stripping = fire.Native.Strippings.Parse(value);
                    if (stripping == null) return Fail(command, "--stripping must be followed by 0, 1, 2 or 3 (none, protected, methods, all).");
                }
                else if (string.Equals(arg, "--keep-all", StringComparison.OrdinalIgnoreCase))
                {
                    if (command is not (CommandKind.Build or CommandKind.Native or CommandKind.Test)) return Fail(command, "--keep-all is only available with build, native and test.");
                    keepAll = true;
                }
                else if (string.Equals(arg, "--keep", StringComparison.OrdinalIgnoreCase))
                {
                    if (command != CommandKind.Build) return Fail(command, "--keep is only available with build.");
                    keep = true;
                }
                else if (TryOption(arg, "--types", "--types", out var typesInline))
                {
                    string? value = typesInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (value == null || Unquote(value).ToLowerInvariant() is not ("lax" or "strict")) return Fail(command, "--types must be followed by lax or strict.");
                    typeChecking = Unquote(value).ToLowerInvariant() == "strict" ? TypeCheckMode.Strict : TypeCheckMode.Lax;
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

            if (files.Count == 0 && command != CommandKind.Test)
                return Fail(command, "No source file was specified.");

            return new CommandLineOptions
            {
                Command = command,
                Files = files,
                Mode = mode,
                FloatWidth = floatWidth,
                TypeChecking = typeChecking,
                Defines = defines,
                Target = target,
                TargetName = targetName,
                Engine = engine,
                TestFilter = testFilter,
                ToolchainName = toolchainName,
                ConfigFile = configFile,
                ProgramArguments = programArguments,
                KeepSources = keep,
                KeepAll = keepAll,
                Stripping = stripping,
                Linkage = linkage,
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
            Linker.ProcessDefaultTypeChecking = options.TypeChecking;
            if (options.Command == CommandKind.Help)
            {
                stdout.WriteLine(CommandLineParser.Usage);
                return ExitOk;
            }

            // the warnings of the code (`warn 123: "text"`) are printed to stderr while the program is compiled; they do not stop anything
            var collector = new DiagnosticCollector { OnReport = d => { if (d.IsWarning) stderr.WriteLine(d.ToLogLine()); } };
            using var collecting = collector.Begin();

            if (options.Command == CommandKind.Test) return Test(options, stdout, stderr);
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
            string? engine = ChooseEngine(options.Engine, config.Engine, options.TargetName, config, stderr);
            if (engine == null) return ExitUsage;
            if (engine == "native") return NativeBuild(options, config, sources, stdout, stderr);
            try
            {
                new Linker { Defines = options.Defines, BasePath = BaseDirectory(options), SourcePaths = SourceFiles(options) }.CompileAndLink(sources, null, options.OutputFile, options.Mode, options.FloatWidth);
            }
            catch (Exception ex) when (IsCompileError(ex))
            {
                stderr.WriteLine(DescribeErrors(ex));
                return ExitScriptError;
            }
            stdout.WriteLine($"{Path.GetFullPath(options.OutputFile)} ({new FileInfo(options.OutputFile).Length} bytes)");
            return ExitOk;
        }

        /// <summary>The engine of a build: `--engine`, else the project's or the configuration's - but a target other than this machine is always native: the VM
        /// (a self-contained file with the .NET runtime) exists only for the machine it runs on, and the targets of the native backend (esp32, wasm, another system)
        /// need no .NET at all. `--engine vm` with such a target is an error (null).</summary>
        internal static string? ChooseEngine(string? given, string? configured, string? targetName, fire.Native.NativeConfig config, TextWriter stderr)
        {
            TargetProfile? target = null;
            try { if (targetName != null || config.Target != null) target = config.ResolveTarget(targetName); }
            catch (fire.Native.NativeConfigException) { }   // (an unknown target is reported by the native build)
            bool foreign = target != null && target.Name != TargetProfile.Host.Name || target == null && targetName != null;
            if (given == "vm" && foreign)
            {
                stderr.WriteLine($"--engine vm cannot build for the target '{target?.Name ?? targetName}': the VM runs only on this machine ({TargetProfile.Host.Name}). Leave out --engine or use --engine native.");
                return null;
            }
            if (foreign) return "native";
            return given ?? configured?.ToLowerInvariant() ?? "vm";
        }

        private static fire.Native.NativeConfig LoadConfig(CommandLineOptions options)
        {
            if (options.ConfigFile != null)
            {
                if (!File.Exists(options.ConfigFile)) throw new fire.Native.NativeConfigException($"Configuration not found: {options.ConfigFile}");
                return KeepAll(fire.Native.NativeConfig.Load(options.ConfigFile), options);
            }
            return KeepAll(fire.Native.NativeConfig.FindFor(options.Files[0]), options);
        }

        /// <summary>`--keep-all` and `--stripping` go before the `"stripping"` of the configuration (and of the project, see NativeBuilder.StrippingOf).</summary>
        private static fire.Native.NativeConfig KeepAll(fire.Native.NativeConfig config, CommandLineOptions options)
        {
            if (options.KeepAll) { config.Stripping = fire.Native.StrippingLevel.None; config.StrippingFromCommandLine = true; }
            else if (options.Stripping is { } level) { config.Stripping = level; config.StrippingFromCommandLine = true; }
            if (options.Linkage != null) config.Linkage = options.Linkage;
            return config;
        }

        /// <summary>`build` with the native engine: translate for the target and let the toolchain build the program (or write the files of a project).</summary>
        private static int NativeBuild(CommandLineOptions options, fire.Native.NativeConfig config, List<string> sources, TextWriter stdout, TextWriter stderr)
        {
            try
            {
                var target = config.ResolveTarget(options.TargetName);
                var toolchain = config.ResolveToolchain(target, options.ToolchainName);
                string output = options.OutputGiven ? options.OutputFile : NativeBuilder.DefaultOutput(target);
                var result = NativeBuilder.Build(sources, config, target, toolchain, output, options.Mode, options.FloatWidth, options.KeepSources, basePath: BaseDirectory(options), defines: options.Defines, sourcePaths: SourceFiles(options));
                stdout.Write(result.Log);
                if (!result.Ok) { stderr.WriteLine($"The native build for {target.Name} failed."); return ExitScriptError; }
                stdout.WriteLine($"{result.Output} (native, target {target.Name}, toolchain {toolchain.EffectiveKind})");
                return ExitOk;
            }
            catch (fire.Native.NativeConfigException ex) { stderr.WriteLine(ex.Message); return ExitUsage; }
            catch (Exception ex) when (IsCompileError(ex)) { stderr.WriteLine(DescribeErrors(ex)); return ExitScriptError; }
            catch (fire.Native.NativeNotSupportedException ex) { stderr.WriteLine($"error {ErrorCatalog.NativeUnsupported}: Not supported by the native backend yet: " + ex.Message); return ExitScriptError; }
        }

        private static int Native(CommandLineOptions options, List<string> sources, TextWriter stdout, TextWriter stderr)
        {
            try
            {
                var config = LoadConfig(options);
                var target = NativeBuilder.WithRequestedSdl(config.ResolveTarget(options.TargetName), null);   // (only the sources: SDL2 for a window, unless FIRE_SDL or the target say 3)
                var program = NativeBuilder.GenerateLinked(sources, target, options.Mode, options.FloatWidth, BaseDirectory(options), options.Defines, sourcePaths: SourceFiles(options), stripping: config.Stripping);
                string cpp = program.C;
                string full = Path.GetFullPath(options.OutputFile);
                NativeBuilder.WriteFiles(Path.GetDirectoryName(full)!, cpp, target, Path.GetFileName(full), config.Path == null ? null : Path.GetDirectoryName(config.Path), program.Natives);
                string natives = program.Natives != null ? Path.Combine(Path.GetDirectoryName(full)!, NativeBuilder.NativesName) : "";
                string extra = string.Join(" ", target.Native.CompileArgs);
                stdout.WriteLine(target.IsEmbedded || target.Native.Entry.Kind == fire.Runtime.EntryKind.Function
                    ? $"{full} ({cpp.Length} characters){(natives.Length > 0 ? " and " + natives : "")} for {target.Name} - add {(natives.Length > 0 ? "them" : "it")}, fire_rt.h and platform/{target.Native.Platform}/ to your project (the entry point is {target.Native.Entry.Name})"
                    : natives.Length == 0
                        ? $"{full} ({cpp.Length} characters) - compile with: cc -std=c11 -O2 {extra} \"{full}\" -o program -lm"
                        : $"{full} ({cpp.Length} characters) and {natives} - compile with: cc -std=c11 -O2 {extra} -c \"{full}\" -o program.o && c++ -std=c++17 -O2 {extra} \"{natives}\" program.o -o program");
            }
            catch (fire.Native.NativeConfigException ex)
            {
                stderr.WriteLine(ex.Message);
                return ExitUsage;
            }
            catch (Exception ex) when (IsCompileError(ex))
            {
                stderr.WriteLine(DescribeErrors(ex));
                return ExitScriptError;
            }
            catch (fire.Native.NativeNotSupportedException ex)
            {
                stderr.WriteLine($"error {ErrorCatalog.NativeUnsupported}: Not supported by the native backend yet: " + ex.Message);
                return ExitScriptError;
            }
            return ExitOk;
        }

        /// <summary>`test` (docs/TESTING.md): the tests of files, of a project or of every project of a solution.</summary>
        private static int Test(CommandLineOptions options, TextWriter stdout, TextWriter stderr)
        {
            var files = options.Files.ToList();
            if (files.Count == 0)
            {
                // nothing named: the solution in this folder, else the project
                string here = Directory.GetCurrentDirectory();
                var found = Directory.GetFiles(here, "*" + fire.Projects.FireSolution.Extension);
                if (found.Length == 0) found = Directory.GetFiles(here, "*" + fire.Projects.FireProject.Extension);
                if (found.Length != 1) { stderr.WriteLine(found.Length == 0 ? "No file was named and there is no solution or project in this folder." : "There are several solutions or projects in this folder: name one."); return ExitUsage; }
                files.Add(found[0]);
            }
            foreach (var f in files)
                if (!File.Exists(f)) { stderr.WriteLine($"File not found: {f}"); return ExitUsage; }

            bool live = stdout == Console.Out && !Console.IsOutputRedirected;
            var runner = new TestRunner(stdout, live) { Filter = options.TestFilter != null ? fire.Compiler.TestFilter.Parse(options.TestFilter) : null };
            fire.Native.NativeConfig? config = null;
            string engine = options.Engine ?? "vm";
            if (engine == "native")
            {
                try { config = KeepAll(options.ConfigFile != null ? fire.Native.NativeConfig.Load(options.ConfigFile) : fire.Native.NativeConfig.FindFor(files[0]), options); }
                catch (fire.Native.NativeConfigException ex) { stderr.WriteLine(ex.Message); return ExitUsage; }
            }

            var names = files.Select(Path.GetFileName).ToList();   // a problem names the file, without its folder
            TestRun Run(string title, IReadOnlyList<string> sources, fire.Projects.BuildPlan? plan) => engine == "native"
                ? runner.RunNative(title, sources, config!, options.ToolchainName, options.Mode, options.FloatWidth, options.Defines, plan, plan == null ? names : null)
                : runner.RunInVm(title, sources, options.Mode, options.FloatWidth, options.Defines, plan, plan == null ? names : null);

            var runs = new List<TestRun>();
            bool compileFailed = false;
            void Guarded(string title, Func<TestRun> run)
            {
                try { runs.Add(run()); }
                catch (Exception ex) when (IsCompileError(ex))
                {
                    stderr.WriteLine($"{title}: {DescribeErrors(ex)}");
                    compileFailed = true;
                }
                catch (fire.Native.NativeNotSupportedException ex) { stderr.WriteLine($"{title}: error {ErrorCatalog.NativeUnsupported}: Not supported by the native backend yet: " + ex.Message); compileFailed = true; }
                catch (fire.Native.NativeConfigException ex) { stderr.WriteLine(ex.Message); compileFailed = true; }
            }

            if (files.Count == 1 && IsProjectFile(files[0]))
            {
                fire.Projects.Workspace workspace;
                List<fire.Projects.LoadedProject> projects;
                try
                {
                    workspace = fire.Projects.Workspace.Open(files[0]);
                    projects = options.ProjectName != null ? new() { ProjectBuilder.SelectProject(workspace, options.ProjectName) } : workspace.Projects.ToList();
                }
                catch (fire.Projects.ProjectException ex) { stderr.WriteLine(ex.Message); return ExitUsage; }
                foreach (var project in projects)
                {
                    fire.Projects.BuildPlan plan;
                    try
                    {
                        plan = fire.Projects.BuildPlan.Create(workspace, project, tests: true);
                        if (!plan.IsValid) throw new fire.Projects.ProjectException(string.Join(Environment.NewLine, plan.Errors));
                    }
                    catch (fire.Projects.ProjectException ex) { stderr.WriteLine($"{project.Name}: {ex.Message}"); compileFailed = true; continue; }
                    Guarded(project.Name, () => Run(project.Name, plan.SourceTexts, plan));
                }
            }
            else
            {
                var sources = files.Select(File.ReadAllText).ToList();
                string title = string.Join(", ", files.Select(Path.GetFileName));
                Guarded(title, () => Run(title, sources, null));
            }

            foreach (var run in runs)
            {
                if (runs.Count > 1 && run.Results.Count == 0 && run.Problems.Count == 0) continue;   // a project of the solution without tests
                TestRunner.WriteSummary(run, stdout);
                stdout.WriteLine();
            }
            if (runs.Count > 1)
                stdout.WriteLine($"All: {runs.Sum(r => r.Results.Count)} tests, {runs.Sum(r => r.Passed)} passed, {runs.Sum(r => r.Failed)} failed, {runs.Sum(r => r.Errors)} errors");
            return !compileFailed && runs.All(r => r.Ok) ? ExitOk : ExitScriptError;
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
                }, floatWidth: options.FloatWidth, defines: options.Defines, plan: plan,
                    environment: new fire.Standard.ProgramEnvironment { Args = options.ProgramArguments ?? plan.Settings.Arguments ?? new List<string>(), ProgramPath = plan.SourcePaths.LastOrDefault() ?? "" });
            }
            catch (Exception ex) when (IsCompileError(ex))
            {
                stderr.WriteLine(DescribeErrors(ex));
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

        /// <summary>The mode a project's program is built in (`debug`, `release`, `performance`): -m, the project's settings, the tags of its files (`#debug`), else release -
        /// what the linker decides (a compile of the sources, without writing anything).</summary>
        private static string EffectiveModeName(CommandLineOptions options, fire.Projects.BuildPlan plan, TargetProfile? target)
        {
            var linked = new Linker { Defines = options.Defines, Plan = plan, Engine = target != null ? "native" : ConditionalSymbols.DefaultEngine, SourcePaths = plan.SourcePaths.Cast<string?>().ToList() }
                .CompileAndLink(plan.SourceTexts, null, null, options.Mode, options.FloatWidth, target);
            return linked.ExecutionMode.ToString().ToLowerInvariant();
        }

        /// <summary>The files of the program in the order of the sources: an `#include` is relative to the file that has it (docs/language/17-preprocessor.md).</summary>
        private static IReadOnlyList<string?> SourceFiles(CommandLineOptions options) => options.Files.Select(f => (string?)Path.GetFullPath(f)).ToList();

        /// <summary>The folder of the first file (where a source without a file of its own would look).</summary>
        private static string? BaseDirectory(CommandLineOptions options) =>
            options.Files.Count > 0 ? Path.GetDirectoryName(Path.GetFullPath(options.Files[0])) : null;

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
                fire.Native.NativeConfig config = KeepAll(options.ConfigFile != null ? fire.Native.NativeConfig.Load(options.ConfigFile) : fire.Native.NativeConfig.FindFor(plan.Project.FilePath), options);
                string? engine = ChooseEngine(options.Engine, plan.Settings.Engine ?? config.Engine, options.TargetName ?? plan.Settings.Target, config, stderr);
                if (engine == null) return ExitUsage;
                string? output = options.OutputGiven ? options.OutputFile : plan.Settings.Output;
                if (engine == "native")
                {
                    var target = config.ResolveTarget(options.TargetName ?? plan.Settings.Target);
                    var toolchain = config.ResolveToolchain(target, options.ToolchainName ?? plan.Settings.Toolchain);
                    if (options.Linkage == null && plan.Settings.Linkage != null) config.Linkage = plan.Settings.Linkage;   // (the command line, then the project, then fire.native.json)
                    // without -o and `output`: <project>/bin/<target>-<mode>/<name> (the mode the program gets: -m, the project, the tags of its files)
                    string out2 = output ?? (toolchain.EffectiveKind == "files"
                        ? fire.Projects.ProjectOutput.Folder(plan.Project.Directory, target.Name, EffectiveModeName(options, plan, target))
                        : fire.Projects.ProjectOutput.ProgramPath(plan.Project.Directory, plan.Name, target.Name, EffectiveModeName(options, plan, target), target.Name == "windows"));
                    Directory.CreateDirectory(toolchain.EffectiveKind == "files" ? out2 : Path.GetDirectoryName(Path.GetFullPath(out2))!);
                    var result = NativeBuilder.Build(plan.SourceTexts, config, target, toolchain, out2, options.Mode, options.FloatWidth, options.KeepSources, defines: options.Defines, plan: plan);
                    stdout.Write(result.Log);
                    if (!result.Ok) { stderr.WriteLine($"The native build for {target.Name} failed."); return ExitScriptError; }
                    stdout.WriteLine($"{result.Output} (native, target {target.Name}, toolchain {toolchain.EffectiveKind})");
                    return ExitOk;
                }
                output ??= fire.Projects.ProjectOutput.ProgramPath(plan.Project.Directory, plan.Name, fire.Projects.ProjectOutput.VmTarget, EffectiveModeName(options, plan, null), OperatingSystem.IsWindows());
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                new Linker { Defines = options.Defines, Plan = plan, SourcePaths = plan.SourcePaths.Cast<string?>().ToList() }.CompileAndLink(plan.SourceTexts, null, output, options.Mode, options.FloatWidth);
                stdout.WriteLine($"{Path.GetFullPath(output)} ({new FileInfo(output).Length} bytes)");
                return ExitOk;
            }
            catch (fire.Native.NativeConfigException ex) { stderr.WriteLine(ex.Message); return ExitUsage; }
            catch (fire.Native.NativeNotSupportedException ex) { stderr.WriteLine($"error {ErrorCatalog.NativeUnsupported}: Not supported by the native backend yet: " + ex.Message); return ExitScriptError; }
            catch (fire.Projects.ProjectException ex) { stderr.WriteLine(ex.Message); return ExitScriptError; }
            catch (Package.Manager.PackageException ex) { stderr.WriteLine(ex.Message); return ExitScriptError; }
            catch (Exception ex) when (IsCompileError(ex)) { stderr.WriteLine(DescribeErrors(ex)); return ExitScriptError; }
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
                }, floatWidth: options.FloatWidth, basePath: BaseDirectory(options), defines: options.Defines, sourcePaths: SourceFiles(options),
                    environment: new fire.Standard.ProgramEnvironment { Args = options.ProgramArguments ?? Array.Empty<string>(), ProgramPath = options.Files.Count > 0 ? Path.GetFullPath(options.Files[^1]) : "" });
            }
            catch (Exception ex) when (IsCompileError(ex))
            {
                stderr.WriteLine(DescribeErrors(ex));
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

        /// <summary>The errors of a failed compile, one per line, each with its code: `error E4005: Unknown identifier 'x' (line 3)`.</summary>
        internal static string DescribeErrors(Exception ex) => string.Join(Environment.NewLine, CompileDiagnostics.Of(ex).Select(d => d.ToLogLine()));

        internal static bool IsCompileError(Exception ex) =>
            ex is ParseException or LexException or ResolverException or CompilerException or TypeCheckException
                or NotSupportedException or PreprocessorException or LibraryEntryPointException or fire.Projects.ProjectException;
    }
}
