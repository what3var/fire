# The standard library

## Always available

Every program has, without an import:

| Name | What |
|---|---|
| `print(value)` | writes a value and a line break |
| `Math` | `Sqrt`, `Abs`, `Min`, `Max`, `Clamp`, `Sign`, `Round`, `Floor`, `Ceiling`, `Sin`, `Cos`, `Tan`, `Asin`, `Acos`, `Atan`, `Atan2`, `Log`, `Log10`, `Exp`, `PI`, `E` - see below |
| `StringBuilder` | collects text: `Append`, `AppendLine`, `Length`, `Clear`, `ToString` - see [Strings](06-strings.md#joining) |
| `List<T>` | a growing list - see [Arrays and lists](07-arrays-and-lists.md#lists-listt) |
| `IEnumerable<T>`, `IEnumerator<T>` | the interfaces of `foreach` - see [Interfaces and generics](09-interfaces-and-generics.md#ienumerable) |
| `Exception` and the runtime exceptions | see [Exceptions](14-exceptions.md#errors-of-the-runtime) |
| `Takes` | what travels along with `TakeTo` & co. - see [Ownership](13-ownership.md) |
| `ICommand`, `Command`, `ICommand<T>`, `Command<T>` | a command as an object: a lambda with `Execute()` |
| methods of `string`, `char`, `int` | `ToUpper`, `Split`, `ToInt`, `ToFloat`, `Join`, `ToChar`, ... - see [Strings](06-strings.md) |
| `Resource` | `new Resource("file.png")` embeds a file of the project into the program at compile time |

## Libraries: `#import`

A library is switched on with `#import "name"` at the top of a file. The standard libraries come as packages that are installed with the editor
(the first start of `spark` or `ember` installs them); further packages can be installed with `ember`.

| Import | Contents |
|---|---|
| `"time"` | `Sleep(duration)`, `DateTime` (`Now`, `UtcNow`, `Today`, `Parse`, arithmetic), `TimeSpan` |
| `"random"` | `PseudoRandom`: `Next()`, `Next(max)`, `Next(min, max)`, `NextFloat()`, `NextBool()`, `Pick(list)`, `Shuffle(list)` (a `List` or an array) |
| `"testing"` | namespace `Testing`: `Assert` (`AreEqual`, `IsTrue`, `Throws`, ...) for the tests that `forge test` runs - see docs/TESTING.md |
| `"linq"` | queries over arrays, lists and `IEnumerable`s - see [Arrays and lists](07-arrays-and-lists.md#queries-import-linq) |
| `"reflection"` | `Type`, `Member`, `Reflect`, selectors - see [Probes and reflection](16-probes-and-reflection.md) |
| `"io"` | namespace `IO`: streams (`MemoryStream`, your own), text (`TextReader`, `TextWriter`, `Utf8`), console input and output (`Stdio`) |
| `"filesystem"` | the file system in namespace `IO`, named like .NET: `File`, `Directory`, `Path`, `FileStream` - see docs/FILESYSTEM.md |
| `"ui.docking"` | namespace `UI.Docking`: a `DockManager` for tool windows (docked, tabbed, floating) and movable toolstrips with an overflow menu, a `StatusBar` - see docs/DOCKING.md |
| `"dialogs"` | open, save and folder dialogs and message boxes: `Dialogs.OpenFileDialog`, `SaveFileDialog`, `FolderBrowserDialog`, `MessageBox` - see docs/DIALOGS.md |
| `"net"` | namespace `Net`: TCP clients and servers, UDP, DNS |
| `"http"` | namespace `Http`: HTTP client and server (HTTPS with `"tls"`) |
| `"tls"` | namespace `Tls`: encrypted connections |
| `"devices"` | serial ports and device packets |
| `"gpio"`, `"i2c"`, `"spi"`, `"wifi"` | hardware access on boards like the Raspberry Pi and the ESP32 |
| `"graphics"` | framebuffers, drawing, images, fonts |
| `"windows"` | a window for a framebuffer, keyboard, mouse, touch and joystick events; `WindowManager` cycles windows (`window.Show()` goes on - a manager nobody runs gets a thread of its own; `window.Show(true)` is modal; `window.Run()` is the start of the program, on this thread) |
| `"ui"` | the UI library (controls, layout, styles, data binding, `.fxml` markup) |
| `"audio"` | sound output |
| `"tracker"` | a MOD music player |
| `"scene3d"` | 3D scenes |

## Examples

### Math

```fire
print(Math.Sqrt(16))                // 4
print(Math.Sqrt(9m * 1m))           // 3m - the root of the unit too
print(Math.Abs(-2.5m))              // 2.5m
print(Math.Max(1m, 50cm))           // 1m
print(Math.Round(2.5))              // 3 - halves away from zero
print(Math.Floor(2.7m))             // 2m
print(Math.Sin(Math.PI / 2))        // 1
print(Math.Atan2(1m, 1m) * 4)       // 3.141592653589793
```

- `Round`, `Floor` and `Ceiling` keep the unit; an `int` stays an `int`. `Abs`, `Min`, `Max`, `Clamp` work on every number with or without unit.
- `Sqrt` takes the root of the unit as well; a unit without a root (`m`) is an `ArgumentException`.
- `Sin`, `Cos`, `Tan` take radians; the angle and exponential functions take a number without unit and give a `float`. `Atan2(y, x)` takes two
  values of the same dimension.

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
var cards = [1, 2, 3, 4, 5]
rnd.Shuffle(cards)                  // arrays work as well
print(rnd.Pick(cards) >= 1)
```

`new PseudoRandom()` without a seed starts from the current time.

### Files

```fire
#import "filesystem"
var path = IO.Path.Combine(IO.Path.GetTempPath(), "fire-doc.txt")
IO.File.WriteAllText(path, "hello file")
print(IO.File.ReadAllText(path))
print(IO.File.Exists(path))         // True
IO.File.Delete(path)
```

`IO.File`: `Exists`, `Size`, `GetLastWriteTime`, `Delete`, `Copy`, `Move`, `ReadAllText`, `WriteAllText`, `AppendAllText`, `ReadAllLines`, `WriteAllLines`, `ReadAllBytes`,
`WriteAllBytes`, `OpenRead`, `OpenText`, `CreateText`, `AppendText`. `IO.Directory`: `Exists`, `CreateDirectory`, `Delete`, `Move`, `GetFiles`, `GetDirectories`, ... `IO.Path`:
`Combine`, `GetFileName`, `GetFileNameWithoutExtension`, `GetExtension`, `ChangeExtension`, `GetDirectoryName`, `GetFullPath`, `GetRelativePath`, `GetTempPath`, ...
Errors are `IO.IOException`s (`FileNotFoundException`, `DirectoryNotFoundException`, ...).

### Dialogs

```
#import "dialogs"
var path = Dialogs.OpenFileDialog.Show("Text files|*.txt|All files|*.*")   // undefined when cancelled
if (path != undefined) { print(IO.File.ReadAllText(path)) }
if (Dialogs.MessageBox.Show("Quit?", "Demo", Dialogs.MessageBoxButtons.YesNo) == Dialogs.DialogResult.Yes) { print("bye") }
```
Streams are closed by their destructor when their owner ends (see [Ownership](13-ownership.md)).

## More documentation

The libraries for graphics, UI, network, devices, sound and 3D have their own documents in the `docs` folder of the repository (`UI.md`,
`UI_MARKUP.md`, `NETWORK.md`, `AUDIO.md`, `SCENE3D.md`, `TRACKER.md`, `FONTS.md`, `RESOURCES.md`). The editor completes the members of every
imported library while you type and shows their documentation comments.

## Exercises

1. Measure how long a loop of one million additions takes with `DateTime.Now`.
2. Simulate 1000 throws of two dice with `PseudoRandom` and count how often each sum appears.
3. Write the multiplication table into a text file and read it back line by line with `ReadAllLines`.
