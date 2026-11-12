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
- Inside a class, `fire Method(args)` runs a method of `this` in a new thread. Its arguments are copied like `taking` - except a direct `new`
  (`fire Work(new Job())`), which is handed over to the thread without a copy.
- The program waits at its end for all running threads.
- A fire thread is a light task on a pool of system threads (one per core) - in a native build and in the VM; thousands of threads that wait cost no
  system thread each. `fire dedicated { ... }` (also
  `fire dedicated Work(x)`, with the same clauses) gives it a system thread of its own - for work that waits in natives for a long time. In the VM
  every fire thread is a system thread anyway. `dedicated` is a word only right after `fire` or `await`.
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
- The copy is made at the `fire`, before the thread starts. A lambda is copied with what it captured (see [Lambdas in threads](#lambdas-in-threads)).
  A raw pointer in a field or an element cannot be copied: the `fire` throws an `ArgumentException` ("'taking' rejected: raw pointers cannot be copied
  for a thread.") and the thread does not start.
- `taking` always copies - nothing is moved to the thread. An instance of a **`[NoCopy]`** class (a resource like `NativeHandle`) cannot be copied: when `x`
  (or a field or an element of it) is one, the `fire` throws an `ArgumentException` and the thread does not start. The same goes for an object that
  `fire global` would copy for its job (it is not queued) and for `sync`, when it would copy one back into the original (nothing is written back);
  `sync flat` keeping an object that is on both sides is fine. The table in [Ownership](13-ownership.md#copying-flat-and-copy) lists every form.
- `sync` returning `undefined` is a good moment for the thread to `leave`.

## Taking where it is used: `taking x`, `handover x` in the block

Instead of listing what a thread takes in front of the block, the block can say it where it uses the variable. The compiler collects these words before
the thread starts - the thread gets a copy of each `taking` variable (as with `fire taking x`) and takes over each `handover` variable (as with
`fire handover x`, the caller's variable is `undefined` afterwards):

```fire
#import "time"
class Item { int v
    construct(int v) { this.v = v } }
class Work {
    static undefined Run() {
        var list = [new Item(1), new Item(2)]
        var node = new Item(7)
        fire {
            (taking list)[0].v = 100                     // the thread's copy
            print(list[0].v + " " + (handover node).v)   // 100 7 - `list` is the copy everywhere in the block
        }
        Sleep(20ms)
        print(list[0].v + " " + node)                    // 1 undefined
    }
}
Work.Run()
```

- One word per variable is enough: every other use of it in the block means the same copy (or the moved object). `taking` and `handover` for the same
  variable in one block is an error.
- A local of the code around the block that the block uses **without** a word is copied too. When the copy costs memory - an object, an array, a string,
  a value of unknown type - the type checker says so: `TC023`, a warning in lax mode, an error in strict mode (a number is copied silently). The globals
  of the main program are no locals: the thread reads them directly (see [Global variables](#global-variables)).
- The same words work in a **lambda**: `() => (taking item).v` captures a copy of `item`, made when the lambda is made; `() => (handover item).v` moves
  the instance into the lambda like a handover to a thread - `item` is `undefined` afterwards and every other reference from before is dead (a
  `DestroyedException`). Without a word a lambda gets a copy, like `taking`, with the warning `TC023`. A `taking` copy is new in every call of the lambda;
  the `handover` instance belongs to the lambda and is lent to each call (see [Lambdas](12-lambdas.md#captured-values)).
- A lambda inside a `fire` block captures the block's own variables like locals (`fire { var n = 41; var f = () => n + 1 }`) - so `await [ ... ]`
  works in a `fire` block too.

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
- A lambda moves with what it owns (see below). A raw pointer in a field or an element cannot go to another thread, and a thread cannot hand over what
  belongs to the main program's global variables: the `fire` throws an `ArgumentException` and nothing moves. A global variable of the main program named in a thread is a compile
  error. Actors and destroyed objects stay shared references. `fire global` has no `handover` (a job gets copies).

## Lambdas in threads

A lambda goes to a thread like any other value - as a `taking` copy, moved with `handover`, or in a field or an element of what the thread gets:

- **`taking`** gives the thread a copy of the lambda with copies of what it captured (its store, see [Lambdas](12-lambdas.md#captured-values)).
  **`handover`** moves the lambda with its store; the sender's variable is `undefined` afterwards.
- A lambda without `on` then simply runs in the thread, on its own copies.
- A lambda **bound with `on`** keeps its target. Its body always runs where the target lives: called in a thread with an object of the main
  program as its target, the call is a job of the main program (like `await on obj { ... }`). The arguments are copied for it, the thread waits -
  and yields meanwhile - until the result comes back; an exception the body does not catch is thrown at the call in the thread. So the lambda changes
  the original object, not a copy, and nothing of the main program is touched by two threads at once.
- In the main program the same lambda is called directly. The same holds for the other homes of a target:
  - **an object of a fire thread**: the call goes to that thread, which runs it at its safe points - the jump back of a loop - and while it waits
    itself (`Sleep`, `await`, a call of its own that runs elsewhere). Two threads that call each other's lambdas therefore do not block each other.
    Has the thread ended, its objects are gone: the call is an `ArgumentException` ("... a fire thread that has ended ...");
  - **an actor**: the call is a message in its mailbox, its home runs it at `process` (like `await actor.M()`); on the actor's home itself it runs
    at once.

```fire
class Counter { int n = 0 }
class Demo {
    static undefined Run() {
        var c = new Counter()
        var add = func (int k) on c => { n = n + k; return n }
        var sum = await taking add {
            return add(5) + add(2)          // both run in the main program, on c itself
        }
        print(sum + " " + c.n)              // 12 7
    }
}
Demo.Run()
```

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

What a thread writes into the globals - a global variable or static field, a field or an element of one of their objects or arrays - **belongs to
the main program** from then on, and a **method** of one of their objects runs **in the main program** (the thread waits for its result or exception,
like with `await on`; a `Sleep` in it does not hold up the main program). So an object of the thread goes there only with a word that says what
happens to it:

```fire
#import "time"
class Box { int v; construct(int v) { this.v = v } }
class Store {
    List<Box> items = new List<Box>()
    undefined Put(Box b) { this.items.Add(take b) }
}
var store = new Store()
Box last = undefined
var done = false
fire {
    var mine = new Box(7)
    last = new Box(1)                           // fresh (`new`, the result of a call): it moves
    store.Put(copy mine)                        // a copy goes, `mine` stays the thread's
    store.Put(take mine)                        // `mine` moves - every other reference to it is dead, as after `handover`
    var other = new Box(3)
    try { store.Put(other) } catch (ArgumentException e) { print(e.message) }   // no word: refused
    done = true
}
while (!done) { Sleep(1ms) }
print(last.v + " " + store.items.Count)        // 1 2 - they outlive the thread
```

- Values (numbers, strings, ...) go as they are; so do objects of the main program itself, actors and destroyed objects.
- Without a word the store is the ArgumentException "... belongs to the main program: an object of this thread goes there only with a word - 'copy x'
  (a copy) or 'take x' (moved)" and nothing is stored; the call is the ArgumentException "'Put' runs in the main program ..." and does not run.
  Inside `sync global { ... }` the same - only a method called there runs in the thread (the section holds the main program anyway).
- What moves leaves the thread like `handover`: it is the main program's, also after the thread has ended.
- Reading stays direct: a thread sees the globals as they are, without a job.

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
- The project setting `globalJobs` and `--global-jobs` on the command line go before the directive ([Projects and solutions](../tools/projects.md)).

## Waiting for a result: `await`

`fire` starts something and goes on; `await` waits for it and gets its result:

```fire
#import "time"
class Item { int v = 0
    construct(int v) { this.v = v } }
class Stock {
    static int Count(Item list[]) { Sleep(10ms); return list.Length }
}
var list = [new Item(1), new Item(2)]
var n = await Stock.Count(list)
var sum = await taking list { var s = 0; foreach (var i in list) { s = s + i.v }; return s }
print(n + " " + sum)
```

- `await Call(args)` waits until the call is done and gives its return value. The caller is stopped meanwhile, so nobody else can reach its objects:
  the call gets them **lent**, without a copy. A copy only on request: `await Count(list) taking list` (or `handover list`: moved).
- `await taking x { ... return v }` runs a block that sees only what it takes (copies) and the globals, like the body of `fire`.
- An exception in the awaited code goes on at the `await`, with its type: `try { await Load(name) } catch (IoException e) { ... }`.
- While the awaited code waits (`Sleep`, the net, a section), the caller's task gives its worker to other tasks - on all cores, without a thread of its
  own per call. `await` is a word only in front of what it awaits; elsewhere `await` is an ordinary name.
- `await [f(a), g(b), taking x { return ... }]` runs several calls at once and waits for all; the result is an array of their results in the order of
  the brackets. When some fail, all are waited for and the first exception in that order is thrown. Each element is what a single `await` takes, clauses
  included (`await [Load(a), Merge(x, y) taking y]`). The calls get the caller's objects lent; in a native build they run side by side on all cores when
  no two of them reach the same object (otherwise one after the other); the VM runs them one after the other.
- `await actor.Method()` waits for the result of an actor's method (see [Actors](#actors)).
- `fire on window { ... }` runs a block in the main program - where its windows live - with `window` as the real object (and copies of what it takes with
  `taking`); the caller goes on. `await on window { return window.Title }` waits for the block's result. From a thread the object must be one of the main
  program (a global variable) or an actor. In the main program itself `await on` runs the block at once and `fire on` at its next safe point.
  `fire on window taking x with actorA, channelB { ... }` gives the block references as they are, like `fire with` (an actor; a channel as an end of
  its own - the block can answer through it).
- An event handler of a window is a task of the main program's group (natively and in the VM): when it waits (`Sleep`, `await`), the window goes on, and the
  handler continues later on the same thread. Only one handler runs at a time, and only where it waits does another one get its turn.
- While the main program waits (`Sleep`, `await`, the end of the program), its windows keep answering the system: they repaint and stay responsive;
  their events wait for the next `Pump`/`Tick` (natively and in the VM).
- `await dedicated Work(x)` (also `await dedicated taking x { ... }`, and as an element of `await [ ... ]`) is `await` for work that blocks in natives
  for a long time: in a native build the waiting task runs the call on a system thread of its own and goes back to the pool after it - the call still
  has the caller's objects lent, and an exception goes on at the `await` as usual. Elsewhere it is the same as `await`.

### Cancellation: `until`

```fire
#import "time"
class Work { static int Slow() { Sleep(1s); return 1 } }
try { var r = await Work.Slow() until 50ms } catch (TimeoutException e) { print(e.message) }   // The time limit ran out.
var source = new CancellationSource()
fire until source.Token {
    try { Sleep(10s) } catch (CancelledException e) { print("cancelled") }
}
Sleep(20ms)
source.Cancel()                                  // the thread's Sleep ends at once: cancelled
```

- `until` limits what it stands after: a token (`source.Token`), a source, or a time value (`5s` - then the exception is a `TimeoutException`, a kind of
  `CancelledException`). Several at once: `until source.Token, 10s`. It works with `await Call()`, `await [ ... ]` (all calls; one element:
  `await [Load(a) until 2s, Load(b)]`), `await until x { ... }`, `fire until x { ... }` and `fire Method(args) until x`.
- An awaited call follows its caller's token as well: cancelling the caller cancels it. A `fire` thread does not - only what its `until` names.
- A cancelled task notices it at its next wait - `Sleep`, `await actor.M()`, `await on`, the waits of the net package - which throws the exception, or with
  `CancellationToken.Current().ThrowIfCancelled()`. Code that never waits is not stopped.
- `new CancellationSource()` made inside a task follows the task's token; `CancellationSource.Detached()` follows nobody. `CancelAfter(5s)` cancels later.
  Sources and tokens are never copied (`[NoCopy]`): a thread gets one with `until`.

### A value that another task delivers: `Completion`

```fire
#import "time"
var c = new Completion()
var s = c.Share()                                // a second Completion on the same value, for the other task
fire handover s {
    Sleep(20ms)
    s.Set("ready")
}
print(c.Wait(2s))                                // ready - waits until the value is there (at most 2 s)
```

- `Set(value)` delivers the value; only the first `Set` (or `Fail`) counts, a later one returns `False`. The Completion keeps a **copy** of the value
  (objects, arrays and buffers deep, like `taking`), and every `Wait()` returns a copy of its own - the tasks never share it.
- `Fail(exception)` delivers an exception instead: `Wait()` throws (a copy of) it. `IsDone` asks without waiting.
- `Wait(limit)` waits like `Sleep` - the task gives its worker to others, the main program keeps serving its windows and its queue - until the value is
  there. It follows the task's token (`until`, a cancelled source: `CancelledException`), and after `limit` it throws a `TimeoutException`; without a
  limit it waits as long as it takes.
- A Completion is never copied (`[NoCopy]`): `Share()` gives another one on the same value, which goes to a thread with `handover`, as the argument of an
  actor's method (`reader.Expect(id, c.Share())`) or lent to an awaited call. The value lives as long as one of them does.

## Channels

A `Channel<T>` is a queue between threads: `Send(v)` puts a value in (it waits while the channel is full), `Receive()` takes the next one out (it
waits while it is empty). Waiting does not hold anything up: the scheduler runs other tasks meanwhile. A thread gets a channel with `fire with` - an
end of its own on the same queue:

```fire
#import "time"
class Job { int n; construct(int n) { this.n = n } }
var jobs = new Channel<Job>(4)      // at most 4 waiting values (the default: 16)
var results = new Channel<int>()
fire with jobs, results {
    foreach (job in jobs) { results.Send(job.n * job.n) }   // until the channel is closed and empty
    results.Close()
}
for (int i = 1; i <= 3; i++) { jobs.Send(new Job(i)) }
jobs.Close()
int sum = 0
foreach (r in results) { sum = sum + r }
print(sum)                          // 14
```

- A value (a number, a text, a bool) goes as it is. An object or an array goes only fresh (`new`, the result of a call) or with a word:
  `Send(copy x)` (a copy) or `Send(take x)` (moved - like `handover`, every other reference to it is dead; a record keeps its identity, because
  nobody can change it). Without one it is TC029 (an error, also in lax mode) where the type is known, else the ArgumentException.
- A record without a word is **lent**: `Send(r)` - the sender stays its owner, the receiver gets a reference and reads it in its own thread (fields,
  methods, `==`, `copy r` for one of its own; sending it on lends it again). Only the owner moves it: `take r` in the receiver is the
  AccessDeniedException. The lending lasts as long as the record does: when the owner destroys it (its scope ends), the next use in any thread is
  the DestroyedException - a read that is under way at that moment still ends normally (natively the memory goes only once no reader can be looking at
  it any more). When the owner moves it on (`take r`, `handover`), the receivers' references end the same way:

```fire
record Note { string text; construct(string t) { this.text = t } }
var notes = new Channel<Note>()
var read = new Channel<int>()
var gone = new Channel<int>()
fire with notes, read, gone {
    var n = notes.Receive()
    print(n.text)                   // hello - lent, not copied
    read.Send(1)
    gone.Receive()
    try { print(n.text) } catch (DestroyedException e) { print("gone with its owner") }
}
if (true) {
    var note = new Note("hello")
    notes.Send(note)                // lent: `note` is still this scope's
    read.Receive()
}                                   // the scope ends: the note dies - in the thread too
gone.Send(1)
```

- What travels belongs to the channel; after `Receive` it belongs to the receiver - when the receiving thread ends, it ends with it. What nobody
  received is destroyed with the channel.
- `Receive(limit)` waits at most `limit` (the TimeoutException); `TryReceive()` does not wait - undefined when nothing is there. A closed and empty
  channel gives undefined (`foreach` ends there).
- `Send` gives False when the channel is closed (nothing moved). `Close()`, `IsClosed`, `Count`.
- The channel closes when the one that made it ends. It is never copied (`fire taking ch` is the ArgumentException) - it goes with `with`.
- Both directions: `Channel.Pair<A, B>()` gives an end that sends `A` and receives `B`; `.Other()` is the end that sends `B` and receives `A` (it goes to
  the thread with `fire with`). `Close()` ends the direction of the end it is called on:

```fire
#import "time"
var left = Channel.Pair<string, int>()
var right = left.Other()
fire with right {
    foreach (s in right) { right.Send(s.Length) }
}
left.Send("hello")
print(left.Receive())               // 5
left.Close()
```

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
- References to actors may be passed to threads with `fire with a` (several: `fire with a, b`; together with `taking`/`handover`) - the one
  controlled exception to "no shared memory". An actor that a thread reaches
  through a field or an element of what it takes (`taking`, an argument of `fire Method(...)`) is not copied either: its reference goes along, so a
  router or a service object that holds an actor can be taken to a thread and still talks to the one actor.
- A thread that needs an answer awaits it: `var n = await counter.Get()` posts the message and waits until the actor's home has run it at its
  `process`; the result comes back (an exception that nothing in the method caught is thrown at the `await`). What the method made (`return new Box()`)
  is handed over; what the actor keeps - a field, a global - comes back as a copy. On the actor's home thread `await a.M()` runs the method at once,
  like a call (waiting for its own `process` would never end). Threads themselves have no return values.
- The arguments of a message: a direct `new` (`logger.log(new Entry())`) is handed over to the actor's home thread; another new value (`copy x`, a call
  result) belongs to the message and then to the method that runs it; a variable or a field is passed as a reference and stays with its owner.

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
