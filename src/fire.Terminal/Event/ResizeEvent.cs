namespace fire.Terminal.Event
{
    /// <summary>Die Größe des Fensters (ganze Pixel, in den Einheiten, in denen es angelegt wurde) hat sich geändert. Bei einem Fenster mit `AutoResize` hat der Framebuffer schon die neue Größe,
    /// wenn die Größe gültig ist (siehe Framebuffer.IsValidSize).</summary>
    public class ResizeEvent : Event
    {
        public int Width { get; init; }
        public int Height { get; init; }
    }
}
