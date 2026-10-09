using System;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace fire.Values
{
    /// <summary>
    /// A runtime value. bool/char/string are pure value types without a unit.
    /// int/float/undefined additionally carry a Unit (default: Unitless).
    /// class is a reference to an object instance (ownership model,
    /// see runtime layer - not yet part of this milestone).
    /// </summary>
    public readonly struct Value : IEquatable<Value>
    {
        // Memory layout: 24 bytes (kind 4 + width 4 + bits 8 + reference 8) - a Value is copied on EVERY
        // stack access, the earlier version with a separate field per kind of value (long, double,
        // bool, char, string, object + Unit) was 64 bytes big.
        //   _bits: Int = the value, Float = the IEEE-754 bits of the double, Bool = 0/1, Char = the character code.
        //   _ref:  String = the string, Class/Lambda/Pointer/Array/Buffer = the object,
        //          Int/Float/Undefined = the Unit (never both at once - a number has no object
        //          reference and an object has no unit).
        public ValueKind Kind { get; }

        /// <summary>Relevant only for Int/Float (SPEC "APIs & bit widths"). Default
        /// is always the highest precision (W64).</summary>
        public NumericWidth Width { get; }

        private readonly long _bits;
        private readonly object? _ref;

        /// <summary>The unit - only Int/Float/Undefined carry one (otherwise <c>null</c>).</summary>
        public Unit? Unit => Kind is ValueKind.Int or ValueKind.Float or ValueKind.Undefined
            ? Unsafe.As<Unit?>(_ref)
            : null;

        private long _intValue => _bits;
        private double _floatValue => BitConverter.Int64BitsToDouble(_bits);
        private bool _boolValue => _bits != 0;
        private char _charValue => (char)_bits;

        private Value(ValueKind kind, long bits, object? reference, NumericWidth width = NumericWidth.W64)
        {
            Kind = kind;
            Width = width;
            _bits = bits;
            _ref = reference;
        }

        // ---------------------------------------------------------------
        // Program-wide float precision (SPEC 8.2.1): with `#floatwidth 32` every float is a 32-bit IEEE float.
        // The memory stays a double - each float result is rounded to the nearest float, which is exactly what a
        // float32 CPU (or a native build with `float`) computes for + - * / (a double has more than 2p+2 bits).
        // Set by the host before a program runs (RuntimeSession/Session.Build) from LinkedProgram.FloatWidth.
        // ---------------------------------------------------------------
        public static bool SingleFloats;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Fl(double v) => SingleFloats ? (double)(float)v : v;

        // ---------------------------------------------------------------
        // Factories
        // ---------------------------------------------------------------
        public static Value MakeBool(bool value) =>
            new(ValueKind.Bool, value ? 1 : 0, null);

        public static Value MakeInt(long value, Unit? unit = null, NumericWidth width = NumericWidth.W64) =>
            new(ValueKind.Int, value, unit ?? Values.Unit.Unitless, width);

        public static Value MakeFloat(double value, Unit? unit = null, NumericWidth width = NumericWidth.W64) =>
            new(ValueKind.Float, BitConverter.DoubleToInt64Bits(width == NumericWidth.W64 ? Fl(value) : value), unit ?? Values.Unit.Unitless, width);

        public static Value MakeChar(char value) =>
            new(ValueKind.Char, value, null);

        public static Value MakeString(string value) =>
            new(ValueKind.String, 0, value);

        public static Value MakeUndefined(Unit? unit = null) =>
            new(ValueKind.Undefined, 0, unit ?? Values.Unit.Unitless);

        public static Value MakeClassRef(object objectInstance) =>
            new(ValueKind.Class, 0, objectInstance);

        // Loosely typed (object) for the same reason as MakeClassRef: Values
        // stays independent of the runtime layer (Runtime.LambdaValue); the
        // runtime layer depends on Values, not the other way round.
        public static Value MakeLambda(object lambdaValue) =>
            new(ValueKind.Lambda, 0, lambdaValue);

        public static Value MakePointer(PointerTarget target) =>
            new(ValueKind.Pointer, 0, target);

        public static Value MakeArray(ScriptArray array) =>
            new(ValueKind.Array, 0, array);

        public static Value MakeBuffer(ByteBuffer buffer) =>
            new(ValueKind.Buffer, 0, buffer);

        // ---------------------------------------------------------------
        // Accessors (throw on wrong kind)
        // ---------------------------------------------------------------
        public bool AsBool()
        {
            RequireKind(ValueKind.Bool);
            return _boolValue;
        }

        public long AsInt()
        {
            RequireKind(ValueKind.Int);
            return _intValue;
        }

        public double AsFloat()
        {
            RequireKind(ValueKind.Float);
            return _floatValue;
        }

        public char AsChar()
        {
            RequireKind(ValueKind.Char);
            return _charValue;
        }

        public string AsString()
        {
            RequireKind(ValueKind.String);
            return Unsafe.As<string>(_ref)!;
        }

        public object AsObjectRef()
        {
            RequireKind(ValueKind.Class);
            return _ref!;
        }

        public object AsLambda()
        {
            RequireKind(ValueKind.Lambda);
            return _ref!;
        }

        public PointerTarget AsPointer()
        {
            RequireKind(ValueKind.Pointer);
            return Unsafe.As<PointerTarget>(_ref)!;
        }

        public ScriptArray AsArray()
        {
            RequireKind(ValueKind.Array);
            return Unsafe.As<ScriptArray>(_ref)!;
        }

        public ByteBuffer AsBuffer()
        {
            RequireKind(ValueKind.Buffer);
            return Unsafe.As<ByteBuffer>(_ref)!;
        }

        /// <summary>Internal invariant, not a handleable runtime condition
        /// (see VmInvariantViolationException docs - access with the wrong
        /// kind means "the compiler/resolver has a bug", not
        /// "the script reached an invalid state").</summary>
        private void RequireKind(ValueKind expected)
        {
            if (Kind != expected)
                throw new VmInvariantViolationException($"Value is of type {Kind}, not {expected}.");
        }

        // ---------------------------------------------------------------
        // Coercion
        // ---------------------------------------------------------------

        /// <summary>Converts an Int/Float/Undefined value into another (compatible)
        /// unit. Throws UnitMismatchException on incompatible dimension.</summary>
        public Value CoerceUnit(Unit targetUnit)
        {
            if (Kind != ValueKind.Int && Kind != ValueKind.Float && Kind != ValueKind.Undefined)
                throw new InvalidOperationException($"Type {Kind} carries no unit and cannot be converted.");

            var currentUnit = Unit ?? Values.Unit.Unitless;
            if (currentUnit.Equals(targetUnit))
                return this;

            double factor = currentUnit.ConversionFactorTo(targetUnit);

            return Kind switch
            {
                ValueKind.Int => MakeInt((long)Math.Round(_intValue * factor), targetUnit),
                ValueKind.Float => MakeFloat(_floatValue * factor, targetUnit),
                ValueKind.Undefined => MakeUndefined(targetUnit),
                _ => throw new InvalidOperationException("unreachable"),
            };
        }

        /// <summary>Enforces the target type (currently: int/float among themselves, and
        /// trivial identity). Further type combinations follow with the evaluator.</summary>
        public Value CoerceType(ValueKind targetKind)
        {
            if (Kind == targetKind) return this;

            switch (Kind, targetKind)
            {
                case (ValueKind.Int, ValueKind.Float):
                    return MakeFloat(_intValue, Unit);
                case (ValueKind.Float, ValueKind.Int):
                    return MakeInt((long)Math.Round(_floatValue), Unit);
                case (ValueKind.Undefined, ValueKind.Int):
                    return MakeInt(0, Unit);
                case (ValueKind.Undefined, ValueKind.Float):
                    return MakeFloat(0, Unit);
                default:
                    throw new InvalidOperationException(
                        $"Cannot coerce type {Kind} to {targetKind}.");
            }
        }

        // ---------------------------------------------------------------
        // Arithmetic (simple cases with exactly the same unit; the
        // "anchor rule" for automatic target units with mixed
        // operands lives in the evaluator, since it needs syntax info (":"/"!" in the
        // expression) that the Value itself does not have.)
        // ---------------------------------------------------------------
        // Fast path of the basic arithmetic: both operands numbers with the SAME Unit instance (the normal case:
        // `Unit.Unitless`) - then unit comparison and kind checks are dropped, the result is identical to that of the
        // general path (unit of the left operand, width W64).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool BothNumericSameUnit(in Value a, in Value b) =>
            a.Kind is ValueKind.Int or ValueKind.Float && b.Kind is ValueKind.Int or ValueKind.Float
            && ReferenceEquals(a._ref, b._ref);

        // "In place" variants of the fast paths for the VM (see VM.Step): the result overwrites the left
        // operand directly in the stack, without Value copies through arguments and return value. They return false if the
        // fast path does not apply (other unit/kind, division by 0, ...) - then the caller computes via
        // the general way and gets its result or its exception.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryAddInPlace(ref Value a, in Value b)
        {
            if (!BothNumericSameUnit(a, b)) return false;
            a = a.Kind == ValueKind.Int && b.Kind == ValueKind.Int
                ? new Value(ValueKind.Int, a._bits + b._bits, a._ref)
                : new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() + b.ToDouble())), a._ref);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TrySubtractInPlace(ref Value a, in Value b)
        {
            if (!BothNumericSameUnit(a, b)) return false;
            a = a.Kind == ValueKind.Int && b.Kind == ValueKind.Int
                ? new Value(ValueKind.Int, a._bits - b._bits, a._ref)
                : new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() - b.ToDouble())), a._ref);
            return true;
        }

        /// <summary>Only for two values WITHOUT a unit (the same `Unitless` instance) - otherwise a product unit arises.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryMultiplyInPlace(ref Value a, in Value b)
        {
            if (!BothNumericSameUnit(a, b) || !ReferenceEquals(a._ref, Values.Unit.Unitless)) return false;
            a = a.Kind == ValueKind.Int && b.Kind == ValueKind.Int
                ? new Value(ValueKind.Int, a._bits * b._bits, a._ref)
                : new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() * b.ToDouble())), a._ref);
            return true;
        }

        /// <summary>Only int % int with divisor != 0 (otherwise the general way throws as before).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryModuloInPlace(ref Value a, in Value b)
        {
            if (a.Kind != ValueKind.Int || b.Kind != ValueKind.Int || !ReferenceEquals(a._ref, b._ref) || b._bits == 0 || b._bits == -1)
                return false;
            a = new Value(ValueKind.Int, a._bits % b._bits, a._ref);
            return true;
        }

        /// <summary>Comparison of two numbers with the same Unit instance: `kind` 0 = `&lt;`, 1 = `&lt;=`, 2 = `&gt;`, 3 = `&gt;=`.
        /// Compared as with Compare() as double.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryCompareInPlace(ref Value a, in Value b, int kind)
        {
            if (!BothNumericSameUnit(a, b)) return false;
            int c = a.ToDouble().CompareTo(b.ToDouble());
            bool result = kind switch { 0 => c < 0, 1 => c <= 0, 2 => c > 0, _ => c >= 0 };
            a = MakeBool(result);
            return true;
        }

        /// <summary>Fast path of the fused comparison jumps (VM: JumpIfNotLt etc.): compares two numbers of the same unit
        /// (`kind`: 0 &lt;, 1 &lt;=, 2 &gt;, 3 &gt;=) and returns false if the fast path does not apply. Two integers are compared as
        /// integers, everything else like <see cref="Compare"/> as double.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryCompareFast(in Value a, in Value b, int kind, out bool result)
        {
            if (!BothNumericSameUnit(a, b)) { result = false; return false; }
            int c = a.Kind == ValueKind.Int && b.Kind == ValueKind.Int
                ? a._bits.CompareTo(b._bits)
                : a.ToDouble().CompareTo(b.ToDouble());
            result = kind switch { 0 => c < 0, 1 => c <= 0, 2 => c > 0, _ => c >= 0 };
            return true;
        }

        public static Value Add(Value a, Value b)
        {
            if (BothNumericSameUnit(a, b))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, a._bits + b._bits, a._ref);
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() + b.ToDouble())), a._ref);
            }

            if (a.Kind == ValueKind.Pointer && b.Kind == ValueKind.Int)
                return a.OffsetPointer(b._intValue);

            // String concatenation: as soon as ONE side is a string, the
            // OTHER side is appended via its normal ToString() representation
            // (so this also covers "text " + 42 or 42 + " text", not only
            // string + string) - this is the expected behaviour for '+' in
            // a scripting language and corresponds to ToString()'s already
            // existing cross-kind representation.
            if (a.Kind == ValueKind.String || b.Kind == ValueKind.String)
                return MakeString(a.ToString() + b.ToString());

            RequireNumeric(a); RequireNumeric(b);
            AlignUnits(ref a, ref b);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() + b.ToDouble(), a.Unit);
            return MakeInt(a._intValue + b._intValue, a.Unit);
        }

        public static Value Subtract(Value a, Value b)
        {
            if (BothNumericSameUnit(a, b))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, a._bits - b._bits, a._ref);
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() - b.ToDouble())), a._ref);
            }

            if (a.Kind == ValueKind.Pointer && b.Kind == ValueKind.Int)
                return a.OffsetPointer(-b._intValue);
            if (a.Kind == ValueKind.Pointer && b.Kind == ValueKind.Pointer)
                return MakeInt(a.AsPointer().DistanceTo(b.AsPointer())
                    ?? throw new InvalidOperationException("Pointer difference ('ptr1 - ptr2') needs two pointers into the same array or the same variable."));

            RequireNumeric(a); RequireNumeric(b);
            AlignUnits(ref a, ref b);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() - b.ToDouble(), a.Unit);
            return MakeInt(a._intValue - b._intValue, a.Unit);
        }

        /// <summary>Pointer arithmetic: "N elements further" - see
        /// PointerTarget.Advance (for scope slots a logical step in the
        /// contiguous slot list, for object fields valid only at
        /// offset 0).</summary>
        private Value OffsetPointer(long elementOffset)
        {
            return MakePointer(AsPointer().Advance(elementOffset));
        }

        public static Value Modulo(Value a, Value b)
        {
            if (BothNumericSameUnit(a, b))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, a._bits % b._bits, a._ref);
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() % b.ToDouble())), a._ref);
            }

            RequireNumeric(a); RequireNumeric(b);
            AlignUnits(ref a, ref b);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() % b.ToDouble(), a.Unit);
            return MakeInt(a._intValue % b._intValue, a.Unit);
        }

        public static Value Divide(Value a, Value b)
        {
            // Both without a unit (the same `Unitless` instance): result without a unit, like Unit.Divide.
            if (BothNumericSameUnit(a, b) && ReferenceEquals(a._ref, Values.Unit.Unitless))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, a._bits / b._bits, a._ref);
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() / b.ToDouble())), a._ref);
            }

            RequireNumeric(a); RequireNumeric(b);
            var resultUnit = Values.Unit.Divide(a.Unit ?? Values.Unit.Unitless, b.Unit ?? Values.Unit.Unitless);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() / b.ToDouble(), resultUnit);
            return MakeInt(a._intValue / b._intValue, resultUnit);
        }

        public static Value Multiply(Value a, Value b)
        {
            // Both without a unit (the same `Unitless` instance): result without a unit, like Unit.Multiply.
            if (BothNumericSameUnit(a, b) && ReferenceEquals(a._ref, Values.Unit.Unitless))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, a._bits * b._bits, a._ref);
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() * b.ToDouble())), a._ref);
            }

            RequireNumeric(a); RequireNumeric(b);
            var resultUnit = Values.Unit.Multiply(a.Unit ?? Values.Unit.Unitless, b.Unit ?? Values.Unit.Unitless);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() * b.ToDouble(), resultUnit);
            return MakeInt(a._intValue * b._intValue, resultUnit);
        }

        private double ToDouble() => Kind == ValueKind.Float ? _floatValue : (SingleFloats ? (double)(float)_intValue : _intValue);

        public static Value Negate(Value v)
        {
            RequireNumeric(v);
            return v.Kind == ValueKind.Float ? MakeFloat(-v._floatValue, v.Unit) : MakeInt(-v._intValue, v.Unit);
        }

        public static Value LogicalNot(Value v)
        {
            if (v.Kind != ValueKind.Bool)
                throw new InvalidOperationException($"'!' (negation) expects bool, not {v.Kind}.");
            return MakeBool(!v._boolValue);
        }

        public static Value BitNot(Value v)
        {
            if (v.Kind != ValueKind.Int)
                throw new InvalidOperationException($"'~' expects int, not {v.Kind}.");
            return MakeInt(~v._intValue, v.Unit);
        }

        private static void RequireInt(Value v, string opSymbol)
        {
            if (v.Kind != ValueKind.Int)
                throw new InvalidOperationException($"'{opSymbol}' expects int, not {v.Kind}.");
        }

        public static Value BitAnd(Value a, Value b)
        {
            RequireInt(a, "&"); RequireInt(b, "&");
            RequireSameUnit(a, b);
            return MakeInt(a._intValue & b._intValue, a.Unit);
        }

        public static Value BitOr(Value a, Value b)
        {
            RequireInt(a, "|"); RequireInt(b, "|");
            RequireSameUnit(a, b);
            return MakeInt(a._intValue | b._intValue, a.Unit);
        }

        public static Value BitXor(Value a, Value b)
        {
            RequireInt(a, "#"); RequireInt(b, "#");
            RequireSameUnit(a, b);
            return MakeInt(a._intValue ^ b._intValue, a.Unit);
        }

        /// <summary>`<<`/`>>` - the RIGHT operand (the shift amount) is deliberately
        /// exempt from ANY unit check (unlike `&`/`|`/`#`,
        /// which enforce `RequireSameUnit`): a shift amount is a pure
        /// count ("by how many bits"), not a quantity that could sensibly
        /// carry a unit of its own - the result takes over the unit
        /// of the LEFT operand unchanged, as with `~`.</summary>
        public static Value ShiftLeft(Value a, Value b)
        {
            RequireInt(a, "<<"); RequireInt(b, "<<");
            return MakeInt(a._intValue << (int)b._intValue, a.Unit);
        }

        public static Value ShiftRight(Value a, Value b)
        {
            RequireInt(a, ">>"); RequireInt(b, ">>");
            return MakeInt(a._intValue >> (int)b._intValue, a.Unit);
        }

        /// <summary>`^` (power, NOT bitwise XOR - that is `#`, see BitXor).
        /// Integer-fast exponentiation (repeated multiplication) for
        /// `int^int` with a non-negative exponent (result stays `int`,
        /// as with `+`/`-`/`*`); as soon as ONE operand is `float` or the
        /// exponent is negative, it is computed via `Math.Pow` (float result)
        /// - a negative exponent for `int^int` would otherwise yield only
        /// 0 (integer rounding of values &lt;1), which is hardly ever meant.</summary>
        public static Value Power(Value a, Value b)
        {
            RequireNumeric(a); RequireNumeric(b);
            RequireSameUnit(a, b);

            bool useFloat = a.Kind == ValueKind.Float || b.Kind == ValueKind.Float
                || (b.Kind == ValueKind.Int && b._intValue < 0);
            if (useFloat)
                return MakeFloat(System.Math.Pow(a.ToDouble(), b.ToDouble()), a.Unit);

            long result = 1;
            long baseValue = a._intValue;
            long exponent = b._intValue;
            for (long i = 0; i < exponent; i++)
                result *= baseValue;
            return MakeInt(result, a.Unit);
        }

        /// <summary>Truncates an Int/Float value to the given bit width
        /// ("truncated when copying from large to small"). The internal
        /// storage always stays long/double (full width); TruncateTo applies
        /// only the value range/precision enforced by the target width
        /// and marks the result with the new Width.</summary>
        public Value TruncateTo(NumericWidth width)
        {
            if (Kind == ValueKind.Int)
            {
                long truncated = width switch
                {
                    NumericWidth.W8 => (sbyte)_intValue,
                    NumericWidth.W16 => (short)_intValue,
                    NumericWidth.W32 => (int)_intValue,
                    _ => _intValue,
                };
                return MakeInt(truncated, Unit, width);
            }

            if (Kind == ValueKind.Float)
            {
                double truncated = width switch
                {
                    NumericWidth.W64 => _floatValue,
                    NumericWidth.W32 => (float)_floatValue,
                    NumericWidth.W16 => (double)(Half)_floatValue,
                    NumericWidth.W8 => Minifloat8ToDouble(Minifloat8FromDouble(_floatValue)),
                    _ => _floatValue,
                };
                return MakeFloat(truncated, Unit, width);
            }

            throw new InvalidOperationException($"TruncateTo is only valid for int/float, not {Kind}.");
        }

        // 8-Bit-Minifloat: 1 Vorzeichen- + 4 Exponenten- (Bias 7) + 3 Mantissenbits
        // ("E4M3"-artig). Bewusst simple, dokumentierte Wahl - siehe NumericWidth.
        private static byte Minifloat8FromDouble(double value)
        {
            if (double.IsNaN(value)) return 0x7F;
            int sign = value < 0 ? 1 : 0;
            double abs = Math.Abs(value);
            if (abs == 0) return (byte)(sign << 7);

            int exp = (int)Math.Floor(Math.Log2(abs));
            double mantissaF = abs / Math.Pow(2, exp) - 1.0;
            exp += 7; // Bias
            if (exp <= 0) return (byte)(sign << 7); // Underflow -> 0
            if (exp >= 15) return (byte)((sign << 7) | (0xF << 3) | 0x7); // Overflow -> largest value

            int mantissa = (int)Math.Round(mantissaF * 8) & 0x7;
            return (byte)((sign << 7) | (exp << 3) | mantissa);
        }

        private static double Minifloat8ToDouble(byte bits)
        {
            int sign = (bits >> 7) & 1;
            int exp = (bits >> 3) & 0xF;
            int mantissa = bits & 0x7;
            if (exp == 0 && mantissa == 0) return sign == 1 ? -0.0 : 0.0;
            double value = (1.0 + mantissa / 8.0) * Math.Pow(2, exp - 7);
            return sign == 1 ? -value : value;
        }

        /// <summary>Structural equality for '=='/'!=' - works across kinds
        /// (e.g. Bool vs Int simply yields false, no error).</summary>
        public static bool ValuesEqual(Value a, Value b) => a.Equals(b);

        /// <summary>For '&lt;'/'&lt;='/'&gt;'/'&gt;=' - only for numeric values with
        /// matching unit (like Add/Subtract).</summary>
        public static int Compare(Value a, Value b)
        {
            if (BothNumericSameUnit(a, b))
                return a.ToDouble().CompareTo(b.ToDouble());

            RequireNumeric(a); RequireNumeric(b);
            AlignUnits(ref a, ref b);
            return a.ToDouble().CompareTo(b.ToDouble());
        }

        private static void RequireNumeric(Value v)
        {
            if (v.Kind != ValueKind.Int && v.Kind != ValueKind.Float)
                throw new InvalidOperationException($"Type {v.Kind} is not numeric.");
        }

        /// <summary>Operands of the same dimension but a different scale (`500mm + 2m`) are converted
        /// implicitly - no `:` needed. The type never changes: floats stay floats (result in the unit of the left
        /// operand); two ints stay ints. For ints the finer unit (`mm`) is the target as long as the converted value
        /// does not overflow; otherwise the coarser unit (`m`) is the target and the fraction is cut off.
        /// Units of different dimensions (`mm + kg`, `mm + unitless`) are still an error.</summary>
        private static void AlignUnits(ref Value a, ref Value b)
        {
            if (ReferenceEquals(a._ref, b._ref) && a.Kind is ValueKind.Int or ValueKind.Float) return;
            var ua = a.Unit ?? Values.Unit.Unitless;
            var ub = b.Unit ?? Values.Unit.Unitless;
            if (ua.Equals(ub)) return;
            if (!ua.IsCompatibleWith(ub)) throw new UnitMismatchException(ua, ub);

            if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
            {
                // fine = the unit with the smaller scale (more of them per length), coarse = the other one
                bool aIsFine = ua.Scale <= ub.Scale;
                ref Value fine = ref (aIsFine ? ref a : ref b);
                ref Value coarse = ref (aIsFine ? ref b : ref a);
                var uFine = aIsFine ? ua : ub;
                var uCoarse = aIsFine ? ub : ua;

                double up = coarse._intValue * uCoarse.ConversionFactorTo(uFine);
                if (up >= -9.2e18 && up <= 9.2e18)
                {
                    coarse = MakeInt((long)up, uFine);                       // 2m -> 2000mm
                }
                else
                {
                    double down = fine._intValue * uFine.ConversionFactorTo(uCoarse);
                    fine = MakeInt((long)down, uCoarse);                     // 500mm -> 0m (fraction cut off)
                }
                return;
            }

            double factor = ub.ConversionFactorTo(ua);
            b = b.Kind == ValueKind.Int ? MakeFloat(b._intValue * factor, ua) : MakeFloat(b._floatValue * factor, ua);
        }

        private static void RequireSameUnit(Value a, Value b)
        {
            if (ReferenceEquals(a._ref, b._ref) && a.Kind is ValueKind.Int or ValueKind.Float) return;
            var ua = a.Unit ?? Values.Unit.Unitless;
            var ub = b.Unit ?? Values.Unit.Unitless;
            if (!ua.Equals(ub))
                throw new UnitMismatchException(ua, ub);
        }

        // ---------------------------------------------------------------
        // Equality ignores unit and bit width (as before) and compares, per kind of value, only what
        // it actually holds.
        public bool Equals(Value other)
        {
            if (Kind != other.Kind) return false;
            switch (Kind)
            {
                case ValueKind.Int:
                case ValueKind.Bool:
                case ValueKind.Char:
                    return _bits == other._bits;
                case ValueKind.Float:
                    return _floatValue.Equals(other._floatValue);
                case ValueKind.Undefined:
                    return true;
                case ValueKind.String:
                    return (string?)_ref == (string?)other._ref;
                default:
                    return Equals(_ref, other._ref);
            }
        }

        public override bool Equals(object? obj) => obj is Value v && Equals(v);

        public override int GetHashCode() => Kind switch
        {
            ValueKind.Int or ValueKind.Bool or ValueKind.Char => HashCode.Combine(Kind, _bits),
            ValueKind.Float => HashCode.Combine(Kind, _floatValue),
            ValueKind.Undefined => HashCode.Combine(Kind),
            _ => HashCode.Combine(Kind, _ref),
        };

        // single floats print their own shortest representation (0.1f is "0.1", not "0.10000000149011612")
        private string FloatText() => SingleFloats ? ((float)_floatValue).ToString() : _floatValue.ToString();

        public override string ToString() => Kind switch
        {
            ValueKind.Bool => _boolValue.ToString(),
            ValueKind.Int => Unit is { IsUnitless: false } u ? $"{_intValue}{u}" : _intValue.ToString(),
            ValueKind.Float => Unit is { IsUnitless: false } u2 ? $"{FloatText()}{u2}" : FloatText(),
            ValueKind.Char => _charValue.ToString(),
            ValueKind.String => Unsafe.As<string>(_ref) ?? "",
            ValueKind.Class => $"<object {(_ref as fire.Runtime.ObjectInstance)?.ClassName}>",
            ValueKind.Lambda => "<lambda>",
            ValueKind.Pointer => "<pointer>",
            ValueKind.Array => "<array>",
            ValueKind.Buffer => $"<buffer {AsBuffer().Length} bytes>",
            ValueKind.Undefined => Unit is { IsUnitless: false } u3 ? $"undefined:{u3}" : "undefined",
            _ => "?",
        };

        /// <summary>Format specifier for `$"...{expression:SPEC}..."` (see
        /// Compiler/OpCode.FormatValue). An empty specifier behaves
        /// like ToString(). The FIRST letter selects the format (upper/lower
        /// case distinguished for X/x, otherwise taken over as written), everything
        /// after it is an optional decimal-places/
        /// width specification - except for 'B' passed through 1:1 to .NET's built-in
        /// numeric format strings (Standard Numeric Format
        /// Strings, e.g. "X4", "F2", "D5"):
        ///   X/x - hexadecimal, int only. 'X4' padded to at least 4 digits.
        ///   D   - decimal, zero-padded, int only. 'D5' padded to at least 5.
        ///   F   - fixed-point, int/float. 'F2' = 2 decimal places.
        ///   E   - scientific notation, int/float.
        ///   B   - binary, int only. 'B8' padded to at least 8 digits (built
        ///         by hand, since .NET has no native 'B' number format).
        /// An unknown first letter or a type mismatch (e.g. 'X'
        /// on a float) throws a clear exception instead of silently producing a
        /// wrong/confusing string.</summary>
        public string Format(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return ToString();

            string rest = spec.Length > 1 ? spec.Substring(1) : "";

            switch (char.ToUpperInvariant(spec[0]))
            {
                case 'X':
                case 'D':
                    RequireFormatKind(spec, ValueKind.Int);
                    return _intValue.ToString(spec, CultureInfo.InvariantCulture);

                case 'B':
                {
                    RequireFormatKind(spec, ValueKind.Int);
                    string bin = System.Convert.ToString(_intValue, 2);
                    return rest.Length > 0 && int.TryParse(rest, out int width)
                        ? bin.PadLeft(width, '0')
                        : bin;
                }

                case 'F':
                case 'E':
                    RequireFormatKind(spec, ValueKind.Int, ValueKind.Float);
                    return ToDouble().ToString(spec, CultureInfo.InvariantCulture);

                default:
                    throw new InvalidOperationException(
                        $"Unknown format specifier '{spec}' (known: X/x, B, D, F, E, each with " +
                        "an optional decimals/width suffix like 'F2'/'X4'/'D5').");
            }
        }

        private void RequireFormatKind(string spec, params ValueKind[] allowed)
        {
            if (Array.IndexOf(allowed, Kind) < 0)
                throw new InvalidOperationException(
                    $"Format specifier '{spec}' expects {string.Join("/", allowed)}, not {Kind}.");
        }
    }
}
