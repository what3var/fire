/* fire VM in C (spike): the interpreter. It runs the bytecode of a loaded program (vm_load.c) on the runtime of the native backend: every operation is the
 * function the generated C calls for it (add, divide, opEq, concat, print, newObject, leave, claimToList, throwValue, resumeThrow, ...), the frames, the
 * scopes with their dynamic slots (BYTECODE 3) and the dispatch are the VM's own.
 *
 * Exceptions: the runtime runs a catch block on top of the throw site (a callback, Handler.fn); here the callback runs the catch block in a nested run of
 * the interpreter on a pseudo frame of the function that owns the try. Leaving the catch (its scope ends, the jump behind the try), `return` in it and
 * `resume` set the runtime's unwinding state (exitJump, exitReturn, resumeThrow) - the interpreter then unwinds its frames to the frame of the target, like
 * the generated code returns through its functions. A handler at the bottom of every script catches what nothing else catches: the script ends there, its
 * process does not (L6). */

enum {
#define FIRE_OP(name, number, operands) OP_##name = number,
#include "../tables/opcodes.def"
#undef FIRE_OP
    OP_COUNT_
};
static const char* const g_vmOpNames[] = {
#define FIRE_OP(name, number, operands) #name,
#include "../tables/opcodes.def"
#undef FIRE_OP
};
static const char* const g_vmNativeNames[] = {
#define FIRE_NATIVE(index, name, tryable) name,
#include "../tables/natives.def"
#undef FIRE_NATIVE
};

/* The VM whose program runs (the runtime's callbacks - destructors, catch blocks, the exceptions it makes - have no context of their own). */
static Vm* g_vmCur = NULL;

static void vmRun(Vm* vm, int32_t base);
static int vmUnwind(Vm* vm, int32_t base);

FIRE_NORETURN static void vmUnsupported(const char* what, const char* name) {
    char text[300];
    snprintf(text, sizeof text, "the VM spike does not support %s%s%s", what, name ? " " : "", name ? name : "");
    fatal(text);
}

/* ---- scopes ---- */
static VmScope* scNew(Vm* vm, VmScope* parent) {
    VmScope* s = vm->freeScopes;
    if (s) vm->freeScopes = s->nextFree;
    else {
        s = (VmScope*)calloc(1, sizeof(VmScope));
        if (!s) outOfMemory();
    }
    s->parent = parent;
    s->n = 0;
    s->own = scopeList(poolMark());
    s->temp = scopeList(0xFFFFFFFFu);
    s->temp.home = &s->own;
    return s;
}
static void scReserve(VmScope* s, uint32_t n) {
    if (n <= s->cap) return;
    {
        uint32_t cap = s->cap ? s->cap * 2 : 8;
        while (cap < n) cap *= 2;
        s->slots = (Value*)realloc(s->slots, cap * sizeof(Value));
        if (!s->slots) outOfMemory();
        s->cap = cap;
    }
}
static void scDefine(VmScope* s, Value v) {
    if (s->n == s->cap) scReserve(s, s->n + 1);
    retain(v);
    s->slots[s->n++] = v;
}
/* The scope ends: what it owns is destroyed (destructors run), its temporaries and slots let go. */
static void scLeave(Vm* vm, VmScope* s) {
    uint32_t i;
    leave(&s->temp);
    leave(&s->own);
    for (i = 0; i < s->n; i++) release(s->slots[i]);
    s->n = 0;
    s->nextFree = vm->freeScopes;
    vm->freeScopes = s;
}
static VmScope* scAncestor(VmScope* s, uint32_t depth) { while (depth--) s = s->parent; return s; }

/* ---- classes as the runtime sees them ---- */
static VmClass* vmClassNamed(VmProgram* p, const char* name) {
    uint32_t i;
    for (i = 0; i < p->nclassNames; i++) if (p->classNames[i] == name) return p->classByName[i];
    return NULL;
}
static VmClass* vmClassOf(Vm* vm, uint32_t cls) { return cls >= vm->firstId && cls - vm->firstId < vm->nids ? vm->byId[cls - vm->firstId] : NULL; }

static void vmLayout(VmProgram* p, VmClass* c) {
    uint32_t i, n;
    if (c->layout || (c->nfields == 0 && !c->base)) { if (!c->layout) c->layout = (const char**)vmAlloc(&p->arena, sizeof(char*)); return; }
    if (c->base) vmLayout(p, c->base);
    n = (c->base ? c->base->nlayout : 0) + c->nfields;
    c->layout = (const char**)vmAlloc(&p->arena, (n + 1) * sizeof(char*));
    c->nlayout = 0;
    if (c->base) for (i = 0; i < c->base->nlayout; i++) c->layout[c->nlayout++] = c->base->layout[i];
    for (i = 0; i < c->nfields; i++) {
        uint32_t k;
        bool dup = false;
        for (k = 0; k < c->nlayout; k++) if (c->layout[k] == c->fields[i].name) dup = true;
        if (!dup) c->layout[c->nlayout++] = c->fields[i].name;
    }
}
static int32_t vmFieldIndex(VmClass* c, const char* name) {
    uint32_t i;
    for (i = 0; i < c->nlayout; i++) if (c->layout[i] == name) return (int32_t)i;
    return -1;
}
static VmProto* vmFindMethod(VmClass* c, const char* name, int argc) {
    for (; c; c = c->base) {
        uint32_t i, k;
        for (i = 0; i < c->nmethods; i++) {
            VmMethod* m = &c->methods[i];
            if (m->name != name) continue;
            for (k = 0; k < m->nov; k++) if (m->ov[k]->nparams == argc) return m->ov[k];
            for (k = 0; k < m->nov; k++) if (m->ov[k]->nparams > argc && (int32_t)m->ov[k]->ndefaults >= m->ov[k]->nparams) return m->ov[k];
        }
    }
    return NULL;
}
static VmProto* vmFindCtor(VmClass* c, int argc) {
    uint32_t i;
    for (i = 0; i < c->nctors; i++) if (c->ctors[i].arity == argc) return c->ctors[i].p;
    for (i = 0; i < c->nctors; i++) if (c->ctors[i].arity > argc && c->ctors[i].p->ndefaults >= (uint32_t)c->ctors[i].arity) return c->ctors[i].p;
    return NULL;
}
static int32_t vmStaticIndex(VmClass** owner, const char* name) {
    VmClass* c;
    for (c = *owner; c; c = c->base) {
        uint32_t i;
        for (i = 0; i < c->nstatic; i++) if (c->snames[i] == name) { *owner = c; return (int32_t)i; }
    }
    return -1;
}

/* ---- calls ---- */
static Value vmCallNested(Vm* vm, VmProto* p, int argc, const Value* args, Value self, uint8_t kind);

/* Pushes the frame of a call of `p`: its arguments are the top `argc` values of the stack (they become slots 0.. of its scope; the callee holds its own -
 * retain, like the generated code), `drop` values below them go as well (the callee or the receiver). */
static void vmPushCall(Vm* vm, VmProto* p, int argc, int drop, Value self, uint8_t kind) {
    VmFrame* f;
    VmScope* s;
    int i, n = argc > p->nparams ? argc : p->nparams;
    if (FIRE_UNLIKELY(vm->nframes >= VM_FRAMES)) fatal("Stack overflow: too many nested calls.");
    s = scNew(vm, vm->global);
    scReserve(s, (uint32_t)n + 4);
    for (i = 0; i < argc; i++) { Value a = vm->stack[vm->sp - (uint32_t)argc + (uint32_t)i]; retain(a); s->slots[i] = a; }
    s->n = (uint32_t)argc;
    if (FIRE_UNLIKELY(vm->copyMask != 0)) {
        /* CopyArgs: 4 (7) - the argument is what a call returned (`f(g())`): when it lies freshly with the caller, it belongs to the call (SPEC 2.1) */
        uint64_t mask = vm->copyMask;
        VmScope* from = vm->nframes > 0 ? vm->frames[vm->nframes - 1].scope : NULL;
        vm->copyMask = 0;
        for (i = 0; i < argc && i < 16; i++) {
            uint32_t bits = (uint32_t)(mask >> (4 * i)) & 15u;
            if (bits == 0 || !ownedOf(s->slots[i])) continue;
            if ((bits == 4 || bits == 7) && from) { hoistFrom(s->slots[i], &from->temp, &s->own); continue; }
            vmUnsupported("this way of passing an argument (CopyArgs)", NULL);
        }
    }
    vm->sp -= (uint32_t)(argc + drop);
    f = &vm->frames[vm->nframes++];
    f->chunk = p->chunk;
    f->ip = p->chunk->code;
    f->scope = s;
    f->base = s;
    f->self = self;
    f->spBase = vm->sp;
    f->kind = kind;
    f->catchEnded = 0;
    f->rec = NULL;
    /* missing arguments: their default values (a function of their own, run nested), else undefined */
    for (i = argc; i < p->nparams; i++) {
        VmProto* d = (uint32_t)i < p->ndefaults ? p->defaults[i] : NULL;
        Value v = Undef();
        if (d) { v = vmCallNested(vm, d, 0, NULL, self, FR_NESTED); }
        retain(v);
        s->slots[s->n++] = v;
        if (d) release(v);
    }
}

/* The handlers of the frame that are still registered (a `return` in a `try`) go. */
static void vmCatchFn(void* ctx, uint32_t ci, Value e);
static void vmDropHandlers(Vm* vm, int32_t frame) {
    (void)vm;
    while (g_handlers && g_handlers->fn == vmCatchFn && ((VmHandlerRec*)g_handlers)->frame >= frame) {
        VmHandlerRec* r = (VmHandlerRec*)g_handlers;
        popHandler();
        free(r);
    }
}

/* Leaves the scopes of the frame down to `stop` (excluded). */
static void vmLeaveTo(Vm* vm, VmFrame* f, VmScope* stop) {
    while (f->scope != stop && f->scope) {
        VmScope* s = f->scope;
        f->scope = s->parent;
        scLeave(vm, s);
    }
}
static VmScope* vmStopOf(Vm* vm, VmFrame* f) {
    (void)vm;
    if (f->kind == FR_TOP) return vm->global;
    if (f->kind == FR_CATCH) return f->rec->scope;
    return f->base->parent;
}

/* `return v` of the top frame. True: the run goes on; false: the frame was the base of the run. */
static bool vmReturn(Vm* vm, Value r, int32_t base) {
    VmFrame* f = &vm->frames[vm->nframes - 1];
    OwnList* lists[64];
    int n = 0;
    VmScope* s;
    VmScope* stop = vmStopOf(vm, f);
    uint8_t kind = f->kind;
    Value self = f->self;
    vmDropHandlers(vm, vm->nframes - 1);
    /* what the call returns does not die with its scopes: it goes to the caller (SPEC 2.3) */
    for (s = f->scope; s && s != stop && n < 62; s = s->parent) { lists[n++] = &s->temp; lists[n++] = &s->own; }
    if (n) transferTree(r, lists, n);
    retain(r);   /* (the count for the trip; the caller adopts it) */
    vm->sp = f->spBase;
    vmLeaveTo(vm, f, stop);
    vm->nframes--;
    switch (kind) {
        case FR_CTOR:
            release(r);
            vm->stack[vm->sp++] = self;
            break;
        case FR_NESTED:
            vm->ret = r;
            break;
        default: {
            VmFrame* caller = &vm->frames[vm->nframes - 1];
            vm->stack[vm->sp++] = r;
            adopt(r, &caller->scope->temp);
            break;
        }
    }
    return vm->nframes > base;
}

/* A function run from C (a destructor, a default value, an exception the runtime makes, a lambda a native calls): its result, or undefined with the
 * unwinding state set when it ended with an exception. */
static Value vmCallNested(Vm* vm, VmProto* p, int argc, const Value* args, Value self, uint8_t kind) {
    int i;
    int32_t base;
    if (vm->sp + (uint32_t)argc + 64 > VM_STACK) fatal("Stack overflow: the operand stack is full.");
    for (i = 0; i < argc; i++) vm->stack[vm->sp++] = args[i];
    vmPushCall(vm, p, argc, 0, self, kind);
    base = vm->nframes - 1;
    vm->ret = Undef();
    vmRun(vm, base);
    if (g_unwind.active) return Undef();
    if (kind == FR_CTOR) { vm->sp--; return self; }
    return vm->ret;
}

/* ---- what the runtime calls back ---- */
/* runDestructors (the descriptor's destroy): the destructors of the object's class chain, derived class first. */
static void vmDestroyObj(Obj* o) {
    Vm* vm = g_vmCur;
    VmClass* c = vmClassOf(vm, o->cls);
    Value self = ObjV(o);
    for (; c; c = c->base)
        if (c->dtor) {
            Value r = vmCallNested(vm, c->dtor, 0, NULL, self, FR_NESTED);
            if (g_unwind.active) return;
            release(r);
        }
}
/* The descriptor's toString: the class's ToString(). */
static Value vmToString(Value self) {
    Vm* vm = g_vmCur;
    VmClass* c = vmClassOf(vm, asObj(self)->cls);
    VmProto* m = c ? vmFindMethod(c, vmIntern(vm->prog, "ToString", 8, true), 0) : NULL;
    Value r;
    if (!m) return Undef();
    r = vmCallNested(vm, m, 0, NULL, self, FR_NESTED);
    return r;   /* (counted for the trip: userToString adopts it) */
}
/* A lambda of the VM that the runtime (a native) calls. */
static Value vmLamThunk(Value lam, const Value* args) {
    Vm* vm = g_vmCur;
    Lam* l = lamOf(lam);
    VmProto* p = (VmProto*)(uintptr_t)lamCaps(l)[l->ncaps - 1].i;
    return vmCallNested(vm, p, (int)l->nparams, args, l->on, FR_NESTED);
}
static VmProto* vmLamProto(Lam* l) { return (VmProto*)(uintptr_t)lamCaps(l)[l->ncaps - 1].i; }

/* An exception object of the program by the name of its class, made with its constructor of `argc` arguments, owned by the global scope (the generated
 * makeDivideError, makeClassError, ...). Undefined when the program has no such class. */
static Value vmMakeException(const char* cls, int argc, const Value* args) {
    Vm* vm = g_vmCur;
    VmClass* c;
    VmProto* ctor;
    Value o;
    UnwindState saved;
    if (!vm) return Undef();
    c = vmClassNamed(vm->prog, vmIntern(vm->prog, cls, strlen(cls), true));
    if (!c) return Undef();
    ctor = vmFindCtor(c, argc);
    o = newObject(c->id, c->nlayout, g_globalOwn);
    saved = g_unwind;
    if (ctor) vmCallNested(vm, ctor, argc, args, o, FR_CTOR);
    g_unwind = saved;
    return o;
}
Value makeDivideError(Value message) { return vmMakeException("DivideByZeroException", 1, &message); }
Value makeDestroyedError(Value message) { return vmMakeException("DestroyedException", 1, &message); }
Value makeAccessError(Value message) { return vmMakeException("AccessDeniedException", 1, &message); }
Value makeReflectError(Value message) { return vmMakeException("ReflectionException", 1, &message); }
Value makeUnitError(Value message, Value expected, Value actual) { Value a[3]; a[0] = message; a[1] = expected; a[2] = actual; return vmMakeException("UnitMismatchException", 3, a); }
Value makeIndexError(Value message, int64_t index, int64_t length) { Value a[3]; a[0] = message; a[1] = Int(index); a[2] = Int(length); return vmMakeException("IndexOutOfBoundsException", 3, a); }
Value makeClassError(const char* cls, Value message) { return vmMakeException(cls, 1, &message); }
const char* g_dimNames[FIRE_NDIMS] = {""};

/* The catch block of a handler of the VM: runs on top of the throw site (the runtime's throwValue calls it), on a pseudo frame of the function that owns
 * the try, until it leaves (the unwinding state says where to). */
static void vmCatchFn(void* ctx, uint32_t ci, Value e) {
    VmHandlerRec* rec = (VmHandlerRec*)ctx;
    Vm* vm = rec->vm;
    VmFrame* owner = &vm->frames[rec->frame];
    VmFrame* f;
    VmScope* s;
    int32_t base;
    if (vm->nframes >= VM_FRAMES) fatal("Stack overflow: too many nested calls.");
    rec->token = g_pending ? g_pending->token : 0;
    s = scNew(vm, rec->scope);
    scDefine(s, e);
    f = &vm->frames[vm->nframes++];
    f->chunk = owner->chunk;
    f->ip = owner->chunk->code + rec->t->addrs[ci];
    f->scope = s;
    f->base = s;
    f->self = owner->self;
    f->spBase = vm->sp;
    f->kind = FR_CATCH;
    f->catchEnded = 0;
    f->rec = rec;
    base = vm->nframes - 1;
    vmRun(vm, base);
}

/* The handler at the bottom of a script: an exception that nothing catches. The text of the VM's command line (UncaughtScriptException) - then the
 * script ends: its frames are unwound and everything it owns is destroyed, without destructors (the program "ends here", like the C# VM and a native build,
 * where nothing runs after it). */
static void vmUnhandledFn(void* ctx, uint32_t ci, Value e) {
    Vm* vm = (Vm*)ctx;
    (void)ci;
    reportUnhandled(e);
    vm->failed = 1;
    if (!vm->quiet) { vm->quiet = 1; g_zombies.quiet++; }
    exitJump(&vm->bottom, 0);
}

/* ---- the class descriptors: the program's classes registered at run time (like fireRegisterPlugin does for the classes of a plugin) ---- */
static void vmRegisterClasses(Vm* vm) {
    VmProgram* p = vm->prog;
    uint32_t base = g_classDescCount, i, n = p->nclasses;
    const char* message = vmIntern(p, "message", 7, true);
    const char* toString = vmIntern(p, "ToString", 8, true);
    const char* exception = vmIntern(p, "Exception", 9, true);
    FireClassDesc* rows = (FireClassDesc*)calloc(base + n + 1, sizeof(FireClassDesc));
    if (!rows) outOfMemory();
    if (base) memcpy(rows, g_classDescs, base * sizeof(FireClassDesc));
    vm->firstId = base;
    vm->nids = n;
    vm->byId = (VmClass**)calloc(n + 1, sizeof(VmClass*));
    vm->excTypes = (int32_t**)calloc(n + 1, sizeof(int32_t*));
    for (i = 0; i < n; i++) { p->classes[i]->id = base + i; vm->byId[i] = p->classes[i]; }
    for (i = 0; i < n; i++) {
        VmClass* c = p->classes[i];
        VmClass* k;
        FireClassDesc* d = &rows[base + i];
        uint32_t depth = 0, t = 0;
        vmLayout(p, c);
        for (k = c; k; k = k->base) { depth++; if (k->name == exception) c->isException = true; if (k->dtor) c->hasDtor = true; }
        d->name = c->name;
        d->nfields = c->nlayout;
        d->messageIndex = c->isException ? vmFieldIndex(c, message) : -1;
        d->ticksIndex = -1;
        d->destroy = c->hasDtor ? vmDestroyObj : NULL;
        d->toString = vmFindMethod(c, toString, 0) ? vmToString : NULL;
        /* the catch types an object of the class is: every class of its chain (the catch type of class k is its id + 1; FIRE_EXC_ANY is 0) */
        if (c->isException) {
            int32_t* types = (int32_t*)malloc((depth + 1) * sizeof(int32_t));
            if (!types) outOfMemory();
            for (k = c; k; k = k->base) types[t++] = (int32_t)k->id + 1;
            types[t] = -1;
            vm->excTypes[i] = types;
            d->excTypes = types;
        }
        d->flags = (c->flags & 32) ? 1u : 0u;
    }
    vm->descs = rows;
    g_classDescs = rows;
    g_classDescCount = base + n;
}
static void vmUnregisterClasses(Vm* vm) {
    uint32_t i;
    g_classDescCount = vm->firstId;
    if (vm->firstId == 0) g_classDescs = NULL;
    else {
        /* (the rows before belong to whoever registered them before: they stay as they were) */
        FireClassDesc* keep = (FireClassDesc*)malloc(vm->firstId * sizeof(FireClassDesc));
        if (!keep) outOfMemory();
        memcpy(keep, vm->descs, vm->firstId * sizeof(FireClassDesc));
        g_classDescs = keep;
    }
    for (i = 0; i < vm->nids; i++) free(vm->excTypes[i]);
    free(vm->excTypes);
    free(vm->byId);
    free(vm->descs);
    vm->descs = NULL; vm->excTypes = NULL; vm->byId = NULL; vm->nids = 0;
}
/* The catch types of a template, resolved once by the names of its classes. */
static void vmResolveHandler(Vm* vm, VmHandlerT* t) {
    uint32_t i;
    const char* exception = vmIntern(vm->prog, "Exception", 9, true);
    for (i = 0; i < t->ncatch; i++) {
        VmClass* c = t->types[i] ? vmClassNamed(vm->prog, t->types[i]) : NULL;
        t->typeIds[i] = !t->types[i] || t->types[i] == exception ? FIRE_EXC_ANY : c ? (int32_t)c->id + 1 : 0x7FFFFFFF;
    }
    t->info.ncatch = t->ncatch;
    t->info.types = t->typeIds;
    t->info.hasFinally = 0;
    t->info.typeMap = NULL;
}

/* ---- the interpreter loop ---- */
#define U16(p) ((uint32_t)(p)[0] | ((uint32_t)(p)[1] << 8))
#define SYNC() do { fr->ip = ip; vm->sp = (uint32_t)(sp - st); } while (0)
#define RELOAD() do { fr = &vm->frames[vm->nframes - 1]; ip = fr->ip; sp = st + vm->sp; } while (0)
#define CHECK() do { if (FIRE_UNLIKELY(g_unwind.active)) goto unwind; } while (0)
#define PUSH(v) (*sp++ = (v))
#define POP() (*--sp)
#define TOP() (sp[-1])

static VmProto* vmIcMethod(VmChunk* c, const uint8_t* at, uint32_t cls, VmClass* k, const char* name, int argc, VmProgram* p) {
    uint32_t off = (uint32_t)(at - c->code);
    VmProto* m;
    if (!c->icCls) {
        c->icCls = (uint32_t*)vmAlloc(&p->arena, (c->ncode + 1) * sizeof(uint32_t));
        c->icPtr = (void**)vmAlloc(&p->arena, (c->ncode + 1) * sizeof(void*));
        memset(c->icCls, 0xFF, (c->ncode + 1) * sizeof(uint32_t));
    }
    if (c->icCls[off] == cls) return (VmProto*)c->icPtr[off];
    m = vmFindMethod(k, name, argc);
    if (m) { c->icCls[off] = cls; c->icPtr[off] = m; }
    return m;
}
static int32_t vmIcField(VmChunk* c, const uint8_t* at, uint32_t cls, VmClass* k, const char* name, VmProgram* p) {
    uint32_t off = (uint32_t)(at - c->code);
    int32_t i;
    if (!c->icCls) {
        c->icCls = (uint32_t*)vmAlloc(&p->arena, (c->ncode + 1) * sizeof(uint32_t));
        c->icPtr = (void**)vmAlloc(&p->arena, (c->ncode + 1) * sizeof(void*));
        memset(c->icCls, 0xFF, (c->ncode + 1) * sizeof(uint32_t));
    }
    if (c->icCls[off] == cls) return (int32_t)(intptr_t)c->icPtr[off];
    i = vmFieldIndex(k, name);
    if (i >= 0) { c->icCls[off] = cls; c->icPtr[off] = (void*)(intptr_t)i; }
    return i;
}

static FIRE_COLD bool vmNoMember(Value v, const char* what, const char* name) {
    char text[300];
    snprintf(text, sizeof text, "'%s' has no %s '%s'.", v.kind == K_Class ? className(asObj(v)->cls) : kindName(v), what, name);
    fireError("MissingMemberException", text);
    return true;
}

/* GuardLocal/GuardTop: a value going into a declaration int/float/bool/string (DeclaredKind) - the runtime's guardSlot does the conversion and the
 * exception. True when it threw. */
static const fire_char16 g_vmUnitlessD[] = {117, 110, 105, 116, 108, 101, 115, 115, 0};
static const Str g_vmUnitless = {{IMMORTAL, R_Str}, 8, g_vmUnitlessD};
static bool vmGuard(Value* v, uint32_t required, const char* what) {
    static const uint8_t kinds[] = { K_Bool, K_Int, K_Float, K_Char, K_String, K_Word };
    uint32_t tag = (required & 0x1F);
    uint8_t kind;
    if (tag == 0 || tag > 6) return false;
    kind = kinds[tag - 1];
    if (kind == K_String) {
        if (v->kind == K_String || (v->kind == K_Undefined && !(required & 0x20))) return false;
        { Value old = *v; *v = guardStr(*v); retain(*v); release(old); }
        return g_unwind.active;
    }
    if (v->kind == kind && (!(required & 0x80) || v->unit == 0) && (kind != K_Int || v->reserved == 0)) return false;
    if (v->kind == K_Undefined && !(required & 0x20)) return false;
    return guardSlot(v, kind, (uint8_t)(((required & 0x80) ? 1 : 0) | ((required & 0x20) ? 2 : 0)), what ? what : "A value", &g_vmUnitless);
}

static void vmRun(Vm* vm, int32_t base) {
    Value* const st = vm->stack;
    VmFrame* fr;
    const uint8_t* ip;
    Value* sp;
    VmProgram* const prog = vm->prog;
    RELOAD();
    for (;;) {
        uint32_t op = *ip++;
        switch (op) {
        case OP_LoadConst: PUSH(fr->chunk->k[U16(ip)]); ip += 2; break;
        case OP_Pop: sp--; break;
        case OP_Dup: *sp = sp[-1]; sp++; break;
        case OP_Swap: { Value t = sp[-1]; sp[-1] = sp[-2]; sp[-2] = t; break; }
        case OP_RotateUnderTop: { Value c = sp[-1], b = sp[-2], a = sp[-3]; sp[-3] = b; sp[-2] = a; sp[-1] = c; break; }
        case OP_LoadLocal: {
            VmScope* s = scAncestor(fr->scope, U16(ip));
            uint32_t slot = U16(ip + 2);
            ip += 4;
            PUSH(slot < s->n ? s->slots[slot] : Undef());
            break;
        }
        case OP_StoreLocal: case OP_StoreLocalPop: {
            VmScope* s = scAncestor(fr->scope, U16(ip));
            uint32_t slot = U16(ip + 2);
            Value v = sp[-1], old;
            ip += 4;
            if (FIRE_UNLIKELY(slot >= s->n)) { scReserve(s, slot + 1); while (s->n <= slot) s->slots[s->n++] = Undef(); }
            old = s->slots[slot];
            retain(v);
            s->slots[slot] = v;
            if (FIRE_UNLIKELY(isRef(old))) { SYNC(); release(old); }
            if (op == OP_StoreLocalPop) sp--;
            break;
        }
        case OP_LoadGlobal: { uint32_t slot = U16(ip); ip += 2; PUSH(slot < vm->global->n ? vm->global->slots[slot] : Undef()); break; }
        case OP_StoreGlobal: case OP_StoreGlobalPop: {
            VmScope* s = vm->global;
            uint32_t slot = U16(ip);
            Value v = sp[-1], old;
            ip += 2;
            if (FIRE_UNLIKELY(slot >= s->n)) { scReserve(s, slot + 1); while (s->n <= slot) s->slots[s->n++] = Undef(); }
            old = s->slots[slot];
            retain(v);
            s->slots[slot] = v;
            if (FIRE_UNLIKELY(isRef(old))) { SYNC(); release(old); }
            if (op == OP_StoreGlobalPop) sp--;
            break;
        }
        case OP_DeclareLocal: scDefine(fr->scope, POP()); break;

        /* ---- arithmetic: the fast path of the generated code inline, the rest in the runtime ---- */
        case OP_Add: {
            Value b = POP(), a = sp[-1];
            if (FIRE_LIKELY(a.kind == K_Int && b.kind == K_Int && a.unit == b.unit)) sp[-1] = IntOp((int64_t)((uint64_t)a.i + (uint64_t)b.i), a.unit, a, b);
            else { SYNC(); sp[-1] = addR(a, b, &fr->scope->temp); CHECK(); }
            break;
        }
        case OP_Sub: {
            Value b = POP(), a = sp[-1];
            if (FIRE_LIKELY(a.kind == K_Int && b.kind == K_Int && a.unit == b.unit)) sp[-1] = IntOp((int64_t)((uint64_t)a.i - (uint64_t)b.i), a.unit, a, b);
            else { SYNC(); sp[-1] = sub(a, b); CHECK(); }
            break;
        }
        case OP_Mul: {
            Value b = POP(), a = sp[-1];
            if (FIRE_LIKELY(a.kind == K_Int && b.kind == K_Int && a.unit == 0 && b.unit == 0)) sp[-1] = IntOp((int64_t)((uint64_t)a.i * (uint64_t)b.i), 0, a, b);
            else { SYNC(); sp[-1] = mul(a, b); CHECK(); }
            break;
        }
        case OP_Div: { Value b = POP(), a = sp[-1]; SYNC(); sp[-1] = divide(a, b); CHECK(); break; }
        case OP_Mod: {
            Value b = POP(), a = sp[-1];
            if (FIRE_LIKELY(a.kind == K_Int && b.kind == K_Int && a.unit == b.unit && b.i > 0)) sp[-1] = IntOp(a.i % b.i, a.unit, a, b);
            else { SYNC(); sp[-1] = modulo(a, b); CHECK(); }
            break;
        }
        case OP_Neg: { SYNC(); sp[-1] = negate(sp[-1]); CHECK(); break; }
        case OP_LogicalNot: { Value v = sp[-1]; bool t; SYNC(); t = truthy(v); CHECK(); sp[-1] = Bool(!t); break; }
        case OP_Eq: { Value b = POP(); sp[-1] = Bool(opEq(sp[-1], b)); break; }
        case OP_NotEq: { Value b = POP(); sp[-1] = Bool(!opEq(sp[-1], b)); break; }
        case OP_Lt: case OP_LtEq: case OP_Gt: case OP_GtEq: {
            Value b = POP(), a = sp[-1];
            bool r;
            if (FIRE_LIKELY(a.kind == K_Int && b.kind == K_Int && a.unit == b.unit))
                r = op == OP_Lt ? a.i < b.i : op == OP_LtEq ? a.i <= b.i : op == OP_Gt ? a.i > b.i : a.i >= b.i;
            else { SYNC(); r = op == OP_Lt ? lt(a, b) : op == OP_LtEq ? le(a, b) : op == OP_Gt ? gt(a, b) : ge(a, b); CHECK(); }
            sp[-1] = Bool(r);
            break;
        }
        case OP_JumpIfNotLt: case OP_JumpIfNotLtEq: case OP_JumpIfNotGt: case OP_JumpIfNotGtEq: case OP_JumpIfNotEq: case OP_JumpIfNotNotEq: {
            Value b = POP(), a = POP();
            bool r;
            uint32_t to = U16(ip);
            ip += 2;
            if (op == OP_JumpIfNotEq || op == OP_JumpIfNotNotEq) r = opEq(a, b) == (op == OP_JumpIfNotEq);
            else if (FIRE_LIKELY(a.kind == K_Int && b.kind == K_Int && a.unit == b.unit))
                r = op == OP_JumpIfNotLt ? a.i < b.i : op == OP_JumpIfNotLtEq ? a.i <= b.i : op == OP_JumpIfNotGt ? a.i > b.i : a.i >= b.i;
            else { SYNC(); r = op == OP_JumpIfNotLt ? lt(a, b) : op == OP_JumpIfNotLtEq ? le(a, b) : op == OP_JumpIfNotGt ? gt(a, b) : ge(a, b); CHECK(); }
            if (!r) ip = fr->chunk->code + to;
            break;
        }
        case OP_ArithLocalConstPop: {
            VmScope* s = scAncestor(fr->scope, U16(ip));
            uint32_t slot = U16(ip + 2);
            Value k = fr->chunk->k[U16(ip + 4)];
            bool minus = ip[6] != 0;
            Value* x;
            ip += 7;
            if (FIRE_UNLIKELY(slot >= s->n)) { scReserve(s, slot + 1); while (s->n <= slot) s->slots[s->n++] = Undef(); }
            x = &s->slots[slot];
            if (FIRE_LIKELY(x->kind == K_Int && k.kind == K_Int && x->unit == k.unit)) *x = IntOp((int64_t)(minus ? (uint64_t)x->i - (uint64_t)k.i : (uint64_t)x->i + (uint64_t)k.i), x->unit, *x, k);
            else { Value r; SYNC(); r = minus ? sub(*x, k) : add(*x, k); CHECK(); *x = r; }
            break;
        }
        case OP_ArithGlobalConstPop: {
            VmScope* s = vm->global;
            uint32_t slot = U16(ip);
            Value k = fr->chunk->k[U16(ip + 2)];
            bool minus = ip[4] != 0;
            Value* x;
            ip += 5;
            if (FIRE_UNLIKELY(slot >= s->n)) { scReserve(s, slot + 1); while (s->n <= slot) s->slots[s->n++] = Undef(); }
            x = &s->slots[slot];
            if (FIRE_LIKELY(x->kind == K_Int && k.kind == K_Int && x->unit == k.unit)) *x = IntOp((int64_t)(minus ? (uint64_t)x->i - (uint64_t)k.i : (uint64_t)x->i + (uint64_t)k.i), x->unit, *x, k);
            else { Value r; SYNC(); r = minus ? sub(*x, k) : add(*x, k); CHECK(); *x = r; }
            break;
        }

        /* ---- jumps ---- */
        case OP_Jump: {
            uint32_t to = U16(ip);
            ip += 2;
            if (FIRE_UNLIKELY(fr->catchEnded)) { SYNC(); exitJump(&fr->rec->h, to); goto unwind; }   /* (the jump behind the try: the catch is left) */
            ip = fr->chunk->code + to;
            break;
        }
        case OP_JumpIfFalse: case OP_JumpIfFalsePeek: case OP_JumpIfTruePeek: {
            Value c = op == OP_JumpIfFalse ? POP() : sp[-1];
            uint32_t to = U16(ip);
            bool t;
            ip += 2;
            if (FIRE_LIKELY(c.kind == K_Bool)) t = c.i != 0;
            else { SYNC(); t = truthy(c); CHECK(); }
            if (t == (op == OP_JumpIfTruePeek)) ip = fr->chunk->code + to;
            break;
        }

        /* ---- scopes and ownership ---- */
        case OP_EnterScope: fr->scope = scNew(vm, fr->scope); break;
        case OP_ExitScope: {
            VmScope* s = fr->scope;
            if (FIRE_UNLIKELY(fr->catchEnded)) { fr->rec->extraExits++; break; }   /* (break/continue out of a catch: the owner's scopes, left when it lands) */
            SYNC();
            if (fr->kind == FR_CATCH && s == fr->base) { fr->scope = fr->rec->scope; scLeave(vm, s); fr->catchEnded = 1; break; }
            fr->scope = s->parent;
            scLeave(vm, s);
            break;
        }
        case OP_ReleaseTemps: {
            OwnList* t = &fr->scope->temp;
            if (FIRE_UNLIKELY(t->head != NULL)) { SYNC(); leave(t); }
            break;
        }
        case OP_ClaimToScope: {
            uint32_t depth = U16(ip), flags = ip[2];
            VmScope* target = depth == 0xFFFF ? vm->global : scAncestor(fr->scope, depth);
            Value v = sp[-1];
            ip += 3;
            if (ownedOf(v)) {
                if (flags & 1) claimToList(v, &fr->scope->temp, &target->own);
                if (flags & 2) {
                    VmScope* s;
                    VmScope* stop = vmStopOf(vm, fr);
                    for (s = fr->scope; s && s != target; s = s->parent) {
                        hoistFrom(v, &s->own, &target->own);
                        hoistFrom(v, &s->temp, &target->own);
                        if (s == fr->base || s->parent == stop) break;
                    }
                }
            }
            break;
        }
        case OP_HoistValue: {
            /* `x = value` with a variable of an outer block: a value an inner block of the running call owns moves into the call's scope (the main
             * program: the global scope) - never out of the call (VM.HoistValue) */
            Owned* o = ownedOf(sp[-1]);
            if (o && o->owner) {
                VmScope* stop = vmStopOf(vm, fr);
                VmScope* target = fr->kind == FR_TOP ? vm->global : fr->base;
                VmScope* s;
                for (s = fr->scope; s && s != stop && s != target; s = s->parent)
                    if (o->owner == &s->own || o->owner == &s->temp) { ownUnlink(o); ownLink(&target->own, o); break; }
            }
            break;
        }

        /* ---- natives ---- */
        case OP_CallNative: {
            uint32_t idx = U16(ip), argc = ip[2];
            ip += 3;
            if (idx == 0 && argc == 1) { Value v = POP(); SYNC(); print(v, &fr->scope->temp); CHECK(); PUSH(Undef()); break; }
            SYNC();
            vmUnsupported("the native", idx < sizeof g_vmNativeNames / sizeof g_vmNativeNames[0] ? g_vmNativeNames[idx] : "?");
        }

        /* ---- lambdas and calls ---- */
        case OP_MakeLambda: {
            VmProto* p = fr->chunk->fn[U16(ip)];
            bool hasOn = ip[2] != 0;
            Lam* l;
            ip += 3;
            SYNC();
            /* the proto rides as a hidden last capture (no field is added to the runtime's lambda: L1) */
            l = allocLam((uint32_t)p->nparams, 1, vmLamThunk, &fr->scope->temp);
            lamCaps(l)[0] = Int((int64_t)(intptr_t)p);
            l->on = hasOn ? POP() : Undef();
            PUSH(LamV(l));
            break;
        }
        case OP_Call: {
            uint32_t argc = *ip++;
            Value callee = sp[-(int)argc - 1];
            Lam* l;
            if (FIRE_UNLIKELY(callee.kind != K_Lambda)) { SYNC(); fireError("ArgumentException", "A call of something that is not a lambda."); goto unwind; }
            l = lamOf(callee);
            if (FIRE_UNLIKELY(l->o.flags & 1)) { SYNC(); destroyedError(callee); goto unwind; }
            SYNC();
            if (l->fn == vmLamThunk) {
                vmPushCall(vm, vmLamProto(l), (int)argc, 1, l->on, FR_CALL);
            } else {
                Value r = callLamU(callee, (int)argc, sp - argc, &fr->scope->temp);
                vm->sp -= argc + 1;
                CHECK();
                vm->stack[vm->sp++] = r;
                adopt(r, &fr->scope->temp);
            }
            RELOAD();
            break;
        }
        case OP_Return: {
            Value r = POP();
            SYNC();
            if (FIRE_UNLIKELY(fr->kind == FR_CATCH)) { exitReturn(&fr->rec->h, r); goto unwind; }   /* (`return` in a catch: the owner returns) */
            if (!vmReturn(vm, r, base)) return;
            RELOAD();
            break;
        }
        case OP_Halt: {
            SYNC();
            if (fr->kind == FR_TOP) { vm->nframes--; return; }
            if (!vmReturn(vm, Undef(), base)) return;   /* (fell off the end of a function) */
            RELOAD();
            break;
        }
        case OP_LoadThis: PUSH(fr->self); break;
        case OP_CallProtoWithThis: {
            VmProto* p = fr->chunk->fn[U16(ip)];
            uint32_t argc = ip[2];
            ip += 3;
            SYNC();
            vmPushCall(vm, p, (int)argc, 1, sp[-(int)argc - 1], FR_CALL);
            RELOAD();
            break;
        }
        case OP_CallMethod: {
            const uint8_t* at = ip - 1;
            const char* name = fr->chunk->kname[U16(ip)];
            uint32_t argc = ip[2];
            Value recv = sp[-(int)argc - 1];
            VmClass* c;
            VmProto* m;
            ip += 3;
            SYNC();
            if (FIRE_UNLIKELY(recv.kind != K_Class)) { if (recv.kind == K_Undefined) vmNoMember(recv, "method", name); else vmUnsupported("methods of a value of kind", kindName(recv)); goto unwind; }
            if (FIRE_UNLIKELY(!leafAlive(recv))) { destroyedError(recv); goto unwind; }
            c = vmClassOf(vm, asObj(recv)->cls);
            m = c ? vmIcMethod(fr->chunk, at, asObj(recv)->cls, c, name, (int)argc, prog) : NULL;
            if (FIRE_UNLIKELY(!m)) { vmNoMember(recv, "method", name); goto unwind; }
            vmPushCall(vm, m, (int)argc, 1, recv, FR_CALL);
            RELOAD();
            break;
        }
        case OP_CallStaticMethod: {
            const uint8_t* at = ip - 1;
            VmChunk* ch = fr->chunk;
            uint32_t argc = ip[4];
            VmProto* m;
            ip += 5;
            SYNC();
            if (ch->icCls && ch->icCls[at - ch->code] == 0) m = (VmProto*)ch->icPtr[at - ch->code];
            else {
                VmClass* c = vmClassNamed(prog, ch->kname[U16(at + 1)]);
                m = c ? vmIcMethod(ch, at, 0, c, ch->kname[U16(at + 3)], (int)argc, prog) : NULL;
                if (!m) { vmNoMember(Undef(), "static method", ch->kname[U16(at + 3)]); goto unwind; }
            }
            vmPushCall(vm, m, (int)argc, 0, Undef(), FR_CALL);
            RELOAD();
            break;
        }
        case OP_NewObject: {
            VmClass* c = vmClassNamed(prog, fr->chunk->kname[U16(ip)]);
            uint32_t argc = ip[2];
            VmProto* ctor;
            Value o;
            ip += 3;
            SYNC();
            if (!c) vmUnsupported("the class", fr->chunk->kname[U16(ip - 3)]);
            ctor = vmFindCtor(c, (int)argc);
            if (!ctor) { fireError("ArgumentException", "No constructor takes that many arguments."); goto unwind; }
            o = newObject(c->id, c->nlayout, &fr->scope->temp);   /* unclaimed: the statement temporaries (SPEC 2.1) */
            vmPushCall(vm, ctor, (int)argc, 0, o, FR_CTOR);
            RELOAD();
            break;
        }
        case OP_ConstructBase: {
            VmClass* c = vmClassNamed(prog, fr->chunk->kname[U16(ip)]);
            uint32_t argc = ip[2];
            VmProto* ctor;
            ip += 3;
            SYNC();
            if (!c) vmUnsupported("the class", fr->chunk->kname[U16(ip - 3)]);
            ctor = vmFindCtor(c, (int)argc);
            if (!ctor) { fireError("ArgumentException", "No base constructor takes that many arguments."); goto unwind; }
            vmPushCall(vm, ctor, (int)argc, 0, fr->self, FR_BASECTOR);
            RELOAD();
            break;
        }

        /* ---- fields ---- */
        case OP_GetField: {
            const uint8_t* at = ip - 1;
            const char* name = fr->chunk->kname[U16(ip)];
            Value v = sp[-1];
            VmClass* c;
            int32_t idx;
            ip += 2;
            if (FIRE_UNLIKELY(v.kind != K_Class)) { SYNC(); vmNoMember(v, "field", name); goto unwind; }
            if (FIRE_UNLIKELY(!leafAlive(v))) { SYNC(); destroyedError(v); goto unwind; }
            c = vmClassOf(vm, asObj(v)->cls);
            idx = c ? vmIcField(fr->chunk, at, asObj(v)->cls, c, name, prog) : -1;
            if (FIRE_UNLIKELY(idx < 0)) { SYNC(); vmNoMember(v, "field", name); goto unwind; }
            sp[-1] = objFields(asObj(v))[idx];
            break;
        }
        case OP_SetField: case OP_SetFieldOnThis: case OP_SetFieldClaim: {
            const uint8_t* at = ip - 1;
            const char* name = fr->chunk->kname[U16(ip)];
            uint32_t flags = op == OP_SetFieldClaim ? ip[2] : 0;
            Value x = POP();
            Value v = op == OP_SetFieldOnThis ? fr->self : POP();
            VmClass* c;
            int32_t idx;
            Value old;
            ip += op == OP_SetFieldClaim ? 3 : 2;
            SYNC();
            if (FIRE_UNLIKELY(v.kind != K_Class)) { notObjectAssign(v); CHECK(); vmUnsupported("assigning a field of", kindName(v)); }
            if (FIRE_UNLIKELY(!leafAlive(v))) { destroyedError(v); goto unwind; }
            c = vmClassOf(vm, asObj(v)->cls);
            idx = c ? vmIcField(fr->chunk, at, asObj(v)->cls, c, name, prog) : -1;
            if (FIRE_UNLIKELY(idx < 0)) { vmNoMember(v, "field", name); goto unwind; }
            old = objFields(asObj(v))[idx];
            objFields(asObj(v))[idx] = x;
            retain(x);
            release(old);
            if (op == OP_SetFieldClaim && !(flags & 2)) claimToHolder(v, x, &fr->scope->temp);
            if (op != OP_SetFieldOnThis) PUSH(x);
            break;
        }

        /* ---- statics ---- */
        case OP_SetStaticFieldOnInit: case OP_SetStaticField: case OP_GetStaticField: {
            VmClass* c = vmClassNamed(prog, fr->chunk->kname[U16(ip)]);
            const char* name = fr->chunk->kname[U16(ip + 2)];
            int32_t i;
            ip += 4;
            SYNC();
            if (!c || (i = vmStaticIndex(&c, name)) < 0) { vmNoMember(Undef(), "static field", name); goto unwind; }
            if (op == OP_GetStaticField) PUSH(c->svals[i]);
            else {
                Value x = op == OP_SetStaticField ? sp[-1] : POP();
                Value old = c->svals[i];
                retain(x);
                c->svals[i] = x;
                release(old);
            }
            break;
        }

        /* ---- the checks of declarations ---- */
        case OP_GuardLocal: {
            uint32_t slot = U16(ip), tag = ip[2];
            const char* what = fr->chunk->kname[U16(ip + 3)];
            uint32_t required = ((tag & 0x7F) + 1) | (tag & 0x80);
            ip += 7;
            if (slot < fr->scope->n) { SYNC(); if (vmGuard(&fr->scope->slots[slot], required, what)) goto unwind; }
            break;
        }
        case OP_GuardTop: {
            uint32_t required = (uint32_t)ip[0] + 1;
            const char* what = fr->chunk->kname[U16(ip + 1)];
            ip += 3;
            SYNC();
            if (vmGuard(&sp[-1], required, what)) goto unwind;
            break;
        }
        case OP_CheckUnit: {
            /* a declaration with a unit takes the value (Value.AdaptToUnit): units are not in the spike - a value without one passes */
            Value v = sp[-1];
            ip += 2;
            if ((v.kind == K_Int || v.kind == K_Float) && v.unit != 0) { SYNC(); vmUnsupported("units", NULL); }
            break;
        }
        case OP_CopyArgs: {
            /* the way each argument of the next call goes in (4 bits each): kept for the call (vmPushCall) */
            vm->copyMask = (uint64_t)U16(ip) | ((uint64_t)U16(ip + 2) << 16) | ((uint64_t)U16(ip + 4) << 32) | ((uint64_t)U16(ip + 6) << 48);
            ip += 8;
            break;
        }

        /* ---- exceptions ---- */
        case OP_RegisterHandler: {
            VmHandlerT* t = &fr->chunk->handlers[U16(ip)];
            VmHandlerRec* r = (VmHandlerRec*)malloc(sizeof(VmHandlerRec));
            ip += 2;
            if (!r) outOfMemory();
            if (t->finallyAddr >= 0) { SYNC(); vmUnsupported("finally", NULL); }
            if (t->info.types != t->typeIds) vmResolveHandler(vm, t);
            r->vm = vm; r->t = t; r->frame = vm->nframes - 1; r->scope = fr->scope; r->sp = (uint32_t)(sp - st); r->extraExits = 0;
            r->h.fn = vmCatchFn; r->h.ctx = r; r->h.info = &t->info;
            pushHandler(&r->h);
            break;
        }
        case OP_UnregisterHandler: {
            VmHandlerRec* r = (VmHandlerRec*)g_handlers;
            if (r && r->h.fn == vmCatchFn && r->frame == vm->nframes - 1) { popHandler(); free(r); }
            break;
        }
        case OP_Throw: {
            Value x = POP(), r;
            SYNC();
            r = throwValue(x);
            CHECK();
            PUSH(r);   /* (resumed: the value of the throw) */
            break;
        }
        case OP_ResumeException: { Value v = POP(), e = POP(); SYNC(); resumeThrow(e, v); CHECK(); PUSH(Undef()); break; }
        case OP_ClearPendingResume: { Value e = POP(); clearPending(e); break; }

        default:
            SYNC();
            vmUnsupported("the opcode", op < sizeof g_vmOpNames / sizeof g_vmOpNames[0] ? g_vmOpNames[op] : "?");
        }
        continue;
    unwind:
        if (!vmUnwind(vm, base)) return;
        RELOAD();
    }
}

/* The unwinding state is set (an exception passes, a catch block left with a jump or a return, a resume): the frames go until the one the target belongs
 * to, like the generated functions return one by one. 1: the run goes on (at the landing place), 0: the base frame of this run is gone - the caller (a catch
 * block's callback, a nested call) goes on unwinding. */
static void vmFreeDead(Vm* vm) {
    while (vm->dead) { VmHandlerRec* r = vm->dead; vm->dead = r->nextDead; free(r); }
}
static int vmUnwind(Vm* vm, int32_t base) {
    for (;;) {
        VmFrame* f = &vm->frames[vm->nframes - 1];
        Handler* tg = g_unwind.target;
        VmHandlerRec* rec = tg && tg->fn == vmCatchFn ? (VmHandlerRec*)tg : NULL;
        if (f->kind != FR_CATCH && rec && rec->frame == vm->nframes - 1) {
            /* the frame that owns the try: leave the scopes inside it, land */
            uint8_t kind = g_unwind.kind;
            Value v = g_unwind.value;
            uint32_t label = g_unwind.label, extra = rec->extraExits;
            vm->sp = rec->sp;
            vmLeaveTo(vm, f, rec->scope);
            while (extra--) { VmScope* s = f->scope; f->scope = s->parent; scLeave(vm, s); }
            clearUnwind();
            free(rec);
            vmFreeDead(vm);
            switch (kind) {
                case UW_JUMP: f->ip = f->chunk->code + label; return 1;
                case UW_RETHROW: throwValue(v, false); if (!g_unwind.active) return 1; continue;
                case UW_RETURN: if (!vmReturn(vm, v, base)) return 0; return 1;
                default: fatal("internal error of the VM: an unwinding of an unknown kind.");
            }
        }
        /* a frame above the target: it goes */
        if (f->kind == FR_CATCH && tg != &f->rec->h && !(g_unwind.kind == UW_RESUME && g_unwind.token == f->rec->token)) {
            /* a catch block left by another exception (or a resume of another throw): nothing comes back to its handler - it goes when the
             * runtime is done with it (after its throwValue returned: at the next landing) */
            f->rec->nextDead = vm->dead;
            vm->dead = f->rec;
        }
        vm->sp = f->spBase;
        vmDropHandlers(vm, vm->nframes - 1);
        vmLeaveTo(vm, f, vmStopOf(vm, f));
        if (f->kind == FR_CTOR || f->kind == FR_BASECTOR) ctorEnd(f->self);   /* (a constructor that ended with an exception made no object: no destructor) */
        vm->nframes--;
        if (vm->nframes <= base) return 0;
    }
}
