using System;
using System.Collections.Generic;
using System.Threading;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>A host callback that cannot run on its isolated copy of the globals (FireRuntime.CallCallback): the copy is refused before anything is copied
    /// (a `[NoCopy]` instance, a lambda or a pointer in a field). The message is the text the host gets, like that of an unhandled exception of the callback.</summary>
    public sealed class CallbackRefusedException : Exception
    {
        public CallbackRefusedException(string message) : base(message) { }
    }

    /// <summary>Handle to a running/finished fire thread. Since `fire`
    /// has no return values (docs/spec/THREADING_DESIGN.md section 1),
    /// this handle is of no use in the language itself later (no
    /// join language construct) - here, in this development stage without
    /// language syntax, `Join`/`Error` is the tool for TESTS to wait for the
    /// end and to make errors visible, instead of otherwise letting them silently
    /// vanish in the thread.</summary>
    public sealed class FireThreadHandle
    {
        private readonly Thread? _thread;
        private readonly ManualResetEventSlim _done = new(false);   // (a task of the pool: VmScheduler)

        /// <summary>An exception that broke through unhandled in the thread delegate -
        /// in the later language binding this corresponds exactly
        /// to the case "unhandled user exception in a fire thread"
        /// (docs/spec/THREADING_DESIGN.md section 6.2), here still without the
        /// actual `catch threads(...)` delivery (which needs the
        /// language binding/cooperative check points, see the documentation there) -
        /// deliberately kept only as test visibility.</summary>
        public Exception? Error { get; private set; }

        internal FireThreadHandle(Thread? thread) => _thread = thread;

        public void Join() { if (_thread != null) _thread.Join(); else _done.Wait(); }

        internal void Finish() => _done.Set();

        internal void SetError(Exception ex) => Error = ex;
    }

    /// <summary>
    /// Spawns a REAL thread for `fire` (docs/spec/THREADING_DESIGN.md
    /// section 1). Immutable, already compiled data (chunk,
    /// RuntimeClass definitions, NativeRegistry, ...) can safely be shared between
    /// any number of VM instances/threads (it is never changed after
    /// compiling) - everything mutable at runtime
    /// (frames, current scope, IP) is given to each thread via its OWN
    /// VM instance (see the VM constructor, which for exactly this already takes all necessary
    /// parts as parameters instead of as a singleton/static state).
    ///
    /// Currently takes a pure C# `Action` as the thread body (instead of
    /// e.g. a FunctionProto + arguments) - that is a deliberate
    /// development-stage decision: this class is intended for the direct test of the
    /// architecture via the C# API (see Program.cs tests), the
    /// actual `fire { ... }` language syntax (parser/compiler/VM opcode)
    /// is not yet connected.
    /// </summary>
    public static class FireRuntime
    {
        /// <summary>Optional hook for tools outside the language
        /// itself (currently: the editor debugger, see fire.Editor.
        /// DebugThreadContext) - if set, it is called for EVERY newly created
        /// fire-thread VM instance INSTEAD of letting it run freely
        /// immediately. Receives the fully built VM instance as well as a
        /// delegate that means "run normally, unthrottled"
        /// (`vm.Run()`) - the hook MUST call this delegate at some point
        /// (directly, delayed, step by step via VM.StepInstruction
        /// etc. - entirely up to the hook), otherwise the thread stays
        /// forever without visible effect. If NO hook is registered
        /// (the normal case, e.g. in all Program.cs tests), everything behaves
        /// exactly as before (the delegate is called directly).
        ///
        /// Deliberately a simple, global (static) hook instead of e.g.
        /// an instance property on FireRuntime - FireRuntime does not know
        /// (and is not meant to know anything) about "editor"/"debugger"
        /// as a concept; that stays entirely on the calling side.</summary>
        public static Action<VM, Action>? ThreadBodyInterceptor { get; set; }

        private static void RunVm(VM vm)
        {
            var interceptor = ThreadBodyInterceptor;
            if (interceptor != null)
                interceptor(vm, vm.Run);
            else
                vm.Run();
        }

        // Living fire threads - the main program waits for them at its end before it releases its global scope
        // (see VM.FinishProgram): a thread that writes back into objects of the main program via `sync` must not
        // find them already destroyed.
        private static int _liveThreads;
        private static readonly object _liveThreadsGate = new();

        /// <summary>Blocks until all fire threads have ended (immediately if none is running). With `broker` (the main program, which owns the
        /// globals) it serves the threads' queue meanwhile - they are, after all, waiting for a `sync globals` -, otherwise the program end would
        /// hang on a thread that has just registered a section.</summary>
        public static void WaitForAllFireThreads(GlobalsBroker? broker = null)
        {
            while (true)
            {
                broker?.Drain();
                TaskHooks.IdlePump?.Invoke();   // (the windows of the main program read their events while it waits for the threads)
                lock (_liveThreadsGate)
                {
                    bool pending = broker != null && broker.HasPending;
                    if (_liveThreads == 0 && !pending) return;
                    if (!pending) Monitor.Wait(_liveThreadsGate, TaskHooks.IdlePump != null ? 20 : 50);
                }
            }
        }

        // Host holds (the open windows that a window manager's thread shows) belong to the program they keep alive: VM.AddHostHold of its main VM -
        // a program in the editor's live view does not hold up another one, and nothing outlives the program it was taken for.

        /// <summary>Wakes a main program that is waiting in <see cref="WaitForAllFireThreads"/> (a thread has put something into the queue).</summary>
        internal static void WakeWaitingOwner()
        {
            _ = VmGroup.Any;   // (the static constructor of the group sets the switch point of the UI loop)
            lock (_liveThreadsGate) Monitor.PulseAll(_liveThreadsGate);
            VmScheduler.Wake();   // (the tasks of the pool that wait look again)
        }

        /// <summary>Waits at most `milliseconds`, but wakes earlier if something is put into the main program's queue
        /// (see <see cref="WakeWaitingOwner"/>) - for `Sleep`.</summary>
        internal static void WaitForWake(int milliseconds)
        {
            lock (_liveThreadsGate) Monitor.Wait(_liveThreadsGate, milliseconds);
        }

        public static FireThreadHandle Fire(Action body)
        {
            FireThreadHandle? handle = null;
            lock (_liveThreadsGate) _liveThreads++;
            var thread = new Thread(() =>
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    // An error of the runtime itself (not a script exception - those go to `catch threads`): never silently - the program
                    // ends with the reason, as a native program does (`fire runtime error: ...`, exit code 1); otherwise the main program could wait
                    // forever for a thread that is gone.
                    handle!.SetError(ex);
                    Console.Out.Flush();
                    Console.Error.WriteLine("fire runtime error: " + ex.Message);
                    VM.RequestTerminate(Value.MakeInt(1));
                }
                finally
                {
                    lock (_liveThreadsGate)
                    {
                        _liveThreads--;
                        Monitor.PulseAll(_liveThreadsGate);
                    }
                }
            })
            {
                // Deliberately a foreground thread (the .NET default anyway,
                // only documented explicitly here): `fire` has no join,
                // but running work should not be silently aborted
                // on normal process end - this coincides with the
                // synchronised shutdown idea from THREADING_DESIGN.md
                // section 6.3 (first everyone cleans up, then the process ends).
                IsBackground = false,
            };
            handle = new FireThreadHandle(thread);
            thread.Start();
            return handle;
        }

        /// <summary>Like Fire(Action), but runs REAL, compiled bytecode
        /// on a FRESH, own VM instance - the actual
        /// foretaste of a later `fire { ... }`. `natives`/`classes`
        /// are immutable, already compiled data and are passed on
        /// directly (safely shared, see class comment); the
        /// global scope is created anew for THIS thread (no shared
        /// memory). Values from "outside" (e.g. a `taking` copy) are
        /// currently NOT yet bound via real language syntax, but via
        /// an ordinary native function registered in `natives`
        /// handed in (see Program.cs test) - that needs no
        /// parser/resolver change, because native functions already are
        /// the intended way for bytecode to talk to the "outside world"
        /// (see NativeRegistry class comment).</summary>
        public static FireThreadHandle FireVm(
            Chunk chunk,
            NativeRegistry natives,
            IReadOnlyDictionary<string, RuntimeClass>? classes = null,
            VmExecutionMode executionMode = VmExecutionMode.Debug)
        {
            return Fire(() =>
            {
                var scope = new Scope(null, isGlobal: true);
                var vm = new VM(chunk, scope, natives, classes, isFireThreadVm: true, executionMode: executionMode);
                RunVm(vm);
            });
        }

        /// <summary>Like FireVm, but with `taking`/`with` bindings and the connection to the globals of the main program (see
        /// docs/spec/THREADING_DESIGN.md section 7, GlobalsBroker):
        ///
        /// `broker`/`sharedGlobalCount`: the globals of the main program (slots 0..sharedGlobalCount-1) are NOT copied - the thread reads them
        /// directly and changes them in sections (see VM.AttachToGlobals). The thread's private global scope holds only placeholders for that, so that the
        /// slots of the captures behind them lie at the places that Resolving.Resolver.ResolveFireStmt/Compiler.CompileFireStmt assigned.
        ///
        /// `takingValues`: bound directly following the placeholders. An object, an array or a buffer is bound as an isolated deep copy of everything
        /// reachable from it (Runtime.ObjectCopier.TakeValue - the copying itself activates thread sharing on the original), otherwise directly (Value
        /// is an immutable struct). The copies are made HERE, on the spawning thread, before the new thread starts (a snapshot at the `fire`, as in the
        /// native runtime); their owner is the root scope of the new thread. The last `handoverCount` of them are `fire handover x`: not copied, the
        /// value itself (with everything reachable from it) moves to the root scope of the new thread (Runtime.ObjectHandover.MoveAll: the sender's other references die).
        ///
        /// `withValue` (an actor reference), by contrast, is ALWAYS passed on DIRECTLY, WITHOUT a copy, as the last slot - an actor manages its own
        /// thread safety via its mailbox (see Runtime.ActorMailbox), not via the taking/sync ownership model.</summary>
        public static FireThreadHandle FireVmTaking(
            Chunk chunk,
            NativeRegistry natives,
            IReadOnlyDictionary<string, RuntimeClass>? classes,
            GlobalsBroker? broker,
            int sharedGlobalCount,
            IReadOnlyList<Value> takingValues,
            Value? withValue = null,
            VmExecutionMode executionMode = VmExecutionMode.Debug,
            int handoverCount = 0,
            CancelNode? token = null)
        {
            // The slots 0..sharedGlobalCount-1 are the real globals of the main program (the VM reads/writes them via the broker,
            // see GlobalsBroker/VM.AttachToGlobals) - here only placeholders, so that taking/with lie at the slots assigned by the compiler.
            var scope = new Scope(null, isGlobal: true);
            for (int i = 0; i < sharedGlobalCount; i++) scope.DefineSlot(Value.MakeUndefined());
            int copied = takingValues.Count - handoverCount;
            var copies = new Value[copied];
            for (int i = 0; i < copied; i++) copies[i] = ObjectCopier.TakeValue(takingValues[i], scope);
            // (all `handover` values of the `fire` together: one may reach another; in performance mode nothing is checked, the parts keep their identity)
            var handed = new Value[handoverCount];
            for (int i = 0; i < handoverCount; i++) handed[i] = takingValues[copied + i];
            var moved = ObjectHandover.MoveAll(handed, scope, retire: executionMode != VmExecutionMode.Performance);
            foreach (var v in copies) scope.DefineSlot(v);
            foreach (var v in moved) scope.DefineSlot(v);
            if (withValue is Value wv)
                scope.DefineSlot(wv);
            if (VmScheduler.Enabled && ThreadBodyInterceptor == null)
            {
                // step 7: a task of the pool (VmScheduler) - not a system thread of its own
                var taskVm = new VM(chunk, scope, natives, classes, isFireThreadVm: true, executionMode: executionMode);
                taskVm.ThreadToken = token;
                if (broker != null) taskVm.AttachToGlobals(broker, sharedGlobalCount);
                var poolHandle = new FireThreadHandle(null);
                lock (_liveThreadsGate) _liveThreads++;
                VmScheduler.Start(taskVm, error =>
                {
                    try
                    {
                        taskVm.ReleaseGlobalsSections();
                        if (error != null)
                        {
                            poolHandle.SetError(error);
                            Console.Out.Flush();
                            Console.Error.WriteLine("fire runtime error: " + error.Message);
                            VM.RequestTerminate(Value.MakeInt(1));
                        }
                    }
                    finally
                    {
                        lock (_liveThreadsGate) { _liveThreads--; Monitor.PulseAll(_liveThreadsGate); }
                        poolHandle.Finish();
                    }
                });
                return poolHandle;
            }
            return Fire(() =>
            {
                var vm = new VM(chunk, scope, natives, classes, isFireThreadVm: true, executionMode: executionMode);
                vm.ThreadToken = token;
                if (broker != null) vm.AttachToGlobals(broker, sharedGlobalCount);
                try { RunVm(vm); }
                finally { vm.ReleaseGlobalsSections(); }
            });
        }

        /// <summary>Calls a registered callback lambda SYNCHRONOUSLY on the
        /// CALLING (native) thread, on a FRESH, own
        /// VM instance (VM.CallLambdaEntry) - unlike Fire/FireVm NO new thread is started
        /// HERE: the native host code has already determined
        /// the call time/thread itself (e.g. a
        /// .NET thread-pool thread with SerialPort.DataReceived), this
        /// method merely queues in.
        ///
        /// `globalSnapshot` MUST - as with FireVmTaking - have been read already BEFORE this
        /// call, SYNCHRONOUSLY on a "safe" thread
        /// (no lazy access to the live scope instance of the
        /// main program from here, a real data race otherwise) - the
        /// callback gets from it a NEW, own global scope
        /// (objects as an isolated deep copy, see DefineSnapshotSlot),
        /// NEVER the real global scope of the main program. This coincides
        /// exactly with SPEC 4.2: a lambda sees only its own
        /// scope plus the global one anyway, never surrounding locals - here "the
        /// global scope" is simply this snapshot instead of the original.
        ///
        /// Does NOT rethrow an unhandled script exception in the
        /// callback body itself (it would otherwise break through uncontrolled into
        /// foreign, native caller code) - instead it is
        /// reported to `onUnhandled` (if set) and `undefined`
        /// is returned.</summary>
        public static Value CallCallback(
            LambdaValue callback,
            Value[] args,
            NativeRegistry natives,
            IReadOnlyDictionary<string, RuntimeClass>? classes,
            IReadOnlyList<Value> globalSnapshot,
            Action<Exception>? onUnhandled = null,
            VmExecutionMode executionMode = VmExecutionMode.Debug)
        {
            // `[NoCopy]` (SPEC 5.12): the isolated copy copies every object of the globals (an array is shared as it is, like the arguments of
            // `fire global`) - an instance of such a class reachable from one is refused before anything is copied: the callback does not run, the host
            // gets the ArgumentException as text (like an unhandled exception of the callback). A lambda or a pointer in a field cannot be copied either.
            try
            {
                foreach (var v in globalSnapshot)
                    if (v.Kind == ValueKind.Class) ObjectCopier.RequireNoCopyFree(v, "host callback");
            }
            catch (InvalidOperationException ex)
            {
                onUnhandled?.Invoke(new CallbackRefusedException("Unhandled exception of type 'ArgumentException': " + ex.Message));
                return Value.MakeUndefined();
            }

            var scope = new Scope(null, isGlobal: true);
            VM vm;
            try
            {
                foreach (var v in globalSnapshot) DefineSnapshotSlot(scope, v);
                vm = new VM(callback.Proto.Chunk, scope, natives, classes, isFireThreadVm: true, executionMode: executionMode);
            }
            catch (TakingViolationException ex)
            {
                onUnhandled?.Invoke(new CallbackRefusedException("Unhandled exception of type 'ArgumentException': " + ex.Message));
                return Value.MakeUndefined();
            }
            try
            {
                return vm.CallLambdaEntry(callback, args);
            }
            catch (Exception ex)
            {
                onUnhandled?.Invoke(ex);
                return Value.MakeUndefined();
            }
        }

        /// <summary>Runs the lambda of a native callback (e.g. a window event) - the one place that decides WHERE:
        ///
        /// - On the thread of a running VM (the normal case: the script itself calls e.g. `Window.Tick`, and the
        ///   events fire in the process) it runs NESTED on this VM (<see cref="VM.CallLambdaInline"/>): with the real global variables,
        ///   reading and writing, without a copy. There is no concurrent execution, so nothing to isolate.
        /// - On a thread without a running VM (a host thread, e.g. a serial event) access to the globals would be a data race:
        ///   there it is queued to the owner VM (`owner`, the main program) and executed by it at a safe point
        ///   (`sync globals` or - as `#globaljobs` says - automatically, see <see cref="VM.PostCallback"/>). If the main program is no longer running
        ///   or there is no owner, it runs like <see cref="CallCallback"/> on an isolated copy (`snapshotGlobals` supplies it).
        ///
        /// An unhandled exception in the callback never goes to the caller, but as text to `onUnhandled`.</summary>
        public static void RunCallback(
            LambdaValue callback,
            Value[] args,
            NativeRegistry natives,
            IReadOnlyDictionary<string, RuntimeClass>? classes,
            Func<IReadOnlyList<Value>> snapshotGlobals,
            Action<string>? onUnhandled = null,
            VmExecutionMode executionMode = VmExecutionMode.Debug,
            VM? owner = null,
            bool asTask = false)
        {
            var vm = VM.CurrentThreadVm;
            if (vm != null && !vm.IsHalted)
            {
                // an event handler of a window: a member of the main program's group (VmGroup) - when it waits, the window goes on (as natively)
                if (asTask)
                {
                    VmGroup.RunReady();
                    if (VmGroup.TryStart(vm, callback, args, onUnhandled)) return;
                }
                try
                {
                    var error = vm.CallLambdaInline(callback, args);
                    if (error != null) onUnhandled?.Invoke(new UncaughtScriptException(error).Message);
                }
                catch (Exception ex)
                {
                    onUnhandled?.Invoke(ex.Message);
                }
                return;
            }

            if (owner != null && owner.PostCallback(callback, args, onUnhandled)) return;

            CallCallback(callback, args, natives, classes, snapshotGlobals(), ex => onUnhandled?.Invoke(ex.Message), executionMode);
        }

        /// <summary>Common binding logic for both the globals snapshot
        /// and taking captures (see FireVmTaking documentation) - for an
        /// object an isolated deep copy, otherwise the value directly.</summary>
        private static void DefineSnapshotSlot(Scope scope, Value v)
        {
            if (v.Kind == ValueKind.Class)
            {
                var source = (ObjectInstance)v.AsObjectRef();
                var copy = ObjectCopier.Take(source, scope);
                scope.DefineSlot(Value.MakeClassRef(copy));
            }
            else
            {
                scope.DefineSlot(v);
            }
        }
    }
}
