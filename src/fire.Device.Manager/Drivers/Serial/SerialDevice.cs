using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace fire.Device.Manager.Drivers.Serial
{
    public class SerialDevice : IDevice
    {
        protected SerialPort? _serialPort;

        protected Thread? _portThread;

        protected string? _portName;

        public bool IsConnected { get; private set; }

        protected volatile bool _isDisconnecting;

        public Action<byte[]>? OnRawDataReceived { get; set; }

        public Action<byte[]>? OnRawDataSent { get; set; }

        public event Action? StateChanged;

        public string? PortName => _portName;

        // Used to be an explicit interface implementation that ALWAYS
        // threw ('DeviceAvailability IDevice.Availability =>
        // throw new NotImplementedException()'), INDEPENDENT of the field
        // of the same name directly below - every access through the
        // IDevice interface (exactly what the fire bridge does, since it
        // only knows IDevice) would thus always have failed, even if one had
        // successfully called TestAvailability() beforehand through a concrete
        // SerialDevice reference. Now ONE ordinary,
        // public property that fulfils the interface correctly.
        public DeviceAvailability Availability { get; private set; }

        public SerialDevice(string portName)
        {
            _portName = portName;
            Availability = DeviceAvailability.Unchecked;
        }

        private void RaiseStateChanged()
        {
            try { StateChanged?.Invoke(); }
            catch (Exception ex) { Debug.WriteLine(ex); } // a faulty observer must not disturb the device
        }

        public DeviceAvailability TestAvailability()
        {
            // A connected device keeps its port open - opening it a second time would fail and wrongly
            // report it as "unavailable".
            if (IsConnected)
            {
                Availability = DeviceAvailability.Available;
                return Availability;
            }

            var previous = Availability;
            var serialPort = new SerialPort(_portName, 115200);

            try
            {
                serialPort.Open();
                serialPort.Close();

                Availability = DeviceAvailability.Available;
            }
            catch(Exception e)
            {
                Debug.WriteLine(e);

                Availability = DeviceAvailability.Unavailable;
            }
            finally
            {
                serialPort.Dispose();
            }

            if (Availability != previous) RaiseStateChanged();
            return Availability;
        }

        public void Connect(string portName)
        {
            _portName = portName;
            Connect();
        }

        public void Connect()
        {
            if (string.IsNullOrEmpty(_portName))
            {
                throw new InvalidOperationException("Port name is not set.");
            }

            Disconnect();

            _isDisconnecting = false;
            _serialPort = new SerialPort(_portName,115200);
            _serialPort.Open();
            // First "connected", THEN the read thread: its loop only runs as long as IsConnected holds.
            IsConnected = true;
            Availability = DeviceAvailability.Available;
            StartPolling();
            RaiseStateChanged();
        }

        public void Disconnect()
        {
            _isDisconnecting = true;

            DisconnectInternal();
        }

        protected void DisconnectInternal()
        {
            bool wasConnected = IsConnected;
            var port = _serialPort;
            _serialPort = null;
            IsConnected = false;

            try
            {
                if (port?.IsOpen == true) port.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            finally
            {
                port?.Dispose();
            }

            if (wasConnected) RaiseStateChanged();
        }

        public bool SendCommand(string command)
        {
            var port = _serialPort;
            if (port?.IsOpen != true) return false;

            // Like SerialPort.WriteLine (text + NewLine in the port's encoding), but as bytes, so that
            // packet tracking sees exactly what goes onto the wire.
            var bytes = port.Encoding.GetBytes(command + port.NewLine);
            port.Write(bytes, 0, bytes.Length);
            OnRawDataSent?.Invoke(bytes);
            return true;
        }

        public bool Write(byte[] data)
        {
            var port = _serialPort;
            if (port?.IsOpen != true) return false;
            port.Write(data, 0, data.Length);
            OnRawDataSent?.Invoke((byte[])data.Clone());
            return true;
        }

        protected void StartPolling()
        {
            _portThread = new Thread(() => PollyPocket().Wait()) { IsBackground = true, Name = $"serial-{_portName}" };
            _portThread.Start();
        }

        protected async Task PollyPocket()
        {
            try
            {
                while (!_isDisconnecting && IsConnected)
                {
                    var port = _serialPort;
                    if (port != null && port.BytesToRead > 0)
                    {
                        var buffer = new byte[port.BytesToRead];
                        int read = port.Read(buffer, 0, buffer.Length);
                        if (read < buffer.Length) Array.Resize(ref buffer, read);
                        if (read > 0) OnRawDataReceived?.Invoke(buffer);
                    }

                    Thread.Sleep(10);
                }
            }
            catch(Exception ex)
            {
                // Port gone (device unplugged or similar): report it as a connection loss - unless we are disconnecting ourselves right now.
                if (!_isDisconnecting)
                {
                    Debug.WriteLine($"Error in PollyPocket: {ex.Message}");
                    DisconnectInternal();
                }
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
