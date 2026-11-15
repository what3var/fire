using System;
using System.Collections.Concurrent;
using System.Linq;
using fire.Ast;
using fire.Runtime;
using fire.Values;

namespace fire.Bytecode
{
    /// <summary>The run-time promise of a declaration with a class, an interface or a type parameter (`Point p`, `IShape s`, `T value`): the value
    /// is `undefined` or fits one of the alternatives, each a list of conditions that must all hold - `is of Name` (a class or an interface in the
    /// chain of the object, or a basic type) and `is in unit` (a number of that dimension). A type parameter of a class is checked against the
    /// type argument the object was created with (`new Box&lt;int&gt;()`: ObjectInstance.TypeArgs) and only by its `where` conditions when that is not
    /// known (`new Box()`, a type parameter of a method).
    /// Encoded as one string (GuardLocalType's constant, FieldInfo.RequiredType): the text for messages, then `@k@Class` + TypeParamEnd for the
    /// k-th type parameter of a class, then the alternatives (`|`), their conditions (`&amp;`), `in:` before a unit.</summary>
    public static class DeclaredType
    {
        private const char DisplayEnd = '\u0001';
        private const char TypeParamEnd = '\u0003';
        /// <summary>At the start of the text: a declaration with `defined` (`defined Dog d`, `defined string s`) - undefined does not fit.</summary>
        private const char NonNullMark = '\u0004';

        /// <summary>The declaration `text` with `defined`: undefined does not fit it (an ArgumentException "... is a Dog and never undefined.").</summary>
        public static string NonNullOf(string text) => text.Length > 0 && text[0] == NonNullMark ? text : NonNullMark + text;
        public static bool IsNonNull(string? text) => text is { Length: > 0 } && text[0] == NonNullMark;

        public sealed record Condition(bool IsUnit, string Name, Unit? Unit);
        /// <summary>At the end of the text: the type arguments of a declaration with a generic class (`Bag&lt;int&gt; b`, DECISIONS D10), separated by
        /// TypeArgSep - as the run time keeps them (`int`, a class, `$k@Class` for a type parameter of the code around, "" for what is not known).</summary>
        private const char TypeArgsMark = '\u0005';
        private const char TypeArgSep = '\u0006';
        public static string WithTypeArgs(string text, System.Collections.Generic.IEnumerable<string> typeArgs) => text + TypeArgsMark + string.Join(TypeArgSep, typeArgs);
        public static string WithoutTypeArgs(string text) => text.IndexOf(TypeArgsMark) is var at and >= 0 ? text.Substring(0, at) : text;

        public sealed record Spec(string Display, Condition[][] Alternatives, int TypeParamIndex = -1, string? TypeParamClass = null, bool NonNull = false, string[]? TypeArgs = null)
        {
            /// <summary>A single `is of` a class name (not a basic type, not an interface): what the native generator types as "an object of it".</summary>
            public string? SingleName => Alternatives is [[{ IsUnit: false, Name: var n }]] ? n : null;
        }

        private static readonly ConcurrentDictionary<string, Spec> s_parsed = new();
        private static readonly ConcurrentDictionary<string, Condition> s_conditions = new();

        /// <summary>A declaration of one class or interface.</summary>
        public static string OfName(string name) => GenericClassNames.PlainName(name) + DisplayEnd + name;

        /// <summary>The k-th type parameter of class `owner` (its `where` conditions, OR of ANDs, check it when the type argument is not known).</summary>
        /// <summary>The width code of a typed array (TypeRef.ArrayWidthCode) for the type argument `int`, `float`, `bool` or `char` without a unit (`new T[n]`,
        /// OpCode.TypeArrayOfParam); null for any other.</summary>
        public static int? TypedElementOf(string typeArg) =>
            typeArg is "int" or "float" or "bool" or "char" && new fire.Ast.TypeRef(typeArg, null, 0).ArrayWidthCode(1) is var code and not 0 ? code : null;

        /// <summary>How an array is typed for a type argument (`T items[]`, `new T[n]`; ROADMAP 19): the element type and - for a class, an interface or
        /// `string` (ArrayElem.Class) - the DeclaredType text of the elements; null for what types nothing (a number with a unit, `in cm`, unknown).</summary>
        /// <summary>`findEnum`: the enum of the program a type argument names (null: none) - an array of it has the enum's declaration after its text,
        /// like one declared `Color cs[]` (ArrayElem.Zero).</summary>
        public static (byte Elem, string? Text)? ArrayOfTypeArg(string? typeArg, Func<string, EnumType?>? findEnum = null)
        {
            if (string.IsNullOrEmpty(typeArg) || typeArg[0] == '$' || typeArg.StartsWith("in:", StringComparison.Ordinal)) return null;
            if (TypedElementOf(typeArg) is int code) return ((byte)(code & 0xFF), null);
            if (NumberDemand(typeArg) != null || typeArg is "bool" or "char" or "word") return null;
            if (findEnum?.Invoke(typeArg) is { } enumType) return (fire.Values.ArrayElem.Class, OfName(enumType.Name) + "\u0002" + enumType.Key);
            return (fire.Values.ArrayElem.Class, OfName(typeArg));
        }

        /// <summary>The elements of `lambda&lt;P1, ..., Pn&gt; fs[]` (ROADMAP 19): lambdas with n parameters - what a declaration of one lambda checks too
        /// (OpCode.CheckLambdaSignature; the types of a signature are informational, so the text only has the count).</summary>
        public static string OfLambda(int paramCount) => $"lambda with {paramCount} parameter(s)" + DisplayEnd + LambdaPrefix + paramCount;
        public const string LambdaPrefix = "lambda/";
        /// <summary>The parameter count of a lambda condition (`lambda/2`), -1 for any other.</summary>
        public static int LambdaParamsOf(string conditionName) =>
            conditionName.StartsWith(LambdaPrefix, StringComparison.Ordinal) ? int.Parse(conditionName.AsSpan(LambdaPrefix.Length)) : -1;

        public static string OfTypeParam(TypeParam tp, int index, string owner, Func<string, string> resolveName)
        {
            string Text(TypeConstraint c) => c.Kind == TypeConstraintKind.IsOf ? "is of " + c.Name : "is in " + c.Name;
            string display = tp.ConstraintGroups.Count == 0 ? tp.Name
                : tp.Name + " (" + string.Join(" or ", tp.ConstraintGroups.Select(g => string.Join(" and ", g.Constraints.Select(Text)))) + ")";
            string alternatives = string.Join("|", tp.ConstraintGroups.Select(g =>
                string.Join("&", g.Constraints.Select(c => c.Kind == TypeConstraintKind.IsOf ? resolveName(c.Name) : "in:" + c.Name))));
            return display + DisplayEnd + "@" + index + "@" + owner + TypeParamEnd + alternatives;
        }

        // the last text parsed on this thread: a guard runs with the same constant over and over (no hashing of the string then)
        [ThreadStatic] private static string? t_lastText;
        [ThreadStatic] private static Spec? t_lastSpec;

        public static Spec Parse(string text)
        {
            if (ReferenceEquals(text, t_lastText)) return t_lastSpec!;
            var spec = s_parsed.GetOrAdd(text, ParseText);
            t_lastText = text;
            t_lastSpec = spec;
            return spec;
        }

        private static Spec ParseText(string t)
        {
            bool nonNull = t.Length > 0 && t[0] == NonNullMark;
            if (nonNull) t = t.Substring(1);
            string[]? typeArgs = null;
            if (t.IndexOf(TypeArgsMark) is var argsAt and >= 0) { typeArgs = t.Substring(argsAt + 1).Split(TypeArgSep); t = t.Substring(0, argsAt); }
            int end = t.IndexOf(DisplayEnd);
            string rest = t.Substring(end + 1);
            int index = -1;
            string? owner = null;
            if (rest.StartsWith('@'))
            {
                int typeParamEnd = rest.IndexOf(TypeParamEnd);
                var parts = rest.Substring(1, typeParamEnd - 1).Split('@', 2);
                index = int.Parse(parts[0]);
                owner = parts[1];
                rest = rest.Substring(typeParamEnd + 1);
            }
            var alternatives = rest.Length == 0 ? Array.Empty<Condition[]>()
                : rest.Split('|').Select(alt => alt.Split('&').Select(ConditionOf).ToArray()).ToArray();
            return new Spec(t.Substring(0, end), alternatives, index, owner, nonNull, typeArgs);
        }

        /// <summary>A condition from its text (`Animal`, `int`, `in:cm`) - also a type argument of an object (`int:cm`: the kind, its unit is NumberDemand's).</summary>
        public static Condition ConditionOf(string text) => s_conditions.GetOrAdd(text, c =>
            c.StartsWith("in:", StringComparison.Ordinal) ? new Condition(true, c.Substring(3), Unit.Parse(c.Substring(3)))
            : NumberDemand(c) is { } number ? new Condition(false, number.Kind, null) : new Condition(false, c, null));

        /// <summary>A number type argument: its kind and what it demands of the unit (Value.AdaptToUnit) - `int` is a whole number without a unit
        /// (`unitless`, like a declared `int`), `int:cm` one in cm, `int:any` one in any unit, `float:any m` a float of that dimension; null for others.</summary>
        public static (string Kind, string Demand)? NumberDemand(string typeArg)
        {
            string kind = typeArg.StartsWith("int", StringComparison.Ordinal) ? "int" : typeArg.StartsWith("float", StringComparison.Ordinal) ? "float" : "";
            if (kind.Length == 0 || typeArg.Length > kind.Length && typeArg[kind.Length] != ':') return null;
            string unit = typeArg.Length > kind.Length ? typeArg.Substring(kind.Length + 1) : "unitless";
            return (kind, (kind == "float" ? "#" : "") + unit);
        }

        /// <summary>A type argument as messages show it: `int`, `int : cm`, `in cm`, the class.</summary>
        public static string DisplayOf(string typeArg) =>
            typeArg.StartsWith("in:", StringComparison.Ordinal) ? "in " + typeArg.Substring(3)
            : NumberDemand(typeArg) is { } number && typeArg.Contains(':') ? number.Kind + " : " + typeArg.Substring(number.Kind.Length + 1)
            : GenericClassNames.PlainName(typeArg);

        /// <summary>The type argument of the k-th type parameter of class `declaringClass` for the object (its own, or the one its class gives that base
        /// class: RuntimeClass.BaseTypeArgs); null when it is not known.</summary>
        public static string? TypeArgOf(ObjectInstance? obj, string declaringClass, int index)
        {
            if (obj == null) return null;
            var rc = obj.RtClass;
            var args = obj.TypeArgs;
            while (rc != null && rc.Name != declaringClass)
            {
                var given = rc.BaseTypeArgs;
                if (given == null) args = null;
                else
                {
                    var outer = args;
                    args = given.Select(t => t.StartsWith('$') ? (outer != null && int.Parse(t.AsSpan(1)) is var k && k < outer.Length ? outer[k] : "") : t).ToArray();
                }
                rc = rc.Base;
            }
            if (rc == null || args == null || index >= args.Length || args[index].Length == 0) return null;
            return args[index];
        }

        /// <summary>The type argument a declaration with a type parameter stands for: of the class (from the object) or of the method (from the call).</summary>
        private static string? TypeArgFor(Spec spec, ObjectInstance? owner, string[]? callTypeArgs)
        {
            if (spec.TypeParamClass!.Length > 0) return TypeArgOf(owner, spec.TypeParamClass, spec.TypeParamIndex);
            return callTypeArgs != null && spec.TypeParamIndex < callTypeArgs.Length && callTypeArgs[spec.TypeParamIndex].Length > 0 ? callTypeArgs[spec.TypeParamIndex] : null;
        }

        /// <summary>Does the value fit the declaration (null: nothing is declared)? `owner`: the object whose type parameter a `T` is (the object of the
        /// method for a parameter, the object written to for a field).</summary>
        public static bool Fits(in Value value, string? required, ObjectInstance? owner = null, string[]? callTypeArgs = null)
        {
            if (required == null) return true;
            if (value.Kind == ValueKind.Undefined) return !IsNonNull(required);
            if (value.Kind == ValueKind.Class && value.AsObjectRef() is ObjectInstance { RtClass: null }) return true;   // (a dead object: every use of it reports that)
            var spec = Parse(required);
            if (spec.TypeParamIndex >= 0)
            {
                if (TypeArgFor(spec, owner, callTypeArgs) is { } typeArg)
                    return Holds(value, ConditionOf(typeArg)) && (NumberDemand(typeArg) is not { } number || UnitFits(value, number.Demand));
                if (spec.Alternatives.Length == 0) return true;   // (`T` without `where` and without a known type argument: anything)
            }
            foreach (var alternative in spec.Alternatives)
            {
                bool all = true;
                foreach (var condition in alternative)
                    if (!Holds(value, condition)) { all = false; break; }
                if (all) return spec.TypeArgs == null || TypeArgsFit(value, spec, owner, callTypeArgs);
            }
            return false;
        }

        /// <summary>The type argument a declaration `Bag&lt;int&gt; b` asks for at place k as the object must have it (a `$k@Class` of the code around
        /// resolved); null when it is not known - nothing to check.</summary>
        private static string? WantedTypeArg(string wanted, ObjectInstance? owner, string[]? callTypeArgs)
        {
            if (wanted.Length == 0) return null;
            if (wanted[0] != '$') return wanted;
            int at = wanted.IndexOf('@');
            int k = int.Parse(wanted.AsSpan(1, at - 1));
            if (at == wanted.Length - 1) return callTypeArgs != null && k < callTypeArgs.Length && callTypeArgs[k].Length > 0 ? callTypeArgs[k] : null;
            return TypeArgOf(owner, wanted.Substring(at + 1), k);
        }

        /// <summary>`Bag&lt;int&gt; b` (DECISIONS D10): an object of the class whose type arguments are known has those the declaration names.</summary>
        private static bool TypeArgsFit(in Value value, Spec spec, ObjectInstance? owner, string[]? callTypeArgs)
        {
            if (value.Kind != ValueKind.Class || value.AsObjectRef() is not ObjectInstance obj || spec.SingleName is not { } declaredClass) return true;
            for (int k = 0; k < spec.TypeArgs!.Length; k++)
                if (WantedTypeArg(spec.TypeArgs[k], owner, callTypeArgs) is { } wanted && TypeArgOf(obj, declaredClass, k) is { } has && has != wanted) return false;
            return true;
        }

        /// <summary>`Bag&lt;int&gt;` for messages: the class with the type arguments (`any` for one that is not known).</summary>
        private static string WithArgsDisplay(string cls, System.Collections.Generic.IEnumerable<string?> args) => cls + "<" + string.Join(", ", args.Select(a => a == null ? "any" : DisplayOf(a))) + ">";

        /// <summary>A number that is in the unit a type argument demands already (Fits; anything else goes through FitsOrConvert).</summary>
        private static bool UnitFits(in Value value, string demand)
        {
            demand = demand.TrimStart('#');
            if (demand == "any" || demand.StartsWith("any ", StringComparison.Ordinal)) return demand == "any";
            return (value.Unit ?? Unit.Unitless).Equals(s_demandUnits.GetOrAdd(demand, Unit.Parse));
        }

        private static readonly ConcurrentDictionary<string, Unit> s_demandUnits = new();

        /// <summary>Like Fits, but a number of the kind of a number type argument (or a whole number for a `float`) is taken too: <paramref name="unitDemand"/>
        /// then says what the caller converts it into (Value.AdaptToUnit - a mismatch of the dimension is a UnitMismatchException there).</summary>
        public static bool FitsOrConvert(ref Value value, string? required, ObjectInstance? owner, string[]? callTypeArgs, out string? unitDemand)
        {
            unitDemand = null;
            if (Fits(value, required, owner, callTypeArgs)) return true;
            if (value.Kind is ValueKind.Int or ValueKind.Float && Parse(required!) is { TypeParamIndex: >= 0 } spec && TypeArgFor(spec, owner, callTypeArgs) is { } typeArg
                && NumberDemand(typeArg) is { } number && (number.Kind == "float" || value.Kind == ValueKind.Int))
            {
                unitDemand = number.Demand;
                return true;
            }
            return false;
        }

        private static string WithoutTypeArgsOf(string text) => (IsNonNull(text) ? NonNullMark.ToString() : "") + WithoutTypeArgs(IsNonNull(text) ? text.Substring(1) : text);

        private static bool Holds(in Value value, Condition condition)
        {
            if (condition.IsUnit)
                return value.Kind is ValueKind.Int or ValueKind.Float && (value.Unit ?? Unit.Unitless).IsCompatibleWith(condition.Unit!);
            switch (condition.Name)
            {
                case "int": return value.Kind == ValueKind.Int && !value.IsEnum;
                case "float": return value.Kind == ValueKind.Float;
                case "bool": return value.Kind == ValueKind.Bool;
                case "char": return value.Kind == ValueKind.Char;
                case "word": return value.Kind == ValueKind.Word;
                case "string": return value.Kind == ValueKind.String;
                case "class": case "Exception": return value.Kind == ValueKind.Class;
            }
            if (LambdaParamsOf(condition.Name) is var lambdaParams and >= 0)
                return value.Kind == ValueKind.Lambda && value.AsLambda() is LambdaValue lambda && lambda.Proto.ParamCount == lambdaParams;
            // arrays, buffers and strings are enumerable (foreach): they fulfil IEnumerable of the prelude
            if (GenericClassNames.PlainName(condition.Name) == "IEnumerable" && value.Kind is ValueKind.Array or ValueKind.Buffer or ValueKind.String) return true;
            if (value.IsEnum) return value.Enum!.Matches(condition.Name);
            if (value.Kind != ValueKind.Class || value.AsObjectRef() is not ObjectInstance obj) return false;
            for (var rc = obj.RtClass; rc != null; rc = rc.Base)
                if (rc.Name == condition.Name || rc.Interfaces.Contains(condition.Name)) return true;
            return false;
        }

        /// <summary>The message of the ArgumentException for a value that does not fit.</summary>
        public static string Mismatch(in Value value, string required, string what, ObjectInstance? owner = null, string[]? callTypeArgs = null)
        {
            var spec = Parse(required);
            string display = spec.Display;
            if (spec.TypeParamIndex >= 0 && TypeArgFor(spec, owner, callTypeArgs) is { } typeArg)
                display = spec.Display.Split(' ')[0] + " (" + DisplayOf(typeArg) + ")";
            string have = value.Kind == ValueKind.Class && value.AsObjectRef() is ObjectInstance obj ? GenericClassNames.PlainName(obj.ClassName) : value.IsEnum ? value.Enum!.Name : value.Kind.ToString();
            if (spec.TypeArgs != null && spec.SingleName is { } declaredClass)
            {
                // (`Bag<int> b` given a Bag<string>: both with their type arguments, the object's as the declared class sees them)
                var given = value.Kind == ValueKind.Class && value.AsObjectRef() is ObjectInstance { RtClass: not null } o && Fits(value, WithoutTypeArgsOf(required), owner, callTypeArgs) ? o : null;
                string wantedDisplay = WithArgsDisplay(display, spec.TypeArgs.Select(a => WantedTypeArg(a, owner, callTypeArgs)));
                if (given != null) have = WithArgsDisplay(display, Enumerable.Range(0, spec.TypeArgs.Length).Select(k => TypeArgOf(given, declaredClass, k)));
                else wantedDisplay = display;   // (another class altogether: "is a Bag, not Cat.", like natively)
                display = wantedDisplay;
            }
            string article = "AEIOUaeiou".Contains(display[0]) ? "an" : "a";
            if (value.Kind == ValueKind.Undefined) return $"{what} is {article} {display} and never undefined.";   // (`defined Dog d`)
            return $"{what} is {article} {display}, not {have}.";
        }
    }
}
