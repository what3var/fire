namespace fire.UI.Bridge
{
    /// <summary>
    /// Die Oberflächen-Bibliothek `#import "ui"`: rein in fire geschriebene Elemente, die in einen Framebuffer zeichnen und ihre
    /// Ereignisse vom Fenster der Grafik-Brücke bekommen. Es gibt keine nativen Funktionen, nur den Quelltext
    /// <see cref="PreludeSource"/> (siehe UiPrelude.cs); `ui` setzt `graphics` voraus und schaltet es selbst mit zu (siehe
    /// ImportedPreludes).
    /// </summary>
    public static partial class UiBridge
    {
    }
}
