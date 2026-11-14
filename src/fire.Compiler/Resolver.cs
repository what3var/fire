using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Resolving;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>What the type checker leaves the compiler for the expression trees: the lambdas that become one, the type of every expression in them.</summary>
    public sealed class ExpressionTrees
    {
        public HashSet<LambdaExpr> Lambdas { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<Expr, string> Types { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>A tree (a LambdaExpression) where a lambda is expected: its compiled lambda goes there (`lambda<T> f = tree`).</summary>
        public HashSet<Expr> ToCompiled { get; } = new(ReferenceEqualityComparer.Instance);
    }

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

        /// <summary>The message without the line.</summary>
        public string RawMessage { get; }

        /// <summary>The code of the message (docs/tools/errors.md).</summary>
        public string Code => ErrorCatalog.CodeOf(ErrorArea.Resolver, RawMessage);

        public IReadOnlyList<ResolverException> Errors { get; }

        /// <summary>The index of the source (Parser.ParseMultiple) the error is in; -1 when unknown. The editor shows only the errors of the open file.</summary>
        public int Source { get; internal set; } = -1;

        public ResolverException(string message, int line)
            : base($"{message} ({line})")
        {
            Line = line;
            RawMessage = message;
            Errors = new[] { this };
        }

        /// <summary>Combines several collected errors (at least one).</summary>
        public ResolverException(IReadOnlyList<ResolverException> errors)
            : base(errors[0].Message)
        {
            Line = errors[0].Line;
            RawMessage = errors[0].RawMessage;
            Errors = errors;
        }
    }

    /// <summary>Result of a resolver run: everything the evaluator (and later
    /// the bytecode compiler) needs in order not to have to search by name any more.</summary>
    public sealed class ResolveResult
    {
        public required IReadOnlyDictionary<Expr, ResolvedRef> References { get; init; }

        /// <summary>Values whose unit the type checker could not prove for the declaration they go to (a number declared without a unit is unitless): the
        /// compiler checks them at runtime (OpCode.CheckUnit with the demand). Filled by TypeChecker.</summary>
        public Dictionary<Expr, string> UnitChecks { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>The type arguments the type checker found for a call of a generic method (`Box.Of(5)`: `int`), as DeclaredType texts ("" for one that
        /// is not known): the compiler gives them to the call (SetTypeArgs), the native generator can specialise the method for them. Filled by TypeChecker.</summary>
        public Dictionary<Expr, string[]> MethodTypeArgs { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>The lambdas the type checker found at a parameter `lambda expression<T>` (docs/ROADMAP_EXPRESSIONS.md) and the types of what is in them: the
        /// compiler builds their trees.</summary>
        public ExpressionTrees Trees { get; } = new();

        /// <summary>Values going into a local variable (declaration or assignment) whose type the type checker could not prove for its declaration
        /// (`int x = someAny`, `Dog d = animal`, `T item = v`): the compiler checks them at runtime (GuardTop/GuardTopType). Filled by TypeChecker.</summary>
        public Dictionary<Expr, (TypeRef Type, string What)> LocalGuards { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Calls of a method or constructor overloaded by type whose overload the type checker could tell from the static types of the
        /// arguments: the overload and what it assumed of each argument (Bytecode.OverloadHint text). The compiler puts it in front of the call
        /// (OpCode.OverloadHint); at run time the arguments are checked against the assumptions and the overload is taken without scoring. Filled by TypeChecker.</summary>
        public Dictionary<Expr, string> OverloadHints { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Parameters of the program declared `int`/`float` without `defined` that every call gives a number (ROADMAP 13.9, the derivation): the
        /// compiler checks them like `defined` ones - natively they are C numbers. Filled by TypeChecker (TypeChecker.DeriveDefined).</summary>
        public HashSet<LambdaParam> DerivedDefined { get; } = new(ReferenceEqualityComparer.Instance);
        /// <summary>The bodies of the methods of the program declared `int`/`float` whose returns are certainly numbers (the derivation): checked like a
        /// `defined` return type.</summary>
        public HashSet<Stmt.BlockStmt> DerivedReturns { get; } = new(ReferenceEqualityComparer.Instance);
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
            "bool", "int", "float", "char", "string", "class", "undefined", "lambda", "any", "word",
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
        /// <summary>An unqualified name that more than one `#using` namespace has (TypeRef.AmbiguousCandidates) must be written in full. `ownNamespace`:
        /// the namespace of the code that writes the name (its own namespace comes first and is not ambiguous).</summary>
        private void CheckAmbiguous(TypeRef tr, int line, Func<string, bool> known, string? ownNamespace)
        {
            int typeArgs = tr.TypeArgs?.Count ?? tr.TypeArgCount;
            var matches = tr.AmbiguousCandidates(n => known(n) || typeArgs > 0 && known(GenericClassNames.Mangle(n, typeArgs)), ownNamespace);
            if (matches.Count > 1)
                throw new ResolverException($"'{tr.BaseName}' is ambiguous: {string.Join(", ", matches)} (more than one '#using' or imported library brings it); write the full name", line);
        }

        /// <summary>The namespace a class is declared in (`Geo.Shapes.Circle` -> `Geo.Shapes`), null for a class outside every namespace.</summary>
        private static string? NamespaceOf(string? className)
        {
            if (className == null) return null;
            string plain = GenericClassNames.PlainName(className);
            int dot = plain.LastIndexOf('.');
            return dot < 0 ? null : plain.Substring(0, dot);
        }

        private string ResolveTypeRef(TypeRef tr) =>
            tr.TypeArgs is { Count: > 0 } args ? GenericClassNames.ResolveNewTarget(tr, args.Count, IsKnownClassName) : tr.ResolveBaseName(IsKnownClassName);

        private readonly Dictionary<string, InterfaceDecl> _interfaces = new();
        private readonly Dictionary<string, ExternDecl> _externs = new();
        private readonly Dictionary<string, Dictionary<string, long>> _enums = new();
        /// <summary>The run-time type of each enum (its values carry it, see Values.EnumType).</summary>
        private readonly Dictionary<string, fire.Values.EnumType> _enumTypes = new();
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
            if (error.Source < 0) error.Source = _currentSource;
            if (_errorMessages.Add(error.Source + ":" + error.Message))
                _errors.Add(error);
        }

        /// <summary>The source of the statement that is being resolved (for ResolverException.Source).</summary>
        private int _currentSource = -1;

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
            {
                resolver._currentSource = stmt.Source;
                resolver.ResolveStmt(stmt);
            }
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
                if (ed.InlineC != null) CheckInlineCTypes(ed);
            }
        }

        /// <summary>The types an `inline c` function can take and give (ROADMAP 13.2): `int` (FireInt; `int[8|16|32|64]` the C type of that width), `float` (Real),
        /// `bool`, `char` (uint32_t), `string` (UTF-8, `const char*`), `byte name[]` (the bytes: `uint8_t* name` and `uint32_t name_length`), `any` (the Value),
        /// pointers (`void*`); a result also `undefined` (none).</summary>
        private void CheckInlineCTypes(ExternDecl ed)
        {
            foreach (var p in ed.Params)
            {
                if (p.ByRef) { AddError(new ResolverException($"The parameter '{p.Name}' of the inline C function '{ed.Name}' cannot be 'ref' (take a pointer)", ed.Line)); continue; }
                if (p.Type == null) { AddError(new ResolverException($"The parameter '{p.Name}' of the inline C function '{ed.Name}' needs a type (int, float, bool, char, string, byte[], any or a pointer)", ed.Line)); continue; }
                int rank = p.ArrayRanks.Count + p.Type.ArrayRank;
                bool bytes = p.Type is { BaseName: "int", BitWidth: 8, PointerDepth: 0 } && rank == 1;   // (`byte` is `int[8]`)
                bool scalar = rank == 0 && (p.Type.PointerDepth > 0 || p.Type.BaseName is "int" or "float" or "bool" or "char" or "string" or "any");
                if (!bytes && !scalar)
                    AddError(new ResolverException($"The parameter '{p.Name}' of the inline C function '{ed.Name}' has a type C cannot take (int, float, bool, char, string, byte[], any or a pointer)", ed.Line));
            }
            if (ed.ReturnType is { } r && !(r.ArrayRank == 0 && (r.PointerDepth > 0 || r.BaseName is "int" or "float" or "bool" or "char" or "string" or "any" or "undefined")))
                AddError(new ResolverException($"The inline C function '{ed.Name}' has a result type C cannot give (int, float, bool, char, string, any, a pointer or undefined)", ed.Line));
        }

        /// <summary>Collects all `enum` declarations beforehand (allows forward
        /// references as with classes/externs) and in the process directly computes
        /// the actual int values of each member: an explicitly given
        /// value MUST be an int literal (real compile-time constant
        /// evaluation of arbitrary expressions does not exist in this language),
        /// otherwise auto-increment from the predecessor + 1 (0 for the first member).</summary>
        /// <summary>The run-time type of an enum declaration (Values.EnumType): its members with their values - an int literal, or the one before + 1 (a faulty
        /// member is reported by CollectEnums; here it just counts on).</summary>
        public static fire.Values.EnumType EnumTypeOf(EnumDecl ed)
        {
            var members = new List<(string, long)>();
            var seen = new HashSet<string>();
            long next = 0;
            foreach (var m in ed.Members)
            {
                long value = m.ValueExpr is LiteralExpr { Value.Kind: ValueKind.Int } lit ? lit.Value.AsInt() : next;
                next = value + 1;
                if (seen.Add(m.Name)) members.Add((m.Name, value));
            }
            return fire.Values.EnumType.Intern(ed.Name, ed.IsFlags, members);
        }

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
                _enumTypes[ed.Name] = EnumTypeOf(ed);
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
                _currentSource = stmt.Source;
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
                    try { CheckAmbiguous(baseRef, cd.Line, n => n == "Exception" || _classes.ContainsKey(n) || _interfaces.ContainsKey(n), NamespaceOf(cd.Name)); }
                    catch (ResolverException ambiguous) { AddError(ambiguous); continue; }
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

            // `interface IConnection : IO.IStream, IOther`: every name is a known interface (not a class), and no interface extends itself (over any chain)
            foreach (var id in _interfaces.Values)
            {
                foreach (var baseRef in id.BaseRefs ?? Array.Empty<TypeRef>())
                {
                    try { CheckAmbiguous(baseRef, id.Line, n => _classes.ContainsKey(n) || _interfaces.ContainsKey(n), NamespaceOf(id.Name)); }
                    catch (ResolverException ambiguous) { AddError(ambiguous); continue; }
                    string n = ResolveBaseRef(baseRef);
                    if (!_interfaces.ContainsKey(n))
                        AddError(new ResolverException(
                            $"'{baseRef.BaseName}' of interface '{id.Name}' is not a known interface (an interface can only extend interfaces)", id.Line));
                }
                if (InterfaceCycle(id) is { } cycle)
                    AddError(new ResolverException($"Interface '{id.Name}' extends itself ({string.Join(" -> ", cycle)})", id.Line));
            }

            foreach (var cd in _classes.Values)
            {
                // (the interfaces it names and every interface they extend - each once)
                var done = new HashSet<string>();
                foreach (var baseRef in cd.BaseRefs ?? Array.Empty<TypeRef>())
                {
                    string n = ResolveBaseRef(baseRef);
                    if (!_interfaces.ContainsKey(n)) continue;
                    foreach (var iface in InterfaceClosure(n))
                        if (done.Add(iface.Name)) Guard(() => ValidateImplementsInterface(cd, iface));
                }
                // an interface of a base class: a method the class declares again must stay public (the class is of the interface too)
                var seen = new HashSet<ClassDecl> { cd };
                for (var b = GetBaseClassDecl(cd); b != null && seen.Add(b); b = GetBaseClassDecl(b))
                    foreach (var baseRef in b.BaseRefs ?? Array.Empty<TypeRef>())
                        if (_interfaces.ContainsKey(ResolveBaseRef(baseRef)))
                            foreach (var inherited in InterfaceClosure(ResolveBaseRef(baseRef)))
                                if (done.Add(inherited.Name)) Guard(() => ValidateImplementsInterface(cd, inherited, onlyOwn: true));
            }
        }

        /// <summary>The interface `name` and every interface it extends, directly or over others (each once, the interface itself first; unknown names and
        /// cycles are left out - they are reported on their own).</summary>
        private List<InterfaceDecl> InterfaceClosure(string name)
        {
            var result = new List<InterfaceDecl>();
            var seen = new HashSet<string>();
            var pending = new Stack<string>();
            pending.Push(name);
            while (pending.Count > 0)
            {
                string n = pending.Pop();
                if (!seen.Add(n) || !_interfaces.TryGetValue(n, out var id)) continue;
                result.Add(id);
                var bases = id.BaseRefs ?? Array.Empty<TypeRef>();
                for (int i = bases.Count - 1; i >= 0; i--) pending.Push(ResolveBaseRef(bases[i]));
            }
            return result;
        }

        /// <summary>The chain over which `id` extends itself (`IA -> IB -> IA`), null when it does not.</summary>
        private List<string>? InterfaceCycle(InterfaceDecl id)
        {
            var path = new List<string> { id.Name };
            var visited = new HashSet<string>();
            bool Walk(string name)
            {
                if (!_interfaces.TryGetValue(name, out var decl)) return false;
                foreach (var baseRef in decl.BaseRefs ?? Array.Empty<TypeRef>())
                {
                    string n = ResolveBaseRef(baseRef);
                    path.Add(n);
                    if (n == id.Name) return true;
                    if (visited.Add(n) && Walk(n)) return true;
                    path.RemoveAt(path.Count - 1);
                }
                return false;
            }
            return Walk(id.Name) ? path : null;
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
            var classList = new HashSet<ClassDecl>();

            for (var cd = startClass; cd != null; cd = GetBaseClassDecl(cd))
            {
                if (!classList.Add(cd))
                {
                    var message = "Circular inheritance detected in class '" + cd.Name + "'";
                    if (!_errors.Any(e => e.Line == cd.Line && e.RawMessage == message))
                        _errors.Add(new ResolverException(message, cd.Line));
                    break; // Avoid infinite loops in case of circular inheritance
                }

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
        private void ValidateImplementsInterface(ClassDecl cd, InterfaceDecl iface, bool onlyOwn = false)
        {
            foreach (var m in iface.Methods)
            {
                if (onlyOwn)
                {
                    // (the interface of a base class: the base class is checked for it itself - here only a method this class declares)
                    if (ImplementingMethod(cd, m.Name, m.Params.Count) is { Access: not AccessModifier.Public } own && cd.Members.Contains(own))
                        throw NotPublic(cd, iface, own);
                    continue;
                }
                if (!ClassHasMethod(cd, m.Name, m.Params.Count))
                    throw new ResolverException(
                        $"Class '{cd.Name}' does not implement interface '{iface.Name}' completely " +
                        $"(missing method '{m.Name}' with {m.Params.Count} parameter(s))", cd.Line);
                // a member of an interface is public: the method that implements it is too (so a call through the interface needs no access check at run time)
                if (ImplementingMethod(cd, m.Name, m.Params.Count) is { Access: not AccessModifier.Public } hidden)
                    throw NotPublic(cd, iface, hidden);
            }
            foreach (var p in iface.PropertySigs)
            {
                var own = ImplementingProperty(cd, p.Name);
                if (onlyOwn)
                {
                    if (own is { Access: not AccessModifier.Public } && cd.Members.Contains(own)) throw PropertyNotPublic(cd, iface, own);
                    continue;
                }
                string wanted = p.HasGet && p.HasSet ? "get; set;" : p.HasGet ? "get;" : "set;";
                if (own == null)
                {
                    // a field of that name is no property: the interface promises get_/set_, which a field does not have (see SPEC 8.5)
                    if (FieldInClassChain(cd, p.Name))
                        throw new ResolverException(
                            $"'{p.Name}' of class '{cd.Name}' is a field - interface '{iface.Name}' wants a property '{p.Name}' with '{wanted}'", cd.Line);
                    throw new ResolverException(
                        $"Class '{cd.Name}' does not implement interface '{iface.Name}' completely (missing property '{p.Name}' with '{wanted}')", cd.Line);
                }
                bool canGet = PropertyAccessorInChain(cd, p.Name, getter: true), canSet = PropertyAccessorInChain(cd, p.Name, getter: false);
                if (p.HasGet && !canGet || p.HasSet && !canSet)
                    throw new ResolverException(
                        $"Class '{cd.Name}' does not implement interface '{iface.Name}' completely (missing property '{p.Name}' with '{wanted}')", own.Line);
                if (own.Access != AccessModifier.Public) throw PropertyNotPublic(cd, iface, own);
                if (PropertyTypeConflict(p.Type, own.Type) is { } conflict)
                    throw new ResolverException(
                        $"Property '{p.Name}' of class '{cd.Name}' is {conflict.Have}, interface '{iface.Name}' declares it as {conflict.Want}", own.Line);
            }
        }

        private static ResolverException PropertyNotPublic(ClassDecl cd, InterfaceDecl iface, PropertyDecl pd) =>
            new($"Property '{pd.Name}' of class '{cd.Name}' implements interface '{iface.Name}' and must be public, not " +
                $"{(pd.Access == AccessModifier.Private ? "private" : "protected")} (a member of an interface is public)", pd.Line);

        /// <summary>The property `name` an object of `cd` has: the nearest class of the chain that declares one (not a static one).</summary>
        private PropertyDecl? ImplementingProperty(ClassDecl cd, string name)
        {
            var seen = new HashSet<ClassDecl>();
            for (var c = cd; c != null && seen.Add(c); c = GetBaseClassDecl(c))
                if (c.Members.OfType<PropertyDecl>().FirstOrDefault(pd => pd.Name == name && !pd.IsStatic) is { } found) return found;
            return null;
        }

        /// <summary>Whether some class of the chain gives the property `name` a getter (a setter) - an override may declare only one of them.</summary>
        private bool PropertyAccessorInChain(ClassDecl cd, string name, bool getter)
        {
            var seen = new HashSet<ClassDecl>();
            for (var c = cd; c != null && seen.Add(c); c = GetBaseClassDecl(c))
                if (c.Members.OfType<PropertyDecl>().Any(pd => pd.Name == name && !pd.IsStatic && (getter ? pd.Getter : pd.Setter) != null)) return true;
            return false;
        }

        private bool FieldInClassChain(ClassDecl cd, string name)
        {
            var seen = new HashSet<ClassDecl>();
            for (var c = cd; c != null && seen.Add(c); c = GetBaseClassDecl(c))
                if (c.Members.OfType<FieldDecl>().Any(fd => fd.Name == name && !fd.IsStatic)) return true;
            return false;
        }

        private static readonly HashSet<string> ValueTypeNames = new() { "int", "float", "string", "bool", "char", "byte", "word" };

        /// <summary>The type of an implementing property against the one of the interface: only what is surely different is a conflict - two different value
        /// types (`string` for an `int`), or two units of a different text (`int : s` for `int : ms`; a unit against none). Class types, `any`, type
        /// parameters and untyped ones pass (the type checker looks at the values).</summary>
        private static (string Have, string Want)? PropertyTypeConflict(TypeRef? want, TypeRef? have)
        {
            if (want == null || have == null || want.IsInferred || have.IsInferred) return null;
            string Text(TypeRef t) => "'" + t.BaseName + (t.ArrayRank > 0 ? string.Concat(Enumerable.Repeat("[]", t.ArrayRank)) : "") + (t.Unit != null ? " : " + t.Unit : "") + "'";
            bool valueTypes = ValueTypeNames.Contains(want.BaseName) && ValueTypeNames.Contains(have.BaseName);
            if (valueTypes && (want.BaseName != have.BaseName || want.ArrayRank != have.ArrayRank)) return (Text(have), Text(want));
            if (valueTypes && want.BaseName is "int" or "float" && (want.Unit ?? "") != (have.Unit ?? "")) return (Text(have), Text(want));
            return null;
        }

        private static ResolverException NotPublic(ClassDecl cd, InterfaceDecl iface, MethodDecl md) =>
            new($"Method '{md.Name}' of class '{cd.Name}' implements interface '{iface.Name}' and must be public, not " +
                $"{(md.Access == AccessModifier.Private ? "private" : "protected")} (a member of an interface is public)", md.Line);

        /// <summary>The method a call of `name` with `arity` arguments finds on an object of `cd`: the nearest class of the chain that declares one decides - a public one
        /// there if it has one (an overload), otherwise its first (private/protected: it hides a public one of a base class).</summary>
        private MethodDecl? ImplementingMethod(ClassDecl cd, string name, int arity)
        {
            var seen = new HashSet<ClassDecl>();
            for (var c = cd; c != null && seen.Add(c); c = GetBaseClassDecl(c))
            {
                var found = c.Members.OfType<MethodDecl>().Where(md => md.Name == name && md.Params.Count == arity && !md.IsStatic).ToList();
                if (found.Count > 0) return found.FirstOrDefault(md => md.Access == AccessModifier.Public) ?? found[0];
            }
            return null;
        }

        /// <summary>The parameter types of a method as overloading by type compares them (see Bytecode.Overloads): "" for an untyped parameter, `any`, a
        /// `ref` parameter or a type parameter (they take everything), otherwise the written type.</summary>
        private static string[] SignatureKeys(IReadOnlyList<LambdaParam> parms, IReadOnlyCollection<string> typeParamNames) =>
            parms.Select(p =>
            {
                var t = p.Type;
                if (p.ByRef || t == null || t.IsInferred || t.BaseName == "any" || typeParamNames.Contains(t.BaseName) && t.TypeArgs == null && t.ArrayRank == 0 && p.ArrayRanks.Count == 0) return "";
                if (p.ArrayRanks.Count > 0 || t.ArrayRank > 0) return "array";
                if (t.LambdaSignature is { IsExpression: true }) return "LambdaExpression";   // (a tree: an overload next to `lambda<T>`)
                if (t.LambdaSignature != null || t.BaseName == "lambda") return "lambda";
                return t.BaseName == "byte" ? "int" : (t.Namespaces is { Count: > 0 } ns ? string.Join(".", ns) + "." : "") + t.BaseName;
            }).ToArray();

        /// <summary>Two methods of one class with these parameter types could not be told apart by a call.</summary>
        private static bool SameSignature(string[] a, string[] b) => a.Length == b.Length && a.Zip(b).All(p => p.First == p.Second);

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

        /// <summary>`ClassName.Member`: the class (or one of its base classes) has to declare a static field, property or method of that name - a misspelled one, or one that needs an object
        /// (`DateTime.Now()` for a property, `Box.Grow` for an instance method) is an error of the program here, not a failure when the line is reached. A nested class or an enum of that name
        /// is no member and is not asked.</summary>
        private void CheckStaticMemberExists(string className, MemberExpr me)
        {
            if (me.Target is SelfClassExpr || !_classes.TryGetValue(className, out var classDecl)) return;
            if (_classes.ContainsKey(className + "." + me.Name) || _enums.ContainsKey(className + "." + me.Name)) return;
            var found = FindMemberInClassChain(classDecl, me.Name);
            if (found == null)
                throw new ResolverException($"Class '{GenericClassNames.PlainName(className)}' has no static member '{me.Name}'", me.Line);
            if (!found.Value.IsStatic)
                throw new ResolverException($"'{GenericClassNames.PlainName(className)}.{me.Name}' is an instance member - it needs an object, not the class name", me.Line);
        }

        // -----------------------------------------------------------
        // Warnings of the developer inside classes (`warn` in a class, a method, a constructor, a property): reported where the class or the member is used
        // -----------------------------------------------------------
        private Dictionary<string, List<DeclaredWarning>>? _declaredByClass;
        private bool _declaredLoaded;
        private readonly Dictionary<Expr, int> _calleeArity = new(ReferenceEqualityComparer.Instance);

        private bool HasDeclaredWarnings
        {
            get
            {
                if (!_declaredLoaded)
                {
                    _declaredLoaded = true;
                    var list = DiagnosticCollector.Current?.Declared;
                    if (list is { Count: > 0 }) _declaredByClass = list.GroupBy(w => w.Class).ToDictionary(g => g.Key, g => g.ToList());
                }
                return _declaredByClass != null;
            }
        }

        /// <summary>A use of a class or member that has a declared warning: the warning goes to the place of the use (once per place). Inside the declaring class itself nothing is reported -
        /// the class uses its own obsolete parts without being told.</summary>
        private void ReportUse(DeclaredWarning warning, int line)
        {
            if (_currentClass != null && GenericClassNames.PlainName(_currentClass.Name) == warning.Class) return;
            string where = warning.Name != null ? warning.Class + "." + warning.Name : warning.Class;
            DiagnosticCollector.Current?.Add(new Diagnostic(line, $"{warning.Text} ({where})", DiagnosticSeverity.Warning, DiagnosticCollector.WarningCode(warning.Id), null));
        }

        /// <summary>`new Class(args)`: the warnings of the class and of its constructors that take that many arguments.</summary>
        private void WarnForNew(string className, int argumentCount, int line)
        {
            if (!HasDeclaredWarnings || !_declaredByClass!.TryGetValue(GenericClassNames.PlainName(className), out var list)) return;
            foreach (var w in list)
                if (w.Target == WarnTarget.Class || (w.Target == WarnTarget.Constructor && argumentCount >= w.MinArity && argumentCount <= w.MaxArity)) ReportUse(w, line);
        }

        /// <summary>`Class.Member` (static access): the warnings of the class itself.</summary>
        private void WarnForClassUse(string className, int line)
        {
            if (!HasDeclaredWarnings || !_declaredByClass!.TryGetValue(GenericClassNames.PlainName(className), out var list)) return;
            foreach (var w in list) if (w.Target == WarnTarget.Class) ReportUse(w, line);
        }

        /// <summary>A method call (`arguments` = the number of arguments) or a property/field access (`arguments` null) of that name: the warnings of such a member in `classes` (null: every class
        /// that has one - the compiler does not know the class of the object).</summary>
        private void WarnForMember(IEnumerable<string>? classes, string name, int? arguments, int line)
        {
            if (!HasDeclaredWarnings) return;
            foreach (var (className, list) in _declaredByClass!)
            {
                if (classes != null && !classes.Contains(className)) continue;
                foreach (var w in list)
                {
                    if (w.Name != name) continue;
                    bool applies = arguments is { } n ? w.Target == WarnTarget.Method && n >= w.MinArity && n <= w.MaxArity : w.Target == WarnTarget.Member;
                    if (applies) ReportUse(w, line);
                }
            }
        }

        /// <summary>The class and its base classes (plain names), for the places that know the class of an object.</summary>
        private List<string> ClassChainNames(string className)
        {
            var names = new List<string>();
            _classes.TryGetValue(className, out var start);
            for (var cd = start; cd != null; cd = GetBaseClassDecl(cd)) names.Add(GenericClassNames.PlainName(cd.Name));
            if (names.Count == 0) names.Add(GenericClassNames.PlainName(className));
            return names;
        }

        /// <summary>The class that an expression certainly holds, as far as the compiler can tell without types: `this`, a variable declared with a class or with `new`, `new Class()`.</summary>
        private string? InferClass(Expr target)
        {
            switch (target)
            {
                case ThisExpr: return _currentClass?.Name;
                case NewExpr ne: return IsKnownClassName(ne.ClassRef.BaseName) ? ne.ClassRef.BaseName : null;
                case IdentifierExpr id:
                    for (var scope = _current; scope != null; scope = scope.Parent)
                        if (scope.Slots.ContainsKey(id.Name)) return scope.KnownClasses.TryGetValue(id.Name, out var known) ? known : null;
                    return null;
                default: return null;
            }
        }

        private string? KnownClassOf(TypeRef? type, Expr? initializer, bool isArray)
        {
            if (isArray) return null;
            if (type != null && type.BaseName != TypeRef.InferredMarker && IsKnownClassName(type.BaseName)) return type.BaseName;
            if (type is null or { BaseName: TypeRef.InferredMarker } && initializer is NewExpr ne && IsKnownClassName(ne.ClassRef.BaseName)) return ne.ClassRef.BaseName;
            return null;
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
        /// The name is looked up like a type name in the namespace of the class the code is written in and its outer ones, then as written
        /// (InNamespaces) - not through `#using` (an IdentifierExpr carries no namespace context of its own, only a TypeRef does: Parser.CurrentNamespaces).</summary>
        private string? TryResolveStaticMemberAccess(MemberExpr me)
        {
            // The class we are currently in (see Ast.SelfClassExpr) -
            // stands only for the backing field of static auto-properties in
            // generic classes.
            if (me.Target is SelfClassExpr) return _currentClass?.Name;

            string? className = DottedName(me.Target);
            return className != null ? InNamespaces(className, IsKnownClassName, me.Line) : null;
        }

        /// <summary>A chain of names in an expression (`Kind`, `Shapes.Circle`) as a name that `known` knows, looked up like a type name (SPEC
        /// "Namespaces"): in the namespace of the class the code is written in and its outer ones, from the inside out, then as written - `Kind.Circle`
        /// and `Helper.Tag()` inside `namespace Geo` are `Geo.Kind.Circle` and `Geo.Helper.Tag()`. A local variable or a member of the object with the
        /// name of the first link wins over every class and enum (as in the type checker, StaticClassRef); `#using` does not apply here (only to type names).
        /// `global.Name` is the global one.</summary>
        private string? InNamespaces(string dotted, Func<string, bool> known, int line = 0)
        {
            // `global.Helper.Tag()`: the global name that a class of the own namespace hides
            if (dotted.StartsWith("global.", StringComparison.Ordinal) && !IsVariableName("global"))
                return known(dotted.Substring(7)) ? dotted.Substring(7) : null;
            int dot = dotted.IndexOf('.');
            string root = dot < 0 ? dotted : dotted.Substring(0, dot);
            // (the order of the lookup: a local variable and a member of the object first - they win over every class and enum, the own as well as a global one)
            if (IsChainRoot(root)) return null;
            for (string? ns = NamespaceOf(_currentClass?.Name); !string.IsNullOrEmpty(ns); ns = NamespaceOf(ns))
                if (known(ns + "." + dotted)) return ns + "." + dotted;
            if (known(dotted)) return dotted;
            // the namespaces of the libraries the source imports (`#import "LibA"`: `Parser` is LibA.Parser); in two of them the short name is ambiguous
            if (Parser.SourceImportNamespaces is { } imports && imports.TryGetValue(_currentSource, out var importNamespaces))
            {
                var found = importNamespaces.Where(ns => known(ns + "." + dotted)).Select(ns => ns + "." + dotted).ToList();
                if (found.Count > 1)
                {
                    string message = $"'{dotted.Split('.')[0]}' is in several imported libraries ({string.Join(", ", found.Select(f => "'" + f + "'"))}) - write the full name";
                    if (!_errors.Any(e => e.RawMessage == message)) AddError(new ResolverException(message, line));
                }
                if (found.Count > 0) return found[0];
            }
            return null;
        }

        /// <summary>Can `name` start the chain `name.X` as a value here: a variable of a scope, or a field or property of the class that is reachable - an
        /// instance one not in a static method or static initialiser (there is no `this`), and no method (`Environment.Platform` in a static method of a class
        /// with a test method `Environment()` is the class Environment)?</summary>
        private bool IsChainRoot(string name)
        {
            for (var s = _current; s != null; s = s.Parent)
                if (!s.IsGlobal && s.Slots.ContainsKey(name)) return true;
            var visited = new HashSet<ClassDecl>();
            for (var cd = _currentClass; cd != null && visited.Add(cd); cd = GetBaseClassDecl(cd))
                foreach (var member in cd.Members)
                    switch (member)
                    {
                        case FieldDecl fd when fd.Name == name: return fd.IsStatic || !_inStaticMethod;
                        case PropertyDecl pd when pd.Name == name: return pd.IsStatic || !_inStaticMethod;
                    }
            return false;
        }

        /// <summary>Is `name` a variable here: of a scope (not the global one) or a member of the object?</summary>
        private bool IsVariableName(string name)
        {
            for (var s = _current; s != null; s = s.Parent)
                if (!s.IsGlobal && s.Slots.ContainsKey(name)) return true;
            return _currentClass != null && FindMemberInClassChain(_currentClass, name) != null;
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
            // `new List()` of a generic class: its raw form (every type argument `any`, SPEC "Generic classes")
            if (typeArgs.Count == 0) return;
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
            // (an interface satisfies the interfaces it extends)
            if (!_classes.ContainsKey(argName) && _interfaces.ContainsKey(argName)) return InterfaceClosure(argName).Any(i => i.Name == targetName);
            if (!_classes.TryGetValue(argName, out var cd)) return false;
            foreach (var baseRef in cd.BaseRefs ?? Array.Empty<TypeRef>())
                if (TypeNameSatisfiesIsOf(ResolveBaseRef(baseRef), targetName))
                    return true;
            return false;
        }

        /// <summary>Whether a type name means an enum: its full name, or the name in the namespace of the current class.</summary>
        private bool IsEnumTypeName(string name) =>
            _enums.ContainsKey(name) || (NamespaceOf(_currentClass?.Name) is { Length: > 0 } ns && _enums.ContainsKey(ns + "." + name));   // (`#using` qualified the name already in the parser)

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
            if (IsEnumTypeName(tr.BaseName)) return;   // (an enum is a type: `Color c`, `is of Color`)
            CheckAmbiguous(tr, line, n => IsKnownClassName(n) || _interfaces.ContainsKey(n), NamespaceOf(_currentClass?.Name));
            if (!IsKnownClassName(ResolveTypeRef(tr)) && !_interfaces.ContainsKey(ResolveBaseRef(tr)))
                throw new ResolverException($"Unknown type '{tr.BaseName}'", line);
            if (tr.TypeArgs is { Count: > 0 } typeArgs) ValidateTypeArgs(tr, typeArgs, line);
        }

        /// <summary>`List&lt;int&gt;` in a declaration: the class (or interface) must be generic with that many type parameters, and every argument a type.
        /// (The `where` conditions are checked where an object is made, at `new`.)</summary>
        private void ValidateTypeArgs(TypeRef tr, IReadOnlyList<TypeRef> typeArgs, int line)
        {
            string name = ResolveTypeRef(tr);
            int parameters = _classes.TryGetValue(name, out var cd) ? cd.TypeParams?.Count ?? 0
                : _interfaces.TryGetValue(ResolveBaseRef(tr), out var id) ? id.TypeParams?.Count ?? 0 : 0;
            string plain = GenericClassNames.PlainName(name);
            if (parameters == 0)
                throw new ResolverException($"'{plain}' is not generic, so it does not accept type arguments in angle brackets", line);
            if (parameters != typeArgs.Count)
                throw new ResolverException($"'{plain}' is generic with {parameters} type parameter(s), not {typeArgs.Count}", line);
            foreach (var arg in typeArgs)
            {
                if (arg.NonNull) throw new ResolverException($"'defined {arg.BaseName}' cannot be a type argument - 'defined' belongs to variables, parameters, fields, properties and return types", line);
                ValidateTypeRef(arg, line);
            }
        }

        /// <summary>`defined int` (SPEC 8.10b) is a single value - not an array.</summary>
        private static void ValidateNonNull(TypeRef? type, int arrayRanks, int line)
        {
            if (type is not { NonNull: true }) return;
            if (arrayRanks > 0 || type.ArrayRank > 0)
                throw new ResolverException($"'defined {type.BaseName}' is a single value that is never undefined - not an array", line);
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
            /// <summary>Declared bit widths (TypeRef.WidthCode) of the variables of this scope: an assignment cuts the value to them.</summary>
            public readonly Dictionary<string, int> Widths = new();

            /// <summary>Names of the `ref` parameters of this scope (the slot holds a pointer to the caller's variable).</summary>
            public readonly HashSet<string> RefNames = new();

            /// <summary>Names that lie in THIS (lambda) scope as a capture (copy of an outer variable) - assignment is an error,
            /// a declaration of its own with the same name hides it (see Define).</summary>
            public readonly HashSet<string> CaptureNames = new();

            /// <summary>The class that a variable of this scope is known to hold (`Cache c = ...`, `var c = new Cache()`): only used to tell where a declared warning applies.</summary>
            public readonly Dictionary<string, string> KnownClasses = new();

            public ResolverScope(ResolverScope? parent, bool isGlobal = false)
            {
                Parent = parent;
                IsGlobal = isGlobal;
            }
        }

        private void PushScope() => _current = new ResolverScope(_current);
        private void PopScope() => _current = _current.Parent!;

        private void Define(string name, int line, bool isReadonly = false, string? requiredUnit = null, bool isRef = false, int width = 0)
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
                _current.Widths.Remove(name);
            }
            _current.Slots[name] = _current.Slots.Count;
            if (isRef) _current.RefNames.Add(name);
            if (isReadonly) _current.ReadonlySlots.Add(name);
            if (requiredUnit != null) _current.RequiredUnits[name] = requiredUnit;
            if (width != 0) _current.Widths[name] = width;
        }

        private ResolvedRef ResolveIdentifierRef(string name, int line)
        {
            // the chain (like C#): the local scopes, the members of the object (its class and the base classes), then the global variables and
            // the natives - a field `count` hides a global `count` inside the class
            int depth = 0;
            var scope = _current;
            ResolverScope? global = null;
            int globalSlot = -1;
            while (scope != null)
            {
                if (scope.Slots.TryGetValue(name, out int slot))
                {
                    if (scope.IsGlobal) { global = scope; globalSlot = slot; break; }
                    scope.RequiredUnits.TryGetValue(name, out var requiredUnit);
                    scope.Widths.TryGetValue(name, out int width);
                    return new ResolvedRef.Local(depth, slot, requiredUnit, scope.RefNames.Contains(name), width);
                }
                depth++;
                scope = scope.Parent;
            }

            if (_currentClass != null && FindMemberInClassChain(_currentClass, name) is { } member)
            {
                if (member.IsStatic) { CheckStaticInitOrder(member.ClassName, name, line); return new ResolvedRef.StaticMember(member.ClassName); }
                // (a fire body runs without `this`: its own variables and the captures - `taking`, `with` - win over a field of the same name)
                if (_fireScope != null && !_inBoundLambda)
                {
                    if (global != null) return new ResolvedRef.Global(globalSlot, global.RequiredUnits.GetValueOrDefault(name), global.Widths.GetValueOrDefault(name));
                    throw new ResolverException(
                        $"'{name}' is an instance member - a 'fire' block runs without 'this': give the thread what it needs with 'taking' or 'with'", line);
                }
                if (!_inStaticMethod) return new ResolvedRef.ImplicitThisMember();
                if (global == null)
                    throw new ResolverException(
                        $"'{name}' is an instance member - it is not reachable in a static method or static " +
                        "field initializer without a bound 'this'", line);
            }

            if (global != null)
            {
                global.RequiredUnits.TryGetValue(name, out var requiredUnit);
                global.Widths.TryGetValue(name, out int width);
                return new ResolvedRef.Global(globalSlot, requiredUnit, width);
            }

            if (_nativeNames.Contains(name))
                return new ResolvedRef.Native(name);
            if (_externs.ContainsKey(name))
                return new ResolvedRef.Extern(name);
            if (_tryableNativeNames.Contains(name))
                throw new ResolverException(
                    $"'{name}' is registered as 'tryable' - it can only be called with 'try {name}(...)', " +
                    "not as a direct call.", line);

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
                    Define(vd.Name, vd.Line, vd.IsReadonly, vd.Type?.UnitDemand, width: vd.ArrayRanks.Count == 0 ? vd.Type?.WidthCode ?? 0 : vd.Type?.ArrayWidthCode(vd.ArrayRanks.Count) ?? 0);
            }
        }

        /// <summary>The part of the resolver state that the resolution of a
        /// statement/class member temporarily changes (see
        /// ResolveStmt).</summary>
        private readonly record struct ResolverState(
            ResolverScope Current, int FunctionDepth, int LoopDepth, System.Collections.Immutable.ImmutableStack<int> LoopFinallyDepths, int TryDepth, int UnsafeDepth,
            ClassDecl? CurrentClass, bool CurrentClassHasBase, bool InConstructor, bool InStaticMethod,
            Dictionary<string, int>? TypeParamNames);

        private ResolverState SaveState() => new(
            _current, _functionDepth, _loopDepth, _loopFinallyDepths, _tryDepth, _unsafeDepth,
            _currentClass, _currentClassHasBase, _inConstructor, _inStaticMethod,
            _currentTypeParamNames.Count > 0 ? new Dictionary<string, int>(_currentTypeParamNames) : null);

        private void RestoreState(ResolverState state)
        {
            _current = state.Current;
            _functionDepth = state.FunctionDepth;
            _loopDepth = state.LoopDepth;
            _loopFinallyDepths = state.LoopFinallyDepths;
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

                case TypeCheckDirective:
                    break; // read by the type checker

                case GlobalJobsDirective:
                    break; // takes effect only at runtime (see Compiler.Compile: SetAutoSync)

                case EndWaitDirective ew:
                    ResolveExpr(ew.Value);
                    break; // takes effect only at runtime (see Compiler.Compile: SetEndWait)

                case TimeoutDirective td:
                    ResolveExpr(td.Value);
                    break; // takes effect only at runtime (see Compiler.Compile: SetTimeout)

                case VarDeclStmt vd:
                    if (vd.Initializer != null) ResolveExprAllowTake(vd.Initializer);
                    if (vd.Type != null) ValidateTypeRef(vd.Type, vd.Line);
                    ValidateNonNull(vd.Type, vd.ArrayRanks.Count, vd.Line);
                    ResolveArrayRanks(vd.ArrayRanks);
                    if (vd.Initializer == null && vd.ArrayRanks.Count == 0 && (vd.Type == null || vd.Type.IsInferred))
                        throw new ResolverException($"'var {vd.Name}' needs an initializer: the type of a 'var' comes from its value (or declare it with a type: 'int {vd.Name}')", vd.Line);
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
                    Define(vd.Name, vd.Line, vd.IsReadonly, vd.Type?.UnitDemand, width: vd.ArrayRanks.Count == 0 ? vd.Type?.WidthCode ?? 0 : vd.Type?.ArrayWidthCode(vd.ArrayRanks.Count) ?? 0);
                    if (KnownClassOf(vd.Type, vd.Initializer, vd.ArrayRanks.Count > 0) is { } declaredClass) _current.KnownClasses[vd.Name] = declaredClass;
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
                    _loopFinallyDepths = _loopFinallyDepths.Push(savedFinallyDepth);
                    ResolveStmtAsScope(ws.Body);
                    _loopFinallyDepths = _loopFinallyDepths.Pop();
                    _tryDepth = savedFinallyDepth;
                    _loopDepth--;
                    break;

                case DoWhileStmt dws:
                    {
                        _loopDepth++;
                        int savedDoFinallyDepth = _tryDepth; _tryDepth = 0;
                        _loopFinallyDepths = _loopFinallyDepths.Push(savedDoFinallyDepth);
                        ResolveStmtAsScope(dws.Body);
                        _loopFinallyDepths = _loopFinallyDepths.Pop();
                        _tryDepth = savedDoFinallyDepth;
                        _loopDepth--;
                        ResolveExpr(dws.Condition);
                        break;
                    }

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
                    CheckLoopLevels("break", bs.Levels, bs.Line);
                    break;

                case ContinueStmt cs:
                    if (_loopDepth == 0)
                        throw new ResolverException("'continue' outside of a loop ('while'/'for'/'foreach')", cs.Line);
                    CheckLoopLevels("continue", cs.Levels, cs.Line);
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

        /// <summary>Finally depth outside each open loop (innermost on top): `break 2` must not leave a `finally` block of an outer loop either.</summary>
        private System.Collections.Immutable.ImmutableStack<int> _loopFinallyDepths = System.Collections.Immutable.ImmutableStack<int>.Empty;

        private void CheckLoopLevels(string keyword, int levels, int line)
        {
            int open = _loopFinallyDepths.Count();
            if (levels > open)
                throw new ResolverException($"'{keyword} {levels}' needs {levels} enclosing loops, there {(open == 1 ? "is 1" : $"are {open}")}", line);
            bool crossesFinally = _tryDepth > 0 || _loopFinallyDepths.Take(levels - 1).Any(d => d > 0);
            if (crossesFinally)
                throw new ResolverException($"'{keyword}' cannot be used to leave a 'finally' block (a limitation of this stage - see BYTECODE.md)", line);
        }

        private void ResolveFor(ForStmt fs)
        {
            PushScope(); // encloses init/condition/increment/body together
            if (fs.Init != null) ResolveStmt(fs.Init);
            if (fs.Condition != null) ResolveExpr(fs.Condition);
            if (fs.Increment != null) ResolveExpr(fs.Increment);
            _loopDepth++;
            int savedFinallyDepth = _tryDepth; _tryDepth = 0;
            _loopFinallyDepths = _loopFinallyDepths.Push(savedFinallyDepth);
            ResolveStmtAsScope(fs.Body);
            _loopFinallyDepths = _loopFinallyDepths.Pop();
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
            _loopFinallyDepths = _loopFinallyDepths.Push(savedFinallyDepth);
            ResolveStmtAsScope(fe.Body);
            _loopFinallyDepths = _loopFinallyDepths.Pop();
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
                // (a class, or an interface: `catch (IRetryable e)` takes an exception whose class implements it)
                if (c.TypeRef != null && !IsKnownClassName(ResolveTypeRef(c.TypeRef)) && !_interfaces.ContainsKey(ResolveBaseRef(c.TypeRef)))
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
            var seenMethodSignatures = new List<(string Name, string[] Types)>();
            var seenConstructorSignatures = new List<string[]>();

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
                            if (fd.Name.StartsWith("_Auto", StringComparison.Ordinal) && fd.Access == AccessModifier.Private && (fd.Type == null || fd.Type.IsInferred)) { }   // (the backing field of an untyped auto-property: the property is reported)
                            else if (fd.Type == null || fd.Type.IsInferred)
                                AddError(new ResolverException($"Field '{fd.Name}' of class '{cd.Name}' needs a type: a field is not declared with 'var' (use the type it holds, or 'any' if every type is allowed)", fd.Line));
                            else Guard(() => ValidateTypeRef(fd.Type, fd.Line));
                            Guard(() => ValidateNonNull(fd.Type, fd.ArrayRanks.Count, fd.Line));
                            ResolveArrayRanks(fd.ArrayRanks);
                            ResolveFieldInitializer(fd);
                            break;

                        case ConstructorDecl ctor:
                            var ctorTypes = SignatureKeys(ctor.Params, classTypeParamNames);
                            if (seenConstructorSignatures.Any(t => SameSignature(t, ctorTypes)))
                                AddError(new ResolverException(
                                    $"A constructor with {ctor.Params.Count} parameter(s) of these types is already defined " +
                                    "in this class (an overload needs a different number of parameters or other parameter types).", ctor.Line));
                            seenConstructorSignatures.Add(ctorTypes);
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
                                {
                                    Guard(() => ValidateTypeRef(md.ReturnType, md.Line));
                                    Guard(() => ValidateNonNull(md.ReturnType, 0, md.Line));
                                }
                                var methodTypes = SignatureKeys(md.Params, classTypeParamNames.Concat(methodTypeParamNames).ToList());
                                if (seenMethodSignatures.Any(m => m.Name == md.Name && SameSignature(m.Types, methodTypes)))
                                    AddError(new ResolverException(
                                        $"Method '{md.Name}' with {md.Params.Count} parameter(s) of these types is already defined " +
                                        "in this class (an overload needs a different number of parameters or other parameter types).", md.Line));
                                seenMethodSignatures.Add((md.Name, methodTypes));
                                ResolveFunctionLike(md.Params, md.Body, baseArgs: null, isConstructor: false, isStatic: md.IsStatic);
                            }
                            finally
                            {
                                RemoveTypeParamNames(methodTypeParamNames);
                            }
                            break;

                        case PropertyDecl pd:
                            if (pd.Type == null || pd.Type.IsInferred)
                                AddError(new ResolverException($"Property '{pd.Name}' of class '{cd.Name}' needs a type (use the type it holds, or 'any' if every type is allowed)", pd.Line));
                            else
                            {
                                Guard(() => ValidateTypeRef(pd.Type, pd.Line));
                                Guard(() => ValidateNonNull(pd.Type, 0, pd.Line));
                            }
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
            var savedStaticInit = _staticInit;
            _staticInit = fd.IsStatic && _currentClass != null ? (_currentClass.Name, fd.Name) : null;
            ResolveExpr(fd.Initializer);
            if (_staticInit is { } done) _staticInitDone.Add(done);
            _staticInit = savedStaticInit;
            _inStaticMethod = savedInStaticMethod;
            _current = saved;
        }

        /// <summary>The static field whose initialiser is being resolved (null: none), and the static fields whose initialisers are done - in the order in which
        /// they run (SPEC "Static members": once at program start, class by class in the order of the program, in a class from the top down).</summary>
        private (string Class, string Field)? _staticInit;
        private readonly HashSet<(string Class, string Field)> _staticInitDone = new();

        /// <summary>A static initialiser reads the static field `name` of the class `className` (the class written, or one of its bases): an error when that
        /// field has an initialiser that only runs later - it would still be `undefined` (a read of another class's field that has none is fine).</summary>
        private void CheckStaticInitOrder(string className, string name, int line)
        {
            if (_staticInit == null || !_classes.TryGetValue(className, out var cd) || FindMemberInClassChain(cd, name) is not { IsStatic: true } member) return;
            if (!_classes.TryGetValue(member.ClassName, out var owner)) return;
            if (owner.Members.OfType<FieldDecl>().FirstOrDefault(f => f.Name == name && f.IsStatic) is not { Initializer: not null }) return;
            if (_staticInitDone.Contains((member.ClassName, name))) return;
            var (initClass, initField) = _staticInit.Value;
            AddError(new ResolverException((member.ClassName, name) == _staticInit.Value
                ? $"The initialiser of the static field '{initClass}.{initField}' reads the field itself - it is still undefined there"
                : $"The initialiser of the static field '{initClass}.{initField}' reads '{member.ClassName}.{name}', whose initialiser only runs later (static fields are initialised once at program start: class by class in the order of the program, in a class from the top down) - declare '{member.ClassName}.{name}' first", line));
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
                Guard(() => ValidateNonNull(p.Type, p.ArrayRanks.Count, body.Line));
                ResolveArrayRanks(p.ArrayRanks);
                Guard(() => Define(p.Name, body.Line, requiredUnit: p.Type?.UnitDemand, isRef: p.ByRef, width: p.ArrayRanks.Count == 0 ? p.Type?.WidthCode ?? 0 : 0));
                if (KnownClassOf(p.Type, null, p.ArrayRanks.Count > 0) is { } parameterClass) _current.KnownClasses[p.Name] = parameterClass;
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
            var savedLoopFinallyDepths = _loopFinallyDepths; _loopFinallyDepths = System.Collections.Immutable.ImmutableStack<int>.Empty;
            _tryDepth = 0;

            _functionDepth++;
            foreach (var s in body.Statements) ResolveStmt(s);
            _functionDepth--;

            _loopDepth = savedLoopDepth;
            _loopFinallyDepths = savedLoopFinallyDepths;
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
            if (expr is UnaryExpr { Op: UnaryOp.Take or UnaryOp.TryTake }) _takeAllowed.Add(expr);
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
                    if (_inStaticMethod && !_inBoundLambda)   // (in a lambda bound with `on`, `this` is its target - also in a static method)
                        throw new ResolverException(
                            "'this' is not valid in a static method or static field initializer " +
                            "(no instance bound)", te.Line);
                    break; // Runtime decides whether/what 'this' is currently bound to

                case BaseExpr be:
                    if (_inStaticMethod)
                        throw new ResolverException(
                            "'base' is not valid in a static method (no instance bound)", be.Line);
                    if (_currentClass == null || !_currentClassHasBase)
                        throw new ResolverException(
                            "'base' is only valid inside a class with a base class", be.Line);
                    break;

                case UnaryExpr u:
                    if (u.Op is UnaryOp.Take or UnaryOp.TryTake && !_takeAllowed.Remove(u))
                        throw new ResolverException(
                            $"'{(u.Op == UnaryOp.TryTake ? "try take" : "take")}' is only valid as an argument of a call or on the right of '=' / 'var x =' (SPEC 2.2)", u.Line);
                    if ((u.Op == UnaryOp.Dereference || u.Op == UnaryOp.AddressOf) && _unsafeDepth == 0)
                        throw new ResolverException(
                            $"'{(u.Op == UnaryOp.Dereference ? "*" : "&")}' is only valid inside an 'unsafe' block", u.Line);
                    ResolveExpr(u.Operand);
                    break;

                case BinaryExpr b:
                    ResolveExpr(b.Left);
                    ResolveExpr(b.Right);
                    break;

                case RebindExpr rb:
                    ResolveExpr(rb.Lambda);
                    ResolveExpr(rb.Target);
                    break;

                case ConditionalExpr ce:
                    ResolveExpr(ce.Condition);
                    ResolveExpr(ce.Then);
                    ResolveExpr(ce.Else);
                    break;

                case UnitCoerceExpr uc:
                    ResolveExpr(uc.Operand);
                    break;

                case TypeCoerceExpr tc:
                    ResolveExpr(tc.Operand);
                    // `x!Dog`: a class or an interface (like `is of`)
                    if (tc.TargetClass is { } castType && !_interfaces.ContainsKey(castType.BaseName)) ValidateTypeName(castType, tc.Line);
                    break;

                case IsInExpr iin:
                    ResolveExpr(iin.Operand);
                    break;

                case RangeExpr range:
                    ResolveExpr(range.From);
                    ResolveExpr(range.To);
                    break;

                case IsOfExpr iof:
                    // `T is of X` with T a type parameter (and no variable of that name): the type argument is tested (OpCode.TypeParamIsOf)
                    if (!(iof.Operand is IdentifierExpr { Name: var tpName } && _currentTypeParamNames.GetValueOrDefault(tpName) > 0 && FindScopeOf(tpName) == null))
                        ResolveExpr(iof.Operand);
                    // `value is of IFoo`: an interface is also allowed as a type (the class names it in `class X : IFoo`)
                    if (!_interfaces.ContainsKey(iof.TypeRef.BaseName)) ValidateTypeName(iof.TypeRef, iof.Line);
                    break;

                case IsFromExpr ifr:
                    ResolveExpr(ifr.Operand);
                    ResolveExpr(ifr.OwnerExpr);
                    break;

                case CallExpr call:
                    _calleeArity[call.Callee] = call.Args.Count;   // (a member that is called: the member warnings of a method, not of a property)
                    if (call.TypeArgs != null)
                        foreach (var typeArg in call.TypeArgs) Guard(() => ValidateTypeRef(typeArg, call.Line));
                    ResolveExpr(call.Callee);
                    foreach (var a in call.Args) ResolveExprAllowTake(a);
                    break;

                case MemberExpr me:
                    // 'EnumName.Member' - the target is a chain of names that is a known enum (InNamespaces: a local variable or a member of
                    // the object with the name of its first link wins).
                    // An enum of the own namespace (or an outer one) by its short name, any other one by its full name ('Geometry.Kind.Round') - as
                    // with static class access (InNamespaces), `#using` does not apply.
                    string? enumName = DottedName(me.Target) is { } enumWritten ? InNamespaces(enumWritten, n => _enums.ContainsKey(n), me.Line) : null;
                    if (enumName != null && _enums.TryGetValue(enumName, out var enumMembers))
                    {
                        if (!enumMembers.ContainsKey(me.Name) && me.Name is "Name" or "Parse" or "IsDefined" or "Values" && IsKnownClassName(Parser.EnumHelperClassName(enumName)))
                        {
                            // `Color.Name(c)`, `Color.Parse("Red")`: the hidden helper class of the enum (see Parser.EnumHelperClass)
                            _refs[me] = new ResolvedRef.StaticMember(Parser.EnumHelperClassName(enumName));
                            break;
                        }
                        if (!enumMembers.TryGetValue(me.Name, out long enumValue))
                            throw new ResolverException($"'{enumName}' has no member '{me.Name}'", me.Line);
                        _refs[me] = new ResolvedRef.EnumMember(enumValue, _enumTypes.GetValueOrDefault(enumName));
                        break;
                    }
                    // 'ClassName.Member' (SPEC "Static members") - as
                    // in the enum case: a local variable or a member of the same name wins
                    // over the class (InNamespaces). The actual
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
                        CheckStaticMemberExists(staticClassName, me);
                        WarnForClassUse(staticClassName, me.Line);
                        WarnForMember(ClassChainNames(staticClassName), me.Name, _calleeArity.TryGetValue(me, out int staticArgs) ? staticArgs : null, me.Line);
                        CheckStaticInitOrder(staticClassName, me.Name, me.Line);
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
                    if (HasDeclaredWarnings)
                    {
                        string? receiver = InferClass(me.Target);
                        WarnForMember(receiver != null ? ClassChainNames(receiver) : null, me.Name, _calleeArity.TryGetValue(me, out int memberArgs) ? memberArgs : null, me.Line);
                    }
                    ResolveExpr(me.Target); // .Name bleibt unresolved - dynamischer Feld-/Methodenzugriff
                    break;

                case IndexExpr ix:
                    ResolveExpr(ix.Target);
                    ResolveExpr(ix.Index);
                    break;

                case AssignExpr asg:
                    ResolveExprAllowTake(asg.Value);
                    if (asg.Value is UnaryExpr { Op: UnaryOp.Take or UnaryOp.TryTake } && asg.Target is UnaryExpr { Op: UnaryOp.Dereference })
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
                        CheckAmbiguous(ne.ClassRef with { TypeArgCount = ne.TypeArgs?.Count ?? 0 }, ne.Line, IsKnownClassName, NamespaceOf(_currentClass?.Name));
                        string resolvedNewClassName = GenericClassNames.ResolveNewTarget(
                            ne.ClassRef, ne.TypeArgs?.Count ?? 0, IsKnownClassName);
                        if (!IsKnownClassName(resolvedNewClassName))
                            throw new ResolverException($"Unknown class '{ne.ClassRef.BaseName}'", ne.Line);
                        WarnForNew(resolvedNewClassName, ne.Args.Count, ne.Line);
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

                case UntilExpr until:
                    foreach (var limit in until.Limits) ResolveExpr(limit);
                    ResolveExpr(until.Inner);
                    break;

                case AwaitOnExpr awaitOn:
                    foreach (var arg in awaitOn.Args) ResolveExprAllowTake(arg);
                    ResolveLambda(awaitOn.Lambda);
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

                case ConditionalAccessExpr conditional:
                    // `a?.b...` / `a??.b...`: the target, then the chain on its value (the placeholder stands for it)
                    ResolveExpr(conditional.Target);
                    ResolveExpr(conditional.Inner);
                    break;

                case ConditionalTargetExpr:
                    break;

                case WithExpr with:
                    // `copy x with { .a = v }`: the copy, then the values of the assignments
                    ResolveExpr(with.Source);
                    foreach (var set in with.Sets) ResolveExprAllowTake(set.Value);   // (`.a = take y` like an assignment)
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
                    // 'obj.field = ...' from anywhere else: an error when every class that declares a field of this name declares it 'readonly'
                    // (the class of 'obj' is not known here; a name that some class declares writable stays allowed)
                    if (me.Target is not ThisExpr && IsReadonlyEverywhere(me.Name))
                        throw new ResolverException(
                            $"'{me.Name}' is 'readonly' and can only be assigned inside a constructor of its class (as 'this.{me.Name} = ...')", me.Line);
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
                        CheckStaticMemberExists(staticAssignClassName, me);
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
        /// <summary>Is `fieldName` declared by at least one class, and `readonly` in every class that declares it?</summary>
        private bool IsReadonlyEverywhere(string fieldName)
        {
            bool any = false;
            foreach (var cd in _classes.Values)
                foreach (var m in cd.Members)
                    if (m is FieldDecl fd && fd.Name == fieldName)
                    {
                        if (!fd.IsReadonly) return false;
                        any = true;
                    }
            return any;
        }

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
        /// docs/spec/THREADING_DESIGN.md section 8: the fire thread gets at
        /// its start an OWN, isolated copy of ALL main-program
        /// globals (see VM.OpCode.Fire) - visible for reading under the same
        /// name as in the main program, but NOT writable (an
        /// assignment would only change the local copy, never the original
        /// - that would be silently wrong, therefore rejected hard here
        /// instead of allowed).</summary>
        private void ResolveFireStmt(FireStmt fs)
        {
            _sawFire = true;
            // a local of the code around the block that the body uses without `taking`/`handover`: the thread gets a copy (as with `taking`) - a warning
            // of the type checker in lax mode, an error in strict mode (TC023)
            if (fs.TakingCaptures is List<FireTakingCapture> captureList)
            {
                var used = new List<string>();
                AstNames.Collect(fs.Body, used, new HashSet<string>());
                var declared = AstNames.Declared(fs.Body);
                foreach (var name in used)
                {
                    if (declared.Contains(name) || captureList.Exists(c => c.VarName == name) || fs.With.Any(w => w.VarName == name)) continue;
                    var scope = FindScopeOf(name);
                    if (scope == null || scope.IsGlobal) continue;   // (the globals of the main program: the thread reads them directly)
                    int firstMoved = captureList.FindIndex(c => c.Handover);
                    var capture = new FireTakingCapture(name, new IdentifierExpr(fs.Line, name), Implicit: true);
                    if (firstMoved < 0) captureList.Add(capture); else captureList.Insert(firstMoved, capture);
                }
            }
            if (fs.Until != null) foreach (var limit in fs.Until) ResolveExpr(limit);   // (in the caller: the token the thread follows)
            foreach (var capture in fs.TakingCaptures)
            {
                ResolveExpr(capture.Source);
                // `handover` in a fire thread: only a variable of the thread itself - a global variable of the main program is not its own to give away
                if (capture.Handover && _fireScope != null && FindScopeOf(capture.VarName) == _fireScope && _fireScope.Slots[capture.VarName] < _fireSharedSlots)
                    throw new ResolverException($"'handover' needs a variable of this thread: '{capture.VarName}' is a global variable of the main program", fs.Line);
            }
            foreach (var with in fs.With) ResolveExpr(with.Source);

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
                    // A fire thread may change globals (docs/spec/THREADING_DESIGN.md section 7): single assignments run as a section that
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
            var savedFireScope = _fireScope;
            int savedFireSharedSlots = _fireSharedSlots;
            _fireScope = _current;
            _fireSharedSlots = nextCaptureSlot;
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
            foreach (var with in fs.With)
            {
                if (!capturesSeen.Add(with.VarName))
                    throw new ResolverException($"'{with.VarName}' was already captured in this 'fire'", fs.Line);
                if (_current.Slots.TryGetValue(with.VarName, out int shadowedWith)) _current.Slots["\u0001shadow:" + with.VarName] = shadowedWith;
                _current.Slots[with.VarName] = nextCaptureSlot++;
                _current.ReadonlySlots.Remove(with.VarName);
            }

            int savedLoopDepth = _loopDepth;
            int savedTryDepth = _tryDepth;
            int savedFunctionDepth = _functionDepth;
            bool savedInConstructor = _inConstructor;
            _loopDepth = 0;
            var savedLoopFinallyDepths = _loopFinallyDepths; _loopFinallyDepths = System.Collections.Immutable.ImmutableStack<int>.Empty;
            _tryDepth = 0;
            _functionDepth = 0;
            _inConstructor = false;

            foreach (var s in fs.Body.Statements) ResolveStmt(s);

            _loopDepth = savedLoopDepth;
            _loopFinallyDepths = savedLoopFinallyDepths;
            _tryDepth = savedTryDepth;
            _functionDepth = savedFunctionDepth;
            _inConstructor = savedInConstructor;
            _fireScope = savedFireScope;
            _fireSharedSlots = savedFireSharedSlots;
            _current = saved;
        }

        // The isolated global scope of the fire body being resolved (null outside of one) and how many of its slots are the main program's
        // globals - a `handover` inside it must not name one of them (see ResolveFireStmt).
        private ResolverScope? _fireScope;
        private int _fireSharedSlots;

        /// <summary>The scope of the current chain that declares `name` (null: none).</summary>
        private ResolverScope? FindScopeOf(string name)
        {
            for (var s = _current; s != null; s = s.Parent)
                if (s.Slots.ContainsKey(name)) return s;
            return null;
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
            var savedLoopFinallyDepths = _loopFinallyDepths; _loopFinallyDepths = System.Collections.Immutable.ImmutableStack<int>.Empty;
            _tryDepth = 0;
            _functionDepth = 0;
            _inConstructor = false;

            foreach (var s in body.Statements) ResolveStmt(s);

            _loopDepth = savedLoopDepth;
            _loopFinallyDepths = savedLoopFinallyDepths;
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
            List<Expr>? loads = null;
            List<string>? implicitNames = null;
            foreach (var name in names)
            {
                if (paramNames.Contains(name)) continue;
                int depth = 0;
                ResolverScope? found = null;
                bool reachesFire = false;
                for (var scope = enclosing; scope != null; scope = scope.Parent, depth++)
                {
                    if (scope.IsGlobal) { reachesFire = ReferenceEquals(scope, _fireScope); break; }
                    if (scope.Slots.ContainsKey(name)) { found = scope; break; }
                }
                var outer = new IdentifierExpr(lambda.Line, name);
                string? requiredUnit;
                if (found != null)
                {
                    found.RequiredUnits.TryGetValue(name, out requiredUnit);
                    _refs[outer] = new ResolvedRef.Local(depth, found.Slots[name], requiredUnit, found.RefNames.Contains(name));   // (the capture copies the VALUE of a ref parameter)
                }
                else if (reachesFire && _fireScope!.Slots.TryGetValue(name, out int threadSlot) && threadSlot >= _fireSharedSlots && !name.StartsWith('\u0001'))
                {
                    // a variable of the `fire` body itself (its own slot of the thread's global scope): the lambda captures it like a local (natively it is a
                    // variable of the body's function, no global the lambda could reach)
                    _fireScope.RequiredUnits.TryGetValue(name, out requiredUnit);
                    _refs[outer] = new ResolvedRef.Global(threadSlot, requiredUnit);
                }
                else if (!reachesFire && _globalScope.Slots.TryGetValue(name, out int globalSlot) && !name.StartsWith('\u0001') && !name.StartsWith('$')
                    && lambda.CaptureModes != null && lambda.CaptureModes.ContainsKey(name))
                {
                    // a global variable (ROADMAP_TASKS step 8): without a word the lambda reads and changes the original (no capture - writes and method
                    // calls from another thread go to the main program); `taking g` captures a copy; it belongs to the program and cannot move into a lambda
                    if (lambda.CaptureModes.TryGetValue(name, out bool movesGlobal) && movesGlobal)
                        throw new ResolverException($"'handover {name}': '{name}' is a global variable - it belongs to the program and cannot move into a lambda; write 'taking {name}' (a copy)", lambda.Line);
                    _globalScope.RequiredUnits.TryGetValue(name, out requiredUnit);
                    _refs[outer] = new ResolvedRef.Global(globalSlot, requiredUnit);
                }
                else
                {
                    if (lambda.CaptureModes != null && lambda.CaptureModes.ContainsKey(name))
                        throw new ResolverException($"'{(lambda.CaptureModes[name] ? "handover" : "taking")} {name}' in a lambda names a variable of the code around it - '{name}' is none", lambda.Line);
                    continue;
                }
                Define(name, lambda.Line, requiredUnit: requiredUnit);
                _current.CaptureNames.Add(name);
                (captures ??= new List<IdentifierExpr>()).Add(outer);
                // (`taking x` / `handover x` in the body: the lambda gets a copy / takes it over when it is made; without a word a copy too - marked
                // implicit, the type checker says so: TC023)
                Expr load = new UnaryExpr(lambda.Line, UnaryOp.DeepCopy, outer);
                // (an element of `await [ ... ]`: the caller waits until it is done - its variables are lent, not copied, like the arguments of a single
                // `await`; natively awaitRun runs the elements side by side only when no two of them reach the same object)
                if (lambda.LendCaptures) load = outer;
                else if (lambda.CaptureModes == null || !lambda.CaptureModes.ContainsKey(name)) (implicitNames ??= new List<string>()).Add(name);
                if (lambda.CaptureModes != null && lambda.CaptureModes.TryGetValue(name, out bool moved))
                {
                    if (moved)
                    {
                        // (moved like a handover to a thread: fire.Standard.LambdaHandover; the compiler resets the variable after the lambda is made)
                        var mover = new IdentifierExpr(lambda.Line, fire.Standard.LambdaHandover.NativeName);
                        _refs[mover] = new ResolvedRef.Native(fire.Standard.LambdaHandover.NativeName);
                        load = new CallExpr(lambda.Line, mover, new List<Expr> { outer });
                    }
                }
                (loads ??= new List<Expr>()).Add(load);
            }
            if (captures != null) _refs[lambda] = new ResolvedRef.LambdaCaptures(captures, loads, implicitNames);
        }

        /// <summary>Is the code currently being resolved inside a lambda with `on target`? Then unknown names are members of the bound object.</summary>
        private bool _inBoundLambda;

        private void ResolveLambda(LambdaExpr lambda)
        {
            bool savedBound = _inBoundLambda;
            _inBoundLambda = lambda.OnTarget != null;
            var savedStaticInit = _staticInit;
            _staticInit = null;   // (a lambda in a static initialiser runs later: it may read any static field)
            try { ResolveLambdaCore(lambda); }
            finally { _inBoundLambda = savedBound; _staticInit = savedStaticInit; }
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
                ValidateNonNull(p.Type, p.ArrayRanks.Count, lambda.Line);
                ResolveArrayRanks(p.ArrayRanks);
                Define(p.Name, lambda.Line, requiredUnit: p.Type?.UnitDemand);
            }

            if (lambda.AutoCapture) DefineCaptures(lambda, enclosing);

            int savedLoopDepth = _loopDepth;
            int savedTryDepth = _tryDepth;
            _loopDepth = 0;
            var savedLoopFinallyDepths = _loopFinallyDepths; _loopFinallyDepths = System.Collections.Immutable.ImmutableStack<int>.Empty;
            _tryDepth = 0;

            _functionDepth++;
            foreach (var s in lambda.Body.Statements) ResolveStmt(s);
            _functionDepth--;

            _loopDepth = savedLoopDepth;
            _loopFinallyDepths = savedLoopFinallyDepths;
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

        // `x??.name` whose member the type checker proves (the type of x has it): the compiler checks only for undefined
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ConditionalAccessExpr, object> s_provenMembers = new();
        public static void ProveMember(ConditionalAccessExpr e) => s_provenMembers.AddOrUpdate(e, true);
        public static bool ProvenMember(ConditionalAccessExpr e) => s_provenMembers.TryGetValue(e, out _);

        /// <summary>The names a node declares anywhere below it (variables, `foreach` variables, the variable of a catch, the parameters of lambdas, the
        /// captures of a nested `fire`) - what a body uses under such a name is its own, not a variable of the code around it.</summary>
        public static HashSet<string> Declared(object? node)
        {
            var declared = new HashSet<string>();
            var all = new List<object>();
            Walk(node, all);
            foreach (var n in all)
                switch (n)
                {
                    case VarDeclStmt v: declared.Add(v.Name); break;
                    case ForeachStmt f: declared.Add(f.VarName); break;
                    case CatchClause c: declared.Add(c.VarName); break;
                    case LambdaParam p: declared.Add(p.Name); break;
                    case FireTakingCapture t: declared.Add(t.VarName); break;
                    case FireStmt fireWith: foreach (var w in fireWith.With) declared.Add(w.VarName); break;
                }
            return declared;
        }

        // (every node below `node`, in no particular order)
        private static void Walk(object? node, List<object> into)
        {
            switch (node)
            {
                case null:
                case string:
                    return;
                case System.Collections.IEnumerable items:
                    foreach (var item in items) Walk(item, into);
                    return;
            }
            var type = node.GetType();
            if (type.IsPrimitive || type.IsEnum) return;
            bool isAst = type.Namespace != null && type.Namespace.StartsWith("fire.Ast", StringComparison.Ordinal);
            bool isTuple = type.IsGenericType && type.FullName!.StartsWith("System.ValueTuple", StringComparison.Ordinal);
            if (!isAst && !isTuple) return;
            into.Add(node);
            foreach (var m in MembersOf(type))
                Walk(m is System.Reflection.PropertyInfo pi ? pi.GetValue(node) : ((System.Reflection.FieldInfo)m).GetValue(node), into);
        }

        private static System.Reflection.MemberInfo[] MembersOf(Type type)
        {
            lock (Members)
            {
                if (!Members.TryGetValue(type, out var members))
                {
                    var list = new List<System.Reflection.MemberInfo>();
                    foreach (var p in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        if (p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract") list.Add(p);
                    foreach (var f in type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        list.Add(f);
                    Members[type] = members = list.ToArray();
                }
                return members;
            }
        }

        /// <summary>The names of <paramref name="names"/> that the body of a lambda only reads (ROADMAP_TASKS step 8): every use is a call of it, a
        /// read of one of its fields or elements that is consumed at once (an operand, a condition, `print`, a text), or the operand of an operator - never
        /// an assignment, a method call on it, an argument, a `return`, a `take` or an alias. A name the body declares again is not read-only.</summary>
        /// <summary>The variables a body assigns to (`x = ...`, `x += ...`, `x++`) - for the derivation of the type checker: a parameter it never assigns
        /// still holds what the call gave it.</summary>
        public static HashSet<string> Assigned(Stmt.BlockStmt body)
        {
            var all = new List<object>();
            Walk(body, all);
            var names = new HashSet<string>();
            foreach (var n in all)
                switch (n)
                {
                    case AssignExpr { Target: IdentifierExpr id }: names.Add(id.Name); break;
                    case IncDecExpr { Target: IdentifierExpr id }: names.Add(id.Name); break;
                    case VarDeclStmt v: names.Add(v.Name); break;   // (a variable of the same name hides the parameter somewhere)
                }
            return names;
        }

        public static HashSet<string> ReadOnly(LambdaExpr lambda, IEnumerable<string> names)
        {
            var all = new List<object>();
            Walk(lambda.Body, all);
            var declared = Declared(lambda.Body);
            var wanted = new HashSet<string>(names);
            wanted.ExceptWith(declared);
            var safe = new HashSet<object>(ReferenceEqualityComparer.Instance);
            // the root variable of a read `x`, `x.a.b`, `x[i]`
            IdentifierExpr? RootOf(Expr? e) => e switch
            {
                IdentifierExpr id => id,
                MemberExpr m => RootOf(m.Target),
                IndexExpr ix => RootOf(ix.Target),
                _ => null,
            };
            void Consumed(Expr? e) { if (RootOf(e) is { } root && wanted.Contains(root.Name)) safe.Add(root); }
            foreach (var n in all)
                switch (n)
                {
                    case BinaryExpr b: Consumed(b.Left); Consumed(b.Right); break;
                    case UnaryExpr { Op: UnaryOp.Negate or UnaryOp.LogicalNot or UnaryOp.BitNot } u: Consumed(u.Operand); break;
                    case ConditionalExpr c: Consumed(c.Condition); break;
                    case IfStmt i: Consumed(i.Condition); break;
                    case WhileStmt w: Consumed(w.Condition); break;
                    case InterpolatedStringExpr s: foreach (var part in s.Parts) if (part is InterpolationExprPart pe) Consumed(pe.Expression); break;
                    case IndexExpr ix: Consumed(ix.Index); break;
                    case CallExpr { Callee: IdentifierExpr callee } call:
                        if (wanted.Contains(callee.Name)) safe.Add(callee);   // (a call of the captured lambda)
                        else if (callee.Name == "print") foreach (var a in call.Args) Consumed(a);
                        break;
                }
            var result = new HashSet<string>(wanted);
            foreach (var n in all)
                if (n is IdentifierExpr id && result.Contains(id.Name) && !safe.Contains(id)) result.Remove(id.Name);
            return result;
        }

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
