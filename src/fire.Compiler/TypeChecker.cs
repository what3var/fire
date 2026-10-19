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
    internal enum StaticKind { Dynamic, Bool, Int, Float, Char, String, Lambda, Undefined, Class, Object, Array }

    /// <summary>A static type: `Element` is the element type of an array, `ParamCount`/`Return` the signature of a lambda (-1 / null: not known).</summary>
    internal sealed record StaticType(StaticKind Kind, string? Class = null, StaticType? Element = null, int ParamCount = -1, StaticType? Return = null, IReadOnlyList<StaticType>? Params = null,
        IReadOnlyList<StaticType>? Args = null, Unit? Unit = null, bool UnitIsDimension = false, string? TypeParam = null)
    {
        // (TypeParam: an `any` that is a type parameter of the running code - `$k@` of the method, `$k@Class` of the class - passed on as a type argument)
        /// <summary>The same type with the unit `unit` (null: not known); `dimensionOnly`: a declaration `: any m` that takes every unit of that dimension.</summary>
        public StaticType WithUnit(Unit? unit, bool dimensionOnly = false) => this with { Unit = unit, UnitIsDimension = dimensionOnly };
        public bool IsNumber => Kind is StaticKind.Int or StaticKind.Float;
        public bool Equals(StaticType? other) =>
            other is not null && Kind == other.Kind && Class == other.Class && Equals(Element, other.Element) && ParamCount == other.ParamCount && Equals(Return, other.Return)
            && (Params == null ? other.Params == null : other.Params != null && Params.SequenceEqual(other.Params))
            && (Args == null ? other.Args == null : other.Args != null && Args.SequenceEqual(other.Args))
            && Equals(Unit, other.Unit) && UnitIsDimension == other.UnitIsDimension;
        public override int GetHashCode() => HashCode.Combine(Kind, Class, Element, ParamCount, Return);

        public static readonly StaticType Dynamic = new(StaticKind.Dynamic);
        public static readonly StaticType Bool = new(StaticKind.Bool);
        public static readonly StaticType Int = new(StaticKind.Int);
        public static readonly StaticType Float = new(StaticKind.Float);
        public static readonly StaticType Char = new(StaticKind.Char);
        public static readonly StaticType String = new(StaticKind.String);
        /// <summary>A lambda whose signature is not known.</summary>
        public static readonly StaticType Lambda = new(StaticKind.Lambda);
        public static readonly StaticType Undefined = new(StaticKind.Undefined);
        /// <summary>The type `class`: any object instance (not a base value, not an array, not a lambda).</summary>
        public static readonly StaticType Object = new(StaticKind.Object);
        public static StaticType ArrayOf(StaticType element) => new(StaticKind.Array, Element: element);
        /// <summary>An instance of a class or interface; `args` are its type arguments (null: the raw form, every type argument `any`).</summary>
        public static StaticType ClassOf(string name, IReadOnlyList<StaticType>? args = null) => new(StaticKind.Class, name, Args: args);
        public static StaticType LambdaOf(int paramCount, StaticType? returns, IReadOnlyList<StaticType>? parameters = null) => new(StaticKind.Lambda, ParamCount: paramCount, Return: returns, Params: parameters);
        public bool IsDynamic => Kind == StaticKind.Dynamic;
        public override string ToString() => Kind switch
        {
            StaticKind.Class => GenericClassNames.PlainName(Class!) + (Args is { Count: > 0 } ? "<" + string.Join(", ", Args) + ">" : ""),
            StaticKind.Object => "class",
            StaticKind.Dynamic => "any",
            StaticKind.Array => Element + "[]",
            StaticKind.Lambda => (Return != null ? Return + " " : "") + "lambda" + (ParamCount > 0 ? "<" + string.Join(", ", Params ?? Enumerable.Repeat(Dynamic, ParamCount)) + ">" : ""),
            _ => Kind.ToString().ToLowerInvariant() + (Unit != null && !Unit.IsUnitless ? (UnitIsDimension ? " : any " : " : ") + Unit : ""),
        };
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
        public const string UnitCode = "TC006";
        public const string UnitLossCode = "TC007";
        public const string UnknownMemberCode = "TC008";
        public const string AssignInConditionCode = "TC009";

        private readonly IReadOnlyDictionary<string, ClassDecl> _classes;
        private readonly Dictionary<string, InterfaceDecl> _interfaces = new();
        private readonly Dictionary<Expr, string> _unitChecks;
        private readonly Dictionary<Expr, string[]> _methodTypeArgs;
        private readonly Dictionary<Expr, (TypeRef Type, string What)> _localGuards;
        /// <summary>The declared types of the local variables (by scope, like _scopes): a value whose type does not prove the declaration is checked at runtime.</summary>
        private readonly List<Dictionary<string, TypeRef>> _localTypes = new();
        private readonly Dictionary<int, bool> _strictBySource = new();
        private readonly TypeCheckMode _defaultMode;
        private readonly int _firstUserSource;
        private readonly List<Diagnostic> _diagnostics = new();
        private readonly List<Dictionary<string, StaticType>> _scopes = new();
        private ClassDecl? _class;
        /// <summary>The type parameters of the method being checked (their uses are passed on as type arguments: `$k@`); not inside a lambda.</summary>
        private IReadOnlyList<TypeParam>? _bodyTypeParams;
        private bool _inLambda;
        /// <summary>The type of `this` in a `class extends string/array/...` (null: the class being checked decides).</summary>
        private StaticType? _thisType;
        private StaticType? _returnType;
        private int _source;
        /// <summary>The types of the `return` statements of the untyped method that is being checked (null: not collecting; a lambda inside does not count).</summary>
        private List<StaticType>? _collectedReturns;
        private static readonly bool SuggestReturnTypes = Environment.GetEnvironmentVariable("FIRE_TYPECHECK_SUGGEST") == "1";

        private TypeChecker(IReadOnlyDictionary<string, ClassDecl> classes, TypeCheckMode defaultMode, int firstUserSource, Dictionary<Expr, string> unitChecks,
            Dictionary<Expr, string[]> methodTypeArgs, Dictionary<Expr, (TypeRef Type, string What)> localGuards)
        {
            _localGuards = localGuards;
            _unitChecks = unitChecks;
            _methodTypeArgs = methodTypeArgs;
            _classes = classes;
            _defaultMode = defaultMode;
            _firstUserSource = firstUserSource;
        }

        /// <summary>Checks the code of the user (the statements from source <paramref name="firstUserSource"/> on) and returns the conflicts: warnings in lax mode, errors in strict mode.
        /// <paramref name="defaultMode"/> is the project's or the command line's setting; a `#lax` / `#strict` in a file takes its place for that file.</summary>
        public static IReadOnlyList<Diagnostic> Check(IReadOnlyList<Stmt> program, ResolveResult resolved, TypeCheckMode defaultMode, int firstUserSource)
        {
            var checker = new TypeChecker(resolved.Classes, defaultMode, firstUserSource, resolved.UnitChecks, resolved.MethodTypeArgs, resolved.LocalGuards);
            foreach (var stmt in program)
                if (stmt is TypeCheckDirective d) checker._strictBySource[d.Source] = d.Strict;
            checker.CollectInterfaces(program);
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

        private void CollectInterfaces(IReadOnlyList<Stmt> statements)
        {
            foreach (var stmt in statements)
            {
                if (stmt is InterfaceDecl i) _interfaces[i.Name] = i;
                else if (stmt is NamespaceDecl n) CollectInterfaces(n.Members);
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // Reporting
        // -------------------------------------------------------------------------------------------------------------
        private bool Strict => _strictBySource.TryGetValue(_source, out bool strict) ? strict : _defaultMode == TypeCheckMode.Strict;

        private void Report(string code, string message, int line) =>
            _diagnostics.Add(new Diagnostic(line, message, Strict && code is not (UnitLossCode or AssignInConditionCode) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, code));

        /// <summary>The condition of `if`, `while` and `for` must be a bool: a number is no condition (`if (n)`), the VM throws an ArgumentException for it.</summary>
        private void CheckCondition(Expr condition)
        {
            // `if (a = b)` is almost always a typo for `==`; `if ((a = b))` says it is meant (a warning in strict mode too)
            // (a compound assignment `a |= b` is `a = a | b` with the target itself as the left operand - not a typo)
            if (condition is AssignExpr assign && !(assign.Value is BinaryExpr bin && ReferenceEquals(bin.Left, assign.Target))
                && !Parser.ParenthesizedAssignments.TryGetValue(condition, out _))
                Report(AssignInConditionCode, "An assignment as a condition - did you mean '=='? (write '((a = b))' if the assignment is meant)", condition.Line);
            var type = Infer(condition);
            if (!Assignable(type, StaticType.Bool)) Report(AssignCode, $"A condition must be a 'bool', not '{type}'", condition.Line);
        }

        /// <summary>An operand of `&&`, `||` or `!` must be a bool (like a condition): a number is an ArgumentException at run time.</summary>
        private void CheckLogicalOperand(StaticType operand, string op, int line)
        {
            if (!Assignable(operand, StaticType.Bool)) Report(AssignCode, $"An operand of '{op}' must be a 'bool', not '{operand}'", line);
        }

        private void CheckAssignable(StaticType value, StaticType target, int line, string what, string code = AssignCode, Expr? valueExpr = null)
        {
            if (!Assignable(value, target)) { Report(code, $"Cannot assign a value of type '{value}' to {what} of type '{target}'", line); return; }
            CheckUnitAssignable(value, target, line, what, valueExpr);
        }

        /// <summary>A number goes into a declaration (SPEC "Unit declarations"): the declared unit is converted into (the same dimension), `: any m` takes every
        /// unit of the dimension, a declaration without a unit takes unitless numbers only. Another dimension is TC006; a whole number converted into a coarser
        /// unit may lose its fraction (TC007, a warning). Not known units say nothing.</summary>
        private void CheckUnitAssignable(StaticType value, StaticType target, int line, string what, Expr? valueExpr = null)
        {
            // a float holds a float: a whole number (or a value not known here) is converted at runtime (`float f = 3` holds 3.0)
            if (valueExpr != null && target.Kind == StaticKind.Float && (value.IsDynamic || value.Kind == StaticKind.Int))
                _unitChecks[valueExpr] = "#any";
            // a number declared without a unit takes unitless numbers only: a value whose unit is not known here (or is another one) is checked at runtime
            if (valueExpr != null && target.IsNumber && target.Unit is { IsUnitless: true } && !target.UnitIsDimension
                && (value.IsDynamic || value.IsNumber && value.Unit is not { IsUnitless: true }))
                _unitChecks[valueExpr] = target.Kind == StaticKind.Float ? "#unitless" : "unitless";
            // a unit taken from an initializer (`int a = 5mm`): a later value is converted into it at runtime (a declared unit does that itself)
            if (valueExpr != null && target.IsNumber && target.Unit is { IsUnitless: false } targetUnit && !target.UnitIsDimension
                && (value.IsDynamic || value.IsNumber && !targetUnit.Equals(value.Unit)) && DemandText(targetUnit) is { } demand)
                _unitChecks[valueExpr] = (target.Kind == StaticKind.Float ? "#" : "") + demand;
            if (!value.IsNumber || !target.IsNumber || value.Unit is not { } from || target.Unit is not { } to) return;
            if (!from.IsCompatibleWith(to))
            {
                Report(UnitCode, $"A value in '{UnitText(from)}' does not fit {what} in '{(target.UnitIsDimension ? "any " : "")}{UnitText(to)}'", line);
                return;
            }
            if (!target.UnitIsDimension && value.Kind == StaticKind.Int && target.Kind == StaticKind.Int && !from.Equals(to) && from.IsLinear && to.IsLinear && from.ConversionFactorTo(to) < 1)
                Report(UnitLossCode, $"A whole number in '{UnitText(from)}' converted into '{UnitText(to)}' may lose its fraction (declare a float or a finer unit)", line);
        }

        private static string UnitText(Unit unit) => unit.IsUnitless ? "unitless" : unit.ToString();

        /// <summary>The name of a unit as the runtime reads it back (`CheckUnit`); null for a composed unit without such a name (`m^3(×1e-09)`).</summary>
        private static string? DemandText(Unit unit)
        {
            string text = unit.ToString();
            try { return Unit.Parse(text).Equals(unit) ? text : null; }
            catch (Exception) { return null; }
        }

        private bool Assignable(StaticType value, StaticType target)
        {
            if (value.IsDynamic || target.IsDynamic || value.Kind == StaticKind.Undefined) return true;
            if (target.Kind == StaticKind.String) return true;                       // everything converts to string (ToString)
            // an array is an `IEnumerable` (of its elements)
            if (value.Kind == StaticKind.Array && target is { Kind: StaticKind.Class, Class: "IEnumerable" })
                return target.Args is not [var wanted] || Assignable(value.Element!, wanted);
            if (value.Kind == StaticKind.Array || target.Kind == StaticKind.Array)
                return value.Kind == target.Kind && Assignable(value.Element!, target.Element!);
            if (value.Kind == StaticKind.Lambda && target.Kind == StaticKind.Lambda)
            {
                if (value.ParamCount >= 0 && target.ParamCount >= 0 && value.ParamCount != target.ParamCount) return false;
                if (value.Params != null && target.Params != null && value.Params.Count == target.Params.Count
                    && value.Params.Zip(target.Params).Any(p => !Assignable(p.Second, p.First))) return false;
                // (a lambda that returns a value may stand where nothing is expected of it)
                return value.Return == null || target.Return == null || target.Return.Kind == StaticKind.Undefined || Assignable(value.Return, target.Return);
            }
            if (value.Kind == StaticKind.Int && target.Kind == StaticKind.Float) return true;
            if (target.Kind == StaticKind.Object) return value.Kind is StaticKind.Class or StaticKind.Object;   // `class` holds objects only
            if (value.Kind == StaticKind.Object) return target.Kind is StaticKind.Class;                          // (an object may be the class the code expects)
            if (value.Kind == StaticKind.Class && target.Kind == StaticKind.Class)
            {
                if (!DerivesFrom(value.Class!, target.Class!) && !DerivesFrom(target.Class!, value.Class!)) return false;   // (a downcast is let through: the code may know better)
                // `List<int>` is not a `List<string>`: the type arguments of the same class must agree (a raw form or an `any` agrees with everything)
                if (target.Args == null) return true;
                var view = value.Class == target.Class ? value : AsBase(value, target.Class!);
                return view?.Args == null || view.Args.Zip(target.Args).All(p => p.First.IsDynamic || p.Second.IsDynamic || p.First == p.Second);
            }
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
        /// <summary>The static type a type annotation stands for; null for `var` (no annotation). `arrayRanks` are the brackets after the name (`int values[]`).</summary>
        private StaticType? Declared(TypeRef? type, int arrayRanks = 0, IReadOnlyDictionary<string, StaticType>? map = null)
        {
            if (type == null || type.IsInferred) return null;
            if (type.PointerDepth > 0) return StaticType.Dynamic;
            var result = type.BaseName == "lambda" ? DeclaredLambda(type.LambdaSignature, map) : FromName(type.BaseName, map);
            // `List<int>`: the generic class (named `List`1` next to a non-generic `List`) with its type arguments
            if (type.TypeArgs is { Count: > 0 } typeArgs && result.Kind == StaticKind.Class)
                result = StaticType.ClassOf(GenericName(PlainName(type.BaseName), typeArgs.Count), typeArgs.Select(a => Declared(a, 0, map) ?? StaticType.Dynamic).ToList());
            // a number declared without a unit is unitless; `: any` takes every unit, `: any m` every unit of that dimension (a type parameter replaced by
            // its type argument keeps the argument's unit: `T v` of a `Box<int : cm>` is in cm)
            bool substituted = map != null && map.ContainsKey(type.BaseName);
            if (result.IsNumber && arrayRanks + type.ArrayRank == 0 && !(substituted && type.Unit == null))
                result = type.Unit switch
                {
                    null => result.WithUnit(Unit.Unitless),
                    "any" => result,
                    { } u when u.StartsWith("any ", StringComparison.Ordinal) => result.WithUnit(Unit.Parse(u.Substring(4)), dimensionOnly: true),
                    { } u => result.WithUnit(Unit.Parse(u)),
                };
            for (int i = 0; i < arrayRanks + type.ArrayRank; i++) result = StaticType.ArrayOf(result);
            return result;
        }

        /// <summary>`[Return] lambda<P1, ..., Pn>`: the number of parameters and the return type (a selector `lambda member<T>` takes one parameter).</summary>
        private StaticType DeclaredLambda(LambdaSignature? signature, IReadOnlyDictionary<string, StaticType>? map = null)
        {
            if (signature == null) return StaticType.LambdaOf(0, null);
            // a selector `lambda member<T>`: one parameter of the type T (so `x => x.nmae` is reported); without a type (`selector<>`) any number
            if (signature.IsSelector)
                return signature.ParamTypeNames.Count == 1 ? StaticType.LambdaOf(1, null, new List<StaticType> { FromName(signature.ParamTypeNames[0], map) }) : StaticType.LambdaOf(-1, null);
            return StaticType.LambdaOf(signature.ParamTypeNames.Count, signature.ReturnTypeName != null ? FromName(signature.ReturnTypeName, map) : null,
                signature.ParamTypeNames.Select(n => FromName(n, map)).ToList());
        }

        /// <summary>The name of the generic class `name` with `count` type parameters (`Box`1` when a non-generic `Box` exists, otherwise `Box`).</summary>
        private string GenericName(string name, int count)
        {
            string mangled = GenericClassNames.Mangle(name, count);
            return _classes.ContainsKey(mangled) || _interfaces.ContainsKey(mangled) ? mangled : name;
        }

        /// <summary>What the type parameters of a generic class stand for in one of its instances (`T` of `List<int>`: `int`; of the raw `List`: `any`).</summary>
        private static Dictionary<string, StaticType>? MapFor(IReadOnlyList<TypeParam>? typeParams, IReadOnlyList<StaticType>? args)
        {
            if (typeParams is not { Count: > 0 }) return null;
            var map = new Dictionary<string, StaticType>();
            for (int i = 0; i < typeParams.Count; i++) map[typeParams[i].Name] = args != null && i < args.Count ? args[i] : StaticType.Dynamic;
            return map;
        }

        /// <summary>The class and its base classes, each with what its type parameters stand for in <paramref name="receiver"/>.</summary>
        private IEnumerable<(ClassDecl Decl, Dictionary<string, StaticType>? Map)> Chain(StaticType receiver)
        {
            string? name = receiver.Class;
            var args = receiver.Args;
            for (int guard = 0; guard < 64 && name != null && _classes.TryGetValue(name, out var decl); guard++)
            {
                var map = MapFor(decl.TypeParams, args);
                yield return (decl, map);
                var baseRef = decl.BaseRefs?.FirstOrDefault(b => _classes.ContainsKey(NameOf(b)));
                if (baseRef == null) yield break;
                name = NameOf(baseRef);
                args = baseRef.TypeArgs?.Select(a => Declared(a, 0, map) ?? StaticType.Dynamic).ToList();
            }
        }

        /// <summary>The class or interface a base reference names (`Command<IDevice>`: the generic `Command`).</summary>
        private string NameOf(TypeRef baseRef) =>
            baseRef.TypeArgs is { Count: > 0 } a ? GenericName(PlainName(baseRef.BaseName), a.Count) : PlainName(baseRef.BaseName);

        /// <summary>`value` seen as its base class or interface `baseName` (with the type arguments it has there); null when it is none.</summary>
        private StaticType? AsBase(StaticType value, string baseName)
        {
            foreach (var (decl, map) in Chain(value))
            {
                if (decl.Name == baseName) return StaticType.ClassOf(decl.Name, map == null ? null : decl.TypeParams!.Select(p => map[p.Name]).ToList());
                foreach (var b in decl.BaseRefs ?? Array.Empty<TypeRef>())
                    if (NameOf(b) == baseName) return StaticType.ClassOf(baseName, b.TypeArgs?.Select(a => Declared(a, 0, map) ?? StaticType.Dynamic).ToList());
            }
            return null;
        }

        /// <summary>The type of the elements `foreach` takes from a value: of an array its element type, of an `IEnumerable<T>` `T`, otherwise what
        /// the `GetCurrent()` of the enumerator of its `GetEnumerator()` returns (not known: `any`).</summary>
        private StaticType ElementOf(StaticType iterable)
        {
            if (iterable.Kind == StaticKind.Array) return iterable.Element!;
            if (iterable.Kind == StaticKind.String) return StaticType.Char;
            if (iterable.Kind != StaticKind.Class) return StaticType.Dynamic;
            if (AsBase(iterable, "IEnumerable") is { Args: [var element] }) return element;
            if (iterable.Class == "IEnumerable" && iterable.Args is [var own]) return own;
            if (FindMethod(iterable, "GetEnumerator", 0, out var map) is { ReturnType: { } enumeratorType }
                && Declared(enumeratorType, 0, map) is { Kind: StaticKind.Class } enumerator)
            {
                if (enumerator.Class == "IEnumerator" && enumerator.Args is [var current]) return current;
                if (AsBase(enumerator, "IEnumerator") is { Args: [var viaInterface] }) return viaInterface;
                if (FindMethod(enumerator, "GetCurrent", 0, out var currentMap) is { ReturnType: { } currentType }) return Declared(currentType, 0, currentMap) ?? StaticType.Dynamic;
            }
            return StaticType.Dynamic;
        }

        /// <summary>The static type of a type name (a keyword, `any`, `class`, a class; everything else - an enum, an interface, a type parameter - is not known).</summary>
        private StaticType FromName(string name, IReadOnlyDictionary<string, StaticType>? map = null)
        {
            if (map != null && map.TryGetValue(name, out var substituted)) return substituted;
            switch (name)
            {
                case "int": case "byte": return StaticType.Int;
                case "float": return StaticType.Float;
                case "bool": return StaticType.Bool;
                case "char": return StaticType.Char;
                case "string": return StaticType.String;
                case "lambda": return StaticType.LambdaOf(0, null);
                case "class": return StaticType.Object;
                case "undefined": return StaticType.Undefined;
                case "any": return StaticType.Dynamic;
            }
            string plain = PlainName(name);
            if (_classes.ContainsKey(plain) || _interfaces.ContainsKey(plain)) return StaticType.ClassOf(plain);
            return TypeParamRef(name) is { } typeParam ? StaticType.Dynamic with { TypeParam = typeParam } : StaticType.Dynamic;
        }

        /// <summary>A type parameter of the running method (`$k@`) or class (`$k@Class`): what a call of a generic method passes on for it (null: none);
        /// a lambda takes those it uses along.</summary>
        private string? TypeParamRef(string name)
        {
            if (_bodyTypeParams != null)
                for (int k = 0; k < _bodyTypeParams.Count; k++)
                    if (_bodyTypeParams[k].Name == name) return $"${k}@";
            if (_class?.TypeParams is { } classParams)
                for (int k = 0; k < classParams.Count; k++)
                    if (classParams[k].Name == name) return $"${k}@{_class.Name}";
            return null;
        }

        private StaticType DeclaredOrDynamic(TypeRef? type, int arrayRanks = 0, IReadOnlyDictionary<string, StaticType>? map = null) => Declared(type, arrayRanks, map) ?? StaticType.Dynamic;

        /// <summary>A number declared without a unit takes the unit of its initializer for good (SPEC 3.5: `int a = 5mm` is in mm from then on); an initializer
        /// whose unit is not known leaves the unit free. Without an initializer (or with a unitless one) the declaration stays unitless.</summary>
        private static StaticType WithInitializerUnit(StaticType declared, TypeRef? type, int arrayRanks, StaticType init) =>
            declared.IsNumber && arrayRanks == 0 && type is { Unit: null } && !declared.UnitIsDimension
                ? init.IsNumber && init.Unit is { } unit ? declared.WithUnit(unit) : init.IsDynamic || init.IsNumber ? declared.WithUnit(null) : declared
                : declared;

        /// <summary>The type of a field: its declaration, with the unit of a literal initializer when it declares none (`float w = 5mm` is in mm).</summary>
        private StaticType FieldType(FieldDecl f, IReadOnlyDictionary<string, StaticType>? map = null)
        {
            var declared = DeclaredOrDynamic(f.Type, f.ArrayRanks.Count, map);
            return f.Initializer == null ? declared : WithInitializerUnit(declared, f.Type, f.ArrayRanks.Count, LiteralType(f.Initializer));
        }

        /// <summary>The type of a constant initializer (a number literal, negated or converted with ':'/'::'), Dynamic for anything else.</summary>
        private static StaticType LiteralType(Expr e) => e switch
        {
            LiteralExpr { Value.Kind: ValueKind.Int } l => StaticType.Int.WithUnit(l.Value.Unit ?? Unit.Unitless),
            LiteralExpr { Value.Kind: ValueKind.Float } l => StaticType.Float.WithUnit(l.Value.Unit ?? Unit.Unitless),
            UnaryExpr { Op: UnaryOp.Negate } u => LiteralType(u.Operand),
            UnitCoerceExpr { TargetUnitName: { } unit } c when LiteralType(c.Operand) is { IsNumber: true } t => t.WithUnit(Unit.Parse(unit)),
            _ => StaticType.Dynamic,
        };

        /// <summary>The type of `this` in the class being checked (its type parameters are `any` there - an `any` that is passed on as the type argument
        /// of the object to a generic method, `$k@Class`; not inside a lambda).</summary>
        private StaticType ThisClass() =>
            // `class extends array/string/int` becomes the class `$array`/`$string`/`$int`: `this` there is a value of that base type
            _class!.Name.StartsWith('$') ? (_class.Name == "$array" ? StaticType.ArrayOf(StaticType.Dynamic) : FromName(_class.Name.Substring(1)))
            : _class.TypeParams is { Count: > 0 } typeParams && !_inLambda
                ? StaticType.ClassOf(_class.Name, typeParams.Select((_, k) => StaticType.Dynamic with { TypeParam = $"${k}@{_class.Name}" }).ToList())
                : StaticType.ClassOf(_class.Name);

        private void Push() => _scopes.Add(new Dictionary<string, StaticType>());
        private void Pop() { _scopes.RemoveAt(_scopes.Count - 1); if (_localTypes.Count > _scopes.Count) _localTypes.RemoveRange(_scopes.Count, _localTypes.Count - _scopes.Count); }

        /// <summary>A local variable declared with int/float/bool, a class, an interface or a type parameter.</summary>
        private void DeclareLocalType(string name, TypeRef type)
        {
            while (_localTypes.Count < _scopes.Count) _localTypes.Add(new Dictionary<string, TypeRef>());
            _localTypes[_scopes.Count - 1][name] = type;
        }

        /// <summary>The declaration of a local variable, if it was declared with a type that is checked (null otherwise, or when an inner scope shadows it).</summary>
        private TypeRef? LocalType(string name)
        {
            for (int i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i].ContainsKey(name)) return i < _localTypes.Count && _localTypes[i].TryGetValue(name, out var type) ? type : null;
            return null;
        }

        /// <summary>A value going into a local declared `target` that its static type does not prove (an `any`, a downcast, a type parameter): checked
        /// at runtime (ResolveResult.LocalGuards).</summary>
        private void GuardLocalValue(Expr valueExpr, StaticType value, StaticType target, TypeRef type, string name)
        {
            // (a conflict the type checker reports - a warning in lax mode - is left as it is: the program runs on, as lax promises)
            if (value.Kind == StaticKind.Undefined || !Assignable(value, target)) return;
            bool needed = target.Kind switch
            {
                StaticKind.Int => value.Kind != StaticKind.Int,
                StaticKind.Float => value.Kind is not (StaticKind.Float or StaticKind.Int),
                StaticKind.Bool => value.Kind != StaticKind.Bool,
                StaticKind.Class => value.Kind != StaticKind.Class || !DerivesFrom(value.Class!, target.Class!),
                StaticKind.Dynamic => target.TypeParam != null && value.TypeParam != target.TypeParam,
                _ => false,
            };
            if (needed) _localGuards[valueExpr] = (type, $"Variable '{name}'");
        }
        private void Declare(string name, StaticType type)
        {
            _scopes[^1][name] = type;
            if (_localTypes.Count == _scopes.Count) _localTypes[^1].Remove(name);   // (a typed local again sets its declaration: DeclareLocalType)
        }

        private StaticType Lookup(string name)
        {
            for (int i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i].TryGetValue(name, out var type)) return type;
            return _class != null && FindMember(ThisClass(), name) is { } member ? member : StaticType.Dynamic;
        }

        /// <summary>The type of a field or property `name` of an instance (looked up in its class and the classes it derives from, with their type
        /// parameters replaced by the instance's type arguments); null when there is none.</summary>
        private StaticType? FindMember(StaticType receiver, string name)
        {
            foreach (var (decl, map) in Chain(receiver))
                foreach (var member in decl.Members)
                {
                    if (member is FieldDecl f && f.Name == name) return FieldType(f, map);
                    if (member is PropertyDecl p && p.Name == name) return DeclaredOrDynamic(p.Type, 0, map);
                }
            return null;
        }

        private StaticType? FindMember(string className, string name) => FindMember(StaticType.ClassOf(className), name);

        /// <summary>The one method `name` of an instance that takes `argc` arguments (the nearest class that has a method of that name decides; of an
        /// interface its signature); null when there is none or several fit (an overload). <paramref name="map"/>: what the type parameters stand for.</summary>
        private MethodDecl? FindMethod(StaticType receiver, string name, int argc, out Dictionary<string, StaticType>? map)
        {
            map = null;
            if (receiver.Class != null && _interfaces.TryGetValue(receiver.Class, out var iface))
            {
                var sigs = iface.Methods.Where(m => m.Name == name && Fits(m.Params, argc)).ToList();
                if (sigs.Count != 1) return null;
                map = MapFor(iface.TypeParams, receiver.Args);
                return new MethodDecl(0, sigs[0].Line, sigs[0].ReturnType, sigs[0].Name, sigs[0].Params, new Stmt.BlockStmt(0, sigs[0].Line, Array.Empty<Stmt>()));
            }
            foreach (var (decl, classMap) in Chain(receiver))
            {
                var named = decl.Members.OfType<MethodDecl>().Where(m => m.Name == name).ToList();
                if (named.Count == 0) continue;
                var fitting = named.Where(m => Fits(m.Params, argc)).ToList();
                if (fitting.Count != 1) return null;
                map = classMap;
                return fitting[0];
            }
            return null;
        }

        private MethodDecl? FindMethod(string className, string name, int argc) => FindMethod(StaticType.ClassOf(className), name, argc, out _);

        private static bool Fits(IReadOnlyList<LambdaParam> parameters, int argc) =>
            argc <= parameters.Count && argc >= parameters.Count(p => p.DefaultValue == null);

        private void CheckArguments(IReadOnlyList<LambdaParam> parameters, IReadOnlyList<StaticType> args, IReadOnlyList<Expr> argExprs, string callee,
            IReadOnlyDictionary<string, StaticType>? map = null)
        {
            for (int i = 0; i < args.Count && i < parameters.Count; i++)
            {
                if (parameters[i].ByRef) continue;
                var declared = Declared(parameters[i].Type, parameters[i].ArrayRanks.Count, map);
                if (declared is { } target) CheckAssignable(args[i], target, argExprs[i].Line, $"parameter '{parameters[i].Name}' of '{callee}'", ArgumentCode, argExprs[i]);
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
                    CheckCondition(i.Condition);
                    CheckScoped(i.Then);
                    if (i.Else != null) CheckScoped(i.Else);
                    break;
                case WhileStmt w: CheckCondition(w.Condition); CheckScoped(w.Body); break;
                case DoWhileStmt dw: CheckScoped(dw.Body); CheckCondition(dw.Condition); break;
                case ForStmt f:
                    Push();
                    if (f.Init != null) Check(f.Init);
                    if (f.Condition != null) CheckCondition(f.Condition);
                    if (f.Increment != null) Infer(f.Increment);
                    CheckScoped(f.Body);
                    Pop();
                    break;
                case ForeachStmt fe:
                    {
                        var iterable = Infer(fe.Iterable);
                        Push();
                        Declare(fe.VarName, ElementOf(iterable));
                    }
                    CheckScoped(fe.Body);
                    Pop();
                    break;
                case ReturnStmt r:
                    if (r.Value != null)
                    {
                        var type = Infer(r.Value, _returnType);
                        _collectedReturns?.Add(type);
                        if (_returnType is { } expected) CheckAssignable(type, expected, r.Line, "the return value", ReturnCode, r.Value);
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
                case FireStmt f:
                    {
                        // the block runs in a scope of its own: the `taking` copies and the actor of `with` are its variables
                        var captured = f.TakingCaptures.Select(c => (c.VarName, Type: Infer(c.Source))).ToList();
                        var actor = f.WithSource != null ? Infer(f.WithSource) : StaticType.Dynamic;
                        Push();
                        foreach (var (name, type) in captured) Declare(name, type);
                        if (f.WithVarName != null) Declare(f.WithVarName, actor);
                        foreach (var s in f.Body.Statements) Check(s);
                        Pop();
                        break;
                    }
                case ProcessStmt p: Infer(p.Target); break;
                case SilenceStmt si: Infer(si.Target); break;
                case DeleteStmt d: Infer(d.Target); break;
                case TerminateStmt te: if (te.Value != null) Infer(te.Value); break;
                case PostGlobalStmt pg: Infer(pg.Lambda); foreach (var a in pg.Args) Infer(a); break;
                case CatchThreadsDecl ct:
                    Push();
                    if (ct.VarName != null) Declare(ct.VarName, DeclaredOrDynamic(ct.TypeRef));
                    Check(ct.Body);
                    Pop();
                    break;
                case CatchTerminateDecl cte:
                    Push();
                    if (cte.VarName != null) Declare(cte.VarName, StaticType.Dynamic);
                    Check(cte.Body);
                    Pop();
                    break;
                case InterfaceDecl id:
                    foreach (var sig in id.Methods)
                    {
                        CheckParametersTyped(sig.Params, sig.Name, sig.Line);
                        if (sig.ReturnType == null || sig.ReturnType.IsInferred) ReportNoReturnType(sig.Name, sig.Line);
                    }
                    break;
                case ClassExtensionDecl ext:
                    {
                        // `class extends string { ... }`: `this` is a value of the extended type
                        var previousThis = _thisType;
                        _thisType = ext.TargetRef.BaseName is "array" or "$array" ? StaticType.ArrayOf(StaticType.Dynamic) : DeclaredOrDynamic(ext.TargetRef);
                        CheckMembers(ext.Members, ext.TargetRef.BaseName);
                        _thisType = previousThis;
                        break;
                    }
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
            var init = v.Initializer != null ? Infer(v.Initializer, declared) : StaticType.Dynamic;
            if (declared is { } target)
            {
                if (v.Initializer != null)
                {
                    target = WithInitializerUnit(target, v.Type, v.ArrayRanks.Count, init);
                    CheckAssignable(init, target, v.Line, $"'{v.Name}'", valueExpr: v.Initializer);
                    if (v.ArrayRanks.Count == 0 && v.Type != null) GuardLocalValue(v.Initializer, init, target, v.Type, v.Name);
                }
                Declare(v.Name, target);
                if (v.ArrayRanks.Count == 0 && v.Type is { ArrayRank: 0, PointerDepth: 0 } localType) DeclareLocalType(v.Name, localType);
            }
            else
            {
                // `var x = ...` has the type of its initializer for good; `undefined` alone says nothing (`undefined!Type` does). Its unit may change
                // (a `var` without a unit takes every unit), `var x : mm` demands mm.
                var type = init.Kind == StaticKind.Undefined ? StaticType.Dynamic : init;
                if (type.IsNumber)
                {
                    if (v.Type is { Unit: { } unitName } && unitName != "any")
                    {
                        var demanded = unitName.StartsWith("any ", StringComparison.Ordinal) ? type.WithUnit(Unit.Parse(unitName.Substring(4)), dimensionOnly: true) : type.WithUnit(Unit.Parse(unitName));
                        if (v.Initializer != null) CheckUnitAssignable(init, demanded, v.Line, $"'{v.Name}'");
                        type = demanded;
                    }
                    else type = type.WithUnit(null);
                }
                Declare(v.Name, type);
            }
        }

        private void CheckClass(ClassDecl decl)
        {
            var previousClass = _class;
            _class = decl;
            CheckMembers(decl.Members, decl.Name);
            _class = previousClass;
        }

        private void ReportNoReturnType(string method, int line) =>
            Report(NoReturnTypeCode, $"Method '{method}' has no return type and is assumed to return 'any'. Please add one (use 'undefined' if it returns nothing).", line);

        private void CheckMembers(IReadOnlyList<Stmt> members, string ownerName)
        {
            foreach (var member in members)
            {
                switch (member)
                {
                    case FieldDecl f when f.Initializer != null:
                        {
                            var declaredField = Declared(f.Type, f.ArrayRanks.Count);
                            var init = Infer(f.Initializer, declaredField);
                            if (declaredField != null) declaredField = WithInitializerUnit(declaredField, f.Type, f.ArrayRanks.Count, LiteralType(f.Initializer));
                            if (declaredField is { } target) CheckAssignable(init, target, f.Line, $"field '{f.Name}'", valueExpr: f.Initializer);
                            break;
                        }
                    case MethodDecl m:
                        {
                            CheckParametersTyped(m.Params, m.Name, m.Line);
                            bool untyped = m.ReturnType == null || m.ReturnType.IsInferred;
                            bool isOperator = m.Name.StartsWith("operator", StringComparison.Ordinal) || m.Name is "GetIndex" or "SetIndex";
                            if (untyped && !isOperator) ReportNoReturnType(m.Name, m.Line);
                            var previousCollected = _collectedReturns;
                            _collectedReturns = untyped && SuggestReturnTypes ? new List<StaticType>() : null;
                            var previousTypeParams = _bodyTypeParams;
                            _bodyTypeParams = m.TypeParams;
                            CheckBody(m.Params, m.Body, untyped ? null : Declared(m.ReturnType));
                            _bodyTypeParams = previousTypeParams;
                            if (_collectedReturns != null) Console.Error.WriteLine($"SUGGEST\t{ownerName}\t{m.Name}\t{m.Params.Count}\t{SuggestedType(_collectedReturns)}");
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
        /// <summary>The type of an expression that goes to a target of the type <paramref name="expected"/>: a lambda takes the types of its parameters and of its result from there.</summary>
        private StaticType Infer(Expr expr, StaticType? expected) =>
            expr is LambdaExpr lam ? InferLambda(lam, expected) : Infer(expr);

        private StaticType Infer(Expr expr)
        {
            switch (expr)
            {
                case LiteralExpr l:
                    return l.Value.Kind switch
                    {
                        ValueKind.Bool => StaticType.Bool,
                        ValueKind.Int => StaticType.Int.WithUnit(l.Value.Unit ?? Unit.Unitless),
                        ValueKind.Float => StaticType.Float.WithUnit(l.Value.Unit ?? Unit.Unitless),
                        ValueKind.Char => StaticType.Char,
                        ValueKind.String => StaticType.String,
                        ValueKind.Undefined => StaticType.Undefined,
                        _ => StaticType.Dynamic,
                    };
                case IdentifierExpr id: return Lookup(id.Name);
                case ThisExpr: return _thisType ?? (_class != null ? ThisClass() : StaticType.Dynamic);
                case InterpolatedStringExpr s:
                    foreach (var part in s.Parts) if (part is InterpolationExprPart ep) Infer(ep.Expression);
                    return StaticType.String;
                case UnaryExpr u: return InferUnary(u);
                case BinaryExpr b: return InferBinary(b);
                case RebindExpr rb: Infer(rb.Target); return Infer(rb.Lambda);
                case ConditionalExpr ce: CheckCondition(ce.Condition); return Join([Infer(ce.Then), Infer(ce.Else)]);
                case AssignExpr a: return InferAssign(a);
                case MemberExpr m: return InferMember(m);
                case IndexExpr ix: return InferIndex(ix);
                case CallExpr c: return InferCall(c);
                case NewExpr n: return InferNew(n);
                case IncDecExpr inc: return Infer(inc.Target);
                case LambdaExpr lam: return InferLambda(lam, null);
                case IsOfExpr io: Infer(io.Operand); return StaticType.Bool;
                case IsInExpr ii: Infer(ii.Operand); return StaticType.Bool;
                case IsFromExpr ifr: Infer(ifr.Operand); Infer(ifr.OwnerExpr); return StaticType.Bool;
                case UnitCoerceExpr uc:
                    {
                        // `x:mm` converts (a whole number into a coarser unit may lose its fraction: TC007), `x::mm` relabels, `x:` takes the unit of the operation (not known here)
                        var operand = Infer(uc.Operand);
                        if (!operand.IsNumber) return StaticType.Dynamic;
                        if (uc.TargetUnitName is not { } targetName) return operand.WithUnit(null);
                        var target = Unit.Parse(targetName);
                        if (!uc.Relabel && operand.Kind == StaticKind.Int && operand.Unit is { } from && !from.Equals(target) && from.IsCompatibleWith(target) && from.IsLinear && target.IsLinear && from.ConversionFactorTo(target) < 1)
                            Report(UnitLossCode, $"A whole number in '{UnitText(from)}' converted into '{UnitText(target)}' may lose its fraction (convert a float: 'x!float:{targetName}')", uc.Line);
                        return operand.WithUnit(target);
                    }
                case TypeCoerceExpr tc:
                    {
                        var operand = Infer(tc.Operand);
                        var operandUnit = operand.IsNumber ? operand.Unit : null;
                        if (tc.TargetClass is { } castType) return Declared(castType, 0) ?? StaticType.Dynamic;
                        return tc.TargetTypeKeyword switch
                        {
                            TokenType.KwInt => StaticType.Int.WithUnit(operandUnit),
                            TokenType.KwFloat => StaticType.Float.WithUnit(operandUnit),
                            TokenType.KwBool => StaticType.Bool,
                            TokenType.KwChar => StaticType.Char,
                            TokenType.KwString => StaticType.String,
                            _ => StaticType.Dynamic,
                        };
                    }
                case ArrayLiteralExpr arr: return StaticType.ArrayOf(Join(arr.Elements.Select(Infer).ToList()));
                case NewArrayExpr na:
                    {
                        foreach (var size in na.SizeExprs) if (size != null) Infer(size);
                        var element = DeclaredOrDynamic(na.ElementType);
                        for (int i = 0; i < na.SizeExprs.Count; i++) element = StaticType.ArrayOf(element);
                        return element;
                    }
                case NewBufferExpr nb: Infer(nb.SizeExpr); return StaticType.Dynamic;
                case ThrowExpr th: Infer(th.Value); return StaticType.Dynamic;
                case SyncExpr sy: Infer(sy.Target); return StaticType.Dynamic;
                case ProbeExpr pr:
                    // the handler of a probe gets the object and the value
                    Infer(pr.Target);
                    Infer(pr.Handler, StaticType.LambdaOf(2, null, new[] { StaticType.Dynamic, StaticType.Dynamic }));
                    return StaticType.Dynamic;
                case TryProcessExpr tp: Infer(tp.Target); return StaticType.Dynamic;
                case TryCallExpr tc: Infer(tc.Call); return StaticType.Dynamic;
                default: return StaticType.Dynamic;
            }
        }

        private StaticType InferUnary(UnaryExpr u)
        {
            var operand = Infer(u.Operand);
            if (u.Op == UnaryOp.LogicalNot) CheckLogicalOperand(operand, "!", u.Line);
            return u.Op switch
            {
                UnaryOp.Negate => operand.Kind is StaticKind.Int or StaticKind.Float ? operand : StaticType.Dynamic,
                UnaryOp.LogicalNot => StaticType.Bool,
                UnaryOp.BitNot => StaticType.Int,
                UnaryOp.FlatCopy or UnaryOp.DeepCopy or UnaryOp.Take => operand,
                _ => StaticType.Dynamic,
            };
        }

        private StaticType InferBinary(BinaryExpr b)
        {
            var left = Infer(b.Left);
            var right = Infer(b.Right);
            if (b.Op is BinaryOp.And or BinaryOp.Or)
            {
                string op = b.Op == BinaryOp.And ? "&&" : "||";
                CheckLogicalOperand(left, op, b.Line);
                CheckLogicalOperand(right, op, b.Line);
            }
            var type = InferBinaryType(b, left, right);
            var unit = BinaryUnit(b, left, right);
            return type.IsNumber ? type.WithUnit(unit) : type;
        }

        /// <summary>The unit of the result of a binary operation (SPEC 3.2, 3.4): `+ - %` and comparisons need the same dimension (another one is TC006) and give
        /// the finer unit of two whole numbers, otherwise the unit of the left operand; `* /` combine the units; `&amp; | #` keep it, a shift the left one.
        /// Not known when an operand's unit is not known.</summary>
        private Unit? BinaryUnit(BinaryExpr b, StaticType left, StaticType right)
        {
            if (!left.IsNumber || !right.IsNumber || left.Unit is not { } l || right.Unit is not { } r || left.UnitIsDimension || right.UnitIsDimension) return null;
            switch (b.Op)
            {
                case BinaryOp.Add or BinaryOp.Sub or BinaryOp.Mod or BinaryOp.Eq or BinaryOp.NotEq or BinaryOp.Lt or BinaryOp.LtEq or BinaryOp.Gt or BinaryOp.GtEq
                    or BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor:
                    if (!l.IsCompatibleWith(r))
                    {
                        Report(UnitCode, $"'{UnitText(l)}' and '{UnitText(r)}' cannot be combined with '{OperatorText(b.Op)}' (another dimension)", b.Line);
                        return null;
                    }
                    if (l.Equals(r)) return l;
                    return left.Kind == StaticKind.Int && right.Kind == StaticKind.Int ? (l.Scale <= r.Scale ? l : r) : l;
                case BinaryOp.Mul: return Unit.Multiply(l, r);
                case BinaryOp.Div: return Unit.Divide(l, r);
                case BinaryOp.ShiftLeft or BinaryOp.ShiftRight: return l;
                default: return null;
            }
        }

        private static string OperatorText(BinaryOp op) => op switch
        {
            BinaryOp.Add => "+", BinaryOp.Sub => "-", BinaryOp.Mod => "%", BinaryOp.Eq => "==", BinaryOp.NotEq => "!=", BinaryOp.Lt => "<", BinaryOp.LtEq => "<=",
            BinaryOp.Gt => ">", BinaryOp.GtEq => ">=", BinaryOp.BitAnd => "&", BinaryOp.BitOr => "|", BinaryOp.BitXor => "#", _ => op.ToString(),
        };

        private StaticType InferBinaryType(BinaryExpr b, StaticType left, StaticType right)
        {
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
            var target = a.Target switch
            {
                IdentifierExpr id => Lookup(id.Name),
                MemberExpr m => InferMemberTarget(m),
                IndexExpr ix => InferIndexTarget(ix),
                _ => StaticType.Dynamic,
            };
            var value = Infer(a.Value, target);
            string what = a.Target switch { IdentifierExpr id => $"'{id.Name}'", MemberExpr m => $"'{m.Name}'", IndexExpr => "an element", _ => "the target" };
            CheckAssignable(value, target, a.Line, what, valueExpr: a.Value);
            if (a.Target is IdentifierExpr local && LocalType(local.Name) is { } localType) GuardLocalValue(a.Value, value, target, localType, local.Name);
            return target.IsDynamic ? value : target;
        }

        private StaticType InferMemberTarget(MemberExpr m) => InferMember(m);

        private StaticType InferIndexTarget(IndexExpr ix)
        {
            var target = Infer(ix.Target);
            Infer(ix.Index);
            if (target.Kind == StaticKind.Array) return target.Element!;
            if (target.Kind == StaticKind.Class && FindMethod(target, "SetIndex", 2, out var map) is { } setter) return DeclaredOrDynamic(setter.Params[1].Type, setter.Params[1].ArrayRanks.Count, map);
            return StaticType.Dynamic;
        }

        /// <summary>`a[i]`: the element of an array, the char of a string, what the `operator[]` of a class returns.</summary>
        private StaticType InferIndex(IndexExpr ix)
        {
            var target = Infer(ix.Target);
            Infer(ix.Index);
            Dictionary<string, StaticType>? map = null;
            return target.Kind switch
            {
                StaticKind.Array => target.Element!,
                StaticKind.String => StaticType.Char,
                StaticKind.Class when FindMethod(target, "GetIndex", 1, out map) is { ReturnType: { } returns } => DeclaredOrDynamic(returns, 0, map),
                _ => StaticType.Dynamic,
            };
        }

        /// <summary>`a.Name`: a field or property of a class (also a static one, `Class.Name`), the length of an array or a string.</summary>
        private StaticType InferMember(MemberExpr m)
        {
            var target = Infer(m.Target);
            if (target.IsDynamic && StaticClassRef(m.Target) is { } staticClass) target = StaticType.ClassOf(staticClass);
            if (target.Kind == StaticKind.Class && FindMember(target, m.Name) == null && !HasMethodNamed(target, m.Name) && FullyKnown(target))
                Report(UnknownMemberCode, $"'{target}' has no field, property or method '{m.Name}'", m.Line);
            return target.Kind switch
            {
                StaticKind.Class when FindMember(target, m.Name) is { } member => member,
                StaticKind.Array when m.Name == "length" => StaticType.Int,
                StaticKind.String when m.Name == "Length" => StaticType.Int,
                _ => StaticType.Dynamic,
            };
        }

        /// <summary>`obj.Name(...)` that finds no method: none of that name at all (TC008), or none that takes that many arguments (TC002).</summary>
        private void ReportMissingMethod(MemberExpr callee, StaticType target, int argc)
        {
            if (target.Kind != StaticKind.Class || !FullyKnown(target) || callee.Name is "TakeLocal" or "TakeUpwards" or "TakeGlobal" or "TakeTo") return;
            if (callee.Name == "resume" && DerivesFrom(target.Class!, "Exception")) return;   // the built-in `e.resume(value)` of every exception (SPEC 7.5)
            if (FindMember(target, callee.Name) != null) return;   // (a field or property: a lambda in it, or something else the runtime decides)
            var named = Chain(target).SelectMany(c => c.Decl.Members.OfType<MethodDecl>()).Where(m => m.Name == callee.Name).ToList();
            if (named.Count == 0) Report(UnknownMemberCode, $"'{target}' has no method '{callee.Name}'", callee.Line);
            else if (!named.Any(m => Fits(m.Params, argc)))
                Report(ArgumentCode, $"'{callee.Name}' of '{target}' takes {string.Join(" or ", named.Select(m => ArityText(m.Params)).Distinct())} argument(s), not {argc}", callee.Line);
        }

        private static string ArityText(IReadOnlyList<LambdaParam> parameters)
        {
            int min = parameters.Count(p => p.DefaultValue == null);
            return min == parameters.Count ? min.ToString() : $"{min} to {parameters.Count}";
        }

        private bool HasMethodNamed(StaticType receiver, string name) =>
            Chain(receiver).Any(c => c.Decl.Members.OfType<MethodDecl>().Any(m => m.Name == name))
            || receiver.Class != null && _interfaces.TryGetValue(receiver.Class, out var iface) && iface.Methods.Any(m => m.Name == name);

        /// <summary>Whether everything a class has is known here: it is a class (not an interface) and all its base classes and interfaces are declared.</summary>
        private bool FullyKnown(StaticType receiver)
        {
            if (receiver.Class == null || !_classes.ContainsKey(receiver.Class)) return false;
            foreach (var (decl, _) in Chain(receiver))
                foreach (var b in decl.BaseRefs ?? Array.Empty<TypeRef>())
                    if (!_classes.ContainsKey(NameOf(b)) && !_interfaces.ContainsKey(NameOf(b))) return false;
            return true;
        }

        /// <summary>A lambda: its body is checked on its own (its returns are not those of the method around it). The types of its parameters and of its result come from
        /// the target it goes to (a typed parameter, field or variable: <paramref name="expected"/>), otherwise from its own declaration (`var f = (int x) => ...`);
        /// a parameter that has neither is `any` and said so (TC004). Its type has its parameters and, when all its returns agree, the type it returns.</summary>
        private StaticType InferLambda(LambdaExpr lam, StaticType? expected)
        {
            // (a lambda runs in its own scope: a type parameter of the method around it is not passed on from there)
            bool previous = _inLambda;
            _inLambda = true;
            try { return InferLambdaCore(lam, expected); }
            finally { _inLambda = previous; }
        }

        private StaticType InferLambdaCore(LambdaExpr lam, StaticType? expected)
        {
            var context = expected is { Kind: StaticKind.Lambda, Params: { } ps } && ps.Count == lam.Params.Count ? ps : null;
            var expectedReturn = expected is { Kind: StaticKind.Lambda, Return: { Kind: not StaticKind.Undefined } r } ? r : null;
            var parameterTypes = new List<StaticType>();
            for (int i = 0; i < lam.Params.Count; i++)
            {
                var p = lam.Params[i];
                var declared = Declared(p.Type, p.ArrayRanks.Count);
                if (declared != null)
                {
                    if (context != null && !Assignable(context[i], declared))
                        Report(ArgumentCode, $"Parameter '{p.Name}' of the lambda is of type '{declared}', but the lambda is called with '{context[i]}'", lam.Line);
                    parameterTypes.Add(declared);
                }
                else if (context != null) parameterTypes.Add(context[i]);
                else
                {
                    // (a target with another number of parameters is reported as such; a selector `lambda member<T>` (ParamCount -1) takes `x => x.member` as it is)
                    if (expected is not { Kind: StaticKind.Lambda })
                        Report(UntypedCode, $"Parameter '{p.Name}' of the lambda has no type and is assumed to be 'any'. Please add a type (use 'any' if every type is allowed).", lam.Line);
                    parameterTypes.Add(StaticType.Dynamic);
                }
            }

            var previousReturn = _returnType;
            var previousCollected = _collectedReturns;
            _returnType = expectedReturn;
            var returns = new List<StaticType>();
            _collectedReturns = returns;
            Push();
            for (int i = 0; i < lam.Params.Count; i++) Declare(lam.Params[i].Name, parameterTypes[i]);
            foreach (var s in lam.Body.Statements) Check(s);
            Pop();
            _returnType = previousReturn;
            _collectedReturns = previousCollected;
            var values = returns.Where(r => r.Kind != StaticKind.Undefined).ToList();
            StaticType? returned = expectedReturn ?? (values.Count == 0 ? StaticType.Undefined : Join(values) is { IsDynamic: false } joined ? joined : null);
            // (a parameter with a default value makes the number of arguments a range: not claimed)
            return StaticType.LambdaOf(lam.Params.Any(p => p.DefaultValue != null) ? -1 : lam.Params.Count, returned, parameterTypes);
        }

        /// <summary>The one type of several values (the elements of an array literal, the returns of a lambda): the same type, `float` for a mix of numbers, otherwise not known.</summary>
        private static StaticType Join(IReadOnlyList<StaticType> types)
        {
            var known = types.Where(t => t.Kind != StaticKind.Undefined).ToList();
            if (known.Count == 0 || known.Any(t => t.IsDynamic)) return StaticType.Dynamic;
            if (known.All(t => t == known[0])) return known[0];
            // numbers of the same kind with other units: the kind (the unit is not known)
            if (known.All(t => t.IsNumber && t.Kind == known[0].Kind)) return known[0].WithUnit(null);
            if (known.All(t => t.Kind is StaticKind.Int or StaticKind.Float)) return StaticType.Float;
            return StaticType.Dynamic;
        }

        private StaticType InferCall(CallExpr c)
        {
            // the callee first: the declared types of its parameters are what the arguments go to (a lambda argument takes its types from there)
            MethodDecl? method = null;
            Dictionary<string, StaticType>? map = null;
            StaticType? lambda = null;
            string name = "the lambda";
            StaticType? calleeTarget = null;
            if (c.Callee is MemberExpr m)
            {
                var target = Infer(m.Target);
                if (target.IsDynamic && StaticClassRef(m.Target) is { } staticClass) target = StaticType.ClassOf(staticClass);
                calleeTarget = target;
                if (target.Kind == StaticKind.Class)
                {
                    method = FindMethod(target, m.Name, c.Args.Count, out map);
                    if (method == null && FindMember(target, m.Name) is { Kind: StaticKind.Lambda } field) { lambda = field; name = m.Name; }
                }
            }
            else if (c.Callee is IdentifierExpr id)
            {
                if (_class != null && !IsLocal(id.Name)) method = FindMethod(ThisClass(), id.Name, c.Args.Count, out map);
                if (method == null && Lookup(id.Name) is { Kind: StaticKind.Lambda } variable) { lambda = variable; name = id.Name; }
            }
            else if (Infer(c.Callee) is { Kind: StaticKind.Lambda } callee) lambda = callee;

            // a generic method (`R Select<R>(R lambda<T> fn)`): its type parameters are taken from the arguments, one after the other (a later argument
            // - a lambda - sees what the earlier ones fixed); what is not found is `any`
            HashSet<string>? open = null;
            if (method?.TypeParams is { Count: > 0 } typeParams)
            {
                map = map == null ? new Dictionary<string, StaticType>() : new Dictionary<string, StaticType>(map);
                open = typeParams.Select(p => p.Name).ToHashSet();
                foreach (var p in open) map.Remove(p);
            }
            var args = new List<StaticType>();
            for (int i = 0; i < c.Args.Count; i++)
            {
                StaticType? expected = null;
                if (method != null && i < method.Params.Count && !method.Params[i].ByRef)
                {
                    var param = method.Params[i];
                    expected = Declared(param.Type, param.ArrayRanks.Count, open == null ? map : WithOpen(map!, open));
                    // a type parameter found to be an int so far may still become a float (`Max(2, 3.5)`): the argument is not held to int
                    if (open != null && param.Type is { TypeArgs: null } bare && open.Contains(bare.BaseName) && param.ArrayRanks.Count + bare.ArrayRank == 0
                        && map!.TryGetValue(bare.BaseName, out var soFar) && soFar.Kind == StaticKind.Int)
                        expected = null;
                    // a lambda whose result is a type parameter still to be found: its own result counts
                    if (open != null && expected is { Kind: StaticKind.Lambda } && param.Type?.LambdaSignature?.ReturnTypeName is { } r && open.Contains(r) && !map!.ContainsKey(r))
                        expected = expected with { Return = null };
                }
                else if (lambda?.Params != null && i < lambda.Params.Count) expected = lambda.Params[i];
                var arg = Infer(c.Args[i], expected);
                args.Add(arg);
                if (open != null && i < method!.Params.Count) Bind(method.Params[i].Type, method.Params[i].ArrayRanks.Count, arg, map!, open);
            }
            if (open != null)
            {
                // what was found is given to the call at run time (ObjectInstance-like: the generic method checks its parameters against it)
                _methodTypeArgs[c] = method!.TypeParams!.Select(p => map!.TryGetValue(p.Name, out var found) ? TypeArgText(found) : "").ToArray();
                // a whole number for a type parameter found to be a float becomes a float at the call (`Max(2, 3.5)`), not only in the method
                for (int i = 0; i < c.Args.Count && i < method.Params.Count; i++)
                    if (method.Params[i] is { ByRef: false, Type: { TypeArgs: null, ArrayRank: 0 } bare } p && p.ArrayRanks.Count == 0 && open.Contains(bare.BaseName)
                        && map!.TryGetValue(bare.BaseName, out var found) && found.Kind == StaticKind.Float && args[i].Kind == StaticKind.Int)
                        _unitChecks.TryAdd(c.Args[i], "#any");
                map = WithOpen(map!, open);
            }
            if (lambda != null) return CallLambda(lambda, args, c, name);
            if (method == null)
            {
                if (c.Callee is MemberExpr callee && calleeTarget != null) ReportMissingMethod(callee, calleeTarget, c.Args.Count);
                return StaticType.Dynamic;
            }
            CheckArguments(method.Params, args, c.Args, method.Name, map);
            return DeclaredOrDynamic(method.ReturnType, 0, map);
        }

        /// <summary>A type as a run-time type argument (DeclaredType text): a basic type, a class or an interface; "" for anything else.</summary>
        private static string TypeArgText(StaticType type) => type.Kind switch
        {
            StaticKind.Int or StaticKind.Float => NumberTypeArgText(type.Kind == StaticKind.Int ? "int" : "float", type),
            StaticKind.Bool => "bool", StaticKind.Char => "char", StaticKind.String => "string",
            StaticKind.Class when type.Class != null => type.Class,
            StaticKind.Dynamic when type.TypeParam != null => type.TypeParam,
            _ => "",
        };

        /// <summary>A number type argument found from a value: `int` without a unit, `int:cm` in cm, `int:any m` of a dimension, `int:any` when its unit is not known.</summary>
        private static string NumberTypeArgText(string kind, StaticType type)
        {
            if (type.Unit is not { } unit) return kind + ":any";
            if (unit.IsUnitless) return kind;
            if (DemandText(unit) is not { } demand) return kind + ":any";
            return kind + ":" + (type.UnitIsDimension ? "any " : "") + demand;
        }

        /// <summary>The map with every type parameter that was not found as `any`.</summary>
        private static Dictionary<string, StaticType> WithOpen(Dictionary<string, StaticType> map, HashSet<string> open)
        {
            var result = new Dictionary<string, StaticType>(map);
            foreach (var p in open) result.TryAdd(p, StaticType.Dynamic);
            return result;
        }

        /// <summary>Finds what the type parameters in a declared parameter type stand for from the argument that goes there (`T` of `IEnumerable<T>`
        /// from a `List<int>` or an `int[]`, `R` of `R lambda<T>` from the result of the lambda); the first finding of a parameter counts.</summary>
        private void Bind(TypeRef? type, int arrayRanks, StaticType arg, Dictionary<string, StaticType> map, HashSet<string> open)
        {
            if (type == null || arg.IsDynamic && type.TypeArgs == null) { if (type != null && open.Contains(type.BaseName) && arrayRanks + type.ArrayRank == 0) map.TryAdd(type.BaseName, arg); return; }
            int ranks = arrayRanks + type.ArrayRank;
            if (ranks > 0)
            {
                if (arg.Kind == StaticKind.Array) Bind(type with { ArrayRank = 0 }, ranks - 1, arg.Element!, map, open);
                return;
            }
            if (open.Contains(type.BaseName) && type.TypeArgs == null)
            {
                // the first argument decides; an int becomes a float when a float follows (like the arithmetic of the two)
                if (arg.Kind == StaticKind.Float && map.TryGetValue(type.BaseName, out var found) && found.Kind == StaticKind.Int) map[type.BaseName] = arg;
                else if (arg.Kind != StaticKind.Undefined) map.TryAdd(type.BaseName, arg);
                return;
            }
            if (type.BaseName == "lambda" && type.LambdaSignature is { IsSelector: false } signature && arg.Kind == StaticKind.Lambda)
            {
                if (signature.ReturnTypeName is { } r && open.Contains(r) && arg.Return is { Kind: not StaticKind.Undefined } returned) map.TryAdd(r, returned);
                if (arg.Params != null)
                    for (int i = 0; i < signature.ParamTypeNames.Count && i < arg.Params.Count; i++)
                        if (open.Contains(signature.ParamTypeNames[i]) && !arg.Params[i].IsDynamic) map.TryAdd(signature.ParamTypeNames[i], arg.Params[i]);
                return;
            }
            if (type.TypeArgs is { Count: > 0 } typeArgs)
            {
                string generic = GenericName(PlainName(type.BaseName), typeArgs.Count);
                IReadOnlyList<StaticType>? actual = null;
                if (arg.Kind == StaticKind.Array && PlainName(type.BaseName) == "IEnumerable") actual = new[] { arg.Element! };
                else if (arg.Kind == StaticKind.Class) actual = (arg.Class == generic ? arg : AsBase(arg, generic))?.Args;
                if (actual == null) return;
                for (int i = 0; i < typeArgs.Count && i < actual.Count; i++) Bind(typeArgs[i], 0, actual[i], map, open);
            }
        }

        /// <summary>`Class.Method(...)`: a (possibly namespace-qualified) class name that is not shadowed by a variable or member.</summary>
        private string? StaticClassRef(Expr target)
        {
            Expr root = target;
            while (root is MemberExpr inner) root = inner.Target;
            if (root is not IdentifierExpr first || IsLocal(first.Name) || _class != null && FindMember(ThisClass(), first.Name) != null) return null;
            string name = target is MemberExpr member ? member.Name : ((IdentifierExpr)target).Name;
            return _classes.ContainsKey(name) ? name : null;
        }

        /// <summary>The call of a lambda: the number of arguments must be the number of its parameters; the result is what it returns (when that is known).</summary>
        private StaticType CallLambda(StaticType lambda, IReadOnlyList<StaticType> args, CallExpr c, string name)
        {
            if (lambda.ParamCount >= 0 && args.Count != lambda.ParamCount)
                Report(ArgumentCode, $"'{name}' ({lambda}) takes {lambda.ParamCount} argument(s), not {args.Count}", c.Line);
            return lambda.Return is { Kind: not StaticKind.Undefined } returns ? returns : StaticType.Dynamic;
        }

        private bool IsLocal(string name)
        {
            for (int i = _scopes.Count - 1; i >= 1; i--)
                if (_scopes[i].ContainsKey(name)) return true;
            return false;
        }

        private StaticType InferNew(NewExpr n)
        {
            // `new List<int>()`: the generic class (named `Box`1` next to a non-generic `Box`) with its type arguments; `new List()` is its raw form
            var typeArgs = n.ClassRef.TypeArgs is { Count: > 0 } refs ? refs.Select(a => DeclaredOrDynamic(a)).ToList() : null;
            string plain = typeArgs != null ? GenericName(PlainName(n.ClassRef.BaseName), typeArgs.Count) : PlainName(n.ClassRef.BaseName);
            if (!_classes.TryGetValue(plain, out var decl)) { foreach (var a in n.Args) Infer(a); return StaticType.Dynamic; }
            var map = MapFor(decl.TypeParams, typeArgs);
            var ctors = decl.Members.OfType<ConstructorDecl>().Where(c => Fits(c.Params, n.Args.Count)).ToList();
            var ctor = ctors.Count == 1 ? ctors[0] : null;
            var args = n.Args.Select((a, i) => Infer(a, ctor != null && i < ctor.Params.Count && !ctor.Params[i].ByRef ? Declared(ctor.Params[i].Type, ctor.Params[i].ArrayRanks.Count, map) : null)).ToList();
            if (ctor != null) CheckArguments(ctor.Params, args, n.Args, "new " + PlainName(plain), map);
            return StaticType.ClassOf(plain, typeArgs);
        }
    }
}
