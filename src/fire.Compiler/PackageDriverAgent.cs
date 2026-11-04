using fire.Device.Manager.DeviceManager;
using fire.Device.Manager.Drivers;
using fire.Package.Manager;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

namespace fire.Compiler
{
    /// <summary>A device driver that an installed package offers (its manifest lists the class under `drivers`).</summary>
    public sealed record PackageDriver(string Package, string Import, string Class);

    /// <summary>
    /// The device drivers of the installed packages for a host (the device manager of spark): a fire program of its own (a <see cref="LiveSession"/> without a
    /// window) imports the packages, makes every driver with `new Class()` and adds it to its DeviceManager. The host talks to it in lines: a command
    /// `seq\tverb\targs` (list, connect, disconnect, write, test, quit) gets the answer `R\tseq\tok|err\tpayload`; what a connected device sends comes as
    /// `T\tid\t0\thex` (received) and what was written to it as `T\tid\t1\thex`. <see cref="Attach"/> puts a driver per driver name into a C# DeviceManager,
    /// whose devices then stand for the devices of the program.
    /// </summary>
    public sealed class PackageDriverAgent : IDisposable
    {
        private readonly object _lock = new();
        private LiveSession? _session;
        private int _seq;
        private readonly Dictionary<int, string> _replies = new();
        private readonly List<string> _startErrors = new();
        private string? _ended;

        /// <summary>The drivers the agent runs.</summary>
        public IReadOnlyList<PackageDriver> Drivers { get; private set; } = Array.Empty<PackageDriver>();

        /// <summary>A device of the agent sent data (`sent` false) or data was written to it (true); on the thread of the agent.</summary>
        public event Action<string, bool, byte[]>? Traffic;

        /// <summary>A line of the agent that is not part of the protocol (a print of a driver), or a problem making a driver.</summary>
        public event Action<string>? Message;

        /// <summary>Why drivers could not be made (one line each), after <see cref="Start"/>.</summary>
        public IReadOnlyList<string> StartErrors { get { lock (_lock) return _startErrors.ToList(); } }

        /// <summary>The agent ended (null) or failed (the message).</summary>
        public string? EndedWith { get { lock (_lock) return _ended; } }

        public bool IsRunning => _session?.IsRunning == true;

        /// <summary>The drivers the installed packages offer (the `drivers` of their imports).</summary>
        public static List<PackageDriver> FindDrivers(PackageStore? store = null)
        {
            var found = new List<PackageDriver>();
            foreach (var package in (store ?? PackageStore.Default).Installed())
                foreach (var import in package.Manifest.Imports)
                    foreach (var cls in import.Drivers ?? new List<string>())
                        if (!string.IsNullOrWhiteSpace(cls) && !string.IsNullOrWhiteSpace(import.Name))
                            found.Add(new PackageDriver(package.Name, import.Name, cls.Trim()));
            return found;
        }

        /// <summary>The program of the agent for these drivers.</summary>
        public static string BuildScript(IEnumerable<PackageDriver> drivers) => BuildScript(drivers, null);

        /// <summary>The program of the agent: the imports with a plugin (`plugins`, by import) are loaded with `Library.Load` and their drivers made with `Reflect.New`
        /// (the modules the plugin was built against are imported - they must be there for it to load); the others are imported (`new Class()`).</summary>
        public static string BuildScript(IEnumerable<PackageDriver> drivers, IReadOnlyDictionary<string, DriverPlugin>? plugins)
        {
            var list = drivers.ToList();
            bool Plugged(PackageDriver d) => plugins != null && plugins.ContainsKey(d.Import);
            var imports = new List<string> { "devices" };
            foreach (var d in list)
                if (!Plugged(d)) imports.Add(d.Import);
                else { imports.Add("reflection"); imports.AddRange(plugins![d.Import].Imports); }
            var sb = new StringBuilder();
            foreach (var import in imports.Distinct(StringComparer.OrdinalIgnoreCase)) sb.AppendLine($"#import \"{import}\"");
            sb.AppendLine("extern string __HostCommand()");
            sb.AppendLine(AgentClass);
            // the plugins first: one that does not load is reported (`P`, the host starts the agent again with the import instead) and its drivers are not made
            var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in list)
            {
                if (!Plugged(d) || flags.ContainsKey(d.Import)) continue;
                string flag = flags[d.Import] = "pluginLoaded" + flags.Count;
                string path = plugins![d.Import].Path.Replace('\\', '/').Replace("\"", "\\\"");
                sb.AppendLine($"var {flag} = true");
                sb.AppendLine($"try {{ Library.Load(\"{path}\") }} catch (Exception e) {{ {flag} = false; print(\"P\\t{d.Import}\\t\" + DriverAgent.Clean(e.message)) }}");
            }
            foreach (var d in list)
            {
                // a driver that cannot be made is reported and left out; the others go on
                if (Plugged(d))
                    sb.AppendLine($"if ({flags[d.Import]}) {{ try {{ DeviceManager.AddDriver(Reflect.New(\"{plugins![d.Import].ClassOf(d.Class)}\", [])) }} catch (Exception e) {{ print(\"E\\t{d.Class}\\t\" + e.message) }} }}");
                else
                    sb.AppendLine("try { DeviceManager.AddDriver(new " + d.Class + "()) } catch (Exception e) { print(\"E\\t" + d.Class + "\\t\" + e.message) }");
            }
            sb.AppendLine("DriverAgent.Run()");
            return sb.ToString();
        }

        // ---- the drivers of a package as a plugin: its prelude compiled once (bytecode for the VM, cached), loaded by the agent instead of compiling the package again ----

        /// <summary>Whether the agent loads the drivers of packages as plugins (FIRE_DRIVER_PLUGINS=0: always through `#import`).</summary>
        public bool UsePlugins { get; set; } = Environment.GetEnvironmentVariable("FIRE_DRIVER_PLUGINS") != "0";

        /// <summary>The imports whose drivers the running agent loaded as plugins.</summary>
        public IReadOnlyList<string> PluginImports { get; private set; } = Array.Empty<string>();

        /// <summary>The plugin of an import: its bytecode file, the imports the agent needs for it (the modules it was built against) and its classes.</summary>
        public sealed record DriverPlugin(string Import, string Path, List<string> Imports, List<string> Classes)
        {
            /// <summary>The full name of a driver class (the manifest may name it without its namespace); the name itself when the plugin has no such class.</summary>
            public string ClassOf(string name) =>
                Classes.FirstOrDefault(c => c == name) ?? Classes.FirstOrDefault(c => c.EndsWith("." + name, StringComparison.Ordinal)) ?? name;
            public bool Has(string name) => Classes.Contains(ClassOf(name));
        }

        /// <summary>The folder of the cached driver plugins (below the user-wide cache of native libraries, `ember cache clean` empties it too).</summary>
        public static string PluginCache => System.IO.Path.Combine(fire.Package.Manager.NativeLibraryCache.Root, "driver-plugins");

        /// <summary>The plugin of the import `importName` (built now if the cache has none for this fire, package, prelude and the preludes it imports); null: the
        /// import has no prelude. Compile errors and a library of natives that cannot be built are thrown.</summary>
        public static DriverPlugin? EnsurePlugin(string importName, fire.Runtime.VmExecutionMode mode = fire.Runtime.VmExecutionMode.Release, Action<string>? log = null)
        {
            var import = PackageStore.Default.FindImport(importName);
            if (import?.ReadPrelude() is not { } prelude) return null;
            // its natives (C or C++) for the VM: the library of the package, named in the plugin by its full path (the VM binds them when it links the plugin)
            string? library = import.Import.Native is { } native && native.Functions.Any(f => !f.Host) ? PackageLibrary.Ensure(import, log) : null;
            string key = PluginKey(import, prelude, library, mode);
            string dir = System.IO.Path.Combine(PluginCache, key);
            string file = System.IO.Path.Combine(dir, new string(import.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()) + ".fireplugin");
            if (!System.IO.File.Exists(file))
            {
                System.IO.Directory.CreateDirectory(dir);
                string partial = file + "." + Environment.ProcessId + ".partial";
                try
                {
                    NativeBuilder.WriteVmPlugin(partial, "driver-" + import.Name, new[] { prelude }, new string?[] { import.FullPath(import.Import.Prelude!) }, mode, new[] { import.Key },
                        linked => PackageNatives(linked, import, library));
                    System.IO.File.Move(partial, file, true);
                }
                finally { try { System.IO.File.Delete(partial); } catch (System.IO.IOException) { } }
            }
            var plugin = fire.Runtime.PluginBytecode.Read(file) ?? throw new InvalidOperationException($"The cached plugin '{file}' cannot be read.");
            var imports = plugin.Deps.Select(d => d[0]).Where(m => m != "core")
                .Select(m => m.StartsWith(PackageStore.KeyPrefix, StringComparison.Ordinal) ? PackageStore.Default.FindKey(m)?.Name ?? m.Substring(PackageStore.KeyPrefix.Length) : m).ToList();
            return new DriverPlugin(import.Name, file, imports, plugin.Classes);
        }

        /// <summary>The natives of the import itself (the other imports are the agent's): by name, with the full path of its library.</summary>
        private static IEnumerable<(string, string)> PackageNatives(fire.Runtime.LinkedProgram linked, InstalledImport import, string? library)
        {
            if (linked.PackageNatives is not { } names || linked.PackageNativeLibraries is not { } files) yield break;
            string own = PackageLibrary.FileNameFor(import);
            var mine = new HashSet<string>(import.Import.Native?.Functions.Select(f => f.Name) ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            for (int i = 0; i < names.Count && i < files.Count; i++)
                if (mine.Contains(names[i]))
                {
                    if (files[i] == fire.Runtime.PackageNativeBinding.HostLibrary) yield return (names[i], files[i]);
                    else if (files[i] == own && library != null) yield return (names[i], library);
                }
        }

        /// <summary>The key of a cached plugin: the build of fire, the format of plugins, the package, its prelude and those of the imports it reaches, its library, the mode.</summary>
        private static string PluginKey(InstalledImport import, string prelude, string? library, fire.Runtime.VmExecutionMode mode)
        {
            var sb = new StringBuilder();
            sb.Append("driver-plugin 1\n").Append(fire.Runtime.ModuleInterface.Format).Append('\n').Append(mode).Append('\n');
            foreach (var type in new[] { typeof(PackageDriverAgent), typeof(fire.Runtime.VM), typeof(fire.Values.Value), typeof(fire.Standard.Prelude) })
                sb.Append(type.Assembly.ManifestModule.ModuleVersionId).Append('\n');
            sb.Append(fire.Standard.Prelude.Source).Append('\n');
            sb.Append(import.Package.Name).Append('@').Append(import.Package.Version).Append(' ').Append(import.Key).Append(' ').Append(library).Append('\n').Append(prelude).Append('\n');
            // the preludes it imports (and theirs): another interface of one of them is another plugin
            var seen = new HashSet<string>(StringComparer.Ordinal) { import.Key };
            var queue = new Queue<string>(ImportedPreludes.FindImportNames(prelude).Concat(import.Import.Requires));
            while (queue.Count > 0)
            {
                string name = queue.Dequeue();
                string key;
                try { key = ImportedPreludes.ParseImportName(name); } catch (Exception) { sb.Append("missing ").Append(name).Append('\n'); continue; }
                foreach (var with in ImportedPreludes.WithDependencies(key))
                {
                    if (!seen.Add(with)) continue;
                    var text = ImportedPreludes.TrySourceFor(with.StartsWith(PackageStore.KeyPrefix, StringComparison.Ordinal) ? PackageStore.Default.FindKey(with)?.Name ?? with : with);
                    sb.Append(with).Append('\n').Append(text).Append('\n');
                    if (text != null) foreach (var more in ImportedPreludes.FindImportNames(text)) queue.Enqueue(more);
                }
            }
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
        }

        // The loop of the agent: commands of the host, then the connected devices are read.
        private const string AgentClass = """
            class DriverAgent {
                static List listening
                static List lastValues

                static string Hex(any data) {
                    if (data is of int) { return "#" + data }
                    var digits = "0123456789abcdef"
                    var parts = new List()
                    for (int i = 0; i < data.length; i++) {
                        var b = data[i] & 255
                        parts.Add(digits.Substring(b >> 4, 1) + digits.Substring(b & 15, 1))
                    }
                    return "".Join(parts)
                }

                static byte[] Bytes(string hex) {
                    var digits = "0123456789abcdef"
                    var n = hex.Length / 2
                    var data = new byte[n]
                    var low = hex.ToLower()
                    for (int i = 0; i < n; i++) { data[i] = digits.IndexOf(low.Substring(2 * i, 1)) * 16 + digits.IndexOf(low.Substring(2 * i + 1, 1)) }
                    return data
                }

                static string Clean(string text) { return text.Replace("\t", " ").Replace("\n", " ").Replace("\r", " ").Replace("|", "/").Replace("^", " ") }

                static string Describe(Device d) {
                    var c = "0"
                    if (d.Connected()) { c = "1" }
                    return d.Identifier() + "|" + d.Owner().Name() + "|" + d.Name + "|" + c + "|" + d.Availability() + "|" + DriverAgent.Clean(d.Description())
                }

                static string List() {
                    var parts = new List()
                    foreach (driver in DeviceManager.Drivers()) {
                        foreach (d in driver.Devices()) { parts.Add(DriverAgent.Describe(d)) }
                    }
                    return "^".Join(parts)
                }

                static string Do(string verb, List args) {
                    if (verb == "list") { return DriverAgent.List() }
                    if (verb == "drivers") {
                        var names = new List()
                        foreach (driver in DeviceManager.Drivers()) { names.Add(driver.Name()) }
                        return "|".Join(names)
                    }
                    if (verb == "refresh") {
                        foreach (driver in DeviceManager.Drivers()) { driver.Refresh(args[0] == "1") }
                        return DriverAgent.List()
                    }
                    var d = DeviceManager.Get(args[0])
                    if (verb == "connect") {
                        if (!d.Connected() && !d.Connect()) { throw new DeviceConnectionException("Connection to '" + d.Identifier() + "' failed") }
                        if (DriverAgent.listening.IndexOf(d.Identifier()) < 0) {
                            DriverAgent.listening.Add(d.Identifier())
                            DriverAgent.lastValues.Add(undefined)
                        }
                        return DriverAgent.Describe(d)
                    }
                    if (verb == "disconnect") {
                        var at = DriverAgent.listening.IndexOf(d.Identifier())
                        if (at >= 0) {
                            DriverAgent.listening.RemoveAt(at)
                            DriverAgent.lastValues.RemoveAt(at)
                        }
                        d.Disconnect()
                        return DriverAgent.Describe(d)
                    }
                    if (verb == "test") { return "" + d.TestAvailability() }
                    if (verb == "write") {
                        var data = DriverAgent.Bytes(args[1])
                        if (!d.Connected()) { return "0" }
                        var ok = d.Write(data)
                        print("T\t" + d.Identifier() + "\t1\t" + args[1])
                        if (ok == false) { return "0" }
                        return "1"
                    }
                    throw new DeviceArgumentException("Unknown command '" + verb + "'")
                }

                static bool Handle(string command) {
                    var f = command.Split("\t")
                    var seq = f[0]
                    var verb = f[1]
                    if (verb == "quit") {
                        print("R\t" + seq + "\tok\t")
                        return false
                    }
                    var args = new List()
                    for (int i = 2; i < f.length; i++) { args.Add(f[i]) }
                    try { print("R\t" + seq + "\tok\t" + DriverAgent.Do(verb, args)) } catch (Exception e) { print("R\t" + seq + "\terr\t" + DriverAgent.Clean(e.message)) }
                    return true
                }

                // reads the devices the host connected; true if something came
                static bool Poll() {
                    var any = false
                    for (int i = 0; i < DriverAgent.listening.Count; i++) {
                        try {
                            var d = DeviceManager.Get(DriverAgent.listening[i])
                            if (!d.Connected()) { continue }
                            var got = d.Read()
                            if (got == undefined) { continue }
                            if (got is of int) {
                                if (DriverAgent.lastValues[i] != got) {
                                    DriverAgent.lastValues[i] = got
                                    print("T\t" + d.Identifier() + "\t0\t#" + got)
                                }
                            } else if (got.length > 0) {
                                print("T\t" + d.Identifier() + "\t0\t" + DriverAgent.Hex(got))
                                any = true
                            }
                        } catch (Exception e) { }
                    }
                    return any
                }

                static undefined Run() {
                    var l = new List()
                    l.TakeGlobal()
                    DriverAgent.listening = l
                    var v = new List()
                    v.TakeGlobal()
                    DriverAgent.lastValues = v
                    print("ready")
                    while (true) {
                        var command = __HostCommand()
                        while (command != "") {
                            if (!DriverAgent.Handle(command)) { return }
                            command = __HostCommand()
                        }
                        if (!DriverAgent.Poll()) { Sleep(5) }
                    }
                }
            }
            """;

        /// <summary>Compiles and starts the agent for these drivers (default: those of the installed packages) and waits until it is ready. False when there are
        /// none or the program could not start (see <see cref="EndedWith"/>).</summary>
        public bool Start(IEnumerable<PackageDriver>? drivers = null, int timeoutMs = 60000)
        {
            Drivers = (drivers ?? FindDrivers()).ToList();
            if (Drivers.Count == 0) return false;
            // the imports as plugins (compiled once, cached): one that cannot be had (no prelude, a compile error, no library for its natives) goes through `#import`
            var plugins = new Dictionary<string, DriverPlugin>(StringComparer.OrdinalIgnoreCase);
            if (UsePlugins)
                foreach (var import in Drivers.Select(d => d.Import).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try { if (EnsurePlugin(import) is { } plugin) plugins[import] = plugin; }
                    catch (Exception ex) { Message?.Invoke($"note: the drivers of '{import}' are compiled with the agent ({CompileErrors.Describe(ex)})"); }
                }
            // a driver its plugin does not have: reported like one that does not compile
            var known = new List<PackageDriver>();
            foreach (var d in Drivers)
                if (plugins.TryGetValue(d.Import, out var p) && !p.Has(d.Class))
                    lock (_lock) _startErrors.Add($"The driver {d.Class} of {d.Package} could not be made: the import '{d.Import}' has no class '{d.Class}'.");
                else known.Add(d);
            Drivers = known;
            if (Drivers.Count == 0) { lock (_lock) _ended = "no driver compiles"; return false; }
            int before;
            lock (_lock) before = _startErrors.Count;
            bool ok = StartSession(plugins, timeoutMs, out var failed);
            if (failed.Count > 0)
            {
                // a plugin that did not load (another interface of a module than the agent's, a native it cannot have): its import goes the old way, the agent starts anew
                foreach (var (import, why) in failed) { Message?.Invoke($"note: the plugin of '{import}' did not load ({why}); its drivers are compiled with the agent"); plugins.Remove(import); }
                StopSession();
                lock (_lock) { _startErrors.RemoveRange(before, _startErrors.Count - before); _ended = null; _replies.Clear(); }
                ok = StartSession(plugins, timeoutMs, out _);
            }
            PluginImports = ok ? plugins.Keys.Where(i => Drivers.Any(d => string.Equals(d.Import, i, StringComparison.OrdinalIgnoreCase))).OrderBy(i => i, StringComparer.Ordinal).ToList() : Array.Empty<string>();
            return ok;
        }

        private readonly List<(string Import, string Why)> _pluginFailures = new();

        private void StopSession()
        {
            var session = _session;
            if (session == null) return;
            try { if (session.IsRunning) Request("quit"); } catch (InvalidOperationException) { }
            _session = null;
            session.Stop();
        }

        private bool StartSession(Dictionary<string, DriverPlugin> plugins, int timeoutMs, out List<(string Import, string Why)> failedPlugins)
        {
            failedPlugins = new();
            lock (_lock) _pluginFailures.Clear();
            var ready = new ManualResetEventSlim();
            var session = new LiveSession();
            session.Message += line =>
            {
                if (_session != session) return;
                if (line == "ready") { ready.Set(); return; }
                OnLine(line);
            };
            session.Ended += error =>
            {
                if (_session != session) return;
                lock (_lock) { _ended = error ?? "the driver agent ended"; Monitor.PulseAll(_lock); }
                ready.Set();
            };
            _session = session;
            try { session.Start(BuildScript(Drivers, plugins)); }
            catch (Exception)
            {
                // a driver that does not compile (a class that is not there) must not take the others with it: each is tried alone, the bad ones are left out
                var good = new List<PackageDriver>();
                foreach (var d in Drivers)
                {
                    try
                    {
                        var externs = new fire.Bytecode.ExternRegistry();
                        externs.Register("__HostCommand", _ => fire.Values.Value.MakeUndefined());
                        RuntimeSession.Build(new[] { BuildScript(new[] { d }, plugins) }, fire.Runtime.VmExecutionMode.Release, _ => fire.Values.Value.MakeUndefined(), externs: externs);
                        good.Add(d);
                    }
                    catch (Exception ex) { lock (_lock) _startErrors.Add($"The driver {d.Class} of {d.Package} could not be made: {CompileErrors.Describe(ex)}"); }
                }
                Drivers = good;
                if (good.Count == 0) { lock (_lock) _ended = "no driver compiles"; return false; }
                try { session.Start(BuildScript(good, plugins)); }
                catch (Exception ex)
                {
                    lock (_lock) _ended = CompileErrors.Describe(ex);
                    return false;
                }
            }
            ready.Wait(timeoutMs);
            lock (_lock) failedPlugins.AddRange(_pluginFailures);
            return EndedWith == null && ready.IsSet;
        }

        private void OnLine(string line)
        {
            if (line.StartsWith("R\t", StringComparison.Ordinal))
            {
                int a = line.IndexOf('\t', 2);
                if (a > 0 && int.TryParse(line.AsSpan(2, a - 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seq))
                    lock (_lock) { _replies[seq] = line.Substring(a + 1); Monitor.PulseAll(_lock); }
                return;
            }
            if (line.StartsWith("T\t", StringComparison.Ordinal))
            {
                var f = line.Split('\t');
                if (f.Length >= 4) Traffic?.Invoke(f[1], f[2] == "1", Decode(f[3]));
                return;
            }
            if (line.StartsWith("P\t", StringComparison.Ordinal))
            {
                var f = line.Split('\t', 3);
                lock (_lock) _pluginFailures.Add((f[1], f.Length > 2 ? f[2] : ""));
                return;
            }
            if (line.StartsWith("E\t", StringComparison.Ordinal))
            {
                var f = line.Split('\t', 3);
                string text = $"The driver {f[1]} could not be made: {(f.Length > 2 ? f[2] : "")}";
                lock (_lock) _startErrors.Add(text);
                Message?.Invoke(text);
                return;
            }
            Message?.Invoke(line);
        }

        /// <summary>The bytes of a hex text of the agent; a value (`#5`, a GPIO port) becomes its text.</summary>
        public static byte[] Decode(string hex)
        {
            if (hex.StartsWith('#')) return Encoding.ASCII.GetBytes(hex.Substring(1));
            var data = new byte[hex.Length / 2];
            for (int i = 0; i < data.Length; i++) data[i] = byte.Parse(hex.AsSpan(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return data;
        }

        /// <summary>Sends a command and waits for its answer: the payload, or throws <see cref="InvalidOperationException"/> with the error of the agent.</summary>
        public string Request(string verb, params string[] args)
        {
            var session = _session ?? throw new InvalidOperationException("The driver agent is not running.");
            int seq;
            lock (_lock) seq = ++_seq;
            session.Send(seq.ToString(CultureInfo.InvariantCulture) + "\t" + verb + (args.Length > 0 ? "\t" + string.Join("\t", args) : ""));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            lock (_lock)
            {
                while (!_replies.ContainsKey(seq))
                {
                    if (_ended != null) throw new InvalidOperationException("The driver agent is not running: " + _ended);
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) throw new InvalidOperationException($"The driver agent did not answer '{verb}'.");
                    Monitor.Wait(_lock, left);
                }
                string reply = _replies[seq];
                _replies.Remove(seq);
                int tab = reply.IndexOf('\t');
                string status = tab < 0 ? reply : reply.Substring(0, tab), payload = tab < 0 ? "" : reply.Substring(tab + 1);
                if (status != "ok") throw new InvalidOperationException(payload);
                return payload;
            }
        }

        /// <summary>A device as the agent describes it.</summary>
        public sealed record RemoteDevice(string Identifier, string Driver, string Name, bool Connected, int Availability, string Description);

        public static RemoteDevice? ParseDevice(string text)
        {
            var f = text.Split('|');
            if (f.Length < 6) return null;
            return new RemoteDevice(f[0], f[1], f[2], f[3] == "1", int.TryParse(f[4], out int a) ? a : 1, f[5]);
        }

        private static List<RemoteDevice> ParseList(string payload) =>
            payload.Length == 0 ? new() : payload.Split('^').Select(ParseDevice).Where(d => d != null).Select(d => d!).ToList();

        /// <summary>The devices of all drivers (`refresh`: the drivers look again first, `fastScan` without testing each).</summary>
        public List<RemoteDevice> ListDevices(bool refresh = false, bool fastScan = true) =>
            ParseList(refresh ? Request("refresh", fastScan ? "1" : "0") : Request("list"));

        /// <summary>The names of the drivers the agent made.</summary>
        public List<string> DriverNames() { var p = Request("drivers"); return p.Length == 0 ? new() : p.Split('|').ToList(); }

        /// <summary>Puts a driver per driver name of the agent into `manager` (its devices stand for the devices of the agent) and looks for the devices.</summary>
        public void Attach(DeviceManager manager)
        {
            foreach (var name in DriverNames())
                if (!manager.GetDriverIdentifiers().Contains(name)) manager.RegisterDriver(new AgentDriver(this, name));
        }

        /// <summary>Ends the agent (it disconnects nothing on its own: the drivers' devices close with the program).</summary>
        public void Dispose() => StopSession();

        // ---- the proxies in the device manager of the host ----

        private sealed class AgentDriver : IDriver
        {
            private readonly PackageDriverAgent _agent;
            public AgentDriver(PackageDriverAgent agent, string name) { _agent = agent; Identifier = name; }
            public string Identifier { get; }

            public IEnumerable<IDevice> EnumerateDevices()
            {
                List<RemoteDevice> all;
                try { all = _agent.ListDevices(refresh: true); }
                catch (InvalidOperationException) { return Array.Empty<IDevice>(); }
                return all.Where(d => d.Driver == Identifier).Select(d => (IDevice)new AgentDevice(_agent, d)).ToList();
            }
        }

        private sealed class AgentDevice : IDevice
        {
            private readonly PackageDriverAgent _agent;
            private readonly string _id;
            private bool _connected;
            private DeviceAvailability _availability;
            private bool _disposed;

            public AgentDevice(PackageDriverAgent agent, RemoteDevice d)
            {
                _agent = agent;
                _id = d.Identifier;
                PortName = d.Name;
                Description = d.Description.Length > 0 ? d.Description : null;
                _connected = d.Connected;
                _availability = (DeviceAvailability)Math.Clamp(d.Availability, 0, 2);
                agent.Traffic += OnTraffic;
            }

            private void OnTraffic(string id, bool sent, byte[] data)
            {
                if (_disposed || id != _id) return;
                if (sent) OnRawDataSent?.Invoke(data); else OnRawDataReceived?.Invoke(data);
            }

            public DeviceAvailability Availability => _availability;
            public bool IsConnected => _connected;
            public string? PortName { get; }
            public string? Description { get; }
            public Action<byte[]>? OnRawDataReceived { get; set; }
            public Action<byte[]>? OnRawDataSent { get; set; }
            public event Action? StateChanged;

            private void Update(string payload)
            {
                var d = ParseDevice(payload);
                if (d == null) return;
                _connected = d.Connected;
                _availability = (DeviceAvailability)Math.Clamp(d.Availability, 0, 2);
                StateChanged?.Invoke();
            }

            public DeviceAvailability TestAvailability()
            {
                try { _availability = (DeviceAvailability)Math.Clamp(int.Parse(_agent.Request("test", _id), CultureInfo.InvariantCulture), 0, 2); }
                catch (Exception) { _availability = DeviceAvailability.Unavailable; }
                StateChanged?.Invoke();
                return _availability;
            }

            public void Connect() => Update(_agent.Request("connect", _id));
            public void Disconnect() => Update(_agent.Request("disconnect", _id));

            public bool SendCommand(string command) => Write(Encoding.Latin1.GetBytes(command + "\n"));

            public bool Write(byte[] data)
            {
                if (!_connected) return false;
                return _agent.Request("write", _id, Convert.ToHexString(data)) == "1";
            }

            // the device manager makes the proxies anew on every search and drops the new ones it knows already: that must not disconnect the device
            public void Dispose()
            {
                _disposed = true;
                _agent.Traffic -= OnTraffic;
            }
        }
    }
}
