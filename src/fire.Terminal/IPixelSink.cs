namespace fire.Terminal
{
    /// <summary>
    /// Where the shapes of <see cref="Shapes"/> send their pixels: single pixels and horizontal spans (both ends included, any order, the sink clips).
    /// A brush (<see cref="Brush"/>) fills them with its colour, a pen (<see cref="Pen"/>) stamps its tip at every pixel. It is implemented by structs,
    /// so that the generic shapes run without an interface call per pixel.
    /// </summary>
    public interface IPixelSink
    {
        void Point(int x, int y);
        void Span(int y, int x0, int x1);
    }
}
