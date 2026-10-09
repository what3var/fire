using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace fire.Terminal
{
    /// <summary>
    /// Der Renderer (früher die "Console"; siehe CONSOLE.md): zeichnet in ein <see cref="IRenderTarget"/> (meist ein <see cref="Framebuffer"/>) - der Software-Renderer. Er hat zwei
    /// Seiten: eine QBasic-artige Terminal-Emulation (Print/Locate/SetColor, Cursor und Farben gehören dem Renderer, nicht dem Ziel) und die Grafikfunktionen in
    /// PIXEL-Koordinaten, die statt Farben <see cref="Brush"/> (Füllungen) und <see cref="Pen"/> (Punkte, Linien, Pfade, Umrisse) nehmen. Die Pinsel und Stifte bieten die
    /// eigentlichen Funktionen an; der Renderer reicht ihnen nur die Fläche (<see cref="Surface"/>) des Ziels.
    ///
    /// <see cref="AlphaBlending"/> schaltet das Mischen halbdurchsichtiger Farben ein (Vorgabe) oder aus (dann wird jede Farbe samt Alpha kopiert). Gemischt wird nur in einem 32-Bit-Ziel;
    /// in einem 8-Bit-Ziel wird eine Farbe ab Alpha 128 kopiert und eine darunter nicht gezeichnet (siehe <see cref="Surface"/>).
    ///
    /// Zeilen- und Spaltenzahl des Zeichenrasters ergeben sich aus der Größe des Ziels geteilt durch die Zellgröße der Schrift. Ein anderes `Target` behält Cursor und Farben
    /// bei (die Position wird auf das neue Raster begrenzt).
    /// </summary>
    public sealed class Renderer
    {
        private IRenderTarget _target;

        public IRenderTarget Target
        {
            get => _target;
            set
            {
                _target = value ?? throw new ArgumentNullException(nameof(value));
                UpdateGrid();
                CursorRow = Math.Clamp(CursorRow, 0, Math.Max(0, Rows - 1));
                CursorColumn = Math.Clamp(CursorColumn, 0, Math.Max(0, Columns - 1));
            }
        }

        public IGlyphFont Font { get; }

        /// <summary>Alpha-Blending (siehe Klassen-Doku). Vorgabe: an.</summary>
        public bool AlphaBlending { get; set; } = true;

        /// <summary>Die Fläche des Ziels für einen Zeichenaufruf (mit dem Beschneidungsrechteck, siehe <see cref="SetClip"/>).</summary>
        public Surface Surface => new(_target, AlphaBlending, _clipLeft, _clipTop, _clipRight, _clipBottom);

        private int _clipLeft, _clipTop, _clipRight = int.MaxValue, _clipBottom = int.MaxValue;

        /// <summary>Beschränkt das Zeichnen (Formen, Text, Füllungen) auf das Rechteck (x, y, w, h); es wird mit dem Ziel geschnitten. Gilt auch für `Blit`, nicht für `Clear` und das Scrollen des Terminals. <see cref="ResetClip"/> hebt es auf.</summary>
        public void SetClip(int x, int y, int w, int h)
        {
            _clipLeft = x;
            _clipTop = y;
            _clipRight = (int)Math.Min((long)x + Math.Max(0, w), int.MaxValue);
            _clipBottom = (int)Math.Min((long)y + Math.Max(0, h), int.MaxValue);
        }

        /// <summary>Hebt das Beschneidungsrechteck auf: wieder das ganze Ziel.</summary>
        public void ResetClip()
        {
            _clipLeft = 0;
            _clipTop = 0;
            _clipRight = int.MaxValue;
            _clipBottom = int.MaxValue;
        }

        /// <summary>Das Beschneidungsrechteck als (x, y, Breite, Höhe) im Ziel.</summary>
        public (int X, int Y, int Width, int Height) GetClip()
        {
            var s = Surface;
            return (s.ClipLeft, s.ClipTop, Math.Max(0, s.ClipRight - s.ClipLeft), Math.Max(0, s.ClipBottom - s.ClipTop));
        }

        /// <summary>Die 256-Farben-Palette des Ziels (siehe Framebuffer.Palette).</summary>
        public Palette Palette => Target.Palette;

        private readonly int _cellWidth;
        private readonly int _cellHeight;
        // das Raster folgt der Größe des Ziels (ein Framebuffer darf seine Größe ändern, siehe Framebuffer.Resize); der Cursor wird vor dem Schreiben ins Raster zurückgeholt (Print)
        public int CellWidth => _cellWidth;
        public int CellHeight => _cellHeight;
        public int Columns => _target.Width / _cellWidth;
        public int Rows => _target.Height / _cellHeight;

        public int CursorRow { get; private set; }
        public int CursorColumn { get; private set; }

        // Vorder-/Hintergrund von Print als Farbangabe (Paint): ein Palette-Index bleibt ein Index (eine später geänderte Palette färbt neu gezeichneten Text um).
        private Paint _foreground = Paint.FromRgba(PixelColor.White);
        private Paint? _background = Paint.FromRgba(PixelColor.Black);

        public PixelColor Foreground
        {
            get => ToColor(_foreground);
            set => _foreground = Paint.FromRgba(value);
        }

        /// <summary>null = TRANSPARENT: eine geschriebene Zelle überschreibt dann NUR die Glyph-Pixel selbst (Vordergrund). Ein gesetzter Wert übermalt immer die GESAMTE Zelle.</summary>
        public PixelColor? Background
        {
            get => _background is Paint b ? ToColor(b) : null;
            set => _background = value is PixelColor c ? Paint.FromRgba(c) : null;
        }

        /// <summary>Wie <see cref="Foreground"/>/<see cref="Background"/>, aber als Farbangabe (Palette-Index ODER direkter Wert); `background` null = transparent.</summary>
        public void SetColor(Paint foreground, Paint? background)
        {
            _foreground = foreground;
            _background = background;
        }

        private PixelColor ToColor(Paint p) => p.IsIndex ? Palette.GetColor((byte)p.Index) : new PixelColor(p.Rgba);

        public Renderer(IRenderTarget target, IGlyphFont font)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            Font = font ?? throw new ArgumentNullException(nameof(font));
            _cellWidth = font.GlyphWidth;
            _cellHeight = font.GlyphHeight;
            UpdateGrid();
        }

        private void UpdateGrid() { }   // (das Raster wird aus der Größe des Ziels berechnet)

        public void Locate(int row, int column)
        {
            CursorRow = Math.Clamp(row, 0, Math.Max(0, Rows - 1));
            CursorColumn = Math.Clamp(column, 0, Math.Max(0, Columns - 1));
        }

        /// <summary>Der Hintergrund als aufgelöste Farbe; ein transparenter Hintergrund zählt als Schwarz ("Bildschirm löschen" ohne jede Farbe ergäbe keinen Sinn).</summary>
        private Pixel ClearPixel() => Surface.Resolve(_background ?? Paint.FromRgba(PixelColor.Black));

        /// <summary>Löscht das GESAMTE Ziel mit der Hintergrundfarbe (wie sie ist, ohne Mischen) und setzt den Cursor auf (0, 0).</summary>
        public void Clear()
        {
            ClearAll(ClearPixel());
            CursorRow = 0;
            CursorColumn = 0;
        }

        /// <summary>Setzt das ganze Ziel auf `paint` (ohne Mischen: so, wie die Farbe ist, auch mit Alpha).</summary>
        public void Clear(Paint paint) => ClearAll(Surface.Resolve(paint));

        private void ClearAll(in Pixel pixel)
        {
            var target = Target;
            if (target.Indices != null)
            {
                Array.Fill(target.Indices, pixel.Index);
                target.MarkDirty();
            }
            else Array.Fill(target.Pixels, pixel.Rgba);
        }

        /// <summary>Schreibt `text` ab der Cursor-Position, zellenweise. '\r' wird übersprungen, '\n' springt an den Anfang der nächsten Zeile. Am Zeilenende geht der Cursor in die nächste
        /// Zeile; am Ende des Bildschirms scrollt der GESAMTE Inhalt eine Zellenhöhe nach oben (was oben herausfällt, ist verloren - es gibt keinen Scrollback).</summary>
        public void Print(string text)
        {
            CursorRow = Math.Clamp(CursorRow, 0, Math.Max(0, Rows - 1));
            CursorColumn = Math.Clamp(CursorColumn, 0, Math.Max(0, Columns - 1));
            var surface = Surface;
            var fg = surface.Resolve(_foreground);
            bool hasBg = _background is Paint;
            var bg = hasBg ? surface.Resolve(_background!.Value) : default;
            foreach (char c in text)
            {
                if (c == '\n') { NewLine(); continue; }
                if (c == '\r') continue;
                DrawGlyphResolved(surface, CursorColumn * CellWidth, CursorRow * CellHeight, c, fg, hasBg, bg);
                Advance();
            }
        }

        // -----------------------------------------------------------
        // Text an Pixel-Koordinaten
        // -----------------------------------------------------------

        /// <summary>Zeichnet Zeichen `c` mit der linken oberen Ecke bei (x, y) in PIXELN: die Pixel der Glyphe mit dem Pinsel `foreground`, mit `background` (null = keiner) die ganze
        /// Zelle darunter.</summary>
        public void DrawGlyph(int x, int y, char c, Brush foreground, Brush? background = null) => DrawText(x, y, c.ToString(), foreground, background);

        /// <summary>Zeichnet `text` ab (x, y) in PIXELN, ein Zeichen nach dem anderen (kein Umbruch, kein Cursor; '\n' und '\r' werden wie jedes Zeichen der Schrift gezeichnet). Die
        /// Glyph-Pixel nehmen die Farbe von `foreground`, mit `background` (null = keiner) wird die ganze Zelle darunter gefüllt.</summary>
        public void DrawText(int x, int y, string text, Brush foreground, Brush? background = null)
        {
            var surface = Surface;
            if (foreground.IsUniform && (background == null || background.IsUniform))
            {
                var fg = foreground.PixelAt(surface, 0, 0);
                bool hasBg = background != null;
                DrawTextResolved(surface, x, y, text, fg, hasBg, hasBg ? background!.PixelAt(surface, 0, 0) : default);
                return;
            }
            // ein Pinsel mit Muster: jedes Pixel einzeln
            int cw = CellWidth, ch = CellHeight;
            foreach (char c in text)
            {
                background?.FillRect(surface, x, y, cw, ch);
                for (int gy = 0; gy < ch; gy++)
                    for (int gx = 0; gx < cw; gx++)
                        if (Font.IsPixelSet(c, gx, gy)) surface.Put(x + gx, y + gy, foreground.PixelAt(surface, x + gx, y + gy));
                x += cw;
            }
        }

        /// <summary>Breite von `text` in Pixeln (die Schrift ist dicktengleich: Zeichenzahl mal Zellbreite).</summary>
        public int MeasureText(string text) => text.Length * CellWidth;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // heiß und schleifenreich: gleich voll optimiert übersetzen, nicht erst nach dem Hochstufen
        private void DrawGlyphResolved(in Surface surface, int x, int y, char c, in Pixel foreground, bool hasBackground, in Pixel background)
        {
            int cw = CellWidth, ch = CellHeight;

            // ein nicht sichtbarer (ganz durchsichtiger) Hintergrund ist keiner; ein nicht sichtbarer Vordergrund zeichnet keine Glyph-Pixel
            if (hasBackground && !surface.Visible(background)) hasBackground = false;
            bool fgVisible = surface.Visible(foreground);
            if (!fgVisible && !hasBackground) return;

            bool direct = fgVisible && surface.IsCopy(foreground) && (!hasBackground || surface.IsCopy(background));

            if (direct && !surface.IsIndexed && Font is IBitmapGlyphFont bitmapFont && cw <= 8
                && x >= surface.ClipLeft && y >= surface.ClipTop && x + cw <= surface.ClipRight && y + ch <= surface.ClipBottom)
            {
                var rows = bitmapFont.GetGlyphRows(c);
                var pixels = surface.Pixels;
                int stride = surface.Width;
                uint fg = foreground.Rgba;

                if (cw == 8 && rows.Length >= ch)
                {
                    ref uint origin = ref pixels[y * stride + x];
                    if (hasBackground) GlyphBlitter.BlitOpaque(ref origin, stride, ch, rows, fg, background.Rgba);
                    else GlyphBlitter.BlitTransparent(ref origin, stride, ch, rows, fg);
                    return;
                }

                // Schmalere Schrift: pro Zeile eine kurze Schleife über die Bits.
                if (hasBackground)
                {
                    uint bg = background.Rgba, diff = bg ^ fg;
                    for (int gy = 0; gy < ch; gy++)
                    {
                        var dst = pixels.AsSpan((y + gy) * stride + x, cw);
                        uint bits = rows[gy];
                        for (int gx = 0; gx < dst.Length; gx++)
                            dst[gx] = bg ^ (diff & (0u - ((bits >> (7 - gx)) & 1u)));
                    }
                }
                else
                {
                    for (int gy = 0; gy < ch; gy++)
                    {
                        uint bits = rows[gy];
                        if (bits == 0) continue;
                        var dst = pixels.AsSpan((y + gy) * stride + x, cw);
                        for (int gx = 0; gx < dst.Length; gx++)
                            if (((bits >> (7 - gx)) & 1u) != 0) dst[gx] = fg;
                    }
                }
                return;
            }

            // Allgemeiner Weg (Palette-Ziel, Rand des Ziels, durchscheinende Farben, andere Schrift): Zelle füllen, dann die Pixel der Glyphe einzeln - Put beschneidet und mischt.
            if (hasBackground) surface.Rect(x, y, cw, ch, background);
            if (!fgVisible) return;
            if (Font is IBitmapGlyphFont bf && cw <= 8)
            {
                var rows = bf.GetGlyphRows(c);
                if (rows.Length >= ch)
                {
                    for (int gy = 0; gy < ch; gy++)
                    {
                        uint bits = rows[gy];
                        if (bits == 0) continue;
                        for (int gx = 0; gx < cw; gx++)
                            if (((bits >> (7 - gx)) & 1u) != 0) surface.Put(x + gx, y + gy, foreground);
                    }
                    return;
                }
            }
            for (int gy = 0; gy < ch; gy++)
                for (int gx = 0; gx < cw; gx++)
                    if (Font.IsPixelSet(c, gx, gy))
                        surface.Put(x + gx, y + gy, foreground);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // schleifenreich und heiß: gleich voll optimiert übersetzen, nicht erst nach dem Hochstufen
        private void DrawTextResolved(in Surface surface, int x, int y, string text, in Pixel foreground, bool hasBackground, in Pixel background)
        {
            int cw = CellWidth, ch = CellHeight;

            // Schnellpfad: liegt der ganze Text im Puffer und hat die Schrift 8 Pixel breite Bitmap-Zeilen (und die Farben werden einfach kopiert), werden Schrift und
            // Randprüfung einmal für den ganzen Text erledigt, nicht je Zeichen.
            if (!surface.IsIndexed && Font is IBitmapGlyphFont bitmapFont && cw == 8 && text.Length > 0
                && surface.Visible(foreground) && surface.IsCopy(foreground)
                && (!hasBackground || (surface.Visible(background) && surface.IsCopy(background)))
                && x >= surface.ClipLeft && y >= surface.ClipTop && y + ch <= surface.ClipBottom && (long)x + (long)cw * text.Length <= surface.ClipRight)
            {
                int stride = surface.Width;
                uint fg = foreground.Rgba, bg = background.Rgba;
                ref uint cell = ref surface.Pixels[y * stride + x];
                foreach (char c in text)
                {
                    var rows = bitmapFont.GetGlyphRows(c);
                    if (rows.Length < ch)
                    {
                        // Ungewöhnliche Schrift (zu wenige Zeilen): zeichenweise auf dem allgemeinen Weg
                        DrawTextSlow(surface, x, y, text, foreground, hasBackground, background);
                        return;
                    }
                    if (hasBackground) GlyphBlitter.BlitOpaque(ref cell, stride, ch, rows, fg, bg);
                    else GlyphBlitter.BlitTransparent(ref cell, stride, ch, rows, fg);
                    cell = ref Unsafe.Add(ref cell, 8);
                }
                return;
            }

            DrawTextSlow(surface, x, y, text, foreground, hasBackground, background);
        }

        private void DrawTextSlow(in Surface surface, int x, int y, string text, in Pixel foreground, bool hasBackground, in Pixel background)
        {
            int cw = CellWidth;
            foreach (char c in text)
            {
                DrawGlyphResolved(surface, x, y, c, foreground, hasBackground, background);
                x += cw;
            }
        }

        private void Advance()
        {
            CursorColumn++;
            if (CursorColumn >= Columns) NewLine();
        }

        private void NewLine()
        {
            CursorColumn = 0;
            CursorRow++;
            if (CursorRow >= Rows)
            {
                ScrollUp(CellHeight, ClearPixel());
                CursorRow = Rows - 1;
            }
        }

        /// <summary>Verschiebt den GESAMTEN Inhalt um `pixelRows` Zeilen nach OBEN; die untersten Zeilen werden mit `fill` aufgefüllt.</summary>
        private void ScrollUp(int pixelRows, in Pixel fill)
        {
            var target = Target;
            int w = target.Width, h = target.Height;
            if (pixelRows <= 0) return;
            if (pixelRows >= h) { ClearAll(fill); return; }
            if (target.Indices != null)
            {
                Array.Copy(target.Indices, pixelRows * w, target.Indices, 0, (h - pixelRows) * w);
                Array.Fill(target.Indices, fill.Index, (h - pixelRows) * w, pixelRows * w);
                target.MarkDirty();
                return;
            }
            Array.Copy(target.Pixels, pixelRows * w, target.Pixels, 0, (h - pixelRows) * w);
            Array.Fill(target.Pixels, fill.Rgba, (h - pixelRows) * w, pixelRows * w);
        }

        // -----------------------------------------------------------
        // Grafikfunktionen in PIXEL-Koordinaten: Füllungen nehmen einen Brush, Zeichnen einen Pen
        // -----------------------------------------------------------

        /// <summary>Ein Pixel mit der Farbe `color` (Farbangabe; gemischt, wenn Blending an und die Farbe halbdurchsichtig ist).</summary>
        public void SetPixel(int x, int y, Paint color) { var s = Surface; s.Put(x, y, s.Resolve(color)); }
        public void SetPixel(int x, int y, PixelColor color) => SetPixel(x, y, (Paint)color);

        public PixelColor GetPixel(int x, int y)
        {
            var t = Target;
            if ((uint)x >= (uint)t.Width || (uint)y >= (uint)t.Height) return PixelColor.Transparent;
            int i = y * t.Width + x;
            return t.Indices != null ? t.Palette.GetColor(t.Indices[i]) : new PixelColor(t.Pixels[i]);
        }

        /// <summary>Der Palette-Index des Pixels (im 32-Bit-Ziel der Eintrag, der der Farbe am nächsten kommt). 0 außerhalb.</summary>
        public byte GetPixelIndex(int x, int y)
        {
            var t = Target;
            if ((uint)x >= (uint)t.Width || (uint)y >= (uint)t.Height) return 0;
            int i = y * t.Width + x;
            return t.Indices != null ? t.Indices[i] : t.Palette.FindNearest(new PixelColor(t.Pixels[i]));
        }

        // ---- Füllungen (Brush) ----

        public void FillRect(int x, int y, int w, int h, Brush brush) => brush.FillRect(Surface, x, y, w, h);
        public void FillCircle(int cx, int cy, int r, Brush brush) => brush.FillCircle(Surface, cx, cy, r);
        public void FillEllipse(int cx, int cy, int rx, int ry, Brush brush) => brush.FillEllipse(Surface, cx, cy, rx, ry);
        public void FillTriangle(int x0, int y0, int x1, int y1, int x2, int y2, Brush brush) => brush.FillTriangle(Surface, x0, y0, x1, y1, x2, y2);

        /// <summary>`points` = x0, y0, x1, y1, ... (ein ungerades letztes Element zählt nicht).</summary>
        public void FillPolygon(int[] points, Brush brush) => brush.FillPolygon(Surface, points);

        /// <summary>Füllt das ganze Ziel mit `brush` (gemischt, wenn Blending an; zum Setzen ohne Mischen <see cref="Clear(Paint)"/>).</summary>
        public void Fill(Brush brush) { var t = Target; brush.FillRect(Surface, 0, 0, t.Width, t.Height); }

        public void FloodFill(int x, int y, Brush brush) => brush.FloodFill(Surface, x, y);
        public void FloodFill(int x, int y, Brush brush, Paint border) => brush.FloodFillBorder(Surface, x, y, border);

        // ---- Zeichnen (Pen) ----

        public void DrawPoint(int x, int y, Pen pen) => pen.DrawPoint(Surface, x, y);
        public void DrawLine(int x0, int y0, int x1, int y1, Pen pen) => pen.DrawLine(Surface, x0, y0, x1, y1);

        /// <summary>Ein Pfad durch die Punkte `points` (x0, y0, x1, y1, ...); `closed` verbindet den letzten mit dem ersten.</summary>
        public void DrawPath(int[] points, Pen pen, bool closed = false) => pen.DrawPath(Surface, points, closed);

        public void DrawRect(int x, int y, int w, int h, Pen pen) => pen.DrawRect(Surface, x, y, w, h);
        public void DrawCircle(int cx, int cy, int r, Pen pen) => pen.DrawCircle(Surface, cx, cy, r);
        public void DrawEllipse(int cx, int cy, int rx, int ry, Pen pen) => pen.DrawEllipse(Surface, cx, cy, rx, ry);
        public void DrawTriangle(int x0, int y0, int x1, int y1, int x2, int y2, Pen pen) => pen.DrawTriangle(Surface, x0, y0, x1, y1, x2, y2);
        public void DrawPolygon(int[] points, Pen pen, bool closed = true) => pen.DrawPolygon(Surface, points, closed);

        // ---- Kopieren ----

        public void Blit(IRenderTarget source, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh, BlitMode mode = BlitMode.Copy, int colorKey = -1) =>
            Blitter.Blit(Target, source, sx, sy, sw, sh, dx, dy, dw, dh, mode, colorKey, AlphaBlending, _clipLeft, _clipTop, _clipRight, _clipBottom);
    }

    /// <summary>Schreibt die Zeilen eines 8 Pixel breiten Zeichens in den Pixelpuffer - ohne Bereichsprüfung: der Aufrufer hat
    /// sichergestellt, dass die ganze Zelle (8 Pixel x `ch` Zeilen) im Puffer liegt. Je Zeile ein Tabellenzugriff für die Pixelmaske
    /// (siehe GlyphMasks) und eine Vektor-Auswahl Vorder-/Hintergrund: mit AVX2 8 Pixel auf einmal, sonst zweimal 4.</summary>
    internal static class GlyphBlitter
    {
        /// <summary>Nur die gesetzten Pixel werden geschrieben (Hintergrund bleibt).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void BlitTransparent(ref uint origin, int stride, int ch, ReadOnlySpan<byte> rows, uint fg)
        {
            ref byte rowBits = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(rows);
            if (Vector256.IsHardwareAccelerated)
            {
                var fgv = Vector256.Create(fg);
                ref Vector256<uint> masks = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(GlyphMasks.Table256);
                for (int gy = 0; gy < ch; gy++)
                {
                    int bits = Unsafe.Add(ref rowBits, gy);
                    if (bits == 0) continue; // leere Zeile: nichts zu schreiben
                    ref uint dst = ref Unsafe.Add(ref origin, gy * stride);
                    Vector256.ConditionalSelect(Unsafe.Add(ref masks, bits), fgv, Vector256.LoadUnsafe(ref dst)).StoreUnsafe(ref dst);
                }
            }
            else
            {
                var fgv = Vector128.Create(fg);
                ref Vector128<uint> masks = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(GlyphMasks.Table);
                for (int gy = 0; gy < ch; gy++)
                {
                    int bits = Unsafe.Add(ref rowBits, gy);
                    if (bits == 0) continue;
                    ref uint dst = ref Unsafe.Add(ref origin, gy * stride);
                    Vector128.ConditionalSelect(Unsafe.Add(ref masks, bits * 2), fgv, Vector128.LoadUnsafe(ref dst)).StoreUnsafe(ref dst);
                    Vector128.ConditionalSelect(Unsafe.Add(ref masks, bits * 2 + 1), fgv, Vector128.LoadUnsafe(ref dst, 4)).StoreUnsafe(ref dst, 4);
                }
            }
        }

        /// <summary>Die ganze Zelle wird geschrieben: gesetzte Pixel in `fg`, die anderen in `bg`.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void BlitOpaque(ref uint origin, int stride, int ch, ReadOnlySpan<byte> rows, uint fg, uint bg)
        {
            ref byte rowBits = ref System.Runtime.InteropServices.MemoryMarshal.GetReference(rows);
            if (Vector256.IsHardwareAccelerated)
            {
                var fgv = Vector256.Create(fg);
                var bgv = Vector256.Create(bg);
                ref Vector256<uint> masks = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(GlyphMasks.Table256);
                for (int gy = 0; gy < ch; gy++)
                {
                    ref uint dst = ref Unsafe.Add(ref origin, gy * stride);
                    Vector256.ConditionalSelect(Unsafe.Add(ref masks, (int)Unsafe.Add(ref rowBits, gy)), fgv, bgv).StoreUnsafe(ref dst);
                }
            }
            else
            {
                var fgv = Vector128.Create(fg);
                var bgv = Vector128.Create(bg);
                ref Vector128<uint> masks = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(GlyphMasks.Table);
                for (int gy = 0; gy < ch; gy++)
                {
                    ref uint dst = ref Unsafe.Add(ref origin, gy * stride);
                    int m = Unsafe.Add(ref rowBits, gy) * 2;
                    Vector128.ConditionalSelect(Unsafe.Add(ref masks, m), fgv, bgv).StoreUnsafe(ref dst);
                    Vector128.ConditionalSelect(Unsafe.Add(ref masks, m + 1), fgv, bgv).StoreUnsafe(ref dst, 4);
                }
            }
        }
    }

    /// <summary>Für jede Bitmap-Zeile (0-255) die Pixelmasken als zwei Vektoren zu je vier Pixeln (Bit 7 = erstes Pixel): ein gesetztes
    /// Bit ist 0xFFFFFFFF, ein leeres 0. Einmal je Prozess berechnet (8 KB), danach genügt ein Tabellenzugriff je Zeile.</summary>
    internal static class GlyphMasks
    {
        internal static readonly Vector128<uint>[] Table = Build();

        /// <summary>Dieselben Masken als EIN Vektor zu acht Pixeln je Bitmap-Zeile (für AVX2).</summary>
        internal static readonly Vector256<uint>[] Table256 = Build256();

        private static Vector256<uint>[] Build256()
        {
            var table = new Vector256<uint>[256];
            for (int bits = 0; bits < 256; bits++)
            {
                Span<uint> lanes = stackalloc uint[8];
                for (int px = 0; px < 8; px++)
                    lanes[px] = (bits & (0x80 >> px)) != 0 ? 0xFFFFFFFFu : 0u;
                table[bits] = Vector256.Create(lanes[0], lanes[1], lanes[2], lanes[3], lanes[4], lanes[5], lanes[6], lanes[7]);
            }
            return table;
        }

        private static Vector128<uint>[] Build()
        {
            var table = new Vector128<uint>[256 * 2];
            for (int bits = 0; bits < 256; bits++)
            {
                Span<uint> lanes = stackalloc uint[8];
                for (int px = 0; px < 8; px++)
                    lanes[px] = (bits & (0x80 >> px)) != 0 ? 0xFFFFFFFFu : 0u;
                table[bits * 2] = Vector128.Create(lanes[0], lanes[1], lanes[2], lanes[3]);
                table[bits * 2 + 1] = Vector128.Create(lanes[4], lanes[5], lanes[6], lanes[7]);
            }
            return table;
        }
    }
}
