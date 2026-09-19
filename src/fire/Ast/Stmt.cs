using System.Collections.Generic;

namespace ScriptLang.Ast
{
    public abstract record Stmt(int Line)
    {
        // Als eigener, benannter Typ referenziert (u.a. von LambdaExpr.Body),
        // daher als nested type statt nur als sealed record auf Namespace-Ebene.
        public sealed record BlockStmt(int Line, IReadOnlyList<Stmt> Statements) : Stmt(Line);
    }

    public sealed record ExprStmt(int Line, Expr Expression) : Stmt(Line);

    /// <summary>Eine Präprozessor-Direktive, die keine eigene Laufzeit-Wirkung
    /// hat (z.B. `#extern "libName"` - wirkt nur beim Parsen, siehe
    /// Parser._currentExternLib/ExternDecl.LibName). Resolver/Compiler
    /// überspringen sie einfach.</summary>
    public sealed record NoOpStmt(int Line) : Stmt(Line);

    /// <summary>`#noshadow` (siehe Parser.ParseDirective/Resolving.Resolver.
    /// ResolveFireStmt) - schaltet den Read-only-Globals-Snapshot in JEDEM
    /// `fire`-Block DES GANZEN PROGRAMMS ab (zurück auf das frühere
    /// Verhalten: ein `fire`-Block sieht dann WIEDER nur seine expliziten
    /// `taking`/`with`-Erfassungen, keine andere Hauptprogramm-Variable).
    /// Erzeugt keinen eigenen, laufzeitrelevanten AST-Knoten-Effekt außerhalb
    /// des Resolvers (der ihn in einem Vorab-Durchlauf einsammelt, wie
    /// Klassen/Enums/Externs) - wirkt global fürs ganze Programm,
    /// unabhängig davon, VOR oder NACH welchem `fire`-Block sie steht.
    /// MUSS dafür allerdings eine TOP-LEVEL-Anweisung sein (direkt im
    /// Programm, nicht verschachtelt innerhalb einer Klasse/Methode/eines
    /// Blocks) - der Vorab-Durchlauf sucht nur dort, exakt wie bei
    /// `#include`/`#extern` üblich (Direktiven stehen konventionell am
    /// Dateianfang).</summary>
    public sealed record NoShadowDirective(int Line) : Stmt(Line);

    /// <summary>Ein einzelnes Enum-Mitglied. ValueExpr fehlt -> Wert ist der
    /// des Vorgängers + 1 (0 beim ersten Mitglied) - klassisches C-artiges
    /// Auto-Increment. Wenn gesetzt, MUSS ValueExpr ein Int-Literal sein (vom
    /// Resolver geprüft) - beliebige Ausdrücke würden eine echte Compile-Zeit-
    /// Konstantenauswertung brauchen, die diese Sprache (noch) nicht hat.</summary>
    public sealed record EnumMember(string Name, Expr? ValueExpr);

    /// <summary>`enum Name { A, B = 5, C }` - reine Compile-Zeit-Konstanten,
    /// keine eigene Laufzeit-Repräsentation (kein eigener ValueKind, keine
    /// Instanzen) - `Name.Mitglied` wird vom Compiler direkt zum passenden
    /// Int-Literal aufgelöst (siehe Resolver.ResolveExpr/MemberExpr-Fall,
    /// ResolvedRef.EnumMember). Bewusst so simpel gehalten statt z.B. jedes
    /// Mitglied als eigene Objekt-Instanz einer generierten Klasse zu
    /// modellieren - das bräuchte ein Konzept für STATISCHE/geteilte Instanzen,
    /// das diese Sprache aktuell nicht hat.</summary>
    public sealed record EnumDecl(int Line, string Name, IReadOnlyList<EnumMember> Members) : Stmt(Line);

    /// <summary>IsReadonly: per `readonly` deklariert - der Resolver verbietet
    /// dann jede weitere Zuweisung an diese Variable nach der Deklaration
    /// (siehe Resolver.ResolveAssignTarget).</summary>
    public sealed record VarDeclStmt(
        int Line, string Name, TypeRef? Type, IReadOnlyList<Expr?> ArrayRanks, Expr? Initializer, bool IsReadonly = false) : Stmt(Line);

    public sealed record IfStmt(int Line, Expr Condition, Stmt Then, Stmt? Else) : Stmt(Line);
    public sealed record WhileStmt(int Line, Expr Condition, Stmt Body) : Stmt(Line);

    public sealed record ForStmt(
        int Line, Stmt? Init, Expr? Condition, Expr? Increment, Stmt Body) : Stmt(Line);

    public sealed record ForeachStmt(int Line, string VarName, Expr Iterable, Stmt Body) : Stmt(Line);

    public sealed record ReturnStmt(int Line, Expr? Value) : Stmt(Line);
    public sealed record ThrowStmt(int Line, Expr Value) : Stmt(Line);

    /// <summary>`break`/`continue` - nur innerhalb einer Schleife (`while`/
    /// `for`/`foreach`) gültig, geprüft vom Resolver (`_loopDepth`). Bricht
    /// NICHT über eine `try`/`catch`/`finally`-Grenze hinweg (siehe
    /// Resolver.ResolveStmt zu `_tryDepth`) - das wird als Fehler abgelehnt,
    /// keine automatische Handler-Abmeldung. Innerhalb eines `switch`-case
    /// ist `break` etwas GANZ ANDERES (reiner Parser-Zweigabschluss, kein
    /// Sprung, siehe Parser.ParseSwitchCaseBody) - dieser Knoten hier
    /// entsteht nur für ein `break` AUSSERHALB eines switch-case (z.B.
    /// direkt in einer Schleife, auch einer, die selbst in einem switch-case
    /// steckt).</summary>
    public sealed record BreakStmt(int Line) : Stmt(Line);
    public sealed record ContinueStmt(int Line) : Stmt(Line);

    /// <summary>Eine einzelne `taking`-Erfassung in einem `fire`-Statement
    /// (siehe Ast.FireStmt-Doku) - `VarName` ist der Name, unter dem der Wert
    /// im isolierten Fire-Block-Scope sichtbar ist, `Source` der Ausdruck,
    /// der ihn im AUFRUFENDEN Kontext liefert (typischerweise ein einfacher
    /// Bezeichner, kann aber - siehe Parser.ParseFireCallForm - auch ein
    /// synthetisches `this` oder ein Argumentausdruck der `fire MethodA(...)`-
    /// Aufrufform sein).</summary>
    public sealed record FireTakingCapture(string VarName, Expr Source);

    /// <summary>`fire { ... }` / `fire taking X { ... }` / `fire with actorA { ... }`
    /// / `fire MethodA(args) with actorA taking X` (docs/THREADING_DESIGN.md
    /// Abschnitt 1/2/3). `TakingCaptures`: 0 bis n `taking`-Erfassungen
    /// (Tiefenkopie bei Objekten, Werteweitergabe bei Primitiven - siehe
    /// Runtime.FireRuntime.FireVmTaking), in der Reihenfolge, in der sie im
    /// neuen globalen Scope des Fire-Threads landen (Resolver.
    /// ResolveFireStmt/Compiler.CompileFireStmt MÜSSEN dieselbe Reihenfolge
    /// verwenden). `WithVarName`/`WithSource`: höchstens eine Actor-Referenz,
    /// DIREKT weitergegeben (keine Kopie, siehe Ast.ProcessStmt-Doku),
    /// belegt IMMER den Slot NACH allen TakingCaptures.
    ///
    /// Die Aufrufform `fire MethodA(args)` (siehe Parser.ParseFireCallForm)
    /// erzeugt GAR KEINEN eigenen AST-Knoten - sie wird beim Parsen direkt zu
    /// diesem Knoten entzuckert: `this` und jedes Argument werden wie
    /// zusätzliche `taking`-Erfassungen unter internen Namen gebunden, und
    /// der Body besteht aus genau einem synthetischen Aufruf der genommenen
    /// Methode auf der genommenen `this`-Kopie. Der Body wird dadurch für
    /// Resolver/Compiler nicht von einem "echten" `fire { ... }`-Block
    /// unterscheidbar - kompletter Code-Reuse ohne jede Sonderbehandlung
    /// jenseits des Parsers.</summary>
    public sealed record FireStmt(
        int Line,
        IReadOnlyList<FireTakingCapture> TakingCaptures,
        string? WithVarName,
        Expr? WithSource,
        Stmt.BlockStmt Body) : Stmt(Line);

    /// <summary>`process X` (docs/THREADING_DESIGN.md Abschnitt 2) -
    /// blockierend: wartet, bis eine Nachricht in der Mailbox des
    /// Actor-Ziels `X` eintrifft, und führt dann GENAU EINE davon aus (siehe
    /// VM.ProcessOneMessage). Ein Statement (kein Ausdruck, kein sinnvoller
    /// Wert - blockiert ja per Definition, bis etwas da ist). Die
    /// nicht-blockierende Variante `try process X` ist dagegen ein AUSDRUCK
    /// (liefert true/false, siehe Ast.TryProcessExpr) - wie beim
    /// `sync`/`try sync`-Paar.</summary>
    public sealed record ProcessStmt(int Line, Expr Target) : Stmt(Line);

    /// <summary>`leave` (docs/THREADING_DESIGN.md Abschnitt 6.1) - verlässt
    /// den aktuellen Fire-Thread (oder, wenn außerhalb eines Fire-Threads
    /// verwendet, die aktuell laufende VM-Instanz allgemein - bewusst nicht
    /// eigens auf "nur innerhalb von fire" eingeschränkt, siehe BYTECODE.md).
    /// Kompiliert zu einem einzelnen Opcode (VM.RequestLeave), keine eigenen
    /// Kinder/Operanden.</summary>
    public sealed record LeaveStmt(int Line) : Stmt(Line);

    /// <summary>`terminate()` / `terminate(wert)` (docs/THREADING_DESIGN.md
    /// Abschnitt 6.3) - globaler, endgültiger Not-Aus für ALLE Threads.
    /// `Value` ist der optionale Exit-Wert (später über `VM.ExitValue`
    /// auslesbar) - `null` bedeutet "kein Argument", nicht "undefined
    /// explizit übergeben" (beides landet zwar zur Laufzeit gleich bei
    /// `undefined`, aber die Unterscheidung ist für den Compiler relevant,
    /// der sonst unnötig einen LoadConst-Undefined emittieren müsste).</summary>
    public sealed record TerminateStmt(int Line, Expr? Value) : Stmt(Line);

    /// <summary>`catch threads(ExceptionType e) { ... }` / `catch threads() { ... }`
    /// (docs/THREADING_DESIGN.md Abschnitt 6.2) - GLOBALE, programmweite
    /// Registrierung (kein normaler try/catch-Handler!), nur an
    /// Top-Level-Programmposition gültig (siehe Parser.ParseProgram). Anders
    /// als beim normalen `catch (varName : TypeName)` bewusst in
    /// Typ-dann-Name-Reihenfolge (`ExceptionType e`, wie ein Methoden-
    /// parameter) - das ist die vom Nutzer vorgegebene Syntax für dieses neue
    /// Konstrukt, keine Notwendigkeit, exakt der alten Konvention zu
    /// folgen. `TypeName`/`VarName` beide `null` bei `catch threads()`
    /// (fängt alles, ohne die Exception an eine Variable zu binden).</summary>
    public sealed record CatchThreadsDecl(int Line, string? TypeName, string? VarName, Stmt.BlockStmt Body) : Stmt(Line);

    /// <summary>`catch terminate(v) { ... }` (docs/THREADING_DESIGN.md
    /// Abschnitt 6.3) - GLOBALER Beobachtungs-Hook für `terminate`, läuft im
    /// Main-Thread, kann das Beenden NICHT verhindern (siehe VM.
    /// RunTerminateHandlerIfAny). Höchstens einmal im ganzen Programm
    /// sinnvoll (spätere Registrierungen überschreiben frühere, siehe
    /// Bytecode.GlobalHandlers). `VarName == null` bei `catch terminate()`
    /// (Body ohne gebundenen Wert).</summary>
    public sealed record CatchTerminateDecl(int Line, string? VarName, Stmt.BlockStmt Body) : Stmt(Line);

    public sealed record CatchClause(int Line, string? TypeName, string VarName, Stmt.BlockStmt Body);

    public sealed record TryStmt(
        int Line,
        Stmt.BlockStmt TryBlock,
        IReadOnlyList<CatchClause> Catches,
        Stmt.BlockStmt? Finally) : Stmt(Line);

    // ---------------------------------------------------------------
    // Klassen
    // ---------------------------------------------------------------
    /// <summary>IsReadonly: per `readonly` deklariert - nur innerhalb der
    /// eigenen Konstruktoren der deklarierenden Klasse per `this.feld = ...`
    /// zuweisbar (Resolver-Check, siehe ResolveAssignTarget); Zuweisungen an
    /// ein dynamisch anderes Ziel (`obj.feld = ...` von außen) werden dagegen
    /// NICHT statisch erfasst (dynamisches Typsystem) - bewusste Grenze
    /// dieser Ausbaustufe.</summary>
    public sealed record FieldDecl(
        int Line, TypeRef? Type, IReadOnlyList<Expr?> ArrayRanks, string Name, Expr? Initializer, bool IsReadonly = false) : Stmt(Line);

    // ---------------------------------------------------------------
    // Generics: Typ-Parameter mit Constraints ('where T is of X, is of Y')
    // ---------------------------------------------------------------

    /// <summary>Eine einzelne Bedingung: entweder 'is of Name' (Name ist eine
    /// Klasse/ein Interface) oder 'is in "unitName"' (dimensional
    /// kompatibel mit dieser Einheit, siehe SPEC 3.4/6).</summary>
    public enum TypeConstraintKind { IsOf, IsIn }
    public sealed record TypeConstraint(TypeConstraintKind Kind, string Name);

    /// <summary>Eine UND-Gruppe von Constraints, mit ':' verbunden - ALLE
    /// müssen erfüllt sein (z.B. 'is of float : is in "mm"' - der Typ muss
    /// BEIDES erfüllen).</summary>
    public sealed record TypeConstraintGroup(IReadOnlyList<TypeConstraint> Constraints);

    /// <summary>Ein Typ-Parameter mit seinen Constraint-Gruppen - mehrere
    /// Gruppen, mit ',' verbunden, sind ODER-verknüpft: mindestens EINE
    /// Gruppe muss vollständig erfüllt sein. Eine leere ConstraintGroups-
    /// Liste bedeutet "keine Einschränkung" (nur `<T>`, kein `where`).</summary>
    public sealed record TypeParam(string Name, IReadOnlyList<TypeConstraintGroup> ConstraintGroups);

    public sealed record MethodDecl(
        int Line,
        TypeRef? ReturnType,
        string Name,
        IReadOnlyList<LambdaParam> Params,
        Stmt.BlockStmt Body,
        IReadOnlyList<TypeParam>? TypeParams = null) : Stmt(Line);

    public sealed record ConstructorDecl(
        int Line,
        IReadOnlyList<LambdaParam> Params,
        IReadOnlyList<Expr>? BaseArgs,
        Stmt.BlockStmt Body) : Stmt(Line);

    public sealed record DestructorDecl(int Line, Stmt.BlockStmt Body) : Stmt(Line);

    /// <summary>TypeParams: die Typ-Parameter dieser Klasse samt Constraints
    /// (siehe TypeParam-Doku), leer für eine nicht-generische Klasse - siehe
    /// SPEC "Generische Klassen". Nur bei der KLASSE selbst ausgewertet
    /// (Resolver.ValidateTypeRef erlaubt Typ-Parameter-Namen als
    /// "bekannten Typ" innerhalb der Klasse, siehe dort); die eigentliche
    /// Constraint-PRÜFUNG passiert bei `new Name&lt;Arg1, ...&gt;(...)`
    /// (siehe NewExpr.TypeArgs, VM.CheckTypeArgConstraints).</summary>
    public sealed record ClassDecl(
        int Line,
        string Name,
        IReadOnlyList<string> BaseNames,
        IReadOnlyList<Stmt> Members,
        IReadOnlyList<TypeParam>? TypeParams = null,
        bool IsActor = false) : Stmt(Line);

    // ---------------------------------------------------------------
    // Interfaces (SPEC 8.5): reine Methodensignaturen, keine Felder/Bodies.
    // Da Methodenaufruf immer ein dynamischer Namens-Lookup ist (kein
    // statisches Typsystem), brauchen Interfaces KEINE eigene
    // Laufzeit-Repräsentation - sie sind ein reiner Resolver-Check ("erfüllt
    // diese Klasse alle Methoden des Interfaces").
    // ---------------------------------------------------------------
    public sealed record InterfaceMethodSig(
        int Line, TypeRef? ReturnType, string Name, IReadOnlyList<LambdaParam> Params);

    public sealed record InterfaceDecl(
        int Line, string Name, IReadOnlyList<InterfaceMethodSig> Methods) : Stmt(Line);

    // ---------------------------------------------------------------
    // Native Anbindung / unsafe (SPEC "APIs & Bitbreiten & Pointer")
    // ---------------------------------------------------------------

    /// <summary>Deklariert eine native Funktionssignatur ohne Body - macht den
    /// Namen als aufrufbar bekannt (wie eine registrierte native Funktion,
    /// siehe Bytecode.NativeRegistry), die tatsächliche Implementierung kommt
    /// später über ein Framework. ReturnType == null bedeutet "kein Rückgabewert"
    /// (die Funktion liefert praktisch `undefined`). LibName kommt von der
    /// nächsten vorangehenden `#extern "libName"`-Direktive im Quelltext (vom
    /// Parser gestempelt, siehe Parser._currentExternLib) - null, wenn keine
    /// solche Direktive vor dieser Deklaration stand, dann bleibt nur die
    /// manuelle Host-Registrierung (Bytecode.ExternRegistry) als Weg zur
    /// Implementierung.</summary>
    public sealed record ExternDecl(
        int Line, TypeRef? ReturnType, string Name, IReadOnlyList<LambdaParam> Params, string? LibName) : Stmt(Line);

    /// <summary>`unsafe { ... }` - nur innerhalb eines solchen Blocks sind
    /// Dereferenzierung ('*ausdruck') und Address-of ('&ausdruck') erlaubt.</summary>
    public sealed record UnsafeStmt(int Line, Stmt.BlockStmt Body) : Stmt(Line);

    /// <summary>`Type Name { get { ... } set { ... } }` - C#-artige Property.
    /// Mindestens eine der beiden (Getter/Setter) muss vorhanden sein (reine
    /// Lese- oder reine Schreib-Property). Intern reine Namenskonvention
    /// (wie GetIndex/SetIndex für '[]'): kompiliert zu zwei gewöhnlichen
    /// Methoden 'get_Name'/'set_Name' (Setter mit implizitem Parameter
    /// 'value') - `obj.Name`/`obj.Name = x` lösen die VM erst als normalen
    /// Feldzugriff auf, und NUR wenn kein Feld dieses Namens existiert, als
    /// Aufruf der passenden get_/set_-Methode (siehe VM.GetField/SetField).
    /// Properties haben deshalb absichtlich NIE einen eigenen
    /// ObjectInstance.Fields-Eintrag ihres eigenen Namens.</summary>
    public sealed record PropertyDecl(
        int Line, TypeRef? Type, string Name, Stmt.BlockStmt? Getter, Stmt.BlockStmt? Setter) : Stmt(Line);

    /// <summary>`class extends Name { neue Mitglieder... }` - fügt die
    /// Mitglieder direkt zur BESTEHENDEN Klasse `Name` hinzu (Ruby-artiges
    /// "Reopening", keine Vererbung: die neuen Mitglieder landen 1:1 in der
    /// ORIGINALEN ClassDecl, als hätten sie direkt dort gestanden). Wird
    /// bereits VOR dem eigentlichen Resolven/Kompilieren komplett aufgelöst
    /// (siehe Parser.MergeClassExtensions) - Resolver/Compiler sehen davon
    /// nichts mehr, nur die bereits zusammengeführte ClassDecl.</summary>
    public sealed record ClassExtensionDecl(int Line, string TargetName, IReadOnlyList<Stmt> Members) : Stmt(Line);
}
