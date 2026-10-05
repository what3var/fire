using System;
using System.Numerics;

namespace fire.Terminal
{
    /// <summary>BMP (Windows-Bitmap und OS/2-Kopfzeile): 1, 4 und 8 Bit mit Palette (auch RLE4/RLE8) bleiben indiziert; 16, 24 und 32 Bit (auch mit Bitmasken)
    /// werden Truecolor. Zeilen von unten nach oben oder (negative Höhe) von oben nach unten.</summary>
    internal static class BmpDecoder
    {
        private static int Le16(byte[] d, int p) => d[p] | (d[p + 1] << 8);
        private static int Le32(byte[] d, int p) => d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24);

        public static ImageData Decode(byte[] d)
        {
            if (d.Length < 26) throw new ImageFormatException("BMP: file too short.");
            int dataOffset = Le32(d, 10);
            int headerSize = Le32(d, 14);

            int width, height, bpp, compression = 0, colorsUsed = 0;
            bool topDown = false;
            if (headerSize == 12)
            {
                width = Le16(d, 18);
                height = Le16(d, 20);
                bpp = Le16(d, 24);
            }
            else if (headerSize >= 40)
            {
                if (d.Length < 14 + 40) throw new ImageFormatException("BMP: file too short.");
                width = Le32(d, 18);
                int h = Le32(d, 22);
                topDown = h < 0;
                height = h == int.MinValue ? 0 : Math.Abs(h);
                if (Le16(d, 26) != 1) throw new ImageFormatException("BMP: invalid plane count.");
                bpp = Le16(d, 28);
                compression = Le32(d, 30);
                colorsUsed = Le32(d, 46);
            }
            else throw new ImageFormatException($"BMP: unsupported header ({headerSize} bytes).");

            ImageData.CheckSize("BMP", width, height);
            if (bpp is not (1 or 4 or 8 or 16 or 24 or 32)) throw new ImageFormatException($"BMP: unsupported color depth ({bpp} bits).");
            if (compression is not (0 or 1 or 2 or 3 or 6)) throw new ImageFormatException($"BMP: unsupported compression ({compression}).");
            if (compression == 1 && bpp != 8 || compression == 2 && bpp != 4) throw new ImageFormatException("BMP: RLE does not match the color depth.");
            if ((compression == 3 || compression == 6) && bpp is not (16 or 32)) throw new ImageFormatException("BMP: bit masks are only allowed with 16 and 32 bits.");
            if (dataOffset < 0 || dataOffset > d.Length) throw new ImageFormatException("BMP: invalid data offset.");

            // ---- Bitmasken ----
            uint maskR = 0, maskG = 0, maskB = 0, maskA = 0;
            bool hasMasks = compression == 3 || compression == 6;
            if (hasMasks)
            {
                int maskPos = 14 + 40; // bei einer 40-Byte-Kopfzeile folgen die Masken unmittelbar dahinter, bei größeren stehen sie darin
                if (d.Length < maskPos + 12) throw new ImageFormatException("BMP: file too short for the bit masks.");
                maskR = (uint)Le32(d, maskPos); maskG = (uint)Le32(d, maskPos + 4); maskB = (uint)Le32(d, maskPos + 8);
                if ((compression == 6 || headerSize >= 56) && d.Length >= maskPos + 16) maskA = (uint)Le32(d, maskPos + 12);
            }
            else if (bpp == 16) { maskR = 0x7C00; maskG = 0x03E0; maskB = 0x001F; }

            // ---- Palette ----
            uint[]? palette = null;
            if (bpp <= 8)
            {
                int entries = colorsUsed > 0 ? colorsUsed : 1 << bpp;
                if (entries > 256 || entries > (1 << bpp)) entries = Math.Min(256, 1 << bpp);
                int entrySize = headerSize == 12 ? 3 : 4;
                int palPos = 14 + headerSize;
                if (palPos + entries * entrySize > d.Length) throw new ImageFormatException("BMP: file too short for the palette.");
                palette = new uint[256];
                Array.Fill(palette, 0xFF000000u);
                for (int i = 0; i < entries; i++)
                {
                    int p = palPos + i * entrySize;
                    palette[i] = d[p + 2] | ((uint)d[p + 1] << 8) | ((uint)d[p] << 16) | 0xFF000000u; // Datei: B, G, R, (unbenutzt)
                }
            }

            if (palette != null)
            {
                byte[] indices = compression == 0
                    ? ReadIndexed(d, dataOffset, width, height, bpp, topDown)
                    : ReadRle(d, dataOffset, width, height, compression == 1);
                return ImageData.CreateIndexed(width, height, indices, palette, -1, "BMP");
            }

            // ---- Truecolor ----
            int rowSize = (int)(((long)width * bpp + 31) / 32 * 4);
            if ((long)dataOffset + (long)rowSize * height > d.Length) throw new ImageFormatException("BMP: file too short for the image data.");
            var pixels = new uint[width * height];
            bool anyAlpha = false;

            for (int row = 0; row < height; row++)
            {
                int y = topDown ? row : height - 1 - row;
                int rowStart = dataOffset + row * rowSize;
                for (int x = 0; x < width; x++)
                {
                    uint r, g, b, a = 255;
                    if (bpp == 24)
                    {
                        int p = rowStart + x * 3;
                        b = d[p]; g = d[p + 1]; r = d[p + 2];
                    }
                    else
                    {
                        uint v = bpp == 16 ? (uint)Le16(d, rowStart + x * 2) : (uint)Le32(d, rowStart + x * 4);
                        if (bpp == 32 && !hasMasks)
                        {
                            b = v & 0xFF; g = (v >> 8) & 0xFF; r = (v >> 16) & 0xFF;
                            a = v >> 24;
                            if (a != 0) anyAlpha = true;
                        }
                        else
                        {
                            r = Extract(v, maskR); g = Extract(v, maskG); b = Extract(v, maskB);
                            if (maskA != 0) a = Extract(v, maskA);
                        }
                    }
                    pixels[y * width + x] = r | (g << 8) | (b << 16) | (a << 24);
                }
            }

            // 32 Bit ohne Alpha-Maske: das vierte Byte ist meist unbenutzt (0) - dann ist das Bild deckend
            if (bpp == 32 && !hasMasks && !anyAlpha)
                for (int i = 0; i < pixels.Length; i++) pixels[i] |= 0xFF000000u;

            return ImageData.CreateTruecolor(width, height, pixels, "BMP");
        }

        /// <summary>Ein Kanalwert aus `v` nach der Maske, auf 0-255 skaliert.</summary>
        private static uint Extract(uint v, uint mask)
        {
            if (mask == 0) return 0;
            int shift = BitOperations.TrailingZeroCount(mask);
            uint max = mask >> shift;
            return ((v & mask) >> shift) * 255 / max;
        }

        private static byte[] ReadIndexed(byte[] d, int dataOffset, int width, int height, int bpp, bool topDown)
        {
            int rowSize = (int)(((long)width * bpp + 31) / 32 * 4);
            if ((long)dataOffset + (long)rowSize * height > d.Length) throw new ImageFormatException("BMP: file too short for the image data.");
            var indices = new byte[width * height];
            for (int row = 0; row < height; row++)
            {
                int y = topDown ? row : height - 1 - row;
                int rowStart = dataOffset + row * rowSize;
                for (int x = 0; x < width; x++)
                {
                    int bitPos = x * bpp;
                    int shift = 8 - bpp - (bitPos & 7);
                    indices[y * width + x] = (byte)((d[rowStart + (bitPos >> 3)] >> shift) & ((1 << bpp) - 1));
                }
            }
            return indices;
        }

        /// <summary>RLE8/RLE4: Wiederholungen, Escape-Codes (Zeilenende, Bildende, Verschiebung) und absolute Läufe. Nicht erreichte Pixel bleiben Index 0.</summary>
        private static byte[] ReadRle(byte[] d, int p, int width, int height, bool rle8)
        {
            var indices = new byte[width * height];
            int x = 0, row = 0; // row 0 = unterste Bildzeile

            void Put(int index)
            {
                if (x >= 0 && x < width && row >= 0 && row < height)
                    indices[(height - 1 - row) * width + x] = (byte)index;
                x++;
            }

            while (p + 1 < d.Length)
            {
                int count = d[p], value = d[p + 1];
                p += 2;
                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                        Put(rle8 ? value : (i % 2 == 0 ? value >> 4 : value & 0xF));
                    continue;
                }
                if (value == 0) { x = 0; row++; continue; }   // Zeilenende
                if (value == 1) break;                         // Bildende
                if (value == 2)                                // Verschiebung
                {
                    if (p + 1 >= d.Length) break;
                    x += d[p]; row += d[p + 1];
                    p += 2;
                    continue;
                }
                // absoluter Lauf von `value` Pixeln, auf eine gerade Byte-Zahl aufgefüllt
                int n = value;
                int bytes = rle8 ? n : (n + 1) / 2;
                if (p + bytes > d.Length) throw new ImageFormatException("BMP: RLE-Daten abgeschnitten.");
                for (int i = 0; i < n; i++)
                    Put(rle8 ? d[p + i] : (i % 2 == 0 ? d[p + i / 2] >> 4 : d[p + i / 2] & 0xF));
                p += bytes + (bytes & 1);
            }
            return indices;
        }
    }
}
