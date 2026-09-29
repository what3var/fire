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

        protected bool _isDisconnecting;

        public Action<byte[]>? OnRawDataReceived { get; set; }

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

        public DeviceAvailability TestAvailability()
        {
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
            StartPolling();
            IsConnected = true;
        }

        public void Disconnect()
        {
            _isDisconnecting = true;

            DisconnectInternal();
        }

        protected void DisconnectInternal()
        {
            if (_serialPort?.IsOpen == true)
            {
                _serialPort.Close();
                _serialPort = null;
                IsConnected = false;
            }
        }

        public void SendCommand(string command)
        {
            if (_serialPort?.IsOpen == true)
            {
                _serialPort.WriteLine(command);
            }
        }

        protected void StartPolling()
        {
            _portThread = new Thread(() => PollyPocket().Wait());
            _portThread.Start();
        }

        protected async Task PollyPocket()
        {
            try
            {
                while (!_isDisconnecting && IsConnected)
                {
                    if (_serialPort?.BytesToRead > 0)
                    {
                        var buffer = new byte[_serialPort.BytesToRead];
                        _serialPort.Read(buffer, 0, buffer.Length);
                        OnRawDataReceived?.Invoke(buffer);
                    }

                    Thread.Sleep(10);
                }
            }
            catch(Exception ex)
            {
                Console.WriteLine($"Error in PollyPocket: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
