# Time

`#import "time"` brings everything about time: `Sleep` waits, `TimeSpan` is a duration, `DateTime` a point in time with calendar and clock. The
package `fire-time` is small and works the same in the VM and natively - on a board too (there the clock starts at the time the board knows, often
1970 without network time).

```fire
#import "time"

var start = DateTime.Now
Sleep(20ms)
var took = DateTime.Now - start            // a DateTime minus a DateTime is a TimeSpan
print(took.TotalMilliseconds >= 20)        // True
```

## Waiting: `Sleep`

`Sleep` takes a **duration** in any of three forms:

| Argument | Example | Means |
| --- | --- | --- |
| a number with a time unit | `Sleep(250ms)`, `Sleep(1.5s)`, `Sleep(2min)` | the duration ([units](05-units.md)) |
| a `TimeSpan` | `Sleep(TimeSpan.FromSeconds(1))` | the duration |
| a plain number | `Sleep(100)` | milliseconds |

Anything else (a text, a length in metres) is a `TimeException`. `Sleep` is a **safe point**: while the program waits, other threads, `fire global`
jobs and the events of windows get their turn ([threads](15-threads-and-actors.md)). A loop that only polls should sleep a little in every round.

```fire
#import "time"

for (var i = 3; i > 0; i--) {
    print(i)
    Sleep(10ms)
}
print("go")                                // 3, 2, 1, go
```

## Durations: `TimeSpan`

A `TimeSpan` counts **ticks** of 100 nanoseconds (like .NET). It can be negative.

```fire
#import "time"

var a = new TimeSpan(1, 30, 0)             // 1 h 30 min (h, m, s)
var b = TimeSpan.FromMinutes(45)
var c = TimeSpan.Of(90s)                   // from a value with a time unit
print(a + b)                               // 02:15:00
print(a - b)                               // 00:45:00
print(b * 2)                               // 01:30:00
print(a > b)                               // True
print(c.TotalMinutes)                      // 1.5
print(new TimeSpan(2, 3, 4, 5, 600))       // 2.03:04:05.6000000 (d, h, m, s, ms)
```

| Member | What it is |
| --- | --- |
| `new TimeSpan(ticks)`, `(h, m, s)`, `(d, h, m, s)`, `(d, h, m, s, ms)` | constructors |
| `TimeSpan.Zero()`, `FromTicks(t)`, `FromMilliseconds(x)`, `FromSeconds(x)`, `FromMinutes(x)`, `FromHours(x)`, `FromDays(x)` | factories (`x` may have a fraction) |
| `TimeSpan.Of(v)` | from a value with a time unit (`1.5s`, `250ms`), a plain number (milliseconds) or a `TimeSpan` |
| `Ticks`, `Days`, `Hours`, `Minutes`, `Seconds`, `Milliseconds` | the parts (`Hours` is 0..23, `Minutes` 0..59, ...) |
| `TotalDays`, `TotalHours`, `TotalMinutes`, `TotalSeconds`, `TotalMilliseconds` | the whole duration as a `float` |
| `Add`, `Subtract`, `Multiply(f)`, `Divide(f)`, `Negate()`, `Abs()`, `CompareTo`, `Equals` | arithmetic as methods |
| `+`, `-`, `*`, `/`, `<`, `>`, `<=`, `>=`, `==`, `!=` | the same as operators |
| `ToString()` | `[-][d.]hh:mm:ss[.fffffff]` |

`span + "text"` is a string concatenation (the span as text), `span + otherSpan` a sum.

## Points in time: `DateTime`

A `DateTime` is a date and a time of day, counted in ticks since 0001-01-01. Its **kind** is `"local"` or `"utc"`; comparisons and differences
assume the same kind - convert with `ToUtc()` / `ToLocal()` first.

```fire
#import "time"

var d = new DateTime(2026, 11, 4, 14, 30, 0)     // local time
print(d)                                         // 2026-11-04 14:30:00
print(d.ToString("dd.MM.yyyy HH:mm"))            // 04.11.2026 14:30
print(d.DayName() + ", " + d.MonthName())        // Wednesday, November
print(d.DayOfYear)                               // 308
var later = d.AddDays(30).AddHours(2)
print(later)                                     // 2026-12-04 16:30:00
print(later - d)                                 // 30.02:00:00
print(DateTime.IsLeapYear(2028))                 // True
print(DateTime.DaysInMonth(2026, 2))             // 28
```

| Member | What it is |
| --- | --- |
| `new DateTime(y, m, d)`, `(y, m, d, h, mi, s)`, `(y, m, d, h, mi, s, ms)` | a local point in time |
| `DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today` | the clock (properties, like in .NET; `Today` is local midnight) |
| `DateTime.FromTicks(ticks, kind)`, `FromUnixSeconds(s)` | from ticks or from Unix time (UTC) |
| `DateTime.Parse(text)`, `TryParse(text)` | ISO and common forms (`"2024-03-15 14:30:00"`, `"2024-03-15T14:30:00Z"`); `Parse` throws a `TimeException`, `TryParse` returns `undefined` |
| `DateTime.DaysInMonth(y, m)`, `IsLeapYear(y)` | calendar helpers |
| `Year`, `Month`, `Day`, `Hour`, `Minute`, `Second`, `Millisecond`, `Ticks`, `Kind` | the parts |
| `DayOfWeek` (0 = Sunday ... 6 = Saturday), `DayOfYear`, `DayName()`, `MonthName()` | the day |
| `Date()`, `TimeOfDay()` | midnight of the same day, the time since then as a `TimeSpan` |
| `Add(span)`, `AddTicks`, `AddMilliseconds`, `AddSeconds`, `AddMinutes`, `AddHours`, `AddDays`, `AddMonths`, `AddYears` | a new point in time (months and years keep the day where the month has it, else its last day) |
| `Subtract(x)`, `-` | minus a `TimeSpan`: a `DateTime`; minus a `DateTime`: a `TimeSpan` |
| `+` | plus a `TimeSpan`: a later `DateTime`; plus a text: concatenation |
| `ToUtc()`, `ToLocal()`, `ToUnixSeconds()` | conversions |
| `<`, `>`, `<=`, `>=`, `==`, `!=`, `CompareTo`, `Equals` | comparison (by ticks) |
| `ToString()`, `ToString(format)` | `yyyy-MM-dd HH:mm:ss`, or a .NET format (`"dd.MM.yyyy"`, `"HH:mm"`, `"o"`) |

Names of days and months are English.

## Measuring time

`DateTime.Now` is fine for durations in milliseconds. A tight loop measures better over many rounds:

```fire
#import "time"

var start = DateTime.Now
var sum = 0
for (var i = 0; i < 100000; i++) { sum += i }
var took = DateTime.Now - start
print(sum)                                   // 4999950000
print(took.TotalMilliseconds < 10000)        // True
```

For a timeout, compare against a point in time:

```fire
#import "time"

var deadline = DateTime.Now + TimeSpan.FromMilliseconds(50)
var rounds = 0
while (DateTime.Now < deadline) {
    rounds++
    Sleep(5ms)
}
print(rounds > 0)                            // True
```

## Time units

The time units of fire (`ms`, `s`, `min`, `h`, ...) are ordinary [units](05-units.md): `float:s t = 90s` and `t + 1min` work without the package.
`#import "time"` connects them to `Sleep` and `TimeSpan.Of`.

```fire
#import "time"

float:s t = 1.5min
print(t)                                     // 90s
print(TimeSpan.Of(t))                        // 00:01:30
```

## Exercises

1. Print the weekday of your birthday in the year 2030.
2. Write a countdown that prints the seconds left until a `DateTime` five seconds from now, once per second.
3. Measure how long `Sleep(1ms)` really takes on your machine, averaged over 100 calls.
4. Parse `"2024-02-28 23:00"`, add two hours and print the result. What happens with `AddMonths(1)` on January 31?
