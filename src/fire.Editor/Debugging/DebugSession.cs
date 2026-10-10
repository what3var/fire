using fire.Bytecode;
using fire.Compiler;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using fire.Terminal;
using fire.Terminal.Bridge;
using fire.Terminal.Windows;
using fire.Values;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using static fire.Resolving.ResolvedRef;

namespace fire.Editor
{
    /// <summary>
    /// Encapsulates one compile+execute run of a script for the
    /// thread-capable step debugger: compiles once (incl. prelude),
    /// afterwards holds the main thread PLUS every thread created at runtime via `fire`
    /// as one DebugThreadContext instance each (see
    /// there - EACH of them runs on its OWN background thread, NEVER on
    /// the UI thread, see the class comment there for the reason).
    /// Control (Step/Continue/...) always acts on the currently ACTIVE
    /// thread (see ActiveThread) - which one that is, the UI chooses via the
    /// threads panel. All control methods are FIRE-AND-FORGET (they return
    /// immediately) - the result arrives asynchronously via ThreadPaused.
    ///
    /// `print()` is redirected (OutputWritten event) instead of going to the real
    /// console, prefixed with the name of the thread that is currently EXECUTING
    /// (see VM.CurrentThreadVm - the [ThreadStatic] field
    /// that every VM instance sets to itself at the start of Run()/StepInstruction()),
    /// so that in the output window it stays traceable which
    /// line came from which thread.
    /// </summary>
    public sealed class DebugSession
    {
        private readonly object _threadsLock = new();
        private readonly List<DebugThreadContext> _threads = new();
        private int _fireThreadCounter;
        private HashSet<(int SourceIndex, int Line)> _breakpointsSnapshot = new();

        public IReadOnlyList<DebugThreadContext> Threads
        {
            get { lock (_threadsLock) return _threads.ToList(); }
        }

        public DebugThreadContext? ActiveThread { get; private set; }

        public VM? Vm => ActiveThread?.Vm;
        public bool IsStarted { get; private set; }
        public bool IsFinished => ActiveThread?.IsFinished ?? false;
        public string? CompileError { get; private set; }
        public string? RuntimeError => ActiveThread?.RuntimeError;
        public IReadOnlyList<(int SourceIndex, int Line)>? ErrorTrace => ActiveThread?.ErrorTrace;
        public bool CanIgnoreError => ActiveThread?.CanIgnoreError == true;
        public bool IgnoreError() => ActiveThread?.IgnoreError() == true;

        public VmExecutionMode ExecutionMode { get; set; } = VmExecutionMode.Debug;

        public VmExecutionMode? ActiveExecutionMode { get; private set; }

        /// <summary>Source index (see Bytecode.Chunk.MarkLine/VM.CurrentLocation),
        /// from which the first OWN source of the caller (the `sources` that
        /// went to Compile()) begins in the compiled program - forwarding
        /// of Runtime.RuntimeSession.FirstUserSourceIndex (see there), only valid
        /// after a successful Compile() call (0 before).</summary>
        public int FirstUserSourceIndex { get; private set; }

        /// <summary>The file of each source by source index (see RuntimeSession.SourceFiles); empty before the first successful Compile().</summary>
        public IReadOnlyList<string?> SourceFiles { get; private set; } = Array.Empty<string?>();

        /// <summary>The host's shared DeviceManager that scripts with `#import "devices"` use (null: every script
        /// gets its own). The host sets it once, see EditorDeviceService.</summary>
        public fire.Device.Manager.DeviceManager.DeviceManager? DeviceManager { get; set; }

        public event Action<string>? OutputWritten;

        /// <summary>A new fire thread has arisen - MAY fire on its
        /// own background thread, the subscriber must take care of
        /// Dispatcher.InvokeAsync itself (see DebugThreadContext.Paused
        /// documentation, NOT the blocking Invoke).</summary>
        public event Action<DebugThreadContext>? ThreadAdded;

        /// <summary>Like ThreadAdded, but for EVERY completed step
        /// of any thread (not only when it arises) -
        /// a separate event, because the UI usually only has to react to it
        /// when the CURRENTLY ACTIVE thread is affected (see MainWindow),
        /// whereas ThreadAdded must ALWAYS rebuild the threads list.</summary>
        public event Action<DebugThreadContext>? ThreadPaused;

        public DebugSession()
        {
        }

        private DebugThreadContext? FindContextFor(VM? vm)
        {
            if (vm == null) return null;
            lock (_threadsLock) return _threads.FirstOrDefault(t => ReferenceEquals(t.Vm, vm));
        }

        /// <summary>Current breakpoint lines, as the editor last
        /// reported them - every NEWLY arising fire thread starts its
        /// automatic "continue" run (see DebugThreadContext.
        /// ForFireThread) with a snapshot of THIS set. Breakpoints
        /// set/removed in the editor afterwards therefore only affect an
        /// ALREADY automatically running fire thread
        /// after it is next started again - a deliberate
        /// simplification, to avoid having to synchronise a breakpoint set
        /// that is shared across several threads and modified
        /// concurrently.</summary>
        public void UpdateBreakpoints(IEnumerable<(int SourceIndex, int Line)> locations) => _breakpointsSnapshot = new HashSet<(int, int)>(locations);

        /// <summary>Recompiles the source text and sets up a fresh
        /// main-thread VM (on its own background thread, see
        /// DebugThreadContext.ForMain - starts waiting, nothing runs yet).
        /// On a parse/resolve error Vm stays null, CompileError
        /// contains the message.</summary>
        public bool Compile(string[] sources, string? outname = null, string? basePath = null, fire.Projects.BuildPlan? plan = null)
        {
            Reset();

            try
            {
                var session = RuntimeSession.Build(sources, ExecutionMode, args =>
                {
                    string text = args.Length > 0 ? args[0].ToString() : "";
                    string? threadName = FindContextFor(VM.CurrentThreadVm)?.Name;
                    OutputWritten?.Invoke(threadName != null && threadName != "Main" ? $"[{threadName}] {text}" : text);
                    return Value.MakeUndefined();
                }, outname,
                // IO.Stdio (`#import "io"`) ends up in the same output window as print():
                // output and errors line by line, input is empty (end immediately).
                ioStdio: fire.IO.Bridge.IoStdio.Custom(line =>
                {
                    string? threadName = FindContextFor(VM.CurrentThreadVm)?.Name;
                    OutputWritten?.Invoke(threadName != null && threadName != "Main" ? $"[{threadName}] {line}" : line);
                }),
                basePath: basePath, deviceManager: DeviceManager, plan: plan);

                ActiveExecutionMode = ExecutionMode;
                _session = session;

                FirstUserSourceIndex = session.FirstUserSourceIndex;
                SourceFiles = session.SourceFiles ?? Array.Empty<string?>();

                var mainCtx = DebugThreadContext.ForMain(session.VirtualMachine);
                mainCtx.Paused += ctx =>
                {
                    CloseHostResourcesIfAllFinished();
                    ThreadPaused?.Invoke(ctx);
                };

                lock (_threadsLock)
                {
                    _threads.Clear();
                    _threads.Add(mainCtx);
                }
                ActiveThread = mainCtx;

                // From now on THIS session takes over every newly arising
                // fire thread (see InterceptNewFireThread) - important: unregister it again in
                // Reset(), otherwise a later,
                // completely independent run (or even another
                // script) in the same process would still accidentally run via
                // THIS (then outdated) session.
                FireRuntime.ThreadBodyInterceptor = InterceptNewFireThread;

                return true;
            }
            catch (Exception ex) when (ex is ParseException or ResolverException
                or NotSupportedException or PreprocessorException or LibraryEntryPointException or fire.Projects.ProjectException)
            {
                // All collected errors (the resolver/compiler do not stop at the
                // first, see CompileErrors), not only the first.
                CompileError = CompileErrors.Describe(ex);
                return false;
            }
        }

        /// <summary>Runs on the thread that is CURRENTLY executing `fire` (that is,
        /// e.g. on the background thread of a DebugThreadContext, see
        /// Runtime.FireRuntime.ThreadBodyInterceptor documentation) - registers
        /// the new thread in the threads panel. The actual new
        /// background thread for the fire-thread VM itself arises INSIDE
        /// DebugThreadContext.ForFireThread (see its constructor) -
        /// this method therefore returns IMMEDIATELY, the caller
        /// (Runtime.FireRuntime.Fire) does NOT have to block any more.</summary>
        private void InterceptNewFireThread(VM vm, Action runNormally)
        {
            string name = $"Fire #{Interlocked.Increment(ref _fireThreadCounter)}";
            var ctx = DebugThreadContext.ForFireThread(vm, name, _breakpointsSnapshot);
            ctx.Paused += c =>
            {
                CloseHostResourcesIfAllFinished();
                ThreadPaused?.Invoke(c);
            };

            lock (_threadsLock) { _threads.Add(ctx); }
            ThreadAdded?.Invoke(ctx);
        }

        public void SelectThread(DebugThreadContext thread) => ActiveThread = thread;

        /// <summary>The session of the current run - holds the safety net for open IO streams (see
        /// RuntimeSession.CloseHostResources).</summary>
        private RuntimeSession? _session;

        /// <summary>Closes the IO streams the script left open, as soon as all threads of the run have ended
        /// (before that a fire thread might still need them). Runs at most once per run.</summary>
        private void CloseHostResourcesIfAllFinished()
        {
            lock (_threadsLock)
            {
                if (_threads.Count == 0 || _threads.Any(t => !t.IsFinished)) return;
            }
            Interlocked.Exchange(ref _session, null)?.CloseHostResources();
        }

        public void Reset()
        {
            if (FireRuntime.ThreadBodyInterceptor != null)
                FireRuntime.ThreadBodyInterceptor = null;

            // Aborted run: close open streams anyway (the threads are unregistered shortly).
            Interlocked.Exchange(ref _session, null)?.CloseHostResources();

            lock (_threadsLock)
            {
                // Release every still running/paused thread from its
                // waiting position (see DebugThreadContext.Abandon documentation)
                // - otherwise a thread that is currently waiting would hang forever on
                // its gate, since from here on nobody calls RequestStep for
                // it any more.
                foreach (var t in _threads)
                    t.Abandon();
                _threads.Clear();
            }
            ActiveThread = null;
            IsStarted = false;
            CompileError = null;
        }

        /// <summary>A debugger "step" - one source line further (step
        /// over, see VM.StepLine) ON THE ACTIVE THREAD. FIRE-AND-FORGET -
        /// the result arrives via ThreadPaused.</summary>
        public void StepLine() => RunGuarded(_ => vm => vm.StepLine());

        /// <summary>Like StepLine, but jumps into a call instead of
        /// skipping it (see VM.StepInto).</summary>
        public void StepInto() => RunGuarded(_ => vm => vm.StepInto());

        /// <summary>Runs until the current function/method/
        /// lambda is left (see VM.StepOut).</summary>
        public void StepOut() => RunGuarded(_ => vm => vm.StepOut());

        /// <summary>Runs to the next breakpoint or program end, on
        /// the ACTIVE thread - interruptible via PauseActiveThread.</summary>
        public void Continue(ISet<(int SourceIndex, int Line)> breakpoints) =>
            RunGuarded(ctx => DebugThreadContext.MakeContinueStep(breakpoints, ctx.ConsumePauseRequest));

        /// <summary>Runs without interruption to the program end (no
        /// debugging, just execute) - ON THE ACTIVE thread,
        /// interruptible via PauseActiveThread; other threads continue
        /// independently of it in their own, possibly still automatic
        /// mode.</summary>
        public void RunToCompletion() =>
            RunGuarded(ctx => DebugThreadContext.MakeRunToCompletionStep(ctx.ConsumePauseRequest));

        /// <summary>Asks the active thread to end a currently running
        /// interruptible step (Continue/RunToCompletion - see
        /// DebugThreadContext.RequestPause documentation for the restriction on
        /// Step Line/Into/Out) at the next opportunity.</summary>
        public void PauseActiveThread() => ActiveThread?.RequestPause();

        private void RunGuarded(Func<DebugThreadContext, Func<VM, bool>> makeStep)
        {
            var ctx = ActiveThread;
            if (ctx == null) return;
            IsStarted = true;
            ctx.RequestStep(makeStep(ctx));
        }
    }
}
