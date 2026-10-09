using System;
using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>How <see cref="Blitter.Blit"/> deals with transparent pixels of the source.</summary>
    public enum BlitMode
    {
        /// <summary>All pixels are copied.</summary>
        Copy = 0,

        /// <summary>Transparent pixels of the source are left untouched: for an RGBA source those with alpha 0, for a palette source those with the
        /// colour key (otherwise the <see cref="Framebuffer.TransparentIndex"/> of the source).</summary>
        Transparent = 1,

        /// <summary>Like Transparent, but partially transparent pixels of an RGBA source are blended with the destination according to their alpha value
        /// (only in an RGBA destination; in a palette destination a pixel counts as opaque from alpha 128 up).</summary>
        Blend = 2,
    }

    /// <summary>
    /// Copies sections between framebuffers (sprites, loaded images), optionally scaled (nearest neighbour) and mirrored, across the
    /// colour modes: palette source into RGBA destination via the palette of the source, RGBA source into palette destination to the nearest entry of the
    /// destination palette, palette to palette via the palettes (directly if they are equal).
    /// </summary>
    public static class Blitter
    {
        /// <summary>Copies the section (sx, sy, sw, sh) of `src` into the rectangle (dx, dy, dw, dh) of `dst`.
        /// If `dw` or `dh` is negative, it is mirrored in the respective direction (|dw| x |dh| pixels from (dx, dy)); if they are 0, nothing happens.
        /// Source and destination size may differ (scaling by nearest neighbour). Section and destination are clipped.
        /// `colorKey` (palette source only, mode Transparent/Blend): the index that is transparent; -1 = the TransparentIndex of the source.
        /// `clipLeft`..`clipBottom`: drawing happens only within this rectangle of the destination (right and bottom excluded).</summary>
        public static void Blit(IRenderTarget dst, IRenderTarget src, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh,
            BlitMode mode = BlitMode.Copy, int colorKey = -1, bool blend = true, int clipLeft = 0, int clipTop = 0, int clipRight = int.MaxValue, int clipBottom = int.MaxValue)
        {
            // without alpha blending (Renderer.AlphaBlending) mode Blend does not blend either: it behaves like Transparent
            if (!blend && mode == BlitMode.Blend) mode = BlitMode.Transparent;
            bool srcIndexed = src.Indices != null, dstIndexed = dst.Indices != null;
            if (sw <= 0 || sh <= 0 || dw == 0 || dh == 0) return;

            long absDw = Math.Abs((long)dw), absDh = Math.Abs((long)dh);
            bool flipX = dw < 0, flipY = dh < 0;

            // source and destination are the same buffer: read from a copy so that overlapping areas do not overwrite themselves
            if (ReferenceEquals(dst, src)) src = Snapshot(src);

            // source pixel of a destination pixel: sxp = sx + floor((i + 0.5) * sw / absDw) - the centre of the destination pixel determines the source pixel
            Span<uint> srcTable = stackalloc uint[256];
            if (srcIndexed) src.Palette.CopyPacked(srcTable);

            Span<byte> map = stackalloc byte[256];                // Palette-Quelle -> Palette-Ziel
            bool directIndices = srcIndexed && dstIndexed && SamePalette(src.Palette, dst.Palette);
            if (srcIndexed && dstIndexed && !directIndices)
                for (int i = 0; i < 256; i++) map[i] = dst.Palette.FindNearest(new PixelColor(srcTable[i]));

            int key = srcIndexed ? (colorKey >= 0 ? colorKey : src.TransparentIndex) : -1;

            // only run through the visible destination rows/columns
            long dxStart = Math.Max(Math.Max(0, clipLeft) - (long)dx, 0), dxEnd = Math.Min(absDw, Math.Min(dst.Width, clipRight) - (long)dx);
            long dyStart = Math.Max(Math.Max(0, clipTop) - (long)dy, 0), dyEnd = Math.Min(absDh, Math.Min(dst.Height, clipBottom) - (long)dy);
            if (dxEnd <= dxStart || dyEnd <= dyStart) return;

            // a cache for lookups in the palette destination with an RGBA source (many identical colours, the search costs 256 comparisons)
            Dictionary<uint, byte>? nearest = !srcIndexed && dstIndexed ? new() : null;

            for (long j = dyStart; j < dyEnd; j++)
            {
                long fy = flipY ? absDh - 1 - j : j;
                long srcY = sy + (2 * fy + 1) * sh / (2 * absDh);
                if (srcY < 0 || srcY >= src.Height) continue;
                int dstY = (int)(dy + j);

                for (long i = dxStart; i < dxEnd; i++)
                {
                    long fx = flipX ? absDw - 1 - i : i;
                    long srcX = sx + (2 * fx + 1) * sw / (2 * absDw);
                    if (srcX < 0 || srcX >= src.Width) continue;
                    int dstX = (int)(dx + i);
                    int srcPos = (int)(srcY * src.Width + srcX);
                    int dstPos = dstY * dst.Width + dstX;

                    if (srcIndexed)
                    {
                        byte idx = src.Indices![srcPos];
                        if (mode != BlitMode.Copy && idx == key) continue;
                        if (dstIndexed)
                            dst.Indices![dstPos] = directIndices ? idx : map[idx];
                        else
                        {
                            uint c = srcTable[idx];
                            if (mode == BlitMode.Blend) c = Mix(dst.Pixels[dstPos], c);
                            dst.Pixels[dstPos] = c;
                        }
                    }
                    else
                    {
                        uint c = src.Pixels[srcPos];
                        uint alpha = c >> 24;
                        if (mode != BlitMode.Copy && alpha == 0) continue;
                        if (dstIndexed)
                        {
                            if (mode == BlitMode.Blend && alpha < 128) continue;
                            if (!nearest!.TryGetValue(c, out byte found))
                                nearest[c] = found = dst.Palette.FindNearest(new PixelColor(c));
                            dst.Indices![dstPos] = found;
                        }
                        else dst.Pixels[dstPos] = mode == BlitMode.Blend ? Mix(dst.Pixels[dstPos], c) : c;
                    }
                }
            }

            if (dstIndexed) dst.MarkDirty();
        }

        /// <summary>The entire content of `src` with its top left corner at (dx, dy), without scaling.</summary>
        public static void Blit(IRenderTarget dst, IRenderTarget src, int dx, int dy, BlitMode mode = BlitMode.Copy, int colorKey = -1, bool blend = true) =>
            Blit(dst, src, 0, 0, src.Width, src.Height, dx, dy, src.Width, src.Height, mode, colorKey, blend);

        private static uint Mix(uint dst, uint src) => Surface.Mix(dst, src);

        private static bool SamePalette(Palette a, Palette b)
        {
            if (ReferenceEquals(a, b)) return true;
            for (int i = 0; i < 256; i++)
                if (a.GetPacked((byte)i) != b.GetPacked((byte)i)) return false;
            return true;
        }

        private static IRenderTarget Snapshot(IRenderTarget fb)
        {
            var copy = new Framebuffer(fb.Width, fb.Height, fb.Mode) { TransparentIndex = fb.TransparentIndex };
            Array.Copy(fb.Pixels, copy.Pixels, fb.Pixels.Length);
            if (fb.Indices != null) Array.Copy(fb.Indices, copy.Indices!, fb.Indices.Length);
            copy.Palette.SetAll(PaletteOf(fb.Palette));
            return copy;
        }

        private static uint[] PaletteOf(Palette p)
        {
            var all = new uint[256];
            p.CopyPacked(all);
            return all;
        }
    }
}
