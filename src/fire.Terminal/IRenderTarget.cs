namespace fire.Terminal
{
    /// <summary>
    /// Something a <see cref="Renderer"/> draws into: an area of pixels in memory. The <see cref="Framebuffer"/> is the default target; another target (a section,
    /// a window that holds its own area) needs only these sizes, the colour mode and the two pixel fields. The software renderer writes directly into the fields
    /// (row by row, `y * Width + x`) - an accelerated renderer could instead be a `Renderer` of its own for another target.
    /// </summary>
    public interface IRenderTarget
    {
        int Width { get; }
        int Height { get; }

        /// <summary>How the pixels are stored: 32 bit (R, G, B, A) or 8 bit (index into the <see cref="Palette"/>). Only a 32-bit target blends (alpha blending);
        /// in an 8-bit target a colour from alpha 128 up is copied, below that it is not drawn.</summary>
        ColorMode Mode { get; }

        Palette Palette { get; }

        /// <summary>One uint per pixel (R, G, B, A, see <see cref="PixelColor"/>). In an 8-bit target only the computed image of the indices (see <see cref="Framebuffer.Resolve"/>).</summary>
        uint[] Pixels { get; }

        /// <summary>8-bit target only: the palette index per pixel; otherwise null. Whoever writes it calls <see cref="MarkDirty"/> afterwards.</summary>
        byte[]? Indices { get; }

        /// <summary>The transparent index of a palette image (see <see cref="Framebuffer.TransparentIndex"/>), or -1.</summary>
        int TransparentIndex { get; }

        /// <summary>Notes that <see cref="Indices"/> were changed from outside.</summary>
        void MarkDirty();
    }
}
