# Devices (`#import "devices"`)

The device framework is written in fire. Every device is a `Device`, and every device comes from a `Driver`. `DeviceManager` holds the drivers of the program and
finds their devices by identifier. The serial port of a printer, the GPIO port of a board, an I2C sensor and an SPI display all work the same way: `Read()`,
`Write(data)`, `Connect()`/`Disconnect()`. On top of these come text (`ReadString`, `WriteString`), commands (`DoCommand`) and waiting for data (`WaitFor`,
`WaitForString`).

```fire
#import "devices"
#import "gpio"
#import "i2c"

DeviceManager.AddDriver(new SerialDriver())     // serial ports + the loopback device "loopback:echo"
DeviceManager.AddDriver(new GpioDriver())       // GPIO ports: gpio:gpioa, gpio:gpiob, ... and gpio:gpiosim
DeviceManager.AddDriver(new I2cDriver())        // I2C buses: i2c:i2c-1, ..., i2c:sim

foreach (d in DeviceManager.Devices()) { print(d.Identifier()) }

var port = DeviceManager.Get("gpioa")           // a bare name works when only one driver has it
port.Write(0b1010)                              // lines 1 and 3 high, the others low (all as outputs)
var sensor = DeviceManager.Get("i2c:i2c-1/0x48") // a device at an address of a bus
print(sensor.ReadRegister(0))
```

## Devices

| | |
|---|---|
| `Identifier()` | `driver:name` (`serial:COM3`, `gpio:gpioa`, `i2c:i2c-1/0x48`, `spi:spidev0.0`) |
| `Name` | the name within the driver |
| `Read()` / `Write(data)` | the raw data: a byte buffer, or the levels of a GPIO port as an `int`. If the device cannot do it, it throws `NotSupportedException` |
| `ReadString()` / `WriteString(text)` | text, one character per byte (Latin1) |
| `DoCommand(command)` / `DoCommands(list)` | a text goes out as a line. A command object (`Command<IDevice>`, or anything with `Execute(device)`) is executed |
| `WaitFor(bytes, timeout)` / `WaitForString(text, timeout)` | waits until the data arrives and cuts what was received behind it (what came after stays) |
| `HasData()` | is received data waiting? |
| `Connect()`, `Disconnect()`, `IsConnected`, `EnsureConnected()` | the connection (a GPIO port is always connected) |
| `IsShared`, `PortName()`, `Availability()`, `TestAvailability()` | as before (docs/SPEC.md 8.16) |

## The drivers of the standard libraries

| Driver | Import | Devices |
|---|---|---|
| `SerialDriver` | `devices` | the serial ports of the platform and `loopback:echo`. In the editor these are the editor's devices: they stay connected after the run and the editor traces their packets. Without any `AddDriver` the program gets this driver the first time it asks. |
| `GpioDriver(width = 32)` | `gpio` | a port per GPIO chip: `gpioa`, `gpiob`, ... in the order of `Gpio.Board.Chips()`. `gpiosim` is the simulated chip. `Read()` gives all lines as an `int` (bit n = line n) and `Write(int)` sets them. A port that cannot be read or written as a whole (a missing or busy line) throws `NotSupportedException`. `PinAt(n)` gives one line as a `Gpio.Pin`. |
| `I2cDriver(speed = 100000)` | `i2c` | the buses (`i2c-1`, `sim`). A bus has no data of its own, so its `Read`/`Write` are `NotSupported`. `bus.At(0x48)` or `DeviceManager.Get("i2c:i2c-1/0x48")` gives an `I2c.Target`, whose `Read()` reads `ReadSize` bytes and whose `Read(n)` reads n. It also has `Write(bytes)`, `WriteRead(bytes, n)`, `ReadRegister`, `WriteRegister`. |
| `SpiDriver()` | `spi` | the SPI devices (`spidev0.0`, `sim`) as `SpiDevice`. `Read()`/`Read(n)` read while sending zeros, and there are `Write(bytes)`, `Transfer(bytes)`, `WriteRead(bytes, n)`. `Mode`, `Speed` and `LsbFirst` can be set before the device is opened on first use. |

## Drivers of your own

```fire
class Thermometer : Device {
    construct(Driver driver) : base(driver, "thermo0") { }
    any Read() { return 21 }          // ask the hardware here
}
class ThermometerDriver : Driver {
    construct() : base("thermo") { }
    undefined Refresh(bool fastScan = true) {
        if (this.Find("thermo0") == undefined) { this.Add(new Thermometer(this)) }
    }
}
DeviceManager.AddDriver(new ThermometerDriver())
print(DeviceManager.Get("thermo:thermo0").Read())
```

`Refresh` adds the devices it finds with `Add`. `Find(name)` may make a device on demand: `I2cDriver` does this for `bus/0x48`. `Get` asks `Find` when the
identifier starts with the driver's name.

## The host

`DeviceManager.Host` (a `DeviceHost`) is what the program's host offers:

- `DefaultIdentifier()`: the default device the editor chose. In a native build it comes from `FIRE_DEFAULT_DEVICE`.
- `Trace(device, sent, data)`: the packets of the GPIO, I2C and SPI devices. The editor traces its serial devices itself.
- `DevicesChanged(list)`: the device list after `AddDriver`/`Refresh`.

`Device.Default` and `DeviceManager.Default` give the device with the default identifier. A program (or a test) may set a host of its own:
`DeviceManager.Host = new MyHost()`.

## Simulations that run alongside

`Simulation` is something simulated that runs next to the program - a circuit at the simulated GPIO chip, I2C bus and SPI device (`circuit.RunAlongside()`,
docs/CIRCUITS.md). `Simulation.Start(s)` makes an object of a class derived from it the one that runs, `Simulation.Stop()` ends it, `Simulation.Current()` gives it.
The simulated devices call `Simulation.Touch()` before every access of the program; it calls `Advance()` of the running simulation, which catches up with the time.
