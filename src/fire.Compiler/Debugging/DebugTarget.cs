using System;
using System.Collections.Generic;

namespace fire.Debugging
{
    /// <summary>
    /// A program under the debugger, independent of the engine that runs it (docs/ROADMAP.md section 8): the virtual machine in this process
    /// (<see cref="VmDebugTarget"/>) or a native debug build that talks to spark over the debug protocol (<see cref="NativeDebugTarget"/>,
    /// docs/NATIVE_DEBUG_PROTOCOL.md). spark's DebugSession, its thread contexts and the debugger panels only see this interface,
    /// <see cref="IDebugThread"/> and <see cref="DebugValue"/> - never VM, Value or ObjectInstance.
    ///
    /// A location is (source index, line): the source index counts the sources of the compiled program (the prelude first, see
    /// <see cref="SourceFiles"/>), the line is 1-based.
    /// </summary>
    public interface IDebugTarget : IDisposable
    {
        /// <summary>"vm" or "native".</summary>
        string Engine { get; }

        /// <summary>The file of each source by source index (null: a source without a file - the prelude, a library from a package).</summary>
        IReadOnlyList<string?> SourceFiles { get; }

        /// <summary>The source index of the first source of the program itself (after the preludes).</summary>
        int FirstUserSourceIndex { get; }

        /// <summary>The main thread. It starts paused before the first line; nothing runs before its first step.</summary>
        IDebugThread MainThread { get; }

        /// <summary>All threads seen so far (main thread first), also the ended ones.</summary>
        IReadOnlyList<IDebugThread> Threads { get; }

        /// <summary>Whether the threads have an operand stack to show (the VM; a native build has none).</summary>
        bool HasOperandStack { get; }

        /// <summary>A new thread of the program (`fire`) has started. It starts running (see <see cref="IDebugThread.IsMain"/>) - the subscriber
        /// usually lets it run on to the next breakpoint. May fire on any thread.</summary>
        event Action<IDebugThread>? ThreadStarted;

        /// <summary>A line of output of the program (print, IO.Stdio). The thread is the one that wrote it, null when not known (a native program writes
        /// to its stdout/stderr). May fire on any thread.</summary>
        event Action<IDebugThread?, string>? Output;

        /// <summary>Called once all threads have ended: releases what the run still holds (the VM closes the IO streams the program left open).</summary>
        void ReleaseResources();

        /// <summary>The program loaded a fire plugin with sources of its own: <see cref="SourceFiles"/> has grown (the new indexes follow the program's). May fire on any
        /// thread, while the thread that loads the plugin waits; the breakpoints given by file (<see cref="SetFileBreakpoints"/>) in the new files apply already.</summary>
        event Action? SourcesAdded;

        /// <summary>The breakpoints by file (full paths) and line: the target applies those in sources that become known while the program runs (a plugin) - the
        /// indexed breakpoints of the control members stay the caller's.</summary>
        void SetFileBreakpoints(IEnumerable<(string File, int Line)> breakpoints);
    }

    /// <summary>
    /// One thread of a program under the debugger. The control members BLOCK until the thread stands still again (a step is done, a breakpoint
    /// is reached, the pause request was honoured, the thread ended or stopped at an unhandled exception): the caller runs them on a thread of its
    /// own (spark: DebugThreadContext), never on the UI thread. All control members return true when the thread is paused with code left to run,
    /// false when it ended - or when it stands at an unhandled exception (<see cref="IsPausedOnUnhandled"/>).
    ///
    /// The state members (location, trace, scopes, ...) are only meaningful while the thread is paused; they may be read from any thread then.
    /// </summary>
    public interface IDebugThread
    {
        IDebugTarget Target { get; }
        string Name { get; }
        bool IsMain { get; }

        /// <summary>One source line further, stepping over calls ("Step Over").</summary>
        bool StepLine();
        /// <summary>One source line further or into a call ("Step Into").</summary>
        bool StepInto();
        /// <summary>Until the current function is left ("Step Out"). At the top level nothing runs: the VM returns false, a native program refuses it and the
        /// thread stays (true) - spark does not offer it there.</summary>
        bool StepOut();
        /// <summary>Until a breakpoint (counted when its line is ENTERED, not for the line the run starts on), a pause request or the end.</summary>
        bool RunUntilBreakpoint(ISet<(int SourceIndex, int Line)> breakpoints, Func<bool> isPauseRequested);
        /// <summary>Until the end or a pause request, ignoring breakpoints.</summary>
        bool RunUntilEnd(Func<bool> isPauseRequested);

        /// <summary>Where the thread stands.</summary>
        (int SourceIndex, int Line) CurrentLocation { get; }
        /// <summary>The call stack as locations, innermost first (the first entry is <see cref="CurrentLocation"/>).</summary>
        IReadOnlyList<(int SourceIndex, int Line)> CurrentTrace();
        /// <summary>The number of active calls (0 at the top level).</summary>
        int CallDepth { get; }

        /// <summary>`this` at the current location, null where none is bound.</summary>
        DebugValue? This { get; }
        /// <summary>The scope chain of the current function, innermost block first, up to the function level (the globals come separately).</summary>
        IReadOnlyList<DebugScope> Scopes();
        /// <summary>The global variables of the program.</summary>
        IReadOnlyList<DebugValue> Globals();
        /// <summary>The operand stack, topmost value LAST; null when the engine has none (see <see cref="IDebugTarget.HasOperandStack"/>).</summary>
        IReadOnlyList<DebugValue>? OperandStack();

        /// <summary>The message of the unhandled exception that stopped the thread (null: none).</summary>
        string? UnhandledError { get; }
        /// <summary>The throw site of that exception as locations, innermost first (null: none or not recorded).</summary>
        IReadOnlyList<(int SourceIndex, int Line)>? UnhandledTrace { get; }
        /// <summary>true while the thread stands at the throw site of an unhandled exception and can go on after it.</summary>
        bool IsPausedOnUnhandled { get; }
        /// <summary>Ignores the unhandled exception: the thread goes on right after the `throw` (like `resume()` in a `catch`). false if it stands at none.</summary>
        bool TryResumeUnhandled();

        /// <summary>Gives a variable, a field or an element (a <see cref="DebugValue"/> with <see cref="DebugValue.CanSet"/>) a new value, written as text:
        /// a number, true/false, a character or a string (with or without quotes) - of the kind the value has now. Like an assignment in the program:
        /// a property runs its setter, probes on the member fire. Only while the thread is paused; runs on the caller's thread. false with a message.</summary>
        bool TrySetValue(DebugValue target, string text, out string error);
    }

    /// <summary>The kind of a <see cref="DebugValue"/>.</summary>
    public enum DebugValueKind { Undefined, Bool, Int, Float, Char, String, Object, Array, Lambda, Pointer, Buffer, Other }

    /// <summary>
    /// A value as the debugger shows it: a name (the variable, field or `[i]`), the text to show and, for objects and arrays, children that are
    /// fetched only when asked for (<see cref="Children"/>). <see cref="Id"/> is the identity of the object or array within the run (0 for a plain
    /// value): two values with the same id are the same object - the panels cut cycles with it.
    /// </summary>
    public sealed class DebugValue
    {
        private readonly Func<IReadOnlyList<DebugValue>>? _children;
        private IReadOnlyList<DebugValue>? _loaded;

        public DebugValue(string name, string display, DebugValueKind kind, string typeName, long id = 0, Func<IReadOnlyList<DebugValue>>? children = null, object? reference = null)
        {
            Name = name;
            Display = display;
            Kind = kind;
            TypeName = typeName;
            Id = id;
            _children = children;
            Reference = reference;
        }

        public string Name { get; }
        /// <summary>The value as text (objects: class name and number, arrays: `Array[n]`).</summary>
        public string Display { get; }
        public DebugValueKind Kind { get; }
        /// <summary>The kind as the engine names it (the VM: its ValueKind; native: the type of the protocol).</summary>
        public string TypeName { get; }
        /// <summary>Identity of the object/array within the run, 0 for a plain value.</summary>
        public long Id { get; }
        /// <summary>Whether <see cref="Children"/> has anything to offer (an object with fields, a non-empty array).</summary>
        public bool HasChildren => _children != null;
        /// <summary>Where the value lives, for <see cref="IDebugThread.TrySetValue"/> - opaque, only the engine that made it reads it. null: cannot be set.</summary>
        public object? Reference { get; }
        public bool CanSet => Reference != null;

        /// <summary>The fields of an object or the elements of an array, fetched on the first call (the native engine asks the program), then kept.</summary>
        public IReadOnlyList<DebugValue> Children() => _loaded ??= _children?.Invoke() ?? Array.Empty<DebugValue>();

        public override string ToString() => $"{Name} = {Display}";
    }

    /// <summary>One level of the scope chain: depth 0 is the innermost (active) block; the level of the function itself holds the parameters.</summary>
    public sealed record DebugScope(int Depth, bool IsFunctionTopLevel, IReadOnlyList<DebugValue> Variables);
}
