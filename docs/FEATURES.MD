# Fire Language Features

A quick overview of the most important language features.

For full details see:

- SPEC.md
- BYTECODE.md
- THREADING_DESIGN.md

---

## Table of Contents

- [Variables & Types](#variables--types)
- [Units](#units)
- [Type & Unit Coercion](#type--unit-coercion)
- [Ownership & Lifetime](#ownership--lifetime)
- [Classes](#classes)
- [Properties](#properties)
- [Operator Overloading](#operator-overloading)
- [Lambdas](#lambdas)
- [Arrays](#arrays)
- [Pointers & Unsafe](#pointers--unsafe)
- [Exceptions & Resume](#exceptions--resume)
- [Interfaces & Foreach](#interfaces--foreach)
- [Generics](#generics)
- [Method overloads & Optional parameters](#method-overloads--optional-parameters)
- [With statement](#with-statement)
- [Extension classes](#extension-classes)
- [Switch statement](#switch-statement)
- [Enums](#enums)
- [Readonly](#readonly)
- [External APIs](#external-apis)
- [Preprocessor Directives](#preprocessor)
- [Actors](#actors)

---

## Variables & Types

```fire
var x = 42
var y = 3.14
var text = "Hello"
var flag = true
```

Built-in types:

- `bool`
- `int`
- `float`
- `char`
- `string`
- `class`
- `undefined`

---

## Units

Units are part of the language.

```fire
var distance = 250cm
var extra = 2m

print(distance + extra)
```

Fire automatically converts compatible units.

```fire
var weight = 5kg
var length = 20cm

print(weight + length)   // Exception
```

Supports SI prefixes and custom dimensions.

```fire
var bandwidth = 100Mbit
var apples = 12apples
```

---

## Type & Unit Coercion

Unit conversion:

```fire
print(1500m : km)
```

Type conversion:

```fire
print(42!float)
```

Automatic conversion:

```fire
var result = a + b:!
```

Fire can infer conversion targets from context.

---

## Ownership & Lifetime

Objects always have exactly one owner.

```fire
var sword = new Sword()

sword.TakeGlobal() //The global scope is the new owner
sword.TakeUpwards() //The parent scope is the new owner
sword.TakeTo(player) //The specified object is the new owner
```

Objects are destroyed automatically when their owner disappears.

```fire
class FileHandle {
    destruct() {
        print("cleanup")
    }
}
```

No garbage collector required.

---

## Classes

```fire
class Animal {
    string name
}

class Dog : Animal {
    Speak() {
        print("Woof")
    }
}
```

Supports:

- Single inheritance, Multiple Interfaces
- Constructors
- Destructors
- Virtual methods
- `this`
- `base`

---

## Properties

```fire
class Circle {
    float radius

    float Diameter {
        get { return radius * 2 }
        set { radius = value / 2 }
    }
}
```

Auto-properties:

```fire
class Person {
    string Name { get; set; }
    int Age { get; }
}
```

---

## Operator Overloading

```fire
class Vector2 {

    operator+(class other) {
        return new Vector2(...)
    }

    operator==(class other) {
        return ...
    }
}
```

Supports:

- Arithmetic operators
- Comparisons
- Bitwise operators
- Index operators

---

## Lambdas

```fire
var doubleIt =
    func(int x) => x * 2

print(doubleIt(21))
```

Bind a custom `this`:

```fire
var callback =
    func() on player => {
        print(this.name)
    }
```

Lambda type annotations:

```fire
int lambda<int, int> add
```

---

## Arrays

```fire
var values = [1, 2, 3]

print(values[0])
```

Fixed-size allocation:

```fire
var buffer = new int[128]
```

Jagged arrays:

```fire
var matrix = new int[10][10]
```

---

## Pointers & Unsafe

```fire
unsafe {

    var ptr = &value

    *ptr = 42

    print(*ptr)
}
```

Supports:

- Address-of (`&`)
- Dereferencing (`*`)
- Pointer arithmetic

---

## Exceptions & Resume

Throwing:

```fire
throw new Exception("Something happened")
```

Catching:

```fire
try {
    RiskyCode()
}
catch (e) {
    print(e.message)
}
```

Resumable exceptions:

```fire
catch (e) {
    e.resume()
}
```

Or continue with a replacement value:

```fire
var x =
    throw new Exception()

catch (e) {
    e.resume(42)
}
```

---

## Interfaces & foreach

```fire
interface ILogger {
    Log(string text)
}
```

Enumeration:

```fire
foreach (item in collection) {
    print(item)
}
```

Duck typing is supported.

Only `GetEnumerator()`, `MoveNext()`
and `GetCurrent()` are required.

---

## Generics

```fire
class Box<T> {
    T value
}
```

Constraints:

```fire
class Container<T>
where T is of Animal
{
}
```

Instantiation:

```fire
var box =
    new Box<int>()
```

---

## Method Overloads & Optional Parameters

Overloads:

```fire
Print(string text)
Print(string text, int count)
```

Optional parameters:

```fire
Greet(
    string name,
    string greeting = "Hello"
)
```

Usage:

```fire
Greet("World")
```

---

## with Statement

```fire
with player {

    .health -= 10

    .gold += 100

    .LevelUp()
}
```

Equivalent to repeatedly writing:

```fire
player.health
player.gold
player.LevelUp()
```

---

## Extension Classes

Extend existing classes without modifying them.

```fire
class extends List {

    bool IsEmpty() {
        return this.Count() == 0
    }
}
```

Similar to reopening classes in Ruby.

---

## switch

```fire
switch (value) {

    case <= 0:
        print("negative")

    case 1:
        print("one")

    case default:
        print("other")
}
```

Comparison operators are supported directly inside cases.

---

## Enums

```fire
enum Color {
    Red,
    Green,
    Blue
}
```

Explicit values:

```fire
enum Status {
    Active = 10,
    Inactive,
    Done
}
```

---

## Readonly

Variables:

```fire
readonly var PI = 3.14159
```

Fields:

```fire
class Circle {

    readonly float radius

    construct(float r) {
        radius = r
    }
}
```

---

## Extern APIs

Declare native functions:

```fire
extern int MessageBoxW(
    int hwnd,
    string text,
    string caption,
    int type
)
```

Dynamic linking:

```fire
#extern "user32.dll"

extern int MessageBoxW(...)
```

---

## Preprocessor

Include files:

```fire
#include "math.fire"
```

Custom directives are supported.

---

## Multithreading

Start a thread:

```fire
fire {

    DoWork()
}
```

Copy ownership into a thread:

```fire
fire taking player {

    player.health -= 10

    sync player
}
```

Synchronization:

```fire
sync player

try sync player
```

Partial synchronization:

```fire
sync flat player
```

Terminate current thread:

```fire
leave
```

Terminate the whole program:

```fire
terminate()
```

---

## Actors

Declare actors:

```fire
actor Logger {

    Log(string text) {
        print(text)
    }
}
```

Use actors from threads:

```fire
fire with logger {

    logger.Log("Hello")
}
```

Process actor messages:

```fire
process logger
```

Non-blocking:

```fire
try process logger
```

Actors communicate via message passing and avoid shared-memory access.
