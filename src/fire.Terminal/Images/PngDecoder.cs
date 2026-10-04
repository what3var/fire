using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace fire.Terminal
{
    /// <summary>PNG: alle Farbarten (Grau, RGB, Palette, Grau+Alpha, RGBA) in allen Bittiefen (1, 2, 4, 8, 16), mit und ohne Adam7-Verschränkung,
    /// `tRNS` (Transparenz für Palette, Grau, RGB). 16-Bit-Kanäle werden auf 8 Bit gekürzt. Palette-Bilder bleiben indiziert, alles andere wird Truecolor.</summary>
    internal static class PngDecoder
    {
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

        private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static uint Be32(byte[] d, int p) => ((uint)d[p] << 24) | ((uint)d[p + 1] << 16) | ((uint)d[p + 2] << 8) | d[p + 3];

        // Adam7: Startspalte/-zeile und Schrittweite je Durchgang
        private static readonly int[] PassX = { 0, 4, 0, 2, 0, 1, 0 };
        private static readonly int[] PassY = { 0, 0, 4, 0, 2, 0, 1 };
        private static readonly int[] PassDx = { 8, 8, 4, 4, 2, 2, 1 };
        private static readonly int[] PassDy = { 8, 8, 8, 4, 4, 2, 2 };

        public static ImageData Decode(byte[] d)
        {
            int pos = 8; // hinter der Signatur
            int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
            bool haveHeader = false, sawEnd = false;
            byte[]? plte = null, trns = null;
            var idat = new MemoryStream();

            while (!sawEnd)
            {
                if (pos + 12 > d.Length) throw new ImageFormatException("PNG: Datei abgeschnitten (kein IEND).");
                uint length = Be32(d, pos);
                if (length > int.MaxValue || (long)pos + 12 + length > d.Length) throw new ImageFormatException("PNG: Datei abgeschnitten (Chunk reicht über das Ende).");
                var typeBytes = new ReadOnlySpan<byte>(d, pos + 4, 4);
                var data = new ReadOnlySpan<byte>(d, pos + 8, (int)length);
                if (Crc(typeBytes, data) != Be32(d, pos + 8 + (int)length))
                    throw new ImageFormatException($"PNG: Prüfsummenfehler im Chunk '{Encoding.ASCII.GetString(typeBytes)}'.");
                string type = Encoding.ASCII.GetString(typeBytes);
                pos += 12 + (int)length;

                if (!haveHeader && type != "IHDR") throw new ImageFormatException("PNG: der erste Chunk muss IHDR sein.");
                switch (type)
                {
                    case "IHDR":
                        if (haveHeader || length != 13) throw new ImageFormatException("PNG: ungültiger IHDR-Chunk.");
                        width = (int)Math.Min(((uint)data[0] << 24) | ((uint)data[1] << 16) | ((uint)data[2] << 8) | data[3], int.MaxValue);
                        height = (int)Math.Min(((uint)data[4] << 24) | ((uint)data[5] << 16) | ((uint)data[6] << 8) | data[7], int.MaxValue);
                        bitDepth = data[8];
                        colorType = data[9];
                        if (data[10] != 0) throw new ImageFormatException("PNG: unbekanntes Kompressionsverfahren.");
                        if (data[11] != 0) throw new ImageFormatException("PNG: unbekanntes Filterverfahren.");
                        interlace = data[12];
                        if (interlace > 1) throw new ImageFormatException("PNG: unbekanntes Verschränkungsverfahren.");
                        ImageData.CheckSize("PNG", width, height);
                        bool validDepth = colorType switch
                        {
                            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
                            2 or 4 or 6 => bitDepth is 8 or 16,
                            3 => bitDepth is 1 or 2 or 4 or 8,
                            _ => false,
                        };
                        if (!validDepth) throw new ImageFormatException($"PNG: ungültige Kombination aus Farbart {colorType} und Bittiefe {bitDepth}.");
                        haveHeader = true;
                        break;
                    case "PLTE":
                        if (length == 0 || length % 3 != 0 || length > 768) throw new ImageFormatException("PNG: ungültige Palette.");
                        plte = data.ToArray();
                        break;
                    case "tRNS":
                        trns = data.ToArray();
                        break;
                    case "IDAT":
                        idat.Write(data);
                        break;
                    case "IEND":
                        sawEnd = true;
                        break;
                }
            }

            if (colorType == 3 && plte == null) throw new ImageFormatException("PNG: Palette-Bild ohne PLTE-Chunk.");
            if (idat.Length == 0) throw new ImageFormatException("PNG: keine Bilddaten (IDAT).");

            int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
            int bitsPerPixel = channels * bitDepth;
            int bpp = Math.Max(1, bitsPerPixel / 8); // Byte-Abstand für die Filter

            // erwartete Größe der entpackten Daten
            long expected = 0;
            if (interlace == 0) expected = (long)((width * (long)bitsPerPixel + 7) / 8 + 1) * height;
            else
                for (int pass = 0; pass < 7; pass++)
                {
                    int pw = PassWidth(width, pass), ph = PassHeight(height, pass);
                    if (pw > 0 && ph > 0) expected += (long)((pw * (long)bitsPerPixel + 7) / 8 + 1) * ph;
                }
            if (expected > int.MaxValue) throw new ImageFormatException("PNG: Bild zu groß.");

            var raw = new byte[expected];
            idat.Position = 0;
            try
            {
                using var z = new ZLibStream(idat, CompressionMode.Decompress);
                int got = 0;
                while (got < raw.Length)
                {
                    int n = z.Read(raw, got, raw.Length - got);
                    if (n <= 0) throw new ImageFormatException("PNG: die Bilddaten sind zu kurz (abgeschnitten oder beschädigt).");
                    got += n;
                }
            }
            catch (InvalidDataException)
            {
                throw new ImageFormatException("PNG: die Bilddaten lassen sich nicht entpacken (beschädigt).");
            }

            bool indexed = colorType == 3;
            var indices = indexed ? new byte[width * height] : null;
            var pixels = indexed ? null : new uint[width * height];

            // Transparenz-Schlüssel (Grau/RGB) bzw. Alphawerte der Palette
            uint keyGray = 0, keyR = 0, keyG = 0, keyB = 0;
            bool hasKey = false;
            if (!indexed && colorType != 4 && colorType != 6 && trns != null)
            {
                if (colorType == 0 && trns.Length >= 2) { keyGray = (uint)(trns[0] << 8 | trns[1]); hasKey = true; }
                else if (colorType == 2 && trns.Length >= 6) { keyR = (uint)(trns[0] << 8 | trns[1]); keyG = (uint)(trns[2] << 8 | trns[3]); keyB = (uint)(trns[4] << 8 | trns[5]); hasKey = true; }
            }

            int offset = 0;
            if (interlace == 0)
            {
                DecodePass(raw, ref offset, width, height, 0, 0, 1, 1, width, bitDepth, colorType, channels, bitsPerPixel, bpp, indices, pixels, hasKey, keyGray, keyR, keyG, keyB);
            }
            else
            {
                for (int pass = 0; pass < 7; pass++)
                {
                    int pw = PassWidth(width, pass), ph = PassHeight(height, pass);
                    if (pw == 0 || ph == 0) continue;
                    DecodePass(raw, ref offset, pw, ph, PassX[pass], PassY[pass], PassDx[pass], PassDy[pass], width, bitDepth, colorType, channels, bitsPerPixel, bpp, indices, pixels, hasKey, keyGray, keyR, keyG, keyB);
                }
            }

            if (indexed)
            {
                var palette = new uint[256];
                Array.Fill(palette, 0xFF000000u);
                int entries = plte!.Length / 3;
                int transparent = -1;
                for (int i = 0; i < entries; i++)
                {
                    uint a = trns != null && i < trns.Length ? trns[i] : 255u;
                    palette[i] = plte[i * 3] | ((uint)plte[i * 3 + 1] << 8) | ((uint)plte[i * 3 + 2] << 16) | (a << 24);
                    if (a == 0 && transparent < 0) transparent = i;
                }
                // ein Index über die Palettengröße hinaus ist beschädigt
                foreach (byte b in indices!)
                    if (b >= entries) throw new ImageFormatException("PNG: ein Pixel verweist auf einen Palette-Eintrag, den es nicht gibt.");
                return ImageData.CreateIndexed(width, height, indices, palette, transparent, "PNG");
            }
            return ImageData.CreateTruecolor(width, height, pixels!, "PNG");
        }

        private static int PassWidth(int width, int pass) => width > PassX[pass] ? (width - PassX[pass] + PassDx[pass] - 1) / PassDx[pass] : 0;
        private static int PassHeight(int height, int pass) => height > PassY[pass] ? (height - PassY[pass] + PassDy[pass] - 1) / PassDy[pass] : 0;

        /// <summary>Entfiltert und überträgt EIN Teilbild (bei verschränkten Bildern ein Durchgang) an seine Stellen im Ergebnis.</summary>
        private static void DecodePass(byte[] raw, ref int offset, int pw, int ph, int x0, int y0, int dx, int dy, int fullWidth,
            int bitDepth, int colorType, int channels, int bitsPerPixel, int bpp, byte[]? indices, uint[]? pixels,
            bool hasKey, uint keyGray, uint keyR, uint keyG, uint keyB)
        {
            int rowBytes = (int)((pw * (long)bitsPerPixel + 7) / 8);
            var prior = new byte[rowBytes];
            var cur = new byte[rowBytes];

            for (int row = 0; row < ph; row++)
            {
                int filter = raw[offset++];
                Buffer.BlockCopy(raw, offset, cur, 0, rowBytes);
                offset += rowBytes;
                Unfilter(filter, cur, prior, bpp);

                int y = y0 + row * dy;
                for (int col = 0; col < pw; col++)
                {
                    int x = x0 + col * dx;
                    int pos = y * fullWidth + x;

                    if (indices != null)
                    {
                        indices[pos] = (byte)Sample(cur, col, bitDepth);
                        continue;
                    }

                    uint r, g, b, a = 255;
                    switch (colorType)
                    {
                        case 0:
                        {
                            uint v = Sample(cur, col, bitDepth);
                            if (hasKey && v == keyGray) a = 0;
                            uint v8 = Scale(v, bitDepth);
                            r = g = b = v8;
                            break;
                        }
                        case 2:
                        {
                            uint rr = Sample(cur, col * 3, bitDepth), gg = Sample(cur, col * 3 + 1, bitDepth), bb = Sample(cur, col * 3 + 2, bitDepth);
                            if (hasKey && rr == keyR && gg == keyG && bb == keyB) a = 0;
                            r = Scale(rr, bitDepth); g = Scale(gg, bitDepth); b = Scale(bb, bitDepth);
                            break;
                        }
                        case 4:
                        {
                            r = g = b = Scale(Sample(cur, col * 2, bitDepth), bitDepth);
                            a = Scale(Sample(cur, col * 2 + 1, bitDepth), bitDepth);
                            break;
                        }
                        default: // 6
                        {
                            r = Scale(Sample(cur, col * 4, bitDepth), bitDepth);
                            g = Scale(Sample(cur, col * 4 + 1, bitDepth), bitDepth);
                            b = Scale(Sample(cur, col * 4 + 2, bitDepth), bitDepth);
                            a = Scale(Sample(cur, col * 4 + 3, bitDepth), bitDepth);
                            break;
                        }
                    }
                    pixels![pos] = r | (g << 8) | (b << 16) | (a << 24);
                }

                (prior, cur) = (cur, prior);
            }
        }

        /// <summary>Der `index`-te Wert (Kanal bzw. Pixel) einer Zeile in der gegebenen Bittiefe; bei 16 Bit der volle Wert.</summary>
        private static uint Sample(byte[] row, int index, int bitDepth)
        {
            switch (bitDepth)
            {
                case 8: return row[index];
                case 16: return (uint)(row[index * 2] << 8 | row[index * 2 + 1]);
                default:
                {
                    int bitPos = index * bitDepth;
                    int shift = 8 - bitDepth - (bitPos & 7);
                    return (uint)((row[bitPos >> 3] >> shift) & ((1 << bitDepth) - 1));
                }
            }
        }

        /// <summary>Wert der Bittiefe auf 0-255 skalieren (16 Bit: das obere Byte).</summary>
        private static uint Scale(uint v, int bitDepth) => bitDepth switch
        {
            8 => v,
            16 => v >> 8,
            1 => v * 255,
            2 => v * 85,
            _ => v * 17, // 4
        };

        private static void Unfilter(int filter, byte[] cur, byte[] prior, int bpp)
        {
            switch (filter)
            {
                case 0: break;
                case 1: // Sub
                    for (int i = bpp; i < cur.Length; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]);
                    break;
                case 2: // Up
                    for (int i = 0; i < cur.Length; i++) cur[i] = (byte)(cur[i] + prior[i]);
                    break;
                case 3: // Average
                    for (int i = 0; i < cur.Length; i++)
                    {
                        int left = i >= bpp ? cur[i - bpp] : 0;
                        cur[i] = (byte)(cur[i] + ((left + prior[i]) >> 1));
                    }
                    break;
                case 4: // Paeth
                    for (int i = 0; i < cur.Length; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0, b = prior[i], c = i >= bpp ? prior[i - bpp] : 0;
                        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        int pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
                        cur[i] = (byte)(cur[i] + pred);
                    }
                    break;
                default:
                    throw new ImageFormatException($"PNG: unbekannter Zeilenfilter {filter}.");
            }
        }
    }
}
