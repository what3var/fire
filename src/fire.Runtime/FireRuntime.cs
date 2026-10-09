using System;
using System.Collections.Generic;
using System.Threading;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Handle to a running/finished fire thread. Since `fire`
    /// has no return values (docs/THREADING_DESIGN.md section 1),
    /// this handle is of no use in the language itself later (no
    /// join language construct) - here, in this development stage without
    /// language syntax, `Join`/`Error` is the tool for TESTS to wait for the
    /// end and to make errors visible, instead of otherwise letting them silently
    /// vanish in the thread.</summary>
    public sealed class FireThreadHandle
    {
        private readonly Thread _thread;

        /// <summary>An exception that broke through unhandled in the thread delegate -
        /// in the later language binding this corresponds exactly
        /// to the case "unhandled user exception in a fire thread"
        /// (docs/THREADING_DESIGN.md section 6.2), here still without the
        /// actual `catch threads(...)` delivery (which needs the
        /// language binding/cooperative check points, see the documentation there) -
        /// deliberately kept only as test visibility.</summary>
        public Exception? Error { get; private set; }

        internal FireThreadHandle(Thread thread) => _thread = thread;

        public void Join() => _thread.Join();

        internal void SetError(Exception ex) => Error = ex;
    }

    /// <summary>
    /// Spawns a REAL thread for `fire` (docs/THREADING_DESIGN.md
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
                lock (_liveThreadsGate)
                {
                    bool pending = broker != null && broker.HasPending;
                    if (_liveThreads == 0 && !pending) return;
                    if (!pending) Monitor.Wait(_liveThreadsGate, 50);
                }
            }
        }

        /// <summary>Wakes a main program that is waiting in <see cref="WaitForAllFireThreads"/> (a thread has put something into the queue).</summary>
        internal static void WakeWaitingOwner()
        {
            lock (_liveThreadsGate) Monitor.PulseAll(_liveThreadsGate);
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
                    handle!.SetError(ex);
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
        /// docs/THREADING_DESIGN.md section 7, GlobalsBroker):
        ///
        /// `broker`/`sharedGlobalCount`: the globals of the main program (slots 0..sharedGlobalCount-1) are NOT copied - the thread reads them
        /// directly and changes them in sections (see VM.AttachToGlobals). The thread's private global scope holds only placeholders for that, so that the
        /// slots of the captures behind them lie at the places that Resolving.Resolver.ResolveFireStmt/Compiler.CompileFireStmt assigned.
        ///
        /// `takingValues`: bound directly following the placeholders. An object (ValueKind.Class) is bound as an isolated deep copy
        /// (Runtime.ObjectCopier.Take - the copying itself activates thread sharing on the original), otherwise directly (Value is an immutable
        /// struct).
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
            VmExecutionMode executionMode = VmExecutionMode.Debug)
        {
            return Fire(() =>
            {
                // The slots 0..sharedGlobalCount-1 are the real globals of the main program (the VM reads/writes them via the broker,
                // see GlobalsBroker/VM.AttachToGlobals) - here only placeholders, so that taking/with lie at the slots assigned by the compiler.
                var scope = new Scope(null, isGlobal: true);
                for (int i = 0; i < sharedGlobalCount; i++) scope.DefineSlot(Value.MakeUndefined());
                foreach (var tv in takingValues) DefineSnapshotSlot(scope, tv);
                if (withValue is Value wv)
                    scope.DefineSlot(wv);
                var vm = new VM(chunk, scope, natives, classes, isFireThreadVm: true, executionMode: executionMode);
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
            var scope = new Scope(null, isGlobal: true);
            foreach (var v in globalSnapshot) DefineSnapshotSlot(scope, v);

            var vm = new VM(callback.Proto.Chunk, scope, natives, classes, isFireThreadVm: true, executionMode: executionMode);
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
        ///   (`sync globals` or - without `#nosync` - automatically, see <see cref="VM.PostCallback"/>). If the main program is no longer running
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
            VM? owner = null)
        {
            var vm = VM.CurrentThreadVm;
            if (vm != null && !vm.IsHalted)
            {
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
