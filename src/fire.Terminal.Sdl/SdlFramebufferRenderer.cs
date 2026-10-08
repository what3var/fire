using System;
using SDL3;
using fire.Terminal;
using static SDL3.SDL;
using System.Runtime.InteropServices;
using fire.Terminal.Event;

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
    /// Format SDL.PixelFormat.ABGR8888: SDL benennt die gepackten Formate nach der Bit-Reihenfolge des 32-Bit-WORTS (höchstes Byte zuerst),
    /// auf Little-Endian liegen die Bytes also umgekehrt im Speicher. PixelColor.Packed hat R im niedrigsten Byte (Speicherreihenfolge
    /// R,G,B,A) - das ist ABGR8888 (= SDL_PIXELFORMAT_RGBA32 auf Little-Endian). Mit ARGB8888 waren Rot und Blau vertauscht; der Framebuffer-Inhalt
    /// wird ohne Umrechnung hochgeladen.
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
        private bool _vsync = true;

        /// <inheritdoc/>
        public bool VSync
        {
            get => _vsync;
            set
            {
                _vsync = value;
                if (_renderer != IntPtr.Zero) SDL.SetRenderVSync(_renderer, value ? 1 : 0);
            }
        }

        private int _internalHandle;

        // Größe des dargestellten Framebuffers: Mausposition kommt von SDL in FENSTER-Koordinaten, das Fenster darf aber frei skaliert
        // werden (der Framebuffer wird gestreckt) - Skripte bekommen die Position deshalb in Framebuffer-Pixeln.
        private int _fbWidth;
        private int _fbHeight;

        private (float X, float Y) ToFramebuffer(float x, float y)
        {
            if (_window == IntPtr.Zero || _fbWidth <= 0 || !SDL.GetWindowSize(_window, out int w, out int h) || w <= 0 || h <= 0)
                return (x, y);
            return (x * _fbWidth / w, y * _fbHeight / h);
        }

        private (float X, float Y) ToFramebufferRelative(float dx, float dy)
        {
            var origin = ToFramebuffer(0, 0);
            var moved = ToFramebuffer(dx, dy);
            return (moved.X - origin.X, moved.Y - origin.Y);
        }

        public void Initialize(string title, int initialWidth, int initialHeight, int internalHandle)
        {
            _internalHandle = internalHandle;
            _fbWidth = initialWidth;
            _fbHeight = initialHeight;

            if (!SDL.Init(SDL.InitFlags.Video))
                throw new InvalidOperationException($"SDL.Init failed: {SDL.GetError()}");

            // SDL3-CS bietet CreateWindowAndRenderer als EINEN Aufruf (statt
            // getrennt CreateWindow + CreateRenderer wie in SDL2) - laut
            // offizieller Doku der empfohlene Weg, um Fenster-Flackern beim
            // ersten Rendern zu vermeiden.
            if (!SDL.CreateWindowAndRenderer(
                    title, initialWidth, initialHeight,
                    SDL.WindowFlags.Resizable, out _window, out _renderer))
                throw new InvalidOperationException($"SDL.CreateWindowAndRenderer failed: {SDL.GetError()}");

            SDL.SetRenderVSync(_renderer, _vsync ? 1 : 0);

            // SDL3 liefert Texteingabe-Ereignisse (EventType.TextInput) erst, wenn sie für das Fenster eingeschaltet sind.
            SDL.StartTextInput(_window);
        }

        public WindowPumpResult PumpEvents()
        {
            var resultEvents = new List<IEvent>();
            while (SDL.PollEvent(out SDL.Event ev))
            {
                // Zwei leicht unterschiedliche Schreibweisen kursieren in
                // den offiziellen SDL3-CS-Beispielen (mit und ohne
                // expliziten Cast) - der Cast hier ist die sichere Variante,
                // die in BEIDEN Fällen kompiliert (ob 'Type' bereits das
                // Enum ist, oder ein roher uint-Wert).

                var eventType = Event.EventType.Unknown;

                switch((SDL.EventType)ev.Type)
                {
                    case SDL.EventType.Quit:
                        eventType = Event.EventType.Close;
                        resultEvents.Add(new Terminal.Event.Event() { SourceHandle = _internalHandle, Type = eventType });
                        _quit = true;
                        break;
                    case SDL.EventType.WindowResized:
                        // die neue Größe des Fensters (in den Einheiten, in denen es angelegt wurde: so wie die Mauspositionen)
                        resultEvents.Add(new ResizeEvent() { SourceHandle = _internalHandle, Type = Event.EventType.Resize, Width = ev.Window.Data1, Height = ev.Window.Data2 });
                        break;
                    case SDL.EventType.WindowCloseRequested:
                        eventType = Event.EventType.CloseRequest;
                        resultEvents.Add(new Terminal.Event.Event() { SourceHandle = _internalHandle, Type = eventType });
                        break;
                    case SDL.EventType.KeyDown:
                    case SDL.EventType.KeyUp:
                        eventType = ((SDL.EventType)ev.Type) switch
                        {
                            SDL.EventType.KeyUp => Event.EventType.KeyUp,
                            SDL.EventType.KeyDown => Event.EventType.KeyDown,
                            _ => Event.EventType.Unknown
                        };
                        var keyevent = new KeyEvent()
                        {
                            IsButtonDown = ev.Key.Down,
                            IsKeyRepeat = ev.Key.Repeat,
                            KeyCode = (int)ev.Key.Key,
                            ScanCode = (int)ev.Key.Scancode,
                            SourceHandle = _internalHandle,
                            Modifier = (int)ev.Key.Mod,
                            Type = eventType
                        };
                        resultEvents.Add(keyevent);
                        break;
                    case SDL.EventType.MouseButtonUp:
                    case SDL.EventType.MouseButtonDown:
                        eventType = ((SDL.EventType)ev.Type) switch
                        {
                            SDL.EventType.MouseButtonUp => Event.EventType.MouseUp,
                            SDL.EventType.MouseButtonDown => Event.EventType.MouseDown,
                            _ => Event.EventType.Unknown
                        };

                        var clickPos = ToFramebuffer(ev.Button.X, ev.Button.Y);
                        var clickevent = new ClickEvent()
                        {
                            X = clickPos.X,
                            Y = clickPos.Y,
                            Button = (int)ev.Button.Button,
                            IsButtonDown = ev.Button.Down,
                            SourceHandle = _internalHandle,
                            Type = eventType
                        };
                        resultEvents.Add(clickevent);
                        break;
                    case SDL.EventType.MouseMotion:
                        eventType = Event.EventType.MouseMove;

                        var movePos = ToFramebuffer(ev.Motion.X, ev.Motion.Y);
                        var moveRel = ToFramebufferRelative(ev.Motion.XRel, ev.Motion.YRel);
                        var moveevent = new MotionEvent()
                        {
                            ButtonState = (int)ev.Motion.State,
                            X = movePos.X,
                            Y = movePos.Y,
                            //Xrel = ev.Motion.XRel,
                            //Yrel = ev.Motion.YRel,
                            SourceHandle = _internalHandle,
                            Type = eventType
                        };
                        resultEvents.Add(moveevent);
                        var moveevent2 = new MotionEvent()
                        {
                            ButtonState = (int)ev.Motion.State,
                            //X = ev.Motion.X,
                            //Y = ev.Motion.Y,
                            Xrel = moveRel.X,
                            Yrel = moveRel.Y,
                            SourceHandle = _internalHandle,
                            Type = Event.EventType.MouseMoveRelative
                        };
                        resultEvents.Add(moveevent2); 
                        break;
                    case SDL.EventType.MouseWheel:
                        eventType = Event.EventType.MouseScroll;

                        var wheelPos = ToFramebuffer(ev.Wheel.MouseX, ev.Wheel.MouseY);
                        var scrollevent = new ScrollEvent()
                        {
                            ScrollX = ev.Wheel.X,
                            ScrollY = ev.Wheel.Y,
                            X = wheelPos.X,
                            Y = wheelPos.Y,
                            SourceHandle = _internalHandle,
                            Type = eventType
                        };

                        resultEvents.Add(scrollevent);
                        break;
                    case SDL.EventType.TextInput:
                        eventType = Event.EventType.TextInput;

                        var textevent = new TextEvent()
                        {
                            Text = Marshal.PtrToStringUTF8(ev.Text.Text),
                            SourceHandle = _internalHandle,
                            Type = eventType
                        };

                        resultEvents.Add(textevent);
                        break;
                }

            }

            return new WindowPumpResult()
            {
                StillOpen = !_quit,
                Events = resultEvents
            };
        }

        public void Present(Framebuffer framebuffer)
        {
            framebuffer.Resolve(); // Palette-Framebuffer: Pixels aus Indizes und Palette berechnen (im RGBA-Modus ein No-op)
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
                SDL.PixelFormat.ABGR8888, // Speicherreihenfolge R,G,B,A (PixelColor.Packed): auf Little-Endian ABGR8888, nicht ARGB8888 (das vertauschte Rot und Blau)
                SDL.TextureAccess.Streaming,
                width, height);
            if (_texture == IntPtr.Zero)
                throw new InvalidOperationException($"SDL.CreateTexture failed: {SDL.GetError()}");

            _texWidth = width;
            _texHeight = height;
            _fbWidth = width;
            _fbHeight = height;
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
