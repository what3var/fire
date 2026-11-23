# Random numbers, queries

## Random numbers (`#import "random"`)

`PseudoRandom` is a fast generator (xoshiro128**) - the same sequence for the same seed in the VM and in every native build:

```fire
#import "random"

var rnd = new PseudoRandom(42)                     // with a seed: always the same sequence; new PseudoRandom(): seeded from the clock
print(rnd.Next(1, 7) >= 1)                         // True - a die: 1 ... 6
print(rnd.NextFloat() < 1.0)                       // True - 0 <= x < 1
var names = new List<string>(["Ada", "Grace", "Linus"])
print(rnd.Pick(names).Length > 0)                 // True - one of the names
rnd.Shuffle(names)                                 // in place
print(names.Count)                                 // 3
```

| Member | Meaning |
| --- | --- |
| `new PseudoRandom()`, `new PseudoRandom(seed)`, `Seed(seed)` | start |
| `Next()` | a non-negative int |
| `Next(max)`, `Next(min, max)` | `0 <= x < max`, `min <= x < max` (without bias) |
| `NextInt()`, `NextUInt32()` | any 64-bit int, an unsigned 32-bit one |
| `NextFloat()`, `NextBool()` | `0 <= x < 1`, true or false |
| `Pick(list)`, `Shuffle(list)` | an element of a list or array, shuffle it in place |

It is not for cryptography; keys and tokens need a source of the operating system.

### Guid

`Guid` (in the same import) is a 128-bit identifier as RFC 9562 and .NET's `System.Guid` define it:

```fire
#import "random"

var id = Guid.NewGuid()                                    // version 4: 3f2c1a0e-...-4...-[89ab]...
var g = Guid.Parse("{6F9619FF-8B86-D011-B42D-00C04FC964FF}")
print(g)                                                   // 6f9619ff-8b86-d011-b42d-00c04fc964ff
print(g.ToString("N"))                                     // 6f9619ff8b86d011b42d00c04fc964ff
print(g == Guid.Parse("6f9619ff8b86d011b42d00c04fc964ff")) // True
```

| Member | Meaning |
| --- | --- |
| `Guid.NewGuid()` | a random version 4 Guid; the bits come from a `PseudoRandom` seeded once per program from the clock and the memory of the process - unique in practice, **not unguessable** (for a token: `Guid.FromBytes(Net.SecureRandom.Bytes(16))`) |
| `Guid.NewGuid(random)` | a version 4 Guid from this `PseudoRandom` (the same seed, the same Guids) |
| `Guid.Parse(text)`, `Guid.TryParse(text)` | the forms of .NET: `D` (`8-4-4-4-12`), `N` (32 digits), `B` (`{...}`), `P` (`(...)`), either case, spaces around; `Parse` throws an `ArgumentException`, `TryParse` gives `undefined` |
| `ToString()`, `ToString(format)` | `D` by default; `N`, `B`, `P`; lower-case digits as in .NET |
| `Guid.Empty`, `IsEmpty`, `Version` | all zeros; the version (4 for `NewGuid`) |
| `Guid.FromBytes(bytes)`, `ToBytes()` | 16 bytes **in the order of the text** (big-endian, RFC 9562) - not the order of .NET's `ToByteArray`, whose first three fields are little-endian |
| `==`, `!=`, `Equals`, `GetHashCode`, `CompareTo` | by the bits (a Guid works as a key of a `Dictionary` or `HashSet`) |

## Queries (`#import "linq"`)

Queries in the style of C# LINQ on arrays, lists and every `IEnumerable`:

```fire
#import "linq"

var nums = [5, 3, 8, 1, 9, 2]
var big = Linq.From(nums).Where(n => n > 3).OrderBy(n => n).ToArray()
foreach (n in big) { print(n) }                    // 5 8 9
print(Linq.From(nums).Sum())                       // 28
print(Linq.Range(1, 5).Select(n => n * n).Aggregate(0, (acc, n) => acc + n))   // 55
```

All methods are in [arrays and lists](../language/07-arrays-and-lists.md#queries-import-linq).
