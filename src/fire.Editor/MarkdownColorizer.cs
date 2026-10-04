using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace fire.Editor
{
    /// <summary>Hebt Markdown-Syntax im QUELLTEXT-Editor hervor (Überschriften
    /// größer/fett, **fett**, *kursiv*, `Code`, Links, Zitate, Listen-Marken,
    /// Code-Blöcke) - wie HighlightingColorizer rein beim Zeichnen, das
    /// Dokument bleibt unberührt.
    ///
    /// Zeilenübergreifender Zustand (innerhalb eines ```-Blocks) steckt in
    /// `FencedLines`, das MarkdownEditorControl bei jeder Textänderung neu
    /// berechnet (siehe ComputeFencedLines).</summary>
    internal sealed class MarkdownColorizer : DocumentColorizingTransformer
    {
        private static readonly Brush HeadingBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0x47, 0x9E)).AsFrozen();
        private static readonly Brush MarkBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0x7F, 0x93)).AsFrozen();
        private static readonly Brush CodeBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x56, 0x6F)).AsFrozen();
        private static readonly Brush LinkBrush = new SolidColorBrush(Color.FromRgb(0xC0, 0x6A, 0xDE)).AsFrozen();
        private static readonly Brush QuoteBrush = new SolidColorBrush(Color.FromRgb(0xF9, 0xCB, 0x5C)).AsFrozen();
        private static readonly Brush ListBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x91, 0x42)).AsFrozen();
        private static readonly Brush FenceBackground = EditorTheme.CodeBlockBackground;
        private static readonly double[] HeadingScale = { 1.6, 1.4, 1.25, 1.15, 1.08, 1.0 };

        private static readonly Regex Heading = new(@"^ {0,3}(#{1,6})(\s|$)", RegexOptions.Compiled);
        private static readonly Regex Bold = new(@"(\*\*|__)(?=\S)(.+?)(?<=\S)\1", RegexOptions.Compiled);
        private static readonly Regex Italic = new(@"(?<![*\w])([*_])(?![*\s])(.+?)(?<![*\s])\1(?![*\w])", RegexOptions.Compiled);
        private static readonly Regex Strike = new(@"~~(?=\S)(.+?)(?<=\S)~~", RegexOptions.Compiled);
        private static readonly Regex InlineCode = new(@"(`+)(?!`)(.+?)(?<!`)\1(?!`)", RegexOptions.Compiled);
        private static readonly Regex Link = new(@"!?\[[^\]\n]*\]\([^)\n]*\)|<https?://[^>\s]+>", RegexOptions.Compiled);
        private static readonly Regex ListMark = new(@"^\s*([-*+]|\d{1,9}[.)])\s", RegexOptions.Compiled);
        private static readonly Regex Quote = new(@"^\s{0,3}>", RegexOptions.Compiled);
        private static readonly Regex Rule = new(@"^ {0,3}([-*_])(\s*\1){2,}\s*$", RegexOptions.Compiled);

        /// <summary>1-basierte Zeilen, die zu einem Code-Block gehören (inkl. der Zaun-Zeilen).</summary>
        public IReadOnlySet<int> FencedLines { get; set; } = new HashSet<int>();

        protected override void ColorizeLine(DocumentLine line)
        {
            int start = line.Offset;
            int end = line.EndOffset;
            if (end <= start) return;

            if (FencedLines.Contains(line.LineNumber))
            {
                ChangeLinePart(start, end, el =>
                {
                    el.TextRunProperties.SetBackgroundBrush(FenceBackground);
                });
                return;
            }

            string text = CurrentContext.Document.GetText(start, end - start);

            var heading = Heading.Match(text);
            if (heading.Success)
            {
                double scale = HeadingScale[heading.Groups[1].Length - 1];
                ChangeLinePart(start, end, el =>
                {
                    var tf = el.TextRunProperties.Typeface;
                    el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, tf.Style, FontWeights.Bold, tf.Stretch));
                    el.TextRunProperties.SetForegroundBrush(HeadingBrush);
                    el.TextRunProperties.SetFontRenderingEmSize(el.TextRunProperties.FontRenderingEmSize * scale);
                });
                ChangeLinePart(start, start + heading.Groups[1].Index + heading.Groups[1].Length, el => el.TextRunProperties.SetForegroundBrush(MarkBrush));
                return;
            }

            if (Rule.IsMatch(text))
            {
                ChangeLinePart(start, end, el => el.TextRunProperties.SetForegroundBrush(MarkBrush));
                return;
            }

            if (Quote.IsMatch(text))
                ChangeLinePart(start, end, el => el.TextRunProperties.SetForegroundBrush(QuoteBrush));

            var list = ListMark.Match(text);
            if (list.Success)
                ChangeLinePart(start + list.Groups[1].Index, start + list.Groups[1].Index + list.Groups[1].Length,
                    el => el.TextRunProperties.SetForegroundBrush(ListBrush));

            // Code zuletzt prüfen, aber als "geschützte" Bereiche merken: darin ist sonst nichts Markdown
            var code = new List<(int S, int E)>();
            foreach (Match m in InlineCode.Matches(text)) code.Add((m.Index, m.Index + m.Length));
            bool InCode(int i) { foreach (var (s, e) in code) if (i >= s && i < e) return true; return false; }

            foreach (Match m in Bold.Matches(text))
            {
                if (InCode(m.Index)) continue;
                ChangeLinePart(start + m.Index, start + m.Index + m.Length, el =>
                {
                    var tf = el.TextRunProperties.Typeface;
                    el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, tf.Style, FontWeights.Bold, tf.Stretch));
                });
                Mark(start + m.Index, 2); Mark(start + m.Index + m.Length - 2, 2);
            }
            foreach (Match m in Italic.Matches(text))
            {
                if (InCode(m.Index)) continue;
                ChangeLinePart(start + m.Index, start + m.Index + m.Length, el =>
                {
                    var tf = el.TextRunProperties.Typeface;
                    el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, FontStyles.Italic, tf.Weight, tf.Stretch));
                });
                Mark(start + m.Index, 1); Mark(start + m.Index + m.Length - 1, 1);
            }
            foreach (Match m in Strike.Matches(text))
            {
                if (InCode(m.Index)) continue;
                ChangeLinePart(start + m.Index, start + m.Index + m.Length, el => el.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough));
            }
            foreach (Match m in Link.Matches(text))
            {
                if (InCode(m.Index)) continue;
                ChangeLinePart(start + m.Index, start + m.Index + m.Length, el => el.TextRunProperties.SetForegroundBrush(LinkBrush));
            }
            foreach (var (s, e) in code)
                ChangeLinePart(start + s, start + e, el => el.TextRunProperties.SetForegroundBrush(CodeBrush));
        }

        private void Mark(int offset, int length) =>
            ChangeLinePart(offset, offset + length, el => el.TextRunProperties.SetForegroundBrush(MarkBrush));

        /// <summary>Welche Zeilen liegen in einem ```/~~~-Block? Zusätzlich
        /// die Bereiche (Start-/Endoffset des INHALTS) der Blöcke mit
        /// Sprache `fire`, für deren Einfärbung im Quelltext.</summary>
        public static HashSet<int> ComputeFencedLines(TextDocument doc, List<(int Start, int End)> fireBlocks)
        {
            var lines = new HashSet<int>();
            string? marker = null;
            bool fire = false;
            int bodyStart = 0;
            foreach (var line in doc.Lines)
            {
                string t = doc.GetText(line.Offset, line.Length);
                string trimmed = t.TrimStart();
                if (marker == null)
                {
                    if (t.Length - trimmed.Length <= 3 && (trimmed.StartsWith("```") || trimmed.StartsWith("~~~")))
                    {
                        char c = trimmed[0];
                        int n = 0;
                        while (n < trimmed.Length && trimmed[n] == c) n++;
                        marker = new string(c, n);
                        string lang = trimmed.Substring(n).Trim().Split(' ', '{')[0];
                        fire = lang.Equals("fire", StringComparison.OrdinalIgnoreCase) || lang.Equals("firescript", StringComparison.OrdinalIgnoreCase);
                        bodyStart = line.Offset + line.TotalLength;
                        lines.Add(line.LineNumber);
                    }
                }
                else
                {
                    lines.Add(line.LineNumber);
                    string tt = t.Trim();
                    if (tt.Length >= marker.Length && tt[0] == marker[0] && tt.Trim(marker[0]).Length == 0)
                    {
                        if (fire && line.Offset > bodyStart) fireBlocks.Add((bodyStart, line.Offset));
                        marker = null;
                    }
                }
            }
            // Nicht geschlossener Block: bis zum Dokumentende
            if (marker != null && fire && doc.TextLength > bodyStart) fireBlocks.Add((bodyStart, doc.TextLength));
            return lines;
        }
    }
}
