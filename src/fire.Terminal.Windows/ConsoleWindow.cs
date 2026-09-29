using System;
using System.Runtime.CompilerServices;
using fire.Terminal;
using fire.Terminal.Sdl;

namespace fire.Terminal.Windows
{
    /// <summary>
    /// Eigenständiges Konsolenfenster (siehe docs/CONSOLE.md) - eine DÜNNE
    /// Hülle, die NUR das Rendering eines EXTERN übergebenen Framebuffers
    /// übernimmt (siehe FramebufferManager) - besitzt bewusst KEINEN
    /// eigenen Framebuffer und KEINE eigene TerminalCanvas mehr (anders als
    /// in einer früheren Ausbaustufe): "Framebuffer", "Konsole"
    /// (TerminalCanvas, siehe ConsoleManager) und "Fenster" (diese Klasse)
    /// sind jetzt drei UNABHÄNGIG voneinander verwaltete Ressourcenarten mit
    /// jeweils eigenen IDs (siehe WindowManager) - ein Fenster zeigt einfach
    /// an, WAS in einem Framebuffer steht, unabhängig davon, WELCHE Konsole
    /// (falls überhaupt eine) gerade hineinzeichnet.
    ///
    /// Zwei Nutzungsarten:
    /// - <see cref="Tick"/>: EIN Zyklus (Events abholen + zeichnen), für
    ///   einen Host, der seine EIGENE Schleife fährt (z.B. eine spätere
    ///   Laufzeit, die daneben auch die Skript-VM taktet) - einfach jeden
    ///   eigenen Schleifendurchlauf einmal aufrufen (siehe WindowManager.
    ///   TickAll für mehrere Fenster auf einmal).
    /// </summary>
    public sealed class ConsoleWindow : IDisposable
    {
        public Framebuffer Framebuffer { get; }

        private readonly IFramebufferRenderer _renderer;
        private bool _opened;
        private bool _disposed;

        private int _handle;

        /// <param name="framebuffer">Der anzuzeigende Framebuffer - lebt
        /// UNABHÄNGIG von diesem Fenster weiter (wird hier weder erzeugt
        /// noch beim Dispose dieses Fensters zerstört).</param>
        /// <param name="renderer">Default: SdlFramebufferRenderer - siehe
        /// IFramebufferRenderer für Austauschmöglichkeiten.</param>
        public ConsoleWindow(Framebuffer framebuffer, IFramebufferRenderer? renderer = null)
        {
            Framebuffer = framebuffer ?? throw new ArgumentNullException(nameof(framebuffer));
            _renderer = renderer ?? new SdlFramebufferRenderer();
        }

        public void Open(int handle, string title = "fire Console")
        {
            if (_opened) return;
            _handle = handle;
            _renderer.Initialize(title, Framebuffer.Width, Framebuffer.Height, _handle);
            _opened = true;
        }

        /// <summary>Ein einzelner Zyklus: Fenster-Events abholen, dann den
        /// AKTUELLEN Inhalt von <see cref="Framebuffer"/> zeichnen - ganz
        /// gleich, was zwischenzeitlich hineingezeichnet hat (eine
        /// TerminalCanvas über ConsoleManager, rohe Grafikoperationen,
        /// direktes Beschreiben der Pixels, ...). Öffnet das Fenster bei
        /// Bedarf automatisch (mit dem Default-Titel), falls Open() noch
        /// nicht explizit aufgerufen wurde. Liefert false, sobald das
        /// Fenster geschlossen wurde - der Host beendet dann üblicherweise
        /// seine eigene Schleife (bzw. entfernt dieses Fenster aus seiner
        /// Verwaltung, siehe WindowManager).</summary>
        public WindowPumpResult? Tick()
        {
            if (!_opened) return null;
            var result = _renderer.PumpEvents();
            _renderer.Present(Framebuffer);
            return result;
        }

        /// <summary>Blockierende Standalone-Schleife - ruft Tick(), bis das
        /// Fenster geschlossen wird. Für einen Host mit eigener Schleife
        /// (typischer späterer Anwendungsfall, siehe Klassen-Doku) NICHT
        /// aufrufen, stattdessen Tick() selbst einbinden.</summary>

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _renderer.Dispose();
        }
    }
}
