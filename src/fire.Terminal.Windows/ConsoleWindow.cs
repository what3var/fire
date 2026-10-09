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
            get => _renderer.TouchMouse;
            set => _renderer.TouchMouse = value;
        }

        /// <summary>Does every tick wait for the monitor's refresh? (see IFramebufferRenderer.VSync)</summary>
        public bool VSync
        {
            get => _renderer.VSync;
            set => _renderer.VSync = value;
        }

        public void Open(int handle, string title = "fire Console")
        {
            if (_opened) return;
            _handle = handle;
            _renderer.Initialize(title, Framebuffer.Width, Framebuffer.Height, _handle);
            _opened = true;
        }

        /// <summary>A single cycle: fetch window events, then draw the
        /// CURRENT content of <see cref="Framebuffer"/> - no matter
        /// what drew into it in the meantime (a
        /// renderer via RendererManager, raw graphics operations,
        /// writing the pixels directly, ...). Opens the window
        /// automatically if needed (with the default title) if Open() has not
        /// been called explicitly. Returns false as soon as the
        /// window has been closed - the host then usually ends
        /// its own loop (or removes this window from its
        /// management, see WindowManager).</summary>
        public WindowPumpResult? Tick()
        {
            if (!_opened) return null;
            var result = _renderer.PumpEvents();
            if (AutoResize && result?.Events != null)
                foreach (var e in result.Events)
                    if (e is Event.ResizeEvent resize && Framebuffer.IsValidSize(resize.Width, resize.Height))
                        Framebuffer.Resize(resize.Width, resize.Height);
            Framebuffer.Resolve(); // Palette framebuffer: indices -> visible colours (a no-op in RGBA mode)
            _renderer.Present(Framebuffer);
            return result;
        }

        /// <summary>Blocking standalone loop - calls Tick() until the
        /// window is closed. For a host with its own loop
        /// (the typical later use case, see the class documentation) do NOT
        /// call it, integrate Tick() yourself instead.</summary>

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _renderer.Dispose();
        }
    }
}
