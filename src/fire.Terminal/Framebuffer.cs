using System;

namespace fire.Terminal
{
    /// <summary>
    /// Ein roher, direkt zugreifbarer Pixel-Puffer (R,G,B,A pro Pixel, siehe
    /// PixelColor - zeilenweise) - das zentrale Objekt dieser Bibliothek: Renderer (Terminal-
    /// Emulation UND rohe Grafikoperationen) schreibt IMMER hierhin, nie
    /// direkt in ein Fenster; ein IFramebufferRenderer (z.B. SDL) liest den
    /// fertigen Inhalt nur noch aus, um ihn darzustellen. Dadurch bleibt der
    /// Puffer selbst komplett unabhängig vom Rendering-Backend UND
    /// direkt aus Code adressierbar (Pixels-Array), z.B. um ihn zu
    /// speichern, zu vergleichen oder in einen anderen Puffer zu kopieren.
    ///
    /// Bewusst KEIN Alpha-Blending beim Schreiben (SetPixel/FillRect/...
    /// überschreiben ein Zielpixel immer vollständig, inklusive seines
    /// Alpha-Werts) - "optional transparent" (siehe Renderer.
    /// Background-Doku) bedeutet hier "eine Zelle NICHT mit Hintergrund
    /// überschreiben", nicht "mit Transparenz vermischen". Ein Renderer, der
    /// den Framebuffer seinerseits über eine bereits vorhandene Szene legt
    /// (z.B. Alpha-Compositing mehrerer Fenster), kann den Alpha-Kanal
    /// trotzdem auswerten - er wird hier nur nicht selbst verrechnet.
    /// </summary>
    public sealed class Framebuffer : IRenderTarget
    {
        public int Width { get; }
        public int Height { get; }

        /// <summary>Wie die Pixel gespeichert werden (siehe <see cref="ColorMode"/>).</summary>
        public ColorMode Mode { get; }

        public bool IsIndexed => Mode == ColorMode.Indexed;

        /// <summary>Ein uint pro Pixel, zeilenweise (Index = y * Width + x) -
        /// jedes uint sind exakt PixelColor.Packed dieses Pixels (R,G,B,A in
        /// genau dieser Byte-Reihenfolge, siehe PixelColor-Doku) - direkt
        /// zugreifbar für alles, was mehr braucht als die Methoden dieser
        /// Klasse (Serialisierung, Diffing, Kopieren in einen zweiten Puffer
        /// per Array.Copy, oder ein späterer Byte-genauer Blick aus der
        /// Skriptsprache heraus).
        ///
        /// Im Palette-Modus (<see cref="ColorMode.Indexed"/>) ist das nur das ABBILD der Indizes (für Renderer und alles, was Farben
        /// liest): es wird bei Bedarf aus <see cref="Indices"/> und der Palette berechnet (<see cref="Resolve"/>) und ist zwischen
        /// zwei Zeichenoperationen NICHT aktuell. Schreiben hat dort keine Wirkung (der nächste Resolve überschreibt es).</summary>
        public uint[] Pixels { get; }

        /// <summary>Nur im Palette-Modus: ein Byte je Pixel, der Index in <see cref="Palette"/> (zeilenweise wie <see cref="Pixels"/>); sonst null.
        /// Wer es direkt beschreibt, ruft danach <see cref="MarkDirty"/> auf.</summary>
        public byte[]? Indices { get; }

        /// <summary>Die 256-Farben-Palette dieses Framebuffers. Im Palette-Modus bestimmt sie die sichtbaren Farben; im RGBA-Modus löst sie
        /// Palette-Indizes auf, die Zeichenfunktionen als Farbe erhalten (siehe <see cref="Paint"/>). Mehrere Konsolen auf demselben
        /// Framebuffer teilen sie.</summary>
        public Palette Palette { get; } = new();

        /// <summary>Palette-Modus: der Index, der in einem Bild als durchsichtig gilt (GIF-Transparenz, PNG-Palette mit Alpha 0), oder -1.
        /// <see cref="Blitter"/> überspringt im Modus "Transparent" Pixel mit diesem Index.</summary>
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

        // -----------------------------------------------------------
        // Palette-Modus: Indizes -> sichtbare Farben
        // -----------------------------------------------------------

        /// <summary>Palette-Modus: vermerkt, dass sich <see cref="Indices"/> von außen geändert haben (das nächste <see cref="Resolve"/> rechnet neu).</summary>
        public void MarkDirty() => _dirty = true;

        /// <summary>Palette-Modus: bringt <see cref="Pixels"/> auf den Stand der Indizes und der Palette - nur wenn sich seit dem letzten Mal
        /// etwas geändert hat. Renderer rufen das vor dem Anzeigen auf; im RGBA-Modus tut es nichts.</summary>
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

        /// <summary>Macht aus einer Farbangabe die Farbe für DIESEN Framebuffer (siehe <see cref="Paint"/>).</summary>
        public Pixel ResolvePixel(Paint paint) => Surface.ResolvePixel(this, paint);

        /// <summary>Der rohe Pixelwert an (x, y) - im Palette-Modus der Index, sonst der gepackte RGBA-Wert; 0 außerhalb. Zum Vergleichen
        /// von Pixeln (Flood-Fill), nicht als Farbe gedacht.</summary>
        public uint GetRaw(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
            int i = y * Width + x;
            return Indices != null ? Indices[i] : Pixels[i];
        }

        /// <summary>Der Palette-Index an (x, y): im Palette-Modus der gespeicherte, sonst der Eintrag, der der Pixelfarbe am nächsten kommt. 0 außerhalb.</summary>
        public byte GetIndex(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
            int i = y * Width + x;
            return Indices != null ? Indices[i] : Palette.FindNearest(new PixelColor(Pixels[i]));
        }

        // -----------------------------------------------------------
        // Maske für den ImageSlicer
        // -----------------------------------------------------------

        /// <summary>Ein NEUER Palette-Framebuffer gleicher Größe, der die Maske dieses Bildes enthält: Index 1 (weiß) = dieses Pixel soll ausgefräst werden,
        /// Index 0 (schwarz, zugleich <see cref="TransparentIndex"/>) = nicht. Ein Pixel mit geringerer Deckkraft als `alphaThreshold` zählt nie;
        /// sonst entscheidet die Helligkeit (0,299 R + 0,587 G + 0,114 B) gegen `threshold`: `darkIsRemoved` = dunkle Pixel werden ausgefräst, sonst helle.
        /// Bei einem Palette-Bild wird die Palette je Eintrag nur einmal bewertet.</summary>
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
        // Zeichnen mit einer aufgelösten Farbe (siehe ResolvePixel)
        // -----------------------------------------------------------

        /// <summary>Ein Pixel, außerhalb des Puffers still beschnitten.</summary>
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

        /// <summary>Eine waagerechte Linie von `x0` bis `x1` (beide eingeschlossen, in beliebiger Reihenfolge) in Zeile `y`, beschnitten.</summary>
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

        /// <summary>Schreibt außerhalb des Puffers liegende Koordinaten
        /// bewusst NICHT (stilles Clipping statt Exception) - eine
        /// Grafikoperation, die teilweise über den Rand hinausragt (z.B.
        /// eine Linie, ein Rechteck am Bildschirmrand), soll den sichtbaren
        /// Teil trotzdem zeichnen, nicht komplett fehlschlagen. Im
        /// Palette-Modus wird die Farbe auf den nächsten Palette-Eintrag abgebildet.</summary>
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

        /// <summary>Die Farbe des Pixels (im Palette-Modus über die Palette); außerhalb: durchsichtig.</summary>
        public PixelColor GetPixel(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return PixelColor.Transparent;
            if (Indices != null) return Palette.GetColor(Indices[y * Width + x]);
            return new PixelColor(Pixels[y * Width + x]);
        }

        public void Clear(PixelColor color) => Clear(ResolvePixel(Paint.FromRgba(color)));

        public void FillRect(int x, int y, int w, int h, PixelColor color) =>
            FillRect(x, y, w, h, ResolvePixel(Paint.FromRgba(color)));

        /// <summary>Verschiebt den GESAMTEN Inhalt um `pixelRows` Pixel-
        /// zeilen nach OBEN (Grundlage für Terminal-Scrolling, siehe
        /// Renderer.NewLine) - die untersten `pixelRows` Zeilen werden
        /// mit `fill` aufgefüllt. Was oben herausfällt, ist UNWIDERRUFLICH
        /// verloren (kein Scrollback-Puffer, wie in SPEC/CONSOLE.md
        /// gefordert) - diese Methode hält absichtlich keine Historie vor.</summary>
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
