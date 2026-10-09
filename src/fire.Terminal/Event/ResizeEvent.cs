namespace fire.Terminal.Event
{
    /// <summary>The size of the window (whole pixels, in the units in which it was created) has changed. For a window with `AutoResize` the framebuffer already has the new size
    /// if the size is valid (see Framebuffer.IsValidSize).</summary>
    public class ResizeEvent : Event
    {
        public int Width { get; init; }
        public int Height { get; init; }
    }
}
