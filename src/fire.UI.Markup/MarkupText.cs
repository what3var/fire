using System;
using System.Collections.Generic;
using System.Text;

namespace fire.UI.Markup
{
    /// <summary>An attribute in the text of a start tag: its name, its value (unescaped) and where the name and the value (with the quotes) lie.</summary>
    public sealed record MarkupTextAttribute(string Name, string Value, int NameStart, int ValueStart, int ValueEnd);

    /// <summary>An element in the text: tag, where it starts (`<`), where its start tag ends (after `>`), where it ends (after its end tag, or the end of the text when it is
    /// not closed), its attributes and the tags of the elements around it (outermost first).</summary>
    public sealed record MarkupTextElement(string Tag, int Start, int StartTagEnd, int End, bool SelfClosing, IReadOnlyList<MarkupTextAttribute> Attributes, IReadOnlyList<string> Ancestors)
    {
        /// <summary>Where a new attribute goes: after the last character before `/>` or `>`.</summary>
        public int InsertAt { get; init; }
    }

    /// <summary>Reads elements and attributes straight from the text of a markup file, also while it is half typed (it never fails) - for the editor, which changes
    /// attributes in place without rewriting the file.</summary>
    public static class MarkupText
    {
        /// <summary>All elements of the text in the order of their start tags.</summary>
        public static List<MarkupTextElement> Elements(string text)
        {
            var result = new List<MarkupTextElement>();
            // the open elements: index in `result`
            var open = new List<int>();
            var ends = new Dictionary<int, int>();
            int i = 0;
            while (i < text.Length)
            {
                int lt = text.IndexOf('<', i);
                if (lt < 0) break;
                if (Starts(text, lt, "<!--")) { i = Skip(text, lt + 4, "-->"); continue; }
                if (Starts(text, lt, "<![CDATA[")) { i = Skip(text, lt + 9, "]]>"); continue; }
                if (Starts(text, lt, "<?")) { i = Skip(text, lt + 2, "?>"); continue; }
                if (Starts(text, lt, "<!")) { i = Skip(text, lt + 2, ">"); continue; }
                if (Starts(text, lt, "</"))
                {
                    int p = lt + 2;
                    int nameStart = p;
                    while (p < text.Length && IsNameChar(text[p])) p++;
                    string name = text.Substring(nameStart, p - nameStart);
                    int gt = text.IndexOf('>', p);
                    int after = gt < 0 ? text.Length : gt + 1;
                    // the innermost open element of this name is closed (and all inside it that were left open)
                    for (int k = open.Count - 1; k >= 0; k--)
                    {
                        if (!string.Equals(result[open[k]].Tag, name, StringComparison.Ordinal)) continue;
                        for (int m = open.Count - 1; m >= k; m--) ends[open[m]] = m == k ? after : lt;
                        open.RemoveRange(k, open.Count - k);
                        break;
                    }
                    i = after;
                    continue;
                }
                int q = lt + 1;
                if (q >= text.Length || !IsNameStart(text[q])) { i = lt + 1; continue; }
                int tagStart = q;
                while (q < text.Length && IsNameChar(text[q])) q++;
                string tag = text.Substring(tagStart, q - tagStart);
                var attributes = new List<MarkupTextAttribute>();
                int end = -1;
                bool selfClosing = false;
                int lastContent = q;
                while (q < text.Length)
                {
                    char c = text[q];
                    if (c == '>') { end = q + 1; break; }
                    if (c == '/' && q + 1 < text.Length && text[q + 1] == '>') { end = q + 2; selfClosing = true; break; }
                    if (c == '<') break;   // a tag that was not finished: the next one starts here
                    if (IsNameStart(c))
                    {
                        int ns = q;
                        while (q < text.Length && IsNameChar(text[q])) q++;
                        string attr = text.Substring(ns, q - ns);
                        lastContent = q;
                        int r = q;
                        while (r < text.Length && char.IsWhiteSpace(text[r])) r++;
                        if (r < text.Length && text[r] == '=')
                        {
                            r++;
                            while (r < text.Length && char.IsWhiteSpace(text[r])) r++;
                            if (r < text.Length && (text[r] == '"' || text[r] == '\''))
                            {
                                char quote = text[r];
                                int close = text.IndexOf(quote, r + 1);
                                int valueEnd = close < 0 ? text.Length : close + 1;
                                string raw = text.Substring(r + 1, (close < 0 ? text.Length : close) - r - 1);
                                attributes.Add(new MarkupTextAttribute(attr, Unescape(raw), ns, r, valueEnd));
                                q = valueEnd;
                                lastContent = q;
                                continue;
                            }
                        }
                        continue;
                    }
                    q++;
                }
                var ancestors = new List<string>();
                foreach (int k in open) ancestors.Add(result[k].Tag);
                int startTagEnd = end < 0 ? q : end;
                result.Add(new MarkupTextElement(tag, lt, startTagEnd, startTagEnd, selfClosing, attributes, ancestors) { InsertAt = lastContent });
                if (!selfClosing) open.Add(result.Count - 1);
                else ends[result.Count - 1] = startTagEnd;
                i = startTagEnd > lt ? startTagEnd : lt + 1;
            }
            for (int k = 0; k < result.Count; k++)
                result[k] = result[k] with { End = ends.TryGetValue(k, out int e) ? e : text.Length };
            return result;
        }

        /// <summary>The innermost element the offset lies in (inside its start tag, its content or its end tag), null when it lies in none.</summary>
        public static MarkupTextElement? ElementAt(string text, int offset)
        {
            MarkupTextElement? best = null;
            foreach (var e in Elements(text))
                if (e.Start < offset && offset <= e.End && (best == null || e.Start >= best.Start)) best = e;
            return best;
        }

        /// <summary>The change that gives the attribute `name` of the element the value `value` (null: removes it): replace the text from `Start` to `End` by `Text`.
        /// Null when nothing changes.</summary>
        public static (int Start, int End, string Text)? SetAttribute(string text, MarkupTextElement element, string name, string? value)
        {
            var existing = element.Attributes.FirstOrDefaultByName(name);
            if (value == null)
            {
                if (existing == null) return null;
                int from = existing.NameStart;
                while (from > 0 && char.IsWhiteSpace(text[from - 1]) && from - 1 > element.Start) from--;
                return (from, existing.ValueEnd, "");
            }
            string quoted = "\"" + Escape(value) + "\"";
            if (existing != null) return (existing.ValueStart, existing.ValueEnd, quoted);
            return (element.InsertAt, element.InsertAt, " " + name + "=" + quoted);
        }

        private static MarkupTextAttribute? FirstOrDefaultByName(this IReadOnlyList<MarkupTextAttribute> attributes, string name)
        {
            foreach (var a in attributes) if (string.Equals(a.Name, name, StringComparison.Ordinal)) return a;
            foreach (var a in attributes) if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        public static string Escape(string value)
        {
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
                sb.Append(c switch { '&' => "&amp;", '"' => "&quot;", '<' => "&lt;", '\n' => "&#10;", _ => c.ToString() });
            return sb.ToString();
        }

        public static string Unescape(string raw)
        {
            if (raw.IndexOf('&') < 0) return raw;
            return raw.Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&#10;", "\n").Replace("&amp;", "&");
        }

        private static bool Starts(string text, int at, string s) => string.CompareOrdinal(text, at, s, 0, s.Length) == 0;
        private static int Skip(string text, int from, string end) { int k = text.IndexOf(end, from, StringComparison.Ordinal); return k < 0 ? text.Length : k + end.Length; }
        private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '-';
    }
}
