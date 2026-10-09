namespace fire.Terminal
{
    /// <summary>How a framebuffer stores its pixels.</summary>
    public enum ColorMode
    {
        /// <summary>4 bytes per pixel (R, G, B, A) - every colour directly. The normal case.</summary>
        Rgba = 0,

        /// <summary>1 byte per pixel: an index into the 256-colour <see cref="Palette"/> of the framebuffer (like VGA mode 13h). If a
        /// palette entry is changed, the colour of ALL pixels with that index changes (palette animation).</summary>
        Indexed = 1,
    }
}
