using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace fire.Device.Manager.DeviceManager
{
    /// <summary>
    /// What a device terminal shows: the bytes that came from the device and went to it, in blocks of one direction (bytes of the same direction that follow each
    /// other join the block), shown as text (ASCII) or as a hex dump like a hex editor - 16 bytes per line separated by spaces, then the 16 characters.
    /// Keeps at most <see cref="Limit"/> bytes (the oldest blocks go). Not thread-safe: the host locks or uses it on one thread.
    /// </summary>
    public sealed class TerminalLog
    {
        public sealed class Block
        {
            public PacketDirection Direction { get; }
            public DateTime Time { get; }
            public List<byte> Data { get; } = new();
            public Block(PacketDirection direction, DateTime time) { Direction = direction; Time = time; }
        }

        private readonly List<Block> _blocks = new();
        private long _count;

        /// <summary>The most bytes kept.</summary>
        public int Limit { get; set; } = 256 * 1024;

        public IReadOnlyList<Block> Blocks => _blocks;

        /// <summary>The bytes kept.</summary>
        public long Count => _count;

        public void Add(PacketDirection direction, byte[] data, DateTime? time = null)
        {
            if (data.Length == 0) return;
            if (_blocks.Count == 0 || _blocks[^1].Direction != direction) _blocks.Add(new Block(direction, time ?? DateTime.Now));
            _blocks[^1].Data.AddRange(data);
            _count += data.Length;
            while (_count > Limit && _blocks.Count > 1)
            {
                _count -= _blocks[0].Data.Count;
                _blocks.RemoveAt(0);
            }
        }

        public void Clear() { _blocks.Clear(); _count = 0; }

        /// <summary>The whole log as text: `hex` a hex dump per block, otherwise the characters; `showSent` false leaves out what was sent.</summary>
        public string Render(bool hex, bool showSent = true)
        {
            var sb = new StringBuilder();
            foreach (var b in _blocks)
            {
                if (!showSent && b.Direction == PacketDirection.HostToDevice) continue;
                string arrow = b.Direction == PacketDirection.DeviceToHost ? "<<" : ">>";
                if (hex)
                {
                    sb.Append(arrow).Append(' ').Append(b.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("  ").Append(b.Data.Count)
                      .Append(b.Data.Count == 1 ? " byte" : " bytes").Append('\n');
                    HexDump(b.Data, sb);
                }
                else
                {
                    sb.Append(arrow).Append(' ').Append(Ascii(b.Data));
                    if (sb.Length == 0 || sb[^1] != '\n') sb.Append('\n');
                }
            }
            return sb.ToString();
        }

        /// <summary>A hex dump: per line the offset, 16 bytes in hex separated by spaces (a wider gap after 8) and the 16 characters ('.' for those that cannot
        /// be shown).</summary>
        public static string HexDump(IReadOnlyList<byte> data)
        {
            var sb = new StringBuilder();
            HexDump(data, sb);
            return sb.ToString();
        }

        private static void HexDump(IReadOnlyList<byte> data, StringBuilder sb)
        {
            for (int row = 0; row < data.Count; row += 16)
            {
                sb.Append(row.ToString("X4", CultureInfo.InvariantCulture)).Append("  ");
                for (int i = 0; i < 16; i++)
                {
                    if (i == 8) sb.Append(' ');
                    if (row + i < data.Count) sb.Append(data[row + i].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                    else sb.Append("   ");
                }
                sb.Append(' ');
                for (int i = 0; i < 16 && row + i < data.Count; i++)
                {
                    byte c = data[row + i];
                    sb.Append(c >= 0x20 && c < 0x7F ? (char)c : '.');
                }
                sb.Append('\n');
            }
        }

        /// <summary>The bytes as text, one character per byte: line breaks and tabs stay (a CR before an LF goes), other control bytes and bytes from 0x7F up
        /// are shown as `&lt;0D&gt;` (Latin-1 letters stay letters).</summary>
        public static string Ascii(IReadOnlyList<byte> data)
        {
            var sb = new StringBuilder(data.Count);
            for (int i = 0; i < data.Count; i++)
            {
                byte c = data[i];
                if (c == '\n' || c == '\t') sb.Append((char)c);
                else if (c == '\r' && i + 1 < data.Count && data[i + 1] == '\n') continue;
                else if (c >= 0x20 && c < 0x7F || c >= 0xA0) sb.Append((char)c);
                else sb.Append('<').Append(c.ToString("X2", CultureInfo.InvariantCulture)).Append('>');
            }
            return sb.ToString();
        }

        /// <summary>Reads bytes written in hex: pairs of digits, separated or not by spaces, commas or dashes, each optionally with `0x` ("48 65 6c", "48656C",
        /// "0x48,0x65"). False with a message when it is not hex.</summary>
        public static bool TryParseHex(string text, out byte[] data, out string error)
        {
            var bytes = new List<byte>();
            data = Array.Empty<byte>();
            error = "";
            var digits = new StringBuilder();
            foreach (var token in text.Split(new[] { ' ', ',', '-', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token.Substring(2) : token;
                if (t.Length == 1) t = "0" + t;
                if (t.Length % 2 != 0) { error = $"'{token}' has an odd number of hex digits."; return false; }
                for (int i = 0; i < t.Length; i += 2)
                {
                    if (!byte.TryParse(t.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b)) { error = $"'{token}' is not hex."; return false; }
                    bytes.Add(b);
                }
            }
            data = bytes.ToArray();
            return true;
        }

        /// <summary>The bytes of a text to send in ASCII mode (one byte per character, Latin-1; `\r`, `\n`, `\t`, `\\` and `\xNN` are understood) with the line ending.</summary>
        public static byte[] TextToBytes(string text, string lineEnding)
        {
            var bytes = new List<byte>();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    char n = text[i + 1];
                    if (n == 'r') { bytes.Add(13); i++; continue; }
                    if (n == 'n') { bytes.Add(10); i++; continue; }
                    if (n == 't') { bytes.Add(9); i++; continue; }
                    if (n == '\\') { bytes.Add((byte)'\\'); i++; continue; }
                    if (n == 'x' && i + 3 < text.Length && byte.TryParse(text.AsSpan(i + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte v))
                    {
                        bytes.Add(v);
                        i += 3;
                        continue;
                    }
                }
                bytes.Add(c <= 0xFF ? (byte)c : (byte)'?');
            }
            foreach (char c in lineEnding) bytes.Add((byte)c);
            return bytes.ToArray();
        }
    }
}
