using System;
using System.Runtime.CompilerServices;
using fire.Terminal;
using fire.Terminal.Sdl;

namespace fire.Terminal.Windows
{
    /// <summary>
    /// Standalone console window (see docs/CONSOLE.md) - a THIN
    /// shell that ONLY takes care of rendering an EXTERNALLY supplied framebuffer
    /// (see FramebufferManager) - deliberately owns NO
    /// framebuffer of its own and NO renderer of its own any more (unlike
    /// in an earlier stage): "framebuffer", "console"
    /// (renderer, see RendererManager) and "window" (this class)
    /// are now three resource kinds managed INDEPENDENTLY of one another, each with
    /// its own IDs (see WindowManager) - a window simply shows
    /// WHAT is in a framebuffer, regardless of WHICH console
    /// (if any) is currently drawing into it.
    ///
    /// Two ways of using it:
    /// - <see cref="Tick"/>: ONE cycle (fetch events + draw), for
    ///   a host that runs its OWN loop (e.g. a later
    ///   runtime that also clocks the script VM alongside) - simply call it once on each
    ///   pass of its own loop (see WindowManager.
    ///   TickAll for several windows at once).
    /// </summary>
    public sealed class ConsoleWindow : IDisposable
    {
        public Framebuffer Framebuffer { get; }

        private readonly IFramebufferRenderer _renderer;
        private bool _opened;
        private bool _disposed;

        private int _handle;

        /// <param name="framebuffer">The framebuffer to show - lives on
        /// INDEPENDENTLY of this window (it is neither created here
        /// nor destroyed when this window is disposed).</param>
        /// <param name="renderer">Default: SdlFramebufferRenderer - see
        /// IFramebufferRenderer for ways to swap it.</param>
        public ConsoleWindow(Framebuffer framebuffer, IFramebufferRenderer? renderer = null)
        {
            Framebuffer = framebuffer ?? throw new ArgumentNullException(nameof(framebuffer));
            _renderer = renderer ?? new SdlFramebufferRenderer();
        }

        /// <summary>true: if the user changes the size of the window, the framebuffer gets exactly that size (as long as it is valid, see Framebuffer.IsValidSize) - instead of its content being stretched to
        /// the window. The program receives the event <see cref="Event.EventType.Resize"/> and redraws.</summary>
        public bool AutoResize { get; set; }

        /// <summary>Does a finger on the touchscreen also trigger mouse events? (see IFramebufferRenderer.TouchMouse)</summary>
        public bool TouchMouse
        {
            get => _realized ? _renderer.TouchMouse : _touchMouse;
            set { _touchMouse = value; if (_realized && !_failed) _renderer.TouchMouse = value; }   // (applied when the window opens)
        }
        private bool _touchMouse = true;

        /// <summary>Does every tick wait for the monitor's refresh? (see IFramebufferRenderer.VSync)</summary>
        public bool VSync
        {
            get => _realized ? _renderer.VSync : _vsync;
            set { _vsync = value; if (_realized && !_failed) _renderer.VSync = value; }
        }
        private bool _vsync = true;

        private string _title = "fire Console";

        /// <summary>Gives the window its handle and title. The OS window itself opens on the thread that first pumps it (<see cref="Realize"/>): a window
        /// that a window manager's thread shows (see WindowManager.StartManager) must be created there, because SDL serves a window only on the thread
        /// that created it.</summary>
        public void Open(int handle, string title = "fire Console")
        {
            if (_opened) return;
            _handle = handle;
            _title = title;
            _opened = true;
        }

        private bool _realized;

        /// <summary>Window.SetSize: a window that is not open yet opens with the size of its framebuffer (the program resizes that), an open one gets the new size.</summary>
        public void SetSize(int width, int height)
        {
            if (_realized && !_failed) _renderer.SetSize(width, height);
        }

        /// <summary>Window.Cursor: the system cursor over the window (SDL numbering, see IFramebufferRenderer.SetCursor).</summary>
        public void SetCursor(int cursor)
        {
            if (_realized && !_failed) _renderer.SetCursor(cursor);
        }

        /// <summary>Opens the OS window now, on the calling thread (once).</summary>
        public void Realize()
        {
            if (!_opened || _realized || _disposed) return;
            _realized = true;
            try
            {
                _renderer.VSync = _vsync;
                _renderer.TouchMouse = _touchMouse;
                _renderer.Initialize(_title, Framebuffer.Width, Framebuffer.Height, _handle);
            }
            catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException)
            {
                // no display (no SDL, no video driver): the window counts as closed
                _failed = true;
                FailureMessage = ex.Message;
            }
        }

        private bool _failed;

        /// <summary>Why the window could not be opened (null while it could).</summary>
        public string? FailureMessage { get; private set; }

        /// <summary>A single cycle: fetch window events, then draw the
        /// CURRENT content of <see cref="Framebuffer"/> - no matter
        /// what drew into it in the meantime (a
        /// renderer via RendererManager, raw graphics operations,
        /// writing the pixels directly, ...). Returns false as soon as the
        /// window has been closed - the host then usually ends
        /// its own loop (or removes this window from its
        /// management, see WindowManager).</summary>
        public WindowPumpResult? Tick()
        {
            if (!_opened) return null;
            var result = Pump();
            Present(Framebuffer);
            return result;
        }

        /// <summary>Only the events (opens the window first if needed); with AutoResize a resize gives the framebuffer the new size.</summary>
        public WindowPumpResult? Pump()
        {
            if (!_opened) return null;
            Realize();
            if (_failed) return new WindowPumpResult { StillOpen = false, Events = Array.Empty<Event.IEvent>() };
            var result = _renderer.PumpEvents();
            if (AutoResize && result?.Events != null)
                foreach (var e in result.Events)
                    if (e is Event.ResizeEvent resize && Framebuffer.IsValidSize(resize.Width, resize.Height))
                        Framebuffer.Resize(resize.Width, resize.Height);
            return result;
        }

        /// <summary>Only the events, without touching the framebuffer (the thread of a window manager: the framebuffer belongs to the program).</summary>
        public WindowPumpResult? PumpRaw()
        {
            if (!_opened) return null;
            Realize();
            if (_failed) return new WindowPumpResult { StillOpen = false, Events = Array.Empty<Event.IEvent>() };
            return _renderer.PumpEvents();
        }

        /// <summary>Shows `framebuffer` (this window's own, or the copy that a window manager's thread shows).</summary>
        public void Present(Framebuffer framebuffer)
        {
            if (!_opened) return;
            Realize();
            if (_failed) return;
            framebuffer.Resolve(); // Palette framebuffer: indices -> visible colours (a no-op in RGBA mode)
            _renderer.Present(framebuffer);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_realized && !_failed) _renderer.Dispose();   // a window that never opened has nothing of the display to release
        }
    }
}
