using System.Globalization;
using System.Text;
using fire.Ast;
using fire.Bytecode;
using fire.Standard;
using fire.Values;

namespace fire.Native
{
    /// <summary>
    /// Reflection (`#import "reflection"`, SPEC 8.13) and `probe`/`silence` (SPEC 8.14) in the native backend. What a program can reach by name depends on the
    /// `__refl_*` functions it calls and on the stripping level (docs/NATIVE_BACKEND.md "Größe des Programms"): the members of an object by name (Get/Set/Has, probes)
    /// need the fields and property accessors of the classes whose objects exist, Reflect.Call their methods that are there (at level 0 and, the public ones, at 1:
    /// all), Reflect.New the constructors of the classes in the tables. The tables describe every class at the levels 0-2, at level 3 the classes the program reaches
    /// (made, used, their bases, `[Keep]`, named as a literal to reflection); a method that is left out is not in them, but its name is: a lookup of it (or of a class
    /// that is left out) is an error that says so. The access by name is generated code over the field helpers and dispatchers.
    /// </summary>
    public sealed partial class CppGenerator
    {
        /// <summary>The program uses reflection: the tables are written.</summary>
        private bool _usesReflection;
        /// <summary>The program uses `probe`: the field setters look for probes.</summary>
        private bool _usesProbes;
        /// <summary>Members of objects by name (Reflect.Get/Set/Has, Selector, probes): the fields and property accessors of every class whose objects exist.</summary>
        private bool _reflMembers;
        /// <summary>Reflect.Call: the instance methods of the classes whose objects exist that are there (see <see cref="RegisterReflectionTargets"/>).</summary>
        private bool _reflCall;
        /// <summary>Reflect.New: the constructors of every class of the tables.</summary>
        private bool _reflNew;
        /// <summary>The names of classes given as literals to reflection (`Reflect.New("Dog")`, `Type.Named("Dog")`): kept like `[Keep]`.</summary>
        private readonly HashSet<string> _literalClasses = new(StringComparer.Ordinal);
        /// <summary>The names of members given as literals to reflection (`Reflect.Call(o, "Bark")`, `Reflect.Get(o, "f")`): kept on the classes whose objects exist.</summary>
        private readonly HashSet<string> _literalMembers = new(StringComparer.Ordinal);
        /// <summary>The functions whose literals have been looked at.</summary>
        private readonly HashSet<Chunk> _literalChunks = new(ReferenceEqualityComparer.Instance);

        /// <summary>A function calls reflection (a static method of Reflect/Type/..., a method of a Type/Member/...): the texts it has as literals that name a class or a
        /// member are kept (docs/NATIVE_BACKEND.md, stripping) - `Reflect.New("Dog")`, `Reflect.Call(o, "Bark")`, also `var n = "Bark"` before the call. Any text of
        /// the function counts, not only the arguments of the call: what it keeps more is little, and a name that reflection looks up is not missing because of it.</summary>
        private void NoteReflectionLiterals(Func f)
        {
            if (IsReflectionHelperClass(f.Chunk.OwnerClass) || !_literalChunks.Add(f.Chunk)) return;
            // (the texts the code loads as values - the names of classes and members its instructions use are constants too)
            foreach (var ins in OpInfo.Decode(f.Chunk.Code))
            {
                if (ins.Op != OpCode.LoadConst) continue;
                var c = f.Chunk.Constants[ins.A[0]];
                if (c.Kind != ValueKind.String) continue;
                string text = c.AsString();
                if (text.Length == 0 || text.Length > 200) continue;
                if (_program.Program.Classes.ContainsKey(text)) { if (_literalClasses.Add(text)) _version++; }
                else if (IsMemberName(text) && _literalMembers.Add(text)) _version++;
            }
        }

        private static bool IsMemberName(string text) =>
            (char.IsLetter(text[0]) || text[0] == '_') && text.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

        /// <summary>A class that stays with all its members: `[Keep]`, or named as a literal to reflection.</summary>
        private bool KeptClass(RuntimeClass rc) => rc.Keep || _literalClasses.Contains(rc.Name);

        /// <summary>The name of a member as reflection sees it: a property accessor `get_X`/`set_X` is `X`.</summary>
        private static string PlainMemberName(string name) =>
            name.Length > 4 && (name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal)) ? name.Substring(4) : name;

        /// <summary>A member named as a literal to reflection.</summary>
        private bool LiteralMember(string name) => _literalMembers.Contains(name) || _literalMembers.Contains(PlainMemberName(name));

        /// <summary>A member that stays: marked `[Keep]`, or named as a literal to reflection.</summary>
        private bool KeptMember(RuntimeClass rc, string name) =>
            rc.KeptMembers is { } kept && (kept.Contains(name) || kept.Contains(PlainMemberName(name))) || LiteralMember(name);

        private void UseReflection()
        {
            if (_usesReflection) return;
            _usesReflection = true;
            UseExceptions();   // a failed reflection call is a ReflectionException
            _version++;
        }

        private void UseProbes()
        {
            UseReflection();
            UseReflectionLevel("__refl_probe");   // (a probe on a computed property calls its getter: the accessors are needed as for Reflect.Get)
            if (_usesProbes) return;
            _usesProbes = true;
            _version++;
        }

        /// <summary>What a reflection native needs of the program (see the fields above).</summary>
        private void UseReflectionLevel(string native)
        {
            void Raise(ref bool level)
            {
                if (level) return;
                level = true;
                _version++;
            }
            switch (native)
            {
                case "__refl_get": case "__refl_set": case "__refl_has": case "__refl_member_kind": case "__refl_probe": case "__refl_silence": case "__refl_silence_handle":
                    Raise(ref _reflMembers); break;
                case "__refl_call": Raise(ref _reflCall); break;
                case "__refl_new": Raise(ref _reflNew); break;
            }
        }

        private static readonly string[] ReflectionNatives =
        {
            "__refl_class_name", "__refl_class_info", "__refl_members", "__refl_classes", "__refl_is_sub", "__refl_get", "__refl_set", "__refl_call", "__refl_new",
            "__refl_has", "__refl_selector_path", "__refl_member_kind", "__refl_probe", "__refl_silence", "__refl_silence_handle", "__refl_enum_name", "__refl_enum_of",
        };

        private HashSet<string>? _reflectionMethodNames;
        /// <summary>The names of the methods of the library classes: a call of one of them from user code tells the library who calls (private members, SPEC 8.13).</summary>
        private HashSet<string> ReflectionMethodNames
        {
            get
            {
                if (_reflectionMethodNames != null) return _reflectionMethodNames;
                var names = new HashSet<string>();
                foreach (var cls in ReflectionPrelude.HelperClasses)
                    if (_program.Program.Classes.TryGetValue(cls, out var rc))
                        foreach (var name in rc.Methods.Keys) names.Add(name);
                return _reflectionMethodNames = names;
            }
        }

        private bool IsReflectionHelperClass(RuntimeClass? rc) => rc != null && ReflectionPrelude.HelperClasses.Contains(rc.Name);

        /// <summary>The call of a library method from code outside the library: it tells the library the class of the caller (g_reflCaller).</summary>
        private string ReflectionCallerAssign(Func f) => _usesReflection && CheckAccess && !IsReflectionHelperClass(f.Chunk.OwnerClass) ? $"g_reflCaller = {CallerId(f)}; " : "";

        /// <summary>A dispatcher for a method name and number of arguments (a call by name can reach it).</summary>
        private void AddDispatcher(string name, int argc)
        {
            if (_dispatchers.Add((name, argc))) _version++;
        }

        /// <summary>A name that is read or written by name (the field helpers gf_/sf_ of the name).</summary>
        private void AddFieldName(string name)
        {
            if (_fieldNames.Add(name)) _version++;
        }

        /// <summary>The classes the program reaches: those whose objects exist, those whose code or static fields are used, the classes marked `[Keep]` (or with a
        /// member marked so), each with its base classes.</summary>
        private HashSet<RuntimeClass> ReachedClasses()
        {
            var set = new HashSet<RuntimeClass>(ReferenceEqualityComparer.Instance);
            void Add(RuntimeClass? rc)
            {
                for (var c = rc; c != null && set.Add(c); c = c.Base) { }
            }
            foreach (var cls in _classList) Add(cls.Rc);
            foreach (var f in _funcs) Add(f.Chunk.OwnerClass);
            foreach (var key in _staticFields.Keys) Add(_program.Program.Classes.GetValueOrDefault(key.Class));
            foreach (var rc in _program.Program.Classes.Values) if (KeptClass(rc) || rc.KeptMembers != null) Add(rc);
            return set;
        }

        /// <summary>The classes the reflection tables describe (Reflect.Classes, Type.Named, Reflect.New by name), sorted by name: all of them at the stripping levels
        /// 0-2, the reached ones at 3.</summary>
        private List<RuntimeClass> ReflectionTableClasses()
        {
            IEnumerable<RuntimeClass> classes = _strip <= StrippingLevel.Methods
                ? _program.Program.Classes.Values
                : ReachedClasses().Where(rc => _program.Program.Classes.TryGetValue(rc.Name, out var known) && ReferenceEquals(known, rc));
            return classes.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>The constructors of a class (with the functions of their default values, `Reflect.New` with fewer arguments evaluates them): it can be made.</summary>
        private void KeepConstructors(RuntimeClass rc)
        {
            RegisterClass(rc.Name);
            if (Modular && !LibraryRole && !IsOwnClass(rc)) return;   // (a library keeps the constructors of its classes and makes their objects for Reflect.New)
            foreach (var ctor in rc.AllConstructors)
            {
                GetFunc(ctor, FuncKind.Ctor);
                int needed = ctor.ParamCount;
                while (needed > 0 && needed - 1 < ctor.ParamDefaults.Count && ctor.ParamDefaults[needed - 1] != null) needed--;
                for (int argc = needed; argc < ctor.ParamCount; argc++) DefaultArgs(ctor, argc, "self", "list");
            }
        }

        /// <summary>An instance method that a call by name may reach: its dispatchers for every number of arguments it takes.</summary>
        private void KeepInstanceMethod(string name, FunctionProto m)
        {
            GetFunc(m, FuncKind.Method);
            int required = m.ParamCount;
            while (required > 0 && required - 1 < m.ParamDefaults.Count && m.ParamDefaults[required - 1] != null) required--;
            for (int argc = required; argc <= m.ParamCount; argc++) AddDispatcher(name, argc);
            if (name.StartsWith("get_") || name.StartsWith("set_")) AddFieldName(name.Substring(4));
        }

        /// <summary>What reflection can reach by name (see the levels at the top).</summary>
        private void RegisterReflectionTargets()
        {
            if (!_usesReflection) return;
            if (_reflNew)
                foreach (var rc in ReflectionTableClasses()) KeepConstructors(rc);
            // (the names given as literals to reflection: the members of that name of the classes whose objects exist)
            if (_literalMembers.Count > 0)
                foreach (var cls in _classList.ToList())
                    for (var c = cls.Rc; c != null; c = c.Base)
                    {
                        if (Modular && !IsOwnClass(c)) continue;   // (a library keeps its API itself)
                        foreach (var field in c.OwnFieldInfo.Keys) if (_literalMembers.Contains(field)) AddFieldName(field);
                        foreach (var (name, protos) in c.Methods)
                            if (LiteralMember(name))
                                foreach (var m in protos) if (!m.IsStatic) KeepInstanceMethod(name, m);
                    }
            if (!_reflMembers && !_reflCall) return;
            // (modules: Get/Set by name go through the tables of the runtime - the program's tables list every field and property of its classes, a library's all of its
            // members; Reflect.Call the methods that are there, like with one file)
            if (Modular && !_tablesAll) { _tablesAll = true; _version++; }
            foreach (var cls in _classList.ToList())
            {
                if (_reflMembers && !Modular) foreach (var field in cls.Rc.FieldIndex.Keys) AddFieldName(field);
                for (var c = cls.Rc; c != null; c = c.Base)
                {
                    if (Modular && !IsOwnClass(c)) continue;   // (the table of a library's class names all its members)
                    foreach (var (name, protos) in c.Methods)
                        foreach (var m in protos)
                        {
                            if (m.IsStatic) continue;
                            // a method that is there (level 0: every one) - one that is left out is an error that says so (rf_call)
                            if (_reflCall && (_strip == StrippingLevel.None || _funcByProto.ContainsKey(m))) KeepInstanceMethod(name, m);
                            else if (!Modular && (name.StartsWith("get_") || name.StartsWith("set_"))) AddFieldName(name.Substring(4));   // (RegisterTargets makes the accessors of the names)
                        }
                }
            }
        }

        /// <summary>What stays although nothing may reach it (the stripping levels): everything at level 0 (`--keep-all`, debug builds); every class with its
        /// constructors at 1 and 2, at 1 also with every public member; at every level the classes and members marked `[Keep]` (SPEC 5.12; not inherited) or named as
        /// literals to reflection (those in <see cref="RegisterReflectionTargets"/>).</summary>
        private void RegisterKeptTargets()
        {
            foreach (var rc in _program.Program.Classes.Values.ToList())
            {
                // (modules: a library keeps all of its classes itself - the program only makes the kept classes of the libraries known)
                if (Modular && !IsOwnClass(rc))
                {
                    if (_strip <= StrippingLevel.Methods || KeptClass(rc)) KeepConstructors(rc);
                    else if (rc.KeptMembers != null) RegisterClass(rc.Name);
                    continue;
                }
                if (_strip == StrippingLevel.None || KeptClass(rc))
                {
                    KeepConstructors(rc);
                    foreach (var field in rc.FieldIndex.Keys) AddFieldName(field);
                    foreach (var (name, protos) in rc.Methods)
                        foreach (var m in protos) KeepMethod(name, m);
                    continue;
                }
                if (_strip <= StrippingLevel.Methods) KeepConstructors(rc);   // (levels 1 and 2: every class can be made - Reflect.New)
                if (_strip == StrippingLevel.Protected)
                    foreach (var (name, protos) in rc.Methods)
                        foreach (var m in protos)
                            if ((m.Access ?? AccessModifier.Public) == AccessModifier.Public) KeepMethod(name, m);
                if (rc.KeptMembers is { } kept)
                {
                    RegisterClass(rc.Name);
                    foreach (var name in kept)
                    {
                        if (rc.FieldIndex.ContainsKey(name)) AddFieldName(name);
                        foreach (var methodName in new[] { name, "get_" + name, "set_" + name })
                            if (rc.Methods.TryGetValue(methodName, out var protos))
                                foreach (var m in protos) KeepMethod(methodName, m);
                    }
                }
            }
        }

        private void KeepMethod(string name, FunctionProto m)
        {
            if (m.IsStatic) GetFunc(m, FuncKind.Static);
            else KeepInstanceMethod(name, m);
        }

        private static string CStr(string s) => CString(s);

        private static string StringList(string name, IReadOnlyList<string> items) =>
            items.Count == 0 ? "NULL" : name;

        /// <summary>Tables, the functions behind the `__refl_*` natives and the hooks - written after the dispatchers.</summary>
        private int _rfListIndex;

        /// <summary>A method that stripping left out (not generated): the highest level that keeps it (1 for a public one, else 0); null: it is there (or is not a method).
        /// Only the members of this module count (a library's table names all of its members).</summary>
        private int? StrippedMethod(RuntimeClass rc, MemberMeta m)
        {
            if (_strip == StrippingLevel.None || LibraryRole || m.Kind != "method" || !rc.Methods.TryGetValue(m.Name, out var protos)) return null;
            var mine = protos.Where(p => p.ParamCount == m.ParamNames.Count && p.IsStatic == m.IsStatic && (!Modular || OwnsProto(p))).ToList();
            if (mine.Count == 0 || mine.Any(p => _funcByProto.ContainsKey(p))) return null;
            return m.Access == "public" ? 1 : 0;
        }

        /// <summary>The rows of the reflection tables (RfClass) of these classes, in their order; their lists and functions go into `sb` first. A method that stripping
        /// left out is no row of its class, its name goes into the list `stripped` of the class (with the level that keeps it first: "1Bark") - for the error that
        /// says so.</summary>
        private List<string> ReflectionRows(StringBuilder sb, IEnumerable<RuntimeClass> classes)
        {
            // ---- tables ----------------------------------------------------------------------------------------------------
            int listIndex = _rfListIndex;
            // (a list of names is written once: the parameters of many methods are the same)
            var stringLists = new Dictionary<string, string>(StringComparer.Ordinal);
            string Strings(IReadOnlyList<string> items)
            {
                if (items.Count == 0) return "NULL";
                string text = string.Join(", ", items.Select(CStr));
                if (stringLists.TryGetValue(text, out var known)) return known;
                string name = stringLists[text] = $"{Px}kRfS{listIndex++}";
                sb.AppendLine($"static const char* const {name}[] = {{{text}}};");
                return name;
            }
            // the attributes of a member: a function that builds [[name, values, texts], ...] (the values the compiler knew, see AttributeMeta.ArgsJson)
            string Attributes(IReadOnlyList<AttributeMeta> attributes)
            {
                if (attributes.Count == 0) return "NULL";
                string name = $"{Px}rf_attrs{listIndex++}";
                var body = new StringBuilder();
                int temp = 0;
                string Json(System.Text.Json.Nodes.JsonNode? node)
                {
                    if (node is not System.Text.Json.Nodes.JsonObject o) return "Undef()";
                    if (o["i"] is { } i) return $"Int({i.GetValue<long>()}LL)";
                    if (o["f"] is { } f) return $"Float({DoubleLiteral(f.GetValue<double>(), false)})";
                    if (o["s"] is { } s) return $"strFromUtf8({CStr(s.GetValue<string>())}, list)";
                    if (o["b"] is { } b) return b.GetValue<bool>() ? "Bool(true)" : "Bool(false)";
                    if (o["a"] is System.Text.Json.Nodes.JsonArray a)
                    {
                        string arr = $"t{temp++}";
                        body.AppendLine($"    Arr* {arr} = allocArr({a.Count}, list);");
                        for (int k = 0; k < a.Count; k++) { string item = Json(a[k]); body.AppendLine($"    {{ Value x = {item}; arrItems({arr})[{k}] = x; retain(x); }}"); }
                        return $"ArrV({arr})";
                    }
                    return "Undef()";
                }
                body.AppendLine($"    Arr* all = allocArr({attributes.Count}, list);");
                for (int i = 0; i < attributes.Count; i++)
                {
                    var at = attributes[i];
                    var values = System.Text.Json.Nodes.JsonNode.Parse(at.ArgsJson) as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
                    string entry = $"t{temp++}", vals = $"t{temp++}";
                    body.AppendLine($"    Arr* {entry} = allocArr(3, list);");
                    body.AppendLine($"    {{ Value x = strFromUtf8({CStr(at.Name)}, list); arrItems({entry})[0] = x; retain(x); }}");
                    body.AppendLine($"    Arr* {vals} = allocArr({values.Count}, list);");
                    for (int k = 0; k < values.Count; k++) { string item = Json(values[k]); body.AppendLine($"    {{ Value x = {item}; arrItems({vals})[{k}] = x; retain(x); }}"); }
                    body.AppendLine($"    arrItems({entry})[1] = ArrV({vals});");
                    body.AppendLine($"    arrItems({entry})[2] = strArrayOf({Strings(at.ArgTexts)}, {at.ArgTexts.Count}, list);");
                    body.AppendLine($"    arrItems(all)[{i}] = ArrV({entry});");
                }
                body.AppendLine("    return ArrV(all);");
                sb.AppendLine($"static Value {name}(OwnList* list) {{");
                sb.Append(body);
                sb.AppendLine("}");
                return name;
            }
            var classRows = new List<string>();
            // the members a class declares itself (in the order of the source); the inherited ones come from the rows of its base classes (rf_visible)
            foreach (var rc in classes)
            {
                var memberRows = new List<string>();
                var stripped = new List<string>();
                foreach (var m in rc.Meta?.Members ?? new List<MemberMeta>())
                {
                    if (StrippedMethod(rc, m) is int keepAt)
                    {
                        if (!stripped.Any(x => x.Substring(1) == m.Name)) stripped.Add(keepAt.ToString(CultureInfo.InvariantCulture) + m.Name);
                        continue;
                    }
                    string pnames = Strings(m.ParamNames), ptypes = Strings(m.ParamTypes);
                    int flags = (m.IsStatic ? 1 : 0) | (m.IsReadonly ? 2 : 0) | (m.CanRead ? 4 : 0) | (m.CanWrite ? 8 : 0);
                    string attrs = Attributes(m.Attributes);
                    memberRows.Add($"{{{CStr(m.Name)}, {CStr(m.Kind)}, {CStr(m.TypeName)}, {CStr(m.Access)}, {flags}, {CStr(m.Unit)}, {CStr(rc.Name)}, {pnames}, {ptypes}, {m.ParamNames.Count}, {attrs}}}");
                }
                string membersName = "NULL";
                if (memberRows.Count > 0)
                {
                    membersName = $"{Px}kRfM{listIndex++}";
                    sb.AppendLine($"static const RfMember {membersName}[] = {{{string.Join(", ", memberRows)}}};");
                }
                var interfaces = new List<string>();
                if (rc.Meta != null)
                    foreach (var b in rc.Meta.BaseNames)
                        if (rc.Base == null || !(rc.Base.Name == b || rc.Base.Name.EndsWith("." + b, StringComparison.Ordinal))) interfaces.Add(b);
                string classAttrs = Attributes((IReadOnlyList<AttributeMeta>?)rc.Meta?.Attributes ?? Array.Empty<AttributeMeta>());
                classRows.Add($"{{{CStr(rc.Name)}, {(rc.Base != null ? CStr(rc.Base.Name) : "NULL")}, {(rc.IsActor ? 1 : 0)}, {Strings(interfaces)}, {interfaces.Count}, {membersName}, {memberRows.Count}, {classAttrs}, {Strings(stripped)}, {stripped.Count}, NULL}}");
            }
            _rfListIndex = listIndex;
            return classRows;
        }

        /// <summary>The constructors of a class for Reflect.New: an arm per number of arguments.</summary>
        private List<string> NewCases(RuntimeClass rc, ClassInfo info)
        {
            var byArgc = new List<string>();
            foreach (var ctor in rc.AllConstructors)
            {
                int required = ctor.ParamCount;
                while (required > 0 && required - 1 < ctor.ParamDefaults.Count && ctor.ParamDefaults[required - 1] != null) required--;
                for (int argc = required; argc <= ctor.ParamCount; argc++)
                {
                    if (rc.FindConstructor(argc) != ctor) continue;
                    var (pre, defaults) = DefaultArgs(ctor, argc, "o", "list");
                    string items = string.Concat(Enumerable.Range(0, argc).Select(i => $", items[{i}]"));
                    var access = ctor.Access ?? AccessModifier.Public;
                    string guard = CheckAccess && access != AccessModifier.Public
                        ? $"if (!{AllowedExpr(rc, access)}) return {(LibraryRole ? "rfFail" : "accessDeniedNew")}({CStr($"The constructor of '{rc.Name}' is {DescribeAccess(access)} and cannot be called from here.")}); "
                        : "";
                    byArgc.Add($"            case {argc}: {{ {guard}Value o = newObject({ClassIdExpr(info)}, {info.Fields.Count}, list); {pre}{GetFunc(ctor, FuncKind.Ctor).Name}(o{items}{defaults}); return o; }}");
                }
            }
            return byArgc;
        }

        private static string NewFunctionName(string module, RuntimeClass rc) => $"fire_new_{module}_{Mangle(rc.Name)}";

        /// <summary>The name of the reflection row of a class (modules: every module writes those of its classes).</summary>
        private static string RfRowName(string module, RuntimeClass rc) => $"fire_rf_{module}_{Mangle(rc.Name)}";

        /// <summary>A library module of a program with reflection: the rows of all of its classes (the program lists those its tables have).</summary>
        private string LibraryReflectionRows()
        {
            if (!_usesReflection) return "";
            var sb = new StringBuilder();
            var own = _program.Program.Classes.Values.Where(IsOwnClass).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            var rows = ReflectionRows(sb, own);
            // (a plugin: Reflect.New of the program makes the objects of its classes through their rows)
            if (_plugin)
                for (int i = 0; i < own.Count; i++)
                {
                    sb.AppendLine($"Value {NewFunctionName(ModId, own[i])}(int argc, const Value* items, OwnList* list, uint32_t caller);");
                    rows[i] = rows[i].Substring(0, rows[i].Length - "NULL}".Length) + $"{NewFunctionName(ModId, own[i])}}}";
                }
            for (int i = 0; i < own.Count; i++) sb.AppendLine($"const RfClass {RfRowName(ModId, own[i])} = {rows[i]};");
            // Reflect.New of its classes (the program's rf_new calls them by the name of the class)
            foreach (var rc in own)
            {
                sb.AppendLine($"Value {NewFunctionName(ModId, rc)}(int argc, const Value* items, OwnList* list, uint32_t caller) {{");
                sb.AppendLine("    (void)items; (void)list; (void)caller;");
                sb.AppendLine("    switch (argc) {");
                foreach (var row in NewCases(rc, RegisterClass(rc.Name))) sb.AppendLine(row);
                sb.AppendLine("        default: break;");
                sb.AppendLine("    }");
                sb.AppendLine($"    {{ char t[300]; snprintf(t, sizeof t, \"'%s' has no constructor with %d parameter(s).\", {CStr(rc.Name)}, argc); return rfFail(t); }}");
                sb.AppendLine("}");
            }
            return sb.ToString();
        }

        private string ReflectionCode()
        {
            var sb = new StringBuilder();
            var classes = ReflectionTableClasses();

            // ---- tables ----------------------------------------------------------------------------------------------------
            // (modules: the rows of the classes of a library are in its file, the program lists them)
            string rfAt = Modular ? "kRfClassList[i]" : "&kRfClasses[i]";
            if (Modular)
            {
                var own = classes.Where(IsOwnClass).ToList();
                var ownRows = ReflectionRows(sb, own);
                for (int i = 0; i < own.Count; i++) sb.AppendLine($"static const RfClass {RfRowName("prog", own[i])} = {ownRows[i]};");
                foreach (var rc in classes.Where(c => !IsOwnClass(c))) sb.AppendLine($"extern const RfClass {RfRowName(ModIdOf(rc), rc)};");
                sb.AppendLine($"static const RfClass* const kRfClassList[] = {{{string.Join(", ", classes.Select(rc => "&" + RfRowName(ModIdOf(rc), rc)).DefaultIfEmpty("NULL"))}}};");
            }
            else
            {
            var classRows = ReflectionRows(sb, classes);
            sb.AppendLine($"static const RfClass kRfClasses[] = {{{string.Join(",\n    ", classRows)}}};");
            }
            sb.AppendLine($"static const uint32_t kRfClassCount = {classes.Count};");
            // the classes stripping left out (level 3): a lookup of one by its name is an error that says so
            var inTables = new HashSet<RuntimeClass>(classes, ReferenceEqualityComparer.Instance);
            var strippedClasses = _strip == StrippingLevel.None ? new List<string>()
                : _program.Program.Classes.Values.Where(rc => !inTables.Contains(rc)).Select(rc => rc.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            sb.AppendLine($"FIRE_MAYBE_UNUSED static const int kRfStripping = {(int)_strip};");
            sb.AppendLine($"FIRE_MAYBE_UNUSED static const char* const kRfStrippedClasses[] = {{{string.Join(", ", strippedClasses.Select(CStr).DefaultIfEmpty("NULL"))}}};");
            sb.AppendLine($"FIRE_MAYBE_UNUSED static const uint32_t kRfStrippedCount = {strippedClasses.Count};");
            sb.AppendLine(@"FIRE_MAYBE_UNUSED static bool rf_isStripped(Value name) {
    if (name.kind != K_String) return false;
    for (uint32_t i = 0; i < kRfStrippedCount; i++) if (strIs(name, kRfStrippedClasses[i])) return true;
    return false;
}
/* a class that stripping left out (docs/NATIVE_BACKEND.md): the error says so and what keeps it */
FIRE_MAYBE_UNUSED static Value rf_strippedClass(Value name) {
    char n[200], t[700]; strToUtf8(name, n, sizeof n);
    snprintf(t, sizeof t, ""Class '%s' was left out of the build (stripping %d). Add [Keep] to '%s' or use stripping <= 2."", n, kRfStripping, n);
    return rfFail(t);
}");
            sb.AppendLine($"#define RF_CLASS_AT(i) ({rfAt.Replace("[i]", "[(i)]")})");
            if (_openWorld) sb.AppendLine("#define RF_PLUGIN_COUNT g_pluginRfCount   // (the classes of the plugins the program loaded)");
            sb.AppendLine("FIRE_MAYBE_UNUSED static const RfClass* rf_find(Value name) {");
            sb.AppendLine($"    for (uint32_t i = 0; i < kRfClassCount; i++) if (strIs(name, ({rfAt})->name)) return {rfAt};");
            if (_openWorld) sb.AppendLine("    for (uint32_t i = 0; i < g_pluginRfCount; i++) if (strIs(name, ((const RfClass*)g_pluginRf[i])->name)) return (const RfClass*)g_pluginRf[i];   // (the classes of plugins)");
            sb.AppendLine("    return NULL;");
            sb.AppendLine("}");
            sb.AppendLine("FIRE_MAYBE_UNUSED static const RfClass* rf_findC(const char* name) {");
            sb.AppendLine($"    for (uint32_t i = 0; i < kRfClassCount; i++) if (strcmp(name, ({rfAt})->name) == 0) return {rfAt};");
            if (_openWorld) sb.AppendLine("    for (uint32_t i = 0; i < g_pluginRfCount; i++) if (strcmp(name, ((const RfClass*)g_pluginRf[i])->name) == 0) return (const RfClass*)g_pluginRf[i];");
            sb.AppendLine("    return NULL;");
            sb.AppendLine("}");
            sb.AppendLine("FIRE_MAYBE_UNUSED static const RfClass* rf_classOf(uint32_t cls) { return rf_findC(className(cls)); }");
            // The members of a class with the inherited ones (like ReflectionNatives.Members of the VM): the class first, then its bases; a member is left out when a more derived
            // class - or the same class before it - declares one with the same key (a field or property by its name, a method by name and number of parameters);
            // the constructors are those of the class itself.
            sb.AppendLine(@"FIRE_MAYBE_UNUSED static const RfClass* rf_baseOf(const RfClass* c) { return c->base ? rf_findC(c->base) : NULL; }
FIRE_MAYBE_UNUSED static bool rf_sameKey(const RfMember* a, const RfMember* b) {
    bool am = strcmp(a->kind, ""method"") == 0, bm = strcmp(b->kind, ""method"") == 0;
    return am == bm && strcmp(a->name, b->name) == 0 && (!am || a->pcount == b->pcount);
}
FIRE_MAYBE_UNUSED static bool rf_visible(const RfClass* from, const RfClass* level, uint32_t index) {
    const RfMember* m = &level->members[index];
    if (strcmp(m->kind, ""constructor"") == 0) return level == from;
    for (const RfClass* c = from; c; c = rf_baseOf(c)) {
        uint32_t n = c == level ? index : c->nMembers;
        for (uint32_t i = 0; i < n; i++)
            if (strcmp(c->members[i].kind, ""constructor"") != 0 && rf_sameKey(&c->members[i], m)) return false;
        if (c == level) break;
    }
    return true;
}");

            // member lookup: 1 field, 2 property, 3 method (the closest, fields first like the VM), 0 nothing
            sb.AppendLine(@"FIRE_MAYBE_UNUSED static int rf_kind(const RfClass* c, Value name) {
    int best = 0;
    for (const RfClass* l = c; l; l = rf_baseOf(l))
        for (uint32_t i = 0; i < l->nMembers; i++) {
            const RfMember* m = &l->members[i];
            if (!strIs(name, m->name) || strcmp(m->kind, ""constructor"") == 0 || !rf_visible(c, l, i)) continue;
            int k = strcmp(m->kind, ""field"") == 0 ? ((m->flags & 1) ? 0 : 1) : strcmp(m->kind, ""property"") == 0 ? 2 : 3;
            if (k && (best == 0 || k < best)) best = k;
        }
    return best;
}");
            sb.AppendLine(@"FIRE_MAYBE_UNUSED static const RfMember* rf_member(const RfClass* c, Value name, const char* kind) {
    for (const RfClass* l = c; l; l = rf_baseOf(l))
        for (uint32_t i = 0; i < l->nMembers; i++)
            if (strIs(name, l->members[i].name) && strcmp(l->members[i].kind, kind) == 0 && rf_visible(c, l, i)) return &l->members[i];
    return NULL;
}");
            // a member that stripping left out: its class (or a base) names it in `stripped`, after the level that keeps it ("1Bark")
            sb.AppendLine(@"FIRE_MAYBE_UNUSED static const char* rf_stripped(const RfClass* c, Value name, const RfClass** in) {
    for (const RfClass* l = c; l; l = rf_baseOf(l))
        for (uint32_t i = 0; i < l->nStripped; i++) if (strIs(name, l->stripped[i] + 1)) { *in = l; return l->stripped[i]; }
    return NULL;
}
FIRE_MAYBE_UNUSED static Value rf_strippedMember(const RfClass* in, const char* entry) {
    char t[700]; int keep = entry[0] - '0';
    if (keep == 0) snprintf(t, sizeof t, ""Member '%s.%s' was left out of the build (stripping %d). Add [Keep] to '%s.%s' or use stripping 0."", in->name, entry + 1, kRfStripping, in->name, entry + 1);
    else snprintf(t, sizeof t, ""Member '%s.%s' was left out of the build (stripping %d). Add [Keep] to '%s.%s' or use stripping <= %d."", in->name, entry + 1, kRfStripping, in->name, entry + 1, keep);
    return rfFail(t);
}");
            sb.AppendLine("FIRE_MAYBE_UNUSED static bool rf_isSub(const char* name, const char* base) {");
            sb.AppendLine("    for (const RfClass* c = rf_findC(name); c; c = c->base ? rf_findC(c->base) : NULL) if (strcmp(c->name, base) == 0) return true;");
            sb.AppendLine("    return false;");
            sb.AppendLine("}");

            // ---- the natives -----------------------------------------------------------------------------------------------
            sb.AppendLine(@"FIRE_MAYBE_UNUSED static Value rf_class_name(Value x, OwnList* list) {
    if (x.kind == K_Class) return strFromUtf8(className(asObj(x)->cls), list);
    if (x.kind == K_String) { const RfClass* c = rf_find(x); if (c) return strFromUtf8(c->name, list); if (rf_isStripped(x)) return rf_strippedClass(x); }
    return Undef();
}
FIRE_MAYBE_UNUSED static Value rf_class_info(Value name, OwnList* list) {
    const RfClass* c = name.kind == K_String ? rf_find(name) : NULL;
    if (!c) return rf_isStripped(name) ? rf_strippedClass(name) : Undef();
    Arr* info = allocArr(5, list);
    Value v0 = strFromUtf8(c->name, list); arrItems(info)[0] = v0; retain(v0);
    if (c->base) { Value v1 = strFromUtf8(c->base, list); arrItems(info)[1] = v1; retain(v1); }
    arrItems(info)[2] = Bool(c->isActor != 0);
    Value v3 = strArrayOf(c->ifaces, c->nIfaces, list); arrItems(info)[3] = v3;
    arrItems(info)[4] = c->attrs ? c->attrs(list) : ArrV(allocArr(0, list));   /* (the attributes before the class) */
    return ArrV(info);
}
/* the enum of a value (its full name, undefined for anything else), and a number as a value of the enum of another value */
FIRE_MAYBE_UNUSED static Value rf_enum_name(Value v, OwnList* list) { const FireEnum* e = enumOf(v); return e ? strFromUtf8(e->name, list) : Undef(); }
FIRE_MAYBE_UNUSED static Value rf_enum_of(Value v, Value n) { return enumOf(v) && n.kind == K_Int ? EnumV(n.i, v.reserved) : n; }
FIRE_MAYBE_UNUSED static Value rf_members(Value name, OwnList* list) {
    const RfClass* c = name.kind == K_String ? rf_find(name) : NULL;
    if (!c) return Undef();
    uint32_t count = 0, at = 0;
    for (const RfClass* l = c; l; l = rf_baseOf(l))
        for (uint32_t i = 0; i < l->nMembers; i++) if (rf_visible(c, l, i)) count++;
    Arr* all = allocArr(count, list);
    for (const RfClass* l = c; l; l = rf_baseOf(l))
    for (uint32_t i = 0; i < l->nMembers; i++) {
        if (!rf_visible(c, l, i)) continue;
        const RfMember m = l->members[i];
        Arr* d = allocArr(13, list);
        const char* texts[] = {m.name, m.kind, m.type, m.access};
        for (int k = 0; k < 4; k++) { Value s = strFromUtf8(texts[k], list); arrItems(d)[k] = s; retain(s); }
        arrItems(d)[4] = Bool((m.flags & 1) != 0); arrItems(d)[5] = Bool((m.flags & 2) != 0);
        arrItems(d)[6] = Bool((m.flags & 4) != 0); arrItems(d)[7] = Bool((m.flags & 8) != 0);
        Value u = strFromUtf8(m.unit, list); arrItems(d)[8] = u; retain(u);
        Value dec = strFromUtf8(m.declared, list); arrItems(d)[9] = dec; retain(dec);
        arrItems(d)[10] = strArrayOf(m.pnames, m.pcount, list);
        arrItems(d)[11] = strArrayOf(m.ptypes, m.pcount, list);
        arrItems(d)[12] = m.attrs ? m.attrs(list) : ArrV(allocArr(0, list));
        arrItems(all)[at++] = ArrV(d);
    }
    return ArrV(all);
}
FIRE_MAYBE_UNUSED static Value rf_classes(OwnList* list) {
    Arr* names = allocArr(kRfClassCount + RF_PLUGIN_COUNT, list);
    for (uint32_t i = 0; i < kRfClassCount; i++) { Value s = strFromUtf8(RF_CLASS_AT(i)->name, list); arrItems(names)[i] = s; retain(s); }
    for (uint32_t i = 0; i < RF_PLUGIN_COUNT; i++) { Value s = strFromUtf8(((const RfClass*)g_pluginRf[i])->name, list); arrItems(names)[kRfClassCount + i] = s; retain(s); }
    return ArrV(names);
}
FIRE_MAYBE_UNUSED static Value rf_is_sub(Value name, Value base) {
    char a[200], b[200];
    strToUtf8(name, a, sizeof a); strToUtf8(base, b, sizeof b);
    return Bool(rf_isSub(a, b));
}
FIRE_MAYBE_UNUSED static Value rf_has(Value obj, Value name) {
    if (obj.kind != K_Class) return Bool(false);
    return Bool(rf_kind(rf_classOf(asObj(obj)->cls), name) != 0);
}
FIRE_MAYBE_UNUSED static Value rf_member_kind(Value obj, Value name, OwnList* list) {
    if (obj.kind != K_Class) return Undef();
    switch (rf_kind(rf_classOf(asObj(obj)->cls), name)) {
        case 1: return strFromUtf8(""field"", list);
        case 2: return strFromUtf8(""property"", list);
        case 3: return strFromUtf8(""method"", list);
        default: return Undef();
    }
}
FIRE_MAYBE_UNUSED static Value rf_selector_path(Value l, OwnList* list) {
    if (l.kind != K_Lambda) {
        char text[200];
        snprintf(text, sizeof text, ""A selector ('lambda member<...>' etc.) expects a lambda like `c => c.radius`, got: %s."", kindName(l));
        return rfFail(text);
    }
    Lam* lam = lamOf(l);
    if (!lam->sel) return rfFail(""The lambda is not a selector: it needs exactly one parameter, and its body may only be a member chain on it (`c => c.radius`, `p => p.address.city`)."");
    return strArrayOf(lam->sel, lam->nsel, list);
}");

            // ---- get / set / call / new ------------------------------------------------------------------------------------
            var fieldNames = _fieldNames.OrderBy(n => n, StringComparer.Ordinal).ToList();
            string tail(string name, bool setter) => (FieldHasList(name) ? ", list" : "") + (FieldHasCaller(name) ? ", caller" : "");
            var get = new StringBuilder();
            var set = new StringBuilder();
            if (Modular)
            {
                get.AppendLine("    { char n_[200]; strToUtf8(name, n_, sizeof n_); return fireGetField(obj, n_, list, caller); }");
                // (an object with probes: they hear of it - the member is written by its name)
                set.AppendLine("    { char n_[200]; strToUtf8(name, n_, sizeof n_);" + (_usesProbes ? " if (FIRE_UNLIKELY(asObj(obj)->h.flags & 2)) { probedSet(obj, n_, value, NULL, NULL, caller); return Undef(); }" : "") + " fireSetField(obj, value, n_, list, caller); return Undef(); }");
            }
            foreach (var name in Modular ? new List<string>() : fieldNames)
            {
                get.AppendLine($"    if (strIs(name, {CStr(name)})) return gf_{Mangle(name)}(obj{tail(name, false)});");
                set.AppendLine($"    if (strIs(name, {CStr(name)})) {{ sf_{Mangle(name)}(obj, value{tail(name, true)}); return Undef(); }}");
            }
            sb.AppendLine($@"FIRE_MAYBE_UNUSED static Value rf_get(Value obj, Value name, OwnList* list, uint32_t caller) {{
    (void)list; (void)caller;
    if (obj.kind != K_Class) {{ char t[120]; snprintf(t, sizeof t, ""Reflect.Get: expects an object, got: %s."", kindName(obj)); return rfFail(t); }}
    const RfClass* c = rf_classOf(asObj(obj)->cls);
    int kind = rf_kind(c, name);
    const RfMember* getter = rf_member(c, name, ""property"");
    if (kind != 1 && !(getter && (getter->flags & 4))) {{ char n[200], t[500]; strToUtf8(name, n, sizeof n); snprintf(t, sizeof t, ""'%s' has no readable member '%s'."", c->name, n); return rfFail(t); }}
{get}    {{ const RfClass* in = NULL; const char* s = rf_stripped(c, name, &in); if (s) return rf_strippedMember(in, s); }}
    {{ char n[200], t[700]; strToUtf8(name, n, sizeof n); snprintf(t, sizeof t, ""Member '%s.%s' was left out of the build (stripping %d): it cannot be read by name. Add [Keep] to it or use stripping 0."", c->name, n, kRfStripping); return rfFail(t); }}
}}");
            sb.AppendLine($@"FIRE_MAYBE_UNUSED static Value rf_set(Value obj, Value name, Value value, OwnList* list, uint32_t caller) {{
    (void)list; (void)caller;
    if (obj.kind != K_Class) {{ char t[120]; snprintf(t, sizeof t, ""Reflect.Set: expects an object, got: %s."", kindName(obj)); return rfFail(t); }}
    const RfClass* c = rf_classOf(asObj(obj)->cls);
    int kind = rf_kind(c, name);
    const RfMember* prop = rf_member(c, name, ""property"");
    if (kind != 1 && !(prop && (prop->flags & 8))) {{
        char n[200], t[500]; strToUtf8(name, n, sizeof n);
        if (prop && (prop->flags & 4)) snprintf(t, sizeof t, ""The property '%s' of '%s' has no setter (only 'get')."", n, c->name);
        else snprintf(t, sizeof t, ""'%s' has no writable member '%s'."", c->name, n);
        return rfFail(t);
    }}
    {{ const RfMember* field = rf_member(c, name, ""field""); if (field && !(field->flags & 1) && (field->flags & 2)) {{
        char n[200], t[500]; strToUtf8(name, n, sizeof n);
        snprintf(t, sizeof t, ""The field '%s' of '%s' is 'readonly' and cannot be assigned."", n, field->declared);
        return rfFail(t);
    }} }}
{set}    {{ char n[200], t[700]; strToUtf8(name, n, sizeof n); snprintf(t, sizeof t, ""Member '%s.%s' was left out of the build (stripping %d): it cannot be written by name. Add [Keep] to it or use stripping 0."", c->name, n, kRfStripping); return rfFail(t); }}
}}");

            // calls: one entry per (name, number of arguments) that some class has
            var callSb = new StringBuilder();
            if (Modular)
                callSb.AppendLine(@"    { char n_[200]; uint32_t k_; const FireMethod* m_; const RfMember* sm_; strToUtf8(name, n_, sizeof n_);
      m_ = fireFindMethod(asObj(obj)->cls, n_, argc, &k_);
      if (m_) return m_->fn(k_, obj, items, list, caller);
      sm_ = rf_member(rf_classOf(asObj(obj)->cls), name, ""method"");
      if (sm_ && (sm_->flags & 1) && sm_->pcount == (uint32_t)argc) { char u[400]; snprintf(u, sizeof u, ""'%s' is a static method - Reflect.Call only calls instance methods."", n_); return rfFail(u); } }");
            foreach (var (name, argc) in Modular ? new List<(string Name, int Argc)>() : _dispatchers.Where(d => !d.Name.StartsWith("operator")).ToList())
            {
                var inst = new List<int>();
                var stat = new List<int>();
                foreach (var cls in _classList)
                {
                    var found = cls.Rc.FindMethodWithAccess(name, argc);
                    if (found.Proto == null) continue;
                    (found.Proto.IsStatic ? stat : inst).Add(cls.Id);
                }
                if (inst.Count == 0 && stat.Count == 0) continue;
                string items = string.Concat(Enumerable.Range(0, argc).Select(i => $", items[{i}]"));
                callSb.AppendLine($"    if (argc == {argc} && strIs(name, {CStr(name)})) {{");
                callSb.AppendLine("        switch (asObj(obj)->cls) {");
                if (inst.Count > 0) callSb.AppendLine("            " + string.Concat(inst.Select(i => $"case {i}: ")) + $"return call_{Mangle(name)}_{argc}(obj{items}{DispatchTail(name, argc, "list", "caller")});");
                if (stat.Count > 0) callSb.AppendLine("            " + string.Concat(stat.Select(i => $"case {i}: ")) + $"{{ char t[300]; strToUtf8(name, t, sizeof t); char u[400]; snprintf(u, sizeof u, \"'%s' is a static method - Reflect.Call only calls instance methods.\", t); return rfFail(u); }}");
                callSb.AppendLine("            default: break;");
                callSb.AppendLine("        }");
                callSb.AppendLine("    }");
            }
            sb.AppendLine($@"FIRE_MAYBE_UNUSED static Value rf_call(Value obj, Value name, Value args, OwnList* list, uint32_t caller) {{
    (void)list; (void)caller;
    if (obj.kind != K_Class) {{ char t[120]; snprintf(t, sizeof t, ""Reflect.Call: expects an object, got: %s."", kindName(obj)); return rfFail(t); }}
    int argc = args.kind == K_Array ? (int)arrOf(args)->length : 0;
    const Value* items = args.kind == K_Array ? arrItems(arrOf(args)) : NULL; (void)items;
{callSb}    {{ const RfClass* in = NULL; const char* s = rf_stripped(rf_classOf(asObj(obj)->cls), name, &in); if (s) return rf_strippedMember(in, s); }}
    char n[200], t[500]; strToUtf8(name, n, sizeof n);
    snprintf(t, sizeof t, ""'%s' has no method '%s' with %d parameter(s)."", className(asObj(obj)->cls), n, argc);
    return rfFail(t);
}}");

            // new: a constructor per class and number of arguments
            var newSb = new StringBuilder();
            foreach (var rc in classes)
            {
                // (without Reflect.New the constructors are not kept: rf_new is not called)
                if (!_reflNew || !_classes.TryGetValue(rc.Name, out var info)) continue;
                // (modules: the class of a library makes its objects in its own file)
                if (Modular && !IsOwnClass(rc))
                {
                    string fn = NewFunctionName(ModIdOf(rc), rc);
                    sb.Insert(0, $"Value {fn}(int argc, const Value* items, OwnList* list, uint32_t caller);\n");
                    newSb.AppendLine($"    if (strIs(cname, {CStr(rc.Name)})) return {fn}(argc, items, list, caller);");
                    continue;
                }
                var byArgc = NewCases(rc, info);
                if (byArgc.Count == 0) continue;
                newSb.AppendLine($"    if (strIs(cname, {CStr(rc.Name)})) {{");
                newSb.AppendLine("        switch (argc) {");
                foreach (var row in byArgc) newSb.AppendLine(row);
                newSb.AppendLine("            default: break;");
                newSb.AppendLine("        }");
                newSb.AppendLine($"        {{ char t[300]; snprintf(t, sizeof t, \"'%s' has no constructor with %d parameter(s).\", {CStr(rc.Name)}, argc); return rfFail(t); }}");
                newSb.AppendLine("    }");
            }
            sb.AppendLine(@"FIRE_MAYBE_UNUSED static Value accessDeniedNew(const char* message) { return rfFail(message); }");
            sb.AppendLine($@"FIRE_MAYBE_UNUSED static Value rf_new(Value cname, Value args, OwnList* list, uint32_t caller) {{
    (void)caller; (void)list;
    int argc = args.kind == K_Array ? (int)arrOf(args)->length : 0;
    const Value* items = args.kind == K_Array ? arrItems(arrOf(args)) : NULL; (void)items;
{newSb}{(_openWorld ? "    { const RfClass* pc = rf_find(cname); if (pc && pc->make) return pc->make(argc, items, list, caller); }   // (a class of a plugin)\n" : "")}    if (rf_isStripped(cname)) return rf_strippedClass(cname);
    char n[200], t[500]; strToUtf8(cname, n, sizeof n);
    if (!rf_find(cname)) snprintf(t, sizeof t, ""Unknown class '%s'."", n);
    else snprintf(t, sizeof t, ""'%s' has no constructor with %d parameter(s)."", n, argc);
    return rfFail(t);
}}");
            if (_usesProbes) sb.Append(ProbeCode(fieldNames));
            if (!_openWorld)
            {
                // (no plugins: rf_classes lists the program's classes - without the line's end in the pattern: the text above is a verbatim string, and a checkout
                // on Windows (.gitattributes: text=auto) gives it \r\n)
                sb.Replace("    for (uint32_t i = 0; i < RF_PLUGIN_COUNT; i++) { Value s = strFromUtf8(((const RfClass*)g_pluginRf[i])->name, list); arrItems(names)[kRfClassCount + i] = s; retain(s); }", "");
                sb.Replace("allocArr(kRfClassCount + RF_PLUGIN_COUNT, list)", "allocArr(kRfClassCount, list)");
            }
            return sb.ToString();
        }

        /// <summary>The setter of a name that looks for probes: the setter that is generated for the name becomes `sfo_`, `sf_` checks the flag of the object first.</summary>
        private string WrapProbeSetter(string name, string text)
        {
            string m = Mangle(name);
            bool list = FieldHasList(name), caller = FieldHasCaller(name);
            string lp = (list ? ", OwnList* list" : "") + (caller ? ", uint32_t caller" : "");
            string callArgs = (list ? ", list" : "") + (caller ? ", caller" : "");
            text = text.Replace($"{HelperInline}void sf_{m}(", $"static inline void sfo_{m}(");
            var sb = new StringBuilder(text);
            sb.AppendLine($"static void sfa_{m}(Value v, Value x, OwnList* list, uint32_t caller) {{ (void)list; (void)caller; sfo_{m}(v, x{callArgs}); }}");
            // the old value as the probe sees it: the field, else the getter, else undefined (no access check)
            var arms = new List<(int, string)>();
            foreach (var cls in _classList)
            {
                if (cls.Rc.FieldIndex.TryGetValue(name, out int index)) arms.Add((cls.Id, $"return objFields(o)[{index}];"));
                else if (cls.Rc.FindMethodWithAccess("get_" + name, 0).Proto is { IsStatic: false } getter) arms.Add((cls.Id, $"{{ Value r = {GetFunc(getter, FuncKind.Method).Name}(v); adopt(r, list); return r; }}"));
            }
            sb.AppendLine($"static Value gfp_{m}(Value v, OwnList* list) {{\n    Obj* o = asObj(v); (void)list;\n    switch (o->cls) {{\n{MergedCases(arms)}        default: return Undef();\n    }}\n}}");
            sb.AppendLine($"{HelperInline}void sf_{m}(Value v, Value x{lp}) {{");
            sb.AppendLine($"    if (FIRE_UNLIKELY(asObj(v)->h.flags & 2)) {{ probedSet(v, {CStr(name)}, x, sfa_{m}, gfp_{m}, {(caller ? "caller" : "0xFFFFFFFFu")}); return; }}");
            sb.AppendLine($"    sfo_{m}(v, x{callArgs});");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>`probe`/`silence`: the registration of probes and the natives (SPEC 8.14); the setters are wrapped by <see cref="WrapProbeSetter"/>.</summary>
        private string ProbeCode(List<string> fieldNames)
        {
            var sb = new StringBuilder();
            // the computed properties (a getter without a setter) of the classes: what their getter reads (RuntimeClass.ComputedSources) and the getter itself
            // (a getter and what it reads are written once: the classes that inherit the getter share them, and classes with the same rows share their arm)
            var computedArms = new List<(int, string)>();
            var computedIds = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var cls in _classList)
            {
                var names = new HashSet<string>();
                for (var c = cls.Rc; c != null; c = c.Base)
                    foreach (var key in c.Methods.Keys) if (key.StartsWith("get_")) names.Add(key.Substring(4));
                var rows = new StringBuilder();
                foreach (var name in names.OrderBy(n => n, StringComparer.Ordinal))
                {
                    if (cls.Rc.ComputedSources(name) is not { } sources || cls.Rc.FindMethodWithAccess("get_" + name, 0).Proto is not { } getter || getter.IsStatic || !_funcByProto.TryGetValue(getter, out var getterFunc)) continue;
                    string sourceList = string.Concat(sources.Select(src => CStr(src) + ", "));
                    string key = getterFunc.Name + "|" + sourceList;
                    if (!computedIds.TryGetValue(key, out int id))
                    {
                        computedIds[key] = id = computedIds.Count;
                        sb.AppendLine($"static const char* const rfc_{id}[] = {{{sourceList}NULL}};");
                        sb.AppendLine($"static Value rfg_{id}(Value v, OwnList* list) {{ Value r = {getterFunc.Name}(v); adopt(r, list); return r; }}");
                    }
                    rows.Append($" if (strcmp(member, {CStr(name)}) == 0) {{ *getter = rfg_{id}; return rfc_{id}; }}");
                }
                if (rows.Length > 0) computedArms.Add((cls.Id, "{" + rows + " break; }"));
            }
            // the members a probe can be put on (fields, properties, methods - also without the reflection metadata of `#import "reflection"`)
            // (per class the names it declares itself, written once; a class looks through the lists of its chain)
            var memberArms = new List<(int, string)>();
            var ownNames = new Dictionary<RuntimeClass, string?>(ReferenceEqualityComparer.Instance);
            string? OwnNames(RuntimeClass c)
            {
                if (ownNames.TryGetValue(c, out var known)) return known;
                var names = new SortedSet<string>(c.Fields.Select(f => f.Name), StringComparer.Ordinal);
                foreach (var key in c.Methods.Keys) names.Add(key.StartsWith("get_") || key.StartsWith("set_") ? key.Substring(4) : key);
                string? list = null;
                if (names.Count > 0)
                {
                    list = $"rfn_{ownNames.Count}";
                    sb.AppendLine($"static const char* const {list}[] = {{{string.Concat(names.Select(n => CStr(n) + ", "))}NULL}};");
                }
                return ownNames[c] = list;
            }
            foreach (var cls in _classList)
            {
                var chain = new List<string>();
                for (var c = cls.Rc; c != null; c = c.Base) if (OwnNames(c) is { } list) chain.Add(list);
                if (chain.Count > 0) memberArms.Add((cls.Id, $"{{ static const char* const* const chain[] = {{{string.Concat(chain.Select(l => l + ", "))}NULL}}; return rf_inNames(chain, member); }}"));
            }
            sb.AppendLine("FIRE_MAYBE_UNUSED static bool rf_inNames(const char* const* const* chain, const char* member) {\n    for (; *chain; chain++) for (const char* const* p = *chain; *p; p++) if (strcmp(*p, member) == 0) return true;\n    return false;\n}");
            sb.AppendLine($"FIRE_MAYBE_UNUSED static bool rf_probeHas(uint32_t cls, const char* member) {{\n    switch (cls) {{\n{MergedCases(memberArms)}        default: return false;\n    }}\n}}");
            sb.AppendLine($"FIRE_MAYBE_UNUSED static const char* const* rf_computed(uint32_t cls, const char* member, ProbeRawGet* getter) {{\n    (void)member; (void)getter;\n    switch (cls) {{\n{MergedCases(computedArms)}        default: break;\n    }}\n    return NULL;\n}}");
            sb.AppendLine(@"FIRE_MAYBE_UNUSED static int64_t rf_probeAdd(Value target, const char* member, bool changing, Value handler, char* err, size_t cap) {
    if (target.kind != K_Class) { snprintf(err, cap, ""'probe' expects an object, got: %s."", kindName(target)); return -1; }
    if (handler.kind != K_Lambda) { snprintf(err, cap, ""The handler of a probe must be a lambda, got: %s."", kindName(handler)); return -1; }
    if (lamOf(handler)->nparams > 4) { snprintf(err, cap, ""The handler of a probe may have at most 4 parameters (object, name, old, new), it has %u."", (unsigned)lamOf(handler)->nparams); return -1; }
    if (member) {
        const char* cname = className(asObj(target)->cls);
        if (!rf_probeHas(asObj(target)->cls, member)) { snprintf(err, cap, ""'%s' has no member '%s' - no probe can be registered there."", cname, member); return -1; }
        // a computed property: the probe watches what its getter reads and compares the value
        ProbeRawGet getter = NULL;
        const char* const* deps = rf_computed(asObj(target)->cls, member, &getter);
        if (deps) {
            if (changing) { snprintf(err, cap, ""'%s' of '%s' is a computed property (a getter without a setter): it can only be probed with 'changed'."", member, cname); return -1; }
            OwnList current = {NULL, NULL, poolMark(), 0, NULL, NULL};
            Value now = getter(target, &current);
            if (FIRE_UNLIKELY(g_unwind.active)) { leave(&current); return -2; }   // the getter threw: its exception is already unwinding, no probe
            int64_t id = probeAddEntryFull(asObj(target), member, changing, handler, deps, getter, now);
            leave(&current);
            return id;
        }
    }
    return probeAddEntry(asObj(target), member, changing, handler);
}
FIRE_MAYBE_UNUSED static bool rf_silenceCore(Value target, const char* member, char* err, size_t cap) {
    if (target.kind != K_Class) { snprintf(err, cap, ""'silence' expects an object, got: %s."", kindName(target)); return false; }
    probeSilence(asObj(target), member);
    return true;
}
FIRE_MAYBE_UNUSED static bool rf_silenceValue(Value v, char* err, size_t cap) {
    if (v.kind == K_Class) return rf_silenceCore(v, NULL, err, cap);
    if (v.kind == K_Int) { probeSilenceHandle(v.i); return true; }
    snprintf(err, cap, ""'silence' expects a probe handle or an object, got: %s."", kindName(v));
    return false;
}
FIRE_MAYBE_UNUSED static Value rf_probe(Value obj, Value member, Value kind, Value handler, OwnList* list, uint32_t caller) {
    (void)list; (void)caller;
    char k[64];
    strToUtf8(kind, k, sizeof k);
    if (strcmp(k, ""changed"") != 0 && strcmp(k, ""changing"") != 0) {
        char t[200]; snprintf(t, sizeof t, ""The kind of a probe is \""changed\"" or \""changing\"", got: \""%s\""."", k); return rfFail(t);
    }
    char m[200]; const char* mem = NULL;
    if (member.kind == K_String) { strToUtf8(member, m, sizeof m); mem = m; }
    char err[500];
    int64_t id = rf_probeAdd(obj, mem, strcmp(k, ""changing"") == 0, handler, err, sizeof err);
    if (id == -2) return Undef();   // the getter's own exception unwinds
    if (id < 0) return rfFail(err);
    return Int(id);
}
FIRE_MAYBE_UNUSED static Value rf_silence(Value obj, Value member) {
    char m[200]; const char* mem = NULL;
    if (member.kind == K_String) { strToUtf8(member, m, sizeof m); mem = m; }
    char err[500];
    if (!rf_silenceCore(obj, mem, err, sizeof err)) return rfFail(err);
    return Undef();
}
FIRE_MAYBE_UNUSED static Value rf_silence_handle(Value handle) {
    char err[500];
    if (!rf_silenceValue(handle, err, sizeof err)) return rfFail(err);
    return Undef();
}");
            return sb.ToString();
        }
    }
}
