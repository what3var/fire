# Lambdas

A lambda is a function as a value: it can be stored in a variable or field, passed to a method and called later.

## Writing a lambda

```fire
var twice = (int x) => x * 2            // one expression
print(twice(4))                         // 8

var add = (int a, int b) => a + b
print(add(2, 3))                        // 5

var hello = () => print("hi")           // no parameters
hello()

var sumTo = (int n) => {                // a block: return gives the result
    int sum = 0
    for (int i = 1; i <= n; i++) { sum += i }
    return sum
}
print(sumTo(4))                         // 10

var inc = func (int x) => x + 1         // the long form with 'func'
print(inc(1))
```

- Forms: `x => expr`, `(a, b) => expr`, `() => expr`, each also with `=> { ... }`, and `func (params) => ...`.
- Parameters are written like method parameters: `(int a, int b = 10)` - with types and default values. A parameter without a type is `any`
  (`TC004`) - unless the lambda goes to a typed target, which gives it its types (below).
- A lambda is called like a method: `f(1, 2)`. The wrong number of arguments is an `ArgumentException`.

## Lambda types

The type of a lambda is `lambda`, with the parameter types in angle brackets and the result type in front:

| Type | Meaning |
|---|---|
| `lambda` | a lambda without parameters |
| `lambda<int>` | one `int` parameter |
| `int lambda<int, int>` | two `int` parameters, returns an `int` |

```fire
int lambda<int, int> mul = (a, b) => a * b      // a and b are ints, from the declaration
print(mul(6, 7))                                // 42

class Runner {
    int Apply(int lambda<int> f, int x) { return f(x) }
}
print(new Runner().Apply(n => n * 3, 5))        // 15
```

A lambda that goes to a typed parameter, field or variable takes its parameter types from there. The number of parameters is also checked at run time.
An optional callback is declared `any` (or `lambda` and checked for `undefined`).

## Captured values

A lambda can use local variables of the method it is created in. A word where the variable is used says how the lambda gets it:

```fire
class Box {
    int v = 0
    construct(int v) { this.v = v }
}
class Demo {
    static lambda Counter() {
        var box = new Box(0)
        return () => { (handover box).v++; return box.v }   // the lambda owns the box: every call gets the same one
    }
    static undefined Run() {
        var limit = 3
        var over = (int x) => x > (taking limit)    // a copy of limit: 3
        limit = 10
        print(over(5))                              // True - the lambda still sees 3
        var next = Counter()
        print(next() + " " + next())                // 1 2
    }
}
Demo.Run()
```

| Where it is used | What the lambda gets |
|---|---|
| `(taking x)` | a **copy** of `x` (objects and arrays deep), made when the lambda is made. Every **call** starts with a copy of it of its own: what a call changes is gone when it ends. |
| `(handover x)` | `x` **itself**: it moves into the lambda, `x` is `undefined` outside afterwards and every other reference to it from before is dead. The lambda owns it, every call gets the same instance (lent). `take x` in the body passes it on - after that the lambda has none (`undefined`). |
| no word | like `taking`, with the warning `TC023`: a copy of it - a change does not reach the variable outside, a later change outside is not seen. A copy the body only reads is lent to the calls (no copy per call). |

- One word per variable is enough: every other use of it in the lambda means the same copy (or the moved instance).
- Assigning to a captured variable inside the lambda is allowed: it changes the call's copy.
- There is no loop trap: in `for (...) { fs.Add(() => (taking i)) }` every lambda gets the value of `i` of its round.
- **Global variables** (including the variables of the top-level code) are not captured: the lambda always sees their current value. `(taking g)` gives
  it a copy; `handover` of a global is an error - it belongs to the program.
- `take` into a variable that was captured without a word is the warning `TC024`: what it gets dies with the copy.
- `this` is not captured; bind it with `on` (below).

```fire
var fs = new List<lambda>()
for (int i = 0; i < 3; i++) { fs.Add(() => (taking i)) }
foreach (g in fs) { print(g()) }        // 0 1 2
```

### A lambda has an owner

A lambda belongs to an owner like an object (see [Ownership](13-ownership.md)): the scope of the variable it is assigned to first, the object or the list it is
stored in, or the statement when nobody takes it. What it captured belongs to the lambda and dies with it. When its owner ends, the lambda is destroyed:
calling it is a `DestroyedException` ("Access to a destroyed lambda."). A lambda that has to outlive its scope is passed on with `take` (`list.Add(take f)`,
`this.handler = take f`) or returned. `copy f` gives a lambda with copies of what it captured, `a on obj` (below) as well. A lambda can go to a thread
(`taking`, `handover`); one bound with `on` then still runs where its target lives (see [Threads](15-threads-and-actors.md#lambdas-in-threads)).

## `on`: a lambda with an object

`func (...) on obj => ...` binds an object to the lambda: inside, the members of `obj` can be used without `obj.`:

```fire
class Counter {
    int n = 0
}
var c = new Counter()
var add = func (int by) on c => { n = n + by }
add(2)
add(3)
print(c.n)                      // 5
```

Unknown names in the body are members of the bound object; local variables, parameters and captured values come first.

`lambda on other` gives a **new** lambda with the same body, bound to another object; the old one stays as it was:

```fire
class Counter {
    int n = 0
}
var a = new Counter()
var b = new Counter()
var inc = func () on a => { n++ }
var incB = inc on b
inc()
incB()
incB()
print(a.n + " " + b.n)          // 1 2
```

The value before `on` must be a lambda (otherwise an `ArgumentException`).

## Lambdas as fields: callbacks

```fire
class Button {
    string text
    any onClick                 // a callback; undefined until one is set
    construct(string text) { this.text = text }
    undefined Click() {
        if (this.onClick != undefined) { this.onClick() }
    }
}
var b = new Button("OK")
b.onClick = () => print("clicked")
b.Click()
b.onClick()
```

`obj.name()` calls the method `name`; when no class has a method of that name but a field (or property) `name` can hold a lambda (declared as
`lambda`, `any` or without a type), the lambda in the field is called - also as `onClick()` inside the class.

Lambdas work well with lists and queries (see [Arrays and lists](07-arrays-and-lists.md#queries-import-linq)) and are the bodies of threads
(see [Threads and actors](15-threads-and-actors.md)).

## Exercises

1. Write a method `static int[] Map(int values[], int lambda<int> f)` that applies `f` to every element.
2. Store three lambdas `+`, `-` and `*` in an array and apply each of them to `6` and `3`.
3. Write a method `Counter(int start)` that returns a lambda that counts up from `start` on every call. (Hint: a `taking` copy is new in every call -
   hand an object over to the lambda.)
4. Build a small event: a class with a `List<lambda>` of handlers and a method `Raise()` that calls all of them.
