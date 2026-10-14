# Control flow

## `if` / `else`

The condition stands in parentheses and must be a `bool`. A number is not a condition: write `if (n != 0)`, not `if (n)` (the type checker reports it, at run time it is an `ArgumentException`).

```fire
var n = 3
if (n < 2) {
    print("small")
} else if (n < 5) {
    print("medium")
} else {
    print("large")
}
```

- Braces are optional for a single statement: `if (n == 3) print("three")`.
- `else` may stand on the line after the closing `}`.
- There is no `?:` operator; use `if`/`else` or a small method.

## `while`

```fire
var w = 10
while (w > 0) {
    w -= 3
}
print(w)        // -2
```

The condition is checked before every round.

## `do` ... `while`

The body runs first and the condition is checked after every round, so the body runs at least once. `continue` jumps to the condition.

```fire
var n = 0
do {
    n++
    print(n)        // 1 2 3
} while (n < 3)
do print("once") while (false)
```

## `for`

`for (initializer; condition; step)` works like in C. The initializer can declare a variable (`int i = 0`, `var i = 0`) or assign an existing one;
every part may be left out (`for (;;)` loops until `break`).

```fire
for (int i = 0; i < 3; i++) {
    print(i)
}

var list = [3, 1, 2]
for (int i = list.length - 1; i >= 0; i--) {
    print(list[i])
}

var n = 0
for (;;) {
    n++
    if (n > 3) { break }
}
print(n)        // 4
```

A variable declared in the initializer lives only inside the loop. Only one variable can be declared there.

## `foreach`

`foreach (name in collection)` runs once for every element. The name is new for each round; it takes the type of the elements (do not write a type
before it).

```fire
foreach (x in [1, 2, 3]) {
    print(x)
}
foreach (c in "abc") {      // a string gives its characters
    print(c)
}
var total = 0
foreach (row in [[1, 2], [3]]) {
    foreach (v in row) { total += v }
}
print(total)                // 6
```

`foreach` works on arrays, byte buffers, strings, `List<T>`, every class that implements `IEnumerable` and every object with a method
`GetEnumerator()` whose result has `MoveNext()` and `GetCurrent()` (see [Interfaces and generics](09-interfaces-and-generics.md#ienumerable)).

## `break` and `continue`

`break` ends the innermost loop at once; `continue` skips the rest of the round and goes on with the next one - in a `for` loop **after** the step.
Both may stand deep inside `if`s and blocks; leaving a `try` runs its `finally` first.

```fire
var i = 0
while (i < 10) {
    i++
    if (i == 3) { continue }
    if (i == 6) { break }
    print(i)        // 1 2 4 5
}
```

- Outside a loop they are an error. A lambda does not see the loop around it.
- **`break 2`** leaves two loops at once (`break 3` three, ...); **`continue 2`** ends the inner loop and goes on with the next round of the outer one:

```fire
foreach (row in [[1, 2], [3, -1], [5, 6]]) {
    foreach (cell in row) {
        if (cell < 0) { break 2 }      // stops both loops
        print(cell)                    // 1 2 3
    }
}
```

  Leaving a `try` with a `finally` this way is not supported yet (leave the loops one by one there).
- Inside a `switch`, `break` ends the **branch**, not the loop around the `switch` (see below).

## `switch`

`switch` compares one value with a list of cases. A case is a value (compared with `==`) or a comparison operator and a value (`< 3`, `>= 10`,
`!= 0`). The cases are checked from the top; **the first one that matches runs, and only that one** - there is no fall-through.
`case default:` matches when no other case did.

```fire
var x = 5
switch (x) {
    case < 3:
        print("small")
    case 5:
        print("five")
    case <= 10:
        print("up to ten")    // not reached for 5: the first match wins
    case default:
        print("large")
}

var s = "b"
switch (s) {
    case "a":
        print("A")
    case "b":
        print("B")
}
```

- The value after `switch` is evaluated once.
- A case starts on a line of its own; the statements of the branch follow on the next lines.
- `break` at the end of a branch is allowed but not needed. If it is used, it must be the last statement of the branch.
- A case can list several values and comparisons, separated by commas: `case 5, 6:`, `case < 0, > 100:` - it matches when one of them does.
- Strings, numbers (also with units: `case < 1m:`), characters and enum values can be compared.

## `with`

`with` names an object once; inside the block, an expression that starts with `.` refers to it.

```fire
var p = new Point()
with p {
    .x = 3
    .y = 4
    print(.x + .y)      // 7
}

class Point { int x; int y }
```

The expression after `with` is evaluated once. `with` blocks can be nested; a `.` always means the innermost one.

## Blocks and scopes

Every `{ }` block, every round of a loop and every method has its own scope: a variable declared inside is not visible outside, and objects that
belong to the scope are destroyed when it is left (see [Ownership](13-ownership.md)).

```fire
var outer = 1
{
    var inner = 2
    print(outer + inner)
}
// inner does not exist here
```

## Leaving the program

- `return` leaves a method or a lambda (with a value: `return x`); in the top-level code it is an error.
- `leave` ends the current thread, `terminate` all threads (see [Threads and actors](15-threads-and-actors.md#ending-leave-and-terminate)).
- An exception that nobody catches stops the program (see [Exceptions](14-exceptions.md)).

## Exercises

1. Print the numbers from 1 to 100; for multiples of 3 print `Fizz`, of 5 `Buzz`, of both `FizzBuzz`.
2. Write a `switch` that turns a school grade (1-6) into a word; use `case default:` for invalid numbers.
3. Sum all even numbers of an array with `foreach` and `continue`.
4. Find the first pair `(a, b)` with `a * b == 36` and `a < b` in two nested `for` loops and stop both loops when you have it - without labels.
5. Rewrite a `for` loop as a `while` loop. Where must the step go so that `continue` still works?
