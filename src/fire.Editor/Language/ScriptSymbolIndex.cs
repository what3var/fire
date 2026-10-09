using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using fire.Compiler;
using fire.Lexing;
using fire.Standard;

namespace fire.Editor
{
    public enum MemberKind
    {
        Field,
        Method,
        Property,
        Constructor,
    }

    public enum NamespaceMemberKind
    {
        Namespace,
        Class,
        Interface,
        Enum,
    }

    /// <summary>An entry IN a namespace: `Name` simple, `FullName`
    /// fully qualified (key in Classes/EnumMembers/Namespaces).</summary>
    public sealed record NamespaceMember(string Name, NamespaceMemberKind Kind, string FullName);

    public enum MemberAccess
    {
        Public,
        Protected,
        Private,
    }

    public sealed class MemberInfo
    {
        public string Name { get; }
        public MemberKind Kind { get; }
        public int ParamCount { get; }

        /// <summary>1-based source line of the NAME token of this
        /// member (for "go to definition", see NavigationEngine)
        /// - 0 if unknown (should not occur
        /// during normal collection).</summary>
        public int DeclLine { get; }

        /// <summary>The declared type of a field/property or the
        /// return type of a method (class name or type keyword like `int`),
        /// `null` if none is given (dynamic typing - the
        /// normal case, see ScriptSymbolIndex.InferReturnType for
        /// methods). For `TypeIsArray` the ELEMENT type (`Foo items[]`).</summary>
        public string? TypeName { get; init; }
        public bool TypeIsArray { get; init; }

        public bool IsStatic { get; init; }
        public MemberAccess Access { get; init; } = MemberAccess.Public;

        /// <summary>The parameter list of a method as text (`int a, Foo b`),
        /// for display in completion.</summary>
        public string? Signature { get; init; }

        /// <summary>Name of the class that declares this member.</summary>
        public string Owner { get; internal set; } = string.Empty;

        // Where the token indices below come from (every index - also that of the
        // prelude - has its OWN token stream) and where the body of a
        // method lies (for return type derivation, see
        // ScriptSymbolIndex.InferReturnType). BodyStart = -1: no body.
        internal ScriptSymbolIndex? Source { get; set; }
        internal int BodyStart { get; init; } = -1;
        internal int BodyEnd { get; init; } = -1;

        /// <summary>The `///` documentation comment above the declaration, null if there is none.</summary>
        public DocComment? Documentation => DeclLine > 0 ? Source?.DocumentationAt(DeclLine) : null;

        public MemberInfo(string name, MemberKind kind, int paramCount, int declLine = 0)
        {
            Name = name;
            Kind = kind;
            ParamCount = paramCount;
            DeclLine = declLine;
        }
    }

    public sealed class ClassInfo
    {
        /// <summary>The FULLY QUALIFIED name (`Geometry.Circle`, without a
        /// namespace just `Circle`) - the key in
        /// <see cref="ScriptSymbolIndex.Classes"/>, as in the real compiler
        /// (see Parser.QualifyDeclName).</summary>
        public string Name { get; }

        /// <summary>The name without namespace (`Circle`).</summary>
        public string SimpleName { get; }

        /// <summary>The namespace in which the class is declared
        /// (`Geometry`, `A.B`), empty without a namespace.</summary>
        public string Namespace { get; }

        /// <summary>The namespaces against which type names in the declaration of this
        /// class (base classes, field/return types) are resolved: its
        /// own namespace first, then the `#using` names of its file (see
        /// TypeRef.ResolveBaseName).</summary>
        public IReadOnlyList<string> Context { get; init; } = System.Array.Empty<string>();

        /// <summary>The index whose token stream describes this class (for
        /// a prelude class that of the prelude).</summary>
        internal ScriptSymbolIndex? Source { get; set; }

        /// <summary>All names after the ':' in the class head (base class AND
        /// interfaces - which of them is the real base class is decided
        /// only by the resolver; for completion all are equally
        /// useful), in source order.</summary>
        public List<string> BaseNames { get; } = new();

        /// <summary>The FIRST name from <see cref="BaseNames"/> (rough
        /// approximation of the base class), null without a base.</summary>
        public string? BaseName
        {
            get => BaseNames.Count > 0 ? BaseNames[0] : null;
            set
            {
                if (value != null && !BaseNames.Contains(value))
                    BaseNames.Insert(0, value);
            }
        }

        public List<MemberInfo> Members { get; } = new();

        /// <summary>Token ranges (index of the opening '{', of the closing
        /// '}') of all bodies of this class in the token stream of
        /// <see cref="MemberInfo.Source"/> - several for `class extends X`
        /// (see ScriptSymbolIndex.InferFieldType).</summary>
        internal List<(int Start, int End)> BodyRanges { get; } = new();

        /// <summary>1-based source line of the class name token (for
        /// "go to definition", see NavigationEngine) - can be set
        /// afterwards if an extension (see ScriptSymbolIndex.
        /// HarvestClassExtension) stood before the real declaration in the document
        /// and initially only created a placeholder without a known line.
        /// For <see cref="IsFromPrelude"/> it refers to
        /// a line INSIDE the prelude source, NOT to the
        /// currently edited document.</summary>
        public int DeclLine { get; set; }

        /// <summary>The `///` documentation comment above the class declaration, null if there is none.</summary>
        public DocComment? Documentation => DeclLine > 0 ? Source?.DocumentationAt(DeclLine) : null;

        /// <summary>true if this class comes from the built-in
        /// standard library (see ScriptSymbolIndex.
        /// MergeInPrelude/PreludeIndex), NOT from the document
        /// being edited itself - "go to definition" must then show the
        /// prelude in a separate, read-only popup (see
        /// NavigationEngine/MainWindow.ShowPreludeSource) instead of
        /// scrolling in the main document to a line (that does not exist there at all).
        /// </summary>
        public bool IsFromPrelude => PreludeName != null;

        /// <summary>Which prelude the class comes from: <see cref="ScriptSymbolIndex.StandardPreludeName"/>
        /// for the standard library, otherwise the name of the extension (`graphics`, `time`, ...);
        /// null = is in the edited document itself.</summary>
        public string? PreludeName { get; set; }

        /// <summary>true for an `interface` (signatures only, never
        /// instantiable with `new`).</summary>
        public bool IsInterface { get; set; }

        public ClassInfo(string name, int declLine = 0)
        {
            Name = name;
            DeclLine = declLine;
            int dot = name.LastIndexOf('.');
            Namespace = dot < 0 ? string.Empty : name.Substring(0, dot);
            SimpleName = dot < 0 ? name : name.Substring(dot + 1);
        }
    }

    /// <summary>An `#include "path"` line in the document (see
    /// Parsing.Preprocessor - recognised purely TEXTUALLY, exactly the same regular
    /// expression as there, so that "what the editor recognises on click" and
    /// "what the real preprocessor actually inserts" can never drift
    /// apart).</summary>
    public sealed record IncludeDirective(string RelativePath, int Line);

    /// <summary>
    /// Best-effort symbol table for auto-completion in the editor - deliberately
    /// TOKEN-based (via the real lexer), NOT via the real parser:
    /// that very often fails while typing live exactly at the point where
    /// typing is currently happening (incomplete expressions), and then returns NOTHING
    /// AT ALL - tokenising, on the other hand, fails only for really broken
    /// literals (open string etc.), so it is considerably more
    /// robust for this purpose. For that it deliberately models only a PART of the real grammar
    /// (distinguishing field/method/property/constructor when collecting
    /// class members) and for the scope assignment (which
    /// local variable is visible at a cursor position) deliberately makes
    /// simplifying assumptions (see LocalsVisibleAt) - sufficient for
    /// completion SUGGESTIONS, but no replacement for a real
    /// resolution (that would need Resolver.Resolve on complete,
    /// valid source).
    /// </summary>
    public sealed partial class ScriptSymbolIndex
    {
        public Dictionary<string, ClassInfo> Classes { get; } = new();
        public Dictionary<string, List<string>> EnumMembers { get; } = new();

        /// <summary>1-based declaration line per enum name (for "go to
        /// definition", see NavigationEngine).</summary>
        public Dictionary<string, int> EnumDeclLines { get; } = new();

        /// <summary>Which prelude an enum comes from (keys like <see cref="EnumDeclLines"/>); missing for enums of the document.</summary>
        public Dictionary<string, string> EnumPreludes { get; } = new();

        /// <summary>Name of the standard library in <see cref="ClassInfo.PreludeName"/>.</summary>
        public const string StandardPreludeName = "standard";

        /// <summary>What the prelude described by THIS index is called (null = an ordinary document).</summary>
        public string? PreludeName { get; private set; }

        /// <summary>The source of the prelude `preludeName` (for display when jumping), null if unknown.</summary>
        public static string? PreludeSourceOf(string preludeName) =>
            preludeName == StandardPreludeName ? fire.Standard.Prelude.Source : ImportedPreludes.TrySourceFor(preludeName);

        /// <summary>Display name of a prelude for window titles.</summary>
        public static string PreludeTitleOf(string preludeName) =>
            preludeName == StandardPreludeName ? "Standard library (prelude)" : $"Prelude '{(preludeName.StartsWith("pkg:", StringComparison.Ordinal) ? preludeName.Substring(4) : preludeName)}'";

        /// <summary>All identifier names seen anywhere in the document
        /// (variables, parameters, fields, ...) - a fuzzy but robust
        /// fallback for general identifier completion when
        /// no more precise information is available.</summary>
        public HashSet<string> AllDeclaredNames { get; } = new();

        /// <summary>All `#include "path"` lines in the document (see
        /// IncludeDirective documentation) - for "go to file"
        /// (NavigationEngine). Recognised purely via regex on the RAW source
        /// (NOT via the tokens - `#include` is a pure
        /// preprocessor text replacement, see Parsing.Preprocessor, the
        /// lexer sees nothing of it after real compiling, but
        /// HERE we work on the unprocessed editor text).</summary>
        public List<IncludeDirective> IncludeDirectives { get; } = new();

        private readonly List<Token> _tokens = new();
        private readonly string _source = string.Empty;
        private readonly int[] _lineStarts;

        /// <summary>All namespaces declared in the document (fully qualified,
        /// including all preliminary stages: `A.B` also creates `A`).</summary>
        public HashSet<string> Namespaces { get; } = new();

        /// <summary>The `#using` names of the document (see Preprocessor - apply
        /// to the whole file).</summary>
        public List<string> UsingNamespaces { get; } = new();

        private readonly List<(string Name, int Start, int End)> _namespaceRanges = new();
        private readonly List<(string Key, int Start, int End)> _classSpans = new();
        private readonly Stack<(string Name, int EndIdx)> _namespaceStack = new();
        private readonly List<(string Target, IReadOnlyList<string> Context, int BodyStart)> _pendingExtensions = new();

        private ScriptSymbolIndex(string source, List<Token> tokens)
        {
            _source = source;
            _tokens = tokens;
            _lineStarts = ComputeLineStarts(source);
        }

        /// <summary>The documentation comment (`///` lines) directly above the 1-based line `declLine` of THIS index's source.</summary>
        internal DocComment? DocumentationAt(int declLine) => DocComments.Find(_source, _lineStarts, declLine);

        private static int[] ComputeLineStarts(string source)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < source.Length; i++)
                if (source[i] == '\n') starts.Add(i + 1);
            return starts.ToArray();
        }

        // Exactly the same expression as Parsing.Preprocessor.IncludeLine -
        // deliberately duplicated instead of shared (Preprocessor is part of the
        // CORE project, ScriptSymbolIndex works on UNPROCESSED,
        // possibly just freshly typed text and therefore needs
        // error tolerance of its own anyway) - but must stay in sync in content,
        // otherwise the editor recognises includes differently from the real compiler.
        private static readonly System.Text.RegularExpressions.Regex IncludeLine =
            new(@"^\s*#include\s+""([^""]*)""\s*$");

        private static readonly System.Text.RegularExpressions.Regex UsingLine =
            new(@"^[ \t]*#using[ \t]+([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)[ \t]*\r?$",
                System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>The names of all `#using X` lines (the same name grammar
        /// as Preprocessor.UsingName).</summary>
        private static IEnumerable<string> FindUsings(string source) =>
            UsingLine.Matches(source).Select(m => m.Groups[1].Value).Distinct();

        public static ScriptSymbolIndex Build(string source) => Build(source, Array.Empty<string>());

        /// <summary>Like Build(source); `extraImports` count in addition to the `#import` lines in `source` as
        /// switched on (e.g. when a prelude is shown that itself depends on another extension).</summary>
        public static ScriptSymbolIndex Build(string source, IEnumerable<string> extraImports) => Build(source, extraImports, Array.Empty<(string, string)>());

        /// <summary>The file this index describes when it was built as one of the other files of a project (null: the document itself, or a prelude).</summary>
        public string? FilePath { get; private set; }

        /// <summary>The file of an enum that is declared in another file of the project (key as in <see cref="EnumDeclLines"/>).</summary>
        public Dictionary<string, string> EnumFiles { get; } = new();

        private static readonly Dictionary<string, (string Text, ScriptSymbolIndex Index)> ProjectFileIndexes = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The index of another file of the project, kept as long as its text does not change.</summary>
        private static ScriptSymbolIndex IndexOfProjectFile(string path, string text)
        {
            lock (ProjectFileIndexes)
            {
                if (ProjectFileIndexes.TryGetValue(path, out var known) && string.Equals(known.Text, text, StringComparison.Ordinal)) return known.Index;
            }
            var index = Build(text);
            index.FilePath = path;
            lock (ProjectFileIndexes) ProjectFileIndexes[path] = (text, index);
            return index;
        }

        /// <summary>Like Build(source, extraImports); `projectFiles` are the other files of the project (and the imported libraries): what they declare is known without an `#include`, for
        /// completion, tooltips and "go to definition" - as it is for the build.</summary>
        public static ScriptSymbolIndex Build(string source, IEnumerable<string> extraImports, IEnumerable<(string Path, string Text)> projectFiles)
        {
            List<Token> tokens;
            try
            {
                tokens = new Lexer(source).Tokenize();
            }
            catch (LexException)
            {
                tokens = new List<Token>();
            }

            var index = new ScriptSymbolIndex(source, tokens);
            index.UsingNamespaces.AddRange(FindUsings(source));
            index.Harvest();
            index.HarvestIncludes(source);
            foreach (var (path, text) in projectFiles)
            {
                try { index.MergeInPrelude(IndexOfProjectFile(path, text), ownDeclarationsOnly: true); }
                catch (Exception) { /* a file that cannot be scanned adds nothing */ }
            }
            index.MergeInPrelude(PreludeIndex.Value);
            // Preludes of the extensions switched on via '#import' (see
            // ImportedPreludes) - `Framebuffer`/`Device`/... should be completed
            // just like the standard library.
            var merged = new HashSet<string>();
            foreach (var name in ImportedPreludes.FindImportNames(source).Concat(extraImports))
            {
                // `#import "ui"` brings `graphics` along, `linq` brings `reflection` along (see ImportedPreludes.WithDependencies).
                IEnumerable<string> keys;
                try { keys = ImportedPreludes.WithDependencies(ImportedPreludes.ParseImportName(name)).ToList(); }
                catch (Exception) { continue; } // unknown extension - the diagnostics report it
                foreach (var key in keys)
                {
                    if (!merged.Add(key)) continue;
                    var importIndex = ImportedPreludeIndex(key);
                    if (importIndex != null) index.MergeInPrelude(importIndex);
                }
            }
            return index;
        }

        /// <summary>Mixes classes/interfaces of the built-in standard
        /// library (fire.Standard.Prelude, see SPEC "Prepended
        /// standard library") into THIS index, marked with
        /// <see cref="ClassInfo.IsFromPrelude"/> - every real script is after all
        /// actually compiled TOGETHER WITH this library (see
        /// Runtime.RuntimeSession.Build/Parser.ParseMultiple), completion/
        /// member search should therefore know `List`/`IEnumerable`/etc. just as well
        /// as classes defined in the document itself.
        ///
        /// A class completely (newly) defined in the document ITSELF
        /// wins (no overwriting) - coincides with the behaviour of the
        /// real compiler, which would reject such a name collision as an
        /// error anyway; for the editor "showing the own definition"
        /// is the more helpful choice here. A `class extends List
        /// { ... }` in the document, on the other hand, when harvesting (see
        /// HarvestClassExtension) only creates a PLACEHOLDER without a real
        /// declaration line (DeclLine stays 0) with the new extension
        /// members - for THIS case the real prelude
        /// members are ADDED afterwards (instead of replacing the extension by
        /// the pure prelude definition), otherwise an
        /// extended `List` in the editor would suddenly "lose" its own Add/Get/...
        /// methods.</summary>
        private void MergeInPrelude(ScriptSymbolIndex prelude, bool ownDeclarationsOnly = false)
        {
            foreach (var (name, preludeClass) in prelude.Classes)
            {
                if (ownDeclarationsOnly && preludeClass.IsFromPrelude) continue;   // the preludes are merged by this index itself
                if (Classes.TryGetValue(name, out var existing))
                {
                    if (existing.DeclLine != 0) continue; // echte eigene (Neu-)Deklaration gewinnt

                    // Placeholder from a 'class extends X { ... }'
                    // extension (see HarvestClassExtension) - the real
                    // prelude members are still missing from it, add them here;
                    // the own extension members remain
                    // in addition (only INSERTED, not replaced).
                    existing.DeclLine = preludeClass.DeclLine;
                    existing.PreludeName = preludeClass.PreludeName;
                    existing.BaseName ??= preludeClass.BaseName;
                    existing.Members.AddRange(preludeClass.Members);
                }
                else
                {
                    Classes[name] = preludeClass;
                }
            }
            // Namespaces and enums of the prelude (e.g. `IO` including `IO.FileMode` with
            // `#import "io"`) - without them there would be no `IO.` suggestions.
            foreach (var ns in prelude.Namespaces)
                Namespaces.Add(ns);
            foreach (var (name, members) in prelude.EnumMembers)
                if (!EnumMembers.ContainsKey(name) && !(ownDeclarationsOnly && prelude.EnumPreludes.ContainsKey(name)))
                {
                    EnumMembers[name] = members;
                    if (ownDeclarationsOnly && prelude.FilePath != null) EnumFiles[name] = prelude.FilePath;
                    if (prelude.EnumDeclLines.TryGetValue(name, out var line)) EnumDeclLines[name] = line;
                    if (prelude.PreludeName != null) EnumPreludes[name] = prelude.PreludeName;
                }
            foreach (var name in prelude.AllDeclaredNames)
                AllDeclaredNames.Add(name);
        }

        /// <summary>The index of the built-in standard library itself -
        /// built ONCE and reused (the source is a
        /// constant, see Standard.Prelude.Source), not anew on every
        /// MergeInPrelude() call. Every ClassInfo contained in it is
        /// marked here with IsFromPrelude=true BEFORE it is mixed into any
        /// document.</summary>
        private static readonly System.Lazy<ScriptSymbolIndex> PreludeIndex = new(() =>
        {
            var index = Build(fire.Standard.Prelude.Source, StandardPreludeName);
            return index;
        });

        private static readonly Dictionary<string, ScriptSymbolIndex?> ImportedPreludeIndexes = new();

        /// <summary>The index of the prelude of an extension switched on via `#import "name"`
        /// (built once and remembered), null
        /// for an unknown name. The definition lines refer to
        /// the prelude source (<see cref="PreludeSourceOf"/>), which the editor
        /// shows in a window of its own when jumping.</summary>
        private static ScriptSymbolIndex? ImportedPreludeIndex(string importName)
        {
            lock (ImportedPreludeIndexes)
            {
                if (ImportedPreludeIndexes.TryGetValue(importName, out var cached)) return cached;
                string? preludeSource = ImportedPreludes.TrySourceFor(importName);
                var index = preludeSource == null ? null : Build(preludeSource, importName.ToLowerInvariant());
                ImportedPreludeIndexes[importName] = index;
                return index;
            }
        }

        /// <summary>Internal Build overload for the prelude source itself
        /// (see PreludeIndex) - builds ONLY the raw index (no recursive
        /// MergeInPrelude(), the prelude does not need to mix
        /// itself in) and afterwards marks every found class as
        /// IsFromPrelude.</summary>
        private static ScriptSymbolIndex Build(string source, string preludeName)
        {
            List<Token> tokens;
            try
            {
                tokens = new Lexer(source).Tokenize();
            }
            catch (LexException)
            {
                tokens = new List<Token>();
            }

            var index = new ScriptSymbolIndex(source, tokens) { PreludeName = preludeName };
            index.Harvest();
            foreach (var classInfo in index.Classes.Values)
                classInfo.PreludeName = preludeName;
            return index;
        }

        private void HarvestIncludes(string source)
        {
            var lines = source.Split('\n');
            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                var match = IncludeLine.Match(lines[lineIdx].TrimEnd('\r'));
                if (match.Success)
                    IncludeDirectives.Add(new IncludeDirective(match.Groups[1].Value, lineIdx + 1));
            }
        }

        // -----------------------------------------------------------
        // Collecting: classes + their members, enums, all identifiers
        // -----------------------------------------------------------

        /// <summary>The namespace the harvest run is currently in (empty =
        /// none) - nested blocks append themselves with '.'.</summary>
        private string CurrentNamespace => _namespaceStack.Count > 0 ? _namespaceStack.Peek().Name : string.Empty;

        /// <summary>Fully qualifies a name declared in the current namespace
        /// (see Parser.QualifyDeclName).</summary>
        private string Qualify(string simpleName) =>
            CurrentNamespace.Length == 0 ? simpleName : CurrentNamespace + "." + simpleName;

        /// <summary>The namespaces for resolving type names at the
        /// current harvest position: current namespace, then `#using`.</summary>
        private IReadOnlyList<string> CurrentContext() =>
            CurrentNamespace.Length == 0
                ? UsingNamespaces.ToList()
                : new[] { CurrentNamespace }.Concat(UsingNamespaces).ToList();

        /// <summary>`namespace A.B { ... }` at `i` - remembers namespace and
        /// range, returns the index AFTER the opening '{' (the content is
        /// read on normally by the harvest run).</summary>
        private int HarvestNamespace(int i)
        {
            int j = i + 1;
            if (j >= _tokens.Count || _tokens[j].Type != TokenType.Identifier) return i + 1;
            string name = _tokens[j].Lexeme;
            j++;
            while (j + 1 < _tokens.Count && _tokens[j].Type == TokenType.Dot && _tokens[j + 1].Type == TokenType.Identifier)
            {
                name += "." + _tokens[j + 1].Lexeme;
                j += 2;
            }
            if (j >= _tokens.Count || _tokens[j].Type != TokenType.LBrace) return i + 1;

            string full = Qualify(name);
            int end = MatchBrace(j);
            _namespaceStack.Push((full, end));
            _namespaceRanges.Add((full, TokenOffset(j), TokenOffset(end) + 1));
            for (string part = full; ; )
            {
                Namespaces.Add(part);
                int dot = part.LastIndexOf('.');
                if (dot < 0) break;
                part = part.Substring(0, dot);
            }
            return j + 1;
        }

        private void Harvest()
        {
            int i = 0;
            while (i < _tokens.Count && _tokens[i].Type != TokenType.Eof)
            {
                while (_namespaceStack.Count > 0 && _namespaceStack.Peek().EndIdx < i)
                    _namespaceStack.Pop();

                if (_tokens[i].Type == TokenType.Namespace)
                {
                    i = HarvestNamespace(i);
                }
                else if (_tokens[i].Type == TokenType.Class || _tokens[i].Type == TokenType.Actor)
                {
                    i = HarvestClass(i);
                }
                else if (_tokens[i].Type == TokenType.Interface)
                {
                    i = HarvestClass(i, isInterface: true);
                }
                else if (_tokens[i].Type == TokenType.Enum)
                {
                    i = HarvestEnum(i);
                }
                else
                {
                    if (_tokens[i].Type == TokenType.Identifier)
                        AllDeclaredNames.Add(_tokens[i].Lexeme);
                    i++;
                }
            }

            // `class extends X { ... }` only AFTER all declarations: X can
            // also stand in the document AFTER the extension, and in which
            // namespace it lies can only be resolved then.
            foreach (var (target, context, bodyStart) in _pendingExtensions)
            {
                string key = ResolveClassKey(target, context) ?? target;
                if (!Classes.TryGetValue(key, out var info))
                {
                    info = new ClassInfo(key) { Source = this };
                    Classes[key] = info;
                }
                HarvestMembersBody(bodyStart, info);
            }
            _pendingExtensions.Clear();
        }

        /// <summary>The line that a symbol gets as its definition location.</summary>
        private static int DeclLineOf(Token token) => token.Line;

        private void AddMember(ClassInfo info, MemberInfo member)
        {
            member.Owner = info.Name;
            member.Source = this;
            info.Members.Add(member);
        }

        /// <summary>Index behind the `&gt;` belonging to `_tokens[ltIdx]` (must be `&lt;`)
        /// - for `class Box&lt;T&gt;` and generic
        /// methods. Nested `&lt;...&gt;` are counted along.</summary>
        private int SkipAngleBrackets(int ltIdx)
        {
            int depth = 0;
            for (int j = ltIdx; j < _tokens.Count; j++)
            {
                if (_tokens[j].Type == TokenType.Lt) depth++;
                else if (_tokens[j].Type == TokenType.Gt)
                {
                    depth--;
                    if (depth == 0) return j + 1;
                }
                else if (_tokens[j].Type is TokenType.LBrace or TokenType.LParen or TokenType.Eof)
                    return ltIdx + 1; // no real type-argument pair - do not get stuck
            }
            return ltIdx + 1;
        }

        /// <summary>`class Name { ... }` / `actor Name { ... }` (both
        /// treated the same - 'actor' is no category of its own for this best-effort preview,
        /// just classes with a mailbox at
        /// runtime, see Bytecode.RuntimeClass.IsActor) / `class extends X { ... }`
        /// / `actor extends X { ... }` (extension - see
        /// HarvestClassExtension, merges into an EXISTING or previously
        /// created ClassInfo instead of creating a new one).</summary>
        private int HarvestClass(int i, bool isInterface = false)
        {
            i++; // 'class'/'actor'/'interface'

            if (!isInterface && i < _tokens.Count && _tokens[i].Type == TokenType.Extends)
                return HarvestClassExtension(i + 1);

            if (i >= _tokens.Count || _tokens[i].Type != TokenType.Identifier) return i;

            // 'class Name(' is not a class, but a return type 'class'
            // in front of a method (e.g. 'class GetCurrent()' in an interface).
            if (i + 1 < _tokens.Count && _tokens[i + 1].Type == TokenType.LParen) return i;

            string className = Qualify(_tokens[i].Lexeme);
            // For a duplicate declaration (an error that the resolver
            // reports) keep using the first - irrelevant for the suggestions.
            var info = Classes.TryGetValue(className, out var existing)
                ? existing
                : new ClassInfo(className, DeclLineOf(_tokens[i])) { Context = CurrentContext(), Source = this };
            info.DeclLine = DeclLineOf(_tokens[i]);
            info.IsInterface = isInterface;
            i++;

            // Generic class ('class Name<T> ...') - skip the type
            // parameters, otherwise the rest of the head would not be recognisable
            // and the class would have no members at all.
            if (i < _tokens.Count && _tokens[i].Type == TokenType.Lt)
                i = SkipAngleBrackets(i);

            // ': Base, Interface, ...' - remember ALL names (which of them is
            // the real base class is decided only by the resolver).
            if (i < _tokens.Count && _tokens[i].Type == TokenType.Colon)
            {
                i++;
                while (i < _tokens.Count && _tokens[i].Type != TokenType.LBrace && _tokens[i].Type != TokenType.Where
                       && _tokens[i].Type != TokenType.Eof)
                {
                    if (_tokens[i].Type == TokenType.Identifier)
                    {
                        // Also a qualified name ('Geometry.Shape').
                        string baseName = _tokens[i].Lexeme;
                        while (i + 2 < _tokens.Count && _tokens[i + 1].Type == TokenType.Dot
                               && _tokens[i + 2].Type == TokenType.Identifier)
                        {
                            baseName += "." + _tokens[i + 2].Lexeme;
                            i += 2;
                        }
                        if (!info.BaseNames.Contains(baseName)) info.BaseNames.Add(baseName);
                    }
                    i++;
                }
            }

            // Skip 'where' clauses (generics) up to the body.
            while (i < _tokens.Count && _tokens[i].Type != TokenType.LBrace && _tokens[i].Type != TokenType.Eof) i++;

            Classes[className] = info;

            if (i >= _tokens.Count || _tokens[i].Type != TokenType.LBrace) return i;
            return HarvestMembersBody(i, info);
        }

        /// <summary>`class extends X { ... }` / `actor extends X { ... }`
        /// (see Parser.ParseClassExtensionDecl/MergeClassExtensions for the
        /// real semantics: members move 1:1 into the
        /// TARGET class at compile time) - here accordingly: enter members directly into the
        /// ClassInfo of X (already known, or still unknown and then created beforehand -
        /// the actual declaration of X can stand in the document BEFORE or AFTER
        /// this extension) instead of creating a
        /// new class of its own.</summary>
        private int HarvestClassExtension(int i)
        {
            if (i >= _tokens.Count) return i;

            // `class extends string { ... }` (SPEC 5.5.1): a base type is a keyword. Its
            // methods end up, as in the compiler, in a collective class (`$string`, see
            // BaseTypeExtensions), which completion evaluates for values of this type.
            if (_tokens[i].Type is TokenType.KwString or TokenType.KwChar or TokenType.KwInt or TokenType.KwFloat or TokenType.KwBool)
            {
                string baseTypeClass = BaseTypeExtensions.ClassName(_tokens[i].Lexeme);
                i++;
                if (i >= _tokens.Count || _tokens[i].Type != TokenType.LBrace) return i;
                _pendingExtensions.Add((baseTypeClass, CurrentContext(), i));
                return MatchBrace(i) + 1;
            }

            if (_tokens[i].Type != TokenType.Identifier) return i;
            string targetName = _tokens[i].Lexeme;
            i++;
            while (i + 1 < _tokens.Count && _tokens[i].Type == TokenType.Dot && _tokens[i + 1].Type == TokenType.Identifier)
            {
                targetName += "." + _tokens[i + 1].Lexeme;
                i += 2;
            }

            if (i >= _tokens.Count || _tokens[i].Type != TokenType.LBrace) return i;

            // Enter the members only at the end of the harvest run (see
            // there) - here only skip the body.
            _pendingExtensions.Add((targetName, CurrentContext(), i));
            return MatchBrace(i) + 1;
        }

        /// <summary>Collects the members of a class/extension body
        /// from its opening '{' at `bodyStart` into `info` - shared
        /// by HarvestClass (real declaration) and
        /// HarvestClassExtension (extension), since both have the same
        /// member grammar.</summary>
        private int HarvestMembersBody(int bodyStart, ClassInfo info)
        {
            int bodyEnd = MatchBrace(bodyStart);
            info.BodyRanges.Add((bodyStart, bodyEnd));
            _classSpans.Add((info.Name, TokenOffset(bodyStart), TokenOffset(bodyEnd) + 1));
            int i = bodyStart + 1; // behind the opening '{'
            string className = info.SimpleName;

            // Modifiers stand BEFORE the actual member and apply
            // to the next recognised member.
            bool isStatic = false;
            var access = MemberAccess.Public;

            while (i < bodyEnd)
            {
                var type = _tokens[i].Type;
                if (type == TokenType.Static) { isStatic = true; i++; continue; }
                if (type == TokenType.Public) { access = MemberAccess.Public; i++; continue; }
                if (type == TokenType.Private) { access = MemberAccess.Private; i++; continue; }
                if (type == TokenType.Protected) { access = MemberAccess.Protected; i++; continue; }

                if (type == TokenType.Construct)
                {
                    int parenAt = i + 1;
                    if (parenAt < bodyEnd && _tokens[parenAt].Type == TokenType.LParen)
                    {
                        int paramCount = CountParams(parenAt);
                        AddMember(info, new MemberInfo(className, MemberKind.Constructor, paramCount, DeclLineOf(_tokens[i]))
                        {
                            Access = access,
                            Signature = FormatSignature(ReadParamList(parenAt, out _)),
                        });
                    }
                    i = SkipToNextMemberAfterBody(i, bodyEnd);
                }
                else if (type == TokenType.Destruct)
                {
                    i = SkipToNextMemberAfterBody(i, bodyEnd);
                }
                else if (type == TokenType.Operator)
                {
                    i = HarvestOperatorMember(i, bodyEnd, info);
                }
                else if (type == TokenType.Identifier || IsTypeKeyword(type) || type == TokenType.Readonly)
                {
                    i = HarvestMember(i, bodyEnd, info, isStatic, access);
                }
                else
                {
                    i++;
                    continue; // no member - do NOT reset modifiers
                }

                isStatic = false;
                access = MemberAccess.Public;
            }

            return bodyEnd + 1;
        }

        private static string FormatSignature(List<(string Name, string? TypeName)> parms) =>
            string.Join(", ", parms.Select(p => p.TypeName != null ? p.TypeName + " " + p.Name : p.Name));

        /// <summary>Skips a bit width (`int[16]`) and pointer stars (`int*`) behind a type keyword,
        /// see TypeRef.</summary>
        private int SkipTypeSuffix(int i, int end)
        {
            if (i + 2 < end && _tokens[i].Type == TokenType.LBracket
                && _tokens[i + 1].Type == TokenType.IntLiteral && _tokens[i + 2].Type == TokenType.RBracket)
                i += 3;
            while (i < end && _tokens[i].Type == TokenType.Star) i++;
            return i;
        }

        /// <summary>Skips empty bracket pairs `[]` from `i` (array type, `int[] Name()`) and reports via
        /// `found` whether there were any.</summary>
        private int SkipEmptyBrackets(int i, int end, ref bool found)
        {
            while (i + 1 < end && _tokens[i].Type == TokenType.LBracket && _tokens[i + 1].Type == TokenType.RBracket)
            {
                found = true;
                i += 2;
            }
            return i;
        }

        /// <summary>Index of the first token AFTER an expression that begins at
        /// `startIdx`: the end is a `;`, a closing
        /// '}'/')'/']' or a line break - in each case only at
        /// bracket depth 0 (multi-line argument lists/lambdas thus remain
        /// ONE expression), as with the statement separation of the parser.</summary>
        internal int ExpressionEnd(int startIdx, int limit)
        {
            int depth = 0;
            for (int k = startIdx; k < limit; k++)
            {
                var t = _tokens[k];
                if (t.Type == TokenType.Eof) return k;
                if (depth == 0 && k > startIdx && (t.NewlineBefore || t.Type == TokenType.Semicolon
                    || t.Type is TokenType.RBrace or TokenType.RParen or TokenType.RBracket or TokenType.Comma))
                    return k;
                if (t.Type is TokenType.LParen or TokenType.LBracket or TokenType.LBrace) depth++;
                else if (t.Type is TokenType.RParen or TokenType.RBracket or TokenType.RBrace) depth--;
            }
            return limit;
        }

        /// <summary>A single class member from token index `i` (after
        /// an optional 'readonly' and an optional type comes the name) - the same
        /// rough heuristic as Parser.NextLooksLikeTypeThenName (type keyword
        /// OR two consecutive identifiers), only without real
        /// AST construction.</summary>
        private int HarvestMember(int i, int bodyEnd, ClassInfo info, bool isStatic, MemberAccess access)
        {
            int start = i;
            if (_tokens[i].Type == TokenType.Readonly) i++;
            if (i >= bodyEnd) return bodyEnd;

            // Remember/skip the optional type (type keyword, or two
            // identifiers in a row = "class name field name").
            // `int[] Name()` - empty brackets behind the type = array RETURN TYPE (method/property).
            string? typeName = null;
            bool returnsArray = false;
            if (IsTypeKeyword(_tokens[i].Type))
            {
                typeName = _tokens[i].Lexeme;
                i = SkipTypeSuffix(i + 1, bodyEnd);
                i = SkipEmptyBrackets(i, bodyEnd, ref returnsArray);
            }
            else if (_tokens[i].Type == TokenType.Identifier)
            {
                // Class name, also qualified ('Geometry.Circle'), followed
                // by the name on the same line.
                int typeEnd = i;
                while (typeEnd + 2 < bodyEnd && _tokens[typeEnd + 1].Type == TokenType.Dot
                       && _tokens[typeEnd + 2].Type == TokenType.Identifier)
                    typeEnd += 2;
                bool identifierArray = false;
                int afterType = SkipEmptyBrackets(typeEnd + 1, bodyEnd, ref identifierArray);
                if (afterType < bodyEnd && _tokens[afterType].Type == TokenType.Identifier
                    && !_tokens[afterType].NewlineBefore)
                {
                    typeName = string.Concat(_tokens.Skip(i).Take(typeEnd - i + 1).Select(t => t.Lexeme));
                    returnsArray = identifierArray;
                    i = afterType;
                }
            }

            if (i >= bodyEnd || _tokens[i].Type != TokenType.Identifier)
                return start + 1; // no recognisable member - move on by only one token, do not get stuck

            string name = _tokens[i].Lexeme;
            int nameIdx = i;
            int afterName = i + 1;

            // Generic method ('Name<T>(...)') - skip the type parameters.
            if (afterName < bodyEnd && _tokens[afterName].Type == TokenType.Lt)
                afterName = SkipAngleBrackets(afterName);

            if (afterName < bodyEnd && _tokens[afterName].Type == TokenType.LParen)
            {
                int paramCount = CountParams(afterName);
                var parms = ReadParamList(afterName, out int afterParams);

                // Body directly behind the signature - an interface method has
                // none (then the member ends behind the ')').
                int bodyOpen = FindBodyBrace(afterParams);
                int bodyClose = bodyOpen >= 0 && bodyOpen < bodyEnd ? MatchBrace(bodyOpen) : -1;

                AddMember(info, new MemberInfo(name, MemberKind.Method, paramCount, DeclLineOf(_tokens[nameIdx]))
                {
                    TypeName = typeName,
                    TypeIsArray = returnsArray,
                    IsStatic = isStatic,
                    Access = access,
                    Signature = FormatSignature(parms),
                    BodyStart = bodyClose >= 0 ? bodyOpen : -1,
                    BodyEnd = bodyClose,
                });
                return bodyClose >= 0 ? bodyClose + 1 : Math.Min(afterParams, bodyEnd);
            }

            if (afterName < bodyEnd && _tokens[afterName].Type == TokenType.LBrace)
            {
                AddMember(info, new MemberInfo(name, MemberKind.Property, 0, DeclLineOf(_tokens[nameIdx]))
                {
                    TypeName = typeName,
                    TypeIsArray = returnsArray,
                    IsStatic = isStatic,
                    Access = access,
                });
                return SkipBraceBlock(afterName) + 1;
            }

            // Otherwise: field. Also skip the array declarator ('type name[]') and initialiser
            // ('= ...') - otherwise identifiers IN them
            // (e.g. 'new Foo()') would be misunderstood as members of their own.
            bool isArray = false;
            while (afterName < bodyEnd && _tokens[afterName].Type == TokenType.LBracket)
            {
                isArray = true;
                int close = afterName;
                int depth = 0;
                for (; close < bodyEnd; close++)
                {
                    if (_tokens[close].Type == TokenType.LBracket) depth++;
                    else if (_tokens[close].Type == TokenType.RBracket && --depth == 0) break;
                }
                afterName = close + 1;
            }

            if (afterName < bodyEnd && _tokens[afterName].Type == TokenType.Assign)
            {
                // Infer from a 'new X(...)' initialiser without a declared type
                // (the most common case for dynamically typed fields).
                if (typeName == null && afterName + 2 < bodyEnd && _tokens[afterName + 1].Type == TokenType.New
                    && _tokens[afterName + 2].Type == TokenType.Identifier)
                {
                    typeName = _tokens[afterName + 2].Lexeme;
                    isArray = afterName + 3 < bodyEnd && _tokens[afterName + 3].Type == TokenType.LBracket;
                }
                afterName = ExpressionEnd(afterName + 1, bodyEnd);
            }

            AddMember(info, new MemberInfo(name, MemberKind.Field, 0, DeclLineOf(_tokens[nameIdx]))
            {
                TypeName = typeName,
                TypeIsArray = isArray,
                IsStatic = isStatic,
                Access = access,
            });
            return afterName;
        }

        /// <summary>`operator SYMBOL(params) { body }` (see Parser.
        /// ParseOperatorMember/ParseOperatorSymbol) - the same rough
        /// token heuristic as in the rest of this class, without real
        /// AST construction. Captured as MemberKind.Method with a
        /// readable name ("operator+", "operator[]", ...) - for navigation/
        /// completion that is enough, the exact internal naming convention
        /// (GetIndex/SetIndex/"operator+") does not have to be replicated
        /// here.</summary>
        private int HarvestOperatorMember(int i, int bodyEnd, ClassInfo info)
        {
            int line = DeclLineOf(_tokens[i]);
            i++; // 'operator'
            if (i >= bodyEnd) return bodyEnd;

            string symbol;
            if (_tokens[i].Type == TokenType.LBracket && i + 1 < bodyEnd && _tokens[i + 1].Type == TokenType.RBracket)
            {
                symbol = "[]";
                i += 2;
            }
            else if ((_tokens[i].Type == TokenType.Lt && i + 1 < bodyEnd && _tokens[i + 1].Type == TokenType.Lt)
                     || (_tokens[i].Type == TokenType.Gt && i + 1 < bodyEnd && _tokens[i + 1].Type == TokenType.Gt))
            {
                symbol = _tokens[i].Type == TokenType.Lt ? "<<" : ">>";
                i += 2;
            }
            else
            {
                symbol = _tokens[i].Lexeme;
                i++;
            }

            if (i >= bodyEnd || _tokens[i].Type != TokenType.LParen)
                return i; // unexpected continuation - do not get stuck

            int paramCount = CountParams(i);
            var parms = ReadParamList(i, out int afterParams);
            int bodyOpen = FindBodyBrace(afterParams);
            int bodyClose = bodyOpen >= 0 && bodyOpen < bodyEnd ? MatchBrace(bodyOpen) : -1;
            AddMember(info, new MemberInfo("operator" + symbol, MemberKind.Method, paramCount, line)
            {
                Signature = FormatSignature(parms),
                BodyStart = bodyClose >= 0 ? bodyOpen : -1,
                BodyEnd = bodyClose,
            });
            return bodyClose >= 0 ? bodyClose + 1 : Math.Min(afterParams, bodyEnd);
        }

        private int HarvestEnum(int i)
        {
            i++; // 'enum'
            if (i >= _tokens.Count || _tokens[i].Type != TokenType.Identifier) return i;
            string enumName = Qualify(_tokens[i].Lexeme);
            EnumDeclLines[enumName] = DeclLineOf(_tokens[i]);
            i++;
            if (i >= _tokens.Count || _tokens[i].Type != TokenType.LBrace) return i;

            int bodyEnd = MatchBrace(i);
            var members = new List<string>();
            i++;
            while (i < bodyEnd)
            {
                if (_tokens[i].Type == TokenType.Identifier)
                {
                    members.Add(_tokens[i].Lexeme);
                    i++;
                    // skip the optional '= value' up to the next comma
                    while (i < bodyEnd && _tokens[i].Type != TokenType.Comma) i++;
                }
                else
                {
                    i++;
                }
            }
            EnumMembers[enumName] = members;
            return bodyEnd + 1;
        }

        // -----------------------------------------------------------
        // Helper functions over the token stream
        // -----------------------------------------------------------

        private static bool IsTypeKeyword(TokenType t) =>
            t is TokenType.KwBool or TokenType.KwInt or TokenType.KwFloat or TokenType.KwChar or TokenType.KwString or TokenType.KwByte;

        /// <summary>Index of the closing '}' belonging to `_tokens[openBraceIdx]`
        /// (must be '{').</summary>
        private int MatchBrace(int openBraceIdx)
        {
            int depth = 0;
            for (int j = openBraceIdx; j < _tokens.Count; j++)
            {
                if (_tokens[j].Type == TokenType.LBrace) depth++;
                else if (_tokens[j].Type == TokenType.RBrace)
                {
                    depth--;
                    if (depth == 0) return j;
                }
            }
            return _tokens.Count - 1;
        }

        private int SkipBraceBlock(int openBraceIdx) => MatchBrace(openBraceIdx);

        /// <summary>After a method/constructor signature (parameters
        /// read, index points at or before the body '{') jump to behind the
        /// associated body.</summary>
        private int SkipToNextMemberAfterBody(int fromIdx, int bodyEnd)
        {
            int j = fromIdx;
            while (j < bodyEnd && _tokens[j].Type != TokenType.LBrace) j++;
            if (j >= bodyEnd) return bodyEnd;
            return MatchBrace(j) + 1;
        }

        /// <summary>Rough parameter count of a bracket list from the opening
        /// '(' at `parenIdx` - counts commas at bracket depth 1, not exact
        /// (default values with commas in them would falsify it), but sufficient
        /// for a display number in completion.</summary>
        private int CountParams(int parenIdx)
        {
            int depth = 0;
            int commas = 0;
            bool any = false;
            for (int j = parenIdx; j < _tokens.Count; j++)
            {
                if (_tokens[j].Type == TokenType.LParen) depth++;
                else if (_tokens[j].Type == TokenType.RParen)
                {
                    depth--;
                    if (depth == 0) break;
                }
                else if (depth == 1)
                {
                    any = true;
                    if (_tokens[j].Type == TokenType.Comma) commas++;
                }
            }
            return any ? commas + 1 : 0;
        }

        // -----------------------------------------------------------
        // All members of a class incl. base-class chain (as far as
        // known in the same document - stops at an unknown/external
        // base class).
        // -----------------------------------------------------------

        public IEnumerable<MemberInfo> MembersOf(string className) =>
            MembersOfWithDepth(className).Select(x => x.Member);

        /// <summary>Like <see cref="MembersOf"/>, with the distance of the
        /// declaring class to `className` (0 = the class itself, 1 =
        /// direct base/interface, ...) - breadth-first over ALL base names
        /// (see ClassInfo.BaseNames); a member that a nearer class
        /// already declares (overriding) appears only there.</summary>
        public IEnumerable<(MemberInfo Member, int Depth)> MembersOfWithDepth(string className)
        {
            var seen = new HashSet<string>();
            var visited = new HashSet<string> { className };
            var queue = new Queue<(string Name, int Depth)>();
            queue.Enqueue((className, 0));
            while (queue.Count > 0)
            {
                var (current, depth) = queue.Dequeue();
                if (!Classes.TryGetValue(current, out var info)) continue;
                foreach (var m in info.Members)
                    if (seen.Add(m.Kind + ":" + m.Name + "/" + m.ParamCount))
                        yield return (m, depth);
                foreach (var baseKey in ResolvedBases(info))
                    if (visited.Add(baseKey))
                        queue.Enqueue((baseKey, depth + 1));
            }
        }

        /// <summary>The base classes/interfaces of `info` as keys in
        /// <see cref="Classes"/> (the names in the class head are written relative to the
        /// context of the class, see ClassInfo.Context) - those that cannot be
        /// resolved are left out.</summary>
        public IEnumerable<string> ResolvedBases(ClassInfo info)
        {
            foreach (var baseName in info.BaseNames)
            {
                string? key = ResolveClassKey(baseName, info.Context);
                if (key != null) yield return key;
            }
        }

        // -----------------------------------------------------------
        // Resolving names: classes, enums, namespaces
        // -----------------------------------------------------------

        /// <summary>Resolves `name` (as written, possibly qualified) against
        /// `context` (see ClassInfo.Context) to a key in
        /// <see cref="Classes"/> - like TypeRef.ResolveBaseName: first the
        /// exact name, then every namespace of the context in front of it. As a last
        /// resort (e.g. because of a `#using` in another file) a
        /// UNIQUE hit via the simple name. `null` if nothing
        /// fits.</summary>
        public string? ResolveClassKey(string name, IReadOnlyList<string> context, bool lenient = true)
        {
            if (Classes.ContainsKey(name)) return name;
            foreach (var ns in context)
                if (Classes.ContainsKey(ns + "." + name)) return ns + "." + name;

            if (!lenient || name.Contains('.')) return null;
            string? only = null;
            foreach (var cls in Classes.Values)
            {
                if (cls.SimpleName != name) continue;
                if (only != null) return null; // mehrdeutig
                only = cls.Name;
            }
            return only;
        }

        /// <summary>Like <see cref="ResolveClassKey"/>, for enums (keys in
        /// <see cref="EnumMembers"/>), without the uniqueness resort.</summary>
        public string? ResolveEnumKey(string name, IReadOnlyList<string> context)
        {
            if (EnumMembers.ContainsKey(name)) return name;
            foreach (var ns in context)
                if (EnumMembers.ContainsKey(ns + "." + name)) return ns + "." + name;
            return null;
        }

        /// <summary>For click navigation: `name` (class name as written)
        /// at document position `offset` (-1: no context, e.g. ANOTHER
        /// file) to the key in <see cref="Classes"/>.</summary>
        public string? TryFindClass(string name, int offset) =>
            ResolveClassKey(name, offset < 0 ? UsingNamespaces : ContextAt(offset));

        /// <summary>Like <see cref="TryFindClass"/>, for enums.</summary>
        public string? TryFindEnum(string name, int offset)
        {
            var context = offset < 0 ? UsingNamespaces : ContextAt(offset);
            return ResolveEnumKey(name, context)
                ?? EnumMembers.Keys.FirstOrDefault(k => k.EndsWith("." + name, StringComparison.Ordinal));
        }

        /// <summary>The innermost namespace block that contains `offset` (null
        /// outside any block).</summary>
        public string? NamespaceAt(int offset)
        {
            string? best = null;
            int bestStart = -1;
            foreach (var (name, start, end) in _namespaceRanges)
                if (offset >= start && offset <= end && start > bestStart)
                {
                    best = name;
                    bestStart = start;
                }
            return best;
        }

        /// <summary>The namespaces against which type names at `offset` are resolved:
        /// the enclosing namespace first, then the `#using` names
        /// of the document (see TypeRef.ResolveBaseName - the namespaces
        /// ABOVE the current one do NOT count).</summary>
        public IReadOnlyList<string> ContextAt(int offset)
        {
            string? ns = NamespaceAt(offset);
            return ns == null ? UsingNamespaces : new[] { ns }.Concat(UsingNamespaces).ToList();
        }

        /// <summary>What stands directly IN a namespace (`ns` empty = at the very top):
        /// sub-namespaces, classes, interfaces and enums, each with
        /// the simple name - basis for `Namespace.` suggestions.</summary>
        public List<NamespaceMember> MembersOfNamespace(string ns)
        {
            var result = new List<NamespaceMember>();
            string prefix = ns.Length == 0 ? string.Empty : ns + ".";

            foreach (var name in Namespaces)
                if (name.StartsWith(prefix, StringComparison.Ordinal) && !name.Substring(prefix.Length).Contains('.')
                    && name.Length > prefix.Length)
                    result.Add(new NamespaceMember(name.Substring(prefix.Length), NamespaceMemberKind.Namespace, name));

            foreach (var cls in Classes.Values)
                if (cls.Namespace == ns)
                    result.Add(new NamespaceMember(cls.SimpleName,
                        cls.IsInterface ? NamespaceMemberKind.Interface : NamespaceMemberKind.Class, cls.Name));

            foreach (var key in EnumMembers.Keys)
            {
                int dot = key.LastIndexOf('.');
                string enumNs = dot < 0 ? string.Empty : key.Substring(0, dot);
                if (enumNs == ns)
                    result.Add(new NamespaceMember(dot < 0 ? key : key.Substring(dot + 1), NamespaceMemberKind.Enum, key));
            }
            return result;
        }

        /// <summary>Is `ancestor` (directly or via several levels) a base
        /// of `className`?</summary>
        public bool DerivesFrom(string className, string ancestor)
        {
            var visited = new HashSet<string> { className };
            var queue = new Queue<string>();
            queue.Enqueue(className);
            while (queue.Count > 0)
            {
                if (!Classes.TryGetValue(queue.Dequeue(), out var info)) continue;
                foreach (var baseKey in ResolvedBases(info))
                {
                    if (baseKey == ancestor) return true;
                    if (visited.Add(baseKey)) queue.Enqueue(baseKey);
                }
            }
            return false;
        }

        // -----------------------------------------------------------
        // Context at a cursor position (character offset in the source text)
        // -----------------------------------------------------------

        /// <summary>Name of the class whose body contains the cursor position `offset`
        /// (for 'this.'), or null outside any class.</summary>
        public string? EnclosingClassAt(int offset)
        {
            // From the class bodies remembered during harvest: 'class Name {...}'
            // yields the class itself, 'class extends X {...}' its TARGET class
            // X (see Parser.ParseClassExtensionDecl) - in each case as a
            // fully qualified key in Classes.
            string? best = null;
            int bestStart = -1;
            foreach (var (key, start, end) in _classSpans)
                if (offset >= start && offset <= end && start > bestStart)
                {
                    best = key;
                    bestStart = start;
                }
            return best;
        }

        private List<(string Name, string? TypeName)> ReadParamList(int parenIdx, out int afterParen)
        {
            var result = new List<(string, string?)>();
            int depth = 0;
            int j = parenIdx;
            var currentTokens = new List<Token>();
            for (; j < _tokens.Count; j++)
            {
                if (_tokens[j].Type == TokenType.LParen) { depth++; if (depth == 1) continue; }
                else if (_tokens[j].Type == TokenType.RParen)
                {
                    depth--;
                    if (depth == 0) { FlushParam(currentTokens, result); j++; break; }
                }
                if (depth >= 1)
                {
                    if (_tokens[j].Type == TokenType.Comma && depth == 1)
                    {
                        FlushParam(currentTokens, result);
                        currentTokens.Clear();
                    }
                    else if (!(depth == 1 && _tokens[j].Type == TokenType.LParen))
                    {
                        currentTokens.Add(_tokens[j]);
                    }
                }
            }
            afterParen = j;
            return result;

            static void FlushParam(List<Token> toks, List<(string, string?)> outList)
            {
                // The last identifier in the group is the parameter name, an
                // identifier/type keyword in front of it (if present) the
                // type name - matches "[type] name" as in the rest of the language.
                // A default value ('= ...') no longer belongs to "[type] name".
                int assign = toks.FindIndex(t => t.Type == TokenType.Assign);
                if (assign >= 0) toks = toks.GetRange(0, assign);

                int nameIdx = toks.FindLastIndex(t => t.Type == TokenType.Identifier);
                if (nameIdx < 0) return;
                string name = toks[nameIdx].Lexeme;

                // Type before it: skip bit width/stars ('int[16]', 'int*'), then
                // type keyword or (possibly qualified) class name.
                int t2 = nameIdx - 1;
                while (t2 >= 0 && toks[t2].Type == TokenType.Star) t2--;
                if (t2 >= 0 && toks[t2].Type == TokenType.RBracket)
                {
                    while (t2 >= 0 && toks[t2].Type != TokenType.LBracket) t2--;
                    t2--;
                }
                string? type = null;
                if (t2 >= 0 && (toks[t2].Type == TokenType.Identifier || IsTypeKeyword(toks[t2].Type)))
                {
                    type = toks[t2].Lexeme;
                    while (t2 >= 2 && toks[t2 - 1].Type == TokenType.Dot && toks[t2 - 2].Type == TokenType.Identifier)
                    {
                        type = toks[t2 - 2].Lexeme + "." + type;
                        t2 -= 2;
                    }
                }
                outList.Add((name, type));
            }
        }

        private int OffsetOf(Token token)
        {
            int idx = token.Line - 1;
            if (idx < 0 || idx >= _lineStarts.Length) return 0;
            return _lineStarts[idx] + (token.Column - 1);
        }

        /// <summary>Character offset of each token (parallel to `_tokens`),
        /// computed once - the type derivation (see ScriptSymbolIndex.
        /// Types) compares offsets very often.</summary>
        private int[]? _tokenOffsets;
        private int TokenOffset(int tokenIdx)
        {
            if (_tokenOffsets == null)
            {
                var offsets = new int[_tokens.Count];
                for (int k = 0; k < offsets.Length; k++) offsets[k] = OffsetOf(_tokens[k]);
                _tokenOffsets = offsets;
            }
            return _tokenOffsets[tokenIdx];
        }
    }
}
