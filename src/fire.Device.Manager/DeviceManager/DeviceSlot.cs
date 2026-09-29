using fire.Device.Manager.Drivers;
using System;
using System.Collections.Generic;
using System.Text;

namespace fire.Device.Manager.DeviceManager
{
    public class DeviceSlot
    {
        public int Handle { get; private set; }

        public string Identifier { get; private set; }

        public IDevice Device { get; private set; }

        public DeviceSlot(int handle, string identifier, IDevice device)
        {
            Handle = handle;
            Identifier = identifier;
            Device = device;
        }
    }
}
