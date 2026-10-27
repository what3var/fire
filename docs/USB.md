# USB devices (`#import "usb"`)

The package `fire-usb` lets a program talk to USB devices directly - without a kernel driver of its own: control transfers on
endpoint 0, bulk and interrupt transfers, and the bulk endpoints of an interface as an `IO.Stream`. It is a standard package
(`packages/usb`); its natives are one C++ file (`native/fire_bridge_usb.hpp`) that the VM builds into a library and the native
backend compiles into the program.

| Platform | Backend | Notes |
|---|---|---|
| Linux, macOS (`posix`) | libusb-1.0, loaded at run time (`libusb-1.0.so.0` / `.dylib`) | no headers needed to build; without the library only `"sim"` is there |
| Windows (`windows`) | WinUSB (`winusb.dll`, `setupapi.dll`, loaded at run time) | the device must use the WinUSB driver (see below) |
| ESP32, FreeRTOS, Wasm | none | only `"sim"`; `Usb.Bus.Available()` is `false` |

Every platform has the simulated device **`"sim"`**, so programs and tests run without hardware.

## A first program

```fire
#import "usb"
#import "io"

foreach (var d in Usb.Bus.Devices()) { print(d.ToString()) }

var dev = new Usb.Device(0x1209, 0x0001)          // vendor and product id (or an id: "sim", "001:004", a Windows device path)
foreach (var e in dev.Endpoints()) { print(e.ToString()) }

var stream = dev.OpenStream()                      // claims interface 0, its bulk OUT and IN endpoints as a stream
stream.WriteText("hello")
var buffer = new byte[64]
int n = stream.Read(buffer, 0, 64)                 // what came (at least one byte; waits up to stream.ReadTimeout)
print("got " + n + " bytes")
dev.Close()                                        // also done by the destructor
```

## API

### `Usb.Bus`

| Member | |
|---|---|
| `static bool Available()` | libusb / WinUSB is there |
| `static List Devices()` | `Usb.DeviceInfo` of every device, `"sim"` first |
| `static List Find(int vendorId, int productId, string serialNumber = "")` | the matching devices |

`Usb.DeviceInfo`: `Id`, `VendorId`, `ProductId`, `DeviceClass`, `Manufacturer`, `Product`, `SerialNumber`, `Bus`, `Address`
(the strings are empty when the device cannot be opened to read them, e.g. without permission).

### `Usb.Device`

| Member | |
|---|---|
| `new Usb.Device(id)` / `new Usb.Device(vendorId, productId, serial = "")` | opens the device (`NotFoundException`, `PermissionException`, `BusyException`) |
| `List Endpoints()` | `Usb.Endpoint`: `Interface`, `AlternateSetting`, `Address`, `Type` (`Usb.TransferType`), `MaxPacketSize`, `InterfaceClass`, `IsIn` |
| `ClaimInterface(n = 0)`, `ReleaseInterface(n = 0)`, `SetAlternateSetting(itf, alt)` | an interface must be claimed before its endpoints are used; on Linux a kernel driver that holds it is detached meanwhile |
| `int Control(requestType, request, value, index, byte data[], offset = 0, length = -1, timeout = 1000)` | a control transfer; bit 7 of `requestType` gives the direction |
| `byte[] ControlIn(requestType, request, value, index, length, timeout = 1000)` | reads up to `length` bytes |
| `int ControlOut(requestType, request, value, index, byte data[] = undefined, timeout = 1000)` | sends `data` |
| `int BulkRead(ep, byte buffer[], offset, count, timeout = undefined)` / `byte[] BulkRead(ep, count = 512, timeout = undefined)` | what came in the first packet(s) |
| `int BulkWrite(ep, byte buffer[], offset = 0, count = -1, timeout = undefined)` | sends everything |
| `InterruptRead` / `InterruptWrite` | the same for interrupt endpoints |
| `ClearHalt(ep)` | a stalled endpoint goes on |
| `UsbStream OpenStream(itf = 0)` | claims the interface; its first bulk OUT / IN endpoints as a stream |
| `UsbStream EndpointStream(outEp, inEp, maxPacketSize = 512)` | two given endpoints (`-1`: none); the interface must be claimed |
| `Close()` | releases the claimed interfaces and closes the device |

`Usb.Request` has the usual request types: `StandardIn`/`StandardOut`, `ClassIn`/`ClassOut`, `VendorIn`/`VendorOut`.

**Timeouts** are milliseconds (an `int`) or a value with a time unit (`250ms`, `2s`); `undefined` means no limit. Transfers run in
slices of 20 ms, so a long wait never blocks the VM in one native call: other threads, the debugger and Stop keep working.
A transfer that does not finish in time throws `Usb.TimeoutException`; a write that timed out after some bytes counts what was sent.

### `Usb.UsbStream : IO.Stream`

`Read(buffer, offset, count)` returns what came (at least one byte, or throws `TimeoutException` after `ReadTimeout`);
it reads whole packets into an internal buffer, so asking for fewer bytes than a packet loses nothing. `Write`, `WriteText`,
`Available`, `ReadTimeout`, `WriteTimeout`, `Close` as for other streams - the stream fits everywhere an `IO.Stream` does.

### Errors

`Usb.UsbException` (with `code`) and its subclasses: `NotFoundException` (3), `BusyException` (4), `PermissionException` (5),
`UnsupportedException` (6), `TimeoutException` (8), `StallException` (9), `DisconnectedException` (10). Other codes: 1 invalid
argument, 2 invalid handle (closed device), 7 other, 11 overflow (the device sent more than the buffer holds).

## In the device framework

`#import "usb"` also brings `UsbDriver` (docs/DEVICES.md): `DeviceManager.AddDriver(new UsbDriver())` lists every device of the bus as `usb:<id>`, a `Usb.Port`
whose `Read`/`Write` go to the bulk endpoints of its first interface that has some. The package names the driver in its project (`"drivers": ["UsbDriver"]`), so the device
manager of spark lists the USB devices too, and its terminal talks to them.

## The simulated device `"sim"`

Vendor `0x1209`, product `0x0001`, one interface (class `0xFF`) with:

- bulk OUT `0x01` -> bulk IN `0x81`: a loopback (64 byte packets; reading an empty queue times out),
- interrupt IN `0x82`: one byte, a counter that goes up with each read,
- vendor request `1`: `ControlOut(Usb.Request.VendorOut, 1, ...)` stores up to 64 bytes, `ControlIn(Usb.Request.VendorIn, 1, ...)` returns them,
- `GET_DESCRIPTOR` (device) returns 18 bytes; every other request stalls.

`Usb.Sim.Info(which)` (0 bytes written, 1 bytes read, 2 control transfers, 3 bytes queued, 4 interface 0 claimed) and
`Usb.Sim.Clear()` are for tests. The package tests (`forge test packages/usb/usb.fireproj`) run against it.

## Access to real devices

**Linux**: libusb needs write access to `/dev/bus/usb/BBB/AAA`. Either run as root (not recommended) or add a udev rule, e.g.
`/etc/udev/rules.d/50-mydevice.rules`:

```
SUBSYSTEM=="usb", ATTR{idVendor}=="1209", ATTR{idProduct}=="0001", MODE="0660", TAG+="uaccess"
```

then `sudo udevadm control --reload && sudo udevadm trigger`. Install libusb with the distribution (`libusb-1.0-0`).

**macOS**: install libusb (`brew install libusb`). Devices that a system driver holds (HID keyboards, mass storage) cannot be claimed.

**Windows**: WinUSB talks only to devices whose driver is `WinUSB`. Devices with a Microsoft OS descriptor get it automatically;
for others install it once with [Zadig](https://zadig.akeo.ie/) (choose the device, then "WinUSB"). The device id is the device
interface path; `Usb.Bus.Find(vid, pid)` is the easy way to get it. A composite device lists its interfaces as associated
interfaces of the first one, which `ClaimInterface(n)` opens.

## Status

The simulated device is tested in the suite (VM). The libusb backend was built and loaded, but tried without real hardware
(the build machine has no USB bus); the WinUSB backend is compile-checked with MinGW. Isochronous transfers and hotplug events
are not there yet.
