using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace fire.Terminal
{
    /// <summary>
    /// The renderer (formerly the "Console"; see CONSOLE.md): draws into an <see cref="IRenderTarget"/> (usually a <see cref="Framebuffer"/>) - the software renderer. It has two
    /// sides: a QBasic-like terminal emulation (Print/Locate/SetColor, cursor and colours belong to the renderer, not to the target) and the graphics functions in
    /// PIXEL coordinates, which take <see cref="Brush"/> (fills) and <see cref="Pen"/> (points, lines, paths, outlines) instead of colours. The brushes and pens offer the
    /// actual functions; the renderer only hands them the area (<see cref="Surface"/>) of the target.
    ///
    /// <see cref="AlphaBlending"/> switches the blending of semi-transparent colours on (default) or off (then every colour is copied including its alpha). Blending only happens in a 32-bit target;
    /// in an 8-bit target a colour from alpha 128 up is copied and one below it is not drawn (see <see cref="Surface"/>).
    ///
    /// The number of rows and columns of the character grid result from the size of the target divided by the cell size of the font. Another `Target` keeps cursor and colours
    /// (the position is limited to the new grid).
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

        /// <summary>The area of the target for a drawing call (with the clipping rectangle, see <see cref="SetClip"/>).</summary>
        public Surface Surface => new(_target, AlphaBlending, _clipLeft, _clipTop, _clipRight, _clipBottom);

        private int _clipLeft, _clipTop, _clipRight = int.MaxValue, _clipBottom = int.MaxValue;

        /// <summary>Restricts drawing (shapes, text, fills) to the rectangle (x, y, w, h); it is intersected with the target. Also applies to `Blit`, not to `Clear` and the scrolling of the terminal. <see cref="ResetClip"/> lifts it.</summary>
        public void SetClip(int x, int y, int w, int h)
        {
            _clipLeft = x;
            _clipTop = y;
            _clipRight = (int)Math.Min((long)x + Math.Max(0, w), int.MaxValue);
            _clipBottom = (int)Math.Min((long)y + Math.Max(0, h), int.MaxValue);
        }

        /// <summary>Lifts the clipping rectangle: the whole target again.</summary>
        public void ResetClip()
        {
            _clipLeft = 0;
            _clipTop = 0;
            _clipRight = int.MaxValue;
            _clipBottom = int.MaxValue;
        }

        /// <summary>The clipping rectangle as (x, y, width, height) in the target.</summary>
        public (int X, int Y, int Width, int Height) GetClip()
        {
            var s = Surface;
            return (s.ClipLeft, s.ClipTop, Math.Max(0, s.ClipRight - s.ClipLeft), Math.Max(0, s.ClipBottom - s.ClipTop));
        }

        /// <summary>The 256-colour palette of the target (see Framebuffer.Palette).</summary>
        public Palette Palette => Target.Palette;

        private readonly int _cellWidth;
        private readonly int _cellHeight;
        // the grid follows the size of the target (a framebuffer may change its size, see Framebuffer.Resize); the cursor is pulled back into the grid before writing (Print)
        public int CellWidth => _cellWidth;
        public int CellHeight => _cellHeight;
        public int Columns => _target.Width / _cellWidth;
        public int Rows => _target.Height / _cellHeight;

        public int CursorRow { get; private set; }
        public int CursorColumn { get; private set; }

        // foreground/background of Print as a colour specification (Paint): a palette index stays an index (a palette changed later recolours newly drawn text).
        private Paint _foreground = Paint.FromRgba(PixelColor.White);
        private Paint? _background = Paint.FromRgba(PixelColor.Black);

        public PixelColor Foreground
        {
            get => ToColor(_foreground);
            set => _foreground = Paint.FromRgba(value);
        }

        /// <summary>null = TRANSPARENT: a written cell then overwrites ONLY the glyph pixels themselves (foreground). A set value always paints over the ENTIRE cell.</summary>
        public PixelColor? Background
        {
            get => _background is Paint b ? ToColor(b) : null;
            set => _background = value is PixelColor c ? Paint.FromRgba(c) : null;
        }

        /// <summary>Like <see cref="Foreground"/>/<see cref="Background"/>, but as a colour specification (palette index OR direct value); `background` null = transparent.</summary>
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

        private void UpdateGrid() { }   // (the grid is computed from the size of the target)

        public void Locate(int row, int column)
        {
            CursorRow = Math.Clamp(row, 0, Math.Max(0, Rows - 1));
            CursorColumn = Math.Clamp(column, 0, Math.Max(0, Columns - 1));
        }

        /// <summary>The background as a resolved colour; a transparent background counts as black ("clear screen" without any colour would make no sense).</summary>
        private Pixel ClearPixel() => Surface.Resolve(_background ?? Paint.FromRgba(PixelColor.Black));

        /// <summary>Clears the ENTIRE target with the background colour (as it is, without blending) and sets the cursor to (0, 0).</summary>
        public void Clear()
        {
            ClearAll(ClearPixel());
            CursorRow = 0;
            CursorColumn = 0;
        }

        /// <summary>Sets the whole target to `paint` (without blending: as the colour is, also with alpha).</summary>
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

        /// <summary>Writes `text` from the cursor position, cell by cell. '\r' is skipped, '\n' jumps to the start of the next line. At the end of the line the cursor goes to the next
        /// line; at the end of the screen the ENTIRE content scrolls up by one cell height (what falls out at the top is lost - there is no scrollback).</summary>
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

        /// <summary>Draws character `c` with its top left corner at (x, y) in PIXELS: the pixels of the glyph with the brush `foreground`, with `background` (null = none) the whole
        /// cell beneath.</summary>
        public void DrawGlyph(int x, int y, char c, Brush foreground, Brush? background = null) => DrawText(x, y, c.ToString(), foreground, background);

        /// <summary>Draws `text` from (x, y) in PIXELS, one character after the other (no wrapping, no cursor; '\n' and '\r' are drawn like any other character of the font). The
        /// glyph pixels take the colour of `foreground`, with `background` (null = none) the whole cell beneath is filled.</summary>
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
            // a brush with a pattern: every pixel individually
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

        /// <summary>Width of `text` in pixels (the font is monospaced: number of characters times cell width).</summary>
        public int MeasureText(string text) => text.Length * CellWidth;

        /// <summary>Like <see cref="DrawText(int, int, string, Brush, Brush?)"/>, but in the font `font` (null = the font of the renderer): a bitmap font draws its cells, a TrueType font draws anti-aliased
        /// glyphs of `size` pixels (see <see cref="TextFont"/>); with `background` the line box (width of the text, height of the line) is filled first.</summary>
        public void DrawText(int x, int y, string text, Brush foreground, Brush? background, TextFont? font, int size)
        {
            if (font == null) { DrawText(x, y, text, foreground, background); return; }
            var surface = Surface;
            var fg = foreground.PixelAt(surface, 0, 0);
            bool hasBg = background != null;
            var bg = hasBg ? background!.PixelAt(surface, 0, 0) : default;
            if (font.IsBitmap) font.DrawBitmap(surface, x, y, text, fg, hasBg, bg);
            else font.DrawTrueType(surface, x, y, text, size, fg, hasBg, bg);
        }

        /// <summary>Width of `text` in pixels in the font `font` (null = the font of the renderer).</summary>
        public int MeasureText(string text, TextFont? font, int size) => font == null ? MeasureText(text) : font.Measure(text, size);

        /// <summary>Height of a line of text in pixels in the font `font` (null = the font of the renderer).</summary>
        public int TextHeight(TextFont? font, int size) => font == null ? CellHeight : font.LineHeight(size);

        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // hot and loop-heavy: compile fully optimised straight away, not only after tiering up
        private void DrawGlyphResolved(in Surface surface, int x, int y, char c, in Pixel foreground, bool hasBackground, in Pixel background)
        {
            int cw = CellWidth, ch = CellHeight;

            // a non-visible (fully transparent) background is none; a non-visible foreground draws no glyph pixels
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

                // Narrower font: per row a short loop over the bits.
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

            // General path (palette target, edge of the target, translucent colours, other font): fill the cell, then the pixels of the glyph individually - Put clips and blends.
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

        [MethodImpl(MethodImplOptions.AggressiveOptimization)] // loop-heavy and hot: compile fully optimised straight away, not only after tiering up
        private void DrawTextResolved(in Surface surface, int x, int y, string text, in Pixel foreground, bool hasBackground, in Pixel background)
        {
            int cw = CellWidth, ch = CellHeight;

            // Fast path: if the whole text lies in the buffer and the font has 8-pixel-wide bitmap rows (and the colours are simply copied), font and
            // edge check are done once for the whole text, not per character.
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
                        // Unusual font (too few rows): character by character on the general path
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

        /// <summary>Shifts the ENTIRE content UP by `pixelRows` rows; the bottom rows are filled with `fill`.</summary>
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
        // Graphics functions in PIXEL coordinates: fills take a Brush, drawing takes a Pen
        // -----------------------------------------------------------

        /// <summary>A pixel with the colour `color` (colour specification; blended if blending is on and the colour is semi-transparent).</summary>
        public void SetPixel(int x, int y, Paint color) { var s = Surface; s.Put(x, y, s.Resolve(color)); }
        public void SetPixel(int x, int y, PixelColor color) => SetPixel(x, y, (Paint)color);

        public PixelColor GetPixel(int x, int y)
        {
            var t = Target;
            if ((uint)x >= (uint)t.Width || (uint)y >= (uint)t.Height) return PixelColor.Transparent;
            int i = y * t.Width + x;
            return t.Indices != null ? t.Palette.GetColor(t.Indices[i]) : new PixelColor(t.Pixels[i]);
        }

        /// <summary>The palette index of the pixel (in a 32-bit target the entry that comes closest to the colour). 0 outside.</summary>
        public byte GetPixelIndex(int x, int y)
        {
            var t = Target;
            if ((uint)x >= (uint)t.Width || (uint)y >= (uint)t.Height) return 0;
            int i = y * t.Width + x;
            return t.Indices != null ? t.Indices[i] : t.Palette.FindNearest(new PixelColor(t.Pixels[i]));
        }

        // ---- Fills (Brush) ----

        public void FillRect(int x, int y, int w, int h, Brush brush) => brush.FillRect(Surface, x, y, w, h);
        public void FillCircle(int cx, int cy, int r, Brush brush) => brush.FillCircle(Surface, cx, cy, r);
        public void FillEllipse(int cx, int cy, int rx, int ry, Brush brush) => brush.FillEllipse(Surface, cx, cy, rx, ry);
        public void FillTriangle(int x0, int y0, int x1, int y1, int x2, int y2, Brush brush) => brush.FillTriangle(Surface, x0, y0, x1, y1, x2, y2);

        /// <summary>`points` = x0, y0, x1, y1, ... (an odd last element does not count).</summary>
        public void FillPolygon(int[] points, Brush brush) => brush.FillPolygon(Surface, points);

        /// <summary>Fills the whole target with `brush` (blended if blending is on; to set without blending use <see cref="Clear(Paint)"/>).</summary>
        public void Fill(Brush brush) { var t = Target; brush.FillRect(Surface, 0, 0, t.Width, t.Height); }

        public void FloodFill(int x, int y, Brush brush) => brush.FloodFill(Surface, x, y);
        public void FloodFill(int x, int y, Brush brush, Paint border) => brush.FloodFillBorder(Surface, x, y, border);

        // ---- Zeichnen (Pen) ----

        public void DrawPoint(int x, int y, Pen pen) => pen.DrawPoint(Surface, x, y);
        public void DrawLine(int x0, int y0, int x1, int y1, Pen pen) => pen.DrawLine(Surface, x0, y0, x1, y1);

        /// <summary>A path through the points `points` (x0, y0, x1, y1, ...); `closed` connects the last to the first.</summary>
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

    /// <summary>Writes the rows of an 8-pixel-wide character into the pixel buffer - without range checking: the caller has
    /// ensured that the whole cell (8 pixels x `ch` rows) lies in the buffer. Per row one table access for the pixel mask
    /// (see GlyphMasks) and a vector selection of foreground/background: with AVX2 8 pixels at once, otherwise twice 4.</summary>
    internal static class GlyphBlitter
    {
        /// <summary>Only the set pixels are written (background stays).</summary>
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
                    if (bits == 0) continue; // empty line: nothing to write
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

        /// <summary>The whole cell is written: set pixels in `fg`, the others in `bg`.</summary>
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

    /// <summary>For every bitmap row (0-255) the pixel masks as two vectors of four pixels each (bit 7 = first pixel): a set
    /// bit is 0xFFFFFFFF, an empty one 0. Computed once per process (8 KB), after that one table access per row suffices.</summary>
    internal static class GlyphMasks
    {
        internal static readonly Vector128<uint>[] Table = Build();

        /// <summary>The same masks as ONE vector of eight pixels per bitmap row (for AVX2).</summary>
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
