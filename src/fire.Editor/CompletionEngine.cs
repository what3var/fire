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
        public string Display => Detail != null ? $"{Text}  {Detail}" : Text;
    }

    /// <summary>
    /// Ermittelt Vervollständigungs-Vorschläge an einer Cursor-Position, auf
    /// Basis von ScriptSymbolIndex. Zwei Modi: nach einem '.' (Member-
    /// Vervollständigung, siehe Klassen-Kommentar dort für die Grenzen der
    /// Typ-Erkennung bei dynamischer Typisierung) oder allgemeine Bezeichner-
    /// Vervollständigung (Keywords, Typ-Keywords, Klassen-/Enum-Namen,
    /// Parameter/lokale Variablen der umschließenden Funktion, Mitglieder der
    /// umschließenden Klasse, alle sonst bekannten Namen als Fallback).
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
            "fire", "taking", "sync", "flat", "leave", "terminate", "actor", "process", "operator",
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

        /// <summary>Vorschläge für die Cursor-Position `offset` in `source`.
        /// `index` muss für DASSELBE `source` gebaut worden sein.</summary>
        public static List<CompletionItem> GetSuggestions(string source, int offset, ScriptSymbolIndex index)
        {
            if (offset < 0 || offset > source.Length) return new List<CompletionItem>();

            int i = offset - 1;

            // '#using Namespace' - hier kommen nur Namespaces in Frage.
            var usingLine = UsingLinePrefix.Match(source.Substring(LineStart(source, offset), offset - LineStart(source, offset)));
            if (usingLine.Success)
                return GetUsingSuggestions(index, usingLine.Groups[1].Value);

            // Direkt nach einem '.' (noch nichts vom Mitgliedsnamen getippt).
            if (i >= 0 && source[i] == '.')
                return GetMemberSuggestions(source, offset, index, dotOffset: i, string.Empty);

            int start = i;
            while (start >= 0 && (char.IsLetterOrDigit(source[start]) || source[start] == '_')) start--;

            // Rückwärts bis zum Anfang des aktuell getippten Bezeichners.
            if (start >= 0 && source[start] == '.')
            {
                string prefix = source.Substring(start + 1, offset - start - 1);
                return GetMemberSuggestions(source, offset, index, dotOffset: start, prefix);
            }

            string idPrefix = start + 1 <= offset ? source.Substring(start + 1, offset - start - 1) : string.Empty;

            // Nach 'new ' kommt ein Klassenname, sonst nichts.
            if (IsAfterNew(source, start))
                return GetClassNameSuggestions(offset, index, idPrefix);

            return GetIdentifierSuggestions(offset, index, idPrefix);
        }

        // Der Text VOR dem Cursor in einer '#using'-Zeile (Gruppe 1: schon Getipptes
        // hinter '#using ', evtl. mit Punkten).
        private static readonly System.Text.RegularExpressions.Regex UsingLinePrefix =
            new(@"^[ \t]*#using[ \t]+([A-Za-z0-9_.]*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static int LineStart(string source, int offset)
        {
            int j = offset;
            while (j > 0 && source[j - 1] != '\n') j--;
            return j;
        }

        /// <summary>`#using |`: der nächste Namespace-Abschnitt hinter dem, was
        /// schon dasteht (`Geo` -> Namespaces auf oberster Ebene mit diesem
        /// Anfang, `Geometry.` -> deren Unter-Namespaces) - jeweils nur EIN
        /// Abschnitt, weil der Editor beim Einfügen nur den Text hinter dem
        /// letzten '.' ersetzt.</summary>
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

        /// <summary>Steht vor dem Bezeichner, der bei `identifierStart - 1`
        /// endet (`start` = Index des Zeichens DAVOR), das Wort `new`?</summary>
        /// <summary>Steht vor dem (evtl. qualifizierten) Namen `A.B.`, dessen
        /// letzter Punkt bei `dotOffset` liegt, das Wort `new`?</summary>
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
            if (j == start) return false; // kein Leerraum zwischen 'new' und dem Bezeichner
            int wordEnd = j;
            while (j >= 0 && (char.IsLetterOrDigit(source[j]) || source[j] == '_')) j--;
            return source.Substring(j + 1, wordEnd - j) == "new";
        }

        /// <summary>Nach `new `: die Klassen, die an `offset` ohne Qualifizierung
        /// ansprechbar sind (im aktuellen Namespace, per `#using` oder ohne
        /// Namespace - keine Interfaces), dazu die Namespaces oberster Ebene,
        /// um `new Namespace.Klasse(...)` fortzusetzen.</summary>
        private static List<CompletionItem> GetClassNameSuggestions(int offset, ScriptSymbolIndex index, string prefix)
        {
            var results = new List<CompletionItem>();
            AddVisibleTypes(results, offset, index, prefix, includeInterfaces: false, includeEnums: false);
            AddTopLevelNamespaces(results, index, prefix);
            return Dedupe(results);
        }

        /// <summary>Fügt die Klassen (und ggf. Interfaces/Enums) hinzu, deren
        /// EINFACHER Name an `offset` genau sie meint - also nicht von einer
        /// gleichnamigen Klasse in einem anderen Namespace verdeckt und über
        /// den aktuellen Namespace oder ein `#using` erreichbar. Klassen in
        /// anderen Namespaces erscheinen so erst nach `Namespace.`.</summary>
        private static void AddVisibleTypes(
            List<CompletionItem> results, int offset, ScriptSymbolIndex index, string prefix, bool includeInterfaces, bool includeEnums)
        {
            var context = index.ContextAt(offset);
            foreach (var cls in index.Classes.Values)
            {
                if ((cls.IsInterface && !includeInterfaces) || !MatchesPrefix(cls.SimpleName, prefix)) continue;
                if (index.ResolveClassKey(cls.SimpleName, context, lenient: false) != cls.Name) continue;
                string detail = cls.Namespace.Length > 0 ? $"{(cls.IsInterface ? "Interface" : "Klasse")} in {cls.Namespace}" : (cls.IsInterface ? "Interface" : "Klasse");
                results.Add(new CompletionItem(cls.SimpleName, CompletionKind.ClassName, CompareKeywords(prefix, cls.SimpleName), detail));
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

        /// <summary>Darf Code in der Klasse `fromClass` (null = außerhalb jeder
        /// Klasse) auf `m` zugreifen? `private`: nur die deklarierende
        /// Klasse, `protected`: auch ihre Ableitungen.</summary>
        private static bool IsVisible(MemberInfo m, string? fromClass, ScriptSymbolIndex index) => m.Access switch
        {
            MemberAccess.Private => fromClass == m.Owner,
            MemberAccess.Protected => fromClass != null && (fromClass == m.Owner || index.DerivesFrom(fromClass, m.Owner)),
            _ => true,
        };

        private static bool IsListable(MemberInfo m) =>
            m.Kind != MemberKind.Constructor && !m.Name.StartsWith("operator", StringComparison.Ordinal);

        /// <summary>Vorschläge nach `Ausdruck.` - der Typ des Ausdrucks wird
        /// hergeleitet (siehe ScriptSymbolIndex.ResolveReceiver) und dann NUR
        /// dessen Mitglieder angeboten (inkl. geerbter, ohne nicht
        /// zugreifbare `private`/`protected`, bei einer Klasse `Name.` nur
        /// statische, bei einer Instanz nur nicht-statische). Nur wenn sich
        /// der Typ gar nicht bestimmen lässt (dynamische Typisierung), fällt
        /// das auf Mitglieder ALLER bekannten Klassen zurück.</summary>
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
                        // 'Namespace.' - Unter-Namespaces, Klassen, Interfaces und
                        // Enums darin; hinter 'new Namespace.' nur, was sich
                        // instanziieren lässt (Klassen) bzw. weiterführt (Namespaces).
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
                                _ => (CompletionKind.ClassName, "Klasse"),
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
                        // Eigene Mitglieder vor geerbten, nähere Basis vor fernerer.
                        float baseScore = Math.Max(0.1f, 0.3f - 0.05f * depth);
                        results.Add(ToItem(m, prefix, baseScore, showOwner: depth > 0));
                    }
                    return Dedupe(results);

                case TypeKind.Primitive:
                case TypeKind.Array:
                    // Einfache Werte/Arrays haben keine Klassen-Mitglieder, aber eingebaute
                    // (`text.Length`, `text.IndexOf(...)`, siehe BuiltinMembers).
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
                    return Dedupe(results);

                default:
                    // Typ nicht bestimmbar - als bestmöglicher Fallback zugreifbare
                    // Mitglieder ALLER bekannten Klassen anbieten (mit Klassenname
                    // dahinter, damit man sieht, woher ein Vorschlag stammt),
                    // statt gar nichts vorzuschlagen.
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

            // Typen, die hier ohne Qualifizierung ansprechbar sind, und die
            // Namespaces oberster Ebene (für 'Namespace.Klasse').
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
                _ => $"{modifiers}Feld{(type.Length > 0 ? " : " + type : string.Empty)}{owner}",
            };
            return new CompletionItem(m.Name, kind, CompareKeywords(prefix, m.Name, baseScore), detail);
        }

        private static List<CompletionItem> Dedupe(List<CompletionItem> items) =>
            items.GroupBy(i => (i.Text, i.Kind))
                 .Select(g => g.OrderByDescending(i => i.Score).First())
                 .OrderBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
                 .ToList();
    }
}
