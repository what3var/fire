using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Animation;
using System.Xml.Linq;

namespace fire.Editor
{
    public enum CompletionKind
    {
        Keyword,
        TypeKeyword,
        ClassName,
        EnumName,
        Field,
        Method,
        Property,
        Variable,
        Parameter,
    }

    public sealed record CompletionItem(string Text, CompletionKind Kind, float Score, string? Detail = null)
    {
        public string Display => Detail != null ? $"{Text}  ({Detail})" : Text;
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

            // Direkt nach einem '.' (noch nichts vom Mitgliedsnamen getippt).
            if (i >= 0 && source[i] == '.')
            {
                string? target = ReadIdentifierBefore(source, i);
                return GetMemberSuggestions(offset, index, target, string.Empty);
            }

            int start = i;
            while (start >= 0 && (char.IsLetterOrDigit(source[start]) || source[start] == '_')) start--;

            // Rückwärts bis zum Anfang des aktuell getippten Bezeichners.
            if (start >= 0 && source[start] == '.')
            {
                string prefix = source.Substring(start + 1, offset - start - 1);
                string? target = ReadIdentifierBefore(source, start);
                return GetMemberSuggestions(offset, index, target, prefix);
            }

            string idPrefix = start + 1 <= offset ? source.Substring(start + 1, offset - start - 1) : string.Empty;
            return GetIdentifierSuggestions(offset, index, idPrefix);
        }

        private static string? ReadIdentifierBefore(string source, int dotIdx)
        {
            int end = dotIdx;
            int start = end - 1;
            while (start >= 0 && (char.IsLetterOrDigit(source[start]) || source[start] == '_')) start--;
            if (start + 1 >= end) return null;
            return source.Substring(start + 1, end - start - 1);
        }

        private static List<CompletionItem> GetMemberSuggestions(
            int offset, ScriptSymbolIndex index, string? target, string prefix)
        {
            var results = new List<CompletionItem>();
            if (string.IsNullOrEmpty(target)) return results;

            string? className = target == "this"
                ? index.EnclosingClassAt(offset)
                : index.TryResolveDeclaredType(offset, target);

            if (className == null && index.Classes.ContainsKey(target))
                className = target; // 'ClassName.' direkt (unüblich, aber warum nicht abdecken)

            if (className == null)
            {
                // Typ nicht bestimmbar (dynamische Typisierung - der
                // Normalfall für nicht explizit typisierte Variablen) - als
                // bestmöglicher Fallback Mitglieder ALLER bekannten Klassen
                // anbieten, statt gar nichts vorzuschlagen.
                foreach (var cls in index.Classes.Values)
                    foreach (var m in cls.Members)
                        if (m.Kind != MemberKind.Constructor && MatchesPrefix(m.Name, prefix))
                            results.Add(ToItem(m, prefix, 0f));
                return Dedupe(results);
            }

            foreach (var m in index.MembersOf(className))
                if (m.Kind != MemberKind.Constructor && MatchesPrefix(m.Name, prefix))
                    results.Add(ToItem(m, prefix, 0.25f));

            return Dedupe(results);
        }

        private static List<CompletionItem> GetIdentifierSuggestions(int offset, ScriptSymbolIndex index, string prefix)
        {
            var results = new List<CompletionItem>();

            foreach (var kw in Keywords)
                if (MatchesPrefix(kw, prefix)) results.Add(new CompletionItem(kw, CompletionKind.Keyword, CompareKeywords(prefix, kw)));
            foreach (var kw in TypeKeywords)
                if (MatchesPrefix(kw, prefix)) results.Add(new CompletionItem(kw, CompletionKind.TypeKeyword, CompareKeywords(prefix, kw)));

            foreach (var cls in index.Classes.Keys)
                if (MatchesPrefix(cls, prefix)) results.Add(new CompletionItem(cls, CompletionKind.ClassName, CompareKeywords(prefix, cls)));

            foreach (var enumName in index.EnumMembers.Keys)
                if (MatchesPrefix(enumName, prefix)) results.Add(new CompletionItem(enumName, CompletionKind.EnumName, CompareKeywords(prefix, enumName)));

            foreach (var (name, _) in index.EnclosingFunctionParams(offset))
                if (MatchesPrefix(name, prefix)) results.Add(new CompletionItem(name, CompletionKind.Parameter, CompareKeywords(prefix, name)));

            foreach (var name in index.LocalVarsBeforeCursor(string.IsNullOrEmpty(prefix) ? offset : offset - prefix.Length))
                if (MatchesPrefix(name, prefix)) results.Add(new CompletionItem(name, CompletionKind.Variable, CompareKeywords(prefix, name)));

            string? enclosingClass = index.EnclosingClassAt(offset);
            if (enclosingClass != null)
                foreach (var m in index.MembersOf(enclosingClass))
                    if (m.Kind != MemberKind.Constructor && MatchesPrefix(m.Name, prefix))
                        results.Add(ToItem(m, prefix, 0.1f));

            //foreach (var name in index.AllDeclaredNames)
            //    if (MatchesPrefix(name, prefix)) results.Add(new CompletionItem(name, CompletionKind.Variable, CompareKeywords(prefix, name)));

            return Dedupe(results);
        }

        private static bool MatchesPrefix(string candidate, string prefix) =>
            prefix.Length == 0 || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

        private static CompletionItem ToItem(MemberInfo m, string prefix, float baseScore) => m.Kind switch
        {
            MemberKind.Method => new CompletionItem(m.Name, CompletionKind.Method, CompareKeywords(prefix, m.Name, baseScore), $"{m.ParamCount} Parameter"),
            MemberKind.Property => new CompletionItem(m.Name, CompletionKind.Property, CompareKeywords(prefix, m.Name, baseScore), "Property"),
            _ => new CompletionItem(m.Name, CompletionKind.Field, CompareKeywords(prefix, m.Name, baseScore), "Feld"),
        };

        private static List<CompletionItem> Dedupe(List<CompletionItem> items) =>
            items.GroupBy(i => (i.Text, i.Kind))
                 .Select(g => g.First())
                 .OrderBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
                 .ToList();
    }
}
