using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using fire.Lexing;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>Context that a preprocessor directive implementation
    /// gets when it is called (see DirectiveHandler/DirectiveRegistry) -
    /// allows it to recursively smuggle in further files (ProcessFile)
    /// AND to access the GLOBALLY shared "already included" set.</summary>
    /// <summary>Result of Preprocessor.Process: the finished preprocessed
    /// source text (all `#include`s inserted, all recognised directive
    /// lines removed) AND the namespace names collected in it via
    /// `#using Name` (SPEC "Namespaces") - from now on a pure
    /// preprocessor matter, no longer a task of the parser (see
    /// Parser._usingNamespaces/ParseMultiple): `#using` lines are already
    /// recognised HERE and removed from the text (like any other recognised
    /// directive), the lexer/parser never sees them. A `#using` line
    /// INSIDE a file inserted via `#include` ends up in
    /// THE SAME `Usings` as the including file - consistent with the
    /// "pure text splicing" semantics of `#include` (SPEC 8.1.5): after
    /// insertion it can no longer be distinguished whether a line originally
    /// came from the root file or an inserted file.</summary>
    public sealed record ProcessedSource(string Source, IReadOnlyList<string> Usings, string? Name = null);

    /// <summary>The files embedded in a program while it is preprocessed: the same file is stored once (the id of a resource is its index).</summary>
    public sealed class ResourceTable
    {
        private readonly Dictionary<string, int> _byFullPath = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        public List<fire.Runtime.ResourceEntry> Entries { get; } = new();

        /// <summary>Reads the file and returns its id; `name` is the path as it was written in the source.</summary>
        public int Add(string name, string fullPath)
        {
            if (_byFullPath.TryGetValue(fullPath, out int id)) return id;
            id = Entries.Count;
            Entries.Add(new fire.Runtime.ResourceEntry { Name = name, Data = File.ReadAllBytes(fullPath) });
            _byFullPath[fullPath] = id;
            return id;
        }
    }

    public sealed class DirectiveContext
    {
        /// <summary>Directory relative to which path arguments of THIS
        /// directive line are to be resolved (the directory of the file
        /// currently being processed).</summary>
        public string BasePath { get; }

        /// <summary>GLOBALLY shared set of already inserted (absolute)
        /// file paths - deliberately NOT created anew per Preprocessor.Process() call,
        /// but provided by the CALLER and thus
        /// SHAREABLE between several Process() calls: if a
        /// host processes several root files together (e.g. prelude + user
        /// script, see Runtime.RuntimeSession.Build/Parser.ParseMultiple),
        /// a COMMON
        /// instance ensures that a file (transitively) included from BOTH
        /// sides ends up only ONCE in total in the
        /// combined output - a global code composition,
        /// instead of separate insertion trees, independent per root file,
        /// each with its own "already seen" memory.</summary>
        public HashSet<string> AlreadyIncluded { get; }

        private readonly Preprocessor _owner;

        internal DirectiveContext(string basePath, HashSet<string> alreadyIncluded, Preprocessor owner)
        {
            BasePath = basePath;
            AlreadyIncluded = alreadyIncluded;
            _owner = owner;
        }

        /// <summary>Reads `fullPath` and processes its content RECURSIVELY
        /// (incl. any own directives contained in it, with the same
        /// directory of `fullPath` as the new BasePath for relative
        /// paths in it) - for directive implementations like "include"
        /// that want to smuggle in foreign file content. Uses the same
        /// `AlreadyIncluded` set and directive registry as the outer
        /// call; recognises circular inclusions (A includes B,
        /// B includes A again) and aborts with a clear
        /// error instead of recursing endlessly.</summary>
        public string ProcessFile(string fullPath) => _owner.ProcessFileRecursive(fullPath, AlreadyIncluded);
    }

    /// <summary>A preprocessor directive implementation: gets the
    /// call context, the argument values already checked against their declared count
    /// (see DirectiveRegistry.Register) and the line number
    /// of the directive line. Returns the text that replaces the directive line in
    /// the preprocessed output (empty/null for "insert
    /// nothing").</summary>
    public delegate string? DirectiveHandler(DirectiveContext ctx, IReadOnlyList<Value> args, int line);

    public sealed class DirectiveDefinition
    {
        public string Name { get; }
        public int ParamCount { get; }
        public DirectiveHandler Handler { get; }

        public DirectiveDefinition(string name, int paramCount, DirectiveHandler handler)
        {
            Name = name;
            ParamCount = paramCount;
            Handler = handler;
        }
    }

    /// <summary>
    /// Registry of freely definable preprocessor directives (SPEC 8.1.5):
    /// host C# code registers via <see cref="Register"/> a new
    /// `#name value1, value2, ...` directive with a FIXED declared
    /// expected parameter count (error on deviation, see Preprocessor.
    /// ParseDirectiveArgs) and a handler that receives the parsed
    /// values. Every parameter is a pure LITERAL (string/number with
    /// optional unit suffix/character/bool/undefined) - preprocessor
    /// directives run BEFORE the lexer/parser, at that point there are
    /// no variables/expressions yet, therefore deliberately no full
    /// expression grammar.
    ///
    /// `#include` is from now on ONLY the built-in default registration
    /// of this mechanism (see <see cref="CreateDefault"/>), no
    /// special treatment any more in the rest of the preprocessor code - every further,
    /// self-registered directive works by the same pattern.
    /// `#extern "libName"`/`#noshadow`, on the other hand, deliberately stay OUTSIDE
    /// this registry (see Preprocessor class documentation: unknown `#...`
    /// lines are passed through unchanged) - they have a STICKY meaning,
    /// acting across several following statements,
    /// for the PARSER itself (which library following `extern`
    /// declarations link, whether globals shadowing applies), not a pure
    /// "replace this one line with text" semantics like `#include` - a
    /// conversion there would be possible, but a different kind of intervention (the
    /// parser would then have to query state from the preprocessor instead of
    /// managing both itself as before) and is therefore deliberately not part of
    /// this change.
    /// </summary>
    public sealed class DirectiveRegistry
    {
        private readonly Dictionary<string, DirectiveDefinition?> _directives =
            new(StringComparer.OrdinalIgnoreCase);

        public void Annouce(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("The directive name must not be empty.", nameof(name));
            _directives[name] = null;
        }

        /// <summary>The symbols of `#if` (see <see cref="ConditionalSymbols"/>), case-insensitive. Shared by everything processed with this registry, so a
        /// `#define` of one file is seen by the next. Empty at first; the linker and the runtime session fill it from the target.</summary>
        public HashSet<string> Symbols { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The files of `new Resource("path")` (docs/RESOURCES.md): where the preprocessor puts them. Without a table (live diagnostics) the text stays as it is.</summary>
        public ResourceTable? Resources { get; set; }

        public DirectiveRegistry()
        {
            Annouce("import");
            Annouce("include");
            Annouce("name");
            Annouce("codename");
            Annouce("author");
            Annouce("comments");
            Annouce("description");
            Annouce("icon");
            Annouce("debug");
            Annouce("performance");
            Annouce("floatwidth");
            Annouce("noconsole");
            Annouce("version");
            Annouce("fileversion");
        }

        /// <summary>Registers (or replaces) the directive `name` - a
        /// call `#name ...` with a DIFFERENT number of arguments than
        /// `paramCount` is afterwards a clear compile error (see
        /// Preprocessor.ParseDirectiveArgs).</summary>
        public void Register(string name, int paramCount, DirectiveHandler handler)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("The directive name must not be empty.", nameof(name));
            if (paramCount < 0)
                throw new ArgumentOutOfRangeException(nameof(paramCount));
            _directives[name] = new DirectiveDefinition(name, paramCount, handler ?? throw new ArgumentNullException(nameof(handler)));
        }

        public bool TryGet(string name, out DirectiveDefinition definition) =>
            _directives.TryGetValue(name, out definition!);

        /// <summary>`#include "path"` as the only built-in directive (1
        /// parameter: the path as a string) - "include once" GLOBALLY across the
        /// whole composition (see DirectiveContext.AlreadyIncluded documentation),
        /// not like C's raw multiple inclusion. A host that wants/needs no
        /// `#include` at all can use an empty
        /// `new DirectiveRegistry()` instead.</summary>
        public static DirectiveRegistry CreateDefault()
        {
            var registry = new DirectiveRegistry();
            registry.Register("include", 1, (ctx, args, line) =>
            {
                if (args[0].Kind != ValueKind.String)
                    throw new PreprocessorException(
                        $"'#include' expects a string as the path, not {args[0].Kind} (line {line}).");

                string relativePath = args[0].AsString();
                string fullPath = Path.GetFullPath(Path.Combine(ctx.BasePath, relativePath));

                if (!ctx.AlreadyIncluded.Add(fullPath))
                    return ""; // already inserted (anywhere in the WHOLE composition) - skip

                if (!File.Exists(fullPath))
                    throw new PreprocessorException(
                        $"'#include \"{relativePath}\"' - file not found: '{fullPath}' (line {line}).");

                return ctx.ProcessFile(fullPath);
            });
            return registry;
        }
    }

    /// <summary>
    /// Pure text preprocessing BEFORE the lexer: lines of the form
    /// `#name value1, value2, ...` are processed via a <see cref="DirectiveRegistry"/>
    /// (default: only `#include`, see DirectiveRegistry.
    /// CreateDefault) - deliberately kept simple (no lexer/parser
    /// pass of its own needed for the REST of the file, only for the argument list
    /// of ONE directive line). A `#...` line whose name is NOT in the
    /// registry (e.g. `#extern "libName"`, `#noshadow` - see
    /// DirectiveRegistry class documentation) is passed through UNCHANGED - the
    /// preprocessor only interferes with directives that it actually
    /// knows.
    ///
    /// Line numbers in error messages become
    /// imprecise for inserted text (as with any simple text preprocessor, incl. C without
    /// `#line`) - a known, accepted limit of this simple
    /// implementation.
    /// </summary>
    public sealed class Preprocessor
    {
        private static readonly Regex DirectiveLine =
            new(@"^\s*#([A-Za-z_][A-Za-z0-9_]*)(?:[ \t]+(.*))?\s*$", RegexOptions.Compiled);

        internal static Regex DirectiveLineRegex => DirectiveLine;

        /// <summary>A valid (possibly dotted) namespace name after
        /// `#using` - the same name grammar as Parser.ParseDottedName
        /// (`A` or `A.B.C`), but here as a pure text check instead of via
        /// the real lexer/parser (see class documentation: deliberately simple).</summary>
        private static readonly Regex UsingName =
            new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

        private readonly DirectiveRegistry _registry;
        private readonly List<string> _includeChain = new();

        /// <summary>All namespace names collected via `#using Name` - ONE
        /// common list for the WHOLE Process() call incl. all
        /// files inserted recursively via `#include` (see ProcessedSource
        /// documentation for the reasoning), hence an instance field instead of a
        /// return value of ProcessInner (which runs recursively for `#include`).</summary>
        private readonly List<string> _usings = new();

        private Preprocessor(DirectiveRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>Processes ONE source text on its own, with a
        /// FRESH "already included" set - for the simple case
        /// that only ONE root file is compiled. `registry`: default
        /// `DirectiveRegistry.CreateDefault()` (only `#include`).</summary>
        public static ProcessedSource Process(string source, string basePath, DirectiveRegistry? registry = null) =>
            Process(source, basePath, new HashSet<string>(StringComparer.OrdinalIgnoreCase), registry);

        /// <summary>Like Process(source, basePath), but with an "already included" set PROVIDED BY THE
        /// CALLER (and thus SHAREABLE between several Process()
        /// calls) - see
        /// DirectiveContext.AlreadyIncluded documentation for the reason (global
        /// instead of per-root-file composition, e.g. prelude + several
        /// user files together, see Parser.ParseMultiple).</summary>
        public static ProcessedSource Process(
            string source, string basePath, HashSet<string> alreadyIncluded, DirectiveRegistry? registry = null)
        {
            var pre = new Preprocessor(registry ?? DirectiveRegistry.CreateDefault());
            string result = pre.ProcessInner(source, basePath, alreadyIncluded);
            return new ProcessedSource(result, pre._usings);
        }

        internal string ProcessFileRecursive(string fullPath, HashSet<string> alreadyIncluded)
        {
            if (_includeChain.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                throw new PreprocessorException(
                    $"Circular include of '{fullPath}' (chain: {string.Join(" -> ", _includeChain)} -> {fullPath}).");

            string includedSource;
            try
            {
                includedSource = File.ReadAllText(fullPath);
            }
            catch (Exception ex)
            {
                throw new PreprocessorException($"'{fullPath}' could not be read: {ex.Message}", ex);
            }

            // a markup file of a user interface (docs/UI_MARKUP.md) is included as the script generated from it
            if (string.Equals(Path.GetExtension(fullPath), ".fxml", StringComparison.OrdinalIgnoreCase))
            {
                try { includedSource = fire.UI.Markup.FireUiGenerator.Generate(fire.UI.Markup.MarkupParser.Parse(includedSource), fullPath); }
                catch (fire.UI.Markup.MarkupException ex) { throw new PreprocessorException($"'{Path.GetFileName(fullPath)}': {ex.Message}", ex); }
            }

            _includeChain.Add(fullPath);
            string? includedDir = Path.GetDirectoryName(fullPath);
            string result = ProcessInner(includedSource, includedDir ?? ".", alreadyIncluded);
            _includeChain.RemoveAt(_includeChain.Count - 1);
            return result;
        }

        private string ProcessInner(string source, string basePath, HashSet<string> alreadyIncluded)
        {
            var sb = new StringBuilder();
            var lines = source.Split('\n');
            var conditional = new ConditionalState(); // `#if` nesting of this file; the symbols are those of the registry (shared by all files)

            for (int lineNo = 0; lineNo < lines.Length; lineNo++)
            {
                var line = lines[lineNo].TrimEnd('\r');
                var match = DirectiveLine.Match(line);
                if (!match.Success)
                {
                    // a branch that is not taken: an empty line, the numbering stays
                    sb.Append(conditional.Active ? ResolveResources(line, basePath, lineNo + 1) : "").Append('\n');
                    continue;
                }

                string name = match.Groups[1].Value;

                if (conditional.TryHandle(name, match.Groups[2].Success ? match.Groups[2].Value : "", lineNo + 1, _registry.Symbols))
                {
                    sb.Append('\n');
                    continue;
                }
                if (!conditional.Active)
                {
                    sb.Append('\n');
                    continue;
                }

                if (string.Equals(name, "using", StringComparison.OrdinalIgnoreCase))
                {
                    string usingArg = (match.Groups[2].Success ? match.Groups[2].Value : "").Trim();
                    if (!UsingName.IsMatch(usingArg))
                        throw new PreprocessorException(
                            $"'#using' expects a (possibly dotted) namespace name, not '{usingArg}' (line {lineNo + 1}).");
                    _usings.Add(usingArg);
                    sb.Append('\n'); // Line "disappears" like any other recognised directive.
                    continue;
                }

                if (!_registry.TryGet(name, out var def))
                {
                    // Not registered with THIS preprocessor - e.g.
                    // '#extern "lib"' or '#noshadow', which the PARSER
                    // handles itself (see DirectiveRegistry class documentation) -
                    // pass through unchanged, NOT an error.
                    sb.Append(line).Append('\n');
                    continue;
                }

                if (def == null)
                {
                    sb.Append('\n'); // Line "disappears", line count is nevertheless preserved (see class docs).
                }
                else
                {
                    string argText = match.Groups[2].Success ? match.Groups[2].Value : "";
                    var args = ParseDirectiveArgs(argText, def, lineNo + 1);

                    var ctx = new DirectiveContext(basePath, alreadyIncluded, this);
                    string? replacement = def.Handler(ctx, args, lineNo + 1);

                    if (!string.IsNullOrEmpty(replacement))
                    {
                        sb.Append(replacement);
                        if (!replacement.EndsWith("\n")) sb.Append('\n');
                    }
                    else
                    {
                        sb.Append('\n'); // Line "disappears", line count is nevertheless preserved (see class docs).
                    }
                }
            }

            conditional.EnsureClosed();
            return sb.ToString();
        }

        private static readonly Regex ResourceCall = new(@"\bnew\s+Resource\s*\(\s*""((?:[^""\\]|\\.)*)""\s*\)", RegexOptions.Compiled);

        /// <summary>`new Resource("path")` in a line of code: the file (relative to the source file that mentions it) is embedded in the program and the path becomes the number of the
        /// resource - `new Resource(3)`. What stands in a comment (`//`) or a string stays as it is.</summary>
        private string ResolveResources(string line, string basePath, int lineNo)
        {
            if (_registry.Resources is not { } table || line.IndexOf("Resource", StringComparison.Ordinal) < 0) return line;
            return ResourceCall.Replace(line, m =>
            {
                if (!IsCode(line, m.Index)) return m.Value;
                string path = Regex.Unescape(m.Groups[1].Value);
                string full = Path.GetFullPath(path, basePath);
                if (!File.Exists(full))
                    throw new PreprocessorException($"Resource file not found: '{path}' ('{full}', line {lineNo}).");
                return $"new Resource({table.Add(path, full)})";
            });
        }

        /// <summary>Is the position in the line code - not in a string and not behind a `//`?</summary>
        private static bool IsCode(string line, int index)
        {
            bool inString = false;
            for (int i = 0; i < index; i++)
            {
                char c = line[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return false;
            }
            return !inString;
        }

        /// <summary>Reads the argument list of ONE directive line - tokenised
        /// via the REAL lexer (correct for strings/numbers with
        /// unit suffix/etc.), then read as a comma-separated list of pure
        /// literal values (see ReadDirectiveLiteral) - NO
        /// general expression grammar (see class documentation). Afterwards checks
        /// the actual against the declared parameter count.</summary>
        private static IReadOnlyList<Value> ParseDirectiveArgs(string argText, DirectiveDefinition def, int line)
        {
            if (def.ParamCount == 0)
            {
                if (!string.IsNullOrWhiteSpace(argText))
                    throw new PreprocessorException($"'#{def.Name}' expects no parameters (line {line}).");
                return Array.Empty<Value>();
            }

            List<Token> tokens;
            try
            {
                tokens = new Lexer(argText).Tokenize();
            }
            catch (LexException ex)
            {
                throw new PreprocessorException($"Invalid parameters for '#{def.Name}' (line {line}): {ex.Message}");
            }

            var values = new List<Value>();
            int i = 0;
            while (i < tokens.Count && tokens[i].Type != TokenType.Eof)
            {
                values.Add(ReadDirectiveLiteral(tokens, ref i, def.Name, line));
                if (i < tokens.Count && tokens[i].Type == TokenType.Comma)
                {
                    i++;
                    continue;
                }
                break;
            }

            if (i < tokens.Count && tokens[i].Type != TokenType.Eof)
                throw new PreprocessorException(
                    $"Unexpected token '{tokens[i].Lexeme}' in the parameters of '#{def.Name}' (line {line}).");

            if (values.Count != def.ParamCount)
                throw new PreprocessorException(
                    $"'#{def.Name}' expects {def.ParamCount} parameters, got {values.Count} (line {line}).");

            return values;
        }

        /// <summary>Reads ONE literal (string/int/float/char/bool/undefined,
        /// numbers with optional leading '-' and optional unit
        /// suffix like '74mm') from token index `i`, advancing `i` in the process.</summary>
        private static Value ReadDirectiveLiteral(List<Token> tokens, ref int i, string directiveName, int line)
        {
            var tok = tokens[i];
            switch (tok.Type)
            {
                case TokenType.StringLiteral:
                    i++;
                    return Value.MakeString((string)tok.LiteralValue!);

                case TokenType.IntLiteral:
                    i++;
                    return Value.MakeInt((long)tok.LiteralValue!, Unit.Parse(tok.UnitSuffix ?? ""));

                case TokenType.FloatLiteral:
                    i++;
                    return Value.MakeFloat((double)tok.LiteralValue!, Unit.Parse(tok.UnitSuffix ?? ""));

                case TokenType.CharLiteral:
                    i++;
                    return Value.MakeChar((char)tok.LiteralValue!);

                case TokenType.True:
                    i++;
                    return Value.MakeBool(true);

                case TokenType.False:
                    i++;
                    return Value.MakeBool(false);

                case TokenType.Undefined:
                    i++;
                    return Value.MakeUndefined();

                case TokenType.Minus:
                {
                    // Sign directly before a number ('-5', '-3.2mm') -
                    // directive arguments are pure literals, no
                    // general expression (see class documentation), therefore here
                    // as an explicit special case instead of via the full
                    // expression precedence chain of the normal parser.
                    i++;
                    if (i >= tokens.Count || (tokens[i].Type != TokenType.IntLiteral && tokens[i].Type != TokenType.FloatLiteral))
                        throw new PreprocessorException(
                            $"Expected a number after '-' in the parameters of '#{directiveName}' (line {line}).");
                    var numTok = tokens[i];
                    i++;
                    return numTok.Type == TokenType.IntLiteral
                        ? Value.MakeInt(-(long)numTok.LiteralValue!, Unit.Parse(numTok.UnitSuffix ?? ""))
                        : Value.MakeFloat(-(double)numTok.LiteralValue!, Unit.Parse(numTok.UnitSuffix ?? ""));
                }

                default:
                    throw new PreprocessorException(
                        $"Expected a literal value (string/number/char/bool/undefined) in the parameters of " +
                        $"'#{directiveName}', not '{tok.Lexeme}' (line {line}).");
            }
        }
    }

    public sealed class PreprocessorException : Exception
    {
        public PreprocessorException(string message) : base(message) { }
        public PreprocessorException(string message, Exception inner) : base(message, inner) { }
    }
}
