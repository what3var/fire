using fire.Device.Manager.Drivers;
using System;
using System.Collections.Generic;
using System.Text;

namespace fire.Device.Manager.DeviceManager
{
    public class DeviceSlot
    {
        public int Handle { get; private set; }

        /// <summary>`treiber:anschluss`, z.B. `serial:COM3`.</summary>
        public string Identifier { get; private set; }

        /// <summary>Identifier of the driver that found the device (`serial`).</summary>
        public string DriverIdentifier { get; private set; }

        public IDevice Device { get; private set; }

        /// <summary>The device belongs to a shared DeviceManager (see <see cref="DeviceManager.IsShared"/>):
        /// scripts may use it (connect, send, receive), but not destroy it.</summary>
        public bool IsShared { get; private set; }

        public DeviceSlot(int handle, string identifier, IDevice device, string driverIdentifier = "", bool isShared = false)
        {
            Handle = handle;
            Identifier = identifier;
            Device = device;
            DriverIdentifier = driverIdentifier;
            IsShared = isShared;
        }
    }
}
