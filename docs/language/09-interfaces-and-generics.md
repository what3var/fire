# Interfaces and generics

## Interfaces

An `interface` lists methods (and properties, see below) that a class promises to have. It has no fields and no code.

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
- The methods of an interface are public, and so are the methods that implement them: `private int Area()` or `protected` for a method of
  `IShape` is a compile error (`E4063`). That holds for a derived class too - it cannot hide a method of its base class's interface with a
  `private` one. A public method inherited from the base class counts, and other methods of the same name (another number of parameters) may be
  private. A call through a value of an interface type (`s.Area()` with `IShape s`) therefore needs no access check.
- An interface is a type: parameters, fields, variables and return values can be declared with it, and `is of IShape` tests it. `catch (IShape e)`
  catches an exception whose class implements it.
- Interfaces can be generic: `interface ICommand<T> { undefined Execute(T context) }`.
- Through a value of an interface type only what the interface declares (with the interfaces it extends) is reachable: `s.radius` or `s.Grow()`
  with `IShape s` is `TC008` (a warning in lax mode, an error in strict mode) even when the class of the object has the member, and a call with a
  number of arguments no declared method takes is `TC002`. A cast reaches the rest: `(s!Circle).radius`. The ownership methods every object has
  (`TakeTo`, `TakeLocal`, `TakeUpwards`, `TakeGlobal`) are always allowed.

### Properties in an interface

An interface can declare properties - written like an auto-property of a class, the accessors without bodies:

```fire
interface IGauge {
    int Limit { get; set; }
    string Label { get; }
    float Delay : ms { get; }
}

class Gauge : IGauge {
    int Limit { get; set; }                          // an auto-property
    string Label { get { return "gauge" } }          // a property with its own get
    float Delay : ms { get; set; }                   // read-write for a read-only member: fine
}

IGauge g = new Gauge()
g.Limit = 5
print(g.Label + " " + g.Limit)      // gauge 5
// g.Label = "x"                    // error TC016: IGauge declares Label without 'set'
```

- `{ get; set; }`, `{ get; }` or `{ set; }`; the type is required (`any` if every type is allowed), a unit is written as in a class (`float Delay : ms`).
- The class implements it with a **property** of the same name - an auto-property, one with its own `get`/`set`, or one inherited from the base class.
  It needs at least the accessors the interface names; a read-only member may be implemented by a read-write property. A missing property (or a missing
  `set`) is `E4064`, a `private`/`protected` one `E4063`, another value type or unit (`string` for `int`, `int : s` for `int : ms`) `E4066`.
- A **field** of the same name does not implement it (`E4065`), and neither do methods `get_Limit()`/`set_Limit(v)` - a property is what the interface promises.
- Through a value of the interface type the property has its declared type and unit (`g.Limit = "x"` is `TC001`), and writing a property declared without
  `set` (or reading one without `get`) is the error `TC016`, even when the class of the object would have the accessor.

### Interfaces that extend interfaces

An interface can extend one or more interfaces. A class that implements it implements all of their members and is of every one of them:

```fire
interface IReader { int Read() }
interface IWriter { undefined Write(int v) }
interface IStream : IReader, IWriter { int Size { get; } }

class Memory : IStream {
    int last
    int Read() { return this.last }
    undefined Write(int v) { this.last = v }
    int Size { get { return 1 } }
}

class Copy {
    static int Pull(IReader r) { return r.Read() }
}

IStream s = new Memory()
s.Write(7)
IReader r = s                    // no cast needed: an IStream is an IReader
print(Copy.Pull(s) + r.Read())   // 14
print(s is of IWriter)           // True
```

- A missing member of a base interface is `E4010` (`E4064` for a property), as for the interface itself.
- `is of`, casts (`r!IStream`), `catch`, overloads (`Pick(IWriter w)`) and `where T is of IReader` see the base interfaces too.
- Only interfaces can be extended (`interface I : SomeClass` is `E4067`). An interface may not extend itself over any chain (`E4068`); reaching the same
  interface on two ways (a diamond: `IBoth : ILeft, IRight` with `ILeft : IBase` and `IRight : IBase`) is fine.
- A generic interface can extend a generic one: `interface IList<T> : IEnumerable<T> { ... }` - `foreach` over an `IList<int>` knows its elements are `int`.
- Through a value of a derived interface the members of its base interfaces are reachable directly (`s.Read()` with `IStream s`).
- `Type.Of(x).Interfaces` (reflection) lists every interface the class reaches - the ones it names, the ones they extend and those of its base
  classes; `Type.Of(x).DeclaredInterfaces` only the ones it names itself (see [Probes and reflection](16-probes-and-reflection.md#reflection-import-reflection)).

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

In the code of a generic class or method, `T is of Name` asks the same at run time - about the type argument that runs, not about a value:

```fire
class Box<T> {
    string What() {
        if (T is of Animal) { return "animal" }    // Box<Dog>: the base class counts
        if (T is of IShape) { return "shape" }     // a class that implements IShape
        if (T is of int) { return "int" }
        return "other"
    }
}
print(new Box<Dog>().What() + " " + new Box<int>().What())   // animal int
```

`List<int>` is of `List`; a type argument that is not known (the type is not generic there) is of nothing (`false`). The same in the VM and natively.

The conditions are checked at `new`. At run time fire does not create a separate class per type argument - a `Box<int>` and a `Box<string>` are
the same class - but every object keeps its type arguments, and what is stored in a `T` is checked against them:

```fire
class Box<T> {
    T item
    undefined Put(T item) { this.item = item }
}
var b = new Box<int>()
any text = "x"
try { b.Put(text) } catch (ArgumentException e) { print(e.message) }   // Parameter 'item' is a T (int), not String.
var f = new Box<float>()
f.Put(3)                                                                // stored as 3.0
```

`new Box<T>()` inside generic code passes the type argument on, and a class like `class IntBox : Box<int>` fixes it for its objects. A lambda
made in generic code takes the type arguments it uses along.

A number type argument means exactly what the declaration `int x` or `int x : cm` means - `T` is replaced by it:

| Type argument | `T value` holds |
|---|---|
| `Box<int>` | whole numbers without a unit (`Put(4cm)` is a `UnitMismatchException`) |
| `Box<int : cm>` | whole numbers in cm (`Put(2m)` stores `200cm`, `Put(3kg)` is a `UnitMismatchException`) |
| `Box<float : any m>` | floats of the dimension length, in their own unit |
| `Box<int : any>` | whole numbers in any unit |

```fire
class Box<T> {
    T item
    undefined Put(T item) { this.item = item }
}
var lengths = new List<int : cm>()
lengths.Add(3m)
print(lengths[0])        // 300cm
```

## Generic methods

A method can have its own type parameters. They are taken from the arguments - the call is usually written without `<...>`:

```fire
class Util {
    static T Pick<T>(T a, T b, bool first) {
        if (first) { return a }
        return b
    }
}
print(Util.Pick("a", "b", false))   // b - the type checker knows the result is a string
```

The type arguments found for a call are passed to it, so its parameters are checked like those of a generic class (`Util.Pick(1, text, true)`
with an `any text = "x"` is an `ArgumentException`). An `int` and a `float` together give a `float` (`Pick(2, 3.5, true)`), and the `2` is a
`2.0` already at the call. In native code a call with `int`, `float` or `bool` type arguments runs a copy of the method made for them, with
typed arithmetic - for generic methods and for the methods of a `Box<int>` or `Box<float>` alike.

The type arguments can also be written: `Util.Pick<string>(a, b, true)`. Then they are not inferred, and when the method is overloaded
(see [Overloading by type](08-classes.md#overloading-by-type)) the call means the generic overload with that many type parameters:

```fire
class Util {
    static string Describe(string s) { return "a string" }
    static string Describe<T>(T x) { return "something" }
}
print(Util.Describe("x"))           // a string
print(Util.Describe<string>("x"))   // something
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
