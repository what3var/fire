using fire.Ast;
using fire.Values;

namespace fire.Bytecode
{
    /// <summary>The run-time promise of a declaration `int`/`float`/`bool` (a parameter, a field): the value is of that kind or `undefined`
    /// (like a nullable type); a whole number given to a `float` becomes a float, anything else is an ArgumentException. A number declared
    /// without a unit (no `: unit`, no `: any`) is also unitless: a value with a unit is converted like `CheckUnit "unitless"` (another dimension is a
    /// UnitMismatchException). Encoded as TypeTag + 1 (0: nothing is checked), | <see cref="UnitlessBit"/> for the unit.</summary>
    public static class DeclaredKind
    {
        public const byte UnitlessBit = 0x80;
        /// <summary>An int declared with an explicit width beyond the program's (`int[64]`, `int[32]`): a value keeps the width it has (Value.Wide). A plain `int`
        /// holds the program's int - with `#intwidth 32`/`16` a wider value is cut when it goes in (like a store into `int[32]`; natively a narrow field).</summary>
        public const byte KeepsWidthBit = 0x40;
        /// <summary>`defined int`, `defined float`, `defined bool`, `defined word`, `defined char` (SPEC 8.10b): undefined does not fit (an ArgumentException), a value of an enum becomes its
        /// number. `word!`/`char!` are the only declarations of a word or a char that are checked here (their stores convert or check the rest).</summary>
        public const byte NonNullBit = 0x20;

        /// <summary>`initializer`: the initializer of a field or the default value of a parameter. A declaration without a unit takes the unit of its
        /// initializer (the type checker converts at the assignments then); only without one, or with a number literal without a unit, it is
        /// certainly unitless (UnitlessBit).</summary>
        public static byte Of(TypeRef? type, Expr? initializer = null)
        {
            if (type is not { PointerDepth: 0, ArrayRank: 0, LambdaSignature: null }) return 0;
            byte unitless = type.Unit == null && IsUnitlessLiteral(initializer) ? UnitlessBit : (byte)0;
            byte nonNull = type.NonNull ? NonNullBit : (byte)0;
            return type.BaseName switch
            {
                "int" => (byte)((int)TypeTag.Int + 1 | unitless | nonNull | (type.BitWidth is 32 or 64 && !type.Unsigned ? KeepsWidthBit : 0)),
                "float" => (byte)((int)TypeTag.Float + 1 | unitless | nonNull),
                "bool" => (byte)((int)TypeTag.Bool + 1 | nonNull),
                "word" when type.NonNull => (byte)((int)TypeTag.Word + 1 | nonNull),
                "char" when type.NonNull => (byte)((int)TypeTag.Char + 1 | nonNull),
                _ => (byte)0,
            };
        }

        private static bool IsUnitlessLiteral(Expr? e) => e switch
        {
            null => true,
            LiteralExpr { Value: var v } => v.Kind is ValueKind.Int or ValueKind.Float && v.Unit is not { IsUnitless: false },
            UnaryExpr u => IsUnitlessLiteral(u.Operand),
            _ => false,
        };

        public static TypeTag TagOf(byte required) => (TypeTag)((required & 0x1F) - 1);
        public static bool IsNonNull(byte required) => (required & NonNullBit) != 0;
        public static bool KeepsWidth(byte required) => (required & KeepsWidthBit) != 0;
        /// <summary>A plain `int` declaration and an int that keeps a wider width (`#intwidth 32`): it is cut when it goes in.</summary>
        private static bool CutsWide(in Value value, byte required) =>
            value.Wide != 0 && Value.Ints32 && (required & KeepsWidthBit) == 0 && value.Kind == ValueKind.Int && TagOf(required) == TypeTag.Int;
        public static bool IsUnitless(byte required) => (required & UnitlessBit) != 0;

        public static ValueKind ValueKindOf(byte required) => TagOf(required) switch
        {
            TypeTag.Int => ValueKind.Int, TypeTag.Float => ValueKind.Float, TypeTag.Word => ValueKind.Word, TypeTag.Char => ValueKind.Char, _ => ValueKind.Bool,
        };

        /// <summary>Does the value fit as it is?</summary>
        public static bool Fits(in Value value, byte required) =>
            required == 0 || value.Kind == ValueKind.Undefined && !IsNonNull(required)
            || value.Kind == ValueKindOf(required) && (!IsUnitless(required) || value.Unit is null || ReferenceEquals(value.Unit, Unit.Unitless)) && !CutsWide(value, required)
               && !(value.IsEnum && IsNonNull(required));

        /// <summary>A number that has to be unitless but carries a unit (the caller converts it: AdaptToUnit "unitless").</summary>
        public static bool NeedsUnitless(in Value value, byte required) =>
            IsUnitless(required) && value.Kind is ValueKind.Int or ValueKind.Float && value.Unit is { } unit && !ReferenceEquals(unit, Unit.Unitless);

        /// <summary>Checks the kind and converts a whole number for a float in place; null when the value fits now, else the message of the ArgumentException.</summary>
        public static string? Accept(ref Value value, byte required, string what)
        {
            if (CutsWide(value, required)) value = value.WithWide(0);   // (the program's int: cut to its width)
            if (IsNonNull(required))
            {
                if (value.Kind == ValueKind.Undefined) return $"{what} is {Expected(required)} and never undefined.";
                if (value.IsEnum) value = value.WithoutEnum();   // (`defined int` takes the number of a value of an enum)
            }
            if (value.Kind == ValueKind.Undefined || value.Kind == ValueKindOf(required)) return null;
            if (ValueKindOf(required) == ValueKind.Float && value.Kind == ValueKind.Int)
            {
                value = Value.MakeFloat(value.AsInt(), value.Unit);
                return null;
            }
            if (ValueKindOf(required) == ValueKind.Float && value.Kind == ValueKind.Word)
            {
                value = Value.MakeFloat(Value.WordToDouble(value.AsWord()));   // (a word as its unsigned value)
                return null;
            }
            return $"{what} is {Expected(required)}, not {value.Kind}.";
        }

        private static string Expected(byte required) => ValueKindOf(required) switch
        {
            ValueKind.Int => "an int", ValueKind.Float => "a float", ValueKind.Word => "a word", ValueKind.Char => "a char", _ => "a bool",
        };

        /// <summary>The value a field declared `defined int` (`defined float`, `bool`, `word`, `char`) starts with: zero in the unit it demands.</summary>
        public static Value ZeroOf(byte required, Unit? unit) => ValueKindOf(required) switch
        {
            ValueKind.Int => Value.MakeInt(0, unit),
            ValueKind.Float => Value.MakeFloat(0, unit),
            ValueKind.Word => Value.MakeWord(0),
            ValueKind.Char => Value.MakeChar((char)0),
            _ => Value.MakeBool(false),
        };
    }
}
