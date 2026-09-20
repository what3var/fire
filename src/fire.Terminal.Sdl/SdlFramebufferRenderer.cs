using System;
using SDL3;
using fire.Terminal;

namespace fire.Terminal.Sdl
{
    /// <summary>IFramebufferRenderer über das SDL3-CS-NuGet-Paket (siehe
    /// .csproj) - bewusst KEINE eigenen [DllImport]-Deklarationen, nur
    /// Aufrufe der bereits fertigen SDL-Bindings dieses Pakets.
    ///
    /// SDL3-CS benennt seine Bindings NICHT mehr 1:1 wie die native C-API
    /// (kein doppeltes "SDL_"-Präfix): aus `SDL_Init` wird `SDL.Init`, aus
    /// `SDL_EVENT_QUIT` wird `SDL.EventType.Quit`, aus `SDL_INIT_VIDEO` wird
    /// `SDL.InitFlags.Video`, usw. - Konstanten-Gruppen sind jeweils zu
    /// einem echten C#-Enum zusammengefasst (siehe die einzelnen Aufrufe
    /// unten), keine losen Ganzzahl-Konstanten mehr.
    ///
    /// Zeichnet den Framebuffer als STREAMING-Textur (SDL.TextureAccess.
    /// Streaming, jeden Frame per SDL.UpdateTexture neu hochgeladen) im
    /// Format SDL.PixelFormat.RGBA8888 (in SDL3 der Name für das gepackte
    /// 32-Bit-RGBA-Format - hieß in früheren SDL3-Vorabversionen "RGBA32",
    /// wurde aber vor dem finalen Release in "RGBA8888" umbenannt) - passend
    /// zu PixelColor.Packed (siehe dortige Doku): "RGBA8888" bedeutet bei
    /// SDL "R,G,B,A in genau dieser Byte-Reihenfolge im Speicher", der
    /// Framebuffer-Inhalt kann dadurch ohne jede Umrechnung direkt
    /// hochgeladen werden.
    ///
    /// Kopiert die Textur per SDL.RenderTexture (in SDL3 der Nachfolger von
    /// SDL2s SDL_RenderCopy) mit `dstrect = NULL` auf das gesamte aktuelle
    /// Render-Ziel - SDL streckt/staucht dabei automatisch auf die
    /// tatsächliche (möglicherweise vom Nutzer per Fenster-Resize
    /// veränderte) Fenstergröße, ganz ohne dass diese Klasse Resize-Events
    /// selbst auswerten müsste (siehe docs/CONSOLE.md "darf einfach auf die
    /// passende Größe skaliert werden").
    ///
    /// HINWEIS: aus dieser Sandbox heraus kein Paket-Restore/Kompilieren
    /// möglich (kein Netzwerkzugriff) - diese Datei ist nach bestem Wissen
    /// gegen die offiziellen SDL3-CS-Beispiele (github.com/edwardgushchin/
    /// SDL3-CS, README/Wiki) geschrieben, aber noch NICHT selbst gegen das
    /// tatsächliche Paket kompiliert worden. Am unsichersten: die genaue
    /// Schreibweise von SDL.TextureAccess.Streaming (Groß-/Kleinschreibung)
    /// - bitte beim ersten lokalen Build gegenprüfen (IntelliSense auf
    /// `SDL.TextureAccess.` zeigt die tatsächlichen Namen).</summary>
    public sealed class SdlFramebufferRenderer : IFramebufferRenderer
    {
        private IntPtr _window;
        private IntPtr _renderer;
        private IntPtr _texture;
        private int _texWidth;
        private int _texHeight;
        private bool _quit;
        private bool _disposed;

        public void Initialize(string title, int initialWidth, int initialHeight)
        {
            if (!SDL.Init(SDL.InitFlags.Video))
                throw new InvalidOperationException($"SDL.Init fehlgeschlagen: {SDL.GetError()}");

            // SDL3-CS bietet CreateWindowAndRenderer als EINEN Aufruf (statt
            // getrennt CreateWindow + CreateRenderer wie in SDL2) - laut
            // offizieller Doku der empfohlene Weg, um Fenster-Flackern beim
            // ersten Rendern zu vermeiden.
            if (!SDL.CreateWindowAndRenderer(
                    title, initialWidth, initialHeight,
                    SDL.WindowFlags.Resizable, out _window, out _renderer))
                throw new InvalidOperationException($"SDL.CreateWindowAndRenderer fehlgeschlagen: {SDL.GetError()}");

            SDL.SetRenderVSync(_renderer, 1);
        }

        public bool PumpEvents()
        {
            while (SDL.PollEvent(out SDL.Event ev))
            {
                // Zwei leicht unterschiedliche Schreibweisen kursieren in
                // den offiziellen SDL3-CS-Beispielen (mit und ohne
                // expliziten Cast) - der Cast hier ist die sichere Variante,
                // die in BEIDEN Fällen kompiliert (ob 'Type' bereits das
                // Enum ist, oder ein roher uint-Wert).
                if ((SDL.EventType)ev.Type == SDL.EventType.Quit)
                    _quit = true;
            }
            return !_quit;
        }

        public void Present(Framebuffer framebuffer)
        {
            EnsureTexture(framebuffer.Width, framebuffer.Height);

            unsafe
            {
                fixed (uint* src = framebuffer.Pixels)
                {
                    SDL.UpdateTexture(_texture, IntPtr.Zero, (IntPtr)src, framebuffer.Width * sizeof(uint));
                }
            }

            SDL.RenderClear(_renderer);
            // dstrect = NULL -> das GESAMTE aktuelle Render-Ziel (die
            // tatsächliche Fenstergröße), nicht die Textur-Originalgröße -
            // das IST die automatische Skalierung.
            SDL.RenderTexture(_renderer, _texture, IntPtr.Zero, IntPtr.Zero);
            SDL.RenderPresent(_renderer);
        }

        private void EnsureTexture(int width, int height)
        {
            if (_texture != IntPtr.Zero && _texWidth == width && _texHeight == height) return;
            if (_texture != IntPtr.Zero) SDL.DestroyTexture(_texture);

            _texture = SDL.CreateTexture(
                _renderer,
                SDL.PixelFormat.ARGB8888,
                SDL.TextureAccess.Streaming,
                width, height);
            if (_texture == IntPtr.Zero)
                throw new InvalidOperationException($"SDL.CreateTexture fehlgeschlagen: {SDL.GetError()}");

            _texWidth = width;
            _texHeight = height;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_texture != IntPtr.Zero) SDL.DestroyTexture(_texture);
            if (_renderer != IntPtr.Zero) SDL.DestroyRenderer(_renderer);
            if (_window != IntPtr.Zero) SDL.DestroyWindow(_window);
            SDL.Quit();
        }
    }
}
