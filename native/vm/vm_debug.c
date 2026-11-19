/* fire VM in C: the debugger (docs/ROADMAP_C_PORT.md phase B step 6, docs/spec/VM_C.md "Debugger"). The VM speaks the debug protocol of native programs
 * (docs/spec/NATIVE_DEBUG_PROTOCOL.md) with the agent of the runtime (native/runtime/fire_debug.h, FIRE_DEBUG_AGENT): its transports (tcp, stdio framed),
 * its thread, its commands, its values, `set`, the profiler - spark's NativeDebugTarget drives a program of the VM like a native one.
 *
 * What the agent reads is the shadow stack of a native debug build (DbgFrame: function, variables by level, `this`, position). The generated code keeps it
 * all the time; the VM makes it only when a thread stops (vmDbgPark): the logical frames of the C# VM from the VM's frames, each with its scope chain (one
 * DbgLevel per scope, the names of the slots from the DeclareLocal that made them - Scope.DebugNameOf), `this`, the position. While a thread runs, its
 * g_dbgTop is one frame with the position only (what an `unhandled` stop of the runtime's own - a fire thread - shows).
 *
 * The decisions are those of the C# VM's stepping loops (VM.StepLine/StepInto/StepOut/RunUntilBreakpoint), taken before every instruction while a debugger
 * is attached (the hook in vmRun): the position is that of the instruction (Chunk.GetLocation), the depth that of the C# VM's frames. What the C# VM runs
 * nested (getters, setters, operators, GetIndex/SetIndex, destructors, ToString, default values, lambdas a native calls, the constructor of an exception the
 * runtime makes) is hidden: a frame that an instruction other than a call made (or one run nested - FR_NESTED). A catch block (FR_CATCH, run on top of the
 * throw site) is the frame of its function; the frames of the throw site under it are gone for the debugger. */

#define VM_DBG_HIDDEN 1

typedef struct VmDbg {
    DbgFrame cur;            /* g_dbgTop while the strand runs: the position of the innermost visible frame (no function, no variables) */
    uint8_t* hidden;         /* per frame index: run nested (VM_FRAMES) */
    uint16_t* opAt;          /* per frame index: the instruction it runs (seen at its last hook) - what made the frame above it */
    const void** keyProto;   /* per frame index: the frame the marks are for (its function and its scope: a frame that replaced it is looked at anew) */
    const void** keyBase;
    VmScope* declScope;      /* a DeclareLocal ran: the scope, the slot it made, its name (written into the scope at the next instruction) */
    uint32_t declSlot;
    const char* declName;
    /* the chain of visible frames, computed when the frames change */
    int32_t chainN;
    const void* chainKey;
    int chainHidden;
    uint32_t chainDepth;
    /* the position: the range of code where it holds */
    const VmChunk* rangeChunk;
    uint32_t rangeStart, rangeEnd;
    int32_t posSrc, posLine;
    /* the frames as the agent reads them while the strand stands (vmDbgMaterialize) */
    DbgFrame* frames; DbgFunc* fns; uint32_t capFrames;
    DbgLevel* levels; uint32_t capLevels;
    DbgVar* vars; Value** slots; uint32_t capVars;
} VmDbg;

static DbgProgram g_vmDbgProg;
static DbgSource* g_vmDbgSources;
static DbgGlobal* g_vmDbgGlobals;
static uint32_t g_vmDbgGlobalsCap;
static int32_t* g_vmDbgLines;
static Vm* g_vmDbgMain;          /* the program under the debugger (the first one of the process) */
static int g_vmDbgStarted;
static DbgClass* g_vmDbgClasses;  /* by class id - firstId (fire_dbgClass) */
static uint32_t g_vmDbgClassCount;
static Value g_vmDbgUndef;      /* (undefined: what a slot the scope does not have shows) */

/* The location of an offset (Chunk.GetLocationRange): source and line, and the range of offsets where it holds. */
static void vmDbgLocation(const VmChunk* c, uint32_t off, int32_t* src, int32_t* line, uint32_t* start, uint32_t* end) {
    int32_t lo = 0, hi = (int32_t)c->nlines - 1, found = -1;
    while (lo <= hi) {
        int32_t mid = (lo + hi) >> 1;
        if ((uint32_t)c->lines[3 * mid] <= off) { found = mid; lo = mid + 1; }
        else hi = mid - 1;
    }
    if (start) *start = found < 0 ? 0 : (uint32_t)c->lines[3 * found];
    if (end) *end = found + 1 < (int32_t)c->nlines ? (uint32_t)c->lines[3 * (found + 1)] : 0xFFFFFFFFu;
    *src = found < 0 ? 0 : c->lines[3 * found + 1];
    *line = found < 0 ? 0 : c->lines[3 * found + 2];
}

static VmDbg* vmDbgOf(Vm* vm) {
    VmDbg* d = vm->dbg;
    if (d) return d;
    d = (VmDbg*)calloc(1, sizeof(VmDbg));
    if (!d) outOfMemory();
    d->hidden = (uint8_t*)calloc(VM_FRAMES, 1);
    d->opAt = (uint16_t*)calloc(VM_FRAMES, sizeof(uint16_t));
    d->keyProto = (const void**)calloc(VM_FRAMES, sizeof(void*));
    d->keyBase = (const void**)calloc(VM_FRAMES, sizeof(void*));
    if (!d->hidden || !d->opAt || !d->keyProto || !d->keyBase) outOfMemory();
    d->chainN = -1;
    d->cur.lv = -1;
    vm->dbg = d;
    return d;
}

static void vmDbgFree(Vm* vm) {
    VmDbg* d = vm->dbg;
    if (!d) return;
    if (g_dbgTop == &d->cur || (d->frames && g_dbgTop >= d->frames && g_dbgTop < d->frames + d->capFrames)) g_dbgTop = NULL;
    free(d->hidden); free(d->opAt); free(d->keyProto); free(d->keyBase); free(d->frames); free(d->fns); free(d->levels); free(d->vars); free(d->slots);
    free(d);
    vm->dbg = NULL;
}

/* Is a frame that is new since the last instruction run nested? Only the calls of the bytecode make frames the C# VM steps into (`lastOp`: the instruction
 * the frame under it runs). */
static uint8_t vmDbgHiddenFrame(const VmFrame* f, uint32_t lastOp) {
    if (f->kind == FR_TOP || f->kind == FR_CATCH) return 0;
    if (f->kind == FR_NESTED) return VM_DBG_HIDDEN;
    switch (lastOp) {
        case OP_Call: case OP_CallProtoWithThis: case OP_CallMethod: case OP_CallBaseMethod: case OP_CallStaticMethod:
        case OP_NewObject: case OP_NewObjectOwned: case OP_ConstructBase:
            return 0;
        default: return VM_DBG_HIDDEN;
    }
}

/* The visible frames: false when the code runs nested; the depth of the C# VM (its frames without the top level). */
static bool vmDbgChain(Vm* vm, VmDbg* d) {
    int32_t n = vm->nframes, i;
    const void* key = n > 0 ? (const void*)vm->frames[n - 1].base : NULL;
    uint32_t count = 0;
    if (n == d->chainN && key == d->chainKey) return !d->chainHidden;
    d->chainN = n; d->chainKey = key;
    for (i = n - 1; i >= 0; ) {
        VmFrame* f = &vm->frames[i];
        if (f->kind == FR_CATCH) {
            if (d->hidden[f->rec->frame]) { d->chainHidden = 1; return false; }
            count++;
            i = f->rec->frame - 1;
            continue;
        }
        if (d->hidden[i]) { d->chainHidden = 1; return false; }
        count++;
        i--;
    }
    d->chainHidden = 0;
    d->chainDepth = count > 0 ? count - 1 : 0;
    return true;
}

static void vmDbgLevelRoom(VmDbg* d, uint32_t levels, uint32_t vars) {
    if (levels > d->capLevels) {
        uint32_t cap = d->capLevels ? d->capLevels : 16;
        while (cap < levels) cap *= 2;
        d->levels = (DbgLevel*)realloc(d->levels, cap * sizeof(DbgLevel));
        if (!d->levels) outOfMemory();
        d->capLevels = cap;
    }
    if (vars > d->capVars) {
        uint32_t cap = d->capVars ? d->capVars : 64;
        while (cap < vars) cap *= 2;
        d->vars = (DbgVar*)realloc(d->vars, cap * sizeof(DbgVar));
        d->slots = (Value**)realloc(d->slots, cap * sizeof(Value*));
        if (!d->vars || !d->slots) outOfMemory();
        d->capVars = cap;
    }
}

/* The name of a slot (VM.DebugScopeChain): the DeclareLocal that made it, a parameter of the function's top level, else "(local N)". */
static const char* vmDbgSlotName(Vm* vm, VmScope* s, uint32_t slot, bool top, const VmChunk* chunk, bool global) {
    char text[40];
    if (slot < s->nnames && s->names[slot]) return s->names[slot];
    if (top && chunk && slot < chunk->nlocalNames && chunk->localNames[slot]) return chunk->localNames[slot];
    if (global) return NULL;   /* (the agent says "(global N)") */
    snprintf(text, sizeof text, "(local %u)", (unsigned)slot);
    return vmName(vm->prog, text);
}

/* The name of a function for the call stack (CppGenerator.DebugFunctionName). */
static const char* vmDbgFuncName(Vm* vm, const VmFrame* own) {
    const VmProto* p = own->proto;
    const VmClass* c;
    char text[300];
    if (own->kind == FR_TOP) return vm->isStrand ? "<fire>" : "<main>";
    if (!p) return "?";
    c = p->declaring ? p->declaring : p->owner;
    if (own->kind == FR_CTOR || own->kind == FR_BASECTOR) {
        snprintf(text, sizeof text, "%s.construct", c ? c->name : "?");
        return vmName(vm->prog, text);
    }
    if (!p->name) return "<lambda>";
    if (!c) return p->name;
    snprintf(text, sizeof text, "%s.%s", c->name, p->name);
    return vmName(vm->prog, text);
}

/* The shadow stack of a native build for the agent, made from the VM's frames while the strand stands: innermost first (g_dbgTop), its scope chain as
 * levels (innermost block first, `functionTop` where the parent is the global scope); the bottom frame of a fire thread with the level of its own globals
 * (functionTop 2). */
static DbgFrame* vmDbgMaterialize(Vm* vm, VmDbg* d) {
    int32_t i, nl = 0;
    uint32_t count = 0, k, nlev = 0, nvar = 0;
    VmScope* s;
    VmFrame* inner;
    for (i = vm->nframes - 1; i >= 0; count++) i = vm->frames[i].kind == FR_CATCH ? vm->frames[i].rec->frame - 1 : i - 1;
    if (count == 0) return NULL;
    if (count > d->capFrames) {
        d->frames = (DbgFrame*)realloc(d->frames, count * sizeof(DbgFrame));
        d->fns = (DbgFunc*)realloc(d->fns, count * sizeof(DbgFunc));
        if (!d->frames || !d->fns) outOfMemory();
        d->capFrames = count;
    }
    inner = &vm->frames[vm->nframes - 1];
    /* the levels: the scope chain of the innermost frame, then (a fire thread) the level of its own globals */
    for (s = inner->scope; s && s != vm->global; s = s->parent) { nlev++; nvar += s->n; }
    if (vm->isStrand) { nlev++; nvar += vm->global->n > vm->sharedCount ? vm->global->n - vm->sharedCount : 0; }
    vmDbgLevelRoom(d, nlev + 1, nvar + 1);
    nvar = 0;
    for (s = inner->scope; s && s != vm->global; s = s->parent) {
        DbgLevel* lv = &d->levels[nl];
        bool top = s->parent == vm->global;
        lv->parent = -1;
        lv->functionTop = top ? 1 : 0;
        lv->count = (uint16_t)s->n;
        lv->vars = &d->vars[nvar];
        for (k = 0; k < s->n; k++) {
            d->vars[nvar].name = vmDbgSlotName(vm, s, k, top, inner->chunk, false);
            d->vars[nvar].index = (uint16_t)nvar;
            d->slots[nvar] = k < s->hi ? &s->slots[k] : (Value*)&g_vmDbgUndef;
            nvar++;
        }
        if (nl > 0) d->levels[nl - 1].parent = nl;
        nl++;
    }
    if (vm->isStrand) {
        DbgLevel* lv = &d->levels[nl];
        VmScope* g = vm->global;
        lv->parent = -1;
        lv->functionTop = 2;
        lv->count = (uint16_t)(g->n > vm->sharedCount ? g->n - vm->sharedCount : 0);
        lv->vars = &d->vars[nvar];
        for (k = vm->sharedCount; k < g->n; k++) {
            d->vars[nvar].name = vmDbgSlotName(vm, g, k, false, NULL, false);
            d->vars[nvar].index = (uint16_t)nvar;
            d->slots[nvar] = k < g->hi ? &g->slots[k] : (Value*)&g_vmDbgUndef;
            nvar++;
        }
        if (nl > 0 && count == 1) d->levels[nl - 1].parent = nl;
        nl++;
    }
    /* the frames */
    {
        uint32_t j = 0;
        for (i = vm->nframes - 1; i >= 0; j++) {
            VmFrame* f = &vm->frames[i];
            VmFrame* own = f->kind == FR_CATCH ? &vm->frames[f->rec->frame] : f;
            DbgFrame* df = &d->frames[j];
            DbgFunc* fn = &d->fns[j];
            uint32_t off = (uint32_t)(f->ip - f->chunk->code);
            int32_t src = 0, line = 0;
            if (f->ip >= f->chunk->code && off <= f->chunk->ncode && f->chunk->lines) vmDbgLocation(f->chunk, j == 0 || off == 0 ? off : off - 1, &src, &line, NULL, NULL);
            fn->name = vmDbgFuncName(vm, own);
            fn->levels = NULL;
            df->fn = fn;
            df->vars = NULL;
            df->lv = -1;
            df->thisSlot = &f->self;
            df->src = src; df->line = line;
            df->depth = count - 1 - j;
            df->hidden = 0;
            df->prev = j + 1 < count ? &d->frames[j + 1] : NULL;
            if (nl > 0 && j == 0) { fn->levels = d->levels; df->vars = (Value* const*)d->slots; df->lv = inner->scope != vm->global ? 0 : (vm->isStrand ? nl - 1 : -1); }
            else if (vm->isStrand && j + 1 == count) { fn->levels = d->levels; df->vars = (Value* const*)d->slots; df->lv = nl - 1; }
            i = f->kind == FR_CATCH ? f->rec->frame - 1 : i - 1;
        }
    }
    return &d->frames[0];
}

/* The globals of the program for the agent (VM.DebugGlobals: the slots declared so far, with the names of their declarations). Under the GIL. */
static void vmDbgGlobals(void) {
    VmScope* g;
    uint32_t i;
    if (!g_vmDbgMain) return;
    g = g_vmDbgMain->global;
    if (g->n > g_vmDbgGlobalsCap) {
        uint32_t cap = g_vmDbgGlobalsCap ? g_vmDbgGlobalsCap : 64;
        while (cap < g->n) cap *= 2;
        g_vmDbgGlobals = (DbgGlobal*)realloc(g_vmDbgGlobals, cap * sizeof(DbgGlobal));
        if (!g_vmDbgGlobals) outOfMemory();
        g_vmDbgGlobalsCap = cap;
    }
    for (i = 0; i < g->n; i++) {
        g_vmDbgGlobals[i].name = vmDbgSlotName(g_vmDbgMain, g, i, false, NULL, true);
        g_vmDbgGlobals[i].slot = i < g->hi ? &g->slots[i] : NULL;
    }
    g_vmDbgProg.globals = g_vmDbgGlobals;
    g_vmDbgProg.nglobals = (int32_t)g->n;
    g_dbgGlobalCount = (int32_t)g->n;
}

/* The thread stops (the agent's dbgPark) with the frames made for the agent. */
static void vmDbgPark(Vm* vm, VmDbg* d, DbgThread* t, const char* reason) {
    DbgFrame* top = vmDbgMaterialize(vm, d);
    vmDbgGlobals();
    g_dbgTop = top ? top : &d->cur;
    dbgPark(t, reason, 1);
    g_dbgTop = &d->cur;
    d->chainN = -1;   /* (a `set` may have run code: the frames are looked at again) */
}

/* The runtime is about to throw an exception it made (vmMakeException): until the next instruction, g_dbgTop is the strand's frames - a fire thread that
 * the runtime's agent stops at it (fire_dbgUnhandled: nothing of the thread catches it) shows its call stack and its scopes. */
static void vmDbgSnapshot(Vm* vm) {
    VmDbg* d;
    DbgFrame* top;
    if (!g_dbgSelf || g_dbgNest || vm->nframes <= 0) return;
    d = vmDbgOf(vm);
    d->chainN = -1;
    if (!vmDbgChain(vm, d)) return;
    top = vmDbgMaterialize(vm, d);
    vmDbgGlobals();
    if (top) g_dbgTop = top;
}

/* Would a catch block take the exception (VM.WouldBeCaught) - the bottom of the script does not count? */
static void vmUnhandledFn(void* ctx, uint32_t ci, Value e);
static bool vmDbgWouldBeCaught(Value exception) {
    Handler* h;
    uint32_t i;
    for (h = g_handlers; h; h = h->prev) {
        if (h->finallyOnly || h->fn == vmUnhandledFn) continue;
        for (i = 0; i < h->info->ncatch; i++)
            if (h->info->types[i] < 0 || excMatches(exception, FIRE_HANDLER_TYPE(h->info, i))) return true;
    }
    return false;
}

/* An exception that nothing catches: the thread stops at the throw site (fire_dbgUnhandled). True: the debugger ignores it (`resume`). */
static bool vmDbgStopUnhandled(Vm* vm, VmDbg* d, DbgThread* t, Value exception) {
    bool resumed;
    if (exception.p == t->declined) return false;
    t->exc = exception; t->hasExc = 1; t->resumeExc = 0;
    vmDbgPark(vm, d, t, "unhandled");
    resumed = t->resumeExc != 0;
    t->hasExc = 0; t->resumeExc = 0;
    if (!resumed) t->declined = exception.p;
    return resumed;
}

/* The hook before every instruction while a debugger is attached (vmRun): the names of declared slots, the frames that are run nested, the profiler,
 * breakpoints, steps, pause, an unhandled `throw`. The frame's ip and the stack are synced; the strand may stand here (the GIL released). */
static FIRE_COLD void vmDbgStep(Vm* vm) {
    VmDbg* d = vmDbgOf(vm);
    DbgThread* t = g_dbgSelf;
    VmFrame* fr = &vm->frames[vm->nframes - 1];
    VmChunk* c = fr->chunk;
    uint32_t off, op;
    int32_t k;
    const char* reason = NULL;
    if (d->declScope) {
        /* (the slot the DeclareLocal made gets its name: Scope.SetDebugName) */
        VmScope* s = d->declScope;
        d->declScope = NULL;
        if (s->n > d->declSlot) {
            if (d->declSlot >= s->capNames) {
                uint32_t cap = s->capNames ? s->capNames * 2 : 8;
                while (cap <= d->declSlot) cap *= 2;
                s->names = (const char**)realloc(s->names, cap * sizeof(char*));
                if (!s->names) outOfMemory();
                s->capNames = cap;
            }
            while (s->nnames < d->declSlot) s->names[s->nnames++] = NULL;
            s->names[d->declSlot] = d->declName;
            if (s->nnames <= d->declSlot) s->nnames = d->declSlot + 1;
        }
    }
    /* the frames that are new (from the top down to the first one already seen): run nested unless a call of the frame under them made them */
    for (k = vm->nframes - 1; k >= 0; k--) {
        VmFrame* f = &vm->frames[k];
        if (d->keyProto[k] == (const void*)f->proto && d->keyBase[k] == (const void*)f->base) break;
        d->keyProto[k] = f->proto; d->keyBase[k] = f->base;
        d->hidden[k] = vmDbgHiddenFrame(f, k > 0 ? d->opAt[k - 1] : 0xFFFFu);
        d->chainN = -1;
    }
    if (fr->ip < c->code || fr->ip >= c->code + c->ncode) { d->opAt[vm->nframes - 1] = 0xFFFFu; return; }   /* (the stub of an unwinding) */
    off = (uint32_t)(fr->ip - c->code);
    op = *fr->ip;
    if (t && !g_dbgNest && vmDbgChain(vm, d)) {
        int32_t src, line;
        uint32_t depth = d->chainDepth;
        g_dbgTop = &d->cur;   /* (not a snapshot of vmDbgSnapshot any more) */
        if (c != d->rangeChunk || off < d->rangeStart || off >= d->rangeEnd) {
            if (c->lines) vmDbgLocation(c, off, &d->posSrc, &d->posLine, &d->rangeStart, &d->rangeEnd);
            else { d->posSrc = 0; d->posLine = 0; d->rangeStart = 0; d->rangeEnd = 0xFFFFFFFFu; }
            d->rangeChunk = c;
        }
        src = d->posSrc; line = d->posLine;
        d->cur.src = src; d->cur.line = line; d->cur.depth = depth;
        if (g_dbgProfile && (src != t->profSrc || line != t->profLine)) dbgProfHit(t, src, line, off == d->rangeStart);
        if (t->pauseReq) reason = "pause";
        else switch (t->mode) {
            case DBG_RUN:
                if (t->fresh || src != t->lastSrc || line != t->lastLine) {
                    int fresh = t->fresh;
                    t->fresh = 0;
                    t->lastSrc = src; t->lastLine = line;
                    if (!fresh && dbgBreakpointAt(src, line)) reason = "breakpoint";
                }
                break;
            case DBG_INTO: if (line != t->startLine || depth != t->startDepth) reason = "step"; break;
            case DBG_OVER: if (depth <= t->startDepth && line != t->startLine) reason = "step"; break;
            case DBG_OUT: if (depth < t->startDepth) reason = "step"; break;
            default: break;
        }
        if (reason) vmDbgPark(vm, d, t, reason);
        if (op == OP_Throw && g_dbgArmed && vm->sp > 0) {
            /* `throw` of an exception that nothing will catch: the thread stops before anything is unwound (also past a finally, VM.PauseOnUnhandled);
             * `resume` ignores it - the throw gives undefined */
            Value x = vm->stack[vm->sp - 1];
            if (x.kind == K_Class && leafAlive(x) && !vmDbgWouldBeCaught(x) && vmDbgStopUnhandled(vm, d, t, x)) {
                vm->stack[vm->sp - 1] = Undef();
                fr->ip++;
                op = fr->ip < c->code + c->ncode ? *fr->ip : 0xFFFFFFFFu;
                off++;
            }
        }
    }
    d->opAt[vm->nframes - 1] = (uint16_t)op;
    if (op == OP_DeclareLocal) {
        d->declScope = fr->scope;
        d->declSlot = fr->scope->n;
        d->declName = off < c->ndeclNames ? c->declNames[off] : NULL;
    }
}

/* The bottom of the script took an exception (vmUnhandledFn, on top of the throw site): the thread stops there. True: `resume` - the throw gives undefined. */
static bool vmDbgUnhandled(Value exception) {
    Vm* vm = g_vmCur;
    VmDbg* d;
    DbgThread* t = g_dbgSelf;
    if (!vm || !t || g_dbgNest || !g_dbgArmed || exception.kind != K_Class) return false;
    d = vmDbgOf(vm);
    d->chainN = -1;
    if (!vmDbgChain(vm, d)) return false;
    if (!vmDbgStopUnhandled(vm, d, t, exception)) return false;
    if (!g_pending || !g_pending->resumable || !sameRef(g_pending->exception, exception)) return false;   /* (a rethrow: it cannot be resumed) */
    resumeThrow(exception, Undef());
    return !g_unwind.active || g_unwind.kind == UW_RESUME;
}

/* ---- what the agent asks the program (fire_debug.h: generated in a native build) ---- */
const DbgClass* fire_dbgClass(uint32_t cls) {
    Vm* vm = g_vmDbgMain;
    VmClass* c;
    if (!vm || cls < vm->firstId || cls - vm->firstId >= vm->nids) return NULL;
    if (!g_vmDbgClasses) {
        g_vmDbgClasses = (DbgClass*)calloc(vm->nids + 1, sizeof(DbgClass));
        if (!g_vmDbgClasses) return NULL;
        g_vmDbgClassCount = vm->nids;
    }
    if (cls - vm->firstId >= g_vmDbgClassCount) return NULL;
    c = vm->byId[cls - vm->firstId];
    if (!c) return NULL;
    {
        DbgClass* dc = &g_vmDbgClasses[cls - vm->firstId];
        if (!dc->name) { dc->name = c->name; dc->nfields = c->nlayout; dc->fields = c->layout; dc->places = NULL; }
        return dc;
    }
}
/* `set` of a field: like `obj.name = value` in the program (its unit, kind, type, a record, the probes - VM.DebugAssignMember). */
void fire_dbgSetField(Value o, uint32_t index, Value x) {
    Vm* vm = g_vmCur;
    VmClass* c = vm ? vmClassOfValue(vm, o) : NULL;
    if (!c || index >= c->nlayout) { fireError("ArgumentException", "The object has no such field."); return; }
    (void)vmReflWrite(vm, NULL, o, c->layout[index], x);
}

/* ---- the start and the end ---- */
static int vmDbgLineCmp(const void* a, const void* b) {
    const int32_t* x = (const int32_t*)a;
    const int32_t* y = (const int32_t*)b;
    return x[0] != y[0] ? (x[0] < y[0] ? -1 : 1) : x[1] != y[1] ? (x[1] < y[1] ? -1 : 1) : 0;
}

/* The program starts (after its first frame is made, with the GIL): FIRE_DEBUG (or `fire --debug`) connects to the debugger, says hello and waits at the
 * entry. Only the first program of the process is debugged. */
static void vmDbgBegin(Vm* vm) {
    VmProgram* p = vm->prog;
    uint32_t i, k, n = 0, nsources;
    int32_t maxSrc = p->firstUserSource;
    if (g_vmDbgStarted || !getenv("FIRE_DEBUG")) return;
    g_vmDbgStarted = 1;
    g_vmDbgMain = vm;
    /* the lines with code (the profiler reports those that never ran too) */
    for (i = 0; i < p->nchunks; i++) n += p->chunks[i]->nlines;
    g_vmDbgLines = (int32_t*)malloc((2 * (size_t)n + 2) * sizeof(int32_t));
    if (!g_vmDbgLines) outOfMemory();
    n = 0;
    for (i = 0; i < p->nchunks; i++)
        for (k = 0; k < p->chunks[i]->nlines; k++) {
            int32_t src = p->chunks[i]->lines[3 * k + 1], line = p->chunks[i]->lines[3 * k + 2];
            if (line <= 0) continue;
            if (src > maxSrc) maxSrc = src;
            g_vmDbgLines[2 * n] = src; g_vmDbgLines[2 * n + 1] = line; n++;
        }
    qsort(g_vmDbgLines, n, 2 * sizeof(int32_t), vmDbgLineCmp);
    for (i = 0, k = 0; i < n; i++)
        if (k == 0 || g_vmDbgLines[2 * i] != g_vmDbgLines[2 * (k - 1)] || g_vmDbgLines[2 * i + 1] != g_vmDbgLines[2 * (k - 1) + 1]) {
            g_vmDbgLines[2 * k] = g_vmDbgLines[2 * i]; g_vmDbgLines[2 * k + 1] = g_vmDbgLines[2 * i + 1]; k++;
        }
    g_vmDbgProg.lines = g_vmDbgLines;
    g_vmDbgProg.nlines = (int32_t)k;
    /* the sources: those the file names (a debug build), else as many as the lines name, without files */
    nsources = p->nsources ? p->nsources : (uint32_t)maxSrc + 1;
    g_vmDbgSources = (DbgSource*)calloc(nsources + 1, sizeof(DbgSource));
    if (!g_vmDbgSources) outOfMemory();
    for (i = 0; i < p->nsources; i++) { g_vmDbgSources[i].file = p->sourceFiles[i]; g_vmDbgSources[i].hash = p->sourceHashes[i]; }
    g_vmDbgProg.sources = g_vmDbgSources;
    g_vmDbgProg.nsources = (int32_t)nsources;
    g_vmDbgProg.firstUser = p->firstUserSource;
    g_vmDbgProg.globals = NULL;
    g_vmDbgProg.nglobals = 0;
    g_vmDbgProg.entrySrc = 0; g_vmDbgProg.entryLine = 0;
    if (p->top->lines) vmDbgLocation(p->top, 0, &g_vmDbgProg.entrySrc, &g_vmDbgProg.entryLine, NULL, NULL);
#if defined(_WIN32)
    g_vmDbgProg.target = "windows";
#elif defined(__APPLE__)
    g_vmDbgProg.target = "macos";
#else
    g_vmDbgProg.target = "linux";
#endif
    g_vmDbgUndef = Undef();
    vmDbgOf(vm);
    fire_dbgStart(&g_vmDbgProg);
}

/* The program under the debugger has ended (everything it had is gone): `profile`, `exited`. */
static void vmDbgEnd(Vm* vm, int code) {
    if (vm != g_vmDbgMain) return;
    fire_dbgExited(code);
    g_dbgTop = NULL;
    vmDbgFree(vm);
    g_vmDbgMain = NULL;
    free(g_vmDbgGlobals); g_vmDbgGlobals = NULL; g_vmDbgGlobalsCap = 0;
    g_vmDbgProg.globals = NULL; g_vmDbgProg.nglobals = 0; g_dbgGlobalCount = 0;
    free(g_vmDbgClasses); g_vmDbgClasses = NULL; g_vmDbgClassCount = 0;
}
