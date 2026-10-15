# Classes

Classes are the building blocks of every larger fire program: all methods live in classes (there are no free functions), and objects are created
from them with `new`.

## Fields, constructors, methods

```fire
class Animal {
    string name             // a field
    int legs = 4            // a field with an initial value
    static int created = 0  // one value for the whole class

    construct(string name) {
        this.name = name
        Animal.created++
    }

    string Describe() {
        return this.name + " has " + this.legs + " legs"
    }
}

var cat = new Animal("Tom")
print(cat.Describe())
print(cat.name)
print(Animal.created)       // 1
```

- A **field** is declared like a variable: `Type name` or `Type name = value`. A field always has a type (`var` is only for local variables).
  The initial values are set before the constructor runs.
- `construct(...)` is the **constructor**; `new Animal("Tom")` calls it. A class without a constructor gets an empty one.
- A **method** is `ReturnType Name(parameters) { ... }`. A method that returns nothing is declared `undefined`. A method without a return type returns
  `any` (the type checker says so, `TC005`).
- Inside a method `this` is the object. A field can also be written without `this.` (`name` instead of `this.name`) when no local variable has the
  same name.
- `static` fields and methods belong to the class: `Animal.created`, `Calc.Twice(2)`. A static method has no `this`.

## Parameters

```fire
class Calc {
    int Add(int a, int b) { return a + b }
    int Add(int a, int b, int c) { return a + b + c }
    string Greet(string name, string greeting = "Hello") { return greeting + ", " + name }
}
var c = new Calc()
print(c.Add(1, 2))              // 3
print(c.Add(1, 2, 3))           // 6
print(c.Greet("World"))         // Hello, World
print(c.Greet("World", "Hi"))   // Hi, World
```

- **Overloads**: several methods (and constructors) with the same name are allowed when they have a different **number** of parameters. The number of
  arguments decides which one runs.
- **Default values**: `Type name = value` makes a parameter optional. Optional parameters stand at the end.
- A parameter without a type is `any` (`TC004`).
- **Passing**: numbers, `bool`, `char` and strings are copied; objects, arrays and lambdas are passed by reference.
- **`ref`**: a parameter declared `ref` changes the caller's variable itself. The call needs no marker:

```fire
class Util {
    static undefined Swap(ref int a, ref int b) { int t = a; a = b; b = t }
}
int x = 1
int y = 2
Util.Swap(x, y)
print(x + " " + y)      // 2 1
```

A `ref` argument must be a variable, a field or an array element; `ref` parameters cannot have a default value.

## Inheritance

A class can derive from **one** base class: `class Dog : Animal`. It inherits all fields and methods; a method with the same name and number of
parameters **replaces** the inherited one (every method can be overridden, there is no `virtual` keyword).

```fire
class Animal {
    string name
    construct(string name) { this.name = name }
    string Speak() { return "..." }
    string Describe() { return this.name + " says " + this.Speak() }
}
class Dog : Animal {
    construct(string name) : base(name) { }
    string Speak() { return "Woof" }
    string Describe() { return base.Describe() + "!" }
}

Animal a = new Dog("Rex")
print(a.Describe())             // Rex says Woof!
print(a is of Dog)              // True
```

- `construct(...) : base(args)` calls the constructor of the base class first.
- `base.Method(...)` calls the version of the base class.
- Constructors are not inherited: `new Dog(...)` uses only the constructors of `Dog`.
- A variable of the base type can hold an object of a derived class; calls go to the method of the real class.
- Besides the base class, a class can implement several interfaces: `class Dog : Animal, IComparable` (see
  [Interfaces and generics](09-interfaces-and-generics.md)).

## Access: `public`, `private`, `protected`

Members are public by default. `private` members can only be used inside the class itself, `protected` members also in derived classes. Access from
elsewhere throws an `AccessDeniedException` (and the compiler reports it where it knows the class).

```fire
class Account {
    private int balance = 0
    protected string owner = "nobody"
    undefined Deposit(int amount) { this.balance += amount }
    int Balance() { return this.balance }
}
var acc = new Account()
acc.Deposit(50)
print(acc.Balance())            // 50
try {
    print(acc.balance)
} catch (AccessDeniedException e) {
    print(e.message)
}
```

## `readonly`

A `readonly` field can be set only in its initializer or in a constructor of its own class (as `this.field = ...`). The compiler reports
`this.field = ...` in other methods and `obj.field = ...` from anywhere else (when every class that declares a field of this name declares it
`readonly`).

```fire
class Circle {
    readonly float radius
    construct(float r) { this.radius = r }
}
print(new Circle(2.5).radius)
```

## Properties

A property looks like a field but runs code on reading (`get`) and writing (`set`); in `set`, `value` is the new value.

```fire
class Temperature {
    float celsius
    float Fahrenheit {
        get { return this.celsius * 9 / 5 + 32 }
        set { this.celsius = (value - 32) * 5 / 9 }
    }
    bool Freezing { get { return this.celsius <= 0 } }      // read-only
}
var t = new Temperature()
t.Fahrenheit = 212
print(t.celsius)        // 100
print(t.Freezing)       // False
```

**Auto-properties** create the field themselves: `string Name { get; set; }`, with an initial value `int Count { get; set; } = 5`. The hidden field is called `_AutoName`; a get-only auto-property is
set through it in the constructor:

```fire
class Person {
    string Name { get; set; }
    int Age { get; }
    construct(string name, int age) {
        this.Name = name
        this._AutoAge = age
    }
}
var p = new Person("Ada", 36)
p.Name = "Ada L."
print(p.Name + ", " + p.Age)
```

Writing a property without `set` or reading one without `get` is an error. A property must not have the same name as a field.

## Destructors

`destruct()` runs when the object is destroyed - when its owner ends (for a local object: when its block is left) or with `delete`. For a derived
class the destructors of the whole chain run, the derived one first. See [Ownership](13-ownership.md).

```fire
class Handle {
    string n
    construct(string n) { this.n = n; print("open " + n) }
    destruct() { print("close " + this.n) }
}
{
    var r = new Handle("A")
    print("using A")
}
print("after")          // open A, using A, close A, after
```

## Operator overloading

A class can define the operators `+ - * / % ^ & | # << >> == != < <= > >=` and the index `[]`. `this` is the left operand; the right one is the
parameter. Only the left operand decides: `5 + point` does not call the operator of `Point`.

```fire
class Point {
    int x
    int y
    construct(int x, int y) { this.x = x; this.y = y }
    Point operator+(Point o) { return new Point(this.x + o.x, this.y + o.y) }
    bool operator==(any o) { return o is of Point && this.x == o.x && this.y == o.y }
    string ToString() { return $"({x}, {y})" }
}
print(new Point(1, 2) + new Point(3, 4))        // (4, 6)
print(new Point(1, 2) == new Point(1, 2))       // True

class Bag {
    int data[]
    construct() { this.data = new int[3] }
    int operator[](int i) { return this.data[i] }               // read: bag[i]
    undefined operator[](int i, int v) { this.data[i] = v }     // write: bag[i] = v
}
var bag = new Bag()
bag[1] = 4
print(bag[1])
```

Without `operator==`, `==` compares objects by reference (the same instance).

## `ToString()`

`print`, `+` with a string and format strings use the method `string ToString()` of the class if it has one; otherwise an object prints as
`<object Name>`.

## Static members and the top level

Top-level code (outside of classes) runs from the top when the program starts; it can declare variables and use all classes, wherever they stand in the
file. Methods that do not need an object are `static`. A `static` field is shared by all objects of the class.

## Exercises

1. Write a class `Rectangle` with `width` and `height`, a method `Area()` and a property `bool IsSquare`.
2. Derive `Square : Rectangle` with a constructor that takes only one side.
3. Give `Rectangle` an `operator==` and a `ToString()`, then compare two rectangles.
4. Write a class `Counter` with a `private int` and the methods `Increment()` and `Value()`; count how many counters exist with a `static` field.
5. Write a class with a destructor and observe when it runs: in a block, in a loop, in a method.
