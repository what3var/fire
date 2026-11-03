using System.Globalization;
using System.Text;
using fire.Bytecode;
using fire.Runtime;
using fire.Values;
using fire.Ast;

namespace fire.Native
{
    /// <summary>
    /// Every module serves its own classes (ROADMAP 9, phase B2; docs/NATIVE_BACKEND.md "Getrennte Übersetzung"): with modules (linkage "static", FIRE_MODULES=1)
    /// each module - a library or the program - generates the dispatchers, field helpers, `is of`, operators, class scores and type arguments of objects for
    /// the call sites of its own code, switching over its own classes only (a library numbers them from 0: `cls - fire_mb_M`). An object of a class the module
    /// does not serve - of another module, a class of the program derived from a class of a library, later a plugin - goes through the runtime: every module
    /// writes a table of the members each of its classes declares (FireMembers in fire_rt.h: methods by name and number of arguments, fields by name), the
    /// program hangs them into the class descriptors (its class first, then its bases), and `fireFindMethod`/`fireGetField`/... walk them. A row of a table calls
    /// the module's dispatcher for that class (`disp_X_n(k, ...)`), so overloads, default values and access checks work as for its own calls.
    ///
    /// The program does not generate anything for the classes of the libraries any more; the C it compiles on every build is its own code, its tables and the
    /// runtime. Its tables list the members that some module calls by name (the libraries' needs), not all.
    /// </summary>
    public sealed partial class CppGenerator
    {
        // ---- the names of the helpers of this module (a library's carry its prefix: they are its own) ----------------------------------------
        private string CallFn(string name, int argc, string prefix = "call_") => $"{Px}{prefix}{Mangle(name)}_{argc}";
        private string FieldFn(string prefix, string field) => $"{Px}{prefix}_{Mangle(field)}";
        private string IsOfFn(string typeName) => $"{Px}isof_{Mangle(typeName)}";
        private string OvdFn(string name) => $"{Px}ovd_{Mangle(name)}";
        private string TArgOfFn(string cls, int k) => $"{Px}targOf_{Mangle(cls)}_{k}";
        private string OperatorWrapper(string method) => Px + "ov_" + Mangle(method.Substring("operator".Length));

        /// <summary>The program with modules: the first id of its own classes (those below are the libraries').</summary>
        private int _progBase;
        /// <summary>Classes of other modules that this module adds members to (`class extends List` of linq; the program's own extensions of a library class): it
        /// serves them for those members (a library numbers them after its own classes, the program by their id).</summary>
        private List<ClassInfo>? _extServed;
        /// <summary>The program: the members the libraries call by name (their tables of the program's classes list these) and the fields they use by name.</summary>
        private readonly HashSet<(string Name, int Argc)> _libDispatchers = new();
        private readonly HashSet<string> _libFields = new(StringComparer.Ordinal);
        private readonly HashSet<string> _libFieldAddresses = new(StringComparer.Ordinal);
        /// <summary>The program's tables list every field and property of its classes (Reflect.Get/Set by any name; Reflect.Call: the methods that are there, by their
        /// dispatchers - see RegisterReflectionTargets).</summary>
        private bool _tablesAll;

        /// <summary>The module of this generator as a C name in exported symbols (`prog` for the program).</summary>
        private string ModId => _self?.Id ?? "prog";
        private string ModIdOf(RuntimeClass rc) => ModuleOf(rc)?.Id ?? "prog";

        /// <summary>The classes the helpers of this generator switch over: all (one file), with modules its own and those it extends.</summary>
        private IEnumerable<ClassInfo> Served => Modular ? _classList.Where(c => IsOwnClass(c.Rc)).Concat(ExtServed) : _classList;

        private List<ClassInfo> ExtServed
        {
            get
            {
                if (_extServed != null) return _extServed;
                _extServed = new List<ClassInfo>();
                if (!Modular) return _extServed;
                int next = _self?.Classes.Count ?? 0;
                foreach (var rc in _program.Program.Classes.Values.Where(c => !IsOwnClass(c)).OrderBy(c => c.Name, StringComparer.Ordinal))
                {
                    if (!ExtendsClass(rc, _self)) continue;
                    int id = LibraryRole ? next++ : _fixedIds != null && _fixedIds.TryGetValue(rc.Name, out int fixedId) ? fixedId : -1;
                    if (id < 0) continue;
                    _extServed.Add(new ClassInfo { Rc = rc, Id = id, IdText = LibraryRole ? ClassIdExpr(RegisterClass(rc.Name)) : null });
                }
                return _extServed;
            }
        }

        /// <summary>Does the module (null: the program) add members to the class of another module?</summary>
        private bool ExtendsClass(RuntimeClass rc, ModuleSpec? module) =>
            rc.Methods.Values.Any(list => list.Any(p => ProtoKey(p, out var owner, out _) && ReferenceEquals(owner, module)));

        /// <summary>The number of this module for the class of an object: a library's own classes are 0, 1, ... (`cls - fire_mb_M`), the program's are their ids.</summary>
        private string OwnLocal(string clsExpr) => LibraryRole ? $"({clsExpr} - {ModuleBase(_self!)})" : clsExpr;
        /// <summary>The test that a number of <see cref="OwnLocal"/> is a class of this module.</summary>
        private string OwnRangeTest(string k) => LibraryRole ? $"{k} < {_self!.Classes.Count}u" : $"{k} >= {_progBase}u";

        private static int RequiredArgs(FunctionProto p)
        {
            int required = p.ParamCount;
            while (required > 0 && required - 1 < p.ParamDefaults.Count && p.ParamDefaults[required - 1] != null) required--;
            return required;
        }

        /// <summary>Does the dispatcher of `name(argc)` have a case for the class (the same test as <see cref="Dispatcher"/>)?</summary>
        private static bool HasCase(RuntimeClass rc, string name, int argc) =>
            rc.IsTypeOverloaded(name, argc) ? rc.Candidates(name, argc).Protos.Any(p => !p.IsStatic) : rc.FindMethodWithAccess(name, argc).Proto is { IsStatic: false };

        /// <summary>Is the member of this module (a library: its own code; the program: code of the program)?</summary>
        private bool OwnsProto(FunctionProto p) => ProtoKey(p, out var owner, out _) && ReferenceEquals(owner, _self);

        private bool WantsField(string name) => LibraryRole || _tablesAll || _fieldNames.Contains(name) || _libFields.Contains(name) || _libFieldAddresses.Contains(name);

        /// <summary>Does the table of the program list the method (some module calls it by name)? A library lists all its members.</summary>
        private bool TableWants(string name, int argc) =>
            LibraryRole || _dispatchers.Contains((name, argc)) || _libDispatchers.Contains((name, argc))
            || argc == 0 && name.StartsWith("get_", StringComparison.Ordinal) && WantsField(name.Substring(4))
            || argc == 1 && name.StartsWith("set_", StringComparison.Ordinal) && WantsField(name.Substring(4));

        /// <summary>The methods of the table of a served class: those it declares in this module (not static), one row per number of arguments they take.</summary>
        private List<(string Name, int Argc, int Params)> TableMethodsOf(ClassInfo cls)
        {
            var result = new List<(string, int, int)>();
            var rc = cls.Rc;
            foreach (var (name, protos) in rc.Methods)
            {
                // (a private method only when the module's own code calls it by that name: on an object of a class derived from its class in another module)
                var mine = protos.Where(p => !p.IsStatic && OwnsProto(p) && (p.Access != AccessModifier.Private || !LibraryRole || KeepsPrivate(rc, name) || _dispatchers.Any(d => d.Name == name))).ToList();
                if (mine.Count == 0) continue;
                var counts = new SortedSet<int>();
                foreach (var p in mine) for (int argc = RequiredArgs(p); argc <= p.ParamCount; argc++) counts.Add(argc);
                foreach (int argc in counts)
                    if (HasCase(rc, name, argc) && TableWants(name, argc))
                        result.Add((name, argc, rc.FindMethodWithAccess(name, argc).Proto?.ParamCount ?? argc));
            }
            return result;
        }

        /// <summary>The fields of the table of a served class: those it declares itself (not static).</summary>
        private List<string> TableFieldsOf(ClassInfo cls)
        {
            if (!IsOwnClass(cls.Rc)) return new List<string>();
            return cls.Rc.OwnFieldInfo.Where(f => !f.Value.IsStatic && cls.Rc.FieldIndex.ContainsKey(f.Key) && WantsField(f.Key)).Select(f => f.Key).ToList();
        }

        /// <summary>The names the dispatchers of this module serve: its call sites and the rows of its tables.</summary>
        private SortedSet<(string Name, int Argc)> ModDispatchNames()
        {
            var names = new SortedSet<(string Name, int Argc)>(_dispatchers);
            foreach (var cls in Served) foreach (var (name, argc, _) in TableMethodsOf(cls)) names.Add((name, argc));
            return names;
        }

        private SortedSet<(string Name, int Argc)> TableMethodNames()
        {
            var names = new SortedSet<(string Name, int Argc)>();
            foreach (var cls in Served) foreach (var (name, argc, _) in TableMethodsOf(cls)) names.Add((name, argc));
            return names;
        }

        /// <summary>The names the field functions of this module serve: its call sites and the fields of its tables.</summary>
        private SortedSet<string> ModFieldNames()
        {
            var names = new SortedSet<string>(_fieldNames, StringComparer.Ordinal);
            foreach (var cls in Served) names.UnionWith(TableFieldsOf(cls));
            return names;
        }

        /// <summary>What the helpers of a module pull in (instead of <see cref="RegisterTargets"/>'s loops over every class): the members of its served classes
        /// for its dispatchers and fields, the program's own base classes (they have tables), the classes of the runtime's needs.</summary>
        private void RegisterServedTargets()
        {
            if (!LibraryRole)
                foreach (var cls in _classList.ToList())
                    for (var rc = cls.Rc.Base; rc != null; rc = rc.Base)
                        if (IsOwnClass(rc)) RegisterClass(rc.Name);
            // (`a[i]` on an object, `foreach` over an object: the dispatchers of every module)
            AddDispatcher("GetIndex", 1);
            AddDispatcher("SetIndex", 2);
            foreach (var (name, argc) in ModDispatchNames())
            {
                foreach (var cls in Served.ToList())
                {
                    var methods = MethodsFor(cls.Rc, name, argc);
                    bool overloaded = methods.Count > 1 && methods.Any(m => !m.IsStatic);
                    foreach (var method in methods)
                        if (!method.IsStatic) { GetFunc(method, FuncKind.Method); DefaultArgs(method, argc, "self", "list"); }
                        else if (overloaded) { GetFunc(method, FuncKind.Static); DefaultArgs(method, argc, "Undef()", "list"); }
                }
                foreach (var (_, _, extensionRc) in ExtensionMethods(name, argc))
                    foreach (var method in MethodsFor(extensionRc, name, argc))
                    {
                        GetFunc(method, FuncKind.Method);
                        DefaultArgs(method, argc, "self", "list");
                    }
                if (name == "GetEnumerator" && argc == 0 && _program.Program.Classes.ContainsKey("ListEnumerator"))
                {
                    var enumerator = RegisterClass("ListEnumerator");
                    if (enumerator.Rc.FindConstructor(2) is { } ctor && ctor.ParamCount == 2) GetFunc(ctor, FuncKind.Ctor);
                }
            }
            foreach (var field in ModFieldNames())
                foreach (var cls in Served.ToList())
                {
                    if (cls.Rc.FieldIndex.ContainsKey(field))
                    {
                        if (cls.Rc.FindFieldRequiredUnit(field) is { } requiredUnit)
                        {
                            UnitId(DemandUnit(requiredUnit));
                            Constant(Value.MakeString(Unit.DemandDisplay(requiredUnit)));
                        }
                        continue;
                    }
                    if (cls.Rc.FindMethodWithAccess("get_" + field, 0).Proto is { IsStatic: false } getter) GetFunc(getter, FuncKind.Method);
                    if (cls.Rc.FindMethodWithAccess("set_" + field, 1).Proto is { IsStatic: false } setter) GetFunc(setter, FuncKind.Method);
                }
            // (a type argument of an object of a class with a generic class in its chain: the functions of the descriptors register theirs as they are made)
            foreach (var cls in Served.Where(c => IsOwnClass(c.Rc)).ToList()) TArgOfClassFunction(cls.Rc, register: true);
        }

        // ---- dispatchers -----------------------------------------------------------------------------------------------------------------------
        /// <summary>The dispatchers of `name(argc)` of a module: `disp_` (the served classes by their number, the others to `miss_`), `miss_` (the tables of the
        /// object's class through the runtime, then what the VM does without the method), `call_` for the call sites (the base types, the module's own classes,
        /// everything else to `miss_`) and `mt_`, the row of the tables.</summary>
        private string ModDispatcher(string name, int argc, bool entry, bool row)
        {
            var served = Served.ToList();
            var byFunc = new Dictionary<Func, List<int>>();
            var overloaded = new List<(FunctionProto[] Protos, RuntimeClass[] Declaring, List<ClassInfo> Classes)>();
            foreach (var cls in served)
                if (cls.Rc.IsTypeOverloaded(name, argc) && cls.Rc.Candidates(name, argc) is var set && set.Protos.Any(p => !p.IsStatic))
                {
                    int group = overloaded.FindIndex(o => o.Protos.SequenceEqual(set.Protos) && o.Declaring.SequenceEqual(set.Declaring));
                    if (group < 0) overloaded.Add((set.Protos, set.Declaring, new List<ClassInfo> { cls }));
                    else overloaded[group].Classes.Add(cls);
                }
                else if (cls.Rc.FindMethodWithAccess(name, argc).Proto is { IsStatic: false } method)
                {
                    var f = GetFunc(method, FuncKind.Method);
                    if (!byFunc.TryGetValue(f, out var ids)) byFunc[f] = ids = new List<int>();
                    ids.Add(cls.Id);
                }
            var extensions = ExtensionMethods(name, argc).ToList();
            bool enumeratorOfArray = name == "GetEnumerator" && argc == 0 && _classes.ContainsKey("ListEnumerator") || name == "GetEnumerator" && argc == 0 && _foreignClasses.ContainsKey("ListEnumerator");
            var builtins = BuiltinMethods(name, argc).ToList();
            // a call site that passes addresses (of this module, or of another for a method with a `ref` parameter): an implementation without `ref` there gets the value
            bool derefs = _refCallSites.Contains((name, argc)) || _program.Program.Classes.Values.Any(rc => MethodsFor(rc, name, argc).Any(p => p.RefMask != 0));
            string StaticImpl(FunctionProto proto)
            {
                var (pre, defaults) = DefaultArgs(proto, argc, "Undef()", "list");
                return $"{{ {pre}return {GetFunc(proto, FuncKind.Static).Name}({(ArgsFor(proto) + defaults).TrimStart(',', ' ')}); }}";
            }
            string ScoredArg(int i) => derefs ? $"derefArg(a{i})" : $"a{i}";
            string ArgsFor(FunctionProto proto) => string.Concat(Enumerable.Range(0, argc).Select(i => derefs && (proto.RefMask >> i & 1) == 0 ? $", derefArg(a{i})" : $", a{i}"));
            bool restricted = RestrictedMethodName(name);
            string CallImpl(string target, FunctionProto proto, RuntimeClass? sample = null, bool extension = false, RuntimeClass? declaringOverload = null)
            {
                var (pre, defaults) = DefaultArgs(proto, argc, "self", "list");
                string access = "";
                var found = declaringOverload != null ? (Proto: proto, DeclaringClass: declaringOverload, Access: proto.Access ?? AccessModifier.Public)
                    : sample?.FindMethodWithAccess(name, argc) ?? default;
                if (restricted && sample != null && found.Proto != null && found.Access != AccessModifier.Public)
                {
                    string cond = AllowedExpr(found.DeclaringClass!, found.Access);
                    string message = extension
                        ? $"Method '{name}' of the extension of '{sample.Name.Substring(1)}' is {DescribeAccess(found.Access)} and cannot be called from here."
                        : $"Method '{name}' of '{found.DeclaringClass!.Name}' is {DescribeAccess(found.Access)} and cannot be called from here.";
                    access = $"if (!{cond}) return accessDenied({CString(message)}); ";
                }
                return pre.Length == 0 ? $"{{ {access}return {target}(self{ArgsFor(proto)}); }}" : $"{{ {access}{pre}return {target}(self{ArgsFor(proto)}{defaults}); }}";
            }
            string ps = string.Concat(Enumerable.Range(0, argc).Select(i => $", Value a{i}"));
            string args = string.Concat(Enumerable.Range(0, argc).Select(i => $", a{i}"));
            string disp = CallFn(name, argc, "disp_"), miss = CallFn(name, argc, "miss_");
            var sb = new StringBuilder();

            // miss_: an object of a class this module does not serve (or a served one without the method: another module may add it), or no object
            sb.AppendLine($"FIRE_MAYBE_UNUSED static Value {miss}(Value self{ps}, OwnList* list, uint32_t caller) {{");
            sb.AppendLine("    (void)list; (void)caller;");
            string av = argc == 0 ? "NULL" : "av_";
            string avDecl = argc == 0 ? "" : $"Value av_[{argc}] = {{{string.Join(", ", Enumerable.Range(0, argc).Select(i => $"a{i}"))}}}; ";
            sb.AppendLine($"    if (self.kind == K_Class && leafAlive(self)) {{ uint32_t k_; const FireMethod* m_ = fireFindMethod(asObj(self)->cls, {CString(name)}, {argc}, &k_); if (m_) {{ {avDecl}return m_->fn(k_, self, {av}, list, caller); }} }}");
            if (OwnMethodId(name, argc) is { } ownId)
            {
                int baseArgs = OwnMethodBaseArgs(ownId);
                string target = baseArgs == 1 ? "a0" : "Undef()";
                if (argc == baseArgs) sb.AppendLine($"    ownMethod({ownId}, self, {target}, list);");
                else sb.AppendLine($"    ownMethodT({ownId}, self, {target}, a{baseArgs}, list, g_scopeLists, g_scopeCount);");
                sb.AppendLine("    return Undef();");
            }
            else sb.AppendLine($"    return fireNoMethod(self, {CString(name)}, {argc});");
            sb.AppendLine("}");

            // disp_: the served classes by their number
            sb.AppendLine($"FIRE_MAYBE_UNUSED static Value {disp}(uint32_t k, Value self{ps}, OwnList* list, uint32_t caller) {{");
            sb.AppendLine("    (void)list; (void)caller;");
            sb.AppendLine("    switch (k) {");
            foreach (var (f, ids) in byFunc)
            {
                foreach (int id in ids) sb.AppendLine($"        case {id}:");
                sb.AppendLine($"            {CallImpl(f.Name, f.Proto!, served.First(c => c.Id == ids[0]).Rc)}");
            }
            foreach (var (protos, declaring, classes) in overloaded)
            {
                foreach (var cls in classes) sb.AppendLine($"        case {cls.Id}:");
                sb.AppendLine("        {");
                var sample = classes[0].Rc;
                sb.Append(OverloadChoice(protos, argc, "objAt(self)", c => protos[c].IsStatic ? StaticImpl(protos[c]) : CallImpl(GetFunc(protos[c], FuncKind.Method).Name, protos[c], sample, false, declaring[c]), "            ", ScoredArg));
                sb.AppendLine("        }");
            }
            // a class that has the method with other numbers of arguments: an ArgumentException (like the VM)
            if (OwnMethodId(name, argc) == null)
                foreach (var c in served.Where(c => HasMethodNamed(c.Rc, name) && c.Rc.FindMethodWithAccess(name, argc).Proto == null))
                    sb.AppendLine($"        case {c.Id}: return fireError(\"ArgumentException\", {CString(DescribeMethodNotFound(c.Rc, name, argc))});");
            sb.AppendLine("        default: break;");
            sb.AppendLine("    }");
            sb.AppendLine($"    return {miss}(self{args}, list, caller);");
            sb.AppendLine("}");

            // call_ (direct_ when the threads wrap it): what the call sites of this module call
            if (entry)
            {
                sb.AppendLine(DispatcherSignature(name, argc, WrapDispatchers ? "direct_" : "call_"));
                sb.AppendLine("{");
                sb.AppendLine("    (void)list; (void)caller;");
                sb.AppendLine("    switch (self.kind) {");
                foreach (var (cpp, proto, extensionRc) in extensions)
                {
                    if (extensionRc.IsTypeOverloaded(name, argc))
                    {
                        var set = extensionRc.Candidates(name, argc);
                        sb.AppendLine($"        case {cpp}: {{");
                        sb.Append(OverloadChoice(set.Protos, argc, "NULL", c => CallImpl(GetFunc(set.Protos[c], FuncKind.Method).Name, set.Protos[c], extensionRc, true, set.Declaring[c]), "            ", ScoredArg));
                        sb.AppendLine("        }");
                    }
                    else sb.AppendLine($"        case {cpp}: {CallImpl(GetFunc(proto, FuncKind.Method).Name, proto, extensionRc, true)}");
                }
                foreach (var (kind, expr) in builtins)
                    if (!extensions.Any(e => e.Cpp == kind)) sb.AppendLine($"        case {kind}: return {expr};");
                if (enumeratorOfArray)
                {
                    var info = RegisterClass("ListEnumerator");
                    var ctor = GetFunc(info.Rc.FindConstructor(2)!, FuncKind.Ctor);
                    sb.AppendLine("        case K_Array: case K_Buffer: case K_String: {");
                    sb.AppendLine("            bool ok;");
                    sb.AppendLine("            Value count = lengthOf(self, &ok);");
                    sb.AppendLine($"            Value e = newObject({ClassIdExpr(info)}, {info.Fields.Count}, list);");
                    sb.AppendLine($"            {Nested($"{ctor.Name}(e, self, count);")}");
                    sb.AppendLine("            return e;");
                    sb.AppendLine("        }");
                }
                if (served.Any(c => IsOwnClass(c.Rc)))
                {
                    sb.AppendLine("        case K_Class: {");
                    sb.AppendLine("            if (FIRE_UNLIKELY(!leafAlive(self))) return destroyedError(self);");
                    sb.AppendLine($"            uint32_t k_ = {OwnLocal("asObj(self)->cls")};");
                    sb.AppendLine($"            if (FIRE_LIKELY({OwnRangeTest("k_")})) return {disp}(k_, self{args}, list, caller);");
                    sb.AppendLine("            break;");
                    sb.AppendLine("        }");
                }
                sb.AppendLine("        default: break;");
                sb.AppendLine("    }");
                sb.AppendLine($"    return {miss}(self{args}, list, caller);");
                sb.AppendLine("}");
                if (WrapDispatchers) sb.Append(ActorWrapper(name, argc));
            }
            // mt_: the row of the tables (the arguments as an array)
            if (row)
                sb.AppendLine($"{(LibraryRole ? "" : "static ")}Value {CallFn(name, argc, "mt_")}(uint32_t k, Value self, const Value* a, OwnList* list, uint32_t caller) {{ (void)a; return {disp}(k, self{string.Concat(Enumerable.Range(0, argc).Select(i => $", a[{i}]"))}, list, caller); }}");
            return sb.ToString();
        }

        // ---- fields -----------------------------------------------------------------------------------------------------------------------------
        /// <summary>The functions of a field name of a module: `fget_`/`fset_`/`fptr_` (the served classes by their number - the rows of the tables call them), and for
        /// the module's call sites `gf_`/`sf_`/`fp_` (the base types, its own classes, everything else through the runtime).</summary>
        private string ModFieldHelpers(string name)
        {
            string m = Mangle(name);
            string cname = CString(name);
            var (getArms, setArms, ptrArms) = FieldArms(name, Served.Where(c => IsOwnClass(c.Rc)).ToList());
            var sb = new StringBuilder();
            string rowStorage = LibraryRole ? "" : "static ";   // (a library's: the program's tables of its classes name them)
            sb.AppendLine($"FIRE_MAYBE_UNUSED {rowStorage}Value {FieldFn("fget", name)}(uint32_t k, Value v, OwnList* list, uint32_t caller) {{\n    Obj* o = asObj(v); (void)o; (void)list; (void)caller;\n    switch (k) {{\n{MergedCases(getArms)}        default: break;\n    }}\n    return fireGetField(v, {cname}, list, caller);\n}}");
            sb.AppendLine($"FIRE_MAYBE_UNUSED {rowStorage}void {FieldFn("fset", name)}(uint32_t k, Value v, Value x, OwnList* list, uint32_t caller) {{\n    Obj* o = asObj(v); (void)o; (void)list; (void)caller;\n    switch (k) {{\n{MergedCases(setArms)}        default: break;\n    }}\n    fireSetField(v, x, {cname}, list, caller);\n}}");
            sb.AppendLine($"FIRE_MAYBE_UNUSED {rowStorage}Value {FieldFn("fptr", name)}(uint32_t k, Value v) {{\n    Obj* o = asObj(v); (void)o;\n    switch (k) {{\n{MergedCases(ptrArms)}        default: break;\n    }}\n    return fireFieldPtr(v, {cname});\n}}");
            if (!_fieldNames.Contains(name)) return sb.ToString();
            bool isLength = name is "Length" or "length";
            string lengthCode = isLength ? "    { bool ok; Value n = lengthOf(v, &ok); if (ok) return n; }\n"
                : name == "littleEndian" ? "    if (v.kind == K_Buffer) return Bool(bufOf(v)->little != 0);\n" : "";
            string own = $"    {{ uint32_t k_ = {OwnLocal("asObj(v)->cls")}; if (FIRE_LIKELY({OwnRangeTest("k_")})) ";
            sb.AppendLine($"static inline Value {FieldFn("gf", name)}(Value v, OwnList* list, uint32_t caller) {{\n{lengthCode}    if (FIRE_UNLIKELY(v.kind != K_Class)) return missingField(v, {cname});\n    if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v);\n{own}return {FieldFn("fget", name)}(k_, v, list, caller); }}\n    return fireGetField(v, {cname}, list, caller);\n}}");
            // the setter: (probes) sf_ -> sfo_ -> (threads: one section) -> sfb_
            string plain = _usesProbes ? FieldFn("sfo", name) : FieldFn("sf", name);
            string body = _usesThreads ? FieldFn("sfb", name) : plain;
            sb.AppendLine($"static inline void {body}(Value v, Value x, OwnList* list, uint32_t caller) {{\n    if (FIRE_UNLIKELY(v.kind != K_Class)) {{ notObjectAssign(v); return; }}\n    if (FIRE_UNLIKELY(!leafAlive(v))) {{ destroyedError(v); return; }}\n{own}{{ {FieldFn("fset", name)}(k_, v, x, list, caller); return; }} }}\n    fireSetField(v, x, {cname}, list, caller);\n}}");
            if (_usesThreads)
                sb.AppendLine($"static inline void {plain}(Value v, Value x, OwnList* list, uint32_t caller) {{ bool ss_ = sectionScopeEnterV(v); {body}(v, x, list, caller); sectionScopeExit(ss_); }}");
            if (_usesProbes)
            {
                sb.AppendLine($"static void {FieldFn("sfa", name)}(Value v, Value x, OwnList* list, uint32_t caller) {{ {plain}(v, x, list, caller); }}");
                // the old value as the probe sees it: the field, else the getter, else undefined (no access check)
                var arms = new List<(int, string)>();
                foreach (var cls in Served.Where(c => IsOwnClass(c.Rc)))
                {
                    if (cls.Rc.FieldIndex.TryGetValue(name, out int index)) arms.Add((cls.Id, $"return objFields(o)[{index}];"));
                    else if (cls.Rc.FindMethodWithAccess("get_" + name, 0).Proto is { IsStatic: false } getter) arms.Add((cls.Id, $"{{ Value r = {GetFunc(getter, FuncKind.Method).Name}(v); adopt(r, list); return r; }}"));
                }
                sb.AppendLine($"static Value {FieldFn("gfp", name)}(Value v, OwnList* list) {{\n    Obj* o = asObj(v); (void)list;\n    uint32_t k_ = {OwnLocal("o->cls")};\n    if (!({OwnRangeTest("k_")})) return Undef();\n    switch (k_) {{\n{MergedCases(arms)}        default: return Undef();\n    }}\n}}");
                sb.AppendLine($"static inline void {FieldFn("sf", name)}(Value v, Value x, OwnList* list, uint32_t caller) {{");
                sb.AppendLine($"    if (v.kind == K_Class && FIRE_UNLIKELY(asObj(v)->h.flags & 2)) {{ probedSet(v, {cname}, x, {FieldFn("sfa", name)}, {FieldFn("gfp", name)}, caller); return; }}");
                sb.AppendLine($"    {plain}(v, x, list, caller);");
                sb.AppendLine("}");
            }
            if (_fieldAddressTaken.Contains(name))
                sb.AppendLine($"static inline Value {FieldFn("fp", name)}(Value v) {{\n    if (FIRE_UNLIKELY(v.kind != K_Class)) return fireFieldPtr(v, {cname});\n    if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v);\n{own}return {FieldFn("fptr", name)}(k_, v); }}\n    return fireFieldPtr(v, {cname});\n}}");
            return sb.ToString();
        }

        private string ModFieldPrototypes(string name)
        {
            var sb = new StringBuilder();
            if (!_fieldNames.Contains(name)) return "";
            sb.AppendLine($"FIRE_MAYBE_UNUSED static inline Value {FieldFn("gf", name)}(Value v, OwnList* list, uint32_t caller);");
            sb.AppendLine($"FIRE_MAYBE_UNUSED static inline void {FieldFn("sf", name)}(Value v, Value x, OwnList* list, uint32_t caller);");
            if (_fieldAddressTaken.Contains(name)) sb.AppendLine($"FIRE_MAYBE_UNUSED static inline Value {FieldFn("fp", name)}(Value v);");
            return sb.ToString();
        }

        // ---- is of, operators, class scores, type arguments -----------------------------------------------------------------------------------------
        private string ModIsOf(string typeName)
        {
            var ids = Served.Where(c => IsOwnClass(c.Rc) && ClassIs(c.Rc, typeName)).Select(c => c.Id).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"FIRE_MAYBE_UNUSED static bool {IsOfFn(typeName)}(Value v) {{");
            if (EnumIndexNamed(typeName) is var enumIndex and > 0) sb.AppendLine($"    if (v.kind == K_Int) return v.reserved == {EnumC(enumIndex)};   // (the enum {typeName})");
            if (typeName == "IEnumerable") sb.AppendLine("    if (v.kind == K_Array || v.kind == K_Buffer) return true;");
            sb.AppendLine("    if (v.kind != K_Class) return false;");
            sb.AppendLine($"    {{ uint32_t k_ = {OwnLocal("asObj(v)->cls")}; if (FIRE_LIKELY({OwnRangeTest("k_")})) {(ids.Count == 0 ? "return false;" : "switch (k_) { " + string.Concat(ids.Select(id => $"case {id}: ")) + "return true; default: return false; }")} }}");
            sb.AppendLine($"    return fireIsOf(asObj(v)->cls, {CString(typeName)});");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string ModOperator(string method)
        {
            var ids = Served.Where(c => IsOwnClass(c.Rc) && c.Rc.FindMethodWithAccess(method, 1).Proto != null).Select(c => c.Id).ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"static Value {OperatorWrapper(method)}(Value a, Value b, OwnList* list) {{");
            sb.AppendLine("    if (a.kind == K_Class && leafAlive(a)) {");
            sb.AppendLine($"        uint32_t k_ = {OwnLocal("asObj(a)->cls")};");
            sb.AppendLine($"        if (FIRE_LIKELY({OwnRangeTest("k_")})) {{");
            if (ids.Count > 0)
                sb.AppendLine("            switch (k_) { " + string.Concat(ids.Select(id => $"case {id}: ")) + $"{{ Value r = {CallFn(method, 1)}(a, b, list, 0xFFFFFFFFu); adopt(r, list); return r; }} default: break; }}");
            sb.AppendLine("        } else {");
            sb.AppendLine($"            uint32_t m_; const FireMethod* f_ = fireFindMethod(asObj(a)->cls, {CString(method)}, 1, &m_);");
            sb.AppendLine("            if (f_) { Value r = f_->fn(m_, a, &b, list, 0xFFFFFFFFu); adopt(r, list); return r; }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine($"    return {OperatorFallback[method]};");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private string ModClassScore(string name)
        {
            var cases = new StringBuilder();
            foreach (var cls in Served.Where(c => IsOwnClass(c.Rc)))
            {
                int distance = 0;
                for (var rc = cls.Rc; rc != null; rc = rc.Base, distance++)
                    if (rc.Name == name)
                    {
                        cases.Append($"case {cls.Id}: return {(Overloads.ClassExact - distance).ToString(CultureInfo.InvariantCulture)}; ");
                        break;
                    }
            }
            var sb = new StringBuilder();
            sb.AppendLine($"FIRE_MAYBE_UNUSED static int {OvdFn(name)}(Value v) {{");
            sb.AppendLine($"    if (!leafAlive(v)) return {Overloads.UndefinedScore};");
            sb.AppendLine($"    {{ uint32_t k_ = {OwnLocal("objAt(v)->cls")}; if (FIRE_LIKELY({OwnRangeTest("k_")})) {(cases.Length == 0 ? $"return {Overloads.NoMatch};" : $"switch (k_) {{ {cases}default: return {Overloads.NoMatch}; }}")} }}");
            sb.AppendLine($"    return fireClassScore(objAt(v)->cls, {CString(name)}, {Overloads.ClassExact}, {Overloads.NoMatch});");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>The type argument `k` of the generic class `cls` for an object of the class `ci` (its own, or given by a class of its chain), null: `ci` is not derived from it.</summary>
        private string? TArgCaseExpr(RuntimeClass ci, string cls, int k, string obj = "o")
        {
            string[]? args = null;   // (null: the class's own type arguments; "$j" is its j-th)
            var rc = ci;
            while (rc != null && rc.Name != cls)
            {
                var given = rc.BaseTypeArgs ?? Array.Empty<string>();
                var outer = args;
                args = given.Select(t => !t.StartsWith('$') ? t
                    : outer == null ? t
                    : int.Parse(t.AsSpan(1), CultureInfo.InvariantCulture) is var j && j < outer.Length ? outer[j] : "").ToArray();
                rc = rc.Base;
            }
            if (rc == null) return null;
            string token = args == null ? "$" + k : k < args.Length ? args[k] : "";
            return token.Length == 0 ? "NULL"
                : token.StartsWith('$') ? $"({obj}->targs ? {obj}->targs[{token.Substring(1)}] : NULL)"
                : TArgRef(token);
        }

        private string ModTArgOf(string cls, int k)
        {
            var cases = new List<(int Id, string Expr)>();
            foreach (var ci in Served.Where(c => IsOwnClass(c.Rc)))
                if (TArgCaseExpr(ci.Rc, cls, k) is { } expr) cases.Add((ci.Id, expr));
            var sb = new StringBuilder();
            sb.AppendLine($"FIRE_MAYBE_UNUSED static const TArg* {TArgOfFn(cls, k)}(Obj* o) {{");
            sb.AppendLine("    if (!o) return NULL;");
            sb.AppendLine($"    {{ uint32_t k_ = {OwnLocal("o->cls")}; if (FIRE_LIKELY({OwnRangeTest("k_")})) switch (k_) {{");
            foreach (var group in cases.GroupBy(c => c.Expr))
                sb.AppendLine("        " + string.Concat(group.Select(c => $"case {c.Id}: ")) + $"return {group.Key};");
            sb.AppendLine("        default: return NULL;");
            sb.AppendLine("    } }");
            sb.AppendLine($"    return fireTArgOf(o, {CString(cls)}, {k});");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>The number of type parameters of a class (0: not generic).</summary>
        private static int TypeParamCountOf(RuntimeClass rc) => rc.Decl?.TypeParams?.Count ?? 0;

        /// <summary>Does the class have a generic class in its chain (its descriptor has a function for the type arguments)?</summary>
        private static bool HasGenericChain(RuntimeClass rc)
        {
            for (var c = rc; c != null; c = c.Base) if (TypeParamCountOf(c) > 0) return true;
            return false;
        }

        private static string TArgFunctionName(string module, RuntimeClass rc) => $"fire_targ_{module}_{Mangle(rc.Name)}";

        /// <summary>The function of a class's descriptor for the type arguments of its generic classes (`fire_targ_M_C(o, "List", 0)`); null without a generic class in its chain.</summary>
        private string? TArgOfClassFunction(RuntimeClass rc, bool register = false)
        {
            if (!HasGenericChain(rc)) return null;
            var sb = new StringBuilder();
            sb.AppendLine($"{(LibraryRole ? "" : "static ")}const TArg* {TArgFunctionName(ModId, rc)}(Obj* o, const char* cls, int k) {{");
            sb.AppendLine("    (void)o; (void)k;");
            for (var c = rc; c != null; c = c.Base)
            {
                int count = TypeParamCountOf(c);
                if (count == 0) continue;
                var arms = Enumerable.Range(0, count).Select(j => (j, TArgCaseExpr(rc, c.Name, j) ?? "NULL")).ToList();
                sb.AppendLine($"    if (strcmp(cls, {CString(c.Name)}) == 0) switch (k) {{ {string.Concat(arms.Select(a => $"case {a.j}: return {a.Item2}; "))}default: return NULL; }}");
            }
            sb.AppendLine("    return NULL;");
            sb.AppendLine("}");
            return register ? null : sb.ToString();
        }

        // ---- the tables --------------------------------------------------------------------------------------------------------------------------
        private static string MembersName(string module, RuntimeClass rc) => $"fire_mem_{module}_{Mangle(rc.Name)}";

        /// <summary>The C strings compare by their bytes (strcmp): the rows are sorted so.</summary>
        private static int Utf8Compare(string a, string b)
        {
            var x = Encoding.UTF8.GetBytes(a);
            var y = Encoding.UTF8.GetBytes(b);
            int n = Math.Min(x.Length, y.Length);
            for (int i = 0; i < n; i++) if (x[i] != y[i]) return x[i] - y[i];
            return x.Length - y.Length;
        }

        /// <summary>The tables of the members of the served classes (FireMembers, fire_rt.h) and the functions of their descriptors for type arguments.</summary>
        private string MemberTables()
        {
            var sb = new StringBuilder();
            foreach (var cls in Served)
            {
                var methods = TableMethodsOf(cls);
                methods.Sort((a, b) => Utf8Compare(a.Name, b.Name) is var c && c != 0 ? c : a.Argc.CompareTo(b.Argc));
                var fields = TableFieldsOf(cls);
                fields.Sort(Utf8Compare);
                string table = MembersName(ModId, cls.Rc);
                if (methods.Count > 0)
                    sb.AppendLine($"static const FireMethod {table}_m[] = {{{string.Join(", ", methods.Select(x => $"{{{CString(x.Name)}, {x.Argc}, {x.Params}, {CallFn(x.Name, x.Argc, "mt_")}}}"))}}};");
                if (fields.Count > 0)
                    sb.AppendLine($"static const FireField {table}_f[] = {{{string.Join(", ", fields.Select(f => $"{{{CString(f)}, {cls.Rc.FieldIndex[f]}, {FieldFn("fget", f)}, {FieldFn("fset", f)}, {FieldFn("fptr", f)}}}"))}}};");
                sb.AppendLine($"{(LibraryRole ? "" : "static ")}const FireMembers {table} = {{{cls.Id}u, {methods.Count}, {(methods.Count > 0 ? table + "_m" : "NULL")}, {fields.Count}, {(fields.Count > 0 ? table + "_f" : "NULL")}}};   // {cls.Rc.Name}");
                if (IsOwnClass(cls.Rc) && TArgOfClassFunction(cls.Rc) is { } targ) sb.Append(targ);
            }
            return sb.ToString();
        }

        /// <summary>The program: the columns of a class descriptor for modules - the names of its chain and interfaces, the tables of its members (of every
        /// module that declares some on a class of its chain) and the function for its type arguments.</summary>
        private string ModularDescColumns(RuntimeClass rc, StringBuilder decls, int id)
        {
            var isa = new List<string>();
            var ifaces = new List<string>();
            var tables = new List<string>();
            var allModules = _modules.Values.OrderBy(m => m.Index).ToList();
            for (var c = rc; c != null; c = c.Base)
            {
                isa.Add(c.Name);
                foreach (var i in c.Interfaces) if (!ifaces.Contains(i)) ifaces.Add(i);
                var owner = ModuleOf(c);
                // the table of the class's own module (the program: only for its classes it serves), then of the modules that extend it - for a class of a
                // library a table of the program with the members some module calls by name (the library's own tables name all: the linker keeps what they name)
                if (owner != null) tables.Add(LibraryClassTable(owner, c, decls));
                else if (_classes.ContainsKey(c.Name)) tables.Add(MembersName("prog", c));
                foreach (var m in allModules) if (!ReferenceEquals(m, owner) && ExtendsClass(c, m)) tables.Add(LibraryClassTable(m, c, decls));
                if (owner != null && ExtendsClass(c, null) && ExtServed.Any(e => e.Rc == c)) tables.Add(MembersName("prog", c));
            }
            string isaName = $"kIsa{id}", ifaceName = "NULL", tablesName = "NULL";
            decls.AppendLine($"static const char* const {isaName}[] = {{{string.Join(", ", isa.Select(CString))}, NULL}};");
            if (ifaces.Count > 0) { ifaceName = $"kIfc{id}"; decls.AppendLine($"static const char* const {ifaceName}[] = {{{string.Join(", ", ifaces.Select(CString))}, NULL}};"); }
            if (tables.Count > 0)
            {
                tablesName = $"kMem{id}";
                decls.AppendLine($"static const FireMembers* const {tablesName}[] = {{{string.Join(", ", tables.Select(t => "&" + t))}, NULL}};");
            }
            string targ = "NULL";
            if (HasGenericChain(rc))
            {
                targ = TArgFunctionName(ModIdOf(rc), rc);
                if (ModuleOf(rc) != null && _externTables.Add(targ)) decls.Insert(0, $"const TArg* {targ}(Obj* o, const char* cls, int k);\n");
                else if (ModuleOf(rc) == null && !_classes.ContainsKey(rc.Name)) targ = "NULL";
            }
            return $", {isaName}, {ifaceName}, {tablesName}, {targ}";
        }
        private readonly HashSet<string> _externTables = new(StringComparer.Ordinal);
        private readonly Dictionary<(ModuleSpec, string), string> _libraryClassTables = new();
        /// <summary>The program: its tables of the classes of the libraries (written before the descriptors).</summary>
        private readonly StringBuilder _libraryTablesCode = new();

        /// <summary>The program: its table of the members that module `m` declares on the class `c` of a library (its own class, or one it extends), with the rows
        /// some module calls by name - they call the functions the library exports for its tables (`<m>_mt_X_n`, `<m>_fget_x`, ...) with the number the library gives
        /// the class. The same choice as the library's own table (<see cref="TableMethodsOf"/>), narrowed to what is wanted.</summary>
        private string LibraryClassTable(ModuleSpec m, RuntimeClass c, StringBuilder decls)
        {
            if (_libraryClassTables.TryGetValue((m, c.Name), out var known)) return known;
            // (every member is wanted - Reflect.Call/Get by any name: the library's own table, it names them all)
            if (_tablesAll)
            {
                string full = MembersName(m.Id, c);
                _libraryTablesCode.AppendLine($"extern const FireMembers {full};");
                return _libraryClassTables[(m, c.Name)] = full;
            }
            string px = m.Id + "_";
            bool own = ReferenceEquals(ModuleOf(c), m);
            int k = own ? m.Classes.IndexOf(c.Name)
                : m.Classes.Count + m.Deps.SelectMany(d => d.Classes).Distinct().OrderBy(n => n, StringComparer.Ordinal)
                    .Where(n => _program.Program.Classes.TryGetValue(n, out var x) && ExtendsClass(x, m)).ToList().IndexOf(c.Name);
            var libDispatchers = _libraries?.FirstOrDefault(l => ReferenceEquals(l.Spec, m))?.Needs.Dispatchers;
            // (what the module calls itself on its own class goes through its own dispatcher - its names count only when a class of another module derives from it
            // and inherits the member: a call of the module on such an object looks for it in this table)
            bool derived = !own || _classList.Any(x => !ReferenceEquals(ModuleOf(x.Rc), m) && x.Rc != c && ClassIs(x.Rc, c.Name));   // (a class it extends: its own calls on it come here)
            var others = _libraries?.Where(l => !ReferenceEquals(l.Spec, m) || derived).ToList() ?? new List<ModuleOutput>();
            bool Wanted(string name, int argc)
            {
                if (_tablesAll || _dispatchers.Contains((name, argc)) || others.Any(l => l.Needs.Dispatchers.Contains((name, argc)) || l.Needs.Operators.Contains(name) && argc == 1)) return true;
                string field = name.Length > 4 ? name.Substring(4) : "";
                return (argc == 0 && name.StartsWith("get_", StringComparison.Ordinal) || argc == 1 && name.StartsWith("set_", StringComparison.Ordinal)) && FieldWanted(field);
            }
            bool FieldWanted(string field) => _tablesAll || _fieldNames.Contains(field) || others.Any(l => l.Needs.Fields.Contains(field) || l.Needs.FieldAddresses.Contains(field));
            var methods = new List<(string Name, int Argc, int Params)>();
            foreach (var (name, protos) in c.Methods)
            {
                var mine = protos.Where(p => !p.IsStatic && ProtoKey(p, out var o, out _) && ReferenceEquals(o, m)
                    && (p.Access != AccessModifier.Private || KeepsPrivate(c, name) || libDispatchers != null && libDispatchers.Any(d => d.Name == name))).ToList();
                if (mine.Count == 0) continue;
                var counts = new SortedSet<int>();
                foreach (var p in mine) for (int argc = RequiredArgs(p); argc <= p.ParamCount; argc++) counts.Add(argc);
                foreach (int argc in counts)
                    if (HasCase(c, name, argc) && Wanted(name, argc)) methods.Add((name, argc, c.FindMethodWithAccess(name, argc).Proto?.ParamCount ?? argc));
            }
            methods.Sort((a, b) => Utf8Compare(a.Name, b.Name) is var cmp && cmp != 0 ? cmp : a.Argc.CompareTo(b.Argc));
            var fields = own ? c.OwnFieldInfo.Where(f => !f.Value.IsStatic && c.FieldIndex.ContainsKey(f.Key) && FieldWanted(f.Key)).Select(f => f.Key).ToList() : new List<string>();
            fields.Sort(Utf8Compare);
            string table = $"kPm_{m.Id}_{Mangle(c.Name)}";
            var sb = new StringBuilder();
            foreach (var (name, argc, _) in methods)
                if (_externTables.Add($"{px}mt_{Mangle(name)}_{argc}")) sb.AppendLine($"Value {px}mt_{Mangle(name)}_{argc}(uint32_t k, Value self, const Value* a, OwnList* list, uint32_t caller);");
            foreach (var f in fields)
                if (_externTables.Add($"{px}fget_{Mangle(f)}"))
                    sb.AppendLine($"Value {px}fget_{Mangle(f)}(uint32_t k, Value v, OwnList* list, uint32_t caller);\nvoid {px}fset_{Mangle(f)}(uint32_t k, Value v, Value x, OwnList* list, uint32_t caller);\nValue {px}fptr_{Mangle(f)}(uint32_t k, Value v);");
            if (methods.Count > 0)
                sb.AppendLine($"static const FireMethod {table}_m[] = {{{string.Join(", ", methods.Select(x => $"{{{CString(x.Name)}, {x.Argc}, {x.Params}, {px}mt_{Mangle(x.Name)}_{x.Argc}}}"))}}};");
            if (fields.Count > 0)
                sb.AppendLine($"static const FireField {table}_f[] = {{{string.Join(", ", fields.Select(f => $"{{{CString(f)}, {c.FieldIndex[f]}, {px}fget_{Mangle(f)}, {px}fset_{Mangle(f)}, {px}fptr_{Mangle(f)}}}"))}}};");
            sb.AppendLine($"static const FireMembers {table} = {{{k}u, {methods.Count}, {(methods.Count > 0 ? table + "_m" : "NULL")}, {fields.Count}, {(fields.Count > 0 ? table + "_f" : "NULL")}}};   // {c.Name} ({m.Name})");
            _libraryTablesCode.Append(sb);
            return _libraryClassTables[(m, c.Name)] = table;
        }

        /// <summary>The helpers of a module (the program's and every library's): prototypes, then the code - after the functions' prototypes.</summary>
        private string ModularHelpers()
        {
            var fieldNames = ModFieldNames();
            var dispatchNames = ModDispatchNames();
            var rowNames = TableMethodNames();
            // the field helpers first: their checks register the `is of` tests and type arguments they use
            var fieldCode = fieldNames.Select(ModFieldHelpers).ToList();
            foreach (var name in fieldNames)
                foreach (var cls in Served)
                    if (cls.Rc.FindFieldRequiredType(name) is { } requiredType) _isOfTypes.UnionWith(IsOfNames(requiredType));
            var dispatcherCode = dispatchNames.Select(x => ModDispatcher(x.Name, x.Argc, _dispatchers.Contains(x), rowNames.Contains(x)))
                .Concat(_interfaceDispatchers.Select(x => InterfaceDispatcher(x.Interface, x.Name, x.Argc))).ToList();
            var selectorCode = _overloadSelectors.Values.Select(SelectorFunction).ToList();
            string tables = MemberTables();
            string targTables = TArgTables();
            string ovTests = OverloadClassFunctions();
            var sb = new StringBuilder();
            foreach (var typeName in _isOfTypes) sb.AppendLine($"FIRE_MAYBE_UNUSED static bool {IsOfFn(typeName)}(Value v);");
            sb.Append(targTables);
            foreach (var name in fieldNames) sb.Append(ModFieldPrototypes(name));
            foreach (var (name, argc) in dispatchNames)
            {
                string ps = string.Concat(Enumerable.Range(0, argc).Select(i => $", Value a{i}"));
                sb.AppendLine($"FIRE_MAYBE_UNUSED static Value {CallFn(name, argc, "disp_")}(uint32_t k, Value self{ps}, OwnList* list, uint32_t caller);");
                if (!_dispatchers.Contains((name, argc))) continue;
                sb.AppendLine(DispatcherSignature(name, argc) + ";");
                if (WrapDispatchers) sb.AppendLine(DispatcherSignature(name, argc, "direct_") + ";");
                if (_usesActors) sb.AppendLine($"static Value {CallFn(name, argc, "msg_")}(Value self, const Value* a, OwnList* list);");
            }
            foreach (var (iface, name, argc) in _interfaceDispatchers) sb.AppendLine(InterfaceDispatcherSignature(iface, name, argc) + ";");
            foreach (var method in _operators) sb.AppendLine($"static Value {OperatorWrapper(method)}(Value a, Value b, OwnList* list);");
            sb.AppendLine($"FIRE_MAYBE_UNUSED static inline Value {Px}aget_g(Value a, Value i, OwnList* list);");
            sb.AppendLine($"FIRE_MAYBE_UNUSED static inline void {Px}aset_g(Value a, Value i, Value v, OwnList* list);");
            foreach (var selector in _overloadSelectors.Values) sb.AppendLine(SelectorSignature(selector) + ";");
            sb.Append(ovTests);
            foreach (var code in fieldCode) sb.AppendLine(code);
            foreach (var code in dispatcherCode) sb.AppendLine(code);
            foreach (var code in selectorCode) sb.AppendLine(code);
            if (!LibraryRole && _usesTakeEnumerate) sb.Append(EnumerateItemsFunction());
            foreach (var method in _operators) sb.Append(ModOperator(method));
            foreach (var typeName in _isOfTypes) sb.Append(ModIsOf(typeName));
            sb.Append(tables);
            sb.AppendLine(IndexHelpers());
            return sb.ToString();
        }
    }
}
