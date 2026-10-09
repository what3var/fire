using System;

namespace fire.Terminal
{
    /// <summary>Abstraction over the actual origin of the character
    /// bitmaps of a monospace font - exchangeable (see GdiGlyphFont
    /// for the current, GDI+-based implementation), so that later e.g.
    /// an embedded bitmap font (for platform independence) or
    /// SDL_ttf can be used without touching the renderer -
    /// the renderer only knows this interface, never a concrete
    /// implementation.</summary>
    public interface IGlyphFont
    {
        /// <summary>Width of a cell in pixels - together with
        /// GlyphHeight it determines how many columns/rows fit on a given
        /// framebuffer (see Renderer.Columns/Rows).</summary>
        int GlyphWidth { get; }
        int GlyphHeight { get; }

        /// <summary>Is the pixel at position (px, py) INSIDE the cell
        /// for character `c` part of the glyph (foreground) or not
        /// (background/empty)? (px, py) lie in the range
        /// [0, GlyphWidth) x [0, GlyphHeight).</summary>
        bool IsPixelSet(char c, int px, int py);
    }

    /// <summary>A font whose characters exist as bitmaps made of rows of at most 8 bits each (bit 7 = left pixel,
    /// 8 pixels wide or less): the renderer then reads the rows once per character and writes the pixels
    /// directly into the framebuffer, instead of asking <see cref="IGlyphFont.IsPixelSet"/> for every pixel (a factor of ~10
    /// faster). Every other font keeps working via IsPixelSet.</summary>
    public interface IBitmapGlyphFont : IGlyphFont
    {
        /// <summary>The `GlyphHeight` rows of character `c`, one byte per row (bit 7 = pixel 0).</summary>
        ReadOnlySpan<byte> GetGlyphRows(char c);
    }
}
