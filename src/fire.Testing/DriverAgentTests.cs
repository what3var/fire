using fire.Tooling;
using fire.Device.Manager.DeviceManager;

/// <summary>The device drivers of installed packages in spark's device manager (PackageDriverAgent): the agent loads each package's drivers as a fire plugin (its prelude
/// compiled once for the VM, cached), the old way (`#import`) is the fallback. Part of the device checks; alone: `FIRE_TESTS_ONLY=drivers`.</summary>
static class DriverAgentTests
{
    private static int _failures;

    private static void Check(string title, bool ok, string? detail = null)
    {
        if (!ok) _failures++;
        Console.WriteLine(ok ? $"OK: Treiber im Paket: {title}" : $"FEHLER: Treiber im Paket: {title}" + (detail == null ? "" : $"\n{detail}"));
    }

    /// <summary>Runs the checks; the number of failures.</summary>
    public static int Run()
    {
        _failures = 0;
        var proj = fire.Projects.FireProject.Parse("{\"name\": \"x\", \"type\": \"library\", \"drivers\": [\"A.MyDriver\"]}");
        var again = fire.Projects.FireProject.Parse(proj.ToJson());
        var manifest = fire.Projects.PackageManifest.Parse("{\"name\": \"p\", \"version\": \"1.0.0\", \"imports\": [{\"name\": \"p\", \"drivers\": [\"PDriver\"]}]}");
        Check("'drivers' im Projekt und im Manifest (leer wird nicht geschrieben)", again.Drivers is { Count: 1 } && again.Drivers[0] == "A.MyDriver" && manifest.Imports[0].Drivers is { Count: 1 }
            && !fire.Projects.FireProject.Parse("{\"name\": \"y\"}").ToJson().Contains("drivers"));

        var found = PackageDriverAgent.FindDrivers();
        Check("die installierten Standardpakete melden ihre Treiber (usb, gpio, i2c, spi)", found.Any(d => d.Class == "UsbDriver" && d.Import == "usb") && found.Any(d => d.Class == "GpioDriver"),
            string.Join(", ", found));

        // the agent (M3: compiled by forge, the packages through `#import` - the bytecode plugins of the C# forge are gone): its devices, sending and echo
        var withImport = AgentRun(false, "#import");
        Check("der Agent hat Geraete", withImport.Count > 0, string.Join(",", withImport));

        // all drivers of the installed packages
        var allImport = AllDrivers(false, out _);
        Check("alle Treiber der Pakete (usb, gpio, i2c, spi) mit #import: Treiber und Geraete", allImport.Count > 0, string.Join(",", allImport));
        return _failures;
    }

    private static List<string> AgentRun(bool plugins, string how)
    {
        using var agent = new PackageDriverAgent { UsePlugins = plugins };
        bool ok = agent.Start(new[] { new PackageDriver("fire-usb", "usb", "UsbDriver"), new PackageDriver("x", "usb", "GibtEsNicht") });
        var manager = new DeviceManager();
        var captured = new List<PacketRecord>();
        string detail = agent.EndedWith ?? "";
        // (the agent is a process of the VM in C - its plugins loaded there - unless no `fire` can be had)
        bool cvm = false;
        try { cvm = Forge.LocateVm(64, 64) != null; } catch (Exception) { }
        if (cvm) Check($"der Treiber-Agent ({how}) laeuft in der C-VM (fire), ohne Rueckfall in die C#-VM", ok && agent.RunsInFire && agent.EndedWith == null, $"{ok} {agent.RunsInFire} {agent.EndedWith}");
        var ids = new List<string>();
        if (ok)
        {
            agent.Attach(manager);
            manager.PacketCaptured += p => { lock (captured) captured.Add(p); };
            manager.RefreshDevices(true);
            ids = manager.GetSlots().Select(s => s.Identifier).OrderBy(s => s, StringComparer.Ordinal).ToList();
            var slot = manager.GetSlotByIdentifier("usb:sim");
            ok = slot != null && slot.Device.Description == "simulated USB device (echo)" && !slot.Device.IsConnected && agent.StartErrors.Count == 1 && agent.StartErrors[0].Contains("GibtEsNicht")
                && agent.PluginImports.Contains("usb") == plugins;
            if (ok)
            {
                slot!.Device.Connect();
                ok = slot.Device.IsConnected && slot.Device.Write(System.Text.Encoding.ASCII.GetBytes("ping"));
                for (int i = 0; i < 300 && ok; i++) { lock (captured) if (captured.Count >= 2) break; Thread.Sleep(10); }
                lock (captured)
                    ok = ok && captured.Count == 2 && captured[0].Direction == PacketDirection.HostToDevice
                        && captured[1].Direction == PacketDirection.DeviceToHost && captured.All(c => System.Text.Encoding.ASCII.GetString(c.Data) == "ping");
                slot.Device.Disconnect();
                ok = ok && !slot.Device.IsConnected;
            }
            detail = string.Join(" | ", ids) + " / " + string.Join(";", agent.StartErrors) + " / " + captured.Count + " / plugins " + string.Join(",", agent.PluginImports);
        }
        manager.Shutdown();
        Check($"der Treiber-Agent ({how}) bringt usb:sim in den DeviceManager (Beschreibung, Verbinden, Senden, Echo, Trennen); ein Treiber, den es nicht gibt, wird gemeldet", ok, detail);
        return ok ? ids : new List<string>();
    }

    private static List<string> AllDrivers(bool plugins, out List<string> pluginImports)
    {
        using var agent = new PackageDriverAgent { UsePlugins = plugins };
        pluginImports = new List<string>();
        if (!agent.Start()) return new List<string> { "failed: " + agent.EndedWith };
        pluginImports = agent.PluginImports.ToList();
        var names = agent.DriverNames().OrderBy(n => n, StringComparer.Ordinal);
        var devices = agent.ListDevices(refresh: true).Select(d => d.Driver + ":" + d.Identifier + ":" + d.Name).OrderBy(n => n, StringComparer.Ordinal);
        return names.Concat(devices).Concat(agent.StartErrors).ToList();
    }
}
