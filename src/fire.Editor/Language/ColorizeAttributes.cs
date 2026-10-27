using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace fire.Editor
{
    /// <summary>
    /// The design-time attribute `[Colorize 0xRRGGBB]` (or `[Colorize "#RRGGBB"]`) before a field, property or method: the editor shows the name of that member in
    /// the color - where it is declared and wherever it is used as a member (`amp.In`, `this.In`). The compiler keeps attributes it does not know and ignores them
    /// (SPEC 5.12), so it changes nothing in the program. The circuit generator puts it on the ports of a circuit, in the colors of the schematic.
    /// </summary>
    public static class ColorizeAttributes
    {
        // the attribute (also with parentheses: [Colorize(0x6010C0)]), then further attributes, doc comments and blank lines, then the declaration up to "(", "{", "=" or
        // the end of its line
        private static readonly Regex Attribute = new(
            @"\[\s*Colorize\s*\(?\s*(?:0[xX](?<hex>[0-9A-Fa-f]{6})|""#?(?<hex>[0-9A-Fa-f]{6})""|#(?<hex>[0-9A-Fa-f]{6}))\s*\)?\s*\]" +
            @"(?:\s*(?:\[[^\]\r\n]*\]|//[^\r\n]*))*\s*(?<decl>[^\r\n({=]*)",
            RegexOptions.Compiled);

        private static readonly Regex Name = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

        /// <summary>The members with a color in `source`: name -> 0xRRGGBB.</summary>
        public static Dictionary<string, int> Collect(string source)
        {
            var colors = new Dictionary<string, int>(StringComparer.Ordinal);
            Collect(source, colors);
            return colors;
        }

        /// <summary>Adds the members with a color in `source` to `colors` (a later one of the same name wins).</summary>
        public static void Collect(string source, Dictionary<string, int> colors)
        {
            if (string.IsNullOrEmpty(source) || source.IndexOf("Colorize", StringComparison.Ordinal) < 0) return;
            foreach (Match m in Attribute.Matches(source))
            {
                // the name is the last word of the declaration ("Circuits.Port In", "float:V Level", "undefined Run")
                string? name = null;
                foreach (Match w in Name.Matches(m.Groups["decl"].Value)) name = w.Value;
                if (name == null) continue;
                colors[name] = int.Parse(m.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Gives the identifiers of `spans` that name a member with a color that color: after a '.' (a use as member), or anywhere in a file that declares
        /// it (`declaredHere`: there the name is the member itself, also without `this.`).</summary>
        public static void Apply(List<HighlightSpan> spans, string source, IReadOnlyDictionary<string, int> colors, IReadOnlyDictionary<string, int> declaredHere)
        {
            if (colors.Count == 0 && declaredHere.Count == 0) return;
            for (int i = 0; i < spans.Count; i++)
            {
                var s = spans[i];
                if (s.Category != HighlightCategory.Identifier || s.Start + s.Length > source.Length) continue;
                string word = source.Substring(s.Start, s.Length);
                int rgb;
                if (declaredHere.TryGetValue(word, out rgb)) { }
                else if (colors.TryGetValue(word, out rgb) && AfterDot(source, s.Start)) { }
                else continue;
                spans[i] = new HighlightSpan(s.Start, s.Length, HighlightCategory.Colorized, rgb);
            }
        }

        private static bool AfterDot(string source, int at)
        {
            int k = at - 1;
            while (k >= 0 && (source[k] == ' ' || source[k] == '\t')) k--;
            return k >= 0 && source[k] == '.';
        }
    }
}
