# Units

Numbers in fire can carry a **unit**. The unit is part of the value: it is converted, checked and printed.

```fire
print(5mm + 2cm)        // 25mm
print(2m + 500mm)       // 2500mm
print(10m / 2s)         // 5m/s
print(3m * 2m)          // 6m^2
```

## Writing a unit

A unit stands directly after a number literal, without a space: `5mm`, `1.5km`, `90min`, `8kB`.

| Base unit | Meaning | With prefixes |
|---|---|---|
| `m` | metre | `mm`, `cm`, `km`, `µm`, `nm`, ... |
| `g` | gram | `mg`, `kg`, ... |
| `s` | second | `ms`, `µs`, `ns`, ... |
| `b` | bit | `kb`, `Mb`, ... |
| `B` | byte (8 bit) | `kB`, `MB`, `GB` |
| `min`, `h`, `d` | minute, hour, day (compatible with `s`) | no prefixes |

Prefixes: `f` (1e-15), `p` (1e-12), `n` (1e-9), `µ` or `u` (1e-6: `5µs` and `5us` are the same), `m` (1e-3), `c` (1e-2), `k` (1e3), `M` (1e6), `G` (1e9).

Any other word is an **own unit** that is only compatible with itself: `5apples + 3apples` is `8apples`, `5apples + 3pears` is an error.

## Declaring units

A program can declare units of its own - with a name, a symbol and, if it has one, a conversion into a unit that already exists:

```fire
prefixed unit Volt: V
unit Inch: in { coerce m => 0.0254 }
unit Foot: ft { coerce to in => value * 12 }
unprefixed unit Knot: kn { coerce m/s => 1852 / 3600.0 }

print(5V + 300mV)             // 5300mV
print(1.0m:in)                // 39.37007874015748in
print(10in + 1ft)             // 22in
float speed : kn = 10m / 1s
print(speed)                  // 19.438444924406046kn
print(2Inch)                  // 2in - the name works like the symbol
```

- `unit Name : symbol` - the symbol stands after the number and is printed. It is written with letters only.
- `prefixed` allows the prefixes (`mV`, `kV`); `unprefixed`, the default, does not.
- Without a body the unit is a new dimension. With `coerce` it gets the dimension of the unit it names:
  - `coerce m => 0.0254` - 1in is 0.0254m;
  - `coerce to m => value * 0.0254` - how a value in inches becomes metres;
  - `coerce from m => value / 0.0254` - how a value in metres becomes inches.

  The compiler computes the factor from either form (and checks that several of them agree).
- Declare units at the top level of a file. They are known everywhere in the program, also above the declaration and in other files.

### Offsets and other conversions

`coerce unit => ...` without `to` or `from` is a **factor and an offset**: the expression may use `value`, numbers, `+ - * / ^`, `Math.PI` and
`Math.E`. The compiler computes it and inverts it itself:

```fire
unit Kelvin: K
unit Celsius: C { coerce K => value + 273.15 }
unit Fahrenheit: F { coerce C => (value - 32) * 5 / 9 }

print(100.0C:F)        // 212F
print(32.0F:C)         // 0C
print(20C + 9F)        // 7C - the right operand is converted into the unit of the left one
```

`coerce to` and `coerce from` take **any expression** - a method call, `Math.Log10`, a calculation of your own. Such a unit needs both
directions; they are called at run time (slower than a factor):

```fire
prefixed unit Watt: W
unit DecibelMilliwatt: dBm
{
    coerce to mW => 10 ^ (value / 10)
    coerce from mW => 10 * Math.Log10(value)
}

print(30.0dBm:W)       // 1W
print(1.0W:dBm)        // 30dBm
float level : dBm = 2mW
print(level)           // 3.010299956639812dBm
```

- If `coerce to` and `coerce from` turn out to be a factor and an offset, the compiler uses that - nothing is called at run time.
- A unit with an offset or conversion functions takes no prefixes.
- Multiplied with another unit (`20C * 2s`) such a unit counts as its reference unit: the offset and the functions do not take part (`40K*s`).
  A number keeps the unit (`20C * 2` is `40C`).
- An exception in a conversion function is thrown where the conversion happens.

## Calculating with units

- `+`, `-`, `%` and the comparisons need the **same dimension**. Different scales are converted automatically: two `int`s are converted into the **finer** unit
  (`2m + 500mm` is `2500mm`); with a `float` the result is a `float` in the unit of the left operand (`1.5mm + 1m` is `1001.5mm`).
- `*` and `/` combine the dimensions: `3m * 2m` is `6m^2`, `10m / 2s` is `5m/s`. With a number without a unit the unit stays: `20mm / 2` is `10mm`.
- `==` compares the values after conversion: `1m == 100cm` is `True`.
- Different dimensions (`5mm + 2kg`, `5mm + 2`) throw an `UnitMismatchException`. The type checker reports it already where it knows both units (`TC006`).
- `x++` and `x--` count in the unit of `x`: `5cm` becomes `6cm`.

```fire
var a = 1500mm
var b = 2m
print(a < b)                    // True
print(b - a)                    // 500mm
try {
    print(5mm + 2kg)
} catch (UnitMismatchException e) {
    print("caught: " + e.message)
}
```

## Converting: `:` and `::`

| Expression | Result | Meaning |
|---|---|---|
| `2km:m` | `2000m` | convert into another unit of the same dimension |
| `90min:h` | `2h` | an `int` stays an `int` (rounded; `TC007` warns) - convert a float for `1.5h`: `90min!float:h` |
| `12mm:unitless` | `0.012` | the value in the base unit, without a unit |
| `7:s` | `7s` | give a number without unit a unit |
| `5mm:kg` | `0.005kg` | another dimension: the number in the base unit, with the new unit |
| `12mm::m` | `12m` | **relabel**: same number, other unit, nothing is calculated |
| `12mm::unitless` | `12` | remove the unit, keep the number |

`:` never fails; use it only when you really mean a conversion. `value:` without a unit takes the unit from the other operand (see below).

```fire
var distance = 2.5km
print(distance:m)               // 2500m
print(distance::m)              // 2.5m - only the label changed
var seconds = 150s
print(seconds!float:min)        // 2.5min
```

## Testing a unit: `is in`

`value is in unit` is `True` when the value has a unit of the same dimension (the scale does not matter):

```fire
print(5mm is in m)      // True
print(5mm is in kg)     // False
print(3h is in s)       // True
```

When the units of a product or a quotient cancel, their factor goes into the number: `5cm / 1mm` is `50`, `1s / 1ms` is `1000`, `1.5ms / 1us` is
`1500`. Two `int`s stay an `int` when the factor is a whole number or one divided by one (`1ms / 1s` is `0`, like `1 / 1000`).

## Units in declarations

A unit after the name (`int length : mm`) or at the type (`float:m width`) fixes the unit of everything that is stored there:

- A value of the same dimension is **converted** when it is stored: `int a : mm = 2m` holds `2000mm`.
- A value of another dimension or without unit is reported (`TC006`) and throws an `UnitMismatchException` at run time.
- `: any` accepts every unit unchanged; `: any m` accepts every unit of the dimension of `m` without converting it.
- A declaration **without** a unit takes the unit of its initializer: after `int e = 5mm`, `e = 2cm` holds `20mm`. Without an initializer (or with a
  number without unit) it is unitless. `var` is free: `var x = 5mm` and later `x = 3s` is allowed.
- The return type of a method and a property can have a unit too: `float:m Length()` converts its result into metres.

A declaration can also name a **combined unit**: units joined by `*` and `/`, each with an optional power - `m/s`, `km/h`, `m/s^2`, `kg*m/s^2`,
`m^2`. Everything after a `/` divides.

```fire
float speed : km/h = 10m / 1s
print(speed)                    // 36km/h
float g : m/s^2 = 9.81m / 1s / 1s
print(g)                        // 9.81m/s^2
float area : m^2 = 2m * 3m
print(area)                     // 6m^2
```

```fire
int a : mm = 2m
print(a)                        // 2000mm
int e = 5mm
e = 2cm
print(e)                        // 20mm
float len : any m = 3km
print(len)                      // 3km

class Board {
    float:mm thickness
    construct(float t : mm) { this.thickness = t }
    float:cm Thickness() { return this.thickness }
}
var board = new Board(0.02m)
print(board.thickness)          // 20mm
print(board.Thickness())        // 2cm
```

## The target unit of an operation (advanced)

When one operand asks for automatic conversion with `:` (no unit after it), the **other** operand decides the unit; when all operands ask for it, the result
is unitless:

```fire
float b = 2km
int a = 500m
var c = b + a:!         // b decides: 2.5km (a is converted, and to float with !)
print(c)
```

A chain `a + b + c` is calculated from left to right: `(a + b) + c`, and the rule applies at every step.

## Exercises

1. Compute the speed of a runner who runs `10km` in `48min` - in `m/s` and in `km/h`.
2. Write a method `float:mm Perimeter(float w : mm, float h : mm)` and call it with `2cm` and `0.5m`.
3. What is the difference between `150cm:m` and `150cm::m`? Print both.
4. Catch the error of `1m + 1s` and print its message.
5. Store file sizes with `B` and `kB` and add them. In which unit is the result?
6. Declare `Yard` (`yd`, 3 feet) on top of `Foot` and print a 100m run in yards.
