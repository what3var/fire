using fire.Compiler;
using fire.Debugging;
using fire.Parsing;
using fire.Resolving;
using fire.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace fire.Editor
{
    /// <summary>
    /// Encapsulates one compile+execute run of a script for the
    /// thread-capable step debugger: compiles once (incl. prelude) into an IDebugTarget (fire.Debugging - the VM: VmDebugTarget),
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
    /// console, prefixed with the name of the thread that wrote it (see IDebugTarget.Output),
    /// so that in the output window it stays traceable which
    /// line came from which thread.
    /// </summary>
    public sealed class DebugSession
    {
        private readonly object _threadsLock = new();
        private readonly List<DebugThreadContext> _threads = new();
        private HashSet<(int SourceIndex, int Line)> _breakpointsSnapshot = new();

        public IReadOnlyList<DebugThreadContext> Threads
        {
            get { lock (_threadsLock) return _threads.ToList(); }
        }

        public DebugThreadContext? ActiveThread { get; private set; }

        /// <summary>The program under the debugger (null: nothing compiled, or stopped).</summary>
        public IDebugTarget? Target => _target;

        /// <summary>Whether a program is compiled and can be stepped (the main thread exists).</summary>
        public bool HasProgram => ActiveThread != null;
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
        /// On a parse/resolve error HasProgram stays false, CompileError
        /// contains the message.</summary>
        /// <summary>The errors (when the compile failed) and warnings of the last <see cref="Compile"/>, with their codes; a file is set when the compiler knows it.</summary>
        public IReadOnlyList<Diagnostic> LastDiagnostics { get; private set; } = Array.Empty<Diagnostic>();

        /// <param name="environment">The arguments and the path the program gets (Environment.Args/ProgramPath); none without.</param>
        public bool Compile(string[] sources, string? outname = null, string? basePath = null, fire.Projects.BuildPlan? plan = null, fire.Standard.ProgramEnvironment? environment = null)
        {
            Reset();
            LastDiagnostics = Array.Empty<Diagnostic>();

            // the warnings of the code (`warn 123: "text"`) are collected while the parser reads it
            var warnings = new DiagnosticCollector();
            using var collecting = warnings.Begin();
            try
            {
                var target = VmDebugTarget.Build(sources, ExecutionMode, outname, basePath, DeviceManager, plan, environment);
                LastDiagnostics = warnings.Items.ToList();
                Attach(target);
                return true;
            }
            catch (Exception ex) when (ex is ParseException or LexException or ResolverException or TypeCheckException
                or NotSupportedException or PreprocessorException or LibraryEntryPointException or fire.Projects.ProjectException)
            {
                // All collected errors (the resolver/compiler do not stop at the
                // first, see CompileErrors), not only the first.
                CompileError = CompileErrors.Describe(ex);
                LastDiagnostics = CompileDiagnostics.Of(ex).Concat(warnings.Items).ToList();
                return false;
            }
        }

        /// <summary>Like <see cref="Compile"/>, but the compiler runs on a thread of the pool (the window stays responsive and can show the progress): <paramref name="monitor"/>
        /// gets its phases and the commands of a package's natives that are compiled for it. The session takes the program on the caller's thread. Cancelled: throws
        /// <see cref="OperationCanceledException"/> and keeps no program.</summary>
        public async System.Threading.Tasks.Task<bool> CompileAsync(string[] sources, string? outname, string? basePath, fire.Projects.BuildPlan? plan,
            fire.Standard.ProgramEnvironment? environment, fire.Native.BuildMonitor? monitor)
        {
            Reset();
            LastDiagnostics = Array.Empty<Diagnostic>();
            var mode = ExecutionMode;
            var devices = DeviceManager;
            var (target, diagnostics, error) = await System.Threading.Tasks.Task.Run(() =>
            {
                using var watching = fire.Native.BuildMonitor.Use(monitor);
                var warnings = new DiagnosticCollector();
                using var collecting = warnings.Begin();
                try
                {
                    var built = VmDebugTarget.Build(sources, mode, outname, basePath, devices, plan, environment);
                    if (monitor?.IsCancellationRequested == true)
                    {
                        built.Dispose();
                        throw new OperationCanceledException(monitor.Token);
                    }
                    return ((IDebugTarget?)built, (IReadOnlyList<Diagnostic>)warnings.Items.ToList(), (string?)null);
                }
                catch (Exception ex) when (ex is ParseException or LexException or ResolverException or TypeCheckException
                    or NotSupportedException or PreprocessorException or LibraryEntryPointException or fire.Projects.ProjectException)
                {
                    return (null, CompileDiagnostics.Of(ex).Concat(warnings.Items).ToList(), CompileErrors.Describe(ex));
                }
            });
            LastDiagnostics = diagnostics;
            if (target == null)
            {
                CompileError = error;
                return false;
            }
            Attach(target);
            return true;
        }

        /// <summary>Takes over a debug target that is ready (compiled, its main thread paused before the first line): its output, its main thread and
        /// every thread it starts later.</summary>
        public void Attach(IDebugTarget target)
        {
            ActiveExecutionMode = ExecutionMode;
            _target = target;

            FirstUserSourceIndex = target.FirstUserSourceIndex;
            SourceFiles = target.SourceFiles;

            target.Output += (thread, text) =>
                OutputWritten?.Invoke(thread != null && !thread.IsMain ? $"[{thread.Name}] {text}" : text);
            // From now on THIS session takes over every newly arising
            // thread of the program (see OnThreadStarted).
            target.ThreadStarted += OnThreadStarted;

            {
                var mainCtx = DebugThreadContext.ForMain(target.MainThread);
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
            }
        }

        /// <summary>Runs on the thread that is CURRENTLY executing `fire` (the VM: e.g. on the background thread of a DebugThreadContext, see
        /// IDebugTarget.ThreadStarted) - registers the new thread in the threads panel. The actual new
        /// background thread for the thread arises INSIDE
        /// DebugThreadContext.ForFireThread (see its constructor) -
        /// this method therefore returns IMMEDIATELY, the caller
        /// (Runtime.FireRuntime.Fire) does NOT have to block any more.</summary>
        private void OnThreadStarted(IDebugThread thread)
        {
            var ctx = DebugThreadContext.ForFireThread(thread, _breakpointsSnapshot);
            ctx.Paused += c =>
            {
                CloseHostResourcesIfAllFinished();
                ThreadPaused?.Invoke(c);
            };

            lock (_threadsLock) { _threads.Add(ctx); }
            ThreadAdded?.Invoke(ctx);
        }

        public void SelectThread(DebugThreadContext thread) => ActiveThread = thread;

        /// <summary>The program of the current run - holds the safety net for open IO streams (see
        /// IDebugTarget.ReleaseResources).</summary>
        private IDebugTarget? _target;

        /// <summary>Closes the IO streams the script left open, as soon as all threads of the run have ended
        /// (before that a fire thread might still need them).</summary>
        private void CloseHostResourcesIfAllFinished()
        {
            lock (_threadsLock)
            {
                if (_threads.Count == 0 || _threads.Any(t => !t.IsFinished)) return;
            }
            _target?.ReleaseResources();
        }

        public void Reset()
        {
            // Aborted run: close open streams anyway (the threads are unregistered shortly); the target no longer takes over new threads -
            // otherwise a later, completely independent run in the same process would still accidentally run via THIS (then outdated) session.
            Interlocked.Exchange(ref _target, null)?.Dispose();

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
        /// over, see IDebugThread.StepLine) ON THE ACTIVE THREAD. FIRE-AND-FORGET -
        /// the result arrives via ThreadPaused.</summary>
        public void StepLine() => RunGuarded(_ => t => t.StepLine());

        /// <summary>Like StepLine, but jumps into a call instead of
        /// skipping it (see IDebugThread.StepInto).</summary>
        public void StepInto() => RunGuarded(_ => t => t.StepInto());

        /// <summary>Runs until the current function/method/
        /// lambda is left (see IDebugThread.StepOut).</summary>
        public void StepOut() => RunGuarded(_ => t => t.StepOut());

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

        /// <summary>Runs a thread (not necessarily the active one) on to the next breakpoint or its end - after its error was ignored. FIRE-AND-FORGET like every step.</summary>
        public void ContinueThread(DebugThreadContext ctx, ISet<(int SourceIndex, int Line)> breakpoints) =>
            ctx.RequestStep(DebugThreadContext.MakeContinueStep(breakpoints, ctx.ConsumePauseRequest));

        private void RunGuarded(Func<DebugThreadContext, Func<IDebugThread, bool>> makeStep)
        {
            var ctx = ActiveThread;
            if (ctx == null) return;
            IsStarted = true;
            ctx.RequestStep(makeStep(ctx));
        }
    }
}
