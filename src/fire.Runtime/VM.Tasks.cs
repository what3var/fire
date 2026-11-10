using System;
using System.Diagnostics;
using System.Threading;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>Step 7 (docs/ROADMAP_TASKS.md): a fire thread of the VM as a task of the pool (VmScheduler). It runs in slices: <see cref="RunSlice"/> runs
    /// until the thread ends or waits; a wait on its own level - not inside a callback, a getter or a section - leaves the loop of the VM: the running chunk
    /// is swapped for one that only halts, and the next slice puts it back and goes on where it was (after <see cref="_resumeAction"/>).</summary>
    public sealed partial class VM
    {
        /// <summary>The task of the pool this VM runs as (null: a system thread of its own, or the main program).</summary>
        internal VmScheduler.VmTask? PoolTask;

        /// <summary>A member of the main program's group (VmGroup): the main program's VM it belongs to.</summary>
        private VM? _groupHome;
        internal bool IsGroupMember => _groupHome != null;

        internal static void SetCurrentThreadVm(VM? vm) => _currentThreadVm = vm;

        /// <summary>A member of this main program's group for a callback: a VM of its own on the same global scope (the main program's role - its globals,
        /// its broker), whose program is the call of the lambda; at its end the global scope stays (it is the main program's).</summary>
        internal VM MakeGroupMember(LambdaValue lambda, Value[] args)
        {
            var vm = new VM(StopChunk, _globalScope, _natives, _classes, _externs, _externSignatures, isMainThreadVm: false, isFireThreadVm: false, executionMode: ExecutionMode)
            {
                DestroyGlobalsAtEnd = false,
            };
            vm._groupHome = this;
            vm._ownerBroker = _ownerBroker;
            vm._globalJobs = _globalJobs;
            CheckArity(lambda.Proto, args.Length);
            args = vm.FillDefaultArgs(lambda.Proto, args, lambda.OnTarget);
            // (the lambda is the program: when it returns, the VM halts - the return goes to a chunk that only halts)
            vm._frames.Push(new CallFrame(StopChunk, 0, _globalScope, null, null));
            var funcScope = new Scope(_globalScope);
            foreach (var a in args) funcScope.DefineSlot(a);
            if (lambda.Captures != null) foreach (var c in lambda.Captures) funcScope.DefineSlot(c);
            vm._pendingCallTypeArgs = lambda.TypeArgs;
            vm._currentThis = lambda.OnTarget;
            vm._currentScope = funcScope;
            vm._currentChunk = lambda.Proto.Chunk;
            vm._sliceStarted = true;   // (RunLoop would start at the entry chunk: it goes on at the lambda's first instruction)
            vm._resumeChunk = lambda.Proto.Chunk;
            vm._resumeIp = 0;
            return vm;
        }

        private static readonly Chunk SuspendChunk = BuildStopChunk();
        private bool _sliceStarted;
        private Chunk? _resumeChunk;
        private int _resumeIp;
        private Action? _resumeAction;

        /// <summary>Can the running wait leave the loop (a task of the pool on its own level)?</summary>
        internal bool CanSuspend => PoolTask != null && _nestedDepth == 0 && _sectionDepth == 0 && _callbackBoundaries.Count == 0;

        /// <summary>Leaves the loop after the running instruction: the task waits until <paramref name="waitFor"/> holds or <paramref name="until"/>
        /// (Stopwatch timestamp, 0: none) passed - or a signal (terminate, leave) came -, then <paramref name="resume"/> runs where the VM goes on. A signal
        /// may wake it early: <paramref name="resume"/> looks at its condition again (and suspends again).</summary>
        internal void Suspend(Func<bool>? waitFor, long until, Action? resume)
        {
            int epoch = Volatile.Read(ref s_signalEpoch);
            PoolTask!.WaitFor = () => Volatile.Read(ref s_signalEpoch) != epoch || (waitFor?.Invoke() ?? false);
            PoolTask.WaitUntil = until;
            _resumeChunk = _currentChunk;
            _resumeIp = _ip;
            _resumeAction = resume;
            _currentChunk = SuspendChunk;
            _ip = 0;
        }

        /// <summary>One slice of the task: true when it waits (suspended), false when it has ended.</summary>
        internal bool RunSlice()
        {
            _currentThreadVm = this;
            _acceptingCallbacks = true;
            bool suspended = false;
            try
            {
                if (_groupHome != null) _ownerBroker = _groupHome._ownerBroker;   // (a member of the group: the main program's broker, made in the meantime)
                if (!_sliceStarted) { _sliceStarted = true; RunLoop(); }
                else
                {
                    _currentChunk = _resumeChunk!;
                    _ip = _resumeIp;
                    _resumeChunk = null;
                    var resume = _resumeAction;
                    _resumeAction = null;
                    if (CheckShutdownSignals()) StopExecution();
                    else resume?.Invoke();
                    ContinueLoop();
                }
                suspended = ReferenceEquals(_currentChunk, SuspendChunk);
                return suspended;
            }
            finally
            {
                _currentThreadVm = null;
                if (!suspended) _acceptingCallbacks = false;
            }
        }

        /// <summary>`Sleep` of a task of the pool: it leaves the loop until its time (or its token's time limit, or a cancel); the next slice throws the
        /// CancelledException when the token was cancelled. false: it cannot (it sleeps on the worker's thread).</summary>
        internal bool TrySuspendSleep(long ticks)
        {
            if (!CanSuspend) return false;
            long until = Stopwatch.GetTimestamp() + (long)(Math.Max(0, ticks) * (Stopwatch.Frequency / 10_000_000.0));
            var token = CurrentToken;
            if (token != null && token.Deadline is var limit and not 0 && limit < until) until = limit;
            SuspendSleep(token, until);
            return true;
        }

        private void SuspendSleep(CancelNode? token, long until)
        {
            Suspend(token != null ? () => token.State != 0 : null, until, () =>
            {
                if (CancelledClass() is { } cancelled)
                {
                    NativeFail(cancelled, cancelled == "TimeoutException" ? "The time limit ran out." : "The task was cancelled.");
                    _nativeRedirected = false;
                }
                else if (Stopwatch.GetTimestamp() < until) SuspendSleep(token, until);   // (woken early by a signal: the rest of the time)
            });
        }

        /// <summary>`process a` of a task of the pool while a's mailbox is empty: it waits for a message by leaving its loop, then runs it.</summary>
        private void SuspendForProcess(ObjectInstance actor)
        {
            Suspend(() => actor.Mailbox?.HasMessages ?? true, 0, () =>
            {
                if (actor.Mailbox is { HasMessages: false }) SuspendForProcess(actor);
                else ProcessOneMessage(actor, blocking: true);
            });
        }

        /// <summary>`sync global { }` of a task of the pool: it waits for the section the main program grants by leaving its loop.</summary>
        internal void SuspendForSection(object? request)
        {
            Suspend(() => GlobalsBroker.IsGranted(request), 0, () => { if (!GlobalsBroker.IsGranted(request)) SuspendForSection(request); });
        }

        /// <summary>The wait of `await actor.M()` / `await on` of a task of the pool: it leaves the loop until the reply is there (or its token is
        /// cancelled). false: it cannot.</summary>
        private bool TrySuspendReply(ActorReply reply)
        {
            if (!CanSuspend) return false;
            var token = CurrentToken;
            Suspend(() => reply.Done.IsSet || (token != null && token.State != 0), token?.Deadline ?? 0, () => TakeReply(reply));   // (woken early: it waits again)
            return true;
        }

        private void ContinueLoop()
        {
            while (true)
            {
                try { RunLoopCore(); return; }
                catch (ScriptErrorException error) { RaiseScriptError(error); }
                catch (InvalidOperationException error) when (IsCatchableRuntimeError(error)) { RaiseScriptError(new ScriptErrorException("ArgumentException", error.Message)); }
            }
        }
    }
}
