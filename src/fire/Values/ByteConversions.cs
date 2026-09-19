namespace ScriptLang.Values
{
    /// <summary>ASCII-/"Unicode"-Umwandlungen zwischen string/char und
    /// ByteBuffer (siehe SPEC 8.10). Bewusst NUR einfachste Bausteine
    /// (Arrays, Schleifen, Bit-Shifts) - keine System.Text.Encoding, keine
    /// echte Unicode-Bibliothek (Normalisierung, Surrogatpaare, etc.): "ASCII"
    /// meint hier wörtlich 1 Byte pro Zeichen (das niedrigste Byte des
    /// char-Werts, alles darüber geht verloren), "Unicode" eine feste
    /// Breite von N Bytes pro Zeichen (N frei wählbar, typischerweise 2 -
    /// entspricht dann UTF-16/Windows "Unicode" für Zeichen im BMP, ohne
    /// Surrogatpaare gesondert zu behandeln, da ein Sprach-`char` hier
    /// ohnehin nur eine einzelne 16-Bit-Codeeinheit ist, kein volles
    /// Codepoint-Konzept). Diese Einfachheit ist ABSICHT: die Klasse soll
    /// sich später 1:1 in eine C++-VM übertragen lassen.</summary>
    public static class ByteConversions
    {
        /// <summary>Little- oder Big-Endian der HOST-Architektur - zur
        /// Laufzeit per Test ermittelt (kein Compile-Flag, keine Annahme
        /// über die Zielplattform): ein Mehrbyte-Wert wird geschrieben, dann
        /// geprüft, ob das NIEDRIGSTWERTIGE Byte im Speicher ZUERST liegt.
        /// BitConverter.GetBytes ist hier nur ein (C#-seitig) bequemer
        /// Ersatz für den klassischen "short x=1; *((char*)&amp;x)==1"-
        /// Zeiger-Trick (der in dieser Laufzeit ohne 'unsafe'-Codeblöcke
        /// auskommt) - bei einem Port nach C++ ist genau dieser Zeiger-Trick
        /// der 1:1-Ersatz für diese eine Zeile.</summary>
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
                    $"Puffergröße ({buf.Length}) ist kein Vielfaches der Zeichenbreite ({bytesPerChar}).");
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
                    $"Puffer zu klein ({buf.Length} Byte) für ein {bytesPerChar}-Byte-Zeichen.");
            return ReadCodeUnit(buf.Bytes, 0, bytesPerChar, buf.Order);
        }

        private static void RequireValidWidth(int bytesPerChar)
        {
            if (bytesPerChar < 1 || bytesPerChar > 4)
                throw new System.InvalidOperationException(
                    $"Ungültige Zeichenbreite {bytesPerChar} (erlaubt: 1-4 Byte pro Zeichen).");
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
