using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using fire.Bytecode;
using fire.Compiler;
using fire.Runtime;
using fire.Values;

/// <summary>
/// The debugger of native debug builds (docs/NATIVE_DEBUG_PROTOCOL.md): a client that launches a native program with FIRE_DEBUG and drives it over the
/// protocol, the same way spark drives the VM.
/// </summary>
sealed class NativeDebugClient : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Process _process;
    private TcpClient? _client;
    private StreamWriter? _writer;
    private readonly BlockingCollection<JsonElement> _replies = new();
    private readonly BlockingCollection<JsonElement> _events = new();
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private int _nextId = 1;
    public JsonElement Hello { get; private set; }
    public string Output { get { lock (_stdout) return _stdout.ToString(); } }
    public string Errors { get { lock (_stderr) return _stderr.ToString(); } }
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private NativeDebugClient(string exe, string workDir, string token)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        var start = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workDir };
        start.Environment["FIRE_DEBUG"] = $"tcp:127.0.0.1:{port};token={token}";
        _process = Process.Start(start)!;
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_stdout) _stdout.Append(e.Data).Append('\n'); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) _stderr.Append(e.Data).Append('\n'); };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>Starts the program and waits for its hello (and the stop at the entry).</summary>
    public static NativeDebugClient Launch(string exe, string workDir)
    {
        string token = Guid.NewGuid().ToString("N");
        var c = new NativeDebugClient(exe, workDir, token);
        var accept = c._listener.AcceptTcpClientAsync();
        if (!accept.Wait(Timeout)) throw new TimeoutException("the program did not connect: " + c.Errors);
        c._client = accept.Result;
        c._client.NoDelay = true;
        var stream = c._client.GetStream();
        c._writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var reader = new StreamReader(stream, new UTF8Encoding(false));
        new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    var e = JsonDocument.Parse(line).RootElement.Clone();
                    if (e.TryGetProperty("re", out _)) c._replies.Add(e); else c._events.Add(e);
                }
            }
            catch (Exception) { }
            c._replies.CompleteAdding();
            c._events.CompleteAdding();
        }) { IsBackground = true }.Start();
        c.Hello = c.NextEvent("hello");
        if (c.Hello.GetProperty("token").GetString() != token) throw new InvalidOperationException("wrong token");
        return c;
    }

    /// <summary>The next event (skipping none: an unexpected one is an error).</summary>
    public JsonElement NextEvent(string? name = null)
    {
        if (!_events.TryTake(out var e, Timeout)) throw new TimeoutException($"no event ({name}); stderr: {Errors}");
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
        _writer!.WriteLine(JsonSerializer.Serialize(message));
        if (!_replies.TryTake(out var reply, Timeout)) throw new TimeoutException($"no reply to {cmd}; stderr: {Errors}");
        if (reply.GetProperty("re").GetInt32() != id) throw new InvalidOperationException($"reply {reply} for {id}");
        return reply;
    }

    public static bool Ok(JsonElement reply) => reply.GetProperty("ok").GetBoolean();

    public bool WaitExit(TimeSpan timeout) => _process.WaitForExit((int)timeout.TotalMilliseconds);

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(); } catch (Exception) { }
        _process.WaitForExit(5000);
        _client?.Dispose();
        _listener.Stop();
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
    static (List<string> Stops, List<string> Output, string Errors) RunNative(string exe, string dir, int[] bps, IEnumerable<Op> plan, bool details, Func<Op?>? stepAll = null)
    {
        using var c = NativeDebugClient.Launch(exe, dir);
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
    static string? Build(string script, string cxx, string dir, string name, string flags, out string error)
    {
        error = "";
        fire.Native.NativeSources generated;
        try { generated = fire.Native.CppGenerator.GenerateSources(new Linker().CompileAndLink(new[] { script }, null, null, VmExecutionMode.Debug, null, TargetProfile.Host), TargetProfile.Host); }
        catch (Exception ex) { error = "nicht uebersetzbar: " + ex.Message; return null; }
        string exe = Path.Combine(dir, name + ".bin");
        string build = NativeTestBuild.Build(NativeTestBuild.In(dir), cxx, generated, dir, name, flags, exe, out int exit);
        if (exit != 0 || build.Contains("warning:")) { error = "C-Compiler: " + build; return null; }
        return exe;
    }

    public static void Run(Action<string, bool, string?> check, string cxx, string workDir, string sanitize)
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

    static bool TestSelectedName(string name)
    {
        string? filter = Environment.GetEnvironmentVariable("FIRE_TESTS_FILTER");
        if (string.IsNullOrWhiteSpace(filter)) return true;
        return filter.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
    }
}
