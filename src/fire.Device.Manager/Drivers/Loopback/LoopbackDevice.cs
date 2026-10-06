using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace fire.Device.Manager.Drivers.Loopback
{
    /// <summary>Siehe <see cref="LoopbackDriver"/>. Die Antwort kommt - wie bei echter Hardware - nicht im Aufruf von
    /// SendCommand, sondern kurz danach auf einem anderen Thread.</summary>
    public class LoopbackDevice : IDevice
    {
        private readonly string _name;

        public LoopbackDevice(string name) => _name = name;

        public DeviceAvailability Availability => DeviceAvailability.Available;

        public bool IsConnected { get; private set; }

        public string? PortName => _name;

        public Action<byte[]>? OnRawDataReceived { get; set; }

        public Action<byte[]>? OnRawDataSent { get; set; }

        public event Action? StateChanged;

        public DeviceAvailability TestAvailability() => DeviceAvailability.Available;

        public void Connect()
        {
            if (IsConnected) return;
            IsConnected = true;
            StateChanged?.Invoke();
        }

        public void Disconnect()
        {
            if (!IsConnected) return;
            IsConnected = false;
            StateChanged?.Invoke();
        }

        public bool SendCommand(string command) => Write(Encoding.UTF8.GetBytes(command + "\n"));

        public bool Write(byte[] data)
        {
            if (!IsConnected) return false;

            var bytes = (byte[])data.Clone();
            OnRawDataSent?.Invoke(bytes);

            // The echoes are delivered one after the other by a single worker, so they arrive in the order they were
            // written (one thread-pool item per write could overtake each other).
            lock (_echoGate)
            {
                _echoQueue.Enqueue(bytes);
                if (_delivering) return true;
                _delivering = true;
            }
            ThreadPool.QueueUserWorkItem(_ => DeliverEchoes());
            return true;
        }

        private readonly object _echoGate = new();
        private readonly Queue<byte[]> _echoQueue = new();
        private bool _delivering;

        private void DeliverEchoes()
        {
            while (true)
            {
                byte[] next;
                lock (_echoGate)
                {
                    if (_echoQueue.Count == 0)
                    {
                        _delivering = false;
                        return;
                    }
                    next = _echoQueue.Dequeue();
                }
                Thread.Sleep(5);
                if (IsConnected) OnRawDataReceived?.Invoke(next);
            }
        }

        public void Dispose() => Disconnect();
    }
}
