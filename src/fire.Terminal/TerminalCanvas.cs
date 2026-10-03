using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace fire.Terminal
{
    /// <summary>
    /// Das "Grafikobjekt" (siehe CONSOLE.md): kennt die aktuelle Cursor-
    /// Position, Vorder-/Hintergrundfarbe, und führt darauf sowohl
    /// QBasic-artige Terminal-Emulation (Print/Color/Locate) als auch rohe
    /// Pixel-Grafikoperationen (SetPixel/Line/Rect/...) aus - IMMER auf dem
    /// aktuell gewählten `Target`-Framebuffer. Spaltenzahl/Zeilenzahl des
    /// Zeichenrasters ergeben sich dynamisch aus Target.Width/Height geteilt
    /// durch die Zellgröße der Schrift (CellWidth/CellHeight), nicht aus
    /// einem festen Wert - `Target` neu zu setzen (z.B. auf einen zweiten,
    /// unsichtbaren Puffer) ändert das Raster deshalb automatisch mit.
    ///
    /// Cursor-Position und Farben gehören bewusst DIESEM Objekt, nicht dem
    /// jeweiligen Framebuffer - ein Wechsel von `Target` behält Cursor/
    /// Farben bei (nur die Cursor-Position wird ggf. auf die neue,
    /// womöglich kleinere Rastergröße eingeklammert), was das Muster
    /// "kurz in einen zweiten Puffer zeichnen, dann zurückwechseln"
    /// unterstützt, ohne Cursor/Farbe jedes Mal neu setzen zu müssen.
    /// </summary>
    public sealed class TerminalCanvas
    {
        private Framebuffer _target;

        public Framebuffer Target
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

        /// <summary>Die 256-Farben-Palette des aktuellen Ziel-Framebuffers (siehe Framebuffer.Palette): Palette-Indizes als Farbe (`byte`-Überladungen,
        /// <see cref="Paint.FromIndex"/>) schlagen hier nach. Sie gehört dem Framebuffer, nicht der Canvas - mehrere Konsolen auf demselben
        /// Framebuffer teilen sie, und im Palette-Modus IST sie die Farbtabelle des Bildes.</summary>
        public Palette Palette => Target.Palette;

        // Zellgröße und Raster werden einmal berechnet (Framebuffer haben eine feste Größe, die Schrift ändert sich nicht) -
        // Print/Advance fragen sie für jedes Zeichen ab.
        private readonly int _cellWidth;
        private readonly int _cellHeight;
        private int _columns;
        private int _rows;

        public int CellWidth => _cellWidth;
        public int CellHeight => _cellHeight;
        public int Columns => _columns;
        public int Rows => _rows;

        public int CursorRow { get; private set; }
        public int CursorColumn { get; private set; }

        // Vorder-/Hintergrund als Farbangabe (Paint): ein Palette-Index bleibt ein Index (eine später geänderte Palette färbt neu gezeichneten Text um),
        // ein direkter Wert bleibt ein direkter Wert. Aufgelöst wird erst beim Zeichnen, für den dann aktuellen Ziel-Framebuffer.
        private Paint _foreground = Paint.FromRgba(PixelColor.White);
        private Paint? _background = Paint.FromRgba(PixelColor.Black);

        public PixelColor Foreground
        {
            get => ToColor(_foreground);
            set => _foreground = Paint.FromRgba(value);
        }

        /// <summary>null = TRANSPARENT: eine geschriebene Zelle überschreibt
        /// dann NUR die Glyph-Pixel selbst (Vordergrund), der vorhandene
        /// Zellinhalt bleibt sonst unverändert stehen - kein Framebuffer-
        /// Alpha-Blending nötig dafür (siehe Framebuffer-Doku), das ist
        /// reine "schreibe diese Pixel nicht"-Logik in PutChar. Ein
        /// gesetzter Wert übermalt dagegen immer die GESAMTE Zelle (siehe
        /// SPEC/CONSOLE.md "es werden immer ganze Zellen übermalt").</summary>
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

        public TerminalCanvas(Framebuffer target, IGlyphFont font)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            Font = font ?? throw new ArgumentNullException(nameof(font));
            _cellWidth = font.GlyphWidth;
            _cellHeight = font.GlyphHeight;
            UpdateGrid();
        }

        private void UpdateGrid()
        {
            _columns = _target.Width / _cellWidth;
            _rows = _target.Height / _cellHeight;
        }

        public void Locate(int row, int column)
        {
            CursorRow = Math.Clamp(row, 0, Math.Max(0, Rows - 1));
            CursorColumn = Math.Clamp(column, 0, Math.Max(0, Columns - 1));
        }

        /// <summary>Der Hintergrund als aufgelöste Farbe; ein transparenter Hintergrund zählt als Schwarz ("Bildschirm löschen" ohne jede Farbe
        /// ergäbe keinen Sinn).</summary>
        private Brush ClearBrush() => Target.ResolveBrush(_background ?? Paint.FromRgba(PixelColor.Black));

        /// <summary>Löscht den GESAMTEN aktuellen Target-Framebuffer mit der
        /// aktuellen Hintergrundfarbe (Transparent-Hintergrund zählt dabei
        /// als Schwarz, da "Bildschirm löschen" ohne jede Farbe keinen Sinn
        /// ergäbe) und setzt den Cursor auf (0, 0).</summary>
        public void Clear()
        {
            Target.Clear(ClearBrush());
            CursorRow = 0;
            CursorColumn = 0;
        }

        /// <summary>Schreibt `text` ab der aktuellen Cursor-Position,
        /// zellenweise (siehe Background-Doku). '\r' wird übersprungen
        /// (Teil von "\r\n"), '\n' springt an den Anfang der nächsten Zeile.
        /// Erreicht der Cursor das Zeilenende, springt er automatisch in die
        /// nächste Zeile; erreicht er das Ende des Bildschirms, scrollt der
        /// GESAMTE Target-Inhalt eine Zellenhöhe nach oben (Framebuffer.
        /// ScrollUp) - nach oben herausfallende Zeilen sind UNWIDERRUFLICH
        /// verloren, es gibt keinen Scrollback-Puffer (wie gefordert).</summary>
        public void Print(string text)
        {
            var target = Target;
            var fg = target.ResolveBrush(_foreground);
            bool hasBg = _background is Paint;
            var bg = hasBg ? target.ResolveBrush(_background!.Value) : default;
            foreach (char c in text)
            {
                if (c == '\n') { NewLine(); continue; }
                if (c == '\r') continue;
                DrawGlyphResolved(CursorColumn * CellWidth, CursorRow * CellHeight, c, fg, hasBg, bg);
                Advance();
            }
        }

        /// <summary>Zeichnet Zeichen `c` mit der linken oberen Ecke bei (x, y) in PIXELN. `background` null = transparent
        /// (nur die Glyph-Pixel werden geschrieben), sonst wird die ganze Zelle übermalt. Liegt die Zelle vollständig im Target
        /// und hat die Schrift Bitmap-Zeilen (<see cref="IBitmapGlyphFont"/>), gehen die Pixel zeilenweise ohne Abfrage und ohne
        /// Randprüfung direkt in den Puffer; sonst (Rand des Puffers, andere Schrift) pixelweise mit Clipping.</summary>
        public void DrawGlyph(int x, int y, char c, PixelColor foreground, PixelColor? background)
        {
            if (!Target.IsIndexed)
            {
                DrawGlyphResolved(x, y, c, new Brush(foreground.Packed, 0), background.HasValue, new Brush(background.GetValueOrDefault().Packed, 0));
                return;
            }
            DrawGlyph(x, y, c, (Paint)foreground, background is PixelColor b ? (Paint?)b : null);
        }

        public void DrawGlyph(int x, int y, char c, Paint foreground, Paint? background)
        {
            var target = Target;
            var fg = target.ResolveBrush(foreground);
            bool hasBg = background is Paint;
            DrawGlyphResolved(x, y, c, fg, hasBg, hasBg ? target.ResolveBrush(background!.Value) : default);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // heiß und schleifenreich: gleich voll optimiert übersetzen, nicht erst nach dem Hochstufen
        private void DrawGlyphResolved(int x, int y, char c, in Brush foreground, bool hasBackground, in Brush background)
        {
            int cw = CellWidth, ch = CellHeight;
            var target = Target;

            if (target.IsIndexed)
            {
                DrawGlyphIndexed(x, y, c, foreground, hasBackground, background);
                return;
            }

            if (Font is IBitmapGlyphFont bitmapFont && cw <= 8
                && x >= 0 && y >= 0 && x + cw <= target.Width && y + ch <= target.Height)
            {
                var rows = bitmapFont.GetGlyphRows(c);
                var pixels = target.Pixels;
                int stride = target.Width;
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

            // Allgemeiner Weg: jedes Pixel einzeln erfragen, Plot beschneidet am Rand.
            if (hasBackground)
                target.FillRect(x, y, cw, ch, background);
            for (int gy = 0; gy < ch; gy++)
                for (int gx = 0; gx < cw; gx++)
                    if (Font.IsPixelSet(c, gx, gy))
                        target.Plot(x + gx, y + gy, foreground);
        }

        /// <summary>Ein Zeichen in einen Palette-Framebuffer: die gesetzten Pixel (und mit Hintergrund die ganze Zelle) als Indizes. Ohne die
        /// Vektor-Wege des RGBA-Puffers - ein Index je Pixel, mit Beschneidung am Rand.</summary>
        private void DrawGlyphIndexed(int x, int y, char c, in Brush foreground, bool hasBackground, in Brush background)
        {
            int cw = CellWidth, ch = CellHeight;
            var target = Target;
            if (hasBackground) target.FillRect(x, y, cw, ch, background);

            if (Font is IBitmapGlyphFont bitmapFont && cw <= 8)
            {
                var rows = bitmapFont.GetGlyphRows(c);
                if (rows.Length >= ch)
                {
                    for (int gy = 0; gy < ch; gy++)
                    {
                        uint bits = rows[gy];
                        if (bits == 0) continue;
                        for (int gx = 0; gx < cw; gx++)
                            if (((bits >> (7 - gx)) & 1u) != 0) target.Plot(x + gx, y + gy, foreground);
                    }
                    return;
                }
            }

            for (int gy = 0; gy < ch; gy++)
                for (int gx = 0; gx < cw; gx++)
                    if (Font.IsPixelSet(c, gx, gy))
                        target.Plot(x + gx, y + gy, foreground);
        }

        /// <summary>Zeichnet `text` ab (x, y) in PIXELN, ein Zeichen nach dem anderen (kein Umbruch, kein Cursor; '\n' und
        /// '\r' werden wie jedes Zeichen der Schrift gezeichnet). Die Basis für Oberflächen, die Text an beliebigen Pixeln
        /// brauchen statt im Zellenraster.</summary>
        public void DrawText(int x, int y, string text, PixelColor foreground, PixelColor? background = null)
        {
            if (!Target.IsIndexed) // der häufigste Fall (RGBA, direkte Farben): ohne Umweg über die Farbauflösung
            {
                DrawTextResolved(x, y, text, new Brush(foreground.Packed, 0), background.HasValue, new Brush(background.GetValueOrDefault().Packed, 0));
                return;
            }
            DrawText(x, y, text, (Paint)foreground, background is PixelColor b ? (Paint?)b : null);
        }

        public void DrawText(int x, int y, string text, Paint foreground, Paint? background)
        {
            var target = Target;
            var fg = target.ResolveBrush(foreground);
            bool hasBg = background is Paint;
            DrawTextResolved(x, y, text, fg, hasBg, hasBg ? target.ResolveBrush(background!.Value) : default);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // schleifenreich und heiß: gleich voll optimiert übersetzen, nicht erst nach dem Hochstufen
        private void DrawTextResolved(int x, int y, string text, in Brush foreground, bool hasBackground, in Brush background)
        {
            int cw = CellWidth, ch = CellHeight;
            var target = Target;

            // Schnellpfad: liegt der ganze Text im Puffer und hat die Schrift 8 Pixel breite Bitmap-Zeilen, werden Schrift und Randprüfung
            // einmal für den ganzen Text erledigt, nicht je Zeichen.
            if (!target.IsIndexed && Font is IBitmapGlyphFont bitmapFont && cw == 8 && text.Length > 0
                && x >= 0 && y >= 0 && y + ch <= target.Height && (long)x + (long)cw * text.Length <= target.Width)
            {
                int stride = target.Width;
                uint fg = foreground.Rgba, bg = background.Rgba;
                ref uint cell = ref target.Pixels[y * stride + x];
                foreach (char c in text)
                {
                    var rows = bitmapFont.GetGlyphRows(c);
                    if (rows.Length < ch)
                    {
                        // Ungewöhnliche Schrift (zu wenige Zeilen): zeichenweise auf dem allgemeinen Weg
                        DrawTextSlow(x, y, text, foreground, hasBackground, background);
                        return;
                    }
                    if (hasBackground) GlyphBlitter.BlitOpaque(ref cell, stride, ch, rows, fg, bg);
                    else GlyphBlitter.BlitTransparent(ref cell, stride, ch, rows, fg);
                    cell = ref Unsafe.Add(ref cell, 8);
                }
                return;
            }

            DrawTextSlow(x, y, text, foreground, hasBackground, background);
        }

        private void DrawTextSlow(int x, int y, string text, in Brush foreground, bool hasBackground, in Brush background)
        {
            int cw = CellWidth;
            foreach (char c in text)
            {
                DrawGlyphResolved(x, y, c, foreground, hasBackground, background);
                x += cw;
            }
        }

        /// <summary>Breite von `text` in Pixeln (die Schrift ist dicktengleich: Zeichenzahl mal Zellbreite).</summary>
        public int MeasureText(string text) => text.Length * CellWidth;

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
                Target.ScrollUp(CellHeight, ClearBrush());
                CursorRow = Rows - 1;
            }
        }

        // -----------------------------------------------------------
        // Rohe Grafikoperationen - dasselbe Target wie Print/Color/Locate,
        // arbeiten aber in PIXEL- statt Zellen-Koordinaten. Jede Farbe ist
        // eine Paint-Angabe (Palette-Index ODER direkter Wert) und wird für das
        // Target aufgelöst; die PixelColor-/byte-Überladungen sind Kurzformen.
        // -----------------------------------------------------------

        public void SetPixel(int x, int y, Paint color) => Target.Plot(x, y, Target.ResolveBrush(color));
        public void SetPixel(int x, int y, PixelColor color) => SetPixel(x, y, (Paint)color);
        public void SetPixel(int x, int y, byte paletteIndex) => SetPixel(x, y, Paint.FromIndex(paletteIndex));

        public PixelColor GetPixel(int x, int y) => Target.GetPixel(x, y);

        /// <summary>Der Palette-Index des Pixels (im RGBA-Framebuffer der Eintrag, der der Farbe am nächsten kommt).</summary>
        public byte GetPixelIndex(int x, int y) => Target.GetIndex(x, y);

        /// <summary>Bresenham-Linienalgorithmus - keine externe Abhängigkeit,
        /// funktioniert identisch unabhängig vom Rendering-Backend.</summary>
        public void DrawLine(int x0, int y0, int x1, int y1, Paint color) => Shapes.Line(Target, x0, y0, x1, y1, Target.ResolveBrush(color));
        public void DrawLine(int x0, int y0, int x1, int y1, PixelColor color) => DrawLine(x0, y0, x1, y1, (Paint)color);
        public void DrawLine(int x0, int y0, int x1, int y1, byte paletteIndex) => DrawLine(x0, y0, x1, y1, Paint.FromIndex(paletteIndex));

        public void DrawRect(int x, int y, int w, int h, Paint color) => Shapes.Rect(Target, x, y, w, h, Target.ResolveBrush(color));
        public void DrawRect(int x, int y, int w, int h, PixelColor color) => DrawRect(x, y, w, h, (Paint)color);
        public void DrawRect(int x, int y, int w, int h, byte paletteIndex) => DrawRect(x, y, w, h, Paint.FromIndex(paletteIndex));

        public void FillRect(int x, int y, int w, int h, Paint color) => Target.FillRect(x, y, w, h, Target.ResolveBrush(color));
        public void FillRect(int x, int y, int w, int h, PixelColor color) => FillRect(x, y, w, h, (Paint)color);
        public void FillRect(int x, int y, int w, int h, byte paletteIndex) => FillRect(x, y, w, h, Paint.FromIndex(paletteIndex));

        // ---- Kreis, Ellipse, Dreieck, Polygon, Füllung, Kopieren (siehe Shapes/Blitter) ----

        public void DrawCircle(int cx, int cy, int r, Paint color) => Shapes.Circle(Target, cx, cy, r, Target.ResolveBrush(color));
        public void FillCircle(int cx, int cy, int r, Paint color) => Shapes.FillCircle(Target, cx, cy, r, Target.ResolveBrush(color));
        public void DrawEllipse(int cx, int cy, int rx, int ry, Paint color) => Shapes.Ellipse(Target, cx, cy, rx, ry, Target.ResolveBrush(color));
        public void FillEllipse(int cx, int cy, int rx, int ry, Paint color) => Shapes.FillEllipse(Target, cx, cy, rx, ry, Target.ResolveBrush(color));

        public void DrawTriangle(int x0, int y0, int x1, int y1, int x2, int y2, Paint color) =>
            Shapes.Triangle(Target, x0, y0, x1, y1, x2, y2, Target.ResolveBrush(color));
        public void FillTriangle(int x0, int y0, int x1, int y1, int x2, int y2, Paint color) =>
            Shapes.FillTriangle(Target, x0, y0, x1, y1, x2, y2, Target.ResolveBrush(color));

        /// <summary>`points` = x0, y0, x1, y1, ... (ein ungerades letztes Element zählt nicht).</summary>
        public void DrawPolygon(int[] points, Paint color, bool closed = true) => Shapes.Polygon(Target, points, Target.ResolveBrush(color), closed);
        public void FillPolygon(int[] points, Paint color) => Shapes.FillPolygon(Target, points, Target.ResolveBrush(color));

        public void FloodFill(int x, int y, Paint color) => Shapes.FloodFill(Target, x, y, Target.ResolveBrush(color));
        public void FloodFill(int x, int y, Paint color, Paint border) =>
            Shapes.FloodFillBorder(Target, x, y, Target.ResolveBrush(color), Target.ResolveBrush(border));

        public void Blit(Framebuffer source, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh, BlitMode mode = BlitMode.Copy, int colorKey = -1) =>
            Blitter.Blit(Target, source, sx, sy, sw, sh, dx, dy, dw, dh, mode, colorKey);
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
