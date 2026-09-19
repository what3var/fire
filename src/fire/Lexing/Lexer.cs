using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace fire.Lexing
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

            // Dateianfang zählt als "Zeilenanfang" - es gibt kein Vorgänger-Statement,
            // das fälschlich weitergelesen werden könnte.
            bool pendingNewline = true;

            while (true)
            {
                pendingNewline |= SkipWhitespaceAndComments();

                if (IsAtEnd)
                {
                    tokens.Add(new Token(TokenType.Eof, "", _line, _col, newlineBefore: pendingNewline));
                    break;
                }

                int startLine = _line, startCol = _col;
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

                tokens.Add(tok);
                pendingNewline = false;
            }
            return tokens;
        }

        // -----------------------------------------------------------
        // Whitespace & Kommentare. Rückgabe: wurde mind. ein '\n' übersprungen?
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
                    if (IsAtEnd) throw new LexException("Unbeendeter Blockkommentar", _line, _col);
                    Advance(); Advance();
                }
                else if (c == '_' && TryConsumeLineContinuation())
                {
                    // Bewusst KEIN sawNewline = true: der ganze Zweck von '_' als
                    // explizite Zeilenfortsetzung ist es, den überschrittenen
                    // Zeilenumbruch NICHT als Statement-Trenner zählen zu lassen.
                }
                else
                {
                    break;
                }
            }
            return sawNewline;
        }

        /// <summary>
        /// Prüft auf ein alleinstehendes '_' als explizite Zeilenfortsetzung: das
        /// letzte "Wort" der Zeile, optional gefolgt von einem Zeilenkommentar,
        /// dann Zeilenende (oder Dateiende). Trifft das zu, wird alles bis
        /// inklusive des Zeilenumbruchs konsumiert und true zurückgegeben - dieser
        /// Zeilenumbruch darf dann keinen Statement-Trenner darstellen.
        /// Andernfalls wird nichts konsumiert (false); '_' bleibt für die normale
        /// Identifier-Tokenisierung stehen (z.B. als gültiger Variablenname).
        /// </summary>
        private bool TryConsumeLineContinuation()
        {
            if (IsIdentifierPart(PeekNext()))
                return false; // Teil eines längeren Bezeichners, z.B. "_foo" oder "foo_bar"

            int offset = 1; // hinter dem '_'
            while (PeekAt(offset) is ' ' or '\t' or '\r') offset++;

            if (PeekAt(offset) == '/' && PeekAt(offset + 1) == '/')
            {
                offset += 2;
                while (PeekAt(offset) != '\n' && PeekAt(offset) != '\0') offset++;
            }

            if (PeekAt(offset) != '\n' && PeekAt(offset) != '\0')
                return false; // in der Zeile folgt noch echter Code -> '_' ist ein normaler Bezeichner

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

            // '0x'/'0X' (hex) bzw. '0b'/'0B' (binär) - reine Ganzzahl-Sonderformen,
            // kein Bruchteil/Exponent. Erkannt an einer führenden '0' direkt
            // gefolgt von 'x'/'b' UND direkt danach mindestens einer für den
            // jeweiligen Radix gültigen Ziffer (Lookahead, OHNE schon zu
            // committen) - die Sprache hat eine Basiseinheit 'b'/'B' (Bit,
            // siehe Values.Unit.PrefixableBaseUnits), '0b' allein (bzw. '0b'
            // gefolgt von etwas, das keine gültige Binärziffer ist, z.B.
            // Leerraum oder ein Operator) MUSS deshalb weiterhin "null Bit"
            // bedeuten können (normale Zahl '0' mit Einheiten-Suffix 'b'),
            // nicht als kaputtes Binärliteral abgelehnt werden.
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

            // Einheiten-Suffix: direkt anschließende Buchstaben/µ, ohne Whitespace dazwischen.
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

        /// <summary>`0x`/`0b`-Literale (siehe ReadNumber) - liest den Präfix,
        /// dann so viele für `radix` gültige Ziffern wie möglich (bricht bei
        /// der ersten ungültigen Ziffer ab, wie bei jedem anderen Lexer-Scan
        /// auch - `0b012` ergäbe also das Literal `0b01` gefolgt von einem
        /// separaten `2`-Token, keinen Fehler). Ergibt IMMER ein
        /// `IntLiteral` (nie `FloatLiteral` - Bruchteile ergeben für eine
        /// Bitmuster-Schreibweise keinen Sinn), unterstützt aber denselben
        /// Einheiten-Suffix wie ein normales Zahlen-Literal.</summary>
        private Token ReadRadixNumber(int line, int col, bool newlineBefore, int start, int radix, Func<char, bool> isDigit)
        {
            Advance(); Advance(); // '0x'/'0X'/'0b'/'0B'
            int digitsStart = _pos;
            while (!IsAtEnd && isDigit(Peek())) Advance();

            if (_pos == digitsStart)
                throw new LexException(
                    $"Erwarte mindestens eine gültige Ziffer nach '{_source.Substring(start, _pos - start)}'", line, col);

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
                    if (IsAtEnd) throw new LexException("Unbeendetes String-Literal", line, col);
                    sb.Append(UnescapeChar(Advance()));
                }
                else if (c == '\n')
                {
                    throw new LexException("String-Literal über Zeilenende nicht erlaubt", line, col);
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (IsAtEnd) throw new LexException("Unbeendetes String-Literal", line, col);
            Advance(); // closing "

            string value = sb.ToString();
            return new Token(TokenType.StringLiteral, value, line, col, null, value, newlineBefore);
        }

        /// <summary>`$"literal {ausdruck} literal {ausdruck:Format} ..."` -
        /// Format-Strings. Zerlegt hier bereits in Segmente (siehe
        /// InterpolationSegment-Doku); die eigentliche Neu-Lexung/-Parsung
        /// jedes `{...}`-Ausdrucks passiert erst im Parser (ParsePrimary),
        /// dieser Lexer-Durchlauf sammelt nur die ROHEN Ausdrucks-Teilstrings
        /// ein - er müsste sonst selbst eine vollständige Ausdrucks-Grammatik
        /// kennen. `{{`/`}}` sind escapte literale Klammern (wie bei C#/
        /// Python-Format-Strings), ein normaler Escape (`\n` etc.) wird wie
        /// bei ReadString behandelt. Zeilenumbrüche sind - wie bei einem
        /// normalen String-Literal - nicht erlaubt.</summary>
        private Token ReadInterpolatedString(int line, int col, bool newlineBefore)
        {
            Advance(); // '$'
            Advance(); // öffnendes '"'

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
                    throw new LexException("Unbeendeter Format-String", line, col);

                char c = Peek();

                if (c == '"')
                {
                    Advance();
                    break;
                }
                if (c == '\n')
                    throw new LexException("Format-String über Zeilenende nicht erlaubt", line, col);
                if (c == '\\')
                {
                    Advance();
                    if (IsAtEnd) throw new LexException("Unbeendeter Format-String", line, col);
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

        /// <summary>Liest EINEN `{ausdruck[:format]}`-Abschnitt einer
        /// Format-String-Interpolation (siehe ReadInterpolatedString), direkt
        /// NACH der bereits konsumierten öffnenden '{'. Sammelt den
        /// Ausdruckstext ROH ein (keine eigene Ausdrucks-Grammatik hier),
        /// muss dafür aber '(', '[', '{' (z.B. eine Lambda mit Block-Body
        /// INNERHALB der Interpolation) UND verschachtelte String-Literale
        /// (deren eigene '{'/'}'/':' NICHT mitzählen dürfen) korrekt
        /// überspringen, um die schließende '}' der Interpolation selbst
        /// nicht mit einer der verschachtelten zu verwechseln. Ein ':' NUR
        /// auf oberster Klammerungsebene trennt Ausdruck und Format-
        /// Spezifizierer (siehe SPEC: braucht die Ausdruck selbst einen
        /// Doppelpunkt, z.B. die Einheiten-Koersion, muss er geklammert
        /// werden - '{(x : km)}' statt '{x : km}').</summary>
        private InterpolationSegment ReadInterpolationExprSegment(int line, int col)
        {
            var expr = new StringBuilder();
            int depth = 0; // '(' + '[' + '{' zusammen - nur die Interpolationsklammer selbst zählt separat

            while (true)
            {
                if (IsAtEnd)
                    throw new LexException("Unbeendeter Ausdruck in einem Format-String", line, col);

                char c = Peek();

                if (c == '"')
                {
                    // Verschachteltes String-Literal ROH mitkopieren (inkl.
                    // seiner eigenen Escapes) - dessen '{'/'}'/':' dürfen die
                    // Klammerungstiefe/Format-Erkennung hier nicht berühren,
                    // die eigentliche Interpretation passiert erst bei der
                    // Neu-Lexung dieses Ausdruckstexts im Parser.
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
                    if (!IsAtEnd) { expr.Append(Peek()); Advance(); } // schließendes '"'
                    continue;
                }

                if (c == '(' || c == '[' || c == '{') { depth++; expr.Append(c); Advance(); continue; }
                if (c == ')' || c == ']') { depth--; expr.Append(c); Advance(); continue; }

                if (c == '}')
                {
                    if (depth > 0) { depth--; expr.Append(c); Advance(); continue; }
                    Advance(); // schließende '}' der Interpolation
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
                        throw new LexException("Unbeendeter Format-Spezifizierer in einem Format-String", line, col);
                    Advance(); // schließende '}'
                    return new InterpolationSegment(true, expr.ToString(), format.ToString());
                }

                if (c == '\n')
                    throw new LexException("Format-String über Zeilenende nicht erlaubt", line, col);

                expr.Append(c);
                Advance();
            }
        }

        private Token ReadChar(int line, int col, bool newlineBefore)
        {
            Advance(); // opening '
            if (IsAtEnd) throw new LexException("Unbeendetes Char-Literal", line, col);

            char value;
            char c = Advance();
            if (c == '\\')
            {
                if (IsAtEnd) throw new LexException("Unbeendetes Char-Literal", line, col);
                value = UnescapeChar(Advance());
            }
            else
            {
                value = c;
            }

            if (IsAtEnd || Peek() != '\'')
                throw new LexException("Char-Literal muss genau ein Zeichen enthalten", line, col);
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
                case '#': return Tok(TokenType.Hash, "#", line, col, newlineBefore);

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
                    throw new LexException($"Unerwartetes Zeichen '{c}'", line, col);
            }
        }

        private static Token Tok(TokenType type, string lexeme, int line, int col, bool newlineBefore) =>
            new(type, lexeme, line, col, newlineBefore: newlineBefore);

        // -----------------------------------------------------------
        // Low-level Zeichen-Handling mit Zeilen-/Spaltenzählung
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
