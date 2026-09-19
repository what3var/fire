using System;

namespace ScriptLang.Terminal
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

        public int CellWidth => Font.GlyphWidth;
        public int CellHeight => Font.GlyphHeight;
        public int Columns => Target.Width / CellWidth;
        public int Rows => Target.Height / CellHeight;

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
        }

        public void Locate(int row, int column)
        {
            CursorRow = Math.Clamp(row, 0, Math.Max(0, Rows - 1));
            CursorColumn = Math.Clamp(column, 0, Math.Max(0, Columns - 1));
        }

        public void Color(PixelColor foreground, PixelColor? background)
        {
            Foreground = foreground;
            Background = background;
        }

        /// <summary>Wie Color(PixelColor, PixelColor?), aber über
        /// Palette-Indizes statt direkter Farbwerte - `backgroundIndex ==
        /// null` bedeutet wie beim direkten Überload "transparent", nicht
        /// "Index 0" (Index 0 der Palette ist eine ganz normale, eigene
        /// Farbe, siehe Palette-Doku).</summary>
        public void Color(byte foregroundIndex, byte? backgroundIndex)
        {
            Foreground = Palette.GetColor(foregroundIndex);
            Background = backgroundIndex is byte bgIdx ? Palette.GetColor(bgIdx) : null;
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

            if (Background is PixelColor bg)
                Target.FillRect(px, py, CellWidth, CellHeight, bg);

            for (int y = 0; y < CellHeight; y++)
            {
                for (int x = 0; x < CellWidth; x++)
                {
                    if (Font.IsPixelSet(c, x, y))
                        Target.SetPixel(px + x, py + y, Foreground);
                }
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
}
