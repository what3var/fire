# Tasks, `await` and multicore - roadmap

Status: **design, nothing built.** fire was meant to be a nicer counterpart to async/await; today it has `fire` (a thread, no result) but no way to wait for
a result. This plan adds `await`, a scheduler with a pool of workers in the C runtime, and removes the global lock so that fire code runs on all cores.
It is the base of the HTTP server (docs/ROADMAP_HTTP.md). Open points are marked **open**.

## 1. Where we are

* **VM (C#):** every fire thread is a real .NET thread with its own VM instance - already parallel, no global lock. The globals of the main program
  are protected by a reader/writer lock (`ThreadShareLock` in `GlobalsBroker`): threads read them directly, changes go through sections that the main
  program grants. The model "no shared memory" works on several cores there.
* **Native (C runtime, `native/runtime/fire_rt.h`):** every fire thread is a real thread, but all fire code runs under one **global lock (GIL)**, a fair
  ticket lock that is handed on at safe points (loop back edges, function entry, every 4096 stations, waits). Only one core works.

## 2. Multicore: removing the GIL from the C runtime

What the GIL protects today, and what replaces it:

| What | Without the GIL | Replacement |
|---|---|---|
| state of a strand (`g_unwind`, pool, handlers, ...) | fine, already `thread_local` (later: per task, section 4) | - |
| objects, owner lists | fine, every object belongs to exactly one thread | - |
| **globals of the main program** (threads read them directly) | torn values, use after free | reader/writer lock as in the VM; the objects of the global scope are already marked (flag 64, `domainLink`) |
| **reference counts** of strings (`rc++`, not atomic) | strings cross threads (globals, `taking`, results) | **a string belongs to one thread**: its counts stay plain; where it crosses a thread boundary (`taking`, `fire` arguments, `sync`, messages, results, exceptions of a thread) it is **copied** like the objects. Only the global scope is shared: a string stored there (a global, a field of an object of the global scope, flag 64) once the first `fire` ran is marked *shared* and counts atomically. A lock alone is not enough there, because a count taken under the lock is let go after it; literals stay immortal |
| slot table of generations (`g_slots`) | `realloc` while others read, one free list | segments that never move, free slots cached per thread, generations read and written atomically |
| global maps: probes, mailboxes, `originOf`/`copiesOf`, run-time units, host services, thread list | changed at the same time | one small mutex each (rarely used); probes may move to the object later |
| natives of packages (today under the library's lock) | not written for parallel calls | one lock per package by default; a package that declares itself thread safe in the ABI runs without it |
| `terminate`, the debugger | need every thread to stop | through the existing safe points (`g_attn`) |

Check: the thread tests of the suite on several cores, plus a run under ThreadSanitizer.

### Step 1 in detail: what the GIL protects in `fire_rt.h` today (inventory)

The state of a thread is already `thread_local` (`FIRE_THREAD_VARS`: unwinding, pool, handlers, global scope, sections, ...). What is shared:

| State | Changes at run time? | Plan |
|---|---|---|
| `g_enums`, `g_unitTo/From`, `g_textUnits`, `g_classDescs`, `g_resources`, `g_argc/argv`, `g_enumerateItems`, `g_defaultTimeoutTicks`, `g_terminateFn`, `g_globalJobs` | no - set before `fire_main` (`fire_init`, unit init) | nothing (read-only); an assertion in debug builds that they are not written after the first thread started |
| `g_slots` (generations, free list, `born`/`freed`) | yes - every object, array, buffer | 1c: segments that never move (a table of segment pointers), free slots cached per thread and refilled in batches under a mutex, generations as atomics |
| reference counts (`retain`/`release`, `rc++`) | yes - strings cross threads (globals, `taking`, results, messages) | 1b: strings are copied at the thread boundary (like objects); strings of the global scope are marked shared and count atomically (`retain`: one branch on the bit) |
| `g_ud`, `g_udCount` (units made at run time) | yes, rarely | 1d: a mutex for adding; readers see a published count (release/acquire) |
| `g_fieldPtrs`, `g_targsHead`, `g_probeNodes`/`g_probeNextId`, `g_pluginRf`, `g_hostServices`, `g_threadsCatches` | yes, rarely | 1d: one small mutex each |
| `g_mailboxes`, `g_originOf`/`g_copiesOf` (actors, copies across threads) | yes | 1d: one mutex each (the maps are only touched at copies and messages) |
| `g_threadList`, `g_threadsLive`, `g_pendingExc`, `g_graveyard`, `g_reqs` (sections), `g_terminate` | yes | 1d: under the existing event mutex (`g_evM`) where they are already signalled; sections keep their protocol |
| globals of the main program and the objects of the global scope (`g_domainOn`, `g_domainRoot`, flag 64) | yes - threads read them, sections write them | 1e: a reader/writer lock as in the VM (`ThreadShareLock`): reads by threads under the read lock, sections and the main program's writes under the write lock |
| natives of packages | they assume one caller at a time | 1f: the GIL becomes the lock of the package (`fire_pkgLock` exists for libraries already); a package can declare itself thread safe |
| the debugger (`g_dbgArmed`, its threads), `terminate` | stop the world | through the safe points (`g_attn`) as now - the GIL hand-over points stay the safe points |

Order, each step with the GIL still on (no change of behaviour, the suite stays green), the last one switches it off:

1. **1a** a build switch `FIRE_NO_GIL` and the test setup: the thread tests of the native group also with `FIRE_NO_GIL` and under ThreadSanitizer (`-fsanitize=thread`), on 4 cores.
   *Begun:* `FIRE_NO_GIL` makes `gilAcquire`/`gilRelease` empty. With the GIL a program with two fire threads, `taking` and `sync global` is clean under
   ThreadSanitizer (gcc, 4 cores); without it ThreadSanitizer reports 9 races, all where the inventory expects them: the queue of section requests
   (`reqPush`/`reqPop`/`drainQueue`/`sectionAcquire`/`sectionRelease`, 1d) and the slot table (`slotAcquire`, `allocArr`, 1c).
   *Done:* the test "ThreadSanitizer: die Thread-Faelle laufen ohne Data Race (mit GIL)" of the native group builds all thread cases (`Threads:`) with
   `-fsanitize=thread` and fails on any report; `FIRE_TEST_NO_GIL=1` builds the same cases without the GIL - the work list of 1b-1f. Before 1c/1d that run
   had races in 42 of the 52 cases (the first of each: the section queue, the slot table, the thread list and count, mailboxes, `terminate`, the globals).
2. **1b** strings per thread: copied at the thread boundary, shared (atomic) only in the global scope. *First part done* (`FIRE_STR_CROSS`, with
   `FIRE_NO_GIL`): `strCross` copies a string where it would cross - `taking` (the copier's `cross`), `sync` (case A and every string that goes back),
   `handover` (the strings in the moved parts), the arguments of `fire global` and of actor messages, the value of `terminate`. The bit `R_SHARED` in the type
   byte of a string or lambda makes its counts atomic (`retain`/`release` look at it first: one byte, a branch that is never taken in a program without
   threads); the lambda of a `fire global` job carries it (made in the thread, let go of by the main program). `sync` writes into its original inside a
   section, `originOf`/`copiesOf` have `g_originM`. *Second part done (with 1e):* a string or lambda gets `R_SHARED` when it goes into the shared domain -
   a store into a global or static field (`FIRE_DOM_SHARE`), a field or an element of an object or array of the domain (`FIRE_DOM_STORE` in `setFieldAt`,
   `arraySet` and the generated setters), and at the first `fire` everything the domain holds already (`markOwnedShared`, the program's
   `fire_shareGlobals`). Whoever writes the domain holds its write side, so nobody counts the string at that moment. An original of another fire thread (not the main program) - `fire taking a { ... sync a }` inside a
   thread: rare, but allowed fire and correct in the VM (a lock per tree) and with the GIL, so without the GIL it was a regression (a race, a use after
   free when the owner destroys the original) - is now a section of its owner: `taking` notes the owner's `ThreadRec` beside the original, `sync` asks it
   (`threadRecAsk`), the owner grants it at its next safe point (`ATTN_PEER`) or while it waits (`blockUntil` serves it) and waits until the sync is done; an
   owner that ends destroys its originals first (`sync` says undefined) and then grants what is still asked. A sample where the owner reads the original
   while a child syncs into it: 14 races before, none after. In step 4 this becomes the mailbox of a task.
3. **1c** the slot table in segments. *Done* (only with `FIRE_NO_GIL`; the GIL build keeps the one array): `FIRE_SLOT_SEGMENTS` - a directory of 2^14
   segments of 2^14 slots that never move (the same two loads as before), the free slots cached per thread (64, exchanged half by half with the shared
   stack under `g_slotM`, given back when a thread ends), generations read and written atomically (relaxed: plain moves on x86 and arm).
4. **1d** the maps and lists with their mutexes. *Done for the threads' own tables:* `g_reqM` (the queue of sections and jobs; a section request
   counts its two holders atomically and is handed over with release/acquire), `g_mailboxM` (actors), `g_thrM` (the thread list, their exceptions, the
   graveyard, the value of `terminate`; `terminate` requested and the live count are atomic). The platform layer got `fire_atomic_loadAcquire`,
   `fire_atomic_storeRelease`, `fire_atomic_addAcqRel`. Still open: probes, `originOf`/`copiesOf`, run-time units, host services.
5. **1e** the reader/writer lock of the globals. *Done* (`DomLock`, with `FIRE_NO_GIL` it takes the place of the GIL): the main program holds the write
   side while it runs and lets go of it where it let go of the GIL (waits, blocking natives) and, when threads wait to read, at its safe points; a fire
   thread takes the read side when it first touches the shared domain - `FIRE_DOM_TOUCH` in `leafOf` (one test of flag 64 in the header; every access to
   an object, array or buffer goes through it), `FIRE_DOM_READ` before a thread reads a global or a static field - and lets go of it at its next safe point
   or wait; threads read side by side, a thread that never touches the domain never waits. A section holds the write side alone. A program with two
   threads reading global strings and fields of a global object while the main program rewrites them is clean under ThreadSanitizer without the GIL.
6. **1f** a lock per package instead of the GIL around natives. *Done:* in a program with fire threads every call of a package's native is between
   `FIRE_PKG_ENTER`/`FIRE_PKG_LEAVE` of that package's lock (`fire_pkgM`, nothing with the GIL; a typed native in C then goes through its Value). A thread
   takes the read side of the globals before a package's lock, so the main program, holding the write side, never waits for a package's lock; the lock is
   given back where the GIL was (`gilRelease` in a native that waits, the safe points of a callback). `"threadSafe": true` in the manifest: no lock.
7. **1g** `FIRE_NO_GIL` by default: `gilAcquire`/`gilRelease` only mark safe points; ThreadSanitizer clean on the thread tests. *Done:* the default where
   the platform's threads are real (`FIRE_PLAT_PARALLEL` from the posix and windows layers) and the compiler has atomics; the targets with a scheduler of
   fire itself (dos, win16, wasm), FreeRTOS and TinyCC (`FIRE_TLS_STRUCT`), the modules (`FIRE_MODULES`, `FIRE_DLL` - no package locks and no
   `fire_shareGlobals` there yet) and debug builds (the native debugger stops the threads through the GIL) keep the GIL; `FIRE_GIL` keeps it anywhere.
   The ThreadSanitizer test builds the default (`FIRE_TEST_GIL=1`: with the GIL). Left for later: the debugger without the GIL, the modules.

### Step 2 in detail - state per task

*Done:* `FIRE_TASK_STATE` (the default without the GIL; `FIRE_TLS_VARS` keeps the thread-local variables). The state of a strand - the list
`FIRE_THREAD_VARS` in `fire_rt.h`: `g_unwind`, the temporary pool, the handlers, the slot cache, the reader flags, ... - is one structure (`ThreadLocals`);
a worker holds only a pointer to the running one (`fire_curTask`, thread-local). A pointer read before a switch is still the task's after it - unlike the
address of a thread-local variable, which a compiler may keep across a call and which names the old worker's after a move. Every access is
`fire_curTask->name`, read without a test: every strand gets its structure before it runs fire code (the main program in `fire_threadsInit`, a fire thread
in `threadEntry`; a thread of a package that calls into the runtime: `fire_strandAttach()`/`fire_strandDetach()` - the threads of the packages today
(audio, circuits, scene3d, the window pump) do not). Measured with four strands on four cores (objects, methods, strings; the best of 7 runs): 383 ms with
thread-local variables, 399 ms with the structure (a test for a missing structure on every access cost 12 % - it is gone), 487 ms with the GIL; the code is
as big. Left per worker on purpose: two caches of unit factors and the last error text of circuits (pure caches, or read right after the call that set them -
no switch in between). FreeRTOS, TinyCC and the scheduler of fire itself (`FIRE_TLS_STRUCT`) and the modules (`FIRE_DLL`) had a structure already; the
switch of a task (step 3) sets `fire_curTask`.

## 3. The language: `fire` and `await`

```
fire Log(text)                                  // fire and forget: starts at once, no result, nothing to wait for
fire taking list { Save(list) }

var n = await Count(list)                        // runs as a task; the caller yields until it is done and gets the return value
var s = await taking list { return list.Sum() }  // a block with return
var r = await [Load("a.json"), Load("b.json")]   // several at once, waiting for all: the results as an array
```

* **`fire`** stays fire and forget and returns nothing (no future). Arguments are copied by default (`copy`); `flat` or `handover` on request.
* **`await`** does the same as `fire` but waits: the calling task gives up control (yield) until the awaited one is done, then gets its return value,
  if there is one. The result is **handed over**, not copied (like `return new ...` goes to the caller). An exception in the awaited task is
  **thrown again at the `await`**, with its type; `catch threads` stays for `fire`.
* **Lending:** while a direct `await` waits, the caller is stopped and nobody else can reach its objects (no shared memory) - also on several cores.
  So the awaited call may use `this` and the arguments **without a copy**; a copy only on request (`taking`, `flat`). Objects of the global scope keep
  their lock. With several calls at once lending is only allowed when they share no objects; otherwise they get copies. Strings lent to several calls running at once are copied for all but one (their counts are plain, section 2).
* **Several at once:** `await [f(), g()]`, written like an array literal, starts all calls, waits for all and returns an array of the results in the
  order of the brackets. Its element type is the common type of the results (as for an array literal), `any` when they have nothing close in common.
  When several fail, all are waited for and the first exception in the order of the brackets is thrown.
  **What each call gets** is written with the call, inside the brackets, with the clauses of `fire` (`taking`, `handover`, `flat`, `copy`):
  `await [Load(a) taking a, Merge(x, y) handover y, taking list { return list.Sum() }]`. Lending applies per call (section above): what a call names
  in no clause and shares with none of the others is lent; what two calls share is copied for all but one, unless a clause says otherwise.
* **Pooled is the default.** A task runs on a shared worker; a real thread of its own with **`dedicated`** (`fire dedicated { }`,
  `await dedicated Work()`) for work that blocks in natives for a long time.
* **Actors get their way back:** a method call on an actor already posts a message; with `await` it returns the result:
  `var count = await counter.Get()`. The home of an actor processes its mailbox as a task in the pool. This closes the open point of the single
  instances in docs/ROADMAP_HTTP.md.

## 4. The scheduler in C

All of it is a small part of the C runtime: native programs use it directly, the VM through the package ABI. Lean and fast.

* **A task is a coroutine with a stack of its own** (stackful): `Yield` and `Sleep` work anywhere, deep in calls - no "async" functions that spread
  through the code; every function can be awaited. Stacks are reserved and committed on use, with a guard page; small fixed stacks on the ESP32.
* **Context switch:** a few lines of assembler per architecture (x64, arm64, xtensa, riscv), fibers on Windows; wasm cannot switch stacks - there a
  task falls back to the behaviour of a thread where threads exist.
* **Workers:** one work-stealing deque each; a timer heap for the sleepers.
* **`Yield`** gives up control for an unknown time; the task goes to the end of the ready queue.
  **`Sleep(t)`** gives it up too and notes the wish to come back at `t`; from then on its urgency is highest: at every switch the overdue sleepers come
  first (by due time), then the ready queue.
* **Switch points** are today's safe points: where the GIL is handed on now, the scheduler checks whether the time slice is over or a stop is wanted
  (quasi preemptive, an endless loop does not block a worker). Waiting in the net package (it sleeps 1 to 10 ms between polls in fire code) becomes a
  switch by itself; later a poller (epoll, IOCP, kqueue) wakes tasks when a socket is ready.
* **The pool grows with the load:** at least one worker, normally up to the number of cores. A watcher starts another worker when one hangs in a
  blocking native while work is waiting (natives report enter/leave of a blocking call through the wrapper); idle workers end after a while.
* **State per task, not per thread:** a task can move to another worker, so its state must not be in `thread_local` (a compiler may keep the address
  of a TLS variable across a switch). `FIRE_TLS_STRUCT` (made for FreeRTOS) becomes the general way: the state is a struct on the task, the worker
  only holds "current task".
* **VM:** the CLR cannot switch stacks. The VM uses the queues, timers and pool rules of the C side but suspends a task itself by keeping its frames
  on the heap. Where a native calls back into fire (`call_lambda`) the task cannot be suspended: it counts as blocking and the pool adds a worker.

## 5. The UI loop

* The UI loop runs on **a pooled task of its own that is pinned** to the worker that is the main thread of the process (Win32 delivers the messages of a
  window to the thread that made it; AppKit must run on the main thread). Pinned is not exclusive: while the UI task waits for events, its worker runs
  other tasks.
* **The idle wait of this worker is the system's event wait** together with the wake signal of the scheduler: `MsgWaitForMultipleObjects` (Win32),
  `poll` on the display fd plus an eventfd/pipe (X11, Wayland), the run loop with a source of its own (macOS), the browser's event loop (web). The
  display backends already have such waits (the "wake" of the HAL).
* **Event handlers run as tasks of their own**, so an `await` in a handler stops only that handler and the window stays responsive.
* The UI task and its handlers form a **task group**: it runs only on the UI worker and switches between its members only at `await`, `Yield` and
  `Sleep` - not at the time slice points, so a handler is undisturbed between two awaits (the rule known from WPF and the browser). Tasks outside the
  group may take the worker at any safe point; they cannot see the UI objects.
* Other tasks do not touch the UI directly. A handler fetches results itself (`var data = await Load(...)`, then sets them). For code from outside
  (progress, a push from the server) a window has a **dispatcher**: it is the mailbox of its UI group, and a reference to a window may go to other tasks
  like an actor reference (`with window`). The language form:

  ```
  fire on window { window.status.Text = text }       // runs in the UI group, the caller goes on
  var name = await on window { return window.nameBox.Text }   // runs there and returns the result (the way back)
  ```

  The block is compiled like the body of a `fire`: what it takes from the caller is copied (`taking`, numbers and strings by default); inside the block
  `window` is the real object. `on` names *where* the code runs (a window, later any pinned group or an actor); `pinned` stays the word of the
  scheduler for "this task does not move". A library form `window.Dispatcher.Post(...)` is not possible as a plain lambda, because lambdas cannot go to
  another thread.
* The window thread of the windows package that presents the frames (GPU blit, DirectComposition) stays separate from drawing: a pinned task or a
  thread of its own, since presenting may block (vsync).
* **The main program is the UI task:** it always starts as a pinned task on the main thread, and `Run()` makes it the UI group - the most compatible
  way, nothing changes for existing programs.

## 6. Cancellation

**Cancellation tokens**, as in .NET. In C a token is a small object with an atomic flag, its parent and a list of waiters: `Sleep`, `await` and the
waits of `net` wake **at once** when it is cancelled, nobody polls the flag. In code `token.ThrowIfCancelled()` checks by hand; `CurrentToken` is the
token of the running task.

* **Every task has a token.** An awaited call gets a **new token linked to the caller's**: cancelling the caller cancels the child (and everything below
  it), cancelling the child's own token does not reach the caller. So a task can end its children without ending itself or its caller.
* **A source is born linked:** a `new CancellationSource()` made inside a task is implicitly a child of the task's token (`CurrentToken`). A task that makes
  its own source for its own tasks therefore stays reachable from above: cancelling the caller cancels the task, its source and everything started with
  `until source.Token` - also `fire` tasks. Cancelling the source reaches only what hangs below it. A source that must not follow its creator is made
  explicitly: `CancellationSource.Detached()` (e.g. for work that is meant to outlive the task).
* In C the tokens are the nodes of a tree with reference counts, apart from the fire objects: the end of a `CancellationSource` (its owner's scope) only
  lets go of its node; tokens handed to tasks keep it alive and keep the link to the parent. A node that is cancelled cancels its subtree at once.
* **`await` inherits** (the callee is a child the caller waits for). **`fire` does not inherit** - a fire and forget task lives on independently - but it can
  be cancelled like any task with `until`.
* **`until`** - a keyword like the other clauses (`taking`, `with`, `handover`) - gives a task its token: the token of the task is then linked to the
  given one (with `await` to the caller's token as well). A time value is a time limit (a token that is cancelled when the time runs out):

  ```
  var source = new CancellationSource()
  fire until source.Token { Watch() }                  // cancellable fire and forget
  fire Poll(url) until source.Token
  var data = await Download(url) until 5s              // time limit: TimeoutException
  var r = await [Load("a"), Load("b")] until source.Token, 10s
  await until source.Token { ... }
  source.Cancel()
  ```

* **`TimeoutException`** (a subclass of `CancelledException`) is what a time limit of `until` throws: the reason is visible, and one
  `catch (CancelledException e)` still catches both. The own time limits of the net package (`ReadTimeout`, `Accept(timeout)`) keep throwing
  `Net.TimeoutException` (a `Net.NetException`); a limit given with `until` around net calls throws `TimeoutException`.
* What a cancelled task sees: the next wait (`Sleep`, `await`, net) or `ThrowIfCancelled()` throws a `CancelledException`; it runs through `finally`
  and destructors as usual. An `await` of a cancelled task throws it at the caller. Code that never waits and never checks is not stopped (no hard abort).

## Steps

1. Remove the GIL from the C runtime: lock for the globals, biased counting, slot table, maps, a lock per package. Thread tests on several cores,
   ThreadSanitizer.
2. State per task instead of `thread_local` (`FIRE_TLS_STRUCT` everywhere).
3. Scheduler: coroutines, workers with work stealing, timer heap, `Yield`/`Sleep`, a pool that grows and shrinks, blocking natives.
4. `await` in the language (lending, result, exceptions, several calls at once) and the way back of actors.
5. Pinned tasks, task groups and the UI loop on the scheduler (event waits of the display backends).
6. Cancellation tokens and `until`.
7. The VM on the scheduler.

## Open

Nothing at the moment - the design questions above are decided.
