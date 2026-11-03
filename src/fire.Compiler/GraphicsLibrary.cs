using System.Runtime.InteropServices;
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

        /// <summary>Registers the natives of graphics (and, with <paramref name="withWindows"/>, of windows) bound to the C library, in the order of the stubs - to the
        /// library of the process, or to a copy of it (<paramref name="instance"/>: a program whose windows a host drives). Returns the file name the natives are bound to.</summary>
        public static string Register(NativeRegistry natives, bool withWindows, Action<string>? log = null, Instance? instance = null)
        {
            SyncEnvironment();
            var stubs = new NativeRegistry();
            fire.Terminal.Bridge.GraphicsBridge.RegisterStubs(stubs);
            if (withWindows) fire.Windows.Bridge.WindowsBridge.RegisterStubs(stubs);
            var names = stubs.Names.OrderBy(stubs.IndexOf).ToList();
            string file = instance?.File ?? PackageLibrary.GraphicsFileName;
            string? path = instance?.Path ?? Locate(log);
            if (instance == null) PackageHost.GraphicsInC = true;   /* (scene3d's presenter goes to this library) */
            PackageNativeBinding.Register(natives, names, names.Select(_ => file).ToList(),
                f => f == file ? path ?? throw new DllNotFoundException(_failure ?? "the library of graphics and windows is not there") : null);
            return file;
        }

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate int PixelsFn(int id, uint[]? output, int capacity, out int width, out int height);

        /// <summary>The pixels of a framebuffer of the program (R,G,B,A per word), or null when there is no such framebuffer (<paramref name="instance"/>: of the program that
        /// runs on that copy of the library).</summary>
        public static uint[]? Pixels(int framebuffer, out int width, out int height, Instance? instance = null)
        {
            width = height = 0;
            var fn = instance != null ? PackageNativeBinding.Export(instance.File, f => instance.Path, "fire_gfx_host_pixels")
                                      : PackageNativeBinding.Export(PackageLibrary.GraphicsFileName, f => Locate(), "fire_gfx_host_pixels");
            if (fn == IntPtr.Zero) return null;
            var pixels = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<PixelsFn>(fn);
            int n = pixels(framebuffer, null, 0, out width, out height);
            if (n <= 0) return null;
            var result = new uint[n];
            pixels(framebuffer, result, n, out width, out height);
            return result;
        }

        // ---- programs whose windows a host drives: the editor's live view (LiveSession) and design view (UiPreview) ----

        /// <summary>
        /// A copy of the library for one program at a time whose windows the host drives (<see cref="IHostDisplay"/>): the library has one state per process - its framebuffers,
        /// its windows, the display it talks to - so a program in the editor's live view gets a copy of its own (loaded under another name), and a program next to it still
        /// shows its windows on the desktop. The copies are kept for the next program (a pool: <see cref="Acquire"/>, <see cref="Dispose"/>); its end resets the copy.
        /// </summary>
        public sealed class Instance : IDisposable
        {
            [StructLayout(LayoutKind.Sequential)]
            private struct HostBlock { public int Size; public IntPtr Ctx, NextEvent, Present, Cursor, StartDrag, CanStartDrag; }
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NextEventFn(IntPtr ctx, int window, IntPtr output);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PresentFn(IntPtr ctx, int window, IntPtr rgba, int width, int height, int vsync);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CursorFn(IntPtr ctx, int window, int cursor);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DisplayFn(IntPtr host);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StartDragFn(IntPtr ctx, int window, IntPtr text, IntPtr files);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CanStartDragFn(IntPtr ctx, int window);

            private static readonly List<Instance> Pool = new();
            private static bool _swept;

            /// <summary>The name the natives are bound to (PackageNativeBinding) and the file of the copy.</summary>
            public string File { get; }
            public string Path { get; }
            private readonly DateTime _stamp;
            private bool _busy;
            private volatile IHostDisplay? _display;
            private readonly NextEventFn _next;
            private readonly PresentFn _present;
            private readonly CursorFn _cursor;
            private readonly StartDragFn _startDrag;
            private readonly CanStartDragFn _canStartDrag;
            private readonly IntPtr _block, _hostless;
            private int[] _scratch = Array.Empty<int>();
            private IntPtr _data;

            private static IntPtr Utf8(string text)
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text + "\0");
                var block = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, block, bytes.Length);
                return block;
            }

            private Instance(string file, string path, DateTime stamp)
            {
                File = file; Path = path; _stamp = stamp;
                _next = NextEvent; _present = Present; _cursor = Cursor; _startDrag = StartDrag; _canStartDrag = CanStartDrag;
                _block = Marshal.AllocHGlobal(Marshal.SizeOf<HostBlock>());
                Marshal.StructureToPtr(new HostBlock
                {
                    Size = Marshal.SizeOf<HostBlock>(),
                    NextEvent = Marshal.GetFunctionPointerForDelegate(_next),
                    Present = Marshal.GetFunctionPointerForDelegate(_present),
                    Cursor = Marshal.GetFunctionPointerForDelegate(_cursor),
                    StartDrag = Marshal.GetFunctionPointerForDelegate(_startDrag),
                    CanStartDrag = Marshal.GetFunctionPointerForDelegate(_canStartDrag),
                }, _block, false);
                _hostless = Marshal.AllocHGlobal(Marshal.SizeOf<HostBlock>());
                Marshal.StructureToPtr(new HostBlock { Size = Marshal.SizeOf<HostBlock>() }, _hostless, false);
            }

            /// <summary>A copy for one program (null: the library cannot be had - see <see cref="Failure"/>; the caller takes the C# implementation then).</summary>
            public static Instance? Acquire(Action<string>? log = null)
            {
                string? source = Locate(log);
                if (source == null) return null;
                DateTime stamp = System.IO.File.GetLastWriteTimeUtc(source);
                lock (Pool)
                {
                    if (Pool.FirstOrDefault(i => !i._busy && i._stamp == stamp) is { } free) { free._busy = true; return free; }
                    try
                    {
                        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fire-gfx-" + Environment.ProcessId);
                        if (!_swept) { _swept = true; SweepCopies(); }
                        Directory.CreateDirectory(dir);
                        string file = (Pool.Count + 1) + "-" + PackageLibrary.GraphicsFileName;
                        string path = System.IO.Path.Combine(dir, file);
                        System.IO.File.Copy(source, path, overwrite: true);
                        var made = new Instance(file, path, stamp) { _busy = true };
                        Pool.Add(made);
                        return made;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _failure = "the library of graphics and windows could not be copied for the editor (" + ex.Message + ")";
                        return null;
                    }
                }
            }

            /// <summary>The copies of processes that have ended (they could not delete them while their copies were loaded).</summary>
            private static void SweepCopies()
            {
                try
                {
                    foreach (string dir in Directory.GetDirectories(System.IO.Path.GetTempPath(), "fire-gfx-*"))
                    {
                        if (!int.TryParse(System.IO.Path.GetFileName(dir).AsSpan("fire-gfx-".Length), out int pid) || pid == Environment.ProcessId) continue;
                        bool running;
                        try { using var p = System.Diagnostics.Process.GetProcessById(pid); running = !p.HasExited; }
                        catch (ArgumentException) { running = false; }
                        catch (InvalidOperationException) { running = false; }
                        if (!running) try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
                    }
                }
                catch (Exception) { }
            }

            /// <summary>The host drives the program's windows from now on (<paramref name="display"/> null: windows without a screen - no events, the pictures stay in the
            /// framebuffers). Before the program opens one.</summary>
            public void Attach(IHostDisplay? display)
            {
                _display = display;
                var fn = PackageNativeBinding.Export(File, f => Path, "fire_gfx_host_display");
                if (fn == IntPtr.Zero) throw new InvalidOperationException("The library of graphics and windows has no display for a host (fire_gfx_host_display).");
                Marshal.GetDelegateForFunctionPointer<DisplayFn>(fn)(display != null ? _block : _hostless);
            }

            /// <summary>The program has ended (its session reset the copy): the copy is free for the next one.</summary>
            public void Dispose()
            {
                _display = null;
                lock (Pool) _busy = false;
            }

            private int NextEvent(IntPtr ctx, int window, IntPtr output)
            {
                try
                {
                    if (_display is not { } d || !d.NextEvent(window, out var e)) return 0;
                    int[] ints = { e.Type, e.Button, e.Buttons, e.KeyCode, e.Modifiers, e.Width, e.Height };
                    Marshal.Copy(ints, 0, output, ints.Length);
                    float[] floats = { e.X, e.Y, e.Dx, e.Dy };
                    Marshal.Copy(floats, 0, output + 28, floats.Length);
                    byte[] text = new byte[32];
                    if (e.Text is { Length: > 0 } t)
                    {
                        // (UTF-8, at most 31 bytes, cut at a character: longer text comes as several events - LiveSession splits it)
                        int n = 0;
                        foreach (var rune in t.EnumerateRunes())
                        {
                            int len = rune.Utf8SequenceLength;
                            if (n + len > 31) break;
                            rune.EncodeToUtf8(text.AsSpan(n));
                            n += len;
                        }
                    }
                    Marshal.Copy(text, 0, output + 44, 32);
                    Marshal.Copy(new[] { e.Joystick, e.Index }, 0, output + 76, 2);
                    Marshal.Copy(new[] { e.Value, e.Pressure }, 0, output + 84, 2);
                    Marshal.WriteInt64(output + 96, e.Finger);
                    if (_data != IntPtr.Zero) { Marshal.FreeHGlobal(_data); _data = IntPtr.Zero; }   // (the library copied the last one)
                    if (e.Data != null) _data = Utf8(e.Data);
                    Marshal.WriteIntPtr(output + 104, _data);
                    return 1;
                }
                catch (Exception) { return 0; }
            }

            private void Present(IntPtr ctx, int window, IntPtr rgba, int width, int height, int vsync)
            {
                try
                {
                    if (_display is not { } d || rgba == IntPtr.Zero || width <= 0 || height <= 0) return;
                    int n = width * height;
                    if (_scratch.Length < n) _scratch = new int[n];
                    Marshal.Copy(rgba, _scratch, 0, n);
                    d.Present(window, MemoryMarshal.Cast<int, uint>(_scratch.AsSpan(0, n)), width, height, vsync != 0);
                }
                catch (Exception) { }
            }

            private void Cursor(IntPtr ctx, int window, int cursor)
            {
                try { _display?.Cursor(window, cursor); } catch (Exception) { }
            }

            private int StartDrag(IntPtr ctx, int window, IntPtr text, IntPtr files)
            {
                try
                {
                    if (_display is not { } d) return 0;
                    string paths = Marshal.PtrToStringUTF8(files) ?? "";
                    return d.StartDrag(window, Marshal.PtrToStringUTF8(text) ?? "", paths.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (Exception) { return 0; }
            }

            private int CanStartDrag(IntPtr ctx, int window)
            {
                try { return _display?.CanStartDrag(window) == true ? 1 : 0; } catch (Exception) { return 0; }
            }
        }
    }

    /// <summary>The host drives the windows of the program (RuntimeSession.Build): <see cref="Display"/> gets their pictures and gives their events; null: windows
    /// without a screen (the design view: the pictures stay in the framebuffers).</summary>
    public sealed record HostWindows(IHostDisplay? Display);

    /// <summary>One event a host hands to a program's window (<see cref="IHostDisplay.NextEvent"/>): <see cref="Type"/> as EventType (mouse, keys, text, resize, close);
    /// positions in pixels of the window. MouseDown/Up: Button, X, Y; MouseMove: X, Y (the buttons the window keeps itself); MouseScroll: Dx, Dy (notches, Dy &gt; 0: up), X, Y;
    /// KeyDown/Up: KeyCode, Modifiers (SDL's numbers, see UI.Keys); TextInput: Text; Resize: Width, Height; Touch*: Finger, X, Y, Pressure; Joystick*: Joystick, Index, Value;
    /// DropFiles/DropText: Data, X, Y; DragHover: X, Y; DragLeave; Close.</summary>
    public struct HostDisplayEvent
    {
        public int Type, Button, Buttons, KeyCode, Modifiers, Width, Height;
        public float X, Y, Dx, Dy;
        public string? Text;
        /// <summary>TouchDown/Move/Up: the finger and how hard it presses (0 to 1).</summary>
        public long Finger;
        public float Pressure;
        /// <summary>Joystick events: the device, the axis/button/hat and its value.</summary>
        public int Joystick, Index;
        public float Value;
        /// <summary>DropFiles (the paths, one per line) / DropText: what was dropped.</summary>
        public string? Data;
    }

    /// <summary>A host that drives the windows of a program running on the C library (<see cref="GraphicsLibrary.Instance"/>): the editor's live view. Called on the thread of
    /// the window (the windows' manager), not the host's.</summary>
    public interface IHostDisplay
    {
        /// <summary>The next event for window <paramref name="window"/> (1, 2, ...: in the order the program opened them); false: none now.</summary>
        bool NextEvent(int window, out HostDisplayEvent e);
        /// <summary>A picture of the window (R,G,B,A per word, row by row; only valid during the call). With <paramref name="vsync"/>, the host waits like the vertical blank.</summary>
        void Present(int window, ReadOnlySpan<uint> rgba, int width, int height, bool vsync);
        /// <summary>The mouse pointer the program wants over the window (SDL numbering, see UI.Cursor).</summary>
        void Cursor(int window, int cursor);
        /// <summary>Window.StartDrag: the program drags text and/or files out of the window - 0 nothing happened, 1 copied, 2 moved.</summary>
        int StartDrag(int window, string text, string[] files) => 0;
        /// <summary>Window.CanStartDrag: does the host take drags out of the window?</summary>
        bool CanStartDrag(int window) => false;
    }
}
