using System.Reflection;

namespace fire.UI.Bridge
{
    /// <summary>
    /// Die Oberflächen-Bibliothek `#import "ui"`: rein in fire geschriebene Elemente, die in einen Framebuffer zeichnen und ihre
    /// Ereignisse vom Fenster der Grafik-Brücke bekommen. Es gibt keine nativen Funktionen, nur den Quelltext
    /// <see cref="PreludeSource"/>; `ui` setzt `graphics` voraus und schaltet es selbst mit zu (siehe ImportedPreludes).
    ///
    /// Aufbau: `UI.Root` ist das oberste Element. Sein Konstruktor nimmt den Framebuffer, in den gezeichnet wird, und das Fenster, von dem die Ereignisse
    /// (Maus, Tastatur, Text) kommen; er legt selbst den Renderer zum Zeichnen an und schaltet dort die Ereignis-Warteschlange ein (`Window.EnableEvents`).
    /// `Root.Tick()` ordnet die Oberfläche an und zeichnet sie, lässt das Fenster einen Zyklus laufen (Ereignisse abholen, Framebuffer anzeigen) und
    /// verarbeitet die angekommenen Ereignisse - jede Schleife ruft es einmal auf.
    ///
    /// Elemente liegen in Containern (siehe docs/UI.md). Ein Element gehört seinem Container (`Add` ruft `TakeTo`): die Oberfläche lebt so, solange ihr Root
    /// lebt, auch wenn sie in einer Hilfsfunktion aufgebaut wurde. Auf Klicks reagiert man mit einem Lambda (`button.onClick = func () => { ... }`; es läuft im
    /// Hauptprogramm und sieht die echten globalen Variablen) oder durch Abfragen in der eigenen Schleife (`if (button.TakeClicked()) { ... }`). Gezeichnet wird mit dem
    /// `Renderer` der Grafik-Brücke: Flächen mit einem `Brush`, Rahmen und Linien mit einem `Pen`, Text mit einem Brush.
    ///
    /// Der Quelltext steht in <c>ui/*.fire</c> (eingebettet, nach Dateinamen geordnet): so lässt er sich mit dem Editor bearbeiten und prüfen.
    /// </summary>
    public static partial class UiBridge
    {
        /// <summary>The source of the library (`namespace UI`), to be put BEFORE the user's script when there is `#import "ui"`.</summary>
        public static string PreludeSource { get; } = ReadSource();

        private static string ReadSource()
        {
            var assembly = typeof(UiBridge).Assembly;
            var parts = assembly.GetManifestResourceNames()
                .Select(name => (Name: name.Replace('\\', '/'), Actual: name))
                .Where(n => n.Name.StartsWith("ui/", StringComparison.Ordinal) && n.Name.EndsWith(".fire", StringComparison.Ordinal))
                .OrderBy(n => n.Name, StringComparer.Ordinal)
                .Select(n =>
                {
                    using var reader = new StreamReader(assembly.GetManifestResourceStream(n.Actual)!);
                    return reader.ReadToEnd();
                });
            return string.Join("\n", parts);
        }
    }
}
