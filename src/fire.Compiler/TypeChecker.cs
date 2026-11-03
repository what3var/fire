using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Lexing;
using fire.Resolving;
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
    internal enum StaticKind { Dynamic, Bool, Int, Float, Char, String, Lambda, Undefined, Class, Object, Array, Enum }

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
        /// <summary>A value of the enum `name` (its full name): not an int (docs/language/10-enums-and-namespaces.md).</summary>
        public static StaticType EnumOf(string name) => new(StaticKind.Enum, name);
        public static StaticType LambdaOf(int paramCount, StaticType? returns, IReadOnlyList<StaticType>? parameters = null) => new(StaticKind.Lambda, ParamCount: paramCount, Return: returns, Params: parameters);
        public bool IsDynamic => Kind == StaticKind.Dynamic;
        public override string ToString() => Kind switch
        {
            StaticKind.Class => GenericClassNames.PlainName(Class!) + (Args is { Count: > 0 } ? "<" + string.Join(", ", Args) + ">" : ""),
            StaticKind.Object => "class",
            StaticKind.Enum => Class!,
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
        public const string SwitchMissingCode = "TC010";
        /// <summary>A named argument (`f(size: 2)`) that no parameter has, or a parameter that gets no value with named arguments - an error in lax mode too.</summary>
        public const string NamedArgumentCode = "TC011";
        /// <summary>A private or protected member that the code may not use (on a receiver whose class is known) - an error in lax mode too: the access
        /// would certainly throw an AccessDeniedException at run time.</summary>
        public const string AccessCode = "TC012";
        /// <summary>A cast `x!Dog` that can never succeed: the static type of `x` and the target are unrelated classes (or a class and an interface no class of
        /// the program joins).</summary>
        public const string ImpossibleCastCode = "TC013";
        /// <summary>A method, property or operator that replaces one of a base class without `override` (a warning in lax mode).</summary>
        public const string MissingOverrideCode = "TC014";
        /// <summary>`override` on a member that replaces nothing (a mistyped name or other parameters) - an error in lax mode too.</summary>
        public const string NothingToOverrideCode = "TC015";

        private readonly IReadOnlyDictionary<string, ClassDecl> _classes;
        private readonly Dictionary<string, InterfaceDecl> _interfaces = new();
        private readonly Dictionary<Expr, string> _unitChecks;
        private readonly Dictionary<Expr, string[]> _methodTypeArgs;
        private readonly Dictionary<Expr, (TypeRef Type, string What)> _localGuards;
        private readonly Dictionary<Expr, string> _overloadHints;
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
        /// <summary>Inside a lambda with `on target`: `this` is the bound object (its class is not known), the access to its members is checked at run time.</summary>
        private bool _inBoundLambda;
        /// <summary>The type of `this` in a `class extends string/array/...` (null: the class being checked decides).</summary>
        private StaticType? _thisType;
        private StaticType? _returnType;
        /// <summary>The declared return type of the method (or getter) being checked and what a failed check of a return value calls it - a value whose
        /// type is not proven is checked at run time (LocalGuards), so the declared type of a call holds for its result.</summary>
        private TypeRef? _returnTypeRef;
        private string _returnWhat = "";
        private int _source;
        /// <summary>The types of the `return` statements of the untyped method that is being checked (null: not collecting; a lambda inside does not count).</summary>
        private List<StaticType>? _collectedReturns;
        private static readonly bool SuggestReturnTypes = Environment.GetEnvironmentVariable("FIRE_TYPECHECK_SUGGEST") == "1";

        /// <summary>What the resolver found for the names (`Color.Red`: a member of an enum, with its type).</summary>
        private IReadOnlyDictionary<Expr, ResolvedRef> _refs = new Dictionary<Expr, ResolvedRef>();
        /// <summary>The enums of the program: full name -> a flag enum.</summary>
        private readonly Dictionary<string, bool> _enumNames = new(StringComparer.Ordinal);

        private TypeChecker(IReadOnlyDictionary<string, ClassDecl> classes, TypeCheckMode defaultMode, int firstUserSource, Dictionary<Expr, string> unitChecks,
            Dictionary<Expr, string[]> methodTypeArgs, Dictionary<Expr, (TypeRef Type, string What)> localGuards, Dictionary<Expr, string> overloadHints)
        {
            _localGuards = localGuards;
            _overloadHints = overloadHints;
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
            var checker = new TypeChecker(resolved.Classes, defaultMode, firstUserSource, resolved.UnitChecks, resolved.MethodTypeArgs, resolved.LocalGuards, resolved.OverloadHints);
            foreach (var stmt in program)
                if (stmt is TypeCheckDirective d) checker._strictBySource[d.Source] = d.Strict;
            checker.CollectInterfaces(program);
            checker._refs = resolved.References;
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
                else if (stmt is EnumDecl e) _enumNames[e.Name] = e.IsFlags;
                else if (stmt is NamespaceDecl n) CollectInterfaces(n.Members);
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // Reporting
        // -------------------------------------------------------------------------------------------------------------
        private bool Strict => _strictBySource.TryGetValue(_source, out bool strict) ? strict : _defaultMode == TypeCheckMode.Strict;

        private void Report(string code, string message, int line) =>
            _diagnostics.Add(new Diagnostic(line, message, Strict && code is not (UnitLossCode or AssignInConditionCode or SwitchMissingCode) ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning, code));

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
            // an array into an array with a unit (`float:km/h speeds[] = [...]`): its elements (converted at runtime, CheckUnit takes them one by one)
            if (value.Kind == StaticKind.Array && target.Kind == StaticKind.Array && value.Element is { } valueElement && target.Element is { } targetElement)
            {
                CheckUnitAssignable(valueElement, targetElement, line, what == "an element" ? what : $"an element of {what}");
                return;
            }
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
            // a unit that becomes a plain number (dB) and a plain number go into each other (by its functions, at runtime)
            if (from.IsUnitless && to.UnitlessConverter != null || to.IsUnitless && from.UnitlessConverter != null) return;
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
            // an enum is not an int (and an int not an enum): `Color c = 5`, `int i = c` want a `!` (`5!Color`, `c!int`)
            if (value.Kind == StaticKind.Enum || target.Kind == StaticKind.Enum) return value.Kind == target.Kind && value.Class == target.Class;
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

        /// <summary>`x!Dog` (a cast to a class or an interface): reported (TC013) when no object can be both the static type of `x` and the target - two classes
        /// of which neither derives from the other (a class has one base class, so they have no common subclass), or a class and an interface that neither
        /// the class nor any class deriving from it implements. Two interfaces, `class`, `any` and anything not fully known pass. (Casts of numbers, chars
        /// and bools keep their own rules.)</summary>
        private void CheckCast(StaticType operand, StaticType target, int line)
        {
            if (operand.Kind != StaticKind.Class || target.Kind != StaticKind.Class || CastPossible(operand.Class!, target.Class!)) return;
            Report(ImpossibleCastCode, $"A value of type '{operand}' can never be a '{target}': the cast '!{target}' always fails at run time (only undefined passes)", line);
        }

        private bool CastPossible(string from, string to)
        {
            bool fromInterface = _interfaces.ContainsKey(PlainName(from)) && !_classes.ContainsKey(from);
            bool toInterface = _interfaces.ContainsKey(PlainName(to)) && !_classes.ContainsKey(to);
            if (fromInterface && toInterface) return true;                  // (a class may implement both)
            if (!fromInterface && !_classes.ContainsKey(from) || !toInterface && !_classes.ContainsKey(to)) return true;
            if (!fromInterface && !toInterface) return DerivesFrom(from, to) || DerivesFrom(to, from);
            // a class and an interface: the class itself or one of the classes deriving from it must implement it
            string cls = fromInterface ? to : from, iface = PlainName(fromInterface ? from : to);
            foreach (var name in _classes.Keys)
                if (DerivesFrom(name, cls) && MayImplement(name, iface)) return true;
            return false;
        }

        /// <summary>Does the class (or a base class of it) name the interface - or a base that is not known (then it may)?</summary>
        private bool MayImplement(string className, string iface)
        {
            string? name = className;
            for (int d = 0; d < 64 && name != null; d++)
            {
                if (!_classes.TryGetValue(name, out var decl)) return true;
                foreach (var b in decl.BaseRefs ?? Array.Empty<TypeRef>())
                {
                    string plain = PlainName(b.BaseName);
                    if (plain == iface || !_classes.ContainsKey(NameOf(b)) && !_interfaces.ContainsKey(plain)) return true;
                }
                name = decl.BaseRefs?.Select(b => NameOf(b)).FirstOrDefault(n => _classes.ContainsKey(n));
            }
            return name != null;    // (the end of the chain: it does not; a chain too deep to follow: it may)
        }

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
            // (an array with a unit, `float:km/h speeds[]`, has elements in that unit; one without says nothing about its elements)
            if (result.IsNumber && (arrayRanks + type.ArrayRank == 0 || type.Unit != null) && !(substituted && type.Unit == null))
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
            if (EnumNamed(name) is { } enumName) return StaticType.EnumOf(enumName);
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

        /// <summary>Like GuardLocalValue, for another declared target (a return value): `what` names it in the message.</summary>
        private void GuardValue(Expr valueExpr, StaticType value, StaticType target, TypeRef type, string what)
        {
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
            if (needed) _localGuards[valueExpr] = (type, what);
        }
        private void Declare(string name, StaticType type)
        {
            _scopes[^1][name] = type;
            if (_localTypes.Count == _scopes.Count) _localTypes[^1].Remove(name);   // (a typed local again sets its declaration: DeclareLocalType)
        }

        private StaticType Lookup(string name, int line = 0)
        {
            for (int i = _scopes.Count - 1; i >= 0; i--)
                if (_scopes[i].TryGetValue(name, out var type)) return type;
            if (_class != null && FindMember(ThisClass(), name) is { } member)
            {
                if (MemberLog != null && ThisClass().Class is { } cls) MemberLog.Add((_source, line, cls, name, true));
                return member;
            }
            return StaticType.Dynamic;
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

        /// <summary>Every method `name` of the receiver that takes `argc` arguments, in the order the run time prefers them for a tie (the own class first;
        /// a method of a base class that one of a derived class overrides - same parameter count, the same or untyped parameter types - left out).</summary>
        private List<(MethodDecl Method, Dictionary<string, StaticType>? Map)> FindMethods(StaticType receiver, string name, int argc)
        {
            var found = new List<(MethodDecl, Dictionary<string, StaticType>?)>();
            if (receiver.Class != null && _interfaces.TryGetValue(receiver.Class, out var iface))
            {
                var map = MapFor(iface.TypeParams, receiver.Args);
                foreach (var sig in iface.Methods.Where(m => m.Name == name && Fits(m.Params, argc)))
                    found.Add((new MethodDecl(0, sig.Line, sig.ReturnType, sig.Name, sig.Params, new Stmt.BlockStmt(0, sig.Line, Array.Empty<Stmt>())), map));
                return found;
            }
            var seen = new List<MethodDecl>();
            foreach (var (decl, classMap) in Chain(receiver))
            {
                var named = decl.Members.OfType<MethodDecl>().Where(m => m.Name == name).ToList();
                var fitting = named.Where(m => Fits(m.Params, argc) && !seen.Any(d => OverridesStatic(d, m)))
                    .OrderBy(m => m.Params.Count == argc ? 0 : 1).ThenBy(m => m.Params.Count);
                foreach (var m in fitting) found.Add((m, classMap));
                seen.AddRange(named);
            }
            return found;
        }

        /// <summary>The type of a parameter as overloading compares it: "" for one that takes everything (untyped, `any`, `ref`, a type parameter).</summary>
        private static string OverloadKey(LambdaParam p, MethodDecl m)
        {
            var t = p.Type;
            if (p.ByRef || t == null || t.IsInferred || t.BaseName == "any") return "";
            if (m.TypeParams?.Any(tp => tp.Name == t.BaseName) == true && t.TypeArgs == null) return "";
            if (p.ArrayRanks.Count > 0 || t.ArrayRank > 0) return "array";
            return t.BaseName == "lambda" ? "lambda" : t.BaseName;
        }

        private static bool OverridesStatic(MethodDecl derived, MethodDecl baseMethod) =>
            derived.Params.Count == baseMethod.Params.Count
            && derived.Params.Zip(baseMethod.Params).All(p => OverloadKey(p.First, derived) is var a && OverloadKey(p.Second, baseMethod) is var b && (a == b || a == "" || b == ""));

        private const int NoMatch = -1;
        private const int Unknown = -2;

        /// <summary>How closely an argument of this static type fits the parameter (like Bytecode.Overloads at run time); Unknown when only the value can
        /// tell (a dynamic argument, or a class argument whose subclasses another candidate takes more closely).</summary>
        private int StaticScore(StaticType arg, LambdaParam p, MethodDecl m, Dictionary<string, StaticType>? map)
        {
            if (p.ByRef) return Bytecode.Overloads.AnyScore;
            string key = OverloadKey(p, m);
            if (key == "")
            {
                bool generic = p.Type != null && m.TypeParams?.Any(tp => tp.Name == p.Type.BaseName) == true || p.Type != null && map?.ContainsKey(p.Type.BaseName) == true;
                return arg.Kind == StaticKind.Undefined ? Bytecode.Overloads.UndefinedScore : generic ? Bytecode.Overloads.GenericScore : Bytecode.Overloads.AnyScore;
            }
            if (arg.Kind == StaticKind.Undefined) return Bytecode.Overloads.UndefinedScore;
            if (arg.IsDynamic) return Unknown;
            var declared = Declared(p.Type, p.ArrayRanks.Count, map);
            if (declared == null || declared.IsDynamic) return arg.Kind == StaticKind.Undefined ? Bytecode.Overloads.UndefinedScore : Bytecode.Overloads.GenericScore;
            switch (declared.Kind)
            {
                case StaticKind.Int: case StaticKind.Bool: case StaticKind.Char: case StaticKind.String: case StaticKind.Lambda: case StaticKind.Array:
                    return arg.Kind == declared.Kind ? Bytecode.Overloads.ExactKind : NoMatch;
                case StaticKind.Float:
                    return arg.Kind == StaticKind.Float ? Bytecode.Overloads.ExactKind : arg.Kind == StaticKind.Int ? Bytecode.Overloads.IntToFloat : NoMatch;
                case StaticKind.Object:
                    return arg.Kind is StaticKind.Class or StaticKind.Object ? Bytecode.Overloads.ObjectScore : NoMatch;
                case StaticKind.Class:
                    if (arg.Kind == StaticKind.Object) return Unknown;
                    if (arg.Kind == StaticKind.Array && declared.Class == "IEnumerable") return Bytecode.Overloads.InterfaceScore;
                    if (arg.Kind != StaticKind.Class || arg.Class == null || declared.Class == null) return NoMatch;
                    if (_interfaces.ContainsKey(declared.Class)) return Implements(arg.Class, declared.Class) ? Bytecode.Overloads.InterfaceScore : _interfaces.ContainsKey(arg.Class) ? Unknown : NoMatch;
                    int distance = ClassDistance(arg.Class, declared.Class);
                    if (distance >= 0) return Bytecode.Overloads.ClassExact - distance;
                    // a value declared as a base class may be an object of the parameter's class at run time
                    return ClassDistance(declared.Class, arg.Class) >= 0 || _interfaces.ContainsKey(arg.Class) ? Unknown : NoMatch;
            }
            return Bytecode.Overloads.AnyScore;
        }

        /// <summary>How many steps up from `derived` the class `baseName` is (0: the same), -1 when it is no base class of it.</summary>
        private int ClassDistance(string derived, string baseName)
        {
            string? name = derived;
            for (int d = 0; d < 64 && name != null; d++)
            {
                if (name == baseName) return d;
                if (!_classes.TryGetValue(name, out var decl)) return -1;
                name = decl.BaseRefs?.Select(b => NameOf(b)).FirstOrDefault(n => _classes.ContainsKey(n));
            }
            return -1;
        }

        /// <summary>Does the class (or one of its base classes) implement the interface?</summary>
        private bool Implements(string className, string iface)
        {
            string? name = className;
            for (int d = 0; d < 64 && name != null && _classes.TryGetValue(name, out var decl); d++)
            {
                if (decl.BaseRefs?.Any(b => NameOf(b) == iface) == true) return true;
                name = decl.BaseRefs?.Select(b => NameOf(b)).FirstOrDefault(n => _classes.ContainsKey(n));
            }
            return false;
        }

        /// <summary>The overload the run time takes for arguments of these static types; null when only the values can tell (or none fits).</summary>
        private (MethodDecl Method, Dictionary<string, StaticType>? Map)? PickOverload(List<(MethodDecl Method, Dictionary<string, StaticType>? Map)> candidates, IReadOnlyList<StaticType> args)
        {
            int best = -1;
            long bestScore = long.MinValue;
            bool unknown = false;
            for (int c = 0; c < candidates.Count; c++)
            {
                var (m, map) = candidates[c];
                long total = 0;
                bool fits = true;
                for (int i = 0; i < args.Count && i < m.Params.Count; i++)
                {
                    int score = StaticScore(args[i], m.Params[i], m, map);
                    if (score == NoMatch) { fits = false; break; }
                    if (score == Unknown) { unknown = true; fits = false; break; }
                    total += score;
                }
                if (fits && total > bestScore) { best = c; bestScore = total; }
            }
            if (unknown || best < 0) return null;
            return candidates[best];
        }


        /// <summary>The OverloadHint of a call whose overload the static types decide, or null when the run time has to decide after all: an argument the
        /// overloads differ on is of an unknown type, a parameter is a type parameter whose type argument or `where` the value has to meet, or (for a
        /// call on an object) a class derived from the receiver's adds an overload of its own.</summary>
        private string? OverloadHintFor(MethodDecl method, List<(MethodDecl Method, Dictionary<string, StaticType>? Map)> candidates, IReadOnlyList<StaticType> args,
            string receiverClass, bool virtualCall)
        {
            if (_interfaces.ContainsKey(receiverClass)) return null;
            var declaring = _classes.FirstOrDefault(d => d.Value.Members.Any(x => ReferenceEquals(x, method)));
            if (declaring.Value == null) return null;
            if (virtualCall && !DerivedKeepOverloads(receiverClass, method.Name, args.Count, candidates.Select(x => x.Method).ToList())) return null;
            var assumptions = Assumptions(candidates.Select(x => (x.Method, declaring.Value)).ToList(), args);
            if (assumptions == null) return null;
            int ordinal = declaring.Value.Members.OfType<MethodDecl>().Where(x => x.Name == method.Name).TakeWhile(x => !ReferenceEquals(x, method)).Count();
            return Bytecode.OverloadHint.Format(declaring.Key, method.Name, ordinal, method.Params.Count, args.Count, assumptions);
        }

        private string? ConstructorHint(ClassDecl decl, string className, ConstructorDecl chosen, List<MethodDecl> asMethods, MethodDecl picked,
            IReadOnlyList<StaticType> args, Dictionary<string, StaticType>? map)
        {
            var assumptions = Assumptions(asMethods.Select(m => (m, decl)).ToList(), args);
            if (assumptions == null) return null;
            int ordinal = decl.Members.OfType<ConstructorDecl>().Where(k => k.Params.Count == chosen.Params.Count).TakeWhile(k => !ReferenceEquals(k, chosen)).Count();
            return Bytecode.OverloadHint.Format(className, Bytecode.OverloadHint.ConstructorName, ordinal, chosen.Params.Count, args.Count, assumptions);
        }

        /// <summary>What the choice assumes of each argument (Bytecode.Overloads.Holds): its kind or class as the type checker knows it; "" for an argument
        /// all overloads take untyped. Null when the choice depends on more than that (an unknown type where an overload is typed, a type parameter of
        /// the class, or one with `where` conditions).</summary>
        private string[]? Assumptions(List<(MethodDecl Method, ClassDecl Owner)> candidates, IReadOnlyList<StaticType> args)
        {
            var result = new string[args.Count];
            for (int i = 0; i < args.Count; i++)
            {
                bool allLoose = true;
                foreach (var (m, owner) in candidates)
                {
                    if (i >= m.Params.Count) continue;
                    var p = m.Params[i];
                    if (p.ByRef || p.Type == null || p.Type.IsInferred || p.Type.BaseName == "any") continue;
                    // a type parameter: of the class (its type argument decides at run time) or of the method with `where` conditions - not decided by the kind
                    if (owner.TypeParams?.Any(tp => tp.Name == p.Type.BaseName) == true) return null;
                    if (m.TypeParams?.FirstOrDefault(tp => tp.Name == p.Type.BaseName) is { } tp)
                    {
                        if (tp.ConstraintGroups.Count > 0) return null;
                        continue;
                    }
                    allLoose = false;
                }
                var a = args[i];
                result[i] = a.Kind switch
                {
                    StaticKind.Undefined => "u",
                    StaticKind.Int => "k:int",
                    StaticKind.Float => "k:float",
                    StaticKind.Bool => "k:bool",
                    StaticKind.Char => "k:char",
                    StaticKind.String => "k:string",
                    StaticKind.Lambda => "k:lambda",
                    StaticKind.Array => "k:array",
                    StaticKind.Object => "k:class",
                    StaticKind.Class when a.Class != null => (_interfaces.ContainsKey(a.Class) ? "i:" : "c:") + a.Class,
                    _ => allLoose ? "" : null!,
                };
                if (result[i] == null) return null;
            }
            return result;
        }

        /// <summary>Does no class derived from `cls` declare a method `name` for `argc` arguments other than one with the very parameter types of a
        /// candidate (an override that keeps the choice)? Otherwise an object of such a class may take another overload.</summary>
        private bool DerivedKeepOverloads(string cls, string name, int argc, List<MethodDecl> candidates)
        {
            foreach (var (subName, sub) in _classes)
            {
                if (subName == cls || ClassDistance(subName, cls) <= 0) continue;
                foreach (var m in sub.Members.OfType<MethodDecl>())
                    if (m.Name == name && Fits(m.Params, argc) && !candidates.Any(c => c.Params.Count == m.Params.Count
                            && c.Params.Zip(m.Params).All(p => OverloadKey(p.First, c) == OverloadKey(p.Second, m))))
                        return false;
            }
            return true;
        }

        private static bool Fits(IReadOnlyList<LambdaParam> parameters, int argc) =>
            argc <= parameters.Count && argc >= parameters.Count(p => p.DefaultValue == null);

        private void CheckArguments(IReadOnlyList<LambdaParam?> parameters, IReadOnlyList<StaticType> args, IReadOnlyList<Expr> argExprs, string callee,
            IReadOnlyDictionary<string, StaticType>? map = null)
        {
            for (int i = 0; i < args.Count && i < parameters.Count; i++)
            {
                if (parameters[i] is not { ByRef: false } parameter) continue;
                var declared = Declared(parameter.Type, parameter.ArrayRanks.Count, map);
                if (declared is { } target) CheckAssignable(args[i], target, argExprs[i].Line, $"parameter '{parameter.Name}' of '{callee}'", ArgumentCode, argExprs[i]);
            }
        }

        /// <summary>The parameter each argument of a call goes to: the positional ones in order, a named one (`size: 2`) to the parameter of its name
        /// (docs/language/08-classes.md). With names, a name that no parameter has and a parameter without a value and without a default value are errors
        /// (TC011, in lax mode too); null for an argument without a parameter.</summary>
        private List<LambdaParam?> ArgParams(IReadOnlyList<LambdaParam> parameters, IReadOnlyList<string?>? names, int argCount, string callee, int line)
        {
            var result = new List<LambdaParam?>(argCount);
            for (int i = 0; i < argCount; i++)
            {
                string? name = names != null && i < names.Count ? names[i] : null;
                if (name == null) { result.Add(i < parameters.Count ? parameters[i] : null); continue; }
                var param = parameters.FirstOrDefault(p => p.Name == name);
                if (param == null) ReportError(NamedArgumentCode, $"'{callee}' has no parameter '{name}'", line);
                result.Add(param);
            }
            if (names != null)
                foreach (var p in parameters)
                    if (!result.Contains(p) && p.DefaultValue == null)
                        ReportError(NamedArgumentCode, $"The parameter '{p.Name}' of '{callee}' gets no value (it has no default value)", line);
            return result;
        }

        private void ReportError(string code, string message, int line) => _diagnostics.Add(new Diagnostic(line, message, DiagnosticSeverity.Error, code));

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
                        if (_returnType is { } expected)
                        {
                            CheckAssignable(type, expected, r.Line, "the return value", ReturnCode, r.Value);
                            if (_returnTypeRef != null) GuardValue(r.Value, type, expected, _returnTypeRef, _returnWhat);
                        }
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
            if (init.Kind == StaticKind.Enum && Parser.Switches.TryGetValue(v, out var caseValues)) CheckSwitchOverEnum(init.Class!, caseValues, v.Line);
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
            // a class without a constructor of its own calls the one of its base class without arguments
            if (!decl.Members.OfType<ConstructorDecl>().Any() && BaseClassName() is { } baseClass) CheckConstructorAccess(baseClass, 0, decl.Line);
            CheckOverrides(decl);
            CheckMembers(decl.Members, decl.Name);
            _class = previousClass;
        }

        // -------------------------------------------------------------------------------------------------------------
        // `override` (SPEC 5.1): a member that replaces one of a base class says so; one that says so replaces something
        // -------------------------------------------------------------------------------------------------------------
        /// <summary>The base classes of a class, the nearest first (interfaces are not followed: implementing an interface member needs no `override`).</summary>
        private IEnumerable<ClassDecl> BaseClassesOf(ClassDecl decl)
        {
            var current = decl;
            for (int guard = 0; guard < 64; guard++)
            {
                var baseRef = current.BaseRefs?.FirstOrDefault(b => _classes.ContainsKey(NameOf(b)));
                if (baseRef == null || !_classes.TryGetValue(NameOf(baseRef), out var next) || ReferenceEquals(next, decl)) yield break;
                yield return next;
                current = next;
            }
        }

        /// <summary>A method (`m`, a member of `owner`) replaces `baseMethod` (of `baseOwner`) - the rule of the VM (Bytecode.Overloads.Overrides): the same number
        /// of parameters, and per parameter the same type, or an untyped one / a type parameter on either side.</summary>
        private bool Replaces(MethodDecl m, ClassDecl owner, MethodDecl baseMethod, ClassDecl baseOwner)
        {
            if (baseMethod.IsStatic || baseMethod.Name != m.Name || baseMethod.Params.Count != m.Params.Count) return false;
            for (int i = 0; i < m.Params.Count; i++)
            {
                string a = OverloadDescriptor(m.Params[i], owner, m.TypeParams), b = OverloadDescriptor(baseMethod.Params[i], baseOwner, baseMethod.TypeParams);
                if (a == b || a.Length == 0 || a[0] == 'g' || b.Length == 0 || b[0] == 'g') continue;
                return false;
            }
            return true;
        }

        /// <summary>What overloading by type makes of a parameter (like Compiler.OverloadTypeOf): "" untyped, "g" a type parameter, "k:int", "c:Class", "i:Interface", ...</summary>
        private string OverloadDescriptor(LambdaParam p, ClassDecl owner, IReadOnlyList<TypeParam>? methodTypeParams)
        {
            var t = p.Type;
            if (p.ByRef) return "";
            if (p.ArrayRanks.Count > 0 || t is { ArrayRank: > 0 }) return "k:array";
            if (t == null || t.IsInferred || t.PointerDepth > 0) return "";
            if (t.LambdaSignature != null || t.BaseName == "lambda") return "k:lambda";
            if (methodTypeParams?.Any(tp => tp.Name == t.BaseName) == true || owner.TypeParams?.Any(tp => tp.Name == t.BaseName) == true) return "g";
            switch (t.BaseName)
            {
                case "int": return "k:int";
                case "float": return "k:float";
                case "bool": return "k:bool";
                case "char": return "k:char";
                case "string": return "k:string";
                case "class": return "k:class";
                case "any": return "";
            }
            if (_enumNames.ContainsKey(t.BaseName)) return "k:int";
            bool Known(string n) => _classes.ContainsKey(n) || _interfaces.ContainsKey(n);
            string name = t.TypeArgs is { Count: > 0 } args ? GenericClassNames.ResolveNewTarget(t, args.Count, Known) : t.ResolveBaseName(Known);
            if (_enumNames.ContainsKey(name)) return "k:int";
            if (_interfaces.ContainsKey(name)) return "i:" + name;
            if (_classes.ContainsKey(name)) return "c:" + name;
            return "";
        }

        /// <summary>A method, a property or an operator that replaces an inherited one must say `override` (TC014, a warning in lax mode); `override` on one that
        /// replaces nothing is an error (TC015, also in lax mode) - it finds a mistyped name or other parameters.</summary>
        private void CheckOverrides(ClassDecl decl)
        {
            var bases = BaseClassesOf(decl).ToList();
            string plain = PlainName(decl.Name);
            foreach (var member in decl.Members)
            {
                switch (member)
                {
                    case MethodDecl { IsStatic: false } m:
                    {
                        bool isOperator = m.Name.StartsWith("operator", StringComparison.Ordinal) || m.Name is "GetIndex" or "SetIndex";
                        string what = isOperator ? $"The operator '{OperatorText(m.Name)}'" : $"The method '{m.Name}'";
                        var replaced = bases.FirstOrDefault(b => b.Members.OfType<MethodDecl>().Any(bm => Replaces(m, decl, bm, b)));
                        if (replaced != null && !m.IsOverride)
                            Report(MissingOverrideCode, $"{what} of '{plain}' replaces the one of its base class '{PlainName(replaced.Name)}' - mark it 'override' ({(m.Access == AccessModifier.Public ? "" : AccessText(m.Access) + " ")}override {(m.ReturnType != null && !m.ReturnType.IsInferred ? TypeText(m.ReturnType) + " " : "")}{(isOperator ? "operator" + OperatorText(m.Name) : m.Name)}({ParamsText(m.Params)}))", m.Line);
                        else if (replaced == null && m.IsOverride)
                        {
                            bool sameName = bases.Any(b => b.Members.OfType<MethodDecl>().Any(bm => !bm.IsStatic && bm.Name == m.Name));
                            bool inInterface = !sameName && InterfaceHasMethod(decl, m.Name);
                            ReportError(NothingToOverrideCode, $"{what} of '{plain}' is marked 'override', but no base class of '{plain}' has {(isOperator ? "this operator" : $"a method '{m.Name}'")}"
                                + (sameName ? " with these parameters" : "") + (inInterface ? " (a method of an interface is implemented without 'override')" : "") + " - nothing is overridden", m.Line);
                        }
                        break;
                    }
                    case PropertyDecl { IsStatic: false } p:
                    {
                        var replaced = bases.FirstOrDefault(b => b.Members.OfType<PropertyDecl>().Any(bp => !bp.IsStatic && bp.Name == p.Name));
                        if (replaced != null && !p.IsOverride)
                            Report(MissingOverrideCode, $"The property '{p.Name}' of '{plain}' replaces the one of its base class '{PlainName(replaced.Name)}' - mark it 'override' (override {(p.Type != null && !p.Type.IsInferred ? TypeText(p.Type) + " " : "")}{p.Name} {{ ... }})", p.Line);
                        else if (replaced == null && p.IsOverride)
                            ReportError(NothingToOverrideCode, $"The property '{p.Name}' of '{plain}' is marked 'override', but no base class of '{plain}' has a property '{p.Name}' - nothing is overridden", p.Line);
                        break;
                    }
                }
            }
        }

        private bool InterfaceHasMethod(ClassDecl decl, string name)
        {
            foreach (var c in BaseClassesOf(decl).Prepend(decl))
                foreach (var b in c.BaseRefs ?? Array.Empty<TypeRef>())
                    if (_interfaces.TryGetValue(NameOf(b), out var iface) && iface.Methods.Any(m => m.Name == name)) return true;
            return false;
        }

        /// <summary>`operator+` -> `+`, the index operator (`GetIndex`/`SetIndex`) -> `[]`.</summary>
        private static string OperatorText(string name) => name is "GetIndex" or "SetIndex" ? "[]" : name.StartsWith("operator", StringComparison.Ordinal) ? name.Substring("operator".Length) : name;

        private static string ParamsText(IReadOnlyList<LambdaParam> parameters) =>
            string.Join(", ", parameters.Select(p => (p.ByRef ? "ref " : "") + (p.Type != null && !p.Type.IsInferred ? TypeText(p.Type) + " " : "") + p.Name + string.Concat(Enumerable.Repeat("[]", p.ArrayRanks.Count))));

        private static string TypeText(TypeRef t) => (t.TypeArgs is { Count: > 0 } args ? PlainName(t.BaseName) + "<" + string.Join(", ", args.Select(TypeText)) + ">" : PlainName(t.BaseName))
            + string.Concat(Enumerable.Repeat("[]", t.ArrayRank));

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
                            CheckBody(m.Params, m.Body, untyped ? null : Declared(m.ReturnType), untyped ? null : m.ReturnType, $"Return value of '{m.Name}'");
                            _bodyTypeParams = previousTypeParams;
                            if (_collectedReturns != null) Console.Error.WriteLine($"SUGGEST\t{ownerName}\t{m.Name}\t{m.Params.Count}\t{SuggestedType(_collectedReturns)}");
                            _collectedReturns = previousCollected;
                            break;
                        }
                    case ConstructorDecl c:
                        CheckParametersTyped(c.Params, "construct", c.Line);
                        if (BaseClassName() is { } baseOfCtor) CheckConstructorAccess(baseOfCtor, c.BaseArgs?.Count ?? 0, c.Line);
                        CheckBody(c.Params, c.Body, null);
                        break;
                    case DestructorDecl d:
                        CheckBody(Array.Empty<LambdaParam>(), d.Body, null);
                        break;
                    case PropertyDecl p:
                        if (p.Getter != null) CheckBody(Array.Empty<LambdaParam>(), p.Getter, Declared(p.Type), p.Type, $"Property '{p.Name}'");
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

        private void CheckBody(IReadOnlyList<LambdaParam> parameters, Stmt.BlockStmt body, StaticType? returnType, TypeRef? returnTypeRef = null, string what = "")
        {
            var previousReturn = _returnType;
            var (previousRef, previousWhat) = (_returnTypeRef, _returnWhat);
            _returnType = returnType;
            (_returnTypeRef, _returnWhat) = (returnTypeRef, what);
            Push();
            foreach (var p in parameters) Declare(p.Name, DeclaredOrDynamic(p.Type, p.ArrayRanks.Count));
            foreach (var s in body.Statements) Check(s);
            Pop();
            _returnType = previousReturn;
            (_returnTypeRef, _returnWhat) = (previousRef, previousWhat);
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
                case IdentifierExpr id:
                    CheckImplicitThisAccess(id, MemberUse.Read);
                    return Lookup(id.Name, id.Line);
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
                case RangeExpr range:
                    {
                        // `1..5`: whole numbers in the unit of the start
                        var from = Infer(range.From, StaticType.Int);
                        var to = Infer(range.To, StaticType.Int);
                        foreach (var (bound, boundExpr) in new[] { (from, range.From), (to, range.To) })
                            if (!bound.IsDynamic && bound.Kind != StaticKind.Int)
                                Report(AssignCode, $"A range goes from a whole number to a whole number, not '{bound}'", boundExpr.Line);
                        return StaticType.ArrayOf(StaticType.Int.WithUnit(from.Kind == StaticKind.Int ? from.Unit : null));
                    }
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
                        if (tc.TargetClass is { } castType)
                        {
                            var castTarget = Declared(castType, 0) ?? StaticType.Dynamic;
                            CheckCast(operand, castTarget, tc.Line);
                            return castTarget;
                        }
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
                        // (the elements are undefined: they take the unit of a declaration with a unit, `float:m grid[][] = new float[2][2]`)
                        if (element.IsNumber && na.ElementType?.Unit == null) element = element.WithUnit(null);
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
            // `|`, `&`, `#` of two values of the same flag enum: that enum; any other operation on an enum value works on its number (an int)
            if (b.Op is BinaryOp.BitAnd or BinaryOp.BitOr or BinaryOp.BitXor && left.Kind == StaticKind.Enum && left == right && _enumNames.GetValueOrDefault(left.Class!)) return left;
            if (b.Op is BinaryOp.Eq or BinaryOp.NotEq or BinaryOp.Lt or BinaryOp.LtEq or BinaryOp.Gt or BinaryOp.GtEq && (left.Kind == StaticKind.Enum || right.Kind == StaticKind.Enum)) return StaticType.Bool;
            if (left.Kind == StaticKind.Enum) left = StaticType.Int;
            if (right.Kind == StaticKind.Enum) right = StaticType.Int;
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
                IdentifierExpr id => CheckImplicitThisAccess(id, MemberUse.Write) ?? Lookup(id.Name, id.Line),
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

        private StaticType InferMemberTarget(MemberExpr m) => InferMember(m, MemberUse.Write);

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
        /// <summary>A tool for refactorings (FIRE_MEMBER_LOG): every member access whose receiver class is known (source, line, class, name, bare).</summary>
        public static List<(int Source, int Line, string Class, string Name, bool Bare)>? MemberLog;

        /// <summary>A `switch` over a value of an enum without `case default`: the members no `case` names (TC010, a warning also in strict mode - it may be meant).</summary>
        private void CheckSwitchOverEnum(string enumName, List<Expr> caseValues, int line)
        {
            fire.Values.EnumType? type = null;
            var covered = new HashSet<long>();
            foreach (var value in caseValues)
                if (_refs.TryGetValue(value, out var r) && r is ResolvedRef.EnumMember { Type: { } t } member && t.Name == enumName) { type = t; covered.Add(member.Value); }
                else return;   // (a case that is no member of the enum: not checked)
            if (type == null) return;
            var missing = type.Members.Where(m => !covered.Contains(m.Value)).Select(m => m.Name).Distinct().ToList();
            if (missing.Count > 0)
                Report(SwitchMissingCode, $"The switch over '{enumName}' has no case for {string.Join(", ", missing)} (add the cases or a 'case default')", line);
        }

        /// <summary>The full name of the enum a type name means (its full name, or the name in the namespace of the class that is being checked), or null.</summary>
        private string? EnumNamed(string name)
        {
            if (_enumNames.ContainsKey(name)) return name;
            if (_class?.Name is { } cls && cls.LastIndexOf('.') is var dot and > 0 && _enumNames.ContainsKey(cls.Substring(0, dot) + "." + name)) return cls.Substring(0, dot) + "." + name;
            return null;
        }

        private StaticType InferMember(MemberExpr m, MemberUse use = MemberUse.Read)
        {
            // `Color.Red`: a value of the enum
            if (_refs.TryGetValue(m, out var resolvedRef) && resolvedRef is ResolvedRef.EnumMember { Type: { } enumType }) return StaticType.EnumOf(enumType.Name);
            var target = Infer(m.Target);
            bool isStatic = false;
            if (target.IsDynamic && StaticClassRef(m.Target) is { } staticClass) { target = StaticType.ClassOf(staticClass); isStatic = true; }
            CheckAccess(target, m.Name, use, 0, m.Line, isStatic);
            if (MemberLog != null && target.Kind == StaticKind.Class && target.Class != null) MemberLog.Add((_source, m.Line, target.Class, m.Name, false));
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

        // -------------------------------------------------------------------------------------------------------------
        // Access modifiers (TC012): decided here where the class of the receiver is known; a dynamic receiver (`any`) and reflection are checked at run time
        // -------------------------------------------------------------------------------------------------------------

        private enum MemberUse { Read, Write, Call }

        /// <summary>May the code being checked (the class around it - a lambda in a method counts as code of that class; top-level code: none) use a member that
        /// <paramref name="declaring"/> declares with this access? The rule of the run time (VM.IsMemberAccessAllowed): private - the same class, protected - the
        /// class or one derived from it.</summary>
        private bool AccessAllowedHere(string declaring, AccessModifier access)
        {
            if (access == AccessModifier.Public) return true;
            if (_class == null) return false;
            if (access == AccessModifier.Private) return _class.Name == declaring;
            return Chain(StaticType.ClassOf(_class.Name)).Any(c => c.Decl.Name == declaring);
        }

        private static string AccessText(AccessModifier access) => access == AccessModifier.Private ? "private" : access == AccessModifier.Protected ? "protected" : "public";

        /// <summary>What the run time finds for `name` on an object (or, <paramref name="isStatic"/>, the class) <paramref name="className"/> and whether this code
        /// may use it: null when nothing is found (the run time reports that); otherwise allowed, or the message of the AccessDeniedException.</summary>
        private (bool Allowed, string Message)? AccessOf(string className, string name, MemberUse use, int argc, bool isStatic)
        {
            var chain = Chain(StaticType.ClassOf(className)).Select(c => c.Decl).ToList();
            if (use == MemberUse.Call)
            {
                // every method the run time can choose for that many arguments (overloads by type: the values decide - all of them must be denied)
                var methods = new List<(MethodDecl Method, string Declaring)>();
                var seen = new List<MethodDecl>();
                foreach (var decl in chain)
                {
                    var named = decl.Members.OfType<MethodDecl>().Where(m => m.Name == name && m.IsStatic == isStatic).ToList();
                    foreach (var m in named.Where(m => Fits(m.Params, argc) && !seen.Any(d => OverridesStatic(d, m)))) methods.Add((m, decl.Name));
                    seen.AddRange(named);
                }
                if (methods.Count == 0) return null;
                if (methods.Any(m => AccessAllowedHere(m.Declaring, m.Method.Access))) return (true, "");
                var (first, declaring) = methods[0];
                return (false, $"{(isStatic ? "Static method" : "Method")} '{name}' of '{declaring}' is {AccessText(first.Access)} and cannot be called from here.");
            }
            // a field first (the run time looks for the field of the object, then for an accessor of a property)
            foreach (var decl in chain)
                if (decl.Members.OfType<FieldDecl>().FirstOrDefault(f => f.Name == name && f.IsStatic == isStatic) is { } field)
                    return AccessAllowedHere(decl.Name, field.Access) ? (true, "")
                        : (false, $"{(isStatic ? "Static field" : "Field")} '{name}' of '{decl.Name}' is {AccessText(field.Access)} and cannot be accessed from here.");
            foreach (var decl in chain)
                if (decl.Members.OfType<PropertyDecl>().FirstOrDefault(p => p.Name == name && p.IsStatic == isStatic && (use == MemberUse.Read ? p.Getter : p.Setter) != null) is { } property)
                    return AccessAllowedHere(decl.Name, property.Access) ? (true, "")
                        : (false, $"'{(use == MemberUse.Read ? "get_" : "set_")}{name}' of '{decl.Name}' is {AccessText(property.Access)} and cannot be accessed from here.");
            return null;
        }

        private readonly Dictionary<string, List<string>> _derivedClasses = new();
        /// <summary>The classes of the program derived from <paramref name="className"/> (directly or further down).</summary>
        private List<string> DerivedClasses(string className)
        {
            if (_derivedClasses.TryGetValue(className, out var found)) return found;
            found = _classes.Values.Where(c => c.Name != className && Chain(StaticType.ClassOf(c.Name)).Skip(1).Any(b => b.Decl.Name == className)).Select(c => c.Name).ToList();
            _derivedClasses[className] = found;
            return found;
        }

        /// <summary>Reports a member that this code may not use on a receiver of a known class (TC012). <paramref name="exact"/>: the object is certainly of that
        /// class (`ClassName.Static`, `base.M()`); otherwise it may be of a derived class, where the run time finds the member there - reported only when every
        /// class the object can be denies it (a derived class may declare the name again with another access: then the run time decides).</summary>
        private void CheckAccess(StaticType receiver, string name, MemberUse use, int argc, int line, bool isStatic = false, bool exact = false)
        {
            // (in a lambda with `on target` `this` is the bound object, whose class is not known here)
            if (_inBoundLambda && !isStatic && !exact) return;
            if (receiver.Kind != StaticKind.Class || receiver.Class is not { } className || !_classes.ContainsKey(className) || !FullyKnown(receiver)) return;
            if (AccessOf(className, name, use, argc, isStatic) is not (false, var message)) return;
            if (!exact && !isStatic)
                foreach (var derived in DerivedClasses(className))
                    if (AccessOf(derived, name, use, argc, false) is not (false, _)) return;
            ReportError(AccessCode, message, line);
        }

        /// <summary>A field or property of `this` named without `this.` (ResolvedRef.ImplicitThisMember): checked like `this.name`. Returns null (for `??`).</summary>
        private StaticType? CheckImplicitThisAccess(IdentifierExpr id, MemberUse use)
        {
            if (_class != null && !_inBoundLambda && _refs.TryGetValue(id, out var r) && r is ResolvedRef.ImplicitThisMember && !IsLocal(id.Name))
                CheckAccess(ThisClass(), id.Name, use, 0, id.Line);
            return null;
        }

        /// <summary>`new C(...)` and the call of the base constructor: denied when every constructor of the class for that many arguments is (a class without
        /// constructors has a public one).</summary>
        private void CheckConstructorAccess(string className, int argc, int line)
        {
            if (!_classes.TryGetValue(className, out var decl)) return;
            var ctors = decl.Members.OfType<ConstructorDecl>().Where(c => Fits(c.Params, argc)).ToList();
            if (ctors.Count == 0 || ctors.Any(c => AccessAllowedHere(decl.Name, c.Access))) return;
            ReportError(AccessCode, $"Constructor of '{decl.Name}' is {AccessText(ctors[0].Access)} and cannot be called from here.", line);
        }

        /// <summary>The class `base` stands for in the class being checked (null: none known).</summary>
        private string? BaseClassName() =>
            _class?.BaseRefs?.Select(NameOf).FirstOrDefault(n => _classes.ContainsKey(n));

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
            bool previous = _inLambda, previousBound = _inBoundLambda;
            _inLambda = true;
            _inBoundLambda |= lam.OnTarget != null;
            try { return InferLambdaCore(lam, expected); }
            finally { _inLambda = previous; _inBoundLambda = previousBound; }
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
            var previousReturnRef = _returnTypeRef;
            _returnTypeRef = null;   // (a lambda: its values are not checked against a declaration)
            var previousCollected = _collectedReturns;
            _returnType = expectedReturn;
            var returns = new List<StaticType>();
            _collectedReturns = returns;
            Push();
            for (int i = 0; i < lam.Params.Count; i++) Declare(lam.Params[i].Name, parameterTypes[i]);
            foreach (var s in lam.Body.Statements) Check(s);
            Pop();
            _returnType = previousReturn;
            _returnTypeRef = previousReturnRef;
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
            List<(MethodDecl Method, Dictionary<string, StaticType>? Map)>? candidates = null;
            // the class whose methods the call searches, and whether an object of a derived class can be the receiver (then its overloads count too)
            string? receiverClass = null;
            bool virtualCall = true;
            if (c.Callee is MemberExpr m)
            {
                var target = Infer(m.Target);
                bool staticCall = false;
                if (target.IsDynamic && StaticClassRef(m.Target) is { } staticClass) { target = StaticType.ClassOf(staticClass); virtualCall = false; staticCall = true; }
                if (m.Target is BaseExpr) virtualCall = false;
                if (staticCall) CheckAccess(target, m.Name, MemberUse.Call, c.Args.Count, m.Line, isStatic: true);
                else if (m.Target is BaseExpr && BaseClassName() is { } baseClass) CheckAccess(StaticType.ClassOf(baseClass), m.Name, MemberUse.Call, c.Args.Count, m.Line, exact: true);
                else CheckAccess(target, m.Name, MemberUse.Call, c.Args.Count, m.Line);
                receiverClass = target.Class;
                calleeTarget = target;
                if (target.Kind == StaticKind.Class)
                {
                    candidates = FindMethods(target, m.Name, c.Args.Count);
                    if (candidates.Count == 0 && FindMember(target, m.Name) is { Kind: StaticKind.Lambda } field) { lambda = field; name = m.Name; }
                }
            }
            else if (c.Callee is IdentifierExpr id)
            {
                if (_class != null && !IsLocal(id.Name))
                {
                    candidates = FindMethods(ThisClass(), id.Name, c.Args.Count); receiverClass = ThisClass().Class;
                    // `M()` in a class: a method of `this` (or a static one of the class)
                    if (_refs.TryGetValue(id, out var bare) && bare is ResolvedRef.ImplicitThisMember) CheckAccess(ThisClass(), id.Name, MemberUse.Call, c.Args.Count, id.Line);
                }
                if (candidates is not { Count: > 0 } && Lookup(id.Name) is { Kind: StaticKind.Lambda } variable) { lambda = variable; name = id.Name; }
            }
            else if (Infer(c.Callee) is { Kind: StaticKind.Lambda } callee) lambda = callee;

            // `Pick<string>(a, b)`: only generic methods with that many type parameters are meant
            if (c.TypeArgs is { Count: > 0 } written && candidates != null)
            {
                candidates = candidates.Where(x => x.Method.TypeParams?.Count == written.Count).ToList();
                if (candidates.Count == 0)
                {
                    Report(ArgumentCode, $"No generic method '{(c.Callee as MemberExpr)?.Name ?? (c.Callee as IdentifierExpr)?.Name}' with {written.Count} type parameter(s) takes {c.Args.Count} argument(s)", c.Line);
                    foreach (var a in c.Args) Infer(a);
                    return StaticType.Dynamic;
                }
            }
            if (c.ArgNames != null && candidates is { Count: > 1 })
            {
                // named arguments: only the overloads that have parameters of these names
                var named = candidates.Where(x => c.ArgNames.All(n => n == null || x.Method.Params.Any(p => p.Name == n))).ToList();
                if (named.Count > 1) { foreach (var a in c.Args) Infer(a); return StaticType.Dynamic; }
                if (named.Count == 1) candidates = named;
            }
            if (candidates is { Count: > 1 }) return InferOverloadedCall(c, candidates, receiverClass, virtualCall);
            if (candidates is { Count: 1 }) (method, map) = candidates[0];
            var argParams = method == null ? null : ArgParams(method.Params, c.ArgNames, c.Args.Count, method.Name, c.Line);

            // a generic method (`R Select<R>(R lambda<T> fn)`): its type parameters are taken from the arguments, one after the other (a later argument
            // - a lambda - sees what the earlier ones fixed); what is not found is `any`
            HashSet<string>? open = null;
            if (method?.TypeParams is { Count: > 0 } writtenParams && c.TypeArgs is { Count: > 0 } writtenArgs)
            {
                map = map == null ? new Dictionary<string, StaticType>() : new Dictionary<string, StaticType>(map);
                for (int i = 0; i < writtenParams.Count; i++) map[writtenParams[i].Name] = Declared(writtenArgs[i], 0, map) ?? StaticType.Dynamic;
            }
            else if (method?.TypeParams is { Count: > 0 } typeParams)
            {
                map = map == null ? new Dictionary<string, StaticType>() : new Dictionary<string, StaticType>(map);
                open = typeParams.Select(p => p.Name).ToHashSet();
                foreach (var p in open) map.Remove(p);
            }
            var args = new List<StaticType>();
            for (int i = 0; i < c.Args.Count; i++)
            {
                StaticType? expected = null;
                if (argParams?[i] is { ByRef: false } param)
                {
                    expected = Declared(param.Type, param.ArrayRanks.Count, open == null ? map : WithOpen(map!, open));
                    // a type parameter found to be an int so far may still become a float (`Max(2, 3.5)`): the argument is not held to int
                    if (open != null && param.Type is { TypeArgs: null } bare && open.Contains(bare.BaseName) && param.ArrayRanks.Count + bare.ArrayRank == 0
                        && map!.TryGetValue(bare.BaseName, out var soFar) && soFar.Kind == StaticKind.Int)
                        expected = null;
                    // a lambda whose result is a type parameter still to be found: its own result counts
                    if (open != null && expected is { Kind: StaticKind.Lambda } && param.Type?.LambdaSignature?.ReturnTypeName is { } r && open.Contains(r) && !map!.ContainsKey(r))
                        expected = expected with { Return = null };
                }
                else if (lambda?.Params != null && i < lambda.Params.Count && c.ArgNames == null) expected = lambda.Params[i];
                var arg = Infer(c.Args[i], expected);
                args.Add(arg);
                if (open != null && argParams![i] is { } bound) Bind(bound.Type, bound.ArrayRanks.Count, arg, map!, open);
            }
            if (open != null)
            {
                // what was found is given to the call at run time (ObjectInstance-like: the generic method checks its parameters against it)
                _methodTypeArgs[c] = method!.TypeParams!.Select(p => map!.TryGetValue(p.Name, out var found) ? TypeArgText(found) : "").ToArray();
                // a whole number for a type parameter found to be a float becomes a float at the call (`Max(2, 3.5)`), not only in the method
                for (int i = 0; i < c.Args.Count; i++)
                    if (argParams![i] is { ByRef: false, Type: { TypeArgs: null, ArrayRank: 0 } bare } p && p.ArrayRanks.Count == 0 && open.Contains(bare.BaseName)
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
            CheckArguments(argParams!, args, c.Args, method.Name, map);
            return DeclaredOrDynamic(method.ReturnType, 0, map);
        }

        /// <summary>A call that several overloads by type could take: the arguments are inferred first (without the parameter types to go by), then the
        /// overload the run time takes for them is checked like a single method - when the static types decide it; otherwise the result is not known.</summary>
        private StaticType InferOverloadedCall(CallExpr c, List<(MethodDecl Method, Dictionary<string, StaticType>? Map)> candidates, string? receiverClass, bool virtualCall)
        {
            var args = c.Args.Select(a => Infer(a)).ToList();
            if (PickOverload(candidates, args) is not var (method, map)) return StaticType.Dynamic;
            if (receiverClass != null && OverloadHintFor(method, candidates, args, receiverClass, virtualCall) is { } hint) _overloadHints[c] = hint;
            if (method.TypeParams is { Count: > 0 } typeParams)
            {
                map = map == null ? new Dictionary<string, StaticType>() : new Dictionary<string, StaticType>(map);
                if (c.TypeArgs is { Count: > 0 } written)
                    for (int i = 0; i < typeParams.Count; i++) map[typeParams[i].Name] = Declared(written[i], 0, map) ?? StaticType.Dynamic;
                else
                {
                    var open = typeParams.Select(p => p.Name).ToHashSet();
                    foreach (var p in open) map.Remove(p);
                    for (int i = 0; i < args.Count && i < method.Params.Count; i++) Bind(method.Params[i].Type, method.Params[i].ArrayRanks.Count, args[i], map, open);
                    _methodTypeArgs[c] = typeParams.Select(p => map.TryGetValue(p.Name, out var found) ? TypeArgText(found) : "").ToArray();
                    map = WithOpen(map, open);
                }
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
            CheckConstructorAccess(plain, n.Args.Count, n.Line);
            var map = MapFor(decl.TypeParams, typeArgs);
            var ctors = decl.Members.OfType<ConstructorDecl>().Where(c => Fits(c.Params, n.Args.Count)).ToList();
            if (n.ArgNames != null)
            {
                // named arguments: the constructors with parameters of these names; several of them - not checked
                ctors = ctors.Where(k => n.ArgNames.All(name => name == null || k.Params.Any(p => p.Name == name))).ToList();
                if (ctors.Count == 0) ReportError(NamedArgumentCode, $"No constructor of '{PlainName(plain)}' has parameters named {string.Join(", ", n.ArgNames.Where(x => x != null).Select(x => "'" + x + "'"))}", n.Line);
                if (ctors.Count != 1) { foreach (var a in n.Args) Infer(a); return StaticType.ClassOf(plain, typeArgs); }
                var named = ArgParams(ctors[0].Params, n.ArgNames, n.Args.Count, "new " + PlainName(plain), n.Line);
                var namedArgs = n.Args.Select((a, i) => Infer(a, named[i] is { ByRef: false } p ? Declared(p.Type, p.ArrayRanks.Count, map) : null)).ToList();
                CheckArguments(named, namedArgs, n.Args, "new " + PlainName(plain), map);
                return StaticType.ClassOf(plain, typeArgs);
            }
            var ctor = ctors.Count == 1 ? ctors[0] : null;
            if (ctors.Count > 1)
            {
                // constructors overloaded by type: the one the static types of the arguments decide (in the order the run time prefers them)
                var asMethods = ctors.OrderBy(k => k.Params.Count == n.Args.Count ? 0 : 1).ThenBy(k => k.Params.Count)
                    .Select(k => (Ctor: k, Method: new MethodDecl(k.Source, k.Line, null, "#new", k.Params, k.Body))).ToList();
                var ctorArgs = n.Args.Select(a => Infer(a)).ToList();
                if (PickOverload(asMethods.Select(x => (x.Method, (Dictionary<string, StaticType>?)map)).ToList(), ctorArgs) is var (picked, _))
                {
                    var chosen = asMethods.First(x => ReferenceEquals(x.Method, picked)).Ctor;
                    CheckArguments(chosen.Params, ctorArgs, n.Args, "new " + PlainName(plain), map);
                    if (ConstructorHint(decl, plain, chosen, asMethods.Select(x => x.Method).ToList(), picked, ctorArgs, map) is { } hint) _overloadHints[n] = hint;
                }
                return StaticType.ClassOf(plain, typeArgs);
            }
            var args = n.Args.Select((a, i) => Infer(a, ctor != null && i < ctor.Params.Count && !ctor.Params[i].ByRef ? Declared(ctor.Params[i].Type, ctor.Params[i].ArrayRanks.Count, map) : null)).ToList();
            if (ctor != null) CheckArguments(ctor.Params, args, n.Args, "new " + PlainName(plain), map);
            return StaticType.ClassOf(plain, typeArgs);
        }
    }
}
