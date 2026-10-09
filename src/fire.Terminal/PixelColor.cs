using System.Runtime.InteropServices;

namespace fire.Terminal
{
    /// <summary>
    /// A single colour, 4 bytes: R, G, B, A - ALWAYS in exactly this
    /// byte order in memory (R first, then G, B, A), INDEPENDENT of
    /// the endianness of the host architecture. This is deliberately NOT a packed
    /// 32-bit integer value in the classic "ARGB" sense (whose bit layout
    /// would depend on the host endianness) - Packed here is the pure
    /// memory reinterpretation of the same 4 bytes as one number, intended only for
    /// FAST whole-value operations in C# itself (comparing, hashing,
    /// copying), not as a portable "colour value".
    ///
    /// For the later scripting-language binding this is the point: a
    /// PixelColor can be read as 4 contiguous bytes (R,G,B,A,
    /// exactly in this order, e.g. through a byte-buffer view of
    /// this memory) WITHOUT any bit-shifting/masking arithmetic - "R" is
    /// simply byte 0, "G" byte 1, and so on, no matter whether you read single bytes
    /// or the whole 32-bit block.
    ///
    /// [StructLayout(LayoutKind.Explicit)] overlays Packed AND the four
    /// byte fields on the SAME memory area (FieldOffset 0-3) - both
    /// views are literally the same 4 bytes, no conversion, no
    /// additional memory, no overhead.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public readonly struct PixelColor
    {
        [FieldOffset(0)] public readonly byte R;
        [FieldOffset(1)] public readonly byte G;
        [FieldOffset(2)] public readonly byte B;
        [FieldOffset(3)] public readonly byte A;

        /// <summary>The same 4 bytes as ONE 32-bit value (R in the lowest
        /// byte, see the class documentation) - for fast comparisons/hashing/
        /// copying, without touching the individual channels one by one.</summary>
        [FieldOffset(0)] public readonly uint Packed;

        public PixelColor(byte r, byte g, byte b, byte a = 255) : this()
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public PixelColor(uint packed) : this()
        {
            Packed = packed;
        }

        /// <summary>Implicit conversion to a 32-bit colour value (see the
        /// class documentation) - makes `SetColor(index, color)`/graphics functions
        /// possible that accept either a PixelColor OR directly a raw
        /// int colour value, without needing two separate call sites
        /// in the calling code (see Renderer/Palette).</summary>
        public static implicit operator int(PixelColor color) => unchecked((int)color.Packed);

        /// <summary>Completely transparent, as (0, 1, 0, 0) and not (0, 0, 0, 0): as a number (256) it is not taken for palette index 0
        /// (see <see cref="Paint.ToArgument"/>). See Renderer.
        /// Background documentation ("optionally transparent") and Framebuffer.SetPixel
        /// (writes the alpha value unchanged into the destination pixel, does NOT BLEND
        /// - a framebuffer of this library does no alpha
        /// blending itself, see the documentation there).</summary>
        public static readonly PixelColor Transparent = new(0, 1, 0, 0);

        public static PixelColor FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);

        // ---------------------------------------------------------------
        // Classic 16-colour CGA/QBasic palette (COLOR statement,
        // colour numbers 0-15) - as named constants, so that a later
        // fire binding (e.g. "color(QBColor.LightBlue, ...)") does not
        // first have to define/research its own colour values. Values
        // correspond to the standard CGA palette.
        // ---------------------------------------------------------------
        public static readonly PixelColor Black = FromRgb(0, 0, 0);
        public static readonly PixelColor Blue = FromRgb(0, 0, 170);
        public static readonly PixelColor Green = FromRgb(0, 170, 0);
        public static readonly PixelColor Cyan = FromRgb(0, 170, 170);
        public static readonly PixelColor Red = FromRgb(170, 0, 0);
        public static readonly PixelColor Magenta = FromRgb(170, 0, 170);
        public static readonly PixelColor Brown = FromRgb(170, 85, 0);
        public static readonly PixelColor LightGray = FromRgb(170, 170, 170);
        public static readonly PixelColor DarkGray = FromRgb(85, 85, 85);
        public static readonly PixelColor LightBlue = FromRgb(85, 85, 255);
        public static readonly PixelColor LightGreen = FromRgb(85, 255, 85);
        public static readonly PixelColor LightCyan = FromRgb(85, 255, 255);
        public static readonly PixelColor LightRed = FromRgb(255, 85, 85);
        public static readonly PixelColor LightMagenta = FromRgb(255, 85, 255);
        public static readonly PixelColor Yellow = FromRgb(255, 255, 85);
        public static readonly PixelColor White = FromRgb(255, 255, 255);

        /// <summary>Die 16 QBasic-Farben in ihrer klassischen Nummerierung
        /// (index 0-15, as with the `COLOR` statement) - for a later
        /// binding that wants to choose colours by integer instead of by name.</summary>
        public static readonly PixelColor[] QBasicPalette =
        {
            Black, Blue, Green, Cyan, Red, Magenta, Brown, LightGray,
            DarkGray, LightBlue, LightGreen, LightCyan, LightRed, LightMagenta, Yellow, White,
        };
    }
}
