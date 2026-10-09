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
            "if", "else", "while", "for", "foreach", "in", "on", "new", "base", "this",
            "try", "catch", "finally", "throw", "is", "of", "from", "under",
            "extern", "unsafe", "interface", "readonly", "enum",
            "public", "private", "protected",
            "namespace",
            "with", "extends", "switch", "case", "default", "break", "continue", "where",
            "fire", "taking", "sync", "flat", "copy", "take", "leave", "terminate", "actor", "process", "operator",
            "true", "false", "undefined", "and", "or",
        };

        private static readonly string[] TypeKeywords = { "bool", "int", "float", "char", "string", "byte" };

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

            return baseScore + matchOffset + ((1.0f - matchOffset - baseScore) * (float)current.Length / (float)candidate.Length);
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

            return GetIdentifierSuggestions(offset, index, idPrefix);
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
                // `$string` & co.: the collective classes of the base-type extensions are not types to write down.
                if (cls.Name.StartsWith('$')) continue;
                if ((cls.IsInterface && !includeInterfaces) || !MatchesPrefix(cls.SimpleName, prefix)) continue;
                if (index.ResolveClassKey(cls.SimpleName, context, lenient: false) != cls.Name) continue;
                string detail = cls.Namespace.Length > 0 ? $"{(cls.IsInterface ? "Interface" : "Class")} in {cls.Namespace}" : (cls.IsInterface ? "Interface" : "Class");
                results.Add(new CompletionItem(cls.SimpleName, CompletionKind.ClassName, CompareKeywords(prefix, cls.SimpleName), detail) { Documentation = cls.Documentation });
            }

            if (!includeEnums) return;
            foreach (var key in index.EnumMembers.Keys)
            {
                string simple = key.Substring(key.LastIndexOf('.') + 1);
                if (!MatchesPrefix(simple, prefix) || index.ResolveEnumKey(simple, context) != key) continue;
                results.Add(new CompletionItem(simple, CompletionKind.EnumName, CompareKeywords(prefix, simple),
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

        private static bool IsListable(MemberInfo m) =>
            m.Kind != MemberKind.Constructor && !m.Name.StartsWith("operator", StringComparison.Ordinal);

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
                        if (!IsListable(m) || !MatchesPrefix(m.Name, prefix) || !IsVisible(m, fromClass, index)) continue;
                        bool staticOk = receiver.Kind == TypeKind.Static ? m.IsStatic : (!m.IsStatic || receiver.ViaThis);
                        if (!staticOk) continue;
                        // Own members before inherited ones, nearer base before more distant.
                        float baseScore = Math.Max(0.1f, 0.3f - 0.05f * depth);
                        results.Add(ToItem(m, prefix, baseScore, showOwner: depth > 0));
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
                            if (m.Kind == MemberKind.Method && IsListable(m) && MatchesPrefix(m.Name, prefix) && IsVisible(m, fromClass, index))
                                results.Add(ToItem(m, prefix, 0.3f, showOwner: false));
                    return Dedupe(results);

                default:
                    // Type not determinable - as the best possible fallback offer accessible
                    // members of ALL known classes (with the class name
                    // behind it, so that you can see where a suggestion comes from),
                    // instead of suggesting nothing at all.
                    foreach (var cls in index.Classes.Values)
                        foreach (var m in cls.Members)
                            if (IsListable(m) && MatchesPrefix(m.Name, prefix) && IsVisible(m, fromClass, index))
                                results.Add(ToItem(m, prefix, cls.IsFromPrelude ? 0f : 0.05f, showOwner: true));
                    return Dedupe(results);
            }
        }

        private static List<CompletionItem> GetIdentifierSuggestions(int offset, ScriptSymbolIndex index, string prefix)
        {
            var results = new List<CompletionItem>();

            foreach (var kw in Keywords)
                if (MatchesPrefix(kw, prefix)) results.Add(new CompletionItem(kw, CompletionKind.Keyword, CompareKeywords(prefix, kw)));
            foreach (var kw in TypeKeywords)
                if (MatchesPrefix(kw, prefix)) results.Add(new CompletionItem(kw, CompletionKind.TypeKeyword, CompareKeywords(prefix, kw)));

            // Types that are addressable here without qualification, and the
            // top-level namespaces (for 'Namespace.Class').
            AddVisibleTypes(results, offset, index, prefix, includeInterfaces: true, includeEnums: true);
            AddTopLevelNamespaces(results, index, prefix);

            foreach (var (name, _) in index.EnclosingFunctionParams(offset))
                if (MatchesPrefix(name, prefix)) results.Add(new CompletionItem(name, CompletionKind.Parameter, CompareKeywords(prefix, name)));

            foreach (var name in index.LocalVarsBeforeCursor(string.IsNullOrEmpty(prefix) ? offset : offset - prefix.Length))
                if (MatchesPrefix(name, prefix)) results.Add(new CompletionItem(name, CompletionKind.Variable, CompareKeywords(prefix, name)));

            string? enclosingClass = index.EnclosingClassAt(offset);
            if (enclosingClass != null)
                foreach (var (m, depth) in index.MembersOfWithDepth(enclosingClass))
                    if (IsListable(m) && MatchesPrefix(m.Name, prefix) && IsVisible(m, enclosingClass, index))
                        results.Add(ToItem(m, prefix, Math.Max(0.05f, 0.15f - 0.03f * depth), showOwner: depth > 0));

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
