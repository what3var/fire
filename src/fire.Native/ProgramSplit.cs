using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace fire.Native
{
    /// <summary>Whether the C of a program is one file or several translation units (docs/spec/NATIVE_SPLIT.md): `"split"` in fire.native.json, `--split`,
    /// FIRE_SPLIT=1/0 (which goes before both).</summary>
    public enum SplitMode
    {
        /// <summary>Several units with gcc, clang (MinGW too) and Emscripten when the program is large (<see cref="ProgramSplit.AutoMinBytes"/>); one file otherwise.</summary>
        Auto,
        /// <summary>Several units whenever the toolchain can build them.</summary>
        On,
        /// <summary>One file, always.</summary>
        Off,
    }

    /// <summary>What a translation unit of a split program holds.</summary>
    public enum SplitUnitKind
    {
        /// <summary>`fire_program.h`: the configuration of the runtime, `fire_rt.h` and the declarations of the program (every unit includes it).</summary>
        Header,
        /// <summary>`fire_program.c`, unit 0: the state of the runtime (FIRE_RT_IMPLEMENTATION), the data of the program, `fire_init` and the entry point.</summary>
        Runtime,
        /// <summary>`fire_program_hN.c`: the helpers (dispatchers, selectors, field helpers, reflection, JSON tables, `inline c`).</summary>
        Helpers,
        /// <summary>`fire_program_N.c`: the bodies of a group of fire functions.</summary>
        Functions,
        /// <summary>`fire_natives_<import>.c`: the natives in C of an import that stands alone (or of those that belong together).</summary>
        Natives,
    }

    /// <summary>A file of a split program: its name (next to the runtime files), its text, what it holds and the estimated cost of compiling it (the builder
    /// starts the costly ones first). The header is not compiled on its own.</summary>
    public sealed record SplitUnit(string Name, string Text, SplitUnitKind Kind, long Cost);

    /// <summary>The split mode as the configuration and the switches give it.</summary>
    public static class SplitModes
    {
        /// <summary>`auto`/`on`/`off` (also `true`/`false`, `1`/`0`); null: not one of them.</summary>
        public static SplitMode? Parse(string? text) => text?.Trim().ToLowerInvariant() switch
        {
            "auto" or "" => SplitMode.Auto,
            "on" or "true" or "1" or "yes" => SplitMode.On,
            "off" or "false" or "0" or "no" => SplitMode.Off,
            _ => null,
        };

        /// <summary>FIRE_SPLIT=1/0 (the tests): forces the mode, before the configuration; null: not set.</summary>
        public static SplitMode? FromEnvironment => Environment.GetEnvironmentVariable("FIRE_SPLIT") is { Length: > 0 } v ? Parse(v) is SplitMode.Off ? SplitMode.Off : SplitMode.On : null;

        /// <summary>The toolchain can compile units on their own and link them (msvc is not done yet - it is not tested here; the drivers of their own - Open Watcom - and `custom` cannot; `files` writes them).</summary>
        public static bool ToolchainCan(string? kind) => kind is "gcc" or "clang" or "tcc" or "emscripten" or "files";

        /// <summary>`auto` splits with these (a large program compiled with an optimizing compiler; TinyCC compiles any program in a second).</summary>
        public static bool AutoToolchain(string? kind) => kind is "gcc" or "clang" or "emscripten";
    }

    /// <summary>
    /// Puts the text of a program into several translation units (docs/spec/NATIVE_SPLIT.md, phase 1). The generator writes the text as for one file, with marks
    /// between its parts (<see cref="Marker"/>); the passes over the whole text have run. This splits it: the head and the declarations go to the common header, the
    /// data, `fire_init` and the entry point to unit 0 (with the state of the runtime), the helpers to helper units, the bodies of the fire functions to function
    /// units (grouped by their key - module, source, line - and cut by estimated cost), the natives in C of the packages to units of their own. A symbol that is used
    /// in another unit than the one that defines it loses its `static` for <c>FIRE_PS</c> (hidden visibility) and is declared in the header; everything else stays
    /// `static`. Constants compared by address (strings, type arguments, tables) have one definition, the others see an `extern` declaration.
    /// </summary>
    internal static class ProgramSplit
    {
        public const string MarkPrefix = "//@@fire-split ";
        public static string Marker(string what) => MarkPrefix + what + "@@";

        /// <summary>A program whose C is at least this big is split by `auto` (about 50 000 lines; compiling it with -O2 takes some 30 s on one core).</summary>
        public const int AutoMinBytes = 2 * 1024 * 1024;

        /// <summary>The estimated cost of a unit of functions (C without the `#line` lines and comments); FIRE_SPLIT_UNIT_KB sets another (the tests: small units, every
        /// border is crossed once). Fixed - not from the number of cores: the same program gets the same units on every machine.</summary>
        public static long UnitCost => Environment.GetEnvironmentVariable("FIRE_SPLIT_UNIT_KB") is { } kb && long.TryParse(kb, out long k) && k > 0 ? k * 1024 : 600 * 1024;

        public const string HeaderName = "fire_program.h";

        /// <summary>The factor of the estimated cost of a helper against that of a fire function (see the helper units).</summary>
        private const int HelperWeight = 2;

        /// <summary>A fire function as the generator wrote it after a `func` mark: its module (a new module starts a new unit once the unit has half its cost) and the
        /// key it is sorted by.</summary>
        internal sealed record FuncKey(string Module, string Key);

        /// <summary>A native in C that code of the program calls: the group of its unit, the function and its C signature.</summary>
        internal sealed record NativeFn(string Group, string Function, string Return, IReadOnlyList<string> Params);

        private enum K { Pre, Func, Proto, Data, Type, Trivia, Group }

        private sealed class Item
        {
            public int Start, End;
            public K Kind;
            public List<string> Names = new();
            public bool Static, Inline, Extern;
            public int Body = -1;   // (a function: its `{`)
            public List<Item>? Children;   // (a group: #if ... #endif)
            public HashSet<string> Uses = new(StringComparer.Ordinal);
            public long Cost;
            public string Src = "";
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------
        // Scanning the C text into top-level items
        // ------------------------------------------------------------------------------------------------------------------------------------------------

        private static bool IdStart(char c) => c == '_' || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        private static bool IdChar(char c) => IdStart(c) || (c >= '0' && c <= '9');

        /// <summary>The end of the preprocessor line that starts at <paramref name="i"/> (after its newline; continued lines included).</summary>
        private static int LineEnd(string s, int i)
        {
            while (true)
            {
                int e = s.IndexOf('\n', i);
                if (e < 0) return s.Length;
                if (e > 0 && s[e - 1] == '\\' || e > 1 && s[e - 1] == '\r' && s[e - 2] == '\\') { i = e + 1; continue; }
                return e + 1;
            }
        }

        private static int SkipString(string s, int i)
        {
            char q = s[i++];
            while (i < s.Length && s[i] != q) { if (s[i] == '\\') i++; else if (s[i] == '\n') break; i++; }
            return Math.Min(s.Length, i + 1);
        }

        private static int SkipComment(string s, int i)
        {
            if (s[i + 1] == '/') { int e = s.IndexOf('\n', i); return e < 0 ? s.Length : e; }
            int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
            return end < 0 ? s.Length : end + 2;
        }

        /// <summary>The top-level items of a C text: preprocessor lines, function definitions, declarations and definitions ending with `;`; leading whitespace and
        /// comments belong to the item after them; what follows the last one is a trivia item.</summary>
        private static List<Item> Scan(string s)
        {
            var items = new List<Item>();
            int i = 0, n = s.Length, start = 0, depth = 0, par = 0, brk = 0, firstParen = -1, body = -1;
            bool lineStart = true, code = false, eq = false, isFunc = false;
            string? first = null;
            void Reset(int at) { start = at; code = false; eq = false; isFunc = false; firstParen = -1; body = -1; first = null; brk = 0; }
            while (i < n)
            {
                char c = s[i];
                if (lineStart)
                {
                    int j = i;
                    while (j < n && (s[j] == ' ' || s[j] == '\t')) j++;
                    if (j < n && s[j] == '#')
                    {
                        int e = LineEnd(s, j);
                        if (depth == 0 && par == 0 && !code) { items.Add(new Item { Start = start, End = e, Kind = K.Pre }); Reset(e); }
                        i = e; lineStart = true; continue;
                    }
                    lineStart = false;
                }
                if (c == '\n') { lineStart = true; i++; continue; }
                if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || c == '\v') { i++; continue; }
                if (c == '/' && i + 1 < n && (s[i + 1] == '/' || s[i + 1] == '*')) { i = SkipComment(s, i); continue; }
                code = true;
                if (c == '"' || c == '\'') { i = SkipString(s, i); continue; }
                if (IdStart(c))
                {
                    int e = i + 1;
                    while (e < n && IdChar(s[e])) e++;
                    if (first == null && depth == 0 && par == 0) first = s.Substring(i, e - i);
                    i = e; continue;
                }
                if (c >= '0' && c <= '9') { i++; while (i < n && (IdChar(s[i]) || s[i] == '.')) i++; continue; }
                switch (c)
                {
                    case '(': if (depth == 0 && par == 0 && brk == 0 && firstParen < 0) firstParen = i; par++; break;
                    case ')': par--; break;
                    case '[': if (depth == 0) brk++; break;
                    case ']': if (depth == 0) brk--; break;
                    case '=': if (depth == 0 && par == 0 && brk == 0) eq = true; break;
                    case '{':
                        if (depth == 0 && par == 0)
                        {
                            isFunc = !eq && firstParen >= 0 && first is not ("typedef" or "struct" or "union" or "enum");
                            body = i;
                        }
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0 && par == 0 && isFunc)
                        {
                            int end = Trailing(s, i + 1);
                            items.Add(new Item { Start = start, End = end, Kind = K.Func, Body = body });
                            Reset(end);
                            i = end;
                            continue;
                        }
                        break;
                    case ';':
                        if (depth == 0 && par == 0)
                        {
                            var kind = first is "typedef" or "struct" or "union" or "enum" ? K.Type : firstParen >= 0 && !eq ? K.Proto : K.Data;
                            int end = Trailing(s, i + 1);
                            items.Add(new Item { Start = start, End = end, Kind = kind });
                            Reset(end);
                            i = end;
                            continue;
                        }
                        break;
                }
                i++;
            }
            if (start < n) items.Add(new Item { Start = start, End = n, Kind = code ? K.Data : K.Trivia });
            foreach (var item in items) { item.Src = s; Describe(item); }
            return Group(items);
        }

        /// <summary>The end of an item with the comment after it on the same line (`static Value SF2 = ...; // Word.Bits`): the comment stays with it.</summary>
        private static int Trailing(string s, int i)
        {
            int e = i;
            while (e < s.Length && (s[e] == ' ' || s[e] == '\t')) e++;
            if (e + 1 < s.Length && s[e] == '/' && s[e + 1] == '/') { int nl = s.IndexOf('\n', e); return nl < 0 ? s.Length : nl; }
            if (e + 1 < s.Length && s[e] == '/' && s[e + 1] == '*')
            {
                int close = s.IndexOf("*/", e + 2, StringComparison.Ordinal), nl = s.IndexOf('\n', e);
                if (close >= 0 && (nl < 0 || close < nl)) return close + 2;
            }
            return i;
        }

        /// <summary>Folds `#if ... #endif` at the top level into group items (with their lines as children).</summary>
        private static List<Item> Group(List<Item> items)
        {
            var result = new List<Item>();
            var stack = new Stack<Item>();
            foreach (var item in items)
            {
                string? directive = item.Kind == K.Pre ? Directive(item) : null;
                if (directive is "if" or "ifdef" or "ifndef")
                {
                    var g = new Item { Start = item.Start, Kind = K.Group, Children = new List<Item> { item }, Src = item.Src };
                    stack.Push(g);
                    continue;
                }
                if (stack.Count > 0)
                {
                    var g = stack.Peek();
                    g.Children!.Add(item);
                    if (directive == "endif")
                    {
                        stack.Pop();
                        g.End = item.End;
                        if (stack.Count > 0) stack.Peek().Children!.Add(g); else result.Add(g);
                        // (the group was added to its parent when it closed: the item itself was added above as its last child)
                    }
                    continue;
                }
                result.Add(item);
            }
            while (stack.Count > 0) { var g = stack.Pop(); g.End = g.Children!.Last().End; result.Add(g); }   // (an unclosed #if: keep what it has)
            // a group added to its parent twice? (above: the closing item goes to the group, the group to the parent) - fine
            return result;
        }

        /// <summary>The directive of a preprocessor item (`if`, `define`, `line`, ...).</summary>
        private static string Directive(Item item)
        {
            string s = item.Src;
            int i = item.Start;
            while (i < item.End && s[i] != '#') i++;
            i++;
            while (i < item.End && (s[i] == ' ' || s[i] == '\t')) i++;
            int e = i;
            while (e < item.End && IdChar(s[e])) e++;
            return s.Substring(i, e - i);
        }

        /// <summary>The names an item declares or defines, whether it is `static`/`inline`, its uses (identifiers outside strings and comments, without its own names)
        /// and its cost (the characters of its code).</summary>
        private static void Describe(Item item)
        {
            string s = item.Src;
            if (item.Kind is K.Func or K.Proto or K.Data or K.Type)
            {
                // the declaration specifiers and declarators: identifiers at depth 0 (before the body / initializers)
                var heads = new List<(string Id, int At)>();
                int depth = 0, par = 0, limit = item.Kind == K.Func ? item.Body : item.End;
                bool skipInit = false;
                var declarators = new List<string>();
                string? lastId = null;
                for (int i = item.Start; i < limit;)
                {
                    char c = s[i];
                    if (c == '/' && i + 1 < limit && (s[i + 1] == '/' || s[i + 1] == '*')) { i = SkipComment(s, i); continue; }
                    if (c == '"' || c == '\'') { i = SkipString(s, i); continue; }
                    if (c == '#' && (i == item.Start || s[i - 1] == '\n')) { i = LineEnd(s, i); continue; }
                    if (IdStart(c))
                    {
                        int e = i + 1;
                        while (e < limit && IdChar(s[e])) e++;
                        string id = s.Substring(i, e - i);
                        if (depth == 0 && par == 0 && !skipInit) { heads.Add((id, i)); if (id != "const" && id != "volatile") lastId = id; }
                        else if (depth == 0 && par == 1 && item.Kind is K.Data && !skipInit && lastId == null) { }
                        i = e; continue;
                    }
                    switch (c)
                    {
                        case '(':
                            // (a function or prototype: the name is the identifier before the first parenthesis)
                            if (depth == 0 && par == 0 && item.Kind is K.Func or K.Proto && declarators.Count == 0 && lastId != null) declarators.Add(lastId);
                            par++; break;
                        case ')': par--; break;
                        case '{': depth++; break;
                        case '}': depth--; break;
                        case '[': depth++; break;
                        case ']': depth--; break;
                        case '=': if (depth == 0 && par == 0) { if (item.Kind == K.Data && lastId != null && !skipInit) declarators.Add(lastId); skipInit = true; } break;
                        case ',': if (depth == 0 && par == 0 && item.Kind == K.Data) { if (!skipInit && lastId != null) declarators.Add(lastId); skipInit = false; lastId = null; } break;
                        case ';': if (depth == 0 && par == 0 && item.Kind == K.Data && !skipInit && lastId != null) declarators.Add(lastId); break;
                    }
                    i++;
                }
                if (item.Kind == K.Type)
                {
                    // typedef ... Name; - the last identifier at depth 0; struct Tag { } ; - the tag
                    if (heads.Count > 0 && heads[0].Id == "typedef") { if (lastId != null) declarators.Add(lastId); }
                    else if (heads.Count > 1) declarators.Add(heads[1].Id);
                }
                item.Names = declarators.Distinct().ToList();
                foreach (var (id, _) in heads)
                {
                    if (id == "static") item.Static = true;
                    if (id == "inline" || id == "__inline") item.Inline = true;
                    if (id == "extern") item.Extern = true;
                }
            }
            CollectUses(item);
        }

        private static void CollectUses(Item item)
        {
            string s = item.Src;
            long cost = 0;
            var own = item.Names;
            for (int i = item.Start; i < item.End;)
            {
                char c = s[i];
                if (c == '\n') { i++; continue; }
                if (c == '/' && i + 1 < item.End && (s[i + 1] == '/' || s[i + 1] == '*')) { i = SkipComment(s, i); continue; }
                if (c == '"' || c == '\'') { int e = SkipString(s, i); cost += e - i; i = e; continue; }
                if (c == '#' && LineStartAt(s, i))
                {
                    int e = LineEnd(s, i);
                    // (#define: its body may name symbols; #line, #include, #pragma, #if: none of the program)
                    int d = i + 1;
                    while (d < e && (s[d] == ' ' || s[d] == '\t')) d++;
                    if (string.CompareOrdinal(s, d, "define", 0, 6) != 0) { i = e; continue; }
                    i = d + 6; continue;
                }
                if (IdStart(c))
                {
                    int e = i + 1;
                    while (e < item.End && IdChar(s[e])) e++;
                    // (a member after `.`/`->`: not a symbol)
                    int b = i - 1;
                    while (b >= item.Start && (s[b] == ' ' || s[b] == '\t')) b--;
                    bool member = b >= item.Start && (s[b] == '.' || (s[b] == '>' && b > item.Start && s[b - 1] == '-'));
                    if (!member)
                    {
                        string id = s.Substring(i, e - i);
                        if (!own.Contains(id)) item.Uses.Add(id);
                    }
                    cost += e - i;
                    i = e; continue;
                }
                if (c >= '0' && c <= '9') { int e = i + 1; while (e < item.End && (IdChar(s[e]) || s[e] == '.')) e++; cost += e - i; i = e; continue; }
                if (c != ' ' && c != '\t' && c != '\r') cost++;
                i++;
            }
            item.Cost = cost;
        }

        private static bool LineStartAt(string s, int i)
        {
            int b = i - 1;
            while (b >= 0 && (s[b] == ' ' || s[b] == '\t')) b--;
            return b < 0 || s[b] == '\n';
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------
        // Declarations
        // ------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The text of the item from its first code (without the comments and blank lines before it).</summary>
        private static int CodeStart(Item item)
        {
            string s = item.Src;
            int i = item.Start;
            while (i < item.End)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
                if (c == '/' && i + 1 < item.End && (s[i + 1] == '/' || s[i + 1] == '*')) { i = SkipComment(s, i); continue; }
                break;
            }
            return i;
        }

        /// <summary>`static` becomes FIRE_PS (and an exported function is not inline: other units call it).</summary>
        private static string Exported(string head, bool function)
        {
            var sb = new StringBuilder(head.Length + 8);
            bool replaced = false;
            for (int i = 0; i < head.Length;)
            {
                char c = head[i];
                if (c == '/' && i + 1 < head.Length && (head[i + 1] == '/' || head[i + 1] == '*')) { int e = SkipComment(head, i); sb.Append(head, i, e - i); i = e; continue; }
                if (c == '"' || c == '\'') { int e = SkipString(head, i); sb.Append(head, i, e - i); i = e; continue; }
                if (IdStart(c))
                {
                    int e = i + 1;
                    while (e < head.Length && IdChar(head[e])) e++;
                    string id = head.Substring(i, e - i);
                    if (id == "static" && !replaced) { sb.Append("FIRE_PS"); replaced = true; }
                    else if (function && (id == "inline" || id == "FIRE_ALWAYS_INLINE"))
                    {
                        // (the word and one space after it)
                        if (e < head.Length && head[e] == ' ') e++;
                    }
                    else sb.Append(id);
                    i = e; continue;
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>The definition of an exported item as its unit writes it.</summary>
        private static string ExportedDefinition(Item item)
        {
            string s = item.Src;
            if (item.Kind == K.Func) return Exported(s.Substring(item.Start, item.Body - item.Start), true) + s.Substring(item.Body, item.End - item.Body);
            if (item.Kind == K.Data)
            {
                int eq = InitializerStart(item);
                return Exported(s.Substring(item.Start, eq - item.Start), false) + s.Substring(eq, item.End - eq);
            }
            if (item.Kind == K.Proto) return Exported(s.Substring(item.Start, item.End - item.Start), true);
            return s.Substring(item.Start, item.End - item.Start);
        }

        private static int InitializerStart(Item item)
        {
            string s = item.Src;
            int depth = 0;
            for (int i = item.Start; i < item.End;)
            {
                char c = s[i];
                if (c == '/' && i + 1 < item.End && (s[i + 1] == '/' || s[i + 1] == '*')) { i = SkipComment(s, i); continue; }
                if (c == '"' || c == '\'') { i = SkipString(s, i); continue; }
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (c == '=' && depth == 0) return i;
                i++;
            }
            return item.End;
        }

        /// <summary>The declaration of an item in the header: a function its prototype, data an `extern` declaration (without the initializers); `exported`: it is
        /// FIRE_PS, otherwise it keeps its storage (a static prototype).</summary>
        private static string Declaration(Item item, bool exported)
        {
            string s = item.Src;
            int from = CodeStart(item);
            switch (item.Kind)
            {
                case K.Func:
                {
                    string head = s.Substring(from, item.Body - from).TrimEnd();
                    return (exported ? Exported(head, true) : head) + ";";
                }
                case K.Proto:
                {
                    string text = s.Substring(from, item.End - from);
                    return exported ? Exported(text, true) : text;
                }
                case K.Data:
                {
                    // the declarators without their initializers
                    var sb = new StringBuilder();
                    int depth = 0;
                    bool skip = false;
                    for (int i = from; i < item.End;)
                    {
                        char c = s[i];
                        if (c == '/' && i + 1 < item.End && (s[i + 1] == '/' || s[i + 1] == '*')) { i = SkipComment(s, i); continue; }
                        if (c == '"' || c == '\'') { int e = SkipString(s, i); if (!skip) sb.Append(s, i, e - i); i = e; continue; }
                        if (c == '(' || c == '[' || c == '{') depth++;
                        else if (c == ')' || c == ']' || c == '}') depth--;
                        if (depth == 0 && c == '=') { skip = true; while (sb.Length > 0 && sb[^1] == ' ') sb.Length--; i++; continue; }
                        if (depth == 0 && (c == ',' || c == ';')) skip = false;
                        if (!skip) sb.Append(c == '\n' ? ' ' : c);
                        i++;
                    }
                    string decl = sb.ToString();
                    if (item.Static) return Exported(decl, false).Replace("FIRE_PS", "FIRE_PS extern");
                    return "extern " + decl;
                }
                default:
                    return s.Substring(item.Start, item.End - item.Start);
            }
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------
        // The split
        // ------------------------------------------------------------------------------------------------------------------------------------------------

        private sealed class Place
        {
            public required string Name;
            public required SplitUnitKind Kind;
            public readonly List<Item> Items = new();
            public readonly HashSet<string> Uses = new(StringComparer.Ordinal);
            public long Cost;
            public string? NativesText;
            public string? Group;
        }

        /// <summary>The units of the text (the marks of <see cref="Marker"/> in it). <paramref name="funcs"/>: the key of each `func N` mark; <paramref name="natives"/>:
        /// the natives in C the code may call; <paramref name="resolveLines"/>: the last step on each file (its `#line` markers, with its name).</summary>
        public static List<SplitUnit> Split(string text, IReadOnlyList<FuncKey> funcs, IReadOnlyList<NativeFn> natives, Func<string, string, string> resolveLines, long unitCost)
        {
            // ---- the parts between the marks
            string? head = null, p1 = null, tail = null;
            var nativeBlocks = new List<(string Group, string Text)>();
            var regions = new List<(int Func, string Text)>();
            var atomics = new List<string>();
            {
                int pos = 0;
                string current = "head";
                string? arg = null;
                var buffer = new StringBuilder();
                var p1Parts = new StringBuilder();
                void Flush()
                {
                    string t = buffer.ToString();
                    buffer.Clear();
                    switch (current)
                    {
                        case "head": head = t; break;
                        case "natives": nativeBlocks.Add((arg!, t)); break;
                        case "program": p1Parts.Append(t); break;
                        case "atomic": atomics.Add(t); p1Parts.Append(Marker("atomic " + (atomics.Count - 1))).Append('\n'); break;
                        case "func": regions.Add((int.Parse(arg!), t)); break;
                        case "tail": tail = t; break;
                    }
                }
                while (pos < text.Length)
                {
                    int e = text.IndexOf('\n', pos);
                    int next = e < 0 ? text.Length : e + 1;
                    if (string.CompareOrdinal(text, pos, MarkPrefix, 0, MarkPrefix.Length) == 0)
                    {
                        string mark = text.Substring(pos + MarkPrefix.Length, next - pos - MarkPrefix.Length).TrimEnd('\n', '\r');
                        mark = mark.EndsWith("@@", StringComparison.Ordinal) ? mark.Substring(0, mark.Length - 2) : mark;
                        Flush();
                        int sp = mark.IndexOf(' ');
                        string word = sp < 0 ? mark : mark.Substring(0, sp);
                        arg = sp < 0 ? null : mark.Substring(sp + 1);
                        current = word == "atomic-end" ? "program" : word == "head-end" ? "program" : word;
                        pos = next;
                        continue;
                    }
                    buffer.Append(text, pos, next - pos);
                    pos = next;
                }
                Flush();
                p1 = p1Parts.ToString();
            }
            head ??= "";
            p1 ??= "";
            tail ??= "";

            var header = new Place { Name = HeaderName, Kind = SplitUnitKind.Header };
            var unit0 = new Place { Name = CppGenerator.ProgramFileName, Kind = SplitUnitKind.Runtime };
            var helperUnits = new List<Place>();
            var funcUnits = new List<Place>();
            var nativesUnits = new List<Place>();

            // ---- the head: everything before the program, the same in every unit (the state of the runtime only in unit 0)
            var headLines = head.Split('\n').ToList();
            bool rtImpl = headLines.RemoveAll(l => l.StartsWith("#define FIRE_RT_IMPLEMENTATION 1", StringComparison.Ordinal)) > 0;
            string headText = string.Join("\n", headLines);

            // ---- the program before the functions
            var p1Items = Scan(p1);
            bool json = false;
            var helperFuncs = new List<Item>();
            // (in order: 0 - in the header as it is (a prototype: its declaration), 1 - a static inline function defined in the header, 2 - in a unit, its declaration here)
            var headerItems = new List<(Item Item, int Mode)>();
            Item? atomicItem = null;
            var atomicItems = new List<Item>();
            foreach (var item in p1Items)
            {
                if (item.Kind == K.Pre)
                {
                    string line = p1.Substring(item.Start, item.End - item.Start);
                    if (line.Contains("#define FIRE_JSON_IMPLEMENTATION 1", StringComparison.Ordinal)) { json = true; continue; }
                    if (line.Contains("#include \"fire_json.h\"", StringComparison.Ordinal)) { json = true; continue; }
                    if (line.TrimStart().StartsWith(MarkPrefix + "atomic ", StringComparison.Ordinal))
                    {
                        string a = line.Trim();
                        int k = int.Parse(a.Substring((MarkPrefix + "atomic ").Length).TrimEnd('@'));
                        var inner = Scan(atomics[k]);
                        atomicItems.AddRange(inner);
                        foreach (var ai in inner) headerItems.Add((ai, 2));
                        continue;
                    }
                    headerItems.Add((item, 0));
                    continue;
                }
                // (an atomic mark is a comment line: it is the leading trivia of the next item or a trivia item)
                int at = item.Src.IndexOf(MarkPrefix + "atomic ", item.Start, item.End - item.Start, StringComparison.Ordinal);
                if (at >= 0)
                {
                    int e = item.Src.IndexOf("@@", at + MarkPrefix.Length, StringComparison.Ordinal);
                    int k = int.Parse(item.Src.Substring(at + (MarkPrefix + "atomic ").Length, e - at - (MarkPrefix + "atomic ").Length));
                    var inner = Scan(atomics[k]);
                    atomicItems.AddRange(inner);
                    foreach (var ai in inner) headerItems.Add((ai, 2));
                }
                switch (item.Kind)
                {
                    case K.Trivia: break;
                    case K.Group:
                        if (item.Children!.All(ch => ch.Kind is K.Pre or K.Type or K.Proto or K.Trivia)) headerItems.Add((item, 0));
                        else { unit0.Items.Add(item); headerItems.Add((item, 2)); }
                        break;
                    case K.Func:
                        if (item.Static && item.Inline) headerItems.Add((item, 1));
                        else { helperFuncs.Add(item); headerItems.Add((item, 2)); }
                        break;
                    case K.Proto: case K.Type: headerItems.Add((item, 0)); break;
                    case K.Data when item.Extern && InitializerStart(item) == item.End: headerItems.Add((item, 0)); break;   // (a declaration)
                    default: unit0.Items.Add(item); headerItems.Add((item, 2)); break;
                }
            }
            if (atomicItems.Count > 0)
            {
                var all = atomicItems;
                atomicItem = new Item { Kind = K.Group, Children = all, Src = "" };
            }

            // helper units: the helper functions in their order, cut by cost (the inline c of the program in the first, all of it together)
            {
                Place? current = null;
                foreach (var item in helperFuncs)
                {
                    if (current == null || current.Cost >= unitCost) { current = new Place { Name = $"fire_program_h{helperUnits.Count + 1}.c", Kind = SplitUnitKind.Helpers }; helperUnits.Add(current); }
                    current.Items.Add(item);
                    // (a helper costs about twice what a fire function of the same size costs: dispatchers and reflection are switches over the classes, with the field
                    // helpers inlined into them - measured with the assistant at -O2)
                    current.Cost += item.Cost * HelperWeight;
                }
                if (atomicItem != null)
                {
                    if (helperUnits.Count == 0) helperUnits.Add(new Place { Name = "fire_program_h1.c", Kind = SplitUnitKind.Helpers });
                    helperUnits[0].Items.InsertRange(0, atomicItem.Children!);
                    helperUnits[0].Cost += atomicItem.Children!.Sum(c => c.Cost);
                }
            }

            // ---- the functions: grouped by key, cut by cost (a module starts a unit once the unit has half of it)
            var regionItems = regions.Select(r => (Key: r.Func < funcs.Count ? funcs[r.Func] : new FuncKey("", r.Func.ToString("D8")), Items: Scan(r.Text), Order: r.Func)).ToList();
            var sorted = regionItems.OrderBy(r => r.Key.Module, StringComparer.Ordinal).ThenBy(r => r.Key.Key, StringComparer.Ordinal).ThenBy(r => r.Order).ToList();
            {
                Place? current = null;
                string? module = null;
                foreach (var r in sorted)
                {
                    long cost = r.Items.Sum(x => x.Cost);
                    bool newModule = module != null && r.Key.Module != module;
                    if (current == null || current.Cost >= unitCost || newModule && current.Cost >= unitCost / 2)
                    {
                        current = new Place { Name = $"fire_program_{funcUnits.Count + 1}.c", Kind = SplitUnitKind.Functions };
                        funcUnits.Add(current);
                    }
                    module = r.Key.Module;
                    current.Items.AddRange(r.Items);
                    current.Cost += cost;
                }
            }

            // ---- the tail: unit 0
            foreach (var item in Scan(tail)) unit0.Items.Add(item);

            // ---- the natives in C: a unit per group
            foreach (var group in nativeBlocks.GroupBy(b => b.Group))
            {
                string name = new string(group.Key.Select(ch => IdChar(ch) ? ch : '_').ToArray());
                var place = new Place { Name = $"fire_natives_{name}.c", Kind = SplitUnitKind.Natives, NativesText = string.Concat(group.Select(g => g.Text)), Group = group.Key };
                place.Cost = place.NativesText.Length / 2;
                nativesUnits.Add(place);
            }

            // ---- who defines what, who uses what
            // (the static inline helpers once, for a build without optimization: in the last helper unit - the smallest -, which counts their cost)
            long inlineCost = headerItems.Where(x => x.Mode == 1).Sum(x => x.Item.Cost);
            if (inlineCost > 0 && helperUnits.Count == 0) helperUnits.Add(new Place { Name = "fire_program_h1.c", Kind = SplitUnitKind.Helpers });
            if (inlineCost > 0) helperUnits[^1].Cost += inlineCost;
            var units = new List<Place> { unit0 };
            units.AddRange(helperUnits);
            units.AddRange(funcUnits);
            var definedIn = new Dictionary<string, Place>(StringComparer.Ordinal);
            void Defines(Place p, Item item)
            {
                if (item.Kind == K.Group) { foreach (var ch in item.Children!) Defines(p, ch); return; }
                if (item.Kind is K.Func || item.Kind is K.Data && !(item.Extern && InitializerStart(item) == item.End)) foreach (var name in item.Names) definedIn.TryAdd(name, p);
            }
            void UsesOf(Place p, Item item)
            {
                if (item.Kind == K.Group) { foreach (var ch in item.Children!) UsesOf(p, ch); return; }
                p.Uses.UnionWith(item.Uses);
            }
            foreach (var u in units) foreach (var item in u.Items) { Defines(u, item); UsesOf(u, item); }
            foreach (var (item, mode) in headerItems) if (mode != 2) UsesOf(header, item);
            // (the header's own prototypes name what they declare: not a use)
            // types: a type of a function unit or of the tail that another unit uses moves to the header (with what it needs, until nothing more moves)
            var movedTypes = new List<Item>();
            var typeIn = new Dictionary<string, (Place Place, Item Item)>(StringComparer.Ordinal);
            foreach (var u in units) foreach (var item in u.Items) if (item.Kind == K.Type) foreach (var name in item.Names) typeIn.TryAdd(name, (u, item));
            for (bool moved = true; moved;)
            {
                moved = false;
                foreach (var (name, (place, item)) in typeIn.ToList())
                {
                    if (!place.Items.Contains(item)) continue;
                    bool used = header.Uses.Contains(name) || units.Any(u => u != place && u.Uses.Contains(name));
                    if (!used) continue;
                    place.Items.Remove(item);
                    movedTypes.Add(item);
                    header.Uses.UnionWith(item.Uses);
                    moved = true;
                }
            }
            // the natives the program calls
            var programUses = new HashSet<string>(header.Uses, StringComparer.Ordinal);
            foreach (var u in units) programUses.UnionWith(u.Uses);
            var calledNatives = natives.Where(n => programUses.Contains(n.Function)).GroupBy(n => n.Function).Select(g => g.First()).ToList();

            // exported: defined in one place, used in another (the header counts as every unit)
            var exported = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, place) in definedIn)
                if (header.Uses.Contains(name) || units.Any(u => u != place && u.Uses.Contains(name))) exported.Add(name);

            bool IsExported(Item item) => item.Names.Any(exported.Contains);

            var inlineDefs = new Dictionary<string, Item>(StringComparer.Ordinal);
            foreach (var (item, mode) in headerItems) if (mode == 1) foreach (var n in item.Names) inlineDefs.TryAdd(n, item);
            string inlineHome = helperUnits.Count > 0 ? helperUnits[^1].Name : unit0.Name;

            // ---- the header
            var h = new StringBuilder();
            h.Append("// The declarations of the program, shared by its translation units (docs/spec/NATIVE_SPLIT.md) - generated, do not edit.\n");
            h.Append("#ifndef FIRE_PROGRAM_H\n#define FIRE_PROGRAM_H\n");
            h.Append("#define FIRE_SPLIT 1   // the program is several units: what the platform keeps once lives in unit 0 (fire_rt.h FIRE_ONCE)\n");
            h.Append(headText);
            if (!headText.EndsWith('\n')) h.Append('\n');
            h.Append("/* a symbol of the program that other units of it use: visible in the program, not exported (also not with -rdynamic) */\n");
            // (gcc stops looking for misleading indentation in a file as large as one with the whole program - in a unit it finds the guards the generator writes
            // on one line with the statement after them)
            h.Append("#if defined(__GNUC__) || defined(__clang__)\n#pragma GCC diagnostic ignored \"-Wmisleading-indentation\"\n#endif\n");
            h.Append("#if (defined(__GNUC__) || defined(__clang__)) && !defined(_WIN32) && !defined(__CYGWIN__)\n#define FIRE_PS __attribute__((visibility(\"hidden\")))\n#else\n#define FIRE_PS\n#endif\n");
            // (the static inline helpers: an optimizing build has them in each unit that uses them - inlined like in one file -, a build without optimization (-O0, a
            // debug build, TinyCC) once, in a helper unit: it inlines nothing, and thousands of copies cost time and size)
            h.Append("#if defined(__OPTIMIZE__) && !defined(FIRE_SPLIT_NO_INLINE)\n#define FIRE_SPLIT_INLINE 1\n#else\n#define FIRE_SPLIT_INLINE 0\n#endif\n");
            h.Append("#ifndef FIRE_NATIVES_UNIT\n");
            if (json) h.Append("#include \"fire_json.h\"\n");
            if (calledNatives.Count > 0)
            {
                h.Append("/* the natives in C, in units of their own (fire_natives_*.c): called through these */\n");
                foreach (var nf in calledNatives)
                    h.Append($"FIRE_PS {nf.Return} fire_cn_{nf.Function}({(nf.Params.Count == 0 ? "void" : string.Join(", ", nf.Params))});\n#define {nf.Function} fire_cn_{nf.Function}\n");
            }
            var declared = new HashSet<string>(StringComparer.Ordinal);
            // (a declaration starts on a line of its own: what is before it may end in a // comment)
            void NewLine() { if (h.Length > 0 && h[^1] != '\n') h.Append('\n'); }
            void HeaderItem(Item item, bool definition)
            {
                if (item.Kind == K.Group)
                {
                    foreach (var ch in item.Children!) HeaderItem(ch, definition);
                    return;
                }
                // (a static inline helper of the program - the field helpers: thousands of them - is defined in each unit that uses it, the header only declares it)
                if (definition) return;   // (see below)
                if (item.Kind == K.Proto && item.Names.Count > 0 && item.Names.All(inlineDefs.ContainsKey)) return;
                if (item.Kind is K.Pre or K.Type or K.Trivia) { h.Append(item.Src, item.Start, item.End - item.Start); foreach (var n in item.Names) declared.Add(n); return; }
                bool ex = IsExported(item);
                if (item.Kind == K.Proto) { NewLine(); h.Append(Declaration(item, ex)).Append('\n'); foreach (var n in item.Names) declared.Add(n); return; }
                h.Append(item.Src, item.Start, item.End - item.Start);
            }
            foreach (var (item, mode) in headerItems)
                if (mode == 2) Declare(item);
                else HeaderItem(item, mode == 1);
            h.Append('\n');
            if (inlineDefs.Count > 0)
            {
                var defs = headerItems.Where(x => x.Mode == 1).Select(x => x.Item).ToList();
                h.Append("/* the static inline helpers of the program (FIRE_SPLIT_INLINE: in every unit that uses them; else once, in ").Append(inlineHome).Append(") */\n#if FIRE_SPLIT_INLINE\n");
                foreach (var d in defs) { NewLine(); h.Append(Declaration(d, false)).Append('\n'); }
                h.Append("#else\n");
                foreach (var d in defs) { NewLine(); h.Append(Declaration(d, true)).Append('\n'); }
                h.Append("#endif\n");
            }
            if (movedTypes.Count > 0)
            {
                h.Append("/* types of the functions that other units use */\n");
                foreach (var t in movedTypes) h.Append(t.Src, t.Start, t.End - t.Start).Append('\n');
            }
            // the declarations of the exported definitions that have no prototype in the header (data, functions of the units and the tail); a group keeps its #if
            h.Append("/* what the units of the program define for each other */\n");
            void Declare(Item item)
            {
                if (item.Kind == K.Group)
                {
                    if (!item.Children!.Any(ch => ch.Kind is K.Func or K.Data && IsExported(ch) || ch.Kind == K.Group)) return;
                    foreach (var ch in item.Children!)
                        if (ch.Kind == K.Pre) { NewLine(); h.Append(ch.Src, ch.Start, ch.End - ch.Start); }
                        else Declare(ch);
                    return;
                }
                if (item.Kind is not (K.Func or K.Data) || !IsExported(item) || item.Names.All(declared.Contains)) return;
                NewLine();
                h.Append(Declaration(item, true)).Append('\n');
                foreach (var n in item.Names) declared.Add(n);
            }
            foreach (var u in units) foreach (var item in u.Items) Declare(item);
            h.Append("#endif /* FIRE_NATIVES_UNIT */\n#endif /* FIRE_PROGRAM_H */\n");

            // ---- the units
            var result = new List<SplitUnit> { new SplitUnit(HeaderName, resolveLines(h.ToString(), HeaderName), SplitUnitKind.Header, 0) };
            string UnitText(Place p)
            {
                var sb = new StringBuilder();
                if (p == unit0)
                {
                    // (the head of the one file - the target, the switches of the runtime, what the build links - without its includes: unit 0 says what the program is
                    // like the one file does; the header defines the same again, a macro defined twice the same way is no error)
                    foreach (var line in headLines)
                        if (line.StartsWith("// ", StringComparison.Ordinal) || line.StartsWith("#define ", StringComparison.Ordinal)) sb.Append(line).Append('\n');
                    if (rtImpl) sb.Append("#define FIRE_RT_IMPLEMENTATION 1   // the runtime's global state lives in this file\n");
                    if (json) sb.Append("#define FIRE_JSON_IMPLEMENTATION 1   // the JSON engine too\n");
                }
                sb.Append("#include \"").Append(HeaderName).Append("\"\n");
                // the static inline helpers this unit uses (and those they use), in the order of the program
                var need = new HashSet<Item>();
                var work = new Stack<Item>();
                foreach (var id in p.Uses) if (inlineDefs.TryGetValue(id, out var d) && need.Add(d)) work.Push(d);
                while (work.Count > 0)
                    foreach (var id in work.Pop().Uses)
                        if (inlineDefs.TryGetValue(id, out var d) && need.Add(d)) work.Push(d);
                if (need.Count > 0)
                {
                    sb.Append("#if FIRE_SPLIT_INLINE   /* the helpers of the program that this unit uses */");
                    foreach (var (item, mode) in headerItems)
                        if (mode == 1 && need.Contains(item)) { if (item.Src[item.Start] != '\n') sb.Append('\n'); sb.Append(item.Src, item.Start, item.End - item.Start); }
                    sb.Append("\n#endif\n");
                }
                void Write(Item item)
                {
                    if (item.Kind == K.Group)
                    {
                        foreach (var ch in item.Children!) Write(ch);
                        return;
                    }
                    // (items of different places meet here: each starts on a line of its own)
                    if (sb.Length > 0 && sb[^1] != '\n' && item.Src[item.Start] != '\n') sb.Append('\n');
                    if (item.Static && item.Kind is K.Func or K.Data or K.Proto && IsExported(item)) sb.Append(ExportedDefinition(item));
                    else sb.Append(item.Src, item.Start, item.End - item.Start);
                    // (a function with lines of the fire sources: the lines after it are this file's again - its own marker may have stayed with the item after it)
                    if (item.Kind == K.Func && item.Src.IndexOf("#line ", item.Start, item.End - item.Start, StringComparison.Ordinal) >= 0) sb.Append('\n').Append(CppGenerator.LineResetMark).Append('\n');
                }
                foreach (var item in p.Items) Write(item);
                if (p.Name == inlineHome && inlineDefs.Count > 0)
                {
                    sb.Append("\n#if !FIRE_SPLIT_INLINE   /* the static inline helpers of the program, once (a build without optimization) */\n");
                    foreach (var (item, mode) in headerItems)
                        if (mode == 1) sb.Append(Exported(item.Src.Substring(item.Start, item.Body - item.Start), true)).Append(item.Src, item.Body, item.End - item.Body).Append('\n');
                    sb.Append("#endif\n");
                }
                sb.Append('\n');
                return sb.ToString();
            }
            result.Add(new SplitUnit(unit0.Name, resolveLines(UnitText(unit0), unit0.Name), SplitUnitKind.Runtime, unit0.Items.Sum(i => i.Cost)));
            foreach (var p in helperUnits) result.Add(new SplitUnit(p.Name, resolveLines(UnitText(p), p.Name), p.Kind, p.Cost));
            foreach (var p in funcUnits) result.Add(new SplitUnit(p.Name, resolveLines(UnitText(p), p.Name), p.Kind, p.Cost));
            foreach (var p in nativesUnits)
            {
                var sb = new StringBuilder();
                sb.Append("#define FIRE_NATIVES_UNIT 1   // the natives in C of the program: the configuration of the runtime, not the declarations of the program\n");
                sb.Append("#include \"").Append(HeaderName).Append("\"\n");
                sb.Append(p.NativesText);
                var mine = calledNatives.Where(n => n.Group == p.Group).ToList();
                if (mine.Count > 0)
                {
                    sb.Append("\n/* the natives the program calls (its units see them under these names) */\n");
                    foreach (var nf in mine)
                    {
                        var ps = nf.Params.Select((t, i) => t == "OwnList*" ? $"{t} list" : $"{t} a{i}").ToList();
                        var args = nf.Params.Select((t, i) => t == "OwnList*" ? "list" : $"a{i}");
                        sb.Append($"FIRE_PS {nf.Return} fire_cn_{nf.Function}({(ps.Count == 0 ? "void" : string.Join(", ", ps))}) {{ {(nf.Return == "void" ? "" : "return ")}{nf.Function}({string.Join(", ", args)}); }}\n");
                    }
                }
                result.Add(new SplitUnit(p.Name, sb.ToString(), SplitUnitKind.Natives, p.Cost));
            }
            return result;
        }
    }
}
