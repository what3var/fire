using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace fire.Compiler
{
    public enum DiagnosticSeverity
    {
        Error,
        Warning,
    }

    /// <summary>A message of the compiler about the code: an error (with a code of the <see cref="ErrorCatalog"/>, `E3012`) or a warning (`warn 123: "text"` in the code: `W123`). `File` is null
    /// when the compiler does not know it (an error of the resolver or the bytecode compiler in a project of several files; the file of a single script is the script itself).</summary>
    public sealed record Diagnostic(int Line, string Message, DiagnosticSeverity Severity = DiagnosticSeverity.Error, string Code = "", string? File = null, int Column = 0)
    {
        public bool IsError => Severity == DiagnosticSeverity.Error;
        public bool IsWarning => Severity == DiagnosticSeverity.Warning;

        public override string ToString() => $"Line {Line}: {Message}";

        /// <summary>The form of a build log and of the command line: `file(12): error E3012: message`.</summary>
        public string ToLogLine()
        {
            string kind = IsError ? "error" : "warning";
            string code = Code.Length > 0 ? " " + Code : "";
            if (File == null) return $"{kind}{code}: {Message}" + (Line > 0 ? $" (line {Line})" : "");
            return $"{File}{(Line > 0 ? $"({Line})" : "")}: {kind}{code}: {Message}";
        }
    }

    /// <summary>Collects the warnings that the parser meets while it reads the code (`warn 123: "text"`): a warning is no code - nothing of it reaches the program -, it tells the developer about a
    /// problem or about something that is going to be obsolete. The caller of a compile wraps it in <see cref="Begin"/>; without a collector the warnings are dropped.</summary>
    public sealed class DiagnosticCollector
    {
        [ThreadStatic] private static DiagnosticCollector? _current;

        private readonly List<Diagnostic> _items = new();
        private readonly HashSet<Diagnostic> _seen = new();
        private readonly List<DeclaredWarning> _declared = new();

        public IReadOnlyList<Diagnostic> Items => _items;

        /// <summary>The warnings that stand in classes and members (`warn` in the body of a class, a method, a constructor, a property): they are not reported where they are written but
        /// where the class or the member is used (the resolver does that, see Resolver.ReportUse).</summary>
        public IReadOnlyList<DeclaredWarning> Declared => _declared;

        public void Declare(DeclaredWarning warning) => _declared.Add(warning);

        public static DiagnosticCollector? Current => _current;

        /// <summary>Called for every diagnostic as it is added (the command line prints warnings at once, while the program is still compiled).</summary>
        public Action<Diagnostic>? OnReport { get; init; }

        public void Add(Diagnostic diagnostic)
        {
            if (!_seen.Add(diagnostic)) return;   // (the same message for the same place once, also when a stage of the compiler runs twice)
            _items.Add(diagnostic);
            OnReport?.Invoke(diagnostic);
        }

        /// <summary>Makes this the collector of the calling thread until the result is disposed (a collector that was active before comes back).</summary>
        public IDisposable Begin()
        {
            var previous = _current;
            _current = this;
            return new Scope(previous);
        }

        /// <summary>The warnings of the thread's collector (if there is one).</summary>
        public static void Warn(int id, string message, int line, string? file) =>
            _current?.Add(new Diagnostic(line, message, DiagnosticSeverity.Warning, WarningCode(id), file));

        public static string WarningCode(int id) => "W" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

        private sealed class Scope : IDisposable
        {
            private readonly DiagnosticCollector? _previous;
            public Scope(DiagnosticCollector? previous) => _previous = previous;
            public void Dispose() => _current = _previous;
        }
    }

    /// <summary>What a declared warning (`warn` inside a class) belongs to.</summary>
    public enum WarnTarget
    {
        /// <summary>The class itself (a `warn` in the class body, outside of its members): reported at `new` and at static access.</summary>
        Class,
        /// <summary>A constructor: reported at `new` with that number of arguments.</summary>
        Constructor,
        /// <summary>A method: reported at calls of that name with that number of arguments.</summary>
        Method,
        /// <summary>A property or a field: reported at every access of that name.</summary>
        Member,
    }

    /// <summary>A warning that the developer wrote inside a class (`warn 204: "Flush is obsolete"` in `Cache.Flush`): reported where the class or the member is USED, once per place.</summary>
    public sealed record DeclaredWarning(string Class, WarnTarget Target, string? Name, int MinArity, int MaxArity, bool IsStatic, int Id, string Text, int Line, string? File);

    /// <summary>The part of the compiler that a message comes from; it decides the thousands of the code (1 lexer, 2 preprocessor, 3 parser, 4 resolver, 5 bytecode compiler, 6 linker, 7 native build).</summary>
    public enum ErrorArea
    {
        Lexer,
        Preprocessor,
        Parser,
        Resolver,
        Compiler,
        Linker,
        Native,
    }

    /// <summary>The codes of the errors of the compiler (docs/ERRORS.md): every message has one, `E3012` - the letter, the area (the thousands) and a number inside the area. The codes are
    /// stable: a code names a message even after its wording is improved, so that a search for it, a note in a bug report or a link in the docs keeps working. A message that has no entry of its
    /// own in the catalogue (a new one) has the general code of its area (`E3000` for the parser, ...).</summary>
    public static partial class ErrorCatalog
    {
        /// <summary>The native generator cannot translate something (see NativeNotSupportedException).</summary>
        public const string NativeUnsupported = "E7001";
        /// <summary>The C++ compiler of a native build reported errors (its own messages are in the build log).</summary>
        public const string NativeToolchain = "E7002";
        /// <summary>The configuration of the native build (fire.native.json, target, toolchain) is not usable.</summary>
        public const string NativeConfiguration = "E7003";

        private sealed record Compiled(string Code, ErrorArea Area, Regex Regex, int LiteralLength, string Template);

        private static readonly Lazy<Compiled[]> Compiled_ = new(() =>
        {
            var list = new List<Compiled>();
            foreach (var (code, area, template) in Entries)
            {
                var parts = template.Split("{*}");
                string pattern = "^" + string.Join(".+?", parts.Select(Regex.Escape)) + "$";
                list.Add(new Compiled(code, area, new Regex(pattern, RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant), parts.Sum(p => p.Length), template));
            }
            // the most specific first (the one with the most fixed text): "Unknown type '{*}'" before a shorter one that would also match
            return list.OrderByDescending(c => c.LiteralLength).ToArray();
        });

        /// <summary>The code of a message of the area (the text without position and file); the general code of the area when the catalogue does not know the message.</summary>
        public static string CodeOf(ErrorArea area, string rawMessage)
        {
            foreach (var entry in Compiled_.Value)
                if (entry.Area == area && entry.Regex.IsMatch(rawMessage)) return entry.Code;
            return GeneralCode(area);
        }

        /// <summary>The code for everything of an area that has none of its own.</summary>
        public static string GeneralCode(ErrorArea area) => area switch
        {
            ErrorArea.Lexer => "E1000",
            ErrorArea.Preprocessor => "E2000",
            ErrorArea.Parser => "E3000",
            ErrorArea.Resolver => "E4000",
            ErrorArea.Compiler => "E5000",
            ErrorArea.Linker => "E6000",
            _ => "E7000",
        };

        /// <summary>All codes with their area and the text of the message (`{*}` where it varies), in the order of the catalogue (the general ones first).</summary>
        public static IEnumerable<(string Code, ErrorArea Area, string Template)> All()
        {
            foreach (var area in Enum.GetValues<ErrorArea>())
                yield return (GeneralCode(area), area, GeneralText(area));
            yield return (NativeUnsupported, ErrorArea.Native, "The native generator cannot translate this program");
            yield return (NativeToolchain, ErrorArea.Native, "The C++ compiler of the native build reported errors");
            yield return (NativeConfiguration, ErrorArea.Native, "The configuration of the native build cannot be used");
            foreach (var entry in Entries) yield return entry;
        }

        private static string GeneralText(ErrorArea area) => area switch
        {
            ErrorArea.Lexer => "Another error in the text of the program (characters, literals, comments)",
            ErrorArea.Preprocessor => "Another error in a directive (#include, #if, #import, ...)",
            ErrorArea.Parser => "Another syntax error",
            ErrorArea.Resolver => "Another error in names, types or declarations",
            ErrorArea.Compiler => "Another error while the bytecode is made",
            ErrorArea.Linker => "Another error while the program and its libraries are put together",
            _ => "Another error of the native build",
        };

        /// <summary>The text of a code for a person: the message with `...` where it varies; null for a code that is not in the catalogue (also a warning, `W123`).</summary>
        public static string? Describe(string code)
        {
            foreach (var (c, _, template) in All())
                if (c == code) return template.Replace("{*}", "...");
            return null;
        }
    }

    /// <summary>The errors of an exception of the compiler as diagnostics (with codes), whichever part of the compiler it comes from.</summary>
    public static class CompileDiagnostics
    {
        private static readonly Regex LineInText = new(@"\(line (?<n>\d+)\)", RegexOptions.Compiled);

        /// <summary>`file`: the file that an error without one of its own is in (the script that was compiled).</summary>
        public static IReadOnlyList<Diagnostic> Of(Exception ex, string? file = null)
        {
            switch (ex)
            {
                case ParseException p:
                    return new[] { new Diagnostic(p.Line, p.RawMessage, DiagnosticSeverity.Error, p.Code, p.FileName ?? file, p.Column) };
                case LexException l:
                    return new[] { new Diagnostic(l.Line, l.RawMessage, DiagnosticSeverity.Error, l.Code, file, l.Column) };
                case ResolverException r:
                    return r.Errors.Select(e => new Diagnostic(e.Line, e.RawMessage, DiagnosticSeverity.Error, e.Code, file)).ToList();
                case CompilerException c:
                    return c.Errors.Select(e => new Diagnostic(e.Line, e.RawMessage, DiagnosticSeverity.Error, e.Code, file)).ToList();
                case PreprocessorException pre:
                {
                    var m = LineInText.Match(pre.Message);
                    return new[] { new Diagnostic(m.Success ? int.Parse(m.Groups["n"].Value) : 1, pre.Message, DiagnosticSeverity.Error, pre.Code, file) };
                }
                case fire.Native.NativeNotSupportedException native:
                    return new[] { new Diagnostic(0, native.Message, DiagnosticSeverity.Error, ErrorCatalog.NativeUnsupported, file) };
                default:
                    // the linker and others: an error without a position of its own
                    return new[] { new Diagnostic(1, ex.Message, DiagnosticSeverity.Error, ErrorCatalog.CodeOf(ErrorArea.Linker, ex.Message), file) };
            }
        }
    }
}
