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
