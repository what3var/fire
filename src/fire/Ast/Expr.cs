using System.Collections.Generic;
using fire.Lexing;
using fire.Values;

namespace fire.Ast
{
    public abstract record Expr(int Line);

    // ---------------------------------------------------------------
    // Primäre Ausdrücke
    // ---------------------------------------------------------------
    public sealed record LiteralExpr(int Line, Value Value) : Expr(Line);
    public sealed record IdentifierExpr(int Line, string Name) : Expr(Line);
    public sealed record ThisExpr(int Line) : Expr(Line);
    public sealed record BaseExpr(int Line) : Expr(Line);

    /// <summary>"Die Klasse, in deren Körper dieser Ausdruck steht" als
    /// Ziel eines statischen Zugriffs (`SelfClassExpr.feld`) - vom Parser NUR
    /// für das synthetisierte Backing-Field einer statischen Auto-Property
    /// in einer GENERISCHEN Klasse erzeugt. Ein normaler Bezeichner mit dem
    /// Klassennamen würde dort nicht genügen: heißt daneben eine
    /// nicht-generische Klasse gleich (SPEC "Generische Klassen"), trägt die
    /// generische intern einen anderen Namen (siehe GenericClassNames) - der
    /// Parser weiß beim Schreiben der Auto-Property aber noch nicht, ob es
    /// so eine Namensgleichheit gibt. Der Resolver löst dieses Ziel auf
    /// die aktuell aufgelöste Klasse auf, der Compiler sieht es nie (er
    /// arbeitet mit dem daraus entstandenen ResolvedRef.StaticMember).</summary>
    public sealed record SelfClassExpr(int Line) : Expr(Line);

    /// <summary>DefaultValue: optionaler Standardwert-Ausdruck (siehe SPEC
    /// "Optionale Parameter") - null bedeutet "erforderlich". Muss (falls
    /// vorhanden) IMMER an vom Ende her ZUSAMMENHÄNGENDEN Parametern stehen
    /// (kein Pflichtparameter nach einem optionalen), das prüft der
    /// Resolver. Wird pro Parameter zu einem eigenen 0-Arg-Proto kompiliert
    /// (siehe Compiler.CompileParamDefaults) und beim Aufruf mit zu wenigen
    /// Argumenten für die fehlenden TRAILING Parameter ausgewertet (siehe
    /// VM.FillDefaultArgs) - nicht statisch zur Compile-Zeit des Aufrufs,
    /// da die Sprache dynamisch typisiert ist und der Aufrufer nicht
    /// grundsätzlich wissen kann, wie viele Parameter das Ziel hat/welche
    /// davon optional sind.</summary>
    public sealed record LambdaParam(string Name, TypeRef? Type, IReadOnlyList<Expr?> ArrayRanks, Expr? DefaultValue = null);

    // AutoCapture: äußere LOKALE Variablen, die der Körper benutzt, werden beim Erzeugen als Wert kopiert (SPEC 4.2);
    // false für `fire global { }` (dort gilt allein `taking`).
    // Body ist entweder ein BlockStmt (mehrzeiliger Lambda-Body) oder ein
    // einzelnes ReturnStmt (Kurzform `=> ausdruck`, implizit als Return gewrappt).
    public sealed record LambdaExpr(
        int Line,
        IReadOnlyList<LambdaParam> Params,
        Expr? OnTarget,
        Stmt.BlockStmt Body,
        bool AutoCapture = true) : Expr(Line);

    /// <summary>`sync X` / `try sync X` / `sync flat X` / `try sync flat X`
    /// (docs/THREADING_DESIGN.md Abschnitt 4) - ein AUSDRUCK (kein
    /// Statement), liefert `true`/`false`/`undefined` (siehe
    /// Bytecode.VM.OpCode.Sync). `IsTry`: `false` = blockierend (nur `true`
    /// oder `undefined` möglich), `true` = nicht-blockierend (zusätzlich
    /// `false`, falls der Baum-Lock gerade belegt ist). `IsFlat`: `false` =
    /// voller rekursiver Sync, `true` = nur direkte Kinder (Fall A/B/C).
    /// `Target` ist typischerweise die `taking`-gebundene Variable, kann
    /// aber jeder Ausdruck sein, der zu einer Objektreferenz auswertet, die
    /// tatsächlich eine `taking`-Kopie ist (sonst Laufzeitfehler, siehe
    /// Runtime.SyncEngine).</summary>
    public sealed record SyncExpr(int Line, bool IsTry, bool IsFlat, Expr Target) : Expr(Line);

    /// <summary>`sync globals` (docs/THREADING_DESIGN.md Abschnitt 7) - im Hauptprogramm: arbeitet ab, was die Fire-Threads an
    /// Änderungen der Globals angemeldet haben; liefert die Anzahl der bearbeiteten Einträge (int).</summary>
    public sealed record SyncGlobalsExpr(int Line) : Expr(Line);

    /// <summary>`try process X` (docs/THREADING_DESIGN.md Abschnitt 2) -
    /// nicht-blockierende Variante von Ast.ProcessStmt: liefert `true`, wenn
    /// eine Nachricht verarbeitet wurde, sonst `false` (nie `undefined` -
    /// anders als `sync` gibt es hier kein "Ziel weg"-Szenario in dieser
    /// Ausbaustufe).</summary>
    public sealed record TryProcessExpr(int Line, Expr Target) : Expr(Line);

    /// <summary>`try Name(args)` - Aufruf einer als "tryable" registrierten
    /// nativen Funktion (siehe Bytecode.NativeRegistry.RegisterTryable,
    /// Resolving.ResolvedRef.TryableNative, SPEC 8.1.3). `Call` ist der
    /// geparste innere Aufruf (immer ein CallExpr mit IdentifierExpr-Callee -
    /// vom Resolver geprüft, hier nur syntaktisch erfasst). Liefert bei
    /// Erfolg den Ergebniswert, bei Fehlschlag/Timeout `undefined` - KEINE
    /// Exception.</summary>
    public sealed record TryCallExpr(int Line, Expr Call) : Expr(Line);

    /// <summary>TypeArgs: explizite Typ-Argumente bei `new Name&lt;Arg1, ...&gt;(...)`
    /// für eine generische Klasse (siehe ClassDecl.TypeParams) - leer für
    /// eine nicht-generische Instanziierung. Jedes Argument ist der reine
    /// Name (Klasse/Interface/primitiver Typ ODER, für eine `is in`-Prüfung,
    /// ein Einheitenname) - ausgewertet/geprüft erst zur Laufzeit (VM.
    /// CheckTypeArgConstraints), da die Sprache dynamisch typisiert ist.</summary>
    public sealed record NewExpr(int Line, TypeRef ClassRef, IReadOnlyList<Expr> Args, IReadOnlyList<string>? TypeArgs = null) : Expr(Line);

    /// <summary>`new Type[sizeExpr]` bzw. `new Type[sizeExpr][sizeExpr]...` -
    /// Array-Allokation, ggf. mehrdimensional ("jagged", SPEC 8.4: mehrere
    /// `[...]`-Gruppen = Array von Arrays). Getrennt von NewExpr
    /// (Klasseninstanzierung), da hier ein TypeRef + Größen statt ein
    /// Klassenname + Argumentliste gebraucht wird. SizeExprs[0] darf nicht
    /// null sein (der Resolver prüft das) - eine spätere Gruppe ohne Größe
    /// (`new int[3][]`) bricht die Auto-Allokation an der Stelle ab, die
    /// inneren Slots bleiben 'undefined'.</summary>
    public sealed record NewArrayExpr(int Line, TypeRef ElementType, IReadOnlyList<Expr?> SizeExprs) : Expr(Line);

    /// <summary>`new byte[sizeExpr]` - erzeugt einen Values.ByteBuffer (NICHT
    /// ein ScriptArray, siehe SPEC 8.10) mit der Host-Byte-Order als Default
    /// (ByteConversions.HostByteOrder) - eine ANDERE Order deklariert man
    /// nicht hier, sondern direkt danach über `.ToLittleEndian()`/
    /// `.ToBigEndian()` (liefert eine ggf. umsortierte Kopie mit dieser
    /// Order). Bewusst nur EINDIMENSIONAL (kein Analogon zu NewArrayExprs
    /// mehreren SizeExprs) - ein "verschachtelter Byte-Puffer" ergibt für
    /// rohe Binärdaten keinen sinnvollen Anwendungsfall.</summary>
    public sealed record NewBufferExpr(int Line, Expr SizeExpr) : Expr(Line);

    /// <summary>`[e1, e2, ...]` - Array-Literal. Ein Element, das selbst wieder
    /// ein Array-Literal ist, ergibt ganz natürlich ein verschachteltes
    /// ("jagged") Array (`[[1,2],[3,4]]`) - keine eigene Sonderbehandlung
    /// nötig, das läuft einfach über normale rekursive Auswertung der
    /// Elemente.</summary>
    public sealed record ArrayLiteralExpr(int Line, IReadOnlyList<Expr> Elements) : Expr(Line);

    // ---------------------------------------------------------------
    // Format-Strings: $"literal {ausdruck} literal {ausdruck:Format} ..."
    // ---------------------------------------------------------------
    public abstract record InterpolationPart;
    public sealed record InterpolationTextPart(string Text) : InterpolationPart;
    public sealed record InterpolationExprPart(Expr Expression, string? Format) : InterpolationPart;
    public sealed record InterpolatedStringExpr(int Line, IReadOnlyList<InterpolationPart> Parts) : Expr(Line);

    public sealed record ThrowExpr(int Line, Expr Value) : Expr(Line);

    // ---------------------------------------------------------------
    // Unär / Binär
    // ---------------------------------------------------------------
    // Dereference ('*ausdruck') und AddressOf ('&ausdruck') sind nur innerhalb
    // von 'unsafe { }' gültig (vom Resolver geprüft, siehe SPEC "Pointer/unsafe").
    //
    // FlatCopy (`flat x`) und DeepCopy (`copy x`) sind Kopier-Präfixe (SPEC 2.4): `flat` kopiert das Objekt selbst
    // samt seiner wertartigen Mitglieder, Referenzen bleiben wie im Original; `copy` ist eine Tiefenkopie (jede
    // erreichbare Instanz genau einmal kopiert). Der Compiler behandelt sie wie `new` bei der Owner-Wahl (SPEC 2.1).
    public enum UnaryOp { Negate, LogicalNot, BitNot, Dereference, AddressOf, FlatCopy, DeepCopy }
    public sealed record UnaryExpr(int Line, UnaryOp Op, Expr Operand) : Expr(Line);

    public enum BinaryOp { Add, Sub, Mul, Div, Mod, Eq, NotEq, Lt, LtEq, Gt, GtEq, And, Or, BitAnd, BitOr, BitXor, ShiftLeft, ShiftRight, Power }
    public sealed record BinaryExpr(int Line, BinaryOp Op, Expr Left, Expr Right) : Expr(Line);

    // ---------------------------------------------------------------
    // Coercion (Postfix): ':' -> Einheit, '!' -> Typ.
    // TargetUnitName/TargetTypeKeyword == null bedeutet "automatisch aus Kontext ableiten".
    // ---------------------------------------------------------------
    public sealed record UnitCoerceExpr(int Line, Expr Operand, string? TargetUnitName) : Expr(Line);
    public sealed record TypeCoerceExpr(int Line, Expr Operand, TokenType? TargetTypeKeyword) : Expr(Line);

    // ---------------------------------------------------------------
    // Prüf-Operatoren: is in / is of / is from / is under
    // ---------------------------------------------------------------
    public sealed record IsInExpr(int Line, Expr Operand, string UnitName) : Expr(Line);
    public sealed record IsOfExpr(int Line, Expr Operand, TypeRef TypeRef) : Expr(Line);
    public sealed record IsFromExpr(int Line, Expr Operand, Expr OwnerExpr, bool Transitive) : Expr(Line);

    // ---------------------------------------------------------------
    // Postfix: Aufruf / Member / Index / Zuweisung
    // ---------------------------------------------------------------
    public sealed record CallExpr(int Line, Expr Callee, IReadOnlyList<Expr> Args) : Expr(Line);
    public sealed record MemberExpr(int Line, Expr Target, string Name) : Expr(Line);
    public sealed record IndexExpr(int Line, Expr Target, Expr Index) : Expr(Line);
    public sealed record AssignExpr(int Line, Expr Target, Expr Value) : Expr(Line);

    /// <summary>`++x`/`--x` (IsPrefix=true, Ergebnis ist der NEUE Wert) bzw.
    /// `x++`/`x--` (IsPrefix=false, Ergebnis ist der ALTE Wert vor der
    /// Änderung) - SPEC "Inkrement/Dekrement". `Target` ist wie bei
    /// AssignExpr ein zuweisbarer Ausdruck (Variable/Feld/Index/
    /// Dereferenzierung, siehe Resolver.ResolveAssignTarget, hier
    /// wiederverwendet - `++`/`--` brauchen ja ohnehin sowohl Lese- als
    /// auch Schreibzugriff auf dasselbe Ziel).</summary>
    public sealed record IncDecExpr(int Line, Expr Target, bool IsIncrement, bool IsPrefix) : Expr(Line);
}
