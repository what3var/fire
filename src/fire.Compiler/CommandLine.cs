using fire.Bytecode;
using fire.Runtime;
using fire.Values;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace fire.Compiler
{
    /// <summary>Die Art des Aufrufs auf der Befehlszeile.</summary>
    public enum CommandKind { Run, Build, Native, Help }

    /// <summary>Ergebnis des Auswertens der Befehlszeile (siehe <see cref="CommandLineParser.Parse"/>).</summary>
    public sealed class CommandLineOptions
    {
        public CommandKind Command { get; init; }
        /// <summary>Quelldateien in der angegebenen Reihenfolge (Pfade, wie eingegeben).</summary>
        public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
        /// <summary>`-m`: Ausführungsmodus; null = der im Skript (`#debug`/`#performance`) bzw. Release.</summary>
        public VmExecutionMode? Mode { get; init; }
        /// <summary>`-f`: Genauigkeit von `float` (32 oder 64); null = der im Skript (`#floatwidth`) bzw. 64.</summary>
        public int? FloatWidth { get; init; }

        /// <summary>`-D name` (repeatable): extra symbols for `#if`.</summary>
        public IReadOnlyList<string> Defines { get; init; } = Array.Empty<string>();
        /// <summary>`-t`: Zielprofil von `native` (siehe TargetProfile); null = der Rechner, auf dem der Compiler läuft.</summary>
        public TargetProfile? Target { get; init; }
        /// <summary>`-o`: Ausgabedatei von `build` (bzw. die C++-Datei von `native`).</summary>
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
        /// <summary>Gesetzt, wenn die Befehlszeile ungültig ist (Meldung für den Nutzer).</summary>
        public string? Error { get; init; }
    }

    /// <summary>
    /// Befehlszeile des Compilers:
    ///
    ///   fire.Compiler run   datei1 [datei2 ...] [-m DEBUG|RELEASE|PERFORMANCE]
    ///   fire.Compiler build datei1 [datei2 ...] [-o ziel.exe] [-m DEBUG|RELEASE|PERFORMANCE]
    ///
    /// `run` kompiliert und führt die Dateien (in dieser Reihenfolge zu EINEM Programm verbunden) sofort aus, `build`
    /// erzeugt daraus eine eigenständige Datei (Vorgabe `out.exe`). Dateinamen ohne Leerzeichen brauchen keine
    /// Anführungszeichen; mit Leerzeichen setzt die Shell sie wie üblich in Anführungszeichen - die Anführungszeichen
    /// selbst kommen nie im Argument an. Falls doch (z.B. durch eine Shell, die sie durchreicht), werden umschließende
    /// Anführungszeichen entfernt. Optionen dürfen an beliebiger Stelle stehen; `-m` ist ohne Beachtung der Groß-/
    /// Kleinschreibung (auch `-m=DEBUG`, `--mode DEBUG`), ebenso `-o`/`--out`.
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
            string? targetName = null, engine = null, toolchainName = null, configFile = null;
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
                OutputGiven = output != null,
                OutputFile = output ?? (command == CommandKind.Native ? DefaultNativeOutputFile : DefaultOutputFile),
            };
        }

        private static bool IsHelp(string arg) =>
            arg is "-h" or "--help" or "/?" or "help" or "-?";

        private static CommandLineOptions Fail(CommandKind command, string message) =>
            new() { Command = command, Error = message };

        /// <summary>Erkennt `-m`/`--mode` (Wert im nächsten Argument) und `-m=X`/`--mode=X` (Wert inline).</summary>
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

        /// <summary>Entfernt umschließende Anführungszeichen (`"a b.script"` -> `a b.script`).</summary>
        private static string Unquote(string text)
        {
            text = text.Trim();
            if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
                return text.Substring(1, text.Length - 2);
            return text;
        }
    }

    /// <summary>Führt die Befehle der Befehlszeile aus (siehe <see cref="CommandLineParser"/>).</summary>
    public static class CommandLineRunner
    {
        public const int ExitOk = 0;
        /// <summary>Kompilier- oder Laufzeitfehler des Skripts.</summary>
        public const int ExitScriptError = 1;
        /// <summary>Ungültige Befehlszeile oder fehlende Datei.</summary>
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

        private static int Execute(CommandLineOptions options, List<string> sources, TextWriter stderr)
        {
            RuntimeSession session;
            try
            {
                // print() geht direkt auf die Konsole, IO.Stdio ebenfalls (Vorgabe von RuntimeSession.Build).
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

            // `terminate(wert)`: ein Ganzzahlwert ist der Exitcode des Prozesses.
            var exit = VM.ExitValue;
            return exit.Kind == ValueKind.Int ? (int)exit.AsInt() : ExitOk;
        }

        /// <summary>Fehler, die das Skript selbst verursacht (Parser, Resolver, Compiler, Präprozessor) - alles
        /// andere ist ein Fehler im Werkzeug und soll mit seinem Stacktrace sichtbar bleiben.</summary>
        internal static bool IsCompileError(Exception ex) =>
            ex is ParseException or ResolverException or CompilerException
                or NotSupportedException or PreprocessorException;
    }
}
