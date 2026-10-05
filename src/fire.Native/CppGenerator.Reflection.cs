using System.Globalization;
using System.Text;
using fire.Ast;
using fire.Bytecode;
using fire.Standard;
using fire.Values;

namespace fire.Native
{
    /// <summary>
    /// Reflection (`#import "reflection"`, SPEC 8.13) and `probe`/`silence` (SPEC 8.14) in the native backend. When a program calls one of the `__refl_*`
    /// functions every class of the program is generated (a class can be reached by its name), with its constructors, methods and the field helpers of
    /// all its names; the descriptions are tables, the access by name is generated code over the field helpers and dispatchers.
    /// </summary>
    public sealed partial class CppGenerator
    {
        /// <summary>The program uses reflection: all classes are generated and the tables are written.</summary>
        private bool _usesReflection;
        /// <summary>The program uses `probe`: the field setters look for probes.</summary>
        private bool _usesProbes;

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
            if (_usesProbes) return;
            _usesProbes = true;
            _version++;
        }

        private static readonly string[] ReflectionNatives =
        {
            "__refl_class_name", "__refl_class_info", "__refl_members", "__refl_classes", "__refl_is_sub", "__refl_get", "__refl_set", "__refl_call", "__refl_new",
            "__refl_has", "__refl_selector_path", "__refl_member_kind", "__refl_probe", "__refl_silence", "__refl_silence_handle",
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

        /// <summary>Every class, constructor, method and member name of the program is reachable by name now.</summary>
        private void RegisterReflectionTargets()
        {
            if (!_usesReflection) return;
            foreach (var rc in _program.Program.Classes.Values.ToList())
            {
                RegisterClass(rc.Name);
                foreach (var ctor in rc.Constructors.Values) GetFunc(ctor, FuncKind.Ctor);
                foreach (var (name, protos) in rc.Methods)
                    foreach (var m in protos)
                    {
                        if (m.IsStatic) continue;
                        GetFunc(m, FuncKind.Method);
                        int required = m.ParamCount;
                        while (required > 0 && required - 1 < m.ParamDefaults.Count && m.ParamDefaults[required - 1] != null) required--;
                        for (int argc = required; argc <= m.ParamCount; argc++) _dispatchers.Add((name, argc));
                        if (name.StartsWith("get_") || name.StartsWith("set_")) _fieldNames.Add(name.Substring(4));
                    }
                foreach (var field in rc.FieldIndex.Keys) _fieldNames.Add(field);
            }
        }

        private static string CStr(string s) => CString(s);

        private sealed record RfEntry(MemberMeta Member, RuntimeClass DeclaredIn);

        /// <summary>The members of a class with the inherited ones: a derived class hides what the base declares under the same name, constructors are the class's own.</summary>
        private static List<RfEntry> FlattenMembers(RuntimeClass rc)
        {
            var seen = new HashSet<string>();
            var result = new List<RfEntry>();
            for (var c = rc; c != null; c = c.Base)
            {
                if (c.Meta == null) continue;
                foreach (var m in c.Meta.Members)
                {
                    if (m.Kind == "constructor")
                    {
                        if (!ReferenceEquals(c, rc) && c.Name != rc.Name) continue;
                    }
                    else
                    {
                        string key = m.Kind == "method" ? "m:" + m.Name + "/" + m.ParamNames.Count : "f:" + m.Name;
                        if (!seen.Add(key)) continue;
                    }
                    result.Add(new RfEntry(m, c));
                }
            }
            return result;
        }

        private static string StringList(string name, IReadOnlyList<string> items) =>
            items.Count == 0 ? "nullptr" : name;

        /// <summary>Tables, the functions behind the `__refl_*` natives and the hooks - written after the dispatchers.</summary>
        private string ReflectionCode()
        {
            var sb = new StringBuilder();
            var classes = _program.Program.Classes.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

            // ---- tables ----------------------------------------------------------------------------------------------------
            int listIndex = 0;
            string Strings(IReadOnlyList<string> items)
            {
                if (items.Count == 0) return "nullptr";
                string name = $"kRfS{listIndex++}";
                sb.AppendLine($"static const char* const {name}[] = {{{string.Join(", ", items.Select(CStr))}}};");
                return name;
            }
            var classRows = new List<string>();
            foreach (var rc in classes)
            {
                var members = FlattenMembers(rc);
                var memberRows = new List<string>();
                foreach (var e in members)
                {
                    var m = e.Member;
                    string pnames = Strings(m.ParamNames), ptypes = Strings(m.ParamTypes);
                    int flags = (m.IsStatic ? 1 : 0) | (m.IsReadonly ? 2 : 0) | (m.CanRead ? 4 : 0) | (m.CanWrite ? 8 : 0);
                    memberRows.Add($"{{{CStr(m.Name)}, {CStr(m.Kind)}, {CStr(m.TypeName)}, {CStr(m.Access)}, {flags}, {CStr(m.Unit)}, {CStr(e.DeclaredIn.Name)}, {pnames}, {ptypes}, {m.ParamNames.Count}}}");
                }
                string membersName = "nullptr";
                if (memberRows.Count > 0)
                {
                    membersName = $"kRfM{listIndex++}";
                    sb.AppendLine($"static const RfMember {membersName}[] = {{{string.Join(", ", memberRows)}}};");
                }
                var interfaces = new List<string>();
                if (rc.Meta != null)
                    foreach (var b in rc.Meta.BaseNames)
                        if (rc.Base == null || !(rc.Base.Name == b || rc.Base.Name.EndsWith("." + b, StringComparison.Ordinal))) interfaces.Add(b);
                classRows.Add($"{{{CStr(rc.Name)}, {(rc.Base != null ? CStr(rc.Base.Name) : "nullptr")}, {(rc.IsActor ? 1 : 0)}, {Strings(interfaces)}, {interfaces.Count}, {membersName}, {memberRows.Count}}}");
            }
            sb.AppendLine($"static const RfClass kRfClasses[] = {{{string.Join(",\n    ", classRows)}}};");
            sb.AppendLine($"static const uint32_t kRfClassCount = {classes.Count};");
            sb.AppendLine("static const RfClass* rf_find(Value name) {");
            sb.AppendLine("    for (uint32_t i = 0; i < kRfClassCount; i++) if (strIs(name, kRfClasses[i].name)) return &kRfClasses[i];");
            sb.AppendLine("    return nullptr;");
            sb.AppendLine("}");
            sb.AppendLine("static const RfClass* rf_findC(const char* name) {");
            sb.AppendLine("    for (uint32_t i = 0; i < kRfClassCount; i++) if (std::strcmp(name, kRfClasses[i].name) == 0) return &kRfClasses[i];");
            sb.AppendLine("    return nullptr;");
            sb.AppendLine("}");
            sb.AppendLine("static const RfClass* rf_classOf(uint32_t cls) { return rf_findC(className(cls)); }");

            // member lookup: 1 field, 2 property, 3 method (the closest, fields first like the VM), 0 nothing
            sb.AppendLine(@"static int rf_kind(const RfClass* c, Value name) {
    int best = 0;
    for (uint32_t i = 0; i < c->nMembers; i++) {
        const RfMember& m = c->members[i];
        if (!strIs(name, m.name) || std::strcmp(m.kind, ""constructor"") == 0) continue;
        int k = std::strcmp(m.kind, ""field"") == 0 ? ((m.flags & 1) ? 0 : 1) : std::strcmp(m.kind, ""property"") == 0 ? 2 : 3;
        if (k && (best == 0 || k < best)) best = k;
    }
    return best;
}");
            sb.AppendLine(@"static const RfMember* rf_member(const RfClass* c, Value name, const char* kind) {
    for (uint32_t i = 0; i < c->nMembers; i++)
        if (strIs(name, c->members[i].name) && std::strcmp(c->members[i].kind, kind) == 0) return &c->members[i];
    return nullptr;
}");
            sb.AppendLine("static bool rf_isSub(const char* name, const char* base) {");
            sb.AppendLine("    for (const RfClass* c = rf_findC(name); c; c = c->base ? rf_findC(c->base) : nullptr) if (std::strcmp(c->name, base) == 0) return true;");
            sb.AppendLine("    return false;");
            sb.AppendLine("}");

            // ---- the natives -----------------------------------------------------------------------------------------------
            sb.AppendLine(@"static Value rf_class_name(Value x, OwnList* list) {
    if (x.kind == K_Class) return strFromUtf8(className(asObj(x)->cls), list);
    if (x.kind == K_String) if (const RfClass* c = rf_find(x)) return strFromUtf8(c->name, list);
    return Undef();
}
static Value rf_class_info(Value name, OwnList* list) {
    const RfClass* c = name.kind == K_String ? rf_find(name) : nullptr;
    if (!c) return Undef();
    Arr* info = allocArr(4, list);
    Value v0 = strFromUtf8(c->name, list); info->items()[0] = v0; retain(v0);
    if (c->base) { Value v1 = strFromUtf8(c->base, list); info->items()[1] = v1; retain(v1); }
    info->items()[2] = Bool(c->isActor != 0);
    Value v3 = strArrayOf(c->ifaces, c->nIfaces, list); info->items()[3] = v3;
    return ArrV(info);
}
static Value rf_members(Value name, OwnList* list) {
    const RfClass* c = name.kind == K_String ? rf_find(name) : nullptr;
    if (!c) return Undef();
    Arr* all = allocArr(c->nMembers, list);
    for (uint32_t i = 0; i < c->nMembers; i++) {
        const RfMember& m = c->members[i];
        Arr* d = allocArr(12, list);
        const char* texts[] = {m.name, m.kind, m.type, m.access};
        for (int k = 0; k < 4; k++) { Value s = strFromUtf8(texts[k], list); d->items()[k] = s; retain(s); }
        d->items()[4] = Bool((m.flags & 1) != 0); d->items()[5] = Bool((m.flags & 2) != 0);
        d->items()[6] = Bool((m.flags & 4) != 0); d->items()[7] = Bool((m.flags & 8) != 0);
        Value u = strFromUtf8(m.unit, list); d->items()[8] = u; retain(u);
        Value dec = strFromUtf8(m.declared, list); d->items()[9] = dec; retain(dec);
        d->items()[10] = strArrayOf(m.pnames, m.pcount, list);
        d->items()[11] = strArrayOf(m.ptypes, m.pcount, list);
        all->items()[i] = ArrV(d);
    }
    return ArrV(all);
}
static Value rf_classes(OwnList* list) {
    Arr* names = allocArr(kRfClassCount, list);
    for (uint32_t i = 0; i < kRfClassCount; i++) { Value s = strFromUtf8(kRfClasses[i].name, list); names->items()[i] = s; retain(s); }
    return ArrV(names);
}
static Value rf_is_sub(Value name, Value base) {
    char a[200], b[200];
    strToUtf8(name, a, sizeof a); strToUtf8(base, b, sizeof b);
    return Bool(rf_isSub(a, b));
}
static Value rf_has(Value obj, Value name) {
    if (obj.kind != K_Class) return Bool(false);
    return Bool(rf_kind(rf_classOf(asObj(obj)->cls), name) != 0);
}
static Value rf_member_kind(Value obj, Value name, OwnList* list) {
    if (obj.kind != K_Class) return Undef();
    switch (rf_kind(rf_classOf(asObj(obj)->cls), name)) {
        case 1: return strFromUtf8(""field"", list);
        case 2: return strFromUtf8(""property"", list);
        case 3: return strFromUtf8(""method"", list);
        default: return Undef();
    }
}
static Value rf_selector_path(Value l, OwnList* list) {
    if (l.kind != K_Lambda) {
        char text[200];
        std::snprintf(text, sizeof text, ""A selector ('lambda member<...>' etc.) expects a lambda like `c => c.radius`, got: %s."", kindName(l));
        return rfFail(text);
    }
    Lam* lam = lamOf(l);
    if (!lam->sel) return rfFail(""The lambda is not a selector: it needs exactly one parameter, and its body may only be a member chain on it (`c => c.radius`, `p => p.address.city`)."");
    return strArrayOf(lam->sel, lam->nsel, list);
}");

            // ---- get / set / call / new ------------------------------------------------------------------------------------
            var fieldNames = _fieldNames.OrderBy(n => n, StringComparer.Ordinal).ToList();
            string tail(string name, bool setter) => (HasProperty(name) ? ", list" : "") + (RestrictedField(name) ? ", caller" : "");
            var get = new StringBuilder();
            var set = new StringBuilder();
            foreach (var name in fieldNames)
            {
                get.AppendLine($"    if (strIs(name, {CStr(name)})) return gf_{Mangle(name)}(obj{tail(name, false)});");
                set.AppendLine($"    if (strIs(name, {CStr(name)})) {{ sf_{Mangle(name)}(obj, value{tail(name, true)}); return Undef(); }}");
            }
            sb.AppendLine($@"static Value rf_get(Value obj, Value name, OwnList* list, uint32_t caller) {{
    (void)list; (void)caller;
    if (obj.kind != K_Class) {{ char t[120]; std::snprintf(t, sizeof t, ""Reflect.Get: expects an object, got: %s."", kindName(obj)); return rfFail(t); }}
    const RfClass* c = rf_classOf(asObj(obj)->cls);
    int kind = rf_kind(c, name);
    const RfMember* getter = rf_member(c, name, ""property"");
    if (kind != 1 && !(getter && (getter->flags & 4))) {{ char n[200], t[500]; strToUtf8(name, n, sizeof n); std::snprintf(t, sizeof t, ""'%s' has no readable member '%s'."", c->name, n); return rfFail(t); }}
{get}    return rfFail(""This member cannot be read by name."");
}}");
            sb.AppendLine($@"static Value rf_set(Value obj, Value name, Value value, OwnList* list, uint32_t caller) {{
    (void)list; (void)caller;
    if (obj.kind != K_Class) {{ char t[120]; std::snprintf(t, sizeof t, ""Reflect.Set: expects an object, got: %s."", kindName(obj)); return rfFail(t); }}
    const RfClass* c = rf_classOf(asObj(obj)->cls);
    int kind = rf_kind(c, name);
    const RfMember* prop = rf_member(c, name, ""property"");
    if (kind != 1 && !(prop && (prop->flags & 8))) {{
        char n[200], t[500]; strToUtf8(name, n, sizeof n);
        if (prop && (prop->flags & 4)) std::snprintf(t, sizeof t, ""The property '%s' of '%s' has no setter (only 'get')."", n, c->name);
        else std::snprintf(t, sizeof t, ""'%s' has no writable member '%s'."", c->name, n);
        return rfFail(t);
    }}
    if (const RfMember* field = rf_member(c, name, ""field"")) if (!(field->flags & 1) && (field->flags & 2)) {{
        char n[200], t[500]; strToUtf8(name, n, sizeof n);
        std::snprintf(t, sizeof t, ""The field '%s' of '%s' is 'readonly' and cannot be assigned."", n, field->declared);
        return rfFail(t);
    }}
{set}    return rfFail(""This member cannot be written by name."");
}}");

            // calls: one entry per (name, number of arguments) that some class has
            var callSb = new StringBuilder();
            foreach (var (name, argc) in _dispatchers.Where(d => !d.Name.StartsWith("operator")))
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
                if (stat.Count > 0) callSb.AppendLine("            " + string.Concat(stat.Select(i => $"case {i}: ")) + $"{{ char t[300]; strToUtf8(name, t, sizeof t); char u[400]; std::snprintf(u, sizeof u, \"'%s' is a static method - Reflect.Call only calls instance methods.\", t); return rfFail(u); }}");
                callSb.AppendLine("            default: break;");
                callSb.AppendLine("        }");
                callSb.AppendLine("    }");
            }
            sb.AppendLine($@"static Value rf_call(Value obj, Value name, Value args, OwnList* list, uint32_t caller) {{
    (void)list; (void)caller;
    if (obj.kind != K_Class) {{ char t[120]; std::snprintf(t, sizeof t, ""Reflect.Call: expects an object, got: %s."", kindName(obj)); return rfFail(t); }}
    int argc = args.kind == K_Array ? (int)arrOf(args)->length : 0;
    const Value* items = args.kind == K_Array ? arrOf(args)->items() : nullptr; (void)items;
{callSb}    char n[200], t[500]; strToUtf8(name, n, sizeof n);
    std::snprintf(t, sizeof t, ""'%s' has no method '%s' with %d parameter(s)."", className(asObj(obj)->cls), n, argc);
    return rfFail(t);
}}");

            // new: a constructor per class and number of arguments
            var newSb = new StringBuilder();
            foreach (var rc in classes)
            {
                if (!_classes.TryGetValue(rc.Name, out var info)) continue;
                var byArgc = new List<string>();
                foreach (var ctor in rc.Constructors.Values)
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
                            ? $"if (!{AllowedExpr(rc, access)}) return accessDeniedNew({CStr($"The constructor of '{rc.Name}' is {DescribeAccess(access)} and cannot be called from here.")}); "
                            : "";
                        byArgc.Add($"            case {argc}: {{ {guard}Value o = newObject({info.Id}, {info.Fields.Count}, list); {pre}{_funcByProto[ctor].Name}(o{items}{defaults}); return o; }}");
                    }
                }
                if (byArgc.Count == 0) continue;
                newSb.AppendLine($"    if (strIs(cname, {CStr(rc.Name)})) {{");
                newSb.AppendLine("        switch (argc) {");
                foreach (var row in byArgc) newSb.AppendLine(row);
                newSb.AppendLine("            default: break;");
                newSb.AppendLine("        }");
                newSb.AppendLine($"        {{ char t[300]; std::snprintf(t, sizeof t, \"'%s' has no constructor with %d parameter(s).\", {CStr(rc.Name)}, argc); return rfFail(t); }}");
                newSb.AppendLine("    }");
            }
            sb.AppendLine(@"[[maybe_unused]] static Value accessDeniedNew(const char* message) { return rfFail(message); }");
            sb.AppendLine($@"static Value rf_new(Value cname, Value args, OwnList* list, uint32_t caller) {{
    (void)caller;
    int argc = args.kind == K_Array ? (int)arrOf(args)->length : 0;
    const Value* items = args.kind == K_Array ? arrOf(args)->items() : nullptr; (void)items;
{newSb}    char n[200], t[500]; strToUtf8(cname, n, sizeof n);
    if (!rf_find(cname)) std::snprintf(t, sizeof t, ""Unknown class '%s'."", n);
    else std::snprintf(t, sizeof t, ""'%s' has no constructor with %d parameter(s)."", n, argc);
    return rfFail(t);
}}");
            if (_usesProbes) sb.Append(ProbeCode(fieldNames));
            return sb.ToString();
        }

        /// <summary>The setter of a name that looks for probes: the setter that is generated for the name becomes `sfo_`, `sf_` checks the flag of the object first.</summary>
        private string WrapProbeSetter(string name, string text)
        {
            string m = Mangle(name);
            bool list = HasProperty(name), caller = RestrictedField(name);
            string lp = (list ? ", OwnList* list" : "") + (caller ? ", uint32_t caller" : "");
            string callArgs = (list ? ", list" : "") + (caller ? ", caller" : "");
            text = text.Replace($"static inline void sf_{m}(", $"static inline void sfo_{m}(");
            var sb = new StringBuilder(text);
            sb.AppendLine($"static void sfa_{m}(Value v, Value x, OwnList* list, uint32_t caller) {{ (void)list; (void)caller; sfo_{m}(v, x{callArgs}); }}");
            // the old value as the probe sees it: the field, else the getter, else undefined (no access check)
            var cases = new StringBuilder();
            foreach (var cls in _classList)
            {
                if (cls.Rc.FieldIndex.TryGetValue(name, out int index)) cases.AppendLine($"        case {cls.Id}: return o->fields()[{index}];");
                else if (cls.Rc.FindMethodWithAccess("get_" + name, 0).Proto is { } getter) cases.AppendLine($"        case {cls.Id}: {{ Value r = {_funcByProto[getter].Name}(v); adopt(r, list); return r; }}");
            }
            sb.AppendLine($"static Value gfp_{m}(Value v, OwnList* list) {{\n    Obj* o = asObj(v); (void)list;\n    switch (o->cls) {{\n{cases}        default: return Undef();\n    }}\n}}");
            sb.AppendLine($"static inline void sf_{m}(Value v, Value x{lp}) {{");
            sb.AppendLine($"    if (FIRE_UNLIKELY(asObj(v)->flags & 2)) {{ probedSet(v, {CStr(name)}, x, sfa_{m}, gfp_{m}, {(caller ? "caller" : "0xFFFFFFFFu")}); return; }}");
            sb.AppendLine($"    sfo_{m}(v, x{callArgs});");
            sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>`probe`/`silence`: the registration of probes and the natives (SPEC 8.14); the setters are wrapped by <see cref="WrapProbeSetter"/>.</summary>
        private string ProbeCode(List<string> fieldNames)
        {
            var sb = new StringBuilder();
            sb.AppendLine(@"static int64_t rf_probeAdd(Value target, const char* member, bool changing, Value handler, char* err, size_t cap) {
    if (target.kind != K_Class) { std::snprintf(err, cap, ""'probe' expects an object, got: %s."", kindName(target)); return -1; }
    if (handler.kind != K_Lambda) { std::snprintf(err, cap, ""The handler of a probe must be a lambda, got: %s."", kindName(handler)); return -1; }
    if (lamOf(handler)->nparams > 4) { std::snprintf(err, cap, ""The handler of a probe may have at most 4 parameters (object, name, old, new), it has %u."", (unsigned)lamOf(handler)->nparams); return -1; }
    if (member) {
        OwnList local = {nullptr, nullptr, poolMark(), 0, nullptr, nullptr};
        Value name = strFromUtf8(member, &local);
        const RfClass* c = rf_classOf(asObj(target)->cls);
        bool has = rf_kind(c, name) != 0;
        leave(&local);
        if (!has) { std::snprintf(err, cap, ""'%s' has no member '%s' - no probe can be registered there."", c->name, member); return -1; }
    }
    return probeAddEntry(asObj(target), member, changing, handler);
}
static bool rf_silenceCore(Value target, const char* member, char* err, size_t cap) {
    if (target.kind != K_Class) { std::snprintf(err, cap, ""'silence' expects an object, got: %s."", kindName(target)); return false; }
    probeSilence(asObj(target), member);
    return true;
}
static bool rf_silenceValue(Value v, char* err, size_t cap) {
    if (v.kind == K_Class) return rf_silenceCore(v, nullptr, err, cap);
    if (v.kind == K_Int) { probeSilenceHandle(v.i); return true; }
    std::snprintf(err, cap, ""'silence' expects a probe handle or an object, got: %s."", kindName(v));
    return false;
}
static Value rf_probe(Value obj, Value member, Value kind, Value handler, OwnList* list, uint32_t caller) {
    (void)list; (void)caller;
    char k[64];
    strToUtf8(kind, k, sizeof k);
    if (std::strcmp(k, ""changed"") != 0 && std::strcmp(k, ""changing"") != 0) {
        char t[200]; std::snprintf(t, sizeof t, ""The kind of a probe is \""changed\"" or \""changing\"", got: \""%s\""."", k); return rfFail(t);
    }
    char m[200]; const char* mem = nullptr;
    if (member.kind == K_String) { strToUtf8(member, m, sizeof m); mem = m; }
    char err[500];
    int64_t id = rf_probeAdd(obj, mem, std::strcmp(k, ""changing"") == 0, handler, err, sizeof err);
    if (id < 0) return rfFail(err);
    return Int(id);
}
static Value rf_silence(Value obj, Value member) {
    char m[200]; const char* mem = nullptr;
    if (member.kind == K_String) { strToUtf8(member, m, sizeof m); mem = m; }
    char err[500];
    if (!rf_silenceCore(obj, mem, err, sizeof err)) return rfFail(err);
    return Undef();
}
static Value rf_silence_handle(Value handle) {
    char err[500];
    if (!rf_silenceValue(handle, err, sizeof err)) return rfFail(err);
    return Undef();
}");
            return sb.ToString();
        }
    }
}
