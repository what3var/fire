using System.Collections.Generic;

namespace fire.Editor
{
    /// <summary>Ein eingebautes Mitglied eines einfachen Werttyps (`string`, Array,
    /// Byte-Puffer, ...): kein Klasseninhalt im Quelltext, sondern von der VM
    /// bereitgestellt (siehe SPEC 8.10/8.12). `ReturnType`: `int`, `string`, `bool`,
    /// `char`, `buffer` (Byte-Puffer), oder `X[]` (Array von X) - null: kein Ergebnis.</summary>
    public sealed record BuiltinMember(string Name, bool IsProperty, string Signature, string? ReturnType);

    /// <summary>Die eingebauten Mitglieder der Werttypen - Grundlage für Vorschläge
    /// nach `text.` (siehe CompletionEngine) UND für die Typ-Herleitung von Ketten
    /// wie `text.Trim().Split(",")` (siehe ScriptSymbolIndex.MemberType). Muss mit
    /// Runtime.StringMethods/VM.TryCallBuiltinMethod übereinstimmen.</summary>
    public static class BuiltinMembers
    {
        private static BuiltinMember P(string name, string type) => new(name, true, string.Empty, type);
        private static BuiltinMember M(string name, string signature, string? returnType) => new(name, false, signature, returnType);

        private static readonly BuiltinMember[] StringMembers =
        {
            P("Length", "int"),
            M("IndexOf", "wert[, start]", "int"),
            M("LastIndexOf", "wert[, start]", "int"),
            M("Substring", "start[, anzahl]", "string"),
            M("CharAt", "index", "char"),
            M("Contains", "wert", "bool"),
            M("StartsWith", "wert", "bool"),
            M("EndsWith", "wert", "bool"),
            M("ToUpper", "", "string"),
            M("ToLower", "", "string"),
            M("Trim", "", "string"),
            M("TrimStart", "", "string"),
            M("TrimEnd", "", "string"),
            M("Replace", "alt, neu", "string"),
            M("Split", "trenner", "string[]"),
            M("PadLeft", "breite[, zeichen]", "string"),
            M("PadRight", "breite[, zeichen]", "string"),
            M("ToBytes", "", "buffer"),
            M("ToUnicode", "breite", "buffer"),
        };

        private static readonly BuiltinMember[] ArrayMembers = { P("Length", "int") };

        private static readonly BuiltinMember[] BufferMembers =
        {
            P("Length", "int"),
            P("littleEndian", "bool"),
            M("ToString", "", "string"),
            M("ToUnicode", "[breite]", "string"),
            M("ToUnicodeChar", "[breite]", "char"),
            M("ToLittleEndian", "", "buffer"),
            M("ToBigEndian", "", "buffer"),
        };

        private static readonly BuiltinMember[] CharMembers =
        {
            M("ToByte", "", "int"),
            M("ToUnicode", "breite", "buffer"),
        };

        private static readonly BuiltinMember[] IntMembers = { M("ToChar", "", "char") };

        /// <summary>Die eingebauten Mitglieder des Typs `type` (leer für alles ohne
        /// eingebaute Mitglieder).</summary>
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
                    // Ein `byte`-Array ist ein Byte-Puffer (`new byte[n]`, SPEC 8.10).
                    return type.Name == "byte" ? BufferMembers : ArrayMembers;
                default:
                    return System.Array.Empty<BuiltinMember>();
            }
        }

        /// <summary>Der Typ, den `returnType` (siehe BuiltinMember) meint.</summary>
        public static ExprType ToExprType(string? returnType)
        {
            if (returnType == null) return ExprType.Unknown;
            if (returnType == "buffer") return new ExprType(TypeKind.Array, "byte");
            if (returnType.EndsWith("[]")) return new ExprType(TypeKind.Array, returnType.Substring(0, returnType.Length - 2));
            return new ExprType(TypeKind.Primitive, returnType);
        }
    }
}
