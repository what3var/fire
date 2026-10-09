using System;

namespace fire.Terminal
{
    /// <summary>
    /// Manages <see cref="Renderer"/>s via ascending, unique IDs (see IdManager) - every renderer draws into ONE framebuffer (by ID, see FramebufferManager). It likewise
    /// manages the brushes (<see cref="Brush"/>) and pens (<see cref="Pen"/>) that the drawing functions take as arguments (each with its own ID sequence). Bundles the purely functional
    /// API for terminal emulation and graphics: every method takes only IDs plus primitives, never an object reference (see FramebufferManager).
    ///
    /// Gets the font injected via the constructor (`defaultFont`) instead of knowing it itself.
    /// </summary>
    public sealed class RendererManager
    {
        private readonly IdManager<Renderer> _renderers = new();
        private readonly IdManager<Brush> _brushes = new();
        private readonly IdManager<Pen> _pens = new();
        private readonly FramebufferManager _framebuffers;
        private readonly IGlyphFont _defaultFont;

        public RendererManager(FramebufferManager framebuffers, IGlyphFont defaultFont)
        {
            _framebuffers = framebuffers ?? throw new ArgumentNullException(nameof(framebuffers));
            _defaultFont = defaultFont ?? throw new ArgumentNullException(nameof(defaultFont));
        }

        // ---- Renderer ----

        public int CreateRenderer(int framebufferId) => _renderers.Create(new Renderer(_framebuffers.GetFramebuffer(framebufferId), _defaultFont));

        public bool DestroyRenderer(int id) => _renderers.Destroy(id);

        /// <summary>For continued use on the C# side - not part of the purely ID-based surface API.</summary>
        public Renderer GetRenderer(int id) => _renderers.Get(id);

        public int GetColumns(int id) => _renderers.Get(id).Columns;
        public int GetRows(int id) => _renderers.Get(id).Rows;
        public int GetCursorRow(int id) => _renderers.Get(id).CursorRow;
        public int GetCursorColumn(int id) => _renderers.Get(id).CursorColumn;

        /// <summary>Lets a renderer draw into a DIFFERENT framebuffer (e.g. briefly into an invisible second buffer).</summary>
        public void SetTargetFramebuffer(int rendererId, int framebufferId) =>
            _renderers.Get(rendererId).Target = _framebuffers.GetFramebuffer(framebufferId);

        public void SetClip(int id, int x, int y, int w, int h) => _renderers.Get(id).SetClip(x, y, w, h);
        public void ResetClip(int id) => _renderers.Get(id).ResetClip();

        public bool GetAlphaBlending(int id) => _renderers.Get(id).AlphaBlending;
        public void SetAlphaBlending(int id, bool on) => _renderers.Get(id).AlphaBlending = on;

        public void Print(int id, string text) => _renderers.Get(id).Print(text);
        public void Locate(int id, int row, int column) => _renderers.Get(id).Locate(row, column);
        public void Clear(int id) => _renderers.Get(id).Clear();
        public void ClearTo(int id, int color) => _renderers.Get(id).Clear(Paint.FromArgument(color));

        public int GetCellWidth(int id) => _renderers.Get(id).CellWidth;
        public int GetCellHeight(int id) => _renderers.Get(id).CellHeight;

        /// <summary>Foreground and background colour for Print (numeric values per <see cref="Paint.FromArgument"/>: 0-255 = palette index, otherwise direct value).</summary>
        public void SetColor(int id, int foreground, int background) =>
            _renderers.Get(id).SetColor(Paint.FromArgument(foreground), Paint.FromArgument(background));

        public void SetPixel(int id, int x, int y, int color) => _renderers.Get(id).SetPixel(x, y, Paint.FromArgument(color));
        public int GetPixel(int id, int x, int y) => Paint.ToArgument(_renderers.Get(id).GetPixel(x, y).Packed);

        /// <summary>The palette index of the pixel (in the palette framebuffer the stored one, otherwise the nearest entry).</summary>
        public int GetPixelIndex(int id, int x, int y) => _renderers.Get(id).GetPixelIndex(x, y);

        /// <summary>Text at a PIXEL position; `background` is the ID of a brush or 0 (no background).</summary>
        public void DrawText(int id, int x, int y, string text, int foregroundBrush, int backgroundBrush) =>
            _renderers.Get(id).DrawText(x, y, text, _brushes.Get(foregroundBrush), backgroundBrush == 0 ? null : _brushes.Get(backgroundBrush));

        // ---- Fills (brush ID) ----

        public void FillRect(int id, int x, int y, int w, int h, int brush) => _renderers.Get(id).FillRect(x, y, w, h, _brushes.Get(brush));
        public void Fill(int id, int brush) => _renderers.Get(id).Fill(_brushes.Get(brush));
        public void FillCircle(int id, int cx, int cy, int r, int brush) => _renderers.Get(id).FillCircle(cx, cy, r, _brushes.Get(brush));
        public void FillEllipse(int id, int cx, int cy, int rx, int ry, int brush) => _renderers.Get(id).FillEllipse(cx, cy, rx, ry, _brushes.Get(brush));
        public void FillTriangle(int id, int x0, int y0, int x1, int y1, int x2, int y2, int brush) =>
            _renderers.Get(id).FillTriangle(x0, y0, x1, y1, x2, y2, _brushes.Get(brush));
        public void FillPolygon(int id, int[] points, int brush) => _renderers.Get(id).FillPolygon(points, _brushes.Get(brush));
        public void FloodFill(int id, int x, int y, int brush) => _renderers.Get(id).FloodFill(x, y, _brushes.Get(brush));
        public void FloodFillBorder(int id, int x, int y, int brush, int border) =>
            _renderers.Get(id).FloodFill(x, y, _brushes.Get(brush), Paint.FromArgument(border));

        // ---- Zeichnen (Stift-ID) ----

        public void DrawPoint(int id, int x, int y, int pen) => _renderers.Get(id).DrawPoint(x, y, _pens.Get(pen));
        public void DrawLine(int id, int x0, int y0, int x1, int y1, int pen) => _renderers.Get(id).DrawLine(x0, y0, x1, y1, _pens.Get(pen));
        public void DrawPath(int id, int[] points, int pen, bool closed) => _renderers.Get(id).DrawPath(points, _pens.Get(pen), closed);
        public void DrawRect(int id, int x, int y, int w, int h, int pen) => _renderers.Get(id).DrawRect(x, y, w, h, _pens.Get(pen));
        public void DrawCircle(int id, int cx, int cy, int r, int pen) => _renderers.Get(id).DrawCircle(cx, cy, r, _pens.Get(pen));
        public void DrawEllipse(int id, int cx, int cy, int rx, int ry, int pen) => _renderers.Get(id).DrawEllipse(cx, cy, rx, ry, _pens.Get(pen));
        public void DrawTriangle(int id, int x0, int y0, int x1, int y1, int x2, int y2, int pen) =>
            _renderers.Get(id).DrawTriangle(x0, y0, x1, y1, x2, y2, _pens.Get(pen));
        public void DrawPolygon(int id, int[] points, int pen, bool closed) => _renderers.Get(id).DrawPolygon(points, _pens.Get(pen), closed);

        /// <summary>Copies a section of the framebuffer `sourceFramebufferId` into that of the renderer (see <see cref="Blitter.Blit"/>).</summary>
        public void Blit(int id, int sourceFramebufferId, int sx, int sy, int sw, int sh, int dx, int dy, int dw, int dh, int mode, int colorKey) =>
            _renderers.Get(id).Blit(_framebuffers.GetFramebuffer(sourceFramebufferId), sx, sy, sw, sh, dx, dy, dw, dh, (BlitMode)mode, colorKey);

        /// <summary>Sets palette index `index` of this renderer's framebuffer to a new 32-bit colour value.</summary>
        public void SetPaletteColor(int id, byte index, int color) => _renderers.Get(id).Palette.SetColor(index, color);
        public int GetPaletteColor(int id, byte index) => _renderers.Get(id).Palette.GetColor(index);

        // ---- Pinsel ----

        /// <summary>A single-colour brush (colour: numeric value per <see cref="Paint.FromArgument"/>).</summary>
        public int CreateSolidBrush(int color) => _brushes.Create(new SolidBrush(Paint.FromArgument(color)));
        public bool DestroyBrush(int id) => _brushes.Destroy(id);
        public Brush GetBrush(int id) => _brushes.Get(id);
        public int GetBrushColor(int id) => _brushes.Get(id) is SolidBrush s ? ColorNumber(s.Color) : 0;
        public void SetBrushColor(int id, int color)
        {
            if (_brushes.Get(id) is SolidBrush s) s.Color = Paint.FromArgument(color);
        }

        // ---- Stifte ----

        public int CreatePen(int color, int width, int shape) => _pens.Create(new Pen(Paint.FromArgument(color), width, (PenShape)shape));
        public bool DestroyPen(int id) => _pens.Destroy(id);
        public Pen GetPen(int id) => _pens.Get(id);
        public int GetPenColor(int id) => ColorNumber(_pens.Get(id).Color);
        public void SetPenColor(int id, int color) => _pens.Get(id).Color = Paint.FromArgument(color);
        public int GetPenWidth(int id) => _pens.Get(id).Width;
        public void SetPenWidth(int id, int width) => _pens.Get(id).Width = width;
        public int GetPenShape(int id) => (int)_pens.Get(id).Shape;
        public void SetPenShape(int id, int shape) => _pens.Get(id).Shape = (PenShape)shape;

        /// <summary>The colour specification as a number, as a script gives it (palette index 0-255 or direct value).</summary>
        private static int ColorNumber(Paint paint) => paint.IsIndex ? paint.Index : unchecked((int)paint.Rgba);
    }
}
