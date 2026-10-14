# Operators

## Precedence

From the loosest to the tightest binding:

| Operators | Meaning |
|---|---|
| `=` `+=` `-=` `*=` `/=` `%=` `^=` `&=` `\|=` `#=` `<<=` `>>=` | assignment (right-associative) |
| `\|\|` `or` | logical or |
| `&&` `and` | logical and |
| `\|` | bitwise or (int only) |
| `#` | bitwise exclusive or (int only) |
| `&` | bitwise and (int only) |
| `==` `!=` `##` | equality (`##` is the same as `!=`) |
| `<` `<=` `>` `>=` `is of` `is in` `is from` `is under` | comparison and tests (see below), left to right |
| `<<` `>>` | bit shift (int only) |
| `+` `-` | addition, subtraction, joining texts |
| `*` `/` `%` | multiplication, division, remainder |
| `^` | power (right-associative) |
| `-` `!` `~` `*` `&` (prefix) | sign, not, bit inversion, dereference, address-of |
| `x++` `x--` `x!` `x:unit` `x::unit` `.` `[]` `()` | postfix, conversions, member, index, call |

fire differs from C-style languages in a few places:

- **`^` is the power**, not exclusive or. Exclusive or is `#`. A sign in front binds looser than `^`: `-2^2` is `-4`; the exponent may start with a sign: `2^-1` is `0.5`.
- **`##`** is another way to write `!=`.
- **`and`/`or`** are words for `&&`/`||`.
- There is **no `?:` operator** - use `if`/`else`.

```fire
print(2 ^ 10)        // 1024
print(-2 ^ 2)        // -4
print(2 ^ -1)        // 0.5
print(5 # 3)         // 6 (exclusive or)
print(5 & 3)         // 1
print(5 | 3)         // 7
print(~0)            // -1
print(1 << 4)        // 16
print(3 ## 4)        // True
print(true and !false)
```

## Arithmetic

- `+ - * / %` work on `int` and `float`. Two `int`s stay an `int`: `7 / 2` is `3`. With a `float` on one side the result is a `float`: `7.0 / 2` is `3.5`.
- `int ^ int` with an exponent of 0 or more stays an `int`; otherwise the power is computed as `float`.
- Division of an `int` by zero throws a `DivideByZeroException`.
- `+` joins texts as soon as one side is a `string`: `"n = " + 3` is `"n = 3"`.
- Numbers with [units](05-units.md) are converted and checked: `500mm + 2m` is `2500mm`, `5mm + 2kg` throws a `UnitMismatchException`.
- `& | # << >> ~` take `int`s only. The right side of `<<`/`>>` is a count without a unit.

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
units) and characters.

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
| `value is of Type` | the value has the type `Type`: a base type, the class or one of its base classes, an interface the class implements |
| `value is in unit` | the value carries a unit of the same dimension as `unit` (`5mm is in m`) |
| `object is from owner` | the object's owner is exactly `owner` (see [Ownership](13-ownership.md)) |
| `object is under owner` | `owner` is anywhere in the chain of owners above the object |

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

## Conversions: `!`, `:` and `::`

- `value!type` converts between `int` and `float` (see [Types](02-types.md#converting-)).
- `value:unit` converts a number into another unit of the same dimension (`2km:m` is `2000m`); `value::unit` only relabels it (`12mm::m` is `12m`). See [Units](05-units.md).

## Exercises

1. Compute `2^0.5` and `(-8)^(1/3)` - why does the second one not give `-2`? (Hint: `1/3` is an `int` division.)
2. Write `if (a ## b)` with `!=` and with `!(a == b)` - which do you find clearer?
3. Use `+=` to build the text `"1,2,3,4,5"` in a loop.
4. Swap two `int`s without a third variable using `#`.
