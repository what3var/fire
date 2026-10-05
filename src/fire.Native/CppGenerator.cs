using System.Globalization;
using System.Text;
using fire.Bytecode;
using fire.Runtime;
using fire.Values;

namespace fire.Native
{
    /// <summary>
    /// Translates a linked program (bytecode) into one C++ source file that includes <c>fire_rt.hpp</c>.
    ///
    /// Every chunk (the top-level code and each function, method, constructor, field initializer and destructor) becomes a
    /// C++ function. The operand stack disappears: its depth is known at every instruction, so slot <c>k</c> of the stack is
    /// the C++ local <c>s{k}</c>, and the C++ compiler turns those into registers. Scopes disappear as far as they hold
    /// nothing but values: a variable that lives in a scope is a C++ local (<c>B{scope}_{slot}</c>, parameters
    /// <c>P{slot}</c>, globals <c>G{slot}</c>). Jumps become <c>goto</c>.
    ///
    /// Objects: a class is a class id plus a field layout (base fields first, like the VM), an object is a
    /// <c>fire::Obj</c> with its fields as <c>Value</c>s. A scope that can own objects (it creates some or calls something
    /// that may hand one back) gets a local <c>OwnList</c>; leaving the scope - or <c>return</c> - destroys what is on it
    /// (SPEC 2.3), a returned object moves to the caller's innermost scope. Method calls are dispatched over the classes that
    /// exist in the program (it is a closed world); field access goes through small generated helpers.
    ///
    /// What is translated is a growing subset of the ISA (see <see cref="NativeNotSupportedException"/> for the rest):
    /// the generator never produces code with different semantics, it refuses.
    /// </summary>
    public sealed class CppGenerator
    {
        private enum FuncKind { Main, Static, Method, Ctor, Init, Dtor }

        private sealed class Func
        {
            public required FunctionProto? Proto;
            public required Chunk Chunk;
            public required FuncKind Kind;
            public int Index;
            public string Name => Kind == FuncKind.Main ? "fire_main" : $"f{Index}";
            public bool HasSelf => Kind is FuncKind.Method or FuncKind.Ctor or FuncKind.Init or FuncKind.Dtor;
            public int ParamCount => Proto?.ParamCount ?? 0;
            /// <summary>Scopes of this function that can own objects (they get an OwnList).</summary>
            public readonly HashSet<int> NeedsList = new();
            public string Code = "";
            public string Signature => Kind == FuncKind.Main ? "static void fire_main()"
                : $"static Value {Name}(" + string.Join(", ", (HasSelf ? new[] { "Value self" } : Array.Empty<string>()).Concat(Enumerable.Range(0, ParamCount).Select(i => $"Value P{i}"))) + ")";
        }

        private sealed class ClassInfo
        {
            public required RuntimeClass Rc;
            public required int Id;
            public IReadOnlyList<string> Fields => Rc.FlattenedFieldNames;
        }

        private readonly LinkedProgram _program;
        private readonly IReadOnlyList<string> _natives;
        private readonly TargetProfile _target;
        private readonly List<Unit> _units = new() { Unit.Unitless };
        private readonly List<string> _strings = new();
        private readonly Dictionary<string, int> _stringIndex = new();
        private readonly HashSet<int> _globals = new();
        private readonly Dictionary<FunctionProto, Func> _funcByProto = new(ReferenceEqualityComparer.Instance);
        private readonly List<Func> _funcs = new();
        private readonly Queue<Func> _pending = new();
        private readonly Dictionary<string, ClassInfo> _classes = new();
        private readonly List<ClassInfo> _classList = new();
        private readonly SortedSet<(string Name, int Argc)> _dispatchers = new();
        private readonly SortedSet<string> _fieldNames = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Class, string Field), int> _staticFields = new();

        private CppGenerator(LinkedProgram program, TargetProfile target)
        {
            _program = program;
            _target = target;
            _natives = program.NativeNames
                ?? throw new NativeNotSupportedException("the program does not carry its native function names (link it with Linker.CompileAndLink)");
        }

        /// <summary>Generates the C++ source of the program for <paramref name="target"/> (default: the machine this runs on).
        /// Link the program with the same target (<c>Linker.CompileAndLink(..., target:)</c>) so that its libraries are checked.</summary>
        public static string Generate(LinkedProgram program, TargetProfile? target = null) => new CppGenerator(program, target ?? TargetProfile.Host).Run();

        // -------------------------------------------------------------------------------------------------------------
        // Registries
        // -------------------------------------------------------------------------------------------------------------
        private Func GetFunc(FunctionProto proto, FuncKind kind)
        {
            if (_funcByProto.TryGetValue(proto, out var existing))
            {
                if (existing.Kind != kind) throw new NativeNotSupportedException($"a function is used both as {existing.Kind} and as {kind}");
                return existing;
            }
            var func = new Func { Proto = proto, Chunk = proto.Chunk, Kind = kind, Index = _funcs.Count };
            _funcs.Add(func);
            _funcByProto[proto] = func;
            _pending.Enqueue(func);
            return func;
        }

        private RuntimeClass FindClass(string name) =>
            _program.Program.Classes.TryGetValue(name, out var rc) ? rc : throw new NativeNotSupportedException($"unknown class '{name}'");

        private ClassInfo RegisterClass(string name)
        {
            if (_classes.TryGetValue(name, out var info)) return info;
            var rc = FindClass(name);
            if (rc.IsActor) throw new NativeNotSupportedException($"actor class '{name}'");
            info = new ClassInfo { Rc = rc, Id = _classList.Count };
            _classes[name] = info;
            _classList.Add(info);
            return info;
        }

        private int StaticFieldIndex(string className, string field)
        {
            var owner = FindClass(className).FindStaticFieldOwner(field)
                ?? throw new NativeNotSupportedException($"static field '{className}.{field}' not found");
            var key = (owner.Name, field);
            if (!_staticFields.TryGetValue(key, out int index)) { index = _staticFields.Count; _staticFields[key] = index; }
            return index;
        }

        private static string Mangle(string name)
        {
            var sb = new StringBuilder();
            foreach (char c in name)
                if (char.IsAsciiLetterOrDigit(c) || c == '_') sb.Append(c);
                else sb.Append("_x").Append(((int)c).ToString("X4"));
            return sb.ToString();
        }

        // -------------------------------------------------------------------------------------------------------------
        // Whole program
        // -------------------------------------------------------------------------------------------------------------
        private string Run()
        {
            var main = new Func { Proto = null, Chunk = _program.Program.TopLevel, Kind = FuncKind.Main };
            GenerateChunk(main);

            // Fixpoint: generating a function can create classes and method calls, which pull in more functions.
            while (true)
            {
                while (_pending.Count > 0) GenerateChunk(_pending.Dequeue());
                foreach (var (name, argc) in _dispatchers)
                    foreach (var cls in _classList.ToList())
                        if (cls.Rc.FindMethodWithAccess(name, argc).Proto is { } method)
                        {
                            if (method.ParamCount != argc) throw new NativeNotSupportedException($"default arguments in {cls.Rc.Name}.{name}");
                            GetFunc(method, FuncKind.Method);
                        }
                foreach (var cls in _classList.ToList())
                    for (var rc = cls.Rc; rc != null; rc = rc.Base)
                        if (rc.Destructor != null) GetFunc(rc.Destructor, FuncKind.Dtor);
                if (_pending.Count == 0) break;
            }

            var sb = new StringBuilder();
            sb.AppendLine("// Generated by fire.Native from the bytecode - do not edit.");
            sb.AppendLine($"// target: {_target.Name}");
            sb.AppendLine($"#define FIRE_TARGET \"{_target.Name}\"");
            sb.AppendLine($"#define FIRE_TARGET_{_target.Name.ToUpperInvariant()} 1");
            sb.AppendLine($"#define FIRE_HAL_{_target.HalPackage.ToUpperInvariant()} 1");
            sb.AppendLine($"#define FIRE_DEFAULT_STACK_BYTES {_target.DefaultStackBytes}");
            if (_program.FloatWidth == 32) sb.AppendLine("#define FIRE_FLOAT32 1 // #floatwidth 32: float is a 32-bit float, like in the VM");
            sb.AppendLine("#include \"fire_rt.hpp\"");
            sb.AppendLine("using namespace fire;");
            sb.AppendLine();
            sb.AppendLine("namespace fire {");
            sb.Append("const char* const g_unitNames[] = {");
            sb.Append(string.Join(", ", _units.Select(u => u.IsUnitless ? "\"\"" : CString(u.ToString()))));
            sb.AppendLine("};");
            sb.AppendLine("}");
            sb.AppendLine();
            for (int i = 0; i < _strings.Count; i++)
            {
                var bytes = Encoding.UTF8.GetBytes(_strings[i]);
                sb.AppendLine($"static const StrObj K{i} = {{{bytes.Length}, {CString(bytes)}}};");
            }
            if (_strings.Count > 0) sb.AppendLine();
            if (_globals.Count > 0)
            {
                sb.AppendLine("static Value " + string.Join(", ", _globals.OrderBy(g => g).Select(g => $"G{g}")) + ";");
                sb.AppendLine();
            }
            foreach (var (key, index) in _staticFields.OrderBy(kv => kv.Value))
                sb.AppendLine($"static Value SF{index} = Undef(); // {key.Class}.{key.Field}");
            if (_staticFields.Count > 0) sb.AppendLine();

            // Prototypes of everything, then the helpers (they call functions), then the functions (they call helpers).
            foreach (var f in _funcs) sb.AppendLine(f.Signature + ";");
            foreach (var name in _fieldNames) sb.AppendLine($"static inline Value gf_{Mangle(name)}(Value v);").AppendLine($"static inline void sf_{Mangle(name)}(Value v, Value x);");
            foreach (var (name, argc) in _dispatchers) sb.AppendLine(DispatcherSignature(name, argc) + ";");
            sb.AppendLine();

            foreach (var name in _fieldNames) sb.AppendLine(FieldHelpers(name));
            foreach (var (name, argc) in _dispatchers) sb.AppendLine(Dispatcher(name, argc));
            sb.AppendLine(DestructorTable());

            foreach (var f in _funcs) sb.AppendLine(f.Code);
            sb.AppendLine(main.Code);
            if (_target.IsEmbedded)
            {
                // The board's startup code calls app_main (ESP-IDF) once FreeRTOS is running; there is no process to exit.
                sb.AppendLine("extern \"C\" void app_main(void) {");
                sb.AppendLine("    fire_main();");
                sb.AppendLine("    std::fflush(stdout);");
                sb.AppendLine("}");
            }
            else
            {
                sb.AppendLine("int main() {");
                sb.AppendLine("    fire_main();");
                sb.AppendLine("    std::fflush(stdout);");
                sb.AppendLine("    return 0;");
                sb.AppendLine("}");
            }
            return sb.ToString();
        }

        // -------------------------------------------------------------------------------------------------------------
        // Helpers generated after the fixpoint (their content depends on all classes of the program)
        // -------------------------------------------------------------------------------------------------------------
        private static string DispatcherSignature(string name, int argc) =>
            $"static Value call_{Mangle(name)}_{argc}(" + string.Join(", ", new[] { "Value self" }.Concat(Enumerable.Range(0, argc).Select(i => $"Value a{i}"))) + ")";

        /// <summary>A method call on an object: picks the implementation by the class of the receiver.</summary>
        private string Dispatcher(string name, int argc)
        {
            var byFunc = new Dictionary<Func, List<int>>();
            foreach (var cls in _classList)
                if (cls.Rc.FindMethodWithAccess(name, argc).Proto is { } method)
                {
                    var f = _funcByProto[method];
                    if (!byFunc.TryGetValue(f, out var ids)) byFunc[f] = ids = new List<int>();
                    ids.Add(cls.Id);
                }
            if (byFunc.Count == 0)
                throw new NativeNotSupportedException($"method '{name}' with {argc} argument(s): no class of the program defines it (built-in methods such as TakeTo, string and array methods are not supported yet)");

            var args = string.Concat(Enumerable.Range(0, argc).Select(i => $", a{i}"));
            var sb = new StringBuilder();
            sb.AppendLine(DispatcherSignature(name, argc));
            sb.AppendLine("{");
            if (byFunc.Count == 1 && byFunc.Values.First().Count == _classList.Count)
            {
                var only = byFunc.Keys.First();
                sb.AppendLine("    asObj(self);");
                sb.AppendLine($"    return {only.Name}(self{args});");
            }
            else
            {
                sb.AppendLine("    switch (asObj(self)->cls) {");
                foreach (var (f, ids) in byFunc)
                {
                    foreach (int id in ids) sb.AppendLine($"        case {id}:");
                    sb.AppendLine($"            return {f.Name}(self{args});");
                }
                sb.AppendLine("        default: break;");
                sb.AppendLine("    }");
                sb.AppendLine($"    fatal(\"Method '{name}' not found on this object.\");");
            }
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>Read and write access to the field `name`: the index of a field is the same in a class and all its subclasses,
        /// but not between unrelated classes that happen to use the same name.</summary>
        private string FieldHelpers(string name)
        {
            var indexByClass = new Dictionary<int, int>();
            foreach (var cls in _classList)
            {
                if (cls.Rc.FieldIndex.TryGetValue(name, out int index)) indexByClass[cls.Id] = index;
                else if (cls.Rc.FindMethodWithAccess("get_" + name, 0).Proto != null || cls.Rc.FindMethodWithAccess("set_" + name, 1).Proto != null)
                    throw new NativeNotSupportedException($"property '{name}' of class {cls.Rc.Name}");
            }
            if (indexByClass.Count == 0)
                throw new NativeNotSupportedException($"member '{name}': no class of the program has a field with this name (built-in members such as Length are not supported yet)");

            string m = Mangle(name);
            string indexCode;
            if (indexByClass.Count == _classList.Count && indexByClass.Values.Distinct().Count() == 1)
                indexCode = $"    const uint32_t idx = {indexByClass.Values.First()};";
            else
            {
                var cases = new StringBuilder();
                foreach (var group in indexByClass.GroupBy(kv => kv.Value))
                {
                    foreach (var kv in group) cases.Append($"        case {kv.Key}:\n");
                    cases.Append($"            idx = {group.Key}; break;\n");
                }
                indexCode = "    uint32_t idx = 0;\n    switch (o->cls) {\n" + cases + $"        default: fatal(\"Field '{name}' not found on this object.\");\n    }}";
            }
            return $"static inline Value gf_{m}(Value v) {{\n    Obj* o = asObj(v);\n{indexCode}\n    return o->fields()[idx];\n}}\n"
                 + $"static inline void sf_{m}(Value v, Value x) {{\n    Obj* o = asObj(v);\n{indexCode}\n    o->fields()[idx] = x;\n}}\n";
        }

        /// <summary>fire::runDestructors: the destructors of the class chain, derived class first.</summary>
        private string DestructorTable()
        {
            var sb = new StringBuilder();
            sb.AppendLine("namespace fire {");
            sb.AppendLine("void runDestructors(Obj* o) {");
            var cases = new StringBuilder();
            foreach (var cls in _classList)
            {
                var chain = new List<string>();
                for (var rc = cls.Rc; rc != null; rc = rc.Base)
                    if (rc.Destructor != null) chain.Add($"{_funcByProto[rc.Destructor].Name}(ObjV(o));");
                if (chain.Count > 0) cases.AppendLine($"        case {cls.Id}: {string.Join(" ", chain)} break;");
            }
            if (cases.Length > 0)
            {
                sb.AppendLine("    switch (o->cls) {");
                sb.Append(cases);
                sb.AppendLine("        default: break;");
                sb.AppendLine("    }");
            }
            else sb.AppendLine("    (void)o;");
            sb.AppendLine("}");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // -------------------------------------------------------------------------------------------------------------
        // Flow state: stack depth and the static scope chain (identity of the scope + number of declared slots)
        // -------------------------------------------------------------------------------------------------------------
        private const int FunctionScope = -1;
        private const int GlobalScope = -2;

        private sealed class Flow
        {
            public int Depth;
            public List<(int Id, int Declared)> Scopes = new();

            public Flow Clone() => new() { Depth = Depth, Scopes = new List<(int, int)>(Scopes) };

            public bool SameAs(Flow o) => Depth == o.Depth && Scopes.SequenceEqual(o.Scopes);
        }

        private static string ListName(int scopeId) => scopeId == FunctionScope ? "OP" : scopeId == GlobalScope ? "OG" : $"O{scopeId}";

        private void GenerateChunk(Func f)
        {
            var chunk = f.Chunk;
            string name = f.Name;
            var code = OpInfo.Decode(chunk.Code);
            var indexOfAddr = new Dictionary<int, int>();
            for (int i = 0; i < code.Count; i++) indexOfAddr[code[i].Addr] = i;

            var entry = new Flow?[code.Count];
            var start = new Flow();
            start.Scopes.Add(f.Kind == FuncKind.Main ? (GlobalScope, 0) : (FunctionScope, f.ParamCount));
            entry[0] = start;

            var targets = new HashSet<int>();
            var work = new Stack<int>();
            work.Push(0);
            int maxDepth = 0;
            var locals = new SortedSet<string>(StringComparer.Ordinal);

            void Propagate(int addr, Flow state, int from)
            {
                if (!indexOfAddr.TryGetValue(addr, out int target))
                    throw new NativeNotSupportedException($"jump to {addr} inside an instruction ({name})");
                if (entry[target] == null) { entry[target] = state.Clone(); work.Push(target); }
                else if (!entry[target]!.SameAs(state))
                    throw new NativeNotSupportedException($"inconsistent stack/scope state at {addr} ({name}), reached from {from}");
            }

            // Pass 1: propagate states (and find out which scopes can own objects); pass 2: emit with the states found.
            while (work.Count > 0)
            {
                int idx = work.Pop();
                var state = entry[idx]!.Clone();
                var ins = code[idx];
                var step = Step(f, chunk, ins, state, null, locals);
                maxDepth = Math.Max(maxDepth, Math.Max(entry[idx]!.Depth, state.Depth));
                if (step.JumpTarget is int jt) { targets.Add(jt); Propagate(jt, state, ins.Addr); }
                if (step.FallsThrough)
                {
                    if (idx + 1 >= code.Count) throw new NativeNotSupportedException($"code of {name} runs past its end");
                    Propagate(code[idx + 1].Addr, state, ins.Addr);
                }
            }

            var sb = new StringBuilder();
            sb.AppendLine(f.Signature);
            sb.AppendLine("{");
            if (f.HasSelf) sb.AppendLine("    (void)self;");
            for (int i = 0; i < f.ParamCount; i++) sb.AppendLine($"    (void)P{i};");
            if (maxDepth > 0) sb.AppendLine("    Value " + string.Join(", ", Enumerable.Range(0, maxDepth).Select(i => $"s{i}")) + ";");
            var localsToDeclare = locals.Where(l => !(l.StartsWith("P") && int.Parse(l.AsSpan(1)) < f.ParamCount)).ToList();
            if (localsToDeclare.Count > 0) sb.AppendLine("    Value " + string.Join(", ", localsToDeclare) + ";");
            if (f.NeedsList.Count > 0) sb.AppendLine("    OwnList " + string.Join(", ", f.NeedsList.OrderBy(i => i).Select(i => ListName(i) + " = {nullptr, nullptr}")) + ";");

            for (int i = 0; i < code.Count; i++)
            {
                if (entry[i] == null) continue; // unreachable
                var ins = code[i];
                if (targets.Contains(ins.Addr)) sb.AppendLine($"L{ins.Addr}:;");
                sb.AppendLine($"    // {ins.Addr}: {ins.Op}{(ins.A.Length > 0 ? " " + string.Join(",", ins.A) : "")}");
                var state = entry[i]!.Clone();
                Step(f, chunk, ins, state, sb, locals);
            }
            if (f.Kind != FuncKind.Main) sb.AppendLine("    fatal(\"control flow fell off the end of a function\");");
            sb.AppendLine("}");
            f.Code = sb.ToString();
        }

        private readonly record struct StepResult(bool FallsThrough, int? JumpTarget);

        private string Var(Flow st, int depth, int slot, ISet<string> locals)
        {
            int scopeIndex = st.Scopes.Count - 1 - depth;
            if (scopeIndex < 0) throw new NativeNotSupportedException("access to a variable outside of the function (closures are not supported yet)");
            int id = st.Scopes[scopeIndex].Id;
            if (id == GlobalScope) { _globals.Add(slot); return $"G{slot}"; }
            string v = id == FunctionScope ? $"P{slot}" : $"B{id}_{slot}";
            locals.Add(v);
            return v;
        }

        /// <summary>Applies the effect of one instruction to <paramref name="st"/>; with a builder it also emits the C++.</summary>
        private StepResult Step(Func f, Chunk chunk, Instr ins, Flow st, StringBuilder? sb, ISet<string> locals)
        {
            int d = st.Depth;
            string fn = f.Name;
            string S(int k) => $"s{k}";
            void E(string text) { sb?.Append("    ").AppendLine(text); }
            void Need(int n)
            {
                if (d < n) throw new NativeNotSupportedException($"stack underflow at {ins.Addr} in {fn}");
            }
            StepResult Next() { st.Depth = d; return new StepResult(true, null); }
            string Str(int constIdx) => chunk.Constants[constIdx].AsString();
            // The innermost scope owns what is created or handed back at this point.
            string OwnerList()
            {
                f.NeedsList.Add(st.Scopes[^1].Id);
                return ListName(st.Scopes[^1].Id);
            }
            void RequireSelf() { if (!f.HasSelf) throw new NativeNotSupportedException($"'this' outside of an instance member ({fn} at {ins.Addr})"); }
            string Args(int first, int count) => string.Concat(Enumerable.Range(first, count).Select(i => ", " + S(i)));

            switch (ins.Op)
            {
                case OpCode.LoadConst:
                    E($"{S(d)} = {Constant(chunk.Constants[ins.A[0]])};");
                    d++; return Next();
                case OpCode.Pop:
                    Need(1); d--; return Next();
                case OpCode.Dup:
                    Need(1); E($"{S(d)} = {S(d - 1)};"); d++; return Next();
                case OpCode.Swap:
                    Need(2); E($"{{ Value t = {S(d - 1)}; {S(d - 1)} = {S(d - 2)}; {S(d - 2)} = t; }}"); return Next();

                case OpCode.DeclareLocal:
                {
                    Need(1);
                    var (id, declared) = st.Scopes[^1];
                    if (id == GlobalScope) { _globals.Add(declared); E($"G{declared} = {S(d - 1)};"); }
                    else
                    {
                        string v = id == FunctionScope ? $"P{declared}" : $"B{id}_{declared}";
                        locals.Add(v);
                        E($"{v} = {S(d - 1)};");
                    }
                    st.Scopes[^1] = (id, declared + 1);
                    d--; return Next();
                }
                case OpCode.LoadLocal:
                    E($"{S(d)} = {Var(st, ins.A[0], ins.A[1], locals)};");
                    d++; return Next();
                case OpCode.StoreLocal:
                    Need(1); E($"{Var(st, ins.A[0], ins.A[1], locals)} = {S(d - 1)};"); return Next();
                case OpCode.StoreLocalPop:
                    Need(1); E($"{Var(st, ins.A[0], ins.A[1], locals)} = {S(d - 1)};"); d--; return Next();
                case OpCode.LoadGlobal:
                    _globals.Add(ins.A[0]); E($"{S(d)} = G{ins.A[0]};"); d++; return Next();
                case OpCode.StoreGlobal:
                    Need(1); _globals.Add(ins.A[0]); E($"G{ins.A[0]} = {S(d - 1)};"); return Next();
                case OpCode.StoreGlobalPop:
                    Need(1); _globals.Add(ins.A[0]); E($"G{ins.A[0]} = {S(d - 1)};"); d--; return Next();
                case OpCode.ArithLocalConstPop:
                {
                    string v = Var(st, ins.A[0], ins.A[1], locals);
                    E($"{v} = {(ins.A[3] == 0 ? "add" : "sub")}({v}, {Constant(chunk.Constants[ins.A[2]])});");
                    return Next();
                }
                case OpCode.ArithGlobalConstPop:
                {
                    _globals.Add(ins.A[0]);
                    E($"G{ins.A[0]} = {(ins.A[2] == 0 ? "add" : "sub")}(G{ins.A[0]}, {Constant(chunk.Constants[ins.A[1]])});");
                    return Next();
                }

                case OpCode.Add: return Binary("add");
                case OpCode.Sub: return Binary("sub");
                case OpCode.Mul: return Binary("mul");
                case OpCode.Div: return Binary("divide");
                case OpCode.Mod: return Binary("modulo");
                case OpCode.BitAnd: return Binary("bitAnd");
                case OpCode.BitOr: return Binary("bitOr");
                case OpCode.BitXor: return Binary("bitXor");
                case OpCode.ShiftLeft: return Binary("shl");
                case OpCode.ShiftRight: return Binary("shr");
                case OpCode.Neg: return Unary("negate");
                case OpCode.LogicalNot: return Unary("lnot");
                case OpCode.BitNot: return Unary("bitNot");

                case OpCode.Eq: return Compare("eq", false);
                case OpCode.NotEq: return Compare("eq", true);
                case OpCode.Lt: return Compare("lt", false);
                case OpCode.LtEq: return Compare("le", false);
                case OpCode.Gt: return Compare("gt", false);
                case OpCode.GtEq: return Compare("ge", false);

                case OpCode.Jump:
                    E($"goto L{ins.A[0]};");
                    return new StepResult(false, ins.A[0]);
                case OpCode.JumpIfFalse:
                    Need(1); E($"if (!truthy({S(d - 1)})) goto L{ins.A[0]};"); d--; st.Depth = d;
                    return new StepResult(true, ins.A[0]);
                case OpCode.JumpIfFalsePeek:
                    Need(1); E($"if (!truthy({S(d - 1)})) goto L{ins.A[0]};");
                    return new StepResult(true, ins.A[0]);
                case OpCode.JumpIfTruePeek:
                    Need(1); E($"if (truthy({S(d - 1)})) goto L{ins.A[0]};");
                    return new StepResult(true, ins.A[0]);
                case OpCode.JumpIfNotLt: return JumpIfNot("lt", false);
                case OpCode.JumpIfNotLtEq: return JumpIfNot("le", false);
                case OpCode.JumpIfNotGt: return JumpIfNot("gt", false);
                case OpCode.JumpIfNotGtEq: return JumpIfNot("ge", false);
                case OpCode.JumpIfNotEq: return JumpIfNot("eq", false);
                case OpCode.JumpIfNotNotEq: return JumpIfNot("eq", true);

                case OpCode.EnterScope:
                    st.Scopes.Add((ins.Addr, 0)); return Next();
                case OpCode.ExitScope:
                {
                    if (st.Scopes.Count <= 1) throw new NativeNotSupportedException($"ExitScope without scope at {ins.Addr} in {fn}");
                    int id = st.Scopes[^1].Id;
                    if (f.NeedsList.Contains(id)) E($"release(&{ListName(id)});");
                    st.Scopes.RemoveAt(st.Scopes.Count - 1);
                    return Next();
                }

                case OpCode.CallNative:
                {
                    int argc = ins.A[1];
                    Need(argc);
                    string native = ins.A[0] < _natives.Count ? _natives[ins.A[0]] : "?";
                    if (native == "print" && argc == 1)
                    {
                        E($"{S(d - 1)} = print({S(d - 1)});");
                        return Next();
                    }
                    throw new NativeNotSupportedException($"native function '{native}' (called in {fn})");
                }
                case OpCode.CallStaticMethod:
                {
                    int argc = ins.A[2];
                    Need(argc);
                    string cls = Str(ins.A[0]), method = Str(ins.A[1]);
                    var rc = FindClass(cls);
                    var proto = rc.FindMethodWithAccess(method, argc).Proto
                        ?? throw new NativeNotSupportedException($"{cls}.{method} with {argc} argument(s) not found");
                    if (!proto.IsStatic) throw new NativeNotSupportedException($"{cls}.{method} is not static");
                    if (proto.ParamCount != argc) throw new NativeNotSupportedException($"{cls}.{method}: default arguments");
                    var target = GetFunc(proto, FuncKind.Static);
                    string owner = OwnerList();
                    E($"{S(d - argc)} = {target.Name}({string.Join(", ", Enumerable.Range(d - argc, argc).Select(S))});");
                    E($"adopt({S(d - argc)}, &{owner});");
                    d = d - argc + 1; return Next();
                }
                case OpCode.Return:
                {
                    Need(1);
                    if (f.Kind == FuncKind.Main) throw new NativeNotSupportedException("return in top-level code");
                    var chain = st.Scopes.AsEnumerable().Reverse().Where(sc => f.NeedsList.Contains(sc.Id)).Select(sc => ListName(sc.Id)).ToList();
                    if (f.Kind == FuncKind.Ctor)
                    {
                        foreach (var l in chain) E($"release(&{l});");
                        E("return self;");
                    }
                    else if (chain.Count == 0) E($"return {S(d - 1)};");
                    else
                    {
                        E($"{{ Value r = {S(d - 1)};");
                        foreach (var l in chain) E($"  transferOut(r, &{l});");
                        foreach (var l in chain) E($"  release(&{l});");
                        E("  return r; }");
                    }
                    return new StepResult(false, null);
                }
                case OpCode.Halt:
                    if (f.Kind != FuncKind.Main) throw new NativeNotSupportedException("Halt in a function");
                    if (f.NeedsList.Contains(GlobalScope)) E("release(&OG);");
                    E("return;");
                    return new StepResult(false, null);
                case OpCode.SetTimeout:
                case OpCode.SetAutoSync:
                    // Waiting functions and queue processing do not exist in the native backend yet; the directives are no-ops there.
                    if (ins.Op == OpCode.SetTimeout) { Need(1); d--; }
                    return Next();

                // ---------------------------------------------------------------------------------------------------
                // Objects
                // ---------------------------------------------------------------------------------------------------
                case OpCode.LoadThis:
                    RequireSelf(); E($"{S(d)} = self;"); d++; return Next();
                case OpCode.GetField:
                {
                    Need(1);
                    string field = Str(ins.A[0]);
                    _fieldNames.Add(field);
                    E($"{S(d - 1)} = gf_{Mangle(field)}({S(d - 1)});");
                    return Next();
                }
                case OpCode.SetField:
                {
                    Need(2);
                    string field = Str(ins.A[0]);
                    _fieldNames.Add(field);
                    E($"sf_{Mangle(field)}({S(d - 2)}, {S(d - 1)});");
                    E($"{S(d - 2)} = {S(d - 1)};");
                    d--; return Next();
                }
                case OpCode.SetFieldOnThis:
                {
                    Need(1); RequireSelf();
                    string field = Str(ins.A[0]);
                    _fieldNames.Add(field);
                    E($"sf_{Mangle(field)}(self, {S(d - 1)});");
                    d--; return Next();
                }
                case OpCode.NewObject:
                case OpCode.NewObjectOwned:
                {
                    int argc = ins.A[1];
                    bool owned = ins.Op == OpCode.NewObjectOwned;
                    Need(argc + (owned ? 1 : 0));
                    var cls = RegisterClass(Str(ins.A[0]));
                    var ctor = cls.Rc.FindConstructor(argc) ?? throw new NativeNotSupportedException($"class {cls.Rc.Name} has no constructor with {argc} argument(s)");
                    if (ctor.ParamCount != argc) throw new NativeNotSupportedException($"{cls.Rc.Name}: constructor default arguments");
                    var target = GetFunc(ctor, FuncKind.Ctor);
                    int slot = d - argc - (owned ? 1 : 0);
                    string ownerExpr = owned ? $"&asObj({S(slot)})->owned" : "&" + OwnerList();
                    E($"{{ Value o = newObject({cls.Id}, {cls.Fields.Count}, {ownerExpr}); {target.Name}(o{Args(d - argc, argc)}); {S(slot)} = o; }}");
                    d = slot + 1; return Next();
                }
                case OpCode.ConstructBase:
                {
                    int argc = ins.A[1];
                    Need(argc); RequireSelf();
                    var rc = FindClass(Str(ins.A[0]));
                    var ctor = rc.FindConstructor(argc) ?? throw new NativeNotSupportedException($"class {rc.Name} has no constructor with {argc} argument(s)");
                    if (ctor.ParamCount != argc) throw new NativeNotSupportedException($"{rc.Name}: constructor default arguments");
                    var target = GetFunc(ctor, FuncKind.Ctor);
                    OwnerList();
                    E($"{target.Name}(self{Args(d - argc, argc)});");
                    E($"{S(d - argc)} = Undef();");
                    d = d - argc + 1; return Next();
                }
                case OpCode.CallProtoWithThis:
                {
                    int argc = ins.A[1];
                    Need(argc + 1);
                    var proto = chunk.Functions[ins.A[0]];
                    if (proto.ParamCount != argc) throw new NativeNotSupportedException($"initializer with {proto.ParamCount} parameters called with {argc}");
                    var target = GetFunc(proto, FuncKind.Init);
                    string owner = OwnerList();
                    int slot = d - argc - 1;
                    E($"{S(slot)} = {target.Name}({S(slot)}{Args(d - argc, argc)});");
                    E($"adopt({S(slot)}, &{owner});");
                    d = slot + 1; return Next();
                }
                case OpCode.CallMethod:
                {
                    int argc = ins.A[1];
                    Need(argc + 1);
                    string method = Str(ins.A[0]);
                    _dispatchers.Add((method, argc));
                    string owner = OwnerList();
                    int slot = d - argc - 1;
                    E($"{S(slot)} = call_{Mangle(method)}_{argc}({S(slot)}{Args(d - argc, argc)});");
                    E($"adopt({S(slot)}, &{owner});");
                    d = slot + 1; return Next();
                }
                case OpCode.CallBaseMethod:
                {
                    int argc = ins.A[2];
                    Need(argc); RequireSelf();
                    var rc = FindClass(Str(ins.A[0]));
                    string method = Str(ins.A[1]);
                    var proto = rc.FindMethodWithAccess(method, argc).Proto ?? throw new NativeNotSupportedException($"{rc.Name}.{method} with {argc} argument(s) not found");
                    if (proto.ParamCount != argc) throw new NativeNotSupportedException($"{rc.Name}.{method}: default arguments");
                    var target = GetFunc(proto, FuncKind.Method);
                    string owner = OwnerList();
                    E($"{S(d - argc)} = {target.Name}(self{Args(d - argc, argc)});");
                    E($"adopt({S(d - argc)}, &{owner});");
                    d = d - argc + 1; return Next();
                }
                case OpCode.GetStaticField:
                {
                    int index = StaticFieldIndex(Str(ins.A[0]), Str(ins.A[1]));
                    E($"{S(d)} = SF{index};");
                    d++; return Next();
                }
                case OpCode.SetStaticField:
                case OpCode.SetStaticFieldOnInit:
                {
                    Need(1);
                    int index = StaticFieldIndex(Str(ins.A[0]), Str(ins.A[1]));
                    E($"SF{index} = {S(d - 1)};");
                    return Next();
                }

                default:
                    throw new NativeNotSupportedException($"opcode {ins.Op} (in {fn} at {ins.Addr})");
            }

            StepResult Binary(string fnName)
            {
                Need(2); E($"{S(d - 2)} = {fnName}({S(d - 2)}, {S(d - 1)});"); d--; return Next();
            }
            StepResult Unary(string fnName)
            {
                Need(1); E($"{S(d - 1)} = {fnName}({S(d - 1)});"); return Next();
            }
            StepResult Compare(string fnName, bool negate)
            {
                Need(2); E($"{S(d - 2)} = Bool({(negate ? "!" : "")}{fnName}({S(d - 2)}, {S(d - 1)}));"); d--; return Next();
            }
            StepResult JumpIfNot(string fnName, bool negate)
            {
                Need(2); E($"if (!({(negate ? "!" : "")}{fnName}({S(d - 2)}, {S(d - 1)}))) goto L{ins.A[0]};");
                d -= 2; st.Depth = d;
                return new StepResult(true, ins.A[0]);
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // Constants
        // -------------------------------------------------------------------------------------------------------------
        private int UnitId(Unit? unit)
        {
            if (unit == null || unit.IsUnitless) return 0;
            for (int i = 1; i < _units.Count; i++) if (_units[i].Equals(unit)) return i;
            _units.Add(unit);
            return _units.Count - 1;
        }

        private string Constant(Value v)
        {
            switch (v.Kind)
            {
                case ValueKind.Int:
                {
                    long i = v.AsInt();
                    string lit = i == long.MinValue ? "INT64_MIN" : i.ToString(CultureInfo.InvariantCulture) + "LL";
                    int unit = UnitId(v.Unit);
                    return unit == 0 ? $"Int({lit})" : $"Int({lit}, {unit})";
                }
                case ValueKind.Float:
                {
                    int unit = UnitId(v.Unit);
                    string lit = DoubleLiteral(v.AsFloat(), _program.FloatWidth == 32);
                    return unit == 0 ? $"Float({lit})" : $"Float({lit}, {unit})";
                }
                case ValueKind.Bool: return v.AsBool() ? "Bool(true)" : "Bool(false)";
                case ValueKind.Char: return $"Char({(int)v.AsChar()})";
                case ValueKind.Undefined: { int unit = UnitId(v.Unit); return unit == 0 ? "Undef()" : $"Undef({unit})"; }
                case ValueKind.String:
                {
                    string s = v.AsString();
                    if (!_stringIndex.TryGetValue(s, out int index)) { index = _strings.Count; _strings.Add(s); _stringIndex[s] = index; }
                    return $"Str(&K{index})";
                }
                default:
                    throw new NativeNotSupportedException($"constant of kind {v.Kind}");
            }
        }

        private static string DoubleLiteral(double d, bool single)
        {
            if (double.IsNaN(d)) return single ? "std::nanf(\"\")" : "std::nan(\"\")";
            if (double.IsPositiveInfinity(d)) return single ? "HUGE_VALF" : "HUGE_VAL";
            if (double.IsNegativeInfinity(d)) return single ? "-HUGE_VALF" : "-HUGE_VAL";
            if (d == 0 && double.IsNegative(d)) return single ? "-0.0f" : "-0.0";
            string text = single ? ((float)d).ToString("R", CultureInfo.InvariantCulture) : d.ToString("R", CultureInfo.InvariantCulture);
            if (!(text.Contains('.') || text.Contains('E') || text.Contains('e'))) text += ".0";
            return single ? text + "f" : text;
        }

        private static string CString(string s) => CString(Encoding.UTF8.GetBytes(s));

        /// <summary>A C string literal of the bytes (octal escapes for everything outside printable ASCII).</summary>
        private static string CString(byte[] bytes)
        {
            var sb = new StringBuilder("\"");
            foreach (byte b in bytes)
            {
                if (b == '"' || b == '\\') sb.Append('\\').Append((char)b);
                else if (b >= 0x20 && b < 0x7F && b != '?') sb.Append((char)b); // '?' avoids trigraphs
                else sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            }
            return sb.Append('"').ToString();
        }
    }
}
