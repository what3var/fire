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
