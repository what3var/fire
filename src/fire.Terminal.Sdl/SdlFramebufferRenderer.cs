using System;
using SDL3;
using fire.Terminal;
using static SDL3.SDL;
using System.Runtime.InteropServices;
using fire.Terminal.Event;

namespace fire.Terminal.Sdl
{
    /// <summary>IFramebufferRenderer via the SDL3-CS NuGet package (see
    /// .csproj) - deliberately NO [DllImport] declarations of its own, only
    /// calls of the ready-made SDL bindings of this package.
    ///
    /// SDL3-CS no longer names its bindings 1:1 like the
    /// native C API (no doubled "SDL_" prefix): `SDL_Init` becomes `SDL.Init`,
    /// `SDL_EVENT_QUIT` becomes `SDL.EventType.Quit`, `SDL_INIT_VIDEO` becomes
    /// `SDL.InitFlags.Video`, and so on - constant groups are each combined into
    /// a real C# enum (see the individual calls
    /// below), no more loose integer constants.
    ///
    /// Draws the framebuffer as a STREAMING texture (SDL.TextureAccess.
    /// Streaming, uploaded anew every frame via SDL.UpdateTexture) in the
    /// format SDL.PixelFormat.ABGR8888: SDL names the packed formats after the bit order of the 32-bit WORD (highest byte first),
    /// so on little-endian the bytes are reversed in memory. PixelColor.Packed has R in the lowest byte (memory order
    /// R,G,B,A) - that is ABGR8888 (= SDL_PIXELFORMAT_RGBA32 on little-endian). With ARGB8888 red and blue were swapped; the framebuffer content
    /// is uploaded without conversion.
    ///
    /// Copies the texture via SDL.RenderTexture (in SDL3 the successor of
    /// SDL2's SDL_RenderCopy) with `dstrect = NULL` onto the entire current
    /// render target - SDL automatically stretches/squeezes it to the
    /// actual window size (possibly changed by the user via window resize),
    /// without this class having to evaluate resize events
    /// itself (see docs/CONSOLE.md "may simply be scaled to the
    /// fitting size").
    ///
    /// NOTE: package restore/compiling was not possible from this sandbox
    /// (no network access) - this file is written to the best of knowledge
    /// against the official SDL3-CS examples (github.com/edwardgushchin/
    /// SDL3-CS, README/wiki), but has NOT yet been compiled against the
    /// actual package itself. Least certain: the exact
    /// spelling of SDL.TextureAccess.Streaming (capitalization)
    /// - please double-check on the first local build (IntelliSense on
    /// `SDL.TextureAccess.` shows the actual names).</summary>
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
        private bool _touchMouse = true;
        // the opened joysticks (instance ID -> handle); a joystick only sends events once it is opened
        private readonly Dictionary<uint, IntPtr> _joysticks = new();

        /// <inheritdoc/>
        public bool TouchMouse
        {
            get => _touchMouse;
            set
            {
                _touchMouse = value;
                SDL.SetHint(SDL.Hints.TouchMouseEvents, value ? "1" : "0");
            }
        }

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

        // Size of the framebuffer being shown: the mouse position comes from SDL in WINDOW coordinates, but the window may be freely scaled
        // (the framebuffer is stretched) - scripts therefore get the position in framebuffer pixels.
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
            // Joysticks are a bonus: without a driver the window opens anyway. The devices that are already plugged in report themselves afterwards as JoystickAdded.
            SDL.InitSubSystem(SDL.InitFlags.Joystick);
            SDL.SetHint(SDL.Hints.TouchMouseEvents, _touchMouse ? "1" : "0");

            // SDL3-CS offers CreateWindowAndRenderer as ONE call (instead of
            // separate CreateWindow + CreateRenderer as in SDL2) - according to the
            // official documentation the recommended way to avoid window flicker on the
            // first render.
            if (!SDL.CreateWindowAndRenderer(
                    title, initialWidth, initialHeight,
                    SDL.WindowFlags.Resizable, out _window, out _renderer))
                throw new InvalidOperationException($"SDL.CreateWindowAndRenderer failed: {SDL.GetError()}");

            SDL.SetRenderVSync(_renderer, _vsync ? 1 : 0);

            // SDL3 delivers text input events (EventType.TextInput) only once they are enabled for the window.
            SDL.StartTextInput(_window);
        }

        public WindowPumpResult PumpEvents()
        {
            var resultEvents = new List<IEvent>();
            while (SDL.PollEvent(out SDL.Event ev))
            {
                // Two slightly different spellings circulate in
                // the official SDL3-CS examples (with and without an
                // explicit cast) - the cast here is the safe variant,
                // which compiles in BOTH cases (whether 'Type' is already the
                // enum, or a raw uint value).

                var eventType = Event.EventType.Unknown;

                switch((SDL.EventType)ev.Type)
                {
                    case SDL.EventType.Quit:
                        eventType = Event.EventType.Close;
                        resultEvents.Add(new Terminal.Event.Event() { SourceHandle = _internalHandle, Type = eventType });
                        _quit = true;
                        break;
                    case SDL.EventType.WindowResized:
                        // the new size of the window (in the units in which it was created: like the mouse positions)
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
                    case SDL.EventType.FingerDown:
                    case SDL.EventType.FingerMotion:
                    case SDL.EventType.FingerUp:
                        // the position comes from SDL normalised (0 to 1 across the window): in framebuffer pixels it is simply x * width
                        resultEvents.Add(new TouchEvent()
                        {
                            Type = ((SDL.EventType)ev.Type) switch { SDL.EventType.FingerDown => Event.EventType.TouchDown, SDL.EventType.FingerUp => Event.EventType.TouchUp, _ => Event.EventType.TouchMove },
                            Finger = (long)ev.TFinger.FingerID,
                            X = ev.TFinger.X * _fbWidth,
                            Y = ev.TFinger.Y * _fbHeight,
                            Pressure = ev.TFinger.Pressure,
                            SourceHandle = _internalHandle,
                        });
                        break;
                    case SDL.EventType.JoystickAdded:
                        {
                            uint id = ev.JDevice.Which;
                            if (!_joysticks.ContainsKey(id))
                            {
                                var joystick = SDL.OpenJoystick(id);
                                if (joystick != IntPtr.Zero) _joysticks[id] = joystick;
                            }
                            resultEvents.Add(new JoystickEvent() { Type = Event.EventType.JoystickAdded, Joystick = (int)id, SourceHandle = _internalHandle });
                        }
                        break;
                    case SDL.EventType.JoystickRemoved:
                        {
                            uint id = ev.JDevice.Which;
                            if (_joysticks.Remove(id, out var joystick)) SDL.CloseJoystick(joystick);
                            resultEvents.Add(new JoystickEvent() { Type = Event.EventType.JoystickRemoved, Joystick = (int)id, SourceHandle = _internalHandle });
                        }
                        break;
                    case SDL.EventType.JoystickAxisMotion:
                        // 16-bit value from -32768 to 32767 -> -1 to 1
                        resultEvents.Add(new JoystickEvent() { Type = Event.EventType.JoystickAxis, Joystick = (int)ev.JAxis.Which, Index = ev.JAxis.Axis, Value = Math.Max(-1f, ev.JAxis.Value / 32767f), SourceHandle = _internalHandle });
                        break;
                    case SDL.EventType.JoystickButtonDown:
                    case SDL.EventType.JoystickButtonUp:
                        resultEvents.Add(new JoystickEvent()
                        {
                            Type = ((SDL.EventType)ev.Type) == SDL.EventType.JoystickButtonDown ? Event.EventType.JoystickButtonDown : Event.EventType.JoystickButtonUp,
                            Joystick = (int)ev.JButton.Which, Index = ev.JButton.Button, SourceHandle = _internalHandle,
                        });
                        break;
                    case SDL.EventType.JoystickHatMotion:
                        resultEvents.Add(new JoystickEvent() { Type = Event.EventType.JoystickHat, Joystick = (int)ev.JHat.Which, Index = ev.JHat.Hat, Value = ev.JHat.Value, SourceHandle = _internalHandle });
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
            framebuffer.Resolve(); // Palette framebuffer: compute pixels from indices and palette (a no-op in RGBA mode)
            EnsureTexture(framebuffer.Width, framebuffer.Height);

            unsafe
            {
                fixed (uint* src = framebuffer.Pixels)
                {
                    SDL.UpdateTexture(_texture, IntPtr.Zero, (IntPtr)src, framebuffer.Width * sizeof(uint));
                }
            }

            SDL.RenderClear(_renderer);
            // dstrect = NULL -> the ENTIRE current render target (the
            // actual window size), not the original texture size -
            // that IS the automatic scaling.
            SDL.RenderTexture(_renderer, _texture, IntPtr.Zero, IntPtr.Zero);
            SDL.RenderPresent(_renderer);
        }

        private void EnsureTexture(int width, int height)
        {
            if (_texture != IntPtr.Zero && _texWidth == width && _texHeight == height) return;
            if (_texture != IntPtr.Zero) SDL.DestroyTexture(_texture);

            _texture = SDL.CreateTexture(
                _renderer,
                SDL.PixelFormat.ABGR8888, // memory order R,G,B,A (PixelColor.Packed): on little-endian ABGR8888, not ARGB8888 (that swapped red and blue)
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
            foreach (var joystick in _joysticks.Values) SDL.CloseJoystick(joystick);
            _joysticks.Clear();
            if (_texture != IntPtr.Zero) SDL.DestroyTexture(_texture);
            if (_renderer != IntPtr.Zero) SDL.DestroyRenderer(_renderer);
            if (_window != IntPtr.Zero) SDL.DestroyWindow(_window);
            SDL.Quit();
        }
    }
}
