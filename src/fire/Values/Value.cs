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
        // Memory layout: 24 bytes (kind 1 + width 1 + enum 2 + padding 4 + bits 8 + reference 8) - a Value is copied on EVERY
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
        /// <summary>Int: the id of its enum type (EnumType.Id), 0 for a plain whole number.</summary>
        private readonly ushort _enum;
        /// <summary>Int: the width a value of an explicit width keeps beyond the program's `int` (SPEC 8.2.1: `int[64]` with `#intwidth 32`) - 64 or 32;
        /// 0 for an int of the program's width. Set by a store into such a declaration (OpCode.ConvertWidth) and by a literal written for one; an operation
        /// with such an operand computes in its width and passes it on. Without `#intwidth` (Ints32 false) it changes nothing.</summary>
        private readonly byte _wide;

        /// <summary>See <see cref="_wide"/>: 64, 32 or 0.</summary>
        public byte Wide => _wide;

        /// <summary>The enum type of an enum value (null for anything else). An enum value is an Int that carries its type (see EnumType).</summary>
        public EnumType? Enum => _enum == 0 ? null : EnumType.ById(_enum);
        /// <summary>Whether this is a value of an enum.</summary>
        public bool IsEnum => _enum != 0;
        /// <summary>The id of the enum type (0: none) - for code that compares types quickly.</summary>
        public ushort EnumId => _enum;

        /// <summary>The unit - only Int/Float/Undefined carry one (otherwise <c>null</c>).</summary>
        public Unit? Unit => Kind is ValueKind.Int or ValueKind.Float or ValueKind.Undefined
            ? Unsafe.As<Unit?>(_ref)
            : null;

        private long _intValue => _bits;
        private double _floatValue => BitConverter.Int64BitsToDouble(_bits);
        private bool _boolValue => _bits != 0;
        private char _charValue => (char)_bits;

        private Value(ValueKind kind, long bits, object? reference, NumericWidth width = NumericWidth.W64, ushort enumId = 0, byte wide = 0)
        {
            Kind = kind;
            Width = width;
            _bits = kind == ValueKind.Int && Ints32 ? CutInt(bits, wide) : bits;
            _ref = reference;
            _enum = enumId;
            _wide = kind == ValueKind.Int ? wide : (byte)0;
        }

        /// <summary>An int of a narrow program (Ints32) cut to its width: the program's, or the wider one it keeps (<see cref="Wide"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long CutInt(long bits, byte wide) => wide >= 64 ? bits : wide == 32 || !Ints16 ? (int)bits : (short)bits;

        /// <summary>The wider of the widths two operands keep (<see cref="Wide"/>).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte W(in Value a, in Value b) => a._wide >= b._wide ? a._wide : b._wide;

        // ---------------------------------------------------------------
        // Program-wide float precision (SPEC 8.2.1): with `#floatwidth 32` every float is a 32-bit IEEE float.
        // The memory stays a double - each float result is rounded to the nearest float, which is exactly what a
        // float32 CPU (or a native build with `float`) computes for + - * / (a double has more than 2p+2 bits).
        // Set by the host before a program runs (RuntimeSession/Session.Build) from LinkedProgram.FloatWidth.
        // ---------------------------------------------------------------
        public static bool SingleFloats;

        // ---------------------------------------------------------------
        // Program-wide int width (SPEC 8.2.1): with `#intwidth 32` every int is a 32-bit two's complement number. The memory stays a
        // long - every int that is made is cut to 32 bits (the operation itself runs in 64 bits, as natively: an int32 operand, the
        // result stored into an int32). Set by the host before a program runs from LinkedProgram.IntWidth.
        // ---------------------------------------------------------------
        public static bool Ints32;
        // `#intwidth 16` (with Ints32 set too: an int narrower than 64 bits): every int is cut to 16 bits.
        public static bool Ints16;

        // ---------------------------------------------------------------
        // The width of `word` (SPEC 8.10): the machine word of the target the program is built for - 16, 32 or 64 bits, unsigned. A word keeps its bits in
        // _bits (zero above the width; with 64 bits all of them, read as an unsigned number). Set by the host before a program runs from
        // LinkedProgram.WordWidth (the VM runs on the machine itself: 64).
        // ---------------------------------------------------------------
        public static int WordBits = 64;
        /// <summary>The bits a word keeps (all ones for 64).</summary>
        public static ulong WordMask => WordBits >= 64 ? ulong.MaxValue : (1UL << WordBits) - 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double Fl(double v) => SingleFloats ? (double)(float)v : v;

        // ---------------------------------------------------------------
        // Factories
        // ---------------------------------------------------------------
        public static Value MakeBool(bool value) =>
            new(ValueKind.Bool, value ? 1 : 0, null);

        public static Value MakeInt(long value, Unit? unit = null, NumericWidth width = NumericWidth.W64, byte wide = 0) =>
            new(ValueKind.Int, value, unit ?? Values.Unit.Unitless, width, 0, wide);

        /// <summary>A whole number from native code (a package, a host function; its ints have 64 bits): with `#intwidth 32`/`16` a number that does not fit the
        /// program's int keeps its 64 bits (<see cref="Wide"/>) - the native meant it (ticks of a clock, sizes of files).</summary>
        public static Value MakeNativeInt(long value, Unit? unit = null) =>
            MakeInt(value, unit, NumericWidth.W64, Ints32 && value != (Ints16 ? (short)value : (int)value) ? (byte)64 : (byte)0);

        /// <summary>The same int keeping the width <paramref name="wide"/> (64 or 32; 0: the program's) - a literal written for an `int[64]`.</summary>
        public Value WithWide(byte wide) => Kind == ValueKind.Int ? new Value(Kind, _bits, _ref, Width, _enum, wide) : this;

        /// <summary>A value of an enum: the whole number `value` that carries its type.</summary>
        public static Value MakeEnum(long value, EnumType type) =>
            new(ValueKind.Int, value, Values.Unit.Unitless, NumericWidth.W64, type.Id);

        /// <summary>The same number without its enum type (a plain int); anything else stays as it is.</summary>
        public Value WithoutEnum() => _enum == 0 ? this : new Value(Kind, _bits, _ref, Width);

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

        /// <summary>A machine word (SPEC 8.10): the bits cut to <see cref="WordBits"/>.</summary>
        public static Value MakeWord(ulong bits) =>
            new(ValueKind.Word, unchecked((long)(bits & WordMask)), null);

        /// <summary>The bits of a word (unsigned).</summary>
        public ulong AsWord()
        {
            RequireKind(ValueKind.Word);
            return unchecked((ulong)_bits);
        }

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

        /// <summary>Converts an Int/Float/Undefined value into another unit: of the same dimension by its factor, of another one the number
        /// in the base unit gets the target unit (SPEC 3.1: `:` always coerces).</summary>
        public Value CoerceUnit(Unit targetUnit)
        {
            if (Kind != ValueKind.Int && Kind != ValueKind.Float && Kind != ValueKind.Undefined)
                throw new InvalidOperationException($"Type {Kind} carries no unit and cannot be converted.");

            var currentUnit = Unit ?? Values.Unit.Unitless;
            if (currentUnit.Equals(targetUnit))
                return LabelledAs(currentUnit, targetUnit);

            // `a:unitless` takes the dimension out: the number in the base unit (12mm becomes 0.012, 2km 2000); an int stays an int when that is exact.
            // `a:unit` of another dimension never fails either: the number in the base unit gets the target unit (`5mm:kg` is 0.005kg, `7:s` is 7s).
            if (Kind != ValueKind.Undefined && (targetUnit.IsUnitless && !currentUnit.IsUnitless || !currentUnit.IsCompatibleWith(targetUnit)))
            {
                double inBase = currentUnit.ToBase(Kind == ValueKind.Int ? _intValue : _floatValue);
                if (Kind == ValueKind.Int && Math.Abs(inBase) < 9.0e15 && inBase == Math.Floor(inBase)) return MakeInt((long)inBase, targetUnit);
                return MakeFloat(inBase, targetUnit);
            }

            if (Kind == ValueKind.Undefined) return MakeUndefined(targetUnit);
            if (!currentUnit.IsLinear || !targetUnit.IsLinear)   // an offset or conversion functions (`20C:F`)
                return Kind == ValueKind.Int
                    ? MakeInt((long)Math.Round(currentUnit.Convert(_intValue, targetUnit)), targetUnit)
                    : MakeFloat(currentUnit.Convert(_floatValue, targetUnit), targetUnit);
            double factor = currentUnit.ConversionFactorTo(targetUnit);

            return Kind switch
            {
                ValueKind.Int => MakeInt((long)Math.Round(_intValue * factor), targetUnit),
                ValueKind.Float => MakeFloat(_floatValue * factor, targetUnit),
                ValueKind.Undefined => MakeUndefined(targetUnit),
                _ => throw new InvalidOperationException("unreachable"),
            };
        }

        /// <summary>The same number in an equal unit under the name of <paramref name="target"/>: `1dm^3:l` is `1l`, and a value stored in a declaration
        /// with a unit (`float:kg*m/s^2 f = x`) prints with the unit of the declaration (fire_rt.h coerceUnit/adaptUnit do the same).</summary>
        private Value LabelledAs(Unit current, Unit target) =>
            ReferenceEquals(current, target) || current.DisplaySymbol == target.DisplaySymbol && current.Offset == target.Offset ? this : new Value(Kind, _bits, target, Width, _enum);

        /// <summary>What a unit declaration does with a value (SPEC "Unit declarations").</summary>
        public enum UnitAdaptation { Ok, Mismatch, Overflow }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Unit> DemandUnits = new();

        /// <summary>A unit declaration takes `value` (see TypeRef.UnitDemand; a leading `#`: the declaration is a float): `demand` is a unit (`mm`, `unitless`) - a value of the same dimension is converted into it, `undefined`
        /// takes it - or `any m` - every unit of the dimension of `m`, nothing is converted. Another dimension is a `Mismatch`, a whole number that does not
        /// fit any more after the conversion an `Overflow`. Values that carry no unit (strings, objects) are left alone. An array (`float:km/h speeds[] =
        /// [...]`) takes the demand for each of its elements, also of inner arrays: they are converted where they are.</summary>
        public static UnitAdaptation AdaptToUnit(ref Value value, string demand) => AdaptToUnit(ref value, demand, out _);

        /// <summary>AdaptToUnit; <paramref name="offending"/> is the value that did not fit (the element of an array, else `value` as it came).</summary>
        public static UnitAdaptation AdaptToUnit(ref Value value, string demand, out Value offending) => AdaptToUnit(ref value, demand, out offending, 0);

        private static UnitAdaptation AdaptToUnit(ref Value value, string demand, out Value offending, int depth)
        {
            offending = value;
            if (value.Kind == ValueKind.Array)
            {
                var array = value.AsArray();
                if (array.IsDestroyed || depth > 32) return UnitAdaptation.Ok;   // (a destroyed array is reported where it is used; an array inside itself ends here)
                var items = array.Items;
                for (int i = 0; i < items.Length; i++)
                {
                    var element = items[i];
                    var result = AdaptToUnit(ref element, demand, out offending, depth + 1);
                    if (result != UnitAdaptation.Ok) return result;
                    items[i] = element;
                }
                return UnitAdaptation.Ok;
            }
            // a word into a float declaration: its unsigned value as a float (without a unit)
            if (value.Kind == ValueKind.Word && demand.StartsWith('#')) value = MakeFloat(WordToDouble(value.AsWord()));
            if (value.Kind is not (ValueKind.Int or ValueKind.Float or ValueKind.Undefined)) return UnitAdaptation.Ok;
            if (demand.StartsWith('#'))
            {
                // the declaration is a float: a whole number becomes one (`float f = 3` holds 3.0)
                demand = demand.Substring(1);
                if (value.Kind == ValueKind.Int) value = MakeFloat(value._intValue, value.Unit);
            }
            if (demand == "any") return UnitAdaptation.Ok;   // (`#any`: only the int becomes a float, every unit stays)
            bool dimensionOnly = demand.StartsWith("any ", StringComparison.Ordinal);
            var target = DemandUnits.GetOrAdd(dimensionOnly ? demand.Substring(4) : demand, Values.Unit.Parse);
            var actual = value.Unit ?? Values.Unit.Unitless;
            if (actual.Equals(target))
            {
                if (!dimensionOnly) value = value.LabelledAs(actual, target);
                return UnitAdaptation.Ok;
            }
            if (value.Kind == ValueKind.Undefined)
            {
                if (!dimensionOnly) value = MakeUndefined(target);
                return UnitAdaptation.Ok;
            }
            // a unit that becomes a plain number (dB) stored without a unit, and a plain number stored in such a unit
            if (!dimensionOnly && (target.IsUnitless && actual.UnitlessConverter != null || actual.IsUnitless && target.UnitlessConverter != null))
            {
                double plain = value.Kind == ValueKind.Float ? value._floatValue : value._intValue;
                double result = target.IsUnitless ? actual.ToUnitless(plain) : target.FromUnitless(plain);
                if (value.Kind == ValueKind.Float) { value = MakeFloat(result, target, value.Width); return UnitAdaptation.Ok; }
                if (Math.Abs(Math.Round(result)) >= 9.2e18) return UnitAdaptation.Overflow;
                value = MakeInt((long)Math.Round(result), target, value.Width, value._wide);
                return UnitAdaptation.Ok;
            }
            if (!actual.IsCompatibleWith(target)) return UnitAdaptation.Mismatch;
            if (dimensionOnly) return UnitAdaptation.Ok;
            if (!actual.IsLinear || !target.IsLinear)
            {
                double result = actual.Convert(value.Kind == ValueKind.Float ? value._floatValue : value._intValue, target);
                if (value.Kind == ValueKind.Float) { value = MakeFloat(result, target, value.Width); return UnitAdaptation.Ok; }
                if (Math.Abs(Math.Round(result)) >= 9.2e18) return UnitAdaptation.Overflow;
                value = MakeInt((long)Math.Round(result), target, value.Width, value._wide);
                return UnitAdaptation.Ok;
            }
            double factor = actual.ConversionFactorTo(target);
            if (value.Kind == ValueKind.Float)
            {
                value = MakeFloat(value._floatValue * factor, target, value.Width);
                return UnitAdaptation.Ok;
            }
            double converted = Math.Round(value._intValue * factor);
            if (Math.Abs(converted) >= 9.2e18) return UnitAdaptation.Overflow;
            value = MakeInt((long)converted, target, value.Width, value._wide);
            return UnitAdaptation.Ok;
        }

        /// <summary>`a::unit`: the same number with another unit, nothing is calculated (12mm::unitless is 12, 12mm::m is 12m).</summary>
        public Value RelabelUnit(Unit targetUnit)
        {
            return Kind switch
            {
                ValueKind.Int => MakeInt(_intValue, targetUnit),
                ValueKind.Float => MakeFloat(_floatValue, targetUnit),
                ValueKind.Undefined => MakeUndefined(targetUnit),
                _ => throw new InvalidOperationException($"Type {Kind} carries no unit and cannot be relabelled."),
            };
        }

        /// <summary>Enforces the target type (currently: int/float among themselves, and
        /// trivial identity). Further type combinations follow with the evaluator.</summary>
        public Value CoerceType(ValueKind targetKind)
        {
            if (Kind == targetKind) return targetKind == ValueKind.Int ? WithoutEnum() : this;   // (`c!int`: the number of an enum value)
            if (Kind == ValueKind.Word)
            {
                // a word as an int: the same bits as a signed number (a 64-bit word above 2^63 is negative, like a cast in C); as a float: its unsigned value
                ulong w = unchecked((ulong)_bits);
                switch (targetKind)
                {
                    case ValueKind.Int: return MakeInt(unchecked((long)w));
                    case ValueKind.Float: return MakeFloat(WordToDouble(w));
                    case ValueKind.Char: return MakeChar(unchecked((char)w));
                }
            }
            if (targetKind == ValueKind.Word) return ToWord();

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
                // characters: the number of the 16-bit code unit (`65!char` is 'A', `'A'!int` is 65); a number outside 0..65535 wraps like a 16-bit cast
                case (ValueKind.Int, ValueKind.Char):
                    return MakeChar(unchecked((char)_intValue));
                case (ValueKind.Float, ValueKind.Char):
                    return MakeChar(unchecked((char)(long)Math.Round(_floatValue)));
                case (ValueKind.Char, ValueKind.Int):
                    return MakeInt(_intValue);
                case (ValueKind.Char, ValueKind.Float):
                    return MakeFloat(_intValue);
                case (ValueKind.Undefined, ValueKind.Char):
                    return MakeChar('\0');
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
                ? new Value(ValueKind.Int, a._bits + b._bits, a._ref, NumericWidth.W64, 0, W(a, b))
                : new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() + b.ToDouble())), a._ref);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TrySubtractInPlace(ref Value a, in Value b)
        {
            if (!BothNumericSameUnit(a, b)) return false;
            a = a.Kind == ValueKind.Int && b.Kind == ValueKind.Int
                ? new Value(ValueKind.Int, a._bits - b._bits, a._ref, NumericWidth.W64, 0, W(a, b))
                : new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() - b.ToDouble())), a._ref);
            return true;
        }

        /// <summary>Only for two values WITHOUT a unit (the same `Unitless` instance) - otherwise a product unit arises.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryMultiplyInPlace(ref Value a, in Value b)
        {
            if (!BothNumericSameUnit(a, b) || !ReferenceEquals(a._ref, Values.Unit.Unitless)) return false;
            a = a.Kind == ValueKind.Int && b.Kind == ValueKind.Int
                ? new Value(ValueKind.Int, a._bits * b._bits, a._ref, NumericWidth.W64, 0, W(a, b))
                : new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() * b.ToDouble())), a._ref);
            return true;
        }

        /// <summary>Only int % int with divisor != 0 (otherwise the general way throws as before).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryModuloInPlace(ref Value a, in Value b)
        {
            if (a.Kind != ValueKind.Int || b.Kind != ValueKind.Int || !ReferenceEquals(a._ref, b._ref) || b._bits == 0 || b._bits == -1)
                return false;
            a = new Value(ValueKind.Int, a._bits % b._bits, a._ref, NumericWidth.W64, 0, W(a, b));
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

        // ---------------------------------------------------------------
        // Words (SPEC 8.10): when one operand is a word, the operation is one on words - the other operand (an int, a char) is taken as a word (cut to the
        // width, two's complement), the result is a word cut to the width (it wraps); division, modulo, comparison and `>>` are unsigned. A float operand
        // makes it a float operation (the word as its unsigned value). An int with a unit does not go with a word (raw data): a UnitMismatchException. The
        // native runtime does the same (fire_rt.h wordOp).
        // ---------------------------------------------------------------
        public enum WordOperation : byte { Add, Sub, Mul, Div, Mod, Pow, And, Or, Xor, Shl, Shr }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool AnyWord(in Value a, in Value b) => a.Kind == ValueKind.Word || b.Kind == ValueKind.Word;

        /// <summary>An operand of a word operation as a word.</summary>
        private static ulong WordOperand(in Value v, string op)
        {
            switch (v.Kind)
            {
                case ValueKind.Word: return unchecked((ulong)v._bits);
                case ValueKind.Int:
                    if (v.Unit is { IsUnitless: false } unit) throw new UnitMismatchException(Values.Unit.Unitless, unit);
                    return unchecked((ulong)v._bits) & WordMask;
                case ValueKind.Char: return unchecked((ulong)v._bits) & WordMask;
                default:
                    if (op is "+" or "-" or "*" or "/" or "%" or "^" or "<") throw new InvalidOperationException($"Type {v.Kind} is not numeric.");
                    throw new InvalidOperationException($"'{op}' expects int, not {v.Kind}.");
            }
        }

        private static readonly string[] WordOpSymbols = { "+", "-", "*", "/", "%", "^", "&", "|", "#", "<<", ">>" };

        /// <summary>`a op b` with a word on one side (see above).</summary>
        public static Value WordOp(WordOperation op, Value a, Value b)
        {
            string symbol = WordOpSymbols[(int)op];
            if ((a.Kind == ValueKind.Float || b.Kind == ValueKind.Float) && op <= WordOperation.Pow)
            {
                // with a float: a float operation (the word as its unsigned value, without a unit)
                if (a.Kind == ValueKind.Word) a = MakeFloat(WordToDouble(a.AsWord()));
                if (b.Kind == ValueKind.Word) b = MakeFloat(WordToDouble(b.AsWord()));
                return op switch
                {
                    WordOperation.Add => Add(a, b), WordOperation.Sub => Subtract(a, b), WordOperation.Mul => Multiply(a, b),
                    WordOperation.Div => Divide(a, b), WordOperation.Mod => Modulo(a, b), _ => Power(a, b),
                };
            }
            // a negative int exponent: a float (like int ^ int)
            if (op == WordOperation.Pow && b.Kind == ValueKind.Int && b._bits < 0 && a.Kind == ValueKind.Word)
                return MakeFloat(Math.Pow(WordToDouble(a.AsWord()), b._bits));
            ulong x = WordOperand(a, symbol), y = WordOperand(b, symbol);
            int bits = WordBits;
            ulong r;
            switch (op)
            {
                case WordOperation.Add: r = unchecked(x + y); break;
                case WordOperation.Sub: r = unchecked(x - y); break;
                case WordOperation.Mul: r = unchecked(x * y); break;
                case WordOperation.Div: if (y == 0) throw new DivideByZeroException(); r = x / y; break;
                case WordOperation.Mod: if (y == 0) throw new DivideByZeroException(); r = x % y; break;
                case WordOperation.Pow:
                    r = 1;
                    for (ulong e = y, f = x; e != 0; e >>= 1) { if ((e & 1) != 0) r = unchecked(r * f); f = unchecked(f * f); }
                    break;
                case WordOperation.And: r = x & y; break;
                case WordOperation.Or: r = x | y; break;
                case WordOperation.Xor: r = x ^ y; break;
                // a shift by the width or more gives 0 (the count as a word: a negative int is a very large count)
                case WordOperation.Shl: r = y >= (ulong)bits ? 0 : x << (int)y; break;
                default: r = y >= (ulong)bits ? 0 : x >> (int)y; break;
            }
            return MakeWord(r);
        }

        /// <summary>A comparison with a word on one side: unsigned (an int is taken as a word); with a float by value. -1, 0, 1.</summary>
        private static int CompareWord(in Value a, in Value b)
        {
            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
            {
                double x = a.Kind == ValueKind.Word ? WordToDouble(a.AsWord()) : a.ToDouble(), y = b.Kind == ValueKind.Word ? WordToDouble(b.AsWord()) : b.ToDouble();
                if (b.Kind == ValueKind.Float && b.Unit is { IsUnitless: false } ub) throw new UnitMismatchException(Values.Unit.Unitless, ub);
                if (a.Kind == ValueKind.Float && a.Unit is { IsUnitless: false } ua) throw new UnitMismatchException(Values.Unit.Unitless, ua);
                return x.CompareTo(y);
            }
            ulong p = WordOperand(a, "<"), q = WordOperand(b, "<");
            return p < q ? -1 : p > q ? 1 : 0;
        }

        public static Value Add(Value a, Value b)
        {
            if (BothNumericSameUnit(a, b))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, a._bits + b._bits, a._ref, NumericWidth.W64, 0, W(a, b));
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
            if (AnyWord(a, b)) return WordOp(WordOperation.Add, a, b);

            RequireNumeric(a); RequireNumeric(b);
            AlignUnits(ref a, ref b);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() + b.ToDouble(), a.Unit);
            return MakeInt(a._intValue + b._intValue, a.Unit, NumericWidth.W64, W(a, b));
        }

        public static Value Subtract(Value a, Value b)
        {
            if (BothNumericSameUnit(a, b))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, a._bits - b._bits, a._ref, NumericWidth.W64, 0, W(a, b));
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() - b.ToDouble())), a._ref);
            }

            if (a.Kind == ValueKind.Pointer && b.Kind == ValueKind.Int)
                return a.OffsetPointer(-b._intValue);
            if (a.Kind == ValueKind.Pointer && b.Kind == ValueKind.Pointer)
                return MakeInt(a.AsPointer().DistanceTo(b.AsPointer())
                    ?? throw new InvalidOperationException("Pointer difference ('ptr1 - ptr2') needs two pointers into the same array or the same variable."));
            if (AnyWord(a, b)) return WordOp(WordOperation.Sub, a, b);

            RequireNumeric(a); RequireNumeric(b);
            AlignUnits(ref a, ref b);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() - b.ToDouble(), a.Unit);
            return MakeInt(a._intValue - b._intValue, a.Unit, NumericWidth.W64, W(a, b));
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
                    return new Value(ValueKind.Int, b._bits == -1 ? 0 : a._bits % b._bits, a._ref, NumericWidth.W64, 0, W(a, b));   // (long.MinValue % -1 overflows in .NET)
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() % b.ToDouble())), a._ref);
            }
            if (AnyWord(a, b)) return WordOp(WordOperation.Mod, a, b);

            RequireNumeric(a); RequireNumeric(b);
            AlignUnits(ref a, ref b);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() % b.ToDouble(), a.Unit);
            return MakeInt(b._intValue == -1 ? 0 : a._intValue % b._intValue, a.Unit, NumericWidth.W64, W(a, b));
        }

        public static Value Divide(Value a, Value b)
        {
            if (AnyWord(a, b)) return WordOp(WordOperation.Div, a, b);
            if (a._ref is Values.Unit { UnitlessConverter: not null } ua) a = MakeFloat(ua.ToUnitless(a.ToDouble()));
            if (b._ref is Values.Unit { UnitlessConverter: not null } ub) b = MakeFloat(ub.ToUnitless(b.ToDouble()));
            // Both without a unit (the same `Unitless` instance): result without a unit, like Unit.Divide.
            if (BothNumericSameUnit(a, b) && ReferenceEquals(a._ref, Values.Unit.Unitless))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, b._bits == -1 ? unchecked(-a._bits) : a._bits / b._bits, a._ref, NumericWidth.W64, 0, W(a, b));   // (long.MinValue / -1 wraps, like the native code)
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() / b.ToDouble())), a._ref);
            }

            RequireNumeric(a); RequireNumeric(b);
            var resultUnit = Values.Unit.Divide(a.Unit ?? Values.Unit.Unitless, b.Unit ?? Values.Unit.Unitless);
            if (resultUnit.IsUnitless && resultUnit.Scale != 1.0) return FoldScale(a, b, resultUnit.Scale, divide: true);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() / b.ToDouble(), resultUnit);
            return MakeInt(b._intValue == -1 ? unchecked(-a._intValue) : a._intValue / b._intValue, resultUnit, NumericWidth.W64, W(a, b));
        }

        public static Value Multiply(Value a, Value b)
        {
            if (AnyWord(a, b)) return WordOp(WordOperation.Mul, a, b);
            if (a._ref is Values.Unit { UnitlessConverter: not null } ua) a = MakeFloat(ua.ToUnitless(a.ToDouble()));
            if (b._ref is Values.Unit { UnitlessConverter: not null } ub) b = MakeFloat(ub.ToUnitless(b.ToDouble()));
            // Both without a unit (the same `Unitless` instance): result without a unit, like Unit.Multiply.
            if (BothNumericSameUnit(a, b) && ReferenceEquals(a._ref, Values.Unit.Unitless))
            {
                if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
                    return new Value(ValueKind.Int, a._bits * b._bits, a._ref, NumericWidth.W64, 0, W(a, b));
                return new Value(ValueKind.Float, BitConverter.DoubleToInt64Bits(Fl(a.ToDouble() * b.ToDouble())), a._ref);
            }

            RequireNumeric(a); RequireNumeric(b);
            var resultUnit = Values.Unit.Multiply(a.Unit ?? Values.Unit.Unitless, b.Unit ?? Values.Unit.Unitless);
            if (resultUnit.IsUnitless && resultUnit.Scale != 1.0) return FoldScale(a, b, resultUnit.Scale, divide: false);

            if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                return MakeFloat(a.ToDouble() * b.ToDouble(), resultUnit);
            return MakeInt(a._intValue * b._intValue, resultUnit, NumericWidth.W64, W(a, b));
        }

        /// <summary>A product or quotient whose units cancel but leave a factor (`5cm / 1mm`, `1km * (1/1m)`): the factor goes into the number (50), the
        /// result has no unit. Two ints stay an int when the factor is a whole number or its inverse is one (`1s / 1ms` is 1000, `1ms / 1s` is 0 like
        /// `1 / 1000`); otherwise the result is a float. The native runtime does the same (fire_rt.h, foldScale).</summary>
        private static Value FoldScale(Value a, Value b, double scale, bool divide)
        {
            scale = Values.Unit.NiceFactor(scale);
            if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int)
            {
                long x = a._intValue, y = b._intValue;
                if (scale >= 1 && scale == Math.Floor(scale) && scale < 9.2e18)
                {
                    long s = (long)scale;
                    return divide ? MakeInt(y == -1 ? unchecked(-(x * s)) : unchecked(x * s) / y, null, NumericWidth.W64, W(a, b)) : MakeInt(unchecked(x * y * s), null, NumericWidth.W64, W(a, b));
                }
                double inv = Values.Unit.NiceFactor(1.0 / scale);
                if (inv >= 1 && inv == Math.Floor(inv) && inv < 9.2e18)
                {
                    long s = (long)inv;
                    if (divide) { long d = unchecked(y * s); return MakeInt(d == -1 ? unchecked(-x) : x / d, null, NumericWidth.W64, W(a, b)); }
                    return MakeInt(unchecked(x * y) / s, null, NumericWidth.W64, W(a, b));
                }
            }
            double r = divide ? a.ToDouble() / b.ToDouble() : a.ToDouble() * b.ToDouble();
            return MakeFloat(r * scale);
        }

        private double ToDouble() => Kind == ValueKind.Float ? _floatValue : (SingleFloats ? (double)(float)_intValue : _intValue);

        public static Value Negate(Value v)
        {
            if (v.Kind == ValueKind.Word) return MakeWord(unchecked(0UL - v.AsWord()));   // (wraps: -1 as a word is the largest one)
            RequireNumeric(v);
            return v.Kind == ValueKind.Float ? MakeFloat(-v._floatValue, v.Unit) : MakeInt(-v._intValue, v.Unit, NumericWidth.W64, v._wide);
        }

        public static Value LogicalNot(Value v)
        {
            if (v.Kind != ValueKind.Bool)
                throw new InvalidOperationException($"'!' (negation) expects bool, not {v.Kind}.");
            return MakeBool(!v._boolValue);
        }

        public static Value BitNot(Value v)
        {
            if (v.Kind == ValueKind.Word) return MakeWord(~v.AsWord());
            if (v.Kind != ValueKind.Int)
                throw new InvalidOperationException($"'~' expects int, not {v.Kind}.");
            if (v.Enum is { IsFlags: true } type) return MakeEnum(~v._intValue, type);   // (the complement of a flag value is one too)
            return MakeInt(~v._intValue, v.Unit, NumericWidth.W64, v._wide);
        }

        private static void RequireInt(Value v, string opSymbol)
        {
            if (v.Kind != ValueKind.Int)
                throw new InvalidOperationException($"'{opSymbol}' expects int, not {v.Kind}.");
        }

        public static Value BitAnd(Value a, Value b)
        {
            if (AnyWord(a, b)) return WordOp(WordOperation.And, a, b);
            RequireInt(a, "&"); RequireInt(b, "&");
            RequireSameUnit(a, b);
            return FlagResult(a, b, a._intValue & b._intValue) ?? MakeInt(a._intValue & b._intValue, a.Unit, NumericWidth.W64, W(a, b));
        }

        public static Value BitOr(Value a, Value b)
        {
            if (AnyWord(a, b)) return WordOp(WordOperation.Or, a, b);
            RequireInt(a, "|"); RequireInt(b, "|");
            RequireSameUnit(a, b);
            return FlagResult(a, b, a._intValue | b._intValue) ?? MakeInt(a._intValue | b._intValue, a.Unit, NumericWidth.W64, W(a, b));
        }

        public static Value BitXor(Value a, Value b)
        {
            if (AnyWord(a, b)) return WordOp(WordOperation.Xor, a, b);
            RequireInt(a, "#"); RequireInt(b, "#");
            RequireSameUnit(a, b);
            return FlagResult(a, b, a._intValue ^ b._intValue) ?? MakeInt(a._intValue ^ b._intValue, a.Unit, NumericWidth.W64, W(a, b));
        }

        /// <summary>`&amp;`, `|`, `#` of two values of the same flag enum: a value of that enum again (`Test.a | Test.b` is a `Test`); otherwise null (a plain int).</summary>
        private static Value? FlagResult(Value a, Value b, long result) =>
            a._enum != 0 && a._enum == b._enum && a.Enum is { IsFlags: true } type ? MakeEnum(result, type) : null;

        /// <summary>`value.HasFlag(flag)` of a flag enum: all bits of `flag` are set in `value` (a flag of 0 is always set, like .NET).</summary>
        public static bool HasFlag(Value value, Value flag) =>
            value.Kind == ValueKind.Int && flag.Kind == ValueKind.Int && (value._intValue & flag._intValue) == flag._intValue;

        /// <summary>`<<`/`>>` - the RIGHT operand (the shift amount) is deliberately
        /// exempt from ANY unit check (unlike `&`/`|`/`#`,
        /// which enforce `RequireSameUnit`): a shift amount is a pure
        /// count ("by how many bits"), not a quantity that could sensibly
        /// carry a unit of its own - the result takes over the unit
        /// of the LEFT operand unchanged, as with `~`.</summary>
        public static Value ShiftLeft(Value a, Value b)
        {
            if (AnyWord(a, b)) return WordOp(WordOperation.Shl, a, b);
            RequireInt(a, "<<"); RequireInt(b, "<<");
            return MakeInt(a._intValue << (int)b._intValue, a.Unit, NumericWidth.W64, a._wide);
        }

        public static Value ShiftRight(Value a, Value b)
        {
            if (AnyWord(a, b)) return WordOp(WordOperation.Shr, a, b);
            RequireInt(a, ">>"); RequireInt(b, ">>");
            return MakeInt(a._intValue >> (int)b._intValue, a.Unit, NumericWidth.W64, a._wide);
        }

        /// <summary>`^` (power, NOT bitwise XOR - that is `#`, see BitXor).
        /// A power is always a float (`2 ^ 3` is 8.0, `2 ^ -1` is 0.5): its type must not depend on the value of the exponent - a variable keeps its
        /// type (docs/spec/TYPES.md), and `int ^ int` as an int would be one for some exponents only. A whole result is `x!int` away.</summary>
        /// <summary>`a ^ b` (SPEC 1.1): two ints give the int power (<see cref="IntPower"/>), anything with a float the float power. The units must be equal.</summary>
        public static Value Power(Value a, Value b)
        {
            if (AnyWord(a, b)) return WordOp(WordOperation.Pow, a, b);
            RequireNumeric(a); RequireNumeric(b);
            RequireSameUnit(a, b);
            if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int) return MakeInt(IntPower(a._intValue, b._intValue), a.Unit, NumericWidth.W64, W(a, b));
            return MakeFloat(System.Math.Pow(a.ToDouble(), b.ToDouble()), a.Unit);
        }

        /// <summary>`a ^ b` whose target is a float (OpCode.PowerFloat): always the float power, also of two ints (`float f = 2 ^ -1` is 0.5).</summary>
        public static Value PowerFloat(Value a, Value b)
        {
            if (AnyWord(a, b)) return WordOp(WordOperation.Pow, a, b);
            RequireNumeric(a); RequireNumeric(b);
            RequireSameUnit(a, b);
            return MakeFloat(System.Math.Pow(a.ToDouble(), b.ToDouble()), a.Unit);
        }

        /// <summary>The int power (wraps like `*`); a negative exponent is what dividing gives, like `/` of ints: 1 for 1, ±1 for -1, 0 otherwise, and a
        /// DivideByZeroException for 0.</summary>
        public static long IntPower(long b, long e)
        {
            if (e < 0)
            {
                if (b == 0) throw new DivideByZeroException();
                return b == 1 ? 1 : b == -1 ? ((e & 1) == 0 ? 1 : -1) : 0;
            }
            long r = 1;
            unchecked
            {
                while (e > 0)
                {
                    if ((e & 1) != 0) r *= b;
                    e >>= 1;
                    if (e > 0) b *= b;
                }
            }
            return r;
        }

        /// <summary>Truncates an Int/Float value to the given bit width
        /// ("truncated when copying from large to small"). The internal
        /// storage always stays long/double (full width); TruncateTo applies
        /// only the value range/precision enforced by the target width
        /// and marks the result with the new Width.</summary>
        /// <summary>`x!int[8]`, `x!byte`, `x!float[32]` (OpCode.ConvertWidth): the value as a number of that kind (like `!int`/`!float`, also from a char)
        /// cut to the width - an int in two's complement (`300!int[8]` is 44, `200!int[8]` is -56), `unsigned` keeps 0..2^bits-1 (`byte`: `300!byte` is 44,
        /// `-1!byte` is 255); a float is rounded to float32, to IEEE half precision (16) or to FP8 E4M3 (8), see <see cref="RoundFloatToWidth"/> (64 leaves it).</summary>
        public Value ConvertWidth(int bits, bool toFloat, bool unsigned)
        {
            var number = CoerceType(toFloat ? ValueKind.Float : ValueKind.Int);
            if (toFloat) return bits is 8 or 16 or 32 ? MakeFloat(RoundFloatToWidth(number._floatValue, bits), number.Unit, (NumericWidth)bits) : MakeFloat(number._floatValue, number.Unit);
            // (an explicit width beyond the program's int - `int[64]` with `#intwidth 32`, `int[32]` with `#intwidth 16` - is kept by the value: Wide)
            if (bits >= 64) return MakeInt(number._intValue, number.Unit, NumericWidth.W64, 64);
            long mask = (1L << bits) - 1, raw = number._intValue & mask;
            if (!unsigned && (raw & (1L << (bits - 1))) != 0) raw -= 1L << bits;
            // (`byte` - the unsigned width - is raw data without a unit, SPEC 8.10: the number stored there loses its unit)
            return MakeInt(raw, unsigned ? Values.Unit.Unitless : number.Unit, (NumericWidth)bits, bits == 32 && !unsigned ? (byte)32 : (byte)0);
        }

        /// <summary>`x!word` and a store into a `word` (OpCode.ConvertWidth with the word flag, SPEC 8.10): an int cut to the width of a word (two's complement:
        /// `-1!word` is the largest word), a float rounded to a whole number first (NaN is 0, beyond the range the nearest end), a char its code, `undefined` 0;
        /// a unit is dropped (raw data). Anything else is an InvalidOperationException (an ArgumentException in the program).</summary>
        public Value ToWord() => Kind switch
        {
            ValueKind.Word => this,
            ValueKind.Int or ValueKind.Char => MakeWord(unchecked((ulong)_bits)),
            ValueKind.Float => MakeWord(DoubleToWordBits(_floatValue)),
            ValueKind.Undefined => MakeWord(0),
            _ => throw new InvalidOperationException($"Cannot coerce type {Kind} to Word."),
        };

        /// <summary>A float as the bits of a word (the native runtime: wordOfReal): rounded half to even; NaN is 0; below -2^63 it is -2^63, from 2^64 on all ones;
        /// a negative number in two's complement.</summary>
        public static ulong DoubleToWordBits(double f)
        {
            if (double.IsNaN(f)) return 0;
            double r = Math.Round(f);
            if (r >= 18446744073709551616.0) return ulong.MaxValue;
            if (r >= 9223372036854775808.0) return (ulong)r;
            if (r <= -9223372036854775808.0) return 0x8000000000000000UL;
            return unchecked((ulong)(long)r);
        }

        /// <summary>The unsigned value of a word as a double (rounded to float32 with `#floatwidth 32`).</summary>
        public static double WordToDouble(ulong w) => SingleFloats ? (double)(float)w : (double)w;

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
                    NumericWidth.W32 or NumericWidth.W16 or NumericWidth.W8 => RoundFloatToWidth(_floatValue, (int)width),
                    _ => _floatValue,
                };
                return MakeFloat(truncated, Unit, width);
            }

            throw new InvalidOperationException($"TruncateTo is only valid for int/float, not {Kind}.");
        }

        /// <summary>A float rounded to a small float format (round to nearest, ties to even) - the native runtime does exactly the same (roundFloatToWidth in
        /// fire_rt.h): 32 = IEEE binary32; 16 = IEEE binary16 (half precision: 5 exponent bits, 10 mantissa bits, largest value 65504, subnormals down to
        /// 2^-24, a larger value becomes infinity); 8 = FP8 E4M3 (OCP 8-bit floating point, "E4M3FN": 4 exponent bits with bias 7, 3 mantissa bits, largest
        /// value 448, subnormals down to 2^-9, no infinity - a larger value and infinity saturate to +-448, NaN stays NaN). The result is the stored value as a
        /// double.</summary>
        public static double RoundFloatToWidth(double value, int bits)
        {
            if (bits == 32) return (float)value;
            int mantissaBits = bits == 16 ? 10 : 3, minExponent = bits == 16 ? -14 : -6;
            double max = bits == 16 ? 65504.0 : 448.0;
            if (double.IsNaN(value) || value == 0) return value;
            double abs = Math.Abs(value);
            if (double.IsInfinity(abs)) return bits == 16 ? value : Math.CopySign(max, value);
            int exponent = Math.Max(Math.ILogB(abs), minExponent);
            double quantum = Math.ScaleB(1.0, exponent - mantissaBits);     // the distance between two neighbours at this exponent (a power of two: exact)
            double steps = abs / quantum, whole = Math.Floor(steps), rest = steps - whole;
            if (rest > 0.5 || rest == 0.5 && whole % 2 == 1) whole += 1;
            double rounded = whole * quantum;
            if (rounded > max) rounded = bits == 16 ? double.PositiveInfinity : max;
            return Math.CopySign(rounded, value);
        }

        /// <summary>Structural equality for '=='/'!=' - works across kinds
        /// (e.g. Bool vs Int simply yields false, no error).</summary>
        public static bool ValuesEqual(Value a, Value b) => a.Equals(b);

        /// <summary>The operators `==`/`!=`: numbers compare by value - an `int` and a `float` alike (`1 == 1.0`), and units of the same dimension are converted
        /// like for `<` (`1m == 100cm`); a number with a unit of another dimension (or a unitless one) is compared as written (`5cm == 5`, the type checker
        /// reports it, TC006). Everything else as <see cref="ValuesEqual"/>.</summary>
        public static bool OperatorEquals(Value a, Value b)
        {
            // a word and a number: by value (an int taken as a word, `w == -1` is true for the largest word; a float by its value)
            if (AnyWord(a, b) && a.Kind is ValueKind.Word or ValueKind.Int or ValueKind.Float && b.Kind is ValueKind.Word or ValueKind.Int or ValueKind.Float)
            {
                if (a.Kind == ValueKind.Float || b.Kind == ValueKind.Float)
                    return (a.Kind == ValueKind.Word ? WordToDouble(a.AsWord()) : a.ToDouble()) == (b.Kind == ValueKind.Word ? WordToDouble(b.AsWord()) : b.ToDouble());
                return (unchecked((ulong)a._bits) & WordMask) == (unchecked((ulong)b._bits) & WordMask);
            }
            // (two records: by their content - step 9)
            if (a.Kind == ValueKind.Class && b.Kind == ValueKind.Class && fire.Runtime.RecordValues.Equal(a, b)) return true;
            if (a.Kind is not (ValueKind.Int or ValueKind.Float) || b.Kind is not (ValueKind.Int or ValueKind.Float)) return a.Equals(b);
            if (!ReferenceEquals(a._ref, b._ref))
            {
                var ua = a.Unit ?? Values.Unit.Unitless;
                var ub = b.Unit ?? Values.Unit.Unitless;
                if (!ua.Equals(ub) && ua.IsCompatibleWith(ub)) AlignUnits(ref a, ref b);
            }
            if (a.Kind == ValueKind.Int && b.Kind == ValueKind.Int) return a._intValue == b._intValue;
            return a.ToDouble() == b.ToDouble();
        }

        /// <summary>For '&lt;'/'&lt;='/'&gt;'/'&gt;=' - only for numeric values with
        /// matching unit (like Add/Subtract).</summary>
        public static int Compare(Value a, Value b)
        {
            if (BothNumericSameUnit(a, b))
                return a.ToDouble().CompareTo(b.ToDouble());
            if (a.Kind == ValueKind.Char && b.Kind == ValueKind.Char) return a.AsChar().CompareTo(b.AsChar());   // characters by their code
            if (a.Kind == ValueKind.String && b.Kind == ValueKind.String) return Math.Sign(string.CompareOrdinal(a.AsString(), b.AsString()));   // texts by their codes
            if (AnyWord(a, b)) return CompareWord(a, b);

            RequireNumeric(a); RequireNumeric(b);
            AlignUnits(ref a, ref b);
            return a.ToDouble().CompareTo(b.ToDouble());
        }

        /// <summary>The step of `x++`/`x--` (`one` is the constant 1): in the unit of `target`, so that `5cm` becomes `6cm` (a unitless 1 would be a unit mismatch).</summary>
        public static Value StepOf(in Value target, Value one)
        {
            if (target.Kind is not (ValueKind.Int or ValueKind.Float)) return one;
            var unit = target.Unit;
            if (unit == null || unit.IsUnitless) return one;
            return one.Kind == ValueKind.Float ? MakeFloat(one.AsFloat(), unit) : MakeInt(one.AsInt(), unit);
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

            if (!ua.IsLinear || !ub.IsLinear)
            {
                // an offset or conversion functions: the right operand goes into the unit of the left one (`20C + 9F`)
                double converted = ub.Convert(b.Kind == ValueKind.Int ? b._intValue : b._floatValue, ua);
                b = a.Kind == ValueKind.Int && b.Kind == ValueKind.Int ? MakeInt((long)Math.Round(converted), ua) : MakeFloat(converted, ua);
                return;
            }

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
                    coarse = MakeInt((long)SnapWhole(up), uFine);            // 2m -> 2000mm
                }
                else
                {
                    double down = fine._intValue * uFine.ConversionFactorTo(uCoarse);
                    fine = MakeInt((long)SnapWhole(down), uCoarse);          // 500mm -> 0m (fraction cut off)
                }
                return;
            }

            double factor = ub.ConversionFactorTo(ua);
            b = b.Kind == ValueKind.Int ? MakeFloat(b._intValue * factor, ua) : MakeFloat(b._floatValue * factor, ua);
        }

        /// <summary>A converted whole number: a value a rounding error away from a whole number is that number (1ft is 11.999999999999998in by the
        /// factors), any other fraction is cut off.</summary>
        internal static double SnapWhole(double x)
        {
            double r = Math.Round(x);
            return Math.Abs(x - r) <= 1e-9 * Math.Max(1.0, Math.Abs(x)) ? r : Math.Truncate(x);
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
                case ValueKind.Word:
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
            ValueKind.Int or ValueKind.Bool or ValueKind.Char or ValueKind.Word => HashCode.Combine(Kind, _bits),
            ValueKind.Float => HashCode.Combine(Kind, _floatValue),
            ValueKind.Undefined => HashCode.Combine(Kind),
            _ => HashCode.Combine(Kind, _ref),
        };

        // single floats print their own shortest representation (0.1f is "0.1", not "0.10000000149011612")
        private string FloatText() => SingleFloats ? ((float)_floatValue).ToString() : _floatValue.ToString();

        public override string ToString() => Kind switch
        {
            ValueKind.Bool => _boolValue.ToString(),
            ValueKind.Int => _enum != 0 ? (Enum?.Format(_intValue) ?? _intValue.ToString()) : Unit is { IsUnitless: false } u ? $"{_intValue}{u}" : _intValue.ToString(),
            ValueKind.Float => Unit is { IsUnitless: false } u2 ? $"{FloatText()}{u2}" : FloatText(),
            ValueKind.Char => _charValue.ToString(),
            ValueKind.String => Unsafe.As<string>(_ref) ?? "",
            ValueKind.Class => $"<object {(_ref as fire.Runtime.ObjectInstance)?.ClassName}>",
            ValueKind.Lambda => "<lambda>",
            ValueKind.Pointer => "<pointer>",
            ValueKind.Array => "<array>",
            ValueKind.Buffer => AsBuffer() is { IsWords: true } words ? $"<buffer {words.Count} words>" : $"<buffer {AsBuffer().Length} bytes>",
            ValueKind.Undefined => Unit is { IsUnitless: false } u3 ? $"undefined:{u3}" : "undefined",
            ValueKind.Word => unchecked((ulong)_bits).ToString(CultureInfo.InvariantCulture),   // (unsigned)
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
                    RequireFormatKind(spec, ValueKind.Int, ValueKind.Word);
                    return Kind == ValueKind.Word ? unchecked((ulong)_bits).ToString(spec, CultureInfo.InvariantCulture) : _intValue.ToString(spec, CultureInfo.InvariantCulture);

                case 'B':
                {
                    RequireFormatKind(spec, ValueKind.Int, ValueKind.Word);
                    string bin = System.Convert.ToString(_intValue, 2);
                    return rest.Length > 0 && int.TryParse(rest, out int width)
                        ? bin.PadLeft(width, '0')
                        : bin;
                }

                case 'F':
                case 'E':
                    RequireFormatKind(spec, ValueKind.Int, ValueKind.Float, ValueKind.Word);
                    return (Kind == ValueKind.Word ? WordToDouble(unchecked((ulong)_bits)) : ToDouble()).ToString(spec, CultureInfo.InvariantCulture);

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
