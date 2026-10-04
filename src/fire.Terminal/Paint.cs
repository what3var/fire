namespace fire.Terminal
{
    /// <summary>
    /// Eine Farbangabe des Aufrufers: entweder ein Index der Palette des Ziel-Framebuffers (0-255) oder ein direkter RGBA-Wert. Erst
    /// <see cref="Framebuffer.ResolveBrush"/> macht daraus die Farbe für GENAU diesen Framebuffer - dieselbe Angabe zeichnet also in jedem
    /// Farbmodus: im RGBA-Framebuffer wird ein Index über die Palette in eine Farbe übersetzt, im Palette-Framebuffer ein RGBA-Wert auf den
    /// nächsten Palette-Eintrag abgebildet.
    ///
    /// Aus einem Zahlenwert (Skript-Argument) entsteht sie nach der Regel von `Console.SetColor`: ein Wert, in dem nur das niedrigste Byte
    /// belegt ist (0-255), ist ein Palette-Index; jeder andere ein direkter Wert (R im niedrigsten Byte, Alpha im höchsten).
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

        public static implicit operator Paint(PixelColor color) => FromRgba(color);
    }

    /// <summary>Eine Farbe, aufgelöst für einen bestimmten Framebuffer (siehe <see cref="Framebuffer.ResolveBrush"/>): `Rgba` für einen
    /// RGBA-Framebuffer, `Index` für einen Palette-Framebuffer. Die Zeichenfunktionen arbeiten nur noch damit.</summary>
    public readonly struct Brush
    {
        public readonly uint Rgba;
        public readonly byte Index;

        public Brush(uint rgba, byte index)
        {
            Rgba = rgba;
            Index = index;
        }
    }
}
