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
        /// <summary>`-t`: Zielprofil von `native` (siehe TargetProfile); null = der Rechner, auf dem der Compiler läuft.</summary>
        public TargetProfile? Target { get; init; }
        /// <summary>`-o`: Ausgabedatei von `build` (bzw. die C++-Datei von `native`).</summary>
        public string OutputFile { get; init; } = CommandLineParser.DefaultOutputFile;
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
              fire.Compiler build <file>... [-o <target.exe>] [-m DEBUG|RELEASE|PERFORMANCE] [-f 32|64]
              fire.Compiler native <file>... [-o <target.cpp>] [-t <target>] [-f 32|64]

            run     compiles the files into one program and runs it.
            build   turns them into a self-contained executable (default: out.exe).
            -m      Execution mode (default: whatever the script sets with #debug/#performance, otherwise RELEASE).
            native  translates the files into C++ (written next to fire_rt.hpp, the runtime it includes; default: out.cpp)
                    - an experimental ahead-of-time backend, see docs/NATIVE_BACKEND.md.
            -t      Target of native: windows, linux, macos or esp32 (default: this machine). The target decides the default
                    precision of float, which libraries can be imported and how the program starts.
            -f      Precision of float in bits: 32 or 64 (default: whatever the script sets with #floatwidth, otherwise 64).
            -o      Name of the file produced by build or native.

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
            string? output = null;

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
                    if (command != CommandKind.Native) return Fail(command, "-t is only available with native.");
                    string? value = targetInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (value == null) return Fail(command, "-t must be followed by the target name.");
                    if (!TargetProfile.TryGet(Unquote(value), out var found))
                        return Fail(command, $"Unknown target '{value}' (allowed: {string.Join(", ", TargetProfile.All.Select(p => p.Name))}).");
                    target = found;
                }
                else if (TryOption(arg, "-f", "--float", out var floatInline))
                {
                    string? value = floatInline ?? (i + 1 < args.Count ? args[++i] : null);
                    if (value == null) return Fail(command, "-f must be followed by the float precision (32 or 64).");
                    if (Unquote(value) is not ("32" or "64"))
                        return Fail(command, $"Unknown float precision '{value}' (allowed: 32, 64).");
                    floatWidth = int.Parse(Unquote(value));
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
                Target = target,
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
            try
            {
                new Linker().CompileAndLink(sources, null, options.OutputFile, options.Mode, options.FloatWidth);
            }
            catch (Exception ex) when (IsCompileError(ex))
            {
                stderr.WriteLine(CompileErrors.Describe(ex));
                return ExitScriptError;
            }
            stdout.WriteLine($"{Path.GetFullPath(options.OutputFile)} ({new FileInfo(options.OutputFile).Length} bytes)");
            return ExitOk;
        }

        private static int Native(CommandLineOptions options, List<string> sources, TextWriter stdout, TextWriter stderr)
        {
            try
            {
                var target = options.Target ?? TargetProfile.Host;
                var linked = new Linker().CompileAndLink(sources, null, null, options.Mode, options.FloatWidth, target);
                string cpp = fire.Native.CppGenerator.Generate(linked, target);
                string full = Path.GetFullPath(options.OutputFile);
                File.WriteAllText(full, cpp);
                fire.Native.NativeRuntimeFiles.WriteTo(Path.GetDirectoryName(full)!);
                stdout.WriteLine(target.IsEmbedded
                    ? $"{full} ({cpp.Length} characters) for {target.Name} - add it and fire_rt.hpp to a component of your project (the entry point is app_main)"
                    : $"{full} ({cpp.Length} characters) - compile with: c++ -std=c++17 -O2 \"{full}\" -o program");
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
                }, floatWidth: options.FloatWidth);
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
        private static bool IsCompileError(Exception ex) =>
            ex is ParseException or ResolverException or CompilerException
                or NotSupportedException or PreprocessorException;
    }
}
