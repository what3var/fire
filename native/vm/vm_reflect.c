/* fire VM in C: reflection and probes (`#import "reflection"`, SPEC 8.13/8.14, docs/spec/DESIGN_LAMBDA_REFLECTION_PROBE.md; phase B step 5).
 *
 * The natives behind the reflection library (native/tables/bridges.def, src/fire.Runtime/ReflectionNatives.cs and VM.Reflection.cs): the descriptions come
 * from what the compiler wrote per class (ClassMeta, kept by the loader: VmMeta), reading, writing and calling by name go the ways of the interpreter - the
 * fields with their checks (access, unit, kind, type), the accessors and methods as nested calls - with the code that called the library as the caller of
 * the access checks (a class of the library, VC_REFLHELPER, does not count: VM.ReflectionCallerClass).
 *
 * Probes (`probe obj.member changed|changing handler`, Reflect.Probe, `silence`): the runtime keeps them (fire_rt.h, FIRE_REFLECTION: ProbeNode per object,
 * flag 2 on the object); a write to a member of an object with probes goes through vmProbedSet - the VM's version of the runtime's probedSet, by the name of
 * the member instead of the callbacks without context the generated code gives it (VM_SPIKE point 1: ProbeRawSet/Get): `changing` handlers (one that returns
 * false cancels the write), the write, `changed` handlers when the value is another, then the probes on computed properties that read the member
 * (RuntimeClass.ComputedSources, from the class's thisReads). */

static Value vmReflFail(const char* text) { return fireError("ReflectionException", text); }
static bool vmPerf(Vm* vm) { return vm->prog->mode == 2; }

/* The chunk of the code that called the reflection library (NULL: none). */
static VmChunk* vmReflCaller(Vm* vm) {
    int32_t i;
    for (i = vm->nframes - 1; i >= 0; i--) {
        VmChunk* ch = vm->frames[i].chunk;
        if (!ch || !ch->owner || !(ch->owner->flags & VC_REFLHELPER)) return ch;
    }
    return NULL;
}

/* The name a string value gives, interned (NULL: not a string). */
static const char* vmReflName(Vm* vm, Value v) {
    char buf[400];
    if (v.kind != K_String) return NULL;
    strToUtf8(v, buf, sizeof buf);
    return vmName(vm->prog, buf);
}
static const char* vmReflPrefixed(Vm* vm, const char* prefix, const char* name) {
    char buf[420];
    snprintf(buf, sizeof buf, "%s%s", prefix, name);
    return vmName(vm->prog, buf);
}

static Value vmStrArr(const char* const* items, uint32_t n, OwnList* list) {
    Arr* a = allocArr(n, list);
    uint32_t i;
    for (i = 0; i < n; i++) { Value s = strFromUtf8(items[i] ? items[i] : "", list); arrItems(a)[i] = s; retain(s); }
    return ArrV(a);
}
static void vmArrPut(Arr* a, uint32_t i, Value v) { arrItems(a)[i] = v; retain(v); }

/* ---- the attributes: [[name, values, texts], ...] - the values from AttributeMeta.ArgsJson ({"i":..}, {"f":..}, {"s":..}, {"b":..}, {"a":[..]}) ---- */
typedef struct VmJs { const char* p; } VmJs;
static void vmJsSpace(VmJs* j) { while (*j->p == ' ' || *j->p == '\n' || *j->p == '\r' || *j->p == '\t') j->p++; }
static void vmJsSkip(VmJs* j);
static Value vmJsString(VmJs* j, OwnList* list) {
    char buf[1024];
    size_t n = 0;
    if (*j->p != '"') return Undef();
    j->p++;
    while (*j->p && *j->p != '"') {
        char c = *j->p++;
        if (c == '\\' && *j->p) {
            char e = *j->p++;
            if (e == 'n') c = '\n'; else if (e == 't') c = '\t'; else if (e == 'r') c = '\r'; else if (e == 'b') c = '\b'; else if (e == 'f') c = '\f';
            else if (e == 'u') {
                unsigned cp = 0; int k;
                for (k = 0; k < 4 && *j->p; k++, j->p++) { char h = *j->p; cp = cp * 16 + (unsigned)(h >= 'a' ? h - 'a' + 10 : h >= 'A' ? h - 'A' + 10 : h - '0'); }
                if (cp < 0x80) c = (char)cp;
                else { if (cp < 0x800) { if (n + 2 < sizeof buf) { buf[n++] = (char)(0xC0 | (cp >> 6)); buf[n++] = (char)(0x80 | (cp & 0x3F)); } }
                       else if (n + 3 < sizeof buf) { buf[n++] = (char)(0xE0 | (cp >> 12)); buf[n++] = (char)(0x80 | ((cp >> 6) & 0x3F)); buf[n++] = (char)(0x80 | (cp & 0x3F)); }
                       continue; }
            } else c = e;
        }
        if (n + 1 < sizeof buf) buf[n++] = c;
    }
    if (*j->p == '"') j->p++;
    buf[n] = 0;
    return strFromUtf8(buf, list);
}
static void vmJsSkip(VmJs* j) {
    int depth = 0;
    vmJsSpace(j);
    do {
        if (*j->p == '"') { OwnList none; ownListInit(&none, poolMark(), NULL); (void)vmJsString(j, &none); leave(&none); continue; }
        if (*j->p == '{' || *j->p == '[') depth++;
        else if (*j->p == '}' || *j->p == ']') depth--;
        else if (depth == 0 && (*j->p == ',' || !*j->p)) return;
        if (!*j->p) return;
        j->p++;
    } while (depth > 0 || (*j->p && *j->p != ',' && *j->p != '}' && *j->p != ']'));
}
static Value vmJsValue(VmJs* j, OwnList* list);
/* An array of values ([...]). */
static Value vmJsArray(VmJs* j, OwnList* list) {
    Value items[64];
    uint32_t n = 0, i;
    Arr* a;
    vmJsSpace(j);
    if (*j->p != '[') return ArrV(allocArr(0, list));
    j->p++;
    for (;;) {
        vmJsSpace(j);
        if (*j->p == ']') { j->p++; break; }
        if (!*j->p) break;
        { Value v = vmJsValue(j, list); if (n < 64) items[n++] = v; }
        vmJsSpace(j);
        if (*j->p == ',') j->p++;
    }
    a = allocArr(n, list);
    for (i = 0; i < n; i++) vmArrPut(a, i, items[i]);
    return ArrV(a);
}
/* One value: an object with one of the keys i, f, s, b, a - anything else is undefined. */
static Value vmJsValue(VmJs* j, OwnList* list) {
    Value r = Undef();
    vmJsSpace(j);
    if (*j->p != '{') { vmJsSkip(j); return Undef(); }
    j->p++;
    for (;;) {
        char key[8] = {0};
        vmJsSpace(j);
        if (*j->p == '}') { j->p++; break; }
        if (*j->p != '"') { vmJsSkip(j); break; }
        { OwnList tmp; Value k; ownListInit(&tmp, poolMark(), NULL); k = vmJsString(j, &tmp); strToUtf8(k, key, sizeof key); leave(&tmp); }
        vmJsSpace(j);
        if (*j->p == ':') j->p++;
        vmJsSpace(j);
        if (!strcmp(key, "i")) { r = Int((int64_t)strtoll(j->p, (char**)&j->p, 10)); }
        else if (!strcmp(key, "f")) { r = Float((Real)strtod(j->p, (char**)&j->p)); }
        else if (!strcmp(key, "s")) r = vmJsString(j, list);
        else if (!strcmp(key, "b")) { r = Bool(!strncmp(j->p, "true", 4)); vmJsSkip(j); }
        else if (!strcmp(key, "a")) r = vmJsArray(j, list);
        else vmJsSkip(j);
        vmJsSpace(j);
        if (*j->p == ',') j->p++;
    }
    return r;
}
static Value vmReflAttrs(const VmAttr* attrs, uint32_t n, OwnList* list) {
    Arr* a = allocArr(n, list);
    uint32_t i;
    for (i = 0; i < n; i++) {
        Arr* e = allocArr(3, list);
        VmJs j;
        j.p = attrs[i].json ? attrs[i].json : "[]";
        vmArrPut(e, 0, strFromUtf8(attrs[i].name, list));
        vmArrPut(e, 1, vmJsArray(&j, list));
        vmArrPut(e, 2, vmStrArr(attrs[i].texts, attrs[i].ntexts, list));
        vmArrPut(a, i, ArrV(e));
    }
    return ArrV(a);
}

/* ---- the descriptions ---- */
static VmClass* vmReflClass(Vm* vm, Value name) {
    char buf[400];
    if (name.kind != K_String) return NULL;
    strToUtf8(name, buf, sizeof buf);
    return (VmClass*)fbMapGet(&vm->prog->classByName, buf);
}

/* RuntimeClass.ReflectedInterfaces: every interface the class reaches (over its base classes), or only those it names itself - by their plain names. */
static Value vmReflInterfaces(VmClass* c, bool declaredOnly, OwnList* list) {
    const char* names[64];
    char plain[160];
    uint32_t n = 0, i, k;
    VmClass* x;
    Vm* vm = g_vmCur;
    for (x = c; x && n < 64; x = declaredOnly ? NULL : x->base) {
        const char** items = declaredOnly ? (c->meta ? c->meta->declIfaces : NULL) : x->interfaces;
        uint32_t count = declaredOnly ? (c->meta ? c->meta->ndeclIfaces : 0) : x->ninterfaces;
        for (i = 0; i < count && n < 64; i++) {
            const char* p = vmName(vm->prog, vmPlainName(items[i], plain, sizeof plain));
            bool dup = false;
            for (k = 0; k < n; k++) if (names[k] == p) dup = true;
            if (!dup) names[n++] = p;
        }
    }
    return vmStrArr(names, n, list);
}

static Value vmReflClassInfo(Vm* vm, Value name, OwnList* list) {
    VmClass* c = vmReflClass(vm, name);
    Arr* info;
    if (!c) return Undef();
    info = allocArr(6, list);
    vmArrPut(info, 0, strFromUtf8(c->name, list));
    if (c->base) vmArrPut(info, 1, strFromUtf8(c->base->name, list));
    arrItems(info)[2] = Bool((c->flags & VC_ACTOR) != 0);
    vmArrPut(info, 3, vmReflInterfaces(c, false, list));
    vmArrPut(info, 4, c->meta ? vmReflAttrs(c->meta->attrs, c->meta->nattrs, list) : ArrV(allocArr(0, list)));
    vmArrPut(info, 5, vmReflInterfaces(c, true, list));
    return ArrV(info);
}

/* Every member including the inherited ones (a derived class hides those of the same name - a method by its name and number of parameters -, constructors
 * only its own): [name, kind, type, access, static, readonly, readable, writable, unit, declared in, parameter names, parameter types, attributes]. */
static Value vmReflMembers(Vm* vm, Value name, OwnList* list) {
    VmClass* c = vmReflClass(vm, name);
    VmClass* x;
    const VmMember* picked[512];
    VmClass* in[512];
    uint32_t n = 0, i, k;
    Arr* all;
    if (!c) return Undef();
    for (x = c; x; x = x->base) {
        if (!x->meta) continue;
        for (i = 0; i < x->meta->nmembers && n < 512; i++) {
            const VmMember* m = &x->meta->members[i];
            bool hidden = false;
            if (!strcmp(m->kind, "constructor")) { if (x != c) continue; }
            else
                for (k = 0; k < n && !hidden; k++) {
                    const VmMember* o = picked[k];
                    if (!strcmp(o->kind, "constructor") || strcmp(o->name, m->name) != 0) continue;
                    if (!strcmp(m->kind, "method")) hidden = !strcmp(o->kind, "method") && o->npnames == m->npnames;
                    else hidden = strcmp(o->kind, "method") != 0;
                }
            if (hidden) continue;
            picked[n] = m; in[n] = x; n++;
        }
    }
    all = allocArr(n, list);
    for (i = 0; i < n; i++) {
        const VmMember* m = picked[i];
        Arr* d = allocArr(13, list);
        vmArrPut(d, 0, strFromUtf8(m->name, list));
        vmArrPut(d, 1, strFromUtf8(m->kind, list));
        vmArrPut(d, 2, strFromUtf8(m->type ? m->type : "", list));
        vmArrPut(d, 3, strFromUtf8(m->access ? m->access : "", list));
        arrItems(d)[4] = Bool((m->flags & 1) != 0);
        arrItems(d)[5] = Bool((m->flags & 2) != 0);
        arrItems(d)[6] = Bool((m->flags & 4) != 0);
        arrItems(d)[7] = Bool((m->flags & 8) != 0);
        vmArrPut(d, 8, strFromUtf8(m->unit ? m->unit : "", list));
        vmArrPut(d, 9, strFromUtf8(in[i]->name, list));
        vmArrPut(d, 10, vmStrArr(m->pnames, m->npnames, list));
        vmArrPut(d, 11, vmStrArr(m->ptypes, m->nptypes, list));
        vmArrPut(d, 12, vmReflAttrs(m->attrs, m->nattrs, list));
        vmArrPut(all, i, ArrV(d));
    }
    return ArrV(all);
}

static int vmReflCmp(const void* a, const void* b) { return strcmp(*(const char* const*)a, *(const char* const*)b); }
static Value vmReflClasses(Vm* vm, OwnList* list) {
    FbMap* m = &vm->prog->classByName;
    const char** names = (const char**)malloc((m->count + 1) * sizeof(char*));
    uint32_t n = 0;
    Value r;
    if (!names) outOfMemory();
    FB_MAP_EACH(m, e) names[n++] = e->key;
    qsort(names, n, sizeof(char*), vmReflCmp);
    r = vmStrArr(names, n, list);
    free(names);
    return r;
}

static bool vmReflHasMethod(VmClass* c, const char* name) {
    VmClass* x;
    uint32_t i;
    for (x = c; x; x = x->base) for (i = 0; i < x->nmethods; i++) if (x->methods[i].name == name) return true;
    return false;
}
static VmProto* vmReflAccessor(Vm* vm, VmClass* c, const char* prefix, const char* name, int argc, const Value* args, Value self, VmClass** declaring) {
    return vmFindMethod(vm, c, vmReflPrefixed(vm, prefix, name), argc, args, self, declaring);
}

/* ---- reading and writing a member by name ---- */
/* The value of member `name` of the object (a field, else the getter): undefined with the unwinding state set when it threw. */
static Value vmReflRead(Vm* vm, VmChunk* caller, Value obj, const char* name, OwnList* list) {
    VmClass* c = vmClassOfValue(vm, obj);
    int32_t idx = c ? vmFieldIndex(c, name) : -1;
    VmClass* declaring = NULL;
    if (idx >= 0) {
        const VmFieldInfo* fi = vmFieldInfo(c, name, &declaring);
        Value x;
        if (caller && !vmPerf(vm) && fi && fi->access && !vmAccessAllowed(caller, declaring, fi->access)) { vmAccessDenied("Field", name, declaring, fi->access, "accessed"); return Undef(); }
        x = objFields(asObj(obj))[idx];
        if (isRef(x)) holdTemp(x, list);
        return x;
    }
    {
        VmProto* g = c ? vmReflAccessor(vm, c, "get_", name, 0, NULL, obj, &declaring) : NULL;
        Value r;
        if (!g) return Undef();
        if (caller && !vmPerf(vm) && !vmAccessAllowed(caller, declaring, vmProtoAccess(g))) {
            char text[400];
            snprintf(text, sizeof text, "'get_%s' of '%s' is %s and cannot be accessed from here.", name, declaring->name, vmAccessName(vmProtoAccess(g)));
            accessDenied(text);
            return Undef();
        }
        r = vmCallNested(vm, g, 0, NULL, obj, FR_NESTED);
        if (g_unwind.active) return Undef();
        adopt(r, list);
        return r;
    }
}

/* `obj.name = value` without the probes: the field with its checks, else the setter. False when it threw. */
static bool vmReflWriteRaw(Vm* vm, VmChunk* caller, Value obj, const char* name, Value x) {
    VmClass* c = vmClassOfValue(vm, obj);
    int32_t idx = c ? vmFieldIndex(c, name) : -1;
    VmClass* declaring = NULL;
    if (idx < 0) {
        VmProto* s = c ? vmReflAccessor(vm, c, "set_", name, 1, &x, obj, &declaring) : NULL;
        Value r;
        if (!s) {
            if (c && vmReflAccessor(vm, c, "get_", name, 0, NULL, obj, NULL)) noSetterError(obj, name);
            else missingFieldSet(obj, name);
            return false;
        }
        if (caller && !vmPerf(vm) && !vmAccessAllowed(caller, declaring, vmProtoAccess(s))) {
            char text[400];
            snprintf(text, sizeof text, "'set_%s' of '%s' is %s and cannot be accessed from here.", name, declaring->name, vmAccessName(vmProtoAccess(s)));
            accessDenied(text);
            return false;
        }
        r = vmCallNested(vm, s, 1, &x, obj, FR_NESTED);
        if (g_unwind.active) return false;
        release(r);
        return true;
    }
    {
        const VmFieldInfo* fi = vmFieldInfo(c, name, &declaring);
        char what[300];
        Value old;
        snprintf(what, sizeof what, "Field '%s'", name);
        if (caller && !vmPerf(vm) && fi && fi->access && !vmAccessAllowed(caller, declaring, fi->access)) { vmAccessDenied("Field", name, declaring, fi->access, "accessed"); return false; }
        if (fi && fi->requiredUnit) { x = vmAdaptUnit(vm, x, fi->requiredUnit); if (g_unwind.active) return false; }
        if (fi && fi->requiredKind && vmGuard(&x, fi->requiredKind, what, false)) return false;
        if (fi && fi->requiredType && vmGuardType(vm, &x, fi->requiredType, obj, NULL, what, false)) return false;
        if (fi && fi->arrayOfParam && x.kind == K_Array) { vmTypeArrayOfParam(vm, x, fi->arrayOfParam, obj, NULL); if (g_unwind.active) return false; }
        if ((c->flags & VC_RECORD) && !recInit(asObj(obj))) { recordWriteError(asObj(obj), name); return false; }
        old = objFields(asObj(obj))[idx];
        objFields(asObj(obj))[idx] = x;
        retain(x);
        release(old);
        return true;
    }
}

/* ---- probes ---- */
/* RuntimeClass.ComputedSources: for a computed property (a getter without a setter, no field) the fields and settable properties its getter reads (through
 * the getters and methods it calls); NULL for any other member. A NULL-terminated list in the program's arena. */
static const char** vmReflReadsOf(VmClass* c, const char* key, uint32_t* n) {
    VmClass* x;
    uint32_t i;
    for (x = c; x; x = x->base) for (i = 0; i < x->nthisReads; i++) if (x->thisReads[i].key == key) { *n = x->thisReads[i].n; return x->thisReads[i].reads; }
    *n = 0;
    return NULL;
}
static const char** vmReflComputedSources(Vm* vm, VmClass* c, const char* name) {
    const char* pending[128];
    const char* sources[128];
    uint32_t np = 0, at = 0, ns = 0, i, k;
    const char** result;
    if (vmFieldIndex(c, name) >= 0 || vmReflAccessor(vm, c, "set_", name, 1, NULL, Undef(), NULL) || !vmReflAccessor(vm, c, "get_", name, 0, NULL, Undef(), NULL)) return NULL;
    pending[np++] = vmReflPrefixed(vm, "get_", name);
    while (at < np) {
        uint32_t nr;
        const char** reads = vmReflReadsOf(c, pending[at++], &nr);
        for (i = 0; reads && i < nr; i++) {
            const char* read = reads[i];
            bool isField = vmFieldIndex(c, read) >= 0, hasSetter = vmReflAccessor(vm, c, "set_", read, 1, NULL, Undef(), NULL) != NULL;
            bool dup = false;
            const char* next = NULL;
            for (k = 0; k < ns; k++) if (sources[k] == read) dup = true;
            if ((isField || hasSetter) && !dup && ns < 127) sources[ns++] = read;
            if (!isField && vmReflAccessor(vm, c, "get_", read, 0, NULL, Undef(), NULL)) next = vmReflPrefixed(vm, "get_", read);
            else if (!isField && vmReflHasMethod(c, read)) next = read;
            if (next) {
                bool seen = false;
                for (k = 0; k < np; k++) if (pending[k] == next) seen = true;
                if (!seen && np < 128) pending[np++] = next;
            }
        }
    }
    result = (const char**)vmAlloc(&vm->prog->arena, (ns + 1) * sizeof(char*));
    for (i = 0; i < ns; i++) result[i] = sources[i];
    result[ns] = NULL;
    return result;
}

static bool vmReflHas(Vm* vm, Value obj, const char* name) {
    VmClass* c = vmClassOfValue(vm, obj);
    if (!c || !name) return false;
    return vmFieldIndex(c, name) >= 0 || vmReflAccessor(vm, c, "get_", name, 0, NULL, obj, NULL) || vmReflAccessor(vm, c, "set_", name, 1, NULL, obj, NULL) || vmReflHasMethod(c, name);
}

/* TryProbeAdd: the handle (> 0); 0 with the text in `error` (empty: the getter of a computed property threw - its exception is on its way). */
static int64_t vmProbeAdd(Vm* vm, Value target, const char* member, bool changing, Value handler, char* error, size_t cap) {
    VmClass* c;
    const char** sources = NULL;
    Value last = Undef();
    error[0] = 0;
    if (target.kind != K_Class) { snprintf(error, cap, "'probe' expects an object, got: %s.", kindName(target)); return 0; }
    if (handler.kind != K_Lambda) { snprintf(error, cap, "The handler of a probe must be a lambda, got: %s.", kindName(handler)); return 0; }
    if (!leafAlive(target)) { destroyedError(target); return 0; }
    c = vmClassOfValue(vm, target);
    if (lamOf(handler)->nparams > 4) { snprintf(error, cap, "The handler of a probe may have at most 4 parameters (object, name, old, new), it has %u.", lamOf(handler)->nparams); return 0; }
    if (member && !vmReflHas(vm, target, member)) { snprintf(error, cap, "'%s' has no member '%s' - no probe can be registered there.", c ? c->name : "?", member); return 0; }
    if (member && c) sources = vmReflComputedSources(vm, c, member);
    if (sources) {
        OwnList list;
        if (changing) { snprintf(error, cap, "'%s' of '%s' is a computed property (a getter without a setter): it can only be probed with 'changed'.", member, c->name); return 0; }
        ownListInit(&list, poolMark(), NULL);
        last = vmReflRead(vm, NULL, target, member, &list);
        if (g_unwind.active) { leave(&list); return 0; }
        {   /* (the probe keeps the value: a copy that outlives the statement) */
            int64_t id = probeAddEntryFull(asObj(target), member, false, handler, sources, NULL, last);
            leave(&list);
            return id;
        }
    }
    return probeAddEntryFull(asObj(target), member, changing, handler, NULL, NULL, Undef());
}

static bool vmProbeRunning(ProbeNode* node, const char* name) {
    uint32_t i;
    for (i = 0; i < node->nrunning; i++) if (node->running[i] == name) return true;
    return false;
}
/* Does a probe of the object hear a write to `name` (ProbeTable.Affects)? */
static bool vmProbeAffects(ProbeNode* node, const char* name) {
    uint32_t i;
    const char* const* d;
    for (i = 0; i < node->n; i++) {
        const ProbeEntry* e = &node->entries[i];
        if (!e->member || strcmp(e->member, name) == 0) return true;
        if (e->deps) for (d = e->deps; *d; d++) if (*d == name) return true;
    }
    return false;
}

/* `obj.name = value` on an object with probes (VM.SetFieldProbed). False when it threw. */
static bool vmProbedSet(Vm* vm, VmChunk* caller, Value obj, const char* name, Value value) {
    ProbeNode* node = probeNodeOf(asObj(obj), false);
    OwnList local;
    Value oldValue;
    uint32_t i, n;
    bool cancelled = false;
    ProbeEntry* snapshot;
    if (!node || !vmProbeAffects(node, name) || vmProbeRunning(node, name) || node->nrunning >= 7) return vmReflWriteRaw(vm, caller, obj, name, value);
    node->running[node->nrunning++] = name;
    ownListInit(&local, poolMark(), NULL);
    {
        VmClass* c = vmClassOfValue(vm, obj);
        oldValue = c && (vmFieldIndex(c, name) >= 0 || vmReflAccessor(vm, c, "get_", name, 0, NULL, obj, NULL)) ? vmReflRead(vm, NULL, obj, name, &local) : Undef();
    }
    retain(oldValue);
    /* (a snapshot of the entries: a handler may add or silence probes) */
    n = node->n;
    snapshot = (ProbeEntry*)malloc((n + 1) * sizeof(ProbeEntry));
    if (!snapshot) outOfMemory();
    memcpy(snapshot, node->entries, n * sizeof(ProbeEntry));
    for (i = 0; i < n; i++) retain(snapshot[i].handler);
    for (i = 0; i < n && !g_unwind.active; i++) {
        Value verdict;
        if (!snapshot[i].changing || (snapshot[i].member && strcmp(snapshot[i].member, name) != 0)) continue;
        verdict = probeRun(&snapshot[i], obj, name, oldValue, value, &local);
        if (g_unwind.active) break;
        if (verdict.kind == K_Bool && !verdict.i) { cancelled = true; break; }
    }
    if (!g_unwind.active && !cancelled && vmReflWriteRaw(vm, caller, obj, name, value)) {
        if (!eq(oldValue, value))
            for (i = 0; i < n && !g_unwind.active; i++) {
                if (snapshot[i].changing || snapshot[i].deps || (snapshot[i].member && strcmp(snapshot[i].member, name) != 0)) continue;
                probeRun(&snapshot[i], obj, name, oldValue, value, &local);
            }
        /* the probes on computed properties that read this member: their getter again, `changed` when its value is another */
        for (i = 0; i < n && !g_unwind.active; i++) {
            const char* const* d;
            bool depends = false;
            const char* prop = snapshot[i].member ? vmName(vm->prog, snapshot[i].member) : NULL;
            const char* key;
            Value now;
            uint32_t k;
            if (snapshot[i].deps) for (d = snapshot[i].deps; *d; d++) if (*d == name) depends = true;
            if (!depends || !prop) continue;
            node = probeNodeOf(asObj(obj), false);
            if (!node) break;
            key = vmReflPrefixed(vm, "get ", prop);
            if (vmProbeRunning(node, key) || node->nrunning >= 7) continue;
            node->running[node->nrunning++] = key;
            now = vmReflRead(vm, NULL, obj, prop, &local);
            if (!g_unwind.active) {
                /* (the entry's last value: on the probe as it is now - it may have moved) */
                for (k = 0; k < node->n; k++) if (node->entries[k].id == snapshot[i].id) break;
                if (k < node->n && !eq(node->entries[k].last, now)) {
                    Value old = node->entries[k].last;
                    retain(now);
                    node->entries[k].last = now;
                    probeRun(&snapshot[i], obj, prop, old, now, &local);
                    release(old);
                }
            }
            node = probeNodeOf(asObj(obj), false);
            if (node) fire_probeDone(node, key);
        }
    }
    for (i = 0; i < n; i++) release(snapshot[i].handler);
    free(snapshot);
    node = probeNodeOf(asObj(obj), false);
    if (node) fire_probeDone(node, name);
    release(oldValue);
    leave(&local);
    return !g_unwind.active;
}

/* `obj.name = value` by name (Reflect.Set, a SetField on an object with probes). */
static bool vmReflWrite(Vm* vm, VmChunk* caller, Value obj, const char* name, Value value) {
    if (FIRE_UNLIKELY(asObj(obj)->h.flags & 2)) return vmProbedSet(vm, caller, obj, name, value);
    return vmReflWriteRaw(vm, caller, obj, name, value);
}

/* the opcodes (vm_run.c): `probe obj.member changed|changing handler` (flags 1 changing, 2 every member) - the handle; `silence obj.member` / `obj.*`;
 * `silence x` (a handle or an object). False when it threw. */
static const char* vmProbeErrorClass(const char* error) { return strstr(error, "has no member") ? "MissingMemberException" : "ArgumentException"; }
static bool vmOpProbe(Vm* vm, Value target, const char* member, uint32_t flags, Value handler, Value* result) {
    char error[400];
    int64_t id = vmProbeAdd(vm, target, (flags & 2) ? NULL : member, (flags & 1) != 0, handler, error, sizeof error);
    if (!id) { if (error[0]) fireError(vmProbeErrorClass(error), error); return false; }
    *result = Int(id);
    return true;
}
static bool vmSilenceMember(Value target, const char* member, char* error, size_t cap) {
    if (target.kind != K_Class) { snprintf(error, cap, "'silence' expects an object, got: %s.", kindName(target)); return false; }
    if (leafAlive(target)) probeSilence(asObj(target), member);
    return true;
}
static bool vmSilenceValue(Value v, char* error, size_t cap) {
    if (v.kind == K_Class) return vmSilenceMember(v, NULL, error, cap);
    if (v.kind == K_Int) { probeSilenceHandle(v.i); return true; }   /* (a handle that is gone is no error) */
    snprintf(error, cap, "'silence' expects a probe handle or an object, got: %s.", kindName(v));
    return false;
}
static bool vmOpSilenceMember(Value target, const char* member) {
    char error[300];
    if (!vmSilenceMember(target, member, error, sizeof error)) { fireError(vmProbeErrorClass(error), error); return false; }
    return true;
}
static bool vmOpSilenceValue(Value v) {
    char error[300];
    if (!vmSilenceValue(v, error, sizeof error)) { fireError(vmProbeErrorClass(error), error); return false; }
    return true;
}

/* ---- calls and construction by name ---- */
static Value vmReflCall(Vm* vm, Value obj, const char* name, Value args, OwnList* list) {
    VmClass* c = vmClassOfValue(vm, obj);
    int argc = args.kind == K_Array ? (int)arrOf(args)->length : 0;
    const Value* items = args.kind == K_Array ? arrItems(arrOf(args)) : NULL;
    VmCands* cands = c ? vmCandidates(vm->prog, c, name, argc) : NULL;
    VmClass* declaring = NULL;
    VmProto* p;
    VmChunk* caller = vmReflCaller(vm);
    Value r;
    char text[500];
    uint64_t mask;
    if (!cands || !cands->n) { snprintf(text, sizeof text, "'%s' has no method '%s' with %d parameter(s).", c ? c->name : "?", name, argc); return vmReflFail(text); }
    if (cands->protos[0]->isStatic) { snprintf(text, sizeof text, "'%s' is a static method - Reflect.Call only calls instance methods.", name); return vmReflFail(text); }
    p = vmFindMethod(vm, c, name, argc, items, obj, &declaring);
    if (!p) { p = cands->protos[0]; declaring = cands->declaring[0]; }
    if (!vmPerf(vm) && !vmAccessAllowed(caller, declaring, vmProtoAccess(p))) {
        snprintf(text, sizeof text, "'%s' of '%s' is %s and cannot be accessed from here.", name, declaring->name, vmAccessName(vmProtoAccess(p)));
        return accessDenied(text);
    }
    mask = vm->copyMask;
    vm->copyMask = 0;
    r = vmCallNested(vm, p, argc, items, obj, FR_NESTED);
    vm->copyMask = mask;
    if (g_unwind.active) return Undef();
    adopt(r, list);
    return r;
}

static Value vmReflNew(Vm* vm, Value cname, Value args, OwnList* list) {
    VmClass* c = vmReflClass(vm, cname);
    int argc = args.kind == K_Array ? (int)arrOf(args)->length : 0;
    const Value* items = args.kind == K_Array ? arrItems(arrOf(args)) : NULL;
    VmProto* ctor;
    Value o;
    char text[500], n[300];
    uint64_t mask;
    strToUtf8(cname, n, sizeof n);
    if (!c) { snprintf(text, sizeof text, "Unknown class '%s'.", n); return vmReflFail(text); }
    ctor = vmFindCtor(vm, c, argc, items);
    if (!ctor) { snprintf(text, sizeof text, "'%s' has no constructor with %d parameter(s).", n, argc); return vmReflFail(text); }
    if (!vmPerf(vm) && !vmAccessAllowed(vmReflCaller(vm), c, vmProtoAccess(ctor))) {
        snprintf(text, sizeof text, "The constructor of '%s' is %s and cannot be called from here.", n, vmAccessName(vmProtoAccess(ctor)));
        return accessDenied(text);
    }
    o = newObject(c->id, c->nlayout, list);
    mask = vm->copyMask;
    vm->copyMask = 0;
    vmCallNested(vm, ctor, argc, items, o, FR_CTOR);
    vm->copyMask = mask;
    return g_unwind.active ? Undef() : o;
}

/* ---- the natives (in the order of bridges.def) ---- */
static Value vmReflNative(Vm* vm, VmFrame* fr, uint32_t which, const Value* a, OwnList* list) {
    char text[600], n[300];
    (void)fr;
    switch (which) {
        case 0:   /* __refl_class_name(x) */
            if (a[0].kind == K_Class) { VmClass* c = vmClassOfValue(vm, a[0]); return strFromUtf8(c ? c->name : className(asObj(a[0])->cls), list); }
            if (a[0].kind == K_String) { VmClass* c = vmReflClass(vm, a[0]); if (c) return strFromUtf8(c->name, list); }
            return Undef();
        case 1: return vmReflClassInfo(vm, a[0], list);
        case 2: return vmReflMembers(vm, a[0], list);
        case 3: return vmReflClasses(vm, list);
        case 4: {   /* __refl_is_sub(name, base) */
            VmClass* c = vmReflClass(vm, a[0]);
            strToUtf8(a[1], n, sizeof n);
            for (; c; c = c->base) if (!strcmp(c->name, n)) return Bool(true);
            return Bool(false);
        }
        case 5: case 6: {   /* __refl_get(obj, name), __refl_set(obj, name, value) */
            Value obj = a[0];
            const char* name = vmReflName(vm, a[1]);
            VmClass* c;
            bool field;
            if (obj.kind != K_Class) { snprintf(text, sizeof text, "%s: expects an object, got: %s.", which == 5 ? "Reflect.Get" : "Reflect.Set", kindName(obj)); return vmReflFail(text); }
            if (!leafAlive(obj)) return destroyedError(obj);
            c = vmClassOfValue(vm, obj);
            if (!name) name = vmName(vm->prog, "");
            field = c && vmFieldIndex(c, name) >= 0;
            if (which == 5) {
                if (!field && !(c && vmReflAccessor(vm, c, "get_", name, 0, NULL, obj, NULL))) { snprintf(text, sizeof text, "'%s' has no readable member '%s'.", c ? c->name : "?", name); return vmReflFail(text); }
                return vmReflRead(vm, vmReflCaller(vm), obj, name, list);
            }
            if (!field && !(c && vmReflAccessor(vm, c, "set_", name, 1, NULL, obj, NULL))) {
                if (c && vmReflAccessor(vm, c, "get_", name, 0, NULL, obj, NULL)) snprintf(text, sizeof text, "The property '%s' of '%s' has no setter (only 'get').", name, c->name);
                else snprintf(text, sizeof text, "'%s' has no writable member '%s'.", c ? c->name : "?", name);
                return vmReflFail(text);
            }
            {   /* a `readonly` field (the meta says it) */
                VmClass* x;
                uint32_t i;
                for (x = c; x; x = x->base) {
                    bool found = false;
                    if (!x->meta) continue;
                    for (i = 0; i < x->meta->nmembers; i++) {
                        const VmMember* m = &x->meta->members[i];
                        if (strcmp(m->kind, "field") != 0 || strcmp(m->name, name) != 0 || (m->flags & 1)) continue;
                        if (m->flags & 2) { snprintf(text, sizeof text, "The field '%s' of '%s' is 'readonly' and cannot be assigned.", name, x->name); return vmReflFail(text); }
                        found = true;
                        break;
                    }
                    if (found) break;
                }
            }
            vmReflWrite(vm, vmReflCaller(vm), obj, name, a[2]);
            return Undef();
        }
        case 7: {   /* __refl_call(obj, name, args) */
            const char* name = vmReflName(vm, a[1]);
            if (a[0].kind != K_Class) { snprintf(text, sizeof text, "Reflect.Call: expects an object, got: %s.", kindName(a[0])); return vmReflFail(text); }
            if (!leafAlive(a[0])) return destroyedError(a[0]);
            return vmReflCall(vm, a[0], name ? name : vmName(vm->prog, ""), a[2], list);
        }
        case 8: return vmReflNew(vm, a[0], a[1], list);
        case 9: return Bool(a[0].kind == K_Class && leafAlive(a[0]) && vmReflHas(vm, a[0], vmReflName(vm, a[1])));
        case 10: {   /* __refl_selector_path(lambda) */
            VmProto* p;
            if (a[0].kind != K_Lambda) { snprintf(text, sizeof text, "A selector ('lambda member<...>' etc.) expects a lambda like `c => c.radius`, got: %s.", kindName(a[0])); return vmReflFail(text); }
            p = lamOf(a[0])->fn == vmLamThunk ? vmLamProto(lamOf(a[0])) : NULL;
            if (!p || !p->selectorPath)
                return vmReflFail("The lambda is not a selector: it needs exactly one parameter, and its body may only be a member chain on it (`c => c.radius`, `p => p.address.city`).");
            return vmStrArr(p->selectorPath, p->nselectorPath, list);
        }
        case 11: {   /* __refl_member_kind(obj, name) */
            const char* name = vmReflName(vm, a[1]);
            VmClass* c = vmClassOfValue(vm, a[0]);
            if (a[0].kind != K_Class || !c || !name || !leafAlive(a[0])) return Undef();
            if (vmFieldIndex(c, name) >= 0) return strFromUtf8("field", list);
            if (vmReflAccessor(vm, c, "get_", name, 0, NULL, a[0], NULL) || vmReflAccessor(vm, c, "set_", name, 1, NULL, a[0], NULL)) return strFromUtf8("property", list);
            if (vmReflHasMethod(c, name)) return strFromUtf8("method", list);
            return Undef();
        }
        case 12: {   /* __refl_probe(obj, member, kind, handler) */
            char kind[40], error[400];
            int64_t id;
            strToUtf8(a[2], kind, sizeof kind);
            if (strcmp(kind, "changed") != 0 && strcmp(kind, "changing") != 0) { snprintf(text, sizeof text, "The kind of a probe is \"changed\" or \"changing\", got: \"%s\".", kind); return vmReflFail(text); }
            id = vmProbeAdd(vm, a[0], a[1].kind == K_String ? vmReflName(vm, a[1]) : NULL, !strcmp(kind, "changing"), a[3], error, sizeof error);
            if (!id) return error[0] ? vmReflFail(error) : Undef();
            return Int(id);
        }
        case 13: {   /* __refl_silence(obj, member) */
            char error[300];
            if (!vmSilenceMember(a[0], a[1].kind == K_String ? vmReflName(vm, a[1]) : NULL, error, sizeof error)) return vmReflFail(error);
            return Undef();
        }
        case 14: {   /* __refl_silence_handle(handle) */
            char error[300];
            if (!vmSilenceValue(a[0], error, sizeof error)) return vmReflFail(error);
            return Undef();
        }
        case 15: { const FireEnum* e = enumOf(a[0]); return e ? strFromUtf8(e->name, list) : Undef(); }
        case 16: return enumOf(a[0]) && a[1].kind == K_Int ? EnumV(a[1].i, a[0].reserved) : a[1];
        default: vmUnsupported("the native", g_vmBridgeReflection[which]);
    }
}
