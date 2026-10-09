using System;
using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>
    /// A brush: says how an area is filled. It offers the fill functions (rectangle, circle, ellipse, triangle, polygon, area fill); the <see cref="Renderer"/> only hands in its
    /// surface (<see cref="Surface"/>). The colour is a colour specification (<see cref="Paint"/>: palette index 0-255 or direct RGBA value); its alpha is blended if the surface
    /// has blending (see there).
    ///
    /// The brush is abstract so that further kinds (pattern, image) can be added; for now there is the single-colour <see cref="SolidBrush"/>.
    /// </summary>
    public abstract class Brush
    {
        /// <summary>The colour of the pixel (x, y) for this area; always the same for single-colour brushes.</summary>
        public abstract Pixel PixelAt(in Surface surface, int x, int y);

        /// <summary>Does the brush have the same colour everywhere (then spans and characters can use fast paths)?</summary>
        public virtual bool IsUniform => true;

        /// <summary>The colour specification of a single-colour brush (otherwise null).</summary>
        public virtual Paint? SolidPaint => null;

        // ---- the sink that applies a brush ----

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

        // ---- fill functions ----

        public void FillSpan(in Surface surface, int y, int x0, int x1) { var s = Sink(surface); s.Span(y, x0, x1); }

        public void FillPoint(in Surface surface, int x, int y) { var s = Sink(surface); s.Point(x, y); }

        public void FillRect(in Surface surface, int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            var s = Sink(surface);
            int y0 = Math.Max(surface.ClipTop, y), y1 = (int)Math.Min((long)surface.ClipBottom, (long)y + h);
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

        /// <summary>`points` = x0, y0, x1, y1, ... (even-odd rule, the edge pixels belong to it).</summary>
        public void FillPolygon(in Surface surface, int[] points)
        {
            var s = Sink(surface);
            Shapes.FillPolygon(ref s, points, surface.Height);
        }

        // ---- area fill ----

        /// <summary>Fills the connected area (4-neighbourhood) around (x, y) that has the same pixel value as the starting point. If the starting point is outside
        /// or already has the fill colour (opaque fill), nothing happens.</summary>
        public void FloodFill(in Surface surface, int x, int y) => Flood(surface, x, y, hasBorder: false, border: default);

        /// <summary>Like <see cref="FloodFill"/>, but the area is bounded by pixels of the colour `border` (like `PAINT x, y, colour, border` in QBasic): everything is filled
        /// that is neither border nor fill colour.</summary>
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

            // First determine the area (the pixels stay untouched meanwhile), then fill: this way filling - also blending a
            // semi-transparent colour - does not change what belongs to the area.
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

    /// <summary>A single-colour brush.</summary>
    public sealed class SolidBrush : Brush
    {
        /// <summary>The colour (palette index 0-255 or direct RGBA value, see <see cref="Paint"/>).</summary>
        public Paint Color { get; set; }

        public SolidBrush(Paint color) => Color = color;

        public override Pixel PixelAt(in Surface surface, int x, int y) => surface.Resolve(Color);

        public override Paint? SolidPaint => Color;
    }
}
