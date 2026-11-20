/* native/forge/gen/moddispatch.c - src/fire.Native/CppGenerator.ModuleDispatch.cs (commit 661e55b3): every module serves its own classes (ROADMAP 9, phase B2)
 * - with modules each module, a library or the program, generates the dispatchers, field helpers, `is of`, operators, class scores and type arguments of
 * objects for the call sites of its own code, switching over its own classes only (a library numbers them from 0: `cls - fire_mb_M`); an object of a class
 * the module does not serve goes through the tables of the runtime (FireMembers in fire_rt.h). Dazu TableSlotOf aus CppGenerator.Packed.cs. In the order of
 * the C# file (the names of the helpers, CallFn ... OperatorWrapper, are in gen/cppgenerator.c). */

/* ModId: the module of this generator as a C name in exported symbols (`prog` for the program) */
static const char* ModId(FgGen* g) { return g->self ? g->self->id : "prog"; }
static const char* ModIdOf(FgGen* g, const FcRtClass* rc) { FgModuleSpec* m = ModuleOf(g, rc); return m ? m->id : "prog"; }

static bool ExtendsClass(FgGen* g, const FcRtClass* rc, const FgModuleSpec* module);
static FcList* ExtServed(FgGen* g);
/* The classes the helpers of this generator switch over: all (one file), with modules its own and those it extends. */
static FcList* Served(FgGen* g) {
    FcList* l;
    FcList* ext;
    int i;
    if (!Modular) return g->classList;
    l = fcListNew(g->c);
    for (i = 0; i < g->classList->count; i++) { FgClassInfo* c = (FgClassInfo*)g->classList->items[i]; if (IsOwnClass(g, c->rc)) fcListAdd(g->c, l, c); }
    ext = ExtServed(g);
    for (i = 0; i < ext->count; i++) fcListAdd(g->c, l, ext->items[i]);
    return l;
}

static FcList* ExtServed(FgGen* g) {
    FcList* classes;
    int next, i;
    if (g->extServed) return g->extServed;
    g->extServed = fcListNew(g->c);
    if (!Modular) return g->extServed;
    next = g->self ? g->self->classes->count : 0;
    classes = fgClassesByName(g);
    for (i = 0; i < classes->count; i++) {
        FcRtClass* rc = (FcRtClass*)classes->items[i];
        FgClassInfo* info;
        int id;
        void* fixedId;
        if (IsOwnClass(g, rc) || !ExtendsClass(g, rc, g->self)) continue;
        id = LibraryRole ? next++ : g->fixedIds && fcDictTry(g->fixedIds, rc->name, &fixedId) ? FC_VALI(fixedId) - 1 : -1;
        if (id < 0) continue;
        info = (FgClassInfo*)fcAlloc(g->c, sizeof *info);
        info->rc = rc; info->id = id; info->idText = LibraryRole ? ClassIdExpr(g, RegisterClass(g, rc->name)) : NULL;
        fcListAdd(g->c, g->extServed, info);
    }
    return g->extServed;
}

/* Does the module (NULL: the program) add members to the class of another module? */
static bool ExtendsClass(FgGen* g, const FcRtClass* rc, const FgModuleSpec* module) {
    FC_DICT_EACH(rc->methods, e) {
        FcList* list = (FcList*)e->value;
        int i;
        for (i = 0; i < list->count; i++) { FgModuleSpec* owner; if (ProtoKey(g, (FcProto*)list->items[i], &owner, NULL) && owner == module) return true; }
    }
    return false;
}

/* The number of this module for the class of an object: a library's own classes are 0, 1, ... (`cls - fire_mb_M`), the program's are their ids. */
static const char* OwnLocal(FgGen* g, const char* clsExpr) { return LibraryRole ? fcFmt(g->c, "(%s - %s)", clsExpr, ModuleBase(g, g->self)) : clsExpr; }
/* The test that a number of OwnLocal is a class of this module. */
static const char* OwnRangeTest(FgGen* g, const char* k) {
    if (LibraryRole) return fcFmt(g->c, "%s < %du", k, g->self->classes->count);
    return g->openWorld ? fcFmt(g->c, "(%s >= %du && %s < FIRE_PROG_END)", k, g->progBase, k) : fcFmt(g->c, "%s >= %du", k, g->progBase);
}

static int RequiredArgs(const FcProto* p) {
    int required = p->paramCount;
    while (required > 0 && required - 1 < p->paramDefaults->count && p->paramDefaults->items[required - 1]) required--;
    return required;
}

/* Does the dispatcher of `name(argc)` have a case for the class (the same test as Dispatcher)? */
static bool HasCase(FgGen* g, const FcRtClass* rc, const char* name, int argc) {
    if (FG_OVERLOADED(rc, name, argc)) {
        FgCands* set = fgCandidates(g->c, g->rtCands, rc, name, argc);
        int i;
        for (i = 0; i < set->count; i++) if (!set->protos[i]->isStatic) return true;
        return false;
    }
    {
        FcProto* p = FG_FIND(rc, name, argc).proto;
        return p && !p->isStatic;
    }
}

/* Can this module call `name(argc)` of the class directly: its code is in this module or one it needs? The program knows all. */
static bool VisibleMember(FgGen* g, const FcRtClass* rc, const char* name, int argc) {
    FcProto** protos;
    FcProto* one;
    int count, i;
    if (!LibraryRole) return true;
    if (FG_OVERLOADED(rc, name, argc)) { FgCands* set = fgCandidates(g->c, g->rtCands, rc, name, argc); protos = set->protos; count = set->count; }
    else { one = FG_FIND(rc, name, argc).proto; protos = &one; count = one ? 1 : 0; }
    for (i = 0; i < count; i++) {
        FgModuleSpec* owner;
        int k;
        bool dep = false;
        if (!ProtoKey(g, protos[i], &owner, NULL) || owner == g->self) continue;
        if (owner) for (k = 0; k < g->self->deps->count; k++) if (g->self->deps->items[k] == owner) dep = true;
        if (!dep) return false;
    }
    return true;
}

/* Is the member of this module (a library: its own code; the program: code of the program)? */
static bool OwnsProto(FgGen* g, const FcProto* p) { FgModuleSpec* owner; return ProtoKey(g, p, &owner, NULL) && owner == g->self; }

static bool WantsField(FgGen* g, const char* name) {
    return LibraryRole || g->tablesAll || fcDictHas(g->fieldNames, name) || fcDictHas(g->libFields, name) || fcDictHas(g->libFieldAddresses, name);
}

/* Does the table of the program list the method (some module calls it by name)? A library lists all its members. */
static bool TableWants(FgGen* g, const char* name, int argc) {
    return LibraryRole || g->openWorld || fgHasNameArgc(g, g->dispatchers, name, argc) || fgHasNameArgc(g, g->libDispatchers, name, argc)
        || (argc == 0 && fcStartsWith(name, "get_") && WantsField(g, name + 4))
        || (argc == 1 && fcStartsWith(name, "set_") && WantsField(g, name + 4));
}

/* a row of a table of methods: (name, argc, params) */
typedef struct FgTableMethod { const char* name; int argc; int params; } FgTableMethod;
static bool fgAnyDispatcherNamed(FgGen* g, FcDict* set, const char* name) {
    FC_DICT_EACH(set, e) if (streq0(((FgNameArgc*)e->value)->name, name)) return true;
    (void)g;
    return false;
}
static int fgCmpIntItemsAsc(const void* a, const void* b) { int x = FC_VALI(*(void* const*)a), y = FC_VALI(*(void* const*)b); return x < y ? -1 : x > y; }
/* the numbers of arguments the methods take (SortedSet<int>) */
static FcList* fgArgCounts(FgGen* g, FcList* mine) {
    FcList* counts = fcListNew(g->c);
    int i, argc;
    for (i = 0; i < mine->count; i++) {
        FcProto* p = (FcProto*)mine->items[i];
        for (argc = RequiredArgs(p); argc <= p->paramCount; argc++) {
            int k;
            bool seen = false;
            for (k = 0; k < counts->count; k++) if (FC_VALI(counts->items[k]) == argc) seen = true;
            if (!seen) fcListAdd(g->c, counts, FC_IVAL(argc));
        }
    }
    if (counts->count > 1) qsort(counts->items, (size_t)counts->count, sizeof(void*), fgCmpIntItemsAsc);
    return counts;
}
/* The methods of the table of a served class: those it declares in this module (not static), one row per number of arguments they take. */
static FcList* TableMethodsOf(FgGen* g, const FgClassInfo* cls) {
    FcList* result = fcListNew(g->c);
    const FcRtClass* rc = cls->rc;
    FC_DICT_EACH(rc->methods, e) {
        const char* name = (const char*)e->key;
        FcList* protos = (FcList*)e->value;
        FcList* mine = fcListNew(g->c);
        FcList* counts;
        int i;
        /* (a private method only when the module's own code calls it by that name: on an object of a class derived from its class in another module) */
        for (i = 0; i < protos->count; i++) {
            FcProto* p = (FcProto*)protos->items[i];
            if (!p->isStatic && OwnsProto(g, p) && (p->access != ACC_Private || !LibraryRole || KeepsPrivate(g, rc, name) || fgAnyDispatcherNamed(g, g->dispatchers, name))) fcListAdd(g->c, mine, p);
        }
        if (mine->count == 0) continue;
        counts = fgArgCounts(g, mine);
        for (i = 0; i < counts->count; i++) {
            int argc = FC_VALI(counts->items[i]);
            if (HasCase(g, rc, name, argc) && TableWants(g, name, argc)) {
                FgTableMethod* m = (FgTableMethod*)fcAlloc(g->c, sizeof *m);
                FcProto* found = FG_FIND(rc, name, argc).proto;
                m->name = name; m->argc = argc; m->params = found ? found->paramCount : argc;
                fcListAdd(g->c, result, m);
            }
        }
    }
    return result;
}

/* The fields of the table of a served class: those it declares itself (not static). */
static FcList* TableFieldsOf(FgGen* g, const FgClassInfo* cls) {
    FcList* l = fcListNew(g->c);
    if (!IsOwnClass(g, cls->rc)) return l;
    FC_DICT_EACH(cls->rc->ownFieldInfo, e)
        if (!((FcFieldInfo*)e->value)->isStatic && fcDictHas(FG_INDEX(cls->rc), e->key) && WantsField(g, (const char*)e->key)) fcListAdd(g->c, l, (void*)e->key);
    return l;
}

/* The names the dispatchers of this module serve: its call sites and the rows of its tables. */
static FcList* ModDispatchNames(FgGen* g) {
    FcDict* names = fcDictCopy(g->c, g->dispatchers);
    FcList* served = Served(g);
    int i, k;
    for (i = 0; i < served->count; i++) {
        FcList* rows = TableMethodsOf(g, (FgClassInfo*)served->items[i]);
        for (k = 0; k < rows->count; k++) { FgTableMethod* m = (FgTableMethod*)rows->items[k]; fgAddNameArgc(g, names, m->name, m->argc); }
    }
    return fgSortedNameArgc(g, names);
}
static FcDict* TableMethodNames(FgGen* g) {
    FcDict* names = fcDictNew(g->c, FC_KEY_STR);
    FcList* served = Served(g);
    int i, k;
    for (i = 0; i < served->count; i++) {
        FcList* rows = TableMethodsOf(g, (FgClassInfo*)served->items[i]);
        for (k = 0; k < rows->count; k++) { FgTableMethod* m = (FgTableMethod*)rows->items[k]; fgAddNameArgc(g, names, m->name, m->argc); }
    }
    return names;
}

/* The names the field functions of this module serve: its call sites and the fields of its tables. */
static FcList* ModFieldNames(FgGen* g) {
    FcDict* names = fcDictCopy(g->c, g->fieldNames);
    FcList* served = Served(g);
    int i, k;
    for (i = 0; i < served->count; i++) {
        FcList* fields = TableFieldsOf(g, (FgClassInfo*)served->items[i]);
        for (k = 0; k < fields->count; k++) fcSetAdd(names, fields->items[k]);
    }
    return fgSortedKeys(g->c, names, true);
}

static const char* TArgOfClassFunction(FgGen* g, const FcRtClass* rc, bool registerOnly);
/* What the helpers of a module pull in: the members of its served classes for its dispatchers and fields, the program's own base classes (they have tables),
 * the classes of the runtime's needs. */
static void RegisterServedTargets(FgGen* g) {
    FcList* names;
    FcList* fields;
    int i, k, m;
    if (!LibraryRole) {
        FcList* list = fcListCopy(g->c, g->classList);
        for (i = 0; i < list->count; i++) {
            const FcRtClass* rc;
            for (rc = ((FgClassInfo*)list->items[i])->rc->base; rc; rc = rc->base) if (IsOwnClass(g, rc)) RegisterClass(g, rc->name);
        }
    }
    /* (`a[i]` on an object, `foreach` over an object: the dispatchers of every module) */
    AddDispatcher(g, "GetIndex", 1);
    AddDispatcher(g, "SetIndex", 2);
    names = ModDispatchNames(g);
    for (i = 0; i < names->count; i++) {
        FgNameArgc* d = (FgNameArgc*)names->items[i];
        FcList* served = fcListCopy(g->c, Served(g));
        FcList* exts;
        const char* args;
        for (k = 0; k < served->count; k++) {
            FcList* methods = MethodsFor(g, ((FgClassInfo*)served->items[k])->rc, d->name, d->argc);
            bool overloaded = false;
            if (methods->count > 1) for (m = 0; m < methods->count; m++) if (!((FcProto*)methods->items[m])->isStatic) overloaded = true;
            for (m = 0; m < methods->count; m++) {
                FcProto* method = (FcProto*)methods->items[m];
                if (!method->isStatic) { GetFunc(g, method, FK_Method, 0); DefaultArgsPre(g, method, d->argc, "self", "list", &args); }
                else if (overloaded) { GetFunc(g, method, FK_Static, 0); DefaultArgsPre(g, method, d->argc, "Undef()", "list", &args); }
            }
        }
        exts = ExtensionMethods(g, d->name, d->argc);
        for (k = 0; k < exts->count; k++) {
            FcList* methods = MethodsFor(g, ((FgExtension*)exts->items[k])->rc, d->name, d->argc);
            for (m = 0; m < methods->count; m++) { GetFunc(g, (FcProto*)methods->items[m], FK_Method, 0); DefaultArgsPre(g, (FcProto*)methods->items[m], d->argc, "self", "list", &args); }
        }
        if (streq0(d->name, "GetEnumerator") && d->argc == 0 && fgClassOf(g, "ListEnumerator")) {
            FgClassInfo* enumerator = RegisterClass(g, "ListEnumerator");
            FcProto* ctor = fgFindConstructor(enumerator->rc, 2);
            if (ctor && ctor->paramCount == 2) GetFunc(g, ctor, FK_Ctor, 0);
        }
    }
    fields = ModFieldNames(g);
    for (i = 0; i < fields->count; i++) {
        const char* field = (const char*)fields->items[i];
        FcList* served = fcListCopy(g->c, Served(g));
        for (k = 0; k < served->count; k++) {
            FgClassInfo* cls = (FgClassInfo*)served->items[k];
            FcProto* getter;
            FcProto* setter;
            if (fcDictHas(FG_INDEX(cls->rc), field)) {
                const char* requiredUnit = fgFieldRequiredUnit(cls->rc, field);
                if (requiredUnit) {
                    UnitId(g, DemandUnit(g, requiredUnit));
                    Constant(g, fcMakeString(g->c, UnitDemandDisplay(g, requiredUnit)));
                }
                continue;
            }
            getter = FG_FIND(cls->rc, fcFmt(g->c, "get_%s", field), 0).proto;
            if (getter && !getter->isStatic) GetFunc(g, getter, FK_Method, 0);
            setter = FG_FIND(cls->rc, fcFmt(g->c, "set_%s", field), 1).proto;
            if (setter && !setter->isStatic) GetFunc(g, setter, FK_Method, 0);
        }
    }
    /* (a type argument of an object of a class with a generic class in its chain: the functions of the descriptors register theirs as they are made) */
    {
        FcList* served = fcListCopy(g->c, Served(g));
        for (k = 0; k < served->count; k++) { FgClassInfo* cls = (FgClassInfo*)served->items[k]; if (IsOwnClass(g, cls->rc)) TArgOfClassFunction(g, cls->rc, true); }
    }
}

/* ---- dispatchers ---- */
/* StaticImpl of the module's dispatcher: a static overload the values pick on an object */
static const char* fgDispStaticImplG(FgDispatchCtx* d, FcProto* proto) {
    FgGen* g = d->g;
    const char* defaults;
    const char* pre = DefaultArgsPre(g, proto, d->argc, "Undef()", "list", &defaults);
    const char* args = fcFmt(g->c, "%s%s", fgDispArgsFor(d, proto), defaults);
    while (*args == ',' || *args == ' ') args++;
    return fcFmt(g->c, "{ %sreturn %s(%s); }", pre, GetFunc(g, proto, FK_Static, 0)->name, args);
}
static const char* fgModDispOverloadCall(void* ctx, int c) {
    FgDispatchCtx* d = (FgDispatchCtx*)ctx;
    FcProto* p = d->protos[c];
    if (!d->extension && p->isStatic) return fgDispStaticImplG(d, p);
    return fgDispCallImpl(d, GetFunc(d->g, p, FK_Method, 0)->name, p, d->sample, d->extension, d->declaring[c]);
}
typedef struct FgModFuncIds { FgFunc* f; FcList* ids; } FgModFuncIds;
/* The dispatchers of `name(argc)` of a module: `disp_` (the served classes by their number, the others to `miss_`), `miss_` (the tables of the object's
 * class through the runtime, then what the VM does without the method), `call_` for the call sites and `mt_`, the row of the tables. */
static const char* ModDispatcher(FgGen* g, const char* name, int argc, bool entry, bool row) {
    FcList* served = Served(g);
    FcDict* byFunc = fcDictNew(g->c, FC_KEY_PTR);
    FcList* byFuncOrder = fcListNew(g->c);
    FcList* overloaded = fcListNew(g->c);
    FcList* extensions;
    FcList* builtins;
    bool enumeratorOfArray;
    FgDispatchCtx d;
    FcBuf sb = {0};
    FcBuf ps = {0};
    FcBuf args = {0};
    const char* disp;
    const char* miss;
    const char* ownId;
    const char* av;
    const char* avDecl;
    int i, k;
    for (i = 0; i < served->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)served->items[i];
        bool anyInstance = false;
        FgCands* set = NULL;
        if (!VisibleMember(g, cls->rc, name, argc)) continue;   /* (a member another module adds that this one does not know: through the runtime, `miss_`) */
        if (FG_OVERLOADED(cls->rc, name, argc)) {
            set = fgCandidates(g->c, g->rtCands, cls->rc, name, argc);
            for (k = 0; k < set->count; k++) if (!set->protos[k]->isStatic) anyInstance = true;
        }
        if (anyInstance) {
            FgDispGroup* group = NULL;
            for (k = 0; k < overloaded->count; k++) {
                FgDispGroup* o = (FgDispGroup*)overloaded->items[k];
                int m;
                bool same = o->set->count == set->count;
                for (m = 0; same && m < set->count; m++) if (o->set->protos[m] != set->protos[m] || o->set->declaring[m] != set->declaring[m]) same = false;
                if (same) { group = o; break; }
            }
            if (!group) { group = (FgDispGroup*)fcAlloc(g->c, sizeof *group); group->set = set; group->classes = fcListNew(g->c); fcListAdd(g->c, overloaded, group); }
            fcListAdd(g->c, group->classes, cls);
        } else {
            FcProto* method = FG_FIND(cls->rc, name, argc).proto;
            if (method && !method->isStatic) {
                FgFunc* f = GetFunc(g, method, FK_Method, 0);
                FgModFuncIds* ids = (FgModFuncIds*)fcDictGet(byFunc, f);
                if (!ids) { ids = (FgModFuncIds*)fcAlloc(g->c, sizeof *ids); ids->f = f; ids->ids = fcListNew(g->c); fcDictPut(byFunc, f, ids); fcListAdd(g->c, byFuncOrder, ids); }
                fcListAdd(g->c, ids->ids, FC_IVAL(cls->id));
            }
        }
    }
    extensions = ExtensionMethods(g, name, argc);
    enumeratorOfArray = (streq0(name, "GetEnumerator") && argc == 0 && fcDictHas(g->classes, "ListEnumerator")) || (streq0(name, "GetEnumerator") && argc == 0 && fcDictHas(g->foreignClasses, "ListEnumerator"));
    builtins = BuiltinMethods(g, name, argc);
    memset(&d, 0, sizeof d);
    d.g = g; d.name = name; d.argc = argc;
    /* a call site that passes addresses (of this module, or of another for a method with a `ref` parameter): an implementation without `ref` there gets the value */
    d.derefs = fcDictHas(g->refCallSites, fgNameArgcKey(g, name, argc));
    if (!d.derefs)
        FC_DICT_EACH(g->program->program->classes, e) {
            FcList* ms = MethodsFor(g, (FcRtClass*)e->value, name, argc);
            for (k = 0; k < ms->count && !d.derefs; k++) if (((FcProto*)ms->items[k])->refMask != 0) d.derefs = true;
            if (d.derefs) break;
        }
    d.restricted = RestrictedMethodName(g, name);
    fcBufStr(&ps, ""); fcBufStr(&args, "");
    for (i = 0; i < argc; i++) { fcBufFmt(&ps, ", Value a%d", i); fcBufFmt(&args, ", a%d", i); }
    disp = CallFn(g, name, argc, "disp_");
    miss = CallFn(g, name, argc, "miss_");

    /* miss_: an object of a class this module does not serve (or a served one without the method: another module may add it), or no object */
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static Value %s(Value self%s, OwnList* list, uint32_t caller) {\n", miss, ps.p);
    fcBufStr(&sb, "    (void)list; (void)caller;\n");
    av = argc == 0 ? "NULL" : "av_";
    if (argc == 0) avDecl = "";
    else {
        FcList* items = fcListNew(g->c);
        for (i = 0; i < argc; i++) fcListAdd(g->c, items, (void*)fcFmt(g->c, "a%d", i));
        avDecl = CArray(g, "Value", "av_", items);
    }
    /* (a base value: what plugins add to its type - `class extends string` in a plugin) */
    fcBufFmt(&sb, "    { uint32_t k_; const FireMethod* m_ = fireFindValueMethod(self, %s, %d, &k_); if (m_) { %sreturn m_->fn(k_, self, %s, list, caller); } }\n", CString(g, name), argc, avDecl, av);
    if ((ownId = OwnMethodId(name, argc)) != NULL) {
        int baseArgs = OwnMethodBaseArgs(ownId);
        const char* target = baseArgs == 1 ? "a0" : streq0(ownId, "OM_TakeGlobal") && argc == 2 ? "a1" : "Undef()";
        if (argc == baseArgs) fcBufFmt(&sb, "    ownMethod(%s, self, %s, list);\n", ownId, target);
        else fcBufFmt(&sb, "    ownMethodT(%s, self, %s, a%d, list, g_scopeLists, g_scopeCount);\n", ownId, target, baseArgs);
        fcBufStr(&sb, "    return Undef();\n");
    } else {
        /* (every object has ToString(), DECISIONS D14: `<object Name>` for one whose class has none) */
        if (streq0(name, "ToString") && argc == 0) fcBufStr(&sb, "    if (self.kind == K_Class) return retainedV(toStringValue(self, list));\n");
        fcBufFmt(&sb, "    return fireNoMethod(self, %s, %d);\n", CString(g, name), argc);
    }
    fcBufStr(&sb, "}\n");

    /* disp_: the served classes by their number */
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static Value %s(uint32_t k, Value self%s, OwnList* list, uint32_t caller) {\n", disp, ps.p);
    fcBufStr(&sb, "    (void)list; (void)caller;\n");
    fcBufStr(&sb, "    switch (k) {\n");
    for (i = 0; i < byFuncOrder->count; i++) {
        FgModFuncIds* ids = (FgModFuncIds*)byFuncOrder->items[i];
        const FcRtClass* sample = NULL;
        for (k = 0; k < ids->ids->count; k++) fcBufFmt(&sb, "        case %d:\n", FC_VALI(ids->ids->items[k]));
        for (k = 0; k < served->count; k++) if (((FgClassInfo*)served->items[k])->id == FC_VALI(ids->ids->items[0])) { sample = ((FgClassInfo*)served->items[k])->rc; break; }
        fcBufFmt(&sb, "            %s\n", fgDispCallImpl(&d, ids->f->name, ids->f->proto, sample, false, NULL));
    }
    for (i = 0; i < overloaded->count; i++) {
        FgDispGroup* o = (FgDispGroup*)overloaded->items[i];
        for (k = 0; k < o->classes->count; k++) fcBufFmt(&sb, "        case %d:\n", ((FgClassInfo*)o->classes->items[k])->id);
        fcBufStr(&sb, "        {\n");
        d.protos = o->set->protos; d.declaring = o->set->declaring; d.sample = ((FgClassInfo*)o->classes->items[0])->rc; d.extension = false;
        fcBufStr(&sb, OverloadChoice(g, o->set->protos, o->set->count, argc, "objAt(self)", fgModDispOverloadCall, &d, "            ", fgDispScoredArg));
        fcBufStr(&sb, "        }\n");
    }
    /* a class that has the method with other numbers of arguments: an ArgumentException (like the VM) */
    if (!OwnMethodId(name, argc))
        for (i = 0; i < served->count; i++) {
            FgClassInfo* c = (FgClassInfo*)served->items[i];
            if (fgHasMethodNamed(c->rc, name) && !FG_FIND(c->rc, name, argc).proto)
                fcBufFmt(&sb, "        case %d: return fireError(\"ArgumentException\", %s);\n", c->id, CString(g, DescribeMethodNotFound(g, c->rc, name, argc)));
        }
    fcBufStr(&sb, "        default: break;\n");
    fcBufStr(&sb, "    }\n");
    fcBufFmt(&sb, "    return %s(self%s, list, caller);\n", miss, args.p);
    fcBufStr(&sb, "}\n");

    /* call_ (direct_ when the threads wrap it): what the call sites of this module call */
    if (entry) {
        bool anyOwn = false;
        fcBufFmt(&sb, "%s\n", DispatcherSignature(g, name, argc, WrapDispatchers ? "direct_" : "call_"));
        fcBufStr(&sb, "{\n");
        fcBufStr(&sb, "    (void)list; (void)caller;\n");
        fcBufStr(&sb, "    switch (self.kind) {\n");
        for (i = 0; i < extensions->count; i++) {
            FgExtension* x = (FgExtension*)extensions->items[i];
            if (FG_OVERLOADED(x->rc, name, argc)) {
                FgCands* set = fgCandidates(g->c, g->rtCands, x->rc, name, argc);
                fcBufFmt(&sb, "        case %s: {\n", x->cpp);
                d.protos = set->protos; d.declaring = set->declaring; d.sample = x->rc; d.extension = true;
                fcBufStr(&sb, OverloadChoice(g, set->protos, set->count, argc, "NULL", fgModDispOverloadCall, &d, "            ", fgDispScoredArg));
                fcBufStr(&sb, "        }\n");
            }
            else fcBufFmt(&sb, "        case %s: %s\n", x->cpp, fgDispCallImpl(&d, GetFunc(g, x->proto, FK_Method, 0)->name, x->proto, x->rc, true, NULL));
        }
        for (i = 0; i < builtins->count; i++) {
            FgBuiltin* b = (FgBuiltin*)builtins->items[i];
            bool shadowed = false;
            for (k = 0; k < extensions->count; k++) if (streq0(((FgExtension*)extensions->items[k])->cpp, b->kind)) shadowed = true;
            if (!shadowed) fcBufFmt(&sb, "        case %s: return %s;\n", b->kind, b->expr);
        }
        if (enumeratorOfArray) {
            FgClassInfo* info = RegisterClass(g, "ListEnumerator");
            FgFunc* ctor = GetFunc(g, fgFindConstructor(info->rc, 2), FK_Ctor, 0);
            fcBufStr(&sb, "        case K_Array: case K_Buffer: case K_String: {\n");
            fcBufStr(&sb, "            bool ok;\n");
            fcBufStr(&sb, "            Value count = lengthOf(self, &ok);\n");
            fcBufFmt(&sb, "            Value e = %s;\n", NewObjectCall(g, info, "list"));
            fcBufFmt(&sb, "            %s\n", Nested(g, fcFmt(g->c, "%s(e, self, count);", ctor->name)));
            fcBufStr(&sb, "            return e;\n");
            fcBufStr(&sb, "        }\n");
        }
        for (i = 0; i < served->count; i++) if (IsOwnClass(g, ((FgClassInfo*)served->items[i])->rc)) anyOwn = true;
        if (anyOwn) {
            fcBufStr(&sb, "        case K_Class: {\n");
            fcBufStr(&sb, "            if (FIRE_UNLIKELY(!leafAlive(self))) return destroyedError(self);\n");
            fcBufFmt(&sb, "            uint32_t k_ = %s;\n", OwnLocal(g, "asObj(self)->cls"));
            fcBufFmt(&sb, "            if (FIRE_LIKELY(%s)) return %s(k_, self%s, list, caller);\n", OwnRangeTest(g, "k_"), disp, args.p);
            fcBufStr(&sb, "            break;\n");
            fcBufStr(&sb, "        }\n");
        }
        fcBufStr(&sb, "        default: break;\n");
        fcBufStr(&sb, "    }\n");
        fcBufFmt(&sb, "    return %s(self%s, list, caller);\n", miss, args.p);
        fcBufStr(&sb, "}\n");
        if (WrapDispatchers) fcBufStr(&sb, ActorWrapper(g, name, argc));
    }
    /* mt_: the row of the tables (the arguments as an array) */
    if (row) {
        FcBuf a = {0};
        fcBufStr(&a, "");
        for (i = 0; i < argc; i++) fcBufFmt(&a, ", a[%d]", i);
        fcBufFmt(&sb, "%sValue %s(uint32_t k, Value self, const Value* a, OwnList* list, uint32_t caller) { (void)a; return %s(k, self%s, list, caller); }\n",
            LibraryRole ? "" : "static ", CallFn(g, name, argc, "mt_"), disp, fcBufDone(g->c, &a));
    }
    fcBufFree(&ps); fcBufFree(&args);
    return fcBufDone(g->c, &sb);
}

/* ---- fields ---- */
/* The functions of a field name of a module: `fget_`/`fset_`/`fptr_` (the served classes by their number - the rows of the tables call them), and for the
 * module's call sites `gf_`/`sf_`/`fp_` (the base types, its own classes, everything else through the runtime). */
static const char* ModFieldHelpers(FgGen* g, const char* name) {
    const char* cname = CString(g, name);
    FcList* own = fcListNew(g->c);
    FcList* served = Served(g);
    FcList* getArms = fcListNew(g->c);
    FcList* setArms = fcListNew(g->c);
    FcList* ptrArms = fcListNew(g->c);
    FcBuf sb = {0};
    const char* rowStorage = LibraryRole ? "" : "static ";   /* (a library's: the program's tables of its classes name them) */
    const char* lengthCode;
    const char* ownTest;
    const char* plain;
    const char* body;
    bool isLength;
    int i;
    for (i = 0; i < served->count; i++) if (IsOwnClass(g, ((FgClassInfo*)served->items[i])->rc)) fcListAdd(g->c, own, served->items[i]);
    FieldArms(g, name, own, getArms, setArms, ptrArms);
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED %sValue %s(uint32_t k, Value v, OwnList* list, uint32_t caller) {\n    Obj* o = asObj(v); (void)o; (void)list; (void)caller;\n    switch (k) {\n%s        default: break;\n    }\n    return fireGetField(v, %s, list, caller);\n}\n",
        rowStorage, FieldFn(g, "fget", name), MergedCases(g, getArms, NULL), cname);
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED %svoid %s(uint32_t k, Value v, Value x, OwnList* list, uint32_t caller) {\n    Obj* o = asObj(v); (void)o; (void)list; (void)caller;\n    switch (k) {\n%s        default: break;\n    }\n    fireSetField(v, x, %s, list, caller);\n}\n",
        rowStorage, FieldFn(g, "fset", name), MergedCases(g, setArms, NULL), cname);
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED %sValue %s(uint32_t k, Value v) {\n    Obj* o = asObj(v); (void)o;\n    switch (k) {\n%s        default: break;\n    }\n    return fireFieldPtr(v, %s);\n}\n",
        rowStorage, FieldFn(g, "fptr", name), MergedCases(g, ptrArms, NULL), cname);
    if (!fcDictHas(g->fieldNames, name)) return fcBufDone(g->c, &sb);
    isLength = streq0(name, "Length") || streq0(name, "length");
    lengthCode = isLength ? "    { bool ok; Value n = lengthOf(v, &ok); if (ok) return n; }\n"
        : streq0(name, "littleEndian") ? "    if (v.kind == K_Buffer) return Bool(bufOf(v)->little != 0);\n" : "";
    ownTest = fcFmt(g->c, "    { uint32_t k_ = %s; if (FIRE_LIKELY(%s)) ", OwnLocal(g, "asObj(v)->cls"), OwnRangeTest(g, "k_"));
    fcBufFmt(&sb, "static inline Value %s(Value v, OwnList* list, uint32_t caller) {\n%s    if (FIRE_UNLIKELY(v.kind != K_Class)) return missingField(v, %s);\n    if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v);\n%sreturn %s(k_, v, list, caller); }\n    return fireGetField(v, %s, list, caller);\n}\n",
        FieldFn(g, "gf", name), lengthCode, cname, ownTest, FieldFn(g, "fget", name), cname);
    /* the setter: (probes) sf_ -> sfo_ -> (threads: one section) -> sfb_ */
    plain = g->usesProbes ? FieldFn(g, "sfo", name) : FieldFn(g, "sf", name);
    body = g->usesThreads ? FieldFn(g, "sfb", name) : plain;
    fcBufFmt(&sb, "static inline void %s(Value v, Value x, OwnList* list, uint32_t caller) {\n    if (FIRE_UNLIKELY(v.kind != K_Class)) { notObjectAssign(v); return; }\n    if (FIRE_UNLIKELY(!leafAlive(v))) { destroyedError(v); return; }\n%s{ %s(k_, v, x, list, caller); return; } }\n    fireSetField(v, x, %s, list, caller);\n}\n",
        body, ownTest, FieldFn(g, "fset", name), cname);
    if (g->usesThreads)
        fcBufFmt(&sb, "static inline void %s(Value v, Value x, OwnList* list, uint32_t caller) { bool ss_ = sectionScopeEnterV(v); if (FIRE_LIKELY(!g_isThread || !sharedValue(v) || toMainHolder(v, &x, %s))) %s(v, x, list, caller); sectionScopeExit(ss_); }\n",
            plain, CString(g, fcFmt(g->c, "The field '%s' of an object of the globals", name)), body);
    if (g->usesProbes) {
        FcList* arms = fcListNew(g->c);
        fcBufFmt(&sb, "static void %s(Value v, Value x, OwnList* list, uint32_t caller) { %s(v, x, list, caller); }\n", FieldFn(g, "sfa", name), plain);
        /* the old value as the probe sees it: the field, else the getter, else undefined (no access check) */
        for (i = 0; i < own->count; i++) {
            FgClassInfo* cls = (FgClassInfo*)own->items[i];
            int index = fgFieldIndexOf(g, cls->rc, name);
            FcProto* getter;
            if (index >= 0) fgArmAdd(g, arms, cls->id, fcFmt(g->c, "return %s;", FieldRead(g, cls->rc, index, "o")));
            else if ((getter = FG_FIND(cls->rc, fcFmt(g->c, "get_%s", name), 0).proto) != NULL && !getter->isStatic)
                fgArmAdd(g, arms, cls->id, fcFmt(g->c, "{ Value r = %s(v); adopt(r, list); return r; }", GetFunc(g, getter, FK_Method, 0)->name));
        }
        fcBufFmt(&sb, "static Value %s(Value v, OwnList* list) {\n    Obj* o = asObj(v); (void)list;\n    uint32_t k_ = %s;\n    if (!(%s)) return Undef();\n    switch (k_) {\n%s        default: return Undef();\n    }\n}\n",
            FieldFn(g, "gfp", name), OwnLocal(g, "o->cls"), OwnRangeTest(g, "k_"), MergedCases(g, arms, NULL));
        fcBufFmt(&sb, "static inline void %s(Value v, Value x, OwnList* list, uint32_t caller) {\n", FieldFn(g, "sf", name));
        fcBufFmt(&sb, "    if (v.kind == K_Class && FIRE_UNLIKELY(asObj(v)->h.flags & 2)) { probedSet(v, %s, x, %s, %s, caller); return; }\n", cname, FieldFn(g, "sfa", name), FieldFn(g, "gfp", name));
        fcBufFmt(&sb, "    %s(v, x, list, caller);\n", plain);
        fcBufStr(&sb, "}\n");
    }
    if (fgFieldAddressTaken(g, name))
        fcBufFmt(&sb, "static inline Value %s(Value v) {\n    if (FIRE_UNLIKELY(v.kind != K_Class)) return fireFieldPtr(v, %s);\n    if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v);\n%sreturn %s(k_, v); }\n    return fireFieldPtr(v, %s);\n}\n",
            FieldFn(g, "fp", name), cname, ownTest, FieldFn(g, "fptr", name), cname);
    return fcBufDone(g->c, &sb);
}

static const char* ModFieldPrototypes(FgGen* g, const char* name) {
    FcBuf sb = {0};
    if (!fcDictHas(g->fieldNames, name)) return "";
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static inline Value %s(Value v, OwnList* list, uint32_t caller);\n", FieldFn(g, "gf", name));
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static inline void %s(Value v, Value x, OwnList* list, uint32_t caller);\n", FieldFn(g, "sf", name));
    if (fgFieldAddressTaken(g, name)) fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static inline Value %s(Value v);\n", FieldFn(g, "fp", name));
    return fcBufDone(g->c, &sb);
}

/* ---- is of, operators, class scores, type arguments ---- */
static const char* ModIsOf(FgGen* g, const char* typeName) {
    FcList* served = Served(g);
    FcList* ids = fcListNew(g->c);
    FcBuf sb = {0};
    int enumIndex, i;
    for (i = 0; i < served->count; i++) { FgClassInfo* c = (FgClassInfo*)served->items[i]; if (IsOwnClass(g, c->rc) && ClassIs(c->rc, typeName)) fcListAdd(g->c, ids, FC_IVAL(c->id)); }
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static bool %s(Value v) {\n", IsOfFn(g, typeName));
    if ((enumIndex = EnumIndexNamed(g, typeName)) > 0) fcBufFmt(&sb, "    if (v.kind == K_Int) return v.reserved == %s;   // (the enum %s)\n", EnumC(g, enumIndex), typeName);
    if (streq0(typeName, "IEnumerable")) fcBufStr(&sb, "    if (v.kind == K_Array || v.kind == K_Buffer) return true;\n");
    fcBufStr(&sb, "    if (v.kind != K_Class) return false;\n");
    fcBufStr(&sb, "    if (FIRE_UNLIKELY(!leafAlive(v))) return false;   /* (destroyed: its class is not known any more - `is of` and `!` throw before) */\n");
    fcBufFmt(&sb, "    { uint32_t k_ = %s; if (FIRE_LIKELY(%s)) ", OwnLocal(g, "asObj(v)->cls"), OwnRangeTest(g, "k_"));
    if (ids->count == 0) fcBufStr(&sb, "return false;");
    else {
        fcBufStr(&sb, "switch (k_) { ");
        for (i = 0; i < ids->count; i++) fcBufFmt(&sb, "case %d: ", FC_VALI(ids->items[i]));
        fcBufStr(&sb, "return true; default: return false; }");
    }
    fcBufStr(&sb, " }\n");
    fcBufFmt(&sb, "    return fireIsOf(asObj(v)->cls, %s);\n", CString(g, typeName));
    fcBufStr(&sb, "}\n");
    return fcBufDone(g->c, &sb);
}

static const char* ModOperator(FgGen* g, const char* method) {
    FcList* served = Served(g);
    FcList* ids = fcListNew(g->c);
    FcBuf sb = {0};
    int i;
    for (i = 0; i < served->count; i++) { FgClassInfo* c = (FgClassInfo*)served->items[i]; if (IsOwnClass(g, c->rc) && FG_FIND(c->rc, method, 1).proto) fcListAdd(g->c, ids, FC_IVAL(c->id)); }
    fcBufFmt(&sb, "static Value %s(Value a, Value b, OwnList* list) {\n", OperatorWrapper(g, method));
    fcBufStr(&sb, "    if (a.kind == K_Class && leafAlive(a)) {\n");
    fcBufFmt(&sb, "        uint32_t k_ = %s;\n", OwnLocal(g, "asObj(a)->cls"));
    fcBufFmt(&sb, "        if (FIRE_LIKELY(%s)) {\n", OwnRangeTest(g, "k_"));
    if (ids->count > 0) {
        fcBufStr(&sb, "            switch (k_) { ");
        for (i = 0; i < ids->count; i++) fcBufFmt(&sb, "case %d: ", FC_VALI(ids->items[i]));
        fcBufFmt(&sb, "{ Value r = %s(a, b, list, 0xFFFFFFFFu); adopt(r, list); return r; } default: break; }\n", CallFn(g, method, 1, "call_"));
    }
    fcBufStr(&sb, "        } else {\n");
    fcBufFmt(&sb, "            uint32_t m_; const FireMethod* f_ = fireFindMethod(asObj(a)->cls, %s, 1, &m_);\n", CString(g, method));
    fcBufStr(&sb, "            if (f_) { Value r = f_->fn(m_, a, &b, list, 0xFFFFFFFFu); adopt(r, list); return r; }\n");
    fcBufStr(&sb, "        }\n");
    fcBufStr(&sb, "    }\n");
    fcBufFmt(&sb, "    return %s;\n", OperatorFallback(method));
    fcBufStr(&sb, "}\n");
    return fcBufDone(g->c, &sb);
}

static const char* ModClassScore(FgGen* g, const char* name) {
    FcList* served = Served(g);
    FcBuf cases = {0};
    FcBuf sb = {0};
    int i;
    fcBufStr(&cases, "");
    for (i = 0; i < served->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)served->items[i];
        const FcRtClass* rc;
        int distance = 0;
        if (!IsOwnClass(g, cls->rc)) continue;
        for (rc = cls->rc; rc; rc = rc->base, distance++)
            if (streq0(rc->name, name)) { fcBufFmt(&cases, "case %d: return %d; ", cls->id, FGOV_ClassExact - distance); break; }
    }
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static int %s(Value v) {\n", OvdFn(g, name));
    fcBufFmt(&sb, "    if (!leafAlive(v)) return %d;\n", FGOV_UndefinedScore);
    fcBufFmt(&sb, "    { uint32_t k_ = %s; if (FIRE_LIKELY(%s)) ", OwnLocal(g, "objAt(v)->cls"), OwnRangeTest(g, "k_"));
    if (cases.len == 0) fcBufFmt(&sb, "return %d;", FGOV_NoMatch);
    else fcBufFmt(&sb, "switch (k_) { %sdefault: return %d; }", cases.p, FGOV_NoMatch);
    fcBufStr(&sb, " }\n");
    fcBufFmt(&sb, "    return fireClassScore(objAt(v)->cls, %s, %d, %d);\n", CString(g, name), FGOV_ClassExact, FGOV_NoMatch);
    fcBufStr(&sb, "}\n");
    fcBufFree(&cases);
    return fcBufDone(g->c, &sb);
}

/* The type argument `k` of the generic class `cls` for an object of the class `ci` (its own, or given by a class of its chain), NULL: `ci` is not derived from it. */
static const char* TArgCaseExpr(FgGen* g, const FcRtClass* ci, const char* cls, int k, const char* obj) {
    FcList* args = NULL;   /* (NULL: the class's own type arguments; "$j" is its j-th) */
    const FcRtClass* rc = ci;
    const char* token;
    int i;
    if (!obj) obj = "o";
    while (rc && !streq0(rc->name, cls)) {
        FcList* given = rc->baseTypeArgs ? rc->baseTypeArgs : fcListNew(g->c);
        FcList* outer = args;
        args = fcListNew(g->c);
        for (i = 0; i < given->count; i++) {
            const char* t = (const char*)given->items[i];
            if (t[0] != '$' || !outer) fcListAdd(g->c, args, (void*)t);
            else { int j = atoi(t + 1); fcListAdd(g->c, args, j < outer->count ? outer->items[j] : (void*)""); }
        }
        rc = rc->base;
    }
    if (!rc) return NULL;
    token = !args ? fcFmt(g->c, "$%d", k) : k < args->count ? (const char*)args->items[k] : "";
    return !*token ? "NULL"
        : token[0] == '$' ? fcFmt(g->c, "(objTArgs(%s) ? objTArgs(%s)[%s] : NULL)", obj, obj, token + 1)
        : TArgRef(g, token);
}

static const char* ModTArgOf(FgGen* g, const char* cls, int k) {
    FcList* served = Served(g);
    FcList* ids = fcListNew(g->c);
    FcList* exprs = fcListNew(g->c);
    FcList* groups = fcListNew(g->c);
    FcBuf sb = {0};
    int i, m;
    for (i = 0; i < served->count; i++) {
        FgClassInfo* ci = (FgClassInfo*)served->items[i];
        const char* expr;
        if (!IsOwnClass(g, ci->rc)) continue;
        if ((expr = TArgCaseExpr(g, ci->rc, cls, k, NULL)) != NULL) { fcListAdd(g->c, ids, FC_IVAL(ci->id)); fcListAdd(g->c, exprs, (void*)expr); if (!fcListHasStr(groups, expr)) fcListAdd(g->c, groups, (void*)expr); }
    }
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static const TArg* %s(Obj* o) {\n", TArgOfFn(g, cls, k));
    fcBufStr(&sb, "    if (!o) return NULL;\n");
    fcBufFmt(&sb, "    { uint32_t k_ = %s; if (FIRE_LIKELY(%s)) switch (k_) {\n", OwnLocal(g, "o->cls"), OwnRangeTest(g, "k_"));
    for (i = 0; i < groups->count; i++) {
        fcBufStr(&sb, "        ");
        for (m = 0; m < ids->count; m++) if (streq0((const char*)exprs->items[m], (const char*)groups->items[i])) fcBufFmt(&sb, "case %d: ", FC_VALI(ids->items[m]));
        fcBufFmt(&sb, "return %s;\n", (const char*)groups->items[i]);
    }
    fcBufStr(&sb, "        default: return NULL;\n");
    fcBufStr(&sb, "    } }\n");
    fcBufFmt(&sb, "    return fireTArgOf(o, %s, %d);\n", CString(g, cls), k);
    fcBufStr(&sb, "}\n");
    return fcBufDone(g->c, &sb);
}

/* The number of type parameters of a class (0: not generic). */
static int TypeParamCountOf(const FcRtClass* rc) { return rc->decl && rc->decl->typeParams ? rc->decl->typeParams->count : 0; }

/* Does the class have a generic class in its chain (its descriptor has a function for the type arguments)? */
static bool HasGenericChain(const FcRtClass* rc) {
    const FcRtClass* c;
    for (c = rc; c; c = c->base) if (TypeParamCountOf(c) > 0) return true;
    return false;
}

static const char* TArgFunctionName(FgGen* g, const char* module, const FcRtClass* rc) { return fcFmt(g->c, "fire_targ_%s_%s", module, Mangle(g, rc->name)); }

/* The function of a class's descriptor for the type arguments of its generic classes (`fire_targ_M_C(o, "List", 0)`); NULL without a generic class in its chain. */
static const char* TArgOfClassFunction(FgGen* g, const FcRtClass* rc, bool registerOnly) {
    FcBuf sb = {0};
    const FcRtClass* c;
    int j;
    if (!HasGenericChain(rc)) return NULL;
    fcBufFmt(&sb, "%sconst TArg* %s(Obj* o, const char* cls, int k) {\n", LibraryRole ? "" : "static ", TArgFunctionName(g, ModId(g), rc));
    fcBufStr(&sb, "    (void)o; (void)k;\n");
    for (c = rc; c; c = c->base) {
        int count = TypeParamCountOf(c);
        if (count == 0) continue;
        fcBufFmt(&sb, "    if (strcmp(cls, %s) == 0) switch (k) { ", CString(g, c->name));
        for (j = 0; j < count; j++) { const char* e = TArgCaseExpr(g, rc, c->name, j, NULL); fcBufFmt(&sb, "case %d: return %s; ", j, e ? e : "NULL"); }
        fcBufStr(&sb, "default: return NULL; }\n");
    }
    fcBufStr(&sb, "    return NULL;\n");
    fcBufStr(&sb, "}\n");
    if (registerOnly) { fcBufFree(&sb); return NULL; }
    return fcBufDone(g->c, &sb);
}

/* ---- the tables ---- */
static const char* MembersName(FgGen* g, const char* module, const FcRtClass* rc) { return fcFmt(g->c, "fire_mem_%s_%s", module, Mangle(g, rc->name)); }

/* The C strings compare by their bytes (strcmp): the rows are sorted so. */
static int fgCmpTableMethod(const void* a, const void* b) {
    const FgTableMethod* x = *(const FgTableMethod* const*)a;
    const FgTableMethod* y = *(const FgTableMethod* const*)b;
    int c = strcmp(x->name, y->name);
    return c ? c : (x->argc < y->argc ? -1 : x->argc > y->argc);
}
static int fgCmpUtf8Items(const void* a, const void* b) { return strcmp(*(const char* const*)a, *(const char* const*)b); }
static void fgSortTableMethods(FcList* l) { if (l->count > 1) qsort(l->items, (size_t)l->count, sizeof(void*), fgCmpTableMethod); }

/* The slot of a field for the member tables of the modules (FireField.index): -1 for a packed one (its module's helpers read it) - CppGenerator.Packed.cs. */
static int TableSlotOf(FgGen* g, const FcRtClass* rc, const char* name) {
    int index = fgFieldIndexOf(g, rc, name), slot, half;
    if (index < 0 || RawTypeAt(g, rc, index) != 0) return -1;
    FieldPlace(g, rc, index, &slot, &half);
    return slot;
}

/* The tables of the members of the served classes (FireMembers, fire_rt.h) and the functions of their descriptors for type arguments. */
static const char* MemberTables(FgGen* g) {
    FcBuf sb = {0};
    FcList* served = Served(g);
    int i, k;
    fcBufStr(&sb, "");
    for (i = 0; i < served->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)served->items[i];
        FcList* methods = TableMethodsOf(g, cls);
        FcList* fields = TableFieldsOf(g, cls);
        const char* table = MembersName(g, ModId(g), cls->rc);
        const char* targ;
        fgSortTableMethods(methods);
        if (fields->count > 1) qsort(fields->items, (size_t)fields->count, sizeof(void*), fgCmpUtf8Items);
        if (methods->count > 0) {
            fcBufFmt(&sb, "static const FireMethod %s_m[] = {", table);
            for (k = 0; k < methods->count; k++) {
                FgTableMethod* x = (FgTableMethod*)methods->items[k];
                fcBufFmt(&sb, "%s{%s, %d, %d, %s}", k ? ", " : "", CString(g, x->name), x->argc, x->params, CallFn(g, x->name, x->argc, "mt_"));
            }
            fcBufStr(&sb, "};\n");
        }
        if (fields->count > 0) {
            fcBufFmt(&sb, "static const FireField %s_f[] = {", table);
            for (k = 0; k < fields->count; k++) {
                const char* f = (const char*)fields->items[k];
                fcBufFmt(&sb, "%s{%s, %d, %s, %s, %s}", k ? ", " : "", CString(g, f), TableSlotOf(g, cls->rc, f), FieldFn(g, "fget", f), FieldFn(g, "fset", f), FieldFn(g, "fptr", f));
            }
            fcBufStr(&sb, "};\n");
        }
        fcBufFmt(&sb, "%sconst FireMembers %s = {%du, %d, %s, %d, %s};   // %s\n", LibraryRole ? "" : "static ", table, cls->id, methods->count,
            methods->count > 0 ? fcFmt(g->c, "%s_m", table) : "NULL", fields->count, fields->count > 0 ? fcFmt(g->c, "%s_f", table) : "NULL", cls->rc->name);
        if (IsOwnClass(g, cls->rc) && (targ = TArgOfClassFunction(g, cls->rc, false)) != NULL) fcBufStr(&sb, targ);
    }
    return fcBufDone(g->c, &sb);
}

static const char* LibraryClassTable(FgGen* g, FgModuleSpec* m, const FcRtClass* c, FcBuf* decls);
static int fgCmpModuleIndex(const void* a, const void* b);
/* The program: the columns of a class descriptor for modules - the names of its chain and interfaces, the tables of its members (of every module that
 * declares some on a class of its chain) and the function for its type arguments. decls: what goes before the descriptors. */
static void fgBufInsert0(FcBuf* b, const char* text) {
    FcBuf n = {0};
    fcBufStr(&n, text);
    if (b->len) fcBufPut(&n, b->p, b->len);
    fcBufFree(b);
    *b = n;
}
static const char* ModularDescColumns(FgGen* g, const FcRtClass* rc, FcBuf* decls, int id) {
    FcList* isa = fcListNew(g->c);
    FcList* ifaces = fcListNew(g->c);
    FcList* tables = fcListNew(g->c);
    FcList* allModules = fcListNew(g->c);
    FcList* ext = ExtServed(g);
    const FcRtClass* c;
    const char* isaName = fcFmt(g->c, "kIsa%d", id);
    const char* ifaceName = "NULL";
    const char* tablesName = "NULL";
    const char* targ = "NULL";
    int i, k;
    FC_DICT_EACH(g->modules, e) fcListAdd(g->c, allModules, e->value);
    if (allModules->count > 1) qsort(allModules->items, (size_t)allModules->count, sizeof(void*), fgCmpModuleIndex);
    for (c = rc; c; c = c->base) {
        FgModuleSpec* owner = ModuleOf(g, c);
        fcListAdd(g->c, isa, (void*)c->name);
        for (i = 0; i < c->interfaces->count; i++) if (!fcListHasStr(ifaces, (const char*)c->interfaces->items[i])) fcListAdd(g->c, ifaces, c->interfaces->items[i]);
        /* the table of the class's own module (the program: only for its classes it serves), then of the modules that extend it */
        if (owner) fcListAdd(g->c, tables, (void*)LibraryClassTable(g, owner, c, decls));
        else if (fcDictHas(g->classes, c->name)) fcListAdd(g->c, tables, (void*)MembersName(g, "prog", c));
        for (i = 0; i < allModules->count; i++) {
            FgModuleSpec* m = (FgModuleSpec*)allModules->items[i];
            if (m != owner && ExtendsClass(g, c, m)) fcListAdd(g->c, tables, (void*)LibraryClassTable(g, m, c, decls));
        }
        if (owner && ExtendsClass(g, c, NULL)) {
            bool served = false;
            for (k = 0; k < ext->count; k++) if (((FgClassInfo*)ext->items[k])->rc == c) served = true;
            if (served) fcListAdd(g->c, tables, (void*)MembersName(g, "prog", c));
        }
    }
    fcBufFmt(decls, "static const char* const %s[] = {", isaName);
    for (i = 0; i < isa->count; i++) fcBufFmt(decls, "%s, ", CString(g, (const char*)isa->items[i]));
    fcBufStr(decls, "NULL};\n");
    if (ifaces->count > 0) {
        ifaceName = fcFmt(g->c, "kIfc%d", id);
        fcBufFmt(decls, "static const char* const %s[] = {", ifaceName);
        for (i = 0; i < ifaces->count; i++) fcBufFmt(decls, "%s, ", CString(g, (const char*)ifaces->items[i]));
        fcBufStr(decls, "NULL};\n");
    }
    if (tables->count > 0) {
        tablesName = fcFmt(g->c, "kMem%d", id);
        fcBufFmt(decls, "static const FireMembers* const %s[] = {", tablesName);
        for (i = 0; i < tables->count; i++) fcBufFmt(decls, "&%s, ", (const char*)tables->items[i]);
        fcBufStr(decls, "NULL};\n");
    }
    if (HasGenericChain(rc)) {
        targ = TArgFunctionName(g, ModIdOf(g, rc), rc);
        if (ModuleOf(g, rc) && fcSetAdd(g->externTables, targ)) fgBufInsert0(decls, fcFmt(g->c, "const TArg* %s(Obj* o, const char* cls, int k);\n", targ));
        else if (!ModuleOf(g, rc) && !fcDictHas(g->classes, rc->name)) targ = "NULL";
    }
    return fcFmt(g->c, ", %s, %s, %s, %s", isaName, ifaceName, tablesName, targ);
}

/* The program: its table of the members that module `m` declares on the class `c` of a library (its own class, or one it extends), with the rows some module
 * calls by name. */
typedef struct FgTableCtx { FgGen* g; FcList* others; } FgTableCtx;
static bool fgFieldWanted(FgTableCtx* t, const char* field) {
    FgGen* g = t->g;
    int i;
    if (g->tablesAll || fcDictHas(g->fieldNames, field)) return true;
    for (i = 0; i < t->others->count; i++) {
        FgModuleNeeds* n = ((FgModuleOutput*)t->others->items[i])->needs;
        if (fcDictHas(n->fields, field) || fcDictHas(n->fieldAddresses, field)) return true;
    }
    return false;
}
static bool fgTableWanted(FgTableCtx* t, const char* name, int argc) {
    FgGen* g = t->g;
    const char* field;
    int i;
    if (g->tablesAll || fgHasNameArgc(g, g->dispatchers, name, argc)) return true;
    for (i = 0; i < t->others->count; i++) {
        FgModuleNeeds* n = ((FgModuleOutput*)t->others->items[i])->needs;
        if (fgHasNameArgc(g, n->dispatchers, name, argc) || (fcDictHas(n->operators, name) && argc == 1)) return true;
    }
    field = strlen(name) > 4 ? name + 4 : "";
    return ((argc == 0 && fcStartsWith(name, "get_")) || (argc == 1 && fcStartsWith(name, "set_"))) && fgFieldWanted(t, field);
}
static const char* LibraryClassTable(FgGen* g, FgModuleSpec* m, const FcRtClass* c, FcBuf* decls) {
    const char* key = fcFmt(g->c, "%s\x01%s", m->id, c->name);
    const char* known = (const char*)fcDictGet(g->libraryClassTables, key);
    const char* px = m->idPx;
    const char* table;
    FgModuleNeeds* libNeeds = NULL;
    FgTableCtx t;
    FcList* methods;
    FcList* fields;
    FcBuf sb = {0};
    bool own, derived;
    int k, i;
    (void)decls;
    if (known) return known;
    /* (every member is wanted - Reflect.Call/Get by any name: the library's own table, it names them all) */
    if (g->tablesAll) {
        const char* full = MembersName(g, m->id, c);
        fcBufFmt(&g->libraryTablesCode, "extern const FireMembers %s;\n", full);
        fcDictPut(g->libraryClassTables, key, (void*)full);
        return full;
    }
    own = ModuleOf(g, c) == m;
    if (own) k = fgListIndexOfStr(m->classes, c->name);
    else {
        FcDict* names = fcDictNew(g->c, FC_KEY_STR);
        FcList* sorted;
        FcList* extended = fcListNew(g->c);
        for (i = 0; i < m->deps->count; i++) {
            FgModuleSpec* d = (FgModuleSpec*)m->deps->items[i];
            int j;
            for (j = 0; j < d->classes->count; j++) fcSetAdd(names, d->classes->items[j]);
        }
        sorted = fgSortedKeys(g->c, names, true);
        for (i = 0; i < sorted->count; i++) { FcRtClass* x = fgClassOf(g, (const char*)sorted->items[i]); if (x && ExtendsClass(g, x, m)) fcListAdd(g->c, extended, sorted->items[i]); }
        k = m->classes->count + fgListIndexOfStr(extended, c->name);
    }
    if (g->libraries) for (i = 0; i < g->libraries->count; i++) if (((FgModuleOutput*)g->libraries->items[i])->spec == m) { libNeeds = ((FgModuleOutput*)g->libraries->items[i])->needs; break; }
    /* (what the module calls itself on its own class goes through its own dispatcher - its names count only when a class of another module derives from it) */
    derived = !own;
    if (!derived)
        for (i = 0; i < g->classList->count; i++) {
            FgClassInfo* x = (FgClassInfo*)g->classList->items[i];
            if (ModuleOf(g, x->rc) != m && x->rc != c && ClassIs(x->rc, c->name)) { derived = true; break; }
        }
    t.g = g;
    t.others = fcListNew(g->c);
    if (g->libraries) for (i = 0; i < g->libraries->count; i++) { FgModuleOutput* l = (FgModuleOutput*)g->libraries->items[i]; if (l->spec != m || derived) fcListAdd(g->c, t.others, l); }
    methods = fcListNew(g->c);
    FC_DICT_EACH(c->methods, e) {
        const char* name = (const char*)e->key;
        FcList* protos = (FcList*)e->value;
        FcList* mine = fcListNew(g->c);
        FcList* counts;
        int j;
        for (j = 0; j < protos->count; j++) {
            FcProto* p = (FcProto*)protos->items[j];
            FgModuleSpec* o;
            if (!p->isStatic && ProtoKey(g, p, &o, NULL) && o == m
                && (p->access != ACC_Private || KeepsPrivate(g, c, name) || (libNeeds && fgAnyDispatcherNamed(g, libNeeds->dispatchers, name)))) fcListAdd(g->c, mine, p);
        }
        if (mine->count == 0) continue;
        counts = fgArgCounts(g, mine);
        for (j = 0; j < counts->count; j++) {
            int argc = FC_VALI(counts->items[j]);
            if (HasCase(g, c, name, argc) && fgTableWanted(&t, name, argc)) {
                FgTableMethod* tm = (FgTableMethod*)fcAlloc(g->c, sizeof *tm);
                FcProto* found = FG_FIND(c, name, argc).proto;
                tm->name = name; tm->argc = argc; tm->params = found ? found->paramCount : argc;
                fcListAdd(g->c, methods, tm);
            }
        }
    }
    fgSortTableMethods(methods);
    fields = fcListNew(g->c);
    if (own) FC_DICT_EACH(c->ownFieldInfo, e) if (!((FcFieldInfo*)e->value)->isStatic && fcDictHas(FG_INDEX(c), e->key) && fgFieldWanted(&t, (const char*)e->key)) fcListAdd(g->c, fields, (void*)e->key);
    if (fields->count > 1) qsort(fields->items, (size_t)fields->count, sizeof(void*), fgCmpUtf8Items);
    table = fcFmt(g->c, "kPm_%s_%s", m->id, Mangle(g, c->name));
    fcBufStr(&sb, "");
    for (i = 0; i < methods->count; i++) {
        FgTableMethod* x = (FgTableMethod*)methods->items[i];
        const char* fn = fcFmt(g->c, "%smt_%s_%d", px, Mangle(g, x->name), x->argc);
        if (fcSetAdd(g->externTables, fn)) fcBufFmt(&sb, "Value %s(uint32_t k, Value self, const Value* a, OwnList* list, uint32_t caller);\n", fn);
    }
    for (i = 0; i < fields->count; i++) {
        const char* f = Mangle(g, (const char*)fields->items[i]);
        if (fcSetAdd(g->externTables, fcFmt(g->c, "%sfget_%s", px, f)))
            fcBufFmt(&sb, "Value %sfget_%s(uint32_t k, Value v, OwnList* list, uint32_t caller);\nvoid %sfset_%s(uint32_t k, Value v, Value x, OwnList* list, uint32_t caller);\nValue %sfptr_%s(uint32_t k, Value v);\n",
                px, f, px, f, px, f);
    }
    if (methods->count > 0) {
        fcBufFmt(&sb, "static const FireMethod %s_m[] = {", table);
        for (i = 0; i < methods->count; i++) {
            FgTableMethod* x = (FgTableMethod*)methods->items[i];
            fcBufFmt(&sb, "%s{%s, %d, %d, %smt_%s_%d}", i ? ", " : "", CString(g, x->name), x->argc, x->params, px, Mangle(g, x->name), x->argc);
        }
        fcBufStr(&sb, "};\n");
    }
    if (fields->count > 0) {
        fcBufFmt(&sb, "static const FireField %s_f[] = {", table);
        for (i = 0; i < fields->count; i++) {
            const char* f = (const char*)fields->items[i];
            const char* mf = Mangle(g, f);
            fcBufFmt(&sb, "%s{%s, %d, %sfget_%s, %sfset_%s, %sfptr_%s}", i ? ", " : "", CString(g, f), TableSlotOf(g, c, f), px, mf, px, mf, px, mf);
        }
        fcBufStr(&sb, "};\n");
    }
    fcBufFmt(&sb, "static const FireMembers %s = {%du, %d, %s, %d, %s};   // %s (%s)\n", table, k, methods->count, methods->count > 0 ? fcFmt(g->c, "%s_m", table) : "NULL",
        fields->count, fields->count > 0 ? fcFmt(g->c, "%s_f", table) : "NULL", c->name, m->name);
    fcBufStr(&g->libraryTablesCode, fcBufDone(g->c, &sb));
    fcDictPut(g->libraryClassTables, key, (void*)table);
    return table;
}

/* The helpers of a module (the program's and every library's): prototypes, then the code - after the functions' prototypes. */
static const char* ModularHelpers(FgGen* g) {
    FcList* fieldNames = ModFieldNames(g);
    FcList* dispatchNames = ModDispatchNames(g);
    FcDict* rowNames = TableMethodNames(g);
    FcList* fieldCode = fcListNew(g->c);
    FcList* dispatcherCode = fcListNew(g->c);
    FcList* selectorCode = fcListNew(g->c);
    FcList* idispatchers;
    FcList* isOfTypes;
    FcList* operators;
    const char* tables;
    const char* targTables;
    const char* ovTests;
    FcBuf sb = {0};
    int i, k;
    /* the field helpers first: their checks register the `is of` tests and type arguments they use */
    for (i = 0; i < fieldNames->count; i++) fcListAdd(g->c, fieldCode, (void*)ModFieldHelpers(g, (const char*)fieldNames->items[i]));
    for (i = 0; i < fieldNames->count; i++) {
        FcList* served = Served(g);
        for (k = 0; k < served->count; k++) {
            const char* requiredType = fgFieldRequiredType(((FgClassInfo*)served->items[k])->rc, (const char*)fieldNames->items[i]);
            if (requiredType) { FcList* names = IsOfNames(g, requiredType); int n; for (n = 0; n < names->count; n++) fcSetAdd(g->isOfTypes, names->items[n]); }
        }
    }
    for (i = 0; i < dispatchNames->count; i++) {
        FgNameArgc* x = (FgNameArgc*)dispatchNames->items[i];
        fcListAdd(g->c, dispatcherCode, (void*)ModDispatcher(g, x->name, x->argc, fgHasNameArgc(g, g->dispatchers, x->name, x->argc), fgHasNameArgc(g, rowNames, x->name, x->argc)));
    }
    idispatchers = fgSortedIDispatch(g);
    for (i = 0; i < idispatchers->count; i++) { FgIDisp* x = (FgIDisp*)idispatchers->items[i]; fcListAdd(g->c, dispatcherCode, (void*)InterfaceDispatcher(g, x->iface, x->name, x->argc)); }
    FC_DICT_EACH(g->overloadSelectors, e) fcListAdd(g->c, selectorCode, (void*)SelectorFunction(g, (FgSelector*)e->value));
    tables = MemberTables(g);
    targTables = TArgTables(g, true);
    ovTests = OverloadClassFunctions(g, true);
    isOfTypes = fgSortedKeys(g->c, g->isOfTypes, false);
    for (i = 0; i < isOfTypes->count; i++) fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static bool %s(Value v);\n", IsOfFn(g, (const char*)isOfTypes->items[i]));
    fcBufStr(&sb, targTables);
    for (i = 0; i < fieldNames->count; i++) fcBufStr(&sb, ModFieldPrototypes(g, (const char*)fieldNames->items[i]));
    for (i = 0; i < dispatchNames->count; i++) {
        FgNameArgc* x = (FgNameArgc*)dispatchNames->items[i];
        FcBuf ps = {0};
        fcBufStr(&ps, "");
        for (k = 0; k < x->argc; k++) fcBufFmt(&ps, ", Value a%d", k);
        fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static Value %s(uint32_t k, Value self%s, OwnList* list, uint32_t caller);\n", CallFn(g, x->name, x->argc, "disp_"), ps.p);
        fcBufFree(&ps);
        if (!fgHasNameArgc(g, g->dispatchers, x->name, x->argc)) continue;
        fcBufFmt(&sb, "%s;\n", DispatcherSignature(g, x->name, x->argc, "call_"));
        if (WrapDispatchers) fcBufFmt(&sb, "%s;\n", DispatcherSignature(g, x->name, x->argc, "direct_"));
        if (WrapDispatchers) fcBufFmt(&sb, "static Value %s(Value self, const Value* a, OwnList* list);\n", CallFn(g, x->name, x->argc, "msg_"));
    }
    for (i = 0; i < idispatchers->count; i++) { FgIDisp* x = (FgIDisp*)idispatchers->items[i]; fcBufFmt(&sb, "%s;\n", InterfaceDispatcherSignature(g, x->iface, x->name, x->argc)); }
    operators = fgSortedKeys(g->c, g->operators, false);
    for (i = 0; i < operators->count; i++) fcBufFmt(&sb, "static Value %s(Value a, Value b, OwnList* list);\n", OperatorWrapper(g, (const char*)operators->items[i]));
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static inline Value %saget_g(Value a, Value i, OwnList* list);\n", Px);
    fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static inline Value %saset_g(Value a, Value i, Value v, OwnList* list);\n", Px);
    FC_DICT_EACH(g->overloadSelectors, e) fcBufFmt(&sb, "%s;\n", SelectorSignature(g, (FgSelector*)e->value));
    fcBufStr(&sb, ovTests);
    for (i = 0; i < fieldCode->count; i++) fcBufFmt(&sb, "%s\n", (const char*)fieldCode->items[i]);
    for (i = 0; i < dispatcherCode->count; i++) fcBufFmt(&sb, "%s\n", (const char*)dispatcherCode->items[i]);
    for (i = 0; i < selectorCode->count; i++) fcBufFmt(&sb, "%s\n", (const char*)selectorCode->items[i]);
    if (!LibraryRole && g->usesTakeEnumerate) fcBufStr(&sb, EnumerateItemsFunction(g));
    for (i = 0; i < operators->count; i++) fcBufStr(&sb, ModOperator(g, (const char*)operators->items[i]));
    for (i = 0; i < isOfTypes->count; i++) fcBufStr(&sb, ModIsOf(g, (const char*)isOfTypes->items[i]));
    g->isOfWritten = fcDictNew(g->c, FC_KEY_STR);
    for (i = 0; i < isOfTypes->count; i++) fcSetAdd(g->isOfWritten, isOfTypes->items[i]);
    fcBufStr(&sb, tables);
    fcBufFmt(&sb, "%s\n", IndexHelpers(g));
    return fcBufDone(g->c, &sb);
}
