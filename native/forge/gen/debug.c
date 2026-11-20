/* native/forge/gen/debug.c - src/fire.Native/CppGenerator.Debug.cs (commit 808e9788): debug builds (mode `debug`, docs/spec/NATIVE_BACKEND.md
 * "Debug-Builds", docs/spec/NATIVE_DEBUG_PROTOCOL.md) - every fire function gets a frame on a shadow stack (fire_debug.h), pushed by its wrapper, and
 * the code updates its position where a line begins (`FIRE_DBG_L(source, line, scope state)`) and at every jump target. The scope state is a static
 * table per function: the levels of the scope chain with the names of their variables and their slots in the frame's array of variable addresses.
 * (DebugBuild, Wrapped and DebugAgent are macros of cppgenerator.c/cppgenerator2.c, Nested is in misc.c.) */

/* the debug information of one function, made anew by every generation of its chunk (FuncDebug) */
typedef struct FgDbgVar { const char* name; int dv; } FgDbgVar;
typedef struct FgDbgLevel { int parent, top; FcList* vars; /* FgDbgVar* */ } FgDbgLevel;
struct FgFuncDebug {
    FcDict* names;        /* "scope,slot" -> name */
    FcList* levels;       /* FgDbgLevel* */
    FcDict* levelIndex;   /* key -> index + 1 */
    FcList* dvNames;
    FcDict* dvIndex;      /* variable -> index + 1 */
};
static int fgDbgDv(FgGen* g, FgFuncDebug* d, const char* variable) {
    void* at;
    int index;
    if (fcDictTry(d->dvIndex, variable, &at)) return FC_VALI(at) - 1;
    index = d->dvNames->count;
    fcListAdd(g->c, d->dvNames, (void*)variable);
    fcDictPut(d->dvIndex, variable, FC_IVAL(index + 1));
    return index;
}
/* Chunk.DebugLocalNames[(depth, slot)]; DeclaredNameAt is the depth -1 */
static const char* fgDebugLocalName(FgGen* g, const FcChunk* chunk, int depth, int slot) {
    void* at;
    if (!fcDictTry(chunk->nameIndex, fcFmt(g->c, "%d,%d", depth, slot), &at)) return NULL;
    return chunk->names[FC_VALI(at) - 1].name;
}

/* Starts the debug information of a function (after pass 1: the states of the instructions are known): the names of its variables. */
static FgFuncDebug* DebugBegin(FgGen* g, FgFunc* f, FgCode code, FgFlow** entry) {
    FgFuncDebug* d = (FgFuncDebug*)fcAlloc(g->c, sizeof *d);
    int i;
    d->names = fcDictNew(g->c, FC_KEY_STR); d->levels = fcListNew(g->c); d->levelIndex = fcDictNew(g->c, FC_KEY_STR);
    d->dvNames = fcListNew(g->c); d->dvIndex = fcDictNew(g->c, FC_KEY_STR);
    fcDictPut(g->funcDebug, f, d);
    for (i = 0; i < code.n; i++) {
        FgFlow* st = entry[i];
        int id, declared;
        const char* name;
        if (!st || code.p[i].op != OP_DeclareLocal) continue;
        id = st->scopes[st->scopeCount - 1].id; declared = st->scopes[st->scopeCount - 1].declared;
        if (!(name = fgDebugLocalName(g, f->chunk, -1, code.p[i].addr))) continue;
        if (id == GlobalScope && f->kind == FK_Main) fgIMapPut(g->c, g->globalNames, declared, (void*)name);
        else fcDictPut(d->names, fcFmt(g->c, "%d,%d", id, declared), (void*)name);   /* (a fire body: its own variables of the top level are global slots behind the shared ones) */
    }
    return d;
}

/* The index of the innermost level of the scope chain of `st` (-1: only the global scope). */
static int DebugLevelOf(FgGen* g, FgFunc* f, FgFuncDebug* d, FgFlow* st, FcDict* locals) {
    int parent = -1, j, k;
    FcBuf key = {0};
    fcBufStr(&key, "");
    for (j = 0; j < st->scopeCount; j++) {
        int id = st->scopes[j].id, declared = st->scopes[j].declared, top, index;
        void* at;
        if (id == GlobalScope && !(j == 0 && f->kind == FK_FireBody)) continue;
        /* (VM: the top level of a function is the scope whose parent is the global one; 2: the variables of a fire body's top level) */
        top = id == GlobalScope ? 2 : id == FunctionScope || (j > 0 && st->scopes[j - 1].id == GlobalScope) ? 1 : 0;
        fcBufFmt(&key, "%d:%d;", id, declared);
        if (fcDictTry(d->levelIndex, fcStrN(g->c, key.p, key.len), &at)) index = FC_VALI(at) - 1;
        else {
            FgDbgLevel* level = (FgDbgLevel*)fcAlloc(g->c, sizeof *level);
            level->vars = fcListNew(g->c);
            for (k = id == GlobalScope ? f->globalSlotCount : 0; k < declared; k++) {
                const char* variable = id == GlobalScope ? fcFmt(g->c, "T%d", k) : id == FunctionScope ? fcFmt(g->c, "P%d", k) : fcFmt(g->c, "B%d_%d", id, k);
                const char* name = (const char*)fcDictGet(d->names, fcFmt(g->c, "%d,%d", id, k));
                const char* p;
                bool exists;
                FgDbgVar* v = (FgDbgVar*)fcAlloc(g->c, sizeof *v);
                if (!name) name = id == GlobalScope ? fcFmt(g->c, "(global %d)", k) : top == 1 && (p = fgDebugLocalName(g, f->chunk, 0, k)) != NULL ? p : fcFmt(g->c, "(local %d)", k);
                exists = fcDictHas(locals, variable) || (id == FunctionScope && k < fgParamLike(f));
                v->name = name; v->dv = exists ? fgDbgDv(g, d, variable) : -1;
                fcListAdd(g->c, level->vars, v);
            }
            level->parent = parent; level->top = top;
            index = d->levels->count;
            fcListAdd(g->c, d->levels, level);
            fcDictPut(d->levelIndex, fcStrN(g->c, key.p, key.len), FC_IVAL(index + 1));
        }
        parent = index;
    }
    fcBufFree(&key);
    return parent;
}

/* the source of a line (CppGenerator.Modules.cs DbgSrc: a library's own sources by its table - the modules are not ported) */
static const char* DbgSrc(FgGen* g, int src) { return fcFmt(g->c, "%d", src); }

/* Before instruction `i` (pass 2): the position where a line begins (or a jump lands), else the scope state where it changed. */
static void DebugBeforeInstruction(FgGen* g, FgFunc* f, FgFuncDebug* d, FcChunk* chunk, FgInstr* ins, int i, FgFlow* st, FgOut* o, bool isTarget, FcDict* locals) {
    int lv = DebugLevelOf(g, f, d, st, locals);
    int lo = 0, hi = chunk->lineCount - 1, found = -1, start, src, line;
    while (lo <= hi) {   /* (Chunk.GetLocationRange) */
        int mid = (lo + hi) >> 1;
        if (chunk->lines[mid].offset <= ins->addr) { found = mid; lo = mid + 1; }
        else hi = mid - 1;
    }
    start = found < 0 ? 0 : chunk->lines[found].offset;
    src = found < 0 ? 0 : chunk->lines[found].source;
    line = found < 0 ? 0 : chunk->lines[found].line;
    if (i == 0 || start == ins->addr) {
        fcBufFmt(&o->code, "    FIRE_DBG_L(%s, %d, %d);\n", DbgSrc(g, src), line, lv);
        if (line > 0 && !LibraryRole) fcSetAdd(g->dbgLines, fcFmt(g->c, "%d,%d", src, line));   /* (the lines with code: the profiler's table, kDbgProgram) */
    }
    else if (isTarget) fcBufFmt(&o->code, "    FIRE_DBG_J(%s, %d, %d);\n", DbgSrc(g, src), line, lv);   /* (a jump into the middle of a line: no new entry for the profiler) */
    else if (lv != o->dbgLv) fcBufFmt(&o->code, "    DF->lv = %d;\n", lv);
    o->dbgLv = lv;
}

/* After instruction (pass 2): a global variable of the top level was declared - the debugger shows it from now on. */
static void DebugAfterInstruction(FgGen* g, FgFunc* f, FgInstr* ins, FgFlow* st, FgOut* o) {
    if (f->kind != FK_Main || ins->op != OP_DeclareLocal || st->scopes[st->scopeCount - 1].id != GlobalScope) return;
    if (g->lineDir) fcBufFmt(&o->code, "%s\n", g->lineDir);
    fcBufFmt(&o->code, "    g_dbgGlobalCount = %d;\n", st->scopes[st->scopeCount - 1].declared + 1);
}

/* The start of the function proper: the addresses of its variables and its `this` go into its frame. */
static const char* DebugPrologue(FgGen* g, FgFunc* f, FgFuncDebug* d) {
    FcBuf sb = {0};
    int i;
    fcBufStr(&sb, "");
    if (d->dvNames->count > 0) {
        FcList* items = fcListNew(g->c);
        for (i = 0; i < d->dvNames->count; i++) fcListAdd(g->c, items, (void*)fcFmt(g->c, "&%s", (const char*)d->dvNames->items[i]));
        fcBufFmt(&sb, "    %s\n", CArray(g, "Value*", "dv_", items));
        fcBufStr(&sb, "    DF->vars = dv_;\n");
    }
    if (fgHasSelf(f) || f->kind == FK_Lambda) fcBufStr(&sb, "    DF->thisSlot = &self;\n");
    return fcBufDone(g->c, &sb);
}

static const char* DebugFunctionName(FgGen* g, FgFunc* f);
/* The static tables of a function: its variables, the levels of its scope states, its name. */
static const char* DebugTables(FgGen* g, FgFunc* f, FgFuncDebug* d) {
    FcBuf sb = {0};
    const char* n = f->name;
    FcList* offsets = fcListNew(g->c);
    FcList* all = fcListNew(g->c);
    int i, k;
    fcBufStr(&sb, "");
    for (i = 0; i < d->levels->count; i++) {
        FgDbgLevel* level = (FgDbgLevel*)d->levels->items[i];
        fcListAdd(g->c, offsets, FC_IVAL(all->count));
        for (k = 0; k < level->vars->count; k++) fcListAdd(g->c, all, level->vars->items[k]);
    }
    if (all->count > 0) {
        FcList* items = fcListNew(g->c);
        for (i = 0; i < all->count; i++) {
            FgDbgVar* v = (FgDbgVar*)all->items[i];
            fcListAdd(g->c, items, (void*)fcFmt(g->c, "{%s, %s}", CString(g, v->name), v->dv < 0 ? "0xFFFF" : fcFmt(g->c, "%d", v->dv)));
        }
        fcBufFmt(&sb, "static const DbgVar kDbgV_%s[] = {%s};\n", n, fgJoin(g->c, items, ", "));
    }
    if (d->levels->count > 0) {
        FcList* items = fcListNew(g->c);
        for (i = 0; i < d->levels->count; i++) {
            FgDbgLevel* l = (FgDbgLevel*)d->levels->items[i];
            fcListAdd(g->c, items, (void*)fcFmt(g->c, "{%d, %d, %d, %s}", l->parent, l->top, l->vars->count, l->vars->count > 0 ? fcFmt(g->c, "kDbgV_%s + %d", n, FC_VALI(offsets->items[i])) : "NULL"));
        }
        fcBufFmt(&sb, "static const DbgLevel kDbgLv_%s[] = {%s};\n", n, fgJoin(g->c, items, ", "));
    }
    fcBufFmt(&sb, "static const DbgFunc kDbgF_%s = {%s, %s};\n", n, CString(g, DebugFunctionName(g, f)), d->levels->count > 0 ? fcFmt(g->c, "kDbgLv_%s", n) : "NULL");
    return fcBufDone(g->c, &sb);
}

static bool IsHiddenFunc(FgGen* g, FgFunc* f);
/* The wrapper `fN` around the function proper `fN_b`: it keeps the frame. */
static const char* DebugWrapper(FgGen* g, FgFunc* f, const char* call) {
    const char* hidden = IsHiddenFunc(g, f) ? "1" : "0";
    return f->kind == FK_FireBody || f->kind == FK_Main
        ? fcFmt(g->c, "{ DbgFrame DF_; fire_dbgEnter(&DF_, &kDbgF_%s, %s); %s; fire_dbgLeave(&DF_); }", f->name, hidden, call)
        : fcFmt(g->c, "{ DbgFrame DF_; Value r_; fire_dbgEnter(&DF_, &kDbgF_%s, %s); r_ = %s; fire_dbgLeave(&DF_); return r_; }", f->name, hidden, call);
}

/* The wrapper of the catch blocks of a `try`: while they run, the frame of their function is the top of the shadow stack. */
static const char* DebugCatchWrapper(FgGen* g, FgFunc* f, const char* catchName) {
    return fcFmt(g->c, "static void %s(void* ctx_, uint32_t ci, Value e) { DbgFrame* saved_ = g_dbgTop; DbgFrame* own_ = ((FR_%s*)ctx_)->DF; int32_t src_ = own_->src, line_ = own_->line, lv_ = own_->lv; "
        "g_dbgTop = own_; %s_b(ctx_, ci, e); own_->src = src_; own_->line = line_; own_->lv = lv_; g_dbgTop = saved_; }", catchName, f->name, catchName);
}

/* The name of a function for the call stack. */
static const char* fgKindLower(FgFuncKind k) {
    switch (k) {
        case FK_Main: return "main"; case FK_Static: return "static"; case FK_Method: return "method"; case FK_Ctor: return "ctor";
        case FK_Init: return "init"; case FK_Dtor: return "dtor"; case FK_Lambda: return "lambda"; default: return "firebody";
    }
}
static const char* DebugFunctionName(FgGen* g, FgFunc* f) {
    const char* known;
    if (f->kind == FK_Main) return "<main>";
    if (f->kind == FK_FireBody) return "<fire>";
    if (f->kind == FK_Lambda) return "<lambda>";
    if (!g->protoNames) {
        g->protoNames = fcDictNew(g->c, FC_KEY_PTR);
        FC_DICT_EACH(g->program->program->classes, e) {
            FcRtClass* rc = (FcRtClass*)e->value;
            FcList* ctors = fgAllConstructors(g->c, rc);
            int k;
            FC_DICT_EACH(rc->methods, m) {
                FcList* protos = (FcList*)m->value;
                for (k = 0; k < protos->count; k++) if (!fcDictHas(g->protoNames, protos->items[k])) fcDictPut(g->protoNames, protos->items[k], (void*)fcFmt(g->c, "%s.%s", rc->name, (const char*)m->key));
            }
            for (k = 0; k < ctors->count; k++) if (!fcDictHas(g->protoNames, ctors->items[k])) fcDictPut(g->protoNames, ctors->items[k], (void*)fcFmt(g->c, "%s.construct", rc->name));
            if (rc->destructor && !fcDictHas(g->protoNames, rc->destructor)) fcDictPut(g->protoNames, rc->destructor, (void*)fcFmt(g->c, "%s.destruct", rc->name));
        }
    }
    if (f->proto && (known = (const char*)fcDictGet(g->protoNames, f->proto)) != NULL) return known;
    return f->chunk->ownerClass ? fcFmt(g->c, "%s.<%s>", f->chunk->ownerClass->name, fgKindLower(f->kind)) : fcFmt(g->c, "<%s>", fgKindLower(f->kind));
}

/* A function the VM always runs nested: destructors, the accessors of properties, operators, GetIndex/SetIndex, the default values of parameters and
 * the conversion functions of units. The debugger does not stop in it. */
static void fgHiddenVisitChunk(FgGen* g, FcDict* seen, const FcChunk* c);
static void fgHiddenVisit(FgGen* g, FcDict* seen, const FcProto* p) {
    int i;
    for (i = 0; i < p->paramDefaults->count; i++) if (p->paramDefaults->items[i]) fcSetAdd(g->hiddenProtos, p->paramDefaults->items[i]);
    fgHiddenVisitChunk(g, seen, p->chunk);
}
static void fgHiddenVisitChunk(FgGen* g, FcDict* seen, const FcChunk* c) {
    int i;
    if (!fcSetAdd(seen, c)) return;
    for (i = 0; i < c->functions->count; i++) fgHiddenVisit(g, seen, (const FcProto*)c->functions->items[i]);
}
static bool IsHiddenFunc(FgGen* g, FgFunc* f) {
    if (f->kind == FK_Dtor) return true;
    if (!f->proto) return false;
    if (!g->hiddenProtos) {
        FcDict* seen = fcDictNew(g->c, FC_KEY_PTR);
        g->hiddenProtos = fcDictNew(g->c, FC_KEY_PTR);
        fgHiddenVisitChunk(g, seen, g->program->program->topLevel);
        FC_DICT_EACH(g->program->program->classes, e) {
            FcRtClass* rc = (FcRtClass*)e->value;
            FcList* ctors = fgAllConstructors(g->c, rc);
            int k;
            FC_DICT_EACH(rc->methods, m) {
                const char* name = (const char*)m->key;
                FcList* protos = (FcList*)m->value;
                for (k = 0; k < protos->count; k++) {
                    if (fcStartsWith(name, "get_") || fcStartsWith(name, "set_") || fcStartsWith(name, "operator") || streq0(name, "GetIndex") || streq0(name, "SetIndex"))
                        fcSetAdd(g->hiddenProtos, protos->items[k]);
                    fgHiddenVisit(g, seen, (const FcProto*)protos->items[k]);
                }
            }
            for (k = 0; k < ctors->count; k++) fgHiddenVisit(g, seen, (const FcProto*)ctors->items[k]);
            if (rc->destructor) fgHiddenVisit(g, seen, rc->destructor);
        }
        FC_DICT_EACH(g->unitConverters, e) {
            FgUnitConv* cv = (FgUnitConv*)e->value;
            if (cv->to->proto) fcSetAdd(g->hiddenProtos, cv->to->proto);
            if (cv->from->proto) fcSetAdd(g->hiddenProtos, cv->from->proto);
        }
    }
    return fcDictHas(g->hiddenProtos, f->proto);
}

/* (CppGenerator.Packed.cs) The FirePlace of the field `index` of `rc` (the debugger). */
static const char* PlaceOf(FgGen* g, const FcRtClass* rc, int index) {
    int slot, half, t, unit;
    FieldPlace(g, rc, index, &slot, &half);
    t = RawTypeAt(g, rc, index);
    unit = t != 0 ? UnitOfType(t) : 0;
    return fcFmt(g->c, "{%d, %d, %s, %s}", slot > 0 ? slot : 0, half, fgRawPlaceOf(g, rc, index), unit == 0 ? "0" : UnitC(g, unit));
}

/* the lines of the profiler's table in the order of the SortedSet<(int, int)> */
static int fgCmpDbgLine(const void* a, const void* b) {
    int sa, la, sb, lb;
    sscanf(*(const char* const*)a, "%d,%d", &sa, &la);
    sscanf(*(const char* const*)b, "%d,%d", &sb, &lb);
    return sa != sb ? (sa < sb ? -1 : 1) : la < lb ? -1 : la > lb;
}

/* The tables of the program for the debugger (after the functions): sources, globals, classes, and the program itself. */
static const char* DebugProgramTables(FgGen* g) {
    FcBuf sb = {0};
    FcList* files = g->program->sourceFiles;
    FcList* hashes = g->program->sourceHashes;
    int nfiles = files ? files->count : 0, nhashes = hashes ? hashes->count : 0;
    int nsources = nfiles > nhashes ? nfiles : nhashes, nglobals = 0, i, k, n;
    int* globals;
    FcList* setters;
    FcList* lines;
    int entrySrc, entryLine;
    fcBufStr(&sb, "/* ---- the debugger (fire_debug.h): sources, globals, classes ---- */\n");
    if (nsources > 0) {
        FcList* items = fcListNew(g->c);
        for (i = 0; i < nsources; i++) {
            const char* file = i < nfiles ? (const char*)files->items[i] : NULL;
            const char* hash = i < nhashes ? (const char*)hashes->items[i] : NULL;
            fcListAdd(g->c, items, (void*)fcFmt(g->c, "{%s, %s}", file ? CString(g, file) : "NULL", hash ? CString(g, hash) : "NULL"));
        }
        fcBufFmt(&sb, "static const DbgSource kDbgSources[] = {%s};\n", fgJoin(g->c, items, ", "));
    }
    globals = fgISetItems(g->c, g->globals, true, &n);
    if (n > 0) nglobals = globals[n - 1] + 1;
    {
        int gn, *gkeys = fgISetItems(g->c, g->globalNames, true, &gn);
        if (gn > 0 && gkeys[gn - 1] + 1 > nglobals) nglobals = gkeys[gn - 1] + 1;
    }
    if (nglobals > 0) {
        FcList* items = fcListNew(g->c);
        for (i = 0; i < nglobals; i++) {
            void* name;
            fcListAdd(g->c, items, (void*)fcFmt(g->c, "{%s, %s}", fgIMapTry(g->globalNames, i, &name) ? CString(g, (const char*)name) : "NULL", fgISetHas(g->c, g->globals, i) ? fcFmt(g->c, "&G%d", i) : "NULL"));
        }
        fcBufFmt(&sb, "static const DbgGlobal kDbgGlobals[] = {%s};\n", fgJoin(g->c, items, ", "));
    }
    /* (a class whose fields are not slot by slot - packed or narrow ones: where each of them lives) */
    {
        FcList* rows = fcListNew(g->c);
        bool* placed = (bool*)fcAlloc(g->c, sizeof(bool) * (size_t)(g->classList->count + 1));
        for (i = 0; i < g->classList->count; i++) {
            FgClassInfo* cls = (FgClassInfo*)g->classList->items[i];
            FcList* fields = FG_NAMES(cls->rc);
            placed[i] = false;
            for (k = 0; k < fields->count; k++) { int slot, half; FieldPlace(g, cls->rc, k, &slot, &half); if (slot != k || half >= 0) placed[i] = true; }
            if (fields->count > 0) {
                FcList* names = fcListNew(g->c);
                for (k = 0; k < fields->count; k++) fcListAdd(g->c, names, (void*)CString(g, (const char*)fields->items[k]));
                fcBufFmt(&sb, "static const char* const kDbgFields%d[] = {%s};\n", cls->id, fgJoin(g->c, names, ", "));
            }
            if (fields->count > 0 && placed[i]) {
                FcList* places = fcListNew(g->c);
                for (k = 0; k < fields->count; k++) fcListAdd(g->c, places, (void*)PlaceOf(g, cls->rc, k));
                fcBufFmt(&sb, "static const FirePlace kDbgPlaces%d[] = {%s};\n", cls->id, fgJoin(g->c, places, ", "));
            }
        }
        for (i = 0; i < g->classList->count; i++) {
            FgClassInfo* c = (FgClassInfo*)g->classList->items[i];
            int count = FG_NAMES(c->rc)->count;
            fcListAdd(g->c, rows, (void*)fcFmt(g->c, "{%s, %d, %s, %s}", CString(g, c->rc->name), count, count > 0 ? fcFmt(g->c, "kDbgFields%d", c->id) : "NULL",
                count > 0 && placed[i] ? fcFmt(g->c, "kDbgPlaces%d", c->id) : "NULL"));
        }
        fcBufFmt(&sb, "static const DbgClass kDbgClasses[] = {%s};\n", rows->count ? fgJoin(g->c, rows, ", ") : "{\"\", 0, NULL, NULL}");
    }
    fcBufStr(&sb, "const DbgClass* fire_dbgClass(uint32_t cls) {\n");
    if (g->classList->count > 0) {
        fcBufStr(&sb, "    switch (cls) {\n");
        for (i = 0; i < g->classList->count; i++) fcBufFmt(&sb, "        case %d: return &kDbgClasses[%d];\n", ((FgClassInfo*)g->classList->items[i])->id, i);
        fcBufStr(&sb, "        default: return NULL;\n");
        fcBufStr(&sb, "    }\n");
    }
    else fcBufStr(&sb, "    (void)cls; return NULL;\n");
    fcBufStr(&sb, "}\n");
    /* `set` of a field: through the setter of the field (the declared kind is checked, probes hear it) as if the class itself assigned it */
    fcBufStr(&sb, "void fire_dbgSetField(Value o, uint32_t index, Value x) {\n");
    fcBufStr(&sb, "    OwnList scratch_ = scopeList(poolMark());\n");
    fcBufStr(&sb, "    (void)scratch_;\n");
    setters = fcListNew(g->c);
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)g->classList->items[i];
        FcList* fields = FG_NAMES(cls->rc);
        for (k = 0; k < fields->count; k++) if (fcDictHas(g->fieldNames, fields->items[k])) { fcListAdd(g->c, setters, cls); break; }
    }
    if (setters->count > 0) {
        fcBufStr(&sb, "    switch (objAt(o)->cls) {\n");
        for (i = 0; i < setters->count; i++) {
            FgClassInfo* cls = (FgClassInfo*)setters->items[i];
            FcList* fields = FG_NAMES(cls->rc);
            fcBufFmt(&sb, "        case %d: switch (index) {\n", cls->id);
            for (k = 0; k < fields->count; k++) {
                const char* field = (const char*)fields->items[k];
                if (!fcDictHas(g->fieldNames, field)) continue;
                fcBufFmt(&sb, "            case %d: %s(o, x%s%s); leave(&scratch_); return;\n", k, FieldFn(g, "sf", field), FieldHasList(g, field) ? ", &scratch_" : "",
                    FieldHasCaller(g, field) ? fcFmt(g->c, ", %du", cls->id) : "");
            }
            fcBufStr(&sb, "            default: break;\n");
            fcBufStr(&sb, "        } break;\n");
        }
        fcBufStr(&sb, "        default: break;\n");
        fcBufStr(&sb, "    }\n");
    }
    fcBufStr(&sb, "    setFieldAt(o, index, x);\n");
    fcBufStr(&sb, "}\n");
    fgLocation(g->program->program->topLevel, 0, &entrySrc, &entryLine);
    lines = fgKeys(g->c, g->dbgLines);
    if (lines->count > 1) qsort(lines->items, (size_t)lines->count, sizeof(void*), fgCmpDbgLine);
    if (lines->count > 0) fcBufFmt(&sb, "static const int32_t kDbgLines[] = {%s};\n", fgJoin(g->c, lines, ","));
    fcBufFmt(&sb, "static const DbgProgram kDbgProgram = {%s, %d, %d, %s, %d, %d, %d, %s, %s, %d};\n", nsources > 0 ? "kDbgSources" : "NULL", nsources, g->program->firstUserSource,
        nglobals > 0 ? "kDbgGlobals" : "NULL", nglobals, entrySrc, entryLine, CString(g, g->target->name), lines->count > 0 ? "kDbgLines" : "NULL", lines->count);
    return fcBufDone(g->c, &sb);
}
