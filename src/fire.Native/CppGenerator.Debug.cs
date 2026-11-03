using System.Globalization;
using System.Text;
using fire.Bytecode;
using fire.Runtime;

namespace fire.Native
{
    /// <summary>
    /// Debug builds (mode `debug`, docs/NATIVE_BACKEND.md "Debug-Builds", docs/NATIVE_DEBUG_PROTOCOL.md): every fire function gets a frame on a shadow stack
    /// (fire_debug.h) - pushed by its wrapper, which also notices the return into the caller - and the code updates its position where a line begins
    /// (`FIRE_DBG_L(source, line, scope state)`), at the same places where the stepping loops of the VM see a new position: the start of a range of the line
    /// table, and every jump target. The scope state is a static table per function: the levels of the scope chain with the names of their variables and
    /// their slots in the frame's array of variable addresses, like VM.DebugScopeChain.
    /// </summary>
    public sealed partial class CppGenerator
    {
        /// <summary>The program is built for the debugger (mode `debug`).</summary>
        private bool DebugBuild => _program.ExecutionMode == VmExecutionMode.Debug;

        /// <summary>The functions have a wrapper (`fN` calls `fN_b`): it counts the call depth (`try x.Take...`) or keeps the debug frame.</summary>
        private bool Wrapped => _usesTryDepth || DebugBuild;

        /// <summary>The platforms with a debug agent (threads and TCP of the desktop).</summary>
        private bool DebugAgent => _target.Native.Platform is "posix" or "windows";

        /// <summary>The debug information of one function, made anew by every generation of its chunk.</summary>
        private sealed class FuncDebug
        {
            public readonly Dictionary<(int Scope, int Slot), string> Names = new();
            public readonly List<(int Parent, int Top, List<(string Name, int Dv)> Vars)> Levels = new();
            public readonly Dictionary<string, int> LevelIndex = new(StringComparer.Ordinal);
            public readonly List<string> DvNames = new();
            public readonly Dictionary<string, int> DvIndex = new(StringComparer.Ordinal);
            public int Dv(string variable)
            {
                if (DvIndex.TryGetValue(variable, out int index)) return index;
                index = DvNames.Count;
                DvNames.Add(variable);
                DvIndex[variable] = index;
                return index;
            }
        }

        private readonly Dictionary<Func, FuncDebug> _funcDebug = new();
        /// <summary>The names of the global slots (the `DeclareLocal`s of the top level).</summary>
        private readonly Dictionary<int, string> _globalNames = new();
        private HashSet<FunctionProto>? _hiddenProtos;
        private Dictionary<FunctionProto, string>? _protoNames;

        /// <summary>Starts the debug information of a function (after pass 1: the states of the instructions are known): the names of its variables.</summary>
        private FuncDebug DebugBegin(Func f, List<Instr> code, Flow?[] entry)
        {
            var d = new FuncDebug();
            _funcDebug[f] = d;
            for (int i = 0; i < code.Count; i++)
            {
                if (entry[i] is not { } st || code[i].Op != OpCode.DeclareLocal) continue;
                var (id, declared) = st.Scopes[^1];
                string? name = f.Chunk.DeclaredNameAt(code[i].Addr);
                if (name == null) continue;
                if (id == GlobalScope && f.Kind == FuncKind.Main) _globalNames[declared] = name;
                else d.Names[(id, declared)] = name;   // (a fire body: its own variables of the top level are global slots behind the shared ones)
            }
            return d;
        }

        /// <summary>The index of the innermost level of the scope chain of <paramref name="st"/> (-1: only the global scope).</summary>
        private int DebugLevelOf(Func f, FuncDebug d, Flow st, ISet<string> locals)
        {
            int parent = -1;
            var key = new StringBuilder();
            for (int j = 0; j < st.Scopes.Count; j++)
            {
                var (id, declared) = st.Scopes[j];
                if (id == GlobalScope && !(j == 0 && f.Kind == FuncKind.FireBody)) continue;
                // (VM: the top level of a function is the scope whose parent is the global one - the function scope, or a block of the top level)
                // (2: the variables of a fire body's top level - the globals of its thread, not a scope of the chain)
                int top = id == GlobalScope ? 2 : id == FunctionScope || (j > 0 && st.Scopes[j - 1].Id == GlobalScope) ? 1 : 0;
                key.Append(id).Append(':').Append(declared).Append(';');
                if (!d.LevelIndex.TryGetValue(key.ToString(), out int index))
                {
                    var vars = new List<(string, int)>();
                    for (int k = id == GlobalScope ? f.GlobalSlotCount : 0; k < declared; k++)
                    {
                        string variable = id == GlobalScope ? $"T{k}" : id == FunctionScope ? $"P{k}" : $"B{id}_{k}";
                        string name = d.Names.TryGetValue((id, k), out var n) ? n
                            : id == GlobalScope ? $"(global {k})" : top == 1 && f.Chunk.DebugLocalNames.TryGetValue((0, k), out var p) ? p : $"(local {k})";
                        bool exists = locals.Contains(variable) || (id == FunctionScope && k < f.ParamLike);
                        vars.Add((name, exists ? d.Dv(variable) : -1));
                    }
                    index = d.Levels.Count;
                    d.Levels.Add((parent, top, vars));
                    d.LevelIndex[key.ToString()] = index;
                }
                parent = index;
            }
            return parent;
        }

        /// <summary>Before instruction <paramref name="i"/> (pass 2): the position where a line begins (or a jump lands), else the scope state where it changed.</summary>
        private void DebugBeforeInstruction(Func f, FuncDebug d, Chunk chunk, Instr ins, int i, Flow st, Out o, bool isTarget, ISet<string> locals)
        {
            int lv = DebugLevelOf(f, d, st, locals);
            var (src, line) = chunk.GetLocationRange(ins.Addr, out int start, out _);
            if (i == 0 || start == ins.Addr || isTarget)
                o.Code.AppendLine($"    FIRE_DBG_L({src}, {line}, {lv});");
            else if (lv != o.DbgLv)
                o.Code.AppendLine($"    DF->lv = {lv};");
            o.DbgLv = lv;
        }

        /// <summary>After instruction (pass 2): a global variable of the top level was declared - the debugger shows it from now on.</summary>
        private void DebugAfterInstruction(Func f, Instr ins, Flow st, Out o)
        {
            if (f.Kind != FuncKind.Main || ins.Op != OpCode.DeclareLocal || st.Scopes[^1].Id != GlobalScope) return;
            if (_lineDir != null) o.Code.AppendLine(_lineDir);
            o.Code.AppendLine($"    g_dbgGlobalCount = {st.Scopes[^1].Declared + 1};");
        }

        /// <summary>The start of the function proper: the addresses of its variables and its `this` go into its frame.</summary>
        private string DebugPrologue(Func f, FuncDebug d)
        {
            var sb = new StringBuilder();
            if (d.DvNames.Count > 0)
            {
                sb.AppendLine($"    Value* const dv_[] = {{{string.Join(", ", d.DvNames.Select(n => "&" + n))}}};");
                sb.AppendLine("    DF->vars = dv_;");
            }
            if (f.HasSelf || f.Kind == FuncKind.Lambda) sb.AppendLine("    DF->thisSlot = &self;");
            return sb.ToString();
        }

        /// <summary>The static tables of a function: its variables, the levels of its scope states, its name.</summary>
        private string DebugTables(Func f, FuncDebug d)
        {
            var sb = new StringBuilder();
            string n = f.Name;
            var offsets = new List<int>();
            var all = new List<(string Name, int Dv)>();
            foreach (var level in d.Levels) { offsets.Add(all.Count); all.AddRange(level.Vars); }
            if (all.Count > 0)
                sb.AppendLine($"static const DbgVar kDbgV_{n}[] = {{{string.Join(", ", all.Select(v => $"{{{CString(v.Name)}, {(v.Dv < 0 ? "0xFFFF" : v.Dv.ToString(CultureInfo.InvariantCulture))}}}"))}}};");
            if (d.Levels.Count > 0)
                sb.AppendLine($"static const DbgLevel kDbgLv_{n}[] = {{{string.Join(", ", d.Levels.Select((l, i) => $"{{{l.Parent}, {l.Top}, {l.Vars.Count}, {(l.Vars.Count > 0 ? $"kDbgV_{n} + {offsets[i]}" : "NULL")}}}"))}}};");
            sb.AppendLine($"static const DbgFunc kDbgF_{n} = {{{CString(DebugFunctionName(f))}, {(d.Levels.Count > 0 ? $"kDbgLv_{n}" : "NULL")}}};");
            return sb.ToString();
        }

        /// <summary>The wrapper `fN` around the function proper `fN_b`: it keeps the frame (and counts the call depth for `try x.Take...`).</summary>
        private string DebugWrapper(Func f, string call)
        {
            string hidden = IsHiddenFunc(f) ? "1" : "0";
            string depthIn = _usesTryDepth ? " ++g_callDepth;" : "", depthOut = _usesTryDepth ? " --g_callDepth;" : "";
            return f.Kind is FuncKind.FireBody or FuncKind.Main
                ? $"{{ DbgFrame DF_; fire_dbgEnter(&DF_, &kDbgF_{f.Name}, {hidden});{depthIn} {call};{depthOut} fire_dbgLeave(&DF_); }}"
                : $"{{ DbgFrame DF_; Value r_; fire_dbgEnter(&DF_, &kDbgF_{f.Name}, {hidden});{depthIn} r_ = {call};{depthOut} fire_dbgLeave(&DF_); return r_; }}";
        }

        /// <summary>The wrapper of the catch blocks of a `try`: while they run, the frame of their function is the top of the shadow stack (the VM has left the
        /// frames of the throw site; `resume` goes back to them - then the function is at the call it made again).</summary>
        private static string DebugCatchWrapper(Func f, string catchName) =>
            $"static void {catchName}(void* ctx_, uint32_t ci, Value e) {{ DbgFrame* saved_ = g_dbgTop; DbgFrame* own_ = ((FR_{f.Name}*)ctx_)->DF; int32_t src_ = own_->src, line_ = own_->line, lv_ = own_->lv; " +
            $"g_dbgTop = own_; {catchName}_b(ctx_, ci, e); own_->src = src_; own_->line = line_; own_->lv = lv_; g_dbgTop = saved_; }}";

        /// <summary>The name of a function for the call stack.</summary>
        private string DebugFunctionName(Func f)
        {
            if (f.Kind == FuncKind.Main) return "<main>";
            if (f.Kind == FuncKind.FireBody) return "<fire>";
            if (f.Kind == FuncKind.Lambda) return "<lambda>";
            if (_protoNames == null)
            {
                _protoNames = new Dictionary<FunctionProto, string>();
                foreach (var rc in _program.Program.Classes.Values)
                {
                    foreach (var (name, protos) in rc.Methods) foreach (var p in protos) _protoNames.TryAdd(p, $"{rc.Name}.{name}");
                    foreach (var p in rc.Constructors.Values.Concat(rc.ExtraConstructors)) _protoNames.TryAdd(p, $"{rc.Name}.construct");
                    if (rc.Destructor != null) _protoNames.TryAdd(rc.Destructor, $"{rc.Name}.destruct");
                }
            }
            if (f.Proto != null && _protoNames.TryGetValue(f.Proto, out var known)) return known;
            return f.Chunk.OwnerClass is { } owner ? $"{owner.Name}.<{f.Kind.ToString().ToLowerInvariant()}>" : $"<{f.Kind.ToString().ToLowerInvariant()}>";
        }

        /// <summary>A function the VM always runs nested (inside the instruction that needs it): destructors, the getters and setters of properties, operators,
        /// GetIndex/SetIndex, the default values of parameters and the conversion functions of units. The debugger does not stop in it.</summary>
        private bool IsHiddenFunc(Func f)
        {
            if (f.Kind == FuncKind.Dtor) return true;
            if (f.Proto == null) return false;
            if (_hiddenProtos == null)
            {
                _hiddenProtos = new HashSet<FunctionProto>();
                var seen = new HashSet<Chunk>();
                void Visit(FunctionProto p)
                {
                    foreach (var dp in p.ParamDefaults) if (dp != null) _hiddenProtos.Add(dp);
                    VisitChunk(p.Chunk);
                }
                void VisitChunk(Chunk c)
                {
                    if (!seen.Add(c)) return;
                    foreach (var inner in c.Functions) Visit(inner);
                }
                VisitChunk(_program.Program.TopLevel);
                foreach (var rc in _program.Program.Classes.Values)
                {
                    foreach (var (name, protos) in rc.Methods)
                        foreach (var p in protos)
                        {
                            if (name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal) || name.StartsWith("operator", StringComparison.Ordinal)
                                || name is "GetIndex" or "SetIndex")
                                _hiddenProtos.Add(p);
                            Visit(p);
                        }
                    foreach (var p in rc.Constructors.Values.Concat(rc.ExtraConstructors)) Visit(p);
                    if (rc.Destructor != null) Visit(rc.Destructor);
                }
                foreach (var (_, to, from) in _unitConverters.Values) { if (to.Proto != null) _hiddenProtos.Add(to.Proto); if (from.Proto != null) _hiddenProtos.Add(from.Proto); }
            }
            return _hiddenProtos.Contains(f.Proto);
        }

        /// <summary>A statement that runs fire code the VM runs nested (from the runtime hooks): no stops inside.</summary>
        private string Nested(string statement) => DebugBuild ? $"FIRE_DBG_NESTED({statement.TrimEnd(';')});" : statement;

        /// <summary>The tables of the program for the debugger (after the functions): sources, globals, classes, and the program itself.</summary>
        private string DebugProgramTables()
        {
            var sb = new StringBuilder();
            var files = _program.SourceFiles ?? Array.Empty<string?>();
            var hashes = _program.SourceHashes ?? Array.Empty<string?>();
            int nsources = Math.Max(files.Count, hashes.Count);
            sb.AppendLine("/* ---- the debugger (fire_debug.h): sources, globals, classes ---- */");
            if (nsources > 0)
                sb.AppendLine("static const DbgSource kDbgSources[] = {" + string.Join(", ", Enumerable.Range(0, nsources).Select(i =>
                    $"{{{(i < files.Count && files[i] != null ? CString(files[i]!) : "NULL")}, {(i < hashes.Count && hashes[i] != null ? CString(hashes[i]!) : "NULL")}}}")) + "};");
            int nglobals = _globals.Count == 0 ? 0 : _globals.Max() + 1;
            nglobals = Math.Max(nglobals, _globalNames.Count == 0 ? 0 : _globalNames.Keys.Max() + 1);
            if (nglobals > 0)
                sb.AppendLine("static const DbgGlobal kDbgGlobals[] = {" + string.Join(", ", Enumerable.Range(0, nglobals).Select(i =>
                    $"{{{(_globalNames.TryGetValue(i, out var n) ? CString(n) : "NULL")}, {(_globals.Contains(i) ? $"&G{i}" : "NULL")}}}")) + "};");
            foreach (var cls in _classList)
                if (cls.Fields.Count > 0) sb.AppendLine($"static const char* const kDbgFields{cls.Id}[] = {{{string.Join(", ", cls.Fields.Select(CString))}}};");
            sb.AppendLine("static const DbgClass kDbgClasses[] = {" + string.Join(", ", _classList.Select(c => $"{{{CString(c.Rc.Name)}, {c.Fields.Count}, {(c.Fields.Count > 0 ? $"kDbgFields{c.Id}" : "NULL")}}}").DefaultIfEmpty("{\"\", 0, NULL}")) + "};");
            sb.AppendLine("const DbgClass* fire_dbgClass(uint32_t cls) {");
            if (_classList.Count > 0)
            {
                sb.AppendLine("    switch (cls) {");
                for (int i = 0; i < _classList.Count; i++) sb.AppendLine($"        case {_classList[i].Id}: return &kDbgClasses[{i}];");
                sb.AppendLine("        default: return NULL;");
                sb.AppendLine("    }");
            }
            else sb.AppendLine("    (void)cls; return NULL;");
            sb.AppendLine("}");
            // `set` of a field: through the setter of the field (the declared kind is checked, probes hear it) as if the class itself assigned it
            sb.AppendLine("void fire_dbgSetField(Value o, uint32_t index, Value x) {");
            sb.AppendLine("    OwnList scratch_ = {NULL, NULL, poolMark(), 0, NULL, NULL};");
            sb.AppendLine("    (void)scratch_;");
            var setters = _classList.Where(c => c.Fields.Any(_fieldNames.Contains)).ToList();
            if (setters.Count > 0)
            {
                sb.AppendLine("    switch (objAt(o)->cls) {");
                foreach (var cls in setters)
                {
                    sb.AppendLine($"        case {cls.Id}: switch (index) {{");
                    for (int i = 0; i < cls.Fields.Count; i++)
                    {
                        string field = cls.Fields[i];
                        if (!_fieldNames.Contains(field)) continue;
                        string tail = (HasProperty(field) ? ", &scratch_" : "") + (RestrictedField(field) ? $", {cls.Id}u" : "");
                        sb.AppendLine($"            case {i}: sf_{Mangle(field)}(o, x{tail}); leave(&scratch_); return;");
                    }
                    sb.AppendLine("            default: break;");
                    sb.AppendLine("        } break;");
                }
                sb.AppendLine("        default: break;");
                sb.AppendLine("    }");
            }
            sb.AppendLine("    setFieldAt(o, index, x);");
            sb.AppendLine("}");
            var (entrySrc, entryLine) = _program.Program.TopLevel.GetLocation(0);
            sb.AppendLine($"static const DbgProgram kDbgProgram = {{{(nsources > 0 ? "kDbgSources" : "NULL")}, {nsources}, {_program.FirstUserSource}, {(nglobals > 0 ? "kDbgGlobals" : "NULL")}, {nglobals}, {entrySrc}, {entryLine}, {CString(_target.Name)}}};");
            return sb.ToString();
        }
    }
}
