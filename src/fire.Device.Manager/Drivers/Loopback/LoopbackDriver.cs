using System;
using System.Collections.Generic;

namespace fire.Device.Manager.Drivers.Loopback
{
    /// <summary>Ein simuliertes Gerät ohne Hardware: alles, was man sendet, kommt als Antwort zurück.
    /// Zum Ausprobieren von Skripten, Standardgerät und Paketverfolgung ohne angeschlossenes Gerät
    /// (Kennung `loopback:echo`).</summary>
    public class LoopbackDriver : IDriver
    {
        public string Identifier => "loopback";

        public IEnumerable<IDevice> EnumerateDevices()
        {
            yield return new LoopbackDevice("echo");
        }
    }
}
