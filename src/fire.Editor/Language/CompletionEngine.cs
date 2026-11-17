using System;
using System.Collections.Generic;
using System.Linq;

namespace fire.Editor
{
    public enum CompletionKind
    {
        Keyword,
        TypeKeyword,
        ClassName,
        EnumName,
        EnumMember,
        Namespace,
        Field,
        Method,
        Property,
        Variable,
        Parameter,
    }

    public sealed record CompletionItem(string Text, CompletionKind Kind, float Score, string? Detail = null)
    {
        /// <summary>The `///` documentation of the class/member this item stands for (shown as a tooltip next to the list).</summary>
        public DocComment? Documentation { get; init; }

        public string Display => Detail != null ? $"{Text}  {Detail}" : Text;
    }

    /// <summary>
    /// Determines completion suggestions at a cursor position, on the
    /// basis of ScriptSymbolIndex. Two modes: after a '.' (member
    /// completion, see the class comment there for the limits of
    /// type recognition with dynamic typing) or general identifier
    /// completion (keywords, type keywords, class/enum names,
    /// parameters/local variables of the enclosing function, members of the
    /// enclosing class, all otherwise known names as a fallback).
    /// </summary>
    public static class CompletionEngine
    {
        private static readonly string[] Keywords =
        {
            "var", "func", "class", "construct", "destruct", "return",
            "if", "else", "while", "do", "for", "foreach", "in", "on", "new", "base", "this",
            "try", "catch", "finally", "throw", "is", "of", "from", "under",
            "extern", "unsafe", "interface", "readonly", "enum", "override",
            "public", "private", "protected",
            "namespace",
            "with", "extends", "switch", "case", "default", "break", "continue", "where",
            "fire", "await", "dedicated", "until", "taking", "handover", "sync", "flat", "copy", "take", "replace", "leave", "terminate", "actor", "process", "operator", "defined", "trust",
            "true", "false", "undefined", "and", "or",
            "unit", "prefixed", "unprefixed", "coerce",
        };

        private static readonly string[] TypeKeywords = { "bool", "int", "float", "char", "string", "byte", "word", "any" };

        public static string? GetCurrentKeyword(string source, int offset)
        {
            if (offset < 0 || offset > source.Length) return null;
            
            int i = offset - 1;
            while (i >= 0 && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i--;
            
            return source.Substring(i + 1, offset - i - 1);
        }

        public static float CompareKeywords(string? current, string? candidate, float baseScore = 0f)
        {
            if (current == null || candidate == null) 
                return 0f;

            var matchOffset = 0f;

            if (candidate.StartsWith(current, StringComparison.OrdinalIgnoreCase))
                matchOffset = 0.35f;
            else if (!candidate.Contains(current, StringComparison.OrdinalIgnoreCase))
                return 0f;

            // what was typed with exactly this case (`Te` -> `Text` before `template`) comes first
            float exactCase = current.Length > 0 && candidate.StartsWith(current, StringComparison.Ordinal) ? 0.05f : 0f;
            return baseScore + matchOffset + exactCase + ((1.0f - matchOffset - baseScore) * (float)current.Length / (float)candidate.Length);
        }

        /// <summary>Suggestions for the cursor position `offset` in `source`.
        /// `index` must have been built for the SAME `source`.</summary>
        public static List<CompletionItem> GetSuggestions(string source, int offset, ScriptSymbolIndex index)
        {
            if (offset < 0 || offset > source.Length) return new List<CompletionItem>();

            int i = offset - 1;

            // '#using Namespace' - only namespaces come into question here.
            var usingLine = UsingLinePrefix.Match(source.Substring(LineStart(source, offset), offset - LineStart(source, offset)));
            if (usingLine.Success)
                return GetUsingSuggestions(index, usingLine.Groups[1].Value);

            // Directly after a '.' (nothing of the member name typed yet).
            if (i >= 0 && source[i] == '.')
                return GetMemberSuggestions(source, offset, index, dotOffset: i, string.Empty);

            int start = i;
            while (start >= 0 && (char.IsLetterOrDigit(source[start]) || source[start] == '_')) start--;

            // Backwards to the start of the identifier currently being typed.
            if (start >= 0 && source[start] == '.')
            {
                string prefix = source.Substring(start + 1, offset - start - 1);
                return GetMemberSuggestions(source, offset, index, dotOffset: start, prefix);
            }

            string idPrefix = start + 1 <= offset ? source.Substring(start + 1, offset - start - 1) : string.Empty;

            // After 'new ' comes a class name, nothing else.
            if (IsAfterNew(source, start))
                return GetClassNameSuggestions(offset, index, idPrefix);

            var suggestions = GetIdentifierSuggestions(offset, index, idPrefix);
            AddNamedArgumentSuggestions(suggestions, source, start, index, idPrefix);
            return suggestions;
        }

        /// <summary>At the start of an argument (`f(1, |` or `new Box(|`): the parameters of the called method or constructor as `name: ` (named
        /// arguments, docs/language/08-classes.md), the ones already named left out.</summary>
        private static void AddNamedArgumentSuggestions(List<CompletionItem> results, string source, int beforeWord, ScriptSymbolIndex index, string prefix)
        {
            int k = beforeWord;
            while (k >= 0 && source[k] is ' ' or '\t') k--;
            if (k < 0 || source[k] is not ('(' or ',')) return;
            // the opening parenthesis of this argument list (nested parentheses, brackets and braces skipped; strings roughly)
            int depth = 0;
            int open = -1;
            for (int j = k; j >= 0 && j > k - 4000; j--)
            {
                char ch = source[j];
                if (ch is ')' or ']' or '}') depth++;
                else if (ch is '(' or '[' or '{')
                {
                    if (depth == 0) { if (ch == '(') open = j; break; }
                    depth--;
                }
            }
            if (open < 0) return;
            string args = source.Substring(open + 1, Math.Max(0, beforeWord + 1 - (open + 1)));
            var given = System.Text.RegularExpressions.Regex.Matches(args, @"(?:^|,)\s*([A-Za-z_][A-Za-z0-9_]*):\s").Select(m => m.Groups[1].Value).ToHashSet();
            int nameEnd = open - 1;
            while (nameEnd >= 0 && source[nameEnd] is ' ' or '\t') nameEnd--;
            int nameStart = nameEnd;
            while (nameStart >= 0 && (char.IsLetterOrDigit(source[nameStart]) || source[nameStart] is '_' or '.')) nameStart--;
            string callee = source.Substring(nameStart + 1, nameEnd - nameStart);
            if (callee.Length == 0) return;
            IEnumerable<MemberInfo> members;
            if (IsAfterNew(source, nameStart))
            {
                if (index.TryFindClass(callee, open) is not { } cls) return;
                members = index.MembersOfWithDepth(cls).Where(x => x.Depth == 0 && x.Member.Kind == MemberKind.Constructor).Select(x => x.Member);
            }
            else
            {
                int dot = callee.LastIndexOf('.');
                string method = dot >= 0 ? callee.Substring(dot + 1) : callee;
                string? cls = null;
                if (dot >= 0)
                {
                    var receiver = index.ResolveReceiver(nameStart + 1 + dot);
                    if (receiver.Kind is TypeKind.Instance or TypeKind.Static) cls = receiver.Name;
                }
                else cls = index.EnclosingClassAt(open);
                if (cls == null) return;
                members = index.MembersOfWithDepth(cls).Where(x => x.Member.Kind == MemberKind.Method && x.Member.Name == method).Select(x => x.Member);
            }
            string? fromClass = index.EnclosingClassAt(open);
            members = members.Where(m => IsVisible(m, fromClass, index));
            foreach (var name in members.SelectMany(m => m.ParamNames).Distinct())
                if (!given.Contains(name) && MatchesPrefix(name, prefix))
                    results.Add(new CompletionItem(name + ": ", CompletionKind.Parameter, CompareKeywords(prefix, name, 0.4f), "named argument"));
        }

        // The text BEFORE the cursor in a '#using' line (group 1: what has already been typed
        // behind '#using ', possibly with dots).
        private static readonly System.Text.RegularExpressions.Regex UsingLinePrefix =
            new(@"^[ \t]*#using[ \t]+([A-Za-z0-9_.]*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static int LineStart(string source, int offset)
        {
            int j = offset;
            while (j > 0 && source[j - 1] != '\n') j--;
            return j;
        }

        /// <summary>`#using |`: the next namespace section behind what
        /// is already there (`Geo` -> top-level namespaces with this
        /// beginning, `Geometry.` -> their sub-namespaces) - always only ONE
        /// section, because the editor on insertion only replaces the text behind the
        /// last '.'.</summary>
        private static List<CompletionItem> GetUsingSuggestions(ScriptSymbolIndex index, string typed)
        {
            int dot = typed.LastIndexOf('.');
            string parent = dot < 0 ? string.Empty : typed.Substring(0, dot);
            string prefix = dot < 0 ? typed : typed.Substring(dot + 1);

            var results = new List<CompletionItem>();
            foreach (var member in index.MembersOfNamespace(parent))
                if (member.Kind == NamespaceMemberKind.Namespace && MatchesPrefix(member.Name, prefix))
                    results.Add(new CompletionItem(member.Name, CompletionKind.Namespace, CompareKeywords(prefix, member.Name, 0.3f), $"Namespace {member.FullName}"));
            return Dedupe(results);
        }

        /// <summary>Is the word `new` in front of the identifier that
        /// ends at `identifierStart - 1` (`start` = index of the character BEFORE it)?</summary>
        /// <summary>Is the word `new` in front of the (possibly qualified) name `A.B.` whose
        /// last dot lies at `dotOffset`?</summary>
        private static bool IsQualifiedNameAfterNew(string source, int dotOffset)
        {
            int j = dotOffset - 1;
            while (j >= 0 && (char.IsLetterOrDigit(source[j]) || source[j] == '_' || source[j] == '.')) j--;
            return IsAfterNew(source, j);
        }

        private static bool IsAfterNew(string source, int start)
        {
            int j = start;
            while (j >= 0 && (source[j] == ' ' || source[j] == '\t')) j--;
            if (j == start) return false; // no whitespace between 'new' and the identifier
            int wordEnd = j;
            while (j >= 0 && (char.IsLetterOrDigit(source[j]) || source[j] == '_')) j--;
            return source.Substring(j + 1, wordEnd - j) == "new";
        }

        /// <summary>After `new `: the classes that are addressable at `offset` without qualification
        /// (in the current namespace, via `#using` or without
        /// namespace - no interfaces), plus the top-level namespaces,
        /// to continue `new Namespace.Class(...)`.</summary>
        private static List<CompletionItem> GetClassNameSuggestions(int offset, ScriptSymbolIndex index, string prefix)
        {
            var results = new List<CompletionItem>();
            AddVisibleTypes(results, offset, index, prefix, includeInterfaces: false, includeEnums: false);
            AddTopLevelNamespaces(results, index, prefix);
            return Dedupe(results);
        }

        /// <summary>Adds the classes (and, if applicable, interfaces/enums) whose
        /// SIMPLE name at `offset` means exactly them - i.e. not hidden by a
        /// class of the same name in another namespace and reachable via
        /// the current namespace or a `#using`. Classes in
        /// other namespaces thus appear only after `Namespace.`.</summary>
        private static void AddVisibleTypes(
            List<CompletionItem> results, int offset, ScriptSymbolIndex index, string prefix, bool includeInterfaces, bool includeEnums)
        {
            var context = index.ContextAt(offset);
            foreach (var cls in index.Classes.Values)
            {
                // `$string` & co.: the collective classes of the base-type extensions are not types to write down; `__Await` & co.: helpers of the runtime.
                if (cls.Name.StartsWith('$') || cls.SimpleName.StartsWith("__", StringComparison.Ordinal)) continue;
                if ((cls.IsInterface && !includeInterfaces) || !MatchesPrefix(cls.SimpleName, prefix)) continue;
                if (index.ResolveClassKey(cls.SimpleName, context, lenient: false) != cls.Name) continue;
                string detail = cls.Namespace.Length > 0 ? $"{(cls.IsInterface ? "Interface" : "Class")} in {cls.Namespace}" : (cls.IsInterface ? "Interface" : "Class");
                // the classes of the program itself before those of the libraries
                results.Add(new CompletionItem(cls.SimpleName, CompletionKind.ClassName, CompareKeywords(prefix, cls.SimpleName, cls.IsFromPrelude ? 0f : OwnTypeScore), detail) { Documentation = cls.Documentation });
            }

            if (!includeEnums) return;
            foreach (var key in index.EnumMembers.Keys)
            {
                string simple = key.Substring(key.LastIndexOf('.') + 1);
                if (!MatchesPrefix(simple, prefix) || index.ResolveEnumKey(simple, context) != key) continue;
                results.Add(new CompletionItem(simple, CompletionKind.EnumName, CompareKeywords(prefix, simple, index.EnumPreludes.ContainsKey(key) ? 0f : OwnTypeScore),
                    key.Contains('.') ? $"Enum in {key.Substring(0, key.LastIndexOf('.'))}" : "Enum"));
            }
        }

        private static void AddTopLevelNamespaces(List<CompletionItem> results, ScriptSymbolIndex index, string prefix)
        {
            foreach (var member in index.MembersOfNamespace(string.Empty))
                if (member.Kind == NamespaceMemberKind.Namespace && MatchesPrefix(member.Name, prefix))
                    results.Add(new CompletionItem(member.Name, CompletionKind.Namespace, CompareKeywords(prefix, member.Name), "Namespace"));
        }

        /// <summary>May code in the class `fromClass` (null = outside any
        /// class) access `m`? `private`: only the declaring
        /// class, `protected`: also its derived classes.</summary>
        private static bool IsVisible(MemberInfo m, string? fromClass, ScriptSymbolIndex index) => m.Access switch
        {
            MemberAccess.Private => fromClass == m.Owner,
            MemberAccess.Protected => fromClass != null && (fromClass == m.Owner || index.DerivesFrom(fromClass, m.Owner)),
            _ => true,
        };

        private static bool IsListable(MemberInfo m, ScriptSymbolIndex index) =>
            m.Kind != MemberKind.Constructor && !m.Name.StartsWith("operator", StringComparison.Ordinal)
            && !m.Name.StartsWith("__", StringComparison.Ordinal)   // (internals of the prelude: `__NextEntry` of the hash tables)
            && !(IsGeneratorHelper(m.Name) && index.Classes.TryGetValue(m.Owner, out var owner) && owner.IsGenerated);

        /// <summary>The names the markup generator gives its own helpers (FireUiGenerator: `fxBindAll`, `fxConv_Upper`, `fxEl_3`, `fxHandles0`, ...) - in
        /// a generated class they are no API: the code-behind knows only the names it chose itself (docs/ui/markup.md).</summary>
        private static bool IsGeneratorHelper(string name) =>
            name.Length > 2 && name.StartsWith("fx", StringComparison.Ordinal) && (char.IsUpper(name[2]) || name[2] == '_');

        /// <summary>A member of a library class (prelude, package) whose name starts with a small letter or `_`: the libraries mark their public
        /// API with capitals (`Text`, `Width`, `Add`) and keep their state and helpers in small ones (`cw`, `styled`, `dirtyX0`). The language does
        /// not hide them (everything without `private`/`protected` is public), so they stay in the list - behind the API.</summary>
        private static bool IsLibraryInternal(MemberInfo m, ScriptSymbolIndex index) =>
            m.Name.Length > 0 && (char.IsLower(m.Name[0]) || m.Name[0] == '_')
            && index.Classes.TryGetValue(m.Owner, out var owner) && owner.IsFromPrelude;

        /// <summary>The base score of a member: the class itself before its bases (the nearer one first), the API of a library before its
        /// internals.</summary>
        private static float MemberBaseScore(MemberInfo m, int depth, ScriptSymbolIndex index)
        {
            float score = Math.Max(0.1f, 0.3f - 0.05f * depth);
            return IsLibraryInternal(m, index) ? score - 0.1f : score;
        }

        /// <summary>The base score of the classes and enums of the program itself (before those of the libraries, `0`).</summary>
        private const float OwnTypeScore = 0.1f;

        /// <summary>Suggestions after `expression.` - the type of the expression is
        /// derived (see ScriptSymbolIndex.ResolveReceiver) and then ONLY
        /// its members are offered (incl. inherited, without inaccessible
        /// `private`/`protected`, for a class `Name.` only
        /// static ones, for an instance only non-static ones). Only if
        /// the type cannot be determined at all (dynamic typing) does
        /// this fall back to members of ALL known classes.</summary>
        private static List<CompletionItem> GetMemberSuggestions(
            string source, int offset, ScriptSymbolIndex index, int dotOffset, string prefix)
        {
            var results = new List<CompletionItem>();
            var receiver = index.ResolveReceiver(dotOffset);
            string? fromClass = index.EnclosingClassAt(offset);

            switch (receiver.Kind)
            {
                case TypeKind.Namespace:
                    {
                        // 'Namespace.' - sub-namespaces, classes, interfaces and
                        // enums in it; behind 'new Namespace.' only what can be
                        // instantiated (classes) or leads further (namespaces).
                        bool afterNew = IsQualifiedNameAfterNew(source, dotOffset);
                        foreach (var member in index.MembersOfNamespace(receiver.Name!))
                        {
                            if (!MatchesPrefix(member.Name, prefix)) continue;
                            if (afterNew && member.Kind is NamespaceMemberKind.Interface or NamespaceMemberKind.Enum) continue;
                            var (kind, detail) = member.Kind switch
                            {
                                NamespaceMemberKind.Namespace => (CompletionKind.Namespace, $"Namespace {member.FullName}"),
                                NamespaceMemberKind.Interface => (CompletionKind.ClassName, "Interface"),
                                NamespaceMemberKind.Enum => (CompletionKind.EnumName, "Enum"),
                                _ => (CompletionKind.ClassName, "Class"),
                            };
                            results.Add(new CompletionItem(member.Name, kind, CompareKeywords(prefix, member.Name, 0.3f), detail));
                        }
                        return Dedupe(results);
                    }

                case TypeKind.Enum:
                    if (index.EnumMembers.TryGetValue(receiver.Name!, out var enumMembers))
                        foreach (var name in enumMembers)
                            if (MatchesPrefix(name, prefix))
                                results.Add(new CompletionItem(name, CompletionKind.EnumMember, CompareKeywords(prefix, name, 0.3f), $"Enum {receiver.Name}"));
                    return Dedupe(results);

                case TypeKind.Instance:
                case TypeKind.Static:
                    foreach (var (m, depth) in index.MembersOfWithDepth(receiver.Name!))
                    {
                        if (!IsListable(m, index) || !MatchesPrefix(m.Name, prefix) || !IsVisible(m, fromClass, index)) continue;
                        bool staticOk = receiver.Kind == TypeKind.Static ? m.IsStatic : (!m.IsStatic || receiver.ViaThis);
                        if (!staticOk) continue;
                        // Own members before inherited ones, nearer base before more distant, the API of a library before its internals.
                        results.Add(ToItem(m, prefix, MemberBaseScore(m, depth, index), showOwner: depth > 0));
                    }
                    return Dedupe(results);

                case TypeKind.Primitive:
                case TypeKind.Array:
                    // Simple values/arrays have no class members, but built-in ones
                    // (`text.Length`, `text.IndexOf(...)`, see BuiltinMembers).
                    foreach (var member in BuiltinMembers.For(receiver))
                    {
                        if (!MatchesPrefix(member.Name, prefix)) continue;
                        string type = member.ReturnType?.Replace("buffer", "byte[]") ?? string.Empty;
                        string detail = member.IsProperty
                            ? $"Property : {type}"
                            : $"({member.Signature}){(type.Length > 0 ? " → " + type : string.Empty)}";
                        results.Add(new CompletionItem(member.Name,
                            member.IsProperty ? CompletionKind.Property : CompletionKind.Method,
                            CompareKeywords(prefix, member.Name, 0.3f), detail));
                    }

                    // Methods from `class extends string { ... }` (prelude and own extensions).
                    if (BuiltinMembers.ExtensionClassOf(receiver) is { } extensionKey && index.Classes.ContainsKey(extensionKey))
                        foreach (var (m, _) in index.MembersOfWithDepth(extensionKey))
                            if (m.Kind == MemberKind.Method && IsListable(m, index) && MatchesPrefix(m.Name, prefix) && IsVisible(m, fromClass, index))
                                results.Add(ToItem(m, prefix, 0.3f, showOwner: false));
                    return Dedupe(results);

                default:
                    // Type not determinable (a value of type `any`, a call of something unknown) - as the best possible
                    // fallback offer the accessible instance members of the classes of the PROGRAM (with the class name behind
                    // it, so that you can see where a suggestion comes from) instead of suggesting nothing at all. Those of all
                    // the libraries would be a thousand names (`#import "ui"`): they come only when the program has none that
                    // fit what was typed, and only their API.
                    AddFallbackMembers(results, index, prefix, fromClass, fromLibraries: false);
                    if (results.Count == 0 && prefix.Length > 0)
                        AddFallbackMembers(results, index, prefix, fromClass, fromLibraries: true);
                    return Dedupe(results);
            }
        }

        private static void AddFallbackMembers(List<CompletionItem> results, ScriptSymbolIndex index, string prefix, string? fromClass, bool fromLibraries)
        {
            foreach (var cls in index.Classes.Values)
            {
                if (cls.IsFromPrelude != fromLibraries || cls.Name.StartsWith('$') || cls.SimpleName.StartsWith("__", StringComparison.Ordinal)) continue;
                foreach (var m in cls.Members)
                    if (IsListable(m, index) && !m.IsStatic && MatchesPrefix(m.Name, prefix) && IsVisible(m, fromClass, index) && !IsLibraryInternal(m, index))
                        results.Add(ToItem(m, prefix, 0f, showOwner: true));
            }
        }

        private static List<CompletionItem> GetIdentifierSuggestions(int offset, ScriptSymbolIndex index, string prefix)
        {
            var results = new List<CompletionItem>();

            // Order (by the base score): parameters and variables, the members of the class, the types of the program and the
            // keywords, the types of the libraries.
            foreach (var kw in Keywords)
                if (MatchesPrefix(kw, prefix)) results.Add(new CompletionItem(kw, CompletionKind.Keyword, CompareKeywords(prefix, kw, 0.05f)));
            foreach (var kw in TypeKeywords)
                if (MatchesPrefix(kw, prefix)) results.Add(new CompletionItem(kw, CompletionKind.TypeKeyword, CompareKeywords(prefix, kw, 0.05f)));

            // Types that are addressable here without qualification, and the
            // top-level namespaces (for 'Namespace.Class').
            AddVisibleTypes(results, offset, index, prefix, includeInterfaces: true, includeEnums: true);
            AddTopLevelNamespaces(results, index, prefix);

            foreach (var (name, _) in index.EnclosingFunctionParams(offset))
                if (MatchesPrefix(name, prefix)) results.Add(new CompletionItem(name, CompletionKind.Parameter, CompareKeywords(prefix, name, 0.25f)));

            foreach (var name in index.LocalVarsBeforeCursor(string.IsNullOrEmpty(prefix) ? offset : offset - prefix.Length))
                if (MatchesPrefix(name, prefix)) results.Add(new CompletionItem(name, CompletionKind.Variable, CompareKeywords(prefix, name, 0.25f)));

            string? enclosingClass = index.EnclosingClassAt(offset);
            if (enclosingClass != null)
            {
                // in a static method there is no `this`: only the static members are reachable without a qualifier
                bool staticOnly = index.IsInStaticMethod(offset);
                foreach (var (m, depth) in index.MembersOfWithDepth(enclosingClass))
                    if (IsListable(m, index) && MatchesPrefix(m.Name, prefix) && IsVisible(m, enclosingClass, index) && (!staticOnly || m.IsStatic))
                        results.Add(ToItem(m, prefix, Math.Max(0.1f, 0.2f - 0.03f * depth) - (IsLibraryInternal(m, index) ? 0.1f : 0f), showOwner: depth > 0));
            }

            //foreach (var name in index.AllDeclaredNames)
            //    if (MatchesPrefix(name, prefix)) results.Add(new CompletionItem(name, CompletionKind.Variable, CompareKeywords(prefix, name)));

            return Dedupe(results);
        }

        private static bool MatchesPrefix(string candidate, string prefix) =>
            prefix.Length == 0 || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        private static CompletionItem ToItem(MemberInfo m, string prefix, float baseScore, bool showOwner)
        {
            string type = m.TypeName != null ? m.TypeName + (m.TypeIsArray ? "[]" : "") : string.Empty;
            string owner = showOwner ? $"  [{m.Owner}]" : string.Empty;
            string modifiers = (m.IsStatic ? "static " : string.Empty) + m.Access switch
            {
                MemberAccess.Private => "private ",
                MemberAccess.Protected => "protected ",
                _ => string.Empty,
            };

            var kind = m.Kind switch
            {
                MemberKind.Method => CompletionKind.Method,
                MemberKind.Property => CompletionKind.Property,
                _ => CompletionKind.Field,
            };
            string detail = m.Kind switch
            {
                MemberKind.Method => $"{modifiers}({m.Signature}){(type.Length > 0 ? " → " + type : string.Empty)}{owner}",
                MemberKind.Property => $"{modifiers}Property{(type.Length > 0 ? " : " + type : string.Empty)}{owner}",
                _ => $"{modifiers}Field{(type.Length > 0 ? " : " + type : string.Empty)}{owner}",
            };
            return new CompletionItem(m.Name, kind, CompareKeywords(prefix, m.Name, baseScore), detail) { Documentation = m.Documentation };
        }

        private static List<CompletionItem> Dedupe(List<CompletionItem> items) =>
            items.GroupBy(i => (i.Text, i.Kind))
                 .Select(g => g.OrderByDescending(i => i.Score).First())
                 .OrderBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
                 .ToList();
    }
}
