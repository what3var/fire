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
}
