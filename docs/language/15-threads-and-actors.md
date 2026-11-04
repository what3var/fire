# Threads and actors

fire has **no shared memory** between threads, so there are no data races on ordinary objects. Threads communicate through **copies** that are
written back on request (`taking` and `sync`) and through **actors** that receive messages.

## Starting a thread: `fire`

```fire
#import "time"
fire {
    print("hello from a fire thread")
}
Sleep(100ms)
print("main goes on")
```

- `fire { ... }` runs the block in a new thread. It has no result and ends at the end of its block (or earlier with `leave`).
- Inside a class, `fire Method(args)` runs a method of `this` in a new thread.
- The program waits at its end for all running threads.
- Which thread prints first is decided by the scheduler; the examples use `Sleep` from `#import "time"` to wait.

## Copies: `taking` and `sync`

A thread cannot simply use the objects of the main program. `taking x` gives the thread its own **deep copy** of `x`: everything reachable from `x`
through fields and elements (objects, arrays, buffers) is copied, whoever owns it - an object `x` only refers to, an array literal in a field and the
objects in it. The copies belong to the thread (a copied object that belonged to another copied one belongs to that one's copy).
Changes in the thread are invisible outside - until the thread writes the copy back with `sync x` (the last writer wins):

```fire
#import "time"
class Counter {
    int value
    construct() { this.value = 10 }
}
var counter = new Counter()
fire taking counter {
    counter.value = counter.value + 5       // the thread's own copy
    sync counter                            // write it back to the original
}
Sleep(200ms)
print("main: " + counter.value)             // main: 15
```

| Form | Meaning |
|---|---|
| `sync x` | writes the whole copy back; waits for the lock. `True`, or `undefined` if the original is gone |
| `try sync x` | does not wait: `False` if the original is busy |
| `sync flat x` / `try sync flat x` | writes back only the direct fields (array: the elements) |

- `taking` can be repeated: `fire taking a taking b { ... }`. Numbers and strings can be taken too (`taking i`). An array is copied as well (`taking arr`).
- The copy is made at the `fire`, before the thread starts. A lambda or a pointer in a field or an element cannot be copied: the `fire` throws an
  `ArgumentException` ("'taking' rejected: lambda values cannot be copied for a thread.") and the thread does not start.
- `taking` always copies - nothing is moved to the thread. An instance of a **`[NoCopy]`** class (a resource like `NativeHandle`) cannot be copied: when `x`
  (or a field or an element of it) is one, the `fire` throws an `ArgumentException` and the thread does not start. The same goes for an object that
  `fire global` would copy for its job (it is not queued) and for `sync`, when it would copy one back into the original (nothing is written back);
  `sync flat` keeping an object that is on both sides is fine. The table in [Ownership](13-ownership.md#copying-flat-and-copy) lists every form.
- `sync` returning `undefined` is a good moment for the thread to `leave`.

## Handing over: `handover`

Some objects must not be copied - a connection, a `NativeHandle`, anything marked `[NoCopy]`. `fire handover x` gives `x` itself to the thread,
without a copy: from then on the thread owns it (its destructor runs in the thread, when the thread ends), and the sender's variable is `undefined`:

```fire
#import "time"
[NoCopy]
class Connection {
    int id
    construct(int id) { this.id = id }
    destruct() { print("closed " + this.id) }
}
var conn = new Connection(7)
fire handover conn {
    print("thread uses " + conn.id)
}                                           // closed 7 - at the end of the thread
print(conn == undefined)                    // True - main no longer has it
Sleep(100ms)
```

- Everything reachable from `x` through fields and elements (and what the objects own) goes along, as it is - like `taking`, but moved instead of
  copied. A part whose owner goes along keeps it; every other part belongs to the thread now, whoever owned it before.
- **Every other reference to them is dead.** The objects belong to the thread now; any other reference that existed before the handover - another
  variable, a field, an element, in the sender or anywhere else (also one the thread itself reads from the main program's globals) - is treated like a
  reference to a destroyed object: using it throws a catchable `DestroyedException` with the message
  "Access to an object that was handed over to a thread ('fire handover')." (`an array` / `a buffer`), instead of a data race. Only the thread's own
  variable and the references among the moved objects themselves work. Once the thread has destroyed them, the message is the usual
  "Access to a destroyed object.". What counts as a use: reading or writing a field or an element, `Length`, calling a method, `is of` a class
  (an interface, an enum) and `x!Class`, `throw`, `take`, `foreach` (`copy`/`flat` refuse it like a destroyed object: an `ArgumentException`). Not a use (always answered, never an exception): `==`/`!=` - they compare the
  object, so a dead reference is equal to the one the thread got - `x == undefined` (`False`), `is of class`, and passing or storing the reference.
- This costs nothing outside the `fire`: the moved objects get a new identity at the handover (natively the next generation of their handle slot), so
  the checks that already find a destroyed object find these references too. In `#performance` nothing is checked (as for destroyed objects).
- What the thread later shares itself (an object it assigns to a global variable in `sync global { }`) is a new reference and works, like any
  object a thread shares; the main program then must not change it while the thread does.
- `handover` can be repeated and mixed with `taking`: `fire taking a handover b handover c { ... }`, also `fire Method(args) handover h`.
- A lambda or a pointer in a field or an element cannot go to another thread, and a thread cannot hand over what belongs to the main program's
  global variables: the `fire` throws an `ArgumentException` and nothing moves. A global variable of the main program named in a thread is a compile
  error. Actors and destroyed objects stay shared references. `fire global` has no `handover` (a job gets copies).

## Global variables

A thread can **read** the global variables of the main program. A **change** goes through the main program: the thread waits until the main program
lets it in, one writer at a time. `sync global { ... }` makes several changes one atomic step:

```fire
#import "time"
var total = 0
for (var i = 0; i < 3; i++) {
    fire taking i {
        sync global { total = total + i }
    }
}
Sleep(300ms)
print(total)                                // 3
```

`fire global { ... }` does not start a thread: it queues a **job** that the main program runs with the real globals. When the main program lets the
waiting threads in and runs the jobs is a build setting, `#globaljobs` (the same in the VM and in a native build):

| `#globaljobs` | the main program processes its queue at |
|---|---|
| `safepoints` (default) | the jump back of every loop, `Sleep` (also `Sleep(0)`) and the wait functions, `sync globals`, the end of the program |
| `sleep` | `Sleep` and the wait functions, `sync globals`, the end of the program |
| `sync` | only `sync globals` and the end of the program (`#nosync` means the same) |

```fire
#globaljobs sleep
#import "time"
fire global { print("job") }
for (var i = 0; i < 2; i++) { print("loop " + i) }   // loop 0, loop 1 - a loop is no safe point in this mode
Sleep(1ms)                                           // job
print(sync globals)                                  // 0: nothing left
```

- A call and a native function (`print`) are never such a point; with `safepoints` the job above would run after `loop 0`.
- `sync globals` runs everything that is queued and returns how many entries that were.
- Inside a destructor, a running job and `catch threads` the queue waits; the next job never starts inside another one.
- The project setting `globalJobs` and `--global-jobs` on the command line go before the directive (docs/PROJECTS.md).

## Actors

An `actor` is declared like a class. A method call on an actor does not run the method: it puts a **message** into the actor's mailbox. The thread
that created the actor (its home thread) runs the messages with `process`:

```fire
actor Logger {
    int count
    construct() { this.count = 0 }
    undefined log(string text) {
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
print(try process logger)                   // False - the mailbox is empty
```

- `process a` waits for the next message and runs it; `try process a` runs one if there is one and returns `True`/`False`.
- References to actors may be passed to threads with `fire with a` - the one controlled exception to "no shared memory".
- A thread that needs an answer sends a message back to an actor of its caller; threads have no return values.

## Ending: `leave` and `terminate`

- `leave` ends the current thread at once; its `finally` blocks and destructors still run. In the main program, `leave` ends the main program (it still
  waits for the running threads).
- `terminate()` or `terminate(value)` ends **all** threads and the program, from anywhere. `value` becomes the exit code.
- `catch terminate(v) { ... }` in the main program is a last hook that sees the value; it cannot stop the end.

```fire
fire {
    print("before leave")
    leave
    print("never")
}
catch terminate(v) {
    print("terminating with " + v)
}
terminate(3)
print("never printed")
```

## Errors in threads

An exception that a thread does not catch ends the thread and is delivered to the main program. `catch threads(Type e) { ... }` in the main program
handles it; `catch threads() { ... }` catches the rest. Without a handler, the program stops.

```fire
#import "time"
catch threads(Exception e) {
    print("a thread failed: " + e.message)
}
fire {
    throw new Exception("boom")
}
Sleep(200ms)
print("main is fine")
```

`resume` works only in the thread that threw the exception.

## Exercises

1. Start three threads that each print their number; run the program several times - is the order always the same?
2. Compute the sum of 1..1000 in two threads (1..500 and 501..1000) and add the parts with `sync global`.
3. Write an actor `Bank` with `Deposit(int)` and `Balance()`; let two threads deposit money and process the messages in the main program.
4. Write a thread that checks `sync` in a loop and `leave`s when the original object has been deleted.
5. Catch an exception of a thread with `catch threads(...)` and print which thread failed.
