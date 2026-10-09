using System;
using System.Collections.Generic;

namespace fire.Device.Manager.Drivers.Loopback
{
    /// <summary>A simulated device without hardware: everything that is sent comes back as the reply.
    /// For trying out scripts, the default device and packet tracking without a connected device
    /// (identifier `loopback:echo`).</summary>
    public class LoopbackDriver : IDriver
    {
        public string Identifier => "loopback";

        public IEnumerable<IDevice> EnumerateDevices()
        {
            yield return new LoopbackDevice("echo");
        }
    }
}
