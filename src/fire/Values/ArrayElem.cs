using fire.Ast;

namespace fire.Values
{
    /// <summary>The element types of typed arrays (<see cref="ScriptArray.ElemType"/>, SPEC 8.4.2). The numbers are the same in the native runtime
    /// (`E_I8` ... in fire_rt.h): there an element of a typed array is one C number (int8_t ... double, bool, uint16_t) instead of a Value.</summary>
    public static class ArrayElem
    {
        public const byte None = 0, I8 = 1, I16 = 2, I32 = 3, I64 = 4, F8 = 5, F16 = 6, F32 = 7, F64 = 8, Bool = 9, Char = 10, U8 = 11;
        /// <summary>`word a[]` (SPEC 8.10): like `byte a[]` a declaration of a buffer - of machine words (the native runtime: E_WORD).</summary>
        public const byte Word = 12;
        /// <summary>An array of a class, an interface, a record, `string` or an enum (`Dog pets[]`, `string names[]`, ROADMAP 19): the elements are Values
        /// (never packed) that fit the declaration - its DeclaredType text stands where the unit of a number array is (OpCode.TypeArray, ElemUnit,
        /// FieldInfo.RequiredArrayType); `undefined` too, unless the array is defined.</summary>
        public const byte Class = 13;
        /// <summary>`defined int a[]` (SPEC 8.4.2): a mark beside the element type in the code of a declaration (TypeRef.ArrayWidthCode, OpCode.TypeArray,
        /// FieldInfo.RequiredArray) - the elements are never `undefined`: they start at 0 (false, '\0'), storing `undefined` is an ArgumentException.</summary>
        public const byte DefinedFlag = 0x40;

        /// <summary>`new int[n]` (DECISIONS D10): a mark beside the element kind in the code of OpCode.TypeArray - the array is not typed yet (its width
        /// and unit come with the first declaration it is stored in), but it remembers that it was made as an array of ints (I64), floats (F64), bools or
        /// chars: ScriptArray.OpenKind / fire_rt.h Arr.open. A declaration of another kind refuses it (OpenFits).</summary>
        public const byte OpenFlag = 0x80;

        /// <summary>The kind `new T[n]` remembers for a number type (I64 for every int, F64 for every float, Bool, Char); 0 for any other element.</summary>
        public static byte OpenKindOf(TypeRef? type) => Of(type) switch
        {
            >= I8 and <= I64 => I64,
            >= F8 and <= F64 => F64,
            Bool => Bool,
            Char => Char,
            _ => None,
        };

        /// <summary>Does an array made as `new K[n]` (OpenKindOf) go where an array of the element `declared` is declared? The same kind - whatever
        /// width and unit - and an int array where floats are declared (a whole number goes into a float, like a value).</summary>
        public static bool OpenFits(byte open, byte declared)
        {
            declared = TypeOf(declared);
            if (open == None) return true;
            byte kind = declared switch { >= I8 and <= I64 => I64, >= F8 and <= F64 => F64, _ => declared };
            return kind == open || open == I64 && kind == F64;
        }

        /// <summary>The element type of such a code (without DefinedFlag).</summary>
        public static byte TypeOf(int code) => (byte)(code & 0x3F);
        public static bool IsDefined(int code) => (code & DefinedFlag) != 0;

        /// <summary>The zero of an element type - what an element of a defined array starts with (in the unit of the elements, where it can take it).</summary>
        public static Value Zero(byte type, string? unit)
        {
            // (an array of strings starts at ""; of an enum at its first value - its declaration after '\u0002' in the text; of objects at nothing: undefined)
            if (type == Class)
            {
                if (unit == null) return Value.MakeUndefined();
                if (unit.IndexOf('\u0002') is var at and >= 0 && EnumType.FromKey(unit.Substring(at + 1)) is { Members.Count: > 0 } enumType)
                    return Value.MakeEnum(enumType.Members[0].Value, enumType);
                return fire.Bytecode.DeclaredType.Parse(ClassText(unit)).SingleName == "string" ? Value.MakeString("") : Value.MakeUndefined();
            }
            Value zero = type switch { Bool => Value.MakeBool(false), Char => Value.MakeChar('\0'), _ when IsFloat(type) => Value.MakeFloat(0.0), _ => Value.MakeInt(0) };
            return unit != null && TryConvert(type, unit, zero, out var withUnit, out _, out _) ? withUnit : zero;
        }

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
                "word" => Word,
                "bool" => Bool,
                "char" => Char,
                _ => None,
            };
        }

        public static bool IsFloat(byte type) => type is F8 or F16 or F32 or F64;

        public static int Bits(byte type) => type switch { I8 or F8 or U8 => 8, I16 or F16 => 16, I32 or F32 => 32, Word => Value.WordBits, _ => 64 };

        /// <summary>The type as it is written (`int[8]`, `float`, `bool`, the class) - for messages.</summary>
        public static string Name(byte type, string? unit) => type == Class && unit != null ? fire.Bytecode.DeclaredType.Parse(ClassText(unit)).Display : Name(type);

        /// <summary>Is it the text of an array `string a[]` (ArrayElem.Class with the DeclaredType of `string`)?</summary>
        public static bool IsStringText(string? unit) => unit != null && fire.Bytecode.DeclaredType.Parse(ClassText(unit)).SingleName == "string";

        /// <summary>The DeclaredType text of an array of a class (ArrayElem.Class) without an enum's declaration after it.</summary>
        public static string ClassText(string unit) => unit.IndexOf('\u0002') is var at and >= 0 ? unit.Substring(0, at) : unit;

        /// <summary>The type as it is written (`int[8]`, `float`, `bool`) - for messages.</summary>
        public static string Name(byte type) => type switch
        {
            I8 => "int[8]", I16 => "int[16]", I32 => "int[32]", I64 => "int",
            F8 => "float[8]", F16 => "float[16]", F32 => "float[32]", F64 => "float",
            Bool => "bool", Char => "char", U8 => "byte", Word => "word", _ => "any",
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
            if (type == Class)
            {
                // (the declaration: an object of that class or of one derived from it, of that interface, a string, a value of that enum - or undefined)
                if (value.Kind == ValueKind.Undefined || unit == null || fire.Bytecode.DeclaredType.Fits(value, ClassText(unit))) return true;
                // (`string names[]`, DECISIONS D14: any other value becomes its text - an object's ToString() is run by the VM before it gets here)
                if (IsStringText(unit) && value.Kind != ValueKind.Class)
                {
                    stored = Value.MakeString(value.Kind == ValueKind.Buffer ? ByteConversions.AsciiDecode(value.AsBuffer()) : value.ToString());
                    return true;
                }
                error = Error.Type;
                string display = fire.Bytecode.DeclaredType.Parse(ClassText(unit)).Display;
                string have = value.Kind == ValueKind.Class && value.AsObjectRef() is fire.Runtime.ObjectInstance obj ? fire.Ast.GenericClassNames.PlainName(obj.ClassName)
                    : value.IsEnum ? value.Enum!.Name
                    : value.Kind == ValueKind.Lambda && value.AsLambda() is fire.Runtime.LambdaValue lambda ? $"a lambda with {lambda.Proto.ParamCount} parameter(s)" : value.Kind.ToString();
                message = $"An element of an array of {display} must be {("AEIOUaeiou".Contains(display[0]) ? "an" : "a")} {display} (or undefined), not {have}.";
                return false;
            }
            // (an element of `int a[]` is an int: a value of an enum is stored as its number, like in an `int` variable - the native runtime does the same)
            if (value.IsEnum) stored = value = value.WithoutEnum();
            // (the common case - an int of the program without a unit into `int a[]`: stored as it is)
            if (type == I64 && unit == null && value.Kind == ValueKind.Int && value.Wide == 0 && value.Unit is null or { IsUnitless: true }) return true;
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
            // (an element of `int a[]` is an int of the program: no kept width - Value.Wide - for it)
            stored = type == I64 ? (value.Wide != 0 ? Value.MakeInt(value.AsInt(), value.Unit) : value) : value.ConvertWidth(Bits(type), isFloat, type == U8);
            return true;
        }

        /// <summary>Like TryConvert, for an element of a defined array (<paramref name="defined"/>): `undefined` does not fit.</summary>
        public static bool TryConvert(byte type, string? unit, Value value, bool defined, out Value stored, out Error error, out string? message)
        {
            if (defined && value.Kind == ValueKind.Undefined)
            {
                stored = value;
                error = Error.Type;
                message = $"An element of a defined array of {Name(type, unit)} cannot be undefined.";
                return false;
            }
            return TryConvert(type, unit, value, out stored, out error, out message);
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
