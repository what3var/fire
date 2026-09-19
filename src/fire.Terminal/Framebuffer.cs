using System;

namespace fire.Terminal
{
    /// <summary>
    /// Ein roher, direkt zugreifbarer Pixel-Puffer (R,G,B,A pro Pixel, siehe
    /// PixelColor - zeilenweise) - das zentrale Objekt dieser Bibliothek: TerminalCanvas (Terminal-
    /// Emulation UND rohe Grafikoperationen) schreibt IMMER hierhin, nie
    /// direkt in ein Fenster; ein IFramebufferRenderer (z.B. SDL) liest den
    /// fertigen Inhalt nur noch aus, um ihn darzustellen. Dadurch bleibt der
    /// Puffer selbst komplett unabhängig vom Rendering-Backend UND
    /// direkt aus Code adressierbar (Pixels-Array), z.B. um ihn zu
    /// speichern, zu vergleichen oder in einen anderen Puffer zu kopieren.
    ///
    /// Bewusst KEIN Alpha-Blending beim Schreiben (SetPixel/FillRect/...
    /// überschreiben ein Zielpixel immer vollständig, inklusive seines
    /// Alpha-Werts) - "optional transparent" (siehe TerminalCanvas.
    /// Background-Doku) bedeutet hier "eine Zelle NICHT mit Hintergrund
    /// überschreiben", nicht "mit Transparenz vermischen". Ein Renderer, der
    /// den Framebuffer seinerseits über eine bereits vorhandene Szene legt
    /// (z.B. Alpha-Compositing mehrerer Fenster), kann den Alpha-Kanal
    /// trotzdem auswerten - er wird hier nur nicht selbst verrechnet.
    /// </summary>
    public sealed class Framebuffer
    {
        public int Width { get; }
        public int Height { get; }

        /// <summary>Ein uint pro Pixel, zeilenweise (Index = y * Width + x) -
        /// jedes uint sind exakt PixelColor.Packed dieses Pixels (R,G,B,A in
        /// genau dieser Byte-Reihenfolge, siehe PixelColor-Doku) - direkt
        /// zugreifbar für alles, was mehr braucht als die Methoden dieser
        /// Klasse (Serialisierung, Diffing, Kopieren in einen zweiten Puffer
        /// per Array.Copy, oder ein späterer Byte-genauer Blick aus der
        /// Skriptsprache heraus).</summary>
        public uint[] Pixels { get; }

        public Framebuffer(int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "Framebuffer-Größe muss positiv sein.");
            Width = width;
            Height = height;
            Pixels = new uint[width * height];
        }

        /// <summary>Schreibt außerhalb des Puffers liegende Koordinaten
        /// bewusst NICHT (stilles Clipping statt Exception) - eine
        /// Grafikoperation, die teilweise über den Rand hinausragt (z.B.
        /// eine Linie, ein Rechteck am Bildschirmrand), soll den sichtbaren
        /// Teil trotzdem zeichnen, nicht komplett fehlschlagen.</summary>
        public void SetPixel(int x, int y, PixelColor color)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
            Pixels[y * Width + x] = color.Packed;
        }

        public PixelColor GetPixel(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return PixelColor.Transparent;
            return new PixelColor(Pixels[y * Width + x]);
        }

        public void Clear(PixelColor color)
        {
            Array.Fill(Pixels, color.Packed);
        }

        public void FillRect(int x, int y, int w, int h, PixelColor color)
        {
            uint packed = color.Packed;
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y);
            int x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
            for (int yy = y0; yy < y1; yy++)
            {
                int rowStart = yy * Width;
                for (int xx = x0; xx < x1; xx++)
                    Pixels[rowStart + xx] = packed;
            }
        }

        /// <summary>Verschiebt den GESAMTEN Inhalt um `pixelRows` Pixel-
        /// zeilen nach OBEN (Grundlage für Terminal-Scrolling, siehe
        /// TerminalCanvas.NewLine) - die untersten `pixelRows` Zeilen werden
        /// mit `fill` aufgefüllt. Was oben herausfällt, ist UNWIDERRUFLICH
        /// verloren (kein Scrollback-Puffer, wie in SPEC/CONSOLE.md
        /// gefordert) - diese Methode hält absichtlich keine Historie vor.</summary>
        public void ScrollUp(int pixelRows, PixelColor fill)
        {
            if (pixelRows <= 0) return;
            if (pixelRows >= Height)
            {
                Clear(fill);
                return;
            }
            Array.Copy(Pixels, pixelRows * Width, Pixels, 0, (Height - pixelRows) * Width);
            Array.Fill(Pixels, fill.Packed, (Height - pixelRows) * Width, pixelRows * Width);
        }
    }
}
