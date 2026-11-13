# Operators

## Precedence

From the loosest to the tightest binding:

| Operators | Meaning |
|---|---|
| `=` `+=` `-=` `*=` `/=` `%=` `^=` `&=` `\|=` `#=` `<<=` `>>=` `>>>=` | assignment (right-associative) |
| `cond ? a : b` | conditional (right-associative) |
| `\|\|` `or` | logical or |
| `&&` `and` | logical and |
| `\|` | bitwise or (int only) |
| `#` | bitwise exclusive or (int only) |
| `&` | bitwise and (int only) |
| `==` `!=` `##` | equality (`##` is the same as `!=`) |
| `<` `<=` `>` `>=` `is of` `is in` `is from` `is under` | comparison and tests (see below), left to right |
| `a..b` | range: the whole numbers from `a` to `b` (see below) |
| `<<` `>>` `>>>` | bit shift (int and word only; `>>>` is the logical shift right) |
| `+` `-` | addition, subtraction, joining texts |
| `*` `/` `%` | multiplication, division, remainder |
| `^` | power (right-associative) |
| `-` `!` `~` `*` `&` (prefix) | sign, not, bit inversion, dereference, address-of |
| `x++` `x--` `x!` `x:unit` `x::unit` `.` `?.` `??.` `[]` `()` `f on obj` | postfix, conversions, member, conditional member (see below), index, call, rebinding a lambda |

fire differs from C-style languages in a few places:

- **`^` is the power**, not exclusive or. Exclusive or is `#`. A sign in front binds looser than `^`: `-2^2` is `-4`; the exponent may start with a sign: `2.0^-1` is `0.5`.
- **`##`** is another way to write `!=`.
- **`and`/`or`** are words for `&&`/`||`.
- **`cond ? a : b`** gives `a` when the condition is `true`, otherwise `b`; only that side is evaluated. Inside the middle part a `:` ends it, so
  a unit conversion there needs brackets: `ok ? (x:m) : 0m`.

```fire
print(2 ^ 10)        // 1024
print(-2 ^ 2)        // -4
print(2.0 ^ -1)      // 0.5
float half = 2 ^ -1  // 0.5: the target is a float
print(2 ^ -1)        // 0: two ints, an int power
print(5 # 3)         // 6 (exclusive or)
print(5 & 3)         // 1
print(5 | 3)         // 7
print(~0)            // -1
print(1 << 4)        // 16
print(-16 >> 2)      // -4 (the sign stays)
print(-1 >>> 60)     // 15 (zeros come in at the top)
print(3 ## 4)        // True
print(true and !false)
var n = 7
print(n % 2 == 0 ? "even" : "odd")              // odd
print(n < 0 ? "neg" : n == 0 ? "zero" : "pos")   // pos
```

## Arithmetic

- `+ - * / %` work on `int` and `float`. Two `int`s stay an `int`: `7 / 2` is `3`. With a `float` on one side the result is a `float`: `7.0 / 2` is `3.5`.
- A power of two `int`s is an `int` (`2 ^ 10` is `1024`; a negative exponent works like `/` of ints: `2 ^ -1` is `0`), with a `float` operand a `float`. When the result goes to a `float` - a `float` variable, field, parameter or return value - the float power is used also for two ints: `float f = 2 ^ -1` is `0.5`.
- Division of an `int` by zero throws a `DivideByZeroException`.
- `+` joins texts as soon as one side is a `string`: `"n = " + 3` is `"n = 3"`.
- Numbers with [units](05-units.md) are converted and checked: `500mm + 2m` is `2500mm`, `5mm + 2kg` throws a `UnitMismatchException`.
- `& | # << >> >>> ~` take `int`s (and `word`s) only. The right side of a shift is a count without a unit; it is taken `& 63`.
- `>>` keeps the sign of an `int` (`-16 >> 2` is `-4`); **`>>>` is the logical shift**: the `int` is shifted as the unsigned number of its width -
  64 bits, or 32/16 with [`#intwidth`](17-preprocessor.md) - and zeros come in at the top (`-1 >>> 60` is `15`; with `#intwidth 32` `-1 >>> 28` is
  `15`). A count of the width or more gives `0`. A `word` is unsigned anyway: `>>` and `>>>` are the same there.

```fire
print(7 / 2)         // 3
print(7.0 / 2)       // 3.5
print(7 % 3)         // 1
print("n = " + 3)
try { print(1 / 0) } catch (DivideByZeroException e) { print("caught: " + e.message) }
```

## Assignment

`=` assigns; it is an expression and yields the assigned value. The compound forms `a op= b` mean `a = a op (b)` - the whole right side counts:

```fire
int a = 10
a += 5          // 15
a *= 2 + 1      // 45: a = a * (2 + 1)
a <<= 1         // 90
string s = "fire"
s += "!"
print(a + " " + s)
```

Because an assignment is an expression, it can stand inside another one: `int b = (a = 5) + 1`, `x = y = 7` (right to left),
`while ((i += 2) < 7)`, `print(arr[1] += 4)`. Put it in parentheses - it has the lowest precedence of all operators.

As the condition of `if`, `while`, `for`, `do ... while` or `? :`, a plain assignment is almost always a typo for `==`: `if (ok = true)` is the warning
`TC009`. Write it in its own parentheses if the assignment is meant: `if ((line = reader.ReadLine()) != undefined)` needs none (the condition is the
comparison), `if ((found = Search()))` says it on purpose.

The target of a compound assignment is evaluated twice, so it must not have effects of its own: a name, a member (`c.n += 1`) or an element
(`arr[i + 1] *= 5`) is fine; a call or `++` in the target is the error `E3159`.

`x++` and `x--` add or subtract 1 **in the unit of the variable**: `5cm` becomes `6cm`. As prefix and postfix they work like in C (`++x` yields the new value, `x++` the old one).

```fire
var length = 5cm
length++
print(length)       // 6cm
int i = 1
print(i++)          // 1
print(++i)          // 3
```

## Comparisons

`==` and `!=` compare numbers by value (units are converted: `1m == 100cm`), texts by their characters, `bool` and `char` by value, and objects, arrays and
lambdas **by reference** - unless the class defines `operator==` ([Classes](08-classes.md#operator-overloading)). `<`, `<=`, `>`, `>=` compare numbers (with
units), characters and texts (ordinal, see [Strings](06-strings.md#comparing)).

```fire
print(1m == 100cm)          // True
print("abc" == "abc")       // True
print('a' < 'b')            // True
var p = new Box()
var q = new Box()
print(p == q)               // False - two objects
print(p == p)               // True

class Box { }
```

## Ranges: `a..b`

`a..b` is an array of the whole numbers from `a` to `b`, both included; when `b` is smaller than `a` it counts down. A unit of `a` stays on every number.

```fire
foreach (i in 1..5) { print(i) }        // 1 2 3 4 5 (one per line)
print((5..1).length)                     // 5 - counting down: 5 4 3 2 1
var steps = 1cm..3cm                     // [1cm, 2cm, 3cm]
print(steps[2])                          // 3cm
```

The bounds are `int`s (a `float` is an `ArgumentException`). `..` binds looser than `+`, so `1..n - 1` is `1..(n - 1)`. In an attribute (`[Parameters 1..5]`,
see Testing: forge test (`docs/tools/testing.md`)) a range runs a test once for each of its numbers.

## Logic

`&&`/`and` and `||`/`or` evaluate their right side only when it is needed. `!` negates a `bool`.

```fire
var list = [1, 2, 3]
var i = 5
if (i < list.length && list[i] > 0) { print("never reached: the index check stops it") }
print(!(1 > 2))
```

## Tests: `is of`, `is in`, `is from`, `is under`

| Test | True when |
|---|---|
| `value is of Type` | the value has the type `Type`: a base type, the class or one of its base classes, an interface the class implements (a destroyed object: a `DestroyedException`, except for `is of class` and the base types) |
| `value is in unit` | the value carries a unit of the same dimension as `unit` (`5mm is in m`) |
| `object is from owner` | the object's owner is exactly `owner` (see [Ownership](13-ownership.md)) |
| `object is under owner` | `owner` is anywhere in the chain of owners above the object |
| `x is from local`, `x is from global` | `x` belongs to a scope of the running call / to the global scope (see [Ownership](13-ownership.md#testing-the-owner)) |

```fire
print(5mm is in m)          // True
print(5mm is in kg)         // False
print("x" is of string)     // True
var bag = new Bag()
print(bag.item is from bag) // True

class Item { }
class Bag {
    Item item
    construct() { this.item = new Item() }
}
```

## Conditional access: `?.` and `??.`

`a?.b` uses the member only when `a` is there: is `a` `undefined`, the result is `undefined` and nothing else of the chain runs - no call, no argument
is evaluated. `a??.b` checks the member too: it runs only when `a` is there **and** has `b` (a field, a property or a method; the built-in members such
as `Length` or `TakeTo` count too).

```fire
interface IAnimal { string Name() }
class Dog : IAnimal { string Name() { return "dog" } string Bark() { return "wuff" } }
class Cat : IAnimal { string Name() { return "cat" } }
class Box { int v = 3; Box next }

Box b = new Box()
Box none = undefined
print(b?.v)                     // 3
print(none?.v)                  // undefined
print(none?.next.v)             // undefined - the whole chain is skipped
IAnimal a = new Cat()
print(a??.Bark())               // undefined: a cat cannot bark
any x = 5
print(x??.Length)               // undefined: a number has no Length
```

- The whole rest of the chain belongs to the test: in `a?.b.c(d)` nothing after `a` runs when `a` is undefined. A later `?.` tests again
  (`a?.b?.c`).
- `?.` only tests for `undefined`; the member must be there as with `.` (the type checker says so where the type is known). A destroyed object is not
  `undefined`: `?.` uses it and gets the DestroyedException.
- `??.` tests the member at run time only when the type does not prove it:
  - the type of `a` has the member: only `undefined` is tested (as with `?.`);
  - the type certainly has not - neither the class nor a class derived from it (or implementing the interface): the error `TC025`, the access would
    always be skipped;
  - an interface or a base class whose member a derived class has (`a??.Bark()` above), `any`, a type parameter: tested at run time, a value without
    the member is skipped. A destroyed value has no members.
- `try` goes with it: `try x??.TakeTo(this)` moves `x` when it belongs to the call - and skips a number, which has no `TakeTo` (the collections of the
  prelude take their elements this way). The result is `undefined` when it was skipped.
- `?.` and `??.` read and call; they are not a place to assign to (`a?.b = 1`, `a?.n++` are errors - write an `if`).

## Conversions: `!`, `:` and `::`

- `value!type` converts between numbers and `char`, cuts to a bit width (`300!int[8]`, `x!byte`) and casts objects to a derived class or an
  interface (`animal!Dog`) - see [Types](02-types.md#converting-).
- `value:unit` converts a number into another unit of the same dimension (`2km:m` is `2000m`); `value::unit` only relabels it (`12mm::m` is `12m`). See [Units](05-units.md).

## Exercises

1. Compute `2^0.5` and `(-8)^(1/3)` - why does the second one not give `-2`? (Hint: `1/3` is an `int` division.)
2. Write `if (a ## b)` with `!=` and with `!(a == b)` - which do you find clearer?
3. Use `+=` to build the text `"1,2,3,4,5"` in a loop.
4. Swap two `int`s without a third variable using `#`.

## Bit functions

The class `Bits` (always there, like `Math`) counts, rotates and reverses the bits of an `int` or a `word`, and turns a `float` into its IEEE bits
and back:

```fire
print(Bits.PopCount(0xF0F0))             // 8
print(Bits.LeadingZeros(1))              // 63 (in 64 bits)
print(Bits.TrailingZeros(8))             // 3
print(Bits.RotateLeft(0x81, 1, 8))       // 3 - in 8 bits
print(Bits.ReverseBytes(0x11223344, 32)) // 1144201745 (0x44332211)
print(Bits.Extract(0xABCD, 4, 8))        // 188 (0xBC)
print(Bits.Insert(0xFF00, 0xA, 4, 4))    // 65440 (0xFFA0)
print(Bits.FromFloat32(1.0))             // 1065353216 (0x3F800000)
print(Bits.ToFloat64(Bits.FromFloat64(2.5)))  // 2.5
print(Bits.FromFloat16(0.1))             // 11878 (0x2E66, half precision)
print(Bits.ToFloat16(0x3C00))            // 1
```

| Function | Result |
|---|---|
| `PopCount(x[, width])` | the number of bits that are 1 |
| `LeadingZeros(x[, width])`, `TrailingZeros(x[, width])` | the 0 bits above the highest / below the lowest 1 (the width for 0) |
| `RotateLeft(x, n[, width])`, `RotateRight(x, n[, width])` | the bits rotated by `n` (what leaves on one side comes in on the other) |
| `ReverseBytes(x[, width])` | the bytes in the opposite order (the width a multiple of 8) |
| `ReverseBits(x[, width])` | the bits in the opposite order |
| `Extract(x, start, count)` | `count` bits from bit `start` on (0 is the lowest) as an unsigned number |
| `Insert(x, value, start, count)` | `x` with those bits replaced by the low bits of `value` |
| `FromFloat64(f)`, `ToFloat64(bits)` | the 64 bits of IEEE binary64 (an `int[64]`, also with `#intwidth 32`) and back |
| `FromFloat32(f)`, `ToFloat32(bits)` | the 32 bits of IEEE binary32 (`f` rounded to it; 0 to 2^32 - 1) and back |
| `FromFloat16(f)`, `ToFloat16(bits)` | the 16 bits of IEEE binary16, half precision (rounded to nearest even; largest value 65504, a larger one is infinity) and back |

- **The width**: an `int` is taken in the width of the program's `int` (64; 32 or 16 with `#intwidth`), a `word` in its width (`Word.Bits`);
  `width` (1 to 64) takes the low bits of another width. A result in the program's width is an `int` of the program (`Bits.RotateLeft(-1, 5)` is
  `-1`); one of a smaller width is the unsigned number of its bits (`Bits.RotateRight(1, 1, 32)` is `2147483648`). A number that does not fit the
  program's `int` keeps its 64 bits (like the result of a native). For a `word` the result is a `word` again.
- NaN gives the bits of the standard quiet NaN (`0x7FF8000000000000`, `0x7FC00000`, `0x7E00`); a number with a unit is an `ArgumentException`.
- Together with the [typed reads and writes of a byte buffer](07-arrays-and-lists.md#reading-and-writing-numbers) this is what binary formats need:
  headers, float data of a GPU, half-precision weights, checksums.

