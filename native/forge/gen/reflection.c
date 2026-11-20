/* native/forge/gen/reflection.c - src/fire.Native/CppGenerator.Reflection.cs (commit 808e9788): reflection (`#import "reflection"`, SPEC 8.13) and
 * `probe`/`silence` (SPEC 8.14) in the native backend - what a program can reach by name (UseReflection, UseReflectionLevel, RegisterReflectionTargets,
 * RegisterKeptTargets), the tables (ReflectionRows, ReflectionTableClasses), the functions behind the `__refl_*` natives (ReflectionCode), the setters
 * that look for probes (WrapProbeSetter) and the registration of probes (ProbeCode); from src/fire/Bytecode/RuntimeClass.cs ComputedSources. The parts
 * of a library module (LibraryReflectionRows, the rows of the classes of the libraries) come with the modules (CppGenerator.Modules.cs). */

static bool fgIsReflectionHelperClass(const FcRtClass* rc) { return rc && fcIsReflectionHelper(rc->name); }

/* ---- the levels (the fields at the top of the C# file) ---- */
static void UseReflection(FgGen* g) {
    if (g->usesReflection) return;
    g->usesReflection = true;
    UseExceptions(g);   /* a failed reflection call is a ReflectionException */
    g->version++;
}
static void UseReflectionLevel(FgGen* g, const char* native);
static void UseProbes(FgGen* g) {
    UseReflection(g);
    UseReflectionLevel(g, "__refl_probe");   /* (a probe on a computed property calls its getter: the accessors are needed as for Reflect.Get) */
    if (g->usesProbes) return;
    g->usesProbes = true;
    g->version++;
}
/* What a reflection native needs of the program */
static void UseReflectionLevel(FgGen* g, const char* native) {
    bool* level = NULL;
    if (streq0(native, "__refl_get") || streq0(native, "__refl_set") || streq0(native, "__refl_has") || streq0(native, "__refl_member_kind") || streq0(native, "__refl_probe")
        || streq0(native, "__refl_silence") || streq0(native, "__refl_silence_handle")) level = &g->reflMembers;
    else if (streq0(native, "__refl_call")) level = &g->reflCall;
    else if (streq0(native, "__refl_new")) level = &g->reflNew;
    if (!level || *level) return;
    *level = true;
    g->version++;
}
static bool fgIsReflectionNative(const char* native) {
    static const char* const names[] = {
        "__refl_class_name", "__refl_class_info", "__refl_members", "__refl_classes", "__refl_is_sub", "__refl_get", "__refl_set", "__refl_call", "__refl_new",
        "__refl_has", "__refl_selector_path", "__refl_member_kind", "__refl_probe", "__refl_silence", "__refl_silence_handle", "__refl_enum_name", "__refl_enum_of",
    };
    size_t i;
    for (i = 0; i < sizeof names / sizeof names[0]; i++) if (streq0(native, names[i])) return true;
    return false;
}

/* PlainMemberName: a property accessor `get_X`/`set_X` is `X` */
static const char* fgPlainMemberName(const char* name) { return strlen(name) > 4 && (fcStartsWith(name, "get_") || fcStartsWith(name, "set_")) ? name + 4 : name; }
static bool LiteralMember(FgGen* g, const char* name) { return fcDictHas(g->literalMembers, name) || fcDictHas(g->literalMembers, fgPlainMemberName(name)); }

/* ---- RuntimeClass.ComputedSources ---- */
/* A computed property (a getter, no setter, no field of that name): the fields and settable properties of `this` its value depends on - what its
 * getter reads, followed through the getters and methods of the class it calls. NULL when `name` is no computed property. */
static FcList* ComputedSources(FgGen* g, const FcRtClass* rc, const char* name) {
    FcList* sources;
    FcDict* seen;
    FcList* pending;
    int head = 0, i;
    if (fgHasField(g, rc, name) || FG_FIND(rc, fcFmt(g->c, "set_%s", name), 1).proto || !FG_FIND(rc, fcFmt(g->c, "get_%s", name), 0).proto) return NULL;
    sources = fcListNew(g->c);
    seen = fcDictNew(g->c, FC_KEY_STR);
    pending = fcListNew(g->c);
    fcListAdd(g->c, pending, (void*)fcFmt(g->c, "get_%s", name));
    fcSetAdd(seen, pending->items[0]);
    while (head < pending->count) {
        const char* key = (const char*)pending->items[head++];
        FcList* reads = NULL;
        const FcRtClass* c;
        for (c = rc; c && !reads; c = c->base) if (c->thisReads) reads = (FcList*)fcDictGet(c->thisReads, key);
        if (!reads) continue;
        for (i = 0; i < reads->count; i++) {
            const char* read = (const char*)reads->items[i];
            bool isField = fgHasField(g, rc, read), hasSetter = FG_FIND(rc, fcFmt(g->c, "set_%s", read), 1).proto != NULL;
            const char* getter = fcFmt(g->c, "get_%s", read);
            if ((isField || hasSetter) && !fcListHasStr(sources, read)) fcListAdd(g->c, sources, (void*)read);
            if (!isField && FG_FIND(rc, getter, 0).proto && !fcDictHas(seen, getter)) { fcSetAdd(seen, getter); fcListAdd(g->c, pending, (void*)getter); }
            else if (!isField && fgHasMethodNamed(rc, read) && !fcDictHas(seen, read)) { fcSetAdd(seen, read); fcListAdd(g->c, pending, (void*)read); }
        }
    }
    return sources;
}

/* ---- what reflection can reach ---- */
static bool KeptClass(FgGen* g, const FcRtClass* rc) { return rc->keep || fcDictHas(g->literalClasses, rc->name); }
static void AddDispatcher(FgGen* g, const char* name, int argc) { if (!fgHasNameArgc(g, g->dispatchers, name, argc)) { fgAddNameArgc(g, g->dispatchers, name, argc); g->version++; } }
static void AddFieldName(FgGen* g, const char* name) { if (fcSetAdd(g->fieldNames, name)) g->version++; }
/* the call of a library method from code outside the library: it tells the library the class of the caller (g_reflCaller; the C# trims the space
 * at its end at every use) */
static const char* ReflectionCallerAssign(FgGen* g, FgFunc* f) {
    return g->usesReflection && CheckAccess && !fgIsReflectionHelperClass(f->chunk->ownerClass) ? fcFmt(g->c, "g_reflCaller = %s;", CallerId(g, f)) : "";
}

/* ReachedClasses: the classes whose objects exist, those whose code or static fields are used, the kept ones, each with its base classes */
static void fgReachAdd(FgGen* g, FcDict* set, FcList* order, const FcRtClass* rc) {
    for (; rc && !fcDictHas(set, rc); rc = rc->base) { fcSetAdd(set, rc); fcListAdd(g->c, order, (void*)rc); }
}
static FcList* ReachedClasses(FgGen* g) {
    FcDict* set = fcDictNew(g->c, FC_KEY_PTR);
    FcList* order = fcListNew(g->c);
    int i;
    for (i = 0; i < g->classList->count; i++) fgReachAdd(g, set, order, ((FgClassInfo*)g->classList->items[i])->rc);
    for (i = 0; i < g->funcs->count; i++) fgReachAdd(g, set, order, ((FgFunc*)g->funcs->items[i])->chunk->ownerClass);
    FC_DICT_EACH(g->staticFields, e) {
        const char* key = (const char*)e->key;
        const char* sep = strchr(key, '\x01');
        fgReachAdd(g, set, order, fgClassOf(g, sep ? fcStrN(g->c, key, (size_t)(sep - key)) : key));
    }
    FC_DICT_EACH(g->program->program->classes, e) { FcRtClass* rc = (FcRtClass*)e->value; if (KeptClass(g, rc) || rc->keptMembers) fgReachAdd(g, set, order, rc); }
    return order;
}
static int fgCmpClassName(const void* a, const void* b) { return strcmp((*(const FcRtClass* const*)a)->name, (*(const FcRtClass* const*)b)->name); }
/* The classes the reflection tables describe, sorted by name: all of them at the stripping levels 0-2, the reached ones at 3. */
static FcList* ReflectionTableClasses(FgGen* g) {
    FcList* classes = fcListNew(g->c);
    if (g->strip <= 2) { FC_DICT_EACH(g->program->program->classes, e) fcListAdd(g->c, classes, e->value); }
    else {
        FcList* reached = ReachedClasses(g);
        int i;
        for (i = 0; i < reached->count; i++) {
            const FcRtClass* rc = (const FcRtClass*)reached->items[i];
            if (fgClassOf(g, rc->name) == rc) fcListAdd(g->c, classes, (void*)rc);
        }
    }
    if (classes->count > 1) qsort(classes->items, (size_t)classes->count, sizeof(void*), fgCmpClassName);   /* (the names are distinct: the order is that of OrderBy) */
    return classes;
}

/* The constructors of a class (with the functions of their default values, `Reflect.New` with fewer arguments evaluates them): it can be made. */
static void KeepConstructors(FgGen* g, const FcRtClass* rc) {
    FcList* all;
    int i, argc;
    RegisterClass(g, rc->name);
    if (Modular && !LibraryRole && !IsOwnClass(g, rc)) return;   /* (a library keeps the constructors of its classes and makes their objects for Reflect.New) */
    all = fgAllConstructors(g->c, rc);
    for (i = 0; i < all->count; i++) {
        FcProto* ctor = (FcProto*)all->items[i];
        int needed = ctor->paramCount;
        const char* args;
        GetFunc(g, ctor, FK_Ctor, 0);
        while (needed > 0 && needed - 1 < ctor->paramDefaults->count && ctor->paramDefaults->items[needed - 1]) needed--;
        for (argc = needed; argc < ctor->paramCount; argc++) DefaultArgsPre(g, ctor, argc, "self", "list", &args);
    }
}
/* An instance method that a call by name may reach: its dispatchers for every number of arguments it takes. */
static void KeepInstanceMethod(FgGen* g, const char* name, FcProto* m) {
    int required = m->paramCount, argc;
    GetFunc(g, m, FK_Method, 0);
    while (required > 0 && required - 1 < m->paramDefaults->count && m->paramDefaults->items[required - 1]) required--;
    for (argc = required; argc <= m->paramCount; argc++) AddDispatcher(g, name, argc);
    if (fcStartsWith(name, "get_") || fcStartsWith(name, "set_")) AddFieldName(g, name + 4);
}
static void KeepMethod(FgGen* g, const char* name, FcProto* m) {
    if (m->isStatic) GetFunc(g, m, FK_Static, 0);
    else KeepInstanceMethod(g, name, m);
}

/* What reflection can reach by name (see the levels at the top of the C# file). */
static void RegisterReflectionTargets(FgGen* g) {
    int count, i, k;
    const FcRtClass* c;
    if (!g->usesReflection) return;
    if (g->reflNew) {
        FcList* classes = ReflectionTableClasses(g);
        for (i = 0; i < classes->count; i++) KeepConstructors(g, (const FcRtClass*)classes->items[i]);
    }
    /* (the names given as literals to reflection: the members of that name of the classes whose objects exist) */
    if (fcDictCount(g->literalMembers) > 0) {
        count = g->classList->count;   /* (_classList.ToList(): the classes so far) */
        for (i = 0; i < count; i++)
            for (c = ((FgClassInfo*)g->classList->items[i])->rc; c; c = c->base) {
                if (Modular && !IsOwnClass(g, c)) continue;   /* (a library keeps its API itself) */
                FC_DICT_EACH(c->ownFieldInfo, e) if (fcDictHas(g->literalMembers, e->key)) AddFieldName(g, (const char*)e->key);
                FC_DICT_EACH(c->methods, e) {
                    const char* name = (const char*)e->key;
                    FcList* protos = (FcList*)e->value;
                    if (LiteralMember(g, name))
                        for (k = 0; k < protos->count; k++) if (!((FcProto*)protos->items[k])->isStatic) KeepInstanceMethod(g, name, (FcProto*)protos->items[k]);
                }
            }
    }
    if (!g->reflMembers && !g->reflCall) return;
    /* (modules: Get/Set by name go through the tables of the runtime - the program's tables list every field and property of its classes, a library's all of its
     * members; Reflect.Call the methods that are there, like with one file) */
    if (Modular && !g->tablesAll) { g->tablesAll = true; g->version++; }
    count = g->classList->count;
    for (i = 0; i < count; i++) {
        FgClassInfo* cls = (FgClassInfo*)g->classList->items[i];
        if (g->reflMembers && !Modular) FC_DICT_EACH(FG_INDEX(cls->rc), e) AddFieldName(g, (const char*)e->key);
        for (c = cls->rc; c; c = c->base) {
            if (Modular && !IsOwnClass(g, c)) continue;   /* (the table of a library's class names all its members) */
            FC_DICT_EACH(c->methods, e) {
                const char* name = (const char*)e->key;
                FcList* protos = (FcList*)e->value;
                for (k = 0; k < protos->count; k++) {
                    FcProto* m = (FcProto*)protos->items[k];
                    if (m->isStatic) continue;
                    /* a method that is there (level 0: every one) - one that is left out is an error that says so (rf_call) */
                    if (g->reflCall && (g->strip == 0 || fcDictHas(g->funcByProto, m))) KeepInstanceMethod(g, name, m);
                    else if (!Modular && (fcStartsWith(name, "get_") || fcStartsWith(name, "set_"))) AddFieldName(g, name + 4);   /* (RegisterTargets makes the accessors of the names) */
                }
            }
        }
    }
}

/* What stays although nothing may reach it (the stripping levels): everything at level 0; every class with its constructors at 1 and 2, at 1 also
 * with every public member; at every level the classes and members marked `[Keep]` or named as literals to reflection. */
static void RegisterKeptTargets(FgGen* g) {
    FcList* classes = fcListNew(g->c);
    int i, k;
    FC_DICT_EACH(g->program->program->classes, e) fcListAdd(g->c, classes, e->value);
    for (i = 0; i < classes->count; i++) {
        FcRtClass* rc = (FcRtClass*)classes->items[i];
        /* (modules: a library keeps all of its classes itself - the program only makes the kept classes of the libraries known) */
        if (Modular && !IsOwnClass(g, rc)) {
            if (g->strip <= 2 || KeptClass(g, rc)) KeepConstructors(g, rc);
            else if (rc->keptMembers) RegisterClass(g, rc->name);
            continue;
        }
        if (g->strip == 0 || KeptClass(g, rc)) {
            FcDict* index = FG_INDEX(rc);
            KeepConstructors(g, rc);
            FC_DICT_EACH(index, f) AddFieldName(g, (const char*)f->key);
            FC_DICT_EACH(rc->methods, m) { FcList* protos = (FcList*)m->value; for (k = 0; k < protos->count; k++) KeepMethod(g, (const char*)m->key, (FcProto*)protos->items[k]); }
            continue;
        }
        if (g->strip <= 2) KeepConstructors(g, rc);
        if (g->strip == 1)
            FC_DICT_EACH(rc->methods, m) { FcList* protos = (FcList*)m->value; for (k = 0; k < protos->count; k++) if (fgAccessOf((FcProto*)protos->items[k]) == ACC_Public) KeepMethod(g, (const char*)m->key, (FcProto*)protos->items[k]); }
        if (rc->keptMembers) {
            RegisterClass(g, rc->name);
            for (k = 0; k < rc->keptMembers->count; k++) {
                const char* name = (const char*)rc->keptMembers->items[k];
                const char* methodNames[3];
                int n, p;
                if (fgHasField(g, rc, name)) AddFieldName(g, name);
                methodNames[0] = name; methodNames[1] = fcFmt(g->c, "get_%s", name); methodNames[2] = fcFmt(g->c, "set_%s", name);
                for (n = 0; n < 3; n++) {
                    FcList* protos = (FcList*)fcDictGet(rc->methods, methodNames[n]);
                    if (protos) for (p = 0; p < protos->count; p++) KeepMethod(g, methodNames[n], (FcProto*)protos->items[p]);
                }
            }
        }
    }
}

/* ---- the tables ---- */
/* A method that stripping left out (not generated): the highest level that keeps it (1 for a public one, else 0); -1: it is there (or is not a method). */
static int StrippedMethod(FgGen* g, const FcRtClass* rc, const FcMemberMeta* m) {
    FcList* protos;
    int i, mine = 0;
    if (g->strip == 0 || LibraryRole || !streq0(m->kind, "method") || !(protos = (FcList*)fcDictGet(rc->methods, m->name))) return -1;
    for (i = 0; i < protos->count; i++) {
        FcProto* p = (FcProto*)protos->items[i];
        if (p->paramCount != m->paramNames->count || p->isStatic != m->isStatic || (Modular && !OwnsProto(g, p))) continue;
        mine++;
        if (fcDictHas(g->funcByProto, p)) return -1;
    }
    if (mine == 0) return -1;
    return streq0(m->access, "public") ? 1 : 0;
}

/* the state of ReflectionRows (its local functions Strings and Attributes) */
typedef struct FgRfRows { FgGen* g; FcBuf* sb; FcDict* stringLists; int listIndex; } FgRfRows;
static const char* RfStrings(FgRfRows* r, const FcList* items) {
    FgGen* g = r->g;
    FcList* parts;
    const char* text;
    const char* name;
    int i;
    if (!items || items->count == 0) return "NULL";
    parts = fcListNew(g->c);
    for (i = 0; i < items->count; i++) fcListAdd(g->c, parts, (void*)CString(g, (const char*)items->items[i]));
    text = fgJoin(g->c, parts, ", ");
    if ((name = (const char*)fcDictGet(r->stringLists, text)) != NULL) return name;
    name = fcFmt(g->c, "%skRfS%d", Px, r->listIndex++);
    fcDictPut(r->stringLists, text, (void*)name);
    fcBufFmt(r->sb, "static const char* const %s[] = {%s};\n", name, text);
    return name;
}
static const FcJson* fgJsonMember(const FcJson* o, const char* name) {
    const FcJson* v = fcJsonGet(o, name);
    return v && v->kind != FJ_NULL ? v : NULL;
}
static const char* RfJson(FgRfRows* r, FcBuf* body, int* temp, const FcJson* node) {
    FgGen* g = r->g;
    const FcJson* v;
    if (!node || node->kind != FJ_OBJ) return "Undef()";
    if ((v = fgJsonMember(node, "i")) != NULL) return fcFmt(g->c, "Int(%" PRId64 "LL)", (int64_t)strtoll(v->s ? v->s : "0", NULL, 10));
    if ((v = fgJsonMember(node, "f")) != NULL) return fcFmt(g->c, "Float(%s)", DoubleLiteral(g, v->n, false));
    if ((v = fgJsonMember(node, "s")) != NULL) return fcFmt(g->c, "strFromUtf8(%s, list)", CString(g, v->s));
    if ((v = fgJsonMember(node, "b")) != NULL) return v->b ? "Bool(true)" : "Bool(false)";
    if ((v = fgJsonMember(node, "a")) != NULL && v->kind == FJ_ARR) {
        const char* arr = fcFmt(g->c, "t%d", (*temp)++);
        int k;
        fcBufFmt(body, "    Arr* %s = allocArr(%d, list);\n", arr, v->items->count);
        for (k = 0; k < v->items->count; k++) { const char* item = RfJson(r, body, temp, (const FcJson*)v->items->items[k]); fcBufFmt(body, "    { Value x = %s; arrItems(%s)[%d] = x; retain(x); }\n", item, arr, k); }
        return fcFmt(g->c, "ArrV(%s)", arr);
    }
    return "Undef()";
}
/* the attributes of a member: a function that builds [[name, values, texts], ...] (the values the compiler knew, see AttributeMeta.ArgsJson) */
static const char* RfAttributes(FgRfRows* r, const FcList* attributes) {
    FgGen* g = r->g;
    const char* name;
    FcBuf body = {0};
    int temp = 0, i, k;
    if (!attributes || attributes->count == 0) return "NULL";
    name = fcFmt(g->c, "%srf_attrs%d", Px, r->listIndex++);
    fcBufStr(&body, "");
    fcBufFmt(&body, "    Arr* all = allocArr(%d, list);\n", attributes->count);
    for (i = 0; i < attributes->count; i++) {
        const FcAttributeMeta* at = (const FcAttributeMeta*)attributes->items[i];
        FcJsonReader j;
        FcJson* parsed = NULL;
        int nvalues;
        if (at->argsJson) { j.c = g->c; j.p = at->argsJson; j.bad = false; parsed = fcJsonValue(&j); }
        nvalues = parsed && parsed->kind == FJ_ARR ? parsed->items->count : 0;
        const char* entry = fcFmt(g->c, "t%d", temp++);
        const char* vals = fcFmt(g->c, "t%d", temp++);
        fcBufFmt(&body, "    Arr* %s = allocArr(3, list);\n", entry);
        fcBufFmt(&body, "    { Value x = strFromUtf8(%s, list); arrItems(%s)[0] = x; retain(x); }\n", CString(g, at->name), entry);
        fcBufFmt(&body, "    Arr* %s = allocArr(%d, list);\n", vals, nvalues);
        for (k = 0; k < nvalues; k++) { const char* item = RfJson(r, &body, &temp, (const FcJson*)parsed->items->items[k]); fcBufFmt(&body, "    { Value x = %s; arrItems(%s)[%d] = x; retain(x); }\n", item, vals, k); }
        fcBufFmt(&body, "    arrItems(%s)[1] = ArrV(%s);\n", entry, vals);
        {
            const char* list = RfStrings(r, at->argTexts);
            fcBufFmt(&body, "    arrItems(%s)[2] = strArrayOf(%s, %d, list);\n", entry, list, at->argTexts ? at->argTexts->count : 0);
        }
        fcBufFmt(&body, "    arrItems(all)[%d] = ArrV(%s);\n", i, entry);
    }
    fcBufStr(&body, "    return ArrV(all);\n");
    fcBufFmt(r->sb, "static Value %s(OwnList* list) {\n", name);
    fcBufStr(r->sb, fcBufDone(g->c, &body));
    fcBufStr(r->sb, "}\n");
    return name;
}
/* The rows of the reflection tables (RfClass) of these classes, in their order; their lists and functions go into `sb` first. A method that stripping
 * left out is no row of its class, its name goes into the list `stripped` of the class (with the level that keeps it first: "1Bark"). */
static FcList* ReflectionRows(FgGen* g, FcBuf* sb, const FcList* classes) {
    FgRfRows r;
    FcList* classRows = fcListNew(g->c);
    int i, k, s;
    r.g = g; r.sb = sb; r.stringLists = fcDictNew(g->c, FC_KEY_STR); r.listIndex = g->rfListIndex;
    for (i = 0; i < classes->count; i++) {
        const FcRtClass* rc = (const FcRtClass*)classes->items[i];
        FcList* memberRows = fcListNew(g->c);
        FcList* stripped = fcListNew(g->c);
        const char* membersName = "NULL";
        FcList* interfaces;
        FcList* declared;
        const char* classAttrs;
        FcList* members = rc->meta ? rc->meta->members : NULL;
        for (k = 0; members && k < members->count; k++) {
            const FcMemberMeta* m = (const FcMemberMeta*)members->items[k];
            int keepAt = StrippedMethod(g, rc, m), flags;
            const char* pnames;
            const char* ptypes;
            const char* attrs;
            if (keepAt >= 0) {
                bool known = false;
                for (s = 0; s < stripped->count; s++) if (streq0((const char*)stripped->items[s] + 1, m->name)) known = true;
                if (!known) fcListAdd(g->c, stripped, (void*)fcFmt(g->c, "%d%s", keepAt, m->name));
                continue;
            }
            pnames = RfStrings(&r, m->paramNames);
            ptypes = RfStrings(&r, m->paramTypes);
            flags = (m->isStatic ? 1 : 0) | (m->isReadonly ? 2 : 0) | (m->canRead ? 4 : 0) | (m->canWrite ? 8 : 0);
            attrs = RfAttributes(&r, m->attributes);
            fcListAdd(g->c, memberRows, (void*)fcFmt(g->c, "{%s, %s, %s, %s, %d, %s, %s, %s, %s, %d, %s}", CString(g, m->name), CString(g, m->kind), CString(g, m->typeName),
                CString(g, m->access), flags, CString(g, m->unit), CString(g, rc->name), pnames, ptypes, m->paramNames->count, attrs));
        }
        if (memberRows->count > 0) {
            membersName = fcFmt(g->c, "%skRfM%d", Px, r.listIndex++);
            fcBufFmt(sb, "static const RfMember %s[] = {%s};\n", membersName, fgJoin(g->c, memberRows, ", "));
        }
        /* (as the VM: every interface the class reaches - also over its base classes - and the ones it names itself) */
        interfaces = fgReflectedInterfaces(g->c, rc, false);
        declared = fgReflectedInterfaces(g->c, rc, true);
        classAttrs = RfAttributes(&r, rc->meta ? rc->meta->attributes : NULL);
        {
            const char* name = CString(g, rc->name);
            const char* base = rc->base ? CString(g, rc->base->name) : "NULL";
            const char* ifaces = RfStrings(&r, interfaces);
            const char* decl = RfStrings(&r, declared);
            const char* strippedList = RfStrings(&r, stripped);
            fcListAdd(g->c, classRows, (void*)fcFmt(g->c, "{%s, %s, %d, %s, %d, %s, %d, %s, %d, %s, %s, %d, NULL}", name, base, rc->isActor ? 1 : 0, ifaces, interfaces->count,
                decl, declared->count, membersName, memberRows->count, classAttrs, strippedList, stripped->count));
        }
    }
    g->rfListIndex = r.listIndex;
    return classRows;
}

/* The constructors of a class for Reflect.New: an arm per number of arguments. */
static FcList* NewCases(FgGen* g, const FcRtClass* rc, const FgClassInfo* info) {
    FcList* byArgc = fcListNew(g->c);
    FcList* all = fgAllConstructors(g->c, rc);
    int i, argc, k;
    for (i = 0; i < all->count; i++) {
        FcProto* ctor = (FcProto*)all->items[i];
        int required = ctor->paramCount;
        while (required > 0 && required - 1 < ctor->paramDefaults->count && ctor->paramDefaults->items[required - 1]) required--;
        for (argc = required; argc <= ctor->paramCount; argc++) {
            const char* defaults;
            const char* pre;
            FcBuf items = {0};
            int access;
            const char* guard = "";
            if (fgFindConstructor(rc, argc) != ctor) continue;
            pre = DefaultArgsPre(g, ctor, argc, "o", "list", &defaults);
            fcBufStr(&items, "");
            for (k = 0; k < argc; k++) fcBufFmt(&items, ", items[%d]", k);
            access = fgAccessOf(ctor);
            if (CheckAccess && access != ACC_Public) {
                const char* allowed = AllowedExpr(g, rc, access);
                guard = fcFmt(g->c, "if (!%s) return %s(%s); ", allowed, LibraryRole ? "rfFail" : "accessDeniedNew",
                    CString(g, fcFmt(g->c, "The constructor of '%s' is %s and cannot be called from here.", rc->name, DescribeAccess(access))));
            }
            {
                const char* make = NewObjectCall(g, info, "list");
                const char* fn = GetFunc(g, ctor, FK_Ctor, 0)->name;
                fcListAdd(g->c, byArgc, (void*)fcFmt(g->c, "            case %d: { %sValue o = %s; %s%s%s(o%s%s); %sctorEnd(o); return o; }", argc, guard, make, pre, RecInitEnter(info), fn,
                    fcBufDone(g->c, &items), defaults, RecInitLeave(info)));
            }
        }
    }
    return byArgc;
}

static const char* NewFunctionName(FgGen* g, const char* module, const FcRtClass* rc) { return fcFmt(g->c, "fire_new_%s_%s", module, Mangle(g, rc->name)); }
/* The name of the reflection row of a class (modules: every module writes those of its classes). */
static const char* RfRowName(FgGen* g, const char* module, const FcRtClass* rc) { return fcFmt(g->c, "fire_rf_%s_%s", module, Mangle(g, rc->name)); }

/* A library module of a program with reflection: the rows of all of its classes (the program lists those its tables have). */
static const char* LibraryReflectionRows(FgGen* g) {
    FcBuf sb = {0};
    FcList* own;
    FcList* rows;
    FcList* byName;
    int i, k;
    if (!g->usesReflection) return "";
    fcBufStr(&sb, "");
    own = fcListNew(g->c);
    byName = fgClassesByName(g);
    for (i = 0; i < byName->count; i++) if (IsOwnClass(g, (const FcRtClass*)byName->items[i])) fcListAdd(g->c, own, byName->items[i]);
    rows = ReflectionRows(g, &sb, own);
    /* (a plugin: Reflect.New of the program makes the objects of its classes through their rows) */
    if (g->plugin)
        for (i = 0; i < own->count; i++) {
            const char* row = (const char*)rows->items[i];
            const char* fn = NewFunctionName(g, ModId(g), (const FcRtClass*)own->items[i]);
            fcBufFmt(&sb, "Value %s(int argc, const Value* items, OwnList* list, uint32_t caller);\n", fn);
            rows->items[i] = (void*)fcFmt(g->c, "%s%s}", fcStrN(g->c, row, strlen(row) - strlen("NULL}")), fn);
        }
    for (i = 0; i < own->count; i++) fcBufFmt(&sb, "const RfClass %s = %s;\n", RfRowName(g, ModId(g), (const FcRtClass*)own->items[i]), (const char*)rows->items[i]);
    /* Reflect.New of its classes (the program's rf_new calls them by the name of the class) */
    for (i = 0; i < own->count; i++) {
        const FcRtClass* rc = (const FcRtClass*)own->items[i];
        FcList* cases;
        fcBufFmt(&sb, "Value %s(int argc, const Value* items, OwnList* list, uint32_t caller) {\n", NewFunctionName(g, ModId(g), rc));
        fcBufStr(&sb, "    (void)items; (void)list; (void)caller;\n");
        fcBufStr(&sb, "    switch (argc) {\n");
        cases = NewCases(g, rc, RegisterClass(g, rc->name));
        for (k = 0; k < cases->count; k++) fcBufFmt(&sb, "%s\n", (const char*)cases->items[k]);
        fcBufStr(&sb, "        default: break;\n");
        fcBufStr(&sb, "    }\n");
        fcBufFmt(&sb, "    { char t[300]; snprintf(t, sizeof t, \"'%%s' has no constructor with %%d parameter(s).\", %s, argc); return rfFail(t); }\n", CString(g, rc->name));
        fcBufStr(&sb, "}\n");
    }
    return fcBufDone(g->c, &sb);
}

/* ---- the code ---- */
static const char* ProbeCode(FgGen* g, FcList* fieldNames);
static const char* ReflectionCode(FgGen* g) {
    FcBuf sb = {0};
    FcList* classes = ReflectionTableClasses(g);
    const char* rfAt = Modular ? "kRfClassList[i]" : "&kRfClasses[i]";
    FcList* strippedClasses;
    FcList* fieldNames;
    FcBuf get = {0}, set = {0}, callSb = {0}, newSb = {0};
    FcList* dispatchers;
    int i, k;
    const char* text;

    fcBufStr(&sb, "");
    /* ---- tables ---- */
    /* (modules: the rows of the classes of a library are in its file, the program lists them) */
    if (Modular) {
        FcList* own = fcListNew(g->c);
        FcList* ownRows;
        FcList* refs = fcListNew(g->c);
        for (i = 0; i < classes->count; i++) if (IsOwnClass(g, (const FcRtClass*)classes->items[i])) fcListAdd(g->c, own, classes->items[i]);
        ownRows = ReflectionRows(g, &sb, own);
        for (i = 0; i < own->count; i++) fcBufFmt(&sb, "static const RfClass %s = %s;\n", RfRowName(g, "prog", (const FcRtClass*)own->items[i]), (const char*)ownRows->items[i]);
        for (i = 0; i < classes->count; i++) {
            const FcRtClass* rc = (const FcRtClass*)classes->items[i];
            if (!IsOwnClass(g, rc)) fcBufFmt(&sb, "extern const RfClass %s;\n", RfRowName(g, ModIdOf(g, rc), rc));
        }
        for (i = 0; i < classes->count; i++) { const FcRtClass* rc = (const FcRtClass*)classes->items[i]; fcListAdd(g->c, refs, (void*)fcFmt(g->c, "&%s", RfRowName(g, ModIdOf(g, rc), rc))); }
        if (refs->count == 0) fcListAdd(g->c, refs, (void*)"NULL");
        fcBufFmt(&sb, "static const RfClass* const kRfClassList[] = {%s};\n", fgJoin(g->c, refs, ", "));
    } else {
        FcList* classRows = ReflectionRows(g, &sb, classes);
        fcBufFmt(&sb, "static const RfClass kRfClasses[] = {%s};\n", fgJoin(g->c, classRows, ",\n    "));
    }
    fcBufFmt(&sb, "static const uint32_t kRfClassCount = %d;\n", classes->count);
    /* the classes stripping left out (level 3): a lookup of one by its name is an error that says so */
    strippedClasses = fcListNew(g->c);
    if (g->strip != 0) {
        FcDict* inTables = fcDictNew(g->c, FC_KEY_PTR);
        for (i = 0; i < classes->count; i++) fcSetAdd(inTables, classes->items[i]);
        FC_DICT_EACH(g->program->program->classes, e) if (!fcDictHas(inTables, e->value)) fcListAdd(g->c, strippedClasses, (void*)((FcRtClass*)e->value)->name);
        if (strippedClasses->count > 1) qsort(strippedClasses->items, (size_t)strippedClasses->count, sizeof(void*), fgCmpOrdinal);
    }
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static const int kRfStripping = %d;\n", g->strip);
    {
        FcList* texts = fcListNew(g->c);
        for (i = 0; i < strippedClasses->count; i++) fcListAdd(g->c, texts, (void*)CString(g, (const char*)strippedClasses->items[i]));
        fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static const char* const kRfStrippedClasses[] = {%s};\n", texts->count ? fgJoin(g->c, texts, ", ") : "NULL");
    }
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static const uint32_t kRfStrippedCount = %d;\n", strippedClasses->count);
    fcBufStr(&sb,
        "FIRE_MAYBE_UNUSED static bool rf_isStripped(Value name) {\n"
        "    if (name.kind != K_String) return false;\n"
        "    for (uint32_t i = 0; i < kRfStrippedCount; i++) if (strIs(name, kRfStrippedClasses[i])) return true;\n"
        "    return false;\n"
        "}\n"
        "/* a class that stripping left out (docs/spec/NATIVE_BACKEND.md): the error says so and what keeps it */\n"
        "FIRE_MAYBE_UNUSED static Value rf_strippedClass(Value name) {\n"
        "    char n[200], t[700]; strToUtf8(name, n, sizeof n);\n"
        "    snprintf(t, sizeof t, \"Class '%s' was left out of the build (stripping %d). Add [Keep] to '%s' or use stripping <= 2.\", n, kRfStripping, n);\n"
        "    return rfFail(t);\n"
        "}\n");
    fcBufFmt(&sb, "#define RF_CLASS_AT(i) (%s)\n", fgReplace(g->c, rfAt, "[i]", "[(i)]"));
    if (g->openWorld) fcBufStr(&sb, "#define RF_PLUGIN_COUNT g_pluginRfCount   // (the classes of the plugins the program loaded)\n");
    fcBufStr(&sb, "FIRE_MAYBE_UNUSED static const RfClass* rf_find(Value name) {\n");
    fcBufFmt(&sb, "    for (uint32_t i = 0; i < kRfClassCount; i++) if (strIs(name, (%s)->name)) return %s;\n", rfAt, rfAt);
    if (g->openWorld) fcBufStr(&sb, "    for (uint32_t i = 0; i < g_pluginRfCount; i++) if (strIs(name, ((const RfClass*)g_pluginRf[i])->name)) return (const RfClass*)g_pluginRf[i];   // (the classes of plugins)\n");
    fcBufStr(&sb, "    return NULL;\n");
    fcBufStr(&sb, "}\n");
    fcBufStr(&sb, "FIRE_MAYBE_UNUSED static const RfClass* rf_findC(const char* name) {\n");
    fcBufFmt(&sb, "    for (uint32_t i = 0; i < kRfClassCount; i++) if (strcmp(name, (%s)->name) == 0) return %s;\n", rfAt, rfAt);
    if (g->openWorld) fcBufStr(&sb, "    for (uint32_t i = 0; i < g_pluginRfCount; i++) if (strcmp(name, ((const RfClass*)g_pluginRf[i])->name) == 0) return (const RfClass*)g_pluginRf[i];\n");
    fcBufStr(&sb, "    return NULL;\n");
    fcBufStr(&sb, "}\n");
    fcBufStr(&sb, "FIRE_MAYBE_UNUSED static const RfClass* rf_classOf(uint32_t cls) { return rf_findC(className(cls)); }\n");
    fcBufStr(&sb,
        "FIRE_MAYBE_UNUSED static const RfClass* rf_baseOf(const RfClass* c) { return c->base ? rf_findC(c->base) : NULL; }\n"
        "FIRE_MAYBE_UNUSED static bool rf_sameKey(const RfMember* a, const RfMember* b) {\n"
        "    bool am = strcmp(a->kind, \"method\") == 0, bm = strcmp(b->kind, \"method\") == 0;\n"
        "    return am == bm && strcmp(a->name, b->name) == 0 && (!am || a->pcount == b->pcount);\n"
        "}\n"
        "FIRE_MAYBE_UNUSED static bool rf_visible(const RfClass* from, const RfClass* level, uint32_t index) {\n"
        "    const RfMember* m = &level->members[index];\n"
        "    if (strcmp(m->kind, \"constructor\") == 0) return level == from;\n"
        "    for (const RfClass* c = from; c; c = rf_baseOf(c)) {\n"
        "        uint32_t n = c == level ? index : c->nMembers;\n"
        "        for (uint32_t i = 0; i < n; i++)\n"
        "            if (strcmp(c->members[i].kind, \"constructor\") != 0 && rf_sameKey(&c->members[i], m)) return false;\n"
        "        if (c == level) break;\n"
        "    }\n"
        "    return true;\n"
        "}\n");
    /* member lookup: 1 field, 2 property, 3 method (the closest, fields first like the VM), 0 nothing */
    fcBufStr(&sb,
        "FIRE_MAYBE_UNUSED static int rf_kind(const RfClass* c, Value name) {\n"
        "    int best = 0;\n"
        "    for (const RfClass* l = c; l; l = rf_baseOf(l))\n"
        "        for (uint32_t i = 0; i < l->nMembers; i++) {\n"
        "            const RfMember* m = &l->members[i];\n"
        "            if (!strIs(name, m->name) || strcmp(m->kind, \"constructor\") == 0 || !rf_visible(c, l, i)) continue;\n"
        "            int k = strcmp(m->kind, \"field\") == 0 ? ((m->flags & 1) ? 0 : 1) : strcmp(m->kind, \"property\") == 0 ? 2 : 3;\n"
        "            if (k && (best == 0 || k < best)) best = k;\n"
        "        }\n"
        "    return best;\n"
        "}\n");
    fcBufStr(&sb,
        "FIRE_MAYBE_UNUSED static const RfMember* rf_member(const RfClass* c, Value name, const char* kind) {\n"
        "    for (const RfClass* l = c; l; l = rf_baseOf(l))\n"
        "        for (uint32_t i = 0; i < l->nMembers; i++)\n"
        "            if (strIs(name, l->members[i].name) && strcmp(l->members[i].kind, kind) == 0 && rf_visible(c, l, i)) return &l->members[i];\n"
        "    return NULL;\n"
        "}\n");
    /* a member that stripping left out: its class (or a base) names it in `stripped`, after the level that keeps it ("1Bark") */
    fcBufStr(&sb,
        "FIRE_MAYBE_UNUSED static const char* rf_stripped(const RfClass* c, Value name, const RfClass** in) {\n"
        "    for (const RfClass* l = c; l; l = rf_baseOf(l))\n"
        "        for (uint32_t i = 0; i < l->nStripped; i++) if (strIs(name, l->stripped[i] + 1)) { *in = l; return l->stripped[i]; }\n"
        "    return NULL;\n"
        "}\n"
        "FIRE_MAYBE_UNUSED static Value rf_strippedMember(const RfClass* in, const char* entry) {\n"
        "    char t[700]; int keep = entry[0] - '0';\n"
        "    if (keep == 0) snprintf(t, sizeof t, \"Member '%s.%s' was left out of the build (stripping %d). Add [Keep] to '%s.%s' or use stripping 0.\", in->name, entry + 1, kRfStripping, in->name, entry + 1);\n"
        "    else snprintf(t, sizeof t, \"Member '%s.%s' was left out of the build (stripping %d). Add [Keep] to '%s.%s' or use stripping <= %d.\", in->name, entry + 1, kRfStripping, in->name, entry + 1, keep);\n"
        "    return rfFail(t);\n"
        "}\n");
    fcBufStr(&sb, "FIRE_MAYBE_UNUSED static bool rf_isSub(const char* name, const char* base) {\n");
    fcBufStr(&sb, "    for (const RfClass* c = rf_findC(name); c; c = c->base ? rf_findC(c->base) : NULL) if (strcmp(c->name, base) == 0) return true;\n");
    fcBufStr(&sb, "    return false;\n");
    fcBufStr(&sb, "}\n");

    /* ---- the natives ---- */
    fcBufStr(&sb,
        "FIRE_MAYBE_UNUSED static Value rf_class_name(Value x, OwnList* list) {\n"
        "    if (x.kind == K_Class) return strFromUtf8(className(asObj(x)->cls), list);\n"
        "    if (x.kind == K_String) { const RfClass* c = rf_find(x); if (c) return strFromUtf8(c->name, list); if (rf_isStripped(x)) return rf_strippedClass(x); }\n"
        "    return Undef();\n"
        "}\n"
        "FIRE_MAYBE_UNUSED static Value rf_class_info(Value name, OwnList* list) {\n"
        "    const RfClass* c = name.kind == K_String ? rf_find(name) : NULL;\n"
        "    if (!c) return rf_isStripped(name) ? rf_strippedClass(name) : Undef();\n"
        "    Arr* info = allocArr(6, list);\n"
        "    Value v0 = strFromUtf8(c->name, list); arrItems(info)[0] = v0; retain(v0);\n"
        "    if (c->base) { Value v1 = strFromUtf8(c->base, list); arrItems(info)[1] = v1; retain(v1); }\n"
        "    arrItems(info)[2] = Bool(c->isActor != 0);\n"
        "    Value v3 = strArrayOf(c->ifaces, c->nIfaces, list); arrItems(info)[3] = v3;\n"
        "    arrItems(info)[4] = c->attrs ? c->attrs(list) : ArrV(allocArr(0, list));   /* (the attributes before the class) */\n"
        "    arrItems(info)[5] = strArrayOf(c->declIfaces, c->nDeclIfaces, list);\n"
        "    return ArrV(info);\n"
        "}\n"
        "/* the enum of a value (its full name, undefined for anything else), and a number as a value of the enum of another value */\n"
        "FIRE_MAYBE_UNUSED static Value rf_enum_name(Value v, OwnList* list) { const FireEnum* e = enumOf(v); return e ? strFromUtf8(e->name, list) : Undef(); }\n"
        "FIRE_MAYBE_UNUSED static Value rf_enum_of(Value v, Value n) { return enumOf(v) && n.kind == K_Int ? EnumV(n.i, v.reserved) : n; }\n"
        "FIRE_MAYBE_UNUSED static Value rf_members(Value name, OwnList* list) {\n"
        "    const RfClass* c = name.kind == K_String ? rf_find(name) : NULL;\n"
        "    if (!c) return Undef();\n"
        "    uint32_t count = 0, at = 0;\n"
        "    for (const RfClass* l = c; l; l = rf_baseOf(l))\n"
        "        for (uint32_t i = 0; i < l->nMembers; i++) if (rf_visible(c, l, i)) count++;\n"
        "    Arr* all = allocArr(count, list);\n"
        "    for (const RfClass* l = c; l; l = rf_baseOf(l))\n"
        "    for (uint32_t i = 0; i < l->nMembers; i++) {\n"
        "        if (!rf_visible(c, l, i)) continue;\n"
        "        const RfMember m = l->members[i];\n"
        "        Arr* d = allocArr(13, list);\n"
        "        const char* texts[4];\n"
        "        texts[0] = m.name; texts[1] = m.kind; texts[2] = m.type; texts[3] = m.access;\n"
        "        for (int k = 0; k < 4; k++) { Value s = strFromUtf8(texts[k], list); arrItems(d)[k] = s; retain(s); }\n"
        "        arrItems(d)[4] = Bool((m.flags & 1) != 0); arrItems(d)[5] = Bool((m.flags & 2) != 0);\n"
        "        arrItems(d)[6] = Bool((m.flags & 4) != 0); arrItems(d)[7] = Bool((m.flags & 8) != 0);\n"
        "        Value u = strFromUtf8(m.unit, list); arrItems(d)[8] = u; retain(u);\n"
        "        Value dec = strFromUtf8(m.declared, list); arrItems(d)[9] = dec; retain(dec);\n"
        "        arrItems(d)[10] = strArrayOf(m.pnames, m.pcount, list);\n"
        "        arrItems(d)[11] = strArrayOf(m.ptypes, m.pcount, list);\n"
        "        arrItems(d)[12] = m.attrs ? m.attrs(list) : ArrV(allocArr(0, list));\n"
        "        arrItems(all)[at++] = ArrV(d);\n"
        "    }\n"
        "    return ArrV(all);\n"
        "}\n"
        "FIRE_MAYBE_UNUSED static Value rf_classes(OwnList* list) {\n"
        "    Arr* names = allocArr(kRfClassCount + RF_PLUGIN_COUNT, list);\n"
        "    for (uint32_t i = 0; i < kRfClassCount; i++) { Value s = strFromUtf8(RF_CLASS_AT(i)->name, list); arrItems(names)[i] = s; retain(s); }\n"
        "    for (uint32_t i = 0; i < RF_PLUGIN_COUNT; i++) { Value s = strFromUtf8(((const RfClass*)g_pluginRf[i])->name, list); arrItems(names)[kRfClassCount + i] = s; retain(s); }\n"
        "    return ArrV(names);\n"
        "}\n"
        "FIRE_MAYBE_UNUSED static Value rf_is_sub(Value name, Value base) {\n"
        "    char a[200], b[200];\n"
        "    strToUtf8(name, a, sizeof a); strToUtf8(base, b, sizeof b);\n"
        "    return Bool(rf_isSub(a, b));\n"
        "}\n"
        "FIRE_MAYBE_UNUSED static Value rf_has(Value obj, Value name) {\n"
        "    if (obj.kind != K_Class) return Bool(false);\n"
        "    return Bool(rf_kind(rf_classOf(asObj(obj)->cls), name) != 0);\n"
        "}\n"
        "FIRE_MAYBE_UNUSED static Value rf_member_kind(Value obj, Value name, OwnList* list) {\n"
        "    if (obj.kind != K_Class) return Undef();\n"
        "    switch (rf_kind(rf_classOf(asObj(obj)->cls), name)) {\n"
        "        case 1: return strFromUtf8(\"field\", list);\n"
        "        case 2: return strFromUtf8(\"property\", list);\n"
        "        case 3: return strFromUtf8(\"method\", list);\n"
        "        default: return Undef();\n"
        "    }\n"
        "}\n"
        "FIRE_MAYBE_UNUSED static Value rf_selector_path(Value l, OwnList* list) {\n"
        "    if (l.kind != K_Lambda) {\n"
        "        char text[200];\n"
        "        snprintf(text, sizeof text, \"A selector ('lambda member<...>' etc.) expects a lambda like `c => c.radius`, got: %s.\", kindName(l));\n"
        "        return rfFail(text);\n"
        "    }\n"
        "    Lam* lam = lamOf(l);\n"
        "    if (!lam->sel) return rfFail(\"The lambda is not a selector: it needs exactly one parameter, and its body may only be a member chain on it (`c => c.radius`, `p => p.address.city`).\");\n"
        "    return strArrayOf(lam->sel, lam->nsel, list);\n"
        "}\n");

    /* ---- get / set / call / new ---- */
    fieldNames = fgSortedKeys(g->c, g->fieldNames, true);
    fcBufStr(&get, ""); fcBufStr(&set, ""); fcBufStr(&callSb, ""); fcBufStr(&newSb, "");
    if (Modular) {
        fcBufStr(&get, "    { char n_[200]; strToUtf8(name, n_, sizeof n_); return fireGetField(obj, n_, list, caller); }\n");
        /* (an object with probes: they hear of it - the member is written by its name) */
        fcBufFmt(&set, "    { char n_[200]; strToUtf8(name, n_, sizeof n_);%s fireSetField(obj, value, n_, list, caller); return Undef(); }\n",
            g->usesProbes ? " if (FIRE_UNLIKELY(asObj(obj)->h.flags & 2)) { probedSet(obj, n_, value, NULL, NULL, caller); return Undef(); }" : "");
    }
    for (i = 0; i < (Modular ? 0 : fieldNames->count); i++) {
        const char* name = (const char*)fieldNames->items[i];
        const char* tail = fcFmt(g->c, "%s%s", FieldHasList(g, name) ? ", list" : "", FieldHasCaller(g, name) ? ", caller" : "");
        FcBuf arms = {0};
        int bytesFirst = -1, wordsFirst = -1, nArms = 0, pass;
        const char* toBuffer = "";
        fcBufFmt(&get, "    if (strIs(name, %s)) return gf_%s(obj%s);\n", CString(g, name), Mangle(g, name), tail);
        /* (`byte data[]`/`word w[]`: a store by name gets the buffer a compiled store converts to; the groups in the order of their first class) */
        for (k = 0; k < g->classList->count; k++) {
            FgClassInfo* c = (FgClassInfo*)g->classList->items[k];
            int kind;
            if (!fgHasField(g, c->rc, name) || (kind = BufferFieldOf(c->rc, name)) == 0) continue;
            if (kind == BufWords) { if (wordsFirst < 0) wordsFirst = k; }
            else if (bytesFirst < 0) bytesFirst = k;
        }
        fcBufStr(&arms, "");
        for (pass = 0; pass < 2; pass++) {
            bool words = (pass == 0) == (wordsFirst >= 0 && (bytesFirst < 0 || wordsFirst < bytesFirst));
            if ((words ? wordsFirst : bytesFirst) < 0) continue;
            for (k = 0; k < g->classList->count; k++) {
                FgClassInfo* c = (FgClassInfo*)g->classList->items[k];
                int kind;
                if (!fgHasField(g, c->rc, name) || (kind = BufferFieldOf(c->rc, name)) == 0 || (kind == BufWords) != words) continue;
                fcBufFmt(&arms, "case %d: ", c->id);
            }
            fcBufFmt(&arms, "{ Value nb_ = %s(value, 1, list); if (nb_.kind == K_Buffer && value.kind == K_Array && ownedOf(nb_) && scopeListOf(ownedOf(nb_))) claimToHolder(obj, nb_, scopeListOf(ownedOf(nb_))); value = nb_; } break; ",
                words ? "typeWordBuffer" : "typeBuffer");
            nArms++;
        }
        if (nArms > 0) toBuffer = fcFmt(g->c, "switch (asObj(obj)->cls) { %sdefault: break; } %s", fcBufDone(g->c, &arms), g->usesExceptions ? "if (FIRE_UNLIKELY(g_unwind.active)) return Undef(); " : "");
        fcBufFmt(&set, "    if (strIs(name, %s)) { %ssf_%s(obj, value%s); return Undef(); }\n", CString(g, name), toBuffer, Mangle(g, name), tail);
    }
    fcBufFmt(&sb,
        "FIRE_MAYBE_UNUSED static Value rf_get(Value obj, Value name, OwnList* list, uint32_t caller) {\n"
        "    (void)list; (void)caller;\n"
        "    if (obj.kind != K_Class) { char t[120]; snprintf(t, sizeof t, \"Reflect.Get: expects an object, got: %%s.\", kindName(obj)); return rfFail(t); }\n"
        "    const RfClass* c = rf_classOf(asObj(obj)->cls);\n"
        "    int kind = rf_kind(c, name);\n"
        "    const RfMember* getter = rf_member(c, name, \"property\");\n"
        "    if (kind != 1 && !(getter && (getter->flags & 4))) { char n[200], t[500]; strToUtf8(name, n, sizeof n); snprintf(t, sizeof t, \"'%%s' has no readable member '%%s'.\", c->name, n); return rfFail(t); }\n"
        "%s    { const RfClass* in = NULL; const char* s = rf_stripped(c, name, &in); if (s) return rf_strippedMember(in, s); }\n"
        "    { char n[200], t[700]; strToUtf8(name, n, sizeof n); snprintf(t, sizeof t, \"Member '%%s.%%s' was left out of the build (stripping %%d): it cannot be read by name. Add [Keep] to it or use stripping 0.\", c->name, n, kRfStripping); return rfFail(t); }\n"
        "}\n", fcBufDone(g->c, &get));
    fcBufFmt(&sb,
        "FIRE_MAYBE_UNUSED static Value rf_set(Value obj, Value name, Value value, OwnList* list, uint32_t caller) {\n"
        "    (void)list; (void)caller;\n"
        "    if (obj.kind != K_Class) { char t[120]; snprintf(t, sizeof t, \"Reflect.Set: expects an object, got: %%s.\", kindName(obj)); return rfFail(t); }\n"
        "    const RfClass* c = rf_classOf(asObj(obj)->cls);\n"
        "    int kind = rf_kind(c, name);\n"
        "    const RfMember* prop = rf_member(c, name, \"property\");\n"
        "    if (kind != 1 && !(prop && (prop->flags & 8))) {\n"
        "        char n[200], t[500]; strToUtf8(name, n, sizeof n);\n"
        "        if (prop && (prop->flags & 4)) snprintf(t, sizeof t, \"The property '%%s' of '%%s' has no setter (only 'get').\", n, c->name);\n"
        "        else snprintf(t, sizeof t, \"'%%s' has no writable member '%%s'.\", c->name, n);\n"
        "        return rfFail(t);\n"
        "    }\n"
        "    { const RfMember* field = rf_member(c, name, \"field\"); if (field && !(field->flags & 1) && (field->flags & 2)) {\n"
        "        char n[200], t[500]; strToUtf8(name, n, sizeof n);\n"
        "        snprintf(t, sizeof t, \"The field '%%s' of '%%s' is 'readonly' and cannot be assigned.\", n, field->declared);\n"
        "        return rfFail(t);\n"
        "    } }\n"
        "%s    { char n[200], t[700]; strToUtf8(name, n, sizeof n); snprintf(t, sizeof t, \"Member '%%s.%%s' was left out of the build (stripping %%d): it cannot be written by name. Add [Keep] to it or use stripping 0.\", c->name, n, kRfStripping); return rfFail(t); }\n"
        "}\n", fcBufDone(g->c, &set));

    /* calls: one entry per (name, number of arguments) that some class has */
    if (Modular)
        fcBufStr(&callSb, "    { char n_[200]; uint32_t k_; const FireMethod* m_; const RfMember* sm_; strToUtf8(name, n_, sizeof n_);\n"
            "      m_ = fireFindMethod(asObj(obj)->cls, n_, argc, &k_);\n"
            "      if (m_) return m_->fn(k_, obj, items, list, caller);\n"
            "      sm_ = rf_member(rf_classOf(asObj(obj)->cls), name, \"method\");\n"
            "      if (sm_ && (sm_->flags & 1) && sm_->pcount == (uint32_t)argc) { char u[400]; snprintf(u, sizeof u, \"'%s' is a static method - Reflect.Call only calls instance methods.\", n_); return rfFail(u); } }\n");
    dispatchers = Modular ? fcListNew(g->c) : fgSortedNameArgc(g, g->dispatchers);
    for (i = 0; i < dispatchers->count; i++) {
        const FgNameArgc* na = (const FgNameArgc*)dispatchers->items[i];
        FcBuf inst = {0}, stat = {0}, items = {0};
        int nInst = 0, nStat = 0;
        if (fcStartsWith(na->name, "operator")) continue;
        fcBufStr(&inst, ""); fcBufStr(&stat, ""); fcBufStr(&items, "");
        for (k = 0; k < g->classList->count; k++) {
            FgClassInfo* cls = (FgClassInfo*)g->classList->items[k];
            FcProto* p = FG_FIND(cls->rc, na->name, na->argc).proto;
            if (!p) continue;
            if (p->isStatic) { fcBufFmt(&stat, "case %d: ", cls->id); nStat++; }
            else { fcBufFmt(&inst, "case %d: ", cls->id); nInst++; }
        }
        if (nInst == 0 && nStat == 0) continue;
        for (k = 0; k < na->argc; k++) fcBufFmt(&items, ", items[%d]", k);
        fcBufFmt(&callSb, "    if (argc == %d && strIs(name, %s)) {\n", na->argc, CString(g, na->name));
        fcBufStr(&callSb, "        switch (asObj(obj)->cls) {\n");
        if (nInst > 0) fcBufFmt(&callSb, "            %s{ Value r = call_%s_%d(obj%s%s); adopt(r, list); return r; }\n", fcBufDone(g->c, &inst), Mangle(g, na->name), na->argc, fcBufDone(g->c, &items),
            DispatchTail(g, na->name, na->argc, "list", "caller"));
        if (nStat > 0) fcBufFmt(&callSb, "            %s{ char t[300]; strToUtf8(name, t, sizeof t); char u[400]; snprintf(u, sizeof u, \"'%%s' is a static method - Reflect.Call only calls instance methods.\", t); return rfFail(u); }\n",
            fcBufDone(g->c, &stat));
        fcBufStr(&callSb, "            default: break;\n");
        fcBufStr(&callSb, "        }\n");
        fcBufStr(&callSb, "    }\n");
    }
    fcBufFmt(&sb,
        "FIRE_MAYBE_UNUSED static Value rf_call(Value obj, Value name, Value args, OwnList* list, uint32_t caller) {\n"
        "    (void)list; (void)caller;\n"
        "    if (obj.kind != K_Class) { char t[120]; snprintf(t, sizeof t, \"Reflect.Call: expects an object, got: %%s.\", kindName(obj)); return rfFail(t); }\n"
        "    int argc = args.kind == K_Array ? (int)arrOf(args)->length : 0;\n"
        "    const Value* items = args.kind == K_Array ? arrItems(arrOf(args)) : NULL; (void)items;\n"
        "%s    { const RfClass* in = NULL; const char* s = rf_stripped(rf_classOf(asObj(obj)->cls), name, &in); if (s) return rf_strippedMember(in, s); }\n"
        "    char n[200], t[500]; strToUtf8(name, n, sizeof n);\n"
        "    snprintf(t, sizeof t, \"'%%s' has no method '%%s' with %%d parameter(s).\", className(asObj(obj)->cls), n, argc);\n"
        "    return rfFail(t);\n"
        "}\n", fcBufDone(g->c, &callSb));

    /* new: a constructor per class and number of arguments */
    for (i = 0; i < classes->count; i++) {
        const FcRtClass* rc = (const FcRtClass*)classes->items[i];
        FgClassInfo* info;
        FcList* byArgc;
        /* (without Reflect.New the constructors are not kept: rf_new is not called) */
        if (!g->reflNew || !(info = (FgClassInfo*)fcDictGet(g->classes, rc->name))) continue;
        /* (modules: the class of a library makes its objects in its own file) */
        if (Modular && !IsOwnClass(g, rc)) {
            const char* fn = NewFunctionName(g, ModIdOf(g, rc), rc);
            fgBufInsert0(&sb, fcFmt(g->c, "Value %s(int argc, const Value* items, OwnList* list, uint32_t caller);\n", fn));
            fcBufFmt(&newSb, "    if (strIs(cname, %s)) return %s(argc, items, list, caller);\n", CString(g, rc->name), fn);
            continue;
        }
        byArgc = NewCases(g, rc, info);
        if (byArgc->count == 0) continue;
        fcBufFmt(&newSb, "    if (strIs(cname, %s)) {\n", CString(g, rc->name));
        fcBufStr(&newSb, "        switch (argc) {\n");
        for (k = 0; k < byArgc->count; k++) fcBufFmt(&newSb, "%s\n", (const char*)byArgc->items[k]);
        fcBufStr(&newSb, "            default: break;\n");
        fcBufStr(&newSb, "        }\n");
        fcBufFmt(&newSb, "        { char t[300]; snprintf(t, sizeof t, \"'%%s' has no constructor with %%d parameter(s).\", %s, argc); return rfFail(t); }\n", CString(g, rc->name));
        fcBufStr(&newSb, "    }\n");
    }
    fcBufStr(&sb, "FIRE_MAYBE_UNUSED static Value accessDeniedNew(const char* message) { return rfFail(message); }\n");
    fcBufFmt(&sb,
        "FIRE_MAYBE_UNUSED static Value rf_new(Value cname, Value args, OwnList* list, uint32_t caller) {\n"
        "    (void)caller; (void)list;\n"
        "    int argc = args.kind == K_Array ? (int)arrOf(args)->length : 0;\n"
        "    const Value* items = args.kind == K_Array ? arrItems(arrOf(args)) : NULL; (void)items;\n"
        "%s%s    if (rf_isStripped(cname)) return rf_strippedClass(cname);\n"
        "    char n[200], t[500]; strToUtf8(cname, n, sizeof n);\n"
        "    if (!rf_find(cname)) snprintf(t, sizeof t, \"Unknown class '%%s'.\", n);\n"
        "    else snprintf(t, sizeof t, \"'%%s' has no constructor with %%d parameter(s).\", n, argc);\n"
        "    return rfFail(t);\n"
        "}\n", fcBufDone(g->c, &newSb),
        g->openWorld ? "    { const RfClass* pc = rf_find(cname); if (pc && pc->make) return pc->make(argc, items, list, caller); }   // (a class of a plugin)\n" : "");
    if (g->usesProbes) fcBufStr(&sb, ProbeCode(g, fieldNames));
    text = fcBufDone(g->c, &sb);
    if (!g->openWorld) {
        /* (no plugins: rf_classes lists the program's classes) */
        text = fgReplace(g->c, text, "    for (uint32_t i = 0; i < RF_PLUGIN_COUNT; i++) { Value s = strFromUtf8(((const RfClass*)g_pluginRf[i])->name, list); arrItems(names)[kRfClassCount + i] = s; retain(s); }", "");
        text = fgReplace(g->c, text, "allocArr(kRfClassCount + RF_PLUGIN_COUNT, list)", "allocArr(kRfClassCount, list)");
    }
    return text;
}

/* The setter of a name that looks for probes: the setter that is generated for the name becomes `sfo_`, `sf_` checks the flag of the object first. */
static const char* WrapProbeSetter(FgGen* g, const char* name, const char* text) {
    const char* m = Mangle(g, name);
    bool list = FieldHasList(g, name), caller = FieldHasCaller(g, name);
    const char* lp = fcFmt(g->c, "%s%s", list ? ", OwnList* list" : "", caller ? ", uint32_t caller" : "");
    const char* callArgs = fcFmt(g->c, "%s%s", list ? ", list" : "", caller ? ", caller" : "");
    FcBuf sb = {0};
    FcList* arms = fcListNew(g->c);
    int i;
    text = fgReplace(g->c, text, fcFmt(g->c, "%svoid sf_%s(", HelperInline, m), fcFmt(g->c, "static inline void sfo_%s(", m));
    fcBufStr(&sb, text);
    fcBufFmt(&sb, "static void sfa_%s(Value v, Value x, OwnList* list, uint32_t caller) { (void)list; (void)caller; sfo_%s(v, x%s); }\n", m, m, callArgs);
    /* the old value as the probe sees it: the field, else the getter, else undefined (no access check) */
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)g->classList->items[i];
        void* index;
        FcProto* getter;
        if (fcDictTry(FG_INDEX(cls->rc), name, &index)) fgArmAdd(g, arms, cls->id, fcFmt(g->c, "return %s;", FieldRead(g, cls->rc, FC_VALI(index) - 1, "o")));
        else if ((getter = FG_FIND(cls->rc, fcFmt(g->c, "get_%s", name), 0).proto) != NULL && !getter->isStatic)
            fgArmAdd(g, arms, cls->id, fcFmt(g->c, "{ Value r = %s(v); adopt(r, list); return r; }", GetFunc(g, getter, FK_Method, 0)->name));
    }
    fcBufFmt(&sb, "static Value gfp_%s(Value v, OwnList* list) {\n    Obj* o = asObj(v); (void)list;\n    switch (o->cls) {\n%s        default: return Undef();\n    }\n}\n", m, MergedCases(g, arms, NULL));
    fcBufFmt(&sb, "%svoid sf_%s(Value v, Value x%s) {\n", HelperInline, m, lp);
    fcBufFmt(&sb, "    if (FIRE_UNLIKELY(asObj(v)->h.flags & 2)) { probedSet(v, %s, x, sfa_%s, gfp_%s, %s); return; }\n", CString(g, name), m, m, caller ? "caller" : "0xFFFFFFFFu");
    fcBufFmt(&sb, "    sfo_%s(v, x%s);\n", m, callArgs);
    fcBufStr(&sb, "}\n");
    return fcBufDone(g->c, &sb);
}

/* `probe`/`silence`: the registration of probes and the natives (SPEC 8.14); the setters are wrapped by WrapProbeSetter. */
static const char* ProbeCode(FgGen* g, FcList* fieldNames) {
    FcBuf sb = {0};
    FcList* computedArms = fcListNew(g->c);
    FcDict* computedIds = fcDictNew(g->c, FC_KEY_STR);
    FcList* memberArms = fcListNew(g->c);
    FcDict* ownNames = fcDictNew(g->c, FC_KEY_PTR);   /* RuntimeClass -> list name ("" for none) */
    int i, k;
    (void)fieldNames;
    fcBufStr(&sb, "");
    /* the computed properties (a getter without a setter) of the classes: what their getter reads and the getter itself */
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)g->classList->items[i];
        FcDict* names = fcDictNew(g->c, FC_KEY_STR);
        FcList* sorted;
        FcBuf rows = {0};
        const FcRtClass* c;
        bool anyRow = false;
        for (c = cls->rc; c; c = c->base) FC_DICT_EACH(c->methods, e) if (fcStartsWith((const char*)e->key, "get_")) fcSetAdd(names, (const char*)e->key + 4);
        sorted = fgSortedKeys(g->c, names, true);
        fcBufStr(&rows, "");
        for (k = 0; k < sorted->count; k++) {
            const char* name = (const char*)sorted->items[k];
            FcList* sources = ComputedSources(g, cls->rc, name);
            FcProto* getter;
            FgFunc* getterFunc;
            FcBuf sourceList = {0};
            const char* key;
            void* known;
            int id, s;
            if (!sources || !(getter = FG_FIND(cls->rc, fcFmt(g->c, "get_%s", name), 0).proto) || getter->isStatic || !(getterFunc = (FgFunc*)fcDictGet(g->funcByProto, getter))) continue;
            fcBufStr(&sourceList, "");
            for (s = 0; s < sources->count; s++) fcBufFmt(&sourceList, "%s, ", CString(g, (const char*)sources->items[s]));
            key = fcFmt(g->c, "%s|%s", getterFunc->name, fcBufDone(g->c, &sourceList));
            if (fcDictTry(computedIds, key, &known)) id = FC_VALI(known);
            else {
                id = fcDictCount(computedIds);
                fcDictPut(computedIds, key, FC_IVAL(id));
                fcBufFmt(&sb, "static const char* const rfc_%d[] = {%sNULL};\n", id, key + strlen(getterFunc->name) + 1);
                fcBufFmt(&sb, "static Value rfg_%d(Value v, OwnList* list) { Value r = %s(v); adopt(r, list); return r; }\n", id, getterFunc->name);
            }
            fcBufFmt(&rows, " if (strcmp(member, %s) == 0) { *getter = rfg_%d; return rfc_%d; }", CString(g, name), id, id);
            anyRow = true;
        }
        if (anyRow) fgArmAdd(g, computedArms, cls->id, fcFmt(g->c, "{%s break; }", fcBufDone(g->c, &rows)));
    }
    /* the members a probe can be put on (fields, properties, methods); per class the names it declares itself, written once */
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)g->classList->items[i];
        FcBuf chain = {0};
        bool any = false;
        const FcRtClass* c;
        fcBufStr(&chain, "");
        for (c = cls->rc; c; c = c->base) {
            const char* list;
            void* known;
            if (fcDictTry(ownNames, c, &known)) list = (const char*)known;
            else {
                FcDict* names = fcDictNew(g->c, FC_KEY_STR);
                FcList* sorted;
                for (k = 0; k < c->fieldNames->count; k++) fcSetAdd(names, c->fieldNames->items[k]);
                FC_DICT_EACH(c->methods, e) {
                    const char* key = (const char*)e->key;
                    fcSetAdd(names, fcStartsWith(key, "get_") || fcStartsWith(key, "set_") ? key + 4 : key);
                }
                list = "";
                if (fcDictCount(names) > 0) {
                    FcBuf items = {0};
                    sorted = fgSortedKeys(g->c, names, true);
                    fcBufStr(&items, "");
                    for (k = 0; k < sorted->count; k++) fcBufFmt(&items, "%s, ", CString(g, (const char*)sorted->items[k]));
                    list = fcFmt(g->c, "rfn_%d", fcDictCount(ownNames));
                    fcBufFmt(&sb, "static const char* const %s[] = {%sNULL};\n", list, fcBufDone(g->c, &items));
                }
                fcDictPut(ownNames, c, (void*)list);
            }
            if (*list) { fcBufFmt(&chain, "%s, ", list); any = true; }
        }
        if (any) fgArmAdd(g, memberArms, cls->id, fcFmt(g->c, "{ static const char* const* const chain[] = {%sNULL}; return rf_inNames(chain, member); }", fcBufDone(g->c, &chain)));
    }
    fcBufStr(&sb, "FIRE_MAYBE_UNUSED static bool rf_inNames(const char* const* const* chain, const char* member) {\n    for (; *chain; chain++) for (const char* const* p = *chain; *p; p++) if (strcmp(*p, member) == 0) return true;\n    return false;\n}\n");
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static bool rf_probeHas(uint32_t cls, const char* member) {\n    switch (cls) {\n%s        default: return false;\n    }\n}\n", MergedCases(g, memberArms, NULL));
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static const char* const* rf_computed(uint32_t cls, const char* member, ProbeRawGet* getter) {\n    (void)member; (void)getter;\n    switch (cls) {\n%s        default: break;\n    }\n    return NULL;\n}\n",
        MergedCases(g, computedArms, NULL));
    fcBufStr(&sb,
        "FIRE_MAYBE_UNUSED static int64_t rf_probeAdd(Value target, const char* member, bool changing, Value handler, char* err, size_t cap) {\n"
        "    if (target.kind != K_Class) { snprintf(err, cap, \"'probe' expects an object, got: %s.\", kindName(target)); return -1; }\n"
        "    if (handler.kind != K_Lambda) { snprintf(err, cap, \"The handler of a probe must be a lambda, got: %s.\", kindName(handler)); return -1; }\n"
        "    if (lamOf(handler)->nparams > 4) { snprintf(err, cap, \"The handler of a probe may have at most 4 parameters (object, name, old, new), it has %u.\", (unsigned)lamOf(handler)->nparams); return -1; }\n"
        "    if (member) {\n"
        "        const char* cname = className(asObj(target)->cls);\n"
        "        if (!rf_probeHas(asObj(target)->cls, member)) { snprintf(err, cap, \"'%s' has no member '%s' - no probe can be registered there.\", cname, member); return -1; }\n"
        "        // a computed property: the probe watches what its getter reads and compares the value\n"
        "        ProbeRawGet getter = NULL;\n"
        "        const char* const* deps = rf_computed(asObj(target)->cls, member, &getter);\n"
        "        if (deps) {\n"
        "            if (changing) { snprintf(err, cap, \"'%s' of '%s' is a computed property (a getter without a setter): it can only be probed with 'changed'.\", member, cname); return -1; }\n"
        "            OwnList current = scopeList(poolMark());\n"
        "            Value now = getter(target, &current);\n"
        "            if (FIRE_UNLIKELY(g_unwind.active)) { leave(&current); return -2; }   // the getter threw: its exception is already unwinding, no probe\n"
        "            int64_t id = probeAddEntryFull(asObj(target), member, changing, handler, deps, getter, now);\n"
        "            leave(&current);\n"
        "            return id;\n"
        "        }\n"
        "    }\n"
        "    return probeAddEntry(asObj(target), member, changing, handler);\n"
        "}\n"
        "FIRE_MAYBE_UNUSED static bool rf_silenceCore(Value target, const char* member, char* err, size_t cap) {\n"
        "    if (target.kind != K_Class) { snprintf(err, cap, \"'silence' expects an object, got: %s.\", kindName(target)); return false; }\n"
        "    probeSilence(asObj(target), member);\n"
        "    return true;\n"
        "}\n"
        "FIRE_MAYBE_UNUSED static bool rf_silenceValue(Value v, char* err, size_t cap) {\n"
        "    if (v.kind == K_Class) return rf_silenceCore(v, NULL, err, cap);\n"
        "    if (v.kind == K_Int) { probeSilenceHandle(v.i); return true; }\n"
        "    snprintf(err, cap, \"'silence' expects a probe handle or an object, got: %s.\", kindName(v));\n"
        "    return false;\n"
        "}\n"
        "FIRE_MAYBE_UNUSED static Value rf_probe(Value obj, Value member, Value kind, Value handler, OwnList* list, uint32_t caller) {\n"
        "    (void)list; (void)caller;\n"
        "    char k[64];\n"
        "    strToUtf8(kind, k, sizeof k);\n"
        "    if (strcmp(k, \"changed\") != 0 && strcmp(k, \"changing\") != 0) {\n"
        "        char t[200]; snprintf(t, sizeof t, \"The kind of a probe is \\\"changed\\\" or \\\"changing\\\", got: \\\"%s\\\".\", k); return rfFail(t);\n"
        "    }\n"
        "    char m[200]; const char* mem = NULL;\n"
        "    if (member.kind == K_String) { strToUtf8(member, m, sizeof m); mem = m; }\n"
        "    char err[500];\n"
        "    int64_t id = rf_probeAdd(obj, mem, strcmp(k, \"changing\") == 0, handler, err, sizeof err);\n"
        "    if (id == -2) return Undef();   // the getter's own exception unwinds\n"
        "    if (id < 0) return rfFail(err);\n"
        "    return Int(id);\n"
        "}\n"
        "FIRE_MAYBE_UNUSED static Value rf_silence(Value obj, Value member) {\n"
        "    char m[200]; const char* mem = NULL;\n"
        "    if (member.kind == K_String) { strToUtf8(member, m, sizeof m); mem = m; }\n"
        "    char err[500];\n"
        "    if (!rf_silenceCore(obj, mem, err, sizeof err)) return rfFail(err);\n"
        "    return Undef();\n"
        "}\n"
        "FIRE_MAYBE_UNUSED static Value rf_silence_handle(Value handle) {\n"
        "    char err[500];\n"
        "    if (!rf_silenceValue(handle, err, sizeof err)) return rfFail(err);\n"
        "    return Undef();\n"
        "}\n");
    return fcBufDone(g->c, &sb);
}
