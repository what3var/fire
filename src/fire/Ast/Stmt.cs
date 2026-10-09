using System.Collections.Generic;

namespace fire.Ast
{
    public abstract record Stmt(int Source, int Line)
    {
        // Referenced as its own named type (among others by LambdaExpr.Body),
        // hence as a nested type instead of just a sealed record at namespace level.
        public sealed record BlockStmt(int Source, int Line, IReadOnlyList<Stmt> Statements) : Stmt(Source, Line);
    }

    public sealed record ExprStmt(int Source, int Line, Expr Expression) : Stmt(Source, Line);

    /// <summary>A preprocessor directive that has no runtime effect of its own
    /// (e.g. `#extern "libName"` - takes effect only when parsing, see
    /// Parser._currentExternLib/ExternDecl.LibName). Resolver/compiler
    /// simply skip it.</summary>
    public sealed record NoOpStmt(int Source, int Line) : Stmt(Source, Line);

    /// <summary>`#noshadow` (see Parser.ParseDirective/Resolving.Resolver.
    /// ResolveFireStmt) - switches off the read-only globals snapshot in EVERY
    /// `fire` block OF THE WHOLE PROGRAM (back to the earlier
    /// behaviour: a `fire` block then AGAIN sees only its explicit
    /// `taking`/`with` captures, no other main-program variable).
    /// Produces no AST-node effect of its own relevant at runtime outside
    /// the resolver (which collects it in a pre-pass, like
    /// classes/enums/externs) - applies globally to the whole program,
    /// regardless of BEFORE or AFTER which `fire` block it appears.
    /// However, it MUST be a TOP-LEVEL statement (directly in the
    /// program, not nested inside a class/method/
    /// block) - the pre-pass looks only there, exactly as is
    /// usual for `#include`/`#extern` (directives conventionally sit at
    /// the top of the file).</summary>
    public sealed record NoShadowDirective(int Source, int Line) : Stmt(Source, Line);

    /// <summary>`#nosync` (docs/THREADING_DESIGN.md section 7): the main program NO LONGER processes the queue of its fire threads (and incoming
    /// host callbacks) itself at safe points, but only at an explicit `sync globals`. Applies to the whole
    /// program (the compiler emits `SetAutoSync 0` at the very start for this); must be a top-level statement.</summary>
    public sealed record NoSyncDirective(int Source, int Line) : Stmt(Source, Line);

    /// <summary>`#timeout value` (e.g. `#timeout 10s`, `#timeout 500ms`, `#timeout 2000`): the wait time that wait functions without their own duration
    /// use (`Device.WaitFor`/`WaitForString`; 30 seconds without the directive). `value`: a time value (`10s`), a `TimeSpan` is not possible here (the
    /// directive is evaluated at the very start of the program) or a number in milliseconds - as with `Sleep`. The compiler emits for this at the very start
    /// `SetTimeout`; must be a top-level statement.</summary>
    public sealed record TimeoutDirective(int Source, int Line, Expr Value) : Stmt(Source, Line);

    /// <summary>A single enum member. If ValueExpr is missing -> the value is that
    /// of the predecessor + 1 (0 for the first member) - classic C-like
    /// auto-increment. If set, ValueExpr MUST be an int literal (checked by the
    /// resolver) - arbitrary expressions would need real compile-time
    /// constant evaluation, which this language does not (yet) have.</summary>
    public sealed record EnumMember(string Name, Expr? ValueExpr);

    /// <summary>`enum Name { A, B = 5, C }` - pure compile-time constants,
    /// no runtime representation of their own (no ValueKind of their own, no
    /// instances) - `Name.Member` is resolved by the compiler directly to the matching
    /// int literal (see Resolver.ResolveExpr/MemberExpr case,
    /// ResolvedRef.EnumMember). Deliberately kept this simple instead of e.g. modelling every
    /// member as an object instance of a generated class of its own -
    /// that would need a concept for STATIC/shared instances,
    /// which this language does not currently have.</summary>
    public sealed record EnumDecl(int Source, int Line, string Name, IReadOnlyList<EnumMember> Members) : Stmt(Source, Line);

    /// <summary>IsReadonly: declared with `readonly` - the resolver then forbids
    /// any further assignment to this variable after the declaration
    /// (see Resolver.ResolveAssignTarget).</summary>
    public sealed record VarDeclStmt(
        int Source, int Line, string Name, TypeRef? Type, IReadOnlyList<Expr?> ArrayRanks, Expr? Initializer, bool IsReadonly = false) : Stmt(Source, Line);

    public sealed record IfStmt(int Source, int Line, Expr Condition, Stmt Then, Stmt? Else) : Stmt(Source, Line);
    public sealed record WhileStmt(int Source, int Line, Expr Condition, Stmt Body) : Stmt(Source, Line);

    public sealed record ForStmt(
        int Source, int Line, Stmt? Init, Expr? Condition, Expr? Increment, Stmt Body) : Stmt(Source, Line);

    public sealed record ForeachStmt(int Source, int Line, string VarName, Expr Iterable, Stmt Body) : Stmt(Source, Line);

    public sealed record ReturnStmt(int Source, int Line, Expr? Value) : Stmt(Source, Line);
    public sealed record ThrowStmt(int Source, int Line, Expr Value) : Stmt(Source, Line);

    /// <summary>`delete expression` (SPEC 2.5): destroys the object, array or buffer immediately - destructor and cascade run as when the owner is left.</summary>
    public sealed record DeleteStmt(int Source, int Line, Expr Target) : Stmt(Source, Line);

    /// <summary>`break`/`continue` - valid only inside a loop (`while`/
    /// `for`/`foreach`), checked by the resolver (`_loopDepth`). Does
    /// NOT break across a `try`/`catch`/`finally` boundary (see
    /// Resolver.ResolveStmt on `_tryDepth`) - that is rejected as an error,
    /// no automatic handler deregistration. Inside a `switch` case
    /// `break` is something COMPLETELY DIFFERENT (a pure parser branch terminator, no
    /// jump, see Parser.ParseSwitchCaseBody) - this node here
    /// arises only for a `break` OUTSIDE a switch case (e.g.
    /// directly in a loop, even one that is itself inside a switch
    /// case).</summary>
    public sealed record BreakStmt(int Source, int Line) : Stmt(Source, Line);
    public sealed record ContinueStmt(int Source, int Line) : Stmt(Source, Line);

    /// <summary>A single `taking` capture in a `fire` statement
    /// (see Ast.FireStmt docs) - `VarName` is the name under which the value is
    /// visible in the isolated fire-block scope, `Source` the expression
    /// that supplies it in the CALLING context (typically a simple
    /// identifier, but - see Parser.ParseFireCallForm - it can also be a
    /// synthetic `this` or an argument expression of the `fire MethodA(...)`
    /// call form).</summary>
    public sealed record FireTakingCapture(string VarName, Expr Source);

    /// <summary>`fire { ... }` / `fire taking X { ... }` / `fire with actorA { ... }`
    /// / `fire MethodA(args) with actorA taking X` (docs/THREADING_DESIGN.md
    /// section 1/2/3). `TakingCaptures`: 0 to n `taking` captures
    /// (deep copy for objects, value passing for primitives - see
    /// Runtime.FireRuntime.FireVmTaking), in the order in which they end up in the
    /// new global scope of the fire thread (Resolver.
    /// ResolveFireStmt/Compiler.CompileFireStmt MUST use the same
    /// order). `WithVarName`/`WithSource`: at most one actor reference,
    /// passed DIRECTLY (no copy, see Ast.ProcessStmt docs),
    /// ALWAYS occupies the slot AFTER all TakingCaptures.
    ///
    /// The call form `fire MethodA(args)` (see Parser.ParseFireCallForm)
    /// produces NO AST node of its own - it is desugared directly into
    /// this node while parsing: `this` and each argument are bound like
    /// additional `taking` captures under internal names, and
    /// the body consists of exactly one synthetic call of the taken
    /// method on the taken `this` copy. The body thereby becomes, for
    /// the resolver/compiler, indistinguishable from a "real" `fire { ... }` block -
    /// complete code reuse without any special handling
    /// beyond the parser.</summary>
    public sealed record FireStmt(
        int Source,
        int Line,
        IReadOnlyList<FireTakingCapture> TakingCaptures,
        string? WithVarName,
        Expr? WithSource,
        Stmt.BlockStmt Body) : Stmt(Source, Line);

    /// <summary>Start of a `sync global { ... }` section (docs/THREADING_DESIGN.md section 7) - the parser desugars the block into
    /// `SectionEnterStmt; try { Body } finally { SectionExitStmt }`, so that the section also ends on `throw` in the block.</summary>
    public sealed record SectionEnterStmt(int Source, int Line) : Stmt(Source, Line);

    /// <summary>End of a `sync global { ... }` section (see <see cref="SectionEnterStmt"/>).</summary>
    public sealed record SectionExitStmt(int Source, int Line) : Stmt(Source, Line);

    /// <summary>`silence obj.member` / `silence obj.*` / `silence x`: removes probes (see Ast.ProbeExpr). `MemberForm`: `Target` is the object and
    /// `Member` the member (null = all probes of the object); otherwise `Target` is an expression that evaluates to a probe handle or an object.</summary>
    public sealed record SilenceStmt(int Source, int Line, Expr Target, string? Member, bool MemberForm) : Stmt(Source, Line);

    /// <summary>`fire global { ... } [taking X ...]` (docs/THREADING_DESIGN.md section 7): a job for the main program that runs at
    /// its next `sync globals` with the real globals, without the caller waiting. The parser turns the block into a
    /// lambda whose parameters are the `taking` captures (they are passed as value/copy when enqueued) - like any lambda, the lambda sees
    /// the globals, but no locals of the caller.</summary>
    public sealed record PostGlobalStmt(int Source, int Line, LambdaExpr Lambda, IReadOnlyList<Expr> Args) : Stmt(Source, Line);

    /// <summary>`process X` (docs/THREADING_DESIGN.md Abschnitt 2) -
    /// blocking: waits until a message arrives in the mailbox of the
    /// actor target `X`, and then executes EXACTLY ONE of them (see
    /// VM.ProcessOneMessage). A statement (not an expression, no meaningful
    /// value - it blocks by definition until something is there). The
    /// non-blocking variant `try process X`, by contrast, is an EXPRESSION
    /// (yields true/false, see Ast.TryProcessExpr) - as with the
    /// `sync`/`try sync` pair.</summary>
    public sealed record ProcessStmt(int Source, int Line, Expr Target) : Stmt(Source, Line);

    /// <summary>`leave` (docs/THREADING_DESIGN.md section 6.1) - leaves
    /// the current fire thread (or, when used outside a fire thread,
    /// the currently running VM instance in general - deliberately not
    /// restricted specifically to "only inside fire", see BYTECODE.md).
    /// Compiles to a single opcode (VM.RequestLeave), no
    /// children/operands of its own.</summary>
    public sealed record LeaveStmt(int Source, int Line) : Stmt(Source, Line);

    /// <summary>`terminate()` / `terminate(value)` (docs/THREADING_DESIGN.md
    /// section 6.3) - global, final emergency stop for ALL threads.
    /// `Value` is the optional exit value (can later be read via `VM.ExitValue`);
    /// `null` means "no argument", not "undefined
    /// passed explicitly" (both do end up the same at runtime at
    /// `undefined`, but the distinction is relevant to the compiler,
    /// which would otherwise needlessly have to emit a LoadConst-Undefined).</summary>
    public sealed record TerminateStmt(int Source, int Line, Expr? Value) : Stmt(Source, Line);

    /// <summary>`catch threads(ExceptionType e) { ... }` / `catch threads() { ... }`
    /// (docs/THREADING_DESIGN.md section 6.2) - GLOBAL, program-wide
    /// registration (not a normal try/catch handler!), valid only at
    /// top-level program position (see Parser.ParseProgram).
    /// Type-then-name order (`ExceptionType e`, like a method
    /// parameter) - by now the same order as for the normal
    /// `catch (TypeName varName)` (see Parser.ParseCatchClause).
    /// `TypeName`/`VarName` both `null` for `catch threads()`
    /// (catches everything, without binding the exception to a variable).</summary>
    public sealed record CatchThreadsDecl(int Source, int Line, TypeRef? TypeRef, string? VarName, Stmt.BlockStmt Body) : Stmt(Source, Line);

    /// <summary>`catch terminate(v) { ... }` (docs/THREADING_DESIGN.md
    /// section 6.3) - GLOBAL observation hook for `terminate`, runs in the
    /// main thread, can NOT prevent termination (see VM.
    /// RunTerminateHandlerIfAny). Makes sense at most once in the whole program
    /// (later registrations override earlier ones, see
    /// Bytecode.GlobalHandlers). `VarName == null` for `catch terminate()`
    /// (body without a bound value).</summary>
    public sealed record CatchTerminateDecl(int Source, int Line, string? VarName, Stmt.BlockStmt Body) : Stmt(Source, Line);

    public sealed record CatchClause(int Source, int Line, TypeRef? TypeRef, string VarName, Stmt.BlockStmt Body);

    public sealed record TryStmt(
        int Source,
        int Line,
        Stmt.BlockStmt TryBlock,
        IReadOnlyList<CatchClause> Catches,
        Stmt.BlockStmt? Finally,
        bool IsSyncSection = false) : Stmt(Source, Line); // IsSyncSection: created by the parser from `sync global { }` (only for the error message)

    // ---------------------------------------------------------------
    // Klassen
    // ---------------------------------------------------------------

    /// <summary>Access modifier of a class member (field/method/
    /// property/constructor) - SPEC "Access modifiers". `Public`
    /// (default if no modifier was given - existing scripts/
    /// the prelude itself thereby stay valid unchanged): accessible from
    /// anywhere. `Private`: only inside the DECLARING class itself
    /// (not even from a derived class). `Protected`: from
    /// the declaring class AND every class derived from it
    /// (recursively along the entire inheritance chain) - see Resolver.
    /// CheckMemberAccess for the exact check.</summary>
    public enum AccessModifier { Public, Private, Protected }

    /// <summary>IsReadonly: declared with `readonly` - assignable only inside the
    /// declaring class's own constructors via `this.field = ...`
    /// (resolver check, see ResolveAssignTarget); assignments to
    /// a dynamically different target (`obj.field = ...` from outside), on the other hand, are
    /// NOT captured statically (dynamic type system) - a deliberate limit
    /// of this stage.</summary>
    public sealed record FieldDecl(
        int Source, int Line, TypeRef? Type, IReadOnlyList<Expr?> ArrayRanks, string Name, Expr? Initializer, bool IsReadonly = false,
        AccessModifier Access = AccessModifier.Public, bool IsStatic = false) : Stmt(Source, Line);

    // ---------------------------------------------------------------
    // Generics: Typ-Parameter mit Constraints ('where T is of X, is of Y')
    // ---------------------------------------------------------------

    /// <summary>A single condition: either 'is of Name' (Name is a
    /// class/an interface) or 'is in "unitName"' (dimensionally
    /// compatible with this unit, see SPEC 3.4/6).</summary>
    public enum TypeConstraintKind { IsOf, IsIn }
    public sealed record TypeConstraint(TypeConstraintKind Kind, string Name);

    /// <summary>An AND group of constraints, joined with ':' - ALL
    /// must be satisfied (e.g. 'is of float : is in "mm"' - the type must
    /// satisfy BOTH).</summary>
    public sealed record TypeConstraintGroup(IReadOnlyList<TypeConstraint> Constraints);

    /// <summary>A type parameter with its constraint groups - several
    /// groups, joined with ',', are OR-combined: at least ONE
    /// group must be fully satisfied. An empty ConstraintGroups
    /// list means "no restriction" (only `<T>`, no `where`).</summary>
    public sealed record TypeParam(string Name, IReadOnlyList<TypeConstraintGroup> ConstraintGroups);

    public sealed record MethodDecl(
        int Source,
        int Line,
        TypeRef? ReturnType,
        string Name,
        IReadOnlyList<LambdaParam> Params,
        Stmt.BlockStmt Body,
        IReadOnlyList<TypeParam>? TypeParams = null,
        AccessModifier Access = AccessModifier.Public,
        bool IsStatic = false) : Stmt(Source, Line);

    public sealed record ConstructorDecl(
        int Source,
        int Line,
        IReadOnlyList<LambdaParam> Params,
        IReadOnlyList<Expr>? BaseArgs,
        Stmt.BlockStmt Body,
        AccessModifier Access = AccessModifier.Public) : Stmt(Source, Line);

    public sealed record DestructorDecl(int Source, int Line, Stmt.BlockStmt Body) : Stmt(Source, Line);

    /// <summary>TypeParams: the type parameters of this class including constraints
    /// (see TypeParam docs), empty for a non-generic class - see
    /// SPEC "Generic classes". Evaluated only on the CLASS itself
    /// (Resolver.ValidateTypeRef allows type-parameter names as a
    /// "known type" inside the class, see there); the actual
    /// constraint CHECK happens at `new Name&lt;Arg1, ...&gt;(...)`
    /// (see NewExpr.TypeArgs, VM.CheckTypeArgConstraints).</summary>
    public sealed record ClassDecl(
        int Source,
        int Line,
        string Name,
        IReadOnlyList<TypeRef>? BaseRefs,
        IReadOnlyList<Stmt> Members,
        IReadOnlyList<TypeParam>? TypeParams = null,
        bool IsActor = false) : Stmt(Source, Line);

    // ---------------------------------------------------------------
    // Interfaces (SPEC 8.5): pure method signatures, no fields/bodies.
    // Since a method call is always a dynamic name lookup (no
    // static type system), interfaces need NO runtime
    // representation of their own - they are a pure resolver check ("does
    // this class satisfy all methods of the interface").
    // ---------------------------------------------------------------
    public sealed record InterfaceMethodSig(
        int Line, TypeRef? ReturnType, string Name, IReadOnlyList<LambdaParam> Params);

    public sealed record InterfaceDecl(
        int Source, int Line, string Name, IReadOnlyList<InterfaceMethodSig> Methods,
        IReadOnlyList<TypeParam>? TypeParams = null) : Stmt(Source, Line);

    // ---------------------------------------------------------------
    // Native Anbindung / unsafe (SPEC "APIs & Bitbreiten & Pointer")
    // ---------------------------------------------------------------

    /// <summary>Declares a native function signature without a body - makes the
    /// name known as callable (like a registered native function,
    /// see Bytecode.NativeRegistry), the actual implementation comes
    /// later via a framework. ReturnType == null means "no return value"
    /// (the function practically returns `undefined`). LibName comes from the
    /// nearest preceding `#extern "libName"` directive in the source (stamped
    /// by the parser, see Parser._currentExternLib) - null if no
    /// such directive preceded this declaration, in which case only
    /// manual host registration (Bytecode.ExternRegistry) remains as the way to
    /// the implementation.</summary>
    public sealed record ExternDecl(
        int Source, int Line, TypeRef? ReturnType, string Name, IReadOnlyList<LambdaParam> Params, string? LibName) : Stmt(Source, Line);

    /// <summary>`unsafe { ... }` - only inside such a block are
    /// dereference ('*expression') and address-of ('&expression') allowed.</summary>
    public sealed record UnsafeStmt(int Source, int Line, Stmt.BlockStmt Body) : Stmt(Source, Line);

    /// <summary>`Type Name { get { ... } set { ... } }` - C#-like property.
    /// At least one of the two (getter/setter) must be present (pure
    /// read or pure write property). Internally a pure naming convention
    /// (like GetIndex/SetIndex for '[]'): compiles to two ordinary
    /// methods 'get_Name'/'set_Name' (setter with the implicit parameter
    /// 'value') - `obj.Name`/`obj.Name = x` are resolved by the VM first as a normal
    /// field access, and ONLY if no field of this name exists, as a
    /// call of the matching get_/set_ method (see VM.GetField/SetField).
    /// Properties therefore deliberately NEVER have their own
    /// ObjectInstance.Fields entry of their own name.</summary>
    public sealed record PropertyDecl(
        int Source, int Line, TypeRef? Type, string Name, Stmt.BlockStmt? Getter, Stmt.BlockStmt? Setter,
        AccessModifier Access = AccessModifier.Public, bool IsStatic = false) : Stmt(Source, Line);

    /// <summary>`class extends Name { new members... }` - adds the
    /// members directly to the EXISTING class `Name` (Ruby-like
    /// "reopening", no inheritance: the new members land 1:1 in the
    /// ORIGINAL ClassDecl, as if they had been written there directly). Is
    /// resolved completely BEFORE the actual resolving/compiling
    /// (see Parser.MergeClassExtensions) - resolver/compiler see nothing
    /// of it any more, only the already merged ClassDecl. `TargetRef`
    /// carries (like every other TypeRef) the namespace current at
    /// the parsing of this extension + the active `#using` names (see
    /// TypeRef.Namespaces) - as a result MergeClassExtensions finds the
    /// right target class even if `class extends X` writes the name ONLY
    /// unqualified and `X` has to be resolved only via the namespace context OF THE
    /// EXTENSION itself (not that of the target class!).</summary>
    public sealed record ClassExtensionDecl(int Source, int Line, TypeRef TargetRef, IReadOnlyList<Stmt> Members) : Stmt(Source, Line);

    /// <summary>`namespace Name { members... }` or `namespace A.B { ... }`
    /// (SPEC "Namespaces"). Unlike before, NO LONGER resolved by a
    /// separate tree pass after parsing: the parser
    /// qualifies every contained class/interface/enum declaration
    /// already DURING parsing (see Parser._currentNamespace/
    /// ParseNamespaceDecl), and every reference (base class, `new`, `is of`,
    /// `catch`, field/parameter/return types) carries its own
    /// namespace context directly (see TypeRef.Namespaces). This
    /// node itself is thereby merely a pure grouping without meaning of its own
    /// for resolver/compiler - it is trivially "flattened" right after parsing
    /// (members move 1:1 into the place of the
    /// NamespaceDecl node, see Parser.FlattenNamespaceWrappers), WITHOUT
    /// renaming anything in the process. `Name` is purely informational
    /// (e.g. for editor display), and plays no role in the actual
    /// resolution any more.</summary>
    public sealed record NamespaceDecl(int Source, int Line, string Name, IReadOnlyList<Stmt> Members) : Stmt(Source, Line);
}
