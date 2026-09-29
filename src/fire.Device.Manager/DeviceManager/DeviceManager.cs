using fire.Device.Manager.Drivers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace fire.Device.Manager.DeviceManager
{
    public class DeviceManager
    {
        private int _handleCounter;
        //Devices und Drivers bekommen handles
        private List<DeviceSlot> _deviceSlots { get; set; }

        private List<DriverSlot> _driverSlots { get; set; }

        public DeviceManager()
        {
            _driverSlots = new List<DriverSlot>();
            _deviceSlots = new List<DeviceSlot>();
        }

        public int? GetNewHandle()
        {
            if (_handleCounter == int.MaxValue)
                return null;
            return _handleCounter++;
        }

        public void RegisterDriver(IDriver instance)
        {
            if (_driverSlots.Any(d => (d.Driver.GetType() == instance.GetType()) || (d.Driver.Identifier == instance.Identifier)))
                return;

            var handle = GetNewHandle();

            if (handle == null)
                throw new OutOfMemoryException("The source of handles has dried out. How did you get to creating about 2 billion!?");

            var slot = new DriverSlot(handle.Value, instance);

            _driverSlots.Add(slot);
        }

        public void UnregisterDriver(IDriver driver)
        {
            var slot = _driverSlots.FirstOrDefault(s => s.Driver == driver);

            if (slot == null)
                throw new ArgumentException("The driver has not been found");

            _driverSlots.Remove(slot);
        }

        public void RefreshDevices(bool fastscan)
        {
            foreach (var driver in _driverSlots)
            {
                var devices = driver.Driver.EnumerateDevices();

                foreach (var device in devices)
                {
                    var identifier = $"{driver.Driver.Identifier}:{device.PortName}";
                    
                    if (_deviceSlots.Any(d => d.Identifier == identifier))
                        continue;

                    var handle = GetNewHandle();

                    if (handle == null)
                        throw new OutOfMemoryException("The source of handles has dried out. How did you get to creating about 2 billion!?");

                    var slot = new DeviceSlot(handle.Value, identifier, device);

                    _deviceSlots.Add(slot);
                }

                if (!fastscan)
                    foreach (var device in devices)
                        device.TestAvailability();
            }
        }

        // ------------------------------------------------------------
        // Abfrage per Handle/Identifier (SPEC-Anforderung der fire-Brücke,
        // siehe fire.Device.Bridge) - vorher gab es KEINE Möglichkeit, an
        // ein bereits registriertes Gerät wieder heranzukommen, nur an
        // RefreshDevices/RegisterDriver. Rein lesende Ergänzungen, ändern
        // nichts am bisherigen Verhalten.
        // ------------------------------------------------------------

        /// <summary>Das Gerät mit diesem Handle, `null` wenn kein Gerät (mehr)
        /// unter diesem Handle registriert ist (z.B. nie vergeben oder ein
        /// Handle, der eigentlich zu einem DRIVER statt einem Gerät gehört -
        /// Handles werden aus DEMSELBEN Zähler für Geräte UND Treiber
        /// vergeben, siehe GetNewHandle, sind also nicht automatisch je jede
        /// Nummer ein Gerät).</summary>
        public IDevice? GetDeviceByHandle(int handle) =>
            _deviceSlots.FirstOrDefault(d => d.Handle == handle)?.Device;

        /// <summary>Der Identifier (z.B. "serial:COM3") des Geräts mit diesem
        /// Handle, `null` wenn kein Gerät unter diesem Handle bekannt ist.</summary>
        public string? GetIdentifierByHandle(int handle) =>
            _deviceSlots.FirstOrDefault(d => d.Handle == handle)?.Identifier;

        /// <summary>Das Handle des Geräts mit diesem Identifier, `null` wenn
        /// kein Gerät mit diesem Identifier (mehr) bekannt ist (z.B. noch
        /// kein RefreshDevices() gelaufen, oder das Gerät wurde seither
        /// physisch entfernt und ist beim letzten Scan nicht mehr
        /// aufgetaucht - ein einmal vergebenes Handle wird NIE erneut
        /// entfernt/invalidiert, siehe DeviceSlot, ein "verschwundenes"
        /// Gerät bleibt also unter seinem alten Handle weiter abfragbar,
        /// meldet dann aber über TestAvailability() Unavailable).</summary>
        public int? GetHandleByIdentifier(string identifier) =>
            _deviceSlots.FirstOrDefault(d => d.Identifier == identifier)?.Handle;

        /// <summary>Alle aktuell bekannten Geräte-Handles, in der Reihenfolge
        /// ihrer Registrierung (nicht notwendigerweise numerisch sortiert,
        /// falls dazwischen auch Treiber-Handles vergeben wurden).</summary>
        public IReadOnlyList<int> GetAllDeviceHandles() =>
            _deviceSlots.Select(d => d.Handle).ToList();

        public int DeviceCount => _deviceSlots.Count;
    }
}
