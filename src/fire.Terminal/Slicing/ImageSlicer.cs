using System;
using System.Collections.Generic;

namespace fire.Terminal
{
    /// <summary>Punkt in Millimetern.</summary>
    public readonly struct PointD
    {
        public PointD(double x, double y) { X = x; Y = y; }
        public double X { get; }
        public double Y { get; }
        public override string ToString() => $"({X:0.###}; {Y:0.###})";
    }

    /// <summary>Art einer Bahn: Ausräumen der Fläche oder Schlichtkontur am Rand.</summary>
    public enum PathKind { Fill, Outline }

    /// <summary>Wie das Innere einer Fläche ausgeräumt wird.</summary>
    public enum FillStrategy
    {
        /// <summary>Konzentrische, konturparallele Bahnen (von innen nach außen).</summary>
        Contour,
        /// <summary>Horizontale Zickzack-Bahnen plus Randkontur.</summary>
        ZigZag,
        /// <summary>Nur die Randkontur, kein Ausräumen.</summary>
        OutlineOnly
    }

    /// <summary>Eine einzelne Werkzeugbahn (Linienzug der Werkzeugmitte).</summary>
    public sealed class ToolPath
    {
        public ToolPath(List<PointD> points, bool closed, PathKind kind)
        {
            Points = points;
            Closed = closed;
            Kind = kind;
        }

        public IReadOnlyList<PointD> Points { get; }
        public bool Closed { get; }
        public PathKind Kind { get; }
    }

    /// <summary>
    /// Zerlegt ein 2D-Binärbild in Linien (Werkzeugbahnen) mit fester Linienstärke.
    ///
    /// Vorgehen:
    ///  1. Euklidische Distanztransformation: Für jedes Pixel der zu fräsenden Fläche
    ///     wird der Abstand zum nächsten Rand berechnet.
    ///  2. Die Werkzeugmitte darf nur dort liegen, wo der Abstand >= Linienradius ist.
    ///     Die Isolinie auf genau diesem Niveau ist die Randkontur (subpixelgenau
    ///     per Marching Squares).
    ///  3. Das Innere wird mit Bahnen im Abstand "StepOver" gefüllt. Da StepOver kleiner
    ///     als die Linienstärke ist, überlappen sich benachbarte Bahnen.
    ///
    /// Alle Ausgabekoordinaten sind in Millimetern und beschreiben die Werkzeugmitte.
    ///
    /// Die Maske kommt aus einem Framebuffer (<see cref="Framebuffer.ToMask"/>), die Bahnen gehen als Liste von <see cref="ToolPath"/> zurück
    /// (in fire: `Slicer.Slice(maske)`).
    /// </summary>
    public sealed class ImageSlicer
    {
        private double _overlap = 0.5;

        /// <param name="lineWidth">Linienstärke bzw. Werkzeugdurchmesser in mm.</param>
        /// <param name="pixelSize">Größe eines Pixels in mm (z. B. 25.4 / dpi).</param>
        public ImageSlicer(double lineWidth, double pixelSize)
        {
            if (lineWidth <= 0) throw new ArgumentOutOfRangeException(nameof(lineWidth));
            if (pixelSize <= 0) throw new ArgumentOutOfRangeException(nameof(pixelSize));
            LineWidth = lineWidth;
            PixelSize = pixelSize;
        }

        /// <summary>Linienstärke / Werkzeugdurchmesser in mm.</summary>
        public double LineWidth { get; }

        /// <summary>Kantenlänge eines Pixels in mm.</summary>
        public double PixelSize { get; }

        /// <summary>
        /// Überlappung benachbarter Bahnen als Anteil der Linienstärke (0 … 0.95).
        /// 0.5 = jede Bahn überdeckt die vorherige zur Hälfte. Bei der Strategie
        /// Contour garantieren Werte >= 0.5, dass keine Restinseln in der Flächenmitte bleiben.
        /// </summary>
        public double Overlap
        {
            get => _overlap;
            set
            {
                if (value < 0 || value > 0.95) throw new ArgumentOutOfRangeException(nameof(value));
                _overlap = value;
            }
        }

        /// <summary>Strategie zum Ausräumen der Flächen.</summary>
        public FillStrategy Strategy { get; set; } = FillStrategy.Contour;

        /// <summary>
        /// Toleranz in mm für die Punktreduktion (Douglas-Peucker).
        /// NaN = automatisch (1/4 Pixel), 0 = keine Reduktion.
        /// </summary>
        public double SimplifyTolerance { get; set; } = double.NaN;

        /// <summary>true: Y-Achse zeigt nach oben (Maschinenkoordinaten), false: Bildkoordinaten.</summary>
        public bool FlipY { get; set; } = true;

        /// <summary>Abstand zwischen benachbarten Bahnen in mm.</summary>
        public double StepOver => LineWidth * (1.0 - Overlap);

        /// <summary>
        /// Erzeugt die Bahnen.
        /// </summary>
        /// <param name="mask">mask[x, y] == true bedeutet: dieses Pixel soll ausgefräst werden.</param>
        public List<ToolPath> Slice(bool[,] mask)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));

            int w = mask.GetLength(0);
            int h = mask.GetLength(1);
            double[,] dist = ComputeDistanceField(mask, w, h);

            double radiusPx = LineWidth / 2.0 / PixelSize;
            double stepPx = StepOver / PixelSize;
            // Distanzen werden von Pixelmitte zu Pixelmitte gemessen; der echte Rand liegt
            // eine halbe Pixelbreite weiter außen.
            double baseLevel = radiusPx + 0.5;
            double maxDist = Max(dist);

            var result = new List<ToolPath>();
            if (baseLevel > maxDist)
                return result; // Keine Stelle ist breit genug für die Linienstärke.

            List<ToolPath> outline = ToPaths(TraceContours(dist, baseLevel), h, PathKind.Outline);

            switch (Strategy)
            {
                case FillStrategy.Contour:
                    var rings = new List<ToolPath>();
                    for (double level = baseLevel + stepPx; level < maxDist; level += stepPx)
                        rings.AddRange(ToPaths(TraceContours(dist, level), h, PathKind.Fill));
                    rings.Reverse(); // innen beginnen, nach außen arbeiten
                    result.AddRange(rings);
                    break;

                case FillStrategy.ZigZag:
                    result.AddRange(Hatch(dist, baseLevel, stepPx, h));
                    break;
            }

            result.AddRange(outline); // Randkontur zuletzt als Schlichtbahn
            return result;
        }

        /// <summary>Erzeugt die Bahnen aus einem Framebuffer, der die Maske enthält (siehe <see cref="Framebuffer.ToMask"/>): jedes gesetzte Pixel
        /// soll ausgefräst werden (Palette-Framebuffer: Index ungleich 0; RGBA: eine nicht-schwarze, nicht durchsichtige Farbe).</summary>
        public List<ToolPath> Slice(Framebuffer mask)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            return Slice(MaskFromFramebuffer(mask));
        }

        /// <summary>Die Maske `mask[x, y]` aus einem Framebuffer: gesetzt ist ein Pixel mit Index ungleich 0 (Palette) bzw. mit sichtbarer, nicht-schwarzer Farbe (RGBA).</summary>
        public static bool[,] MaskFromFramebuffer(Framebuffer fb)
        {
            int w = fb.Width, h = fb.Height;
            var mask = new bool[w, h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    mask[x, y] = fb.Indices != null
                        ? fb.Indices[i] != 0
                        : (fb.Pixels[i] & 0x00FFFFFFu) != 0 && (fb.Pixels[i] >> 24) != 0;
                }
            return mask;
        }

        /// <summary>Hilfsfunktion: Graustufenbild (Zeile für Zeile) in eine Maske umwandeln.</summary>
        public static bool[,] MaskFromGrayscale(byte[] gray, int width, int height,
                                                byte threshold = 128, bool darkIsRemoved = true)
        {
            if (gray == null) throw new ArgumentNullException(nameof(gray));
            if (gray.Length < width * height) throw new ArgumentException("Array zu klein.", nameof(gray));

            var mask = new bool[width, height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    bool dark = gray[y * width + x] < threshold;
                    mask[x, y] = darkIsRemoved ? dark : !dark;
                }
            return mask;
        }

        // ------------------------------------------------------------------
        // Distanzfeld
        // ------------------------------------------------------------------

        /// <summary>
        /// Exakte euklidische Distanztransformation (Felzenszwalb/Huttenlocher).
        /// Das Ergebnis hat einen Rand von 1 Pixel (Wert 0), damit alle Konturen geschlossen sind.
        /// </summary>
        private static double[,] ComputeDistanceField(bool[,] mask, int w, int h)
        {
            int W = w + 2, H = h + 2;
            double inf = (double)(W + H) * (W + H); // größer als jede mögliche quadrierte Distanz
            var d = new double[W, H];

            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    bool inside = x >= 1 && x <= w && y >= 1 && y <= h && mask[x - 1, y - 1];
                    d[x, y] = inside ? inf : 0.0;
                }

            int n = Math.Max(W, H);
            var f = new double[n];
            var outp = new double[n];
            var v = new int[n];
            var z = new double[n + 1];

            for (int x = 0; x < W; x++)
            {
                for (int y = 0; y < H; y++) f[y] = d[x, y];
                Edt1D(f, H, outp, v, z);
                for (int y = 0; y < H; y++) d[x, y] = outp[y];
            }

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++) f[x] = d[x, y];
                Edt1D(f, W, outp, v, z);
                for (int x = 0; x < W; x++) d[x, y] = Math.Sqrt(outp[x]);
            }

            return d;
        }

        private static void Edt1D(double[] f, int n, double[] d, int[] v, double[] z)
        {
            int k = 0;
            v[0] = 0;
            z[0] = double.NegativeInfinity;
            z[1] = double.PositiveInfinity;

            for (int q = 1; q < n; q++)
            {
                double s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
                while (s <= z[k])
                {
                    k--;
                    s = ((f[q] + (double)q * q) - (f[v[k]] + (double)v[k] * v[k])) / (2.0 * q - 2.0 * v[k]);
                }
                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = double.PositiveInfinity;
            }

            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                double dq = q - v[k];
                d[q] = dq * dq + f[v[k]];
            }
        }

        private static double Max(double[,] d)
        {
            double m = 0;
            foreach (double value in d)
                if (value > m) m = value;
            return m;
        }

        // ------------------------------------------------------------------
        // Konturen (Marching Squares)
        // ------------------------------------------------------------------

        /// <summary>Liefert geschlossene Isolinien des Distanzfelds auf dem angegebenen Niveau (Gitterkoordinaten).</summary>
        private static List<List<(double X, double Y)>> TraceContours(double[,] d, double level)
        {
            int W = d.GetLength(0), H = d.GetLength(1);
            var adj = new Dictionary<long, List<long>>();

            long HKey(int x, int y) => ((long)y * W + x) << 1;       // Kante (x,y)-(x+1,y)
            long VKey(int x, int y) => (((long)y * W + x) << 1) | 1; // Kante (x,y)-(x,y+1)

            void AddHalf(long from, long to)
            {
                if (!adj.TryGetValue(from, out var list))
                {
                    list = new List<long>(2);
                    adj[from] = list;
                }
                list.Add(to);
            }

            void Link(long e1, long e2) { AddHalf(e1, e2); AddHalf(e2, e1); }

            for (int y = 0; y < H - 1; y++)
                for (int x = 0; x < W - 1; x++)
                {
                    double a = d[x, y], b = d[x + 1, y], c = d[x + 1, y + 1], e = d[x, y + 1];
                    int idx = (a >= level ? 8 : 0) | (b >= level ? 4 : 0) | (c >= level ? 2 : 0) | (e >= level ? 1 : 0);
                    if (idx == 0 || idx == 15) continue;

                    long T = HKey(x, y), R = VKey(x + 1, y), B = HKey(x, y + 1), L = VKey(x, y);
                    bool centerIn = (a + b + c + e) / 4.0 >= level;

                    switch (idx)
                    {
                        case 1: case 14: Link(L, B); break;
                        case 2: case 13: Link(B, R); break;
                        case 3: case 12: Link(L, R); break;
                        case 4: case 11: Link(T, R); break;
                        case 6: case 9: Link(T, B); break;
                        case 7: case 8: Link(L, T); break;
                        case 5: // b und d innen (Sattelpunkt)
                            if (centerIn) { Link(L, T); Link(B, R); }
                            else { Link(T, R); Link(L, B); }
                            break;
                        case 10: // a und c innen (Sattelpunkt)
                            if (centerIn) { Link(L, B); Link(T, R); }
                            else { Link(L, T); Link(B, R); }
                            break;
                    }
                }

            (double X, double Y) EdgePoint(long key)
            {
                long cell = key >> 1;
                int x = (int)(cell % W), y = (int)(cell / W);
                if ((key & 1) == 0)
                {
                    double v0 = d[x, y], v1 = d[x + 1, y];
                    return (x + (level - v0) / (v1 - v0), y);
                }
                else
                {
                    double v0 = d[x, y], v1 = d[x, y + 1];
                    return (x, y + (level - v0) / (v1 - v0));
                }
            }

            var visited = new HashSet<long>();
            var contours = new List<List<(double X, double Y)>>();

            foreach (long start in adj.Keys)
            {
                if (visited.Contains(start)) continue;

                var poly = new List<(double X, double Y)>();
                long prev = -1, cur = start;
                while (true)
                {
                    visited.Add(cur);
                    poly.Add(EdgePoint(cur));

                    var neighbours = adj[cur];
                    long next = neighbours[0] != prev ? neighbours[0]
                              : neighbours.Count > 1 ? neighbours[1] : -1;

                    if (next == -1 || next == start || visited.Contains(next)) break;
                    prev = cur;
                    cur = next;
                }

                if (poly.Count >= 2) contours.Add(poly);
            }

            return contours;
        }

        // ------------------------------------------------------------------
        // Zickzack-Füllung
        // ------------------------------------------------------------------

        private List<ToolPath> Hatch(double[,] d, double level, double stepPx, int h)
        {
            int W = d.GetLength(0);
            var paths = new List<ToolPath>();
            bool leftToRight = true;
            int lastRow = -1;

            for (double gy = 1.0; gy <= h; gy += stepPx)
            {
                int row = (int)Math.Round(gy);
                if (row == lastRow) continue;
                lastRow = row;

                var segments = new List<(double X0, double X1)>();
                double start = 0;

                for (int x = 1; x < W; x++)
                {
                    double v0 = d[x - 1, row], v1 = d[x, row];
                    bool prevIn = v0 >= level, curIn = v1 >= level;
                    if (!prevIn && curIn) start = (x - 1) + (level - v0) / (v1 - v0);
                    else if (prevIn && !curIn) segments.Add((start, (x - 1) + (level - v0) / (v1 - v0)));
                }

                if (segments.Count == 0) continue;

                if (!leftToRight)
                {
                    segments.Reverse();
                    for (int i = 0; i < segments.Count; i++)
                        segments[i] = (segments[i].X1, segments[i].X0);
                }

                foreach (var (x0, x1) in segments)
                {
                    var pts = new List<PointD> { ToMm(x0, row, h), ToMm(x1, row, h) };
                    paths.Add(new ToolPath(pts, false, PathKind.Fill));
                }

                leftToRight = !leftToRight;
            }

            return paths;
        }

        // ------------------------------------------------------------------
        // Umrechnung & Vereinfachung
        // ------------------------------------------------------------------

        private List<ToolPath> ToPaths(List<List<(double X, double Y)>> contours, int h, PathKind kind)
        {
            double tol = double.IsNaN(SimplifyTolerance) ? PixelSize * 0.25 : SimplifyTolerance;
            var result = new List<ToolPath>(contours.Count);

            foreach (var contour in contours)
            {
                var pts = new List<PointD>(contour.Count);
                foreach (var (x, y) in contour) pts.Add(ToMm(x, y, h));
                result.Add(new ToolPath(Simplify(pts, tol, closed: true), true, kind));
            }

            return result;
        }

        /// <summary>Gitterkoordinaten (mit 1-Pixel-Rand) in mm umrechnen.</summary>
        private PointD ToMm(double gx, double gy, int h)
        {
            double x = (gx - 0.5) * PixelSize;
            double y = FlipY ? (h - (gy - 0.5)) * PixelSize : (gy - 0.5) * PixelSize;
            return new PointD(x, y);
        }

        private static List<PointD> Simplify(List<PointD> pts, double tol, bool closed)
        {
            if (tol <= 0 || pts.Count < 4) return pts;

            var work = new List<PointD>(pts);
            if (closed) work.Add(pts[0]);

            var keep = new bool[work.Count];
            keep[0] = keep[keep.Length - 1] = true;

            var stack = new Stack<(int S, int E)>();
            stack.Push((0, work.Count - 1));

            while (stack.Count > 0)
            {
                var (s, e) = stack.Pop();
                double maxDist = 0;
                int index = -1;

                for (int i = s + 1; i < e; i++)
                {
                    double dist = SegmentDistance(work[i], work[s], work[e]);
                    if (dist > maxDist) { maxDist = dist; index = i; }
                }

                if (index >= 0 && maxDist > tol)
                {
                    keep[index] = true;
                    stack.Push((s, index));
                    stack.Push((index, e));
                }
            }

            var result = new List<PointD>();
            for (int i = 0; i < work.Count; i++)
                if (keep[i]) result.Add(work[i]);
            if (closed) result.RemoveAt(result.Count - 1);

            return result.Count >= 2 ? result : pts;
        }

        private static double SegmentDistance(PointD p, PointD a, PointD b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 > 0 ? ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2 : 0;
            t = Math.Max(0, Math.Min(1, t));
            double px = a.X + t * dx - p.X, py = a.Y + t * dy - p.Y;
            return Math.Sqrt(px * px + py * py);
        }
    }
}
