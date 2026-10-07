# Networking and hardware buses - design and steps

Status: **networking (`fire-net`, `fire-http`, `fire-tls`) GPIO (`fire-gpio`), I2C (`fire-i2c`), SPI (`fire-spi`) and WiFi (`fire-wifi`) are built**, see the references below; nothing of the hardware is tried on a board yet. The steps are also kept as tasks (Net 1-3, GPIO, I2C, SPI, WiFi). Everything is a **package** (`ember`, docs/PACKAGES.md): the compiler and the
runtime do not change, a program that does not `#import` it does not carry it. Like `time`, `io` and `devices`, each one is a prelude in fire plus C++ in `native/bridges/` over a thin
platform layer in `native/platform/<name>/` (`plat::`), so the same code runs in the VM (through the package ABI, docs/PACKAGE_NATIVES.md) and in a native build, and a platform without the
feature fails with a clear "not supported" error instead of not compiling.

## Networking

### Layers

```
program        #import "net"      TcpClient, TcpListener, UdpSocket, Dns, NetAddress   (fire, package fire-net)
bridge         fire_bridge_net.hpp   handles, errors, timeouts, policy                  (C++, shared by VM and native)
platform       plat::net            connect/listen/accept/send/recv/poll/resolve       (posix: BSD sockets, windows: Winsock, freertos/esp32: lwIP)
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

### Steps

(1 and 2 are done; the text is what was planned.)

1. **Net 1 - design and HAL.** This document made concrete: `plat::net` for posix (BSD sockets) and windows (Winsock), the stub for platforms without a network, the bridge with the handle table, the
   error codes and the timeouts, the policy hook, the package skeleton `fire-net` (prelude + natives list), `ember` entry. Test: handle table, errors, policy.
2. **Net 2 - TCP, UDP, DNS.** `TcpClient`, `TcpListener`, `UdpSocket`, `Dns`; stream integration; loopback tests in the VM and the native build (the `natCases` comparison); `docs/NETWORK.md` becomes the
   reference. FreeRTOS/ESP32 build of `plat::net` over lwIP (`sockets.h` is BSD compatible: most of the posix file is shared).
3. **Net 3 - HTTP and TLS.** `fire-http`: a small HTTP/1.1 client (`Get`, `Post`, headers, chunked, redirects) and server (route lambdas) as a separate package on top of `net`. TLS (mbedTLS, which ESP-IDF and
   most Linux systems have) as its own step and package `fire-tls` that wraps a `TcpClient`; certificate handling decided there.

## Reference (`#import "net"`, the package `fire-net`)

Everything is in `namespace Net`. The package needs `io` and `time` (they come with it). Time limits are a `TimeSpan`, a time value (`500ms`, `5s`) or a number (milliseconds); `undefined` means "no limit".

| class | |
|---|---|
| `Net.TcpClient(host, port, timeout = 10000)` | connects; an `IO.Stream` (`Read`, `Write`, `ReadByte`, `ReadBytes`, `CopyTo`, ...), plus `WriteString(text)` (UTF-8), `ReadTimeout`/`WriteTimeout` (settable), `RemoteHost`, `RemotePort`, `LocalHost`, `LocalPort`, `Available`, `WaitReadable(timeout = 0)`, `NoDelay`, `KeepAlive`, `Shutdown(0 receive \| 1 send \| 2 both)`, `Close()`. `Read` returns 0 when the other side has closed its end. |
| `Net.TcpListener(host, port, backlog = 16)` | host `""` listens on every interface, port 0 takes a free port: `Port`, `Host`; `Accept(timeout = undefined)` returns a `TcpClient` (`Net.TimeoutException` when the limit runs out), `TryAccept(timeout)` returns undefined instead, `Pending(timeout = 0)`, `Close()` |
| `Net.UdpSocket(host = "", port = 0)` | `SendTo(buffer, offset, count, host, port)`, `SendTo(buffer, host, port)`, `SendString(text, host, port)`, `ReceiveFrom(buffer, offset, count, timeout = undefined)` returns the size of the datagram (a longer one is cut), the sender is `FromHost`/`FromPort`; `Port`, `Available` (size of the next datagram), `WaitReadable(timeout = 0)`, `Broadcast`, `Close()` |
| `Net.Dns` | `Resolve(name)` returns a list of the addresses as text (IPv4 and IPv6; a literal address gives itself), `First(name)` |

Errors: `Net.NetException` (`message`, `code`) and its subclasses `RefusedException` (3), `TimeoutException` (4), `ClosedException` (2 handle closed, 7 reset or ended, 12 not connected), `PermissionException` (8 the host's policy, 13 the operating system), `ResolveException` (10);
the other codes (1 invalid argument, 5 unreachable, 6 address in use, 9 not supported, 11 other) come as a plain `NetException`. `Net.NetCodes` names the numbers. A destructor closes what is left open, and so does the end of the program.

The host decides with `NetPolicy` (`fire.Runtime`): `AllowAll` (default), `DenyAll`, `LoopbackOnly`, `Hosts(rules, allowListen)`; `RuntimeSession.Build(..., netPolicy: ...)` passes it, the library asks through `fire_host.net_allow` (native/abi/fire_pkg_abi.h). A native build is
unrestricted unless the target defines `FIRE_NET_POLICY`. A platform without a network (`freertos` until a board package provides one) throws `NetException` with code 9 on the first call.

## HTTP (`#import "http"`, the package `fire-http`)

Written in fire on top of `net` (any `IO.Stream` works as the connection, so TLS plugs in later). Everything is in `namespace Http`; the package needs `net` (and with it `io`, `time`).

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
| `Http.Server(host, port)` | `Route(method, path, lambda)` (method `"*"` = any; a path ending in `*` matches the prefix; HEAD uses the route of GET and sends no body), `Fallback(lambda)` (default 404), `OnError(lambda)` (a route's lambda threw: the client gets a 500), `ServeOne(timeout = undefined)` (true if a connection was served), `Run()` (until `Stop()`), `Port`, `Close()`; `readTimeout` (10 s) and `maxBodyBytes` (1 MiB) |
| `Http.Headers` | `Get(name)` (not case sensitive), `Set`, `Add`, `Has`, `Remove`, `Count`; `Set`/`Add` return the headers |
| `Http.Url` | `Parse(text)` (http and https, IPv6 in brackets), `scheme`, `host`, `port`, `path`, `Resolve(location)` |
| `Http.Uri` | `Encode(text)`, `Decode(text, plus = false)` (UTF-8 percent encoding) |

The server answers one connection after the other with `Connection: close`: simple and light enough for a microcontroller. For parallel requests run several servers (on different ports) in fire threads; a route's lambda
runs in the thread of its server. A route's lambda must not fail with a *runtime error* (`x.y` on undefined ends the thread, like anywhere): throw an exception of your own class; `OnError` sees it. Errors of the library are `Http.HttpException`
(`code`: 1 bad URL, 2 malformed message, 3 too many redirects, 4 too large, 5 not supported); network errors are the `Net.NetException`s of the net package. `https://` URLs work through the `tls` package (below): `client.tls = options` (a `Tls.Options`) says whom to trust; `server.UseTls(certPem, keyPem)` serves HTTPS.

## TLS (`#import "tls"`, the package `fire-tls`)

TLS over a TCP connection of the net package. The natives are C++ (`native/bridges/fire_bridge_tls.hpp` over `plat::tls`, one backend per platform); the classes are in `namespace Tls`. The package needs `net` (`io`, `time`).
`http` uses it for `https://` URLs and for `Http.Server.UseTls`.

```
var s = Tls.Stream.Connect("example.org", 443)            // TCP connect and handshake; certificate chain and host name are checked against the system's certificates
s.WriteString("GET / HTTP/1.0\r\nHost: example.org\r\n\r\n")
print(s.Info)                                              // "TLSv1.3 TLS_AES_256_GCM_SHA384"

var o = new Tls.Options()
o.caPem = IO.File.ReadAllText("my-ca.pem")                 // trust exactly these (or o.caFile = "path")
// o.verify = false                                        // no checking at all: for tests only
var t = Tls.Stream.Connect("intranet", 8443, o)

var server = new Tls.Server(certPem, keyPem)               // PEM text: the certificate (then its chain) and the private key
var secure = server.Accept(listener.Accept())              // handshake as the server; the stream owns the connection
```

| class | |
|---|---|
| `Tls.Stream` | an `IO.Stream` like `Net.TcpClient`: `Read` (0 when the other side has ended the connection), `Write`, `WriteString`, `ReadTimeout`/`WriteTimeout`, `WaitReadable`, `Info`, `RemoteHost`, `RemotePort`, `Connection` (the TCP connection - do not use it), `Close()` (close_notify, then the connection is closed). `Tls.Stream.Connect(host, port, options = undefined, timeout = 10000)`; `new Tls.Stream(tcp, serverName, options, timeout)` wraps a connection you made (it belongs to the stream from then on) |
| `Tls.Options` | `verify` (true), `caFile`, `caPem` (PEM text; both empty: the system's certificates), `certPem` + `keyPem` (a client certificate) |
| `Tls.Server(certPem, keyPem)` | `Accept(tcp, timeout = 10000)` returns the `Tls.Stream`, `Close()` (close the streams first) |
| `Tls.Support.Available()` | false on a build or platform without TLS |

Errors: `Tls.TlsException` (code 14: handshake or protocol failure), `Tls.CertificateException` (15: expired, not signed by a trusted authority, wrong host name) - both are `Net.NetException`s - and the exceptions of net (a time limit is `Net.TimeoutException`).
The versions are TLS 1.2 and 1.3; SNI is sent; a server that closes without `close_notify` (HTTP/1.0 style) ends the data like a normal close. Everything runs without waiting inside the library (the handshake is done step by step from fire), so other threads and `terminate` keep working.

**Backends** (`native/platform/std/`): *OpenSSL* (`fire_tls_openssl.hpp`, 1.1.1 and 3.x) on Linux, macOS and Windows; *mbedTLS* (`fire_tls_mbedtls.hpp`, 2.28 and 3.x) on the ESP32 (it comes with ESP-IDF) and on a desktop with `FIRE_TLS_MBEDTLS`; *none*
(`FIRE_NO_TLS`, and `freertos` without a board package of your own).

* **Linux**: `libssl-dev` (Debian/Ubuntu) or `openssl-devel` must be installed where the library or the native program is built - the package's library for the VM is built at first use (or comes prebuilt in the package). Without the headers the build says so; a program
  that only uses `http://` is not affected (the error only shows when a TLS function is called, and a failed build is remembered for five minutes so that it is not retried at every start).
* **macOS**: OpenSSL from Homebrew/MacPorts; add its `include` and `lib` folders to the toolchain (`includeDirs`, `libs`).
* **Windows**: an OpenSSL for the MinGW toolchain (MSYS2: `mingw-w64-x86_64-openssl`); the system's certificate store is not used by OpenSSL there, so give `caFile`/`caPem`. A backend on SChannel (no extra installation, the Windows store) is planned.
* **ESP32**: the mbedTLS of ESP-IDF; add the component `mbedtls` to the project. To check servers without giving CA certificates define `FIRE_TLS_CA_ATTACH(conf)` as `esp_crt_bundle_attach(conf)` in the target (`includes`: `esp_crt_bundle.h`); there is no system
  store on a microcontroller. (Not tried on a board yet.)
* mbedTLS checks host *names* only, not IP addresses in the certificate: connecting to `127.0.0.1` against a certificate with an IP entry works with OpenSSL, not with mbedTLS.

## GPIO (`#import "gpio"`, the package `fire-gpio`)

Digital pins. It needs `time` (the clock of the time limits). The natives are `native/bridges/fire_bridge_gpio.hpp` over `plat::gpio` (`native/platform/std/fire_gpio_*.hpp`): the **GPIO character device** of Linux
(`/dev/gpiochipN`, the v2 ioctl interface of kernel 5.10+ - Raspberry Pi and other boards; used only when the kernel headers are there, `FIRE_NO_GPIO` switches it off), **ESP-IDF `driver/gpio`** on an ESP32
(add the components `driver` and `esp_timer`), and a stub elsewhere (Windows, macOS: no chips). Every platform has the **simulated chip** `"sim"`, so a program can be written and tested on the PC.

```
#import "gpio"

var led = new Gpio.Pin(17).Output()                        // line 17 of the default chip, an output that starts low
led.Write(true)
led.Toggle()                                               // returns the new level

var button = new Gpio.Pin(27).Input(Gpio.Pull.Up, Gpio.Edge.Falling)
if (button.WaitEdge(5s) != Gpio.Edge.None) { print("pressed at " + button.EdgeTime + " us") }
button.onEdge = (edge, micros) => { print("edge " + edge) }
button.Poll()                                              // hands every waiting edge to onEdge; call it from the program's loop
```

* `Gpio.Board.Chips()` lists the chips (`"sim"` first, then `"gpiochip0"`, ... on Linux, `"gpio"` on an ESP32); `Gpio.Board.DefaultChip()` is the first real one, or `"sim"` if the machine has none (`new Gpio.Pin(line)`); `Gpio.Board.Available()` tells whether there is
  hardware. `new Gpio.Pin("gpiochip1", 4)` names the chip.
* A pin is configured by `Input(pull, edge)` (`Gpio.Pull.None/Up/Down`, `Gpio.Edge.None/Rising/Falling/Both`) or `Output(initial)`; both return the pin. Opening claims nothing, configuring does: a second pin on the same line throws `Gpio.BusyException`. `Close()` gives it back.
  `Read()` (bool) / `ReadInt()`, `Write(bool)`, `Toggle()`; an output can be read back.
* Edges are collected (by the sim, by the kernel's event queue, by an interrupt on the ESP32) and handed out one by one: `TakeEdge()` (no waiting; `Gpio.Edge.None` if there is none, else `Rising`/`Falling`), `WaitEdge(timeout)` (polls and sleeps 1 to 5 ms, so `terminate` and other threads
  work; `None` when the time ran out) and `Poll()`. `EdgeTime` is when the edge that was taken last happened (microseconds on a clock that only goes forward). At most 16 edges are kept per pin; when nobody takes them the oldest are lost (Linux: the kernel's queue).
  Nothing is debounced: a mechanical button needs a pause in the program.
* **The simulated chip** has the lines 0..31. `Gpio.Sim.Wire(a, b)` / `Unwire(a, b)` connect two lines (what one drives, the other sees; two outputs that disagree: low wins), `Gpio.Sim.Drive(line, level)` / `Release(line)` drive a line from outside like a button or sensor
  (`Gpio.Sim.Level(line)` looks at the level), pull resistors work on inputs, `Gpio.Sim.Reset()` removes wires and drives. Edges and busy-ness behave as on the real thing.
* Errors are `Gpio.GpioException` (with a `code`): `NotFoundException` (3: no such chip or line), `BusyException` (4), `PermissionException` (5: on Linux the user must be in the group `gpio`) and `UnsupportedException` (6).
  Bad use (reading a pin that is not set up, writing an input, taking edges of an output) is code 1.
* Not tried on hardware yet: the Linux and ESP32 backends are written against the documented interfaces; the ESP32 file compiles against a stand-in of `driver/gpio.h`, the Linux one against the kernel headers.

## I2C (`#import "i2c"`, the package `fire-i2c`)

The I2C bus as a controller. The natives are `native/bridges/fire_bridge_i2c.hpp` over `plat::i2c` (`native/platform/std/fire_i2c_*.hpp`): **`/dev/i2c-N`** of Linux (the i2c-dev interface with the `I2C_RDWR` ioctl; used only when the kernel headers are
there, `FIRE_NO_I2C` switches it off), the **master driver of ESP-IDF** (`driver/i2c_master.h`, IDF 5.2+; add the component `esp_driver_i2c` or `driver`) on an ESP32, and a stub elsewhere (no buses). Every platform has the **simulated bus** `"sim"`.

```
#import "i2c"

var bus = new I2c.Bus(1)                          // "i2c-1"; a name works too: new I2c.Bus("i2c-1"), new I2c.Bus("sim"); the second argument is the speed in Hz (default 100000)
print(bus.Scan())                                 // a list of the addresses that answer (0x08..0x77)
var id = bus.ReadRegister(0x76, 0xD0)             // write the register number, read a byte back with a repeated start
bus.WriteRegister(0x76, 0xF4, 0x27)
var six = bus.ReadRegisters(0x76, 0xF7, 6)        // a byte[] from the register on
bus.Write(0x3C, buffer)                           // Write(address, buffer, offset = 0, count = all), WriteByte(address, value)
var reply = bus.Read(0x3C, 2)                     // ReadInto(address, buffer, offset, count) reads into a buffer
var r2 = bus.WriteRead(0x3C, command, 4)          // the general form: write, repeated start, read 4 bytes
```

* Addresses are the 7 bit ones (0..127); a transfer is at most 65535 bytes. It is done in the call (a transfer takes about a millisecond per few bytes at 100 kHz; the ESP32 gives up after 200 ms, `FIRE_I2C_TIMEOUT_MS`).
* `I2c.Board.Buses()` lists the buses (`"sim"` first, then `"i2c-1"`, ... on Linux, `"i2c-0"`, `"i2c-1"` on an ESP32); `I2c.Board.Available()` tells whether there is hardware. `Probe(address)` asks one address (a quick write on Linux, like `i2cdetect`; an address
  that a kernel driver owns counts as there).
* **Speed:** the ESP32 takes it from the constructor or the `Speed` property; on Linux the adapter's speed comes from the device tree (`dtparam=i2c_arm_baudrate=400000` on a Raspberry Pi) - the value is accepted and ignored.
* **Pins on the ESP32:** bus 0 uses GPIO 21 (SDA) and 22 (SCL), bus 1 uses 18 and 19; set `FIRE_I2C0_SDA`, `FIRE_I2C0_SCL`, `FIRE_I2C1_SDA`, `FIRE_I2C1_SCL` in the defines of the target to change them. The internal pull-ups are switched on (weak: use external ones for anything but short wires).
* **The simulated bus:** `I2c.Sim.Add(address)` puts a device on it with 256 registers (all 0) that behaves like a typical sensor chip - the first byte of a write is the register, more bytes are stored from there on (the register counts up), a read returns bytes from the current register on
  (counting up). `I2c.Sim.SetRegister(address, register, value)` changes what it "measures", `GetRegister` shows what the program wrote, `Remove(address)` / `Reset()` take devices off. An address without a device does not answer (`NoAckException`), as on a real bus.
* Errors are `I2c.I2cException` (with a `code`): `NotFoundException` (3: no such bus), `BusyException` (4), `PermissionException` (5: on Linux the user must be in the group `i2c`), `UnsupportedException` (6), `NoAckException` (8: nobody answered - no device, no power, wrong wiring) and
  `TimeoutException` (9). Bad arguments (address, buffer range) are code 1.
* Not tried on hardware yet: the Linux backend is written against the i2c-dev interface and compiles against the kernel headers; the ESP32 file compiles against a stand-in of the driver header (the mapping of the driver's error codes to NoAck is a best guess until it runs on a board).

## SPI (`#import "spi"`, the package `fire-spi`)

The SPI bus as a controller. The natives are `native/bridges/fire_bridge_spi.hpp` over `plat::spi` (`native/platform/std/fire_spi_*.hpp`): **`/dev/spidevB.C`** of Linux (the spidev interface with `SPI_IOC_MESSAGE`; used only when the kernel headers are there,
`FIRE_NO_SPI` switches it off), the **SPI master driver of ESP-IDF** (`driver/spi_master.h`; add the component `esp_driver_spi` or `driver`) on an ESP32, and a stub elsewhere (no devices). Every platform has the **simulated device** `"sim"`.
A *device* is a bus with one chip select (that is how the kernel sees it and what a chip on the bus needs).

```
#import "spi"

var chip = new Spi.Device("0.0", 0, 1000000)      // /dev/spidev0.0, mode 0, 1 MHz (Spi.Device(device, mode = 0, speed = 1000000, lsbFirst = false)); "spidev0.0", "spi-2", "sim" are names too
var back = chip.Transfer(bytes)                    // sends the bytes and receives as many at the same time (a byte[])
var id = chip.WriteRead(command, 3)                // sends the command, then clocks in 3 bytes - one transfer, the chip select stays low
chip.Write(data, offset, count)                    // what comes in is dropped
var data = chip.Read(16)                           // zeros are sent meanwhile
chip.TransferInto(out, 0, into, 0, count)          // the general form, with buffers and offsets
chip.Mode = 3                                      // clock polarity and phase; Speed (Hz) and LsbFirst can be changed too (or all at once with Configure)
```

* `Spi.Board.Devices()` lists the devices (`"sim"` first, then `"spidev0.0"`, ... on Linux, `"spi-2"`, `"spi-3"` on an ESP32); `Spi.Board.Available()` tells whether there is hardware. Mode is 0..3, the speed 1 Hz .. 80 MHz, the word is 8 bits.
* **Linux:** a device node per chip select; the kernel drives the chip select line and keeps it low during a transfer (longer transfers are cut into pieces of 4096 bytes). Enable the interface (`dtparam=spi=on` on a Raspberry Pi) and let the user into the group `spi`.
* **ESP32:** `"spi-2"` and `"spi-3"` are the general purpose controllers (SPI2_HOST/SPI3_HOST). The pins are the defaults of the classic ESP32 (host 2: MOSI 13, MISO 12, SCLK 14, CS 15; host 3: 23, 19, 18, 5); set `FIRE_SPI2_MOSI`, `_MISO`, `_SCLK`, `_CS` (and
  `FIRE_SPI3_...`) in the defines of the target to change them. Several devices on one bus differ in the chip select: `"spi-2.5"` is host 2 with GPIO 5 as chip select. The bus is set up when its first device opens and freed with the last. Transfers are polled and go
  through a DMA buffer in pieces of 2048 bytes.
* **The simulated device** is a loopback by default (MISO tied to MOSI: what is sent comes back - the usual wiring test). `Spi.Sim.Reply(bytes)` makes it a device that answers: one queued byte comes back per byte sent, starting with the first - so the answer to a command
  byte is in the bytes *after* it (queue a leading 0 for the command byte, as the data sheets draw it); when the queue is empty it answers 0. `Spi.Sim.Loopback()` turns the loopback on again. `Spi.Sim.Sent()` is everything the program sent (the first 65536 bytes),
  `Mode()`, `Speed()`, `LsbFirst()` and `Transfers()` say what a chip would have seen, `Clear()` forgets it.
* Errors are `Spi.SpiException` (with a `code`): `NotFoundException` (3), `BusyException` (4), `PermissionException` (5: on Linux the user must be in the group `spi`), `UnsupportedException` (6) and `TimeoutException` (8). Bad arguments (mode, speed, buffer range) are code 1.
* Not tried on hardware yet: the Linux backend compiles against the kernel headers; the ESP32 file compiles against a stand-in of the driver headers.

## WiFi (`#import "wifi"`, the package `fire-wifi`)

The radio of a board: scan for networks, join one as a **station**, be an **access point**. It needs `time`. The natives are `native/bridges/fire_bridge_wifi.hpp` over `plat::wifi` (`native/platform/std/fire_wifi_*.hpp`): the **WiFi driver of ESP-IDF** (`esp_wifi`, `esp_event`,
`esp_netif`, `nvs_flash`) on an ESP32. Where the operating system owns the network (Windows, Linux, macOS) there is no radio for a program to control: the list of interfaces is empty, and the package reports "not supported" (`WiFi.Board.Available()` is false). Every platform has the
**simulated radio** `"sim"`. Once the station has an address the lwIP sockets of the `net` package (and `http`, `tls`) work - **the network packages do not know about WiFi**; a program joins the network first and then opens its sockets. Ethernet on the ESP32 would follow the same pattern.

```
#import "wifi"

var wifi = new WiFi.Station()                               // the first real interface, or "sim" (a name works too: new WiFi.Station("wifi"))
foreach (var n in wifi.Scan()) { print(n.ssid + " " + n.rssi + " dBm, channel " + n.channel + ", secure: " + n.Secure) }     // strongest first
wifi.Connect("home", ReadPasswordFromSomewhere(), 20s)       // waits until joined (default limit 20 s); an empty password joins an open network
print(wifi.Ip + " " + wifi.Ssid + " " + wifi.Rssi)
var page = Http.Client().Get("http://example.org/").Text()   // the net package works now
wifi.Disconnect()

var ap = new WiFi.AccessPoint()
ap.Start("fire-board", "password123")                       // Start(ssid, password = "", channel = 1, maxClients = 4); no password: an open network
print(ap.Ip + " " + ap.Clients)                             // the stations get their addresses from the board (default 192.168.4.1/24)
ap.Stop()
```

* **Nothing waits inside the natives**: a scan or a join takes seconds on a real radio, so the fire code asks again and again (`ScanStep`, `ConnectState`) with a pause of 1 to 20 ms in between. The program stays abortable (`terminate`) and the other threads keep running. Limits are
  a `TimeSpan`, a time value (`500ms`, `5s`) or a number (milliseconds); an exceeded limit is `WiFi.TimeoutException`.
* `Station.Start(ssid, password)` joins without waiting; `State` (`WiFi.State.Idle/Connecting/Connected`) and `IsConnected` ask, `Poll()` calls `onState(state)` when the state has changed - for a program that does other things meanwhile. A failed join throws the exception of the failure
  from `State`/`Poll`.
* **Errors** are `WiFi.WiFiException` (with a `code`): `NotFoundException` (3: the network is not in range), `BusyException` (4: e.g. a scan while the station is joining), `UnsupportedException` (6), `AuthException` (8: the network refused the login - wrong password),
  `TimeoutException` (9) and `NotConnectedException` (10). Bad arguments (name or password length, channel) are code 1. Passwords are 8 to 63 characters (or empty). **Credentials are never part of a generated file**: pass them at run time (read them from a file or the program's settings).
* **ESP32 notes:** the first call starts the driver (NVS, `esp_netif`, the default event loop - if the program did that itself, it is not done twice) and the station and the access point share the one radio (the access point runs on the channel of the station when both are on). A join is tried
  up to 3 more times (`FIRE_WIFI_RETRIES`) when the radio fails for other reasons than a missing network or a wrong password. A scan lists up to 48 networks (`FIRE_WIFI_MAX_SCAN`). Add the components `esp_wifi`, `esp_event`, `esp_netif` and `nvs_flash` to the project.
  (Not tried on a board yet; the file compiles against a stand-in of the driver headers. The mapping of the driver's disconnect reasons to NotFound/Auth is from its documentation.)
* **The simulated radio** has the networks that `WiFi.Sim.AddNetwork(ssid, password = "", rssi = -60, channel = 1)` puts in the air; a scan finds them, a join works with the right password (`AuthException` otherwise, `NotFoundException` for an unknown name) and the station gets
  `192.168.1.50`. `WiFi.Sim.Delays(joinQuestions, scanQuestions)` sets how many questions an answer takes (default 2 and 1: the waiting code is really exercised), `Drop()` loses the connection, `RemoveNetwork(ssid)` takes a network away, `ClientJoins()`/`ClientLeaves()` let stations
  join the simulated access point (`192.168.4.1`), `Reset()` starts over. It is not a network: the sockets of the net package keep using the network of the PC.

## Hardware buses (the device platform)

The device platform of `devices` today knows serial ports and loopback devices. GPIO, I2C and SPI are added the same way: a driver per platform under `native/platform/std/fire_<bus>_*.hpp`
(`posix` uses what Linux offers **when it is there**, `esp32` the ESP-IDF drivers, the others the "not supported" stub) and **one package each**, so a program that only needs I2C does not carry SPI.
On Linux an unavailable bus is not an error of the program: the list of buses is just empty, `Open` of a missing one throws `DeviceException` (`code = NotFound`).

* **GPIO (`fire-gpio`):** built, see "GPIO" below.
* **I2C (`fire-i2c`):** built, see "I2C" below.
* **SPI (`fire-spi`):** built, see "SPI" below.
* **Common:** all three register their buses as **devices** in the device manager (`Device.Find("i2c:1")`, visible in the editor's Devices panel), take buffers from `byte[]` / `IO` memory streams, and are
  testable without hardware through a **loopback driver** (a fake I2C slave with a register file, SPI loopback with MISO tied to MOSI, GPIO pins that are wired in pairs) in the suite.

### Steps

4. **GPIO** - done: package `fire-gpio`, Linux + ESP32 + stub, simulated chip, tests, docs.
5. **I2C** - done: package `fire-i2c`, Linux + ESP32 + stub, simulated bus with register-file devices, tests, docs.
6. **SPI** - done: package `fire-spi`, Linux + ESP32 + stub, simulated loopback device, tests, docs.
7. **WiFi (ESP32)** - done: package `fire-wifi` (station, access point, scan), ESP-IDF backend, simulated radio, tests, docs; see "WiFi" below.
