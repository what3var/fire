using System;
using fire.Values;

namespace fire.Standard
{
    /// <summary>The functions of the class `Bits` that need the host (IDs for <see cref="BitsMethods.NativeName"/>, fixed values - never renumber).</summary>
    public enum BitsMethod
    {
        PopCount = 1,
        LeadingZeros = 2,
        TrailingZeros = 3,
        RotateLeft = 4,
        RotateRight = 5,
        ReverseBytes = 6,
        ReverseBits = 7,
        FromFloat64 = 8,
        ToFloat64 = 9,
        FromFloat32 = 10,
        ToFloat32 = 11,
        FromFloat16 = 12,
        ToFloat16 = 13,
    }

    /// <summary>
    /// The class `Bits` of the prelude (always there, docs/language/03-operators.md "Bit functions"): counting, rotating and reversing the bits of an int or a
    /// word, the IEEE bits of a float (64, 32 and 16 bits: half precision) and back. They go through ONE native function `__BitsCall(id, x[, y[, z]])`, which the
    /// native backend implements alike (fire_rt.h: bitsCall); `Extract`/`Insert` are written in fire.
    ///
    /// The width: an int is counted in the width of the program's int (64; 32 or 16 with `#intwidth`), a word in its width (`Word.Bits`); `width` (1 to 64)
    /// takes another one - the low `width` bits. A result in the program's width is an int of the program (rotating -1 gives -1); one of a smaller width is
    /// the unsigned number of those bits. A number that does not fit the program's int keeps its 64 bits (like the result of a native).
    /// </summary>
    public static class BitsMethods
    {
        public const string NativeName = "__BitsCall";

        public static string PreludeSource { get; } = $$"""

            /// Bit functions (always available): counting, rotating and reversing the bits of an int or a word, the IEEE bits of a float. `width` (1 to 64)
            /// works on the low bits of that width; without it the width of the program's int (64, or 32/16 with #intwidth) - of a word its width.
            class Bits {
                /// The number of bits that are 1.
                static int PopCount(any x, int width = 0) { return {{NativeName}}({{(int)BitsMethod.PopCount}}, x, width) }
                /// The number of 0 bits above the highest 1 (the width for 0).
                static int LeadingZeros(any x, int width = 0) { return {{NativeName}}({{(int)BitsMethod.LeadingZeros}}, x, width) }
                /// The number of 0 bits below the lowest 1 (the width for 0).
                static int TrailingZeros(any x, int width = 0) { return {{NativeName}}({{(int)BitsMethod.TrailingZeros}}, x, width) }
                /// The bits rotated left by `n` (what leaves at the top comes in at the bottom).
                static any RotateLeft(any x, int n, int width = 0) { return {{NativeName}}({{(int)BitsMethod.RotateLeft}}, x, n, width) }
                /// The bits rotated right by `n`.
                static any RotateRight(any x, int n, int width = 0) { return {{NativeName}}({{(int)BitsMethod.RotateRight}}, x, n, width) }
                /// The bytes in the opposite order (the width a multiple of 8: 16, 32, 64).
                static any ReverseBytes(any x, int width = 0) { return {{NativeName}}({{(int)BitsMethod.ReverseBytes}}, x, width) }
                /// The bits in the opposite order.
                static any ReverseBits(any x, int width = 0) { return {{NativeName}}({{(int)BitsMethod.ReverseBits}}, x, width) }
                /// `count` bits of `x` from bit `start` on (0 is the lowest), as an unsigned number.
                static int Extract(int x, int start, int count) {
                    if (start < 0 || count < 0 || start + count > 64) { throw new ArgumentException("Bits.Extract: start " + start + " and count " + count + " do not fit 64 bits.") }
                    if (count == 0) { return 0 }
                    if (count >= 64) { return x >>> start }
                    return (x >>> start) & ((1 << count) - 1)
                }
                /// `x` with `count` bits from bit `start` on replaced by the low bits of `value`.
                static int Insert(int x, int value, int start, int count) {
                    if (start < 0 || count < 0 || start + count > 64) { throw new ArgumentException("Bits.Insert: start " + start + " and count " + count + " do not fit 64 bits.") }
                    if (count == 0) { return x }
                    int mask = count >= 64 ? -1 : (1 << count) - 1
                    return (x & ~(mask << start)) | ((value & mask) << start)
                }
                /// The 64 bits of a float (IEEE binary64) as an int of 64 bits (also with #intwidth 32); NaN gives the bits of the standard NaN.
                static int[64] FromFloat64(any f) { return {{NativeName}}({{(int)BitsMethod.FromFloat64}}, f) }
                /// The float whose 64 bits (IEEE binary64) are `bits`.
                static float ToFloat64(any bits) { return {{NativeName}}({{(int)BitsMethod.ToFloat64}}, bits) }
                /// The 32 bits of a float rounded to IEEE binary32 (0 to 2^32 - 1, an int of 64 bits).
                static int[64] FromFloat32(any f) { return {{NativeName}}({{(int)BitsMethod.FromFloat32}}, f) }
                /// The float whose 32 bits (IEEE binary32) are the low 32 bits of `bits`.
                static float ToFloat32(any bits) { return {{NativeName}}({{(int)BitsMethod.ToFloat32}}, bits) }
                /// The 16 bits of a float rounded to half precision (IEEE binary16: largest value 65504, a larger one is infinity).
                static int FromFloat16(any f) { return {{NativeName}}({{(int)BitsMethod.FromFloat16}}, f) }
                /// The float whose 16 bits (IEEE binary16) are the low 16 bits of `bits`.
                static float ToFloat16(any bits) { return {{NativeName}}({{(int)BitsMethod.ToFloat16}}, bits) }
            }
            """;

        /// <summary>The width of the program's int for the int `x` (a value that keeps a wider width keeps it, Value.Wide).</summary>
        public static int IntBits(Value x) => x.Wide >= 64 ? 64 : x.Wide == 32 ? 32 : !Value.Ints32 ? 64 : Value.Ints16 ? 16 : 32;

        /// <summary>The native function `__BitsCall(id, x[, y[, z]])`.</summary>
        public static Value Call(Value[] args)
        {
            if (args.Length is < 2 or > 4) throw new InvalidOperationException($"{NativeName}(id, x[, y[, z]]) expects one to three values.");
            var method = (BitsMethod)args[0].AsInt();
            var x = args[1];
            Value Arg(int i) => i < args.Length ? args[i] : Value.MakeUndefined();
            switch (method)
            {
                case BitsMethod.FromFloat64:
                {
                    double d = Number(x, "FromFloat64");
                    return Value.MakeNativeInt(double.IsNaN(d) ? 0x7FF8000000000000L : BitConverter.DoubleToInt64Bits(d));
                }
                case BitsMethod.ToFloat64: return Value.MakeFloat(BitConverter.Int64BitsToDouble((long)Raw(x, "ToFloat64")));
                case BitsMethod.FromFloat32:
                {
                    double d = Number(x, "FromFloat32");
                    return Value.MakeNativeInt(double.IsNaN(d) ? 0x7FC00000L : BitConverter.SingleToUInt32Bits((float)d));
                }
                case BitsMethod.ToFloat32: return Value.MakeFloat(BitConverter.UInt32BitsToSingle((uint)Raw(x, "ToFloat32")));
                case BitsMethod.FromFloat16: return Value.MakeNativeInt(HalfBits(Number(x, "FromFloat16")));
                case BitsMethod.ToFloat16: return Value.MakeFloat(HalfValue((ushort)Raw(x, "ToFloat16")));
            }
            string name = method.ToString();
            bool rotate = method is BitsMethod.RotateLeft or BitsMethod.RotateRight;
            var (bits, w, word, dflt) = Operand(x, rotate ? Arg(3) : Arg(2), name);
            switch (method)
            {
                case BitsMethod.PopCount: return Value.MakeInt(System.Numerics.BitOperations.PopCount(bits));
                case BitsMethod.LeadingZeros: return Value.MakeInt(bits == 0 ? w : System.Numerics.BitOperations.LeadingZeroCount(bits) - (64 - w));
                case BitsMethod.TrailingZeros: return Value.MakeInt(bits == 0 ? w : System.Numerics.BitOperations.TrailingZeroCount(bits));
                case BitsMethod.RotateLeft: case BitsMethod.RotateRight:
                {
                    var n = Arg(2);
                    if (n.Kind != ValueKind.Int) throw new InvalidOperationException($"Bits.{name} expects an int count, not a value of type '{n.Kind}'.");
                    int s = (int)(((n.AsInt() % w) + w) % w);
                    if (method == BitsMethod.RotateRight) s = (w - s) % w;
                    ulong r = s == 0 ? bits : (bits << s | bits >> (w - s)) & Mask(w);
                    return Result(r, w, word, dflt);
                }
                case BitsMethod.ReverseBytes:
                {
                    if (w % 8 != 0) throw new InvalidOperationException($"Bits.ReverseBytes: the width must be a multiple of 8, not {w}.");
                    ulong r = 0;
                    for (int i = 0; i < w / 8; i++) r = r << 8 | (bits >> (8 * i) & 0xFF);
                    return Result(r, w, word, dflt);
                }
                case BitsMethod.ReverseBits:
                {
                    ulong r = 0;
                    for (int i = 0; i < w; i++) r = r << 1 | (bits >> i & 1);
                    return Result(r, w, word, dflt);
                }
                default: throw new InvalidOperationException($"{NativeName}: unknown function {(int)method}.");
            }
        }

        private static ulong Mask(int w) => w >= 64 ? ulong.MaxValue : (1UL << w) - 1;

        /// <summary>The bits of `x` (an int or a word) in the width `width` (0 or undefined: the program's int, a word's width): the low bits, the width,
        /// whether it is a word, whether the width is the default one.</summary>
        private static (ulong Bits, int Width, bool Word, bool Default) Operand(Value x, Value width, string name)
        {
            bool word = x.Kind == ValueKind.Word;
            if (!word && x.Kind != ValueKind.Int) throw new InvalidOperationException($"Bits.{name} expects an int or a word, not a value of type '{x.Kind}'.");
            int dflt = word ? Value.WordBits : IntBits(x);
            if (width.Kind is not (ValueKind.Int or ValueKind.Undefined)) throw new InvalidOperationException($"Bits.{name} expects an int width, not a value of type '{width.Kind}'.");
            int w = width.Kind == ValueKind.Undefined || width.AsInt() == 0 ? dflt : (int)Math.Clamp(width.AsInt(), -1, 65);
            if (w is < 1 or > 64) throw new InvalidOperationException($"Bits.{name}: the width must be 1 to 64, not {width.AsInt()}.");
            ulong raw = word ? x.AsWord() : (ulong)x.AsInt();
            return (raw & Mask(w), w, word, w == dflt);
        }

        /// <summary>The bits of a result: a word, the program's int (in its width: sign-extended), or the unsigned number of a smaller width.</summary>
        private static Value Result(ulong bits, int w, bool word, bool dflt)
        {
            if (word) return Value.MakeWord(bits);
            if (dflt && w < 64 && (bits >> (w - 1) & 1) != 0) return Value.MakeNativeInt((long)(bits | ~Mask(w)));
            return Value.MakeNativeInt((long)bits);
        }

        /// <summary>A number without a unit as a double (an int converted).</summary>
        private static double Number(Value x, string name)
        {
            if (x.Kind is not (ValueKind.Int or ValueKind.Float)) throw new InvalidOperationException($"Bits.{name} expects a number, not a value of type '{x.Kind}'.");
            if (x.Unit is { IsUnitless: false } unit) throw new InvalidOperationException($"Bits.{name} expects a number without a unit, not one in '{unit}'.");
            return x.Kind == ValueKind.Int ? x.AsInt() : x.AsFloat();
        }

        /// <summary>The bits of an int or a word (an int in two's complement).</summary>
        private static ulong Raw(Value x, string name) => x.Kind switch
        {
            ValueKind.Int => (ulong)x.AsInt(),
            ValueKind.Word => x.AsWord(),
            _ => throw new InvalidOperationException($"Bits.{name} expects an int or a word, not a value of type '{x.Kind}'."),
        };

        /// <summary>A double as the 16 bits of IEEE binary16 (round to nearest, ties to even - Value.RoundFloatToWidth; NaN: 0x7E00). The same steps as
        /// fire_rt.h halfBits, so that the VM and a native build agree bit for bit.</summary>
        public static ushort HalfBits(double d)
        {
            if (double.IsNaN(d)) return 0x7E00;
            uint sign = BitConverter.DoubleToInt64Bits(d) < 0 ? 0x8000u : 0;
            double r = Value.RoundFloatToWidth(Math.Abs(d), 16);
            if (double.IsInfinity(r)) return (ushort)(sign | 0x7C00);
            if (r == 0) return (ushort)sign;
            int e = Math.ILogB(r);
            if (e < -14) return (ushort)(sign | (uint)Math.ScaleB(r, 24));
            uint mant = (uint)((Math.ScaleB(r, -e) - 1.0) * 1024.0);
            return (ushort)(sign | (uint)(e + 15) << 10 | mant);
        }

        /// <summary>The 16 bits of IEEE binary16 as a double (exact).</summary>
        public static double HalfValue(ushort h)
        {
            int exp = h >> 10 & 31, mant = h & 1023;
            double v = exp == 0 ? Math.ScaleB(mant, -24) : exp == 31 ? (mant == 0 ? double.PositiveInfinity : double.NaN) : Math.ScaleB(1.0 + mant / 1024.0, exp - 15);
            return (h & 0x8000) != 0 ? -v : v;
        }
    }
}
