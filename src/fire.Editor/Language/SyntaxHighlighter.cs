using System;
using System.Collections.Generic;
using fire.Compiler;
using fire.Lexing;

namespace fire.Editor
{
    public enum HighlightCategory
    {
        Default,
        Keyword,
        Type,
        String,
        Char,
        Number,
        Identifier,
        Comment,
        /// <summary>A line of a branch of `#if` that is not taken (greyed out).</summary>
        Inactive,
    }

    public readonly struct HighlightSpan
    {
        public int Start { get; }
        public int Length { get; }
        public HighlightCategory Category { get; }

        public HighlightSpan(int start, int length, HighlightCategory category)
        {
            Start = start;
            Length = length;
            Category = category;
        }
    }

    /// <summary>
    /// Tokenises source text with the REAL fire lexer for syntax
    /// highlighting - deliberately NO second tokenisation of its own (potentially deviating from the real
    /// behaviour). The lexer does not know comments
    /// as a token type of their own (it simply skips them when tokenising) -
    /// therefore the GAPS between real tokens are additionally searched
    /// for '//...' and '/* ... */' runs. This is safe (no
    /// accidental colouring of "//" INSIDE a string literal),
    /// because string/char literals have already been recognised by the real lexer as tokens of their own
    /// and are thus excluded from the searched gaps.
    ///
    /// On a LexException (e.g. an as yet unterminated
    /// string literal while typing) NOTHING is coloured at all, instead of only up to the
    /// error position - the rest would otherwise look confusingly "orphaned". The
    /// next successful tokenisation run (e.g. after the next
    /// keypress) then catches up with the highlighting automatically.
    /// </summary>
    public static class SyntaxHighlighter
    {
        private static readonly HashSet<TokenType> Keywords = new()
        {
            TokenType.Var, TokenType.Func, TokenType.Class, TokenType.Construct, TokenType.Destruct,
            TokenType.Return, TokenType.If, TokenType.Else, TokenType.While, TokenType.For,
            TokenType.Foreach, TokenType.In, TokenType.On, TokenType.New, TokenType.Base, TokenType.This,
            TokenType.Try, TokenType.Catch, TokenType.Finally, TokenType.Throw,
            TokenType.Is, TokenType.Of, TokenType.From, TokenType.Under,
            TokenType.Extern, TokenType.Unsafe, TokenType.Interface,
            TokenType.Readonly, TokenType.Enum,
            TokenType.Public, TokenType.Private, TokenType.Protected,
            TokenType.Namespace,
            TokenType.With, TokenType.Extends,
            TokenType.Switch, TokenType.Case, TokenType.Default, TokenType.Break, TokenType.Continue,
            TokenType.Where,
            TokenType.Fire, TokenType.Taking, TokenType.Sync, TokenType.Flat, TokenType.Copy, TokenType.Take,
            TokenType.Operator,
            TokenType.Leave, TokenType.Terminate, TokenType.Actor, TokenType.Process,
            TokenType.True, TokenType.False, TokenType.Undefined,
            TokenType.And, TokenType.Or,
        };

        private static readonly HashSet<TokenType> TypeKeywords = new()
        {
            TokenType.KwBool, TokenType.KwInt, TokenType.KwFloat, TokenType.KwChar, TokenType.KwString,
            TokenType.KwByte,
        };

        /// <summary>Like <see cref="Highlight(string)"/>, and the lines of branches of `#if` that are not taken under <paramref name="symbols"/> are greyed out (SPEC 8.1.7): what is in them is
        /// not read by the compiler, so it gets no colours of its own.</summary>
        public static List<HighlightSpan> Highlight(string source, ISet<string>? symbols)
        {
            if (symbols == null || string.IsNullOrEmpty(source) || source.IndexOf('#') < 0) return Highlight(source);
            var inactive = ConditionalSymbols.InactiveLines(source, symbols);
            if (!inactive.Contains(true)) return Highlight(source);

            // The inactive lines are blanked (same offsets): the lexer does not trip over what is not code in this configuration, and they get no colours of their own.
            int[] lineStarts = ComputeLineStarts(source);
            var blanked = source.ToCharArray();
            var greyed = new List<HighlightSpan>();
            for (int line = 0; line < inactive.Length && line < lineStarts.Length; line++)
            {
                if (!inactive[line]) continue;
                int start = lineStarts[line];
                int end = line + 1 < lineStarts.Length ? lineStarts[line + 1] - 1 : source.Length;   // without the line break
                if (end > start && source[end - 1] == '\r') end--;
                if (end <= start) continue;
                for (int k = start; k < end; k++) blanked[k] = ' ';
                greyed.Add(new HighlightSpan(start, end - start, HighlightCategory.Inactive));
            }
            var result = Highlight(new string(blanked));
            result.AddRange(greyed);
            result.Sort((x, y) => x.Start.CompareTo(y.Start));
            return result;
        }

        public static List<HighlightSpan> Highlight(string source)
        {
            var spans = new List<HighlightSpan>();
            if (string.IsNullOrEmpty(source)) return spans;

            List<Token> tokens;
            try
            {
                tokens = new Lexer(source).Tokenize();
            }
            catch (LexException)
            {
                return spans;
            }

            int[] lineStarts = ComputeLineStarts(source);
            int lastEnd = 0;

            foreach (var tok in tokens)
            {
                if (tok.Type == TokenType.Eof) break;

                int start = ToOffset(lineStarts, tok.Line, tok.Column);
                if (start < 0 || start < lastEnd || start > source.Length) continue;
                int length = Math.Min(tok.Length > 0 ? tok.Length : tok.Lexeme.Length, source.Length - start);

                ScanCommentsInGap(source, lastEnd, start, spans);

                var category = CategoryOf(tok.Type);
                if (category != HighlightCategory.Default)
                    spans.Add(new HighlightSpan(start, length, category));

                lastEnd = start + length;
            }

            ScanCommentsInGap(source, lastEnd, source.Length, spans);
            return spans;
        }

        private static HighlightCategory CategoryOf(TokenType type)
        {
            if (type == TokenType.StringLiteral) return HighlightCategory.String;
            if (type == TokenType.InterpolatedStringLiteral) return HighlightCategory.String;
            if (type == TokenType.CharLiteral) return HighlightCategory.Char;
            if (type is TokenType.IntLiteral or TokenType.FloatLiteral) return HighlightCategory.Number;
            if (Keywords.Contains(type)) return HighlightCategory.Keyword;
            if (TypeKeywords.Contains(type)) return HighlightCategory.Type;
            if (type == TokenType.Identifier) return HighlightCategory.Identifier;
            return HighlightCategory.Default;
        }

        private static int[] ComputeLineStarts(string source)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < source.Length; i++)
                if (source[i] == '\n') starts.Add(i + 1);
            return starts.ToArray();
        }

        /// <summary>1-based (line, column) -> absolute character offset in the
        /// source text.</summary>
        private static int ToOffset(int[] lineStarts, int line, int column)
        {
            int idx = line - 1;
            if (idx < 0 || idx >= lineStarts.Length) return -1;
            return lineStarts[idx] + (column - 1);
        }

        private static void ScanCommentsInGap(string source, int from, int to, List<HighlightSpan> spans)
        {
            int i = Math.Max(from, 0);
            to = Math.Min(to, source.Length);

            while (i < to)
            {
                if (i + 1 < to && source[i] == '/' && source[i + 1] == '/')
                {
                    int nl = source.IndexOf('\n', i, to - i);
                    int end = nl < 0 ? to : nl;
                    spans.Add(new HighlightSpan(i, end - i, HighlightCategory.Comment));
                    i = end;
                }
                else if (i + 1 < to && source[i] == '/' && source[i + 1] == '*')
                {
                    int close = source.IndexOf("*/", i, StringComparison.Ordinal);
                    int end = (close < 0 || close + 2 > to) ? to : close + 2;
                    spans.Add(new HighlightSpan(i, end - i, HighlightCategory.Comment));
                    i = end;
                }
                else
                {
                    i++;
                }
            }
        }
    }
}
