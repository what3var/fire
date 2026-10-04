namespace fire.Lexing
{
    public enum TokenType
    {
        // Literale
        IntLiteral,
        FloatLiteral,
        StringLiteral,
        InterpolatedStringLiteral, // $"..." - LiteralValue trägt eine List<InterpolationSegment> (siehe dort)
        CharLiteral,
        Identifier,

        // Schlüsselwörter – Deklaration/Kontrollfluss
        Var, Func, Class, Construct, Destruct, Return,
        If, Else, While, For, Foreach, In, On, New, Base, This,
        Try, Catch, Finally, Throw,
        Is, Of, From, Under,
        Extern, Unsafe,
        Interface,
        Readonly, Enum,
        Static, // 'static' bei Feldern/Methoden/Properties (siehe Parser.ParseClassMember)
        Public, Private, Protected, // Zugriffsmodifikatoren für Klassenmitglieder (siehe Parser.ParseAccessModifier)
        Namespace, // 'namespace Name { ... }' (siehe Parser.ParseNamespaceDecl/FlattenNamespaces)
        With,
        Extends,
        Switch, Case, Default, Break,
        Continue,
        Where,
        Fire, Taking,
        Operator, // 'operator' - Operator-Überladung (siehe Parser.ParseOperatorMember)
        Sync, Flat,
        Copy, // 'copy ausdruck' - tiefe Kopie (siehe Ast.UnaryOp.DeepCopy); 'flat ausdruck' ist die flache (UnaryOp.FlatCopy)
        Leave, Terminate,
        Actor, Process,

        // Schlüsselwörter – Literale/Typen
        True, False, Undefined,
        KwBool, KwInt, KwFloat, KwChar, KwString,
        KwByte, // 'byte' - reines Sugar für 'int[8]' (siehe Parser.ParseTypeRef), UND Element-Typ für 'new byte[n]' -> ByteBuffer statt ScriptArray

        // Logik
        And, Or,

        // Operatoren
        Plus, Minus, Star, Slash, Percent,
        PlusPlus, MinusMinus, // ++ / -- (siehe Parser.ParseUnary/ParsePostfix)
        Caret,       // '^'  -> Potenz (NICHT bitweises XOR - das ist Hash, siehe dort)
        Pipe,        // '|'  -> bitweises Oder. '||' bleibt eigenes Token (Or).
        Assign, Eq, NotEq, Lt, LtEq, Gt, GtEq,
        Bang,        // '!'  -> als Präfix: logische Negation. Als Suffix: Typ-Coercion.
        Tilde,       // '~'  -> Präfix: bitweise Inversion
        Amp,         // '&'  -> Präfix: Address-of (unsafe). '&&' bleibt eigenes Token (And).
        Colon,       // ':'  -> Einheiten-Coercion / Typ-Deklaration (kontextabhängig)
        Arrow,       // '=>'
        Dot, Comma, Semicolon,
        LParen, RParen, LBrace, RBrace, LBracket, RBracket,
        Hash,        // '#'  -> Präprozessor-Direktiven (Statement-Anfang, siehe Parser.ParseDirective) ODER bitweises XOR (Ausdrucks-Mitte, siehe Parser.ParseBitwiseXor) - rein positionsabhängig unterschieden, kein Konflikt: eine Direktive steht immer am STATEMENT-Anfang, XOR immer NACH einem bereits geparsten linken Operanden.

        Eof,
    }

    public readonly struct Token
    {
        public TokenType Type { get; }
        public string Lexeme { get; }
        public int Line { get; }
        public int Column { get; }

        // Länge des Tokens im QUELLTEXT (in Zeichen). Bei Strings/Chars weicht sie von Lexeme.Length ab (Lexeme ist der Inhalt ohne
        // Anführungszeichen und nach Escape-Verarbeitung; bei `$"..."` nur ein Platzhalter) - gebraucht für Editor-Hervorhebung.
        // Vom Lexer in Tokenize gesetzt; 0 bei von Hand erzeugten Tokens.
        public int Length { get; init; }

        // Nur für numerische Literale gesetzt: der direkt am Literal
        // anhängende Einheiten-Suffix ("mm", "km", ...), oder null/"" wenn keiner.
        public string? UnitSuffix { get; }

        // true, wenn zwischen dem vorherigen Token und diesem mindestens ein
        // Zeilenumbruch im Quelltext lag. Grundlage für die Statement-Trennungs-
        // regel (";" ODER Zeilenumbruch zwischen Statements) und dafür, dass
        // "optionale Weiterlese"-Entscheidungen (Postfix-Kette, Binär-Operatoren,
        // Coercion-Lookahead) nicht versehentlich in die nächste Zeile greifen.
        public bool NewlineBefore { get; }

        // Ausgewerteter Literalwert (long für Int, double für Float,
        // string für String/Char-Inhalt nach Escape-Verarbeitung).
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

    /// <summary>Ein Teilstück eines interpolierten Strings (`$"..."`), von
    /// Lexer.ReadInterpolatedString erzeugt und im `LiteralValue` eines
    /// InterpolatedStringLiteral-Tokens transportiert (als
    /// `List&lt;InterpolationSegment&gt;`). `IsExpression == false`: `Text` ist
    /// bereits fertig escape-verarbeiteter Literaltext. `IsExpression ==
    /// true`: `Text` ist der ROHE, noch NICHT geparste Quelltext zwischen
    /// `{` und `}`/`:` (der Parser lext/parst ihn eigenständig neu, siehe
    /// Parser.ParsePrimary), `Format` der optionale Format-Spezifizierer
    /// nach einem `:` auf oberster Klammerungsebene (z.B. "X", "F2") oder
    /// null, falls keiner angegeben wurde.</summary>
    public sealed record InterpolationSegment(bool IsExpression, string Text, string? Format);
}
