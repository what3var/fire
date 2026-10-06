using System;
using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>Wie <see cref="Blitter.Blit"/> mit durchsichtigen Pixeln der Quelle umgeht.</summary>
    public enum BlitMode
    {
        /// <summary>Alle Pixel werden kopiert.</summary>
        Copy = 0,

        /// <summary>Durchsichtige Pixel der Quelle bleiben unberührt: bei einer RGBA-Quelle die mit Alpha 0, bei einer Palette-Quelle die mit dem
        /// Farbschlüssel (sonst dem <see cref="Framebuffer.TransparentIndex"/> der Quelle).</summary>
        Transparent = 1,

        /// <summary>Wie Transparent, aber teilweise durchsichtige Pixel einer RGBA-Quelle werden nach ihrem Alpha-Wert mit dem Ziel gemischt
        /// (nur in einem RGBA-Ziel; in einem Palette-Ziel zählt ein Pixel ab Alpha 128 als deckend).</summary>
        Blend = 2,
    }

    /// <summary>
    /// Kopiert Ausschnitte zwischen Framebuffern (Sprites, geladene Bilder), wahlweise skaliert (nächster Nachbar) und gespiegelt, über die
    /// Farbmodi hinweg: Palette-Quelle in RGBA-Ziel über die Palette der Quelle, RGBA-Quelle in Palette-Ziel auf den nächsten Eintrag der
    /// Ziel-Palette, Palette in Palette über die Paletten (direkt, wenn sie gleich sind).
    /// </summary>
    public static class Blitter
    {
        /// <summary>Kopiert den Ausschnitt (sx, sy, sw, sh) von `src` in das Rechteck (dx, dy, dw, dh) von `dst`.
        /// Ist `dw` oder `dh` negativ, wird in der jeweiligen Richtung gespiegelt (|dw| x |dh| Pixel ab (dx, dy)); sind sie 0, geschieht nichts.
        /// Quell- und Zielgröße dürfen verschieden sein (Skalierung durch den nächsten Nachbarn). Ausschnitt und Ziel werden beschnitten.
        /// `colorKey` (nur Palette-Quelle, Modus Transparent/Blend): der Index, der durchsichtig ist; -1 = der TransparentIndex der Quelle.</summary>
        public static void Blit(IRenderTarget dst, IRenderTarget src, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh,
            BlitMode mode = BlitMode.Copy, int colorKey = -1, bool blend = true)
        {
            // ohne Alpha-Blending (Renderer.AlphaBlending) mischt auch der Modus Blend nicht: er verhält sich wie Transparent
            if (!blend && mode == BlitMode.Blend) mode = BlitMode.Transparent;
            bool srcIndexed = src.Indices != null, dstIndexed = dst.Indices != null;
            if (sw <= 0 || sh <= 0 || dw == 0 || dh == 0) return;

            long absDw = Math.Abs((long)dw), absDh = Math.Abs((long)dh);
            bool flipX = dw < 0, flipY = dh < 0;

            // Quelle und Ziel sind derselbe Puffer: von einer Kopie lesen, damit sich überlappende Bereiche nicht selbst überschreiben
            if (ReferenceEquals(dst, src)) src = Snapshot(src);

            // Quell-Pixel eines Ziel-Pixels: sxp = sx + floor((i + 0.5) * sw / absDw) - die Mitte des Zielpixels bestimmt das Quellpixel
            Span<uint> srcTable = stackalloc uint[256];
            if (srcIndexed) src.Palette.CopyPacked(srcTable);

            Span<byte> map = stackalloc byte[256];                // Palette-Quelle -> Palette-Ziel
            bool directIndices = srcIndexed && dstIndexed && SamePalette(src.Palette, dst.Palette);
            if (srcIndexed && dstIndexed && !directIndices)
                for (int i = 0; i < 256; i++) map[i] = dst.Palette.FindNearest(new PixelColor(srcTable[i]));

            int key = srcIndexed ? (colorKey >= 0 ? colorKey : src.TransparentIndex) : -1;

            // nur die sichtbaren Zielzeilen/-spalten durchlaufen
            long dxStart = Math.Max(0, -(long)dx), dxEnd = Math.Min(absDw, dst.Width - (long)dx);
            long dyStart = Math.Max(0, -(long)dy), dyEnd = Math.Min(absDh, dst.Height - (long)dy);
            if (dxEnd <= dxStart || dyEnd <= dyStart) return;

            // ein Zwischenspeicher für Nachschlagen im Palette-Ziel bei RGBA-Quelle (viele gleiche Farben, die Suche kostet 256 Vergleiche)
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

        /// <summary>Der ganze Inhalt von `src` mit der linken oberen Ecke bei (dx, dy), ohne Skalierung.</summary>
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
