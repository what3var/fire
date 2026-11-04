# GPIO (`#import "gpio"`)


Digital pins. It needs `time` (the clock of the time limits). The natives are C (`packages/gpio/native/fire_bridge_gpio.h` over `fire_gpio_*` of `fire_gpio.h`, see `native/platform/std/fire_gpio_c.h`; the implementations are `native/platform/std/fire_gpio_*.h`): the **GPIO character device** of Linux
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
  (`Gpio.Sim.Level(line)` looks at the level, `Gpio.Sim.Output(line)` what the program drives: 1/0, or -1 while the line is no output of it), pull resistors work on inputs, `Gpio.Sim.Reset()` removes wires and drives. Edges and busy-ness behave as on the real thing.
  A circuit of the package `circuits` can sit on the lines (`Circuits.GpioLine`, docs/circuits/README.md).
* Errors are `Gpio.GpioException` (with a `code`): `NotFoundException` (3: no such chip or line), `BusyException` (4), `PermissionException` (5: on Linux the user must be in the group `gpio`) and `UnsupportedException` (6).
  Bad use (reading a pin that is not set up, writing an input, taking edges of an output) is code 1.
* Not tried on hardware yet: the Linux and ESP32 backends are written against the documented interfaces; the ESP32 file compiles against a stand-in of `driver/gpio.h`, the Linux one against the kernel headers.

## On the PC: the simulated chip

```fire
#import "gpio"

Gpio.Sim.Wire(4, 5)                                     // two lines of the simulated chip, connected
var led = new Gpio.Pin("sim", 4).Output()
var sense = new Gpio.Pin("sim", 5).Input(Gpio.Pull.None, Gpio.Edge.Both)
led.Write(true)
print(sense.Read())                                     // True
print(sense.TakeEdge() == Gpio.Edge.Rising)             // True
led.Toggle()
print(sense.Read())                                     // False

var button = new Gpio.Pin("sim", 7).Input(Gpio.Pull.Up, Gpio.Edge.Falling)
Gpio.Sim.Drive(7, false)                                // someone presses the button
print(button.WaitEdge(1s) == Gpio.Edge.Falling)         // True
```
