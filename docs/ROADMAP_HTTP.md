# HTTP serving - roadmap (brainstorm)

Status: **design, nothing built.** Collected ideas for an application server on top of `fire-http` (docs/NETWORK.md): a chain of routers, sessions,
static files from `wwwroot`, RPC on classes and FXML pages that run on the server and show in the browser (in the spirit of Blazor, but simpler).
Open points are marked **open**.

## 1. Threads and sessions

fire threads share no memory, and the objects of a session (RPC instances, FXML pages with probes and lambdas) cannot be copied with `taking`.
Therefore:

* **Every session belongs to one worker thread.** An accept thread takes the connection, reads the headers, finds the session cookie and hands the
  connection to the worker that owns the session (new sessions: the least busy worker). Everything of the session lives only there: no locks, the
  state is always consistent.
* Handing over: sockets are integer handles in the bridge of `net`, so the number can go to the worker in an actor message. To check: the same for
  the state of a `Tls.Stream`.
* **Single instances** cannot be shared objects. Idea: they are `actor`s.
  **Open:** an actor call has no way back (no return value, only messages). A form of request/answer is needed before this works - to be designed.
  Fallback: one instance per worker.

## 2. Router chain

A request runs through a chain of routers; the last element is an endpoint that resolves the real target.

```
var server = new Http.AppServer("", 8080)
    .PushTo(take new Http.SessionResolver(store, 20min))
    .PushTo(take new Http.Authenticator(users))
    .PushTo(take new Http.Router("/app"))
    .PushTo(take new Http.EndpointSelector())
server.Start()
```

* A router returns a **response object** (no hidden state): `Http.Response Handle(Http.Context ctx, Http.Next next)`. It answers itself or calls
  `next`, and can work on the answer of the rest of the chain (cookies, compression, logging, timing).
* `PushTo` returns the server, so the declaration ends with the server and `Start()` is on the right object; the server owns the chain (`take`).
* `Http.Context`: the request plus `Session`, `User`, route values, `Items`.
* Order: session before authentication (a login lives in the session). The router *authenticates* (who); the endpoint *authorizes* (may), because
  only it knows the target: `[Authorize "admin"]` on the class.

## 3. Paths and `wwwroot`

* By convention the project has a folder `wwwroot` (any other can be set). The standard router maps paths 1:1; a parameter allows leaving out the
  extension of rendered or executed things (`.html`, classes, `.fxml`).
* Musts: no path traversal (`..`, encoded forms, symlinks), **never serve `.fire` sources**, default documents (`index.html`, `index.fxml`), MIME table,
  `ETag`/`304`, ranges, streamed bodies (`IO.IStream` as a response body, not only `byte[]`).
* A native build has no compiler at run time: `.fxml` pages and classes in `wwwroot` are **compiled with the project**; the router maps the path to the
  generated class (`Type.Named`). What is cached at run time is the HTML template of a class. Static files are copied to the output.

## 4. RPC

* Functions are called, properties changed and events raised on classes and FXML pages. Only what is marked is reachable (`[Remote]`).
* Lifetime by attribute: `[Lifetime Session]`, `[Lifetime Request]`, `[Lifetime Single]` (see section 1). Instances belong to the session and die at
  their timeout: idle time since the last call, default 5 min, longer with `[Timeout 30min]`.
* Dependency injection is automatic: constructor parameters by type (reflection `Constructors()`, `ParamTypes`) from a container on the server.
  Arguments come as JSON and are checked against the parameter types.
* Instance ids are random tokens; every access checks that the id belongs to the session.

## 5. FXML in the browser

* First request: full HTML (fast, readable for search engines); then a small `fire.js` connects over a **WebSocket** and attaches to the instance.
* No virtual DOM: probes and bindings tell the server exactly which property of which element changed. An update is `(element, property, value)`,
  lists get insert/remove.
* Layout `Stack`/`Grid`/absolute to flex, CSS grid, `position:absolute`; styles and triggers to CSS classes (hover etc. without a round trip). Text
  input is sent debounced.
* Drawing: the draw calls are recorded on the server (rect, text, path, image), sent as a list and replayed on a canvas 2D; unchanged frames are
  skipped by hash, images cached by id.
* JS from the server: fire has no `async`, and a blocking wait would deadlock (the answer comes over the socket of the same worker). Default
  `Js.Call("fn", args, func(result) => ...)`, plus a blocking form with a timeout that keeps working on the connection's messages while it waits.
* A lost WebSocket keeps the instance for a grace period (reload, short loss of network).

## 6. Session store

* Sessions have a time limit (set in the router's constructor) and are thread safe through their owner thread (section 1).
* `ISessionStore` (`Load`, `Save`, `Touch`, `Delete`); `FileSessionStore` first, databases implement the same interface later.
* Only the serializable data is stored; live instances end with a restart. Written at the end of a request (milliseconds). Several processes on one
  store need versions (optimistic locking) - later.
* Cookies `HttpOnly`, `Secure`, `SameSite`; a new id after login; origin check for WebSockets.

## 7. Endpoint selector

Resolves in this order: explicit routes (lambdas), RPC/FXML, static files, fallback. The file client works with lambdas:
`Map(pattern, func(req) => path)` (URL to real path) or `Map(pattern, func(ctx) => response)` (does the request itself).

## Steps

1. Server core: keep-alive, worker threads with session ownership, router chain, `Http.Context`, streamed bodies.
2. File client and `wwwroot` (safety, caching, MIME).
3. Sessions and `ISessionStore` (files).
4. RPC with DI and lifetimes.
5. WebSocket.
6. FXML to HTML/CSS, `fire.js`, live updates.
7. Canvas recording, JS calls.

## Open

* The way back for actors (request/answer), needed for single instances.
* A further language/runtime feature that touches this plan (to be discussed).
* Which FXML elements the browser covers first.
