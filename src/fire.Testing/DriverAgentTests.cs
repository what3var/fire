using fire.Compiler;
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
        var manifest = fire.Package.Manager.PackageManifest.Parse("{\"name\": \"p\", \"version\": \"1.0.0\", \"imports\": [{\"name\": \"p\", \"drivers\": [\"PDriver\"]}]}");
        Check("'drivers' im Projekt und im Manifest (leer wird nicht geschrieben)", again.Drivers is { Count: 1 } && again.Drivers[0] == "A.MyDriver" && manifest.Imports[0].Drivers is { Count: 1 }
            && !fire.Projects.FireProject.Parse("{\"name\": \"y\"}").ToJson().Contains("drivers"));

        var found = PackageDriverAgent.FindDrivers();
        Check("die installierten Standardpakete melden ihre Treiber (usb, gpio, i2c, spi)", found.Any(d => d.Class == "UsbDriver" && d.Import == "usb") && found.Any(d => d.Class == "GpioDriver"),
            string.Join(", ", found));

        // the plugin of usb: built into the cache once, the second time the same file (not written again)
        PackageDriverAgent.DriverPlugin? plugin = null;
        string error = "";
        try { plugin = PackageDriverAgent.EnsurePlugin("usb"); } catch (Exception ex) { error = CompileErrors.Describe(ex); }
        DateTime written = plugin != null ? File.GetLastWriteTimeUtc(plugin.Path) : default;
        var second = plugin != null ? PackageDriverAgent.EnsurePlugin("usb") : null;
        Check("das Plugin von usb: der Bytecode des Preludes im Cache (mit den Klassen und den Modulen, gegen die es gebaut ist), beim zweiten Mal dieselbe Datei",
            plugin != null && plugin.Classes.Contains("UsbDriver") && plugin.Classes.Contains("Usb.Device") && plugin.Imports.Contains("devices")
            && second?.Path == plugin.Path && File.GetLastWriteTimeUtc(plugin.Path) == written,
            plugin == null ? error : $"{plugin.Path}: {string.Join(",", plugin.Classes.Take(8))} / {string.Join(",", plugin.Imports)}");

        // the agent with the plugin (and a driver the package does not have), then the old way: the same devices, sending and echo
        var withPlugin = AgentRun(true, "Plugin");
        var withImport = AgentRun(false, "#import");
        Check("mit Plugin und mit #import dieselben Geraete", withPlugin.Count > 0 && withPlugin.SequenceEqual(withImport), string.Join(",", withPlugin) + " / " + string.Join(",", withImport));

        // all drivers of the installed packages: each as a plugin, the same drivers and devices as through #import
        var all = AllDrivers(true, out var allPlugins);
        var allImport = AllDrivers(false, out _);
        Check("alle Treiber der Pakete als Plugins (usb, gpio, i2c, spi): dieselben Treiber und Geraete wie mit #import",
            allPlugins.Count == found.Select(d => d.Import).Distinct(StringComparer.OrdinalIgnoreCase).Count() && all.Count > 0 && all.SequenceEqual(allImport),
            $"plugins {string.Join(",", allPlugins)}: {string.Join(",", all)} / {string.Join(",", allImport)}");

        // a cached plugin that no longer fits (built against another interface of `devices`): the agent says it did not load and starts again with `#import "usb"`
        if (plugin != null)
        {
            var good = File.ReadAllBytes(plugin.Path);
            try
            {
                // (the header of the file: magic, length, JSON - the interface of `devices` in it changed)
                int length = BitConverter.ToInt32(good, 8);
                var header = System.Text.Json.Nodes.JsonNode.Parse(good.AsSpan(12, length).ToArray())!;
                foreach (var dep in header["Deps"]!.AsArray()) if ((string?)dep![0] == "pkg:devices") dep[1] = "1:0000";
                var json = System.Text.Encoding.UTF8.GetBytes(header.ToJsonString());
                File.WriteAllBytes(plugin.Path, good.Take(8).Concat(BitConverter.GetBytes(json.Length)).Concat(json).Concat(good.Skip(12 + length)).ToArray());
                var notes = new List<string>();
                using var agent = new PackageDriverAgent();
                agent.Message += m => { lock (notes) notes.Add(m); };
                bool ok = agent.Start(new[] { new PackageDriver("fire-usb", "usb", "UsbDriver") });
                var devices = ok ? agent.ListDevices(refresh: true).Select(d => d.Identifier).ToList() : new List<string>();
                lock (notes)
                    Check("ein Plugin, das nicht mehr passt (andere Schnittstelle von devices): gemeldet, der Agent startet neu mit #import und hat usb:sim",
                        ok && agent.PluginImports.Count == 0 && devices.Contains("usb:sim") && agent.StartErrors.Count == 0 && notes.Any(n => n.Contains("did not load") && n.Contains("devices")),
                        $"{ok} {agent.EndedWith} / {string.Join(",", devices)} / {string.Join(" | ", notes)} / {string.Join(";", agent.StartErrors)}");
            }
            finally { File.WriteAllBytes(plugin.Path, good); }
        }
        return _failures;
    }

    private static List<string> AgentRun(bool plugins, string how)
    {
        using var agent = new PackageDriverAgent { UsePlugins = plugins };
        bool ok = agent.Start(new[] { new PackageDriver("fire-usb", "usb", "UsbDriver"), new PackageDriver("x", "usb", "GibtEsNicht") });
        var manager = new DeviceManager();
        var captured = new List<PacketRecord>();
        string detail = agent.EndedWith ?? "";
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
