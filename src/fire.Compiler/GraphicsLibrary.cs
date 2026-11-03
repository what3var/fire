using fire.Bytecode;
using fire.Package.Manager;
using fire.Runtime;

namespace fire.Compiler
{
    /// <summary>
    /// `graphics` and `windows` in the virtual machine through their C implementation (docs/UI_PLATFORM.md, part 2): the same bridges and the same display as a native build,
    /// as one shared library (PackageLibrary.EnsureGraphics) whose natives are bound like those of a package (PackageNativeBinding). The names are registered in the order of the
    /// compiler's stubs (GraphicsBridge, WindowsBridge: the calls address natives by index).
    /// </summary>
    public static class GraphicsLibrary
    {
        /// <summary>Does the VM take the C implementation? FIRE_VM_GRAPHICS=cs keeps the C# one (fire.Terminal), =c asks for C; the default is C - except where the host
        /// renders the windows itself (the editor's previews: `hostRenders`), which still needs the C# one.</summary>
        public static bool Wanted(bool hostRenders)
        {
            string? choice = Environment.GetEnvironmentVariable("FIRE_VM_GRAPHICS");
            if (choice == "cs") return false;
            if (choice == "c") return true;
            return !hostRenders;
        }

        private static string? _path;
        private static string? _failure;

        /// <summary>The library on this machine, built now if needed (null: it cannot be built - <see cref="Failure"/> says why).</summary>
        public static string? Locate(Action<string>? log = null)
        {
            if (_path != null && File.Exists(_path)) return _path;
            var graphics = PackageStore.Default.FindImport("graphics");
            var windows = PackageStore.Default.FindImport("windows");
            if (graphics == null || windows == null) { _failure = "the standard packages graphics and windows are not installed"; return null; }
            try { _path = PackageLibrary.EnsureGraphics(graphics, windows, log ?? (m => (fire.Native.ToolchainProvider.Log ?? Console.Error.WriteLine)(m))); _failure = null; }
            catch (PackageException ex) { _failure = ex.Message; _path = null; }
            return _path;
        }

        /// <summary>Why the library cannot be had (null: it can).</summary>
        public static string? Failure => _failure;

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "setenv")] private static extern int SetEnvC(string name, string value, int overwrite);
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "unsetenv")] private static extern int UnsetEnvC(string name);
        [System.Runtime.InteropServices.DllImport("msvcrt", EntryPoint = "_wputenv_s", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int PutEnvW(string name, string value);

        /// <summary>What the library reads of the environment (the display it talks to, the look of the desktop, the folders of the fonts): the host may have changed it for this
        /// program (Environment.SetEnvironmentVariable changes .NET's copy only) - the C runtime of the process gets the same values.</summary>
        private static void SyncEnvironment()
        {
            var names = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(n => n.StartsWith("FIRE_", StringComparison.Ordinal))
                .Concat(new[] { "FIRE_DISPLAY", "FIRE_SYSTEM_THEME", "FIRE_FONT_DIRS", "SDL_VIDEODRIVER", "DISPLAY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR" }).Distinct();
            foreach (string name in names)
            {
                string? value = Environment.GetEnvironmentVariable(name);
                try
                {
                    if (OperatingSystem.IsWindows()) PutEnvW(name, value ?? "");
                    else if (value != null) SetEnvC(name, value, 1);
                    else UnsetEnvC(name);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return; }
            }
        }

        /// <summary>Registers the natives of graphics (and, with <paramref name="withWindows"/>, of windows) bound to the C library, in the order of the stubs.</summary>
        public static void Register(NativeRegistry natives, bool withWindows, Action<string>? log = null)
        {
            SyncEnvironment();
            var stubs = new NativeRegistry();
            fire.Terminal.Bridge.GraphicsBridge.RegisterStubs(stubs);
            if (withWindows) fire.Windows.Bridge.WindowsBridge.RegisterStubs(stubs);
            var names = stubs.Names.OrderBy(stubs.IndexOf).ToList();
            string file = PackageLibrary.GraphicsFileName;
            string? path = Locate(log);
            PackageHost.GraphicsInC = true;   /* (scene3d's presenter goes to this library) */
            PackageNativeBinding.Register(natives, names, names.Select(_ => file).ToList(),
                f => f == file ? path ?? throw new DllNotFoundException(_failure ?? "the library of graphics and windows is not there") : null);
        }

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate int PixelsFn(int id, uint[]? output, int capacity, out int width, out int height);

        /// <summary>The pixels of a framebuffer of the program (R,G,B,A per word), or null when there is no such framebuffer.</summary>
        public static uint[]? Pixels(int framebuffer, out int width, out int height)
        {
            width = height = 0;
            var fn = PackageNativeBinding.Export(PackageLibrary.GraphicsFileName, f => Locate(), "fire_gfx_host_pixels");
            if (fn == IntPtr.Zero) return null;
            var pixels = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<PixelsFn>(fn);
            int n = pixels(framebuffer, null, 0, out width, out height);
            if (n <= 0) return null;
            var result = new uint[n];
            pixels(framebuffer, result, n, out width, out height);
            return result;
        }
    }
}
