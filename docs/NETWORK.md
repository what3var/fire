# Networking and hardware buses - design and steps

Status: **plan** (nothing of this is built yet). The steps are also kept as tasks (Net 1-3, GPIO, I2C, SPI, WiFi). Everything is a **package** (`ember`, docs/PACKAGES.md): the compiler and the
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
  waiting. Waiting is **not deaf** like `Sleep`: in short slices, so `terminate`, threads and the main queue keep working (THREADING_DESIGN.md section 7). No hidden background threads on small targets.
* **Errors:** `NetException` with a `code` (`Refused`, `TimedOut`, `Unreachable`, `AddressInUse`, `Closed`, `Unsupported`, ...), never a crash; an error text from the OS goes into `message`.
* **Addresses:** `NetAddress` (IPv4 and IPv6, `Parse("10.0.0.5:80")`, `Dns.Resolve("example.org")` returns a list), and `"host:port"` strings accepted where an address is expected.
* **Policy:** like `IoPolicy` for files, the host can restrict the network (allow nothing, allow loopback only, an allow list of hosts/ports); the editor and `forge` pass it. Default for a
  program started by hand: everything allowed; `DenyAll` for scripts that are only run from a package source.
* **Tests:** loopback only (a listener and a client in the same program, in threads) - no real network in the test suite. UDP: loopback datagrams. DNS: `localhost` and literal addresses.

### Steps

1. **Net 1 - design and HAL.** This document made concrete: `plat::net` for posix (BSD sockets) and windows (Winsock), the stub for platforms without a network, the bridge with the handle table, the
   error codes and the timeouts, the policy hook, the package skeleton `fire-net` (prelude + natives list), `ember` entry. Test: handle table, errors, policy.
2. **Net 2 - TCP, UDP, DNS.** `TcpClient`, `TcpListener`, `UdpSocket`, `Dns`; stream integration; loopback tests in the VM and the native build (the `natCases` comparison); `docs/NETWORK.md` becomes the
   reference. FreeRTOS/ESP32 build of `plat::net` over lwIP (`sockets.h` is BSD compatible: most of the posix file is shared).
3. **Net 3 - HTTP and TLS.** `fire-http`: a small HTTP/1.1 client (`Get`, `Post`, headers, chunked, redirects) and server (route lambdas) as a separate package on top of `net`. TLS (mbedTLS, which ESP-IDF and
   most Linux systems have) as its own step and package `fire-tls` that wraps a `TcpClient`; certificate handling decided there.

## Hardware buses (the device platform)

The device platform of `devices` today knows serial ports and loopback devices. GPIO, I2C and SPI are added the same way: a driver per platform under `native/platform/std/fire_<bus>_*.hpp`
(`posix` uses what Linux offers **when it is there**, `esp32` the ESP-IDF drivers, the others the "not supported" stub) and **one package each**, so a program that only needs I2C does not carry SPI.
On Linux an unavailable bus is not an error of the program: the list of buses is just empty, `Open` of a missing one throws `DeviceException` (`code = NotFound`).

* **GPIO (`fire-gpio`):** `Gpio.Pin(n)` / `Gpio.Pin("chip0", n)`; `Mode(Input | Output, Pull)`, `Read()`, `Write(value)`, `Toggle()`; edges: `OnChange(Rising | Falling | Both, lambda)` or polled
  `TakeEdge()` (the usual fire pair of lambda and `Take...`). Linux: the character device `/dev/gpiochipN` (libgpiod v2 ioctl interface, fallback sysfs for old kernels); ESP32: `driver/gpio`
  with an ISR that only sets a flag (the lambda runs on the main queue).
* **I2C (`fire-i2c`):** `I2c.Open(bus)`; `Write(address, buffer)`, `Read(address, count)`, `WriteRead(address, out, count)`, `Scan()`; speed and pins configurable (`FIRE_I2C<n>_SDA` ... like the UART
  pins). Linux: `/dev/i2c-N` with the `I2C_RDWR` ioctl; ESP32: the new `i2c_master` driver.
* **SPI (`fire-spi`):** `Spi.Open(bus, chipSelect)`, `Mode(0..3)`, `Speed(hz)`, `Transfer(out)` (full duplex, returns the received bytes), `Write`, `Read`, `BitOrder`. Linux: `/dev/spidevB.C`
  (`SPI_IOC_MESSAGE`); ESP32: `spi_master` with a device handle per chip select.
* **Common:** all three register their buses as **devices** in the device manager (`Device.Find("i2c:1")`, visible in the editor's Devices panel), take buffers from `byte[]` / `IO` memory streams, and are
  testable without hardware through a **loopback driver** (a fake I2C slave with a register file, SPI loopback with MISO tied to MOSI, GPIO pins that are wired in pairs) in the suite.

### Steps

4. **GPIO** - package, Linux + ESP32 + stub, loopback pair driver, tests, docs.
5. **I2C** - package, Linux + ESP32 + stub, fake slave, tests, docs.
6. **SPI** - package, Linux + ESP32 + stub, MISO/MOSI loopback, tests, docs.
7. **WiFi (ESP32)** - a second step after `net`, through the device platform: `fire-wifi` brings a `WiFi` device (`Scan()`, `Connect(ssid, password)`, `StartAccessPoint(...)`, state changes as device
   events); once it is connected, `plat::net` (lwIP) already works - the network package never knows about WiFi. On Linux/Windows the package reports "not supported" (the OS owns the network); an
   Ethernet interface on the ESP32 later follows the same pattern. Credentials are never part of the generated source files (a runtime call or a settings file of the target).
