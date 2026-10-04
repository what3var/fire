using System;
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
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(5);
                if (IsConnected) OnRawDataReceived?.Invoke((byte[])bytes.Clone());
            });
            return true;
        }

        public void Dispose() => Disconnect();
    }
}
