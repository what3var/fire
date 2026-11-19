/* fire VM in C: what the interpreter (vm_run.c) stands on - scopes, the classes as the runtime sees them (registration, layout, fields, statics), the
 * choice of a method or constructor (RuntimeClass.Candidates, Overloads.Select), access checks, calls (frames, arguments, default values, nested runs) and
 * the callbacks of the runtime (destructors, ToString, lambdas a native calls, the exceptions the runtime makes). */

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
#define VM_NNATIVES ((uint32_t)(sizeof g_vmNativeNames / sizeof g_vmNativeNames[0]))

/* The VM whose program runs (the runtime's callbacks - destructors, catch blocks, the exceptions it makes - have no context of their own; with fire threads,
 * phase B step 3, one per thread). */
static FIRE_TLS Vm* g_vmCur = NULL;   /* (the strand's: a fire thread has a Vm of its own - vm_threads.c) */
static const uint8_t g_vmUnwindStub[1] = { 255 };   /* the code of a frame whose entry (a safe point) started an unwinding: it goes at once (vm_run.c) */

static void vmRun(Vm* vm, int32_t base);
static int vmUnwind(Vm* vm, int32_t base);

FIRE_NORETURN static void vmUnsupported(const char* what, const char* name) {
    char text[300];
    snprintf(text, sizeof text, "the VM in C does not support %s%s%s yet", what, name ? " " : "", name ? name : "");
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
    s->hi = 0;
    s->own = scopeList(poolMark());
    s->temp = scopeList(0xFFFFFFFFu);
    s->temp.home = &s->own;
    s->own.parent = parent ? &parent->own : NULL;   /* (TakeUpwards: a block scope goes to the scope around it, a call scope to the global scope) */
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
    retain(v);
    if (s->n < s->hi) { Value old = s->slots[s->n]; s->slots[s->n++] = v; release(old); return; }   /* (a global written before its declaration: the declaration's value counts) */
    if (s->n == s->cap) scReserve(s, s->n + 1);
    s->slots[s->n++] = v;
    s->hi = s->n;
}
static Value* scSlot(VmScope* s, uint32_t slot) {
    if (FIRE_UNLIKELY(slot >= s->hi)) { scReserve(s, slot + 1); while (s->hi <= slot) s->slots[s->hi++] = Undef(); }
    return &s->slots[slot];
}
/* The scope ends: what it owns is destroyed (destructors run), its temporaries and slots let go. */
static void scLeave(Vm* vm, VmScope* s) {
    uint32_t i;
    leave(&s->temp);
    leave(&s->own);
    for (i = 0; i < s->hi; i++) release(s->slots[i]);
    s->n = s->hi = 0;
    s->nextFree = vm->freeScopes;
    vm->freeScopes = s;
}
static VmScope* scAncestor(VmScope* s, uint32_t depth) { while (depth-- && s->parent) s = s->parent; return s; }

/* ---- classes as the runtime sees them ---- */
static VmClass* vmClassNamed(VmProgram* p, const char* name) { return name ? (VmClass*)fbMapGet(&p->classByName, name) : NULL; }
static VmClass* vmClassOf(Vm* vm, uint32_t cls) { return cls >= vm->firstId && cls - vm->firstId < vm->nids ? vm->byId[cls - vm->firstId] : NULL; }
static VmClass* vmClassOfValue(Vm* vm, Value v) { return v.kind == K_Class && leafAlive(v) ? vmClassOf(vm, asObj(v)->cls) : NULL; }   /* (a destroyed one: its memory may be gone - a thread sees what the main program destroyed) */
static const char* vmPlainName(const char* name, char* buf, size_t cap) {
    const char* tick = strchr(name, '`');
    size_t n = tick ? (size_t)(tick - name) : strlen(name);
    if (n >= cap) n = cap - 1;
    memcpy(buf, name, n);
    buf[n] = 0;
    return buf;
}

static void vmLayout(VmProgram* p, VmClass* c) {
    uint32_t i, n;
    if (c->layoutDone) return;
    c->layoutDone = true;
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
/* The FieldInfo of a field (the class that declares it: the nearest in the chain). */
static const VmFieldInfo* vmFieldInfo(VmClass* c, const char* name, VmClass** declaring) {
    for (; c; c = c->base) {
        uint32_t i;
        for (i = 0; i < c->nfinfo; i++) if (c->finfo[i].name == name) { if (declaring) *declaring = c; return &c->finfo[i]; }
    }
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
static bool vmClassIs(VmClass* c, const char* name) {
    for (; c; c = c->base) {
        uint32_t i;
        if (c->name == name || strcmp(c->name, name) == 0) return true;
        for (i = 0; i < c->ninterfaces; i++) if (c->interfaces[i] && strcmp(c->interfaces[i], name) == 0) return true;
    }
    return false;
}
static int vmClassDistance(VmClass* c, const char* name) {
    int d = 0;
    for (; c; c = c->base, d++) if (strcmp(c->name, name) == 0) return d;
    return -1;
}

/* ---- the choice of a method (RuntimeClass.Candidates, Overloads.Select) ---- */
static bool vmAllTrailingDefaults(VmProto* p, int supplied) {
    int i;
    for (i = supplied; i < p->nparams; i++) if ((uint32_t)i >= p->ndefaults || !p->defaults[i]) return false;
    return true;
}
static const char* vmTypeAt(VmProto* p, int i) { return p->paramTypes && (uint32_t)i < p->nparamTypes ? p->paramTypes[i] : NULL; }
static bool vmLoose(const char* d) { return !d || !*d || d[0] == 'g'; }
static bool vmOverrides(VmProto* derived, VmProto* b) {
    int i;
    if (derived->nparams != b->nparams) return false;
    for (i = 0; i < derived->nparams; i++) {
        const char* x = vmTypeAt(derived, i);
        const char* y = vmTypeAt(b, i);
        if ((x == y) || (x && y && strcmp(x, y) == 0) || vmLoose(x) || vmLoose(y)) continue;
        return false;
    }
    return true;
}
static VmCands* vmCandidates(VmProgram* p, VmClass* cls, const char* name, int argc) {
    VmCands* c;
    VmClass* rc;
    VmProto* tmp[64];
    VmClass* dtmp[64];
    VmProto* seen[256];
    uint32_t n = 0, nseen = 0, i, k;
    for (c = cls->cands; c; c = c->next) if (c->name == name && c->argc == argc) return c;
    for (rc = cls; rc; rc = rc->base) {
        VmMethod* m = NULL;
        VmProto* fit[64];
        uint32_t nfit = 0;
        for (i = 0; i < rc->nmethods; i++) if (rc->methods[i].name == name) { m = &rc->methods[i]; break; }
        if (!m) continue;
        for (k = 0; k < m->nov; k++) {
            VmProto* q = m->ov[k];
            uint32_t s;
            bool over = false;
            if (!(q->nparams == argc || (q->nparams > argc && vmAllTrailingDefaults(q, argc)))) continue;
            for (s = 0; s < nseen && !over; s++) if (vmOverrides(seen[s], q)) over = true;
            if (!over && nfit < 64) fit[nfit++] = q;
        }
        /* (exact number of parameters first, then fewer parameters; stable) */
        for (i = 1; i < nfit; i++) {
            VmProto* x = fit[i];
            int kx = x->nparams == argc ? 0 : 1;
            k = i;
            while (k > 0) {
                VmProto* y = fit[k - 1];
                int ky = y->nparams == argc ? 0 : 1;
                if (ky > kx || (ky == kx && y->nparams > x->nparams)) { fit[k] = y; k--; } else break;
            }
            fit[k] = x;
        }
        for (i = 0; i < nfit && n < 64; i++) { tmp[n] = fit[i]; dtmp[n] = rc; n++; }
        for (k = 0; k < m->nov && nseen < 256; k++) seen[nseen++] = m->ov[k];
    }
    c = (VmCands*)vmAlloc(&p->arena, sizeof(VmCands));
    c->name = name; c->argc = argc; c->n = n;
    c->protos = (VmProto**)vmAlloc(&p->arena, (n + 1) * sizeof(VmProto*));
    c->declaring = (VmClass**)vmAlloc(&p->arena, (n + 1) * sizeof(VmClass*));
    memcpy(c->protos, tmp, n * sizeof(VmProto*));
    memcpy(c->declaring, dtmp, n * sizeof(VmClass*));
    if (n >= 2)
        for (i = 0; i < n && !c->runtimeChoice; i++) {
            int a;
            if (c->protos[i]->typeParamCount > 0) c->runtimeChoice = true;
            for (a = 0; a < argc && a < c->protos[i]->nparams; a++) { const char* t = vmTypeAt(c->protos[i], a); if (t && *t) c->runtimeChoice = true; }
        }
    c->next = cls->cands;
    cls->cands = c;
    return c;
}

enum { SC_NOMATCH = -1, SC_ANY = 10, SC_GENERIC = 20, SC_OBJECT = 15, SC_IFACE = 30, SC_CLASS = 1000, SC_EXACT = 1000, SC_INT2FLOAT = 900, SC_INT2WORD = 950, SC_UNDEF = 1 };
static bool vmFitsDeclared(Vm* vm, Value v, const char* spec, Value self, TArgs ctargs);
static int vmScore(Vm* vm, Value v, const char* d, Value owner, TArgs ctargs) {
    if (!d || !*d) return v.kind == K_Undefined ? SC_UNDEF : SC_ANY;
    if (v.kind == K_Undefined) return SC_UNDEF;
    switch (d[0]) {
        case 'k':
            if (!strcmp(d, "k:int")) return v.kind == K_Int ? SC_EXACT : SC_NOMATCH;
            if (!strcmp(d, "k:float")) return v.kind == K_Float ? SC_EXACT : v.kind == K_Int ? SC_INT2FLOAT : SC_NOMATCH;
            if (!strcmp(d, "k:bool")) return v.kind == K_Bool ? SC_EXACT : SC_NOMATCH;
            if (!strcmp(d, "k:char")) return v.kind == K_Char ? SC_EXACT : SC_NOMATCH;
            if (!strcmp(d, "k:string")) return v.kind == K_String ? SC_EXACT : SC_NOMATCH;
            if (!strcmp(d, "k:lambda")) return v.kind == K_Lambda ? SC_EXACT : SC_NOMATCH;
            if (!strcmp(d, "k:array")) return v.kind == K_Array || v.kind == K_Buffer ? SC_EXACT : SC_NOMATCH;
            if (!strcmp(d, "k:class")) return v.kind == K_Class ? SC_OBJECT : SC_NOMATCH;
            if (!strcmp(d, "k:word")) return v.kind == K_Word ? SC_EXACT : v.kind == K_Int ? SC_INT2WORD : SC_NOMATCH;
            return SC_ANY;
        case 'c': {
            VmClass* c = vmClassOfValue(vm, v);
            int dist;
            if (!c || !leafAlive(v)) return SC_NOMATCH;
            dist = vmClassDistance(c, d + 2);
            return dist < 0 ? SC_NOMATCH : SC_CLASS - dist;
        }
        case 'i': {
            VmClass* c;
            char plain[128];
            vmPlainName(d + 2, plain, sizeof plain);
            if (!strcmp(plain, "IEnumerable") && (v.kind == K_Array || v.kind == K_Buffer || v.kind == K_String)) return SC_IFACE;
            c = vmClassOfValue(vm, v);
            if (!c || !leafAlive(v)) return SC_NOMATCH;
            for (; c; c = c->base) { uint32_t i; for (i = 0; i < c->ninterfaces; i++) if (c->interfaces[i] && !strcmp(c->interfaces[i], d + 2)) return SC_IFACE; }
            return SC_NOMATCH;
        }
        case 'g':
            if (!d[1] || !d[2]) return SC_GENERIC;
            return vmFitsDeclared(vm, v, d + 2, owner, ctargs) ? SC_GENERIC : SC_NOMATCH;
    }
    return SC_ANY;
}
static int vmSelect(Vm* vm, VmProto* const* cands, uint32_t n, const Value* args, int argc, Value owner, TArgs ctargs) {
    int best = -1;
    int64_t bestScore = INT64_MIN;
    uint32_t c;
    for (c = 0; c < n; c++) {
        VmProto* p = cands[c];
        int64_t total = 0;
        bool fits = true;
        int i;
        for (i = 0; i < argc && i < p->nparams; i++) {
            int s = vmScore(vm, args[i], vmTypeAt(p, i), owner, p->typeParamCount > 0 ? ctargs : NULL);
            if (s == SC_NOMATCH) { fits = false; break; }
            total += s;
        }
        if (fits && total > bestScore) { best = (int)c; bestScore = total; }
    }
    return best;
}
/* The method a call of `name` with these arguments reaches on an object of `cls` (NULL: none); `declaring`: the class that declares it. */
static VmProto* vmFindMethod(Vm* vm, VmClass* cls, const char* name, int argc, const Value* args, Value self, VmClass** declaring) {
    VmCands* c = vmCandidates(vm->prog, cls, name, argc);
    int i = 0;
    if (c->n == 0) return NULL;
    if (c->runtimeChoice && args) {
        i = vmSelect(vm, c->protos, c->n, args, argc, self, vm->pendingTArgs);
        if (i < 0) i = 0;
    }
    if (declaring) *declaring = c->declaring[i];
    return c->protos[i];
}
static bool vmHasMethodNamed(VmClass* c, const char* name) {
    for (; c; c = c->base) { uint32_t i; for (i = 0; i < c->nmethods; i++) if (c->methods[i].name == name) return true; }
    return false;
}
static VmProto* vmFindCtor(Vm* vm, VmClass* c, int argc, const Value* args) {
    uint32_t i, n = 0;
    VmProto* cands[64];
    if (c->nxctors == 0 || !args) {
        for (i = 0; i < c->nctors; i++) if (c->ctors[i].arity == argc) return c->ctors[i].p;
        {
            VmProto* best = NULL;
            for (i = 0; i < c->nctors; i++) {
                VmProto* p = c->ctors[i].p;
                if (p->nparams > argc && vmAllTrailingDefaults(p, argc) && (!best || p->nparams < best->nparams)) best = p;
            }
            return best;
        }
    }
    for (i = 0; i < c->nctors && n < 64; i++) { VmProto* p = c->ctors[i].p; if (p->nparams == argc || (p->nparams > argc && vmAllTrailingDefaults(p, argc))) cands[n++] = p; }
    for (i = 0; i < c->nxctors && n < 64; i++) { VmProto* p = c->xctors[i]; if (p->nparams == argc || (p->nparams > argc && vmAllTrailingDefaults(p, argc))) cands[n++] = p; }
    if (n == 0) return NULL;
    {   /* (exact first, then fewer parameters - stable) */
        uint32_t a, b;
        for (a = 1; a < n; a++) {
            VmProto* x = cands[a];
            b = a;
            while (b > 0) {
                VmProto* y = cands[b - 1];
                int kx = x->nparams == argc ? 0 : 1, ky = y->nparams == argc ? 0 : 1;
                if (ky > kx || (ky == kx && y->nparams > x->nparams)) { cands[b] = y; b--; } else break;
            }
            cands[b] = x;
        }
    }
    {
        int k = vmSelect(vm, cands, n, args, argc, Undef(), NULL);
        return cands[k < 0 ? 0 : k];
    }
}

/* DeclaredKind.Fits: a value fits a declaration int/float/bool/... without converting it */
static bool vmKindFits(Value v, uint8_t required) {
    static const uint8_t kinds[] = { K_Bool, K_Int, K_Float, K_Char, K_String, K_Word };
    uint32_t tag = required & 0x1F;
    if (required == 0) return true;
    if (v.kind == K_Undefined) return !(required & 0x20);
    if (tag == 0 || tag > 6 || v.kind != kinds[tag - 1]) return false;
    if ((required & 0x80) && v.unit != 0) return false;
    return !(v.kind == K_Int && v.reserved != 0);
}
/* A typed specialization written in the class (`Add<int>`, SPEC 5.8.1) runs instead of the generic method when the object's type arguments are its
 * own and the arguments fit (VM.SpecializationFor). */
static const char* vmTypeArgOf(Vm* vm, Value obj, const char* declaring, int k);
static VmProto* vmSpecializationFor(Vm* vm, VmProto* p, Value self, const Value* args, int argc) {
    uint32_t s;
    for (s = 0; s < p->nspecs; s++) {
        VmProto* sp = p->specs[s];
        bool fits = sp->specArgs != NULL && argc == sp->nparams;
        int i;
        uint32_t k;
        for (i = 0; fits && i < argc && i < 64; i++)
            fits = sp->specParamKinds && (uint32_t)i < sp->nspecParamKinds ? vmKindFits(args[i], sp->specParamKinds[i]) : !((sp->specDefinedMask >> i) & 1) || args[i].kind != K_Undefined;
        for (k = 0; fits && k < sp->nspecArgs; k++) {
            const char* want = sp->specArgs[k];
            const char* have;
            if (want && !strcmp(want, "any")) continue;
            have = sp->specOwner ? vmTypeArgOf(vm, self, sp->specOwner, (int)k) : NULL;
            fits = have && want && !strcmp(have, want);
        }
        if (fits) return sp;
    }
    return p;
}

/* ---- access (VM.IsMemberAccessAllowed): private only from the declaring class, protected from it and the classes derived from it ---- */
static bool vmAccessAllowed(VmChunk* caller, VmClass* declaring, uint8_t access) {
    VmClass* rc = caller ? caller->owner : NULL;
    if (access == 0) return true;
    if (!rc || !declaring) return false;
    if (access == 1) return rc == declaring;
    for (; rc; rc = rc->base) if (rc == declaring) return true;
    return false;
}
static const char* vmAccessName(uint8_t access) { return access == 1 ? "private" : access == 2 ? "protected" : "public"; }
/* a proto's access as FieldInfo counts it (0 public, 1 private, 2 protected) */
static uint8_t vmProtoAccess(VmProto* p) { return p->access <= 1 ? 0 : (uint8_t)(p->access - 1); }

/* ---- calls ---- */
static Value vmCallNested(Vm* vm, VmProto* p, int argc, const Value* args, Value self, uint8_t kind);

/* The way each argument goes in (CopyArgs, 4 bits each: VM.ApplyCopyMask): 1 flat copy, 2 deep copy, 3 an address (a `ref` parameter keeps it), 4/7 the
 * result of a call - it belongs to the call when it lies freshly with the caller, 5 `take x`, 6 a claim of a setter, 8 `try take x`. */
static void vmApplyCopyMask(Vm* vm, VmScope* s, uint64_t mask, VmProto* p, int argc, VmScope* from) {
    int i;
    for (i = 0; i < argc && i < 16; i++) {
        uint32_t bits = (uint32_t)(mask >> (4 * i)) & 15u;
        Value* a = &s->slots[i];
        if (bits == 0) continue;
        if (bits == 3) { if (!((p->refMask >> i) & 1)) { Value v = ptrRead(*a); retain(v); release(*a); *a = v; } continue; }
        if (!ownedOf(*a) && bits != 1 && bits != 2) continue;
        switch (bits) {
            case 1: case 2: {
                Value c = copyValue(*a, bits == 2, &s->own);
                if (g_unwind.active) return;
                retain(c); release(*a); *a = c;
                break;
            }
            case 4: case 7: case 6:
                if (from) { hoistFrom(*a, &from->temp, &s->own); }
                break;
            case 5:
                if (takeAlive(*a)) takeToList(*a, &s->own);
                break;
            case 8:
                if (from && ownedOf(*a)) {
                    Owned* o = ownedOf(*a);
                    VmScope* sc;
                    for (sc = from; sc; sc = sc->parent) if (o->owner == &sc->own || o->owner == &sc->temp) { takeToList(*a, &s->own); break; }
                }
                break;
            default: break;
        }
    }
}

/* Pushes the frame of a call of `p`: its arguments are the top `argc` values of the stack (they become slots 0.. of its scope; the callee holds its own -
 * retain, like the generated code), `drop` values below them go as well (the callee or the receiver). */
/* A call with named arguments (`f(1, size: 2)`, VM.FillNamedArgs): the positional arguments first, each named one at the parameter of its name; the
 * parameters that got none are `missing` (their default values follow). False: the ArgumentException is set. */
static bool vmNamedArgs(VmProto* p, VmScope* s, int argc, const char* names, uint64_t* missing) {
    Value tmp[64];
    bool given[64];
    const char* at[64];
    size_t len[64];
    int k = 0, positional, i, j;
    char text[300];
    const char* c = names;
    for (;;) {
        const char* e = strchr(c, '|');
        if (k < 64) { at[k] = c; len[k] = e ? (size_t)(e - c) : strlen(c); k++; }
        if (!e) break;
        c = e + 1;
    }
    positional = argc - k;
    if (!p->paramNames || p->nparams > 64) { fireError("ArgumentException", "Named arguments need a method whose parameters have names."); return false; }
    if (positional > p->nparams || positional < 0) {
        snprintf(text, sizeof text, "Wrong number of arguments: expected %d, got %d.", p->nparams, argc);
        fireError("ArgumentException", text);
        return false;
    }
    for (i = 0; i < p->nparams; i++) { given[i] = false; tmp[i] = Undef(); }
    for (i = 0; i < positional; i++) { tmp[i] = s->slots[i]; given[i] = true; }
    for (j = 0; j < k; j++) {
        int found = -1;
        for (i = 0; i < p->nparams && (uint32_t)i < p->nparamNames; i++)
            if (p->paramNames[i] && strlen(p->paramNames[i]) == len[j] && !strncmp(p->paramNames[i], at[j], len[j])) { found = i; break; }
        if (found < 0 || given[found]) {
            snprintf(text, sizeof text, found < 0 ? "There is no parameter '%.*s'." : "The parameter '%.*s' is given twice.", (int)len[j], at[j]);
            fireError("ArgumentException", text);
            return false;
        }
        tmp[found] = s->slots[positional + j];
        given[found] = true;
    }
    for (i = 0; i < p->nparams; i++) {
        if (given[i]) continue;
        if (!((uint32_t)i < p->ndefaults && p->defaults[i])) {
            snprintf(text, sizeof text, "The parameter '%s' has no value (and no default value).", (uint32_t)i < p->nparamNames && p->paramNames[i] ? p->paramNames[i] : "?");
            fireError("ArgumentException", text);
            return false;
        }
        *missing |= (uint64_t)1 << i;
    }
    for (i = 0; i < p->nparams; i++) s->slots[i] = tmp[i];
    s->n = s->hi = (uint32_t)p->nparams;
    return true;
}

static void vmPushCall(Vm* vm, VmProto* p, int argc, int drop, Value self, uint8_t kind) {
    VmFrame* f;
    VmScope* s;
    int i, n;
    uint64_t missing = 0;
    bool named = false, bad = false;
    if (FIRE_UNLIKELY(p->nspecs != 0) && self.kind == K_Class) p = vmSpecializationFor(vm, p, self, vm->stack + vm->sp - argc, argc);
    n = argc > p->nparams ? argc : p->nparams;
    VmScope* from = vm->nframes > 0 ? vm->frames[vm->nframes - 1].scope : NULL;
    if (FIRE_UNLIKELY(vm->nframes >= VM_FRAMES)) fatal("Stack overflow: too many nested calls.");
    s = scNew(vm, vm->global);
    scReserve(s, (uint32_t)n + 4);
    for (i = 0; i < argc; i++) { Value a = vm->stack[vm->sp - (uint32_t)argc + (uint32_t)i]; retain(a); s->slots[i] = a; }
    s->n = s->hi = (uint32_t)argc;
    if (FIRE_UNLIKELY(vm->copyMask != 0)) {
        uint64_t mask = vm->copyMask;
        vm->copyMask = 0;
        vmApplyCopyMask(vm, s, mask, p, argc, from);
    }
    if (FIRE_UNLIKELY(vm->pendingArgNames != NULL)) {
        const char* names = vm->pendingArgNames;
        vm->pendingArgNames = NULL;
        named = true;
        bad = !vmNamedArgs(p, s, argc, names, &missing);
    }
    vm->sp -= (uint32_t)(argc + drop);
    f = &vm->frames[vm->nframes++];
    f->chunk = p->chunk;
    f->ip = p->chunk->code;
    f->scope = s;
    f->base = s;
    f->self = self;
    f->proto = p;
    f->ctargs = p->typeParamCount > 0 ? vm->pendingTArgs : NULL;
    if (p->typeParamCount > 0 || vm->pendingTArgsFor == 1) { vm->pendingTArgs = NULL; vm->pendingTArgsFor = 0; }
    f->spBase = vm->sp;
    f->kind = kind;
    f->catchEnded = 0;
    f->retMode = RM_PUSH;
    f->after = Undef();
    f->rec = NULL;
    f->recInit = NULL;
    f->nfin = vm->nfin;
    if (FIRE_UNLIKELY(named)) {
        if (bad) { f->ip = g_vmUnwindStub; return; }
        /* (the parameters a named call left out: their default values) */
        for (i = 0; i < p->nparams; i++) {
            Value v;
            Owned* o;
            if (!((missing >> i) & 1)) continue;
            v = vmCallNested(vm, p->defaults[i], 0, NULL, self, FR_NESTED);
            if (g_unwind.active) { f->ip = g_vmUnwindStub; return; }
            s->slots[i] = v;
            o = ownedOf(v);
            if (o && !o->owner) ownLink(&s->own, o);
        }
        argc = p->nparams;
    }
    /* missing arguments: their default values (a function of their own, run nested), else undefined */
    for (i = argc; i < p->nparams; i++) {
        VmProto* d = (uint32_t)i < p->ndefaults ? p->defaults[i] : NULL;
        Value v = Undef();
        if (d) {
            v = vmCallNested(vm, d, 0, NULL, self, FR_NESTED);
            if (g_unwind.active) { s->slots[s->n++] = Undef(); s->hi = s->n; continue; }
        }
        /* (the slot takes the count of the trip; what has no owner belongs to the call) */
        s->slots[s->n++] = v;
        s->hi = s->n;
        if (d) { Owned* o = ownedOf(v); if (o && !o->owner) ownLink(&s->own, o); }
    }
    /* the safe point at a function's entry (CppGenerator: pollSignals - the GIL now and then, leave/terminate): an unwinding it starts takes the new
     * frame with it at once */
    if (FIRE_UNLIKELY(pollSignals())) f->ip = g_vmUnwindStub;
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

/* The end of a frame of a record's constructor: the record is no longer being constructed. */
static void vmRecInitEnd(VmFrame* f) {
    RecInit** p;
    if (!f->recInit) return;
    for (p = &g_recInit; *p; p = &(*p)->next) if (*p == f->recInit) { *p = f->recInit->next; break; }
    free(f->recInit);
    f->recInit = NULL;
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
    uint8_t kind = f->kind, mode = f->retMode;
    Value self = f->self, after = f->after;
    vmDropHandlers(vm, vm->nframes - 1);
    /* what the call returns does not die with its scopes: it goes to the caller (SPEC 2.3) */
    for (s = f->scope; s && s != stop && n < 62; s = s->parent) { lists[n++] = &s->temp; lists[n++] = &s->own; }
    if (n) transferTree(r, lists, n);
    retain(r);   /* (the count for the trip; the caller adopts it) */
    vm->sp = f->spBase;
    vm->nfin = f->nfin;
    vmRecInitEnd(f);
    vmLeaveTo(vm, f, stop);
    vm->nframes--;
    switch (kind) {
        case FR_CTOR:
            release(r);
            if (mode != RM_DROP) vm->stack[vm->sp++] = self;
            break;
        case FR_NESTED:
            vm->ret = r;
            break;
        default: {
            VmFrame* caller = &vm->frames[vm->nframes - 1];
            if (mode == RM_PUSH) vm->stack[vm->sp++] = r;
            else if (mode == RM_VALUE) vm->stack[vm->sp++] = after;
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
    VmProto* m = c ? vmFindMethod(vm, c, vmName(vm->prog, "ToString"), 0, NULL, self, NULL) : NULL;
    if (!m) return Undef();
    return vmCallNested(vm, m, 0, NULL, self, FR_NESTED);   /* (counted for the trip: userToString adopts it) */
}
/* A lambda of the VM that the runtime (a native) calls: the proto rides as the hidden last capture. */
static VmProto* vmLamProto(Lam* l) { return (VmProto*)(uintptr_t)lamCaps(l)[l->ncaps - 1].i; }
static Value vmLamThunk(Value lam, const Value* args);
static void vmLamEnter(Vm* vm, Lam* l);

/* An exception object of the program by the name of its class, made with its constructor of `argc` arguments, owned by the global scope (the generated
 * makeDivideError, makeClassError, ...). Undefined when the program has no such class. */
static Value vmMakeException(const char* cls, int argc, const Value* args) {
    Vm* vm = g_vmCur;
    VmClass* c;
    VmProto* ctor;
    Value o;
    UnwindState saved;
    if (!vm) return Undef();
    c = vmClassNamed(vm->prog, vmName(vm->prog, cls));
    if (!c) return Undef();
    ctor = vmFindCtor(vm, c, argc, args);
    o = newObject(c->id, c->nlayout, g_globalOwn);
    saved = g_unwind;
    clearUnwind();
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
    rec->inCatch = true;
    s = scNew(vm, rec->scope);
    scDefine(s, e);
    f = &vm->frames[vm->nframes++];
    f->chunk = owner->chunk;
    f->ip = owner->chunk->code + rec->t->addrs[ci];
    f->scope = s;
    f->base = s;
    f->self = owner->self;
    f->proto = owner->proto;
    f->ctargs = owner->ctargs;
    f->spBase = vm->sp;
    f->kind = FR_CATCH;
    f->catchEnded = 0;
    f->retMode = RM_PUSH;
    f->after = Undef();
    f->rec = rec;
    f->recInit = NULL;
    f->nfin = vm->nfin;
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

/* ---- records (step 9 of the native backend): `==` by content, the hash of a key by content - the descriptor's callbacks get the value, so the class ---- */
static bool vmRecEq(Value a, Value b) {
    Obj* x = asObj(a);
    Obj* y = asObj(b);
    uint32_t i;
    if (x->cls != y->cls) return false;
    for (i = 0; i < x->nfields; i++) if (!recFieldEq(objFields(x)[i], objFields(y)[i])) return false;
    return true;
}
static int64_t vmRecHash(Value a) {
    Obj* x = asObj(a);
    uint64_t h = 1469598103934665603ull ^ x->cls;
    uint32_t i;
    for (i = 0; i < x->nfields; i++) h = fire_mixHash(h * 31u + (uint64_t)keyHash(objFields(x)[i]));
    return (int64_t)h;
}

static bool vmRecCheck(Value a) {
    Vm* vm = g_vmCur;
    Obj* x = asObj(a);
    VmClass* c = vmClassOf(vm, x->cls);
    uint32_t i;
    for (i = 0; c && i < x->nfields && i < c->nlayout; i++)
        if (!recFieldOk(objFields(x)[i])) return recordFieldError(a, c->layout[i], objFields(x)[i]);
    return true;
}

/* ---- the class descriptors: the program's classes registered at run time (like fireRegisterPlugin does for the classes of a plugin) ---- */
static void vmRegisterClasses(Vm* vm) {
    VmProgram* p = vm->prog;
    uint32_t base = g_classDescCount, i, n = p->nclasses;
    const char* message = vmName(p, "message");
    const char* toString = vmName(p, "ToString");
    const char* exception = vmName(p, "Exception");
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
        const char* tick;
        vmLayout(p, c);
        for (k = c; k; k = k->base) { depth++; if (k->name == exception) c->isException = true; if (k->dtor) c->hasDtor = true; }
        tick = strchr(c->name, '`');
        c->typeParams = tick ? atoi(tick + 1) : 0;
        d->name = c->name;
        d->nfields = c->nlayout;
        d->messageIndex = vmFieldIndex(c, message);   /* (any object can be thrown: its `message`, if it has one) */
        d->ticksIndex = -1;
        d->destroy = c->hasDtor ? vmDestroyObj : NULL;
        d->toString = vmHasMethodNamed(c, toString) ? vmToString : NULL;
        /* the catch types an object of the class is: every class of its chain (the catch type of class k is its id + 1; FIRE_EXC_ANY is 0) */
        {   /* (any object can be thrown and caught by its class)  */
            int32_t* types = (int32_t*)malloc((depth + 1) * sizeof(int32_t));
            if (!types) outOfMemory();
            for (k = c; k; k = k->base) types[t++] = (int32_t)k->id + 1;
            types[t] = -1;
            vm->excTypes[i] = types;
            d->excTypes = types;
        }
        d->flags = 0;
        for (k = c; k; k = k->base) if (k->flags & VC_NOCOPY) d->flags = 1;
        if (c->flags & VC_RECORD) { d->recEq = vmRecEq; d->recHash = vmRecHash; d->recCheck = vmRecCheck; }
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
    const char* exception = vmName(vm->prog, "Exception");
    for (i = 0; i < t->ncatch; i++) {
        VmClass* c = t->types[i] ? vmClassNamed(vm->prog, t->types[i]) : NULL;
        t->typeIds[i] = !t->types[i] || t->types[i] == exception ? FIRE_EXC_ANY : c ? (int32_t)c->id + 1 : 0x7FFFFFFF;
    }
    t->info.ncatch = t->ncatch;
    t->info.types = t->typeIds;
    t->info.hasFinally = t->finallyAddr >= 0 ? 1 : 0;
    t->info.typeMap = NULL;
    t->resolved = true;
}

/* The conversion functions of the units (UnitConvFn has no context): a fixed pool of trampolines, each with its index into the program's classes of
 * conversion functions (VM_SPIKE point 1, "ein fester Vorrat an Trampolinen"). */
static Value vmUnitConv(uint32_t index, bool to, Value v) {
    Vm* vm = g_vmCur;
    VmClass* c;
    VmProto* m;
    Value r;
    if (!vm || index >= vm->prog->nconv || !(c = vmClassNamed(vm->prog, vm->prog->convClasses[index]))) return v;
    m = vmFindMethod(vm, c, vmName(vm->prog, to ? "To" : "From"), 1, &v, Undef(), NULL);
    if (!m) return v;
    r = vmCallNested(vm, m, 1, &v, Undef(), FR_NESTED);
    if (g_unwind.active) return Undef();
    adopt(r, &vm->frames[vm->nframes - 1].scope->temp);
    return r;
}
#define VM_CONV(i) static Value vmUnitTo##i(Value v) { return vmUnitConv(i, true, v); } static Value vmUnitFrom##i(Value v) { return vmUnitConv(i, false, v); }
VM_CONV(0) VM_CONV(1) VM_CONV(2) VM_CONV(3) VM_CONV(4) VM_CONV(5) VM_CONV(6) VM_CONV(7)
VM_CONV(8) VM_CONV(9) VM_CONV(10) VM_CONV(11) VM_CONV(12) VM_CONV(13) VM_CONV(14) VM_CONV(15)
static const UnitConvFn g_vmUnitTo[16] = { vmUnitTo0, vmUnitTo1, vmUnitTo2, vmUnitTo3, vmUnitTo4, vmUnitTo5, vmUnitTo6, vmUnitTo7,
    vmUnitTo8, vmUnitTo9, vmUnitTo10, vmUnitTo11, vmUnitTo12, vmUnitTo13, vmUnitTo14, vmUnitTo15 };
static const UnitConvFn g_vmUnitFrom[16] = { vmUnitFrom0, vmUnitFrom1, vmUnitFrom2, vmUnitFrom3, vmUnitFrom4, vmUnitFrom5, vmUnitFrom6, vmUnitFrom7,
    vmUnitFrom8, vmUnitFrom9, vmUnitFrom10, vmUnitFrom11, vmUnitFrom12, vmUnitFrom13, vmUnitFrom14, vmUnitFrom15 };

/* ---- the units of the program: its table becomes the runtime's (unitsInit), its symbols the ones a text may name (g_textUnits) ---- */
static TextUnit* g_vmTextUnits;
static void vmInstallUnits(Vm* vm) {
    VmProgram* p = vm->prog;
    static const char* const builtins[] = { "m", "g", "s", "b", "B", "l", "min", "h", "d" };
    static const char* const bases[] = { "m", "g", "s", "b", "b", "m", "s", "s", "s" };
    static const int exps[] = { 1, 1, 1, 1, 1, 3, 1, 1, 1 };
    static const double scales[] = { 1.0, 1.0, 1.0, 1.0, 8.0, 0.001, 60.0, 3600.0, 86400.0 };
    static const bool prefixable[] = { true, true, true, true, true, true, false, false, false };
    uint32_t i, rows = p->unitRows, nt = 0;
    for (i = 0; i < FIRE_NDIMS; i++) g_dimNames[i] = i < p->ndims ? p->dimNames[i] : "";
    unitsInit(p->unitInit, rows, g_vmUnitTo, g_vmUnitFrom);
    g_vmTextUnits = (TextUnit*)calloc(16, sizeof(TextUnit));
    for (i = 0; i < 9; i++) {
        int32_t dims[FIRE_NDIMS];
        uint32_t d;
        memset(dims, 0, sizeof dims);
        for (d = 0; d < FIRE_NDIMS && g_dimNames[d][0] && strcmp(g_dimNames[d], bases[i]) != 0; d++) { }
        if (d >= FIRE_NDIMS) continue;
        if (!g_dimNames[d][0]) { g_dimNames[d] = bases[i]; if (d >= p->ndims) p->ndims = d + 1; }
        dims[d] = exps[i];
        g_vmTextUnits[nt].symbol = builtins[i];
        g_vmTextUnits[nt].unit = unitIntern(dims, scales[i], builtins[i]);
        g_vmTextUnits[nt].prefixable = prefixable[i];
        nt++;
    }
    {   /* the units of the program (`unit in = 2.54cm`): by their symbol and their name */
        uint32_t k;
        TextUnit* more = (TextUnit*)realloc(g_vmTextUnits, (nt + 2 * p->ncustomUnits + 1) * sizeof(TextUnit));
        if (!more) outOfMemory();
        g_vmTextUnits = more;
        for (k = 0; k < p->ncustomUnits; k++) {
            struct VmCustomUnit* cu = &p->customUnits[k];
            uint32_t u = 0, r;
            for (r = 1; r < rows && !u; r++) if (p->unitInit[r].display && !strcmp(p->unitInit[r].display, cu->symbol)) u = r;   /* (the row of the file: with its offset and conversion functions) */
            if (!u) u = cu->offset != 0.0 ? unitAppend(cu->dims, cu->scale, cu->symbol, cu->offset, -1, -1) : unitIntern(cu->dims, cu->scale, cu->symbol);
            g_vmTextUnits[nt].symbol = cu->symbol; g_vmTextUnits[nt].unit = u; g_vmTextUnits[nt].prefixable = cu->prefixed; nt++;
            if (strcmp(cu->name, cu->symbol) != 0) { g_vmTextUnits[nt].symbol = cu->name; g_vmTextUnits[nt].unit = u; g_vmTextUnits[nt].prefixable = false; nt++; }
        }
    }
    g_textUnits = g_vmTextUnits;
    g_textUnitCount = nt;
}

/* The unit a declaration demands (`int x : mm`, CheckUnit, a field's requiredUnit): Unit.Parse of the text - "§"-coded units of the program, the units a
 * text may name, else a word of its own as a new base dimension (`apples`). The result is kept per text (the texts are interned or constants). */
typedef struct VmDemand { const char* text; uint32_t unit; bool any, toFloat, dimensionOnly; Value display; struct VmDemand* next; } VmDemand;
static VmDemand* g_vmDemands;
static uint32_t vmUnitOfName(Vm* vm, const char* text) {
    fire_char16 buf[128];
    uint32_t n = utf8ToUtf16(text, buf, 128), u;
    if (!text[0] || !strcmp(text, "unitless")) return 0;
    if ((unsigned char)text[0] == 0xC2 && (unsigned char)text[1] == 0xA7) {   /* "§symbol§dims§scale§offset§conv§ulconv" */
        char tmp[256], *parts[8];
        int np = 0;
        int32_t dims[FIRE_NDIMS];
        char* q;
        snprintf(tmp, sizeof tmp, "%s", text);
        for (q = tmp; *q && np < 8;) {
            if ((unsigned char)q[0] == 0xC2 && (unsigned char)q[1] == 0xA7) { *q = 0; q += 2; parts[np++] = q; } else q++;
        }
        memset(dims, 0, sizeof dims);
        if (np >= 4) {
            char* e = parts[1];
            while (*e) {
                char* comma = strchr(e, ',');
                char* colon;
                if (comma) *comma = 0;
                colon = strrchr(e, ':');
                if (colon) {
                    uint32_t d;
                    const char* sym;
                    *colon = 0;
                    sym = vmName(vm->prog, e);
                    for (d = 0; d < FIRE_NDIMS && g_dimNames[d][0] && strcmp(g_dimNames[d], sym) != 0; d++) { }
                    if (d < FIRE_NDIMS) { if (!g_dimNames[d][0]) g_dimNames[d] = sym; dims[d] = atoi(colon + 1); }
                }
                if (!comma) break;
                e = comma + 1;
            }
            {   /* (with an offset or conversion functions: the row of that unit - the file's when it has one) */
                const char* sym = vmName(vm->prog, parts[0]);
                double scale = atof(parts[2]), offset = np > 3 ? atof(parts[3]) : 0.0;
                int32_t conv[2] = { -1, -1 };
                int q;
                uint32_t r;
                for (q = 0; q < 2; q++) {
                    const char* cn = np > 4 + q && parts[4 + q][0] ? vmName(vm->prog, parts[4 + q]) : NULL;
                    uint32_t c;
                    if (!cn) continue;
                    for (c = 0; c < vm->prog->nconv && vm->prog->convClasses[c] != cn; c++) { }
                    if (c == vm->prog->nconv && c < 16) vm->prog->convClasses[vm->prog->nconv++] = cn;
                    if (c < 16) conv[q] = (int32_t)c;
                }
                if (offset == 0.0 && conv[0] < 0 && conv[1] < 0) return unitIntern(dims, scale, sym);
                for (r = 1; r < g_udCount; r++)
                    if (g_ud[r].display && !strcmp(g_ud[r].display, sym) && g_ud[r].offset == offset && g_ud[r].scale == scale) return r;
                return unitAppend(dims, scale, sym, offset, conv[0], conv[1]);
            }
        }
        return 0;
    }
    if (unitOfText(buf, n, &u)) return u;
    {   /* a word of its own: a base dimension of its own */
        int32_t dims[FIRE_NDIMS];
        uint32_t d;
        const char* sym = vmName(vm->prog, text);
        memset(dims, 0, sizeof dims);
        for (d = 0; d < FIRE_NDIMS && g_dimNames[d][0] && strcmp(g_dimNames[d], sym) != 0; d++) { }
        if (d >= FIRE_NDIMS) return 0;
        if (!g_dimNames[d][0]) g_dimNames[d] = sym;
        dims[d] = 1;
        return unitIntern(dims, 1.0, sym);
    }
}
static VmDemand* vmDemand(Vm* vm, const char* text) {
    VmDemand* d;
    const char* t = text;
    for (d = g_vmDemands; d; d = d->next) if (d->text == text) return d;
    d = (VmDemand*)vmAlloc(&vm->prog->arena, sizeof(VmDemand));
    d->text = text;
    if (*t == '#') { d->toFloat = true; t++; }
    if (!strcmp(t, "any")) d->any = true;
    else {
        char shown[256];
        if (!strncmp(t, "any ", 4)) { d->dimensionOnly = true; t += 4; }
        d->unit = vmUnitOfName(vm, t);
        if ((unsigned char)t[0] == 0xC2 && (unsigned char)t[1] == 0xA7) {
            const char* e = t + 2;
            const char* end = strstr(e, "\xC2\xA7");
            size_t len = end ? (size_t)(end - e) : strlen(e);
            snprintf(shown, sizeof shown, "%s%.*s", d->dimensionOnly ? "any " : "", (int)len, e);
        } else snprintf(shown, sizeof shown, "%s%s", d->dimensionOnly ? "any " : "", t);
        d->display = vmConstStrUtf8(vm->prog, shown);
    }
    d->next = g_vmDemands;
    g_vmDemands = d;
    return d;
}
/* Value.AdaptToUnit (CheckUnit, a field or parameter with a unit): converted into the unit of the demand, or the exception. */
static Value vmAdaptUnit(Vm* vm, Value v, const char* text) {
    VmDemand* d = vmDemand(vm, text);
    if (d->any) return d->toFloat ? intToFloat(v) : v;
    if (v.unit == d->unit && (v.kind == K_Float || (!d->toFloat && v.kind == K_Int))) return v;
    return adaptUnit(v, d->unit, d->dimensionOnly, d->toFloat, d->display);
}

/* ---- type arguments (SetTypeArgs, TypeParamIsOf, `__type_arg`): the runtime's interned TArgs, each TArg one of the VM (its display text; the VM
 * checks with its own functions - VM_SPIKE point 1: no context-less callbacks) ---- */
static const TArg* vmTArgNamed(Vm* vm, const char* text) {
    VmTArg* t;
    for (t = vm->top->targs; t; t = t->next) if (t->text == text) return &t->ta;
    t = (VmTArg*)vmAlloc(&vm->prog->arena, sizeof(VmTArg));
    t->text = text;
    t->ta.display = text;
    t->ta.isFloat = !strcmp(text, "float");
    t->ta.kind = !strcmp(text, "int") ? K_Int : !strcmp(text, "float") ? K_Float : !strcmp(text, "bool") ? K_Bool : !strcmp(text, "string") ? K_String
        : !strcmp(text, "char") ? K_Char : K_Undefined;
    t->cls = vmClassNamed(vm->prog, text);
    t->next = vm->top->targs;
    vm->top->targs = t;
    return &t->ta;
}
static TArgs vmTArgsOf(Vm* vm, VmFrame* fr, const char* text) {
    const TArg* items[16];
    uint32_t n = 0;
    const char* s = text;
    if (!text) return NULL;
    while (*s && n < 16) {
        const char* e = strchr(s, '|');
        size_t len = e ? (size_t)(e - s) : strlen(s);
        const TArg* ta = NULL;
        if (len && s[0] == '$') {
            int k = atoi(s + 1);
            const char* at = (const char*)memchr(s, '@', len);
            if (at && (size_t)(at - s) + 1 == len) { if (fr->ctargs) ta = fr->ctargs[k]; }   /* `$k@`: of the call */
            else if (fr->self.kind == K_Class && leafAlive(fr->self)) { TArgs o = objTArgs(asObj(fr->self)); if (o) ta = o[k]; }   /* `$k@Class`: of `this` */
        } else if (len) ta = vmTArgNamed(vm, vmIntern(vm->prog, s, len));
        items[n++] = ta;
        if (!e) break;
        s = e + 1;
    }
    if (!n) return NULL;
    {
        TArgs r = internTArgsN(items, n);
        struct VmTArgsLen* l;
        for (l = vm->top->targsLen; l; l = l->next) if (l->ta == r) return r;
        l = (struct VmTArgsLen*)vmAlloc(&vm->prog->arena, sizeof *l);
        l->ta = r; l->n = n; l->next = vm->top->targsLen; vm->top->targsLen = l;
        return r;
    }
}
static uint32_t vmTArgsLen(Vm* vm, TArgs ta) {
    struct VmTArgsLen* l;
    for (l = vm->top->targsLen; l; l = l->next) if (l->ta == ta) return l->n;
    return 0;
}
/* Is the type `name` (a type argument's text) `target` or one that derives from it or implements it (VM.TypeNameIsOf)? */
static bool vmTypeTextIsOf(Vm* vm, const char* name, const char* target) {
    char a[160], b[160];
    const char* lt;
    VmClass* c;
    size_t n;
    char* t;
    lt = strchr(name, '<'); n = lt ? (size_t)(lt - name) : strlen(name); if (n >= sizeof a) n = sizeof a - 1; memcpy(a, name, n); a[n] = 0;
    lt = strchr(target, '<'); n = lt ? (size_t)(lt - target) : strlen(target); if (n >= sizeof b) n = sizeof b - 1; memcpy(b, target, n); b[n] = 0;
    t = strchr(a, '`'); if (t) *t = 0;
    t = strchr(b, '`'); if (t) *t = 0;
    if (!strcmp(a, b)) return true;
    c = vmClassNamed(vm->prog, vmName(vm->prog, a));
    if (!c) {
        uint32_t i;
        for (i = 0; i < vm->prog->nclasses && !c; i++) { char p[160]; if (!strcmp(vmPlainName(vm->prog->classes[i]->name, p, sizeof p), a)) c = vm->prog->classes[i]; }
    }
    for (; c; c = c->base) {
        char p[160];
        uint32_t i;
        if (!strcmp(vmPlainName(c->name, p, sizeof p), b)) return true;
        for (i = 0; i < c->ninterfaces; i++) if (c->interfaces[i] && !strcmp(vmPlainName(c->interfaces[i], p, sizeof p), b)) return true;
    }
    return false;
}
