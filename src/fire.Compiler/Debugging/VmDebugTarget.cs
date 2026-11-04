using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using fire.Bytecode;
using fire.Compiler;
using fire.Runtime;
using fire.Values;

namespace fire.Debugging
{
    /// <summary>
    /// The virtual machine in this process as an <see cref="IDebugTarget"/>: compiles the program (<see cref="Build"/>), the main thread is its
    /// VM, every thread the program starts with `fire` becomes a <see cref="VmDebugThread"/> of its own (through FireRuntime.ThreadBodyInterceptor -
    /// so only ONE VmDebugTarget can be live in a process at a time). The members forward to the debugging API of the VM unchanged.
    /// </summary>
    public sealed class VmDebugTarget : IDebugTarget
    {
        private readonly object _lock = new();
        private readonly List<VmDebugThread> _threads = new();
        private readonly Dictionary<VM, VmDebugThread> _byVm = new(ReferenceEqualityComparer.Instance);
        private readonly Action<VM, Action> _interceptor;
        private RuntimeSession? _session;
        private int _fireThreadCounter;

        // identities of objects and arrays for DebugValue.Id (weak: the run keeps them alive, not the debugger)
        private readonly ConditionalWeakTable<object, StrongBox<long>> _ids = new();
        private long _nextId;

        public string Engine => "vm";
        public IReadOnlyList<string?> SourceFiles { get; private set; } = Array.Empty<string?>();
        public int FirstUserSourceIndex { get; private set; }
        public IDebugThread MainThread => Main;
        public VmDebugThread Main { get; private set; } = null!;
        public bool HasOperandStack => true;

        /// <summary>The session of the compiled program (null after <see cref="Dispose"/>).</summary>
        public RuntimeSession? Session => _session;

        public IReadOnlyList<IDebugThread> Threads
        {
            get { lock (_lock) return _threads.ToList(); }
        }

        public event Action<IDebugThread>? ThreadStarted;
        public event Action<IDebugThread?, string>? Output;
        public event Action? SourcesAdded;

        // ---- fire plugins: their sources follow the program's (VM.PluginSourcesLinked); the breakpoints by file in them apply as soon as they are known ----
        private readonly Action<object, int, IReadOnlyList<string?>, IReadOnlyList<string?>> _onPluginSources;
        private List<(string File, int Line)> _fileBreakpoints = new();
        private int _programSources;
        /// <summary>The breakpoints of <see cref="_fileBreakpoints"/> in the plugins' sources (by index).</summary>
        private HashSet<(int, int)> _pluginBreakpoints = new();
        /// <summary>The breakpoints the run of this thread looks at (RunUntilBreakpoint): a plugin loaded by it adds those in its files at once.</summary>
        [ThreadStatic] private static HashSet<(int SourceIndex, int Line)>? t_running;

        private VmDebugTarget()
        {
            _interceptor = InterceptNewFireThread;
            _onPluginSources = OnPluginSources;
        }

        private static bool SameFile(string? a, string? b)
        {
            if (a == null || b == null) return false;
            try { return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException) { return false; }
        }

        /// <summary>The breakpoints by file in the sources after the program's (those of plugins).</summary>
        private HashSet<(int, int)> ResolvePluginBreakpoints()
        {
            var result = new HashSet<(int, int)>();
            var files = SourceFiles;
            for (int i = _programSources; i < files.Count; i++)
                foreach (var (file, line) in _fileBreakpoints)
                    if (SameFile(files[i], file)) result.Add((i, line));
            return result;
        }

        public void SetFileBreakpoints(IEnumerable<(string File, int Line)> breakpoints)
        {
            lock (_lock)
            {
                _fileBreakpoints = breakpoints.ToList();
                _pluginBreakpoints = ResolvePluginBreakpoints();
            }
        }

        private void OnPluginSources(object classes, int first, IReadOnlyList<string?> files, IReadOnlyList<string?> hashes)
        {
            if (_session == null || !ReferenceEquals(classes, _session.CompiledProgram.Classes)) return;
            lock (_lock)
            {
                var all = SourceFiles.ToList();
                while (all.Count < first) all.Add(null);
                for (int k = 0; k < files.Count; k++) { if (first + k < all.Count) all[first + k] = files[k]; else all.Add(files[k]); }
                SourceFiles = all;
                _pluginBreakpoints = ResolvePluginBreakpoints();
            }
            // (the thread that loads the plugin runs on with the breakpoints in its files)
            if (t_running != null) t_running.UnionWith(_pluginBreakpoints);
            SourcesAdded?.Invoke();
        }

        /// <summary>A run until a breakpoint: the caller's breakpoints and those by file in the plugins' sources (also of a plugin this run loads).</summary>
        internal bool RunWithPluginBreakpoints(ISet<(int SourceIndex, int Line)> breakpoints, Func<ISet<(int SourceIndex, int Line)>, bool> run)
        {
            HashSet<(int, int)> plugin;
            lock (_lock) plugin = _pluginBreakpoints;
            var set = new HashSet<(int SourceIndex, int Line)>(breakpoints);
            set.UnionWith(plugin);
            var outer = t_running;
            t_running = set;
            try { return run(set); }
            finally { t_running = outer; }
        }

        /// <summary>Compiles the program (with the prelude) and makes its main VM; nothing runs yet. Throws the exceptions of the compiler
        /// (ParseException, ResolverException, ...) like RuntimeSession.Build. print() and IO.Stdio go to <see cref="Output"/>.</summary>
        public static VmDebugTarget Build(IReadOnlyList<string> sources, VmExecutionMode mode, string? outname = null, string? basePath = null,
            fire.Device.Manager.DeviceManager.DeviceManager? deviceManager = null, fire.Projects.BuildPlan? plan = null, fire.Standard.ProgramEnvironment? environment = null)
        {
            var target = new VmDebugTarget();
            var session = RuntimeSession.Build(sources, mode, args =>
            {
                target.Write(args.Length > 0 ? args[0].ToString() : "");
                return Value.MakeUndefined();
            }, outname,
            // IO.Stdio (`#import "io"`) ends up in the same output as print(): line by line, input is empty (end immediately)
            ioStdio: fire.IO.Bridge.IoStdio.Custom(target.Write),
            basePath: basePath, deviceManager: deviceManager, plan: plan, environment: environment);
            target.Attach(session);
            return target;
        }

        private void Attach(RuntimeSession session)
        {
            _session = session;
            FirstUserSourceIndex = session.FirstUserSourceIndex;
            SourceFiles = session.SourceFiles ?? Array.Empty<string?>();
            _programSources = SourceFiles.Count;
            VM.PluginSourcesLinked += _onPluginSources;
            Main = Register(session.VirtualMachine!, "Main", isMain: true);
            // From now on this target takes over every new fire thread - unregistered again in Dispose, otherwise a later run in the same process
            // would still go through this one.
            FireRuntime.ThreadBodyInterceptor = _interceptor;
        }

        private VmDebugThread Register(VM vm, string name, bool isMain)
        {
            var thread = new VmDebugThread(this, vm, name, isMain);
            lock (_lock)
            {
                _threads.Add(thread);
                _byVm[vm] = thread;
            }
            return thread;
        }

        private void Write(string text)
        {
            var vm = VM.CurrentThreadVm;
            VmDebugThread? thread = null;
            if (vm != null) lock (_lock) _byVm.TryGetValue(vm, out thread);
            Output?.Invoke(thread, text);
        }

        /// <summary>Runs on the thread that executes `fire`: the new thread's VM is registered and announced; its body is NOT run here - the
        /// subscriber of <see cref="ThreadStarted"/> drives it (spark: on a thread of its own), so the caller does not block.</summary>
        private void InterceptNewFireThread(VM vm, Action runNormally)
        {
            var thread = Register(vm, $"Fire #{Interlocked.Increment(ref _fireThreadCounter)}", isMain: false);
            ThreadStarted?.Invoke(thread);
        }

        public void ReleaseResources() => Interlocked.Exchange(ref _session, null)?.CloseHostResources();

        public void Dispose()
        {
            if (ReferenceEquals(FireRuntime.ThreadBodyInterceptor, _interceptor)) FireRuntime.ThreadBodyInterceptor = null;
            VM.PluginSourcesLinked -= _onPluginSources;
            ReleaseResources();
        }

        internal long IdOf(object o) => _ids.GetValue(o, _ => new StrongBox<long>(Interlocked.Increment(ref _nextId))).Value;

        // ---------------------------------------------------------------
        // Values
        // ---------------------------------------------------------------

        internal sealed record SlotRef(Scope Scope, int Slot);
        internal sealed record FieldRef(ObjectInstance Object, string Field);
        internal sealed record ElementRef(ScriptArray Array, int Index);

        internal static DebugValueKind KindOf(ValueKind kind) => kind switch
        {
            ValueKind.Bool => DebugValueKind.Bool,
            ValueKind.Int => DebugValueKind.Int,
            ValueKind.Float => DebugValueKind.Float,
            ValueKind.Char => DebugValueKind.Char,
            ValueKind.String => DebugValueKind.String,
            ValueKind.Class => DebugValueKind.Object,
            ValueKind.Lambda => DebugValueKind.Lambda,
            ValueKind.Pointer => DebugValueKind.Pointer,
            ValueKind.Array => DebugValueKind.Array,
            ValueKind.Buffer => DebugValueKind.Buffer,
            ValueKind.Undefined => DebugValueKind.Undefined,
            _ => DebugValueKind.Other,
        };

        /// <summary>A value of the VM for the panels: objects as "Class (#id)" with their fields as children, arrays as "Array[n]" with their elements
        /// (both fetched only when asked for), everything else as Value.ToString().</summary>
        internal DebugValue Describe(string name, Value value, object? reference)
        {
            switch (value.Kind)
            {
                case ValueKind.Class when value.AsObjectRef() is ObjectInstance obj:
                    return new DebugValue(name, $"{obj.ClassName} (#{obj.Id})", DebugValueKind.Object, value.Kind.ToString(), IdOf(obj),
                        obj.Fields.Any() ? () => obj.Fields.Select(f => Describe(f.Key, f.Value, new FieldRef(obj, f.Key))).ToList() : null, reference);
                case ValueKind.Array:
                    var arr = value.AsArray();
                    return new DebugValue(name, $"Array[{arr.Length}]", DebugValueKind.Array, value.Kind.ToString(), IdOf(arr),
                        arr.Length > 0 ? () => Enumerable.Range(0, arr.Length).Select(i => Describe($"[{i}]", arr.Items[i], new ElementRef(arr, i))).ToList() : null, reference);
                default:
                    return new DebugValue(name, value.ToString(), KindOf(value.Kind), value.Kind.ToString(), 0, null, reference);
            }
        }

        /// <summary>The new value for `set`, written as text, of the kind the old value has (an int stays an int of the same width and unit, ...).</summary>
        internal static bool TryParseLike(Value old, string text, out Value result, out string error)
        {
            result = default;
            error = "";
            string t = text.Trim();
            switch (old.Kind)
            {
                case ValueKind.Int:
                    if (!long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)
                        && !(t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(t[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out l)))
                    { error = $"'{text}' is not a whole number."; return false; }
                    result = old.IsEnum ? Value.MakeEnum(l, old.Enum!) : Value.MakeInt(l, old.Unit, old.Width);
                    return true;
                case ValueKind.Float:
                    if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) { error = $"'{text}' is not a number."; return false; }
                    result = Value.MakeFloat(d, old.Unit, old.Width);
                    return true;
                case ValueKind.Bool:
                    if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) result = Value.MakeBool(true);
                    else if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) result = Value.MakeBool(false);
                    else { error = $"'{text}' is neither true nor false."; return false; }
                    return true;
                case ValueKind.Char:
                    if (t.Length >= 3 && t[0] == '\'' && t[^1] == '\'') t = t[1..^1];
                    if (t.Length != 1) { error = $"'{text}' is not a single character."; return false; }
                    result = Value.MakeChar(t[0]);
                    return true;
                case ValueKind.String:
                    // a string keeps blanks at its ends; quotes around it are optional
                    string s = text.Length >= 2 && text[0] == '"' && text[^1] == '"' ? text[1..^1] : text;
                    result = Value.MakeString(s);
                    return true;
                default:
                    error = $"A value of the kind {old.Kind} cannot be set - only numbers, bools, characters and strings.";
                    return false;
            }
        }
    }

    /// <summary>One VM (the main program or a `fire` thread) as an <see cref="IDebugThread"/>.</summary>
    public sealed class VmDebugThread : IDebugThread
    {
        private readonly VmDebugTarget _target;

        internal VmDebugThread(VmDebugTarget target, VM vm, string name, bool isMain)
        {
            _target = target;
            Vm = vm;
            Name = name;
            IsMain = isMain;
            vm.CaptureErrorTrace = true;
            vm.PauseOnUnhandled = true; // stack trace + line marking for unhandled script errors, ignoring them is possible
        }

        public VM Vm { get; }
        public IDebugTarget Target => _target;
        public string Name { get; }
        public bool IsMain { get; }

        public bool StepLine() => Vm.StepLine();
        public bool StepInto() => Vm.StepInto();
        public bool StepOut() => Vm.StepOut();
        public bool RunUntilBreakpoint(ISet<(int SourceIndex, int Line)> breakpoints, Func<bool> isPauseRequested) => _target.RunWithPluginBreakpoints(breakpoints, set => Vm.RunUntilBreakpoint(set, isPauseRequested));
        public bool RunUntilEnd(Func<bool> isPauseRequested) => Vm.RunUntilEnd(isPauseRequested);

        // at an unhandled exception the VM keeps the throw site aside (its own position is not meaningful then): that is where the thread stands
        public (int SourceIndex, int Line) CurrentLocation =>
            Vm.IsPausedOnUnhandled && Vm.UnhandledTrace is { Count: > 0 } trace ? trace[0] : Vm.CurrentLocation;
        public IReadOnlyList<(int SourceIndex, int Line)> CurrentTrace() => Vm.CurrentTrace();
        public int CallDepth => Vm.DebugCallDepth;

        public DebugValue? This => Vm.DebugThisValue is Value v ? _target.Describe("this", v, null) : null;

        public IReadOnlyList<DebugScope> Scopes() => Vm.DebugScopeChain().Select(level => new DebugScope(level.Depth, level.IsFunctionTopLevel,
            level.Variables.Select((v, slot) => _target.Describe(v.Name, v.Value, level.Scope != null ? new VmDebugTarget.SlotRef(level.Scope, slot) : null)).ToList())).ToList();

        public IReadOnlyList<DebugValue> Globals()
        {
            var scope = Vm.DebugGlobalScope;
            return Vm.DebugGlobals().Select((v, slot) => _target.Describe(v.Name, v.Value, new VmDebugTarget.SlotRef(scope, slot))).ToList();
        }

        public IReadOnlyList<DebugValue>? OperandStack() =>
            Vm.DebugStackSnapshot.Select((v, i) => new DebugValue($"[{i}]", v.ToString(), VmDebugTarget.KindOf(v.Kind), v.Kind.ToString())).ToList();

        public string? UnhandledError => Vm.UnhandledException is { } e ? new UncaughtScriptException(e).Message : null;
        public IReadOnlyList<(int SourceIndex, int Line)>? UnhandledTrace => Vm.UnhandledTrace;
        public bool IsPausedOnUnhandled => Vm.IsPausedOnUnhandled;
        public bool TryResumeUnhandled() => Vm.TryResumeUnhandled();

        public bool TrySetValue(DebugValue target, string text, out string error)
        {
            Value old;
            switch (target.Reference)
            {
                case VmDebugTarget.SlotRef s when s.Slot < s.Scope.SlotCount: old = s.Scope.GetSlot(s.Slot); break;
                case VmDebugTarget.FieldRef f when f.Object.TryGetFieldLocked(f.Field, out var fv): old = fv; break;
                case VmDebugTarget.ElementRef a when a.Index < a.Array.Length: old = a.Array.Items[a.Index]; break;
                default: error = $"{target.Name} cannot be set."; return false;
            }
            if (!VmDebugTarget.TryParseLike(old, text, out var value, out error)) return false;
            switch (target.Reference)
            {
                case VmDebugTarget.SlotRef s:
                    s.Scope.SetSlot(s.Slot, value);
                    return true;
                case VmDebugTarget.ElementRef a:
                    if (!a.Array.TrySet(a.Index, value)) { error = $"{target.Name} cannot be set."; return false; }
                    return true;
                case VmDebugTarget.FieldRef f:
                    if (Vm.DebugAssignMember(f.Object, f.Field, value, out var assignError)) return true;
                    error = assignError ?? $"{target.Name} cannot be set.";
                    return false;
            }
            error = $"{target.Name} cannot be set.";
            return false;
        }
    }
}
