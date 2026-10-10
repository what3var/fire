using System;
using System.Collections.Generic;
using System.Linq;
using fire.Ast;
using fire.Bytecode;
using fire.Standard;
using fire.Runtime;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>An entry in the call stack: everything that has to be
    /// restored on RETURN in order to continue at the caller exactly there
    /// where the call took place. ConstructedInstance is only set for NewObject/
    /// NewObjectOwned: there RETURN should not push the (discarded)
    /// return value of the constructor, but the newly created instance.</summary>
    internal readonly struct CallFrame
    {
        public readonly Chunk ReturnChunk;
        public readonly int ReturnIp;
        public readonly Scope ReturnScope;
        public readonly object? ReturnThis;
        public readonly ObjectInstance? ConstructedInstance;

        public CallFrame(Chunk returnChunk, int returnIp, Scope returnScope, object? returnThis, ObjectInstance? constructedInstance)
        {
            ReturnChunk = returnChunk;
            ReturnIp = returnIp;
            ReturnScope = returnScope;
            ReturnThis = returnThis;
            ConstructedInstance = constructedInstance;
        }
    }

    /// <summary>
    /// Executes a chunk. Deliberately kept simple: a stack machine with
    /// a flat switch statement over the opcodes (in <see cref="Execute"/>)
    /// - each instruction is a small, self-contained step that is meant to be
    /// translatable 1:1 into a short sequence of native instructions (that
    /// was the requirement for the later "bytecode -> native code" step).
    ///
    /// Itself implements IDestructRunner: if the ownership cascade
    /// (Scope.Release/ObjectInstance.Destroy, see runtime layer) has to execute a destruct() body in the middle of
    /// an ExitScope/Return opcode,
    /// that happens via <see cref="RunNestedUntil"/> - a NESTED execution,
    /// but one working with the same instruction loop and the same frame stack,
    /// which returns only once the destructor frame
    /// (and everything it itself still called) has been torn down again. This
    /// avoids the reentrancy problem: the outer opcode handler (ExitScope)
    /// afterwards sees a consistent state again (_currentScope etc.), because the
    /// nested execution returns only when exactly that is the case.
    ///
    /// 'this' (_currentThis) is either an ObjectInstance (method/
    /// constructor/field-init/destructor execution) or a boxed Value
    /// (lambda with an 'on' binding to a non-object value) or null.
    /// </summary>
    /// <summary>An actively registered try handler (VM runtime counterpart to
    /// HandlerTemplate): frame depth + target scope at the time of
    /// registration, plus a reference to the compiled catch/finally data.</summary>
    internal readonly struct ActiveHandler
    {
        public readonly Chunk Chunk;
        public readonly int FrameDepthAtEntry;
        public readonly Scope TargetScope;
        public readonly HandlerTemplate Template;

        /// <summary>Height of the operand stack at registration: a `catch` starts again at this height - what the throw site left above it
        /// (started expressions, the enumerators of a `foreach`, operands of deeper calls) is set aside for a possible `resume()`.</summary>
        public readonly int StackPointer;

        /// <summary>During a `catch` block only the `finally` of the `try` stays active (the `catch` itself does not catch a further exception of the same `try`).</summary>
        public readonly bool FinallyOnly;

        public ActiveHandler(Chunk chunk, int frameDepthAtEntry, Scope targetScope, HandlerTemplate template, int stackPointer, bool finallyOnly = false)
        {
            FinallyOnly = finallyOnly;
            Chunk = chunk;
            FrameDepthAtEntry = frameDepthAtEntry;
            TargetScope = targetScope;
            Template = template;
            StackPointer = stackPointer;
        }
    }

    /// <summary>The execution state "frozen" at throw time at the
    /// throw site itself (not at the handler!) - for a possible later
    /// `resume()`. Corresponds exactly to what `CallFrame` records for a normal
    /// call, only additionally with the POPPED (but NOT destroyed via
    /// Release()) frames between throw site and handler.</summary>
    internal sealed class SavedContinuation
    {
        public readonly Chunk Chunk;
        public readonly int Ip;
        public readonly Scope Scope;
        public readonly object? This;
        public readonly List<CallFrame> Frames;

        /// <summary>The operands of the throw site above <see cref="ActiveHandler.StackPointer"/> (see there), played back on resuming.</summary>
        public Value[] Stack = Array.Empty<Value>();

        public SavedContinuation(Chunk chunk, int ip, Scope scope, object? thisObj, List<CallFrame> frames)
        {
            Chunk = chunk;
            Ip = ip;
            Scope = scope;
            This = thisObj;
            Frames = frames;
        }
    }

    /// <summary>An exception that is currently being handled in a `catch` and
    /// could still be continued via `resume()` - as long as this entry
    /// exists, SavedContinuation is "alive" (nothing of it has been
    /// released). HandlerFrameDepthAtEntry/TargetScope are needed to
    /// cleanly unwind the CURRENTLY RUNNING catch context on the actual `resume()` call
    /// (via UnwindTo, exactly as on the handler entry
    /// itself) - even if `resume()` is called from a nested function call
    /// INSIDE the catch block. Handler is
    /// additionally carried along to re-arm it on resume() -
    /// the continued throw site is conceptually "still inside the
    /// try block", a renewed throw in it must again be able to reach the same catch,
    /// and `UnregisterHandler` at the end of the try block
    /// expects its entry to still be there on a normal pass.</summary>
    internal sealed class PendingResume
    {
        public readonly SavedContinuation Continuation;
        public readonly ActiveHandler Handler;

        /// <summary>Number of active handlers when the `catch` began (without the finally-only one of the `try`): `resume()` discards everything above it.</summary>
        public readonly int HandlerCount;

        /// <summary>Exceptions whose `catch` this exception has left (it was thrown in a `catch` and handled by an outer `try`): their frozen throw sites
        /// are given up together with this one - the inner ones first, then this one. `resume()` of this exception reinstates them.</summary>
        public List<(ObjectInstance Exception, PendingResume Pending)>? Inner;

        public PendingResume(SavedContinuation continuation, ActiveHandler handler, int handlerCount)
        {
            Continuation = continuation;
            Handler = handler;
            HandlerCount = handlerCount;
        }
    }

    public sealed partial class VM : IDestructRunner
    {
        // The chunk currently running together with array copies of code/constants (see Chunk.CodeArray) - every
        // assignment to _currentChunk (call, return, exception jump, ...) updates them along.
        private Chunk _chunk;
        private byte[] _code;
        private Value[] _constants;
        private Chunk _currentChunk
        {
            get => _chunk;
            set
            {
                _chunk = value;
                _code = value.CodeArray;
                _constants = value.ConstantsArray;
            }
        }
        private readonly Scope _globalScope;
        private readonly NativeRegistry _natives;
        private readonly ExternRegistry _externs;
        private readonly IReadOnlyDictionary<string, RuntimeClass> _classes;
        private readonly IReadOnlyDictionary<string, ExternSignature> _externSignatures;

        /// <summary>The collective classes of the base-type extensions (`class extends string { ... }`, SPEC
        /// 5.5.1), indexed via `(int)ValueKind` - an array instead of a name lookup, because CallMethod
        /// looks here for EVERY method call on a non-object. `null` = for this kind of value there is
        /// no extension.</summary>
        private readonly RuntimeClass?[] _baseTypeClasses;

        // The value stack: an array with a stack pointer instead of a List<Value> (no version counter, no
        // double range check, no zeroing on removal) - push/pop are the hottest path of the VM.
        private long _copyArgMask; // set by the prefix CopyArgs, picked up by the next call opcode (TakeCopyMask)
        private Value[] _stack = new Value[256];
        private int _sp;
        private readonly Stack<CallFrame> _frames = new();
        private readonly List<ActiveHandler> _handlers = new();
        private readonly Dictionary<ObjectInstance, PendingResume> _pendingResumes = new();

        // Caches for dynamic extern linking (see CallExtern/
        // ResolveDynamicExtern) - a library is loaded only ONCE
        // (NativeLibrary.Load is not exactly cheap), a delegate built only once
        // per extern name (reflection/MakeGenericType likewise).
        private readonly Dictionary<string, IntPtr> _loadedNativeLibraries = new();
        private readonly Dictionary<string, Delegate> _dynamicExternDelegates = new();

        private Scope _currentScope;
        private object? _currentThis;
        private int _ip;

        // -----------------------------------------------------------
        // leave/terminate (docs/THREADING_DESIGN.md section 6) - cooperative
        // check points instead of real interruption: every running VM instance
        // notices a signal at the latest at the next safe point (see
        // PollSignals) and then cleanly unwinds itself (UnwindForShutdown),
        // WITHOUT passing through any normal `catch`/`catch(e)` -
        // both signals are deliberately NEVER checked against HandlerTemplate.Catches,
        // they run only through any `finally` blocks.
        // -----------------------------------------------------------

        /// <summary>Only for THIS one VM instance (== this one thread) -
        /// `leave` affects only the thread that calls it.</summary>
        private bool _leaveRequested;

        /// <summary>Shared across ALL VM instances/threads (`terminate`
        /// is a global emergency stop) - `volatile`, since it is
        /// set from ANY thread and read by ANY thread at its own
        /// check point. "First call wins" (THREADING_DESIGN.md 6.3) is ensured via
        /// Interlocked.CompareExchange in RequestTerminate -
        /// this field itself is therefore set only ONCE, by the winner, to
        /// a value other than 0.</summary>
        private static volatile bool _terminateRequested;
        private static Value _terminateValue = Value.MakeUndefined();
        private static readonly object _terminateGate = new();

        /// <summary>The value passed to `terminate(value)`, readable after completely
        /// synchronised shutdown of ALL threads (see
        /// THREADING_DESIGN.md 6.3) - `undefined` if the program ran through
        /// regularly without `terminate`. "Waiting until all threads
        /// are finished" is the host's job (see FireThreadHandle.Join in
        /// the Program.cs tests) - the VM itself cannot enforce that, it
        /// only ensures that ITS OWN unwinding (incl. finally)
        /// is complete before Run() returns.</summary>
        public static Value ExitValue => _terminateValue;

        /// <summary>Remembers for THIS thread that at the next check point
        /// `leave` is to be triggered (see VM.CurrentThreadVm for the
        /// assignment "which VM instance belongs to the calling thread" -
        /// basis of a native bridge function like `__leave()`, see
        /// Program.cs test, as long as there is no real `leave` language syntax
        /// yet).</summary>
        public void RequestLeave()
        {
            _leaveRequested = true;
            RaiseSignal();
        }

        /// <summary>Global emergency stop (see THREADING_DESIGN.md 6.3) - "first
        /// call wins", all further ones become no-ops.</summary>
        public static void RequestTerminate(Value value)
        {
            lock (_terminateGate)
            {
                if (_terminateRequested) return;
                _terminateValue = value;
                _terminateRequested = true;
            }
            RaiseSignal();
        }

        /// <summary>Intended only for tests/a fresh program execution -
        /// resets the GLOBAL terminate signal. The multithreading
        /// emergency stop is deliberately "first call wins, final" (see
        /// RequestTerminate) - this reset does NOT exist for normal
        /// language semantics, but purely so that several independent
        /// program runs (like our Program.cs tests) do not
        /// influence each other via the static field.</summary>
        public static void ResetTerminateForTests()
        {
            lock (_terminateGate)
            {
                _terminateRequested = false;
                _terminateValue = Value.MakeUndefined();
            }
        }

        /// <summary>The VM instance that is CURRENTLY running on the calling
        /// thread (set by Run() at the start) - basis for a
        /// native bridge function (which itself has no VM access, see the
        /// NativeFunction delegate) to still be able to call `RequestLeave()` on the
        /// RIGHT (its own) VM instance, without the
        /// language itself needing a `leave` syntax yet.</summary>
        [ThreadStatic]
        private static VM? _currentThreadVm;

        public static VM? CurrentThreadVm => _currentThreadVm;

        /// <summary>Marks EXACTLY one VM instance in the whole program as "the"
        /// main-thread instance - basis for `catch threads(...)`/`catch
        /// terminate(v)` (docs/THREADING_DESIGN.md 6.2/6.3), which according to the design
        /// run EXCLUSIVELY there, not on any fire thread.
        /// Must be set explicitly by the caller (no automatic
        /// "first created VM is the main VM" - that would be fragile in tests that
        /// run several VMs independently of one another).</summary>
        public bool IsMainThreadVm { get; }

        /// <summary>Set explicitly ONLY by FireRuntime.FireVm (see there) -
        /// deliberately NOT simply "!IsMainThreadVm": most VM instances
        /// in all the remaining code (every simple single-VM test run,
        /// every VM without any multithreading relation) never set `isMainThreadVm`
        /// and are still NOT a fire thread - an unhandled
        /// exception there must continue to be signalled quite normally via UnhandledException
        /// (the original behaviour assumed everywhere), not silently redirected
        /// into the global fire-thread queue. Only a VM instance that REALLY arose via `fire`
        /// is to get this special behaviour.</summary>
        public bool IsFireThreadVm { get; }

        /// <summary>A script `throw` for which no matching `catch` was found in THIS VM instance -
        /// set instead of thrown (see ThrowException, last branch): deliberately NO C# exception any more
        /// beyond the Run() call site (see docs/PORTING.md,
        /// section "VM-internal control flow") - in a C++ version
        /// without exceptions (usual on embedded targets) there would be no
        /// equivalent for it anyway. Run() returns quite normally in this case
        /// (see CheckShutdownSignals/_stopExecutionRequested);
        /// after Run() the caller checks this field, instead of putting a `try`/
        /// `catch` around the call. `null` means "no unhandled
        /// error" (the normal case). A pure C# convenience for
        /// host code that prefers to work with a real exception remains
        /// possible via Bytecode.UncaughtScriptException - which can be CONSTRUCTED
        /// (but is no longer thrown internally), e.g.
        /// `throw new UncaughtScriptException(vm.UnhandledException)`.</summary>
        public ObjectInstance? UnhandledException { get; private set; }

        /// <summary>When set (the editor debugger does), every `throw` records the call stack at the throw site
        /// (innermost first) so that an UNHANDLED exception can be shown with a stack trace and its line marked.</summary>
        public bool CaptureErrorTrace { get; set; }

        /// <summary>Source locations (innermost first) of the throw site of the unhandled exception - only filled if
        /// <see cref="CaptureErrorTrace"/> was on; the first entry is the line that threw.</summary>
        public IReadOnlyList<(int SourceIndex, int Line)>? UnhandledTrace { get; private set; }

        private List<(int SourceIndex, int Line)>? _lastThrowTrace;

        private void RecordThrowTrace()
        {
            var trace = new List<(int SourceIndex, int Line)> { _currentChunk.GetLocation(_ip) };
            foreach (var frame in _frames) // Stack enumerates top (innermost) first
                trace.Add(frame.ReturnChunk.GetLocation(Math.Max(0, frame.ReturnIp - 1)));
            _lastThrowTrace = trace;
        }

        /// <summary>Queue shared across ALL VM instances/threads
        /// for unhandled fire-thread exceptions (see ThrowException),
        /// processed by the main thread at its next check point (see
        /// CheckShutdownSignals/HandleDeliveredThreadException) - thread-safe
        /// via ConcurrentQueue, since several fire threads may throw at the same time
        /// .</summary>
        private static readonly System.Collections.Concurrent.ConcurrentQueue<ObjectInstance> _pendingThreadExceptions = new();

        /// <summary>Set when THIS VM instance has already performed its unwinding (unwind
        /// incl. finally) ITSELF (see
        /// ThrowException's fire-thread branch) and at the next check point in
        /// Run() only has to STOP cleanly, without continuing to use the (possibly
        /// meanwhile meaningless) `_ip`/`_currentChunk` state -
        /// unlike `_leaveRequested`/
        /// `_terminateRequested`, where CheckShutdownSignals itself still performs the unwind
        /// .</summary>
        private bool _stopExecutionRequested;

        /// <summary>`leave`/`terminate` has ended this VM: at the halt the global scope is released, as at the normal program end
        /// (the main program waits for all fire threads beforehand) - unlike after an unhandled exception.</summary>
        private bool _shutdownReleasePending;

        /// <summary>`leave`/`terminate` was called in a NESTED execution (destructor, property, operator, callback):
        /// the VM stops there immediately (halt), the orderly unwinding (finally, destructors) is made up for by
        /// <see cref="FinishDeferredShutdown"/> as soon as the nesting is back.</summary>
        private bool _shutdownDeferred;

        /// <summary>A lambda that a native call runs nested on THIS VM (see <see cref="CallLambdaInline"/>): where it
        /// began - frame depth, scope and stack height of the caller and how many handlers the caller had already registered
        /// (a `throw` in the callback must not see the caller's try/catch).</summary>
        private readonly record struct CallbackBoundary(int FrameDepth, Scope Scope, int HandlerFloor, int StackPointer);
        private readonly Stack<CallbackBoundary> _callbackBoundaries = new();
        private ObjectInstance? _callbackError;

        // -----------------------------------------------------------
        // Global variables and fire threads (docs/THREADING_DESIGN.md section 7, Runtime.GlobalsBroker)
        // -----------------------------------------------------------

        /// <summary>In the main program: set as soon as a `fire` (or `fire global`) runs for the first time. From then on this VM writes globals
        /// under the tree lock (fire threads read them at the same time).</summary>
        private GlobalsBroker? _ownerBroker;

        /// <summary>In a fire thread: the mediation to the main program. The globals slots 0 to `_sharedCount` - 1 are the real
        /// globals of the main program (read directly, write in a section); above them lie its own (taking/with, top-level variables
        /// of the block) in the private `_globalScope`.</summary>
        private GlobalsBroker? _threadBroker;
        private int _sharedCount;

        /// <summary>Depth of the section that this thread currently holds (0 = none). Nested accesses (a method that writes `this.x = ...`
        /// in an already granted section) run directly.</summary>
        private int _sectionDepth;
        private object? _sectionHandle;

        /// <summary>Attaches this (fire-thread) VM to the globals of the main program (see FireRuntime.FireVmTaking).</summary>
        internal void AttachToGlobals(GlobalsBroker broker, int sharedGlobalCount)
        {
            _threadBroker = broker;
            _sharedCount = sharedGlobalCount;
        }

        private GlobalsBroker EnsureOwnerBroker()
        {
            if (_ownerBroker != null) return _ownerBroker;
            var broker = new GlobalsBroker(this, _globalScope);
            _ownerBroker = broker;
            ShareGlobals(broker);
            return broker;
        }

        /// <summary>Takes everything the globals reach (objects including ownership, fields, arrays, static fields) into the shared area.</summary>
        private void ShareGlobals(GlobalsBroker broker)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var owned in _globalScope.OwnedObjects.ToArray())
                ShareValue(Value.MakeClassRef(owned), broker.Lock, seen);
            for (int i = 0; i < _globalScope.SlotCount; i++)
                ShareValue(_globalScope.GetSlot(i), broker.Lock, seen);
            foreach (var rc in _classes.Values)
                foreach (var staticValue in rc.StaticFieldValues.Values.ToArray())
                    ShareValue(staticValue, broker.Lock, seen);
        }

        private static void ShareValue(Value value, ThreadShareLock treeLock, HashSet<object> seen)
        {
            if (value.Kind == ValueKind.Class)
            {
                var obj = (ObjectInstance)value.AsObjectRef();
                if (!seen.Add(obj)) return;
                obj.MarkGlobalsDomain(treeLock);
                foreach (var field in obj.Fields.ToArray()) ShareValue(field.Value, treeLock, seen);
                foreach (var child in obj.OwnedObjects.ToArray()) ShareValue(Value.MakeClassRef(child), treeLock, seen);
            }
            else if (value.Kind == ValueKind.Array)
            {
                var array = value.AsArray();
                if (!seen.Add(array)) return;
                array.IsShared = true;
                foreach (var item in array.Items) ShareValue(item, treeLock, seen);
            }
        }

        /// <summary>An array that a fire thread reaches via the globals: shared from now on (element accesses under the lock).</summary>
        private static void MarkShared(Value value)
        {
            if (value.Kind != ValueKind.Array) return;
            var array = value.AsArray();
            if (array.IsShared) return;
            array.IsShared = true;
            foreach (var item in array.Items) MarkShared(item);
        }

        /// <summary>Registers the thread for a section (or only counts up if it already holds one). Only when the main program grants the section at
        /// `sync globals` does the call return.</summary>
        private void EnterGlobalsSection()
        {
            if (_sectionDepth++ == 0) _sectionHandle = _threadBroker!.EnterSection();
        }

        private void ExitGlobalsSection()
        {
            if (--_sectionDepth == 0)
            {
                var handle = _sectionHandle;
                _sectionHandle = null;
                _threadBroker!.ExitSection(handle);
            }
        }

        /// <summary>Safety net at the end of a fire thread: a section still held is released, otherwise the main program waits forever.</summary>
        internal void ReleaseGlobalsSections()
        {
            if (_threadBroker == null || _sectionDepth == 0) return;
            _sectionDepth = 0;
            var handle = _sectionHandle;
            _sectionHandle = null;
            _threadBroker.ExitSection(handle);
        }

        private bool NeedsSection(ObjectInstance obj) => _threadBroker != null && _sectionDepth == 0 && obj.InGlobalsDomain;

        private Value LoadSharedGlobal(int slot)
        {
            var broker = _threadBroker!;
            Value value;
            broker.Lock.Enter();
            try { value = slot < broker.Scope.SlotCount ? broker.Scope.GetSlot(slot) : Value.MakeUndefined(); }
            finally { broker.Lock.Exit(); }
            MarkShared(value);
            return value;
        }

        private void StoreSharedGlobal(int slot, Value value)
        {
            var broker = _threadBroker!;
            EnterGlobalsSection();
            try
            {
                broker.Lock.Enter();
                try
                {
                    if (slot >= broker.Scope.SlotCount)
                        throw new InvalidOperationException("This global variable has not been declared in the main program yet.");
                    broker.Scope.SetSlot(slot, value);
                }
                finally { broker.Lock.Exit(); }
            }
            finally { ExitGlobalsSection(); }
        }

        /// <summary>Main program, after the first `fire`: writing the globals under the lock (fire threads read at the same time).</summary>
        private void StoreOwnerGlobal(int slot, Value value)
        {
            var broker = _ownerBroker!;
            broker.Lock.Enter();
            try { _globalScope.SetSlot(slot, value); }
            finally { broker.Lock.Exit(); }
        }

        private void DeclareOwnerGlobal(Value value)
        {
            var broker = _ownerBroker!;
            broker.Lock.Enter();
            try { _globalScope.DefineSlot(value); }
            finally { broker.Lock.Exit(); }
        }

        /// <summary>Read an element of a shared array: in the fire thread under the lock, in the main program (only writer) directly.</summary>
        private bool TryGetSharedElement(ScriptArray array, long index, out Value value)
        {
            var broker = _threadBroker;
            if (broker == null) return array.TryGet(index, out value);
            broker.Lock.Enter();
            try { return array.TryGet(index, out value); }
            finally { broker.Lock.Exit(); }
        }

        /// <summary>Write an element of a shared array: in the fire thread in a section, everywhere under the lock.</summary>
        private bool TrySetSharedElement(ScriptArray array, long index, Value value)
        {
            var broker = _threadBroker ?? _ownerBroker;
            if (broker == null) return array.TrySet(index, value);
            bool inThread = _threadBroker != null;
            if (inThread) EnterGlobalsSection();
            try
            {
                broker.Lock.Enter();
                try { return array.TrySet(index, value); }
                finally { broker.Lock.Exit(); }
            }
            finally { if (inThread) ExitGlobalsSection(); }
        }

        /// <summary>Method call of a fire thread on an object of the shared area: the method runs as a whole while the thread
        /// holds the section - so even its read-modify-write is atomic.</summary>
        private Value? CallGlobalsMethodInSection(ObjectInstance obj, string methodName, Value[] args)
        {
            EnterGlobalsSection();
            try
            {
                var rc = ResolveClass(obj.ClassName);
                if (rc.FindMethodWithAccess(methodName, args.Length).Item1 == null && TryCallOwnershipMethod(obj, methodName, args, out var ownershipResult))
                    return ownershipResult;
                return CallMethodNested(obj, methodName, args);
            }
            finally { ExitGlobalsSection(); }
        }

        /// <summary>`sync globals` in the main program: processes what fire threads have registered (grant sections, execute jobs) and
        /// what host threads have queued as a callback. Returns the number of entries.</summary>
        private int SyncGlobalsNow() => DrainInbound() + (_ownerBroker?.Drain() ?? 0);

        // ---- Callbacks from host threads (e.g. a serial event): they do NOT run on the foreign thread, but are queued here
        // and executed by the main program - at `sync globals` or (without `#nosync`) automatically at a safe point. This way they see the real
        // globals, and there is no concurrent access to them.

        private readonly record struct InboundCallback(LambdaValue Lambda, Value[] Args, Action<string>? OnUnhandled);
        private readonly System.Collections.Concurrent.ConcurrentQueue<InboundCallback> _inbound = new();
        private volatile bool _acceptingCallbacks;

        /// <summary>Should the main program process the queue itself at safe points? Default yes; `#nosync` switches it off.</summary>
        private bool _autoSync = true;

        /// <summary>Accepts a callback from ANY thread (thread-safe) and queues it for this VM. false if the VM is not (any longer)
        /// running - then the caller has to choose another way.</summary>
        public bool PostCallback(LambdaValue lambda, Value[] args, Action<string>? onUnhandled)
        {
            if (!_acceptingCallbacks) return false;
            _inbound.Enqueue(new InboundCallback(lambda, args, onUnhandled));
            RaiseSignal();
            FireRuntime.WakeWaitingOwner();
            return true;
        }

        private int DrainInbound()
        {
            int handled = 0;
            while (_inbound.TryDequeue(out var callback))
            {
                handled++;
                try
                {
                    var error = CallLambdaInline(callback.Lambda, callback.Args);
                    if (error != null) callback.OnUnhandled?.Invoke(new UncaughtScriptException(error).Message);
                }
                catch (Exception ex)
                {
                    callback.OnUnhandled?.Invoke(ex.Message);
                }
                if (_stopExecutionRequested) break;
            }
            return handled;
        }

        /// <summary>Automatic processing at a safe point (not in nested execution, not with `#nosync`): first host callbacks, then
        /// the sections and jobs of the fire threads. true if the program was ended in the process (`leave`/`terminate` in a job).</summary>
        private bool AutoSyncNow()
        {
            if (!_autoSync || _nestedDepth > 0) return false;
            if (!_inbound.IsEmpty) DrainInbound();
            if (!_stopExecutionRequested && _ownerBroker != null && _ownerBroker.HasPending) _ownerBroker.Drain();
            return _stopExecutionRequested;
        }

        /// <summary>Executes a `fire global` job on this (the owner) VM. An unhandled exception in it is treated like that of a
        /// fire thread: it goes to the main program (`catch threads`), otherwise it aborts.</summary>
        internal void RunGlobalsJob(LambdaValue lambda, Value[] args)
        {
            var error = CallLambdaInline(lambda, args);
            if (error != null)
            {
                _pendingThreadExceptions.Enqueue(error);
                RaiseSignal();
            }
        }
        // (Now checked only in the NESTED loops and in single step - Run() reads the halt after StopExecution().)

        public VM(
            Chunk chunk,
            Scope globalScope,
            NativeRegistry natives,
            IReadOnlyDictionary<string, RuntimeClass>? classes = null,
            ExternRegistry? externs = null,
            IReadOnlyDictionary<string, ExternSignature>? externSignatures = null,
            bool isMainThreadVm = false,
            bool isFireThreadVm = false,
            VmExecutionMode executionMode = VmExecutionMode.Debug)
        {
            _currentChunk = chunk;
            _globalScope = globalScope;
            _natives = natives;
            _classes = classes ?? new Dictionary<string, RuntimeClass>();
            _externs = externs ?? new ExternRegistry();
            _externSignatures = externSignatures ?? new Dictionary<string, ExternSignature>();
            _currentScope = globalScope;
            _baseTypeClasses = new RuntimeClass?[Enum.GetValues<ValueKind>().Length];
            foreach (var kind in Enum.GetValues<ValueKind>())
                if (BaseTypeExtensions.ClassNameFor(kind) is { } extensionClassName
                    && _classes.TryGetValue(extensionClassName, out var extensionClass))
                    _baseTypeClasses[(int)kind] = extensionClass;
            IsMainThreadVm = isMainThreadVm;
            if (isMainThreadVm) ResetDefaultTimeout(); // a new program starts again with the default waiting time (`#timeout` sets it anew)
            IsFireThreadVm = isFireThreadVm;
            ExecutionMode = executionMode;
        }

        /// <summary>See VmExecutionMode documentation - controls among other things whether
        /// ArrayGet/ArraySet/buffer accesses skip their bounds check
        /// (see the respective opcode handlers).</summary>
        public VmExecutionMode ExecutionMode { get; }

        // -----------------------------------------------------------
        // Shutdown signals: checked only at safe points, ending via the halt chunk
        // -----------------------------------------------------------
        //
        // Signals mostly come from OTHER threads (`terminate`, an unhandled fire-thread exception for the
        // main thread) - no exception can be "thrown into" a running thread, it has to
        // notice them itself. That no longer happens before every instruction, but only at the safe points (loop
        // back jump, call, `leave`/`terminate`), and there with ONE comparison: every signal increases the global
        // counter `s_signalEpoch`, every VM remembers the last seen state (`_seenEpoch`) - only on a
        // deviation does the actual check run (CheckShutdownSignals).
        //
        // ENDING is deliberately not a C# exception (docs/PORTING.md, "VM-internal control flow": in a C++ version
        // without exceptions there would be no equivalent for it), but a pure state switch like the jump into a
        // `catch`: StopExecution() sets chunk/ip to a chunk consisting only of `Halt` - Run() reads
        // this `Halt` next and returns, without any instruction having to query a stop flag.

        private static int s_signalEpoch;
        private int _seenEpoch = int.MinValue;

        /// <summary>Depth of the nested executions (RunNestedUntil) - signals are not checked in them (as before).</summary>
        private int _nestedDepth;

        /// <summary>The chunk that StopExecution() switches to: only a `Halt`.</summary>
        private static readonly Chunk StopChunk = BuildStopChunk();

        private static Chunk BuildStopChunk()
        {
            var chunk = new Chunk();
            chunk.EmitOp(OpCode.Halt);
            return chunk;
        }

        internal static void RaiseSignal() => System.Threading.Interlocked.Increment(ref s_signalEpoch);

        /// <summary>Ends the execution of this VM: notes the stop and jumps to the halt chunk. Callers must afterwards
        /// return from their instruction immediately (as after ThrowException).</summary>
        /// <summary>Discards the (meaningless) return value of a nested execution - unless the VM was ended in the process
        /// (unhandled exception): then the call returned nothing.</summary>
        private void PopNestedResult()
        {
            if (!_stopExecutionRequested) Pop();
        }

        private void StopExecution()
        {
            _stopExecutionRequested = true;
            _currentChunk = StopChunk;
            _ip = 0;
        }

        /// <summary>Safe point: has a signal been reported since the last time (one comparison, nothing else)? Returns true
        /// if the VM was ended as a result - the calling instruction must then return immediately.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private bool PollSignals() =>
            _seenEpoch != System.Threading.Volatile.Read(ref s_signalEpoch) && PollSignalsSlow();

        /// <summary>Like <see cref="PollSignals"/>, but AFTER a completely executed instruction (`_ip` is already at the next
        /// instruction boundary) - for native calls: a native function may trigger `leave`/`terminate` (e.g. `VM.RequestLeave`), that must
        /// take effect immediately afterwards.</summary>
        private void PollSignalsAfterOp()
        {
            if (_seenEpoch == System.Threading.Volatile.Read(ref s_signalEpoch) || _nestedDepth > 0) return;
            _seenEpoch = System.Threading.Volatile.Read(ref s_signalEpoch);
            if (CheckShutdownSignals()) StopExecution();
            else AutoSyncNow(); // Host callbacks and fire threads that wait for the main program (see `#nosync`)
        }

        private bool PollSignalsSlow()
        {
            // In a nested execution (destructor, operator overload, property, ...) nothing is checked - as
            // before; the signal stays pending and applies at the next safe point outside.
            if (_nestedDepth > 0) return false;
            _seenEpoch = System.Threading.Volatile.Read(ref s_signalEpoch);

            // The call comes from inside an instruction whose opcode has already been read: for a possibly nested
            // handler `_ip` must point to the instruction boundary, so that its return lands at the right place.
            _ip--;
            if (CheckShutdownSignals())
            {
                StopExecution();
                return true;
            }
            _ip++;
            // Host callbacks and fire threads that wait for the main program (see `#nosync`): at the place the caller is about to
            // continue, the processing runs nested and returns unchanged to here.
            return AutoSyncNow();
        }

        /// <summary>The `leave`/`terminate` call of the VM itself: it goes into the halt IMMEDIATELY, regardless of whether the signal
        /// has been pending for a while (e.g. `terminate` has already been triggered by another thread - the caller must still not
        /// execute another statement). The opcode has no operands, so `_ip - 1` is the instruction boundary.</summary>
        private void ShutdownSelfNow()
        {
            _seenEpoch = System.Threading.Volatile.Read(ref s_signalEpoch);
            if (_nestedDepth > 0)
            {
                // In the middle of a destructor/property/operator/callback: only stop here (the callers know this
                // from the stop through an unhandled exception), the unwinding follows in FinishDeferredShutdown.
                _shutdownDeferred = true;
                StopExecution();
                return;
            }
            _ip--;
            CheckShutdownSignals();
            StopExecution();
        }

        /// <summary>Makes up for the unwinding of a `leave`/`terminate` triggered in nested execution (see
        /// <see cref="_shutdownDeferred"/>). true = something was done, the VM is back on the halt chunk afterwards.</summary>
        private bool FinishDeferredShutdown()
        {
            if (!_shutdownDeferred || _nestedDepth > 0) return false;
            _shutdownDeferred = false;
            _stopExecutionRequested = false; // the nested runs during unwinding (finally, destructors) must be allowed to run again
            _currentChunk = StopChunk;
            _ip = 0;
            CheckShutdownSignals();
            StopExecution();
            return true;
        }

        public void Run()
        {
            // `terminate` is process-wide: a program that ends with it must not stop the NEXT program of the same host
            // (editor, embedding application) right at its start.
            if (IsMainThreadVm) ResetTerminateForTests();
            _currentThreadVm = this;
            _acceptingCallbacks = true;
            try { RunLoop(); }
            finally
            {
                _acceptingCallbacks = false; // host callbacks take the other way afterwards
                _currentThreadVm = null; // a callback firing later on this thread does not look for a finished VM
                _ownerBroker?.Close();   // no owner any more: waiting fire threads are released
            }
        }

        private void RunLoop()
        {
            _ip = 0;

            while (true)
            {
                var op = (OpCode)ReadByte();
                if (op == OpCode.Halt)
                {
                    // A leave/terminate called in nested execution is only now unwound in an orderly way;
                    // afterwards the halt chunk is there again and the next read lands here again.
                    if (FinishDeferredShutdown()) continue;
                    // Normal end or orderly leave/terminate (not the halt chunk of an unhandled exception).
                    if (!_stopExecutionRequested || _shutdownReleasePending) ReleaseGlobalScopeAtEnd();
                    return;
                }
                Step(op);
            }
        }

        /// <summary>Normal program end (or end of a thread): the global scope is released like any other scope on
        /// leaving - `destruct()` runs for everything that belongs to it, open streams are closed.
        /// The main program waits beforehand for all fire threads still running (they can write back into its objects via `sync`
        /// ). A host that still needs the state AFTER the run (tests, inspection) switches that off with
        /// <see cref="DestroyGlobalsAtEnd"/>.</summary>
        private void ReleaseGlobalScopeAtEnd()
        {
            bool afterShutdown = _shutdownReleasePending;
            _shutdownReleasePending = false;
            if (!DestroyGlobalsAtEnd) return;
            if (!IsFireThreadVm) FireRuntime.WaitForAllFireThreads(_ownerBroker); // the main program (every VM that is not a fire thread); meanwhile it serves the threads' queue
            ReleaseGlobalScopeAfterStop(afterShutdown);
        }

        /// <summary>Releases the global scope; after a leave/terminate the VM is already in the stop state in which
        /// destructors no longer run (see RunDestructor) - it is briefly lifted for that.</summary>
        private void ReleaseGlobalScopeAfterStop(bool afterShutdown)
        {
            if (!afterShutdown) { ReleaseGlobalScope(); return; }
            _stopExecutionRequested = false;
            try { ReleaseGlobalScope(); }
            finally { _stopExecutionRequested = true; }
        }

        /// <summary>Releases the global scope. For a fire thread the objects with `SyncOrigin` are copies of objects of the
        /// main program (globals snapshot, `taking`) - they stay untouched, only what the thread created itself is
        /// destroyed.</summary>
        private void ReleaseGlobalScope()
        {
            if (IsFireThreadVm) _globalScope.ReleaseWhere(this, o => !o.IsTakingCopy);
            else _globalScope.Release(this);
        }

        /// <summary>Should the normal program end release the global scope (default: yes)? `false` for hosts that still
        /// read or keep using the objects after the run (e.g. tests that afterwards let threads work on them).</summary>
        public bool DestroyGlobalsAtEnd { get; set; } = true;

        /// <summary>The cooperative check point for `leave`/`terminate` (see
        /// field documentation above) - deliberately checked before EVERY single instruction
        /// (not only at function calls/loop back jumps), that is
        /// the simplest, guaranteed correct variant (never "misses" a
        /// signal) - a later optimisation could restrict that to rarer,
        /// but structurally more sensible points, should the
        /// overhead ever become relevant.</summary>
        private bool CheckShutdownSignals()
        {
            // Only the main thread processes delivered fire-thread
            // exceptions (see HandleDeliveredThreadException) - runs
            // NESTED (like RunFinallyNested), the main thread afterwards continues
            // quite normally, so it is NOT stopped.
            if (IsMainThreadVm)
                while (_pendingThreadExceptions.TryDequeue(out var excInstance))
                {
                    if (HandleDeliveredThreadException(excInstance)) return true;
                }

            // `terminate` and `leave` both end like the normal program end: unwind scopes (finally, destructors),
            // afterwards - at the halt, after the end of all fire threads - release the global scope (see _shutdownReleasePending).
            if (_terminateRequested)
            {
                UnwindForShutdown();
                _shutdownReleasePending = true;
                if (IsMainThreadVm) RunTerminateHandlerIfAny();
                return true;
            }
            if (_leaveRequested)
            {
                _leaveRequested = false;
                UnwindForShutdown();
                _shutdownReleasePending = true;
                return true;
            }
            return false;
        }

        /// <summary>Cleanly unwinds the ENTIRE current state of THIS VM instance:
        /// first all still active try handlers (in the usual
        /// order, see ThrowException), but - unlike with
        /// a real exception - NEVER matching a `catch` (every handler
        /// is treated like "no matching catch"), its `finally`
        /// however runs quite normally. Afterwards frames/scopes WITHOUT
        /// their own try/finally may still be active (a simple method call without a
        /// try block) - these are finally regularly unwound down to the base (global
        /// scope, frame depth 0) (Release() per scope,
        /// incl. destructor cascade), only without any further finally (since none
        /// is registered any more). Common basis for `leave` (only
        /// this one VM instance) and `terminate` (every VM instance notices
        /// the global signal at its own next check point and unwinds
        /// itself EXACTLY the same way - only the triggering differs).</summary>
        private void UnwindForShutdown(bool destroyGlobalScope = false)
        {
            while (_handlers.Count > 0)
            {
                var handler = _handlers[^1];
                _handlers.RemoveAt(_handlers.Count - 1);

                UnwindTo(handler.FrameDepthAtEntry, handler.TargetScope);
                if (handler.Template.FinallyAddr is int finallyAddr)
                    RunFinallyInlineNested(handler.Chunk, finallyAddr);
            }

            UnwindTo(0, _globalScope);
            if (destroyGlobalScope) ReleaseGlobalScope();
        }

        /// <summary>Executes a delivered, unhandled fire-thread
        /// exception handler (`catch threads(...)`) nested - like
        /// RunFinallyNested: it runs, AFTERWARDS the main thread continues normally at exactly the
        /// interrupted place (no stopping, unlike with
        /// leave/terminate). No matching handler registered -> complete
        /// program abort, like an unhandled exception in the main thread
        /// itself (docs/THREADING_DESIGN.md 6.2).</summary>
        private bool HandleDeliveredThreadException(ObjectInstance excInstance)
        {
            var handlerProto = FindGlobalThreadsCatch(excInstance);
            if (handlerProto == null)
            {
                // Complete program abort - set instead of thrown, see
                // UnhandledException documentation. The caller (CheckShutdownSignals)
                // MUST stop immediately afterwards, instead of possibly processing further exceptions
                // standing in the queue.
                UnhandledException = excInstance;
                return true;
            }

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;

            var handlerScope = new Scope(_globalScope);
            if (handlerProto.ParamCount >= 1)
                handlerScope.DefineSlot(Value.MakeClassRef(excInstance));

            _currentThis = null;
            _currentScope = handlerScope;
            _currentChunk = handlerProto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);
            PopNestedResult(); // Return value of the handler proto unused, like RunFinallyNested/RunDestructor.
            return false;
        }

        /// <summary>`catch threads(...)` resolution - exactly like FindMatchingCatch
        /// for a normal try/catch (first hit wins, `typeName ==
        /// null` matches everything, see InstanceMatchesClassName for 'Exception'
        /// as a special case), only against the GLOBAL registration instead of
        /// a single HandlerTemplate.</summary>
        private FunctionProto? FindGlobalThreadsCatch(ObjectInstance exc)
        {
            foreach (var (typeName, proto) in GlobalHandlers.ThreadsCatches)
            {
                if (typeName == null) return proto;
                if (InstanceMatchesClassName(exc, typeName)) return proto;
            }
            return null;
        }

        /// <summary>Executes `catch terminate(v)` nested, if registered
        /// - unlike HandleDeliveredThreadException NOTHING has to
        /// "continue normally afterwards" here, since the caller (CheckShutdownSignals)
        /// returns `true` (= Run() is to stop) directly afterwards anyway
        /// - the return frame pushed here is therefore never
        /// really needed, but is necessary so that RunNestedUntil knows at all
        /// when the handler call has ended via Return.</summary>
        private void RunTerminateHandlerIfAny()
        {
            var proto = GlobalHandlers.TerminateHandler;
            if (proto == null) return;

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;

            var handlerScope = new Scope(_globalScope);
            if (proto.ParamCount >= 1)
                handlerScope.DefineSlot(_terminateValue);

            _currentThis = null;
            _currentScope = handlerScope;
            _currentChunk = proto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);
            PopNestedResult();
        }

        /// <summary>`process X`/`try process X` (see Ast.ProcessStmt/
        /// TryProcessExpr documentation) - fetches EXACTLY ONE message from the mailbox
        /// of the actor (see Runtime.ActorMailbox.TryProcessOne) and executes
        /// the matching method NESTED (like the CallMethodNested pattern:
        /// `this` = the actor, return value unused - a message has
        /// no result that anybody could pick up). Returns `false` only
        /// in the non-blocking case when the mailbox is currently empty -
        /// when blocking this method always returns `true` (it waits until
        /// something is there).</summary>
        private bool ProcessOneMessage(ObjectInstance actor, bool blocking)
        {
            var mailbox = actor.Mailbox
                ?? throw new InvalidOperationException(
                    $"'process'/'try process' on an instance of '{actor.ClassName}', which is not an actor.");

            if (!mailbox.TryProcessOne(blocking, out var message))
                return false;

            var rc = ResolveClass(actor.ClassName);
            var proto = rc.FindMethod(message.MethodName, message.Args.Length)
                ?? throw new InvalidOperationException(
                    DescribeMethodNotFound(rc, message.MethodName, message.Args.Length));
            CheckArity(proto, message.Args.Length);
            var args = FillDefaultArgs(proto, message.Args, actor);

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;

            var scope = new Scope(_globalScope);
            foreach (var a in args) scope.DefineSlot(a);

            _currentThis = actor;
            _currentScope = scope;
            _currentChunk = proto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);
            PopNestedResult(); // Return value unused, see documentation above.
            return true;
        }

        // -----------------------------------------------------------
        // Stepping/inspection API for external tools (step debugger in the
        // editor sub-project) - Run() stays unchanged for the normal "just
        // run through" case, this here is an ALTERNATIVE
        // step-by-step capable entry into the same Execute() loop.
        // -----------------------------------------------------------

        private bool _steppingStarted;

        public bool IsHalted { get; private set; }
        
        /// <summary>Current source position (source index + source line) at the
                                                  /// execution position (via the chunk line table, see Chunk.MarkLine) -
                                                  /// (0, 0) if no information is available (e.g. programmatically
                                                  /// built chunks without a compiler run). Basis for the line
                                                  /// highlighting in the editor AND for the step/breakpoint logic below -
                                                  /// IMPORTANT: since a program can consist of several files (SPEC
                                                  /// "Multiple source files") the mere line comparison alone
                                                  /// is no longer enough (line 5 in file A and line 5 in file B are
                                                  /// different places) - ALWAYS compare both values together.</summary>
        public (int SourceIndex, int Line) CurrentLocation => _currentChunk.GetLocation(_ip);

        /// <summary>Short form for `CurrentLocation.Line` - for callers to whom
        /// (still) only ONE file is known (e.g. the old single-file
        /// editor window) and who can therefore ignore the source index.</summary>
        public int CurrentLine => CurrentLocation.Line;



        /// <summary>Executes EXACTLY one instruction - counterpart to the loop body of Run(),
        /// only callable individually with an explicit halt status
        /// instead of an endless loop. The first call initialises `_ip`
        /// as Run() does too. Returns false as soon as the program has ended
        /// (afterwards every further call stays a no-op returning false).</summary>
        public bool StepInstruction()
        {
            if (IsHalted) return false;
            if (!_steppingStarted) BeginStepping();

            var op = (OpCode)ReadByte();
            if (op == OpCode.Halt) return FinishAtHalt();
            Step(op);
            return AfterStep();
        }

        /// <summary>The first step: like Run() (see there) - _currentThreadVm must also point correctly to THIS
        /// instance during step-by-step debugging, otherwise every native bridge that relies on it (see CurrentThreadVm documentation) would
        /// wrongly see null during single-step debugging, although clearly ONE VM instance is currently active.</summary>
        private void BeginStepping()
        {
            _currentThreadVm = this;
            _ip = 0;
            _steppingStarted = true;
            _acceptingCallbacks = true;
        }

        /// <summary>`Halt` read: like Run(), the normal end cleans up the global scope - in single step without waiting for threads (the
        /// debugger may have stopped them). Returns false (ended).</summary>
        private bool FinishAtHalt()
        {
            if ((!_stopExecutionRequested || _shutdownReleasePending) && DestroyGlobalsAtEnd) ReleaseGlobalScopeAfterStop(_shutdownReleasePending);
            _shutdownReleasePending = false;
            IsHalted = true;
            _acceptingCallbacks = false;
            return false;
        }

        /// <summary>After every instruction in single step: process queues (fire threads, host callbacks) and notice a program end
        /// through `leave`/`terminate`/unhandled exception. false = ended.</summary>
        private bool AfterStep()
        {
            // Only if a signal has arrived since the last safe point (every queueing triggers one, see RaiseSignal):
            // querying the queues on EVERY instruction was the biggest item of the single-step loop.
            if (_autoSync && _seenEpoch != System.Threading.Volatile.Read(ref s_signalEpoch) && _nestedDepth == 0 && !_stopExecutionRequested
                && (!_inbound.IsEmpty || (_ownerBroker != null && _ownerBroker.HasPending)))
                AutoSyncNow();

            // Since UnhandledException (see there) an unhandled script exception is no longer thrown, but only SET -
            // Run() notices that via CheckShutdownSignals (deliberately NOT called here, see field documentation), so during step-by-step debugging
            // it has to be checked explicitly HERE, otherwise StepLine/StepInto/StepOut/Continue (all build on this
            // method) would simply keep running as if nothing had happened, instead of stopping cleanly.
            if (_stopExecutionRequested)
            {
                FinishDeferredShutdown();
                // leave/terminate end like the normal program end (without waiting for threads, the debugger may have stopped them).
                if (_shutdownReleasePending && DestroyGlobalsAtEnd) ReleaseGlobalScopeAfterStop(true);
                _shutdownReleasePending = false;
                IsHalted = true;
                _acceptingCallbacks = false;
                return false;
            }

            return true;
        }

        /// <summary>Runs until a breakpoint, a pause request or the program end (the "Continue" run of the
        /// debugger). true = stopped (at a breakpoint or on request, there is still something to execute), false = ended.
        ///
        /// Fast, because lookup only happens at a line change: the position applies to the whole byte range of its
        /// line (see Chunk.GetLocationRange), and the pause query comes only every 256 instructions. A breakpoint
        /// counts only on ENTERING its line, not on every instruction in it - and not for the line on which the run begins.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)] // a long loop: compile fully optimised straight away
        public bool RunUntilBreakpoint(ISet<(int SourceIndex, int Line)> breakpoints, Func<bool> isPauseRequested)
        {
            if (IsHalted) return false;
            if (!_steppingStarted) BeginStepping();

            var last = CurrentLocation;
            Chunk? rangeChunk = null;
            int rangeStart = 0, rangeEnd = 0, counter = 0;

            while (true)
            {
                var op = (OpCode)ReadByte();
                if (op == OpCode.Halt) return FinishAtHalt();
                Step(op);
                if (!AfterStep()) return false;

                if ((++counter & 255) == 0 && isPauseRequested()) return true;

                if (!ReferenceEquals(_currentChunk, rangeChunk) || _ip < rangeStart || _ip >= rangeEnd)
                {
                    var location = _currentChunk.GetLocationRange(_ip, out rangeStart, out rangeEnd);
                    rangeChunk = _currentChunk;
                    if (location != last)
                    {
                        last = location;
                        if (breakpoints.Contains(location)) return true;
                    }
                }
            }
        }

        /// <summary>Runs until the program end or a pause request ("Run to end"). true = stopped, false = ended.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public bool RunUntilEnd(Func<bool> isPauseRequested)
        {
            if (IsHalted) return false;
            if (!_steppingStarted) BeginStepping();

            int counter = 0;
            while (true)
            {
                var op = (OpCode)ReadByte();
                if (op == OpCode.Halt) return FinishAtHalt();
                Step(op);
                if (!AfterStep()) return false;
                if ((++counter & 255) == 0 && isPauseRequested()) return true;
            }
        }

        /// <summary>Executes instructions until either the current
        /// source line changes (target again at the same or a
        /// SHALLOWER call depth - "Step Over", so it does not run into deeper
        /// nested calls) or the program has ended. For
        /// the "Step" button in the editor.</summary>
        public bool StepLine()
        {
            int startLine = CurrentLine;
            int startDepth = _frames.Count;
            while (StepInstruction())
            {
                if (_frames.Count <= startDepth && CurrentLine != startLine)
                    return true;
            }
            return false;
        }

        /// <summary>Like StepLine ("Step Over"), but jumps INTO a call instead of
        /// skipping it ("Step Into") - stops at EVERY
        /// change of the current line OR call depth. A call of a
        /// native function (e.g. `print(...)`) has nothing to
        /// "jump into" there (no chunk/line of its own) - it then automatically behaves for the
        /// debugger like a normal step.</summary>
        public bool StepInto()
        {
            int startLine = CurrentLine;
            int startDepth = _frames.Count;
            while (StepInstruction())
            {
                if (CurrentLine != startLine || _frames.Count != startDepth)
                    return true;
            }
            return false;
        }

        /// <summary>Runs until the current function/method/the current
        /// lambda has been left (call depth falls below the
        /// initial depth) - "Step Out", sensibly complements Step Into/Over.</summary>
        public bool StepOut()
        {
            int startDepth = _frames.Count;
            if (startDepth == 0) return false; // already at top level, nothing to leave
            while (StepInstruction())
            {
                if (_frames.Count < startDepth)
                    return true;
            }
            return false;
        }

        /// <summary>Executes instructions until either a line from
        /// `breakpointLines` is reached (checked only on the CHANGE of line,
        /// so that a breakpoint on the line just left does not immediately
        /// trigger again) or the program has ended. For the
        /// "Continue" button in the editor.</summary>
        public bool Continue(ISet<int> breakpointLines)
        {
            int lastLine = CurrentLine;
            while (StepInstruction())
            {
                int line = CurrentLine;
                if (line != lastLine)
                {
                    lastLine = line;
                    if (breakpointLines.Contains(line)) return true;
                }
            }
            return false;
        }

        /// <summary>Immutable view of the current value stack - purely
        /// for inspection, no copy (values themselves are immutable
        /// structs anyway).</summary>
        public IReadOnlyList<Value> DebugStackSnapshot => new ArraySegment<Value>(_stack, 0, _sp);

        /// <summary>Current call depth (number of active CallFrames) - for a
        /// simple display "how deeply nested am I right now".</summary>
        public int DebugCallDepth => _frames.Count;

        /// <summary>Short description of the currently bound `this` - null
        /// (no text) if no `this` is bound at this point (e.g.
        /// top-level code or a lambda without an `on` binding).</summary>
        public string? DebugThisDescription => _currentThis switch
        {
            null => null,
            ObjectInstance oi => $"{oi.ClassName}-Instanz",
            Value v => $"this (per 'on' gebunden) = {v}",
            _ => _currentThis.ToString(),
        };

        /// <summary>Like DebugThisDescription, but as a real Value instead of only
        /// a text description - basis for the expandable field
        /// display in the editor (see MainWindow.RefreshDebugPanels/
        /// BuildVariableTreeItem). Null under the same conditions as
        /// DebugThisDescription.</summary>
        public Value? DebugThisValue => _currentThis switch
        {
            null => null,
            ObjectInstance oi => Value.MakeClassRef(oi),
            Value v => v,
            _ => null,
        };

        /// <summary>One level of the scope chain at the current execution
        /// position, for a structured scope view in the editor (each
        /// level separately instead of a single flat list - makes visible
        /// which variables belong to the CURRENTLY ACTIVE (innermost) block and
        /// which are "passed through" from an enclosing level).
        /// Depth 0 = the currently innermost scope (`_currentScope` itself).</summary>
        public sealed record ScopeLevel(int Depth, bool IsFunctionTopLevel, IReadOnlyList<(string Name, Value Value)> Variables);

        /// <summary>The complete scope chain of the current function/method/
        /// lambda, from the currently active (innermost) block up to
        /// its top-level scope (after that comes global, see DebugGlobals) -
        /// each level as an entry of its own, so that the editor can show them
        /// separately. Names reliable only for parameters of the top-
        /// level (see Chunk.DebugLocalNames comment), everything else
        /// as "(local N)".</summary>
        public IReadOnlyList<ScopeLevel> DebugScopeChain()
        {
            var result = new List<ScopeLevel>();
            var scope = _currentScope;
            int depth = 0;
            while (scope != null && !scope.IsGlobal)
            {
                bool isFunctionTopLevel = scope.Parent != null && scope.Parent.IsGlobal;
                var vars = new List<(string, Value)>();
                for (int slot = 0; slot < scope.SlotCount; slot++)
                {
                    string name = isFunctionTopLevel && _currentChunk.DebugLocalNames.TryGetValue((0, slot), out var n)
                        ? n
                        : $"(local {slot})";
                    vars.Add((name, scope.GetSlot(slot)));
                }
                result.Add(new ScopeLevel(depth, isFunctionTopLevel, vars));
                scope = scope.Parent;
                depth++;
            }
            return result;
        }

        /// <summary>Global variables - unlike function parameters
        /// there is (still) no name register for them (see Chunk.
        /// DebugLocalNames comment: there deliberately only the TOP-LEVEL
        /// scope of a FUNCTION is recorded, not the global top-level code) -
        /// they therefore appear completely as "(global N)".</summary>
        public IEnumerable<(string Name, Value Value)> DebugGlobals()
        {
            for (int slot = 0; slot < _globalScope.SlotCount; slot++)
                yield return ($"(global {slot})", _globalScope.GetSlot(slot));
        }

        /// <summary>Executes instructions until the call stack has fallen below
        /// <paramref name="targetFrameDepth"/> again - that is, until exactly the
        /// frame that was pushed immediately before this call (plus everything
        /// the executed code itself called further), has been torn down again via RETURN.
        /// This way "call this one proto and
        /// wait synchronously for its return" can be realised in the middle of a C# method call
        /// (for destructors, see RunDestructor), without having to nest
        /// the main loop itself recursively - recursion
        /// arises instead quite naturally via nested C# calls
        /// of RunDestructor itself, if a destructor in turn triggers further
        /// destructors.</summary>
        private void RunNestedUntil(int targetFrameDepth)
        {
            _nestedDepth++;
            try { RunNestedLoop(targetFrameDepth); }
            finally { _nestedDepth--; } // release again also on a C# exception (e.g. in performance mode without checks)
        }

        private void RunNestedLoop(int targetFrameDepth)
        {
            while (_frames.Count >= targetFrameDepth)
            {
                var op = (OpCode)ReadByte();
                if (op == OpCode.Halt)
                    throw new InvalidOperationException(
                        "Unexpected halt in nested execution (e.g. during a destructor call).");
                Step(op);

                // Like Run() (see there for the detailed reasoning) -
                // an unhandled exception sets _stopExecutionRequested
                // immediately, WITHOUT yet unwinding the frames/the stack itself
                // completely (that is only done by the next
                // CheckShutdownSignals call) - normally the
                // loop condition above (_frames.Count >= targetFrameDepth)
                // would catch that by itself after unwinding, but if
                // the frame state stands EXACTLY at targetFrameDepth when that
                // happens, the loop would otherwise wrongly keep running
                // and end with the same "Pop() on an empty stack" symptom
                // as in the bug report that led to this fix.
                if (_stopExecutionRequested) return;
            }
        }

        /// <summary>IDestructRunner: called by Scope.Release/ObjectInstance.Destroy
        /// when an object is destroyed by the ownership cascade.
        /// Executes the compiled destruct() body (if the class
        /// declares one), with 'this' = the object to be destroyed.</summary>
        public void RunDestructor(ObjectInstance instance)
        {
            // The destructors of the WHOLE class chain, derived class
            // first, then every base class (as in C#) - a base class that
            // holds resources (e.g. a file handle) thus cleans them up also for
            // derived classes that have no destruct() of their own.
            if (_stopExecutionRequested) return;
            for (var rc = instance.RtClass ?? ResolveClass(instance.ClassName); rc != null; rc = rc.Base)
            {
                if (rc.Destructor == null) continue;

                _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
                int targetDepth = _frames.Count;

                var scope = RentCallScope(SlotSlack, 0);
                _currentThis = instance;
                _currentScope = scope;
                _currentChunk = rc.Destructor.Chunk;
                _ip = 0;

                RunNestedUntil(targetDepth);

                // The final RETURN of the destructor body pushes its (here
                // meaningless) return value onto the value stack - unlike with
                // a normal Call/CallMethod/etc., there is however no
                // expression context here that picks it up. Without this pop the stack
                // would "grow" by one value on every destructor execution.
                PopNestedResult();
                if (_stopExecutionRequested) return; // a destructor has ended the VM (unhandled exception) - call nothing any more
            }
        }

        /// <summary>Executes ONE instruction. The most frequent ones (load/store, basic arithmetic, comparisons,
        /// jumps, scopes) are written out directly here, everything else goes to <see cref="Execute"/>.
        /// The reason for the separation: Execute is a huge method with very many local variables, and
        /// its stack frame is zeroed anew on EVERY call - that cost a multiple per instruction
        /// of the actual work. This method has almost no locals and stays cheap. Every case here
        /// behaves exactly like its counterpart in Execute; where a case does not apply (e.g. an object as the
        /// left operand with operator overloading, a scope with ownership), it falls through to Execute.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void Step(OpCode op)
        {
            switch (op)
            {
                case OpCode.LoadConst:
                    Push(_constants[ReadU16()]);
                    return;

                case OpCode.Pop:
                    _sp--;
                    return;

                case OpCode.Dup:
                    Push(_stack[_sp - 1]);
                    return;

                case OpCode.LoadLocal:
                {
                    int depth = ReadU16(); int slot = ReadU16();
                    if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
                    _stack[_sp] = _currentScope.GetAncestor(depth).SlotRef(slot);
                    _sp++;
                    return;
                }

                case OpCode.StoreLocal:
                {
                    int depth = ReadU16(); int slot = ReadU16();
                    _currentScope.GetAncestor(depth).SlotRef(slot) = _stack[_sp - 1];
                    return;
                }

                case OpCode.LoadGlobal:
                {
                    int slot = ReadU16();
                    if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
                    if (slot < _sharedCount) { _stack[_sp] = LoadSharedGlobal(slot); _sp++; return; } // Fire thread: the real globals
                    _stack[_sp] = _globalScope.SlotRef(slot);
                    _sp++;
                    return;
                }

                case OpCode.StoreGlobal:
                {
                    int slot = ReadU16();
                    if (slot < _sharedCount) { StoreSharedGlobal(slot, _stack[_sp - 1]); return; }
                    if (_ownerBroker != null) { StoreOwnerGlobal(slot, _stack[_sp - 1]); return; }
                    _globalScope.SlotRef(slot) = _stack[_sp - 1];
                    return;
                }

                case OpCode.DeclareLocal:
                    if (_ownerBroker != null && ReferenceEquals(_currentScope, _globalScope)) { DeclareOwnerGlobal(Pop()); return; }
                    _currentScope.DefineSlot(Pop());
                    return;

                // Binary operators: the left operand lies at _sp-2, the right at _sp-1; the result
                // replaces both. An object on the left (operator overloading) falls through to Execute.
                case OpCode.Add:
                    if (Value.TryAddInPlace(ref _stack[_sp - 2], in _stack[_sp - 1])) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class && _stack[_sp - 1].Kind != ValueKind.Class) { ReplaceTwoWith(Value.Add(_stack[_sp - 2], _stack[_sp - 1])); return; }
                    break;
                case OpCode.Sub:
                    if (Value.TrySubtractInPlace(ref _stack[_sp - 2], in _stack[_sp - 1])) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.Subtract(_stack[_sp - 2], _stack[_sp - 1])); return; }
                    break;
                case OpCode.Mul:
                    if (Value.TryMultiplyInPlace(ref _stack[_sp - 2], in _stack[_sp - 1])) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.Multiply(_stack[_sp - 2], _stack[_sp - 1])); return; }
                    break;
                case OpCode.Div:
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.Divide(_stack[_sp - 2], _stack[_sp - 1])); return; }
                    break;
                case OpCode.Mod:
                    if (Value.TryModuloInPlace(ref _stack[_sp - 2], in _stack[_sp - 1])) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.Modulo(_stack[_sp - 2], _stack[_sp - 1])); return; }
                    break;
                case OpCode.Eq:
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(Value.ValuesEqual(_stack[_sp - 2], _stack[_sp - 1]))); return; }
                    break;
                case OpCode.NotEq:
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(!Value.ValuesEqual(_stack[_sp - 2], _stack[_sp - 1]))); return; }
                    break;
                case OpCode.Lt:
                    if (Value.TryCompareInPlace(ref _stack[_sp - 2], in _stack[_sp - 1], 0)) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(Value.Compare(_stack[_sp - 2], _stack[_sp - 1]) < 0)); return; }
                    break;
                case OpCode.LtEq:
                    if (Value.TryCompareInPlace(ref _stack[_sp - 2], in _stack[_sp - 1], 1)) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(Value.Compare(_stack[_sp - 2], _stack[_sp - 1]) <= 0)); return; }
                    break;
                case OpCode.Gt:
                    if (Value.TryCompareInPlace(ref _stack[_sp - 2], in _stack[_sp - 1], 2)) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(Value.Compare(_stack[_sp - 2], _stack[_sp - 1]) > 0)); return; }
                    break;
                case OpCode.GtEq:
                    if (Value.TryCompareInPlace(ref _stack[_sp - 2], in _stack[_sp - 1], 3)) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(Value.Compare(_stack[_sp - 2], _stack[_sp - 1]) >= 0)); return; }
                    break;

                case OpCode.CallMethod:
                    OpCallMethod();
                    return;

                case OpCode.CallStaticMethod:
                    OpCallStaticMethod();
                    return;

                case OpCode.Call:
                    OpCall();
                    return;

                case OpCode.Return:
                    OpReturn();
                    return;

                case OpCode.GetField:
                    OpGetField();
                    return;

                case OpCode.SetField:
                    OpSetField();
                    return;

                case OpCode.LoadThis:
                    OpLoadThis();
                    return;

                case OpCode.SetFieldOnThis:
                    OpSetFieldOnThis();
                    return;

                case OpCode.NewObject:
                    OpNewObject();
                    return;

                case OpCode.GetStaticField:
                    OpGetStaticField();
                    return;

                case OpCode.SetStaticField:
                    OpSetStaticField();
                    return;

                case OpCode.ArrayGet:
                    OpArrayGet();
                    return;

                case OpCode.ArraySet:
                    OpArraySet();
                    return;

                case OpCode.IncDecIndex:
                    OpIncDecIndex();
                    return;

                case OpCode.NewArray:
                    OpNewArray();
                    return;

                case OpCode.MakeArrayLiteral:
                    OpMakeArrayLiteral();
                    return;

                case OpCode.CallNative:
                    OpCallNative();
                    return;

                case OpCode.MakeLambda:
                    OpMakeLambda();
                    return;

                case OpCode.MakeLambdaCapturing:
                    OpMakeLambdaCapturing();
                    return;

                case OpCode.Probe:
                    OpProbe();
                    return;

                case OpCode.SilenceMember:
                    OpSilenceMember();
                    return;

                case OpCode.SilenceValue:
                    OpSilenceValue();
                    return;

                case OpCode.CallBaseMethod:
                    OpCallBaseMethod();
                    return;

                case OpCode.NewObjectOwned:
                    OpNewObjectOwned();
                    return;

                case OpCode.ConstructBase:
                    OpConstructBase();
                    return;

                case OpCode.CallProtoWithThis:
                    OpCallProtoWithThis();
                    return;

                case OpCode.Neg:
                    _stack[_sp - 1] = Value.Negate(_stack[_sp - 1]);
                    return;
                case OpCode.LogicalNot:
                    _stack[_sp - 1] = Value.LogicalNot(_stack[_sp - 1]);
                    return;

                case OpCode.Jump:
                {
                    // A back jump (loop) is a safe point for shutdown signals (see PollSignals); `_ip` here still
                    // points to the operand, PollSignalsSlow calculates with the instruction boundary before it.
                    int target = _code[_ip] | (_code[_ip + 1] << 8);
                    if (target <= _ip && PollSignals()) return;
                    _ip = target;
                    return;
                }

                case OpCode.JumpIfFalse:
                {
                    int addr = ReadU16();
                    if (!Pop().AsBool()) _ip = addr;
                    return;
                }

                case OpCode.JumpIfFalsePeek:
                {
                    int addr = ReadU16();
                    if (!_stack[_sp - 1].AsBool()) _ip = addr;
                    return;
                }

                case OpCode.JumpIfTruePeek:
                {
                    int addr = ReadU16();
                    if (_stack[_sp - 1].AsBool()) _ip = addr;
                    return;
                }

                case OpCode.EnterScope:
                    _currentScope = RentScope(_currentScope);
                    return;

                case OpCode.ExitScope:
                {
                    var scope = _currentScope;
                    if (scope.HasOwned) { ExitScopeOwning(); return; } // Release can execute destructors - the detailed way
                    _currentScope = scope.Parent
                        ?? throw new InvalidOperationException("ExitScope called on the global scope.");
                    if (scope.CanRecycle) ReturnScopeToPool(scope);
                    return;
                }

                // ---- Verschmolzene Instruktionen (siehe OpCode.StoreLocalPop ff.) ----

                case OpCode.StoreLocalPop:
                {
                    int depth = ReadU16(); int slot = ReadU16();
                    _currentScope.GetAncestor(depth).SlotRef(slot) = _stack[--_sp];
                    return;
                }

                case OpCode.StoreGlobalPop:
                {
                    int slot = ReadU16();
                    var value = _stack[_sp - 1];
                    if (slot < _sharedCount) StoreSharedGlobal(slot, value);
                    else if (_ownerBroker != null) StoreOwnerGlobal(slot, value);
                    else _globalScope.SlotRef(slot) = value;
                    _sp--;
                    return;
                }

                case OpCode.JumpIfNotLt:
                case OpCode.JumpIfNotLtEq:
                case OpCode.JumpIfNotGt:
                case OpCode.JumpIfNotGtEq:
                {
                    int addr = ReadU16();
                    int kind = op - OpCode.JumpIfNotLt; // Lt, LtEq, Gt, GtEq liegen in dieser Reihenfolge hintereinander
                    bool result;
                    if (!Value.TryCompareFast(in _stack[_sp - 2], in _stack[_sp - 1], kind, out result))
                    {
                        // Fast path does not apply (different unit/kind, object with operator overloading ...): the ordinary comparison
                        ExecuteCompareSlow(kind switch { 0 => OpCode.Lt, 1 => OpCode.LtEq, 2 => OpCode.Gt, _ => OpCode.GtEq });
                        result = _stack[--_sp].AsBool();
                    }
                    else _sp -= 2;
                    if (!result) _ip = addr;
                    return;
                }

                case OpCode.JumpIfNotEq:
                case OpCode.JumpIfNotNotEq:
                {
                    int addr = ReadU16();
                    bool equal;
                    if (_stack[_sp - 2].Kind != ValueKind.Class)
                    {
                        equal = Value.ValuesEqual(_stack[_sp - 2], _stack[_sp - 1]);
                        _sp -= 2;
                    }
                    else
                    {
                        ExecuteCompareSlow(op == OpCode.JumpIfNotEq ? OpCode.Eq : OpCode.NotEq);
                        equal = _stack[--_sp].AsBool();
                        if (op == OpCode.JumpIfNotNotEq) equal = !equal; // the result was `a != b`; below `a == b` is expected
                    }
                    // JumpIfNotEq jumps if a == b does NOT hold; JumpIfNotNotEq if a != b does NOT hold (i.e. a == b)
                    if (op == OpCode.JumpIfNotEq ? !equal : equal) _ip = addr;
                    return;
                }

                case OpCode.ArithLocalConstPop:
                {
                    int depth = ReadU16(); int slot = ReadU16(); int constIdx = ReadU16(); bool subtract = ReadByte() != 0;
                    ref Value variable = ref _currentScope.GetAncestor(depth).SlotRef(slot);
                    if (subtract ? Value.TrySubtractInPlace(ref variable, in _constants[constIdx]) : Value.TryAddInPlace(ref variable, in _constants[constIdx]))
                        return;
                    ArithSlow(depth, slot, constIdx, subtract);
                    return;
                }

                case OpCode.ArithGlobalConstPop:
                {
                    int slot = ReadU16(); int constIdx = ReadU16(); bool subtract = ReadByte() != 0;
                    // Fire threads and a main program with running threads read/write globals via the broker: ordinary way
                    if (slot < _sharedCount || _ownerBroker != null) { ArithGlobalSlow(slot, constIdx, subtract); return; }
                    ref Value variable = ref _globalScope.SlotRef(slot);
                    if (subtract ? Value.TrySubtractInPlace(ref variable, in _constants[constIdx]) : Value.TryAddInPlace(ref variable, in _constants[constIdx]))
                        return;
                    ArithGlobalSlow(slot, constIdx, subtract);
                    return;
                }
            }

            Execute(op);
        }

        /// <summary>Slow path of the fused comparison jumps: the comparison `op` over the two topmost stack values, exactly like
        /// the ordinary instruction (also with operator overloading); the result then lies on top of the stack.</summary>
        private void ExecuteCompareSlow(OpCode op) => Step(op);

        /// <summary>Slow path of `x = x + c`/`x++` on a local: ordinary loading, calculating (strings, units, errors) and storing.</summary>
        private void ArithSlow(int depth, int slot, int constIdx, bool subtract)
        {
            Push(_currentScope.GetAncestor(depth).SlotRef(slot));
            Push(_constants[constIdx]);
            Step(subtract ? OpCode.Sub : OpCode.Add);
            _currentScope.GetAncestor(depth).SlotRef(slot) = _stack[--_sp];
        }

        /// <summary>Like <see cref="ArithSlow"/> for a global variable (also via the broker of the fire threads).</summary>
        private void ArithGlobalSlow(int slot, int constIdx, bool subtract)
        {
            if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
            if (slot < _sharedCount) _stack[_sp] = LoadSharedGlobal(slot); else _stack[_sp] = _globalScope.SlotRef(slot);
            _sp++;
            Push(_constants[constIdx]);
            Step(subtract ? OpCode.Sub : OpCode.Add);
            var value = _stack[_sp - 1];
            if (slot < _sharedCount) StoreSharedGlobal(slot, value);
            else if (_ownerBroker != null) StoreOwnerGlobal(slot, value);
            else _globalScope.SlotRef(slot) = value;
            _sp--;
        }

        /// <summary>Leaves the current scope and destroys the objects that belong to it (destructors run nested).</summary>
        private void ExitScopeOwning()
        {
            var scope = _currentScope;
            scope.Release(this);
            _currentScope = scope.Parent
                ?? throw new InvalidOperationException("ExitScope called on the global scope.");
            if (scope.CanRecycle) ReturnScopeToPool(scope);
        }

        /// <summary>Replaces the topmost TWO stack values by `result` (result of a binary operation).</summary>
        private void ReplaceTwoWith(Value result)
        {
            _sp--;
            _stack[_sp - 1] = result;
        }

        private void OpIncDecIndex()
        {
        {
            bool isIncrement = ReadByte() != 0;
            bool isPrefix = ReadByte() != 0;
            var indexVal = Pop();
            var target = Pop();
            long idx = indexVal.AsInt();
            if (IsDestroyedLeaf(target)) { ThrowDestroyed(target); return; }

            if (target.Kind == ValueKind.Array)
            {
                var arr = target.AsArray();
                if (arr.IsShared && _threadBroker != null && _sectionDepth == 0)
                    throw new InvalidOperationException(
                        "'++'/'--' on an array element of the globals is only possible in a fire thread inside 'sync global { ... }' (reading and writing must happen together).");
                if (!arr.TryGet(idx, out var oldVal))
                {
                    ThrowIndexOutOfBounds(idx, arr.Length);
                    return;
                }
                var newVal = isIncrement ? Value.Add(oldVal, Value.MakeInt(1)) : Value.Subtract(oldVal, Value.MakeInt(1));
                if (arr.IsShared) TrySetSharedElement(arr, idx, newVal); else arr.TrySet(idx, newVal);
                Push(isPrefix ? newVal : oldVal);
            }
            else if (target.Kind == ValueKind.Buffer)
            {
                var buf = target.AsBuffer();
                if (!buf.TryGet(idx, out byte oldByte))
                {
                    ThrowIndexOutOfBounds(idx, buf.Length);
                    return;
                }
                byte newByte = (byte)(isIncrement ? oldByte + 1 : oldByte - 1);
                buf.TrySet(idx, newByte);
                Push(Value.MakeInt(isPrefix ? newByte : oldByte, width: NumericWidth.W8));
            }
            else
            {
                throw new InvalidOperationException(
                    $"'++'/'--' on an index target expects an array or a byte buffer, not {target.Kind}.");
            }
            return;
        }
        }

        private void OpCallNative()
        {
        {
            int nativeIdx = ReadU16();
            int argCount = ReadByte();
            var args = new Value[argCount];
            for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
            if (CallNativeGuarded(nativeIdx, args, out Value nativeResult))
                Push(nativeResult);
            PollSignalsAfterOp();
            return;
        }
        }

        private void OpMakeLambda()
        {
        {
            int protoIdx = ReadU16();
            bool hasOnTarget = ReadByte() != 0;
            var proto = _currentChunk.Functions[protoIdx];
            object? onTarget = hasOnTarget ? BoxValueForOnTarget(Pop()) : null;
            var lambdaValue = new LambdaValue(proto, onTarget);
            Push(Value.MakeLambda(lambdaValue));
            return;
        }
        }

        private void OpMakeLambdaCapturing()
        {
            int protoIdx = ReadU16();
            bool hasOnTarget = ReadByte() != 0;
            int captureCount = ReadByte();
            var proto = _currentChunk.Functions[protoIdx];
            object? onTarget = hasOnTarget ? BoxValueForOnTarget(Pop()) : null;
            var captures = new Value[captureCount];
            for (int i = captureCount - 1; i >= 0; i--) captures[i] = Pop();
            Push(Value.MakeLambda(new LambdaValue(proto, onTarget, null, captures)));
        }

        // -----------------------------------------------------------
        // Inline caches of the call sites (see Bytecode.SiteCache)
        // -----------------------------------------------------------

        /// <summary>Space for local variables that a call scope gets
        /// right away beyond the parameters (saves growing the slot array on the first `var`s in the body).</summary>
        private const int SlotSlack = 4;

        /// <summary>Switches into the call of `proto`: the topmost `argCount` stack values are taken directly as
        /// parameter slots of the new scope and (together with the receiver/callee below them, if
        /// `dropBelow`) removed from the stack. Only for calls with EXACTLY the matching argument count (no default value needed).</summary>
        private void EnterCall(FunctionProto proto, int argCount, bool dropBelow, object? newThis, ObjectInstance? constructed = null, long copyMask = 0, Value[]? captures = null)
        {
            int captureCount = captures?.Length ?? 0;
            int paramCount = argCount + captureCount;
            var scope = RentCallScope(paramCount + SlotSlack, paramCount);
            var slots = scope.SlotArray;
            Array.Copy(_stack, _sp - argCount, slots, 0, argCount);
            if (captureCount != 0) Array.Copy(captures!, 0, slots, argCount, captureCount); // Lambda captures directly behind the parameters
            _sp -= argCount + (dropBelow ? 1 : 0);

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, constructed));
            _currentThis = newThis;
            _currentScope = scope;
            if (copyMask != 0) ApplyCopyMask(_currentScope, copyMask, proto.RefMask);
            _currentChunk = proto.Chunk;
            _ip = 0;
        }

        // -----------------------------------------------------------
        // `flat x` / `copy x` als Argument (SPEC 2.4, Opcode CopyArgs)
        // -----------------------------------------------------------

        /// <summary>The copy mask that the prefix `CopyArgs` has stored for the call opcode - read here AND
        /// cleared (every call opcode fetches it right at the start, so that it never lands at a later call).</summary>
        private long TakeCopyMask()
        {
            long mask = _copyArgMask;
            _copyArgMask = 0;
            return mask;
        }

        /// <summary>Copies the marked parameters of a freshly built call scope: the copy belongs to this scope
        /// (is thus destroyed on leaving the function, unless the function returns it or passes it on via TakeTo).</summary>
        private void ApplyCopyMask(Scope scope, long mask, uint calleeRefMask)
        {
            if (mask == 0) return;
            for (int i = 0; i < 16; i++)
            {
                int bits = (int)(mask >> (4 * i)) & 15;
                if (bits == 0) continue;
                if (bits == 4)
                {
                    // The argument is directly the return value of a call (`f(g())`): its ownership goes into the called function, not to the caller
                    // (SPEC 2.1) - only if the value lay freshly with the caller, not if g returned something that belongs to others.
                    if (_frames.Count > 0) AdoptReturnedArgument(scope.SlotRef(i), _frames.Peek().ReturnScope, scope);
                    continue;
                }
                if (bits == 5)
                {
                    // `f(take x)` (SPEC 2.2): the value belongs to the call from now on - unconditionally, even if it belonged to someone else before
                    TakeArgument(scope.SlotRef(i), scope);
                    continue;
                }
                if (bits == 3)
                {
                    // The caller has passed the address (name + argument count know a `ref` parameter, SPEC 5.4.2): a `ref` parameter keeps it,
                    // an ordinary one gets the value (base types and strings as a copy, objects and arrays as a reference).
                    if ((calleeRefMask >> i & 1) == 0) scope.SlotRef(i) = ReadRefArgument(scope.SlotRef(i));
                    continue;
                }
                scope.SlotRef(i) = ObjectCloner.Clone(scope.SlotRef(i), scope, deep: bits == 2);
            }
        }

        /// <summary>`f(take x)`: the object/array/buffer belongs to the call of <paramref name="calleeScope"/> (dies after its locals, like a passed-on call result).</summary>
        private void TakeArgument(Value arg, Scope calleeScope)
        {
            switch (arg.Kind)
            {
                case ValueKind.Class: ((ObjectInstance)arg.AsObjectRef()).ReparentToArgument(calleeScope); break;   // (a destroyed object stays; the check ran before the call)
                case ValueKind.Array when !arg.AsArray().IsDestroyed: LeafOwnership.Reparent(arg.AsArray(), calleeScope); break;
                case ValueKind.Buffer when !arg.AsBuffer().IsDestroyed: LeafOwnership.Reparent(arg.AsBuffer(), calleeScope); break;
            }
        }

        /// <summary>`take x` on something destroyed: DestroyedException (true: it was thrown).</summary>
        private bool ThrowIfDeadForTake(Value v)
        {
            if (v.Kind == ValueKind.Class && v.AsObjectRef() is ObjectInstance obj && IsDeadObject(obj)) { ThrowDestroyedObject(obj); return true; }
            if (IsDestroyedLeaf(v)) { ThrowDestroyed(v); return true; }
            return false;
        }

        /// <summary>An object/array/buffer that belongs to the caller's scope (freshly returned) belongs to the called function from now on.</summary>
        private static void AdoptReturnedArgument(Value arg, Scope callerScope, Scope calleeScope)
        {
            switch (arg.Kind)
            {
                case ValueKind.Class:
                {
                    var obj = (ObjectInstance)arg.AsObjectRef();
                    if (!obj.IsDestroyed && ReferenceEquals(obj.Owner, callerScope)) obj.ReparentToArgument(calleeScope);
                    break;
                }
                case ValueKind.Array:
                case ValueKind.Buffer:
                {
                    var leaf = arg.Kind == ValueKind.Array ? (IOwnedLeaf)arg.AsArray() : arg.AsBuffer();
                    if (!leaf.IsDestroyed && ReferenceEquals(leaf.LeafOwner, callerScope)) LeafOwnership.Reparent(leaf, calleeScope);
                    break;
                }
            }
        }

        /// <summary>For calls WITHOUT a function scope (built-in methods, actor messages): the copy belongs to the
        /// current scope, as with an ordinary copy.</summary>
        private void ApplyCopyMaskToArgs(Value[] args, long mask)
        {
            for (int i = 0; i < args.Length && i < 16; i++)
            {
                int bits = (int)(mask >> (4 * i)) & 15;
                if (bits == 5) OwnershipWalk.TakeValue(args[i], _currentScope, this);   // built-in functions know no call scope: the value belongs to the current scope
                else if (bits == 3) args[i] = ReadRefArgument(args[i]);   // built-in functions and messages know no `ref`: the value
                else if (bits != 0) args[i] = ObjectCloner.Clone(args[i], _currentScope, deep: bits == 2);
            }
        }

        private void StoreSite(int site, SiteCache entry) => _chunk.EnsureSiteCaches()[site] = entry;

        private SiteCache? LookupSite(int site) => _chunk.SiteCaches?[site];

        private void OpCall()
        {
        {
            if (PollSignals()) return;
            int argCount = ReadByte();
            long copyMask = TakeCopyMask();

            // Fast path: a lambda with exactly this parameter count (no default value needed).
            if (_stack[_sp - 1 - argCount] is { Kind: ValueKind.Lambda } fastCallee
                && fastCallee.AsLambda() is LambdaValue fastLambda
                && fastLambda.Proto.ParamCount == argCount)
            {
                EnterCall(fastLambda.Proto, argCount, dropBelow: true, fastLambda.OnTarget, copyMask: copyMask, captures: fastLambda.Captures);
                return;
            }

            OpCallSlow(argCount, copyMask);
            return;
        }
        }

        private void OpCallSlow(int argCount, long copyMask)
        {
        {
            var args = new Value[argCount];
            for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
            var calleeVal = Pop();

            if (calleeVal.Kind != ValueKind.Lambda)
                throw new InvalidOperationException(
                    $"Call of a value of type {calleeVal.Kind} which is not a lambda.");

            var lambda = (LambdaValue)calleeVal.AsLambda();
            CheckArity(lambda.Proto, args.Length);
            args = FillDefaultArgs(lambda.Proto, args, lambda.OnTarget);

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));

            var funcScope = new Scope(_globalScope);
            foreach (var a in args) funcScope.DefineSlot(a);
            if (lambda.Captures != null) foreach (var c in lambda.Captures) funcScope.DefineSlot(c);
            ApplyCopyMask(funcScope, copyMask, lambda.Proto.RefMask);

            _currentThis = lambda.OnTarget;
            _currentScope = funcScope;
            _currentChunk = lambda.Proto.Chunk;
            _ip = 0;
            return;
        }
        }

        // Completion kinds that a `finally` block finds on top of the operand stack (payload, kind) - see OpCode.EndFinally
        private const int FinallyNormal = 0, FinallyThrow = 1, FinallyReturn = 2, FinallyJump = 3, FinallyNestedReturn = 4;

        private void OpReturn() => DoReturn(Pop());

        /// <summary>Does `candidate` belong to the scopes of the running call (from the current scope upwards up to and including the
        /// function scope whose parent is the global scope)?</summary>
        private bool OwnsWithinCall(Scope candidate)
        {
            for (var scope = _currentScope; scope != null; scope = scope.Parent)
            {
                if (ReferenceEquals(scope, candidate)) return true;
                if (scope.Parent == null || scope.Parent.IsGlobal) return false; // the function scope was the last
            }
            return false;
        }

        /// <summary>On `return`, leaves ALL scopes of the call (innermost first up to the function scope): the objects that belong to them
        /// are destroyed. Without that, the objects of the surrounding blocks (`if`/`for`/`try` around the `return`) would lie around forever.</summary>
        private void ReleaseCallScopes()
        {
            var scope = _currentScope;
            while (true)
            {
                var parent = scope.Parent;
                scope.Release(this);
                if (scope.CanRecycle) ReturnScopeToPool(scope);
                if (parent == null || parent.IsGlobal) return;
                scope = parent;
            }
        }

        // -----------------------------------------------------------
        // Pool of scopes (see Scope.CanRecycle): blocks, loop passes and calls would otherwise create a scope
        // including a slot array anew on every entry. The pool belongs to the VM (every VM runs on exactly one thread) and is small - deep recursion
        // creates beyond it simply new scopes, which the GC collects again.
        // -----------------------------------------------------------
        private const int ScopePoolMax = 256;
        private readonly Scope[] _scopePool = new Scope[ScopePoolMax];
        private int _scopePoolCount;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private Scope RentScope(Scope parent)
        {
            if (_scopePoolCount == 0) return Scope.CreatePooled(parent);
            var scope = _scopePool[--_scopePoolCount];
            scope.Reinit(parent);
            return scope;
        }

        /// <summary>A scope for a call with space for `capacity` slots, the first `paramCount` occupied (the caller copies the parameters into <see cref="Scope.SlotArray"/>).</summary>
        private Scope RentCallScope(int capacity, int paramCount)
        {
            Scope scope;
            if (_scopePoolCount == 0) scope = Scope.CreatePooled(null);
            else scope = _scopePool[--_scopePoolCount];
            scope.ReinitForCall(_globalScope, paramCount, capacity);
            return scope;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private void ReturnScopeToPool(Scope scope)
        {
            scope.Recycle();
            if (_scopePoolCount < ScopePoolMax) _scopePool[_scopePoolCount++] = scope;
        }

        /// <summary>`return` (SPEC 2.3): if the returned value (object, array, buffer) belongs to one of the scopes that are about to be left, it goes to the calling scope - and
        /// with it everything that hangs on it and likewise belongs to these scopes (recursively, each node once): it moves to the object that points to it. Without that
        /// the elements of a returned list would die with the scope that created them.</summary>
        private void MoveReturned(Value retVal, Func<Scope, bool> isLocalScope)
        {
            var target = _frames.Peek().ReturnScope;
            switch (retVal.Kind)
            {
                case ValueKind.Class:
                {
                    var obj = (ObjectInstance)retVal.AsObjectRef();
                    if (obj.IsDestroyed || !OwnershipWalk.IsLocal(obj.Owner, isLocalScope)) return;
                    obj.ReparentTo(target);
                    break;
                }
                case ValueKind.Array:
                case ValueKind.Buffer:
                {
                    var leaf = LeafOf(retVal)!;
                    if (leaf.IsDestroyed || !OwnershipWalk.IsLocal(leaf.LeafOwner, isLocalScope)) return;
                    LeafOwnership.Reparent(leaf, target);
                    break;
                }
                default:
                    return;
            }
            OwnershipWalk.MoveReachable(retVal, Takes.Locals, isLocalScope, target);
        }

        /// <summary>Ends the current function with `retVal`. If a `try` with `finally` of this frame is still open (also a `catch` block that
        /// still belongs to such a `try`), it does not return, but first executes its `finally` (completion "return"): its `EndFinally`
        /// calls this method again, until no `finally` is open any more. Handlers without `finally` are simply unregistered.</summary>
        private void DoReturn(Value retVal)
        {
        {
            if (_handlers.Count > 0)
            {
                int handlerFloor = _callbackBoundaries.Count > 0 ? _callbackBoundaries.Peek().HandlerFloor : 0;
                while (_handlers.Count > handlerFloor && _handlers[^1].FrameDepthAtEntry >= _frames.Count)
                {
                    var handler = _handlers[^1];
                    _handlers.RemoveAt(_handlers.Count - 1);
                    if (handler.Template.FinallyAddr is not int finallyAddr) continue;

                    // A returned value that belongs to one of the scopes that are about to be left goes, with everything that hangs on it, to the caller (as below at `Return`)
                    if (_frames.Count > 0)
                    {
                        var leavingTo = handler.TargetScope;
                        MoveReturned(retVal, ownerScope =>
                        {
                            for (var sc = _currentScope; sc != null && !ReferenceEquals(sc, leavingTo); sc = sc.Parent)
                                if (ReferenceEquals(sc, ownerScope)) return true;
                            return false;
                        });
                    }
                    UnwindTo(handler.FrameDepthAtEntry, handler.TargetScope);
                    if (_sp > handler.StackPointer) _sp = handler.StackPointer;
                    Push(retVal);
                    Push(Value.MakeInt(FinallyReturn));
                    _currentChunk = handler.Chunk;
                    _ip = finallyAddr;
                    return;
                }
            }

            // SPEC 2.3: If an object instance is returned whose
            // owner is the scope just left, the ownership
            // passes to the CALLING scope (not simply '.Parent' -
            // function/method scopes always have global as their parent,
            // that would not be the desired "one level up" here).
            // Without that, the returned object would be destroyed along with it
            // by the immediately following Release() of its own scope.
            // This applies to EVERY scope of this call (innermost block up to function scope): a `return` in the middle of nested
            // blocks leaves them all at once.
            if (_frames.Count > 0) MoveReturned(retVal, OwnsWithinCall);

            ReleaseCallScopes();

            var frame = _frames.Pop();
            _currentChunk = frame.ReturnChunk;
            _ip = frame.ReturnIp;
            _currentScope = frame.ReturnScope;
            _currentThis = frame.ReturnThis;

            Push(frame.ConstructedInstance != null
                ? Value.MakeClassRef(frame.ConstructedInstance)
                : retVal);
            return;
        }
        }

        private void OpNewObject()
        {
            if (PollSignals()) return;
            int site = _ip - 1;
            int classNameIdx = ReadU16();
            int argCount = ReadByte();
            long copyMask = TakeCopyMask();

            // Fast path (inline cache): class and constructor of this site are known, access and
            // argument check already passed.
            if (LookupSite(site) is { Class: { } cachedClass, Proto: { } cachedCtor })
            {
                var created = new ObjectInstance(cachedClass.Name, _currentScope, cachedClass);
                if (cachedClass.IsActor) created.Mailbox = new ActorMailbox();
                EnterCall(cachedCtor, argCount, dropBelow: false, newThis: created, constructed: created, copyMask: copyMask);
                return;
            }
            OpNewObjectSlow(site, classNameIdx, argCount, copyMask);
        }

        private void OpNewObjectSlow(int site, int classNameIdx, int argCount, long copyMask)
        {
        {
            var args = new Value[argCount];
            for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();

            var rc = ResolveClass(_constants[classNameIdx].AsString());
            var ctorProto = rc.FindConstructor(args.Length)
                ?? throw new InvalidOperationException(DescribeConstructorNotFound(rc, args.Length));
            if (ExecutionMode != VmExecutionMode.Performance
                && !IsMemberAccessAllowed(rc, ctorProto.Access ?? AccessModifier.Public))
            {
                ThrowAccessDenied(
                    $"Constructor of '{rc.Name}' is {DescribeAccess(ctorProto.Access ?? AccessModifier.Public)} and cannot be called from here.");
                return;
            }

            if (ctorProto.ParamCount == argCount)
                StoreSite(site, new SiteCache(rc, ctorProto, 0));
            var instance = new ObjectInstance(rc.Name, _currentScope, rc);
            if (rc.IsActor) instance.Mailbox = new ActorMailbox();
            args = FillDefaultArgs(ctorProto, args, instance);
            BeginConstruction(instance, ctorProto, args, copyMask);
            return;
        }
        }

        private void OpNewObjectOwned()
        {
        {
            int classNameIdx = ReadU16();
            int argCount = ReadByte();
            long copyMask = TakeCopyMask();
            var args = new Value[argCount];
            for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
            var owner = RequireObjectInstance(Pop(), "Object creation with owner");

            var rc = ResolveClass(_constants[classNameIdx].AsString());
            var ctorProto = rc.FindConstructor(args.Length)
                ?? throw new InvalidOperationException(DescribeConstructorNotFound(rc, args.Length));
            if (ExecutionMode != VmExecutionMode.Performance
                && !IsMemberAccessAllowed(rc, ctorProto.Access ?? AccessModifier.Public))
            {
                ThrowAccessDenied(
                    $"Constructor of '{rc.Name}' is {DescribeAccess(ctorProto.Access ?? AccessModifier.Public)} and cannot be called from here.");
                return;
            }

            var instance = new ObjectInstance(rc.Name, owner, rc);
            if (rc.IsActor) instance.Mailbox = new ActorMailbox();
            args = FillDefaultArgs(ctorProto, args, instance);
            BeginConstruction(instance, ctorProto, args, copyMask);
            return;
        }
        }

        private void OpConstructBase()
        {
        {
            int classNameIdx = ReadU16();
            int argCount = ReadByte();
            long copyMask = TakeCopyMask();
            var args = new Value[argCount];
            for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();

            var rc = ResolveClass(_constants[classNameIdx].AsString());
            var ctorProto = rc.FindConstructor(args.Length)
                ?? throw new InvalidOperationException(DescribeConstructorNotFound(rc, args.Length));
            args = FillDefaultArgs(ctorProto, args, _currentThis);

            // The same instance continues to be constructed - 'this' stays
            // unchanged (is nevertheless written into the frame, so that
            // RETURN can restore uniformly).
            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));

            var baseScope = new Scope(_globalScope);
            foreach (var a in args) baseScope.DefineSlot(a);
            ApplyCopyMask(baseScope, copyMask, ctorProto.RefMask);

            _currentScope = baseScope;
            _currentChunk = ctorProto.Chunk;
            _ip = 0;
            return;
        }
        }

        private void OpGetField()
        {
        {
            int site = _ip - 1;
            int fieldNameIdx = ReadU16();

            // Schnellpfad (Inline-Cache): Objekt derselben Klasse wie zuvor, Feld liegt an bekanntem Index.
            if (_stack[_sp - 1] is { Kind: ValueKind.Class } cachedTarget
                && LookupSite(site) is { } fieldEntry
                && cachedTarget.AsObjectRef() is ObjectInstance cachedObj
                && ReferenceEquals(cachedObj.RtClass, fieldEntry.Class)
                && cachedObj.ThreadLock == null)
            {
                _stack[_sp - 1] = cachedObj.Fields.GetAt(fieldEntry.FieldIndex);
                return;
            }

            OpGetFieldSlow(site, fieldNameIdx);
            return;
        }
        }

        private void OpGetFieldSlow(int site, int fieldNameIdx) => GetFieldSlowCore(_constants[fieldNameIdx].AsString(), site);

        /// <summary>Field access `target.fieldName` (value ON TOP of the stack) including access check and property getter - the slow path of
        /// GetField, also for reflection (site &lt; 0: no inline cache). true if the result lies on the stack; false if
        /// instead an exception was redirected to a handler.</summary>
        private bool GetFieldSlowCore(string fieldName, int site)
        {
        {
            var target = Pop();

            // `Length` is the spelling of the properties (as with `string`),
            // `length` the older one - both equivalent for array/buffer/string.
            if (target.Kind == ValueKind.String)
            {
                if (fieldName is "Length" or "length")
                {
                    Push(Value.MakeInt(target.AsString().Length));
                    return true;
                }
                throw new InvalidOperationException($"Strings have no field '{fieldName}' (only 'Length').");
            }

            if (target.Kind == ValueKind.Array)
            {
                if (fieldName is "Length" or "length")
                {
                    if (IsDestroyedLeaf(target)) { ThrowDestroyed(target); return false; }
                    Push(Value.MakeInt(target.AsArray().Length));
                    return true;
                }
                throw new InvalidOperationException($"Arrays have no field '{fieldName}' (only 'Length').");
            }

            if (target.Kind == ValueKind.Buffer)
            {
                var buf = target.AsBuffer();
                if (fieldName is "Length" or "length")
                {
                    if (IsDestroyedLeaf(target)) { ThrowDestroyed(target); return false; }
                    Push(Value.MakeInt(buf.Length));
                    return true;
                }
                if (fieldName == "littleEndian")
                {
                    Push(Value.MakeBool(buf.Order == ByteOrder.Little));
                    return true;
                }
                throw new InvalidOperationException(
                    $"Byte buffers have no field '{fieldName}' (only 'Length', 'littleEndian').");
            }

            var obj = RequireObjectInstance(target, "Feldzugriff");
            if (IsDeadObject(obj)) { ThrowDestroyedObject(obj); return false; }
            if (obj.TryGetFieldLocked(fieldName, out var val))
            {
                if (_threadBroker != null && obj.InGlobalsDomain) MarkShared(val); // an array of the shared area
                if (ExecutionMode != VmExecutionMode.Performance && obj.RtClass != null)
                {
                    var fieldAccess = obj.RtClass.FindFieldAccess(fieldName);
                    if (fieldAccess is (var declaringRcGet, var accessGet) && !IsMemberAccessAllowed(declaringRcGet, accessGet))
                    {
                        ThrowAccessDenied(
                            $"Field '{fieldName}' of '{declaringRcGet.Name}' is {DescribeAccess(accessGet)} " +
                            "and cannot be accessed from here.");
                        return false;
                    }
                }
                if (site >= 0 && obj.RtClass != null && obj.ThreadLock == null && obj.RtClass.FieldIndex.TryGetValue(fieldName, out int getIndex))
                    StoreSite(site, new SiteCache(obj.RtClass, null, getIndex));
                Push(val);
                return true;
            }

            // No field of this name - try property getters
            // (naming convention 'get_'+name, see Ast.PropertyDecl).
            // Properties deliberately NEVER have a Fields
            // entry of their own, so they always end up here.
            var rcGet = ResolveClass(obj.ClassName);
            if (rcGet.FindMethod("get_" + fieldName, 0) != null)
            {
                var result = CallMethodNested(obj, "get_" + fieldName, Array.Empty<Value>());
                if (result != null) Push(result.Value);
                return result != null;
            }

            throw new InvalidOperationException(
                $"Field '{fieldName}' does not exist on an instance of '{obj.ClassName}' " +
                $"(and there is no 'get_{fieldName}' property either).");
        }
        }

        private void OpSetField()
        {
        {
            int site = _ip - 1;
            int fieldNameIdx = ReadU16();

            // Schnellpfad (Inline-Cache, siehe OpGetField): Objekt derselben Klasse wie zuvor.
            if (_stack[_sp - 2] is { Kind: ValueKind.Class } cachedTarget
                && LookupSite(site) is { } fieldEntry
                && cachedTarget.AsObjectRef() is ObjectInstance cachedObj
                && ReferenceEquals(cachedObj.RtClass, fieldEntry.Class)
                && cachedObj.AccessGuard == null)
            {
                var assigned = _stack[_sp - 1];
                cachedObj.Fields.SetAt(fieldEntry.FieldIndex, assigned);
                _sp--;
                _stack[_sp - 1] = assigned;
                return;
            }

            OpSetFieldSlow(site, fieldNameIdx);
            return;
        }
        }

        private void OpSetFieldSlow(int site, int fieldNameIdx) => SetFieldSlow(_constants[fieldNameIdx].AsString(), site);

        /// <summary>`obj.fieldName = value` (stack: obj, value) - slow path of SetField, also for reflection (site &lt; 0: no
        /// inline cache). true if the assigned value lies on the stack; false for an exception redirected to a handler.</summary>
        private bool SetFieldSlow(string fieldName, int site)
        {
            // Does the object have probes on this member: `changing` handlers, write, `changed` handlers (see SetFieldProbed)
            if (_stack[_sp - 2] is { Kind: ValueKind.Class } probeTarget
                && ((ObjectInstance)probeTarget.AsObjectRef()).Probes is { } probes && probes.Affects(fieldName))
                return SetFieldProbed(fieldName, probes);
            return SetFieldSlowSections(fieldName, site);
        }

        private bool SetFieldSlowSections(string fieldName, int site)
        {
            // A fire thread changes an object of the shared area only in a section (see GlobalsBroker).
            if (_threadBroker != null && _sectionDepth == 0
                && _stack[_sp - 2] is { Kind: ValueKind.Class } sectionTarget
                && ((ObjectInstance)sectionTarget.AsObjectRef()).InGlobalsDomain)
            {
                EnterGlobalsSection();
                try { return SetFieldSlowCore(fieldName, site); }
                finally { ExitGlobalsSection(); }
            }
            return SetFieldSlowCore(fieldName, site);
        }

        private bool SetFieldSlowCore(string fieldName, int site)
        {
        {
            var value = Pop();
            var obj = RequireObjectInstance(Pop(), "Feldzuweisung");
            if (IsDeadObject(obj)) { ThrowDestroyedObject(obj); return false; }

            if (obj.HasFieldLocked(fieldName))
            {
                bool hasUnitRule = false;
                if (ExecutionMode != VmExecutionMode.Performance && obj.RtClass != null)
                {
                    var fieldAccess = obj.RtClass.FindFieldAccess(fieldName);
                    if (fieldAccess is (var declaringRcSet, var accessSet) && !IsMemberAccessAllowed(declaringRcSet, accessSet))
                    {
                        ThrowAccessDenied(
                            $"Field '{fieldName}' of '{declaringRcSet.Name}' is {DescribeAccess(accessSet)} " +
                            "and cannot be accessed from here.");
                        return false;
                    }

                    // SPEC "Unit declarations" - field access is
                    // fundamentally dynamic (the actual class
                    // is only known here, at runtime), therefore
                    // unlike with local/global variables NO
                    // compile-time check is possible (see Compiler.
                    // CompileClassBody comment) - the check itself
                    // is however identical in content to OpCode.CheckUnit.
                    string? requiredUnitName = obj.RtClass.FindFieldRequiredUnit(fieldName);
                    if (requiredUnitName != null)
                    {
                        hasUnitRule = true; // every assignment must check the unit - do not cache
                        var requiredUnit = Values.Unit.Parse(requiredUnitName);
                        var actualUnit = value.Unit ?? Values.Unit.Unitless;
                        if (!actualUnit.Equals(requiredUnit))
                        {
                            ThrowUnitMismatch(requiredUnitName, actualUnit);
                            return false;
                        }
                    }
                }
                if (site >= 0 && !hasUnitRule && obj.RtClass != null && obj.AccessGuard == null
                    && obj.RtClass.FieldIndex.TryGetValue(fieldName, out int setIndex))
                    StoreSite(site, new SiteCache(obj.RtClass, null, setIndex));
                obj.SetFieldLocked(fieldName, value);
                Push(value);
                return true;
            }

            // No existing field of this name - try property setter
            // (naming convention 'set_'+name).
            var rcSet = ResolveClass(obj.ClassName);
            if (rcSet.FindMethod("set_" + fieldName, 1) != null)
            {
                var result = CallMethodNested(obj, "set_" + fieldName, new[] { value });
                // Return value of the setter itself unused - an
                // assignment always evaluates to the ASSIGNED value,
                // not to what the setter returns. null ==
                // redirected via exception (see CallMethodNested
                // documentation) - then do NOT push.
                if (result != null) Push(value);
                return result != null;
            }

            // A property of the same name WITH a getter, but WITHOUT a setter,
            // exists - that is an error, NOT "create a new field"
            // (otherwise the property would from here on be unnoticedly shadowed by a
            // field of the same name, also for future
            // read accesses via GetField, which checks fields before properties
            // ).
            if (rcSet.FindMethod("get_" + fieldName, 0) != null)
                throw new InvalidOperationException(
                    $"Property '{fieldName}' on '{obj.ClassName}' has no setter (only 'get').");

            // Neither existing field nor property - as before:
            // simply create a new field (dynamic language, no
            // up-front declaration obligation for fields).
            obj.SetFieldLocked(fieldName, value);
            Push(value);
            return true;
        }
        }

        private void OpLoadThis()
        {
            Push(_currentThis switch
            {
                null => throw new InvalidOperationException("'this' is not bound at this point."),
                ObjectInstance oi => Value.MakeClassRef(oi),
                Value v => v,
                _ => throw new InvalidOperationException("Unexpected 'this' value."),
            });
            return;
        }

        private void OpSetFieldOnThis()
        {
            int site = _ip - 1;
            int fieldNameIdx = ReadU16();

            // Schnellpfad (Inline-Cache, siehe OpSetField): `this` hat dieselbe Klasse wie zuvor.
            if (_currentThis is ObjectInstance fastThis
                && LookupSite(site) is { } thisEntry
                && ReferenceEquals(fastThis.RtClass, thisEntry.Class)
                && fastThis.ThreadLock == null)
            {
                fastThis.Fields.SetAt(thisEntry.FieldIndex, _stack[--_sp]);
                return;
            }
            OpSetFieldOnThisSlow(site, fieldNameIdx);
        }

        private void OpSetFieldOnThisSlow(int site, int fieldNameIdx)
        {
        {
            string fieldName = _constants[fieldNameIdx].AsString();
            var value = Pop();
            if (_currentThis is not ObjectInstance oi)
                throw new InvalidOperationException("SetFieldOnThis without a bound ObjectInstance as 'this'.");
            if (IsDeadObject(oi)) { ThrowDestroyedObject(oi); return; }

            // SPEC "Unit declarations" - the same check as in
            // SetField (see there for the reason why this happens at
            // runtime instead of compile time). This opcode
            // is used for the field INITIALISERS themselves (see
            // Compiler.CompileConstructorProto) - `int x : mm = 5`
            // would let the first, declared
            // value pass completely unchecked without this check here.
            bool hasUnitRule = false;
            if (ExecutionMode != VmExecutionMode.Performance && oi.RtClass != null)
            {
                string? requiredUnitName = oi.RtClass.FindFieldRequiredUnit(fieldName);
                if (requiredUnitName != null)
                {
                    hasUnitRule = true; // every assignment must check the unit - do not cache
                    var requiredUnit = Values.Unit.Parse(requiredUnitName);
                    var actualUnit = value.Unit ?? Values.Unit.Unitless;
                    if (!actualUnit.Equals(requiredUnit))
                    {
                        ThrowUnitMismatch(requiredUnitName, actualUnit);
                        return;
                    }
                }
            }

            if (!hasUnitRule && oi.RtClass != null && oi.ThreadLock == null
                && oi.RtClass.FieldIndex.TryGetValue(fieldName, out int thisIndex))
                StoreSite(site, new SiteCache(oi.RtClass, null, thisIndex));
            oi.SetFieldLocked(fieldName, value);
            return;
        }
        }

        private void OpCallMethod()
        {
        {
            if (PollSignals()) return;
            int site = _ip - 1;
            int methodNameIdx = ReadU16();
            int argCount = ReadByte();
            long copyMask = TakeCopyMask();

            // Fast path (inline cache, see SiteCache): an object of the same class as at the last call
            // of this site - method, access and argument checks are already done.
            if (_stack[_sp - 1 - argCount] is { Kind: ValueKind.Class } cachedTarget
                && LookupSite(site) is { Proto: { } cachedMethod } siteEntry
                && cachedTarget.AsObjectRef() is ObjectInstance cachedObj
                && ReferenceEquals(cachedObj.RtClass, siteEntry.Class)
                && cachedObj.Mailbox == null
                && (_threadBroker == null || _sectionDepth > 0 || !cachedObj.InGlobalsDomain))
            {
                // A method that only calls a native function with `this.field` and its parameters (all methods of the
                // bridge preludes): call the native function directly, without scope/frame (see NativeForwarder).
                if (siteEntry.Forwarder is { } forwarder && copyMask == 0 && cachedObj.ThreadLock == null)
                {
                    var nativeArgs = new Value[argCount + 1];
                    nativeArgs[0] = cachedObj.Fields.GetAt(siteEntry.ForwarderFieldIndex);
                    Array.Copy(_stack, _sp - argCount, nativeArgs, 1, argCount);
                    _sp -= argCount + 1; // Arguments and receiver
                    if (CallNativeGuarded(forwarder.NativeIndex, nativeArgs, out Value forwarded))
                        Push(forwarder.ReturnsResult ? forwarded : Value.MakeUndefined());
                    PollSignalsAfterOp();
                    return;
                }

                EnterCall(cachedMethod, argCount, dropBelow: true, cachedObj, copyMask: copyMask);
                return;
            }

            OpCallMethodSlow(site, methodNameIdx, argCount, copyMask);
            return;
        }
        }

        private void OpCallMethodSlow(int site, int methodNameIdx, int argCount, long copyMask)
        {
        {
            string methodName = _constants[methodNameIdx].AsString();
            var args = new Value[argCount];
            for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
            var target = Pop();

            // Built-in methods on primitive values (string/char/
            // int (as byte)/buffer, see TryCallBuiltinMethod, SPEC
            // 8.10) - SEPARATE from the normal class method call
            // below, since a primitive value is not an ObjectInstance
            // and never was one (RequireObjectInstance would
            // otherwise wrongly reject here).
            if (target.Kind != ValueKind.Class)
            {
                // `foreach (x in array)` / `foreach (b in buffer)`: an array/buffer is
                // no object instance with a GetEnumerator() of its own - here a
                // ListEnumerator of the prelude over it (the same class that `List`
                // uses; it only reads `items[index]`/`count`). Without a prelude (pure
                // core programs) it stays with the error below.
                if (methodName == "GetEnumerator" && args.Length == 0
                    && target.Kind is ValueKind.Array or ValueKind.Buffer
                    && _classes.TryGetValue("ListEnumerator", out var enumeratorClass))
                {
                    long itemCount = target.Kind == ValueKind.Array ? target.AsArray().Length : target.AsBuffer().Length;
                    var enumerator = ConstructNested(enumeratorClass, new[] { target, Value.MakeInt(itemCount) });
                    Push(Value.MakeClassRef(enumerator));
                    return;
                }

                // Methods from a base-type extension (`class extends string { ... }`,
                // SPEC 5.5.1 - e.g. IndexOf/Substring in the prelude): like an object call, only
                // `this` is the value itself. Before the fixed built-in conversions below.
                if (_baseTypeClasses[(int)target.Kind] is { } extensionRc)
                {
                    var (extProto, extDeclaringRc, extAccess) = extensionRc.FindMethodWithAccess(methodName, args.Length);
                    if (extProto != null)
                    {
                        if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(extDeclaringRc!, extAccess))
                        {
                            ThrowAccessDenied(
                                $"Method '{methodName}' of the extension of '{extensionRc.Name.Substring(1)}' is " +
                                $"{DescribeAccess(extAccess)} and cannot be called from here.");
                            return;
                        }
                        CheckArity(extProto, args.Length);
                        args = FillDefaultArgs(extProto, args, target);

                        _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
                        var extScope = new Scope(_globalScope);
                        foreach (var a in args) extScope.DefineSlot(a);
                        ApplyCopyMask(extScope, copyMask, extProto.RefMask);

                        _currentThis = target;
                        _currentScope = extScope;
                        _currentChunk = extProto.Chunk;
                        _ip = 0;
                        return;
                    }
                }

                if (LeafOf(target) is { } ownedLeaf && TryCallLeafOwnershipMethod(ownedLeaf, methodName, args, out var leafResult))
                {
                    Push(leafResult);
                    return;
                }
                if (copyMask != 0) ApplyCopyMaskToArgs(args, copyMask);
                if (TryCallBuiltinMethod(target, methodName, args, out Value builtinResult))
                {
                    AdoptFresh(builtinResult);
                    Push(builtinResult);
                    return;
                }
                throw new InvalidOperationException(
                    $"'{methodName}' ({args.Length} argument(s)) is not a known built-in method " +
                    $"on a value of type {target.Kind}.");
            }

            var obj = (ObjectInstance)target.AsObjectRef();
            if (IsDeadObject(obj) && !(methodName is "TakeLocal" or "TakeUpwards" or "TakeGlobal" or "TakeTo" or "tryTakeLocal" or "tryTakeUpwards" or "tryTakeGlobal" or "tryTakeTo")) { ThrowDestroyedObject(obj); return; }

            // Actor target (see Runtime.ObjectInstance.Mailbox documentation):
            // EVERY method call becomes an asynchronous message
            // instead of a direct call, regardless of the calling
            // thread - this call itself returns 'undefined' and
            // continues normally (no jump into any chunk).
            if (obj.Mailbox != null)
            {
                if (copyMask != 0) ApplyCopyMaskToArgs(args, copyMask);
                obj.Mailbox.Enqueue(new ActorMessage(methodName, args));
                Push(Value.MakeUndefined());
                return;
            }

            // Fire thread calls a method of an object of the shared area: it runs as a whole in a section (atomic).
            if (NeedsSection(obj))
            {
                if (copyMask != 0) ApplyCopyMaskToArgs(args, copyMask);
                var sectionResult = CallGlobalsMethodInSection(obj, methodName, args);
                if (sectionResult != null) Push(sectionResult.Value); // null: an exception has redirected the flow
                return;
            }

            var rc = ResolveClass(obj.ClassName);
            var (proto, declaringRcCall, accessCall) = rc.FindMethodWithAccess(methodName, args.Length);
            if (proto == null)
            {
                // The ownership transfer (SPEC 2.2) is there for every object, without the class declaring it.
                if (TryCallOwnershipMethod(obj, methodName, args, out var ownershipValue))
                {
                    Push(ownershipValue);
                    return;
                }
                throw new InvalidOperationException(DescribeMethodNotFound(rc, methodName, args.Length));
            }
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcCall!, accessCall))
            {
                ThrowAccessDenied(
                    $"Method '{methodName}' of '{declaringRcCall!.Name}' is {DescribeAccess(accessCall)} " +
                    "and cannot be called from here.");
                return;
            }
            CheckArity(proto, args.Length);
            if (proto.ParamCount == argCount && obj.RtClass != null)
            {
                var forwarder = proto.Forwarder;
                int forwarderField = -1;
                if (forwarder != null && !obj.RtClass.FieldIndex.TryGetValue(forwarder.FieldName, out forwarderField))
                    forwarder = null;
                StoreSite(site, new SiteCache(obj.RtClass, proto, 0, forwarder, forwarderField));
            }
            args = FillDefaultArgs(proto, args, obj);

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            var scope = new Scope(_globalScope);
            foreach (var a in args) scope.DefineSlot(a);
            ApplyCopyMask(scope, copyMask, proto.RefMask);

            _currentThis = obj;
            _currentScope = scope;
            _currentChunk = proto.Chunk;
            _ip = 0;
            return;
        }
        }

        /// <summary>`obj.TakeLocal(...)`, `obj.TakeUpwards(...)`, `obj.TakeGlobal(...)`, `obj.TakeTo(other, ...)` (SPEC 2.2): built-in methods of every object that
        /// only apply if the class declares nothing of the same name. The last argument may be a `Takes` value (what moves along besides the object).
        /// With `try` in front (`tryTake...`, from the compiler) the method only moves if the caller is the owner (the scope of the call or `this`), and
        /// returns whether it did so. Returns false if `name`/argument count is none of these.</summary>
        private bool TryCallOwnershipMethod(ObjectInstance obj, string name, Value[] args, out Value result)
        {
            result = Value.MakeUndefined();
            bool conditional = name.StartsWith("tryTake", StringComparison.Ordinal);
            string method = conditional ? name.Substring(3) : name;
            if (method is not ("TakeLocal" or "TakeUpwards" or "TakeGlobal" or "TakeTo")) return false;
            int baseArgs = method == "TakeTo" ? 1 : 0;
            if (args.Length != baseArgs && args.Length != baseArgs + 1) return false;
            int mode = args.Length > baseArgs ? TakesMode(args[baseArgs]) : Takes.This;
            if (conditional)
            {
                // only the owner moves: the object belongs to the scope of this call or to the current object
                bool owned = !obj.IsDestroyed && (IsCurrentCallOwner(obj.Owner) || ReferenceEquals(obj.Owner, _currentThis));
                result = Value.MakeBool(owned);
                if (!owned) return true;
            }
            Scope? target;
            switch (method)
            {
                case "TakeUpwards":
                    obj.TakeUpwards();
                    target = obj.Owner as Scope;
                    break;
                case "TakeGlobal":
                    obj.TakeGlobal(_globalScope);
                    target = _globalScope;
                    break;
                case "TakeTo":
                {
                    var other = RequireObjectInstance(args[0], "TakeTo");
                    obj.TakeTo(other, this);
                    if (!obj.IsDestroyed && mode != Takes.This) OwnershipWalk.MoveReachable(Value.MakeClassRef(obj), mode, OwnsWithinCall, other, EnumerateItems);
                    return true;
                }
                default:   // TakeLocal
                    // pull into the current scope (the scope in which the call stands)
                    if (obj.IsDestroyed) throw new OwnershipException("A destroyed object cannot change its owner.");
                    obj.ReparentTo(_currentScope);
                    target = _currentScope;
                    break;
            }
            if (mode != Takes.This && target != null) OwnershipWalk.MoveReachable(Value.MakeClassRef(obj), mode, OwnsWithinCall, target, EnumerateItems);
            return true;
        }

        private static int TakesMode(Value v)
        {
            if (v.Kind != ValueKind.Int || v.AsInt() < Takes.This || v.AsInt() > Takes.All)
                throw new OwnershipException("The mode of Take... must be one of Takes.This, Takes.Children, Takes.Locals, Takes.All.");
            return (int)v.AsInt();
        }

        /// <summary>The items of an object that implements `IEnumerable` (`Takes.Children`, SPEC 2.2): via its enumerator (GetEnumerator, MoveNext, GetCurrent). null if it implements none
        /// or an exception aborted the enumeration.</summary>
        private IReadOnlyList<Value>? EnumerateItems(ObjectInstance obj)
        {
            bool enumerable = false;
            for (var rc = ResolveClass(obj.ClassName); rc != null && !enumerable; rc = rc.Base)
                enumerable = rc.Interfaces.Contains("IEnumerable");
            if (!enumerable) return null;
            var enumerator = CallMethodNested(obj, "GetEnumerator", Array.Empty<Value>());
            if (enumerator == null || enumerator.Value.Kind != ValueKind.Class) return null;
            var enumeratorObj = (ObjectInstance)enumerator.Value.AsObjectRef();
            var items = new List<Value>();
            while (true)
            {
                var more = CallMethodNested(enumeratorObj, "MoveNext", Array.Empty<Value>());
                if (more == null) return null;
                if (!more.Value.AsBool()) break;
                var current = CallMethodNested(enumeratorObj, "GetCurrent", Array.Empty<Value>());
                if (current == null) return null;
                items.Add(current.Value);
            }
            return items;
        }

        /// <summary>The ownership methods of an array or buffer (SPEC 2.2): TakeUpwards, TakeGlobal, TakeTo(object), TakeLocal(), each with a `Takes` value as the last argument
        /// and with `try` in front (`tryTake...`).</summary>
        private bool TryCallLeafOwnershipMethod(IOwnedLeaf leaf, string name, Value[] args, out Value result)
        {
            result = Value.MakeUndefined();
            bool conditional = name.StartsWith("tryTake", StringComparison.Ordinal);
            string method = conditional ? name.Substring(3) : name;
            if (method is not ("TakeUpwards" or "TakeGlobal" or "TakeTo" or "TakeLocal")) return false;
            int baseArgs = method == "TakeTo" ? 1 : 0;
            if (args.Length != baseArgs && args.Length != baseArgs + 1) return false;
            int mode = args.Length > baseArgs ? TakesMode(args[baseArgs]) : Takes.This;
            if (conditional)
            {
                bool owned = !leaf.IsDestroyed && (IsCurrentCallOwner(leaf.LeafOwner) || ReferenceEquals(leaf.LeafOwner, _currentThis));
                result = Value.MakeBool(owned);
                if (!owned) return true;
            }
            IOwner target;
            switch (method)
            {
                case "TakeUpwards": LeafOwnership.TakeUpwards(leaf); target = leaf.LeafOwner!; break;
                case "TakeGlobal": LeafOwnership.Reparent(leaf, _globalScope); target = _globalScope; break;
                case "TakeTo":
                {
                    var other = RequireObjectInstance(args[0], "TakeTo");
                    LeafOwnership.TakeTo(leaf, other, this);
                    target = other;
                    break;
                }
                default: LeafOwnership.Reparent(leaf, _currentScope); target = _currentScope; break;
            }
            if (mode != Takes.This && !leaf.IsDestroyed)
                OwnershipWalk.MoveReachable(leaf is ScriptArray a ? Value.MakeArray(a) : Value.MakeBuffer((ByteBuffer)leaf), mode, OwnsWithinCall, target, EnumerateItems);
            return true;
        }

        /// <summary>`delete x`: destroys an object (destructor, cascade), an array or a buffer immediately and detaches it from its owner.</summary>
        private void DeleteValue(Value v)
        {
            switch (v.Kind)
            {
                case ValueKind.Class:
                {
                    var obj = (ObjectInstance)v.AsObjectRef();
                    if (obj.IsDestroyed) return;
                    obj.Owner.RemoveOwned(obj);
                    obj.Destroy(this);
                    return;
                }
                case ValueKind.Array:
                case ValueKind.Buffer:
                    LeafOwnership.Destroy(LeafOf(v)!, this);
                    return;
                default:
                    throw new InvalidOperationException($"'delete' expects an object, an array or a buffer, not {v.Kind}.");
            }
        }

        private void OpCallBaseMethod()
        {
        {
            string baseClassName = _constants[ReadU16()].AsString();
            string methodName = _constants[ReadU16()].AsString();
            int argCount = ReadByte();
            long copyMask = TakeCopyMask();
            var args = new Value[argCount];
            for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();

            var rc = ResolveClass(baseClassName);
            var (proto, declaringRcBase, accessBase) = rc.FindMethodWithAccess(methodName, args.Length);
            if (proto == null)
                throw new InvalidOperationException(DescribeMethodNotFound(rc, methodName, args.Length));
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcBase!, accessBase))
            {
                ThrowAccessDenied(
                    $"Method '{methodName}' of '{declaringRcBase!.Name}' is {DescribeAccess(accessBase)} " +
                    "and cannot be called from here.");
                return;
            }
            CheckArity(proto, args.Length);
            args = FillDefaultArgs(proto, args, _currentThis);

            // 'this' remains the same object (non-virtual call).
            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            var scope = new Scope(_globalScope);
            foreach (var a in args) scope.DefineSlot(a);
            ApplyCopyMask(scope, copyMask, proto.RefMask);

            _currentScope = scope;
            _currentChunk = proto.Chunk;
            _ip = 0;
            return;
        }
        }

        private void OpGetStaticField()
        {
        {
            // SPEC "Static members" - no object on the stack
            // (the class name is already a constant in the bytecode,
            // see Resolver.TryResolveStaticMemberAccess/Compiler),
            // the actual storage location lies directly on the
            // RuntimeClass (see FindStaticFieldOwner - possibly shares
            // the same storage location with a base class).
            string className = _constants[ReadU16()].AsString();
            string fieldName = _constants[ReadU16()].AsString();
            var staticRc = ResolveClass(className);
            var owner = staticRc.FindStaticFieldOwner(fieldName);

            if (owner == null)
            {
                // No static field of this name - try the property
                // getter (naming convention 'get_'+name,
                // exactly as with GetField), this time as a STATIC
                // call (no instance).
                if (staticRc.FindMethod("get_" + fieldName, 0) is { IsStatic: true })
                {
                    var result = CallStaticMethodNested(staticRc, "get_" + fieldName, Array.Empty<Value>());
                    if (result != null) Push(result.Value);
                    return;
                }
                throw new InvalidOperationException(
                    $"'{className}' has no static field '{fieldName}' (and no static " +
                    $"'get_{fieldName}' property).");
            }

            if (ExecutionMode != VmExecutionMode.Performance)
            {
                var fieldAccess = owner.FindFieldAccess(fieldName);
                if (fieldAccess is (var declaringRc, var access) && !IsMemberAccessAllowed(declaringRc, access))
                {
                    ThrowAccessDenied(
                        $"Static field '{fieldName}' of '{declaringRc.Name}' is {DescribeAccess(access)} " +
                        "and cannot be accessed from here.");
                    return;
                }
            }

            Value staticVal;
            var staticBroker = _threadBroker;
            if (staticBroker == null)
            {
                staticVal = owner.StaticFieldValues.TryGetValue(fieldName, out var plain) ? plain : Value.MakeUndefined();
            }
            else
            {
                // Fire thread: static fields are part of the shared area - reading under the lock
                staticBroker.Lock.Enter();
                try { staticVal = owner.StaticFieldValues.TryGetValue(fieldName, out var shared) ? shared : Value.MakeUndefined(); }
                finally { staticBroker.Lock.Exit(); }
                MarkShared(staticVal);
            }
            Push(staticVal);
            return;
        }
        }

        private void OpSetStaticField()
        {
        {
            string setClassName = _constants[ReadU16()].AsString();
            string setFieldName = _constants[ReadU16()].AsString();
            var setValue = Pop();
            var setRc = ResolveClass(setClassName);
            var setOwner = setRc.FindStaticFieldOwner(setFieldName);

            if (setOwner == null)
            {
                // No static field of this name - try the static
                // property setter (naming convention
                // 'set_'+name, counterpart to the getter fallback in
                // GetStaticField, see also SetField).
                if (setRc.FindMethod("set_" + setFieldName, 1) is { IsStatic: true })
                {
                    var setterResult = CallStaticMethodNested(setRc, "set_" + setFieldName, new[] { setValue });
                    // An assignment evaluates to the ASSIGNED value,
                    // not to the return value of the setter. null == redirected
                    // via exception - then do NOT push.
                    if (setterResult != null) Push(setValue);
                    return;
                }
                throw new InvalidOperationException(
                    $"'{setClassName}' has no static field '{setFieldName}' (and no static " +
                    $"'set_{setFieldName}' property).");
            }

            if (ExecutionMode != VmExecutionMode.Performance)
            {
                var fieldAccess = setOwner.FindFieldAccess(setFieldName);
                if (fieldAccess is (var declaringRc, var access) && !IsMemberAccessAllowed(declaringRc, access))
                {
                    ThrowAccessDenied(
                        $"Static field '{setFieldName}' of '{declaringRc.Name}' is {DescribeAccess(access)} " +
                        "and cannot be accessed from here.");
                    return;
                }

                // SPEC "Unit declarations" - identical in content
                // to SetField, see there.
                string? requiredUnitName = setOwner.FindFieldRequiredUnit(setFieldName);
                if (requiredUnitName != null)
                {
                    var requiredUnit = Values.Unit.Parse(requiredUnitName);
                    var actualUnit = setValue.Unit ?? Values.Unit.Unitless;
                    if (!actualUnit.Equals(requiredUnit))
                    {
                        ThrowUnitMismatch(requiredUnitName, actualUnit);
                        return;
                    }
                }
            }

            var staticWriteBroker = _threadBroker ?? _ownerBroker;
            if (staticWriteBroker == null)
            {
                setOwner.StaticFieldValues[setFieldName] = setValue;
            }
            else
            {
                // Static fields belong to the shared area: a fire thread writes in a section, everywhere under the lock
                bool staticInThread = _threadBroker != null;
                if (staticInThread) EnterGlobalsSection();
                try
                {
                    staticWriteBroker.Lock.Enter();
                    try { setOwner.StaticFieldValues[setFieldName] = setValue; }
                    finally { staticWriteBroker.Lock.Exit(); }
                }
                finally { if (staticInThread) ExitGlobalsSection(); }
            }
            Push(setValue);
            return;
        }
        }

        private void OpCallStaticMethod()
        {
        {
            if (PollSignals()) return;
            int site = _ip - 1;
            int classNameIdx = ReadU16();
            int methodNameIdx = ReadU16();
            int callArgCount = ReadByte();
            long copyMask = TakeCopyMask();

            // Fast path: the same site has already resolved once (class/method are fixed as constants
            // in the bytecode, see SiteCache) - no more lookup by class and method name.
            if (LookupSite(site) is { Proto: { } cachedStatic })
            {
                EnterCall(cachedStatic, callArgCount, dropBelow: false, newThis: null, copyMask: copyMask);
                return;
            }

            OpCallStaticMethodSlow(site, classNameIdx, methodNameIdx, callArgCount, copyMask);
            return;
        }
        }

        private void OpCallStaticMethodSlow(int site, int classNameIdx, int methodNameIdx, int callArgCount, long copyMask)
        {
        {
            string callClassName = _constants[classNameIdx].AsString();
            string callMethodName = _constants[methodNameIdx].AsString();
            var callArgs = new Value[callArgCount];
            for (int i = callArgCount - 1; i >= 0; i--) callArgs[i] = Pop();

            var callRc = ResolveClass(callClassName);
            var (callProto, declaringRcCall, accessCall) = callRc.FindMethodWithAccess(callMethodName, callArgs.Length);
            if (callProto == null)
                throw new InvalidOperationException(DescribeMethodNotFound(callRc, callMethodName, callArgs.Length));
            if (!callProto.IsStatic)
                throw new InvalidOperationException(
                    $"'{callMethodName}' on '{callClassName}' is not a static method - " +
                    $"'ClassName.{callMethodName}(...)' can only be used to call 'static' methods.");
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcCall!, accessCall))
            {
                ThrowAccessDenied(
                    $"Static method '{callMethodName}' of '{declaringRcCall!.Name}' is " +
                    $"{DescribeAccess(accessCall)} and cannot be called from here.");
                return;
            }
            CheckArity(callProto, callArgs.Length);
            callArgs = FillDefaultArgs(callProto, callArgs, null);

            if (callProto.ParamCount == callArgCount)
                StoreSite(site, new SiteCache(null, callProto, 0));

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            var callScope = new Scope(_globalScope);
            foreach (var a in callArgs) callScope.DefineSlot(a);
            ApplyCopyMask(callScope, copyMask, callProto.RefMask);

            // Explicitly NO 'this' (unlike above at CallBaseMethod,
            // which keeps the calling instance) - the resolver
            // already forbids 'this'/'super' in the body of a static
            // method (see Resolver.ResolveExpr/ThisExpr),
            // this here is the additional runtime safeguard for it.
            _currentThis = null;
            _currentScope = callScope;
            _currentChunk = callProto.Chunk;
            _ip = 0;
            return;
        }
        }

        private void OpCallProtoWithThis()
        {
        {
            int protoIdx = ReadU16();
            int argCount = ReadByte();
            var proto = _currentChunk.Functions[protoIdx];
            Scope scope;
            Value thisVal;
            if (argCount == 0)
            {
                // Field initialisers (no parameters): no argument array, scope from the pool
                thisVal = Pop();
                CheckArity(proto, 0);
                _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
                scope = RentCallScope(SlotSlack, 0);
            }
            else
            {
                var args = new Value[argCount];
                for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
                thisVal = Pop();
                CheckArity(proto, args.Length);

                _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
                scope = RentCallScope(argCount + SlotSlack, argCount);
                Array.Copy(args, scope.SlotArray, argCount);
            }

            _currentThis = thisVal.Kind == ValueKind.Class ? thisVal.AsObjectRef() : (object)thisVal;
            _currentScope = scope;
            _currentChunk = proto.Chunk;
            _ip = 0;
            return;
        }
        }

        private void OpNewArray()
        {
        {
            long size = Pop().AsInt();
            if (size < 0)
                throw new InvalidOperationException($"Invalid array size {size}.");
            var created = new ScriptArray((int)size);
            LeafOwnership.Adopt(created, _currentScope);
            Push(Value.MakeArray(created));
            return;
        }
        }

        private void OpMakeArrayLiteral()
        {
        {
            int count = ReadU16();
            var arr = new ScriptArray(count);
            for (int i = count - 1; i >= 0; i--)
                arr.Items[i] = Pop();
            LeafOwnership.Adopt(arr, _currentScope);
            Push(Value.MakeArray(arr));
            return;
        }
        }

        private void OpArrayGet()
        {
            // Fast path: array with int index in the valid range (everything else - also the error case - below).
            ref Value fastTarget = ref _stack[_sp - 2];
            ref Value fastIndex = ref _stack[_sp - 1];
            if (fastTarget.Kind == ValueKind.Array && fastIndex.Kind == ValueKind.Int)
            {
                var fastArray = fastTarget.AsArray();
                var items = fastArray.Items;
                long i = fastIndex.AsInt();
                if ((ulong)i < (ulong)items.Length && !fastArray.Special)
                {
                    fastTarget = items[i];
                    _sp--;
                    return;
                }
            }
            OpArrayGetSlow();
        }

        private void OpArrayGetSlow()
        {
        {
            var indexVal = Pop();
            var target = Pop();
            if (IsDestroyedLeaf(target)) { ThrowDestroyed(target); return; }

            if (target.Kind == ValueKind.Array && target.AsArray().IsShared)
            {
                // Array of the shared area (see GlobalsBroker): a fire thread reads it under the lock
                long sharedIdx = indexVal.AsInt();
                if (TryGetSharedElement(target.AsArray(), sharedIdx, out var sharedValue))
                {
                    if (_threadBroker != null) MarkShared(sharedValue);
                    Push(sharedValue);
                }
                else ThrowIndexOutOfBounds(sharedIdx, target.AsArray().Length);
            }
            else if (target.Kind == ValueKind.Array)
            {
                if (ExecutionMode == VmExecutionMode.Performance)
                {
                    // See VmExecutionMode.Performance documentation - NO
                    // bounds check, an invalid index leads to
                    // a raw .NET IndexOutOfRangeException instead of
                    // a catchable script exception.
                    Push(target.AsArray().GetUnchecked(indexVal.AsInt()));
                }
                else
                {
                    long idx = indexVal.AsInt();
                    if (target.AsArray().TryGet(idx, out var v))
                        Push(v);
                    else
                        // Turns an invalid index into a real,
                        // catchable script exception instead of a raw
                        // C# error - NO push here, ThrowIndexOutOfBounds
                        // has already redirected _currentChunk/_ip.
                        ThrowIndexOutOfBounds(idx, target.AsArray().Length);
                }
            }
            else if (target.Kind == ValueKind.Buffer)
            {
                // ALWAYS returns int[8] (width W8, see Values.NumericWidth) -
                // 'byte' is pure type sugar for int[8] (SPEC 8.10),
                // no ValueKind of its own, a single byte is therefore
                // simply an ordinary int value with this width.
                if (ExecutionMode == VmExecutionMode.Performance)
                {
                    byte bFast = target.AsBuffer().GetUnchecked(indexVal.AsInt());
                    Push(Value.MakeInt(bFast, width: NumericWidth.W8));
                }
                else
                {
                    long idx = indexVal.AsInt();
                    if (target.AsBuffer().TryGet(idx, out byte b))
                        Push(Value.MakeInt(b, width: NumericWidth.W8));
                    else
                        ThrowIndexOutOfBounds(idx, target.AsBuffer().Length);
                }
            }
            else if (target.Kind == ValueKind.String)
            {
                // `s[i]` reads the character at index i (read-only - strings are
                // immutable, see ArraySet).
                long idx = indexVal.AsInt();
                string text = target.AsString();
                if (idx >= 0 && idx < text.Length)
                    Push(Value.MakeChar(text[(int)idx]));
                else
                    ThrowIndexOutOfBounds(idx, text.Length, "String index");
            }
            else if (target.Kind == ValueKind.Class)
            {
                // '[]' operator overloading by naming convention (like
                // GetEnumerator/MoveNext/GetCurrent with foreach): a
                // class with a GetIndex(i) method is used for read accesses
                // - purely dynamic, works on every class
                // with a matching method, not only on 'List'.
                var obj = (ObjectInstance)target.AsObjectRef();
                var result = CallMethodNested(obj, "GetIndex", new[] { indexVal });
                // null == GetIndex() was left through a thrown exception
                // (see CallMethodNested documentation) - then do NOT
                // push, execution already continues elsewhere.
                if (result != null) Push(result.Value);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Index access ('[]') on a value of type {target.Kind} is not possible " +
                    "(neither an array nor a class with a 'GetIndex' method).");
            }
            return;
        }
        }

        private void OpArraySet()
        {
            // Fast path as with OpArrayGet.
            ref Value fastTarget = ref _stack[_sp - 3];
            ref Value fastIndex = ref _stack[_sp - 2];
            if (fastTarget.Kind == ValueKind.Array && fastIndex.Kind == ValueKind.Int)
            {
                var fastArray = fastTarget.AsArray();
                var items = fastArray.Items;
                long i = fastIndex.AsInt();
                if ((ulong)i < (ulong)items.Length && !fastArray.Special)
                {
                    var assigned = _stack[_sp - 1];
                    items[i] = assigned;
                    _sp -= 2;
                    _stack[_sp - 1] = assigned;
                    return;
                }
            }
            OpArraySetSlow();
        }

        private void OpArraySetSlow()
        {
        {
            var value = Pop();
            var indexVal = Pop();
            var target = Pop();
            if (IsDestroyedLeaf(target)) { ThrowDestroyed(target); return; }

            if (target.Kind == ValueKind.Array && target.AsArray().IsShared)
            {
                // Array of the shared area: a fire thread changes it only in a section, everywhere under the lock
                long sharedIdx = indexVal.AsInt();
                if (TrySetSharedElement(target.AsArray(), sharedIdx, value)) Push(value);
                else ThrowIndexOutOfBounds(sharedIdx, target.AsArray().Length);
            }
            else if (target.Kind == ValueKind.Array)
            {
                if (ExecutionMode == VmExecutionMode.Performance)
                {
                    target.AsArray().SetUnchecked(indexVal.AsInt(), value);
                    Push(value);
                }
                else
                {
                    long idx = indexVal.AsInt();
                    if (target.AsArray().TrySet(idx, value))
                        Push(value);
                    else
                        // No push here - ThrowIndexOutOfBounds has already
                        // redirected _currentChunk/_ip, an
                        // additional push would shift the stack there.
                        ThrowIndexOutOfBounds(idx, target.AsArray().Length);
                }
            }
            else if (target.Kind == ValueKind.Buffer)
            {
                if (value.Kind != ValueKind.Int)
                    throw new InvalidOperationException(
                        $"Assigning to a byte buffer expects an int value (byte = int[8]), not {value.Kind}.");
                if (ExecutionMode == VmExecutionMode.Performance)
                {
                    target.AsBuffer().SetUnchecked(indexVal.AsInt(), (byte)value.AsInt());
                    Push(value);
                }
                else
                {
                    long idx = indexVal.AsInt();
                    if (target.AsBuffer().TrySet(idx, (byte)value.AsInt()))
                        Push(value);
                    else
                        ThrowIndexOutOfBounds(idx, target.AsBuffer().Length);
                }
            }
            else if (target.Kind == ValueKind.Class)
            {
                var obj = (ObjectInstance)target.AsObjectRef();
                var result = CallMethodNested(obj, "SetIndex", new[] { indexVal, value }); // Return value unused
                // null == SetIndex() was left through a thrown exception
                // (see CallMethodNested documentation) - then do NOT
                // push, execution already continues elsewhere.
                if (result != null) Push(value);
            }
            else if (target.Kind == ValueKind.String)
            {
                throw new InvalidOperationException(
                    "Strings are immutable - 's[i] = ...' is not possible " +
                    "(Replace/Substring return a new string).");
            }
            else
            {
                throw new InvalidOperationException(
                    $"Index assignment ('[]=') on a value of type {target.Kind} is not possible " +
                    "(neither an array nor a class with a 'SetIndex' method).");
            }
            return;
        }
        }

        private void Execute(OpCode op)
        {
            switch (op)
            {
                case OpCode.LoadConst:
                    Push(_constants[ReadU16()]);
                    break;

                case OpCode.Pop:
                    Pop();
                    break;

                case OpCode.Dup:
                    Push(Peek());
                    break;

                case OpCode.Swap:
                {
                    var top = Pop();
                    var below = Pop();
                    Push(top);
                    Push(below);
                    break;
                }

                case OpCode.RotateUnderTop:
                {
                    var c = Pop(); // oben
                    var b = Pop();
                    var a = Pop(); // unten
                    Push(b);
                    Push(a);
                    Push(c);
                    break;
                }

                case OpCode.IncDecIndex:
                    OpIncDecIndex();
                    break;

                case OpCode.LoadLocal:
                {
                    int depth = ReadU16(); int slot = ReadU16();
                    Push(_currentScope.GetAncestor(depth).GetSlot(slot));
                    break;
                }

                case OpCode.StoreLocal:
                {
                    int depth = ReadU16(); int slot = ReadU16();
                    _currentScope.GetAncestor(depth).SetSlot(slot, Peek());
                    break;
                }

                case OpCode.LoadGlobal:
                    {
                        int loadSlot = ReadU16();
                        Push(loadSlot < _sharedCount ? LoadSharedGlobal(loadSlot) : _globalScope.GetSlot(loadSlot));
                    }
                    break;

                case OpCode.StoreGlobal:
                    {
                        int storeSlot = ReadU16();
                        if (storeSlot < _sharedCount) StoreSharedGlobal(storeSlot, Peek());
                        else if (_ownerBroker != null) StoreOwnerGlobal(storeSlot, Peek());
                        else _globalScope.SetSlot(storeSlot, Peek());
                    }
                    break;

                case OpCode.DeclareLocal:
                    if (_ownerBroker != null && ReferenceEquals(_currentScope, _globalScope)) DeclareOwnerGlobal(Pop());
                    else _currentScope.DefineSlot(Pop());
                    break;

                case OpCode.Add: BinaryNumericOrOperator(Value.Add, "operator+"); break;
                case OpCode.Sub: BinaryNumericOrOperator(Value.Subtract, "operator-"); break;
                case OpCode.Mul: BinaryNumericOrOperator(Value.Multiply, "operator*"); break;
                case OpCode.Div: BinaryNumericOrOperator(Value.Divide, "operator/"); break;
                case OpCode.Mod: BinaryNumericOrOperator(Value.Modulo, "operator%"); break;
                case OpCode.Power: BinaryNumericOrOperator(Value.Power, "operator^"); break;
                case OpCode.BitAnd: BinaryNumericOrOperator(Value.BitAnd, "operator&"); break;
                case OpCode.BitOr: BinaryNumericOrOperator(Value.BitOr, "operator|"); break;
                case OpCode.BitXor: BinaryNumericOrOperator(Value.BitXor, "operator#"); break;
                case OpCode.ShiftLeft: BinaryNumericOrOperator(Value.ShiftLeft, "operator<<"); break;
                case OpCode.ShiftRight: BinaryNumericOrOperator(Value.ShiftRight, "operator>>"); break;

                case OpCode.FormatValue:
                {
                    string format = _constants[ReadU16()].AsString();
                    var v = Pop();
                    if (v.Kind == ValueKind.Class && string.IsNullOrEmpty(format))
                    {
                        // `$"{object}"`: the result of ToString() of the object
                        var text = StringifyForText(v);
                        if (text == null) break;
                        Push(Value.MakeString(text));
                        break;
                    }
                    Push(Value.MakeString(v.Format(format)));
                    break;
                }


                case OpCode.Neg: Push(Value.Negate(Pop())); break;
                case OpCode.LogicalNot: Push(Value.LogicalNot(Pop())); break;
                case OpCode.BitNot: Push(Value.BitNot(Pop())); break;

                case OpCode.Eq:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(Value.ValuesEqual(a, b)), "operator==");
                    break;
                case OpCode.NotEq:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(!Value.ValuesEqual(a, b)), "operator!=");
                    break;
                case OpCode.Lt:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(Value.Compare(a, b) < 0), "operator<");
                    break;
                case OpCode.LtEq:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(Value.Compare(a, b) <= 0), "operator<=");
                    break;
                case OpCode.Gt:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(Value.Compare(a, b) > 0), "operator>");
                    break;
                case OpCode.GtEq:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(Value.Compare(a, b) >= 0), "operator>=");
                    break;

                case OpCode.CoerceUnit:
                {
                    var unit = _currentChunk.Units[ReadU16()];
                    Push(Pop().CoerceUnit(unit));
                    break;
                }
                case OpCode.CoerceUnitDynamic:
                {
                    var candidate = Pop();
                    var anchor = Peek();
                    var targetUnit = anchor.Unit ?? Unit.Unitless;
                    Push(candidate.CoerceUnit(targetUnit));
                    break;
                }
                case OpCode.CoerceType:
                {
                    var tag = (TypeTag)ReadByte();
                    Push(Pop().CoerceType(TagToKind(tag)));
                    break;
                }
                case OpCode.CoerceTypeDynamic:
                {
                    var candidate = Pop();
                    var anchor = Peek();
                    Push(candidate.CoerceType(anchor.Kind));
                    break;
                }

                case OpCode.Jump:
                    _ip = ReadU16();
                    break;

                case OpCode.JumpIfFalse:
                {
                    int addr = ReadU16();
                    if (!Pop().AsBool()) _ip = addr;
                    break;
                }
                case OpCode.JumpIfFalsePeek:
                {
                    int addr = ReadU16();
                    if (!Peek().AsBool()) _ip = addr;
                    break;
                }
                case OpCode.JumpIfTruePeek:
                {
                    int addr = ReadU16();
                    if (Peek().AsBool()) _ip = addr;
                    break;
                }

                case OpCode.EnterScope:
                    _currentScope = RentScope(_currentScope);
                    break;

                case OpCode.ExitScope:
                    ExitScopeOwning();
                    break;

                case OpCode.CallNative:
                    OpCallNative();
                    break;

                case OpCode.CallTryableNative:
                {
                    int tryableIdx = ReadU16();
                    int argCount = ReadByte();
                    var args = new Value[argCount];
                    for (int i = argCount - 1; i >= 0; i--) args[i] = Pop();
                    // NO exception on failure/timeout (see
                    // TryableNativeFunction documentation) - the host implementation
                    // reports that via the return value 'false', not via
                    // a throw; the script then simply sees 'undefined'.
                    bool success = _natives.TryableAt(tryableIdx)(args, out Value tryResult);
                    if (success) AdoptFresh(tryResult);
                    Push(success ? tryResult : Value.MakeUndefined());
                    PollSignalsAfterOp();
                    break;
                }

                case OpCode.CallExtern:
                {
                    int nameIdx = ReadU16();
                    int argCount = ReadByte();
                    string externName = _constants[nameIdx].AsString();

                    var scriptArgs = new Value[argCount];
                    for (int i = argCount - 1; i >= 0; i--) scriptArgs[i] = Pop();

                    var (nativeArgs, cleanups) = MarshalArgsOut(scriptArgs);
                    object? nativeResult;
                    try
                    {
                        if (_externs.TryGet(externName, out var fn))
                        {
                            // Registered manually by the host (ExternRegistry) - as before.
                            nativeResult = fn(nativeArgs);
                        }
                        else if (_externSignatures.TryGetValue(externName, out var sig) && sig.LibName != null)
                        {
                            // Linked dynamically against a native library named via '#extern "libName"'
                            // - no host code needed.
                            var del = ResolveDynamicExtern(externName, sig);
                            nativeResult = del.DynamicInvoke(nativeArgs);
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"'{externName}' is declared extern, but neither linked manually " +
                                "(ExternRegistry.Register in the host) nor assigned to a " +
                                "native library with '#extern \"libName\"'.");
                        }
                    }
                    finally
                    {
                        // Copy-out for pointer arguments (see MarshalArgsOut)
                        // AND release of the native memory allocated for it -
                        // must also happen on a C# exception from the native
                        // function, otherwise native memory leaks.
                        foreach (var cleanup in cleanups) cleanup();
                    }

                    Push(MarshalResultIn(nativeResult));
                    PollSignalsAfterOp();
                    break;
                }

                case OpCode.MakeLambda:
                    OpMakeLambda();
                    break;

                case OpCode.MakeLambdaCapturing:
                    OpMakeLambdaCapturing();
                    break;

                case OpCode.Probe:
                    OpProbe();
                    break;

                case OpCode.SilenceMember:
                    OpSilenceMember();
                    break;

                case OpCode.SilenceValue:
                    OpSilenceValue();
                    break;

                case OpCode.Call:
                    OpCall();
                    break;

                case OpCode.Return:
                    OpReturn();
                    break;

                case OpCode.NewObject:
                    OpNewObject();
                    break;

                case OpCode.NewObjectOwned:
                    OpNewObjectOwned();
                    break;

                case OpCode.CopyValue:
                {
                    // `flat x` / `copy x` (SPEC 2.4) - the copy belongs to the current scope (SPEC 2.1).
                    bool deep = (ReadByte() & 1) != 0;
                    var source = Pop();
                    Push(ObjectCloner.Clone(source, _currentScope, deep));
                    break;
                }

                case OpCode.CopyArgs:
                {
                    long mask = ReadU16();
                    mask |= (long)ReadU16() << 16;
                    mask |= (long)ReadU16() << 32;
                    mask |= (long)ReadU16() << 48;
                    _copyArgMask = mask;
                    break;
                }

                case OpCode.CopyValueOwned:
                {
                    // Assigned directly to a field: the copy belongs to the target object (like NewObjectOwned).
                    bool deep = (ReadByte() & 1) != 0;
                    var source = Pop();
                    var owner = RequireObjectInstance(Pop(), "Copy with owner");
                    Push(ObjectCloner.CloneOwnedBy(source, owner, deep, this));
                    break;
                }

                case OpCode.ConstructBase:
                    OpConstructBase();
                    break;

                case OpCode.GetField:
                    OpGetField();
                    break;

                case OpCode.SetField:
                    OpSetField();
                    break;

                case OpCode.LoadThis:
                    OpLoadThis();
                    break;

                case OpCode.SetFieldOnThis:
                    OpSetFieldOnThis();
                    break;

                case OpCode.CallMethod:
                    OpCallMethod();
                    break;

                case OpCode.CallBaseMethod:
                    OpCallBaseMethod();
                    break;

                case OpCode.GetStaticField:
                    OpGetStaticField();
                    break;

                case OpCode.SetStaticField:
                    OpSetStaticField();
                    break;

                case OpCode.SetStaticFieldOnInit:
                {
                    // Like SetStaticField, but WITHOUT access modifier check
                    // (see OpCode.SetStaticFieldOnInit documentation) - ONLY for the
                    // one-time initialisation of a static field at
                    // program start (see Compiler.Compile), analogous to
                    // SetFieldOnThis for instance fields. The unit check
                    // stays (as with SetFieldOnThis) nevertheless - it
                    // applies independently of WHO writes.
                    string initClassName = _constants[ReadU16()].AsString();
                    string initFieldName = _constants[ReadU16()].AsString();
                    var initValue = Pop();
                    var initRc = ResolveClass(initClassName);
                    var initOwner = initRc.FindStaticFieldOwner(initFieldName);

                    if (initOwner == null)
                        throw new InvalidOperationException($"'{initClassName}' has no static field '{initFieldName}'.");

                    if (ExecutionMode != VmExecutionMode.Performance)
                    {
                        string? requiredUnitName = initOwner.FindFieldRequiredUnit(initFieldName);
                        if (requiredUnitName != null)
                        {
                            var requiredUnit = Values.Unit.Parse(requiredUnitName);
                            var actualUnit = initValue.Unit ?? Values.Unit.Unitless;
                            if (!actualUnit.Equals(requiredUnit))
                            {
                                ThrowUnitMismatch(requiredUnitName, actualUnit);
                                break;
                            }
                        }
                    }

                    initOwner.StaticFieldValues[initFieldName] = initValue;
                    break;
                }

                case OpCode.CallStaticMethod:
                    OpCallStaticMethod();
                    break;

                case OpCode.CallProtoWithThis:
                    OpCallProtoWithThis();
                    break;

                case OpCode.AddressOfLocal:
                {
                    int depth = ReadU16(); int slot = ReadU16();
                    Push(Value.MakePointer(new ScopeSlotPointerTarget(_currentScope.GetAncestor(depth), slot)));
                    break;
                }

                case OpCode.AddressOfGlobal:
                {
                    int slot = ReadU16();
                    if (slot < _sharedCount)
                        throw new InvalidOperationException("A fire thread cannot take a pointer to a global variable of the main program ('&').");
                    Push(Value.MakePointer(new ScopeSlotPointerTarget(_globalScope, slot)));
                    break;
                }

                case OpCode.AddressOfField:
                {
                    string fieldName = _constants[ReadU16()].AsString();
                    var obj = RequireObjectInstance(Pop(), "Address-of on field");
                    Push(Value.MakePointer(new FieldPointerTarget(obj, fieldName)));
                    break;
                }

                case OpCode.MakeArrayLiteralParts:
                {
                    int count = ReadU16();
                    uint mask = (uint)ReadU16();
                    mask |= (uint)ReadU16() << 16;
                    var arr = new ScriptArray(count);
                    for (int i = count - 1; i >= 0; i--) arr.Items[i] = Pop();
                    for (int i = 0; i < count; i++)
                        if ((mask >> i & 1) != 0 && LeafOf(arr.Items[i]) is { } part)
                            LeafOwnership.AttachPart(arr, part);   // the inner one now belongs to the outer one, no longer to the scope that has just created it
                    LeafOwnership.Adopt(arr, _currentScope);
                    Push(Value.MakeArray(arr));
                    break;
                }

                case OpCode.NewJagged:
                {
                    int ranks = ReadByte();
                    var sizes = new long[ranks];
                    for (int i = ranks - 1; i >= 0; i--) sizes[i] = Pop().AsInt();
                    foreach (long size in sizes)
                        if (size < 0) throw new InvalidOperationException($"Invalid array size {size}.");
                    var parts = new List<IOwnedLeaf>();
                    var outer = BuildJagged(sizes, 0, parts);
                    outer.Parts = parts.Count > 0 ? parts : null;
                    LeafOwnership.Adopt(outer, _currentScope);
                    Push(Value.MakeArray(outer));
                    break;
                }

                case OpCode.OwnValue:
                {
                    // `obj.field = <direct value>`: the object takes the value when it is fresh (owned by a scope of this call, or by nobody)
                    var value = Pop();
                    var owner = RequireObjectInstance(Pop(), "Assigning to a field");
                    if (LeafOf(value) is { } leaf)
                    {
                        if (leaf.LeafOwner == null || IsCurrentCallOwner(leaf.LeafOwner)) LeafOwnership.TakeTo(leaf, owner, this);
                    }
                    else if (value.Kind == ValueKind.Class)
                    {
                        var child = (ObjectInstance)value.AsObjectRef();
                        if (!child.IsDestroyed && !ReferenceEquals(child, owner) && IsCurrentCallOwner(child.Owner)) child.TakeTo(owner, this);
                    }
                    Push(value);
                    break;
                }

                case OpCode.HoistValue:
                    HoistValue(_stack[_sp - 1]);
                    break;

                case OpCode.TakeToScope:
                {
                    int depth = ReadU16();
                    if (ThrowIfDeadForTake(_stack[_sp - 1])) break;
                    OwnershipWalk.TakeValue(_stack[_sp - 1], depth == 0xFFFF ? _globalScope : _currentScope.GetAncestor(depth), this);
                    break;
                }

                case OpCode.TakeCheck:
                    ThrowIfDeadForTake(_stack[_sp - 1]);
                    break;

                case OpCode.TakeToObject:
                {
                    var holder = RequireObjectInstance(_stack[_sp - 2], "take");
                    if (ThrowIfDeadForTake(_stack[_sp - 1])) break;
                    OwnershipWalk.TakeValue(_stack[_sp - 1], holder, this);
                    break;
                }

                case OpCode.TakeToArray:
                {
                    var holder = _stack[_sp - 3];
                    if (IsDestroyedLeaf(holder)) { ThrowDestroyed(holder); break; }
                    if (ThrowIfDeadForTake(_stack[_sp - 1])) break;
                    if (holder.Kind == ValueKind.Array) OwnershipWalk.TakeValue(_stack[_sp - 1], holder.AsArray(), this);
                    break;
                }

                case OpCode.Delete:
                    DeleteValue(Pop());
                    break;

                case OpCode.RequireRefParam:
                {
                    int slot = ReadU16();
                    string paramName = _constants[ReadU16()].AsString();
                    if (_currentScope.GetSlot(slot).Kind != ValueKind.Pointer)
                        throw new InvalidOperationException($"Parameter '{paramName}' is declared 'ref': pass a variable, a field or an array element, not a value.");
                    break;
                }

                case OpCode.AddressOfIndex:
                {
                    long idx = Pop().AsInt();
                    var target = Pop();
                    if (IsDestroyedLeaf(target)) { ThrowDestroyed(target); break; }
                    if (target.Kind == ValueKind.Array)
                    {
                        var arr = target.AsArray();
                        if (!arr.TryGet(idx, out _)) { ThrowIndexOutOfBounds(idx, arr.Length); break; }
                        Push(Value.MakePointer(new ElementPointerTarget(arr, idx)));
                    }
                    else if (target.Kind == ValueKind.Buffer)
                    {
                        var buf = target.AsBuffer();
                        if (!buf.TryGet(idx, out _)) { ThrowIndexOutOfBounds(idx, buf.Length); break; }
                        Push(Value.MakePointer(new ElementPointerTarget(buf, idx)));
                    }
                    else throw new InvalidOperationException($"A 'ref' argument 'x[i]' expects an array or a byte buffer, not {target.Kind}.");
                    break;
                }

                case OpCode.PtrRead:
                {
                    var ptr = Pop();
                    if (TryReadPointer(ptr, out var read)) Push(read);
                    break;
                }

                case OpCode.PtrWrite:
                {
                    var value = Pop();
                    var ptr = Pop();
                    if (TryWritePointer(ptr, value)) Push(value);
                    break;
                }

                case OpCode.NewArray:
                    OpNewArray();
                    break;

                case OpCode.MakeArrayLiteral:
                    OpMakeArrayLiteral();
                    break;

                case OpCode.MakeBuffer:
                {
                    long size = Pop().AsInt();
                    if (size < 0)
                        throw new InvalidOperationException($"Invalid byte buffer size {size} (must be >= 0).");
                    var createdBuffer = new ByteBuffer((int)size, ByteConversions.HostByteOrder);
                    LeafOwnership.Adopt(createdBuffer, _currentScope);
                    Push(Value.MakeBuffer(createdBuffer));
                    break;
                }

                case OpCode.ArrayGet:
                    OpArrayGet();
                    break;

                case OpCode.ArraySet:
                    OpArraySet();
                    break;

                case OpCode.RegisterHandler:
                {
                    int templateIdx = ReadU16();
                    var template = _currentChunk.Handlers[templateIdx];
                    _handlers.Add(new ActiveHandler(_currentChunk, _frames.Count, _currentScope, template, _sp));
                    break;
                }

                case OpCode.UnregisterHandler:
                    _handlers.RemoveAt(_handlers.Count - 1);
                    break;

                case OpCode.EnterFinallyNormal:
                    Push(Value.MakeUndefined());
                    Push(Value.MakeInt(FinallyNormal));
                    break;

                case OpCode.PushJump:
                    Push(Value.MakeInt(ReadU16()));
                    Push(Value.MakeInt(FinallyJump));
                    break;

                case OpCode.EndFinally:
                {
                    int kind = (int)Pop().AsInt();
                    var payload = Pop();
                    switch (kind)
                    {
                        case FinallyNormal:
                            break;
                        case FinallyThrow:
                            ThrowException(payload);
                            break;
                        case FinallyReturn:
                            DoReturn(payload);
                            break;
                        case FinallyJump:
                            _ip = (int)payload.AsInt();
                            break;
                        case FinallyNestedReturn:
                        {
                            // End of a nested started finally (leave/terminate, see RunFinallyInlineNested): back to the caller
                            var frame = _frames.Pop();
                            _currentChunk = frame.ReturnChunk;
                            _ip = frame.ReturnIp;
                            _currentScope = frame.ReturnScope;
                            _currentThis = frame.ReturnThis;
                            break;
                        }
                    }
                    break;
                }

                case OpCode.Throw:
                {
                    var thrown = Pop();
                    ThrowException(thrown);
                    break;
                }

                case OpCode.IsInUnit:
                {
                    var unit = _currentChunk.Units[ReadU16()];
                    var v = Pop();
                    var valueUnit = v.Unit ?? Unit.Unitless;
                    Push(Value.MakeBool(valueUnit.IsCompatibleWith(unit)));
                    break;
                }

                case OpCode.IsOfType:
                {
                    string typeName = _constants[ReadU16()].AsString();
                    var v = Pop();
                    Push(Value.MakeBool(IsOfType(v, typeName)));
                    break;
                }

                case OpCode.IsFrom:
                {
                    bool transitive = ReadByte() != 0;
                    var ownerVal = Pop();
                    var operandVal = Pop();
                    var operandObj = RequireObjectInstance(operandVal, "'is from'/'is under'");
                    var ownerObj = RequireObjectInstance(ownerVal, "'is from'/'is under' (owner expression)");
                    bool result = transitive
                        ? operandObj.IsTransitivelyOwnedBy(ownerObj)
                        : operandObj.IsOwnedBy(ownerObj);
                    Push(Value.MakeBool(result));
                    break;
                }

                case OpCode.ResumeException:
                {
                    var resumeValue = Pop();
                    var excVal = Pop();
                    var excInstance = RequireObjectInstance(excVal, "resume");

                    if (!_pendingResumes.TryGetValue(excInstance, out var pending))
                        throw new InvalidOperationException(
                            "resume() was called, but this exception is not being handled right now " +
                            "(either it was already resumed, or there is no active catch for it).");

                    _pendingResumes.Remove(excInstance);
                    // the `catch` blocks that this exception has left run again after the `resume`
                    if (pending.Inner != null) foreach (var (innerExc, innerPending) in pending.Inner) _pendingResumes[innerExc] = innerPending;

                    // Unwind the CURRENTLY running catch context that resume()
                    // leaves - exactly as when entering the handler itself,
                    // therefore also works if resume() is called from a
                    // nested function call INSIDE the catch
                    // .
                    UnwindTo(pending.Handler.FrameDepthAtEntry, pending.Handler.TargetScope);

                    // Play back the frozen throw-site state exactly.
                    for (int i = pending.Continuation.Frames.Count - 1; i >= 0; i--)
                        _frames.Push(pending.Continuation.Frames[i]);

                    _currentChunk = pending.Continuation.Chunk;
                    _ip = pending.Continuation.Ip;
                    _currentScope = pending.Continuation.Scope;
                    _currentThis = pending.Continuation.This;

                    // Operands of the throw site back onto the stack (the catch context that resume() leaves is discarded)
                    _sp = pending.Handler.StackPointer;
                    foreach (var operand in pending.Continuation.Stack) Push(operand);

                    // The continued place is conceptually "still inside the
                    // try block" - re-arm the handler (see
                    // PendingResume comment), otherwise a renewed throw
                    // there no longer finds a matching catch and UnregisterHandler at the
                    // end of the try block accidentally removes someone else's
                    // entry.
                    while (_handlers.Count > pending.HandlerCount) _handlers.RemoveAt(_handlers.Count - 1);
                    _handlers.Add(pending.Handler);

                    Push(resumeValue); // that is the value that 'throw' now evaluates to
                    break;
                }

                case OpCode.ClearPendingResume:
                {
                    var excVal = Pop();
                    var excInstance = (ObjectInstance)excVal.AsObjectRef();
                    if (_pendingResumes.TryGetValue(excInstance, out var pending))
                    {
                        _pendingResumes.Remove(excInstance);
                        DiscardPending(pending);
                    }
                    break;
                }

                case OpCode.CheckLambdaSignature:
                {
                    // Checks Peek() (NOT Pop() - the value is
                    // still used normally right afterwards, e.g. via DeclareLocal/
                    // StoreLocal, which is only an additional validation
                    // BEFORE this further use) against the expected
                    // parameter count from the type annotation (see Ast.
                    // TypeRef.LambdaSignature, Compiler.EmitCheckLambdaSignature).
                    int expectedParamCount = ReadByte();
                    var v = Peek();
                    if (v.Kind != ValueKind.Lambda)
                        throw new InvalidOperationException(
                            $"Expected a lambda value (type 'lambda'), got: {v.Kind}.");
                    var lambdaVal = (LambdaValue)v.AsLambda();
                    if (lambdaVal.Proto.ParamCount != expectedParamCount)
                        throw new InvalidOperationException(
                            $"Lambda signature does not match: expected {expectedParamCount} parameters, " +
                            $"the lambda has {lambdaVal.Proto.ParamCount}.");
                    break;
                }

                case OpCode.CheckUnit:
                {
                    // Like CheckLambdaSignature: checks only Peek() (NOT Pop()),
                    // the value is still used normally right afterwards
                    // (see OpCode.CheckUnit documentation/Compiler.EmitCheckUnitIfNeeded).
                    // Unlike with CheckLambdaSignature (raw C# error), a
                    // mismatch here throws a REAL script exception, catchable via try/catch
                    // (see ThrowUnitMismatch) - explicitly wanted by the
                    // user per SPEC "Unit declarations".
                    // Skipped in performance mode - like every other
                    // "extra safety instead of speed" check in
                    // this VM (access modifiers, array/buffer bounds).
                    string requiredUnitName = _constants[ReadU16()].AsString();
                    if (ExecutionMode != VmExecutionMode.Performance)
                    {
                        var checkedValue = Peek();
                        var requiredUnit = Values.Unit.Parse(requiredUnitName);
                        var actualUnit = checkedValue.Unit ?? Values.Unit.Unitless;
                        if (!actualUnit.Equals(requiredUnit))
                        {
                            ThrowUnitMismatch(requiredUnitName, actualUnit);
                            break;
                        }
                    }
                    break;
                }

                case OpCode.Fire:
                {
                    // Starts a REAL thread (see Runtime.FireRuntime) -
                    // `_natives`/`_classes`/`_externs`/`_externSignatures`
                    // are shared with the NEW VM instance (immutable
                    // after compiling, safe across threads, see
                    // FireRuntime class comment) - fire itself does
                    // NOT block (no return values, no join, see
                    // docs/THREADING_DESIGN.md section 1).
                    int protoIdx = ReadU16();
                    int globalSlotCount = ReadU16();
                    int takingCount = ReadByte();
                    bool hasWith = ReadByte() != 0;
                    // Order reversed to compiling (stack!): with
                    // was pushed LAST, so lies on top, is popped FIRST;
                    // afterwards the taking values in REVERSED
                    // list order (last first) - both together
                    // brought into the right order again (see
                    // Compiler.CompileFireStmt).
                    Value? withValue = hasWith ? Pop() : null;
                    var takingValues = new Value[takingCount];
                    for (int i = takingCount - 1; i >= 0; i--) takingValues[i] = Pop();

                    // The globals of the main program are NOT copied: the thread reads them directly (under the lock) and changes them only in
                    // a section that the main program grants at `sync globals` (see Runtime.GlobalsBroker). At the first `fire`
                    // everything that the globals reach is for that purpose taken into the shared area (locking active). A thread that itself
                    // executes `fire` passes its connection on.
                    var broker = IsFireThreadVm ? _threadBroker : EnsureOwnerBroker();

                    var fireProto = _currentChunk.Functions[protoIdx];
                    // The new fire thread inherits the ExecutionMode of THIS VM -
                    // otherwise every `fire` thread would silently run again
                    // in the (slowest) debug mode, regardless of
                    // which mode the main program itself runs in (see
                    // VmExecutionMode documentation).
                    FireRuntime.FireVmTaking(
                        fireProto.Chunk, _natives, _classes, broker, globalSlotCount, takingValues, withValue,
                        executionMode: ExecutionMode);
                    break;
                }

                case OpCode.SyncGlobals:
                    Push(Value.MakeInt(SyncGlobalsNow()));
                    break;

                case OpCode.SetAutoSync:
                    _autoSync = ReadByte() != 0;
                    break;

                case OpCode.SetTimeout:
                {
                    var timeout = Pop();
                    if (!TimeNatives.TryTimeTicks(timeout, out long timeoutTicks, out var timeoutError))
                        throw new InvalidOperationException("#timeout " + timeoutError);
                    SetDefaultTimeout(TimeSpan.FromTicks(timeoutTicks));
                    break;
                }

                case OpCode.SectionEnter:
                    if (_threadBroker != null) EnterGlobalsSection(); // in the main program: no effect (it is the owner itself)
                    break;

                case OpCode.SectionExit:
                    if (_threadBroker != null && _sectionDepth > 0) ExitGlobalsSection();
                    break;

                case OpCode.PostGlobal:
                {
                    int jobArgCount = ReadByte();
                    var jobLambda = (LambdaValue)Pop().AsLambda();
                    var jobArgs = new Value[jobArgCount];
                    for (int i = jobArgCount - 1; i >= 0; i--) jobArgs[i] = Pop();

                    // The arguments belong to the job, not to this thread: objects are deeply copied, their owner is a holder scope that
                    // the main program releases after the run.
                    var holder = new Scope(null);
                    for (int i = 0; i < jobArgs.Length; i++)
                        if (jobArgs[i].Kind == ValueKind.Class) jobArgs[i] = ObjectCloner.Clone(jobArgs[i], holder, deep: true);

                    var postBroker = IsFireThreadVm ? _threadBroker! : EnsureOwnerBroker();
                    postBroker.PostJob(jobLambda, jobArgs, holder);
                    break;
                }

                case OpCode.Sync:
                {
                    // See Ast.SyncExpr documentation / Runtime.SyncEngine - `this`
                    // (the VM implements IDestructRunner, see
                    // class signature) is passed on
                    // as the destructor runner, so that a case-B object loss (see
                    // SyncEngine.SyncSingleValue) runs via the SAME
                    // destructor execution as the rest of this
                    // VM instance.
                    byte flags = ReadByte();
                    bool isTry = (flags & 1) != 0;
                    bool isFlat = (flags & 2) != 0;
                    var syncTarget = RequireObjectInstance(Pop(), "sync");

                    var syncResult = isFlat
                        ? SyncEngine.SyncFlat(syncTarget, blocking: !isTry, this)
                        : SyncEngine.Sync(syncTarget, blocking: !isTry, this);

                    Push(syncResult switch
                    {
                        SyncResult.Success => Value.MakeBool(true),
                        SyncResult.LockBusy => Value.MakeBool(false),
                        _ => Value.MakeUndefined(),
                    });
                    break;
                }

                case OpCode.Leave:
                    // The calling thread goes into the halt immediately (ShutdownSelfNow) - after `leave` no statement runs any more.
                    RequestLeave();
                    ShutdownSelfNow();
                    break;

                case OpCode.Terminate:
                {
                    // Even if another thread was faster (first call wins): this thread stops here.
                    var terminateValue = Pop();
                    RequestTerminate(terminateValue);
                    ShutdownSelfNow();
                    break;
                }

                case OpCode.RegisterThreadsCatch:
                {
                    int protoIdx = ReadU16();
                    bool hasType = ReadByte() != 0;
                    string? typeName = hasType ? _constants[ReadU16()].AsString() : null;
                    GlobalHandlers.RegisterThreadsCatch(typeName, _currentChunk.Functions[protoIdx]);
                    break;
                }

                case OpCode.RegisterTerminateCatch:
                {
                    int protoIdx = ReadU16();
                    GlobalHandlers.RegisterTerminateCatch(_currentChunk.Functions[protoIdx]);
                    break;
                }

                case OpCode.Process:
                {
                    var actorTarget = RequireObjectInstance(Pop(), "process");
                    ProcessOneMessage(actorTarget, blocking: true);
                    break;
                }

                case OpCode.TryProcess:
                {
                    var actorTarget = RequireObjectInstance(Pop(), "try process");
                    bool processed = ProcessOneMessage(actorTarget, blocking: false);
                    Push(Value.MakeBool(processed));
                    break;
                }

                default:
                    throw new InvalidOperationException($"Unknown opcode {op}");
            }
        }

        /// <summary>Common jump into the constructor proto for NewObject and
        /// NewObjectOwned - they differ only in which owner the new
        /// instance gets (already decided before this call).</summary>
        // -----------------------------------------------------------
        // extern linking: marshalling script value <-> real native type
        // -----------------------------------------------------------

        /// <summary>Converts script arguments into real native CLR types for an
        /// extern call. Value types (bool/int/float/char/string) are
        /// copied directly into their native counterpart. A pointer argument
        /// by contrast gets REAL unmanaged memory (Marshal.AllocHGlobal) -
        /// the current value is written into it, the native function
        /// gets the raw address (IntPtr), and via the returned
        /// cleanup delegate, after the call the (possibly changed by the
        /// native side) value is written back into the PointerTarget
        /// (copy-out) and the native memory is released again -
        /// real pointer marshalling instead of just passing an address through,
        /// since our pointers point to managed scope slots/fields, not
        /// to already-native addresses (see PointerTarget documentation).</summary>
        private (object?[] nativeArgs, List<Action> cleanups) MarshalArgsOut(Value[] args)
        {
            var nativeArgs = new object?[args.Length];
            var cleanups = new List<Action>();

            for (int i = 0; i < args.Length; i++)
            {
                var v = args[i];
                switch (v.Kind)
                {
                    case ValueKind.Bool:
                        nativeArgs[i] = v.AsBool();
                        break;
                    case ValueKind.Int:
                        nativeArgs[i] = v.AsInt();
                        break;
                    case ValueKind.Float:
                        nativeArgs[i] = v.AsFloat();
                        break;
                    case ValueKind.Char:
                        nativeArgs[i] = v.AsChar();
                        break;
                    case ValueKind.String:
                        nativeArgs[i] = v.AsString();
                        break;
                    case ValueKind.Undefined:
                        nativeArgs[i] = null;
                        break;
                    case ValueKind.Pointer:
                    {
                        var target = v.AsPointer();
                        Value current;
                        try { current = target.Read(); } catch (PointerRangeException ex) { throw new InvalidOperationException("A pointer argument of an extern function: " + ex.Message); }
                        IntPtr native = System.Runtime.InteropServices.Marshal.AllocHGlobal(8);
                        WriteNativeValue(native, current);
                        nativeArgs[i] = native;

                        var targetKind = current.Kind;
                        cleanups.Add(() =>
                        {
                            target.Write(ReadNativeValue(native, targetKind));
                            System.Runtime.InteropServices.Marshal.FreeHGlobal(native);
                        });
                        break;
                    }
                    default:
                        throw new InvalidOperationException(
                            $"Values of type {v.Kind} cannot be passed to an extern function.");
                }
            }

            return (nativeArgs, cleanups);
        }

        /// <summary>Converts the native return value of an extern function into
        /// a script Value - based on the actual CLR runtime type,
        /// since extern declarations have no strictly enforced return type
        /// (dynamic, like the rest of the language).</summary>
        private static Value MarshalResultIn(object? nativeResult) => nativeResult switch
        {
            null => Value.MakeUndefined(),
            bool b => Value.MakeBool(b),
            long l => Value.MakeInt(l),
            int i => Value.MakeInt(i),
            double d => Value.MakeFloat(d),
            float f => Value.MakeFloat(f),
            char c => Value.MakeChar(c),
            string s => Value.MakeString(s),
            _ => throw new InvalidOperationException(
                $"A return value of the native type {nativeResult.GetType().Name} cannot be converted to a script value " +
                "(supported: bool/int/float/char/string)."),
        };

        // -----------------------------------------------------------
        // extern linking: dynamic loading against '#extern "libName"'
        // -----------------------------------------------------------

        // IMPORTANT PITFALL (only discovered at runtime): Marshal.
        // GetDelegateForFunctionPointer rejects EVERY generic delegate type
        // - even an already fully CLOSED one like Func<long> (the
        // error message "The specified Type must not be a generic type" evidently checks
        // Type.IsGenericType, not ContainsGenericParameters). The
        // BCL delegates Action<...>/Func<...> are therefore
        // NOT usable for this purpose, although closed generic types are otherwise
        // treated like normal types everywhere. Instead a REAL, NON-generic delegate
        // type is generated here at runtime via System.Reflection.Emit (standard pattern: derive a TypeBuilder from
        // MulticastDelegate, define constructor + virtual Invoke method
        // with the desired signature, mark both as 'runtime-
        // implemented') - the same technique that .NET's
        // own C# compiler uses for a `delegate` keyword,
        // only at RUNTIME instead of compile time, since the signature is only
        // fixed by the parsed `extern` declaration.
        private static readonly System.Reflection.Emit.ModuleBuilder DynamicDelegateModule =
            System.Reflection.Emit.AssemblyBuilder
                .DefineDynamicAssembly(
                    new System.Reflection.AssemblyName("fireDynamicExterns"),
                    System.Reflection.Emit.AssemblyBuilderAccess.Run)
                .DefineDynamicModule("DynamicExterns");

        /// <summary>ModuleBuilder.DefineType/TypeBuilder.CreateType are, according to the
        /// .NET documentation, NOT safe for simultaneous calls from
        /// several threads - since DynamicDelegateModule is static (shared across ALL
        /// VM instances/threads), the ENTIRE
        /// type-building process (DefineType up to CreateType) must run exclusively for the duration of a
        /// single ResolveDynamicExtern call - this
        /// lock protects exactly that. Concerns only the (rare) initial construction
        /// of a dynamically linked delegate type, not the actual
        /// native call itself (which afterwards runs via the already finished,
        /// cached delegate).</summary>
        private static readonly object DynamicDelegateModuleLock = new();

        private static int _dynamicDelegateCounter;

        /// <summary>Resolves (and caches) the delegate for a dynamically
        /// linked `extern` call: loads the library (cached by
        /// name, `NativeLibrary.Load` is not exactly cheap), looks up the
        /// export, builds via Reflection.Emit a real, non-generic
        /// delegate type matching the script signature (`ExternSignature`,
        /// see BuildNonGenericDelegateType) and makes a callable delegate of it via `Marshal.
        /// GetDelegateForFunctionPointer`. The
        /// actual native call mechanics (calling convention, argument
        /// marshalling per parameter type) are thus completely taken over by the
        /// built-in .NET interop layer - here only the
        /// MATCHING delegate form is assembled at runtime, which would not be
        /// possible at compile time.</summary>
        private Delegate ResolveDynamicExtern(string externName, ExternSignature sig)
        {
            if (_dynamicExternDelegates.TryGetValue(externName, out var cached))
                return cached;

            string libName = sig.LibName!;
            if (!_loadedNativeLibraries.TryGetValue(libName, out var libHandle))
            {
                try
                {
                    libHandle = System.Runtime.InteropServices.NativeLibrary.Load(libName);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Native library '{libName}' could not be loaded (for extern '{externName}', " +
                        $"declared with '#extern \"{libName}\"'): {ex.Message}", ex);
                }
                _loadedNativeLibraries[libName] = libHandle;
            }

            IntPtr fnPtr;
            try
            {
                fnPtr = System.Runtime.InteropServices.NativeLibrary.GetExport(libHandle, externName);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Function '{externName}' was not found in '{libName}': {ex.Message}", ex);
            }

            var paramClrTypes = sig.ParamTypes.Select(t => MapExternClrType(t, externName)).ToArray();
            Type returnClrType = sig.ReturnType != null ? MapExternClrType(sig.ReturnType, externName) : typeof(void);
            Type delegateType = BuildNonGenericDelegateType(paramClrTypes, returnClrType, externName);

            Delegate del;
            try
            {
                del = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer(fnPtr, delegateType);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not build a callable delegate for extern '{externName}' from '{libName}': {ex.Message}", ex);
            }

            _dynamicExternDelegates[externName] = del;
            return del;
        }

        /// <summary>Builds a real, non-generic delegate type (see
        /// class comment above for WHY that is necessary instead of simply using
        /// Action&lt;...&gt;/Func&lt;...&gt;) with exactly the
        /// desired parameter/return signature.</summary>
        private static Type BuildNonGenericDelegateType(Type[] paramTypes, Type returnType, string externName)
        {
            // See DynamicDelegateModuleLock documentation: the ENTIRE construction (not only
            // DefineType) must run exclusively, since ModuleBuilder/TypeBuilder
            // according to the .NET documentation are not designed for simultaneous use by several
            // threads - relevant as soon as several VM instances
            // (a fire thread brings its own) link the same or different externs dynamically
            // for the first time at the same time.
            lock (DynamicDelegateModuleLock)
            {
                string typeName = $"fireExtern_{externName}_{System.Threading.Interlocked.Increment(ref _dynamicDelegateCounter)}";

                var typeBuilder = DynamicDelegateModule.DefineType(
                    typeName,
                    System.Reflection.TypeAttributes.Public | System.Reflection.TypeAttributes.Sealed
                        | System.Reflection.TypeAttributes.AnsiClass | System.Reflection.TypeAttributes.AutoClass,
                    typeof(MulticastDelegate));

                var ctor = typeBuilder.DefineConstructor(
                    System.Reflection.MethodAttributes.RTSpecialName | System.Reflection.MethodAttributes.HideBySig
                        | System.Reflection.MethodAttributes.Public,
                    System.Reflection.CallingConventions.Standard,
                    new[] { typeof(object), typeof(IntPtr) });
                ctor.SetImplementationFlags(System.Reflection.MethodImplAttributes.Runtime | System.Reflection.MethodImplAttributes.Managed);

                var invoke = typeBuilder.DefineMethod(
                    "Invoke",
                    System.Reflection.MethodAttributes.Public | System.Reflection.MethodAttributes.HideBySig
                        | System.Reflection.MethodAttributes.NewSlot | System.Reflection.MethodAttributes.Virtual,
                    returnType,
                    paramTypes);
                invoke.SetImplementationFlags(System.Reflection.MethodImplAttributes.Runtime | System.Reflection.MethodImplAttributes.Managed);

                return typeBuilder.CreateType()!;
            }
        }

        /// <summary>Maps a script type (parameter or return type of an
        /// `extern` declaration) to the matching CLR type for dynamic
        /// linking - the same set of supported types as with
        /// pointer marshalling (MarshalArgsOut/WriteNativeValue): bool/int/
        /// float/char/string directly, every pointer type as IntPtr (the real native
        /// address provided via MarshalArgsOut).</summary>
        private static Type MapExternClrType(TypeRef? t, string externName)
        {
            if (t == null)
                throw new InvalidOperationException(
                    $"extern '{externName}': a parameter/return type is missing or too unspecific " +
                    "for dynamic linking (bool/int/float/char/string or a pointer type is required).");
            if (t.PointerDepth > 0) return typeof(IntPtr);
            return t.BaseName switch
            {
                "bool" => typeof(bool),
                "int" => typeof(long),
                "float" => typeof(double),
                "char" => typeof(char),
                "string" => typeof(string),
                _ => throw new InvalidOperationException(
                    $"extern '{externName}': type '{t.BaseName}' cannot be linked dynamically " +
                    "(supported: bool/int/float/char/string/pointer)."),
            };
        }

        /// <summary>Writes a primitive script value into 8 bytes of native
        /// memory (enough for all supported primitive types).</summary>
        private static void WriteNativeValue(IntPtr ptr, Value v)
        {
            switch (v.Kind)
            {
                case ValueKind.Int:
                    System.Runtime.InteropServices.Marshal.WriteInt64(ptr, v.AsInt());
                    break;
                case ValueKind.Float:
                    var bytes = BitConverter.GetBytes(v.AsFloat());
                    System.Runtime.InteropServices.Marshal.Copy(bytes, 0, ptr, bytes.Length);
                    break;
                case ValueKind.Bool:
                    System.Runtime.InteropServices.Marshal.WriteByte(ptr, (byte)(v.AsBool() ? 1 : 0));
                    break;
                case ValueKind.Char:
                    System.Runtime.InteropServices.Marshal.WriteInt32(ptr, v.AsChar());
                    break;
                default:
                    throw new InvalidOperationException(
                        $"A pointer to a value of type {v.Kind} cannot be marshalled to an extern function " +
                        "(supported: int/float/bool/char).");
            }
        }

        private static Value ReadNativeValue(IntPtr ptr, ValueKind kind) => kind switch
        {
            ValueKind.Int => Value.MakeInt(System.Runtime.InteropServices.Marshal.ReadInt64(ptr)),
            ValueKind.Float => Value.MakeFloat(ReadNativeDouble(ptr)),
            ValueKind.Bool => Value.MakeBool(System.Runtime.InteropServices.Marshal.ReadByte(ptr) != 0),
            ValueKind.Char => Value.MakeChar((char)System.Runtime.InteropServices.Marshal.ReadInt32(ptr)),
            _ => throw new InvalidOperationException(
                $"Unsupported pointer target type {kind} when reading back from native memory."),
        };

        private static double ReadNativeDouble(IntPtr ptr)
        {
            var buf = new byte[8];
            System.Runtime.InteropServices.Marshal.Copy(ptr, buf, 0, 8);
            return BitConverter.ToDouble(buf, 0);
        }

        private void BeginConstruction(ObjectInstance instance, FunctionProto ctorProto, Value[] args, long copyMask = 0)
        {
            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, instance));

            var ctorScope = new Scope(_globalScope);
            foreach (var a in args) ctorScope.DefineSlot(a);
            if (copyMask != 0) ApplyCopyMask(ctorScope, copyMask, ctorProto.RefMask);

            _currentThis = instance;
            _currentScope = ctorScope;
            _currentChunk = ctorProto.Chunk;
            _ip = 0;
        }

        // -----------------------------------------------------------
        // Exceptions
        // -----------------------------------------------------------

        /// <summary>Searches - from the inside out - a registered handler with a
        /// matching catch clause. Handlers that are propagated past in the process
        /// are discarded (together with their finally, if present). If no
        /// handler is found, execution aborts with UncaughtScriptException.</summary>
        private void ThrowException(Value exceptionValue)
        {
            var excInstance = RequireObjectInstance(exceptionValue, "throw");
            if (CaptureErrorTrace) RecordThrowTrace();

            // The exception object still belongs to the throwing scope - it is
            // resolved at the (later, possibly delayed) unwinding. Without
            // this ownership transfer the exception itself would be
            // destroyed along with it before the catch block can read it.
            excInstance.TakeGlobal(_globalScope);

            // In a nested callback (CallLambdaInline) the handlers up to the lower bound belong to the caller.
            int handlerFloor = _callbackBoundaries.Count > 0 ? _callbackBoundaries.Peek().HandlerFloor : 0;
            while (_handlers.Count > handlerFloor)
            {
                var handler = _handlers[^1];
                _handlers.RemoveAt(_handlers.Count - 1);

                int? matchedAddr = handler.FinallyOnly ? null : FindMatchingCatch(handler.Template, excInstance);

                if (matchedAddr != null)
                {
                    // IMPORTANT: do NOT unwind destructively here (UnwindTo) -
                    // instead freeze the throw-site state
                    // (CaptureContinuation), so that a possible `resume()`
                    // can later jump back exactly here. Nothing is
                    // destroyed as long as it is not clear whether resume() is called
                    // or not (see ClearPendingResume).
                    var continuation = CaptureContinuation(handler.FrameDepthAtEntry);
                    var newPending = new PendingResume(continuation, handler, _handlers.Count);
                    newPending.Inner = TakePendingBelow(handler.FrameDepthAtEntry, excInstance);
                    _pendingResumes[excInstance] = newPending;

                    // The `catch` begins at the stack height of the `try`: the operands of the throw site (e.g. the enumerator of a `foreach` that
                    // was thrown from, or half-evaluated expressions of the caller of deeper frames) do not stay lying around as corpses and
                    // do not shift operands later. `resume()` plays them back.
                    if (_sp > handler.StackPointer)
                    {
                        continuation.Stack = new Value[_sp - handler.StackPointer];
                        Array.Copy(_stack, handler.StackPointer, continuation.Stack, 0, continuation.Stack.Length);
                        _sp = handler.StackPointer;
                    }

                    // If the `try` has a `finally`, it stays active for the duration of the `catch` block (an exception OUT of the catch must trigger it)
                    if (handler.Template.FinallyAddr != null)
                        _handlers.Add(new ActiveHandler(handler.Chunk, handler.FrameDepthAtEntry, handler.TargetScope, handler.Template, handler.StackPointer, finallyOnly: true));

                    var catchScope = new Scope(handler.TargetScope);
                    catchScope.DefineSlot(exceptionValue);
                    _currentScope = catchScope;
                    _currentChunk = handler.Chunk;
                    _ip = matchedAddr.Value;
                    return;
                }

                // No match at this handler - it is thus finally
                // discarded (no resume() possible for non-matching
                // handlers), so unwind quite normally destructively. The throw sites of the exceptions whose `catch` is left in the process, beforehand (SPEC 2.3: the throw site first).
                if (TakePendingBelow(handler.FrameDepthAtEntry, excInstance) is { } leftCatches)
                    foreach (var left in leftCatches) DiscardPending(left.Pending);
                UnwindTo(handler.FrameDepthAtEntry, handler.TargetScope);

                // The exception passes this `try` by - its `finally` runs (in the same chunk, with the local variables), and `EndFinally`
                // rethrows it afterwards (completion "exception"). The operands of the throw site lapse (no resume() across a finally).
                if (handler.Template.FinallyAddr is int finallyAddr)
                {
                    if (_sp > handler.StackPointer) _sp = handler.StackPointer;
                    Push(exceptionValue);
                    Push(Value.MakeInt(FinallyThrow));
                    _currentChunk = handler.Chunk;
                    _ip = finallyAddr;
                    return;
                }
            }

            // Unhandled in a callback: only the callback aborts (unwinding up to its caller, where CallLambdaInline
            // catches the error) - the program keeps running, and the caller of the callback sees no exception (SPEC 8.1.4).
            if (_callbackBoundaries.Count > 0)
            {
                var boundary = _callbackBoundaries.Peek();
                UnwindTo(boundary.FrameDepth, boundary.Scope);
                _sp = boundary.StackPointer;
                _callbackError = excInstance;
                UnhandledTrace = _lastThrowTrace;
                return;
            }

            // No handler in THIS VM instance matched. On a
            // fire thread (not the main thread) that means by design NOT
            // "abort the program", but "end this thread cleanly and
            // deliver the exception (without resumability - that has in any case never arisen as more than
            // purely LOCAL VM state, see _pendingResumes documentation)
            // to the main thread" (docs/THREADING_DESIGN.md 6.2).
            if (IsFireThreadVm)
            {
                UnhandledTrace = _lastThrowTrace;
                _pendingThreadExceptions.Enqueue(excInstance);
                RaiseSignal();
                // The global scope stays: the exception belongs to it (TakeGlobal above) and is still delivered to the main thread.
                UnwindForShutdown(); // _handlers is already empty at this point anyway, see loop above - equivalent to UnwindTo(0, _globalScope), but one call instead of code duplication.
                StopExecution();
                return;
            }

            // No handler in THIS VM instance matched - the program
            // stops here (see UnhandledException documentation: set instead of
            // thrown, Run() returns quite normally right afterwards via the next
            // CheckShutdownSignals check point).
            UnhandledException = excInstance;
            UnhandledTrace = _lastThrowTrace;
            StopExecution();
        }

        /// <summary>Freezes the current execution state by lifting the
        /// frames down to the target depth off `_frames` - WITHOUT releasing them
        /// (or the scopes passed through) via Release(). `this`
        /// is adjusted for the LIVE VM just as with UnwindTo,
        /// so that the handler context stands correctly afterwards; the
        /// returned SavedContinuation, by contrast, carries the ORIGINAL
        /// throw-site state (before the adjusting).</summary>
        private SavedContinuation CaptureContinuation(int targetFrameDepth)
        {
            var savedChunk = _currentChunk;
            var savedIp = _ip;
            var savedScope = _currentScope;
            var savedThis = _currentThis;
            var frames = new List<CallFrame>();

            while (_frames.Count > targetFrameDepth)
            {
                var frame = _frames.Pop();
                frames.Add(frame);
                _currentThis = frame.ReturnThis;
            }

            return new SavedContinuation(savedChunk, savedIp, savedScope, savedThis, frames);
        }

        /// <summary>Resolves a frozen, never continued throw-site
        /// continuation cleanly after the fact (ownership cascade incl.
        /// destructors) - mirrors exactly UnwindTo's logic, only on the
        /// SECURED (copied) data instead of on the LIVE VM state.</summary>
        /// <summary>Takes the exceptions out of `_pendingResumes` whose `catch` (handler at <paramref name="frameDepth"/> or deeper) is left by a new exception that is handled further out.</summary>
        private List<(ObjectInstance Exception, PendingResume Pending)>? TakePendingBelow(int frameDepth, ObjectInstance except)
        {
            List<(ObjectInstance, PendingResume)>? taken = null;
            foreach (var kv in _pendingResumes.ToList())
            {
                if (ReferenceEquals(kv.Key, except) || kv.Value.Handler.FrameDepthAtEntry < frameDepth) continue;
                (taken ??= new()).Add((kv.Key, kv.Value));
                _pendingResumes.Remove(kv.Key);
            }
            return taken;
        }

        /// <summary>Gives up a frozen throw site: first those of the inner exceptions, then this one (SPEC 2.3).</summary>
        private void DiscardPending(PendingResume pending)
        {
            if (pending.Inner != null) foreach (var (_, inner) in pending.Inner) DiscardPending(inner);
            DiscardContinuation(pending.Continuation, pending.Handler.TargetScope);
        }

        private void DiscardContinuation(SavedContinuation continuation, Scope targetScope)
        {
            var scope = continuation.Scope;
            int frameIdx = 0;

            while (frameIdx < continuation.Frames.Count || !ReferenceEquals(scope, targetScope))
            {
                bool atFrameBase = ReferenceEquals(scope!.Parent, _globalScope);
                scope.Release(this);

                if (atFrameBase && frameIdx < continuation.Frames.Count)
                {
                    scope = continuation.Frames[frameIdx].ReturnScope;
                    frameIdx++;
                }
                else
                {
                    scope = scope.Parent;
                }
            }
        }

        private int? FindMatchingCatch(HandlerTemplate template, ObjectInstance exc)
        {
            foreach (var (typeName, addr) in template.Catches)
            {
                if (typeName == null) return addr;
                if (InstanceMatchesClassName(exc, typeName)) return addr;
            }
            return null;
        }

        /// <summary>Walks up the base-class chain of an instance and checks for
        /// name equality with `typeName` - basis of typed `catch`
        /// AND of `is of` (see IsOfType). 'Exception' always matches
        /// (built-in base class without its own RuntimeClass, see
        /// Compiler.CompileClasses comment) - this deliberately applies across the board to
        /// every instance, not only to classes actually derived from 'Exception',
        /// since this information (the raw BaseNames list) is no longer available at
        /// RuntimeClass level.</summary>
        private bool InstanceMatchesClassName(ObjectInstance instance, string typeName)
        {
            if (typeName == "Exception") return true;
            if (!_classes.TryGetValue(instance.ClassName, out var rc)) return false;
            for (; rc != null; rc = rc.Base)
                if (rc.Name == typeName || rc.Interfaces.Contains(typeName)) return true;
            return false;
        }

        /// <summary>`value is of Type` (SPEC 6): for base-type names a simple
        /// kind comparison, for class names recursively over the base-class chain
        /// (InstanceMatchesClassName).</summary>
        private bool IsOfType(Value v, string typeName)
        {
            switch (typeName)
            {
                case "bool": return v.Kind == ValueKind.Bool;
                case "int": return v.Kind == ValueKind.Int;
                case "float": return v.Kind == ValueKind.Float;
                case "char": return v.Kind == ValueKind.Char;
                case "string": return v.Kind == ValueKind.String;
                case "undefined": return v.Kind == ValueKind.Undefined;
                case "class": return v.Kind == ValueKind.Class;
            }
            // Arrays and buffers are enumerable (GetEnumerator, foreach): they fulfil the interface IEnumerable of the prelude
            if (v.Kind is ValueKind.Array or ValueKind.Buffer && typeName == "IEnumerable") return true;
            if (v.Kind != ValueKind.Class) return false;
            return InstanceMatchesClassName((ObjectInstance)v.AsObjectRef(), typeName);
        }

        /// <summary>Unwinds scopes/frames until exactly `targetFrameDepth`/
        /// `targetScope` is reached - in the process Release() is called quite normally
        /// for every scope left (ownership cascade incl. destructors
        /// therefore also run correctly when aborting through an exception). Makes use of
        /// the fact that EVERY frame base scope always has the global
        /// scope directly as its parent (this is how Call/CallMethod/NewObject/etc. create their scopes) -
        /// that recognises a frame boundary without having to carry it along separately.</summary>
        private void UnwindTo(int targetFrameDepth, Scope targetScope)
        {
            while (_frames.Count > targetFrameDepth || !ReferenceEquals(_currentScope, targetScope))
            {
                bool atFrameBase = ReferenceEquals(_currentScope.Parent, _globalScope);
                _currentScope.Release(this);

                if (atFrameBase && _frames.Count > targetFrameDepth)
                {
                    var frame = _frames.Pop();
                    _currentChunk = frame.ReturnChunk;
                    _ip = frame.ReturnIp;
                    _currentScope = frame.ReturnScope;
                    _currentThis = frame.ReturnThis;
                }
                else
                {
                    _currentScope = _currentScope.Parent
                        ?? throw new InvalidOperationException(
                            "Unwind beyond the global scope (inconsistent handler state).");
                }
            }
        }

        /// <summary>Executes a `finally` block in the chunk `chunk` at `addr` nested (for `leave`/`terminate`, which let every open `finally` run,
        /// without returning): like a call, the block itself runs in the scope of the `try`; its `EndFinally` (completion 4) returns here.</summary>
        private void RunFinallyInlineNested(Chunk chunk, int addr)
        {
            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;

            Push(Value.MakeUndefined());
            Push(Value.MakeInt(FinallyNestedReturn));
            _currentChunk = chunk;
            _ip = addr;

            RunNestedUntil(targetDepth);
        }

        /// <summary>Calls a method on `obj` nested (see
        /// RunDestructor/RunFinallyNested) and returns its return value -
        /// or `null` if the call did NOT end normally via `Return`,
        /// but an exception redirected the execution completely elsewhere via a continuation jump (ThrowException/
        /// ResumeException)
        /// (e.g. into a `catch` outside this call). In that case
        /// NO return value was ever pushed, and the caller itself must not do anything
        /// any more (no push of its own, no further processing) - the
        /// execution already continues elsewhere. This is recognised
        /// NOT via the frame depth (which could by coincidence
        /// fit exactly again through a nesting, see e.g. a try/catch on exactly
        /// this level), but robustly via whether chunk/ip/scope after the
        /// nested execution have landed exactly at the initial state again
        /// - this is guaranteed only with a real, normal
        /// return (which restores exactly these three values from the pushed frame
        /// ).</summary>
        /// <summary>Takes a snapshot of ALL current values of the global
        /// scope of THIS VM - intended for native callback registration
        /// (see Runtime.FireRuntime.CallCallback), the same basic idea as
        /// the internal snapshot before a 'fire' block (OpCode.Fire), only
        /// triggered from OUTSIDE (host C# code) instead of by a script opcode.
        /// The caller MUST ensure that this call does
        /// NOT happen simultaneously with a running bytecode execution of THIS
        /// VM on ANOTHER thread (no built-in locking
        /// here, for the same race reason as with the 'fire' snapshot) - for
        /// the usual use (the host registers a callback,
        /// directly following the native registration call, while
        /// the script is thus standing in exactly this call, not
        /// running concurrently elsewhere) this is automatically given.</summary>
        public IReadOnlyList<Value> SnapshotGlobals()
        {
            var snapshot = new Value[_globalScope.SlotCount];
            for (int i = 0; i < snapshot.Length; i++)
                snapshot[i] = _globalScope.GetSlot(i);
            return snapshot;
        }

        /// <summary>Executes a lambda that a NATIVE call of this VM triggers (e.g. a window event that `Window.Tick` delivers),
        /// nested on this VM: it sees the real global variables (reading AND writing, like any lambda, SPEC 4.2),
        /// without a copy and without thread locks, and `leave`/`terminate` in it act on this program. Call only if the call
        /// happens on the thread of this VM (`VM.CurrentThreadVm`). Returns null if the lambda ended normally (or the program
        /// was ended), otherwise the unhandled exception of the lambda - which does NOT interrupt the caller: like for any callback
        /// the host reports it, the program keeps running.</summary>
        public ObjectInstance? CallLambdaInline(LambdaValue lambda, Value[] args)
        {
            CheckArity(lambda.Proto, args.Length);
            args = FillDefaultArgs(lambda.Proto, args, lambda.OnTarget);

            int frameDepth = _frames.Count;
            var callerScope = _currentScope;
            int stackPointer = _sp;

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;
            _callbackBoundaries.Push(new CallbackBoundary(frameDepth, callerScope, _handlers.Count, stackPointer));
            _callbackError = null;

            var funcScope = new Scope(_globalScope);
            foreach (var a in args) funcScope.DefineSlot(a);
            if (lambda.Captures != null) foreach (var c in lambda.Captures) funcScope.DefineSlot(c);

            _currentThis = lambda.OnTarget;
            _currentScope = funcScope;
            _currentChunk = lambda.Proto.Chunk;
            _ip = 0;

            try
            {
                RunNestedUntil(targetDepth);
            }
            catch
            {
                // A C# exception in the middle of the callback (e.g. an access outside the array in performance mode, which checks nothing):
                // restore the caller's state so that the program can keep running, and report the exception to the host.
                while (_frames.Count > frameDepth + 1) _frames.Pop();
                var callerFrame = _frames.Pop();
                _currentChunk = callerFrame.ReturnChunk;
                _ip = callerFrame.ReturnIp;
                _currentScope = callerFrame.ReturnScope;
                _currentThis = callerFrame.ReturnThis;
                _sp = stackPointer;
                var floor = _callbackBoundaries.Pop().HandlerFloor;
                while (_handlers.Count > floor) _handlers.RemoveAt(_handlers.Count - 1);
                throw;
            }
            _callbackBoundaries.Pop();

            // `leave`/`terminate` in the callback: unwind the program in an orderly way (on returning into the main loop the halt stands there).
            if (_shutdownDeferred) FinishDeferredShutdown();

            var error = _callbackError;
            _callbackError = null;
            if (error != null) return error;

            PopNestedResult(); // the (unused) return value of the lambda
            return null;
        }

        /// <summary>Calls a lambda as the ONLY execution of THIS VM
        /// instance - unlike CallMethodNested (nested into an
        /// already running main program) for a FRESHLY created
        /// VM without a "main program" of its own (see Runtime.FireRuntime.
        /// CallCallback, for native callbacks). `this` in the lambda body is
        /// `lambda.OnTarget`, as with any other lambda call (SPEC
        /// 4.2/Runtime.LambdaValue) - according to the
        /// language definition the lambda anyway sees only its own scope plus the global scope of THIS
        /// VM instance (`_globalScope`, passed here on construction), never the locals of any
        /// kind of
        /// "calling" context - the caller of this method is pure
        /// C# code, not a script scope.</summary>
        public Value CallLambdaEntry(LambdaValue lambda, Value[] args)
        {
            CheckArity(lambda.Proto, args.Length);
            args = FillDefaultArgs(lambda.Proto, args, lambda.OnTarget);

            var savedChunk = _currentChunk;
            var savedIp = _ip;
            var savedScope = _currentScope;

            _frames.Push(new CallFrame(savedChunk, savedIp, savedScope, _currentThis, null));
            int targetDepth = _frames.Count;

            var funcScope = new Scope(_globalScope);
            foreach (var a in args) funcScope.DefineSlot(a);
            if (lambda.Captures != null) foreach (var c in lambda.Captures) funcScope.DefineSlot(c);

            _currentThis = lambda.OnTarget;
            _currentScope = funcScope;
            _currentChunk = lambda.Proto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);

            // `leave`/`terminate` in the callback: unwind cleanly (no error, the callback returns nothing).
            if (_shutdownDeferred)
            {
                FinishDeferredShutdown();
                return Value.MakeUndefined();
            }

            // An unhandled error in the callback ends the VM (StopExecution): the host receives it as an exception that it
            // can deliberately catch (see FireRuntime.CallCallback) - instead of a return value missing here.
            if (_stopExecutionRequested)
                throw UnhandledException != null
                    ? new UncaughtScriptException(UnhandledException)
                    : new InvalidOperationException("The callback was terminated early.");

            return Pop();
        }

        private Value? CallMethodNested(ObjectInstance obj, string methodName, Value[] args)
        {
            var rc = ResolveClass(obj.ClassName);
            var (proto, declaringRcNested, accessNested) = rc.FindMethodWithAccess(methodName, args.Length);
            if (proto == null)
                throw new InvalidOperationException(
                    $"Method '{methodName}' not found on '{rc.Name}' (needed for a property or operator overload).");
            // Covers both property accesses (get_X/set_X, see VM.GetField/
            // SetField) and operator overloads (see
            // Parser.ParseOperatorMember) - operators never get an
            // explicit modifier (always Public), the check therefore
            // practically only applies here to properties.
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcNested!, accessNested))
            {
                ThrowAccessDenied(
                    $"'{methodName}' of '{declaringRcNested!.Name}' is {DescribeAccess(accessNested)} " +
                    "and cannot be accessed from here.");
                return null;
            }
            CheckArity(proto, args.Length);
            args = FillDefaultArgs(proto, args, obj);

            var savedChunk = _currentChunk;
            var savedIp = _ip;
            var savedScope = _currentScope;

            _frames.Push(new CallFrame(savedChunk, savedIp, savedScope, _currentThis, null));
            int targetDepth = _frames.Count;

            var scope = new Scope(_globalScope);
            foreach (var a in args) scope.DefineSlot(a);

            _currentThis = obj;
            _currentScope = scope;
            _currentChunk = proto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);

            bool completedNormally =
                ReferenceEquals(_currentChunk, savedChunk) && _ip == savedIp && ReferenceEquals(_currentScope, savedScope);
            return completedNormally ? Pop() : (Value?)null;
        }

        /// <summary>Like CallMethodNested, but for a STATIC method
        /// (SPEC "Static members") - no ObjectInstance, no
        /// bound 'this' (see OpCode.CallStaticMethod for the same
        /// reasoning). For the property getter fallback in GetStaticField
        /// (static 'get_X', analogous to CallMethodNested there for
        /// instance properties).</summary>
        private Value? CallStaticMethodNested(RuntimeClass rc, string methodName, Value[] args)
        {
            var (proto, declaringRcNested, accessNested) = rc.FindMethodWithAccess(methodName, args.Length);
            if (proto == null)
                throw new InvalidOperationException(
                    $"Static method '{methodName}' not found on '{rc.Name}' (needed for a property).");
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcNested!, accessNested))
            {
                ThrowAccessDenied(
                    $"'{methodName}' of '{declaringRcNested!.Name}' is {DescribeAccess(accessNested)} " +
                    "and cannot be accessed from here.");
                return null;
            }
            CheckArity(proto, args.Length);
            args = FillDefaultArgs(proto, args, null);

            var savedChunk = _currentChunk;
            var savedIp = _ip;
            var savedScope = _currentScope;
            var savedThis = _currentThis;

            _frames.Push(new CallFrame(savedChunk, savedIp, savedScope, savedThis, null));
            int targetDepth = _frames.Count;

            var scope = new Scope(_globalScope);
            foreach (var a in args) scope.DefineSlot(a);

            _currentThis = null;
            _currentScope = scope;
            _currentChunk = proto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);

            bool completedNormally =
                ReferenceEquals(_currentChunk, savedChunk) && _ip == savedIp && ReferenceEquals(_currentScope, savedScope);
            return completedNormally ? Pop() : (Value?)null;
        }

        /// <summary>Constructs a new instance of `rc` nested (like
        /// CallMethodNested) and returns it fully constructed - for
        /// exceptions created by the VM ITSELF (see ThrowIndexOutOfBounds),
        /// where there is no script `new` in the bytecode that could create the instance.
        /// Like CallMethodNested robust against an exception that occurs DURING
        /// construction (continuation jump instead of normal
        /// return) - in that (very rare) case there is no finished instance,
        /// this is treated as a hard internal error instead of trying
        /// to recursively build yet ANOTHER exception for it.</summary>
        private ObjectInstance ConstructNested(RuntimeClass rc, Value[] args)
        {
            var ctorProto = rc.FindConstructor(args.Length)
                ?? throw new InvalidOperationException(DescribeConstructorNotFound(rc, args.Length));
            var instance = new ObjectInstance(rc.Name, _currentScope, rc);
            if (rc.IsActor) instance.Mailbox = new ActorMailbox();
            args = FillDefaultArgs(ctorProto, args, instance);

            var savedChunk = _currentChunk;
            var savedIp = _ip;
            var savedScope = _currentScope;

            _frames.Push(new CallFrame(savedChunk, savedIp, savedScope, _currentThis, instance));
            int targetDepth = _frames.Count;

            var ctorScope = new Scope(_globalScope);
            foreach (var a in args) ctorScope.DefineSlot(a);

            _currentThis = instance;
            _currentScope = ctorScope;
            _currentChunk = ctorProto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);

            bool completedNormally =
                ReferenceEquals(_currentChunk, savedChunk) && _ip == savedIp && ReferenceEquals(_currentScope, savedScope);
            if (!completedNormally)
                throw new InvalidOperationException(
                    $"Internal error: construction of '{rc.Name}' was interrupted by an exception " +
                    "(exception while building a VM-internal exception instance).");

            Pop(); // Return at the end of the constructor pushes 'instance' itself (see BeginConstruction/frame.ConstructedInstance) - we already have it directly, discard here
            return instance;
        }

        /// <summary>Calls a native function. If it reports an invalid index
        /// (<see cref="NativeIndexOutOfRangeException"/>, e.g. `"abc".Substring(9)`), it is turned into a
        /// catchable `IndexOutOfBoundsException` of the script and `false` is returned (push NO result -
        /// execution already continues in the handler). A separate method instead of try/catch in the middle of
        /// Execute, so that its register allocation stays untouched.</summary>
        private bool CallNativeGuarded(int nativeIdx, Value[] args, out Value result)
        {
            try
            {
                result = _natives[nativeIdx](args);
                if (_nativeRedirected)
                {
                    // The native function (reflection) raised an exception that has already been redirected to a handler
                    _nativeRedirected = false;
                    return false;
                }
                AdoptFresh(result);
                return true;
            }
            catch (NativeIndexOutOfRangeException ex)
            {
                result = default;
                ThrowIndexOutOfBounds(ex.Index, ex.Length, ex.What);
                return false;
            }
        }

        /// <summary>Builds an `IndexOutOfBoundsException` instance (prelude) and
        /// throws it quite normally via ThrowException - turns an invalid
        /// array index into a real script exception catchable via `try`/`catch`
        /// instead of a raw C# error that would abort the
        /// whole program. Called from ArrayGet/ArraySet when
        /// ScriptArray/ByteBuffer.TryGet/TrySet returns `false` (deliberately no
        /// throw/catch there itself - see ScriptArray documentation, C++
        /// portability).</summary>
        private void ThrowIndexOutOfBounds(long index, int length, string what = "Array index")
        {
            var rc = ResolveClass("IndexOutOfBoundsException");
            string msg = $"{what} {index} out of range (length {length}).";
            var args = new[] { Value.MakeString(msg), Value.MakeInt(index), Value.MakeInt(length) };
            var instance = ConstructNested(rc, args);
            ThrowException(Value.MakeClassRef(instance));
        }

        /// <summary>An array/buffer that a built-in or native function has freshly created (still without owner) belongs to the current scope.</summary>
        private void AdoptFresh(Value v)
        {
            if (LeafOf(v) is { LeafOwner: null, IsDestroyed: false } leaf) LeafOwnership.Adopt(leaf, _currentScope);
        }

        /// <summary>Creates a multi-dimensional array; the inner arrays come in <paramref name="parts"/> (they belong to the outer one).</summary>
        private static ScriptArray BuildJagged(long[] sizes, int level, List<IOwnedLeaf> parts)
        {
            var array = new ScriptArray((int)sizes[level]);
            if (level + 1 < sizes.Length)
                for (int i = 0; i < array.Length; i++)
                {
                    var inner = BuildJagged(sizes, level + 1, parts);
                    parts.Add(inner);
                    array.Items[i] = Value.MakeArray(inner);
                }
            return array;
        }

        /// <summary>Does something belong to the scope of the current call (the current scope or a surrounding one of the running function; in the main program also the global one)?</summary>
        private bool IsCurrentCallOwner(IOwner? owner)
        {
            if (owner is not Scope scope) return false;
            if (ReferenceEquals(scope, _currentScope) || OwnsWithinCall(scope)) return true;
            return _frames.Count == 0 && scope.IsGlobal;
        }

        /// <summary>`x = value` with a variable in an outer scope: if the value belongs to an inner block (loop, `if`) of the running function, it moves
        /// into its function scope - never out of the function (in the main program: into the global scope).</summary>
        private void HoistValue(Value v)
        {
            IOwner? owner = v.Kind switch
            {
                ValueKind.Class => ((ObjectInstance)v.AsObjectRef()).Owner,
                ValueKind.Array => v.AsArray().LeafOwner,
                ValueKind.Buffer => v.AsBuffer().LeafOwner,
                _ => null,
            };
            if (owner is not Scope scope || !OwnsWithinCall(scope)) return;
            Scope target = _globalScope;
            if (_frames.Count > 0)
            {
                target = _currentScope;
                while (target.Parent != null && !target.Parent.IsGlobal) target = target.Parent;
            }
            if (ReferenceEquals(scope, target)) return;
            if (v.Kind == ValueKind.Class)
            {
                var obj = (ObjectInstance)v.AsObjectRef();
                if (!obj.IsDestroyed) obj.ReparentTo(target);
            }
            else if (LeafOf(v) is { IsDestroyed: false } leaf) LeafOwnership.Reparent(leaf, target);
        }

        /// <summary>The value as an ownable leaf (array or buffer), otherwise null.</summary>
        private static IOwnedLeaf? LeafOf(Value v) => v.Kind switch
        {
            ValueKind.Array => v.AsArray(),
            ValueKind.Buffer => v.AsBuffer(),
            _ => null,
        };

        /// <summary>Is the value a destroyed array/buffer whose use is checked (not in performance mode)?</summary>
        private bool IsDestroyedLeaf(Value v) =>
            v.Kind is ValueKind.Array or ValueKind.Buffer && ExecutionMode != VmExecutionMode.Performance
            && (v.Kind == ValueKind.Array ? v.AsArray().IsDestroyed : v.AsBuffer().IsDestroyed);

        /// <summary>Use of a destroyed array/buffer: a catchable `DestroyedException` (prelude).</summary>
        private void ThrowDestroyed(Value leaf)
        {
            var rc = ResolveClass("DestroyedException");
            string what = leaf.Kind == ValueKind.Buffer ? "buffer" : "array";
            var instance = ConstructNested(rc, new[] { Value.MakeString($"Access to a destroyed {what}.") });
            ThrowException(Value.MakeClassRef(instance));
        }

        /// <summary>Use of a destroyed object (SPEC 2.5): a catchable `DestroyedException`. Not in performance mode.</summary>
        /// <summary>`*p` (SPEC 8.3): outside the range (element beyond the bounds, offset on a variable, destroyed array) the catchable exception is thrown - false.</summary>
        private bool TryReadPointer(Value ptr, out Value value)
        {
            try { value = ptr.AsPointer().Read(); return true; }
            catch (PointerRangeException ex) { value = Value.MakeUndefined(); ThrowPointerRange(ex); return false; }
        }

        private bool TryWritePointer(Value ptr, Value value)
        {
            try { ptr.AsPointer().Write(value); return true; }
            catch (PointerRangeException ex) { ThrowPointerRange(ex); return false; }
        }

        private void ThrowPointerRange(PointerRangeException ex)
        {
            if (ex.Destroyed)
            {
                var rc = ResolveClass("DestroyedException");
                ThrowException(Value.MakeClassRef(ConstructNested(rc, new[] { Value.MakeString(ex.Message) })));
                return;
            }
            ThrowIndexOutOfBounds(ex.Index, (int)ex.Length, ex.What);
        }

        /// <summary>The value behind the pointer that the caller passed for a `ref` parameter if the callee knows none; outside the range: undefined (the exception is thrown).</summary>
        private Value ReadRefArgument(Value ptr) => TryReadPointer(ptr, out var v) ? v : Value.MakeUndefined();

        private bool IsDeadObject(ObjectInstance obj) => obj.IsDead && ExecutionMode != VmExecutionMode.Performance;

        private void ThrowDestroyedObject(ObjectInstance obj)
        {
            var rc = ResolveClass("DestroyedException");
            var instance = ConstructNested(rc, new[] { Value.MakeString("Access to a destroyed object.") });
            ThrowException(Value.MakeClassRef(instance));
        }

        private void ThrowAccessDenied(string message)
        {
            var rc = ResolveClass("AccessDeniedException");
            var args = new[] { Value.MakeString(message) };
            var instance = ConstructNested(rc, args);
            ThrowException(Value.MakeClassRef(instance));
        }

        /// <summary>Builds a `UnitMismatchException` instance (prelude) and
        /// throws it quite normally via ThrowException (SPEC "Unit
        /// declarations") - turns a unit violation in a
        /// declaration with an explicit `: unit` into a real script exception catchable via
        /// `try`/`catch`. Called from
        /// OpCode.CheckUnit (local/global variables, parameters) and
        /// SetField/SetFieldOnThis (fields, see there for the reason
        /// why this is checked there instead of at compile time).</summary>
        private void ThrowUnitMismatch(string requiredUnitName, Values.Unit actualUnit)
        {
            var rc = ResolveClass("UnitMismatchException");
            string actualDescription = actualUnit.IsUnitless ? "(no unit)" : actualUnit.ToString();
            string msg = $"Expected unit '{requiredUnitName}', got: {actualDescription}.";
            var args = new[] { Value.MakeString(msg), Value.MakeString(requiredUnitName), Value.MakeString(actualDescription) };
            var instance = ConstructNested(rc, args);
            ThrowException(Value.MakeClassRef(instance));
        }

        /// <summary>Checks whether the CURRENTLY executing code is allowed, according to the
        /// access modifier, to access a member that
        /// `declaringRc` itself declared. `Public` (or no
        /// entry at all - backward compatibility) is always allowed.
        ///
        /// "Who is accessing right now" is `_currentChunk.OwnerClass` - the
        /// class whose method/constructor/property accessor is CURRENTLY
        /// executing (see Chunk.OwnerClass documentation). DELIBERATELY NOT
        /// the concrete class of `_currentThis`: a `Derived` instance that calls an
        /// inherited, not overridden `Base` method (or whose
        /// construction is currently running through `Base`'s own constructor code via
        /// ConstructBase), has `this` bound concretely as `Derived`
        /// although `Base`'s own code is running - for "may THIS
        /// code access Base's private member" what counts is WHOSE CODE
        /// is running, not which concrete class the instance belongs to (otherwise
        /// e.g. every construction of a derived class would fail on a
        /// private field initialiser of the base class - exactly
        /// this error was found and corrected here).</summary>
        private bool IsMemberAccessAllowed(RuntimeClass declaringRc, AccessModifier access)
        {
            if (access == AccessModifier.Public) return true;

            var callerRc = _currentChunk.OwnerClass;
            // If the access goes via the reflection library (`Reflect.Get` &amp; co.), the code that called it counts
            if (callerRc is { IsReflectionHelper: true }) callerRc = ReflectionCallerClass();
            if (callerRc == null) return false;

            if (access == AccessModifier.Private)
                return ReferenceEquals(callerRc, declaringRc);

            // Protected: callerRc itself or any class derived from it.
            for (var rc = callerRc; rc != null; rc = rc.Base)
                if (ReferenceEquals(rc, declaringRc)) return true;
            return false;
        }

        private static string DescribeAccess(AccessModifier access) => access switch
        {
            AccessModifier.Private => "private",
            AccessModifier.Protected => "protected",
            _ => "public",
        };

        /// <summary>Basis for EVERY arithmetic/bitwise/comparison
        /// operation (see the corresponding OpCode handlers): if the LEFT
        /// operand is an object WITH a matching operator overload
        /// method (see Ast.MethodDecl naming convention "operator+" etc.,
        /// generated by Parser.ParseOperatorMember), THAT one is called
        /// nested (`this` = the left operand, one parameter = the
        /// right one) - EXACTLY the same pattern that ArrayGet/ArraySet have always
        /// used for 'GetIndex'/'SetIndex', generalised here for ALL
        /// binary operators. Otherwise (no object, or an object without a
        /// matching method) the built-in operation `op`. Deliberately ONLY the
        /// LEFT operand is checked for an overload (no Python
        /// `__radd__` equivalent for the right operand) - see the
        /// ParseOperatorMember documentation for the reasoning.</summary>
        /// <summary>Built-in methods on primitive values (string/char/
        /// int/buffer, see SPEC 8.10 for the complete table) -
        /// 'obj.Method()' normally goes to an ObjectInstance (see
        /// OpCode.CallMethod); for every other ValueKind this list
        /// instead checks the built-in conversions. Returns false (instead of
        /// throwing) if there is no hit - the caller then decides
        /// itself how to report that.</summary>
        private static bool TryCallBuiltinMethod(Value target, string methodName, Value[] args, out Value result)
        {
            result = default;
            switch (target.Kind)
            {
                case ValueKind.String:
                    if (methodName == "ToBytes" && args.Length == 0)
                    {
                        result = Value.MakeBuffer(ByteConversions.AsciiEncode(target.AsString()));
                        return true;
                    }
                    if (methodName == "ToUnicode" && args.Length == 1)
                    {
                        result = Value.MakeBuffer(ByteConversions.UnicodeEncodeString(target.AsString(), (int)args[0].AsInt()));
                        return true;
                    }
                    return false;

                case ValueKind.Char:
                    if (methodName == "ToByte" && args.Length == 0)
                    {
                        result = Value.MakeInt((byte)target.AsChar(), width: NumericWidth.W8);
                        return true;
                    }
                    if (methodName == "ToUnicode" && args.Length == 1)
                    {
                        result = Value.MakeBuffer(ByteConversions.UnicodeEncodeChar(target.AsChar(), (int)args[0].AsInt()));
                        return true;
                    }
                    return false;

                case ValueKind.Int:
                    if (methodName == "ToChar" && args.Length == 0)
                    {
                        result = Value.MakeChar((char)(byte)target.AsInt());
                        return true;
                    }
                    return false;

                case ValueKind.Buffer:
                {
                    var buf = target.AsBuffer();
                    if (methodName == "ToString" && args.Length == 0)
                    {
                        result = Value.MakeString(ByteConversions.AsciiDecode(buf));
                        return true;
                    }
                    if (methodName == "ToUnicode" && args.Length == 0)
                    {
                        result = Value.MakeString(ByteConversions.UnicodeDecodeString(buf, 2));
                        return true;
                    }
                    if (methodName == "ToUnicode" && args.Length == 1)
                    {
                        result = Value.MakeString(ByteConversions.UnicodeDecodeString(buf, (int)args[0].AsInt()));
                        return true;
                    }
                    if (methodName == "ToUnicodeChar" && args.Length == 0)
                    {
                        result = Value.MakeChar(ByteConversions.UnicodeDecodeChar(buf, 2));
                        return true;
                    }
                    if (methodName == "ToUnicodeChar" && args.Length == 1)
                    {
                        result = Value.MakeChar(ByteConversions.UnicodeDecodeChar(buf, (int)args[0].AsInt()));
                        return true;
                    }
                    // Endianness (see ByteBuffer documentation): if the
                    // CURRENT order is already right, returns a pure copy (no
                    // byte swap needed); otherwise a mirrored copy with the
                    // new order. The original stays unchanged in EVERY
                    // case - like every other "returns a new value"
                    // conversion in this language.
                    if (methodName == "ToLittleEndian" && args.Length == 0)
                    {
                        result = Value.MakeBuffer(buf.Order == ByteOrder.Little ? buf.Clone() : buf.Reversed(ByteOrder.Little));
                        return true;
                    }
                    if (methodName == "ToBigEndian" && args.Length == 0)
                    {
                        result = Value.MakeBuffer(buf.Order == ByteOrder.Big ? buf.Clone() : buf.Reversed(ByteOrder.Big));
                        return true;
                    }
                    return false;
                }

                default:
                    return false;
            }
        }

        private void BinaryNumericOrOperator(Func<Value, Value, Value> op, string operatorMethodName)
        {
            var b = Pop();
            var a = Pop();

            // `"text" + object` / `object + "text"`: an object with `ToString()` enters the concatenation with this text
            if (operatorMethodName == "operator+"
                && ((a.Kind == ValueKind.String && b.Kind == ValueKind.Class)
                    || (b.Kind == ValueKind.String && a.Kind == ValueKind.Class
                        && ResolveClass(((ObjectInstance)a.AsObjectRef()).ClassName).FindMethod("operator+", 1) == null)))
            {
                bool objectIsRight = a.Kind == ValueKind.String;
                var text = StringifyForText(objectIsRight ? b : a);
                if (text == null) return;
                if (objectIsRight) b = Value.MakeString(text); else a = Value.MakeString(text);
            }

            if (a.Kind == ValueKind.Class)
            {
                var obj = (ObjectInstance)a.AsObjectRef();
                var rc = ResolveClass(obj.ClassName);
                if (rc.FindMethod(operatorMethodName, 1) != null)
                {
                    var result = CallMethodNested(obj, operatorMethodName, new[] { b });
                    // null == the overload was left through a thrown
                    // exception (see CallMethodNested documentation) -
                    // then do NOT push, execution already continues
                    // elsewhere.
                    if (result != null) Push(result.Value);
                    return;
                }
            }
            Push(op(a, b));
        }

        private RuntimeClass ResolveClass(string name) =>
            _classes.TryGetValue(name, out var rc)
                ? rc
                : throw new InvalidOperationException($"Unknown class '{name}' at run time.");

        private static ObjectInstance RequireObjectInstance(Value v, string context)
        {
            if (v.Kind != ValueKind.Class)
                throw new InvalidOperationException($"{context} on a value of type {v.Kind} which is not an object.");
            return (ObjectInstance)v.AsObjectRef();
        }

        private static void CheckArity(FunctionProto proto, int argCount)
        {
            if (argCount == proto.ParamCount) return;
            if (argCount < proto.ParamCount && RuntimeClass.AllTrailingHaveDefaults(proto, argCount)) return;
            throw new InvalidOperationException(
                $"Wrong number of arguments: expected {proto.ParamCount}" +
                (argCount < proto.ParamCount ? " (the missing parameters have no default value)" : "") +
                $", got {argCount}.");
        }

        /// <summary>Builds, if needed, the complete argument array for a
        /// call against `proto` - supplements missing TRAILING parameters with
        /// their evaluated default values (see FunctionProto.ParamDefaults,
        /// Ast.LambdaParam.DefaultValue documentation). The caller must BEFOREHAND have
        /// ensured via CheckArity/RuntimeClass.FindMethod/FindConstructor
        /// that enough default values are present. `thisForDefaults`
        /// is bound when evaluating (e.g. for a default value like
        /// `= this.something`) - null if no `this` makes
        /// sense at this point (e.g. free-standing lambdas without an `on` binding).</summary>
        private Value[] FillDefaultArgs(FunctionProto proto, Value[] suppliedArgs, object? thisForDefaults)
        {
            if (suppliedArgs.Length == proto.ParamCount) return suppliedArgs;

            var result = new Value[proto.ParamCount];
            Array.Copy(suppliedArgs, result, suppliedArgs.Length);
            for (int i = suppliedArgs.Length; i < proto.ParamCount; i++)
            {
                var defaultProto = i < proto.ParamDefaults.Count ? proto.ParamDefaults[i] : null;
                if (defaultProto == null)
                    throw new InvalidOperationException(
                        $"Internal error: parameter {i} of the call target has no default value " +
                        "(CheckArity should have caught this).");
                result[i] = EvaluateDefaultNested(defaultProto, thisForDefaults);
            }
            return result;
        }

        /// <summary>Evaluates a default-value proto (0 arguments) nested
        /// - the same technique as CallMethodNested/ConstructNested (see
        /// there for the explanation why it is checked robustly against an exception
        /// that leaves the evaluation via a continuation jump: for
        /// a simple default-value expression that is indeed a very rare
        /// case, but not fundamentally impossible - e.g. a default value
        /// that itself contains a method call that throws).</summary>
        private Value EvaluateDefaultNested(FunctionProto defaultProto, object? thisForDefaults)
        {
            var savedChunk = _currentChunk;
            var savedIp = _ip;
            var savedScope = _currentScope;
            var savedThis = _currentThis;

            _frames.Push(new CallFrame(savedChunk, savedIp, savedScope, savedThis, null));
            int targetDepth = _frames.Count;

            _currentThis = thisForDefaults;
            _currentScope = new Scope(_globalScope);
            _currentChunk = defaultProto.Chunk;
            _ip = 0;

            RunNestedUntil(targetDepth);

            bool completedNormally =
                ReferenceEquals(_currentChunk, savedChunk) && _ip == savedIp && ReferenceEquals(_currentScope, savedScope);
            if (!completedNormally)
                throw new InvalidOperationException(
                    "Internal error: evaluating a default value was interrupted by an exception.");

            _currentThis = savedThis;
            return Pop();
        }

        /// <summary>Builds a helpful error message for a failed
        /// method call (RuntimeClass.FindMethod found nothing) -
        /// distinguishes "method does not exist under this name at all" from
        /// "method exists, but no overload with this argument count"
        /// (and in the second case also lists the argument counts
        /// actually present, across the whole base-class chain).</summary>
        private static string DescribeMethodNotFound(RuntimeClass rc, string methodName, int argCount)
        {
            var arities = new List<int>();
            for (var c = rc; c != null; c = c.Base)
                if (c.Methods.TryGetValue(methodName, out var overloads))
                    arities.AddRange(overloads.Select(p => p.ParamCount));

            if (arities.Count == 0)
                return $"Method '{methodName}' not found on '{rc.Name}'.";

            var distinctArities = arities.Distinct().OrderBy(x => x);
            return $"Method '{methodName}' on '{rc.Name}' has no overload with {argCount} argument(s) " +
                   $"(available: {string.Join(", ", distinctArities)} argument(s)).";
        }

        /// <summary>Analogous to DescribeMethodNotFound, for constructors (no
        /// base-class chain, see RuntimeClass.Constructors documentation).</summary>
        private static string DescribeConstructorNotFound(RuntimeClass rc, int argCount)
        {
            if (rc.Constructors.Count == 0)
                return $"Class '{rc.Name}' has no constructor."; // should never occur, at least 1 is always synthesised
            var arities = rc.Constructors.Keys.OrderBy(x => x);
            return $"Class '{rc.Name}' has no constructor with {argCount} argument(s) " +
                   $"(available: {string.Join(", ", arities)} argument(s)).";
        }

        private static ValueKind TagToKind(TypeTag tag) => tag switch
        {
            TypeTag.Bool => ValueKind.Bool,
            TypeTag.Int => ValueKind.Int,
            TypeTag.Float => ValueKind.Float,
            TypeTag.Char => ValueKind.Char,
            TypeTag.String => ValueKind.String,
            _ => throw new InvalidOperationException($"Unknown TypeTag {tag}"),
        };

        /// <summary>Wraps an 'on' target value for LambdaValue.OnTarget. For
        /// an object reference the ObjectInstance directly, otherwise the Value
        /// boxed as object.</summary>
        private static object BoxValueForOnTarget(Value v) =>
            v.Kind == ValueKind.Class ? v.AsObjectRef() : v;

        // -----------------------------------------------------------
        // Stack- & Code-Zugriff
        // -----------------------------------------------------------
        private void Push(Value v)
        {
            if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
            _stack[_sp++] = v;
        }

        private Value Pop() => _stack[--_sp];

        private Value Peek() => _stack[_sp - 1];

        private byte ReadByte() => _code[_ip++];

        private int ReadU16()
        {
            var code = _code;
            int ip = _ip;
            _ip = ip + 2;
            return code[ip] | (code[ip + 1] << 8);
        }
    }
}
