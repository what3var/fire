# Enums and namespaces

## Enums

An `enum` gives names to whole numbers:

```fire
enum Color { Red, Green, Blue }
enum Status {
    Active = 10,
    Inactive,       // 11 - one more than the one before
    Paused = 20,
    Done            // 21
}
print(Color.Green)      // 1
print(Status.Done)      // 21

var c = Color.Blue
if (c == Color.Blue) { print("blue") }
switch (c) {
    case Color.Red:
        print("red")
    case Color.Blue:
        print("is blue")
}
```

- Without a value the first member is `0` and every following one is one more than the one before. An explicit value is an integer literal.
- A member is written with the enum name: `Color.Red`. The compiler replaces it by its number.
- An enum value **is an `int`**: it can be compared, calculated with and stored in `int` variables; `Color.Red is of int` is `True`. There is no type
  `Color` at run time, and `print` shows the number, not the name.
- Names and numbers: `Color.Name(value)` gives the name of a member (the number as text if no member has it), `Color.Parse("Red")` the value of a
  name (an `ArgumentException` for an unknown one), `Color.IsDefined(value)` whether a member has the value:

```fire
enum Color { Red, Green = 5, Blue }
print(Color.Name(Color.Blue))   // Blue
print(Color.Name(42))           // 42
print(Color.Parse("Green"))     // 5
print(Color.IsDefined(1))       // False
try { Color.Parse("Pink") } catch (ArgumentException e) { print(e.message) }
```

  (A member called `Name`, `Parse` or `IsDefined` hides the helper of the same name.)
- The members are separated by commas and may stand on several lines.

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
4. Why does `print(Color.Red)` print `0` and not `Red`? Write a method `string ColorName(int c)` that returns the name.
