# Tasks, `await` and multicore - roadmap

Status: **steps 1-7 built** (see the details of each step below). fire was meant to be a nicer counterpart to async/await; it had `fire`
(a thread, no result) but no way to wait for a result. This plan adds `await`, a scheduler with a pool of workers in the C runtime, and removes the global
lock so that fire code runs on all cores.
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

### Step 3 in detail - the scheduler

*Done so far* (`FIRE_TASKS`: without the GIL, with the task state, where the platform has coroutines - posix, windows; `FIRE_NO_TASKS` keeps a system
thread per fire thread):

* **3a coroutines** (`platform/std/fire_coro.h`): the switch in assembler on x86-64 (System V; 15 ns a switch), fibers on Windows, ucontext elsewhere
  (arm64 and riscv in assembler are left for later); stacks reserved with a guard page below them (8 MB like a system thread, committed on use), a cache of
  64 stacks of ended tasks for the next ones; every switch is told to AddressSanitizer and ThreadSanitizer. A coroutine stopped on one worker goes on on
  another.
* **3b/3c the scheduler** (`fire_rt.h`, before `notifyEvent`): `fire` starts a task (`taskSpawn`). Workers - system threads, one per core to begin with -
  take the ready tasks from a queue; a task switches back to its worker to give its turn away, and the worker then requeues, parks or frees it (a task is
  never in a queue while its stack is in use; a wake while it is parking is put right by the worker, `TS_WOKEN`). `blockUntil` parks a task instead of
  blocking its worker: until an event, its time (the heap of the sleepers - `Sleep`), or one thing it waits for (`notifyKey`: a section granted or done, a
  message for an actor - waking every waiting task at every grant was quadratic). The safe points give the turn away at the end of a time slice when tasks
  wait for a worker or a sleeper is due. A native that waits (`gilRelease`) counts its worker as blocked: while tasks wait, another worker starts; an idle
  worker ends after 10 s (never the last). The task pointer (`fire_curTask`) is read through a function that is never inlined - ThreadSanitizer showed a
  compiler keeping the address of the thread-local slot across a switch, which is the old worker's after a move.
* Measured (4 cores): 20000 `fire` that do nothing: 82 ms with tasks, 531 ms with system threads; 10000 `fire` with a section each: 567 ms against 855 ms;
  four strands computing (objects, methods, strings): the same (about 230 ms). A program without `fire` is not touched.
* Fixed on the way: `blockUntil` read the count of events after looking at its condition - an event between the two was lost (the waits of the threads
  hid it by looking again every 5 ms).

* **3d work stealing:** every worker has a queue of its own (under a small lock of its own): a task that gives its turn away, a task started or woken by
  a task go there, without the shared lock; a worker takes from its own queue first, then from the shared one (the main program's and the timers' tasks),
  then it steals the oldest task of another worker. 32 tasks that give their turn away 20000 times each (`Sleep(0)`): 2.26 s with one shared queue,
  0.46 s with the queues of the workers; the other measurements are the same within the noise.
* The thread cases of the suite (287 checks with ThreadSanitizer) pass with tasks.

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

### Step 4 in detail - `await`

* **4a done:** `await Call(args)` and `await [taking x | handover x] { ... return v }` (the parser: `await` is a word only in front of what it awaits,
  like `trust`; the editor knows it). A single `await` runs the awaited code **on the caller's strand**: the caller waits anyway, so this is exactly
  lending - no copy, no switch; the result and an exception are those of a call. While the awaited code waits, the caller's task is parked and its worker
  runs others (step 3). Clauses after the call copy (`taking x` -> `copy x`) or move (`handover x` -> `take x`) the argument or receiver they name; the
  block form is a function of its own that sees only what it takes and the globals, called at once. Fixed on the way: `f(copy x)` on a lambda did not copy
  in a native build (the prefix CopyArgs was only read by calls of methods).
* **4b done:** `await [ ... ]`. The parser makes every element (a call with its clauses, or a block) a lambda that sees the caller's variables (lent)
  and keeps its result or exception: `(__res, __fails, __i) => { try { __res[__i] = call } catch (Exception __e) { __fails[__i] = __e } }`; the
  prelude's `__Await.All` runs them with the native `__await_run` and throws the first exception in the order of the brackets, or gives the results
  (the results belong to the array: the first slot claims them, SPEC 2.1). The VM runs them one after the other. A native build (`awaitRun`) runs them as
  tasks side by side - the last one on the caller's strand - when no two of them reach the same object (else one after the other: lending to two at once
  is not safe); the strings they reach count atomically from then on; every task has its own arrays of one element for its result and exception and a
  global scope of its own, which the caller takes over when all are done. The main program's globals are not lent (the calls read them directly): the
  global scope becomes shared as at the first `fire` - the tasks read it under the lock of the globals and change it in sections that the waiting caller
  grants; so calls that change the same global object run safely, in an order that is not fixed (as threads). A program with `await [ ... ]` uses the scheduler of tasks. Four calls that
  sleep 100 ms: about 100 ms natively; ThreadSanitizer and AddressSanitizer clean. The element type of the result is `any` for now (the common type of
  the results, as for an array literal, is left for later).
* **4c done:** `await actor.Method()`. The parser marks a method call after `await` (CallExpr.Awaited); the compiler puts the prefix
  `OpCode.AwaitReply` in front of it. On an actor the message then gets a way back (VM: ActorReply with an event; native: `g_awaitReplyTo`, which the
  actor's dispatcher compares with its receiver - with the generation, so a stale value never hits a later object - and `actorAwait`). The home of the
  actor runs the message at its `process` under a catch of its own: an exception that nothing in the method caught goes back and is thrown at the `await`,
  not to the handlers around `process`. The result goes over like a handover when the call made it, as a deep copy when the actor keeps it (a field, a
  global); the strings in it are copied for the other thread. The caller waits with its section given back (the home may be the main program, which
  waits for sections) - a task is parked, keyed to its reply; the main program serves its queue meanwhile. On the actor's home `await` runs the method
  at once, like a call. `terminate` ends the wait; an actor that dies with waiting messages answers them with undefined. Fixed on the way: the waits of
  `await [ ... ]` (4b) were not keyed - a task looked again only every 5 ms; keyed, a round trip of `await actor.M()` takes about 30 us natively
  (2000 in 62 ms; before the fix 5 ms each). ThreadSanitizer and AddressSanitizer clean with four threads awaiting one actor.
* **4d done:** `dedicated`. `fire dedicated { }` (and the call form) sets bit 1 of the `with` byte of OpCode.Fire; a native build starts a system
  thread of its own for it (`fireThreadDedicated`) instead of a task. `await dedicated X` (a call, a block, an element of `await [ ... ]`) puts
  OpCode.Dedicate/Undedicate around the call; natively the running task switches out with TA_DEDICATE, its worker starts a system thread that switches
  into the task's coroutine (a FireWorker with `dedicated`: taskRunning is false there, so the task waits, sleeps and blocks like a thread and never
  parks), and Undedicate switches out with TA_REQUEUE - that thread puts the task back into the pool and ends. It is the same strand, so lending stays as
  it is. An exception that leaves the call skips Undedicate: throwValueR, when the catch it found was there before the `await dedicated`
  (g_dedHandlers), takes the task back first. Nested: counted (an exception out of an inner one that the outer one catches keeps the task on its thread
  until the outer one ends). The VM ignores both (its threads are system threads). Twenty tasks in `await dedicated` sleeping at once: 25 system
  threads (20 of their own and the pool), 5 without `dedicated`; ThreadSanitizer and AddressSanitizer clean.
* Fixed later (with the capture of locals): a lambda inside a `fire` body could not use the variables of the body's top level (they are in the thread's
  own global scope - natively variables of the body's function); the lambda captures them now like locals, by value (Resolver.DefineCaptures).

### Taking at the use: `taking x` / `handover x` in a block or a lambda

* **Done:** in the body of a `fire` block or a lambda, `taking x` / `handover x` is an operand: the parser notes the variable with its way (a body inside
  a body passes its words on: the `fire` block around a lambda needs the variable first) and the block captures it like the clause (`fire taking x`,
  copies first, the moved ones last; `handover` resets the caller's variable as before); the lambda's capture is loaded as `copy x` / `take x` when it is
  made (ResolvedRef.LambdaCaptures.Loads). A local of the code around a `fire` block that the body uses without a word (Resolver: the names it uses that
  are locals there and that the body does not declare itself) is captured as a copy marked Implicit - `TC023` of the type checker (lax a warning, strict
  an error) - only when the copy costs memory (an object, an array, a string, an unknown value; a number is copied silently). A lambda without words
  captures as before: the instance is shared, nothing is copied, no warning. Tests: native == VM (copy, move, implicit copy, lambdas, a lambda and
  `await [ ... ]` in a `fire` body), TC023 lax/strict.
* **`handover x` in a lambda - done as far as the definition goes:** the instance (and what it reaches) moves into the lambda like a handover to a
  thread: the caller's variable is `undefined` afterwards and every other reference from before is dead (the next generation of the moved parts -
  VM: ObjectHandover.MoveAll, native: handOverAll via `__lambda_handover`; natively this needs the thread runtime).
  **Open (to be decided):** who owns the moved instance while the lambda holds it, and when it is destroyed. For now it belongs to the global scope of
  the program (it lives until the program ends) - a lambda owns nothing in either backend yet (natively a lambda is counted and could destroy what it
  owns at its end; the VM's lambdas are not counted). Also open: what a lambda that is called after its instance was destroyed or taken out sees.

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

### Step 5 in detail - pinned, the group, the UI loop

* **5a done - the main program is pinned, not alone:** it is no coroutine (its stack is the thread's, it never moves), but while it waits (blockUntil:
  `Sleep`, `process`, `await`, a section, the end of the program) its thread is a worker of the pool (`g_homeWorker`, `homeHelp`): it runs one task that
  waits for a worker, then looks again at what it waits for. A task that borrows the thread yields at its next safe point once an event came
  (`fire_homeWanted`: the count of the events `g_evTick` against what the main program had seen); one that enters a blocking native there
  (`gilRelease`) goes to a thread of its own for the wait (like `dedicated`) and back to the pool after it, so the main program never waits for it.
  Found on the way: a task of `await [ ... ]` read its group after counting it down - the caller may have freed it by then (ThreadSanitizer, now that the
  main program finishes such waits sooner).
* **5b done - the wait of the UI loop is a wait of the main program:** the pacing of the frames (VSync of X11, Win32, Cocoa) and the wait for a
  window manager's cycle go through `fire_waitHelping` / `fire_frameWait` (`FIRE_RT_HELPING_WAIT`; the display backends are built without the runtime
  too): on the main program with tasks the thread runs tasks meanwhile, else the old sleep.
* **5b+ done - the waits of the main program serve its windows:** every wait of the main program (`Sleep`, `await`, a section, a reply, the end of the
  program; with and without threads) reads at each turn what the system has for the windows it pumps itself (`fire_setIdleHooks`, registered by the
  windows package when the first window opens on the main program's thread): the events go into the window's inbox for its next `Pump`/`Tick` (no fire
  code runs in the wait), but the window answers the system at once - it repaints after an Expose, it answers pings, Windows does not call it "not
  responding". The wait itself is the system's together with the scheduler's wake signal (`notifyEvent` & co. call `fire_idleWake`): `poll` on the X
  server's connection and a pipe (posix), `MsgWaitForMultipleObjects` with an event (Win32); elsewhere (Wayland, Cocoa, DOS) the runtime's own wait, and
  the pump at each of its turns. Not in a wait inside the display (the pacing of a frame: the next Tick pumps anyway), never a window of another thread
  (a token of the thread that opened it). The VM: the same through `fire_pkg_idle` of the C library at each turn of its waits (`VmGroup.WaitTurn`,
  `TaskHooks.IdlePump`; the C# window manager does it for its own windows). Test: while the program sleeps, its window is unmapped and mapped again
  under Xvfb - it shows its frame again at once (native and VM; before: black until the next Tick).
* **5c done - event handlers are members of the main program's group:** a callback of a window (`fire_winb_dispatch` -> `fire_groupCall`) is a task with
  the main program's role (its globals, the writer of the shared domain), pinned to its thread (a queue of its own that no other worker takes; never a
  time slice there, never `dedicated`, never handed off). It starts at once and runs until it ends or waits; then the window goes on, and the handler
  goes on at the next wait of the main program or at the next `Tick` (`fire_groupRun`, the switch point of the UI loop). Only one member runs at a time
  (one thread), so they share the main program's objects like one thread; the end of the program waits for them. A program that registers callbacks of
  windows uses the scheduler (posix, windows). A member that runs a loop of its own (a modal window) calls the callbacks of that loop itself. `Pump`
  is a switch point too, like `Tick`.
* **5c in the VM too:** a window's callback from the host (`RuntimeSession.CallLambda` -> `FireRuntime.RunCallback(asTask)`) is a task of the group
  of the VM's main program (`VmGroup`): a VM of its own that shares the main program's globals and owner broker (`MakeGroupMember`), pinned to the
  thread of the main program and without time slices. It runs until it ends or waits (`Sleep`, `await`, a section, a reply); then the window goes on,
  and it goes on at the switch points (`Tick`/`Pump` of the window bridge, the waits of the main program: `Sleep`, waiting for a reply, holds of
  the host, the end of the program). Not a member: a callback of a fire thread's VM or of a member itself (a nested loop calls it directly).
* **5d done - `fire on window { }` and `await on window { return ... }`:** a job of the main program (like `fire global`, OpCode.PostGlobal with bit 7:
  the first argument is the object itself, not a copy; bit 6: the result comes back) - it runs in the main program's group at its next safe point; from a
  thread the object must be one of the main program (its globals) or an actor, else ArgumentException. `await on` from a thread waits for the result
  (the way back of 4c: what the block made goes over, anything else is copied; an exception is thrown at the `await`); the main program itself runs the
  block at once. The block's parameters get the types of the arguments, so `await on` has the type of the block's result. `on` is the keyword of bound
  lambdas; there is no `on` for actors yet (an actor has its methods).
* Tests: a handler that sleeps while the loop goes on (native, headless and Wayland; VM: the window manager's `HandlerTasks`), `fire on` / `await on` (native == VM); ThreadSanitizer and
  AddressSanitizer clean.

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

### Step 6 in detail - cancellation

* **Done:** the prelude has `CancellationSource` (`Cancel()`, `CancelAfter(t)`, `Token`, `IsCancelled`, `CancellationSource.Detached()`),
  `CancellationToken` (`IsCancelled`, `ThrowIfCancelled()`, `CancellationToken.Current()` - the running task's) and `CancelledException` /
  `TimeoutException`. Both classes are `[NoCopy]` (a token goes to a task with `until`, never as a copy): they keep an int handle of a node of the
  runtime (natives `__cancel_*`; the VM: fire.Runtime.Cancellation, a table of handles; native: fire_rt.h, the node's address) and let go of it in
  their destructor. A node is cancelled itself, by its time limit or by one of its parents (it knows its parents, a check walks up); `Cancel` wakes
  every wait (notifyEventAll / the VM's wake), and each looks at its token again.
* **`until`** is a clause like `taking`: `await Call() until token, 5s`, `await [ ... ] until token` (an element of the brackets takes one limit of its
  own: `await [Load(a) until 2s, Load(b)]`), `await until token { ... }`, `fire until token { }`, `fire Poll(url) until token`. Each limit becomes
  `__Cancel.Limit(x)` (a token, a source or a time value as a handle); `await ... until` is UntilExpr: OpCode.CancelPush (a node that follows the
  caller's token and the limits - a frame of the task) around the call and OpCode.CancelPop after it. An exception that leaves the call skips the pop:
  the catch that takes it outside leaves the frame (VM: the number of handlers when the frame began; native: throwValueR, like `dedicated`). `fire ...
  until` sets bit 7: the node is the token of the next thread (a thread never follows its caller). The tasks of `await [ ... ]` follow the caller's
  token; a source made inside a task follows the task's token (`new CancellationSource()`), a detached one nobody.
* **What a cancelled task sees:** `Sleep` waits no longer than the time limit of its token and throws `CancelledException` (`TimeoutException` for a time
  limit) - at once when the token is cancelled while it sleeps; the waits of `await actor.M()` and `await on` stop waiting the same way (the reply is
  given up: who finishes last - the caller or the home - frees it). The net package waits with `Sleep` between its polls, so its waits end as well.
  `ThrowIfCancelled()` checks by hand. Code that never waits is not stopped. `process` does not look at the token yet.
* Tests (native == VM): time limit, done before the limit, `fire until` woken at once, `ThrowIfCancelled`, a catch inside the frame, `await [ ... ] until`,
  a source that follows its task, `CancelAfter`, `await actor.M() until`, `Detached`; ThreadSanitizer and AddressSanitizer clean (the given-up replies
  were a leak before the claim of who frees them).

### Step 7 in detail - the VM on the scheduler

* **Done (fire.Runtime/VmScheduler.cs, VM.Tasks.cs):** a fire thread of the VM is a task of a pool of system threads (as many as the machine has cores,
  more while some are blocked), not a system thread of its own. The CLR cannot switch stacks, but the VM keeps its frames on the heap: a wait of a task
  on its own level - not inside a callback, a getter or a section - leaves the VM's loop: `Suspend` swaps the running chunk for one that only halts
  (nothing is checked in the hot loop: the halt of that chunk is the way out) and notes what the task waits for; the worker takes the next task, and the
  task goes on - on any worker - when its condition holds or its time comes (a queue of times; the conditions are looked at again after a wake: a reply,
  a cancel, a section granted, a message, a signal). The next slice puts the chunk back and runs the resume of the wait, which looks at its condition again
  (a signal may wake it early) - so `Sleep` sleeps the rest of its time, a section waits until it is granted.
* Waits that leave the loop: `Sleep` (and its time limit and cancel of step 6), `await actor.M()` / `await on` (the reply), `sync global { }` (the
  section is asked for, the task waits until the main program grants it), `process` with an empty mailbox. Any other wait - inside a callback or a getter,
  a section asked for in the middle of an instruction (a write to a global), a native that waits - blocks its worker and counts as blocked: the pool starts
  another worker for the tasks that are ready.
* The main program stays on its own thread (pinned, like the native build); the debugger's thread hook (ThreadBodyInterceptor) keeps a system thread per
  fire thread, and FIRE_VM_THREADS=1 does so for every program.
* Found on the way: a task that waited again before its old entry of the waiting list was gone stood in it twice and ran on two workers at once - every
  wait has a number now, an entry of an earlier one is skipped.
* Measured: 2000 fire threads that sleep 50 ms and then write in a section: 1.3 s on 4 workers (1.5 s with 2000 system threads); actors with `process`
  in threads that start threads as fast as before. Tests (native == VM): 600 threads that sleep and write in sections, `process` in threads.

### Step 8 - captures only with a word, globals by `taking`, `on` runs on its target (next; decided by the user)

The rules that make a lambda the same kind of thing as a `fire` body - everything it holds is its own, or it runs where its target lives:

* **Captures only with `taking` / `handover`.** A local that a lambda uses without a word is copied like `taking` (lax: with the warning `TC023`,
  strict: an error) - no longer shared. This replaces "a lambda without words captures as before: the instance is shared" (section 3 above,
  docs/language/12-lambdas.md "Objects are copied as references", 15-threads-and-actors.md).
* **Globals are captured the same way, but only by `taking`:** `(taking counter)` copies the global into the lambda when it is made; `handover` of a
  global is a compile error (a global belongs to the program, it cannot move into a lambda).
* **`on` runs the lambda on its target, always:** `func () on obj => ...` called from anywhere runs on the home of `obj` (the main program's group for
  its objects and windows, the actor for an actor), like `await on obj { ... }`; the caller waits for the result and meanwhile yields/sleeps (its own
  queue, its group and its windows are served while it waits - no deadlock when the home waits for the caller). Called on the home itself: directly.
* Tests: VM == native for each rule (implicit copy with TC023 lax/strict, `taking` of a global, `handover` of a global as an error, an `on` lambda called
  from a thread and from an actor runs on the main program / the actor, the result and an exception come back, the caller's group goes on meanwhile).

**Decided (the user):**

1. **Every implicit copy is said** - a number too: `TC023` (lax a warning, strict an error) for each variable that a lambda or a `fire` body uses
   without a word. The point is no longer the memory: the user must know that he may be working with a copy without knowing it.
2. **Globals are copied implicitly too** (in a lambda: a global without a word is a copy like a local). **Copies can be changed:** a captured copy
   (of a local or a global) is a variable of the lambda - assigning to it is allowed (today an error) and changes only the copy. That is why the
   warning matters.
3. **Any object can be the target of `on`.** Its home is the thread that owns it (the main program, a fire thread, an actor). A call of the lambda
   from elsewhere is asynchronous on that thread; the caller waits and yields meanwhile. Inside, calls on the target (`this`) run normally - the body
   runs where the object lives. (A fire thread then serves such calls at its safe points and waits, like an actor its mailbox.)
4. **Lambdas may go to other threads** (an argument of `fire`, a field of what a thread takes): the lambda goes with its captures (copied or moved
   like the rest of what the thread gets); a bound lambda keeps its target, and its calls go to the target's home.
5. **What `handover` moves into a lambda belongs to the lambda's scope** and its thread (or the thread of its `on` target): it dies when that scope
   ends, and can be passed on with `take` before.

6. **A `take` into an implicit copy is said too:** when a `take` goes to a variable that was captured implicitly (with `TC023`) - assigned to it
   (`copy = take node`) or handed to it as the owner (`copy.Add(take node)`) - the type checker warns once more (a code of its own: ownership given
   to an implicit copy - it dies with the copy, not with the variable of the code around it).

7. **The captures belong to the scope that the lambda makes, not to the scope that declares it** (the user): a copy (`taking`, implicit) and what
   `handover` moves in are owned by the lambda's own scope and live and die with it.

**Found on the way:** natively a `taking` copy in a lambda dies after the first call (`var t = () => (taking b).v; t(); t()` - the second call throws
`DestroyedException`; the VM keeps it): the copy is owned by the temporaries of the statement that makes the lambda. Fixed by rule 7.

**Still to settle on the way:** whether a change of a captured copy lasts from one call to the next (the copy is the lambda's variable, as long as the
lambda lives - assumed) or each call starts from the copy made with the lambda; whether "the lambda's scope" in 5 is the lambda's life (assumed) or
one call.

## Steps

1. Remove the GIL from the C runtime: lock for the globals, biased counting, slot table, maps, a lock per package. Thread tests on several cores,
   ThreadSanitizer.
2. State per task instead of `thread_local` (`FIRE_TLS_STRUCT` everywhere).
3. Scheduler: coroutines, workers with work stealing, timer heap, `Yield`/`Sleep`, a pool that grows and shrinks, blocking natives.
4. `await` in the language (lending, result, exceptions, several calls at once) and the way back of actors.
5. Pinned tasks, task groups and the UI loop on the scheduler (event waits of the display backends).
6. Cancellation tokens and `until`.
7. The VM on the scheduler.
8. Captures only with a word, globals by `taking`, `on` runs on its target (see "Step 8" above) - next.

## Open

Step 8: the two points under "Still to settle on the way".
