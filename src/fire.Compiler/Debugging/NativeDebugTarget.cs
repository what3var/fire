using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace fire.Debugging
{
    /// <summary>A failure of the native debug connection (no program connected, a wrong hello, the connection broke).</summary>
    public sealed class NativeDebugException : Exception
    {
        public NativeDebugException(string message) : base(message) { }
    }

    /// <summary>
    /// The listening side of the native debug protocol (docs/spec/NATIVE_DEBUG_PROTOCOL.md, ROADMAP 8.2): spark listens on 127.0.0.1 on a free port and hands
    /// the program <see cref="EnvironmentValue"/> as FIRE_DEBUG; the program connects and sends `hello` with the token. <see cref="Accept"/> takes the
    /// first connection whose hello carries the token (others are closed) and stops listening.
    /// </summary>
    public sealed class NativeDebugListener : IDisposable
    {
        /// <summary>The variable the program reads.</summary>
        public const string EnvironmentVariable = "FIRE_DEBUG";

        private readonly TcpListener _listener;

        private NativeDebugListener(TcpListener listener, string token)
        {
            _listener = listener;
            Token = token;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string Token { get; }
        /// <summary>`tcp:127.0.0.1:PORT;token=T` - the value of FIRE_DEBUG for the program.</summary>
        public string EnvironmentValue => $"tcp:127.0.0.1:{Port};token={Token}";

        public static NativeDebugListener Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(4);
            return new NativeDebugListener(listener, Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant());
        }

        /// <summary>Waits for the program (at most <paramref name="timeout"/>; <paramref name="giveUp"/> is asked in between - e.g. the process has ended) and
        /// returns the target once its main thread stands at its entry. Throws <see cref="NativeDebugException"/>.</summary>
        public NativeDebugTarget Accept(TimeSpan timeout, Func<string?>? giveUp = null)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var accept = _listener.AcceptTcpClientAsync();
                while (!accept.Wait(200))
                {
                    // (stopping the listener drops the pending accept)
                    if (giveUp?.Invoke() is { } why) { _listener.Stop(); throw new NativeDebugException(why); }
                    if (DateTime.UtcNow > deadline) { _listener.Stop(); throw new NativeDebugException($"The program did not connect to the debugger within {timeout.TotalSeconds:0} s."); }
                }
                TcpClient client;
                try { client = accept.Result; }
                catch (AggregateException ex) { throw new NativeDebugException("Accepting the debug connection failed: " + ex.InnerException?.Message); }
                var target = NativeDebugTarget.TryOpen(client, Token, deadline);
                if (target != null)
                {
                    _listener.Stop();
                    return target;
                }
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch (SocketException) { }
        }
    }

    /// <summary>
    /// A native debug build under the debugger, over the protocol of docs/spec/NATIVE_DEBUG_PROTOCOL.md: a transport (<see cref="INativeDebugTransport"/>) - a
    /// TCP connection with one JSON message per line (the desktop), or a framed one that carries the program's console too (the console UART of an ESP32,
    /// stdio: <see cref="OpenFramed"/>). A reader thread takes the answers (`re`) to their requests and the events (`stopped`, `thread`, `threadExited`,
    /// `exited`) to the threads. Over TCP the output of the program is the output of its process (<see cref="AttachProcess"/>); over a framed transport it
    /// is the console between the frames.
    /// </summary>
    public sealed class NativeDebugTarget : IDebugTarget
    {
        /// <summary>How long a request waits for its answer.</summary>
        public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

        private readonly INativeDebugTransport _transport;
        private readonly object _lock = new();
        private readonly ConcurrentDictionary<long, TaskCompletionSource<Dictionary<string, object?>>> _pending = new();
        private readonly Dictionary<long, NativeDebugThread> _threads = new();
        private readonly List<NativeDebugThread> _order = new();
        private long _nextId;
        private HashSet<(int, int)>? _sentBreakpoints;
        private Process? _process;
        /// <summary>The program's process (see <see cref="AttachProcess"/>), null over a framed transport (a board).</summary>
        public Process? Process => _process;

        /// <summary>The profile the program sent at its end (after <see cref="StartProfile"/>): source, line, how often a thread entered it, microseconds spent in it.</summary>
        public IReadOnlyList<(int Source, int Line, long Hits, long Micros)>? Profile { get; private set; }

        /// <summary>The program counts its lines from now on (command `profile`, docs/spec/NATIVE_DEBUG_PROTOCOL.md); the counts come with its end (<see cref="Profile"/>).</summary>
        public bool StartProfile()
        {
            try { return Request("profile", ("on", true)).TryGetValue("ok", out var ok) && ok is true; }
            catch (NativeDebugException) { return false; }
        }
        private readonly List<string> _bufferedOutput = new();
        private Action<IDebugThread?, string>? _output;
        private volatile bool _closed;

        private NativeDebugTarget(INativeDebugTransport transport, Dictionary<string, object?> hello)
        {
            _transport = transport;
            Hello = hello;
            Transport = hello.Str("transport") ?? (transport.CarriesConsole ? "stdio" : "tcp");
            Pid = (int)(hello.Num("pid") ?? 0);
            TargetName = hello.Str("target");
            Mode = hello.Str("mode");
            FirstUserSourceIndex = (int)(hello.Num("firstUserSource") ?? 0);
            var sources = hello.Arr("sources").OfType<Dictionary<string, object?>>().ToList();
            int count = sources.Count == 0 ? 0 : (int)sources.Max(s => s.Num("index") ?? 0) + 1;
            var files = new string?[count];
            var hashes = new string?[count];
            foreach (var s in sources)
            {
                int index = (int)(s.Num("index") ?? -1);
                if (index < 0 || index >= count) continue;
                files[index] = s.Str("file");
                hashes[index] = s.Str("hash");
            }
            SourceFiles = files;
            SourceHashes = hashes;
            long mainId = hello.Num("mainThread") ?? 1;
            Main = new NativeDebugThread(this, mainId, "Main", isMain: true, running: false);
            _threads[mainId] = Main;
            _order.Add(Main);
        }

        /// <summary>Reads the hello of a new connection: null (connection closed) when the token is not ours or it is no hello. Then waits for the main
        /// thread's `stopped` at its entry.</summary>
        internal static NativeDebugTarget? TryOpen(TcpClient client, string token, DateTime deadline)
        {
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = (int)Math.Clamp((deadline - DateTime.UtcNow).TotalMilliseconds, 500, 30000);
                var reader = new StreamReader(stream, new UTF8Encoding(false));
                string? line = reader.ReadLine();
                var hello = line == null ? null : DebugJson.Parse(line) as Dictionary<string, object?>;
                if (hello == null || hello.Str("event") != "hello" || hello.Str("token") != token)
                {
                    client.Dispose();
                    return null;
                }
                if (hello.Num("protocol") is long p && p != 1)
                {
                    client.Dispose();
                    throw new NativeDebugException($"The program speaks debug protocol {p}; spark knows protocol 1.");
                }
                stream.ReadTimeout = Timeout.Infinite;
                var target = new NativeDebugTarget(new TcpDebugTransport(client, reader), hello);
                target.StartReader();
                if (!target.Main.WaitForFirstStop(deadline - DateTime.UtcNow))
                {
                    target.Dispose();
                    throw new NativeDebugException("The program connected but its main thread did not stop at its entry.");
                }
                return target;
            }
            catch (Exception ex) when (ex is IOException or FormatException or SocketException)
            {
                client.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Opens a debug session over a framed transport (the console UART of a board, stdio of a process): reads until the program's `hello` (the console
        /// before it - the boot log of a board - goes to <paramref name="console"/>), claims the program at once with a first command (it waits only
        /// FIRE_DEBUG_WAIT_MS, 3 s, for one) and waits for its main thread at the entry. <paramref name="token"/>: the hello must carry it (null: any).
        /// Throws <see cref="NativeDebugException"/>; the transport is disposed then.
        /// </summary>
        public static NativeDebugTarget OpenFramed(INativeDebugTransport transport, TimeSpan timeout, Action<string>? console = null, string? token = null)
        {
            var deadline = DateTime.UtcNow + timeout;
            var early = new List<string>();
            var gate = new object();
            NativeDebugTarget? target = null;
            transport.Console = line =>
            {
                NativeDebugTarget? t;
                lock (gate) { t = target; if (t == null) early.Add(line); }
                if (t != null) t.WriteOutput(line);
                else console?.Invoke(line);
            };
            var helloTask = System.Threading.Tasks.Task.Run(() =>
            {
                while (true)
                {
                    string? line = transport.ReadMessage();
                    if (line == null) return null;
                    try
                    {
                        if (DebugJson.Parse(line) is Dictionary<string, object?> m && m.Str("event") == "hello" && (token == null || m.Str("token") == token)) return m;
                    }
                    catch (FormatException) { }
                }
            });
            bool arrived;
            try { arrived = helloTask.Wait(timeout); }
            catch (AggregateException ex) { transport.Dispose(); throw new NativeDebugException("Reading the debug connection failed: " + ex.InnerException?.Message); }
            if (!arrived || helloTask.Result == null)
            {
                transport.Dispose();
                string said;
                lock (gate) said = string.Join(Environment.NewLine, early.TakeLast(20));
                throw new NativeDebugException((arrived ? "The connection ended before the program said hello." : $"The program did not say hello within {timeout.TotalSeconds:0} s - is it a debug build, and is it the program on the other end?")
                    + (said.Length > 0 ? Environment.NewLine + said : ""));
            }
            var hello = helloTask.Result;
            if (hello.Num("protocol") is long p && p != 1)
            {
                transport.Dispose();
                throw new NativeDebugException($"The program speaks debug protocol {p}; spark knows protocol 1.");
            }
            var opened = new NativeDebugTarget(transport, hello);
            lock (gate)
            {
                target = opened;
                if (console == null) foreach (var line in early) opened._bufferedOutput.Add(line);   // (with a console callback it has them already)
                early.Clear();
            }
            opened.StartReader();
            try
            {
                opened.Request("threads");   // (the first command: the program now waits for the debugger, not only 3 s)
                if (!opened.Main.WaitForFirstStop(deadline - DateTime.UtcNow)) throw new NativeDebugException("The program said hello but its main thread did not stop at its entry.");
            }
            catch (NativeDebugException)
            {
                opened.Dispose();
                throw;
            }
            return opened;
        }

        public Dictionary<string, object?> Hello { get; }
        /// <summary>The transport the program named in its hello: `tcp`, `stdio` or `uart`.</summary>
        public string Transport { get; }
        /// <summary>The program's console comes over the debug connection (a framed transport): <see cref="WriteInput"/> reaches its standard input.</summary>
        public bool CarriesConsole => _transport.CarriesConsole;

        /// <summary>A line for the program's standard input (a framed transport only). false: not possible.</summary>
        public bool WriteInput(string line) => !_closed && _transport.WriteInput(line);
        public int Pid { get; }
        public string? TargetName { get; }
        public string? Mode { get; }
        public string Engine => "native";
        public IReadOnlyList<string?> SourceFiles { get; private set; }
        /// <summary>SHA-256 (hex) of each source as the compiler got it (null: the prelude, a library).</summary>
        public IReadOnlyList<string?> SourceHashes { get; private set; }
        public int FirstUserSourceIndex { get; }
        public IDebugThread MainThread => Main;
        internal NativeDebugThread Main { get; }
        public bool HasOperandStack => false;
        public bool HasExited { get; private set; }
        public int? ExitCode { get; private set; }

        public IReadOnlyList<IDebugThread> Threads
        {
            get { lock (_lock) return _order.ToList(); }
        }

        public event Action<IDebugThread>? ThreadStarted;

        /// <summary>The output of the program's process (see <see cref="AttachProcess"/>): what came before the first subscriber is handed to it.</summary>
        public event Action<IDebugThread?, string>? Output
        {
            add
            {
                List<string> pending;
                lock (_lock)
                {
                    _output += value;
                    pending = _bufferedOutput.ToList();
                    _bufferedOutput.Clear();
                }
                foreach (var line in pending) value?.Invoke(null, line);
            }
            remove { lock (_lock) _output -= value; }
        }

        /// <summary>The program ended (`exited`, or the connection or the process went away).</summary>
        public event Action<int?>? Exited;

        /// <summary>The sources whose text differs from what the program was built from (by the hashes of the hello): the indexes whose hash is set and is
        /// not the hash of any of <paramref name="texts"/>.</summary>
        public IReadOnlyList<int> SourcesNotMatching(IEnumerable<string> texts)
        {
            var known = new HashSet<string>(texts.Select(HashOf));
            return Enumerable.Range(0, SourceHashes.Count).Where(i => SourceHashes[i] is { } h && !known.Contains(h)).ToList();
        }

        /// <summary>The hash of a source as the protocol has it: SHA-256 of its UTF-8 bytes, hex, lower case.</summary>
        public static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

        /// <summary>The program's process: its stdout and stderr go to <see cref="Output"/> line by line (<paramref name="readOutput"/>: false when the caller
        /// already reads them and calls <see cref="WriteOutput"/>); the end of the process ends all threads; <see cref="Dispose"/> kills it if it is still there.</summary>
        public void AttachProcess(Process process, bool readOutput = true)
        {
            _process = process;
            if (readOutput)
            {
                process.OutputDataReceived += (_, e) => { if (e.Data != null) WriteOutput(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) WriteOutput(e.Data); };
                try { process.BeginOutputReadLine(); } catch (InvalidOperationException) { }
                try { process.BeginErrorReadLine(); } catch (InvalidOperationException) { }
            }
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                // the connection usually ends first; a process that died without `exited` (a crash) still ends its threads
                Task.Delay(300).ContinueWith(_ => { int code = 0; try { code = process.ExitCode; } catch (InvalidOperationException) { } MarkExited(code); });
            };
            if (process.HasExited) MarkExited(process.ExitCode);
        }

        /// <summary>A line of the program's output (to <see cref="Output"/>, kept until there is a subscriber).</summary>
        public void WriteOutput(string line)
        {
            Action<IDebugThread?, string>? output;
            lock (_lock)
            {
                output = _output;
                if (output == null) { _bufferedOutput.Add(line); return; }
            }
            output(null, line);
        }

        // ---------------------------------------------------------------
        // Requests and events
        // ---------------------------------------------------------------

        /// <summary>Sends a command and waits for its answer. Throws <see cref="NativeDebugException"/> when the connection is gone or no answer comes;
        /// an answer with `ok: false` is returned as it is.</summary>
        internal Dictionary<string, object?> Request(string command, params (string Key, object? Value)[] fields)
        {
            if (_closed) throw new NativeDebugException("The program is no longer connected.");
            long id = Interlocked.Increment(ref _nextId);
            var message = new Dictionary<string, object?> { ["id"] = id, ["cmd"] = command };
            foreach (var (k, v) in fields) message[k] = v;
            var tcs = new TaskCompletionSource<Dictionary<string, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                _transport.WriteMessage(DebugJson.Write(message));
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
            {
                _pending.TryRemove(id, out _);
                throw new NativeDebugException("The program is no longer connected: " + ex.Message);
            }
            if (!tcs.Task.Wait(RequestTimeout))
            {
                _pending.TryRemove(id, out _);
                throw new NativeDebugException($"The program did not answer '{command}'.");
            }
            return tcs.Task.Result;
        }

        /// <summary>Like <see cref="Request"/>; `ok: false` is an exception with the error of the answer.</summary>
        internal Dictionary<string, object?> RequestOk(string command, params (string Key, object? Value)[] fields)
        {
            var answer = Request(command, fields);
            if (!answer.Flag("ok")) throw new NativeDebugException(answer.Str("error") ?? $"'{command}' failed");
            return answer;
        }

        /// <summary>Sends the breakpoints when they differ from the ones the program has (they apply to all threads).</summary>
        internal void EnsureBreakpoints(ISet<(int SourceIndex, int Line)> breakpoints)
        {
            HashSet<(int, int)> all;
            lock (_lock)
            {
                _given = new HashSet<(int, int)>(breakpoints);
                all = new HashSet<(int, int)>(_given);
                all.UnionWith(_pluginBreakpoints);
                if (_sentBreakpoints != null && _sentBreakpoints.SetEquals(all)) return;
                _sentBreakpoints = all;
            }
            SendBreakpoints(all);
        }

        private void SendBreakpoints(IEnumerable<(int SourceIndex, int Line)> breakpoints) =>
            Request("breakpoints", ("breakpoints", breakpoints.OrderBy(b => b.SourceIndex).ThenBy(b => b.Line).Select(b => new List<object?> { (long)b.SourceIndex, (long)b.Line }).ToList()));

        // ---- fire plugins: their sources come with the event `sources` (after the program's); the breakpoints by file in them are sent at once ----
        private HashSet<(int, int)> _given = new();
        private List<(string File, int Line)> _fileBreakpoints = new();
        private HashSet<(int, int)> _pluginBreakpoints = new();
        private int _programSources = -1;

        public event Action? SourcesAdded;

        private static bool SameFile(string? a, string? b)
        {
            if (a == null || b == null) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        }

        private HashSet<(int, int)> ResolvePluginBreakpoints()
        {
            var result = new HashSet<(int, int)>();
            if (_programSources < 0) _programSources = SourceFiles.Count;
            for (int i = _programSources; i < SourceFiles.Count; i++)
                foreach (var (file, line) in _fileBreakpoints)
                    if (SameFile(SourceFiles[i], file)) result.Add((i, line));
            return result;
        }

        public void SetFileBreakpoints(IEnumerable<(string File, int Line)> breakpoints)
        {
            lock (_lock)
            {
                _fileBreakpoints = breakpoints.ToList();
                _pluginBreakpoints = ResolvePluginBreakpoints();
            }
        }

        /// <summary>`sources`: a plugin's sources (docs/spec/NATIVE_DEBUG_PROTOCOL.md) - the lists grow, the breakpoints go to the program again (it waits for them), with those
        /// by file in the new sources. On the reader thread: the request is sent from another one.</summary>
        private void OnSources(Dictionary<string, object?> message)
        {
            HashSet<(int, int)> all;
            lock (_lock)
            {
                if (_programSources < 0) _programSources = SourceFiles.Count;
                var files = SourceFiles.ToList();
                var hashes = SourceHashes.ToList();
                foreach (var s in message.Arr("sources").OfType<Dictionary<string, object?>>())
                {
                    int index = (int)(s.Num("index") ?? -1);
                    if (index < 0) continue;
                    while (files.Count <= index) { files.Add(null); hashes.Add(null); }
                    files[index] = s.Str("file");
                    hashes[index] = s.Str("hash");
                }
                SourceFiles = files;
                SourceHashes = hashes;
                _pluginBreakpoints = ResolvePluginBreakpoints();
                all = new HashSet<(int, int)>(_given);
                all.UnionWith(_pluginBreakpoints);
                _sentBreakpoints = all;
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { SendBreakpoints(all); }
                catch (Exception ex) when (ex is NativeDebugException or IOException or ObjectDisposedException or InvalidOperationException) { }
            });
            SourcesAdded?.Invoke();
        }

        private void StartReader()
        {
            var thread = new Thread(ReadLoop) { IsBackground = true, Name = "fire-native-debug-reader" };
            thread.Start();
        }

        private void ReadLoop()
        {
            try
            {
                if (_transport.CarriesConsole) _transport.Console = WriteOutput;
                while (true)
                {
                    string? line = _transport.ReadMessage();
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    Dictionary<string, object?> message;
                    try { message = DebugJson.ParseObject(line); }
                    catch (FormatException) { continue; }
                    if (message.Num("re") is long re)
                    {
                        if (_pending.TryRemove(re, out var tcs)) tcs.TrySetResult(message);
                        continue;
                    }
                    HandleEvent(message);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
            _closed = true;
            foreach (var (_, tcs) in _pending) tcs.TrySetException(new NativeDebugException("The program is no longer connected."));
            _pending.Clear();
            MarkExited(ExitCode);
        }

        private void HandleEvent(Dictionary<string, object?> message)
        {
            switch (message.Str("event"))
            {
                case "stopped":
                    ThreadById(message.Num("thread"))?.OnStopped(message);
                    break;
                case "thread":
                {
                    long id = message.Num("thread") ?? 0;
                    NativeDebugThread thread;
                    lock (_lock)
                    {
                        if (_threads.ContainsKey(id)) break;
                        thread = new NativeDebugThread(this, id, message.Str("name") ?? $"Fire #{_order.Count}", isMain: false, running: true);
                        _threads[id] = thread;
                        _order.Add(thread);
                    }
                    ThreadStarted?.Invoke(thread);
                    break;
                }
                case "threadExited":
                    ThreadById(message.Num("thread"))?.OnEnded();
                    break;
                case "sources":
                    OnSources(message);
                    break;
                case "profile":
                    // (the profiler's counts, sent before `exited`: [source, line, hits, microseconds] for every line with code)
                    if (message.TryGetValue("lines", out var raw) && raw is List<object?> rows)
                        Profile = rows.OfType<List<object?>>().Where(r => r.Count >= 4)
                            .Select(r => ((int)Convert.ToInt64(r[0]), (int)Convert.ToInt64(r[1]), Convert.ToInt64(r[2]), Convert.ToInt64(r[3]))).ToList();
                    break;
                case "exited":
                    MarkExited((int?)message.Num("code"));
                    break;
                case "hello":
                    // (a board that started again - a reset, a crash - and says hello anew: the program that was debugged is gone)
                    WriteOutput("(the program started again)");
                    MarkExited(null);
                    break;
            }
        }

        private NativeDebugThread? ThreadById(long? id)
        {
            if (id == null) return null;
            lock (_lock) return _threads.TryGetValue(id.Value, out var t) ? t : null;
        }

        private void MarkExited(int? code)
        {
            List<NativeDebugThread> all;
            lock (_lock)
            {
                if (HasExited) return;
                HasExited = true;
                ExitCode = code;
                all = _order.ToList();
            }
            foreach (var t in all) t.OnEnded();
            Exited?.Invoke(code);
        }

        public void ReleaseResources() { }

        /// <summary>Ends the program (`terminate`, then the process is killed if it is still there) and closes the connection.</summary>
        public void Dispose()
        {
            if (!_closed && !HasExited)
            {
                try
                {
                    long id = Interlocked.Increment(ref _nextId);
                    _transport.WriteMessage(DebugJson.Write(new Dictionary<string, object?> { ["id"] = id, ["cmd"] = "terminate", ["code"] = 1L }));
                    if (_transport.CarriesConsole) Thread.Sleep(100);   // (a serial line: the frame is out before the port closes)
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException) { }
            }
            _closed = true;
            try { _transport.Dispose(); } catch (Exception ex) when (ex is IOException or SocketException) { }
            if (_process is { } p)
            {
                try
                {
                    if (!p.WaitForExit(1000)) p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            MarkExited(ExitCode);
        }

        // ---------------------------------------------------------------
        // Values
        // ---------------------------------------------------------------

        internal static DebugValueKind KindOf(string? kind) => kind switch
        {
            "int" => DebugValueKind.Int,
            "float" => DebugValueKind.Float,
            "bool" => DebugValueKind.Bool,
            "string" => DebugValueKind.String,
            "char" => DebugValueKind.Char,
            "undefined" => DebugValueKind.Undefined,
            "object" => DebugValueKind.Object,
            "array" => DebugValueKind.Array,
            "buffer" => DebugValueKind.Buffer,
            "lambda" => DebugValueKind.Lambda,
            "pointer" => DebugValueKind.Pointer,
            _ => DebugValueKind.Other,
        };

        /// <summary>A value V of the protocol. Its `ref` is the reference for expand and set; an object or array expands with `expand`.</summary>
        internal DebugValue ToValue(Dictionary<string, object?> v)
        {
            string name = v.Str("name") ?? "?";
            string kindName = v.Str("kind") ?? "undefined";
            var kind = KindOf(kindName);
            string display = v.Str("display") ?? "";
            if (v.Flag("truncated")) display += " (truncated)";
            long id = v.Num("id") ?? 0;   // (an identity of the object, when the program sends one - the protocol does not require it)
            if (kind == DebugValueKind.Object && id != 0) display = $"{display} (#{id})";
            string? reference = v.Str("ref");
            Func<IReadOnlyList<DebugValue>>? children = v.Flag("expandable") && reference != null ? () => Expand(reference) : null;
            return new DebugValue(name, display, kind, kindName, id, children, reference != null ? new NativeRef(reference) : null);
        }

        internal IReadOnlyList<DebugValue> ToValues(IEnumerable<object?> list) => list.OfType<Dictionary<string, object?>>().Select(ToValue).ToList();

        private IReadOnlyList<DebugValue> Expand(string reference) => ToValues(RequestOk("expand", ("ref", reference)).Arr("children"));

        internal sealed record NativeRef(string Path);

        /// <summary>The text of `set` as the literal the protocol wants: a string in quotes (escaped), everything else as written.</summary>
        internal static string Literal(DebugValue target, string text)
        {
            if (target.Kind != DebugValueKind.String) return text.Trim();
            string s = text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
            if (s != text) return text;   // already quoted: as the user wrote it (with their escapes)
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";
        }
    }

    /// <summary>
    /// One thread of a native program. Control: the command goes out (`continue`/`step`), then the caller waits until the thread stops (`stopped`) or
    /// ends; while it waits, a pause request becomes `pause`. Values are asked for (`scopes`, `globals`, `this`, `trace`, `expand`).
    /// </summary>
    public sealed class NativeDebugThread : IDebugThread
    {
        private readonly NativeDebugTarget _target;
        private readonly object _lock = new();
        private int _generation;
        private bool _running;
        private bool _ended;
        private (int, int) _location;
        private int _depth;
        private string? _reason;
        private string? _unhandledError;
        private IReadOnlyList<(int SourceIndex, int Line)>? _unhandledTrace;
        private bool _unhandled;
        private bool _autoRunPending;

        internal NativeDebugThread(NativeDebugTarget target, long id, string name, bool isMain, bool running)
        {
            _target = target;
            Id = id;
            Name = name;
            IsMain = isMain;
            _running = running;
            _autoRunPending = running;
        }

        public long Id { get; }
        public IDebugTarget Target => _target;
        public string Name { get; }
        public bool IsMain { get; }
        /// <summary>Why the thread stopped last (`entry`, `breakpoint`, `step`, `pause`, `unhandled`).</summary>
        public string? StopReason { get { lock (_lock) return _reason; } }
        public bool IsEnded { get { lock (_lock) return _ended; } }

        internal bool WaitForFirstStop(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + (timeout < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : timeout);
            lock (_lock)
            {
                while (_generation == 0 && !_ended)
                {
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero) return false;
                    Monitor.Wait(_lock, left);
                }
                return !_ended;
            }
        }

        internal void OnStopped(Dictionary<string, object?> e)
        {
            lock (_lock)
            {
                _running = false;
                _location = ((int)(e.Num("source") ?? 0), (int)(e.Num("line") ?? 0));
                _depth = (int)(e.Num("depth") ?? 0);
                _reason = e.Str("reason");
                _unhandled = _reason == "unhandled";
                _unhandledTrace = null;
                if (_unhandled)
                {
                    var ex = e.Obj("exception");
                    string cls = ex?.Str("class") ?? "Exception";
                    string msg = ex?.Str("message") ?? "(no 'message' field)";
                    _unhandledError = $"Unhandled exception of type '{cls}': {msg}";   // (the text of the VM, see UncaughtScriptException)
                }
                else _unhandledError = null;
                _generation++;
                Monitor.PulseAll(_lock);
            }
        }

        internal void OnEnded()
        {
            lock (_lock)
            {
                _ended = true;
                _running = false;
                Monitor.PulseAll(_lock);
            }
        }

        /// <summary>Sends the command and waits for the next stop or the end. A thread the program started (`thread` event) runs already, in continue mode:
        /// its first run sends nothing and also counts a stop that came before the caller got here.</summary>
        private bool RunAndWait(Func<Dictionary<string, object?>>? send, Func<bool>? isPauseRequested)
        {
            int generation;
            lock (_lock)
            {
                if (_ended) return false;
                generation = _generation;
                if (_autoRunPending)
                {
                    _autoRunPending = false;
                    generation = 0;
                    send = null;
                }
                if (send != null) _running = true;
            }
            if (send != null)
            {
                Dictionary<string, object?> answer;
                try { answer = send(); }
                catch (NativeDebugException) { return false; }   // the connection is gone: the program has ended for the debugger
                if (!answer.Flag("ok"))
                {
                    lock (_lock)
                    {
                        if (_generation == generation && !_ended) _running = false;   // refused (e.g. step out at the top level): the thread stays where it is
                        return !_ended && !_unhandled;
                    }
                }
            }
            bool pauseSent = false;
            while (true)
            {
                lock (_lock)
                {
                    if (_ended) return false;
                    if (_generation != generation) return !_unhandled;
                    Monitor.Wait(_lock, 50);
                    if (_ended) return false;
                    if (_generation != generation) return !_unhandled;
                }
                if (!pauseSent && isPauseRequested?.Invoke() == true)
                {
                    pauseSent = true;
                    try { _target.Request("pause", ("thread", Id)); }
                    catch (Exception ex) when (ex is NativeDebugException or IOException or ObjectDisposedException or InvalidOperationException) { }
                }
            }
        }

        public bool StepLine() => RunAndWait(() => _target.Request("step", ("thread", Id), ("kind", "line")), null);
        public bool StepInto() => RunAndWait(() => _target.Request("step", ("thread", Id), ("kind", "into")), null);
        /// <summary>Step Out; at the top level the program refuses it and the thread stays where it is (true).</summary>
        public bool StepOut() => RunAndWait(() => _target.Request("step", ("thread", Id), ("kind", "out")), null);

        public bool RunUntilBreakpoint(ISet<(int SourceIndex, int Line)> breakpoints, Func<bool> isPauseRequested)
        {
            try { _target.EnsureBreakpoints(breakpoints); }
            catch (NativeDebugException) { }
            return RunAndWait(() => _target.Request("continue", ("thread", Id), ("breakpoints", true)), isPauseRequested);
        }

        public bool RunUntilEnd(Func<bool> isPauseRequested)
        {
            return RunAndWait(() => _target.Request("continue", ("thread", Id), ("breakpoints", false)), isPauseRequested);
        }

        public (int SourceIndex, int Line) CurrentLocation { get { lock (_lock) return _location; } }
        public int CallDepth { get { lock (_lock) return _depth; } }

        public IReadOnlyList<(int SourceIndex, int Line)> CurrentTrace()
        {
            try
            {
                return _target.RequestOk("trace", ("thread", Id)).Arr("frames").OfType<Dictionary<string, object?>>()
                    .Select(f => ((int)(f.Num("source") ?? 0), (int)(f.Num("line") ?? 0))).ToList();
            }
            catch (NativeDebugException) { return new[] { CurrentLocation }; }
        }

        public DebugValue? This
        {
            get
            {
                try { return _target.RequestOk("this", ("thread", Id)).Obj("value") is { } v ? _target.ToValue(v) : null; }
                catch (NativeDebugException) { return null; }
            }
        }

        public IReadOnlyList<DebugScope> Scopes()
        {
            try
            {
                return _target.RequestOk("scopes", ("thread", Id)).Arr("scopes").OfType<Dictionary<string, object?>>()
                    .Select(s => new DebugScope((int)(s.Num("depth") ?? 0), s.Flag("functionTop"), _target.ToValues(s.Arr("variables")))).ToList();
            }
            catch (NativeDebugException) { return Array.Empty<DebugScope>(); }
        }

        /// <summary>The globals as the thread sees them: a fire thread first its own variables of its top level (`globals` with its `thread`: refs
        /// `T:THREAD:INDEX`), then the program's; the main thread the program's.</summary>
        public IReadOnlyList<DebugValue> Globals()
        {
            try
            {
                if (!IsMain && !IsEnded)
                {
                    var answer = _target.Request("globals", ("thread", Id));
                    if (answer.Flag("ok")) return _target.ToValues(answer.Arr("variables"));
                    // (the thread is not paused: the program's globals)
                }
                return _target.ToValues(_target.RequestOk("globals").Arr("variables"));
            }
            catch (NativeDebugException) { return Array.Empty<DebugValue>(); }
        }

        /// <summary>A native program has no operand stack.</summary>
        public IReadOnlyList<DebugValue>? OperandStack() => null;

        public string? UnhandledError { get { lock (_lock) return _unhandled ? _unhandledError : null; } }

        public IReadOnlyList<(int SourceIndex, int Line)>? UnhandledTrace
        {
            get
            {
                lock (_lock)
                {
                    if (!_unhandled) return null;
                    if (_unhandledTrace != null) return _unhandledTrace;
                }
                var trace = CurrentTrace();
                lock (_lock) { if (_unhandled) _unhandledTrace = trace; return _unhandledTrace; }
            }
        }

        public bool IsPausedOnUnhandled { get { lock (_lock) return _unhandled && !_ended; } }

        public bool TryResumeUnhandled()
        {
            lock (_lock) if (!_unhandled || _ended) return false;
            try { _target.RequestOk("resume", ("thread", Id)); }
            catch (NativeDebugException) { return false; }
            lock (_lock)
            {
                _unhandled = false;
                _unhandledError = null;
                _unhandledTrace = null;
                _reason = "resume";
            }
            return true;
        }

        public bool TrySetValue(DebugValue target, string text, out string error)
        {
            error = "";
            if (target.Reference is not NativeDebugTarget.NativeRef r) { error = $"{target.Name} cannot be set."; return false; }
            if (target.Kind is DebugValueKind.Object or DebugValueKind.Array or DebugValueKind.Lambda or DebugValueKind.Buffer or DebugValueKind.Pointer)
            {
                error = $"A value of the kind {target.Kind} cannot be set - only numbers, bools, characters and strings.";
                return false;
            }
            try
            {
                var answer = _target.Request("set", ("thread", Id), ("ref", r.Path), ("value", NativeDebugTarget.Literal(target, text)));
                if (answer.Flag("ok")) return true;
                error = answer.Str("error") ?? $"{target.Name} was not set.";
                return false;
            }
            catch (NativeDebugException ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
