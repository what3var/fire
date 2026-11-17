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
- An index and a size (`new int[n]`, `new byte[n]`) are ints (a `word` counts by its bits). Any other type is an error when the program is compiled
  (`a["x"]`, `new int[2.5]`: `TC033`); a value of another kind that comes through `any` is a catchable `ArgumentException` at run time
  (`An index must be an int, not String.`, `An array size must be an int, not Float.`, `A byte buffer size must be an int, not String.`), in the VM
  and natively. A negative size is the `ArgumentException` `Invalid array size -1.`.
- New elements are `undefined` until something is assigned - also in an `int` array.
- The size never changes; for a collection that grows, use `List<T>` (below).
- **The brackets stand after the name**: `int values[]`, `string names[10]`. Brackets after the type are a bit width (`int[8] small`, see
  [Types](02-types.md#bit-widths)).
- An array literal may hold different types (`[1, "two", 3.0]`); a typed declaration (`int values[]`, `Dog pets[]`, `string names[]`) makes a
  **typed array** (below).
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
  variable or field. Its elements are converted then. An array typed already for another element type is an `ArgumentException`, and so is a value
  that is no array at all (`int a[] = 5`): a typed array declaration holds an array or `undefined`, like `int x` holds an int. This holds for every way
  a field is set - an assignment, `Reflect.Set`, `Json.Deserialize` (which reads the array typed).
- A parameter `int a[]` types what it receives like a variable (in place: the caller's array becomes typed; an element that does not fit is the
  exception). Copies (`copy`, `flat`, [`taking`](15-threads-and-actors.md)) of a typed array are typed.
- `int m[3][4]` types the inner arrays. `var`, `any` and pointers declare no element type: such an array takes any value. Classes, interfaces,
  records, strings, enums and lambdas type it too (below).
- **A declaration with a size makes the array** - a local or global where it is declared, a field together with its object
  (`class Box { int data[3] }`: `new Box().data.length` is 3). Without a size (`int arr[]`) the variable or field stays `undefined` until an array is assigned.
- Natively a typed array is **packed**: one C number per element (`int8_t` for `int[8]`, `float` for `float[32]`, one byte for `bool`) instead of a
  16-byte value - `int[8] a[100000]` needs 100 KB instead of 1.6 MB. Its elements are read and written in place, about as fast as a buffer's.
- In the VM a `bool` array is packed too: one byte per element (`false`, `true` or `undefined`) instead of a 24-byte value - `bool seen[20000000]`
  needs 20 MB. Arrays of the other element types hold one value per element there. A new array (`new int[n]`) takes its memory with its first use.

**`defined` arrays.** `defined int a[]` (any value type: `int`, `int[N]`, `float`, `float[N]`, `bool`, `char`) holds elements that are never `undefined`:

```fire
defined float speeds[] = new float[3]
print(speeds[0])                // 0 - a new defined array starts at 0 (false, '\0')
speeds[1] = 2.5
int raw[] = [1, undefined, 3]
defined int sure[] = raw        // the array becomes defined: its undefined element becomes 0
print(raw[1])                   // 0 - it is the same array
any nothing = undefined
try { sure[0] = nothing } catch (ArgumentException e) { print(e.message) }   // An element of a defined array of int cannot be undefined.
```

- Reading an element gives a `defined` value (the type checker knows it; storing `undefined` where it can see it is `TC022`). Natively an element of a
  defined array is read with one load into a C number - use it for the numbers of a hot loop.
- The array stays defined through every reference: an `int b[] = sure` may not store `undefined` either. The variable or field itself may still be
  `undefined` (no array yet). `byte`/`word` declarations are buffers - never undefined anyway.

**Arrays of classes, strings, enums and lambdas.** `Dog pets[]`, `IShape shapes[]`, `string names[]`, `Color cs[]` (a variable, a field, a parameter, a
global) type the array the same way: an element takes a value of that type - an object of the class or of a class derived from it, of a class that
implements the interface, a string, a value of the enum - or `undefined`; anything else is an `ArgumentException`, through any reference. An array of
a derived class goes where one of its base class (or of an interface it implements) is declared, and **keeps its own element type**:

```fire
enum Color { Red, Green }
class Animal { }
class Dog : Animal { }
class Cat : Animal { }
Dog dogs[] = new Dog[2]
dogs[0] = new Dog()
any other = new Cat()
try { dogs[1] = other } catch (ArgumentException e) { print(e.message) }   // An element of an array of Dog must be a Dog (or undefined), not Cat.
Animal all[] = dogs                 // fine: every Dog is an Animal
try { all[1] = other } catch (ArgumentException e) { print(e.message) }    // the same: it is still the Dog array
defined Color colors[] = new Color[2]
print(colors[1])                    // Red - a defined enum array starts at the first value
int lambda<int, int> ops[] = [(int a, int b) => a + b, (int a, int b) => a * b]
print(ops[1](6, 7))                 // 42
```

- The other direction (an `Animal` array where `Dog d[]` is declared) and an array of an unrelated class are refused (`An array of Dog cannot be
  stored where an array of Cat is declared.`) - by the type checker where it knows both types (`TC001`), at run time in any case.
- `new Dog[n]` (a class, an interface, a record, `string`, an enum) **is** an array of Dog from the start, also where no declaration says so:
  `any d = new Dog[2]`, `var x = new Dog[2]`, an argument. `Cat c[] = d` is the `ArgumentException` above, `Animal a[] = d` is fine and `a[0] = new
  Cat()` is refused by the Dog array. An array of numbers (`new int[n]`) and a literal (`[a, b]`) take their element type from the first declaration
  they are stored in (`int[8] small[] = new int[4]`, `float:m lens[] = new float[3]`). `new int[n]` remembers that it was made for ints all the same:
  it goes where ints (of any width and unit) or floats are declared, and nowhere else - `any d = new int[2]; Cat c[] = d` is the
  `ArgumentException` (`An array of int cannot be stored where an array of Cat is declared.`); a `new float[n]` does not go into `int a[]`.
- A method that returns an array (`Cat[] Find()`, `int[] Sizes()`) checks what it returns like a variable of that type: an array of another
  class or a value that is no array is the same `ArgumentException`.
- `defined` works for them too: `defined string s[] = new string[n]` starts with `""`, a `defined` enum array with the first value; `defined Dog d[]` has
  no zero, so it only takes an array whose elements are all given (`new Dog[2]` there is an `ArgumentException`).
- `lambda<int, int> fs[]` holds lambdas with two parameters (or `undefined`); the types of the signature are hints for the type checker.
- Generic code does the same by its type argument: `T items[]` and `new T[n]` in a `Bag<Dog>` are an array of Dog - so `List<Dog>`,
  `Dictionary<string, Dog>` and `List<Color>` check their elements.

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
| `Contains(value)` | `true` if an equal element is in the list (`IndexOf(value) >= 0`) |
| `Clear()` | removes everything |
| `Sort()` | sorts the elements by `<` (numbers, texts, a class with `operator<`); stable - equal elements keep their order |
| `Sort((a, b) => ...)` | sorts by a comparison: below 0 - `a` first, 0 - equal (they keep their order), above 0 - `b` first |

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
var ages = new List<int>([30, 20, 40])
ages.Sort()                              // 20 30 40
ages.Sort((a, b) => b - a)               // 40 30 20

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

What the container owns is **destroyed when it falls out**: `Remove`, overwriting (`dict[k] = other`, `list[i] = other`), `RemoveAt`, `Clear`.
That holds for everything that was handed over to it - with `Owns.Explicit` too: an object made in the call, one given with `take`, one moved in
with `node.TakeTo(dict)`. An object that belongs to someone else (a plain reference in an `Owns.Explicit` container) only leaves the container.
`Take` / `TakeAt` take a value out instead and give it to the caller, who owns it then:

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

With `Owns.Explicit` the same holds for what was handed over (`dict["a"] = new Node("a")`, `node.TakeTo(dict)`); a value that is only referenced
(`dict["k"] = keep`) stays with its owner. To keep an object that the container owns, take it out with `Take` / `TakeAt` instead of `Remove`.

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
| `ReadInt16(at)`, `WriteFloat32(at, x)`, ... | a number of 1, 2, 4 or 8 bytes at a position (see below) |
| `Fill`, `CopyTo`, `Slice`, `IndexOf`, `Compare` | fast operations on the bytes (see below) |

### Reading and writing numbers

A buffer reads and writes whole numbers and floats at a byte position - in the byte order of the buffer (`littleEndian`: a new buffer is little
endian), or in the one given as the last argument (`true`: little endian, `false`: big endian):

```fire
var header = new byte[16]
header.WriteUInt32(0, 0x46495245, false)    // "FIRE", big endian
header.WriteInt16(4, -2)
header.WriteFloat32(8, 1.5)
header.WriteFloat16(12, -0.5)               // half precision
print(header.ReadUInt32(0, false))          // 1179210309
print(header.ReadInt16(4))                  // -2
print(header.ReadUInt16(4))                 // 65534
print(header.ReadFloat32(8))                // 1.5
print(header.ReadFloat16(12))               // -0.5
```

| Read | Write | Bytes | Value |
|---|---|---|---|
| `ReadInt8(at)`, `ReadUInt8(at)` | `WriteInt8(at, v)`, `WriteUInt8(at, v)` | 1 | -128 .. 127 / 0 .. 255 |
| `ReadInt16(at[, little])`, `ReadUInt16` | `WriteInt16(at, v[, little])`, `WriteUInt16` | 2 | -32768 .. 32767 / 0 .. 65535 |
| `ReadInt32(at[, little])`, `ReadUInt32` | `WriteInt32(at, v[, little])`, `WriteUInt32` | 4 | 32 bits signed / unsigned |
| `ReadInt64(at[, little])` | `WriteInt64(at, v[, little])` | 8 | 64 bits |
| `ReadFloat16(at[, little])` | `WriteFloat16(at, x[, little])` | 2 | IEEE binary16 (half precision) |
| `ReadFloat32(at[, little])` | `WriteFloat32(at, x[, little])` | 4 | IEEE binary32 |
| `ReadFloat64(at[, little])` | `WriteFloat64(at, x[, little])` | 8 | IEEE binary64 |

- A write takes the low bits of the number (`WriteUInt8(0, 300)` writes 44, `WriteInt16` and `WriteUInt16` write the same bytes); a float is rounded
  to the format (`WriteFloat16`: to nearest even, above 65504 infinity). A `word` can be written as an int; a number with a unit is an
  `ArgumentException`.
- A read that does not fit the program's `int` (`ReadUInt32` or `ReadInt64` with `#intwidth 32`) keeps its 64 bits, like the result of a native.
- A position outside the buffer (`at < 0` or `at + size > length`) is an `IndexOutOfBoundsException`; a buffer of words (`word a[]`) is an
  `ArgumentException` - its bytes depend on the target.

### Fast operations

```fire
var b = new byte[8]
b.Fill(0xFF)                      // every byte
b.Fill(0, 2, 4)                   // 4 bytes from position 2
var c = new byte[8]
b.CopyTo(c, 0)                    // all of b into c from position 0
b.CopyTo(c, 1, 2, 3)              // 3 bytes of b from 2 into c from 1 (overlapping parts of one buffer are fine)
var part = b.Slice(2, 4)          // a new buffer with the 4 bytes from 2 (in the same byte order)
var text = "hello world".ToBytes()
print(text.IndexOf(111))          // 4 - the first 'o'
print(text.IndexOf(111, 5))       // 7 - from position 5
print(text.IndexOf("wor".ToBytes()))  // 6 - a sequence of bytes
print(text.Compare("hello".ToBytes()))  // 1 - like comparing texts: -1, 0 or 1
```

| Method | What |
|---|---|
| `Fill(value[, start, count])` | sets the bytes to `value` (its low 8 bits) |
| `CopyTo(target, targetAt[, start, count])` | copies bytes into another buffer (or into itself: overlapping parts are copied as if through a copy) |
| `Slice(start[, count])` | a new buffer with those bytes |
| `IndexOf(value[, start])` | the position of the first byte `value` (0 .. 255) or of the first occurrence of a byte buffer; -1 if there is none |
| `Compare(other)` | -1, 0 or 1: byte by byte, a shorter buffer that is the start of the other is smaller |

A range outside a buffer is an `IndexOutOfBoundsException`. Natively these are a loop or `memset`/`memmove` over the bytes; for bits of single
numbers see [Bit functions](03-operators.md#bit-functions).

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
- A parameter `byte data[]` is a `byte` declaration too: an array given to it becomes a new buffer with its elements (what the method writes then
  does not reach the array - the warning `TC032` says so for an array of a variable or a field), a buffer and `undefined` pass, anything else is an
  `ArgumentException` (`A byte buffer is declared here, not String.`). So the native backend knows what it holds: `b[i]` in a loop reads the byte
  in place.

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

### Buffers and threads

A buffer of the global variables (in a global, a static field, a field of a global object or an array of them) is **shared memory** for the fire
threads: every thread reads and writes its elements directly, without a section and without waiting for the main program. The bounds and the
destroyed state are checked as always, and the memory stays valid while any thread may still be in an access - after `delete` (or a reassignment
that destroys it) the next access of every thread is the `DestroyedException`. What is not guaranteed is the order and atomicity of the accesses of
different threads: a race reads the old or the new byte (a `word` may be torn), and what another thread wrote is certain to be seen only after a
synchronising operation (`sync global`, a channel, an actor message, `await`, ...). See Threads and actors, "Global buffers: shared memory"
(`15-threads-and-actors.md`). A buffer a thread owns itself - its own, a `taking` copy, one it got with `handover` - is its alone.

## Exercises

1. Fill an array with the first 10 square numbers and print them backwards.
2. Write `static int Max(int values[])` that returns the largest element; what should it do for an empty array?
3. Build a multiplication table `int table[][]` of 10 × 10 and print the row for 7.
4. Collect all words longer than 3 characters of `"the quick brown fox jumps"` in a `List<string>`.
5. Solve exercise 4 again with `#import "linq"` in one line.
