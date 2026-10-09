namespace fire.Lexing
{
    public enum TokenType
    {
        // Literale
        IntLiteral,
        FloatLiteral,
        StringLiteral,
        InterpolatedStringLiteral, // $"..." - LiteralValue carries a List<InterpolationSegment> (see there)
        CharLiteral,
        Identifier,

        // Keywords – declaration/control flow
        Var, Func, Class, Construct, Destruct, Return,
        If, Else, While, For, Foreach, In, On, New, Base, This,
        Try, Catch, Finally, Throw,
        Is, Of, From, Under,
        Extern, Unsafe,
        Interface,
        Readonly, Enum,
        Static, // 'static' on fields/methods/properties (see Parser.ParseClassMember)
        Public, Private, Protected, // Access modifiers for class members (see Parser.ParseAccessModifier)
        Namespace, // 'namespace Name { ... }' (siehe Parser.ParseNamespaceDecl/FlattenNamespaces)
        With,
        Extends,
        Switch, Case, Default, Break,
        Continue,
        Where,
        Fire, Taking,
        Operator, // 'operator' - operator overloading (see Parser.ParseOperatorMember)
        Sync, Flat,
        Copy, // 'copy expression' - deep copy (see Ast.UnaryOp.DeepCopy); 'flat expression' is the flat one (UnaryOp.FlatCopy)
        Take, // 'take expression' - ownership goes to the call or to the owner of the assignment target (see Ast.UnaryOp.Take, SPEC 2.2)
        Leave, Terminate,
        Actor, Process,

        // Keywords – literals/types
        True, False, Undefined,
        KwBool, KwInt, KwFloat, KwChar, KwString,
        KwByte, // 'byte' - pure sugar for 'int[8]' (see Parser.ParseTypeRef), AND element type for 'new byte[n]' -> ByteBuffer instead of ScriptArray

        // Logik
        And, Or,

        // Operatoren
        Plus, Minus, Star, Slash, Percent,
        PlusPlus, MinusMinus, // ++ / -- (siehe Parser.ParseUnary/ParsePostfix)
        Caret,       // '^'  -> power (NOT bitwise XOR - that is Hash, see there)
        Pipe,        // '|'  -> bitwise or. '||' remains a token of its own (Or).
        Assign, Eq, NotEq, Lt, LtEq, Gt, GtEq,
        Bang,        // '!'  -> as a prefix: logical negation. As a suffix: type coercion.
        Tilde,       // '~'  -> prefix: bitwise inversion
        Amp,         // '&'  -> prefix: address-of (unsafe). '&&' remains a token of its own (And).
        Colon,       // ':'  -> unit coercion / type declaration (context-dependent)
        Arrow,       // '=>'
        Dot, Comma, Semicolon,
        LParen, RParen, LBrace, RBrace, LBracket, RBracket,
        Hash,        // '#'  -> preprocessor directives (statement start, see Parser.ParseDirective) OR bitwise XOR (mid-expression, see Parser.ParseBitwiseXor) - distinguished purely by position, no conflict: a directive always sits at the STATEMENT start, XOR always AFTER an already parsed left operand.

        Eof,
    }

    public readonly struct Token
    {
        public TokenType Type { get; }
        public string Lexeme { get; }
        public int Line { get; }
        public int Column { get; }

        // Length of the token in the SOURCE TEXT (in characters). For strings/chars it differs from Lexeme.Length (Lexeme is the content without
        // quotation marks and after escape processing; for `$"..."` only a placeholder) - needed for editor highlighting.
        // Set by the lexer in Tokenize; 0 for hand-made tokens.
        public int Length { get; init; }

        // Set only for numeric literals: the unit suffix attached
        // directly to the literal ("mm", "km", ...), or null/"" if there is none.
        public string? UnitSuffix { get; }

        // true if there was at least one line break in the source text
        // between the previous token and this one. Basis for the statement separation
        // rule (";" OR line break between statements) and for ensuring that
        // "optional continue reading" decisions (postfix chain, binary operators,
        // coercion lookahead) do not accidentally reach into the next line.
        public bool NewlineBefore { get; }

        // Evaluated literal value (long for int, double for float,
        // string for string/char content after escape processing).
        public object? LiteralValue { get; }

        public Token(TokenType type, string lexeme, int line, int column,
            string? unitSuffix = null, object? literalValue = null, bool newlineBefore = false)
        {
            Type = type;
            Lexeme = lexeme;
            Line = line;
            Column = column;
            UnitSuffix = unitSuffix;
            LiteralValue = literalValue;
            NewlineBefore = newlineBefore;
        }

        public override string ToString() =>
            UnitSuffix is { Length: > 0 }
                ? $"{Type} '{Lexeme}' (unit: {UnitSuffix}) @ {Line}:{Column}"
                : $"{Type} '{Lexeme}' @ {Line}:{Column}";
    }

    /// <summary>A piece of an interpolated string (`$"..."`), produced by
    /// Lexer.ReadInterpolatedString and carried in the `LiteralValue` of an
    /// InterpolatedStringLiteral token (as
    /// `List&lt;InterpolationSegment&gt;`). `IsExpression == false`: `Text` is
    /// already finished, escape-processed literal text. `IsExpression ==
    /// true`: `Text` is the RAW, not yet parsed source text between
    /// `{` and `}`/`:` (the parser lexes/parses it again on its own, see
    /// Parser.ParsePrimary), `Format` the optional format specifier
    /// after a `:` at the top bracket level (e.g. "X", "F2") or
    /// null if none was given.</summary>
    public sealed record InterpolationSegment(bool IsExpression, string Text, string? Format);
}
