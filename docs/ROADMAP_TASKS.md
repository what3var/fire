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
| **reference counts** of strings (`rc++`, not atomic) | strings cross threads (globals, `taking`, results) | **biased counting**: plain counts in the owning thread; a string that crosses a thread boundary gets a "shared" bit and counts atomically from then on; literals stay immortal |
| slot table of generations (`g_slots`) | `realloc` while others read, one free list | segments that never move, free slots cached per thread, generations read and written atomically |
| global maps: probes, mailboxes, `originOf`/`copiesOf`, run-time units, host services, thread list | changed at the same time | one small mutex each (rarely used); probes may move to the object later |
| natives of packages (today under the library's lock) | not written for parallel calls | one lock per package by default; a package that declares itself thread safe in the ABI runs without it |
| `terminate`, the debugger | need every thread to stop | through the existing safe points (`g_attn`) |

Check: the thread tests of the suite on several cores, plus a run under ThreadSanitizer.

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
  their lock. With several calls at once lending is only allowed when they share no objects; otherwise they get copies.
* **Several at once:** `await [f(), g()]`, written like an array literal, starts all calls, waits for all and returns an array of the results in the
  order of the brackets. Its element type is the common type of the results (as for an array literal), `any` when they have nothing close in common.
  When several fail, all are waited for and the first exception in the order of the brackets is thrown.
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

