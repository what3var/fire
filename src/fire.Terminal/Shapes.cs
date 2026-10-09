using System;
using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>
    /// The drawing algorithms (line, circle, ellipse, triangle, polygon, area fill) on an <see cref="IPixelSink"/> - the same for both colour modes, because only single pixels and horizontal spans are delivered. Everything is silently clipped at the edge (a shape that lies partly outside shows its visible
    /// part). Pure integer arithmetic, without floating point: the same pixels on every platform.
    /// </summary>
    public static class Shapes
    {
        // The algorithms deliver only pixels and spans to a sink (<see cref="IPixelSink"/>): a brush fills them, a pen stamps them. The sinks are structs and the methods
        // generic - the JIT generates its own code per sink without an interface call per pixel.

        /// <summary>Bresenham line algorithm - no external dependency, works identically regardless of the rendering backend.</summary>
        public static void Line<TSink>(ref TSink sink, int x0, int y0, int x1, int y1) where TSink : IPixelSink
        {
            long dxl = Math.Abs((long)x1 - x0), dyl = Math.Abs((long)y1 - y0);
            // A horizontal line is a single span (the most common case for frames and fills).
            if (y0 == y1) { sink.Span(y0, x0, x1); return; }
            if (dxl > int.MaxValue / 2 || dyl > int.MaxValue / 2) return; // absurd coordinates: draw nothing instead of overflowing

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
        // Circle and ellipse
        //
        // The area is the set of pixels (dx, dy) around the centre with (2dx)^2/a^2 + (2dy)^2/b^2 <= 1, a = 2rx+1, b = 2ry+1 (the extent
        // in pixels: a pixel of the centre has width 1). For a circle this is dx^2 + dy^2 <= r^2 + r - the well-known round shape.
        // Line and fill come from the same spans, the line is the pixels of the area at the edge (with an outer neighbour).
        // -----------------------------------------------------------

        /// <summary>Largest permitted radius (larger ones are limited to it): the calculation needs (2r+1)^4 in 64 bit.</summary>
        public const int MaxRadius = 1 << 14;

        /// <summary>Circle line around (cx, cy) with radius `r` (r = 0: one pixel; negative: nothing).</summary>
        public static void Circle<TSink>(ref TSink sink, int cx, int cy, int r) where TSink : IPixelSink => Ellipse(ref sink, cx, cy, r, r);

        /// <summary>Filled circle (covers exactly the area that <see cref="Circle"/> encloses, including the line).</summary>
        public static void FillCircle<TSink>(ref TSink sink, int cx, int cy, int r) where TSink : IPixelSink => FillEllipse(ref sink, cx, cy, r, r);

        /// <summary>Half width of the ellipse per row `dy` = 0..ry (index = dy): the largest `dx` that still belongs to the area.</summary>
        private static int[] EllipseSpans(int rx, int ry)
        {
            long a = 2L * rx + 1, b = 2L * ry + 1;
            long a2 = a * a, b2 = b * b;
            var spans = new int[ry + 1];
            for (int dy = 0; dy <= ry; dy++)
            {
                // largest dx with 4*dx^2*b^2 <= a^2 * (b^2 - 4*dy^2)
                long rhs = a2 * (b2 - 4L * dy * dy);
                long dx = (long)Math.Sqrt((double)rhs / (4.0 * b2));
                while (dx > 0 && 4 * dx * dx * b2 > rhs) dx--;
                while (4 * (dx + 1) * (dx + 1) * b2 <= rhs) dx++;
                spans[dy] = (int)Math.Min(dx, rx);
            }
            return spans;
        }

        /// <summary>Ellipse line around (cx, cy) with the semi-axes `rx` (horizontal) and `ry` (vertical); a negative radius draws nothing.</summary>
        public static void Ellipse<TSink>(ref TSink sink, int cx, int cy, int rx, int ry) where TSink : IPixelSink
        {
            if (rx < 0 || ry < 0) return;
            rx = Math.Min(rx, MaxRadius);
            ry = Math.Min(ry, MaxRadius);
            var spans = EllipseSpans(rx, ry);
            for (int dy = 0; dy <= ry; dy++)
            {
                int dx = spans[dy];
                // the outer neighbour towards the tip: the row above/below; outside the ellipse (-1) everything is edge
                int neighbor = dy < ry ? spans[dy + 1] : -1;
                if (dy == 0) neighbor = ry >= 1 ? Math.Min(neighbor, spans[1]) : -1;
                int from = Math.Min(neighbor + 1, dx);
                for (int sign = -1; sign <= 1; sign += 2)       // links/rechts
                    for (int rowSign = -1; rowSign <= 1; rowSign += 2)   // oben/unten
                    {
                        if (dy == 0 && rowSign == 1) continue;  // the middle row only once
                        int y = cy + rowSign * dy;
                        // the pixels from `from` to `dx` distance from the centre (on this side)
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
        // Triangle and polygon
        // -----------------------------------------------------------

        public static void Triangle<TSink>(ref TSink sink, int x0, int y0, int x1, int y1, int x2, int y2) where TSink : IPixelSink
        {
            Line(ref sink, x0, y0, x1, y1);
            Line(ref sink, x1, y1, x2, y2);
            Line(ref sink, x2, y2, x0, y0);
        }

        public static void FillTriangle<TSink>(ref TSink sink, int x0, int y0, int x1, int y1, int x2, int y2, int clipHeight) where TSink : IPixelSink =>
            FillPolygon(ref sink, new[] { x0, y0, x1, y1, x2, y2 }, clipHeight);

        /// <summary>Outline through the points `points` (x0, y0, x1, y1, ...); `closed` connects the last to the first. Fewer than two points: nothing.</summary>
        public static void Polygon<TSink>(ref TSink sink, int[] points, bool closed = true) where TSink : IPixelSink
        {
            int n = points.Length / 2;
            if (n < 2) return;
            for (int i = 0; i + 1 < n; i++)
                Line(ref sink, points[2 * i], points[2 * i + 1], points[2 * i + 2], points[2 * i + 3]);
            if (closed && n > 2)
                Line(ref sink, points[2 * (n - 1)], points[2 * (n - 1) + 1], points[0], points[1]);
        }

        /// <summary>Fills the polygon through `points` (even-odd rule, as with a pen without winding number: overlapping parts stay empty).
        /// The edge pixels belong to it (the outline is drawn as well). Fewer than three points: only a stroke/point.</summary>
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
                    if (ay == by) continue;                       // horizontal edges do not cut a row
                    if (ay > by) { (ax, bx) = (bx, ax); (ay, by) = (by, ay); }
                    if (y < ay || y >= by) continue;              // half-open [ay, by): a vertex does not count twice
                    // x of the edge in row y, rounded to the nearest pixel
                    long num = (long)(bx - ax) * (y - ay);
                    long den = by - ay;
                    long x = ax + (num >= 0 ? (2 * num + den) / (2 * den) : -((2 * -num + den) / (2 * den)));
                    crossings.Add((int)Math.Clamp(x, int.MinValue / 2, int.MaxValue / 2));
                }
                crossings.Sort();
                for (int i = 0; i + 1 < crossings.Count; i += 2)
                    sink.Span(y, crossings[i], crossings[i + 1]);
            }

            Polygon(ref sink, points, closed: true); // the outline belongs to the area (and closes gaps caused by the rounding)
        }
    }
}
