using fire.Device.Manager.Drivers;
using fire.Device.Manager.Drivers.Serial;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace fire.Device.Manager.DeviceManager
{
    /// <summary>
    /// Manages drivers and the devices they find. All public members are thread-safe (a
    /// script, the editor and the driver threads access it at the same time).
    ///
    /// SHARED manager (<see cref="IsShared"/>): belongs to the host (the editor), not to the running script.
    /// Several scripts in a row work with the same devices (open connections stay open), and
    /// a script must not tear it down: <see cref="Dispose"/> then has no effect, only the owner cleans up via
    /// <see cref="Shutdown"/>.
    ///
    /// Packet tracking (<see cref="PacketCaptured"/>) sees all traffic, whether the script or the host sends,
    /// because it attaches directly to the devices.
    /// </summary>
    public class DeviceManager : IDisposable
    {
        private readonly object _lock = new();
        private int _handleCounter;
        // Devices and Drivers get handles
        private readonly List<DeviceSlot> _deviceSlots = new();
        private readonly List<DriverSlot> _driverSlots = new();
        private string? _defaultIdentifier;

        /// <summary>True if the manager belongs to the host and is only used by scripts (see the class documentation).</summary>
        public bool IsShared { get; init; }

        /// <summary>The default device identifier (e.g. `serial:COM3`) that scripts receive as `Device.Default`; null = none chosen.</summary>
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

        /// <summary>The handle of the default device, null if none is chosen or it has not (yet) been found.</summary>
        public int? DefaultHandle
        {
            get
            {
                lock (_lock)
                    return _defaultIdentifier == null ? null : _deviceSlots.FirstOrDefault(d => d.Identifier == _defaultIdentifier)?.Handle;
            }
        }

        /// <summary>The device list has changed (new device found, search finished).</summary>
        public event Action? DevicesChanged;

        /// <summary>The connection or availability status of a device has changed. May fire on a background thread.</summary>
        public event Action<DeviceSlot>? DeviceStateChanged;

        /// <summary>The default device was changed.</summary>
        public event Action? DefaultChanged;

        /// <summary>A packet was sent or received. Fires on the thread that sends, or on the device's thread.</summary>
        public event Action<PacketRecord>? PacketCaptured;

        public DeviceManager()
        {
        }

        /// <summary>A manager with the built-in drivers (serial).</summary>
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

        /// <summary>The identifiers of the registered drivers.</summary>
        public IReadOnlyList<string> GetDriverIdentifiers()
        {
            lock (_lock) return _driverSlots.Select(d => d.Driver.Identifier).ToList();
        }

        /// <summary>Removes the driver with this identifier together with its devices (disconnecting them first). false if there is none.
        /// For the owner of the manager - scripts have no access to it.</summary>
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
                        // The driver delivers new objects on every run - the already known device stays, the new one is redundant.
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
                    // Also re-check devices that the driver no longer reports this time (unplugged) - they then become "unavailable".
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
        // Query by handle/identifier (SPEC requirement of the fire bridge,
        // see fire.Device.Bridge) - before this there was NO way to get back to
        // an already registered device, only to
        // RefreshDevices/RegisterDriver. Purely reading additions, they change
        // nothing about the previous behaviour.
        // ------------------------------------------------------------

        /// <summary>The device with this handle, `null` if no device is (any longer)
        /// registered under this handle (e.g. never assigned or a
        /// handle that actually belongs to a DRIVER rather than a device -
        /// handles are assigned from the SAME counter for devices AND drivers,
        /// see GetNewHandle, so not every
        /// number is a device).</summary>
        public IDevice? GetDeviceByHandle(int handle)
        {
            lock (_lock) return _deviceSlots.FirstOrDefault(d => d.Handle == handle)?.Device;
        }

        /// <summary>The identifier (e.g. "serial:COM3") of the device with this
        /// handle, `null` if no device is known under this handle.</summary>
        public string? GetIdentifierByHandle(int handle)
        {
            lock (_lock) return _deviceSlots.FirstOrDefault(d => d.Handle == handle)?.Identifier;
        }

        /// <summary>The handle of the device with this identifier, `null` if
        /// no device with this identifier is (any longer) known (e.g. no
        /// RefreshDevices() has run yet, or the device has since been
        /// physically removed and did not show up in the last
        /// scan - a handle, once assigned, is NEVER
        /// removed/invalidated, see DeviceSlot, a "vanished"
        /// device can thus still be queried under its old handle,
        /// but then reports Unavailable via TestAvailability()).</summary>
        public int? GetHandleByIdentifier(string identifier)
        {
            lock (_lock) return _deviceSlots.FirstOrDefault(d => d.Identifier == identifier)?.Handle;
        }

        /// <summary>All currently known device handles, in the order
        /// of their registration (not necessarily numerically sorted,
        /// if driver handles were also assigned in between).</summary>
        public IReadOnlyList<int> GetAllDeviceHandles()
        {
            lock (_lock) return _deviceSlots.Select(d => d.Handle).ToList();
        }

        public int DeviceCount
        {
            get { lock (_lock) return _deviceSlots.Count; }
        }

        /// <summary>A snapshot of all known devices.</summary>
        public IReadOnlyList<DeviceSlot> GetSlots()
        {
            lock (_lock) return _deviceSlots.ToList();
        }

        public DeviceSlot? GetSlotByIdentifier(string identifier)
        {
            lock (_lock) return _deviceSlots.FirstOrDefault(d => d.Identifier == identifier);
        }

        // ------------------------------------------------------------
        // Clean-up
        // ------------------------------------------------------------

        /// <summary>Shuts the manager down: disconnects and releases all devices. With a SHARED manager it has no effect
        /// (see the class documentation) - only <see cref="Shutdown"/> tears it down.</summary>
        public void Dispose()
        {
            if (IsShared) return;
            Shutdown();
        }

        /// <summary>Tears the manager down unconditionally (owner, e.g. when the editor exits).</summary>
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
