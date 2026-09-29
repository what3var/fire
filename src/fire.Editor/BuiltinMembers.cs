using System.Collections.Generic;

namespace fire.Editor
{
    /// <summary>Ein eingebautes Mitglied eines einfachen Werttyps (`string`, Array,
    /// Byte-Puffer, ...): kein Klasseninhalt im Quelltext, sondern von der VM
    /// bereitgestellt (siehe SPEC 8.10/8.12). `ReturnType`: `int`, `string`, `bool`,
    /// `char`, `buffer` (Byte-Puffer), oder `X[]` (Array von X) - null: kein Ergebnis.</summary>
    public sealed record BuiltinMember(string Name, bool IsProperty, string Signature, string? ReturnType);

    /// <summary>Die von der VM SELBST bereitgestellten Mitglieder der Werttypen (Properties wie
    /// `Length`, feste Konvertierungen wie `ToBytes()`) - Grundlage für Vorschläge nach `text.`
    /// (siehe CompletionEngine) UND für die Typ-Herleitung von Ketten (siehe
    /// ScriptSymbolIndex.MemberType). Die Methoden von `string`/`char` (`IndexOf`, `Trim`, ...) stehen
    /// NICHT hier, sondern als `class extends string { ... }` im Prelude (SPEC 5.5.1/8.12) - der Index
    /// liest sie von dort (auch die eigenen Erweiterungen des Nutzers), siehe
    /// <see cref="ExtensionClassOf"/>. Muss mit VM.GetField/VM.TryCallBuiltinMethod übereinstimmen.</summary>
    public static class BuiltinMembers
    {
        private static BuiltinMember P(string name, string type) => new(name, true, string.Empty, type);
        private static BuiltinMember M(string name, string signature, string? returnType) => new(name, false, signature, returnType);

        private static readonly BuiltinMember[] StringMembers =
        {
            P("Length", "int"),
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

        /// <summary>Der Schlüssel der Sammelklasse, in die der Index die Basistyp-Erweiterungen
        /// (`class extends string`) legt (siehe fire.Standard.BaseTypeExtensions) - für Werte dieses
        /// Typs; null für Arrays und Typen ohne erweiterbaren Basistyp. Ein `byte` ist zur Laufzeit ein
        /// `int`.</summary>
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

        /// <summary>Rückgabetypen, die sich in fire nicht als Typ hinschreiben lassen (ein Array wie bei
        /// `Split`) - der Prelude deklariert die Methode dann ohne Typ, der Editor kennt ihn hierher.</summary>
        public static ExprType? ReturnTypeHint(ExprType receiver, string method) =>
            receiver.Kind == TypeKind.Primitive && receiver.Name == "string" && method == "Split"
                ? new ExprType(TypeKind.Array, "string")
                : null;

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
