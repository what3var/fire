using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using fire.Device.Manager.DeviceManager;
using fire.Device.Manager.Drivers;

namespace fire.Runtime
{
    /// <summary>The devices of the host for the natives of the devices package (`dev_*` of `fire_host`, native/abi/fire_pkg_abi.h): the device manager of the host - the editor's shared one with its
    /// drivers, sharing and packet trace, or a manager of its own for a program run without an editor - answers the library. What a device receives (on the thread of its driver) is collected
    /// in a queue per device that the library takes packets from; the hooks are removed when the program ends.</summary>
    internal static class DeviceHost
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DevIntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DevIntIntFn(int a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DevVoidIntFn(int a);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DevTextFn(int handle, IntPtr buffer, int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DevBytesFn(int handle, IntPtr data, int count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DevCommandFn(int handle, IntPtr textUtf8);

        private sealed class DeviceState
        {
            public DeviceManager? Manager;
            public bool Owned;
            public readonly ConcurrentDictionary<int, ConcurrentQueue<byte[]>> Queues = new();
            public readonly ConcurrentBag<(IDevice Device, Action<byte[]> Handler)> Hooks = new();
        }

        private static DeviceState _devices = new();
        private static bool _installed;
        private static readonly List<Delegate> DeviceDelegates = new();   // referenced: the native side holds their function pointers

        /// <summary>The manager of the program run: the host's (null: one with the built-in drivers, made when the program first asks). Hands the library the callbacks (once). The result is
        /// what <see cref="End"/> needs. Kept apart from PackageHost so that the assembly of the device manager is only loaded by a program that uses devices.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static object Begin(object? manager, IntPtr hostBlock)
        {
            lock (DeviceDelegates)
            {
                if (!_installed)
                {
                    var host = Marshal.PtrToStructure<PackageHost.FireHost>(hostBlock);
                    FillDevices(ref host);
                    Marshal.StructureToPtr(host, hostBlock, false);
                    _installed = true;
                }
            }
            var previous = _devices;
            _devices = new DeviceState { Manager = (DeviceManager?)manager };
            return previous;
        }

        /// <summary>The program has ended: the receive hooks are removed (a shared manager outlives the program, the hooks must not) and the manager is disposed (a shared one ignores that).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void End(object previous)
        {
            var ended = _devices;
            foreach (var (device, handler) in ended.Hooks) device.OnRawDataReceived -= handler;
            ended.Manager?.Dispose();
            _devices = (DeviceState)previous;
        }

        private static DeviceManager Manager()
        {
            var state = _devices;
            lock (state)
            {
                if (state.Manager == null) { state.Manager = DeviceManager.CreateDefault(); state.Owned = true; }
                return state.Manager;
            }
        }

        private static IntPtr Pointer<T>(T callback) where T : Delegate
        {
            DeviceDelegates.Add(callback);
            return Marshal.GetFunctionPointerForDelegate(callback);
        }

        private static IDevice? Device(int handle) => Manager().GetDeviceByHandle(handle);

        /// <summary>The device of a handle, with its receive hook in place (once): data arrives on the thread of the driver and waits in the queue until the library takes it.</summary>
        private static ConcurrentQueue<byte[]>? QueueOf(int handle)
        {
            var state = _devices;
            var device = Device(handle);
            if (device == null) return null;
            if (state.Queues.TryGetValue(handle, out var existing)) return existing;
            var queue = new ConcurrentQueue<byte[]>();
            if (!state.Queues.TryAdd(handle, queue)) return state.Queues[handle];
            Action<byte[]> handler = bytes => { if (bytes.Length > 0) queue.Enqueue(bytes); };
            device.OnRawDataReceived += handler;
            state.Hooks.Add((device, handler));
            return queue;
        }

        private static int CopyText(string? text, IntPtr buffer, int size)
        {
            if (text == null) return -1;
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            int n = Math.Min(bytes.Length, Math.Max(0, size - 1));
            if (buffer != IntPtr.Zero && size > 0) { Marshal.Copy(bytes, 0, buffer, n); Marshal.WriteByte(buffer, n, 0); }
            return bytes.Length;
        }

        private static int Guard(Func<int> action, int failed = 0)
        {
            try { return action(); }
            catch (Exception) { return failed; }
        }

        private static void FillDevices(ref PackageHost.FireHost host)
        {
            host.DevRefresh = Pointer<DevIntIntFn>(fast => Guard(() => { Manager().RefreshDevices(fast != 0); return Manager().DeviceCount; }));
            host.DevCount = Pointer<DevIntFn>(() => Guard(() => Manager().DeviceCount));
            host.DevHandleAt = Pointer<DevIntIntFn>(index => Guard(() => { var handles = Manager().GetAllDeviceHandles(); return index >= 0 && index < handles.Count ? handles[index] : -1; }, -1));
            host.DevIdentifier = Pointer<DevTextFn>((handle, buffer, size) => Guard(() => CopyText(Manager().GetIdentifierByHandle(handle), buffer, size), -1));
            host.DevDefault = Pointer<DevIntFn>(() => Guard(() => Manager().DefaultHandle ?? -1, -1));
            host.DevManagerShared = Pointer<DevIntFn>(() => Guard(() => Manager().IsShared ? 1 : 0));
            host.DevShared = Pointer<DevIntIntFn>(handle => Guard(() => Manager().GetSlotByIdentifier(Manager().GetIdentifierByHandle(handle) ?? "")?.IsShared == true ? 1 : 0));
            host.DevAvailability = Pointer<DevIntIntFn>(handle => Guard(() => (int)(Device(handle)?.Availability ?? DeviceAvailability.Unavailable)));
            host.DevTestAvailability = Pointer<DevIntIntFn>(handle => Guard(() => (int)(Device(handle)?.TestAvailability() ?? DeviceAvailability.Unavailable)));
            host.DevConnected = Pointer<DevIntIntFn>(handle => Guard(() => Device(handle)?.IsConnected == true ? 1 : 0));
            host.DevPortName = Pointer<DevTextFn>((handle, buffer, size) => Guard(() => CopyText(Device(handle)?.PortName ?? "", buffer, size), -1));
            host.DevConnect = Pointer<DevIntIntFn>(handle => Guard(() => { var device = Device(handle); if (device == null) return 0; QueueOf(handle); device.Connect(); return 1; }));
            host.DevDisconnect = Pointer<DevVoidIntFn>(handle => { try { Device(handle)?.Disconnect(); } catch (Exception) { /* disconnecting never fails */ } });
            host.DevWrite = Pointer<DevBytesFn>((handle, data, count) => Guard(() =>
            {
                var device = Device(handle);
                if (device == null) return 0;
                var bytes = new byte[Math.Max(0, count)];
                if (count > 0) Marshal.Copy(data, bytes, 0, count);
                return device.Write(bytes) ? 1 : 0;
            }));
            host.DevSendCommand = Pointer<DevCommandFn>((handle, text) => Guard(() => Device(handle)?.SendCommand(Marshal.PtrToStringUTF8(text) ?? "") == true ? 1 : 0));
            host.DevPoll = Pointer<DevBytesFn>((handle, buffer, size) => Guard(() =>
            {
                var queue = QueueOf(handle);
                if (queue == null || !queue.TryPeek(out var packet)) return -1;
                if (packet.Length > size) return packet.Length;   // kept: the library asks again with room
                queue.TryDequeue(out packet);
                if (packet == null) return -1;
                Marshal.Copy(packet, 0, buffer, packet.Length);
                return packet.Length;
            }, -1));
        }
    }
}
