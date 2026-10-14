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

A thread cannot simply use the objects of the main program. `taking x` gives the thread its own **deep copy** of `x` (with everything `x` owns).
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

- `taking` can be repeated: `fire taking a taking b { ... }`. Numbers and strings can be taken too (`taking i`).
- A copy must not point to objects outside its own ownership tree; `taking` refuses such an object with an error instead of sharing memory.
- `sync` returning `undefined` is a good moment for the thread to `leave`.

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

The main program lets waiting threads in at safe points (after `Sleep`, a window event, ...). With `#nosync` at the top of the program it does so only
at an explicit `sync globals` (and at the end of the program). `fire global { ... }` does not start a thread: it queues a job that the main program
runs at its next `sync globals`.

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
