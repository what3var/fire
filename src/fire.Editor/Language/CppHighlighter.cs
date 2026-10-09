using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace fire.Editor
{
    /// <summary>Colours for C and C++ (the natives of a project, docs/PROJECTS.md): one pass with one expression, so the parts never overlap - comments and strings first, then preprocessor lines, numbers, words.</summary>
    internal static class CppHighlighter
    {
        private static readonly string Keywords = string.Join('|', new[]
        {
            "alignas", "alignof", "auto", "bool", "break", "case", "catch", "class", "const", "constexpr", "continue", "default", "delete", "do", "else", "enum", "explicit", "extern", "false", "for", "friend",
            "goto", "if", "inline", "namespace", "new", "noexcept", "nullptr", "operator", "private", "protected", "public", "return", "sizeof", "static", "static_assert", "struct", "switch", "template",
            "this", "throw", "true", "try", "typedef", "typename", "union", "using", "virtual", "volatile", "while", "override", "final",
        });
        private static readonly string Types = string.Join('|', new[]
        {
            "void", "char", "short", "int", "long", "float", "double", "signed", "unsigned", "size_t", "int8_t", "int16_t", "int32_t", "int64_t", "uint8_t", "uint16_t", "uint32_t", "uint64_t", "char16_t",
            "string", "vector", "Value", "OwnList", "Str", "Buf", "Arr", "Obj",
        });

        private static readonly Regex Token = new(
            @"(?<comment>//[^\n]*|/\*.*?(?:\*/|\z))" +
            @"|(?<string>""(?:\\.|[^""\\\n])*""?)" +
            @"|(?<char>'(?:\\.|[^'\\\n])*'?)" +
            @"|(?<pp>^[ \t]*#[ \t]*\w+(?:[ \t]*<[^>\n]*>)?)" +
            @"|(?<number>\b(?:0[xX][0-9a-fA-F']+|\d[\d']*(?:\.\d+)?(?:[eE][+-]?\d+)?)[uUlLfF]*\b)" +
            @"|(?<keyword>\b(?:" + Keywords + @")\b)" +
            @"|(?<type>\b(?:" + Types + @")\b)",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.Multiline);

        public static List<HighlightSpan> Highlight(string text)
        {
            var spans = new List<HighlightSpan>();
            foreach (Match m in Token.Matches(text))
            {
                HighlightCategory category =
                    m.Groups["comment"].Success ? HighlightCategory.Comment :
                    m.Groups["string"].Success ? HighlightCategory.String :
                    m.Groups["char"].Success ? HighlightCategory.Char :
                    m.Groups["pp"].Success ? HighlightCategory.Keyword :
                    m.Groups["number"].Success ? HighlightCategory.Number :
                    m.Groups["keyword"].Success ? HighlightCategory.Keyword : HighlightCategory.Type;
                spans.Add(new HighlightSpan(m.Index, m.Length, category));
            }
            return spans;
        }

        public static bool IsCppFile(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is ".h" or ".hpp" or ".hh" or ".hxx" or ".c" or ".cc" or ".cpp" or ".cxx" or ".inl";
    }
}
