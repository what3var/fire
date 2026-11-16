using System;
using System.Runtime.InteropServices;
using fire.Values;

namespace fire.Standard
{
    /// <summary>The operations of <see cref="StringBuilderMethods.NativeName"/> (fixed values - never renumber; fire_rt.h: FIRE_SB_*).</summary>
    public enum StringBuilderOp
    {
        Append = 1,
        Move = 2,
        Length = 3,
        Clear = 4,
        Text = 5,
    }

    /// <summary>
    /// `__SbCall(op, data, value)`: the `StringBuilder` of the prelude (StringMethods.StringBuilderSource). Its text lives in ONE byte buffer `data` of the
    /// object - <see cref="Head"/> bytes (the number of characters as a uint32, the rest reserved), then the characters (UTF-16, the byte order of the
    /// machine) - instead of one heap string per piece. The buffer is an ordinary one (copies, handover and the debugger see bytes) and never grows in place:
    /// when a text does not fit, Append says how big the next buffer must be and the prelude replaces it. The native backend has the same (fire_rt.h sbCall).
    ///   Append: 0 - appended; &gt; 0 - nothing appended: the bytes of a buffer that takes it (at least twice the current one); -1 - nothing appended: a
    ///           value whose text is not simply Value.ToString() (an object: its ToString() is a call of the program) - the prelude passes `"" + value`.
    ///   Move:   `value` is the old buffer: its text into `data` (a new, bigger one).
    /// </summary>
    public static class StringBuilderMethods
    {
        public const string NativeName = "__SbCall";
        public const int Head = 8;
        private const long MaxBytes = 0x7FFFFFF0;

        public static Value Call(Value[] args)
        {
            if (args.Length is < 2 or > 3 || args[0].Kind != ValueKind.Int) throw Bad();
            byte[] bytes = BytesOf(args[1], out int length);
            switch ((StringBuilderOp)args[0].AsInt())
            {
                case StringBuilderOp.Append:
                {
                    if (args.Length != 3) throw Bad();
                    var value = args[2];
                    var chars = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(Head));
                    ReadOnlySpan<char> text;
                    switch (value.Kind)
                    {
                        case ValueKind.Int when !value.IsEnum && value.Unit is null or { IsUnitless: true }:
                            // (a plain whole number - the common case: its digits straight into the buffer, no string)
                            if (value.AsInt().TryFormat(chars.Slice(length), out int written, default, System.Globalization.CultureInfo.InvariantCulture))
                            {
                                WriteLength(bytes, length + written);
                                return Value.MakeInt(0);
                            }
                            text = value.ToString();
                            break;
                        case ValueKind.String:
                            text = value.AsString();
                            break;
                        case ValueKind.Int or ValueKind.Float or ValueKind.Bool or ValueKind.Char or ValueKind.Word or ValueKind.Undefined:
                            text = value.ToString();
                            break;
                        default:
                            return Value.MakeInt(-1);
                    }
                    long need = Head + ((long)length + text.Length) * sizeof(char);
                    if (need > bytes.Length)
                    {
                        if (need > MaxBytes) throw new InvalidOperationException("The text of a StringBuilder is too long.");
                        return Value.MakeNativeInt(Math.Max(need, Math.Min(MaxBytes, bytes.Length * 2L)));
                    }
                    text.CopyTo(chars.Slice(length));
                    WriteLength(bytes, length + text.Length);
                    return Value.MakeInt(0);
                }
                case StringBuilderOp.Move:
                {
                    if (args.Length != 3) throw Bad();
                    byte[] old = BytesOf(args[2], out int oldLength);
                    int used = Head + oldLength * sizeof(char);
                    if (used > bytes.Length) throw Bad();
                    if (!ReferenceEquals(old, bytes)) Buffer.BlockCopy(old, 0, bytes, 0, used);
                    return Value.MakeUndefined();
                }
                case StringBuilderOp.Length:
                    return Value.MakeInt(length);
                case StringBuilderOp.Clear:
                    WriteLength(bytes, 0);
                    return Value.MakeUndefined();
                case StringBuilderOp.Text:
                    return Value.MakeString(new string(MemoryMarshal.Cast<byte, char>(bytes.AsSpan(Head, length * sizeof(char)))));
                default:
                    throw Bad();
            }
        }

        /// <summary>The bytes of the buffer of a StringBuilder and the number of characters in it.</summary>
        private static byte[] BytesOf(Value data, out int length)
        {
            if (data.Kind != ValueKind.Buffer) throw Bad();
            var buffer = data.AsBuffer();
            if (buffer.IsDestroyed) throw new InvalidOperationException("Access to a destroyed buffer.");
            byte[] bytes = buffer.Bytes;
            if (buffer.IsWords || bytes.Length < Head) throw Bad();
            uint n = MemoryMarshal.Read<uint>(bytes);
            if (n > (uint)((bytes.Length - Head) / sizeof(char))) throw Bad();
            length = (int)n;
            return bytes;
        }

        private static void WriteLength(byte[] bytes, int length) => MemoryMarshal.Write(bytes.AsSpan(0, sizeof(uint)), (uint)length);

        private static InvalidOperationException Bad() => new($"{NativeName} expects the buffer of a StringBuilder.");
    }
}
