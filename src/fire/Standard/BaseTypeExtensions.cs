using System.Diagnostics.CodeAnalysis;
using fire.Values;

namespace fire.Standard
{
    /// <summary>
    /// Extensions of base types (`class extends string { ... }`, SPEC 5.5.1).
    ///
    /// A base type (`string`, `char`, `int`, `float`, `bool`) has no class declaration that
    /// a `class extends` could extend. The parser therefore collects all extensions of a
    /// base type into ONE synthetic class with the internal name <see cref="ClassName"/>
    /// (`$string`, ...) - not a valid identifier, so it never collides with a user class. For
    /// resolver and compiler this is an ordinary class; the VM calls its methods when a
    /// method call on a value of this <see cref="ValueKind"/> does not hit an object instance, with
    /// the value itself as `this`.
    ///
    /// ONLY methods are allowed: a base value has no storage in which a field or a
    /// property could live (and `int`/`string` are not held by reference).
    /// </summary>
    public static class BaseTypeExtensions
    {
        /// <summary>All base types that can be extended (keyword → kind of value).
        /// `byte` is deliberately missing: at runtime a `byte` is an `int` (only with a different width) -
        /// whoever wants to extend it extends `int`.</summary>
        public static bool TryGetKind(string typeName, out ValueKind kind)
        {
            switch (typeName)
            {
                case "string": kind = ValueKind.String; return true;
                case "char": kind = ValueKind.Char; return true;
                case "int": kind = ValueKind.Int; return true;
                case "float": kind = ValueKind.Float; return true;
                case "bool": kind = ValueKind.Bool; return true;
                case "array": kind = ValueKind.Array; return true; // `class extends array { ... }`: methods for EVERY array (no keyword, an identifier)
                default: kind = default; return false;
            }
        }

        public static bool IsExtendable(string typeName) => TryGetKind(typeName, out _);

        /// <summary>Internal class name of the collecting class for `typeName` (e.g. `$string`).</summary>
        public static string ClassName(string typeName) => "$" + typeName;

        /// <summary>The collecting class for a base value of this kind - <c>null</c> if the kind
        /// is not extendable at all.</summary>
        public static string? ClassNameFor(ValueKind kind) => kind switch
        {
            ValueKind.String => "$string",
            ValueKind.Char => "$char",
            ValueKind.Int => "$int",
            ValueKind.Float => "$float",
            ValueKind.Bool => "$bool",
            ValueKind.Array => "$array",
            _ => null,
        };

        /// <summary>The type name behind a collecting class name (`$string` → `string`), otherwise
        /// <c>null</c>.</summary>
        public static bool TryGetTypeName(string className, [NotNullWhen(true)] out string? typeName)
        {
            typeName = null;
            if (className.Length < 2 || className[0] != '$') return false;
            string candidate = className.Substring(1);
            if (!IsExtendable(candidate)) return false;
            typeName = candidate;
            return true;
        }
    }
}
