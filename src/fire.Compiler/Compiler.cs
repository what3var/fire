using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
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

        /// <summary>The message without the line.</summary>
        public string RawMessage { get; }

        /// <summary>The code of the message (docs/tools/errors.md).</summary>
        public string Code => ErrorCatalog.CodeOf(ErrorArea.Compiler, RawMessage);

        public IReadOnlyList<CompilerException> Errors { get; }

        /// <summary>The index of the source (Parser.ParseMultiple) the error is in; -1 when unknown. The editor shows only the errors of the open file.</summary>
        public int Source { get; internal set; } = -1;

        internal CompilerException At(int source) { if (Source < 0) Source = source; return this; }

        public CompilerException(string message, int line)
            : base($"{message} ({line})")
        {
            Line = line;
            RawMessage = message;
            Errors = new[] { this };
        }

        /// <summary>Combines several collected errors (at least one).</summary>
        public CompilerException(IReadOnlyList<CompilerException> errors)
            : base(errors[0].Message)
        {
            Line = errors[0].Line;
            RawMessage = errors[0].RawMessage;
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
        /// <summary>The run-time types of the enums of the program (Values.EnumType), by full name.</summary>
        private Dictionary<string, EnumType> _enumTypes = new(StringComparer.Ordinal);   // (shared with the compilers of the methods, lambdas and default values)

        /// <summary>The enum a type name means (its full name, or the name in the namespace of the class that is compiled), or null.</summary>
        private EnumType? EnumTypeNamed(string name)
        {
            if (_enumTypes.TryGetValue(name, out var type)) return type;
            foreach (var t in _enumTypes.Values) if (t.Matches(name)) return t;
            return null;
        }

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

            /// <summary>Per jump (`break`/`continue` and the number of loop levels it still has to leave outside the `try`) that leaves the `try`: the places
            /// (operands of `PushJump`) waiting for the address of the exit piece behind the `finally` (see CompileTry).</summary>
            public readonly SortedDictionary<(bool IsBreak, int Levels), List<int>> StubPatches = new();
        }

        private readonly List<TryCompileContext> _tryStack = new();

        /// <summary>Everything a block leaves on the operand stack while it runs (from bottom to top): a `foreach` its enumerator (1),
        /// a `finally` block its completion (2). A `return` in it removes them before returning (swap + pop per entry), otherwise they would stay
        /// below the return value and shift the caller's operands.</summary>
        private readonly List<int> _residents = new();

        /// <summary>Common compilation for `break`/`continue` (also `break 2`/`continue 3`): leaves from the inside out all `try`/`catch` blocks that have
        /// been open since the body of the target loop (close scopes, unregister handlers or discard the catch state) and the inner loops (the enumerators of
        /// `foreach` loops on the stack). If it meets a `try` WITH `finally` on the way, it does not jump on by itself, but into its `finally` (completion
        /// "jump"); after the `finally` an exit piece behind the `try` continues the journey (it is the same jump, only translated from outside the `try`
        /// with the loops inside the `try` already left, see CompileTry) - so every `finally` on the way runs, innermost first. Otherwise it closes the
        /// remaining scopes and jumps - the address goes into the matching list (Break-/ContinueJumpPatchAddrs) of the target loop and is resolved when
        /// that loop is compiled completely.</summary>
        private void CompileBreakOrContinue(bool isBreak, int levels = 1)
        {
            var loops = _loopStack.Take(levels).ToList();      // innermost first
            var target = loops[^1];
            int targetLoopCount = _loopStack.Count - levels + 1;   // loops open inside the target loop's body: count >= this
            int depth = _currentScopeDepth;      // actual depth at the jump; `_currentScopeDepth` itself stays unchanged

            for (int k = _tryStack.Count - 1; k >= 0 && _tryStack[k].LoopCount >= targetLoopCount; k--)
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
                    // the loops inside this `try` are left here: their enumerators go, so the `finally` finds the stack as the `try` found it
                    int inside = _loopStack.Count - t.LoopCount;
                    foreach (var inner in loops.Take(inside))
                        if (inner.IsForeach) _chunk.EmitOp(OpCode.Pop);
                    // into the finally of this `try`: afterwards it continues at the exit piece behind the `try` (with the levels that remain outside it)
                    _chunk.EmitOp(OpCode.PushJump);
                    var key = (isBreak, levels - inside);
                    if (!t.StubPatches.TryGetValue(key, out var patches)) t.StubPatches[key] = patches = new List<int>();
                    patches.Add(_chunk.Here);
                    _chunk.EmitU16(0);
                    _chunk.EmitOp(OpCode.Jump);
                    t.FinallyJumpPatches.Add(_chunk.Here);
                    _chunk.EmitU16(0);
                    return;
                }
            }

            for (; depth > target.ScopeDepthAtLoopBodyStart; depth--) _chunk.EmitOp(OpCode.ExitScope);
            // the enumerators of the inner `foreach` loops lie on the stack above the target loop's own values
            foreach (var inner in loops.Take(levels - 1))
                if (inner.IsForeach) _chunk.EmitOp(OpCode.Pop);
            _chunk.EmitOp(OpCode.Jump);
            (isBreak ? target.BreakJumpPatchAddrs : target.ContinueJumpPatchAddrs).Add(_chunk.Here);
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
        private int CurrentSourceIndex => s_memberSource ?? _enclosingClass?.Decl.Source ?? _topLevelSourceIndex;

        /// <summary>The source of the member of a class being compiled - for a member of `class extends X` that of the extension, not of X (its lines lie in the
        /// file of the extension; the native backend gives the member to the module of that file, ROADMAP 9). Null outside a member.</summary>
        [ThreadStatic] private static int? s_memberSource;

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
            private readonly HashSet<string> _methodNames = new();
            private readonly HashSet<string> _lambdaFieldNames = new();
            private readonly Dictionary<string, int> _fieldWidths = new();
            // the unit demand of the elements of typed array fields (`float:cm sizes[]`); names declared with different units are not typed (see _fieldWidths)
            private readonly Dictionary<string, string?> _fieldUnits = new();
            private readonly HashSet<string> _typeParamNames = new();
            /// <summary>The interfaces of the program (CompileClasses): a declaration with one is checked like one with a class.</summary>
            public readonly HashSet<string> InterfaceNames = new();
            /// <summary>The enums of the program: a type argument that names one is an `int`.</summary>
            public readonly HashSet<string> EnumNames = new();
            /// <summary>The declarations of the classes (by their name, generic ones with the `N suffix): the `where` of a type parameter.</summary>
            public readonly Dictionary<string, ClassDecl> ClassDecls = new();
            /// <summary>A name some class or method uses as a type parameter (`T`): a declaration of that type is not checked at run time.</summary>
            public bool IsTypeParam(string name) => _typeParamNames.Contains(name);

            public static RefParamTable Build(IEnumerable<ClassDecl> classes)
            {
                var table = new RefParamTable();
                foreach (var cls in classes)
                {
                    table.ClassDecls[cls.Name] = cls;
                    foreach (var tp in cls.TypeParams ?? Array.Empty<TypeParam>()) table._typeParamNames.Add(tp.Name);
                    foreach (var md in cls.Members.OfType<MethodDecl>())
                        foreach (var tp in md.TypeParams ?? Array.Empty<TypeParam>()) table._typeParamNames.Add(tp.Name);
                }
                // ALL methods take part (also those without `ref`), so that a contradiction between classes becomes apparent
                foreach (var cls in classes)
                    foreach (var member in cls.Members)
                    {
                        if (member is FieldDecl wd)
                        {
                            // the width of a field name; names declared with different widths in different classes are not cut (-1)
                            // (a typed array field, `int[8] data[]`: the code of its element type, see TypeRef.ArrayWidthCode)
                            int code = wd.ArrayRanks.Count == 0 ? wd.Type?.WidthCode ?? 0 : wd.Type?.ArrayWidthCode(wd.ArrayRanks.Count) ?? 0;
                            if ((code & 0x10000) != 0 && table._fieldUnits.TryGetValue(wd.Name, out var knownUnit) && knownUnit != wd.Type?.UnitDemand) code = -1;
                            if ((code & 0x10000) != 0) table._fieldUnits[wd.Name] = wd.Type?.UnitDemand;
                            table._fieldWidths[wd.Name] = table._fieldWidths.TryGetValue(wd.Name, out int known) && known != code ? -1 : code;
                        }
                        if (member is MethodDecl md) { table.Register(table._methods, md.Name, md.Params, md.Line, $"method '{md.Name}'"); table._methodNames.Add(md.Name); }
                        else if (member is FieldDecl fd && fd.ArrayRanks.Count == 0 && (fd.Type == null || fd.Type.LambdaSignature != null || fd.Type.BaseName is "lambda" or "any"))
                            table._lambdaFieldNames.Add(fd.Name);
                        else if (member is PropertyDecl pd && (pd.Type == null || pd.Type.LambdaSignature != null || pd.Type.BaseName is "lambda" or "any"))
                            table._lambdaFieldNames.Add(pd.Name);
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
            /// <summary>`obj.name(...)` where no class has a method `name`, but a field or property `name` can hold a lambda: the lambda in it is called.</summary>
            /// <summary>The bit width (TypeRef.WidthCode) of the fields called `name` - 0 when none declares one, or when classes declare it differently.</summary>
            public int FieldWidth(string name) => _fieldWidths.TryGetValue(name, out int code) && code > 0 ? code : 0;
            /// <summary>The unit demand of the elements of a typed array field (null: none).</summary>
            public string? FieldUnit(string name) => _fieldUnits.GetValueOrDefault(name);
            public bool IsLambdaField(string name) => _lambdaFieldNames.Contains(name) && !_methodNames.Contains(name);
            public bool[]? ForConstructor(string cls, int argc) => _constructors.TryGetValue((cls, argc), out var m) && m.Any(x => x) ? m : null;
        }

        /// <summary>Does the program use the reflection library (`#import "reflection"`)? Then the compiler writes the declared types along as
        /// <see cref="ClassMeta"/> and marks the classes of the library.</summary>
        private bool Reflection => _natives.Has(fire.Standard.ReflectionPrelude.MembersNative);

        /// <summary>Write the <see cref="ClassMeta"/> even without the reflection library (a plugin: the program that loads it reflects on its classes).</summary>
        private bool _classMeta;

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
        /// <summary>What a declaration `C x` (a parameter, a field) with a class, an interface or a type parameter of `owner` that has `where` conditions
        /// promises, checked at run time (GuardLocalType, FieldInfo.RequiredType, DeclaredType); null for a number, a string, an unconstrained type
        /// parameter (or one of a method), an array, a lambda, `any`...</summary>
        private string? DeclaredTypeOf(TypeRef? type, RuntimeClass? owner) => MapSpec(DeclaredTypeOfCore(type, owner));

        private string? DeclaredTypeOfCore(TypeRef? type, RuntimeClass? owner)
        {
            if (type is not { PointerDepth: 0, ArrayRank: 0, LambdaSignature: null, IsInferred: false } || _knownClassNames == null) return null;
            bool Known(string n) => _knownClassNames.Contains(n) || _refParams.InterfaceNames.Contains(n);
            string Resolve(string n) => (type with { BaseName = n, TypeArgs = null, TypeArgCount = 0 }).ResolveBaseName(Known);
            if (_methodTypeParams?.ToList().FindIndex(tp => tp.Name == type.BaseName) is var m and >= 0)
                return DeclaredType.OfTypeParam(_methodTypeParams![m!.Value], m.Value, "", Resolve);   // (the type arguments of the call: Scope.TypeArgs)
            if (owner?.Decl?.TypeParams is { } ownParams && ownParams.ToList().FindIndex(tp => tp.Name == type.BaseName) is var k and >= 0)
                return DeclaredType.OfTypeParam(ownParams[k], k, owner.Name, Resolve);
            if (_refParams.IsTypeParam(type.BaseName)) return null;
            string name = type.TypeArgs is { Count: > 0 } args ? GenericClassNames.ResolveNewTarget(type, args.Count, Known) : type.ResolveBaseName(Known);
            return Known(name) ? DeclaredType.OfName(name) : null;
        }

        /// <summary>A type argument as the run time keeps it (DeclaredType text): `int`, a class or an interface, `in:cm` for a unit (when the type parameter
        /// `index` of `target` has an `is in`), `$k@Class` for the k-th type parameter of the class `owner` (taken from `this`), "" for what is not known
        /// at run time (a type parameter of a method, `any`, an array, a lambda).</summary>
        private string TypeArgText(TypeRef tr, RuntimeClass? owner, ClassDecl? target, int index)
        {
            if (tr.LambdaSignature != null || tr.ArrayRank > 0 || tr.PointerDepth > 0 || tr.IsInferred || _knownClassNames == null) return "";
            if (_methodTypeParams?.ToList().FindIndex(tp => tp.Name == tr.BaseName) is var m and >= 0) return $"${m}@";
            if (owner?.Decl?.TypeParams is { } ownParams && ownParams.ToList().FindIndex(tp => tp.Name == tr.BaseName) is var k and >= 0) return $"${k}@{owner.Name}";
            if (_refParams.IsTypeParam(tr.BaseName)) return "";
            switch (tr.BaseName)
            {
                // a number without a unit is unitless (like a declared `int`); `int : cm` is one in cm, `int : any` one in any unit
                case "int": case "float": return tr.Unit == null ? tr.BaseName : tr.BaseName + ":" + tr.Unit;
                case "bool": case "char": case "string": return tr.BaseName;
                case "any": case "class": return "";
            }
            if (_refParams.EnumNames.Contains(tr.BaseName)) return "";   // (an enum: the type checker sees to it; at run time `Color c` takes what it gets, like `any`)
            bool Known(string n) => _knownClassNames.Contains(n) || _refParams.InterfaceNames.Contains(n);
            string name = tr.TypeArgs is { Count: > 0 } args ? GenericClassNames.ResolveNewTarget(tr, args.Count, Known) : tr.ResolveBaseName(Known);
            if (Known(name)) return name;
            if (target?.TypeParams is { } tps && index < tps.Count && tps[index].ConstraintGroups.Any(g => g.Constraints.Any(c => c.Kind == TypeConstraintKind.IsIn)))
                return "in:" + tr.BaseName;
            return "";
        }

        /// <summary>In a lambda: the type parameters of the code around it that its body uses (`$k@` of the method, `$k@Class` of the class, as that
        /// code names them) - the lambda takes them along when it is made (SetTypeArgs for a lambda, LambdaValue.TypeArgs) and names the i-th `$i@`.
        /// Null outside of a lambda.</summary>
        private List<string>? _lambdaTypeRefs;

        /// <summary>A type argument text as the code being compiled names it (in a lambda: the type parameter it took along).</summary>
        private string MapTypeRef(string text)
        {
            if (_lambdaTypeRefs == null || !text.StartsWith('$')) return text;
            int i = _lambdaTypeRefs.IndexOf(text);
            if (i < 0) { i = _lambdaTypeRefs.Count; _lambdaTypeRefs.Add(text); }
            return $"${i}@";
        }

        private string MapTypeRefs(IEnumerable<string> texts) => string.Join("|", texts.Select(MapTypeRef));

        /// <summary>A DeclaredType of a type parameter as the code being compiled names it (in a lambda: the type parameter it took along).</summary>
        private string? MapSpec(string? spec)
        {
            if (spec == null || _lambdaTypeRefs == null || DeclaredType.Parse(spec) is not { TypeParamIndex: >= 0 } parsed) return spec;
            string mapped = MapTypeRef($"${parsed.TypeParamIndex}@{parsed.TypeParamClass}");
            int start = spec.IndexOf('\u0001'), end = spec.IndexOf('\u0003');
            return spec.Substring(0, start + 1) + "@" + mapped.Substring(1, mapped.Length - 2) + "@" + spec.Substring(end);
        }

        /// <summary>`new Box<...>(...)`: the type arguments for the object (SetTypeArgs, right before NewObject/NewObjectOwned); nothing when none is known.</summary>
        private void EmitTypeArgs(NewExpr ne)
        {
            EmitOverloadHint(ne);
            if (ne.ClassRef.TypeArgs is not { Count: > 0 } typeArgs) return;
            _refParams.ClassDecls.TryGetValue(ResolveNewClassName(ne), out var target);
            var texts = typeArgs.Select((t, i) => MapTypeRef(TypeArgText(t, _enclosingClass, target, i))).ToList();
            if (texts.All(t => t.Length == 0)) return;
            _chunk.EmitOp(OpCode.SetTypeArgs);
            _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(string.Join("|", texts))));
            _chunk.EmitByte(0);
        }

        /// <summary>The type of a `catch`: a class, or an interface the exception class implements (`catch (IRetryable e)`).</summary>
        private string ResolveCatchType(TypeRef tr)
        {
            string name = ResolveTypeRef(tr);
            if (_knownClassNames == null || _knownClassNames.Contains(name) || tr.TypeArgs is { Count: > 0 }) return name;
            string iface = tr.ResolveBaseName(n => _refParams.InterfaceNames.Contains(n));
            return _refParams.InterfaceNames.Contains(iface) ? iface : name;
        }

        private string ResolveTypeRef(TypeRef tr) =>
            _knownClassNames == null ? tr.BaseName
            : tr.TypeArgs is { Count: > 0 } args ? GenericClassNames.ResolveNewTarget(tr, args.Count, _knownClassNames.Contains)
            : tr.ResolveBaseName(_knownClassNames.Contains);

        /// <summary>The class name that `new Name&lt;...&gt;(...)` instantiates - the
        /// number of type arguments chooses between a non-generic and
        /// a generic class of the same name (see GenericClassNames;
        /// same choice as in the resolver).</summary>
        private string ResolveNewClassName(NewExpr ne) =>
            _knownClassNames != null
                ? GenericClassNames.ResolveNewTarget(ne.ClassRef, ne.TypeArgs?.Count ?? 0, _knownClassNames.Contains)
                : ne.ClassRef.BaseName;

        public static CompiledProgram Compile(
            IReadOnlyList<Stmt> program, ResolveResult resolveResult, NativeRegistry natives, bool classMeta = false,
            GlobalJobsMode? globalJobsForced = null, GlobalJobsMode? globalJobsDefault = null)
        {
            s_unitChecks = resolveResult.UnitChecks;
            s_methodTypeArgs = resolveResult.MethodTypeArgs;
            s_localGuards = resolveResult.LocalGuards;
            s_overloadHints = resolveResult.OverloadHints;
            var compiler = new Compiler(resolveResult, natives) { _classMeta = classMeta };
            var classes = compiler.CompileClasses(program);

            // When the main program processes its queue by itself (docs/spec/THREADING_DESIGN.md section 7): the command line and the project (`globalJobsForced`) go before
            // `#globaljobs`/`#nosync` (the last one in the top-level code, no matter where it stands), that before fire.native.json (`globalJobsDefault`); right at the
            // start, before any code runs. The default (safepoints) needs no instruction.
            var globalJobs = globalJobsForced ?? program.OfType<GlobalJobsDirective>().LastOrDefault()?.Mode ?? globalJobsDefault ?? GlobalJobsMode.Safepoints;
            if (globalJobs != GlobalJobsMode.Safepoints)
            {
                compiler._chunk.EmitOp(OpCode.SetAutoSync);
                compiler._chunk.EmitByte((byte)globalJobs);
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
                    EmitWidthIfNeeded(compiler, compiler._refParams.FieldWidth(fieldName), compiler._refParams.FieldUnit(fieldName));
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
                    // (an `inline c` parameter `byte data[]`: the array brackets stand after the name - the type gets its rank)
                    ParamTypes = ed.Params.Select(p => ed.InlineC != null && p.Type != null && p.ArrayRanks.Count > 0 ? p.Type with { ArrayRank = p.ArrayRanks.Count } : p.Type).ToList(),
                    ReturnType = ed.ReturnType,
                    InlineC = ed.InlineC,
                    ParamNames = ed.InlineC != null ? ed.Params.Select(p => p.Name).ToList() : null,
                    InlineSource = ed.Source,
                    InlineLine = ed.InlineLine,
                    DisplayName = ed.DisplayName,
                };
            }

            return new CompiledProgram { TopLevel = compiler._chunk, Classes = classes, ExternSignatures = externSignatures };
        }

        // -----------------------------------------------------------
        // Classes (pre-pass: name -> RuntimeClass, analogous to the resolver)
        // -----------------------------------------------------------
        /// <summary>The type as it stood in the source (`int`, `Circle`, `lambda<int>`, `float[]`), "" if not given.</summary>
        /// <summary>The value of an attribute argument as JSON when the compiler knows it (see AttributeMeta.ArgsJson), else null.</summary>
        private static System.Text.Json.Nodes.JsonNode? ConstantJson(Expr expr)
        {
            static System.Text.Json.Nodes.JsonNode Tagged(string tag, System.Text.Json.Nodes.JsonNode? value) => new System.Text.Json.Nodes.JsonObject { [tag] = value };
            switch (expr)
            {
                case LiteralExpr { Value: var v } when v.Kind == ValueKind.Int && (v.Unit == null || v.Unit.IsUnitless): return Tagged("i", v.AsInt());
                case LiteralExpr { Value: var v } when v.Kind == ValueKind.Float && (v.Unit == null || v.Unit.IsUnitless): return Tagged("f", v.AsFloat());
                case LiteralExpr { Value: var v } when v.Kind == ValueKind.String: return Tagged("s", v.AsString());
                case LiteralExpr { Value: var v } when v.Kind == ValueKind.Bool: return Tagged("b", v.AsBool());
                case UnaryExpr { Op: UnaryOp.Negate, Operand: LiteralExpr { Value: var v } } when (v.Unit == null || v.Unit.IsUnitless) && v.Kind == ValueKind.Int: return Tagged("i", -v.AsInt());
                case UnaryExpr { Op: UnaryOp.Negate, Operand: LiteralExpr { Value: var v } } when (v.Unit == null || v.Unit.IsUnitless) && v.Kind == ValueKind.Float: return Tagged("f", -v.AsFloat());
                case ArrayLiteralExpr array:
                    var items = array.Elements.Select(ConstantJson).ToList();
                    return items.Any(i => i == null) ? null : Tagged("a", new System.Text.Json.Nodes.JsonArray(items.ToArray()));
                case RangeExpr range when ConstantJson(range.From) is System.Text.Json.Nodes.JsonObject from && from["i"] is { } f
                                       && ConstantJson(range.To) is System.Text.Json.Nodes.JsonObject to && to["i"] is { } t:
                    long a = f.GetValue<long>(), b = t.GetValue<long>();
                    if (Math.Abs(b - a) > 10000) return null;
                    var numbers = new List<System.Text.Json.Nodes.JsonNode?>();
                    for (long n = a; a <= b ? n <= b : n >= b; n += a <= b ? 1 : -1) numbers.Add(Tagged("i", n));
                    return Tagged("a", new System.Text.Json.Nodes.JsonArray(numbers.ToArray()));
                default: return null;
            }
        }

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

        /// <summary>The declared type of a field for JSON (FieldInfo.TypeText): basic types as written, classes resolved, `$k` for a type parameter of the class.</summary>
        private string JsonTypeText(TypeRef? type, int extraArrayRank, ClassDecl? owner)
        {
            if (type == null || type.IsInferred || type.LambdaSignature != null || type.PointerDepth > 0) return "";
            string arrays = string.Concat(Enumerable.Repeat("[]", type.ArrayRank + extraArrayRank));
            string b = type.BaseName == "int" && type.BitWidth == 8 && type.Unsigned ? "byte" : type.BaseName;   // (`byte` is `int[8]`: a `byte[]` field is a buffer)
            if (type.TypeArgs == null && owner?.TypeParams is { } typeParams && typeParams.ToList().FindIndex(tp => tp.Name == b) is var k and >= 0) return "$" + k + arrays;
            switch (b)
            {
                case "int": case "float": case "bool": case "char": case "string": case "byte": case "word": case "class":
                case "int8": case "int16": case "int32": case "int64": case "uint8": case "uint16": case "uint32": case "uint64": case "float32": case "float64":
                    return b + arrays;
                case "any": return arrays.Length > 0 ? "any" + arrays : "";
            }
            if (_knownClassNames == null) return "";
            string name = ResolveTypeRef(type);
            bool isClass = _knownClassNames.Contains(name), isInterface = !isClass && _refParams.InterfaceNames.Contains(type.ResolveBaseName(n => _refParams.InterfaceNames.Contains(n)));
            if (!isClass && !isInterface) return arrays.Length > 0 ? "any" + arrays : "";   // (an enum is an int at run time)
            if (isInterface) name = type.ResolveBaseName(n => _refParams.InterfaceNames.Contains(n));
            if (type.TypeArgs is { Count: > 0 } args)
                name = GenericClassNames.PlainName(name) + "<" + string.Join(",", args.Select(a => JsonTypeText(a, 0, owner))) + ">";
            return name + arrays;
        }

        /// <summary>The attributes of a member for the runtime (names, argument texts and the values the compiler knows), null for none.</summary>
        private List<AttributeMeta>? AttributeMetas(IReadOnlyList<AttributeDecl>? attributes) =>
            attributes is not { Count: > 0 } ? null
            : attributes.Select(a => FlagAttribute(a) ?? new AttributeMeta { Name = a.Name, ArgTexts = a.ArgTexts.ToList(), ArgsJson = new System.Text.Json.Nodes.JsonArray(a.Args.Select(ConstantJson).ToArray()).ToJsonString() }).ToList();

        /// <summary>An attribute that names a flag enum (`[Test a, b]`, docs/language/10-enums-and-namespaces.md): its name becomes the full name of the enum and its
        /// arguments - names of flags, also `Test.a` - one value (`a | b`), which reflection reads (`member.HasFlag(Test.a)`); null for any other attribute.
        /// A name that is no flag of the enum is an error.</summary>
        private AttributeMeta? FlagAttribute(AttributeDecl a)
        {
            if (EnumTypeNamed(a.Name) is not { IsFlags: true } type) return null;
            long value = 0;
            for (int i = 0; i < a.Args.Count; i++)
            {
                string? flag = a.Args[i] switch
                {
                    IdentifierExpr id => id.Name,
                    MemberExpr { Target: IdentifierExpr or MemberExpr } me when type.Matches(DottedText(me.Target) ?? "") => me.Name,
                    _ => null,
                };
                var member = type.Members.FirstOrDefault(m => m.Name == flag);
                if (flag == null || member.Name == null)
                    throw new CompilerException($"'{a.ArgTexts[i]}' is not a flag of '{type.Name}' (attribute '[{a.Name} ...]')", a.Line);
                value |= member.Value;
            }
            return new AttributeMeta { Name = type.Name, ArgTexts = a.ArgTexts.ToList(), ArgsJson = $"[{{\"i\":{value}}}]" };
        }

        /// <summary>`A.B.C` as text (null for anything else).</summary>
        private static string? DottedText(Expr e) => e switch
        {
            IdentifierExpr id => id.Name,
            MemberExpr me when DottedText(me.Target) is { } left => left + "." + me.Name,
            _ => null,
        };

        /// <summary>The reflection metadata of a class: what the runtime otherwise does not keep (type names, parameter names, `readonly`, property form).</summary>
        /// <summary>The interface `name` and every interface it extends (`interface IConnection : IO.IStream`), each once, the interface itself first. A cycle
        /// (the resolver reports it) ends where a name repeats.</summary>
        internal static List<string> InterfaceClosure(string name, IReadOnlyDictionary<string, InterfaceDecl> interfaces)
        {
            var result = new List<string>();
            var pending = new Stack<string>();
            pending.Push(name);
            while (pending.Count > 0)
            {
                string n = pending.Pop();
                if (result.Contains(n)) continue;
                result.Add(n);
                if (!interfaces.TryGetValue(n, out var id)) continue;
                var bases = id.BaseRefs ?? Array.Empty<TypeRef>();
                for (int i = bases.Count - 1; i >= 0; i--)
                {
                    var b = bases[i];
                    bool Known(string k) => interfaces.ContainsKey(k);
                    pending.Push(b.TypeArgCount == 0 ? b.ResolveBaseName(Known) : GenericClassNames.ResolveNewTarget(b, b.TypeArgCount, Known));
                }
            }
            return result;
        }

        private ClassMeta BuildClassMeta(ClassDecl cd)
        {
            var meta = new ClassMeta();
            meta.Attributes = AttributeMetas(cd.Attributes) ?? new();
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
                            IsStatic = f.IsStatic, IsReadonly = f.IsReadonly, Unit = f.Type?.RequiredUnit ?? "",
                            Attributes = AttributeMetas(f.Attributes) ?? new(),
                        });
                        break;
                    case PropertyDecl pd:
                        meta.Members.Add(new MemberMeta
                        {
                            Name = pd.Name, Kind = "property", TypeName = TypeText(pd.Type), Access = AccessText(pd.Access),
                            IsStatic = pd.IsStatic, CanRead = pd.Getter != null, CanWrite = pd.Setter != null, Unit = pd.Type?.RequiredUnit ?? "",
                            Attributes = AttributeMetas(pd.Attributes) ?? new(),
                        });
                        break;
                    case MethodDecl md:
                        {
                            var m = new MemberMeta { Name = md.Name, Kind = "method", TypeName = TypeText(md.ReturnType), Access = AccessText(md.Access), IsStatic = md.IsStatic };
                            AddParams(m, md.Params);
                            m.Attributes = AttributeMetas(md.Attributes) ?? new();
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

        /// <summary>`[Keep]` (and `[Remote]`, docs/networking/http.md) before the class or its members (a native build keeps them, docs/spec/NATIVE_BACKEND.md), `[NotSerializable]` before the class (docs/stdlib/json.md) and `[NoCopy]` (SPEC 5.12).</summary>
        private static void MarkKeepAndSerializable(RuntimeClass rc, ClassDecl cd)
        {
            static bool Has(IReadOnlyList<AttributeDecl>? attributes, string name) => attributes?.Any(a => a.Name == name) == true;
            rc.Keep = Has(cd.Attributes, "Keep") || Has(cd.Attributes, "Remote") || Has(cd.Attributes, "Page");   // ([Remote], [Page]: http.rpc / http.pages make and call it by its name)
            if (Has(cd.Attributes, "NotSerializable")) rc.NotSerializable = true;
            rc.NoCopy = Has(cd.Attributes, "NoCopy");
            foreach (var member in cd.Members)
            {
                (string? name, IReadOnlyList<AttributeDecl>? attributes) = member switch
                {
                    FieldDecl f => (f.Name, f.Attributes),
                    PropertyDecl p => (p.Name, p.Attributes),
                    MethodDecl m => (m.Name, m.Attributes),
                    _ => ((string?)null, (IReadOnlyList<AttributeDecl>?)null),
                };
                if (name != null && (Has(attributes, "Keep") || Has(attributes, "Remote"))) (rc.KeptMembers ??= new()).Add(name);
            }
        }

        private Dictionary<string, RuntimeClass> CompileClasses(IReadOnlyList<Stmt> program)
        {
            // the run-time types of the enums first: the attributes of the classes may name a flag enum
            foreach (var ed in program.OfType<EnumDecl>()) _enumTypes[ed.Name] = Resolver.EnumTypeOf(ed);
            var classes = new Dictionary<string, RuntimeClass>();
            foreach (var stmt in program)
                if (stmt is ClassDecl cd)
                {
                    classes[cd.Name] = new RuntimeClass(cd.Name, cd);
                    MarkKeepAndSerializable(classes[cd.Name], cd);
                    if (Reflection || _classMeta)
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
            var interfaceDecls = new Dictionary<string, InterfaceDecl>();
            foreach (var id in program.OfType<InterfaceDecl>()) interfaceDecls.TryAdd(id.Name, id);
            _refParams.InterfaceNames.UnionWith(interfaceNames);
            _refParams.EnumNames.UnionWith(program.OfType<EnumDecl>().Select(e => e.Name));
            var baseRefOf = new Dictionary<RuntimeClass, TypeRef>();
            foreach (var rc in classes.Values)
            {
                foreach (var baseRef in rc.Decl.BaseRefs ?? Array.Empty<TypeRef>())
                {
                    bool Known(string name) => name == "Exception" || classes.ContainsKey(name) || interfaceNames.Contains(name);
                    string n = baseRef.TypeArgCount == 0 ? baseRef.ResolveBaseName(Known) : GenericClassNames.ResolveNewTarget(baseRef, baseRef.TypeArgCount, Known);
                    // (an interface and every interface it extends: the object is of each - `is of`, casts, overloads and the native isof tables read this list)
                    if (interfaceNames.Contains(n))
                    {
                        foreach (var i in InterfaceClosure(n, interfaceDecls)) if (!rc.Interfaces.Contains(i)) rc.Interfaces.Add(i);
                        if (rc.Meta != null && !rc.Meta.DeclaredInterfaces.Contains(n)) rc.Meta.DeclaredInterfaces.Add(n);   // (reflection: Type.DeclaredInterfaces)
                        continue;
                    }
                    if (n != "Exception" && !classes.TryGetValue(n, out _)) continue;
                    if (n == "Exception" && !classes.ContainsKey(n)) continue; // no RuntimeClass available (a program without the prelude) -> Base stays null
                    // (the interfaces may follow the base class: `class Puppy : Dog, IPet` - the loop goes on for them)
                    if (rc.Base != null) continue;
                    rc.Base = classes[n];
                    baseRefOf[rc] = baseRef;
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
                var set = new HashSet<RuntimeClass>();
                bool isActor = false;
                for (var walk = rc; walk != null; walk = walk.Base)
                {
                    if (walk.Decl.IsActor) { isActor = true; break; }
                    if (set.Contains(walk))
                    {
                        _errors.Add(new CompilerException("Circular class inheritance!", rc.Decl.Line).At(rc.Decl.Source));
                        break;
                    }
                    set.Add(walk);
                }
                rc.IsActor = isActor;
                rc.IsRecord = rc.Decl.IsRecord;
            }
            // a record derives only from a record, a class not from one (what a record is - unchangeable - holds for its whole chain)
            foreach (var rc in classes.Values)
            {
                if (rc.Base == null) continue;
                bool baseIsRecord = rc.Base.Decl?.IsRecord ?? rc.Base.IsRecord;
                if (rc.Decl.IsRecord && !baseIsRecord)
                    _errors.Add(new CompilerException($"The record '{rc.Name}' cannot derive from the class '{rc.Base.Name}': a record derives only from a record.", rc.Decl.Line).At(rc.Decl.Source));
                else if (!rc.Decl.IsRecord && baseIsRecord)
                    _errors.Add(new CompilerException($"The class '{rc.Name}' cannot derive from the record '{rc.Base.Name}': only a record derives from a record.", rc.Decl.Line).At(rc.Decl.Source));
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

            // the type arguments a class gives its generic base class (`class IntBox : Box<int>`): `$k` for its own k-th type parameter
            foreach (var (rc, baseRef) in baseRefOf)
                if (baseRef.TypeArgs is { Count: > 0 } baseArgs)
                    rc.BaseTypeArgs = baseArgs.Select((t, i) => TypeArgText(t, rc, rc.Base!.Decl, i) is var text && text.StartsWith('$') ? text.Substring(0, text.IndexOf('@')) : text).ToArray();

            foreach (var rc in classes.Values)
                CompileClassBody(rc);

            return classes;
        }

        /// <summary>What every method and property getter of the class reads of `this` (RuntimeClass.ThisReads): `this.x`, a member without `this.`
        /// (ImplicitThisMember), `this.M()` - also inside its lambdas.</summary>
        private Dictionary<string, string[]> ComputeThisReads(ClassDecl decl)
        {
            var result = new Dictionary<string, string[]>();
            void Add(string key, Stmt.BlockStmt body)
            {
                var reads = new HashSet<string>(result.TryGetValue(key, out var known) ? known : Array.Empty<string>());
                CollectThisReads(body, reads);
                result[key] = reads.ToArray();
            }
            foreach (var member in decl.Members)
            {
                if (member is MethodDecl { IsStatic: false } md) Add(md.Name, md.Body);
                else if (member is PropertyDecl { IsStatic: false, Getter: { } getter } pd) Add("get_" + pd.Name, getter);
            }
            return result;
        }

        private static readonly Dictionary<Type, System.Reflection.PropertyInfo[]> s_astChildren = new();

        private void CollectThisReads(object? node, HashSet<string> reads)
        {
            switch (node)
            {
                case null: return;
                case MemberExpr { Target: ThisExpr } member: reads.Add(member.Name); break;
                case IdentifierExpr id when _refs.TryGetValue(id, out var r) && r is ResolvedRef.ImplicitThisMember: reads.Add(id.Name); break;
                case string: return;
                case System.Collections.IEnumerable items:
                    foreach (var item in items) CollectThisReads(item, reads);
                    return;
            }
            var type = node.GetType();
            if (type.Namespace != "fire.Ast") return;
            System.Reflection.PropertyInfo[] children;
            lock (s_astChildren)
                if (!s_astChildren.TryGetValue(type, out children!))
                    s_astChildren[type] = children = type.GetProperties().Where(p => p.GetIndexParameters().Length == 0 && !p.PropertyType.IsValueType && p.PropertyType != typeof(string)
                        && p.Name != "EqualityContract").ToArray();
            foreach (var child in children) CollectThisReads(child.GetValue(node), reads);
        }

        private void CompileClassBody(RuntimeClass rc)
        {
            var ctorDecls = new List<ConstructorDecl>();
            rc.ThisReads = ComputeThisReads(rc.Decl);

            foreach (var member in rc.Decl.Members)
            {
                // Every member is a restart point of its own (see
                // CompileStmt) - an error in one field/method does
                // not prevent the compilation of the other members.
                var outerMember = s_memberSource;
                s_memberSource = member.Source;
                try
                {
                    switch (member)
                    {
                        case FieldDecl fd:
                        {
                            var fieldInit = CompileFieldInitProto(rc, fd.Type, fd.Initializer, fd.IsStatic, fd.Line, fd.ArrayRanks);
                            rc.OwnFieldInfo[fd.Name] = new FieldInfo()
                            {
                                AccessModifier = fd.Access,
                                RequiredUnit = fd.Type?.UnitDemand,
                                RequiredKind = fd.ArrayRanks.Count == 0 ? DeclaredKind.Of(fd.Type, fd.Initializer) : (byte)0,
                                RequiredType = fd.ArrayRanks.Count == 0 ? DeclaredTypeOf(fd.Type, rc) : null,
                                RequiredArray = fd.Type?.ArrayWidthCode(fd.ArrayRanks.Count) is var arrayCode and not 0 && (arrayCode & 0xFF) is not (fire.Values.ArrayElem.U8 or fire.Values.ArrayElem.Word)
                                    ? (ushort)(arrayCode & 0xFFFF) : (ushort)0,
                                IsStatic = fd.IsStatic,
                                TypeText = JsonTypeText(fd.Type, fd.ArrayRanks.Count, rc.Decl),
                                Attributes = AttributeMetas(fd.Attributes),
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
                            rc.AddMethod(md.Name, CompileMethodProto(rc, md.Params, md.Body, md.Access, md.IsStatic, md.ReturnType?.UnitDemand, md.TypeParams, returnType: md.ReturnType, name: md.Name));
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
                            {
                                var getterProto = CompileMethodProto(rc, Array.Empty<LambdaParam>(), pd.Getter, pd.Access, pd.IsStatic, pd.Type?.UnitDemand, accessor: 1, returnType: pd.Type, name: pd.Name);
                                // `int Count { get { return this.count } }`: the VM reads the field directly (FunctionProto.FieldGetterOf)
                                if (!pd.IsStatic && pd.Type?.UnitDemand == null && pd.Getter.Statements is [ReturnStmt { Value: MemberExpr { Target: ThisExpr, Name: var backing } }])
                                    getterProto.FieldGetterOf = backing;
                                rc.AddMethod("get_" + pd.Name, getterProto);
                            }
                            if (pd.Setter != null)
                            {
                                var setterParams = new[] { new LambdaParam("value", pd.Type, Array.Empty<Expr?>()) };
                                rc.AddMethod("set_" + pd.Name, CompileMethodProto(rc, setterParams, pd.Setter, pd.Access, pd.IsStatic, accessor: 2));
                            }
                            break;
                    }
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
                {
                    _errors.Add((ex as CompilerException ?? new CompilerException(ex.Message, member.Line)).At(rc.Decl.Source));
                }
                finally { s_memberSource = outerMember; }
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
                    var outerMember = s_memberSource;
                    s_memberSource = ctor.Source;
                    try
                    {
                        rc.AddConstructor(CompileConstructorProto(rc, ctor, ctor.Access));
                    }
                    catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
                    {
                        _errors.Add((ex as CompilerException ?? new CompilerException(ex.Message, ctor.Line)).At(rc.Decl.Source));
                    }
                    finally { s_memberSource = outerMember; }
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
                var inner = new Compiler(_refs, _natives, rc, _globalSlotCount, _knownClassNames, _errors, _refParams); inner._enumTypes = _enumTypes; inner._topLevelSourceIndex = _topLevelSourceIndex;
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

        private FunctionProto CompileFieldInitProto(RuntimeClass rc, TypeRef? type, Expr? initializer, bool isStatic = false, int line = 0, IReadOnlyList<Expr?>? arrayRanks = null)
        {
            var inner = new Compiler(_refs, _natives, rc, _globalSlotCount, _knownClassNames, _errors, _refParams); inner._enumTypes = _enumTypes; inner._topLevelSourceIndex = _topLevelSourceIndex;
            inner._chunk.OwnerClass = rc;
            if (initializer != null)
            {
                // the line of the field: a debugger that steps into the initialiser shows it (and an error in it names it)
                if (line > 0) inner._chunk.MarkLine(inner.CurrentSourceIndex, line);
                // An instance field with `new X()`/`flat x`/`copy x` as initialiser: the new object belongs to the instance
                // (`this` is bound during evaluation, see CallProtoWithThis), not the initialiser scope - otherwise
                // it would be destroyed after the constructor while the field points to it (SPEC 2.1).
                if (!isStatic && MayBeUnclaimed(initializer))
                {
                    inner._chunk.EmitOp(OpCode.LoadThis);
                    inner.CompileExpr(initializer);
                    inner._chunk.EmitOp(OpCode.ClaimToObject);   // [this, value] -> [value]: the object owns it
                }
                else
                {
                    inner.CompileExprFor(initializer, arrayRanks is { Count: > 0 } ? 0 : type?.WidthCode ?? 0);
                    if (isStatic) inner.EmitClaimToScope(0xFFFF, unclaimed: MayBeUnclaimed(initializer), hoist: false);   // a static field: the program
                }
                inner.EmitCheckLambdaSignatureIfNeeded(type);
            }
            // `int data[16]` as a field: the array is made with the object (like a variable `int data[16]`; `int data[]` stays undefined)
            else if (arrayRanks is { Count: > 0 } && arrayRanks[0] != null)
            {
                if (line > 0) inner._chunk.MarkLine(inner.CurrentSourceIndex, line);
                // (it belongs to the object - [this] -> [this, array] -> [array], as for `int data[] = new int[16]`; a static field's to the program)
                if (!isStatic) inner._chunk.EmitOp(OpCode.LoadThis);
                inner.CompileArrayAlloc(arrayRanks, 0, IsByteBuffer(type, arrayRanks.Count));
                // (`word data[4]`: the array becomes a buffer of words - TypeArray, as for a variable)
                if (IsWordBuffer(type, arrayRanks.Count)) EmitWidthIfNeeded(inner, type!.ArrayWidthCode(arrayRanks.Count));
                if (!isStatic) inner._chunk.EmitOp(OpCode.ClaimToObject);
                else inner.EmitClaimToScope(0xFFFF, unclaimed: true, hoist: false);
            }
            // `defined int count` (SPEC 8.10b): a field that never holds undefined starts at zero (in the unit it demands)
            else if (arrayRanks is not { Count: > 0 } && type is { NonNull: true } && DeclaredKind.Of(type) is var nonNullKind and not 0)
                inner.EmitLoadConst(DeclaredKind.ZeroOf(nonNullKind, ZeroUnitOf(type)));
            else inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);
            return new FunctionProto(inner._chunk, 0, AccessModifier.Private);
        }

        /// <summary>The unit the zero of a `defined` declaration without an initializer has (SPEC 8.10b): the one it demands, none for an open one.</summary>
        private static fire.Values.Unit? ZeroUnitOf(TypeRef type) =>
            type.RequiredUnit is { } unit && !unit.StartsWith("any", StringComparison.Ordinal) ? fire.Values.Unit.Parse(unit) : null;

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
            // a parameter with a bit width cuts its argument (SPEC 8.2: "copied into a variable/parameter")
            for (int i = 0; i < parms.Count; i++)
                if (!parms[i].ByRef && parms[i].ArrayRanks.Count == 0 && parms[i].Type?.WidthCode is { } code and not 0)
                {
                    inner._chunk.EmitOp(OpCode.LoadLocal);
                    inner._chunk.EmitU16(0);
                    inner._chunk.EmitU16((ushort)i);
                    EmitWidthIfNeeded(inner, code);
                    inner._chunk.EmitOp(OpCode.StoreLocal);
                    inner._chunk.EmitU16(0);
                    inner._chunk.EmitU16((ushort)i);
                    inner._chunk.EmitOp(OpCode.Pop);
                }

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
                    inner.EmitClaimToScope(0, unclaimed: true, hoist: false);   // (the reflection object belongs to the call)
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
            // The value is converted into the unit of the parameter and written back (into the caller's variable for a `ref` parameter).
            for (int i = 0; i < parms.Count; i++)
            {
                string? unit = parms[i].Type?.UnitDemand;
                if (unit == null) continue;
                inner._chunk.EmitOp(OpCode.LoadLocal);
                inner._chunk.EmitU16(0);
                inner._chunk.EmitU16((ushort)i);
                if (parms[i].ByRef) inner._chunk.EmitOp(OpCode.PtrRead);
                EmitCheckUnitIfNeeded(inner, unit);
                inner.EmitStoreVariable(new ResolvedRef.Local(0, i, null, parms[i].ByRef));
                inner._chunk.EmitOp(OpCode.Pop);
            }

            // A parameter declared int/float/bool holds that kind or `undefined` (DeclaredKind) - checked last, after its width and unit.
            for (int i = 0; i < parms.Count; i++)
            {
                if (parms[i].ByRef || parms[i].ArrayRanks.Count > 0 || DeclaredKind.Of(parms[i].Type, parms[i].DefaultValue) is not (var kind and not 0)) continue;
                // the unit the parameter certainly has afterwards: the checked one, none (the guard converts into no unit), or open (`: any`)
                string? unit = parms[i].Type?.UnitDemand ?? (DeclaredKind.IsUnitless(kind) ? "unitless" : null);
                inner._chunk.EmitOp(OpCode.GuardLocal);
                inner._chunk.EmitU16((ushort)i);
                inner._chunk.EmitByte((byte)((kind & 0x7F) - 1 | kind & DeclaredKind.UnitlessBit));
                inner._chunk.EmitU16(inner._chunk.AddConstant(Value.MakeString($"Parameter '{parms[i].Name}'")));
                inner._chunk.EmitU16(unit == null ? (ushort)0xFFFF : inner._chunk.AddConstant(Value.MakeString(unit)));
            }

            // A parameter declared with a class (`Point p`) holds `undefined` or an object of that class or of a class derived from it; likewise with an
            // interface or a type parameter of the class with `where` conditions (DeclaredType).
            for (int i = 0; i < parms.Count; i++)
            {
                if (parms[i].ByRef || parms[i].ArrayRanks.Count > 0 || inner.DeclaredTypeOf(parms[i].Type, inner._enclosingClass) is not { } cls) continue;
                inner._chunk.EmitOp(OpCode.GuardLocalType);
                inner._chunk.EmitU16((ushort)i);
                inner._chunk.EmitU16(inner._chunk.AddConstant(Value.MakeString(cls)));
                inner._chunk.EmitU16(inner._chunk.AddConstant(Value.MakeString($"Parameter '{parms[i].Name}'")));
            }
        }

        /// <summary>Emits (if `unit` != null) a CheckUnit opcode for
        /// the value that lies CURRENTLY on top of the stack (SPEC "Unit
        /// declarations") - checks (in the VM) whether its unit exactly
        /// matches `unit`, otherwise throws a `UnitMismatchException`
        /// (see VM.ThrowUnitMismatch). Only peeks (see OpCode.CheckUnit
        /// documentation) - the caller itself decides whether/when it
        /// still needs the value afterwards or pops it.</summary>
        /// <summary>A variable, parameter or field declared with a bit width (`int[8]`, `byte`, `float[32]`, see TypeRef.WidthCode): the value on top of
        /// the stack is cut to it (SPEC 8.2). Implicit (flag 4): only a number of the declared kind is cut, every other value stays as it is.</summary>
        /// <summary>A declaration with the element type `byte` and array brackets (`byte data[]`, `byte data[16]`): it holds a byte buffer, not a typed array
        /// (SPEC 8.4.2) - the array stored in it becomes a buffer (OpCode.TypeArray with ArrayElem.U8, VM.BufferFromArrayValue / fire_rt.h bufFromArray).</summary>
        private static bool IsByteBuffer(TypeRef? type, int rank) => ((type?.ArrayWidthCode(rank) ?? 0) & 0x100FF) == (0x10000 | fire.Values.ArrayElem.U8);
        /// <summary>`word data[]`, `word data[16]`: a buffer of machine words (SPEC 8.10) - OpCode.TypeArray with ArrayElem.Word makes it from the array.</summary>
        private static bool IsWordBuffer(TypeRef? type, int rank) => ((type?.ArrayWidthCode(rank) ?? 0) & 0x100FF) == (0x10000 | fire.Values.ArrayElem.Word);

        private static void EmitWidthIfNeeded(Compiler target, int widthCode, string? unit = null)
        {
            if (widthCode == 0) return;
            if ((widthCode & 0x10000) != 0)
            {
                // a typed array (`int[8] a[]`, TypeRef.ArrayWidthCode): the array stored becomes typed (SPEC 8.4.2)
                target._chunk.EmitOp(OpCode.TypeArray);
                target._chunk.EmitByte((byte)(widthCode & 0xFF));
                target._chunk.EmitByte((byte)((widthCode >> 8) & 0xFF));
                target._chunk.EmitU16(unit == null ? (ushort)0xFFFF : (ushort)target._chunk.AddConstant(Value.MakeString(unit)));
                return;
            }
            target._chunk.EmitOp(OpCode.ConvertWidth);
            if (widthCode == TypeRef.WordWidthCode)
            {
                // `word`: into a machine word (flag 8; the width is the target's, the VM's Value.WordBits / FIRE_WORD_BITS)
                target._chunk.EmitByte(0);
                target._chunk.EmitByte(8 | 4);
                return;
            }
            target._chunk.EmitByte((byte)(widthCode & 0xFF));
            target._chunk.EmitByte((byte)(((widthCode & 0x100) != 0 ? 1 : 0) | ((widthCode & 0x200) != 0 ? 2 : 0) | 4));
        }

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
            var inner = new Compiler(_refs, _natives, null, _globalSlotCount, _knownClassNames, _errors, _refParams); inner._enumTypes = _enumTypes;
            inner._topLevelSourceIndex = CurrentSourceIndex; // its lines belong to the source it stands in (a debugger stops at a breakpoint in it)
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

            if (fs.Until is { Count: > 0 } fireUntil)
            {
                // `fire ... until x`: the node the thread follows (bit 7: for the Fire that follows - a thread does not follow its caller)
                foreach (var limit in fireUntil) CompileExpr(limit);
                _chunk.EmitOp(OpCode.CancelPush);
                _chunk.EmitByte((byte)(fireUntil.Count | 0x80));
            }
            _chunk.EmitOp(OpCode.Fire);
            _chunk.EmitU16(protoIdx);
            _chunk.EmitU16(_globalSlotCount);
            _chunk.EmitByte((byte)fs.TakingCaptures.Count);
            _chunk.EmitByte((byte)((hasWith ? 1 : 0) | (fs.Dedicated ? 2 : 0)));   // (bit 1: `fire dedicated`)
            _chunk.EmitByte((byte)fs.TakingCaptures.Count(c => c.Handover));   // (the parser put them last)
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
            var inner = new Compiler(_refs, _natives, null, _globalSlotCount, _knownClassNames, _errors, _refParams); inner._enumTypes = _enumTypes;
            inner._topLevelSourceIndex = CurrentSourceIndex; // its lines belong to the source it stands in (a debugger stops at a breakpoint in it)
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
            var inner = new Compiler(_refs, _natives, null, _globalSlotCount, _knownClassNames, _errors, _refParams); inner._enumTypes = _enumTypes;
            inner._topLevelSourceIndex = CurrentSourceIndex; // its lines belong to the source it stands in (a debugger stops at a breakpoint in it)
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

        private FunctionProto CompileMethodProto(RuntimeClass? rc, IReadOnlyList<LambdaParam> parms, Stmt.BlockStmt body, AccessModifier access, bool isStatic = false, string? returnUnit = null,
            IReadOnlyList<TypeParam>? typeParams = null, int accessor = 0, TypeRef? returnType = null, string? name = null)
        {
            var inner = new Compiler(_refs, _natives, rc, _globalSlotCount, _knownClassNames, _errors, _refParams); inner._enumTypes = _enumTypes; inner._topLevelSourceIndex = _topLevelSourceIndex;
            inner._accessor = isStatic ? 0 : accessor;
            // a setter passes its `value` on at its claim sites (SPEC 2.1 "Setters")
            if (accessor == 2 && !isStatic) inner._setterClaims = SetterClaims.Analyze(body).Sites;
            inner._methodTypeParams = typeParams;
            inner._chunk.OwnerClass = rc;
            inner._returnUnit = returnUnit;
            // `defined int Count()` (SPEC 8.10b): every value it returns is checked - undefined (also the end of the body) is an ArgumentException
            if (returnType is { NonNull: true } && DeclaredKind.Of(returnType) is var returnKind and not 0) { inner._returnKind = returnKind; inner._returnWhat = $"The return value of '{name}'"; }
            // SPEC "Static members": a static method has no
            // bound 'this' - the inner compiler remembers that in order to
            // reject 'this'/'super' in the body (see resolver instead of
            // compiler: the check itself runs in the resolver, BEFORE
            // compiling, via ResolveResult - mentioned here only for
            // completeness, no check of its own needed at this point).
            for (int i = 0; i < parms.Count; i++)
                inner._chunk.MarkLocalName(0, i, parms[i].Name);
            if (typeParams is { Count: > 0 }) inner._chunk.EmitOp(OpCode.TakeCallTypeArgs);
            EmitLambdaParamChecks(inner, parms);
            foreach (var stmt in body.Statements) inner.CompileStmt(stmt);
            inner.EmitLoadConst(Value.MakeUndefined());
            inner.EmitReturnGuard();
            inner._chunk.EmitOp(OpCode.Return);
            return new FunctionProto(inner._chunk, parms.Count, access, CompileParamDefaults(rc, parms), isStatic)
            {
                RefMask = RefMaskOf(parms), ParamTypes = inner.OverloadTypesOf(parms, rc), TypeParamCount = typeParams?.Count ?? 0, ParamNames = parms.Select(p => p.Name).ToArray(),
            };
        }

        /// <summary>The types of the parameters for overloading by type (Overloads; FunctionProto.ParamTypes).</summary>
        private string?[] OverloadTypesOf(IReadOnlyList<LambdaParam> parms, RuntimeClass? owner) => parms.Select(p => OverloadTypeOf(p, owner)).ToArray();

        private string OverloadTypeOf(LambdaParam p, RuntimeClass? owner)
        {
            var t = p.Type;
            if (p.ByRef) return "";
            if (p.ArrayRanks.Count > 0 || t is { ArrayRank: > 0 }) return "k:array";
            if (t == null || t.IsInferred || t.PointerDepth > 0) return "";
            if (t.LambdaSignature != null || t.BaseName == "lambda") return "k:lambda";
            bool methodParam = _methodTypeParams?.Any(tp => tp.Name == t.BaseName) == true;
            bool classParam = owner?.Decl?.TypeParams?.Any(tp => tp.Name == t.BaseName) == true;
            if (methodParam || classParam) return "g:" + (DeclaredTypeOfCore(t, owner) ?? "");
            switch (t.BaseName)
            {
                case "int": return "k:int";
                case "float": return "k:float";
                case "bool": return "k:bool";
                case "char": return "k:char";
                case "string": return "k:string";
                case "class": return "k:class";
                case "word": return "k:word";   // (a word, or an int that the store converts)
                case "any": return "";
            }
            if (_refParams.EnumNames.Contains(t.BaseName)) return "k:int";
            if (_knownClassNames == null) return "";
            bool Known(string n) => _knownClassNames.Contains(n) || _refParams.InterfaceNames.Contains(n);
            string name = t.TypeArgs is { Count: > 0 } args ? GenericClassNames.ResolveNewTarget(t, args.Count, Known) : t.ResolveBaseName(Known);
            if (_refParams.InterfaceNames.Contains(name)) return "i:" + name;
            if (_knownClassNames.Contains(name)) return "c:" + name;
            return _refParams.IsTypeParam(t.BaseName) ? "g:" : "";
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
            var inner = new Compiler(_refs, _natives, rc, _globalSlotCount, _knownClassNames, _errors, _refParams); inner._enumTypes = _enumTypes; inner._topLevelSourceIndex = _topLevelSourceIndex;
            inner._chunk.OwnerClass = rc;
            // the base call and the field initialisers belong to the line of `construct` (of the class, when it declares none): a debugger shows that, not line 0
            int headerLine = ctor?.Line ?? rc.Decl?.Line ?? 0;
            if (headerLine > 0) inner._chunk.MarkLine(inner.CurrentSourceIndex, headerLine);

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
                    EmitWidthIfNeeded(inner, _refParams.FieldWidth(fieldName), _refParams.FieldUnit(fieldName));
                    inner._chunk.EmitOp(OpCode.SetFieldOnThis);
                    inner._chunk.EmitU16(inner._chunk.AddConstant(Value.MakeString(fieldName)));
                    continue;
                }

                int protoIdx = inner._chunk.AddFunctionProto(initProto);
                inner._chunk.EmitOp(OpCode.LoadThis);
                inner._chunk.EmitOp(OpCode.CallProtoWithThis);
                inner._chunk.EmitU16(protoIdx);
                inner._chunk.EmitByte(0);
                EmitWidthIfNeeded(inner, _refParams.FieldWidth(fieldName), _refParams.FieldUnit(fieldName));
                inner._chunk.EmitOp(OpCode.SetFieldOnThis);
                inner._chunk.EmitU16(inner._chunk.AddConstant(Value.MakeString(fieldName)));
                fieldsMayBeTouched = true;
            }

            if (ctor != null)
                foreach (var stmt in ctor.Body.Statements) inner.CompileStmt(stmt);

            inner.EmitLoadConst(Value.MakeUndefined());
            inner._chunk.EmitOp(OpCode.Return);

            var paramDefaults = ctor != null ? CompileParamDefaults(rc, ctor.Params) : Array.Empty<FunctionProto?>();
            return new FunctionProto(inner._chunk, ctor?.Params.Count ?? 0, access, paramDefaults)
            {
                RefMask = ctor != null ? RefMaskOf(ctor.Params) : 0, ParamTypes = ctor != null ? inner.OverloadTypesOf(ctor.Params, rc) : null,
                ParamNames = ctor?.Params.Select(p => p.Name).ToArray() ?? Array.Empty<string>(),
            };
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
                _errors.Add((ex as CompilerException ?? new CompilerException(ex.Message, stmt.Line)).At(CurrentSourceIndex));
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
                    EmitReleaseTemps(es.Expression);   // a value no slot took dies at the end of the statement (SPEC 2.1)
                    break;

                case NoOpStmt:
                    break;

                case TypeCheckDirective:
                    break;

                case GlobalJobsDirective:
                    break; // see Compile: `SetAutoSync mode` already stands at the program start

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
                        CompileExprFor(vd.Initializer, vd.ArrayRanks.Count == 0 ? vd.Type?.WidthCode ?? 0 : 0);
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
                        CompileArrayAlloc(vd.ArrayRanks, 0, IsByteBuffer(vd.Type, vd.ArrayRanks.Count));
                    else if (vd.Type is { NonNull: true } && DeclaredKind.Of(vd.Type) is var zeroKind and not 0)
                        EmitLoadConst(DeclaredKind.ZeroOf(zeroKind, ZeroUnitOf(vd.Type)));   // (`defined int n` starts at 0: SPEC 8.10b)
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
                    EmitCheckUnitIfNeeded(this, vd.Type?.UnitDemand);
                    EmitWidthIfNeeded(this, vd.ArrayRanks.Count == 0 ? vd.Type?.WidthCode ?? 0 : vd.Type?.ArrayWidthCode(vd.ArrayRanks.Count) ?? 0, vd.Type?.UnitDemand);

                    // `var v = E`: an unclaimed value belongs to the scope that declares v (SPEC 2.1); `int a[10]` is made for it
                    if (vd.Initializer != null ? MayBeUnclaimed(vd.Initializer) : autoArrayAlloc) EmitClaimToScope(0, unclaimed: true, hoist: false);
                    _chunk.MarkDeclaredName(_chunk.Here, vd.Name);
                    _chunk.EmitOp(OpCode.DeclareLocal);
                    if (vd.Initializer != null) EmitReleaseTemps(vd.Initializer);
                    break;
                }

                case IfStmt ifs:
                    CompileIf(ifs);
                    break;

                case WhileStmt ws:
                    CompileWhile(ws);
                    break;

                case DoWhileStmt dws:
                    CompileDoWhile(dws);
                    break;

                case ForStmt fs:
                    CompileFor(fs);
                    break;

                case ForeachStmt fes:
                    CompileForeach(fes);
                    break;

                case ReturnStmt rs:
                    if (rs.Value != null)
                    {
                        CompileExpr(rs.Value);
                        if (_returnUnit != null) EmitCheckUnitIfNeeded(this, _returnUnit);
                    }
                    else EmitLoadConst(Value.MakeUndefined());
                    EmitReturnGuard();
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
                    EmitReleaseTemps(del.Target);
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

                case BreakStmt bs:
                    // The resolver has already checked that we are in enough loops
                    // and that no finally block is left - _loopStack is therefore deep enough here.
                    CompileBreakOrContinue(isBreak: true, bs.Levels);
                    break;

                case ContinueStmt cs:
                    CompileBreakOrContinue(isBreak: false, cs.Levels);
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
                    // (bit 7: the first argument is the object of `fire on` - not copied; bit 6: `await on` - the result comes back)
                    _chunk.EmitByte((byte)(postGlobal.Args.Count | (postGlobal.OnTarget ? 0x80 : 0)));
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
            bool isStep = false;

            switch (expr)
            {
                case IncDecExpr { Target: IdentifierExpr incTarget } incDec:
                    target = incTarget;
                    constant = Value.MakeInt(1);
                    subtract = !incDec.IsIncrement;
                    isStep = true;
                    break;

                case AssignExpr { Target: IdentifierExpr assignTarget, Value: BinaryExpr { Op: BinaryOp.Add or BinaryOp.Sub, Left: IdentifierExpr left, Right: LiteralExpr literal } binary }
                    when left.Name == assignTarget.Name && literal.Value.Kind is ValueKind.Int or ValueKind.Float
                         && s_localGuards?.ContainsKey(binary) != true && s_unitChecks?.ContainsKey(binary) != true   // (`x += 0.5` on an int: the guard throws)
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
                case ResolvedRef.Local { RequiredUnit: null, ByRef: false, Width: 0 } local:
                    _chunk.EmitOp(OpCode.ArithLocalConstPop);
                    _chunk.EmitU16(local.Depth);
                    _chunk.EmitU16(local.Slot);
                    _chunk.EmitU16(_chunk.AddConstant(constant));
                    _chunk.EmitByte((byte)((subtract ? 1 : 0) | (isStep ? 2 : 0)));
                    return true;
                case ResolvedRef.Global { RequiredUnit: null, Width: 0 } global:
                    _chunk.EmitOp(OpCode.ArithGlobalConstPop);
                    _chunk.EmitU16(global.Slot);
                    _chunk.EmitU16(_chunk.AddConstant(constant));
                    _chunk.EmitByte((byte)((subtract ? 1 : 0) | (isStep ? 2 : 0)));
                    return true;
                default:
                    return false;
            }
        }

        private void CompileIf(IfStmt s)
        {
            CompileExpr(s.Condition);
            EmitReleaseTemps(s.Condition);   // (the condition's temporaries die before the branch runs, SPEC 2.1)
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
            EmitReleaseTemps(s.Condition);   // (the condition's temporaries die before the branch runs, SPEC 2.1)
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

        private void CompileDoWhile(DoWhileStmt s)
        {
            var ctx = new LoopCompileContext { ScopeDepthAtLoopBodyStart = _currentScopeDepth };
            _loopStack.Push(ctx);

            int loopStart = _chunk.Here;
            CompileScopedBody(s.Body);

            // `continue` jumps to the condition
            _chunk.MarkLine(CurrentSourceIndex, s.Line);
            int continueTarget = _chunk.Here;
            foreach (var addr in ctx.ContinueJumpPatchAddrs) _chunk.PatchU16(addr, continueTarget);
            CompileExpr(s.Condition);
            EmitReleaseTemps(s.Condition);   // (the condition's temporaries die before the branch runs, SPEC 2.1)
            int endJumpAt = EmitJumpIfFalse();
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
            EmitReleaseTemps(s.Condition);   // (the condition's temporaries die before the branch runs, SPEC 2.1)
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
            {
                CompileDiscardedExpr(s.Increment);
                EmitReleaseTemps(s.Increment);
            }

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

            EmitEnterScope(); // corresponds to Resolver.PushScope() for the loop variable
            _chunk.EmitOp(OpCode.Dup);
            EmitCallMethodByName("GetCurrent", 0);
            // Stack: [enumerator, current] - an element GetCurrent made belongs to the loop variable's scope (SPEC 2.1)
            EmitClaimToScope(0, unclaimed: true, hoist: false);
            _chunk.MarkDeclaredName(_chunk.Here, fs.VarName);
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
            EmitReleaseTemps(fs.Iterable);   // the collection and its enumerator lived as temporaries of the `foreach` until here (SPEC 2.1)

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
        private void CompileArrayAlloc(IReadOnlyList<Expr?> ranks, int rankIndex, bool byteBuffer = false)
        {
            // `byte data[16]` is `new byte[16]`: a `byte` declaration holds a byte buffer (SPEC 8.4.2; several dimensions: TypeArray converts the inner arrays)
            if (byteBuffer && ranks.Count == 1 && rankIndex == 0 && ranks[0] != null)
            {
                CompileExpr(ranks[0]!);
                _chunk.EmitOp(OpCode.MakeBuffer);
                return;
            }
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
                template.Catches.Add((c.TypeRef == null ? null : ResolveCatchType(c.TypeRef), catchAddr));

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
            if (tryContext.StubPatches.Count > 0)
            {
                _chunk.EmitOp(OpCode.Jump);
                int skipStubs = _chunk.Here;
                _chunk.EmitU16(0);
                foreach (var ((isBreak, levels), patches) in tryContext.StubPatches)
                {
                    foreach (var addr in patches) _chunk.PatchU16(addr, _chunk.Here);
                    CompileBreakOrContinue(isBreak, levels);
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
        /// <summary>The values whose unit is checked at runtime (ResolveResult.UnitChecks) of the program being compiled.</summary>
        [ThreadStatic] private static Dictionary<Expr, string>? s_unitChecks;
        [ThreadStatic] private static Dictionary<Expr, string[]>? s_methodTypeArgs;
        [ThreadStatic] private static Dictionary<Expr, (TypeRef Type, string What)>? s_localGuards;
        [ThreadStatic] private static Dictionary<Expr, string>? s_overloadHints;
        /// <summary>`FIRE_NO_OVERLOAD_HINTS=1`: no OverloadHint is emitted (every call scores its candidates) - to compare and to find a wrong hint.</summary>
        private static readonly bool NoOverloadHints = Environment.GetEnvironmentVariable("FIRE_NO_OVERLOAD_HINTS") == "1";

        /// <summary>A call of an overload the type checker could tell (ResolveResult.OverloadHints): the hint goes in front of its other prefixes.</summary>
        private void EmitOverloadHint(Expr call)
        {
            if (s_overloadHints == null || NoOverloadHints || !s_overloadHints.TryGetValue(call, out var hint)) return;
            if (call is CallExpr { ArgNames: not null } or NewExpr { ArgNames: not null }) return;   // (the hint assumes the arguments in the order of the parameters)
            _chunk.EmitOp(OpCode.OverloadHint);
            _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(hint)));
        }

        /// <summary>A value going into a local whose declaration the type checker could not prove: checked on the stack (GuardTop/GuardTopType).</summary>
        private void EmitLocalGuard(TypeRef type, string what)
        {
            if (DeclaredKind.Of(type) is var kind and not 0)
            {
                _chunk.EmitOp(OpCode.GuardTop);
                _chunk.EmitByte((byte)((kind & 0x7F) - 1));   // (no unit demand: a local takes the unit of its value)
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(what)));
            }
            else if (DeclaredTypeOf(type, _enclosingClass) is { } spec)
            {
                _chunk.EmitOp(OpCode.GuardTopType);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(spec)));
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(what)));
            }
        }
        /// <summary>The type parameters of the generic method being compiled (`R Select&lt;R&gt;(...)`), null for any other code.</summary>
        private IReadOnlyList<TypeParam>? _methodTypeParams;

        /// <summary>A call of a generic method whose type arguments the type checker found: they go to the call (SetTypeArgs, right before it).</summary>
        private void EmitCallTypeArgs(CallExpr call)
        {
            EmitOverloadHint(call);
            if (call.TypeArgs is { Count: > 0 } written)
            {
                // `Pick<string>(a, b)`: the written type arguments (mode 3: a generic overload is meant)
                _chunk.EmitOp(OpCode.SetTypeArgs);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(MapTypeRefs(written.Select(t => TypeArgText(t, _enclosingClass, null, 0))))));
                _chunk.EmitByte(3);
                return;
            }
            if (s_methodTypeArgs == null || !s_methodTypeArgs.TryGetValue(call, out var texts) || texts.All(t => t.Length == 0)) return;
            _chunk.EmitOp(OpCode.SetTypeArgs);
            _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(MapTypeRefs(texts))));
            _chunk.EmitByte(1);
        }

        /// <summary>The unit a method with a unit at its return type (`float:m Len()`) or the getter of a property with a unit (`float X : m { get ... }`)
        /// converts its return values into, otherwise null.</summary>
        private string? _returnUnit;
        /// <summary>`defined int Count()`: the DeclaredKind its return values are checked against (GuardTop), 0: none; and how the error names the value.</summary>
        private byte _returnKind;
        private string _returnWhat = "";

        /// <summary>The value on top of the stack goes back from a method declared `defined int` (`defined float`, ...): it is checked like a parameter (GuardTop).</summary>
        private void EmitReturnGuard()
        {
            if (_returnKind == 0) return;
            _chunk.EmitOp(OpCode.GuardTop);
            _chunk.EmitByte((byte)((_returnKind & 0x7F) - 1));
            _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(_returnWhat)));
        }
        /// <summary>The body of a property accessor of an object (SPEC 2.2 "Ownership in property accessors"): 1 a getter, 2 a setter, 0 anything else - its
        /// assignments to members take SetFieldAccessor.</summary>
        private int _accessor;

        /// <summary>A literal compiled now keeps this width (64 or 32; 0: the program's) - SPEC 8.2.1: an int literal in an expression with an operand of an
        /// explicit wider width (`this.ticks + 864000000000` with `int[64] ticks`), or stored into one (`int[64] t = 864000000000`), is not cut to `#intwidth`.
        /// Only arithmetic, comparisons, signs and width casts pass it on to their operands; any other expression (a call, an index, ...) starts without.</summary>
        private int _wideLiteral;

        /// <summary>The explicit width beyond `int` an expression is known to have (64 or 32), 0 when it has none or is not known: a variable, parameter or
        /// field declared `int[64]`/`int[32]`, a cast `!int[64]`, arithmetic with such an operand.</summary>
        private int StaticWide(Expr e)
        {
            static int OfCode(int code) => code is 64 or 32 ? code : 0;
            switch (e)
            {
                case IdentifierExpr id when _refs.TryGetValue(id, out var r):
                    return r switch
                    {
                        ResolvedRef.Local l => OfCode(l.Width),
                        ResolvedRef.Global g => OfCode(g.Width),
                        ResolvedRef.ImplicitThisMember => OfCode(_refParams.FieldWidth(id.Name)),
                        _ => 0,
                    };
                case MemberExpr me: return OfCode(_refParams.FieldWidth(me.Name));
                case TypeCoerceExpr { Width: 64 or 32, Unsigned: false } c when c.TargetTypeKeyword != TokenType.KwFloat: return c.Width;
                case BinaryExpr { Op: not (BinaryOp.And or BinaryOp.Or) } b: return Math.Max(StaticWide(b.Left), StaticWide(b.Right));
                case UnaryExpr { Op: UnaryOp.Negate or UnaryOp.BitNot } u: return StaticWide(u.Operand);
                case AssignExpr a: return StaticWide(a.Target);
                default: return 0;
            }
        }

        private void CompileExpr(Expr expr)
        {
            int outerWide = _wideLiteral;
            switch (expr)
            {
                case LiteralExpr: break;
                case BinaryExpr { Op: not (BinaryOp.And or BinaryOp.Or) }: _wideLiteral = Math.Max(outerWide, StaticWide(expr)); break;
                case UnaryExpr { Op: UnaryOp.Negate or UnaryOp.BitNot }: break;
                case TypeCoerceExpr { Width: > 0 }: _wideLiteral = Math.Max(outerWide, StaticWide(expr)); break;
                case AssignExpr: _wideLiteral = StaticWide(expr); break;   // (`t = 864000000000` for an `int[64] t`)
                default: _wideLiteral = 0; break;
            }
            try
            {
                CompileExprCore(expr);
                if (s_unitChecks != null && s_unitChecks.TryGetValue(expr, out var demand)) EmitCheckUnitIfNeeded(this, demand);
                if (s_localGuards != null && s_localGuards.TryGetValue(expr, out var guard)) EmitLocalGuard(guard.Type, guard.What);
            }
            catch (Exception ex) when ((ex is NotSupportedException || ex is InvalidOperationException) && ex is not CompilerException)
            {
                throw new CompilerException(ex.Message, expr.Line);
            }
            finally { _wideLiteral = outerWide; }
        }

        /// <summary>Compiles the value stored into a declaration of the width code <paramref name="widthCode"/> (TypeRef.WidthCode): a literal in it keeps an
        /// explicit wider width (<see cref="_wideLiteral"/>).</summary>
        private void CompileExprFor(Expr value, int widthCode)
        {
            int outer = _wideLiteral;
            _wideLiteral = widthCode is 64 or 32 ? widthCode : 0;
            try { CompileExpr(value); }
            finally { _wideLiteral = outer; }
        }

        private void CompileExprCore(Expr expr)
        {
            switch (expr)
            {
                case LiteralExpr lit:
                    EmitLoadConst(_wideLiteral != 0 && lit.Value.Kind == ValueKind.Int && !lit.Value.IsEnum ? lit.Value.WithWide((byte)_wideLiteral) : lit.Value);
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

                case RebindExpr rb:
                    CompileExpr(rb.Lambda);
                    CompileExpr(rb.Target);
                    _chunk.EmitOp(OpCode.RebindLambda);
                    break;

                case ConditionalAccessExpr access:
                    {
                        // `a?.chain` / `a??.chain`: [a] -> [a, a] -> the test; the chain runs on the a below it (the placeholder compiles to nothing),
                        // otherwise a goes and undefined is the result
                        CompileExpr(access.Target);
                        _chunk.EmitOp(OpCode.Dup);
                        if (access.CheckMember && !AstNames.ProvenMember(access))
                        {
                            _chunk.EmitOp(OpCode.HasMember);
                            _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(access.Member)));
                        }
                        else
                        {
                            _chunk.EmitOp(OpCode.IsOfType);
                            _chunk.EmitU16(_chunk.AddConstant(Value.MakeString("undefined")));
                            _chunk.EmitOp(OpCode.LogicalNot);
                        }
                        int skipAt = EmitJumpIfFalse();
                        CompileExpr(access.Inner);
                        _chunk.EmitOp(OpCode.Jump);
                        int doneAt = _chunk.Here;
                        _chunk.EmitU16(0);
                        _chunk.PatchU16(skipAt, _chunk.Here);
                        _chunk.EmitOp(OpCode.Pop);
                        CompileExpr(new LiteralExpr(access.Line, Value.MakeUndefined()));
                        _chunk.PatchU16(doneAt, _chunk.Here);
                        break;
                    }

                case ConditionalTargetExpr:
                    break;   // (its value is on the stack already)

                case ConditionalExpr ce:
                    {
                        CompileExpr(ce.Condition);
                        int elseJumpAt = EmitJumpIfFalse();
                        CompileExpr(ce.Then);
                        _chunk.EmitOp(OpCode.Jump);
                        int endJumpAt = _chunk.Here;
                        _chunk.EmitU16(0);
                        _chunk.PatchU16(elseJumpAt, _chunk.Here);
                        CompileExpr(ce.Else);
                        _chunk.PatchU16(endJumpAt, _chunk.Here);
                        break;
                    }

                case UnitCoerceExpr { Relabel: true, TargetUnitName: not null } relabel:
                    CompileExpr(relabel.Operand);
                    _chunk.EmitOp(OpCode.RelabelUnit);
                    _chunk.EmitU16(_chunk.AddUnit(fire.Values.Unit.Parse(relabel.TargetUnitName!)));
                    break;

                case TypeCoerceExpr { TargetClass: not null } toEnum when EnumTypeNamed(toEnum.TargetClass!.BaseName) is { } enumType:
                    // `5!Color`: the number as a value of the enum
                    CompileExpr(toEnum.Operand);
                    _chunk.EmitOp(OpCode.ToEnum);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeEnum(0, enumType)));
                    break;

                case TypeCoerceExpr { TargetClass: not null } cast:
                    CompileExpr(cast.Operand);
                    _chunk.EmitOp(OpCode.CastTo);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ResolveTypeRef(cast.TargetClass))));
                    break;

                case TypeCoerceExpr { TargetTypeKeyword: TokenType.KwWord } wordCast:
                    // `x!word`: the value as a machine word (SPEC 8.10)
                    CompileExpr(wordCast.Operand);
                    _chunk.EmitOp(OpCode.ConvertWidth);
                    _chunk.EmitByte(0);
                    _chunk.EmitByte(8);
                    break;

                case TypeCoerceExpr { Width: > 0 } widthCast:
                    CompileExpr(widthCast.Operand);
                    _chunk.EmitOp(OpCode.ConvertWidth);
                    _chunk.EmitByte((byte)widthCast.Width);
                    _chunk.EmitByte((byte)((widthCast.TargetTypeKeyword == TokenType.KwFloat ? 1 : 0) | (widthCast.Unsigned ? 2 : 0)));
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

                case RangeExpr range:
                    // `a..b` runs as `__Range.Of(a, b)` of the prelude
                    CompileExpr(range.From);
                    CompileExpr(range.To);
                    _chunk.EmitOp(OpCode.CallStaticMethod);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString("__Range")));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString("Of")));
                    _chunk.EmitByte(2);
                    break;

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
                    _chunk.EmitByte(ifr.Place == "local" ? (byte)2 : ifr.Place == "global" ? (byte)3 : ifr.Transitive ? (byte)1 : (byte)0);   // (2/3: the owner is a scope)
                    break;

                case NewExpr ne:
                {
                    ulong newCopyMask = CompileArgs(ne.Args, scopeCreating: true, _refParams.ForConstructor(ResolveNewClassName(ne), ne.Args.Count));
                    EmitTypeArgs(ne);
                    EmitCopyArgsPrefix(newCopyMask);
                    EmitArgNames(ne.ArgNames);
                    _chunk.EmitOp(OpCode.NewObject);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(ResolveNewClassName(ne))));
                    _chunk.EmitByte((byte)ne.Args.Count);
                    break;
                }

                case NewArrayExpr na:
                    CompileArrayAlloc(na.SizeExprs, 0);
                    // `new T[n]` of a type parameter: typed by the type argument at run time (`List<int>`: raw ints)
                    if (na.SizeExprs.Count == 1 && na.ElementType is { ArrayRank: 0, PointerDepth: 0, TypeArgs: null } elementType
                        && TypeArgText(elementType, _enclosingClass, null, 0) is ['$', ..] paramText)
                    {
                        _chunk.EmitOp(OpCode.TypeArrayOfParam);
                        _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(paramText)));
                    }
                    break;

                case NewBufferExpr { Words: true } nw:
                    // `new word[n]` (SPEC 8.10): n zero words - an array of n that TypeArray turns into a buffer of words (natively at once: newWordBuffer)
                    CompileExpr(nw.SizeExpr);
                    _chunk.EmitOp(OpCode.NewArray);
                    EmitWidthIfNeeded(this, 0x10000 | (1 << 8) | fire.Values.ArrayElem.Word);
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
                        // a value of the enum: it carries its type (it shows its name, `is of Color`, see Values.EnumType)
                        EmitLoadConst(em.Type != null ? Value.MakeEnum(em.Value, em.Type) : Value.MakeInt(em.Value));
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

                case UntilExpr until:
                    // the call between CancelPush and CancelPop: it follows a token of its own (the caller's and the limits); a catch outside it leaves the frame
                    foreach (var limit in until.Limits) CompileExpr(limit);
                    _chunk.EmitOp(OpCode.CancelPush);
                    _chunk.EmitByte((byte)until.Limits.Count);
                    CompileExpr(until.Inner);
                    _chunk.EmitOp(OpCode.CancelPop);
                    break;

                case AwaitOnExpr awaitOn:
                    foreach (var arg in awaitOn.Args) CompileExpr(arg);
                    CompileLambda(awaitOn.Lambda);
                    _chunk.EmitOp(OpCode.PostGlobal);
                    _chunk.EmitByte((byte)(awaitOn.Args.Count | 0x80 | 0x40));
                    break;

                case CallExpr { Dedicated: true } dedicatedCall:
                    // `await dedicated ...`: the call between Dedicate and Undedicate (natively on a system thread of its own; an exception that leaves it
                    // takes the task back to the pool at its catch - fire_rt.h fire_dedicateCaught)
                    _chunk.EmitOp(OpCode.Dedicate);
                    CompileCall(dedicatedCall with { Dedicated = false });
                    _chunk.EmitOp(OpCode.Undedicate);
                    break;

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
            var inner = new Compiler(_refs, _natives, _enclosingClass, _globalSlotCount, _knownClassNames, _errors, _refParams); inner._enumTypes = _enumTypes; inner._topLevelSourceIndex = _topLevelSourceIndex;
            inner._chunk.OwnerClass = _enclosingClass;
            inner._topLevelSourceIndex = CurrentSourceIndex; // a lambda at the top level: its lines belong to the source it stands in
            // in generic code the lambda takes the type parameters it uses along (it has no `this` and no call of its own to take them from)
            inner._methodTypeParams = _methodTypeParams;
            if (_methodTypeParams is { Count: > 0 } || _enclosingClass?.Decl?.TypeParams is { Count: > 0 } || _lambdaTypeRefs != null)
            {
                inner._lambdaTypeRefs = new List<string>();
                inner._chunk.EmitOp(OpCode.TakeCallTypeArgs);
            }
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

            var proto = new FunctionProto(inner._chunk, lambda.Params.Count, AccessModifier.Public, CompileParamDefaults(_enclosingClass, lambda.Params))
            {
                ParamNames = lambda.Params.Select(p => p.Name).ToArray(),
            };
            if (Reflection) proto.SelectorPath = TrySelectorPath(lambda);
            // (the store of the lambda - ROADMAP_TASKS step 8: which captures each call copies, which ones `handover` moved in)
            if (_refs.TryGetValue(lambda, out var storeRef) && storeRef is ResolvedRef.LambdaCaptures { Loads: { } storeLoads } stored)
            {
                var readOnly = AstNames.ReadOnly(lambda, stored.Variables.Select(v => v.Name));
                for (int i = 0; i < storeLoads.Count && i < 64; i++)
                {
                    if (storeLoads[i] is CallExpr { Callee: IdentifierExpr { Name: fire.Standard.LambdaHandover.NativeName } }) proto.CaptureMovedMask |= 1UL << i;
                    else if (storeLoads[i] is UnaryExpr { Op: UnaryOp.DeepCopy } && !readOnly.Contains(stored.Variables[i].Name)) proto.CaptureCopyMask |= 1UL << i;
                }
            }
            int protoIdx = _chunk.AddFunctionProto(proto);

            // Lambda captures (SPEC 4.2): the values of the used outer locals are loaded NOW (copy), in the enclosing scope.
            var captures = _refs.TryGetValue(lambda, out var captureRef) && captureRef is ResolvedRef.LambdaCaptures lc ? lc.Variables : null;
            if (captures != null)
            {
                if (captures.Count > 255) throw new NotSupportedException("A lambda can capture at most 255 outer variables.");
                var loads = captureRef is ResolvedRef.LambdaCaptures withLoads ? withLoads.Loads : null;
                for (int i = 0; i < captures.Count; i++) CompileExpr(loads?[i] ?? captures[i]);   // (`taking x` in the body: a copy, `handover x`: moved)
            }

            bool hasOnTarget = lambda.OnTarget != null;
            if (hasOnTarget)
                CompileExpr(lambda.OnTarget!); // in the ENCLOSING (current) scope, not in the lambda scope

            if (inner._lambdaTypeRefs is { Count: > 0 } usedTypeParams)
            {
                // the type arguments the body uses, as the code here has them (MakeLambda keeps them in the lambda)
                _chunk.EmitOp(OpCode.SetTypeArgs);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(MapTypeRefs(usedTypeParams))));
                _chunk.EmitByte(2);
            }

            if (captures != null)
            {
                _chunk.EmitOp(OpCode.MakeLambdaCapturing);
                _chunk.EmitU16(protoIdx);
                _chunk.EmitByte(hasOnTarget ? (byte)1 : (byte)0);
                _chunk.EmitByte((byte)captures.Count);
                // `handover x` in the body: the lambda has the instance now - the caller's variable no longer refers to it
                var moves = captureRef is ResolvedRef.LambdaCaptures movedLoads ? movedLoads.Loads : null;
                if (moves != null)
                    for (int i = 0; i < captures.Count; i++)
                        if (moves[i] is CallExpr { Callee: IdentifierExpr { Name: fire.Standard.LambdaHandover.NativeName } })
                        {
                            CompileExpr(new AssignExpr(lambda.Line, captures[i], new LiteralExpr(lambda.Line, Value.MakeUndefined())));
                            _chunk.EmitOp(OpCode.Pop);
                        }
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
                else if (args[i] is UnaryExpr { Op: UnaryOp.TryTake } tryTakeArg)
                {
                    // `f(try take x)` (SPEC 2.2): like `take x`, but only when the running call owns x (code 8 of the mask); a native or built-in: the current scope
                    CompileExpr(tryTakeArg.Operand);
                    if (scopeCreating && i < 16) mask |= 8UL << (4 * i);
                    else { _chunk.EmitOp(OpCode.TryTakeToScope); _chunk.EmitU16(0); }
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
                    // `f(g())`, `f(new X())`: an unclaimed value goes into the parameter (the caller holds no reference to it), so it belongs to the called
                    // function (SPEC 2.1), not to the caller; a direct `new` is marked as such (a message or `fire M(...)` hands it over). A claim site of a
                    // setter (`Store(value)`) gives the setter's value to the call if the setter call still owns it.
                    if (scopeCreating && i < 16)
                    {
                        if (IsSetterClaim(args[i])) mask |= 6UL << (4 * i);
                        else if (args[i] is NewExpr) mask |= 7UL << (4 * i);
                        else if (MayBeUnclaimed(args[i])) mask |= 4UL << (4 * i);
                    }
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

        private void EmitTakeToScope(int depth, bool tryTake = false)
        {
            _chunk.EmitOp(tryTake ? OpCode.TryTakeToScope : OpCode.TakeToScope);
            _chunk.EmitU16(depth);
        }

        // ---- The first slot owns an unclaimed value (SPEC 2.1) ----

        /// <summary>Can the value of this expression be unclaimed - belong to the statement temporaries (`new`, a literal, a copy, what a call hands back)?
        /// Names, literals, `this`, an assignment (its inner slot claimed already) and `take` cannot; the VM checks the rest at run time (the value must
        /// still belong to the temporaries).</summary>
        private static bool MayBeUnclaimed(Expr e) => e switch
        {
            LiteralExpr or IdentifierExpr or ThisExpr or BaseExpr or SelfClassExpr or AssignExpr or IncDecExpr or InterpolatedStringExpr
                or IsOfExpr or IsInExpr or IsFromExpr or TryProcessExpr => false,   // (`a..b` is a call of __Range.Of: a new array, claimed like one)
            UnaryExpr { Op: UnaryOp.Take or UnaryOp.TryTake } => false,
            UnaryExpr { Op: UnaryOp.Trust } t => MayBeUnclaimed(t.Operand),
            ConditionalExpr c => MayBeUnclaimed(c.Then) || MayBeUnclaimed(c.Else),
            _ => true,
        };

        /// <summary>Can evaluating the expression leave something in the statement temporaries (a `new`, a call, an operator, a getter)? Without it a
        /// statement needs no ReleaseTemps.</summary>
        private static bool MayLeaveTemps(Expr? e) => e switch
        {
            null or LiteralExpr or IdentifierExpr or ThisExpr or BaseExpr or SelfClassExpr => false,   // (a lambda is a new value of the statement - ROADMAP_TASKS step 8)
            AssignExpr a => (a.Target is not IdentifierExpr && MayLeaveTemps(a.Target)) || MayLeaveTemps(a.Value),
            IncDecExpr i => i.Target is not IdentifierExpr,
            UnaryExpr { Op: UnaryOp.Take or UnaryOp.TryTake or UnaryOp.Trust } u => MayLeaveTemps(u.Operand),
            _ => true,
        };

        /// <summary>The end of a statement (SPEC 2.1): what is still in the temporaries of the current scope is destroyed.</summary>
        private void EmitReleaseTemps(Expr? e)
        {
            if (MayLeaveTemps(e)) _chunk.EmitOp(OpCode.ReleaseTemps);
        }

        /// <summary>An assignment to a variable `depth` scopes up (0xFFFF: a global): an unclaimed value goes to the scope of the variable; with
        /// `hoist` a value of an inner block moves up to it (SPEC 2.1).</summary>
        private void EmitClaimToScope(int depth, bool unclaimed, bool hoist)
        {
            int flags = (unclaimed ? 1 : 0) | (hoist ? 2 : 0);
            if (flags == 0) return;
            _chunk.EmitOp(OpCode.ClaimToScope);
            _chunk.EmitU16(depth);
            _chunk.EmitByte((byte)flags);
        }

        /// <summary>`obj.name = value` with a value that may be unclaimed (or a claim site of a setter, `setterValue`): SetFieldClaim instead of SetField.</summary>
        private void EmitSetFieldClaim(string name, bool setterValue)
        {
            _chunk.EmitOp(OpCode.SetFieldClaim);
            _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(name)));
            _chunk.EmitByte((byte)(setterValue ? 2 : 0));
        }

        /// <summary>The claim sites of the running setter's `value` (SPEC 2.1 "Setters"): assignments and call arguments; null outside a setter.</summary>
        private HashSet<object>? _setterClaims;

        private bool IsSetterClaim(object node) => _setterClaims != null && _setterClaims.Contains(node);

        /// <summary>Emits the prefix `CopyArgs` (only if a mask is present) - directly BEFORE the call opcode.</summary>
        private void EmitCopyArgsPrefix(ulong mask)
        {
            if (mask == 0) return;
            _chunk.EmitOp(OpCode.CopyArgs);
            for (int part = 0; part < 4; part++) _chunk.EmitU16((int)((mask >> (16 * part)) & 0xFFFF));
        }

        /// <summary>`f(1, size: 2)`: the names of the named arguments for the call that follows (OpCode.ArgNames; nothing without).</summary>
        private void EmitArgNames(IReadOnlyList<string?>? names)
        {
            if (names == null) return;
            var named = names.Where(n => n != null).ToList();
            if (named.Count == 0) return;
            _chunk.EmitOp(OpCode.ArgNames);
            _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(string.Join("|", named))));
        }

        private static void RejectArgNames(CallExpr call, string what)
        {
            if (call.ArgNames != null) throw new CompilerException($"Named arguments are not possible for {what}.", call.Line);
        }

        private void CompileCall(CallExpr call)
        {
            if (call.Callee is IdentifierExpr calleeId && _refs.TryGetValue(calleeId, out var resolved))
            {
                if (resolved is ResolvedRef.Native nativeRef)
                {
                    RejectArgNames(call, "a native function");
                    foreach (var arg in call.Args) CompileExpr(arg);
                    _chunk.EmitOp(OpCode.CallNative);
                    _chunk.EmitU16(_natives.IndexOf(nativeRef.Name));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                if (resolved is ResolvedRef.Extern ext)
                {
                    RejectArgNames(call, "an extern function");
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
                    ulong callCopyMask = CompileArgs(call.Args, scopeCreating: true, _refParams.ForMethod(calleeId.Name, call.Args.Count));
                    EmitCallTypeArgs(call);
                    EmitCopyArgsPrefix(callCopyMask);
                    EmitArgNames(call.ArgNames);
                    _chunk.EmitOp(OpCode.CallStaticMethod);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(callSm.ClassName)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(calleeId.Name)));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                if (resolved is ResolvedRef.ImplicitThisMember && _refParams.IsLambdaField(calleeId.Name))
                {
                    CompileExpr(calleeId);
                    EmitCopyArgsPrefix(CompileArgs(call.Args, scopeCreating: true));
                    RejectArgNames(call, "a lambda");
                    _chunk.EmitOp(OpCode.Call);
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
                    ulong callCopyMask = CompileArgs(call.Args, scopeCreating: true, _refParams.ForMethod(calleeId.Name, call.Args.Count));
                    EmitCallTypeArgs(call);
                    EmitCopyArgsPrefix(callCopyMask);
                    EmitArgNames(call.ArgNames);
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
                    RejectArgNames(call, "'resume'");
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
                    ulong memberCopyMask = CompileArgs(call.Args, scopeCreating: true, _refParams.ForMethod(me.Name, call.Args.Count));
                    EmitCallTypeArgs(call);
                    EmitCopyArgsPrefix(memberCopyMask);
                    EmitArgNames(call.ArgNames);
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

                    ulong staticCopyMask = CompileArgs(call.Args, scopeCreating: true, _refParams.ForMethod(me.Name, call.Args.Count));

                    EmitCallTypeArgs(call);

                    EmitCopyArgsPrefix(staticCopyMask);

                    EmitArgNames(call.ArgNames);
                    _chunk.EmitOp(OpCode.CallBaseMethod);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(_enclosingClass.Base.Name)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                if (_refParams.IsLambdaField(me.Name))
                {
                    // `this.OnClick()`: read the field, call the lambda in it
                    CompileExpr(me);
                    EmitCopyArgsPrefix(CompileArgs(call.Args, scopeCreating: true));
                    RejectArgNames(call, "a lambda");
                    _chunk.EmitOp(OpCode.Call);
                    _chunk.EmitByte((byte)call.Args.Count);
                    return;
                }

                CompileExpr(me.Target);
                ulong callCopyMask = CompileArgs(call.Args, scopeCreating: true, _refParams.ForMethod(me.Name, call.Args.Count));
                EmitCallTypeArgs(call);
                if (call.Awaited) _chunk.EmitOp(OpCode.AwaitReply);   // (in front of the other prefixes: they are looked for right in front of the call)
                EmitCopyArgsPrefix(callCopyMask);
                EmitArgNames(call.ArgNames);
                _chunk.EmitOp(OpCode.CallMethod);
                _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                _chunk.EmitByte((byte)call.Args.Count);
                return;
            }

            CompileExpr(call.Callee);
            EmitCopyArgsPrefix(CompileArgs(call.Args, scopeCreating: true));
            RejectArgNames(call, "a lambda");
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
                EmitUnitStatic(fire.Values.Unit.Unitless, info.RelabelAuto);
        }

        private void EmitUnitStatic(fire.Values.Unit unit, bool relabel)
        {
            if (!relabel) { EmitCoerceUnitStatic(unit); return; }
            _chunk.EmitOp(OpCode.RelabelUnit);
            _chunk.EmitU16(_chunk.AddUnit(unit));
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
                    EmitClaimToScope(0xFFFF, unclaimed: MayBeUnclaimed(a.Value), hoist: false);   // a static field belongs to the program (SPEC 2.1)
                    EmitWidthIfNeeded(this, _refParams.FieldWidth(me.Name), _refParams.FieldUnit(me.Name));
                    _chunk.EmitOp(OpCode.SetStaticField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(sm.ClassName)));
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(me.Name)));
                    return;
                }

                CompileExpr(me.Target);

                if (a.Value is UnaryExpr { Op: UnaryOp.Take or UnaryOp.TryTake } takeValue)
                {
                    CompileExpr(takeValue.Operand);          // [obj, value]
                    _chunk.EmitOp(takeValue.Op == UnaryOp.TryTake ? OpCode.TryTakeToObject : OpCode.TakeToObject);      // the object owns the value from now on (SPEC 2.2)
                }
                else
                {
                    CompileExpr(a.Value);
                    // `obj.f = E`: an unclaimed value belongs to the object - or to the setter call when f is a property (SPEC 2.1); a claim site of a
                    // setter passes the setter's value on the same way
                    bool setterValue = IsSetterClaim(a);
                    if (setterValue || MayBeUnclaimed(a.Value))
                    {
                        EmitWidthIfNeeded(this, _refParams.FieldWidth(me.Name), _refParams.FieldUnit(me.Name));
                        EmitSetFieldClaim(me.Name, setterValue);
                        return;
                    }
                    if (_accessor != 0)
                    {
                        EmitWidthIfNeeded(this, _refParams.FieldWidth(me.Name));
                        EmitSetFieldAccessor(me.Name);
                        return;
                    }
                }

                EmitWidthIfNeeded(this, _refParams.FieldWidth(me.Name), _refParams.FieldUnit(me.Name));
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
                if (a.Value is UnaryExpr { Op: UnaryOp.Take or UnaryOp.TryTake } takeElement)
                {
                    CompileExpr(takeElement.Operand);
                    _chunk.EmitOp(takeElement.Op == UnaryOp.TryTake ? OpCode.TryTakeToArray : OpCode.TakeToArray);       // the array owns the value from now on (SPEC 2.2)
                }
                else
                {
                    CompileExpr(a.Value);
                    if (MayBeUnclaimed(a.Value)) _chunk.EmitOp(OpCode.ClaimToArray);   // `arr[i] = new X()`: the array owns it (SPEC 2.1)
                }
                _chunk.EmitOp(OpCode.ArraySet);
                return;
            }

            if (a.Target is not IdentifierExpr id)
                throw new NotSupportedException(
                    "Invalid assignment target for the bytecode compiler.");

            // Bare field name in a class (`field = new X()` / `field = copy x`): like `this.field = ...` the new
            // object belongs to the object, not the scope (SPEC 2.1/2.4) - otherwise it would be destroyed on leaving the method,
            // while the field still points to it.
            if (a.Value is UnaryExpr { Op: UnaryOp.Take or UnaryOp.TryTake } takeVar)
            {
                CompileTakeAssign(id, takeVar);
                return;
            }

            if (_refs[id] is ResolvedRef.ImplicitThisMember && (MayBeUnclaimed(a.Value) || IsSetterClaim(a)))
            {
                // `field = E` in a class: like `this.field = E` (SPEC 2.1)
                _chunk.EmitOp(OpCode.LoadThis);
                CompileExpr(a.Value);                   // [this, value]
                EmitWidthIfNeeded(this, _refParams.FieldWidth(id.Name), _refParams.FieldUnit(id.Name));
                EmitSetFieldClaim(id.Name, IsSetterClaim(a));
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
                    EmitWidthIfNeeded(this, local.Width, local.RequiredUnit);
                    // `v = E`: an unclaimed value belongs to the scope that declares v; a value of an inner block moves up to it (SPEC 2.1)
                    if (!local.ByRef) EmitClaimToScope(local.Depth, unclaimed: MayBeUnclaimed(a.Value), hoist: local.Depth > 0);
                    EmitStoreVariable(local);
                    break;
                case ResolvedRef.Global global:
                    EmitCheckUnitIfNeeded(this, global.RequiredUnit);
                    EmitWidthIfNeeded(this, global.Width, global.RequiredUnit);
                    // a global variable: an unclaimed value belongs to the global scope; a value of a block of the top-level code moves up to it (SPEC 2.1)
                    EmitClaimToScope(0xFFFF, unclaimed: MayBeUnclaimed(a.Value), hoist: _currentScopeDepth > 0 && IsTopLevelCode);
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
                    EmitWidthIfNeeded(this, _refParams.FieldWidth(id.Name), _refParams.FieldUnit(id.Name));
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
                    EmitWidthIfNeeded(this, _refParams.FieldWidth(id.Name), _refParams.FieldUnit(id.Name));
                    if (_accessor != 0) { EmitSetFieldAccessor(id.Name); break; }
                    _chunk.EmitOp(OpCode.SetField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name)));
                    break;
            }
        }

        /// <summary>A member assignment in a property accessor (SPEC 2.2 "Ownership in property accessors"): what belongs to the accessor call goes to `this`
        /// (a field of it) or to the setter (a property).</summary>
        private void EmitSetFieldAccessor(string name)
        {
            _chunk.EmitOp(OpCode.SetFieldAccessor);
            _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(name)));
            _chunk.EmitByte((byte)(_accessor == 1 ? 1 : 0));
        }

        /// <summary>`name = take x` (SPEC 2.2): x belongs to whoever holds `name` - the scope of the variable, or the object for a field.</summary>
        private void CompileTakeAssign(IdentifierExpr id, UnaryExpr take)
        {
            CompileExpr(take.Operand);
            switch (_refs[id])
            {
                case ResolvedRef.Local local:
                    EmitCheckUnitIfNeeded(this, local.RequiredUnit);
                    EmitWidthIfNeeded(this, local.Width, local.RequiredUnit);
                    EmitTakeToScope(local.ByRef ? 0 : local.Depth, take.Op == UnaryOp.TryTake);
                    EmitStoreVariable(local);
                    break;
                case ResolvedRef.Global global:
                    EmitCheckUnitIfNeeded(this, global.RequiredUnit);
                    EmitWidthIfNeeded(this, global.Width, global.RequiredUnit);
                    EmitTakeToScope(0xFFFF, take.Op == UnaryOp.TryTake);
                    _chunk.EmitOp(OpCode.StoreGlobal);
                    _chunk.EmitU16(global.Slot);
                    break;
                case ResolvedRef.ImplicitThisMember:
                    _chunk.EmitOp(OpCode.LoadThis);         // [value, this]
                    _chunk.EmitOp(OpCode.Swap);             // [this, value]
                    _chunk.EmitOp(take.Op == UnaryOp.TryTake ? OpCode.TryTakeToObject : OpCode.TakeToObject);
                    EmitWidthIfNeeded(this, _refParams.FieldWidth(id.Name), _refParams.FieldUnit(id.Name));
                    _chunk.EmitOp(OpCode.SetField);
                    _chunk.EmitU16(_chunk.AddConstant(Value.MakeString(id.Name)));
                    break;
                default:
                    throw new NotSupportedException($"'{(take.Op == UnaryOp.TryTake ? "try take" : "take")}' needs a holder: '{id.Name}' is not a variable or a field.");
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
            var addSubOp = e.IsIncrement ? OpCode.StepAdd : OpCode.StepSub;

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
                    EmitWidthIfNeeded(this, _refParams.FieldWidth(me.Name), _refParams.FieldUnit(me.Name));
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
                EmitWidthIfNeeded(this, _refParams.FieldWidth(me.Name), _refParams.FieldUnit(me.Name));
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
                EmitWidthIfNeeded(this, _refParams.FieldWidth(id.Name), _refParams.FieldUnit(id.Name));
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
                EmitWidthIfNeeded(this, _refParams.FieldWidth(id.Name), _refParams.FieldUnit(id.Name));
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
                    EmitWidthIfNeeded(this, local.Width, local.RequiredUnit);
                        EmitStoreVariable(local);
                        break;
                    case ResolvedRef.Global global:
                        EmitCheckUnitIfNeeded(this, global.RequiredUnit);
                    EmitWidthIfNeeded(this, global.Width, global.RequiredUnit);
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

            if (u.Op == UnaryOp.Trust)
            {
                // `trust x` (SPEC 8.10b): the value itself - the developer vouches that it is not undefined (the VM still checks where `defined` is declared;
                // a native release build takes it as a defined number)
                CompileExpr(u.Operand);
                _chunk.EmitOp(OpCode.Trust);
                return;
            }
            if (u.Op is UnaryOp.Take or UnaryOp.TryTake)
            {
                // `var a = take x` (SPEC 2.2): x belongs to the current scope (other places - arguments, assignments - are handled where they occur); `try take`
                // only when the running call owns it
                CompileExpr(u.Operand);
                EmitTakeToScope(0, u.Op == UnaryOp.TryTake);
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
                BinaryOp.Power => b.FloatPower ? OpCode.PowerFloat : OpCode.Power,   // (a float target: the float power, SPEC 1.1)
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
                if (left.UnitIsAuto) _chunk.EmitOp(left.RelabelAuto ? OpCode.RelabelUnitDynamic : OpCode.CoerceUnitDynamic);
                _chunk.EmitOp(OpCode.Swap);
            }
            else if (rightAuto && !leftAuto)
            {
                // left (TOS-1) is the anchor; adjust right (TOS) directly (type before unit, see above).
                if (right.TypeIsAuto) _chunk.EmitOp(OpCode.CoerceTypeDynamic);
                if (right.UnitIsAuto) _chunk.EmitOp(right.RelabelAuto ? OpCode.RelabelUnitDynamic : OpCode.CoerceUnitDynamic);
            }
            else if (leftAuto && rightAuto)
            {
                // No anchor present -> unit falls back to unitless.
                if (left.UnitIsAuto)
                {
                    _chunk.EmitOp(OpCode.Swap);
                    EmitUnitStatic(fire.Values.Unit.Unitless, left.RelabelAuto);
                    _chunk.EmitOp(OpCode.Swap);
                }
                if (right.UnitIsAuto) EmitUnitStatic(fire.Values.Unit.Unitless, right.RelabelAuto);
            }
            // otherwise: both fixed/explicit -> no further adjustment; the
            // arithmetic operation itself checks compatibility at runtime.
        }

        private readonly record struct CoercionInfo(
            Expr Inner, bool WantsUnit, string? ExplicitUnit, bool WantsType, TokenType? ExplicitType, bool RelabelAuto = false)
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
            bool relabelAuto = false;
            var current = expr;

            while (true)
            {
                if (current is UnitCoerceExpr { Relabel: false } or UnitCoerceExpr { Relabel: true, TargetUnitName: null })
                {
                    var uc = (UnitCoerceExpr)current;
                    wantsUnit = true;
                    explicitUnit = uc.TargetUnitName;
                    if (uc.Relabel) relabelAuto = true;
                    current = uc.Operand;
                }
                else if (current is TypeCoerceExpr { TargetClass: null, Width: 0, TargetTypeKeyword: not TokenType.KwWord } tc)
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

            return new CoercionInfo(current, wantsUnit, explicitUnit, wantsType, explicitType, relabelAuto);
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
