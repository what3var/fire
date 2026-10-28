# Circuits (`#import "circuits"`)

Circuit simulation after the model of Paul Falstad's circuit simulator. A circuit is made of **parts** with **ports**; connected ports form a **node**. Every
time step the circuit is solved (modified nodal analysis; capacitors and inductors with the trapezoidal rule), and every port knows the voltage of its node, every
part its current. Instruments record and measure what happens.

The circuits and parts are written in fire; the steps run in a C++ kernel of the package. The first step after a change **compiles** the circuit into a plan for
the kernel (see [Speed](#speed)): in the virtual machine a step of a circuit with 40 unknowns takes about 2 µs instead of more than a millisecond.

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
| `TriangleSource(amplitude, frequency, offset = 0, phase = 0, sawtooth = false)` | a triangle or a sawtooth between offset - amplitude and offset + amplitude |
| `NoiseSource(amplitude = 5V, offset = 0)` | a new random voltage every step |
| `SweepSource(amplitude = 5V, minFrequency = 20Hz, maxFrequency = 4kHz, sweepTime = 0.1s, logarithmic = false, bidirectional = true)` | a sine whose frequency sweeps up (and down again) |
| `FunctionSource(t => volts)` | any function of the time (seconds); it is computed in fire every step |
| `CurrentSource(i)` | drives `i` from `A` through itself to `B`; `Level` |
| `Switch(closed = false)` | `Closed`, `Toggle()`; closed it is a wire (1 µOhm), open it is not there |
| `Ground()` | ties its port `G` to ground |
| `Switch2(position = 0, throws = 2, centerOff = false)` | ports `Common`, `Throw(i)`; `Position`, `Toggle()`; a center-off switch has a middle position that connects nothing |
| `AnalogSwitch(threshold = 2.5V, inverting = false, onResistance = 20, offResistance = 10G)` | ports `A`, `B`, `Control` (takes no current); `IsOn`. `AnalogSwitch2(threshold, ...)`: `Common` on `NormallyOpen` above the threshold, on `NormallyClosed` below |
| `Lamp(nominalPower = 100W, nominalVoltage = 120V, warmTime = 0.4s, coolTime = 0.4s)` | the filament heats with the power it takes, its resistance grows with the temperature (cold about a twentieth); `Temperature` (K), `Brightness` |
| `Fuse(resistance = 61.3mOhm, i2t = 6.73)` | blows when the heat i²t of its current reaches `i2t` (it gives it off in 3 s); `Blown`, `Heat`, `Repair()` |
| `Memristor(onResistance = 100, offResistance = 16k, width = 10nm, mobility = 1e-10)` | HP's model: the resistance moves between the two with the charge through it; `Resistance` |
| `SparkGap(breakdown = 1kV, holdCurrent = 1mA, onResistance = 1k, offResistance = 1G)` | fires above `breakdown`, goes out below `holdCurrent`; `IsOn` |
| `LightResistor(lux = 100, resistance10 = 10k, gamma = 0.7)` | an LDR: `resistance10` at 10 lux, falling as lux^-gamma; `Lux` |
| `Thermistor(temperature = 25, resistance25 = 10k, beta = 3950)` | an NTC at `Temperature` (°C) |
| `TransmissionLine(impedance = 75, delay = 1us)` | ports `A1`, `B1` (one end), `A2`, `B2` (the other); a lossless line: a wave arrives after `delay`, an unmatched end reflects it |
| `Potentiometer(r, position = 0.5)` | ports `A`, `B`, `Wiper`; `Resistance`, `Position` (0 at `A`, 1 at `B`) |
| `Transformer(inductance = 4H, ratio = 1, coupling = 0.999)` | ports `P1`, `P2` (primary), `S1`, `S2` (secondary); two coupled inductors, the secondary has `ratio`² times the inductance of the primary; `SecondaryCurrent` |

Every two-terminal part has `Voltage` (from `A` to `B`), `Current` (from `A` through the part to `B`) and `PowerRaw()`.

## Semiconductors, op amps and logic

These parts are not linear: every step solves the circuit with Newton's method in the kernel (the junction voltages limited like SPICE does, the
factorization run again with the same list of operations each iteration). A step whose Newton's method does not converge (a negative resistance
such as the one of a UJT at its peak point, a tunnel diode, a diac) is done again from its start in steps of half the time step - halved again
where one does not converge, down to 1/64, and doubled again after one that did. Scopes and recordings keep the time step of the circuit; a circuit
with a transmission line keeps its steps (the line needs whole steps).

| Part | Ports | |
|---|---|---|
| `Diode(model = "diode")` | `A` anode, `B` cathode | the Shockley equation with emission coefficient and breakdown |
| `Zener(voltage = 5.6V)` | `A`, `B` | a diode that conducts backwards above `voltage` |
| `Led(color = "red", ratedCurrent = 20mA)` | `A`, `B` | `Brightness` (0 to 1 at the rated current), `IsOn` |
| `Transistor(model = "npn")` | `C`, `B`, `E` | bipolar (Ebers-Moll): `CollectorCurrent`, `BaseCurrent`, `EmitterCurrent` |
| `Mosfet(model = "nmos")` | `D`, `G`, `S` | level 1 (Shichman-Hodges), drain and source swap when the voltage turns: `DrainCurrent` |
| `OpAmp(model = "opamp")` | `Plus`, `Minus`, `Out` | gain * (plus - minus), limited smoothly to the output range of the model, through its output resistance |
| `Gate(kind, inputs = 2, supply = 5V)` | `In(i)`, `Out` | `"AND"`, `"OR"`, `"XOR"`, `"NAND"`, `"NOR"`, `"XNOR"`, `"NOT"`, `"BUFFER"`; `State` |
| `FlipFlop(supply = 5V)` | `D`, `Clock`, `Q`, `NotQ` | takes D on the rising edge of the clock |
| `LogicInput(high = false, supply = 5V)` | `A`, `B` | a source of 0 V or `supply`: `High`, `Toggle()` |
| `Comparator(supply = 5V)` | `Plus`, `Minus`, `Out` | high while Plus is above Minus (analog inputs, logic output) |
| `SRLatch(supply = 5V, withReset = false)` | `S`, `R`, `Q`, `NotQ`, `Reset` | S sets, R clears (R wins), Reset low clears |
| `JKFlipFlop(supply = 5V, fallingEdge = false, withReset = false)` | `J`, `Clock`, `K`, `Q`, `NotQ`, `Reset` | on the clock edge: J sets, K clears, both toggle; Reset high clears |
| `SchmittTrigger(lower = 1.66V, upper = 3.33V, supply = 5V, inverting = false)` | `In`, `Out` | goes high above `upper` and low again below `lower` (or the other way round when inverting) |
| `Jfet(pChannel = false, threshold = -4V, beta = 1.25m)` | `D`, `G`, `S` | a depletion MOSFET with the gate junction (Falstad's JFET); `DrainCurrent` |
| `Darlington(model = "npn")` | `C`, `B`, `E` | two transistors of the model, the first one's emitter on the second one's base |
| `Thyristor(triggerCurrent = 10mA, holdingCurrent = 8.2mA, gateResistance = 50)` | `Anode`, `Gate`, `Cathode` | fires when the gate current reaches `triggerCurrent`, conducts (a diode drop) until the current falls below `holdingCurrent`; `IsOn` |
| `Triac(triggerCurrent = 10mA, holdingCurrent = 8.2mA, gateResistance = 100)` | `MT1`, `MT2`, `Gate` | the same in both directions, the gate against MT1 |
| `Diac(breakdown = 30V, holdCurrent = 10mA, onResistance = 500, offResistance = 100M)` | `A`, `B` | conducts both ways above `breakdown` until the current falls below `holdCurrent` |
| `Varactor(capacitance = 4pF)` | `A`, `B` | a diode whose junction capacitance falls with the reverse voltage |
| `TunnelDiode()` | `A`, `B` | Falstad's model: 4.7 mA at 0.1 V, a valley near 0.37 V (negative resistance between) |
| `Unijunction()` | `E`, `B1`, `B2` | Falstad's 2N2646: the emitter diode, its current makes the lower base region conduct (relaxation oscillators) |
| `Triode(model = "12AX7", parameters = "")` | `Plate`, `Grid`, `Cathode` | a vacuum triode, see [Vacuum tubes](#vacuum-tubes) |
| `Pentode(model = "EL34", parameters = "")` | `Plate`, `Screen`, `Grid`, `Cathode` | a pentode or beam tetrode |
| `VacuumDiode(model = "EZ81", parameters = "")` | `Plate`, `Cathode` | a vacuum rectifier or signal diode |
| `VCVS(gain = 1)`, `VCCS(gain = 1mS)` | `A`, `B`, `InPlus`, `InMinus` | V(A) - V(B) = gain · (V(InPlus) - V(InMinus)); or that many amperes from A through the source to B |
| `CCCS(gain = 1)`, `CCVS(gain = 1kOhm)` | `A`, `B`, `InPlus`, `InMinus` | controlled by the current from InPlus to InMinus (a short between them) |
| `CurrentConveyor(gain = 1)` | `X`, `Y`, `Z` | CCII: X follows Y (Y takes no current), the current into Z is `gain` (+1 or -1) times the current into X |
| `Ota(positive = 9V, negative = -9V)` | `InPlus`, `InMinus`, `Bias`, `Iabc`, `Out` | Falstad's transistor OTA (LM13700 style): output current about Iabc / (2 Vt) · (V+ - V-) |
| `Optocoupler(ctr = 1)` | `Anode`, `Cathode`, `Collector`, `Emitter` | an LED whose current drives a phototransistor (collector current about `ctr` times the LED current) |

The logic parts read their inputs after every step (high above half the supply) and drive their outputs in the next one through a small resistance
(10 Ohm), like the gates of Falstad's simulator: a gate delays by one time step.

```fire
#import "circuits"

var c = new Circuits.Circuit()
var vcc = c.Add(new Circuits.VoltageSource(5V))
var rb = c.Add(new Circuits.Resistor(10kOhm))
var led = c.Add(new Circuits.Led("green"))
var rc = c.Add(new Circuits.Resistor(220))
var q = c.Add(new Circuits.Transistor("2N3904"))
vcc.A.Connect(rb.A)
rb.B.Connect(q.B)
vcc.A.Connect(rc.A)
rc.B.Connect(led.A)
led.B.Connect(q.C)
q.E.Connect(c.Ground)
vcc.B.Connect(c.Ground)
c.Step()
print(led.IsOn)                     // True: the transistor switches the LED on
```

### Models

A semiconductor or op amp takes a **model** - the numbers of a kind of part, like a `.model` line of SPICE: a name of the library or a
`Circuits.Model`. `Circuits.Models.Names(kind)` lists them, `Circuits.Models.Add(model)` adds one of your own.

| Kind | Numbers (p0, p1, p2, p3) | In the library |
|---|---|---|
| `"D"` | IS, N, BV, IBV | `diode`, `1N4148`, `1N4001`, `1N5817`, `zener`, `1N4733` (5.1 V), `1N4742` (12 V), `led`/`red`, `yellow`, `green`, `blue`, `white` |
| `"NPN"`, `"PNP"` | IS, BF, BR | `npn`, `pnp`, `2N3904`, `2N3906`, `BC547`, `BC557`, `2N2222` |
| `"NMOS"`, `"PMOS"` | VTO, KP, LAMBDA | `nmos`, `pmos`, `2N7000`, `BS250`, `IRF540`, `IRF9540` |
| `"OPAMP"` | GAIN, VMIN, VMAX, ROUT | `opamp` (+-15 V), `LM358` (0 to 3.5 V), `TL072`, `LM741` |

```fire
#import "circuits"

Circuits.Models.Add(new Circuits.Model("BAT54", "D", 0.0000002, 1.0, 30.0))
var d = new Circuits.Diode("BAT54")
var fast = Circuits.Models.Get("1N4148").With("1N4148-hot", 0, 0.000000005)   // a copy with another IS
print(Circuits.Models.Names("NPN").count)
```

### Parts given by a formula

`Formula(current, parameters)` is a part with two terminals whose current (from A through the part to B) is a formula of `V` = V(A) - V(B);
`Formula3(current1, current2, parameters)` has three terminals X, Y, Ref with the currents into X and Y as formulas of `V1` = V(X) - V(Ref) and
`V2` = V(Y) - V(Ref). The kernel compiles a formula once and derives it by itself (dual numbers): it runs as fast as a part of the package, the same
in the virtual machine and in a native build.

| In a formula | |
|---|---|
| names | the parameters (`"Is=1e-12 n=1.5"`, also with prefixes: `"R=4.7k"`), `V` or `V1`/`V2`, `t` (the time), `Vt` (thermal voltage), `pi`, `e` |
| operators | `+ - * / ^` (power, right-associative), parentheses |
| functions | `exp`, `log`, `sqrt`, `abs`, `sin`, `cos`, `tanh`, `atan`, `min`, `max`, `pow`, `if(c, a, b)` (a when c > 0), `limit(x, lo, hi)` |

```fire
#import "circuits"

var c = new Circuits.Circuit()
var source = c.Add(new Circuits.VoltageSource(5V))
var r = c.Add(new Circuits.Resistor(1kOhm))
var diode = c.Add(new Circuits.Formula("Is*(exp(V/(n*Vt))-1)", "Is=1e-14 n=1"))
source.A.Connect(r.A)
r.B.Connect(diode.A)
diode.B.Connect(c.Ground)
source.B.Connect(c.Ground)
c.Step()
print(diode.VoltageRaw())          // 0.693 - the same as Circuits.Diode()
diode.Set("n=2")                   // other parameters: the circuit compiles again
```

A formula can also be a **model**: `Circuits.Model.Formula(name, current, parameters)` and `Circuits.Model.Formula3(...)`, kind `"FORMULA"` and
`"FORMULA3"`. `new Circuits.Formula("tunnel", "Ip=8m")` takes the model and changes some of its parameters; `model.Evaluate(v)` gives the value
of its formula, to look at its curve. The library has `tunnel` (a tunnel diode with its negative resistance), `varistor` and `lamp` (a filament
that grows hotter with its voltage).

A mistake in a formula is an `ArgumentException` when the part is made: `')' expected after the arguments of exp (at 19 of '...')`.

### Vacuum tubes

Triodes and pentodes follow Norman Koren's models (as in SPICE), vacuum diodes the law of Child and Langmuir (`Ip = k * Vpk^1.5`); the kernel simulates
them like the semiconductors, with the capacitances between their electrodes. A tube takes a model of the library or a `Model` of its kind, and parameters
of its own that change some of the model's (`"kg1=1100"`, also with prefixes: `"ccg=2.3p"`):

| kind | models | parameters |
|---|---|---|
| `TRIODE` (`Triode`) | 12AX7, 12AT7, 12AU7, 6DJ8, 300B; `falstad` (the simpler tube of Falstad's simulator: `mu`, `kg1`) | `mu ex kg1 kp kvb rgi`, capacitances `ccg cgp ccp` |
| `PENTODE` (`Pentode`) | EL34, 6L6GC, KT88, EL84, 6V6 (the suppressor grid is joined to the cathode) | `mu ex kg1 kg2 kp kvb rgi`, capacitances `cin cgp cout` |
| `VDIODE` (`VacuumDiode`) | 5Y3, 5AR4, EZ81 (rectifiers), 6AL5 (signal diode) | `k ex`, capacitance `cpk` |

Triode: `E1 = Vpk/kp * ln(1 + exp(kp * (1/mu + Vgk/sqrt(kvb + Vpk^2))))`, `Ip = 2 * E1^ex / kg1`. Pentode: `E1 = Vg2k/kp * ln(1 + exp(kp * (1/mu +
Vg1k/Vg2k)))`, `Ip = 2 * E1^ex / kg1 * atan(Vpk/kvb)`, the screen current `(Vg1k + Vg2k/mu)^ex / kg2`. A positive grid draws current through `rgi`.
A 12AX7 with 100 kOhm on the plate at 250 V and 1.5 kOhm on the cathode sits at 170 V and 0.8 mA; an EL34 at 250 V with -13.5 V on the grid draws 97 mA.
The heater is not simulated. `Circuits.Model.Tube(name, kind, parameters)` makes a model of your own (`Circuits.Models.Add` puts it into the library).

```fire
#import "circuits"

var c = new Circuits.Circuit()
var t = c.Add(new Circuits.Triode("12AT7"))
var p = c.Add(new Circuits.Pentode("EL84", "kg1=650"))
Circuits.Models.Add(Circuits.Model.Tube("my6SN7", "TRIODE", "mu=20 ex=1.4 kg1=1260 kp=50 kvb=300 rgi=2000"))
print(t.PlateCurrent)
```

In the schematic the tubes are in the category **Tubes**: `VacuumDiode` (plate at the first point), `Triode` (grid at the first point, plate 4 units to the
side of the second, cathode on the other side) and `Pentode` (as a triode, the screen grid 4 units beyond the second point); each with `model` (a list of
the library) and `parameters` (text, as above).

### Capacitances

The models also carry the capacitances of the parts, by name (`model.Set("CJO", 4pF)`, `model.Param("CJO")`):

| Kind | Numbers | |
|---|---|---|
| `"D"` | `CJO`, `VJ`, `M`, `TT` | the depletion capacitance of the junction (SPICE, FC = 0.5) and the diffusion charge TT * current: the reverse recovery |
| `"NPN"`, `"PNP"` | `CJE`, `CJC`, `VJ`, `M`, `TF`, `TR` | base-emitter and base-collector junction; TF the forward, TR the reverse transit time (the storage time when a saturated transistor turns off) |
| `"NMOS"`, `"PMOS"` | `CGS`, `CGD` | gate-source and gate-drain (linear) |

The parts of the library have the values of their SPICE models (a 1N4001 recovers in about 3 µs, a 1N4148 in about 10 ns, a 2N3904 in saturation
needs about half a microsecond to turn off). The charges are integrated with backward Euler: a stored charge leaves without ringing. An effect shorter
than the time step is not seen - a 1N4148 needs a step of a few nanoseconds to show its recovery.

The models of the library are approximations from the data sheets and the usual SPICE models, good for the behaviour of a circuit - not for the
details of a data sheet (no temperature).

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
| `IsCompiled` | is there a plan for the circuit as it is now? |
| `PlanInfo(what)` | numbers of the plan: 0 unknowns, 1 entries of the factorized matrix, 2 update operations of a factorization, 3 capacitors and inductors, 4 sources, 5 parts that step in fire, 6 parts that are not linear, 7 Newton iterations, 8 steps that did not converge (also at 1/64), 9 steps done again in smaller steps |

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

Changing a value (`r.Resistance = 2kOhm`), switching, connecting or adding parts makes the circuit compile again on the next step; the capacitors keep their charge,
the inductors their current and the scopes their samples. The level of a source (`Level`, `Amplitude`, `Frequency`) changes without a new plan.

## Parameters and subcircuits

A circuit can have **parameters**: `Parameter("R", 2kOhm)` sets one (in SI numbers), `Get("R")` reads it, `Value("1/(2*pi*R*C)")` computes an
expression of them (with the functions of formulas). A nested circuit sees the parameters of the circuits around it, unless it has its own of the
same name. `Bind(c => ...)` runs now and every time a parameter changes - it sets the parts from the parameters:

```fire
#import "circuits"

class LowPass : Circuits.Circuit {
    construct(float r : any, float cap : any) : base("lowpass") {
        this.Parameter("R", r)
        this.Parameter("C", cap)
        var input = this.AddPort("in")
        var output = this.AddPort("out")
        var res = this.Add(new Circuits.Resistor(1))
        var c = this.Add(new Circuits.Capacitor(1))
        input.Connect(res.A)
        res.B.Connect(c.A)
        c.B.Connect(this.Ground)
        output.Connect(res.B)
        this.Bind((Circuits.Circuit p) => res.Resistance = p.Get("R"))
        this.Bind((Circuits.Circuit p) => c.Capacitance = p.Get("C"))
    }
    float Cutoff() { return this.Value("1/(2*pi*R*C)") }
}
var filter = new LowPass(1kOhm, 1uF)
print(Math.Round(filter.Cutoff()))     // 159
filter.Parameter("R", 2kOhm)           // the resistor follows
print(Math.Round(filter.Cutoff()))     // 80
```

### ICs

ICs are circuits of the package made of its parts - subcircuits to use like any part:

| IC | Ports | |
|---|---|---|
| `Timer555(supply = 5V, vccPin = false)` | `Trigger`, `Threshold`, `Discharge`, `Output`, `Control`, `Reset` (`Vcc`) | divider, two comparators, SR latch, discharge transistor; powered inside at `supply`, or with `vccPin` at the port `Vcc` (the output swings up to it) |
| `Counter(bits = 4, supply = 5V)` | `Clock`, `Q(i)` | a ripple counter of D flip-flops; `Count` |
| `Crystal(seriesCapacitance = 0.1pF, parallelCapacitance = 28.7pF, inductance = 2.5mH, resistance = 6.4)` | `A`, `B` | a quartz: series R-L-C with the holder's capacitance in parallel (about 10 MHz) |
| `Relay(inductance = 0.2H, coilResistance = 20, onCurrent = 20mA, offCurrent = 15mA, onResistance = 0.05)` | `Coil1`, `Coil2`, `Common`, `NormallyClosed`, `NormallyOpen` | a coil (inductor and resistor) and a changeover contact: it pulls in when the coil current reaches `onCurrent` and drops out below `offCurrent`; `Energized`, `CoilCurrent` |

The logic chips of Falstad's simulator run inside the kernel as one part each (`Circuits.Chip`): every time step the chip reads its inputs
(high above half of `supply`), works out its outputs and drives them through 10 Ohm, like the gates. Their ports are named as the pins in Falstad's chip
layout (`D0`, `Q0`, `Clock`, ...), the state survives a recompile of the circuit:

| Chip | |
|---|---|
| `TFlipFlop(supply = 5V, withReset = false, withSet = false)` | a high T toggles Q on the rising clock edge; `State` |
| `Latch(bits = 4, supply = 5V, levelTriggered = false, withReset = false, withSet = false, resetActiveLow = false)` | D0.. go to Q0.. on the rising edge of Load (while it is high with `levelTriggered`); `Number` |
| `RingCounter(bits = 10, supply = 5V, clockInhibit = true, resetActiveHigh = false)` | like a 4017: one of Q0.. is high, the next on every clock edge; `Count` |
| `Multiplexer(selectBits = 2, supply = 5V, invertedOutput = false, strobe = false)` | Q is the input I(S) |
| `Demultiplexer(selectBits = 2, supply = 5V, invertedOutputs = false)` | the input goes to Q(S) |
| `HalfAdder(supply = 5V)`, `FullAdder(bits = 1, supply = 5V)` | S = A + B (+ Cin), C the carry; `Number` |
| `SipoShift(bits = 8, supply = 5V)`, `PisoShift(bits = 8, supply = 5V, serialIn = false)` | shift registers, serial in/parallel out and parallel in/serial out |
| `Adc(bits = 8, supply = 5V)`, `Dac(bits = 8, supply = 5V)` | D = (2^bits - 1) In / V+ (rounded down), O = V+ D / (2^bits - 1) |
| `SevenSegmentDecoder(supply = 5V, blankInput = false, blankOnF = false)` | the segments a..g of the hex digit I3..I0 |
| `SevenSegment(point = false, supply = 5V)` | a display: segment lit while its pin is high, draws no current; `Digit` |
| `Monostable(time = 10ms, retriggerable = false, invertTrigger = false, supply = 5V)` | an edge at the input makes Q high for `time` |
| `PhaseComparator(supply = 5V)` | like the type II of a 4046: an edge at I1 drives O high, one at I2 low, after both O is open |
| `Vco(supply = 5V)` | like the VCO of a 4046: the frequency grows with Vi, set by the capacitor between C1/C2 and the resistors at R1/R2 |
| `TriState(supply = 5V, onResistance = 0.1, offResistance = 10 GOhm, offGroundResistance = 0)` | Out follows In while Control is high, else it is open |

A full adder of 4 bits gives 9 + 7 + 1 = 17, an ADC of 8 bits reads 3.3 V at 5 V as 168, and a PLL of the VCO, the phase comparator and an RC filter locks onto
60 Hz.

A 555 as an astable multivibrator with RA = 1 kOhm, RB = 10 kOhm and C = 100 nF runs at 666 Hz (the formula 1.44 / ((RA + 2 RB) C) gives 686 Hz:
the comparators react one time step late and the discharge transistor has a resistance), the capacitor swings between 1/3 and 2/3 of the supply.

## Sound through a circuit

`AudioInput(amplitude = 1V)` is the input of sound: a voltage source that `ProcessAudio` drives with the samples (full scale is `amplitude`).
`circuit.ProcessAudio(sound, input, output, reference = undefined, outputScale = amplitude, oversample = 4)` sends an `Audio.Sound` of
`#import "audio"` (or any object with Data, Offset, Frames, Channels and Rate) through the circuit and returns the samples at `output` - mono,
16 bit, full scale at `outputScale`. The time step becomes 1 / (rate * oversample); the kernel does the whole sound in one call (a circuit with
parts in fire goes step by step). `Clipped` says how many samples were louder than full scale. `ProcessSamples(input, output, data, offset,
frames, channels, rate, ...)` takes raw 16 bit samples; the circuit keeps its state, so a long sound - or a stream - can go through in pieces.

```fire
#import "circuits"
#import "audio"

var tone = Audio.Sound.Tone(1000, 100, 0, 100, 22050)        // or Audio.Sound.FromWav(bytes of a .wav file)
var c = new Circuits.Circuit()
var input = c.Add(new Circuits.AudioInput(3V))
var r = c.Add(new Circuits.Resistor(1kOhm))
var d1 = c.Add(new Circuits.Diode("1N4148"))
var d2 = c.Add(new Circuits.Diode("1N4148"))
input.A.Connect(r.A)
input.B.Connect(c.Ground)
r.B.Connect(d1.A)
d1.B.Connect(c.Ground)
d2.A.Connect(c.Ground)
d2.B.Connect(r.B)
var samples = c.ProcessAudio(tone, input, r.B, undefined, 1V)   // a diode clipper: distortion
var result = new Audio.Sound(tone.Rate, 1, samples)
print(result.ToWav().length)                                  // the bytes of a .wav file (write them with #import "filesystem")
// result.Open().Play(result) plays it on the sound device
```

`Audio.Sound.ToWav()` (in `#import "audio"`) gives the bytes of a WAV file of a sound.

## Instruments

**`Scope(circuit, capacity = 100000, rolling = false)`** records every step of the circuit (it stops when `capacity` samples are there; a `rolling` scope
drops the older half then and goes on - the scopes of a schematic roll with 8192 samples):

| | |
|---|---|
| `Voltage(name, port, reference = undefined)` | a channel for the voltage of a port (against ground or `reference`) |
| `Current(name, part)` | a channel for the current through a part |
| `Times()`, `Samples(name)` | the recorded times (seconds) and samples (volts, amperes) as `float[]` |
| `Last(name)`, `Count`, `Clear()` | the last sample, the number of samples, start again |
| `Trace(name, columns, span = 0, trigger = 0, level = 0, times = "")` | a channel over `columns` columns of a screen, computed natively: the last samples, or `span` seconds - from the latest crossing of `level` (`trigger` 1 rising, 2 falling); `times` multiplies another channel in (power); `[first column with data, values...]` |
| `Pairs(x, y, count = 2000)` | the last pairs of two channels for an XY display: `[x0, y0, x1, y1, ...]` |
| `Stats(name, times = "")` | measurements of a channel over what the scope holds, computed natively: `[count, min, max, average, rms, frequency in Hz (0: none found), duration in s]`; average and rms over whole periods (between the first and the last rising crossing of the middle, with a hysteresis of a tenth of peak to peak) |
| `Spectrum(name, size = 512, fmax = 20kHz)` | the spectrum of a channel, computed natively: `size` samples taken at the rate `2 * fmax` (fmax 0: the rate of the simulation), mean removed, Hann window; `[bin width in Hz, amplitude of bin 1, ..., of bin size/2]` |

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
| `Stamp(solver)` | when the circuit is compiled | `Conductance(p, q, g)`, `VoltageBranch(k, plus, minus)` |
| `StampStep(solver)` | every step | `CurrentInto(port, amps)`, `BranchValue(k, volts)` |
| `Commit(solver)` | after every step | `BranchCurrent(k)`, `NodeVoltage(node)`; `solver.time`, `solver.dt` |
| `Branches()` | | the number of extra unknowns (1 for a voltage source; its index is `branch`) |
| `Reset()`, `CurrentRaw()` | | back to time 0; the current of the part |

A part of your own steps in fire: while a circuit has one with `StampStep` or `Commit`, every step goes through fire (still with the solving in the kernel). A part
that only has `Stamp` - like the lamp below - costs nothing after the first step: the circuit notices that it has nothing to do in a step and lets the kernel run
alone again. The same holds for instruments of your own (any object with `Record(circuit)` given to `Attach`) and for scope channels of the current of such a part.

```fire
/// A lamp: a resistor that glows above 1 W.
class Lamp : Circuits.TwoTerminal {
    construct() : base("lamp") { }
    undefined Stamp(Circuits.Solver solver) { solver.Conductance(this.A, this.B, 1.0 / 50) }
    float CurrentRaw() { return this.VoltageRaw() / 50 }
    bool Glows() { return this.PowerRaw() > 1 }
}
```

## Parts in fire (`Attached`)

`Circuits.Attached` is a part made of fire code instead of a circuit: a **logical part** with pins. Every step it reads the voltages of its pins and drives the pins it
wants to, through `outputResistance` like the output of a chip. With it a program puts its own logic into a circuit - a controller, a sensor model, a part of a protocol -
and the parts at the simulated devices below are attached parts too.

| | |
|---|---|
| `Attached(name, supply = 5V, outputResistance = 10 Ohm)` | the base class; `AddPin(name)` adds a pin (an input until it is driven) |
| `Update(time)` | override it: called every step before the circuit is solved (`time` in seconds at the end of the step; the voltages are those of the step before) |
| `VoltsAt(pin)`, `IsHigh(pin)` | the voltage of a pin, and whether it is above half the supply |
| `Drive(pin, volts)`, `DriveHigh(pin, high)`, `Release(pin)` | drive a pin (0 V to the supply, or any voltage), or let go of it again; `IsDriven(pin)`, `CurrentAt(pin)` |
| `Pin(name or number)`, `PinCount`, `Supply` | |

```fire
#import "circuits"

/// A part in fire: drives its pin with a square wave of 1 kHz.
class Blinker : Circuits.Attached {
    construct() : base("blink") { this.AddPin("out") }
    undefined Update(float time) { this.DriveHigh(0, (Math.Floor(time * 2000)!int) % 2 == 0) }
}

var c = new Circuits.Circuit()
var b = c.Add(new Blinker())
var r = c.Add(new Circuits.Resistor(1kOhm))
b.Pin(0).Connect(r.A)
r.B.Connect(c.Ground)
c.Run(0.1ms)
print(r.A.Voltage)      // 4.95 V: 5 V through the 10 Ohm of the output
```

Changing the level of a driven pin costs nothing; starting or stopping to drive a pin compiles the circuit again after the step. A circuit with attached parts steps in
fire (see [Speed](#speed)).

## Parts at the simulated devices

The GPIO, I2C and SPI packages have a simulated device each (the chip `"sim"`, the bus `"sim"`, the device `"sim"`; docs/DEVICES.md). These parts connect a circuit to
them: a program that uses the simulated devices - directly or through the device framework (`gpio:gpiosim`, `i2c:sim`, `spi:sim`) - talks to the circuit, without a
change in the program when it later runs on the board.

| Part | Pins | |
|---|---|---|
| `GpioLine(line, supply = 3.3V, outputResistance = 25 Ohm)` | `P` | a line of the simulated GPIO chip: while the program has the line as an output, the pin drives the circuit with 0 V or the supply; otherwise the line follows the circuit (high above half the supply) - a button, a sensor, a signal for the program |
| `I2cAdc(address = 0x48, channels = 4, reference = 5V, bits = 8)` | `In(k)` | an analog-to-digital converter on the simulated I2C bus: register k is the value of channel k (0 V is 0, `reference` the largest number); with more than 8 bits registers 2k (high byte) and 2k+1 (low byte) |
| `I2cDac(address = 0x60, channels = 1, reference = 5V)` | `Out(k)` | a digital-to-analog converter: what the program writes into register k comes out as k / 255 of `reference` |
| `SpiShiftOut(supply = 5V)` | `Q(0..7)` | a shift register with outputs (like a 74HC595): the last byte the program sent over SPI |
| `SpiShiftIn(supply = 5V)` | `D(0..7)` | a shift register with inputs (like a 74HC165): the program reads the levels as the first byte of every transfer |

```fire
#import "circuits"

var c = new Circuits.Circuit()
var line = c.Add(new Circuits.GpioLine(3))          // GPIO line 3 of the chip "sim"
var r = c.Add(new Circuits.Resistor(100))
var led = c.Add(new Circuits.Led("red"))
line.P.Connect(r.A)
r.B.Connect(led.A)
led.B.Connect(c.Ground)

var pin = new Gpio.Pin("sim", 3)                    // the program: an output
pin.Output(true)
c.Run(1ms)
print(led.IsOn)                                     // True
```

The schematic has them as elements too (the category Devices of the palette): `GpioLine` (`line`, `supply`), `I2cAdc` (`address` like `0x48`, `channels`,
`reference`, `bits`), `I2cDac`, `SpiShiftOut` and `SpiShiftIn`.

### Real time

`circuit.RealTime = true` lets the simulation keep pace with the clock - never faster than its `Speed`. `circuit.Speed` is the pace: simulated seconds per
second of the clock, 1 (the default) for real time, 0.01 for a hundred times slower (slow motion, to watch a circuit of 1 kHz), 10 for ten times faster (as far
as the computer keeps up); between 0.0001 and 1000. The `.biscuit` file keeps it (`"speed": "0.01"`, left out at 1). `circuit.AdvanceRealTime(budget = 20ms)` simulates the time since its last
call (the first call starts the clock), using at most `budget` of computing time; `circuit.RunRealTime(duration)` does that for `duration` of the clock and
returns. The circuit simulates with its own `TimeStep` as long as the computer keeps up. When it falls behind for a while, the step doubles (up to
`MaxTimeStep`, by default 64 times `TimeStep`); with time to spare it halves again. `CurrentTimeStep` is the step it uses now, `RealTimeShare` how much of real
time it manages at the chosen speed (1 = all of it, 0.5 = half of it). `TimeStep` and the saved file keep the step the circuit was given; `RealTime = false` goes back
to it. `RestartRealTime()` starts the clock anew after a pause, so the pause is not made up.

```fire
var c = new Circuits.Circuit()
c.TimeStep = 10us
c.MaxTimeStep = 200us
// ... the parts ...
c.RunRealTime(2s)
print(c.Time)                  // about 2 s, if the computer kept up
print(c.RealTimeShare)         // 1.0 if it did
```

A `UI.CircuitView` always simulates that way, at the speed of its circuit, and draws at most 30 times a second (in spark: the list of speeds in the tool
strip Simulation - faster, **Real time**, slow motion down to 1/10000; the choice is saved with the circuit, with undo). The corner shows the speed, how much of
it the simulation manages and the step it takes for that (`1/100 real time 100%  (step 10us)`; yellow below 99 %).

### A circuit that runs alongside the program

`circuit.RunAlongside(speed = 1.0, maxLag = 0.1s)` lets the circuit run **in real time** next to the program: every time the program uses one of the simulated devices,
the circuit is first simulated up to now. The program then sees the circuit as it would see the hardware - a pin it sets rises through an RC filter as fast as it would
on the board. A circuit that cannot keep up skips what it is behind by more than `maxLag`. `StopAlongside()` ends it, `IsAlongside` tells. A time step of 100 µs or
more keeps a circuit with attached parts in real time in the virtual machine (a step there takes some 20 to 50 µs).

```fire
var c = new Circuits.Circuit()
c.TimeStep = 100us
var out = c.Add(new Circuits.GpioLine(5))
var back = c.Add(new Circuits.GpioLine(6))
var r = c.Add(new Circuits.Resistor(10kOhm))
var cap = c.Add(new Circuits.Capacitor(1uF))
out.P.Connect(r.A)
r.B.Connect(cap.A)
cap.B.Connect(c.Ground)
cap.A.Connect(back.P)
c.RunAlongside()

var o = new Gpio.Pin("sim", 5)
var i = new Gpio.Pin("sim", 6)
o.Output(true)
while (!i.Read()) { Sleep(1) }      // about 7 ms later: RC * ln 2
```

This is the class `Simulation` of the package `devices`: `Simulation.Start(s)` makes any object of a class derived from it (with `Advance()`) the simulation that runs
alongside the program; the simulated GPIO chip, I2C bus and SPI device call `Simulation.Touch()` before every access.

## Speed

The first step after a change compiles the circuit:

1. **Plan.** Every node and branch gets its number; the parts of the package give their tables to the kernel (resistors and switches their conductance, capacitors
   and inductors their companion conductance and state, sources their curve) instead of being called every step.
2. **Sparse factorization.** The kernel decides the order of the pivots once and writes the elimination as a list of operations on the entries that are not zero
   (including the ones that fill in). A step only walks that list: a chain of 200 resistors and capacitors (202 unknowns) has 802 entries instead of 40804.
3. **Many steps in one call.** Without parts or instruments in fire, `Run(5ms)` is one call of the kernel; the scopes record into buffers of the kernel.

A chain of 20 RC stages (41 unknowns), 5000 steps: 11 ms in the virtual machine (6.2 s before the kernel), 1.8 ms in a native build (166 ms before).

## Accuracy

The trapezoidal rule follows oscillations well; its error shrinks with the square of the time step. A source that jumps at time 0 starts from a resting circuit, so the
first step of an RC circuit is a little behind the exact curve (`5 * (1 - e^-1)` is 3.161, the simulation gives 3.151 with 100 steps). A node without a path to ground
(it hangs in the air, or only current sources and open switches reach it) gets a tiny conductance to ground (1e-12 S), so that it does not stop the simulation. A loop of voltage sources cannot be solved
(`ArgumentException`).

A switch that opens while a current flows through a capacitor makes the trapezoidal rule take half a step of that current along (the voltage moves by
`dt / 2C * i` once) - the same as in other simulators that use it.

What is much faster than the time step, the trapezoidal rule does not damp: a capacitor shorted by a switch (or an inductor left open) would keep
a current (a voltage) that changes its sign every step and never dies away. A capacitor current (an inductor voltage) that has changed its sign two steps in
a row is therefore replaced by the mean of the last two - the ringing is gone within three steps, and a signal the time step resolves is not touched.

## Schematics (`.biscuit`)

A circuit can also be **drawn**: a schematic is a list of elements on a grid, each between two points (the way Falstad's simulator places them). Pins at the same
point are connected, a wire connects its two ends, labels of the same text are one node. The schematic makes the parts of its circuit and connects their ports; every
change is applied at once.

```fire
#import "circuits"

var c = new Circuits.Circuit("rc")
var s = c.Schematic
s.Add("Source", 0, 8, 0, 0, "voltage=5V")               // from (0,8) [-] to (0,0) [+]
s.Add("Resistor", 0, 0, 8, 0, "resistance=1k", "r1")    // properties, then a name
s.Add("Capacitor", 8, 0, 8, 8, "capacitance=1uF", "c1")
s.Add("Wire", 0, 8, 8, 8)
s.Add("Ground", 0, 8, 0, 12)
c.Run(1ms)
print(s.Find("c1").PinVoltage(0))      // 3.15: the voltage at its first pin
print(s.ToText())                      // the schematic as the text of a .biscuit file
```

- **Elements:** `Circuits.Symbols.Catalog()` lists them with their properties (`category|type|name:kind=default;...`):
  Wire, Ground, Resistor, Capacitor, Inductor, Switch (`closed`, `momentary`), Source and Rail (`waveform` dc/sine/square, `voltage`, `frequency`, `offset`, `phase`, `duty`;
  a rail is a source to ground with one pin), CurrentSource, AudioInput, LogicInput, Diode, Zener, Led, Transistor and Mosfet (`model`, `flip`), Formula, OpAmp, Comparator,
  And/Nand/Or/Nor/Xor/Xnor (`inputs`), Not, Buffer, DFlipFlop, SRLatch, JKFlipFlop (`falling`, `reset`, `spacing`), SchmittTrigger (`lower`, `upper`,
  `inverting`), Counter, Timer555 (DFlipFlop, Counter and Timer555 also in Falstad's chip layout: `layout` = falstad puts their pins on all four sides as there,
  `spacing` 4 or 2; the 555 then takes its supply at the Vcc pin, `groundPin`), Potentiometer (`position`; potentiometers with the same `group` turn together, like a stereo potentiometer), Transformer (`ratio`, `coupling`, `width`, `reverse`), Relay (`coil` both/start/end),
  Switch2 (`position`, `throws`, `centerOff`, `link`: switches of the same link move together), AnalogSwitch, AnalogSwitch2 (`swap`), Lamp, Fuse, Crystal,
  LightResistor, Thermistor, Memristor, SparkGap, TransmissionLine (`width`), Sweep (one pin, like a rail), RelayCoil and RelayContact (`label`), Jfet (`pChannel`, `flip`),
  Darlington, Thyristor and Triac (`smallGrid`, `legacy`: where Falstad puts the gate), Diac, Varactor, TunnelDiode, Unijunction (`flip`), the tubes VacuumDiode, Triode and Pentode (`model`, `parameters`, `flip`), VCVS, VCCS, CCCS, CCVS,
  CurrentConveyor, Ota, Optocoupler, the logic chips TFlipFlop, Latch, RingCounter, Multiplexer, Demultiplexer, HalfAdder, FullAdder, SipoShift, PisoShift,
  Adc, Dac, SevenSegmentDecoder, SevenSegment, Monostable, PhaseComparator, Vco (always in Falstad's chip layout; `bits`, `spacing` and the options of the
  chip) and TriState (`flip`: the control on the other side); sources and rails also have the waveforms triangle,
  sawtooth and noise, LogicOutput, Probe (`measure` voltage - the value now -, max, min, peakToPeak, average, rms or frequency, from what it recorded),
  Scope (`probe` = the name of an element or one of its pins - `R1.2` counted from 1, or the name of a port of its part: `Q1.B` -, `quantity`
  voltage/current/power (v * i), `timePerDiv` (the time base: 10 divisions across; 0 = one step per pixel), `trigger` free/rising/falling through `triggerLevel`,
  `display` time, xy (the current of the element over its voltage, its characteristic curve) or spectrum - an FFT analyzer: the last `fftSize` samples (128 .. 4096, default 512) taken at twice `maxFrequency` (default 20kHz; 0: at the rate of the simulation, the axis automatically), Hann window, in dB, computed natively; `range`), Label (`text`), Pin (`port`: a port of the circuit, so a schematic can be a subcircuit), Text, and the parts at the simulated devices: GpioLine,
  I2cAdc, I2cDac, SpiShiftOut, SpiShiftIn (see [Parts at the simulated devices](#parts-at-the-simulated-devices)).
- **Pins:** two-pin elements at their two points (a source and a diode: `-`/anode at the first point); a transistor has its base at the first point and collector and
  emitter 2 units to the sides of the second, an op amp its inputs 2 units to the sides of the first point and the output at the second, a gate its inputs 2 units apart
  - all as in Falstad's simulator (a unit of the grid is 8 of its pixels). Chips (555, flip-flops, counter, the logic chips) are boxes with their pins down the left and right side
  (`spacing` units apart). A potentiometer has its ends on the axis and the wiper at the middle, as far to the side as the second point says; a transformer has
  its primary along the left and the secondary along the right side, `width` units long; a relay has the common contact at the first point, the two
  contacts 2 units to the sides of the second and the coil below them.
- **Values** are numbers with an SI prefix and a unit after it (`4.7k`, `10uF`, `2.2µH`, `1meg`) or expressions of the parameters of the circuit (`2*R + 1k`): they are
  worked out again when a parameter changes.
- `s.Move(e, x1, y1, x2, y2)`, `s.Set(e, key, value)`, `s.Rename(e, name)`, `s.Remove(e)`, `s.Clear()`, `s.Find(name)`, `s.PartOf(name)`; an element tells `PinCount`,
  `PinX(i)`/`PinY(i)`, `PinVoltage(i)`, its `Part` and an `Error` when its part could not be made (an unknown model).

### The file and the generated class

A `.biscuit` file is that text: JSON with the elements (`type`, `name`, `from`, `to` and the properties that differ from the defaults), optionally `class`, `namespace`,
`timeStep` and `parameters`. Like a `.fxml` file (docs/UI_MARKUP.md) it is compiled into a script: **one class**, named like the file (or `class`), that derives from
`Circuits.Circuit`, loads the schematic in its constructor and has a field for every named element, typed with its part.

```fire
#include "Amp.biscuit"          // in a project the file is compiled with the others, no #include

var amp = new Amp()
amp.Run(1ms)
print(amp.out.PinVoltage(0))    // the LogicOutput "out"
amp.rf.Resistance = 22kOhm      // the Resistor "rf"
amp.Parameter("gain", 4)        // a value "gain*1k" follows
```

The fields are set again whenever the schematic makes its parts anew (`OnBuilt()`, which a derived class can override too). `fire.Compiler biscuit Amp.biscuit` shows the script.

### Inputs and outputs

`"ports"` lists the **inputs and outputs** of the circuit: each a port of the circuit, seen from outside, bound to a pin of an element - `R1.2` is the second
pin of R1 (counted from 1), `Q1.B` the port named B of its part:

```json
"ports": [
  {"name": "In", "direction": "input", "pin": "R1.1", "color": "#E03030"},
  {"name": "Out", "direction": "output", "pin": "C1.2", "color": "#9040E0"}
]
```

The schematic puts a port of that name on the node of the pin (`AddPort`, as a Pin element does), so the generated class can be a part of another circuit:
it gets a member `Circuits.Port In` per port, marked `[Colorize 0xE03030]` - the editor shows `amp.In` in the color of the port (SPEC 5.12).

```fire
var amp = new Amp()
var top = new Circuits.Circuit("top")
top.Add(amp)
var v = new Circuits.VoltageSource(1V)
top.Add(v)
v.A.Connect(amp.In)
v.B.Connect(top.Ground)
top.Run(1ms)
print(amp.Out.Voltage)
```

A port that names no element or pin, a name used twice or by an element is reported (`PortDefinition.Error`; the generator refuses a name of an element
or of a member of `Circuits.Circuit`). Renaming an element moves the bindings along. In fire: `s.PortDefinitions()` (`Name`, `Direction`, `Pin`, `Color`,
`Error`), `s.AddPortDefinition(name, direction, pin, color)` (an empty name or color: the next free one, `In1`/`Out1`, red, violet, orange, blue, green, ...),
`s.SetPortDefinition(index, key, value)`, `s.RemovePortDefinition(index)`, `s.PinNames()`.

## The circuit view (`#import "circuits.ui"`)

`UI.CircuitView` is a control of the UI library (docs/UI.md) that shows a circuit as a live schematic, like Falstad's simulator: wires and leads coloured by their voltage
(green positive, red negative, grey 0 V; `voltageRange`), dots running with the current through the parts and the wires, LEDs that light up, logic outputs, probes,
scopes drawn into the schematic. While `running` it simulates in real time at the speed of the circuit (`Circuit.Speed`, see "Real time"; Space pauses).

```fire
#import "circuits.ui"

var fb = new Framebuffer(800, 500)
var ui = new UI.Root(fb, new Window(fb, "Circuit"))
var amp = new Amp()
var view = new UI.CircuitView(amp, 0, 0, 800, 500)
ui.Add(view)
view.FitToView()
ui.Run()
```

A click toggles switches (a `momentary` one while it is held) and logic inputs - also when the view is `readOnly`. Without `readOnly` the schematic is edited right in the view:

| | |
|---|---|
| place | `view.BeginPlace("Resistor")` (from a palette, a menu, a toolstrip of the program); then a click places it with its default length, a drag gives length and direction; Esc stops |
| wires | drag from a pin (or `BeginPlace("Wire")`, W); wires go along the grid |
| select | click, a rectangle on empty space, Ctrl+A; `Selection()`, `SelectedElement`, `OnSelect` |
| move, stretch | drag the selection; drag an end of a selected element |
| rotate, flip, duplicate, delete | R (Shift+R counterclockwise), F, Ctrl+D, Del (or `RotateSelection(ccw)`, `MirrorSelection(horizontal)`, ...) |
| copy, paste | `SelectionText()` gives the selection as JSON (the format of the file), `PasteText(text)` inserts it a little offset and selects it; names stay when they are free |
| find, replace | `FindNext(text, backwards)` selects and shows the next element whose name or value contains the text; `ReplaceAll(find, replace, selectionOnly)` |
| undo, redo | Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z (`Undo()`, `Redo()`) |
| properties | double click or Enter: the view's own editor, or `OnEdit` of the program |
| the circuit | `SetCircuitProperty("class" / "namespace" / "timeStep", text)` and `CircuitProperty(key)` (with undo); `Positions()` lists what can be operated while it runs (potentiometers - a group once -, switches, push buttons, selector switches, logic inputs) and `Operate(index, value)` operates it; `Ports()` lists the Pin elements and the parts bound to devices; `PortLines()` the inputs and outputs (`index|name|direction|pin|color|error`), `AddPort(direction, pin)`, `SetPort(index, key, value)`, `RemovePort(index)` change them with undo |
| zoom, pan | the wheel zooms at the pointer, the middle or right button pans; `FitToView()` |
| context menu | a right click without dragging: on an element Attach Scope (voltage, current, power), an XY scope, Attach FFT Analyzer; on a pin a scope for that pin; properties, rotate, flip, duplicate, delete; on a scope time, spectrum or XY; on empty space run, reset, select all, fit, grid, values, current. `AttachScope(element, quantity, display)` does the same from a program |

The view draws at the full resolution of the screen (a UI.Root with `Scaling` above 1): lines and text are sharp, not stretched. Changes that keep the connections
(moving a scope, a text, or a group as a whole) do not compile the circuit again - the simulation and the scopes go on.

**Info box.** While exactly one element is selected, a box at the top right shows its values, like the one at the bottom right of Falstad's simulator: its kind
and name, its properties with a unit, the voltage across it, the current through it and the power (a part with more pins: the voltage of each pin). For a scope
(also as FFT analyzer or XY) and a probe it shows what was recorded: max, min, peak to peak, average, rms, frequency and period, the time it covers - and for a
spectrum the strongest line and the width of a bin. `InfoLines(element)` gives the lines.

`OnChange` (or `TakeChanged()`) reports every change - `view.Schematic.ToText()` is the file to save.

## The circuit editor of spark

spark opens `.biscuit` files in its circuit editor (Add > New > Circuit makes one). In the middle is the CircuitView itself, running live in a fire program of its own,
so the editor draws and simulates exactly like a program does; around it are the palette of parts (from `Circuits.Symbols.Catalog()`, each with a small picture of its
symbol) and the tools. The palette is the tool window **Toolbox**, docked at the left edge: it appears while a circuit is the active document and hides
again when another document is active (unless it is pinned); closed while a circuit is active, it stays closed until **View > Toolbox** (there while a circuit is active) brings it back. The tool strip **Edit** works for every document - undo, redo, cut, copy, paste, delete, select
all, find and replace (Ctrl+F / Ctrl+H, F3 and Shift+F3 search on), go to definition and toggle comment (these two only in scripts); in a circuit, copy puts the
selected elements on the clipboard as JSON and paste inserts them, so they also go from one circuit to another. The tool strip **Simulation** is on while a
circuit is active: pause/run, reset, the steps per frame (1 to 10k, behind the tempo symbol, or **Real time**, see [Real time](#real-time)), rotate clockwise and counterclockwise, flip horizontally and
vertically, fit to view, **Import** (a Falstad file or the clipboard), **Samples** and **Properties**. The embedded view takes the accent color of spark. The properties of the selected element (name, values with
units, models of the library, choices) are edited in the tool window **Properties** of spark (View > Properties; by default under the Solution Explorer), the same
window the UI markup editor uses - a table like the property grid of .NET: names and values with lines between the cells, categories that fold, sorted by
category or by name, a column border to drag and a description of the selected row; double click or Enter on an element puts the focus into its first field.
With nothing selected the window shows the **circuit** itself: the class and namespace of the generated class, the **time step** of the simulation, and under
**Positions** a control for everything that is operated while the circuit runs - a slider for each potentiometer (one for a `group`; dragged, it sets the
position when it is let go, keys and the wheel at once), an on/off box for
switches and logic inputs, a button for push buttons (closed while it is held), a list for selector switches. **Properties** in the tool strip Simulation opens the same
settings as a dialog, with the **inputs and outputs** of the circuit: a row per port with its color (a palette or any `#RRGGBB`), its name, input or output, the
pin it is bound to (a list of the pins of the schematic, or typed: `R1.2`, `Q1.B`) and what is wrong with it; **Add input** / **Add output** make a new one
with the next free name and color; the dialog follows every change at once (its rows are bound to a model that the schematic's reports update). Right-clicking
any pin (also of a wire or a ground) offers **Make Pin an Input/Output of the Circuit** - an element without a name gets one first (R1, W2, ...) - or removes the
port bound there. The drawing marks every bound pin with a dot in the color of its port and its name (`> In1` for an input, `Out1 >` for an output).

**Samples** loads one of the example circuits of the editor (our own, embedded in `fire.UI.Markup`, `CircuitSamples.All`): an RC
low-pass, an LC tank, a bridge rectifier, a Zener regulator, a transistor switch, an inverting amplifier, a Schmitt trigger oscillator, a 555 blinker, a
counter with a 7-segment display and a full adder. When a circuit is already drawn, the editor asks before it replaces it (Undo brings it back) - the same
for the import.

**Import** takes a circuit of Paul Falstad's simulator - the XML file it saves now (`<cir ...>` with a tag per element), or the older text of File > Export As
Text, from a file or the clipboard - and keeps its drawing: the points are
divided by 8, wires, resistors, capacitors, inductors, sources and rails (DC, sine, square; triangle and the like as square), current sources, switches, diodes, Zeners,
LEDs, transistors, MOSFETs, op amps, comparators, gates, potentiometers, transformers, JK flip-flops, Schmitt triggers, relays (the first pole), two-way and
analog switches, lamps, fuses, memristors, spark gaps, LDRs, thermistors, transmission lines, sweeps, noise, triangle and sawtooth sources, rails with a slider,
relay coils and contacts, JFETs, Darlingtons, thyristors, triacs, diacs, unijunction transistors, varactors, tunnel diodes, triodes, current conveyors, OTAs, optocouplers, the controlled
sources whose expression is linear, D flip-flops, 555 timers and counters (in Falstad's chip layout, `layout` = falstad), T flip-flops, latches, ring counters, multiplexers,
demultiplexers, adders, shift registers, ADCs, DACs, 7-segment decoders and displays, monostables, VCOs, phase comparators, tri-state buffers, logic inputs and outputs, probes,
labels, texts and scopes become elements. What the package has not got (latches with enable pins, 14- and 16-segment displays, nonlinear controlled sources, saturation of a transformer, ...) is
listed. Of the 373 example circuits in text form, 336 come over without leaving an element out. In the XML format the values come from the named attributes; pots
linked there (`li`) get a common `group`, the scopes look at the first value of their plot.
`forge falstad circuit.txt -o circuit.biscuit` (or `circuit.xml`) does the same on the command line.

**Help > Samples > Circuits** has two projects that use a drawn circuit in a program: *RcFilter* (the class of `RcFilter.biscuit` in a circuit of the
program, driven at its input `In`, read at `Out`, a part changed while it runs) and *AudioFuzz* (tones through a diode clipper with a tone filter,
`Fuzz.biscuit`: `ProcessAudio` with its `AudioInput` and the port `Out`, played clean and distorted on the sound device).

## Coming next

More ICs (counters with load, custom logic), more parts at the simulated devices (I2C sensors with their own registers, an SPI ADC), and more of Falstad's elements for the import.
