using System;
using System.Collections.Generic;
using ScriptLang.Lexing;

namespace ScriptLang.Editor
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
    /// Tokenisiert Quelltext mit dem ECHTEN ScriptLang-Lexer für Syntax-
    /// Highlighting - bewusst KEINE eigene, zweite (und potenziell vom echten
    /// Verhalten abweichende) Tokenisierung. Der Lexer kennt Kommentare nicht
    /// als eigenen Token-Typ (er überspringt sie beim Tokenisieren einfach) -
    /// deshalb wird zusätzlich in den LÜCKEN zwischen echten Tokens nach
    /// '//...'- und '/* ... */'-Läufen gesucht. Das ist sicher (kein
    /// versehentliches Einfärben von "//" INNERHALB eines String-Literals),
    /// weil String-/Char-Literale bereits vom echten Lexer als eigene Tokens
    /// erkannt und damit aus den durchsuchten Lücken ausgeklammert sind.
    ///
    /// Bei einer LexException (z.B. während des Tippens ein noch unbeendetes
    /// String-Literal) wird GAR NICHT eingefärbt, statt nur bis zur
    /// Fehlerstelle - der Rest würde sonst verwirrend "verwaist" aussehen. Der
    /// nächste erfolgreiche Tokenisierungslauf (z.B. nach dem nächsten
    /// Tastendruck) holt die Hervorhebung dann automatisch nach.
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
            TokenType.With, TokenType.Extends,
            TokenType.Switch, TokenType.Case, TokenType.Default, TokenType.Break, TokenType.Continue,
            TokenType.Where,
            TokenType.Fire, TokenType.Taking, TokenType.Sync, TokenType.Flat,
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
                int length = Math.Min(tok.Lexeme.Length, source.Length - start);

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

        /// <summary>1-basierte (Zeile, Spalte) -> absoluter Zeichen-Offset im
        /// Quelltext.</summary>
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
