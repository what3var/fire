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
    public enum CommandKind { Run, Build, Help }

    /// <summary>Ergebnis des Auswertens der Befehlszeile (siehe <see cref="CommandLineParser.Parse"/>).</summary>
    public sealed class CommandLineOptions
    {
        public CommandKind Command { get; init; }
        /// <summary>Quelldateien in der angegebenen Reihenfolge (Pfade, wie eingegeben).</summary>
        public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
        /// <summary>`-m`: Ausführungsmodus; null = der im Skript (`#debug`/`#performance`) bzw. Release.</summary>
        public VmExecutionMode? Mode { get; init; }
        /// <summary>`-o`: Ausgabedatei von `build`.</summary>
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

        public static string Usage =>
            """
            Usage:
              fire.Compiler run   <file>... [-m DEBUG|RELEASE|PERFORMANCE]
              fire.Compiler build <file>... [-o <target.exe>] [-m DEBUG|RELEASE|PERFORMANCE]

            run     compiles the files into one program and runs it.
            build   turns them into a self-contained executable (default: out.exe).
            -m      Execution mode (default: whatever the script sets with #debug/#performance, otherwise RELEASE).
            -o      Name of the file produced by build.

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
                default:
                    return Fail(CommandKind.Help, $"Unknown command '{args[0]}' (expected: run or build).");
            }

            var files = new List<string>();
            VmExecutionMode? mode = null;
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
                else if (TryOption(arg, "-o", "--out", out var outInline))
                {
                    if (command != CommandKind.Build) return Fail(command, "-o is only available with build.");
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
                OutputFile = output ?? DefaultOutputFile,
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

            return options.Command == CommandKind.Build
                ? Build(options, sources, stdout, stderr)
                : Execute(options, sources, stderr);
        }

        private static int Build(CommandLineOptions options, List<string> sources, TextWriter stdout, TextWriter stderr)
        {
            try
            {
                new Linker().CompileAndLink(sources, null, options.OutputFile, options.Mode);
            }
            catch (Exception ex) when (IsCompileError(ex))
            {
                stderr.WriteLine(CompileErrors.Describe(ex));
                return ExitScriptError;
            }
            stdout.WriteLine($"{Path.GetFullPath(options.OutputFile)} ({new FileInfo(options.OutputFile).Length} bytes)");
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
                });
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
