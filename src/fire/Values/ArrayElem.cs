using fire.Ast;

namespace fire.Values
{
    /// <summary>The element types of typed arrays (<see cref="ScriptArray.ElemType"/>, SPEC 8.4.2). The numbers are the same in the native runtime
    /// (`E_I8` ... in fire_rt.h): there an element of a typed array is one C number (int8_t ... double, bool, uint16_t) instead of a Value.</summary>
    public static class ArrayElem
    {
        public const byte None = 0, I8 = 1, I16 = 2, I32 = 3, I64 = 4, F8 = 5, F16 = 6, F32 = 7, F64 = 8, Bool = 9, Char = 10, U8 = 11;

        public enum Error : byte { None, Type, Unit, Overflow }

        /// <summary>The element type of an array declared with this type (`int a[]`, `int[8] a[]`, `float[32] a[]`, `bool a[]`, `char a[]`, `byte a[]`);
        /// 0 for every other type (`var`, `any`, strings, classes, pointers): such an array takes any value.</summary>
        public static byte Of(TypeRef? type)
        {
            if (type == null || type.PointerDepth > 0 || type.LambdaSignature != null || type.TypeArgCount > 0) return None;
            return type.BaseName switch
            {
                "int" when type.Unsigned => type.BitWidth == 8 ? U8 : None,   // (`byte` is an unsigned int[8])
                "int" => type.BitWidth switch { null or 64 => I64, 32 => I32, 16 => I16, 8 => I8, _ => None },
                "float" => type.BitWidth switch { null or 64 => F64, 32 => F32, 16 => F16, 8 => F8, _ => None },
                "byte" => U8,
                "bool" => Bool,
                "char" => Char,
                _ => None,
            };
        }

        public static bool IsFloat(byte type) => type is F8 or F16 or F32 or F64;

        public static int Bits(byte type) => type switch { I8 or F8 or U8 => 8, I16 or F16 => 16, I32 or F32 => 32, _ => 64 };

        /// <summary>The type as it is written (`int[8]`, `float`, `bool`) - for messages.</summary>
        public static string Name(byte type) => type switch
        {
            I8 => "int[8]", I16 => "int[16]", I32 => "int[32]", I64 => "int",
            F8 => "float[8]", F16 => "float[16]", F32 => "float[32]", F64 => "float",
            Bool => "bool", Char => "char", U8 => "byte", _ => "any",
        };

        /// <summary>The value as an element of an array of this type: cut to the width (an int), rounded (a float), an int becomes a float in a float array,
        /// a number is converted into the unit of the elements (<paramref name="unit"/>; null: the elements have no unit). `undefined` fits every type.
        /// Otherwise false: <paramref name="error"/> says why, <paramref name="message"/> is the text of the exception.</summary>
        public static bool TryConvert(byte type, string? unit, Value value, out Value stored, out Error error, out string? message)
        {
            stored = value;
            error = Error.None;
            message = null;
            if (type == None) return true;
            switch (type)
            {
                case Bool:
                case Char:
                    if (value.Kind == ValueKind.Undefined) { stored = Value.MakeUndefined(); return true; }
                    if (value.Kind == (type == Bool ? ValueKind.Bool : ValueKind.Char)) return true;
                    return Fail(type, value, out error, out message);
            }
            bool isFloat = IsFloat(type);
            if (!(value.Kind is ValueKind.Int or ValueKind.Undefined || isFloat && value.Kind == ValueKind.Float)) return Fail(type, value, out error, out message);
            if (unit != null)
            {
                switch (Value.AdaptToUnit(ref value, unit))
                {
                    case Value.UnitAdaptation.Mismatch:
                        error = Error.Unit;
                        message = $"Expected unit '{Unit.DemandDisplay(unit)}', got: {UnitText(value)}.";
                        return false;
                    case Value.UnitAdaptation.Overflow:
                        error = Error.Overflow;
                        message = $"{value} does not fit into a whole number in '{Unit.DemandDisplay(unit)}'.";
                        return false;
                }
            }
            else if (value.Unit is { IsUnitless: false })
            {
                error = Error.Unit;
                message = $"Expected unit '(no unit)', got: {UnitText(value)}.";
                return false;
            }
            if (value.Kind == ValueKind.Undefined) { stored = value; return true; }
            stored = value.ConvertWidth(Bits(type), isFloat, type == U8);
            return true;
        }

        private static string UnitText(Value value) => value.Unit is { IsUnitless: false } u ? u.ToString() : "(no unit)";

        private static bool Fail(byte type, Value value, out Error error, out string? message)
        {
            error = Error.Type;
            string expected = type switch { Bool => "a bool", Char => "a char", _ when IsFloat(type) => "a number", _ => "an int" };
            message = $"An element of an array of {Name(type)} must be {expected} (or undefined), not {value.Kind}.";
            return false;
        }
    }
}
