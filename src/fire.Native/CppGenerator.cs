using System.Globalization;
using System.Text;
using fire.Bytecode;
using fire.Runtime;
using fire.Standard;
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
    /// <c>fire::Obj</c> with its fields as <c>Value</c>s. A scope that can own objects or strings/arrays gets a local
    /// <c>OwnList</c>; leaving the scope - or <c>return</c> - destroys what is on it (SPEC 2.3), a returned object moves to
    /// the caller's innermost scope. Method calls are dispatched over the classes that exist in the program (it is a closed
    /// world); field access goes through small generated helpers.
    ///
    /// Strings, arrays and byte buffers are reference counted (see fire_rt.hpp): a variable, parameter, field or array
    /// element holds a count; a fresh value is registered on its creator's scope. To keep numeric code free of that, the
    /// generator infers for every variable and stack slot whether it can hold such a value ("may be a reference") and
    /// emits counting - and the string-capable <c>addR</c> instead of <c>add</c> - only where it can.
    ///
    /// What is translated is a growing subset of the ISA (see <see cref="NativeNotSupportedException"/> for the rest):
    /// the generator never produces code with different semantics, it refuses.
    /// </summary>
    public sealed class CppGenerator
    {
        private enum FuncKind { Main, Static, Method, Ctor, Init, Dtor, Lambda }

        private sealed class Func
        {
            public required FunctionProto? Proto;
            public required Chunk Chunk;
            public required FuncKind Kind;
            public int Index;
            public string Name => Kind == FuncKind.Main ? "fire_main" : $"f{Index}";
            public bool HasSelf => Kind is FuncKind.Method or FuncKind.Ctor or FuncKind.Init or FuncKind.Dtor;
            public int ParamCount => Proto?.ParamCount ?? 0;
            /// <summary>Lambdas: the number of values captured when the lambda was created; they are variables behind the parameters.</summary>
            public int CaptureCount;
            /// <summary>Parameters plus captures: the variables of the function scope that exist on entry.</summary>
            public int ParamLike => ParamCount + CaptureCount;
            /// <summary>Scopes of this function that can own objects or reference values (they get an OwnList).</summary>
            public readonly HashSet<int> NeedsList = new();
            public string Code = "";
            public string Signature => Kind == FuncKind.Main ? "static void fire_main()"
                : $"static Value {Name}(" + string.Join(", ", (HasSelf ? new[] { "Value self" } : Kind == FuncKind.Lambda ? new[] { "Value lam" } : Array.Empty<string>()).Concat(Enumerable.Range(0, ParamCount).Select(i => $"Value P{i}"))) + ")";
            /// <summary>Lambdas are called through a pointer with a uniform signature.</summary>
            public string ThunkSignature => $"static Value {Name}_t(Value lam, const Value* a)";
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
        private readonly Dictionary<string, ClassInfo> _classes = new();
        private readonly List<ClassInfo> _classList = new();
        private readonly SortedSet<(string Name, int Argc)> _dispatchers = new();
        private readonly SortedSet<string> _fieldNames = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Class, string Field), int> _staticFields = new();

        // Which variables can hold a string/array/buffer ("may be a reference"). Only ever grows; generation is repeated
        // until nothing changes (a store in one function can change what a load in another one has to do).
        private readonly Dictionary<string, bool> _varRef = new();
        private int _version;   // bumped whenever something that earlier generated code depended on changes

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
        private Func GetFunc(FunctionProto proto, FuncKind kind, int captureCount = 0)
        {
            if (_funcByProto.TryGetValue(proto, out var existing))
            {
                if (existing.Kind != kind) throw new NativeNotSupportedException($"a function is used both as {existing.Kind} and as {kind}");
                if (existing.CaptureCount != captureCount) throw new NativeNotSupportedException("a lambda is created with different numbers of captures");
                return existing;
            }
            var func = new Func { Proto = proto, Chunk = proto.Chunk, Kind = kind, Index = _funcs.Count, CaptureCount = captureCount };
            _funcs.Add(func);
            _funcByProto[proto] = func;
            _version++;
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
            _version++;
            return info;
        }

        private int StaticFieldIndex(string className, string field)
        {
            var owner = FindClass(className).FindStaticFieldOwner(field)
                ?? throw new NativeNotSupportedException($"static field '{className}.{field}' not found");
            var key = (owner.Name, field);
            if (!_staticFields.TryGetValue(key, out int index)) { index = _staticFields.Count; _staticFields[key] = index; _version++; }
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

        private bool VarRef(string key) => _varRef.TryGetValue(key, out bool r) && r;

        private void MarkVarRef(string key)
        {
            if (VarRef(key)) return;
            _varRef[key] = true;
            _version++;
        }

        /// <summary>Key of a variable in <see cref="_varRef"/>: parameters and block locals belong to one function, globals and statics are shared.</summary>
        private static string VarKey(Func f, string name) => name[0] == 'G' || name.StartsWith("SF", StringComparison.Ordinal) ? name : f.Name + ":" + name;

        // -------------------------------------------------------------------------------------------------------------
        // Whole program
        // -------------------------------------------------------------------------------------------------------------
        private static readonly (ValueKind Kind, string Cpp)[] ExtendableKinds =
        {
            (ValueKind.String, "K_String"), (ValueKind.Char, "K_Char"), (ValueKind.Int, "K_Int"),
            (ValueKind.Float, "K_Float"), (ValueKind.Bool, "K_Bool"), (ValueKind.Array, "K_Array"),
        };

        /// <summary>The methods of a base-type extension (`class extends string`) for this name, per kind of receiver.</summary>
        private IEnumerable<(string Cpp, FunctionProto Proto)> ExtensionMethods(string name, int argc)
        {
            foreach (var (kind, cpp) in ExtendableKinds)
                if (BaseTypeExtensions.ClassNameFor(kind) is { } className
                    && _program.Program.Classes.TryGetValue(className, out var rc)
                    && rc.FindMethodWithAccess(name, argc).Proto is { } proto)
                    yield return (cpp, proto);
        }

        private bool AnyClassHasMethod(string name, int argc) => _classList.Any(c => c.Rc.FindMethodWithAccess(name, argc).Proto != null);

        private string Run()
        {
            var main = new Func { Proto = null, Chunk = _program.Program.TopLevel, Kind = FuncKind.Main };

            // Fixpoint: generating code can create classes, functions and method calls, and can show that a variable may
            // hold a reference; all of that changes what the code generated before it has to look like.
            for (int round = 0; ; round++)
            {
                int before = _version;
                GenerateChunk(main);
                for (int i = 0; i < _funcs.Count; i++) GenerateChunk(_funcs[i]);
                RegisterTargets();
                if (_version == before) break;
                if (round > 40) throw new NativeNotSupportedException("code generation does not settle");
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
                string text = _strings[i];
                bool plain = text.All(c => c >= 0x20 && c < 0x7F);
                string data = plain ? "u" + CString(text) : "{" + string.Join(", ", text.Select(c => ((int)c).ToString(CultureInfo.InvariantCulture)).Append("0")) + "}";
                sb.AppendLine($"static const char16_t K{i}_d[] = {data};");
                sb.AppendLine($"static const Str K{i} = {{{{IMMORTAL, R_Str}}, {text.Length}, K{i}_d}};");
            }
            if (_strings.Count > 0) sb.AppendLine();
            if (_globals.Count > 0)
            {
                sb.AppendLine("static Value " + string.Join(", ", _globals.OrderBy(g => g).Select(g => $"G{g} = Undef()")) + ";");
                sb.AppendLine();
            }
            foreach (var (key, index) in _staticFields.OrderBy(kv => kv.Value))
                sb.AppendLine($"static Value SF{index} = Undef(); // {key.Class}.{key.Field}");
            if (_staticFields.Count > 0) sb.AppendLine();

            // Prototypes of everything, then the helpers (they call functions), then the functions (they call helpers).
            foreach (var f in _funcs) sb.AppendLine(f.Signature + ";");
            foreach (var f in _funcs.Where(f => f.Kind == FuncKind.Lambda))
                sb.AppendLine(f.ThunkSignature + " { " + (f.ParamCount == 0 ? "(void)a; " : "") + $"return {f.Name}(lam{string.Concat(Enumerable.Range(0, f.ParamCount).Select(i => $", a[{i}]"))}); }}");
            foreach (var name in _fieldNames) sb.AppendLine($"static inline Value gf_{Mangle(name)}(Value v);").AppendLine($"static inline void sf_{Mangle(name)}(Value v, Value x);");
            foreach (var (name, argc) in _dispatchers) sb.AppendLine(DispatcherSignature(name, argc) + ";");
            sb.AppendLine("static inline Value aget_g(Value a, Value i, OwnList* list);");
            sb.AppendLine("static inline void aset_g(Value a, Value i, Value v, OwnList* list);");
            sb.AppendLine();

            foreach (var name in _fieldNames) sb.AppendLine(FieldHelpers(name));
            foreach (var (name, argc) in _dispatchers) sb.AppendLine(Dispatcher(name, argc));
            sb.AppendLine(IndexHelpers());
            sb.AppendLine(RuntimeHooks());

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

        /// <summary>Pulls in what dispatching needs: the method of every class for every called name, destructors, ToString (for
        /// text conversion), the index methods, and the enumerator class that `foreach` over an array creates.</summary>
        private void RegisterTargets()
        {
            if (_classList.Count > 0 || _dispatchers.Count > 0)
                foreach (var cls in _classList.ToList())
                    if (cls.Rc.FindMethodWithAccess("ToString", 0).Proto is { } toString && toString.ParamCount == 0)
                        GetFunc(toString, FuncKind.Method);
            if (AnyClassHasMethod("GetIndex", 1)) _dispatchers.Add(("GetIndex", 1));
            if (AnyClassHasMethod("SetIndex", 2)) _dispatchers.Add(("SetIndex", 2));
            if (_dispatchers.Contains(("GetEnumerator", 0)) && _program.Program.Classes.ContainsKey("ListEnumerator"))
            {
                var enumerator = RegisterClass("ListEnumerator");
                if (enumerator.Rc.FindConstructor(2) is { } ctor && ctor.ParamCount == 2) GetFunc(ctor, FuncKind.Ctor);
            }

            foreach (var (name, argc) in _dispatchers.ToList())
            {
                foreach (var cls in _classList.ToList())
                    if (cls.Rc.FindMethodWithAccess(name, argc).Proto is { } method)
                    {
                        if (method.ParamCount != argc) throw new NativeNotSupportedException($"default arguments in {cls.Rc.Name}.{name}");
                        GetFunc(method, FuncKind.Method);
                    }
                foreach (var (_, method) in ExtensionMethods(name, argc))
                {
                    if (method.ParamCount != argc) throw new NativeNotSupportedException($"default arguments in the extension method {name}");
                    GetFunc(method, FuncKind.Method);
                }
            }
            foreach (var cls in _classList.ToList())
                for (var rc = cls.Rc; rc != null; rc = rc.Base)
                    if (rc.Destructor != null) GetFunc(rc.Destructor, FuncKind.Dtor);
        }

        // -------------------------------------------------------------------------------------------------------------
        // Helpers generated after the fixpoint (their content depends on all classes of the program)
        // -------------------------------------------------------------------------------------------------------------
        private static string DispatcherSignature(string name, int argc)
        {
            var parameters = new List<string> { "Value self" };
            parameters.AddRange(Enumerable.Range(0, argc).Select(i => $"Value a{i}"));
            if (name == "GetEnumerator" && argc == 0) parameters.Add("OwnList* list");
            return $"static Value call_{Mangle(name)}_{argc}(" + string.Join(", ", parameters) + ")";
        }

        /// <summary>A method call: picks the implementation by the class of the receiver - or, for a string, number, array and so on,
        /// by the extension of its base type.</summary>
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
            var extensions = ExtensionMethods(name, argc).ToList();
            bool enumeratorOfArray = name == "GetEnumerator" && argc == 0 && _classes.ContainsKey("ListEnumerator");
            if (byFunc.Count == 0 && extensions.Count == 0 && !enumeratorOfArray)
                throw new NativeNotSupportedException($"method '{name}' with {argc} argument(s): no class of the program and no base-type extension defines it (the built-in methods TakeTo, TakeUpwards and TakeGlobal are not supported yet)");

            var args = string.Concat(Enumerable.Range(0, argc).Select(i => $", a{i}"));
            var sb = new StringBuilder();
            sb.AppendLine(DispatcherSignature(name, argc));
            sb.AppendLine("{");
            sb.AppendLine("    switch (self.kind) {");
            foreach (var (cpp, proto) in extensions)
                sb.AppendLine($"        case {cpp}: return {_funcByProto[proto].Name}(self{args});");
            if (enumeratorOfArray)
            {
                var info = _classes["ListEnumerator"];
                var ctor = _funcByProto[info.Rc.FindConstructor(2)!];
                sb.AppendLine("        case K_Array: case K_Buffer: {");
                sb.AppendLine("            bool ok;");
                sb.AppendLine("            Value count = lengthOf(self, &ok);");
                sb.AppendLine($"            Value e = newObject({info.Id}, {info.Fields.Count}, list);");
                sb.AppendLine($"            {ctor.Name}(e, self, count);");
                sb.AppendLine("            return e;");
                sb.AppendLine("        }");
            }
            if (byFunc.Count > 0)
            {
                sb.AppendLine("        case K_Class:");
                if (byFunc.Count == 1 && byFunc.Values.First().Count == _classList.Count)
                    sb.AppendLine($"            asObj(self); return {byFunc.Keys.First().Name}(self{args});");
                else
                {
                    sb.AppendLine("            switch (asObj(self)->cls) {");
                    foreach (var (f, ids) in byFunc)
                    {
                        foreach (int id in ids) sb.AppendLine($"                case {id}:");
                        sb.AppendLine($"                    return {f.Name}(self{args});");
                    }
                    sb.AppendLine("                default: break;");
                    sb.AppendLine("            }");
                    sb.AppendLine("            break;");
                }
            }
            sb.AppendLine("        default: break;");
            sb.AppendLine("    }");
            sb.AppendLine($"    fatal(\"Method '{name}' not found on this value.\");");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>Read and write access to the field `name`: the index of a field is the same in a class and all its subclasses,
        /// but not between unrelated classes that happen to use the same name. `Length`/`length` also work on strings, arrays and buffers.</summary>
        private string FieldHelpers(string name)
        {
            var indexByClass = new Dictionary<int, int>();
            foreach (var cls in _classList)
            {
                if (cls.Rc.FieldIndex.TryGetValue(name, out int index)) indexByClass[cls.Id] = index;
                else if (cls.Rc.FindMethodWithAccess("get_" + name, 0).Proto != null || cls.Rc.FindMethodWithAccess("set_" + name, 1).Proto != null)
                    throw new NativeNotSupportedException($"property '{name}' of class {cls.Rc.Name}");
            }
            bool isLength = name is "Length" or "length";
            if (indexByClass.Count == 0 && !isLength)
                throw new NativeNotSupportedException($"member '{name}': no class of the program has a field with this name (built-in members are not supported yet)");

            string m = Mangle(name);
            string lengthCode = isLength ? "    { bool ok; Value n = lengthOf(v, &ok); if (ok) return n; }\n" : "";
            var sb = new StringBuilder();
            if (indexByClass.Count == 0)
            {
                sb.AppendLine($"static inline Value gf_{m}(Value v) {{\n{lengthCode}    fatal(\"Field '{name}' not found on this value.\");\n}}");
                sb.AppendLine($"static inline void sf_{m}(Value v, Value x) {{ (void)v; (void)x; fatal(\"Field '{name}' cannot be assigned.\"); }}");
                return sb.ToString();
            }

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
            return $"static inline Value gf_{m}(Value v) {{\n{lengthCode}    Obj* o = asObj(v);\n{indexCode}\n    return o->fields()[idx];\n}}\n"
                 + $"static inline void sf_{m}(Value v, Value x) {{\n    Obj* o = asObj(v);\n{indexCode}\n    Value old = o->fields()[idx];\n    o->fields()[idx] = x;\n    retain(x);\n    release(old);\n}}\n";
        }

        /// <summary>`a[i]` and `a[i] = v`: arrays, buffers and strings directly, objects through their GetIndex/SetIndex methods.</summary>
        private string IndexHelpers()
        {
            var sb = new StringBuilder();
            sb.AppendLine("static inline Value aget_g(Value a, Value i, OwnList* list) {");
            if (_dispatchers.Contains(("GetIndex", 1)))
            {
                sb.AppendLine("    if (a.kind == K_Class) { Value r = call_GetIndex_1(a, i); adopt(r, list); return r; }");
            }
            else sb.AppendLine("    (void)list;");
            sb.AppendLine("    return arrayGet(a, i);");
            sb.AppendLine("}");
            sb.AppendLine("static inline void aset_g(Value a, Value i, Value v, OwnList* list) {");
            if (_dispatchers.Contains(("SetIndex", 2)))
                sb.AppendLine("    if (a.kind == K_Class) { Value r = call_SetIndex_2(a, i, v); adopt(r, list); return; }");
            else sb.AppendLine("    (void)list;");
            sb.AppendLine("    arraySet(a, i, v);");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>fire::runDestructors (destructors of the class chain, derived class first) and fire::userToString (ToString()).</summary>
        private string RuntimeHooks()
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

            sb.AppendLine("bool userToString(Value object, OwnList* list, Value* result) {");
            var toStrings = new StringBuilder();
            foreach (var cls in _classList)
                if (cls.Rc.FindMethodWithAccess("ToString", 0).Proto is { } m && m.ParamCount == 0)
                    toStrings.AppendLine($"        case {cls.Id}: *result = {_funcByProto[m].Name}(object); adopt(*result, list); return true;");
            if (toStrings.Length > 0)
            {
                sb.AppendLine("    switch (asObj(object)->cls) {");
                sb.Append(toStrings);
                sb.AppendLine("        default: break;");
                sb.AppendLine("    }");
            }
            else sb.AppendLine("    (void)object; (void)list; (void)result;");
            sb.AppendLine("    return false;");
            sb.AppendLine("}");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // -------------------------------------------------------------------------------------------------------------
        // Flow state: stack depth, the static scope chain (identity of the scope + number of declared slots) and which stack
        // slots may hold a reference value
        // -------------------------------------------------------------------------------------------------------------
        private const int FunctionScope = -1;
        private const int GlobalScope = -2;

        private sealed class Flow
        {
            public int Depth;
            public List<(int Id, int Declared)> Scopes = new();
            /// <summary>Bit k: stack slot k may hold a string/array/buffer (slots beyond 63 are assumed to).</summary>
            public ulong Refs;

            public Flow Clone() => new() { Depth = Depth, Scopes = new List<(int, int)>(Scopes), Refs = Refs };

            public bool SameShape(Flow o) => Depth == o.Depth && Scopes.SequenceEqual(o.Scopes);
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
            start.Scopes.Add(f.Kind == FuncKind.Main ? (GlobalScope, 0) : (FunctionScope, f.ParamLike));
            entry[0] = start;
            // Parameters can be anything: assume they may be references (a call-site analysis could narrow this).
            for (int i = 0; i < f.ParamLike; i++) MarkVarRef(VarKey(f, $"P{i}"));

            var targets = new HashSet<int>();
            var work = new Stack<int>();
            work.Push(0);
            int maxDepth = 0;
            var locals = new SortedSet<string>(StringComparer.Ordinal);

            void Propagate(int addr, Flow state, int from)
            {
                if (!indexOfAddr.TryGetValue(addr, out int target))
                    throw new NativeNotSupportedException($"jump to {addr} inside an instruction ({name})");
                if (entry[target] == null) { entry[target] = state.Clone(); work.Push(target); return; }
                var existing = entry[target]!;
                if (!existing.SameShape(state))
                    throw new NativeNotSupportedException($"inconsistent stack/scope state at {addr} ({name}), reached from {from}");
                if ((existing.Refs | state.Refs) != existing.Refs) { existing.Refs |= state.Refs; work.Push(target); }
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
            if (f.Kind == FuncKind.Lambda)
            {
                sb.AppendLine("    Value self = lamOn(lam); (void)self;");
                for (int i = 0; i < f.CaptureCount; i++) sb.AppendLine($"    Value P{f.ParamCount + i} = lamCapture(lam, {i});");
            }
            for (int i = 0; i < f.ParamCount; i++) sb.AppendLine($"    (void)P{i};");
            if (maxDepth > 0) sb.AppendLine("    Value " + string.Join(", ", Enumerable.Range(0, maxDepth).Select(i => $"s{i}")) + ";");
            var localsToDeclare = locals.Where(l => !(l.StartsWith("P") && int.Parse(l.AsSpan(1)) < f.ParamLike)).ToList();
            if (localsToDeclare.Count > 0) sb.AppendLine("    Value " + string.Join(", ", localsToDeclare.Select(l => l + " = Undef()")) + ";");
            if (f.NeedsList.Count > 0) sb.AppendLine("    OwnList " + string.Join(", ", f.NeedsList.OrderBy(i => i).Select(i => ListName(i) + (i < 0 ? " = {nullptr, nullptr, poolMark()}" : " = {nullptr, nullptr, 0}"))) + ";");
            // Parameters are variables: they hold what was passed.
            for (int i = 0; i < f.ParamLike; i++)
                if (VarRef(VarKey(f, $"P{i}"))) sb.AppendLine($"    retain(P{i});");

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

        /// <summary>The variables that belong to a scope of the function (parameters and base-scope locals for the function scope).</summary>
        private static IEnumerable<string> ScopeVariables(Func f, int scopeId, ISet<string> locals)
        {
            if (scopeId == GlobalScope) return Enumerable.Empty<string>();
            if (scopeId == FunctionScope)
                return Enumerable.Range(0, f.ParamLike).Select(i => $"P{i}").Concat(locals.Where(l => l.StartsWith("P") && int.Parse(l.AsSpan(1)) >= f.ParamLike));
            return locals.Where(l => l.StartsWith($"B{scopeId}_", StringComparison.Ordinal));
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
            void RequireSelf() { if (!f.HasSelf && f.Kind != FuncKind.Lambda) throw new NativeNotSupportedException($"'this' outside of an instance member ({fn} at {ins.Addr})"); }
            string Args(int first, int count) => string.Concat(Enumerable.Range(first, count).Select(i => ", " + S(i)));
            bool R(int k) => k >= 64 || (st.Refs >> k & 1UL) != 0;
            void SetR(int k, bool value)
            {
                if (k >= 64) return;
                if (value) st.Refs |= 1UL << k; else st.Refs &= ~(1UL << k);
            }
            // After a call: whatever came back belongs to the innermost scope now.
            void AdoptResult(int slot) { E($"adopt({S(slot)}, &{OwnerList()});"); SetR(slot, true); }
            // Stores into a variable: count what it now holds, let go of what it held.
            void StoreVar(string variable, int slot)
            {
                string key = VarKey(f, variable);
                bool valueRef = R(slot);
                if (valueRef) MarkVarRef(key);
                bool varRef = VarRef(key);
                if (valueRef) E($"{{ Value n = {S(slot)}; Value o = {variable}; {variable} = n; retain(n); release(o); }}");
                else if (varRef) E($"{{ Value o = {variable}; {variable} = {S(slot)}; release(o); }}");
                else E($"{variable} = {S(slot)};");
            }
            // The variables of a scope let go of their values when the scope is left.
            void ReleaseScopeVariables(int scopeId, bool reset)
            {
                foreach (var v in ScopeVariables(f, scopeId, locals))
                    if (VarRef(VarKey(f, v))) E(reset ? $"release({v}); {v} = Undef();" : $"release({v});");
            }

            switch (ins.Op)
            {
                case OpCode.LoadConst:
                {
                    var c = chunk.Constants[ins.A[0]];
                    E($"{S(d)} = {Constant(c)};");
                    SetR(d, c.Kind == ValueKind.String);
                    d++; return Next();
                }
                case OpCode.Pop:
                    Need(1); d--; return Next();
                case OpCode.Dup:
                    Need(1); E($"{S(d)} = {S(d - 1)};"); SetR(d, R(d - 1)); d++; return Next();
                case OpCode.Swap:
                {
                    Need(2); E($"{{ Value t = {S(d - 1)}; {S(d - 1)} = {S(d - 2)}; {S(d - 2)} = t; }}");
                    bool a = R(d - 1), b = R(d - 2); SetR(d - 1, b); SetR(d - 2, a);
                    return Next();
                }

                case OpCode.DeclareLocal:
                {
                    Need(1);
                    var (id, declared) = st.Scopes[^1];
                    string v;
                    if (id == GlobalScope) { _globals.Add(declared); v = $"G{declared}"; }
                    else
                    {
                        v = id == FunctionScope ? $"P{declared}" : $"B{id}_{declared}";
                        locals.Add(v);
                    }
                    string key = VarKey(f, v);
                    if (R(d - 1)) { MarkVarRef(key); E($"{v} = {S(d - 1)}; retain({v});"); }
                    else E($"{v} = {S(d - 1)};");
                    st.Scopes[^1] = (id, declared + 1);
                    d--; return Next();
                }
                case OpCode.LoadLocal:
                {
                    string v = Var(st, ins.A[0], ins.A[1], locals);
                    E($"{S(d)} = {v};");
                    SetR(d, VarRef(VarKey(f, v)));
                    d++; return Next();
                }
                case OpCode.StoreLocal:
                    Need(1); StoreVar(Var(st, ins.A[0], ins.A[1], locals), d - 1); return Next();
                case OpCode.StoreLocalPop:
                    Need(1); StoreVar(Var(st, ins.A[0], ins.A[1], locals), d - 1); d--; return Next();
                case OpCode.LoadGlobal:
                    _globals.Add(ins.A[0]); E($"{S(d)} = G{ins.A[0]};"); SetR(d, VarRef($"G{ins.A[0]}")); d++; return Next();
                case OpCode.StoreGlobal:
                    Need(1); _globals.Add(ins.A[0]); StoreVar($"G{ins.A[0]}", d - 1); return Next();
                case OpCode.StoreGlobalPop:
                    Need(1); _globals.Add(ins.A[0]); StoreVar($"G{ins.A[0]}", d - 1); d--; return Next();
                case OpCode.ArithLocalConstPop:
                case OpCode.ArithGlobalConstPop:
                {
                    bool local = ins.Op == OpCode.ArithLocalConstPop;
                    string v;
                    Value constant;
                    bool subtract;
                    if (local) { v = Var(st, ins.A[0], ins.A[1], locals); constant = chunk.Constants[ins.A[2]]; subtract = ins.A[3] != 0; }
                    else { _globals.Add(ins.A[0]); v = $"G{ins.A[0]}"; constant = chunk.Constants[ins.A[1]]; subtract = ins.A[2] != 0; }
                    string key = VarKey(f, v);
                    if (constant.Kind == ValueKind.String) MarkVarRef(key);
                    string k = Constant(constant);
                    if (!subtract && VarRef(key))
                        E($"{{ Value o = {v}; {v} = addR(o, {k}, &{OwnerList()}); retain({v}); release(o); }}");
                    else
                        E($"{v} = {(subtract ? "sub" : "add")}({v}, {k});");
                    return Next();
                }

                case OpCode.Add:
                {
                    Need(2);
                    if (R(d - 2) || R(d - 1))
                    {
                        E($"{S(d - 2)} = addR({S(d - 2)}, {S(d - 1)}, &{OwnerList()});");
                        d--; SetR(d - 1, true);
                    }
                    else { E($"{S(d - 2)} = add({S(d - 2)}, {S(d - 1)});"); d--; SetR(d - 1, false); }
                    return Next();
                }
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
                    if (f.NeedsList.Contains(ins.Addr)) E($"{ListName(ins.Addr)}.mark = poolMark();");
                    st.Scopes.Add((ins.Addr, 0)); return Next();
                case OpCode.ExitScope:
                {
                    if (st.Scopes.Count <= 1) throw new NativeNotSupportedException($"ExitScope without scope at {ins.Addr} in {fn}");
                    int id = st.Scopes[^1].Id;
                    if (f.NeedsList.Contains(id)) E($"leave(&{ListName(id)});");
                    ReleaseScopeVariables(id, reset: true);
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
                        E($"{S(d - 1)} = print({S(d - 1)}, &{OwnerList()});");
                        SetR(d - 1, false);
                        return Next();
                    }
                    if (native == StringMethods.NativeName && argc is >= 2 and <= 4)
                    {
                        int first = d - argc;
                        string a0 = argc > 2 ? S(first + 2) : "Undef()", a1 = argc > 3 ? S(first + 3) : "Undef()";
                        E($"{S(first)} = stringCall({S(first)}.i, {S(first + 1)}, {argc - 2}, {a0}, {a1}, &{OwnerList()});");
                        d = first + 1; SetR(first, true); return Next();
                    }
                    if (native == CharMethods.NativeName && argc == 2)
                    {
                        int first = d - 2;
                        E($"{S(first)} = charCall({S(first)}.i, {S(first + 1)}, &{OwnerList()});");
                        d = first + 1; SetR(first, true); return Next();
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
                    E($"{S(d - argc)} = {target.Name}({string.Join(", ", Enumerable.Range(d - argc, argc).Select(S))});");
                    AdoptResult(d - argc);
                    d = d - argc + 1; return Next();
                }
                case OpCode.Return:
                {
                    Need(1);
                    if (f.Kind == FuncKind.Main) throw new NativeNotSupportedException("return in top-level code");
                    var chain = st.Scopes.AsEnumerable().Reverse().ToList();   // innermost first
                    bool isCtor = f.Kind == FuncKind.Ctor;
                    bool anything = chain.Any(sc => f.NeedsList.Contains(sc.Id) || ScopeVariables(f, sc.Id, locals).Any(v => VarRef(VarKey(f, v))));
                    if (!isCtor && (R(d - 1) || anything)) E($"{{ Value r = {S(d - 1)};");
                    else if (!isCtor) { E($"return {S(d - 1)};"); return new StepResult(false, null); }
                    else E("{");
                    // the value is on its way to the caller: it keeps a count, and an object that the leaving scopes owned goes along
                    if (!isCtor && R(d - 1)) E("  retain(r);");
                    if (!isCtor)
                        foreach (var sc in chain)
                            if (f.NeedsList.Contains(sc.Id)) E($"  transferOut(r, &{ListName(sc.Id)});");
                    foreach (var sc in chain)
                    {
                        if (f.NeedsList.Contains(sc.Id)) E($"  leave(&{ListName(sc.Id)});");
                        foreach (var v in ScopeVariables(f, sc.Id, locals))
                            if (VarRef(VarKey(f, v))) E($"  release({v});");
                    }
                    E(isCtor ? "  return self; }" : "  return r; }");
                    return new StepResult(false, null);
                }
                case OpCode.Halt:
                    if (f.Kind != FuncKind.Main) throw new NativeNotSupportedException("Halt in a function");
                    // the end of the program: the global scope is released like any other (destructors run), then what the globals hold
                    if (f.NeedsList.Contains(GlobalScope)) E("leave(&OG);");
                    foreach (var g in _globals.OrderBy(x => x))
                        if (VarRef($"G{g}")) E($"release(G{g});");
                    foreach (var (_, index) in _staticFields.OrderBy(kv => kv.Value))
                        if (VarRef($"SF{index}")) E($"release(SF{index});");
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
                    RequireSelf(); E($"{S(d)} = self;"); SetR(d, false); d++; return Next();
                case OpCode.GetField:
                {
                    Need(1);
                    string field = Str(ins.A[0]);
                    _fieldNames.Add(field);
                    E($"{S(d - 1)} = gf_{Mangle(field)}({S(d - 1)});");
                    SetR(d - 1, true);
                    return Next();
                }
                case OpCode.SetField:
                {
                    Need(2);
                    string field = Str(ins.A[0]);
                    _fieldNames.Add(field);
                    E($"sf_{Mangle(field)}({S(d - 2)}, {S(d - 1)});");
                    E($"{S(d - 2)} = {S(d - 1)};");
                    SetR(d - 2, R(d - 1));
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
                    SetR(slot, false);
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
                    SetR(d - argc, false);
                    d = d - argc + 1; return Next();
                }
                case OpCode.CallProtoWithThis:
                {
                    int argc = ins.A[1];
                    Need(argc + 1);
                    var proto = chunk.Functions[ins.A[0]];
                    if (proto.ParamCount != argc) throw new NativeNotSupportedException($"initializer with {proto.ParamCount} parameters called with {argc}");
                    var target = GetFunc(proto, FuncKind.Init);
                    int slot = d - argc - 1;
                    E($"{S(slot)} = {target.Name}({S(slot)}{Args(d - argc, argc)});");
                    AdoptResult(slot);
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
                    string extra = method == "GetEnumerator" && argc == 0 ? $", &{owner}" : "";
                    E($"{S(slot)} = call_{Mangle(method)}_{argc}({S(slot)}{Args(d - argc, argc)}{extra});");
                    AdoptResult(slot);
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
                    E($"{S(d - argc)} = {target.Name}(self{Args(d - argc, argc)});");
                    AdoptResult(d - argc);
                    d = d - argc + 1; return Next();
                }
                case OpCode.GetStaticField:
                {
                    int index = StaticFieldIndex(Str(ins.A[0]), Str(ins.A[1]));
                    E($"{S(d)} = SF{index};");
                    SetR(d, VarRef($"SF{index}"));
                    d++; return Next();
                }
                case OpCode.SetStaticField:
                case OpCode.SetStaticFieldOnInit:
                {
                    Need(1);
                    int index = StaticFieldIndex(Str(ins.A[0]), Str(ins.A[1]));
                    StoreVar($"SF{index}", d - 1);
                    return Next();
                }

                // ---------------------------------------------------------------------------------------------------
                // Lambdas (capture by value, SPEC 4.2.1)
                // ---------------------------------------------------------------------------------------------------
                case OpCode.MakeLambda:
                case OpCode.MakeLambdaCapturing:
                {
                    bool capturing = ins.Op == OpCode.MakeLambdaCapturing;
                    bool hasOn = ins.A[1] != 0;
                    int captures = capturing ? ins.A[2] : 0;
                    Need(captures + (hasOn ? 1 : 0));
                    var proto = chunk.Functions[ins.A[0]];
                    if (proto.ParamDefaults.Any(p => p != null)) throw new NativeNotSupportedException("a lambda with default parameter values");
                    var target = GetFunc(proto, FuncKind.Lambda, captures);
                    int first = d - captures - (hasOn ? 1 : 0);
                    var sbCaps = new StringBuilder();
                    for (int i = 0; i < captures; i++) sbCaps.Append($" l->caps()[{i}] = {S(first + i)}; retain({S(first + i)});");
                    E($"{{ Lam* l = allocLam({proto.ParamCount}, {captures}, {target.Name}_t, &{OwnerList()}); l->on = {(hasOn ? S(d - 1) : "Undef()")};{sbCaps} {S(first)} = LamV(l); }}");
                    d = first + 1; SetR(first, true); return Next();
                }
                case OpCode.Call:
                {
                    int argc = ins.A[0];
                    Need(argc + 1);
                    int slot = d - argc - 1;
                    string owner = OwnerList();
                    var argList = argc == 0 ? "Undef()" : string.Join(", ", Enumerable.Range(slot + 1, argc).Select(S));
                    E($"{{ Value args[{Math.Max(argc, 1)}] = {{{argList}}}; {S(slot)} = callLam({S(slot)}, {argc}, args); }}");
                    AdoptResult(slot);
                    d = slot + 1; return Next();
                }
                case OpCode.CheckLambdaSignature:
                    Need(1); E($"checkLambda({S(d - 1)}, {ins.A[0]});"); return Next();
                case OpCode.CheckUnit:
                {
                    Need(1);
                    string unitText = Str(ins.A[0]);
                    E($"checkUnit({S(d - 1)}, {UnitId(Unit.Parse(unitText))});");
                    return Next();
                }

                // ---------------------------------------------------------------------------------------------------
                // Strings, arrays, buffers
                // ---------------------------------------------------------------------------------------------------
                case OpCode.FormatValue:
                {
                    Need(1);
                    string spec = Str(ins.A[0]);
                    if (spec.Length > 0 && "XDBFxdbf".IndexOf(spec[0]) < 0)
                        throw new NativeNotSupportedException($"format specifier '{spec}' (supported: X, D, B, F)");
                    E($"{S(d - 1)} = formatValue({S(d - 1)}, {Constant(chunk.Constants[ins.A[0]])}, &{OwnerList()});");
                    SetR(d - 1, true);
                    return Next();
                }
                case OpCode.NewArray:
                    Need(1); E($"{S(d - 1)} = newArray({S(d - 1)}, &{OwnerList()});"); SetR(d - 1, true); return Next();
                case OpCode.MakeBuffer:
                    Need(1); E($"{S(d - 1)} = newBuffer({S(d - 1)}, &{OwnerList()});"); SetR(d - 1, true); return Next();
                case OpCode.MakeArrayLiteral:
                {
                    int count = ins.A[0];
                    Need(count);
                    int first = d - count;
                    var sbItems = new StringBuilder();
                    for (int i = 0; i < count; i++) sbItems.Append($" a->items()[{i}] = {S(first + i)}; retain({S(first + i)});");
                    E($"{{ Arr* a = allocArr({count}, &{OwnerList()});{sbItems} {S(first)} = ArrV(a); }}");
                    d = first + 1; SetR(first, true); return Next();
                }
                case OpCode.ArrayGet:
                    Need(2); E($"{S(d - 2)} = aget_g({S(d - 2)}, {S(d - 1)}, &{OwnerList()});"); d--; SetR(d - 1, true); return Next();
                case OpCode.ArraySet:
                    Need(3); E($"aset_g({S(d - 3)}, {S(d - 2)}, {S(d - 1)}, &{OwnerList()});"); E($"{S(d - 3)} = {S(d - 1)};"); SetR(d - 3, R(d - 1)); d -= 2; return Next();
                case OpCode.IncDecIndex:
                {
                    Need(2);
                    bool increment = ins.A[0] != 0, prefix = ins.A[1] != 0;
                    string list = "&" + OwnerList();
                    E($"{{ Value o = aget_g({S(d - 2)}, {S(d - 1)}, {list}); Value n = {(increment ? "add" : "sub")}(o, Int(1)); aset_g({S(d - 2)}, {S(d - 1)}, n, {list}); {S(d - 2)} = {(prefix ? "n" : "o")}; }}");
                    d--; SetR(d - 1, false); return Next();
                }

                default:
                    throw new NativeNotSupportedException($"opcode {ins.Op} (in {fn} at {ins.Addr})");
            }

            StepResult Binary(string fnName)
            {
                Need(2); E($"{S(d - 2)} = {fnName}({S(d - 2)}, {S(d - 1)});"); d--; SetR(d - 1, false); return Next();
            }
            StepResult Unary(string fnName)
            {
                Need(1); E($"{S(d - 1)} = {fnName}({S(d - 1)});"); SetR(d - 1, false); return Next();
            }
            StepResult Compare(string fnName, bool negate)
            {
                Need(2); E($"{S(d - 2)} = Bool({(negate ? "!" : "")}{fnName}({S(d - 2)}, {S(d - 1)}));"); d--; SetR(d - 1, false); return Next();
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
                    if (!_stringIndex.TryGetValue(s, out int index)) { index = _strings.Count; _strings.Add(s); _stringIndex[s] = index; _version++; }
                    return $"StrV(&K{index})";
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
