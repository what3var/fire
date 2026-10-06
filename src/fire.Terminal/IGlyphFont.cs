using System;

namespace fire.Terminal
{
    /// <summary>Abstraktion über die tatsächliche Herkunft der Zeichen-
    /// Bitmaps einer Monospace-Schrift - austauschbar (siehe GdiGlyphFont
    /// für die aktuelle, GDI+-basierte Implementierung), damit später z.B.
    /// eine eingebettete Bitmap-Schrift (für Plattformunabhängigkeit) oder
    /// SDL_ttf eingesetzt werden kann, ohne Renderer anzufassen -
    /// Renderer kennt nur diese Schnittstelle, nie eine konkrete
    /// Implementierung.</summary>
    public interface IGlyphFont
    {
        /// <summary>Breite einer Zelle in Pixeln - bestimmt zusammen mit
        /// GlyphHeight, wie viele Spalten/Zeilen auf einen gegebenen
        /// Framebuffer passen (siehe Renderer.Columns/Rows).</summary>
        int GlyphWidth { get; }
        int GlyphHeight { get; }

        /// <summary>Ist das Pixel an Position (px, py) INNERHALB der Zelle
        /// für Zeichen `c` Teil des Glyphen (Vordergrund) oder nicht
        /// (Hintergrund/leer)? (px, py) liegen im Bereich
        /// [0, GlyphWidth) x [0, GlyphHeight).</summary>
        bool IsPixelSet(char c, int px, int py);
    }

    /// <summary>Eine Schrift, deren Zeichen als Bitmaps aus Zeilen zu je höchstens 8 Bits vorliegen (Bit 7 = linkes Pixel,
    /// 8 Pixel Breite oder weniger): Renderer liest dann pro Zeichen einmal die Zeilen und schreibt die Pixel
    /// direkt in den Framebuffer, statt für jedes Pixel <see cref="IGlyphFont.IsPixelSet"/> zu fragen (Faktor ~10
    /// schneller). Jede andere Schrift funktioniert weiter über IsPixelSet.</summary>
    public interface IBitmapGlyphFont : IGlyphFont
    {
        /// <summary>Die `GlyphHeight` Zeilen von Zeichen `c`, je Zeile ein Byte (Bit 7 = Pixel 0).</summary>
        ReadOnlySpan<byte> GetGlyphRows(char c);
    }
}
