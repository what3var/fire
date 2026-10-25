# Circuits (`#import "circuits"`)

Circuit simulation after the model of Paul Falstad's circuit simulator, written in fire. A circuit is made of **parts** with **ports**; connected ports form a
**node**. Every time step the circuit is solved (modified nodal analysis; capacitors and inductors with the trapezoidal rule), and every port knows the voltage of its
node, every part its current. Instruments record and measure what happens.

```fire
#import "circuits"

var c = new Circuits.Circuit("rc")
var source = c.Add(new Circuits.VoltageSource(5V))
var r = c.Add(new Circuits.Resistor(1kOhm))
var cap = c.Add(new Circuits.Capacitor(1uF))
source.A.Connect(r.A)
r.B.Connect(cap.A)
cap.B.Connect(c.Ground)
source.B.Connect(c.Ground)

c.TimeStep = 10us
var scope = new Circuits.Scope(c)
scope.Voltage("out", cap.A)
c.Run(5ms)
print(scope.Last("out"))        // 4.966...: five time constants
```

## Units

The package declares the units of electricity: `V`, `A`, `Ohm`, `F`, `H` and `Hz`, each with the prefixes of fire (`4.7kOhm`, `10uF` or `10µF`, `1mH`, `2kHz`,
`100mA`). `Ohm` is `V/A`, `F` is `A*s/V`, `H` is `V*s/A`. Every value may also be a plain number in SI units (`new Circuits.Resistor(220)`); a value of the wrong
kind is an `ArgumentException` (`new Circuits.Resistor(3V)`). Properties give their values with units (`r.Resistance` is `1000Ohm`, `port.Voltage` is `5V`); the
methods ending in `Raw` give plain numbers (volts, amperes, seconds).

## Ports and nodes

| | |
|---|---|
| `port.Connect(other)` | a wire: both ports are one node from now on (returns `port`) |
| `port.IsConnected(other)` | are they on the same node? |
| `port.Voltage` / `port.Raw()` | the voltage of its node against ground after the last step |
| `part.A`, `part.B` | the two ports of a two-terminal part (`A` is the positive side) |
| `part.PortAt("a")` / `part.PortAt(0)`, `part.Ports()` | a port by name or number, all ports |
| `circuit.Ground` | the ground (0 V); nested circuits share the ground of the outermost one |

## Parts

| Part | |
|---|---|
| `Resistor(r)` | `Resistance` |
| `Capacitor(c, initial = 0)` | `Capacitance`; `initial` is its voltage at time 0 |
| `Inductor(l, initial = 0)` | `Inductance`; `initial` is its current at time 0 |
| `VoltageSource(v)` | DC; `Level`. `A` is the positive pole, `Current` the current it drives out of `A` |
| `SineSource(amplitude, frequency, offset = 0, phase = 0)` | `offset + amplitude * sin(2 pi f t + phase)` |
| `SquareSource(high, frequency, low = 0, duty = 0.5)` | high for `duty` of every period |
| `CurrentSource(i)` | drives `i` from `A` through itself to `B`; `Level` |
| `Switch(closed = false)` | `Closed`, `Toggle()`; closed it is a wire (1 µOhm), open it is not there |
| `Ground()` | ties its port `G` to ground |

Every two-terminal part has `Voltage` (from `A` to `B`), `Current` (from `A` through the part to `B`) and `PowerRaw()`.

## Circuits

| | |
|---|---|
| `new Circuits.Circuit(name)` | an empty circuit |
| `Add(part)` | the part (or a nested circuit) belongs to the circuit from now on; returns it |
| `AddPort(name)` | a port of the circuit itself: the circuit can be used as a part of a larger one |
| `TimeStep` | the step of the simulation (default `10us`) |
| `Step()`, `Run(duration)` | one step; steps for `duration` (`Run(20ms)`) |
| `Time`, `Reset()` | the time of the simulation; back to time 0 (the parts take their initial state) |
| `Parts()` | the parts and nested circuits |

A nested circuit is a part: give it ports with `AddPort`, connect them inside, and connect them outside like the ports of any part. Stepping a nested circuit steps the
outermost one.

```fire
var outer = new Circuits.Circuit("outer")
var divider = outer.Add(new Circuits.Circuit("divider"))
var input = divider.AddPort("in")
var output = divider.AddPort("out")
var ra = divider.Add(new Circuits.Resistor(1kOhm))
var rb = divider.Add(new Circuits.Resistor(1kOhm))
input.Connect(ra.A)
ra.B.Connect(rb.A)
rb.B.Connect(divider.Ground)
output.Connect(ra.B)
var battery = outer.Add(new Circuits.VoltageSource(10V))
battery.A.Connect(input)
battery.B.Connect(outer.Ground)
outer.Step()
print(output.Voltage)           // 5V
```

Changing a value (`r.Resistance = 2kOhm`), switching, connecting or adding parts makes the circuit build its equations again on the next step. Without a change
they are solved with the same factorization every step.

## Instruments

**`Scope(circuit, capacity = 100000)`** records every step of the circuit:

| | |
|---|---|
| `Voltage(name, port, reference = undefined)` | a channel for the voltage of a port (against ground or `reference`) |
| `Current(name, part)` | a channel for the current through a part |
| `Times()`, `Samples(name)` | the recorded times (seconds) and samples (volts, amperes) as `float[]` |
| `Last(name)`, `Count`, `Clear()` | the last sample, the number of samples, start again |

**`Multimeter`**: `Multimeter.Volts(a, b = undefined)` (between two ports), `Multimeter.Amps(part)`, `Multimeter.Rms(samples)`.

**`Fft`**: `Fft.Magnitudes(samples)` gives the amplitudes of the frequencies 0 to half the sample rate (the samples are padded with zeros to a power of two; bin `k`
is `k * sampleRate / n`). `Fft.Dominant(samples, sampleRate)` is the strongest frequency other than DC:

```fire
var lc = new Circuits.Circuit()
var l = lc.Add(new Circuits.Inductor(1mH))
var cap = lc.Add(new Circuits.Capacitor(1uF, 1V))
l.A.Connect(cap.A)
l.B.Connect(lc.Ground)
cap.B.Connect(lc.Ground)
lc.TimeStep = 1us
var scope = new Circuits.Scope(lc)
scope.Voltage("v", cap.A)
lc.Run(10ms)
print(Circuits.Fft.Dominant(scope.Samples("v"), 1MHz))   // about 5kHz
```

## Parts of your own

Derive from `Circuits.TwoTerminal` (or `Circuits.Part` with `AddPort`) and put the part into the equations with the methods of `Circuits.Solver`:

| Method of the part | When | Solver calls |
|---|---|---|
| `Stamp(solver)` | when the equations are built | `Conductance(p, q, g)`, `VoltageBranch(k, plus, minus)` |
| `StampStep(solver)` | every step | `CurrentInto(port, amps)`, `BranchValue(k, volts)` |
| `Commit(solver)` | after every step | `BranchCurrent(k)`; `solver.time`, `solver.dt` |
| `Branches()` | | the number of extra unknowns (1 for a voltage source; its index is `branch`) |
| `Reset()`, `CurrentRaw()` | | back to time 0; the current of the part |

```fire
/// A lamp: a resistor that glows above 1 W.
class Lamp : Circuits.TwoTerminal {
    construct() : base("lamp") { }
    undefined Stamp(Circuits.Solver solver) { solver.Conductance(this.A, this.B, 1.0 / 50) }
    float CurrentRaw() { return this.VoltageRaw() / 50 }
    bool Glows() { return this.PowerRaw() > 1 }
}
```

## Accuracy

The trapezoidal rule follows oscillations well; its error shrinks with the square of the time step. A source that jumps at time 0 starts from a resting circuit, so the
first step of an RC circuit is a little behind the exact curve (`5 * (1 - e^-1)` is 3.161, the simulation gives 3.151 with 100 steps). A node without a path to ground
(it hangs in the air, or only current sources and open switches reach it) gets a tiny conductance to ground (1e-12 S), so that it does not stop the simulation. A loop of voltage sources cannot be solved
(`ArgumentException`).

## Coming next

Diodes, transistors and other non-linear parts (Newton iteration), binding parameters, parts attached to the devices of the program (`Attached`), simulated
devices, a circuit control for the UI library and a circuit editor in the IDE with the import of Falstad files.
