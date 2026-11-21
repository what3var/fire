using System;
using System.Collections.Generic;
using System.Threading;
using fire.Bytecode;
using fire.Values;

namespace fire.Runtime
{
    /// <summary>
    /// What the VMs of one program share for stack traces (docs/spec/STACKTRACE.md section 8): the setting (<see cref="StackTraceMode"/>), the display names of
    /// the sources and the names of the functions (<see cref="TraceNames"/>), and the counter of the threads (`Fire #1`, as the debugger names them).
    /// The main VM makes it (from the program it runs); a VM of a thread, a task or a callback takes the one of the VM that started it.
    /// </summary>
    public sealed class TraceContext
    {
        public StackTraceMode Mode { get; }
        private readonly IReadOnlyList<string?>? _files;
        private readonly Chunk _topLevel;
        private readonly IReadOnlyDictionary<string, RuntimeClass> _classes;
        private Dictionary<Chunk, string>? _names;
        private int _threads;

        public TraceContext(StackTraceMode mode, IReadOnlyList<string?>? files, Chunk topLevel, IReadOnlyDictionary<string, RuntimeClass> classes)
        {
            Mode = mode; _files = files; _topLevel = topLevel; _classes = classes;
        }

        /// <summary>Is it the context of the program with these classes?</summary>
        internal bool Of(IReadOnlyDictionary<string, RuntimeClass> classes) => ReferenceEquals(_classes, classes);

        /// <summary>The file of a source as a trace shows it (null: the prelude, or a source the program has no name for).</summary>
        public string? FileOf(int sourceIndex) => _files != null && sourceIndex >= 0 && sourceIndex < _files.Count ? _files[sourceIndex] : null;

        public string NameOf(Chunk chunk)
        {
            var names = _names;
            if (names == null)
            {
                names = TraceNames.Build(_topLevel, _classes.Values);
                Interlocked.CompareExchange(ref _names, names, null);
                names = _names!;
            }
            return names.TryGetValue(chunk, out var name) ? name : TraceNames.Lambda;
        }

        /// <summary>The name of the next thread that a `fire` starts (`Fire #1`, `Fire #2`, ... in the order of the starts).</summary>
        public string NextThreadName() => "Fire #" + Interlocked.Increment(ref _threads);

        /// <summary>The program the last main VM of this process ran (a VM made without a VM to inherit from - a callback of a native thread - takes it).</summary>
        internal static TraceContext? Last;
    }

    public sealed partial class VM
    {
        private TraceContext? _trace;
        /// <summary>A fire thread: the line that ends the traces of its exceptions (`in thread 'Fire #1', started at main.fire:40`); null: not a thread.</summary>
        private string? _threadTrailer;

        /// <summary>The setting of the program for stack traces (docs/spec/STACKTRACE.md section 7) and the display names of its sources (by source index,
        /// LinkedProgram.TraceFiles). Set by whoever makes the main VM; the VMs of threads take it from the VM that starts them.</summary>
        public void ConfigureTrace(StackTraceMode mode, IReadOnlyList<string?>? files)
        {
            _trace = new TraceContext(mode, files, _currentChunk, _classes);
            TraceContext.Last = _trace;
        }

        /// <summary>The setting the traces of this VM follow (an engine shows them by it: off - none, calls - without lines).</summary>
        public StackTraceMode TraceMode => Trace.Mode;

        private TraceContext Trace => _trace ??=
            (CurrentThreadVm is { } running && !ReferenceEquals(running, this) && running._trace is { } parent && parent.Of(_classes) ? parent : null)
            ?? (TraceContext.Last is { } last && last.Of(_classes) ? last : null)
            ?? new TraceContext(ExecutionMode == VmExecutionMode.Performance ? StackTraceMode.Off : StackTraceMode.Lines, null, _currentChunk, _classes);

        /// <summary>What a VM that this one starts (a fire thread) inherits: the context and the line of the start (`started at main.fire:40`, the `fire`
        /// that runs now).</summary>
        internal (TraceContext Context, string Trailer) TraceForThread()
        {
            var ctx = Trace;
            var (src, line) = _currentChunk.GetLocation(Math.Max(0, _ip - 1));
            string? file = ctx.FileOf(src);
            string where = file == null ? "prelude" : ctx.Mode == StackTraceMode.Lines && line > 0 ? file + ":" + line : file;
            return (ctx, $"in thread '{ctx.NextThreadName()}', started at {where}");
        }

        /// <summary>A VM of a thread takes the trace setting of the VM that started it.</summary>
        internal void InheritTrace((TraceContext Context, string Trailer)? from)
        {
            if (from is not { } f) return;
            _trace = f.Context;
            _threadTrailer = f.Trailer;
        }

        /// <summary>The stack as it is now, innermost first (the trace an exception gets at its first throw): the running function at the current
        /// instruction, then each frame below at the call it made. A thread's base function is `&lt;fire&gt;`.</summary>
        private ExceptionTrace CaptureTrace()
        {
            var ctx = Trace;
            int total = _frames.Count + 1;
            var frames = _frames.ToArray();   // (innermost first)
            return ExceptionTrace.Of(total, i =>
            {
                Chunk chunk; int ip;
                if (i == 0) { chunk = _currentChunk; ip = Math.Max(0, _ip - 1); }
                else { chunk = frames[i - 1].ReturnChunk; ip = Math.Max(0, frames[i - 1].ReturnIp - 1); }
                var (src, line) = chunk.GetLocation(ip);
                string name = ctx.NameOf(chunk);
                if (i == total - 1 && IsFireThreadVm && name == TraceNames.Lambda) name = TraceNames.Fire;
                string? file = ctx.FileOf(src);
                return new ExceptionTrace.Frame(name, file, file == null ? 0 : line);
            }, _threadTrailer);
        }

        /// <summary>The first throw of an exception gives it its trace (a rethrow, a `finally` and `resume` keep it).</summary>
        private void StampTrace(ObjectInstance exception)
        {
            if (exception.Trace == null) exception.Trace = CaptureTrace();
        }

        /// <summary>An exception of a task that the waiting one throws again (`await`, `Completion.Wait`, the reply of an actor): its trace gets a section with
        /// the stack of the waiting one (`--- awaited at ---`).</summary>
        private void StampAwaited(ObjectInstance exception)
        {
            if (exception.Trace == null) exception.Trace = CaptureTrace();
            else exception.Trace = exception.Trace.Append(CaptureTrace());
        }

        /// <summary>The trace of an exception as this program shows it (`e.StackTrace`, the report of an unhandled exception): "" without one or with
        /// `#stacktrace off`.</summary>
        public string TraceText(ObjectInstance exception) => exception.Trace?.Format(Trace.Mode) ?? "";

        /// <summary>The report of an exception that nothing caught, as the command line writes it to stderr: the line of UncaughtScriptException, then the
        /// trace (STACKTRACE.md section 2), without a line feed at the end.</summary>
        public string UnhandledReport(ObjectInstance exception) => (new UncaughtScriptException(exception).Message + "\n" + TraceText(exception)).TrimEnd('\n');

        /// <summary>`__exc_trace(e)`, `__exc_frames(e, i, part)` (fire.Standard.ExceptionTraceMethods).</summary>
        internal Value ExcTraceNative(string name, Value[] args)
        {
            var trace = args.Length > 0 && args[0].Kind == ValueKind.Class && args[0].AsObjectRef() is ObjectInstance { Trace: { } t } && Trace.Mode != StackTraceMode.Off ? t : null;
            if (name == fire.Standard.ExceptionTraceMethods.TraceName) return Value.MakeString((trace?.Format(Trace.Mode) ?? "").TrimEnd('\n'));   // (the lines without the last line feed)
            int index = args.Length > 1 && args[1].Kind == ValueKind.Int ? (int)args[1].AsInt() : -1;
            int part = args.Length > 2 && args[2].Kind == ValueKind.Int ? (int)args[2].AsInt() : 0;
            int count = 0;
            for (var s = trace; s != null; s = s.Next)
            {
                if (index >= count && index < count + s.Frames.Count)
                {
                    var f = s.Frames[index - count];
                    return part == 0 ? Value.MakeString(f.Function) : part == 1 ? Value.MakeString(f.File ?? "") : Value.MakeInt(Trace.Mode == StackTraceMode.Lines ? f.Line : 0);
                }
                count += s.Frames.Count;
            }
            return index < 0 ? Value.MakeInt(count) : Value.MakeUndefined();
        }
    }
}
