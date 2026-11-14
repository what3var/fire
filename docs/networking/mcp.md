# MCP: the Model Context Protocol (`#import "mcp"`)

MCP connects an AI client (an agent, an assistant, an editor's chat) with servers that offer **tools** (functions it may call), **resources** (texts it
may read) and **prompts** (ready questions). The messages are JSON-RPC 2.0. The package has both sides, written in fire:

* `Mcp.Client` - asks a server: its tools, calls them, reads its resources, gets its prompts, receives its notifications;
* `Mcp.Server` - offers tools, resources and prompts as lambdas, over stdio or HTTP.

It speaks the protocol versions 2025-11-25 (the default), 2025-06-18, 2025-03-26 and 2024-11-05 - the ones with the `initialize` handshake. It was
checked against the official Python SDK of MCP (2.3): a fire client with a Python server and a Python client with a fire server, over stdio and over
Streamable HTTP. (The stateless revision 2026-07-28 - one exchange per request, `subscriptions/listen` - is not built yet; servers of the newer SDKs
still speak the versions above.)

## A client

```fire
#import "mcp"
#import "json"

var client = new Mcp.Client(new Mcp.ProcessTransport("python3", ["server.py"]))   // stdio: the server is a process
client.Initialize("my-agent", "1.0")
print(client.ServerName + " " + client.ProtocolVersion)

foreach (t in client.Tools()) { Mcp.Tool tool = t; print(tool.Name + ": " + tool.Description) }
var r = client.CallTool("add", "{\"a\": 2, \"b\": 40}")          // arguments as JSON text or a JsonObject
print(r.Text + " " + r.IsError)                                   // 42 False

foreach (x in client.Resources()) { Mcp.Resource res = x; print(res.Uri + ": " + client.ReadResource(res.Uri)) }
print(client.GetPrompt("greet", "{\"name\": \"Ann\"}"))
client.Close()
```

| `Mcp.Client` | |
| --- | --- |
| `new Mcp.Client(transport)` | the client owns the transport (closes it) |
| `Initialize(name, version)` | the handshake; then `ServerName`, `ServerVersion`, `ProtocolVersion`, `Instructions`, `Capabilities` (JSON text) |
| `Tools()` | a list of `Mcp.Tool`: `Name`, `Title`, `Description`, `Schema` (the JSON schema of its arguments) - all pages |
| `CallTool(name, arguments)` | an `Mcp.ToolResult`: `Text` (its text parts), `IsError`, `Content` (the parts as `JsonObject`s), `Structured` |
| `Resources()`, `ReadResource(uri)`, `Subscribe(uri)` | `Mcp.Resource`: `Uri`, `Name`, `Description`, `MimeType` |
| `Prompts()`, `GetPrompt(name, arguments)` | `Mcp.Prompt`: `Name`, `Description`, `Arguments` |
| `Ping()`, `Call(method, paramsJson)`, `Notify(method, paramsJson)` | any request (its result as JSON text) or notification |
| `Poll()`, `Notifications()`, `OnNotification` | what the server sends on its own (`notifications/tools/list_changed`, `notifications/resources/updated`, `notifications/message` ...): to the lambda `(string method, string paramsJson) => ...`, or kept for `Notifications()` (`Mcp.Notification`: `Method`, `Params`) |
| `Timeout` | milliseconds a request may take (60000); then `notifications/cancelled` and an `Mcp.McpException` |

An error of the server is an `Mcp.McpException` with its JSON-RPC `code` (-32601 no such method, -32602 invalid parameters - also an unknown tool -,
-32002 no such resource, -32603 internal). A tool that fails is no exception: its result has `IsError`.

## Transports

| | |
| --- | --- |
| `Mcp.ProcessTransport(file, args, workingDirectory)` | stdio, the client side: starts the server, a message is a line of its input and output; what it writes to its standard error is in `Log` |
| `Mcp.HttpTransport(url)` | Streamable HTTP: every message is a POST, the answer JSON or server-sent events; `SessionId` (the server's `Mcp-Session-Id`), `Headers` (sent with every request, e.g. `Authorization`), `Listen()` opens the stream of the server's own messages (`http://`); `Close()` ends the session (DELETE) |
| `Mcp.DirectTransport(server)` | a server in the same program |
| `Mcp.StdioTransport()` | stdio, the server side (`server.RunStdio()` uses it) |

A transport of your own implements `Mcp.ITransport` (`Send`, `Receive(timeoutMs)`, `Open`, `Close`).

## A server

```fire
#import "mcp"
#import "json"

var server = new Mcp.Server("fire-tools", "1.0")
server.Instructions = "Tools written in fire."
server.Tool("add", "Adds two numbers",
    "{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"number\"},\"b\":{\"type\":\"number\"}},\"required\":[\"a\",\"b\"]}",
    (JsonObject args) => Mcp.Result.Text("" + (args.Get("a") + args.Get("b"))))
server.Resource("fire://notes", "notes", "Some notes", "text/plain", () => "Buy milk.")
server.Prompt("greet", "A greeting", ["name"], (JsonObject a) => "Say hello to " + a.Get("name"))
server.RunStdio()                                         // or: server.HttpServer("127.0.0.1", 8765).Run()
```

| `Mcp.Server` | |
| --- | --- |
| `Tool(name, description, schemaJson, handler)` | `handler`: `(JsonObject args) => Mcp.Result` - `Mcp.Result.Text(text)`, `Mcp.Result.Error(text)`, `AddText`, `AddImage(base64, mimeType)`, `Structured` (JSON text); a handler that throws gives an error result |
| `Resource(uri, name, description, mimeType, handler)`, `Prompt(name, description, arguments, handler)` | `() => text`, `(JsonObject args) => text` |
| `RemoveTool(name)`, `ResourceUpdated(uri)`, `Log(level, text)`, `Notify(method, paramsJson)` | a change of the tools is announced (`list_changed`), a resource the client subscribed to as `resources/updated` |
| `Handle(message)` | the answer to one message (JSON text; "" for a notification) - for a transport of your own; `TakeNotifications()` gives what to send besides |
| `RunStdio()` | serves on this program's standard input and output until the client closes it |
| `HttpServer(host, port, path = "/mcp")` | an `Http.Server` for Streamable HTTP: POST answers as JSON (as server-sent events when notifications go with it), a session id at the start, DELETE ends it; GET is 405 (the notifications go with the answers) - `Run()` or `ServeOne()` it |
| `OnRequest` | `(string method, string paramsJson) => any`: an answer (JSON text) to a method that is no part of MCP, undefined for "no such method" |

## With an agent

The tools of a server become tools of an agent of the package [`ai.agent`](../ai/agent.md): their schemas fit `ToolWithSchema`.

```fire
#import "ai.agent"
#import "mcp"

var client = new Mcp.Client(new Mcp.HttpTransport("http://127.0.0.1:8765/mcp"))
client.Initialize("assistant")
var agent = new AI.Agent("LFM3-1.7B-QAD-Q4_0.gguf", "You help in the editor.")
foreach (t in client.Tools()) {
    Mcp.Tool tool = t
    string name = tool.Name
    agent.ToolWithSchema(name, tool.Description, tool.Schema, func (JsonObject args) on client => CallTool((taking name), args).Text)
}
print(agent.Ask("Which errors does the project have?"))
```
