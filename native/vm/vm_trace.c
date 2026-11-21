/* fire VM in C: stack traces of exceptions (docs/spec/STACKTRACE.md section 8).
 *
 * The runtime (fire_rt.h) holds a trace on the exception object and formats it; the VM gives it the pairs (function, line) at the first throw of an object
 * (fire_vmTraceThrow, the hook of throwValueR behind FIRE_VM): its frames from the top down, a catch block as the function that owns its `try` (the frames
 * of the throw site above that function are skipped), the line of each frame at the instruction it runs (the top) or the call it made (the others) from
 * the line tables of its chunk. The names of the functions follow TraceNames (src/fire/Bytecode/TraceNames.cs) - the same as the VM in C#: `<main>`,
 * `Class.Method`, `Class.construct`, `Class.destruct`, `Class.<init>`, `<lambda>`, and `<fire>` for the base of a fire thread. */

/* A class as a trace shows it: without the arity of a generic class (`List`1`) or type arguments (TraceNames.ClassDisplay). */
static const char* vmTraceClass(VmProgram* p, const char* name) {
    size_t cut = strcspn(name, "`<");
    return cut > 0 && name[cut] ? vmIntern(p, name, cut) : name;
}
static const char* vmTraceJoin(VmProgram* p, const char* cls, const char* member) {
    size_t a = strlen(cls), b = strlen(member);
    char small[256];
    char* t = a + b + 2 <= sizeof small ? small : (char*)malloc(a + b + 2);
    const char* r;
    if (!t) outOfMemory();
    memcpy(t, cls, a); t[a] = '.'; memcpy(t + a + 1, member, b);
    r = vmIntern(p, t, a + 1 + b);
    if (t != small) free(t);
    return r;
}
static void vmTraceName(VmProto* f, const char* name) {
    uint32_t i;
    if (!f) return;
    if (!f->chunk->traceName) f->chunk->traceName = name;
    for (i = 0; i < f->nspecs; i++) if (f->specs[i] && !f->specs[i]->chunk->traceName) f->specs[i]->chunk->traceName = name;
}
static void vmTraceDefaults(VmProto* f, const char* init) {
    uint32_t i;
    if (!f) return;
    for (i = 0; i < f->ndefaults; i++) if (f->defaults[i] && !f->defaults[i]->chunk->traceName) f->defaults[i]->chunk->traceName = init;
}

/* The names of the functions and their descriptors (FireFnInfo: name and file, one per source a chunk has lines in). */
static void vmTraceNames(VmProgram* p) {
    uint32_t i, j, k;
    p->top->traceName = "<main>";
    for (i = 0; i < p->nclasses; i++) {
        VmClass* c = p->classes[i];
        const char* cls = vmTraceClass(p, c->name);
        for (j = 0; j < c->nmethods; j++) {
            const char* name = vmTraceJoin(p, cls, c->methods[j].name);
            for (k = 0; k < c->methods[j].nov; k++) vmTraceName(c->methods[j].ov[k], name);
        }
        for (j = 0; j < c->nctors; j++) vmTraceName(c->ctors[j].p, vmTraceJoin(p, cls, "construct"));
        for (j = 0; j < c->nxctors; j++) vmTraceName(c->xctors[j], vmTraceJoin(p, cls, "construct"));
        if (c->dtor) vmTraceName(c->dtor, vmTraceJoin(p, cls, "destruct"));
    }
    /* (the initializers after every member: a member that is also a default value keeps its own name) */
    for (i = 0; i < p->nclasses; i++) {
        VmClass* c = p->classes[i];
        const char* init = vmTraceJoin(p, vmTraceClass(p, c->name), "<init>");
        for (j = 0; j < c->nfields; j++) vmTraceName(c->fields[j].init, init);
        for (j = 0; j < c->nstatic; j++) if (c->sinit) vmTraceName(c->sinit[j], init);
        for (j = 0; j < c->nmethods; j++) for (k = 0; k < c->methods[j].nov; k++) vmTraceDefaults(c->methods[j].ov[k], init);
        for (j = 0; j < c->nctors; j++) vmTraceDefaults(c->ctors[j].p, init);
        for (j = 0; j < c->nxctors; j++) vmTraceDefaults(c->xctors[j], init);
    }
    /* the descriptors: per chunk one for each source of its line table, and the last one for a place without a line (the prelude's: no file) */
    for (i = 0; i < p->nchunks; i++) {
        VmChunk* ch = p->chunks[i];
        uint32_t n = 0;
        const char* name = ch->traceName ? ch->traceName : "<lambda>";
        ch->traceSrcs = (int32_t*)vmAlloc(&p->arena, (ch->nlines + 1) * sizeof(int32_t));
        for (j = 0; j < ch->nlines; j++) {
            int32_t src = ch->lines[3 * j + 1];
            for (k = 0; k < n && ch->traceSrcs[k] != src; k++) { }
            if (k == n) ch->traceSrcs[n++] = src;
        }
        /* (a lambda has them twice: the second half as `<fire>`, the base of a fire thread) */
        ch->traceFns = (FireFnInfo*)vmAlloc(&p->arena, 2 * (n + 1) * sizeof(FireFnInfo));
        for (j = 0; j <= n; j++) {
            int32_t src = j < n ? ch->traceSrcs[j] : -1;
            ch->traceFns[j].name = name;
            ch->traceFns[j].file = src >= 0 && (uint32_t)src < p->ntraceFiles ? p->traceFiles[src] : NULL;
            ch->traceFns[n + 1 + j].name = ch->traceName ? name : "<fire>";
            ch->traceFns[n + 1 + j].file = ch->traceFns[j].file;
        }
        ch->ntraceFns = n;
    }
}

/* The descriptor of a chunk at a source (the one without a file if the source is not one of its lines); `base`: the base frame of a fire thread. */
static const FireFnInfo* vmTraceFn(const VmChunk* c, int32_t src, bool base) {
    uint32_t i, off = base ? c->ntraceFns + 1 : 0;
    if (!c->traceFns) return NULL;
    for (i = 0; i < c->ntraceFns; i++) if (c->traceSrcs[i] == src) return &c->traceFns[off + i];
    return &c->traceFns[off + c->ntraceFns];
}
static void vmTraceLocation(const VmChunk* c, uint32_t off, int32_t* src, int32_t* line) {
    int32_t lo = 0, hi = (int32_t)c->nlines - 1, found = -1;
    while (lo <= hi) {
        int32_t mid = (lo + hi) >> 1;
        if ((uint32_t)c->lines[3 * mid] <= off) { found = mid; lo = mid + 1; }
        else hi = mid - 1;
    }
    *src = found < 0 ? 0 : c->lines[3 * found + 1];
    *line = found < 0 ? 0 : c->lines[3 * found + 2];
}
/* The frame below frame `i` in the trace: a catch block stands for the function that owns its `try` (the frames of the throw site between are left out). */
static int32_t vmTraceBelow(const Vm* vm, int32_t i) {
    const VmFrame* f = &vm->frames[i];
    if (f->kind == FR_CATCH && f->rec && f->rec->frame < i) return f->rec->frame - 1;
    return i - 1;
}
static FireTrace* vmTraceCapture(Vm* vm) {
    uint32_t total = 0, k = 0;
    int32_t i;
    FireTrace* t;
    for (i = vm->nframes - 1; i >= 0; i = vmTraceBelow(vm, i)) total++;
    t = fire_traceNew(total, vm->threadTrailer);
    for (i = vm->nframes - 1; i >= 0; i = vmTraceBelow(vm, i), k++) {
        FireTraceFrame* slot = fire_traceSlot(t, k);
        const VmFrame* f = &vm->frames[i];
        int32_t src, line;
        const FireFnInfo* fn;
        if (!slot) continue;
        vmTraceLocation(f->chunk, f->ip > f->chunk->code ? (uint32_t)(f->ip - f->chunk->code) - 1 : 0, &src, &line);
        fn = vmTraceFn(f->chunk, src, k == total - 1 && vm->isStrand);   /* (the base of a thread: the body of `fire { }` - TraceNames.Fire) */
        slot->fn = fn;
        slot->line = fn && fn->file ? line : 0;
    }
    return t;
}

void fire_vmTraceThrow(Obj* o) {
    Vm* vm = g_vmCur;
    if (!vm || vm->prog->stackTrace == FIRE_TRACE_OFF || fire_traceOf(o) || vm->nframes <= 0) return;
    fire_traceSet(o, vmTraceCapture(vm));
}
void fire_vmTraceAwaited(Obj* o) {
    Vm* vm = g_vmCur;
    if (!vm || vm->prog->stackTrace == FIRE_TRACE_OFF || vm->nframes <= 0) return;
    fire_traceAwaited(o, vmTraceCapture(vm));
}

/* The line that ends the traces of a thread that `fire` starts now (fr: the frame of the `fire`, synced): `in thread 'Fire #1', started at main.fire:40`. */
static char* vmTraceThreadLine(Vm* vm, VmFrame* fr) {
    VmProgram* p = vm->prog;
    int32_t src, line;
    const char* file;
    char where[512], text[640];
    size_t n;
    char* r;
    unsigned number;
    if (p->stackTrace == FIRE_TRACE_OFF) return NULL;
    number = (unsigned)fire_atomic_add(&p->nthreads, 1) + 1;
    vmTraceLocation(fr->chunk, fr->ip > fr->chunk->code ? (uint32_t)(fr->ip - fr->chunk->code) - 1 : 0, &src, &line);
    file = src >= 0 && (uint32_t)src < p->ntraceFiles ? p->traceFiles[src] : NULL;
    if (!file) snprintf(where, sizeof where, "prelude");
    else if (p->stackTrace == FIRE_TRACE_LINES && line > 0) snprintf(where, sizeof where, "%s:%ld", file, (long)line);
    else snprintf(where, sizeof where, "%s", file);
    snprintf(text, sizeof text, "in thread 'Fire #%u', started at %s", number, where);
    n = strlen(text);
    r = (char*)malloc(n + 1);
    if (r) memcpy(r, text, n + 1);
    return r;
}
