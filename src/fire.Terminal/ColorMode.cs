namespace fire.Terminal
{
    /// <summary>Wie ein Framebuffer seine Pixel speichert.</summary>
    public enum ColorMode
    {
        /// <summary>4 Byte je Pixel (R, G, B, A) - jede Farbe direkt. Der Normalfall.</summary>
        Rgba = 0,

        /// <summary>1 Byte je Pixel: ein Index in die 256-Farben-<see cref="Palette"/> des Framebuffers (wie VGA-Modus 13h). Ändert man einen
        /// Palette-Eintrag, ändert sich die Farbe ALLER Pixel mit diesem Index (Paletten-Animation).</summary>
        Indexed = 1,
    }
}
