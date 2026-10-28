using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using fire.Device.Manager.DeviceManager;
using fire.Device.Manager.Drivers.Loopback;

namespace fire.Editor
{
    /// <summary>The SHARED DeviceManager of the editor (see DeviceManager.IsShared): all scripts running in the editor
    /// use it together, open connections survive a run, and a script cannot tear it down.
    /// In addition the default device (`Device.Default` in the script) and the settings that survive an editor restart
    /// (default device, simulation device) in `%AppData%/fire/editor-devices.txt`.
    ///
    /// Everything here is UI-free; events of the manager may fire on background threads - the views
    /// (DevicesPanelControl, PacketTraceControl) bring them to the UI thread themselves.</summary>
    public sealed class EditorDeviceService
    {
        private static string SettingsPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fire", "editor-devices.txt");

        public DeviceManager Manager { get; }

        private bool _loopbackEnabled;

        public EditorDeviceService()
        {
            Manager = DeviceManager.CreateDefault(isShared: true);
            LoadSettings();
            Manager.DefaultChanged += SaveSettings;
        }

        /// <summary>Show the simulated echo device `loopback:echo` (for trying things out without hardware).</summary>
        public bool LoopbackEnabled
        {
            get => _loopbackEnabled;
            set
            {
                if (_loopbackEnabled == value) return;
                _loopbackEnabled = value;
                if (value)
                {
                    Manager.RegisterDriver(new LoopbackDriver());
                    Manager.RefreshDevices(fastscan: true);
                }
                else
                {
                    Manager.RemoveDriver("loopback");
                }
                SaveSettings();
            }
        }

        /// <summary>Searches for devices on a background thread. `fastScan`: only list; otherwise check every device for availability
        /// (briefly opens its port - takes a while).</summary>
        public Task RefreshAsync(bool fastScan) => Task.Run(() => Manager.RefreshDevices(fastScan));

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                string? defaultId = null;
                foreach (var line in File.ReadAllLines(SettingsPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    if (key == "default" && value.Length > 0) defaultId = value;
                    else if (key == "loopback" && value == "1") _loopbackEnabled = true;
                }
                if (_loopbackEnabled) Manager.RegisterDriver(new LoopbackDriver());
                Manager.DefaultIdentifier = defaultId;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex); // an unreadable settings file must not disturb the editor
            }
        }

        private void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllLines(SettingsPath, new[]
                {
                    "default=" + (Manager.DefaultIdentifier ?? ""),
                    "loopback=" + (_loopbackEnabled ? "1" : "0"),
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
            }
        }

        // ---- the device drivers of the installed packages (manifest `drivers`): a fire program of their own, see PackageDriverAgent ----

        private fire.Compiler.PackageDriverAgent? _agent;
        private List<string> _agentDrivers = new();
        private readonly SemaphoreSlim _agentGate = new(1, 1);

        /// <summary>What happened when the package drivers were loaded last (for the status bar).</summary>
        public string PackageDriverStatus { get; private set; } = "";

        /// <summary>Starts the drivers of the installed packages (`reload`: ends the running ones first) and looks for their devices. Runs in the background.</summary>
        public Task StartPackageDriversAsync(bool reload = false) => Task.Run(async () =>
        {
            await _agentGate.WaitAsync();
            try
            {
                if (_agent != null && !reload) return;
                StopAgent();
                var drivers = fire.Compiler.PackageDriverAgent.FindDrivers();
                if (drivers.Count == 0) { PackageDriverStatus = "No installed package has device drivers."; return; }
                var agent = new fire.Compiler.PackageDriverAgent();
                if (!agent.Start(drivers))
                {
                    PackageDriverStatus = "The device drivers of the packages could not be started: " + (agent.EndedWith ?? "no answer");
                    agent.Dispose();
                    return;
                }
                _agent = agent;
                _agentDrivers = agent.DriverNames();
                agent.Attach(Manager);
                Manager.RefreshDevices(fastscan: true);
                var errors = agent.StartErrors;
                PackageDriverStatus = $"Device drivers of the packages: {string.Join(", ", _agentDrivers)}" + (errors.Count > 0 ? " - " + string.Join("; ", errors) : "");
            }
            catch (Exception ex) { PackageDriverStatus = "The device drivers of the packages failed: " + ex.Message; }
            finally { _agentGate.Release(); }
        });

        private void StopAgent()
        {
            foreach (var name in _agentDrivers) Manager.RemoveDriver(name);
            _agentDrivers = new();
            _agent?.Dispose();
            _agent = null;
        }

        /// <summary>When the editor exits: disconnect and release devices (only the owner may tear down the shared manager).</summary>
        public void Shutdown()
        {
            Manager.Shutdown();
            try { _agent?.Dispose(); } catch (Exception) { }
        }
    }
}
