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
    /// <summary>An error found inside an instruction that the script can catch (a call with the wrong number of arguments: `ArgumentException`, a member that
    /// does not exist: `MissingMemberException`). Thrown where it is found, turned into the script exception by the execution loop (VM.RaiseScriptError).</summary>
    public sealed class ScriptErrorException : InvalidOperationException
    {
        public string ClassName { get; }
        /// <summary>An exception object that already exists (thrown by a conversion function of a unit): it is thrown as it is.</summary>
        public ObjectInstance? Instance { get; }
        public ScriptErrorException(string className, string message) : base(message) { ClassName = className; }
        public ScriptErrorException(ObjectInstance instance) : base(instance.Fields.TryGetValue("message", out var m) ? m.ToString() : instance.ClassName)
        {
            ClassName = instance.ClassName;
            Instance = instance;
        }
    }

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

    public sealed partial class VM : IDestructRunner, IDanglingReporter
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
        // leave/terminate (docs/spec/THREADING_DESIGN.md section 6) - cooperative
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

        // A unit with conversion functions (`coerce to W => ...`) converts by calling them on the VM of the calling thread.
        static VM()
        {
            Unit.ConverterCall = (className, method, value) =>
                (_currentThreadVm ?? throw new InvalidOperationException("A unit with conversion functions can only be converted while the program runs."))
                    .CallUnitConverter(className, method, value);
            fire.Standard.JsonMethods.Host = () => _currentThreadVm;
            fire.Standard.LibraryMethods.Implementation = args =>
                (_currentThreadVm ?? throw new InvalidOperationException("Native libraries can only be loaded while the program runs.")).LibraryCall(args);
            fire.Standard.AwaitMethods.Implementation = args =>
                (_currentThreadVm ?? throw new InvalidOperationException("'await [ ... ]' needs a running program.")).AwaitRun(args);
            fire.Standard.LambdaHandover.Implementation = args =>
                (_currentThreadVm ?? throw new InvalidOperationException("'handover' needs a running program.")).LambdaHandoverNow(args);
            fire.Standard.CancelMethods.Implementation = (name, args) =>
                (_currentThreadVm ?? throw new InvalidOperationException("Cancellation needs a running program.")).CancelNative(name, args);
            fire.Standard.FdWaitMethods.Implementation = args =>
                (_currentThreadVm ?? throw new InvalidOperationException("__fd_wait needs a running program.")).FdWaitNative(args);
            fire.Standard.CompletionMethods.Implementation = (name, args) =>
                (_currentThreadVm ?? throw new InvalidOperationException("A Completion needs a running program.")).CompletionNative(name, args);
        }

        /// <summary>`__await_run(lambdas, res, fails)` (fire.Standard.AwaitMethods): calls every lambda with (res, fails, i), one after the other, nested like a
        /// getter. The lambdas catch the exceptions of their calls themselves; a `leave`/`terminate` in one goes on as usual.</summary>
        private Value AwaitRun(Value[] args)
        {
            if (args.Length < 3 || args[0].Kind != ValueKind.Array) return Value.MakeUndefined();
            var items = args[0].AsArray().Items;
            for (int i = 0; i < items.Length; i++)
            {
                if (items[i].Kind != ValueKind.Lambda) continue;
                var result = CallLambdaNested((LambdaValue)items[i].AsLambda(), new[] { args[1], args[2], Value.MakeInt(i) });
                if (result == null) { _nativeRedirected = true; return default; }
            }
            return Value.MakeUndefined();
        }

        /// <summary>Calls a conversion function of a unit (`coerce to`/`coerce from`) nested on this VM, like a getter or setter: in the middle of the
        /// instruction that converts, with the globals of the program. An exception in it is thrown where the conversion happened.</summary>
        private double CallUnitConverter(string className, string method, double value)
        {
            if (!_classes.TryGetValue(className, out var rc) || rc.FindMethodWithAccess(method, 1).Proto is not { } proto)
                throw new InvalidOperationException($"The conversion function '{className}.{method}' of a unit is missing.");
            string symbol = className.StartsWith("__unit_", StringComparison.Ordinal) ? className.Substring(7) : className;
            var error = CallInline(new LambdaValue(proto, null), new[] { Value.MakeFloat(value) }, out var result);
            if (error != null) throw new ScriptErrorException(error);
            if (_stopExecutionRequested) return double.NaN;   // (`leave`/`terminate` in the conversion: the program ends right after the instruction)
            return result.Kind switch
            {
                ValueKind.Int => result.AsInt(),
                ValueKind.Float => result.AsFloat(),
                _ => throw new InvalidOperationException($"The conversion of '{symbol}' must give a number, not a value of type '{result.Kind}'."),
            };
        }

        /// <summary>Marks EXACTLY one VM instance in the whole program as "the"
        /// main-thread instance - basis for `catch threads(...)`/`catch
        /// terminate(v)` (docs/spec/THREADING_DESIGN.md 6.2/6.3), which according to the design
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
        /// beyond the Run() call site (VM-internal control flow uses no exceptions) - in a C++ version
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

        /// <summary>Debugger mode: a `throw` that no `catch` in this VM would take does not unwind but PAUSES at the throw site
        /// (<see cref="UnhandledException"/> is set, the stepping loops return as for a program end, but nothing is destroyed). The debugger can then
        /// end the program or call <see cref="TryResumeUnhandled"/> to ignore the error and go on after the `throw`, like `resume()` in a `catch`.</summary>
        public bool PauseOnUnhandled { get; set; }

        private bool _unhandledPaused;
        private Chunk? _pausedChunk;
        private int _pausedIp;

        /// <summary>true while the VM stands at the throw site of an unhandled exception (<see cref="PauseOnUnhandled"/>).</summary>
        public bool IsPausedOnUnhandled => _unhandledPaused;

        /// <summary>Ignores the paused unhandled exception: execution goes on right after the `throw` (which evaluates to `undefined`).
        /// false if the VM is not paused on one.</summary>
        public bool TryResumeUnhandled()
        {
            if (!_unhandledPaused) return false;
            _unhandledPaused = false;
            _stopExecutionRequested = false;
            UnhandledException = null;
            UnhandledTrace = null;
            _currentChunk = _pausedChunk!;
            _ip = _pausedIp;
            _pausedChunk = null;
            Push(Value.MakeUndefined());
            return true;
        }

        private bool WouldBeCaught(ObjectInstance exc)
        {
            int floor = _callbackBoundaries.Count > 0 ? _callbackBoundaries.Peek().HandlerFloor : 0;
            for (int i = _handlers.Count - 1; i >= floor; i--)
                if (!_handlers[i].FinallyOnly && FindMatchingCatch(_handlers[i].Template, exc) != null) return true;
            return false;
        }

        private void RecordThrowTrace() => _lastThrowTrace = CurrentTrace();

        /// <summary>The current call stack as source locations, innermost first (for error reports of the debugger).</summary>
        public List<(int SourceIndex, int Line)> CurrentTrace()
        {
            var trace = new List<(int SourceIndex, int Line)> { _currentChunk.GetLocation(_ip) };
            foreach (var frame in _frames) // Stack enumerates top (innermost) first
                trace.Add(frame.ReturnChunk.GetLocation(Math.Max(0, frame.ReturnIp - 1)));
            return trace;
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
        /// <summary>`await x.Method()` (OpCode.AwaitReply): the next method call waits for the result when x is an actor (Next: set by the prefix, Now: of the running call).</summary>
        private bool _awaitReplyNext, _awaitReplyNow;

        // -----------------------------------------------------------
        // Global variables and fire threads (docs/spec/THREADING_DESIGN.md section 7, Runtime.GlobalsBroker)
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
            if (_groupHome != null) return _ownerBroker = _groupHome.EnsureOwnerBroker();   // (a member of the main program's group: the main program's)
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
            if (_sectionDepth++ == 0)
            {
                // (a task of the pool waits for the main program on its worker: the pool may start another one meanwhile)
                if (PoolTask != null) VmScheduler.BlockEnter();
                try { _sectionHandle = _threadBroker!.EnterSection(); }
                finally { if (PoolTask != null) VmScheduler.BlockLeave(); }
            }
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

        // (rule 10: the value of the last `x = take v` / `obj.f = take v` of this fire thread - its store into the main program's globals moves it there)
        private object? _pendingGlobalTake;

        /// <summary>Rule 10 (docs/ROADMAP_TASKS.md step 8): what a fire thread writes into the main program's globals (a global variable, a field of one
        /// of their objects) goes there - a value as it is; an object (array, buffer, lambda) of this thread only when it is fresh (`new`, a call's
        /// result, `copy x`) or taken (`take x`): it moves to `target` like `handover` and joins the globals' domain. Another one of this thread is the
        /// ArgumentException (false); one of the main program stays the reference it is.</summary>
        private bool ToMainGlobals(ref Value value, IOwner target, ThreadShareLock treeLock, string what)
        {
            object? taken = _pendingGlobalTake;
            _pendingGlobalTake = null;
            if (value.Kind is not (ValueKind.Class or ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda)) return true;
            if (value.Kind == ValueKind.Class ? ((ObjectInstance)value.AsObjectRef()).IsDestroyed : IsDestroyedLeaf(value)) return true;
            if (InMainGlobals(value)) return true;
            object identity = value.Kind switch { ValueKind.Class => value.AsObjectRef(), ValueKind.Array => value.AsArray(), ValueKind.Buffer => value.AsBuffer(), _ => value.AsLambda() };
            if (!ReferenceEquals(taken, identity) && !IsUnclaimed(value))
            {
                ThrowArgumentError($"{what} belongs to the main program: an object of this thread goes there only with a word - 'copy x' (a copy) or 'take x' (moved).");
                return false;
            }
            value = ObjectHandover.MoveAll(new[] { value }, target, senderHome: EnsureHomeInbox())[0];
            ShareValue(value, treeLock, new HashSet<object>(ReferenceEqualityComparer.Instance));
            return true;
        }

        /// <summary>False: the value was refused (the ArgumentException is raised, the stack is the handler's).</summary>
        private bool StoreSharedGlobal(int slot, Value value)
        {
            var broker = _threadBroker!;
            EnterGlobalsSection();
            try
            {
                if (!ToMainGlobals(ref value, broker.Scope, broker.Lock, "A global variable")) return false;
                _stack[_sp - 1] = value;   // (the value the assignment gives: the moved one)
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
            return true;
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
            try
            {
                int slot = _globalScope.DefineSlot(value);
                if (ExecutionMode != VmExecutionMode.Performance && _currentChunk.DeclaredNameAt(_ip - 1) is { } name) _globalScope.SetDebugName(slot, name);
            }
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

        /// <summary>Rule 10 (docs/ROADMAP_TASKS.md step 8): the arguments go to the main program - a value as it is; an object (an array, a buffer, a
        /// lambda) of this thread only with a word: `copy`/`flat` (a copy), `take` (moved, like `handover`), or fresh (`new`, a call's result: moved);
        /// one of the main program's globals as the reference it is. Then the call is a job of the main program, the result comes back like `await on`.</summary>
        private void CallGlobalsMethodAtHome(ObjectInstance obj, string methodName, Value[] args, long copyMask)
        {
            var holder = new Scope(null);
            object? home = EnsureHomeInbox();
            for (int i = 0; i < args.Length; i++)
            {
                int bits = i < 16 ? (int)(copyMask >> (4 * i)) & 15 : 0;
                var a = args[i];
                if (bits == 3) { a = args[i] = ReadRefArgument(a); bits = 0; }
                if (a.Kind is not (ValueKind.Class or ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda)) continue;
                if (a.Kind == ValueKind.Class ? ((ObjectInstance)a.AsObjectRef()).IsDestroyed : IsDestroyedLeaf(a)) continue;
                if (bits is 1 or 2) args[i] = ObjectCloner.Clone(a, holder, deep: bits == 2);
                else if (bits is 5 or 7 || IsUnclaimed(a)) args[i] = ObjectHandover.MoveAll(new[] { a }, holder, senderHome: home)[0];
                else if (!InMainGlobals(a))
                {
                    ThrowArgumentError($"'{methodName}' runs in the main program (an object of its globals): an object of this thread goes there only with a word - 'copy x' (a copy) or 'take x' (moved).");
                    return;
                }
            }
            var reply = new ActorReply();
            _threadBroker!.PostMethodJob(obj, methodName, args, holder, reply);
            TakeReply(reply);
        }

        private static object? IdentityOf(Value v) => v.Kind switch
        {
            ValueKind.Class => v.AsObjectRef(),
            ValueKind.Array => v.AsArray(),
            ValueKind.Buffer => v.AsBuffer(),
            ValueKind.Lambda => v.AsLambda(),
            _ => null,
        };

        /// <summary>Does the value belong to the main program's globals (an object of their domain, a shared array)?</summary>
        private bool InMainGlobals(Value v) => v.Kind switch
        {
            ValueKind.Class => v.AsObjectRef() is ObjectInstance { InGlobalsDomain: true } || v.AsObjectRef() is ObjectInstance { Mailbox: not null },
            ValueKind.Array => v.AsArray().IsShared,
            // (a lambda or buffer carries no mark: it is the main program's when its owner is - the globals' scope or an object of their domain)
            _ => LeafOf(v) is { LeafOwner: { } owner } && (ReferenceEquals(owner, _threadBroker?.Scope) || owner is ObjectInstance { InGlobalsDomain: true }),
        };

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
        // and executed by the main program - at `sync globals` or automatically at a safe point (as `#globaljobs` says). This way they see the real
        // globals, and there is no concurrent access to them.

        private readonly record struct InboundCallback(LambdaValue? Lambda, Value[] Args, Action<string>? OnUnhandled, Action? HostAction = null);
        private readonly System.Collections.Concurrent.ConcurrentQueue<InboundCallback> _inbound = new();
        private volatile bool _acceptingCallbacks;

        /// <summary>When the main program processes the queue by itself (`#globaljobs`, `#nosync`, `--global-jobs`; docs/spec/THREADING_DESIGN.md section 7). The safe points
        /// are the same as in a native build: a loop's jump back (only `Safepoints`), `Sleep` and the wait functions (`Safepoints`, `Sleep`); `sync globals` and the end
        /// of the program always. Not a call, not a native call (`print`, `Window.Tick`).</summary>
        private GlobalJobsMode _globalJobs = GlobalJobsMode.Safepoints;

        /// <summary>Running something during which the queue is NOT processed automatically: a destructor, a `fire global` job or a host callback (one runs for
        /// itself), `catch threads`/`catch terminate`. Everything else nested (a property, an operator, a callback of a library
        /// function, an actor's message) has the safe points as usual - as in a native build.</summary>
        private int _noAutoSyncDepth;

        /// <summary>Counts every entry put into a main program's queue (process-wide, a hint only): a loop's jump back compares it with
        /// <see cref="_seenQueued"/> - one comparison - and processes the queue only when something came. Separate from the signals, so that a
        /// call (a safe point for `leave`/`terminate` only) does not use up the notice that the next loop needs.</summary>
        private static int s_queued;
        private int _seenQueued;

        /// <summary>Something was put into a main program's queue (a job, a section, a host callback).</summary>
        internal static void RaiseQueued()
        {
            System.Threading.Interlocked.Increment(ref s_queued);
            RaiseSignal();
        }

        /// <summary>Accepts a callback from ANY thread (thread-safe) and queues it for this VM. false if the VM is not (any longer)
        /// running - then the caller has to choose another way.</summary>
        public bool PostCallback(LambdaValue lambda, Value[] args, Action<string>? onUnhandled)
        {
            if (!_acceptingCallbacks) return false;
            _inbound.Enqueue(new InboundCallback(lambda, args, onUnhandled));
            RaiseQueued();
            FireRuntime.WakeWaitingOwner();
            return true;
        }

        /// <summary>Accepts an action of the host from ANY thread (a library's services: fire_host.request_service) and runs it at this VM's next safe point, on its thread.
        /// false if the VM is not (any longer) running.</summary>
        public bool PostHostAction(Action action)
        {
            if (!_acceptingCallbacks) return false;
            _inbound.Enqueue(new InboundCallback(null, Array.Empty<Value>(), null, action));
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
                if (callback.HostAction != null)
                {
                    try { callback.HostAction(); }
                    catch (Exception ex) { Console.Error.WriteLine("(host: " + ex.Message + ")"); }
                    if (_stopExecutionRequested) break;
                    continue;
                }
                _noAutoSyncDepth++;   // (a callback runs for itself: its own safe points do not run the next one)
                try
                {
                    var error = CallLambdaInline(callback.Lambda!, callback.Args);
                    if (error != null) callback.OnUnhandled?.Invoke(new UncaughtScriptException(error).Message);
                }
                catch (Exception ex)
                {
                    callback.OnUnhandled?.Invoke(ex.Message);
                }
                finally { _noAutoSyncDepth--; }
                if (_stopExecutionRequested) break;
            }
            return handled;
        }

        /// <summary>Automatic processing at a safe point - a loop's jump back (`waiting` false) or while waiting (`Sleep`, the wait functions; true) - if the mode
        /// asks for it there and nothing that runs for itself is running (<see cref="_noAutoSyncDepth"/>): first host callbacks, then the sections and jobs of the
        /// fire threads. true if the program was ended in the process (`leave`/`terminate` in a job).</summary>
        private bool AutoSyncAt(bool waiting)
        {
            if (_globalJobs == GlobalJobsMode.Sync || (_globalJobs == GlobalJobsMode.Sleep && !waiting)) return false;
            if (_noAutoSyncDepth > 0 || _stopExecutionRequested) return false;
            _seenQueued = System.Threading.Volatile.Read(ref s_queued);
            if (!_inbound.IsEmpty) DrainInbound();
            if (!_stopExecutionRequested && _ownerBroker != null && _ownerBroker.HasPending) _ownerBroker.Drain();
            return _stopExecutionRequested;
        }

        /// <summary>Executes a `fire global` job on this (the owner) VM. An unhandled exception in it is treated like that of a
        /// fire thread: it goes to the main program (`catch threads`), otherwise it aborts.</summary>
        /// <summary>A method call of a fire thread on an object of the main program's globals (rule 10): it runs here, its result or exception goes back.</summary>
        internal void RunGlobalsMethodJob(ObjectInstance target, string methodName, Value[] args, Scope argHolder, ActorReply reply)
        {
            var freshOwner = _currentScope.Temp;
            Value result;
            ObjectInstance? error;
            _noAutoSyncDepth++;
            try
            {
                if (IsDeadObject(target)) { reply.Failure = "The object of the main program's globals is destroyed."; reply.Done.Set(); VmScheduler.Wake(); return; }
                error = CallMethodInline(target, methodName, args, out result, argHolder);
            }
            catch (ScriptErrorException e) { reply.Failure = e.Message; reply.Done.Set(); VmScheduler.Wake(); return; }
            finally { _noAutoSyncDepth--; }
            if (error != null) { error.ReparentTo(reply.Holder); reply.Exception = error; }
            else reply.Result = ReplyValue(result, freshOwner, reply.Holder);
            reply.Done.Set();
            VmScheduler.Wake();
        }

        internal void RunGlobalsJob(LambdaValue lambda, Value[] args, ActorReply? reply = null)
        {
            ObjectInstance? error;
            if (reply != null)
            {
                // `await on`: the result or the exception goes back to the caller that waits (what the block made goes over, anything else as a copy)
                var freshOwner = _currentScope.Temp;
                Value result;
                _noAutoSyncDepth++;
                try { error = CallInline(lambda, args, out result); }
                finally { _noAutoSyncDepth--; }
                if (error != null) { error.ReparentTo(reply.Holder); reply.Exception = error; }
                else reply.Result = ReplyValue(result, freshOwner, reply.Holder);
                reply.Done.Set();
                VmScheduler.Wake();   // (a task of the pool that waits for it)
                return;
            }
            _noAutoSyncDepth++;   // (a job runs for itself: at its safe points the next job does not start)
            try { error = CallLambdaInline(lambda, args); }
            finally { _noAutoSyncDepth--; }
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
            globalScope.EarlyAccess = true;   // (a global used before its declaration: see Scope.EarlyAccess)
            _natives = natives;
            _watchDangling = executionMode == VmExecutionMode.Debug && natives.Has("print");
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
        // ENDING is deliberately not a C# exception (VM-internal control flow uses no exceptions: in a C++ version
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
            // (the queue is not processed here: a native call is not a safe point for it - see _globalJobs)
        }

        /// <summary>A loop's jump back: processes the queue if something came since the last time and the mode is `safepoints` (one comparison otherwise).
        /// true if the VM was ended by it.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private bool PollQueueAtLoop() =>
            _globalJobs == GlobalJobsMode.Safepoints && _seenQueued != System.Threading.Volatile.Read(ref s_queued) && AutoSyncAt(false);

        /// <summary>While waiting (`Sleep`, the wait functions): the signals, then the queue (modes `safepoints` and `sleep`).</summary>
        private void PollWhileWaiting()
        {
            PollSignalsAfterOp();
            if (!_stopExecutionRequested && _seenQueued != System.Threading.Volatile.Read(ref s_queued)) AutoSyncAt(true);
            if (!_stopExecutionRequested) ServeHomeInbox();
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
            // (the queue - host callbacks, the fire threads that wait for the main program - is processed only at a loop's jump back and while
            // waiting, see PollQueueAtLoop/PollWhileWaiting: a call is no safe point for it)
            return false;
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
                _homeInbox?.Close();     // (the calls of lambdas bound to this thread's objects fail from now on)
            }
        }

        // ---- docs/ROADMAP_TASKS.md step 8 (e): the calls other threads send for lambdas bound to objects of this fire thread
        private HomeInbox? _homeInbox;
        /// <summary>The inbox of this fire thread (made when a lambda bound to one of its objects leaves it).</summary>
        internal object EnsureHomeInbox() => _homeInbox ??= new HomeInbox();
        internal void CloseHomeInbox() => _homeInbox?.Close();
        /// <summary>Runs the calls that came (at a safe point of this thread, or while it waits); true if the VM was ended by one.</summary>
        private bool ServeHomeInbox()
        {
            if (_homeInbox == null || !_homeInbox.HasPending || _noAutoSyncDepth > 0 || _nestedDepth > 0) return false;
            _homeInbox.Drain(this);
            return _stopExecutionRequested;
        }

        private void RunLoop()
        {
            _ip = 0;
            // (the try is outside the hot loop: an error inside an instruction leaves it, becomes a script exception and the loop goes on - ContinueLoop)
            ContinueLoop();
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)] // the loop of the whole program: entered once, so compile it fully optimised straight away (not tier 0 and on-stack replacement)
        private void RunLoopCore()
        {
            while (true)
            {
                var op = (OpCode)ReadByte();
                if (op == OpCode.Halt)
                {
                    if (ReferenceEquals(_currentChunk, SuspendChunk)) return;   // (a task of the pool waits: VM.Tasks.cs)
                    // A leave/terminate called in nested execution is only now unwound in an orderly way;
                    // afterwards the halt chunk is there again and the next read lands here again.
                    if (FinishDeferredShutdown()) continue;
                    // Windows that a window manager's thread shows keep the program alive: it serves them until they are closed.
                    if (!_stopExecutionRequested && !IsFireThreadVm && HostHolds > 0) { WaitForHostHolds(); if (FinishDeferredShutdown()) continue; }
                    // Normal end or orderly leave/terminate (not the halt chunk of an unhandled exception).
                    if (!_stopExecutionRequested || _shutdownReleasePending) ReleaseGlobalScopeAtEnd();
                    return;
                }
                Step(op);
            }
        }

        // Holds of the host on the end of THIS program (see AddHostHold).
        private int _hostHolds;

        /// <summary>Adds (or, negative, removes) holds on the end of this program: something of the host keeps it alive after its last statement - the open windows
        /// that a window manager's thread shows. The program then waits at its end, serving what is posted to it, until they are gone. Only this program's main
        /// VM waits for them; once it has ended they no longer matter.</summary>
        public void AddHostHold(int delta)
        {
            Interlocked.Add(ref _hostHolds, delta);
            FireRuntime.WakeWaitingOwner();
        }

        /// <summary>The holds on the end of this program (see <see cref="AddHostHold"/>).</summary>
        public int HostHolds => Volatile.Read(ref _hostHolds);

        /// <summary>The program has run its last statement, but the host still holds it (open windows of a window manager's thread, see
        /// <see cref="AddHostHold"/>): it waits and runs what is posted to it (the managers' cycles, callbacks, jobs of fire threads) until the holds are
        /// gone or the program is ended (`leave`/`terminate` in a callback).</summary>
        private void WaitForHostHolds()
        {
            while (HostHolds > 0 && !_stopExecutionRequested)
            {
                if (!_inbound.IsEmpty) DrainInbound();
                if (!_stopExecutionRequested && _ownerBroker != null && _ownerBroker.HasPending) _ownerBroker.Drain();
                if (_stopExecutionRequested) break;
                VmGroup.WaitTurn();   // (a handler that waited; the events of the windows)
                if (_inbound.IsEmpty) FireRuntime.WaitForWake(5);
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
            // (the members of the main program's group - handlers that wait - end before its globals do)
            if (!IsFireThreadVm) while (VmGroup.Any && !_stopExecutionRequested) { VmGroup.RunReady(); if (VmGroup.Any) FireRuntime.WaitForWake(5); }
            if (!IsFireThreadVm) FireRuntime.WaitForAllFireThreads(_ownerBroker); // the main program (every VM that is not a fire thread); meanwhile it serves the threads' queue
            // An exception a thread did not catch, delivered after the main program's last safe point, is not dropped: `catch threads`, or the
            // program ends with it (like at a safe point - the global scope then stays, as after any unhandled exception; native: the same at its end).
            if (IsMainThreadVm && !afterShutdown)
                while (_pendingThreadExceptions.TryDequeue(out var lateException))
                    if (HandleDeliveredThreadException(lateException)) return;
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
        /// itself (docs/spec/THREADING_DESIGN.md 6.2).</summary>
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

            _noAutoSyncDepth++;
            try { RunNestedUntil(targetDepth); }
            finally { _noAutoSyncDepth--; }
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

            _noAutoSyncDepth++;
            try { RunNestedUntil(targetDepth); }
            finally { _noAutoSyncDepth--; }
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

            if (message.Lambda is { } lambdaCall)
            {
                // a lambda bound to the actor, called elsewhere (docs/ROADMAP_TASKS.md step 8 (e)): it runs here like a job, its result goes back
                if (message.Reply != null) RunGlobalsJob(lambdaCall, message.Args, message.Reply);
                else { var lambdaError = CallLambdaInline(lambdaCall, message.Args); if (lambdaError != null) ThrowException(Value.MakeClassRef(lambdaError)); }
                return true;
            }

            var rc = ResolveClass(actor.ClassName);
            var proto = FindOverload(rc, message.MethodName, message.Args, actor).Proto
                ?? throw MethodNotFound(rc, message.MethodName, message.Args.Length);
            CheckArity(proto, message.Args.Length);
            var args = FillDefaultArgs(proto, message.Args, actor);

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;

            var scope = new Scope(_globalScope);
            foreach (var a in args) scope.DefineSlot(a);
            // what the message owned goes to the method's call, like an argument (dies after its locals unless it keeps it)
            if (message.Holder != null)
                for (int i = 0; i < scope.SlotCount; i++)
                {
                    var arg = scope.GetSlot(i);
                    if (!ReferenceEquals(OwnerOfValue(arg), message.Holder)) continue;
                    if (arg.Kind == ValueKind.Class) ((ObjectInstance)arg.AsObjectRef()).ReparentToArgument(scope);
                    else if (LeafOf(arg) is { } leaf) LeafOwnership.Reparent(leaf, scope);
                }

            _currentThis = actor;
            _currentScope = scope;
            _currentChunk = proto.Chunk;
            _ip = 0;

            if (message.Reply is { } reply)
            {
                // `await actor.Method()`: the exception that nothing in the method caught goes back to the caller, not to the handlers of `process`
                int frameDepth = targetDepth - 1;
                var freshOwner = _frames.Peek().ReturnScope.Temp;   // (where a value made by the call lands when it returns)
                _callbackBoundaries.Push(new CallbackBoundary(frameDepth, _frames.Peek().ReturnScope, _handlers.Count, _sp));
                _callbackError = null;
                try { RunNestedUntil(targetDepth); }
                finally { _callbackBoundaries.Pop(); }
                if (_shutdownDeferred) FinishDeferredShutdown();
                var error = _callbackError;
                _callbackError = null;
                if (error != null)
                {
                    error.ReparentTo(reply.Holder);
                    reply.Exception = error;
                }
                else if (!_stopExecutionRequested)
                {
                    reply.Result = ReplyValue(Pop(), freshOwner, reply.Holder);
                }
                reply.Done.Set();
                VmScheduler.Wake();
                return true;
            }

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
            if (!_stopExecutionRequested && !IsFireThreadVm && HostHolds > 0) WaitForHostHolds();
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
            // (the queue is processed by the instructions themselves - a loop's jump back, Sleep - as when the program runs: not after every step)

            // Since UnhandledException (see there) an unhandled script exception is no longer thrown, but only SET -
            // Run() notices that via CheckShutdownSignals (deliberately NOT called here, see field documentation), so during step-by-step debugging
            // it has to be checked explicitly HERE, otherwise StepLine/StepInto/StepOut/Continue (all build on this
            // method) would simply keep running as if nothing had happened, instead of stopping cleanly.
            if (_unhandledPaused) return false; // paused at the throw site: not ended, the debugger decides (TryResumeUnhandled)
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
            // (the profiler of a debug run: the instructions of each line and how often a line is entered, LineProfile)
            var profile = LineProfile.Active;
            long ran = 0;

            try
            {
                while (true)
                {
                    var op = (OpCode)ReadByte();
                    if (op == OpCode.Halt) return FinishAtHalt();
                    Step(op);
                    ran++;
                    if (!AfterStep()) return false;

                    if ((++counter & 255) == 0 && isPauseRequested()) return true;

                    if (!ReferenceEquals(_currentChunk, rangeChunk) || _ip < rangeStart || _ip >= rangeEnd)
                    {
                        var location = _currentChunk.GetLocationRange(_ip, out rangeStart, out rangeEnd);
                        rangeChunk = _currentChunk;
                        if (location != last)
                        {
                            // (entered: at the start of the line's code - not back from a call into its middle, not by a jump to the end of a block whose
                            // code still counts as the line before)
                            if (profile != null) { profile.Add(last, 0, ran); if (_ip == rangeStart) profile.Add(location, 1, 0); }
                            ran = 0;
                            last = location;
                            if (breakpoints.Contains(location)) return true;
                        }
                    }
                }
            }
            finally
            {
                if (profile != null && ran > 0) profile.Add(last, 0, ran);
            }
        }

        private static readonly HashSet<(int SourceIndex, int Line)> NoBreakpoints = new();

        /// <summary>Runs until the program end or a pause request ("Run to end"). true = stopped, false = ended.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public bool RunUntilEnd(Func<bool> isPauseRequested)
        {
            if (LineProfile.Active != null) return RunUntilBreakpoint(NoBreakpoints, isPauseRequested);   // (profiling: the loop that knows the lines)
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
        public sealed record ScopeLevel(int Depth, bool IsFunctionTopLevel, IReadOnlyList<(string Name, Value Value)> Variables)
        {
            /// <summary>The scope itself (its slots are the variables in order) - for the debugger's `set`.</summary>
            public Scope? Scope { get; init; }
        }

        /// <summary>The complete scope chain of the current function/method/
        /// lambda, from the currently active (innermost) block up to
        /// its top-level scope (after that comes global, see DebugGlobals) -
        /// each level as an entry of its own, so that the editor can show them
        /// separately. Names reliable only for parameters of the top-
        /// level (see Chunk.DebugLocalNames comment), everything else
        /// as "(local N)".</summary>
        /// <summary>Gives the slot just made by `DeclareLocal` (the instruction before `_ip`) the name of its variable, for the debugger.</summary>
        private void NameDeclaredSlot(int slot)
        {
            if (_currentChunk.DeclaredNameAt(_ip - 1) is { } name) _currentScope.SetDebugName(slot, name);
        }

        /// <summary>`value!type`: a conversion that `!` cannot do (only int and float convert into each other) is a catchable `ArgumentException`.</summary>
        private static Value CoerceWidthChecked(Value value, int bits, bool toFloat, bool unsigned)
        {
            try { return value.ConvertWidth(bits, toFloat, unsigned); }
            catch (InvalidOperationException ex) when (ex is not ScriptErrorException)
            {
                throw new ScriptErrorException("ArgumentException", ex.Message);
            }
        }

        private static Value CoerceTypeChecked(Value value, ValueKind kind)
        {
            try { return value.CoerceType(kind); }
            catch (InvalidOperationException ex) when (ex is not ScriptErrorException)
            {
                throw new ScriptErrorException("ArgumentException", ex.Message);
            }
        }

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
                    string name = scope.DebugNameOf(slot)
                        ?? (isFunctionTopLevel && _currentChunk.DebugLocalNames.TryGetValue((0, slot), out var n) ? n : $"(local {slot})");
                    vars.Add((name, scope.GetSlot(slot)));
                }
                result.Add(new ScopeLevel(depth, isFunctionTopLevel, vars) { Scope = scope });
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
                yield return (_globalScope.DebugNameOf(slot) ?? $"(global {slot})", _globalScope.GetSlot(slot));
        }

        /// <summary>The global scope (its slots are <see cref="DebugGlobals"/> in order) - for the debugger's `set`.</summary>
        public Scope DebugGlobalScope => _globalScope;

        /// <summary>The debugger's `set` of a member of an object while this VM is paused - runs on the VM's own thread. Like the assignment
        /// `obj.name = value` in the program: probes on the member fire, a property runs its setter, the unit and type rules of the field apply.
        /// false with a message if that assignment threw (the exception then goes its way like one of the program: to a handler, or the VM
        /// stops at it as unhandled).</summary>
        public bool DebugAssignMember(ObjectInstance obj, string name, Value value, out string? error)
        {
            error = null;
            if (!_steppingStarted) BeginStepping();
            int sp = _sp;
            Push(Value.MakeClassRef(obj));
            Push(value);
            bool ok;
            try { ok = SetFieldSlow(name, -1); }
            catch (Exception ex) when (ex is ScriptErrorException or InvalidOperationException)
            {
                _sp = sp;
                error = ex.Message;
                return false;
            }
            if (!ok)
            {
                error = UnhandledException != null ? new UncaughtScriptException(UnhandledException).Message : "The assignment threw an exception.";
                return false;
            }
            _sp = sp;
            return true;
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
            while (true)
            {
                try { RunNestedLoopCore(targetFrameDepth); return; }
                catch (ScriptErrorException error) { RaiseScriptError(error); if (_stopExecutionRequested) return; }
                catch (InvalidOperationException error) when (IsCatchableRuntimeError(error)) { RaiseScriptError(new ScriptErrorException("ArgumentException", error.Message)); if (_stopExecutionRequested) return; }
            }
        }

        private void RunNestedLoopCore(int targetFrameDepth)
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

                _noAutoSyncDepth++;   // (a destructor runs in the middle of an instruction: no jobs in it)
                try { RunNestedUntil(targetDepth); }
                finally { _noAutoSyncDepth--; }

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
                // (every assignment of a value and every call with declared parameters: not through Execute, whose frame is expensive to set up)
                case OpCode.HoistValue:
                    if (_stack[_sp - 1].Kind is ValueKind.Class or ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda) HoistValue(_stack[_sp - 1]);
                    return;
                case OpCode.GuardLocal: OpGuardLocal(); return;
                case OpCode.GuardLocalType: OpGuardLocalType(); return;

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
                    {
                        int declared = _currentScope.DefineSlot(Pop());
                        if (ExecutionMode != VmExecutionMode.Performance) NameDeclaredSlot(declared);
                    }
                    return;

                // Binary operators: the left operand lies at _sp-2, the right at _sp-1; the result
                // replaces both. An object on the left (operator overloading) falls through to Execute.
                case OpCode.Add:
                    if (Value.TryAddInPlace(ref _stack[_sp - 2], in _stack[_sp - 1])) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class && _stack[_sp - 1].Kind != ValueKind.Class) { ReplaceTwoGuarded(Value.Add); return; }
                    break;
                case OpCode.StepAdd:
                case OpCode.StepSub:
                    _stack[_sp - 1] = Value.StepOf(in _stack[_sp - 2], _stack[_sp - 1]);
                    Step(op == OpCode.StepAdd ? OpCode.Add : OpCode.Sub);
                    return;
                case OpCode.Sub:
                    if (Value.TrySubtractInPlace(ref _stack[_sp - 2], in _stack[_sp - 1])) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoGuarded(Value.Subtract); return; }
                    break;
                case OpCode.Mul:
                    if (Value.TryMultiplyInPlace(ref _stack[_sp - 2], in _stack[_sp - 1])) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoGuarded(Value.Multiply); return; }
                    break;
                case OpCode.Div:
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoGuarded(Value.Divide); return; }
                    break;
                case OpCode.Mod:
                    if (Value.TryModuloInPlace(ref _stack[_sp - 2], in _stack[_sp - 1])) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoGuarded(Value.Modulo); return; }
                    break;
                case OpCode.Eq:
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(Value.OperatorEquals(_stack[_sp - 2], _stack[_sp - 1]))); return; }
                    break;
                case OpCode.NotEq:
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoWith(Value.MakeBool(!Value.OperatorEquals(_stack[_sp - 2], _stack[_sp - 1]))); return; }
                    break;
                case OpCode.Lt:
                    if (Value.TryCompareInPlace(ref _stack[_sp - 2], in _stack[_sp - 1], 0)) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoGuarded(static (a, b) => Value.MakeBool(Value.Compare(a, b) < 0)); return; }
                    break;
                case OpCode.LtEq:
                    if (Value.TryCompareInPlace(ref _stack[_sp - 2], in _stack[_sp - 1], 1)) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoGuarded(static (a, b) => Value.MakeBool(Value.Compare(a, b) <= 0)); return; }
                    break;
                case OpCode.Gt:
                    if (Value.TryCompareInPlace(ref _stack[_sp - 2], in _stack[_sp - 1], 2)) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoGuarded(static (a, b) => Value.MakeBool(Value.Compare(a, b) > 0)); return; }
                    break;
                case OpCode.GtEq:
                    if (Value.TryCompareInPlace(ref _stack[_sp - 2], in _stack[_sp - 1], 3)) { _sp--; return; }
                    if (_stack[_sp - 2].Kind != ValueKind.Class) { ReplaceTwoGuarded(static (a, b) => Value.MakeBool(Value.Compare(a, b) >= 0)); return; }
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
                    if (_stack[_sp - 1].Kind is ValueKind.Int or ValueKind.Float) _stack[_sp - 1] = Value.Negate(_stack[_sp - 1]);
                    else ReplaceOneGuarded(Value.Negate);
                    return;
                case OpCode.LogicalNot:
                    if (_stack[_sp - 1].Kind == ValueKind.Bool) _stack[_sp - 1] = Value.LogicalNot(_stack[_sp - 1]);
                    else ReplaceOneGuarded(Value.LogicalNot);
                    return;

                case OpCode.Jump:
                {
                    // A back jump (loop) is a safe point for shutdown signals (see PollSignals); `_ip` here still
                    // points to the operand, PollSignalsSlow calculates with the instruction boundary before it.
                    int target = _code[_ip] | (_code[_ip + 1] << 8);
                    if (target <= _ip && (PollSignals() || PollQueueAtLoop() || (_homeInbox != null && ServeHomeInbox()))) return;
                    _ip = target;
                    return;
                }

                case OpCode.JumpIfFalse:
                {
                    int addr = ReadU16();
                    var condition = Pop();
                    if (condition.Kind != ValueKind.Bool) { ThrowConditionError(condition); return; }
                    if (!condition.AsBool()) _ip = addr;
                    return;
                }

                case OpCode.JumpIfFalsePeek:
                {
                    int addr = ReadU16();
                    if (_stack[_sp - 1].Kind != ValueKind.Bool) { ThrowConditionError(_stack[_sp - 1]); return; }
                    if (!_stack[_sp - 1].AsBool()) _ip = addr;
                    return;
                }

                case OpCode.JumpIfTruePeek:
                {
                    int addr = ReadU16();
                    if (_stack[_sp - 1].Kind != ValueKind.Bool) { ThrowConditionError(_stack[_sp - 1]); return; }
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
                    if (slot < _sharedCount) { if (!StoreSharedGlobal(slot, value)) return; }
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
                        int ipAfter = _ip;
                        var chunkBefore = _currentChunk;
                        ExecuteCompareSlow(kind switch { 0 => OpCode.Lt, 1 => OpCode.LtEq, 2 => OpCode.Gt, _ => OpCode.GtEq });
                        if (_ip != ipAfter || !ReferenceEquals(_currentChunk, chunkBefore)) return;   // it threw (`1m < 1s`): the handler already continues
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
                        equal = Value.OperatorEquals(_stack[_sp - 2], _stack[_sp - 1]);
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
                    int depth = ReadU16(); int slot = ReadU16(); int constIdx = ReadU16(); int mode = ReadByte(); bool subtract = (mode & 1) != 0;
                    ref Value variable = ref _currentScope.GetAncestor(depth).SlotRef(slot);
                    if (subtract ? Value.TrySubtractInPlace(ref variable, in _constants[constIdx]) : Value.TryAddInPlace(ref variable, in _constants[constIdx]))
                        return;
                    ArithSlow(depth, slot, constIdx, subtract, (mode & 2) != 0);
                    return;
                }

                case OpCode.ArithGlobalConstPop:
                {
                    int slot = ReadU16(); int constIdx = ReadU16(); int mode = ReadByte(); bool subtract = (mode & 1) != 0; bool step = (mode & 2) != 0;
                    // Fire threads and a main program with running threads read/write globals via the broker: ordinary way
                    if (slot < _sharedCount || _ownerBroker != null) { ArithGlobalSlow(slot, constIdx, subtract, step); return; }
                    ref Value variable = ref _globalScope.SlotRef(slot);
                    if (subtract ? Value.TrySubtractInPlace(ref variable, in _constants[constIdx]) : Value.TryAddInPlace(ref variable, in _constants[constIdx]))
                        return;
                    ArithGlobalSlow(slot, constIdx, subtract, step);
                    return;
                }
            }

            Execute(op);
        }

        /// <summary>Slow path of the fused comparison jumps: the comparison `op` over the two topmost stack values, exactly like
        /// the ordinary instruction (also with operator overloading); the result then lies on top of the stack.</summary>
        private void ExecuteCompareSlow(OpCode op) => Step(op);

        /// <summary>Slow path of `x = x + c`/`x++` on a local: ordinary loading, calculating (strings, units, errors) and storing.</summary>
        private void ArithSlow(int depth, int slot, int constIdx, bool subtract, bool step = false)
        {
            Push(_currentScope.GetAncestor(depth).SlotRef(slot));
            Push(step ? Value.StepOf(in _stack[_sp - 1], _constants[constIdx]) : _constants[constIdx]);
            _arithFault = false;
            Step(subtract ? OpCode.Sub : OpCode.Add);
            if (_arithFault) { _arithFault = false; if (_unhandledPaused) _sp--; return; }   // an exception: the variable stays as it is (an unhandled one stands here, its placeholder result goes)
            _currentScope.GetAncestor(depth).SlotRef(slot) = _stack[--_sp];
        }

        /// <summary>Like <see cref="ArithSlow"/> for a global variable (also via the broker of the fire threads).</summary>
        private void ArithGlobalSlow(int slot, int constIdx, bool subtract, bool step = false)
        {
            if (_sp == _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
            if (slot < _sharedCount) _stack[_sp] = LoadSharedGlobal(slot); else _stack[_sp] = _globalScope.SlotRef(slot);
            _sp++;
            Push(step ? Value.StepOf(in _stack[_sp - 1], _constants[constIdx]) : _constants[constIdx]);
            _arithFault = false;
            Step(subtract ? OpCode.Sub : OpCode.Add);
            if (_arithFault) { _arithFault = false; if (_unhandledPaused) _sp--; return; }
            var value = _stack[_sp - 1];
            if (slot < _sharedCount) { if (!StoreSharedGlobal(slot, value)) return; }
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
            if (indexVal.Kind == ValueKind.Word && target.Kind is ValueKind.Array or ValueKind.Buffer or ValueKind.String) indexVal = indexVal.CoerceType(ValueKind.Int);   // (a word as an index: its bits as an int)
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
                var one = Value.StepOf(in oldVal, Value.MakeInt(1));
                var newVal = isIncrement ? Value.Add(oldVal, one) : Value.Subtract(oldVal, one);
                if (!ConvertArrayElement(arr, ref newVal)) return;   // (a typed array: 127 + 1 in an int[8] array is -128)
                if (arr.IsShared) TrySetSharedElement(arr, idx, newVal); else arr.TrySet(idx, newVal);
                Push(isPrefix ? newVal : oldVal);
            }
            else if (target.Kind == ValueKind.Buffer && target.AsBuffer().IsWords)
            {
                var words = target.AsBuffer();
                if (idx < 0 || idx >= words.Count) { ThrowIndexOutOfBounds(idx, words.Count); return; }
                var oldWord = Value.MakeWord(words.GetWord(idx));
                var newWord = isIncrement ? Value.Add(oldWord, Value.MakeInt(1)) : Value.Subtract(oldWord, Value.MakeInt(1));
                words.SetWord(idx, newWord.AsWord());
                Push(isPrefix ? newWord : oldWord);
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
            var lambdaValue = new LambdaValue(proto, onTarget) { TypeArgs = TakeLambdaTypeArgs() };
            LeafOwnership.Adopt(lambdaValue, _currentScope.Temp);   // (a new lambda belongs to the statement until a slot claims it, like a new array)
            Push(Value.MakeLambda(lambdaValue));
            return;
        }
        }

        /// <summary>The store of a new lambda (ROADMAP_TASKS step 8): what it captured and is still in the temporaries of the statement - the copies of
        /// `taking` (and of a variable without a word), the instances `handover` moved - belongs to the lambda and dies with it.</summary>
        private void OwnStore(LambdaValue lambda)
        {
            var temp = _currentScope.Temp;
            if (lambda.Captures == null || !temp.HasOwned) return;
            foreach (var c in lambda.Captures)
                if (c.Kind is ValueKind.Class or ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda && RootedIn(c, temp)) OwnershipWalk.TakeValue(c, lambda, this);
        }

        /// <summary>What a call of the lambda gets for capture i (ROADMAP_TASKS step 8): a copy of the store's value that belongs to the call (`taking`, a
        /// variable without a word - unless the body only reads it), the store's instance itself (`handover`: lent; once it was taken away, the store has
        /// none), or the store's value (a number, a text, a copy that is only read).</summary>
        private Value CaptureForCall(LambdaValue lambda, int i, Scope callScope)
        {
            var c = lambda.Captures![i];
            var proto = lambda.Proto;
            if (i >= 64) return c.Kind is ValueKind.Class or ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda ? ObjectCloner.Clone(c, callScope, deep: true) : c;
            if ((proto.CaptureMovedMask >> i & 1) != 0)
            {
                if (c.Kind is ValueKind.Class or ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda && !ReferenceEquals(OwnerOfLive(c), lambda))
                    lambda.Captures[i] = c = Value.MakeUndefined();   // (taken away in an earlier call - `take`)
                return c;
            }
            if ((proto.CaptureCopyMask >> i & 1) != 0 && c.Kind is ValueKind.Class or ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda && !IsDestroyedLeaf(c)
                && !(c.Kind == ValueKind.Class && c.AsObjectRef() is ObjectInstance { IsDestroyed: true }))
                return ObjectCloner.Clone(c, callScope, deep: true);
            return c;
        }

        /// <summary>The captures of a lambda as the slots behind its parameters (the calls that build their scope themselves).</summary>
        private void DefineCaptureSlots(LambdaValue lambda, Scope funcScope)
        {
            if (lambda.Captures == null) return;
            for (int i = 0; i < lambda.Captures.Length; i++) funcScope.DefineSlot(CaptureForCall(lambda, i, funcScope));
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
            var lambda = new LambdaValue(proto, onTarget, null, captures) { TypeArgs = TakeLambdaTypeArgs() };
            LeafOwnership.Adopt(lambda, _currentScope.Temp);
            OwnStore(lambda);
            Push(Value.MakeLambda(lambda));
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
        private void EnterCall(FunctionProto proto, int argCount, bool dropBelow, object? newThis, ObjectInstance? constructed = null, long copyMask = 0, LambdaValue? lambda = null)
        {
            var captures = lambda?.Captures;
            int captureCount = captures?.Length ?? 0;
            int paramCount = argCount + captureCount;
            var scope = RentCallScope(paramCount + SlotSlack, paramCount);
            var slots = scope.SlotArray;
            Array.Copy(_stack, _sp - argCount, slots, 0, argCount);
            if (captureCount != 0)   // Lambda captures directly behind the parameters (from its store - ROADMAP_TASKS step 8)
            {
                if ((proto.CaptureCopyMask | proto.CaptureMovedMask) == 0) Array.Copy(captures!, 0, slots, argCount, captureCount);
                else for (int i = 0; i < captureCount; i++) slots[argCount + i] = CaptureForCall(lambda!, i, scope);
            }
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
                if (bits is 4 or 7)
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
                if (bits == 8)
                {
                    // `f(try take x)` (SPEC 2.2): the same, but only when the caller owns x - a scope of its call or its `this` (the frame of the caller is on top)
                    if (_frames.Count > 0 && CallerOwns(scope.SlotRef(i), _frames.Peek(), _frames.Count == 1)) TakeArgument(scope.SlotRef(i), scope);
                    continue;
                }
                if (bits == 6)
                {
                    // a claim site of a setter (SPEC 2.1 "Setters"): `Store(value)` - the value goes to the call if it still belongs to the setter call
                    if (_frames.Count > 0 && OwnerOfValue(scope.SlotRef(i)) is Scope owner)
                    {
                        var function = _frames.Peek().ReturnScope;
                        while (function.Parent != null && !function.Parent.IsGlobal) function = function.Parent;
                        if (ReferenceEquals(owner.Home, function)) TakeArgument(scope.SlotRef(i), scope);
                    }
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
                case ValueKind.Lambda when arg.AsLambda() is LambdaValue { IsDestroyed: false } lambda: LeafOwnership.Reparent(lambda, calleeScope); break;
            }
        }

        /// <summary>`try take x` (SPEC 2.2): the value belongs to a scope of the running call (its statement temporaries, its blocks, its call scope with the
        /// arguments it was given) or to `this` - like `try x.Take...`. A destroyed value or one of something else: false.</summary>
        private bool OwnedByRunningCall(Value v)
        {
            var owner = OwnerOfValue(v);
            return owner != null && (IsCurrentCallOwner(owner) || ReferenceEquals(owner, _currentThis));
        }

        /// <summary>The same for the caller whose frame is <paramref name="frame"/> (the callee's scope is already the current one); `topLevel`: the caller is the
        /// top level, whose global scope counts.</summary>
        private static bool CallerOwns(Value v, CallFrame frame, bool topLevel)
        {
            var owner = OwnerOfValue(v);
            if (owner == null) return false;
            if (frame.ReturnThis != null && ReferenceEquals(owner, frame.ReturnThis)) return true;
            if (owner is not Scope s) return false;
            s = s.Home;
            for (var c = frame.ReturnScope; c != null; c = c.Parent)
            {
                if (ReferenceEquals(c, s)) return true;
                if (c.Parent == null || c.Parent.IsGlobal) break;   // (the caller's function scope was the last)
            }
            return topLevel && s.IsGlobal;
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
                    if (!obj.IsDestroyed && RootedIn(arg, callerScope.Temp)) obj.ReparentToArgument(calleeScope);
                    break;
                }
                case ValueKind.Array:
                case ValueKind.Buffer:
                case ValueKind.Lambda:   // (a lambda made in the call's arguments - `list.Add(() => ...)` - belongs to the call like a new object)
                {
                    if (LeafOf(arg) is not { } leaf) break;
                    if (!leaf.IsDestroyed && RootedIn(arg, callerScope.Temp)) LeafOwnership.Reparent(leaf, calleeScope);
                    break;
                }
            }
        }

        /// <summary>For calls WITHOUT a function scope (built-in methods, actor messages): the copy belongs to the
        /// current scope, as with an ordinary copy.</summary>
        private void ApplyCopyMaskToArgs(Value[] args, long mask, Scope? message = null)
        {
            for (int i = 0; i < args.Length && i < 16; i++)
            {
                int bits = (int)(mask >> (4 * i)) & 15;
                if (bits == 5) OwnershipWalk.TakeValue(args[i], message ?? _currentScope, this);   // built-in functions know no call scope: the value belongs to the current scope (a message: to it)
                else if (bits == 8) { if (OwnedByRunningCall(args[i])) OwnershipWalk.TakeValue(args[i], message ?? _currentScope, this); }   // `try take`: only what this call owns
                else if (bits == 3) args[i] = ReadRefArgument(args[i]);   // built-in functions and messages know no `ref`: the value
                else if (bits is 1 or 2) args[i] = ObjectCloner.Clone(args[i], message ?? _currentScope.Temp, deep: bits == 2);   // (a built-in: the copy dies at the end of the statement, SPEC 2.4)
                else if (message != null && bits is 4 or 7 && IsUnclaimed(args[i]))
                {
                    // a message owns an unclaimed argument; a direct `new` is handed over to the actor's thread (THREADING_DESIGN.md section 2)
                    if (bits == 7) args[i] = ObjectHandover.MoveAll(new[] { args[i] }, message)[0];
                    else if (args[i].Kind == ValueKind.Class) ((ObjectInstance)args[i].AsObjectRef()).ReparentTo(message);
                    else if (LeafOf(args[i]) is { } leaf) LeafOwnership.Reparent(leaf, message);
                }
            }
        }

        private void StoreSite(int site, SiteCache entry)
        {
            if (entry.Proto != null && _pendingArgNames != null) return;   // (a call with named arguments takes the slow route: FillNamedArgs)
            _chunk.EnsureSiteCaches()[site] = entry;
        }

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
                && fastLambda.Proto.ParamCount == argCount && !fastLambda.IsDestroyed && !RunsElsewhere(fastLambda))
            {
                _pendingCallTypeArgs = fastLambda.TypeArgs;   // (taken by the TakeCallTypeArgs of a lambda made in generic code)
                _pendingExplicitTypeArgs = false;
                EnterCall(fastLambda.Proto, argCount, dropBelow: true, fastLambda.OnTarget, copyMask: copyMask, lambda: fastLambda);
                return;
            }

            OpCallSlow(argCount, copyMask);
            return;
        }
        }

        /// <summary>Does a call of the lambda run elsewhere (docs/ROADMAP_TASKS.md step 8 (e))? Bound to an object of the main program and called in a fire
        /// thread: its body runs in the main program; bound to an object of another fire thread: not served yet.</summary>
        private bool RunsElsewhere(LambdaValue lambda) => lambda.Home switch
        {
            LambdaHome.MainProgram => IsFireThreadVm,
            // (an actor: elsewhere unless this is the thread that made it; a fire thread: unless this is that thread)
            LambdaHome.OtherThread => lambda.HomeBox == null
                ? !(lambda.OnTarget is ObjectInstance { Mailbox: { } mailbox } && ReferenceEquals(mailbox.Home, this))
                : !ReferenceEquals(lambda.HomeBox, _homeInbox),
            _ => false,
        };

        /// <summary>The call of a lambda whose target lives in the main program, from a fire thread: a job of the main program like `await on obj` - the
        /// arguments are copied for it, the thread waits (and yields) until the result or the exception comes back.</summary>
        private void CallAtHome(LambdaValue lambda, Value[] args)
        {
            CheckArity(lambda.Proto, args.Length);
            foreach (var a in args)
                if (a.Kind == ValueKind.Class) ObjectCopier.RequireNoCopyFree(a, "fire global");
            var holder = new Scope(null);
            for (int i = 0; i < args.Length; i++)
                if (args[i].Kind is ValueKind.Class or ValueKind.Array or ValueKind.Buffer) args[i] = ObjectCloner.Clone(args[i], holder, deep: true);
            var reply = new ActorReply();
            if (lambda.Home == LambdaHome.MainProgram) _threadBroker!.PostJob(lambda, args, holder, reply);
            else if (lambda.HomeBox is HomeInbox inbox)
            {
                // a fire thread: it runs the call at its safe points or while it waits
                if (!inbox.Post(lambda, args, reply)) { ThrowArgumentError(HomeInbox.EndedText); return; }
            }
            else if (lambda.OnTarget is ObjectInstance { Mailbox: { } mailbox })
                mailbox.Enqueue(new ActorMessage("", args, holder, reply, lambda));   // (an actor: at the `process` of its home)
            else { ThrowArgumentError("The lambda is bound to an object that lives elsewhere and cannot be reached from here."); return; }
            TakeReply(reply);
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
            if (IsDestroyedLeaf(calleeVal)) { ThrowDestroyed(calleeVal); return; }   // (a lambda that died with its owner - ROADMAP_TASKS step 8)
            if (RunsElsewhere(lambda)) { CallAtHome(lambda, args); return; }
            CheckArity(lambda.Proto, args.Length);
            args = FillDefaultArgs(lambda.Proto, args, lambda.OnTarget);

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));

            var funcScope = new Scope(_globalScope);
            foreach (var a in args) funcScope.DefineSlot(a);
            DefineCaptureSlots(lambda, funcScope);
            _pendingCallTypeArgs = lambda.TypeArgs;
            _pendingExplicitTypeArgs = false;
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
            candidate = candidate.Home;   // (the statement temporaries of a scope count as that scope)
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
            var target = _frames.Peek().ReturnScope.Temp;   // the caller's statement temporaries: the slot of the call claims it (SPEC 2.1, 2.3)
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
                case ValueKind.Lambda:   // (a lambda with its store - ROADMAP_TASKS step 8)
                {
                    if (LeafOf(retVal) is not { } leaf || leaf.IsDestroyed || !OwnershipWalk.IsLocal(leaf.LeafOwner, isLocalScope)) return;
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
            if (_accessorAdopted is { Count: > 0 }) PullBackAdopted(retVal);
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
                                if (ReferenceEquals(sc, ownerScope.Home)) return true;
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

            // (a record at the end of its (outermost) constructor - before the scopes of the call let go of what they hold: it holds only what does not
            // change, step 9)
            if (_frames.Count > 0 && _frames.Peek().ConstructedInstance is { RtClass.IsRecord: true } record
                && !_frames.Skip(1).Any(f => ReferenceEquals(f.ConstructedInstance, record)) && (_recordInit == null || !_recordInit.Contains(record))
                && RecordValues.FirstChangeableField(record) is { } changeable)
            {
                ThrowArgumentError($"The field '{changeable}' of the record '{record.ClassName}' holds {DescribeKindOf(record.Fields[changeable])}: a record holds only what does not change - numbers, texts, enums, records, lambdas without captures.");
                return;
            }

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

        /// <summary>Is the record being constructed - a constructor of it on the stack of calls, or a copy / `with` that fills it (<see cref="_recordInit"/>)?</summary>
        private bool InConstruction(ObjectInstance record)
        {
            if (_recordInit != null && _recordInit.Contains(record)) return true;
            foreach (var f in _frames) if (ReferenceEquals(f.ConstructedInstance, record)) return true;
            return false;
        }

        /// <summary>The records that a copy or `with` is filling right now (their writes are allowed).</summary>
        private HashSet<ObjectInstance>? _recordInit;

        private void ThrowRecordWrite(ObjectInstance record, string fieldName) =>
            ThrowAccessDenied($"The record '{record.ClassName}' is unchangeable: its field '{fieldName}' is written only while it is constructed - a changed copy is 'copy r with {{ .{fieldName} = ... }}'.");

        private static string DescribeKindOf(Value v) => v.Kind switch
        {
            ValueKind.Class => $"an object of the class '{((ObjectInstance)v.AsObjectRef()).ClassName}'",
            ValueKind.Array => "an array",
            ValueKind.Buffer => "a buffer",
            ValueKind.Lambda => "a lambda with captures",
            _ => v.Kind.ToString(),
        };

        private void OpNewObject()
        {
            if (PollSignals()) return;
            int site = _ip - 1;
            int classNameIdx = ReadU16();
            int argCount = ReadByte();
            long copyMask = TakeCopyMask();

            // Fast path (inline cache): class and constructor of this site are known, access and
            // argument check already passed.
            if (LookupSite(site) is { Class: { } cachedClass, Proto: { } cachedCtor } ctorEntry
                && (ctorEntry.Guard == null || ctorEntry.Guard.Holds(_stack.AsSpan(_sp - argCount, argCount))))
            {
                if (ctorEntry.Guard != null) TakeGuardedHit(cachedCtor);
                var created = new ObjectInstance(cachedClass.Name, _currentScope.Temp, cachedClass);   // unclaimed: the statement temporaries (SPEC 2.1)
                if (cachedClass.IsActor) created.Mailbox = new ActorMailbox { Home = this };
                TakeTypeArgs(created);
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
            var ctorProto = FindConstructorFor(rc, args)
                ?? throw new ScriptErrorException("ArgumentException", DescribeConstructorNotFound(rc, args.Length));
            if (ExecutionMode != VmExecutionMode.Performance
                && !IsMemberAccessAllowed(rc, ctorProto.Access ?? AccessModifier.Public))
            {
                ThrowAccessDenied(
                    $"Constructor of '{rc.Name}' is {DescribeAccess(ctorProto.Access ?? AccessModifier.Public)} and cannot be called from here.");
                return;
            }

            if (ctorProto.ParamCount == argCount && (!rc.IsConstructorTypeOverloaded(argCount) || _hintTaken != null))
                StoreSite(site, new SiteCache(rc, ctorProto, 0, guard: _hintTaken));
            var instance = new ObjectInstance(rc.Name, _currentScope.Temp, rc);   // unclaimed: the statement temporaries (SPEC 2.1)
            if (rc.IsActor) instance.Mailbox = new ActorMailbox { Home = this };
            TakeTypeArgs(instance);
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
            var ctorProto = FindConstructorFor(rc, args)
                ?? throw new ScriptErrorException("ArgumentException", DescribeConstructorNotFound(rc, args.Length));
            if (ExecutionMode != VmExecutionMode.Performance
                && !IsMemberAccessAllowed(rc, ctorProto.Access ?? AccessModifier.Public))
            {
                ThrowAccessDenied(
                    $"Constructor of '{rc.Name}' is {DescribeAccess(ctorProto.Access ?? AccessModifier.Public)} and cannot be called from here.");
                return;
            }

            var instance = new ObjectInstance(rc.Name, owner, rc);
            if (rc.IsActor) instance.Mailbox = new ActorMailbox { Home = this };
            TakeTypeArgs(instance);
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
            var ctorProto = FindConstructorFor(rc, args)
                ?? throw new ScriptErrorException("ArgumentException", DescribeConstructorNotFound(rc, args.Length));
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
                throw new ScriptErrorException("MissingMemberException", $"Strings have no field '{fieldName}' (only 'Length').");
            }

            if (target.Kind == ValueKind.Array)
            {
                if (fieldName is "Length" or "length")
                {
                    if (IsDestroyedLeaf(target)) { ThrowDestroyed(target); return false; }
                    Push(Value.MakeInt(target.AsArray().Length));
                    return true;
                }
                throw new ScriptErrorException("MissingMemberException", $"Arrays have no field '{fieldName}' (only 'Length').");
            }

            if (target.Kind == ValueKind.Buffer)
            {
                var buf = target.AsBuffer();
                if (fieldName is "Length" or "length")
                {
                    if (IsDestroyedLeaf(target)) { ThrowDestroyed(target); return false; }
                    Push(Value.MakeInt(buf.Count));   // (elements: bytes, or the words of a word buffer)
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

            var obj = RequireObjectInstance(target, "Field access");
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
            // a getter that only returns a field (FunctionProto.FieldGetterOf): read like that field, and the site caches it - as fast as a field
            if (rcGet.FindMethodWithAccess("get_" + fieldName, 0) is { Proto: { FieldGetterOf: { } backing } } plainGetter
                && obj.RtClass != null && obj.ThreadLock == null && obj.Mailbox == null && !(_threadBroker != null && obj.InGlobalsDomain)
                && obj.RtClass.FieldIndex.TryGetValue(backing, out int backingIndex) && obj.TryGetFieldLocked(backing, out var backingValue))
            {
                if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(plainGetter.DeclaringClass!, plainGetter.Access))
                {
                    ThrowAccessDenied($"'get_{fieldName}' of '{plainGetter.DeclaringClass!.Name}' is {DescribeAccess(plainGetter.Access)} and cannot be accessed from here.");
                    return false;
                }
                if (site >= 0 && plainGetter.Access == AccessModifier.Public) StoreSite(site, new SiteCache(obj.RtClass, null, backingIndex));
                Push(backingValue);
                return true;
            }
            if (rcGet.FindMethod("get_" + fieldName, 0) != null)
            {
                var result = CallMethodNested(obj, "get_" + fieldName, Array.Empty<Value>());
                if (result != null) Push(result.Value);
                return result != null;
            }

            throw new ScriptErrorException("MissingMemberException",
                $"Field '{fieldName}' does not exist on an instance of '{obj.ClassName}' " +
                $"(and there is no 'get_{fieldName}' property either).");
        }
        }

        private void OpSetField()
        {
            int site = _ip - 1;
            SetFieldAt(site, ReadU16());
        }

        /// <summary>`obj.name = value` in a property accessor (OpCode.SetFieldAccessor, SPEC 2.2 "Ownership in property accessors"): a value that belongs to
        /// the running accessor call goes to the setter like `take x` when `name` is a property of `obj`; when it is a field of `this`, `this` takes it over after
        /// the store (a getter remembers that: a `return` of the value gives it to the caller after all, see PullBackAdopted).</summary>
        private void OpSetFieldAccessor()
        {
            int site = _ip - 1;
            int fieldNameIdx = ReadU16();
            bool getter = (ReadByte() & 1) != 0;
            var value = _stack[_sp - 1];
            if (value.Kind is not (ValueKind.Class or ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda) || !IsCurrentCallOwner(OwnerOfLive(value)))
            {
                SetFieldAt(site, fieldNameIdx);
                return;
            }
            string name = _constants[fieldNameIdx].AsString();
            var target = _stack[_sp - 2].Kind == ValueKind.Class ? _stack[_sp - 2].AsObjectRef() as ObjectInstance : null;
            if (target != null && !IsDeadObject(target) && !target.HasFieldLocked(name) && ResolveClass(target.ClassName).FindMethod("set_" + name, 1) != null)
            {
                // a property: the setter call owns the value (the setter's own field rule may keep it)
                _takeSetterArg = true;
                try { SetFieldSlow(name, -1); }
                finally { _takeSetterArg = false; }
                return;
            }
            if (!SetFieldSlow(name, -1)) return;
            if (target == null || !ReferenceEquals(target, _currentThis) || !IsCurrentCallOwner(OwnerOfLive(value))) return;
            OwnershipWalk.TakeValue(value, target, this);
            if (getter) (_accessorAdopted ??= new List<(Value, int)>()).Add((value, _frames.Count));
        }

        /// <summary>`obj.name = value` that claims (OpCode.SetFieldClaim, SPEC 2.1): an unclaimed value - or, at a claim site of a setter (flags bit 1), a value that
        /// still belongs to the setter call - goes to the setter when `name` is a property of `obj`, else to `obj` after the store.</summary>
        private void OpSetFieldClaim()
        {
            int site = _ip - 1;
            int fieldNameIdx = ReadU16();
            bool setterValue = (ReadByte() & 2) != 0;
            var value = _stack[_sp - 1];
            if (!(setterValue ? OwnedByCallScope(value) : IsUnclaimed(value)))
            {
                SetFieldAt(site, fieldNameIdx);
                return;
            }
            string name = _constants[fieldNameIdx].AsString();
            var target = _stack[_sp - 2].Kind == ValueKind.Class ? _stack[_sp - 2].AsObjectRef() as ObjectInstance : null;
            if (target != null && !IsDeadObject(target) && !target.HasFieldLocked(name) && ResolveClass(target.ClassName).FindMethod("set_" + name, 1) != null)
            {
                // a property: the setter call owns the value and passes it on to its claim site
                _takeSetterArg = true;
                try { SetFieldSlow(name, -1); }
                finally { _takeSetterArg = false; }
                return;
            }
            bool toMain = _threadBroker != null && target is { InGlobalsDomain: true };
            if (toMain) _pendingGlobalTake = IdentityOf(value);   // (the store moves it into the main program - rule 10)
            if (!SetFieldSlow(name, -1)) return;
            if (toMain || target == null || IsDeadObject(target)) return;
            if (setterValue) OwnershipWalk.TakeValue(value, target, this);
            else ClaimTree(value, target);
        }

        /// <summary>Does the value belong to the function scope of the running call (an argument it was handed - a setter's `value`)?</summary>
        private bool OwnedByCallScope(Value v)
        {
            if (OwnerOfValue(v) is not Scope owner) return false;
            var function = _currentScope;
            while (function.Parent != null && !function.Parent.IsGlobal) function = function.Parent;
            return _frames.Count > 0 && ReferenceEquals(owner.Home, function);
        }

        /// <summary>The owner of a live object, array or buffer (null for anything else and for what is destroyed).</summary>
        private static IOwner? OwnerOfLive(Value v) => v.Kind switch
        {
            ValueKind.Class => v.AsObjectRef() is ObjectInstance { IsDestroyed: false } o ? o.Owner : null,
            ValueKind.Array => v.AsArray() is { IsDestroyed: false } a ? a.LeafOwner : null,
            ValueKind.Buffer => v.AsBuffer() is { IsDestroyed: false } b ? b.LeafOwner : null,
            ValueKind.Lambda => v.AsLambda() is LambdaValue { IsDestroyed: false } l ? l.LeafOwner : null,
            _ => null,
        };

        // SetFieldAccessor: the next setter call that SetFieldSlowCore makes takes its argument like `take x`
        private bool _takeSetterArg;
        // ... the values a getter's `this` took over (the getter's frame depth): its `return` of one of them gives it to the caller (SPEC 2.2)
        private List<(Value Value, int Depth)>? _accessorAdopted;

        /// <summary>`return v` of a getter: a value its `this` took over in this call (the field rule of SetFieldAccessor) belongs to the call again, so that it
        /// goes to the caller like any returned local (the return rule wins). Forgets what this call and the calls it made remembered.</summary>
        private void PullBackAdopted(Value retVal)
        {
            var list = _accessorAdopted!;
            int depth = _frames.Count;
            for (int i = list.Count - 1; i >= 0 && list[i].Depth >= depth; i--)
            {
                if (list[i].Depth != depth || !SameReference(list[i].Value, retVal) || _currentThis is not ObjectInstance self) continue;
                if (ReferenceEquals(OwnerOfLive(retVal), self)) OwnershipWalk.TakeValue(retVal, _currentScope, this);
                break;
            }
            ForgetAdopted(depth);
        }

        /// <summary>Forgets what the calls from frame depth <paramref name="depth"/> on remembered (they end).</summary>
        private void ForgetAdopted(int depth)
        {
            var list = _accessorAdopted!;
            int keep = list.Count;
            while (keep > 0 && list[keep - 1].Depth >= depth) keep--;
            if (keep < list.Count) list.RemoveRange(keep, list.Count - keep);
        }

        private static bool SameReference(Value a, Value b) => a.Kind == b.Kind && a.Kind switch
        {
            ValueKind.Class => ReferenceEquals(a.AsObjectRef(), b.AsObjectRef()),
            ValueKind.Array => ReferenceEquals(a.AsArray(), b.AsArray()),
            ValueKind.Buffer => ReferenceEquals(a.AsBuffer(), b.AsBuffer()),
            _ => false,
        };

        private void SetFieldAt(int site, int fieldNameIdx)
        {
        {

            // Schnellpfad (Inline-Cache, siehe OpGetField): Objekt derselben Klasse wie zuvor.
            if (_stack[_sp - 2] is { Kind: ValueKind.Class } cachedTarget
                && LookupSite(site) is { } fieldEntry
                && cachedTarget.AsObjectRef() is ObjectInstance cachedObj
                && ReferenceEquals(cachedObj.RtClass, fieldEntry.Class)
                && cachedObj.AccessGuard == null
                && DeclaredKind.Fits(_stack[_sp - 1], fieldEntry.RequiredKind)
                && DeclaredType.Fits(_stack[_sp - 1], fieldEntry.RequiredType, cachedObj))
            {
                var assigned = _stack[_sp - 1];
                cachedObj.Fields.SetAt(fieldEntry.FieldIndex, assigned);
                if (_watchDangling && assigned.Kind == ValueKind.Class) NoteStore(assigned, cachedObj, _constants[fieldNameIdx].AsString());
                _sp--;
                _stack[_sp - 1] = assigned;
                return;
            }

            OpSetFieldSlow(site, fieldNameIdx);
            return;
        }
        }

        // Debug mode: stores of objects that belong to a call are noted, their holders checked when they die (see DanglingWatch)
        private readonly bool _watchDangling;
        private HashSet<string>? _danglingReported;

        private static void NoteStore(Value value, object holder, string? field)
        {
            if (value.AsObjectRef() is ObjectInstance target && target.Owner is Scope or ObjectInstance) DanglingWatch.Note(target, holder, field);
        }

        public void ReportDangling(ObjectInstance dying, object holder, string? field)
        {
            if (!_watchDangling) return;
            // each place once (a loop would repeat it)
            var (sourceIndex, line) = CurrentLocation;
            string key = dying.ClassName + "|" + (holder is ObjectInstance o ? o.ClassName + "." + field : "[]") + "|" + sourceIndex + ":" + line;
            if (!(_danglingReported ??= new HashSet<string>()).Add(key)) return;
            int print = _natives.IndexOf("print");
            if (print < 0) return;
            try { _natives[print](new[] { Value.MakeString($"warning (line {line}): " + DanglingWatch.Describe(dying, holder, field)) }); }
            catch (Exception) { /* a warning never stops the program */ }
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
                try
                {
                    var holderObj = (ObjectInstance)sectionTarget.AsObjectRef();
                    if (!ToMainGlobals(ref _stack[_sp - 1], holderObj, holderObj.ThreadLock ?? _threadBroker.Lock, $"The field '{fieldName}' of an object of the globals")) return false;
                    return SetFieldSlowCore(fieldName, site);
                }
                finally { ExitGlobalsSection(); }
            }
            return SetFieldSlowCore(fieldName, site);
        }

        private bool SetFieldSlowCore(string fieldName, int site)
        {
        {
            var value = Pop();
            var obj = RequireObjectInstance(Pop(), "Field assignment");
            if (IsDeadObject(obj)) { ThrowDestroyedObject(obj); return false; }
            if (obj.RtClass is { IsRecord: true } && !InConstruction(obj)) { ThrowRecordWrite(obj, fieldName); return false; }

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

                }
                // SPEC "Unit declarations": a field with a unit converts what is assigned to it (like OpCode.CheckUnit) - in every mode, as it is part
                // of what the value means
                if (obj.RtClass?.FindFieldRequiredUnit(fieldName) is { } requiredUnitName)
                {
                    hasUnitRule = true; // every assignment must check the unit - do not cache
                    if (!AdaptFieldUnit(ref value, requiredUnitName)) return false;
                }
                byte requiredKind = obj.RtClass?.FindFieldRequiredKind(fieldName) ?? 0;
                if (!GuardFieldKind(ref value, requiredKind, fieldName)) return false;
                string? requiredType = obj.RtClass?.FindFieldRequiredType(fieldName);
                if (!GuardFieldType(ref value, requiredType, fieldName, obj)) return false;
                if (obj.RtClass?.FindFieldRequiredArray(fieldName) is { } requiredArray and not 0)
                {
                    hasUnitRule = true;   // (a typed array: every assignment types it - do not cache)
                    if (!GuardFieldArray(value, requiredArray, obj.RtClass.FindFieldRequiredUnit(fieldName))) return false;
                }
                if (site >= 0 && !hasUnitRule && obj.RtClass != null && obj.AccessGuard == null && !obj.RtClass.IsRecord
                    && obj.RtClass.FieldIndex.TryGetValue(fieldName, out int setIndex))
                    StoreSite(site, new SiteCache(obj.RtClass, null, setIndex, requiredKind: requiredKind, requiredType: requiredType));
                obj.SetFieldLocked(fieldName, value);
                if (_watchDangling && value.Kind == ValueKind.Class) NoteStore(value, obj, fieldName);
                Push(value);
                return true;
            }

            // No existing field of this name - try property setter
            // (naming convention 'set_'+name).
            var rcSet = ResolveClass(obj.ClassName);
            if (rcSet.FindMethod("set_" + fieldName, 1) != null)
            {
                bool takeArg = _takeSetterArg;
                _takeSetterArg = false;
                var result = CallMethodNested(obj, "set_" + fieldName, new[] { value }, takeFirst: takeArg);
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
            if (_watchDangling && value.Kind == ValueKind.Class) NoteStore(value, obj, fieldName);
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
                && fastThis.ThreadLock == null
                && DeclaredKind.Fits(_stack[_sp - 1], thisEntry.RequiredKind)
                && DeclaredType.Fits(_stack[_sp - 1], thisEntry.RequiredType, fastThis))
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
            if (oi.RtClass is { IsRecord: true } && !InConstruction(oi)) { ThrowRecordWrite(oi, fieldName); return; }

            // SPEC "Unit declarations" - the same check as in
            // SetField (see there for the reason why this happens at
            // runtime instead of compile time). This opcode
            // is used for the field INITIALISERS themselves (see
            // Compiler.CompileConstructorProto) - `int x : mm = 5`
            // would let the first, declared
            // value pass completely unchecked without this check here.
            bool hasUnitRule = false;
            if (oi.RtClass != null)   // (converting into the unit of the field is part of what the value means: in every mode)
            {
                string? requiredUnitName = oi.RtClass.FindFieldRequiredUnit(fieldName);
                if (requiredUnitName != null)
                {
                    hasUnitRule = true; // every assignment must check the unit - do not cache
                    if (!AdaptFieldUnit(ref value, requiredUnitName)) return;
                }
            }
            byte requiredKind = oi.RtClass?.FindFieldRequiredKind(fieldName) ?? 0;
            if (!GuardFieldKind(ref value, requiredKind, fieldName)) return;
            string? requiredType = oi.RtClass?.FindFieldRequiredType(fieldName);
            if (!GuardFieldType(ref value, requiredType, fieldName, oi)) return;
            if (oi.RtClass?.FindFieldRequiredArray(fieldName) is { } requiredArray and not 0)
            {
                hasUnitRule = true;   // (a typed array: every assignment types it - do not cache)
                if (!GuardFieldArray(value, requiredArray, oi.RtClass.FindFieldRequiredUnit(fieldName))) return;
            }

            if (!hasUnitRule && oi.RtClass != null && oi.ThreadLock == null && !oi.RtClass.IsRecord
                && oi.RtClass.FieldIndex.TryGetValue(fieldName, out int thisIndex))
                StoreSite(site, new SiteCache(oi.RtClass, null, thisIndex, requiredKind: requiredKind, requiredType: requiredType));
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
            _awaitReplyNow = _awaitReplyNext;
            _awaitReplyNext = false;

            // Fast path (inline cache, see SiteCache): an object of the same class as at the last call
            // of this site - method, access and argument checks are already done.
            if (_stack[_sp - 1 - argCount] is { Kind: ValueKind.Class } cachedTarget
                && LookupSite(site) is { Proto: { } cachedMethod } siteEntry
                && cachedTarget.AsObjectRef() is ObjectInstance cachedObj
                && ReferenceEquals(cachedObj.RtClass, siteEntry.Class)
                && cachedObj.Mailbox == null
                && (_threadBroker == null || _sectionDepth > 0 || !cachedObj.InGlobalsDomain)
                && (siteEntry.Guard == null || siteEntry.Guard.Holds(_stack.AsSpan(_sp - argCount, argCount))))
            {
                if (siteEntry.Guard != null) TakeGuardedHit(cachedMethod);
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
                // `foreach (c in text)` likewise: the enumerator indexes the string, `text[i]` is a char.
                if (methodName == "GetEnumerator" && args.Length == 0
                    && target.Kind is ValueKind.Array or ValueKind.Buffer or ValueKind.String
                    && _classes.TryGetValue("ListEnumerator", out var enumeratorClass))
                {
                    long itemCount = target.Kind switch
                    {
                        ValueKind.Array => target.AsArray().Length,
                        ValueKind.Buffer => target.AsBuffer().Count,
                        _ => target.AsString().Length,
                    };
                    var enumerator = ConstructNested(enumeratorClass, new[] { target, Value.MakeInt(itemCount) });
                    Push(Value.MakeClassRef(enumerator));
                    return;
                }

                // Methods from a base-type extension (`class extends string { ... }`,
                // SPEC 5.5.1 - e.g. IndexOf/Substring in the prelude): like an object call, only
                // `this` is the value itself. Before the fixed built-in conversions below.
                if (_baseTypeClasses[(int)target.Kind] is { } extensionRc)
                {
                    var (extProto, extDeclaringRc, extAccess) = FindOverload(extensionRc, methodName, args, null);
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
                RejectArgNames("the built-in method '" + methodName + "'");
                if (TryCallBuiltinMethod(target, methodName, args, out Value builtinResult))
                {
                    AdoptFresh(builtinResult);
                    Push(builtinResult);
                    return;
                }
                throw new ScriptErrorException("MissingMemberException",
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
            // (the ownership methods of an actor - the claim of an assignment or of `list.Add(actor)`, `try x.TakeTo(this)` - are never messages: at its home
            // they run as for any object; from another thread the actor stays with its home and only its reference is stored)
            bool ownershipCall = methodName is "TakeLocal" or "TakeUpwards" or "TakeGlobal" or "TakeTo" or "tryTakeLocal" or "tryTakeUpwards" or "tryTakeGlobal" or "tryTakeTo";
            if (obj.Mailbox != null && ownershipCall && !ReferenceEquals(obj.Mailbox.Home, this))
            {
                Push(Value.MakeBool(false));
                return;
            }
            if (obj.Mailbox != null && !ownershipCall && !(_awaitReplyNow && ReferenceEquals(obj.Mailbox.Home, this)))
            {
                var holder = new Scope(null);   // the message owns its unclaimed arguments until it runs (SPEC 2.4)
                if (copyMask != 0) ApplyCopyMaskToArgs(args, copyMask, holder);
                RejectArgNames("a method of an actor");
                if (_awaitReplyNow)
                {
                    // `await actor.Method()`: the message has a way back; this thread waits until the actor's home ran it (at its `process`)
                    _awaitReplyNow = false;
                    var reply = new ActorReply();
                    obj.Mailbox.Enqueue(new ActorMessage(methodName, args, holder, reply));
                    TakeReply(reply);
                    return;
                }
                obj.Mailbox.Enqueue(new ActorMessage(methodName, args, holder));
                Push(Value.MakeUndefined());
                return;
            }
            _awaitReplyNow = false;   // (an `await` of the actor's own home runs the method at once, like a call)

            // A fire thread calls a method of an object of the main program's globals: it runs in the main program (rule 10 of step 8) - a job, the
            // thread waits (and serves its own calls) until the result or the exception comes back.
            if (NeedsSection(obj))
            {
                CallGlobalsMethodAtHome(obj, methodName, args, copyMask);
                return;
            }

            var rc = ResolveClass(obj.ClassName);
            var (proto, declaringRcCall, accessCall) = FindOverload(rc, methodName, args, obj);
            if (proto == null)
            {
                // The ownership transfer (SPEC 2.2) is there for every object, without the class declaring it.
                if (TryCallOwnershipMethod(obj, methodName, args, out var ownershipValue))
                {
                    Push(ownershipValue);
                    return;
                }
                throw MethodNotFound(rc, methodName, args.Length);
            }
            if (ExecutionMode != VmExecutionMode.Performance && !IsMemberAccessAllowed(declaringRcCall!, accessCall))
            {
                ThrowAccessDenied(
                    $"Method '{methodName}' of '{declaringRcCall!.Name}' is {DescribeAccess(accessCall)} " +
                    "and cannot be called from here.");
                return;
            }
            CheckArity(proto, args.Length);
            if (proto.ParamCount == argCount && obj.RtClass != null && (!rc.IsTypeOverloaded(methodName, argCount) || _hintTaken != null))
            {
                var forwarder = proto.Forwarder;
                int forwarderField = -1;
                if (forwarder != null && !obj.RtClass.FieldIndex.TryGetValue(forwarder.FieldName, out forwarderField))
                    forwarder = null;
                StoreSite(site, new SiteCache(obj.RtClass, proto, 0, forwarder, forwarderField, guard: _hintTaken));
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
            bool currentThread = TakeGlobalFlag(method, ref args);
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
                    if (_threadBroker != null && !currentThread)
                    {
                        // (to the main program: what goes along by the mode comes below the object first, in the thread - then the check and the move)
                        if (mode != Takes.This && !obj.IsDestroyed) OwnershipWalk.MoveReachable(Value.MakeClassRef(obj), mode, OwnsWithinCall, obj, EnumerateItems);
                        if (TakeGlobalSafe(Value.MakeClassRef(obj))) TakeGlobalMain(Value.MakeClassRef(obj));
                        return true;
                    }
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

        /// <summary>`x.TakeGlobal(currentThread)` (also after a `Takes` mode): the flag is the last argument, a bool - taken off `args`. False (the default):
        /// the global scope of the main program, also in a fire thread; true: the global scope of the running thread.</summary>
        private static bool TakeGlobalFlag(string method, ref Value[] args)
        {
            if (method != "TakeGlobal" || args.Length == 0 || args[^1].Kind != ValueKind.Bool) return false;
            bool flag = args[^1].AsBool();
            args = args[..^1];
            return flag;
        }

        /// <summary>`x.TakeGlobal()` in a fire thread: x goes to the global scope of the main program and into the globals' domain (in a section); it keeps its
        /// identity - the thread's references stay valid and reach it as an object of the globals (rule 10).</summary>
        private void TakeGlobalMain(Value v)
        {
            var broker = _threadBroker!;
            EnterGlobalsSection();
            try
            {
                if (v.Kind == ValueKind.Class) ((ObjectInstance)v.AsObjectRef()).ReparentTo(broker.Scope);
                else if (LeafOf(v) is { } leaf) LeafOwnership.Reparent(leaf, broker.Scope);
                ShareValue(v, broker.Lock, new HashSet<object>(ReferenceEqualityComparer.Instance));
                RehomeToMain(v);
            }
            finally { ExitGlobalsSection(); }
        }

        /// <summary>Before `x.TakeGlobal()` in a fire thread: what x and what it owns point to must be in what moves, the main program's already (its globals, an
        /// actor) or a value - else the main program would reach an object of this thread through a field: the ArgumentException (false), nothing moves.</summary>
        private bool TakeGlobalSafe(Value root)
        {
            object? rootNode = IdentityOf(root);
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var work = new Stack<Value>();
            work.Push(root);
            while (work.Count > 0)
            {
                var v = work.Pop();
                IEnumerable<Value> refs = v.Kind switch
                {
                    ValueKind.Class => ((ObjectInstance)v.AsObjectRef()).Fields.Select(f => f.Value)
                        .Concat(((ObjectInstance)v.AsObjectRef()).OwnedObjects.Select(Value.MakeClassRef)),
                    ValueKind.Array => ArrayItems(v.AsArray()),
                    ValueKind.Lambda => ((LambdaValue)v.AsLambda()).Captures ?? Array.Empty<Value>(),
                    _ => Array.Empty<Value>(),
                };
                foreach (var r in refs)
                {
                    if (r.Kind is not (ValueKind.Class or ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda)) continue;
                    if (r.Kind == ValueKind.Class ? ((ObjectInstance)r.AsObjectRef()).IsDestroyed : IsDestroyedLeaf(r)) continue;
                    if (InMainGlobals(r)) continue;
                    var node = IdentityOf(r)!;
                    if (!ReferenceEquals(node, rootNode) && !OwnedWithin(rootNode!, node))
                    {
                        ThrowArgumentError("'TakeGlobal': the value points to an object of this thread that it does not own - the main program would reach it. Take it along "
                            + "(TakeGlobal(Takes.Locals), Takes.All), let the value own it (TakeTo) or keep it in this thread (TakeGlobal(true)).");
                        return false;
                    }
                    if (seen.Add(node)) work.Push(r);
                }
            }
            return true;
        }

        private static IEnumerable<Value> ArrayItems(ScriptArray a)
        {
            for (long i = 0; a.TryGet(i, out var item); i++) yield return item;
        }

        /// <summary>Is `root` in the chain of owners of `node`?</summary>
        private static bool OwnedWithin(object root, object node)
        {
            object? o = node switch { ObjectInstance oi => oi.Owner, IOwnedLeaf l => l.LeafOwner, _ => null };
            for (int guard = 0; o != null && guard < 100000; guard++)
            {
                if (ReferenceEquals(o, root)) return true;
                o = o switch { ObjectInstance oi => oi.Owner, IOwnedLeaf l => l.LeafOwner, _ => null };
            }
            return false;
        }

        /// <summary>(a lambda in what moved whose calls were to come to this thread - bound to what is the main program's now: its calls go there)</summary>
        private void RehomeToMain(Value root)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var work = new Stack<Value>();
            work.Push(root);
            while (work.Count > 0)
            {
                var v = work.Pop();
                switch (v.Kind)
                {
                    case ValueKind.Class when v.AsObjectRef() is ObjectInstance { IsDestroyed: false, InGlobalsDomain: true } obj && seen.Add(obj):
                        foreach (var field in obj.Fields) work.Push(field.Value);
                        break;
                    case ValueKind.Array when !v.AsArray().IsDestroyed && seen.Add(v.AsArray()):
                        for (long i = 0; v.AsArray().TryGet(i, out var item); i++) work.Push(item);
                        break;
                    case ValueKind.Lambda when v.AsLambda() is LambdaValue { IsDestroyed: false } l && seen.Add(l):
                        if (l.Home == LambdaHome.OtherThread && ReferenceEquals(l.HomeBox, _homeInbox) && l.OnTarget is ObjectInstance { InGlobalsDomain: true, Mailbox: null })
                        {
                            l.Home = LambdaHome.MainProgram;
                            l.HomeBox = null;
                        }
                        if (l.Captures != null) foreach (var c in l.Captures) work.Push(c);
                        break;
                }
            }
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
            bool currentThread = TakeGlobalFlag(method, ref args);
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
                case "TakeGlobal" when _threadBroker != null && !currentThread:
                {
                    var leafValue = leaf switch { ScriptArray a => Value.MakeArray(a), LambdaValue l => Value.MakeLambda(l), _ => Value.MakeBuffer((ByteBuffer)leaf) };
                    if (mode != Takes.This && !leaf.IsDestroyed) OwnershipWalk.MoveReachable(leafValue, mode, OwnsWithinCall, _currentScope, EnumerateItems);
                    if (TakeGlobalSafe(leafValue)) TakeGlobalMain(leafValue);
                    return true;
                }
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
                OwnershipWalk.MoveReachable(leaf switch { ScriptArray a => Value.MakeArray(a), LambdaValue l => Value.MakeLambda(l), _ => Value.MakeBuffer((ByteBuffer)leaf) }, mode, OwnsWithinCall, target, EnumerateItems);
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
                case ValueKind.Lambda:
                    LeafOwnership.Destroy(LeafOf(v)!, this);
                    return;
                default:
                    throw new InvalidOperationException($"'delete' expects an object, an array, a buffer or a lambda, not {v.Kind}.");
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
            var (proto, declaringRcBase, accessBase) = FindOverload(rc, methodName, args, _currentThis as ObjectInstance);
            if (proto == null)
                throw MethodNotFound(rc, methodName, args.Length);
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
                if (requiredUnitName != null && !AdaptFieldUnit(ref setValue, requiredUnitName)) return;
            }
            if (!GuardFieldKind(ref setValue, setOwner.FindFieldRequiredKind(setFieldName), setFieldName)) return;
            if (!GuardFieldType(ref setValue, setOwner.FindFieldRequiredType(setFieldName), setFieldName, null)) return;
            if (!GuardFieldArray(setValue, setOwner.FindFieldRequiredArray(setFieldName), setOwner.FindFieldRequiredUnit(setFieldName))) return;

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
                    // (rule 10: what a fire thread stores here is the main program's - see ToMainGlobals)
                    if (staticInThread && !ToMainGlobals(ref setValue, _threadBroker!.Scope, _threadBroker.Lock, "A static field")) return;
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
            if (LookupSite(site) is { Proto: { } cachedStatic } staticEntry
                && (staticEntry.Guard == null || staticEntry.Guard.Holds(_stack.AsSpan(_sp - callArgCount, callArgCount))))
            {
                if (staticEntry.Guard != null) TakeGuardedHit(cachedStatic);
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
            var (callProto, declaringRcCall, accessCall) = FindOverload(callRc, callMethodName, callArgs, null);
            if (callProto == null)
                throw MethodNotFound(callRc, callMethodName, callArgs.Length);
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

            if (callProto.ParamCount == callArgCount && (!callRc.IsTypeOverloaded(callMethodName, callArgCount) || _hintTaken != null))
                StoreSite(site, new SiteCache(null, callProto, 0, guard: _hintTaken));

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
            LeafOwnership.Adopt(created, _currentScope.Temp);
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
            LeafOwnership.Adopt(arr, _currentScope.Temp);
            ClaimElements(arr);
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
            if (indexVal.Kind == ValueKind.Word && target.Kind is ValueKind.Array or ValueKind.Buffer or ValueKind.String) indexVal = indexVal.CoerceType(ValueKind.Int);   // (a word as an index: its bits as an int)
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
            else if (target.Kind == ValueKind.Buffer && target.AsBuffer().IsWords)
            {
                // a buffer of machine words (`word a[]`, SPEC 8.10): the element is a word
                var words = target.AsBuffer();
                long idx = indexVal.AsInt();
                if (idx >= 0 && idx < words.Count) Push(Value.MakeWord(words.GetWord(idx)));
                else ThrowIndexOutOfBounds(idx, words.Count);
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
                    if (_watchDangling && assigned.Kind == ValueKind.Class) NoteStore(assigned, fastArray, null);
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
            if (indexVal.Kind == ValueKind.Word && target.Kind is ValueKind.Array or ValueKind.Buffer or ValueKind.String) indexVal = indexVal.CoerceType(ValueKind.Int);   // (a word as an index: its bits as an int)
            if (IsDestroyedLeaf(target)) { ThrowDestroyed(target); return; }
            // a typed array (`int[8] a[]`): the value is converted (the assignment yields the stored value), or it is the exception - before the index is checked, as natively
            if (target.Kind == ValueKind.Array && !ConvertArrayElement(target.AsArray(), ref value)) return;

            if (target.Kind == ValueKind.Array && target.AsArray().IsShared)
            {
                // Array of the shared area: a fire thread changes it only in a section, everywhere under the lock
                long sharedIdx = indexVal.AsInt();
                if (_threadBroker != null)
                {
                    // (rule 10: an object of this thread goes there only fresh or taken - see ToMainGlobals)
                    EnterGlobalsSection();
                    try { if (!ToMainGlobals(ref value, target.AsArray(), _threadBroker.Lock, "An element of an array of the globals")) return; }
                    finally { ExitGlobalsSection(); }
                }
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
                    {
                        if (_watchDangling && value.Kind == ValueKind.Class) NoteStore(value, target.AsArray(), null);
                        Push(value);
                    }
                    else
                        // No push here - ThrowIndexOutOfBounds has already
                        // redirected _currentChunk/_ip, an
                        // additional push would shift the stack there.
                        ThrowIndexOutOfBounds(idx, target.AsArray().Length);
                }
            }
            else if (target.Kind == ValueKind.Buffer && target.AsBuffer().IsWords)
            {
                // a buffer of machine words (`word a[]`, SPEC 8.10): an int or a word, cut to the width
                if (value.Kind is not (ValueKind.Int or ValueKind.Word))
                {
                    ThrowArgumentError($"An element of a word buffer must be an int or a word, not {value.Kind}.");   // (the same text natively)
                    return;
                }
                var words = target.AsBuffer();
                long idx = indexVal.AsInt();
                if (idx < 0 || idx >= words.Count) { ThrowIndexOutOfBounds(idx, words.Count); return; }
                var stored = value.ToWord();
                words.SetWord(idx, stored.AsWord());
                Push(stored);
            }
            else if (target.Kind == ValueKind.Buffer)
            {
                if (value.Kind == ValueKind.Word) value = value.CoerceType(ValueKind.Int);   // (a word into a byte buffer: cut like an int)
                if (value.Kind != ValueKind.Int)
                {
                    // (no push: the exception has redirected _ip already)
                    ThrowArgumentError($"An element of a byte buffer must be an int, not {value.Kind}.");   // (the same text natively)
                    return;
                }
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
                var result = CallMethodNested(obj, "SetIndex", new[] { indexVal, value }, adoptUnclaimed: 0b11); // (the key and the value are slots of the container, SPEC 2.1) Return value unused
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
                    else
                    {
                        int declared = _currentScope.DefineSlot(Pop());
                        if (ExecutionMode != VmExecutionMode.Performance) NameDeclaredSlot(declared);
                    }
                    break;

                case OpCode.StepAdd:
                case OpCode.StepSub:
                    _stack[_sp - 1] = Value.StepOf(in _stack[_sp - 2], _stack[_sp - 1]);
                    Execute(op == OpCode.StepAdd ? OpCode.Add : OpCode.Sub);
                    break;
                case OpCode.Add: BinaryNumericOrOperator(Value.Add, "operator+"); break;
                case OpCode.Sub: BinaryNumericOrOperator(Value.Subtract, "operator-"); break;
                case OpCode.Mul: BinaryNumericOrOperator(Value.Multiply, "operator*"); break;
                case OpCode.Div: BinaryNumericOrOperator(Value.Divide, "operator/"); break;
                case OpCode.Mod: BinaryNumericOrOperator(Value.Modulo, "operator%"); break;
                case OpCode.Power: BinaryNumericOrOperator(Value.Power, "operator^"); break;
                case OpCode.PowerFloat: BinaryNumericOrOperator(Value.PowerFloat, "operator^"); break;
                case OpCode.AwaitReply: _awaitReplyNext = true; break;
                case OpCode.Dedicate: case OpCode.Undedicate: break;
                case OpCode.CancelPush: { int f = ReadByte(); UntilPush(f & 0x7F, (f & 0x80) != 0); break; }
                case OpCode.CancelPop: UntilPop(); break;   // (`dedicated`: a native build moves the task to a system thread; a VM thread is one)
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


                case OpCode.Neg: ReplaceOneGuarded(Value.Negate); break;
                case OpCode.LogicalNot: ReplaceOneGuarded(Value.LogicalNot); break;
                case OpCode.BitNot: ReplaceOneGuarded(Value.BitNot); break;

                case OpCode.Eq:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(Value.OperatorEquals(a, b)), "operator==");
                    break;
                case OpCode.NotEq:
                    BinaryNumericOrOperator((a, b) => Value.MakeBool(!Value.OperatorEquals(a, b)), "operator!=");
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
                case OpCode.RelabelUnitDynamic:
                {
                    var candidate = Pop();
                    var anchor = Peek();
                    Push(candidate.RelabelUnit(anchor.Unit ?? Unit.Unitless));
                    break;
                }
                case OpCode.RelabelUnit:
                {
                    var unit = _currentChunk.Units[ReadU16()];
                    Push(Pop().RelabelUnit(unit));
                    break;
                }
                case OpCode.CastTo:
                {
                    string typeName = _constants[ReadU16()].AsString();
                    var v = Peek();
                    if (ReadsDeadClass(v, typeName, out var deadCast)) { ThrowDestroyedObject(deadCast); break; }
                    if (v.Kind != ValueKind.Undefined && !IsOfType(v, typeName))
                    {
                        string have = v.Kind == ValueKind.Class ? ((ObjectInstance)v.AsObjectRef()).ClassName : v.Kind.ToString();
                        ThrowArgumentError($"Cannot convert a value of type '{have}' to '{typeName}'.");
                    }
                    break;
                }
                case OpCode.ToEnum:
                {
                    var type = _constants[ReadU16()].Enum!;
                    var v = Pop();
                    if (v.Kind == ValueKind.Int && (!v.IsEnum || v.EnumId == type.Id)) Push(Value.MakeEnum(v.AsInt(), type));
                    else if (v.Kind == ValueKind.Undefined) Push(v);
                    else { Push(v); ThrowArgumentError($"Cannot convert a value of type '{(v.IsEnum ? v.Enum!.Name : v.Kind.ToString())}' to '{type.Name}'."); }
                    break;
                }
                case OpCode.RebindLambda:
                {
                    var onTarget = Pop();
                    var lambdaValue = Pop();
                    if (lambdaValue.Kind != ValueKind.Lambda)
                    {
                        ThrowArgumentError("'on' binds a lambda to an object; the value before 'on' is not a lambda.");
                        break;
                    }
                    if (IsDestroyedLeaf(lambdaValue)) { ThrowDestroyed(lambdaValue); break; }
                    // (its own store: copies of the first lambda's - each lambda owns what it holds)
                    var rebound = ((LambdaValue)lambdaValue.AsLambda()).WithOnTarget(BoxValueForOnTarget(onTarget));
                    LeafOwnership.Adopt(rebound, _currentScope.Temp);
                    if (rebound.Captures != null)
                        for (int i = 0; i < rebound.Captures.Length; i++) rebound.Captures[i] = ObjectCloner.Clone(rebound.Captures[i], rebound, deep: true);
                    Push(Value.MakeLambda(rebound));
                    break;
                }
                case OpCode.TypeArray:
                {
                    byte elemType = ReadByte();
                    int rank = ReadByte();
                    int unitIdx = ReadU16();
                    if (elemType is ArrayElem.U8 or ArrayElem.Word)
                    {
                        // `byte a[]` holds a byte buffer (SPEC 8.4.2), `word a[]` a buffer of machine words (SPEC 8.10): an array stored there becomes one
                        var top = Peek();
                        if (BufferFromArrayValue(ref top, null, rank, 1, elemType == ArrayElem.Word)) _stack[_sp - 1] = top;
                        break;
                    }
                    TypeArrayValue(Peek(), elemType, rank, unitIdx == 0xFFFF ? null : _constants[unitIdx].AsString(), 1);
                    break;
                }
                case OpCode.Trust:
                    break;   // (`trust x`: only the native backend acts on it)
                case OpCode.TypeArrayOfParam:
                {
                    string paramText = _constants[ReadU16()].AsString();
                    if (DeclaredType.TypedElementOf(TypeArgOfParam(paramText)) is int code) TypeArrayValue(Peek(), (byte)(code & 0xFF), 1, null, 1);
                    break;
                }
                case OpCode.ConvertWidth:
                {
                    int bits = ReadByte();
                    int flags = ReadByte();
                    if ((flags & 8) != 0)
                    {
                        // `word` (SPEC 8.10): implicit - a store into a word - converts an int (and a word: nothing to do), `x!word` every number
                        if ((flags & 4) != 0)
                        {
                            // (a store: an int converted, a word and undefined stay, anything else - a float too - wants `!word`)
                            var stored = Peek().Kind;
                            if (stored == ValueKind.Int) _stack[_sp - 1] = Peek().ToWord();
                            else if (stored is not (ValueKind.Word or ValueKind.Undefined))
                                ThrowArgumentError($"A word takes an int or a word, not {stored} (convert with '!word').");
                            break;
                        }
                        Push(CoerceTypeChecked(Pop(), ValueKind.Word));
                        break;
                    }
                    // implicit (an assignment to a declared width): only a number of the declared kind is cut (a word counts as an int)
                    if ((flags & 4) != 0 && Peek().Kind != ((flags & 1) != 0 ? ValueKind.Float : ValueKind.Int) && !((flags & 1) == 0 && Peek().Kind == ValueKind.Word)) break;
                    Push(CoerceWidthChecked(Pop(), bits, (flags & 1) != 0, (flags & 2) != 0));
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
                    var coerced = CoerceTypeChecked(Pop(), TagToKind(tag));
                    // (`x!int`: the program's int - an int that keeps a wider width is cut, TC021)
                    if (tag == TypeTag.Int && coerced.Wide != 0 && Value.Ints32) coerced = coerced.WithWide(0);
                    Push(coerced);
                    break;
                }
                case OpCode.CoerceTypeDynamic:
                {
                    var candidate = Pop();
                    var anchor = Peek();
                    Push(CoerceTypeChecked(candidate, anchor.Kind));
                    break;
                }

                case OpCode.Jump:
                    _ip = ReadU16();
                    break;

                case OpCode.JumpIfFalse:
                {
                    int addr = ReadU16();
                    var condition = Pop();
                    if (condition.Kind != ValueKind.Bool) { ThrowConditionError(condition); break; }
                    if (!condition.AsBool()) _ip = addr;
                    break;
                }
                case OpCode.JumpIfFalsePeek:
                {
                    int addr = ReadU16();
                    if (Peek().Kind != ValueKind.Bool) { ThrowConditionError(Peek()); break; }
                    if (!Peek().AsBool()) _ip = addr;
                    break;
                }
                case OpCode.JumpIfTruePeek:
                {
                    int addr = ReadU16();
                    if (Peek().Kind != ValueKind.Bool) { ThrowConditionError(Peek()); break; }
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

                    // `inline c` (ROADMAP 13.2): its body is C - only a native build has it (a host may still register the name)
                    if (_externSignatures.TryGetValue(externName, out var inlineSig) && inlineSig.InlineC != null && !_externs.TryGet(externName, out _))
                        throw new InvalidOperationException(
                            $"'{inlineSig.DisplayName ?? externName}' is an inline C function: it runs only in a native build " +
                            "(give the virtual machine an alternative with '#if native ... #else ... #endif').");

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
                    Push(ObjectCloner.Clone(source, _currentScope.Temp, deep));
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

                case OpCode.SetFieldAccessor:
                    OpSetFieldAccessor();
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

                    if (initOwner.FindFieldRequiredUnit(initFieldName) is { } requiredUnitName && !AdaptFieldUnit(ref initValue, requiredUnitName)) break;
                    if (!GuardFieldKind(ref initValue, initOwner.FindFieldRequiredKind(initFieldName), initFieldName)) break;
                    if (!GuardFieldType(ref initValue, initOwner.FindFieldRequiredType(initFieldName), initFieldName, null)) break;
                    if (!GuardFieldArray(initValue, initOwner.FindFieldRequiredArray(initFieldName), initOwner.FindFieldRequiredUnit(initFieldName))) break;

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
                    LeafOwnership.Adopt(arr, _currentScope.Temp);
                    ClaimElements(arr);
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
                    LeafOwnership.Adopt(outer, _currentScope.Temp);
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
                    if (_stack[_sp - 1].Kind is ValueKind.Class or ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda) HoistValue(_stack[_sp - 1]);
                    break;

                case OpCode.TakeToScope:
                {
                    int depth = ReadU16();
                    if (ThrowIfDeadForTake(_stack[_sp - 1])) break;
                    OwnershipWalk.TakeValue(_stack[_sp - 1], depth == 0xFFFF ? _globalScope : _currentScope.GetAncestor(depth), this);
                    if (depth == 0xFFFF && _threadBroker != null) _pendingGlobalTake = IdentityOf(_stack[_sp - 1]);   // (a global of the main program: rule 10)
                    break;
                }

                case OpCode.TakeCheck:
                    ThrowIfDeadForTake(_stack[_sp - 1]);
                    break;

                // `try take x` (SPEC 2.2): like the three above, only when the running call owns the value - otherwise nothing changes
                case OpCode.TryTakeToScope:
                {
                    int depth = ReadU16();
                    if (OwnedByRunningCall(_stack[_sp - 1])) OwnershipWalk.TakeValue(_stack[_sp - 1], depth == 0xFFFF ? _globalScope : _currentScope.GetAncestor(depth), this);
                    break;
                }
                case OpCode.TryTakeToObject:
                {
                    var holder = RequireObjectInstance(_stack[_sp - 2], "take");
                    if (OwnedByRunningCall(_stack[_sp - 1])) OwnershipWalk.TakeValue(_stack[_sp - 1], holder, this);
                    break;
                }
                case OpCode.TryTakeToArray:
                {
                    var holder = _stack[_sp - 3];
                    if (IsDestroyedLeaf(holder)) { ThrowDestroyed(holder); break; }
                    if (holder.Kind == ValueKind.Array && OwnedByRunningCall(_stack[_sp - 1])) OwnershipWalk.TakeValue(_stack[_sp - 1], holder.AsArray(), this);
                    break;
                }

                case OpCode.ReleaseTemps:
                    _currentScope.ReleaseTemps(this);
                    break;

                case OpCode.ClaimToScope:
                {
                    int depth = ReadU16();
                    int flags = ReadByte();
                    // (a fresh value for a global of the main program: its store moves it there - rule 10)
                    if (depth == 0xFFFF && _threadBroker != null && IsUnclaimed(_stack[_sp - 1])) _pendingGlobalTake = IdentityOf(_stack[_sp - 1]);
                    ClaimToScope(_stack[_sp - 1], depth == 0xFFFF ? _globalScope : _currentScope.GetAncestor(depth), flags);
                    break;
                }

                case OpCode.ClaimToObject:
                {
                    var value = Pop();
                    var holderValue = Pop();
                    if (holderValue.Kind == ValueKind.Class && holderValue.AsObjectRef() is ObjectInstance holder && !holder.IsDestroyed)
                    {
                        // (for an object of the main program's globals: the store moves it there - rule 10)
                        if (_threadBroker != null && holder.InGlobalsDomain) { if (IsUnclaimed(value)) _pendingGlobalTake = IdentityOf(value); }
                        else ClaimTree(value, holder);
                    }
                    Push(value);
                    break;
                }

                case OpCode.SetFieldClaim: OpSetFieldClaim(); break;

                case OpCode.ClaimToArray:
                {
                    var holder = _stack[_sp - 3];
                    if (holder.Kind == ValueKind.Array && !holder.AsArray().IsDestroyed)
                    {
                        // (for an array of the main program's globals: the store moves it there - rule 10)
                        if (_threadBroker != null && holder.AsArray().IsShared) { if (IsUnclaimed(_stack[_sp - 1])) _pendingGlobalTake = IdentityOf(_stack[_sp - 1]); }
                        else ClaimTree(_stack[_sp - 1], holder.AsArray());
                    }
                    break;
                }

                case OpCode.TakeToObject:
                {
                    var holder = RequireObjectInstance(_stack[_sp - 2], "take");
                    if (ThrowIfDeadForTake(_stack[_sp - 1])) break;
                    // (a fire thread takes it for an object of the main program's globals: it moves there with the store - rule 10)
                    if (_threadBroker != null && holder.InGlobalsDomain) { _pendingGlobalTake = IdentityOf(_stack[_sp - 1]); break; }
                    OwnershipWalk.TakeValue(_stack[_sp - 1], holder, this);
                    break;
                }

                case OpCode.TakeToArray:
                {
                    var holder = _stack[_sp - 3];
                    if (IsDestroyedLeaf(holder)) { ThrowDestroyed(holder); break; }
                    if (ThrowIfDeadForTake(_stack[_sp - 1])) break;
                    // (a fire thread takes it for an array of the main program's globals: it moves there with the store - rule 10)
                    if (holder.Kind == ValueKind.Array && _threadBroker != null && holder.AsArray().IsShared) { _pendingGlobalTake = IdentityOf(_stack[_sp - 1]); break; }
                    if (holder.Kind == ValueKind.Array) OwnershipWalk.TakeValue(_stack[_sp - 1], holder.AsArray(), this);
                    break;
                }

                case OpCode.Delete:
                    DeleteValue(Pop());
                    break;

                case OpCode.GuardLocal: OpGuardLocal(); break;
                case OpCode.SetTypeArgs: OpSetTypeArgs(); break;
                case OpCode.OverloadHint:
                {
                    int hintIdx = ReadU16();
                    var parsed = _chunk.ParsedHints ??= new OverloadHint?[_constants.Length];
                    _pendingHint = parsed[hintIdx] ??= OverloadHint.Of(_constants[hintIdx].AsString());
                    break;
                }
                case OpCode.ArgNames:
                {
                    string text = _constants[ReadU16()].AsString();
                    if (!_argNameLists.TryGetValue(text, out var names)) _argNameLists[text] = names = text.Split('|');
                    _pendingArgNames = names;
                    break;
                }
                case OpCode.TakeCallTypeArgs: _currentScope.TypeArgs = _pendingCallTypeArgs; _pendingCallTypeArgs = null; break;
                case OpCode.GuardTop: OpGuardTop(); break;
                case OpCode.GuardTopType: OpGuardTopType(); break;
                case OpCode.GuardLocalType: OpGuardLocalType(); break;

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
                    var refIndex = Pop();
                    long idx = (refIndex.Kind == ValueKind.Word ? refIndex.CoerceType(ValueKind.Int) : refIndex).AsInt();
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
                        if (buf.IsWords) { ThrowArgumentError("A 'ref' argument 'x[i]' of a word buffer is not possible (only of an array or a byte buffer)."); break; }   // (the same text natively)
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
                    LeafOwnership.Adopt(createdBuffer, _currentScope.Temp);
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
                    if (ReadsDeadClass(v, typeName, out var deadObj)) { Push(Value.MakeUndefined()); ThrowDestroyedObject(deadObj); break; }
                    Push(Value.MakeBool(IsOfType(v, typeName)));
                    break;
                }

                case OpCode.HasMember:
                {
                    // `x??.name`: is x there (not undefined, not destroyed) and has it the member - a field, property or method of its class, or a built-in one?
                    string memberName = _constants[ReadU16()].AsString();
                    var v = Pop();
                    bool has = v.Kind switch
                    {
                        ValueKind.Undefined => false,
                        ValueKind.Class => v.AsObjectRef() is ObjectInstance { IsDestroyed: false } o && !IsDeadObject(o)
                            && (fire.Runtime.BuiltinMembers.IsOwnership(memberName) || ReflectHas(o, memberName)),
                        ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda => !IsDestroyedLeaf(v) && fire.Runtime.BuiltinMembers.Has(v.Kind, false, memberName),
                        _ => fire.Runtime.BuiltinMembers.Has(v.Kind, v.IsEnum, memberName),
                    };
                    Push(Value.MakeBool(has));
                    break;
                }

                case OpCode.IsFrom:
                {
                    // 0 `is from owner`, 1 `is under owner`, 2 `is from local` (a scope of the running call), 3 `is from global` (the global scope)
                    int fromMode = ReadByte();
                    var ownerVal = Pop();
                    var operandVal = Pop();
                    IOwner? direct = OwnerOfValue(operandVal);   // (null: a value type, a destroyed one - it belongs to nothing)
                    if (fromMode >= 2)
                    {
                        Push(Value.MakeBool(fromMode == 2 ? IsCurrentCallOwner(direct) : direct is Scope { Home.IsGlobal: true }));
                        break;
                    }
                    var ownerObj = RequireObjectInstance(ownerVal, "'is from'/'is under' (owner expression)");
                    if (operandVal.Kind == ValueKind.Class)
                    {
                        var operandObj = RequireObjectInstance(operandVal, "'is from'/'is under'");
                        Push(Value.MakeBool(fromMode == 1 ? operandObj.IsTransitivelyOwnedBy(ownerObj) : operandObj.IsOwnedBy(ownerObj)));
                        break;
                    }
                    // an array, a buffer, a lambda (owned leaves): its owner, or one above it
                    IOwner? up = direct;
                    bool found = up != null && ReferenceEquals(up, ownerObj);
                    for (int guard = 0; fromMode == 1 && !found && up != null && guard < 10000; guard++)
                    {
                        up = up switch { ObjectInstance oi => oi.Owner, IOwnedLeaf l => l.LeafOwner, _ => null };
                        found = ReferenceEquals(up, ownerObj);
                    }
                    Push(Value.MakeBool(found));
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
                    // a mismatch is a catchable ArgumentException (the value stays on the stack, as the handler unwinds it)
                    if (v.Kind != ValueKind.Lambda)
                        ThrowArgumentError($"Expected a lambda with {expectedParamCount} parameter(s), got a value that is no lambda.");
                    else if (((LambdaValue)v.AsLambda()).Proto.ParamCount is var actualCount && actualCount != expectedParamCount)
                        ThrowArgumentError($"The lambda has {actualCount} parameter(s), its declaration says {expectedParamCount}.");
                    break;
                }

                case OpCode.CheckUnit:
                {
                    // SPEC "Unit declarations": the value on top of the stack goes into a declaration with a unit - it is converted into that unit
                    // (the same dimension), another dimension is a catchable UnitMismatchException, a whole number that overflows an OverflowException.
                    // (Converting is part of what the value means, so it happens in every execution mode.)
                    string requiredUnitName = _constants[ReadU16()].AsString();
                    ref var adapted = ref _stack[_sp - 1];
                    switch (Value.AdaptToUnit(ref adapted, requiredUnitName, out var offending))
                    {
                        case Value.UnitAdaptation.Mismatch: ThrowUnitMismatch(Values.Unit.DemandDisplay(requiredUnitName), offending.Unit ?? Values.Unit.Unitless); break;
                        case Value.UnitAdaptation.Overflow: ThrowOverflow($"{adapted} does not fit into a whole number in '{Values.Unit.DemandDisplay(requiredUnitName)}'."); break;
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
                    // docs/spec/THREADING_DESIGN.md section 1).
                    int protoIdx = ReadU16();
                    int globalSlotCount = ReadU16();
                    int takingCount = ReadByte();
                    bool hasWith = (ReadByte() & 1) != 0;   // (bit 1: `fire dedicated` - a VM thread is a system thread anyway)
                    int handoverCount = ReadByte();   // the last ones of the taking values are `handover` (moved, not copied)
                    // Order reversed to compiling (stack!): with
                    // was pushed LAST, so lies on top, is popped FIRST;
                    // afterwards the taking values in REVERSED
                    // list order (last first) - both together
                    // brought into the right order again (see
                    // Compiler.CompileFireStmt).
                    Value? withValue = hasWith ? Pop() : null;
                    var takingValues = new Value[takingCount];
                    for (int i = takingCount - 1; i >= 0; i--) takingValues[i] = Pop();
                    // `[NoCopy]` (SPEC 5.12): every `taking` is a copy - an instance of such a class (also in a field or an element), and a lambda or a pointer
                    // in a field or an element, is the ArgumentException here in the spawning thread, before anything is copied and before the thread starts.
                    // The copies (of everything reachable) are made afterwards, also here, by FireRuntime.FireVmTaking.
                    for (int i = 0; i < takingCount - handoverCount; i++) ObjectCopier.RequireNoCopyFree(takingValues[i], "taking");
                    // `handover` (no copy, a `[NoCopy]` instance is fine): what cannot go to another thread is refused the same way, before anything moves
                    for (int i = takingCount - handoverCount; i < takingCount; i++) ObjectHandover.RequireMovable(takingValues[i], fromFireThread: IsFireThreadVm);

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
                    var fireToken = _pendingFireToken;   // (`fire ... until x`)
                    _pendingFireToken = null;
                    FireRuntime.FireVmTaking(
                        fireProto.Chunk, _natives, _classes, broker, globalSlotCount, takingValues, withValue,
                        executionMode: ExecutionMode, handoverCount: handoverCount, token: fireToken);
                    break;
                }

                case OpCode.SyncGlobals:
                    Push(Value.MakeInt(SyncGlobalsNow()));
                    break;

                case OpCode.SetAutoSync:
                    _globalJobs = (GlobalJobsMode)ReadByte();
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
                    if (_threadBroker != null)   // in the main program: no effect (it is the owner itself)
                    {
                        if (_sectionDepth == 0 && CanSuspend)
                        {
                            // `sync global { }` of a task of the pool: it asks for the section and leaves its loop until the main program grants it
                            var request = _threadBroker.RequestSection();
                            _sectionDepth = 1;
                            _sectionHandle = request;
                            if (!GlobalsBroker.IsGranted(request)) SuspendForSection(request);
                        }
                        else EnterGlobalsSection();
                    }
                    break;

                case OpCode.SectionExit:
                    if (_threadBroker != null && _sectionDepth > 0) ExitGlobalsSection();
                    break;

                case OpCode.PostGlobal:
                {
                    int jobFlags = ReadByte();
                    int jobArgCount = jobFlags & 0x3F;
                    bool onTarget = (jobFlags & 0x80) != 0;   // `fire on x` / `await on x`: the first argument is the object itself
                    bool awaitJob = (jobFlags & 0x40) != 0;   // `await on x`: the result comes back
                    var jobLambda = (LambdaValue)Pop().AsLambda();
                    var jobArgs = new Value[jobArgCount];
                    for (int i = jobArgCount - 1; i >= 0; i--) jobArgs[i] = Pop();
                    if (onTarget && jobArgCount > 0 && IsFireThreadVm && jobArgs[0].Kind == ValueKind.Class
                        && jobArgs[0].AsObjectRef() is ObjectInstance { InGlobalsDomain: false, Mailbox: null })
                    {
                        ThrowArgumentError("'on': the object is not one of the main program (a thread may name a global variable of the main program)");
                        break;
                    }
                    if (awaitJob && !IsFireThreadVm)
                    {
                        // the main program itself: the block runs at once (the copies are made all the same)
                        var nowHolder = new Scope(null);
                        for (int i = onTarget ? 1 : 0; i < jobArgs.Length; i++)
                            if (jobArgs[i].Kind == ValueKind.Class) jobArgs[i] = ObjectCloner.Clone(jobArgs[i], nowHolder, deep: true);
                        var nowError = CallInline(jobLambda, jobArgs, out var nowResult);
                        nowHolder.Release(this);
                        if (nowError != null) { ThrowException(Value.MakeClassRef(nowError)); break; }
                        Push(nowResult);
                        break;
                    }

                    // The arguments belong to the job, not to this thread: objects are deeply copied, their owner is a holder scope that
                    // the main program releases after the run.
                    // `[NoCopy]` (SPEC 5.12): checked for all of them first - nothing is copied or queued when one is refused (the ArgumentException).
                    foreach (var ja in jobArgs)
                        if (ja.Kind == ValueKind.Class) ObjectCopier.RequireNoCopyFree(ja, "fire global");
                    var holder = new Scope(null);
                    for (int i = onTarget ? 1 : 0; i < jobArgs.Length; i++)
                        if (jobArgs[i].Kind == ValueKind.Class) jobArgs[i] = ObjectCloner.Clone(jobArgs[i], holder, deep: true);

                    var postBroker = IsFireThreadVm ? _threadBroker! : EnsureOwnerBroker();
                    if (awaitJob)
                    {
                        var jobReply = new ActorReply();
                        postBroker.PostJob(jobLambda, jobArgs, holder, jobReply);
                        TakeReply(jobReply);
                        break;
                    }
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
                    // a task of the pool with an empty mailbox: it leaves its loop until a message comes (VM.Tasks.cs)
                    if (actorTarget.Mailbox is { HasMessages: false } && CanSuspend) { SuspendForProcess(actorTarget); break; }
                    bool waits = PoolTask != null && actorTarget.Mailbox is { HasMessages: false };
                    if (waits) VmScheduler.BlockEnter();
                    try { ProcessOneMessage(actorTarget, blocking: true); }
                    finally { if (waits) VmScheduler.BlockLeave(); }
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
                    case ValueKind.Word:
                        nativeArgs[i] = unchecked((long)v.AsWord());   // (a machine word: its bits, as C sees an unsigned long - ROADMAP 13.1c)
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
            // (a destroyed object cannot be thrown: what is thrown is the DestroyedException - natively its class is not known any more)
            if (IsDeadObject(excInstance)) { ThrowDestroyedObject(excInstance); return; }
            if (CaptureErrorTrace) RecordThrowTrace();

            if (PauseOnUnhandled && _nestedDepth == 0 && _callbackBoundaries.Count == 0 && !WouldBeCaught(excInstance))
            {
                excInstance.TakeGlobal(_globalScope);
                _pausedChunk = _currentChunk;
                _pausedIp = _ip;
                _unhandledPaused = true;
                UnhandledException = excInstance;
                UnhandledTrace = _lastThrowTrace;
                StopExecution();
                return;
            }

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
                    if (_untilFrames.Count > 0) UntilCaught(_handlers.Count);   // (a catch outside an `await ... until x`: its frame is left)
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
            // to the main thread" (docs/spec/THREADING_DESIGN.md 6.2).
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
            // the constructors among the dropped frames will not go on (no resume()): their objects never came to be - no destruct() for them (SPEC 5.3)
            foreach (var frame in continuation.Frames) if (frame.ConstructedInstance != null) frame.ConstructedInstance.ConstructorFailed = true;

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

        /// <summary>`x is of Dog` / `x!Dog` on a destroyed object: the test needs its class, which a destroyed object does not have any more (natively its memory
        /// is freed) - a DestroyedException. The kind tests (`is of class`, `is of int`, ...) only look at the value and stay answerable.</summary>
        private bool ReadsDeadClass(Value v, string typeName, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ObjectInstance? dead)
        {
            dead = v.Kind == ValueKind.Class && v.AsObjectRef() is ObjectInstance obj && IsDeadObject(obj)
                && typeName is not ("bool" or "int" or "float" or "char" or "word" or "string" or "undefined" or "class") ? obj : null;
            return dead != null;
        }

        /// <summary>`value is of Type` (SPEC 6): for base-type names a simple
        /// kind comparison, for class names recursively over the base-class chain
        /// (InstanceMatchesClassName).</summary>
        private bool IsOfType(Value v, string typeName)
        {
            switch (typeName)
            {
                case "bool": return v.Kind == ValueKind.Bool;
                case "int": return v.Kind == ValueKind.Int && !v.IsEnum;   // (an enum is not an int, like in C#)
                case "float": return v.Kind == ValueKind.Float;
                case "char": return v.Kind == ValueKind.Char;
                case "word": return v.Kind == ValueKind.Word;
                case "string": return v.Kind == ValueKind.String;
                case "undefined": return v.Kind == ValueKind.Undefined;
                case "class": return v.Kind == ValueKind.Class;
                case "lambda": return v.Kind == ValueKind.Lambda;
            }
            if (v.IsEnum) return v.Enum!.Matches(typeName);
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
            if (_accessorAdopted is { Count: > 0 }) ForgetAdopted(targetFrameDepth + 1);
            while (_frames.Count > targetFrameDepth || !ReferenceEquals(_currentScope, targetScope))
            {
                bool atFrameBase = ReferenceEquals(_currentScope.Parent, _globalScope);
                _currentScope.Release(this);

                if (atFrameBase && _frames.Count > targetFrameDepth)
                {
                    var frame = _frames.Pop();
                    // a constructor left by an exception: the object never came to be - its destruct() does not run (what it owns is freed as usual)
                    if (frame.ConstructedInstance != null) frame.ConstructedInstance.ConstructorFailed = true;
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
        public ObjectInstance? CallLambdaInline(LambdaValue lambda, Value[] args) => CallInline(lambda, args, out _);

        /// <summary>CallLambdaInline with the result of the lambda (undefined when it threw or the program ended).</summary>
        private ObjectInstance? CallInline(LambdaValue lambda, Value[] args, out Value result) => CallInlineProto(lambda.Proto, lambda.OnTarget, args, lambda, out result);

        /// <summary>A method of an object, run like <see cref="CallInline"/> (its exception comes back instead of going to the handlers around): the calls
        /// of fire threads on the objects of the main program's globals (docs/ROADMAP_TASKS.md step 8, rule 10).</summary>
        private ObjectInstance? CallMethodInline(ObjectInstance obj, string methodName, Value[] args, out Value result, Scope? argOwner = null)
        {
            var rc = ResolveClass(obj.ClassName);
            var proto = FindOverload(rc, methodName, args, obj).Proto;
            if (proto == null)
            {
                if (TryCallOwnershipMethod(obj, methodName, args, out result)) return null;
                throw MethodNotFound(rc, methodName, args.Length);
            }
            return CallInlineProto(proto, obj, args, null, out result, argOwner);
        }

        private ObjectInstance? CallInlineProto(FunctionProto proto, object? thisObject, Value[] args, LambdaValue? lambda, out Value result, Scope? argOwner = null)
        {
            result = Value.MakeUndefined();
            CheckArity(proto, args.Length);
            args = FillDefaultArgs(proto, args, thisObject);

            int frameDepth = _frames.Count;
            var callerScope = _currentScope;
            int stackPointer = _sp;

            _frames.Push(new CallFrame(_currentChunk, _ip, _currentScope, _currentThis, null));
            int targetDepth = _frames.Count;
            _callbackBoundaries.Push(new CallbackBoundary(frameDepth, callerScope, _handlers.Count, stackPointer));
            _callbackError = null;

            var funcScope = new Scope(_globalScope);
            foreach (var a in args) funcScope.DefineSlot(a);
            if (argOwner != null)
                foreach (var a in args)
                {
                    // (what the job owned - a moved argument - belongs to the call, like an argument it was handed; it dies after the call unless kept)
                    if (!ReferenceEquals(OwnerOfValue(a), argOwner)) continue;
                    if (a.Kind == ValueKind.Class) ((ObjectInstance)a.AsObjectRef()).ReparentToArgument(funcScope);
                    else if (LeafOf(a) is { } argLeaf) LeafOwnership.Reparent(argLeaf, funcScope);
                }
            if (lambda != null) DefineCaptureSlots(lambda, funcScope);
            _pendingCallTypeArgs = lambda?.TypeArgs;
            _pendingExplicitTypeArgs = false;

            _currentThis = thisObject;
            _currentScope = funcScope;
            _currentChunk = proto.Chunk;
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

            if (!_stopExecutionRequested) result = Pop();
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
            DefineCaptureSlots(lambda, funcScope);
            _pendingCallTypeArgs = lambda.TypeArgs;
            _pendingExplicitTypeArgs = false;

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

        private Value? CallMethodNested(ObjectInstance obj, string methodName, Value[] args, bool takeFirst = false, int adoptUnclaimed = 0)
        {
            _pendingArgNames = null;   // (a call the VM makes itself has no named arguments - the names of a call that threw stay unused)
            var rc = ResolveClass(obj.ClassName);
            var (proto, declaringRcNested, accessNested) = FindOverload(rc, methodName, args, obj);
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
            if (takeFirst) TakeArgument(scope.SlotRef(0), scope);   // (SetFieldAccessor: a property assignment in an accessor passes the value on like `take`)
            // `obj[i] = new X()` on an object: an unclaimed value goes to the call of SetIndex, like an argument (SPEC 2.1)
            // (adoptUnclaimed: a mask of the arguments that are slots of the call - an unclaimed value there belongs to the call)
            if (adoptUnclaimed != 0 && _frames.Count > 0)
                for (int i = 0; i < args.Length && i < 31; i++)
                    if ((adoptUnclaimed >> i & 1) != 0) AdoptReturnedArgument(scope.SlotRef(i), _frames.Peek().ReturnScope, scope);

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
            _pendingArgNames = null;   // (a call the VM makes itself has no named arguments - the names of a call that threw stay unused)
            var (proto, declaringRcNested, accessNested) = FindOverload(rc, methodName, args, null);
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
            _pendingArgNames = null;   // (a call the VM makes itself has no named arguments - the names of a call that threw stay unused)
            var ctorProto = FindConstructorFor(rc, args)
                ?? throw new ScriptErrorException("ArgumentException", DescribeConstructorNotFound(rc, args.Length));
            var instance = new ObjectInstance(rc.Name, _currentScope.Temp, rc);   // unclaimed: the statement temporaries (SPEC 2.1)
            if (rc.IsActor) instance.Mailbox = new ActorMailbox { Home = this };
            TakeTypeArgs(instance);
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

        /// <summary>A result for a caller on another thread (`await actor.M()`, `await on`): what the call made (it landed in <paramref name="freshOwner"/>)
        /// goes over (handover), anything else - a field, a global - as a copy; both into <paramref name="holder"/>.</summary>
        private Value ReplyValue(Value result, Scope freshOwner, Scope holder)
        {
            // (an actor - kept in a field of the actor, a registry of actors: its reference goes back, like `fire with`)
            if (result.Kind == ValueKind.Class && result.AsObjectRef() is ObjectInstance { } obj && (obj.Mailbox != null || (obj.RtClass?.IsActor ?? false))) return result;
            if (OwnerOfValue(result) is not { } owner) return result;
            object? home = IsFireThreadVm ? EnsureHomeInbox() : null;   // (a bound lambda in the result: its calls come back here - step 8 (e))
            var value = ReferenceEquals(owner, freshOwner)
                ? ObjectHandover.MoveAll(new[] { result }, holder, senderHome: home)[0]
                : ObjectCloner.Clone(result, holder, deep: true);
            if (value.Kind == ValueKind.Lambda && value.AsLambda() is LambdaValue { Home: LambdaHome.Here, OnTarget: ObjectInstance } bound) ObjectCopier.SetHome(bound, bound, home);
            return value;
        }

        /// <summary>The waiting caller of `await actor.M()` / `await on`: waits for the reply, then throws its exception or pushes its result (it belongs to
        /// this call now, like a returned value).</summary>
        private void TakeReply(ActorReply reply)
        {
            // (a task of the pool leaves the loop until the reply is there - not one that serves calls of other threads: it serves them while it waits)
            if (!reply.Done.IsSet && CancelledClass() == null && _homeInbox == null && TrySuspendReply(reply)) return;
            bool blocked = PoolTask != null && !reply.Done.IsSet;
            if (blocked) VmScheduler.BlockEnter();
            try { WaitReply(reply); }
            finally { if (blocked) VmScheduler.BlockLeave(); }
        }

        private void WaitReply(ActorReply reply)
        {
            while (!reply.Done.Wait(PoolTask == null && VmGroup.Any ? 5 : 20))
            {
                if (PoolTask == null) VmGroup.WaitTurn();   // (the main program waits: a handler that waited may go on, its windows read their events)
                ServeHomeInbox();   // (the calls of other threads for this thread's lambdas: no thread waits for the other forever)
                if (CheckShutdownSignals()) { Push(Value.MakeUndefined()); StopExecution(); return; }
                // (cancelled: the caller stops waiting - what it waited for may still run)
                if (CancelledClass() is { } cancelled) { NativeFail(cancelled, cancelled == "TimeoutException" ? "The time limit ran out." : "The task was cancelled."); _nativeRedirected = false; return; }
            }
            if (reply.Exception != null) { ThrowException(Value.MakeClassRef(reply.Exception)); return; }
            if (reply.Failure != null) { ThrowArgumentError(reply.Failure); return; }
            var result = reply.Result;
            if (ReferenceEquals(OwnerOfValue(result), reply.Holder))
            {
                if (result.Kind == ValueKind.Class) ((ObjectInstance)result.AsObjectRef()).ReparentTo(_currentScope.Temp);
                else if (LeafOf(result) is { } resultLeaf) LeafOwnership.Reparent(resultLeaf, _currentScope.Temp);
            }
            Push(result);
        }

        /// <summary>An array/buffer that a built-in or native function has freshly created (still without owner) belongs to the current scope.</summary>
        private void AdoptFresh(Value v)
        {
            if (LeafOf(v) is { LeafOwner: null, IsDestroyed: false } leaf) LeafOwnership.Adopt(leaf, _currentScope.Temp);
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
            scope = scope.Home;
            if (ReferenceEquals(scope, _currentScope) || OwnsWithinCall(scope)) return true;
            return _frames.Count == 0 && scope.IsGlobal;
        }

        /// <summary>The owner of an object, array or buffer (null for anything else, or a destroyed one).</summary>
        private static IOwner? OwnerOfValue(Value v) => v.Kind switch
        {
            ValueKind.Class when v.AsObjectRef() is ObjectInstance { IsDestroyed: false } o => o.Owner,
            ValueKind.Array when !v.AsArray().IsDestroyed => v.AsArray().LeafOwner,
            ValueKind.Buffer when !v.AsBuffer().IsDestroyed => v.AsBuffer().LeafOwner,
            ValueKind.Lambda when v.AsLambda() is LambdaValue { IsDestroyed: false } l => l.LeafOwner,
            _ => null,
        };

        /// <summary>A slot claims an unclaimed value (SPEC 2.1): the value goes to `target`, and with it what hangs on it and is still in the temporaries
        /// too (the inner copies of a `copy`, the parts of a literal) - it belongs to the object that points to it, otherwise to `target`.</summary>
        private void ClaimTree(Value v, IOwner target)
        {
            var temp = _currentScope.Temp;
            if (!RootedIn(v, temp)) return;
            OwnershipWalk.TakeValue(v, target, this);
            if (temp.HasOwned) OwnershipWalk.MoveReachable(v, Takes.Locals, s => ReferenceEquals(s, temp), target);
        }

        /// <summary>An array literal: its elements are slots of the array - an unclaimed element (`[new A(), Make()]`) belongs to it (SPEC 2.1).</summary>
        private void ClaimElements(ScriptArray arr)
        {
            var temp = _currentScope.Temp;
            if (!temp.HasOwned) return;
            for (int i = 0; i < arr.Items.Length; i++)
                if (arr.Items[i].Kind is ValueKind.Class or ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda) ClaimTree(arr.Items[i], arr);
        }

        /// <summary>Is the value unclaimed - does it belong to the statement temporaries of the current scope (SPEC 2.1)?</summary>
        private bool IsUnclaimed(Value v) => RootedIn(v, _currentScope.Temp);

        /// <summary>Does the value belong to <paramref name="temp"/> - directly or via objects/arrays that belong to it (`Type.Of(c).Find("x")`: the member
        /// hangs on a temporary type)? Such a value is unclaimed too: the first slot takes it out of the temporary (SPEC 2.1).</summary>
        private static bool RootedIn(Value v, Scope temp)
        {
            IOwner? o = OwnerOfValue(v);
            for (int guard = 0; o != null && guard < 10000; guard++)
            {
                if (ReferenceEquals(o, temp)) return true;
                o = o switch { ObjectInstance oi => oi.Owner, IOwnedLeaf leaf => leaf.LeafOwner, _ => null };
            }
            return false;
        }

        /// <summary>An assignment to a variable (SPEC 2.1): an unclaimed value (flags bit 0) goes to the scope of the variable; with bit 1 a value of a
        /// block between the current one and that scope moves up to it (within the call; the global scope only from the top-level code).</summary>
        private void ClaimToScope(Value v, Scope target, int flags)
        {
            if ((flags & 1) != 0 && RootedIn(v, _currentScope.Temp)) { ClaimTree(v, target); return; }
            if (OwnerOfValue(v) is not Scope owner || ReferenceEquals(owner, target)) return;
            bool move = false;
            if ((flags & 2) != 0)
            {
                // a scope between the current one (included) and the target (excluded) - or the temporaries of one of them
                var home = owner.Home;
                for (var sc = _currentScope; sc != null && !ReferenceEquals(sc, target); sc = sc.Parent)
                {
                    if (ReferenceEquals(sc, home)) { move = true; break; }
                    if (sc.Parent == null || sc.Parent.IsGlobal) break;   // (the function scope was the last of the call)
                }
            }
            if (!move) return;
            if (v.Kind == ValueKind.Class) ((ObjectInstance)v.AsObjectRef()).ReparentTo(target);
            else if (LeafOf(v) is { } leaf) LeafOwnership.Reparent(leaf, target);
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
                ValueKind.Lambda => (v.AsLambda() as LambdaValue)?.LeafOwner,
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
            ValueKind.Lambda => v.AsLambda() as LambdaValue,   // (a lambda belongs to its owner like an array - ROADMAP_TASKS step 8)
            _ => null,
        };

        /// <summary>Is the value a destroyed array/buffer whose use is checked (not in performance mode)?</summary>
        private bool IsDestroyedLeaf(Value v) =>
            v.Kind is ValueKind.Array or ValueKind.Buffer or ValueKind.Lambda && ExecutionMode != VmExecutionMode.Performance
            && (v.Kind == ValueKind.Array ? v.AsArray().IsDestroyed : v.Kind == ValueKind.Buffer ? v.AsBuffer().IsDestroyed : v.AsLambda() is LambdaValue { IsDestroyed: true });

        /// <summary>Use of a destroyed array/buffer: a catchable `DestroyedException` (prelude).</summary>
        private void ThrowDestroyed(Value leaf)
        {
            var rc = ResolveClass("DestroyedException");
            string what = leaf.Kind == ValueKind.Buffer ? "buffer" : leaf.Kind == ValueKind.Lambda ? "lambda" : "array";
            // (`fire handover`: a reference from before the handover - the array lives on in the thread, under a new identity)
            bool handedOver = leaf.Kind == ValueKind.Buffer ? leaf.AsBuffer().LivesInAnotherThread : leaf.Kind == ValueKind.Array && leaf.AsArray().LivesInAnotherThread;
            var instance = ConstructNested(rc, new[] { Value.MakeString(handedOver ? $"Access to {(leaf.Kind == ValueKind.Buffer ? "a buffer" : "an array")} that was handed over to a thread ('fire handover')." : $"Access to a destroyed {what}.") });
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
            // a field declared `defined` (SPEC 8.10b): what goes through the pointer is checked like an assignment to the field - its unit, its kind, never undefined
            if (ptr.AsPointer() is FieldPointerTarget { Offset: 0 } field && field.Instance.RtClass is { } rc
                && rc.FindFieldRequiredKind(field.FieldName) is var kind and not 0 && DeclaredKind.IsNonNull(kind))
            {
                if (rc.FindFieldRequiredUnit(field.FieldName) is { } unit && !AdaptFieldUnit(ref value, unit)) return false;
                if (!GuardFieldKind(ref value, kind, field.FieldName)) return false;
            }
            try { ptr.AsPointer().Write(value); return true; }
            catch (PointerRangeException ex) { ThrowPointerRange(ex); return false; }
            catch (ArrayElementException ex) { ThrowArrayElementError(ex.ElemUnit, ex.Offending, ex.Error, ex.Message); return false; }
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
            // (`fire handover`: a reference from before the handover - the object lives on in the thread, under a new identity)
            var instance = ConstructNested(rc, new[] { Value.MakeString(obj.LivesInAnotherThread ? "Access to an object that was handed over to a thread ('fire handover')." : "Access to a destroyed object.") });
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
        /// <summary>A field with a unit takes `value` (converted into its unit, see Value.AdaptToUnit); false when that threw a script exception.</summary>
        private bool AdaptFieldUnit(ref Value value, string requiredUnitName)
        {
            switch (Value.AdaptToUnit(ref value, requiredUnitName, out var offending))
            {
                case Value.UnitAdaptation.Mismatch: ThrowUnitMismatch(Values.Unit.DemandDisplay(requiredUnitName), offending.Unit ?? Values.Unit.Unitless); return false;
                case Value.UnitAdaptation.Overflow: ThrowOverflow($"{value} does not fit into a whole number in '{Values.Unit.DemandDisplay(requiredUnitName)}'."); return false;
                default: return true;
            }
        }

        /// <summary>A whole number that does not fit (a conversion into a finer unit that a declaration demands): a catchable `OverflowException`.</summary>
        private void ThrowOverflow(string message)
        {
            if (!_classes.ContainsKey("OverflowException")) throw new OverflowException(message);
            var instance = ConstructNested(ResolveClass("OverflowException"), new[] { Value.MakeString(message) });
            ThrowException(Value.MakeClassRef(instance));
        }

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
                    if (target.IsEnum)
                    {
                        // a value of an enum: its name, and for a flag enum whether a flag is set
                        if (methodName == "ToString" && args.Length == 0) { result = Value.MakeString(target.ToString()); return true; }
                        if (methodName == "HasFlag" && args.Length == 1) { result = Value.MakeBool(Value.HasFlag(target, args[0])); return true; }
                    }
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
                        result = Value.MakeBuffer(buf.IsWords ? buf.WordBytes(ByteOrder.Little) : buf.Order == ByteOrder.Little ? buf.Clone() : buf.Reversed(ByteOrder.Little));
                        return true;
                    }
                    if (methodName == "ToBigEndian" && args.Length == 0)
                    {
                        result = Value.MakeBuffer(buf.IsWords ? buf.WordBytes(ByteOrder.Big) : buf.Order == ByteOrder.Big ? buf.Clone() : buf.Reversed(ByteOrder.Big));
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
            try { Push(op(a, b)); }
            catch (UnitMismatchException ex) { Push(Value.MakeUndefined()); ThrowUnitConflict(ex); }
            catch (DivideByZeroException) { Push(Value.MakeUndefined()); ThrowDivideByZero(); }
            catch (InvalidOperationException ex) when (ex is not ScriptErrorException) { Push(Value.MakeUndefined()); ThrowArgumentError(ex.Message); }
        }

        /// <summary>Replaces the two topmost values by `f(left, right)`; units that cannot be put together are a script exception (`UnitMismatchException`): catchable, and an unhandled one stops at this instruction with the result `undefined` on the stack.</summary>
        /// <summary>A unary operator on the top of the stack (`-x`, `!x`, `~x`): an operand of the wrong type (`-"a"`, `!5`) is a catchable ArgumentException.</summary>
        private void ReplaceOneGuarded(Func<Value, Value> f)
        {
            try { _stack[_sp - 1] = f(_stack[_sp - 1]); }
            catch (InvalidOperationException ex) when (ex is not ScriptErrorException)
            {
                _stack[_sp - 1] = Value.MakeUndefined();
                ThrowArgumentError(ex.Message);
            }
        }

        private void ReplaceTwoGuarded(Func<Value, Value, Value> f)
        {
            try { ReplaceTwoWith(f(_stack[_sp - 2], _stack[_sp - 1])); }
            catch (UnitMismatchException ex)
            {
                _sp--;
                _stack[_sp - 1] = Value.MakeUndefined();
                ThrowUnitConflict(ex);
            }
            catch (DivideByZeroException)
            {
                _sp--;
                _stack[_sp - 1] = Value.MakeUndefined();
                ThrowDivideByZero();
            }
            catch (InvalidOperationException ex) when (ex is not ScriptErrorException)
            {
                // an operation on values it does not take (`'a' < "b"`, `true + 1`): a catchable ArgumentException instead of an internal error
                _sp--;
                _stack[_sp - 1] = Value.MakeUndefined();
                ThrowArgumentError(ex.Message);
            }
        }

        /// <summary>A value that does not fit what a declaration demands of it (a lambda with another number of parameters): a catchable `ArgumentException`
        /// (without the prelude class an internal error, as before).</summary>
        /// <summary>`if (n)` with a number: a condition must be a bool (a catchable ArgumentException, the type checker reports it already where it knows the type).</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void ThrowConditionError(Value condition) => ThrowArgumentError($"A condition must be a bool, not {condition.Kind}.");

        /// <summary>A field declared int/float/bool takes a value of that kind or undefined (DeclaredKind); false: an ArgumentException is thrown.</summary>
        private bool GuardFieldKind(ref Value value, byte requiredKind, string fieldName) =>
            DeclaredKind.Fits(value, requiredKind) || GuardDeclared(ref value, requiredKind, $"Field '{fieldName}'");

        /// <summary>A field declared as a typed array (FieldInfo.RequiredArray) holds an array of that element or undefined: the array is typed like by OpCode.TypeArray;
        /// false: the ArgumentException is thrown.</summary>
        private bool GuardFieldArray(Value value, ushort requiredArray, string? unit) =>
            requiredArray == 0 || TypeArrayValue(value, (byte)(requiredArray & 0xFF), requiredArray >> 8, unit, 1);

        /// <summary>GuardLocal: the end of the prologue - a parameter declared int/float/bool holds that kind or undefined (DeclaredKind).</summary>
        private void OpGuardLocal()
        {
            int slot = ReadU16();
            byte tag = ReadByte();
            byte required = (byte)((tag & 0x7F) + 1 | tag & DeclaredKind.UnitlessBit);
            int whatIdx = ReadU16();
            ReadU16();   // (the unit it has now: for the native generator)
            ref var guarded = ref _currentScope.SlotRef(slot);
            if (DeclaredKind.Fits(guarded, required)) return;
            var checkedValue = guarded;
            if (GuardDeclared(ref checkedValue, required, _constants[whatIdx].AsString())) guarded = checkedValue;
        }

        /// <summary>GuardLocalType: the end of the prologue - a parameter declared with a class, an interface or a constrained type parameter holds
        /// undefined or what that says (DeclaredType).</summary>
        private void OpGuardLocalType()
        {
            int slot = ReadU16();
            string requiredType = _constants[ReadU16()].AsString();
            int whatIdx = ReadU16();
            ref var guarded = ref _currentScope.SlotRef(slot);
            var owner = _currentThis as ObjectInstance;   // (a `T` of the class: the type argument of the object of the method)
            var callTypeArgs = _currentScope.TypeArgs;    // (a `U` of the method: the type argument of the call - the prologue runs in its scope)
            if (DeclaredType.Fits(guarded, requiredType, owner, callTypeArgs)) return;
            var checkedValue = guarded;
            if (DeclaredType.FitsOrConvert(ref checkedValue, requiredType, owner, callTypeArgs, out var unitDemand))
            {
                if (unitDemand == null || AdaptFieldUnit(ref checkedValue, unitDemand)) guarded = checkedValue;
                return;
            }
            ThrowArgumentError(DeclaredType.Mismatch(guarded, requiredType, _constants[whatIdx].AsString(), owner, callTypeArgs));
        }

        /// <summary>GuardTop: a value going into a local declared int/float/bool (the type checker could not prove it) holds that kind or undefined.</summary>
        private void OpGuardTop()
        {
            byte required = (byte)(ReadByte() + 1);
            int whatIdx = ReadU16();
            ref var top = ref _stack[_sp - 1];
            if (DeclaredKind.Fits(top, required)) return;
            var checkedValue = top;
            if (GuardDeclared(ref checkedValue, required, _constants[whatIdx].AsString())) top = checkedValue;
        }

        /// <summary>GuardTopType: a value going into a local declared with a class, an interface or a type parameter (DeclaredType).</summary>
        private void OpGuardTopType()
        {
            string requiredType = _constants[ReadU16()].AsString();
            int whatIdx = ReadU16();
            ref var top = ref _stack[_sp - 1];
            var owner = _currentThis as ObjectInstance;
            var callTypeArgs = CallScopeTypeArgs();
            if (DeclaredType.Fits(top, requiredType, owner, callTypeArgs)) return;
            var checkedValue = top;
            if (DeclaredType.FitsOrConvert(ref checkedValue, requiredType, owner, callTypeArgs, out var unitDemand))
            {
                if (unitDemand == null || AdaptFieldUnit(ref checkedValue, unitDemand)) top = checkedValue;
                return;
            }
            ThrowArgumentError(DeclaredType.Mismatch(top, requiredType, _constants[whatIdx].AsString(), owner, callTypeArgs));
        }

        /// <summary>The type arguments of the call of the generic method that runs (kept in the scope of the call; blocks inside it are its children).</summary>
        private string[]? CallScopeTypeArgs()
        {
            var s = _currentScope;
            while (s.Parent != null && !s.Parent.IsGlobal) s = s.Parent;
            return s.TypeArgs;
        }

        /// <summary>A field declared with a class, an interface or a constrained type parameter takes `undefined` or what that says (DeclaredType); false: an ArgumentException is thrown.</summary>
        private bool GuardFieldType(ref Value value, string? requiredType, string fieldName, ObjectInstance? owner)
        {
            // (a number type argument with a unit: converted into it, a UnitMismatchException for another dimension)
            if (DeclaredType.FitsOrConvert(ref value, requiredType, owner, null, out var unitDemand)) return unitDemand == null || AdaptFieldUnit(ref value, unitDemand);
            ThrowArgumentError(DeclaredType.Mismatch(value, requiredType!, $"Field '{fieldName}'", owner));
            return false;
        }

        /// <summary>The type arguments for the next object (SetTypeArgs; NewObject/NewObjectOwned take them).</summary>
        private string[]? _pendingTypeArgs;
        private readonly Dictionary<string, string[]> _typeArgTemplates = new();

        /// <summary>SetTypeArgs: the type arguments of the next `new` - `$k@Class` is the k-th type parameter of `Class` of the object of the method.</summary>
        private void OpSetTypeArgs()
        {
            string text = _constants[ReadU16()].AsString();
            byte forCall = ReadByte();
            if (!_typeArgTemplates.TryGetValue(text, out var template)) _typeArgTemplates[text] = template = text.Split('|');
            string[] args;
            if (!text.Contains('$')) args = template;   // (the same array for every object: it is never changed)
            else
            {
                args = new string[template.Length];
                string[]? callTypeArgs = null;
                for (int i = 0; i < template.Length; i++)
                {
                    string t = template[i];
                    if (!t.StartsWith('$')) { args[i] = t; continue; }
                    int at = t.IndexOf('@');
                    int k = int.Parse(t.AsSpan(1, at - 1));
                    if (at == t.Length - 1)
                    {
                        // a type parameter of the generic method that runs: the type arguments of its call (in the scope the call created)
                        callTypeArgs ??= CallScopeTypeArgs() ?? Array.Empty<string>();
                        args[i] = k < callTypeArgs.Length ? callTypeArgs[k] : "";
                    }
                    else args[i] = DeclaredType.TypeArgOf(_currentThis as ObjectInstance, t.Substring(at + 1), k) ?? "";
                }
            }
            if (forCall is 1 or 3)
            {
                _pendingCallTypeArgs = args;
                _pendingExplicitTypeArgs = forCall == 3;   // (3: written at the call, `M<string>(x)`)
            }
            else if (forCall == 2) _pendingLambdaTypeArgs = args;
            else _pendingTypeArgs = args;
        }

        /// <summary>The type arguments for the next call of a generic method (SetTypeArgs with `forCall`; TakeCallTypeArgs, the first instruction of the method, gives them to the scope of the call).</summary>
        private string[]? _pendingCallTypeArgs;

        /// <summary>The call names its type arguments (`M&lt;string&gt;(x)`, SetTypeArgs with `forCall` 3): a generic overload is meant.</summary>
        private bool _pendingExplicitTypeArgs;

        /// <summary>The method a call `name(args)` on `rc` reaches: among overloads by type the one that fits the arguments most closely (Overloads).
        /// `owner`: the object whose type parameters a `T` parameter means. A method that is not generic leaves the type arguments of the call alone.</summary>
        private (FunctionProto? Proto, RuntimeClass? DeclaringClass, AccessModifier Access) FindOverload(RuntimeClass rc, string name, Value[] args, ObjectInstance? owner)
        {
            int explicitCount = _pendingExplicitTypeArgs ? _pendingCallTypeArgs?.Length ?? 0 : 0;
            _pendingExplicitTypeArgs = false;
            // the overload the type checker chose (OverloadHint): taken when the arguments are what it assumed - no scoring
            var hint = _pendingHint;
            _hintTaken = null;
            if (hint != null)
            {
                _pendingHint = null;
                if (hint.Method == name && hint.Holds(args) && hint.Resolve(rc, _classByName) is { Proto: { } chosen } resolved)
                {
                    System.Threading.Interlocked.Increment(ref HintsTaken);
                    _hintTaken = hint;
                    if (chosen.TypeParamCount == 0) _pendingCallTypeArgs = null;
                    return (chosen, resolved.Declaring, chosen.Access ?? AccessModifier.Public);
                }
                System.Threading.Interlocked.Increment(ref HintsMissed);
            }
            var found = explicitCount > 0 || rc.IsTypeOverloaded(name, args.Length)
                ? rc.FindMethodWithAccess(name, args, owner, _pendingCallTypeArgs, explicitCount)
                : rc.FindMethodWithAccess(name, args.Length);
            if (found.Proto is { TypeParamCount: 0 }) _pendingCallTypeArgs = null;
            return found;
        }

        /// <summary>The overload the next call takes when its arguments meet the type checker's assumptions (OpCode.OverloadHint); FindOverload and
        /// FindConstructorFor take it.</summary>
        private OverloadHint? _pendingHint;
        /// <summary>The hint the last FindOverload/FindConstructorFor took (null: it scored): the call site may cache its choice, guarded by the hint.</summary>
        private OverloadHint? _hintTaken;

        /// <summary>A cache hit of a site guarded by a hint (SiteCache.Guard): what the slow route would do with the pending prefixes.</summary>
        private void TakeGuardedHit(FunctionProto proto)
        {
            _pendingHint = null;
            _pendingExplicitTypeArgs = false;
            if (proto.TypeParamCount == 0) _pendingCallTypeArgs = null;
        }
        /// <summary>How often a call took the overload of its hint, and how often its arguments did not meet the assumptions (all VMs; for tests).</summary>
        public static long HintsTaken, HintsMissed;
        private Func<string, RuntimeClass?>? _classByNameCache;
        private Func<string, RuntimeClass?> _classByName => _classByNameCache ??= n => _classes.TryGetValue(n, out var c) ? c : null;

        /// <summary>The constructor `new` runs for these arguments: the one the type checker chose (OverloadHint) when they meet its assumptions,
        /// otherwise the one that fits them most closely.</summary>
        private FunctionProto? FindConstructorFor(RuntimeClass rc, Value[] args)
        {
            var hint = _pendingHint;
            _hintTaken = null;
            if (hint != null)
            {
                _pendingHint = null;
                if (hint.IsConstructor && hint.Holds(args) && hint.Resolve(rc, _classByName) is { Proto: { } chosen })
                {
                    System.Threading.Interlocked.Increment(ref HintsTaken);
                    _hintTaken = hint;
                    return chosen;
                }
                System.Threading.Interlocked.Increment(ref HintsMissed);
            }
            return rc.FindConstructor(args, null);
        }

        /// <summary>The type arguments the next lambda takes along (SetTypeArgs with `forCall` 2).</summary>
        private string[]? _pendingLambdaTypeArgs;

        private string[]? TakeLambdaTypeArgs()
        {
            var args = _pendingLambdaTypeArgs;
            _pendingLambdaTypeArgs = null;
            return args;
        }

        /// <summary>A new object takes the type arguments of its `new` (SetTypeArgs right before it), if there are any.</summary>
        private void TakeTypeArgs(ObjectInstance instance)
        {
            if (_pendingTypeArgs == null) return;
            instance.TypeArgs = _pendingTypeArgs;
            _pendingTypeArgs = null;
        }

        /// <summary>The slow route of a declaration int/float/bool (DeclaredKind): a number declared without a unit is converted into no unit (or a
        /// UnitMismatchException), then the kind is checked (or an ArgumentException). false: an exception is thrown.</summary>
        private bool GuardDeclared(ref Value value, byte required, string what)
        {
            if (DeclaredKind.NeedsUnitless(value, required) && !AdaptFieldUnit(ref value, "unitless")) return false;
            if (DeclaredKind.Accept(ref value, required, what) is not { } message) return true;
            ThrowArgumentError(message);
            return false;
        }

        /// <summary>The type argument a type parameter of the running code stands for (`$k@Class`: of `this`, `$k@`: of the call of the generic method); "" when
        /// it is not known.</summary>
        private string TypeArgOfParam(string text)
        {
            int at = text.IndexOf('@');
            int k = int.Parse(text.AsSpan(1, at - 1));
            if (at == text.Length - 1) { var call = CallScopeTypeArgs(); return call != null && k < call.Length ? call[k] : ""; }
            return DeclaredType.TypeArgOf(_currentThis as ObjectInstance, text.Substring(at + 1), k) ?? "";
        }

        /// <summary>OpCode.TypeArray: the array `value` (at depth `rank` its inner arrays) becomes typed. False when that is the exception: an element that
        /// does not fit, or an array of another element type.</summary>
        private bool TypeArrayValue(Value value, byte elemType, int rank, string? unit, int depth)
        {
            if (value.Kind == ValueKind.Undefined) return true;
            if (value.Kind != ValueKind.Array)
            {
                // a declaration of a typed array holds an array or nothing - like `int x` holds an int (the same text natively)
                ThrowArgumentError($"An array of {ArrayElem.Name(elemType)} is declared here, not {value.Kind}.");
                return false;
            }
            var array = value.AsArray();
            if (array.IsDestroyed || depth > 64) return true;   // (a destroyed array is reported where it is used)
            if (depth < rank)
            {
                foreach (var inner in array.Items)
                    if (!TypeArrayValue(inner, elemType, rank, unit, depth + 1)) return false;
                return true;
            }
            if (array.ElemType == elemType && array.ElemUnit == unit) return true;
            if (array.ElemType != ArrayElem.None)
            {
                ThrowArgumentError($"An array of {ArrayElem.Name(array.ElemType)} cannot be stored where an array of {ArrayElem.Name(elemType)} is declared.");   // (the same text natively)
                return false;
            }
            if (array.MakeTyped(elemType, unit, out var error, out var message, out var offending)) return true;
            ThrowArrayElementError(unit, offending, error, message!);
            return false;
        }

        /// <summary>OpCode.TypeArray of a `byte` declaration (SPEC 8.4.2): the array `value` (at depth `rank` its inner arrays) is replaced by a byte buffer
        /// with its elements - THE conversion of an array into a buffer (the native runtime: bufFromArray). A buffer stays as it is, anything else too. The
        /// buffer gets the owner of the array (an inner array's: the outer one). False when that is the exception (an element that is no int; the array
        /// stays) - `value` is unchanged then.</summary>
        private bool BufferFromArrayValue(ref Value value, ScriptArray? outer, int rank, int depth, bool words = false)
        {
            if (value.Kind != ValueKind.Array) return true;
            var array = value.AsArray();
            if (array.IsDestroyed || depth > 64) return true;   // (a destroyed array is reported where it is used)
            if (depth < rank)
            {
                for (int i = 0; i < array.Items.Length; i++)
                {
                    var inner = array.Items[i];
                    if (!BufferFromArrayValue(ref inner, array, rank, depth + 1, words)) return false;
                    array.Items[i] = inner;
                }
                return true;
            }
            if (words)
            {
                // a buffer of machine words (SPEC 8.10): an int or a word cut to the width, `undefined` 0 (the native runtime: bufFromArray with E_WORD)
                var wordBuffer = ByteBuffer.Words(array.Length, Value.WordBits / 8);
                for (int i = 0; i < array.Length; i++)
                {
                    var e = array.Items[i];
                    if (e.Kind is ValueKind.Int or ValueKind.Word) wordBuffer.SetWord(i, e.ToWord().AsWord());
                    else if (e.Kind != ValueKind.Undefined)
                    {
                        ThrowArgumentError($"An element of a word buffer must be an int or a word, not {e.Kind}.");   // (the same text natively)
                        return false;
                    }
                }
                AdoptConverted(wordBuffer, array, outer);
                value = Value.MakeBuffer(wordBuffer);
                return true;
            }
            var bytes = new byte[array.Length];
            for (int i = 0; i < bytes.Length; i++)
            {
                var e = array.Items[i];
                if (e.Kind == ValueKind.Int) bytes[i] = (byte)e.AsInt();   // (cut to 8 bits, like `buffer[i] = 300`)
                else if (e.Kind != ValueKind.Undefined)
                {
                    ThrowArgumentError($"An element of a byte buffer must be an int, not {e.Kind}.");   // (the same text natively)
                    return false;
                }
            }
            var buffer = new ByteBuffer(bytes.Length, ByteConversions.HostByteOrder);
            System.Array.Copy(bytes, buffer.Bytes, bytes.Length);
            AdoptConverted(buffer, array, outer);
            value = Value.MakeBuffer(buffer);
            return true;
        }

        /// <summary>The buffer made from `array` gets its owner (an inner array's: the outer one; none: the running scope).</summary>
        private void AdoptConverted(ByteBuffer buffer, ScriptArray array, ScriptArray? outer)
        {
            if (array.LeafOwner is { } owner) LeafOwnership.Adopt(buffer, owner);
            else if (outer != null && outer.Parts != null && outer.Parts.Contains(array)) LeafOwnership.AttachPart(outer, buffer);
            else LeafOwnership.Adopt(buffer, _currentScope);
        }

        /// <summary>`array[i] = value` on a typed array (SPEC 8.4.2): the value converted to the element type. A value that does not fit is the exception
        /// (ArgumentException, UnitMismatchException, OverflowException) - then false, nothing is stored and nothing is pushed.</summary>
        private bool ConvertArrayElement(ScriptArray array, ref Value value)
        {
            if (array.ElemType == ArrayElem.None) return true;
            if (array.TryConvertElement(value, out var stored, out var error, out var message)) { value = stored; return true; }
            ThrowArrayElementError(array.ElemUnit, value, error, message!);
            return false;
        }

        private void ThrowArrayElementError(string? elemUnit, Value value, ArrayElem.Error error, string message)
        {
            switch (error)
            {
                case ArrayElem.Error.Unit: ThrowUnitMismatch(elemUnit != null ? Values.Unit.DemandDisplay(elemUnit) : "(no unit)", value.Unit ?? Values.Unit.Unitless); break;
                case ArrayElem.Error.Overflow: ThrowOverflow(message); break;
                default: ThrowArgumentError(message); break;
            }
        }

        private void ThrowArgumentError(string message)
        {
            _arithFault = true;   // `x++`/`x += c` on a local (ArithSlow): the variable stays as it is
            if (!_classes.ContainsKey("ArgumentException")) throw new InvalidOperationException(message);
            var instance = ConstructNested(ResolveClass("ArgumentException"), new[] { Value.MakeString(message) });
            ThrowException(Value.MakeClassRef(instance));
        }

        /// <summary>An integer division or modulo by zero is a catchable script exception (`DivideByZeroException`); floats follow IEEE (infinity, NaN).</summary>
        private void ThrowDivideByZero()
        {
            _arithFault = true;
            var rc = ResolveClass("DivideByZeroException");
            var instance = ConstructNested(rc, new[] { Value.MakeString("Division by zero.") });
            ThrowException(Value.MakeClassRef(instance));
        }

        /// <summary>Set when ThrowUnitConflict raised an exception (read by the statement forms of `x++`/`x += c`, which must not store a result then).</summary>
        private bool _arithFault;

        private void ThrowUnitConflict(UnitMismatchException ex)
        {
            _arithFault = true;
            var rc = ResolveClass("UnitMismatchException");
            var args = new[] { Value.MakeString(ex.Message), Value.MakeString(ex.To.ToString()), Value.MakeString(ex.From.IsUnitless ? "(no unit)" : ex.From.ToString()) };
            var instance = ConstructNested(rc, args);
            ThrowException(Value.MakeClassRef(instance));
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
            throw new ScriptErrorException("ArgumentException",
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
            if (_pendingArgNames is { } names)
            {
                _pendingArgNames = null;
                return FillNamedArgs(proto, suppliedArgs, names, thisForDefaults);
            }
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

        /// <summary>The names of the last arguments of the next call (OpCode.ArgNames), taken by FillDefaultArgs.</summary>
        private string[]? _pendingArgNames;

        /// <summary>A call that does not reach a method of a class (a built-in method, an actor's message): named arguments are an error there.</summary>
        private void RejectArgNames(string what)
        {
            if (_pendingArgNames == null) return;
            _pendingArgNames = null;
            throw new ScriptErrorException("ArgumentException", $"Named arguments are not possible for {what}.");
        }
        private readonly Dictionary<string, string[]> _argNameLists = new();

        /// <summary>A call with named arguments (`f(1, size: 2)`, docs/language/08-classes.md): the positional arguments first, each named one at the
        /// parameter of its name, every other parameter its default value.</summary>
        private Value[] FillNamedArgs(FunctionProto proto, Value[] suppliedArgs, string[] names, object? thisForDefaults)
        {
            var paramNames = proto.ParamNames
                ?? throw new ScriptErrorException("ArgumentException", "Named arguments need a method whose parameters have names.");
            int positional = suppliedArgs.Length - names.Length;
            var result = new Value[proto.ParamCount];
            var given = new bool[proto.ParamCount];
            if (positional > proto.ParamCount)
                throw new ScriptErrorException("ArgumentException", $"Wrong number of arguments: expected {proto.ParamCount}, got {suppliedArgs.Length}.");
            for (int i = 0; i < positional; i++) { result[i] = suppliedArgs[i]; given[i] = true; }
            for (int k = 0; k < names.Length; k++)
            {
                int at = Array.IndexOf(paramNames, names[k]);
                if (at < 0) throw new ScriptErrorException("ArgumentException", $"There is no parameter '{names[k]}'.");
                if (given[at]) throw new ScriptErrorException("ArgumentException", $"The parameter '{names[k]}' is given twice.");
                result[at] = suppliedArgs[positional + k];
                given[at] = true;
            }
            for (int i = 0; i < proto.ParamCount; i++)
            {
                if (given[i]) continue;
                var defaultProto = i < proto.ParamDefaults.Count ? proto.ParamDefaults[i] : null;
                if (defaultProto == null) throw new ScriptErrorException("ArgumentException", $"The parameter '{paramNames[i]}' has no value (and no default value).");
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
        /// <summary>A call that finds no method: an `ArgumentException` when the method exists with other numbers of arguments, otherwise a `MissingMemberException`.</summary>
        private static ScriptErrorException MethodNotFound(RuntimeClass rc, string methodName, int argCount)
        {
            bool exists = false;
            for (var c = rc; c != null && !exists; c = c.Base) exists = c.Methods.ContainsKey(methodName);
            return new ScriptErrorException(exists ? "ArgumentException" : "MissingMemberException", DescribeMethodNotFound(rc, methodName, argCount));
        }

        /// <summary>Turns an error found inside an instruction (see ScriptErrorException) into a script exception that `try`/`catch` can catch; without the
        /// prelude class it stays an internal error.</summary>
        /// <summary>An error of the program that the VM reports as a .NET `InvalidOperationException` (an index on an `int`, `delete 5`, a value where a `ref`
        /// argument needs a variable, ...): it becomes a catchable `ArgumentException` at the instruction that failed instead of ending the host. Only when the
        /// program has the class (the prelude) and the VM is not stopping anyway.</summary>
        private bool IsCatchableRuntimeError(InvalidOperationException error) =>
            error is not ScriptErrorException && !_stopExecutionRequested && _classes.ContainsKey("ArgumentException");

        private void RaiseScriptError(ScriptErrorException error)
        {
            if (error.Instance != null) { ThrowException(Value.MakeClassRef(error.Instance)); return; }
            if (!_classes.ContainsKey(error.ClassName)) throw new InvalidOperationException(error.Message, error);
            var instance = ConstructNested(ResolveClass(error.ClassName), new[] { Value.MakeString(error.Message) });
            ThrowException(Value.MakeClassRef(instance));
        }

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
