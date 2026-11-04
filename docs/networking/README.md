# Networking

fire talks to the network with three packages, each imported on its own:

| Import | Package | What |
| --- | --- | --- |
| `#import "net"` | `fire-net` | TCP clients and servers, UDP, DNS - this page |
| `#import "http"` | `fire-http` | an HTTP/1.1 client and server, written in fire on top of `net` - [HTTP](http.md) |
| `#import "tls"` | `fire-tls` | TLS 1.2/1.3 over a TCP connection; `http` uses it for `https://` - [TLS](tls.md) |
| `#import "wifi"` | `fire-wifi` | the radio of a board: scan, join, access point - [WiFi](wifi.md) |

They work the same in the VM and in a native program, on Windows, Linux, macOS and the ESP32 (over lwIP); a platform without a network (`freertos`
without a board package) throws a `NetException` with code 9 ("not supported") at the first call.

## A TCP client and server

```fire
#import "net"

var listener = new Net.TcpListener("127.0.0.1", 0)      // port 0: any free port
int port = listener.Port

fire handover listener {                                // the server runs in a thread of its own
    var client = listener.Accept(5s)
    var buffer = new byte[64]
    var n = client.Read(buffer, 0, 64)
    client.Write(buffer, 0, n)                          // echo
    client.Close()
    listener.Close()
}

var c = new Net.TcpClient("127.0.0.1", port)
c.WriteString("hello")
var reply = new byte[64]
var n = c.Read(reply, 0, 64)
print(IO.Utf8.GetString(reply).Substring(0, n))         // hello
c.Close()
```

`fire handover` gives the listener to the thread ([threads](../language/15-threads-and-actors.md#handing-over-handover)) - a socket must not be copied.

## UDP and DNS

```fire
#import "net"

var a = new Net.UdpSocket("127.0.0.1", 0)
var b = new Net.UdpSocket("127.0.0.1", 0)
b.SendString("ping", "127.0.0.1", a.Port)
var buffer = new byte[32]
var n = a.ReceiveFrom(buffer, 0, 32, 2s)
print(n + " bytes, from b: " + (a.FromPort == b.Port))   // 4 bytes, from b: True
print(Net.Dns.First("localhost"))                         // 127.0.0.1
```

## How it works


```
program        #import "net"      TcpClient, TcpListener, UdpSocket, Dns, NetAddress   (fire, package fire-net)
bridge         fire_bridge_net.h     handles, errors, timeouts, policy                  (C99, shared by VM and native; TinyCC is enough)
platform       fire_net_*            connect/listen/accept/send/recv/poll/resolve       (fire_net.h: posix BSD sockets, windows Winsock, esp32 lwIP; freertos/wasm none)
```

* **Handles, not objects:** a socket is an integer handle in a table of the bridge, like the streams of `io`. The fire classes own the handle (`Close()`, destructor closes it).
* **Streams:** a connected TCP socket is an `IO.Stream` (`Read`, `Write`, `ReadByte`, ..., not seekable) so everything that works with streams works with the network
  (`IO.Stdio`, text readers, a future HTTP package). `UdpSocket` is message based: `SendTo(address, buffer)`, `ReceiveFrom(buffer)` returns the sender.
* **Blocking with a timeout, and polling:** every call that waits takes an optional `TimeSpan` (`Read`, `Accept`, `Connect`, `ReceiveFrom`); `Available()` / `Poll(read, write, timeout)` ask without
  waiting. Waiting is **not deaf** like `Sleep`: the fire code polls the natives (a timeout of 0) and sleeps 1 to 10 ms between the attempts - it never sits inside the library - so `terminate`, threads and the main queue keep working (THREADING_DESIGN.md section 7). No hidden background threads on small targets.
* **Errors:** `NetException` with a `code` (`Refused`, `TimedOut`, `Unreachable`, `AddressInUse`, `Closed`, `Unsupported`, ...), never a crash; an error text from the OS goes into `message`.
* **Addresses:** `NetAddress` (IPv4 and IPv6, `Parse("10.0.0.5:80")`, `Dns.Resolve("example.org")` returns a list), and `"host:port"` strings accepted where an address is expected.
* **Policy:** like `IoPolicy` for files, the host can restrict the network (allow nothing, allow loopback only, an allow list of hosts/ports); the editor and `forge` pass it. Default for a
  program started by hand: everything allowed; `DenyAll` for scripts that are only run from a package source.
* **Tests:** loopback only (a listener and a client in the same program, in threads) - no real network in the test suite. UDP: loopback datagrams. DNS: `localhost` and literal addresses.

## Reference

Everything is in `namespace Net`. The package needs `io` and `time` (they come with it). Time limits are a `TimeSpan`, a time value (`500ms`, `5s`) or a number (milliseconds); `undefined` means "no limit".

| class | |
|---|---|
| `Net.TcpClient(host, port, timeout = 10000)` | connects; an `IO.Stream` (`Read`, `Write`, `ReadByte`, `ReadBytes`, `CopyTo`, ...), plus `WriteString(text)` (UTF-8), `ReadTimeout`/`WriteTimeout` (settable), `RemoteHost`, `RemotePort`, `LocalHost`, `LocalPort`, `Available`, `WaitReadable(timeout = 0)`, `NoDelay`, `KeepAlive`, `Shutdown(0 receive \| 1 send \| 2 both)`, `Close()`. `Read` returns 0 when the other side has closed its end. |
| `Net.IConnection` (interface) | what a network connection can do, implemented by `Net.TcpClient` and `Tls.Stream`: it extends `IO.IStream` (`Read(buffer, offset, count)`, `Write(buffer, offset, count)`, `Flush()`, `Close()`) and has the properties `ReadTimeout`, `WriteTimeout` (`get`/`set`, milliseconds or a time like `5s`, `undefined` for no limit) and `RemoteHost`, `RemotePort`, `LocalHost`, `LocalPort` (`get`). Code that works with any connection (like `Http.Server`) takes a `Net.IConnection`; since it is an `IO.IStream`, it goes without a cast to code that only reads and writes (`IO.IStream s = connection`, a parameter of type `IO.IStream`). |
| `Net.TcpListener(host, port, backlog = 16)` | host `""` listens on every interface, port 0 takes a free port: `Port`, `Host`; `Accept(timeout = undefined)` returns a `TcpClient` (`Net.TimeoutException` when the limit runs out), `TryAccept(timeout)` returns undefined instead, `Pending(timeout = 0)`, `Close()` |
| `Net.UdpSocket(host = "", port = 0)` | `SendTo(buffer, offset, count, host, port)`, `SendTo(buffer, host, port)`, `SendString(text, host, port)`, `ReceiveFrom(buffer, offset, count, timeout = undefined)` returns the size of the datagram (a longer one is cut), the sender is `FromHost`/`FromPort`; `Port`, `Available` (size of the next datagram), `WaitReadable(timeout = 0)`, `Broadcast`, `Close()` |
| `Net.Dns` | `Resolve(name)` returns a list of the addresses as text (IPv4 and IPv6; a literal address gives itself), `First(name)` |

Errors: `Net.NetException` (`message`, `code`) and its subclasses `RefusedException` (3), `TimeoutException` (4), `ClosedException` (2 handle closed, 7 reset or ended, 12 not connected), `PermissionException` (8 the host's policy, 13 the operating system), `ResolveException` (10);
the other codes (1 invalid argument, 5 unreachable, 6 address in use, 9 not supported, 11 other) come as a plain `NetException`. `Net.NetCodes` names the numbers. A destructor closes what is left open, and so does the end of the program.

The host decides with `NetPolicy` (`fire.Runtime`): `AllowAll` (default), `DenyAll`, `LoopbackOnly`, `Hosts(rules, allowListen)`; `RuntimeSession.Build(..., netPolicy: ...)` passes it, the library asks through `fire_host.net_allow` (native/abi/fire_pkg_abi.h). A native build is
unrestricted unless the target defines `FIRE_NET_POLICY` (a C function `int(const char* host, int port, int access, char* reason, size_t size)`, 1 allowed). A platform without a network (`freertos` until a board package provides one) throws `NetException` with code 9 on the first call.

**Byte buffers:** the methods that take `byte[]` need a byte buffer (`new byte[n]`, `"text".ToBytes()`, `IO.Utf8.GetBytes(text)`, what `Read` and
the other methods return). An array literal (`[1, 2, 3]`) or a declared `byte a[] = [...]` is not a buffer - passing one is a known bug that can
crash the program; see the [roadmap](../ROADMAP.md).
