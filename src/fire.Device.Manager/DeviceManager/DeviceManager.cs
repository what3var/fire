using fire.Device.Manager.Drivers;
using fire.Device.Manager.Drivers.Serial;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace fire.Device.Manager.DeviceManager
{
    /// <summary>
    /// Verwaltet Treiber und die von ihnen gefundenen Geräte. Alle öffentlichen Mitglieder sind thread-sicher (ein
    /// Skript, der Editor und die Treiber-Threads greifen gleichzeitig zu).
    ///
    /// GETEILTER Manager (<see cref="IsShared"/>): gehört dem Host (dem Editor), nicht dem laufenden Skript.
    /// Mehrere Skripte hintereinander arbeiten mit denselben Geräten (offene Verbindungen bleiben bestehen), und
    /// ein Skript darf ihn nicht abbauen: <see cref="Dispose"/> ist dann wirkungslos, nur der Besitzer räumt über
    /// <see cref="Shutdown"/> auf.
    ///
    /// Die Paketverfolgung (<see cref="PacketCaptured"/>) sieht jeden Verkehr, egal ob Skript oder Host sendet,
    /// weil sie sich direkt an die Geräte hängt.
    /// </summary>
    public class DeviceManager : IDisposable
    {
        private readonly object _lock = new();
        private int _handleCounter;
        //Devices und Drivers bekommen handles
        private readonly List<DeviceSlot> _deviceSlots = new();
        private readonly List<DriverSlot> _driverSlots = new();
        private string? _defaultIdentifier;

        /// <summary>Wahr, wenn der Manager dem Host gehört und von Skripten nur benutzt wird (siehe Klassendoku).</summary>
        public bool IsShared { get; init; }

        /// <summary>Die Standardgeräte-Kennung (z.B. `serial:COM3`), die Skripte als `Device.Default` bekommen; null = keins gewählt.</summary>
        public string? DefaultIdentifier
        {
            get { lock (_lock) return _defaultIdentifier; }
            set
            {
                lock (_lock)
                {
                    if (_defaultIdentifier == value) return;
                    _defaultIdentifier = value;
                }
                DefaultChanged?.Invoke();
            }
        }

        /// <summary>Das Handle des Standardgeräts, null wenn keins gewählt ist oder es (noch) nicht gefunden wurde.</summary>
        public int? DefaultHandle
        {
            get
            {
                lock (_lock)
                    return _defaultIdentifier == null ? null : _deviceSlots.FirstOrDefault(d => d.Identifier == _defaultIdentifier)?.Handle;
            }
        }

        /// <summary>Die Geräteliste hat sich geändert (neues Gerät gefunden, Suche beendet).</summary>
        public event Action? DevicesChanged;

        /// <summary>Verbindungs- oder Verfügbarkeitsstatus eines Geräts hat sich geändert. Kann auf einem Hintergrund-Thread feuern.</summary>
        public event Action<DeviceSlot>? DeviceStateChanged;

        /// <summary>Das Standardgerät wurde gewechselt.</summary>
        public event Action? DefaultChanged;

        /// <summary>Ein Paket wurde gesendet oder empfangen. Feuert auf dem Thread, der sendet bzw. des Geräts.</summary>
        public event Action<PacketRecord>? PacketCaptured;

        public DeviceManager()
        {
        }

        /// <summary>Ein Manager mit den eingebauten Treibern (seriell).</summary>
        public static DeviceManager CreateDefault(bool isShared = false)
        {
            var manager = new DeviceManager { IsShared = isShared };
            manager.RegisterDriver(new SerialDriver());
            return manager;
        }

        public int? GetNewHandle()
        {
            lock (_lock)
            {
                if (_handleCounter == int.MaxValue)
                    return null;
                return _handleCounter++;
            }
        }

        public void RegisterDriver(IDriver instance)
        {
            lock (_lock)
            {
                if (_driverSlots.Any(d => (d.Driver.GetType() == instance.GetType()) || (d.Driver.Identifier == instance.Identifier)))
                    return;

                var handle = GetNewHandle();

                if (handle == null)
                    throw new OutOfMemoryException("The source of handles has dried out. How did you get to creating about 2 billion!?");

                _driverSlots.Add(new DriverSlot(handle.Value, instance));
            }
        }

        public void UnregisterDriver(IDriver driver)
        {
            lock (_lock)
            {
                var slot = _driverSlots.FirstOrDefault(s => s.Driver == driver);

                if (slot == null)
                    throw new ArgumentException("The driver has not been found");

                _driverSlots.Remove(slot);
            }
        }

        /// <summary>Die Kennungen der registrierten Treiber.</summary>
        public IReadOnlyList<string> GetDriverIdentifiers()
        {
            lock (_lock) return _driverSlots.Select(d => d.Driver.Identifier).ToList();
        }

        /// <summary>Entfernt den Treiber mit dieser Kennung samt seiner Geräte (trennt sie vorher). false, wenn es ihn nicht gibt.
        /// Für den Besitzer des Managers - Skripte haben darauf keinen Zugriff.</summary>
        public bool RemoveDriver(string driverIdentifier)
        {
            List<DeviceSlot> removed;
            lock (_lock)
            {
                var driver = _driverSlots.FirstOrDefault(d => d.Driver.Identifier == driverIdentifier);
                if (driver == null) return false;
                _driverSlots.Remove(driver);
                removed = _deviceSlots.Where(s => s.DriverIdentifier == driverIdentifier).ToList();
                foreach (var slot in removed) _deviceSlots.Remove(slot);
            }
            foreach (var slot in removed) DisposeSlot(slot);
            DevicesChanged?.Invoke();
            return true;
        }

        public void RefreshDevices(bool fastscan)
        {
            List<DriverSlot> drivers;
            lock (_lock) drivers = _driverSlots.ToList();

            foreach (var driver in drivers)
            {
                var found = driver.Driver.EnumerateDevices().ToList();
                var toTest = new List<IDevice>();

                foreach (var device in found)
                {
                    var identifier = $"{driver.Driver.Identifier}:{device.PortName}";

                    DeviceSlot? existing;
                    lock (_lock) existing = _deviceSlots.FirstOrDefault(d => d.Identifier == identifier);
                    if (existing != null)
                    {
                        // Der Treiber liefert bei jedem Lauf neue Objekte - das schon bekannte Gerät bleibt, das neue ist überflüssig.
                        if (!ReferenceEquals(existing.Device, device)) device.Dispose();
                        toTest.Add(existing.Device);
                        continue;
                    }

                    DeviceSlot slot;
                    lock (_lock)
                    {
                        var handle = GetNewHandle();

                        if (handle == null)
                            throw new OutOfMemoryException("The source of handles has dried out. How did you get to creating about 2 billion!?");

                        slot = new DeviceSlot(handle.Value, identifier, device, driver.Driver.Identifier, IsShared);
                        _deviceSlots.Add(slot);
                    }
                    HookTraffic(slot);
                    toTest.Add(device);
                }

                if (!fastscan)
                {
                    // Auch Geräte, die der Treiber diesmal nicht mehr meldet (abgezogen), neu prüfen - sie werden dann "nicht verfügbar".
                    List<IDevice> vanished;
                    lock (_lock)
                        vanished = _deviceSlots.Where(s => s.DriverIdentifier == driver.Driver.Identifier && !toTest.Contains(s.Device)).Select(s => s.Device).ToList();
                    foreach (var device in toTest.Concat(vanished))
                        device.TestAvailability();
                }
            }

            DevicesChanged?.Invoke();
        }

        private void HookTraffic(DeviceSlot slot)
        {
            slot.Device.OnRawDataReceived += bytes => RaisePacket(slot, PacketDirection.DeviceToHost, bytes);
            slot.Device.OnRawDataSent += bytes => RaisePacket(slot, PacketDirection.HostToDevice, bytes);
            slot.Device.StateChanged += () => DeviceStateChanged?.Invoke(slot);
        }

        private void RaisePacket(DeviceSlot slot, PacketDirection direction, byte[] bytes)
        {
            var handler = PacketCaptured;
            if (handler == null) return;
            handler(new PacketRecord(DateTime.UtcNow, slot.Identifier, direction, (byte[])bytes.Clone()));
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
        public IDevice? GetDeviceByHandle(int handle)
        {
            lock (_lock) return _deviceSlots.FirstOrDefault(d => d.Handle == handle)?.Device;
        }

        /// <summary>Der Identifier (z.B. "serial:COM3") des Geräts mit diesem
        /// Handle, `null` wenn kein Gerät unter diesem Handle bekannt ist.</summary>
        public string? GetIdentifierByHandle(int handle)
        {
            lock (_lock) return _deviceSlots.FirstOrDefault(d => d.Handle == handle)?.Identifier;
        }

        /// <summary>Das Handle des Geräts mit diesem Identifier, `null` wenn
        /// kein Gerät mit diesem Identifier (mehr) bekannt ist (z.B. noch
        /// kein RefreshDevices() gelaufen, oder das Gerät wurde seither
        /// physisch entfernt und ist beim letzten Scan nicht mehr
        /// aufgetaucht - ein einmal vergebenes Handle wird NIE erneut
        /// entfernt/invalidiert, siehe DeviceSlot, ein "verschwundenes"
        /// Gerät bleibt also unter seinem alten Handle weiter abfragbar,
        /// meldet dann aber über TestAvailability() Unavailable).</summary>
        public int? GetHandleByIdentifier(string identifier)
        {
            lock (_lock) return _deviceSlots.FirstOrDefault(d => d.Identifier == identifier)?.Handle;
        }

        /// <summary>Alle aktuell bekannten Geräte-Handles, in der Reihenfolge
        /// ihrer Registrierung (nicht notwendigerweise numerisch sortiert,
        /// falls dazwischen auch Treiber-Handles vergeben wurden).</summary>
        public IReadOnlyList<int> GetAllDeviceHandles()
        {
            lock (_lock) return _deviceSlots.Select(d => d.Handle).ToList();
        }

        public int DeviceCount
        {
            get { lock (_lock) return _deviceSlots.Count; }
        }

        /// <summary>Eine Momentaufnahme aller bekannten Geräte.</summary>
        public IReadOnlyList<DeviceSlot> GetSlots()
        {
            lock (_lock) return _deviceSlots.ToList();
        }

        public DeviceSlot? GetSlotByIdentifier(string identifier)
        {
            lock (_lock) return _deviceSlots.FirstOrDefault(d => d.Identifier == identifier);
        }

        // ------------------------------------------------------------
        // Aufräumen
        // ------------------------------------------------------------

        /// <summary>Beendet den Manager: trennt und gibt alle Geräte frei. Bei einem GETEILTEN Manager wirkungslos
        /// (siehe Klassendoku) - nur <see cref="Shutdown"/> baut ihn ab.</summary>
        public void Dispose()
        {
            if (IsShared) return;
            Shutdown();
        }

        /// <summary>Baut den Manager unbedingt ab (Besitzer, z.B. beim Beenden des Editors).</summary>
        public void Shutdown()
        {
            List<DeviceSlot> slots;
            lock (_lock)
            {
                slots = _deviceSlots.ToList();
                _deviceSlots.Clear();
                _driverSlots.Clear();
            }
            foreach (var slot in slots) DisposeSlot(slot);
        }

        private static void DisposeSlot(DeviceSlot slot)
        {
            try { slot.Device.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); }
        }
    }
}
