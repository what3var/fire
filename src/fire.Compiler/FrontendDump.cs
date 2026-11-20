using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using fire.Ast;
using fire.Lexing;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>`forge tokens <file>` / `forge ast <file>` (docs/spec/FORGE_C.md): the tokens and the syntax tree of one source as text, with the
    /// diagnostics as forge prints them - the measure for the frontend in C (native/forge, docs/ROADMAP_C_PORT.md Phase C 1/2), which prints the same.
    /// The source is preprocessed with the directives of a build (`#include`, and the ones Linker registers - their parameters checked,
    /// without their effects) and the symbols of a VM run on this machine;
    /// `ast` parses it alone, as `Parser.ParseMultiple` of one source named by its file name. `--list <file> --out <dir>` dumps many files, one output
    /// each (`<dir>/<n>.txt`, n the line of the list, from 1).</summary>
    public static class FrontendDump
    {
        public static int Run(IReadOnlyList<string> args)
        {
            string command = args[0];
            if ((command == "fbc" || command == "cgen" || command == "buildplan") && args.Count >= 3)
            {
                // `cgen <options> <file>`: the C `forge native` writes (Phase D 1), the options a word (NativeDump); `buildplan <options> <file>`: what a native build would run (Phase D 2)
                if (command == "cgen" || command == "buildplan") { command = command + ":" + args[1]; args = args.Skip(1).ToList(); goto parsed; }
                // `fbc <debug|release|performance|default> <file>`: the program file `forge bytecode` writes (Phase C 4); the mode is a parameter of the command
                command = "fbc:" + args[1];
                args = args.Skip(1).ToList();
            }
            parsed:
            if (args.Count == 2) { Console.Out.Flush(); return Dump(command, args[1], Console.OpenStandardOutput()); }
            if (args.Count == 5 && args[1] == "--list" && args[3] == "--out")
            {
                var files = File.ReadAllLines(args[2]);
                Directory.CreateDirectory(args[4]);
                for (int i = 0; i < files.Length; i++)
                {
                    if (files[i].Length == 0) continue;
                    using var stream = File.Create(Path.Combine(args[4], (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt"));
                    Dump(command, files[i], stream);
                }
                return 0;
            }
            Console.Error.WriteLine($"usage: {command} <file.fire> | {command} --list <files.txt> --out <folder>");
            return 2;
        }

        /// <summary>The directives Linker registers besides `#include` (name, number of parameters); native/forge/preprocessor.c has the same list.</summary>
        private static readonly (string Name, int Count)[] LinkerDirectives =
        {
            ("import", 1), ("noconsole", 0), ("debug", 0), ("performance", 0), ("floatwidth", 1), ("intwidth", 1), ("name", 1), ("codename", 1),
            ("description", 1), ("author", 1), ("comments", 1), ("icon", 1), ("version", 1), ("fileversion", 1),
        };

        private static int Dump(string command, string path, Stream output)
        {
            if (command == "diagnostics") return DumpDiagnostics(path, output);
            if (command.StartsWith("fbc:", StringComparison.Ordinal)) return DumpProgramFile(command.Substring(4), path, output);
            if (command.StartsWith("cgen:", StringComparison.Ordinal)) return DumpNative(command.Substring(5), path, output);
            if (command.StartsWith("buildplan:", StringComparison.Ordinal)) return DumpBuildPlan(command.Substring(10), path, output);
            var o = new StringBuilder();
            int result = 0;
            try
            {
                string text = File.ReadAllText(path);
                var registry = DirectiveRegistry.CreateDefault();
                // the directives a build registers (Linker), with their numbers of parameters - what they do is not part of the frontend
                foreach (var (name, count) in LinkerDirectives) registry.Register(name, count, (_, _, _) => null);
                foreach (var symbol in ConditionalSymbols.For(null, ConditionalSymbols.DefaultEngine)) registry.Symbols.Add(symbol);
                var processed = Preprocessor.Process(text, Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", registry) with { Name = Path.GetFileName(path) };
                if (command == "tokens")
                {
                    foreach (var u in processed.Usings) o.Append("using ").Append(Esc(u)).Append('\n');
                    foreach (var t in new Lexer(processed.Source).Tokenize()) Token(o, t);
                }
                else
                {
                    var collector = new DiagnosticCollector();
                    List<fire.Ast.Stmt> program;
                    using (collector.Begin()) program = Parser.ParseMultiple(new[] { processed });
                    Node(o, 0, "program", program);
                    foreach (var d in collector.Items) o.Append(d.ToLogLine()).Append('\n');
                    foreach (var w in collector.Declared)
                        o.Append("declared ").Append(Esc(w.Class)).Append(' ').Append(w.Target).Append(' ').Append(w.Name == null ? "null" : Esc(w.Name)).Append(' ')
                         .Append(w.MinArity).Append(' ').Append(w.MaxArity).Append(' ').Append(w.IsStatic ? "static" : "instance").Append(" W").Append(w.Id).Append(' ')
                         .Append(Esc(w.Text)).Append(" line ").Append(w.Line).Append(' ').Append(w.File == null ? "null" : Esc(w.File)).Append('\n');
                }
            }
            catch (Exception ex) when (ex is LexException or ParseException or PreprocessorException)
            {
                foreach (var d in CompileDiagnostics.Of(ex)) o.Append(d.ToLogLine()).Append('\n');
                result = 1;
            }
            var bytes = new UTF8Encoding(false).GetBytes(o.ToString());
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
            return result;
        }

        /// <summary>`forge diagnostics <file>` (Phase C 3): what `forge run <file>` writes to the error output while it compiles - the warnings as they come, then the
        /// errors of a failed compile - up to and with the type check (Linker.FrontendOnly: the bytecode compiler does not run). A `//# types: strict` among the last
        /// 12 lines runs it with `--types strict`, as tests/run-cases.sh does. An exception that is no compile error: `internal: <type>: <message>`.</summary>
        private static int DumpDiagnostics(string path, Stream output)
        {
            var o = new StringBuilder();
            int result = 0;
            var lines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
            bool strict = lines.Skip(Math.Max(0, lines.Length - 12)).Any(l => l.Trim() == "//# types: strict");
            var collector = new DiagnosticCollector { OnReport = d => { if (d.IsWarning) o.Append(d.ToLogLine()).Append('\n'); } };
            var before = Linker.ProcessDefaultTypeChecking;
            Linker.ProcessDefaultTypeChecking = strict ? TypeCheckMode.Strict : null;
            Linker.FrontendOnly = true;
            try
            {
                using (collector.Begin())
                {
                    string text = File.ReadAllText(path);
                    new Linker { BasePath = Path.GetDirectoryName(Path.GetFullPath(path)), SourcePaths = new string?[] { Path.GetFullPath(path) } }.CompileAndLink(new[] { text });
                }
            }
            catch (Exception ex) when (CommandLineRunner.IsCompileError(ex))
            {
                o.Append(CommandLineRunner.DescribeErrors(ex).Replace("\r\n", "\n")).Append('\n');
                result = 1;
            }
            catch (Exception ex)
            {
                o.Append("internal: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message.Replace("\r\n", "\n")).Append('\n');
                result = 1;
            }
            finally
            {
                Linker.FrontendOnly = false;
                Linker.ProcessDefaultTypeChecking = before;
            }
            var bytes = new UTF8Encoding(false).GetBytes(o.ToString());
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
            return result;
        }

        /// <summary>`forge fbc <mode> <file>` (Phase C 4, docs/spec/FORGE_C.md): the bytes `forge bytecode <file> -m <mode>` writes (with the line tables; in Debug the
        /// sources), compiled as there; `default` leaves out `-m`. A `//# types: strict` among the last 12 lines compiles with `--types strict` (as tests/run-cases-vm.sh).
        /// A failed compile gives the text `error` and the errors as forge prints them, an exception that is no compile error `internal: <type>: <message>`.</summary>
        private static int DumpProgramFile(string mode, string path, Stream output)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
            bool strict = lines.Skip(Math.Max(0, lines.Length - 12)).Any(l => l.Trim() == "//# types: strict");
            fire.Runtime.VmExecutionMode? executionMode = mode switch
            {
                "debug" => fire.Runtime.VmExecutionMode.Debug, "release" => fire.Runtime.VmExecutionMode.Release, "performance" => fire.Runtime.VmExecutionMode.Performance, _ => null,
            };
            var before = Linker.ProcessDefaultTypeChecking;
            Linker.ProcessDefaultTypeChecking = strict ? TypeCheckMode.Strict : null;
            byte[] bytes;
            int result = 0;
            try
            {
                string full = Path.GetFullPath(path);
                fire.Runtime.LinkedProgram program;
                if (full.EndsWith(fire.Projects.FireProject.Extension, StringComparison.OrdinalIgnoreCase) || full.EndsWith(fire.Projects.FireSolution.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    // a project or a solution (Phase D 3): the program of its startup project (a library: as ProjectBuilder.Check compiles it)
                    var workspace = fire.Projects.Workspace.Open(full);
                    var plan = ProjectBuilder.PlanOrThrow(workspace, ProjectBuilder.SelectProject(workspace, null));
                    // (`fbc native <project>`: linked for a native build for this machine, as NativeBuilder links it - the input of the generator)
                    program = mode == "native"
                        ? new Linker { Plan = plan, Engine = "native", SourcePaths = plan.SourcePaths.Cast<string?>().ToList(), Optimize = NativeBuilder.OptimizeOf(null, plan, fire.Runtime.TargetProfile.Host) }
                            .CompileAndLink(plan.SourceTexts, null, null, null, null, fire.Runtime.TargetProfile.Host)
                        : new Linker { Plan = plan, SourcePaths = plan.SourcePaths.Cast<string?>().ToList() }.CompileAndLink(plan.SourceTexts, null, null, executionMode);
                }
                else program = new Linker { BasePath = Path.GetDirectoryName(full), SourcePaths = new string?[] { full } }
                    .CompileAndLink(new[] { File.ReadAllText(path) }, null, null, executionMode);
                bytes = fire.Bytecode.ProgramFile.Write(program);
            }
            catch (Exception ex) when (CommandLineRunner.IsCompileError(ex))
            {
                bytes = new UTF8Encoding(false).GetBytes("error\n" + CommandLineRunner.DescribeErrors(ex).Replace("\r\n", "\n") + "\n");
                result = 1;
            }
            catch (Exception ex)
            {
                bytes = new UTF8Encoding(false).GetBytes("internal: " + ex.GetType().Name + ": " + ex.Message.Replace("\r\n", "\n") + "\n");
                result = 1;
            }
            finally { Linker.ProcessDefaultTypeChecking = before; }
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
            return result;
        }

        /// <summary>`forge cgen <options> <file>` (Phase D 1, docs/spec/FORGE_C.md): what `forge native <file>` writes - the program in C, then (when there are) the natives
        /// in C++ and the units of a split program, each after a line `//==== <name>` - generated as there (`NativeBuilder.GenerateLinked`, the toolchain `files`, a
        /// configuration without a file). The options are words joined by `+`: `default`; `debug`, `release`, `performance` (`-m`); `split` (`--split on`); `strip0` ...
        /// `strip3` (`--stripping`); `speed`, `size` (`--optimize`); `i16`, `i32` (`-i`); `f32` (`-f 32`); `t:<target>` (`-t`). A failed compile gives `error` and the
        /// errors, a program the backend refuses `unsupported: <message>`, any other exception `internal: <type>: <message>`. `//# types: strict` as with `fbc`.</summary>
        private static int DumpNative(string options, string path, Stream output)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
            bool strict = lines.Skip(Math.Max(0, lines.Length - 12)).Any(l => l.Trim() == "//# types: strict");
            fire.Runtime.VmExecutionMode? mode = null;
            int? floatWidth = null, intWidth = null;
            string? targetName = null;
            var config = new fire.Native.NativeConfig();
            foreach (var word in options.Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (word)
                {
                    case "default": break;
                    case "debug": mode = fire.Runtime.VmExecutionMode.Debug; break;
                    case "release": mode = fire.Runtime.VmExecutionMode.Release; break;
                    case "performance": mode = fire.Runtime.VmExecutionMode.Performance; break;
                    case "split": config.Split = "on"; break;
                    case "speed": case "size": config.Optimize = word; config.OptimizeFromCommandLine = true; break;
                    case "i16": intWidth = 16; break;
                    case "i32": intWidth = 32; break;
                    case "f32": floatWidth = 32; break;
                    default:
                        if (word.StartsWith("strip", StringComparison.Ordinal) && int.TryParse(word.AsSpan(5), out int level)) { config.Stripping = (fire.Native.StrippingLevel)level; config.StrippingFromCommandLine = true; }
                        else if (word.StartsWith("t:", StringComparison.Ordinal)) targetName = word.Substring(2);
                        else throw new ArgumentException("unknown option " + word);
                        break;
                }
            }
            var before = Linker.ProcessDefaultTypeChecking;
            var intBefore = Linker.ProcessIntWidth;
            Linker.ProcessDefaultTypeChecking = strict ? TypeCheckMode.Strict : null;
            Linker.ProcessIntWidth = intWidth;
            var o = new StringBuilder();
            int result = 0;
            try
            {
                string full = Path.GetFullPath(path);
                var target = config.ResolveTarget(targetName);
                var program = NativeBuilder.GenerateLinked(new[] { File.ReadAllText(path) }, target, mode, floatWidth, Path.GetDirectoryName(full), null, sourcePaths: new string?[] { full },
                    stripping: config.Stripping, globalJobs: config.GlobalJobsValue, optimize: NativeBuilder.OptimizeOf(config, (fire.Projects.BuildPlan?)null, target), toolchainKind: "files", split: config.SplitValue);
                o.Append(program.C);
                if (program.Natives != null) o.Append("//==== ").Append(NativeBuilder.NativesName).Append('\n').Append(program.Natives);
                if (program.Units != null) foreach (var unit in program.Units) o.Append("//==== ").Append(unit.Name).Append(' ').Append(unit.Kind).Append(' ').Append(unit.Cost).Append('\n').Append(unit.Text);
            }
            catch (Exception ex) when (CommandLineRunner.IsCompileError(ex))
            {
                o.Clear().Append("error\n").Append(CommandLineRunner.DescribeErrors(ex).Replace("\r\n", "\n")).Append('\n');
                result = 1;
            }
            catch (fire.Native.NativeNotSupportedException ex)
            {
                o.Clear().Append("unsupported: ").Append(ex.Message.Replace("\r\n", "\n")).Append('\n');
                result = 1;
            }
            catch (Exception ex)
            {
                o.Clear().Append("internal: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message.Replace("\r\n", "\n")).Append('\n');
                result = 1;
            }
            finally
            {
                Linker.ProcessDefaultTypeChecking = before;
                Linker.ProcessIntWidth = intBefore;
            }
            var bytes = new UTF8Encoding(false).GetBytes(o.ToString());
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
            return result;
        }

        /// <summary>`forge buildplan <options> <file>` (Phase D 2, docs/spec/FORGE_C.md): what `forge build <file> --engine native` (linkage "source") would run - the steps of
        /// NativeBuilder.Build up to the cache, without running a compiler: the toolchain (after the fallback of a C-only compiler for natives in C++), the lines of the log
        /// before the commands (the fallback, a quick build), the commands (the folder of the build `&lt;build&gt;`, the program `&lt;program&gt;`), the key of
        /// ProgramCache (`none`: not cached) and every file of the folder of the build with its SHA-256. The options are those of `cgen` (`split` is `"split": "on"`) and
        /// `nosplit` (`"split": "off"`), `tc:<toolchain>` (`--toolchain`; default: the machine's compiler for building), `quick`, `quick1` (`--quick`, `--quick=1`).
        /// The toolchain `files` writes into a temporary folder (`<build>`). Errors as with `cgen`.</summary>
        private static int DumpBuildPlan(string options, string path, Stream output)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
            bool strict = lines.Skip(Math.Max(0, lines.Length - 12)).Any(l => l.Trim() == "//# types: strict");
            fire.Runtime.VmExecutionMode? mode = null;
            int? floatWidth = null, intWidth = null;
            string? targetName = null, toolchainName = null;
            var config = new fire.Native.NativeConfig();
            foreach (var word in options.Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                switch (word)
                {
                    case "default": break;
                    case "debug": mode = fire.Runtime.VmExecutionMode.Debug; break;
                    case "release": mode = fire.Runtime.VmExecutionMode.Release; break;
                    case "performance": mode = fire.Runtime.VmExecutionMode.Performance; break;
                    case "split": config.Split = "on"; break;
                    case "nosplit": config.Split = "off"; break;
                    case "quick": config.Quick = 0; break;
                    case "quick1": config.Quick = 1; break;
                    case "speed": case "size": config.Optimize = word; config.OptimizeFromCommandLine = true; break;
                    case "i16": intWidth = 16; break;
                    case "i32": intWidth = 32; break;
                    case "f32": floatWidth = 32; break;
                    default:
                        if (word.StartsWith("strip", StringComparison.Ordinal) && int.TryParse(word.AsSpan(5), out int level)) { config.Stripping = (fire.Native.StrippingLevel)level; config.StrippingFromCommandLine = true; }
                        else if (word.StartsWith("t:", StringComparison.Ordinal)) targetName = word.Substring(2);
                        else if (word.StartsWith("tc:", StringComparison.Ordinal)) toolchainName = word.Substring(3);
                        else throw new ArgumentException("unknown option " + word);
                        break;
                }
            }
            var before = Linker.ProcessDefaultTypeChecking;
            var intBefore = Linker.ProcessIntWidth;
            Linker.ProcessDefaultTypeChecking = strict ? TypeCheckMode.Strict : null;
            Linker.ProcessIntWidth = intWidth;
            var o = new StringBuilder();
            int result = 0;
            string? workDir = null;
            try
            {
                string full = Path.GetFullPath(path);
                // a project or a solution (Phase D 3): the program of its startup project, with the project's target, toolchain, stripping and direction
                fire.Projects.BuildPlan? plan = null;
                if (full.EndsWith(fire.Projects.FireProject.Extension, StringComparison.OrdinalIgnoreCase) || full.EndsWith(fire.Projects.FireSolution.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    var workspace = fire.Projects.Workspace.Open(full);
                    plan = ProjectBuilder.PlanOrThrow(workspace, ProjectBuilder.SelectProject(workspace, null));
                    if (plan.Type == fire.Projects.OutputType.Library) { o.Append("library\n"); goto written; }
                }
                var target = config.ResolveTarget(targetName ?? plan?.Settings.Target);
                var toolchain = config.ResolveToolchain(target, toolchainName ?? plan?.Settings.Toolchain, fire.Native.ToolchainPurpose.Build);
                string exe = Path.GetFullPath("plan-out" + target.ProgramExtension);
                var log = new StringBuilder();
                NativeBuilder.NativeProgram program;
                while (true)
                {
                    // (Build: the direction, the program for the kind of the toolchain, the compiler that compiles C++ for a C-only one)
                    toolchain = NativeBuilder.WithDirection(toolchain, NativeBuilder.OptimizeOf(config, plan, target));
                    program = plan != null
                        ? NativeBuilder.GenerateLinked(plan.SourceTexts, target, mode, floatWidth, null, null, plan, null,
                            NativeBuilder.StrippingOf(config, plan), config.GlobalJobsValue, toolchain.EffectiveKind, NativeBuilder.OptimizeOf(config, plan, target), config.Stats,
                            NativeBuilder.QuickBuild.SplitOf(config, toolchain.EffectiveKind))
                        : NativeBuilder.GenerateLinked(new[] { File.ReadAllText(path) }, target, mode, floatWidth, Path.GetDirectoryName(full), null, null, new string?[] { full },
                            NativeBuilder.StrippingOf(config, null), config.GlobalJobsValue, toolchain.EffectiveKind, NativeBuilder.OptimizeOf(config, (fire.Projects.BuildPlan?)null, target), config.Stats,
                            NativeBuilder.QuickBuild.SplitOf(config, toolchain.EffectiveKind));
                    if (program.Natives != null && NativeBuilder.CppFallback(toolchain) is { } cppToolchain)
                    {
                        log.AppendLine(NativeBuilder.CppFallbackNote(toolchain, cppToolchain, program.Natives));
                        toolchain = cppToolchain;
                        continue;
                    }
                    break;
                }
                workDir = Path.Combine(Path.GetTempPath(), "fire-native-" + Guid.NewGuid().ToString("N"));
                var commands = new List<(string Executable, string Arguments)>();
                string? key = null;
                if (toolchain.EffectiveKind == "files")
                {
                    NativeBuilder.WriteFiles(workDir, program.C, target, NativeBuilder.SourceName, null, program.Natives, program.Units);
                    if (toolchain.Layout == "idf-component")
                    {
                        string moreSources = program.Units == null ? "" : string.Concat(program.Units.Where(u => u.Kind is not (fire.Native.SplitUnitKind.Header or fire.Native.SplitUnitKind.Runtime)).Select(u => $" \"{u.Name}\""));
                        File.WriteAllText(Path.Combine(workDir, "CMakeLists.txt"),
                            $"# Generated by fire.Native for target '{target.Name}': the program as an ESP-IDF component (call {target.Native.Entry.Name} from the application).\n" +
                            $"idf_component_register(SRCS \"{NativeBuilder.SourceName}\"{moreSources}{(program.Natives != null ? $" \"{NativeBuilder.NativesName}\"" : "")}\n                       INCLUDE_DIRS \".\")\n" +
                            "target_compile_options(${COMPONENT_LIB} PRIVATE $<$<COMPILE_LANGUAGE:C>:-std=gnu11> $<$<COMPILE_LANGUAGE:CXX>:-std=gnu++17>)\n");
                    }
                    log.AppendLine($"{workDir}: {NativeBuilder.SourceName}{(program.Natives != null ? ", " + NativeBuilder.NativesName : "")}, fire_rt.h and platform/{target.Native.Platform}/ for {target.Name}; the entry point is {target.Native.Entry.Name}");
                }
                else
                {
                    string cppFile = NativeBuilder.WriteFiles(workDir, program.C, target, NativeBuilder.SourceName, null, program.Natives, program.Units);
                    string? nativesFile = program.Natives != null ? Path.Combine(workDir, NativeBuilder.NativesName) : null;
                    if (toolchain.EffectiveKind != "custom" && fire.Native.ToolchainDetector.Find(toolchain) == null)
                    {
                        o.Append("toolchain ").Append(toolchain.EffectiveKind).Append("\nnot found ").Append(toolchain.EffectiveCompiler).Append('\n');
                        goto written;
                    }
                    fire.Native.ToolchainDef? nativesToolchain = null;
                    if (config.Quick is { } quick)
                    {
                        nativesToolchain = toolchain;
                        toolchain = NativeBuilder.QuickBuild.Apply(toolchain, quick);
                        log.AppendLine(NativeBuilder.QuickBuild.LogLine(quick, toolchain.EffectiveKind));
                    }
                    var units = program.Units?.Where(u => u.Kind != fire.Native.SplitUnitKind.Header)
                        .OrderByDescending(u => nativesToolchain != null && u.Kind == fire.Native.SplitUnitKind.Natives).ThenByDescending(u => u.Cost).ToList();
                    commands = units != null ? NativeBuilder.SplitCommands(toolchain, target, units, program.Units![0].Name, nativesFile, workDir, exe, program.Gui, nativesToolchain)
                        : NativeBuilder.BuildCommands(toolchain, target, cppFile, nativesFile, workDir, exe, program.Gui).ToList();
                    key = NativeBuilder.ProgramCache.Key(toolchain, target, workDir, exe, commands, config.Stats, units?.Count ?? 0, config.Quick);
                }
                o.Append("toolchain ").Append(toolchain.EffectiveKind).Append('\n');
                if (log.Length > 0) o.Append("log\n").Append(log.ToString().Replace(workDir, "<build>", StringComparison.Ordinal));
                foreach (var (executable, arguments) in commands)
                    o.Append("command ").Append(executable).Append(' ').Append(arguments.Replace(exe, "<program>", StringComparison.Ordinal).Replace(workDir, "<build>", StringComparison.Ordinal)).Append('\n');
                o.Append("key ").Append(key ?? "none").Append('\n');
                foreach (var file in Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(workDir, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal))
                    o.Append("file ").Append(file).Append(' ').Append(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(workDir, file)))).ToLowerInvariant()).Append('\n');
                written:;
            }
            catch (Exception ex) when (CommandLineRunner.IsCompileError(ex))
            {
                o.Clear().Append("error\n").Append(CommandLineRunner.DescribeErrors(ex).Replace("\r\n", "\n")).Append('\n');
                result = 1;
            }
            catch (fire.Native.NativeNotSupportedException ex)
            {
                o.Clear().Append("unsupported: ").Append(ex.Message.Replace("\r\n", "\n")).Append('\n');
                result = 1;
            }
            catch (Exception ex)
            {
                o.Clear().Append("internal: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message.Replace("\r\n", "\n")).Append('\n');
                result = 1;
            }
            finally
            {
                Linker.ProcessDefaultTypeChecking = before;
                Linker.ProcessIntWidth = intBefore;
                if (workDir != null) try { Directory.Delete(workDir, true); } catch (IOException) { }
            }
            var bytes = new UTF8Encoding(false).GetBytes(o.ToString());
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
            return result;
        }

        /// <summary>A text in quotes: printable ASCII as it is, `\\` and `\"`, every other UTF-16 unit as `\uXXXX`.</summary>
        private static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '"') sb.Append("\\\"");
                else if (c >= 0x20 && c < 0x7F) sb.Append(c);
                else sb.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.Append('"').ToString();
        }

        private static string Bits(double d) => "0x" + BitConverter.DoubleToInt64Bits(d).ToString("X16", System.Globalization.CultureInfo.InvariantCulture);

        private static void Token(StringBuilder o, Token t)
        {
            o.Append(t.Line).Append(':').Append(t.Column).Append(' ').Append(t.Type).Append(' ').Append(Esc(t.Lexeme)).Append(" len=").Append(t.Length);
            if (t.NewlineBefore) o.Append(" nl");
            if (!string.IsNullOrEmpty(t.UnitSuffix)) o.Append(" unit=").Append(Esc(t.UnitSuffix));
            switch (t.LiteralValue)
            {
                case long l: o.Append(" int=").Append(l.ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                case double d: o.Append(" float=").Append(Bits(d)); break;
                case string s: o.Append(" value=").Append(Esc(s)); break;
                case char c: o.Append(" value=").Append(Esc(c.ToString())); break;
                case List<InterpolationSegment> segs:
                    o.Append(" segments=").Append(segs.Count);
                    foreach (var seg in segs)
                    {
                        o.Append(seg.IsExpression ? " E" : " T").Append(Esc(seg.Text));
                        if (seg.Format != null) o.Append(':').Append(Esc(seg.Format));
                    }
                    break;
            }
            o.Append('\n');
        }

        private static string ValueText(Value v)
        {
            string unit = v.Unit is { IsUnitless: false } u ? " unit=" + Esc(u.ToString()) : "";
            return v.Kind switch
            {
                ValueKind.Int => "int " + v.AsInt().ToString(System.Globalization.CultureInfo.InvariantCulture) + unit,
                ValueKind.Float => "float " + Bits(v.AsFloat()) + unit,
                ValueKind.String => "string " + Esc(v.AsString()),
                ValueKind.Char => "char " + Esc(v.AsChar().ToString()),
                ValueKind.Bool => v.AsBool() ? "bool true" : "bool false",
                ValueKind.Undefined => "undefined" + unit,
                _ => "value " + v.Kind,
            };
        }

        private static readonly Dictionary<Type, PropertyInfo[]> PropertyCache = new();

        /// <summary>The fields of a node in a fixed order: the parameters of its primary constructor, then the other properties that can be set.</summary>
        private static PropertyInfo[] Fields(Type type)
        {
            if (PropertyCache.TryGetValue(type, out var known)) return known;
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.GetIndexParameters().Length == 0).ToList();
            var ctor = type.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
            var ordered = new List<PropertyInfo>();
            if (ctor != null)
                foreach (var p in ctor.GetParameters())
                    if (props.FirstOrDefault(x => x.Name == p.Name) is { } prop) ordered.Add(prop);
            foreach (var p in props) if (!ordered.Contains(p)) ordered.Add(p);
            var result = ordered.ToArray();
            PropertyCache[type] = result;
            return result;
        }

        private static void Node(StringBuilder o, int indent, string label, object? v)
        {
            o.Append(' ', indent * 2).Append(label).Append(": ");
            switch (v)
            {
                case null: o.Append("null\n"); return;
                case string s: o.Append(Esc(s)).Append('\n'); return;
                case bool b: o.Append(b ? "true" : "false").Append('\n'); return;
                case int i: o.Append(i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n'); return;
                case long l: o.Append(l.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n'); return;
                case double d: o.Append(Bits(d)).Append('\n'); return;
                case Enum e: o.Append(e.ToString()).Append('\n'); return;
                case Value val: o.Append(ValueText(val)).Append('\n'); return;
                case IDictionary dict:
                {
                    o.Append('{').Append(dict.Count).Append("}\n");
                    foreach (DictionaryEntry entry in dict) Node(o, indent + 1, Esc((string)entry.Key), entry.Value);
                    return;
                }
                case IEnumerable list:
                {
                    var items = list.Cast<object?>().ToList();
                    o.Append(v is OwnNamespaceList ? "own" : "").Append('[').Append(items.Count).Append("]\n");
                    foreach (var item in items) Node(o, indent + 1, "-", item);
                    return;
                }
            }
            var type = v.GetType();
            o.Append(type.Name).Append('\n');
            foreach (var p in Fields(type)) Node(o, indent + 1, p.Name, p.GetValue(v));
            // what the parser notes beside the tree (TypeChecker reads it)
            if (v is AssignExpr && Parser.ParenthesizedAssignments.TryGetValue((Expr)v, out _)) Node(o, indent + 1, "Parenthesized", true);
            if (v is VarDeclStmt && Parser.Switches.TryGetValue((Stmt)v, out var values)) Node(o, indent + 1, "SwitchValues", values);
        }
    }
}
