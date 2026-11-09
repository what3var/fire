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
