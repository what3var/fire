# Types

fire is a typed language with a gradual type checker: what a declaration says is checked where the compiler can see both sides, and values whose type it
cannot know stay dynamic.

## The basic types

| Type | Values | Notes |
|---|---|---|
| `bool` | `true`, `false` | prints as `True`/`False` |
| `int` | 64-bit whole numbers | can carry a [unit](05-units.md) |
| `float` | 64-bit floating point numbers (`double`) | can carry a unit; 32 bit with `#floatwidth 32` |
| `char` | one 16-bit character | `'a'` |
| `string` | an immutable text | see [Strings](06-strings.md) |
| `undefined` | the value `undefined` | "nothing"; fits every type |
| `byte` | a whole number of 8 bits | the same as `int[8]` |
| `any` | every value | the type that accepts everything |
| `class` | every object | not a number, text, array or lambda |
| a class name | objects of that class or a class derived from it | `Circle c` |
| an interface name | objects of a class that implements it | `IEnumerable e` |
| `lambda`, `lambda<int, int>` | a lambda with that many parameters | see [Lambdas](12-lambdas.md) |
| arrays | `int values[]`, return type `int[]` | see [Arrays](07-arrays-and-lists.md) |
| pointers | `int* p` | only in `unsafe` code, see [Native code](18-native-code.md) |

`bool`, `int`, `float`, `char` and `string` are **values**: assigning or passing them copies them. Objects, arrays and lambdas are **references**:
assigning them shares the same instance (see [Ownership](13-ownership.md)).

## Declarations and inference

```fire
int a = 5
float b = 2           // a float declaration holds a float: 2.0
var c = a * 2         // int - the type of the value
string s = "x" + a    // everything converts to string
any v = 5
v = "now a text"      // any takes every value
print(b / 4)          // 0.5
print(v)
```

- `var` takes the type of its value; it never changes later.
- `undefined` fits every type: `Circle c = undefined` is fine. `undefined!Circle` is a typed `undefined` (useful for `var`: `var c = undefined!Circle`).
- `int` converts to `float` implicitly, every value converts to `string`, an object converts to every class it derives from and every interface its class
  implements. Everything else between two known types is a conflict.
- A **field and a property always have a type** (`var` is for local variables). A **parameter** without a type is `any` (reported as `TC004`), a **method**
  without a return type returns `any` (`TC005`). A method that returns nothing is declared `undefined`.

### `int`, `float` and `bool` are a promise

A parameter or a field declared `int`, `float` or `bool` holds a value of that kind - or `undefined` (like a nullable type in C#: a field without an
initializer is `undefined` until it is assigned). This is checked at run time too, wherever the value comes from (dynamic code, a callback, reflection):

- a whole number given to a `float` becomes a float (`3` is `3.0`),
- anything else is an `ArgumentException` (`Parameter 'a' is a float, not String.`, `Field 'n' is an int, not Float.`),
- a number declared without a unit (and without an initializer, or with a plain number like `= 0.0`) is unitless: a value with a unit of no dimension
  is converted, one with a dimension is a `UnitMismatchException`. A declaration with a unit (`float:m`) converts into it, `: any` takes every unit, and
  one whose initializer has a unit (`float len = 1cm`) takes that unit (see [Units](05-units.md)).

```fire
class Box {
    float size
    construct(float s) { this.size = s }
}
Box b = new Box(3)        // 3 becomes 3.0
print(b.size)
any text = "big"
try { b.size = text } catch (ArgumentException e) { print(e.message) }   // Field 'size' is a float, not String.
b.size = undefined        // allowed
```

The same holds for a **class**: a parameter or field declared `Point p` holds `undefined` or an object of `Point` or of a class derived from it;
anything else is an `ArgumentException` (`Parameter 'p' is a Point, not String.`). An **interface** is checked the same way (the class of the object
declares it; `IEnumerable` also takes arrays, buffers and strings), and a **type parameter** by its type argument: an object keeps the type
arguments of its `new` (`T value` of a `Box<int>` holds only ints without a unit - `Box<int : cm>` ints in cm, `Box<int : any>` ints in any
unit -, of a `Box<float>` a float - a whole number becomes one), a call of a generic
method those that were found for it. Without type arguments (the raw form `new Box()`) `T` is checked by its `where` conditions (`T inside` with
`where T is of Animal` holds an `Animal`, `where T is in "m"` a length) and not at all without them; `any` and `class` are not checked.
A **local variable** keeps the promise of its type too: `int x = someAny` with a string in it is an `ArgumentException`
(`Variable 'x' is an int, not String.`), checked only where the type checker cannot tell beforehand.

```fire
class Point { int x = 1 }
class Other { int x = 2 }
class Show {
    static string Of(Point p) { return p == undefined ? "nothing" : "x = " + p.x }
}
print(Show.Of(new Point()))
print(Show.Of(undefined))
any o = new Other()
try { Show.Of(o) } catch (ArgumentException e) { print(e.message) }   // Parameter 'p' is a Point, not Other.
```

The native compiler relies on this promise: with it the arithmetic on such parameters and fields is plain C++ on the number, and the fields and
methods of a parameter or field of a class are reached directly (by index, without asking the class at run time).

## The type checker: lax and strict

The checker runs after every compile and in the editor while you type. Its findings have codes:

| Code | Meaning |
|---|---|
| `TC001` | a value does not fit the declared type (assignment, initializer, field) |
| `TC002` | a call has a number of arguments that no overload takes, or an argument does not fit its parameter |
| `TC003` | a return value or a lambda result does not fit |
| `TC004` | a parameter has no type |
| `TC005` | a method has no return type |
| `TC006` | a unit does not fit (another dimension) |
| `TC007` | a whole number loses its fraction when it is converted into a coarser unit (always a warning) |
| `TC008` | a member that the (fully known) class does not have |
| `TC009` | an assignment as a condition (`if (a = b)`, probably meant `==`; always a warning) |

In **lax** mode (the default) these are warnings; in **strict** mode they are errors. Choose the mode with `#strict` or `#lax` at the top of a file, the project
setting `typeChecking`, or the compiler switch `--types strict`.

A method in strict mode needs its types:

```fire
#strict
class Calc {
    static int Twice(int x) { return x * 2 }
}
print(Calc.Twice(21))
```

## Converting: `!`

`value!type` converts a value to another type; `value!` without a type takes the type from the other side of the operation.

```fire
print(3.7!int)          // 4 (rounded)
print(5!float / 2)      // 2.5
print(undefined!int)    // 0
print(65!char)          // A
print('b'!int)          // 98
print(66.4!char)        // B (rounded)
```

| Target | Converts from |
|---|---|
| `int`, `float` | `int`, `float`, `char` (its code), `bool`, `undefined` (`0`) |
| `char` | `int` (the code), `float` (rounded), `undefined` (`'\0'`) |
| `int[8]`, `int[16]`, `int[32]`, `int[64]`, `byte` | like `int`, then cut to the width (see [Bit widths](#bit-widths)) |
| `float[32]`, `float[64]` | like `float`; `float[32]` rounds to single precision |
| a class or interface | an object of that class or of a derived class, `undefined` - see below |

### Casting objects

`value!ClassName` tells the compiler "I know this is a `ClassName`": the result has that type, so its members can be used. At run time the cast
is checked: an object of another class throws an `ArgumentException`. `undefined` passes unchanged.

```fire
interface IAnimal { string Name() }
class Dog : IAnimal {
    string Name() { return "Rex" }
    string Bark() { return "Woof" }
}
class Cat : IAnimal {
    string Name() { return "Tom" }
}

IAnimal a = new Dog()
print(a!Dog.Bark())             // Woof
IAnimal c = new Cat()
try {
    print(c!Dog.Bark())
} catch (ArgumentException e) {
    print(e.message)            // Cannot convert a value of type 'Cat' to 'Dog'.
}
if (c is of Dog) { print("never") }     // test first with `is of` when unsure
```

Other conversions are methods: `'A'.ToInt()` (65), `65.ToChar()` (`'A'`), `"x".ToBytes()`, `value.ToString()`. A conversion that `!` cannot do
(`"42"!int`) throws an `ArgumentException`.

## Testing a type: `is of`

```fire
var x = 2.5
print(x is of float)            // True
print(x is of int)              // False
print(new Dog() is of Animal)   // True - the inheritance chain counts
print([1, 2] is of IEnumerable) // True - arrays are enumerable

class Animal { }
class Dog : Animal { }
```

## Bit widths

`int` and `float` can declare a width in brackets directly after the type: `8`, `16`, `32` or `64` (the default). The brackets after the **type** are the width; array
brackets stand after the **name** (`int[8] small`, `int values[10]`).

```fire
int[8] small = 100
int[16] medium = 30000
float[32] single = 0.1
byte b = 200            // byte: 8 bits without sign (0 ... 255)
print(small + medium)
```

The width says how the value is stored natively and how it is marshalled to native functions, and every assignment cuts the value to it: a
variable, a parameter or a field declared `int[8]` wraps an `int` in two's complement, `byte` keeps `0`...`255`, `float[32]` rounds a `float` to
single precision. Only a number of the declared kind is cut (an `int[8]` that gets a `float` or a text keeps it as it is; the type checker
reports that). A byte buffer (`new byte[n]`) cuts its elements to 8 bits as well.

```fire
int[8] small = 300
print(small)            // 44
small = 200
print(small)            // -56
byte b = 300
print(b)                // 44
class Calc { static int Narrow(int[16] x) { return x } }
print(Calc.Narrow(70000))   // 4464
```

A conversion with `!` cuts a value to a width explicitly - `int[N]` is signed (two's complement), `byte` is unsigned:

```fire
print(300!int[8])           // 44
print(200!int[8])           // -56
print(300!byte)             // 44
print((-1)!byte)            // 255
print(5000000000!int[32])   // 705032704
print(0.1!float[32])        // 0.10000000149011612
```

`-1!byte` is `-(1!byte)`: the `!` binds tighter than the minus sign, so write `(-1)!byte`.

A whole program can use 32-bit floats with `#floatwidth 32` (see [Preprocessor](17-preprocessor.md)) - useful for small targets like the ESP32; the VM then
rounds exactly like a 32-bit CPU.

## Exercises

1. Declare an `int`, a `float` and a `string` with `var` and print their types with `is of`.
2. Put `#strict` at the top of a file and write a method without a return type - read the error, then fix it.
3. Convert `9.5` to an `int` with `!` - which way does it round? What does `-9.5!int` give?
4. Why does `float f = 3` print `3`, but `f / 2` print `1.5`?
