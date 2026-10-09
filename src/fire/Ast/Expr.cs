using System.Collections.Generic;
using fire.Lexing;
using fire.Values;

namespace fire.Ast
{
    public abstract record Expr(int Line);

    // ---------------------------------------------------------------
    // Primary expressions
    // ---------------------------------------------------------------
    public sealed record LiteralExpr(int Line, Value Value) : Expr(Line);
    public sealed record IdentifierExpr(int Line, string Name) : Expr(Line);
    public sealed record ThisExpr(int Line) : Expr(Line);
    public sealed record BaseExpr(int Line) : Expr(Line);

    /// <summary>"The class in whose body this expression appears" as
    /// target of a static access (`SelfClassExpr.field`) - produced by the parser ONLY
    /// for the synthesised backing field of a static auto-property
    /// in a GENERIC class. A normal identifier with the
    /// class name would not suffice there: if a
    /// non-generic class of the same name exists next to it (SPEC "Generic classes"), the
    /// generic one internally carries a different name (see GenericClassNames) - but the
    /// parser does not yet know, when writing the auto-property, whether
    /// such a name clash exists. The resolver resolves this target to
    /// the class currently being resolved, the compiler never sees it (it
    /// works with the resulting ResolvedRef.StaticMember).</summary>
    public sealed record SelfClassExpr(int Line) : Expr(Line);

    /// <summary>DefaultValue: optional default-value expression (see SPEC
    /// "Optional parameters") - null means "required". If
    /// present it must ALWAYS be on parameters CONTIGUOUS from the end
    /// (no required parameter after an optional one), which the
    /// resolver checks. Compiled per parameter into its own 0-arg proto
    /// (see Compiler.CompileParamDefaults) and, when called with too few
    /// arguments, evaluated for the missing TRAILING parameters (see
    /// VM.FillDefaultArgs) - not statically at the compile time of the call,
    /// since the language is dynamically typed and the caller cannot
    /// in general know how many parameters the target has/which
    /// of them are optional.</summary>
    /// <summary>ByRef: the parameter is declared with a leading `ref` - the argument is passed by reference (SPEC 5.4.2): the function
    /// reads and writes the caller's variable (the field, the array element). Without `ref`, base types and strings are copied.</summary>
    public sealed record LambdaParam(string Name, TypeRef? Type, IReadOnlyList<Expr?> ArrayRanks, Expr? DefaultValue = null, bool ByRef = false);

    // AutoCapture: outer LOCAL variables used by the body are copied by value on creation (SPEC 4.2);
    // false for `fire global { }` (there only `taking` applies).
    // Body is either a BlockStmt (multi-line lambda body) or a
    // single ReturnStmt (short form `=> expression`, implicitly wrapped as a return).
    public sealed record LambdaExpr(
        int Line,
        IReadOnlyList<LambdaParam> Params,
        Expr? OnTarget,
        Stmt.BlockStmt Body,
        bool AutoCapture = true) : Expr(Line);

    /// <summary>`sync X` / `try sync X` / `sync flat X` / `try sync flat X`
    /// (docs/THREADING_DESIGN.md section 4) - an EXPRESSION (not a
    /// statement), yields `true`/`false`/`undefined` (see
    /// Bytecode.VM.OpCode.Sync). `IsTry`: `false` = blocking (only `true`
    /// or `undefined` possible), `true` = non-blocking (additionally
    /// `false` if the tree lock is currently held). `IsFlat`: `false` =
    /// full recursive sync, `true` = direct children only (case A/B/C).
    /// `Target` is typically the `taking`-bound variable, but can
    /// be any expression that evaluates to an object reference that
    /// actually is a `taking` copy (otherwise a runtime error, see
    /// Runtime.SyncEngine).</summary>
    public sealed record SyncExpr(int Line, bool IsTry, bool IsFlat, Expr Target) : Expr(Line);

    /// <summary>`sync globals` (docs/THREADING_DESIGN.md section 7) - in the main program: processes what the fire threads have
    /// registered as changes to the globals; returns the number of entries processed (int).</summary>
    public sealed record SyncGlobalsExpr(int Line) : Expr(Line);

    /// <summary>`probe obj.member changed|changing handler` (docs/DESIGN_LAMBDA_REFLECTION_PROBE.md): registers a handler for write accesses to the
    /// member `Member` of the object `Target` (`Member == null`: `probe obj.* ...`, all members). Evaluates to the probe handle (int).
    /// `Handler` is a lambda (from the block/`=>` short form or any lambda expression).</summary>
    public sealed record ProbeExpr(int Line, Expr Target, string? Member, bool IsChanging, Expr Handler) : Expr(Line);

    /// <summary>`try process X` (docs/THREADING_DESIGN.md section 2) -
    /// non-blocking variant of Ast.ProcessStmt: returns `true` if
    /// a message was processed, otherwise `false` (never `undefined` -
    /// unlike `sync` there is no "target gone" scenario at this
    /// stage).</summary>
    public sealed record TryProcessExpr(int Line, Expr Target) : Expr(Line);

    /// <summary>`try Name(args)` - call of a native
    /// function registered as "tryable" (see Bytecode.NativeRegistry.RegisterTryable,
    /// Resolving.ResolvedRef.TryableNative, SPEC 8.1.3). `Call` is the
    /// parsed inner call (always a CallExpr with an IdentifierExpr callee -
    /// checked by the resolver, only captured syntactically here). Yields the result value on
    /// success, `undefined` on failure/timeout - NO
    /// exception.</summary>
    public sealed record TryCallExpr(int Line, Expr Call) : Expr(Line);

    /// <summary>TypeArgs: explicit type arguments in `new Name&lt;Arg1, ...&gt;(...)`
    /// for a generic class (see ClassDecl.TypeParams) - empty for
    /// a non-generic instantiation. Each argument is the plain
    /// name (class/interface/primitive type OR, for an `is in` check,
    /// a unit name) - evaluated/checked only at runtime (VM.
    /// CheckTypeArgConstraints), since the language is dynamically typed.</summary>
    public sealed record NewExpr(int Line, TypeRef ClassRef, IReadOnlyList<Expr> Args, IReadOnlyList<string>? TypeArgs = null) : Expr(Line);

    /// <summary>`new Type[sizeExpr]` or `new Type[sizeExpr][sizeExpr]...` -
    /// array allocation, possibly multi-dimensional ("jagged", SPEC 8.4: several
    /// `[...]` groups = array of arrays). Separate from NewExpr
    /// (class instantiation), since a TypeRef + sizes is needed here instead of a
    /// class name + argument list. SizeExprs[0] must not be
    /// null (the resolver checks this) - a later group without a size
    /// (`new int[3][]`) stops the auto-allocation at that point, the
    /// inner slots stay 'undefined'.</summary>
    public sealed record NewArrayExpr(int Line, TypeRef ElementType, IReadOnlyList<Expr?> SizeExprs) : Expr(Line);

    /// <summary>`new byte[sizeExpr]` - creates a Values.ByteBuffer (NOT
    /// a ScriptArray, see SPEC 8.10) with the host byte order as default
    /// (ByteConversions.HostByteOrder) - a DIFFERENT order is not declared
    /// here, but directly afterwards via `.ToLittleEndian()`/
    /// `.ToBigEndian()` (returns a possibly reordered copy with that
    /// order). Deliberately ONE-dimensional only (no analogue to NewArrayExpr's
    /// several SizeExprs) - a "nested byte buffer" makes no
    /// sensible use case for raw binary data.</summary>
    public sealed record NewBufferExpr(int Line, Expr SizeExpr) : Expr(Line);

    /// <summary>`[e1, e2, ...]` - array literal. An element that is itself
    /// an array literal naturally yields a nested
    /// ("jagged") array (`[[1,2],[3,4]]`) - no special handling
    /// needed, it simply works via normal recursive evaluation of the
    /// elements.</summary>
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
    // Unary / Binary
    // ---------------------------------------------------------------
    // Dereference ('*expression') and AddressOf ('&expression') are valid only inside
    // 'unsafe { }' (checked by the resolver, see SPEC "Pointer/unsafe").
    //
    // FlatCopy (`flat x`) and DeepCopy (`copy x`) are copy prefixes (SPEC 2.4): `flat` copies the object itself
    // including its value-like members, references stay as in the original; `copy` is a deep copy (each
    // reachable instance copied exactly once). The compiler treats them like `new` when choosing the owner (SPEC 2.1).
    public enum UnaryOp { Negate, LogicalNot, BitNot, Dereference, AddressOf, FlatCopy, DeepCopy, Take }
    public sealed record UnaryExpr(int Line, UnaryOp Op, Expr Operand) : Expr(Line);

    public enum BinaryOp { Add, Sub, Mul, Div, Mod, Eq, NotEq, Lt, LtEq, Gt, GtEq, And, Or, BitAnd, BitOr, BitXor, ShiftLeft, ShiftRight, Power }
    public sealed record BinaryExpr(int Line, BinaryOp Op, Expr Left, Expr Right) : Expr(Line);

    // ---------------------------------------------------------------
    // Coercion (postfix): ':' -> unit, '!' -> type.
    // TargetUnitName/TargetTypeKeyword == null means "derive automatically from context".
    // ---------------------------------------------------------------
    public sealed record UnitCoerceExpr(int Line, Expr Operand, string? TargetUnitName) : Expr(Line);
    public sealed record TypeCoerceExpr(int Line, Expr Operand, TokenType? TargetTypeKeyword) : Expr(Line);

    // ---------------------------------------------------------------
    // Check operators: is in / is of / is from / is under
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

    /// <summary>`++x`/`--x` (IsPrefix=true, the result is the NEW value) or
    /// `x++`/`x--` (IsPrefix=false, the result is the OLD value before the
    /// change) - SPEC "Increment/Decrement". `Target`, as with
    /// AssignExpr, is an assignable expression (variable/field/index/
    /// dereference, see Resolver.ResolveAssignTarget, reused
    /// here - `++`/`--` need both read and
    /// write access to the same target anyway).</summary>
    public sealed record IncDecExpr(int Line, Expr Target, bool IsIncrement, bool IsPrefix) : Expr(Line);
}
