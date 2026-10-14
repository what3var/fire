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

`value!type` converts a number between `int` and `float`; `value!` without a type takes the type from the other side of the operation.

```fire
print(3.7!int)          // 4 (rounded)
print(5!float / 2)      // 2.5
print(undefined!int)    // 0
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
byte b = 200            // byte is int[8]
print(small + medium)
```

The width says how the value is stored natively and how it is marshalled to native functions. **The VM does not cut a value to the width on
assignment yet** (`int[8] small = 300` keeps `300`); keep the values in range yourself. A byte buffer (`new byte[n]`) does cut its elements to 8 bits.

A whole program can use 32-bit floats with `#floatwidth 32` (see [Preprocessor](17-preprocessor.md)) - useful for small targets like the ESP32; the VM then
rounds exactly like a 32-bit CPU.

## Exercises

1. Declare an `int`, a `float` and a `string` with `var` and print their types with `is of`.
2. Put `#strict` at the top of a file and write a method without a return type - read the error, then fix it.
3. Convert `9.5` to an `int` with `!` - which way does it round? What does `-9.5!int` give?
4. Why does `float f = 3` print `3`, but `f / 2` print `1.5`?
