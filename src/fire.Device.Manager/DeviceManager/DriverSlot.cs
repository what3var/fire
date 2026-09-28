using fire.Device.Manager.Drivers;
using System;
using System.Collections.Generic;
using System.Text;

namespace fire.Device.Manager.DeviceManager
{
    public class DriverSlot
    {
        public int Handle { get; private set; }

        public IDriver Driver { get; private set; }

        public DriverSlot(int handle, IDriver driver    )
        {
            Handle = handle;
            Driver = driver;
        }
    }
}
