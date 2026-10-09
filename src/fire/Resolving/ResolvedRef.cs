namespace fire.Resolving
{
    /// <summary>
    /// Where a variable reference (IdentifierExpr or assignment target) was statically
    /// resolved to. Basis for later slot-based accesses instead of
    /// name lookup at runtime (and thus also for the later bytecode compiler).
    /// </summary>
    public abstract record ResolvedRef
    {
        /// <summary>Local variable. Depth = number of scope hops upward from the current
        /// execution position (0 = the current scope itself). Slot = index
        /// within the scope there. RequiredUnit: required unit (SPEC
        /// "Unit declarations") if the declaration had an explicit
        /// `: unit` - `null` otherwise (any value permitted, as before).
        /// ByRef: a `ref` parameter - the slot holds a pointer to the caller's variable, reading and writing go through it.</summary>
        public sealed record Local(int Depth, int Slot, string? RequiredUnit = null, bool ByRef = false) : ResolvedRef;

        /// <summary>Global variable (top-level declaration). Slot = index in the
        /// global scope. RequiredUnit: as with Local.</summary>
        public sealed record Global(int Slot, string? RequiredUnit = null) : ResolvedRef;

        /// <summary>Registered for a <see cref="fire.Ast.LambdaExpr"/> that uses outer local variables (lambda captures,
        /// SPEC 4.2): `Variables` are synthetic identifiers, resolved in the ENCLOSING scope - the compiler loads their values when
        /// creating the lambda (copy); in the lambda scope they sit as slots directly after the parameters.</summary>
        public sealed record LambdaCaptures(IReadOnlyList<fire.Ast.IdentifierExpr> Variables) : ResolvedRef;

        /// <summary>A registered native function (outside the SPEC, a pure bytecode
        /// extension point, see Bytecode.NativeRegistry) - valid only as a direct
        /// call `name(...)`, not usable or assignable as a value.</summary>
        public sealed record Native(string Name) : ResolvedRef;

        /// <summary>A native function signature declared in the source via `extern`
        /// (SPEC "APIs & bit widths & pointers") - like Native valid only as a
        /// direct call; the actual implementation comes later
        /// via a framework.</summary>
        public sealed record Extern(string Name) : ResolvedRef;

        /// <summary>A native function registered as "tryable" (see
        /// Bytecode.NativeRegistry.RegisterTryable/SPEC 8.1.3) - callable ONLY via
        /// `try Name(...)` (Ast.TryCallExpr), never as a direct
        /// call `Name(...)` like Native/Extern.</summary>
        public sealed record TryableNative(string Name) : ResolvedRef;

        /// <summary>`try obj.Take...(...)` (Ast.TryCallExpr with a method call, SPEC 2.2): the ownership method (`TakeLocal`, `TakeUpwards`, `TakeGlobal`, `TakeTo`) moves
        /// only if the caller is the owner, and returns whether it did so.</summary>
        public sealed record TryTake(string Name) : ResolvedRef;

        /// <summary>An access to an `enum` member (`EnumName.Member`) -
        /// resolves at compile time directly to the matching int value, no
        /// runtime resolution needed (see Ast.EnumDecl docs). Attached by the
        /// resolver to the affected MemberExpr node (not to
        /// an IdentifierExpr like the other ResolvedRef cases).</summary>
        public sealed record EnumMember(long Value) : ResolvedRef;

        /// <summary>A static class-member access ('ClassName.Member',
        /// SPEC "Static members") - like EnumMember attached to the affected
        /// MemberExpr node, `me.Name` remains the member name,
        /// ClassName here is the (exactly as written, possibly already
        /// fully qualified) class name against which it was checked (see
        /// Resolver.TryResolveStaticMemberAccess). The compiler uses this
        /// for GetStaticField/SetStaticField/CallStaticMethod instead of the
        /// normal (dynamic) GetField/SetField/CallMethod.</summary>
        public sealed record StaticMember(string ClassName) : ResolvedRef;

        /// <summary>An instance field/method/property, referenced by a bare name (without
        /// 'this.' prefix) INSIDE a class (SPEC
        /// "Implicit member references") - the compiler treats this
        /// like 'this.Name' (LoadThis + GetField/SetField/CallMethod). Can
        /// arise only inside a NON-static method/a NON-
        /// static field initialiser (see Resolver.
        /// ResolveIdentifierRef) - there is no bound 'this' there.</summary>
        public sealed record ImplicitThisMember : ResolvedRef;
    }
}
