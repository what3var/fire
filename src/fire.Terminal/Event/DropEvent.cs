namespace fire.Terminal.Event
{
    /// <summary>Something the user dropped onto the window from another program: files (<see cref="EventType.DropFiles"/>, their paths) or text
    /// (<see cref="EventType.DropText"/>), at a position in framebuffer pixels.</summary>
    public class DropEvent : Event
    {
        public string[] Files { get; init; } = System.Array.Empty<string>();
        public string Text { get; init; } = "";
        public float X { get; init; }
        public float Y { get; init; }
    }

    /// <summary>Something of another program is dragged over the window (<see cref="EventType.DragHover"/>), at a position in framebuffer pixels; what it is, is not
    /// known until it is dropped. <see cref="EventType.DragLeave"/> (a plain <see cref="Event"/>) says that it left without being dropped.</summary>
    public class DragHoverEvent : Event
    {
        public float X { get; init; }
        public float Y { get; init; }
    }
}
