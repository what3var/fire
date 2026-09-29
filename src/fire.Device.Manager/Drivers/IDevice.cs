using System;
using System.Collections.Generic;
using System.Text;

namespace fire.Device.Manager.Drivers
{
    public interface IDevice : IDisposable
    {
        DeviceAvailability Availability { get; }

        bool IsConnected { get; }

        string? PortName { get; }

        Action<byte[]>? OnRawDataReceived { get; set; }

        DeviceAvailability TestAvailability();
        void Connect();
        void Disconnect();
        void SendCommand(string command);
    }
}
