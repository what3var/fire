using System;

namespace fire.Terminal
{
    /// <summary>
    /// A raw, directly accessible pixel buffer (R,G,B,A per pixel, see
    /// PixelColor - row by row) - the central object of this library: the renderer (terminal
    /// emulation AND raw graphics operations) ALWAYS writes here, never
    /// directly into a window; an IFramebufferRenderer (e.g. SDL) only reads the
    /// finished content out in order to display it. This keeps the
    /// buffer itself completely independent of the rendering backend AND
    /// directly addressable from code (Pixels array), e.g. to
    /// save it, compare it or copy it into another buffer.
    ///
    /// Deliberately NO alpha blending when writing (SetPixel/FillRect/...
    /// always overwrite a destination pixel completely, including its
    /// alpha value) - "optionally transparent" (see Renderer.
    /// Background documentation) here means "do NOT overwrite a cell with
    /// background", not "blend with transparency". A renderer that
    /// itself lays the framebuffer over an already existing scene
    /// (e.g. alpha compositing of several windows) can
    /// still evaluate the alpha channel - it is just not applied here.
    /// </summary>
    public sealed class Framebuffer : IRenderTarget
    {
        public int Width { get; private set; }
        public int Height { get; private set; }

        /// <summary>The largest side length of a framebuffer for <see cref="Resize"/> (and the largest pixel count: <see cref="MaxPixels"/>).</summary>
        public const int MaxSide = 16384;
        public const long MaxPixels = 64L * 1024 * 1024;

        /// <summary>Is this a size that <see cref="Resize"/> brings the framebuffer to: both sides from 1 to <see cref="MaxSide"/>, at most <see cref="MaxPixels"/> pixels?
        /// (A minimised window reports size 0, an absurdly large one the limits.)</summary>
        public static bool IsValidSize(long width, long height) => width >= 1 && height >= 1 && width <= MaxSide && height <= MaxSide && width * height <= MaxPixels;

        /// <summary>How the pixels are stored (see <see cref="ColorMode"/>).</summary>
        public ColorMode Mode { get; }

        public bool IsIndexed => Mode == ColorMode.Indexed;

        /// <summary>One uint per pixel, row by row (index = y * Width + x) -
        /// each uint is exactly PixelColor.Packed of this pixel (R,G,B,A in
        /// exactly this byte order, see the PixelColor documentation) - directly
        /// accessible for everything that needs more than the methods of this
        /// class (serialisation, diffing, copying into a second buffer
        /// via Array.Copy, or a later byte-exact look from the
        /// scripting language).
        ///
        /// In palette mode (<see cref="ColorMode.Indexed"/>) this is only the IMAGE of the indices (for the renderer and everything that
        /// reads colours): it is computed from <see cref="Indices"/> and the palette when needed (<see cref="Resolve"/>) and is NOT current between
        /// two drawing operations. Writing has no effect there (the next Resolve overwrites it).</summary>
        public uint[] Pixels { get; private set; }

        /// <summary>Palette mode only: one byte per pixel, the index into <see cref="Palette"/> (row by row like <see cref="Pixels"/>); otherwise null.
        /// Whoever writes it directly calls <see cref="MarkDirty"/> afterwards.</summary>
        public byte[]? Indices { get; private set; }

        /// <summary>The 256-colour palette of this framebuffer. In palette mode it determines the visible colours; in RGBA mode it resolves
        /// palette indices that the drawing functions receive as a colour (see <see cref="Paint"/>). Several consoles on the same
        /// framebuffer share it.</summary>
        public Palette Palette { get; } = new();

        /// <summary>Palette mode: the index that counts as transparent in an image (GIF transparency, PNG palette with alpha 0), or -1.
        /// <see cref="Blitter"/> skips pixels with this index in "Transparent" mode.</summary>
        public int TransparentIndex { get; set; } = -1;


        private bool _dirty = true;
        private int _resolvedPaletteVersion = -1;

        public Framebuffer(int width, int height, ColorMode mode = ColorMode.Rgba)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "The framebuffer size must be positive.");
            if (mode != ColorMode.Rgba && mode != ColorMode.Indexed)
                throw new ArgumentOutOfRangeException(nameof(mode), "Unknown color mode.");
            Width = width;
            Height = height;
            Mode = mode;
            Pixels = new uint[width * height];
            if (mode == ColorMode.Indexed) Indices = new byte[width * height];
        }

        /// <summary>Brings the framebuffer to a new size (see <see cref="IsValidSize"/>; an invalid size leaves it unchanged and returns false). The content is kept at the top left,
        /// what is added is transparent (RGBA) or index 0 (palette); mode, palette and transparent index stay. Afterwards <see cref="Pixels"/> and <see cref="Indices"/> are
        /// DIFFERENT arrays - whoever has remembered a reference (a pointer to the pixels) fetches it anew.</summary>
        public bool Resize(int width, int height)
        {
            if (!IsValidSize(width, height)) return false;
            if (width == Width && height == Height) return true;
            int copyW = Math.Min(width, Width), copyH = Math.Min(height, Height);
            var newPixels = new uint[width * height];
            var newIndices = Indices != null ? new byte[width * height] : null;
            for (int y = 0; y < copyH; y++)
            {
                Array.Copy(Pixels, y * Width, newPixels, y * width, copyW);
                if (newIndices != null) Array.Copy(Indices!, y * Width, newIndices, y * width, copyW);
            }
            Pixels = newPixels;
            Indices = newIndices;
            Width = width;
            Height = height;
            _dirty = true;
            return true;
        }

        // -----------------------------------------------------------
        // Palette-Modus: Indizes -> sichtbare Farben
        // -----------------------------------------------------------

        /// <summary>Palette mode: notes that <see cref="Indices"/> were changed from outside (the next <see cref="Resolve"/> recomputes).</summary>
        public void MarkDirty() => _dirty = true;

        /// <summary>Palette mode: brings <see cref="Pixels"/> up to date with the indices and the palette - only if something has changed
        /// since the last time. Renderers call this before displaying; in RGBA mode it does nothing.</summary>
        public void Resolve()
        {
            var indices = Indices;
            if (indices == null) return;
            if (!_dirty && _resolvedPaletteVersion == Palette.Version) return;

            Span<uint> table = stackalloc uint[256];
            Palette.CopyPacked(table);
            var pixels = Pixels;
            for (int i = 0; i < indices.Length; i++)
                pixels[i] = table[indices[i]];

            _dirty = false;
            _resolvedPaletteVersion = Palette.Version;
        }

        /// <summary>Turns a colour specification into the colour for THIS framebuffer (see <see cref="Paint"/>).</summary>
        public Pixel ResolvePixel(Paint paint) => Surface.ResolvePixel(this, paint);

        /// <summary>The raw pixel value at (x, y) - in palette mode the index, otherwise the packed RGBA value; 0 outside. For comparing
        /// pixels (flood fill), not meant as a colour.</summary>
        public uint GetRaw(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
            int i = y * Width + x;
            return Indices != null ? Indices[i] : Pixels[i];
        }

        /// <summary>The palette index at (x, y): in palette mode the stored one, otherwise the entry that comes closest to the pixel colour. 0 outside.</summary>
        public byte GetIndex(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
            int i = y * Width + x;
            return Indices != null ? Indices[i] : Palette.FindNearest(new PixelColor(Pixels[i]));
        }

        // -----------------------------------------------------------
        // Mask for the ImageSlicer
        // -----------------------------------------------------------

        /// <summary>A NEW palette framebuffer of the same size that contains the mask of this image: index 1 (white) = this pixel is to be milled out,
        /// index 0 (black, also <see cref="TransparentIndex"/>) = not. A pixel with a lower opacity than `alphaThreshold` never counts;
        /// otherwise the brightness (0.299 R + 0.587 G + 0.114 B) against `threshold` decides: `darkIsRemoved` = dark pixels are milled out, otherwise bright ones.
        /// For a palette image the palette is evaluated only once per entry.</summary>
        public Framebuffer ToMask(byte threshold = 128, bool darkIsRemoved = true, byte alphaThreshold = 128)
        {
            var mask = new Framebuffer(Width, Height, ColorMode.Indexed);
            mask.Palette.SetColor(0, unchecked((int)PixelColor.Black.Packed));
            mask.Palette.SetColor(1, unchecked((int)PixelColor.White.Packed));
            mask.TransparentIndex = 0;

            byte Decide(uint packed)
            {
                if ((packed >> 24) < alphaThreshold) return 0;
                double lum = 0.299 * (packed & 0xFF) + 0.587 * ((packed >> 8) & 0xFF) + 0.114 * ((packed >> 16) & 0xFF);
                bool dark = lum < threshold;
                return (darkIsRemoved ? dark : !dark) ? (byte)1 : (byte)0;
            }

            if (Indices != null)
            {
                var table = new byte[256];
                for (int i = 0; i < 256; i++) table[i] = Decide(Palette.GetPacked((byte)i));
                for (int i = 0; i < Indices.Length; i++) mask.Indices![i] = table[Indices[i]];
            }
            else
                for (int i = 0; i < Pixels.Length; i++) mask.Indices![i] = Decide(Pixels[i]);

            mask.MarkDirty();
            return mask;
        }

        // -----------------------------------------------------------
        // Drawing with a resolved colour (see ResolvePixel)
        // -----------------------------------------------------------

        /// <summary>A pixel, silently clipped outside the buffer.</summary>
        public void Plot(int x, int y, in Pixel brush)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
            int i = y * Width + x;
            if (Indices != null)
            {
                Indices[i] = brush.Index;
                _dirty = true;
            }
            else Pixels[i] = brush.Rgba;
        }

        /// <summary>A horizontal line from `x0` to `x1` (both included, in any order) in row `y`, clipped.</summary>
        public void HLine(int x0, int x1, int y, in Pixel brush)
        {
            if ((uint)y >= (uint)Height) return;
            if (x1 < x0) (x0, x1) = (x1, x0);
            x0 = Math.Max(0, x0);
            x1 = Math.Min(Width - 1, x1);
            if (x1 < x0) return;
            if (Indices != null)
            {
                Indices.AsSpan(y * Width + x0, x1 - x0 + 1).Fill(brush.Index);
                _dirty = true;
            }
            else Pixels.AsSpan(y * Width + x0, x1 - x0 + 1).Fill(brush.Rgba);
        }

        public void FillRect(int x, int y, int w, int h, in Pixel brush)
        {
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
            int x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
            if (x1 <= x0) return;
            if (Indices != null)
            {
                for (int yy = y0; yy < y1; yy++)
                    Indices.AsSpan(yy * Width + x0, x1 - x0).Fill(brush.Index);
                _dirty = true;
            }
            else
                for (int yy = y0; yy < y1; yy++)
                    Pixels.AsSpan(yy * Width + x0, x1 - x0).Fill(brush.Rgba);
        }

        public void Clear(in Pixel brush)
        {
            if (Indices != null)
            {
                Array.Fill(Indices, brush.Index);
                _dirty = true;
            }
            else Array.Fill(Pixels, brush.Rgba);
        }

        /// <summary>Deliberately does NOT write coordinates lying outside
        /// the buffer (silent clipping instead of an exception) - a
        /// graphics operation that partly extends beyond the edge (e.g.
        /// a line, a rectangle at the screen edge) should
        /// still draw the visible part, not fail completely. In
        /// palette mode the colour is mapped to the nearest palette entry.</summary>
        public void SetPixel(int x, int y, PixelColor color)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
            if (Indices == null)
            {
                Pixels[y * Width + x] = color.Packed;
                return;
            }
            Plot(x, y, ResolvePixel(Paint.FromRgba(color)));
        }

        /// <summary>The colour of the pixel (in palette mode via the palette); outside: transparent.</summary>
        public PixelColor GetPixel(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return PixelColor.Transparent;
            if (Indices != null) return Palette.GetColor(Indices[y * Width + x]);
            return new PixelColor(Pixels[y * Width + x]);
        }

        public void Clear(PixelColor color) => Clear(ResolvePixel(Paint.FromRgba(color)));

        public void FillRect(int x, int y, int w, int h, PixelColor color) =>
            FillRect(x, y, w, h, ResolvePixel(Paint.FromRgba(color)));

        /// <summary>Shifts the ENTIRE content up by `pixelRows` pixel
        /// rows (basis for terminal scrolling, see
        /// Renderer.NewLine) - the bottom `pixelRows` rows are filled
        /// with `fill`. What falls out at the top is IRREVOCABLY
        /// lost (no scrollback buffer, as required in SPEC/CONSOLE.md)
        /// - this method deliberately keeps no history.</summary>
        public void ScrollUp(int pixelRows, PixelColor fill) => ScrollUp(pixelRows, ResolvePixel(Paint.FromRgba(fill)));

        public void ScrollUp(int pixelRows, in Pixel fill)
        {
            if (pixelRows <= 0) return;
            if (pixelRows >= Height)
            {
                Clear(fill);
                return;
            }
            if (Indices != null)
            {
                Array.Copy(Indices, pixelRows * Width, Indices, 0, (Height - pixelRows) * Width);
                Array.Fill(Indices, fill.Index, (Height - pixelRows) * Width, pixelRows * Width);
                _dirty = true;
                return;
            }
            Array.Copy(Pixels, pixelRows * Width, Pixels, 0, (Height - pixelRows) * Width);
            Array.Fill(Pixels, fill.Rgba, (Height - pixelRows) * Width, pixelRows * Width);
        }
    }
}
