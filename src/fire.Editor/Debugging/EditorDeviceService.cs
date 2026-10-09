using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using fire.Device.Manager.DeviceManager;
using fire.Device.Manager.Drivers.Loopback;

namespace fire.Editor
{
    /// <summary>Der GETEILTE DeviceManager des Editors (siehe DeviceManager.IsShared): alle im Editor laufenden Skripte
    /// benutzen ihn gemeinsam, offene Verbindungen überleben einen Lauf, und ein Skript kann ihn nicht abbauen.
    /// Dazu das Standardgerät (`Device.Default` im Skript) und die Einstellungen, die den Editor-Neustart überleben
    /// (Standardgerät, Simulationsgerät) in `%AppData%/fire/editor-devices.txt`.
    ///
    /// Alles hier ist UI-frei; Ereignisse des Managers können auf Hintergrund-Threads feuern - die Ansichten
    /// (DevicesPanelControl, PacketTraceControl) holen sie selbst auf den UI-Thread.</summary>
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

        /// <summary>Das simulierte Echo-Gerät `loopback:echo` (ohne Hardware zum Ausprobieren) anzeigen.</summary>
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

        /// <summary>Sucht Geräte auf einem Hintergrund-Thread. `fastScan`: nur auflisten; sonst jedes Gerät auf Verfügbarkeit prüfen
        /// (öffnet kurz seinen Anschluss - dauert).</summary>
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
                System.Diagnostics.Debug.WriteLine(ex); // eine unlesbare Einstellungsdatei darf den Editor nicht stören
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

        /// <summary>Beim Beenden des Editors: Geräte trennen und freigeben (nur der Besitzer darf den geteilten Manager abbauen).</summary>
        public void Shutdown() => Manager.Shutdown();
    }
}
