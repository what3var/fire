using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace fire.Terminal
{
    /// <summary>
    /// Writes pictures (PNG, BMP) from an <see cref="ImageData"/> - what the pixel editor of the editor saves. An indexed picture stays indexed: a PNG gets its palette (`PLTE`) and the
    /// transparent entry as `tRNS`, a BMP its palette (8 bit; a BMP cannot say which entry is transparent). A truecolor picture is written as RGBA (BMP: 24 bit, or 32 bit when it has
    /// transparent pixels). The decoders of this folder read everything back that is written here.
    /// </summary>
    public static class ImageEncoder
    {
        /// <summary>The format for a file name (`.png`, `.bmp`), null for any other.</summary>
        public static string? FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "PNG", ".bmp" => "BMP", _ => null };

        public static byte[] Encode(ImageData image, string format) => format.ToUpperInvariant() switch
        {
            "PNG" => EncodePng(image),
            "BMP" => EncodeBmp(image),
            _ => throw new ImageFormatException($"Pictures can be saved as PNG or BMP, not as {format}."),
        };

        // ---- PNG ---------------------------------------------------------------------------------------------------------------------------

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        private static void Chunk(Stream output, string type, byte[] data)
        {
            Span<byte> head = stackalloc byte[8];
            head[0] = (byte)(data.Length >> 24); head[1] = (byte)(data.Length >> 16); head[2] = (byte)(data.Length >> 8); head[3] = (byte)data.Length;
            Encoding.ASCII.GetBytes(type, head.Slice(4));
            output.Write(head);
            output.Write(data);
            uint c = 0xFFFFFFFFu;
            foreach (byte b in head.Slice(4)) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            c ^= 0xFFFFFFFFu;
            output.WriteByte((byte)(c >> 24)); output.WriteByte((byte)(c >> 16)); output.WriteByte((byte)(c >> 8)); output.WriteByte((byte)c);
        }

        public static byte[] EncodePng(ImageData image)
        {
            int w = image.Width, h = image.Height;
            var output = new MemoryStream();
            output.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A });
            bool indexed = image.IsIndexed;
            bool alpha = !indexed && image.HasAlpha;
            var header = new byte[13];
            header[0] = (byte)(w >> 24); header[1] = (byte)(w >> 16); header[2] = (byte)(w >> 8); header[3] = (byte)w;
            header[4] = (byte)(h >> 24); header[5] = (byte)(h >> 16); header[6] = (byte)(h >> 8); header[7] = (byte)h;
            header[8] = 8;
            header[9] = (byte)(indexed ? 3 : alpha ? 6 : 2);
            Chunk(output, "IHDR", header);
            if (indexed)
            {
                var plte = new byte[256 * 3];
                var trns = new byte[256];
                int lastTransparent = -1;
                for (int i = 0; i < 256; i++)
                {
                    uint p = image.Palette![i];
                    plte[i * 3] = (byte)p; plte[i * 3 + 1] = (byte)(p >> 8); plte[i * 3 + 2] = (byte)(p >> 16);
                    byte a = i == image.TransparentIndex ? (byte)0 : (byte)(p >> 24);
                    trns[i] = a;
                    if (a != 255) lastTransparent = i;
                }
                Chunk(output, "PLTE", plte);
                if (lastTransparent >= 0) Chunk(output, "tRNS", trns.AsSpan(0, lastTransparent + 1).ToArray());
            }

            int bpp = indexed ? 1 : alpha ? 4 : 3;
            var raw = new byte[(1 + w * bpp) * h];
            int at = 0;
            for (int y = 0; y < h; y++)
            {
                raw[at++] = 0;   // filter: none
                for (int x = 0; x < w; x++)
                {
                    if (indexed) raw[at++] = image.Indices![y * w + x];
                    else
                    {
                        uint p = image.Pixels![y * w + x];
                        raw[at++] = (byte)p; raw[at++] = (byte)(p >> 8); raw[at++] = (byte)(p >> 16);
                        if (alpha) raw[at++] = (byte)(p >> 24);
                    }
                }
            }
            var packed = new MemoryStream();
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            Chunk(output, "IDAT", packed.ToArray());
            Chunk(output, "IEND", Array.Empty<byte>());
            return output.ToArray();
        }

        // ---- BMP ---------------------------------------------------------------------------------------------------------------------------

        public static byte[] EncodeBmp(ImageData image)
        {
            int w = image.Width, h = image.Height;
            bool indexed = image.IsIndexed;
            int bits = indexed ? 8 : image.HasAlpha ? 32 : 24;
            int paletteBytes = indexed ? 256 * 4 : 0;
            int rowSize = (w * bits + 31) / 32 * 4;
            int dataOffset = 14 + 40 + paletteBytes;
            var d = new byte[dataOffset + rowSize * h];
            void Put32(int at, int v) { d[at] = (byte)v; d[at + 1] = (byte)(v >> 8); d[at + 2] = (byte)(v >> 16); d[at + 3] = (byte)(v >> 24); }
            d[0] = (byte)'B'; d[1] = (byte)'M';
            Put32(2, d.Length); Put32(10, dataOffset);
            Put32(14, 40); Put32(18, w); Put32(22, h);
            d[26] = 1; d[28] = (byte)bits;
            Put32(34, rowSize * h);
            Put32(46, indexed ? 256 : 0);
            if (indexed)
                for (int i = 0; i < 256; i++)
                {
                    uint p = image.Palette![i];
                    int at = 54 + i * 4;
                    d[at] = (byte)(p >> 16); d[at + 1] = (byte)(p >> 8); d[at + 2] = (byte)p;
                }
            for (int row = 0; row < h; row++)
            {
                int y = h - 1 - row;   // bottom-up
                int at = dataOffset + row * rowSize;
                for (int x = 0; x < w; x++)
                {
                    if (indexed) d[at + x] = image.Indices![y * w + x];
                    else
                    {
                        uint p = image.Pixels![y * w + x];
                        int o = at + x * (bits / 8);
                        d[o] = (byte)(p >> 16); d[o + 1] = (byte)(p >> 8); d[o + 2] = (byte)p;
                        if (bits == 32) d[o + 3] = (byte)(p >> 24);
                    }
                }
            }
            return d;
        }
    }
}
