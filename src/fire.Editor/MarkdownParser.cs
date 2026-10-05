using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace fire.Editor
{
    // -----------------------------------------------------------
    // Dokument-Modell (rein, ohne WPF - dadurch ohne Oberfläche testbar).
    // Die Darstellung als FlowDocument liegt in MarkdownRenderer.
    // -----------------------------------------------------------

    public abstract record MdInline;
    public sealed record MdText(string Text) : MdInline;
    public sealed record MdBold(IReadOnlyList<MdInline> Content) : MdInline;
    public sealed record MdItalic(IReadOnlyList<MdInline> Content) : MdInline;
    public sealed record MdStrike(IReadOnlyList<MdInline> Content) : MdInline;
    public sealed record MdCode(string Code) : MdInline;
    public sealed record MdLink(IReadOnlyList<MdInline> Content, string Url) : MdInline;
    public sealed record MdImage(string Alt, string Url) : MdInline;
    public sealed record MdLineBreak : MdInline;

    public abstract record MdBlock;
    public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Content) : MdBlock;
    public sealed record MdParagraph(IReadOnlyList<MdInline> Content) : MdBlock;
    public sealed record MdCodeBlock(string? Language, string Code) : MdBlock;
    public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;
    public sealed record MdRule : MdBlock;
    /// <summary>`Checked`: null = kein Aufgaben-Eintrag, sonst `- [ ]`/`- [x]`.</summary>
    public sealed record MdListItem(IReadOnlyList<MdBlock> Blocks, bool? Checked);
    public sealed record MdList(bool Ordered, int Start, IReadOnlyList<MdListItem> Items) : MdBlock;
    public enum MdAlign { Left, Center, Right }
    public sealed record MdTable(IReadOnlyList<IReadOnlyList<MdInline>> Header, IReadOnlyList<MdAlign> Aligns,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows) : MdBlock;

    /// <summary>Ein bewusst kompakter Markdown-Parser (CommonMark/GitHub-
    /// Teilmenge): Überschriften (# und Setext), Absätze, Hervorhebungen
    /// (**fett**, *kursiv*, ~~durchgestrichen~~, `Code`), Links/Bilder/
    /// automatische Links, Zitate, (verschachtelte) Listen mit Aufgaben-
    /// Kästchen, Code-Blöcke (``` und ~~~), Trennlinien und Pipe-Tabellen.
    /// Rohes HTML wird nicht interpretiert, sondern als Text angezeigt.</summary>
    public static class MarkdownParser
    {
        public static IReadOnlyList<MdBlock> Parse(string text)
        {
            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(ExpandTabs).ToList();
            return ParseBlocks(lines);
        }

        private static string ExpandTabs(string line)
        {
            if (line.IndexOf('\t') < 0) return line;
            var sb = new StringBuilder();
            foreach (char c in line)
            {
                if (c == '\t') sb.Append(' ', 4 - sb.Length % 4);
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // -----------------------------------------------------------
        // Blöcke
        // -----------------------------------------------------------

        private static readonly Regex FenceStart = new(@"^ {0,3}(```+|~~~+)\s*([^`\s]*)[^`]*$", RegexOptions.Compiled);
        private static readonly Regex Atx = new(@"^ {0,3}(#{1,6})(?:\s+(.*?))?(?:\s+#+)?\s*$", RegexOptions.Compiled);
        private static readonly Regex Rule = new(@"^ {0,3}([-*_])(?:\s*\1){2,}\s*$", RegexOptions.Compiled);
        private static readonly Regex ListMarker = new(@"^(?<indent> *)(?<marker>[-*+]|\d{1,9}[.)])(?<gap> +|$)(?<rest>.*)$", RegexOptions.Compiled);
        private static readonly Regex SetextH1 = new(@"^ {0,3}=+\s*$", RegexOptions.Compiled);
        private static readonly Regex SetextH2 = new(@"^ {0,3}-+\s*$", RegexOptions.Compiled);
        private static readonly Regex TableSeparator = new(@"^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$", RegexOptions.Compiled);
        private static readonly Regex TaskBox = new(@"^\[([ xX])\]\s+", RegexOptions.Compiled);

        private static bool IsBlank(string line) => line.Trim().Length == 0;

        private static int IndentOf(string line)
        {
            int n = 0;
            while (n < line.Length && line[n] == ' ') n++;
            return n;
        }

        private static List<MdBlock> ParseBlocks(IReadOnlyList<string> lines)
        {
            var blocks = new List<MdBlock>();
            int i = 0;
            while (i < lines.Count)
            {
                string line = lines[i];
                if (IsBlank(line)) { i++; continue; }

                // Code-Block
                var fence = FenceStart.Match(line);
                if (fence.Success)
                {
                    string marker = fence.Groups[1].Value;
                    string? lang = fence.Groups[2].Length > 0 ? fence.Groups[2].Value : null;
                    int indent = IndentOf(line);
                    var code = new List<string>();
                    i++;
                    while (i < lines.Count && !IsClosingFence(lines[i], marker))
                    {
                        string l = lines[i];
                        int strip = Math.Min(indent, IndentOf(l));
                        code.Add(l.Substring(strip));
                        i++;
                    }
                    if (i < lines.Count) i++; // schließender Zaun
                    blocks.Add(new MdCodeBlock(lang, string.Join("\n", code)));
                    continue;
                }

                // Überschrift (#)
                var atx = Atx.Match(line);
                if (atx.Success)
                {
                    blocks.Add(new MdHeading(atx.Groups[1].Length, ParseInlines(atx.Groups[2].Value.Trim())));
                    i++;
                    continue;
                }

                // Trennlinie (vor Listen prüfen: `---`/`* * *`)
                if (Rule.IsMatch(line)) { blocks.Add(new MdRule()); i++; continue; }

                // Zitat
                if (IsQuoteLine(line))
                {
                    var inner = new List<string>();
                    while (i < lines.Count && IsQuoteLine(lines[i]))
                    {
                        string l = lines[i].TrimStart();
                        l = l.Substring(1);
                        if (l.StartsWith(' ')) l = l.Substring(1);
                        inner.Add(l);
                        i++;
                    }
                    blocks.Add(new MdQuote(ParseBlocks(inner)));
                    continue;
                }

                // Liste
                if (ListMarker.IsMatch(line) && StartsList(line))
                {
                    blocks.Add(ParseList(lines, ref i));
                    continue;
                }

                // Tabelle
                if (line.Contains('|') && i + 1 < lines.Count && TableSeparator.IsMatch(lines[i + 1]) && lines[i + 1].Contains('-'))
                {
                    var header = SplitRow(line);
                    var sepCells = SplitRow(lines[i + 1]);
                    if (header.Count == sepCells.Count)
                    {
                        var aligns = sepCells.Select(c =>
                        {
                            c = c.Trim();
                            bool left = c.StartsWith(':'), right = c.EndsWith(':');
                            return left && right ? MdAlign.Center : right ? MdAlign.Right : MdAlign.Left;
                        }).ToList();
                        i += 2;
                        var rows = new List<IReadOnlyList<IReadOnlyList<MdInline>>>();
                        while (i < lines.Count && !IsBlank(lines[i]) && lines[i].Contains('|'))
                        {
                            var cells = SplitRow(lines[i]);
                            var row = new List<IReadOnlyList<MdInline>>();
                            for (int c = 0; c < header.Count; c++)
                                row.Add(ParseInlines(c < cells.Count ? cells[c].Trim() : ""));
                            rows.Add(row);
                            i++;
                        }
                        blocks.Add(new MdTable(header.Select(h => ParseInlines(h.Trim())).ToList(), aligns, rows));
                        continue;
                    }
                }

                // Absatz (ggf. Setext-Überschrift)
                var para = new List<string>();
                while (i < lines.Count)
                {
                    string l = lines[i];
                    if (IsBlank(l)) break;
                    if (para.Count > 0)
                    {
                        if (SetextH1.IsMatch(l) || SetextH2.IsMatch(l))
                        {
                            int level = SetextH1.IsMatch(l) ? 1 : 2;
                            blocks.Add(new MdHeading(level, ParseInlines(JoinParagraph(para))));
                            para = null!;
                            i++;
                            break;
                        }
                        if (StartsBlock(l)) break;
                    }
                    para.Add(l);
                    i++;
                }
                if (para != null && para.Count > 0) blocks.Add(new MdParagraph(ParseInlines(JoinParagraph(para))));
            }
            return blocks;
        }

        private static bool IsClosingFence(string line, string marker)
        {
            string t = line.Trim();
            return t.Length >= marker.Length && t[0] == marker[0] && t.All(c => c == marker[0]);
        }

        private static bool IsQuoteLine(string line)
        {
            int n = IndentOf(line);
            return n <= 3 && n < line.Length && line[n] == '>';
        }

        /// <summary>Ein Listenanfang: `-`/`*`/`+`/`1.` gefolgt von Leerzeichen und Inhalt.</summary>
        private static bool StartsList(string line)
        {
            var m = ListMarker.Match(line);
            return m.Success && (m.Groups["gap"].Length > 0 && m.Groups["rest"].Length > 0 || m.Groups["rest"].Length == 0 && m.Groups["gap"].Length == 0);
        }

        /// <summary>Beginnt diese Zeile einen neuen Block, der einen Absatz beendet?</summary>
        private static bool StartsBlock(string line)
        {
            if (FenceStart.IsMatch(line) || Atx.IsMatch(line) || Rule.IsMatch(line) || IsQuoteLine(line)) return true;
            var m = ListMarker.Match(line);
            if (m.Success && m.Groups["rest"].Length > 0 && m.Groups["gap"].Length > 0)
            {
                // Eine nummerierte Liste unterbricht einen Absatz nur mit `1.`
                string marker = m.Groups["marker"].Value;
                return !char.IsDigit(marker[0]) || marker.StartsWith("1");
            }
            return false;
        }

        private static string JoinParagraph(List<string> lines)
        {
            var sb = new StringBuilder();
            for (int k = 0; k < lines.Count; k++)
            {
                string l = lines[k];
                bool last = k == lines.Count - 1;
                if (last) { sb.Append(l.Trim()); break; }
                bool hard = l.EndsWith("  ") || l.EndsWith("\\");
                string t = l.TrimStart();
                if (l.EndsWith("\\")) t = t.Substring(0, t.Length - 1);
                sb.Append(hard ? t.TrimEnd() : t.Trim());
                sb.Append(hard ? "\u0001" : " "); // \u0001 = harter Umbruch (siehe ParseInlines)
            }
            return sb.ToString();
        }

        private static List<string> SplitRow(string line)
        {
            string t = line.Trim();
            if (t.StartsWith('|')) t = t.Substring(1);
            if (t.EndsWith('|') && !t.EndsWith("\\|")) t = t.Substring(0, t.Length - 1);
            var cells = new List<string>();
            var cur = new StringBuilder();
            for (int k = 0; k < t.Length; k++)
            {
                if (t[k] == '\\' && k + 1 < t.Length && t[k + 1] == '|') { cur.Append('|'); k++; }
                else if (t[k] == '|') { cells.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(t[k]);
            }
            cells.Add(cur.ToString());
            return cells;
        }

        private static MdList ParseList(IReadOnlyList<string> lines, ref int i)
        {
            var first = ListMarker.Match(lines[i]);
            int baseIndent = first.Groups["indent"].Length;
            string firstMarker = first.Groups["marker"].Value;
            bool ordered = char.IsDigit(firstMarker[0]);
            int start = ordered ? int.Parse(firstMarker.TrimEnd('.', ')')) : 1;
            char bullet = ordered ? firstMarker[^1] : firstMarker[0];
            var items = new List<MdListItem>();

            while (i < lines.Count)
            {
                var m = ListMarker.Match(lines[i]);
                if (!m.Success || m.Groups["indent"].Length != baseIndent || Rule.IsMatch(lines[i])) break;
                string marker = m.Groups["marker"].Value;
                if (char.IsDigit(marker[0]) != ordered) break;
                if ((ordered ? marker[^1] : marker[0]) != bullet) break;

                // Einrückung des Inhalts: nach Marker + Leerzeichen (max. 4 Leerzeichen, sonst 1)
                int gap = m.Groups["gap"].Length;
                if (gap > 4 || m.Groups["rest"].Length == 0) gap = 1;
                int contentIndent = baseIndent + marker.Length + gap;

                var itemLines = new List<string> { m.Groups["rest"].Value };
                i++;
                bool pendingBlank = false;
                while (i < lines.Count)
                {
                    string l = lines[i];
                    if (IsBlank(l)) { pendingBlank = true; itemLines.Add(""); i++; continue; }
                    int ind = IndentOf(l);
                    if (ind >= contentIndent)
                    {
                        itemLines.Add(l.Substring(contentIndent));
                        pendingBlank = false;
                        i++;
                        continue;
                    }
                    // Weniger eingerückt: neues Listenelement oder Ende - außer träge Fortsetzung
                    // einer Absatzzeile direkt (ohne Leerzeile) darunter.
                    if (!pendingBlank && !StartsBlock(l) && !ListMarker.IsMatch(l) && !IsBlank(itemLines[^1]))
                    {
                        itemLines.Add(l.TrimStart());
                        i++;
                        continue;
                    }
                    break;
                }
                while (itemLines.Count > 0 && IsBlank(itemLines[^1])) itemLines.RemoveAt(itemLines.Count - 1);

                bool? check = null;
                var tb = TaskBox.Match(itemLines[0]);
                if (tb.Success)
                {
                    check = tb.Groups[1].Value != " ";
                    itemLines[0] = itemLines[0].Substring(tb.Length);
                }
                items.Add(new MdListItem(ParseBlocks(itemLines), check));

                // Leerzeilen zwischen Elementen überspringen, falls dahinter weitere Elemente folgen
                int peek = i;
                while (peek < lines.Count && IsBlank(lines[peek])) peek++;
                if (peek < lines.Count && peek != i)
                {
                    var nm = ListMarker.Match(lines[peek]);
                    if (nm.Success && nm.Groups["indent"].Length == baseIndent) i = peek;
                }
            }
            return new MdList(ordered, start, items);
        }

        // -----------------------------------------------------------
        // Inline
        // -----------------------------------------------------------

        private const string Punctuation = "\\`*_{}[]()#+-.!|~<>\"'$%&,/:;=?@^";

        private static readonly Regex AutoLink = new(@"^<(https?://[^\s>]+|[^\s@>]+@[^\s@>]+\.[^\s>]+)>", RegexOptions.Compiled);
        private static readonly Regex BareUrl = new(@"^https?://[^\s<>]+[^\s<>.,;:!?)\]'""]", RegexOptions.Compiled);

        public static IReadOnlyList<MdInline> ParseInlines(string text)
        {
            var result = new List<MdInline>();
            var sb = new StringBuilder();
            void Flush()
            {
                if (sb.Length > 0) { result.Add(new MdText(sb.ToString())); sb.Clear(); }
            }

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                if (c == '\u0001') { Flush(); result.Add(new MdLineBreak()); i++; continue; }

                if (c == '\\' && i + 1 < text.Length && Punctuation.IndexOf(text[i + 1]) >= 0)
                {
                    sb.Append(text[i + 1]);
                    i += 2;
                    continue;
                }

                if (c == '`')
                {
                    int n = RunLength(text, i, '`');
                    int close = FindBacktickRun(text, i + n, n);
                    if (close > 0)
                    {
                        Flush();
                        string code = text.Substring(i + n, close - (i + n)).Replace('\u0001', ' ');
                        if (code.Length > 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code.Substring(1, code.Length - 2);
                        result.Add(new MdCode(code));
                        i = close + n;
                        continue;
                    }
                    sb.Append('`', n);
                    i += n;
                    continue;
                }

                if (c == '!' && i + 1 < text.Length && text[i + 1] == '[')
                {
                    if (TryParseLinkTail(text, i + 1, out int labelEnd, out string url, out int end))
                    {
                        Flush();
                        result.Add(new MdImage(PlainText(text.Substring(i + 2, labelEnd - (i + 2))), url));
                        i = end;
                        continue;
                    }
                }

                if (c == '[')
                {
                    if (TryParseLinkTail(text, i, out int labelEnd, out string url, out int end))
                    {
                        Flush();
                        result.Add(new MdLink(ParseInlines(text.Substring(i + 1, labelEnd - (i + 1))), url));
                        i = end;
                        continue;
                    }
                }

                if (c == '<')
                {
                    var m = AutoLink.Match(text.Substring(i));
                    if (m.Success)
                    {
                        Flush();
                        string target = m.Groups[1].Value;
                        string href = target.Contains("://") ? target : "mailto:" + target;
                        result.Add(new MdLink(new MdInline[] { new MdText(target) }, href));
                        i += m.Length;
                        continue;
                    }
                }

                if (c == 'h' && (i == 0 || !char.IsLetterOrDigit(text[i - 1])) && text.AsSpan(i).StartsWith("http", StringComparison.Ordinal))
                {
                    var m = BareUrl.Match(text.Substring(i));
                    if (m.Success)
                    {
                        Flush();
                        result.Add(new MdLink(new MdInline[] { new MdText(m.Value) }, m.Value));
                        i += m.Length;
                        continue;
                    }
                }

                if (c == '*' || c == '_' || c == '~')
                {
                    int run = RunLength(text, i, c);
                    if (TryEmphasis(text, i, c, run, out var node, out int next))
                    {
                        Flush();
                        result.Add(node!);
                        i = next;
                        continue;
                    }
                    sb.Append(c, run);
                    i += run;
                    continue;
                }

                sb.Append(c);
                i++;
            }
            Flush();
            return result;
        }

        private static int RunLength(string text, int i, char c)
        {
            int n = 0;
            while (i + n < text.Length && text[i + n] == c) n++;
            return n;
        }

        private static int FindBacktickRun(string text, int from, int n)
        {
            int j = from;
            while (j < text.Length)
            {
                if (text[j] == '`')
                {
                    int run = RunLength(text, j, '`');
                    if (run == n) return j;
                    j += run;
                }
                else j++;
            }
            return -1;
        }

        private static bool TryEmphasis(string text, int i, char c, int run, out MdInline? node, out int next)
        {
            node = null; next = i;
            char before = i > 0 ? text[i - 1] : ' ';
            if (c == '_' && char.IsLetterOrDigit(before)) return false; // Unterstrich mitten im Wort

            if (c == '~')
            {
                if (run != 2) return false;
                return TryWrap(text, i, "~~", 2, out node, out next, content => new MdStrike(content));
            }

            // `***x***`: fett + kursiv
            if (run >= 3)
            {
                string d3 = new string(c, 3);
                if (TryWrap(text, i, d3, 3, out node, out next, content => new MdBold(new MdInline[] { new MdItalic(content) })))
                    return true;
                run = 2;
            }
            if (run >= 2)
            {
                string d2 = new string(c, 2);
                if (TryWrap(text, i, d2, 2, out node, out next, content => new MdBold(content))) return true;
            }
            string d1 = new string(c, 1);
            return TryWrap(text, i, d1, 1, out node, out next, content => new MdItalic(content));
        }

        private static bool TryWrap(string text, int i, string delim, int len, out MdInline? node, out int next,
            Func<IReadOnlyList<MdInline>, MdInline> make)
        {
            node = null; next = i;
            int open = i + len;
            if (open >= text.Length || char.IsWhiteSpace(text[open])) return false;
            int close = FindClosing(text, open, delim);
            if (close < 0) return false;
            string inner = text.Substring(open, close - open);
            if (inner.Length == 0) return false;
            node = make(ParseInlines(inner));
            next = close + len;
            return true;
        }

        private static int FindClosing(string text, int from, string delim)
        {
            char d = delim[0];
            int len = delim.Length;
            int j = from;
            while (j < text.Length)
            {
                char c = text[j];
                if (c == '\\') { j += 2; continue; }
                if (c == '`')
                {
                    int n = RunLength(text, j, '`');
                    int close = FindBacktickRun(text, j + n, n);
                    j = close > 0 ? close + n : j + n;
                    continue;
                }
                if (c == '[')
                {
                    // Linkziel überspringen, damit `*` in URLs nicht schließt
                    if (TryParseLinkTail(text, j, out int labelEnd, out _, out int end)) { j = end; continue; }
                }
                if (c == d)
                {
                    int run = RunLength(text, j, d);
                    bool leftFlanking = j > from && !char.IsWhiteSpace(text[j - 1]);
                    char after = j + run < text.Length ? text[j + run] : ' ';
                    bool okUnderscore = d != '_' || !char.IsLetterOrDigit(after);
                    if (leftFlanking && okUnderscore)
                    {
                        if (run == len) return j;
                        if (run > len && run - len <= 2 && len < 3 && d != '~')
                            return j + (run - len); // `**fett *kursiv***`: schließt mit den LETZTEN Zeichen des Laufs
                    }
                    j += run;
                    continue;
                }
                j++;
            }
            return -1;
        }

        /// <summary>`[Text](ziel "titel")` ab `i` (das `[`).</summary>
        private static bool TryParseLinkTail(string text, int i, out int labelEnd, out string url, out int end)
        {
            labelEnd = -1; url = ""; end = i;
            int depth = 0;
            int j = i;
            for (; j < text.Length; j++)
            {
                if (text[j] == '\\') { j++; continue; }
                if (text[j] == '[') depth++;
                else if (text[j] == ']') { depth--; if (depth == 0) break; }
            }
            if (j >= text.Length || j + 1 >= text.Length || text[j + 1] != '(') return false;
            labelEnd = j;
            int k = j + 2;
            int parens = 1;
            var sb = new StringBuilder();
            bool angle = k < text.Length && text[k] == '<';
            if (angle) k++;
            for (; k < text.Length; k++)
            {
                char c = text[k];
                if (c == '\\' && k + 1 < text.Length) { sb.Append(text[k + 1]); k++; continue; }
                if (angle && c == '>') { k++; break; }
                if (!angle && char.IsWhiteSpace(c)) break;
                if (!angle && c == '(') parens++;
                if (!angle && c == ')') { parens--; if (parens == 0) break; }
                sb.Append(c);
            }
            // optionaler Titel
            while (k < text.Length && char.IsWhiteSpace(text[k])) k++;
            if (k < text.Length && (text[k] == '"' || text[k] == '\''))
            {
                char q = text[k];
                int close = text.IndexOf(q, k + 1);
                if (close < 0) return false;
                k = close + 1;
                while (k < text.Length && char.IsWhiteSpace(text[k])) k++;
            }
            if (k >= text.Length || text[k] != ')') return false;
            url = sb.ToString();
            end = k + 1;
            return true;
        }

        /// <summary>Der reine Text eines Inline-Abschnitts (für Bild-Alternativtexte).</summary>
        private static string PlainText(string markup) => PlainText(ParseInlines(markup));

        public static string PlainText(IEnumerable<MdInline> inlines)
        {
            var sb = new StringBuilder();
            foreach (var n in inlines)
            {
                switch (n)
                {
                    case MdText t: sb.Append(t.Text); break;
                    case MdCode c: sb.Append(c.Code); break;
                    case MdBold b: sb.Append(PlainText(b.Content)); break;
                    case MdItalic it: sb.Append(PlainText(it.Content)); break;
                    case MdStrike s: sb.Append(PlainText(s.Content)); break;
                    case MdLink l: sb.Append(PlainText(l.Content)); break;
                    case MdImage im: sb.Append(im.Alt); break;
                    case MdLineBreak: sb.Append(' '); break;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>Heading anchors the way GitHub makes them: lower case, punctuation removed, spaces become hyphens
    /// ("## First Steps!" gives "first-steps"). Repeated headings get -1, -2 ... appended.</summary>
    public static class MdAnchors
    {
        public static string Slug(string headingText)
        {
            var sb = new StringBuilder();
            foreach (char c in headingText.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('-');
            }
            return sb.ToString();
        }

        /// <summary>Like <see cref="Slug"/>, but numbered against anchors already handed out (`used` is extended).</summary>
        public static string Unique(string headingText, ISet<string> used)
        {
            string slug = Slug(headingText);
            string candidate = slug;
            for (int n = 1; !used.Add(candidate); n++) candidate = slug + "-" + n;
            return candidate;
        }
    }
}
