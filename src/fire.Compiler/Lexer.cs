using fire.Lexing;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace fire.Compiler
{
    public sealed class LexException : Exception
    {
        public int Line { get; }
        public int Column { get; }

        public LexException(string message, int line, int column)
            : base($"{message} ({line}:{column})")
        {
            Line = line;
            Column = column;
        }
    }

    public sealed class Lexer
    {
        private static readonly Dictionary<string, TokenType> Keywords = new()
        {
            ["var"] = TokenType.Var,
            ["func"] = TokenType.Func,
            ["class"] = TokenType.Class,
            ["construct"] = TokenType.Construct,
            ["destruct"] = TokenType.Destruct,
            ["return"] = TokenType.Return,
            ["if"] = TokenType.If,
            ["else"] = TokenType.Else,
            ["while"] = TokenType.While,
            ["for"] = TokenType.For,
            ["foreach"] = TokenType.Foreach,
            ["in"] = TokenType.In,
            ["on"] = TokenType.On,
            ["new"] = TokenType.New,
            ["base"] = TokenType.Base,
            ["this"] = TokenType.This,
            ["try"] = TokenType.Try,
            ["catch"] = TokenType.Catch,
            ["finally"] = TokenType.Finally,
            ["throw"] = TokenType.Throw,
            ["is"] = TokenType.Is,
            ["of"] = TokenType.Of,
            ["from"] = TokenType.From,
            ["under"] = TokenType.Under,
            ["extern"] = TokenType.Extern,
            ["unsafe"] = TokenType.Unsafe,
            ["interface"] = TokenType.Interface,
            ["readonly"] = TokenType.Readonly,
            ["static"] = TokenType.Static,
            ["public"] = TokenType.Public,
            ["private"] = TokenType.Private,
            ["protected"] = TokenType.Protected,
            ["namespace"] = TokenType.Namespace,
            ["enum"] = TokenType.Enum,
            ["with"] = TokenType.With,
            ["extends"] = TokenType.Extends,
            ["switch"] = TokenType.Switch,
            ["case"] = TokenType.Case,
            ["default"] = TokenType.Default,
            ["break"] = TokenType.Break,
            ["continue"] = TokenType.Continue,
            ["where"] = TokenType.Where,
            ["fire"] = TokenType.Fire,
            ["taking"] = TokenType.Taking,
            ["operator"] = TokenType.Operator,
            ["sync"] = TokenType.Sync,
            ["flat"] = TokenType.Flat,
            ["copy"] = TokenType.Copy,
            ["take"] = TokenType.Take,
            ["leave"] = TokenType.Leave,
            ["terminate"] = TokenType.Terminate,
            ["actor"] = TokenType.Actor,
            ["process"] = TokenType.Process,
            ["true"] = TokenType.True,
            ["false"] = TokenType.False,
            ["undefined"] = TokenType.Undefined,
            ["bool"] = TokenType.KwBool,
            ["int"] = TokenType.KwInt,
            ["float"] = TokenType.KwFloat,
            ["char"] = TokenType.KwChar,
            ["string"] = TokenType.KwString,
            ["byte"] = TokenType.KwByte,
            ["and"] = TokenType.And,
            ["or"] = TokenType.Or,
        };

        private readonly string _source;
        private int _pos;
        private int _line = 1;
        private int _col = 1;

        public Lexer(string source)
        {
            _source = source;
        }

        public List<Token> Tokenize()
        {
            var tokens = new List<Token>();

            // The start of the file counts as "start of line" - there is no preceding statement
            // that could wrongly be read on.
            bool pendingNewline = true;

            while (true)
            {
                pendingNewline |= SkipWhitespaceAndComments();

                if (IsAtEnd)
                {
                    tokens.Add(new Token(TokenType.Eof, "", _line, _col, newlineBefore: pendingNewline));
                    break;
                }

                int startLine = _line, startCol = _col, startPos = _pos;
                char c = Peek();
                Token tok;

                if (char.IsDigit(c))
                    tok = ReadNumber(startLine, startCol, pendingNewline);
                else if (IsIdentifierStart(c))
                    tok = ReadIdentifierOrKeyword(startLine, startCol, pendingNewline);
                else if (c == '$' && PeekNext() == '"')
                    tok = ReadInterpolatedString(startLine, startCol, pendingNewline);
                else if (c == '"')
                    tok = ReadString(startLine, startCol, pendingNewline);
                else if (c == '\'')
                    tok = ReadChar(startLine, startCol, pendingNewline);
                else
                    tok = ReadOperatorOrPunctuation(startLine, startCol, pendingNewline);

                tokens.Add(tok with { Length = _pos - startPos });
                pendingNewline = false;
            }
            return tokens;
        }

        // -----------------------------------------------------------
        // Whitespace & comments. Return value: was at least one '\n' skipped?
        // -----------------------------------------------------------
        private bool SkipWhitespaceAndComments()
        {
            bool sawNewline = false;
            while (!IsAtEnd)
            {
                char c = Peek();
                if (c is ' ' or '\t' or '\r')
                {
                    Advance();
                }
                else if (c == '\n')
                {
                    sawNewline = true;
                    Advance();
                }
                else if (c == '/' && PeekNext() == '/')
                {
                    while (!IsAtEnd && Peek() != '\n') Advance();
                }
                else if (c == '/' && PeekNext() == '*')
                {
                    Advance(); Advance();
                    while (!IsAtEnd && !(Peek() == '*' && PeekNext() == '/'))
                    {
                        if (Peek() == '\n') sawNewline = true;
                        Advance();
                    }
                    if (IsAtEnd) throw new LexException("Unterminated block comment", _line, _col);
                    Advance(); Advance();
                }
                else if (c == '_' && TryConsumeLineContinuation())
                {
                    // Deliberately NO sawNewline = true: the whole purpose of '_' as an
                    // explicit line continuation is to NOT let the crossed
                    // line break count as a statement separator.
                }
                else
                {
                    break;
                }
            }
            return sawNewline;
        }

        /// <summary>
        /// Checks for a standalone '_' as an explicit line continuation: the
        /// last "word" of the line, optionally followed by a line comment,
        /// then end of line (or end of file). If that applies, everything up to
        /// and including the line break is consumed and true is returned - this
        /// line break then must not represent a statement separator.
        /// Otherwise nothing is consumed (false); '_' stays for the normal
        /// identifier tokenisation (e.g. as a valid variable name).
        /// </summary>
        private bool TryConsumeLineContinuation()
        {
            if (IsIdentifierPart(PeekNext()))
                return false; // part of a longer identifier, e.g. "_foo" or "foo_bar"

            int offset = 1; // behind the '_'
            while (PeekAt(offset) is ' ' or '\t' or '\r') offset++;

            if (PeekAt(offset) == '/' && PeekAt(offset + 1) == '/')
            {
                offset += 2;
                while (PeekAt(offset) != '\n' && PeekAt(offset) != '\0') offset++;
            }

            if (PeekAt(offset) != '\n' && PeekAt(offset) != '\0')
                return false; // real code still follows in the line -> '_' is an ordinary identifier

            for (int i = 0; i < offset; i++) Advance();
            if (Peek() == '\n') Advance();
            return true;
        }

        // -----------------------------------------------------------
        // Zahlen (inkl. Einheiten-Suffix direkt am Literal)
        // -----------------------------------------------------------
        private Token ReadNumber(int line, int col, bool newlineBefore)
        {
            int start = _pos;

            // '0x'/'0X' (hex) or '0b'/'0B' (binary) - pure integer special forms,
            // no fraction/exponent. Recognised by a leading '0' directly
            // followed by 'x'/'b' AND directly after it at least one digit
            // valid for the respective radix (lookahead, WITHOUT
            // committing yet) - the language has a base unit 'b'/'B' (bit,
            // see Values.Unit.PrefixableBaseUnits), '0b' alone (or '0b'
            // followed by something that is not a valid binary digit, e.g.
            // whitespace or an operator) therefore MUST still be able to mean "zero bit"
            // (an ordinary number '0' with the unit suffix 'b'),
            // not be rejected as a broken binary literal.
            if (Peek() == '0' && (PeekNext() is 'x' or 'X') && IsHexDigit(PeekAt(2)))
                return ReadRadixNumber(line, col, newlineBefore, start, radix: 16, IsHexDigit);
            if (Peek() == '0' && (PeekNext() is 'b' or 'B') && IsBinaryDigit(PeekAt(2)))
                return ReadRadixNumber(line, col, newlineBefore, start, radix: 2, IsBinaryDigit);

            while (!IsAtEnd && char.IsDigit(Peek())) Advance();

            bool isFloat = false;
            if (!IsAtEnd && Peek() == '.' && char.IsDigit(PeekNext()))
            {
                isFloat = true;
                Advance(); // '.'
                while (!IsAtEnd && char.IsDigit(Peek())) Advance();
            }

            string numberText = _source.Substring(start, _pos - start);

            // Unit suffix: letters/µ directly following, without whitespace in between.
            int unitStart = _pos;
            while (!IsAtEnd && IsUnitSuffixChar(Peek())) Advance();
            string unitSuffix = _source.Substring(unitStart, _pos - unitStart);

            string lexeme = numberText + unitSuffix;

            if (isFloat)
            {
                double value = double.Parse(numberText, CultureInfo.InvariantCulture);
                return new Token(TokenType.FloatLiteral, lexeme, line, col, unitSuffix, value, newlineBefore);
            }
            else
            {
                long value = long.Parse(numberText, CultureInfo.InvariantCulture);
                return new Token(TokenType.IntLiteral, lexeme, line, col, unitSuffix, value, newlineBefore);
            }
        }

        private static bool IsHexDigit(char c) =>
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

        private static bool IsBinaryDigit(char c) => c == '0' || c == '1';

        /// <summary>`0x`/`0b` literals (see ReadNumber) - reads the prefix,
        /// then as many digits valid for `radix` as possible (stops at
        /// the first invalid digit, as with any other lexer scan
        /// - so `0b012` would yield the literal `0b01` followed by a
        /// separate `2` token, no error). ALWAYS yields an
        /// `IntLiteral` (never `FloatLiteral` - fractions make no sense
        /// for a bit-pattern notation), but supports the same
        /// unit suffix as an ordinary number literal.</summary>
        private Token ReadRadixNumber(int line, int col, bool newlineBefore, int start, int radix, Func<char, bool> isDigit)
        {
            Advance(); Advance(); // '0x'/'0X'/'0b'/'0B'
            int digitsStart = _pos;
            while (!IsAtEnd && isDigit(Peek())) Advance();

            if (_pos == digitsStart)
                throw new LexException(
                    $"Expected at least one valid digit after '{_source.Substring(start, _pos - start)}'", line, col);

            string digits = _source.Substring(digitsStart, _pos - digitsStart);
            long value = Convert.ToInt64(digits, radix);

            string numberText = _source.Substring(start, _pos - start);
            int unitStart = _pos;
            while (!IsAtEnd && IsUnitSuffixChar(Peek())) Advance();
            string unitSuffix = _source.Substring(unitStart, _pos - unitStart);
            string lexeme = numberText + unitSuffix;

            return new Token(TokenType.IntLiteral, lexeme, line, col, unitSuffix, value, newlineBefore);
        }

        private static bool IsUnitSuffixChar(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == 'µ';

        // -----------------------------------------------------------
        // Identifier / Keywords
        // -----------------------------------------------------------
        private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
        private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

        private Token ReadIdentifierOrKeyword(int line, int col, bool newlineBefore)
        {
            int start = _pos;
            while (!IsAtEnd && IsIdentifierPart(Peek())) Advance();
            string text = _source.Substring(start, _pos - start);

            if (Keywords.TryGetValue(text, out var kwType))
                return new Token(kwType, text, line, col, newlineBefore: newlineBefore);

            return new Token(TokenType.Identifier, text, line, col, newlineBefore: newlineBefore);
        }

        // -----------------------------------------------------------
        // Strings & Chars
        // -----------------------------------------------------------
        private Token ReadString(int line, int col, bool newlineBefore)
        {
            Advance(); // opening "
            var sb = new StringBuilder();
            while (!IsAtEnd && Peek() != '"')
            {
                char c = Advance();
                if (c == '\\')
                {
                    if (IsAtEnd) throw new LexException("Unterminated string literal", line, col);
                    sb.Append(UnescapeChar(Advance()));
                }
                else if (c == '\n')
                {
                    throw new LexException("String literals must not span multiple lines", line, col);
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (IsAtEnd) throw new LexException("Unterminated string literal", line, col);
            Advance(); // closing "

            string value = sb.ToString();
            return new Token(TokenType.StringLiteral, value, line, col, null, value, newlineBefore);
        }

        /// <summary>`$"literal {expression} literal {expression:format} ..."` -
        /// format strings. Already split here into segments (see
        /// InterpolationSegment documentation); the actual re-lexing/parsing
        /// of each `{...}` expression only happens in the parser (ParsePrimary),
        /// this lexer pass only collects the RAW expression substrings
        /// - otherwise it would itself have to know a complete expression grammar.
        /// `{{`/`}}` are escaped literal braces (as with C#/
        /// Python format strings), a normal escape (`\\n` etc.) is treated as
        /// with ReadString. Line breaks are - as with a
        /// normal string literal - not allowed.</summary>
        private Token ReadInterpolatedString(int line, int col, bool newlineBefore)
        {
            Advance(); // '$'
            Advance(); // opening '"'

            var segments = new List<InterpolationSegment>();
            var text = new StringBuilder();
            void FlushText()
            {
                if (text.Length > 0)
                {
                    segments.Add(new InterpolationSegment(false, text.ToString(), null));
                    text.Clear();
                }
            }

            while (true)
            {
                if (IsAtEnd)
                    throw new LexException("Unterminated format string", line, col);

                char c = Peek();

                if (c == '"')
                {
                    Advance();
                    break;
                }
                if (c == '\n')
                    throw new LexException("Format strings must not span multiple lines", line, col);
                if (c == '\\')
                {
                    Advance();
                    if (IsAtEnd) throw new LexException("Unterminated format string", line, col);
                    text.Append(UnescapeChar(Advance()));
                    continue;
                }
                if (c == '{' && PeekNext() == '{')
                {
                    Advance(); Advance();
                    text.Append('{');
                    continue;
                }
                if (c == '}' && PeekNext() == '}')
                {
                    Advance(); Advance();
                    text.Append('}');
                    continue;
                }
                if (c == '{')
                {
                    FlushText();
                    Advance(); // '{'
                    segments.Add(ReadInterpolationExprSegment(line, col));
                    continue;
                }

                text.Append(c);
                Advance();
            }

            FlushText();
            return new Token(TokenType.InterpolatedStringLiteral, "$\"...\"", line, col, null, segments, newlineBefore);
        }

        /// <summary>Reads ONE `{expression[:format]}` section of a
        /// format-string interpolation (see ReadInterpolatedString), directly
        /// AFTER the already consumed opening '{'. Collects the
        /// expression text RAW (no expression grammar of its own here),
        /// but for that has to skip '(', '[', '{' (e.g. a lambda with a block body
        /// INSIDE the interpolation) AND nested string literals
        /// (whose own '{'/'}'/':' must NOT count) correctly
        /// so as not to confuse the closing '}' of the interpolation itself
        /// with one of the nested ones. A ':' ONLY
        /// at the topmost bracket level separates expression and format
        /// specifier (see SPEC: if the expression itself needs a
        /// colon, e.g. the unit coercion, it has to be parenthesised
        /// - '{(x : km)}' instead of '{x : km}').</summary>
        private InterpolationSegment ReadInterpolationExprSegment(int line, int col)
        {
            var expr = new StringBuilder();
            int depth = 0; // '(' + '[' + '{' together - only the interpolation brace itself counts separately

            while (true)
            {
                if (IsAtEnd)
                    throw new LexException("Unterminated expression in a format string", line, col);

                char c = Peek();

                if (c == '"')
                {
                    // Copy a nested string literal RAW (incl.
                    // its own escapes) - its '{'/'}'/':' must not touch the
                    // bracket depth/format detection here,
                    // the actual interpretation only happens when this expression text
                    // is re-lexed in the parser.
                    expr.Append(c);
                    Advance();
                    while (!IsAtEnd && Peek() != '"')
                    {
                        if (Peek() == '\\')
                        {
                            expr.Append(Peek());
                            Advance();
                            if (!IsAtEnd) { expr.Append(Peek()); Advance(); }
                            continue;
                        }
                        expr.Append(Peek());
                        Advance();
                    }
                    if (!IsAtEnd) { expr.Append(Peek()); Advance(); } // closing '"'
                    continue;
                }

                if (c == '(' || c == '[' || c == '{') { depth++; expr.Append(c); Advance(); continue; }
                if (c == ')' || c == ']') { depth--; expr.Append(c); Advance(); continue; }

                if (c == '}')
                {
                    if (depth > 0) { depth--; expr.Append(c); Advance(); continue; }
                    Advance(); // closing '}' of the interpolation
                    return new InterpolationSegment(true, expr.ToString(), null);
                }

                if (c == ':' && depth == 0)
                {
                    Advance(); // ':'
                    var format = new StringBuilder();
                    while (!IsAtEnd && Peek() != '}')
                    {
                        format.Append(Peek());
                        Advance();
                    }
                    if (IsAtEnd)
                        throw new LexException("Unterminated format specifier in a format string", line, col);
                    Advance(); // closing '}'
                    return new InterpolationSegment(true, expr.ToString(), format.ToString());
                }

                if (c == '\n')
                    throw new LexException("Format strings must not span multiple lines", line, col);

                expr.Append(c);
                Advance();
            }
        }

        private Token ReadChar(int line, int col, bool newlineBefore)
        {
            Advance(); // opening '
            if (IsAtEnd) throw new LexException("Unterminated char literal", line, col);

            char value;
            char c = Advance();
            if (c == '\\')
            {
                if (IsAtEnd) throw new LexException("Unterminated char literal", line, col);
                value = UnescapeChar(Advance());
            }
            else
            {
                value = c;
            }

            if (IsAtEnd || Peek() != '\'')
                throw new LexException("A char literal must contain exactly one character", line, col);
            Advance(); // closing '

            return new Token(TokenType.CharLiteral, value.ToString(), line, col, null, value, newlineBefore);
        }

        private static char UnescapeChar(char c) => c switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '\\' => '\\',
            '\'' => '\'',
            '"' => '"',
            '0' => '\0',
            _ => c,
        };

        // -----------------------------------------------------------
        // Operatoren & Interpunktion
        // -----------------------------------------------------------
        private Token ReadOperatorOrPunctuation(int line, int col, bool newlineBefore)
        {
            char c = Advance();
            switch (c)
            {
                case '+':
                    if (Match('+')) return Tok(TokenType.PlusPlus, "++", line, col, newlineBefore);
                    return Tok(TokenType.Plus, "+", line, col, newlineBefore);
                case '-':
                    if (Match('-')) return Tok(TokenType.MinusMinus, "--", line, col, newlineBefore);
                    return Tok(TokenType.Minus, "-", line, col, newlineBefore);
                case '*': return Tok(TokenType.Star, "*", line, col, newlineBefore);
                case '%': return Tok(TokenType.Percent, "%", line, col, newlineBefore);
                case '.': return Tok(TokenType.Dot, ".", line, col, newlineBefore);
                case '~': return Tok(TokenType.Tilde, "~", line, col, newlineBefore);
                case ',': return Tok(TokenType.Comma, ",", line, col, newlineBefore);
                case ';': return Tok(TokenType.Semicolon, ";", line, col, newlineBefore);
                case '(': return Tok(TokenType.LParen, "(", line, col, newlineBefore);
                case ')': return Tok(TokenType.RParen, ")", line, col, newlineBefore);
                case '{': return Tok(TokenType.LBrace, "{", line, col, newlineBefore);
                case '}': return Tok(TokenType.RBrace, "}", line, col, newlineBefore);
                case '[': return Tok(TokenType.LBracket, "[", line, col, newlineBefore);
                case ']': return Tok(TokenType.RBracket, "]", line, col, newlineBefore);
                case '#':
                    // `##` is a synonym for `!=` (`#` alone is the bitwise xor or the start of a directive)
                    if (Match('#')) return Tok(TokenType.NotEq, "##", line, col, newlineBefore);
                    return Tok(TokenType.Hash, "#", line, col, newlineBefore);

                case '/':
                    return Tok(TokenType.Slash, "/", line, col, newlineBefore);

                case ':':
                    return Tok(TokenType.Colon, ":", line, col, newlineBefore);

                case '!':
                    if (Match('='))
                        return Tok(TokenType.NotEq, "!=", line, col, newlineBefore);
                    return Tok(TokenType.Bang, "!", line, col, newlineBefore);

                case '=':
                    if (Match('>'))
                        return Tok(TokenType.Arrow, "=>", line, col, newlineBefore);
                    if (Match('='))
                        return Tok(TokenType.Eq, "==", line, col, newlineBefore);
                    return Tok(TokenType.Assign, "=", line, col, newlineBefore);

                case '<':
                    if (Match('='))
                        return Tok(TokenType.LtEq, "<=", line, col, newlineBefore);
                    return Tok(TokenType.Lt, "<", line, col, newlineBefore);

                case '>':
                    if (Match('='))
                        return Tok(TokenType.GtEq, ">=", line, col, newlineBefore);
                    return Tok(TokenType.Gt, ">", line, col, newlineBefore);

                case '&':
                    if (Match('&'))
                        return Tok(TokenType.And, "&&", line, col, newlineBefore);
                    return Tok(TokenType.Amp, "&", line, col, newlineBefore);

                case '|':
                    if (Match('|'))
                        return Tok(TokenType.Or, "||", line, col, newlineBefore);
                    return Tok(TokenType.Pipe, "|", line, col, newlineBefore);

                case '^':
                    return Tok(TokenType.Caret, "^", line, col, newlineBefore);

                default:
                    throw new LexException($"Unexpected character '{c}'", line, col);
            }
        }

        private static Token Tok(TokenType type, string lexeme, int line, int col, bool newlineBefore) =>
            new(type, lexeme, line, col, newlineBefore: newlineBefore);

        // -----------------------------------------------------------
        // Low-level character handling with line/column counting
        // -----------------------------------------------------------
        private bool IsAtEnd => _pos >= _source.Length;

        private char Peek() => IsAtEnd ? '\0' : _source[_pos];
        private char PeekNext() => _pos + 1 >= _source.Length ? '\0' : _source[_pos + 1];
        private char PeekAt(int offset) => _pos + offset >= _source.Length ? '\0' : _source[_pos + offset];

        private char Advance()
        {
            char c = _source[_pos++];
            if (c == '\n') { _line++; _col = 1; }
            else { _col++; }
            return c;
        }

        private bool Match(char expected)
        {
            if (IsAtEnd || _source[_pos] != expected) return false;
            Advance();
            return true;
        }
    }
}
