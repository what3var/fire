using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Lexing;
using fire.Values;

namespace fire.Compiler
{
    /// <summary>How a type conflict is reported (docs/TYPES.md): `Lax` a warning, `Strict` an error.</summary>
    public enum TypeCheckMode { Lax, Strict }

    /// <summary>The errors of the type checker in strict mode (the conflicts of lax mode are warnings and do not stop the build).</summary>
    public sealed class TypeCheckException : Exception
    {
        public IReadOnlyList<Diagnostic> Errors { get; }
        public TypeCheckException(IReadOnlyList<Diagnostic> errors) : base(errors[0].Message + (errors[0].Line > 0 ? $" ({errors[0].Line})" : "")) => Errors = errors;
    }

    /// <summary>
    /// The static types the checker knows. `Dynamic` is "not known": it is compatible with everything, so code that does not state its types is never reported.
    /// </summary>
    internal enum StaticKind { Dynamic, Bool, Int, Float, Char, String, Lambda, Undefined, Class, Object }

    internal readonly record struct StaticType(StaticKind Kind, string? Class = null)
    {
        public static readonly StaticType Dynamic = new(StaticKind.Dynamic);
        public static readonly StaticType Bool = new(StaticKind.Bool);
        public static readonly StaticType Int = new(StaticKind.Int);
        public static readonly StaticType Float = new(StaticKind.Float);
        public static readonly StaticType Char = new(StaticKind.Char);
        public static readonly StaticType String = new(StaticKind.String);
        public static readonly StaticType Lambda = new(StaticKind.Lambda);
        public static readonly StaticType Undefined = new(StaticKind.Undefined);
        /// <summary>The type `class`: any object instance (not a base value, not an array, not a lambda).</summary>
        public static readonly StaticType Object = new(StaticKind.Object);
        public bool IsDynamic => Kind == StaticKind.Dynamic;
        public override string ToString() => Kind == StaticKind.Class ? Class! : Kind.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// The type checker (docs/TYPES.md, step 1): runs after the resolver and computes a static type for every expression it can (literals, declared variables, parameters, return types and fields,
    /// `new`, `var x = typed expression`), `Dynamic` for everything else. A conflict between two KNOWN types (assigning a string to an `int`, an argument that does not fit a typed parameter, a
    /// wrong return value) is reported - as a warning in lax mode, as an error in strict mode. It looks only at the code of the user (not at the preludes) and never changes the program.
    /// Rules: `int` goes to `float` implicitly, everything goes to `string`, `undefined` goes to everything, an object goes to the class it derives from; the type of a target never changes
    /// (a `var` has the type of its initializer for good).
    /// </summary>
    public sealed class TypeChecker
    {
        public const string AssignCode = "TC001";
        public const string ArgumentCode = "TC002";
        public const string ReturnCode = "TC003";
        public const string UntypedCode = "TC004";
        public const string NoReturnTypeCode = "TC005";

        private readonly IReadOnlyDictionary<string, ClassDecl> _classes;
        private readonly Dictionary<int, bool> _strictBySource = new();
        private readonly TypeCheckMode _defaultMode;
        private readonly int _firstUserSource;
        private readonly List<Diagnostic> _diagnostics = new();
        private readonly List<Dictionary<string, StaticType>> _scopes = new();
        private ClassDecl? _class;
        private StaticType? _returnType;
        private int _source;
        /// <summary>The types of the `return` statements of the untyped method that is being checked (null: not collecting; a lambda inside does not count).</summary>
        private List<StaticType>? _collectedReturns;
        private static readonly bool SuggestReturnTypes = Environment.GetEnvironmentVariable("FIRE_TYPECHECK_SUGGEST") == "1";

        private TypeChecker(IReadOnlyDictionary<string, ClassDecl> classes, TypeCheckMode defaultMode, int firstUserSource)
        {
            _classes = classes;
            _defaultMode = defaultMode;
            _firstUserSource = firstUserSource;
        }

        /// <summary>Checks the code of the user (the statements from source <paramref name="firstUserSource"/> on) and returns the conflicts: warnings in lax mode, errors in strict mode.
        /// <paramref name="defaultMode"/> is the project's or the command line's setting; a `#lax` / `#strict` in a file takes its place for that file.</summary>
        public static IReadOnlyList<Diagnostic> Check(IReadOnlyList<Stmt> program, ResolveResult resolved, TypeCheckMode defaultMode, int firstUserSource)
        {
            var checker = new TypeChecker(resolved.Classes, defaultMode, firstUserSource);
            foreach (var stmt in program)
                if (stmt is TypeCheckDirective d) checker._strictBySource[d.Source] = d.Strict;
            checker._scopes.Add(new Dictionary<string, StaticType>());
            foreach (var stmt in program)
            {
                if (stmt.Source < firstUserSource) continue;
                checker._source = stmt.Source;
                checker.Check(stmt);
            }
            return checker._diagnostics;
        }

        /// <summary>The conflicts found, reported the way the build wants them: the warnings go to the collector of the thread, the errors of a strict file stop the build.</summary>
        public static void Run(IReadOnlyList<Stmt> program, ResolveResult resolved, TypeCheckMode defaultMode, int firstUserSource)
        {
            var found = Check(program, resolved, defaultMode, firstUserSource);
            foreach (var d in found.Where(d => d.IsWarning)) DiagnosticCollector.Current?.Add(d);
            var errors = found.Where(d => d.IsError).ToList();
            if (errors.Count > 0) throw new TypeCheckException(errors);
        }

        // -------------------------------------------------------------------------------------------------------------
        // Reporting
        // -------------------------------------------------------------------------------------------------------------
        private bool Strict => _strictBySource.TryGetValue(_source, out bool strict) ? strict : _defaultMode == TypeCheckMode.Strict;

        private void Report(string code, string message, int line) =>
            _diagnostics.Add(new Diagnostic(line, message, Strict ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, code));

        private void CheckAssignable(StaticType value, StaticType target, int line, string what, string code = AssignCode)
        {
            if (Assignable(value, target)) return;
            Report(code, $"Cannot assign a value of type '{value}' to {what} of type '{target}'", line);
        }

        private bool Assignable(StaticType value, StaticType target)
        {
            if (value.IsDynamic || target.IsDynamic || value.Kind == StaticKind.Undefined) return true;
            if (target.Kind == StaticKind.String) return true;                       // everything converts to string (ToString)
            if (value.Kind == StaticKind.Int && target.Kind == StaticKind.Float) return true;
            if (target.Kind == StaticKind.Object) return value.Kind is StaticKind.Class or StaticKind.Object;   // `class` holds objects only
            if (value.Kind == StaticKind.Object) return target.Kind is StaticKind.Class;                          // (an object may be the class the code expects)
            if (value.Kind == StaticKind.Class && target.Kind == StaticKind.Class)
                return DerivesFrom(value.Class!, target.Class!) || DerivesFrom(target.Class!, value.Class!);   // (a downcast is let through: the code may know better)
            return value.Kind == target.Kind;
        }

        private bool DerivesFrom(string derived, string baseName)
        {
            for (int guard = 0; guard < 64; guard++)
            {
                if (derived == baseName) return true;
                if (!_classes.TryGetValue(derived, out var decl) || decl.BaseRefs is not { Count: > 0 }) return false;
                // an interface or an unknown base is not followed: the relation is then not known to be wrong
                if (decl.BaseRefs.Any(b => PlainName(b.BaseName) == baseName)) return true;
                var next = decl.BaseRefs.Select(b => PlainName(b.BaseName)).FirstOrDefault(n => _classes.ContainsKey(n));
                if (next == null) return !decl.BaseRefs.All(b => _classes.ContainsKey(PlainName(b.BaseName)));
                derived = next;
            }
            return true;
        }

        private static string PlainName(string name) => GenericClassNames.PlainName(name);

        // -------------------------------------------------------------------------------------------------------------
        // Types of declarations
        // -------------------------------------------------------------------------------------------------------------
        /// <summary>The static type a type annotation stands for; null for `var` (no annotation).</summary>
        private StaticType? Declared(TypeRef? type, int arrayRanks = 0)
        {
            if (type == null || type.IsInferred) return null;
            if (arrayRanks > 0 || type.ArrayRank > 0 || type.PointerDepth > 0) return StaticType.Dynamic;
            switch (type.BaseName)
            {
                case "int": return StaticType.Int;
                case "float": return StaticType.Float;
                case "bool": return StaticType.Bool;
                case "char": return StaticType.Char;
                case "string": return StaticType.String;
                case "lambda": return StaticType.Lambda;
                case "class": return StaticType.Object;
                case "undefined": return StaticType.Undefined;
                case "any": return StaticType.Dynamic;
            }
            string plain = PlainName(type.BaseName);
            return _classes.ContainsKey(plain) ? new StaticType(StaticKind.Class, plain) : StaticType.Dynamic;
        }

        private StaticType DeclaredOrDynamic(TypeRef? type, int arrayRanks = 0) => Declared(type, arrayRanks) ?? StaticType.Dynamic;

        private void Push() => _scopes.Add(new Dictionary<string, StaticType>());
        private void Pop() => _scopes.RemoveAt(_scopes.Count - 1);
        private void Declare(string name, StaticType type) => _scopes[^1][name] = type;

        private StaticType Lookup(string name)
        {
            for (int i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i].TryGetValue(name, out var type)) return type;
            return _class != null && FindMember(_class.Name, name) is { } member ? member : StaticType.Dynamic;
        }

        /// <summary>The type of a field or property `name` of the class (or of the classes it derives from); null when there is none.</summary>
        private StaticType? FindMember(string className, string name)
        {
            for (int guard = 0; guard < 64 && _classes.TryGetValue(className, out var decl); guard++)
            {
                foreach (var member in decl.Members)
                {
                    if (member is FieldDecl f && f.Name == name) return DeclaredOrDynamic(f.Type, f.ArrayRanks.Count);
                    if (member is PropertyDecl p && p.Name == name) return DeclaredOrDynamic(p.Type);
                }
                var next = decl.BaseRefs?.Select(b => PlainName(b.BaseName)).FirstOrDefault(n => _classes.ContainsKey(n));
                if (next == null) return null;
                className = next;
            }
            return null;
        }

        /// <summary>The one method `name` that takes `argc` arguments (the nearest class that has a method of that name decides); null when there is none or several fit (an overload).</summary>
        private MethodDecl? FindMethod(string className, string name, int argc)
        {
            for (int guard = 0; guard < 64 && _classes.TryGetValue(className, out var decl); guard++)
            {
                var named = decl.Members.OfType<MethodDecl>().Where(m => m.Name == name).ToList();
                if (named.Count > 0)
                {
                    var fitting = named.Where(m => Fits(m.Params, argc)).ToList();
                    return fitting.Count == 1 ? fitting[0] : null;
                }
                var next = decl.BaseRefs?.Select(b => PlainName(b.BaseName)).FirstOrDefault(n => _classes.ContainsKey(n));
                if (next == null) return null;
                className = next;
            }
            return null;
        }

        private static bool Fits(IReadOnlyList<LambdaParam> parameters, int argc) =>
            argc <= parameters.Count && argc >= parameters.Count(p => p.DefaultValue == null);

        private void CheckArguments(IReadOnlyList<LambdaParam> parameters, IReadOnlyList<StaticType> args, IReadOnlyList<Expr> argExprs, string callee)
        {
            for (int i = 0; i < args.Count && i < parameters.Count; i++)
            {
                if (parameters[i].ByRef) continue;
                var declared = Declared(parameters[i].Type, parameters[i].ArrayRanks.Count);
                if (declared is { } target) CheckAssignable(args[i], target, argExprs[i].Line, $"parameter '{parameters[i].Name}' of '{callee}'", ArgumentCode);
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // Statements
        // -------------------------------------------------------------------------------------------------------------
        private void Check(Stmt stmt)
        {
            switch (stmt)
            {
                case Stmt.BlockStmt block:
                    Push();
                    foreach (var s in block.Statements) Check(s);
                    Pop();
                    break;
                case ExprStmt e: Infer(e.Expression); break;
                case VarDeclStmt v: CheckVarDecl(v); break;
                case IfStmt i:
                    Infer(i.Condition);
                    CheckScoped(i.Then);
                    if (i.Else != null) CheckScoped(i.Else);
                    break;
                case WhileStmt w: Infer(w.Condition); CheckScoped(w.Body); break;
                case ForStmt f:
                    Push();
                    if (f.Init != null) Check(f.Init);
                    if (f.Condition != null) Infer(f.Condition);
                    if (f.Increment != null) Infer(f.Increment);
                    CheckScoped(f.Body);
                    Pop();
                    break;
                case ForeachStmt fe:
                    Infer(fe.Iterable);
                    Push();
                    Declare(fe.VarName, StaticType.Dynamic);
                    CheckScoped(fe.Body);
                    Pop();
                    break;
                case ReturnStmt r:
                    if (r.Value != null)
                    {
                        var type = Infer(r.Value);
                        _collectedReturns?.Add(type);
                        if (_returnType is { } expected) CheckAssignable(type, expected, r.Line, "the return value", ReturnCode);
                    }
                    else _collectedReturns?.Add(StaticType.Undefined);
                    break;
                case ThrowStmt t: Infer(t.Value); break;
                case TryStmt t:
                    Check(t.TryBlock);
                    foreach (var c in t.Catches)
                    {
                        Push();
                        Declare(c.VarName, DeclaredOrDynamic(c.TypeRef));
                        Check(c.Body);
                        Pop();
                    }
                    if (t.Finally != null) Check(t.Finally);
                    break;
                case UnsafeStmt u: Check(u.Body); break;
                case NamespaceDecl n:
                    foreach (var m in n.Members) Check(m);
                    break;
                case ClassDecl c: CheckClass(c); break;
            }
        }

        /// <summary>A statement that opens a scope of its own when it is a block (the body of a loop, a branch).</summary>
        private void CheckScoped(Stmt stmt)
        {
            Push();
            Check(stmt);
            Pop();
        }

        private void CheckVarDecl(VarDeclStmt v)
        {
            var declared = Declared(v.Type, v.ArrayRanks.Count);
            var init = v.Initializer != null ? Infer(v.Initializer) : StaticType.Dynamic;
            if (declared is { } target)
            {
                if (v.Initializer != null) CheckAssignable(init, target, v.Line, $"'{v.Name}'");
                Declare(v.Name, target);
            }
            else
                // `var x = ...` has the type of its initializer for good; `undefined` alone says nothing (`undefined!Type` does)
                Declare(v.Name, init.Kind == StaticKind.Undefined ? StaticType.Dynamic : init);
        }

        private void CheckClass(ClassDecl decl)
        {
            var previousClass = _class;
            _class = decl;
            foreach (var member in decl.Members)
            {
                switch (member)
                {
                    case FieldDecl f when f.Initializer != null:
                        {
                            var init = Infer(f.Initializer);
                            if (Declared(f.Type, f.ArrayRanks.Count) is { } target) CheckAssignable(init, target, f.Line, $"field '{f.Name}'");
                            break;
                        }
                    case MethodDecl m:
                        {
                            CheckParametersTyped(m.Params, m.Name, m.Line);
                            bool untyped = m.ReturnType == null || m.ReturnType.IsInferred;
                            bool isOperator = m.Name.StartsWith("operator", StringComparison.Ordinal) || m.Name is "GetIndex" or "SetIndex";
                            if (untyped && !isOperator) Report(NoReturnTypeCode, $"Method '{m.Name}' has no return type and is assumed to return 'any'. Please add one (use 'undefined' if it returns nothing).", m.Line);
                            var previousCollected = _collectedReturns;
                            _collectedReturns = untyped && SuggestReturnTypes ? new List<StaticType>() : null;
                            CheckBody(m.Params, m.Body, untyped ? null : Declared(m.ReturnType, m.ReturnType!.ArrayRank));
                            if (_collectedReturns != null) Console.Error.WriteLine($"SUGGEST\t{decl.Name}\t{m.Name}\t{m.Params.Count}\t{SuggestedType(_collectedReturns)}");
                            _collectedReturns = previousCollected;
                            break;
                        }
                    case ConstructorDecl c:
                        CheckParametersTyped(c.Params, "construct", c.Line);
                        CheckBody(c.Params, c.Body, null);
                        break;
                    case DestructorDecl d:
                        CheckBody(Array.Empty<LambdaParam>(), d.Body, null);
                        break;
                    case PropertyDecl p:
                        if (p.Getter != null) CheckBody(Array.Empty<LambdaParam>(), p.Getter, Declared(p.Type));
                        if (p.Setter != null) CheckBody(new[] { new LambdaParam("value", p.Type, Array.Empty<Expr?>()) }, p.Setter, null);
                        break;
                }
            }
            _class = previousClass;
        }

        /// <summary>A parameter without a type is taken as `any`: said so (a warning in lax mode, an error in strict mode), because a declared type tells the user of the class what it expects.</summary>
        private void CheckParametersTyped(IReadOnlyList<LambdaParam> parameters, string owner, int line)
        {
            foreach (var p in parameters)
                if (p.Type == null || p.Type.IsInferred)
                    Report(UntypedCode, $"Parameter '{p.Name}' of '{owner}' has no type and is assumed to be 'any'. Please add a type (use 'any' if every type is allowed).", line);
        }

        /// <summary>The return type a method without one could declare, from its `return` statements: `undefined` when it returns no value, the one type of its values (`undefined` goes with every type),
        /// `any` for a mix, `?` when a value is of a type that is not known.</summary>
        private static string SuggestedType(List<StaticType> returns)
        {
            var values = returns.Where(r => r.Kind != StaticKind.Undefined).ToList();
            if (values.Count == 0) return "undefined";
            if (values.Any(v => v.IsDynamic)) return "?";
            var kinds = values.Select(v => v.Kind == StaticKind.Class ? "class:" + v.Class : v.Kind.ToString()).Distinct().ToList();
            if (kinds.Count == 1)
                return values[0].Kind switch
                {
                    StaticKind.Class => values[0].Class!,
                    StaticKind.Object => "class",
                    StaticKind.Lambda => "any",
                    _ => values[0].ToString(),
                };
            if (kinds.All(k => k is "Int" or "Float")) return "float";
            return "any";
        }

        private void CheckBody(IReadOnlyList<LambdaParam> parameters, Stmt.BlockStmt body, StaticType? returnType)
        {
            var previousReturn = _returnType;
            _returnType = returnType;
            Push();
            foreach (var p in parameters) Declare(p.Name, DeclaredOrDynamic(p.Type, p.ArrayRanks.Count));
            foreach (var s in body.Statements) Check(s);
            Pop();
            _returnType = previousReturn;
        }

        // -------------------------------------------------------------------------------------------------------------
        // Expressions
        // -------------------------------------------------------------------------------------------------------------
        private StaticType Infer(Expr expr)
        {
            switch (expr)
            {
                case LiteralExpr l:
                    return l.Value.Kind switch
                    {
                        ValueKind.Bool => StaticType.Bool,
                        ValueKind.Int => StaticType.Int,
                        ValueKind.Float => StaticType.Float,
                        ValueKind.Char => StaticType.Char,
                        ValueKind.String => StaticType.String,
                        ValueKind.Undefined => StaticType.Undefined,
                        _ => StaticType.Dynamic,
                    };
                case IdentifierExpr id: return Lookup(id.Name);
                case ThisExpr: return _class != null ? new StaticType(StaticKind.Class, _class.Name) : StaticType.Dynamic;
                case InterpolatedStringExpr s:
                    foreach (var part in s.Parts) if (part is InterpolationExprPart ep) Infer(ep.Expression);
                    return StaticType.String;
                case UnaryExpr u: return InferUnary(u);
                case BinaryExpr b: return InferBinary(b);
                case AssignExpr a: return InferAssign(a);
                case MemberExpr m:
                    {
                        var target = Infer(m.Target);
                        return target.Kind == StaticKind.Class && FindMember(target.Class!, m.Name) is { } member ? member : StaticType.Dynamic;
                    }
                case IndexExpr ix: Infer(ix.Target); Infer(ix.Index); return StaticType.Dynamic;
                case CallExpr c: return InferCall(c);
                case NewExpr n: return InferNew(n);
                case IncDecExpr inc: return Infer(inc.Target);
                case LambdaExpr lam:
                    {
                        var previousReturn = _returnType;
                        var previousCollected = _collectedReturns;
                        _returnType = null;
                        _collectedReturns = null;
                        Push();
                        foreach (var p in lam.Params) Declare(p.Name, DeclaredOrDynamic(p.Type, p.ArrayRanks.Count));
                        foreach (var s in lam.Body.Statements) Check(s);
                        Pop();
                        _returnType = previousReturn;
                        _collectedReturns = previousCollected;
                        return StaticType.Lambda;
                    }
                case IsOfExpr io: Infer(io.Operand); return StaticType.Bool;
                case IsInExpr ii: Infer(ii.Operand); return StaticType.Bool;
                case IsFromExpr ifr: Infer(ifr.Operand); Infer(ifr.OwnerExpr); return StaticType.Bool;
                case UnitCoerceExpr uc: Infer(uc.Operand); return StaticType.Dynamic;
                case TypeCoerceExpr tc:
                    {
                        Infer(tc.Operand);
                        return tc.TargetTypeKeyword switch
                        {
                            TokenType.KwInt => StaticType.Int,
                            TokenType.KwFloat => StaticType.Float,
                            TokenType.KwBool => StaticType.Bool,
                            TokenType.KwChar => StaticType.Char,
                            TokenType.KwString => StaticType.String,
                            _ => StaticType.Dynamic,
                        };
                    }
                case ArrayLiteralExpr arr:
                    foreach (var element in arr.Elements) Infer(element);
                    return StaticType.Dynamic;
                case ThrowExpr th: Infer(th.Value); return StaticType.Dynamic;
                default: return StaticType.Dynamic;
            }
        }

        private StaticType InferUnary(UnaryExpr u)
        {
            var operand = Infer(u.Operand);
            return u.Op switch
            {
                UnaryOp.Negate => operand.Kind is StaticKind.Int or StaticKind.Float ? operand : StaticType.Dynamic,
                UnaryOp.LogicalNot => StaticType.Bool,
                UnaryOp.BitNot => StaticType.Int,
                _ => StaticType.Dynamic,
            };
        }

        private StaticType InferBinary(BinaryExpr b)
        {
            var left = Infer(b.Left);
            var right = Infer(b.Right);
            switch (b.Op)
            {
                case BinaryOp.And or BinaryOp.Or: return StaticType.Bool;
                case BinaryOp.Eq or BinaryOp.NotEq or BinaryOp.Lt or BinaryOp.LtEq or BinaryOp.Gt or BinaryOp.GtEq:
                    // an object can overload the comparison: then the result is whatever the operator says
                    return left.Kind == StaticKind.Class || right.Kind == StaticKind.Class ? StaticType.Dynamic : StaticType.Bool;
                case BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor or BinaryOp.ShiftLeft or BinaryOp.ShiftRight:
                    return left.Kind == StaticKind.Int && right.Kind == StaticKind.Int ? StaticType.Int : StaticType.Dynamic;
                case BinaryOp.Add when left.Kind == StaticKind.String || right.Kind == StaticKind.String:
                    return left.IsDynamic || right.IsDynamic || left.Kind == StaticKind.Class || right.Kind == StaticKind.Class ? StaticType.Dynamic : StaticType.String;
                case BinaryOp.Add or BinaryOp.Sub or BinaryOp.Mul or BinaryOp.Mod:
                    if (left.Kind == StaticKind.Int && right.Kind == StaticKind.Int) return StaticType.Int;
                    if (left.Kind is StaticKind.Int or StaticKind.Float && right.Kind is StaticKind.Int or StaticKind.Float) return StaticType.Float;
                    return StaticType.Dynamic;
                case BinaryOp.Div or BinaryOp.Power:
                    // (the type of `7 / 2` and of `2 ^ -1` is a matter of the arithmetic of the language: not claimed here)
                    return left.Kind == StaticKind.Float && right.Kind is StaticKind.Int or StaticKind.Float
                        || right.Kind == StaticKind.Float && left.Kind is StaticKind.Int or StaticKind.Float ? StaticType.Float : StaticType.Dynamic;
                default: return StaticType.Dynamic;
            }
        }

        private StaticType InferAssign(AssignExpr a)
        {
            var value = Infer(a.Value);
            var target = a.Target switch
            {
                IdentifierExpr id => Lookup(id.Name),
                MemberExpr m => InferMemberTarget(m),
                IndexExpr ix => InferIndexTarget(ix),
                _ => StaticType.Dynamic,
            };
            string what = a.Target switch { IdentifierExpr id => $"'{id.Name}'", MemberExpr m => $"'{m.Name}'", _ => "the target" };
            CheckAssignable(value, target, a.Line, what);
            return target.IsDynamic ? value : target;
        }

        private StaticType InferMemberTarget(MemberExpr m)
        {
            var target = Infer(m.Target);
            return target.Kind == StaticKind.Class && FindMember(target.Class!, m.Name) is { } member ? member : StaticType.Dynamic;
        }

        private StaticType InferIndexTarget(IndexExpr ix)
        {
            Infer(ix.Target);
            Infer(ix.Index);
            return StaticType.Dynamic;
        }

        private StaticType InferCall(CallExpr c)
        {
            var args = c.Args.Select(Infer).ToList();
            MethodDecl? method = null;
            if (c.Callee is MemberExpr m)
            {
                var target = Infer(m.Target);
                if (target.IsDynamic && StaticClassRef(m.Target) is { } staticClass) target = new StaticType(StaticKind.Class, staticClass);
                if (target.Kind == StaticKind.Class) method = FindMethod(target.Class!, m.Name, c.Args.Count);
            }
            else if (c.Callee is IdentifierExpr id)
            {
                if (_class != null && !IsLocal(id.Name)) method = FindMethod(_class.Name, id.Name, c.Args.Count);
            }
            else Infer(c.Callee);
            if (method == null) return StaticType.Dynamic;
            CheckArguments(method.Params, args, c.Args, method.Name);
            return method.ReturnType == null ? StaticType.Dynamic : DeclaredOrDynamic(method.ReturnType, method.ReturnType.ArrayRank);
        }

        /// <summary>`Class.Method(...)`: a (possibly namespace-qualified) class name that is not shadowed by a variable or member.</summary>
        private string? StaticClassRef(Expr target)
        {
            Expr root = target;
            while (root is MemberExpr inner) root = inner.Target;
            if (root is not IdentifierExpr first || IsLocal(first.Name) || _class != null && FindMember(_class.Name, first.Name) != null) return null;
            string name = target is MemberExpr member ? member.Name : ((IdentifierExpr)target).Name;
            return _classes.ContainsKey(name) ? name : null;
        }

        private bool IsLocal(string name)
        {
            for (int i = _scopes.Count - 1; i >= 1; i--)
                if (_scopes[i].ContainsKey(name)) return true;
            return false;
        }

        private StaticType InferNew(NewExpr n)
        {
            var args = n.Args.Select(Infer).ToList();
            string plain = PlainName(n.ClassRef.BaseName);
            if (!_classes.TryGetValue(plain, out var decl)) return StaticType.Dynamic;
            var ctors = decl.Members.OfType<ConstructorDecl>().Where(c => Fits(c.Params, n.Args.Count)).ToList();
            if (ctors.Count == 1) CheckArguments(ctors[0].Params, args, n.Args, "new " + plain);
            return new StaticType(StaticKind.Class, plain);
        }
    }
}
