# Enums and namespaces

## Enums

An `enum` is a type of its own with named values:

```fire
enum Color { Red, Green, Blue }
enum Status {
    Active = 10,
    Inactive,       // 11 - one more than the one before
    Paused = 20,
    Done            // 21
}
print(Color.Green)      // Green
print(Status.Done)      // Done

var c = Color.Blue
if (c == Color.Blue) { print("blue") }
switch (c) {
    case Color.Red:
        print("red")
    case Color.Blue:
        print("is blue")
    case default:
        print("something else")
}
```

- Without a value the first member is `0` and every following one is one more than the one before. An explicit value is an integer literal.
- A member is written with the enum name: `Color.Red`. The members are separated by commas and may stand on several lines.
- An enum value is **not an `int`** (like in C#): `Color.Red is of Color` is `True`, `Color.Red is of int` is `False`, and `print` and string
  concatenation show the name (`"Farbe: " + c` gives `Farbe: Blue`). A value without a member shows its number.
- `Color` can be used as a type everywhere: `Color c = Color.Red`, parameters, fields, return values. The type checker warns (`TC001`) when an `int` is
  assigned to a `Color` or a `Color` to an `int`.
- Converting: `n!Color` makes an enum value of a number, `c!int` the number of an enum value. Comparing with a number (`c == 5`) and calculating
  (`c + 1`, a plain `int`) still work with the number.
- `c.ToString()` gives the name as a string.

### Helpers

Every enum has the static helpers `Name`, `Parse`, `IsDefined` and `Values`:

```fire
enum Color { Red, Green = 5, Blue }
print(Color.Name(Color.Blue))       // Blue
print(Color.Name(42))               // 42
print(Color.Parse("Green"))         // Green (the member, not the number)
print(Color.IsDefined(Color.Red))   // True
print(Color.IsDefined(1))           // False
foreach (x in Color.Values()) { print(x) }   // Red Green Blue
try { Color.Parse("Pink") } catch (ArgumentException e) { print(e.message) }
```

- `Name(v)` gives the name of a member (the number as text if no member has the value), `Parse(text)` the member with that name (an
  `ArgumentException` for an unknown one), `IsDefined(v)` whether a member has the value, `Values()` an array of all members in the order of the
  declaration.
- A member called `Name`, `Parse`, `IsDefined` or `Values` hides the helper of the same name.

### `switch` over an enum

A `switch` over an enum value without `case default` that does not name every member gets the warning `TC010` (also in strict mode - a forgotten
member is a hint, not an error):

```fire
switch (c) {            // warning TC010: The switch over 'Color' has no case for Green ...
    case Color.Red:
        print("red")
    case Color.Blue:
        print("blue")
}
```

### Flag enums

A `flag enum` holds values that are combined bit by bit. Its members are usually powers of two:

```fire
flag enum Access { None = 0, Read = 1, Write = 2, Execute = 4 }
var a = Access.Read | Access.Write
print(a)                            // Read, Write
print(a.HasFlag(Access.Write))      // True
print(a.HasFlag(Access.Execute))    // False
print(a & Access.Read)              // Read
```

- `|`, `&`, `#` (exclusive or) and `~` of two values of the same flag enum give a value of that enum again; `print` shows the names of the flags it combines
  (`Read, Write`) or the number if a bit has no member.
- `value.HasFlag(flag)` is `True` when all bits of `flag` are set in `value`.
- A plain `enum` combined with `|` gives an `int`.

### Flag enums as attributes

A flag enum is also an [attribute](08-classes.md#attributes): `[Access Read]` or `[Access Read, Write]` before a class, a method, a field or a property
marks it with these flags. Several attributes of the same enum are combined. Reflection reads them:

```fire
#import "reflection"
flag enum Test { a = 1, b = 2, c = 4 }

[Test a]
[Test c]
class Sensor {
    [Test b]
    int value
}

var t = Type.Of(new Sensor())
print(t.HasFlag(Test.a))                // True
print(t.Flags(Test.a))                  // a, c
print(t.Find("value").HasFlag(Test.b))  // True
```

- `HasFlag(flag)` on a `Type` or a `Member` says whether the flag is set; `Flags(e)` gives all flags of that enum together (`e` is a member of the enum,
  `Flags(Test.a)`; with its name as a string, `Flags("Test")`, the result is the number) - `0` if there is no such attribute.
- A name that is not a member of the enum is a compile error.
- See [Probes and reflection](16-probes-and-reflection.md).

## Namespaces

A `namespace` groups classes, interfaces and enums under a name:

```fire
namespace Geo {
    enum Kind { Square, Circle }

    class Shape {
        int kind
        construct(int k) { this.kind = k }
        static string Describe() { return "shape" }
    }

    namespace Deep {
        class Inner { }
    }
}

var s = new Geo.Shape(Geo.Kind.Circle)
print(s.kind)                       // 1
print(Geo.Shape.Describe())
print(new Geo.Deep.Inner() is of Geo.Deep.Inner)
```

- From outside, a member is written with its full name: `Geo.Shape`, `Geo.Deep.Inner`. Inside the namespace the short name is enough.
- Namespaces can be nested, also written with a dot: `namespace Geo.Sub { ... }`.
- A name is looked up from the inside out (as in C#): local variables, the members of the object, the namespace the code is written in, the
  global names, then the `#using` namespaces. A class of the own namespace hides a global class of the same name - `new TimeoutException(...)`
  inside `namespace Net` is `Net.TimeoutException`. `global.Name` is the hidden global class (`class Port : global.Device`); a base class with the
  class's own name (`class Port : Port`) is always the other one.
- The same namespace can be opened several times, also in different files; the contents are merged.
- Several libraries of the standard library live in namespaces: `IO`, `Net`, `Http`, `Tls`, `Gpio`, `I2c`, `Spi`, `WiFi`, `Audio`, `Tracker` (see [Standard library](19-standard-library.md)).

## `#using`

`#using Name` at the top of a file lets you write the **type names** of a namespace without the prefix:

```fire
#using Geo
namespace Geo {
    class Shape { static string Describe() { return "shape" } }
}
Shape s = new Shape()               // Geo.Shape
print(s is of Shape)
print(Geo.Shape.Describe())         // static members and enum values keep the full name
```

- `#using` applies to type names: declarations, `new`, `is of`, `catch`, base classes.
- Static members and enum values are still written with the full name (`Geo.Shape.Describe()`, `Geo.Kind.Circle`).
- If two `#using` namespaces contain the same name, the short name is ambiguous (`E4062`); write the full name.
- `#using` applies to the file in which it stands.

## Exercises

1. Write an `enum Weekday` and a method that returns `True` for Saturday and Sunday.
2. Give the members of an `enum HttpStatus` the values `200`, `404` and `500` and print a text for each with `switch`.
3. Put two classes `Vector` and `Matrix` into a `namespace Math2D` and use them from top-level code - once with full names, once with `#using`.
4. Write a `flag enum Permission` with `Read`, `Write` and `Delete` and a method that prints for a combined value whether it may delete.
5. Mark two classes with a flag-enum attribute and print with reflection which of them has a certain flag.
