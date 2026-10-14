# Strings and characters

A `string` is an **immutable** text of 16-bit characters; a `char` is one such character. Strings are values: assigning one copies it, and no method changes
a string - every method returns a new one.

## Literals

```fire
string s = "Hello"
char c = 'H'
print("Quote: \"x\", tab:\there")
print("line1\nline2")
```

Escapes in `"..."` and `'...'`: `\n` (new line), `\r`, `\t`, `\0`, `\\`, `\"`, `\'` (see [Basics](01-basics.md#literals)).

## Joining

`+` joins texts. As soon as one side is a `string`, the other side is converted to text - from left to right:

```fire
print("abc" + 1 + 2)    // abc12
print(1 + 2 + "abc")    // 3abc - 1 + 2 is calculated first
var n = 5
string msg = "n = " + n
msg += "!"
print(msg)
```

Every value can be converted: numbers (with their unit: `"" + 5mm` is `"5mm"`), `bool` (`True`/`False`), `char`, `undefined`, objects (their
`ToString()` if the class has one).

## Format strings: `$"..."`

A string with a leading `$` may contain `{expression}` and `{expression:format}`:

```fire
var name = "World"
print($"Hello, {name}!")
print($"{255:X} {5:B} {5:D3} {3.14159:F2} {1234.5:E2}")    // FF 101 005 3.14 1.23E+003
print($"{{braces}}")                                         // {braces}
var d = 2km
print($"{(d:m)}")                                            // 2000m
```

| Format | Meaning | For | Example |
|---|---|---|---|
| `X`, `x` | hexadecimal (upper/lower case) | `int` | `{255:X}` → `FF` |
| `B` | binary | `int` | `{5:B}` → `101` |
| `Dn` | decimal with at least `n` digits | `int` | `{5:D3}` → `005` |
| `Fn` | fixed point with `n` decimals | `int`, `float` | `{3.14159:F2}` → `3.14` |
| `En` | scientific | `int`, `float` | `{1234.5:E2}` → `1.23E+003` |

Inside the braces any expression is allowed. A `:` at the top level starts the format - a unit conversion needs parentheses: `{(d:m)}`.
`{{` and `}}` write a single brace.

## Length and characters

```fire
string s = "Hello, World"
print(s.Length)         // 12 (s.length is the same)
print(s[0])             // H - a char
foreach (c in "abc") { print(c) }
```

`s[i]` reads a character; strings cannot be changed, so `s[i] = 'x'` is an error. An index outside the text throws an
`IndexOutOfBoundsException`.

## Methods of `string`

| Method | Result |
|---|---|
| `IndexOf(x)`, `IndexOf(x, start)` | first position of `x` (a string or a char), `-1` if not found |
| `LastIndexOf(x)`, `LastIndexOf(x, start)` | last position; with `start` the search goes backwards from there |
| `Substring(start)`, `Substring(start, count)` | part of the text |
| `CharAt(i)` | the same as `s[i]` |
| `Contains(x)`, `StartsWith(x)`, `EndsWith(x)` | `bool` |
| `ToUpper()`, `ToLower()` | upper/lower case |
| `Trim()`, `TrimStart()`, `TrimEnd()` | remove spaces, tabs and line breaks at the ends |
| `Replace(old, new)` | replaces every occurrence |
| `Split(separator)` | `string[]` |
| `PadLeft(width)`, `PadLeft(width, fill)`, `PadRight(...)` | fill up to a minimum width |
| `ToBytes()` | a byte buffer, one byte per character |
| `ToUnicode(n)` | a byte buffer, `n` bytes per character |

Searching and comparing are **ordinal**: case matters, there is no culture.

```fire
string s = "Hello, World, again"
print(s.IndexOf("o"))           // 4
print(s.IndexOf('o', 5))        // 8
print(s.LastIndexOf(","))       // 12
print(s.Substring(7, 5))        // World
print(s.Substring(14))          // again
print(s.Contains("World"))      // True
print("  x  ".Trim() + "|")     // x|
print("a-b-c".Replace("-", "+"))
foreach (part in "a,b,c".Split(",")) { print(part) }
print("7".PadLeft(3, '0'))      // 007
```

## Methods of `char`

| Method | Result |
|---|---|
| `IsDigit()`, `IsLetter()`, `IsLetterOrDigit()`, `IsWhiteSpace()`, `IsUpper()`, `IsLower()` | `bool` |
| `ToUpper()`, `ToLower()` | `char` |
| `ToInt()` | the number of the character (`'A'.ToInt()` is `65`) |
| `ToString()` | a string with this character |
| `ToByte()`, `ToUnicode(n)` | as byte / as byte buffer |

`int.ToChar()` goes the other way: `66.ToChar()` is `'B'`.

```fire
print('a'.IsLetter())       // True
print('A'.ToLower())        // a
print('A'.ToInt())          // 65
print(66.ToChar())          // B
print('a' < 'b')            // True - characters can be compared
```

## Comparing

`==` and `!=` compare texts character by character. `<`, `>`, `<=`, `>=` work on `char`s, but not on whole strings (that is an
`ArgumentException`) - compare character by character if you need an order.

## Bytes and text

`ToBytes()` turns a text into a byte buffer (one byte per character), `buffer.ToString()` back. `ToUnicode(2)` uses two bytes per character and
`buffer.ToUnicode()` reads them back. See [Arrays and lists](07-arrays-and-lists.md#byte-buffers).

```fire
var buf = "Hi".ToBytes()
print(buf.length)           // 2
print(buf[0])               // 72
print(buf.ToString())       // Hi
```

## Your own string methods

`class extends string { ... }` adds methods to every string (see [Extensions](11-extensions.md)):

```fire
class extends string {
    string Shout() { return this.ToUpper() + "!" }
}
print("hello".Shout())      // HELLO!
```

## Exercises

1. Count the vowels of a text with `foreach` and `Contains`.
2. Reverse a string (build a new one from the last character to the first).
3. Split `"3,5,8"` with `Split` and print every part padded to width 4 with `PadLeft`.
4. Print a table of the numbers 0 to 15 in decimal, hexadecimal and binary with a format string.
5. Write `class extends string` with a method `bool IsPalindrome()`.
