using fire.Device.Manager.Drivers;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace fire.Device.Manager.DeviceManager
{
    /// <summary>
    /// The devices of the host for programs in another process (docs/spec/VM_C.md, "Das geteilte Gerät"; M1 (3)): a program that spark runs in `fire` (the VM in C) asks the
    /// `dev_*` of its host (native/abi/fire_pkg_abi.h) over a local connection - FIRE_DEVICE_HOST=tcp:127.0.0.1:PORT;token=T (<see cref="EnvironmentValue"/>), the other
    /// side is native/vm/vm_devhost.c - and this server answers with the shared <see cref="DeviceManager"/> of the editor: its drivers (also those of the driver
    /// agent), its sharing, its packet trace - as the C# VM in spark's process did (src/fire.Runtime/Host/PackageHost.Devices.cs). One connection per program;
    /// what a device receives waits in a queue per device and connection until the program polls it; when the connection ends (the program ends), its hooks go.
    /// The protocol: vm_devhost.c (binary, little-endian; a request, its answer).
    /// </summary>
    public sealed class DeviceHostServer : IDisposable
    {
        public const string EnvironmentVariable = "FIRE_DEVICE_HOST";

        private readonly DeviceManager _manager;
        private readonly TcpListener _listener;
        private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        private readonly Thread _accept;
        private volatile bool _disposed;
        private readonly List<TcpClient> _clients = new();
        private int _connections;

        /// <summary>Programs that are connected now (and that have connected since the start).</summary>
        public int Connected { get { lock (_clients) return _clients.Count; } }
        public int ConnectionsSoFar => Volatile.Read(ref _connections);

        public DeviceHostServer(DeviceManager manager)
        {
            _manager = manager;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "fire device host" };
            _accept.Start();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>The value of FIRE_DEVICE_HOST for a program.</summary>
        public string EnvironmentValue => $"tcp:127.0.0.1:{Port};token={_token}";

        private void AcceptLoop()
        {
            while (!_disposed)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException) { return; }
                client.NoDelay = true;
                lock (_clients) _clients.Add(client);
                Interlocked.Increment(ref _connections);
                new Thread(() => Serve(client)) { IsBackground = true, Name = "fire device host connection" }.Start();
            }
        }

        private sealed class Connection
        {
            public readonly ConcurrentDictionary<int, ConcurrentQueue<byte[]>> Queues = new();
            public readonly List<(IDevice Device, Action<byte[]> Handler)> Hooks = new();
        }

        private static bool ReadExactly(Stream s, byte[] buffer, int count)
        {
            int at = 0;
            while (at < count)
            {
                int n = s.Read(buffer, at, count - at);
                if (n <= 0) return false;
                at += n;
            }
            return true;
        }

        private void Serve(TcpClient client)
        {
            var connection = new Connection();
            try
            {
                using var stream = client.GetStream();
                var head = new byte[9];
                bool greeted = false;
                while (!_disposed)
                {
                    if (!ReadExactly(stream, head, 4)) break;
                    int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(head);
                    if (n < 5 || n > 64 << 20) break;
                    var body = new byte[n];
                    if (!ReadExactly(stream, body, n)) break;
                    int op = body[0];
                    int a = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1));
                    var payload = body.AsSpan(5);
                    if (!greeted)
                    {
                        // (the first request says hello with the token: another process on the machine does not get the devices)
                        if (op != 0 || Encoding.UTF8.GetString(payload) != _token) break;
                        greeted = true;
                        Answer(stream, 1, ReadOnlySpan<byte>.Empty);
                        continue;
                    }
                    byte[]? data = null;
                    int result;
                    try { result = Handle(connection, op, a, payload, out data); }
                    catch (Exception) { result = op is 3 or 4 or 5 or 11 or 16 ? -1 : 0; data = null; }   // (a driver that throws: the call fails, the program goes on)
                    Answer(stream, result, data);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
            finally
            {
                foreach (var (device, handler) in connection.Hooks) device.OnRawDataReceived -= handler;
                lock (_clients) _clients.Remove(client);
                client.Dispose();
            }
        }

        private static void Answer(Stream stream, int result, ReadOnlySpan<byte> payload)
        {
            var answer = new byte[8 + payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(answer, (uint)(4 + payload.Length));
            BinaryPrimitives.WriteInt32LittleEndian(answer.AsSpan(4), result);
            payload.CopyTo(answer.AsSpan(8));
            stream.Write(answer, 0, answer.Length);
        }

        private IDevice? Device(int handle) => _manager.GetDeviceByHandle(handle);

        /// <summary>The queue of a device of this connection, with its receive hook in place (once).</summary>
        private ConcurrentQueue<byte[]>? QueueOf(Connection connection, int handle)
        {
            var device = Device(handle);
            if (device == null) return null;
            if (connection.Queues.TryGetValue(handle, out var existing)) return existing;
            var queue = new ConcurrentQueue<byte[]>();
            if (!connection.Queues.TryAdd(handle, queue)) return connection.Queues[handle];
            Action<byte[]> handler = bytes => { if (bytes.Length > 0) queue.Enqueue(bytes); };
            device.OnRawDataReceived += handler;
            lock (connection.Hooks) connection.Hooks.Add((device, handler));
            return queue;
        }

        private static int Text(string? text, out byte[]? data)
        {
            data = text == null ? null : Encoding.UTF8.GetBytes(text);
            return data?.Length ?? -1;
        }

        private int Handle(Connection connection, int op, int a, ReadOnlySpan<byte> payload, out byte[]? data)
        {
            data = null;
            switch (op)
            {
                case 1: _manager.RefreshDevices(a != 0); return _manager.DeviceCount;
                case 2: return _manager.DeviceCount;
                case 3: { var handles = _manager.GetAllDeviceHandles(); return a >= 0 && a < handles.Count ? handles[a] : -1; }
                case 4: return Text(_manager.GetIdentifierByHandle(a), out data);
                case 5: return _manager.DefaultHandle ?? -1;
                case 6: return _manager.IsShared ? 1 : 0;
                case 7: return _manager.GetSlotByIdentifier(_manager.GetIdentifierByHandle(a) ?? "")?.IsShared == true ? 1 : 0;
                case 8: return (int)(Device(a)?.Availability ?? DeviceAvailability.Unavailable);
                case 9: return (int)(Device(a)?.TestAvailability() ?? DeviceAvailability.Unavailable);
                case 10: return Device(a)?.IsConnected == true ? 1 : 0;
                case 11: { var device = Device(a); return device == null ? -1 : Text(device.PortName ?? "", out data); }
                case 12: { var device = Device(a); if (device == null) return 0; QueueOf(connection, a); device.Connect(); return 1; }
                case 13: try { Device(a)?.Disconnect(); } catch (Exception) { /* disconnecting never fails */ } return 0;
                case 14: return Device(a)?.Write(payload.ToArray()) == true ? 1 : 0;
                case 15: return Device(a)?.SendCommand(Encoding.UTF8.GetString(payload)) == true ? 1 : 0;
                case 16:
                    {
                        int room = payload.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(payload) : 0;
                        var queue = QueueOf(connection, a);
                        if (queue == null || !queue.TryPeek(out var packet)) return -1;
                        if (packet.Length > room) return packet.Length;   // (kept: the library asks again with room)
                        queue.TryDequeue(out packet);
                        if (packet == null) return -1;
                        data = packet;
                        return packet.Length;
                    }
                default: return 0;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _listener.Stop(); } catch (SocketException) { }
            lock (_clients) foreach (var c in _clients) try { c.Dispose(); } catch (Exception) { }
        }
    }
}
