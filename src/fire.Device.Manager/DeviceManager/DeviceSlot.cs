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

        /// <summary>Kennung des Treibers, der das Gerät gefunden hat (`serial`).</summary>
        public string DriverIdentifier { get; private set; }

        public IDevice Device { get; private set; }

        /// <summary>Das Gerät gehört einem geteilten DeviceManager (siehe <see cref="DeviceManager.IsShared"/>):
        /// Skripte dürfen es benutzen (verbinden, senden, empfangen), aber nicht zerstören.</summary>
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
