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

        /// <summary>256-Farben-Palette dieser Canvas (siehe Palette-Doku) -
        /// jede `byte`-Index-Überladung von Color/SetPixel/DrawLine/
        /// DrawRect/FillRect schlägt hier nach, bevor sie zeichnet. Eine
        /// eigene Instanz pro TerminalCanvas (nicht geteilt) - passend zum
        /// bereits etablierten Muster, dass Cursor/Farben dem Grafikobjekt
        /// selbst gehören, nicht dem jeweiligen Framebuffer.</summary>
        public Palette Palette { get; } = new();

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

        public PixelColor Foreground { get; set; } = PixelColor.White;

        /// <summary>null = TRANSPARENT: eine geschriebene Zelle überschreibt
        /// dann NUR die Glyph-Pixel selbst (Vordergrund), der vorhandene
        /// Zellinhalt bleibt sonst unverändert stehen - kein Framebuffer-
        /// Alpha-Blending nötig dafür (siehe Framebuffer-Doku), das ist
        /// reine "schreibe diese Pixel nicht"-Logik in PutChar. Ein
        /// gesetzter Wert übermalt dagegen immer die GESAMTE Zelle (siehe
        /// SPEC/CONSOLE.md "es werden immer ganze Zellen übermalt").</summary>
        public PixelColor? Background { get; set; } = PixelColor.Black;

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

        /// <summary>Löscht den GESAMTEN aktuellen Target-Framebuffer mit der
        /// aktuellen Hintergrundfarbe (Transparent-Hintergrund zählt dabei
        /// als Schwarz, da "Bildschirm löschen" ohne jede Farbe keinen Sinn
        /// ergäbe) und setzt den Cursor auf (0, 0).</summary>
        public void Clear()
        {
            Target.Clear(Background ?? PixelColor.Black);
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
            foreach (char c in text)
            {
                if (c == '\n') { NewLine(); continue; }
                if (c == '\r') continue;
                PutChar(c);
                Advance();
            }
        }

        private void PutChar(char c)
        {
            int px = CursorColumn * CellWidth;
            int py = CursorRow * CellHeight;
            DrawGlyph(px, py, c, Foreground, Background);
        }

        /// <summary>Zeichnet Zeichen `c` mit der linken oberen Ecke bei (x, y) in PIXELN. `background` null = transparent
        /// (nur die Glyph-Pixel werden geschrieben), sonst wird die ganze Zelle übermalt. Liegt die Zelle vollständig im Target
        /// und hat die Schrift Bitmap-Zeilen (<see cref="IBitmapGlyphFont"/>), gehen die Pixel zeilenweise ohne Abfrage und ohne
        /// Randprüfung direkt in den Puffer; sonst (Rand des Puffers, andere Schrift) pixelweise mit Clipping.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // heiß und schleifenreich: gleich voll optimiert übersetzen, nicht erst nach dem Hochstufen
        public void DrawGlyph(int x, int y, char c, PixelColor foreground, PixelColor? background)
        {
            int cw = CellWidth, ch = CellHeight;
            var target = Target;

            if (Font is IBitmapGlyphFont bitmapFont && cw <= 8
                && x >= 0 && y >= 0 && x + cw <= target.Width && y + ch <= target.Height)
            {
                var rows = bitmapFont.GetGlyphRows(c);
                var pixels = target.Pixels;
                int stride = target.Width;
                uint fg = foreground.Packed;

                if (cw == 8 && rows.Length >= ch)
                {
                    ref uint origin = ref pixels[y * stride + x];
                    if (background is PixelColor bgColor) GlyphBlitter.BlitOpaque(ref origin, stride, ch, rows, fg, bgColor.Packed);
                    else GlyphBlitter.BlitTransparent(ref origin, stride, ch, rows, fg);
                    return;
                }

                // Schmalere Schrift: pro Zeile eine kurze Schleife über die Bits.
                if (background is PixelColor bgNarrow)
                {
                    uint bg = bgNarrow.Packed, diff = bg ^ fg;
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

            // Allgemeiner Weg: jedes Pixel einzeln erfragen, SetPixel beschneidet am Rand.
            if (background is PixelColor bgc)
                target.FillRect(x, y, cw, ch, bgc);
            for (int gy = 0; gy < ch; gy++)
                for (int gx = 0; gx < cw; gx++)
                    if (Font.IsPixelSet(c, gx, gy))
                        target.SetPixel(x + gx, y + gy, foreground);
        }

        /// <summary>Zeichnet `text` ab (x, y) in PIXELN, ein Zeichen nach dem anderen (kein Umbruch, kein Cursor; '\n' und
        /// '\r' werden wie jedes Zeichen der Schrift gezeichnet). Die Basis für Oberflächen, die Text an beliebigen Pixeln
        /// brauchen statt im Zellenraster.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // schleifenreich und heiß: gleich voll optimiert übersetzen, nicht erst nach dem Hochstufen
        public void DrawText(int x, int y, string text, PixelColor foreground, PixelColor? background = null)
        {
            int cw = CellWidth, ch = CellHeight;
            var target = Target;

            // Schnellpfad: liegt der ganze Text im Puffer und hat die Schrift 8 Pixel breite Bitmap-Zeilen, werden Schrift und Randprüfung
            // einmal für den ganzen Text erledigt, nicht je Zeichen.
            if (Font is IBitmapGlyphFont bitmapFont && cw == 8 && text.Length > 0
                && x >= 0 && y >= 0 && y + ch <= target.Height && (long)x + (long)cw * text.Length <= target.Width)
            {
                int stride = target.Width;
                uint fg = foreground.Packed;
                ref uint cell = ref target.Pixels[y * stride + x];
                foreach (char c in text)
                {
                    var rows = bitmapFont.GetGlyphRows(c);
                    if (rows.Length < ch)
                    {
                        // Ungewöhnliche Schrift (zu wenige Zeilen): zeichenweise auf dem allgemeinen Weg
                        DrawTextSlow(x, y, text, foreground, background);
                        return;
                    }
                    if (background is PixelColor bgColor) GlyphBlitter.BlitOpaque(ref cell, stride, ch, rows, fg, bgColor.Packed);
                    else GlyphBlitter.BlitTransparent(ref cell, stride, ch, rows, fg);
                    cell = ref Unsafe.Add(ref cell, 8);
                }
                return;
            }

            DrawTextSlow(x, y, text, foreground, background);
        }

        private void DrawTextSlow(int x, int y, string text, PixelColor foreground, PixelColor? background)
        {
            int cw = CellWidth;
            foreach (char c in text)
            {
                DrawGlyph(x, y, c, foreground, background);
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
                Target.ScrollUp(CellHeight, Background ?? PixelColor.Black);
                CursorRow = Rows - 1;
            }
        }

        // -----------------------------------------------------------
        // Rohe Grafikoperationen - dasselbe Target wie Print/Color/Locate,
        // arbeiten aber in PIXEL- statt Zellen-Koordinaten.
        // -----------------------------------------------------------

        public void SetPixel(int x, int y, PixelColor color) => Target.SetPixel(x, y, color);

        public void SetPixel(int x, int y, byte paletteIndex) => SetPixel(x, y, Palette.GetColor(paletteIndex));

        public PixelColor GetPixel(int x, int y) => Target.GetPixel(x, y);

        /// <summary>Bresenham-Linienalgorithmus - keine externe Abhängigkeit,
        /// funktioniert identisch unabhängig vom Rendering-Backend.</summary>
        public void DrawLine(int x0, int y0, int x1, int y1, PixelColor color)
        {
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                Target.SetPixel(x0, y0, color);
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        public void DrawLine(int x0, int y0, int x1, int y1, byte paletteIndex) =>
            DrawLine(x0, y0, x1, y1, Palette.GetColor(paletteIndex));

        public void DrawRect(int x, int y, int w, int h, PixelColor color)
        {
            if (w <= 0 || h <= 0) return;
            DrawLine(x, y, x + w - 1, y, color);
            DrawLine(x, y + h - 1, x + w - 1, y + h - 1, color);
            DrawLine(x, y, x, y + h - 1, color);
            DrawLine(x + w - 1, y, x + w - 1, y + h - 1, color);
        }

        public void DrawRect(int x, int y, int w, int h, byte paletteIndex) =>
            DrawRect(x, y, w, h, Palette.GetColor(paletteIndex));

        public void FillRect(int x, int y, int w, int h, PixelColor color) => Target.FillRect(x, y, w, h, color);

        public void FillRect(int x, int y, int w, int h, byte paletteIndex) =>
            FillRect(x, y, w, h, Palette.GetColor(paletteIndex));
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
