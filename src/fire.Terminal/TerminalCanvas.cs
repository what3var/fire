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
                var masks = GlyphMasks.Table;

                if (cw == 8)
                {
                    // 8 Pixel = 2 Vektoren zu je 4 Pixeln: ein Tabellenzugriff liefert die Masken für eine ganze Zeile (siehe
                    // GlyphMasks), ConditionalSelect wählt je Pixel Vorder- oder Hintergrund - ohne Schleife über die Pixel.
                    var fgv = Vector128.Create(fg);
                    if (background is PixelColor bgColor)
                    {
                        var bgv = Vector128.Create(bgColor.Packed);
                        for (int gy = 0; gy < ch; gy++)
                        {
                            ref uint dst = ref pixels[(y + gy) * stride + x];
                            int m = rows[gy] * 2;
                            Vector128.ConditionalSelect(masks[m], fgv, bgv).StoreUnsafe(ref dst);
                            Vector128.ConditionalSelect(masks[m + 1], fgv, bgv).StoreUnsafe(ref dst, 4);
                        }
                    }
                    else
                    {
                        for (int gy = 0; gy < ch; gy++)
                        {
                            int bits = rows[gy];
                            if (bits == 0) continue; // leere Zeile: nichts zu schreiben
                            ref uint dst = ref pixels[(y + gy) * stride + x];
                            int m = bits * 2;
                            Vector128.ConditionalSelect(masks[m], fgv, Vector128.LoadUnsafe(ref dst)).StoreUnsafe(ref dst);
                            Vector128.ConditionalSelect(masks[m + 1], fgv, Vector128.LoadUnsafe(ref dst, 4)).StoreUnsafe(ref dst, 4);
                        }
                    }
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
        public void DrawText(int x, int y, string text, PixelColor foreground, PixelColor? background = null)
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

    /// <summary>Für jede Bitmap-Zeile (0-255) die Pixelmasken als zwei Vektoren zu je vier Pixeln (Bit 7 = erstes Pixel): ein gesetztes
    /// Bit ist 0xFFFFFFFF, ein leeres 0. Einmal je Prozess berechnet (8 KB), danach genügt ein Tabellenzugriff je Zeile.</summary>
    internal static class GlyphMasks
    {
        internal static readonly Vector128<uint>[] Table = Build();

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
