using System;
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

        /// <summary>When the editor exits: disconnect and release devices (only the owner may tear down the shared manager).</summary>
        public void Shutdown() => Manager.Shutdown();
    }
}
