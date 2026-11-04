using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using fire.Debugging;
using fire.Runtime;

/// <summary>
/// NativeDebugTarget against a fake agent: a small TCP client in this process that speaks docs/spec/NATIVE_DEBUG_PROTOCOL.md like a native debug build would
/// (hello, stopped, thread, exited; breakpoints, continue, step, pause, trace, scopes, globals, this, expand, set, resume, terminate) over a simulated
/// program - so the spark side is tested without the runtime of the native backend.
/// </summary>
static partial class DebuggerTests
{
    /// <summary>One step of a simulated thread: where it stands, how deep, and what happens on entering it.</summary>
    internal sealed record FakeStep(int Source, int Line, int Depth, string? Throws = null, bool SpawnThread = false);

    /// <summary>The fake agent: connects to the listener named by FIRE_DEBUG and plays the program.</summary>
    internal sealed class FakeAgent : IDisposable
    {
        private sealed class FakeThread
        {
            public long Id;
            public string Name = "";
            public List<FakeStep> Steps = new();
            public int Pc;               // the step it stands on
            public bool Paused = true;
            public bool Ended;
            public bool Unhandled;
            public (int, int) Last;
            public int LoopFrom = -1;    // >= 0: after the last step it goes on at this step (an endless loop)
            // what it is doing while it runs
            public string Mode = "";     // "continue", "end", "line", "into", "out"
            public int StartLine, StartDepth;
            public volatile bool PauseRequested;
        }

        // the wire: TCP (one message per line), or framed over a stream (a board's console UART: frames between console lines)
        private readonly TcpClient? _client;
        private readonly StreamReader? _reader;
        private readonly StreamWriter? _writer;
        private readonly Stream? _wire;
        private readonly FramedDebugTransport? _framedIn;
        private readonly object _sendLock = new();
        /// <summary>Framed: the plain lines that came (the program's standard input).</summary>
        public readonly ConcurrentQueue<string> Input = new();
        /// <summary>Framed: write a console line before every message (the log of a board between the frames).</summary>
        public bool Chatty;
        private readonly object _lock = new();
        private readonly Dictionary<long, FakeThread> _threads = new();
        private HashSet<(int, int)> _breakpoints = new();
        private readonly Dictionary<string, (string Kind, string Display)> _values = new();
        private readonly List<FakeStep> _threadSteps;
        public readonly ConcurrentQueue<string> Received = new();
        private readonly Thread _loop;
        private readonly Thread _runner;
        private volatile bool _done;
        public int FirstUser { get; }

        public FakeAgent(string fireDebug, List<FakeStep> mainSteps, List<FakeStep>? threadSteps = null, string? tokenOverride = null, int loopFrom = -1, int firstUser = 1)
        {
            FirstUser = firstUser;
            // FIRE_DEBUG=tcp:127.0.0.1:PORT;token=T
            string spec = fireDebug["tcp:".Length..];
            string hostPort = spec[..spec.IndexOf(';')];
            string token = tokenOverride ?? spec[(spec.IndexOf("token=", StringComparison.Ordinal) + 6)..];
            int port = int.Parse(hostPort[(hostPort.LastIndexOf(':') + 1)..]);
            _client = new TcpClient("127.0.0.1", port);
            _reader = new StreamReader(_client.GetStream(), new UTF8Encoding(false));
            _writer = new StreamWriter(_client.GetStream(), new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            _threadSteps = threadSteps ?? new List<FakeStep>();
            (_loop, _runner) = Begin(mainSteps, token, loopFrom);
        }

        /// <summary>A board on a serial line (framed, docs/spec/NATIVE_DEBUG_PROTOCOL.md "Transport"): first the boot log and a garbled frame, then the hello
        /// (in two pieces, the second after a pause), between the messages console lines, and one frame in the middle of a console line.</summary>
        public FakeAgent(Stream wire, List<FakeStep> mainSteps, List<FakeStep>? threadSteps = null)
        {
            FirstUser = 1;
            _wire = wire;
            _framedIn = new FramedDebugTransport(wire, wire) { Console = Input.Enqueue };
            _threadSteps = threadSteps ?? new List<FakeStep>();
            Raw("ets Jul 29 2019 12:21:46\r\n\r\nrst:0x1 (POWERON_RESET),boot:0x12 (SPI_FAST_FLASH_BOOT)\r\n");
            Raw("I (938) boot: ESP-IDF v5.5 2nd stage bootloader\r\n");
            var garbled = FramedDebugTransport.Frame("{\"event\":\"hello\",\"protocol\":2}");
            garbled[10] ^= 0x20;   // (a bit flipped on the wire: the CRC does not match)
            RawBytes(garbled);
            Raw("I (4464) main_task: Calling app_main()\r\n");
            (_loop, _runner) = Begin(mainSteps, "", -1);
        }

        private void Raw(string text) => RawBytes(Encoding.UTF8.GetBytes(text));

        private void RawBytes(byte[] bytes)
        {
            try { lock (_sendLock) { _wire!.Write(bytes); _wire.Flush(); } }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }

        private (Thread Loop, Thread Runner) Begin(List<FakeStep> mainSteps, string token, int loopFrom)
        {
            _threads[1] = new FakeThread { Id = 1, Name = "Main", Steps = mainSteps, Pc = 0, LoopFrom = loopFrom };
            _values["l:1:0:0"] = ("int", "3");
            _values["l:1:0:1"] = ("object", "Point");
            _values["l:1:0:1/0"] = ("int", "1");
            _values["l:1:0:1/1"] = ("string", "p");
            _values["g:0"] = ("int", "5");
            _values["g:1"] = ("array", "Array[2]");
            _values["g:1/0"] = ("int", "10");
            _values["g:1/1"] = ("int", "20");
            Send(new Dictionary<string, object?>
            {
                ["event"] = "hello", ["protocol"] = 1L, ["token"] = token, ["engine"] = "native", ["pid"] = 4711L, ["target"] = "linux", ["mode"] = "debug",
                ["firstUserSource"] = (long)FirstUser, ["mainThread"] = 1L,
                ["sources"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["index"] = 0L, ["file"] = null, ["hash"] = null },
                    new Dictionary<string, object?> { ["index"] = (long)FirstUser, ["file"] = "/tmp/fake/main.fire", ["hash"] = NativeDebugTarget.HashOf("print(1)\n") },
                },
            });
            var main = _threads[1];
            main.Last = (mainSteps[0].Source, mainSteps[0].Line);
            Stopped(main, "entry");
            var loop = new Thread(Loop) { IsBackground = true };
            loop.Start();
            var runner = new Thread(Runner) { IsBackground = true };
            runner.Start();
            return (loop, runner);
        }

        private int _sent;

        private void Send(Dictionary<string, object?> message)
        {
            string json = DebugJson.Write(message);
            if (_wire == null)
            {
                try { lock (_sendLock) _writer!.WriteLine(json); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                return;
            }
            var frame = FramedDebugTransport.Frame(json);
            lock (_sendLock)
            {
                int n = _sent++;
                if (n == 0)
                {
                    // the hello in two pieces
                    RawBytes(frame[..20]);
                    Thread.Sleep(30);
                    RawBytes(frame[20..]);
                    return;
                }
                if (n == 1)
                {
                    // a frame in the middle of a console line: the console gets "half one, half two"
                    Raw("half one, ");
                    RawBytes(frame);
                    Raw("half two\r\n");
                    return;
                }
                if (Chatty) Raw($"I ({n}) app: line {n}\r\n");
                RawBytes(frame);
            }
        }

        private void Stopped(FakeThread t, string reason, Dictionary<string, object?>? exception = null)
        {
            var step = t.Steps[t.Pc];
            t.Paused = true;
            t.Mode = "";
            var e = new Dictionary<string, object?> { ["event"] = "stopped", ["thread"] = t.Id, ["reason"] = reason, ["source"] = (long)step.Source, ["line"] = (long)step.Line, ["depth"] = (long)step.Depth };
            if (exception != null) e["exception"] = exception;
            Send(e);
        }

        private void Loop()
        {
            try
            {
                while (!_done)
                {
                    string? line = _framedIn != null ? _framedIn.ReadMessage() : _reader!.ReadLine();
                    if (line == null) break;
                    Received.Enqueue(line);
                    var cmd = DebugJson.ParseObject(line);
                    var answer = Handle(cmd);
                    answer["re"] = cmd.Num("id");
                    Send(answer);
                    if (cmd.Str("cmd") == "terminate") { Exit(1); break; }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or FormatException) { }
        }

        private static Dictionary<string, object?> Ok(params (string, object?)[] fields)
        {
            var d = new Dictionary<string, object?> { ["ok"] = true };
            foreach (var (k, v) in fields) d[k] = v;
            return d;
        }

        private static Dictionary<string, object?> Err(string error) => new() { ["ok"] = false, ["error"] = error };

        private Dictionary<string, object?> V(string name, string path)
        {
            var (kind, display) = _values[path];
            var v = new Dictionary<string, object?> { ["name"] = name, ["kind"] = kind, ["display"] = display, ["ref"] = path };
            if (kind is "object" or "array") v["expandable"] = true;
            if (kind == "object") v["class"] = display;
            return v;
        }

        private Dictionary<string, object?> Handle(Dictionary<string, object?> cmd)
        {
            lock (_lock)
            {
                FakeThread? t = cmd.Num("thread") is long id && _threads.TryGetValue(id, out var ft) ? ft : null;
                switch (cmd.Str("cmd"))
                {
                    case "breakpoints":
                        _breakpoints = cmd.Arr("breakpoints").OfType<List<object?>>().Select(b => ((int)(long)b[0]!, (int)(long)b[1]!)).ToHashSet();
                        return Ok();
                    case "continue":
                        if (t == null || !t.Paused) return Err("thread is not paused");
                        if (t.Unhandled) { Exit(1); return Ok(); }
                        t.Paused = false;
                        t.Mode = cmd.TryGetValue("breakpoints", out var bp) && bp is false ? "end" : "continue";
                        return Ok();
                    case "step":
                        if (t == null || !t.Paused) return Err("thread is not paused");
                        var at = t.Steps[t.Pc];
                        string kind = cmd.Str("kind") ?? "line";
                        if (kind == "out" && at.Depth == 0) return Err("step out at the top level");
                        t.StartLine = at.Line;
                        t.StartDepth = at.Depth;
                        t.Paused = false;
                        t.Mode = kind;
                        return Ok();
                    case "pause":
                        foreach (var th in t != null ? new[] { t } : _threads.Values.ToArray()) if (!th.Paused) th.PauseRequested = true;
                        return Ok();
                    case "threads":
                        return Ok(("threads", _threads.Values.Select(th => (object?)new Dictionary<string, object?> { ["id"] = th.Id, ["name"] = th.Name, ["state"] = th.Paused ? "paused" : "running" }).ToList()));
                    case "trace":
                    {
                        if (t == null || !t.Paused) return Err("thread is not paused");
                        var frames = new List<object?>();
                        var cur = t.Steps[t.Pc];
                        frames.Add(new Dictionary<string, object?> { ["function"] = "f", ["source"] = (long)cur.Source, ["line"] = (long)cur.Line });
                        for (int d = cur.Depth; d > 0; d--) frames.Add(new Dictionary<string, object?> { ["function"] = "main", ["source"] = (long)cur.Source, ["line"] = 99L + d });
                        return Ok(("frames", frames));
                    }
                    case "scopes":
                        if (t == null || !t.Paused) return Err("thread is not paused");
                        return Ok(("scopes", new List<object?> { new Dictionary<string, object?> { ["depth"] = 0L, ["functionTop"] = true, ["variables"] = new List<object?> { V("count", "l:1:0:0"), V("p", "l:1:0:1") } } }));
                    case "globals":
                    {
                        var list = new List<object?>();
                        if (t != null && t.Id != 1)
                        {
                            if (!t.Paused) return Err("the thread is not paused");
                            _values.TryAdd($"T:{t.Id}:0", ("int", "77"));
                            list.Add(V("k", $"T:{t.Id}:0"));
                        }
                        list.Add(V("g", "g:0"));
                        list.Add(V("xs", "g:1"));
                        return Ok(("variables", list));
                    }
                    case "this":
                        if (t == null || !t.Paused) return Err("thread is not paused");
                        return Ok(("value", t.Steps[t.Pc].Depth > 0 ? V("this", "l:1:0:1") : null));
                    case "expand":
                    {
                        string path = cmd.Str("ref") ?? "";
                        if (!_values.ContainsKey(path)) return Err("no such value");
                        var children = _values.Keys.Where(k => k.StartsWith(path + "/", StringComparison.Ordinal) && !k[(path.Length + 1)..].Contains('/')).OrderBy(k => k)
                            .Select(k => (object?)V(_values[path].Kind == "array" ? $"[{k[(path.Length + 1)..]}]" : k.EndsWith("/0") ? "x" : "name", k)).ToList();
                        return Ok(("children", children));
                    }
                    case "set":
                    {
                        if (t == null || !t.Paused) return Err("thread is not paused");
                        string path = cmd.Str("ref") ?? "";
                        string value = cmd.Str("value") ?? "";
                        if (!_values.TryGetValue(path, out var old)) return Err("no such value");
                        if (old.Kind == "int" && !long.TryParse(value, out _)) return Err($"'{value}' is not an int");
                        if (old.Kind == "string" && !(value.StartsWith('"') && value.EndsWith('"'))) return Err("a string needs quotes");
                        string display = old.Kind == "string" ? value[1..^1] : value;
                        _values[path] = (old.Kind, display);
                        return Ok(("value", V("?", path)));
                    }
                    case "resume":
                        if (t == null || !t.Unhandled) return Err("not stopped at an unhandled exception");
                        t.Unhandled = false;
                        return Ok();
                    case "terminate":
                        return Ok();
                    default:
                        return Err("unknown command");
                }
            }
        }

        private void Exit(int code)
        {
            if (_done) return;
            foreach (var t in _threads.Values) t.Ended = true;
            Send(new Dictionary<string, object?> { ["event"] = "exited", ["code"] = (long)code });
            _done = true;
            try { _client?.Close(); } catch (SocketException) { }
            try { _wire?.Dispose(); } catch (IOException) { }
        }

        /// <summary>Advances every running thread by one step per millisecond.</summary>
        private void Runner()
        {
            while (!_done)
            {
                Thread.Sleep(1);
                lock (_lock)
                {
                    foreach (var t in _threads.Values.ToList())
                    {
                        if (t.Paused || t.Ended || _done) continue;
                        if (t.PauseRequested) { t.PauseRequested = false; Stopped(t, "pause"); continue; }
                        // one step further
                        if (t.Pc + 1 >= t.Steps.Count)
                        {
                            if (t.LoopFrom >= 0) t.Pc = t.LoopFrom;
                            else
                            {
                                t.Ended = true;
                                if (t.Id == 1) Exit(0);
                                else Send(new Dictionary<string, object?> { ["event"] = "threadExited", ["thread"] = t.Id });
                                continue;
                            }
                        }
                        else t.Pc++;
                        var step = t.Steps[t.Pc];
                        var location = (step.Source, step.Line);
                        bool entered = location != t.Last;
                        t.Last = location;
                        if (step.SpawnThread && !_threads.ContainsKey(2))
                        {
                            var nt = new FakeThread { Id = 2, Name = "Fire #1", Steps = _threadSteps, Pc = 0, Paused = false, Mode = "continue", Last = (_threadSteps[0].Source, _threadSteps[0].Line) };
                            _threads[2] = nt;
                            Send(new Dictionary<string, object?> { ["event"] = "thread", ["thread"] = 2L, ["name"] = "Fire #1" });
                            if (_breakpoints.Contains(nt.Last)) Stopped(nt, "breakpoint");
                        }
                        if (step.Throws != null)
                        {
                            t.Unhandled = true;
                            Stopped(t, "unhandled", new Dictionary<string, object?> { ["class"] = step.Throws, ["message"] = "boom" });
                            continue;
                        }
                        switch (t.Mode)
                        {
                            case "continue" when entered && _breakpoints.Contains(location): Stopped(t, "breakpoint"); break;
                            case "line" when step.Depth <= t.StartDepth && step.Line != t.StartLine: Stopped(t, "step"); break;
                            case "into" when step.Line != t.StartLine || step.Depth != t.StartDepth: Stopped(t, "step"); break;
                            case "out" when step.Depth < t.StartDepth: Stopped(t, "step"); break;
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            _done = true;
            try { _client?.Close(); } catch (SocketException) { }
            try { _wire?.Dispose(); } catch (IOException) { }
        }
    }

    /// <summary>A fake program: main (line 1..) calls a function at line 3 that runs lines 10, 11 (depth 1), then lines 4 and 5.</summary>
    internal static List<FakeStep> FakeMain(int s = 1, bool throwAt4 = false, bool spawnAt2 = false) => new()
    {
        new(s, 1, 0), new(s, 2, 0, SpawnThread: spawnAt2), new(s, 3, 0), new(s, 10, 1), new(s, 11, 1), new(s, 3, 0), new(s, 4, 0, Throws: throwAt4 ? "ArgumentException" : null), new(s, 5, 0),
    };

    /// <summary>An in-memory serial line: two ends, what one writes the other reads (blocking; the end of one end ends the reading of the other).</summary>
    internal sealed class SerialLine
    {
        private sealed class Queue
        {
            private readonly System.Collections.Generic.Queue<byte> _bytes = new();
            private bool _closed;
            public void Put(ReadOnlySpan<byte> data)
            {
                lock (_bytes)
                {
                    foreach (byte b in data) _bytes.Enqueue(b);
                    Monitor.PulseAll(_bytes);
                }
            }
            public int Take(Span<byte> into)
            {
                lock (_bytes)
                {
                    while (_bytes.Count == 0 && !_closed) Monitor.Wait(_bytes);
                    int n = 0;
                    while (n < into.Length && _bytes.Count > 0) into[n++] = _bytes.Dequeue();
                    return n;
                }
            }
            public void Close() { lock (_bytes) { _closed = true; Monitor.PulseAll(_bytes); } }
        }

        private sealed class End : Stream
        {
            private readonly Queue _in, _out;
            public End(Queue @in, Queue @out) { _in = @in; _out = @out; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => _in.Take(buffer.AsSpan(offset, count));
            public override void Write(byte[] buffer, int offset, int count) => _out.Put(buffer.AsSpan(offset, count));
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { _in.Close(); _out.Close(); base.Dispose(disposing); }
        }

        public readonly Stream Host, Device;

        public SerialLine()
        {
            var toDevice = new Queue();
            var toHost = new Queue();
            Host = new End(toHost, toDevice);
            Device = new End(toDevice, toHost);
        }
    }

    /// <summary>spark's SerialFramedTransport against a fake board (an in-memory serial line): the boot log before the hello and the console between the frames
    /// go to the Output, a garbled frame is dropped, a frame in the middle of a console line is cut out of it, input lines reach the board as plain lines; a fire
    /// thread's globals (its own variables of its top level first, `globals` with its thread).</summary>
    private static void FramedChecks()
    {
        var line = new SerialLine();
        var steps = FakeMain(spawnAt2: true);
        var threadSteps = new List<FakeStep> { new(1, 20, 0), new(1, 21, 0), new(1, 22, 0) };
        var agent = new FakeAgent(line.Device, steps, threadSteps) { Chatty = true };
        var transport = new SerialFramedTransport(line.Host);
        var early = new ConcurrentQueue<string>();
        NativeDebugTarget? target = null;
        try { target = NativeDebugTarget.OpenFramed(transport, TimeSpan.FromSeconds(20), early.Enqueue); }
        catch (NativeDebugException ex) { Check(false, "SerialFramedTransport: hello ueber die serielle Leitung", ex.Message); agent.Dispose(); return; }
        using var targetScope = target;
        var output = new ConcurrentQueue<string>();
        target.Output += (_, text) => output.Enqueue(text);
        var main = target.MainThread;
        string earlyText = string.Join("|", early);
        Check(early.Any(l => l == "rst:0x1 (POWERON_RESET),boot:0x12 (SPI_FAST_FLASH_BOOT)") && early.Any(l => l == "I (4464) main_task: Calling app_main()") && !earlyText.Contains("FD:")
            && transport.DroppedFrames == 1 && target.Transport == "stdio" && target.CarriesConsole && main.CurrentLocation == (1, 1)
            && agent.Received.FirstOrDefault()?.Contains("\"cmd\":\"threads\"") == true,
            "SerialFramedTransport: das Boot-Log vor dem hello geht in die Ausgabe (ohne \\r), ein verfaelschter Rahmen wird verworfen, der erste Befehl kommt sofort (threads)",
            $"{earlyText} | dropped {transport.DroppedFrames} | {target.Transport} | {string.Join(" ", agent.Received)}");
        // (the second message - the stop at the entry - came in the middle of a console line)
        Check(early.Contains("half one, half two") || output.Contains("half one, half two"), "SerialFramedTransport: ein Rahmen mitten in einer Konsolenzeile wird herausgeschnitten",
            string.Join("|", early.Concat(output)));

        var started = new BlockingCollection<IDebugThread>();
        target.ThreadStarted += started.Add;
        var bps = new HashSet<(int, int)> { (1, 21), (1, 4) };
        bool mainStopped = main.RunUntilBreakpoint(bps, () => false);
        bool got = started.TryTake(out var fireThread, TimeSpan.FromSeconds(20));
        Thread.Sleep(100);
        bool threadStopped = got && fireThread!.RunUntilBreakpoint(bps, () => false);
        var threadGlobals = threadStopped ? fireThread!.Globals() : Array.Empty<DebugValue>();
        var mainGlobals = main.Globals();
        Check(mainStopped && main.CurrentLocation == (1, 4) && threadStopped && Show(threadGlobals) == "k=77,g=5,xs=Array[2]" && Show(mainGlobals) == "g=5,xs=Array[2]"
            && agent.Received.Any(l => l.Contains("\"cmd\":\"globals\"") && l.Contains("\"thread\":2")) && output.Any(l => l.StartsWith("I (", StringComparison.Ordinal)),
            "Rahmen: Haltepunkte in zwei Threads; die Globals eines fire-Threads zeigen zuerst seine eigenen Variablen (globals mit thread, T:2:0), die Konsole dazwischen kommt an",
            $"{mainStopped} {main.CurrentLocation} {threadStopped} {Show(threadGlobals)} | {Show(mainGlobals)}");
        var k = threadGlobals.FirstOrDefault(v => v.Name == "k");
        bool setK = k != null && fireThread!.TrySetValue(k, "5", out _) && agent.Received.Any(l => l.Contains("\"ref\":\"T:2:0\""));
        Check(setK && Show(fireThread!.Globals()).StartsWith("k=5,", StringComparison.Ordinal), "Rahmen: set einer eigenen Variable des Threads (T:2:0)", Show(fireThread?.Globals() ?? Array.Empty<DebugValue>()));

        bool sent = target.WriteInput("eine Eingabe") && target.WriteInput("\u001Ekein Rahmen");
        string? input1 = null, input2 = null;
        for (int i = 0; i < 100 && agent.Input.Count < 2; i++) Thread.Sleep(20);
        agent.Input.TryDequeue(out input1);
        agent.Input.TryDequeue(out input2);
        Check(sent && input1 == "eine Eingabe" && input2 == "kein Rahmen", "Rahmen: Eingabezeilen gehen als einfache Zeilen zum Board (ein \\x1E am Anfang macht keinen Rahmen daraus)", $"{input1} | {input2}");
        fireThread?.RunUntilEnd(() => false);
        bool ended = !main.RunUntilEnd(() => false);
        Check(ended && target.HasExited && target.ExitCode == 0, "Rahmen: das Programm endet (exited)");
        target.Dispose();
        agent.Dispose();

        // a board that starts again (a reset): a second hello ends the session
        var line2 = new SerialLine();
        var agent2 = new FakeAgent(line2.Device, FakeMain());
        var target2 = NativeDebugTarget.OpenFramed(new SerialFramedTransport(line2.Host), TimeSpan.FromSeconds(20));
        var exited = new TaskCompletionSource<int?>();
        target2.Exited += code => exited.TrySetResult(code);
        line2.Device.Write(FramedDebugTransport.Frame("{\"event\":\"hello\",\"protocol\":1,\"token\":\"\",\"mainThread\":1,\"sources\":[]}"));
        Check(exited.Task.Wait(TimeSpan.FromSeconds(10)) && target2.HasExited, "Rahmen: ein neues hello (das Board startete neu) beendet die Sitzung");
        target2.Dispose();
        agent2.Dispose();

        // no hello: a clear message (with what the board said)
        var line3 = new SerialLine();
        line3.Device.Write(Encoding.ASCII.GetBytes("I (100) app: some other program\r\n"));
        try
        {
            NativeDebugTarget.OpenFramed(new SerialFramedTransport(line3.Host), TimeSpan.FromSeconds(1));
            Check(false, "Rahmen: ohne hello eine klare Meldung");
        }
        catch (NativeDebugException ex) { Check(ex.Message.Contains("did not say hello") && ex.Message.Contains("some other program"), "Rahmen: ohne hello eine klare Meldung (mit der Konsole des Boards)", ex.Message); }

        // the CRC of the agent (CRC-16/CCITT-FALSE): the check value of "123456789" is 0x29B1
        Check(FramedDebugTransport.Crc16(Encoding.ASCII.GetBytes("123456789")) == 0x29B1, "CRC-16/CCITT-FALSE: Pruefwert");
        Check(SerialFramedTransport.PortFromFlashLog("esptool.py v4.8\nSerial port /dev/ttyUSB1\nConnecting....\nSerial port /dev/ttyUSB1:\n") == "/dev/ttyUSB1"
            && Esp32Launch.ProjectName("my-app 2") == "my_app_2" && Esp32Launch.ProjectName("3d") == "fire_3d",
            "ESP32: der Port aus der Ausgabe von esptool, der Name des ESP-IDF-Projekts");
    }

    private static (NativeDebugTarget Target, FakeAgent Agent) ConnectFake(List<FakeStep> main, List<FakeStep>? threadSteps = null, int loopFrom = -1, bool strayFirst = false)
    {
        using var listener = NativeDebugListener.Start();
        FakeAgent? agent = null, stray = null;
        var connect = Task.Run(() =>
        {
            if (strayFirst) stray = new FakeAgent(listener.EnvironmentValue, FakeMain(), tokenOverride: "wrong");
            agent = new FakeAgent(listener.EnvironmentValue, main, threadSteps, loopFrom: loopFrom);
        });
        var target = listener.Accept(TimeSpan.FromSeconds(20));
        connect.Wait(TimeSpan.FromSeconds(20));
        stray?.Dispose();
        return (target, agent!);
    }

    /// <summary>NativeDebugTarget and NativeLaunch: the protocol against the fake agent, the debug session on top, the start of processes.</summary>
    public static void NativeRun()
    {
        Console.WriteLine();
        Console.WriteLine("=== Natives Debug-Ziel (Protokoll gegen einen Schein-Agenten) ===");
        _failures = 0;
        var noBreakpoints = new HashSet<(int, int)>();

        {
            var (target, agent) = ConnectFake(FakeMain(), strayFirst: true);
            using var targetScope = target;
            var main = target.MainThread;
            Check(target.Engine == "native" && !target.HasOperandStack && main.OperandStack() == null && target.FirstUserSourceIndex == 1 && target.SourceFiles.Count == 2
                && target.SourceFiles[1] == "/tmp/fake/main.fire" && main.IsMain && main.CurrentLocation == (1, 1) && main.CallDepth == 0,
                "hello: Quellen, erste eigene Quelle, Hauptthread am Eintritt; eine Verbindung mit falschem Token wird uebergangen",
                $"{target.FirstUserSourceIndex} {string.Join(",", target.SourceFiles)} {main.CurrentLocation}");
            Check(target.SourcesNotMatching(new[] { "print(1)\n" }).Count == 0 && target.SourcesNotMatching(new[] { "print(2)\n" }).SequenceEqual(new[] { 1 }),
                "hello: Hashes der Quellen zeigen, ob die Dateien zum Programm passen");

            bool stopped = main.RunUntilBreakpoint(new HashSet<(int, int)> { (1, 10) }, () => false);
            var trace = main.CurrentTrace();
            Check(stopped && main.CurrentLocation == (1, 10) && main.CallDepth == 1 && trace.Count == 2 && trace[0] == (1, 10)
                && agent.Received.Any(l => l.Contains("\"cmd\":\"breakpoints\"") && l.Contains("[[1,10]]")) && agent.Received.Any(l => l.Contains("\"cmd\":\"continue\"")),
                "RunUntilBreakpoint: breakpoints, continue, stopped am Haltepunkt; trace", $"{stopped} {main.CurrentLocation} {main.CallDepth} {string.Join(" ", trace)}");

            var scopes = main.Scopes();
            var vars = scopes.SelectMany(s => s.Variables).ToList();
            var p = vars.FirstOrDefault(v => v.Name == "p");
            var globals = main.Globals();
            var xs = globals.FirstOrDefault(v => v.Name == "xs");
            Check(scopes.Count == 1 && scopes[0].IsFunctionTopLevel && Show(vars) == "count=3,p=Point" && p is { Kind: DebugValueKind.Object, HasChildren: true, CanSet: true }
                && Show(p.Children()) == "x=1,name=p" && Show(globals) == "g=5,xs=Array[2]" && xs != null && Show(xs.Children()) == "[0]=10,[1]=20"
                && main.This is { Kind: DebugValueKind.Object, Display: "Point" },
                "scopes, globals, this, expand: Werte mit Namen, Art und Kindern", $"{Show(vars)} | {Show(globals)}");

            var count = vars.First(v => v.Name == "count");
            bool bad = !main.TrySetValue(count, "abc", out string badError) && badError.Contains("not an int");
            bool good = main.TrySetValue(count, "40", out string goodError);
            bool str = main.TrySetValue(p!.Children().First(c => c.Name == "name"), "hi", out string strError);
            bool obj = !main.TrySetValue(p, "1", out _);
            Check(bad && good && str && obj && Show(main.Scopes().SelectMany(s => s.Variables)).Contains("count=40") && agent.Received.Any(l => l.Contains("\"value\":\"\\\"hi\\\"\"")),
                "set: Zahl, String (in Anfuehrungszeichen geschickt); falscher Text und ein Objekt werden abgelehnt", $"{badError} | {goodError} | {strError}");

            bool stepped = main.StepLine();   // 10 -> 11 (same depth)
            var afterLine = main.CurrentLocation;
            bool outed = main.StepOut();      // back to depth 0
            var afterOut = (main.CurrentLocation, main.CallDepth);
            bool outTop = main.StepOut();     // at the top level: refused, stays
            var afterTop = main.CurrentLocation;
            bool into = main.StepInto();      // 3 -> 4
            Check(stepped && afterLine == (1, 11) && outed && afterOut == ((1, 3), 0) && outTop && afterTop == (1, 3) && into && main.CurrentLocation == (1, 4)
                && agent.Received.Count(l => l.Contains("\"kind\":\"out\"")) == 2,
                "step line/out/into; step out auf oberster Ebene wird abgelehnt, der Thread bleibt stehen", $"{afterLine} {afterOut} {afterTop} {main.CurrentLocation}");

            bool ended = main.RunUntilEnd(() => false);
            Check(!ended && target.HasExited && target.ExitCode == 0 && main.CurrentTrace().Count == 1, "RunUntilEnd: das Programm endet (exited)", $"{ended} {target.HasExited} {target.ExitCode}");
            agent.Dispose();
        }

        {
            // pause: an endless loop stops on request
            var (target, agent) = ConnectFake(FakeMain(), loopFrom: 1);
            using var targetScope = target;
            int asked = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool paused = target.MainThread.RunUntilEnd(() => watch.ElapsedMilliseconds > 150 && Interlocked.Increment(ref asked) == 1);
            Check(paused && ((NativeDebugThread)target.MainThread).StopReason == "pause" && agent.Received.Any(l => l.Contains("\"cmd\":\"pause\"")),
                "pause: eine Endlosschleife haelt auf Anforderung an", $"{paused} {((NativeDebugThread)target.MainThread).StopReason}");
            target.Dispose();
            bool terminated = false;
            for (int i = 0; i < 100 && !terminated; i++) { terminated = agent.Received.Any(l => l.Contains("\"cmd\":\"terminate\"")); if (!terminated) Thread.Sleep(20); }
            Check(terminated && target.HasExited, "Dispose: terminate an das Programm");
            agent.Dispose();
        }

        {
            // unhandled exception: stops at the throw, resume goes on after it
            var (target, agent) = ConnectFake(FakeMain(throwAt4: true));
            using var targetScope = target;
            var main = target.MainThread;
            bool more = main.RunUntilBreakpoint(noBreakpoints, () => false);
            var trace = main.UnhandledTrace;
            Check(!more && main.IsPausedOnUnhandled && main.UnhandledError == "Unhandled exception of type 'ArgumentException': boom" && trace is { Count: 1 } && trace[0] == (1, 4)
                && main.CurrentLocation == (1, 4),
                "unbehandelte Ausnahme: der Thread steht am throw, Meldung wie die VM, Ort", $"{more} {main.UnhandledError} {main.CurrentLocation}");
            bool resumed = main.TryResumeUnhandled();
            bool end = main.RunUntilBreakpoint(noBreakpoints, () => false);
            Check(resumed && !main.IsPausedOnUnhandled && main.UnhandledError == null && !end && target.ExitCode == 0, "resume: der Thread geht nach dem throw weiter bis zum Ende");
            agent.Dispose();
        }

        {
            // a thread of the program: announced, runs in continue mode, its stop counts even when it came before the caller waited
            var threadSteps = new List<FakeStep> { new(1, 20, 0), new(1, 21, 0), new(1, 22, 0) };
            var (target, agent) = ConnectFake(FakeMain(spawnAt2: true), threadSteps);
            using var targetScope = target;
            var started = new BlockingCollection<IDebugThread>();
            target.ThreadStarted += started.Add;
            var bps = new HashSet<(int, int)> { (1, 21), (1, 4) };
            bool mainStopped = target.MainThread.RunUntilBreakpoint(bps, () => false);
            bool got = started.TryTake(out var fireThread, TimeSpan.FromSeconds(20));
            Thread.Sleep(100);   // the thread has reached its breakpoint by now
            bool threadStopped = got && fireThread!.RunUntilBreakpoint(bps, () => false);
            Check(mainStopped && target.MainThread.CurrentLocation == (1, 4) && got && fireThread is { IsMain: false, Name: "Fire #1" } && threadStopped
                && fireThread.CurrentLocation == (1, 21) && target.Threads.Count == 2,
                "Threads: das thread-Ereignis meldet einen Thread, er laeuft von selbst bis zu seinem Haltepunkt", $"{mainStopped} {got} {threadStopped} {fireThread?.CurrentLocation}");
            bool threadEnd = fireThread != null && fireThread.RunUntilEnd(() => false);
            Check(fireThread != null && !threadEnd && agent.Received.Count(l => l.Contains("\"cmd\":\"continue\"")) == 2 && !target.MainThread.RunUntilEnd(() => false),
                "threadExited beendet den Thread, danach endet das Programm");
            agent.Dispose();
        }

        FramedChecks();
        SessionOverNative();
        LaunchChecks();
        EndToEndNative();

        Console.WriteLine(_failures == 0 ? "Alle nativen Debug-Ziel-Pruefungen bestanden." : $"FEHLER: {_failures} native Debug-Ziel-Pruefung(en) fehlgeschlagen.");
    }

    /// <summary>spark's DebugSession over the native target: Continue, the paused event, set through the context, Run to the end.</summary>
    private static void SessionOverNative()
    {
        var (target, agent) = ConnectFake(FakeMain());
        var session = new fire.Editor.DebugSession();
        var paused = new BlockingCollection<fire.Editor.DebugThreadContext>();
        session.ThreadPaused += paused.Add;
        session.Attach(target);
        session.Continue(new HashSet<(int, int)> { (1, 11) });
        bool hit = paused.TryTake(out var ctx, TimeSpan.FromSeconds(20));
        Check(hit && session.HasProgram && session.Target?.Engine == "native" && ctx!.Thread.CurrentLocation == (1, 11) && !ctx.IsFinished && session.SourceFiles.Count == 2,
            "DebugSession ueber dem nativen Ziel: Continue haelt am Haltepunkt", $"{hit} {ctx?.Thread.CurrentLocation}");
        if (hit)
        {
            var count = ctx!.Thread.Scopes().SelectMany(s => s.Variables).First(v => v.Name == "count");
            string? result = null;
            bool requested = ctx.RequestSetValue(count, "7", (ok, error) => result = ok ? "ok" : error);
            bool after = paused.TryTake(out _, TimeSpan.FromSeconds(20));
            Check(requested && after && result == "ok" && Show(ctx.Thread.Scopes().SelectMany(s => s.Variables)).Contains("count=7"), "RequestSetValue ueber das native Ziel", result);
            session.RunToCompletion();
            bool ended = paused.TryTake(out var endCtx, TimeSpan.FromSeconds(20)) && endCtx!.IsFinished && session.IsFinished && session.RuntimeError == null;
            Check(ended, "RunToCompletion: das native Programm endet");
        }
        session.Reset();
        Check(!session.HasProgram && target.HasExited, "Reset beendet das native Programm");
        agent.Dispose();
    }

    /// <summary>NativeLaunch: a plain run (output and exit code), a debug start whose program ends without connecting, and - with bash - a program
    /// process that connects through FIRE_DEBUG (the environment, the token, the output before and after the connection).</summary>
    private static void LaunchChecks()
    {
        if (OperatingSystem.IsWindows()) { Console.WriteLine("(NativeLaunch: Prozess-Pruefungen nur unter Linux/macOS)"); return; }
        {
            var lines = new ConcurrentQueue<string>();
            var exit = new TaskCompletionSource<int>();
            using var process = NativeLaunch.Start("/bin/sh", new[] { "-c", "echo out; echo err 1>&2; exit 3" }, null, lines.Enqueue, code => exit.TrySetResult(code));
            bool done = exit.Task.Wait(TimeSpan.FromSeconds(20));
            for (int i = 0; i < 50 && lines.Count < 2; i++) Thread.Sleep(20);
            Check(done && exit.Task.Result == 3 && lines.Contains("out") && lines.Contains("err"), "NativeLaunch.Start: stdout und stderr zeilenweise, Exitcode", string.Join("|", lines));
        }
        {
            string? message = null;
            try { NativeLaunch.LaunchDebug("/bin/sh", new[] { "-c", "echo nope; exit 2" }, null, TimeSpan.FromSeconds(20)).Dispose(); }
            catch (NativeDebugException ex) { message = ex.Message; }
            Check(message != null && message.Contains("exit code 2") && message.Contains("nope"), "LaunchDebug: ein Programm, das sich nicht verbindet, wird mit seiner Ausgabe gemeldet", message);
        }
        if (File.Exists("/bin/bash"))
        {
            // a "program" in bash: reads FIRE_DEBUG, connects, says hello and stops at its entry, answers one continue and exits
            const string script = """
                spec=${FIRE_DEBUG#tcp:}; hostport=${spec%%;*}; token=${spec#*token=}; host=${hostport%:*}; port=${hostport##*:}
                exec 3<>/dev/tcp/$host/$port
                echo "before connect"
                printf '{"event":"hello","protocol":1,"token":"%s","engine":"native","pid":%d,"firstUserSource":1,"mainThread":1,"sources":[{"index":0,"file":null,"hash":null},{"index":1,"file":null,"hash":null}]}\n' "$token" $$ >&3
                printf '{"event":"stopped","thread":1,"reason":"entry","source":1,"line":1,"depth":0}\n' >&3
                read -r line <&3
                id=$(echo "$line" | sed 's/.*"id":\([0-9]*\).*/\1/')
                echo "got $line"
                printf '{"re":%s,"ok":true}\n' "$id" >&3
                printf '{"event":"exited","code":0}\n' >&3
                exec 3>&-
                """;
            var lines = new ConcurrentQueue<string>();
            try
            {
                using var target = NativeLaunch.LaunchDebug("/bin/bash", new[] { "-c", script }, null, TimeSpan.FromSeconds(20));
                target.Output += (_, line) => lines.Enqueue(line);
                bool atEntry = target.MainThread.CurrentLocation == (1, 1);
                bool more = target.MainThread.RunUntilEnd(() => false);
                for (int i = 0; i < 100 && !lines.Any(l => l.StartsWith("got ")); i++) Thread.Sleep(20);
                Check(atEntry && !more && target.HasExited && lines.Contains("before connect") && lines.Any(l => l.StartsWith("got ") && l.Contains("\"cmd\":\"continue\"")),
                    "LaunchDebug: das Programm bekommt FIRE_DEBUG, verbindet sich mit Token, seine Ausgabe kommt an", string.Join(" | ", lines));
            }
            catch (NativeDebugException ex) { Check(false, "LaunchDebug mit bash", ex.Message); }
        }
    }

    /// <summary>The real thing: a native debug build of the host (-O0 -g, the debug agent of the runtime), started with FIRE_DEBUG, against the VM - the stop at a
    /// breakpoint in a method, depth, locals, this, set, the output of the process. Skipped while the runtime has no debug agent (the generator of a debug
    /// build does not emit fire_dbgStart) or no C compiler is there.</summary>
    private static void EndToEndNative()
    {
        const string script = """
            class Box {
                int v = 2
                int Twice(int k) {
                    var t = k * this.v
                    return t
                }
            }
            var b = new Box()
            var n = b.Twice(3)
            print("n " + n)
            throw new Exception("boom")
            print("after")
            """;
        var config = new fire.Native.NativeConfig();
        var target = fire.Runtime.TargetProfile.Host;
        var toolchain = config.ResolveToolchain(target);
        string generated;
        try { generated = fire.Compiler.NativeBuilder.Generate(new[] { script }, target, VmExecutionMode.Debug); }
        catch (Exception ex) { Console.WriteLine("(Ende-zu-Ende nativ uebersprungen: " + ex.Message + ")"); return; }
        if (!generated.Contains("fire_dbgStart") || !NativeLaunch.CanRunHere(target, toolchain, out _) || fire.Native.ToolchainDetector.Find(toolchain) == null)
        {
            Console.WriteLine("(Ende-zu-Ende nativ uebersprungen: die Runtime hat noch keinen Debug-Agenten oder es gibt keinen C-Compiler)");
            return;
        }
        string dir = Path.Combine(Path.GetTempPath(), "fire-dbg-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "box.exe" : "box");
            var built = fire.Compiler.NativeBuilder.BuildSafe(new[] { script }, config, target, NativeLaunch.DebugToolchain(toolchain), exe, VmExecutionMode.Debug);
            Check(built.Ok, "Ende-zu-Ende: Debug-Build (-O0 -g) gelingt", built.Log.Length > 2000 ? built.Log[..2000] : built.Log);
            if (!built.Ok) return;

            // the VM as the reference: the stop at line 5, then t = 10 and Step Over to the unhandled exception, then on after it
            VM.ResetTerminateForTests();
            var vmOutput = new ConcurrentQueue<string>();
            using var vm = VmDebugTarget.Build(new[] { script }, VmExecutionMode.Debug);
            vm.Output += (_, line) => vmOutput.Enqueue(line);
            int vmSrc = vm.FirstUserSourceIndex;
            vm.MainThread.RunUntilBreakpoint(new HashSet<(int, int)> { (vmSrc, 5) }, () => false);
            string vmLocals = Show(vm.MainThread.Scopes().SelectMany(s => s.Variables));
            int vmDepth = vm.MainThread.CallDepth;
            vm.MainThread.TrySetValue(vm.MainThread.Scopes().SelectMany(s => s.Variables).First(v => v.Name == "t"), "10", out _);
            var vmSteps = new List<string>();
            while (vm.MainThread.StepLine()) vmSteps.Add($"{vm.MainThread.CurrentLocation.Line}/{vm.MainThread.CallDepth}");
            string vmStop = $"{vm.MainThread.IsPausedOnUnhandled} {vm.MainThread.CurrentLocation.Line} {vm.MainThread.UnhandledError}";
            vm.MainThread.TryResumeUnhandled();
            vm.MainThread.RunUntilEnd(() => false);
            VM.ResetTerminateForTests();

            var lines = new ConcurrentQueue<string>();
            using var native = NativeLaunch.LaunchDebug(built.Output, Array.Empty<string>(), dir, TimeSpan.FromSeconds(30));
            native.Output += (_, line) => lines.Enqueue(line);
            int src = native.FirstUserSourceIndex;
            var main = native.MainThread;
            bool hit = main.RunUntilBreakpoint(new HashSet<(int, int)> { (src, 5) }, () => false);
            var locals = main.Scopes().SelectMany(s => s.Variables).ToList();
            var self = main.This;
            Check(hit && main.CurrentLocation == (src, 5) && main.CallDepth == vmDepth && Show(locals) == vmLocals && self is { Kind: DebugValueKind.Object }
                && Show(self.Children()).Contains("v=2") && native.SourcesNotMatching(new[] { script }).Count == 0,
                "Ende-zu-Ende: Haltepunkt in einer Methode, Tiefe und Locals wie die VM, this mit Feldern", $"{hit} {main.CurrentLocation} depth {main.CallDepth}/{vmDepth} | {Show(locals)} | VM {vmLocals}");
            bool set = locals.FirstOrDefault(v => v.Name == "t") is { } t && main.TrySetValue(t, "10", out _);
            var steps = new List<string>();
            while (main.StepLine()) steps.Add($"{main.CurrentLocation.Line}/{main.CallDepth}");
            string stop = $"{main.IsPausedOnUnhandled} {main.CurrentLocation.Line} {main.UnhandledError}";
            Check(set && steps.SequenceEqual(vmSteps) && stop == vmStop && main.UnhandledTrace is { Count: > 0 } ut && ut[0] == (src, 11),
                "Ende-zu-Ende: set, Step Over bis zur unbehandelten Ausnahme wie die VM (Zeilen, Tiefen, Meldung, Ort)", $"{string.Join(" ", steps)} | VM {string.Join(" ", vmSteps)} | {stop} | VM {vmStop}");
            bool resumed = main.TryResumeUnhandled();
            bool more = main.RunUntilEnd(() => false);
            for (int i = 0; i < 100 && !lines.Contains("after"); i++) Thread.Sleep(20);
            Check(resumed && !more && native.HasExited && lines.Contains("n 10") && lines.Contains("after") && vmOutput.SequenceEqual(new[] { "n 10", "after" }),
                "Ende-zu-Ende: resume nach der Ausnahme, das Programm endet, seine Ausgabe (mit dem gesetzten Wert) kommt an", string.Join(" | ", lines) + " | VM " + string.Join(" | ", vmOutput));
        }
        catch (NativeDebugException ex) { Check(false, "Ende-zu-Ende nativ", ex.Message); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
