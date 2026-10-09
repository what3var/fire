using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace fire.Editor
{
    /// <summary>A parsed documentation comment: the `///` lines above a class, field, property or method.
    /// Roughly like in Visual Studio: either plain text, or the XML-style tags <c>&lt;summary&gt;</c>,
    /// <c>&lt;param name="x"&gt;</c>, <c>&lt;returns&gt;</c> and <c>&lt;remarks&gt;</c>.</summary>
    public sealed record DocComment(
        string Summary,
        IReadOnlyList<(string Name, string Text)> Parameters,
        string? Returns,
        string? Remarks)
    {
        public bool IsEmpty => Summary.Length == 0 && Parameters.Count == 0 && string.IsNullOrEmpty(Returns) && string.IsNullOrEmpty(Remarks);

        /// <summary>The comment as plain text (for tests and for places without rich display).</summary>
        public string ToPlainText()
        {
            var sb = new StringBuilder(Summary);
            foreach (var (name, text) in Parameters)
                sb.Append(sb.Length > 0 ? "\n" : "").Append(name).Append(": ").Append(text);
            if (!string.IsNullOrEmpty(Returns)) sb.Append(sb.Length > 0 ? "\n" : "").Append("Returns: ").Append(Returns);
            if (!string.IsNullOrEmpty(Remarks)) sb.Append(sb.Length > 0 ? "\n" : "").Append(Remarks);
            return sb.ToString();
        }
    }

    /// <summary>Finds and parses `///` documentation comments (see <see cref="DocComment"/>).</summary>
    public static class DocComments
    {
        /// <summary>The raw text of the contiguous `///` lines directly above the 1-based line `declLine`
        /// (without the slashes, one line per source line), or null if there are none. A blank line or an
        /// ordinary `//` comment in between means "no documentation".</summary>
        public static string? ExtractRaw(string source, int[] lineStarts, int declLine)
        {
            var lines = new List<string>();
            for (int line = declLine - 1; line >= 1 && line <= lineStarts.Length; line--)
            {
                int start = lineStarts[line - 1];
                int end = line < lineStarts.Length ? lineStarts[line] : source.Length;
                string text = source.Substring(start, Math.Max(0, end - start)).Trim();
                if (!text.StartsWith("///", StringComparison.Ordinal)) break;
                text = text.Substring(3);
                if (text.StartsWith(' ')) text = text.Substring(1);
                lines.Add(text);
            }
            if (lines.Count == 0) return null;
            lines.Reverse();
            return string.Join("\n", lines);
        }

        /// <summary>Convenience: <see cref="ExtractRaw"/> followed by <see cref="Parse"/>.</summary>
        public static DocComment? Find(string source, int[] lineStarts, int declLine)
        {
            string? raw = ExtractRaw(source, lineStarts, declLine);
            return raw == null ? null : Parse(raw);
        }

        private static readonly Regex SummaryTag = Tag("summary");
        private static readonly Regex ReturnsTag = Tag("returns");
        private static readonly Regex RemarksTag = Tag("remarks");
        private static readonly Regex ParamTag = new(@"<param\s+name\s*=\s*""([^""]*)""\s*>(.*?)</param>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex SeeTag = new(@"<(?:see|seealso)\s+(?:cref|langword|href)\s*=\s*""([^""]*)""\s*/?>(?:</see(?:also)?>)?", RegexOptions.IgnoreCase);
        private static readonly Regex ParaTag = new(@"</?para\s*/?>|<br\s*/?>", RegexOptions.IgnoreCase);
        private static readonly Regex AnyTag = new(@"</?[A-Za-z][^<>]*>");

        private static Regex Tag(string name) => new($@"<{name}\s*>(.*?)</{name}\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        /// <summary>Parses the raw text. Without any of the known tags the whole text is the summary.</summary>
        public static DocComment? Parse(string raw)
        {
            string? summary = null, returns = null, remarks = null;
            var parameters = new List<(string, string)>();

            var summaryMatch = SummaryTag.Match(raw);
            if (summaryMatch.Success) summary = summaryMatch.Groups[1].Value;
            var returnsMatch = ReturnsTag.Match(raw);
            if (returnsMatch.Success) returns = returnsMatch.Groups[1].Value;
            var remarksMatch = RemarksTag.Match(raw);
            if (remarksMatch.Success) remarks = remarksMatch.Groups[1].Value;
            foreach (Match m in ParamTag.Matches(raw))
                parameters.Add((m.Groups[1].Value.Trim(), Clean(m.Groups[2].Value)));

            bool anyKnownTag = summary != null || returns != null || remarks != null || parameters.Count > 0;
            if (!anyKnownTag) summary = raw;

            var doc = new DocComment(Clean(summary ?? ""), parameters, NullIfEmpty(Clean(returns ?? "")), NullIfEmpty(Clean(remarks ?? "")));
            return doc.IsEmpty ? null : doc;
        }

        private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

        /// <summary>Removes tags, resolves entities and collapses the lines of a paragraph into one (a blank line
        /// starts a new paragraph, written as a line break).</summary>
        private static string Clean(string text)
        {
            text = SeeTag.Replace(text, m => m.Groups[1].Value);
            text = ParaTag.Replace(text, "\n\n");
            text = AnyTag.Replace(text, "");
            text = text.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");

            var paragraphs = new List<string>();
            var current = new List<string>();
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                {
                    if (current.Count > 0) { paragraphs.Add(string.Join(" ", current)); current.Clear(); }
                }
                else current.Add(line);
            }
            if (current.Count > 0) paragraphs.Add(string.Join(" ", current));
            return string.Join("\n", paragraphs);
        }
    }
}
