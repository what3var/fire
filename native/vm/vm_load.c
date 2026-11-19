/* fire VM in C (spike): the loader of the program file (.fbc, docs/spec/BYTECODE.md section 48). It reads the whole format - every part is parsed, so
 * that a file of the C# compiler is read to its end and checked like ProgramFile.Read does - and keeps what the interpreter of the spike uses: the names, the
 * classes (fields, statics, methods, constructors, destructor, base), the chunks (code, constants, functions, handlers). Skipped after parsing: units beyond
 * "unitless", enums, custom units, resources, externs, class meta (reflection), field infos, line tables, local names. */

static void* vmAlloc(VmArena* a, size_t n) {
    VmArenaBlock* b = a->head;
    n = (n + 15u) & ~(size_t)15u;
    if (!b || b->used + n > b->cap) {
        size_t cap = n > 65536u ? n : 65536u;
        b = (VmArenaBlock*)malloc(sizeof(VmArenaBlock) + 16 + cap);
        if (!b) { fputs("fire vm: out of memory\n", stderr); exit(3); }
        b->next = a->head; b->used = 0; b->cap = cap;
        a->head = b;
    }
    {
        char* base = (char*)(b + 1);
        void* p;
        base += (16 - ((uintptr_t)base & 15u)) & 15u;
        p = base + b->used;
        b->used += n;
        memset(p, 0, n);
        return p;
    }
}
static void vmArenaFree(VmArena* a) {
    VmArenaBlock* b = a->head;
    while (b) { VmArenaBlock* n = b->next; free(b); b = n; }
    a->head = NULL;
}

typedef struct VmReader {
    const uint8_t* in;
    size_t len, at;
    bool lines;
    VmProgram* p;
    VmProto** protos;
    uint32_t nprotos, capProtos;
    VmChunk** chunks;
    uint32_t nchunks, capChunks;
    jmp_buf fail;
    char error[200];
} VmReader;

static void rdFail(VmReader* r, const char* what) {
    snprintf(r->error, sizeof r->error, "The program file is damaged (%s).", what);
    longjmp(r->fail, 1);
}
static const uint8_t* rdNeed(VmReader* r, size_t n) {
    const uint8_t* p;
    if (n > r->len - r->at) rdFail(r, "it ends too early");
    p = r->in + r->at;
    r->at += n;
    return p;
}
static uint32_t rdU8(VmReader* r) { return *rdNeed(r, 1); }
static uint64_t rdUInt(VmReader* r) {
    uint64_t v = 0;
    int shift;
    for (shift = 0; shift < 64; shift += 7) {
        uint32_t b = rdU8(r);
        v |= (uint64_t)(b & 0x7F) << shift;
        if (b < 0x80) return v;
    }
    rdFail(r, "a number is too long");
    return 0;
}
static uint32_t rdCount(VmReader* r) {
    uint64_t v = rdUInt(r);
    if (v > 0x7FFFFFFF) rdFail(r, "a count is too big");
    return (uint32_t)v;
}
static int64_t rdInt(VmReader* r) { uint64_t v = rdUInt(r); return (int64_t)(v >> 1) ^ -(int64_t)(v & 1); }
static double rdF64(VmReader* r) {
    const uint8_t* b = rdNeed(r, 8);
    uint64_t u = 0;
    double d;
    int i;
    for (i = 7; i >= 0; i--) u = (u << 8) | b[i];
    memcpy(&d, &u, sizeof d);
    return d;
}
static const char* rdStr(VmReader* r) {
    uint32_t i = rdCount(r);
    if (i == 0) return NULL;
    if (i > r->p->nnames) rdFail(r, "a name out of range");
    return r->p->names[i - 1];
}
static const char* rdName(VmReader* r) {
    const char* s = rdStr(r);
    if (!s) rdFail(r, "a name is missing");
    return s;
}
static void rdStrList(VmReader* r) { uint32_t n = rdCount(r); while (n--) rdStr(r); }
static void rdOptStrList(VmReader* r) { uint32_t n = rdCount(r); if (n) { n--; while (n--) rdStr(r); } }
static void rdDims(VmReader* r) { uint32_t n = rdCount(r); while (n--) { rdName(r); rdInt(r); } }
static void rdUnitBody(VmReader* r) { rdDims(r); rdF64(r); rdStr(r); rdF64(r); rdStr(r); rdStr(r); }
static void rdUnitRef(VmReader* r) { uint32_t i = rdCount(r); if (i > r->p->nunits) rdFail(r, "a unit out of range"); }
static void rdAttributes(VmReader* r) { uint32_t n = rdCount(r); while (n--) { rdName(r); rdStrList(r); rdName(r); } }

/* ---- interning: every name of the program once, so that a name compares by its pointer ---- */
static uint32_t vmHashStr(const char* s) { uint32_t h = 2166136261u; while (*s) { h ^= (uint8_t)*s++; h *= 16777619u; } return h; }
static const char* vmIntern(VmProgram* p, const char* s, size_t n, bool copy) {
    uint32_t h, i;
    if (p->internCount * 2 + 2 > p->internCap) {
        uint32_t cap = p->internCap ? p->internCap * 2 : 1024, k;
        const char** t = (const char**)vmAlloc(&p->arena, cap * sizeof(char*));
        for (k = 0; k < p->internCap; k++) {
            const char* e = p->internTab[k];
            if (!e) continue;
            for (i = vmHashStr(e) & (cap - 1); t[i]; i = (i + 1) & (cap - 1)) { }
            t[i] = e;
        }
        p->internTab = t;
        p->internCap = cap;
    }
    {
        char tmp[512];
        const char* key = s;
        char* stored = NULL;
        if (copy) {
            if (n < sizeof tmp) { memcpy(tmp, s, n); tmp[n] = 0; key = tmp; }
            else { stored = (char*)vmAlloc(&p->arena, n + 1); memcpy(stored, s, n); stored[n] = 0; key = stored; }
        }
        h = vmHashStr(key);
        for (i = h & (p->internCap - 1); p->internTab[i]; i = (i + 1) & (p->internCap - 1))
            if (strcmp(p->internTab[i], key) == 0) return p->internTab[i];
        if (copy && !stored) { stored = (char*)vmAlloc(&p->arena, n + 1); memcpy(stored, tmp, n + 1); }
        p->internTab[i] = stored ? stored : s;
        p->internCount++;
        return p->internTab[i];
    }
}

/* An immortal string of the program (a constant): the runtime never counts it (IMMORTAL), the program frees it when it is unloaded. */
static Value vmConstStr(VmProgram* p, const fire_char16* d, uint32_t n) {
    Str* s = (Str*)malloc(sizeof(Str) + (n + 1) * sizeof(fire_char16));
    fire_char16* chars;
    if (!s) { fputs("fire vm: out of memory\n", stderr); exit(3); }
    s->h.rc = IMMORTAL;
    s->h.type = R_Str;
    s->length = n;
    chars = (fire_char16*)(s + 1);
    if (n) memcpy(chars, d, n * sizeof(fire_char16));
    chars[n] = 0;
    s->data = chars;
    if (p->nstrs == p->capStrs) {
        p->capStrs = p->capStrs ? p->capStrs * 2 : 256;
        p->strs = (Str**)realloc(p->strs, p->capStrs * sizeof(Str*));
        if (!p->strs) { fputs("fire vm: out of memory\n", stderr); exit(3); }
    }
    p->strs[p->nstrs++] = s;
    return StrV(s);
}

static Value rdConstant(VmReader* r, const char** name) {
    uint32_t tag = rdU8(r);
    *name = NULL;
    switch (tag) {
        case 0: rdUnitRef(r); return Undef();
        case 1: return Bool(rdU8(r) != 0);
        case 2: {
            int64_t i = rdInt(r);
            uint32_t width = rdU8(r), wide = rdU8(r), e;
            (void)width; (void)wide;
            rdUnitRef(r);   /* (units: not in the spike - the value is unitless) */
            e = rdCount(r);
            return e ? EnumV(i, (uint16_t)e) : Int(i);
        }
        case 3: { double f = rdF64(r); rdU8(r); rdUnitRef(r); return Float((Real)f); }
        case 4: return Char(rdCount(r));
        case 5: {
            uint32_t n = rdCount(r), i;
            const uint8_t* b;
            fire_char16 small[256];
            fire_char16* d = small;
            char utf8[1024];
            Value v;
            if (n > 0x3FFFFFFF) rdFail(r, "a string is too long");
            b = rdNeed(r, (size_t)n * 2);
            if (n > 256) d = (fire_char16*)malloc(n * sizeof(fire_char16));
            for (i = 0; i < n; i++) d[i] = (fire_char16)(b[2 * i] | (b[2 * i + 1] << 8));
            v = vmConstStr(r->p, d, n);
            if (d != small) free(d);
            /* the same text as a name (a field, a method, a class): interned */
            if (n < 200) { strToUtf8(v, utf8, sizeof utf8); *name = vmIntern(r->p, utf8, strlen(utf8), true); }
            return v;
        }
        case 6: { const uint8_t* b = rdNeed(r, 8); uint64_t u = 0; int i; for (i = 7; i >= 0; i--) u = (u << 8) | b[i]; return WordV(u); }
        default: rdFail(r, "a constant of an unknown kind");
    }
    return Undef();
}

static VmChunk* rdChunk(VmReader* r);
static VmProto* rdProto(VmReader* r) {
    uint32_t ref = rdCount(r), i, n;
    VmProto* f;
    if (ref == 0) return NULL;
    if (ref >= 2) { if (ref - 2 >= r->nprotos) rdFail(r, "a function out of range"); return r->protos[ref - 2]; }
    f = (VmProto*)vmAlloc(&r->p->arena, sizeof(VmProto));
    if (r->nprotos == r->capProtos) {
        r->capProtos = r->capProtos ? r->capProtos * 2 : 1024;
        r->protos = (VmProto**)realloc(r->protos, r->capProtos * sizeof(VmProto*));
    }
    r->protos[r->nprotos++] = f;   /* (its number is taken where it begins, as the writer counted) */
    f->chunk = rdChunk(r);
    f->nparams = (int32_t)rdInt(r);
    n = rdCount(r);
    f->ndefaults = n;
    f->defaults = n ? (VmProto**)vmAlloc(&r->p->arena, n * sizeof(VmProto*)) : NULL;
    for (i = 0; i < n; i++) f->defaults[i] = rdProto(r);
    f->access = (uint8_t)rdU8(r);
    f->isStatic = (uint8_t)(rdU8(r) & 1);
    rdUInt(r); rdUInt(r); rdUInt(r);            /* refMask, captureCopyMask, captureMovedMask */
    rdOptStrList(r);                            /* selectorPath */
    rdOptStrList(r);                            /* paramTypes */
    rdStr(r);                                   /* fieldGetterOf */
    rdOptStrList(r);                            /* paramNames */
    rdInt(r);                                   /* typeParamCount */
    n = rdCount(r);                             /* specializations */
    if (n) { n--; while (n--) if (!rdProto(r)) rdFail(r, "a specialization is missing"); }
    rdStr(r); rdOptStrList(r); rdInt(r);        /* specOwner, specArgs, specDefinedMask */
    n = rdCount(r);
    if (n) rdNeed(r, n - 1);                    /* specParamKinds */
    return f;
}

static VmChunk* rdChunk(VmReader* r) {
    uint32_t ref = rdCount(r), i, n;
    VmChunk* c;
    VmArena* a = &r->p->arena;
    if (ref == 0) rdFail(r, "a chunk is missing");
    if (ref >= 2) { if (ref - 2 >= r->nchunks) rdFail(r, "a chunk out of range"); return r->chunks[ref - 2]; }
    c = (VmChunk*)vmAlloc(a, sizeof(VmChunk));
    if (r->nchunks == r->capChunks) {
        r->capChunks = r->capChunks ? r->capChunks * 2 : 1024;
        r->chunks = (VmChunk**)realloc(r->chunks, r->capChunks * sizeof(VmChunk*));
    }
    r->chunks[r->nchunks++] = c;
    n = rdCount(r);
    {
        uint8_t* code = (uint8_t*)vmAlloc(a, n + 8);   /* (a Halt behind the end: falling off a chunk stops) */
        memcpy(code, rdNeed(r, n), n);
        code[n] = 90;
        c->code = code;
        c->ncode = n;
    }
    n = rdCount(r);
    c->nk = n;
    c->k = (Value*)vmAlloc(a, (n + 1) * sizeof(Value));
    c->kname = (const char**)vmAlloc(a, (n + 1) * sizeof(char*));
    for (i = 0; i < n; i++) c->k[i] = rdConstant(r, &c->kname[i]);
    n = rdCount(r);
    for (i = 0; i < n; i++) { uint32_t u = rdCount(r); if (u == 0 || u > r->p->nunits) rdFail(r, "a unit is missing"); }
    n = rdCount(r);
    c->nfn = n;
    c->fn = (VmProto**)vmAlloc(a, (n + 1) * sizeof(VmProto*));
    for (i = 0; i < n; i++) if (!(c->fn[i] = rdProto(r))) rdFail(r, "a function is missing");
    n = rdCount(r);                             /* receiverTypeArgs */
    if (n) { n--; while (n--) { rdInt(r); rdName(r); } }
    n = rdCount(r);
    c->nhandlers = n;
    c->handlers = (VmHandlerT*)vmAlloc(a, (n + 1) * sizeof(VmHandlerT));
    for (i = 0; i < n; i++) {
        VmHandlerT* h = &c->handlers[i];
        uint32_t k, m = rdCount(r);
        int64_t f;
        h->ncatch = m;
        h->types = (const char**)vmAlloc(a, (m + 1) * sizeof(char*));
        h->addrs = (uint32_t*)vmAlloc(a, (m + 1) * sizeof(uint32_t));
        h->typeIds = (int32_t*)vmAlloc(a, (m + 1) * sizeof(int32_t));
        for (k = 0; k < m; k++) { h->types[k] = rdStr(r); h->addrs[k] = (uint32_t)rdInt(r); }
        f = rdInt(r);
        h->finallyAddr = f == 0 ? -1 : (int32_t)(f - 1);
    }
    if (r->lines) { n = rdCount(r); while (n--) { rdInt(r); rdInt(r); rdInt(r); } }
    n = rdCount(r);
    while (n--) { rdInt(r); rdInt(r); rdName(r); }
    return c;
}

static void rdType(VmReader* r) {
    uint32_t n;
    if (!rdU8(r)) return;
    rdName(r); rdInt(r); rdInt(r);
    if (rdU8(r)) { rdStr(r); rdStrList(r); rdU8(r); rdName(r); rdU8(r); }
    rdOptStrList(r); rdStr(r); rdInt(r); rdInt(r);
    n = rdCount(r);
    if (n) { n--; while (n--) { if (!rdU8(r)) rdFail(r, "a type argument is missing"); r->at--; rdType(r); } }
    rdU8(r);
}

static VmClass* rdClass(VmReader* r) {
    VmArena* a = &r->p->arena;
    VmClass* c = (VmClass*)vmAlloc(a, sizeof(VmClass));
    uint32_t n, i, k;
    c->name = rdName(r);
    c->baseRef = rdCount(r);
    c->flags = (uint8_t)rdU8(r);
    if (rdU8(r)) {   /* meta (reflection): parsed, not kept by the spike */
        n = rdCount(r);
        while (n--) { rdName(r); rdName(r); rdName(r); rdName(r); rdU8(r); rdName(r); rdStrList(r); rdStrList(r); rdAttributes(r); }
        rdStrList(r); rdAttributes(r); rdStrList(r);
    }
    rdStrList(r); rdOptStrList(r); rdOptStrList(r);   /* interfaces, baseTypeArgs, keptMembers */
    n = rdCount(r);
    c->nfields = n;
    c->fields = (VmField*)vmAlloc(a, (n + 1) * sizeof(VmField));
    for (i = 0; i < n; i++) { c->fields[i].name = rdName(r); if (!(c->fields[i].init = rdProto(r))) rdFail(r, "a field without its initializer"); }
    n = rdCount(r);   /* fieldInfo */
    while (n--) {
        rdName(r); rdStr(r); rdU8(r); rdStr(r); rdUInt(r); rdStr(r); rdStr(r); rdU8(r); rdU8(r); rdName(r);
        if (rdU8(r)) rdAttributes(r);
    }
    {
        uint32_t nv = rdCount(r), nf;
        const char** vn = (const char**)vmAlloc(a, (nv + 1) * sizeof(char*));
        Value* vv = (Value*)vmAlloc(a, (nv + 1) * sizeof(Value));
        const char* ignored;
        for (i = 0; i < nv; i++) { vn[i] = rdName(r); vv[i] = rdConstant(r, &ignored); }
        nf = rdCount(r);
        c->nstatic = nv + nf;
        c->snames = (const char**)vmAlloc(a, (c->nstatic + 1) * sizeof(char*));
        c->svals = (Value*)vmAlloc(a, (c->nstatic + 1) * sizeof(Value));
        c->sinit = (VmProto**)vmAlloc(a, (c->nstatic + 1) * sizeof(VmProto*));
        for (i = 0; i < nv; i++) { c->snames[i] = vn[i]; c->svals[i] = vv[i]; }
        for (i = 0; i < nf; i++) { c->snames[nv + i] = rdName(r); c->svals[nv + i] = Undef(); if (!(c->sinit[nv + i] = rdProto(r))) rdFail(r, "a static field without its initializer"); }
    }
    rdOptStrList(r);   /* readonlyStatics */
    n = rdCount(r);
    c->nmethods = n;
    c->methods = (VmMethod*)vmAlloc(a, (n + 1) * sizeof(VmMethod));
    for (i = 0; i < n; i++) {
        VmMethod* m = &c->methods[i];
        m->name = rdName(r);
        m->nov = rdCount(r);
        m->ov = (VmProto**)vmAlloc(a, (m->nov + 1) * sizeof(VmProto*));
        for (k = 0; k < m->nov; k++) if (!(m->ov[k] = rdProto(r))) rdFail(r, "a method is missing");
    }
    n = rdCount(r);
    c->nctors = n;
    c->ctors = (VmCtor*)vmAlloc(a, (n + 1) * sizeof(VmCtor));
    for (i = 0; i < n; i++) { c->ctors[i].arity = (int32_t)rdInt(r); if (!(c->ctors[i].p = rdProto(r))) rdFail(r, "a constructor is missing"); }
    n = rdCount(r);   /* extraConstructors (overloads by type): not chosen by the spike */
    while (n--) if (!rdProto(r)) rdFail(r, "a constructor is missing");
    c->dtor = rdProto(r);
    n = rdCount(r);   /* thisReads */
    if (n) { n--; while (n--) { rdName(r); rdStrList(r); } }
    /* (the members know their class) */
    for (i = 0; i < c->nmethods; i++) for (k = 0; k < c->methods[i].nov; k++) if (!c->methods[i].ov[k]->owner) c->methods[i].ov[k]->owner = c;
    for (i = 0; i < c->nctors; i++) if (!c->ctors[i].p->owner) c->ctors[i].p->owner = c;
    if (c->dtor && !c->dtor->owner) c->dtor->owner = c;
    return c;
}

/* Reads a program file. NULL and the reason in `error` when it is not one or is damaged. */
static VmProgram* vmLoad(const uint8_t* in, size_t len, char* error, size_t errorCap) {
    VmReader r;
    VmProgram* volatile p = (VmProgram*)calloc(1, sizeof(VmProgram));
    uint32_t n, i;
    memset(&r, 0, sizeof r);
    r.in = in; r.len = len; r.p = p;
    if (setjmp(r.fail)) {
        VmProgram* q = r.p;
        snprintf(error, errorCap, "%s", r.error);
        free(r.protos); free(r.chunks);
        for (i = 0; i < q->nstrs; i++) free(q->strs[i]);
        free(q->strs);
        vmArenaFree(&q->arena);
        free(q);
        return NULL;
    }
    if (len < 6 || in[0] != 'F' || in[1] != 'B' || in[2] != 'C' || in[3] != 0x1A) { snprintf(r.error, sizeof r.error, "Not a fire program file (.fbc)."); longjmp(r.fail, 1); }
    r.at = 4;
    if (rdU8(&r) != 1) { snprintf(r.error, sizeof r.error, "The program file has another version of the format; this VM reads version 1."); longjmp(r.fail, 1); }
    r.lines = (rdU8(&r) & 1) != 0;
    n = rdCount(&r);
    p->nnames = n;
    p->names = (const char**)vmAlloc(&p->arena, (n + 1) * sizeof(char*));
    for (i = 0; i < n; i++) { uint32_t l = rdCount(&r); const char* s = (const char*)rdNeed(&r, l); p->names[i] = vmIntern(p, s, l, true); }
    p->nunits = rdCount(&r);
    for (i = 0; i < p->nunits; i++) rdUnitBody(&r);
    n = rdCount(&r);   /* enums */
    while (n--) { uint32_t m; rdName(&r); rdU8(&r); for (m = rdCount(&r); m > 0; m--) { rdName(&r); rdInt(&r); } }
    p->mode = (uint8_t)rdU8(&r); p->floatWidth = (uint8_t)rdU8(&r); p->intWidth = (uint8_t)rdU8(&r); p->wordWidth = (uint8_t)rdU8(&r);
    rdInt(&r);                               /* firstUserSource */
    rdStrList(&r);                           /* nativeImports */
    rdOptStrList(&r); rdOptStrList(&r); rdOptStrList(&r);
    n = rdCount(&r);                         /* customUnits */
    while (n--) { rdName(&r); rdName(&r); rdU8(&r); rdDims(&r); rdF64(&r); rdF64(&r); rdStr(&r); rdStr(&r); }
    n = rdCount(&r);                         /* enumDecls */
    while (n--) { uint32_t m; rdName(&r); rdU8(&r); for (m = rdCount(&r); m > 0; m--) { rdName(&r); rdInt(&r); } }
    n = rdCount(&r);                         /* resources */
    while (n--) { rdName(&r); rdNeed(&r, rdCount(&r)); }
    n = rdCount(&r);                         /* externs */
    while (n--) {
        uint32_t m;
        rdName(&r); rdStr(&r);
        for (m = rdCount(&r); m > 0; m--) rdType(&r);
        rdType(&r); rdStr(&r); rdOptStrList(&r); rdInt(&r); rdInt(&r); rdStr(&r);
    }
    n = rdCount(&r);
    p->nclasses = n;
    p->classes = (VmClass**)vmAlloc(&p->arena, (n + 1) * sizeof(VmClass*));
    for (i = 0; i < n; i++) p->classes[i] = rdClass(&r);
    for (i = 0; i < n; i++) {
        uint32_t b = p->classes[i]->baseRef;
        if (b > n) rdFail(&r, "a base class out of range");
        if (b) p->classes[i]->base = p->classes[b - 1];
    }
    n = rdCount(&r);
    p->nclassNames = n;
    p->classNames = (const char**)vmAlloc(&p->arena, (n + 1) * sizeof(char*));
    p->classByName = (VmClass**)vmAlloc(&p->arena, (n + 1) * sizeof(VmClass*));
    for (i = 0; i < n; i++) {
        uint32_t k;
        p->classNames[i] = rdName(&r);
        k = rdCount(&r);
        if (k >= p->nclasses) rdFail(&r, "a class out of range");
        p->classByName[i] = p->classes[k];
    }
    p->top = rdChunk(&r);
    if (r.at != r.len) rdFail(&r, "bytes behind its end");
    free(r.protos); free(r.chunks);
    return p;
}

static void vmUnload(VmProgram* p) {
    uint32_t i;
    if (!p) return;
    for (i = 0; i < p->nstrs; i++) free(p->strs[i]);
    free(p->strs);
    vmArenaFree(&p->arena);
    free(p);
}
