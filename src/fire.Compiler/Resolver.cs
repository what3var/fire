using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Resolving;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>A resolver error. The resolver does
    /// NOT stop at the first error, but collects all further ones (from the
    /// first error on the result is discarded anyway, but the user should not
    /// have to fix one error after the other): `Resolver.Resolve` throws at the
    /// end ONE ResolverException whose `Message`/`Line` are those of the first
    /// error (as before) and whose <see cref="Errors"/> contains ALL
    /// errors found in source order of the resolution
    /// (the first included). An exception thrown individually (internally,
    /// before collecting) has only itself as `Errors`.</summary>
    public sealed class ResolverException : Exception
    {
        public int Line { get; }

        public IReadOnlyList<ResolverException> Errors { get; }

        public ResolverException(string message, int line)
            : base($"{message} ({line})")
        {
            Line = line;
            Errors = new[] { this };
        }

        /// <summary>Combines several collected errors (at least one).</summary>
        public ResolverException(IReadOnlyList<ResolverException> errors)
            : base(errors[0].Message)
        {
            Line = errors[0].Line;
            Errors = errors;
        }
    }

    /// <summary>Result of a resolver run: everything the evaluator (and later
    /// the bytecode compiler) needs in order not to have to search by name any more.</summary>
    public sealed class ResolveResult
    {
        public required IReadOnlyDictionary<Expr, ResolvedRef> References { get; init; }
        public required IReadOnlyDictionary<string, ClassDecl> Classes { get; init; }
        public required int GlobalSlotCount { get; init; }
        public required IReadOnlyDictionary<string, ExternDecl> Externs { get; init; }

        /// <summary>`#noshadow` was present SOMEWHERE in the program (see
        /// Ast.NoShadowDirective/Resolver.ResolveFireStmt) - deactivates the
        /// read-only globals snapshot in EVERY `fire` block. Read by the compiler
        /// to set its `_globalSlotCount` effectively to 0
        /// (see the documentation there) - that alone is enough for
        /// CompileFireStmt to compile exactly as before the introduction of the snapshot,
        /// without needing conditional branches of its own for it.</summary>
        public required bool NoShadowGlobals { get; init; }
    }

    /// <summary>
    /// One-pass resolver over the AST. Two tasks:
    ///
    /// 1) Resolve variable references (IdentifierExpr, assignment targets) to static
    ///    slots (scope depth + index), instead of searching through the scope chain
    ///    by name at runtime later. Lambdas deliberately get a
    ///    scope whose parent is directly the global scope (not the lexically
    ///    enclosing scope) - this models the restricted lambda
    ///    visibility (only own scope + global, SPEC 4.2) all by itself, without
    ///    the depth counting during resolution needing a special case.
    ///
    /// 2) A few static checks that suggest themselves in this pass:
    ///    - 'return' only inside a function/method/lambda/constructor/destructor.
    ///    - 'base' (as an expression or as a constructor initialiser) only inside
    ///      a class that actually has a base class.
    ///    - Referenced class names (base class, 'new X()', 'is of X', type
    ///      annotations) must be known (declared or the built-in
    ///      base class 'Exception').
    ///
    /// Deliberately NOT checked: strict validity of 'this' (lambdas can use 'this'
    /// via 'on' sensibly even outside any class, a purely lexical
    /// check would be more restrictive than helpful here) - we leave that to the
    /// runtime.
    /// </summary>
    public sealed class Resolver
    {
        /// <summary>Primitive/built-in type names that ValidateTypeName accepts without
        /// lookup in `_classes`. 'lambda' stands for a
        /// lambda value (Runtime.LambdaValue/ValueKind.Lambda), optionally with a
        /// signature (`[ReturnType] lambda[&lt;Param1,...,ParamN&gt;]`, see
        /// Ast.TypeRef.LambdaSignature/SPEC "Lambda types with signature") -
        /// lambdas could always be passed as VALUES (dynamically
        /// typed), but until now there was no name to express that (and their
        /// expected parameter count) in a type annotation.
        /// Deliberately NOT 'func' (the keyword that introduces a lambda
        /// EXPRESSION, `func (x) => ...`) - that would collide with the
        /// expression syntax: NextLooksLikeTypeThenName() checks
        /// only the CURRENT token, a standalone 'func(x) => ...' as a
        /// statement would then wrongly be read as the beginning of a typed
        /// declaration (type name 'func', expected name next)
        /// instead of as a lambda expression. 'lambda' itself is only a
        /// PLAIN identifier (no keyword token, see Parser.
        /// NextLooksLikeTypeThenName), so it only collides if someone
        /// actually names something 'lambda' - unlike 'func' no
        /// existing language construct. Like 'class', 'lambda' is syntactically
        /// accepted; UNLIKE other type annotations, the
        /// parameter COUNT of a signature is however actually checked at runtime
        /// (VM.CheckLambdaSignature) - a deliberate exception from
        /// SPEC 8.1 ("type annotations not checked throughout"), because
        /// that could be implemented robustly here (unlike with class types) with reasonable
        /// effort.</summary>
        private static readonly HashSet<string> PrimitiveTypeNames = new()
        {
            "bool", "int", "float", "char", "string", "class", "undefined", "lambda",
        };

        private readonly Dictionary<Expr, ResolvedRef> _refs = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, ClassDecl> _classes = new();

        /// <summary>Resolves `tr` to its fully qualified name IF
        /// necessary (SPEC "Namespaces") - see TypeRef.ResolveBaseName for the
        /// exact rule. `tr.Namespaces` carries the context (current
        /// namespace + `#using`) already directly on itself, set by the
        /// parser EXACTLY at the place where `tr` was parsed - the
        /// resolver needs no "current class"/"active
        /// usings" state of its own for that any more.</summary>
        private string ResolveTypeRef(TypeRef tr) => tr.ResolveBaseName(IsKnownClassName);

        private readonly Dictionary<string, InterfaceDecl> _interfaces = new();
        private readonly Dictionary<string, ExternDecl> _externs = new();
        private readonly Dictionary<string, Dictionary<string, long>> _enums = new();
        private readonly HashSet<string> _nativeNames;
        private readonly HashSet<string> _tryableNativeNames;
        private readonly ResolverScope _globalScope = new(parent: null, isGlobal: true);

        private ResolverScope _current;
        private int _functionDepth;

        /// <summary>All errors found so far (see ResolverException,
        /// RecoverFrom) - evaluated by Resolve() at the end.</summary>
        private readonly List<ResolverException> _errors = new();
        private readonly HashSet<string> _errorMessages = new();

        /// <summary>Remembers an error (see ResolverException). The same
        /// error - same message, and that contains the line - often comes
        /// from several places (e.g. getter AND setter of a
        /// property both validate its type) and is remembered only ONCE.</summary>
        private void AddError(ResolverException error)
        {
            if (_errorMessages.Add(error.Message))
                _errors.Add(error);
        }

        /// <summary>See Ast.NoShadowDirective/ResolveResult.NoShadowGlobals
        /// - set by Resolve() BEFORE any statement resolution in a
        /// pre-pass (as with classes/enums/externs), so that it
        /// applies independently of the position of the `#noshadow` directive in the
        /// source (also for a `fire` block that stands BEFORE the
        /// directive in the source).</summary>
        private bool _noShadowGlobals;

        /// <summary>The final number of global slots of the main program (from a first pass), or null in the first pass. The slots of a
        /// `fire` block (captures and own variables) lie BEHIND all globals of the main program - also behind those that are only
        /// declared after the `fire`; otherwise they would coincide with these (the thread would then read the shared variable instead of its own).</summary>
        private int? _finalGlobalCount;
        private bool _sawFire;

        /// <summary>Like `_functionDepth`, but for `break`/`continue`: number of
        /// enclosing loops (0 = no `break`/`continue` valid) or
        /// `try`/`catch`/`finally` blocks since the last loop (>0 = a
        /// `break`/`continue` here would have to jump across a try boundary
        /// - deliberately rejected as an error, see ResolveTry documentation).
        /// Both are on entering a new function/method/lambda
        /// SAVED AND RESET TO 0 (not simply increased, like
        /// `_functionDepth`) - a loop of the ENCLOSING function
        /// must not be reachable via `break` from a nested lambda
        /// (different call/scope level at runtime).</summary>
        private int _loopDepth;
        private int _tryDepth;
        private int _unsafeDepth;
        private ClassDecl? _currentClass;
        private bool _currentClassHasBase;

        /// <summary>Names of the generic type parameters that are visible at the current
        /// place (class type parameters of the enclosing
        /// class UNION type parameters of the method currently being resolved, if
        /// that one is itself generic) - as a reference count instead of a plain
        /// HashSet, so that a method type parameter that COINCIDENTALLY has the same
        /// name as a class type parameter does not, when leaving the method,
        /// accidentally also remove the outer name (see
        /// AddTypeParamNames/RemoveTypeParamNames). ValidateTypeName
        /// accepts these names like a known type (see there),
        /// although they are no real class - a real type substitution
        /// does NOT take place (see SPEC "Generic classes"), `T` simply stays
        /// unspecific at runtime.</summary>
        private readonly Dictionary<string, int> _currentTypeParamNames = new();

        private void AddTypeParamNames(IEnumerable<string> names)
        {
            foreach (var n in names)
                _currentTypeParamNames[n] = _currentTypeParamNames.GetValueOrDefault(n) + 1;
        }

        private void RemoveTypeParamNames(IEnumerable<string> names)
        {
            foreach (var n in names)
            {
                int count = _currentTypeParamNames[n] - 1;
                if (count <= 0) _currentTypeParamNames.Remove(n);
                else _currentTypeParamNames[n] = count;
            }
        }
        private bool _inConstructor;
        /// <summary>SPEC "Static members" - `true` while the body
        /// of a static method/a static field initialiser
        /// is being resolved (see ResolveFunctionLike/ResolveFieldInitializer)
        /// - there is no bound 'this' there (see ThisExpr/BaseExpr
        /// check in ResolveExpr), unlike with instance methods/fields.</summary>
        private bool _inStaticMethod;

        private Resolver(IEnumerable<string>? nativeNames, IEnumerable<string>? tryableNativeNames)
        {
            _current = _globalScope;
            _nativeNames = nativeNames != null ? new HashSet<string>(nativeNames) : new HashSet<string>();
            _tryableNativeNames = tryableNativeNames != null ? new HashSet<string>(tryableNativeNames) : new HashSet<string>();
        }

        /// <summary>`nativeNames`/`tryableNativeNames`: known native
        /// function names (SPEC "Natives"). Unlike earlier (see
        /// Bytecode.Compiler.Compile history) NO `activeUsings`/
        /// `usingsByStmt` parameter needed any more - every type reference in the
        /// AST carries its own namespace context directly on itself
        /// (see Ast.TypeRef.Namespaces, set by the parser when parsing),
        /// the resolver needs no usings state of its own for that any more.</summary>
        public static ResolveResult Resolve(
            IReadOnlyList<Stmt> program, IEnumerable<string>? nativeNames = null, IEnumerable<string>? tryableNativeNames = null)
        {
            var resolver = Run(program, nativeNames, tryableNativeNames, null);
            // A program with `fire` resolves a second time, now that the number of its globals is known (see _finalGlobalCount).
            if (resolver._sawFire && resolver._errors.Count == 0) resolver = Run(program, nativeNames, tryableNativeNames, resolver._globalScope.Slots.Count);

            // From the first error on it is certain that there is no result -
            // but only HERE, after everything has been resolved, so that the
            // caller gets ALL errors at once (see ResolverException).
            if (resolver._errors.Count > 0)
                throw new ResolverException(resolver._errors);

            return new ResolveResult
            {
                References = resolver._refs,
                Classes = resolver._classes,
                GlobalSlotCount = resolver._globalScope.Slots.Count,
                Externs = resolver._externs,
                NoShadowGlobals = resolver._noShadowGlobals,
            };
        }

        private static Resolver Run(IReadOnlyList<Stmt> program, IEnumerable<string>? nativeNames, IEnumerable<string>? tryableNativeNames, int? finalGlobalCount)
        {
            var resolver = new Resolver(nativeNames, tryableNativeNames) { _finalGlobalCount = finalGlobalCount };
            resolver.CollectClasses(program);
            resolver.CollectExterns(program);
            resolver.CollectEnums(program);
            resolver._noShadowGlobals = program.Any(s => s is NoShadowDirective);
            foreach (var stmt in program)
                resolver.ResolveStmt(stmt);
            return resolver;
        }

        // -----------------------------------------------------------
        // Collect extern declarations beforehand (allows calls before the
        // declaration in the source, as with classes).
        // -----------------------------------------------------------
        private void CollectExterns(IReadOnlyList<Stmt> statements)
        {
            foreach (var stmt in statements)
            {
                if (stmt is not ExternDecl ed) continue;
                // A registered native function of the same name is no conflict,
                // but the normal case: 'extern' declares the signature in the
                // script, the native registry supplies the implementation for it.
                if (_externs.ContainsKey(ed.Name))
                {
                    AddError(new ResolverException($"'{ed.Name}' is already declared as extern", ed.Line));
                    continue;
                }
                _externs[ed.Name] = ed;
            }
        }

        /// <summary>Collects all `enum` declarations beforehand (allows forward
        /// references as with classes/externs) and in the process directly computes
        /// the actual int values of each member: an explicitly given
        /// value MUST be an int literal (real compile-time constant
        /// evaluation of arbitrary expressions does not exist in this language),
        /// otherwise auto-increment from the predecessor + 1 (0 for the first member).</summary>
        private void CollectEnums(IReadOnlyList<Stmt> statements)
        {
            foreach (var stmt in statements)
            {
                if (stmt is not EnumDecl ed) continue;
                if (_enums.ContainsKey(ed.Name))
                {
                    AddError(new ResolverException($"'{ed.Name}' is already declared as an enum", ed.Line));
                    continue;
                }
                if (IsKnownClassName(ed.Name))
                {
                    AddError(new ResolverException($"'{ed.Name}' is already declared as a class", ed.Line));
                    continue;
                }

                // The name is also registered for a faulty MEMBER
                // (with the members valid up to then) - otherwise every
                // later use of the enum would be reported as "unknown identifier",
                // a pure follow-up error of the one real error.
                var members = new Dictionary<string, long>();
                _enums[ed.Name] = members;
                long next = 0;
                foreach (var m in ed.Members)
                {
                    if (members.ContainsKey(m.Name))
                    {
                        AddError(new ResolverException($"Enum member '{ed.Name}.{m.Name}' is already declared", ed.Line));
                        continue;
                    }

                    long value;
                    if (m.ValueExpr != null)
                    {
                        if (m.ValueExpr is not LiteralExpr { Value.Kind: ValueKind.Int } lit)
                        {
                            AddError(new ResolverException(
                                $"Enum member '{ed.Name}.{m.Name}': the value must be an int literal " +
                                "(arbitrary expressions are not evaluated here).", ed.Line));
                            members[m.Name] = next; // Mitglied bleibt bekannt (siehe oben)
                            next++;
                            continue;
                        }
                        value = lit.Value.AsInt();
                    }
                    else
                    {
                        value = next;
                    }

                    members[m.Name] = value;
                    next = value + 1;
                }
            }
        }

        // -----------------------------------------------------------
        // Collect classes & interfaces beforehand (allows forward references and
        // that 'new Foo()' works even if 'Foo' is only declared later in the
        // source). Since the parser cannot know which
        // name after ':' is the base class and which are interfaces (that is
        // decidable only here, with knowledge of all class/interface names),
        // it is resolved here: at most one of the names may be a real
        // class (or 'Exception'), all others must be known
        // interfaces.
        // -----------------------------------------------------------
        /// <summary>Resolves a base class/interface name (entry from
        /// ClassDecl.BaseRefs) to its fully qualified name IF
        /// necessary (SPEC "Namespaces") - `tr.Namespaces` carries for that the context current at
        /// parsing (see TypeRef.ResolveBaseName).
        /// "Known" here means: 'Exception' OR a known class OR
        /// a known interface (unlike ResolveTypeRef, which only knows
        /// classes - a base CAN also be an interface).</summary>
        private string ResolveBaseRef(TypeRef tr)
        {
            bool Known(string n) => n == "Exception" || _classes.ContainsKey(n) || _interfaces.ContainsKey(n);
            // `class Home : Command<IDevice>`: the COUNT of the type arguments selects the generic class/the generic interface (see GenericClassNames)
            return tr.TypeArgCount == 0 ? tr.ResolveBaseName(Known) : GenericClassNames.ResolveNewTarget(tr, tr.TypeArgCount, Known);
        }

        private void CollectClasses(IReadOnlyList<Stmt> statements)
        {
            foreach (var stmt in statements)
            {
                switch (stmt)
                {
                    case ClassDecl cd:
                        if (_classes.ContainsKey(cd.Name))
                            AddError(new ResolverException($"Class '{GenericClassNames.PlainName(cd.Name)}' is already defined", cd.Line));
                        else
                            _classes[cd.Name] = cd;
                        break;
                    case InterfaceDecl id:
                        if (_interfaces.ContainsKey(id.Name))
                            AddError(new ResolverException($"Interface '{id.Name}' is already defined", id.Line));
                        else
                            _interfaces[id.Name] = id;
                        break;
                }
            }

            foreach (var cd in _classes.Values)
            {
                string? baseName = null;
                foreach (var baseRef in cd.BaseRefs ?? Array.Empty<TypeRef>())
                {
                    string n = ResolveBaseRef(baseRef);
                    bool isClass = n == "Exception" || _classes.ContainsKey(n);
                    if (isClass)
                    {
                        if (baseName != null)
                            AddError(new ResolverException(
                                $"Class '{DisplayName(cd)}' cannot have multiple base classes ('{baseName}' and '{n}')", cd.Line));
                        else
                            baseName = n;
                    }
                    else if (!_interfaces.ContainsKey(n))
                    {
                        AddError(new ResolverException(
                            $"'{baseRef.BaseName}' of class '{DisplayName(cd)}' is neither a known class nor a known interface", cd.Line));
                    }
                }
            }

            foreach (var cd in _classes.Values)
                foreach (var baseRef in cd.BaseRefs ?? Array.Empty<TypeRef>())
                {
                    string n = ResolveBaseRef(baseRef);
                    if (_interfaces.TryGetValue(n, out var iface))
                        Guard(() => ValidateImplementsInterface(cd, iface));
                }
        }

        /// <summary>The class name for error messages: for a generic
        /// class with a renamed key (see GenericClassNames) the
        /// name including type parameters (`Box&lt;T&gt;`), so that it can be told apart from the
        /// non-generic one of the same name.</summary>
        private static string DisplayName(ClassDecl cd) =>
            cd.TypeParams is { Count: > 0 }
                ? GenericClassNames.PlainName(cd.Name) + "<" + string.Join(", ", cd.TypeParams.Select(tp => tp.Name)) + ">"
                : cd.Name;

        /// <summary>Runs `check` and collects a resolver error thrown in the process,
        /// instead of passing it on (see
        /// ResolverException) - for checks after which it makes sense
        /// to carry on.</summary>
        private void Guard(Action check)
        {
            try { check(); }
            catch (ResolverException ex) { AddError(ex); }
        }

        /// <summary>Like <see cref="Guard"/>, additionally resets the resolver
        /// state to the state before `action` if an error
        /// occurred (see ResolveStmt) - for actions that change
        /// scopes/depths.</summary>
        private void GuardWithState(Action action)
        {
            var state = SaveState();
            try { action(); }
            catch (ResolverException ex)
            {
                AddError(ex);
                RestoreState(state);
            }
        }

        private string? GetBaseClassName(ClassDecl cd)
        {
            foreach (var baseRef in cd.BaseRefs ?? Array.Empty<TypeRef>())
            {
                string n = ResolveBaseRef(baseRef);
                if (n == "Exception" || _classes.ContainsKey(n))
                    return n;
            }
            return null;
        }

        private ClassDecl? GetBaseClassDecl(ClassDecl cd)
        {
            var baseName = GetBaseClassName(cd);
            return baseName != null && _classes.TryGetValue(baseName, out var baseCd) ? baseCd : null;
        }

        /// <summary>Searches over the base-class chain (AST level, ClassDecl.
        /// Members - at resolve time no RuntimeClass exists yet),
        /// whether `name` is a field/a method/a property - `startClass`
        /// first, then upwards. Returns (name of the declaring class,
        /// IsStatic) of the FIRST (nearest) class with a member
        /// of this name, `null` otherwise (no member in the whole chain).
        /// For SPEC "Implicit member references" (bare name instead of
        /// 'this.'/'ClassName.') AND for 'this.Name' if name is a
        /// static unit (see ResolveExpr/ResolveAssignTarget,
        /// MemberExpr case).</summary>
        private (string ClassName, bool IsStatic)? FindMemberInClassChain(ClassDecl? startClass, string name)
        {
            for (var cd = startClass; cd != null; cd = GetBaseClassDecl(cd))
            {
                foreach (var member in cd.Members)
                {
                    switch (member)
                    {
                        case FieldDecl fd when fd.Name == name: return (cd.Name, fd.IsStatic);
                        case MethodDecl md when md.Name == name: return (cd.Name, md.IsStatic);
                        case PropertyDecl pd when pd.Name == name: return (cd.Name, pd.IsStatic);
                    }
                }
            }
            return null;
        }

        /// <summary>Checks whether `cd` (incl. inherited methods via the
        /// base-class chain) provides all methods of `iface` by name+arity.
        /// A purely structural check, no return type
        /// check - the language is dynamically typed.</summary>
        private void ValidateImplementsInterface(ClassDecl cd, InterfaceDecl iface)
        {
            foreach (var m in iface.Methods)
            {
                if (!ClassHasMethod(cd, m.Name, m.Params.Count))
                    throw new ResolverException(
                        $"Class '{cd.Name}' does not implement interface '{iface.Name}' completely " +
                        $"(missing method '{m.Name}' with {m.Params.Count} parameter(s))", cd.Line);
            }
        }

        private bool ClassHasMethod(ClassDecl cd, string name, int arity)
        {
            foreach (var member in cd.Members)
                if (member is MethodDecl md && md.Name == name && md.Params.Count == arity)
                    return true;

            var baseName = GetBaseClassName(cd);
            if (baseName != null && _classes.TryGetValue(baseName, out var baseCd))
                return ClassHasMethod(baseCd, name, arity);
            return false;
        }

        private bool IsKnownClassName(string name) => name == "Exception" || _classes.ContainsKey(name);

        /// <summary>Tries to read `me.Target` as a closed, exactly
        /// written class name (SPEC "Static members") -
        /// for that builds the identifier chain of `me.Target` (IdentifierExpr
        /// or nested MemberExpr, e.g. with 'Geometry.Circle.Foo')
        /// into a dotted string and checks whether THE WHOLE is a
        /// known class name. `null` if `me.Target` is not a pure
        /// identifier chain or the chain yields no known class name
        /// (then `me` is an ordinary, dynamic expression).
        ///
        /// DELIBERATE RESTRICTION: checks ONLY the exactly written (possibly
        /// already fully qualified) name, NO resolution via #using/the
        /// current namespace - unlike a TypeRef, an
        /// IdentifierExpr/MemberExpr carries no namespace context of its own (that
        /// is only set when PARSING a TypeRef, see
        /// Parser.CurrentNamespaces) - 'Circle.Foo()' would with
        /// '#using Geometry' therefore NOT be recognised as 'Geometry.Circle', only
        /// 'Geometry.Circle.Foo()' written out in full works. A
        /// later extension for that would need namespace info on EVERY
        /// identifier, not only on TypeRef - a larger change of its own.</summary>
        private string? TryResolveStaticMemberAccess(MemberExpr me)
        {
            // The class we are currently in (see Ast.SelfClassExpr) -
            // stands only for the backing field of static auto-properties in
            // generic classes.
            if (me.Target is SelfClassExpr) return _currentClass?.Name;

            string? className = DottedName(me.Target);
            return className != null && IsKnownClassName(className) ? className : null;
        }

        /// <summary>The dotted name that `target` writes as a pure identifier chain
        /// (`A`, `Geometry.Circle`), or `null` if it is not
        /// such a chain (call, index, `this`, ...).</summary>
        private static string? DottedName(Expr target)
        {
            var pathSegments = new List<string>();
            Expr current = target;
            while (current is MemberExpr innerMe)
            {
                pathSegments.Add(innerMe.Name);
                current = innerMe.Target;
            }
            if (current is not IdentifierExpr rootId) return null;
            pathSegments.Add(rootId.Name);
            pathSegments.Reverse();
            return string.Join(".", pathSegments);
        }

        /// <summary>For `new Name&lt;Arg1,...&gt;(...)` (see Ast.NewExpr.
        /// TypeArgs) checks whether the given type arguments fit the type parameters of the
        /// target class (count) and satisfy its 'where' constraints
        /// (see Ast.TypeParam documentation: ',' between groups = OR, ':'
        /// within a group = AND). A purely static check, since
        /// type arguments are NAMES (classes/interfaces/primitive types OR,
        /// for an 'is in' condition, unit names), no runtime values
        /// - therefore completely here in the resolver, without any VM support.</summary>
        private void CheckTypeArgs(ClassDecl targetClass, NewExpr ne)
        {
            var typeParams = targetClass.TypeParams ?? Array.Empty<TypeParam>();
            string className = GenericClassNames.PlainName(targetClass.Name);

            if (typeParams.Count == 0)
            {
                if (ne.TypeArgs != null && ne.TypeArgs.Count > 0)
                    throw new ResolverException(
                        $"Class '{className}' is not generic, so it does not accept " +
                        "type arguments in angle brackets", ne.Line);
                return;
            }

            var typeArgs = ne.TypeArgs ?? Array.Empty<string>();
            if (typeArgs.Count != typeParams.Count)
                throw new ResolverException(
                    $"Class '{className}' is generic with {typeParams.Count} type parameter(s) - " +
                    $"'new {className}<...>' needs explicit type arguments for it " +
                    $"(got: {typeArgs.Count})", ne.Line);

            for (int i = 0; i < typeParams.Count; i++)
            {
                var tp = typeParams[i];
                string arg = typeArgs[i];
                if (tp.ConstraintGroups.Count == 0) continue; // unrestricted ('<T>' without 'where')

                bool satisfied = tp.ConstraintGroups.Any(
                    group => group.Constraints.All(c => SatisfiesConstraint(arg, c)));
                if (!satisfied)
                    throw new ResolverException(
                        $"Type argument '{arg}' for type parameter '{tp.Name}' of '{className}' does not satisfy " +
                        "any of the 'where' conditions", ne.Line);
            }
        }

        private bool SatisfiesConstraint(string argName, TypeConstraint c) => c.Kind switch
        {
            TypeConstraintKind.IsOf => TypeNameSatisfiesIsOf(argName, c.Name),
            // 'is in': argName interpreted as a unit name, dimensionally
            // compatible with c.Name (Unit.Parse never throws - an unknown
            // symbol becomes an atomic unit compatible only with itself,
            // see Unit.Parse documentation - the result is thus simply
            // "not satisfied", no error).
            _ => Unit.Parse(argName).IsCompatibleWith(Unit.Parse(c.Name)),
        };

        /// <summary>Does the type name `argName` satisfy an 'is of `targetName`' -
        /// exact name hit, OR (if argName is a known class)
        /// `targetName` occurs somewhere in its base-class/
        /// interface chain. Purely NAME-based (no instances, no
        /// values), analogous to ClassHasMethod.</summary>
        private bool TypeNameSatisfiesIsOf(string argName, string targetName)
        {
            if (argName == targetName) return true;
            if (!_classes.TryGetValue(argName, out var cd)) return false;
            foreach (var baseRef in cd.BaseRefs ?? Array.Empty<TypeRef>())
                if (TypeNameSatisfiesIsOf(ResolveBaseRef(baseRef), targetName))
                    return true;
            return false;
        }

        private void ValidateTypeName(TypeRef tr, int line)
        {
            // 'var' + only a unit (SPEC "Unit declarations") - no
            // real type name to validate, the type is derived from the
            // initialiser/context (see TypeRef.IsInferred documentation).
            if (tr.IsInferred) return;
            if (tr.LambdaSignature is { IsSelector: true } selector)
            {
                if (!_classes.ContainsKey("Reflect"))
                    throw new ResolverException($"'lambda {selector.SelectorKind}<...>' (selector) needs the reflection library: #import \"reflection\"", line);
                foreach (var target in selector.ParamTypeNames)
                    if (!PrimitiveTypeNames.Contains(target) && !_currentTypeParamNames.ContainsKey(target) && !IsKnownClassName(target))
                        throw new ResolverException($"Unknown type '{target}' in 'lambda {selector.SelectorKind}<{target}>'", line);
                return;
            }
            if (PrimitiveTypeNames.Contains(tr.BaseName)) return;
            if (_currentTypeParamNames.ContainsKey(tr.BaseName)) return;
            if (!IsKnownClassName(ResolveTypeRef(tr)) && !_interfaces.ContainsKey(ResolveBaseRef(tr)))
                throw new ResolverException($"Unknown type '{tr.BaseName}'", line);
        }

        /// <summary>Validates a complete TypeRef: base name like
        /// ValidateTypeName, plus - if present - that a bit width only stands with
        /// int/float and is one of the permitted values (8/16/32/64).
        /// Pointer depth is always valid.</summary>
        private void ValidateTypeRef(TypeRef type, int line)
        {
            ValidateTypeName(type, line);
            if (type.BitWidth == null) return;

            if (type.BaseName != "int" && type.BaseName != "float")
                throw new ResolverException(
                    $"A bit width is only valid for 'int'/'float', not for '{type.BaseName}'", line);
            if (type.BitWidth is not (8 or 16 or 32 or 64))
                throw new ResolverException(
                    $"Invalid bit width {type.BitWidth} (allowed: 8/16/32/64)", line);
        }

        private void ResolveArrayRanks(IReadOnlyList<Expr?> ranks)
        {
            foreach (var rank in ranks)
                if (rank != null) ResolveExpr(rank);
        }

        // -----------------------------------------------------------
        // Scope-Verwaltung
        // -----------------------------------------------------------
        private sealed class ResolverScope
        {
            public readonly ResolverScope? Parent;
            public readonly bool IsGlobal;
            public readonly Dictionary<string, int> Slots = new();
            public readonly HashSet<string> ReadonlySlots = new();

            /// <summary>Required unit (SPEC "Unit declarations") per
            /// name in THIS scope if the declaration had an explicit
            /// `: unit` - see Define/ResolveIdentifierRef.</summary>
            public readonly Dictionary<string, string> RequiredUnits = new();

            /// <summary>Names of the `ref` parameters of this scope (the slot holds a pointer to the caller's variable).</summary>
            public readonly HashSet<string> RefNames = new();

            /// <summary>Names that lie in THIS (lambda) scope as a capture (copy of an outer variable) - assignment is an error,
            /// a declaration of its own with the same name hides it (see Define).</summary>
            public readonly HashSet<string> CaptureNames = new();

            public ResolverScope(ResolverScope? parent, bool isGlobal = false)
            {
                Parent = parent;
                IsGlobal = isGlobal;
            }
        }

        private void PushScope() => _current = new ResolverScope(_current);
        private void PopScope() => _current = _current.Parent!;

        private void Define(string name, int line, bool isReadonly = false, string? requiredUnit = null, bool isRef = false)
        {
            if (_current.Slots.ContainsKey(name))
            {
                if (!_current.CaptureNames.Remove(name))
                    throw new ResolverException($"'{name}' is already declared in this scope", line);
                // The lambda body itself declares a name that the resolver has provisionally created as a capture: the declaration hides
                // it (the capture slot stays unused under an inaccessible key, the slot counting stays gapless).
                int captureSlot = _current.Slots[name];
                _current.Slots.Remove(name);
                _current.Slots["\u0001capture:" + name] = captureSlot;
                _current.ReadonlySlots.Remove(name);
                _current.RequiredUnits.Remove(name);
                _current.RefNames.Remove(name);
            }
            _current.Slots[name] = _current.Slots.Count;
            if (isRef) _current.RefNames.Add(name);
            if (isReadonly) _current.ReadonlySlots.Add(name);
            if (requiredUnit != null) _current.RequiredUnits[name] = requiredUnit;
        }

        private ResolvedRef ResolveIdentifierRef(string name, int line)
        {
            int depth = 0;
            var scope = _current;
            while (scope != null)
            {
                if (scope.Slots.TryGetValue(name, out int slot))
                {
                    scope.RequiredUnits.TryGetValue(name, out var requiredUnit);
                    return scope.IsGlobal
                        ? new ResolvedRef.Global(slot, requiredUnit)
                        : new ResolvedRef.Local(depth, slot, requiredUnit, scope.RefNames.Contains(name));
                }
                depth++;
                scope = scope.Parent;
            }

            if (_nativeNames.Contains(name))
                return new ResolvedRef.Native(name);
            if (_externs.ContainsKey(name))
                return new ResolvedRef.Extern(name);
            if (_tryableNativeNames.Contains(name))
                throw new ResolverException(
                    $"'{name}' is registered as 'tryable' - it can only be called with 'try {name}(...)', " +
                    "not as a direct call.", line);

            // SPEC "Implicit member references" - inside a class
            // a field/a method/a property may also be addressed WITHOUT
            // 'this.'/'ClassName.' prefix, just like a
            // local variable (in addition to, not instead of, the explicit
            // forms - see the MemberExpr case for 'this.X'/'ClassName.X').
            // Deliberately checked AFTER natives/externs - a class member
            // of the same name should not surprisingly shadow an existing native/extern
            // function.
            if (_currentClass != null)
            {
                var found = FindMemberInClassChain(_currentClass, name);
                if (found != null)
                {
                    if (found.Value.IsStatic)
                        return new ResolvedRef.StaticMember(found.Value.ClassName);
                    if (_inStaticMethod)
                        throw new ResolverException(
                            $"'{name}' is an instance member - it is not reachable in a static method or static " +
                            "field initializer without a bound 'this'", line);
                    return new ResolvedRef.ImplicitThisMember();
                }
            }

            // A lambda with `on target` (SPEC 4.2): the members of the bound object are visible unqualified. Whose class that is is only
            // known at runtime - the name is read/written/called there like `this.name` (an unknown name is then a runtime error).
            if (_inBoundLambda)
                return new ResolvedRef.ImplicitThisMember();

            throw new ResolverException($"Unknown identifier '{name}'", line);
        }

        /// <summary>Walks the same scope chain as ResolveIdentifierRef, only to
        /// check whether a variable was declared `readonly` - for
        /// the assignment check in ResolveAssignTarget (which has
        /// already resolved the variable successfully via ResolveIdentifierRef, so
        /// it always succeeds here).</summary>
        private bool IsCapturedVariable(string name)
        {
            var scope = _current;
            while (scope != null)
            {
                if (scope.Slots.ContainsKey(name))
                    return scope.CaptureNames.Contains(name);
                scope = scope.Parent;
            }
            return false;
        }

        private bool IsReadonlyVariable(string name)
        {
            var scope = _current;
            while (scope != null)
            {
                if (scope.Slots.ContainsKey(name))
                    return scope.ReadonlySlots.Contains(name);
                scope = scope.Parent;
            }
            return false;
        }

        // -----------------------------------------------------------
        // Statements
        // -----------------------------------------------------------
        /// <summary>Resolves a statement; an error occurring in the process is
        /// COLLECTED (see ResolverException), and resolution carries on with
        /// the NEXT statement. Every statement - also every one in
        /// a nested block/method body/a lambda - is a
        /// restart point of its own, so an error spoils at most the
        /// rest of ITS statement. The state of the resolver (scope chain,
        /// depth counters, current class...) is reset for that to the state BEFORE the
        /// statement: the resolution itself restores it only at a
        /// normal end, an error in the middle would otherwise leave it
        /// misadjusted and trigger follow-up errors.</summary>
        private void ResolveStmt(Stmt stmt)
        {
            var state = SaveState();
            try
            {
                ResolveStmtCore(stmt);
            }
            catch (ResolverException ex)
            {
                AddError(ex);
                RestoreState(state);
                // A failed `var x = <error>` declares x anyway,
                // otherwise every later use of x would be reported as "unknown
                // identifier" - a pure follow-up error of the one
                // real error.
                if (stmt is VarDeclStmt vd && !_current.Slots.ContainsKey(vd.Name))
                    Define(vd.Name, vd.Line, vd.IsReadonly, vd.Type?.Unit);
            }
        }

        /// <summary>The part of the resolver state that the resolution of a
        /// statement/class member temporarily changes (see
        /// ResolveStmt).</summary>
        private readonly record struct ResolverState(
            ResolverScope Current, int FunctionDepth, int LoopDepth, int TryDepth, int UnsafeDepth,
            ClassDecl? CurrentClass, bool CurrentClassHasBase, bool InConstructor, bool InStaticMethod,
            Dictionary<string, int>? TypeParamNames);

        private ResolverState SaveState() => new(
            _current, _functionDepth, _loopDepth, _tryDepth, _unsafeDepth,
            _currentClass, _currentClassHasBase, _inConstructor, _inStaticMethod,
            _currentTypeParamNames.Count > 0 ? new Dictionary<string, int>(_currentTypeParamNames) : null);

        private void RestoreState(ResolverState state)
        {
            _current = state.Current;
            _functionDepth = state.FunctionDepth;
            _loopDepth = state.LoopDepth;
            _tryDepth = state.TryDepth;
            _unsafeDepth = state.UnsafeDepth;
            _currentClass = state.CurrentClass;
            _currentClassHasBase = state.CurrentClassHasBase;
            _inConstructor = state.InConstructor;
            _inStaticMethod = state.InStaticMethod;
            _currentTypeParamNames.Clear();
            if (state.TypeParamNames != null)
                foreach (var (name, count) in state.TypeParamNames)
                    _currentTypeParamNames[name] = count;
        }

        private void ResolveStmtCore(Stmt stmt)
        {
            switch (stmt)
            {
                case Stmt.BlockStmt block:
                    ResolveBlockNewScope(block);
                    break;

                case ExprStmt es:
                    ResolveExpr(es.Expression);
                    break;

                case NoOpStmt:
                    break;

                case NoShadowDirective:
                    // Already collected in the pre-pass (CollectNoShadowDirective)
                    // - nothing more to do here.
                    break;

                case NoSyncDirective:
                    break; // takes effect only at runtime (see Compiler.Compile: SetAutoSync)

                case TimeoutDirective td:
                    ResolveExpr(td.Value);
                    break; // takes effect only at runtime (see Compiler.Compile: SetTimeout)

                case VarDeclStmt vd:
                    if (vd.Initializer != null) ResolveExprAllowTake(vd.Initializer);
                    if (vd.Type != null) ValidateTypeRef(vd.Type, vd.Line);
                    ResolveArrayRanks(vd.ArrayRanks);
                    if (vd.IsReadonly && vd.Initializer == null &&
                        !(vd.ArrayRanks.Count > 0 && vd.ArrayRanks[0] != null))
                        throw new ResolverException(
                            $"'readonly {vd.Name}' needs an initializer (or an array size)", vd.Line);
                    // 'var arr[4] = [1,2,3,4]' (explicit size AND an
                    // array literal as initialiser) - only a BEST-EFFORT
                    // check if the size itself is an integer LITERAL
                    // (the common case): if it does not match the number of
                    // literal elements, that is a compile error
                    // instead of a silently differently sized array (the
                    // declared size would otherwise be overwritten WITHOUT any message by the
                    // initialiser, see Compiler.CompileStmt).
                    // A DYNAMIC size (variable/expression) is
                    // NOT checked here - that would need a runtime check,
                    // which this development stage deliberately does not build.
                    if (vd.Initializer is ArrayLiteralExpr arrLit
                        && vd.ArrayRanks.Count > 0 && vd.ArrayRanks[0] is LiteralExpr sizeLit
                        && sizeLit.Value.Kind == ValueKind.Int
                        && sizeLit.Value.AsInt() != arrLit.Elements.Count)
                        throw new ResolverException(
                            $"'{vd.Name}[{sizeLit.Value.AsInt()}]' expects {sizeLit.Value.AsInt()} elements, " +
                            $"but the array literal initializer has {arrLit.Elements.Count}", vd.Line);
                    Define(vd.Name, vd.Line, vd.IsReadonly, vd.Type?.Unit);
                    break;

                case IfStmt ifs:
                    ResolveExpr(ifs.Condition);
                    ResolveStmtAsScope(ifs.Then);
                    if (ifs.Else != null) ResolveStmtAsScope(ifs.Else);
                    break;

                case WhileStmt ws:
                    ResolveExpr(ws.Condition);
                    _loopDepth++;
                    int savedFinallyDepth = _tryDepth; _tryDepth = 0;
                    ResolveStmtAsScope(ws.Body);
                    _tryDepth = savedFinallyDepth;
                    _loopDepth--;
                    break;

                case ForStmt fs:
                    ResolveFor(fs);
                    break;

                case ForeachStmt fe:
                    ResolveForeach(fe);
                    break;

                case ReturnStmt rs:
                    if (_functionDepth == 0)
                        throw new ResolverException("'return' outside of a function/method", rs.Line);
                    if (rs.Value != null) ResolveExpr(rs.Value);
                    break;

                case ThrowStmt ts:
                    ResolveExpr(ts.Value);
                    break;

                case DeleteStmt ds:
                    ResolveExpr(ds.Target);
                    break;

                case TryStmt trys:
                    ResolveTry(trys);
                    break;

                case BreakStmt bs:
                    if (_loopDepth == 0)
                        throw new ResolverException("'break' outside of a loop ('while'/'for'/'foreach')", bs.Line);
                    if (_tryDepth > 0)
                        throw new ResolverException("'break' cannot be used to leave a 'finally' block (a limitation of this stage - see BYTECODE.md)", bs.Line);
                    break;

                case ContinueStmt cs:
                    if (_loopDepth == 0)
                        throw new ResolverException("'continue' outside of a loop ('while'/'for'/'foreach')", cs.Line);
                    if (_tryDepth > 0)
                        throw new ResolverException("'continue' cannot be used to leave a 'finally' block (a limitation of this stage - see BYTECODE.md)", cs.Line);
                    break;

                case FireStmt fireStmt:
                    ResolveFireStmt(fireStmt);
                    break;

                case SectionEnterStmt:
                case SectionExitStmt:
                    break;

                case SilenceStmt silence:
                    ResolveExpr(silence.Target);
                    break;

                case PostGlobalStmt postGlobal:
                    foreach (var arg in postGlobal.Args) ResolveExprAllowTake(arg);
                    ResolveLambda(postGlobal.Lambda);
                    break;

                case LeaveStmt:
                    // Deliberately no restriction to "only inside a
                    // fire block" (see Ast.LeaveStmt documentation) - nothing to
                    // check.
                    break;

                case TerminateStmt terminateStmt:
                    if (terminateStmt.Value != null) ResolveExpr(terminateStmt.Value);
                    break;

                case CatchThreadsDecl threadsDecl:
                    if (threadsDecl.TypeRef != null && !IsKnownClassName(ResolveTypeRef(threadsDecl.TypeRef)))
                        AddError(new ResolverException($"Unknown exception type '{threadsDecl.TypeRef.BaseName}'", threadsDecl.Line));
                    ResolveGlobalHandlerBody(threadsDecl.VarName, threadsDecl.Body);
                    break;

                case CatchTerminateDecl terminateDecl:
                    ResolveGlobalHandlerBody(terminateDecl.VarName, terminateDecl.Body);
                    break;

                case ProcessStmt processStmt:
                    ResolveExpr(processStmt.Target);
                    break;

                case ClassDecl cd:
                    ResolveClass(cd);
                    break;

                case InterfaceDecl:
                    // Already validated in the pre-pass (CollectClasses) - nothing to do here.
                    break;

                case EnumDecl:
                    // Already validated in the pre-pass (CollectEnums) and filled with
                    // values - nothing to do here.
                    break;

                case ClassExtensionDecl cx:
                    // Should NEVER arrive here - Parser.MergeClassExtensions
                    // already resolves that completely before resolving (see
                    // Ast.ClassExtensionDecl documentation). Only as a safety net,
                    // in case the program was created by a way other than
                    // Parser.Parse()/ParseMultiple().
                    throw new ResolverException(
                        $"Internal error: 'class extends {cx.TargetRef.BaseName}' was not merged " +
                        "(the program must be produced by Parser.Parse()/ParseMultiple()).", cx.Line);

                case ExternDecl ed:
                    if (ed.ReturnType != null) ValidateTypeRef(ed.ReturnType, ed.Line);
                    foreach (var p in ed.Params)
                    {
                        if (p.Type != null) ValidateTypeRef(p.Type, ed.Line);
                        ResolveArrayRanks(p.ArrayRanks);
                    }
                    break;

                case UnsafeStmt us:
                    _unsafeDepth++;
                    ResolveBlockNewScope(us.Body);
                    _unsafeDepth--;
                    break;

                default:
                    throw new ResolverException(
                        $"Unexpected statement {stmt.GetType().Name} at this position", stmt.Line);
            }
        }

        /// <summary>Resolves a statement so that it gets a scope of its own -
        /// either directly (if it is already a block) or via a
        /// throw-away scope (for single-line if/while/for bodies without '{}').</summary>
        private void ResolveStmtAsScope(Stmt body)
        {
            if (body is Stmt.BlockStmt block)
            {
                ResolveBlockNewScope(block);
            }
            else
            {
                PushScope();
                ResolveStmt(body);
                PopScope();
            }
        }

        private void ResolveBlockNewScope(Stmt.BlockStmt block)
        {
            PushScope();
            foreach (var s in block.Statements) ResolveStmt(s);
            PopScope();
        }

        private void ResolveFor(ForStmt fs)
        {
            PushScope(); // encloses init/condition/increment/body together
            if (fs.Init != null) ResolveStmt(fs.Init);
            if (fs.Condition != null) ResolveExpr(fs.Condition);
            if (fs.Increment != null) ResolveExpr(fs.Increment);
            _loopDepth++;
            int savedFinallyDepth = _tryDepth; _tryDepth = 0;
            ResolveStmtAsScope(fs.Body);
            _tryDepth = savedFinallyDepth;
            _loopDepth--;
            PopScope();
        }

        private void ResolveForeach(ForeachStmt fe)
        {
            ResolveExpr(fe.Iterable); // in the enclosing scope, not in the loop scope
            PushScope();
            Define(fe.VarName, fe.Line);
            _loopDepth++;
            int savedFinallyDepth = _tryDepth; _tryDepth = 0;
            ResolveStmtAsScope(fe.Body);
            _tryDepth = savedFinallyDepth;
            _loopDepth--;
            PopScope();
        }

        /// <summary>`break`/`continue` may be used out of the `try` and `catch` blocks (the compiler clears away handlers and
        /// scopes and executes an existing `finally` beforehand, see Compiler.CompileBreakOrContinue) - but not out of the `finally` block
        /// itself: `_tryDepth` therefore now only counts the surrounding `finally` blocks (anew per loop).</summary>
        private void ResolveTry(TryStmt t)
        {
            ResolveBlockNewScope(t.TryBlock);

            foreach (var c in t.Catches)
            {
                // An unknown exception type does not prevent the resolution
                // of the catch body (and the other blocks).
                if (c.TypeRef != null && !IsKnownClassName(ResolveTypeRef(c.TypeRef)))
                    AddError(new ResolverException($"Unknown exception type '{c.TypeRef.BaseName}'", c.Line));

                PushScope();
                Define(c.VarName, c.Line);
                foreach (var s in c.Body.Statements) ResolveStmt(s);
                PopScope();
            }

            if (t.Finally != null)
            {
                _tryDepth++;
                ResolveBlockNewScope(t.Finally);
                _tryDepth--;
            }
        }

        // -----------------------------------------------------------
        // Klassen
        // -----------------------------------------------------------
        private void ResolveClass(ClassDecl cd)
        {
            var savedClass = _currentClass;
            var savedHasBase = _currentClassHasBase;
            _currentClass = cd;
            _currentClassHasBase = GetBaseClassName(cd) != null;

            var classTypeParamNames = cd.TypeParams?.Select(tp => tp.Name).ToList() ?? new List<string>();
            AddTypeParamNames(classTypeParamNames);

            // Per class: allows several methods of the same name
            // (overloading), but only with a DIFFERENT parameter count -
            // that is the only signature generally distinguishable at call time
            // in a dynamically typed language (see
            // RuntimeClass.FindMethod documentation). Constructors likewise, but in
            // a SEPARATE count (the name plays no role there -
            // there is only "the" constructor of a class, several
            // overloads differ purely by arity).
            var seenMethodSignatures = new HashSet<(string Name, int Arity)>();
            var seenConstructorArities = new HashSet<int>();

            // Every member is a restart point of its own (see
            // ResolveStmt) - an error in one field/method does
            // not prevent the resolution of the other members.
            foreach (var member in cd.Members)
            {
                GuardWithState(() =>
                {
                    switch (member)
                    {
                        case FieldDecl fd:
                            // An invalid type does not prevent the resolution of the
                            // initialiser (and vice versa) - both individually
                            // guarded, so that ALL errors are reported.
                            if (fd.Type != null) Guard(() => ValidateTypeRef(fd.Type, fd.Line));
                            ResolveArrayRanks(fd.ArrayRanks);
                            ResolveFieldInitializer(fd);
                            break;

                        case ConstructorDecl ctor:
                            if (!seenConstructorArities.Add(ctor.Params.Count))
                                AddError(new ResolverException(
                                    $"A constructor with {ctor.Params.Count} parameter(s) is already defined " +
                                    "in this class (an overload needs a different number of parameters).", ctor.Line));
                            ResolveFunctionLike(ctor.Params, ctor.Body, ctor.BaseArgs, isConstructor: true);
                            break;

                        case DestructorDecl dtor:
                            ResolveFunctionLike(Array.Empty<LambdaParam>(), dtor.Body, baseArgs: null, isConstructor: false);
                            break;

                        case MethodDecl md:
                            var methodTypeParamNames = md.TypeParams?.Select(tp => tp.Name).ToList() ?? new List<string>();
                            AddTypeParamNames(methodTypeParamNames);
                            try
                            {
                                if (md.ReturnType != null)
                                    Guard(() => ValidateTypeRef(md.ReturnType, md.Line));
                                if (!seenMethodSignatures.Add((md.Name, md.Params.Count)))
                                    AddError(new ResolverException(
                                        $"Method '{md.Name}' with {md.Params.Count} parameter(s) is already defined " +
                                        "in this class (an overload needs a different number of parameters).", md.Line));
                                ResolveFunctionLike(md.Params, md.Body, baseArgs: null, isConstructor: false, isStatic: md.IsStatic);
                            }
                            finally
                            {
                                RemoveTypeParamNames(methodTypeParamNames);
                            }
                            break;

                        case PropertyDecl pd:
                            if (pd.Type != null) Guard(() => ValidateTypeRef(pd.Type, pd.Line));
                            // Getter: like a parameterless method. Setter: like
                            // a method with exactly one parameter 'value' of the
                            // property type (implicit, like C#'s setter parameter) -
                            // quite normal parameter resolution, no
                            // special treatment needed.
                            if (pd.Getter != null)
                                GuardWithState(() => ResolveFunctionLike(
                                    Array.Empty<LambdaParam>(), pd.Getter, baseArgs: null, isConstructor: false, isStatic: pd.IsStatic));
                            if (pd.Setter != null)
                            {
                                var setterParams = new[] { new LambdaParam("value", pd.Type, Array.Empty<Expr?>()) };
                                GuardWithState(() => ResolveFunctionLike(
                                    setterParams, pd.Setter, baseArgs: null, isConstructor: false, isStatic: pd.IsStatic));
                            }
                            break;
                    }
                });
            }

            RemoveTypeParamNames(classTypeParamNames);
            _currentClass = savedClass;
            _currentClassHasBase = savedHasBase;
        }

        /// <summary>Field initialisers see (like lambdas) only their own scope
        /// + global - plus implicitly 'this', since they are evaluated per instance in the constructor
        /// context, but do not know the parameters of any
        /// particular constructor.</summary>
        private void ResolveFieldInitializer(FieldDecl fd)
        {
            if (fd.Initializer == null) return;
            var saved = _current;
            _current = new ResolverScope(_globalScope);
            bool savedInStaticMethod = _inStaticMethod;
            _inStaticMethod = fd.IsStatic; // SPEC "Static members" - no 'this' in a static field initialiser
            ResolveExpr(fd.Initializer);
            _inStaticMethod = savedInStaticMethod;
            _current = saved;
        }

        /// <summary>Optional parameters (with a default value) must be CONTIGUOUS at the end of the
        /// parameter list - no mandatory parameter after an
        /// optional one (otherwise with a call with fewer arguments
        /// it would not be clear which parameter is "missing"). The same rule as in
        /// most languages with optional parameters.</summary>
        private static void ValidateOptionalParamsAreTrailing(IReadOnlyList<LambdaParam> parms, int line)
        {
            bool seenOptional = false;
            foreach (var p in parms)
            {
                if (p.DefaultValue != null) { seenOptional = true; continue; }
                if (seenOptional)
                    throw new ResolverException(
                        $"Parameter '{p.Name}' without a default value must not follow an optional parameter " +
                        "(optional parameters must be contiguous at the end).", line);
            }
        }

        /// <summary>Default-value expressions, like field initialisers (see
        /// ResolveFieldInitializer), see only their own scope + global + `this`
        /// - NOT the other parameters of the same function, since they are
        /// evaluated independently of these (see VM.
        /// FillDefaultArgs - a separate, isolated evaluation per missing
        /// parameter, not part of the normal function scope).</summary>
        private void ResolveParamDefaults(IReadOnlyList<LambdaParam> parms)
        {
            var saved = _current;
            _current = new ResolverScope(_globalScope);
            foreach (var p in parms)
                if (p.DefaultValue != null)
                    ResolveExpr(p.DefaultValue);
            _current = saved;
        }

        private void ResolveFunctionLike(
            IReadOnlyList<LambdaParam> parms, Stmt.BlockStmt body, IReadOnlyList<Expr>? baseArgs, bool isConstructor, bool isStatic = false)
        {
            // Errors in the head (parameter list, base(...)) do not prevent the
            // resolution of the body - everything individually guarded, so that ALL
            // errors are reported (see ResolveStmt).
            Guard(() => ValidateOptionalParamsAreTrailing(parms, body.Line));
            GuardWithState(() => ResolveParamDefaults(parms));

            PushScope();
            foreach (var p in parms)
            {
                if (p.Type != null) Guard(() => ValidateTypeRef(p.Type, body.Line));
                ResolveArrayRanks(p.ArrayRanks);
                Guard(() => Define(p.Name, body.Line, requiredUnit: p.Type?.Unit, isRef: p.ByRef));
            }

            if (baseArgs != null)
            {
                if (!_currentClassHasBase)
                    AddError(new ResolverException(
                        "'base(...)' is only valid in a class with a base class", body.Line));
                foreach (var a in baseArgs) ResolveExprAllowTake(a);
            }

            // IMPORTANT: do NOT derive from 'baseArgs != null' - that only means
            // "has an EXPLICIT 'base(...)'", not "is a constructor".
            // A class without a base class never has baseArgs, its constructor
            // needs the flag anyway.
            bool savedInConstructor = _inConstructor;
            _inConstructor = isConstructor;

            bool savedInStaticMethod = _inStaticMethod;
            _inStaticMethod = isStatic;

            int savedLoopDepth = _loopDepth;
            int savedTryDepth = _tryDepth;
            _loopDepth = 0;
            _tryDepth = 0;

            _functionDepth++;
            foreach (var s in body.Statements) ResolveStmt(s);
            _functionDepth--;

            _loopDepth = savedLoopDepth;
            _tryDepth = savedTryDepth;

            _inConstructor = savedInConstructor;
            _inStaticMethod = savedInStaticMethod;

            PopScope();
        }

        // -----------------------------------------------------------
        // Expressions
        // -----------------------------------------------------------
        /// <summary>Resolves an expression; an error in it is collected
        /// (see ResolveStmt), resolution carries on with the sibling
        /// expressions - e.g. with `f(a, b)` both unknown
        /// identifiers are reported, not only `a`.</summary>
        /// <summary>`take x` is valid directly as an argument and as the value of an assignment/declaration (SPEC 2.2): the places register it here before resolving.</summary>
        private readonly HashSet<Expr> _takeAllowed = new();
        private void ResolveExprAllowTake(Expr expr)
        {
            if (expr is UnaryExpr { Op: UnaryOp.Take }) _takeAllowed.Add(expr);
            ResolveExpr(expr);
        }

        private void ResolveExpr(Expr expr)
        {
            var state = SaveState();
            try
            {
                ResolveExprCore(expr);
            }
            catch (ResolverException ex)
            {
                AddError(ex);
                RestoreState(state);
            }
        }

        private void ResolveExprCore(Expr expr)
        {
            switch (expr)
            {
                case LiteralExpr:
                    break;

                case IdentifierExpr id:
                    _refs[id] = ResolveIdentifierRef(id.Name, id.Line);
                    break;

                case ThisExpr te:
                    if (_inStaticMethod)
                        throw new ResolverException(
                            "'this' is not valid in a static method or static field initializer " +
                            "(no instance bound)", te.Line);
                    break; // Laufzeit entscheidet, ob/was 'this' aktuell gebunden ist

                case BaseExpr be:
                    if (_inStaticMethod)
                        throw new ResolverException(
                            "'base' is not valid in a static method (no instance bound)", be.Line);
                    if (_currentClass == null || !_currentClassHasBase)
                        throw new ResolverException(
                            "'base' is only valid inside a class with a base class", be.Line);
                    break;

                case UnaryExpr u:
                    if (u.Op == UnaryOp.Take && !_takeAllowed.Remove(u))
                        throw new ResolverException(
                            "'take' is only valid as an argument of a call or on the right of '=' / 'var x =' (SPEC 2.2)", u.Line);
                    if ((u.Op == UnaryOp.Dereference || u.Op == UnaryOp.AddressOf) && _unsafeDepth == 0)
                        throw new ResolverException(
                            $"'{(u.Op == UnaryOp.Dereference ? "*" : "&")}' is only valid inside an 'unsafe' block", u.Line);
                    ResolveExpr(u.Operand);
                    break;

                case BinaryExpr b:
                    ResolveExpr(b.Left);
                    ResolveExpr(b.Right);
                    break;

                case UnitCoerceExpr uc:
                    ResolveExpr(uc.Operand);
                    break;

                case TypeCoerceExpr tc:
                    ResolveExpr(tc.Operand);
                    break;

                case IsInExpr iin:
                    ResolveExpr(iin.Operand);
                    break;

                case IsOfExpr iof:
                    ResolveExpr(iof.Operand);
                    // `value is of IFoo`: an interface is also allowed as a type (the class names it in `class X : IFoo`)
                    if (!_interfaces.ContainsKey(iof.TypeRef.BaseName)) ValidateTypeName(iof.TypeRef, iof.Line);
                    break;

                case IsFromExpr ifr:
                    ResolveExpr(ifr.Operand);
                    ResolveExpr(ifr.OwnerExpr);
                    break;

                case CallExpr call:
                    ResolveExpr(call.Callee);
                    foreach (var a in call.Args) ResolveExprAllowTake(a);
                    break;

                case MemberExpr me:
                    // 'EnumName.Member' - recognised purely by the
                    // target name (as a bare identifier) being a known enum name.
                    // Deliberate design decision: an enum name always "wins"
                    // there against a variable of the same name in the scope
                    // (just as a class name cannot be
                    // shadowed by a variable either) - the same name collision would be
                    // confusing anyway and easy to avoid in practice.
                    // An enum in a namespace is reachable this way too, but then
                    // fully qualified ('Geometry.Kind.Round') - as with
                    // static class access (see TryResolveStaticMemberAccess)
                    // only the exactly written name counts, no `#using`/
                    // namespace resolution.
                    string? enumName = DottedName(me.Target);
                    if (enumName != null && _enums.TryGetValue(enumName, out var enumMembers))
                    {
                        if (!enumMembers.TryGetValue(me.Name, out long enumValue))
                            throw new ResolverException($"'{enumName}' has no member '{me.Name}'", me.Line);
                        _refs[me] = new ResolvedRef.EnumMember(enumValue);
                        break;
                    }
                    // 'ClassName.Member' (SPEC "Static members") - as
                    // in the enum case: a known class name always "wins"
                    // against a variable of the same name. The actual
                    // access (does the member exist, is it REALLY
                    // static, access modifier) is deliberately NOT checked here,
                    // but only in the VM (GetStaticField/
                    // CallStaticMethod) - the same boundary as with normal
                    // instance fields/methods (see VM.CheckFieldAccess),
                    // the resolver does not know the field/method names of a class
                    // completely enough to validate that reliably here already
                    // (inheritance, dynamically set fields).
                    string? staticClassName = TryResolveStaticMemberAccess(me);
                    if (staticClassName != null)
                    {
                        _refs[me] = new ResolvedRef.StaticMember(staticClassName);
                        break;
                    }
                    // 'this.StaticMember' (SPEC "Implicit member
                    // references") - a static member is also reachable via
                    // 'this.' (in addition to a bare name and
                    // 'ClassName.'), although 'this' itself has nothing to do with the
                    // static storage location - the compiler
                    // then converts that into the same GetStaticField/
                    // SetStaticField/CallStaticMethod path as 'ClassName.X',
                    // NOT into GetField/SetField (which would not find a static
                    // field, see RuntimeClass.StaticFieldValues).
                    if (me.Target is ThisExpr && _currentClass != null)
                    {
                        var thisMember = FindMemberInClassChain(_currentClass, me.Name);
                        if (thisMember is { IsStatic: true })
                        {
                            _refs[me] = new ResolvedRef.StaticMember(thisMember.Value.ClassName);
                            break;
                        }
                    }
                    ResolveExpr(me.Target); // .Name bleibt unresolved - dynamischer Feld-/Methodenzugriff
                    break;

                case IndexExpr ix:
                    ResolveExpr(ix.Target);
                    ResolveExpr(ix.Index);
                    break;

                case AssignExpr asg:
                    ResolveExprAllowTake(asg.Value);
                    if (asg.Value is UnaryExpr { Op: UnaryOp.Take } && asg.Target is UnaryExpr { Op: UnaryOp.Dereference })
                        throw new ResolverException("'take' needs a holder: a variable, a field or an array element (not a pointer)", asg.Line);
                    ResolveAssignTarget(asg.Target);
                    break;

                case IncDecExpr incDec:
                    // Needs both read and write access to
                    // the same target - ResolveAssignTarget covers both
                    // (fills e.g. _refs for IdentifierExpr just as a
                    // normal read would, see there), additionally also
                    // the readonly/unsafe checks that apply to assignments
                    // anyway and must apply just the same to '++'/'--'.
                    ResolveAssignTarget(incDec.Target);
                    break;

                case NewExpr ne:
                    // An error in the target (unknown class, wrong
                    // type arguments) does not prevent the resolution of the arguments.
                    Guard(() =>
                    {
                        // The number of type arguments chooses between a
                        // non-generic and a generic class of the same name
                        // (see GenericClassNames).
                        string resolvedNewClassName = GenericClassNames.ResolveNewTarget(
                            ne.ClassRef, ne.TypeArgs?.Count ?? 0, IsKnownClassName);
                        if (!IsKnownClassName(resolvedNewClassName))
                            throw new ResolverException($"Unknown class '{ne.ClassRef.BaseName}'", ne.Line);
                        if (_classes.TryGetValue(resolvedNewClassName, out var newTargetCd))
                            CheckTypeArgs(newTargetCd, ne);
                        else if (ne.TypeArgs != null && ne.TypeArgs.Count > 0)
                            throw new ResolverException(
                                $"'{ne.ClassRef.BaseName}' is not generic, so it does not accept type arguments in angle brackets",
                                ne.Line);
                    });
                    foreach (var a in ne.Args) ResolveExprAllowTake(a);
                    break;

                case NewArrayExpr na:
                    ValidateTypeRef(na.ElementType, na.Line);
                    if (na.SizeExprs.Count == 0 || na.SizeExprs[0] == null)
                        throw new ResolverException(
                            "'new Type[...]' needs a size at least for the first dimension " +
                            "(e.g. 'new int[3]' or 'new int[3][]' - not 'new int[]').", na.Line);
                    ResolveArrayRanks(na.SizeExprs);
                    break;

                case NewBufferExpr nb:
                    ResolveExpr(nb.SizeExpr);
                    break;

                case ArrayLiteralExpr al:
                    foreach (var el in al.Elements) ResolveExpr(el);
                    break;

                case InterpolatedStringExpr ise:
                    foreach (var part in ise.Parts)
                        if (part is InterpolationExprPart ep) ResolveExpr(ep.Expression);
                    break;

                case ThrowExpr th:
                    ResolveExpr(th.Value);
                    break;

                case LambdaExpr lam:
                    ResolveLambda(lam);
                    break;

                case SyncExpr syncExpr:
                    ResolveExpr(syncExpr.Target);
                    break;

                case ProbeExpr probe:
                    ResolveExpr(probe.Target);
                    ResolveExpr(probe.Handler);
                    break;

                case SyncGlobalsExpr:
                    break;

                case TryProcessExpr tryProcessExpr:
                    ResolveExpr(tryProcessExpr.Target);
                    break;

                case TryCallExpr tryCallExpr:
                    ResolveTryCallExpr(tryCallExpr);
                    break;

                default:
                    throw new ResolverException($"Unknown expression type {expr.GetType().Name}", expr.Line);
            }
        }

        /// <summary>`try Name(args)` (see Ast.TryCallExpr) - the inner
        /// call MUST be a direct call of a KNOWN name registered as "tryable"
        /// (see Bytecode.NativeRegistry.
        /// RegisterTryable) - no method call, no lambda call, no
        /// ordinary native name or one declared as 'extern' (those may
        /// NOT be called with 'try' - only registered "tryable"
        /// APIs). The callee itself is deliberately NOT resolved via ResolveExpr
        /// (that would fail as an ordinary identifier or
        /// wrongly set a ResolvedRef.Native/Extern) - instead
        /// its own ResolvedRef.TryableNative is attached directly to the
        /// TryCallExpr node itself here, which the compiler queries.</summary>
        private void ResolveTryCallExpr(TryCallExpr tc)
        {
            // `try obj.Take...(...)` (SPEC 2.2): the ownership method only moves if the caller is the owner
            if (tc.Call is CallExpr takeCall && takeCall.Callee is MemberExpr takeMember && takeMember.Name is "TakeLocal" or "TakeUpwards" or "TakeGlobal" or "TakeTo")
            {
                int baseArgs = takeMember.Name == "TakeTo" ? 1 : 0;
                if (takeCall.Args.Count != baseArgs && takeCall.Args.Count != baseArgs + 1)
                    throw new ResolverException($"'try {takeMember.Name}(...)' expects {(baseArgs == 1 ? "the target object and optionally a Takes mode" : "optionally a Takes mode")}", tc.Line);
                ResolveExpr(takeMember.Target);
                foreach (var a in takeCall.Args) ResolveExprAllowTake(a);
                _refs[tc] = new ResolvedRef.TryTake(takeMember.Name);
                return;
            }

            if (tc.Call is not CallExpr innerCall || innerCall.Callee is not IdentifierExpr calleeIdent)
                throw new ResolverException(
                    "'try' before a call expects a direct call of a registered function " +
                    "(no method call except obj.TakeLocal/TakeUpwards/TakeGlobal/TakeTo, no lambda call)", tc.Line);

            if (!_tryableNativeNames.Contains(calleeIdent.Name))
                throw new ResolverException(
                    _nativeNames.Contains(calleeIdent.Name) || _externs.ContainsKey(calleeIdent.Name)
                        ? $"'{calleeIdent.Name}' is not a function registered as 'tryable' - only APIs that can be " +
                          "called with 'try' may be called like this, ordinary native/extern functions may not."
                        : $"'{calleeIdent.Name}' is not a known native function registered as 'tryable'.",
                    tc.Line);

            foreach (var a in innerCall.Args) ResolveExprAllowTake(a);
            _refs[tc] = new ResolvedRef.TryableNative(calleeIdent.Name);
        }

        private void ResolveAssignTarget(Expr target)
        {
            switch (target)
            {
                case IdentifierExpr id:
                    if (IsCapturedVariable(id.Name))
                        throw new ResolverException(
                            $"'{id.Name}' is a COPY of the outer variable inside the lambda (capture) and cannot be assigned there " +
                            "(declare a new local variable with a different name)", id.Line);
                    if (IsReadonlyVariable(id.Name))
                        throw new ResolverException(
                            $"'{id.Name}' is 'readonly' and cannot be assigned after its declaration", id.Line);
                    _refs[id] = ResolveIdentifierRef(id.Name, id.Line);
                    break;
                case MemberExpr me:
                    // readonly fields may ONLY be assigned as 'this.field = ...' inside
                    // a constructor OF THE DECLARING class
                    // - that is purely statically checkable (target is lexically
                    // 'this', current class is known). For a dynamic
                    // target ('obj.field = ...', 'obj' any expression) the
                    // resolver, lacking a static type system, can NOT know which
                    // class is meant - a deliberate limit of this development stage,
                    // see FieldDecl documentation.
                    if (me.Target is ThisExpr && _currentClass != null && IsReadonlyField(_currentClass, me.Name) && !_inConstructor)
                        throw new ResolverException(
                            $"'{_currentClass.Name}.{me.Name}' is 'readonly' and can only be assigned inside a " +
                            "constructor of the declaring class", me.Line);
                    // 'ClassName.Member = ...' (SPEC "Static members") -
                    // the same detection as when reading (see ResolveExpr/
                    // TryResolveStaticMemberAccess), needed separately here because
                    // an assignment target does NOT go through the normal ResolveExpr
                    // case (otherwise 'ClassName' would be treated as an unknown
                    // identifier instead of as a class name).
                    string? staticAssignClassName = TryResolveStaticMemberAccess(me);
                    if (staticAssignClassName != null)
                    {
                        _refs[me] = new ResolvedRef.StaticMember(staticAssignClassName);
                        break;
                    }
                    // 'this.StaticMember = ...' - see the same reasoning in the
                    // read case above (ResolveExpr, case MemberExpr).
                    if (me.Target is ThisExpr && _currentClass != null)
                    {
                        var thisAssignMember = FindMemberInClassChain(_currentClass, me.Name);
                        if (thisAssignMember is { IsStatic: true })
                        {
                            _refs[me] = new ResolvedRef.StaticMember(thisAssignMember.Value.ClassName);
                            break;
                        }
                    }
                    ResolveExpr(me.Target);
                    break;
                case IndexExpr ix:
                    ResolveExpr(ix.Target);
                    ResolveExpr(ix.Index);
                    break;
                case UnaryExpr { Op: UnaryOp.Dereference } deref:
                    if (_unsafeDepth == 0)
                        throw new ResolverException(
                            "'*' as an assignment target is only valid inside an 'unsafe' block", deref.Line);
                    ResolveExpr(deref.Operand);
                    break;
                default:
                    throw new ResolverException("Invalid assignment target", target.Line);
            }
        }

        /// <summary>Is `fieldName` declared as `readonly` in `cd` (ONLY this class
        /// itself, no base-class chain - deliberate limit)?</summary>
        private static bool IsReadonlyField(ClassDecl cd, string fieldName) =>
            cd.Members.Any(m => m is FieldDecl { IsReadonly: true } fd && fd.Name == fieldName);

        /// <summary>Lambdas see only their own scope + global (SPEC 4.2): the
        /// new scope is deliberately attached directly to the global scope, not to
        /// the currently enclosing one - this way the restricted
        /// visibility "works" simply through the normal depth counting during resolution,
        /// without any special case there. 'on obj', by contrast, is resolved BEFORE the scope switch,
        /// because it is evaluated in the enclosing context (where
        /// the lambda itself is defined), not inside its body.</summary>
        /// <summary>`fire { ... }`/`fire taking X { ... }`/`fire with actorA { ... }`
        /// (see Ast.FireStmt documentation): every `TakingCaptures` source as well as
        /// `WithSource` (if present) are resolved quite normally in the CALLING
        /// scope (the respective variable must already be
        /// declared there - or, with the `fire MethodA(...)` call form,
        /// be a synthetic `this`/an argument expression). The body,
        /// by contrast, gets a COMPLETELY ISOLATED scope (`ResolverScope(null,
        /// isGlobal: true)` - deliberately NOT `_globalScope` as with lambdas, see
        /// FireStmt documentation: the
        /// fire block runs on a VM instance of its own with its own,
        /// fresh global scope). `return` makes
        /// no sense inside a fire block (no return values,
        /// SPEC) - `_functionDepth` is therefore reset to 0 for the duration of the
        /// body resolution, so that a `return` in it is rejected like
        /// "outside of a function".
        ///
        /// IMPORTANT: the order of slot assignment here is the "truth" with which
        /// Compiler.CompileFireStmt and Runtime.FireRuntime.FireVmTaking
        /// MUST agree - FIRST all main-program globals (as
        /// READONLY shadows, at THE SAME slots as in the main program - see
        /// below), THEN all TakingCaptures (in list order), then
        /// with. This enables the read-only snapshot provided for in
        /// docs/THREADING_DESIGN.md section 8: the fire thread gets at
        /// its start an OWN, isolated copy of ALL main-program
        /// globals (see VM.OpCode.Fire) - visible for reading under the same
        /// name as in the main program, but NOT writable (an
        /// assignment would only change the local copy, never the original
        /// - that would be silently wrong, therefore rejected hard here
        /// instead of allowed).</summary>
        private void ResolveFireStmt(FireStmt fs)
        {
            _sawFire = true;
            foreach (var capture in fs.TakingCaptures)
                ResolveExpr(capture.Source);
            if (fs.WithSource != null)
                ResolveExpr(fs.WithSource);

            var saved = _current;
            _current = new ResolverScope(null, isGlobal: true);

            // Shadow entries for ALL main-program globals, at their
            // ORIGINAL slots (0..GlobalSlotCount-1) - the runtime snapshot
            // (see VM.OpCode.Fire) copies exactly these slots 1:1 into the
            // fresh global scope of the fire thread, therefore the
            // slot NUMBERS must be taken over unchanged here, not be
            // assigned anew. Can be switched off via '#noshadow' (see
            // Ast.NoShadowDirective documentation) - then everything behaves as before the
            // introduction of the snapshot: taking/with get their slots
            // from 0 again.
            if (!_noShadowGlobals)
                foreach (var (name, slot) in _globalScope.Slots)
                {
                    // A fire thread may change globals (docs/THREADING_DESIGN.md section 7): single assignments run as a section that
                    // the main program grants at `sync globals` - therefore no write protection any more here.
                    _current.Slots[name] = slot;
                }

            // taking/with - get NEW, OWN slots from here on (directly
            // after the main-program globals, or from 0 with active
            // '#noshadow'). If a capture collides BY NAME with a
            // main-program global, it SUPPRESSES its shadow entry
            // (normal lexical shadowing - the explicit, own
            // capture is then meant in the fire block body, not the
            // main-program global of the same name) and is itself quite normally
            // writable as before, NOT readonly.
            int nextCaptureSlot = _noShadowGlobals ? 0 : _globalScope.Slots.Count;
            if (!_noShadowGlobals && _finalGlobalCount is int finalCount)
            {
                // the slots of the globals that are declared later belong to the main program: keep them free in the body (placeholders)
                for (; nextCaptureSlot < finalCount; nextCaptureSlot++) _current.Slots["\u0001global:" + nextCaptureSlot] = nextCaptureSlot;
            }
            var capturesSeen = new HashSet<string>();
            foreach (var capture in fs.TakingCaptures)
            {
                if (!capturesSeen.Add(capture.VarName))
                    throw new ResolverException($"'{capture.VarName}' was already captured in this 'fire'", fs.Line);
                // A capture with the name of a global replaces its shadow; the old entry stays under a hidden key so that the slot count (the
                // slot of the next variable of the body) stays right.
                if (_current.Slots.TryGetValue(capture.VarName, out int shadowed)) _current.Slots["\u0001shadow:" + capture.VarName] = shadowed;
                _current.Slots[capture.VarName] = nextCaptureSlot++;
                _current.ReadonlySlots.Remove(capture.VarName);
            }
            if (fs.WithVarName != null)
            {
                if (!capturesSeen.Add(fs.WithVarName))
                    throw new ResolverException($"'{fs.WithVarName}' was already captured in this 'fire'", fs.Line);
                if (_current.Slots.TryGetValue(fs.WithVarName, out int shadowedWith)) _current.Slots["\u0001shadow:" + fs.WithVarName] = shadowedWith;
                _current.Slots[fs.WithVarName] = nextCaptureSlot++;
                _current.ReadonlySlots.Remove(fs.WithVarName);
            }

            int savedLoopDepth = _loopDepth;
            int savedTryDepth = _tryDepth;
            int savedFunctionDepth = _functionDepth;
            bool savedInConstructor = _inConstructor;
            _loopDepth = 0;
            _tryDepth = 0;
            _functionDepth = 0;
            _inConstructor = false;

            foreach (var s in fs.Body.Statements) ResolveStmt(s);

            _loopDepth = savedLoopDepth;
            _tryDepth = savedTryDepth;
            _functionDepth = savedFunctionDepth;
            _inConstructor = savedInConstructor;
            _current = saved;
        }

        /// <summary>Common body resolution for `catch threads(...)`/`catch
        /// terminate(v)` (see Ast.CatchThreadsDecl/CatchTerminateDecl documentation)
        /// - exactly the same isolation as ResolveFireStmt (own, empty
        /// global scope, `return` forbidden), for the same reason: the
        /// handler body later runs as an independent proto of its own,
        /// nested into ANY VM instance (see VM.
        /// HandleDeliveredThreadException/RunTerminateHandlerIfAny), not in the
        /// lexical context of its declaration.</summary>
        /// <summary>Common body resolution for `catch threads(...)`/`catch
        /// terminate(v)` (see Ast.CatchThreadsDecl/CatchTerminateDecl documentation).
        ///
        /// IMPORTANT, unlike with ResolveFireStmt: the bound parameter
        /// (`e`/`v`) must be resolved LOCALLY (`ResolvedRef.Local`), NOT
        /// globally (`ResolvedRef.Global`) - the handler does NOT run on an
        /// own, fresh VM instance with its own global scope (like a
        /// fire block), but NESTED INSIDE the respective delivering
        /// VM instance (see VM.HandleDeliveredThreadException/
        /// RunTerminateHandlerIfAny), which uses ITS OWN `_globalScope` field
        /// continues for the main program. A global resolution
        /// of the parameter would therefore collide with its SLOT 0 (e.g.
        /// with the first `var` declaration of the main program) - the
        /// parameter therefore needs the same scope structure as a lambda
        /// (`new ResolverScope(_globalScope)`, NOT `isGlobal: true`): sees
        /// the real globals for NAME lookup, but is itself registered as a
        /// LOCAL variable of depth 0 - exactly what
        /// `handlerScope.DefineSlot(...)` in the VM fills at runtime.</summary>
        private void ResolveGlobalHandlerBody(string? varName, Stmt.BlockStmt body)
        {
            var saved = _current;
            _current = new ResolverScope(_globalScope);

            if (varName != null) Define(varName, body.Line);

            int savedLoopDepth = _loopDepth;
            int savedTryDepth = _tryDepth;
            int savedFunctionDepth = _functionDepth;
            bool savedInConstructor = _inConstructor;
            _loopDepth = 0;
            _tryDepth = 0;
            _functionDepth = 0;
            _inConstructor = false;

            foreach (var s in body.Statements) ResolveStmt(s);

            _loopDepth = savedLoopDepth;
            _tryDepth = savedTryDepth;
            _functionDepth = savedFunctionDepth;
            _inConstructor = savedInConstructor;
            _current = saved;
        }

        /// <summary>Lambda captures (SPEC 4.2): every name that the body uses and that is a local variable (or a
        /// parameter) in the ENCLOSING code - not global, not a parameter of the lambda - is copied as a VALUE when the lambda is created and lies in the lambda scope
        /// as a slot directly behind the parameters. The names are collected beforehand over the whole body (also in nested lambdas),
        /// because the slot numbers must be fixed before the body is resolved; a name captured too many times (newly declared in the body)
        /// costs only a copy, see Define.</summary>
        private void DefineCaptures(LambdaExpr lambda, ResolverScope enclosing)
        {
            var names = new List<string>();
            AstNames.Collect(lambda.Body, names, new HashSet<string>());
            var paramNames = new HashSet<string>();
            foreach (var p in lambda.Params) paramNames.Add(p.Name);

            List<IdentifierExpr>? captures = null;
            foreach (var name in names)
            {
                if (paramNames.Contains(name)) continue;
                int depth = 0;
                ResolverScope? found = null;
                for (var scope = enclosing; scope != null && !scope.IsGlobal; scope = scope.Parent, depth++)
                {
                    if (scope.Slots.ContainsKey(name)) { found = scope; break; }
                }
                if (found == null) continue;

                found.RequiredUnits.TryGetValue(name, out var requiredUnit);
                var outer = new IdentifierExpr(lambda.Line, name);
                _refs[outer] = new ResolvedRef.Local(depth, found.Slots[name], requiredUnit, found.RefNames.Contains(name));   // (the capture copies the VALUE of a ref parameter)
                Define(name, lambda.Line, requiredUnit: requiredUnit);
                _current.CaptureNames.Add(name);
                (captures ??= new List<IdentifierExpr>()).Add(outer);
            }
            if (captures != null) _refs[lambda] = new ResolvedRef.LambdaCaptures(captures);
        }

        /// <summary>Is the code currently being resolved inside a lambda with `on target`? Then unknown names are members of the bound object.</summary>
        private bool _inBoundLambda;

        private void ResolveLambda(LambdaExpr lambda)
        {
            bool savedBound = _inBoundLambda;
            _inBoundLambda = lambda.OnTarget != null;
            try { ResolveLambdaCore(lambda); }
            finally { _inBoundLambda = savedBound; }
        }

        private void ResolveLambdaCore(LambdaExpr lambda)
        {
            if (lambda.OnTarget != null)
                ResolveExpr(lambda.OnTarget);

            var saved = _current;
            var enclosing = _current;
            _current = new ResolverScope(_globalScope);

            // A lambda defined INSIDE a constructor typically runs
            // only LATER (after construction is complete) -
            // 'this.readonlyField = ...' in it could therefore not be classified safely as
            // "still during construction". For the duration of the
            // lambda body deliberately treat it as if one were NOT in the
            // constructor, regardless of the surrounding context.
            bool savedInConstructor = _inConstructor;
            _inConstructor = false;

            // Resolve default values BEFORE defining the parameters (see
            // ResolveParamDefaults comment) - the scope here is still empty
            // (only global as parent), so automatically isolated from the
            // own parameters.
            ValidateOptionalParamsAreTrailing(lambda.Params, lambda.Line);
            foreach (var p in lambda.Params)
                if (p.DefaultValue != null)
                    ResolveExpr(p.DefaultValue);

            foreach (var p in lambda.Params)
            {
                if (p.Type != null) ValidateTypeRef(p.Type, lambda.Line);
                ResolveArrayRanks(p.ArrayRanks);
                Define(p.Name, lambda.Line, requiredUnit: p.Type?.Unit);
            }

            if (lambda.AutoCapture) DefineCaptures(lambda, enclosing);

            int savedLoopDepth = _loopDepth;
            int savedTryDepth = _tryDepth;
            _loopDepth = 0;
            _tryDepth = 0;

            _functionDepth++;
            foreach (var s in lambda.Body.Statements) ResolveStmt(s);
            _functionDepth--;

            _loopDepth = savedLoopDepth;
            _tryDepth = savedTryDepth;

            _inConstructor = savedInConstructor;
            _current = saved;
        }
    }

    /// <summary>Collects via reflection all identifiers (<see cref="IdentifierExpr"/>) below an AST node, in the order of
    /// first occurrence - independent of which node types exist (new syntax needs no maintenance here). Only for the resolver,
    /// once per lambda.</summary>
    internal static class AstNames
    {
        private static readonly Dictionary<Type, System.Reflection.MemberInfo[]> Members = new();

        public static void Collect(object? node, List<string> names, HashSet<string> seen)
        {
            switch (node)
            {
                case null:
                case string:
                    return;
                case IdentifierExpr id:
                    if (seen.Add(id.Name)) names.Add(id.Name);
                    return;
                case System.Collections.IEnumerable items:
                    foreach (var item in items) Collect(item, names, seen);
                    return;
            }

            var type = node.GetType();
            if (type.IsPrimitive || type.IsEnum) return;
            bool isAst = type.Namespace != null && type.Namespace.StartsWith("fire.Ast", StringComparison.Ordinal);
            bool isTuple = type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple", StringComparison.Ordinal);
            if (!isAst && !isTuple) return;

            System.Reflection.MemberInfo[] members;
            lock (Members)
            {
                if (!Members.TryGetValue(type, out members!))
                {
                    var list = new List<System.Reflection.MemberInfo>();
                    foreach (var p in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        if (p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract") list.Add(p);
                    foreach (var f in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        list.Add(f);
                    Members[type] = members = list.ToArray();
                }
            }
            foreach (var m in members)
            {
                object? value = m is System.Reflection.PropertyInfo pi ? pi.GetValue(node) : ((System.Reflection.FieldInfo)m).GetValue(node);
                Collect(value, names, seen);
            }
        }
    }
}
