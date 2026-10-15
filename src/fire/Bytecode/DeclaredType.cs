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

        public sealed record Condition(bool IsUnit, string Name, Unit? Unit);
        public sealed record Spec(string Display, Condition[][] Alternatives, int TypeParamIndex = -1, string? TypeParamClass = null)
        {
            /// <summary>A single `is of` a class name (not a basic type, not an interface): what the native generator types as "an object of it".</summary>
            public string? SingleName => Alternatives is [[{ IsUnit: false, Name: var n }]] ? n : null;
        }

        private static readonly ConcurrentDictionary<string, Spec> s_parsed = new();
        private static readonly ConcurrentDictionary<string, Condition> s_conditions = new();

        /// <summary>A declaration of one class or interface.</summary>
        public static string OfName(string name) => GenericClassNames.PlainName(name) + DisplayEnd + name;

        /// <summary>The k-th type parameter of class `owner` (its `where` conditions, OR of ANDs, check it when the type argument is not known).</summary>
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
            return new Spec(t.Substring(0, end), alternatives, index, owner);
        }

        /// <summary>A condition from its text (`Animal`, `int`, `in:cm`) - also a type argument of an object.</summary>
        public static Condition ConditionOf(string text) => s_conditions.GetOrAdd(text, c =>
            c.StartsWith("in:", StringComparison.Ordinal) ? new Condition(true, c.Substring(3), Unit.Parse(c.Substring(3))) : new Condition(false, c, null));

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
            if (required == null || value.Kind == ValueKind.Undefined) return true;
            if (value.Kind == ValueKind.Class && value.AsObjectRef() is ObjectInstance { RtClass: null }) return true;   // (a dead object: every use of it reports that)
            var spec = Parse(required);
            if (spec.TypeParamIndex >= 0)
            {
                if (TypeArgFor(spec, owner, callTypeArgs) is { } typeArg) return Holds(value, ConditionOf(typeArg));
                if (spec.Alternatives.Length == 0) return true;   // (`T` without `where` and without a known type argument: anything)
            }
            foreach (var alternative in spec.Alternatives)
            {
                bool all = true;
                foreach (var condition in alternative)
                    if (!Holds(value, condition)) { all = false; break; }
                if (all) return true;
            }
            return false;
        }

        /// <summary>Like Fits, but a whole number for a `T` that is a `float` becomes a float (like a declared `float`).</summary>
        public static bool FitsOrConvert(ref Value value, string? required, ObjectInstance? owner = null, string[]? callTypeArgs = null)
        {
            if (Fits(value, required, owner, callTypeArgs)) return true;
            if (value.Kind == ValueKind.Int && Parse(required!) is { TypeParamIndex: >= 0 } spec && TypeArgFor(spec, owner, callTypeArgs) == "float")
            {
                value = Value.MakeFloat(value.AsInt(), value.Unit);
                return true;
            }
            return false;
        }

        private static bool Holds(in Value value, Condition condition)
        {
            if (condition.IsUnit)
                return value.Kind is ValueKind.Int or ValueKind.Float && (value.Unit ?? Unit.Unitless).IsCompatibleWith(condition.Unit!);
            switch (condition.Name)
            {
                case "int": return value.Kind == ValueKind.Int;
                case "float": return value.Kind == ValueKind.Float;
                case "bool": return value.Kind == ValueKind.Bool;
                case "char": return value.Kind == ValueKind.Char;
                case "string": return value.Kind == ValueKind.String;
                case "class": case "Exception": return value.Kind == ValueKind.Class;
            }
            // arrays, buffers and strings are enumerable (foreach): they fulfil IEnumerable of the prelude
            if (GenericClassNames.PlainName(condition.Name) == "IEnumerable" && value.Kind is ValueKind.Array or ValueKind.Buffer or ValueKind.String) return true;
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
                display = spec.Display.Split(' ')[0] + " (" + (typeArg.StartsWith("in:", StringComparison.Ordinal) ? "in " + typeArg.Substring(3) : GenericClassNames.PlainName(typeArg)) + ")";
            string have = value.Kind == ValueKind.Class && value.AsObjectRef() is ObjectInstance obj ? GenericClassNames.PlainName(obj.ClassName) : value.Kind.ToString();
            string article = "AEIOUaeiou".Contains(display[0]) ? "an" : "a";
            return $"{what} is {article} {display}, not {have}.";
        }
    }
}
