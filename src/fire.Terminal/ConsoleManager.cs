using System;

namespace fire.Terminal
{
    /// <summary>
    /// Verwaltet "Konsolen" (TerminalCanvas-Instanzen) über aufsteigende,
    /// eindeutige IDs (siehe IdManager) - jede Konsole ist an EINEN
    /// Framebuffer (per ID, siehe FramebufferManager) gebunden. Bündelt
    /// gleichzeitig das rein funktionale API für Terminal-Emulation und
    /// Grafikoperationen (Print/Color/Locate/SetPixel/...) - jede Methode
    /// nimmt nur eine Konsolen-ID plus Primitive entgegen, nie eine
    /// TerminalCanvas-Referenz direkt (siehe FramebufferManager-Doku für
    /// dieselbe Idee).
    ///
    /// Bekommt die zu verwendende Schrift-Implementierung per Konstruktor
    /// injiziert (`defaultFont`, z.B. eine GdiGlyphFont-Instanz aus
    /// fire.Terminal.Windows) statt sie selbst zu kennen - DAS ist
    /// die einzige Stelle, an der dieser sonst plattformunabhängige Manager
    /// überhaupt von einer konkreten IGlyphFont-Implementierung erfährt,
    /// und das passiert einmalig bei der Komposition (siehe
    /// fire.Terminal.Windows), nicht hier im Kern.
    /// </summary>
    public sealed class ConsoleManager
    {
        private readonly IdManager<TerminalCanvas> _consoles = new();
        private readonly FramebufferManager _framebuffers;
        private readonly IGlyphFont _defaultFont;

        public ConsoleManager(FramebufferManager framebuffers, IGlyphFont defaultFont)
        {
            _framebuffers = framebuffers ?? throw new ArgumentNullException(nameof(framebuffers));
            _defaultFont = defaultFont ?? throw new ArgumentNullException(nameof(defaultFont));
        }

        public int CreateConsole(int framebufferId)
        {
            var fb = _framebuffers.GetFramebuffer(framebufferId);
            return _consoles.Create(new TerminalCanvas(fb, _defaultFont));
        }

        public bool DestroyConsole(int id) => _consoles.Destroy(id);

        /// <summary>Für C#-seitige Weiterverwendung - kein Teil des rein-
        /// ID-basierten Oberflächen-APIs.</summary>
        public TerminalCanvas GetConsole(int id) => _consoles.Get(id);

        public int GetColumns(int id) => _consoles.Get(id).Columns;
        public int GetRows(int id) => _consoles.Get(id).Rows;
        public int GetCursorRow(int id) => _consoles.Get(id).CursorRow;
        public int GetCursorColumn(int id) => _consoles.Get(id).CursorColumn;

        /// <summary>Bindet eine Konsole nachträglich an einen ANDEREN
        /// Framebuffer um (siehe TerminalCanvas.Target-Doku - "wählbarer
        /// Framebuffer") - z.B. um kurzzeitig in einen unsichtbaren
        /// zweiten Puffer zu zeichnen und danach zurückzuwechseln.</summary>
        public void SetTargetFramebuffer(int consoleId, int framebufferId) =>
            _consoles.Get(consoleId).Target = _framebuffers.GetFramebuffer(framebufferId);

        public void Print(int id, string text) => _consoles.Get(id).Print(text);
        public void Locate(int id, int row, int column) => _consoles.Get(id).Locate(row, column);
        public void Clear(int id) => _consoles.Get(id).Clear();

        public int GetCellWidth(int id) => _consoles.Get(id).CellWidth;
        public int GetCellHeight(int id) => _consoles.Get(id).CellHeight;

        /// <summary>Text an einer PIXEL-Position (siehe TerminalCanvas.DrawText). Farben sind Zahlenwerte nach der Regel von <see cref="Paint.FromArgument"/>
        /// (0-255 = Palette-Index, sonst direkter Wert: R im niedrigsten Byte, Alpha im höchsten). Beim HINTERGRUND bedeutet 0 (und jeder direkte Wert mit
        /// Alpha 0) "transparent": nur die Zeichen-Pixel werden geschrieben; einen Palette-Index als Hintergrund gibt es hier nicht (vorher ein
        /// Rechteck füllen).</summary>
        public void DrawText(int id, int x, int y, string text, int foreground, int background)
        {
            Paint? bg = null;
            uint rawBackground = unchecked((uint)background);
            if (rawBackground != 0 && (rawBackground & 0xFFFFFF00) == 0) bg = Paint.FromIndex((byte)rawBackground);
            else if ((rawBackground >> 24) != 0) bg = Paint.FromRgba(rawBackground);
            _consoles.Get(id).DrawText(x, y, text, Paint.FromArgument(foreground), bg);
        }

        /// <summary>Vorder- und Hintergrundfarbe für Print (Zahlenwerte nach <see cref="Paint.FromArgument"/>: 0-255 = Palette-Index, sonst direkter Wert).</summary>
        public void SetColor(int id, int foreground, int background) =>
            _consoles.Get(id).SetColor(Paint.FromArgument(foreground), Paint.FromArgument(background));

        public void SetPixel(int id, int x, int y, int color) => _consoles.Get(id).SetPixel(x, y, Paint.FromArgument(color));

        public void SetPixelByIndex(int id, int x, int y, byte paletteIndex) =>
            _consoles.Get(id).SetPixel(x, y, paletteIndex);

        public int GetPixel(int id, int x, int y) => _consoles.Get(id).GetPixel(x, y);

        /// <summary>Der Palette-Index des Pixels (im Palette-Framebuffer der gespeicherte, sonst der nächstliegende Eintrag).</summary>
        public int GetPixelIndex(int id, int x, int y) => _consoles.Get(id).GetPixelIndex(x, y);

        public void DrawLine(int id, int x0, int y0, int x1, int y1, int color) =>
            _consoles.Get(id).DrawLine(x0, y0, x1, y1, Paint.FromArgument(color));

        public void DrawLineByIndex(int id, int x0, int y0, int x1, int y1, byte paletteIndex) =>
            _consoles.Get(id).DrawLine(x0, y0, x1, y1, paletteIndex);

        public void DrawRect(int id, int x, int y, int w, int h, int color) =>
            _consoles.Get(id).DrawRect(x, y, w, h, Paint.FromArgument(color));

        public void DrawRectByIndex(int id, int x, int y, int w, int h, byte paletteIndex) =>
            _consoles.Get(id).DrawRect(x, y, w, h, paletteIndex);

        public void FillRect(int id, int x, int y, int w, int h, int color) =>
            _consoles.Get(id).FillRect(x, y, w, h, Paint.FromArgument(color));

        public void FillRectByIndex(int id, int x, int y, int w, int h, byte paletteIndex) =>
            _consoles.Get(id).FillRect(x, y, w, h, paletteIndex);

        // ---- Kreis, Ellipse, Dreieck, Polygon, Füllung, Kopieren ----

        public void DrawCircle(int id, int cx, int cy, int r, int color) => _consoles.Get(id).DrawCircle(cx, cy, r, Paint.FromArgument(color));
        public void FillCircle(int id, int cx, int cy, int r, int color) => _consoles.Get(id).FillCircle(cx, cy, r, Paint.FromArgument(color));
        public void DrawEllipse(int id, int cx, int cy, int rx, int ry, int color) => _consoles.Get(id).DrawEllipse(cx, cy, rx, ry, Paint.FromArgument(color));
        public void FillEllipse(int id, int cx, int cy, int rx, int ry, int color) => _consoles.Get(id).FillEllipse(cx, cy, rx, ry, Paint.FromArgument(color));

        public void DrawTriangle(int id, int x0, int y0, int x1, int y1, int x2, int y2, int color) =>
            _consoles.Get(id).DrawTriangle(x0, y0, x1, y1, x2, y2, Paint.FromArgument(color));
        public void FillTriangle(int id, int x0, int y0, int x1, int y1, int x2, int y2, int color) =>
            _consoles.Get(id).FillTriangle(x0, y0, x1, y1, x2, y2, Paint.FromArgument(color));

        public void DrawPolygon(int id, int[] points, int color, bool closed) => _consoles.Get(id).DrawPolygon(points, Paint.FromArgument(color), closed);
        public void FillPolygon(int id, int[] points, int color) => _consoles.Get(id).FillPolygon(points, Paint.FromArgument(color));

        public void FloodFill(int id, int x, int y, int color) => _consoles.Get(id).FloodFill(x, y, Paint.FromArgument(color));
        public void FloodFillBorder(int id, int x, int y, int color, int border) =>
            _consoles.Get(id).FloodFill(x, y, Paint.FromArgument(color), Paint.FromArgument(border));

        /// <summary>Kopiert einen Ausschnitt des Framebuffers `sourceFramebufferId` in den der Konsole (siehe <see cref="Blitter.Blit"/>).</summary>
        public void Blit(int id, int sourceFramebufferId, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh, int mode, int colorKey) =>
            _consoles.Get(id).Blit(_framebuffers.GetFramebuffer(sourceFramebufferId), sx, sy, sw, sh, dx, dy, dw, dh, (BlitMode)mode, colorKey);

        /// <summary>Setzt Palette-Index `index` des Framebuffers dieser Konsole auf einen
        /// neuen 32-Bit-Farbwert (siehe Palette.SetColor-Doku). Die Palette gehört dem
        /// Framebuffer (siehe TerminalCanvas.Palette-Doku).</summary>
        public void SetPaletteColor(int id, byte index, int color) =>
            _consoles.Get(id).Palette.SetColor(index, color);

        public int GetPaletteColor(int id, byte index) => _consoles.Get(id).Palette.GetColor(index);
    }
}
