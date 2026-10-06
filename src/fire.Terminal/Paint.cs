namespace fire.Terminal
{
    /// <summary>
    /// Eine Farbangabe des Aufrufers: entweder ein Index der Palette des Ziel-Framebuffers (0-255) oder ein direkter RGBA-Wert. Erst
    /// <see cref="Surface.Resolve"/> macht daraus die Farbe für GENAU diesen Framebuffer - dieselbe Angabe zeichnet also in jedem
    /// Farbmodus: im RGBA-Framebuffer wird ein Index über die Palette in eine Farbe übersetzt, im Palette-Framebuffer ein RGBA-Wert auf den
    /// nächsten Palette-Eintrag abgebildet.
    ///
    /// Aus einem Zahlenwert (Skript-Argument) entsteht sie nach der Regel von `Renderer.SetColor`: ein Wert, in dem nur das niedrigste Byte
    /// belegt ist (0-255), ist ein Palette-Index; jeder andere ein direkter Wert (R im niedrigsten Byte, Alpha im höchsten). Ein Palette-Index
    /// belegt also nur das R-Byte, Alpha bleibt 0 - er kollidiert nur mit durchsichtigen Farben, deren G und B 0 sind (vor allem 0 = durchsichtiges
    /// Schwarz, das QBasic-Schwarz des Index 0). Deshalb ist die kanonische durchsichtige Farbe (0, 1, 0, 0) = 256 (<see cref="Transparent"/>),
    /// und <see cref="ToArgument"/> bildet solche Werte darauf ab, wo ein Pixel als Zahl an ein Skript geht.
    /// </summary>
    public readonly struct Paint
    {
        /// <summary>Der direkte Wert (gepackt, siehe PixelColor) - nur gültig, wenn <see cref="IsIndex"/> falsch ist.</summary>
        public readonly uint Rgba;

        /// <summary>Palette-Index 0-255, oder -1: ein direkter RGBA-Wert.</summary>
        public readonly short Index;

        private Paint(uint rgba, short index)
        {
            Rgba = rgba;
            Index = index;
        }

        public bool IsIndex => Index >= 0;

        public static Paint FromIndex(byte index) => new(0, index);
        public static Paint FromRgba(PixelColor color) => new(color.Packed, -1);
        public static Paint FromRgba(uint packed) => new(packed, -1);

        /// <summary>Die Regel für Zahlenwerte aus Skripten (siehe Klassen-Doku). Nur die unteren 32 Bit zählen (ein vorzeichenbehafteter Wert
        /// wie der von `GetPixel` ist derselbe Wert).</summary>
        public static Paint FromArgument(long value)
        {
            uint raw = unchecked((uint)value);
            return (raw & 0xFFFFFF00u) == 0 ? FromIndex((byte)raw) : FromRgba(raw);
        }

        /// <summary>Die kanonische durchsichtige Farbe als Zahlenwert: (0, 1, 0, 0) - sie ist komplett durchsichtig, wird aber nicht als Palette-Index gelesen.</summary>
        public const int Transparent = 0x100;

        /// <summary>Ein Pixelwert als Zahl für ein Skript: ein Wert, der als Palette-Index gelesen würde (nur das R-Byte belegt, also immer durchsichtig), wird
        /// zu <see cref="Transparent"/> - so bleibt ein gelesenes Pixel beim Zurückschreiben durchsichtig statt zu Palette-Schwarz zu werden.</summary>
        public static int ToArgument(uint packed) => (packed & 0xFFFFFF00u) == 0 ? Transparent : unchecked((int)packed);

        public static implicit operator Paint(PixelColor color) => FromRgba(color);
    }

    /// <summary>Eine Farbe, aufgelöst für ein bestimmtes Ziel (siehe <see cref="Surface.Resolve"/>): `Rgba` ist der Farbwert (für einen RGBA-Framebuffer das, was ins Pixel
    /// geschrieben wird, auch sein Alpha gilt beim Mischen), `Index` der Palette-Eintrag für einen Palette-Framebuffer. Die Zeichenfunktionen arbeiten nur noch damit.</summary>
    public readonly struct Pixel
    {
        public readonly uint Rgba;
        public readonly byte Index;

        public Pixel(uint rgba, byte index)
        {
            Rgba = rgba;
            Index = index;
        }

        /// <summary>Das Alpha des Farbwerts (255 = deckend).</summary>
        public byte Alpha => (byte)(Rgba >> 24);
    }
}
