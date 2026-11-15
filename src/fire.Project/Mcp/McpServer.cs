using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace fire.Projects.Mcp
{
    /// <summary>The answer of a tool: its text (and whether it is an error the model should see as one).</summary>
    public sealed record McpToolResult(string Text, bool IsError = false)
    {
        public static McpToolResult Ok(string text) => new(text);
        public static McpToolResult Error(string text) => new(text, true);
    }

    /// <summary>A JSON-RPC error of a request (code -32602 invalid parameters, -32002 no such resource, ...).</summary>
    public sealed class McpException : Exception
    {
        public int Code { get; }
        public McpException(int code, string message) : base(message) { Code = code; }
    }

    /// <summary>
    /// A server of the Model Context Protocol (docs/networking/mcp.md, docs/tools/spark-mcp.md): tools, resources and notifications over Streamable HTTP
    /// on 127.0.0.1. The same protocol as the fire package `mcp`; the versions with the initialize handshake (2025-11-25 ... 2024-11-05).
    /// <list type="bullet">
    /// <item>POST: one JSON-RPC message; a request is answered as JSON (or as server-sent events when notifications go with it), a notification with 202.</item>
    /// <item>GET (Accept: text/event-stream): the stream of the server's own messages for the session (notifications of changes).</item>
    /// <item>DELETE: ends the session.</item>
    /// <item>Every request needs the token (Authorization: Bearer ...); a request with an Origin that is not localhost is refused (DNS rebinding).</item>
    /// </list>
    /// Who wants to connect finds the URL and the token in <see cref="ConnectionFile"/> (written by <see cref="Start"/>, removed by <see cref="Stop"/>).
    /// </summary>
    public sealed class McpServer : IDisposable
    {
        public static readonly string[] Versions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

        private sealed record Tool(string Name, string Description, string Schema, Func<JsonElement, Task<McpToolResult>> Handler);
        private sealed record Resource(string Uri, string Name, string Description, string MimeType, Func<Task<string>> Read);

        /// <summary>A session of a client: its subscriptions, the streams it listens on, what waits to be sent with its next answer.</summary>
        private sealed class Session
        {
            public readonly HashSet<string> Subscribed = new();
            public readonly List<Stream> Streams = new();
            public readonly ConcurrentQueue<string> Pending = new();
            public string ClientName = "";
        }

        private readonly List<Tool> _tools = new();
        private readonly List<Resource> _resources = new();
        private readonly ConcurrentDictionary<string, Session> _sessions = new();
        private readonly object _lock = new();
        private HttpListener? _listener;
        private CancellationTokenSource? _stop;
        private string? _connectionFile;

        public string Name { get; }
        public string Version { get; }
        /// <summary>Shown to the client at the start: what the server is for.</summary>
        public string Instructions { get; set; } = "";
        /// <summary>The URL clients post to (http://127.0.0.1:port/mcp), when it runs.</summary>
        public string? Url { get; private set; }
        /// <summary>The token every request must carry (Authorization: Bearer ...); empty: none is needed (tests).</summary>
        public string Token { get; set; } = NewToken();
        public bool IsRunning => _listener != null;
        /// <summary>The clients that are connected (their names, from initialize).</summary>
        public IReadOnlyList<string> Clients => _sessions.Values.Select(s => s.ClientName).ToList();
        /// <summary>Something happened (for a log in the host): a client came or went, a tool was called.</summary>
        public event Action<string>? Activity;

        public McpServer(string name, string version) { Name = name; Version = version; }

        public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

        /// <summary>Where the URL and the token of a running server are written: %AppData%/fire/&lt;name&gt;-mcp.json (only the user can read it).</summary>
        public static string ConnectionFileOf(string name) =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fire", name + "-mcp.json");
        public string ConnectionFile => ConnectionFileOf(Name);

        // ---- what the server offers ----

        /// <summary>A tool: `schema` the JSON schema of its arguments ("" for none).</summary>
        public void AddTool(string name, string description, string schema, Func<JsonElement, Task<McpToolResult>> handler)
        {
            lock (_lock)
            {
                _tools.RemoveAll(t => t.Name == name);
                _tools.Add(new Tool(name, description, schema == "" ? "{\"type\":\"object\",\"properties\":{}}" : schema, handler));
            }
            Broadcast(Notification("notifications/tools/list_changed", null));
        }

        public void AddResource(string uri, string name, string description, string mimeType, Func<Task<string>> read)
        {
            lock (_lock)
            {
                _resources.RemoveAll(r => r.Uri == uri);
                _resources.Add(new Resource(uri, name, description, mimeType, read));
            }
            Broadcast(Notification("notifications/resources/list_changed", null));
        }

        /// <summary>A resource changed: the sessions that subscribed to it are told (notifications/resources/updated).</summary>
        public void ResourceUpdated(string uri)
        {
            var p = new JsonObject { ["uri"] = uri };
            string n = Notification("notifications/resources/updated", p);
            foreach (var s in _sessions.Values) if (s.Subscribed.Contains(uri)) Send(s, n);
        }

        /// <summary>A message for the logs of the clients (notifications/message): level "info", "warning", "error"; `data` text or JSON.</summary>
        public void Log(string level, JsonNode data, string logger = "")
        {
            var p = new JsonObject { ["level"] = level, ["data"] = data };
            if (logger != "") p["logger"] = logger;
            Broadcast(Notification("notifications/message", p));
        }

        private static string Notification(string method, JsonNode? p)
        {
            var m = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (p != null) m["params"] = p;
            return m.ToJsonString();
        }

        private void Broadcast(string message) { foreach (var s in _sessions.Values) Send(s, message); }

        /// <summary>To the streams of the session, or (when it has none) with its next answer.</summary>
        private void Send(Session s, string message)
        {
            List<Stream> streams;
            lock (s.Streams) streams = s.Streams.ToList();
            bool sent = false;
            foreach (var stream in streams)
            {
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes("event: message\ndata: " + message + "\n\n");
                    lock (stream) { stream.Write(bytes); stream.Flush(); }
                    sent = true;
                }
                catch (Exception) { lock (s.Streams) s.Streams.Remove(stream); }
            }
            if (!sent) s.Pending.Enqueue(message);
        }

        // ---- the protocol ----

        /// <summary>The answer to one message (JSON text), null for a notification or an answer of the client. `sessionId`: the session it belongs to (null: one
        /// is made at initialize - <paramref name="newSession"/>).</summary>
        public async Task<(string? Answer, string? NewSession)> HandleAsync(string text, string? sessionId)
        {
            JsonNode? node;
            try { node = JsonNode.Parse(text); }
            catch (JsonException e) { return (Error(null, -32700, "The message is no JSON: " + e.Message), null); }
            if (node is not JsonObject m) return (Error(null, -32600, "A message is a JSON object."), null);
            if (m["method"]?.GetValue<string>() is not { } method) return (null, null);
            JsonNode? id = m["id"]?.DeepClone();
            if (!m.ContainsKey("id")) return (null, null);   // a notification (initialized, cancelled)
            Session? session = sessionId != null && _sessions.TryGetValue(sessionId, out var found) ? found : null;
            string? newSession = null;
            try
            {
                JsonNode result;
                if (method == "initialize")
                {
                    string asked = m["params"]?["protocolVersion"]?.GetValue<string>() ?? Versions[0];
                    string version = Versions.Contains(asked) ? asked : Versions[0];
                    newSession = Guid.NewGuid().ToString("N");
                    var s = new Session { ClientName = m["params"]?["clientInfo"]?["name"]?.GetValue<string>() ?? "" };
                    _sessions[newSession] = s;
                    Activity?.Invoke("MCP: " + (s.ClientName == "" ? "a client" : s.ClientName) + " connected.");
                    var r = new JsonObject
                    {
                        ["protocolVersion"] = version,
                        ["capabilities"] = new JsonObject
                        {
                            ["tools"] = new JsonObject { ["listChanged"] = true },
                            ["resources"] = new JsonObject { ["subscribe"] = true, ["listChanged"] = true },
                            ["logging"] = new JsonObject(),
                        },
                        ["serverInfo"] = new JsonObject { ["name"] = Name, ["version"] = Version },
                    };
                    if (Instructions != "") r["instructions"] = Instructions;
                    result = r;
                }
                else result = await AnswerAsync(method, m["params"] as JsonObject, session);
                return (new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString(), newSession);
            }
            catch (McpException e) { return (Error(id, e.Code, e.Message), newSession); }
            catch (Exception e) { return (Error(id, -32603, e.Message), newSession); }
        }

        private static string Error(JsonNode? id, int code, string message) =>
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();

        private static string Param(JsonObject? p, string name) =>
            p?[name]?.GetValue<string>() ?? throw new McpException(-32602, $"The parameter '{name}' is missing.");

        private async Task<JsonNode> AnswerAsync(string method, JsonObject? p, Session? session)
        {
            switch (method)
            {
                case "ping":
                case "logging/setLevel":
                    return new JsonObject();
                case "tools/list":
                {
                    var list = new JsonArray();
                    lock (_lock)
                        foreach (var t in _tools)
                            list.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = JsonNode.Parse(t.Schema) });
                    return new JsonObject { ["tools"] = list };
                }
                case "tools/call":
                {
                    string name = Param(p, "name");
                    Tool? tool;
                    lock (_lock) tool = _tools.FirstOrDefault(t => t.Name == name);
                    if (tool == null) throw new McpException(-32602, $"There is no tool '{name}'.");
                    JsonElement args = p?["arguments"] is JsonObject a ? JsonDocument.Parse(a.ToJsonString()).RootElement : JsonDocument.Parse("{}").RootElement;
                    McpToolResult r;
                    try { r = await tool.Handler(args); }
                    catch (Exception e) { r = McpToolResult.Error(e.Message); }
                    Activity?.Invoke($"MCP: {name}" + (r.IsError ? " (error)" : ""));
                    var answer = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = r.Text }) };
                    if (r.IsError) answer["isError"] = true;
                    return answer;
                }
                case "resources/list":
                {
                    var list = new JsonArray();
                    lock (_lock)
                        foreach (var r in _resources)
                            list.Add(new JsonObject { ["uri"] = r.Uri, ["name"] = r.Name, ["description"] = r.Description, ["mimeType"] = r.MimeType });
                    return new JsonObject { ["resources"] = list };
                }
                case "resources/templates/list":
                    return new JsonObject { ["resourceTemplates"] = new JsonArray() };
                case "resources/read":
                {
                    string uri = Param(p, "uri");
                    Resource? res;
                    lock (_lock) res = _resources.FirstOrDefault(r => r.Uri == uri);
                    if (res == null) throw new McpException(-32002, $"There is no resource '{uri}'.");
                    string text = await res.Read();
                    return new JsonObject { ["contents"] = new JsonArray(new JsonObject { ["uri"] = uri, ["mimeType"] = res.MimeType, ["text"] = text }) };
                }
                case "resources/subscribe":
                    if (session != null) lock (session.Subscribed) session.Subscribed.Add(Param(p, "uri"));
                    return new JsonObject();
                case "resources/unsubscribe":
                    if (session != null) lock (session.Subscribed) session.Subscribed.Remove(Param(p, "uri"));
                    return new JsonObject();
                default:
                    throw new McpException(-32601, $"There is no method '{method}'.");
            }
        }

        // ---- HTTP ----

        /// <summary>Starts serving on 127.0.0.1 (port 0: a free one) and writes the connection file.</summary>
        public void Start(int port = 0)
        {
            if (_listener != null) return;
            if (port == 0)
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _listener = listener;
            _stop = new CancellationTokenSource();
            Url = $"http://127.0.0.1:{port}/mcp";
            WriteConnectionFile();
            _ = Task.Run(() => AcceptLoop(listener, _stop.Token));
        }

        public void Stop()
        {
            _stop?.Cancel();
            foreach (var s in _sessions.Values) lock (s.Streams) { foreach (var st in s.Streams) { try { st.Close(); } catch (Exception) { } } s.Streams.Clear(); }
            _sessions.Clear();
            try { _listener?.Stop(); _listener?.Close(); } catch (Exception) { }
            _listener = null;
            Url = null;
            if (_connectionFile != null) { try { File.Delete(_connectionFile); } catch (Exception) { } _connectionFile = null; }
        }

        public void Dispose() => Stop();

        private void WriteConnectionFile()
        {
            try
            {
                string path = ConnectionFile;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var o = new JsonObject { ["url"] = Url, ["token"] = Token, ["pid"] = Environment.ProcessId, ["server"] = Name };
                File.WriteAllText(path, o.ToJsonString());
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                _connectionFile = path;
            }
            catch (Exception) { }
        }

        private async Task AcceptLoop(HttpListener listener, CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch (Exception) { break; }
                _ = Task.Run(() => Serve(ctx));
            }
        }

        private async Task Serve(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            try
            {
                if (req.Url?.AbsolutePath.TrimEnd('/') != "/mcp") { await Reply(res, 404, "text/plain", "Not found."); return; }
                string? origin = req.Headers["Origin"];
                if (origin != null && !(origin.StartsWith("http://127.0.0.1") || origin.StartsWith("http://localhost") || origin == "null"))
                {
                    await Reply(res, 403, "text/plain", "Origin not allowed.");
                    return;
                }
                if (Token != "" && req.Headers["Authorization"] != "Bearer " + Token) { await Reply(res, 401, "text/plain", "The token is missing or wrong."); return; }
                string? sessionId = req.Headers["Mcp-Session-Id"];
                if (req.HttpMethod == "POST")
                {
                    string body;
                    using (var reader = new StreamReader(req.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
                    if (sessionId != null && !_sessions.ContainsKey(sessionId) && !body.Contains("\"initialize\"")) { await Reply(res, 404, "text/plain", "No such session."); return; }
                    var (answer, newSession) = await HandleAsync(body, sessionId);
                    string? sid = newSession ?? sessionId;
                    if (sid != null) res.Headers["Mcp-Session-Id"] = sid;
                    if (answer == null) { res.StatusCode = 202; res.Close(); return; }
                    // notifications that waited for this session go with the answer, as server-sent events
                    var pending = new List<string>();
                    if (sid != null && _sessions.TryGetValue(sid, out var s)) while (s.Pending.TryDequeue(out var n)) pending.Add(n);
                    if (pending.Count == 0) await Reply(res, 200, "application/json", answer);
                    else
                    {
                        var sb = new StringBuilder();
                        foreach (var n in pending) sb.Append("event: message\ndata: ").Append(n).Append("\n\n");
                        sb.Append("event: message\ndata: ").Append(answer).Append("\n\n");
                        await Reply(res, 200, "text/event-stream", sb.ToString());
                    }
                    return;
                }
                if (req.HttpMethod == "GET")
                {
                    if (sessionId == null || !_sessions.TryGetValue(sessionId, out var s)) { await Reply(res, 400, "text/plain", "A stream needs a session (Mcp-Session-Id)."); return; }
                    res.StatusCode = 200;
                    res.ContentType = "text/event-stream";
                    res.Headers["Cache-Control"] = "no-cache";
                    res.SendChunked = true;
                    var stream = res.OutputStream;
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(": stream of " + Name + "\n\n"));
                    await stream.FlushAsync();
                    lock (s.Streams) s.Streams.Add(stream);
                    while (s.Pending.TryDequeue(out var n)) Send(s, n);
                    // keep it open: a comment now and then, until the client or the server goes
                    while (_listener != null && _sessions.ContainsKey(sessionId))
                    {
                        await Task.Delay(15000);
                        try { lock (stream) { stream.Write(Encoding.UTF8.GetBytes(": ping\n\n")); stream.Flush(); } }
                        catch (Exception) { break; }
                    }
                    lock (s.Streams) s.Streams.Remove(stream);
                    try { res.Close(); } catch (Exception) { }
                    return;
                }
                if (req.HttpMethod == "DELETE")
                {
                    if (sessionId != null && _sessions.TryRemove(sessionId, out var s))
                    {
                        lock (s.Streams) { foreach (var st in s.Streams) { try { st.Close(); } catch (Exception) { } } s.Streams.Clear(); }
                        Activity?.Invoke("MCP: " + (s.ClientName == "" ? "a client" : s.ClientName) + " left.");
                    }
                    await Reply(res, 200, "text/plain", "");
                    return;
                }
                await Reply(res, 405, "text/plain", "POST, GET or DELETE.");
            }
            catch (Exception)
            {
                try { res.Abort(); } catch (Exception) { }
            }
        }

        private static async Task Reply(HttpListenerResponse res, int status, string type, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            res.StatusCode = status;
            res.ContentType = type + (type.StartsWith("text/") || type == "application/json" ? "; charset=utf-8" : "");
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }
    }
}
