/* fire VM in C: the loader of the program file (.fbc, docs/spec/BYTECODE.md section 48) - ProgramFile.Read of src/fire/Bytecode/ProgramFile.cs, in its
 * order. The whole format is read and checked (magic, version, references in range, "bytes behind its end", a file that ends too early); what the
 * interpreter uses is kept in the arena of the program (native/base): the names (interned - a name compares by its pointer), the units (given to the
 * runtime's unit table when the program starts), the enums (the runtime's g_enums), the classes with their fields, field infos, statics, methods,
 * constructors (by arity and the extra ones of overloading by type) and destructor, the chunks (code, constants, units, functions, handlers, line tables)
 * and the functions (parameters, defaults, access, masks, overload descriptors, specializations). Parsed and skipped: custom units (their units are in the
 * unit table), externs, the meta data of the classes (reflection, phase B step 5), receiverTypeArgs. The names of locals and the sources are kept for the debugger (vm_debug.c). */

static void vmOom(void) { fputs("fire vm: out of memory\n", stderr); exit(3); }
#define vmAlloc(a, n) fbAlloc((a), (n))

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
static const char** rdStrList(VmReader* r, uint32_t* count) {
    uint32_t n = rdCount(r), i;
    const char** l = (const char**)vmAlloc(&r->p->arena, (n + 1) * sizeof(char*));
    for (i = 0; i < n; i++) l[i] = rdStr(r);
    if (count) *count = n;
    return l;
}
/* opt list<str>: NULL when there is none */
static const char** rdOptStrList(VmReader* r, uint32_t* count) {
    uint32_t n = rdCount(r), i;
    const char** l;
    if (count) *count = 0;
    if (!n) return NULL;
    n--;
    l = (const char**)vmAlloc(&r->p->arena, (n + 1) * sizeof(char*));
    for (i = 0; i < n; i++) l[i] = rdStr(r);
    if (count) *count = n;
    return l;
}
static VmAttr* rdAttributes(VmReader* r, uint32_t* count) {
    uint32_t n = rdCount(r), i;
    VmAttr* a = (VmAttr*)vmAlloc(&r->p->arena, (n + 1) * sizeof(VmAttr));
    for (i = 0; i < n; i++) { a[i].name = rdName(r); a[i].texts = rdStrList(r, &a[i].ntexts); a[i].json = rdName(r); }
    *count = n;
    return a;
}

/* ---- interning: every name of the program once, so that a name compares by its pointer ---- */
static const char* vmIntern(VmProgram* p, const char* s, size_t n) { return fbIntern(&p->interns, s, n); }
static const char* vmName(VmProgram* p, const char* s) { return fbIntern(&p->interns, s, strlen(s)); }

/* An immortal string of the program (a constant): the runtime never counts it (IMMORTAL), the program frees it when it is unloaded. */
static Value vmConstStr(VmProgram* p, const fire_char16* d, uint32_t n) {
    Str* s = (Str*)malloc(sizeof(Str) + (n + 1) * sizeof(fire_char16));
    fire_char16* chars;
    if (!s) vmOom();
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
        if (!p->strs) vmOom();
    }
    p->strs[p->nstrs++] = s;
    return StrV(s);
}
static Value vmConstStrUtf8(VmProgram* p, const char* s) {
    fire_char16 small[256];
    uint32_t n = (uint32_t)strlen(s);
    fire_char16* d = n + 2 < 256 ? small : (fire_char16*)malloc((n + 2) * sizeof(fire_char16));
    Value v;
    n = utf8ToUtf16(s, d, n + 2);
    v = vmConstStr(p, d, n);
    if (d != small) free(d);
    return v;
}

static uint32_t rdUnitRef(VmReader* r) {
    uint32_t i = rdCount(r);
    if (i > r->p->nunits) rdFail(r, "a unit out of range");
    return i == 0 ? 0 : r->p->unitIds[i - 1];
}

static Value rdConstant(VmReader* r, const char** name) {
    uint32_t tag = rdU8(r);
    *name = NULL;
    switch (tag) {
        case 0: { uint32_t u = rdUnitRef(r); return UndefU(u); }
        case 1: return Bool(rdU8(r) != 0);
        case 2: {
            int64_t i = rdInt(r);
            uint32_t width = rdU8(r), wide = rdU8(r), u, e;
            (void)width;
            u = rdUnitRef(r);
            e = rdCount(r);
            if (e > r->p->nenums) rdFail(r, "an enum out of range");
            if (e) return EnumV(i, (uint16_t)e);
            { Value v = IntW(i, u, (uint8_t)wide); return v; }
        }
        case 3: { double f = rdF64(r); uint32_t u; rdU8(r); u = rdUnitRef(r); return Float((Real)f, u); }
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
            if (n < 300) { strToUtf8(v, utf8, sizeof utf8); *name = vmIntern(r->p, utf8, strlen(utf8)); }
            return v;
        }
        case 6: { const uint8_t* b = rdNeed(r, 8); uint64_t u = 0; int i; for (i = 7; i >= 0; i--) u = (u << 8) | b[i]; return WordV(u); }
        default: rdFail(r, "a constant of an unknown kind");
    }
    return Undef();
}

static VmChunk* rdChunk(VmReader* r);
static VmProto* rdProto(VmReader* r) {
    uint32_t ref = rdCount(r), i, n, flags;
    VmProto* f;
    FbArena* a = &r->p->arena;
    if (ref == 0) return NULL;
    if (ref >= 2) { if (ref - 2 >= r->nprotos) rdFail(r, "a function out of range"); return r->protos[ref - 2]; }
    f = (VmProto*)vmAlloc(a, sizeof(VmProto));
    if (r->nprotos == r->capProtos) {
        r->capProtos = r->capProtos ? r->capProtos * 2 : 1024;
        r->protos = (VmProto**)realloc(r->protos, r->capProtos * sizeof(VmProto*));
        if (!r->protos) vmOom();
    }
    r->protos[r->nprotos++] = f;   /* (its number is taken where it begins, as the writer counted) */
    f->chunk = rdChunk(r);
    f->nparams = (int32_t)rdInt(r);
    n = rdCount(r);
    f->ndefaults = n;
    f->defaults = n ? (VmProto**)vmAlloc(a, n * sizeof(VmProto*)) : NULL;
    for (i = 0; i < n; i++) f->defaults[i] = rdProto(r);
    f->access = (uint8_t)rdU8(r);
    flags = rdU8(r);
    f->isStatic = (uint8_t)(flags & 1);
    f->readsOnly = (uint8_t)((flags >> 1) & 1);
    f->refMask = rdUInt(r); f->captureCopyMask = rdUInt(r); f->captureMovedMask = rdUInt(r);
    f->selectorPath = rdOptStrList(r, &f->nselectorPath);   /* (reflection: a lambda that is a member chain on its parameter) */
    f->paramTypes = rdOptStrList(r, &f->nparamTypes);
    f->fieldGetterOf = rdStr(r);
    f->paramNames = rdOptStrList(r, &f->nparamNames);
    f->typeParamCount = (int32_t)rdInt(r);
    n = rdCount(r);                             /* specializations */
    if (n) {
        n--;
        f->nspecs = n;
        f->specs = (VmProto**)vmAlloc(a, (n + 1) * sizeof(VmProto*));
        for (i = 0; i < n; i++) if (!(f->specs[i] = rdProto(r))) rdFail(r, "a specialization is missing");
    }
    f->specOwner = rdStr(r);
    f->specArgs = rdOptStrList(r, &f->nspecArgs);
    f->specDefinedMask = rdInt(r);
    n = rdCount(r);
    if (n) { f->nspecParamKinds = n - 1; f->specParamKinds = rdNeed(r, n - 1); }   /* specParamKinds */
    return f;
}

static VmChunk* rdChunk(VmReader* r) {
    uint32_t ref = rdCount(r), i, n;
    VmChunk* c;
    FbArena* a = &r->p->arena;
    if (ref == 0) rdFail(r, "a chunk is missing");
    if (ref >= 2) { if (ref - 2 >= r->nchunks) rdFail(r, "a chunk out of range"); return r->chunks[ref - 2]; }
    c = (VmChunk*)vmAlloc(a, sizeof(VmChunk));
    if (r->nchunks == r->capChunks) {
        r->capChunks = r->capChunks ? r->capChunks * 2 : 1024;
        r->chunks = (VmChunk**)realloc(r->chunks, r->capChunks * sizeof(VmChunk*));
        if (!r->chunks) vmOom();
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
    c->nunits = n;
    c->units = (uint32_t*)vmAlloc(a, (n + 1) * sizeof(uint32_t));
    for (i = 0; i < n; i++) { uint32_t u = rdCount(r); if (u == 0 || u > r->p->nunits) rdFail(r, "a unit is missing"); c->units[i] = r->p->unitIds[u - 1]; }
    n = rdCount(r);
    c->nfn = n;
    c->fn = (VmProto**)vmAlloc(a, (n + 1) * sizeof(VmProto*));
    for (i = 0; i < n; i++) if (!(c->fn[i] = rdProto(r))) rdFail(r, "a function is missing");
    n = rdCount(r);                             /* receiverTypeArgs (the native backend's typing) */
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
    if (r->lines) {
        n = rdCount(r);
        c->nlines = n;
        c->lines = (int32_t*)vmAlloc(a, (3 * n + 1) * sizeof(int32_t));
        for (i = 0; i < n; i++) { c->lines[3 * i] = (int32_t)rdInt(r); c->lines[3 * i + 1] = (int32_t)rdInt(r); c->lines[3 * i + 2] = (int32_t)rdInt(r); }
    }
    n = rdCount(r);                             /* DebugLocalNames (the debugger, vm_debug.c): depth -1 = the DeclareLocal at the offset, depth 0 = a parameter */
    if (n) {
        int64_t* e = (int64_t*)malloc(2 * (size_t)n * sizeof(int64_t));
        const char** names = (const char**)malloc((size_t)n * sizeof(char*));
        uint32_t k, maxDecl = 0, maxLocal = 0;
        if (!e || !names) vmOom();
        for (k = 0; k < n; k++) {
            e[2 * k] = rdInt(r); e[2 * k + 1] = rdInt(r); names[k] = rdName(r);
            if (e[2 * k + 1] < 0 || e[2 * k + 1] > 0x7FFFFFF) continue;
            if (e[2 * k] == -1 && (uint32_t)e[2 * k + 1] >= maxDecl) maxDecl = (uint32_t)e[2 * k + 1] + 1;
            if (e[2 * k] == 0 && (uint32_t)e[2 * k + 1] >= maxLocal) maxLocal = (uint32_t)e[2 * k + 1] + 1;
        }
        if (maxDecl) { c->declNames = (const char**)vmAlloc(a, maxDecl * sizeof(char*)); c->ndeclNames = maxDecl; }
        if (maxLocal) { c->localNames = (const char**)vmAlloc(a, maxLocal * sizeof(char*)); c->nlocalNames = maxLocal; }
        for (k = 0; k < n; k++) {
            if (e[2 * k + 1] < 0 || e[2 * k + 1] > 0x7FFFFFF) continue;
            if (e[2 * k] == -1) c->declNames[e[2 * k + 1]] = names[k];
            else if (e[2 * k] == 0) c->localNames[e[2 * k + 1]] = names[k];
        }
        free(e); free(names);
    }
    return c;
}

/* A TypeRef; `out` (may be NULL) gets its C text for a signature of `Library`: `int[32]`, `float`, `char**` ("void" for none). */
static void rdTypeText(VmReader* r, char* out, size_t cap) {
    uint32_t n;
    const char* base;
    int64_t bits, depth;
    if (!rdU8(r)) { if (out) snprintf(out, cap, "void"); return; }
    base = rdName(r); bits = rdInt(r); depth = rdInt(r);
    if (out) {
        size_t at = (size_t)snprintf(out, cap, bits > 0 ? "%s[%d]" : "%s", base, (int)(bits - 1));
        while (depth-- > 0 && at + 1 < cap) out[at++] = '*';
        if (at < cap) out[at] = 0;
    }
    if (rdU8(r)) { rdStr(r); rdStrList(r, NULL); rdU8(r); rdName(r); rdU8(r); }
    rdOptStrList(r, NULL); rdStr(r); rdInt(r); rdInt(r);
    n = rdCount(r);
    if (n) { n--; while (n--) { if (!rdU8(r)) rdFail(r, "a type argument is missing"); r->at--; rdTypeText(r, NULL, 0); } }
    rdU8(r);
}

static void vmRelinkOwner(VmProto* p, VmClass* c, int depth) {
    uint32_t i;
    if (!p || depth > 64) return;
    if (!p->chunk->owner) p->chunk->owner = c;
    if (!p->owner) p->owner = c;
    for (i = 0; i < p->chunk->nfn; i++) vmRelinkOwner(p->chunk->fn[i], c, depth + 1);
    for (i = 0; i < p->ndefaults; i++) vmRelinkOwner(p->defaults[i], c, depth + 1);
    for (i = 0; i < p->nspecs; i++) vmRelinkOwner(p->specs[i], c, depth + 1);
}

static VmClass* rdClass(VmReader* r) {
    FbArena* a = &r->p->arena;
    VmClass* c = (VmClass*)vmAlloc(a, sizeof(VmClass));
    uint32_t n, i, k;
    c->name = rdName(r);
    c->baseRef = rdCount(r);
    c->flags = (uint8_t)rdU8(r);
    if (rdU8(r)) {   /* meta (ClassMeta: what reflection describes) */
        VmMeta* m = (VmMeta*)vmAlloc(a, sizeof(VmMeta));
        n = rdCount(r);
        m->nmembers = n;
        m->members = (VmMember*)vmAlloc(a, (n + 1) * sizeof(VmMember));
        for (i = 0; i < n; i++) {
            VmMember* e = &m->members[i];
            e->name = rdName(r); e->kind = rdName(r); e->type = rdName(r); e->access = rdName(r); e->flags = (uint8_t)rdU8(r); e->unit = rdName(r);
            e->pnames = rdStrList(r, &e->npnames); e->ptypes = rdStrList(r, &e->nptypes);
            e->attrs = rdAttributes(r, &e->nattrs);
        }
        m->baseNames = rdStrList(r, &m->nbaseNames);
        m->attrs = rdAttributes(r, &m->nattrs);
        m->declIfaces = rdStrList(r, &m->ndeclIfaces);
        c->meta = m;
    }
    c->interfaces = rdStrList(r, &c->ninterfaces);
    c->baseTypeArgs = rdOptStrList(r, &c->nbaseTypeArgs);
    rdOptStrList(r, NULL);   /* keptMembers (stripping of the native backend) */
    n = rdCount(r);
    c->nfields = n;
    c->fields = (VmField*)vmAlloc(a, (n + 1) * sizeof(VmField));
    for (i = 0; i < n; i++) { c->fields[i].name = rdName(r); if (!(c->fields[i].init = rdProto(r))) rdFail(r, "a field without its initializer"); }
    n = rdCount(r);   /* fieldInfo */
    c->nfinfo = n;
    c->finfo = (VmFieldInfo*)vmAlloc(a, (n + 1) * sizeof(VmFieldInfo));
    for (i = 0; i < n; i++) {
        VmFieldInfo* fi = &c->finfo[i];
        fi->name = rdName(r); fi->requiredUnit = rdStr(r); fi->unitId = 0xFFFFFFFFu; fi->requiredKind = (uint8_t)rdU8(r); fi->requiredType = rdStr(r);
        fi->requiredArray = (uint32_t)rdUInt(r); fi->requiredArrayType = rdStr(r); fi->arrayOfParam = rdStr(r);
        fi->flags = (uint8_t)rdU8(r); fi->access = (uint8_t)rdU8(r); fi->typeText = rdName(r);
        if (rdU8(r)) fi->attrs = rdAttributes(r, &fi->nattrs);
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
    c->readonlyStatics = rdOptStrList(r, &c->nreadonly);
    n = rdCount(r);
    c->nmethods = n;
    c->methods = (VmMethod*)vmAlloc(a, (n + 1) * sizeof(VmMethod));
    for (i = 0; i < n; i++) {
        VmMethod* m = &c->methods[i];
        m->name = rdName(r);
        m->nov = rdCount(r);
        m->ov = (VmProto**)vmAlloc(a, (m->nov + 1) * sizeof(VmProto*));
        for (k = 0; k < m->nov; k++) {
            if (!(m->ov[k] = rdProto(r))) rdFail(r, "a method is missing");
            if (!m->ov[k]->name) m->ov[k]->name = m->name;
            if (!m->ov[k]->declaring) m->ov[k]->declaring = c;
        }
    }
    n = rdCount(r);
    c->nctors = n;
    c->ctors = (VmCtor*)vmAlloc(a, (n + 1) * sizeof(VmCtor));
    for (i = 0; i < n; i++) { c->ctors[i].arity = (int32_t)rdInt(r); if (!(c->ctors[i].p = rdProto(r))) rdFail(r, "a constructor is missing"); c->ctors[i].p->declaring = c; }
    n = rdCount(r);
    c->nxctors = n;
    c->xctors = (VmProto**)vmAlloc(a, (n + 1) * sizeof(VmProto*));
    for (i = 0; i < n; i++) { if (!(c->xctors[i] = rdProto(r))) rdFail(r, "a constructor is missing"); c->xctors[i]->declaring = c; }
    c->dtor = rdProto(r);
    n = rdCount(r);   /* thisReads: per getter and method the members of `this` it reads (a probe on a computed property, RuntimeClass.ComputedSources) */
    if (n) {
        n--;
        c->nthisReads = n;
        c->thisReads = (VmThisReads*)vmAlloc(a, (n + 1) * sizeof(VmThisReads));
        for (i = 0; i < n; i++) { c->thisReads[i].key = rdName(r); c->thisReads[i].reads = rdStrList(r, &c->thisReads[i].n); }
    }
    /* (Chunk.OwnerClass: the code of the class knows it - CompiledProgram.RelinkProtoOwner) */
    for (i = 0; i < c->nfields; i++) vmRelinkOwner(c->fields[i].init, c, 0);
    for (i = 0; i < c->nstatic; i++) vmRelinkOwner(c->sinit[i], c, 0);
    for (i = 0; i < c->nmethods; i++) for (k = 0; k < c->methods[i].nov; k++) vmRelinkOwner(c->methods[i].ov[k], c, 0);
    for (i = 0; i < c->nctors; i++) vmRelinkOwner(c->ctors[i].p, c, 0);
    for (i = 0; i < c->nxctors; i++) vmRelinkOwner(c->xctors[i], c, 0);
    vmRelinkOwner(c->dtor, c, 0);
    return c;
}

/* The units of the file, as rows of the runtime's table: unit 0 is "no unit" (the runtime's convention), every other unit gets the next row. */
static void rdUnits(VmReader* r) {
    VmProgram* p = r->p;
    uint32_t n = rdCount(r), i, k;
    p->nunits = n;
    p->unitIds = (uint32_t*)vmAlloc(&p->arena, (n + 1) * sizeof(uint32_t));
    p->unitInit = (UnitInit*)vmAlloc(&p->arena, (n + 2) * sizeof(UnitInit));
    p->unitInit[0].scale = 1.0; p->unitInit[0].conv = -1; p->unitInit[0].ulconv = -1;
    {
        uint32_t rows = 1;
        for (i = 0; i < n; i++) {
            UnitInit* u = &p->unitInit[rows];
            uint32_t nd = rdCount(r);
            bool plain;
            for (k = 0; k < nd; k++) {
                const char* sym = rdName(r);
                int64_t e = rdInt(r);
                uint32_t d;
                for (d = 0; d < p->ndims && p->dimNames[d] != sym; d++) { }
                if (d == p->ndims) {
                    if (p->ndims >= FIRE_NDIMS) rdFail(r, "too many base units for this VM");
                    p->dimNames[p->ndims++] = sym;
                }
                u->dims[d] = (int32_t)e;
            }
            u->scale = rdF64(r);
            u->display = rdStr(r);
            u->offset = rdF64(r);
            {   /* the conversion functions (the static To/From of a hidden class): their index in the VM's trampolines (vm_core.c, VM_SPIKE point 1) */
                const char* cv = rdStr(r);
                const char* ucv = rdStr(r);
                const char* names[2];
                int32_t* outs[2];
                int q;
                names[0] = cv; names[1] = ucv; outs[0] = &u->conv; outs[1] = &u->ulconv;
                for (q = 0; q < 2; q++) {
                    uint32_t c;
                    *outs[q] = -1;
                    if (!names[q]) continue;
                    for (c = 0; c < p->nconv && p->convClasses[c] != names[q]; c++) { }
                    if (c == p->nconv) { if (p->nconv >= 16) rdFail(r, "too many units with conversion functions for this VM"); p->convClasses[p->nconv++] = names[q]; }
                    *outs[q] = (int32_t)c;
                }
            }
            plain = u->scale == 1.0 && !u->display && u->offset == 0.0 && u->conv < 0 && u->ulconv < 0;
            for (k = 0; plain && k < FIRE_NDIMS; k++) if (u->dims[k]) plain = false;
            if (plain) { p->unitIds[i] = 0; memset(u, 0, sizeof *u); continue; }
            p->unitIds[i] = rows++;
        }
        p->unitRows = rows;
    }
}

static void rdEnums(VmReader* r, FireEnum** out, uint32_t* count) {
    uint32_t n = rdCount(r), i, m;
    FireEnum* e = (FireEnum*)vmAlloc(&r->p->arena, (n + 1) * sizeof(FireEnum));
    for (i = 0; i < n; i++) {
        FireEnumMember* ms;
        e[i].name = rdName(r);
        e[i].flags = (uint8_t)rdU8(r);
        m = rdCount(r);
        ms = (FireEnumMember*)vmAlloc(&r->p->arena, (m + 1) * sizeof(FireEnumMember));
        for (e[i].count = 0; e[i].count < m; e[i].count++) { ms[e[i].count].name = rdName(r); ms[e[i].count].value = rdInt(r); }
        e[i].members = ms;
    }
    *out = e;
    *count = n;
}

/* Reads a program file. NULL and the reason in `error` when it is not one or is damaged. */
static VmProgram* vmLoad(const uint8_t* in, size_t len, char* error, size_t errorCap) {
    VmReader r;
    VmProgram* volatile p = (VmProgram*)calloc(1, sizeof(VmProgram));
    uint32_t n, i, flags;
    if (!p) vmOom();
    memset(&r, 0, sizeof r);
    p->interns.arena = &p->arena;
    r.in = in; r.len = len; r.p = p;
    if (setjmp(r.fail)) {
        VmProgram* q = r.p;
        snprintf(error, errorCap, "%s", r.error);
        free(r.protos); free(r.chunks);
        for (i = 0; i < q->nstrs; i++) free(q->strs[i]);
        free(q->strs);
        fbMapFree(&q->classByName);
        fbArenaFree(&q->arena);
        free(q);
        return NULL;
    }
    if (len < 6 || in[0] != 'F' || in[1] != 'B' || in[2] != 'C' || in[3] != 0x1A) { snprintf(r.error, sizeof r.error, "Not a fire program file (.fbc)."); longjmp(r.fail, 1); }
    r.at = 4;
    if (rdU8(&r) != 1) { snprintf(r.error, sizeof r.error, "The program file has another version of the format; this VM reads version 1."); longjmp(r.fail, 1); }
    flags = rdU8(&r);
    r.lines = (flags & 1) != 0;
    n = rdCount(&r);
    p->nnames = n;
    p->names = (const char**)vmAlloc(&p->arena, (n + 1) * sizeof(char*));
    for (i = 0; i < n; i++) { uint32_t l = rdCount(&r); const char* s = (const char*)rdNeed(&r, l); p->names[i] = vmIntern(p, s, l); }
    rdUnits(&r);
    rdEnums(&r, &p->enums, &p->nenums);
    p->mode = (uint8_t)rdU8(&r); p->floatWidth = (uint8_t)rdU8(&r); p->intWidth = (uint8_t)rdU8(&r); p->wordWidth = (uint8_t)rdU8(&r);
    p->firstUserSource = (int32_t)rdInt(&r);
    p->nativeImports = rdStrList(&r, &p->nnativeImports);
    p->packageNatives = rdOptStrList(&r, &p->npackageNatives);
    p->packageLibraries = rdOptStrList(&r, &p->npackageLibraries);
    rdOptStrList(&r, NULL);                  /* graphicsNatives (a packed program: the VM has their order, native/tables/bridges.def) */
    n = rdCount(&r);                         /* customUnits: the units a text may name besides those of fire */
    p->ncustomUnits = n;
    p->customUnits = (struct VmCustomUnit*)vmAlloc(&p->arena, (n + 1) * sizeof(struct VmCustomUnit));
    for (i = 0; i < n; i++) {
        struct VmCustomUnit* cu = &p->customUnits[i];
        uint32_t d, k;
        cu->name = rdName(&r); cu->symbol = rdName(&r); cu->prefixed = rdU8(&r) != 0;
        for (d = rdCount(&r); d > 0; d--) {
            const char* sym = rdName(&r);
            int64_t e = rdInt(&r);
            for (k = 0; k < p->ndims && p->dimNames[k] != sym; k++) { }
            if (k == p->ndims) { if (p->ndims >= FIRE_NDIMS) rdFail(&r, "too many base units for this VM"); p->dimNames[p->ndims++] = sym; }
            cu->dims[k] = (int32_t)e;
        }
        cu->scale = rdF64(&r); cu->offset = rdF64(&r); rdStr(&r); rdStr(&r);
    }
    { FireEnum* decls; uint32_t nd; rdEnums(&r, &decls, &nd); (void)decls; }   /* enumDecls (the same enums by declaration) */
    n = rdCount(&r);                         /* resources */
    p->nresources = n;
    p->resources = vmAlloc(&p->arena, (n + 1) * sizeof(*p->resources));
    for (i = 0; i < n; i++) { p->resources[i].name = rdName(&r); p->resources[i].len = rdCount(&r); p->resources[i].data = rdNeed(&r, p->resources[i].len); }
    n = rdCount(&r);                         /* externs: a function of a C library as a signature of `Library` (vm_lib.c) */
    p->nexterns = n;
    p->externs = (VmExtern*)vmAlloc(&p->arena, (n + 1) * sizeof(VmExtern));
    for (i = 0; i < n; i++) {
        uint32_t m, k;
        char sig[400], t[64];
        size_t at;
        VmExtern* e = &p->externs[i];
        e->name = rdName(&r); e->lib = rdStr(&r);
        m = rdCount(&r);
        at = 0; sig[0] = 0;
        for (k = 0; k < m; k++) { rdTypeText(&r, t, sizeof t); at += (size_t)snprintf(sig + at, sizeof sig > at ? sizeof sig - at : 0, "%s%s", k ? "," : "", t); }
        rdTypeText(&r, t, sizeof t);
        {
            char full[480];
            snprintf(full, sizeof full, "%s(%s)", t, sig);
            e->signature = vmName(p, full);
        }
        e->inlineC = rdStr(&r) != NULL;
        rdOptStrList(&r, NULL); rdInt(&r); rdInt(&r); rdStr(&r);
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
    for (i = 0; i < n; i++) {
        const char* name = rdName(&r);
        uint32_t k = rdCount(&r);
        if (k >= p->nclasses) rdFail(&r, "a class out of range");
        fbMapPut(&p->classByName, name, p->classes[k]);
    }
    p->top = rdChunk(&r);
    if (flags & 2) {                         /* the sources (a debug build): file and SHA-256 by source index, for the debugger */
        n = rdCount(&r);
        p->nsources = n;
        p->sourceFiles = (const char**)vmAlloc(&p->arena, (n + 1) * sizeof(char*));
        p->sourceHashes = (const char**)vmAlloc(&p->arena, (n + 1) * sizeof(char*));
        for (i = 0; i < n; i++) { p->sourceFiles[i] = rdStr(&r); p->sourceHashes[i] = rdStr(&r); }
    }
    if (r.at != r.len) rdFail(&r, "bytes behind its end");
    free(r.protos);
    p->chunks = r.chunks; p->nchunks = r.nchunks;
    /* the base-type extensions (`class extends string`, BaseTypeExtensions.ClassNameFor) */
    p->extClass[K_String] = (VmClass*)fbMapGet(&p->classByName, "$string");
    p->extClass[K_Char] = (VmClass*)fbMapGet(&p->classByName, "$char");
    p->extClass[K_Int] = (VmClass*)fbMapGet(&p->classByName, "$int");
    p->extClass[K_Float] = (VmClass*)fbMapGet(&p->classByName, "$float");
    p->extClass[K_Bool] = (VmClass*)fbMapGet(&p->classByName, "$bool");
    p->extClass[K_Array] = (VmClass*)fbMapGet(&p->classByName, "$array");
    return p;
}

static void vmUnload(VmProgram* p) {
    uint32_t i;
    if (!p) return;
    free(p->chunks);
    for (i = 0; i < p->nstrs; i++) free(p->strs[i]);
    free(p->strs);
    fbMapFree(&p->classByName);
    fbArenaFree(&p->arena);
    free(p);
}
