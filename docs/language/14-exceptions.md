# Exceptions

An exception reports an error that the current code cannot handle itself. It is thrown with `throw`, travels up through the calls and is caught with
`catch`. Exceptions are ordinary objects of classes derived from `Exception`.

## Throwing and catching

```fire
class NotFound : Exception {
    string key
    construct(string key) : base("not found: " + key) { this.key = key }
}

class Store {
    static int Find(string key) {
        if (key == "a") { return 1 }
        throw new NotFound(key)
    }
}

try {
    print(Store.Find("a"))
    print(Store.Find("b"))
    print("not reached")
} catch (NotFound e) {
    print("caught " + e.message + " / " + e.key)
} catch (e) {
    print("something else: " + e.message)
} finally {
    print("finally")
}
```

- `throw value` needs an object of a class derived from `Exception`. Its constructor takes the message (`message`, default `""`).
- The `catch` blocks are checked in order. `catch (Type name)` catches that class and the classes derived from it; `catch (name)` catches everything.
- `finally` runs **always** when the `try` is left: normally, after a `catch`, when an exception passes on, on `return`, `break`, `continue`, `leave`
  and `terminate`.
- An exception that nobody catches stops the program with its message (in the editor a dialog shows it and offers to go on). On the command line
  the program ends with the exit code `1` and writes `Unhandled exception of type 'NotFound': not found: b` to stderr, followed by its
  [stack trace](#stack-traces) - the same text for the VM in C# and the VM in C, for the exceptions of the runtime (`UnitMismatchException`,
  `IndexOutOfBoundsException`, `ArgumentException`, `DestroyedException`, `DivideByZeroException`, ...), for own classes and for an exception of a
  thread that no `catch threads` handles. (A native build writes the first line; its traces follow, docs/spec/STACKTRACE.md.)

## `finally` and `return`

```fire
class T {
    static int Run() {
        try {
            return 1
        } finally {
            print("cleanup")        // runs before the method returns
        }
    }
}
print(T.Run())                      // cleanup, then 1
```

The value of a `return` in the `try` is fixed before `finally` runs. A `return` in `finally` replaces it, a `throw` in `finally` replaces the exception.
`break` and `continue` cannot leave a `finally` block.

## `finally` and `break 2`

`break`, `continue`, `break 2` and `continue 2` run every `finally` they leave on their way to the target loop, the innermost first - also from a
`catch` block. A `throw` in one of them ends the jump: the exception passes on, through the outer `finally` blocks.

```fire
foreach (file in ["a", "b"]) {
    try {
        foreach (line in [1, 2, 3]) {
            try {
                if (line == 2) { continue 2 }   // to the next file
                print(file + line)
            } finally {
                print("close line " + line)
            }
        }
    } finally {
        print("close " + file)
    }
}
// a1, close line 1, close line 2, close a, b1, close line 1, close line 2, close b
```

## `catch` without `try`

A `catch` can stand alone in a block: it protects everything that follows it **up to the end of the block**.

```fire
{
    print("start")
    catch (e) {
        print("block catch: " + e.message)
    }
    throw new Exception("boom")     // caught by the catch above
    print("not reached")
}
print("after block")
```

## Going on after an error: `resume`

`throw` is also an expression. A handler can call `e.resume(value)`: the program continues **at the place of the `throw`**, and the `throw`
expression gives `value`.

```fire
class Reader {
    static int Read() {
        int v = throw new ArgumentException("no value")
        return v + 1
    }
}
try {
    print(Reader.Read())            // 42
} catch (ArgumentException e) {
    e.resume(41)                    // the throw gives 41
}
```

`e.resume()` without a value gives `undefined`. For a `throw` statement the value is ignored and the program goes on with the next statement.
Exceptions are objects: a handler can store `e` or pass it to another method that calls `resume` later.

## Errors of the runtime

The runtime reports its errors as exceptions of the standard library:

| Class | When |
|---|---|
| `IndexOutOfBoundsException` | an index outside an array, list, buffer or string |
| `DivideByZeroException` | `int` division or `%` by zero (floats give infinity/NaN) |
| `ArgumentException` | wrong number of arguments, a value that cannot be converted (`"42"!int`), a condition that is not a `bool`, a value that does not fit its declaration (`Parameter 'a' is an int, not String.`, `undefined` where `defined` is declared); through `any` a value of the wrong kind: an index or an array size that is not an int, `*p` of something that is no pointer, the target of `TakeTo` that is no object, a member of something that is no object; a violation of the ownership rules: `TakeUpwards` with no scope above, a `TakeTo` or `take` that would make a value an owner of itself, a mode that is no `Takes` value (see [Ownership](13-ownership.md#a-reference-does-not-keep-anything-alive)) |
| `MissingMemberException` | a method or field that the object does not have - reading or writing it (the fields of a class are fixed) |
| `AccessDeniedException` | a `private`/`protected` member used from outside |
| `DestroyedException` | using (also printing) an object, array, buffer or lambda that was destroyed, or one that was handed over to a thread; `Take...` of a destroyed one |
| `UnitMismatchException` | units of different dimensions (`5mm + 2kg`) |
| `OverflowException` | a converted number does not fit into its declared unit |
| `CancelledException`, `TimeoutException` | a cancelled task at its next wait; a time limit ran out (`until 2s`, `Wait(limit)`; a `TimeoutException` is a `CancelledException`) - see [Threads and actors](15-threads-and-actors.md#cancellation-until) |
| `Assertion` | a check of a test did not hold (`Testing.Assert`, Testing: forge test (`docs/tools/testing.md`)) - thrown by tests, never by the runtime |

All derive from `Exception` and can be caught:

```fire
try {
    print(1 / 0)
} catch (DivideByZeroException e) {
    print("div: " + e.message)
}
try {
    var a = [1]
    print(a[3])
} catch (IndexOutOfBoundsException e) {
    print("index: " + e.message)
}
```

## Stack traces

An exception remembers where it was thrown: the functions on the stack at its **first** `throw`, the innermost first, each with its file and line.

```fire
class Store {
    static int Find(string key) { throw new NotFound(key) }
    static int Lookup(string key) { return Store.Find(key) }
}
try {
    Store.Lookup("b")
} catch (NotFound e) {
    print(e)                       // NotFound: not found: b
    print(e.StackTrace)            //    at Store.Find (main.fire:2)
                                   //    at Store.Lookup (main.fire:3)
                                   //    at <main> (main.fire:6)
    foreach (f in e.Frames) {      // StackFrame: function, file, line
        print(f.function + " " + f.file + ":" + f.line)
    }
}
```

An exception that nobody catches writes the same lines below its message:

```
Unhandled exception of type 'NotFound': not found: b
   at Store.Find (main.fire:2)
   at Store.Lookup (main.fire:3)
   at <main> (main.fire:6)
```

| Member | What |
|---|---|
| `e.StackTrace` | the trace as text: a line `   at Function (file:line)` per frame (without a line feed at the end); `""` for an exception that was never thrown or a program without traces |
| `e.Frames` | the same frames as `StackFrame` records: `function`, `file` (`""` for the prelude), `line` (`0`: not known, or `#stacktrace calls`) |
| `e.ToString()`, `print(e)`, `"" + e` | `Class: message` - without the trace (a class with a `ToString()` of its own keeps it) |

- **The function**: `Class.Method` (a getter or setter by its method, `Class.get_Name`), `Class.construct`, `Class.destruct`, `Class.<init>` (the
  initializer of a field, the default value of a parameter), `<main>` (the code at the top level), `<lambda>`, `<fire>` (the body of a `fire` block); a
  generic class without its type arguments (`List.Add`).
- **The file** is relative to the folder of the program's first file (`main.fire`, `lib/util.fire`); a source without a file is `fire:source/0`; the code
  of the prelude shows `(prelude)` without a line.
- **The first throw counts**: `catch (e) { ...; throw e }` keeps the original trace, so does an exception that passes a `finally` and one that is
  `resume`d and thrown again; a `throw` inside a `catch` block shows the stack from the function the `catch` belongs to. Whoever wants a new trace
  throws a new exception.
- **Deep stacks** show at most 64 frames: the 48 innermost, a line `   ... 1234 more frames ...`, the 16 outermost.
- **Threads**: the trace of an exception of a `fire` thread ends with the thread's line, `   in thread 'Fire #1', started at main.fire:40` (the threads
  are counted in the order they start). An exception of a task that is thrown again where it is awaited (`Completion.Wait()`, `await actor.M()`, `await on`)
  gets a second part: `   --- awaited at ---` and the stack of the one that waited.
- **The setting** `#stacktrace off | calls | lines` ([Directives](17-preprocessor.md)), `"stackTrace"` in the project, `--stacktrace` on the command line,
  `"stackTrace"` of a target in fire.native.json: `lines` (the default in **release**) shows functions and lines, `calls` only the functions
  (`   at Store.Find (main.fire)`), `off` (the default in **performance**) nothing - `e.StackTrace` is `""`. A debug build always shows lines. The VMs
  record every throw, but show what the setting says; the design and its costs: docs/spec/STACKTRACE.md.
- `StackTrace` and `Frames` are not fields: reflection, JSON and `==` do not see them, a copy of the exception (`taking`, the end of a thread, a
  `Completion`) has the same trace.

## Exceptions and ownership

When an exception leaves a block or a method, the objects of that scope are destroyed as usual (destructors run) before the exception is caught.
With `resume` nothing is left, so nothing is destroyed. See [Ownership](13-ownership.md).

## Exceptions in threads

An exception that is not caught in a `fire` thread ends that thread and is delivered to the main program. There, `catch threads(Type e) { ... }`
catches it; without such a handler the program stops - its trace ends with the line of the thread (`in thread 'Fire #1', started at main.fire:2`). See
[Threads and actors](15-threads-and-actors.md#errors-in-threads).

## Exceptions in callbacks

A lambda that a native function calls back - an event handler of a window during `win.Tick()`, a lambda handed to a package - may throw. The exception
does not jump through the native function: the native finishes, its other callbacks do not run, and then **the call of the native throws the
exception** (the same object). So `try { win.Tick() } catch (e) { ... }` catches what a handler threw. A callback that runs where nobody waits for it (at a
safe point, from another thread) has its exception printed on the error output, and the program goes on. Details: SPEC 8.1.4 (docs/spec/SPEC.md).

## Exercises

1. Write `static float Divide(float a, float b)` that throws an `ArgumentException` for `b == 0`, and catch it.
2. Define your own exception `ValidationException` with a field `field` and throw it from a method that checks a form.
3. Show with `print` that `finally` runs after `return`, after `break` and after an exception.
4. Use `resume` to replace a missing configuration value by a default.
5. What is printed, and in which order, when a destructor and a `finally` are both involved? Write a small program to find out.
