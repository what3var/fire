# Roadmap

What is planned for the language and its libraries. The points come from writing this reference: every gap that showed up while the examples were
tested is listed here. The order inside a group is the suggested order of work.

[Back to the contents](README.md)

## 1. Standard library

| # | Task | Why |
|---|---|---|
| 1.1 | **A math library** (`Math.Sqrt`, `Abs`, `Min`, `Max`, `Round`, `Floor`, `Ceiling`, `Sin`, `Cos`, `Tan`, `Atan2`, `Log`, `Exp`, `PI`, `E`) - in the VM and natively, with units where they make sense (`Math.Sqrt(9m^2)` is `3m`) | today only `^` exists; a square root needs `x ^ 0.5` |
| 1.2 | **Numbers from text**: `"42".ToInt()`, `"2.5".ToFloat()`, `int.TryParse`-like methods that return `undefined` instead of throwing, optionally with a unit (`"5mm"`) | reading numbers from files, the console and the network |
| 1.3 | **Ordering strings**: `string.CompareTo(other)` (ordinal) and `<`/`>` on strings | sorting names with `OrderBy` works only through a key today |
| 1.4 | `PseudoRandom.Pick` and `Shuffle` for arrays too (today they take a `List`) | `rnd.Pick([1, 2, 3])` is an error |
| 1.5 | A `StringBuilder` (or `List<string>.Join`) for building long texts | `+=` in a loop copies the text every time |

## 2. Language

| # | Task | Why |
|---|---|---|
| 2.1 | **Call a lambda stored in a field directly**: `this.onClick()` | today it looks for a method `onClick`; the lambda has to be read into a variable first |
| 2.2 | **Rebind a lambda**: `var b = a on other` (described in the specification, not implemented) | reuse a handler for several objects |
| 2.3 | `switch` with several values per case: `case 5, 6:` | today two cases or a range are needed |
| 2.4 | The conditional operator `cond ? a : b` | short choices in expressions |
| 2.5 | `do { ... } while (cond)` | loops that run at least once |
| 2.6 | Leaving nested loops: `break` with a label, or `break 2` | today a flag or a method with `return` is needed |
| 2.7 | Combined units in declarations: `float speed : m/s` | today only a single unit name can be written; `m/s` comes only from a calculation |
| 2.8 | Enum names: `Color.Red.ToString()` gives `"Red"`, `Color.Parse("Red")` | `print` shows only the number |
| 2.9 | `;` after a block as a separator: `foreach (x in a) { ... }; return t` on one line | today it is a syntax error |
| 2.10 | Digit separators in number literals: `1_000_000` | readability of large numbers |
| 2.11 | Bit widths take effect on assignment: `int[8] x = 300` wraps to `44`, `float[32]` rounds (the mechanism `Value.TruncateTo` exists) | today the width is only metadata in the VM |
| 2.12 | `readonly` fields are protected everywhere: `obj.field = ...` from outside is an error (today only `this.field = ...` outside the constructor is checked) | `c.r = 5` on a readonly field works today |

## 3. Type checker

| # | Task | Why |
|---|---|---|
| 3.1 | A condition that is not a `bool` is reported (`TC001`) where the type is known - **done**; also for `&&`, `||` and `!` operands | an `int` as condition is a run-time error |
| 3.2 | Lambda parameters of selector types (`lambda member<T>`) get the type `T` | today the parameter is `any`, so `x => x.nmae` is found only at run time |
| 3.3 | Report a comparison with `<` between two strings at compile time | it is an `ArgumentException` at run time |

## 4. Runtime and native backend

| # | Task | Why |
|---|---|---|
| 4.1 | Native backend: an operator with operands of the wrong type (`undefined * 2`, `-"a"`) and a non-`bool` condition throw an `ArgumentException` like the VM | the native program ends there today; the VM throws a catchable exception |
| 4.2 | Native backend: the same messages as the VM for all run-time errors (conformance tests for error texts) | users see the same error in the editor and on the device |

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
