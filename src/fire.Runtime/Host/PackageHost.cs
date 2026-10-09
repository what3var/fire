using System.Runtime.InteropServices;
using System.Text;
using fire.IO.Bridge;

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
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IoAllowFn(IntPtr pathUtf8, int access, IntPtr reason, int reasonSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NetAllowFn(IntPtr hostUtf8, int port, int access, IntPtr reason, int reasonSize);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StdReadFn(int stream, IntPtr buffer, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StdWriteFn(int stream, IntPtr buffer, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StdFlushFn(int stream);

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

        /// <summary>The `fire_host` structure that is handed to a library (`fire_pkg_set_host`).</summary>
        internal static IntPtr Block
        {
            get
            {
                lock (Lock)
                {
                    if (_block != IntPtr.Zero) return _block;
                    _ioAllow = IoAllow; _netAllow = NetAllow; _stdRead = StdRead; _stdWrite = StdWrite; _stdFlush = StdFlush;
                    var host = new FireHost
                    {
                        Size = Marshal.SizeOf<FireHost>(),
                        IoAllow = Marshal.GetFunctionPointerForDelegate(_ioAllow),
                        NetAllow = Marshal.GetFunctionPointerForDelegate(_netAllow),
                        StdRead = Marshal.GetFunctionPointerForDelegate(_stdRead),
                        StdWrite = Marshal.GetFunctionPointerForDelegate(_stdWrite),
                        StdFlush = Marshal.GetFunctionPointerForDelegate(_stdFlush),
                    };

                    _block = Marshal.AllocHGlobal(Marshal.SizeOf<FireHost>());
                    Marshal.StructureToPtr(host, _block, false);
                    return _block;
                }
            }
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
