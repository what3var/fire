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
        /// innerhalb des dortigen Scopes. RequiredUnit: geforderte Einheit (SPEC
        /// "Einheiten-Deklarationen"), wenn die Deklaration ein explizites
        /// `: einheit` hatte - `null` sonst (jeder Wert zulässig, wie bisher).
        /// ByRef: ein `ref`-Parameter - der Slot haelt einen Zeiger auf die Variable des Aufrufers, Lesen und Schreiben gehen durch ihn hindurch.</summary>
        public sealed record Local(int Depth, int Slot, string? RequiredUnit = null, bool ByRef = false) : ResolvedRef;

        /// <summary>Globale Variable (Top-Level-Deklaration). Slot = Index im
        /// globalen Scope. RequiredUnit: wie bei Local.</summary>
        public sealed record Global(int Slot, string? RequiredUnit = null) : ResolvedRef;

        /// <summary>Wird für ein <see cref="fire.Ast.LambdaExpr"/> hinterlegt, das äußere lokale Variablen benutzt (Lambda-Captures,
        /// SPEC 4.2): `Variables` sind synthetische Bezeichner, aufgelöst im UMSCHLIESSENDEN Scope - der Compiler lädt ihre Werte beim
        /// Erzeugen der Lambda (Kopie); im Lambda-Scope liegen sie als Slots direkt hinter den Parametern.</summary>
        public sealed record LambdaCaptures(IReadOnlyList<fire.Ast.IdentifierExpr> Variables) : ResolvedRef;

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

        /// <summary>Ein statischer Klassenmitglied-Zugriff ('ClassName.Member',
        /// SPEC "Statische Mitglieder") - wie EnumMember an den betroffenen
        /// MemberExpr-Knoten gehängt, `me.Name` bleibt der Mitgliedsname,
        /// ClassName hier ist der (exakt geschriebene, ggf. schon
        /// vollqualifizierte) Klassenname, gegen den geprüft wurde (siehe
        /// Resolver.TryResolveStaticMemberAccess). Der Compiler nutzt das
        /// für GetStaticField/SetStaticField/CallStaticMethod statt der
        /// normalen (dynamischen) GetField/SetField/CallMethod.</summary>
        public sealed record StaticMember(string ClassName) : ResolvedRef;

        /// <summary>Ein Instanzfeld/-methode/-property, per bloßem Namen (ohne
        /// 'this.'-Präfix) referenziert, INNERHALB einer Klasse (SPEC
        /// "Implizite Mitglieder-Referenzen") - der Compiler behandelt das
        /// wie 'this.Name' (LoadThis + GetField/SetField/CallMethod). Kann
        /// nur innerhalb einer NICHT-statischen Methode/eines NICHT-
        /// statischen Feld-Initialisierers entstehen (siehe Resolver.
        /// ResolveIdentifierRef) - dort gibt es kein gebundenes 'this'.</summary>
        public sealed record ImplicitThisMember : ResolvedRef;
    }
}
