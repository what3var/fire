# HTTP serving - roadmap (brainstorm)

Status: **design, nothing built** (revised for the tasks, `await` and the lambda rules of docs/ROADMAP_TASKS.md). Collected ideas for an application
server on top of `fire-http` (docs/networking/http.md): a chain of routers, sessions,
static files from `wwwroot`, RPC on classes and FXML pages that run on the server and show in the browser (in the spirit of Blazor, but simpler).
Open points are marked **open**.

## 1. Tasks, connections and sessions

fire threads share no memory; since docs/ROADMAP_TASKS.md a fire thread is a light task on a pool of system threads (all cores in a native build), `await`
waits for results (also of actors), lambdas capture only with `taking`/`handover` and may go to other threads.

* **Every connection is a task of its own.** The accept task only hands the connection over: `fire handover tcp { ... }`. Thousands of connections
  (keep-alive, WebSockets of FXML pages) cost no system thread each. In the VM every fire thread is still a system thread: good for development, the
  native build is the target for many connections.
* **Every session has a home**: an actor (or a task) that owns the session object and everything that lives with it (session data, RPC instances of
  lifetime Session). A connection task sends the part of a request that needs the session there - `await session.Handle(req)` or `await on session { ... }` -
  and gets the response back (handed over). All work on a session runs one after the other at its home: the state is always consistent, without locks.
  A session that nobody uses ends at its home (`Sleep`/`until` with its time limit).
* **Single instances are actors**; the way back is `await single.Method()`.
* Time limits per request with `until`: `await endpoint.Invoke(ctx) until server.requestTimeout`.

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
* **Routers are configuration: every connection task gets a copy of the chain** when it starts (with keep-alive the copy serves many requests). Lambdas
  of routers go along; what they need of locals or globals they capture with `taking`. **Shared state** (rate limits, caches, counters) is not kept in a
  router but in an actor that the router asks with `await`. (A first approach, decided by the user; to be measured.)
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
* Lifetime by attribute: `[Lifetime Request]` (lives in the connection task), `[Lifetime Session]` (lives at the session's home), `[Lifetime Single]`
  (an actor), see section 1. Instances die at their timeout: idle time since the last call, default 5 min, longer with `[Timeout 30min]`.
* Dependency injection is automatic: constructor parameters by type (reflection `Constructors()`, `ParamTypes`) from a container on the server. What is
  injected across tasks are actor references (they may be shared); anything else is made where the instance lives.
  Arguments come as JSON and are checked against the parameter types.
* Instance ids are random tokens; every access checks that the id belongs to the session.

## 5. FXML in the browser

* First request: full HTML (fast, readable for search engines); then a small `fire.js` connects over a **WebSocket** and attaches to the instance.
* A page (circuit) has two tasks: one reads the WebSocket and passes events on (`on page`), the other owns the page instance - the probes and bindings
  fire at its home and write the updates to the socket themselves.
* No virtual DOM: probes and bindings tell the server exactly which property of which element changed. An update is `(element, property, value)`,
  lists get insert/remove.
* Layout `Stack`/`Grid`/absolute to flex, CSS grid, `position:absolute`; styles and triggers to CSS classes (hover etc. without a round trip). Text
  input is sent debounced.
* Drawing: the draw calls are recorded on the server (rect, text, path, image), sent as a list and replayed on a canvas 2D; unchanged frames are
  skipped by hash, images cached by id.
* JS from the server: `var r = await Js.Call("fn", args)` - the call sends the request and waits on a **`Completion`** (built, docs/ROADMAP_TASKS.md);
  the reading task sets it when the answer comes over the WebSocket (`c.Set(value)`); `until 5s` or the page's token end the wait.
* A lost WebSocket keeps the instance for a grace period (reload, short loss of network).

## 6. Session store

* Sessions have a time limit (set in the router's constructor) and are thread safe through their home (section 1).
* `ISessionStore` (`Load`, `Save`, `Touch`, `Delete`); `FileSessionStore` first, databases implement the same interface later.
* Only the serializable data is stored; live instances end with a restart. Written at the end of a request (milliseconds). Several processes on one
  store need versions (optimistic locking) - later.
* Cookies `HttpOnly`, `Secure`, `SameSite`; a new id after login; origin check for WebSockets.

## 7. Endpoint selector

Resolves in this order: explicit routes (lambdas), RPC/FXML, static files, fallback. The file client works with lambdas:
`Map(pattern, func(req) => path)` (URL to real path) or `Map(pattern, func(ctx) => response)` (does the request itself).

## 8. Waiting on the network (step 0)

The net package waits by polling: it sleeps 1 to 10 ms between attempts (`net.fire`). In a task that is a switch, but a server with many idle
keep-alive or WebSocket connections would wake every task up to a hundred times a second. Before the server: **a poller in the scheduler** -
epoll (Linux), kqueue (macOS), IOCP or `WSAPoll` (Windows), `select` (lwIP) - a task registers its socket and sleeps until it is ready, its time limit
ran out or its token is cancelled. **Built** (poll()/WSAPoll, not yet epoll/kqueue/IOCP): `__fd_wait(fd, events, ms)` of the runtime - natively a poller thread
(fire_rt.h `fire_fd_wait`, the waiting task parks with `blockUntil` and is woken by its address), in the VM poll()/WSAPoll in short slices (fire.Runtime.FdWait);
`Net.Clock.WaitFor` in net.fire uses it, -1 (no poller) falls back to the old pause. For many thousands of sockets epoll/kqueue can replace poll() later.

## Steps

0. Poller in the scheduler (waits of the network without polling) and `Completion` - built.
1. Server core: a task per connection, keep-alive, router chain (a copy per connection), `Http.Context`, streamed bodies, limits - **built**
   (`Http.AppServer`, docs/networking/http.md). Open: a chain with lambdas waits for step 8 point 4 (lambdas to other threads) and is served in the accepting
   task meanwhile; the TLS handshake runs in the accepting task.
2. File client and `wwwroot` (safety, caching, MIME) - **built** (`Http.Files`, docs/networking/http.md). Open: the `Map(pattern, lambda)` form of the
   file client (with the lambdas of step 8 point 4), symbolic links (the file system API cannot tell them), several ranges in one request.
3. Sessions (their home, time limit) and `ISessionStore` (files) - **built** (`Http.Sessions`, docs/networking/http.md): one home actor for all sessions of a
   server on a thread of its own, ids from `Net.SecureRandom`, `Renew`, `End`, memory and file stores. Found on the way: `taking` copied an actor that it
   reached through a field (VM and native) - now its reference goes along. Open: one home per session (when one home becomes the bottleneck), live objects
   in a session (step 4).
4. RPC with DI and lifetimes - **built** (package `http.rpc`, docs/networking/http.md): `[Remote]` classes and members, JSON, lifetimes request/session/
   single (one home actor for session and single instances), `[Timeout]`, constructor injection from a list of services; `[Remote]` keeps them in a native
   build. Only a client that already has a session may call (401 otherwise); `[Nickname]` gives a class its public name (decided by the user). Found on the way (VM): `list.Add(actor)` and the assignment of an actor to a field posted a message `tryTakeTo` to the actor - ownership calls on an
   actor are no messages now. Open: events (with step 5), the random instance ids of the roadmap (instances are addressed by class and session for now).
5. WebSocket - **built** (`Http.WebSockets`, `Http.WebSocket`, docs/networking/http.md): RFC 6455 server and client, fragments, ping/pong, close, origins. Open:
   sending to a socket from another task (the reader/writer split of an FXML page: the page's task writes, the reading task gets what comes in) and
   compression (permessage-deflate).
6. FXML to HTML/CSS, `fire.js`, live updates.
7. Canvas recording, JS calls.

## Open

* Which FXML elements the browser covers first.
* Whether the copy of the router chain per connection is cheap enough (measure; else one per worker).
