using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Bytecode;
using fire.Lexing;
using fire.Resolving;
using fire.Runtime;
using fire.Values;
using MemoryPack;

namespace fire.Compiler
{
    /// <summary>An error of the bytecode compiler (a `NotSupportedException`,
    /// so that existing code that catches these keeps working) - unlike
    /// a bare NotSupportedException WITH a line in the source. As with the
    /// resolver (see ResolverException) the compiler does NOT stop at the first
    /// error, but collects all further ones: `Compiler.Compile`
    /// throws at the end ONE CompilerException whose `Message`/`Line` are those of the
    /// first error and whose <see cref="Errors"/> contains all errors
    /// (the first included).</summary>
    public sealed class CompilerException : NotSupportedException
    {
        public int Line { get; }

        public IReadOnlyList<CompilerException> Errors { get; }

        public CompilerException(string message, int line)
            : base($"{message} ({line})")
        {
            Line = line;
            Errors = new[] { this };
        }

        /// <summary>Combines several collected errors (at least one).</summary>
        public CompilerException(IReadOnlyList<CompilerException> errors)
            : base(errors[0].Message)
        {
            Line = errors[0].Line;
            Errors = errors;
        }
    }

    /// <summary>
    /// Translates the AST (after the resolver run) into a chunk. Currently covers:
    /// literals, variables (global/local matching the resolver slots),
    /// arithmetic incl. the anchor rule for units/types (SPEC 3.2),
    /// comparisons, short-circuit '&amp;&amp;'/'||', unary operators, if/while/for,
    /// blocks with a real ownership scope, native calls, lambdas (function/
    /// call frames), classes/objects (`new`, fields, methods incl. virtual
    /// resolution, `this`/`base`, constructor chaining).
    ///
    /// Still NOT covered (throws NotSupportedException with a clear message):
    /// try/catch/throw/resume, foreach (collections are not designed at all yet),
    /// destructor EXECUTION (declaration/compilation already, see
    /// RuntimeClass comment) - those are the next development stages.
    /// </summary>
    public sealed class Compiler
    {
        private readonly Chunk _chunk = new();
        private readonly IReadOnlyDictionary<Expr, ResolvedRef> _refs;
        private readonly NativeRegistry _natives;

        // -----------------------------------------------------------
        // break/continue: scope depth and active loops
        // -----------------------------------------------------------

        /// <summary>How many EnterScope opcodes have been emitted since the start of THIS
        /// function body without a matching ExitScope -
        /// NOT counted across function boundaries, since every method/every
        /// constructor/every lambda is compiled with a FRESH compiler object
        /// (see CompileMethodProto/CompileLambda: `new
        /// Compiler(_refs, _natives, ...)`) - this field therefore starts again at 0 for
        /// every function body automatically, without
        /// manual saving/resetting as with the resolver counterpart
        /// (`Resolver._loopDepth`). Changed exclusively via EmitEnterScope/
        /// EmitExitScope - NEVER call `_chunk.EmitOp(OpCode.
        /// Enter/ExitScope)` directly, otherwise `break`/`continue` lose the
        /// correct number of scopes that they have to close on the jump.</summary>
        private int _currentScopeDepth;

        /// <summary>Compiles the main program (not the body of a function, method or lambda).</summary>
        private bool IsTopLevelCode { get; set; }

        /// <summary>Per active loop (nestable, hence a stack):
        /// the scope depth EXACTLY on entering the loop body (for the
        /// number of ExitScope opcodes needed on a break/continue, see
        /// EmitScopeUnwindForJump) as well as the jump targets still to be patched.
        /// `continue` and `break` collect their jump addresses here until the
        /// respective target (loop start/end) is fixed when the
        /// loop has been compiled completely.</summary>
        private sealed class LoopCompileContext
        {
            public int ScopeDepthAtLoopBodyStart;
            /// <summary>`foreach` keeps its enumerator on the operand stack: a `return` in the middle of it has to remove it as well.</summary>
            public bool IsForeach;
            public readonly List<int> BreakJumpPatchAddrs = new();
            public readonly List<int> ContinueJumpPatchAddrs = new();
        }

        private readonly Stack<LoopCompileContext> _loopStack = new();

        private void EmitEnterScope()
        {
            _chunk.EmitOp(OpCode.EnterScope);
            _currentScopeDepth++;
        }

        private void EmitExitScope()
        {
            _chunk.EmitOp(OpCode.ExitScope);
            _currentScopeDepth--;
        }

        /// <summary>Before a break/continue jump emits as many
        /// ExitScope opcodes as necessary to get from the CURRENT scope depth
        /// back to the depth on entering the loop body -
        /// deliberately WITHOUT EmitExitScope (that would change `_currentScopeDepth`
        /// along with it): these are purely "temporary" closes ONLY for this
        /// one jump path, the NORMAL sequential compilation (e.g.
        /// the block's own ExitScope that contains the break/continue)
        /// afterwards continues unchanged as if nothing had happened - the
        /// bytecode directly after the jump is unreachable anyway (jump
        /// is unconditional), just as with `return` in the middle of a block.</summary>
        private void EmitScopeUnwindForJump(LoopCompileContext ctx)
        {
            int toClose = _currentScopeDepth - ctx.ScopeDepthAtLoopBodyStart;
            for (int i = 0; i < toClose; i++)
                _chunk.EmitOp(OpCode.ExitScope);
        }

        private enum TryPhase { Try, Catch }

        /// <summary>A `try` currently being compiled (try or catch part) - basis for `break`/`continue` clearing away the handler and running through the
        /// `finally`. `OuterDepth` = scope depth outside the `try`, `LoopCount` = loops that were already open on entering.</summary>
        private sealed class TryCompileContext
        {
            public TryStmt Stmt = null!;
            public TryPhase Phase;
            public int OuterDepth;
            public int LoopCount;
            /// <summary>Number of stack residents (see _residents) on entering the `try`: they lie below the state to which the handler resets the stack.</summary>
            public int ResidentsAtStart;

            /// <summary>Places (operands of `Jump`) that jump into the `finally` block and are still waiting for its address.</summary>
            public readonly List<int> FinallyJumpPatches = new();

            /// <summary>Per jump kind (`break`/`continue`) that leaves the `try`: the places (operands of `PushJump`) waiting for the address of the exit piece
            /// behind the `finally` (see CompileTry).</summary>
            public readonly List<int> BreakStubPatches = new();
            public readonly List<int> ContinueStubPatches = new();
        }

        private readonly List<TryCompileContext> _tryStack = new();

        /// <summary>Everything a block leaves on the operand stack while it runs (from bottom to top): a `foreach` its enumerator (1),
        /// a `finally` block its completion (2). A `return` in it removes them before returning (swap + pop per entry), otherwise they would stay
        /// below the return value and shift the caller's operands.</summary>
        private readonly List<int> _residents = new();

        /// <summary>Common compilation for `break`/`continue`: leaves from the inside out all `try`/`catch` blocks that have been open since the loop body
        /// (close scopes, unregister handlers or discard the catch state). If it meets a `try` WITH `finally` on the way, it does not jump on by itself,
        /// but into its `finally` (completion "jump"); after the `finally` an exit piece behind the `try` continues the journey (it is
        /// the same `break`/`continue`, only translated from outside the `try`, see CompileTry). Otherwise it closes the remaining scopes and jumps - the
        /// address goes into the matching list (Break-/ContinueJumpPatchAddrs) and is resolved when the loop is compiled completely.</summary>
        private void CompileBreakOrContinue(bool isBreak)
        {
            var ctx = _loopStack.Peek();
            int depth = _currentScopeDepth;      // actual depth at the jump; `_currentScopeDepth` itself stays unchanged

            for (int k = _tryStack.Count - 1; k >= 0 && _tryStack[k].LoopCount == _loopStack.Count; k--)
            {
                var t = _tryStack[k];
                int innerTarget = t.Phase == TryPhase.Catch ? t.OuterDepth + 1 : t.OuterDepth; // in the catch part first up to the catch scope
                for (; depth > innerTarget; depth--) _chunk.EmitOp(OpCode.ExitScope);

                if (t.Phase == TryPhase.Try)
                {
                    _chunk.EmitOp(OpCode.UnregisterHandler);
                }
                else
                {
                    // as at the normal end of the catch block: discard the throw-site state, close the catch scope, unregister the finally-only handler
                    _chunk.EmitOp(OpCode.LoadLocal); _chunk.EmitU16(0); _chunk.EmitU16(0);
                    _chunk.EmitOp(OpCode.ClearPendingResume);
                    _chunk.EmitOp(OpCode.ExitScope);
                    depth--;
                    if (t.Stmt.Finally != null) _chunk.EmitOp(OpCode.UnregisterHandler);
                }

                if (t.Stmt.Finally != null)
                {
                    // into the finally of this `try`: afterwards it continues at the exit piece behind the `try`
                    _chunk.EmitOp(OpCode.PushJump);
                    (isBreak ? t.BreakStubPatches : t.ContinueStubPatches).Add(_chunk.Here);
                    _chunk.EmitU16(0);
                    _chunk.EmitOp(OpCode.Jump);
                    t.FinallyJumpPatches.Add(_chunk.Here);
                    _chunk.EmitU16(0);
                    return;
                }
            }

            for (; depth > ctx.ScopeDepthAtLoopBodyStart; depth--) _chunk.EmitOp(OpCode.ExitScope);
            _chunk.EmitOp(OpCode.Jump);
            (isBreak ? ctx.BreakJumpPatchAddrs : ctx.ContinueJumpPatchAddrs).Add(_chunk.Here);
            _chunk.EmitU16(0);
        }

        // Only set while a field initialiser/method/constructor body
        // of this class is being compiled - basis for resolving `base.Method(...)`
        // statically to THE base class that belongs to the declaring class
        // (not to the actual runtime instance, which with multi-level
        // inheritance can be a different one).
        private readonly RuntimeClass? _enclosingClass;

        /// <summary>Number of global slots of the MAIN PROGRAM (see Resolving.
        /// Resolver.ResolveResult.GlobalSlotCount) - basis for
        /// CompileFireStmt: the fire block body runs on a FRESH
        /// VM instance whose global scope is first filled at runtime with a
        /// snapshot of ALL main-program globals at THEIR ORIGINAL slots
        /// (see Bytecode.VM.OpCode.Fire/Runtime.FireRuntime.
        /// FireVmTaking) - taking/with captures therefore get their
        /// OWN slots only FROM this value on, not from 0 (see Resolving.
        /// Resolver.ResolveFireStmt for the matching slot assignment). MUST
        /// be passed on through EVERY inner compiler (not only the one
        /// of CompileFireStmt itself) - a `fire {}` can after all also stand deeply
        /// nested inside a method/lambda/a further
        /// fire block.</summary>
        private readonly int _globalSlotCount;

        /// <summary>Source index (position in the `sources` list that went to
        /// Parser.ParseMultiple) for TOP-LEVEL code (that is, OUTSIDE
        /// any class, incl. a lambda defined directly there - see
        /// CompileLambda) - inside a class its
        /// OWN `Ast.ClassDecl.SourceIndex` applies instead (see CurrentSourceIndex).
        /// Set anew in the top-level loop of Compile() BEFORE every statement
        /// from `sourceIndexByStmt` (analogous to the earlier
        /// `_topLevelUsings` pattern) - important when SEVERAL of the combined
        /// sources have top-level code of their own.</summary>
        private int _topLevelSourceIndex;

        /// <summary>The source index applying to the place CURRENTLY being compiled
        /// (SPEC "Multiple source files") - inside a class its OWN
        /// `Ast.ClassDecl.SourceIndex`, outside any class
        /// `_topLevelSourceIndex` (see there). Passed to Chunk.MarkLine,
        /// so that a debugger (see editor sub-project) knows, with several
        /// source files, in which file a given line lies.</summary>
        private int CurrentSourceIndex => _enclosingClass?.Decl.Source ?? _topLevelSourceIndex;

        /// <summary>All known (fully qualified) class names - basis
        /// for ResolveTypeRef (SPEC "Namespaces"). Not readonly: the
        /// top-level compiler only gets it IN THE MIDDLE of CompileClasses
        /// assigned (after collecting ALL class names, but BEFORE
        /// actually compiling the class bodies - a chicken-and-egg problem
        /// if one were to assign it only AFTER CompileClasses, since
        /// CompileClasses itself already compiles class bodies that
        /// need it); every INNER compiler (method/constructor/lambda
        /// body), by contrast, gets it directly via the constructor from the outer
        /// compiler (by THAT time long set). `null` only in
        /// test scenarios that would never have to resolve a name.</summary>
        private HashSet<string>? _knownClassNames;

        private Compiler(ResolveResult resolveResult, NativeRegistry natives)
            : this(resolveResult.References, natives, null, resolveResult.NoShadowGlobals ? 0 : resolveResult.GlobalSlotCount, null, new List<CompilerException>(), RefParamTable.Build(resolveResult.Classes.Values))
        {
            IsTopLevelCode = true;
        }

        /// <summary>All errors found so far (see CompilerException) -
        /// ONE list for the outer compiler AND all inner ones (method/
        /// lambda/constructor bodies), evaluated by Compile() at the
        /// end.</summary>
        private readonly List<CompilerException> _errors;

        /// <summary>Which parameters of which methods/constructors are `ref` (SPEC 5.4.2) - the caller must know where to pass an address instead of a value.</summary>
        private readonly RefParamTable _refParams;

        /// <summary>The `ref` parameters of all methods and constructors of the program. A call (`obj.M(a, b)`) is only bound to a class at runtime
        /// - the caller therefore passes the address if ANY method of this name (and this argument count) has `ref` there; the VM
        /// gives an ordinary parameter the value when binding.</summary>
        private sealed class RefParamTable
        {
            private readonly Dictionary<(string Name, int Argc), bool[]> _methods = new();
            private readonly Dictionary<(string Class, int Argc), bool[]> _constructors = new();

            public static RefParamTable Build(IEnumerable<ClassDecl> classes)
            {
                var table = new RefParamTable();
                // ALL methods take part (also those without `ref`), so that a contradiction between classes becomes apparent
                foreach (var cls in classes)
                    foreach (var member in cls.Members)
                    {
                        if (member is MethodDecl md) table.Register(table._methods, md.Name, md.Params, md.Line, $"method '{md.Name}'");
                        else if (member is ConstructorDecl cd) table.Register(table._constructors, cls.Name, cd.Params, cd.Line, $"the constructor of '{cls.Name}'");
                    }
                return table;
            }

            private void Register(Dictionary<(string, int), bool[]> map, string key, IReadOnlyList<LambdaParam> parms, int line, string what)
            {
                int required = parms.TakeWhile(p => p.DefaultValue == null).Count();
                for (int argc = required; argc <= parms.Count; argc++)
                {
                    var mask = parms.Take(argc).Select(p => p.ByRef).ToArray();
                    if (map.TryGetValue((key, argc), out var existing))
                        for (int i = 0; i < argc; i++) existing[i] |= mask[i];
                    else map[(key, argc)] = mask;
                }
            }

            /// <summary>The `ref` mask of a method (null: no parameter is `ref`).</summary>
            public bool[]? ForMethod(string name, int argc) => _methods.TryGetValue((name, argc), out var m) && m.Any(x => x) ? m : null;
            public bool[]? ForConstructor(string cls, int argc) => _constructors.TryGetValue((cls, argc), out var m) && m.Any(x => x) ? m : null;
        }

        /// <summary>Does the program use the reflection library (`#import "reflection"`)? Then the compiler writes the declared types along as
        /// <see cref="ClassMeta"/> and marks the classes of the library.</summary>
        private bool Reflection => _natives.Has(fire.Standard.ReflectionPrelude.MembersNative);

        /// <summary>For compiling a lambda/method/constructor body
        /// into a chunk of its own (FunctionProto): shares the resolver
        /// references and the native registry with the outer compiler, but builds
        /// a fresh chunk of its own.</summary>
        private Compiler(IReadOnlyDictionary<Expr, ResolvedRef> refs, NativeRegistry natives, RuntimeClass? enclosingClass, int globalSlotCount, HashSet<string>? knownClassNames, List<CompilerException> errors, RefParamTable refParams)
        {
            _refParams = refParams;
            _errors = errors;
            _refs = refs;
            _natives = natives;
            _enclosingClass = enclosingClass;
            _globalSlotCount = globalSlotCount;
            _knownClassNames = knownClassNames;
        }

        /// <summary>Resolves `tr` to its fully qualified name IF
        /// necessary (SPEC "Namespaces") - see TypeRef.ResolveBaseName for the
        /// exact rule. `tr.Namespaces` carries the context (current
        /// namespace + `#using`) already directly on itself, set by the
        /// parser EXACTLY at the place where `tr` was parsed - the
        /// compiler needs no "current class"/"active
        /// usings" state of its own for that any more.</summary>
        private string ResolveTypeRef(TypeRef tr) =>
            _knownClassNames != null ? tr.ResolveBaseName(_knownClassNames.Contains) : tr.BaseName;

        /// <summary>The class name that `new Name&lt;...&gt;(...)` instantiates - the
        /// number of type arguments chooses between a non-generic and
        /// a generic class of the same name (see GenericClassNames;
        /// same choice as in the resolver).</summary>
        private string ResolveNewClassName(NewExpr ne) =>
            _knownClassNames != null
                ? GenericClassNames.ResolveNewTarget(ne.ClassRef, ne.TypeArgs?.Count ?? 0, _knownClassNames.Contains)
                : ne.ClassRef.BaseName;

        public static CompiledProgram Compile(
            IReadOnlyList<Stmt> program, ResolveResult resolveResult, NativeRegistry natives)
        {
            var compiler = new Compiler(resolveResult, natives);
            var classes = compiler.CompileClasses(program);

            // `#nosync` (no matter where in the top-level code it stands): right at the start, before any code runs
            if (program.Any(s => s is NoSyncDirective))
            {
                compiler._chunk.EmitOp(OpCode.SetAutoSync);
                compiler._chunk.EmitByte(0);
            }

            // `#timeout value`: likewise right at the start (the value is an expression without reference to variables, e.g. `10s`)
            foreach (var timeout in program.OfType<TimeoutDirective>())
            {
                compiler.CompileExpr(timeout.Value);
                compiler._chunk.EmitOp(OpCode.SetTimeout);
            }

            // SPEC "Static members": static field initialisers
            // run EXACTLY ONCE, before the actual program (unlike
            // instance fields, which run anew on EVERY `new` construction) -
            // emitted directly here at the START of the TopLevel chunk, so that
            // they run exactly once, in class/field-declaration
            // order, before the actual user code begins.
            // Dummy 'this' (undefined): the resolver forbids 'this'/'super'
            // in a static field initialiser (see ResolveExpr/
            // ThisExpr), so the dummy is never actually read -
            // needed only because CallProtoWithThis (the same opcode as for
            // instance field initialisers) ALWAYS expects a 'this'
            // on the stack.
            foreach (var rc in classes.Values)
            {
                foreach (var (fieldName, initProto) in rc.StaticFields)
                {
                    int protoIdx = compiler._chunk.AddFunctionProto(initProto);
                    compiler.EmitLoadConst(Value.MakeUndefined());
                    compiler._chunk.EmitOp(OpCode.CallProtoWithThis);
                    compiler._chunk.EmitU16(protoIdx);
                    compiler._chunk.EmitByte(0);
                    // SetStaticFieldOnInit instead of SetStaticField - NO
                    // access modifier check (see the documentation there), otherwise
                    // a PRIVATE static field would be rejected already on its
                    // own initialisation (which runs as
                    // top-level code, without an OwnerClass context
                    // matching its own class). Unlike SetStaticField it also pushes
                    // nothing back, no pop needed.
                    compiler._chunk.EmitOp(OpCode.SetStaticFieldOnInit);
                    compiler._chunk.EmitU16(compiler._chunk.AddConstant(Value.MakeString(rc.Name)));
                    compiler._chunk.EmitU16(compiler._chunk.AddConstant(Value.MakeString(fieldName)));
                }
            }

            foreach (var stmt in program)
            {
                compiler._topLevelSourceIndex = stmt.Source;
                compiler.CompileStmt(stmt);
            }
            compiler._chunk.EmitOp(OpCode.Halt);

            // From the first error on it is certain that there is no result -
            // but only HERE, after everything has been compiled, so that the
            // caller gets ALL errors at once (see CompilerException).
            if (compiler._errors.Count > 0)
                throw new CompilerException(compiler._errors);

            var externSignatures = new Dictionary<string, ExternSignature>();
            foreach (var (name, ed) in resolveResult.Externs)
            {
                externSignatures[name] = new ExternSignature
                {
                    LibName = ed.LibName,
                    ParamTypes = ed.Params.Select(p => p.Type).ToList(),
                    ReturnType = ed.ReturnType,
                };
            }

            return new CompiledProgram { TopLevel = compiler._chunk, Classes = classes, ExternSignatures = externSignatures };
        }

        // -----------------------------------------------------------
        // Classes (pre-pass: name -> RuntimeClass, analogous to the resolver)
        // -----------------------------------------------------------
        /// <summary>The type as it stood in the source (`int`, `Circle`, `lambda<int>`, `float[]`), "" if not given.</summary>
        private static string TypeText(TypeRef? type, int extraArrayRank = 0)
        {
            if (type == null || type.IsInferred) return "";
            string text = type.LambdaSignature is { IsSelector: true } sel ? "lambda " + sel.SelectorKind + "<" + string.Join(", ", sel.ParamTypeNames) + ">" : type.ToString();
            return text + string.Concat(Enumerable.Repeat("[]", type.ArrayRank + extraArrayRank));
        }

        private static string AccessText(AccessModifier access) => access switch
        {
            AccessModifier.Private => "private",
            AccessModifier.Protected => "protected",
            _ => "public",
        };

        /// <summary>The reflection metadata of a class: what the runtime otherwise does not keep (type names, parameter names, `readonly`, property form).</summary>
        private static ClassMeta BuildClassMeta(ClassDecl cd)
        {
            var meta = new ClassMeta();
            if (cd.BaseRefs != null)
                foreach (var b in cd.BaseRefs) meta.BaseNames.Add(b.BaseName);

            static void AddParams(MemberMeta m, IReadOnlyList<LambdaParam> parms)
            {
                foreach (var p in parms)
                {
                    m.ParamNames.Add(p.Name);
                    m.ParamTypes.Add(TypeText(p.Type, p.ArrayRanks.Count));
                }
            }

            foreach (var member in cd.Members)
            {
                switch (member)
                {
                    case FieldDecl f:
                        meta.Members.Add(new MemberMeta
                        {
                            Name = f.Name, Kind = "field", TypeName = TypeText(f.Type, f.ArrayRanks.Count), Access = AccessText(f.Access),
                            IsStatic = f.IsStatic, IsReadonly = f.IsReadonly, Unit = f.Type?.Unit ?? "",
                        });
                        break;
                    case PropertyDecl pd:
                        meta.Members.Add(new MemberMeta
                        {
                            Name = pd.Name, Kind = "property", TypeName = TypeText(pd.Type), Access = AccessText(pd.Access),
                            IsStatic = pd.IsStatic, CanRead = pd.Getter != null, CanWrite = pd.Setter != null, Unit = pd.Type?.Unit ?? "",
                        });
                        break;
                    case MethodDecl md:
                        {
                            var m = new MemberMeta { Name = md.Name, Kind = "method", TypeName = TypeText(md.ReturnType), Access = AccessText(md.Access), IsStatic = md.IsStatic };
                            AddParams(m, md.Params);
                            meta.Members.Add(m);
                            break;
                        }
                    case ConstructorDecl ctor:
                        {
                            var m = new MemberMeta { Name = cd.Name, Kind = "constructor", Access = AccessText(ctor.Access) };
                            AddParams(m, ctor.Params);
                            meta.Members.Add(m);
                            break;
                        }
                }
            }
            return meta;
        }

        private Dictionary<string, RuntimeClass> CompileClasses(IReadOnlyList<Stmt> program)
        {
            var classes = new Dictionary<string, RuntimeClass>();
            foreach (var stmt in program)
                if (stmt is ClassDecl cd)
                {
                    classes[cd.Name] = new RuntimeClass(cd.Name, cd);
                    if (Reflection)
                    {
                        classes[cd.Name].Meta = BuildClassMeta(cd);
                        classes[cd.Name].IsReflectionHelper = fire.Standard.ReflectionPrelude.HelperClasses.Contains(cd.Name);
                    }
                }

            // Base linking separately, since base classes can stand in the source
            // later than the derived class (forward reference). The
            // resolver has already checked that at most one name in BaseRefs
            // is a real class - so here simply search for the first such name.
            // 'Exception' (base class, SPEC 7.1) is a class of the prelude. Without one (a program
            // compiled without the prelude) classes with ': Exception' get simply Base = null here
            // (their own fields/methods/constructors work nevertheless; on throwing/catching
            // the type name "Exception" always matches anyway, see VM.ExceptionMatchesType).
            // Interface names in BaseRefs are ignored here - interfaces
            // need no runtime representation of their own (purely dynamic
            // method call by name), the resolver has already checked fulfilment.
            var interfaceNames = new HashSet<string>(program.OfType<InterfaceDecl>().Select(i => i.Name));
            foreach (var rc in classes.Values)
            {
                foreach (var baseRef in rc.Decl.BaseRefs ?? Array.Empty<TypeRef>())
                {
                    bool Known(string name) => name == "Exception" || classes.ContainsKey(name) || interfaceNames.Contains(name);
                    string n = baseRef.TypeArgCount == 0 ? baseRef.ResolveBaseName(Known) : GenericClassNames.ResolveNewTarget(baseRef, baseRef.TypeArgCount, Known);
                    if (interfaceNames.Contains(n)) { rc.Interfaces.Add(n); continue; }
                    if (n != "Exception" && !classes.TryGetValue(n, out _)) continue;
                    if (n == "Exception" && !classes.ContainsKey(n)) break; // no RuntimeClass available (a program without the prelude) -> Base stays null
                    rc.Base = classes[n];
                    break;
                }
            }

            // RuntimeClass.IsActor is now a real field instead of a
            // computed property (see the documentation there - because of Decl.
            // [MemoryPackIgnore] for the planned serialisation), computed here
            // ONCE after the base-class linking above -
            // the same logic as the former property, only as an explicit
            // chain walk instead of recursive property access (independent of
            // the iteration order above: every class walks its OWN
            // chain, so it does not need Base to have been preprocessed already).
            foreach (var rc in classes.Values)
            {
                bool isActor = false;
                for (var walk = rc; walk != null; walk = walk.Base)
                    if (walk.Decl.IsActor) { isActor = true; break; }
                rc.IsActor = isActor;
            }

            // Only NOW (after collecting ALL class names, but BEFORE
            // actually compiling the class bodies below) - ResolveTypeRef
            // needs the set of ALL class names, which is complete for the first
            // time exactly HERE. A chicken-and-egg problem if one were to assign it
            // instead AFTER this whole method: the
            // body compilation below (CompileClassBody, among others `new X()`
            // INSIDE methods) needs it after all WHILE this
            // method is still running, not only afterwards.
            _knownClassNames = new HashSet<string>(classes.Keys);

            foreach (var rc in classes.Values)
                CompileClassBody(rc);

            return classes;
        }

        private void CompileClassBody(RuntimeClass rc)
        {
            var ctorDecls = new List<ConstructorDecl>();

            foreach (var member in rc.Decl.Members)
            {
                // Every member is a restart point of its own (see
                // CompileStmt) - an error in one field/method does
                // not prevent the compilation of the other members.
                try
                {
                    switch (member)
                    {
                        case FieldDecl fd:
                        {
                            var fieldInit = CompileFieldInitProto(rc, fd.Type, fd.Initializer, fd.IsStatic);
                            rc.OwnFieldInfo[fd.Name] = new FieldInfo()
                            {
                                AccessModifier = fd.Access,
                                RequiredUnit = fd.Type?.Unit,
                                IsStatic = fd.IsStatic,
                            };
                            // SPEC "Static members": static fields end up
                            // NOT in Fields (the instance init list that EVERY
                            // `new` construction runs through again) - instead
                            // in StaticFields, evaluated ONCE at program start
                            // (see RunStaticInitializers, called
                            // directly after CompileClasses in Compile()).
                            if (fd.IsStatic)
                                rc.StaticFields.Add((fd.Name, fieldInit));
                            else
                                rc.Fields.Add((fd.Name, fieldInit));
                            // SPEC "Unit declarations": this is checked
                            // NOT here on initialising (see
                            // CompileFieldInitProto - unchanged), but
                            // directly in the VM on EVERY SetField/SetStaticField
                            // call - field assignments are (unlike local/
                            // global variables) fundamentally resolved
                            // dynamically, the VM knows the
                            // actual class of the target object at runtime, the compiler
                            // at this point does not.
                            break;
                        }

                        case MethodDecl md:
                            rc.AddMethod(md.Name, CompileMethodProto(rc, md.Params, md.Body, md.Access, md.IsStatic));
                            break;

                        case ConstructorDecl ctor:
                            ctorDecls.Add(ctor);
                            break;

                        case DestructorDecl dtor:
                            rc.Destructor = CompileMethodProto(rc, Array.Empty<LambdaParam>(), dtor.Body, AccessModifier.Public);
                            break;

                        case PropertyDecl pd:
                            // Naming convention 'get_'/'set_' (see Ast.PropertyDecl
                            // documentation) - registered as quite ordinary methods, VM.
                            // GetField/SetField call them by naming convention
                            // if no field of the same name exists. Both accessors
                            // share the ONE modifier of the property itself
                            // (SPEC knows no separate get/set modifiers) -
                            // likewise they share the ONE IsStatic (SPEC knows
                            // no mixed static/non-static accessors).
                            if (pd.Getter != null)
                                rc.AddMethod("get_" + pd.Name, CompileMethodProto(rc, Array.Empty<LambdaParam>(), pd.Getter, pd.Access, pd.IsStatic));
                            if (pd.Setter != null)
                            {
                                var setterParams = new[] { new LambdaParam("value", pd.Type, Array.Empty<Expr?>()) };
                                rc.AddMethod("set_" + pd.Name, CompileMethodProto(rc, setterParams, pd.Setter, pd.Access, pd.IsStatic));
                            }
                            break;
                    }
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
                {
                    _errors.Add(ex as CompilerException ?? new CompilerException(ex.Message, member.Line));
                }
            }

            // No declaration of its own -> exactly ONE synthesised 0-arg public
            // constructor (base call + field inits, otherwise empty) - `new`
            // therefore always works uniformly through the same
            // mechanism. With declarations of their own: ONE overload per
            // `construct(...)` (the resolver has already checked that no
            // two have the same parameter count).
            if (ctorDecls.Count == 0)
            {
                rc.AddConstructor(CompileConstructorProto(rc, null, AccessModifier.Public));
            }
            else
            {
                foreach (var ctor in ctorDecls)
                {
                    try
                    {
                        rc.AddConstructor(CompileConstructorProto(rc, ctor, ctor.Access));
                    }
                    catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
                    {
                        _errors.Add(ex as CompilerException ?? new CompilerException(ex.Message, ctor.Line));
                    }
                }
            }
        }

        /// <summary>Compiles for every parameter with a default value an
        /// 0-arg proto of its own that evaluates its DefaultValue expression
        /// (see FunctionProto.ParamDefaults documentation) - null at that place for
        /// mandatory parameters. Runs in the same compiler context (`rc`) as the
        /// actual method/the constructor, so that e.g. `this.field` works as a
        /// default value.</summary>
        private FunctionProto?[] CompileParamDefaults(RuntimeClass? rc, IReadOnlyList<LambdaParam> parms)
        {
            var defaults = new FunctionProto?[parms.Count];
            for (int i = 0; i < parms.Count; i++)
            {
                if (parms[i].DefaultValue == null) continue;
                var inner = new Compiler(_refs, _natives, rc, _globalSlotCount, _knownClassNames, _errors, _refParams);
                inner.CompileExpr(parms[i].DefaultValue!);
                inner._chunk.EmitOp(OpCode.Return);
                defaults[i] = new FunctionProto(inner._chunk, 0, AccessModifier.Private);
            }
            return defaults;
        }

        /// <summary>Does the initialiser proto consist only of `LoadConst c; Return` (a literal or no initialiser: `undefined`)?
        /// Then it returns `c` without any side effect, and the constructor can set `c` directly instead of calling it.</summary>
        private static bool TryGetConstantInitializer(FunctionProto proto, out Value constant)
        {
            constant = default;
            var code = proto.Chunk.Code;
            if (code.Count != 4 || code[0] != (byte)OpCode.LoadConst || code[3] != (byte)OpCode.Return) return false;
            int idx = code[1] | (code[2] << 8);
            if (idx >= proto.Chunk.Constants.Count) return false;
            constant = proto.Chunk.Constants[idx];
            return true;
        }

        private FunctionProto CompileFieldInitProto(RuntimeClass rc, TypeRef? type, Expr? initializer, bool isStatic = false)
        {
            var inner = new Compiler(_refs, _natives, rc, _globalSlotCount, _knownClassNames, _errors, _refParams);
            inner._chunk.OwnerClass = rc;
            if (initializer != null)
            {
                // An instance field with `new X()`/`flat x`/`copy x` as initialiser: the new object belongs to the instance
                // (`this` is bound during evaluation, see CallProtoWithThis), not the initialiser scope - otherwise
                // it would be destroyed after the constructor while the field points to it (SPEC 2.1).
                if (!isStatic && IsOwnedCreation(initializer))
                {
                    inner._chunk.EmitOp(OpCode.LoadThis);
                    inner.TryCompileOwnedCreation(initializer);
                }
                else
                {
                    inner.CompileExpr(initializer);
                }
                inner.EmitCheckLambdaSignatureIfNeeded(type);
            }
            else inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);
            return new FunctionProto(inner._chunk, 0, AccessModifier.Private);
        }

        /// <summary>Emits for every parameter with a lambda-signature
        /// type annotation (`lambda&lt;P1,...,Pn&gt;`, see Ast.TypeRef.
        /// LambdaSignature) a runtime check RIGHT AT THE START of the
        /// function body (`inner`) - the parameter slots are at this
        /// point already filled by the CALLER (see VM.CallMethod/
        /// NewObject/Call - parameter binding happens there BEFORE the jump into
        /// this chunk, not inside its own bytecode), so the
        /// check simply reads the value back via LoadLocal, checks
        /// it (CheckLambdaSignature) and discards the copy again (Pop) -
        /// the actual slot value stays untouched.</summary>
        private static uint RefMaskOf(IReadOnlyList<LambdaParam> parms)
        {
            uint mask = 0;
            for (int i = 0; i < parms.Count; i++)
                if (parms[i].ByRef)
                {
                    if (i >= 16) throw new NotSupportedException("Only the first 16 parameters of a method can be 'ref'.");
                    mask |= 1u << i;
                }
            return mask;
        }

        private void EmitLambdaParamChecks(Compiler inner, IReadOnlyList<LambdaParam> parms)
        {
            for (int i = 0; i < parms.Count; i++)
                if (parms[i].ByRef)
                {
                    inner._chunk.EmitOp(OpCode.RequireRefParam);
                    inner._chunk.EmitU16(i);
                    inner._chunk.EmitU16(inner._chunk.AddConstant(Value.MakeString(parms[i].Name)));
                }

            for (int i = 0; i < parms.Count; i++)
            {
                var sig = parms[i].Type?.LambdaSignature;
                if (sig == null) continue;
                if (sig.IsSelector)
                {
                    // `lambda member<T> name` (and field/property/selector): the parameter is replaced by the reflection of the chosen member
                    // replaced: name = Reflect.SelectorOf(name, "member")
                    inner._chunk.EmitOp(OpCode.LoadLocal);
                    inner._chunk.EmitU16(0);
                    inner._chunk.EmitU16((ushort)i);
                    inner.EmitLoadConst(Value.MakeString(sig.SelectorKind));
                    inner._chunk.EmitOp(OpCode.CallStaticMethod);
                    inner._chunk.EmitU16(inner._chunk.AddConstant(Value.MakeString("Reflect")));
                    inner._chunk.EmitU16(inner._chunk.AddConstant(Value.MakeString("SelectorOf")));
                    inner._chunk.EmitByte(2);
                    inner._chunk.EmitOp(OpCode.StoreLocal);
                    inner._chunk.EmitU16(0);
                    inner._chunk.EmitU16((ushort)i);
                    inner._chunk.EmitOp(OpCode.Pop);
                    continue;
                }
                inner._chunk.EmitOp(OpCode.LoadLocal);
                inner._chunk.EmitU16(0);
                inner._chunk.EmitU16((ushort)i);
                if (parms[i].ByRef) inner._chunk.EmitOp(OpCode.PtrRead);
                inner._chunk.EmitOp(OpCode.CheckLambdaSignature);
                inner._chunk.EmitByte((byte)sig.ParamTypeNames.Count);
                inner._chunk.EmitOp(OpCode.Pop);
            }

            // SPEC "Unit declarations": a parameter with an explicit
            // `: unit` (see TypeRef.Unit) demands on the actual
            // call EXACTLY this unit in the passed value - the same
            // LoadLocal+check+pop technique as above for the lambda signature,
            // only with CheckUnit instead of CheckLambdaSignature (see
            // EmitCheckUnitIfNeeded).
            for (int i = 0; i < parms.Count; i++)
            {
                string? unit = parms[i].Type?.Unit;
                if (unit == null) continue;
                inner._chunk.EmitOp(OpCode.LoadLocal);
                inner._chunk.EmitU16(0);
                inner._chunk.EmitU16((ushort)i);
                if (parms[i].ByRef) inner._chunk.EmitOp(OpCode.PtrRead);
                EmitCheckUnitIfNeeded(inner, unit);
                inner._chunk.EmitOp(OpCode.Pop);
            }
        }

        /// <summary>Emits (if `unit` != null) a CheckUnit opcode for
        /// the value that lies CURRENTLY on top of the stack (SPEC "Unit
        /// declarations") - checks (in the VM) whether its unit exactly
        /// matches `unit`, otherwise throws a `UnitMismatchException`
        /// (see VM.ThrowUnitMismatch). Only peeks (see OpCode.CheckUnit
        /// documentation) - the caller itself decides whether/when it
        /// still needs the value afterwards or pops it.</summary>
        private static void EmitCheckUnitIfNeeded(Compiler target, string? unit)
        {
            if (unit == null) return;
            target._chunk.EmitOp(OpCode.CheckUnit);
            target._chunk.EmitU16(target._chunk.AddConstant(Value.MakeString(unit)));
        }

        /// <summary>`fire { ... }`/`fire taking X { ... }` (see Ast.FireStmt
        /// documentation) - compiles the body as an OWN, isolated chunk (0 or 1
        /// parameter, depending on whether `taking` is used - the parameter
        /// is however NOT filled via the normal call convention,
        /// but directly by Runtime.FireRuntime.FireVmTaking via
        /// `scope.DefineSlot(...)`, BEFORE the chunk starts to run - see
        /// there). At the fire statement itself (if `taking` is used)
        /// the CURRENT value of the source variable in the CALLING context
        /// is evaluated and put on the stack, then the new `Fire` opcode
        /// is emitted, which pops it and starts a real thread.</summary>
        /// <summary>The order MUST agree with Resolver.ResolveFireStmt (slot
        /// assignment) and the VM.OpCode.Fire handler (pop order, there
        /// reversed, because of the stack): all TakingCaptures first
        /// (in list order), then with.</summary>
        /// <summary>The order MUST agree with Resolver.ResolveFireStmt (slot
        /// assignment: first main-program globals shadows, then taking, then
        /// with) and the VM.OpCode.Fire handler (pop order, there
        /// reversed, because of the stack). The chunk body itself
        /// references the main-program globals (resolved as ordinary
        /// `Global(slot)` references at THEIR ORIGINAL slots, see
        /// ResolveFireStmt) just like every taking/with capture -
        /// so nothing special to compile here, only the
        /// TakingCaptures/with slots must be assigned from `_globalSlotCount` (instead of from
        /// 0), so that they do not
        /// collide with the shadow slots.</summary>
        private void CompileFireStmt(FireStmt fs)
        {
            var inner = new Compiler(_refs, _natives, null, _globalSlotCount, _knownClassNames, _errors, _refParams);
            int slot = _globalSlotCount;
            foreach (var capture in fs.TakingCaptures)
                inner._chunk.MarkLocalName(0, slot++, capture.VarName);
            if (fs.WithVarName != null)
                inner._chunk.MarkLocalName(0, slot++, fs.WithVarName);
            foreach (var stmt in fs.Body.Statements) inner.CompileStmt(stmt);
            // BUGFIX: NOT 'LoadConst Undefined; Return' as with a
            // real method (CompileMethodProto) - a fire block runs as the
            // own, topmost level of a FRESH VM instance (see Runtime.
            // FireRuntime.FireVmTaking: `new VM(fireProto.Chunk, ...)`,
            // directly as its _currentChunk, NOT called via CallMethod)
            // - there is at NO time a
            // CallFrame there. `Return`'s handler, however, pops unchecked from
            // _frames (a Stack<CallFrame>) - with an empty stack
            // that throws a "Stack empty." exception EXACTLY on reaching
            // the end of the chunk. That stayed unnoticed for a long time, because
            // FireRuntime.Fire catches every exception from the thread body itself
            // (in FireThreadHandle.Error) and in the language syntax
            // nobody ever checks this handle - the fire thread "worked"
            // outwardly (everything BEFORE the end of the chunk ran normally after all), but
            // died silently with this exception at the end every time. `Halt`
            // (as with the top-level program itself, see Compiler.Compile)
            // ends the VM correctly, by contrast, without any frame expectation.
            inner._chunk.EmitOp(OpCode.Halt);

            var proto = new FunctionProto(inner._chunk, slot, AccessModifier.Private);
            int protoIdx = _chunk.AddFunctionProto(proto);

            foreach (var capture in fs.TakingCaptures)
                CompileExpr(capture.Source);
            bool hasWith = fs.WithSource != null;
            if (hasWith) CompileExpr(fs.WithSource!);

            _chunk.EmitOp(OpCode.Fire);
            _chunk.EmitU16(protoIdx);
            _chunk.EmitU16(_globalSlotCount);
            _chunk.EmitByte((byte)fs.TakingCaptures.Count);
            _chunk.EmitByte(hasWith ? (byte)1 : (byte)0);
        }

        /// <summary>`catch threads(ExceptionType e) { ... }` / `catch threads() { ... }`
        /// (see Ast.CatchThreadsDecl documentation) - compiles the body as an
        /// own, isolated chunk (0 or 1 parameter, depending on whether a
        /// variable is bound), then registers it via the new opcode
        /// GLOBAL (Bytecode.GlobalHandlers) - runs later nested in the
        /// respective delivering VM instance (see VM.
        /// HandleDeliveredThreadException), not here at this place.</summary>
        private void CompileCatchThreadsDecl(CatchThreadsDecl decl)
        {
            var inner = new Compiler(_refs, _natives, null, _globalSlotCount, _knownClassNames, _errors, _refParams);
            if (decl.VarName != null)
                inner._chunk.MarkLocalName(0, 0, decl.VarName);
            foreach (var stmt in decl.Body.Statements) inner.CompileStmt(stmt);
            inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);

            var proto = new FunctionProto(inner._chunk, decl.VarName != null ? 1 : 0, AccessModifier.Public);
            int protoIdx = _chunk.AddFunctionProto(proto);

            _chunk.EmitOp(OpCode.RegisterThreadsCatch);
            _chunk.EmitU16(protoIdx);
            bool hasType = decl.TypeRef != null;
            _chunk.EmitByte(hasType ? (byte)1 : (byte)0);
            if (hasType) _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ResolveTypeRef(decl.TypeRef!))));
        }

        /// <summary>`catch terminate(v) { ... }` (see Ast.CatchTerminateDecl
        /// documentation) - like CompileCatchThreadsDecl, but without a type name (there is after all
        /// no "kind" of terminate) and with the other register opcode.</summary>
        private void CompileCatchTerminateDecl(CatchTerminateDecl decl)
        {
            var inner = new Compiler(_refs, _natives, null, _globalSlotCount, _knownClassNames, _errors, _refParams);
            if (decl.VarName != null)
                inner._chunk.MarkLocalName(0, 0, decl.VarName);
            foreach (var stmt in decl.Body.Statements) inner.CompileStmt(stmt);
            inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);

            var proto = new FunctionProto(inner._chunk, decl.VarName != null ? 1 : 0, AccessModifier.Public);
            int protoIdx = _chunk.AddFunctionProto(proto);

            _chunk.EmitOp(OpCode.RegisterTerminateCatch);
            _chunk.EmitU16(protoIdx);
        }

        private FunctionProto CompileMethodProto(RuntimeClass? rc, IReadOnlyList<LambdaParam> parms, Stmt.BlockStmt body, AccessModifier access, bool isStatic = false)
        {
            var inner = new Compiler(_refs, _natives, rc, _globalSlotCount, _knownClassNames, _errors, _refParams);
            inner._chunk.OwnerClass = rc;
            // SPEC "Static members": a static method has no
            // bound 'this' - the inner compiler remembers that in order to
            // reject 'this'/'super' in the body (see resolver instead of
            // compiler: the check itself runs in the resolver, BEFORE
            // compiling, via ResolveResult - mentioned here only for
            // completeness, no check of its own needed at this point).
            for (int i = 0; i < parms.Count; i++)
                inner._chunk.MarkLocalName(0, i, parms[i].Name);
            EmitLambdaParamChecks(inner, parms);
            foreach (var stmt in body.Statements) inner.CompileStmt(stmt);
            inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);
            return new FunctionProto(inner._chunk, parms.Count, access, CompileParamDefaults(rc, parms), isStatic) { RefMask = RefMaskOf(parms) };
        }

        /// <summary>Constructor proto: [base constructor call (explicit with
        /// `: base(...)` or implicit without arguments, if a base class
        /// exists)] -> [own field initialisers] -> [own body] ->
        /// implicit `return undefined`. Also synthesised if the
        /// class declares no `construct` of its own (then only base call +
        /// field inits, 0 parameters) - `new` therefore always works
        /// uniformly through the same mechanism.</summary>
        private FunctionProto CompileConstructorProto(RuntimeClass rc, ConstructorDecl? ctor, AccessModifier access)
        {
            var inner = new Compiler(_refs, _natives, rc, _globalSlotCount, _knownClassNames, _errors, _refParams);
            inner._chunk.OwnerClass = rc;

            if (ctor != null)
            {
                for (int i = 0; i < ctor.Params.Count; i++)
                    inner._chunk.MarkLocalName(0, i, ctor.Params[i].Name);
                EmitLambdaParamChecks(inner, ctor.Params);
            }

            if (rc.Base != null)
            {
                var baseArgs = ctor?.BaseArgs;
                ulong baseCopyMask = baseArgs != null ? inner.CompileArgs(baseArgs, scopeCreating: true, _refParams.ForConstructor(rc.Base.Name, baseArgs.Count)) : 0;
                inner.EmitCopyArgsPrefix(baseCopyMask);

                inner._chunk.EmitOp(OpCode.ConstructBase);
                inner._chunk.EmitU16(inner._chunk.AddConstant(Value.MakeString(rc.Base.Name)));
                inner._chunk.EmitByte((byte)(baseArgs?.Count ?? 0));
                inner._chunk.EmitOp(OpCode.Pop); // discard the placeholder return value of the base constructor
            }

            // A field with a constant (or missing) initialiser needs no call of the initialiser proto: the value is loaded directly.
            // A field without an initialiser is `undefined` after allocation anyway and needs nothing at all - unless something could already
            // access `this` beforehand (the constructor of a base class, the initialiser of an earlier field with a call):
            // then the explicit `undefined` resets a field written there as before.
            bool fieldsMayBeTouched = rc.Base != null;
            foreach (var (fieldName, initProto) in rc.Fields)
            {
                if (TryGetConstantInitializer(initProto, out var constant))
                {
                    if (!fieldsMayBeTouched && constant.Kind == ValueKind.Undefined) continue;
                    inner.EmitLoadConst(constant);
                    inner._chunk.EmitOp(OpCode.SetFieldOnThis);
                    inner._chunk.EmitU16(inner._chunk.AddConstant(Value.MakeString(fieldName)));
                    continue;
                }

                int protoIdx = inner._chunk.AddFunctionProto(initProto);
                inner._chunk.EmitOp(OpCode.LoadThis);
                inner._chunk.EmitOp(OpCode.CallProtoWithThis);
                inner._chunk.EmitU16(protoIdx);
                inner._chunk.EmitByte(0);
                inner._chunk.EmitOp(OpCode.SetFieldOnThis);
                inner._chunk.EmitU16(inner._chunk.AddConstant(Value.MakeString(fieldName)));
                fieldsMayBeTouched = true;
            }

            if (ctor != null)
                foreach (var stmt in ctor.Body.Statements) inner.CompileStmt(stmt);

            inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);

            var paramDefaults = ctor != null ? CompileParamDefaults(rc, ctor.Params) : Array.Empty<FunctionProto?>();
            return new FunctionProto(inner._chunk, ctor?.Params.Count ?? 0, access, paramDefaults) { RefMask = ctor != null ? RefMaskOf(ctor.Params) : 0 };
        }

        // -----------------------------------------------------------
        // Statements
        // -----------------------------------------------------------
        /// <summary>Compiles a statement; an error occurring in the process is
        /// COLLECTED (see CompilerException), and the compilation carries on with
        /// the NEXT statement - every statement, also in
        /// nested blocks/method bodies, is a restart
        /// point of its own. The scope/loop state of the compiler
        /// is reset for that to the state BEFORE the statement (the
        /// generated bytecode is worthless after an error anyway and is
        /// discarded, only the counters have to be right for the following
        /// statements).
        private void CompileStmt(Stmt stmt)
        {
            int scopeDepth = _currentScopeDepth;
            int loopCount = _loopStack.Count;
            try
            {
                CompileStmtCore(stmt);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
            {
                _errors.Add(ex as CompilerException ?? new CompilerException(ex.Message, stmt.Line));
                _currentScopeDepth = scopeDepth;
                while (_loopStack.Count > loopCount) _loopStack.Pop();
            }
        }

        private void CompileStmtCore(Stmt stmt)
        {
            // For the step debugger in the editor sub-project (Bytecode.Chunk.
            // MarkLine) - marks at which code position the current
            // source line begins. Purely additive, no runtime effect.
            _chunk.MarkLine(CurrentSourceIndex, stmt.Line);

            switch (stmt)
            {
                case Stmt.BlockStmt block:
                    CompileBlockNewScope(block);
                    break;

                case ExprStmt es:
                    CompileDiscardedExpr(es.Expression);
                    break;

                case NoOpStmt:
                    break;

                case NoSyncDirective:
                    break; // see Compile: `SetAutoSync 0` already stands at the program start

                case TimeoutDirective:
                    break; // see Compile: `SetTimeout` already stands at the program start

                case NoShadowDirective:
                    // Like NoOpStmt - already collected by the resolver in a pre-pass
                    // (see ResolveResult.NoShadowGlobals), nothing more to do
                    // here. Without this case EVERY program
                    // with a '#noshadow' line would fail with a NotSupportedException
                    // (see CompileStmt's default case) - the directive
                    // ends up as a quite ordinary Stmt in the top-level statement
                    // list and is therefore compiled here like every other statement
                    // as well.
                    break;

                case VarDeclStmt vd:
                {
                    bool autoArrayAlloc = vd.Initializer == null && vd.ArrayRanks.Count > 0 && vd.ArrayRanks[0] != null;

                    if (vd.Initializer != null)
                    {
                        CompileExpr(vd.Initializer);
                        EmitCheckLambdaSignatureIfNeeded(vd.Type);
                    }
                    else if (autoArrayAlloc)
                        // `int arr[10]` / `int matrix[3][4]` without an initialiser ->
                        // implicitly `new int[10]` or a nested
                        // ("jagged") array, each dimension filled
                        // by a runtime loop (SPEC 8.4: several `[...]` groups = array of
                        // arrays, no real rectangular matrix). A rank without a
                        // size (e.g. `int arr[3][]`) ends the recursion -
                        // from there on the slots stay 'undefined', as before with
                        // a completely undetermined-size declarator.
                        CompileArrayAlloc(vd.ArrayRanks, 0);
                    else
                        EmitLoadConst(Value.MakeUndefined());

                    // SPEC "Unit declarations": `var a : mm = ...`/
                    // `int a : mm = ...` - the value that is CURRENTLY initially written into
                    // the slot must already carry the required unit
                    // (NO automatic coercion, see
                    // resolver answer/SPEC - deliberately the same check as
                    // with every LATER assignment to the same slot, see
                    // CompileAssign, otherwise one could bypass the check through an
                    // "unsuitable" first assignment).
                    EmitCheckUnitIfNeeded(this, vd.Type?.Unit);

                    _chunk.EmitOp(OpCode.DeclareLocal);
                    break;
                }

                case IfStmt ifs:
                    CompileIf(ifs);
                    break;

                case WhileStmt ws:
                    CompileWhile(ws);
                    break;

                case ForStmt fs:
                    CompileFor(fs);
                    break;

                case ForeachStmt fes:
                    CompileForeach(fes);
                    break;

                case ReturnStmt rs:
                    if (rs.Value != null) CompileExpr(rs.Value);
                    else EmitLoadConst(Value.MakeUndefined());
                    // A `return` in the `catch`: the throw site frozen on throwing (see ClearPendingResume) is discarded, as at the normal end
                    // of the catch block - otherwise it would stay lying around together with its scopes. The exception variable lies in the catch scope (slot 0).
                    for (int k = _tryStack.Count - 1; k >= 0; k--)
                        if (_tryStack[k].Phase == TryPhase.Catch)
                        {
                            _chunk.EmitOp(OpCode.LoadLocal);
                            _chunk.EmitU16((ushort)(_currentScopeDepth - (_tryStack[k].OuterDepth + 1)));
                            _chunk.EmitU16(0);
                            _chunk.EmitOp(OpCode.ClearPendingResume);
                        }
                    // Residents of the stack (the enumerators of the surrounding `foreach`, the completions of the surrounding `finally` blocks) lie below the
                    // return value: otherwise they would stay lying there and shift the caller's operands (`1 + f()` with a `return` in the `foreach` of `f`).
                    // If a `try` with `finally` is open, `DoReturn` intercepts the `return` there and resets the stack to the state that its handler had on entering:
                    // the residents below it (e.g. the completion of a surrounding `finally`) belong to the `finally` block that is about to run, and stay lying -
                    // only the `return` without an open `try` removes all of them.
                    int keepResidents = 0;
                    for (int k = _tryStack.Count - 1; k >= 0; k--)
                        if (_tryStack[k].Stmt.Finally != null) { keepResidents = _tryStack[k].ResidentsAtStart; break; }
                    for (int r = _residents.Count - 1; r >= keepResidents; r--)
                        for (int n = 0; n < _residents[r]; n++)
                        {
                            _chunk.EmitOp(OpCode.Swap);
                            _chunk.EmitOp(OpCode.Pop);
                        }
                    _chunk.EmitOp(OpCode.Return);
                    break;

                case DeleteStmt del:
                    CompileExpr(del.Target);
                    _chunk.EmitOp(OpCode.Delete);
                    break;

                case ThrowStmt th:
                    CompileExpr(th.Value);
                    _chunk.EmitOp(OpCode.Throw);
                    // If this exception is later continued via resume(),
                    // the resume value lands exactly here on the stack (see
                    // OpCode.ResumeException) - as a statement it is not
                    // needed, so discard it like any other ExprStmt. Without
                    // this pop a resume() would shift the stack here.
                    _chunk.EmitOp(OpCode.Pop);
                    break;

                case TryStmt trys:
                    CompileTry(trys);
                    break;

                case BreakStmt:
                    // The resolver has already checked that we are in a loop
                    // and that no try/catch/finally boundary
                    // is crossed - _loopStack.Peek() is therefore always safe here.
                    CompileBreakOrContinue(isBreak: true);
                    break;

                case ContinueStmt:
                    CompileBreakOrContinue(isBreak: false);
                    break;

                case FireStmt fireStmt:
                    CompileFireStmt(fireStmt);
                    break;

                case LeaveStmt:
                    _chunk.EmitOp(OpCode.Leave);
                    break;

                case SectionEnterStmt:
                    _chunk.EmitOp(OpCode.SectionEnter);
                    break;

                case SectionExitStmt:
                    _chunk.EmitOp(OpCode.SectionExit);
                    break;

                case SilenceStmt silence:
                    CompileExpr(silence.Target);
                    if (silence.MemberForm)
                    {
                        _chunk.EmitOp(OpCode.SilenceMember);
                        _chunk.EmitU16(silence.Member != null ? _chunk.AddConstant(Value.MakeString(silence.Member)) : 0);
                        _chunk.EmitByte(silence.Member == null ? (byte)1 : (byte)0);
                    }
                    else _chunk.EmitOp(OpCode.SilenceValue);
                    break;

                case PostGlobalStmt postGlobal:
                    foreach (var arg in postGlobal.Args) CompileExpr(arg);
                    CompileLambda(postGlobal.Lambda);
                    _chunk.EmitOp(OpCode.PostGlobal);
                    _chunk.EmitByte((byte)postGlobal.Args.Count);
                    break;

                case TerminateStmt terminateStmt:
                    if (terminateStmt.Value != null) CompileExpr(terminateStmt.Value);
                    else EmitLoadConst(Value.MakeUndefined());
                    _chunk.EmitOp(OpCode.Terminate);
                    break;

                case CatchThreadsDecl threadsDecl:
                    CompileCatchThreadsDecl(threadsDecl);
                    break;

                case CatchTerminateDecl terminateDecl:
                    CompileCatchTerminateDecl(terminateDecl);
                    break;

                case ProcessStmt processStmt:
                    CompileExpr(processStmt.Target);
                    _chunk.EmitOp(OpCode.Process);
                    break;

                case ClassDecl:
                    // Already handled in the pre-pass (CompileClasses) - nothing to do here.
                    break;

                case InterfaceDecl:
                    // Interfaces need no runtime representation of their own
                    // (purely dynamic method call by name) - the resolver has
                    // already checked fulfilment.
                    break;

                case EnumDecl:
                    // enum members are resolved by the compiler on every access
                    // ('EnumName.Member') directly to an int literal
                    // (see MemberExpr case below) - the declaration
                    // itself generates no code of its own.
                    break;

                case ClassExtensionDecl cx:
                    // Should NEVER arrive here - see the same case in the
                    // resolver (ResolveStmt) for the explanation.
                    throw new NotSupportedException(
                        $"Internal error: 'class extends {cx.TargetRef.BaseName}' was not merged " +
                        "(the program must be produced by Parser.Parse()/ParseMultiple()).");

                case ExternDecl:
                    // Pure signature declaration, generates no code itself (only
                    // the resolver needs it, in order to be able to validate calls).
                    // A call `name(...)` compiles normally via CompileCall
                    // as soon as a native implementation is registered for 'name'.
                    break;

                case UnsafeStmt us:
                    // 'unsafe' itself generates no code of its own - the
                    // permission check (dereferencing/address-of only
                    // inside such a block) is already done by the resolver.
                    CompileBlockNewScope(us.Body);
                    break;

                default:
                    throw new NotSupportedException($"Statement {stmt.GetType().Name} is not supported.");
            }
        }

        /// <summary>Emits - if `type` is a lambda type with a signature
        /// (`lambda&lt;P1,...,Pn&gt;`, see Ast.TypeRef.LambdaSignature) - a
        /// CheckLambdaSignature check for the VALUE that currently lies on top of the
        /// stack (it is only PEEKED in the process, not consumed - the caller
        /// uses it directly afterwards as normal, e.g. via DeclareLocal). A
        /// `lambda` type WITHOUT `&lt;...&gt;` (i.e. without ParamTypeNames entries)
        /// means "0 parameters" (see SPEC "Lambda types with signature") and
        /// is therefore checked EXACTLY like `lambda&lt;&gt;` - not "unchecked".
        /// For every other type (also none at all) a no-op.</summary>
        private void EmitCheckLambdaSignatureIfNeeded(TypeRef? type)
        {
            if (type?.LambdaSignature == null) return;
            _chunk.EmitOp(OpCode.CheckLambdaSignature);
            _chunk.EmitByte((byte)type.LambdaSignature.ParamTypeNames.Count);
        }


        private void CompileBlockNewScope(Stmt.BlockStmt block)
        {
            EmitEnterScope();
            foreach (var s in block.Statements) CompileStmt(s);
            EmitExitScope();
        }

        /// <summary>Compiles a statement as a scope of its own - no matter whether it is already
        /// a block or a single statement (if/while/for body without
        /// '{}'). Must mirror exactly what Resolver.ResolveStmtAsScope does, otherwise
        /// slot/depth numbers no longer match.</summary>
        private void CompileScopedBody(Stmt body)
        {
            // An empty block `{ }` declares nothing and executes nothing: its EnterScope/ExitScope pair would be a pure waste of time
            // (with a loop on every pass). The resolver does create a scope for it, but without variables - the depths of the
            // remaining accesses do not change as a result.
            if (body is Stmt.BlockStmt { Statements.Count: 0 }) return;
            EmitEnterScope();
            if (body is Stmt.BlockStmt block)
                foreach (var s in block.Statements) CompileStmt(s);
            else
                CompileStmt(body);
            EmitExitScope();
        }

        /// <summary>Emits `JumpIfFalse` with a placeholder address and returns the place of the address for later patching. If it is preceded by
        /// a comparison (`Lt`, `LtEq`, `Gt`, `GtEq`, `Eq`, `NotEq`) without a jump target behind it, both are fused into ONE instruction
        /// (`JumpIfNotLt` etc.): the same result, one dispatch and no bool on the stack.</summary>
        private int EmitJumpIfFalse()
        {
            foreach (var (compare, fused) in new[]
            {
                (OpCode.Lt, OpCode.JumpIfNotLt), (OpCode.LtEq, OpCode.JumpIfNotLtEq), (OpCode.Gt, OpCode.JumpIfNotGt),
                (OpCode.GtEq, OpCode.JumpIfNotGtEq), (OpCode.Eq, OpCode.JumpIfNotEq), (OpCode.NotEq, OpCode.JumpIfNotNotEq),
            })
            {
                if (!_chunk.EndsWithOp(compare, 0)) continue;
                _chunk.ReplaceLastOp(fused);
                int fusedAt = _chunk.Here;
                _chunk.EmitU16(0);
                return fusedAt;
            }

            _chunk.EmitOp(OpCode.JumpIfFalse);
            int at = _chunk.Here;
            _chunk.EmitU16(0);
            return at;
        }

        /// <summary>An expression whose value is discarded (expression statement, `for` increment). Common cases become ONE
        /// instruction: `x++`/`x--`/`x = x + c`/`x = x - c` on a variable without a required unit, and an assignment to a
        /// variable (`StoreLocal`/`StoreGlobal` + `Pop` = `StoreLocalPop`/`StoreGlobalPop`).</summary>
        private void CompileDiscardedExpr(Expr expr)
        {
            if (TryCompileArithOnVariable(expr)) return;

            CompileExpr(expr);

            if (_chunk.EndsWithOp(OpCode.StoreLocal, 4)) { _chunk.ReplaceLastOp(OpCode.StoreLocalPop); return; }
            if (_chunk.EndsWithOp(OpCode.StoreGlobal, 2)) { _chunk.ReplaceLastOp(OpCode.StoreGlobalPop); return; }
            _chunk.EmitOp(OpCode.Pop);
        }

        /// <summary>`x++`, `x--`, `x = x + c`, `x = x - c` (c a number literal) on a local or global variable without a required
        /// unit, whose result nobody needs: ArithLocalConstPop/ArithGlobalConstPop.</summary>
        private bool TryCompileArithOnVariable(Expr expr)
        {
            IdentifierExpr? target;
            Value constant;
            bool subtract;

            switch (expr)
            {
                case IncDecExpr { Target: IdentifierExpr incTarget } incDec:
                    target = incTarget;
                    constant = Value.MakeInt(1);
                    subtract = !incDec.IsIncrement;
                    break;

                case AssignExpr { Target: IdentifierExpr assignTarget, Value: BinaryExpr { Op: BinaryOp.Add or BinaryOp.Sub, Left: IdentifierExpr left, Right: LiteralExpr literal } binary }
                    when left.Name == assignTarget.Name && literal.Value.Kind is ValueKind.Int or ValueKind.Float
                         && _refs.TryGetValue(left, out var leftRef) && _refs.TryGetValue(assignTarget, out var targetRef) && leftRef.Equals(targetRef):
                    target = assignTarget;
                    constant = literal.Value;
                    subtract = binary.Op == BinaryOp.Sub;
                    break;

                default:
                    return false;
            }

            if (!_refs.TryGetValue(target, out var reference)) return false;
            switch (reference)
            {
                case ResolvedRef.Local { RequiredUnit: null, ByRef: false } local:
                    _chunk.EmitOp(OpCode.ArithLocalConstPop);
                    _chunk.EmitU16(local.Depth);
                    _chunk.EmitU16(local.Slot);
                    _chunk.EmitU16(_chunk.AddConstant(constant));
                    _chunk.EmitByte(subtract ? (byte)1 : (byte)0);
                    return true;
                case ResolvedRef.Global { RequiredUnit: null } global:
                    _chunk.EmitOp(OpCode.ArithGlobalConstPop);
                    _chunk.EmitU16(global.Slot);
                    _chunk.EmitU16(_chunk.AddConstant(constant));
                    _chunk.EmitByte(subtract ? (byte)1 : (byte)0);
                    return true;
                default:
                    return false;
            }
        }

        private void CompileIf(IfStmt s)
        {
            CompileExpr(s.Condition);
            int elseJumpAt = EmitJumpIfFalse(); // placeholder, patched below

            CompileScopedBody(s.Then);

            if (s.Else != null)
            {
                _chunk.EmitOp(OpCode.Jump);
                int endJumpAt = _chunk.Here;
                _chunk.EmitU16(0);

                _chunk.PatchU16(elseJumpAt, _chunk.Here);
                CompileScopedBody(s.Else);
                _chunk.PatchU16(endJumpAt, _chunk.Here);
            }
            else
            {
                _chunk.PatchU16(elseJumpAt, _chunk.Here);
            }
        }

        private void CompileWhile(WhileStmt s)
        {
            var ctx = new LoopCompileContext { ScopeDepthAtLoopBodyStart = _currentScopeDepth };
            _loopStack.Push(ctx);

            int loopStart = _chunk.Here;
            CompileExpr(s.Condition);
            int endJumpAt = EmitJumpIfFalse();

            CompileScopedBody(s.Body);

            // Back jump and loop end belong to the line of the `while` (not to the last line of the body) - otherwise the
            // debugger would stop again in the last line of the body after leaving the loop (breakpoint there!).
            _chunk.MarkLine(CurrentSourceIndex, s.Line);

            // 'continue' jumps here - directly in front of the jump back to the
            // condition check (for 'while' the same in content as
            // 'Jump loopStart' directly, but held as an address of its own, so that
            // CompileFor/CompileForeach can use the same mechanism with a
            // DIFFERENT target (increment step or before the back jump)
            // without needing a case distinction of their own).
            int continueTarget = _chunk.Here;
            foreach (var addr in ctx.ContinueJumpPatchAddrs) _chunk.PatchU16(addr, continueTarget);

            _chunk.EmitOp(OpCode.Jump);
            _chunk.EmitU16(loopStart);

            int loopEnd = _chunk.Here;
            _chunk.PatchU16(endJumpAt, loopEnd);
            foreach (var addr in ctx.BreakJumpPatchAddrs) _chunk.PatchU16(addr, loopEnd);

            _loopStack.Pop();
        }

        private void CompileFor(ForStmt s)
        {
            // Enclosing scope for init (SPEC/resolver: init/condition/
            // increment/body share a common scope).
            EmitEnterScope();
            if (s.Init != null) CompileStmt(s.Init);

            var ctx = new LoopCompileContext { ScopeDepthAtLoopBodyStart = _currentScopeDepth };
            _loopStack.Push(ctx);

            int loopStart = _chunk.Here;
            int endJumpAt = -1;
            if (s.Condition != null)
            {
                CompileExpr(s.Condition);
                endJumpAt = EmitJumpIfFalse();
            }

            CompileScopedBody(s.Body);

            // Increment, back jump and loop end belong to the line of the `for` (not to the last line of the body): this way the
            // debugger shows the `for` line when stepping over the end of the body, and after leaving the loop a breakpoint
            // in the body does not stop again.
            _chunk.MarkLine(CurrentSourceIndex, s.Line);

            // 'continue' jumps HERE - BEFORE the increment, so that it
            // still runs on a 'continue' (otherwise e.g.
            // 'for (i=0; i<10; i=i+1) { if (x) continue }' would never increase i -
            // an endless loop).
            int continueTarget = _chunk.Here;
            foreach (var addr in ctx.ContinueJumpPatchAddrs) _chunk.PatchU16(addr, continueTarget);

            if (s.Increment != null)
                CompileDiscardedExpr(s.Increment);

            _chunk.EmitOp(OpCode.Jump);
            _chunk.EmitU16(loopStart);

            int loopEnd = _chunk.Here;
            if (endJumpAt >= 0) _chunk.PatchU16(endJumpAt, loopEnd);
            foreach (var addr in ctx.BreakJumpPatchAddrs) _chunk.PatchU16(addr, loopEnd);

            _loopStack.Pop();
            EmitExitScope();
        }

        /// <summary>`foreach (x in iterable) { body }` - purely dynamic via
        /// method calls by name (`GetEnumerator`/`MoveNext`/`GetCurrent`),
        /// so it works on everything that has these three methods, not only
        /// on classes officially declared 'IEnumerable' (duck typing, as
        /// method calls work everywhere here anyway). The enumerator
        /// itself deliberately lives only on the value stack (duplicated via Dup),
        /// not in a scope slot - for `foreach` the resolver knows only ONE
        /// scope (the one for the loop variable, see Resolver.ResolveForeach),
        /// an additional slot for the enumerator would have made its depth
        /// calculations inconsistent. For break/continue that means:
        /// the enumerator needs NO special treatment on the jump -
        /// 'break' to `loopEnd` runs into the common, final
        /// pop anyway (see below), 'continue' to `continueTarget` does not touch the
        /// enumerator at all (simply stays lying on the stack, as
        /// with every normal iteration too).</summary>
        private void CompileForeach(ForeachStmt fs)
        {
            CompileExpr(fs.Iterable);
            EmitCallMethodByName("GetEnumerator", 0);
            // Stack: [enumerator]

            var ctx = new LoopCompileContext { ScopeDepthAtLoopBodyStart = _currentScopeDepth, IsForeach = true };
            _loopStack.Push(ctx);
            _residents.Add(1); // the enumerator lies on the stack as long as the loop runs

            int loopStart = _chunk.Here;
            _chunk.EmitOp(OpCode.Dup);
            EmitCallMethodByName("MoveNext", 0);
            // Stack: [enumerator, bool]

            _chunk.EmitOp(OpCode.JumpIfFalse); // pop bool
            int endJumpAt = _chunk.Here;
            _chunk.EmitU16(0);
            // Stack (on true): [enumerator]

            _chunk.EmitOp(OpCode.Dup);
            EmitCallMethodByName("GetCurrent", 0);
            // Stack: [enumerator, current]

            EmitEnterScope(); // corresponds to Resolver.PushScope() for the loop variable
            _chunk.EmitOp(OpCode.DeclareLocal); // pop 'current', slot 0 of this new scope
            // Stack: [enumerator]

            CompileScopedBody(fs.Body); // entspricht Resolver.ResolveStmtAsScope(body)
            EmitExitScope(); // entspricht PopScope()

            int continueTarget = _chunk.Here;
            foreach (var addr in ctx.ContinueJumpPatchAddrs) _chunk.PatchU16(addr, continueTarget);

            _chunk.EmitOp(OpCode.Jump);
            _chunk.EmitU16(loopStart);

            _chunk.MarkLine(CurrentSourceIndex, fs.Line); // the loop end belongs to the line of the `foreach` (see CompileFor)
            int loopEnd = _chunk.Here;
            _chunk.PatchU16(endJumpAt, loopEnd);
            foreach (var addr in ctx.BreakJumpPatchAddrs) _chunk.PatchU16(addr, loopEnd);
            _chunk.EmitOp(OpCode.Pop); // Enumerator-Referenz verwerfen

            _residents.RemoveAt(_residents.Count - 1);
            _loopStack.Pop();
        }

        private void EmitCallMethodByName(string methodName, int argCount)
        {
            _chunk.EmitOp(OpCode.CallMethod);
            _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(methodName)));
            _chunk.EmitByte((byte)argCount);
        }

        /// <summary>`try { A } catch(T1 e1) { B1 } catch(e2) { B2 } finally { C }`:
        ///
        ///   RegisterHandler [Catches: (T1,addrB1), (null,addrB2)], FinallyProtoIdx
        ///   &lt;A&gt;
        ///   UnregisterHandler
        ///   Jump finallyOrEnd
        /// addrB1: &lt;B1&gt;  ExitScope  Jump finallyOrEnd
        /// addrB2: &lt;B2&gt;  ExitScope  Jump finallyOrEnd
        /// finallyOrEnd: &lt;C inline, falls vorhanden&gt;
        ///
        /// A matching `throw` jumps directly to addrB1/addrB2 (after the VM
        /// has unwound to the registered target scope/frame), with a
        /// scope freshly created by the VM that already contains the exception variable
        /// at slot 0 - the catch bodies themselves therefore need no
        /// EnterScope/DeclareLocal of their own, only a final ExitScope.
        /// FinallyProtoIdx (compiled separately) is only needed if an
        /// exception propagates on outwards past THIS handler (see
        /// HandlerTemplate comment).</summary>
        /// <summary>Compiles the allocation of a (possibly multi-dimensional/
        /// "jagged") array for a declarator like `int matrix[3][4]`:
        /// allocate the outer array, and if the NEXT rank likewise has a
        /// size, fill each element by a runtime loop (the sizes are
        /// expressions, no compile-time constants - hence a real bytecode
        /// loop instead of unrolling) with a recursively allocated inner
        /// array. At the end leaves exactly ONE value (the finished outer
        /// array) on the stack. A rank without a size (e.g. the second `[]` in
        /// `int arr[3][]`) ends the recursion - from there on the slots stay
        /// 'undefined', as a completely undetermined-size declarator always
        /// was. `EnterScope`/`ExitScope` here are harmless,
        /// although the finished value is still needed at the end: arrays do NOT hang
        /// (unlike class instances) on the ownership system, `Release()`
        /// at `ExitScope` therefore only concerns the temporary slots themselves,
        /// not the array VALUE that they had just pointed to.</summary>
        private void CompileArrayAlloc(IReadOnlyList<Expr?> ranks, int rankIndex)
        {
            // All ranks with a size (up to the first one without): every size is evaluated ONCE, NewJagged creates the whole structure.
            // The inner arrays belong to the outer one (SPEC 2.5) - they live and die with it.
            int sized = 0;
            while (rankIndex + sized < ranks.Count && ranks[rankIndex + sized] != null) sized++;
            for (int i = 0; i < sized; i++) CompileExpr(ranks[rankIndex + i]!);
            if (sized == 1) { _chunk.EmitOp(OpCode.NewArray); return; }
            _chunk.EmitOp(OpCode.NewJagged);
            _chunk.EmitByte((byte)sized);
        }

        private void CompileTry(TryStmt t)
        {
            bool hasFinally = t.Finally != null;
            var template = new HandlerTemplate();
            int templateIdx = _chunk.AddHandlerTemplate(template);

            _chunk.EmitOp(OpCode.RegisterHandler);
            _chunk.EmitU16(templateIdx);

            var tryContext = new TryCompileContext { Stmt = t, Phase = TryPhase.Try, OuterDepth = _currentScopeDepth, LoopCount = _loopStack.Count, ResidentsAtStart = _residents.Count };
            _tryStack.Add(tryContext);
            CompileBlockNewScope(t.TryBlock);
            _tryStack.RemoveAt(_tryStack.Count - 1);

            _chunk.EmitOp(OpCode.UnregisterHandler);
            if (hasFinally) _chunk.EmitOp(OpCode.EnterFinallyNormal); // Abschluss "normal"

            _chunk.EmitOp(OpCode.Jump);
            var jumpsToFinallyOrEnd = new List<int> { _chunk.Here };
            _chunk.EmitU16(0);

            foreach (var c in t.Catches)
            {
                int catchAddr = _chunk.Here;
                template.Catches.Add((c.TypeRef == null ? null : ResolveTypeRef(c.TypeRef), catchAddr));

                // The catch scope created by the VM counts along for a `break`/`continue` in the block (see CompileBreakOrContinue).
                tryContext.Phase = TryPhase.Catch;
                _tryStack.Add(tryContext);
                _currentScopeDepth++;
                foreach (var stmt in c.Body.Statements) CompileStmt(stmt);
                _currentScopeDepth--;
                _tryStack.RemoveAt(_tryStack.Count - 1);

                // If this exception was never continued via resume() (the
                // catch block thus arrives here quite normally), the
                // throw-site state frozen on throwing now has to be
                // discarded cleanly after the fact (see VM.ClearPendingResume) - the
                // exception variable lies at this point always at depth 0/
                // slot 0 of the catch scope freshly created by the VM.
                _chunk.EmitOp(OpCode.LoadLocal);
                _chunk.EmitU16(0);
                _chunk.EmitU16(0);
                _chunk.EmitOp(OpCode.ClearPendingResume);

                _chunk.EmitOp(OpCode.ExitScope); // releases the exception scope created by the VM again

                // With finally, a finally-only handler was active during the catch block (see VM.ThrowException): unregister it now
                if (hasFinally)
                {
                    _chunk.EmitOp(OpCode.UnregisterHandler);
                    _chunk.EmitOp(OpCode.EnterFinallyNormal);
                }

                _chunk.EmitOp(OpCode.Jump);
                jumpsToFinallyOrEnd.Add(_chunk.Here);
                _chunk.EmitU16(0);
            }

            int finallyOrEndAddr = _chunk.Here;
            foreach (var addr in jumpsToFinallyOrEnd) _chunk.PatchU16(addr, finallyOrEndAddr);
            if (!hasFinally) return;

            // ONE finally block for all ways in (normal, break/continue, return, exception, leave/terminate): on top of the stack lies the
            // completion (payload, kind) that EndFinally evaluates at the end. Inside the block itself it is a resident of the stack (see _residents).
            template.FinallyAddr = finallyOrEndAddr;
            foreach (var addr in tryContext.FinallyJumpPatches) _chunk.PatchU16(addr, finallyOrEndAddr);

            _residents.Add(2);
            CompileBlockNewScope(t.Finally!);
            _residents.RemoveAt(_residents.Count - 1);
            _chunk.EmitOp(OpCode.EndFinally);

            // Exit pieces for a `break`/`continue` that ran through this finally: here, behind the `try` (scope depth and `_tryStack` are already
            // right), the same jump continues from outside - via a further `finally` or directly to the target. Normal execution skips them.
            if (tryContext.BreakStubPatches.Count > 0 || tryContext.ContinueStubPatches.Count > 0)
            {
                _chunk.EmitOp(OpCode.Jump);
                int skipStubs = _chunk.Here;
                _chunk.EmitU16(0);
                foreach (var (isBreak, patches) in new[] { (true, tryContext.BreakStubPatches), (false, tryContext.ContinueStubPatches) })
                {
                    if (patches.Count == 0) continue;
                    foreach (var addr in patches) _chunk.PatchU16(addr, _chunk.Here);
                    CompileBreakOrContinue(isBreak);
                }
                _chunk.PatchU16(skipStubs, _chunk.Here);
            }
        }

        // -----------------------------------------------------------
        // Expressions
        // -----------------------------------------------------------
        /// <summary>Compiles an expression; an error in it gets here the
        /// line of the INNERMOST affected expression (the throw sites themselves
        /// know no line) - collecting only happens at statement level
        /// (see CompileStmt).</summary>
        private void CompileExpr(Expr expr)
        {
            try
            {
                CompileExprCore(expr);
            }
            catch (Exception ex) when ((ex is NotSupportedException || ex is InvalidOperationException) && ex is not CompilerException)
            {
                throw new CompilerException(ex.Message, expr.Line);
            }
        }

        private void CompileExprCore(Expr expr)
        {
            switch (expr)
            {
                case LiteralExpr lit:
                    EmitLoadConst(lit.Value);
                    break;

                case IdentifierExpr id:
                    CompileIdentifierLoad(id);
                    break;

                case UnaryExpr u:
                    CompileUnary(u);
                    break;

                case BinaryExpr b:
                    CompileBinary(b);
                    break;

                case UnitCoerceExpr:
                case TypeCoerceExpr:
                    CompileStandaloneCoercion(expr);
                    break;

                case AssignExpr a:
                    CompileAssign(a);
                    break;

                case IncDecExpr incDec:
                    CompileIncDec(incDec);
                    break;

                case ThisExpr:
                    _chunk.EmitOp(OpCode.LoadThis);
                    break;

                case BaseExpr:
                    throw new NotSupportedException(
                        "'base' is only valid as 'base.Method(...)', not as a standalone value.");

                case IsInExpr iin:
                    CompileExpr(iin.Operand);
                    _chunk.EmitOp(OpCode.IsInUnit);
                    _chunk.EmitU16(_chunk.AddUnit(fire.Values.Unit.Parse(iin.UnitName)));
                    break;

                case IsOfExpr iof:
                    CompileExpr(iof.Operand);
                    _chunk.EmitOp(OpCode.IsOfType);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ResolveTypeRef(iof.TypeRef))));
                    break;

                case IsFromExpr ifr:
                    CompileExpr(ifr.Operand);
                    CompileExpr(ifr.OwnerExpr);
                    _chunk.EmitOp(OpCode.IsFrom);
                    _chunk.EmitByte(ifr.Transitive ? (byte)1 : (byte)0);
                    break;

                case NewExpr ne:
                {
                    ulong newCopyMask = CompileArgs(ne.Args, scopeCreating: true, _refParams.ForConstructor(ResolveNewClassName(ne), ne.Args.Count));
                    EmitCopyArgsPrefix(newCopyMask);
                    _chunk.EmitOp(OpCode.NewObject);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ResolveNewClassName(ne))));
                    _chunk.EmitByte((byte)ne.Args.Count);
                    break;
                }

                case NewArrayExpr na:
                    CompileArrayAlloc(na.SizeExprs, 0);
                    break;

                case NewBufferExpr nb:
                    CompileExpr(nb.SizeExpr);
                    _chunk.EmitOp(OpCode.MakeBuffer);
                    break;

                case ArrayLiteralExpr al:
                    foreach (var el in al.Elements) CompileExpr(el);
                    // an array/buffer created in the literal itself (`[[1, 2], [3]]`) belongs to the outer one (SPEC 2.5)
                    uint partMask = 0;
                    if (al.Elements.Count <= 32)
                        for (int i = 0; i < al.Elements.Count; i++)
                            if (al.Elements[i] is ArrayLiteralExpr or NewArrayExpr or NewBufferExpr) partMask |= 1u << i;
                    if (partMask == 0)
                    {
                        _chunk.EmitOp(OpCode.MakeArrayLiteral);
                        _chunk.EmitU16(al.Elements.Count);
                    }
                    else
                    {
                        _chunk.EmitOp(OpCode.MakeArrayLiteralParts);
                        _chunk.EmitU16(al.Elements.Count);
                        _chunk.EmitU16((int)(partMask & 0xFFFF));
                        _chunk.EmitU16((int)(partMask >> 16));
                    }
                    break;

                case InterpolatedStringExpr ise:
                {
                    // Builds the result as a chain of string concatenations
                    // via the quite ordinary '+' opcode (which already
                    // appends EVERY value via ToString() as soon as one side
                    // is a string, see Value.Add) - no separate
                    // "string building" mechanism needed. A format
                    // specifier (':X' etc.) converts the expression value
                    // BEFORE the concatenation explicitly via OpCode.FormatValue
                    // into a (formatted) string, instead of using ToString()
                    // for that.
                    EmitLoadConst(Value.MakeString(""));
                    foreach (var part in ise.Parts)
                    {
                        if (part is InterpolationTextPart tp)
                        {
                            EmitLoadConst(Value.MakeString(tp.Text));
                        }
                        else if (part is InterpolationExprPart ep)
                        {
                            CompileExpr(ep.Expression);
                            if (ep.Format != null)
                            {
                                _chunk.EmitOp(OpCode.FormatValue);
                                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ep.Format)));
                            }
                        }
                        _chunk.EmitOp(OpCode.Add);
                    }
                    break;
                }

                case MemberExpr me:
                    // 'EnumName.Member' was already resolved by the resolver to a
                    // fixed int value (ResolvedRef.EnumMember) - then
                    // load directly as a constant, no runtime field-access
                    // logic needed (the compiler in this case also does
                    // NOT try to compile me.Target as an identifier - there is
                    // after all no variable of this name at all).
                    if (_refs.TryGetValue(me, out var memberRef) && memberRef is ResolvedRef.EnumMember em)
                    {
                        EmitLoadConst(Value.MakeInt(em.Value));
                        break;
                    }
                    // 'ClassName.Member' (SPEC "Static members") - no
                    // object needed on the stack (unlike GetField), the
                    // class name already stands as a constant in the bytecode (the
                    // resolver has already resolved it unambiguously, see
                    // ResolvedRef.StaticMember).
                    if (memberRef is ResolvedRef.StaticMember sm)
                    {
                        _chunk.EmitOp(OpCode.GetStaticField);
                        _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(sm.ClassName)));
                        _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                        break;
                    }
                    CompileExpr(me.Target);
                    _chunk.EmitOp(OpCode.GetField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                    break;

                case IndexExpr ix:
                    CompileExpr(ix.Target);
                    CompileExpr(ix.Index);
                    _chunk.EmitOp(OpCode.ArrayGet);
                    break;

                case ThrowExpr te:
                    CompileExpr(te.Value);
                    _chunk.EmitOp(OpCode.Throw);
                    break;

                case LambdaExpr lam:
                    CompileLambda(lam);
                    break;

                case ProbeExpr probe:
                    CompileExpr(probe.Target);
                    CompileExpr(probe.Handler);
                    _chunk.EmitOp(OpCode.Probe);
                    _chunk.EmitU16(probe.Member != null ? _chunk.AddConstant(Value.MakeString(probe.Member)) : 0);
                    _chunk.EmitByte((byte)((probe.IsChanging ? 1 : 0) | (probe.Member == null ? 2 : 0)));
                    break;

                case SyncGlobalsExpr:
                    _chunk.EmitOp(OpCode.SyncGlobals);
                    break;

                case SyncExpr syncExpr:
                {
                    CompileExpr(syncExpr.Target);
                    byte flags = 0;
                    if (syncExpr.IsTry) flags |= 1;
                    if (syncExpr.IsFlat) flags |= 2;
                    _chunk.EmitOp(OpCode.Sync);
                    _chunk.EmitByte(flags);
                    break;
                }

                case TryProcessExpr tryProcessExpr:
                    CompileExpr(tryProcessExpr.Target);
                    _chunk.EmitOp(OpCode.TryProcess);
                    break;

                case TryCallExpr tryCallExpr:
                {
                    // The callee itself is deliberately NOT compiled (see
                    // Resolver.ResolveTryCallExpr - it has no ordinary
                    // ResolvedRef, only the TryCallExpr node itself has
                    // ResolvedRef.TryableNative) - only the arguments.
                    var innerCall = (CallExpr)tryCallExpr.Call;
                    if (_refs.TryGetValue(tryCallExpr, out var takeRef) && takeRef is ResolvedRef.TryTake tryTake)
                    {
                        // `try obj.Take...(...)`: the same call form as the ownership method, under the name `try<Name>` (the VM returns the bool)
                        var takeMember = (MemberExpr)innerCall.Callee;
                        CompileCall(new CallExpr(innerCall.Line, new MemberExpr(takeMember.Line, takeMember.Target, "try" + tryTake.Name), innerCall.Args));
                        break;
                    }
                    foreach (var arg in innerCall.Args) CompileExpr(arg);
                    if (_refs.TryGetValue(tryCallExpr, out var tcRef) && tcRef is ResolvedRef.TryableNative tn)
                    {
                        _chunk.EmitOp(OpCode.CallTryableNative);
                        _chunk.EmitU16(_natives.TryableIndexOf(tn.Name));
                        _chunk.EmitByte((byte)innerCall.Args.Count);
                    }
                    else
                    {
                        throw new NotSupportedException(
                            "TryCallExpr without a resolved TryableNative reference - the resolver should have caught this.");
                    }
                    break;
                }

                case CallExpr call:
                    CompileCall(call);
                    break;

                default:
                    throw new NotSupportedException($"Expression type {expr.GetType().Name} is not supported.");
            }
        }

        /// <summary>Compiles the lambda body ONCE into a chunk of its own
        /// (FunctionProto) - no captured enclosing scope needed, since
        /// lambdas see only their own scope + global anyway (SPEC 4.2),
        /// the resolver already took that into account when resolving. Every
        /// evaluation of THIS LambdaExpr at runtime (MakeLambda) creates a
        /// new LambdaValue that reuses the same proto - only the
        /// 'on' target can differ per evaluation.</summary>
        /// <summary>`c => c.radius` / `p => p.address.city`: one parameter, the body only a member chain on it - the names from the outside in.</summary>
        private static string[]? TrySelectorPath(LambdaExpr lambda)
        {
            if (lambda.Params.Count != 1 || lambda.Body.Statements.Count != 1 || lambda.Body.Statements[0] is not ReturnStmt { Value: { } value })
                return null;
            var path = new List<string>();
            while (value is MemberExpr member)
            {
                path.Add(member.Name);
                value = member.Target;
            }
            if (path.Count == 0 || value is not IdentifierExpr root || root.Name != lambda.Params[0].Name) return null;
            path.Reverse();
            return path.ToArray();
        }

        private void CompileLambda(LambdaExpr lambda)
        {
            var inner = new Compiler(_refs, _natives, _enclosingClass, _globalSlotCount, _knownClassNames, _errors, _refParams);
            inner._chunk.OwnerClass = _enclosingClass;
            for (int i = 0; i < lambda.Params.Count; i++)
                inner._chunk.MarkLocalName(0, i, lambda.Params[i].Name);
            if (_refs.TryGetValue(lambda, out var nameRef) && nameRef is ResolvedRef.LambdaCaptures named)
                for (int i = 0; i < named.Variables.Count; i++)
                    inner._chunk.MarkLocalName(0, lambda.Params.Count + i, named.Variables[i].Name);
            EmitLambdaParamChecks(inner, lambda.Params);
            foreach (var stmt in lambda.Body.Statements)
                inner.CompileStmt(stmt);
            // Implicit "return undefined" if the body runs through without an explicit
            // return.
            inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);

            var proto = new FunctionProto(inner._chunk, lambda.Params.Count, AccessModifier.Public, CompileParamDefaults(_enclosingClass, lambda.Params));
            if (Reflection) proto.SelectorPath = TrySelectorPath(lambda);
            int protoIdx = _chunk.AddFunctionProto(proto);

            // Lambda captures (SPEC 4.2): the values of the used outer locals are loaded NOW (copy), in the enclosing scope.
            var captures = _refs.TryGetValue(lambda, out var captureRef) && captureRef is ResolvedRef.LambdaCaptures lc ? lc.Variables : null;
            if (captures != null)
            {
                if (captures.Count > 255) throw new NotSupportedException("A lambda can capture at most 255 outer variables.");
                foreach (var captured in captures) CompileExpr(captured);
            }

            bool hasOnTarget = lambda.OnTarget != null;
            if (hasOnTarget)
                CompileExpr(lambda.OnTarget!); // in the ENCLOSING (current) scope, not in the lambda scope

            if (captures != null)
            {
                _chunk.EmitOp(OpCode.MakeLambdaCapturing);
                _chunk.EmitU16(protoIdx);
                _chunk.EmitByte(hasOnTarget ? (byte)1 : (byte)0);
                _chunk.EmitByte((byte)captures.Count);
            }
            else
            {
                _chunk.EmitOp(OpCode.MakeLambda);
                _chunk.EmitU16(protoIdx);
                _chunk.EmitByte(hasOnTarget ? (byte)1 : (byte)0);
            }
        }

        /// <summary>Calls of registered native functions (`print(...)` etc.)
        /// run via CALL_NATIVE, `obj.Method(...)` via virtual resolution
        /// (CallMethod), `base.Method(...)` via direct base resolution
        /// (CallBaseMethod), everything else as a general lambda call (the callee
        /// must evaluate to a lambda value at runtime).</summary>
        /// <summary>Compiles the arguments of a call. If an argument is `flat x`/`copy x` and the call creates a scope
        /// for the called function (`scopeCreating`), only `x` is evaluated and the copying is left to the call
        /// (prefix <see cref="OpCode.CopyArgs"/>, see <see cref="EmitCopyArgsPrefix"/>): the copy then belongs to the scope of the
        /// called function (SPEC 2.4). For native functions this scope does not exist - there it stays an ordinary
        /// copy (owner: current scope). Returns the copy mask (2 bits per argument, 0 = none).</summary>
        private ulong CompileArgs(IReadOnlyList<Expr> args, bool scopeCreating, bool[]? refs = null)
        {
            ulong mask = 0;
            for (int i = 0; i < args.Count; i++)
            {
                if (refs != null && i < refs.Length && refs[i] && IsAddressable(args[i]))
                {
                    if (i >= 16) throw new NotSupportedException("A 'ref' argument is only possible for the first 16 arguments of a call.");
                    CompileRefArgument(args[i], i);
                    mask |= 3UL << (4 * i);
                }
                else if (args[i] is UnaryExpr { Op: UnaryOp.Take } takeArg)
                {
                    // `f(take x)` (SPEC 2.2): x belongs to the call of f from now on; natives and built-ins have no call scope - there it goes to the current scope
                    CompileExpr(takeArg.Operand);
                    if (scopeCreating && i < 16) { _chunk.EmitOp(OpCode.TakeCheck); mask |= 5UL << (4 * i); }
                    else EmitTakeToScope(0);
                }
                else if (scopeCreating && args[i] is UnaryExpr { Op: UnaryOp.FlatCopy or UnaryOp.DeepCopy } copyArg)
                {
                    if (i >= 16)
                        throw new NotSupportedException("`flat`/`copy` as an argument is only possible for the first 16 arguments of a call.");
                    mask |= (copyArg.Op == UnaryOp.DeepCopy ? 2UL : 1UL) << (4 * i);
                    CompileExpr(copyArg.Operand);
                }
                else
                {
                    CompileExpr(args[i]);
                    // `f(g())`, `f(new X())`: the returned / freshly made value goes into the parameter (the caller holds no reference to it), so it belongs
                    // to the called function (SPEC 2.1), not to the caller
                    if (scopeCreating && i < 16 && args[i] is CallExpr or NewExpr or NewArrayExpr) mask |= 4UL << (4 * i);
                }
            }
            return mask;
        }

        /// <summary>An argument for a `ref` parameter: instead of the value the address of a variable, a field or an array element (SPEC 5.4.2).</summary>
        /// <summary>Does the expression have an address (variable, field, array element)? Only then is it passed for a `ref` parameter; otherwise the value -
        /// if the call then binds to a `ref` parameter, the VM reports that a variable must stand there.</summary>
        private bool IsAddressable(Expr arg) => arg switch
        {
            IdentifierExpr id => _refs.TryGetValue(id, out var r) && r is ResolvedRef.Local or ResolvedRef.Global or ResolvedRef.ImplicitThisMember,
            MemberExpr me => !(_refs.TryGetValue(me, out var m) && m is ResolvedRef.StaticMember or ResolvedRef.EnumMember),
            IndexExpr => true,
            _ => false,
        };

        private void CompileRefArgument(Expr arg, int index)
        {
            string Fail(string what) => $"Argument {index + 1} is passed to a 'ref' parameter, so it has to be a variable, a field or an array element{what}.";
            switch (arg)
            {
                case IdentifierExpr id:
                    switch (_refs[id])
                    {
                        case ResolvedRef.Local local: EmitAddressOfLocal(local); return;
                        case ResolvedRef.Global global:
                            _chunk.EmitOp(OpCode.AddressOfGlobal);
                            _chunk.EmitU16(global.Slot);
                            return;
                        case ResolvedRef.ImplicitThisMember:
                            _chunk.EmitOp(OpCode.LoadThis);
                            _chunk.EmitOp(OpCode.AddressOfField);
                            _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name)));
                            return;
                        default: throw new NotSupportedException(Fail($" ('{id.Name}' is none of these)"));
                    }
                case MemberExpr me when !(_refs.TryGetValue(me, out var memberRef) && memberRef is ResolvedRef.StaticMember or ResolvedRef.EnumMember):
                    CompileExpr(me.Target);
                    _chunk.EmitOp(OpCode.AddressOfField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                    return;
                case IndexExpr ix:
                    CompileExpr(ix.Target);
                    CompileExpr(ix.Index);
                    _chunk.EmitOp(OpCode.AddressOfIndex);
                    return;
                default:
                    throw new NotSupportedException(Fail(" - an expression has no address"));
            }
        }

        private void EmitTakeToScope(int depth)
        {
            _chunk.EmitOp(OpCode.TakeToScope);
            _chunk.EmitU16(depth);
        }

        /// <summary>Emits the prefix `CopyArgs` (only if a mask is present) - directly BEFORE the call opcode.</summary>
        private void EmitCopyArgsPrefix(ulong mask)
        {
            if (mask == 0) return;
            _chunk.EmitOp(OpCode.CopyArgs);
            for (int part = 0; part < 4; part++) _chunk.EmitU16((int)((mask >> (16 * part)) & 0xFFFF));
        }

        /// <summary>Compiles `new X(...)` or `flat x`/`copy x` for the case that the future OWNER (an object) already
        /// lies on the stack (SPEC 2.1/2.4: assigned directly to a field). Returns false if `value` is neither of the two
        /// (then nothing was emitted).</summary>
        private static bool IsOwnedCreation(Expr value) =>
            value is NewExpr or NewArrayExpr or ArrayLiteralExpr or NewBufferExpr or CallExpr or UnaryExpr { Op: UnaryOp.FlatCopy or UnaryOp.DeepCopy };

        private bool TryCompileOwnedCreation(Expr value)
        {
            if (value is NewExpr ne)
            {
                ulong mask = CompileArgs(ne.Args, scopeCreating: true, _refParams.ForConstructor(ResolveNewClassName(ne), ne.Args.Count));
                EmitCopyArgsPrefix(mask);
                _chunk.EmitOp(OpCode.NewObjectOwned);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ResolveNewClassName(ne))));
                _chunk.EmitByte((byte)ne.Args.Count);
                return true;
            }
            if (value is NewArrayExpr or ArrayLiteralExpr or NewBufferExpr or CallExpr)
            {
                // [owner] -> [owner, array] -> [array]: an array/a buffer assigned directly to a field belongs to the object (SPEC 2.1)
                CompileExpr(value);
                _chunk.EmitOp(OpCode.OwnValue);
                return true;
            }
            if (value is UnaryExpr { Op: UnaryOp.FlatCopy or UnaryOp.DeepCopy } copyExpr)
            {
                CompileExpr(copyExpr.Operand);
                _chunk.EmitOp(OpCode.CopyValueOwned);
                _chunk.EmitByte(copyExpr.Op == UnaryOp.DeepCopy ? (byte)1 : (byte)0);
                return true;
            }
            return false;
        }

        private void CompileCall(CallExpr call)
        {
            if (call.Callee is IdentifierExpr calleeId && _refs.TryGetValue(calleeId, out var resolved))
            {
                if (resolved is ResolvedRef.Native nativeRef)
                {
                    foreach (var arg in call.Args) CompileExpr(arg);
                    _chunk.EmitOp(OpCode.CallNative);
                    _chunk.EmitU16(_natives.IndexOf(nativeRef.Name));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                if (resolved is ResolvedRef.Extern ext)
                {
                    // Unlike with an unknown identifier this is here
                    // NO compile error - 'extern' only declares the
                    // signature, the actual implementation is only linked by the
                    // host at runtime (ExternRegistry, see
                    // VM.CallExtern) - a script can therefore compile even
                    // if nothing is linked (yet), and only fails on the
                    // ACTUAL call, if nothing is
                    // registered by then.
                    foreach (var arg in call.Args) CompileExpr(arg);
                    _chunk.EmitOp(OpCode.CallExtern);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ext.Name)));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                if (resolved is ResolvedRef.StaticMember callSm)
                {
                    // SPEC "Static members" - bare name instead of
                    // 'ClassName.Method(...)'.
                    EmitCopyArgsPrefix(CompileArgs(call.Args, scopeCreating: true, _refParams.ForMethod(calleeId.Name, call.Args.Count)));
                    _chunk.EmitOp(OpCode.CallStaticMethod);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(callSm.ClassName)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(calleeId.Name)));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                if (resolved is ResolvedRef.ImplicitThisMember)
                {
                    // SPEC "Implicit member references" - bare name
                    // instead of 'this.Method(...)'. CallMethod expects the
                    // target object BELOW the arguments on the stack (see
                    // VM.CallMethod: args popped first, only then 'target')
                    // - so push 'this' BEFORE the arguments.
                    _chunk.EmitOp(OpCode.LoadThis);
                    EmitCopyArgsPrefix(CompileArgs(call.Args, scopeCreating: true, _refParams.ForMethod(calleeId.Name, call.Args.Count)));
                    _chunk.EmitOp(OpCode.CallMethod);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(calleeId.Name)));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }
            }

            if (call.Callee is MemberExpr me)
            {
                if (me.Name == "resume")
                {
                    // 'resume' is a reserved method name (like GetIndex/
                    // SetIndex/GetEnumerator) - no real method call,
                    // but jumps back directly to the frozen throw site via an opcode of its own
                    // (see VM.ResumeException).
                    if (call.Args.Count > 1)
                        throw new NotSupportedException(
                            "'resume' expects at most one argument (the resume value).");

                    CompileExpr(me.Target);
                    if (call.Args.Count == 1)
                    {
                        CompileExpr(call.Args[0]);
                    }
                    else
                    {
                        // resume() without an argument == resume(undefined)
                        EmitLoadConst(Value.MakeUndefined());
                    }
                    _chunk.EmitOp(OpCode.ResumeException);
                    return;
                }

                // 'ClassName.Method(...)' (SPEC "Static members") - no
                // object on the stack (unlike CallMethod), the
                // class name already stands as a constant in the bytecode (see
                // ResolvedRef.StaticMember, resolved by the resolver).
                if (_refs.TryGetValue(me, out var calleeMemberRef) && calleeMemberRef is ResolvedRef.StaticMember sm)
                {
                    EmitCopyArgsPrefix(CompileArgs(call.Args, scopeCreating: true, _refParams.ForMethod(me.Name, call.Args.Count)));
                    _chunk.EmitOp(OpCode.CallStaticMethod);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(sm.ClassName)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                if (me.Target is BaseExpr)
                {
                    // 'base.Method(...)' - non-virtual, this stays the current
                    // 'this'. The base class is taken statically from the class
                    // CURRENTLY BEING COMPILED (_enclosingClass), not from the
                    // actual runtime class of 'this' - otherwise that would be
                    // wrong with multi-level inheritance (B.base must always be A, even
                    // if 'this' is an instance of C : B at runtime).
                    if (_enclosingClass?.Base == null)
                        throw new NotSupportedException(
                            "'base.Method(...)' outside of a class with a base class - the resolver should have caught this.");

                    EmitCopyArgsPrefix(CompileArgs(call.Args, scopeCreating: true, _refParams.ForMethod(me.Name, call.Args.Count)));

                    _chunk.EmitOp(OpCode.CallBaseMethod);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(_enclosingClass.Base.Name)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                CompileExpr(me.Target);
                EmitCopyArgsPrefix(CompileArgs(call.Args, scopeCreating: true, _refParams.ForMethod(me.Name, call.Args.Count)));
                _chunk.EmitOp(OpCode.CallMethod);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                _chunk.EmitByte((byte)call.Args.Count);
                return;
            }

            CompileExpr(call.Callee);
            EmitCopyArgsPrefix(CompileArgs(call.Args, scopeCreating: true));
            _chunk.EmitOp(OpCode.Call);
            _chunk.EmitByte((byte)call.Args.Count);
        }

        /// <summary>Standalone (not embedded in an anchor decision)
        /// coercion, e.g. as an initialiser `var y = undefined:km`. As in the
        /// binary-op case: type BEFORE unit (precision with int->float +
        /// unit conversion), and without a sibling anchor an
        /// automatic ('!'/'::' without an argument) unit request falls back to
        /// unitless; an automatic type request stays unchanged for lack of an
        /// anchor (no explicit SPEC requirement for this case).</summary>
        private void CompileStandaloneCoercion(Expr expr)
        {
            var info = AnalyzeCoercion(expr);
            CompileExpr(info.Inner);

            if (info.ExplicitType != null)
                EmitCoerceTypeStatic(TokenTypeToTag(info.ExplicitType.Value));

            if (info.ExplicitUnit != null)
                EmitCoerceUnitStatic(fire.Values.Unit.Parse(info.ExplicitUnit));
            else if (info.UnitIsAuto)
                EmitCoerceUnitStatic(fire.Values.Unit.Unitless);
        }

        /// <summary>Reads a local variable; for a `ref` parameter the slot holds a pointer to the caller's variable - then it is dereferenced.</summary>
        private void EmitLoadVariable(ResolvedRef.Local local)
        {
            _chunk.EmitOp(OpCode.LoadLocal);
            _chunk.EmitU16(local.Depth);
            _chunk.EmitU16(local.Slot);
            if (local.ByRef) _chunk.EmitOp(OpCode.PtrRead);
        }

        /// <summary>Writes the topmost value into a local variable and leaves it on the stack (like StoreLocal); for a `ref` parameter through the pointer.</summary>
        private void EmitStoreVariable(ResolvedRef.Local local)
        {
            if (!local.ByRef)
            {
                _chunk.EmitOp(OpCode.StoreLocal);
                _chunk.EmitU16(local.Depth);
                _chunk.EmitU16(local.Slot);
                return;
            }
            _chunk.EmitOp(OpCode.LoadLocal);   // [value, pointer]
            _chunk.EmitU16(local.Depth);
            _chunk.EmitU16(local.Slot);
            _chunk.EmitOp(OpCode.Swap);        // [pointer, value]
            _chunk.EmitOp(OpCode.PtrWrite);    // writes and returns the value
        }

        /// <summary>The address of a local variable; a `ref` parameter is itself already a pointer.</summary>
        private void EmitAddressOfLocal(ResolvedRef.Local local)
        {
            _chunk.EmitOp(local.ByRef ? OpCode.LoadLocal : OpCode.AddressOfLocal);
            _chunk.EmitU16(local.Depth);
            _chunk.EmitU16(local.Slot);
        }

        private void CompileIdentifierLoad(IdentifierExpr id)
        {
            switch (_refs[id])
            {
                case ResolvedRef.Local local:
                    EmitLoadVariable(local);
                    break;
                case ResolvedRef.Global global:
                    _chunk.EmitOp(OpCode.LoadGlobal);
                    _chunk.EmitU16(global.Slot);
                    break;
                case ResolvedRef.Native native:
                    throw new NotSupportedException(
                        $"'{native.Name}' is a native function and can only be called directly " +
                        $"({native.Name}(...)), not used as a value.");
                case ResolvedRef.Extern ext:
                    throw new NotSupportedException(
                        $"'{ext.Name}' is a function declared extern and can only be called directly, " +
                        $"not used as a value.");
                case ResolvedRef.StaticMember sm:
                    // SPEC "Static members" - bare name instead of
                    // 'ClassName.Name' (see Resolver.ResolveIdentifierRef).
                    _chunk.EmitOp(OpCode.GetStaticField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(sm.ClassName)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name)));
                    break;
                case ResolvedRef.ImplicitThisMember:
                    // SPEC "Implicit member references" - bare name
                    // instead of 'this.Name' (see Resolver.ResolveIdentifierRef).
                    _chunk.EmitOp(OpCode.LoadThis);
                    _chunk.EmitOp(OpCode.GetField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name)));
                    break;
            }
        }

        private void CompileAssign(AssignExpr a)
        {
            if (a.Target is MemberExpr me)
            {
                // 'ClassName.Member = ...' (SPEC "Static members") - no
                // target object on the stack (unlike with an instance
                // field assignment below), the class name already stands as a
                // constant in the bytecode (see ResolvedRef.StaticMember).
                // Deliberately WITHOUT the NewObjectOwned special treatment below (SPEC
                // 2.1, cascading deletion) - it sets the ownership
                // of a freshly created object to the INSTANCE that holds
                // the field; a static field, however, belongs to no instance, there is
                // no sensible counterpart for that here.
                if (_refs.TryGetValue(me, out var staticTargetRef) && staticTargetRef is ResolvedRef.StaticMember sm)
                {
                    CompileExpr(a.Value);
                    _chunk.EmitOp(OpCode.SetStaticField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(sm.ClassName)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                    return;
                }

                CompileExpr(me.Target);

                // Direct field assignment of a freshly created object: the owner becomes
                // the target object itself, not the current scope (SPEC 2.1). For that
                // the target object must already lie on the stack at the NewObjectOwned call
                // (below the constructor arguments) - hence Dup before the
                // arguments are pushed, and SetField at the end uses the second copy.
                if (IsOwnedCreation(a.Value))
                {
                    _chunk.EmitOp(OpCode.Dup);
                    TryCompileOwnedCreation(a.Value);
                }
                else if (a.Value is UnaryExpr { Op: UnaryOp.Take } takeValue)
                {
                    CompileExpr(takeValue.Operand);          // [obj, value]
                    _chunk.EmitOp(OpCode.TakeToObject);      // the object owns the value from now on (SPEC 2.2)
                }
                else
                {
                    CompileExpr(a.Value);
                }

                _chunk.EmitOp(OpCode.SetField);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                return;
            }

            if (a.Target is UnaryExpr { Op: UnaryOp.Dereference } deref)
            {
                CompileExpr(deref.Operand); // push Pointer
                CompileExpr(a.Value);       // push value
                _chunk.EmitOp(OpCode.PtrWrite);
                return;
            }

            if (a.Target is IndexExpr ix)
            {
                CompileExpr(ix.Target);
                CompileExpr(ix.Index);
                if (a.Value is UnaryExpr { Op: UnaryOp.Take } takeElement)
                {
                    CompileExpr(takeElement.Operand);
                    _chunk.EmitOp(OpCode.TakeToArray);       // the array owns the value from now on (SPEC 2.2)
                }
                else CompileExpr(a.Value);
                _chunk.EmitOp(OpCode.ArraySet);
                return;
            }

            if (a.Target is not IdentifierExpr id)
                throw new NotSupportedException(
                    "Invalid assignment target for the bytecode compiler.");

            // Bare field name in a class (`field = new X()` / `field = copy x`): like `this.field = ...` the new
            // object belongs to the object, not the scope (SPEC 2.1/2.4) - otherwise it would be destroyed on leaving the method,
            // while the field still points to it.
            if (a.Value is UnaryExpr { Op: UnaryOp.Take } takeVar)
            {
                CompileTakeAssign(id, takeVar);
                return;
            }

            if (_refs[id] is ResolvedRef.ImplicitThisMember && IsOwnedCreation(a.Value))
            {
                _chunk.EmitOp(OpCode.LoadThis);
                TryCompileOwnedCreation(a.Value);       // [new object]
                _chunk.EmitOp(OpCode.LoadThis);         // [value, obj]
                _chunk.EmitOp(OpCode.Swap);             // [obj, value]
                _chunk.EmitOp(OpCode.SetField);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name)));
                return;
            }

            CompileExpr(a.Value);

            switch (_refs[id])
            {
                case ResolvedRef.Local local:
                    // SPEC "Unit declarations": EVERY assignment to a
                    // slot with a required unit (not only the first, see
                    // VarDeclStmt compilation) - otherwise one could bypass the
                    // initial check simply through a later, "wrong"
                    // assignment.
                    EmitCheckUnitIfNeeded(this, local.RequiredUnit);
                    // an assignment from an inner block to a variable of an outer one: the value must not die with the block (SPEC 2.1)
                    if (local.Depth > 0 && !local.ByRef) _chunk.EmitOp(OpCode.HoistValue);
                    EmitStoreVariable(local);
                    break;
                case ResolvedRef.Global global:
                    EmitCheckUnitIfNeeded(this, global.RequiredUnit);
                    if (_currentScopeDepth > 0 && IsTopLevelCode) _chunk.EmitOp(OpCode.HoistValue);   // top-level block assigning to a global
                    _chunk.EmitOp(OpCode.StoreGlobal);
                    _chunk.EmitU16(global.Slot);
                    break;
                case ResolvedRef.Native native:
                    throw new NotSupportedException(
                        $"Cannot assign to '{native.Name}' - it is a native function.");
                case ResolvedRef.Extern ext:
                    throw new NotSupportedException(
                        $"Cannot assign to '{ext.Name}' - it is a function declared extern.");
                case ResolvedRef.StaticMember sm:
                    // SPEC "Static members" - bare name instead of
                    // 'ClassName.Name = ...' (see Resolver.
                    // ResolveIdentifierRef/ResolveAssignTarget).
                    _chunk.EmitOp(OpCode.SetStaticField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(sm.ClassName)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name)));
                    break;
                case ResolvedRef.ImplicitThisMember:
                    // SPEC "Implicit member references" - bare name
                    // instead of 'this.Name = ...'. The value here lies (unlike
                    // in the MemberExpr branch above) already ON TOP of the
                    // stack (CompileExpr(a.Value) already ran BEFORE this
                    // switch) - only reload 'this' NOW and swap the two,
                    // swap them, so that SetField gets its expected [obj,
                    // value].
                    _chunk.EmitOp(OpCode.LoadThis); // [value, obj]
                    _chunk.EmitOp(OpCode.Swap);     // [obj, value]
                    _chunk.EmitOp(OpCode.SetField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name)));
                    break;
            }
        }

        /// <summary>`name = take x` (SPEC 2.2): x belongs to whoever holds `name` - the scope of the variable, or the object for a field.</summary>
        private void CompileTakeAssign(IdentifierExpr id, UnaryExpr take)
        {
            CompileExpr(take.Operand);
            switch (_refs[id])
            {
                case ResolvedRef.Local local:
                    EmitCheckUnitIfNeeded(this, local.RequiredUnit);
                    EmitTakeToScope(local.ByRef ? 0 : local.Depth);
                    EmitStoreVariable(local);
                    break;
                case ResolvedRef.Global global:
                    EmitCheckUnitIfNeeded(this, global.RequiredUnit);
                    EmitTakeToScope(0xFFFF);
                    _chunk.EmitOp(OpCode.StoreGlobal);
                    _chunk.EmitU16(global.Slot);
                    break;
                case ResolvedRef.ImplicitThisMember:
                    _chunk.EmitOp(OpCode.LoadThis);         // [value, this]
                    _chunk.EmitOp(OpCode.Swap);             // [this, value]
                    _chunk.EmitOp(OpCode.TakeToObject);
                    _chunk.EmitOp(OpCode.SetField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name)));
                    break;
                default:
                    throw new NotSupportedException($"'take' needs a holder: '{id.Name}' is not a variable or a field.");
            }
        }

        /// <summary>`++x`/`--x`/`x++`/`x--` (see Ast.IncDecExpr documentation).
        /// Four kinds of target, each with its own strategy:
        /// - IdentifierExpr/Dereference/MemberExpr (ONE "address" - slot,
        ///   pointer or object instance): via Dup+read+calculate+write
        ///   directly in the bytecode, for postfix additionally RotateUnderTop, to
        ///   keep the old value below the address, while both
        ///   address and new value stay on top for the write opcode
        ///   (see OpCode.RotateUnderTop documentation) - WITHOUT evaluating the target address
        ///   a second time.
        /// - IndexExpr (TWO "address" parts - array AND index): an opcode of its own
        ///   (IncDecIndex) takes over read+calculate+write ATOMICALLY
        ///   in the VM - with mere stack reordering (only RotateUnderTop,
        ///   which after all knows only 3 values) this would not have been cleanly solvable for FOUR values to be kept
        ///   (array, index, old value, new value)
        ///   without evaluating array/index a second time.</summary>
        private void CompileIncDec(IncDecExpr e)
        {
            var addSubOp = e.IsIncrement ? OpCode.Add : OpCode.Sub;

            if (e.Target is IndexExpr ix)
            {
                CompileExpr(ix.Target);
                CompileExpr(ix.Index);
                _chunk.EmitOp(OpCode.IncDecIndex);
                _chunk.EmitByte(e.IsIncrement ? (byte)1 : (byte)0);
                _chunk.EmitByte(e.IsPrefix ? (byte)1 : (byte)0);
                return;
            }

            if (e.Target is MemberExpr me)
            {
                // 'ClassName.staticField++' (SPEC "Static members") -
                // no object on the stack, GetStaticField/SetStaticField
                // instead of GetField/SetField, otherwise the same technique as below.
                if (_refs.TryGetValue(me, out var staticIncDecRef) && staticIncDecRef is ResolvedRef.StaticMember stm)
                {
                    int classNameConstIdx = _chunk.AddConstant(Value.MakeString(stm.ClassName));
                    int fieldNameConstIdx = _chunk.AddConstant(Value.MakeString(me.Name));
                    _chunk.EmitOp(OpCode.GetStaticField);
                    _chunk.EmitU16(classNameConstIdx);
                    _chunk.EmitU16(fieldNameConstIdx);
                    if (!e.IsPrefix) _chunk.EmitOp(OpCode.Dup); // Postfix: [oldVal, oldVal]
                    EmitLoadConst(Value.MakeInt(1));
                    _chunk.EmitOp(addSubOp); // Prefix: [newVal] / Postfix: [oldVal, newVal]
                    _chunk.EmitOp(OpCode.SetStaticField);
                    _chunk.EmitU16(classNameConstIdx);
                    _chunk.EmitU16(fieldNameConstIdx);
                    // SetStaticField pops+pushes the same value again (like
                    // SetField) - stack size stays UNCHANGED. Prefix:
                    // [newVal] is thus already the desired result. Postfix:
                    // [oldVal, newVal] - the upper (new) copy still has to go, so that
                    // oldVal remains as the result.
                    if (!e.IsPrefix) _chunk.EmitOp(OpCode.Pop);
                    return;
                }

                CompileExpr(me.Target);           // [obj]
                _chunk.EmitOp(OpCode.Dup);         // [obj, obj]
                _chunk.EmitOp(OpCode.GetField);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name))); // [obj, oldVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Dup); // Postfix: [obj, oldVal, oldVal]
                EmitLoadConst(Value.MakeInt(1));
                _chunk.EmitOp(addSubOp);           // Prefix: [obj, newVal] / Postfix: [obj, oldVal, newVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.RotateUnderTop); // [oldVal, obj, newVal]
                _chunk.EmitOp(OpCode.SetField);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name))); // [...,newVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Pop); // [oldVal]
                return;
            }

            if (e.Target is UnaryExpr { Op: UnaryOp.Dereference } deref)
            {
                CompileExpr(deref.Operand);        // [ptr]
                _chunk.EmitOp(OpCode.Dup);          // [ptr, ptr]
                _chunk.EmitOp(OpCode.PtrRead);       // [ptr, oldVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Dup); // Postfix: [ptr, oldVal, oldVal]
                EmitLoadConst(Value.MakeInt(1));
                _chunk.EmitOp(addSubOp);
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.RotateUnderTop); // [oldVal, ptr, newVal]
                _chunk.EmitOp(OpCode.PtrWrite);       // [...,newVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Pop); // [oldVal]
                return;
            }

            if (e.Target is not IdentifierExpr id)
                throw new NotSupportedException("Invalid target for '++'/'--' in the bytecode compiler.");

            var refKind = _refs[id];

            // SPEC "Static members"/"Implicit member references":
            // a bare name that refers to a static or (implicitly via
            // 'this') instance field - a self-contained bytecode
            // sequence of its own instead of the generic EmitLoad/EmitStore
            // below (which are tailored to local/global, need no
            // additional object/class name on the stack).
            if (refKind is ResolvedRef.StaticMember sm)
            {
                int classNameConstIdx = _chunk.AddConstant(Value.MakeString(sm.ClassName));
                int fieldNameConstIdx = _chunk.AddConstant(Value.MakeString(id.Name));
                _chunk.EmitOp(OpCode.GetStaticField);
                _chunk.EmitU16(classNameConstIdx);
                _chunk.EmitU16(fieldNameConstIdx);
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Dup); // Postfix: [oldVal, oldVal]
                EmitLoadConst(Value.MakeInt(1));
                _chunk.EmitOp(addSubOp); // Prefix: [newVal] / Postfix: [oldVal, newVal]
                _chunk.EmitOp(OpCode.SetStaticField);
                _chunk.EmitU16(classNameConstIdx);
                _chunk.EmitU16(fieldNameConstIdx);
                // SetStaticField pops+pushes the same value again (like
                // SetField) - stack size stays UNCHANGED (see the same
                // derivation at the MemberExpr case above).
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Pop);
                return;
            }

            if (refKind is ResolvedRef.ImplicitThisMember)
            {
                // Like 'this.field++' above (MemberExpr case), only that 'this'
                // is implicit here instead of written out.
                _chunk.EmitOp(OpCode.LoadThis);   // [obj]
                _chunk.EmitOp(OpCode.Dup);         // [obj, obj]
                _chunk.EmitOp(OpCode.GetField);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name))); // [obj, oldVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Dup); // Postfix: [obj, oldVal, oldVal]
                EmitLoadConst(Value.MakeInt(1));
                _chunk.EmitOp(addSubOp);           // Prefix: [obj, newVal] / Postfix: [obj, oldVal, newVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.RotateUnderTop); // [oldVal, obj, newVal]
                _chunk.EmitOp(OpCode.SetField);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name))); // [...,newVal]
                if (!e.IsPrefix) _chunk.EmitOp(OpCode.Pop); // [oldVal]
                return;
            }

            void EmitLoad()
            {
                switch (refKind)
                {
                    case ResolvedRef.Local local:
                        EmitLoadVariable(local);
                        break;
                    case ResolvedRef.Global global:
                        _chunk.EmitOp(OpCode.LoadGlobal);
                        _chunk.EmitU16(global.Slot);
                        break;
                    default:
                        throw new NotSupportedException($"'++'/'--' on '{id.Name}' is not possible.");
                }
            }
            void EmitStore()
            {
                switch (refKind)
                {
                    case ResolvedRef.Local local:
                        // SPEC "Unit declarations" - the same check as
                        // with every normal assignment (see CompileAssign) -
                        // `++`/`--` is after all only an (more compactly written)
                        // assignment.
                        EmitCheckUnitIfNeeded(this, local.RequiredUnit);
                        EmitStoreVariable(local);
                        break;
                    case ResolvedRef.Global global:
                        EmitCheckUnitIfNeeded(this, global.RequiredUnit);
                        _chunk.EmitOp(OpCode.StoreGlobal);
                        _chunk.EmitU16(global.Slot);
                        break;
                }
            }

            EmitLoad();                              // [oldVal]
            if (!e.IsPrefix) _chunk.EmitOp(OpCode.Dup); // Postfix: [oldVal, oldVal]
            EmitLoadConst(Value.MakeInt(1));
            _chunk.EmitOp(addSubOp);                  // Prefix: [newVal] / postfix: [oldVal, newVal]
            EmitStore();                              // Store* leaves the value (peek instead of pop) on the stack
            if (!e.IsPrefix) _chunk.EmitOp(OpCode.Pop); // [oldVal]
        }

        private void CompileUnary(UnaryExpr u)
        {
            if (u.Op == UnaryOp.AddressOf)
            {
                CompileAddressOf(u.Operand);
                return;
            }

            if (u.Op == UnaryOp.Take)
            {
                // `var a = take x` (SPEC 2.2): x belongs to the current scope (other places - arguments, assignments - are handled where they occur)
                CompileExpr(u.Operand);
                EmitTakeToScope(0);
                return;
            }
            if (u.Op is UnaryOp.FlatCopy or UnaryOp.DeepCopy)
            {
                CompileExpr(u.Operand);
                _chunk.EmitOp(OpCode.CopyValue);
                _chunk.EmitByte(u.Op == UnaryOp.DeepCopy ? (byte)1 : (byte)0);
                return;
            }

            CompileExpr(u.Operand);
            _chunk.EmitOp(u.Op switch
            {
                UnaryOp.Negate => OpCode.Neg,
                UnaryOp.LogicalNot => OpCode.LogicalNot,
                UnaryOp.BitNot => OpCode.BitNot,
                UnaryOp.Dereference => OpCode.PtrRead,
                _ => throw new NotSupportedException($"UnaryOp {u.Op}"),
            });
        }

        /// <summary>`&amp;expression` - applicable only to variables (local/global) or
        /// object fields (the only "addressable" expressions
        /// of this language, analogous to lvalues in C#). The resolver has already
        /// checked that we are inside an 'unsafe' block.</summary>
        private void CompileAddressOf(Expr operand)
        {
            switch (operand)
            {
                case IdentifierExpr id:
                    switch (_refs[id])
                    {
                        case ResolvedRef.Local local:
                            EmitAddressOfLocal(local);
                            return;
                        case ResolvedRef.Global global:
                            _chunk.EmitOp(OpCode.AddressOfGlobal);
                            _chunk.EmitU16(global.Slot);
                            return;
                        default:
                            throw new NotSupportedException(
                                "'&' can only be applied to local/global variables or object fields.");
                    }

                case MemberExpr me:
                    CompileExpr(me.Target);
                    _chunk.EmitOp(OpCode.AddressOfField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                    return;

                default:
                    throw new NotSupportedException(
                        "'&' can only be applied to variables or object fields (not a valid address target).");
            }
        }

        private void CompileBinary(BinaryExpr b)
        {
            if (b.Op == BinaryOp.And) { CompileLogicalAnd(b); return; }
            if (b.Op == BinaryOp.Or) { CompileLogicalOr(b); return; }

            EmitCoercedOperands(b.Left, b.Right);

            _chunk.EmitOp(b.Op switch
            {
                BinaryOp.Add => OpCode.Add,
                BinaryOp.Sub => OpCode.Sub,
                BinaryOp.Mul => OpCode.Mul,
                BinaryOp.Div => OpCode.Div,
                BinaryOp.Mod => OpCode.Mod,
                BinaryOp.Power => OpCode.Power,
                BinaryOp.BitAnd => OpCode.BitAnd,
                BinaryOp.BitOr => OpCode.BitOr,
                BinaryOp.BitXor => OpCode.BitXor,
                BinaryOp.ShiftLeft => OpCode.ShiftLeft,
                BinaryOp.ShiftRight => OpCode.ShiftRight,
                BinaryOp.Eq => OpCode.Eq,
                BinaryOp.NotEq => OpCode.NotEq,
                BinaryOp.Lt => OpCode.Lt,
                BinaryOp.LtEq => OpCode.LtEq,
                BinaryOp.Gt => OpCode.Gt,
                BinaryOp.GtEq => OpCode.GtEq,
                _ => throw new NotSupportedException($"BinaryOp {b.Op}"),
            });
        }

        private void CompileLogicalAnd(BinaryExpr b)
        {
            CompileExpr(b.Left);
            _chunk.EmitOp(OpCode.JumpIfFalsePeek);
            int endJumpAt = _chunk.Here;
            _chunk.EmitU16(0);
            _chunk.EmitOp(OpCode.Pop);
            CompileExpr(b.Right);
            _chunk.PatchU16(endJumpAt, _chunk.Here);
        }

        private void CompileLogicalOr(BinaryExpr b)
        {
            CompileExpr(b.Left);
            _chunk.EmitOp(OpCode.JumpIfTruePeek);
            int endJumpAt = _chunk.Here;
            _chunk.EmitU16(0);
            _chunk.EmitOp(OpCode.Pop);
            CompileExpr(b.Right);
            _chunk.PatchU16(endJumpAt, _chunk.Here);
        }

        // -----------------------------------------------------------
        // The anchor rule (SPEC 3.2) - core of the compiler.
        //
        // For every operand AnalyzeCoercion determines whether it requests ':' (unit)
        // and/or '!' (type), and whether in each case explicitly (fixed value) or
        // "automatically" (no argument). The operand that requests "automatically" on NEITHER axis
        // is the anchor; the other is adjusted to it dynamically (at
        // runtime, since variables only then carry their unit/type)
        // adjusted to it. If BOTH request "automatically", the unit falls back to
        // unitless (SPEC requirement); the type stays untouched in this case
        // (no explicit SPEC requirement - Add/Sub/etc. promote int+float automatically
        // to float anyway, which already covers the practical case).
        // -----------------------------------------------------------
        private void EmitCoercedOperands(Expr leftExpr, Expr rightExpr)
        {
            var left = AnalyzeCoercion(leftExpr);
            var right = AnalyzeCoercion(rightExpr);

            CompileExpr(left.Inner);
            if (left.ExplicitType != null) EmitCoerceTypeStatic(TokenTypeToTag(left.ExplicitType.Value));
            if (left.ExplicitUnit != null) EmitCoerceUnitStatic(fire.Values.Unit.Parse(left.ExplicitUnit));

            CompileExpr(right.Inner);
            if (right.ExplicitType != null) EmitCoerceTypeStatic(TokenTypeToTag(right.ExplicitType.Value));
            if (right.ExplicitUnit != null) EmitCoerceUnitStatic(fire.Values.Unit.Parse(right.ExplicitUnit));

            // Stack now: [..., left', right']
            bool leftAuto = left.RequestsAnyAuto;
            bool rightAuto = right.RequestsAnyAuto;

            if (leftAuto && !rightAuto)
            {
                // right (TOS) is the anchor; left lies below it -> swap, adjust, swap back.
                // Adjust type BEFORE unit: otherwise with int->float conversion
                // combined with a unit conversion precision is lost through
                // premature integer rounding (e.g. 500m -> 1km instead of 0.5km,
                // if it were first rounded to km and only then promoted to float).
                _chunk.EmitOp(OpCode.Swap);
                if (left.TypeIsAuto) _chunk.EmitOp(OpCode.CoerceTypeDynamic);
                if (left.UnitIsAuto) _chunk.EmitOp(OpCode.CoerceUnitDynamic);
                _chunk.EmitOp(OpCode.Swap);
            }
            else if (rightAuto && !leftAuto)
            {
                // left (TOS-1) is the anchor; adjust right (TOS) directly (type before unit, see above).
                if (right.TypeIsAuto) _chunk.EmitOp(OpCode.CoerceTypeDynamic);
                if (right.UnitIsAuto) _chunk.EmitOp(OpCode.CoerceUnitDynamic);
            }
            else if (leftAuto && rightAuto)
            {
                // No anchor present -> unit falls back to unitless.
                if (left.UnitIsAuto)
                {
                    _chunk.EmitOp(OpCode.Swap);
                    EmitCoerceUnitStatic(fire.Values.Unit.Unitless);
                    _chunk.EmitOp(OpCode.Swap);
                }
                if (right.UnitIsAuto) EmitCoerceUnitStatic(fire.Values.Unit.Unitless);
            }
            // otherwise: both fixed/explicit -> no further adjustment; the
            // arithmetic operation itself checks compatibility at runtime.
        }

        private readonly record struct CoercionInfo(
            Expr Inner, bool WantsUnit, string? ExplicitUnit, bool WantsType, TokenType? ExplicitType)
        {
            public bool UnitIsAuto => WantsUnit && ExplicitUnit == null;
            public bool TypeIsAuto => WantsType && ExplicitType == null;
            public bool RequestsAnyAuto => UnitIsAuto || TypeIsAuto;
        }

        /// <summary>Peels ':'/'!' postfix wrappers (in any order, also
        /// both) off an expression and classifies what was requested
        /// in each case.</summary>
        private static CoercionInfo AnalyzeCoercion(Expr expr)
        {
            bool wantsUnit = false; string? explicitUnit = null;
            bool wantsType = false; TokenType? explicitType = null;
            var current = expr;

            while (true)
            {
                if (current is UnitCoerceExpr uc)
                {
                    wantsUnit = true;
                    explicitUnit = uc.TargetUnitName;
                    current = uc.Operand;
                }
                else if (current is TypeCoerceExpr tc)
                {
                    wantsType = true;
                    explicitType = tc.TargetTypeKeyword;
                    current = tc.Operand;
                }
                else
                {
                    break;
                }
            }

            return new CoercionInfo(current, wantsUnit, explicitUnit, wantsType, explicitType);
        }

        private static TypeTag TokenTypeToTag(TokenType t) => t switch
        {
            TokenType.KwBool => TypeTag.Bool,
            TokenType.KwInt => TypeTag.Int,
            TokenType.KwFloat => TypeTag.Float,
            TokenType.KwChar => TypeTag.Char,
            TokenType.KwString => TypeTag.String,
            _ => throw new NotSupportedException($"Coercion target {t} is not supported."),
        };

        // -----------------------------------------------------------
        // Emit-Helfer
        // -----------------------------------------------------------
        private void EmitLoadConst(Value v)
        {
            _chunk.EmitOp(OpCode.LoadConst);
            _chunk.EmitU16(_chunk.AddConstant(v));
        }

        private void EmitCoerceUnitStatic(fire.Values.Unit unit)
        {
            _chunk.EmitOp(OpCode.CoerceUnit);
            _chunk.EmitU16(_chunk.AddUnit(unit));
        }

        private void EmitCoerceTypeStatic(TypeTag tag)
        {
            _chunk.EmitOp(OpCode.CoerceType);
            _chunk.EmitByte((byte)tag);
        }
    }
}
