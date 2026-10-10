# First Steps

Welcome to **spark**, the editor for the **fire** scripting language. This page gets you from an empty tab to a running, debugged program in a few minutes.

- [The editor at a glance](#the-editor-at-a-glance)
- [Your first program](#your-first-program)
- [Variables, types and units](#variables-types-and-units)
- [Control flow and functions](#control-flow-and-functions)
- [Classes](#classes)
- [Ownership in depth](#ownership-in-depth)
- [Errors and exceptions](#errors-and-exceptions)
- [Threads](#threads)
- [Debugging](#debugging)
- [Talking to devices](#talking-to-devices)
- [Documenting your code](#documenting-your-code)
- [Writing documentation](#writing-documentation)
- [Going further](#going-further)

## The editor at a glance

Every file you open gets its own tab. Scripts (`.script`) open in the code editor, Markdown files (`.md`, like this one) in the Markdown editor, and device packet recordings (`.fplog`) in the packet viewer.

| Area | What it is for |
| --- | --- |
| **Output** | Everything your program prints. |
| **Error list** | Compile errors and warnings. They are found while you type; double-click one to jump to the line. |
| **Threads, Scope, Stack** | Appear in use while you debug: all running threads, the variables of the selected frame, and the call stack. |
| **Devices** | Serial and simulated devices you can talk to from scripts. |

All panels can be dragged to any side of the window, stacked as tabs, floated as separate windows or auto-hidden. **View → Reset Layout** brings the original arrangement back.

The tool bars are *tool strips*: drag one by its grip (the bar on its left) to another place in its row, to the top, bottom, left or right edge of the window (a mark shows where it will land; drop it beside a row to start a row of its own), or out of the window to let it float. Double-clicking the grip floats a strip or puts a floating one back, and the grip's context menu docks, floats or hides it (**View → Toolbars** shows hidden ones again). Strips with combo boxes cannot stand at the left or right edge. **View → Reset Layout** resets the tool strips as well.

Handy shortcuts in the code editor:

| Shortcut | Action |
| --- | --- |
| `Ctrl+Space` | Show code completion (it also pops up while you type). |
| `F12` or `Ctrl+Click` | Go to the definition of the symbol under the cursor, even into built-in libraries. |
| `Ctrl+Shift+C` | Comment or uncomment the selected lines. |
| `Ctrl+G` | Go to a line. |
| `Ctrl+F`, `F3` | Find, find next. |

## Your first program

Create a new script with **File → New → Script...** (`Ctrl+N`), type

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

Numbers can carry a **unit**. Units of the same kind are converted for you - integers stay integers and use the finer unit, unless that would overflow. A trailing `:unit` converts a value explicitly, and units that cannot be converted into each other are an error instead of a silently wrong number:

```fire
var length = 500mm
var distance = 2m
print(length + distance)    // 2500mm - the distance is converted to millimeters
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

fire has no garbage collector. Instead every object has exactly **one owner** (the scope that created it, unless you hand it to someone else) and is destroyed together with its owner. A destructor runs when that happens ([more on ownership below](#ownership-in-depth)):

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

## Ownership in depth

The ownership model is the heart of fire, so it is worth a closer look. The rules are short:

- Every object has **exactly one owner**: a *scope* (a block, a function call, the whole program) or *another object*.
- When the owner goes away - the block ends, the function returns, the owning object is destroyed - everything it owns is destroyed with it: an object's `destruct()` runs, then what *it* owns is destroyed, and so on down the tree.
- Arrays and byte buffers have an owner, too. Numbers, `bool`, `char` and strings are plain values and have none.

**Who owns a new object?** An object that you assign **directly to a field** belongs to the object that has the field. Everywhere else - a local variable, an argument, a return value - it belongs to the **current scope**:

```fire
class Engine {
    destruct() { print("engine destroyed") }
}

class Car {
    Engine engine

    construct() {
        this.engine = new Engine()      // assigned to a field: the car owns it
    }

    destruct() { print("car destroyed") }
}

{
    var car = new Car()
    print("driving")
}                                       // the block ends: the car, then its engine, are destroyed
print("done")                           // prints: driving, car destroyed, engine destroyed, done
```

**Returning an object hands it to the caller.** A value that is returned does not die with the function that made it; it (and what hangs on it) passes to the scope that called the function. Everything else the function created is destroyed when it returns:

```fire
class Part {
    string name
    construct(string name) { this.name = name }
    destruct() { print(this.name + " destroyed") }
}

var make = (string name) => {
    var scratch = new Part("scratch")   // stays inside: destroyed at the return
    var result = new Part(name)
    return result                       // leaves the function: now owned by the caller
}

var part = make("gear")
print("got " + part.name)               // prints: scratch destroyed, got gear (and gear destroyed at the end)
```

**Handing ownership over.** A *reference* never keeps an object alive - `this.part = p` only remembers where `p` is. If the object has to outlive the scope it belongs to, move it to an owner that lives on:

| Call | The new owner is |
| --- | --- |
| `obj.TakeLocal()` | the current scope |
| `obj.TakeUpwards()` | the parent of the scope that owns it now |
| `obj.TakeGlobal()` | the global scope |
| `obj.TakeTo(other)` | the object `other` |
| `take obj` | whoever receives it: an argument `f(take obj)` belongs to the call, `field = take obj` to the object, `var a = take obj` to the current scope |

```fire
class Holder {
    Part part

    Keep(Part p) {
        p.TakeTo(this)          // the holder owns p from now on
        this.part = p
    }

    destruct() { print("holder destroyed") }
}

var holder = new Holder()
{
    var p = new Part("p")
    holder.Keep(p)
}                               // p's block ends, but p now belongs to the holder
print("p is alive: " + holder.part.name)
```

At the end of the program this prints `holder destroyed` and then `p destroyed`. Moving an object below itself (`a.TakeTo(b)` where `b` already belongs to `a`) is an error instead of a cycle.

By default only the object itself moves (what it owns goes along anyway). A second argument, the enum `Takes`, widens that: `TakeTo(other, Takes.Children)` also takes everything the object points to directly (for a list: its items), `Takes.Locals` everything reachable that belongs to the running call, `Takes.All` everything reachable. And `try obj.TakeTo(this)` moves the object **only if the caller is its owner** - so a library can keep what was just handed to it without stealing from somebody else.

**Destroying on purpose.** `delete obj` destroys an object, array or buffer right away, exactly as if its owner had gone away: the destructor runs and everything it owns is destroyed. Using a destroyed object afterwards is a catchable error:

```fire
class Note {
    string text
    construct(string text) { this.text = text }
}

var note = new Note("hi")
delete note
try {
    print(note.text)
} catch (e) {
    print("caught: " + e.message)       // caught: Access to a destroyed object.
}
```

**Copies.** `flat x` makes a shallow copy (the object and its fields; objects it points to are shared), `copy x` a deep one (every reachable object once). The copy is a new object with an owner of its own, chosen by the same rules as for `new`:

```fire
class Item {
    int value
    construct(int value) { this.value = value }
}
class Box {
    Item item
    construct(int v) { this.item = new Item(v) }
}

var a = new Box(1)
var shallow = flat a        // a new Box, but the same Item
var deep = copy a           // a new Box and a new Item
a.item.value = 99
print(shallow.item.value)   // 99 - shared
print(deep.item.value)      // 1  - independent
```

Careful with objects that hold something outside the program, like an open file: a copy shares the handle, and both close it when they are destroyed. The full rules are in the language specification, chapter 2.

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

## Threads

fire has no shared memory between threads, so there are no data races on ordinary objects. Threads talk through **copies** that are synchronised on request, and through **actors** that receive messages.

**`fire`** starts a new thread. It has no return value and ends at the end of its block (or earlier with `leave`). The program itself waits at its end for all running threads:

```fire
fire {
    print("hello from a fire thread")
}
print("main goes on")           // which line comes first is up to the scheduler
```

**`taking`** gives the thread a private **deep copy** of an object (and everything that object owns). Changes made in the thread are invisible to the rest of the program - until the thread says `sync`, which writes the copy back to the original (the last writer wins):

```fire
#import "time"

class Counter {
    int value
    construct() { this.value = 10 }
}

var counter = new Counter()
fire taking counter {
    counter.value = counter.value + 5       // changes the thread's own copy
    print("thread: " + counter.value)       // thread: 15
    sync counter                            // write the copy back to the original
}
Sleep(200ms)
print("main: " + counter.value)             // main: 15 (without the sync it would stay 10)
```

`sync` is an expression: `true` when it worked, `undefined` if the original no longer exists (a good moment for the thread to `leave`). `try sync counter` does not wait for a busy lock and gives `false` instead, and `sync flat counter` writes back only the direct fields instead of the whole tree. A copy must not point to objects outside its own ownership tree - `taking` refuses with an error rather than silently sharing memory.

**Actors** are the other way to communicate. An `actor` is declared like a class, but a method call on it is not run directly: it becomes a **message** in the actor's mailbox. The thread that created the actor (its *home thread*) runs the messages with `process`:

```fire
actor Logger {
    int count

    construct() { this.count = 0 }

    log(string text) {
        this.count++
        print("log: " + text)
    }
}

var logger = new Logger()
fire with logger {
    logger.log("one")                       // only posts a message
    logger.log("two")
}
process logger                              // waits for a message and runs it
process logger
print("messages: " + logger.count)          // messages: 2
```

References to an actor can be passed around freely (`fire with logger`) - that is the one controlled exception to "no shared memory". `try process logger` does not wait: it gives `true` if it ran a message and `false` if the mailbox was empty, so the home thread can mix its own work with answering messages.

**Global variables** belong to the main program. A thread can *read* them directly, but a *change* has to go through the main program: a thread that assigns a global waits until the main program lets it in, one writer at a time. A `sync global { ... }` block groups several changes into one atomic step:

```fire
#import "time"

var total = 0
for (var i = 0; i < 3; i++) {
    fire taking i {
        sync global { total = total + i }   // read-modify-write as one step
    }
}
Sleep(300ms)
print(total)                                // 3
```

The main program serves the waiting threads by itself at safe points, for example after `Sleep` or a window event. If you want to decide where globals may change, write `#nosync` at the top of the program; then only an explicit `sync globals` (and the end of the program) lets the threads in. `fire global { ... }` is the non-waiting variant: the thread queues a job that the main program runs at its next `sync globals`.

**Ending threads.** `leave` ends the thread that executes it (its `finally` blocks and destructors still run); `terminate()` or `terminate(42)` ends **all** threads and the program, from anywhere. An exception that nobody catches in a thread does not vanish: it is delivered to the main program, where you can catch it with `catch threads(...)` (without it the program stops):

```fire
#import "time"

class Failure {
    string message
    construct(string message) { this.message = message }
}

catch threads(Failure e)
{
    print("a thread failed: " + e.message)
}

fire {
    throw new Failure("boom")
}
Sleep(200ms)
print("main is fine")
```

The main program also has `catch terminate(v) { ... }`, a last hook that sees the value given to `terminate` and cannot stop the shutdown. The details are in the threading design document (`docs/THREADING_DESIGN.md`) and in the language specification.

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

## Documenting your code

Put `///` lines directly above a class, field, property or method to document it:

```fire
/// A circle with a radius.
class Circle {
    /// The radius, in millimeters.
    float radius

    /// <summary>Scales the circle.</summary>
    /// <param name="factor">What the radius is multiplied by.</param>
    /// <returns>The new radius.</returns>
    float Scale(float factor) {
        this.radius = this.radius * factor
        return this.radius
    }
}
```

The text appears as a tooltip when you select the symbol in the completion list, when the caret rests on its name, and when you hover over it with the mouse.

## Writing documentation

Markdown documents are first-class citizens in spark: **File → New Markdown Document** creates one with a live preview. You can also open documents *read-only* or as a pure *viewer* (this page is open as a viewer): links to other local Markdown files load in the same tab, `Ctrl+Click` opens them in a new tab, and `Alt+Left` / `Alt+Right` go back and forward.

Fenced code blocks marked with ` ```fire ` are colored just like in the code editor.

## Going further

To run fire scripts from your own .NET application, see [Embedding fire in your application](Embedding.md).
