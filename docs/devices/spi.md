# SPI (`#import "spi"`)


The SPI bus as a controller. The natives are C (`packages/spi/native/fire_bridge_spi.h` over `fire_spi_*` of the platform files of the package, see `packages/spi/native/fire_spi_c.h`; the implementations are `packages/spi/native/fire_spi_*.h`): **`/dev/spidevB.C`** of Linux (the spidev interface with `SPI_IOC_MESSAGE`; used only when the kernel headers are there,
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
  `Mode()`, `Speed()`, `LsbFirst()` and `Transfers()` say what a chip would have seen, `LastSent()` and `TotalSent()` the last byte and how many were sent since the start, `Clear()` forgets it.
  `Spi.Sim.SetReply(bytes)` is `Reply` that drops the queued answers first. Shift registers of a circuit can sit on the device (`Circuits.SpiShiftOut`/`SpiShiftIn`, [Circuits](../circuits/README.md)).
* Errors are `Spi.SpiException` (with a `code`): `NotFoundException` (3), `BusyException` (4), `PermissionException` (5: on Linux the user must be in the group `spi`), `UnsupportedException` (6) and `TimeoutException` (8). Bad arguments (mode, speed, buffer range) are code 1.
* Not tried on hardware yet: the Linux backend compiles against the kernel headers; the ESP32 file compiles against a stand-in of the driver headers.

## On the PC: the simulated device

```fire
#import "spi"

var chip = new Spi.Device("sim", 0, 1000000)            // the simulated device: a loopback at first
var back = chip.Transfer("abc".ToBytes())
print(back.ToString())                                  // abc
var answer = new byte[4]                                // a flash chip that answers its id after the command byte
answer[1] = 0xEF
answer[2] = 0x40
answer[3] = 0x18
Spi.Sim.Reply(answer)
var command = new byte[1]
command[0] = 0x9F
var id = chip.WriteRead(command, 3)
print(id[0] + " " + id[1] + " " + id[2])                // 239 64 24
```

**Byte buffers:** the methods that take `byte[]` need a byte buffer (`new byte[n]`, `"text".ToBytes()`, `IO.Utf8.GetBytes(text)`, what `Read` and
the other methods return). An array literal (`[1, 2, 3]`) or a declared `byte a[] = [...]` is not a buffer - passing one is a known bug that can
crash the program; see the [roadmap](../ROADMAP.md).
