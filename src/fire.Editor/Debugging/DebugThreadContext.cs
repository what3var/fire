using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using fire.Debugging;

namespace fire.Editor
{
    /// <summary>
    /// Encapsulates ONE thread of the program under the debugger (an IDebugThread - a VM or a thread of a native debug build) for the thread-capable debugger - both the
    /// main thread and every thread created via `fire` run on
    /// their OWN real background thread (see RunLoop), NEVER on
    /// the UI thread itself - otherwise every longer running step
    /// ("Continue", "Run to end", or even just "Step over" over
    /// a long-running line) would freeze the whole application, since the UI
    /// could no longer process mouse/keyboard/paint events
    /// in the meantime.
    ///
    /// Control from the UI (RequestStep) is therefore deliberately FIRE-AND-
    /// FORGET, NOT blocking: the UI kicks off a step and returns
    /// immediately, the result comes back asynchronously via the Paused event
    /// (which the caller itself has to bring to the UI thread via the dispatcher).
    /// Within ONE thread nothing ever happens at the same time
    /// (the gates ensure that the background thread
    /// executes EXACTLY ONE step after the other).
    ///
    /// Behaviour of a fire thread by default ("nobody has decided in the
    /// threads panel to control it individually"): continues
    /// automatically (like a "Continue" in a normal debugger) until it
    /// either reaches a breakpoint, is finished, crashes, or the
    /// UI pauses it explicitly (RequestPause). The main thread, on the other hand,
    /// always starts in the waiting state (only the first button press
    /// starts any execution at all).
    /// </summary>
    public sealed class DebugThreadContext
    {
        /// <summary>The thread of the debug target this context drives.</summary>
        public IDebugThread Thread { get; }
        public string Name { get; }
        public bool IsMain { get; }
        public bool IsFinished { get; private set; }
        public string? RuntimeError { get; private set; }

        /// <summary>true from a requested step until it is done (Paused fires).</summary>
        public bool IsRunning => _running;
        private volatile bool _running;

        /// <summary>Throw-site locations (innermost first) of the unhandled script exception, if any.</summary>
        public IReadOnlyList<(int SourceIndex, int Line)>? ErrorTrace => _internalErrorTrace ?? Thread.UnhandledTrace;
        private IReadOnlyList<(int SourceIndex, int Line)>? _internalErrorTrace;

        /// <summary>true while this thread stands at the throw site of an unhandled script exception and could ignore it.</summary>
        public bool CanIgnoreError => Thread.IsPausedOnUnhandled;

        /// <summary>Ignores the unhandled exception and makes the thread runnable again right after the `throw` (like `resume()`).</summary>
        public bool IgnoreError()
        {
            if (!Thread.TryResumeUnhandled()) return false;
            RuntimeError = null;
            IsFinished = false;
            return true;
        }

        /// <summary>Raised as soon as this thread has finished a requested
        /// step - fires on THIS thread's OWN
        /// background thread, NEVER on the UI thread; the subscriber must
        /// take care of Dispatcher.InvokeAsync itself (NOT the blocking Invoke -
        /// see the MainWindow constructor for the deadlock reasoning)
        /// .</summary>
        public event Action<DebugThreadContext>? Paused;

        private readonly SemaphoreSlim _resumeGate = new(0);
        private volatile Func<IDebugThread, bool>? _pendingStep;
        private volatile bool _pauseRequested;
        private volatile bool _abandoned;

        private DebugThreadContext(IDebugThread debugThread)
        {
            Thread = debugThread;
            Name = debugThread.Name;
            IsMain = debugThread.IsMain;

            var thread = new System.Threading.Thread(RunLoop)
            {
                Name = $"fire-Debug-{Name}",
                // Foreground thread (.NET default) - a running
                // debug session should neither silently keep the process
                // alive nor abruptly die while
                // something is still running; DebugSession.Reset/Abandon ensures that
                // every thread ends cleanly (instead of waiting forever).
                IsBackground = false,
            };
            thread.Start();
        }

        /// <summary>For the main thread - starts WAITING (no
        /// automatic run, see the class comment).</summary>
        public static DebugThreadContext ForMain(IDebugThread main) => new(main);

        /// <summary>For a thread created via `fire` - starts
        /// IMMEDIATELY in automatic "continue" mode (see the class comment).
        /// Called from IDebugTarget.ThreadStarted
        /// (any thread - the constructor itself starts
        /// a NEW, own background thread for the
        /// actual execution, so it does not run on the calling
        /// thread).</summary>
        public static DebugThreadContext ForFireThread(IDebugThread thread, ISet<(int SourceIndex, int Line)> breakpointsSnapshot)
        {
            var ctx = new DebugThreadContext(thread);
            ctx.RequestStep(MakeContinueStep(breakpointsSnapshot, ctx.ConsumePauseRequest));
            return ctx;
        }

        /// <summary>Consumes a possibly pending pause request (see
        /// RequestPause) - `true` exactly once per RequestPause call,
        /// afterwards `false` again until requested anew. Public,
        /// so that DebugSession can build its own interruptible step functions
        /// (Continue/RunToCompletion) for ANY thread,
        /// not only for the automatic fire-thread run.</summary>
        public bool ConsumePauseRequest()
        {
            if (!_pauseRequested) return false;
            _pauseRequested = false;
            return true;
        }

        /// <summary>Asks a currently running step (typically
        /// "Continue" or "Run to end" - see MakeContinueStep/
        /// MakeRunToCompletionStep, both check this cooperatively) to stop at the
        /// NEXT instruction. `Step Line/Into/Out` themselves do
        /// NOT check it (they use VM.StepLine/StepInto/StepOut directly,
        /// whose internal loop is not accessible to the debugger) - a
        /// single step should almost always finish quickly anyway,
        /// except for an endless loop on a SINGLE
        /// source line (known, accepted restriction).</summary>
        public void RequestPause() => _pauseRequested = true;

        /// <summary>Runs on the OWN background thread of this context
        /// (see constructor) - alternately waits (via _resumeGate) and
        /// executes the respectively requested step until the program has
        /// ended.</summary>
        private void RunLoop()
        {
            while (true)
            {
                _resumeGate.Wait();
                if (_abandoned)
                {
                    // No still pending step is executed any more -
                    // regardless of whether one was due just now or not, this thread
                    // ends now (see Abandon documentation).
                    IsFinished = true;
                    Paused?.Invoke(this);
                    return;
                }

                var step = _pendingStep;
                if (step != null) RunStepNow(step);

                // Abandon() may have been set WHILE RunStepNow was running
                // (the thread was busy, not waiting) - the actual
                // step itself knows nothing about it and might have returned "more work"
                // (e.g. because RequestPause only
                // INTERRUPTED it, did not end it) - force IsFinished here if necessary,
                // otherwise the loop would immediately wait on the
                // gate again without anyone ever opening it again.
                if (_abandoned) IsFinished = true;

                _running = false;
                Paused?.Invoke(this);
                if (IsFinished) return;
            }
        }

        private void RunStepNow(Func<IDebugThread, bool> step)
        {
            try
            {
                bool more = step(Thread);
                IsFinished = !more;

                // An unhandled script exception does NOT throw - it has to be
                // checked explicitly here, otherwise the debugger would simply show the
                // thread as "finished, no error", although
                // the script was actually aborted with an uncaught exception.
                if (Thread.UnhandledError is { } unhandled)
                    RuntimeError = unhandled;
            }
            catch (Exception ex)
            {
                // Deliberately caught broadly (as DebugSession.RunGuarded
                // has always done for the single-thread case) - every internal
                // VM error (e.g. a VmInvariantViolationException bug)
                // should end up here as a clear error message, instead of dragging the thread
                // (and thus potentially the whole application, if
                // unobserved) down with it. An unhandled SCRIPT
                // exception, on the other hand, goes via UnhandledException above, no
                // longer via this catch branch.
                Debug.WriteLine($"{ex.Message}\r\n{ex.StackTrace}");
                RuntimeError = ex.Message;
                try { _internalErrorTrace = Thread.CurrentTrace(); } catch { }
                IsFinished = true;
            }
            _pendingStep = null; // after this run: NOT automatically continue, but wait for the next explicit instruction
        }

        /// <summary>Gives a variable/field/element a new value (IDebugThread.TrySetValue) - on this thread's own background thread between two
        /// steps, FIRE-AND-FORGET like a step: `done` gets the result there, afterwards Paused fires as after a step (the panels show the new value).
        /// false if the thread is not paused (running, finished, or stopped at an unhandled exception).</summary>
        public bool RequestSetValue(DebugValue target, string text, Action<bool, string> done)
        {
            if (IsFinished || _running) return false;
            RequestStep(t =>
            {
                bool ok = t.TrySetValue(target, text, out var error);
                done(ok, error);
                return !t.IsPausedOnUnhandled; // a setter may throw - unhandled, the thread stands at it like after a step
            });
            return true;
        }

        /// <summary>Requests a single step (Step Line/Into/Out,
        /// Continue, RunToCompletion, ... - see DebugSession for the
        /// concrete step functions) - FIRE-AND-FORGET, returns IMMEDIATELY
        /// without waiting for completion (see the class comment
        /// for the reasoning). The result comes via the Paused event.
        /// Has no effect if this thread is already finished.</summary>
        public void RequestStep(Func<IDebugThread, bool> step)
        {
            if (IsFinished) return;
            _pendingStep = step;
            _running = true;
            _resumeGate.Release();
        }

        /// <summary>Releases a thread from its current waiting/running
        /// position, without waiting for its completion (see
        /// DebugSession.Reset) - sets a permanent "abandoned" flag
        /// that RunLoop FORCES to
        /// IsFinished at the NEXT opportunity (regardless of whether the thread
        /// is currently waiting or in the middle of a step) - independent of what a currently running
        /// step itself would return. `RequestPause` additionally, so that
        /// a CURRENTLY running Continue/RunToCompletion (see
        /// MakeContinueStep/MakeRunToCompletionStep) returns as soon as possible
        /// to this check at all, instead of only at the
        /// natural end of the loop. Without the permanent flag (earlier,
        /// faulty version) a thread that was busy EXACTLY DURING
        /// the Abandon call could stop briefly through the pausing,
        /// but then return `more=true` (not finished)
        /// and afterwards wait forever on the gate - exactly the
        /// leak that Abandon is actually supposed to prevent. A thread already in the middle
        /// of a NON-interruptible step (Step Line/Into/Out)
        /// still ends only as soon as THAT step finishes
        /// by itself (known, accepted restriction, see
        /// RequestPause documentation) - afterwards the flag takes effect reliably, though.</summary>
        public void Abandon()
        {
            if (IsFinished) return;
            _abandoned = true;
            RequestPause();
            _resumeGate.Release();
        }

        /// <summary>Like IDebugThread.RunUntilBreakpoint, with cooperatively interruptible
        /// via RequestPause/ConsumePauseRequest - basis both for the
        /// automatic "Continue" run with which every fire thread
        /// starts by default, and for an explicit "Continue"
        /// button press on ANY thread (see DebugSession.
        /// Continue).</summary>
        public static Func<IDebugThread, bool> MakeContinueStep(ISet<(int SourceIndex, int Line)> breakpoints, Func<bool> isPauseRequested) =>
            t => t.RunUntilBreakpoint(breakpoints, isPauseRequested);

        /// <summary>IDebugThread.RunUntilEnd: until the
        /// program end, cooperatively interruptible (see
        /// MakeContinueStep documentation) - basis for "Run to end".</summary>
        public static Func<IDebugThread, bool> MakeRunToCompletionStep(Func<bool> isPauseRequested) =>
            t => t.RunUntilEnd(isPauseRequested);
    }
}
