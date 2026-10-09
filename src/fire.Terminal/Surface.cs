using System;
using System.Runtime.CompilerServices;

namespace fire.Terminal
{
    /// <summary>
    /// The raster cores of the software renderer on an <see cref="IRenderTarget"/>: writing pixels and spans, with clipping at the edge and - if switched on - with
    /// alpha blending. A surface is formed from the target for each drawing call (it only remembers its fields), so that the loops run without an interface call per pixel.
    ///
    /// The rule for colours with alpha: in a 32-bit target with blending, alpha 255 is a copy, alpha 0 nothing, in between it is blended (<see cref="Mix"/>);
    /// without blending the colour is simply copied including its alpha. In an 8-bit target there is only copying: with blending a colour from alpha 128 up is copied (as an index) and
    /// one below it is not drawn, without blending it is always copied.
    /// </summary>
    public readonly struct Surface
    {
        public readonly IRenderTarget Target;
        public readonly uint[] Pixels;
        public readonly byte[]? Indices;
        public readonly int Width;
        public readonly int Height;

        /// <summary>Alpha-Blending eingeschaltet (siehe Klassen-Doku).</summary>
        public readonly bool Blend;

        /// <summary>The clipping rectangle: drawing happens only in x from ClipLeft to ClipRight - 1 and y from ClipTop to ClipBottom - 1 (inside the target; if not given, the whole target).</summary>
        public readonly int ClipLeft, ClipTop, ClipRight, ClipBottom;

        public Surface(IRenderTarget target, bool blend) : this(target, blend, 0, 0, target.Width, target.Height) { }

        public Surface(IRenderTarget target, bool blend, int clipLeft, int clipTop, int clipRight, int clipBottom)
        {
            Target = target;
            Pixels = target.Pixels;
            Indices = target.Indices;
            Width = target.Width;
            Height = target.Height;
            Blend = blend;
            ClipLeft = Math.Max(0, clipLeft);
            ClipTop = Math.Max(0, clipTop);
            ClipRight = Math.Min(Width, clipRight);
            ClipBottom = Math.Min(Height, clipBottom);
        }

        public bool IsIndexed => Indices != null;

        /// <summary>Turns a colour specification into the colour for THIS target (see <see cref="Paint"/>).</summary>
        public Pixel Resolve(Paint paint) => ResolvePixel(Target, paint);

        public static Pixel ResolvePixel(IRenderTarget target, Paint paint)
        {
            var palette = target.Palette;
            if (target.Mode == ColorMode.Indexed)
            {
                byte index = paint.IsIndex ? (byte)paint.Index : palette.FindNearest(new PixelColor(paint.Rgba));
                // the colour value stays the desired one (its alpha decides when drawing), only the index is the approximation
                return new Pixel(paint.IsIndex ? palette.GetPacked(index) : paint.Rgba, index);
            }
            return paint.IsIndex
                ? new Pixel(palette.GetPacked((byte)paint.Index), (byte)paint.Index)
                : new Pixel(paint.Rgba, 0);
        }

        /// <summary>Blends `src` (alpha a) over `dst`: dst*(255-a)/255 + src*a/255 per channel; the result is opaque if one of the two was opaque.</summary>
        public static uint Mix(uint dst, uint src)
        {
            uint a = src >> 24;
            if (a == 255 || (dst >> 24) == 0) return src;  // opaque, or the destination is itself transparent (nothing to blend with)
            if (a == 0) return dst;
            uint inv = 255 - a;
            uint r = ((src & 0xFF) * a + (dst & 0xFF) * inv + 127) / 255;
            uint g = (((src >> 8) & 0xFF) * a + ((dst >> 8) & 0xFF) * inv + 127) / 255;
            uint b = (((src >> 16) & 0xFF) * a + ((dst >> 16) & 0xFF) * inv + 127) / 255;
            uint outA = a + ((dst >> 24) * inv + 127) / 255;   // Alpha-Komposition "over"
            return r | (g << 8) | (b << 16) | (Math.Min(outA, 255u) << 24);
        }

        /// <summary>Is this colour drawn at all in this target (not completely transparent or, in the 8-bit target, at least half opaque)?</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Visible(in Pixel p)
        {
            if (!Blend) return true;
            uint a = p.Rgba >> 24;
            return Indices != null ? a >= 128 : a != 0;
        }

        /// <summary>Is the colour such that every pixel is simply copied (no blending needed)?</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsCopy(in Pixel p) => !Blend || Indices != null || (p.Rgba >> 24) == 255;

        /// <summary>A pixel, silently clipped outside the target.</summary>
        public void Put(int x, int y, in Pixel p)
        {
            if (x < ClipLeft || x >= ClipRight || y < ClipTop || y >= ClipBottom) return;
            if (!Visible(p)) return;
            int i = y * Width + x;
            if (Indices != null)
            {
                Indices[i] = p.Index;
                Target.MarkDirty();
            }
            else if (!Blend || (p.Rgba >> 24) == 255) Pixels[i] = p.Rgba;
            else Pixels[i] = Mix(Pixels[i], p.Rgba);
        }

        /// <summary>A horizontal line from `x0` to `x1` (both included, any order) in row `y`, clipped.</summary>
        public void Span(int y, int x0, int x1, in Pixel p)
        {
            if (y < ClipTop || y >= ClipBottom) return;
            if (x1 < x0) (x0, x1) = (x1, x0);
            x0 = Math.Max(ClipLeft, x0);
            x1 = Math.Min(ClipRight - 1, x1);
            if (x1 < x0 || !Visible(p)) return;
            int start = y * Width + x0, count = x1 - x0 + 1;
            if (Indices != null)
            {
                Indices.AsSpan(start, count).Fill(p.Index);
                Target.MarkDirty();
            }
            else if (!Blend || (p.Rgba >> 24) == 255) Pixels.AsSpan(start, count).Fill(p.Rgba);
            else
            {
                var row = Pixels.AsSpan(start, count);
                for (int i = 0; i < row.Length; i++) row[i] = Mix(row[i], p.Rgba);
            }
        }

        public void Rect(int x, int y, int w, int h, in Pixel p)
        {
            if (w <= 0 || h <= 0) return;
            int y0 = Math.Max(ClipTop, y), y1 = (int)Math.Min((long)ClipBottom, (long)y + h);
            long xr = (long)x + w - 1;
            for (int yy = y0; yy < y1; yy++) Span(yy, x, (int)Math.Min(xr, int.MaxValue), p);
        }

        /// <summary>The raw pixel value at (x, y) - in the 8-bit target the index, otherwise the colour value; 0 outside.</summary>
        public uint Raw(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
            int i = y * Width + x;
            return Indices != null ? Indices[i] : Pixels[i];
        }
    }
}
