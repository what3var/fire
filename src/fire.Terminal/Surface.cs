using System;
using System.Runtime.CompilerServices;

namespace fire.Terminal
{
    /// <summary>
    /// Die Rasterkerne des Software-Renderers auf einem <see cref="IRenderTarget"/>: Pixel und Spans schreiben, mit Beschneidung am Rand und - wenn eingeschaltet - mit
    /// Alpha-Blending. Eine Surface wird je Zeichenaufruf aus dem Ziel gebildet (sie merkt sich nur dessen Felder), damit die Schleifen ohne Schnittstellenaufruf je Pixel laufen.
    ///
    /// Die Regel für Farben mit Alpha: in einem 32-Bit-Ziel mit Blending ist Alpha 255 eine Kopie, Alpha 0 nichts, dazwischen wird gemischt (<see cref="Mix"/>);
    /// ohne Blending wird die Farbe samt Alpha einfach kopiert. In einem 8-Bit-Ziel gibt es nur Kopie: mit Blending wird eine Farbe ab Alpha 128 kopiert (als Index) und
    /// eine darunter nicht gezeichnet, ohne Blending immer kopiert.
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

        /// <summary>Das Beschneidungsrechteck: gezeichnet wird nur in x von ClipLeft bis ClipRight - 1 und y von ClipTop bis ClipBottom - 1 (innerhalb des Ziels; ohne Angabe das ganze Ziel).</summary>
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

        /// <summary>Macht aus einer Farbangabe die Farbe für DIESES Ziel (siehe <see cref="Paint"/>).</summary>
        public Pixel Resolve(Paint paint) => ResolvePixel(Target, paint);

        public static Pixel ResolvePixel(IRenderTarget target, Paint paint)
        {
            var palette = target.Palette;
            if (target.Mode == ColorMode.Indexed)
            {
                byte index = paint.IsIndex ? (byte)paint.Index : palette.FindNearest(new PixelColor(paint.Rgba));
                // der Farbwert bleibt der gewünschte (sein Alpha entscheidet beim Zeichnen), nur der Index ist die Näherung
                return new Pixel(paint.IsIndex ? palette.GetPacked(index) : paint.Rgba, index);
            }
            return paint.IsIndex
                ? new Pixel(palette.GetPacked((byte)paint.Index), (byte)paint.Index)
                : new Pixel(paint.Rgba, 0);
        }

        /// <summary>Mischt `src` (Alpha a) über `dst`: dst*(255-a)/255 + src*a/255 je Kanal; das Ergebnis ist deckend, wenn eines von beiden deckend war.</summary>
        public static uint Mix(uint dst, uint src)
        {
            uint a = src >> 24;
            if (a == 255 || (dst >> 24) == 0) return src;  // deckend, oder das Ziel ist selbst durchsichtig (nichts zum Mischen)
            if (a == 0) return dst;
            uint inv = 255 - a;
            uint r = ((src & 0xFF) * a + (dst & 0xFF) * inv + 127) / 255;
            uint g = (((src >> 8) & 0xFF) * a + ((dst >> 8) & 0xFF) * inv + 127) / 255;
            uint b = (((src >> 16) & 0xFF) * a + ((dst >> 16) & 0xFF) * inv + 127) / 255;
            uint outA = a + ((dst >> 24) * inv + 127) / 255;   // Alpha-Komposition "over"
            return r | (g << 8) | (b << 16) | (Math.Min(outA, 255u) << 24);
        }

        /// <summary>Wird diese Farbe in diesem Ziel überhaupt gezeichnet (nicht vollständig durchsichtig bzw. im 8-Bit-Ziel mindestens halb deckend)?</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Visible(in Pixel p)
        {
            if (!Blend) return true;
            uint a = p.Rgba >> 24;
            return Indices != null ? a >= 128 : a != 0;
        }

        /// <summary>Ist die Farbe so, dass jeder Pixel einfach kopiert wird (kein Mischen nötig)?</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsCopy(in Pixel p) => !Blend || Indices != null || (p.Rgba >> 24) == 255;

        /// <summary>Ein Pixel, außerhalb des Ziels still beschnitten.</summary>
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

        /// <summary>Eine waagerechte Linie von `x0` bis `x1` (beide eingeschlossen, beliebige Reihenfolge) in Zeile `y`, beschnitten.</summary>
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

        /// <summary>Der rohe Pixelwert an (x, y) - im 8-Bit-Ziel der Index, sonst der Farbwert; 0 außerhalb.</summary>
        public uint Raw(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
            int i = y * Width + x;
            return Indices != null ? Indices[i] : Pixels[i];
        }
    }
}
