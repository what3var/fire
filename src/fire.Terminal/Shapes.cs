using System;
using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>
    /// Die Zeichenalgorithmen (Linie, Kreis, Ellipse, Dreieck, Polygon, Flächenfüllung) auf einem <see cref="Framebuffer"/> mit einer schon
    /// aufgelösten <see cref="Brush"/> - gleich für beide Farbmodi, denn geschrieben wird nur über <see cref="Framebuffer.Plot"/> und
    /// <see cref="Framebuffer.HLine"/>. Alles wird still am Rand beschnitten (eine Form, die teilweise außerhalb liegt, zeigt ihren sichtbaren
    /// Teil). Reine Ganzzahl-Arithmetik, ohne Fließkomma: dieselben Pixel auf jeder Plattform.
    /// </summary>
    public static class Shapes
    {
        /// <summary>Bresenham-Linienalgorithmus - keine externe Abhängigkeit, funktioniert identisch unabhängig vom Rendering-Backend.</summary>
        public static void Line(Framebuffer fb, int x0, int y0, int x1, int y1, in Brush brush)
        {
            long dxl = Math.Abs((long)x1 - x0), dyl = Math.Abs((long)y1 - y0);
            // Eine waagerechte Linie ist ein einziger Span (der häufigste Fall bei Rahmen und Füllungen).
            if (y0 == y1) { fb.HLine(x0, x1, y0, brush); return; }
            if (dxl > int.MaxValue / 2 || dyl > int.MaxValue / 2) return; // absurde Koordinaten: nichts zeichnen statt überlaufen

            int dx = (int)dxl, sx = x0 < x1 ? 1 : -1;
            int dy = -(int)dyl, sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                fb.Plot(x0, y0, brush);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        public static void Rect(Framebuffer fb, int x, int y, int w, int h, in Brush brush)
        {
            if (w <= 0 || h <= 0) return;
            fb.HLine(x, x + w - 1, y, brush);
            if (h > 1) fb.HLine(x, x + w - 1, y + h - 1, brush);
            for (int yy = y + 1; yy < y + h - 1; yy++)
            {
                fb.Plot(x, yy, brush);
                if (w > 1) fb.Plot(x + w - 1, yy, brush);
            }
        }

        // -----------------------------------------------------------
        // Kreis und Ellipse
        //
        // Die Fläche ist die Menge der Pixel (dx, dy) um die Mitte mit (2dx)^2/a^2 + (2dy)^2/b^2 <= 1, a = 2rx+1, b = 2ry+1 (die Ausdehnung
        // in Pixeln: ein Pixel der Mitte hat die Breite 1). Für einen Kreis ist das dx^2 + dy^2 <= r^2 + r - die bekannte runde Form.
        // Linie und Füllung kommen aus denselben Spans, die Linie sind die Pixel der Fläche am Rand (mit einem äußeren Nachbarn).
        // -----------------------------------------------------------

        /// <summary>Größter erlaubter Radius (größere werden darauf begrenzt): die Rechnung braucht (2r+1)^4 in 64 Bit.</summary>
        public const int MaxRadius = 1 << 14;

        /// <summary>Kreislinie um (cx, cy) mit Radius `r` (r = 0: ein Pixel; negativ: nichts).</summary>
        public static void Circle(Framebuffer fb, int cx, int cy, int r, in Brush brush) => Ellipse(fb, cx, cy, r, r, brush);

        /// <summary>Gefüllter Kreis (deckt genau die Fläche, die <see cref="Circle"/> umschließt, samt Linie).</summary>
        public static void FillCircle(Framebuffer fb, int cx, int cy, int r, in Brush brush) => FillEllipse(fb, cx, cy, r, r, brush);

        /// <summary>Halbbreite der Ellipse je Zeile `dy` = 0..ry (Index = dy): das größte `dx`, das noch zur Fläche gehört.</summary>
        private static int[] EllipseSpans(int rx, int ry)
        {
            long a = 2L * rx + 1, b = 2L * ry + 1;
            long a2 = a * a, b2 = b * b;
            var spans = new int[ry + 1];
            for (int dy = 0; dy <= ry; dy++)
            {
                // größtes dx mit 4*dx^2*b^2 <= a^2 * (b^2 - 4*dy^2)
                long rhs = a2 * (b2 - 4L * dy * dy);
                long dx = (long)Math.Sqrt((double)rhs / (4.0 * b2));
                while (dx > 0 && 4 * dx * dx * b2 > rhs) dx--;
                while (4 * (dx + 1) * (dx + 1) * b2 <= rhs) dx++;
                spans[dy] = (int)Math.Min(dx, rx);
            }
            return spans;
        }

        /// <summary>Ellipsenlinie um (cx, cy) mit den Halbachsen `rx` (waagerecht) und `ry` (senkrecht); ein negativer Radius zeichnet nichts.</summary>
        public static void Ellipse(Framebuffer fb, int cx, int cy, int rx, int ry, in Brush brush)
        {
            if (rx < 0 || ry < 0) return;
            rx = Math.Min(rx, MaxRadius);
            ry = Math.Min(ry, MaxRadius);
            var spans = EllipseSpans(rx, ry);
            for (int dy = 0; dy <= ry; dy++)
            {
                int dx = spans[dy];
                // der äußere Nachbar in Richtung Spitze: die Zeile darüber/darunter; außerhalb der Ellipse (-1) ist alles Rand
                int neighbor = dy < ry ? spans[dy + 1] : -1;
                if (dy == 0) neighbor = ry >= 1 ? Math.Min(neighbor, spans[1]) : -1;
                int from = Math.Min(neighbor + 1, dx);
                for (int sign = -1; sign <= 1; sign += 2)       // links/rechts
                    for (int rowSign = -1; rowSign <= 1; rowSign += 2)   // oben/unten
                    {
                        if (dy == 0 && rowSign == 1) continue;  // die Mittelzeile nur einmal
                        int y = cy + rowSign * dy;
                        // die Pixel von `from` bis `dx` Abstand zur Mitte (auf dieser Seite)
                        fb.HLine(cx + sign * from, cx + sign * dx, y, brush);
                    }
            }
        }

        public static void FillEllipse(Framebuffer fb, int cx, int cy, int rx, int ry, in Brush brush)
        {
            if (rx < 0 || ry < 0) return;
            rx = Math.Min(rx, MaxRadius);
            ry = Math.Min(ry, MaxRadius);
            var spans = EllipseSpans(rx, ry);
            for (int dy = 0; dy <= ry; dy++)
            {
                fb.HLine(cx - spans[dy], cx + spans[dy], cy + dy, brush);
                if (dy != 0) fb.HLine(cx - spans[dy], cx + spans[dy], cy - dy, brush);
            }
        }

        // -----------------------------------------------------------
        // Dreieck und Polygon
        // -----------------------------------------------------------

        public static void Triangle(Framebuffer fb, int x0, int y0, int x1, int y1, int x2, int y2, in Brush brush)
        {
            Line(fb, x0, y0, x1, y1, brush);
            Line(fb, x1, y1, x2, y2, brush);
            Line(fb, x2, y2, x0, y0, brush);
        }

        public static void FillTriangle(Framebuffer fb, int x0, int y0, int x1, int y1, int x2, int y2, in Brush brush) =>
            FillPolygon(fb, new[] { x0, y0, x1, y1, x2, y2 }, brush);

        /// <summary>Umriss durch die Punkte `points` (x0, y0, x1, y1, ...); `closed` verbindet den letzten mit dem ersten. Weniger als zwei Punkte: nichts.</summary>
        public static void Polygon(Framebuffer fb, int[] points, in Brush brush, bool closed = true)
        {
            int n = points.Length / 2;
            if (n < 2) return;
            for (int i = 0; i + 1 < n; i++)
                Line(fb, points[2 * i], points[2 * i + 1], points[2 * i + 2], points[2 * i + 3], brush);
            if (closed && n > 2)
                Line(fb, points[2 * (n - 1)], points[2 * (n - 1) + 1], points[0], points[1], brush);
        }

        /// <summary>Füllt das Polygon durch `points` (Even-Odd-Regel, wie bei einem Stift ohne Windungszahl: sich überschneidende Teile bleiben leer).
        /// Die Randpixel gehören dazu (der Umriss wird mitgezeichnet). Weniger als drei Punkte: nur ein Strich/Punkt.</summary>
        public static void FillPolygon(Framebuffer fb, int[] points, in Brush brush)
        {
            int n = points.Length / 2;
            if (n < 3) { Polygon(fb, points, brush, closed: false); return; }

            long minY = long.MaxValue, maxY = long.MinValue;
            for (int i = 0; i < n; i++)
            {
                minY = Math.Min(minY, points[2 * i + 1]);
                maxY = Math.Max(maxY, points[2 * i + 1]);
            }
            int yFrom = (int)Math.Max(minY, 0), yTo = (int)Math.Min(maxY, fb.Height - 1);

            var crossings = new List<int>();
            for (int y = yFrom; y <= yTo; y++)
            {
                crossings.Clear();
                for (int i = 0; i < n; i++)
                {
                    int ax = points[2 * i], ay = points[2 * i + 1];
                    int bx = points[2 * ((i + 1) % n)], by = points[2 * ((i + 1) % n) + 1];
                    if (ay == by) continue;                       // waagerechte Kanten schneiden keine Zeile
                    if (ay > by) { (ax, bx) = (bx, ax); (ay, by) = (by, ay); }
                    if (y < ay || y >= by) continue;              // halboffen [ay, by): ein Eckpunkt zählt nicht doppelt
                    // x der Kante in Zeile y, auf das nächste Pixel gerundet
                    long num = (long)(bx - ax) * (y - ay);
                    long den = by - ay;
                    long x = ax + (num >= 0 ? (2 * num + den) / (2 * den) : -((2 * -num + den) / (2 * den)));
                    crossings.Add((int)Math.Clamp(x, int.MinValue / 2, int.MaxValue / 2));
                }
                crossings.Sort();
                for (int i = 0; i + 1 < crossings.Count; i += 2)
                    fb.HLine(crossings[i], crossings[i + 1], y, brush);
            }

            Polygon(fb, points, brush, closed: true); // der Umriss gehört zur Fläche (und schließt Lücken durch die Rundung)
        }

        // -----------------------------------------------------------
        // Flächenfüllung
        // -----------------------------------------------------------

        /// <summary>Füllt die zusammenhängende Fläche (4er-Nachbarschaft) um (x, y), die denselben Pixelwert hat wie der Startpunkt, mit `brush`.
        /// Liegt der Startpunkt außerhalb oder hat er schon die Füllfarbe, geschieht nichts.</summary>
        public static void FloodFill(Framebuffer fb, int x, int y, in Brush brush) =>
            Flood(fb, x, y, brush, hasBorder: false, border: 0);

        /// <summary>Wie <see cref="FloodFill"/>, aber die Fläche wird von Pixeln der Farbe `border` begrenzt (wie `PAINT x, y, farbe, rand` in QBasic):
        /// gefüllt wird alles, was nicht Rand- und nicht Füllfarbe ist.</summary>
        public static void FloodFillBorder(Framebuffer fb, int x, int y, in Brush brush, in Brush border) =>
            Flood(fb, x, y, brush, hasBorder: true, border: fb.IsIndexed ? border.Index : border.Rgba);

        private static void Flood(Framebuffer fb, int x, int y, in Brush brush, bool hasBorder, uint border)
        {
            if ((uint)x >= (uint)fb.Width || (uint)y >= (uint)fb.Height) return;
            uint fill = fb.IsIndexed ? brush.Index : brush.Rgba;
            uint target = fb.GetRaw(x, y);

            // gefüllt wird, was zum Startwert gehört (ohne Rand) bzw. alles außer Rand und Füllfarbe (mit Rand)
            bool Fillable(int px, int py)
            {
                uint v = fb.GetRaw(px, py);
                return hasBorder ? v != border && v != fill : v == target;
            }

            if (!hasBorder && target == fill) return;
            if (hasBorder && (target == border || target == fill)) return;

            // Zeilenweise (Scanline) mit eigenem Stapel: kein rekursiver Aufruf, also keine Stapelüberläufe bei großen Flächen
            var stack = new Stack<(int X, int Y)>();
            stack.Push((x, y));
            while (stack.Count > 0)
            {
                var (sx, sy) = stack.Pop();
                if (!Fillable(sx, sy)) continue;

                int left = sx, right = sx;
                while (left > 0 && Fillable(left - 1, sy)) left--;
                while (right < fb.Width - 1 && Fillable(right + 1, sy)) right++;
                fb.HLine(left, right, sy, brush);

                for (int ny = sy - 1; ny <= sy + 1; ny += 2)
                {
                    if ((uint)ny >= (uint)fb.Height) continue;
                    bool inRun = false;
                    for (int px = left; px <= right; px++)
                    {
                        bool ok = Fillable(px, ny);
                        if (ok && !inRun) stack.Push((px, ny));
                        inRun = ok;
                    }
                }
            }
        }
    }
}
