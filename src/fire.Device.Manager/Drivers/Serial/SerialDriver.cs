using fire.Device.Manager.Drivers;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;

namespace fire.Device.Manager.Drivers.Serial
{
    public class SerialDriver: IDriver
    {
        public string Identifier => "serial";

        public IEnumerable<IDevice> EnumerateDevices()
        {
            var ports = SerialPort.GetPortNames().ToList();

            var devices = ports.Select(p => new SerialDevice(p)).ToList();

            return devices;
        }
    }
}
