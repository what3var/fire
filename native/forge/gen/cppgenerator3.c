/* native/forge/gen/cppgenerator3.c - src/fire.Native/CppGenerator.cs (commit 808e9788), part 3: the flow state (Flow, Out, Region), the switches of the
 * program (UseExceptions, ...), the code of a chunk (GenerateChunkCore: the two passes, the prologue, the frame of a function with catch blocks),
 * `#line`, the regions of `try`, leaving scopes, the exits, `return` through `finally`, the landing sites, and Step around StepCore (the C# file's
 * lines 3513-4679, in its order). */

/* ---- Flow ---- */
static FgFlow* fgFlowNew(FgGen* g) { return (FgFlow*)fbAlloc(&g->flowArena, sizeof(FgFlow)); }
static void* fgFlowDup(FgGen* g, const void* p, size_t n) { void* q; if (!n) return NULL; q = fbAlloc(&g->flowArena, n); memcpy(q, p, n); return q; }
static FgFlow* fgFlowClone(FgGen* g, const FgFlow* o) {
    FgFlow* f = (FgFlow*)fbAlloc(&g->flowArena, sizeof(FgFlow));
    *f = *o;
    f->scopes = (FgScope*)fgFlowDup(g, o->scopes, sizeof(FgScope) * (size_t)o->scopeCount); f->scopeCap = o->scopeCount;
    f->handlers = (FgHEntry*)fgFlowDup(g, o->handlers, sizeof(FgHEntry) * (size_t)o->handlerCount); f->handlerCap = o->handlerCount;
    f->fins = (int*)fgFlowDup(g, o->fins, sizeof(int) * (size_t)o->finCount); f->finCap = o->finCount;
    return f;
}
static void fgFlowPushScope(FgGen* g, FgFlow* f, int id, int declared) {
    if (f->scopeCount == f->scopeCap) { int cap = f->scopeCap ? f->scopeCap * 2 : 4; FgScope* n = (FgScope*)fbAlloc(&g->flowArena, sizeof(FgScope) * (size_t)cap); if (f->scopeCount) memcpy(n, f->scopes, sizeof(FgScope) * (size_t)f->scopeCount); f->scopes = n; f->scopeCap = cap; }
    f->scopes[f->scopeCount].id = id; f->scopes[f->scopeCount].declared = declared; f->scopeCount++;
}
static void fgFlowPushHandler(FgGen* g, FgFlow* f, FgRegion* r, bool finOnly) {
    if (f->handlerCount == f->handlerCap) { int cap = f->handlerCap ? f->handlerCap * 2 : 4; FgHEntry* n = (FgHEntry*)fbAlloc(&g->flowArena, sizeof(FgHEntry) * (size_t)cap); if (f->handlerCount) memcpy(n, f->handlers, sizeof(FgHEntry) * (size_t)f->handlerCount); f->handlers = n; f->handlerCap = cap; }
    f->handlers[f->handlerCount].region = r; f->handlers[f->handlerCount].finOnly = finOnly; f->handlerCount++;
}
static void fgFlowPushFin(FgGen* g, FgFlow* f, int addr) {
    if (f->finCount == f->finCap) { int cap = f->finCap ? f->finCap * 2 : 4; int* n = (int*)fbAlloc(&g->flowArena, sizeof(int) * (size_t)cap); if (f->finCount) memcpy(n, f->fins, sizeof(int) * (size_t)f->finCount); f->fins = n; f->finCap = cap; }
    f->fins[f->finCount++] = addr;
}
/* Forgets the types of the slots from `from` on. */
static void fgFlowClearTypes(FgFlow* f, int from) { if (from < 0) from = 0; if (from < 64) memset(&f->types[from], 0, sizeof(int) * (size_t)(64 - from)); }
static bool fgFlowSameShape(const FgFlow* a, const FgFlow* b) {
    int i;
    if (a->depth != b->depth || a->region != b->region || a->scopeCount != b->scopeCount || a->handlerCount != b->handlerCount || a->finCount != b->finCount) return false;
    for (i = 0; i < a->scopeCount; i++) if (a->scopes[i].id != b->scopes[i].id || a->scopes[i].declared != b->scopes[i].declared) return false;
    for (i = 0; i < a->handlerCount; i++) if (a->handlers[i].region != b->handlers[i].region || a->handlers[i].finOnly != b->handlers[i].finOnly) return false;
    for (i = 0; i < a->finCount; i++) if (a->fins[i] != b->fins[i]) return false;
    return true;
}
static bool fgFlowHasArray(const FgFlow* f, const char* v) { int i; for (i = 0; i < f->arrayCount; i++) if (streq0(f->arrays[i], v)) return true; return false; }

static FgOut* fgOutNew(FgGen* g) { FgOut* o = (FgOut*)fcAlloc(g->c, sizeof *o); o->dbgLv = INT32_MIN; o->exits = fcDictNew(g->c, FC_KEY_STR); fcBufStr(&o->code, ""); fcBufStr(&o->tail, ""); return o; }

/* ---- the switches of the program ---- */
static void UseExceptions(FgGen* g) {
    if (g->usesExceptions && g->usesGlobalOwn) return;
    g->usesExceptions = true;
    g->usesGlobalOwn = true;
    g->version++;
}
static void NeedTimeObj(FgGen* g) { if (g->needsTimeObj) return; g->needsTimeObj = true; g->version++; }
static void UseGraphics(FgGen* g) {
    if (g->usesGraphics) return;
    g->usesGraphics = true;
    g->usesGlobalOwn = true;
    UseExceptions(g);
    g->version++;
}
static void UseWindows(FgGen* g) {
    if (g->usesWindows) return;
    UseGraphics(g);
    g->usesWindows = true;
    g->version++;
}
static void UseThreads(FgGen* g) {
    if (!g->target->supportsThreads) FG_NOTSUP("fire threads are not supported by the platform '%s'", g->target->platform);
    if (!g->usesThreads) { g->usesThreads = true; g->version++; }
    UseExceptions(g);
}
/* A write to a global of the main program or a static field from a fire thread is a section */
static const char* SectionFor(FgGen* g, const char* variable) { return g->usesThreads && (fcStartsWith(variable, "G") || fcStartsWith(variable, "SF")) ? "bool ss_ = sectionScopeEnter(true); " : ""; }
static const char* SectionEnd(FgGen* g, const char* variable) { return *SectionFor(g, variable) ? "sectionScopeExit(ss_); " : ""; }

static const char* GlobalVar(FgGen* g, int slot, FcDict* locals) {
    FgFunc* f = g->cur;
    /* (a library: the slots of a fire body of its own are its thread's variables - only a slot of the program's globals is not possible) */
    if (LibraryRole && (f->kind != FK_FireBody || slot < f->globalSlotCount)) FG_NOTSUP("the library module '%s' uses a global variable of the program", g->self->name);
    if (f->kind != FK_FireBody || slot < f->globalSlotCount) { fgISetAdd(g->c, g->globals, slot); return fcFmt(g->c, "G%d", slot); }
    fgISetAdd(g->c, f->threadVars, slot);
    fcSetAdd(locals, fcFmt(g->c, "T%d", slot));
    return fcFmt(g->c, "T%d", slot);
}
static FgOut* OutOf(FgFunc* f, FgRegion* region) { return region ? region->out : f->root; }
/* The innermost region (starting at `from`) that contains the address; NULL: the function. */
static FgRegion* RegionAt(FgRegion* from, int addr) {
    FgRegion* r;
    for (r = from; r; r = r->outer) if (r->start <= addr && addr < r->end) return r;
    return NULL;
}
static int fgRegionDepthBase(const FgRegion* r) { return r->reg->depth; }
static int fgRegionScopeBase(const FgRegion* r) { return r->reg->scopeCount; }
/* The name of an operand stack slot in the code of `region`. */
static const char* SlotName(FgGen* g, FgRegion* region, int k) {
    static char names[256][8];
    static bool made = false;
    FgRegion* r;
    for (r = region; r; r = r->outer)
        if (k >= fgRegionDepthBase(r)) {
            void* v;
            if (!fgIMapTry(r->slotNames, k, &v)) { v = (void*)fcFmt(g->c, "c%d_%d", r->template_, k); fgIMapPut(g->c, r->slotNames, k, v); }
            return (const char*)v;
        }
    if (k >= 0 && k < 256) {
        if (!made) { int i; for (i = 0; i < 256; i++) snprintf(names[i], sizeof names[i], "s%d", i); made = true; }
        return names[k];
    }
    return fcFmt(g->c, "s%d", k);
}
static int ExcTypeId(FgGen* g, const char* typeName) {
    int i;
    if (!typeName) return -1;
    for (i = 0; i < g->excTypes->count; i++) if (streq0((const char*)g->excTypes->items[i], typeName)) return i;
    fcListAdd(g->c, g->excTypes, (void*)typeName);
    return g->excTypes->count - 1;
}
static bool IsTempId(int id) { return id >= TempBase || id == -11 || id == -12; }
static int TempOf(int scopeId) { return scopeId >= 0 ? scopeId + TempBase : scopeId - 10; }
static int HomeOf(int tempId) { return tempId >= TempBase ? tempId - TempBase : tempId + 10; }
static const char* ListName(FgGen* g, int scopeId) {
    if (IsTempId(scopeId)) return fcFmt(g->c, "T%s", ListName(g, HomeOf(scopeId)));
    return scopeId == FunctionScope ? "OP" : scopeId == GlobalScope ? "OG" : fcFmt(g->c, "O%d", scopeId);
}
/* The lists of the running call's scopes and their statement temporaries (what the caller owns, for `try x.Take...`). */
static FcList* CallLists(FgGen* g, FgFunc* f, FgFlow* st) {
    FcList* l = fcListNew(g->c);
    int i;
    for (i = 0; i < st->scopeCount; i++) {
        int t = TempOf(st->scopes[i].id), id = st->scopes[i].id;
        if (fgISetHas(g->c, f->needsList, t)) fcListAdd(g->c, l, (void*)fcFmt(g->c, "&%s", ListName(g, t)));
        if (fgISetHas(g->c, f->needsList, id)) fcListAdd(g->c, l, (void*)fcFmt(g->c, "&%s", ListName(g, id)));
    }
    return l;
}
/* an emitter: Action<string> */
typedef struct FgEmit { void (*fn)(struct FgEmit* e, const char* text); FcBuf* buf; const char* indent; } FgEmit;
static void fgEmitLine(FgEmit* e, const char* text) { if (e->buf) { fcBufStr(e->buf, e->indent ? e->indent : ""); fcBufStr(e->buf, text); fcBufChar(e->buf, '\n'); } }
static FgEmit fgEmitTo(FcBuf* buf, const char* indent) { FgEmit e; e.fn = fgEmitLine; e.buf = buf; e.indent = indent; return e; }
#define EMIT(e, ...) (e)->fn((e), fcFmt(g->c, __VA_ARGS__))
#define EMITS(e, text) (e)->fn((e), (text))
/* Leaving a scope: its temporaries first (they are the youngest), then the scope's own list. */
static void EmitLeave(FgGen* g, FgFunc* f, int scopeId, FgEmit* emit) {
    if (fgISetHas(g->c, f->needsList, TempOf(scopeId))) EMIT(emit, "leave(&%s);", ListName(g, TempOf(scopeId)));
    if (fgISetHas(g->c, f->needsList, scopeId)) EMIT(emit, "leave(&%s);", ListName(g, scopeId));
}

static void GenerateChunkCore(FgGen* g, FgFunc* f);
/* (gen/debug.c) */
static FgFuncDebug* DebugBegin(FgGen* g, FgFunc* f, FgCode code, FgFlow** entry);
static void DebugBeforeInstruction(FgGen* g, FgFunc* f, FgFuncDebug* d, FcChunk* chunk, FgInstr* ins, int i, FgFlow* st, FgOut* o, bool isTarget, FcDict* locals);
static void DebugAfterInstruction(FgGen* g, FgFunc* f, FgInstr* ins, FgFlow* st, FgOut* o);
static const char* DebugPrologue(FgGen* g, FgFunc* f, FgFuncDebug* d);
static const char* DebugTables(FgGen* g, FgFunc* f, FgFuncDebug* d);
static const char* DebugWrapper(FgGen* g, FgFunc* f, const char* call);
static const char* DebugCatchWrapper(FgGen* g, FgFunc* f, const char* catchName);
/* (gen/stats.c) */
static FgFuncStat* StatOf(FgGen* g, FgFunc* f, int valueLocals, int cLocals);
static void GenerateChunk(FgGen* g, FgFunc* f) {
    const char* owner;
    if (f->external) return;   /* (a member of another module: that module generates it) */
    /* (a library: what this function makes and calls counts for its class - the program follows only the classes it reaches, CollectNeeds MadeBy) */
    owner = g->demandOwner;
    g->demandOwner = LibraryRole ? DemandOwnerOf(f->chunk) : NULL;
    FC_TRY(g->c) { GenerateChunkCore(g, f); }
    FC_CATCH(g->c) { g->demandOwner = owner; fcRethrow(g->c); }
    FC_END_TRY;
    g->demandOwner = owner;
}

static FgStep StepFn(FgGen* g, FgFunc* f, FcChunk* chunk, FgInstr* ins, FgFlow* st, FcBuf* sb, FcDict* locals);
static void Landing(FgGen* g, FgFunc* f, FgRegion* r, FcDict* locals);
static const char* CatchName(FgGen* g, FgFunc* f, FgRegion* region) { return fcFmt(g->c, "C_%s_%d", f->name, region->template_); }
static const char* fgMaxStr(const char* a) { return a; }

/* ---- the two passes over a chunk ---- */
typedef struct FgWork { int* items; int count, cap; } FgWork;
static void fgWorkPush(FgGen* g, FgWork* w, int v) {
    if (w->count == w->cap) { int cap = w->cap ? w->cap * 2 : 64; int* n = (int*)fbAlloc(&g->flowArena, sizeof(int) * (size_t)cap); if (w->count) memcpy(n, w->items, sizeof(int) * (size_t)w->count); w->items = n; w->cap = cap; }
    w->items[w->count++] = v;
}
typedef struct FgPropagate { FgGen* g; FgFunc* f; FgFlow** entry; FgWork* work; FcDict* finAddrs; const char* name; } FgPropagate;
static void Propagate(FgPropagate* p, int addr, FgFlow* state, int from, bool owned) {
    FgGen* g = p->g;
    FgFlow* existing;
    int target, k;
    bool typesChanged = false;
    if (addr < 0 || addr >= p->f->indexOfLen || p->f->indexOf[addr] == 0) FG_NOTSUP("jump to %d inside an instruction (%s)", addr, p->name);
    target = p->f->indexOf[addr] - 1;
    if (fgISetHas(g->c, p->finAddrs, addr)) { state = fgFlowClone(g, state); fgFlowPushFin(g, state, addr); owned = true; }
    if (!p->entry[target]) { p->entry[target] = owned ? state : fgFlowClone(g, state); fgWorkPush(g, p->work, target); return; }
    existing = p->entry[target];
    if (!fgFlowSameShape(existing, state)) FG_NOTSUP("inconsistent stack/scope state at %d (%s), reached from %d", addr, p->name, from);
    if ((existing->refs | state->refs) != existing->refs) { existing->refs |= state->refs; fgWorkPush(g, p->work, target); }
    if ((existing->tempsDirty | state->tempsDirty) != existing->tempsDirty) { existing->tempsDirty |= state->tempsDirty; fgWorkPush(g, p->work, target); }
    if ((existing->borrowed | state->borrowed) != existing->borrowed) { existing->borrowed |= state->borrowed; fgWorkPush(g, p->work, target); }
    if (existing->selfAlive && !state->selfAlive) { existing->selfAlive = false; fgWorkPush(g, p->work, target); }
    if (existing->arrayCount > 0) {
        bool except = false;
        for (k = 0; k < existing->arrayCount; k++) if (!fgFlowHasArray(state, existing->arrays[k])) except = true;
        if (except) {
            const char** kept = (const char**)fbAlloc(&g->flowArena, sizeof(char*) * (size_t)(existing->arrayCount + 1));
            int n = 0, m;
            for (k = 0; k < existing->arrayCount; k++) {
                bool dup = false;
                if (!fgFlowHasArray(state, existing->arrays[k])) continue;
                for (m = 0; m < n; m++) if (streq0(kept[m], existing->arrays[k])) dup = true;
                if (!dup) kept[n++] = existing->arrays[k];
            }
            existing->arrays = kept; existing->arrayCount = n;
            fgWorkPush(g, p->work, target);
        }
    }
    if ((existing->scalar & state->scalar) != existing->scalar) { existing->scalar &= state->scalar; fgWorkPush(g, p->work, target); }
    for (k = 0; k < 64; k++) {
        int joined = JoinTypes(existing->types[k], state->types[k]);
        if (joined != existing->types[k]) { existing->types[k] = joined; typesChanged = true; }
    }
    if (typesChanged) fgWorkPush(g, p->work, target);
}

static FcList* fgSortedLocals(FgGen* g, FcDict* locals) { return fgSortedKeys(g->c, locals, true); }
typedef struct FgMember { const char* decl; const char* name; const char* init; } FgMember;
static void fgMemberAdd(FgGen* g, FcList* members, const char* type, const char* name, const char* init) {
    FgMember* m = (FgMember*)fcAlloc(g->c, sizeof *m);
    m->decl = fcFmt(g->c, "%s %s", type, name); m->name = name; m->init = init;
    fcListAdd(g->c, members, m);
}
static const char* ListInit(FgFunc* f, int i) {
    return IsTempId(i) ? "scopeList(0xFFFFFFFFu)" : i < 0 ? "scopeList(poolMark())" : i >= f->chunk->len ? "scopeList(0xFFFFFFFFu)" : "scopeList(0)";
}
static int fgCmpRegion(const void* a, const void* b) { const FgRegion* x = *(const FgRegion* const*)a; const FgRegion* y = *(const FgRegion* const*)b; return x->template_ - y->template_; }
static FcList* fgRegionsSorted(FgGen* g, FgFunc* f) {
    FcList* l = fcListNew(g->c);
    FC_DICT_EACH(f->regions, e) fcListAdd(g->c, l, e->value);
    if (l->count > 1) qsort(l->items, (size_t)l->count, sizeof(void*), fgCmpRegion);
    return l;
}
/* the identifiers of a text (Regex `\b[A-Za-z_][A-Za-z0-9_]*\b`) as a set */
static FcDict* fgIdentifiers(FgGen* g, const char* text) {
    FcDict* set = fcDictNew(g->c, FC_KEY_STR);
    const char* p = text;
    while (*p) {
        if ((*p >= 'A' && *p <= 'Z') || (*p >= 'a' && *p <= 'z') || *p == '_') {
            const char* s = p;
            bool boundary = s == text || !((s[-1] >= 'A' && s[-1] <= 'Z') || (s[-1] >= 'a' && s[-1] <= 'z') || (s[-1] >= '0' && s[-1] <= '9') || s[-1] == '_' || (unsigned char)s[-1] >= 0x80);
            while ((*p >= 'A' && *p <= 'Z') || (*p >= 'a' && *p <= 'z') || (*p >= '0' && *p <= '9') || *p == '_') p++;
            if (boundary && !((unsigned char)*p >= 0x80)) fcSetAdd(set, fcStrN(g->c, s, (size_t)(p - s)));
        } else p++;
    }
    return set;
}

static const char* fgSuffixInFrame(void* ctx, const char* p) { return fcDictHas((FcDict*)ctx, p) ? "_in" : ""; }
static const char* fgLastLine(FgGen* g, const char* text) {
    const char* t = fgTrim(g->c, text);
    const char* nl = strrchr(t, '\n');
    return fgTrim(g->c, nl ? nl + 1 : t);
}

static const char* PointerFunction(FgGen* g, const char* name, const char* code, const char* signature);

static void GenerateChunkCore(FgGen* g, FgFunc* f) {
    FcChunk* chunk = f->chunk;
    FgCode code;
    FcDict* finAddrs;
    FgFlow** entry;
    FgFlow* start;
    FcDict* scalarParams;
    FcDict* targets;
    FcDict* locals;
    FgWork work = {0};
    FgPropagate prop;
    FcList* catchRegions;
    FcList* regionsSorted;
    FcList* members;
    FcBuf pre = {0};
    FcBuf decl = {0};
    FcBuf post = {0};
    FcBuf sb = {0};
    FcBuf catchCode = {0};
    FcList* listMacros;
    FcList* plain;
    FcDict* inFrame;
    const char* name;
    const char* defName;
    const char* frame;
    const char* defSignature;
    bool framed;
    FgFuncDebug* dbg;
    int i, k;
    g->cur = f;
    f->cabi = CAbiOf(g, f);
    f->csig = NULL;
    name = f->name;
    code = fgDecode(g->c, chunk);
    f->decoded = code;
    f->indexOfLen = chunk->len + 1;
    f->indexOf = (int*)fcAlloc(g->c, sizeof(int) * (size_t)f->indexOfLen);
    for (i = 0; i < code.n; i++) f->indexOf[code.p[i].addr] = i + 1;
    f->hasCallTArgs = false;
    for (i = 0; i < code.n; i++) if (code.p[i].op == OP_TakeCallTypeArgs) f->hasCallTArgs = true;
    g->pendingNewTArgs = NULL;
    g->pendingLambdaTArgs = NULL;
    CollectTArgTexts(g, f);
    f->regions = fcDictNew(g->c, FC_KEY_STR);
    f->root = fgOutNew(g);
    f->needsList = fcDictNew(g->c, FC_KEY_STR);
    f->hoists = fcListNew(g->c);
    if (f->kind == FK_Lambda && f->captureCount > 0 && (f->captureCount > 64 || ((f->proto ? f->proto->captureCopyMask : 0) & ~(f->proto ? f->proto->captureMovedMask : 0)) != 0))
        fgISetAdd(g->c, f->needsList, FunctionScope);
    finAddrs = fcDictNew(g->c, FC_KEY_STR);
    for (i = 0; i < chunk->handlers->count; i++) { FcHandler* t = (FcHandler*)chunk->handlers->items[i]; if (t->finallyAddr >= 0) fgISetAdd(g->c, finAddrs, t->finallyAddr); }

    entry = (FgFlow**)fbAlloc(&g->flowArena, sizeof(FgFlow*) * (size_t)(code.n + 1));
    f->entry = entry;
    start = fgFlowNew(g);
    start->selfAlive = f->kind == FK_Ctor && fgHasSelf(f);
    if (fgIsTop(f)) fgFlowPushScope(g, start, GlobalScope, f->kind == FK_FireBody ? f->proto->paramCount : 0);
    else fgFlowPushScope(g, start, FunctionScope, fgParamLike(f));
    entry[0] = start;
    /* the guards of the parameters: slot -> the address of the later one */
    f->guardAddr = fcDictNew(g->c, FC_KEY_STR);
    for (i = 0; i < code.n; i++)
        if (code.p[i].op == OP_GuardLocal || code.p[i].op == OP_GuardLocalType) {
            void* v;
            if (!fgIMapTry(f->guardAddr, code.p[i].a[0], &v) || FC_VALI(v) < code.p[i].addr) fgIMapPut(g->c, f->guardAddr, code.p[i].a[0], FC_IVAL(code.p[i].addr));
        }
    for (k = 0; k + 2 < code.n; k++) {
        bool prefix = true;
        int m;
        if (!(code.p[k].op == OP_LoadLocal && code.p[k].a[0] == 0 && code.p[k].a[1] < fgParamLike(f) && code.p[k + 1].op == OP_TypeArray
            && code.p[k + 2].op == OP_StoreLocal && code.p[k + 2].a[0] == 0 && code.p[k + 2].a[1] == code.p[k].a[1] && !fgIMapTry(f->guardAddr, code.p[k].a[1], NULL))) continue;
        for (m = 0; m < k; m++) {
            int op = code.p[m].op;
            if (!(op == OP_LoadLocal || op == OP_TypeArray || op == OP_StoreLocal || op == OP_Pop || op == OP_ConvertWidth || op == OP_CopyArgs || op == OP_TakeCallTypeArgs)) { prefix = false; break; }
        }
        if (prefix) fgIMapPut(g->c, f->guardAddr, code.p[k].a[1], FC_IVAL(code.p[k + 2].addr));
    }
    scalarParams = fcDictNew(g->c, FC_KEY_STR);
    for (i = 0; i < code.n; i++) if (code.p[i].op == OP_GuardLocal && code.p[i].a[0] < fgParamLike(f) && (code.p[i].a[1] & 0x1F) != TAG_String) fgISetAdd(g->c, scalarParams, code.p[i].a[0]);
    for (i = 0; i < fgParamLike(f); i++) {
        const char* p = fcFmt(g->c, "P%d", i);
        if (!fgISetHas(g->c, scalarParams, i)) MarkVarRef(g, VarKey(g, f, p));
        if (!fgIMapTry(f->guardAddr, i, NULL)) JoinVarType(g, VarKey(g, f, p), TypeUnknown);
        else if (f->cabi && i < f->cabi->paramCount && f->cabi->paramType[i] != 0) JoinVarType(g, VarKey(g, f, p), f->cabi->paramType[i]);
    }
    if (fgIsTop(f) && (g->usesGlobalOwn || f->kind == FK_FireBody)) fgISetAdd(g->c, f->needsList, GlobalScope);

    targets = f->targets = fcDictNew(g->c, FC_KEY_STR);
    f->scalars = fcDictNew(g->c, FC_KEY_STR);
    fgWorkPush(g, &work, 0);
    locals = fcDictNew(g->c, FC_KEY_STR);
    for (i = 0; i < fgThreadArgs(f); i++) {
        const char* t = fcFmt(g->c, "T%d", f->globalSlotCount + i);
        fcSetAdd(locals, t);
        fgISetAdd(g->c, f->threadVars, f->globalSlotCount + i);
        MarkVarRef(g, VarKey(g, f, t));
        JoinVarType(g, VarKey(g, f, t), TypeUnknown);
    }
    prop.g = g; prop.f = f; prop.entry = entry; prop.work = &work; prop.finAddrs = finAddrs; prop.name = name;

    /* Pass 1: propagate states (and find out which scopes can own objects); pass 2: emit with the states found. */
    for (;;) {
        int signature = fgFinSignature(g, f);
        while (work.count > 0) {
            int idx = work.items[--work.count];
            FgFlow* state = fgFlowClone(g, entry[idx]);
            FgInstr* ins = &code.p[idx];
            FgStep step = StepFn(g, f, chunk, ins, state, NULL, locals);
            FgOut* o = OutOf(f, entry[idx]->region);
            int e2 = entry[idx]->depth > state->depth ? entry[idx]->depth : state->depth;
            if (e2 > o->maxDepth) o->maxDepth = e2;
            if (step.hasJump) { fgISetAdd(g->c, targets, step.jumpTarget); Propagate(&prop, step.jumpTarget, state, ins->addr, false); }
            for (k = 0; k < step.extraCount; k++) { fgISetAdd(g->c, targets, step.extra[k].addr); Propagate(&prop, step.extra[k].addr, step.extra[k].state, ins->addr, false); }
            if (step.fallsThrough) {
                if (idx + 1 >= code.n) FG_NOTSUP("code of %s runs past its end", name);
                Propagate(&prop, code.p[idx + 1].addr, state, ins->addr, true);
            }
        }
        if (fgFinSignature(g, f) == signature) break;
        for (i = 0; i < code.n; i++) if (entry[i]) fgWorkPush(g, &work, i);
    }
    /* a hoist needs the list of its target only when a scope it hoists from can own something */
    for (i = 0; i < f->hoists->count; i++) {
        FgHoist* h = (FgHoist*)f->hoists->items[i];
        bool any = false;
        for (k = 0; k < h->sourceCount; k++) if (fgISetHas(g->c, f->needsList, h->sources[k])) any = true;
        if (any) fgISetAdd(g->c, f->needsList, h->target);
    }

    dbg = DebugBuild ? DebugBegin(g, f, code, entry) : NULL;
    for (i = 0; i < code.n; i++) {
        FgInstr* ins = &code.p[i];
        FgOut* o;
        FgFlow* state;
        const char* comment;
        FcBuf cb = {0};
        if (!entry[i]) continue;
        o = OutOf(f, entry[i]->region);
        if (fgISetHas(g->c, targets, ins->addr)) fcBufFmt(&o->code, "L%d:;\n", ins->addr);
        if (dbg) DebugBeforeInstruction(g, f, dbg, chunk, ins, i, entry[i], o, fgISetHas(g->c, targets, ins->addr), locals);
        fcBufFmt(&cb, "// %d: %s", ins->addr, fgOpName(ins->op));
        for (k = 0; k < ins->n; k++) fcBufFmt(&cb, "%s%d", k ? "," : " ", ins->a[k]);
        comment = fcBufDone(g->c, &cb);
        g->lineDir = NULL;
        if (g->target->lineDirectives) {
            int src, line;
            fgLocation(chunk, ins->addr, &src, &line);
            if (line > 0) o->lineDir = fcFmt(g->c, "#line %d %s", line, LineFileLiteral(g, src));
            g->lineDir = o->lineDir;
        }
        if (g->lineDir) { fcBufFmt(&o->code, "%s %s\n", g->lineDir, comment); g->lineFresh = true; }
        else fcBufFmt(&o->code, "    %s\n", comment);
        state = fgFlowClone(g, entry[i]);
        StepFn(g, f, chunk, ins, state, &o->code, locals);
        if (dbg) DebugAfterInstruction(g, f, ins, entry[i], o);
        g->lineDir = NULL;
    }
    regionsSorted = fgRegionsSorted(g, f);
    for (i = 0; i < regionsSorted->count; i++) Landing(g, f, (FgRegion*)regionsSorted->items[i], locals);

    /* A function with catch blocks keeps its variables in a frame (a struct) */
    catchRegions = fcListNew(g->c);
    for (i = 0; i < regionsSorted->count; i++) if (((FgRegion*)regionsSorted->items[i])->catchCount > 0) fcListAdd(g->c, catchRegions, regionsSorted->items[i]);
    framed = catchRegions->count > 0;
    members = fcListNew(g->c);
    defName = Wrapped && (f->kind != FK_Main || DebugBuild) ? fcFmt(g->c, "%s_b", f->name) : f->name;
    if (framed) { FcList* pn = fgParamNames(g, f); for (i = 0; i < pn->count; i++) fgMemberAdd(g, members, "Value", (const char*)pn->items[i], fcFmt(g->c, "%s_in", (const char*)pn->items[i])); }
    /* a debug build: the frame of the function on the shadow stack (its wrapper pushed it; the catch blocks reach it through the frame struct) */
    if (dbg && framed) fgMemberAdd(g, members, "DbgFrame*", "DF", "g_dbgTop");

    fcBufStr(&pre, ""); fcBufStr(&decl, ""); fcBufStr(&post, "");
    fcBufStr(&pre, Trampoline(g, f));
    if (fgHasSelf(f)) fcBufStr(&pre, "    (void)self;\n");
    if (IsSetterFunc(g, f)) {
        if (framed) fgMemberAdd(g, members, "OwnList*", "MYAL", "accTakeHandOff()"); else fcBufStr(&decl, "    OwnList* const MYAL = accTakeHandOff(); (void)MYAL;\n");
    }
    if (f->accTrack) {
        if (framed) fgMemberAdd(g, members, "uint32_t", "ACCB", "g_acc.top"); else fcBufStr(&decl, "    uint32_t const ACCB = g_acc.top;\n");
    }
    if (f->kind == FK_FireBody) fcBufStr(&pre, "    (void)args;\n");
    if (!fgIsTop(f) && g->usesThreads) fcBufFmt(&pre, "    if (FIRE_UNLIKELY(pollSignals())) return %s;\n", UndefReturnOf(f->cabi));
    if (!fgIsTop(f) && g->usesThreads && fgHasSelf(f) && f->kind == FK_Method) fcBufFmt(&pre, "    if (FIRE_SELF_LOST(self)) { destroyedError(self); return %s; }\n", UndefReturnOf(f->cabi));
    if (f->kind == FK_Lambda) {
        uint64_t copyMask = f->proto ? f->proto->captureCopyMask : 0, movedMask = f->proto ? f->proto->captureMovedMask : 0;
        if (framed) fgMemberAdd(g, members, "Value", "self", "lamOn(lam)"); else fcBufStr(&decl, "    Value self = lamOn(lam); (void)self;\n");
        for (i = 0; i < f->captureCount; i++) {
            bool moved = i < 64 && ((movedMask >> i) & 1) != 0, copied = i >= 64 || ((copyMask >> i) & 1) != 0;
            const char* init = moved ? fcFmt(g->c, "lamCaptureLent(lam, %d)", i) : fcFmt(g->c, "lamCapture(lam, %d)", i);
            int p = fgParamCount(f) + i;
            if (framed) fgMemberAdd(g, members, "Value", fcFmt(g->c, "P%d", p), init); else fcBufFmt(&decl, "    Value P%d = %s; (void)P%d;\n", p, init, p);
            if (copied && !moved) {
                fgISetAdd(g->c, f->needsList, FunctionScope);
                fcBufFmt(&post, "    P%d = lamCaptureCopy(lam, %d, &OP);\n", p, i);
            }
        }
    }
    if (f->cabi)
        for (i = 0; i < fgParamCount(f); i++)
            if (f->cabi->paramType[i] != 0 && !ScalarVarOf(g, f, fcFmt(g->c, "P%d", i)))
                fcBufFmt(&pre, "    Value P%d = %s;\n", i, ScalarValueOf(g, KindOfType(f->cabi->paramType[i]), 0, fcFmt(g->c, "P%d_c", i)));
    for (i = 0; i < fgParamCount(f); i++) fcBufFmt(&pre, "    (void)P%d;\n", i);
    if (dbg && !framed) fcBufStr(&pre, "    DbgFrame* const DF = g_dbgTop;\n");
    if (g->usesTryDepth) {
        if (f->usesTryTake) { if (framed) fgMemberAdd(g, members, "OwnList*", "MYARGS", "g_argIn"); else fcBufStr(&decl, "    OwnList* const MYARGS = g_argIn;\n"); }
        fcBufStr(&post, "    g_argIn = NULL;\n");
    }
    if (f->root->maxDepth > 0) {
        if (framed) for (i = 0; i < f->root->maxDepth; i++) fgMemberAdd(g, members, "Value", fcFmt(g->c, "s%d", i), NULL);
        else {
            fcBufStr(&decl, "    Value ");
            for (i = 0; i < f->root->maxDepth; i++) fcBufFmt(&decl, "%ss%d", i ? ", " : "", i);
            fcBufStr(&decl, ";\n");
            fcBufStr(&post, "    ");
            for (i = 0; i < f->root->maxDepth; i++) fcBufFmt(&post, "(void)s%d; ", i);
            fcBufStr(&post, "\n");
        }
    }
    {
        FcList* all = fgSortedLocals(g, locals);
        FcList* localsToDeclare = fcListNew(g->c);
        FcList* scalarLocals = fcListNew(g->c);
        FcList* scalarNames = fcListNew(g->c);
        FcList* scalarTypes = fcListNew(g->c);
        FcList* scalarKeys;
        for (i = 0; i < all->count; i++) {
            const char* l = (const char*)all->items[i];
            if (l[0] == 'P' && atoi(l + 1) < fgParamLike(f)) continue;
            if (ScalarVarOf(g, f, l)) fcListAdd(g->c, scalarLocals, (void*)l);
            else fcListAdd(g->c, localsToDeclare, (void*)l);
        }
        if (localsToDeclare->count > 0) {
            if (framed) for (i = 0; i < localsToDeclare->count; i++) fgMemberAdd(g, members, "Value", (const char*)localsToDeclare->items[i], "Undef()");
            else {
                fcBufStr(&decl, "    Value ");
                for (i = 0; i < localsToDeclare->count; i++) fcBufFmt(&decl, "%s%s = Undef()", i ? ", " : "", (const char*)localsToDeclare->items[i]);
                fcBufStr(&decl, ";\n");
            }
        }
        for (i = 0; i < scalarLocals->count; i++) { fcListAdd(g->c, scalarNames, scalarLocals->items[i]); fcListAdd(g->c, scalarTypes, (void*)ScalarVarCType(g, f, (const char*)scalarLocals->items[i])); }
        scalarKeys = fgSortedKeys(g->c, f->scalars, true);
        for (i = 0; i < scalarKeys->count; i++) { fcListAdd(g->c, scalarNames, scalarKeys->items[i]); fcListAdd(g->c, scalarTypes, fcDictGet(f->scalars, scalarKeys->items[i])); }
        if (g->collectStats) f->stat = StatOf(g, f, localsToDeclare->count, scalarLocals->count);
        for (i = 0; i < scalarNames->count; i++) {
            const char* sn = (const char*)scalarNames->items[i];
            const char* stype = (const char*)scalarTypes->items[i];
            const char* zero = streq0(stype, "bool") ? "false" : "0";
            if (framed) fgMemberAdd(g, members, stype, sn, zero);
            else { fcBufFmt(&decl, "    %s %s = %s;\n", stype, sn, zero); fcBufFmt(&post, "    (void)%s;\n", sn); }
        }
    }
    listMacros = fcListNew(g->c);
    if (fcDictCount(f->needsList) > 0) {
        int n, *ordered = fgISetItems(g->c, f->needsList, true, &n);
        if (framed) for (i = 0; i < n; i++) fgMemberAdd(g, members, "OwnList", ListName(g, ordered[i]), ListInit(f, ordered[i]));
        else if (g->program->optimize == 1 && n >= 2 && n < 200) {
            FcBuf kinds = {0};
            FcBuf head = {0};
            for (i = 0; i < n; i++) {
                const char* init = ListInit(f, ordered[i]);
                int home = -1;
                fcBufChar(&kinds, strstr(init, "0xFFFFFFFF") ? 'f' : strstr(init, "poolMark") ? 'p' : 'z');
                if (IsTempId(ordered[i])) for (k = 0; k < n; k++) if (ordered[k] == HomeOf(ordered[i])) { home = k; break; }
                fcBufUnit(&kinds, (uint16_t)(home < 0 ? '-' : '0' + home));   /* (a char of .NET: above 0x7F two bytes of UTF-8) */
            }
            fcBufFmt(&decl, "    OwnList OL_[%d];\n", n);
            for (k = 0; k < n; k++) { fcListAdd(g->c, listMacros, (void*)ListName(g, ordered[k])); fcBufFmt(&decl, "#define %s (OL_[%d])\n", ListName(g, ordered[k]), k); }
            fcBufFmt(&head, "    scopeListsInit(OL_, %d, %s);\n", n, CString(g, fcBufDone(g->c, &kinds)));
            fcBufPut(&head, post.p, post.len);
            fcBufFree(&post);
            post = head;
        }
        else {
            fcBufStr(&decl, "    OwnList ");
            for (i = 0; i < n; i++) fcBufFmt(&decl, "%s%s = %s", i ? ", " : "", ListName(g, ordered[i]), ListInit(f, ordered[i]));
            fcBufStr(&decl, ";\n");
        }
    }
    if (f->needsArgList) {
        if (framed) fgMemberAdd(g, members, "OwnList", "AL", "scopeList(0)"); else fcBufStr(&decl, "    OwnList AL = {NULL, NULL, 0, 0, NULL, NULL, NULL}; (void)AL;\n");
    }
    for (i = 0; i < regionsSorted->count; i++) {
        FgRegion* region = (FgRegion*)regionsSorted->items[i];
        if (framed) fgMemberAdd(g, members, "Handler", fcFmt(g->c, "H%d", region->template_), NULL); else fcBufFmt(&decl, "    Handler H%d;\n", region->template_);
        if (region->catchCount > 0) for (k = fgRegionDepthBase(region); k < region->out->maxDepth; k++) fgMemberAdd(g, members, "Value", fcFmt(g->c, "c%d_%d", region->template_, k), NULL);
    }
    if (f->kind == FK_Main && g->usesThreads) fcBufStr(&post, Modular ? "    gilAcquire();\n    g_domainRoot = &OG;\n#ifdef FIRE_STR_CROSS\n    g_domShareGlobals = fire_shareGlobals;\n#endif\n" : "    gilAcquire();\n    g_domainRoot = &OG;\n#ifdef FIRE_STR_CROSS\n    g_domShareGlobals = fire_shareGlobals;\n#endif\n#ifdef FIRE_NO_GIL\n    fire_pkgLocksInit();\n#endif\n");
    if (fgIsTop(f) && (g->usesGlobalOwn || f->kind == FK_FireBody)) fcBufStr(&post, "    g_globalOwn = &OG;\n");
    if (f->kind == FK_FireBody) {
        fcBufStr(&post, "    adoptTravel(&OG);\n");
        for (i = 0; i < fgThreadArgs(f); i++) {
            int t = f->globalSlotCount + i;
            if (VarRef(g, VarKey(g, f, fcFmt(g->c, "T%d", t)))) fcBufFmt(&post, "    T%d = args[%d]; retain(T%d);\n", t, i, t);
            else fcBufFmt(&post, "    T%d = args[%d];\n", t, i);
        }
    }
    if (listMacros->count == 0) {
        int n, *ordered = fgISetItems(g->c, f->needsList, true, &n);
        for (i = 0; i < n; i++)
            if (IsTempId(ordered[i]) && fgISetHas(g->c, f->needsList, HomeOf(ordered[i]))) fcBufFmt(&post, "    %s.home = &%s;\n", ListName(g, ordered[i]), ListName(g, HomeOf(ordered[i])));
    }
    if (!fgIsTop(f) && g->usesTakeUpwards && fgISetHas(g->c, f->needsList, FunctionScope)) fcBufStr(&post, "    OP.parent = g_globalOwn;   // the parent scope of a function scope is the global scope\n");
    for (i = 0; i < fgParamLike(f); i++)
        if (VarRef(g, VarKey(g, f, fcFmt(g->c, "P%d", i)))) fcBufFmt(&post, "    retain(P%d);\n", i);
    if (dbg) fcBufStr(&post, DebugPrologue(g, f, dbg));

    fcBufStr(&sb, "");
    if (dbg) fcBufStr(&sb, DebugTables(g, f, dbg));
    for (i = 0; i < regionsSorted->count; i++) {
        FgRegion* region = (FgRegion*)regionsSorted->items[i];
        FcList* types = fcListNew(g->c);
        for (k = 0; k < region->catchCount; k++) fcListAdd(g->c, types, (void*)fcFmt(g->c, "%d", ExcTypeId(g, region->catches[k].type)));
        fcBufFmt(&sb, "static const int32_t HT_%s_%d[] = {%s};\n", name, region->template_, types->count == 0 ? "0" : fgJoin(g->c, types, ", "));
        fcBufFmt(&sb, "static const HandlerInfo HI_%s_%d = {%d, HT_%s_%d, %d, %s};\n", name, region->template_, region->catchCount, name, region->template_, region->hasFinally ? 1 : 0, LibraryRole ? fcFmt(g->c, "fire_exct_%s", g->self->id) : "NULL");
    }
    frame = fcFmt(g->c, "FR_%s", f->name);
    fcBufStr(&catchCode, "");
    for (i = 0; i < catchRegions->count; i++) {
        FgRegion* region = (FgRegion*)catchRegions->items[i];
        const char* catchVar = fcFmt(g->c, "B%d_0", region->catchScopeId);
        fcBufFmt(&catchCode, "static void %s%s(void* ctx_, uint32_t ci, Value e)\n", CatchName(g, f, region), dbg ? "_b" : "");
        fcBufStr(&catchCode, "{\n");
        fcBufFmt(&catchCode, "    %s* FR = (%s*)ctx_;\n", frame, frame);
        fcBufStr(&catchCode, "    switch (ci) {\n");
        for (k = 0; k < region->catchCount; k++) {
            const char* init = fcFmt(g->c, "%s%s", fcDictHas(locals, catchVar) ? fcFmt(g->c, "%s = e; ", catchVar) : "(void)e; ",
                fgISetHas(g->c, f->needsList, region->catchScopeId) ? fcFmt(g->c, "%s.mark = poolMark(); %s", ListName(g, region->catchScopeId),
                    g->usesTakeUpwards ? fcFmt(g->c, "%s.parent = &%s; ", ListName(g, region->catchScopeId), ListName(g, region->reg->scopes[region->reg->scopeCount - 1].id)) : "") : "");
            fcBufFmt(&catchCode, "    case %d: { %sgoto L%d; }\n", k, init, region->catches[k].addr);
        }
        fcBufStr(&catchCode, "    default: break;\n");
        fcBufStr(&catchCode, "    }\n");
        fcBufPut(&catchCode, region->out->code.p, region->out->code.len);
        fcBufPut(&catchCode, region->out->tail.p, region->out->tail.len);
        fcBufStr(&catchCode, "    fatal(\"control flow fell off the end of a catch block\");\n");
        fcBufStr(&catchCode, "}\n");
    }
    plain = fcListNew(g->c);
    if (framed && !dbg) {   /* (a debug build keeps them all in the frame: the debugger reads them from the catch blocks too) */
        FcDict* used = fgIdentifiers(g, catchCode.p ? catchCode.p : "");
        FcList* kept = fcListNew(g->c);
        for (i = 0; i < members->count; i++) {
            FgMember* m = (FgMember*)members->items[i];
            if (!fcDictHas(used, m->name) && !fcStartsWith(m->decl, "Handler ")) fcListAdd(g->c, plain, m);
            else fcListAdd(g->c, kept, m);
        }
        members = kept;
    }
    inFrame = fcDictNew(g->c, FC_KEY_STR);
    for (i = 0; i < members->count; i++) fcSetAdd(inFrame, ((FgMember*)members->items[i])->name);
    defSignature = framed ? fgSignatureOfFn(g, f, defName, fgSuffixInFrame, inFrame) : fgSignatureOf(g, f, defName, "");
    if (f->cabi) defSignature = f->csig = CSignature(g, f, f->cabi);
    if (framed) {
        fcBufFmt(&sb, "typedef struct %s { ", frame);
        for (i = 0; i < members->count; i++) fcBufFmt(&sb, "%s%s;", i ? " " : "", ((FgMember*)members->items[i])->decl);
        fcBufFmt(&sb, " } %s;\n", frame);
        for (i = 0; i < catchRegions->count; i++) fcBufFmt(&sb, "static void %s(void* ctx_, uint32_t ci, Value e);\n", CatchName(g, f, (FgRegion*)catchRegions->items[i]));
        if (dbg) for (i = 0; i < catchRegions->count; i++) fcBufFmt(&sb, "static void %s_b(void* ctx_, uint32_t ci, Value e);\n", CatchName(g, f, (FgRegion*)catchRegions->items[i]));
        for (i = 0; i < members->count; i++) { const char* mn = ((FgMember*)members->items[i])->name; fcBufFmt(&sb, "#define %s (FR->%s)\n", mn, mn); }
    }
    fcBufFmt(&sb, "%s\n", defSignature);
    fcBufStr(&sb, "{\n");
    if (framed) {
        FcList* pn = fgParamNames(g, f);
        fcBufFmt(&sb, "    %s FRv; %s* FR = &FRv;\n", frame, frame);
        for (i = 0; i < members->count; i++) { FgMember* m = (FgMember*)members->items[i]; if (m->init) fcBufFmt(&sb, "    %s = %s;\n", m->name, m->init); }
        for (i = 0; i < plain->count; i++) {
            FgMember* m = (FgMember*)plain->items[i];
            if (fcListHasStr(pn, m->name)) continue;
            if (!m->init) fcBufFmt(&sb, "    %s;", m->decl); else fcBufFmt(&sb, "    %s = %s;", m->decl, m->init);
            fcBufFmt(&sb, " (void)%s;\n", m->name);
        }
    }
    fcBufPut(&sb, pre.p, pre.len);
    fcBufPut(&sb, decl.p, decl.len);
    fcBufPut(&sb, post.p, post.len);
    fcBufPut(&sb, f->root->code.p, f->root->code.len);
    fcBufPut(&sb, f->root->tail.p, f->root->tail.len);
    {
        const char* lastLine = fgLastLine(g, f->root->tail.len > 0 ? fcStrN(g->c, f->root->tail.p, f->root->tail.len) : fcStrN(g->c, f->root->code.p ? f->root->code.p : "", f->root->code.len));
        bool endsClosed = fcStartsWith(lastLine, "return") || fcStartsWith(lastLine, "goto ");
        if (!fgIsTop(f) && !endsClosed) fcBufStr(&sb, "    fatal(\"control flow fell off the end of a function\");\n");
    }
    fcBufStr(&sb, "}\n");
    if (g->target->lineDirectives) fcBufStr(&sb, LineResetMarker "\n");
    fcBufPut(&sb, catchCode.p, catchCode.len);
    if (g->target->lineDirectives && catchCode.len > 0) fcBufStr(&sb, LineResetMarker "\n");
    if (framed) for (i = 0; i < members->count; i++) fcBufFmt(&sb, "#undef %s\n", ((FgMember*)members->items[i])->name);
    for (i = 0; i < listMacros->count; i++) fcBufFmt(&sb, "#undef %s\n", (const char*)listMacros->items[i]);
    if (dbg) for (i = 0; i < catchRegions->count; i++) fcBufFmt(&sb, "%s\n", DebugCatchWrapper(g, f, CatchName(g, f, (FgRegion*)catchRegions->items[i])));
    if (f->cabi) fcBufStr(&sb, CAbiWrapper(g, f, f->cabi));
    if (!streq0(defName, f->name)) {
        FcList* args = fcListNew(g->c);
        FcList* pn = fgParamNames(g, f);
        if (f->kind == FK_FireBody) fcListAdd(g->c, args, (void*)"args");
        for (i = 0; i < pn->count; i++) fcListAdd(g->c, args, pn->items[i]);
        fcBufFmt(&sb, "%s\n", fgSignature(g, f));
        if (dbg) fcBufFmt(&sb, "%s\n", DebugWrapper(g, f, fcFmt(g->c, "%s(%s)", defName, fgJoin(g->c, args, ", "))));
        else fcBufFmt(&sb, f->kind == FK_FireBody ? "{ %s(%s); }\n" : "{ return %s(%s); }\n", defName, fgJoin(g->c, args, ", "));
    }
    f->code = PointerArgs(g, fcBufDone(g->c, &sb));
    fcBufFree(&pre); fcBufFree(&decl); fcBufFree(&post); fcBufFree(&catchCode);
    fcBufFree(&f->root->code); fcBufFree(&f->root->tail);
    for (i = 0; i < regionsSorted->count; i++) { FgRegion* r = (FgRegion*)regionsSorted->items[i]; fcBufFree(&r->out->code); fcBufFree(&r->out->tail); }
    if (PointerCallsOn && !framed && !dbg && streq0(defName, f->name) && !f->exportName && !f->external && !(f->kind == FK_Main || f->kind == FK_FireBody)) {
        f->code = PointerFunction(g, f->cabi ? CName(g, f) : f->name, f->code, defSignature);
        if (f->cabi) f->code = PointerFunction(g, f->name, f->code, fgSignature(g, f));
    }
    NoteCReturn(g, f);
    fbArenaFree(&g->flowArena);
    memset(&g->flowArena, 0, sizeof g->flowArena);
    f->entry = NULL;
}

/* The runtime functions that only read a Value take it by its address when it is a variable or a slot (fire_rt.h `leafAliveP`, ...) */
static bool fgIsIdChar(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'; }
static bool fgIsIdStart(char c) { return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_'; }
static const char* PointerArgs(FgGen* g, const char* code) {
    static const char* const one[] = { "leafAlive", "destroyedError", "retain", "release", "truthy", "objAt", "ctorEnd" };
    static const char* const two[] = { "adopt", "hoistFrom", "transferTree", "finishArgs", "claimToList" };
    FcBuf b = {0};
    const char* p = code;
    fcBufStr(&b, "");
    while (*p) {
        bool matched = false;
        if (fgIsIdChar(*p) && (p == code || !fgIsIdChar(p[-1]))) {
            size_t k;
            for (k = 0; k < sizeof one / sizeof one[0] && !matched; k++) {
                size_t n = strlen(one[k]);
                const char* q;
                if (strncmp(p, one[k], n) != 0 || p[n] != '(') continue;
                q = p + n + 1;
                if (!fgIsIdStart(*q)) continue;
                while (fgIsIdChar(*q)) q++;
                if (*q != ')') continue;
                fcBufFmt(&b, "%sP(&", one[k]);
                fcBufPut(&b, p + n + 1, (size_t)(q - (p + n + 1)));
                fcBufChar(&b, ')');
                p = q + 1;
                matched = true;
            }
            for (k = 0; k < sizeof two / sizeof two[0] && !matched; k++) {
                size_t n = strlen(two[k]);
                const char* q;
                if (strncmp(p, two[k], n) != 0 || p[n] != '(') continue;
                q = p + n + 1;
                if (!fgIsIdStart(*q)) continue;
                while (fgIsIdChar(*q)) q++;
                if (q[0] != ',' || q[1] != ' ') continue;
                fcBufFmt(&b, "%sP(&", two[k]);
                fcBufPut(&b, p + n + 1, (size_t)(q - (p + n + 1)));
                fcBufStr(&b, ", ");
                p = q + 2;
                matched = true;
            }
            if (!matched) {
                const char* q = p;
                while (fgIsIdChar(*q)) q++;
                fcBufPut(&b, p, (size_t)(q - p));
                p = q;
            }
            continue;
        }
        fcBufChar(&b, *p++);
    }
    return fcBufDone(g->c, &b);
}

/* ---- `#line` ---- */
/* The file name of a source in `#line`: its path; `fire:prelude/N` for the prelude and the preludes of imports, `fire:source/N` for a source without a file. */
static const char* LineFileLiteral(FgGen* g, int sourceIndex) {
    void* v;
    const char* file = NULL;
    const char* nameText;
    const char* literal;
    if (fgIMapTry(g->lineFiles, sourceIndex, &v)) return (const char*)v;
    if (g->program->sourceFiles && sourceIndex < g->program->sourceFiles->count) file = (const char*)g->program->sourceFiles->items[sourceIndex];
    nameText = file ? file : sourceIndex < g->program->firstUserSource ? fcFmt(g->c, "fire:prelude/%d", sourceIndex) : fcFmt(g->c, "fire:source/%d", sourceIndex);
    literal = fcFmt(g->c, "\"%s\"", fgReplace(g->c, fgReplace(g->c, nameText, "\\", "\\\\"), "\"", "\\\""));
    fgIMapPut(g->c, g->lineFiles, sourceIndex, (void*)literal);
    return literal;
}
/* Replaces the markers after the functions with `#line` directives that name the C file and its own line again. */
static const char* ResolveLineResets(FgGen* g, const char* text, const char* fileName) {
    FcBuf result = {0};
    size_t ml = strlen(LineResetMarker);
    int lineNo = 1;
    const char* p = text;
    if (!strstr(text, LineResetMarker)) return text;
    while (*p) {
        const char* end = strchr(p, '\n');
        const char* next = end ? end + 1 : p + strlen(p);
        if (strncmp(p, LineResetMarker, ml) == 0) fcBufFmt(&result, "#line %d \"%s\"\n", lineNo + 1, fileName);
        else fcBufPut(&result, p, (size_t)(next - p));
        lineNo++;
        p = next;
    }
    return fcBufDone(g->c, &result);
}

/* ---- Exceptions: unwinding, landing sites, return through `finally` ---- */
static FgRegion* GetRegion(FgGen* g, FgFunc* f, int template_, FgFlow* st) {
    void* v;
    FgRegion* region;
    FcHandler* t;
    int i;
    if (fgIMapTry(f->regions, template_, &v)) return (FgRegion*)v;
    t = (FcHandler*)f->chunk->handlers->items[template_];
    region = (FgRegion*)fcAlloc(g->c, sizeof *region);
    region->template_ = template_;
    region->outer = st->region;
    region->reg = fgFlowClone(g, st);
    region->catchScopeId = f->chunk->len + template_;
    region->hasFinally = t->finallyAddr >= 0;
    region->hasFinallyAddr = t->finallyAddr >= 0;
    region->finallyAddr = t->finallyAddr;
    region->start = region->end = -1;
    region->exitLabels = fcDictNew(g->c, FC_KEY_STR);
    region->out = fgOutNew(g);
    region->slotNames = fcDictNew(g->c, FC_KEY_STR);
    region->catchCount = t->catchAddrs->count;
    region->catches = (FgCatch*)fcAlloc(g->c, sizeof(FgCatch) * (size_t)(region->catchCount + 1));
    for (i = 0; i < region->catchCount; i++) { region->catches[i].type = (const char*)t->catchTypes->items[i]; region->catches[i].addr = FC_VALI(t->catchAddrs->items[i]); }
    if (region->catchCount > 0) {
        FgInstr* before;
        region->start = region->catches[0].addr;
        before = &f->decoded.p[f->indexOf[region->start] - 2];
        if (before->op != OP_Jump) FG_NOTSUP("unexpected layout of a try statement in %s", f->name);
        region->end = before->a[0];
    }
    fgIMapPut(g->c, f->regions, template_, region);
    return region;
}
/* The state while a catch block runs: the registers of the `try` are there, only its `finally` is still armed. */
static FgFlow* LandingFlow(FgGen* g, FgRegion* region) {
    FgFlow* flow = fgFlowClone(g, region->reg);
    if (region->hasFinally) fgFlowPushHandler(g, flow, region, true);
    return flow;
}
/* The variables that belong to a scope of the function (parameters and base-scope locals for the function scope). */
static FcList* ScopeVariables(FgGen* g, FgFunc* f, int scopeId, FcDict* locals) {
    FcList* l = fcListNew(g->c);
    FcList* sorted;
    int i;
    if (scopeId == GlobalScope) return l;
    sorted = fgSortedLocals(g, locals);
    if (scopeId == FunctionScope) {
        for (i = 0; i < fgParamLike(f); i++) fcListAdd(g->c, l, (void*)fcFmt(g->c, "P%d", i));
        for (i = 0; i < sorted->count; i++) { const char* s = (const char*)sorted->items[i]; if (s[0] == 'P' && atoi(s + 1) >= fgParamLike(f)) fcListAdd(g->c, l, (void*)s); }
        return l;
    }
    {
        const char* prefix = fcFmt(g->c, "B%d_", scopeId);
        for (i = 0; i < sorted->count; i++) if (fcStartsWith((const char*)sorted->items[i], prefix)) fcListAdd(g->c, l, sorted->items[i]);
    }
    return l;
}
/* Leaves the scopes [downTo, fromCount) of `st` (innermost first) */
static void ReleaseScopes(FgGen* g, FgFunc* f, FgFlow* st, int fromCount, int downTo, bool reset, FcDict* locals, FgEmit* emit, const char* transfer) {
    int i, k;
    if (transfer) {
        FcList* leaving = fcListNew(g->c);
        for (i = fromCount - 1; i >= downTo; i--) {
            if (fgISetHas(g->c, f->needsList, TempOf(st->scopes[i].id))) fcListAdd(g->c, leaving, (void*)fcFmt(g->c, "&%s", ListName(g, TempOf(st->scopes[i].id))));
            if (fgISetHas(g->c, f->needsList, st->scopes[i].id)) fcListAdd(g->c, leaving, (void*)fcFmt(g->c, "&%s", ListName(g, st->scopes[i].id)));
        }
        if (leaving->count > 0) EMIT(emit, "{ %stransferTree(%s, T_, %d); }", CArray(g, "OwnList*", "T_", leaving), transfer, leaving->count);
    }
    for (i = fromCount - 1; i >= downTo; i--) {
        int id = st->scopes[i].id;
        FcList* vars;
        if (st->region && id == st->region->catchScopeId && i == fgRegionScopeBase(st->region)) { st->region->deferCatchRelease = true; continue; }
        EmitLeave(g, f, id, emit);
        vars = ScopeVariables(g, f, id, locals);
        for (k = 0; k < vars->count; k++) {
            const char* v = (const char*)vars->items[k];
            if (VarRef(g, VarKey(g, f, v))) EMIT(emit, reset ? "release(%s); %s = Undef();" : "release(%s);", v, v);
        }
    }
}
/* The end of the program: the global scope is released like any other (destructors run), then what the globals hold. */
static void HaltCode(FgGen* g, FgFunc* f, FgEmit* emit) {
    int n, i, *items;
    if (f->kind == FK_FireBody) {
        EMITS(emit, "g_leaving = 1;");
        if (fgISetHas(g->c, f->needsList, TempOf(GlobalScope))) EMIT(emit, "leave(&%s);", ListName(g, TempOf(GlobalScope)));
        EMITS(emit, "if (g_threadFailed) graveyardAdd(&OG); else leave(&OG);");
        items = fgISetItems(g->c, f->threadVars, true, &n);
        for (i = 0; i < n; i++) if (VarRef(g, VarKey(g, f, fcFmt(g->c, "T%d", items[i])))) EMIT(emit, "release(T%d);", items[i]);
        return;
    }
    if (g->usesThreads) EMITS(emit, "mainFinish();");
    if (g->collectStats && !Modular) EMITS(emit, "fire_heapReport();");
    EmitLeave(g, f, GlobalScope, emit);
    items = fgISetItems(g->c, g->globals, true, &n);
    for (i = 0; i < n; i++) if (VarRef(g, fcFmt(g->c, "G%d", items[i]))) EMIT(emit, "release(G%d);", items[i]);
    {
        FcList* byIndex = fgByValue(g, g->staticFields);
        for (i = 0; i < byIndex->count; i++) {
            FcList* parts = fgSplit(g->c, (const char*)((FcDictEntry*)byIndex->items[i])->key, '\x01');
            const char* sfv = StaticFieldVar(g, (const char*)parts->items[0], (const char*)parts->items[1]);
            if (VarRef(g, sfv)) EMIT(emit, "release(%s);", sfv);
        }
    }
}
/* The code that runs when something is unwinding through this point; returns the label. */
static const char* ExitLabel(FgGen* g, FgFunc* f, FgFlow* st, FcDict* locals) {
    FgOut* o = OutOf(f, st->region);
    FcBuf body = {0};
    FgEmit e = fgEmitTo(&body, "    ");
    FgHEntry* local = st->handlerCount > 0 && st->handlers[st->handlerCount - 1].region->outer == st->region ? &st->handlers[st->handlerCount - 1] : NULL;
    int baseCount = st->region ? fgRegionScopeBase(st->region) : (fgIsTop(f) ? 1 : 0);
    int downTo = local ? fgRegionScopeBase(local->region) : baseCount;
    const char* text;
    void* v;
    int id;
    fcBufStr(&body, "");
    if (st->scopeCount > downTo) {
        FgFlow* outer;
        ReleaseScopes(g, f, st, st->scopeCount, st->scopeCount - 1, local != NULL || st->region != NULL, locals, &e, NULL);
        outer = fgFlowClone(g, st);
        outer->scopeCount--;
        EMIT(&e, "goto %s;", ExitLabel(g, f, outer, locals));
    }
    else ReleaseScopes(g, f, st, st->scopeCount, downTo, local != NULL || st->region != NULL, locals, &e, NULL);
    if (st->scopeCount > downTo) {}
    else if (local) EMIT(&e, "goto LAND%d;", local->region->template_);
    else if (st->region) EMITS(&e, "return;");
    else if (fgIsTop(f)) { HaltCode(g, f, &e); EMITS(&e, "return;"); }
    else { if (f->accTrack) EMITS(&e, "if (g_acc.top > ACCB) g_acc.top = ACCB;"); EMIT(&e, "return %s;", UndefReturnOf(f->cabi)); }
    text = fcBufDone(g->c, &body);
    if (fcDictTry(o->exits, text, &v)) id = FC_VALI(v);
    else {
        id = fcDictCount(o->exits);
        fcDictPut(o->exits, text, FC_IVAL(id));
        fcBufFmt(&o->tail, "U%d:;\n", id);
        fcBufStr(&o->tail, text);
    }
    return fcFmt(g->c, "U%d", id);
}
/* A `return` of the C variable `r` from the code described by `st`; returns the edges of the flow graph (the entries of the finally blocks). */
typedef struct FgEdges { FgEdge* items; int count, cap; } FgEdges;
static void fgEdgeAdd(FgGen* g, FgEdges* es, int addr, FgFlow* st) {
    if (es->count == es->cap) { int cap = es->cap ? es->cap * 2 : 4; FgEdge* n = (FgEdge*)fbAlloc(&g->flowArena, sizeof(FgEdge) * (size_t)cap); if (es->count) memcpy(n, es->items, sizeof(FgEdge) * (size_t)es->count); es->items = n; es->cap = cap; }
    es->items[es->count].addr = addr; es->items[es->count].state = st; es->count++;
}
static void ReturnSeq(FgGen* g, FgFunc* f, FgFlow* st, bool rIsRef, bool retain, FgEmit* emit, FcDict* locals, FgEdges* edges) {
    bool isCtor = f->kind == FK_Ctor;
    const char* transfer = isCtor ? NULL : "r";
    bool accPull;
    int hi;
    FgEmit none = fgEmitTo(NULL, NULL);
    if (!emit) emit = &none;
    if (retain && rIsRef && !isCtor) EMITS(emit, "retain(r);");
    accPull = f->accTrack && !isCtor && st->scopeCount > 0;
    if (accPull) EMIT(emit, "accReturn(r, self, ACCB, &%s, false);", ListName(g, st->scopes[0].id));
    for (hi = st->handlerCount - 1; hi >= 0 && st->handlers[hi].region->outer == st->region; hi--) {
        FgRegion* region = st->handlers[hi].region;
        int fin;
        uint64_t below;
        FgFlow* fs;
        if (!st->handlers[hi].finOnly) EMITS(emit, "popHandler();");
        if (!region->hasFinally) continue;
        fin = region->finallyAddr;
        ReleaseScopes(g, f, st, st->scopeCount, fgRegionScopeBase(region), true, locals, emit, transfer);
        EMIT(emit, "%s = r; %s = Int(2); goto L%d;", SlotName(g, st->region, fgRegionDepthBase(region)), SlotName(g, st->region, fgRegionDepthBase(region) + 1), fin);
        fgISetAdd(g->c, f->finReturn, fin);
        below = fgRegionDepthBase(region) >= 64 ? ~0ULL : (1ULL << fgRegionDepthBase(region)) - 1;
        fs = fgFlowNew(g);
        fs->depth = fgRegionDepthBase(region) + 2;
        fs->scopes = (FgScope*)fgFlowDup(g, st->scopes, sizeof(FgScope) * (size_t)fgRegionScopeBase(region)); fs->scopeCount = fs->scopeCap = fgRegionScopeBase(region);
        fs->handlers = (FgHEntry*)fgFlowDup(g, st->handlers, sizeof(FgHEntry) * (size_t)hi); fs->handlerCount = fs->handlerCap = hi;
        fs->region = st->region;
        fs->fins = (int*)fgFlowDup(g, region->reg->fins, sizeof(int) * (size_t)region->reg->finCount); fs->finCount = fs->finCap = region->reg->finCount;
        fs->refs = (st->refs & below) | (rIsRef && fgRegionDepthBase(region) < 64 ? 1ULL << fgRegionDepthBase(region) : 0);
        fgEdgeAdd(g, edges, fin, fs);
        return;
    }
    if (!st->region) {
        if (f->accTrack) EMITS(emit, "if (g_acc.top > ACCB) g_acc.top = ACCB;");
        ReleaseScopes(g, f, st, st->scopeCount, 0, false, locals, emit, transfer);
        EMITS(emit, isCtor ? "return self;" : "return r;");
    } else {
        ReleaseScopes(g, f, st, st->scopeCount, fgRegionScopeBase(st->region), true, locals, emit, transfer);
        EMIT(emit, "exitReturn(&H%d, r); return;", st->region->template_);
        st->region->returnExit = true;
        ReturnSeq(g, f, LandingFlow(g, st->region), true, false, NULL, locals, edges);
    }
}
/* Where unwinding arrives at the function that owns a `try` */
static void Landing(FgGen* g, FgFunc* f, FgRegion* r, FcDict* locals) {
    FgOut* o = OutOf(f, r->outer);
    int t = r->template_, i, k;
    FcBuf lt = {0};
    FgEmit e = fgEmitTo(&lt, "    ");
    FgFlow* reg = r->reg;
    const char* exit;
    fcBufFmt(&lt, "LAND%d: FIRE_UNUSED_LABEL;\n", t);
    if (r->deferCatchRelease) {
        FcList* vars;
        EmitLeave(g, f, r->catchScopeId, &e);
        vars = ScopeVariables(g, f, r->catchScopeId, locals);
        for (k = 0; k < vars->count; k++) { const char* v = (const char*)vars->items[k]; if (VarRef(g, VarKey(g, f, v))) EMIT(&e, "release(%s); %s = Undef();", v, v); }
    }
    if (fcDictCount(r->exitLabels) > 0) {
        int n, *labels = fgISetItems(g->c, r->exitLabels, true, &n);
        EMIT(&e, "if (g_unwind.kind == UW_JUMP && g_unwind.target == &H%d) {", t);
        EMITS(&e, "    uint32_t lab = g_unwind.label; clearUnwind();");
        EMITS(&e, "    switch (lab) {");
        for (i = 0; i < n; i++) {
            int target = labels[i];
            FgFlow* te = f->entry[f->indexOf[target] - 1];
            bool here;
            FgEmit inner = fgEmitTo(&lt, "            ");
            if (!te) FG_NOTSUP("catch block leaves to unreachable code (%s)", f->name);
            here = te->region == r->outer;
            EMIT(&e, "    case %d: {", target);
            ReleaseScopes(g, f, reg, reg->scopeCount, here ? te->scopeCount : r->outer ? fgRegionScopeBase(r->outer) : 0, true, locals, &inner, NULL);
            if (here) {
                for (k = fgRegionDepthBase(r); k < te->depth; k++) EMIT(&e, "        %s = g_unwind.regs[%d];", SlotName(g, r->outer, k), k - fgRegionDepthBase(r));
                EMIT(&e, "        goto L%d;", target);
            }
            else EMIT(&e, "        exitJump(&H%d, %d); return;", r->outer->template_, target);
            EMITS(&e, "    }");
        }
        EMITS(&e, "    default: fatal(\"internal error: unknown landing label\");");
        EMITS(&e, "    }");
        EMITS(&e, "}");
    }
    if (r->returnExit) {
        FgEdges edges = {0};
        FgEmit inner = fgEmitTo(&lt, "        ");
        EMIT(&e, "if (g_unwind.kind == UW_RETURN && g_unwind.target == &H%d) {", t);
        EMITS(&e, "    Value r = g_unwind.value; (void)r; clearUnwind();");
        ReturnSeq(g, f, LandingFlow(g, r), true, false, &inner, locals, &edges);
        EMITS(&e, "}");
    }
    exit = ExitLabel(g, f, reg, locals);
    if (g->usesThreads) {
        EMITS(&e, "if (g_unwind.kind == UW_LEAVE) {");
        EMIT(&e, "    g_handlers = H%d.prev;", t);
        if (r->hasFinally) EMIT(&e, "    clearUnwind(); %s = Undef(); %s = Int(4); goto L%d;", SlotName(g, r->outer, fgRegionDepthBase(r)), SlotName(g, r->outer, fgRegionDepthBase(r) + 1), r->finallyAddr);
        else EMIT(&e, "    goto %s;", exit);
        EMITS(&e, "}");
    }
    EMIT(&e, "if (g_unwind.kind == UW_RETHROW && g_unwind.target == &H%d) {", t);
    EMITS(&e, "    Value x = g_unwind.value; clearUnwind();");
    if (r->hasFinally) EMIT(&e, "    %s = x; %s = Int(1); goto L%d;", SlotName(g, r->outer, fgRegionDepthBase(r)), SlotName(g, r->outer, fgRegionDepthBase(r) + 1), r->finallyAddr);
    else { EMITS(&e, "    throwValue(x, false);"); EMIT(&e, "    goto %s;", exit); }
    EMITS(&e, "}");
    EMIT(&e, "goto %s;", exit);
    fcBufPut(&o->tail, lt.p, lt.len);
    fcBufFree(&lt);
}

static const char* Var(FgGen* g, FgFlow* st, int depth, int slot, FcDict* locals) {
    int scopeIndex = st->scopeCount - 1 - depth, id;
    const char* v;
    if (scopeIndex < 0) FG_NOTSUP("access to a variable outside of the function (closures are not supported yet)");
    id = st->scopes[scopeIndex].id;
    if (id == GlobalScope) return GlobalVar(g, slot, locals);
    v = id == FunctionScope ? fcFmt(g->c, "P%d", slot) : fcFmt(g->c, "B%d_%d", id, slot);
    fcSetAdd(locals, v);
    return v;
}

/* ---- the scalar shadows, the aliases ---- */
static bool ScalarVarOf(FgGen* g, FgFunc* f, const char* v) {
    int type;
    if (!ScalarsOn) return false;
    if (v[0] == 'B') {}
    else if (v[0] == 'P') {
        int pi = atoi(v + 1);
        if (!(pi >= fgParamLike(f) || (f->cabi && pi < f->cabi->paramCount && f->cabi->paramType[pi] != 0))) return false;
    }
    else return false;
    if (!fgVarTypeTry(g, VarKey(g, f, v), &type)) return false;
    if (!(IsNarrowIntType(type) || KindOfType(type) == KindWord || KindOfType(type) == KindFloat)) return false;
    return !VarRef(g, VarKey(g, f, v));
}
static const char* ScalarName(FgGen* g, FgFunc* f, FgFlow* st, int k) {
    int kind = BaseKindOfType(st->types[k]);
    const char* name = fcFmt(g->c, "%s%s", SlotName(g, st->region, k), kind == KindBool ? "_b" : kind == KindWord ? "_w" : kind == KindFloat ? "_f" : "_n");
    if (!fcDictHas(f->scalars, name)) fcDictPut(f->scalars, name, (void*)ScalarCType(kind));
    else *fcDictRef(f->scalars, name) = (void*)ScalarCType(kind);
    return name;
}
/* The Value of a scalar `c` of the kind (its unit is static: the type's). */
static const char* ScalarValueOf(FgGen* g, int kind, int unit, const char* c) {
    switch (kind) {
        case KindBool: return fcFmt(g->c, "Bool(%s)", c);
        case KindWord: return fcFmt(g->c, "WordV((uint64_t)%s)", c);
        case KindFloat: return unit == 0 ? fcFmt(g->c, "Float(%s)", c) : fcFmt(g->c, "Float(%s, %s)", c, UnitC(g, unit));
        default: return unit == 0 ? fcFmt(g->c, "Int(%s)", c) : fcFmt(g->c, "Int(%s, %s)", c, UnitC(g, unit));
    }
}
/* Slot k gets its Value from its scalar shadow (and is a Value again). */
static void Materialize(FgGen* g, FgFunc* f, FgFlow* st, int k, FgEmit* emit) {
    const char* shadow;
    int kind;
    if (k < 0 || k >= 64 || ((st->scalar >> k) & 1) == 0) return;
    shadow = ScalarName(g, f, st, k);
    st->scalar &= ~(1ULL << k);
    kind = BaseKindOfType(st->types[k]);
    EMIT(emit, "%s = %s;", SlotName(g, st->region, k), ScalarValueOf(g, kind, UnitOfType(st->types[k]), shadow));
}
/* Slot k gets the value of the variable it is an alias of (and is a Value of its own again). */
static void MaterializeAlias(FgGen* g, FgFlow* st, int k, FgEmit* emit) {
    const char* variable;
    if (k < 0 || k >= 64 || !(variable = st->alias[k])) return;
    st->alias[k] = NULL;
    EMIT(emit, "%s = %s;", SlotName(g, st->region, k), variable);
}
/* Every alias slot (only those of `variable`, when given: it is about to be written) becomes a Value of its own. */
static void MaterializeAliases(FgGen* g, FgFlow* st, FgEmit* emit, const char* variable) {
    int k;
    for (k = 0; k < 64; k++) if (st->alias[k] && (!variable || streq0(st->alias[k], variable))) MaterializeAlias(g, st, k, emit);
}
/* The C that sets the helpers of a variable in Flow.Arrays from its array */
static const char* ArrayCheckText(FgGen* g, const char* v, int elem) {
    if (g->usesThreads)
        return fcFmt(g->c, "{ Arr* ra_ = NULL; %s_ok = %s.kind == K_Array && leafAlive(%s) && (ra_ = arrOf(%s))->typed && ra_->typed->packed && ra_->typed->elem == %d; %s_dom = false; if (%s_ok) { %s_a = ra_; %s_len = ra_->length; %s_d = ra_->typed->data; %s_dom = (FIRE_FLAGS_READ(&ra_->h) & 64) != 0; %s_seen = fire_domSeen(); } }",
            v, v, v, v, elem, v, v, v, v, v, v, v);
    return fcFmt(g->c, "{ Arr* ra_ = NULL; %s_ok = %s.kind == K_Array && leafAlive(%s) && (ra_ = arrOf(%s))->typed && ra_->typed->packed && ra_->typed->elem == %d; if (%s_ok) { %s_a = ra_; %s_len = ra_->length; %s_d = ra_->typed->data; } }",
        v, v, v, v, elem, v, v, v, v);
}
static const char* ArrayRecheckText(FgGen* g, const char* v, int elem) { return fcFmt(g->c, "if (FIRE_UNLIKELY(%s_dom) && FIRE_DOM_STALE(%s_seen)) %s", v, v, ArrayCheckText(g, v, elem)); }
static int fgKnownArrayElem(FgGen* g, FgFunc* f, const char* v) { void* e; return fcDictTry(g->knownArrayElem, fcFmt(g->c, "%p\x01%s", (void*)f, v), &e) ? FC_VALI(e) - 1 : 0; }
static void fgSetKnownArrayElem(FgGen* g, FgFunc* f, const char* v, int elem) {
    const char* key = fcFmt(g->c, "%p\x01%s", (void*)f, v);
    if (fcDictHas(g->knownArrayElem, key)) *fcDictRef(g->knownArrayElem, key) = FC_IVAL(elem + 1); else fcDictPut(g->knownArrayElem, key, FC_IVAL(elem + 1));
}
static bool IsLoopJump(int op) {
    return op == OP_Jump || op == OP_JumpIfFalse || op == OP_JumpIfFalsePeek || op == OP_JumpIfTruePeek || op == OP_JumpIfNotLt || op == OP_JumpIfNotLtEq
        || op == OP_JumpIfNotGt || op == OP_JumpIfNotGtEq || op == OP_JumpIfNotEq || op == OP_JumpIfNotNotEq;
}
static bool SelfNeutral(int op) {
    return op == OP_LoadConst || op == OP_LoadLocal || op == OP_LoadThis || op == OP_LoadGlobal || op == OP_Pop || op == OP_Dup || op == OP_StoreLocal || op == OP_StoreLocalPop
        || op == OP_DeclareLocal || op == OP_ClaimToScope || op == OP_CopyArgs || op == OP_GuardTop || op == OP_GuardLocal || op == OP_GuardLocalType;
}
static bool fgPlainAt(const int* types, int k) { return k >= 0 && k < 64 && types[k] != TypeUnknown && IsPlainType(types[k]); }
static bool fgArrayAt(const int* types, int k) { return k >= 0 && k < 64 && (types[k] & TypeNone) == 0 && (BaseKindOfType(types[k]) == KindArr || BaseKindOfType(types[k]) == KindBuf); }
static bool SelfNeutralHere(int op, const int* types, int depth) {
    switch (op) {
        case OP_Jump: case OP_EnterScope: case OP_JumpIfFalse: case OP_JumpIfFalsePeek: case OP_JumpIfTruePeek: return true;
        case OP_Neg: case OP_LogicalNot: case OP_CheckUnit: return fgPlainAt(types, depth - 1);
        case OP_Add: case OP_Sub: case OP_Mul: case OP_Div: case OP_Mod: case OP_BitAnd:
        case OP_Eq: case OP_NotEq: case OP_Lt: case OP_LtEq: case OP_Gt: case OP_GtEq:
        case OP_JumpIfNotLt: case OP_JumpIfNotLtEq: case OP_JumpIfNotGt: case OP_JumpIfNotGtEq: case OP_JumpIfNotEq: case OP_JumpIfNotNotEq:
            return fgPlainAt(types, depth - 1) && fgPlainAt(types, depth - 2);
        case OP_ArrayGet: return fgArrayAt(types, depth - 2) && fgPlainAt(types, depth - 1);
        case OP_GetField: return fgArrayAt(types, depth - 1);
        default: return false;
    }
}
static bool AnyAlias(const FgFlow* st) { int k; for (k = 0; k < 64; k++) if (st->alias[k]) return true; return false; }
static bool AliasOn(FgGen* g, FgFunc* f) {
    int i;
    if (DebugBuild || f->chunk->handlers->count != 0) return false;
    for (i = 0; i < f->decoded.n; i++) if (f->decoded.p[i].op == OP_AddressOfLocal) return false;
    return true;
}
static bool AliasAware(int op) {
    switch (op) {
        case OP_LoadConst: case OP_LoadLocal: case OP_LoadThis: case OP_LoadGlobal: case OP_Pop: case OP_Dup: case OP_StoreLocal: case OP_StoreLocalPop: case OP_DeclareLocal:
        case OP_StoreGlobal: case OP_StoreGlobalPop: case OP_GetField: case OP_SetField: case OP_SetFieldClaim: case OP_SetFieldOnThis: case OP_GetStaticField:
        case OP_CallMethod: case OP_CallStaticMethod: case OP_CallNative: case OP_NewObject: case OP_CallProtoWithThis: case OP_ClaimToScope: case OP_ReleaseTemps:
        case OP_CopyArgs: case OP_CheckUnit: case OP_GuardTop: case OP_Add: case OP_Sub: case OP_Mul: case OP_Div: case OP_Mod: case OP_Neg: case OP_LogicalNot:
        case OP_Eq: case OP_NotEq: case OP_Lt: case OP_LtEq: case OP_Gt: case OP_GtEq: case OP_Return: return true;
        default: return false;
    }
}
/* Every scalar slot (but `except1`, `except2`) becomes a Value again */
static void MaterializeAll(FgGen* g, FgFunc* f, FgFlow* st, FgEmit* emit, int except1, int except2) {
    int k;
    for (k = 0; k < 64 && st->scalar != 0; k++) if (k != except1 && k != except2) Materialize(g, f, st, k, emit);
}
/* A GuardTop of `defined int` (`float`, `bool`, `word`) whose slot certainly holds such a number already: nothing to check. */
static bool GuardTopProven(const FgFlow* st, const FgInstr* ins) {
    int declared, type;
    if (ins->op != OP_GuardTop || st->depth < 1 || st->depth > 64) return false;
    declared = (ins->a[0] + 1) & 0xFF;
    if (!fgDkIsNonNull(declared)) return false;
    type = st->types[st->depth - 1];
    switch (KindOfDeclared(declared)) {
        case KindInt: return IsNarrowIntType(type) && !fgDkKeepsWidth(declared);
        case KindFloat: return KindOfType(type) == KindFloat;
        case KindBool: return KindOfType(type) == KindBool;
        case KindWord: return KindOfType(type) == KindWord;
        default: return false;
    }
}
static bool ScalarAware(int op) {
    switch (op) {
        case OP_LoadConst: case OP_LoadLocal: case OP_StoreLocal: case OP_StoreLocalPop: case OP_DeclareLocal: case OP_Add: case OP_Sub: case OP_Mul: case OP_Mod:
        case OP_Lt: case OP_LtEq: case OP_Gt: case OP_GtEq: case OP_Eq: case OP_NotEq: case OP_JumpIfFalse: case OP_JumpIfNotLt: case OP_JumpIfNotLtEq: case OP_JumpIfNotGt:
        case OP_JumpIfNotGtEq: case OP_JumpIfNotEq: case OP_JumpIfNotNotEq: case OP_Pop: case OP_ReleaseTemps: case OP_EnterScope: case OP_ExitScope: case OP_ClaimToScope:
        case OP_ArithLocalConstPop: case OP_BitAnd: case OP_BitOr: case OP_BitXor: case OP_ShiftLeft: case OP_ShiftRight: case OP_ShiftRightLogical: case OP_ConvertWidth:
        case OP_ArrayGet: case OP_Div: case OP_Neg: case OP_LogicalNot: case OP_CheckUnit: case OP_Dup: case OP_StepAdd: case OP_StepSub: case OP_Trust: case OP_Power:
        case OP_PowerFloat: case OP_CoerceType: case OP_BitNot: case OP_ClaimToArray: case OP_ArraySet: case OP_CopyArgs: case OP_LoadThis: return true;
        default: return false;
    }
}
static bool MarksBorrowed(int op) { return op == OP_LoadGlobal || op == OP_GetStaticField || op == OP_GetField || op == OP_ArrayGet || op == OP_PtrRead; }
/* When the operation may run fire code: how many slots on top of the stack it takes as its operands; -1: it runs no fire code. */
static int RunsFireCode(const FgInstr* ins) {
    switch (ins->op) {
        case OP_CallStaticMethod: return ins->a[2];
        case OP_CallBaseMethod: return ins->a[2];
        case OP_CallMethod: return ins->a[1] + 1;
        case OP_CallProtoWithThis: return ins->a[1] + 1;
        case OP_Call: return ins->a[0];
        case OP_NewObject: return ins->a[1];
        case OP_NewObjectOwned: return ins->a[1] + 1;
        case OP_CallNative: case OP_CallExtern: return ins->a[1];
        default: return -1;
    }
}

static FgStep StepCore(FgGen* g, FgFunc* f, FcChunk* chunk, FgInstr* ins, FgFlow* st, FcBuf* sb, FcDict* locals);
/* Applies the effect of one instruction to `st`; with a builder it also emits the C. */
static FgStep StepFn(FgGen* g, FgFunc* f, FcChunk* chunk, FgInstr* ins, FgFlow* st, FcBuf* sb, FcDict* locals) {
    int before = st->depth, k;
    bool fromNone = false, selfAliveBefore, neutralHere, arraysNeutralHere;
    FgStep result;
    FgEmit line = fgEmitTo(sb, "    ");
    g->kindsKept = false;
    for (k = before - 3 > 0 ? before - 3 : 0; k < (before < 64 ? before : 64); k++) fromNone |= st->types[k] == TypeNone;
    selfAliveBefore = st->selfAlive;
    g->selfAliveAfter = -1;
    g->neutralAfter = false;
    neutralHere = (selfAliveBefore || st->arrayCount > 0) && SelfNeutralHere(ins->op, st->types, before);
    arraysNeutralHere = (st->arrayCount > 0 && ins->op == OP_ArraySet && before >= 3 && before <= 64
            && (st->types[before - 3] & TypeNone) == 0 && BaseKindOfType(st->types[before - 3]) == KindArr
            && st->types[before - 2] != TypeUnknown && IsPlainType(st->types[before - 2]) && st->types[before - 1] != TypeUnknown && IsPlainType(st->types[before - 1]))
        || (st->arrayCount > 0 && ins->op == OP_ClaimToArray && before >= 1 && before <= 64 && st->types[before - 1] != TypeUnknown && IsPlainType(st->types[before - 1]))
        || (st->arrayCount > 0 && ins->op == OP_NewArray);
    result = StepCore(g, f, chunk, ins, st, sb, locals);
    st->borrowed &= st->depth >= 64 ? ~0ULL : (1ULL << (st->depth > 0 ? st->depth : 0)) - 1;
    if (result.fallsThrough && MarksBorrowed(ins->op) && st->depth >= 1 && st->depth <= 64 && ((st->refs >> (st->depth - 1)) & 1ULL) != 0 && ((st->scalar >> (st->depth - 1)) & 1ULL) == 0)
        st->borrowed |= 1ULL << (st->depth - 1);
    if (st->arrayCount > 0 && (ins->op == OP_TypeArray || ins->op == OP_TypeArrayOfParam) && result.fallsThrough) {
        for (k = 0; k < st->arrayCount; k++) if (sb) fcBufFmt(sb, "    %s\n", ArrayCheckText(g, st->arrays[k], fgKnownArrayElem(g, f, st->arrays[k])));
        g->neutralAfter = true;
    }
    if (st->arrayCount > 0 && !(SelfNeutral(ins->op) || neutralHere || arraysNeutralHere || g->neutralAfter)) { st->arrays = NULL; st->arrayCount = 0; }
    st->selfAlive = g->selfAliveAfter >= 0 ? g->selfAliveAfter == 1 : (selfAliveBefore && (SelfNeutral(ins->op) || neutralHere));
    if (g->usesThreads && ins->n > 0 && ins->a[0] <= ins->addr && IsLoopJump(ins->op)) st->selfAlive = false;
    st->scalar &= st->depth >= 64 ? ~0ULL : (1ULL << (st->depth > 0 ? st->depth : 0)) - 1;
    for (k = st->depth > 0 ? st->depth : 0; k < 64; k++) st->alias[k] = NULL;
    st->tempsDirty &= st->scopeCount >= 64 ? ~0ULL : (1ULL << st->scopeCount) - 1;
    {
        int at = f->indexOf[ins->addr] - 1;
        bool nextIsTarget = at >= 0 && at + 1 < f->decoded.n && fgISetHas(g->c, f->targets, f->decoded.p[at + 1].addr);
        if (result.fallsThrough && AnyAlias(st) && nextIsTarget) MaterializeAliases(g, st, &line, NULL);
        if (st->scalar != 0 && result.fallsThrough && nextIsTarget) MaterializeAll(g, f, st, &line, -1, -1);
    }
    if (!g->kindsKept) {
        int from = st->depth > before ? before : st->depth - 1;
        uint64_t keep = st->scalar;
        int kept[64];
        if (keep) memcpy(kept, st->types, sizeof kept);
        fgFlowClearTypes(st, from);
        if (keep) for (k = from > 0 ? from : 0; k < 64; k++) if ((keep >> k) & 1) st->types[k] = kept[k];
        if (fromNone) for (k = from > 0 ? from : 0; k < (st->depth < 64 ? st->depth : 64); k++) st->types[k] = TypeNone;
    }
    for (k = 0; k < result.extraCount; k++) fgFlowClearTypes(result.extra[k].state, 0);
    return result;
}

static bool IntKeyHash(FgGen* g, FgFunc* f, FgFlow* st, const FgInstr* ins) {
    int at, key;
    const FcConst* c;
    if (ins->op != OP_CallNative || ins->a[1] != 2 || ins->a[0] >= g->natives->count || !streq0((const char*)g->natives->items[ins->a[0]], "__MathCall")) return false;
    at = f->indexOf[ins->addr] - 1;
    key = st->depth - 1;
    if (at < 2 || f->decoded.p[at - 2].op != OP_LoadConst) return false;
    c = fgConst(f->chunk, f->decoded.p[at - 2].a[0]);
    if (c->kind != CK_Int || c->i != 15) return false;
    return key >= 0 && key < 64 && ((st->scalar >> key) & 1) != 0 && KindOfType(st->types[key]) == KindInt && UnitOfType(st->types[key]) == 0;
}
static bool TypedNativeCall(FgGen* g, const FgInstr* ins) {
    FgPkgNative* pn;
    const char* native;
    int i;
    if (ins->op != OP_CallNative || ins->a[0] >= g->natives->count || !(pn = (FgPkgNative*)fcDictGet(g->packageNatives, (native = (const char*)g->natives->items[ins->a[0]]))) || pn->argc != ins->a[1]) return false;
    if (!fgNativeIsTyped(pn->fn, pn->isC) || !NativeIsHere(g, fcDictGet(g->nativeImportOf, native) ? (const char*)fcDictGet(g->nativeImportOf, native) : "")) return false;
    if (PkgLock(g, native) >= 0) return false;   /* (its call is a statement between the package's lock) */
    for (i = 0; i < pn->argc; i++) if (!fgNativeParamCType(pn->fn, i)) return false;
    return true;
}
