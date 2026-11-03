# Roadmap

What is planned for the language and its libraries. The points come from writing this reference: every gap that showed up while the examples were
tested is listed here. The order inside a group is the suggested order of work.

[Back to the contents](README.md)

The larger plan (native C, debugging of native builds, binary libraries, the libraries for writing ember, forge and spark in fire) is in
`docs/ROADMAP.md` of the repository.

## 1. Standard library

| # | Task | Why |
|---|---|---|
| 1.1 | Numbers with a unit from text: `"5mm".ToFloat()` | `ToInt`/`ToFloat` read only the number |
| 1.2 | `Math` for whole arrays and lists (`Sum`, `Average`, `Min`, `Max` of many values) | today a loop or `#import "linq"` |
| 1.3 | The litre as a built-in unit: `l`, the same as `dm^3` (`1l == 1dm^3`, `1000l == 1m^3`), with prefixes (`ml`, `cl`, with 2.6 also `dl`, `hl`) | volumes are common; today only `m^3` and its parts, or an own `unit` |

## 2. Language

| # | Task | Why |
|---|---|---|
| 2.6 | The prefix `d` (deci) and `h` (hecto): `dm^3`, `dl`, `hPa` | `d` is the day today, `hPa` is a unit of its own |

## 3. Type checker

Nothing open at the moment.

## 4. Runtime and native backend

| # | Task | Why |
|---|---|---|
| 4.1 | Conformance tests that compare the error texts of VM and native for every run-time error | the texts match for all errors found so far, but they are checked by single tests |

## 5. Documentation and tools

| # | Task | Why |
|---|---|---|
| 5.1 | Run every code block of these pages in the test suite (VM and native), so the reference cannot go out of date | the examples were checked by hand while writing |
| 5.2 | A page about the editor and the debugger (breakpoints, scope view, threads view, probes in the debugger) | the editor is documented only in First Steps |
| 5.3 | Pages for the libraries `ui`, `graphics`, `net`, `http`, `io` in the same style (one page each, examples, exercises) | today they are documented in design documents |
| 5.4 | Solutions for the exercises (a `solutions` folder with one `.fire` file per exercise) | self-study |
| 5.5 | A German translation of the reference | the editor has an English UI, but users may prefer German texts |

## Done while writing this reference

These problems were found by the examples and fixed right away:

- `1 == 1.0` was `False` and `1m == 100cm` was `False` - numbers now compare by value, units are converted (VM and native).
- `'a' < 'b'` crashed the VM - characters can be compared (VM and native).
- `"42"!int`, `undefined * 2`, `-"a"`, `!5`, an index on a number, `x++` on `undefined`, a non-`bool` condition and other operations on values of the
  wrong type crashed the VM with an internal error - they are catchable `ArgumentException`s now.
- `e.resume(...)` and selector lambdas got false warnings of the type checker.
- `!` converts from and to `char` (`65!char`, `'a'!int`), cuts to a bit width (`300!int[8]`, `x!byte`, `x!float[32]`) and casts an object to a
  derived class or an interface (`animal!Dog`, checked at run time).
- New: the conditional operator `cond ? a : b`, `do ... while`, `break 2`/`continue 2`, `case 5, 6:`, digit separators (`1_000_000`), a `;` after a
  block, calling a lambda in a field (`this.onClick()`), rebinding a lambda (`f on other`), `Color.Name`/`Parse`/`IsDefined` for enums, combined
  units in declarations (`float v : m/s`), bit widths that take effect on every assignment, `readonly` fields protected from outside.
- New in the standard library: `Math` (`Sqrt` with units, `Abs`, `Min`, `Max`, `Round`, ..., `Sin`, `Atan2`, `Log`, `PI`, `E`), numbers from text
  (`ToInt`, `ToFloat`, `TryToInt`, `TryToFloat`), `CompareTo` and `<`/`>` on strings, `Join` and `StringBuilder`, `Pick`/`Shuffle` for arrays.
- An error that a native of a package throws (`IndexOutOfBounds`, ...) arrives in the VM as a catchable exception of its class.
- The type checker reports operands of `&&`, `||` and `!` that are not `bool` (`TC001`), and the parameter of a selector lambda (`lambda member<T>`)
  has the type `T`, so `x => x.nmae` is found at compile time.
- Native backend: an operator with operands of the wrong type, a non-`bool` condition, a wrong index and a unit conflict throw the same catchable
  `ArgumentException`/`IndexOutOfBoundsException`/`UnitMismatchException` with the same message as the VM.
- Own units: `prefixed unit Volt: V`, `unit Inch: in { coerce m => 0.0254 }` (also `coerce to`/`coerce from` with `value`), VM and native. Converting two whole
  numbers in units with an uneven factor (`10in + 1ft`) no longer loses 1 by a rounding error, and the factor between two units has no rounding error of
  its own any more (`1.0mm:cm` is `0.1cm`, `2ft:in` is `24in`).
- An assignment as a condition (`if (a = b)`) is the warning `TC009`.
- Enum types: `Color c` is a declared type, `c.ToString()` and `print(c)` give the name, `Color c = 5` is a warning of the type checker (lax) or an
  error (strict) - see [Enums](10-enums-and-namespaces.md).
- Own units with an offset (`unit Celsius: C { coerce K => value + 273.15 }`) and with conversion functions of any kind (`coerce to mW => 10 ^ (value
  / 10)` and `coerce from mW => 10 * Math.Log10(value)`), VM and native.
- `break 2`/`continue 2` run every `finally` on their way (VM and native); combined units at a return type (`float:km/h Speed()`); `float[16]` (IEEE
  half precision) and `float[8]` (FP8 E4M3) round on every assignment (VM and native); a cast `x!Dog` that can never succeed is `TC013`; an interface
  named after the base class (`class Puppy : Dog, IPet`) counts for `is of` and `!`.
