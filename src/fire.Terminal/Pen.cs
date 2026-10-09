using System;
using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>The shape of the pen tip.</summary>
    public enum PenShape
    {
        /// <summary>Round: the pixels that belong to the disc with the diameter of the pen width (width 1 and 2: one pixel/four pixels, like a square).</summary>
        Round = 0,

        /// <summary>Square: a square of the pen width.</summary>
        Square = 1,
    }

    /// <summary>
    /// A pen: draws points, lines and paths (and thus the outlines of the shapes) with a tip of `Width` pixels. The tip is rendered ONCE beforehand (the rows
    /// of the stamp area); drawing is done by copying it at every pixel of the line - blended with alpha blending if the colour is semi-transparent and the
    /// surface is a 32-bit target with blending. So that the overlapping stamps of a semi-transparent line are not blended several times (the line would get darker),
    /// the union of all stamps is determined first and this is blended ONCE.
    ///
    /// The colour is a colour specification (<see cref="Paint"/>). Width 1 draws exactly the pixels of <see cref="Shapes"/>.
    /// </summary>
    public sealed class Pen
    {
        /// <summary>Largest width (larger ones are limited to it).</summary>
        public const int MaxWidth = 512;

        private readonly struct StampRow
        {
            public readonly int Dy, Dx0, Dx1;
            public StampRow(int dy, int dx0, int dx1) { Dy = dy; Dx0 = dx0; Dx1 = dx1; }
        }

        private StampRow[] _stamp = Array.Empty<StampRow>();
        private int _width = 1;
        private PenShape _shape = PenShape.Round;

        public Paint Color { get; set; }

        public int Width
        {
            get => _width;
            set { _width = Math.Clamp(value, 1, MaxWidth); Render(); }
        }

        public PenShape Shape
        {
            get => _shape;
            set { _shape = value; Render(); }
        }

        public Pen(Paint color, int width = 1, PenShape shape = PenShape.Round)
        {
            Color = color;
            _width = Math.Clamp(width, 1, MaxWidth);
            _shape = shape;
            Render();
        }

        /// <summary>Renders the tip beforehand: per row the span of pixels relative to the drawn point.</summary>
        private void Render()
        {
            int w = _width, half = (w - 1) / 2;
            var rows = new List<StampRow>(w);
            for (int j = 0; j < w; j++)
            {
                int first = -1, last = -1;
                for (int i = 0; i < w; i++)
                {
                    bool inside = _shape == PenShape.Square
                        || (long)(2 * i - (w - 1)) * (2 * i - (w - 1)) + (long)(2 * j - (w - 1)) * (2 * j - (w - 1)) <= (long)w * w - 1;
                    if (!inside) continue;
                    if (first < 0) first = i;
                    last = i;
                }
                if (first >= 0) rows.Add(new StampRow(j - half, first - half, last - half));
            }
            _stamp = rows.ToArray();
        }

        // ---- the sinks ----

        /// <summary>Stamps the tip at every pixel (copying or blending per pixel).</summary>
        private struct StampSink : IPixelSink
        {
            public Surface Surface;
            public Pixel Pixel;
            public StampRow[] Stamp;

            public void Point(int x, int y)
            {
                if (Stamp.Length == 1) { Surface.Span(y + Stamp[0].Dy, x + Stamp[0].Dx0, x + Stamp[0].Dx1, Pixel); return; }
                foreach (var row in Stamp) Surface.Span(y + row.Dy, x + row.Dx0, x + row.Dx1, Pixel);
            }

            public void Span(int y, int x0, int x1)
            {
                if (x1 < x0) (x0, x1) = (x1, x0);
                foreach (var row in Stamp) Surface.Span(y + row.Dy, x0 + row.Dx0, x1 + row.Dx1, Pixel);
            }
        }

        /// <summary>Collects the spans of all stamps; <see cref="Flush"/> blends their union once.</summary>
        private struct UnionSink : IPixelSink
        {
            public Surface Surface;
            public Pixel Pixel;
            public StampRow[] Stamp;
            public List<(int Y, int X0, int X1)> Spans;

            public void Point(int x, int y)
            {
                foreach (var row in Stamp) Spans.Add((y + row.Dy, x + row.Dx0, x + row.Dx1));
            }

            public void Span(int y, int x0, int x1)
            {
                if (x1 < x0) (x0, x1) = (x1, x0);
                foreach (var row in Stamp) Spans.Add((y + row.Dy, x0 + row.Dx0, x1 + row.Dx1));
            }

            public void Flush()
            {
                Spans.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X0.CompareTo(b.X0));
                int i = 0;
                while (i < Spans.Count)
                {
                    var (y, x0, x1) = Spans[i++];
                    while (i < Spans.Count && Spans[i].Y == y && Spans[i].X0 <= x1 + 1)
                    {
                        x1 = Math.Max(x1, Spans[i].X1);
                        i++;
                    }
                    Surface.Span(y, x0, x1, Pixel);
                }
            }
        }

        private interface IShape
        {
            void Emit<TSink>(ref TSink sink) where TSink : IPixelSink;
        }

        private void Stroke<TShape>(in Surface surface, TShape shape) where TShape : IShape
        {
            var pixel = surface.Resolve(Color);
            if (!surface.Visible(pixel)) return;
            if (surface.IsCopy(pixel))
            {
                var sink = new StampSink { Surface = surface, Pixel = pixel, Stamp = _stamp };
                shape.Emit(ref sink);
            }
            else
            {
                var sink = new UnionSink { Surface = surface, Pixel = pixel, Stamp = _stamp, Spans = new List<(int, int, int)>() };
                shape.Emit(ref sink);
                sink.Flush();
            }
        }

        private readonly struct PointShape : IShape
        {
            private readonly int _x, _y;
            public PointShape(int x, int y) { _x = x; _y = y; }
            public void Emit<TSink>(ref TSink sink) where TSink : IPixelSink => sink.Point(_x, _y);
        }

        private readonly struct LineShape : IShape
        {
            private readonly int _x0, _y0, _x1, _y1;
            public LineShape(int x0, int y0, int x1, int y1) { _x0 = x0; _y0 = y0; _x1 = x1; _y1 = y1; }
            public void Emit<TSink>(ref TSink sink) where TSink : IPixelSink => Shapes.Line(ref sink, _x0, _y0, _x1, _y1);
        }

        private readonly struct RectShape : IShape
        {
            private readonly int _x, _y, _w, _h;
            public RectShape(int x, int y, int w, int h) { _x = x; _y = y; _w = w; _h = h; }
            public void Emit<TSink>(ref TSink sink) where TSink : IPixelSink => Shapes.Rect(ref sink, _x, _y, _w, _h);
        }

        private readonly struct EllipseShape : IShape
        {
            private readonly int _cx, _cy, _rx, _ry;
            public EllipseShape(int cx, int cy, int rx, int ry) { _cx = cx; _cy = cy; _rx = rx; _ry = ry; }
            public void Emit<TSink>(ref TSink sink) where TSink : IPixelSink => Shapes.Ellipse(ref sink, _cx, _cy, _rx, _ry);
        }

        private readonly struct TriangleShape : IShape
        {
            private readonly int _x0, _y0, _x1, _y1, _x2, _y2;
            public TriangleShape(int x0, int y0, int x1, int y1, int x2, int y2) { _x0 = x0; _y0 = y0; _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2; }
            public void Emit<TSink>(ref TSink sink) where TSink : IPixelSink => Shapes.Triangle(ref sink, _x0, _y0, _x1, _y1, _x2, _y2);
        }

        private readonly struct PathShape : IShape
        {
            private readonly int[] _points;
            private readonly bool _closed;
            public PathShape(int[] points, bool closed) { _points = points; _closed = closed; }
            public void Emit<TSink>(ref TSink sink) where TSink : IPixelSink => Shapes.Polygon(ref sink, _points, _closed);
        }

        // ---- Zeichnen ----

        /// <summary>A point: the tip, centred on (x, y).</summary>
        public void DrawPoint(in Surface surface, int x, int y) => Stroke(surface, new PointShape(x, y));

        /// <summary>A line (Bresenham) from (x0, y0) to (x1, y1), both end points included.</summary>
        public void DrawLine(in Surface surface, int x0, int y0, int x1, int y1) => Stroke(surface, new LineShape(x0, y0, x1, y1));

        /// <summary>A path through the points `points` (x0, y0, x1, y1, ...); `closed` connects the last to the first. Fewer than two points: nothing.</summary>
        public void DrawPath(in Surface surface, int[] points, bool closed = false) => Stroke(surface, new PathShape(points, closed));

        // The outlines of the shapes are paths made of the pixels of Shapes.
        public void DrawRect(in Surface surface, int x, int y, int w, int h) => Stroke(surface, new RectShape(x, y, w, h));
        public void DrawCircle(in Surface surface, int cx, int cy, int r) => Stroke(surface, new EllipseShape(cx, cy, r, r));
        public void DrawEllipse(in Surface surface, int cx, int cy, int rx, int ry) => Stroke(surface, new EllipseShape(cx, cy, rx, ry));
        public void DrawTriangle(in Surface surface, int x0, int y0, int x1, int y1, int x2, int y2) => Stroke(surface, new TriangleShape(x0, y0, x1, y1, x2, y2));
        public void DrawPolygon(in Surface surface, int[] points, bool closed = true) => Stroke(surface, new PathShape(points, closed));
    }
}
