using System;
using fire.Values;

namespace fire.Standard
{
    /// <summary>A position or a range outside a buffer (the VM throws it as the IndexOutOfBoundsException of the program).</summary>
    public sealed class BufferRangeException : InvalidOperationException
    {
        public long Index { get; }
        public int Length { get; }
        public BufferRangeException(long index, int length) : base($"Buffer index {index} out of range (length {length}).") { Index = index; Length = length; }
    }

    /// <summary>
    /// The typed reads and writes and the fast operations of a byte buffer (docs/language/07-arrays-and-lists.md "Reading and writing numbers"):
    /// `ReadInt16(at)`, `WriteFloat32(at, x)`, ... in the byte order of the buffer (`littleEndian`) or the one given (`ReadInt32(at, false)`: big endian),
    /// `Fill`, `CopyTo`, `Slice`, `IndexOf`, `Compare`. The native backend has the same (fire_rt.h bufRead/bufWrite/...). Not for a buffer of words.
    /// </summary>
    public static class BufferMethods
    {
        /// <summary>The kinds of a typed access: its size in bytes, signed, a float.</summary>
        private static (int Size, bool Signed, bool Float)? KindOf(string name) => name switch
        {
            "Int8" => (1, true, false), "UInt8" => (1, false, false),
            "Int16" => (2, true, false), "UInt16" => (2, false, false),
            "Int32" => (4, true, false), "UInt32" => (4, false, false),
            "Int64" => (8, true, false),
            "Float16" => (2, false, true), "Float32" => (4, false, true), "Float64" => (8, false, true),
            _ => null,
        };

        /// <summary>Is `name` with `argc` arguments a method of this table (BuiltinMembers, the editor)?</summary>
        public static bool Has(string name) =>
            name.StartsWith("Read", StringComparison.Ordinal) && KindOf(name.Substring(4)) != null
            || name.StartsWith("Write", StringComparison.Ordinal) && KindOf(name.Substring(5)) != null
            || name is "Fill" or "CopyTo" or "Slice" or "IndexOf" or "Compare";

        public static bool TryCall(ByteBuffer buf, string name, Value[] args, out Value result)
        {
            result = Value.MakeUndefined();
            if (name.StartsWith("Read", StringComparison.Ordinal) && KindOf(name.Substring(4)) is { } rk && args.Length is 1 or 2)
            {
                Plain(buf, name);
                long at = Int(args[0], name, "position");
                bool little = Order(buf, args.Length == 2 ? args[1] : Value.MakeUndefined(), name);
                Range(buf, at, rk.Size);
                ulong raw = Get(buf.Bytes, (int)at, rk.Size, little);
                result = rk.Float
                    ? Value.MakeFloat(rk.Size == 2 ? BitsMethods.HalfValue((ushort)raw) : rk.Size == 4 ? BitConverter.UInt32BitsToSingle((uint)raw) : BitConverter.Int64BitsToDouble((long)raw))
                    : Value.MakeNativeInt(rk.Signed && rk.Size < 8 ? SignExtend(raw, rk.Size) : (long)raw);
                return true;
            }
            if (name.StartsWith("Write", StringComparison.Ordinal) && KindOf(name.Substring(5)) is { } wk && args.Length is 2 or 3)
            {
                Plain(buf, name);
                long at = Int(args[0], name, "position");
                bool little = Order(buf, args.Length == 3 ? args[2] : Value.MakeUndefined(), name);
                var x = args[1];
                ulong raw;
                if (wk.Float)
                {
                    if (x.Kind is not (ValueKind.Int or ValueKind.Float)) throw new InvalidOperationException($"buffer.{name} expects a number, not a value of type '{x.Kind}'.");
                    NoUnit(x, name);
                    double d = x.Kind == ValueKind.Int ? x.AsInt() : x.AsFloat();
                    raw = wk.Size == 2 ? BitsMethods.HalfBits(d) : wk.Size == 4 ? (double.IsNaN(d) ? 0x7FC00000u : BitConverter.SingleToUInt32Bits((float)d)) : (ulong)(double.IsNaN(d) ? 0x7FF8000000000000L : BitConverter.DoubleToInt64Bits(d));
                }
                else
                {
                    if (x.Kind is not (ValueKind.Int or ValueKind.Word)) throw new InvalidOperationException($"buffer.{name} expects an int, not a value of type '{x.Kind}'.");
                    if (x.Kind == ValueKind.Int) NoUnit(x, name);
                    raw = x.Kind == ValueKind.Word ? x.AsWord() : (ulong)x.AsInt();
                }
                Range(buf, at, wk.Size);
                Put(buf.Bytes, (int)at, wk.Size, little, raw);
                return true;
            }
            switch (name)
            {
                case "Fill" when args.Length is 1 or 3:
                {
                    Plain(buf, name);
                    long value = Int(args[0], name, "value");
                    var (start, count) = args.Length == 3 ? (Int(args[1], name, "start"), Int(args[2], name, "count")) : (0L, (long)buf.Length);
                    Span(buf, start, count);
                    buf.Bytes.AsSpan((int)start, (int)count).Fill((byte)value);
                    return true;
                }
                case "CopyTo" when args.Length is 2 or 4:
                {
                    Plain(buf, name);
                    if (args[0].Kind != ValueKind.Buffer) throw new InvalidOperationException($"buffer.CopyTo expects a byte buffer to copy into, not a value of type '{args[0].Kind}'.");
                    var target = args[0].AsBuffer();
                    Plain(target, name);
                    long targetAt = Int(args[1], name, "position");
                    var (start, count) = args.Length == 4 ? (Int(args[2], name, "start"), Int(args[3], name, "count")) : (0L, (long)buf.Length);
                    Span(buf, start, count);
                    Span(target, targetAt, count);
                    Array.Copy(buf.Bytes, (int)start, target.Bytes, (int)targetAt, (int)count);   // (overlapping parts of the same buffer: copied as if through a copy)
                    return true;
                }
                case "Slice" when args.Length is 1 or 2:
                {
                    Plain(buf, name);
                    long start = Int(args[0], name, "start");
                    long count = args.Length == 2 ? Int(args[1], name, "count") : buf.Length - start;
                    Span(buf, start, count);
                    result = Value.MakeBuffer(new ByteBuffer(buf.Bytes.AsSpan((int)start, (int)count).ToArray(), buf.Order));
                    return true;
                }
                case "IndexOf" when args.Length is 1 or 2:
                {
                    Plain(buf, name);
                    long start = args.Length == 2 ? Int(args[1], name, "start") : 0;
                    if (start < 0 || start > buf.Length) throw new BufferRangeException(start, buf.Length);
                    var rest = buf.Bytes.AsSpan((int)start);
                    int found;
                    if (args[0].Kind == ValueKind.Buffer)
                    {
                        var needle = args[0].AsBuffer();
                        Plain(needle, name);
                        found = rest.IndexOf(needle.Bytes);
                    }
                    else
                    {
                        long b = Int(args[0], name, "value");
                        found = b is < 0 or > 255 ? -1 : rest.IndexOf((byte)b);
                    }
                    result = Value.MakeInt(found < 0 ? -1 : found + start);
                    return true;
                }
                case "Compare" when args.Length == 1:
                {
                    Plain(buf, name);
                    if (args[0].Kind != ValueKind.Buffer) throw new InvalidOperationException($"buffer.Compare expects a byte buffer, not a value of type '{args[0].Kind}'.");
                    var other = args[0].AsBuffer();
                    Plain(other, name);
                    int c = buf.Bytes.AsSpan().SequenceCompareTo(other.Bytes);
                    result = Value.MakeInt(Math.Sign(c));
                    return true;
                }
            }
            return false;
        }

        private static void Plain(ByteBuffer buf, string name)
        {
            if (buf.IsWords) throw new InvalidOperationException($"buffer.{name} works on bytes, not a word buffer.");
        }

        private static long Int(Value v, string name, string what)
        {
            if (v.Kind != ValueKind.Int) throw new InvalidOperationException($"buffer.{name} expects an int {what}, not a value of type '{v.Kind}'.");
            return v.AsInt();
        }

        private static void NoUnit(Value v, string name)
        {
            if (v.Unit is { IsUnitless: false } unit) throw new InvalidOperationException($"buffer.{name} expects a number without a unit, not one in '{unit}'.");
        }

        /// <summary>The byte order of an access: the one given (true: little endian), else the buffer's.</summary>
        private static bool Order(ByteBuffer buf, Value order, string name)
        {
            if (order.Kind == ValueKind.Undefined) return buf.Order == ByteOrder.Little;
            if (order.Kind != ValueKind.Bool) throw new InvalidOperationException($"buffer.{name} expects a bool for the byte order (true: little endian), not a value of type '{order.Kind}'.");
            return order.AsBool();
        }

        private static void Range(ByteBuffer buf, long at, int size)
        {
            if (at < 0 || at > buf.Length - size) throw new BufferRangeException(at, buf.Length);
        }

        private static void Span(ByteBuffer buf, long start, long count)
        {
            if (start < 0 || start > buf.Length) throw new BufferRangeException(start, buf.Length);
            if (count < 0 || count > buf.Length - start) throw new BufferRangeException(start + count, buf.Length);
        }

        private static ulong Get(byte[] b, int at, int size, bool little)
        {
            ulong r = 0;
            for (int i = 0; i < size; i++) r |= (ulong)b[at + (little ? i : size - 1 - i)] << (8 * i);
            return r;
        }

        private static void Put(byte[] b, int at, int size, bool little, ulong raw)
        {
            for (int i = 0; i < size; i++) b[at + (little ? i : size - 1 - i)] = (byte)(raw >> (8 * i));
        }

        private static long SignExtend(ulong raw, int size)
        {
            int shift = 64 - 8 * size;
            return (long)(raw << shift) >> shift;
        }
    }
}
