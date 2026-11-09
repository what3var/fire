# I2C (`#import "i2c"`)


The I2C bus as a controller. The natives are C (`packages/i2c/native/fire_bridge_i2c.h` over `fire_i2c_*` of the platform files of the package, see `packages/i2c/native/fire_i2c_c.h`; the implementations are `packages/i2c/native/fire_i2c_*.h`): **`/dev/i2c-N`** of Linux (the i2c-dev interface with the `I2C_RDWR` ioctl; used only when the kernel headers are
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
  Converters of a circuit can sit on the bus (`Circuits.I2cAdc`/`I2cDac`, [Circuits](../circuits/README.md)).
* Errors are `I2c.I2cException` (with a `code`): `NotFoundException` (3: no such bus), `BusyException` (4), `PermissionException` (5: on Linux the user must be in the group `i2c`), `UnsupportedException` (6), `NoAckException` (8: nobody answered - no device, no power, wrong wiring) and
  `TimeoutException` (9). Bad arguments (address, buffer range) are code 1.
* Not tried on hardware yet: the Linux backend is written against the i2c-dev interface and compiles against the kernel headers; the ESP32 file compiles against a stand-in of the driver header (the mapping of the driver's error codes to NoAck is a best guess until it runs on a board).

## On the PC: the simulated bus

```fire
#import "i2c"

I2c.Sim.Add(0x76)                                       // a simulated sensor with 256 registers
I2c.Sim.SetRegister(0x76, 0xD0, 0x60)                   // its chip id
var bus = new I2c.Bus("sim")
foreach (a in bus.Scan()) { print("device at " + a) }   // device at 118
print(bus.ReadRegister(0x76, 0xD0))                     // 96
bus.WriteRegister(0x76, 0xF4, 0x27)
print(I2c.Sim.GetRegister(0x76, 0xF4))                  // 39
try {
    bus.ReadRegister(0x50, 0)
} catch (I2c.NoAckException e) {
    print("nobody at 0x50")
}
```

**Byte buffers:** the methods that take `byte[]` need a byte buffer (`new byte[n]`, `"text".ToBytes()`, `IO.Utf8.GetBytes(text)`, what `Read` and
the other methods return). An array literal (`[1, 2, 3]`) or a declared `byte a[] = [...]` is not a buffer - passing one is a known bug that can
crash the program; see the [roadmap](../ROADMAP.md).
