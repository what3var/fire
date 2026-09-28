using System;
using System.Collections.Generic;
using System.Text;

namespace fire.Device.Manager.Drivers
{
    public interface IDriver
    {
        string Identifier { get; }

        IEnumerable<IDevice> EnumerateDevices();
    }
}
