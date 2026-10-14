# Interfaces and generics

## Interfaces

An `interface` lists methods that a class promises to have. It has no fields and no code.

```fire
interface IShape {
    float Area()
    string Name()
}

class Square : IShape {
    float side
    construct(float s) { this.side = s }
    float Area() { return this.side * this.side }
    string Name() { return "square" }
}

class Disc : IShape {
    float r
    construct(float r) { this.r = r }
    float Area() { return 3.14159 * this.r * this.r }
    string Name() { return "disc" }
}

class Report {
    static undefined Show(IShape s) { print(s.Name() + ": " + s.Area()) }
}
Report.Show(new Square(2.0))
Report.Show(new Disc(1.0))
print(new Square(1.0) is of IShape)     // True
```

- A class names its interfaces after the base class: `class Dog : Animal, IShape, IComparable`. It has at most one base class but any number of
  interfaces.
- A class that misses a method of its interfaces is a compile error (`E4010`). A method counts when its name and number of parameters match.
- An interface is a type: parameters, fields, variables and return values can be declared with it, and `is of IShape` tests it.
- Interfaces can be generic: `interface ICommand<T> { undefined Execute(T context) }`.

## IEnumerable

`foreach` works with every class that implements `IEnumerable` - or simply has a method `GetEnumerator()` whose result has `bool MoveNext()` and
`GetCurrent()`:

```fire
class Countdown : IEnumerable<int> {
    int start
    construct(int start) { this.start = start }
    IEnumerator<int> GetEnumerator() { return new CountdownEnumerator(this.start) }
}
class CountdownEnumerator : IEnumerator<int> {
    int current
    construct(int start) { this.current = start + 1 }
    bool MoveNext() { this.current--; return this.current > 0 }
    int GetCurrent() { return this.current }
}
foreach (n in new Countdown(3)) { print(n) }    // 3 2 1
```

`MoveNext()` is called before every round; it returns `False` when there are no more elements. With `IEnumerable<int>` the type checker knows that
`n` is an `int`. Arrays, byte buffers and `List<T>` are `IEnumerable` too.

## Generic classes

A class can have **type parameters**: `class Box<T>`. `T` stands for a type that is chosen with `new Box<int>(...)`.

```fire
class Box<T> {
    T item
    construct(T item) { this.item = item }
    T Get() { return this.item }
}
var b = new Box<int>(5)
print(b.Get() + 1)                  // 6
var s = new Box<string>("x")
print(s.Get())
```

- Several parameters: `class Pair<K, V>`. Type arguments can be nested: `new Box<List<int>>(...)`.
- A type with arguments can be used everywhere a type stands: `Box<int> b`, `List<List<string>> rows`, `IEnumerable<float> Values()`.
- The type checker replaces `T` by the argument: `new Box<int>(5).Get()` is an `int`, and `b.item = "x"` is reported.
- Without arguments a generic class is its **raw form**: `new List()` takes values of every type.
- A generic class can have the same name as a non-generic one (`Box` and `Box<T>`); the number of type arguments at `new` selects the class.

### Conditions: `where`

`where` restricts the type arguments. Conditions joined with `:` must all hold, groups separated by `,` are alternatives:

```fire
class Animal { }
class Dog : Animal { }
class Kennel<T> where T is of Animal {
    T resident
}
var k = new Kennel<Dog>()       // fine
// new Kennel<int>()            // error E4013: int is not an Animal
print(k is of Kennel)
```

- `where T is of Name` - `T` is the class/interface `Name` or derives from it.
- `where T is in "mm"` - `T` is a unit of the same dimension as `mm`.
- `where T is of float : is in "mm"` - both; `where T is of Dog, is of Cat` - one of them.

The conditions are checked at `new`. At run time fire does not create a separate class per type argument: `T` is a placeholder that the
type checker uses, and a `Box<int>` and a `Box<string>` are the same class at run time.

## Generic methods

A method can have its own type parameters. They are taken from the arguments - the call is written without `<...>`:

```fire
class Util {
    static T Pick<T>(T a, T b, bool first) {
        if (first) { return a }
        return b
    }
}
print(Util.Pick("a", "b", false))   // b - the type checker knows the result is a string
```

## Generic types of the standard library

| Type | Meaning |
|---|---|
| `List<T>` | a growing list (see [Arrays and lists](07-arrays-and-lists.md#lists-listt)) |
| `IEnumerable<T>`, `IEnumerator<T>` | something that `foreach` can walk through |
| `ICommand<T>`, `Command<T>` | a command as an object: `Execute(T context)` calls a lambda |
| `Query<T>` | a query of `#import "linq"` |

## Exercises

1. Add a third shape `Triangle` to `IShape` and print the total area of an array of shapes.
2. Write a generic class `Pair<A, B>` with `First` and `Second` and a method `Pair<B, A> Swap()`.
3. Write `class Range : IEnumerable<int>` that counts from `from` to `to` in steps of `step`.
4. Restrict a generic class `Measure<T>` to lengths with `where T is in "m"` and try it with `"cm"` and `"kg"`.
5. Write `static T Largest<T>(T values[])` and call it with an `int` array and a `float` array.
