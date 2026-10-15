using System;
using System.Collections.Concurrent;
using System.Linq;
using fire.Ast;
using fire.Runtime;
using fire.Values;

namespace fire.Bytecode
{
    /// <summary>The run-time promise of a declaration with a class, an interface or a constrained type parameter (`Point p`, `IShape s`, `T value`
    /// with `where T is of Animal`): the value is `undefined` or fits one of the alternatives, each a list of conditions that must all hold -
    /// `is of Name` (a class or an interface in the chain of the object, or a basic type) and `is in unit` (a number of that dimension).
    /// Encoded as one string (GuardLocalType's constant, FieldInfo.RequiredType): the text for messages, then the alternatives
    /// (`|`), their conditions (`&amp;`), `in:` before a unit.</summary>
    public static class DeclaredType
    {
        private const char DisplayEnd = '\u0001';

        public sealed record Condition(bool IsUnit, string Name, Unit? Unit);
        public sealed record Spec(string Display, Condition[][] Alternatives)
        {
            /// <summary>A single `is of` a class name (not a basic type, not an interface): what the native generator types as "an object of it".</summary>
            public string? SingleName => Alternatives is [[{ IsUnit: false, Name: var n }]] ? n : null;
        }

        private static readonly ConcurrentDictionary<string, Spec> s_parsed = new();

        /// <summary>A declaration of one class or interface.</summary>
        public static string OfName(string name) => GenericClassNames.PlainName(name) + DisplayEnd + name;

        /// <summary>A constrained type parameter: its constraint groups (OR of ANDs).</summary>
        public static string OfTypeParam(TypeParam tp, Func<string, string> resolveName)
        {
            string Text(TypeConstraint c) => c.Kind == TypeConstraintKind.IsOf ? "is of " + c.Name : "is in " + c.Name;
            string display = tp.Name + " (" + string.Join(" or ", tp.ConstraintGroups.Select(g => string.Join(" and ", g.Constraints.Select(Text)))) + ")";
            string alternatives = string.Join("|", tp.ConstraintGroups.Select(g =>
                string.Join("&", g.Constraints.Select(c => c.Kind == TypeConstraintKind.IsOf ? resolveName(c.Name) : "in:" + c.Name))));
            return display + DisplayEnd + alternatives;
        }

        public static Spec Parse(string text) => s_parsed.GetOrAdd(text, t =>
        {
            int end = t.IndexOf(DisplayEnd);
            var alternatives = t.Substring(end + 1).Split('|').Select(alt => alt.Split('&').Select(c =>
                c.StartsWith("in:", StringComparison.Ordinal) ? new Condition(true, c.Substring(3), Unit.Parse(c.Substring(3))) : new Condition(false, c, null)).ToArray()).ToArray();
            return new Spec(t.Substring(0, end), alternatives);
        });

        /// <summary>Does the value fit the declaration (null: nothing is declared)?</summary>
        public static bool Fits(in Value value, string? required)
        {
            if (required == null || value.Kind == ValueKind.Undefined) return true;
            if (value.Kind == ValueKind.Class && value.AsObjectRef() is ObjectInstance { RtClass: null }) return true;   // (a dead object: every use of it reports that)
            foreach (var alternative in Parse(required).Alternatives)
            {
                bool all = true;
                foreach (var condition in alternative)
                    if (!Holds(value, condition)) { all = false; break; }
                if (all) return true;
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
        public static string Mismatch(in Value value, string required, string what)
        {
            string display = Parse(required).Display;
            string have = value.Kind == ValueKind.Class && value.AsObjectRef() is ObjectInstance obj ? GenericClassNames.PlainName(obj.ClassName) : value.Kind.ToString();
            string article = "AEIOUaeiou".Contains(display[0]) ? "an" : "a";
            return $"{what} is {article} {display}, not {have}.";
        }
    }
}
