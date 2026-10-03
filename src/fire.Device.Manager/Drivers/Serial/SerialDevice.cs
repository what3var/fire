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

        // War vorher eine explizite Interface-Implementierung, die IMMER
        // geworfen hat ('DeviceAvailability IDevice.Availability =>
        // throw new NotImplementedException()'), UNABHÄNGIG vom Feld
        // gleichen Namens direkt darunter - jeder Zugriff über die
        // IDevice-Schnittstelle (genau das, was die fire-Brücke tut, da sie
        // nur IDevice kennt) wäre also immer gescheitert, selbst wenn man
        // über eine konkrete SerialDevice-Referenz vorher erfolgreich
        // TestAvailability() aufgerufen hätte. Jetzt EIN gewöhnliches,
        // öffentliches Property, das die Schnittstelle korrekt erfüllt.
        public DeviceAvailability Availability { get; private set; }

        public SerialDevice(string portName)
        {
            _portName = portName;
            Availability = DeviceAvailability.Unchecked;
        }

        private void RaiseStateChanged()
        {
            try { StateChanged?.Invoke(); }
            catch (Exception ex) { Debug.WriteLine(ex); } // ein fehlerhafter Beobachter darf das Gerät nicht stören
        }

        public DeviceAvailability TestAvailability()
        {
            // Ein verbundenes Gerät hält seinen Port offen - ein zweites Öffnen würde fehlschlagen und es
            // fälschlich als "nicht verfügbar" melden.
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
            // Erst "verbunden", DANN der Lese-Thread: seine Schleife läuft nur solange IsConnected gilt.
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

            // Wie SerialPort.WriteLine (Text + NewLine in der Kodierung des Ports), aber als Bytes, damit die
            // Paketverfolgung genau sieht, was auf die Leitung geht.
            var bytes = port.Encoding.GetBytes(command + port.NewLine);
            port.Write(bytes, 0, bytes.Length);
            OnRawDataSent?.Invoke(bytes);
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
                // Port weg (Gerät abgezogen o.ä.): als Verbindungsverlust melden - außer wir trennen selbst gerade.
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
