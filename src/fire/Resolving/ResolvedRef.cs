namespace fire.Resolving
{
    /// <summary>
    /// Wohin eine Variablen-Referenz (IdentifierExpr oder Zuweisungsziel) statisch
    /// aufgelöst wurde. Grundlage für spätere Slot-basierte Zugriffe statt
    /// Namens-Lookup zur Laufzeit (und damit auch für den späteren Bytecode-Compiler).
    /// </summary>
    public abstract record ResolvedRef
    {
        /// <summary>Lokale Variable. Depth = Anzahl Scope-Hops von der aktuellen
        /// Ausführungsposition nach oben (0 = aktueller Scope selbst). Slot = Index
        /// innerhalb des dortigen Scopes.</summary>
        public sealed record Local(int Depth, int Slot) : ResolvedRef;

        /// <summary>Globale Variable (Top-Level-Deklaration). Slot = Index im
        /// globalen Scope.</summary>
        public sealed record Global(int Slot) : ResolvedRef;

        /// <summary>Eine registrierte native Funktion (SPEC-fremd, reine Bytecode-
        /// Erweiterungsstelle, siehe Bytecode.NativeRegistry) - nur als direkter
        /// Aufruf `name(...)` gültig, nicht als Wert verwendbar oder zuweisbar.</summary>
        public sealed record Native(string Name) : ResolvedRef;

        /// <summary>Eine im Quelltext per `extern` deklarierte native Funktions-
        /// signatur (SPEC "APIs & Bitbreiten & Pointer") - wie Native nur als
        /// direkter Aufruf gültig; die tatsächliche Implementierung kommt später
        /// über ein Framework.</summary>
        public sealed record Extern(string Name) : ResolvedRef;

        /// <summary>Eine als "tryable" registrierte native Funktion (siehe
        /// Bytecode.NativeRegistry.RegisterTryable/SPEC 8.1.3) - NUR über
        /// `try Name(...)` (Ast.TryCallExpr) aufrufbar, nie als direkter
        /// Aufruf `Name(...)` wie Native/Extern.</summary>
        public sealed record TryableNative(string Name) : ResolvedRef;

        /// <summary>Ein Zugriff auf ein `enum`-Mitglied (`EnumName.Mitglied`) -
        /// löst zur Compile-Zeit direkt zum passenden Int-Wert auf, keine
        /// Laufzeit-Auflösung nötig (siehe Ast.EnumDecl-Doku). Wird vom
        /// Resolver an den betroffenen MemberExpr-Knoten gehängt (nicht an
        /// einen IdentifierExpr wie die anderen ResolvedRef-Fälle).</summary>
        public sealed record EnumMember(long Value) : ResolvedRef;
    }
}
