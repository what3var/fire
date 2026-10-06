namespace fire.Terminal
{
    /// <summary>
    /// Wohin die Formen von <see cref="Shapes"/> ihre Pixel schicken: einzelne Pixel und waagerechte Spans (beide Enden eingeschlossen, beliebige Reihenfolge, die Senke beschneidet).
    /// Ein Pinsel (<see cref="Brush"/>) füllt sie mit seiner Farbe, ein Stift (<see cref="Pen"/>) stempelt an jedem Pixel seine Spitze. Implementiert wird sie von Strukturen,
    /// damit die generischen Formen ohne Schnittstellenaufruf je Pixel laufen.
    /// </summary>
    public interface IPixelSink
    {
        void Point(int x, int y);
        void Span(int y, int x0, int x1);
    }
}
