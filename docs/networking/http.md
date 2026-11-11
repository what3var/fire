# HTTP (`#import "http"`)


Written in fire on top of `net`: a connection is a `Net.IConnection` (a `Net.TcpClient`, or a `Tls.Stream` for https). Everything is in `namespace Http`; the package needs `net` (and with it `io`, `time`).

```
var client = new Http.Client()
var r = client.Get("http://example.org/")
print(r.status + " " + r.reason + " " + r.Text())

var server = new Http.Server("127.0.0.1", 8080)
server.Route("GET", "/hello", func (req) => Http.Response.FromText("hello " + req.Query("name", "world")))
server.Route("POST", "/echo", func (req) => req.Text())            // a text becomes a 200 text response, undefined a 204
server.Run()                                                       // or ServeOne(timeout) in a loop of your own
```

| class | |
|---|---|
| `Http.Client` | `Get(url)`, `Head`, `Delete`, `Post(url, body, contentType = undefined)`, `Put`, `Request(method, url, body, headers)`; a body is a text (UTF-8) or a byte buffer; `headers` an `Http.Headers` or undefined. Settings: `timeout` (ms, connect and every read; 30000), `followRedirects` (true), `maxRedirects` (5), `maxBodyBytes` (16 MiB), `userAgent`, `headers` (sent with every request). Redirects 301/302/303 become a GET without body, 307/308 keep method and body. Sends `Connection: close`. Understands `Content-Length`, chunked bodies and bodies that end with the connection. |
| `Http.Response` | `status`, `reason`, `headers`, `body` (bytes), `url` (the final URL), `Ok` (2xx), `Length`, `Text()` (UTF-8), `Header(name)`; for routes: `FromText(text, status = 200, contentType)`, `FromHtml`, `FromJson`, `Error(status, message)`, `Redirect(location, status = 302)` and `new Http.Response(status, body)` |
| `Http.Request` (server side) | `method`, `target`, `path` (percent decoded), `version`, `headers`, `body`, `remoteHost`, `Query(name, fallback)` (percent decoded, `+` is a space), `Text()`, `Header(name)` |
| `Http.Server(host, port)` | `Route(method, path, lambda)` (method `"*"` = any; a path ending in `*` matches the prefix; HEAD uses the route of GET and sends no body), `Fallback(lambda)` (default 404), `OnError(lambda)` (a route's lambda threw: the client gets a 500), `ServeOne(timeout = undefined)` (true if a connection was served), `Run()` (until `Stop()`), `Handle(connection)` (answers one request on a `Net.IConnection` you accepted yourself and closes it), `Port`, `Close()`; `readTimeout` (10 s) and `maxBodyBytes` (1 MiB) |
| `Http.Headers` | `Get(name)` (not case sensitive), `Set`, `Add`, `Has`, `Remove`, `Count`; `Set`/`Add` return the headers |
| `Http.Url` | `Parse(text)` (http and https, IPv6 in brackets), `scheme`, `host`, `port`, `path`, `Resolve(location)` |
| `Http.Uri` | `Encode(text)`, `Decode(text, plus = false)` (UTF-8 percent encoding) |

The server answers one connection after the other with `Connection: close`: simple and light enough for a microcontroller. For parallel requests run several servers (on different ports) in fire threads; a route's lambda
runs in the thread of its server. A route's lambda must not fail with a *runtime error* (`x.y` on undefined ends the thread, like anywhere): throw an exception of your own class; `OnError` sees it. Errors of the library are `Http.HttpException`
(`code`: 1 bad URL, 2 malformed message, 3 too many redirects, 4 too large, 5 not supported); network errors are the `Net.NetException`s of the net package. `https://` URLs work through the `tls` package ([TLS](tls.md)): `client.tls = options` (a `Tls.Options`) says whom to trust; `server.UseTls(certPem, keyPem)` serves HTTPS.

## A server and a client in one program

```fire
#import "http"

var server = new Http.Server("127.0.0.1", 0)            // port 0: any free port
server.Route("GET", "/hello", func (Http.Request req) => { return Http.Response.FromText("hello " + req.Query("name", "world")) })
server.Route("POST", "/echo", func (Http.Request req) => { return req.Text() })   // a text becomes a 200 text response
int port = server.Port

fire taking port {                                      // a client in a thread of its own
    var client = new Http.Client()
    var r = client.Get("http://127.0.0.1:" + port + "/hello?name=fire")
    print(r.status + " " + r.Text())                    // 200 hello fire
    var p = client.Post("http://127.0.0.1:" + port + "/echo", "ping", "text/plain")
    print(p.Text())                                     // ping
}

server.ServeOne(5s)                                     // answer two requests, then stop
server.ServeOne(5s)
server.Close()
```

The server stays in the main program here: its routes are lambdas, and lambdas cannot be handed to a thread. A server that should run in a thread
is made inside that thread.

## The application server: `Http.AppServer`

A server for many clients at once (docs/ROADMAP_HTTP.md step 1): every connection is a **task of its own** (natively light tasks on all cores; the waits on
the network go through the poller of the runtime), it serves **request after request** (keep-alive, also several requests sent at once), and every request
runs through a **chain of routers**.

```fire
#import "http"
class Stamp : Http.IRouter {                                    // a link of the chain: hands on and works on the answer
    Http.Response Handle(Http.Context ctx, Http.Next next) {
        Http.Response r = next.Invoke(ctx)
        r.headers.Set("X-Number", "" + ctx.Number)
        return r
    }
}
class Hello : Http.IRouter {                                    // answers itself, or hands on
    Http.Response Handle(Http.Context ctx, Http.Next next) {
        if (ctx.Request.path == "/hello") { return Http.Response.FromText("hello " + ctx.Request.Query("name", "world")) }
        return next.Invoke(ctx)                                 // the end of the chain: 404
    }
}
var server = new Http.AppServer("127.0.0.1", 8080)
server.PushTo(new Stamp()).PushTo(new Hello())
server.Run()                                                    // until Stop() or the task's token is cancelled
```

| class | |
|---|---|
| `Http.AppServer(host, port, backlog = 128)` | `PushTo(router)` (the server owns it; returns the server), `Use(func (Http.Context ctx, Http.Next next) => ...)` (a `LambdaRouter`), `UseTls(certPem, keyPem)` (the handshake runs in the accepting task), `Run()` (until `Stop()` or the token of the running task: `await server.Run() until source.Token`), `AcceptOne(timeout)` (accepts one connection and starts its task; true if one came), `Port`, `Connections`, `Settings`, `Close()` |
| `Http.ServerSettings` | `readTimeout` (10 s, every read and write inside a request), `keepAlive` (5 s an idle connection waits for its next request), `maxRequests` (1000 per connection), `maxBodyBytes` (1 MiB), `serverName` |
| `Http.IRouter` | `Http.Response Handle(Http.Context ctx, Http.Next next)`: answer, or `next.Invoke(ctx)` and maybe change its answer |
| `Http.Next` | `Invoke(ctx)`: the answer of the rest of the chain (404 when nothing is left) |
| `Http.Context` | `Request`, `Number` (the request's number on its connection), `Item(name, fallback)` / `SetItem(name, value)` (what a router notes for the ones behind it: the user, the session) |
| `Http.Routes` | the explicit routes as a link: `Map(method, path, func (Http.Context ctx) => ...)` (returns the routes; a text is a 200 text response, undefined a 204; a path ending in `*` is a prefix); what no route matches goes on |
| `Http.LambdaRouter(lambda)` | a router from a lambda (`Use`) |
| `Http.Response.FromStream(stream, contentType, length = -1, status = 200)` | a body read from a stream while it is sent (the response owns the stream; the server closes it); without a length it goes chunked |

* **Every connection gets a copy of the chain** when its task starts (`taking`): routers are configuration. What they share - counters, caches, sessions -
  lives in an actor they ask with `await` (docs/ROADMAP_HTTP.md). A router that throws gives the client a 500.
* **Lambdas in the chain** (`Use`, `Routes`) cannot be copied into a task yet (they may go to other threads with docs/ROADMAP_TASKS.md step 8 point 4):
  until then such a chain is served in the accepting task, one request per connection without keep-alive. Routers written as classes get all of it.
* `Http.Server` (above) stays the small server for one connection after the other.

### Static files: `Http.Files`

```fire
server.PushTo(new Http.Files("wwwroot", "/", true).CacheFor(3600))   // the folder, the path it serves, "/about" finds about.html
```

`Http.Files(root, prefix = "/", omitExtensions = false)` is a link of the chain: the path of a GET or HEAD request maps 1:1 to a file under the folder
(`prefix` is the part of the path that maps to it, e.g. `"/static"`); a folder answers with its `index.html` (or `index.htm`); what is not there goes on to
the next link. The body is streamed from the file, with `Content-Type` from the extension (`Http.Mime.Of`), `ETag` and `Last-Modified` (a request with
`If-None-Match` or `If-Modified-Since` gets a 304), `Accept-Ranges` and one range (`bytes=5-9`, `bytes=-3`: 206 with `Content-Range`; outside the file 416);
`CacheFor(seconds)` adds `Cache-Control: max-age`.

Never served: a path with `..` (also percent-encoded), a backslash, a colon or a part that starts with a dot (`.git`, `.env`), anything that resolves outside
the folder, and the sources of fire (`.fire`, `.fxml`, `.fireproj`, `.firesln`). A symbolic link inside the folder is followed (the file system API cannot
tell it yet). `Http.Files.Serve(file, request)` answers with one file from anywhere (a route of your own), `Http.LimitedStream(stream, count)` reads a part
of a stream.

### Sessions: `Http.Sessions`

```fire
class Counter : Http.IRouter {
    Http.Response Handle(Http.Context ctx, Http.Next next) {
        Http.Session s = ctx.Item("session")
        int n = Http.Num.Parse(s.Get("n", "0")) + 1
        s.Set("n", n)
        return Http.Response.FromText("visit " + n)
    }
}
var server = new Http.AppServer("127.0.0.1", 8080)
server.PushTo(new Http.Sessions(20 * 60000, new Http.FileSessionStore("sessions"))).PushTo(new Counter())
server.Run()
server.Close()                                   // also ends the home of the sessions
```

`Http.Sessions(timeoutMs = 20 min, store = undefined, cookie = "fire-session", secure = false)` is a link of the chain: it finds the session of the request by
its cookie (or starts one), puts an `Http.Session` into the context (`ctx.Item("session")`) and sets the cookie on the answer when the session is new or got a
new id (`HttpOnly; SameSite=Lax`, with `secure` also `Secure`; an ended session clears it).

* **The home of the sessions** is an actor (`Http.SessionHome`) on a thread of its own: every access is a message to it (`await`), so they run one after
  the other and every connection sees the same state of a session, without locks. A session that no request touches for `timeoutMs` ends (and is deleted
  from the store). `Http.AppServer.Close()` ends the home (`Http.IClosable`).
* `Http.Session`: `Id`, `Get(name, fallback)`, `Set(name, value)` (a text; other values as their text; undefined removes), `Remove(name)`, `Renew()` (a new
  id for the same values - after a login, so that an id someone may have seen is no longer good), `End()`.
* **Ids** are 32 hex digits from the random source of the operating system (`Net.SecureRandom`); a cookie that is not such an id starts a new session.
* **Stores** (`Http.ISessionStore`: `Load(id)`, `Save(id, data)`, `Delete(id)`; the home calls them one after the other): `Http.MemorySessionStore` (the default:
  the sessions end with the program), `Http.FileSessionStore(folder)` (one file `<id>.session` per session: a restarted program finds them again). Databases
  implement the same interface later. A store keeps the values as text (`name=value&...`); live objects of a session (RPC instances) are for step 4.

### RPC: `#import "http.rpc"`

Methods and properties of classes marked `[Remote]` are called over HTTP with JSON (docs/ROADMAP_HTTP.md step 4). The package brings `reflection` and `json`;
`http` stays without them.

```fire
#import "http.rpc"
[Remote]
[Lifetime "session"]                         // one instance per session (Http.Sessions before Http.Rpc); "request" (default), "single"
[Timeout 600]                                // seconds without a call until the instance ends (default 300)
class Cart {
    List items
    construct() { this.items = new List() }
    [Remote] int Put(string name) { this.items.Add(name); return this.items.Count }
    [Remote] List Items() { return this.items }
}
var server = new Http.AppServer("127.0.0.1", 8080)
server.PushTo(new Http.Sessions()).PushTo(new Http.Rpc("/rpc", services))
```

| request | |
|---|---|
| `POST /rpc/<Name>/<Method>` (`<Name>`: the nickname or the full name) | body: the arguments as a JSON array (empty: none); `{"result": ...}` |
| `GET /rpc/<Class>/<Property>` | `{"result": ...}` |
| `POST /rpc/<Class>/<Property>` | body `[value]`: writes it |

* **Only with a session:** `Http.Sessions` must come before `Http.Rpc`, and only a client that already has a session may call - a request without the
  cookie of a living session is a 401 (it gets a session cookie with the answer; `Http.Session.IsNew` tells a session that began with the request).
* **Only what is marked is reachable:** a class with `[Remote]`, and of it only the members with `[Remote]` (not static ones). Everything else is a 404. `[Remote]`
  also keeps the class and the members in a native build (like `[Keep]`), since they are only reached by their name.
* **Public names:** a class is reached by its full name (`Shop.Cart`), or - with `[Nickname "cart"]` - only by its nickname: the names of the program stay
  hidden, and two classes of the same name in different namespaces can both be offered. Two classes with the same public name are an `ArgumentException`
  when the `Http.Rpc` is made.
* **Arguments** are converted to the parameter types: numbers, texts, `bool` and arrays as they are, a JSON object to an instance of the parameter's class
  (`Json.Deserialize`). A wrong number of arguments or text that is no JSON is a 400. The result is written with `Json.Serialize` (without `$type`).
* **Errors:** what the member throws is a 500 `{"error": message, "type": class}`; another HTTP method a 405; no session a 401.
* **Lifetimes:** `request` - a new instance for every call, in the task of the connection; `session` - one per session and class; `single` - one for the
  server. Session and single instances live at the home of the RPC instances, an actor on a thread of its own (`Http.RpcHome`), so their calls run one after the
  other; `Http.AppServer.Close()` ends it.
* **Dependency injection:** `new Http.Rpc(prefix, services)` - the first constructor of the class whose parameters all come from `services` (a list; matched
  by type, a subclass or an interface) makes the instance. Actors are shared by every call; other services are copied for every connection and once for the
  home.
* `Http.ServerSettings.showErrors` (false): a 500 of the server tells the message of what a router threw - for development.

### WebSocket: `Http.WebSockets`, `Http.WebSocket`

```fire
class Echo : Http.IWebSocketHandler {
    undefined Serve(Http.WebSocket ws) {                 // runs in the task of the connection, as long as it wants
        while (true) {
            var m = ws.Receive()                         // undefined: closed
            if (m == undefined) { break }
            ws.Send("echo: " + m.Text())
        }
    }
}
server.PushTo((new Http.WebSockets("/chat", new Echo())).AllowOrigin("https://example.org"))

var ws = Http.WebSocket.Connect("ws://127.0.0.1:8080/chat")   // the client side (wss:// over TLS)
ws.Send("hello")
print(ws.ReceiveText(5000))
ws.Close()
```

* `Http.WebSockets(path, handler)` is a link of the chain: a GET on `path` with `Upgrade: websocket` gets the 101 (RFC 6455: `Sec-WebSocket-Accept` from SHA-1 and
  Base64), then the task of the connection gives the connection to `handler.Serve(ws)` until it returns; a request without the upgrade gets a 426, a bad
  handshake a 400. `AllowOrigin(origin)` (any number): only pages of these origins may open it (403 otherwise) - the check against other sites; without
  one every origin may.
* `Http.WebSocket`: `Send(text)`, `SendBytes(bytes)`, `Ping()`, `Receive(timeout = -1)` (the next `Http.WebSocketMessage` - `IsText`, `Text()`, `data`; pings are
  answered and fragments joined on the way; undefined when the other side closed it, or when `timeout` ms ran out - then `IsOpen` is still true),
  `ReceiveText(timeout)`, `Close(code = 1000, reason)`, `IsOpen`, `CloseCode`, `maxMessageBytes` (16 MiB; a longer message closes with 1009).
  `Http.WebSocket.Connect(url, timeout, headers)` is the client (it masks its frames, the server does not). `Http.Base64.Encode(bytes)`.
* The handler is a copy for every connection, like every router. What several connections share (a chat room) is an actor that the handlers send to.
