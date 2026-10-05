using System;
using System.IO;

namespace fire.Terminal
{
    /// <summary>GIF: das ERSTE Bild einer Datei (eine Animation liefert ihr erstes Einzelbild), immer indiziert - mit der lokalen oder globalen Palette,
    /// dem Transparenz-Index der Grafiksteuerung und auch verschränkt. Das Bild hat die Größe des logischen Bildschirms; liegt das erste Teilbild
    /// kleiner oder versetzt darin, füllt der Hintergrundindex (bzw. der durchsichtige Index) den Rest.</summary>
    internal static class GifDecoder
    {
        public static ImageData Decode(byte[] d)
        {
            if (d.Length < 13) throw new ImageFormatException("GIF: file too short.");
            int screenW = d[6] | (d[7] << 8), screenH = d[8] | (d[9] << 8);
            int flags = d[10];
            int backgroundIndex = d[11];
            ImageData.CheckSize("GIF", screenW, screenH);

            int pos = 13;
            uint[]? globalPalette = null;
            if ((flags & 0x80) != 0)
            {
                int n = 2 << (flags & 7);
                globalPalette = ReadPalette(d, ref pos, n);
            }

            int transparent = -1;
            while (pos < d.Length)
            {
                int block = d[pos++];
                if (block == 0x3B) break; // Ende der Datei, ohne dass ein Bild kam
                if (block == 0x21)        // Erweiterung
                {
                    if (pos >= d.Length) break;
                    int label = d[pos++];
                    if (label == 0xF9 && pos + 5 < d.Length && d[pos] == 4)
                        transparent = (d[pos + 1] & 1) != 0 ? d[pos + 4] : -1;
                    SkipSubBlocks(d, ref pos);
                    continue;
                }
                if (block != 0x2C) throw new ImageFormatException($"GIF: unknown block 0x{block:X2}.");

                // Bildbeschreibung
                if (pos + 9 > d.Length) throw new ImageFormatException("GIF: file truncated (image descriptor).");
                int left = d[pos] | (d[pos + 1] << 8), top = d[pos + 2] | (d[pos + 3] << 8);
                int w = d[pos + 4] | (d[pos + 5] << 8), h = d[pos + 6] | (d[pos + 7] << 8);
                int iflags = d[pos + 8];
                pos += 9;
                uint[]? palette = globalPalette;
                if ((iflags & 0x80) != 0) palette = ReadPalette(d, ref pos, 2 << (iflags & 7));
                if (palette == null) throw new ImageFormatException("GIF: weder globale noch lokale Palette.");
                bool interlaced = (iflags & 0x40) != 0;

                if (pos >= d.Length) throw new ImageFormatException("GIF: file truncated (image data).");
                int minCode = d[pos++];
                if (minCode < 2 || minCode > 11) throw new ImageFormatException("GIF: invalid LZW code size.");

                // die Teilblöcke der Bilddaten zusammensetzen
                var packed = new MemoryStream();
                while (true)
                {
                    if (pos >= d.Length) throw new ImageFormatException("GIF: file truncated (image data).");
                    int len = d[pos++];
                    if (len == 0) break;
                    if (pos + len > d.Length) throw new ImageFormatException("GIF: file truncated (image data).");
                    packed.Write(d, pos, len);
                    pos += len;
                }

                var frame = LzwDecode(packed.ToArray(), minCode, checked(w * h));

                // auf den logischen Bildschirm setzen
                int fill = transparent >= 0 ? transparent : Math.Min(backgroundIndex, 255);
                var indices = new byte[screenW * screenH];
                Array.Fill(indices, (byte)fill);
                int[] rows = RowOrder(h, interlaced);
                for (int r = 0; r < h; r++)
                {
                    int y = top + rows[r];
                    if (y < 0 || y >= screenH) continue;
                    for (int x = 0; x < w; x++)
                    {
                        int px = left + x;
                        if (px < 0 || px >= screenW) continue;
                        indices[y * screenW + px] = frame[r * w + x];
                    }
                }
                return ImageData.CreateIndexed(screenW, screenH, indices, palette, transparent, "GIF");
            }
            throw new ImageFormatException("GIF: the file contains no image.");
        }

        private static uint[] ReadPalette(byte[] d, ref int pos, int entries)
        {
            if (pos + entries * 3 > d.Length) throw new ImageFormatException("GIF: file truncated (palette).");
            var palette = new uint[256];
            Array.Fill(palette, 0xFF000000u);
            for (int i = 0; i < entries; i++)
            {
                palette[i] = d[pos] | ((uint)d[pos + 1] << 8) | ((uint)d[pos + 2] << 16) | 0xFF000000u;
                pos += 3;
            }
            return palette;
        }

        private static void SkipSubBlocks(byte[] d, ref int pos)
        {
            while (pos < d.Length)
            {
                int len = d[pos++];
                if (len == 0) return;
                pos += len;
            }
        }

        /// <summary>Welche Bildzeile die r-te gespeicherte ist (bei verschränkten Bildern in vier Durchgängen: 0,8,16.. / 4,12.. / 2,6.. / 1,3,5..).</summary>
        private static int[] RowOrder(int height, bool interlaced)
        {
            var rows = new int[height];
            if (!interlaced)
            {
                for (int i = 0; i < height; i++) rows[i] = i;
                return rows;
            }
            int n = 0;
            foreach (var (start, step) in new[] { (0, 8), (4, 8), (2, 4), (1, 2) })
                for (int y = start; y < height; y += step) rows[n++] = y;
            return rows;
        }

        /// <summary>Der LZW-Strom des GIF (Codes LSB-zuerst, wachsende Codebreite 3-12 Bit, Clear- und Ende-Code). Fehlende Pixel am Ende bleiben 0.</summary>
        private static byte[] LzwDecode(byte[] data, int minCodeSize, int pixelCount)
        {
            var output = new byte[pixelCount];
            int clear = 1 << minCodeSize, end = clear + 1;
            var prefix = new short[4096];
            var suffix = new byte[4096];
            var stack = new byte[4097];

            int codeSize = minCodeSize + 1, next = end + 1;
            int prev = -1;
            byte first = 0;
            int outPos = 0;

            int bitBuffer = 0, bitCount = 0, dataPos = 0;
            while (outPos < pixelCount)
            {
                while (bitCount < codeSize)
                {
                    if (dataPos >= data.Length) return output; // Datenende ohne End-Code: lenient
                    bitBuffer |= data[dataPos++] << bitCount;
                    bitCount += 8;
                }
                int code = bitBuffer & ((1 << codeSize) - 1);
                bitBuffer >>= codeSize;
                bitCount -= codeSize;

                if (code == clear)
                {
                    codeSize = minCodeSize + 1;
                    next = end + 1;
                    prev = -1;
                    continue;
                }
                if (code == end) break;

                if (prev == -1)
                {
                    if (code >= clear) throw new ImageFormatException("GIF: invalid LZW code at the start.");
                    output[outPos++] = (byte)code;
                    prev = code;
                    first = (byte)code;
                    continue;
                }

                int cur = code;
                int sp = 0;
                if (code >= next)
                {
                    if (code > next) throw new ImageFormatException("GIF: corrupt LZW data (code outside of the dictionary).");
                    stack[sp++] = first;   // der Sonderfall K-w-K
                    cur = prev;
                }
                while (cur >= clear)
                {
                    stack[sp++] = suffix[cur];
                    cur = prefix[cur];
                }
                stack[sp++] = (byte)cur;
                first = (byte)cur;

                while (sp > 0 && outPos < pixelCount) output[outPos++] = stack[--sp];

                if (next < 4096)
                {
                    prefix[next] = (short)prev;
                    suffix[next] = first;
                    next++;
                    if (next == (1 << codeSize) && codeSize < 12) codeSize++;
                }
                prev = code;
            }
            return output;
        }
    }
}
