using System;
using System.Runtime.InteropServices;

namespace fire.Terminal
{
    /// <summary>The native handles of a window, for a package that draws into it itself (native/abi/fire_pkg_abi.h: `fire_native_window`).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NativeWindow
    {
        public const int None = 0, Win32 = 1, X11 = 2, Wayland = 3, Metal = 4;
        /// <summary>Win32: A = HINSTANCE, B = HWND; X11: A = Display*, Id = Window; Wayland: A = wl_display*, B = wl_surface*; Metal: B = CAMetalLayer*.</summary>
        public int Kind;
        public int Width, Height;
        public int VSync;
        public IntPtr A, B;
        public ulong Id;
    }

    /// <summary>
    /// A package that shows the pictures of a framebuffer's windows itself (the scene3d package: a camera's pictures straight from the GPU, docs/SCENE3D.md "DirectDrawing"): the
    /// presenter of the package library (`fire_present_fn` of native/abi/fire_pkg_abi.h) and its context. A window that shows the framebuffer calls it, on its own thread, instead of
    /// showing the framebuffer: op 1 shows the package's picture with the framebuffer's pixels laid over it where their alpha is not 0; op 0: let go of the window. It returns 1 when it
    /// showed the picture, 0 when it cannot (the window then shows the framebuffer as before, and does not ask this presenter again).
    /// </summary>
    public sealed class DirectPresenter
    {
        public IntPtr Function { get; }
        public IntPtr Context { get; }

        public DirectPresenter(IntPtr function, IntPtr context) { Function = function; Context = context; }
    }
}
