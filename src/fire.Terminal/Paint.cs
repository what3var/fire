namespace fire.Terminal
{
    /// <summary>
    /// A colour specification from the caller: either an index into the palette of the target framebuffer (0-255) or a direct RGBA value. Only
    /// <see cref="Surface.Resolve"/> turns it into the colour for EXACTLY this framebuffer - the same specification thus draws in every
    /// colour mode: in the RGBA framebuffer an index is translated into a colour via the palette, in the palette framebuffer an RGBA value is mapped to the
    /// nearest palette entry.
    ///
    /// From a numeric value (script argument) it arises by the rule of `Renderer.SetColor`: a value in which only the lowest byte
    /// is occupied (0-255) is a palette index; any other is a direct value (R in the lowest byte, alpha in the highest). A palette index
    /// thus occupies only the R byte, alpha stays 0 - it only collides with transparent colours whose G and B are 0 (above all 0 = transparent
    /// black, the QBasic black of index 0). That is why the canonical transparent colour is (0, 1, 0, 0) = 256 (<see cref="Transparent"/>),
    /// and <see cref="ToArgument"/> maps such values to it wherever a pixel goes to a script as a number.
    /// </summary>
    public readonly struct Paint
    {
        /// <summary>The direct value (packed, see PixelColor) - only valid if <see cref="IsIndex"/> is false.</summary>
        public readonly uint Rgba;

        /// <summary>Palette index 0-255, or -1: a direct RGBA value.</summary>
        public readonly short Index;

        private Paint(uint rgba, short index)
        {
            Rgba = rgba;
            Index = index;
        }

        public bool IsIndex => Index >= 0;

        public static Paint FromIndex(byte index) => new(0, index);
        public static Paint FromRgba(PixelColor color) => new(color.Packed, -1);
        public static Paint FromRgba(uint packed) => new(packed, -1);

        /// <summary>The rule for numeric values from scripts (see the class documentation). Only the lower 32 bits count (a signed value
        /// like the one from `GetPixel` is the same value).</summary>
        public static Paint FromArgument(long value)
        {
            uint raw = unchecked((uint)value);
            return (raw & 0xFFFFFF00u) == 0 ? FromIndex((byte)raw) : FromRgba(raw);
        }

        /// <summary>The canonical transparent colour as a numeric value: (0, 1, 0, 0) - it is completely transparent, but is not read as a palette index.</summary>
        public const int Transparent = 0x100;

        /// <summary>A pixel value as a number for a script: a value that would be read as a palette index (only the R byte occupied, so always transparent) becomes
        /// <see cref="Transparent"/> - so a pixel that was read stays transparent when written back instead of becoming palette black.</summary>
        public static int ToArgument(uint packed) => (packed & 0xFFFFFF00u) == 0 ? Transparent : unchecked((int)packed);

        public static implicit operator Paint(PixelColor color) => FromRgba(color);
    }

    /// <summary>A colour, resolved for a particular target (see <see cref="Surface.Resolve"/>): `Rgba` is the colour value (for an RGBA framebuffer what is written into the pixel,
    /// its alpha also applies when blending), `Index` the palette entry for a palette framebuffer. The drawing functions work only with this.</summary>
    public readonly struct Pixel
    {
        public readonly uint Rgba;
        public readonly byte Index;

        public Pixel(uint rgba, byte index)
        {
            Rgba = rgba;
            Index = index;
        }

        /// <summary>The alpha of the colour value (255 = opaque).</summary>
        public byte Alpha => (byte)(Rgba >> 24);
    }
}
