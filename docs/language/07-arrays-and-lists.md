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
- An array literal may hold different types (`[1, "two", 3.0]`); a typed declaration (`int values[]`) makes a **typed array** (below).
- A unit at the declaration (`float:km/h speeds[]`, `int sizes[3] : mm`) converts every element stored into the array (see
  [Units in declarations](05-units.md#units-in-declarations)).

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

## Typed arrays

An array stored in a variable or a field declared with the element type `int`, `int[8|16|32|64]`, `float`, `float[8|16|32|64]`, `bool`, `char` or
`byte` becomes a **typed array**: the array itself knows its element type, so every write keeps it - through any reference, also `ref a[i]` and `a[i]++`:

```fire
int[8] small[4]                 // four 8-bit ints, made right here
small[0] = 300
print(small[0])                 // 44 - cut to 8 bits like an int[8] variable
var alias = small               // the same array
alias[1] = 129
print(small[1])                 // -127
byte raw[2]
raw[0] = 263
print(raw[0])                   // 7 (0 .. 255)
float[32] f[] = [1, 2.5]
print(f[0])                     // 1 - the int became a float
float:cm sizes[] = [1cm, 2m]
print(sizes[1])                 // 200cm - every element is converted to the unit
try {
    any v = "x"
    small[3] = v
} catch (ArgumentException e) {
    print(e.message)            // An element of an array of int[8] must be an int (or undefined), not String.
}
```

- `undefined` fits every element type (it is what a new array holds). Any other value is an `ArgumentException`, a number with another unit a
  `UnitMismatchException`; the type checker reports both already where it can see them (`TC001`, `TC006`).
- An assignment yields the stored value: `print(a[0] = 300)` prints `44` for an `int[8]` array.
- The array becomes typed where it is stored: the declaration's initializer, a declaration with a size (`int a[10]`), a later assignment to the
  variable or field. Its elements are converted then. An array typed already for another element type is an `ArgumentException`.
- Parameters do not type what they receive. Copies (`copy`, `flat`, [`taking`](15-threads-and-actors.md)) of a typed array are typed.
- `int m[3][4]` types the inner arrays. `var`, `any`, strings, classes and pointers declare no element type: such an array takes any value.
- **A declaration with a size makes the array** - a local or global where it is declared, a field together with its object
  (`class Box { int data[3] }`: `new Box().data.length` is 3). Without a size (`int arr[]`) the variable or field stays `undefined` until an array is assigned.
- Natively a typed array is **packed**: one C number per element (`int8_t` for `int[8]`, `float` for `float[32]`, one byte for `bool`) instead of a
  16-byte value - `int[8] a[100000]` needs 100 KB instead of 1.6 MB.

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
| `new List<T>()`, `new List<T>([1, 2])`, `new List<T>(Owns.Values)` | an empty list, a list with the elements of an array, a list that owns its elements (below) |
| `Count` | the number of elements (a read-only property) |
| `Add(value)` | appends at the end |
| `list[i]`, `list[i] = value` | reads/writes an element |
| `Insert(index, value)` | inserts; the elements behind move up |
| `RemoveAt(index)` | removes the element at `index` |
| `TakeAt(index)` | removes the element at `index` and gives it to the caller (with its ownership, if the list owned it) |
| `Remove(value)` | removes the first equal element; `False` if there is none |
| `IndexOf(value)` | position of the first equal element, `-1` if there is none |
| `Clear()` | removes everything |

```fire
var list = new List<int>()
list.Add(5)
list.Add(7)
list.Insert(0, 1)
print(list.Count)           // 3
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
checked by the type checker: `list.Add("x")` on a `List<int>` is reported. `new List()` without a type argument takes everything. A `List<int>`,
`List<float>`, `List<bool>` or `List<char>` keeps its elements in a typed array (like `int a[]`: raw numbers, natively 8 bytes or less each) - inside a
generic class `new T[n]` makes such an array for the type argument it runs with.

## Dictionaries and sets: `Dictionary<K, V>`, `HashSet<T>`

`Dictionary<K, V>` maps keys to values, `HashSet<T>` holds each value once. Both keep the order in which the keys came in (`foreach`, `Keys`, `Values`).
Like a list, they keep keys and values of `int`, `float`, `bool` or `char` in typed arrays (`Dictionary<int, float>`: raw numbers); their table itself
(hashes, chains) is typed too.

| `Dictionary<K, V>` | Meaning |
|---|---|
| `new Dictionary<K, V>()`, `new Dictionary<K, V>(Owns.Values)` | an empty dictionary; one that owns its values (below) |
| `dict[key] = value` | adds the key or replaces its value |
| `dict[key]` | the value; an `ArgumentException` if the key is not there |
| `Add(key, value)` | adds a new key; an `ArgumentException` if it is there already |
| `Get(key, fallback)` | the value, or `fallback` (default `undefined`) |
| `TryGet(key, value)` | `True` and the value in the variable `value` (a `ref` parameter), or `False` |
| `ContainsKey(key)`, `ContainsValue(value)` | whether it is there |
| `Remove(key)` | removes key and value; `False` if the key is not there |
| `Take(key)` | removes the key and gives its value to the caller |
| `Keys`, `Values` | new lists of the keys / values |
| `Count`, `Clear()` | the number of entries; removes everything |
| `foreach (pair in dict)` | `KeyValuePair`s with `Key` and `Value` |

```fire
var ages = new Dictionary<string, int>()
ages["Ada"] = 36
ages["Linus"] = 28
ages.Add("Grace", 85)
print(ages["Ada"])                  // 36
print(ages.Get("Bob", -1))          // -1
int age = 0
if (ages.TryGet("Linus", age)) { print(age) }   // 28
foreach (pair in ages) { print(pair.Key + ": " + pair.Value) }

var seen = new HashSet<string>()
print(seen.Add("x"))                // True
print(seen.Add("x"))                // False - already there
print(seen.Contains("x"))           // True
```

`HashSet<T>` has `Add` (`False` if an equal value is there), `Contains`, `Remove`, `Take`, `Clear`, `Count` and `foreach`.

**Equal keys** are those that `==` finds equal: strings by their text, numbers by their value (`1` and `1.0`, also `1km` and `1000m`), chars, bools,
enum values; objects only themselves. A class whose objects should be equal by their content implements `IEquatable` - `bool Equals(any other)` and
`int GetHashCode()`, where equal objects give the same hash:

```fire
class Point : IEquatable {
    int x
    int y
    construct(int x, int y) { this.x = x; this.y = y }
    bool Equals(any other) { return other is of Point && other.x == this.x && other.y == this.y }
    int GetHashCode() { return this.x * 31 + this.y }
}
var names = new Dictionary<Point, string>()
names[new Point(1, 2)] = "home"
print(names[new Point(1, 2)])       // home
```

## Containers and ownership: `Owns`

A list, a dictionary and a set are an owner like any object (see [Ownership](13-ownership.md)). The flag enum `Owns` in the constructor says what they
own of what comes in:

| `Owns` | The container owns |
|---|---|
| `Owns.Explicit` (default) | only what is handed over to it: an object made in the call (`dict.Add("a", new Node())`) or given with `take` (`dict.Add("a", take node)`) |
| `Owns.Values` | every value (of a list or set: every element), whoever owned it before |
| `Owns.Keys` | every key of a dictionary |
| `Owns.All` | keys and values |

What the container owns because of `Keys`, `Values` or `All` is **destroyed when it falls out**: `Remove`, overwriting (`dict[k] = other`,
`list[i] = other`), `RemoveAt`, `Clear`. `Take` / `TakeAt` take a value out instead and give it to the caller, who owns it then:

```fire
class Node {
    string n
    construct(string n) { this.n = n }
    destruct() { print("~" + this.n) }
}
var nodes = new Dictionary<string, Node>(Owns.Values)
nodes["a"] = new Node("a")
nodes["a"] = new Node("b")          // ~a - the old value is destroyed
var kept = nodes.Take("a")          // out of the dictionary, it belongs to `kept`'s scope now
nodes["c"] = new Node("c")
nodes.Clear()                       // ~c
print(kept.n)                       // b
```

With `Owns.Explicit` nothing is destroyed when it falls out: what the container owns stays alive until the container itself ends.

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

### `byte` declarations are buffers

A variable, a field or a global declared with the element type `byte` holds a byte buffer, not an array:

```fire
byte header[4]              // the same as new byte[4]: four zeros
byte magic[] = [0x89, 80, 78, 71]
print(magic)                // <buffer 4 bytes>
print(magic.length)         // 4
print(magic[1])             // 80
foreach (b in header) { print(b) }
var numbers = [1, 2, 300]
magic = numbers             // a new buffer with the elements: 1, 2, 44 (numbers stays an array)
class Packet {
    byte data[16]           // made with the object
    byte extra[]            // undefined until something is assigned
}
```

- An array stored in a `byte` declaration (the initializer, a later assignment) becomes a **new buffer** with its elements: an `int` is cut to
  8 bits (`300` becomes 44), `undefined` becomes 0, anything else is an `ArgumentException`. `byte grid[3][4]` is an array of three buffers.
- An element of a buffer is always an `int` from 0 to 255: it is never `undefined`, and assigning something else (`magic[0] = "a"`,
  `magic[0] = undefined`) is an `ArgumentException`.
- `length`, `[i]`, `foreach`, `copy`/`flat` and `is of IEnumerable` work as for arrays; `print` shows `<buffer n bytes>`, JSON writes a buffer as
  base64 text.
- A parameter `byte data[]` takes what it is given (an array stays an array).

### `word` declarations are buffers of machine words

`word` is the machine word of the target (see [Types](02-types.md#the-machine-word-word)). Declared with array brackets it holds a buffer of
words, just like `byte` holds a buffer of bytes:

```fire
word regs[4]                // the same as new word[4]: four zero words
word table[] = [1, 2, -1]   // -1 becomes the largest word
print(table)                // <buffer 3 words>
print(table.length)         // 3 (words, not bytes)
print(table[2])             // 18446744073709551615 on the desktop, 4294967295 on the ESP32
regs[0] = regs[0] + 5       // an element is a word
```

- An array stored in a `word` declaration becomes a new buffer: an `int` is cut to the width of a word, a `word` stays, `undefined` becomes
  0, anything else is an `ArgumentException`. Writing an element takes an `int` or a `word`.
- `length` counts words; `[i]`, `foreach`, `copy`/`flat` and `++` work as for bytes. `ref table[i]` is not possible.
- The words lie in memory in the byte order of the machine, each `Word.Bits / 8` bytes wide: **the size in bytes depends on the target**.
  That is why the functions that read or write bytes do not take a word buffer (an `ArgumentException`: "... not a word buffer"); JSON writes
  it as an array of numbers.

### Bytes for functions of the packages

The functions of the packages that **read** bytes - writing a file (`IO.File.WriteAllBytes`), a stream or a socket, hashing, compressing,
sending to a device - take a buffer and also a plain array of ints from 0 to 255:

```fire
#import "filesystem"
IO.File.WriteAllBytes("out.bin", [0x46, 0x49, 0x52, 0x45])
```

A function that **fills** the buffer you give it (`stream.Read(buffer, 0, n)`, receiving) needs a real buffer (`new byte[n]` or a `byte`
declaration): an array there is an `ArgumentException` ("... expects a byte buffer (new byte[n]), not an array."), so is an element outside
0 .. 255 or a value that is no bytes at all (a string, an object).

## Exercises

1. Fill an array with the first 10 square numbers and print them backwards.
2. Write `static int Max(int values[])` that returns the largest element; what should it do for an empty array?
3. Build a multiplication table `int table[][]` of 10 × 10 and print the row for 7.
4. Collect all words longer than 3 characters of `"the quick brown fox jumps"` in a `List<string>`.
5. Solve exercise 4 again with `#import "linq"` in one line.
