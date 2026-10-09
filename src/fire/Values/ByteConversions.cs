namespace fire.Values
{
    /// <summary>ASCII/"Unicode" conversions between string/char and
    /// ByteBuffer (see SPEC 8.10). Deliberately ONLY the simplest building blocks
    /// (arrays, loops, bit shifts) - no System.Text.Encoding, no
    /// real Unicode library (normalisation, surrogate pairs, etc.): "ASCII"
    /// here literally means 1 byte per character (the lowest byte of the
    /// char value, everything above is lost), "Unicode" a fixed
    /// width of N bytes per character (N freely selectable, typically 2 -
    /// then corresponds to UTF-16/Windows "Unicode" for characters in the BMP, without
    /// treating surrogate pairs separately, since a language `char` here
    /// is only a single 16-bit code unit anyway, not a full
    /// code-point concept). This simplicity is INTENTIONAL: the class is meant to
    /// be transferable 1:1 into a C++ VM later.</summary>
    public static class ByteConversions
    {
        /// <summary>Little or big endian of the HOST architecture - determined
        /// at runtime by a test (no compile flag, no assumption
        /// about the target platform): a multi-byte value is written, then
        /// checked whether the LEAST significant byte comes FIRST in memory.
        /// BitConverter.GetBytes is here only a (C#-side) convenient
        /// substitute for the classic "short x=1; *((char*)&amp;x)==1"
        /// pointer trick (which in this runtime does without 'unsafe' code blocks)
        /// - in a port to C++ exactly this pointer trick
        /// is the 1:1 replacement for this one line.</summary>
        public static readonly ByteOrder HostByteOrder =
            System.BitConverter.GetBytes((ushort)1)[0] == 1 ? ByteOrder.Little : ByteOrder.Big;

        public static ByteBuffer AsciiEncode(string s)
        {
            var bytes = new byte[s.Length];
            for (int i = 0; i < s.Length; i++)
                bytes[i] = (byte)s[i];
            return new ByteBuffer(bytes, HostByteOrder);
        }

        public static string AsciiDecode(ByteBuffer buf)
        {
            var chars = new char[buf.Length];
            for (int i = 0; i < buf.Length; i++)
                chars[i] = (char)buf.Bytes[i];
            return new string(chars);
        }

        public static ByteBuffer UnicodeEncodeString(string s, int bytesPerChar)
        {
            RequireValidWidth(bytesPerChar);
            var bytes = new byte[s.Length * bytesPerChar];
            for (int i = 0; i < s.Length; i++)
                WriteCodeUnit(bytes, i * bytesPerChar, s[i], bytesPerChar, HostByteOrder);
            return new ByteBuffer(bytes, HostByteOrder);
        }

        public static ByteBuffer UnicodeEncodeChar(char c, int bytesPerChar)
        {
            RequireValidWidth(bytesPerChar);
            var bytes = new byte[bytesPerChar];
            WriteCodeUnit(bytes, 0, c, bytesPerChar, HostByteOrder);
            return new ByteBuffer(bytes, HostByteOrder);
        }

        public static string UnicodeDecodeString(ByteBuffer buf, int bytesPerChar)
        {
            RequireValidWidth(bytesPerChar);
            if (buf.Length % bytesPerChar != 0)
                throw new System.InvalidOperationException(
                    $"Buffer size ({buf.Length}) is not a multiple of the character width ({bytesPerChar}).");
            int count = buf.Length / bytesPerChar;
            var chars = new char[count];
            for (int i = 0; i < count; i++)
                chars[i] = ReadCodeUnit(buf.Bytes, i * bytesPerChar, bytesPerChar, buf.Order);
            return new string(chars);
        }

        public static char UnicodeDecodeChar(ByteBuffer buf, int bytesPerChar)
        {
            RequireValidWidth(bytesPerChar);
            if (buf.Length < bytesPerChar)
                throw new System.InvalidOperationException(
                    $"Buffer too small ({buf.Length} bytes) for a {bytesPerChar}-byte character.");
            return ReadCodeUnit(buf.Bytes, 0, bytesPerChar, buf.Order);
        }

        private static void RequireValidWidth(int bytesPerChar)
        {
            if (bytesPerChar < 1 || bytesPerChar > 4)
                throw new System.InvalidOperationException(
                    $"Invalid character width {bytesPerChar} (allowed: 1-4 bytes per character).");
        }

        private static void WriteCodeUnit(byte[] dest, int offset, char value, int width, ByteOrder order)
        {
            uint v = value;
            for (int b = 0; b < width; b++)
            {
                int shift = (order == ByteOrder.Little) ? b * 8 : (width - 1 - b) * 8;
                dest[offset + b] = (byte)((v >> shift) & 0xFF);
            }
        }

        private static char ReadCodeUnit(byte[] src, int offset, int width, ByteOrder order)
        {
            uint v = 0;
            for (int b = 0; b < width; b++)
            {
                int shift = (order == ByteOrder.Little) ? b * 8 : (width - 1 - b) * 8;
                v |= (uint)src[offset + b] << shift;
            }
            return (char)v;
        }
    }
}
