using System;
using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>
    /// Ein Pinsel: sagt, wie eine Fläche gefüllt wird. Er bietet die Füllfunktionen an (Rechteck, Kreis, Ellipse, Dreieck, Polygon, Flächenfüllung); der <see cref="Renderer"/> reicht nur seine
    /// Fläche (<see cref="Surface"/>) hinein. Die Farbe ist eine Farbangabe (<see cref="Paint"/>: Palette-Index 0-255 oder direkter RGBA-Wert); ihr Alpha wird gemischt, wenn die Surface
    /// Blending hat (siehe dort).
    ///
    /// Der Pinsel ist abstrakt, damit weitere Arten (Muster, Bild) dazukommen können; vorerst gibt es den einfarbigen <see cref="SolidBrush"/>.
    /// </summary>
    public abstract class Brush
    {
        /// <summary>Die Farbe des Pixels (x, y) für diese Fläche; für einfarbige Pinsel immer dieselbe.</summary>
        public abstract Pixel PixelAt(in Surface surface, int x, int y);

        /// <summary>Hat der Pinsel überall dieselbe Farbe (dann sind Spans und Zeichen mit Schnellwegen möglich)?</summary>
        public virtual bool IsUniform => true;

        /// <summary>Die Farbangabe eines einfarbigen Pinsels (sonst null).</summary>
        public virtual Paint? SolidPaint => null;

        // ---- die Senke, die einen Pinsel anwendet ----

        private struct FillSink : IPixelSink
        {
            public Surface Surface;
            public Brush Brush;
            public Pixel Uniform;
            public bool IsUniform;

            public void Point(int x, int y) => Surface.Put(x, y, IsUniform ? Uniform : Brush.PixelAt(Surface, x, y));

            public void Span(int y, int x0, int x1)
            {
                if (IsUniform) { Surface.Span(y, x0, x1, Uniform); return; }
                if (x1 < x0) (x0, x1) = (x1, x0);
                for (int x = Math.Max(x0, 0); x <= x1 && x < Surface.Width; x++) Surface.Put(x, y, Brush.PixelAt(Surface, x, y));
            }
        }

        private FillSink Sink(in Surface surface)
        {
            bool uniform = IsUniform;
            return new FillSink { Surface = surface, Brush = this, IsUniform = uniform, Uniform = uniform ? PixelAt(surface, 0, 0) : default };
        }

        // ---- Füllfunktionen ----

        public void FillSpan(in Surface surface, int y, int x0, int x1) { var s = Sink(surface); s.Span(y, x0, x1); }

        public void FillPoint(in Surface surface, int x, int y) { var s = Sink(surface); s.Point(x, y); }

        public void FillRect(in Surface surface, int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            var s = Sink(surface);
            int y0 = Math.Max(0, y), y1 = (int)Math.Min((long)surface.Height, (long)y + h);
            int xr = (int)Math.Min((long)x + w - 1, int.MaxValue);
            for (int yy = y0; yy < y1; yy++) s.Span(yy, x, xr);
        }

        public void FillCircle(in Surface surface, int cx, int cy, int r) { var s = Sink(surface); Shapes.FillCircle(ref s, cx, cy, r); }

        public void FillEllipse(in Surface surface, int cx, int cy, int rx, int ry) { var s = Sink(surface); Shapes.FillEllipse(ref s, cx, cy, rx, ry); }

        public void FillTriangle(in Surface surface, int x0, int y0, int x1, int y1, int x2, int y2)
        {
            var s = Sink(surface);
            Shapes.FillTriangle(ref s, x0, y0, x1, y1, x2, y2, surface.Height);
        }

        /// <summary>`points` = x0, y0, x1, y1, ... (Even-Odd-Regel, die Randpixel gehören dazu).</summary>
        public void FillPolygon(in Surface surface, int[] points)
        {
            var s = Sink(surface);
            Shapes.FillPolygon(ref s, points, surface.Height);
        }

        // ---- Flächenfüllung ----

        /// <summary>Füllt die zusammenhängende Fläche (4er-Nachbarschaft) um (x, y), die denselben Pixelwert hat wie der Startpunkt. Liegt der Startpunkt außerhalb
        /// oder hat er schon die Füllfarbe (deckend gefüllt), geschieht nichts.</summary>
        public void FloodFill(in Surface surface, int x, int y) => Flood(surface, x, y, hasBorder: false, border: default);

        /// <summary>Wie <see cref="FloodFill"/>, aber die Fläche wird von Pixeln der Farbe `border` begrenzt (wie `PAINT x, y, farbe, rand` in QBasic): gefüllt wird alles,
        /// was nicht Rand- und nicht Füllfarbe ist.</summary>
        public void FloodFillBorder(in Surface surface, int x, int y, Paint border) => Flood(surface, x, y, hasBorder: true, border: surface.Resolve(border));

        private void Flood(in Surface surface, int x, int y, bool hasBorder, Pixel border)
        {
            if ((uint)x >= (uint)surface.Width || (uint)y >= (uint)surface.Height) return;
            bool indexed = surface.IsIndexed;
            var fillPixel = PixelAt(surface, x, y);
            uint fill = indexed ? fillPixel.Index : fillPixel.Rgba;
            uint target = surface.Raw(x, y);
            uint borderRaw = indexed ? border.Index : border.Rgba;
            bool copy = !IsUniform ? false : surface.IsCopy(fillPixel);

            if (!hasBorder && target == fill && copy) return;
            if (hasBorder && (target == borderRaw || (target == fill && copy))) return;

            // Zuerst die Fläche bestimmen (die Pixel bleiben dabei unberührt), dann füllen: so ändert das Füllen - auch das Mischen einer
            // halbdurchsichtigen Farbe - nicht, was zur Fläche gehört.
            int w = surface.Width, h = surface.Height;
            var sf = surface;
            var seen = new bool[w * h];
            bool Fillable(int px, int py)
            {
                if (seen[py * w + px]) return false;
                uint v = sf.Raw(px, py);
                return hasBorder ? v != borderRaw && !(copy && v == fill) : v == target;
            }

            var spans = new List<(int Y, int X0, int X1)>();
            var stack = new Stack<(int X, int Y)>();
            stack.Push((x, y));
            while (stack.Count > 0)
            {
                var (sx, sy) = stack.Pop();
                if (!Fillable(sx, sy)) continue;

                int left = sx, right = sx;
                while (left > 0 && Fillable(left - 1, sy)) left--;
                while (right < w - 1 && Fillable(right + 1, sy)) right++;
                for (int px = left; px <= right; px++) seen[sy * w + px] = true;
                spans.Add((sy, left, right));

                for (int ny = sy - 1; ny <= sy + 1; ny += 2)
                {
                    if ((uint)ny >= (uint)h) continue;
                    bool inRun = false;
                    for (int px = left; px <= right; px++)
                    {
                        bool ok = Fillable(px, ny);
                        if (ok && !inRun) stack.Push((px, ny));
                        inRun = ok;
                    }
                }
            }

            var s = Sink(surface);
            foreach (var (sy, x0, x1) in spans) s.Span(sy, x0, x1);
        }
    }

    /// <summary>Ein einfarbiger Pinsel.</summary>
    public sealed class SolidBrush : Brush
    {
        /// <summary>Die Farbe (Palette-Index 0-255 oder direkter RGBA-Wert, siehe <see cref="Paint"/>).</summary>
        public Paint Color { get; set; }

        public SolidBrush(Paint color) => Color = color;

        public override Pixel PixelAt(in Surface surface, int x, int y) => surface.Resolve(Color);

        public override Paint? SolidPaint => Color;
    }
}
