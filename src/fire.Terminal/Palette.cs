using System;

namespace fire.Terminal
{
    /// <summary>
    /// An adjustable 256-colour palette (index 0-255) - each entry is
    /// a 32-bit colour value, freely overwritable at any time via <see cref="SetColor"/>
    /// (classic VGA palette register behaviour: the
    /// index stays the same, the actual colour behind it can
    /// change). The default assignment is structured like the
    /// widespread xterm 256-colour palette:
    ///
    /// - Index 0-15: the classic 16 CGA/QBasic colours (see
    ///   <see cref="PixelColor.QBasicPalette"/>) - for backward compatibility
    ///   with simple `COLOR 0-15`-style usage.
    /// - Index 16-231: a 6x6x6 colour cube (216 colours, levels 0/51/102/
    ///   153/204/255 per channel) - the same, widely known structure as the
    ///   "web-safe" 216-colour palette.
    /// - Index 232-255: a 24-step grey ramp.
    ///
    /// This is DELIBERATELY not the exact historical VGA standard DAC (whose
    /// exact values could not be reliably reproduced from memory without a way
    /// to actually render and check them here) - since the palette is anyway
    /// fully overwritable via SetColor,
    /// what matters for the defaults is above all a comprehensible, guaranteed
    /// correct formula with good colour coverage.
    /// </summary>
    public sealed class Palette
    {
        private readonly int[] _entries = new int[256];

        public Palette()
        {
            FillDefaults();
        }

        /// <summary>Is incremented on EVERY change of the palette - a framebuffer in palette mode recognises from it that its visible image
        /// has to be recomputed (see Framebuffer.Resolve), even if not a single index has changed.</summary>
        public int Version { get; private set; }

        /// <summary>Overwrites palette index `index` with a new
        /// 32-bit colour value - takes effect immediately on everything that looks up this
        /// index afterwards via <see cref="GetColor"/> (e.g. already
        /// drawn pixels NOT retroactively, since a framebuffer pixel
        /// itself stores no palette index, but was already resolved to a concrete
        /// PixelColor when drawing - see the
        /// Renderer.SetPixel(byte) overloads).</summary>
        public void SetColor(byte index, int color)
        {
            _entries[index] = color;
            Version++;
        }

        public PixelColor GetColor(byte index) => new PixelColor(unchecked((uint)_entries[index]));

        /// <summary>The entry as a packed value (R in the lowest byte), without the detour via PixelColor.</summary>
        public uint GetPacked(byte index) => unchecked((uint)_entries[index]);

        /// <summary>Copies all 256 entries (packed) to `destination` (at least 256 slots).</summary>
        public void CopyPacked(Span<uint> destination)
        {
            for (int i = 0; i < 256; i++) destination[i] = unchecked((uint)_entries[i]);
        }

        /// <summary>Sets the first `colors.Length` entries (at most 256) at once; the others stay unchanged.</summary>
        public void SetAll(ReadOnlySpan<uint> colors)
        {
            int n = Math.Min(colors.Length, 256);
            for (int i = 0; i < n; i++) _entries[i] = unchecked((int)colors[i]);
            Version++;
        }

        /// <summary>Restores the default assignment (see the class documentation).</summary>
        public void ResetToDefaults()
        {
            FillDefaults();
            Version++;
        }

        /// <summary>The index of the colour that comes closest to `color` (smallest distance of the channels R, G, B squared; the alpha value does
        /// not count). With equal distance the smaller index wins; an exactly present colour is found immediately.</summary>
        public byte FindNearest(PixelColor color)
        {
            int r = color.R, g = color.G, b = color.B;
            int best = 0, bestDistance = int.MaxValue;
            for (int i = 0; i < 256; i++)
            {
                uint packed = unchecked((uint)_entries[i]);
                int dr = (int)(packed & 0xFF) - r, dg = (int)((packed >> 8) & 0xFF) - g, db = (int)((packed >> 16) & 0xFF) - b;
                int distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                    if (distance == 0) break;
                }
            }
            return (byte)best;
        }

        private void FillDefaults()
        {
            for (int i = 0; i < 16; i++)
                _entries[i] = PixelColor.QBasicPalette[i];

            int[] levels = { 0, 51, 102, 153, 204, 255 };
            int idx = 16;
            foreach (int r in levels)
            {
                foreach (int g in levels)
                {
                    foreach (int b in levels)
                    {
                        _entries[idx] = PixelColor.FromRgb((byte)r, (byte)g, (byte)b);
                        idx++;
                    }
                }
            }
            // idx now stands at 16 + 216 = 232.

            for (int i = 0; i < 24; i++)
            {
                byte v = (byte)(8 + i * 10);
                _entries[232 + i] = PixelColor.FromRgb(v, v, v);
            }
        }
    }
}
