using System.Globalization;
using System.Text;
using fire.Ast;
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
    public sealed partial class CppGenerator
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
            /// <summary>A call passes the result of another call on (`f(g())`): the function has the argument list `AL` (see finishArgs in fire_rt.hpp).</summary>
            public bool NeedsArgList;
            public string Code = "";
            // Per generation of the chunk (see GenerateChunk): the catch regions, the finally bookkeeping and the decoded code.
            public Dictionary<int, Region> Regions = new();
            public readonly HashSet<int> FinReturn = new();                      // finally blocks that a `return` can run through
            public readonly Dictionary<int, SortedSet<int>> FinJumps = new();    // finally block -> the jump targets that `break`/`continue` leave through it
            public List<Instr> Decoded = new();
            public Dictionary<int, int> IndexOf = new();
            public Flow?[] Entry = Array.Empty<Flow?>();
            public Out Root = new();
            public int FinSignature => FinReturn.Count + FinJumps.Values.Sum(v => v.Count);
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
        /// <summary>Operators (method names `operator+`, ...) that the program overloads and that an operation of it uses: each gets a wrapper `ov_...` (SPEC 5.11).</summary>
        private readonly SortedSet<string> _operators = new();
        /// <summary>Class and interface names tested with `is of`: each gets a function `isof_...`.</summary>
        private readonly SortedSet<string> _isOfTypes = new();
        /// <summary>The member chains of selector lambdas (`c => c.radius`), each a static array.</summary>
        private readonly Dictionary<string[], string> _selectorPaths = new(new SelectorPathComparer());
        private sealed class SelectorPathComparer : IEqualityComparer<string[]>
        {
            public bool Equals(string[]? a, string[]? b) => a != null && b != null && a.SequenceEqual(b);
            public int GetHashCode(string[] p) => string.Join("\u0001", p).GetHashCode();
        }

        /// <summary>What the operator wrapper does when the left operand is not an object with that overload: the built-in operation.</summary>
        private static readonly Dictionary<string, string> OperatorFallback = new()
        {
            ["operator+"] = "addR(a, b, list)", ["operator-"] = "sub(a, b)", ["operator*"] = "mul(a, b)", ["operator/"] = "divide(a, b)", ["operator%"] = "modulo(a, b)",
            ["operator^"] = "power(a, b)", ["operator&"] = "bitAnd(a, b)", ["operator|"] = "bitOr(a, b)", ["operator#"] = "bitXor(a, b)", ["operator<<"] = "shl(a, b)", ["operator>>"] = "shr(a, b)",
            ["operator=="] = "Bool(eq(a, b))", ["operator!="] = "Bool(!eq(a, b))", ["operator<"] = "Bool(lt(a, b))", ["operator<="] = "Bool(le(a, b))",
            ["operator>"] = "Bool(gt(a, b))", ["operator>="] = "Bool(ge(a, b))",
        };

        private static string OperatorWrapper(string method) => "ov_" + Mangle(method.Substring("operator".Length));

        /// <summary>Does the program overload this operator in some class? Then the operation goes through the wrapper (and the dispatcher of the method).</summary>
        private bool UseOperator(string method)
        {
            if (!_program.Program.Classes.Values.Any(rc => rc.Methods.ContainsKey(method))) return false;
            _operators.Add(method);
            _dispatchers.Add((method, 1));
            return true;
        }
        private readonly SortedSet<string> _fieldNames = new(StringComparer.Ordinal);
        private readonly Dictionary<(string Class, string Field), int> _staticFields = new();

        // Which variables can hold a string/array/buffer ("may be a reference"). Only ever grows; generation is repeated
        // until nothing changes (a store in one function can change what a load in another one has to do).
        private readonly Dictionary<string, bool> _varRef = new();
        private int _version;   // bumped whenever something that earlier generated code depended on changes
        /// <summary>The program throws or catches exceptions: calls are followed by a check of the unwinding flag, and the exception hooks exist.</summary>
        private bool _usesExceptions;
        private readonly List<string> _excTypes = new();
        /// <summary>The program uses TakeUpwards: every scope then knows its parent scope (and has an owner list).</summary>
        private bool _usesTakeUpwards;
        /// <summary>Something needs the list of the global scope from other functions (`g_globalOwn`): exceptions, TakeGlobal, TakeUpwards.</summary>
        private bool _usesGlobalOwn;
        /// <summary>Methods (name, number of arguments) that a call site passes an address to (`ref` parameters): their dispatcher hands the value to the implementations without `ref`.</summary>
        private readonly HashSet<(string Name, int Argc)> _refCallSites = new();
        /// <summary>Lambdas with default parameter values: each gets a function `fN_d(lam, index)` that evaluates the default of a parameter.</summary>
        private readonly HashSet<Func> _lambdaDefaults = new();   // catch types, as numbered in the HandlerInfo tables

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

        // ---- Access modifiers (SPEC 5.7): checked in Debug and Release, not in Performance ----------------------------------------------
        private bool CheckAccess => _program.ExecutionMode != VmExecutionMode.Performance;
        private static string DescribeAccess(AccessModifier access) => access switch { AccessModifier.Private => "private", AccessModifier.Protected => "protected", _ => "public" };

        /// <summary>May code of the class <paramref name="caller"/> (null: top-level code) use a member that <paramref name="declaring"/> declares with this access?</summary>
        private static bool AccessAllowed(RuntimeClass? caller, RuntimeClass declaring, AccessModifier access)
        {
            if (access == AccessModifier.Public) return true;
            if (caller == null) return false;
            // classes are compared by name: the linked program need not keep one object per class
            if (access == AccessModifier.Private) return caller.Name == declaring.Name;
            for (var rc = caller; rc != null; rc = rc.Base) if (rc.Name == declaring.Name) return true;
            return false;
        }

        /// <summary>The class of the code of this function as a number for the checks at run time (no class: all ones).</summary>
        private string CallerId(Func f) => f.Chunk.OwnerClass is { } owner && CheckAccess ? RegisterClass(owner.Name).Id.ToString(CultureInfo.InvariantCulture) : "0xFFFFFFFFu";

        private bool RestrictedMethodName(string name) =>
            CheckAccess && _program.Program.Classes.Values.Any(rc => rc.Methods.TryGetValue(name, out var list) && list.Any(m => m.Access is { } a && a != AccessModifier.Public));

        /// <summary>Is some field (or property accessor) of this name private or protected somewhere? Then the field helpers check the class of the caller.</summary>
        private bool RestrictedField(string name) =>
            CheckAccess && (_program.Program.Classes.Values.Any(rc => rc.OwnFieldInfo.TryGetValue(name, out var info) && info.AccessModifier != AccessModifier.Public)
                || RestrictedMethodName("get_" + name) || RestrictedMethodName("set_" + name));

        /// <summary>The condition (over `caller`) under which the member may be used, empty when everybody may.</summary>
        private string AllowedExpr(RuntimeClass declaring, AccessModifier access)
        {
            if (access == AccessModifier.Public) return "";
            var ids = _classList.Where(c => AccessAllowed(c.Rc, declaring, access)).Select(c => c.Id).ToList();
            return ids.Count == 0 ? "false" : "(" + string.Join(" || ", ids.Select(id => $"caller == {id}")) + ")";
        }

        /// <summary>The extra arguments of a field helper: the scope list when a class has a property of that name, the caller's class when the name is restricted.</summary>
        private string FieldTail(string name, Func f, System.Func<string> list) =>
            (HasProperty(name) ? ", " + list() : "") + (RestrictedField(name) ? ", " + CallerId(f) : "");

        private HashSet<FunctionProto>? _extensionProtos;
        /// <summary>Is the function a method of a base-type extension (`class extends string`)? Its `this` is then a value that can be a string or an array (reference counted).</summary>
        private bool IsExtensionMethod(FunctionProto? proto)
        {
            if (proto == null) return false;
            if (_extensionProtos == null)
            {
                _extensionProtos = new HashSet<FunctionProto>();
                foreach (var (kind, _) in ExtendableKinds)
                    if (BaseTypeExtensions.ClassNameFor(kind) is { } className && _program.Program.Classes.TryGetValue(className, out var rc))
                        foreach (var list in rc.Methods.Values) foreach (var m in list) _extensionProtos.Add(m);
            }
            return _extensionProtos.Contains(proto);
        }

        /// <summary>The methods of a base-type extension (`class extends string`) for this name, per kind of receiver.</summary>
        private IEnumerable<(string Cpp, FunctionProto Proto, RuntimeClass Rc)> ExtensionMethods(string name, int argc)
        {
            foreach (var (kind, cpp) in ExtendableKinds)
                if (BaseTypeExtensions.ClassNameFor(kind) is { } className
                    && _program.Program.Classes.TryGetValue(className, out var rc)
                    && rc.FindMethodWithAccess(name, argc).Proto is { } proto)
                    yield return (cpp, proto, rc);
        }

        /// <summary>Is the class <paramref name="rc"/>, a base class of it or an interface of it called <paramref name="name"/>? (what a typed `catch` matches)</summary>
        private static bool ClassIs(RuntimeClass rc, string name)
        {
            for (var c = rc; c != null; c = c.Base)
                if (c.Name == name || c.Interfaces.Contains(name)) return true;
            return false;
        }

        /// <summary>The runtime enum of a built-in ownership method (SPEC 2.2), or null.</summary>
        private static string? OwnMethodId(string name, int argc) => (name, argc) switch
        {
            ("Take", 0) => "OM_Take",
            ("TakeUpwards", 0) => "OM_TakeUpwards",
            ("TakeGlobal", 0) => "OM_TakeGlobal",
            ("TakeTo", 1) => "OM_TakeTo",
            _ => null,
        };

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
            if (_usesExceptions) sb.AppendLine("#define FIRE_EXCEPTIONS 1");
            if (_usesReflection) sb.AppendLine("#define FIRE_REFLECTION 1");
            if (_program.ExecutionMode == VmExecutionMode.Performance) sb.AppendLine("#define FIRE_UNCHECKED 1 // #performance: destroyed arrays and buffers are not detected");
            if (_program.FloatWidth == 32) sb.AppendLine("#define FIRE_FLOAT32 1 // #floatwidth 32: float is a 32-bit float, like in the VM");
            // The unit table: the base symbols of all units of the program (sorted like the VM prints them) and one row per unit.
            var dimNames = _units.SelectMany(u => u.Dimensions.Keys).Distinct().OrderBy(k => k).ToList();
            sb.AppendLine($"#define FIRE_NDIMS {Math.Max(1, dimNames.Count)}");
            sb.AppendLine("#include \"fire_rt.hpp\"");
            sb.AppendLine("using namespace fire;");
            sb.AppendLine();
            sb.AppendLine("namespace fire {");
            sb.AppendLine($"const char* const g_dimNames[] = {{{(dimNames.Count == 0 ? "\"\"" : string.Join(", ", dimNames.Select(CString)))}}};");
            sb.AppendLine("}");
            sb.AppendLine("static const UnitInit kUnitInit[] = {");
            foreach (var u in _units)
            {
                var dims = dimNames.Count == 0 ? "0" : string.Join(", ", dimNames.Select(n => u.Dimensions.TryGetValue(n, out int e) ? e.ToString(CultureInfo.InvariantCulture) : "0"));
                string display = u.DisplaySymbol is { } symbol ? CString(symbol) : "nullptr";
                sb.AppendLine($"    {{{{{dims}}}, {DoubleLiteral(u.Scale, false)}, {display}}},");
            }
            sb.AppendLine("};");
            sb.AppendLine("static const bool kUnitsReady = (unitsInit(kUnitInit, " + _units.Count + "), true);");
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
            foreach (var f in _lambdaDefaults)
            {
                sb.AppendLine($"static Value {f.Name}_d(Value lam, uint32_t i) {{");
                sb.AppendLine("    switch (i) {");
                for (int i = 0; i < f.Proto!.ParamDefaults.Count; i++)
                    if (f.Proto.ParamDefaults[i] is { } dp) sb.AppendLine($"        case {i}: return {_funcByProto[dp].Name}(lamOn(lam));");
                sb.AppendLine("        default: return Undef();");
                sb.AppendLine("    }");
                sb.AppendLine("}");
            }
            foreach (var name in _fieldNames)
            {
                string lp = (HasProperty(name) ? ", OwnList* list" : "") + (RestrictedField(name) ? ", uint32_t caller" : "");
                sb.AppendLine($"[[maybe_unused]] static inline Value gf_{Mangle(name)}(Value v{lp});").AppendLine($"[[maybe_unused]] static inline void sf_{Mangle(name)}(Value v, Value x{lp});").AppendLine($"[[maybe_unused]] static inline Value fp_{Mangle(name)}(Value v);");
            }
            foreach (var (name, argc) in _dispatchers) sb.AppendLine(DispatcherSignature(name, argc) + ";");
            foreach (var method in _operators) sb.AppendLine($"static Value {OperatorWrapper(method)}(Value a, Value b, OwnList* list);");
            sb.AppendLine("[[maybe_unused]] static inline Value aget_g(Value a, Value i, OwnList* list);");
            sb.AppendLine("[[maybe_unused]] static inline void aset_g(Value a, Value i, Value v, OwnList* list);");
            sb.AppendLine();

            foreach (var name in _fieldNames) sb.AppendLine(FieldHelpers(name));
            foreach (var (name, argc) in _dispatchers) sb.AppendLine(Dispatcher(name, argc));
            foreach (var method in _operators)
            {
                var ids = _classList.Where(c => c.Rc.FindMethodWithAccess(method, 1).Proto != null).Select(c => c.Id).ToList();
                sb.AppendLine($"static Value {OperatorWrapper(method)}(Value a, Value b, OwnList* list) {{");
                if (ids.Count > 0)
                {
                    sb.AppendLine("    if (a.kind == K_Class) {");
                    sb.AppendLine("        switch (asObj(a)->cls) {");
                    sb.AppendLine("            " + string.Concat(ids.Select(id => $"case {id}: ")) + $"{{ Value r = call_{Mangle(method)}_1(a, b{DispatchTail(method, 1, "list", "0xFFFFFFFFu")}); adopt(r, list); return r; }}");
                    sb.AppendLine("            default: break;");
                    sb.AppendLine("        }");
                    sb.AppendLine("    }");
                }
                sb.AppendLine($"    return {OperatorFallback[method]};");
                sb.AppendLine("}");
            }
            foreach (var typeName in _isOfTypes)
            {
                var ids = _classList.Where(c => ClassIs(c.Rc, typeName)).Select(c => c.Id).ToList();
                sb.AppendLine($"static bool isof_{Mangle(typeName)}(Value v) {{");
                if (typeName == "IEnumerable") sb.AppendLine("    if (v.kind == K_Array || v.kind == K_Buffer) return true;");
                sb.AppendLine("    if (v.kind != K_Class) return false;");
                sb.AppendLine(ids.Count == 0 ? "    return false;" : "    switch (asObj(v)->cls) { " + string.Concat(ids.Select(id => $"case {id}: ")) + "return true; default: return false; }");
                sb.AppendLine("}");
            }
            if (_usesReflection)
            {
                foreach (var (path, name) in _selectorPaths) sb.AppendLine($"static const char* const {name}[] = {{{string.Join(", ", path.Select(CString))}}};");
                sb.AppendLine(ReflectionCode());
            }
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
                sb.AppendLine(_usesExceptions ? "    return g_unwind.active ? 1 : 0;   // an exception that nothing caught" : "    return 0;");
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
            if (_usesExceptions && _program.Program.Classes.ContainsKey("IndexOutOfBoundsException"))
            {
                // run-time errors (index out of range) are thrown as this prelude class
                var indexError = RegisterClass("IndexOutOfBoundsException");
                if (indexError.Rc.FindConstructor(3) is { } indexCtor && indexCtor.ParamCount == 3) GetFunc(indexCtor, FuncKind.Ctor);
                var destroyedError = RegisterClass("DestroyedException");
                if (destroyedError.Rc.FindConstructor(1) is { } destroyedCtor && destroyedCtor.ParamCount == 1) GetFunc(destroyedCtor, FuncKind.Ctor);
                var unitError = RegisterClass("UnitMismatchException");
                if (unitError.Rc.FindConstructor(3) is { } unitCtor && unitCtor.ParamCount == 3) GetFunc(unitCtor, FuncKind.Ctor);
                if (_usesReflection && _program.Program.Classes.ContainsKey("ReflectionException"))
                {
                    var reflectError = RegisterClass("ReflectionException");
                    if (reflectError.Rc.FindConstructor(1) is { } reflectCtor && reflectCtor.ParamCount == 1) GetFunc(reflectCtor, FuncKind.Ctor);
                }
                var accessError = RegisterClass("AccessDeniedException");
                if (accessError.Rc.FindConstructor(1) is { } accessCtor && accessCtor.ParamCount == 1) GetFunc(accessCtor, FuncKind.Ctor);
            }
            foreach (var field in _fieldNames.ToList())
                foreach (var cls in _classList.ToList())
                    if (cls.Rc.FieldIndex.ContainsKey(field) && cls.Rc.FindFieldRequiredUnit(field) is { } requiredUnit)
                    {
                        UnitId(Unit.Parse(requiredUnit));   // the unit and its text for the check in the field setter
                        Constant(Value.MakeString(requiredUnit));
                    }
            foreach (var field in _fieldNames.ToList())
                foreach (var cls in _classList.ToList())
                {
                    if (cls.Rc.FieldIndex.ContainsKey(field)) continue;
                    if (cls.Rc.FindMethodWithAccess("get_" + field, 0).Proto is { } getter) GetFunc(getter, FuncKind.Method);
                    if (cls.Rc.FindMethodWithAccess("set_" + field, 1).Proto is { } setter) GetFunc(setter, FuncKind.Method);
                }
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
                        GetFunc(method, FuncKind.Method);
                        DefaultArgs(method, argc, "self", "list");   // registers the functions of the default values
                    }
                foreach (var (_, method, _) in ExtensionMethods(name, argc))
                {
                    GetFunc(method, FuncKind.Method);
                    DefaultArgs(method, argc, "self", "list");
                }
            }
            RegisterReflectionTargets();
            foreach (var cls in _classList.ToList())
                for (var rc = cls.Rc; rc != null; rc = rc.Base)
                    if (rc.Destructor != null) GetFunc(rc.Destructor, FuncKind.Dtor);
        }

        // -------------------------------------------------------------------------------------------------------------
        // Helpers generated after the fixpoint (their content depends on all classes of the program)
        // -------------------------------------------------------------------------------------------------------------
        /// <summary>The values for the parameters beyond the `argc` that a call supplies (SPEC 5.2): each default value is a function of its own
        /// (it sees `this` of the callee, `self`); the fresh value belongs to the caller's scope `list`. `Pre` evaluates them in order into locals,
        /// `Args` passes them on. Both are empty when nothing is missing.</summary>
        private (string Pre, string Args) DefaultArgs(FunctionProto proto, int argc, string self, string list)
        {
            if (argc >= proto.ParamCount) return ("", "");
            var pre = new StringBuilder();
            var args = new StringBuilder();
            for (int i = argc; i < proto.ParamCount; i++)
            {
                var defaultProto = i < proto.ParamDefaults.Count ? proto.ParamDefaults[i] : null;
                if (defaultProto == null) throw new NativeNotSupportedException($"call with {argc} argument(s) of a function with {proto.ParamCount} parameters");
                pre.Append($"Value dv{i} = adoptV({GetFunc(defaultProto, FuncKind.Init).Name}({self}), {list}); ");
                args.Append($", dv{i}");
            }
            return (pre.ToString(), args.ToString());
        }

        /// <summary>Does a method call of this name with this many arguments reach an implementation that fills in default values? Then its dispatcher gets the scope list.</summary>
        private bool DispatchNeedsDefaults(string name, int argc) =>
            _program.Program.Classes.Values.Any(rc => rc.FindMethodWithAccess(name, argc).Proto is { } m && m.ParamCount != argc)
            || ExtensionMethods(name, argc).Any(e => e.Proto.ParamCount != argc);

        /// <summary>The conversions of the base types that a method call reaches when no class defines the method (SPEC 8.10): per kind of receiver the C++ expression.</summary>
        private static IEnumerable<(string Kind, string Expr)> BuiltinMethods(string name, int argc)
        {
            switch ((name, argc))
            {
                case ("ToBytes", 0): yield return ("K_String", "strToBytes(self, list)"); break;
                case ("ToUnicode", 1): yield return ("K_String", "strToUnicode(self, a0, list)"); yield return ("K_Char", "charToUnicode(self, a0, list)"); yield return ("K_Buffer", "bufToUnicode(self, a0, list)"); break;
                case ("ToUnicode", 0): yield return ("K_Buffer", "bufToUnicode(self, Int(2), list)"); break;
                case ("ToByte", 0): yield return ("K_Char", "charToByte(self)"); break;
                case ("ToChar", 0): yield return ("K_Int", "byteToChar(self)"); break;
                case ("ToString", 0): yield return ("K_Buffer", "bufToString(self, list)"); break;
                case ("ToUnicodeChar", 0): yield return ("K_Buffer", "bufToUnicodeChar(self, Int(2))"); break;
                case ("ToUnicodeChar", 1): yield return ("K_Buffer", "bufToUnicodeChar(self, a0)"); break;
                case ("ToLittleEndian", 0): yield return ("K_Buffer", "bufToEndian(self, true, list)"); break;
                case ("ToBigEndian", 0): yield return ("K_Buffer", "bufToEndian(self, false, list)"); break;
            }
        }

        private bool DispatchNeedsList(string name, int argc) =>
            name == "GetEnumerator" && argc == 0 || OwnMethodId(name, argc) != null || DispatchNeedsDefaults(name, argc) || BuiltinMethods(name, argc).Any();

        /// <summary>The arguments after the operands of a dispatcher call (see <see cref="DispatcherSignature"/>).</summary>
        private string DispatchTail(string name, int argc, string list, string caller) =>
            (DispatchNeedsList(name, argc) ? ", " + list : "") + (RestrictedMethodName(name) ? ", " + caller : "");

        private string DispatcherSignature(string name, int argc)
        {
            var parameters = new List<string> { "Value self" };
            parameters.AddRange(Enumerable.Range(0, argc).Select(i => $"Value a{i}"));
            if (DispatchNeedsList(name, argc)) parameters.Add("OwnList* list");
            if (RestrictedMethodName(name)) parameters.Add("uint32_t caller");
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
            var builtins = BuiltinMethods(name, argc).ToList();
            if (byFunc.Count == 0 && extensions.Count == 0 && !enumeratorOfArray && OwnMethodId(name, argc) == null && builtins.Count == 0)
                throw new NativeNotSupportedException($"method '{name}' with {argc} argument(s): no class of the program and no base-type extension defines it (the built-in methods TakeTo, TakeUpwards and TakeGlobal are not supported yet)");

            // a call site that passes addresses: an implementation without `ref` for that position gets the value
            bool derefs = _refCallSites.Contains((name, argc));
            string ArgsFor(FunctionProto proto) => string.Concat(Enumerable.Range(0, argc).Select(i => derefs && (proto.RefMask >> i & 1) == 0 ? $", derefArg(a{i})" : $", a{i}"));
            // an implementation with default values: the missing arguments are evaluated first (in order), then it is called
            bool restricted = RestrictedMethodName(name);
            string CallImpl(string target, FunctionProto proto, RuntimeClass? sample = null, bool extension = false)
            {
                var (pre, defaults) = DefaultArgs(proto, argc, "self", "list");
                string access = "";
                if (restricted && sample != null && sample.FindMethodWithAccess(name, argc) is { Proto: not null } found && found.Access != AccessModifier.Public)
                {
                    string cond = AllowedExpr(found.DeclaringClass!, found.Access);
                    string message = extension
                        ? $"Method '{name}' of the extension of '{sample.Name.Substring(1)}' is {DescribeAccess(found.Access)} and cannot be called from here."
                        : $"Method '{name}' of '{found.DeclaringClass!.Name}' is {DescribeAccess(found.Access)} and cannot be called from here.";
                    access = $"if (!{cond}) return accessDenied({CString(message)}); ";
                }
                return pre.Length == 0 ? $"{{ {access}return {target}(self{ArgsFor(proto)}); }}" : $"{{ {access}{pre}return {target}(self{ArgsFor(proto)}{defaults}); }}";
            }
            var sb = new StringBuilder();
            sb.AppendLine(DispatcherSignature(name, argc));
            sb.AppendLine("{");
            sb.AppendLine("    switch (self.kind) {");
            foreach (var (cpp, proto, extensionRc) in extensions)
                sb.AppendLine($"        case {cpp}: {CallImpl(_funcByProto[proto].Name, proto, extensionRc, true)}");
            foreach (var (kind, expr) in builtins)
                if (!extensions.Any(e => e.Cpp == kind)) sb.AppendLine($"        case {kind}: return {expr};");
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
                    sb.AppendLine($"            asObj(self); {CallImpl(byFunc.Keys.First().Name, byFunc.Keys.First().Proto!, _classList.First().Rc)}");
                else
                {
                    sb.AppendLine("            switch (asObj(self)->cls) {");
                    foreach (var (f, ids) in byFunc)
                    {
                        foreach (int id in ids) sb.AppendLine($"                case {id}:");
                        sb.AppendLine($"                    {CallImpl(f.Name, f.Proto!, _classList.First(c => c.Id == ids[0]).Rc)}");
                    }
                    sb.AppendLine("                default: break;");
                    sb.AppendLine("            }");
                    sb.AppendLine("            break;");
                }
            }
            sb.AppendLine("        default: break;");
            sb.AppendLine("    }");
            if (OwnMethodId(name, argc) is { } ownId)
            {
                // the built-in ownership method for everything that has no method of this name
                sb.AppendLine($"    ownMethod({ownId}, self, {(argc == 1 ? "a0" : "Undef()")}, list);");
                sb.AppendLine("    return Undef();");
            }
            else sb.AppendLine($"    fatal(\"Method '{name}' not found on this value.\");");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>Read and write access to the field `name`: the index of a field is the same in a class and all its subclasses,
        /// but not between unrelated classes that happen to use the same name. `Length`/`length` also work on strings, arrays and buffers.</summary>
        private string FieldHelpers(string name)
        {
            string text = FieldHelpersCore(name);
            return _usesProbes ? WrapProbeSetter(name, text) : text;
        }

        private string FieldHelpersCore(string name)
        {
            var indexByClass = new Dictionary<int, int>();
            foreach (var cls in _classList)
                if (cls.Rc.FieldIndex.TryGetValue(name, out int index)) indexByClass[cls.Id] = index;
            if (HasProperty(name) || RestrictedField(name)) return PropertyHelpers(name, indexByClass);
            bool isLength = name is "Length" or "length";
            string m = Mangle(name);
            string lengthCode = isLength ? "    { bool ok; Value n = lengthOf(v, &ok); if (ok) return n; }\n"
                : name == "littleEndian" ? "    if (v.kind == K_Buffer) return Bool(bufOf(v)->little != 0);\n" : "";
            var sb = new StringBuilder();
            if (indexByClass.Count == 0)
            {
                // no object of the program has such a field: only a built-in member (or dead code) can get here
                sb.AppendLine($"static inline Value gf_{m}(Value v) {{\n{lengthCode}    fatal(\"Field '{name}' not found on this value.\");\n}}");
                sb.AppendLine($"static inline void sf_{m}(Value v, Value x) {{ (void)v; (void)x; fatal(\"Field '{name}' cannot be assigned.\"); }}");
                sb.AppendLine($"static inline Value fp_{m}(Value v) {{ (void)v; fatal(\"Field '{name}' not found on this object.\"); }}");
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
            // a field declared with a unit (`int len : mm`) checks every assignment
            var unitChecks = new StringBuilder();
            foreach (var cls in _classList)
                if (indexByClass.ContainsKey(cls.Id) && cls.Rc.FindFieldRequiredUnit(name) is { } requiredUnit)
                    unitChecks.Append($"        case {cls.Id}: checkUnit(x, {UnitId(Unit.Parse(requiredUnit))}, {Constant(Value.MakeString(requiredUnit))}); break;\n");
            string unitCode = unitChecks.Length == 0 ? "" : "    switch (o->cls) {\n" + unitChecks + "        default: break;\n    }\n" + (_usesExceptions ? "    if (FIRE_UNLIKELY(g_unwind.active)) return;\n" : "");
            return $"static inline Value gf_{m}(Value v) {{\n{lengthCode}    Obj* o = asObj(v);\n{indexCode}\n    return o->fields()[idx];\n}}\n"
                 + $"static inline void sf_{m}(Value v, Value x) {{\n    Obj* o = asObj(v);\n{indexCode}\n{unitCode}    Value old = o->fields()[idx];\n    o->fields()[idx] = x;\n    retain(x);\n    release(old);\n}}\n"
                 + $"static inline Value fp_{m}(Value v) {{\n    Obj* o = asObj(v);\n{indexCode}\n    return PtrV(&o->fields()[idx]);\n}}\n";
        }

        /// <summary>Does some class of the program declare a property of this name (`get_name`/`set_name`)? Then reading and writing it takes the scope list
        /// of the caller (the getter's result belongs to it).</summary>
        private bool HasProperty(string name) =>
            _program.Program.Classes.Values.Any(rc => rc.Methods.ContainsKey("get_" + name) || rc.Methods.ContainsKey("set_" + name));

        /// <summary>Access to `name` where some class has a property of that name (SPEC 8.8): a real field of the class wins, otherwise the getter/setter
        /// is called - decided per class, like the VM does by name at run time.</summary>
        private string PropertyHelpers(string name, Dictionary<int, int> indexByClass)
        {
            string m = Mangle(name);
            bool isLength = name is "Length" or "length";
            string lengthCode = isLength ? "    { bool ok; Value n = lengthOf(v, &ok); if (ok) return n; }\n"
                : name == "littleEndian" ? "    if (v.kind == K_Buffer) return Bool(bufOf(v)->little != 0);\n" : "";
            var get = new StringBuilder();
            var set = new StringBuilder();
            var ptr = new StringBuilder();
            foreach (var cls in _classList)
            {
                string label = $"        case {cls.Id}: ";
                if (indexByClass.TryGetValue(cls.Id, out int index))
                {
                    string fieldGuard = "", fieldGuardSet = "";
                    if (CheckAccess && cls.Rc.FindFieldAccess(name) is { } fieldAccess && fieldAccess.Access != AccessModifier.Public)
                    {
                        string text = CString($"Field '{name}' of '{fieldAccess.DeclaringClass.Name}' is {DescribeAccess(fieldAccess.Access)} and cannot be accessed from here.");
                        string cond = AllowedExpr(fieldAccess.DeclaringClass, fieldAccess.Access);
                        fieldGuard = $"if (!{cond}) return accessDenied({text}); ";
                        fieldGuardSet = $"if (!{cond}) {{ accessDenied({text}); return; }} ";
                    }
                    get.AppendLine($"{label}{{ {fieldGuard}return o->fields()[{index}]; }}");
                    string unit = cls.Rc.FindFieldRequiredUnit(name) is { } requiredUnit
                        ? $"checkUnit(x, {UnitId(Unit.Parse(requiredUnit))}, {Constant(Value.MakeString(requiredUnit))}); " + (_usesExceptions ? "if (FIRE_UNLIKELY(g_unwind.active)) return; " : "")
                        : "";
                    set.AppendLine($"{label}{{ {fieldGuardSet}{unit}Value old = o->fields()[{index}]; o->fields()[{index}] = x; retain(x); release(old); return; }}");
                    ptr.AppendLine($"{label}return PtrV(&o->fields()[{index}]);");
                    continue;
                }
                var (getter, getterDecl, getterAccess) = cls.Rc.FindMethodWithAccess("get_" + name, 0);
                var (setter, setterDecl, setterAccess) = cls.Rc.FindMethodWithAccess("set_" + name, 1);
                string AccessorGuard(string method, RuntimeClass decl, AccessModifier access, bool isSetter)
                {
                    if (!CheckAccess || access == AccessModifier.Public) return "";
                    string text = CString($"'{method}' of '{decl.Name}' is {DescribeAccess(access)} and cannot be accessed from here.");
                    string cond = AllowedExpr(decl, access);
                    return isSetter ? $"if (!{cond}) {{ accessDenied({text}); return; }} " : $"if (!{cond}) return accessDenied({text}); ";
                }
                if (getter != null) get.AppendLine($"{label}{{ {AccessorGuard("get_" + name, getterDecl!, getterAccess, false)}Value r = {_funcByProto[getter].Name}(v); adopt(r, list); return r; }}");
                else if (setter != null) get.AppendLine($"{label}fatal(\"Property '{name}' of '{cls.Rc.Name}' has no getter (only 'set').\");");
                if (setter != null) set.AppendLine($"{label}{{ {AccessorGuard("set_" + name, setterDecl!, setterAccess, true)}Value r = {_funcByProto[setter].Name}(v, x); adopt(r, list); return; }}");
                else if (getter != null) set.AppendLine($"{label}fatal(\"Property '{name}' on '{cls.Rc.Name}' has no setter (only 'get').\");");
            }
            var sb = new StringBuilder();
            string lp = (HasProperty(name) ? ", OwnList* list" : "") + (RestrictedField(name) ? ", uint32_t caller" : "");
            string unusedLp = (HasProperty(name) ? "(void)list; " : "") + (RestrictedField(name) ? "(void)caller; " : "");
            sb.AppendLine($"static inline Value gf_{m}(Value v{lp}) {{\n{lengthCode}    {unusedLp}\n    Obj* o = asObj(v);\n    switch (o->cls) {{\n{get}        default: fatal(\"Field '{name}' not found on this object.\");\n    }}\n}}");
            sb.AppendLine($"static inline void sf_{m}(Value v, Value x{lp}) {{\n    (void)x; {unusedLp}\n    Obj* o = asObj(v);\n    switch (o->cls) {{\n{set}        default: fatal(\"Field '{name}' not found on this object.\");\n    }}\n}}");
            sb.AppendLine($"static inline Value fp_{m}(Value v) {{\n    Obj* o = asObj(v);\n    switch (o->cls) {{\n{ptr}        default: fatal(\"The address of '{name}' cannot be taken (it is a property or does not exist).\");\n    }}\n}}");
            return sb.ToString();
        }

        /// <summary>`a[i]` and `a[i] = v`: arrays, buffers and strings directly, objects through their GetIndex/SetIndex methods.</summary>
        private string IndexHelpers()
        {
            var sb = new StringBuilder();
            sb.AppendLine("static inline Value aget_g(Value a, Value i, OwnList* list) {");
            if (_dispatchers.Contains(("GetIndex", 1)))
            {
                sb.AppendLine($"    if (a.kind == K_Class) {{ Value r = call_GetIndex_1(a, i{DispatchTail("GetIndex", 1, "list", "0xFFFFFFFFu")}); adopt(r, list); return r; }}");
            }
            else sb.AppendLine("    (void)list;");
            sb.AppendLine("    return arrayGet(a, i);");
            sb.AppendLine("}");
            sb.AppendLine("static inline void aset_g(Value a, Value i, Value v, OwnList* list) {");
            if (_dispatchers.Contains(("SetIndex", 2)))
                sb.AppendLine($"    if (a.kind == K_Class) {{ Value r = call_SetIndex_2(a, i, v{DispatchTail("SetIndex", 2, "list", "0xFFFFFFFFu")}); adopt(r, list); return; }}");
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

            if (_usesExceptions)
            {
                sb.AppendLine("bool excMatches(Value exception, int32_t type) {");
                sb.AppendLine("    uint32_t cls = asObj(exception)->cls; (void)cls;");
                sb.AppendLine("    switch (type) {");
                for (int i = 0; i < _excTypes.Count; i++)
                {
                    string typeName = _excTypes[i];
                    if (typeName == "Exception") { sb.AppendLine($"        case {i}: return true;"); continue; }
                    var ids = _classList.Where(c => ClassIs(c.Rc, typeName)).Select(c => c.Id).ToList();
                    sb.AppendLine($"        case {i}: " + (ids.Count == 0 ? "return false;" : "switch (cls) { " + string.Concat(ids.Select(id => $"case {id}: ")) + "return true; default: return false; }"));
                }
                sb.AppendLine("        default: return false;");
                sb.AppendLine("    }");
                sb.AppendLine("}");
                sb.AppendLine("const char* className(uint32_t cls) {");
                sb.AppendLine("    switch (cls) {");
                foreach (var cls in _classList) sb.AppendLine($"        case {cls.Id}: return {CString(cls.Rc.Name)};");
                sb.AppendLine("        default: return \"?\";");
                sb.AppendLine("    }");
                sb.AppendLine("}");
                var indexClass = _classes.GetValueOrDefault("IndexOutOfBoundsException") ?? throw new NativeNotSupportedException("exceptions need the prelude class IndexOutOfBoundsException");
                var destroyedClass = _classes.GetValueOrDefault("DestroyedException") ?? throw new NativeNotSupportedException("exceptions need the prelude class DestroyedException");
                sb.AppendLine("Value makeDestroyedError(Value message) {");
                sb.AppendLine($"    Value o = newObject({destroyedClass.Id}, {destroyedClass.Fields.Count}, g_globalOwn);");
                sb.AppendLine($"    {_funcByProto[destroyedClass.Rc.FindConstructor(1)!].Name}(o, message);");
                sb.AppendLine("    return o;");
                sb.AppendLine("}");
                if (_usesReflection && _classes.GetValueOrDefault("ReflectionException") is { } reflectClass)
                {
                    sb.AppendLine("Value makeReflectError(Value message) {");
                    sb.AppendLine($"    Value o = newObject({reflectClass.Id}, {reflectClass.Fields.Count}, g_globalOwn);");
                    sb.AppendLine($"    {_funcByProto[reflectClass.Rc.FindConstructor(1)!].Name}(o, message);");
                    sb.AppendLine("    return o;");
                    sb.AppendLine("}");
                }
                var accessClass = _classes.GetValueOrDefault("AccessDeniedException") ?? throw new NativeNotSupportedException("exceptions need the prelude class AccessDeniedException");
                sb.AppendLine("Value makeAccessError(Value message) {");
                sb.AppendLine($"    Value o = newObject({accessClass.Id}, {accessClass.Fields.Count}, g_globalOwn);");
                sb.AppendLine($"    {_funcByProto[accessClass.Rc.FindConstructor(1)!].Name}(o, message);");
                sb.AppendLine("    return o;");
                sb.AppendLine("}");
                var unitClass = _classes.GetValueOrDefault("UnitMismatchException") ?? throw new NativeNotSupportedException("exceptions need the prelude class UnitMismatchException");
                sb.AppendLine("Value makeUnitError(Value message, Value expected, Value actual) {");
                sb.AppendLine($"    Value o = newObject({unitClass.Id}, {unitClass.Fields.Count}, g_globalOwn);");
                sb.AppendLine($"    {_funcByProto[unitClass.Rc.FindConstructor(3)!].Name}(o, message, expected, actual);");
                sb.AppendLine("    return o;");
                sb.AppendLine("}");
                sb.AppendLine("Value makeIndexError(Value message, int64_t index, int64_t length) {");
                sb.AppendLine($"    Value o = newObject({indexClass.Id}, {indexClass.Fields.Count}, g_globalOwn);");
                sb.AppendLine($"    {_funcByProto[indexClass.Rc.FindConstructor(3)!].Name}(o, message, Int(index), Int(length));");
                sb.AppendLine("    return o;");
                sb.AppendLine("}");
            }
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
            /// <summary>The `try` statements that are active here, outermost first.</summary>
            public List<HEntry> Handlers = new();
            /// <summary>The catch lambda this code belongs to (null: the function itself).</summary>
            public Region? Region;
            /// <summary>The `finally` blocks that this code is inside of (their entry addresses), outermost first.</summary>
            public List<int> Fins = new();

            public Flow Clone() => new() { Depth = Depth, Scopes = new List<(int, int)>(Scopes), Refs = Refs, Handlers = new List<HEntry>(Handlers), Region = Region, Fins = new List<int>(Fins) };

            public bool SameShape(Flow o) => Depth == o.Depth && ReferenceEquals(Region, o.Region) && Scopes.SequenceEqual(o.Scopes)
                && Handlers.SequenceEqual(o.Handlers) && Fins.SequenceEqual(o.Fins);
        }

        /// <summary>An active `try`: the region of its template, and whether only its `finally` is still armed (while a catch block runs).</summary>
        private readonly record struct HEntry(Region Region, bool FinOnly);

        /// <summary>The code of a function or catch lambda, and what belongs at its end: shared unwinding exits and the landing sites of its handlers.</summary>
        private sealed class Out
        {
            public readonly StringBuilder Code = new();
            public readonly StringBuilder Tail = new();
            public readonly Dictionary<string, int> Exits = new();
            public int MaxDepth;
        }

        /// <summary>One `try` statement (a handler template): its catch blocks are a C++ lambda that runs on top of the throw site, so that
        /// `resume` can continue there. Slots of the operand stack that the catch code uses are its own (<c>c{template}_{k}</c>).</summary>
        private sealed class Region
        {
            public required int Template;
            /// <summary>The region the `try` statement is in (null: the function).</summary>
            public Region? Outer;
            /// <summary>The state in front of the RegisterHandler instruction.</summary>
            public required Flow Reg;
            public int ScopeBase => Reg.Scopes.Count;
            public int DepthBase => Reg.Depth;
            public int CatchScopeId;
            public bool HasFinally;
            public int? FinallyAddr;
            public List<(string? Type, int Addr)> Catches = new();
            public int Start = -1, End = -1;
            /// <summary>Jump targets outside of the catch blocks that the catch code leaves to.</summary>
            public readonly SortedSet<int> ExitLabels = new();
            public bool ReturnExit;
            public readonly Out Out = new();
        }

        private void UseExceptions()
        {
            if (_usesExceptions && _usesGlobalOwn) return;
            _usesExceptions = true;
            _usesGlobalOwn = true;
            _version++;
        }

        private Out OutOf(Func f, Region? region) => region?.Out ?? f.Root;

        /// <summary>The innermost region (starting at <paramref name="from"/>) that contains the address; null: the function.</summary>
        private static Region? RegionAt(Region? from, int addr)
        {
            for (var r = from; r != null; r = r.Outer)
                if (r.Start <= addr && addr < r.End) return r;
            return null;
        }

        /// <summary>The name of an operand stack slot in the code of <paramref name="region"/>.</summary>
        private static string SlotName(Region? region, int k)
        {
            for (var r = region; r != null; r = r.Outer)
                if (k >= r.DepthBase) return $"c{r.Template}_{k}";
            return $"s{k}";
        }

        private int ExcTypeId(string? typeName)
        {
            if (typeName == null) return -1;
            int index = _excTypes.IndexOf(typeName);
            if (index < 0) { _excTypes.Add(typeName); index = _excTypes.Count - 1; }
            return index;
        }

        private static string ListName(int scopeId) => scopeId == FunctionScope ? "OP" : scopeId == GlobalScope ? "OG" : $"O{scopeId}";

        private void GenerateChunk(Func f)
        {
            var chunk = f.Chunk;
            string name = f.Name;
            var code = OpInfo.Decode(chunk.Code);
            var indexOfAddr = new Dictionary<int, int>();
            for (int i = 0; i < code.Count; i++) indexOfAddr[code[i].Addr] = i;
            f.Decoded = code;
            f.IndexOf = indexOfAddr;
            f.Regions = new Dictionary<int, Region>();
            f.Root = new Out();
            var finAddrs = chunk.Handlers.Where(t => t.FinallyAddr != null).Select(t => t.FinallyAddr!.Value).ToHashSet();

            var entry = new Flow?[code.Count];
            f.Entry = entry;
            var start = new Flow();
            start.Scopes.Add(f.Kind == FuncKind.Main ? (GlobalScope, 0) : (FunctionScope, f.ParamLike));
            entry[0] = start;
            // Parameters can be anything: assume they may be references (a call-site analysis could narrow this).
            for (int i = 0; i < f.ParamLike; i++) MarkVarRef(VarKey(f, $"P{i}"));
            if (f.Kind == FuncKind.Main && _usesGlobalOwn) f.NeedsList.Add(GlobalScope);   // thrown exceptions, TakeGlobal and TakeUpwards need the list of the global scope

            var targets = new HashSet<int>();
            var work = new Stack<int>();
            work.Push(0);
            var locals = new SortedSet<string>(StringComparer.Ordinal);

            void Propagate(int addr, Flow state, int from)
            {
                if (!indexOfAddr.TryGetValue(addr, out int target))
                    throw new NativeNotSupportedException($"jump to {addr} inside an instruction ({name})");
                if (finAddrs.Contains(addr)) { state = state.Clone(); state.Fins.Add(addr); }
                if (entry[target] == null) { entry[target] = state.Clone(); work.Push(target); return; }
                var existing = entry[target]!;
                if (!existing.SameShape(state))
                    throw new NativeNotSupportedException($"inconsistent stack/scope state at {addr} ({name}), reached from {from}");
                if ((existing.Refs | state.Refs) != existing.Refs) { existing.Refs |= state.Refs; work.Push(target); }
            }

            // Pass 1: propagate states (and find out which scopes can own objects); pass 2: emit with the states found.
            // Which jumps and returns run through a `finally` is only known once the code in front of it was seen, so repeat until that settles.
            while (true)
            {
                int signature = f.FinSignature;
                while (work.Count > 0)
                {
                    int idx = work.Pop();
                    var state = entry[idx]!.Clone();
                    var ins = code[idx];
                    var step = Step(f, chunk, ins, state, null, locals);
                    var o = OutOf(f, entry[idx]!.Region);
                    o.MaxDepth = Math.Max(o.MaxDepth, Math.Max(entry[idx]!.Depth, state.Depth));
                    if (step.JumpTarget is int jt) { targets.Add(jt); Propagate(jt, state, ins.Addr); }
                    if (step.Extra != null)
                        foreach (var (addr, edgeState) in step.Extra) { targets.Add(addr); Propagate(addr, edgeState, ins.Addr); }
                    if (step.FallsThrough)
                    {
                        if (idx + 1 >= code.Count) throw new NativeNotSupportedException($"code of {name} runs past its end");
                        Propagate(code[idx + 1].Addr, state, ins.Addr);
                    }
                }
                if (f.FinSignature == signature) break;
                for (int i = 0; i < code.Count; i++) if (entry[i] != null) work.Push(i);
            }

            for (int i = 0; i < code.Count; i++)
            {
                if (entry[i] == null) continue; // unreachable
                var ins = code[i];
                var o = OutOf(f, entry[i]!.Region);
                if (targets.Contains(ins.Addr)) o.Code.AppendLine($"L{ins.Addr}:;");
                o.Code.AppendLine($"    // {ins.Addr}: {ins.Op}{(ins.A.Length > 0 ? " " + string.Join(",", ins.A) : "")}");
                var state = entry[i]!.Clone();
                Step(f, chunk, ins, state, o.Code, locals);
            }
            foreach (var region in f.Regions.Values.OrderBy(r => r.Template)) Landing(f, region, locals);

            var sb = new StringBuilder();
            foreach (var region in f.Regions.Values.OrderBy(r => r.Template))
            {
                var types = region.Catches.Select(c => ExcTypeId(c.Type).ToString(CultureInfo.InvariantCulture)).ToList();
                sb.AppendLine($"static const int32_t HT_{name}_{region.Template}[] = {{{(types.Count == 0 ? "0" : string.Join(", ", types))}}};");
                sb.AppendLine($"static const HandlerInfo HI_{name}_{region.Template} = {{{region.Catches.Count}, HT_{name}_{region.Template}, {(region.HasFinally ? 1 : 0)}}};");
            }
            sb.AppendLine(f.Signature);
            sb.AppendLine("{");
            if (f.HasSelf) sb.AppendLine("    (void)self;");
            if (f.Kind == FuncKind.Lambda)
            {
                sb.AppendLine("    Value self = lamOn(lam); (void)self;");
                for (int i = 0; i < f.CaptureCount; i++) sb.AppendLine($"    Value P{f.ParamCount + i} = lamCapture(lam, {i});");
            }
            for (int i = 0; i < f.ParamCount; i++) sb.AppendLine($"    (void)P{i};");
            if (f.Root.MaxDepth > 0) sb.AppendLine("    Value " + string.Join(", ", Enumerable.Range(0, f.Root.MaxDepth).Select(i => $"s{i}")) + ";");
            var localsToDeclare = locals.Where(l => !(l.StartsWith("P") && int.Parse(l.AsSpan(1)) < f.ParamLike)).ToList();
            if (localsToDeclare.Count > 0) sb.AppendLine("    Value " + string.Join(", ", localsToDeclare.Select(l => l + " = Undef()")) + ";");
            if (f.NeedsList.Count > 0) sb.AppendLine("    OwnList " + string.Join(", ", f.NeedsList.OrderBy(i => i).Select(i => ListName(i) + (i < 0 ? " = {nullptr, nullptr, poolMark(), nullptr, nullptr}" : " = {nullptr, nullptr, 0, nullptr, nullptr}"))) + ";");
            if (f.NeedsArgList) sb.AppendLine("    OwnList AL = {nullptr, nullptr, 0, nullptr, nullptr};");
            if (f.Kind == FuncKind.Main && _usesGlobalOwn) sb.AppendLine("    g_globalOwn = &OG;");
            if (f.Kind != FuncKind.Main && _usesTakeUpwards && f.NeedsList.Contains(FunctionScope)) sb.AppendLine("    OP.parent = g_globalOwn;   // the parent scope of a function scope is the global scope");
            // Parameters are variables: they hold what was passed.
            for (int i = 0; i < f.ParamLike; i++)
                if (VarRef(VarKey(f, $"P{i}"))) sb.AppendLine($"    retain(P{i});");
            EmitHandlers(f, null, sb, locals, "    ");

            sb.Append(f.Root.Code);
            sb.Append(f.Root.Tail);
            if (f.Kind != FuncKind.Main) sb.AppendLine("    fatal(\"control flow fell off the end of a function\");");
            sb.AppendLine("}");
            f.Code = sb.ToString();
        }

        /// <summary>Declares the handlers of the `try` statements that sit directly in <paramref name="outer"/>, and defines their catch lambdas.</summary>
        private void EmitHandlers(Func f, Region? outer, StringBuilder sb, ISet<string> locals, string indent)
        {
            foreach (var region in f.Regions.Values.Where(r => r.Outer == outer).OrderBy(r => r.Template))
            {
                sb.AppendLine($"{indent}Handler H{region.Template};");
                if (region.Catches.Count == 0) continue;
                int t = region.Template;
                sb.AppendLine($"{indent}auto C{t} = [&](uint32_t ci, Value e) {{");
                string inner = indent + "    ";
                int top = region.Out.MaxDepth;
                if (top > region.DepthBase) sb.AppendLine($"{inner}Value " + string.Join(", ", Enumerable.Range(region.DepthBase, top - region.DepthBase).Select(k => $"c{t}_{k}")) + ";");
                EmitHandlers(f, region, sb, locals, inner);
                sb.AppendLine($"{inner}switch (ci) {{");
                string catchVar = $"B{region.CatchScopeId}_0";
                for (int i = 0; i < region.Catches.Count; i++)
                {
                    string init = (locals.Contains(catchVar) ? $"{catchVar} = e; " : "(void)e; ") + (f.NeedsList.Contains(region.CatchScopeId) ? $"{ListName(region.CatchScopeId)}.mark = poolMark(); " + (_usesTakeUpwards ? $"{ListName(region.CatchScopeId)}.parent = &{ListName(region.Reg.Scopes[^1].Id)}; " : "") : "");
                    sb.AppendLine($"{inner}case {i}: {{ {init}goto L{region.Catches[i].Addr}; }}");
                }
                sb.AppendLine($"{inner}default: break;");
                sb.AppendLine($"{inner}}}");
                sb.Append(region.Out.Code);
                sb.Append(region.Out.Tail);
                sb.AppendLine($"{inner}fatal(\"control flow fell off the end of a catch block\");");
                sb.AppendLine($"{indent}}};");
            }
        }

        // -------------------------------------------------------------------------------------------------------------
        // Exceptions: unwinding, landing sites, return through `finally`
        // -------------------------------------------------------------------------------------------------------------
        private Region GetRegion(Func f, int template, Flow st)
        {
            if (f.Regions.TryGetValue(template, out var region)) return region;
            var t = f.Chunk.Handlers[template];
            region = new Region
            {
                Template = template, Outer = st.Region, Reg = st.Clone(), CatchScopeId = f.Chunk.Code.Count + template,
                HasFinally = t.FinallyAddr != null, FinallyAddr = t.FinallyAddr,
            };
            foreach (var (type, addr) in t.Catches) region.Catches.Add((type, addr));
            if (region.Catches.Count > 0)
            {
                region.Start = region.Catches[0].Addr;
                var before = f.Decoded[f.IndexOf[region.Start] - 1];
                if (before.Op != OpCode.Jump) throw new NativeNotSupportedException($"unexpected layout of a try statement in {f.Name}");
                region.End = before.A[0];
            }
            f.Regions[template] = region;
            return region;
        }

        /// <summary>The state while a catch block runs: the registers of the `try` are there, only its `finally` is still armed.</summary>
        private static Flow LandingFlow(Region region)
        {
            var flow = region.Reg.Clone();
            if (region.HasFinally) flow.Handlers.Add(new HEntry(region, true));
            return flow;
        }

        /// <summary>Leaves the scopes <c>[downTo, fromCount)</c> of <paramref name="st"/> (innermost first): what they own is destroyed, what they hold is released.
        /// With <paramref name="transfer"/> an object that is returned does not die with them.</summary>
        private void ReleaseScopes(Func f, Flow st, int fromCount, int downTo, bool reset, ISet<string> locals, Action<string> emit, string? transfer)
        {
            if (transfer != null)
                for (int i = fromCount - 1; i >= downTo; i--)
                    if (f.NeedsList.Contains(st.Scopes[i].Id)) emit($"transferOut({transfer}, &{ListName(st.Scopes[i].Id)});");
            for (int i = fromCount - 1; i >= downTo; i--)
            {
                int id = st.Scopes[i].Id;
                if (f.NeedsList.Contains(id)) emit($"leave(&{ListName(id)});");
                foreach (var v in ScopeVariables(f, id, locals))
                    if (VarRef(VarKey(f, v))) emit(reset ? $"release({v}); {v} = Undef();" : $"release({v});");
            }
        }

        /// <summary>The end of the program: the global scope is released like any other (destructors run), then what the globals hold.</summary>
        private void HaltCode(Func f, Action<string> emit)
        {
            if (f.NeedsList.Contains(GlobalScope)) emit("leave(&OG);");
            foreach (var g in _globals.OrderBy(x => x))
                if (VarRef($"G{g}")) emit($"release(G{g});");
            foreach (var (_, index) in _staticFields.OrderBy(kv => kv.Value))
                if (VarRef($"SF{index}")) emit($"release(SF{index});");
        }

        /// <summary>The code that runs when something is unwinding through this point: the scopes up to the innermost active `try` of this
        /// function (or catch lambda) are left, then control goes to the landing site of that `try`, or out of the function. Returns the label.</summary>
        private string ExitLabel(Func f, Flow st, ISet<string> locals)
        {
            var o = OutOf(f, st.Region);
            var body = new StringBuilder();
            void E(string text) => body.Append("    ").AppendLine(text);
            HEntry? local = st.Handlers.Count > 0 && st.Handlers[^1].Region.Outer == st.Region ? st.Handlers[^1] : null;
            int baseCount = st.Region?.ScopeBase ?? (f.Kind == FuncKind.Main ? 1 : 0);
            ReleaseScopes(f, st, st.Scopes.Count, local?.Region.ScopeBase ?? baseCount, local != null || st.Region != null, locals, E, null);
            if (local != null) E($"goto LAND{local.Value.Region.Template};");
            else if (st.Region != null) E("return;");
            else if (f.Kind == FuncKind.Main) { HaltCode(f, E); E("return;"); }
            else E("return Undef();");
            string text = body.ToString();
            if (!o.Exits.TryGetValue(text, out int id))
            {
                id = o.Exits.Count;
                o.Exits[text] = id;
                o.Tail.AppendLine($"U{id}:;").Append(text);
            }
            return $"U{id}";
        }

        /// <summary>A `return` of the C++ variable <c>r</c> from the code described by <paramref name="st"/>. A `try` with a `finally` that is still active
        /// in this region gets to run it first (completion kind 2); a catch lambda hands the return to the landing site of its `try`. Returns the edges
        /// of the flow graph (the entries of the finally blocks).</summary>
        private List<(int Addr, Flow State)> ReturnSeq(Func f, Flow st, bool rIsRef, bool retain, Action<string>? emit, ISet<string> locals)
        {
            var edges = new List<(int, Flow)>();
            void E(string text) => emit?.Invoke(text);
            bool isCtor = f.Kind == FuncKind.Ctor;
            string? transfer = isCtor ? null : "r";
            if (retain && rIsRef && !isCtor) E("retain(r);");
            for (int hi = st.Handlers.Count - 1; hi >= 0 && st.Handlers[hi].Region.Outer == st.Region; hi--)
            {
                var region = st.Handlers[hi].Region;
                if (!st.Handlers[hi].FinOnly) E("popHandler();");   // (the armed finally of a catch block was already taken off the stack)
                if (!region.HasFinally) continue;
                int fin = region.FinallyAddr!.Value;
                ReleaseScopes(f, st, st.Scopes.Count, region.ScopeBase, true, locals, E, transfer);
                E($"{SlotName(st.Region, region.DepthBase)} = r; {SlotName(st.Region, region.DepthBase + 1)} = Int(2); goto L{fin};");
                f.FinReturn.Add(fin);
                ulong below = region.DepthBase >= 64 ? ulong.MaxValue : (1UL << region.DepthBase) - 1;
                edges.Add((fin, new Flow
                {
                    Depth = region.DepthBase + 2, Scopes = st.Scopes.Take(region.ScopeBase).ToList(), Handlers = st.Handlers.Take(hi).ToList(),
                    Region = st.Region, Fins = new List<int>(region.Reg.Fins), Refs = (st.Refs & below) | (rIsRef && region.DepthBase < 64 ? 1UL << region.DepthBase : 0),
                }));
                return edges;
            }
            if (st.Region == null)
            {
                ReleaseScopes(f, st, st.Scopes.Count, 0, false, locals, E, transfer);
                E(isCtor ? "return self;" : "return r;");
            }
            else
            {
                ReleaseScopes(f, st, st.Scopes.Count, st.Region.ScopeBase, true, locals, E, transfer);
                E($"exitReturn(&H{st.Region.Template}, r); return;");
                st.Region.ReturnExit = true;
                edges.AddRange(ReturnSeq(f, LandingFlow(st.Region), true, false, null, locals));
            }
            return edges;
        }

        /// <summary>Where unwinding arrives at the function that owns a `try` (reached from <c>U</c> exits): what a catch block left through
        /// (jump, return), an exception that this `try` did not catch (it runs the `finally` and throws on), or something passing by.</summary>
        private void Landing(Func f, Region r, ISet<string> locals)
        {
            var o = OutOf(f, r.Outer);
            int t = r.Template;
            var lt = new StringBuilder();
            void E(string text) => lt.Append("    ").AppendLine(text);
            var reg = r.Reg;
            lt.AppendLine($"LAND{t}: FIRE_UNUSED_LABEL;");
            if (r.ExitLabels.Count > 0)
            {
                E($"if (g_unwind.kind == UW_JUMP && g_unwind.target == &H{t}) {{");
                E("    uint32_t lab = g_unwind.label; clearUnwind();");
                E("    switch (lab) {");
                foreach (int target in r.ExitLabels)
                {
                    var te = f.Entry[f.IndexOf[target]] ?? throw new NativeNotSupportedException($"catch block leaves to unreachable code ({f.Name})");
                    bool here = ReferenceEquals(te.Region, r.Outer);
                    E($"    case {target}: {{");
                    ReleaseScopes(f, reg, reg.Scopes.Count, here ? te.Scopes.Count : r.Outer?.ScopeBase ?? 0, true, locals, x => E("        " + x), null);
                    if (here)
                    {
                        for (int k = r.DepthBase; k < te.Depth; k++) E($"        {SlotName(r.Outer, k)} = g_unwind.regs[{k - r.DepthBase}];");
                        E($"        goto L{target};");
                    }
                    else E($"        exitJump(&H{r.Outer!.Template}, {target}); return;");
                    E("    }");
                }
                E("    default: fatal(\"internal error: unknown landing label\");");
                E("    }");
                E("}");
            }
            if (r.ReturnExit)
            {
                E($"if (g_unwind.kind == UW_RETURN && g_unwind.target == &H{t}) {{");
                E("    Value r = g_unwind.value; (void)r; clearUnwind();");
                ReturnSeq(f, LandingFlow(r), true, false, x => E("    " + x), locals);
                E("}");
            }
            string exit = ExitLabel(f, reg, locals);
            E($"if (g_unwind.kind == UW_RETHROW && g_unwind.target == &H{t}) {{");
            E("    Value x = g_unwind.value; clearUnwind();");
            if (r.HasFinally) E($"    {SlotName(r.Outer, r.DepthBase)} = x; {SlotName(r.Outer, r.DepthBase + 1)} = Int(1); goto L{r.FinallyAddr};");
            else { E("    throwValue(x, false);"); E($"    goto {exit};"); }
            E("}");
            E($"goto {exit};");
            o.Tail.Append(lt);
        }

        private readonly record struct StepResult(bool FallsThrough, int? JumpTarget, List<(int Addr, Flow State)>? Extra = null);

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
            string S(int k) => SlotName(st.Region, k);
            // After an operation that can throw: if an exception is unwinding, leave (to the handler of this function, or out of it).
            void Check()
            {
                if (!_usesExceptions || sb == null) return;
                E($"if (FIRE_UNLIKELY(g_unwind.active)) goto {ExitLabel(f, st, locals)};");
            }
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
            // A member that the code of this function may not use: the access is an AccessDeniedException (the code after it is dead when it is thrown)
            void AccessCheck(RuntimeClass declaring, AccessModifier access, string message)
            {
                if (!CheckAccess || AccessAllowed(f.Chunk.OwnerClass, declaring, access)) return;
                E($"accessDenied({CString(message)});");
                Check();
            }
            void RequireSelf() { if (!f.HasSelf && f.Kind != FuncKind.Lambda) throw new NativeNotSupportedException($"'this' outside of an instance member ({fn} at {ins.Addr})"); }
            string Args(int first, int count) => string.Concat(Enumerable.Range(first, count).Select(i => ", " + S(i)));
            // `ref` arguments (SPEC 5.4.2): the prefix CopyArgs in front of the call marks the arguments that carry an address (bits 3); `flat`/`copy` are not translated yet.
            long ArgMask()
            {
                int at = f.IndexOf[ins.Addr];
                if (at == 0 || f.Decoded[at - 1].Op != OpCode.CopyArgs) return 0;
                long mask = (long)f.Decoded[at - 1].A[0] | ((long)f.Decoded[at - 1].A[1] << 16) | ((long)f.Decoded[at - 1].A[2] << 32) | ((long)f.Decoded[at - 1].A[3] << 48);
                return mask;
            }
            // The arguments for a callee that is known: an address goes to a `ref` parameter, the others get the value.
            string CallArgs(int first, int count, long mask, uint calleeRefs) => string.Concat(Enumerable.Range(0, count).Select(i =>
                ", " + ((mask >> (4 * i) & 15) == 3 && (calleeRefs >> i & 1) == 0 ? $"ptrRead({S(first + i)})" : S(first + i))));
            // `f(g())` (SPEC 2.1): the result of `g` that is still the caller's belongs to the callee - it travels in the argument list of the call
            // and is destroyed after the call unless the callee kept it (field, TakeTo, return value). The destructor therefore runs after the
            // callee's own locals, not before them as in the VM.
            void ArgsBefore(int first, int argc, long mask)
            {
                for (int i = 0; i < Math.Min(argc, 16); i++)
                {
                    int bits = (int)(mask >> (4 * i) & 15);
                    if (bits == 4)
                    {
                        f.NeedsArgList = true;
                        E($"reownArg({S(first + i)}, &{OwnerList()}, &AL);");
                    }
                    else if (bits is 1 or 2)
                    {
                        // `f(flat x)` / `f(copy x)`: the copy belongs to the callee - it travels in the argument list like a returned value
                        f.NeedsArgList = true;
                        E($"{S(first + i)} = copyArg({S(first + i)}, {(bits == 2 ? "true" : "false")}, &{OwnerList()}, &AL);");
                        Check();
                    }
                }
            }
            void ArgsAfter(int argc, long mask, string result)
            {
                for (int i = 0; i < Math.Min(argc, 16); i++)
                    if ((mask >> (4 * i) & 15) is 1 or 2 or 4) { E($"finishArgs({result}, &AL);"); return; }
            }
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
                    {
                        E($"{{ Value o = {v}; {v} = addR(o, {k}, &{OwnerList()}); retain({v}); release(o); }}");
                        Check();
                    }
                    else
                        E($"{v} = {(subtract ? "sub" : "add")}({v}, {k});");
                    return Next();
                }

                case OpCode.Add:
                {
                    Need(2);
                    if (UseOperator("operator+"))
                    {
                        E($"{S(d - 2)} = {OperatorWrapper("operator+")}({S(d - 2)}, {S(d - 1)}, &{OwnerList()});");
                        Check();
                        d--; SetR(d - 1, true);
                    }
                    else if (R(d - 2) || R(d - 1))
                    {
                        E($"{S(d - 2)} = addR({S(d - 2)}, {S(d - 1)}, &{OwnerList()});");
                        Check();
                        d--; SetR(d - 1, true);
                    }
                    else { E($"{S(d - 2)} = add({S(d - 2)}, {S(d - 1)});"); d--; SetR(d - 1, false); }
                    return Next();
                }
                case OpCode.Power: return Binary("power", "operator^");
                case OpCode.Sub: return Binary("sub", "operator-");
                case OpCode.Mul: return Binary("mul", "operator*");
                case OpCode.Div: return Binary("divide", "operator/");
                case OpCode.Mod: return Binary("modulo", "operator%");
                case OpCode.BitAnd: return Binary("bitAnd", "operator&");
                case OpCode.BitOr: return Binary("bitOr", "operator|");
                case OpCode.BitXor: return Binary("bitXor", "operator#");
                case OpCode.ShiftLeft: return Binary("shl", "operator<<");
                case OpCode.ShiftRight: return Binary("shr", "operator>>");
                case OpCode.RotateUnderTop:
                {
                    Need(3);
                    E($"{{ Value t = {S(d - 3)}; {S(d - 3)} = {S(d - 2)}; {S(d - 2)} = t; }}");
                    bool lower = R(d - 3), middle = R(d - 2);
                    SetR(d - 3, middle); SetR(d - 2, lower);
                    return Next();
                }
                case OpCode.CoerceUnit:
                    Need(1); E($"{S(d - 1)} = coerceUnit({S(d - 1)}, {UnitId(chunk.Units[ins.A[0]])});"); return Next();
                case OpCode.CoerceUnitDynamic:
                    Need(2); E($"{S(d - 1)} = coerceUnit({S(d - 1)}, unitOf({S(d - 2)}));"); return Next();
                case OpCode.CoerceType:
                    Need(1);
                    E($"{S(d - 1)} = coerceType({S(d - 1)}, {(TypeTag)ins.A[0] switch { TypeTag.Bool => "K_Bool", TypeTag.Int => "K_Int", TypeTag.Float => "K_Float", TypeTag.Char => "K_Char", _ => "K_String" }});");
                    return Next();
                case OpCode.CoerceTypeDynamic:
                    Need(2); E($"{S(d - 1)} = coerceType({S(d - 1)}, {S(d - 2)}.kind);"); return Next();
                case OpCode.IsInUnit:
                    Need(1); E($"{S(d - 1)} = Bool(unitCompatible(unitOf({S(d - 1)}), {UnitId(chunk.Units[ins.A[0]])}));"); SetR(d - 1, false); return Next();
                case OpCode.IsOfType:
                {
                    Need(1);
                    string typeName = chunk.Constants[ins.A[0]].AsString();
                    string test = typeName switch
                    {
                        "bool" => "K_Bool", "int" => "K_Int", "float" => "K_Float", "char" => "K_Char", "string" => "K_String", "undefined" => "K_Undefined", "class" => "K_Class", _ => "",
                    };
                    if (test.Length > 0) E($"{S(d - 1)} = Bool({S(d - 1)}.kind == {test});");
                    else { _isOfTypes.Add(typeName); E($"{S(d - 1)} = Bool(isof_{Mangle(typeName)}({S(d - 1)}));"); }
                    SetR(d - 1, false); return Next();
                }
                case OpCode.IsFrom:
                    Need(2); E($"{S(d - 2)} = Bool(isFrom({S(d - 2)}, {S(d - 1)}, {(ins.A[0] != 0 ? "true" : "false")}));"); d--; SetR(d - 1, false); return Next();
                case OpCode.Neg: return Unary("negate");
                case OpCode.LogicalNot: return Unary("lnot");
                case OpCode.BitNot: return Unary("bitNot");

                case OpCode.Eq: return Compare("eq", false, "operator==");
                case OpCode.NotEq: return Compare("eq", true, "operator!=");
                case OpCode.Lt: return Compare("lt", false, "operator<");
                case OpCode.LtEq: return Compare("le", false, "operator<=");
                case OpCode.Gt: return Compare("gt", false, "operator>");
                case OpCode.GtEq: return Compare("ge", false, "operator>=");

                case OpCode.Jump:
                {
                    int target = ins.A[0];
                    var targetRegion = RegionAt(st.Region, target);
                    if (ReferenceEquals(targetRegion, st.Region)) { E($"goto L{target};"); return new StepResult(false, target); }
                    // a jump out of a catch block: the catch lambda ends, and the function that owns the `try` continues at the target
                    for (var r = st.Region; r != targetRegion; r = r!.Outer) r!.ExitLabels.Add(target);
                    // the operand slots of the catch code travel in registers of the unwinding state: the frames that unwind first overwrite the
                    // slots of the function that owns the `try` (the result of the call that threw), the landing site puts them back
                    var outermost = st.Region!;
                    while (!ReferenceEquals(outermost.Outer, targetRegion)) outermost = outermost.Outer!;
                    if (d - outermost.DepthBase > 4) throw new NativeNotSupportedException($"a jump out of a catch block with {d - outermost.DepthBase} operands ({fn} at {ins.Addr})");
                    for (int k = outermost.DepthBase; k < d; k++) E($"g_unwind.regs[{k - outermost.DepthBase}] = {SlotName(st.Region, k)};");
                    E($"exitJump(&H{st.Region!.Template}, {target}); return;");
                    var exitState = st.Clone();
                    exitState.Region = targetRegion;
                    return new StepResult(false, null, new List<(int, Flow)> { (target, exitState) });
                }
                case OpCode.JumpIfFalse:
                    Need(1); E($"if (!truthy({S(d - 1)})) goto L{ins.A[0]};"); d--; st.Depth = d;
                    return new StepResult(true, ins.A[0]);
                case OpCode.JumpIfFalsePeek:
                    Need(1); E($"if (!truthy({S(d - 1)})) goto L{ins.A[0]};");
                    return new StepResult(true, ins.A[0]);
                case OpCode.JumpIfTruePeek:
                    Need(1); E($"if (truthy({S(d - 1)})) goto L{ins.A[0]};");
                    return new StepResult(true, ins.A[0]);
                case OpCode.JumpIfNotLt: return JumpIfNot("lt", false, "operator<");
                case OpCode.JumpIfNotLtEq: return JumpIfNot("le", false, "operator<=");
                case OpCode.JumpIfNotGt: return JumpIfNot("gt", false, "operator>");
                case OpCode.JumpIfNotGtEq: return JumpIfNot("ge", false, "operator>=");
                case OpCode.JumpIfNotEq: return JumpIfNot("eq", false, "operator==");
                case OpCode.JumpIfNotNotEq: return JumpIfNot("eq", true, "operator!=");

                case OpCode.EnterScope:
                {
                    int parentId = st.Scopes[^1].Id;
                    if (_usesTakeUpwards)
                    {
                        // TakeUpwards moves a value to the parent scope: both scopes need a list, the child knows the parent
                        f.NeedsList.Add(ins.Addr);
                        f.NeedsList.Add(parentId);
                    }
                    if (f.NeedsList.Contains(ins.Addr))
                    {
                        E($"{ListName(ins.Addr)}.mark = poolMark();");
                        if (_usesTakeUpwards) E($"{ListName(ins.Addr)}.parent = &{ListName(parentId)};");
                    }
                    st.Scopes.Add((ins.Addr, 0)); return Next();
                }
                case OpCode.ExitScope:
                {
                    if (st.Scopes.Count <= 1) throw new NativeNotSupportedException($"ExitScope without scope at {ins.Addr} in {fn}");
                    int id = st.Scopes[^1].Id;
                    // a catch block that leaves scopes of its function (break/continue): the function that owns the `try` leaves them, after the unwinding
                    if (st.Scopes.Count - 1 >= (st.Region?.ScopeBase ?? 0))
                    {
                        if (f.NeedsList.Contains(id)) E($"leave(&{ListName(id)});");
                        ReleaseScopeVariables(id, reset: true);
                    }
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
                        Check();
                        SetR(d - 1, false);
                        return Next();
                    }
                    if (native == StringMethods.NativeName && argc is >= 2 and <= 4)
                    {
                        int first = d - argc;
                        string a0 = argc > 2 ? S(first + 2) : "Undef()", a1 = argc > 3 ? S(first + 3) : "Undef()";
                        E($"{S(first)} = stringCall({S(first)}.i, {S(first + 1)}, {argc - 2}, {a0}, {a1}, &{OwnerList()});");
                        Check();
                        d = first + 1; SetR(first, true); return Next();
                    }
                    if (native == CharMethods.NativeName && argc == 2)
                    {
                        int first = d - 2;
                        E($"{S(first)} = charCall({S(first)}.i, {S(first + 1)}, &{OwnerList()});");
                        d = first + 1; SetR(first, true); return Next();
                    }
                    if (native.StartsWith("__refl_") && ReflectionNatives.Contains(native))
                    {
                        UseReflection();
                        if (native is "__refl_probe" or "__refl_silence" or "__refl_silence_handle") UseProbes();
                        int first = d - argc;
                        string callerArg = "g_reflCaller";
                        string list = "&" + OwnerList();
                        string call = native switch
                        {
                            "__refl_class_name" when argc == 1 => $"rf_class_name({S(first)}, {list})",
                            "__refl_class_info" when argc == 1 => $"rf_class_info({S(first)}, {list})",
                            "__refl_members" when argc == 1 => $"rf_members({S(first)}, {list})",
                            "__refl_classes" when argc == 0 => $"rf_classes({list})",
                            "__refl_is_sub" when argc == 2 => $"rf_is_sub({S(first)}, {S(first + 1)})",
                            "__refl_get" when argc == 2 => $"rf_get({S(first)}, {S(first + 1)}, {list}, {callerArg})",
                            "__refl_set" when argc == 3 => $"rf_set({S(first)}, {S(first + 1)}, {S(first + 2)}, {list}, {callerArg})",
                            "__refl_call" when argc == 3 => $"rf_call({S(first)}, {S(first + 1)}, {S(first + 2)}, {list}, {callerArg})",
                            "__refl_new" when argc == 2 => $"rf_new({S(first)}, {S(first + 1)}, {list}, {callerArg})",
                            "__refl_has" when argc == 2 => $"rf_has({S(first)}, {S(first + 1)})",
                            "__refl_selector_path" when argc == 1 => $"rf_selector_path({S(first)}, {list})",
                            "__refl_member_kind" when argc == 2 => $"rf_member_kind({S(first)}, {S(first + 1)}, {list})",
                            "__refl_probe" when argc == 4 => $"rf_probe({S(first)}, {S(first + 1)}, {S(first + 2)}, {S(first + 3)}, {list}, {callerArg})",
                            "__refl_silence" when argc == 2 => $"rf_silence({S(first)}, {S(first + 1)})",
                            "__refl_silence_handle" when argc == 1 => $"rf_silence_handle({S(first)})",
                            _ => throw new NativeNotSupportedException($"native function '{native}' with {argc} argument(s) (called in {fn})"),
                        };
                        E($"{S(first)} = {call};");
                        Check();
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
                    if (rc.FindMethodWithAccess(method, argc) is { } staticFound)
                        AccessCheck(staticFound.DeclaringClass!, staticFound.Access, $"Static method '{method}' of '{staticFound.DeclaringClass!.Name}' is {DescribeAccess(staticFound.Access)} and cannot be called from here.");
                    var target = GetFunc(proto, FuncKind.Static);
                    if (ReflectionPrelude.HelperClasses.Contains(cls)) E(ReflectionCallerAssign(f).TrimEnd());
                    long mask = ArgMask();
                    ArgsBefore(d - argc, argc, mask);
                    var (dpre, dargs) = DefaultArgs(proto, argc, "Undef()", "&" + OwnerList());
                    E($"{(dpre.Length > 0 ? "{ " + dpre : "")}{S(d - argc)} = {target.Name}({(CallArgs(d - argc, argc, mask, proto.RefMask) + dargs).TrimStart(',', ' ')});{(dpre.Length > 0 ? " }" : "")}");
                    ArgsAfter(argc, mask, S(d - argc));
                    Check();
                    AdoptResult(d - argc);
                    d = d - argc + 1; return Next();
                }
                case OpCode.Return:
                {
                    Need(1);
                    if (f.Kind == FuncKind.Main) throw new NativeNotSupportedException("return in top-level code");
                    bool isCtor = f.Kind == FuncKind.Ctor;
                    bool anything = st.Scopes.Any(sc => f.NeedsList.Contains(sc.Id) || ScopeVariables(f, sc.Id, locals).Any(v => VarRef(VarKey(f, v))));
                    bool tryActive = st.Region != null || st.Handlers.Count > 0;
                    if (!isCtor && !tryActive && !R(d - 1) && !anything) { E($"return {S(d - 1)};"); return new StepResult(false, null); }
                    // the value is on its way to the caller: it keeps a count, and an object that the leaving scopes owned goes along
                    E($"{{ Value r = {S(d - 1)}; (void)r;");
                    var edges = ReturnSeq(f, st, R(d - 1), true, sb == null ? null : x => E("  " + x), locals);
                    E("}");
                    return new StepResult(false, null, edges);
                }
                case OpCode.Halt:
                    if (f.Kind != FuncKind.Main) throw new NativeNotSupportedException("Halt in a function");
                    HaltCode(f, E);
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
                    RequireSelf(); E($"{S(d)} = self;"); SetR(d, IsExtensionMethod(f.Proto)); d++; return Next();
                case OpCode.GetField:
                {
                    Need(1);
                    string field = Str(ins.A[0]);
                    _fieldNames.Add(field);
                    if (HasProperty(field) || RestrictedField(field)) { E($"{S(d - 1)} = gf_{Mangle(field)}({S(d - 1)}{FieldTail(field, f, () => "&" + OwnerList())});"); Check(); }
                    else E($"{S(d - 1)} = gf_{Mangle(field)}({S(d - 1)});");
                    SetR(d - 1, true);
                    return Next();
                }
                case OpCode.SetField:
                {
                    Need(2);
                    string field = Str(ins.A[0]);
                    _fieldNames.Add(field);
                    E($"sf_{Mangle(field)}({S(d - 2)}, {S(d - 1)}{FieldTail(field, f, () => "&" + OwnerList())});");
                    Check();
                    E($"{S(d - 2)} = {S(d - 1)};");
                    SetR(d - 2, R(d - 1));
                    d--; return Next();
                }
                case OpCode.SetFieldOnThis:
                {
                    Need(1); RequireSelf();
                    string field = Str(ins.A[0]);
                    _fieldNames.Add(field);
                    E($"sf_{Mangle(field)}(self, {S(d - 1)}{FieldTail(field, f, () => "&" + OwnerList())});");
                    Check();
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
                    AccessCheck(cls.Rc, ctor.Access ?? AccessModifier.Public, $"Constructor of '{cls.Rc.Name}' is {DescribeAccess(ctor.Access ?? AccessModifier.Public)} and cannot be called from here.");
                    var target = GetFunc(ctor, FuncKind.Ctor);
                    int slot = d - argc - (owned ? 1 : 0);
                    string ownerExpr = owned ? $"&asObj({S(slot)})->owned" : "&" + OwnerList();
                    long mask = ArgMask();
                    ArgsBefore(d - argc, argc, mask);
                    var (dpre, dargs) = DefaultArgs(ctor, argc, "o", "&" + OwnerList());
                    E($"{{ Value o = newObject({cls.Id}, {cls.Fields.Count}, {ownerExpr}); {dpre}{target.Name}(o{CallArgs(d - argc, argc, mask, ctor.RefMask)}{dargs}); {S(slot)} = o; }}");
                    ArgsAfter(argc, mask, S(slot));
                    Check();
                    SetR(slot, false);
                    d = slot + 1; return Next();
                }
                case OpCode.ConstructBase:
                {
                    int argc = ins.A[1];
                    Need(argc); RequireSelf();
                    var rc = FindClass(Str(ins.A[0]));
                    var ctor = rc.FindConstructor(argc) ?? throw new NativeNotSupportedException($"class {rc.Name} has no constructor with {argc} argument(s)");
                    var target = GetFunc(ctor, FuncKind.Ctor);
                    OwnerList();
                    long mask = ArgMask();
                    ArgsBefore(d - argc, argc, mask);
                    var (dpre, dargs) = DefaultArgs(ctor, argc, "self", "&" + OwnerList());
                    E($"{(dpre.Length > 0 ? "{ " + dpre : "")}{target.Name}(self{CallArgs(d - argc, argc, mask, ctor.RefMask)}{dargs});{(dpre.Length > 0 ? " }" : "")}");
                    ArgsAfter(argc, mask, "Undef()");
                    Check();
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
                    Check();
                    AdoptResult(slot);
                    d = slot + 1; return Next();
                }
                case OpCode.CallMethod:
                {
                    int argc = ins.A[1];
                    Need(argc + 1);
                    string method = Str(ins.A[0]);
                    string owner = OwnerList();
                    int slot = d - argc - 1;
                    if (OwnMethodId(method, argc) is { } ownMethodId && !AnyClassHasMethod(method, argc))
                    {
                        // the built-in ownership methods of objects, arrays and buffers (a class that declares one itself takes precedence)
                        if (ownMethodId == "OM_TakeUpwards" && !_usesTakeUpwards) { _usesTakeUpwards = true; _usesGlobalOwn = true; _version++; }
                        if (ownMethodId == "OM_TakeGlobal" && !_usesGlobalOwn) { _usesGlobalOwn = true; _version++; }
                        E($"ownMethod({ownMethodId}, {S(slot)}, {(argc == 1 ? S(slot + 1) : "Undef()")}, &{owner});");
                        E($"{S(slot)} = Undef();");
                        SetR(slot, false);
                        d = slot + 1; return Next();
                    }
                    _dispatchers.Add((method, argc));
                    if (Enumerable.Range(0, Math.Min(argc, 16)).Any(i => (ArgMask() >> (4 * i) & 15) == 3)) _refCallSites.Add((method, argc));
                    string extra = DispatchTail(method, argc, "&" + owner, CallerId(f));
                    long dmask = ArgMask();
                    if (_usesReflection && ReflectionMethodNames.Contains(method)) E(ReflectionCallerAssign(f).TrimEnd());
                    ArgsBefore(d - argc, argc, dmask);
                    E($"{S(slot)} = call_{Mangle(method)}_{argc}({S(slot)}{Args(d - argc, argc)}{extra});");
                    ArgsAfter(argc, dmask, S(slot));
                    Check();
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
                    if (rc.FindMethodWithAccess(method, argc) is { Proto: not null } baseFound)
                        AccessCheck(baseFound.DeclaringClass!, baseFound.Access, $"Method '{method}' of '{baseFound.DeclaringClass!.Name}' is {DescribeAccess(baseFound.Access)} and cannot be called from here.");
                    var target = GetFunc(proto, FuncKind.Method);
                    long mask = ArgMask();
                    ArgsBefore(d - argc, argc, mask);
                    var (dpre, dargs) = DefaultArgs(proto, argc, "self", "&" + OwnerList());
                    E($"{(dpre.Length > 0 ? "{ " + dpre : "")}{S(d - argc)} = {target.Name}(self{CallArgs(d - argc, argc, mask, proto.RefMask)}{dargs});{(dpre.Length > 0 ? " }" : "")}");
                    ArgsAfter(argc, mask, S(d - argc));
                    Check();
                    AdoptResult(d - argc);
                    d = d - argc + 1; return Next();
                }
                case OpCode.GetStaticField:
                {
                    // no static field of that name: a static property (`get_Name`, SPEC 8.8)
                    if (FindClass(Str(ins.A[0])) is { } getRc && getRc.FindStaticFieldOwner(Str(ins.A[1])) == null
                        && getRc.FindMethodWithAccess("get_" + Str(ins.A[1]), 0).Proto is { IsStatic: true } staticGetter)
                    {
                        E($"{S(d)} = {GetFunc(staticGetter, FuncKind.Static).Name}();");
                        Check();
                        AdoptResult(d);
                        d++; return Next();
                    }
                    if (FindClass(Str(ins.A[0])).FindFieldAccess(Str(ins.A[1])) is { } getAccess)
                        AccessCheck(getAccess.DeclaringClass, getAccess.Access, $"Static field '{Str(ins.A[1])}' of '{getAccess.DeclaringClass.Name}' is {DescribeAccess(getAccess.Access)} and cannot be accessed from here.");
                    int index = StaticFieldIndex(Str(ins.A[0]), Str(ins.A[1]));
                    E($"{S(d)} = SF{index};");
                    SetR(d, VarRef($"SF{index}"));
                    d++; return Next();
                }
                case OpCode.SetStaticField:
                case OpCode.SetStaticFieldOnInit:
                {
                    Need(1);
                    if (FindClass(Str(ins.A[0])) is { } setRc && setRc.FindStaticFieldOwner(Str(ins.A[1])) == null
                        && setRc.FindMethodWithAccess("set_" + Str(ins.A[1]), 1).Proto is { IsStatic: true } staticSetter)
                    {
                        E($"{{ Value r = {GetFunc(staticSetter, FuncKind.Static).Name}({S(d - 1)}); adopt(r, &{OwnerList()}); }}");
                        Check();
                        return Next();
                    }
                    // (the initializer of a static field runs for its class, whatever the access)
                    if (ins.Op == OpCode.SetStaticField && FindClass(Str(ins.A[0])).FindFieldAccess(Str(ins.A[1])) is { } setAccess)
                        AccessCheck(setAccess.DeclaringClass, setAccess.Access, $"Static field '{Str(ins.A[1])}' of '{setAccess.DeclaringClass.Name}' is {DescribeAccess(setAccess.Access)} and cannot be accessed from here.");
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
                    var target = GetFunc(proto, FuncKind.Lambda, captures);
                    string defaultsInit = "";
                    if (proto.SelectorPath is { } selectorPath)
                    {
                        UseReflection();
                        if (!_selectorPaths.TryGetValue(selectorPath, out var selName)) { selName = $"kSel{_selectorPaths.Count}"; _selectorPaths[selectorPath] = selName; _version++; }
                        defaultsInit = $" l->sel = {selName}; l->nsel = {selectorPath.Length};";
                    }
                    if (proto.ParamDefaults.Any(p => p != null))
                    {
                        // the default values are functions of their own (with the `on` target as `this`); the lambda knows how to reach them
                        foreach (var dp in proto.ParamDefaults) if (dp != null) GetFunc(dp, FuncKind.Init);
                        int required = 0;
                        while (required < proto.ParamCount && (required >= proto.ParamDefaults.Count || proto.ParamDefaults[required] == null)) required++;
                        defaultsInit += $" l->nreq = {required}; l->dflt = {target.Name}_d;";
                        _lambdaDefaults.Add(target);
                    }
                    int first = d - captures - (hasOn ? 1 : 0);
                    var sbCaps = new StringBuilder();
                    for (int i = 0; i < captures; i++) sbCaps.Append($" l->caps()[{i}] = {S(first + i)}; retain({S(first + i)});");
                    E($"{{ Lam* l = allocLam({proto.ParamCount}, {captures}, {target.Name}_t, &{OwnerList()}); l->on = {(hasOn ? S(d - 1) : "Undef()")};{defaultsInit}{sbCaps} {S(first)} = LamV(l); }}");
                    d = first + 1; SetR(first, true); return Next();
                }
                case OpCode.Call:
                {
                    int argc = ins.A[0];
                    Need(argc + 1);
                    int slot = d - argc - 1;
                    string owner = OwnerList();
                    var argList = argc == 0 ? "Undef()" : string.Join(", ", Enumerable.Range(slot + 1, argc).Select(S));
                    E($"{{ Value args[{Math.Max(argc, 1)}] = {{{argList}}}; {S(slot)} = callLam({S(slot)}, {argc}, args, &{owner}); }}");
                    Check();
                    AdoptResult(slot);
                    d = slot + 1; return Next();
                }
                case OpCode.CheckLambdaSignature:
                    Need(1); E($"checkLambda({S(d - 1)}, {ins.A[0]});"); return Next();
                case OpCode.CheckUnit:
                {
                    Need(1);
                    string unitText = Str(ins.A[0]);
                    E($"checkUnit({S(d - 1)}, {UnitId(Unit.Parse(unitText))}, {Constant(chunk.Constants[ins.A[0]])});");
                    Check();
                    return Next();
                }

                // ---------------------------------------------------------------------------------------------------
                // Strings, arrays, buffers
                // ---------------------------------------------------------------------------------------------------
                case OpCode.FormatValue:
                {
                    Need(1);
                    string spec = Str(ins.A[0]);
                    if (spec.Length > 0 && "XDBFExdbfe".IndexOf(spec[0]) < 0)
                        throw new NativeNotSupportedException($"format specifier '{spec}' (supported: X, D, B, F, E)");
                    E($"{S(d - 1)} = formatValue({S(d - 1)}, {Constant(chunk.Constants[ins.A[0]])}, &{OwnerList()});");
                    Check();
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
                case OpCode.MakeArrayLiteralParts:
                {
                    int count = ins.A[0];
                    uint mask = (uint)ins.A[1] | ((uint)ins.A[2] << 16);
                    Need(count);
                    int first = d - count;
                    var sbItems = new StringBuilder();
                    for (int i = 0; i < count; i++) sbItems.Append($" a->items()[{i}] = {S(first + i)}; retain({S(first + i)});");
                    // an array made inside the literal belongs to the outer array
                    var parts = new StringBuilder();
                    for (int i = 0; i < count; i++)
                        if ((mask >> i & 1) != 0) parts.Append($" attachPart(ArrV(a), a->items()[{i}]);");
                    E($"{{ Arr* a = allocArr({count}, &{OwnerList()});{sbItems}{parts} {S(first)} = ArrV(a); }}");
                    d = first + 1; SetR(first, true); return Next();
                }
                case OpCode.NewJagged:
                {
                    int ranks = ins.A[0];
                    Need(ranks);
                    int first = d - ranks;
                    var sizes = string.Join(", ", Enumerable.Range(first, ranks).Select(i => $"sizeArg({S(i)})"));
                    E($"{{ const int64_t sz[{ranks}] = {{{sizes}}}; {S(first)} = newJagged(sz, {ranks}, &{OwnerList()}); }}");
                    d = first + 1; SetR(first, true); return Next();
                }
                case OpCode.HoistValue:
                {
                    Need(1);
                    string target = ListName(st.Scopes[0].Id);
                    f.NeedsList.Add(st.Scopes[0].Id);
                    for (int i = 1; i < st.Scopes.Count; i++)
                    {
                        f.NeedsList.Add(st.Scopes[i].Id);
                        E($"hoistFrom({S(d - 1)}, &{ListName(st.Scopes[i].Id)}, &{target});");
                    }
                    return Next();
                }
                case OpCode.OwnValue:
                    Need(2);
                    foreach (var sc in st.Scopes) f.NeedsList.Add(sc.Id);
                    E($"{S(d - 2)} = ownValue({S(d - 2)}, {S(d - 1)}{string.Concat(st.Scopes.Select(sc => ", &" + ListName(sc.Id)))});");
                    SetR(d - 2, true);
                    d--; return Next();
                case OpCode.Delete:
                    Need(1);
                    E($"deleteValue({S(d - 1)});");
                    d--; return Next();
                case OpCode.ArrayGet:
                    Need(2); E($"{S(d - 2)} = aget_g({S(d - 2)}, {S(d - 1)}, &{OwnerList()});"); Check(); d--; SetR(d - 1, true); return Next();
                case OpCode.ArraySet:
                    Need(3); E($"aset_g({S(d - 3)}, {S(d - 2)}, {S(d - 1)}, &{OwnerList()});"); Check(); E($"{S(d - 3)} = {S(d - 1)};"); SetR(d - 3, R(d - 1)); d -= 2; return Next();
                case OpCode.IncDecIndex:
                {
                    Need(2);
                    bool increment = ins.A[0] != 0, prefix = ins.A[1] != 0;
                    string list = "&" + OwnerList();
                    string guard = _usesExceptions ? $"if (FIRE_UNLIKELY(g_unwind.active)) goto {(sb == null ? "X" : ExitLabel(f, st, locals))}; " : "";
                    E($"{{ Value o = aget_g({S(d - 2)}, {S(d - 1)}, {list}); {guard}Value n = {(increment ? "add" : "sub")}(o, Int(1)); aset_g({S(d - 2)}, {S(d - 1)}, n, {list}); {S(d - 2)} = {(prefix ? "n" : "o")}; }}");
                    Check();
                    d--; SetR(d - 1, false); return Next();
                }

                // ---------------------------------------------------------------------------------------------------
                // `ref` parameters and pointers to variables, fields and elements (SPEC 5.4.2)
                // ---------------------------------------------------------------------------------------------------
                case OpCode.Probe:
                {
                    Need(2); UseProbes();
                    string member = (ins.A[1] & 2) != 0 ? "nullptr" : CString(Str(ins.A[0]));
                    E($"{{ char err[500]; int64_t id = rf_probeAdd({S(d - 2)}, {member}, {((ins.A[1] & 1) != 0 ? "true" : "false")}, {S(d - 1)}, err, sizeof err); if (id < 0) fatal(err); {S(d - 2)} = Int(id); }}");
                    d--; SetR(d - 1, false); return Next();
                }
                case OpCode.SilenceMember:
                {
                    Need(1); UseProbes();
                    string member = ins.A[1] != 0 ? "nullptr" : CString(Str(ins.A[0]));
                    E($"{{ char err[500]; if (!rf_silenceCore({S(d - 1)}, {member}, err, sizeof err)) fatal(err); }}");
                    d--; return Next();
                }
                case OpCode.SilenceValue:
                    Need(1); UseProbes();
                    E($"{{ char err[500]; if (!rf_silenceValue({S(d - 1)}, err, sizeof err)) fatal(err); }}");
                    d--; return Next();
                case OpCode.CopyValue:
                    Need(1);
                    E($"{S(d - 1)} = copyValue({S(d - 1)}, {((ins.A[0] & 1) != 0 ? "true" : "false")}, &{OwnerList()});");
                    Check();
                    return Next();
                case OpCode.CopyValueOwned:
                    Need(2);
                    E($"{S(d - 2)} = copyOwned({S(d - 2)}, {S(d - 1)}, {((ins.A[0] & 1) != 0 ? "true" : "false")});");
                    Check();
                    d--; SetR(d - 1, R(d)); return Next();
                case OpCode.CopyArgs:
                    return Next();
                case OpCode.AddressOfLocal:
                {
                    string v = Var(st, ins.A[0], ins.A[1], locals);
                    MarkVarRef(VarKey(f, v));   // what is written through the pointer is counted by the variable
                    E($"{S(d)} = PtrV(&{v});");
                    SetR(d, false); d++; return Next();
                }
                case OpCode.AddressOfGlobal:
                    _globals.Add(ins.A[0]); MarkVarRef($"G{ins.A[0]}");
                    E($"{S(d)} = PtrV(&G{ins.A[0]});");
                    SetR(d, false); d++; return Next();
                case OpCode.AddressOfField:
                {
                    Need(1);
                    string field = Str(ins.A[0]);
                    _fieldNames.Add(field);
                    E($"{S(d - 1)} = fp_{Mangle(field)}({S(d - 1)});");
                    SetR(d - 1, false);
                    return Next();
                }
                case OpCode.AddressOfIndex:
                    Need(2); E($"{S(d - 2)} = addressOfIndex({S(d - 2)}, {S(d - 1)});"); Check(); d--; SetR(d - 1, false); return Next();
                case OpCode.PtrRead:
                    Need(1); E($"{S(d - 1)} = ptrRead({S(d - 1)});"); SetR(d - 1, true); return Next();
                case OpCode.PtrWrite:
                    Need(2);
                    E($"ptrWrite({S(d - 2)}, {S(d - 1)});");
                    E($"{S(d - 2)} = {S(d - 1)};");
                    SetR(d - 2, R(d - 1));
                    d--; return Next();
                case OpCode.RequireRefParam:
                    E($"requireRef({Var(st, 0, ins.A[0], locals)}, {CString(chunk.Constants[ins.A[1]].AsString())});");
                    return Next();

                // ---------------------------------------------------------------------------------------------------
                // Exceptions
                // ---------------------------------------------------------------------------------------------------
                case OpCode.RegisterHandler:
                {
                    UseExceptions();
                    int t = ins.A[0];
                    var region = GetRegion(f, t, st);
                    if (_usesTakeUpwards && region.Catches.Count > 0) { f.NeedsList.Add(region.CatchScopeId); f.NeedsList.Add(st.Scopes[^1].Id); }
                    E(region.Catches.Count > 0 ? $"H{t}.fn = handlerTrampoline<decltype(C{t})>; H{t}.ctx = &C{t};" : $"H{t}.fn = nullptr; H{t}.ctx = nullptr;");
                    E($"H{t}.info = &HI_{fn}_{t}; pushHandler(&H{t});");
                    var edges = new List<(int, Flow)>();
                    foreach (var (_, addr) in region.Catches)
                    {
                        var catchFlow = st.Clone();
                        catchFlow.Region = region;
                        if (region.HasFinally) catchFlow.Handlers.Add(new HEntry(region, true));
                        catchFlow.Scopes.Add((region.CatchScopeId, 1));
                        edges.Add((addr, catchFlow));
                    }
                    if (region.HasFinally)
                    {
                        // an exception that this `try` does not catch arrives at its `finally`
                        var finallyFlow = st.Clone();
                        finallyFlow.Depth = d + 2;
                        edges.Add((region.FinallyAddr!.Value, finallyFlow));
                    }
                    st.Handlers.Add(new HEntry(region, false));
                    st.Depth = d;
                    return new StepResult(true, null, edges);
                }
                case OpCode.UnregisterHandler:
                    if (st.Handlers.Count == 0) throw new NativeNotSupportedException($"UnregisterHandler without handler at {ins.Addr} in {fn}");
                    st.Handlers.RemoveAt(st.Handlers.Count - 1);
                    E("popHandler();");
                    return Next();
                case OpCode.Throw:
                    UseExceptions(); Need(1);
                    E($"{S(d - 1)} = throwValue({S(d - 1)});");
                    Check();
                    E($"adopt({S(d - 1)}, &{OwnerList()});");
                    SetR(d - 1, true);
                    return Next();
                case OpCode.ResumeException:
                    UseExceptions(); Need(2);
                    E($"resumeThrow({S(d - 2)}, {S(d - 1)});");
                    if (sb != null) E($"goto {ExitLabel(f, st, locals)};");
                    E($"{S(d - 2)} = Undef();");
                    d--; SetR(d - 1, false); return Next();
                case OpCode.ClearPendingResume:
                    Need(1); E($"clearPending({S(d - 1)});"); d--; return Next();
                case OpCode.EnterFinallyNormal:
                    E($"{S(d)} = Undef(); {S(d + 1)} = Int(0);");
                    SetR(d, false); SetR(d + 1, false);
                    d += 2; return Next();
                case OpCode.PushJump:
                {
                    var next = f.Decoded[f.IndexOf[ins.Addr] + 1];
                    if (next.Op != OpCode.Jump) throw new NativeNotSupportedException($"PushJump without Jump at {ins.Addr} in {fn}");
                    if (!f.FinJumps.TryGetValue(next.A[0], out var jumps)) f.FinJumps[next.A[0]] = jumps = new SortedSet<int>();
                    jumps.Add(ins.A[0]);
                    E($"{S(d)} = Int({ins.A[0]}); {S(d + 1)} = Int(3);");
                    SetR(d, false); SetR(d + 1, false);
                    d += 2; return Next();
                }
                case OpCode.EndFinally:
                {
                    Need(2);
                    if (st.Fins.Count == 0) throw new NativeNotSupportedException($"EndFinally outside of a finally block at {ins.Addr} in {fn}");
                    int fin = st.Fins[^1];
                    string payload = S(d - 2), kind = S(d - 1);
                    bool payloadRef = R(d - 2);
                    var after = st.Clone();
                    after.Fins.RemoveAt(after.Fins.Count - 1);
                    after.Depth = d - 2;
                    var edges = new List<(int, Flow)>();
                    E($"switch ({kind}.i) {{");
                    E("case 0: break;");
                    E($"case 1: throwValue({payload}, false);");
                    if (sb != null && _usesExceptions) E($"    goto {ExitLabel(f, after, locals)};");
                    else E("    break;");
                    if (f.FinReturn.Contains(fin))
                    {
                        E("case 2: {");
                        E($"    Value r = {payload}; (void)r;");
                        edges.AddRange(ReturnSeq(f, after, payloadRef, false, sb == null ? null : x => E("    " + x), locals));
                        E("}");
                    }
                    if (f.FinJumps.TryGetValue(fin, out var targets))
                    {
                        E($"case 3: switch ({payload}.i) {{");
                        foreach (int target in targets)
                        {
                            E($"    case {target}: goto L{target};");
                            edges.Add((target, after));
                        }
                        E("    default: break;");
                        E("    }");
                        E("    break;");
                    }
                    E("default: break;");
                    E("}");
                    st.Fins.RemoveAt(st.Fins.Count - 1);
                    d -= 2;
                    st.Depth = d;
                    return new StepResult(true, null, edges);
                }

                default:
                    throw new NativeNotSupportedException($"opcode {ins.Op} (in {fn} at {ins.Addr})");
            }

            StepResult Binary(string fnName, string opMethod)
            {
                Need(2);
                if (UseOperator(opMethod))
                {
                    E($"{S(d - 2)} = {OperatorWrapper(opMethod)}({S(d - 2)}, {S(d - 1)}, &{OwnerList()});");
                    Check(); d--; SetR(d - 1, true); return Next();
                }
                E($"{S(d - 2)} = {fnName}({S(d - 2)}, {S(d - 1)});"); d--; SetR(d - 1, false); return Next();
            }
            StepResult Unary(string fnName)
            {
                Need(1); E($"{S(d - 1)} = {fnName}({S(d - 1)});"); SetR(d - 1, false); return Next();
            }
            StepResult Compare(string fnName, bool negate, string opMethod)
            {
                Need(2);
                if (UseOperator(opMethod))
                {
                    E($"{S(d - 2)} = {OperatorWrapper(opMethod)}({S(d - 2)}, {S(d - 1)}, &{OwnerList()});");
                    Check(); d--; SetR(d - 1, true); return Next();
                }
                E($"{S(d - 2)} = Bool({(negate ? "!" : "")}{fnName}({S(d - 2)}, {S(d - 1)}));"); d--; SetR(d - 1, false); return Next();
            }
            StepResult JumpIfNot(string fnName, bool negate, string opMethod)
            {
                Need(2);
                if (UseOperator(opMethod))
                {
                    // the overload can return anything: a value that is true or false decides
                    E($"{{ Value t = {OperatorWrapper(opMethod)}({S(d - 2)}, {S(d - 1)}, &{OwnerList()});");
                    if (_usesExceptions && sb != null) E($"  if (FIRE_UNLIKELY(g_unwind.active)) goto {ExitLabel(f, st, locals)};");
                    E($"  if (!truthy(t)) goto L{ins.A[0]}; }}");
                    d -= 2; st.Depth = d;
                    return new StepResult(true, ins.A[0]);
                }
                E($"if (!({(negate ? "!" : "")}{fnName}({S(d - 2)}, {S(d - 1)}))) goto L{ins.A[0]};");
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
