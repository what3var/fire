# Basics

This page covers how a fire program is written down: statements, comments, variables, literals and the first built-in function, `print`.

## A program

A fire program is a text file (`.fire`). It runs from top to bottom: statements at the top level of the file are the program. Classes, interfaces, enums and
namespaces can stand anywhere in the file - they are known everywhere, also above their declaration.

```fire
print("Hello, fire!")

var greeting = Greeter.Make("World")
print(greeting)

class Greeter {
    static string Make(string name) { return "Hello, " + name + "!" }
}
```

There are no free functions: a function is always a method of a class (a `static` method if it needs no object, see [Classes](08-classes.md)) or a
[lambda](12-lambdas.md).

## Statements

Two statements are separated by a **line break** or a **semicolon**. Without either, it is an error - the parser never guesses where a statement ends.

```fire
var a = 1
var b = 2; var c = 3
print(a + b + c)
```

A statement continues on the next line when the line ends with an operator or a `.`, when the next line starts with a `.`, `?.`, a binary operator
(`+ - / % ^ | == != < <= > >= && || and or`) or the `?` of a condition, or while a `(` or `[` is open. Not at the start of a line: `++`/`--` (a statement
of their own), `*` (`*p = 1` writes through a pointer), `&`, `!`, `~`, `(`, `[`, `#` and assignments - they start a new statement. So a line that starts
with `-` or `+` belongs to the statement before (`int y = 10` and `-1` on the next line is `y = 9`). In a `with` block a `.` at the start of a line is the member of
the block (`.x = 10`) and starts a statement - a chain there goes on with the `.` at the end of the line.

```fire
var total = 1 +
    2 +
    3
print(Math2.Max(total,
                 10))
bool ok = total > 3
    && total < 10
    || total == 100
var names = new List<string>(["b", "a"])
var first = names
    .Count                  // fluent: a `.` at the start of the line goes on

class Math2 {
    static int Max(int a, int b) { if (a > b) { return a } return b }
}
```

A lone `_` at the end of a line continues the statement explicitly (anywhere else `_` is an ordinary name):

```fire
var sum = 1 _
    + 2 + 3
print(sum)      // 6
```

A block `{ ... }` groups statements. The end of a block also ends a statement, so `{ return x }` needs no separator; a `;` after a block is allowed
as well (`foreach (x in a) { s += x }; print(s)` on one line).

## Comments

```fire
// a line comment
/* a block comment,
   over several lines */
/// a documentation comment: directly above a class, field, property or method it is shown by the editor as a tooltip
print("comments are ignored")
```

Documentation comments (`///`) understand plain text and the tags `<summary>`, `<param name="...">`, `<returns>` and `<remarks>` - see
[Preprocessor and directives](17-preprocessor.md#documentation-comments).

## Variables

A variable is declared with `var` (the type comes from the value) or with a type:

```fire
var name = "fire"       // string
var count = 3           // int
float ratio = 2.5
bool ready = true
int width               // a typed declaration may leave out the value: it starts as undefined
print(name + " " + count + " " + ratio + " " + ready + " " + width)
```

- `var` needs a value (`var x` alone is the error `E4058`); the type of a variable never changes afterwards. `var count = 3` is an `int` for good:
  `count += 0.5` is a type conflict (`TC001`; at run time an `ArgumentException`) - write `var count = 3.` (or `3.0`) for a `float`, or convert
  (`count = (count + 0.5)!int`).
- The value must say the type. `var x = undefined` takes the type of its first assignment (warning `TC020`: say it in the declaration,
  `Dog x = undefined`); a value whose type the compiler does not know (`var x = someAny[i]`) or `undefined` that nothing gives a type is `TC019` - an
  error in strict mode, in lax mode a loud warning and the variable is `any`. Write `any x = ...` when every kind of value is really meant.
- Values of related types join: `var x = c ? 1 : 2.5` is a `float`, `var a = c ? new Dog() : new Cat()` the nearest common base class or interface
  (`Animal`); unrelated ones (`c ? 1 : "one"`, `[1, "one"]`) make `any` with the warning `TC020`.
- The type stands **before** the name (`int x`). A `:` after the name fixes a [unit](05-units.md), not a type: `var len : mm = 5`.
- A variable lives in the block it is declared in. An inner block may declare a name of an outer block again; inside, the inner one counts.
- Variables at the top level of the program are **global**: they are visible in every method and lambda.
- `readonly` makes a variable a constant: it needs a value and cannot be assigned again (see below).

```fire
readonly var Pi = 3.14159
readonly int MaxItems = 100
print(Pi * 2)
// Pi = 3   would be a compile error
```

## Literals

| Literal | Examples | Type |
|---|---|---|
| whole number | `42`, `-7`, `0x1F`, `0b1010`, `1_000_000` (`_` between digits separates groups) | `int` |
| decimal number | `2.5`, `0.001` (a leading digit is needed: `0.5`, not `.5`), `3.` (the digits after the point may be left out: `var x = 0.` is a `float`, `var x = 0` an `int`), `1e3`, `2.5E-3` (scientific notation: always a `float`) | `float` |
| number with unit | `5mm`, `2.5kg`, `500ms`, `0xFFmm`, `1m3` (a power as digits: `m^3`) | `int`/`float` with a unit |
| text | `"Hello"`, `"line\n"` | `string` |
| interpolated text | `$"x = {x}"` | `string` |
| character | `'a'`, `'\n'` | `char` |
| truth value | `true`, `false` | `bool` |
| nothing | `undefined` | `undefined` |
| array | `[1, 2, 3]` | array |

Escape sequences in texts and characters: `\n` (line feed), `\t` (tab), `\r`, `\\`, `\'`, `\"`, `\0`.

```fire
print("a tab:\there, a quote: \" and a backslash: \\")
print('x')
print(0xFF + 0b11)      // 258
print(1_000_000 + 0xFF_FF)   // 1065535
```

`0x`/`0b` only start a hexadecimal/binary number when a valid digit follows: `0b` alone is the number `0` with the unit `b` (bit). The syntax of
the number wins over a unit: `1e3` is the float `1000` (not `1` with the unit `e^3`), `0x1F` is `31`.

## Output: `print`

`print(value)` writes one value and a line break. Everything can be printed: numbers (with their unit), texts, `True`/`False`, `undefined`,
objects (their `ToString()` method if they have one, otherwise `<object Name>`), arrays (`<array>`).

```fire
print(3)
print(2.5kg)
print(1 == 1)
print(undefined)
print(new Point(1, 2))

class Point {
    int x
    int y
    construct(int x, int y) { this.x = x; this.y = y }
    string ToString() { return "(" + this.x + ", " + this.y + ")" }
}
```

Texts are joined with `+`; as soon as one side is a `string`, the other side is converted (`"n = " + 3` is `"n = 3"`).

## Ending the program

The program ends after its last statement. `terminate()` ends it at once (also from a thread), `terminate(value)` passes an exit value
(a whole number becomes the exit code of the process). See [Threads](15-threads-and-actors.md#ending-leave-and-terminate).

## Exercises

1. Write a program that prints your name and your age in one line, using `+`.
2. Declare a `readonly` constant `Seconds = 60` and print how many seconds an hour has.
3. Print the numbers `0x10`, `0b10` and `10` - what are they in decimal?
4. Write a statement that spans three lines using a trailing operator, and another one using `_`.
