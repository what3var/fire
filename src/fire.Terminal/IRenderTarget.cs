namespace fire.Terminal
{
    /// <summary>
    /// Etwas, in das ein <see cref="Renderer"/> zeichnet: eine Fläche aus Pixeln im Speicher. Der <see cref="Framebuffer"/> ist das Standardziel; ein anderes Ziel (ein Ausschnitt,
    /// ein Fenster, das seine Fläche selbst hält) braucht nur diese Größen, den Farbmodus und die beiden Pixelfelder. Der Software-Renderer schreibt direkt in die Felder
    /// (zeilenweise, `y * Width + x`) - ein beschleunigter Renderer könnte dagegen ein eigener `Renderer` für ein anderes Ziel sein.
    /// </summary>
    public interface IRenderTarget
    {
        int Width { get; }
        int Height { get; }

        /// <summary>Wie die Pixel gespeichert werden: 32 Bit (R, G, B, A) oder 8 Bit (Index in die <see cref="Palette"/>). Nur ein 32-Bit-Ziel mischt (Alpha-Blending);
        /// in einem 8-Bit-Ziel wird eine Farbe ab Alpha 128 kopiert, darunter nicht gezeichnet.</summary>
        ColorMode Mode { get; }

        Palette Palette { get; }

        /// <summary>Ein uint je Pixel (R, G, B, A, siehe <see cref="PixelColor"/>). In einem 8-Bit-Ziel nur das berechnete Abbild der Indizes (siehe <see cref="Framebuffer.Resolve"/>).</summary>
        uint[] Pixels { get; }

        /// <summary>Nur in einem 8-Bit-Ziel: der Palette-Index je Pixel; sonst null. Wer es beschreibt, ruft danach <see cref="MarkDirty"/> auf.</summary>
        byte[]? Indices { get; }

        /// <summary>Der durchsichtige Index eines Palette-Bildes (siehe <see cref="Framebuffer.TransparentIndex"/>), oder -1.</summary>
        int TransparentIndex { get; }

        /// <summary>Vermerkt, dass sich <see cref="Indices"/> von außen geändert haben.</summary>
        void MarkDirty();
    }
}
