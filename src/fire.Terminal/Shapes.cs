using System;
using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>
    /// Die Zeichenalgorithmen (Linie, Kreis, Ellipse, Dreieck, Polygon, Flächenfüllung) an eine <see cref="IPixelSink"/> - gleich für beide Farbmodi, denn geliefert werden nur einzelne Pixel und waagerechte Spans. Alles wird still am Rand beschnitten (eine Form, die teilweise außerhalb liegt, zeigt ihren sichtbaren
    /// Teil). Reine Ganzzahl-Arithmetik, ohne Fließkomma: dieselben Pixel auf jeder Plattform.
    /// </summary>
    public static class Shapes
    {
        // Die Algorithmen liefern nur Pixel und Spans an eine Senke (<see cref="IPixelSink"/>): ein Pinsel füllt sie, ein Stift stempelt sie. Die Senken sind Strukturen und die Methoden
        // generisch - der JIT erzeugt je Senke einen eigenen Code ohne Schnittstellenaufruf je Pixel.

        /// <summary>Bresenham-Linienalgorithmus - keine externe Abhängigkeit, funktioniert identisch unabhängig vom Rendering-Backend.</summary>
        public static void Line<TSink>(ref TSink sink, int x0, int y0, int x1, int y1) where TSink : IPixelSink
        {
            long dxl = Math.Abs((long)x1 - x0), dyl = Math.Abs((long)y1 - y0);
            // Eine waagerechte Linie ist ein einziger Span (der häufigste Fall bei Rahmen und Füllungen).
            if (y0 == y1) { sink.Span(y0, x0, x1); return; }
            if (dxl > int.MaxValue / 2 || dyl > int.MaxValue / 2) return; // absurde Koordinaten: nichts zeichnen statt überlaufen

            int dx = (int)dxl, sx = x0 < x1 ? 1 : -1;
            int dy = -(int)dyl, sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                sink.Point(x0, y0);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        public static void Rect<TSink>(ref TSink sink, int x, int y, int w, int h) where TSink : IPixelSink
        {
            if (w <= 0 || h <= 0) return;
            sink.Span(y, x, x + w - 1);
            if (h > 1) sink.Span(y + h - 1, x, x + w - 1);
            for (int yy = y + 1; yy < y + h - 1; yy++)
            {
                sink.Point(x, yy);
                if (w > 1) sink.Point(x + w - 1, yy);
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
        public static void Circle<TSink>(ref TSink sink, int cx, int cy, int r) where TSink : IPixelSink => Ellipse(ref sink, cx, cy, r, r);

        /// <summary>Gefüllter Kreis (deckt genau die Fläche, die <see cref="Circle"/> umschließt, samt Linie).</summary>
        public static void FillCircle<TSink>(ref TSink sink, int cx, int cy, int r) where TSink : IPixelSink => FillEllipse(ref sink, cx, cy, r, r);

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
        public static void Ellipse<TSink>(ref TSink sink, int cx, int cy, int rx, int ry) where TSink : IPixelSink
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
                        sink.Span(y, cx + sign * from, cx + sign * dx);
                    }
            }
        }

        public static void FillEllipse<TSink>(ref TSink sink, int cx, int cy, int rx, int ry) where TSink : IPixelSink
        {
            if (rx < 0 || ry < 0) return;
            rx = Math.Min(rx, MaxRadius);
            ry = Math.Min(ry, MaxRadius);
            var spans = EllipseSpans(rx, ry);
            for (int dy = 0; dy <= ry; dy++)
            {
                sink.Span(cy + dy, cx - spans[dy], cx + spans[dy]);
                if (dy != 0) sink.Span(cy - dy, cx - spans[dy], cx + spans[dy]);
            }
        }

        // -----------------------------------------------------------
        // Dreieck und Polygon
        // -----------------------------------------------------------

        public static void Triangle<TSink>(ref TSink sink, int x0, int y0, int x1, int y1, int x2, int y2) where TSink : IPixelSink
        {
            Line(ref sink, x0, y0, x1, y1);
            Line(ref sink, x1, y1, x2, y2);
            Line(ref sink, x2, y2, x0, y0);
        }

        public static void FillTriangle<TSink>(ref TSink sink, int x0, int y0, int x1, int y1, int x2, int y2, int clipHeight) where TSink : IPixelSink =>
            FillPolygon(ref sink, new[] { x0, y0, x1, y1, x2, y2 }, clipHeight);

        /// <summary>Umriss durch die Punkte `points` (x0, y0, x1, y1, ...); `closed` verbindet den letzten mit dem ersten. Weniger als zwei Punkte: nichts.</summary>
        public static void Polygon<TSink>(ref TSink sink, int[] points, bool closed = true) where TSink : IPixelSink
        {
            int n = points.Length / 2;
            if (n < 2) return;
            for (int i = 0; i + 1 < n; i++)
                Line(ref sink, points[2 * i], points[2 * i + 1], points[2 * i + 2], points[2 * i + 3]);
            if (closed && n > 2)
                Line(ref sink, points[2 * (n - 1)], points[2 * (n - 1) + 1], points[0], points[1]);
        }

        /// <summary>Füllt das Polygon durch `points` (Even-Odd-Regel, wie bei einem Stift ohne Windungszahl: sich überschneidende Teile bleiben leer).
        /// Die Randpixel gehören dazu (der Umriss wird mitgezeichnet). Weniger als drei Punkte: nur ein Strich/Punkt.</summary>
        public static void FillPolygon<TSink>(ref TSink sink, int[] points, int clipHeight) where TSink : IPixelSink
        {
            int n = points.Length / 2;
            if (n < 3) { Polygon(ref sink, points, closed: false); return; }

            long minY = long.MaxValue, maxY = long.MinValue;
            for (int i = 0; i < n; i++)
            {
                minY = Math.Min(minY, points[2 * i + 1]);
                maxY = Math.Max(maxY, points[2 * i + 1]);
            }
            int yFrom = (int)Math.Max(minY, 0), yTo = (int)Math.Min(maxY, clipHeight - 1);

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
                    sink.Span(y, crossings[i], crossings[i + 1]);
            }

            Polygon(ref sink, points, closed: true); // der Umriss gehört zur Fläche (und schließt Lücken durch die Rundung)
        }
    }
}
