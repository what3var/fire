using System;

namespace fire.Terminal
{
    /// <summary>Abstraktion über das tatsächliche Zeichnen eines
    /// Framebuffers in einem Fenster - SDL (siehe Sdl.SdlFramebufferRenderer)
    /// ist nur EINE mögliche Implementierung. ConsoleWindow kennt nur diese
    /// Schnittstelle, nie SDL direkt - das Rendering-Backend lässt sich
    /// dadurch später austauschen, ohne Framebuffer/TerminalCanvas/
    /// ConsoleWindow selbst anzufassen (siehe SPEC/CONSOLE.md-Notiz "damit
    /// ich das Rendering austauschen kann").</summary>
    public interface IFramebufferRenderer : IDisposable
    {
        /// <summary>Öffnet das eigentliche Fenster. `initialWidth`/Height
        /// sind Pixelgrößen (typischerweise die Größe des initialen
        /// Framebuffers) - das Fenster darf vom Nutzer danach frei in der
        /// Größe verändert werden, der dargestellte Framebuffer-Inhalt wird
        /// dabei einfach gestreckt/gestaucht (siehe Present-Doku), nicht neu
        /// gerastert.</summary>
        void Initialize(string title, int initialWidth, int initialHeight, int internalHandle);

        /// <summary>Verarbeitet alle anstehenden Fenster-Events (Resize,
        /// Schließen, ...). Liefert false, sobald das Fenster geschlossen
        /// werden soll (der Aufrufer beendet dann üblicherweise seine
        /// Schleife) - macht selbst KEIN Present, das ist ein eigener aufruf.</summary>
        WindowPumpResult PumpEvents();

        /// <summary>Zeichnet den kompletten aktuellen Inhalt von
        /// `framebuffer` - IMMER auf die volle aktuelle Fenstergröße
        /// gestreckt (siehe Initialize-Doku), unabhängig davon, ob diese der
        /// Framebuffer-Größe entspricht. Ein Framebuffer anderer Größe als
        /// beim letzten Aufruf wird automatisch unterstützt (kein erneutes
        /// Initialize nötig).</summary>
        void Present(Framebuffer framebuffer);

        /// <summary>Wartet <see cref="Present"/> auf die Bildwiederholung des Monitors (vertikale Synchronisation)? Vorgabe: ja.
        /// Dann dauert jeder Zyklus (ConsoleWindow.Tick) bis zu einer Bildperiode (60 Hz: 16,7 ms) - gleichmäßig und ohne
        /// Rechenlast für Animationen und Warteschleifen, aber 256 Ticks in einer Zeichenschleife brauchen über vier Sekunden,
        /// auch wenn das Zeichnen selbst nur Millisekunden dauert. Ohne VSync kehrt Present sofort zurück.
        /// Darf vor dem Öffnen des Fensters gesetzt werden.</summary>
        bool VSync { get; set; }
    }
}
