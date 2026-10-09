using System.Collections.Generic;

namespace fire.Editor
{
    /// <summary>A built-in member of a simple value type (`string`, array,
    /// byte buffer, ...): no class content in the source, but provided by the VM
    /// (see SPEC 8.10/8.12). `ReturnType`: `int`, `string`, `bool`,
    /// `char`, `buffer` (byte buffer), or `X[]` (array of X) - null: no result.</summary>
    public sealed record BuiltinMember(string Name, bool IsProperty, string Signature, string? ReturnType);

    /// <summary>The members of the value types provided by the VM ITSELF (properties like
    /// `Length`, fixed conversions like `ToBytes()`) - basis for suggestions after `text.`
    /// (see CompletionEngine) AND for the type derivation of chains (see
    /// ScriptSymbolIndex.MemberType). The methods of `string`/`char` (`IndexOf`, `Trim`, ...) are
    /// NOT here, but as `class extends string { ... }` in the prelude (SPEC 5.5.1/8.12) - the index
    /// reads them from there (also the user's own extensions), see
    /// <see cref="ExtensionClassOf"/>. Must match VM.GetField/VM.TryCallBuiltinMethod.</summary>
    public static class BuiltinMembers
    {
        private static BuiltinMember P(string name, string type) => new(name, true, string.Empty, type);
        private static BuiltinMember M(string name, string signature, string? returnType) => new(name, false, signature, returnType);

        private static readonly BuiltinMember[] StringMembers =
        {
            P("Length", "int"),
            M("ToBytes", "", "buffer"),
            M("ToUnicode", "width", "buffer"),
        };

        private static readonly BuiltinMember[] ArrayMembers = { P("Length", "int") };

        private static readonly BuiltinMember[] BufferMembers =
        {
            P("Length", "int"),
            P("littleEndian", "bool"),
            M("ToString", "", "string"),
            M("ToUnicode", "[width]", "string"),
            M("ToUnicodeChar", "[width]", "char"),
            M("ToLittleEndian", "", "buffer"),
            M("ToBigEndian", "", "buffer"),
        };

        private static readonly BuiltinMember[] CharMembers =
        {
            M("ToByte", "", "int"),
            M("ToUnicode", "width", "buffer"),
        };

        private static readonly BuiltinMember[] IntMembers = { M("ToChar", "", "char") };

        /// <summary>The built-in members of the type `type` (empty for everything without
        /// built-in members).</summary>
        public static IReadOnlyList<BuiltinMember> For(ExprType type)
        {
            switch (type.Kind)
            {
                case TypeKind.Primitive:
                    return type.Name switch
                    {
                        "string" => StringMembers,
                        "char" => CharMembers,
                        "int" or "byte" => IntMembers,
                        _ => System.Array.Empty<BuiltinMember>(),
                    };
                case TypeKind.Array:
                    // A `byte` array is a byte buffer (`new byte[n]`, SPEC 8.10).
                    return type.Name == "byte" ? BufferMembers : ArrayMembers;
                default:
                    return System.Array.Empty<BuiltinMember>();
            }
        }

        /// <summary>The key of the collective class into which the index puts the base-type extensions
        /// (`class extends string`) (see fire.Standard.BaseTypeExtensions) - for values of this
        /// type; null for arrays and types without an extensible base type. A `byte` is an
        /// `int` at runtime.</summary>
        public static string? ExtensionClassOf(ExprType type)
        {
            if (type.Kind != TypeKind.Primitive) return null;
            return type.Name switch
            {
                "string" => "$string",
                "char" => "$char",
                "int" or "byte" => "$int",
                "float" => "$float",
                "bool" => "$bool",
                _ => null,
            };
        }

        /// <summary>The type that `returnType` (see BuiltinMember) means.</summary>
        public static ExprType ToExprType(string? returnType)
        {
            if (returnType == null) return ExprType.Unknown;
            if (returnType == "buffer") return new ExprType(TypeKind.Array, "byte");
            if (returnType.EndsWith("[]")) return new ExprType(TypeKind.Array, returnType.Substring(0, returnType.Length - 2));
            return new ExprType(TypeKind.Primitive, returnType);
        }
    }
}
