# First Steps

Welcome to **spark**, the editor for the **fire** scripting language. This page gets you from an empty tab to a running, debugged program in a few minutes.

- [The editor at a glance](#the-editor-at-a-glance)
- [Your first program](#your-first-program)
- [Variables, types and units](#variables-types-and-units)
- [Control flow and functions](#control-flow-and-functions)
- [Classes](#classes)
- [Errors and exceptions](#errors-and-exceptions)
- [Debugging](#debugging)
- [Talking to devices](#talking-to-devices)
- [Writing documentation](#writing-documentation)

## The editor at a glance

Every file you open gets its own tab. Scripts (`.script`) open in the code editor, Markdown files (`.md`, like this one) in the Markdown editor, and device packet recordings (`.fplog`) in the packet viewer.

| Area | What it is for |
| --- | --- |
| **Output** | Everything your program prints. |
| **Error list** | Compile errors and warnings. They are found while you type; double-click one to jump to the line. |
| **Threads, Scope, Stack** | Appear in use while you debug: all running threads, the variables of the selected frame, and the call stack. |
| **Devices** | Serial and simulated devices you can talk to from scripts. |

All panels can be dragged to any side of the window, stacked as tabs, floated as separate windows or auto-hidden. **View → Reset Layout** brings the original arrangement back.

Handy shortcuts in the code editor:

| Shortcut | Action |
| --- | --- |
| `Ctrl+Space` | Show code completion (it also pops up while you type). |
| `F12` or `Ctrl+Click` | Go to the definition of the symbol under the cursor, even into built-in libraries. |
| `Ctrl+Shift+C` | Comment or uncomment the selected lines. |
| `Ctrl+G` | Go to a line. |
| `Ctrl+F`, `F3` | Find, find next. |

## Your first program

Create a new script with **File → New Script** (`Ctrl+N`), type

```fire
print("Hello, fire!")
```

and press `F5`. The text appears in the **Output** panel.

Statements are separated by a line break **or** a semicolon, so both of these are fine:

```fire
var a = 1
var b = 2; var c = 3
```

Comments start with `//`, or are wrapped in `/* ... */`.

## Variables, types and units

`var` lets the language pick the type from the value. You can also name the type in front of the variable:

```fire
var name = "fire"
int count = 3
float ratio = 2.5
bool ready = true
print($"{name}: {count} x {ratio} (ready: {ready})")
```

The line with `$"..."` is a *format string*: whatever is in `{curly braces}` is evaluated and inserted.

Numbers can carry a **unit**. A trailing `:` converts a value to the unit of the other operand, `:unit` converts to a unit you name, and incompatible units are an error instead of a silently wrong number:

```fire
var length = 500mm
var distance = 2m
print(length + distance:)   // 2500mm - the distance is converted to millimeters
print(distance:cm)          // 200cm
```

Adding `5kg` to a length stops the program with an error.

## Control flow and functions

```fire
var square = (int n) => n * n

for (var i = 1; i <= 3; i++) {
    print(square(i))
}

var names = ["Ada", "Grace", "Linus"]
foreach (n in names) {
    if (n == "Grace") {
        print("found " + n)
    }
}
```

`while`, `switch`, `break` and `continue` work as you would expect. Small functions are **lambdas** (`x => x * 2`, `(a, b) => a + b`); their parameters may have types and default values. Functions with a name are methods of a class, which is the next section.

## Classes

```fire
class Animal {
    string name

    construct(string name) {
        this.name = name
    }

    Speak() {
        print(this.name + " makes a sound")
    }
}

class Dog : Animal {
    construct(string name) : base(name) { }

    Speak() {
        print(this.name + " barks")
    }
}

var d = new Dog("Rex")
d.Speak()
```

fire has no garbage collector. Instead every object has exactly **one owner** (the scope that created it, unless you hand it to someone else) and is destroyed together with its owner. A destructor runs when that happens:

```fire
class Resource {
    construct() { print("opened") }
    destruct() { print("closed") }
}

{
    var r = new Resource()
}                       // the block ends, so r is destroyed here
print("after")          // prints: opened, closed, after
```

## Errors and exceptions

```fire
var items = [1, 2, 3]

try {
    print(items[10])
} catch (e) {
    print("Something went wrong: " + e.message)
}
```

Errors are objects that you can catch with `catch`. Unlike in most languages, a handler can also **resume** the failed operation after fixing the problem.

## Debugging

| Shortcut | Action |
| --- | --- |
| `F5` | Start, or continue to the next breakpoint. |
| `Ctrl+F5` | Recompile and start again. |
| `Shift+F5` | Stop. |
| `F9` | Toggle a breakpoint on the current line (or click in the margin next to the line number). |
| `F10` | Step over. |
| `F11` | Step into. |
| `Shift+F11` | Step out. |

The yellow line is where the program is paused. While paused, the **Threads**, **Scope** and **Stack** panels show what is going on. The **Run** menu switches between the *Debug*, *Release* and *Performance* execution modes, and can build a standalone program from your script.

## Talking to devices

Scripts reach hardware through the `devices` library:

```fire
#import "devices"

var device = Device.Default.EnsureConnected()   // connects if necessary
device.WriteString("ping\n")
if (device.WaitForString("pong", 2s)) {
    print("the device answered")
}
```

Pick a *default device* in the toolbar (the **Devices** panel finds serial ports; **Devices → Show simulated loopback device** gives you an echo device to experiment with), then run the script. The **packet trace** of a device records everything that was sent and received.

## Writing documentation

Markdown documents are first-class citizens in spark: **File → New Markdown Document** creates one with a live preview. You can also open documents *read-only* or as a pure *viewer* (this page is open as a viewer): links to other local Markdown files load in the same tab, `Ctrl+Click` opens them in a new tab, and `Alt+Left` / `Alt+Right` go back and forward.

Fenced code blocks marked with ` ```fire ` are colored just like in the code editor.
