# Arrays and lists

## Arrays

An array holds a fixed number of values. It is created with a literal, with `new Type[size]` or by a declaration with a size:

```fire
var a = [1, 2, 3]           // a literal
var b = new int[3]          // three elements, all undefined
int fixed[4]                // a declared array of four elements
print(a.length)             // 3 (a.Length is the same)
print(a[0])                 // 1
a[1] = 20
print(a[1])                 // 20
print(b[2])                 // undefined
```

- Indexes start at `0`. An index outside `0 .. length - 1` throws an `IndexOutOfBoundsException`.
- New elements are `undefined` until something is assigned - also in an `int` array.
- The size never changes; for a collection that grows, use `List<T>` (below).
- **The brackets stand after the name**: `int values[]`, `string names[10]`. Brackets after the type are a bit width (`int[8] small`, see
  [Types](02-types.md#bit-widths)).
- An array literal may hold different types (`[1, "two", 3.0]`); a typed declaration (`int values[]`) lets the type checker check the elements.

```fire
try {
    var a = [1, 2, 3]
    print(a[5])
} catch (IndexOutOfBoundsException e) {
    print("caught: " + e.message)
}
```

## Several dimensions

An array of arrays: `new int[2][3]`, a literal `[[1, 2], [3]]` or a declaration `int grid[][]`. The inner arrays may have different lengths.

```fire
var grid = new int[2][3]
grid[1][2] = 7
print(grid[1][2])           // 7
print(grid.length)          // 2
print(grid[0].length)       // 3
var rows = [[1, 2], [3]]
print(rows[1][0])           // 3
```

## Arrays as parameters and return values

A parameter is declared with brackets after its name; a return type has empty brackets after the type:

```fire
class Stats {
    static int Sum(int values[]) {
        int total = 0
        foreach (v in values) { total += v }
        return total
    }
    static int[] Squares(int n) {
        var result = new int[n]
        for (int i = 0; i < n; i++) { result[i] = i * i }
        return result
    }
}
print(Stats.Sum([1, 2, 3]))     // 6
print(Stats.Squares(4)[3])      // 9
```

Arrays are **references**: a method that changes an element of its parameter changes the caller's array. A returned array goes to the caller
(see [Ownership](13-ownership.md)).

## Copying

`flat a` makes a new array with the same elements, `copy a` also copies the elements (objects and inner arrays) - see
[Ownership](13-ownership.md#copying-flat-and-copy).

```fire
var a = [1, 2, 3]
var b = a           // the same array
var c = flat a      // a new array
b[0] = 99
print(a[0])         // 99
print(c[0])         // 1
```

## Lists: `List<T>`

`List<T>` from the standard library grows when you add elements:

| Member | Meaning |
|---|---|
| `new List<T>()`, `new List<T>([1, 2])` | an empty list, a list with the elements of an array |
| `count` | the number of elements |
| `Add(value)` | appends at the end |
| `list[i]`, `list[i] = value` | reads/writes an element |
| `Insert(index, value)` | inserts; the elements behind move up |
| `RemoveAt(index)` | removes the element at `index` |
| `Remove(value)` | removes the first equal element; `False` if there is none |
| `IndexOf(value)` | position of the first equal element, `-1` if there is none |
| `Clear()` | removes everything |

```fire
var list = new List<int>()
list.Add(5)
list.Add(7)
list.Insert(0, 1)
print(list.count)           // 3
print(list[0])              // 1
list.RemoveAt(0)
print(list.IndexOf(7))      // 1
foreach (x in list) { print(x) }

var names = new List<string>(["Ada", "Linus"])
names.Add("Grace")
print(names[2])
```

A list owns the objects that are handed over to `Add`: one made in the argument (`list.Add(new Item())`) or given with `take` (`list.Add(take item)`).
An object that `Add` only gets a reference to (`list.Add(item)`) keeps its owner and dies with it (see [Ownership](13-ownership.md)). `List<int>` and `List<string>` are
checked by the type checker: `list.Add("x")` on a `List<int>` is reported. `new List()` without a type argument takes everything.

## Queries: `#import "linq"`

With `#import "linq"`, arrays, lists and every `IEnumerable` get queries in the style of C# LINQ. `Linq.From(x)` starts a query; arrays have the
methods directly.

```fire
#import "linq"
var nums = [5, 3, 8, 1, 9, 2]
var big = Linq.From(nums).Where(n => n > 3).OrderBy(n => n).ToArray()
foreach (n in big) { print(n) }                 // 5 8 9
print(Linq.From(nums).Sum())                    // 28
print(Linq.From(nums).Count(n => n % 2 == 0))   // 2
print(Linq.Range(1, 5).Select(n => n * n).Aggregate(0, (acc, n) => acc + n))   // 55
print(nums.Where((int n) => n < 3).Count())     // 2
```

| Kind | Methods |
|---|---|
| start | `Linq.From(x)`, `Linq.Range(start, count)`, `Linq.Repeat(value, count)` |
| filter | `Where`, `Take`, `Skip`, `TakeWhile`, `SkipWhile`, `Distinct` |
| transform | `Select`, `SelectMany`, `Zip`, `Concat`, `Join` |
| order | `OrderBy`, `OrderByDescending`, `Reverse` |
| result | `ToArray`, `ToList`, `First`, `FirstOrDefault`, `Last`, `ElementAt`, `ForEach` |
| test | `Any`, `All`, `Contains`, `Count` |
| calculate | `Sum`, `Min`, `Max`, `Average`, `Aggregate` |

## Byte buffers

`new byte[n]` is a compact buffer of bytes for binary data (files, network, devices). An element is an `int` from 0 to 255; a larger value is
cut to its lowest 8 bits.

```fire
var bytes = new byte[3]
bytes[0] = 255
bytes[1] = 256
print(bytes[0])             // 255
print(bytes[1])             // 0
print(bytes.length)         // 3
var text = "Hi".ToBytes()
print(text.ToString())      // Hi
```

| Member | Meaning |
|---|---|
| `length` | size in bytes |
| `ToString()`, `ToUnicode()`, `ToUnicode(n)` | read as text (1 or `n` bytes per character) |
| `ToUnicodeChar()`, `ToUnicodeChar(n)` | the first character |
| `littleEndian` | the byte order of the buffer |
| `ToLittleEndian()`, `ToBigEndian()` | a copy in that byte order (the whole buffer is reversed if needed) |

## Exercises

1. Fill an array with the first 10 square numbers and print them backwards.
2. Write `static int Max(int values[])` that returns the largest element; what should it do for an empty array?
3. Build a multiplication table `int table[][]` of 10 × 10 and print the row for 7.
4. Collect all words longer than 3 characters of `"the quick brown fox jumps"` in a `List<string>`.
5. Solve exercise 4 again with `#import "linq"` in one line.
