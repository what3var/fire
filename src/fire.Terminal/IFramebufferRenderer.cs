using System;

namespace fire.Terminal
{
    /// <summary>Abstraction over the actual drawing of a
    /// framebuffer in a window - SDL (see Sdl.SdlFramebufferRenderer)
    /// is only ONE possible implementation. ConsoleWindow only knows this
    /// interface, never SDL directly - the rendering backend can thus
    /// be exchanged later without touching Framebuffer/Renderer/
    /// ConsoleWindow themselves (see the SPEC/CONSOLE.md note "so that
    /// I can exchange the rendering").</summary>
    public interface IFramebufferRenderer : IDisposable
    {
        /// <summary>Opens the actual window. `initialWidth`/Height
        /// are pixel sizes (typically the size of the initial
        /// framebuffer) - the window may afterwards be freely resized
        /// by the user, the displayed framebuffer content is
        /// then simply stretched/squeezed (see the Present documentation), not
        /// rasterised again.</summary>
        void Initialize(string title, int initialWidth, int initialHeight, int internalHandle);

        /// <summary>Processes all pending window events (resize,
        /// close, ...). Returns false as soon as the window is to
        /// be closed (the caller then usually ends its
        /// loop) - does NOT present itself, that is a separate call.</summary>
        WindowPumpResult PumpEvents();

        /// <summary>Draws the complete current content of
        /// `framebuffer` - ALWAYS stretched to the full current window
        /// size (see the Initialize documentation), regardless of whether it matches the
        /// framebuffer size. A framebuffer of a different size than at
        /// the last call is supported automatically (no new
        /// Initialize needed).</summary>
        void Present(Framebuffer framebuffer);

        /// <summary>Does <see cref="Present"/> wait for the monitor's refresh (vertical synchronisation)? Default: yes.
        /// Every cycle (ConsoleWindow.Tick) then takes up to one frame period (60 Hz: 16.7 ms) - evenly and without
        /// CPU load, for animations and wait loops, but 256 ticks in a drawing loop take over four seconds,
        /// even if the drawing itself takes only milliseconds. Without VSync, Present returns immediately.
        /// May be set before the window is opened.</summary>
        bool VSync { get; set; }

        /// <summary>true (default): a finger on the touchscreen ALSO triggers mouse events (as SDL does by itself) - a program that only listens to the mouse can then also be operated with
        /// a finger. false: only the touch events (the UI library switches this off and evaluates the fingers itself). May be set before the window is opened;
        /// a renderer without a touchscreen ignores it.</summary>
        bool TouchMouse { get => true; set { } }
    }
}
