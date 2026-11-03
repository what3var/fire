using System.Runtime.InteropServices;
using System.Text;
using fire.IO.Bridge;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// What the host (editor, runtime, an embedding program) decides for the natives of packages that run in a shared library (native/abi/fire_pkg_abi.h, `fire_host`): which paths a script may touch
    /// (<see cref="IoPolicy"/>) and where the console goes (<see cref="IoStdio"/>). The library calls these functions back; they belong to the session that is running (<see cref="Begin"/>).
    /// When the session ends, the libraries are told to forget what the program left behind (`fire_pkg_reset`: e.g. open streams).
    /// </summary>
    public static partial class PackageHost
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FireHost
        {
            public int Size;
            public IntPtr IoAllow, StdRead, StdWrite, StdFlush;
            // the devices of the host (see PackageHost.Devices.cs)
            public IntPtr DevRefresh, DevCount, DevHandleAt, DevIdentifier, DevDefault, DevManagerShared, DevShared, DevAvailability, DevTestAvailability, DevConnected, DevPortName, DevConnect,
                DevDisconnect, DevWrite, DevSendCommand, DevPoll;
            // the network (see NetPolicy)
            public IntPtr NetAllow;
            // the windows (a package that draws into them itself: scene3d DirectDrawing)
            public IntPtr WinAttach, WinDetach;
            // lambdas the library calls back, its services at the VM's safe points, the holds on the end of the program
            public IntPtr CallLambda, RequestService, Hold;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IoAllowFn(IntPtr pathUtf8, int access, IntPtr reason, int reasonSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NetAllowFn(IntPtr hostUtf8, int port, int access, IntPtr reason, int reasonSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StdReadFn(int stream, IntPtr buffer, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StdWriteFn(int stream, IntPtr buffer, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StdFlushFn(int stream);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WinAttachFn(int framebuffer, IntPtr present, IntPtr ctx);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WinDetachFn(int framebuffer, IntPtr ctx);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CallLambdaFn(long handle, IntPtr args, int argc);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RequestServiceFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void HoldFn(int delta);

        private static readonly object Lock = new();
        private static IoPolicy _policy = IoPolicy.AllowAll;
        private static NetPolicy _netPolicy = NetPolicy.AllowAll;
        private static IoStdio _stdio = IoStdio.SystemConsole;
        private static readonly Stream?[] StdStreams = new Stream?[3];
        private static IntPtr _block;
        // the delegates stay referenced: the native side holds their function pointers
        private static IoAllowFn? _ioAllow;
        private static NetAllowFn? _netAllow;
        private static StdReadFn? _stdRead;
        private static StdWriteFn? _stdWrite;
        private static StdFlushFn? _stdFlush;
        private static WinAttachFn? _winAttach;
        private static WinDetachFn? _winDetach;
        private static CallLambdaFn? _callLambda;
        private static RequestServiceFn? _requestService;
        private static HoldFn? _hold;

        /// <summary>The `fire_host` structure that is handed to a library (`fire_pkg_set_host`).</summary>
        internal static IntPtr Block
        {
            get
            {
                lock (Lock)
                {
                    if (_block != IntPtr.Zero) return _block;
                    _ioAllow = IoAllow; _netAllow = NetAllow; _stdRead = StdRead; _stdWrite = StdWrite; _stdFlush = StdFlush; _winAttach = WinAttach; _winDetach = WinDetach;
                    _callLambda = CallLambda; _requestService = RequestService; _hold = delta => FireRuntime.AddHostHold(delta);
                    var host = new FireHost
                    {
                        Size = Marshal.SizeOf<FireHost>(),
                        IoAllow = Marshal.GetFunctionPointerForDelegate(_ioAllow),
                        NetAllow = Marshal.GetFunctionPointerForDelegate(_netAllow),
                        StdRead = Marshal.GetFunctionPointerForDelegate(_stdRead),
                        StdWrite = Marshal.GetFunctionPointerForDelegate(_stdWrite),
                        StdFlush = Marshal.GetFunctionPointerForDelegate(_stdFlush),
                        WinAttach = Marshal.GetFunctionPointerForDelegate(_winAttach),
                        WinDetach = Marshal.GetFunctionPointerForDelegate(_winDetach),
                        CallLambda = Marshal.GetFunctionPointerForDelegate(_callLambda),
                        RequestService = Marshal.GetFunctionPointerForDelegate(_requestService),
                        Hold = Marshal.GetFunctionPointerForDelegate(_hold),
                    };

                    _block = Marshal.AllocHGlobal(Marshal.SizeOf<FireHost>());
                    Marshal.StructureToPtr(host, _block, false);
                    return _block;
                }
            }
        }

        // ---- lambdas that cross to a library (FIRE_LAMBDA): a handle each, with the VM that gave it; they stay until the program ends (Begin of the next session) ----
        private static readonly List<(LambdaValue Lambda, VM? Owner)> Lambdas = new();
        private static VM? _mainVm;

        /// <summary>A call from a VM into a library: the main program's VM (not a fire thread's) is the one the library's services and lambdas from other threads go to.</summary>
        internal static void NoteCaller()
        {
            var vm = VM.CurrentThreadVm;
            if (vm != null && !vm.IsFireThreadVm) _mainVm = vm;
        }

        /// <summary>The handle of a lambda for a library.</summary>
        internal static long RegisterLambda(LambdaValue lambda)
        {
            var vm = VM.CurrentThreadVm;
            lock (Lambdas)
            {
                Lambdas.Add((lambda, vm != null && !vm.IsFireThreadVm ? vm : _mainVm ?? vm));
                return Lambdas.Count;   // (1, 2, ...: 0 is no lambda)
            }
        }

        /// <summary>`call_lambda`: on the VM's own thread the lambda runs nested at once (with the real globals); from another thread it is queued to its VM, which runs it at a
        /// safe point. An exception in it is reported, it does not go back into the library.</summary>
        private static int CallLambda(long handle, IntPtr args, int argc)
        {
            (LambdaValue Lambda, VM? Owner) entry;
            lock (Lambdas)
            {
                if (handle < 1 || handle > Lambdas.Count) return 1;
                entry = Lambdas[(int)handle - 1];
            }
            var values = new Value[argc];
            for (int i = 0; i < argc; i++) values[i] = PackageNativeBinding.FromNative(args + i * PackageNativeBinding.ValSize);
            var vm = VM.CurrentThreadVm;
            void Report(string message) => Console.WriteLine($"(unbehandelte Exception im Callback: {message})");
            if (vm != null && !vm.IsHalted)
            {
                try
                {
                    var error = vm.CallLambdaInline(entry.Lambda, values);
                    if (error != null) { Report(new UncaughtScriptException(error).Message); return 1; }
                }
                catch (Exception ex) { Report(ex.Message); return 1; }
                return 0;
            }
            var owner = entry.Owner ?? _mainVm;
            return owner != null && owner.PostCallback(entry.Lambda, values, Report) ? 0 : 1;
        }

        /// <summary>`request_service`: the libraries' services run at the next safe point of the main program (or right away when nobody runs: no program).</summary>
        private static void RequestService()
        {
            var owner = _mainVm;
            if (owner == null || !owner.PostHostAction(PackageNativeBinding.ServiceLibraries)) { }
        }

        /// <summary>A session starts: the policy and the console of the host for the natives of packages (null: everything is allowed, the real console). Dispose at its end.</summary>
        public static IDisposable Begin(IoPolicy? policy, IoStdio? stdio, bool usesDevices = false, object? deviceManager = null, NetPolicy? netPolicy = null)
        {
            IoPolicy previousPolicy;
            IoStdio previousStdio;
            NetPolicy previousNetPolicy;
            lock (Lock)
            {
                previousPolicy = _policy;
                previousStdio = _stdio;
                previousNetPolicy = _netPolicy;
                _netPolicy = netPolicy ?? NetPolicy.AllowAll;
                _policy = policy ?? IoPolicy.AllowAll;
                _stdio = stdio ?? IoStdio.SystemConsole;
                Array.Clear(StdStreams);
            }
            lock (Lambdas) { Lambdas.Clear(); _mainVm = null; }
            // the devices of the host are only touched (and their assembly loaded) by a program that imports the devices package
            object? previousDevices = usesDevices ? DeviceHost.Begin(deviceManager, Block) : null;
            return new Scope(previousPolicy, previousStdio, previousDevices, previousNetPolicy);
        }

        private sealed class Scope : IDisposable
        {
            private readonly IoPolicy _previousPolicy;
            private readonly IoStdio _previousStdio;
            private readonly object? _previousDevices;
            private readonly NetPolicy _previousNetPolicy;
            private bool _done;

            public Scope(IoPolicy policy, IoStdio stdio, object? previousDevices, NetPolicy netPolicy) { _previousPolicy = policy; _previousStdio = stdio; _previousDevices = previousDevices; _previousNetPolicy = netPolicy; }

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                PackageNativeBinding.ResetLibraries();   // what the program left open is closed
                if (_previousDevices != null) DeviceHost.End(_previousDevices);
                lock (Lock)
                {
                    _policy = _previousPolicy;
                    _netPolicy = _previousNetPolicy;
                    _stdio = _previousStdio;
                    Array.Clear(StdStreams);
                }
            }
        }

        /// <summary>`win_attach`: the windows that show the framebuffer call the package's presenter from now on (1), 0: there is no such framebuffer.</summary>
        // (the framebuffers of a program that runs graphics and windows in C: the library of both - PackageLibrary.GraphicsFileName, loaded by the program's natives)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GfxAttachFn(int framebuffer, IntPtr present, IntPtr ctx);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GfxDetachFn(int framebuffer, IntPtr ctx);
        /// <summary>Does the program run graphics in C (its library is loaded)? Then the presenter goes to it.</summary>
        public static bool GraphicsInC { get; set; }
        private static string GraphicsFile => (OperatingSystem.IsWindows() ? "" : "lib") + "fire_gfx" + (OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so");

        private static int WinAttach(int framebuffer, IntPtr present, IntPtr ctx)
        {
            if (GraphicsInC)
            {
                var fn = PackageNativeBinding.Export(GraphicsFile, null, "fire_gfx_host_attach");
                return fn != IntPtr.Zero && present != IntPtr.Zero ? Marshal.GetDelegateForFunctionPointer<GfxAttachFn>(fn)(framebuffer, present, ctx) : 0;
            }
            try
            {
                var fb = fire.Terminal.Bridge.GraphicsBridge.CurrentFramebuffers?.GetFramebuffer(framebuffer);
                if (fb == null || present == IntPtr.Zero) return 0;
                fb.Presenter = new fire.Terminal.DirectPresenter(present, ctx);
                return 1;
            }
            catch (Exception) { return 0; }
        }

        /// <summary>`win_detach`: the presenter of `ctx` is taken away again (the windows show the framebuffer).</summary>
        private static void WinDetach(int framebuffer, IntPtr ctx)
        {
            if (GraphicsInC)
            {
                var fn = PackageNativeBinding.Export(GraphicsFile, null, "fire_gfx_host_detach");
                if (fn != IntPtr.Zero) Marshal.GetDelegateForFunctionPointer<GfxDetachFn>(fn)(framebuffer, ctx);
                return;
            }
            try
            {
                var fb = fire.Terminal.Bridge.GraphicsBridge.CurrentFramebuffers?.GetFramebuffer(framebuffer);
                if (fb?.Presenter is { } p && p.Context == ctx) fb.Presenter = null;
            }
            catch (Exception) { }
        }

        private static Stream StdStream(int kind)
        {
            lock (Lock)
            {
                return StdStreams[kind] ??= kind == 0 ? _stdio.OpenInput() : kind == 1 ? _stdio.OpenOutput() : _stdio.OpenError();
            }
        }

        private static int IoAllow(IntPtr pathUtf8, int access, IntPtr reason, int reasonSize)
        {
            try
            {
                string path = Marshal.PtrToStringUTF8(pathUtf8) ?? "";
                IoPolicy policy;
                lock (Lock) policy = _policy;
                if (policy.IsAllowed(path, (IoAccess)access, out string? why)) return 1;
                if (reason != IntPtr.Zero && reasonSize > 0)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(why ?? "");
                    int n = Math.Min(bytes.Length, reasonSize - 1);
                    Marshal.Copy(bytes, 0, reason, n);
                    Marshal.WriteByte(reason, n, 0);
                }
                return 0;
            }
            catch (Exception) { return 0; }
        }

        private static int NetAllow(IntPtr hostUtf8, int port, int access, IntPtr reason, int reasonSize)
        {
            try
            {
                string host = Marshal.PtrToStringUTF8(hostUtf8) ?? "";
                NetPolicy policy;
                lock (Lock) policy = _netPolicy;
                if (policy.IsAllowed(host, port, (NetAccess)access, out string? why)) return 1;
                if (reason != IntPtr.Zero && reasonSize > 0)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(why ?? "");
                    int n = Math.Min(bytes.Length, reasonSize - 1);
                    Marshal.Copy(bytes, 0, reason, n);
                    Marshal.WriteByte(reason, n, 0);
                }
                return 0;
            }
            catch (Exception) { return 0; }
        }

        private static int StdRead(int stream, IntPtr buffer, int count)
        {
            try
            {
                if (stream != 0 || count <= 0) return -1;
                var bytes = new byte[count];
                int n = StdStream(0).Read(bytes, 0, count);
                if (n > 0) Marshal.Copy(bytes, 0, buffer, n);
                return n;
            }
            catch (Exception) { return -1; }
        }

        private static int StdWrite(int stream, IntPtr buffer, int count)
        {
            try
            {
                if (stream is not (1 or 2) || count < 0) return -1;
                var bytes = new byte[count];
                if (count > 0) Marshal.Copy(buffer, bytes, 0, count);
                StdStream(stream).Write(bytes, 0, count);
                return count;
            }
            catch (Exception) { return -1; }
        }

        private static int StdFlush(int stream)
        {
            try
            {
                if (stream is not (1 or 2)) return -1;
                StdStream(stream).Flush();
                return 0;
            }
            catch (Exception) { return -1; }
        }
    }
}
