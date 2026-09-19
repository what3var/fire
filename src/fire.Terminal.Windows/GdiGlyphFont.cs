using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using ScriptLang.Terminal;

namespace ScriptLang.Terminal.Windows
{
    /// <summary>IGlyphFont-Implementierung, die Glyphen einer INSTALLIERTEN
    /// Monospace-Systemschrift (z.B. "Consolas", "Cascadia Mono") per GDI+
    /// (System.Drawing) rastert - jedes tatsächlich benutzte Zeichen wird
    /// EINMALIG in eine kleine Schwarz/Weiß-Maske gerendert und danach aus
    /// dem Cache gelesen (Bitmap.GetPixel ist zu langsam, um das bei jedem
    /// Print erneut zu tun - hier aber unkritisch, da pro Zeichen nur genau
    /// einmal).
    ///
    /// Bewusst KEINE eingebettete Bitmap-Schrift (siehe IGlyphFont-Doku für
    /// die Austausch-Möglichkeit): eine von Hand eingebettete Bitmap-Font-
    /// Tabelle für einen brauchbaren Zeichensatz ist mehrere hundert Byte
    /// Rohdaten, deren Korrektheit sich ohne die Möglichkeit, sie hier
    /// tatsächlich zu rendern und zu prüfen, nicht verlässlich sicherstellen
    /// lässt - eine echte, bereits im Betriebssystem vorhandene Schrift zu
    /// nutzen ist hier die robustere Wahl.</summary>
    public sealed class GdiGlyphFont : IGlyphFont, IDisposable
    {
        public int GlyphWidth { get; }
        public int GlyphHeight { get; }

        private readonly Font _font;
        private readonly Dictionary<char, bool[]> _cache = new();
        private bool _disposed;

        public GdiGlyphFont(string fontFamily = "Consolas", int glyphHeight = 16)
        {
            if (glyphHeight <= 0) throw new ArgumentOutOfRangeException(nameof(glyphHeight));
            GlyphHeight = glyphHeight;

            // Grobe, aber für Monospace-Schriften zuverlässige Näherung:
            // Pixelgröße direkt als GraphicsUnit.Pixel verwendet, keine
            // Punkt-/DPI-Umrechnung nötig.
            _font = new Font(fontFamily, glyphHeight, FontStyle.Regular, GraphicsUnit.Pixel);

            using var probe = new Bitmap(1, 1);
            using var g = Graphics.FromImage(probe);
            var size = g.MeasureString("M", _font, int.MaxValue, StringFormat.GenericTypographic);
            GlyphWidth = Math.Max(1, (int)MathF.Round(size.Width));
        }

        /// <summary>Gibt die zugrundeliegende GDI+-Font-Ressource frei (die
        /// diese Klasse selbst erzeugt hat, siehe Konstruktor) - der
        /// Glyph-Cache selbst braucht kein Aufräumen (reine `bool[]`-Werte,
        /// keine weiteren nicht verwalteten Ressourcen).</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _font.Dispose();
        }

        public bool IsPixelSet(char c, int px, int py)
        {
            if (px < 0 || px >= GlyphWidth || py < 0 || py >= GlyphHeight) return false;
            if (!_cache.TryGetValue(c, out var mask))
            {
                mask = RasterizeGlyph(c);
                _cache[c] = mask;
            }
            return mask[py * GlyphWidth + px];
        }

        private bool[] RasterizeGlyph(char c)
        {
            var mask = new bool[GlyphWidth * GlyphHeight];

            // Steuerzeichen (z.B. Tab, falls versehentlich hier statt in
            // TerminalCanvas.Print behandelt) rendern als leere Zelle statt
            // eines Platzhalter-Glyphen einer beliebigen Schrift.
            if (char.IsControl(c)) return mask;

            using var bmp = new Bitmap(GlyphWidth, GlyphHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                using var brush = new SolidBrush(Color.White);
                g.DrawString(c.ToString(), _font, brush, new PointF(0, 0), StringFormat.GenericTypographic);
            }

            for (int y = 0; y < GlyphHeight; y++)
            {
                for (int x = 0; x < GlyphWidth; x++)
                {
                    // Schwelle statt reinem Schwarz/Weiß-Vergleich, wegen
                    // Antialiasing/Subpixel-Rendering der Schriftart.
                    mask[y * GlyphWidth + x] = bmp.GetPixel(x, y).R > 96;
                }
            }
            return mask;
        }
    }
}
