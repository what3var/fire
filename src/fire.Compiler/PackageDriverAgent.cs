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
        public static string BuildScript(IEnumerable<PackageDriver> drivers)
        {
            var list = drivers.ToList();
            var sb = new StringBuilder();
            sb.AppendLine("#import \"devices\"");
            foreach (var import in list.Select(d => d.Import).Distinct(StringComparer.OrdinalIgnoreCase)) sb.AppendLine($"#import \"{import}\"");
            sb.AppendLine("extern string __HostCommand()");
            sb.AppendLine(AgentClass);
            foreach (var d in list)
            {
                // a driver that cannot be made is reported and left out; the others go on
                sb.AppendLine("try { DeviceManager.AddDriver(new " + d.Class + "()) } catch (Exception e) { print(\"E\\t" + d.Class + "\\t\" + e.message) }");
            }
            sb.AppendLine("DriverAgent.Run()");
            return sb.ToString();
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
                    for (int i = 0; i < DriverAgent.listening.count; i++) {
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
            var ready = new ManualResetEventSlim();
            var session = new LiveSession();
            session.Message += line =>
            {
                if (line == "ready") { ready.Set(); return; }
                OnLine(line);
            };
            session.Ended += error =>
            {
                lock (_lock) { _ended = error ?? "the driver agent ended"; Monitor.PulseAll(_lock); }
                ready.Set();
            };
            _session = session;
            try { session.Start(BuildScript(Drivers)); }
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
                        RuntimeSession.Build(new[] { BuildScript(new[] { d }) }, fire.Runtime.VmExecutionMode.Release, _ => fire.Values.Value.MakeUndefined(), externs: externs);
                        good.Add(d);
                    }
                    catch (Exception ex) { lock (_lock) _startErrors.Add($"The driver {d.Class} of {d.Package} could not be made: {CompileErrors.Describe(ex)}"); }
                }
                Drivers = good;
                if (good.Count == 0) { lock (_lock) _ended = "no driver compiles"; return false; }
                try { session.Start(BuildScript(good)); }
                catch (Exception ex)
                {
                    lock (_lock) _ended = CompileErrors.Describe(ex);
                    return false;
                }
            }
            ready.Wait(timeoutMs);
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
        public void Dispose()
        {
            var session = _session;
            if (session == null) return;
            try { if (session.IsRunning) Request("quit"); } catch (InvalidOperationException) { }
            session.Stop();
            _session = null;
        }

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
