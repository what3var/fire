# The standard library

## Always available

Every program has, without an import:

| Name | What |
|---|---|
| `print(value)` | writes a value and a line break |
| `List<T>` | a growing list - see [Arrays and lists](07-arrays-and-lists.md#lists-listt) |
| `IEnumerable<T>`, `IEnumerator<T>` | the interfaces of `foreach` - see [Interfaces and generics](09-interfaces-and-generics.md#ienumerable) |
| `Exception` and the runtime exceptions | see [Exceptions](14-exceptions.md#errors-of-the-runtime) |
| `Takes` | what travels along with `TakeTo` & co. - see [Ownership](13-ownership.md) |
| `ICommand`, `Command`, `ICommand<T>`, `Command<T>` | a command as an object: a lambda with `Execute()` |
| methods of `string`, `char`, `int` | `ToUpper`, `Split`, `ToChar`, ... - see [Strings](06-strings.md) |
| `Resource` | `new Resource("file.png")` embeds a file of the project into the program at compile time |

## Libraries: `#import`

A library is switched on with `#import "name"` at the top of a file. The standard libraries come as packages that are installed with the editor
(the first start of `spark` or `ember` installs them); further packages can be installed with `ember`.

| Import | Contents |
|---|---|
| `"time"` | `Sleep(duration)`, `DateTime` (`Now`, `UtcNow`, `Today`, `Parse`, arithmetic), `TimeSpan` |
| `"random"` | `PseudoRandom`: `Next()`, `Next(max)`, `Next(min, max)`, `NextFloat()`, `NextBool()`, `Pick(list)`, `Shuffle(list)` |
| `"linq"` | queries over arrays, lists and `IEnumerable`s - see [Arrays and lists](07-arrays-and-lists.md#queries-import-linq) |
| `"reflection"` | `Type`, `Member`, `Reflect`, selectors - see [Probes and reflection](16-probes-and-reflection.md) |
| `"io"` | namespace `IO`: `File`, `Directory`, `Path`, text and binary streams, console input |
| `"net"` | namespace `Net`: TCP clients and servers, UDP, DNS |
| `"http"` | namespace `Http`: HTTP client and server (HTTPS with `"tls"`) |
| `"tls"` | namespace `Tls`: encrypted connections |
| `"devices"` | serial ports and device packets |
| `"gpio"`, `"i2c"`, `"spi"`, `"wifi"` | hardware access on boards like the Raspberry Pi and the ESP32 |
| `"graphics"` | framebuffers, drawing, images, fonts |
| `"windows"` | a window for a framebuffer, keyboard, mouse, touch and joystick events |
| `"ui"` | the UI library (controls, layout, styles, data binding, `.fxml` markup) |
| `"audio"` | sound output |
| `"tracker"` | a MOD music player |
| `"scene3d"` | 3D scenes |

## Examples

### Time

```fire
#import "time"
var start = DateTime.Now
Sleep(50ms)                         // a duration with a unit: ms, s, min, h
var span = DateTime.Now - start
print(span.TotalMilliseconds >= 50) // True
```

### Random numbers

```fire
#import "random"
var rnd = new PseudoRandom(42)      // with a seed: always the same sequence
var dice = rnd.Next(1, 7)           // 1 ... 6
print(dice >= 1 && dice <= 6)
var names = new List<string>(["Ada", "Grace", "Linus"])
print(rnd.Pick(names))
```

`new PseudoRandom()` without a seed starts from the current time.

### Files

```fire
#import "io"
var path = IO.Path.Combine(IO.Path.Temp(), "fire-doc.txt")
IO.File.WriteAllText(path, "hello file")
print(IO.File.ReadAllText(path))
print(IO.File.Exists(path))         // True
IO.File.Delete(path)
```

`IO.File`: `Exists`, `Size`, `Delete`, `Copy`, `Move`, `ReadAllText`, `WriteAllText`, `AppendAllText`, `ReadAllLines`, `WriteAllLines`, `ReadAllBytes`,
`WriteAllBytes`, `OpenText`, `CreateText`, `AppendText`. `IO.Directory`: `Exists`, `Create`, `Delete`, ... `IO.Path`: `Combine`, `FileName`, `Stem`,
`Extension`, `Parent`, `FullPath`, `Temp`, `Separator`. Errors are `IO.IOException`s (`FileNotFoundException`, `DirectoryNotFoundException`, ...).
Streams are closed by their destructor when their owner ends (see [Ownership](13-ownership.md)).

## More documentation

The libraries for graphics, UI, network, devices, sound and 3D have their own documents in the `docs` folder of the repository (`UI.md`,
`UI_MARKUP.md`, `NETWORK.md`, `AUDIO.md`, `SCENE3D.md`, `TRACKER.md`, `FONTS.md`, `RESOURCES.md`). The editor completes the members of every
imported library while you type and shows their documentation comments.

## Exercises

1. Measure how long a loop of one million additions takes with `DateTime.Now`.
2. Simulate 1000 throws of two dice with `PseudoRandom` and count how often each sum appears.
3. Write the multiplication table into a text file and read it back line by line with `ReadAllLines`.
