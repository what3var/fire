using System.Runtime.InteropServices;
using fire.Bytecode;
using fire.Package.Manager;
using fire.Runtime;

namespace fire.Compiler
{
    // The C library of graphics and windows loaded into this process for the C# VM (fire.Runtime: RuntimeSession.Build, the UI checks of the tests) - it goes
    // with fire.Runtime (docs/ROADMAP_C_PORT.md, M1). `fire` loads the library itself; forge only builds it (Locate).
    public static partial class GraphicsLibrary
    {
        private static int _warned;

        /// <summary>The VM takes the C# implementation because the library cannot be had: said to the host's log (ToolchainProvider.Log), else once per process on the error output, with the reason - a failed
        /// build must not hide behind it (the C# windows have no display).</summary>
        public static void WarnFallback()
        {
            string text = "(graphics and windows run in their C# implementation - without windows of the desktop: the C library cannot be had: " + (_failure ?? "unknown") + ")";
            // (the host's log every time - spark's build log, forge's error output; without one the console, once per process)
            if (fire.Native.ToolchainProvider.Log is { } log) { log(text); return; }
            if (Interlocked.Exchange(ref _warned, 1) != 0) return;
            Console.Error.WriteLine(text);
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "setenv")] private static extern int SetEnvC(string name, string value, int overwrite);
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "unsetenv")] private static extern int UnsetEnvC(string name);
        [System.Runtime.InteropServices.DllImport("msvcrt", EntryPoint = "_wputenv_s", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int PutEnvW(string name, string value);

        /// <summary>What the library reads of the environment (the display it talks to, the look of the desktop, the folders of the fonts): the host may have changed it for this
        /// program (Environment.SetEnvironmentVariable changes .NET's copy only) - the C runtime of the process gets the same values.</summary>
        private static void SyncEnvironment()
        {
            var names = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(n => n.StartsWith("FIRE_", StringComparison.Ordinal))
                .Concat(new[] { "FIRE_DISPLAY", "FIRE_SYSTEM_THEME", "FIRE_FONT_DIRS", "FIRE_GFX_GPU", "DISPLAY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR" }).Distinct();
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
            var names = BridgeNatives.Of("graphics").Concat(withWindows ? BridgeNatives.Of("windows") : Array.Empty<string>()).ToList();
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
}
