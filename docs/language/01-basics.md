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

A statement continues on the next line when the line ends with an operator, or while a `(` or `[` is open:

```fire
var total = 1 +
    2 +
    3
print(Math2.Max(total,
                 10))

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

A block `{ ... }` groups statements. The end of a block also ends a statement, so `{ return x }` needs no separator.

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

- `var` needs a value (`var x` alone is the error `E4058`); the type of a variable never changes afterwards.
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
| whole number | `42`, `-7`, `0x1F`, `0b1010` (no digit separators: `1000`, not `1_000`) | `int` |
| decimal number | `2.5`, `0.001` (a leading digit is needed: `0.5`, not `.5`) | `float` |
| number with unit | `5mm`, `2.5kg`, `500ms`, `0xFFmm` | `int`/`float` with a unit |
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
```

`0x`/`0b` only start a hexadecimal/binary number when a valid digit follows: `0b` alone is the number `0` with the unit `b` (bit).

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
