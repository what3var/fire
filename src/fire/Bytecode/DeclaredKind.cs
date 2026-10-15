using fire.Ast;
using fire.Runtime;
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

        /// <summary>`initializer`: the initializer of a field or the default value of a parameter. A declaration without a unit takes the unit of its
        /// initializer (the type checker converts at the assignments then); only without one, or with a number literal without a unit, it is
        /// certainly unitless (UnitlessBit).</summary>
        public static byte Of(TypeRef? type, Expr? initializer = null)
        {
            if (type is not { PointerDepth: 0, ArrayRank: 0, LambdaSignature: null }) return 0;
            byte unitless = type.Unit == null && IsUnitlessLiteral(initializer) ? UnitlessBit : (byte)0;
            return type.BaseName switch
            {
                "int" => (byte)((int)TypeTag.Int + 1 | unitless),
                "float" => (byte)((int)TypeTag.Float + 1 | unitless),
                "bool" => (byte)((int)TypeTag.Bool + 1),
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

        public static TypeTag TagOf(byte required) => (TypeTag)((required & 0x7F) - 1);
        public static bool IsUnitless(byte required) => (required & UnitlessBit) != 0;

        public static ValueKind ValueKindOf(byte required) => TagOf(required) switch
        {
            TypeTag.Int => ValueKind.Int, TypeTag.Float => ValueKind.Float, _ => ValueKind.Bool,
        };

        /// <summary>Does the value fit as it is?</summary>
        public static bool Fits(in Value value, byte required) =>
            required == 0 || value.Kind == ValueKind.Undefined
            || value.Kind == ValueKindOf(required) && (!IsUnitless(required) || value.Unit is null || ReferenceEquals(value.Unit, Unit.Unitless));

        /// <summary>A number that has to be unitless but carries a unit (the caller converts it: AdaptToUnit "unitless").</summary>
        public static bool NeedsUnitless(in Value value, byte required) =>
            IsUnitless(required) && value.Kind is ValueKind.Int or ValueKind.Float && value.Unit is { } unit && !ReferenceEquals(unit, Unit.Unitless);

        /// <summary>A declaration with a class (`Point p`, FieldInfo.RequiredClass): `undefined` or an object of that class or of one derived from it.</summary>
        public static bool FitsClass(in Value value, string? required)
        {
            if (required == null || value.Kind == ValueKind.Undefined) return true;
            if (value.Kind != ValueKind.Class || value.AsObjectRef() is not ObjectInstance obj) return false;
            if (required == "Exception" || obj.RtClass == null) return true;   // (like `is of Exception`; a dead object: every use of it reports that)
            for (var rc = obj.RtClass; rc != null; rc = rc.Base)
                if (ReferenceEquals(rc.Name, required) || rc.Name == required) return true;
            return false;
        }

        /// <summary>The message of the ArgumentException for a value that does not fit a declaration with a class.</summary>
        public static string ClassMismatch(in Value value, string required, string what)
        {
            string have = value.Kind == ValueKind.Class && value.AsObjectRef() is ObjectInstance obj ? obj.ClassName : value.Kind.ToString();
            string article = "AEIOUaeiou".Contains(required[0]) ? "an" : "a";
            return $"{what} is {article} {required}, not {have}.";
        }

        /// <summary>Checks the kind and converts a whole number for a float in place; null when the value fits now, else the message of the ArgumentException.</summary>
        public static string? Accept(ref Value value, byte required, string what)
        {
            if (value.Kind == ValueKind.Undefined || value.Kind == ValueKindOf(required)) return null;
            if (ValueKindOf(required) == ValueKind.Float && value.Kind == ValueKind.Int)
            {
                value = Value.MakeFloat(value.AsInt(), value.Unit);
                return null;
            }
            string expected = ValueKindOf(required) switch { ValueKind.Int => "an int", ValueKind.Float => "a float", _ => "a bool" };
            return $"{what} is {expected}, not {value.Kind}.";
        }
    }
}
