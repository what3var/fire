namespace ScriptLang.Terminal
{
    /// <summary>Abstraktion über die tatsächliche Herkunft der Zeichen-
    /// Bitmaps einer Monospace-Schrift - austauschbar (siehe GdiGlyphFont
    /// für die aktuelle, GDI+-basierte Implementierung), damit später z.B.
    /// eine eingebettete Bitmap-Schrift (für Plattformunabhängigkeit) oder
    /// SDL_ttf eingesetzt werden kann, ohne TerminalCanvas anzufassen -
    /// TerminalCanvas kennt nur diese Schnittstelle, nie eine konkrete
    /// Implementierung.</summary>
    public interface IGlyphFont
    {
        /// <summary>Breite einer Zelle in Pixeln - bestimmt zusammen mit
        /// GlyphHeight, wie viele Spalten/Zeilen auf einen gegebenen
        /// Framebuffer passen (siehe TerminalCanvas.Columns/Rows).</summary>
        int GlyphWidth { get; }
        int GlyphHeight { get; }

        /// <summary>Ist das Pixel an Position (px, py) INNERHALB der Zelle
        /// für Zeichen `c` Teil des Glyphen (Vordergrund) oder nicht
        /// (Hintergrund/leer)? (px, py) liegen im Bereich
        /// [0, GlyphWidth) x [0, GlyphHeight).</summary>
        bool IsPixelSet(char c, int px, int py);
    }
}
