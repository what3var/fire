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

        /// <summary>Text an einer PIXEL-Position (siehe TerminalCanvas.DrawText). Farben sind hier ROHE 32-Bit-Werte (R im
        /// niedrigsten Byte, Alpha im höchsten - keine Palette-Indizes wie bei SetColor); ein Hintergrund mit Alpha 0 (z.B. 0)
        /// bedeutet "transparent".</summary>
        public void DrawText(int id, int x, int y, string text, int foreground, int background)
        {
            var bg = new PixelColor(unchecked((uint)background));
            _consoles.Get(id).DrawText(x, y, text, new PixelColor(unchecked((uint)foreground)), bg.A == 0 ? null : bg);
        }

        public void SetColor(int id, int foreground, int? background)
        {
            var c = _consoles.Get(id);

            if ((foreground & 0xFFFFFF00) == 0)
                foreground = GetPaletteColor(id, (byte)foreground);

            var hasBackground = false;
            var bg = background ?? 0;

            if (background is int && (bg & 0xFFFFFF00) == 0)
            { 
                background = GetPaletteColor(id, (byte)bg);
                hasBackground = true;
            }

            c.Foreground = new PixelColor(unchecked((uint)foreground));
            c.Background = hasBackground ? new PixelColor(unchecked((uint)bg)) : null;
        }

        public void SetPixel(int id, int x, int y, int color) =>
            _consoles.Get(id).SetPixel(x, y, new PixelColor(unchecked((uint)color)));

        public void SetPixelByIndex(int id, int x, int y, byte paletteIndex) =>
            _consoles.Get(id).SetPixel(x, y, paletteIndex);

        public int GetPixel(int id, int x, int y) => _consoles.Get(id).GetPixel(x, y);

        public void DrawLine(int id, int x0, int y0, int x1, int y1, int color) =>
            _consoles.Get(id).DrawLine(x0, y0, x1, y1, new PixelColor(unchecked((uint)color)));

        public void DrawLineByIndex(int id, int x0, int y0, int x1, int y1, byte paletteIndex) =>
            _consoles.Get(id).DrawLine(x0, y0, x1, y1, paletteIndex);

        public void DrawRect(int id, int x, int y, int w, int h, int color) =>
            _consoles.Get(id).DrawRect(x, y, w, h, new PixelColor(unchecked((uint)color)));

        public void DrawRectByIndex(int id, int x, int y, int w, int h, byte paletteIndex) =>
            _consoles.Get(id).DrawRect(x, y, w, h, paletteIndex);

        public void FillRect(int id, int x, int y, int w, int h, int color) =>
            _consoles.Get(id).FillRect(x, y, w, h, new PixelColor(unchecked((uint)color)));

        public void FillRectByIndex(int id, int x, int y, int w, int h, byte paletteIndex) =>
            _consoles.Get(id).FillRect(x, y, w, h, paletteIndex);

        /// <summary>Setzt Palette-Index `index` dieser Konsole auf einen
        /// neuen 32-Bit-Farbwert (siehe Palette.SetColor-Doku) - jede
        /// Konsole hat ihre EIGENE Palette (wie Cursor/Farben, siehe
        /// TerminalCanvas.Palette-Doku).</summary>
        public void SetPaletteColor(int id, byte index, int color) =>
            _consoles.Get(id).Palette.SetColor(index, color);

        public int GetPaletteColor(int id, byte index) => _consoles.Get(id).Palette.GetColor(index);
    }
}
