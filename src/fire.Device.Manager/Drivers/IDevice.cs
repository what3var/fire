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

        /// <summary>Received raw data. Called from the device's OWN thread. Several interested parties
        /// attach with `+=` (script bridge, packet tracking of the DeviceManager) - a direct `=`
        /// would displace the others.</summary>
        Action<byte[]>? OnRawDataReceived { get; set; }

        /// <summary>Sent raw data (the bytes as they really go onto the wire, i.e. including the line ending).
        /// Same rules as <see cref="OnRawDataReceived"/>.</summary>
        Action<byte[]>? OnRawDataSent { get; set; }

        /// <summary>The connection or availability status has changed (connect, disconnect, connection loss,
        /// availability check). May fire on a background thread.</summary>
        event Action? StateChanged;

        DeviceAvailability TestAvailability();
        void Connect();
        void Disconnect();

        /// <summary>Sends `command` (with a line ending). false if not connected - nothing was sent.</summary>
        bool SendCommand(string command);

        /// <summary>Sends `data` exactly as it is (no line ending, no re-encoding). false if not connected - nothing was sent.</summary>
        bool Write(byte[] data);
    }
}
