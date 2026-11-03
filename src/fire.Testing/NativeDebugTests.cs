using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using fire.Bytecode;
using fire.Compiler;
using fire.Debugging;
using fire.Runtime;
using fire.Values;

/// <summary>
/// The debugger of native debug builds (docs/NATIVE_DEBUG_PROTOCOL.md): a client that launches a native program and drives it over the protocol, the same
/// way spark drives the VM. The wire is one of spark's transports (fire.Debugging): TCP (FIRE_DEBUG=tcp:..., <see cref="Launch"/>), framed over the
/// program's stdio (FIRE_DEBUG=stdio, the FreeRTOS simulator: <see cref="LaunchStdio"/>), or framed over any stream (QEMU's serial port: <see cref="Attach"/>).
/// </summary>
sealed class NativeDebugClient : IDisposable
{
    private readonly TcpListener? _listener;
    private readonly Process? _process;
    private INativeDebugTransport? _transport;
    private readonly BlockingCollection<JsonElement> _replies = new();
    private readonly BlockingCollection<JsonElement> _events = new();
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private int _nextId = 1;
    public JsonElement Hello { get; private set; }
    /// <summary>The program's output: its stdout (TCP), or the console between the frames (framed).</summary>
    public string Output { get { lock (_stdout) return _stdout.ToString(); } }
    public string Errors { get { lock (_stderr) return _stderr.ToString(); } }
    public int? ExitCode => _process is { HasExited: true } ? _process.ExitCode : null;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private NativeDebugClient(string exe, string workDir, string? launcher, IDictionary<string, string>? environment, string? fireDebug, bool stdio)
    {
        var start = new ProcessStartInfo(launcher ?? exe) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdio, UseShellExecute = false, WorkingDirectory = workDir };
        if (launcher != null) start.ArgumentList.Add(exe);
        if (environment != null) foreach (var (k, v) in environment) start.Environment[k] = v;
        if (!stdio)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            fireDebug = fireDebug!.Replace("PORT", ((IPEndPoint)_listener.LocalEndpoint).Port.ToString());
        }
        if (fireDebug != null) start.Environment["FIRE_DEBUG"] = fireDebug;
        _process = Process.Start(start)!;
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) _stderr.Append(e.Data).Append('\n'); };
        _process.BeginErrorReadLine();
        if (stdio) _transport = new FramedDebugTransport(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream) { Console = ConsoleLine };
        else
        {
            _process.OutputDataReceived += (_, e) => { if (e.Data != null) ConsoleLine(e.Data); };
            _process.BeginOutputReadLine();
        }
    }

    private NativeDebugClient(INativeDebugTransport transport)
    {
        _transport = transport;
        transport.Console = ConsoleLine;
    }

    private void ConsoleLine(string line) { lock (_stdout) _stdout.Append(line).Append('\n'); }

    /// <summary>Starts the program with FIRE_DEBUG=tcp:... and waits for its hello.</summary>
    public static NativeDebugClient Launch(string exe, string workDir, string? launcher = null, IDictionary<string, string>? environment = null)
    {
        string token = Guid.NewGuid().ToString("N");
        var c = new NativeDebugClient(exe, workDir, launcher, environment, $"tcp:127.0.0.1:PORT;token={token}", stdio: false);
        var accept = c._listener!.AcceptTcpClientAsync();
        if (!accept.Wait(Timeout)) throw new TimeoutException("the program did not connect: " + c.Errors);
        var client = accept.Result;
        client.NoDelay = true;
        c._transport = new TcpDebugTransport(client);
        c.Start(token);
        return c;
    }

    /// <summary>Starts the program with its stdio as the framed transport: FIRE_DEBUG=<paramref name="fireDebug"/> (null: none - a build whose transport is stdio,
    /// like the FreeRTOS simulator), and waits for its hello.</summary>
    public static NativeDebugClient LaunchStdio(string exe, string workDir, string? fireDebug = "stdio;token=TOKEN")
    {
        string token = Guid.NewGuid().ToString("N");
        var c = new NativeDebugClient(exe, workDir, null, null, fireDebug?.Replace("TOKEN", token), stdio: true);
        c.Start(fireDebug != null && fireDebug.Contains("TOKEN") ? token : null);
        return c;
    }

    /// <summary>A program on the other end of a framed transport (QEMU's serial port, a board): waits for its hello (the console before it is in <see cref="Output"/>).</summary>
    public static NativeDebugClient Attach(INativeDebugTransport transport)
    {
        var c = new NativeDebugClient(transport);
        c.Start(null);
        return c;
    }

    private void Start(string? token)
    {
        var transport = _transport!;
        new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = transport.ReadMessage()) != null)
                {
                    var e = JsonDocument.Parse(line).RootElement.Clone();
                    if (e.TryGetProperty("re", out _)) _replies.Add(e); else _events.Add(e);
                }
            }
            catch (Exception) { }
            _replies.CompleteAdding();
            _events.CompleteAdding();
        }) { IsBackground = true }.Start();
        Hello = NextEvent("hello");
        if (token != null && Hello.GetProperty("token").GetString() != token) throw new InvalidOperationException("wrong token");
    }

    /// <summary>The next event (skipping none: an unexpected one is an error).</summary>
    public JsonElement NextEvent(string? name = null)
    {
        if (!_events.TryTake(out var e, Timeout)) throw new TimeoutException($"no event ({name}); stderr: {Errors}; output: {Output}");
        if (name != null && e.GetProperty("event").GetString() != name) throw new InvalidOperationException($"expected {name}, got {e}");
        return e;
    }

    /// <summary>The next event that is not of the kinds <paramref name="skip"/>.</summary>
    public JsonElement NextEventExcept(params string[] skip)
    {
        while (true)
        {
            var e = NextEvent();
            if (!skip.Contains(e.GetProperty("event").GetString())) return e;
        }
    }

    public JsonElement Request(string cmd, Dictionary<string, object?>? args = null)
    {
        int id = _nextId++;
        var message = new Dictionary<string, object?> { ["id"] = id, ["cmd"] = cmd };
        if (args != null) foreach (var (k, v) in args) message[k] = v;
        _transport!.WriteMessage(JsonSerializer.Serialize(message));
        if (!_replies.TryTake(out var reply, Timeout)) throw new TimeoutException($"no reply to {cmd}; stderr: {Errors}");
        if (reply.GetProperty("re").GetInt32() != id) throw new InvalidOperationException($"reply {reply} for {id}");
        return reply;
    }

    /// <summary>A line for the program's standard input (a framed transport).</summary>
    public bool SendInput(string line) => _transport!.WriteInput(line);

    /// <summary>Raw bytes onto the wire of a framed transport (a garbled frame, ...): through its stream.</summary>
    public void SendRaw(byte[] bytes)
    {
        var s = _process!.StandardInput.BaseStream;
        s.Write(bytes);
        s.Flush();
    }

    public static bool Ok(JsonElement reply) => reply.GetProperty("ok").GetBoolean();

    public bool WaitExit(TimeSpan timeout) => _process == null || _process.WaitForExit((int)timeout.TotalMilliseconds);

    public void Dispose()
    {
        try { if (_process != null && !_process.HasExited) _process.Kill(); } catch (Exception) { }
        _process?.WaitForExit(5000);
        _transport?.Dispose();
        _listener?.Stop();
    }
}


static class NativeDebugTests
{
    /// <summary>One step of a debugger run: a command and what to look at afterwards.</summary>
    enum Op { Continue, StepInto, StepOver, StepOut, Resume }

    sealed record Case(string Name, string Script, int[] Breakpoints, Op[] Plan, bool StepAll = true);

    static readonly Case[] Cases =
    {
        new("Schleifen und Aufrufe", """
            class U {
                static int Sq(int x) {
                    var y = x * x
                    return y
                }
            }
            var total = 0
            for (var i = 0; i < 3; i = i + 1) {
                total = total + U.Sq(i)
            }
            var k = 0
            while (k < 2) {
                k = k + 1
            }
            foreach (x in [5, 6]) {
                total = total + x
            }
            print("fertig " + total)
            """, new[] { 3, 9, 14 }, new[] { Op.Continue, Op.Continue, Op.StepOver, Op.StepInto, Op.StepOut, Op.Continue, Op.Continue, Op.Continue, Op.Continue, Op.Continue, Op.Continue }),
        new("Rekursion", """
            class R {
                static int Fib(int n) {
                    if (n < 2) {
                        return n
                    }
                    return R.Fib(n - 1) + R.Fib(n - 2)
                }
            }
            print(R.Fib(5))
            """, new[] { 4 }, new[] { Op.Continue, Op.Continue, Op.StepOut, Op.StepOut, Op.Continue, Op.Continue, Op.Continue }),
        new("Objekte, this und Felder", """
            class Point {
                int x
                int y
                construct(int x, int y) {
                    this.x = x
                    this.y = y
                }
                int Sum() {
                    var s = this.x + this.y
                    return s
                }
            }
            var p = new Point(3, 4)
            var q = new Point(10, 20)
            print(p.Sum() + q.Sum())
            """, new[] { 9, 5 }, new[] { Op.Continue, Op.StepOver, Op.Continue, Op.Continue, Op.StepInto, Op.Continue }),
        new("Properties (in der VM verschachtelt)", """
            class Circle {
                float radius
                construct(float radius) {
                    this.radius = radius
                }
                float Diameter {
                    get { return this.radius * 2 }
                    set { this.radius = value / 2 }
                }
            }
            var c = new Circle(2.0)
            var d = c.Diameter
            c.Diameter = 10.0
            print(d + c.radius)
            """, new[] { 7, 12 }, new[] { Op.Continue, Op.StepInto, Op.StepInto, Op.Continue }),
        new("Destruktoren (in der VM verschachtelt)", """
            class Res {
                string name
                construct(string name) {
                    this.name = name
                }
                destruct() {
                    print("weg " + this.name)
                }
            }
            {
                var a = new Res("a")
                print("innen")
            }
            print("aussen")
            """, new[] { 7, 12 }, new[] { Op.Continue, Op.StepOver, Op.StepOver, Op.Continue }),
        new("Lambdas mit Captures", """
            var factor = 3
            var times = func (x) => { return x * factor }
            var add = func (a, b) => {
                var s = a + b
                return s
            }
            print(times(4) + add(1, 2))
            """, new[] { 4 }, new[] { Op.Continue, Op.StepOver, Op.StepOut, Op.Continue }),
        new("try/catch", """
            class E : Exception {
                construct(string message) : base(message) { }
            }
            class T {
                static int Risky(int n) {
                    if (n > 1) {
                        throw new E("zu gross " + n)
                    }
                    return n
                }
            }
            var sum = 0
            for (var i = 0; i < 4; i = i + 1) {
                try {
                    sum = sum + T.Risky(i)
                } catch (E e) {
                    print("gefangen " + e.message)
                    sum = sum + 100
                }
            }
            print(sum)
            """, new[] { 17, 7 }, new[] { Op.Continue, Op.StepInto, Op.StepInto, Op.Continue, Op.StepOver, Op.Continue, Op.Continue, Op.Continue }),
        new("finally", """
            class F {
                static int Work(int n) {
                    try {
                        if (n == 1) {
                            throw new Exception("eins")
                        }
                        return n * 10
                    } finally {
                        print("finally " + n)
                    }
                }
            }
            for (var i = 0; i < 3; i = i + 1) {
                try {
                    print(F.Work(i))
                } catch (e) {
                    print("fehler " + e.message)
                }
            }
            """, new[] { 9 }, new[] { Op.Continue, Op.StepOver, Op.Continue, Op.Continue, Op.Continue }),
        new("resume", """
            class Calc {
                static int Div(int a, int b) {
                    if (b == 0) {
                        var r = throw new Exception("null")
                        return r
                    }
                    return a / b
                }
            }
            var total = 0
            try {
                total = Calc.Div(10, 2) + Calc.Div(1, 0)
            } catch (e) {
                print("gefangen")
                e.resume(7)
            }
            print(total)
            """, new[] { 14, 5 }, new[] { Op.Continue, Op.StepInto, Op.Continue, Op.Continue }),
        new("try/catch in einer Methode (Frame mit this)", """
            class Acc {
                int total
                construct() {
                    this.total = 0
                }
                Add(int v) {
                    try {
                        if (v < 0) {
                            throw new ArgumentException("negativ")
                        }
                        this.total = this.total + v
                    } catch (e) {
                        print("fehler " + e.message)
                    }
                    return this.total
                }
            }
            var a = new Acc()
            a.Add(2)
            a.Add(-1)
            print(a.Add(3))
            """, new[] { 12, 14 }, new[] { Op.Continue, Op.StepOver, Op.Continue, Op.StepInto, Op.StepOut, Op.Continue, Op.Continue }),
        new("Strings", """
            var s = "abc"
            var t = s + "def"
            var n = t.Length
            var u = t.ToUpper()
            var parts = "a,b,c".Split(",")
            print(u + " " + n + " " + parts.Length)
            """, new[] { 4, 6 }, new[] { Op.Continue, Op.StepOver, Op.Continue }),
        new("Einheiten", """
            var a = 5mm
            var b = 2cm
            var c = a + b
            var d = 3.5kg
            print(c)
            print(d)
            """, new[] { 3, 5 }, new[] { Op.Continue, Op.Continue, Op.Continue }),
        new("Arrays", """
            var xs = [1, 2, 3]
            var grid = [[1, 2], [3]]
            xs[1] = 20
            var n = 0
            foreach (row in grid) {
                n = n + row.Length
            }
            print(xs[1] + n)
            """, new[] { 6, 8 }, new[] { Op.Continue, Op.Continue, Op.Continue, Op.Continue }),
        new("Statische Felder und Methoden", """
            class Counter {
                static int count = 0
                static Inc() {
                    Counter.count = Counter.count + 1
                }
            }
            Counter.Inc()
            Counter.Inc()
            print(Counter.count)
            """, new[] { 4 }, new[] { Op.Continue, Op.StepOut, Op.Continue, Op.Continue }),
        new("Vererbung und virtuelle Aufrufe", """
            class Animal {
                string Sound() { return "..." }
                string Speak() {
                    var s = this.Sound()
                    return s + "!"
                }
            }
            class Dog : Animal {
                string Sound() { return "wuff" }
            }
            var animals = [new Animal(), new Dog()]
            foreach (a in animals) {
                print(a.Speak())
            }
            """, new[] { 4, 9 }, new[] { Op.Continue, Op.StepInto, Op.Continue, Op.Continue, Op.StepOver, Op.Continue, Op.Continue }),
        new("Operatoren und ToString (in der VM verschachtelt)", """
            class V {
                int x
                construct(int x) {
                    this.x = x
                }
                operator+(class o) {
                    return new V(this.x + o.x)
                }
                string ToString() {
                    return "V(" + this.x + ")"
                }
            }
            var a = new V(1)
            var b = new V(2)
            var c = a + b
            print("c = " + c)
            print(c.ToString())
            """, new[] { 7, 10, 16 }, new[] { Op.Continue, Op.StepInto, Op.Continue, Op.Continue }),
        new("Standardwerte von Parametern", """
            class G {
                static string Greet(string name, string greeting = "Hallo") {
                    var text = greeting + " " + name
                    return text
                }
            }
            print(G.Greet("Ada"))
            print(G.Greet("Bob", "Hi"))
            """, new[] { 3 }, new[] { Op.Continue, Op.Continue, Op.Continue }),
        new("Blocke, if/else, break/continue", """
            var x = 1
            var log = ""
            for (var i = 0; i < 6; i = i + 1) {
                if (i == 1) {
                    continue
                }
                if (i == 4) {
                    break
                }
                var y = i * 2
                if (y > 2) {
                    var z = y + x
                    log = log + z
                } else {
                    log = log + "-"
                }
            }
            print(log)
            """, new[] { 12, 15 }, new[] { Op.Continue, Op.Continue, Op.Continue, Op.Continue }),
        new("Listen (Prelude)", """
            var list = new List<int>()
            list.Add(3)
            list.Add(4)
            var sum = 0
            foreach (v in list) {
                sum = sum + v
            }
            print(sum + " " + list.Count)
            """, new[] { 6 }, new[] { Op.Continue, Op.StepOver, Op.Continue, Op.Continue }),
        new("Haltepunkt beim Rueckkehren in dieselbe Zeile", """
            class H {
                static int One() {
                    return 1
                }
            }
            var a = H.One() + H.One()
            print(a)
            """, new[] { 6, 3 }, new[] { Op.Continue, Op.Continue, Op.Continue, Op.Continue, Op.Continue }),
        new("unbehandelte Ausnahme: anhalten und ignorieren (resume)", """
            class W {
                static int Check(int n) {
                    if (n > 2) {
                        throw new ArgumentException("zu gross: " + n)
                    }
                    return n
                }
            }
            var total = 0
            for (var i = 0; i < 5; i = i + 1) {
                total = total + W.Check(i)
            }
            print("total " + total)
            """, Array.Empty<int>(), new[] { Op.Continue, Op.Resume, Op.StepOver, Op.Continue, Op.Resume, Op.Continue }),
        new("unbehandelte Ausnahme: anhalten, dann endet das Programm", """
            var xs = [1, 2]
            print("vorher")
            var v = xs[5]
            print("nachher")
            """, Array.Empty<int>(), new[] { Op.Continue, Op.Continue }),
    };

    /// <summary>What a stop shows: the position, the depth, the call stack, the scopes, `this`, the globals (one line each).</summary>
    sealed record Stop(string Text);

    static string Loc(int src, int line, int firstUser) => src >= firstUser ? $"{line}" : $"p{src}:{line}";

    static string VmDisplay(Value v) => v.Kind switch
    {
        ValueKind.Class => ((ObjectInstance)v.AsObjectRef()).ClassName,
        ValueKind.Array => $"Array[{v.AsArray().Length}]",
        _ => v.ToString(),
    };

    static string NatDisplay(JsonElement v) => v.GetProperty("display").GetString()!;

    static readonly object VmLock = new();

    /// <summary>The VM: runs the plan (or every step), the stops as text. (One at a time: `terminate` and the handlers are state of the process.)</summary>
    static (List<string> Stops, List<string> Output) RunVm(string script, int[] bps, IEnumerable<Op> plan, bool details, Func<Op?>? stepAll = null)
    {
        lock (VmLock) return RunVmLocked(script, bps, plan, details, stepAll);
    }

    static (List<string> Stops, List<string> Output) RunVmLocked(string script, int[] bps, IEnumerable<Op> plan, bool details, Func<Op?>? stepAll)
    {
        VM.ResetTerminateForTests();
        GlobalHandlers.ResetForTests();
        var lines = new List<string>();
        var session = RuntimeSession.Build(new[] { script }, VmExecutionMode.Debug, args => { lock (lines) lines.Add(args[0].ToString()); return Value.MakeUndefined(); });
        var vm = session.VirtualMachine!;
        vm.CaptureErrorTrace = true;
        vm.PauseOnUnhandled = true;
        int first = session.FirstUserSourceIndex;
        var set = new HashSet<(int, int)>(bps.Select(l => (first, l)));
        var stops = new List<string>();
        string Describe(string reason)
        {
            if (reason == "unhandled" && vm.UnhandledTrace is { Count: > 0 } thrown)
            {
                // (paused at the throw: the position and the call stack are those of the throw site)
                string message = "";
                foreach (var (fieldName, fieldValue) in vm.UnhandledException!.Fields) if (fieldName == "message") message = fieldValue.ToString();
                var top = thrown[0];
                return $"unhandled {Loc(top.SourceIndex, top.Line, first)} d{vm.DebugCallDepth}" + (details ? $" trace[{string.Join(",", thrown.Select(t => Loc(t.SourceIndex, t.Line, first)))}] exc={vm.UnhandledException.ClassName}:{message}" : "");
            }
            var (src, line) = vm.CurrentLocation;
            var sb = new StringBuilder($"{reason} {Loc(src, line, first)} d{vm.DebugCallDepth}");
            if (!details || src < first) return sb.ToString();
            sb.Append(" trace[").Append(string.Join(",", vm.CurrentTrace().Select(t => Loc(t.SourceIndex, t.Line, first)))).Append(']');
            foreach (var level in vm.DebugScopeChain())
                sb.Append(" {").Append(level.Depth).Append(level.IsFunctionTopLevel ? "T" : "").Append(':').Append(string.Join(",", level.Variables.Select(v => $"{v.Name}={VmDisplay(v.Value)}"))).Append('}');
            sb.Append(" this=").Append(vm.DebugThisValue is Value t ? VmDisplay(t) : "-");
            sb.Append(" g[").Append(string.Join(",", vm.DebugGlobals().Select(g => $"{g.Name}={VmDisplay(g.Value)}"))).Append(']');
            if (vm.IsPausedOnUnhandled && vm.UnhandledException is { } ex)
            {
                string message = "";
                foreach (var (fieldName, fieldValue) in ex.Fields) if (fieldName == "message") message = fieldValue.ToString();
                sb.Append(" exc=").Append(ex.ClassName).Append(':').Append(message);
            }
            return sb.ToString();
        }
        bool Do(Op op) => op switch
        {
            Op.Continue => vm.RunUntilBreakpoint(set, () => false),
            Op.StepInto => vm.StepInto(),
            Op.StepOver => vm.StepLine(),
            Op.StepOut => vm.StepOut(),
            _ => vm.TryResumeUnhandled(),
        };
        int guard = 0;
        foreach (var op in stepAll != null ? Repeat(stepAll) : plan)
        {
            if (op == Op.Resume)
            {
                stops.Add(vm.TryResumeUnhandled() ? "resumed" : "no-resume");
                continue;
            }
            if (op == Op.StepOut && vm.DebugCallDepth == 0) { stops.Add("out-at-0"); continue; }
            // (stopped at an unhandled exception that is not ignored: the thread is finished for the debugger - natively the exception goes on and ends the program)
            if (vm.IsPausedOnUnhandled) { stops.Add("end"); break; }
            bool more = Do(op);
            if (vm.IsPausedOnUnhandled) stops.Add(Describe("unhandled"));
            else if (!more) { stops.Add("end"); break; }
            else stops.Add(Describe(op == Op.Continue ? "bp" : "step"));
            if (++guard > 5000) { stops.Add("too many"); break; }
        }
        if (!vm.IsHalted && !vm.IsPausedOnUnhandled && (stops.Count == 0 || stops[^1] != "end"))
        {
            // the rest of the plan ran out: run to the end
            vm.RunUntilEnd(() => false);
        }
        VM.ResetTerminateForTests();
        lock (lines) return (stops, lines.ToList());
    }

    static IEnumerable<Op> Repeat(Func<Op?> next) { while (next() is Op op) yield return op; }

    /// <summary>The native program: the same plan over the protocol.</summary>
    static (List<string> Stops, List<string> Output, string Errors) RunNative(string exe, string dir, int[] bps, IEnumerable<Op> plan, bool details, Func<Op?>? stepAll = null) =>
        RunNative(() => NativeDebugClient.Launch(exe, dir), bps, plan, details, stepAll);

    /// <summary>The same over a client the caller makes (another transport: stdio, the FreeRTOS simulator, QEMU).</summary>
    static (List<string> Stops, List<string> Output, string Errors) RunNative(Func<NativeDebugClient> launch, int[] bps, IEnumerable<Op> plan, bool details, Func<Op?>? stepAll = null)
    {
        using var c = launch();
        int first = c.Hello.GetProperty("firstUserSource").GetInt32();
        int main = c.Hello.GetProperty("mainThread").GetInt32();
        var entry = c.NextEvent("stopped");
        var stops = new List<string>();
        c.Request("breakpoints", new() { ["breakpoints"] = bps.Select(l => new[] { first, l }).ToArray() });
        JsonElement? lastStop = entry;
        string Describe(string reason, JsonElement stop)
        {
            int src = stop.GetProperty("source").GetInt32(), line = stop.GetProperty("line").GetInt32();
            var sb = new StringBuilder($"{reason} {Loc(src, line, first)} d{stop.GetProperty("depth").GetInt32()}");
            if (!details || (src < first && reason != "unhandled")) return sb.ToString();
            var trace = c.Request("trace", new() { ["thread"] = main });
            sb.Append(" trace[").Append(string.Join(",", trace.GetProperty("frames").EnumerateArray().Select(f => Loc(f.GetProperty("source").GetInt32(), f.GetProperty("line").GetInt32(), first)))).Append(']');
            if (reason == "unhandled")
            {
                var thrown = stop.GetProperty("exception");
                return sb.Append(" exc=").Append(thrown.GetProperty("class").GetString()).Append(':').Append(thrown.TryGetProperty("message", out var tm) ? tm.GetString() : "").ToString();
            }
            var scopes = c.Request("scopes", new() { ["thread"] = main });
            foreach (var level in scopes.GetProperty("scopes").EnumerateArray())
                sb.Append(" {").Append(level.GetProperty("depth").GetInt32()).Append(level.GetProperty("functionTop").GetBoolean() ? "T" : "").Append(':')
                    .Append(string.Join(",", level.GetProperty("variables").EnumerateArray().Select(v => $"{v.GetProperty("name").GetString()}={NatDisplay(v)}"))).Append('}');
            var self = c.Request("this", new() { ["thread"] = main }).GetProperty("value");
            sb.Append(" this=").Append(self.ValueKind == JsonValueKind.Null ? "-" : NatDisplay(self));
            var globals = c.Request("globals");
            sb.Append(" g[").Append(string.Join(",", globals.GetProperty("variables").EnumerateArray().Select(g => $"{g.GetProperty("name").GetString()}={NatDisplay(g)}"))).Append(']');
            if (stop.TryGetProperty("exception", out var exc))
                sb.Append(" exc=").Append(exc.GetProperty("class").GetString()).Append(':').Append(exc.TryGetProperty("message", out var m) ? m.GetString() : "");
            return sb.ToString();
        }
        int guard = 0;
        bool ended = false, atUnhandled = false;
        int depth = 0;
        foreach (var op in stepAll != null ? Repeat(stepAll) : plan)
        {
            if (op == Op.Resume)
            {
                bool ok = atUnhandled && NativeDebugClient.Ok(c.Request("resume", new() { ["thread"] = main }));
                stops.Add(ok ? "resumed" : "no-resume");
                atUnhandled = false;
                continue;
            }
            if (op == Op.StepOut && depth == 0) { stops.Add("out-at-0"); continue; }
            var reply = op == Op.Continue ? c.Request("continue", new() { ["thread"] = main })
                : c.Request("step", new() { ["thread"] = main, ["kind"] = op == Op.StepInto ? "into" : op == Op.StepOver ? "line" : "out" });
            if (!NativeDebugClient.Ok(reply)) { stops.Add("refused " + reply); break; }
            var e = c.NextEventExcept("thread", "threadExited");
            string kind = e.GetProperty("event").GetString()!;
            if (kind == "exited") { stops.Add("end"); ended = true; break; }
            string reason = e.GetProperty("reason").GetString()!;
            depth = e.GetProperty("depth").GetInt32();
            atUnhandled = reason == "unhandled";
            stops.Add(Describe(reason == "unhandled" ? "unhandled" : op == Op.Continue ? "bp" : "step", e));
            if (++guard > 5000) { stops.Add("too many"); break; }
        }
        if (!ended)
        {
            c.Request("breakpoints", new() { ["breakpoints"] = Array.Empty<int[]>() });
            c.Request("continue", new() { ["thread"] = main, ["breakpoints"] = false });
            while (true)
            {
                var e = c.NextEvent();
                if (e.GetProperty("event").GetString() == "exited") break;
                if (e.GetProperty("event").GetString() == "stopped") c.Request("continue", new() { ["thread"] = main, ["breakpoints"] = false });
            }
        }
        c.WaitExit(NativeDebugClient.Timeout);
        Thread.Sleep(50);
        return (stops, c.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList(), c.Errors);
    }

    static string Diff(List<string> vm, List<string> nat)
    {
        int i = 0;
        while (i < vm.Count && i < nat.Count && vm[i] == nat[i]) i++;
        var sb = new StringBuilder($"  erste Abweichung bei Halt {i} (VM {vm.Count}, nativ {nat.Count})\n");
        for (int k = Math.Max(0, i - 2); k < Math.Min(i + 3, Math.Max(vm.Count, nat.Count)); k++)
        {
            sb.Append("  VM:    ").Append(k < vm.Count ? vm[k] : "-").Append('\n');
            sb.Append("  nativ: ").Append(k < nat.Count ? nat[k] : "-").Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Builds the case natively in the mode `debug` (-O0 -g; warnings are an error) - null with a message when it does not build.</summary>
    static string? Build(string script, string cxx, string dir, string name, string flags, out string error, string? sourcePath = null)
    {
        error = "";
        fire.Native.NativeSources generated;
        try { generated = Generate(script, VmExecutionMode.Debug, TargetProfile.Host, sourcePath); }
        catch (Exception ex) { error = "nicht uebersetzbar: " + ex.Message; return null; }
        string exe = Path.Combine(dir, name + ".bin");
        string build = NativeTestBuild.Build(NativeTestBuild.In(dir), cxx, generated, dir, name, flags, exe, out int exit);
        if (exit != 0 || build.Contains("warning:")) { error = "C-Compiler: " + build; return null; }
        return exe;
    }

    static fire.Native.NativeSources Generate(string script, VmExecutionMode mode, TargetProfile target, string? sourcePath = null) =>
        fire.Native.CppGenerator.GenerateSources(new Linker { SourcePaths = sourcePath != null ? new[] { sourcePath } : null }.CompileAndLink(new[] { script }, null, null, mode, null, target), target);

    public static void Run(Action<string, bool, string?> check, string cxx, string workDir, string sanitize)
    {
        RunCases(check, cxx, workDir, sanitize);
        RunProtocol(check, cxx, workDir, sanitize);
        RunTransports(check, cxx, workDir, sanitize);
    }

    static void RunCases(Action<string, bool, string?> check, string cxx, string workDir, string sanitize)
    {
        string flags = $"-pthread -O0 -g -Wall -Wextra {sanitize}";
        var selected = Cases.Where(c => TestSelectedName("Native-Debugger: " + c.Name)).ToList();
        var tasks = selected.Select((c, index) => Task.Run(() =>
        {
            var results = new List<(string Title, bool Ok, string? Detail)>();
            string? exe = Build(c.Script, cxx, workDir, $"dbg{index}", flags, out string error);
            if (exe == null) { results.Add(($"Native-Debugger: {c.Name} (Bau)", false, error)); return results; }
            try
            {
                // the plan: breakpoints and steps, with call stack, scopes, this and globals at every stop
                var (vmStops, vmOut) = RunVm(c.Script, c.Breakpoints, c.Plan, true);
                var (natStops, natOut, natErr) = RunNative(exe, workDir, c.Breakpoints, c.Plan, true);
                results.Add(($"Native-Debugger: {c.Name}: Haltepunkte/Schritte, Aufrufstapel, Scopes, this, Globals wie die VM", vmStops.SequenceEqual(natStops) && vmOut.SequenceEqual(natOut),
                    Diff(vmStops, natStops) + $"  Ausgabe VM: {string.Join("|", vmOut)}\n  Ausgabe nativ: {string.Join("|", natOut)}\n  stderr: {natErr}"));
                if (c.StepAll)
                    foreach (var op in new[] { Op.StepInto, Op.StepOver })
                    {
                        // every step from the start to the end (also through the prelude): the same stops at the same depths
                        var (vmAll, _) = RunVm(c.Script, Array.Empty<int>(), Array.Empty<Op>(), false, () => op);
                        var (natAll, _, natErr2) = RunNative(exe, workDir, Array.Empty<int>(), Array.Empty<Op>(), false, () => op);
                        results.Add(($"Native-Debugger: {c.Name}: jeder {(op == Op.StepInto ? "Step Into" : "Step Over")} bis zum Ende wie die VM ({vmAll.Count} Halte)", vmAll.SequenceEqual(natAll),
                            Diff(vmAll, natAll) + "  stderr: " + natErr2));
                    }
            }
            catch (Exception ex) { results.Add(($"Native-Debugger: {c.Name}", false, ex.ToString())); }
            return results;
        })).ToArray();
        Task.WaitAll(tasks);
        foreach (var t in tasks) foreach (var (title, ok, detail) in t.Result) check(title, ok, detail);
    }

    static string RunTool(string tool, string arguments, string dir, out int exit, int timeoutMs = 120000)
    {
        using var p = Process.Start(new ProcessStartInfo(tool, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = dir })!;
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } exit = -1; return "timeout"; }
        exit = p.ExitCode;
        return output.Result + err.Result;
    }

    static bool HasTool(string tool)
    {
        try { RunTool(tool, tool == "tcc" ? "-v" : "--version", Path.GetTempPath(), out int exit, 20000); return exit == 0; }
        catch (Exception) { return false; }
    }

    static JsonElement Find(JsonElement list, string name) => list.EnumerateArray().First(v => v.GetProperty("name").GetString() == name);

    /// <summary>The protocol and what is no comparison with the VM: #line, the compilers, gdb, values (expand, set), pause, threads, detach.</summary>
    static void RunProtocol(Action<string, bool, string?> check, string cxx, string workDir, string sanitize)
    {
        string flags = $"-pthread -O0 -g -Wall -Wextra {sanitize}";
        void Check(string title, Func<(bool Ok, string Detail)> body)
        {
            string full = "Native-Debugger: " + title;
            if (!TestSelectedName(full)) return;
            try { var (ok, detail) = body(); check(full, ok, detail); }
            catch (Exception ex) { check(full, false, ex.ToString()); }
        }
        const string lineScript = "var total = 0\nfor (var i = 0; i < 3; i = i + 1) {\n    total = total + i\n}\nclass U {\n    static int Sq(int x) {\n        var y = x * x\n        return y\n    }\n}\nprint(\"fertig \" + total + \" \" + U.Sq(4))\n";

        Check("#line nennt die fire-Zeilen, danach wieder die C-Datei; \"lineDirectives\": false schaltet sie ab", () =>
        {
            var release = Generate(lineScript, VmExecutionMode.Release, TargetProfile.Host).Program;
            var off = Generate(lineScript, VmExecutionMode.Release, TargetProfile.Host with { Native = TargetProfile.Host.Native with { LineDirectives = false } }).Program;
            var lines = release.Split('\n');
            bool user = lines.Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^#line 3 ""fire:source/\d+"" // \d+: ")) && lines.Any(l => l.StartsWith("#line 7 \"fire:source/", StringComparison.Ordinal));
            bool prelude = lines.Any(l => l.StartsWith("#line ", StringComparison.Ordinal) && l.Contains("\"fire:prelude/"));
            // after a function: the line of the C file (the directive names the line after it)
            bool back = true;
            for (int i = 0; i < lines.Length; i++)
                if (System.Text.RegularExpressions.Regex.Match(lines[i], @"^#line (\d+) ""fire_program.c""$") is { Success: true } m && int.Parse(m.Groups[1].Value) != i + 2) back = false;
            bool resets = lines.Count(l => l.EndsWith("\"fire_program.c\"", StringComparison.Ordinal)) > 3;
            return (user && prelude && back && resets && !off.Contains("#line") && !release.Contains("@@fire-line-reset"), $"user {user}, prelude {prelude}, back {back}, resets {resets}, off {!off.Contains("#line")}");
        });

        Check("gcc und clang uebersetzen das C eines Debug-Builds ohne Warnung, tcc uebersetzt es; es laeuft ohne Debugger wie die VM", () =>
        {
            var sources = Generate(lineScript, VmExecutionMode.Debug, TargetProfile.Host);
            string cFile = Path.Combine(workDir, "dbgcompilers.c");
            File.WriteAllText(cFile, sources.Program);
            var detail = new StringBuilder();
            bool ok = sources.Program.Contains("#define FIRE_DEBUG 1") && sources.Program.Contains("#define FIRE_DEBUG_AGENT 1");
            var compilers = new List<(string Tool, string Args)> { ("gcc", "-std=c11 -O0 -g -Wall -Wextra -pedantic -pthread"), ("gcc", "-std=c11 -O2 -Wall -Wextra -pthread") };
            if (HasTool("clang")) compilers.Add(("clang", "-std=c11 -O0 -g -Wall -Wextra -pedantic -pthread"));
            // (TinyCC 0.9.27 ignores noreturn and so reports every function that ends in fatal() as "might return no value" - the built-in tcc toolchain
            // builds with -w for that reason; here it is checked that tcc builds and runs the debug C, gcc and clang check the warnings)
            if (HasTool("tcc")) compilers.Add(("tcc", "-w -pthread"));
            for (int i = 0; i < compilers.Count; i++)
            {
                var (tool, args) = compilers[i];
                string exe = Path.Combine(workDir, $"dbgcompilers_{tool}{i}.bin");
                string build = RunTool(tool, $"{args} \"{cFile}\" -I\"{workDir}\" -o \"{exe}\" -lm", workDir, out int exit);
                string run = exit == 0 ? RunTool(exe, "", workDir, out _) : "";
                bool good = exit == 0 && !build.Contains("warning") && run == "fertig 3 16\n";
                if (!good) detail.Append($"{tool} {args}: {build} -> {run}\n");
                ok &= good;
            }
            return (ok && compilers.Count >= 3, detail.ToString() + $" ({compilers.Count} Compiler)");
        });

        if (HasTool("gdb"))
            Check("gdb haelt an einer fire-Zeile (break datei.fire:7) und zeigt sie im Aufrufstapel", () =>
            {
                string firePath = Path.Combine(workDir, "dbgline.fire");
                File.WriteAllText(firePath, lineScript);
                string? exe = Build(lineScript, cxx, workDir, "dbgline", "-pthread -O0 -g -Wall -Wextra", out string error, firePath);
                if (exe == null) return (false, error);
                string output = RunTool("gdb", $"-batch -ex \"break dbgline.fire:7\" -ex run -ex bt \"{exe}\"", workDir, out _);
                bool ok = output.Contains("dbgline.fire:7") && output.Contains("var y = x * x") && System.Text.RegularExpressions.Regex.IsMatch(output, @"fire_main.*dbgline\.fire:11");
                return (ok, output);
            });

        // values: expand an object and an array, set a global, a field and an element (the program prints what was set)
        Check("expand und set: Objekt, Array, Variable, Feld, Element - das Programm rechnet mit den neuen Werten", () =>
        {
            const string script = "class P {\n    int x\n    string name\n    construct(int x) {\n        this.x = x\n        this.name = \"p\"\n    }\n}\nvar p = new P(1)\nvar xs = [1, 2, 3]\nvar count = 5\nvar label = \"a\"\nprint(count)\nprint(label)\nprint(p.x)\nprint(p.name)\nprint(xs[1])\nvar f = 1.5m\nprint(f)\n";
            string? exe = Build(script, cxx, workDir, "dbgset", flags, out string error);
            if (exe == null) return (false, error);
            using var c = NativeDebugClient.Launch(exe, workDir);
            int first = c.Hello.GetProperty("firstUserSource").GetInt32(), main = c.Hello.GetProperty("mainThread").GetInt32();
            c.NextEvent("stopped");
            c.Request("breakpoints", new() { ["breakpoints"] = new[] { new[] { first, 13 }, new[] { first, 19 } } });
            c.Request("continue", new() { ["thread"] = main });
            var stop = c.NextEvent("stopped");
            var globals = c.Request("globals").GetProperty("variables");
            var p = Find(globals, "p");
            var xs = Find(globals, "xs");
            var children = c.Request("expand", new() { ["ref"] = p.GetProperty("ref").GetString() }).GetProperty("children");
            var items = c.Request("expand", new() { ["ref"] = xs.GetProperty("ref").GetString() }).GetProperty("children");
            var detail = new StringBuilder($"{stop}\n{globals}\n{children}\n{items}\n");
            bool ok = stop.GetProperty("line").GetInt32() == 13 && p.GetProperty("kind").GetString() == "object" && p.GetProperty("class").GetString() == "P" && p.GetProperty("expandable").GetBoolean()
                && xs.GetProperty("display").GetString() == "Array[3]" && xs.GetProperty("length").GetInt32() == 3
                && string.Join(",", children.EnumerateArray().Select(v => v.GetProperty("name").GetString() + "=" + v.GetProperty("display").GetString())) == "x=1,name=p"
                && string.Join(",", items.EnumerateArray().Select(v => v.GetProperty("name").GetString() + "=" + v.GetProperty("display").GetString())) == "[0]=1,[1]=2,[2]=3";
            string Set(string reference, string literal)
            {
                var reply = c.Request("set", new() { ["thread"] = main, ["ref"] = reference, ["value"] = literal });
                detail.Append(reply).Append('\n');
                return NativeDebugClient.Ok(reply) ? reply.GetProperty("value").GetProperty("display").GetString()! : "error";
            }
            ok &= Set(Find(globals, "count").GetProperty("ref").GetString()!, "42") == "42";
            ok &= Set(Find(globals, "label").GetProperty("ref").GetString()!, "\"neu \\\"x\\\"\"") == "neu \"x\"";
            ok &= Set(children[0].GetProperty("ref").GetString()!, "7") == "7";
            ok &= Set(children[1].GetProperty("ref").GetString()!, "\"q\"") == "q";
            ok &= Set(items[1].GetProperty("ref").GetString()!, "99") == "99";
            ok &= Set("g:999", "1") == "error" && Set(items[1].GetProperty("ref").GetString()!, "kein literal") == "error";
            // a field goes through its setter: the declared kind is checked (an ArgumentException is the answer, the program does not see it)
            ok &= Set(children[0].GetProperty("ref").GetString()!, "\"kein int\"") == "error";
            c.Request("continue", new() { ["thread"] = main });
            var stop2 = c.NextEvent("stopped");
            var f = Find(c.Request("globals").GetProperty("variables"), "f");
            ok &= Set(f.GetProperty("ref").GetString()!, "2.5") == "2.5m";   // (a number keeps its unit)
            c.Request("continue", new() { ["thread"] = main });
            c.NextEvent("exited");
            c.WaitExit(NativeDebugClient.Timeout);
            Thread.Sleep(50);
            ok &= c.Output == "42\nneu \"x\"\n7\nq\n99\n2.5m\n" && stop2.GetProperty("line").GetInt32() == 19;
            return (ok, detail + c.Output + c.Errors);
        });

        Check("hello: Quellen mit Datei und SHA-256, Ziel, Modus; ohne FIRE_DEBUG laeuft der Debug-Build einfach; detach laesst das Programm zu Ende laufen", () =>
        {
            string firePath = Path.Combine(workDir, "dbghello.fire");
            File.WriteAllText(firePath, lineScript);
            string? exe = Build(lineScript, cxx, workDir, "dbghello", flags, out string error, firePath);
            if (exe == null) return (false, error);
            string plain = RunTool(exe, "", workDir, out int plainExit);
            using var c = NativeDebugClient.Launch(exe, workDir);
            var hello = c.Hello;
            int first = hello.GetProperty("firstUserSource").GetInt32();
            var src = hello.GetProperty("sources").EnumerateArray().ToList();
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(lineScript))).ToLowerInvariant();
            bool ok = plain == "fertig 3 16\n" && plainExit == 0 && hello.GetProperty("protocol").GetInt32() == 1 && hello.GetProperty("engine").GetString() == "native"
                && hello.GetProperty("mode").GetString() == "debug" && src.Count > first && src[first].GetProperty("file").GetString() == Path.GetFullPath(firePath)
                && src[first].GetProperty("hash").GetString() == hash && src[0].GetProperty("file").ValueKind == JsonValueKind.Null && src[0].GetProperty("hash").ValueKind == JsonValueKind.Null;
            var entry = c.NextEvent("stopped");
            ok &= entry.GetProperty("reason").GetString() == "entry" && entry.GetProperty("depth").GetInt32() == 0;
            var threads = c.Request("threads").GetProperty("threads");
            ok &= threads.GetArrayLength() == 1 && threads[0].GetProperty("name").GetString() == "Main" && threads[0].GetProperty("state").GetString() == "paused";
            ok &= !NativeDebugClient.Ok(c.Request("step", new() { ["thread"] = 1, ["kind"] = "out" }));   // (depth 0: nothing to step out of)
            ok &= !NativeDebugClient.Ok(c.Request("nonsense"));
            c.Request("breakpoints", new() { ["breakpoints"] = new[] { new[] { first, 3 } } });
            c.Request("detach");
            bool exited = c.WaitExit(NativeDebugClient.Timeout);
            Thread.Sleep(50);
            ok &= exited && c.ExitCode == 0 && c.Output == "fertig 3 16\n";
            return (ok, $"{hello}\n{entry}\n{threads}\n{c.Output}{c.Errors}");
        });

        // Windows (Winsock, Win32 threads): built with MinGW, run under Wine
        string? wine = new[] { "/usr/lib/wine/wine64", "/usr/bin/wine64", "/usr/bin/wine" }.FirstOrDefault(File.Exists);
        if (wine != null && HasTool("x86_64-w64-mingw32-gcc"))
            Check("Windows (MinGW, Wine): der Agent verbindet sich, haelt am Haltepunkt, zeigt Scopes und Aufrufstapel", () =>
            {
                var target = TargetProfile.TryGet("windows", out var windows) ? windows : throw new InvalidOperationException("no windows target");
                var sources = Generate(lineScript, VmExecutionMode.Debug, target);
                string cFile = Path.Combine(workDir, "dbgwin.c"), exe = Path.Combine(workDir, "dbgwin.exe");
                File.WriteAllText(cFile, sources.Program);
                string build = RunTool("x86_64-w64-mingw32-gcc", $"-std=c11 -O0 -g -Wall -Wextra \"{cFile}\" -I\"{workDir}\" -o \"{exe}\" -lws2_32", workDir, out int exit);
                if (exit != 0 || build.Contains("warning")) return (false, build);
                var environment = new Dictionary<string, string> { ["WINEPREFIX"] = Path.Combine(workDir, "wineprefix"), ["WINEDEBUG"] = "-all", ["LANG"] = "C.UTF-8" };
                using var c = NativeDebugClient.Launch(exe, workDir, wine, environment);
                int first = c.Hello.GetProperty("firstUserSource").GetInt32();
                c.NextEvent("stopped");
                c.Request("breakpoints", new() { ["breakpoints"] = new[] { new[] { first, 7 } } });
                c.Request("continue", new() { ["thread"] = 1 });
                var stop = c.NextEvent("stopped");
                var scopes = c.Request("scopes", new() { ["thread"] = 1 });
                var trace = c.Request("trace", new() { ["thread"] = 1 });
                c.Request("continue", new() { ["thread"] = 1 });
                var exited = c.NextEvent("exited");
                c.WaitExit(NativeDebugClient.Timeout);
                Thread.Sleep(50);
                bool ok = stop.GetProperty("line").GetInt32() == 7 && stop.GetProperty("depth").GetInt32() == 1 && scopes.ToString().Contains("\"name\":\"x\",\"kind\":\"int\",\"display\":\"4\"")
                    && trace.GetProperty("frames").GetArrayLength() == 2 && exited.GetProperty("code").GetInt32() == 0 && c.Output.Replace("\r", "").Contains("fertig 3 16\n");
                return (ok, $"{c.Hello}\n{stop}\n{scopes}\n{trace}\n{c.Output}{c.Errors}");
            });

        Check("pause haelt eine Endlosschleife an, terminate beendet das Programm", () =>
        {
            const string script = "var n = 0\nwhile (true) {\n    n = n + 1\n}\n";
            string? exe = Build(script, cxx, workDir, "dbgpause", flags, out string error);
            if (exe == null) return (false, error);
            using var c = NativeDebugClient.Launch(exe, workDir);
            int first = c.Hello.GetProperty("firstUserSource").GetInt32();
            c.NextEvent("stopped");
            c.Request("continue", new() { ["thread"] = 1 });
            Thread.Sleep(300);
            var threads = c.Request("threads").GetProperty("threads");
            c.Request("pause");
            var stop = c.NextEvent("stopped");
            var n = Find(c.Request("globals").GetProperty("variables"), "n");
            bool ok = threads[0].GetProperty("state").GetString() == "running" && stop.GetProperty("reason").GetString() == "pause" && stop.GetProperty("source").GetInt32() == first
                && stop.GetProperty("line").GetInt32() is 2 or 3 && long.Parse(n.GetProperty("display").GetString()!) > 0;
            c.Request("terminate", new() { ["code"] = 3 });
            var exited = c.NextEvent("exited");
            ok &= exited.GetProperty("code").GetInt32() == 3 && c.WaitExit(NativeDebugClient.Timeout) && c.ExitCode == 3;
            return (ok, $"{threads}\n{stop}\n{n}\n{exited}");
        });

        // threads: a breakpoint in a fire thread stops only that thread; it is stepped and continued on its own, the main thread runs on
        Check("Threads: ein neuer fire-Thread meldet sich, ein Haltepunkt haelt nur ihn an, Schritt und Weiterlaufen je Thread, set in seinem Scope", () =>
        {
            const string script = "var done = 0\nfire {\n    var k = 0\n    while (k < 3) {\n        k = k + 1\n    }\n    print(\"thread fertig \" + k)\n}\nvar m = 0\nwhile (m < 2) {\n    m = m + 1\n}\nprint(\"main fertig\")\n";
            string? exe = Build(script, cxx, workDir, "dbgthreads", flags, out string error);
            if (exe == null) return (false, error);
            using var c = NativeDebugClient.Launch(exe, workDir);
            int first = c.Hello.GetProperty("firstUserSource").GetInt32(), main = c.Hello.GetProperty("mainThread").GetInt32();
            c.NextEvent("stopped");
            c.Request("breakpoints", new() { ["breakpoints"] = new[] { new[] { first, 5 } } });
            c.Request("continue", new() { ["thread"] = main });
            var log = new StringBuilder();
            var born = c.NextEvent("thread");
            int t = born.GetProperty("thread").GetInt32();
            var stop = c.NextEventExcept("threadExited");
            log.Append(born).Append('\n').Append(stop).Append('\n');
            bool ok = t != main && born.GetProperty("name").GetString() == "Fire #1" && stop.GetProperty("event").GetString() == "stopped" && stop.GetProperty("thread").GetInt32() == t
                && stop.GetProperty("line").GetInt32() == 5 && stop.GetProperty("reason").GetString() == "breakpoint";
            var threads = c.Request("threads").GetProperty("threads");
            log.Append(threads).Append('\n');
            ok &= threads.GetArrayLength() == 2 && threads.EnumerateArray().Any(x => x.GetProperty("id").GetInt32() == t && x.GetProperty("state").GetString() == "paused");
            var trace = c.Request("trace", new() { ["thread"] = t });
            log.Append(trace).Append('\n');
            ok &= trace.GetProperty("frames").GetArrayLength() == 1 && trace.GetProperty("frames")[0].GetProperty("function").GetString() == "<fire>";
            // a step of the thread: the condition of the loop (line 4)
            c.Request("step", new() { ["thread"] = t, ["kind"] = "line" });
            var stepped = c.NextEvent("stopped");
            log.Append(stepped).Append('\n');
            ok &= stepped.GetProperty("thread").GetInt32() == t && stepped.GetProperty("line").GetInt32() == 4 && stepped.GetProperty("reason").GetString() == "step";
            // (k is a variable of the top level of the thread: a global of the thread, like in the VM, where a fire thread has its own global scope)
            var scopes = c.Request("scopes", new() { ["thread"] = t }).GetProperty("scopes");
            var threadGlobals = c.Request("globals", new() { ["thread"] = t }).GetProperty("variables");
            log.Append(scopes).Append('\n').Append(threadGlobals).Append('\n');
            ok &= scopes.GetArrayLength() == 0 && Find(threadGlobals, "done").GetProperty("display").GetString() == "0";
            var k = Find(threadGlobals, "k");
            ok &= k.GetProperty("display").GetString() == "1";
            ok &= NativeDebugClient.Ok(c.Request("set", new() { ["thread"] = t, ["ref"] = k.GetProperty("ref").GetString(), ["value"] = "10" }));
            ok &= !NativeDebugClient.Ok(c.Request("scopes", new() { ["thread"] = 99 }));
            c.Request("continue", new() { ["thread"] = t });
            // the thread ends (k is 10: no more breakpoint), the main thread ends, the program ends
            var rest = new List<string>();
            while (true)
            {
                var e = c.NextEvent();
                rest.Add(e.ToString());
                if (e.GetProperty("event").GetString() == "exited") break;
                if (e.GetProperty("event").GetString() == "stopped") { ok = false; c.Request("continue"); }
            }
            c.WaitExit(NativeDebugClient.Timeout);
            Thread.Sleep(50);
            log.Append(string.Join("\n", rest)).Append('\n');
            ok &= rest.Any(r => r.Contains("threadExited")) && c.Output.Contains("thread fertig 10\n") && c.Output.Contains("main fertig\n") && c.ExitCode == 0;
            return (ok, log + c.Output + c.Errors);
        });

        Check("Threads: pause haelt alle an, einer laeuft weiter, der andere bleibt stehen; eine unbehandelte Ausnahme im Thread haelt ihn an", () =>
        {
            const string script = "var a = 0\nvar b = 0\nfire {\n    while (true) {\n        b = b + 1\n        if (b == 300000) {\n            throw new ArgumentException(\"im thread\")\n        }\n    }\n}\nwhile (true) {\n    a = a + 1\n}\n";
            string? exe = Build(script, cxx, workDir, "dbgpauseall", flags, out string error);
            if (exe == null) return (false, error);
            using var c = NativeDebugClient.Launch(exe, workDir);
            int main = c.Hello.GetProperty("mainThread").GetInt32();
            c.NextEvent("stopped");
            c.Request("continue", new() { ["thread"] = main });
            int t = c.NextEvent("thread").GetProperty("thread").GetInt32();
            Thread.Sleep(100);
            c.Request("pause");
            var s1 = c.NextEvent("stopped");
            var s2 = c.NextEvent("stopped");
            var log = new StringBuilder($"{s1}\n{s2}\n");
            bool ok = new[] { s1, s2 }.Select(s => s.GetProperty("thread").GetInt32()).OrderBy(x => x).SequenceEqual(new[] { main, t }.OrderBy(x => x))
                && s1.GetProperty("reason").GetString() == "pause" && s2.GetProperty("reason").GetString() == "pause";
            long Global(string name) => long.Parse(Find(c.Request("globals").GetProperty("variables"), name).GetProperty("display").GetString()!);
            long a0 = Global("a"), b0 = Global("b");
            // the main thread runs on, the fire thread stays where it is
            c.Request("continue", new() { ["thread"] = main });
            Thread.Sleep(200);
            long a1 = Global("a"), b1 = Global("b");
            log.Append($"a {a0} -> {a1}, b {b0} -> {b1}\n");
            ok &= a1 > a0 && b1 == b0 && b0 < 300000;
            // the fire thread runs on until its exception: it stops there (unhandled), the main thread keeps running
            c.Request("continue", new() { ["thread"] = t });
            var unhandled = c.NextEvent("stopped");
            log.Append(unhandled).Append('\n');
            ok &= unhandled.GetProperty("thread").GetInt32() == t && unhandled.GetProperty("reason").GetString() == "unhandled" && unhandled.GetProperty("line").GetInt32() == 7
                && unhandled.GetProperty("exception").GetProperty("class").GetString() == "ArgumentException" && unhandled.GetProperty("exception").GetProperty("message").GetString() == "im thread";
            var threads = c.Request("threads").GetProperty("threads");
            ok &= threads.EnumerateArray().Any(x => x.GetProperty("id").GetInt32() == main && x.GetProperty("state").GetString() == "running");
            c.Request("terminate");
            c.WaitExit(NativeDebugClient.Timeout);
            return (ok, log.ToString() + threads + c.Errors);
        });
    }

    /// <summary>The framed transports (docs/NATIVE_DEBUG_PROTOCOL.md "Transport"): stdio on the desktop (FIRE_DEBUG=stdio) and on the FreeRTOS simulator
    /// (native/sim: a FreeRTOS build, whose transport is stdio), the same plans as over TCP against the VM; the console between the frames, garbled frames,
    /// the program's standard input through the agent, the wait of 3 s for a debugger; and an ESP32 on QEMU (the console UART on a socket).</summary>
    static void RunTransports(Action<string, bool, string?> check, string cxx, string workDir, string sanitize)
    {
        string flags = $"-pthread -O0 -g -Wall -Wextra {sanitize}";
        void Check(string title, Func<(bool Ok, string Detail)> body)
        {
            string full = "Native-Debugger: " + title;
            if (!TestSelectedName(full)) return;
            try { var (ok, detail) = body(); check(full, ok, detail); }
            catch (Exception ex) { check(full, false, ex.ToString()); }
        }

        // the same plans as over TCP: stdio, framed (desktop)
        foreach (var name in new[] { "Schleifen und Aufrufe", "Objekte, this und Felder", "unbehandelte Ausnahme: anhalten und ignorieren (resume)" })
        {
            var c = Cases.First(x => x.Name == name);
            Check($"stdio (Rahmen): {c.Name}: wie die VM", () =>
            {
                string? exe = Build(c.Script, cxx, workDir, "dbgstdio" + Math.Abs(c.Name.GetHashCode() % 1000), flags, out string error);
                if (exe == null) return (false, error);
                var (vmStops, vmOut) = RunVm(c.Script, c.Breakpoints, c.Plan, true);
                var (natStops, natOut, natErr) = RunNative(() => NativeDebugClient.LaunchStdio(exe, workDir), c.Breakpoints, c.Plan, true);
                return (vmStops.SequenceEqual(natStops) && vmOut.SequenceEqual(natOut), Diff(vmStops, natStops) + $"  Ausgabe VM: {string.Join("|", vmOut)}\n  Ausgabe nativ: {string.Join("|", natOut)}\n  stderr: {natErr}");
            });
        }

        // the FreeRTOS simulator: a build for the target freertos (its agent talks framed over stdio, no FIRE_DEBUG), main calls the entry point
        fire.Native.NativeRuntimeFiles.WriteSimulatorTo(Path.Combine(workDir, "sim"));
        foreach (var name in new[] { "Schleifen und Aufrufe", "Rekursion" })
        {
            var c = Cases.First(x => x.Name == name);
            Check($"FreeRTOS-Simulator (stdio, Rahmen): {c.Name}: wie die VM", () =>
            {
                var generated = Generate(c.Script, VmExecutionMode.Debug, TargetProfile.FreeRtos);
                if (!generated.Program.Contains("#define FIRE_DEBUG_TRANSPORT_STDIO 1")) return (false, "no stdio transport in the FreeRTOS build");
                generated = generated with { Program = generated.Program + "\nint main(void) { fire_start(); return 0; }\n" };
                string exe = Path.Combine(workDir, "dbgrtos" + Math.Abs(c.Name.GetHashCode() % 1000) + ".bin");
                string build = NativeTestBuild.Build(NativeTestBuild.In(workDir), cxx, generated, workDir, Path.GetFileNameWithoutExtension(exe), $"{flags} -I\"{Path.Combine(workDir, "sim")}\"", exe, out int exit);
                if (exit != 0 || build.Contains("warning:")) return (false, "C-Compiler: " + build);
                var (vmStops, vmOut) = RunVm(c.Script, c.Breakpoints, c.Plan, true);
                var (natStops, natOut, natErr) = RunNative(() => NativeDebugClient.LaunchStdio(exe, workDir, null), c.Breakpoints, c.Plan, true);
                return (vmStops.SequenceEqual(natStops) && vmOut.SequenceEqual(natOut), Diff(vmStops, natStops) + $"  Ausgabe VM: {string.Join("|", vmOut)}\n  Ausgabe nativ: {string.Join("|", natOut)}\n  stderr: {natErr}");
            });
        }

        // the console between the frames, a text that looks like a frame, a garbled frame, the program's standard input (it waits for it)
        const string stdinScript = "#import \"io\"\nprint(\"vorher\")\nvar a = IO.Stdio.ReadLine()\nprint(\"gelesen \" + a)\nvar n = 0\nfor (var i = 0; i < 3; i = i + 1) {\n    n = n + i\n    print(\"FD:{\\\"event\\\":\\\"stopped\\\"}*0000 \" + i)\n}\nvar b = IO.Stdio.ReadLine()\nprint(\"ende \" + n + \" \" + b)\n";
        string? stdinExe = null;
        string stdinError = "";
        if (TestSelectedName("Native-Debugger: stdio (Rahmen): Eingabe") || TestSelectedName("Native-Debugger: stdio (Rahmen): ohne"))
            stdinExe = Build(stdinScript, cxx, workDir, "dbgstdin", flags, out stdinError);
        Check("stdio (Rahmen): Eingabe des Programms durch den Agenten, Ausgabe zwischen den Rahmen, verfaelschte Rahmen werden verworfen", () =>
        {
            if (stdinExe == null) return (false, stdinError);
            using var c = NativeDebugClient.LaunchStdio(stdinExe, workDir);
            int first = c.Hello.GetProperty("firstUserSource").GetInt32(), main = c.Hello.GetProperty("mainThread").GetInt32();
            var log = new StringBuilder($"{c.Hello.GetProperty("transport")}\n");
            bool ok = c.Hello.GetProperty("transport").GetString() == "stdio" && c.NextEvent("stopped").GetProperty("reason").GetString() == "entry";
            c.SendRaw(Encoding.ASCII.GetBytes("\u001EFD:{\"id\":77,\"cmd\":\"threads\"}*0000\n"));   // (a wrong CRC: no answer - the next answer is that of the next request)
            ok &= c.SendInput("eins");
            ok &= NativeDebugClient.Ok(c.Request("breakpoints", new() { ["breakpoints"] = new[] { new[] { first, 7 } } }));
            c.Request("continue", new() { ["thread"] = main });
            var stop = c.NextEvent("stopped");
            var globals = c.Request("globals").GetProperty("variables");
            log.Append(stop).Append('\n').Append(globals).Append('\n');
            ok &= stop.GetProperty("line").GetInt32() == 7 && Find(globals, "a").GetProperty("display").GetString() == "eins";
            c.Request("breakpoints", new() { ["breakpoints"] = Array.Empty<int[]>() });
            c.Request("continue", new() { ["thread"] = main });
            Thread.Sleep(300);   // (the program waits for its second line now)
            var threads = c.Request("threads").GetProperty("threads");
            log.Append(threads).Append('\n');
            ok &= threads[0].GetProperty("state").GetString() == "running" && !c.Output.Contains("ende");
            ok &= c.SendInput("zwei");
            var exited = c.NextEvent("exited");
            c.WaitExit(NativeDebugClient.Timeout);
            Thread.Sleep(50);
            ok &= exited.GetProperty("code").GetInt32() == 0
                && c.Output == "vorher\ngelesen eins\nFD:{\"event\":\"stopped\"}*0000 0\nFD:{\"event\":\"stopped\"}*0000 1\nFD:{\"event\":\"stopped\"}*0000 2\nende 3 zwei\n";
            return (ok, log + c.Output + c.Errors);
        });

        Check("stdio (Rahmen): ohne ersten Befehl laeuft das Programm nach der Wartezeit ohne Debugger, die Eingabe geht weiter durch den Agenten", () =>
        {
            if (stdinExe == null) return (false, stdinError);
            var start = new ProcessStartInfo(stdinExe) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workDir };
            start.Environment["FIRE_DEBUG"] = "stdio;wait=700";
            using var p = Process.Start(start)!;
            var watch = Stopwatch.StartNew();
            var transport = new FramedDebugTransport(p.StandardOutput.BaseStream, p.StandardInput.BaseStream);
            var console = new BlockingCollection<string>();
            transport.Console = console.Add;
            var frames = new List<string>();
            var reader = new Thread(() => { string? m; while ((m = transport.ReadMessage()) != null) lock (frames) frames.Add(m); console.CompleteAdding(); }) { IsBackground = true };
            reader.Start();
            var errTask = p.StandardError.ReadLineAsync();
            bool first = console.TryTake(out var line1, NativeDebugClient.Timeout);
            long firstAt = watch.ElapsedMilliseconds;
            string? err = errTask.Wait(NativeDebugClient.Timeout) ? errTask.Result : null;
            transport.WriteInput("x");
            transport.WriteInput("y");
            var rest = new List<string>();
            while (console.TryTake(out var l, NativeDebugClient.Timeout)) rest.Add(l);
            bool exitedInTime = p.WaitForExit(10000);
            List<string> got;
            lock (frames) got = frames.ToList();
            // (the hello and the stop at the entry are framed; after the wait the program runs on its own: no exited for a debugger that never came)
            bool ok = first && line1 == "vorher" && firstAt >= 650 && err == "fire debug: no debugger within 700 ms - running without one" && exitedInTime && p.ExitCode == 0
                && string.Join("|", rest) == "gelesen x|FD:{\"event\":\"stopped\"}*0000 0|FD:{\"event\":\"stopped\"}*0000 1|FD:{\"event\":\"stopped\"}*0000 2|ende 3 y"
                && got.Count == 2 && got[0].Contains("\"event\":\"hello\"") && got[1].Contains("\"reason\":\"entry\"");
            return (ok, $"{line1} after {firstAt} ms | {err} | {string.Join("|", rest)} | frames: {string.Join(" ", got)}");
        });

        RunQemu(Check, workDir);
    }

    /// <summary>An ESP32 debug build on QEMU (ESP-IDF's qemu-system-xtensa, IDF_PATH): the console UART is a socket of QEMU, the client a SerialFramedTransport on
    /// it - the boot log before the hello, a plan of breakpoints, steps, scopes and an unhandled exception against the VM, the program's standard input; then the
    /// same program without a debugger: after 3 s it runs alone, reads its input from the UART and ends at its exception.</summary>
    static void RunQemu(Action<string, Func<(bool Ok, string Detail)>> Check, string workDir)
    {
        const string titleVm = "ESP32 (QEMU, UART): Boot-Log vor dem hello, Haltepunkte, Schritte, Scopes, unbehandelte Ausnahme wie die VM, Eingabe ueber die UART";
        const string titleWait = "ESP32 (QEMU, UART): ohne Debugger laeuft das Programm nach 3 s allein, liest seine Eingabe von der UART";
        if (!TestSelectedName("Native-Debugger: " + titleVm) && !TestSelectedName("Native-Debugger: " + titleWait)) return;
        string? idf = Esp32Launch.FindIdf(null);
        if (idf == null) { Console.WriteLine("(Native-Debugger auf dem ESP32/QEMU: uebersprungen - IDF_PATH fehlt)"); return; }
        string script = "#import \"io\"\n#if native\nprint(\"gelesen \" + IO.Stdio.ReadLine())\n#endif\nclass W {\n    int low\n    construct(int low) {\n        this.low = low\n    }\n"
            + "    int Check(int n) {\n        var limit = this.low + 2\n        if (n > limit) {\n            throw new ArgumentException(\"zu gross: \" + n)\n        }\n        return n\n    }\n}\n"
            + "var w = new W(0)\nvar total = 0\nfor (var i = 0; i < 4; i = i + 1) {\n    total = total + w.Check(i)\n}\nprint(\"total \" + total)\n";
        int[] bps = { 19 };
        var plan = new[] { Op.Continue, Op.StepOver, Op.StepOver, Op.StepInto, Op.StepOver, Op.StepOut, Op.Continue, Op.Resume, Op.StepOver, Op.Continue };
        // the project (its own folder: the ESP32 test of the backend uses ~/.cache/fire-tests/esp32): the component main/, built by idf.py like spark does it
        string project = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "fire-tests", "esp32-dbg");
        Directory.CreateDirectory(project);
        string scriptPath = Path.Combine(project, "dbg.fire");
        File.WriteAllText(scriptPath, script);
        Esp32Launch.WriteProject(project, "firedbg");
        var buildOut = new StringWriter();
        int code = CommandLineRunner.Run(new[] { "build", scriptPath, "-t", "esp32", "-m", "debug", "-o", Path.Combine(project, "main") }, buildOut, buildOut);
        var idfLog = new StringBuilder();
        var (built, _) = code == 0 ? Esp32Launch.BuildAndFlash(idf, project, null, false, l => { lock (idfLog) idfLog.AppendLine(l); }) : (false, null);
        string idfText;
        lock (idfLog) idfText = idfLog.ToString();
        int warnings = System.Text.RegularExpressions.Regex.Matches(idfText, "warning:").Count;
        if (!built || warnings > 0)
        {
            Check(titleVm, () => (false, $"build {code}: {buildOut}\n{string.Join("\n", idfText.Split('\n').TakeLast(40))}"));
            return;
        }
        string flash = Path.Combine(project, "flash.bin");
        string merge = RunTool("bash", $"-c \". \\\"{idf}/export.sh\\\" > /dev/null 2>&1; cd \\\"{project}/build\\\" && python -m esptool --chip esp32 merge_bin --fill-flash-size 4MB -o \\\"{flash}\\\" @flash_args\"", project, out int mergeExit);
        if (mergeExit != 0) { Check(titleVm, () => (false, merge)); return; }
        string? qemu = RunTool("bash", $"-c \". \\\"{idf}/export.sh\\\" > /dev/null 2>&1; command -v qemu-system-xtensa\"", project, out int whichExit).Trim();
        if (whichExit != 0 || qemu.Length == 0) { Console.WriteLine("(Native-Debugger auf dem ESP32/QEMU: uebersprungen - qemu-system-xtensa fehlt)"); return; }

        // QEMU waits with the start of the chip until the client is there (wait=on): the boot log is complete
        (Process Qemu, SerialFramedTransport Transport) Boot()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var qp = Process.Start(new ProcessStartInfo(qemu, $"-nographic -machine esp32 -m 4M -drive file=\"{flash}\",if=mtd,format=raw -serial tcp:127.0.0.1:{port},server=on,wait=on -monitor none")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            qp.OutputDataReceived += (_, _) => { };
            qp.ErrorDataReceived += (_, _) => { };
            qp.BeginOutputReadLine();
            qp.BeginErrorReadLine();
            for (int i = 0; i < 200; i++)
            {
                try
                {
                    var client = new TcpClient("127.0.0.1", port) { NoDelay = true };
                    return (qp, new SerialFramedTransport(client.GetStream()));
                }
                catch (SocketException) { Thread.Sleep(50); }
            }
            try { qp.Kill(); } catch (Exception) { }
            throw new TimeoutException("QEMU did not open its serial port");
        }

        if (TestSelectedName("Native-Debugger: " + titleVm))
            Check(titleVm, () =>
            {
                var (vmStops, vmOut) = RunVm(script, bps, plan, true);
                var (qp, transport) = Boot();
                string boot = "";
                try
                {
                    var (natStops, natOut, natErr) = RunNative(() =>
                    {
                        var c = NativeDebugClient.Attach(transport);
                        boot = c.Output;
                        c.SendInput("eingabe");   // (the agent keeps it until the program reads)
                        return c;
                    }, bps, plan, true);
                    // the output: the console after the hello (the boot log came before it)
                    int bootLines = boot.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                    var programOut = natOut.Skip(bootLines).Where(l => !l.StartsWith("I (", StringComparison.Ordinal) && !l.StartsWith("W (", StringComparison.Ordinal)).ToList();
                    bool bootLog = boot.Contains("rst:0x1 (POWERON_RESET)") && boot.Contains("main_task: Calling app_main()") && !boot.Contains("FD:");
                    var expectedOut = new[] { "gelesen eingabe" }.Concat(vmOut).ToList();
                    bool ok = bootLog && vmStops.SequenceEqual(natStops) && programOut.SequenceEqual(expectedOut) && vmStops.Count == plan.Length && vmStops.Contains("resumed") && vmStops[^1] == "end";
                    return (ok, $"boot log {bootLog}\n  VM: {string.Join("\n  VM: ", vmStops)}\n" + Diff(vmStops, natStops) + $"  Ausgabe VM: {string.Join("|", expectedOut)}\n  Ausgabe ESP32: {string.Join("|", programOut)}\n  {natErr}");
                }
                finally { try { qp.Kill(); qp.WaitForExit(5000); } catch (Exception) { } }
            });

        if (TestSelectedName("Native-Debugger: " + titleWait))
            Check(titleWait, () =>
            {
                var (qp, transport) = Boot();
                try
                {
                    var console = new BlockingCollection<string>();
                    transport.Console = console.Add;
                    var helloAt = new TaskCompletionSource<long>();
                    var watch = Stopwatch.StartNew();
                    var reader = new Thread(() =>
                    {
                        string? m;
                        while ((m = transport.ReadMessage()) != null) if (m.Contains("\"event\":\"hello\"")) helloAt.TrySetResult(watch.ElapsedMilliseconds);
                        console.CompleteAdding();
                    }) { IsBackground = true };
                    reader.Start();
                    var lines = new List<string>();
                    long messageAt = -1;
                    bool sent = false;
                    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                    while (DateTime.UtcNow < deadline && console.TryTake(out var l, TimeSpan.FromSeconds(30)))
                    {
                        lines.Add(l);
                        if (l.Contains("no debugger within 3000 ms")) { messageAt = watch.ElapsedMilliseconds; transport.WriteInput("spaet"); sent = true; }
                        if (l.Contains("zu gross: 3")) break;
                    }
                    long hello = helloAt.Task.IsCompleted ? helloAt.Task.Result : -1;
                    int msg = lines.FindIndex(l => l.Contains("no debugger within 3000 ms"));
                    int read = lines.FindIndex(l => l == "gelesen spaet");
                    int thrown = lines.FindIndex(l => l.Contains("ArgumentException") && l.Contains("zu gross: 3"));
                    bool ok = hello >= 0 && sent && messageAt - hello >= 2500 && msg >= 0 && read > msg && thrown > read;
                    return (ok, $"hello at {hello} ms, message at {messageAt} ms\n" + string.Join("\n", lines.SkipWhile(l => !l.Contains("app_main"))));
                }
                finally { try { qp.Kill(); qp.WaitForExit(5000); } catch (Exception) { } }
            });
    }

    static bool TestSelectedName(string name)
    {
        string? filter = Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER");
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
    }
}
