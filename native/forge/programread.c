/* native/forge/programread.c - port of src/fire/Bytecode/ProgramFile.cs (commit a3e43117), the reader (ProgramFile.Read, for `forge-c dump <program.fbc>`,
 * Phase D 4): the bytes of the format `.fbc` (docs/spec/BYTECODE.md, section 48) as the linked program the C# reader makes of them - the tables, then the program;
 * a unit is the one that was written (Unit.Restore), an int of an enum is MakeEnum (64 bits, not wide, unitless), a missing unit of a constant is Unit.Unitless,
 * a key that comes twice in a dictionary keeps its place with the last value, the classes are linked to their bases (RelinkAfterDeserialize). Every error is a
 * ProgramFileException: fcThrow with "pf: <message>" (the exceptions of .NET the C# reader turns into "damaged (...)" carry the messages of .NET). */

typedef struct FcPfReader {
    FcCtx* c;
    const uint8_t* in; size_t n, at;
    bool lines;
    const char** strings; int stringCount;
    const FcU** units; int unitCount;
    FcEnumType** enums; int enumCount;
    FcList* protos; FcList* chunks;
    FcDict* interned;
} FcPfReader;
#define PR_FAIL(r, m) fcThrow((r)->c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt((r)->c, "pf: %s", (m)))
#define PR_INDEX "The program file is damaged (Index was outside the bounds of the array.)."
#define PR_RANGE "The program file is damaged (Index was out of range. Must be non-negative and less than the size of the collection. (Parameter 'index'))."
#define PR_OVERFLOW "The program file is damaged (Arithmetic operation resulted in an overflow.)."
static size_t prNeed(FcPfReader* r, int64_t n) {
    size_t at = r->at;
    if (n < 0 || (uint64_t)n > r->n - r->at) PR_FAIL(r, "The program file is damaged (it ends too early).");
    r->at += (size_t)n;
    return at;
}
static int prU8(FcPfReader* r) { return r->in[prNeed(r, 1)]; }
static bool prBool(FcPfReader* r) { return prU8(r) != 0; }
static uint64_t prUInt(FcPfReader* r) {
    uint64_t v = 0;
    int shift;
    for (shift = 0; shift < 64; shift += 7) {
        int b = prU8(r);
        v |= (uint64_t)(b & 0x7F) << shift;
        if (b < 0x80) return v;
    }
    PR_FAIL(r, "The program file is damaged (a number is too long).");
    return 0;
}
static int prCount(FcPfReader* r) { uint64_t v = prUInt(r); if (v > 0x7FFFFFFF) PR_FAIL(r, "The program file is damaged (a count is too big)."); return (int)v; }
static int64_t prInt(FcPfReader* r) { uint64_t v = prUInt(r); return (int64_t)(v >> 1) ^ -(int64_t)(v & 1); }
static uint64_t prU64(FcPfReader* r) { uint64_t bits = 0; size_t at = prNeed(r, 8); int i; for (i = 0; i < 8; i++) bits |= (uint64_t)r->in[at + i] << (8 * i); return bits; }
static double prF64(FcPfReader* r) { uint64_t bits = prU64(r); double d; memcpy(&d, &bits, 8); return d; }
static const char* prStr(FcPfReader* r) { int i = prCount(r); if (i == 0) return NULL; if (i - 1 >= r->stringCount) PR_FAIL(r, PR_INDEX); return r->strings[i - 1]; }
static const char* prName(FcPfReader* r) { const char* s = prStr(r); if (!s) PR_FAIL(r, "The program file is damaged (a name is missing)."); return s; }
static FcList* prStrList(FcPfReader* r) { int n = prCount(r), i; FcList* l = fcListNew(r->c); for (i = 0; i < n; i++) fcListAdd(r->c, l, (void*)prStr(r)); return l; }
static FcList* prOptStrList(FcPfReader* r) { int n = prCount(r), i; FcList* l; if (n == 0) return NULL; l = fcListNew(r->c); for (i = 0; i < n - 1; i++) fcListAdd(r->c, l, (void*)prStr(r)); return l; }
/* Dimensions: a symbol that comes twice keeps its place with the last exponent */
static void prDims(FcPfReader* r, FcUnit* u) {
    int n = prCount(r), k;
    for (; n > 0; n--) {
        const char* sym = prName(r);
        int e = (int)prInt(r);
        for (k = 0; k < u->dimCount && strcmp(u->dimSym[k], sym) != 0; k++) {}
        if (k == u->dimCount) {
            if (u->dimCount == FC_MAX_DIMS) fcThrow(r->c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: a unit with more than 16 dimensions");
            u->dimSym[u->dimCount++] = sym;
        }
        u->dimExp[k] = e;
    }
}
static const FcU* prUnitBody(FcPfReader* r) {
    FcUnit u;
    const char* display;
    memset(&u, 0, sizeof u);
    prDims(r, &u);
    u.scale = prF64(r); display = prStr(r); u.offset = prF64(r); u.converter = prStr(r); u.unitlessConverter = prStr(r);
    return fcUNew(r->c, &u, display);
}
static const FcU* prUnitRef(FcPfReader* r) { int i = prCount(r); if (i == 0) return NULL; if (i - 1 >= r->unitCount) PR_FAIL(r, PR_INDEX); return r->units[i - 1]; }
/* EnumType.Intern */
static FcEnumType* prIntern(FcPfReader* r, const char* name, bool isFlags, FcList* names, FcList* values) {
    const char* key = fcEnumKeyOf(r->c, name, isFlags, names, values);
    FcEnumType* t = (FcEnumType*)fcDictGet(r->interned, key);
    if (t) return t;
    t = (FcEnumType*)fcAlloc(r->c, sizeof *t);
    t->name = name; t->isFlags = isFlags; t->names = names; t->values = values; t->key = key;
    fcDictPut(r->interned, key, t);
    return t;
}
static void prEnumMembers(FcPfReader* r, FcList* names, FcList* values) {
    int n;
    for (n = prCount(r); n > 0; n--) {
        int64_t* boxed = (int64_t*)fcAlloc(r->c, sizeof *boxed);
        fcListAdd(r->c, names, (void*)prName(r));
        *boxed = prInt(r);
        fcListAdd(r->c, values, boxed);
    }
}
static FcConst* prConstant(FcPfReader* r) {
    int tag = prU8(r);
    FcConst* v;
    switch (tag) {
        case CK_Undefined: { const FcU* u = prUnitRef(r); v = fcMakeUndefined(r->c); if (u) v->unit = u; return v; }
        case CK_Bool: return fcMakeBool(r->c, prBool(r));
        case CK_Int: {
            int64_t i = prInt(r);
            int width = prU8(r), wide = prU8(r), e;
            const FcU* unit = prUnitRef(r);
            e = prCount(r);
            if (e != 0) { if (e - 1 >= r->enumCount) PR_FAIL(r, PR_INDEX); return fcMakeEnum(r->c, i, r->enums[e - 1]); }
            v = fcMakeIntU(r->c, i, unit);
            v->width = width; v->wide = wide;
            return v;
        }
        case CK_Float: { double f = prF64(r); int width = prU8(r); v = fcMakeFloatU(r->c, f, prUnitRef(r)); v->width = width; return v; }
        case CK_Char: return fcMakeChar(r->c, (uint16_t)prCount(r));
        case CK_String: {
            int n = prCount(r), i;
            size_t at;
            uint16_t* units;
            if ((int64_t)n * 2 > 0x7FFFFFFF) PR_FAIL(r, PR_OVERFLOW);
            at = prNeed(r, (int64_t)n * 2);
            units = (uint16_t*)fcAlloc(r->c, sizeof(uint16_t) * (size_t)(n + 1));
            for (i = 0; i < n; i++) units[i] = (uint16_t)(r->in[at + 2 * i] | (r->in[at + 2 * i + 1] << 8));
            return fcMakeString(r->c, fc8FromU16(r->c, units, n));
        }
        case CK_Word: return fcMakeWord(r->c, prU64(r));
        default: PR_FAIL(r, fcFmt(r->c, "The program file is damaged (a constant of an unknown kind %d).", tag)); return NULL;
    }
}
static FcChunk* prChunkRef(FcPfReader* r);
static FcProto* prProtoRef(FcPfReader* r) {
    int ref = prCount(r), id, i, n, access, flags, paramCount;
    FcChunk* chunk;
    FcList* defaults;
    FcProto* proto;
    uint64_t mask;
    if (ref == 0) return NULL;
    if (ref >= 2) { if (ref - 2 >= r->protos->count) PR_FAIL(r, PR_RANGE); return (FcProto*)r->protos->items[ref - 2]; }
    id = r->protos->count;
    fcListAdd(r->c, r->protos, NULL);   /* (its number is taken where it begins, as the writer counted) */
    chunk = prChunkRef(r);
    paramCount = (int)prInt(r);
    defaults = fcListNew(r->c);
    n = prCount(r);
    for (i = 0; i < n; i++) fcListAdd(r->c, defaults, prProtoRef(r));
    access = prU8(r);
    flags = prU8(r);
    proto = fcProtoNew(r->c, chunk, paramCount, access == 0 ? -1 : access - 1, defaults, (flags & 1) != 0);
    proto->readsOnly = (flags & 2) != 0;
    mask = prUInt(r);
    if (mask > 0xFFFFFFFFu) PR_FAIL(r, PR_OVERFLOW);
    proto->refMask = (uint32_t)mask;
    proto->captureCopyMask = prUInt(r);
    proto->captureMovedMask = prUInt(r);
    proto->selectorPath = prOptStrList(r);
    proto->paramTypes = prOptStrList(r);
    proto->fieldGetterOf = prStr(r);
    proto->paramNames = prOptStrList(r);
    proto->typeParamCount = (int)prInt(r);
    n = prCount(r);
    if (n > 0) {
        proto->specializations = fcListNew(r->c);
        for (i = 0; i < n - 1; i++) {
            FcProto* s = prProtoRef(r);
            if (!s) PR_FAIL(r, "The program file is damaged (a specialization is missing).");
            fcListAdd(r->c, proto->specializations, s);
        }
    }
    proto->specOwner = prStr(r);
    proto->specArgs = prOptStrList(r);
    proto->specDefinedMask = prInt(r);
    n = prCount(r);
    if (n > 0) {
        size_t at = prNeed(r, n - 1);
        proto->specParamKindCount = n - 1;
        proto->specParamKinds = (uint8_t*)fcAlloc(r->c, (size_t)n);
        if (n > 1) memcpy(proto->specParamKinds, r->in + at, (size_t)(n - 1));
    }
    r->protos->items[id] = proto;
    return proto;
}
static FcChunk* prChunkRef(FcPfReader* r) {
    int ref = prCount(r), id, n, i;
    FcChunk* k;
    size_t at;
    if (ref == 0) PR_FAIL(r, "The program file is damaged (a chunk is missing).");
    if (ref >= 2) { if (ref - 2 >= r->chunks->count) PR_FAIL(r, PR_RANGE); return (FcChunk*)r->chunks->items[ref - 2]; }
    id = r->chunks->count;
    fcListAdd(r->c, r->chunks, NULL);
    k = fcChunkNew(r->c);
    n = prCount(r);
    at = prNeed(r, n);
    k->code = (uint8_t*)fcAlloc(r->c, (size_t)n + 1);
    if (n) memcpy(k->code, r->in + at, (size_t)n);
    k->len = k->cap = n;
    for (n = prCount(r); n > 0; n--) fcListAdd(r->c, k->constants, prConstant(r));
    for (n = prCount(r); n > 0; n--) { const FcU* u = prUnitRef(r); if (!u) PR_FAIL(r, "The program file is damaged (a unit is missing)."); fcListAdd(r->c, k->units, (void*)u); }
    for (n = prCount(r); n > 0; n--) { FcProto* p = prProtoRef(r); if (!p) PR_FAIL(r, "The program file is damaged (a function is missing)."); fcListAdd(r->c, k->functions, p); }
    n = prCount(r);
    if (n > 0) {
        k->receiverOffsets = fcListNew(r->c); k->receiverTexts = fcListNew(r->c);
        for (n = n - 1; n > 0; n--) {
            int offset = (int)prInt(r);
            const char* text = prName(r);
            for (i = 0; i < k->receiverOffsets->count && FC_VALI(k->receiverOffsets->items[i]) != offset; i++) {}
            if (i == k->receiverOffsets->count) { fcListAdd(r->c, k->receiverOffsets, FC_IVAL(offset)); fcListAdd(r->c, k->receiverTexts, (void*)text); }
            else k->receiverTexts->items[i] = (void*)text;
        }
    }
    for (n = prCount(r); n > 0; n--) {
        FcHandler* h = (FcHandler*)fcAlloc(r->c, sizeof *h);
        int64_t f;
        h->catchTypes = fcListNew(r->c); h->catchAddrs = fcListNew(r->c); h->finallyAddr = -1;
        for (i = prCount(r); i > 0; i--) { const char* type = prStr(r); fcListAdd(r->c, h->catchTypes, (void*)type); fcListAdd(r->c, h->catchAddrs, FC_IVAL((int)prInt(r))); }
        f = prInt(r);
        if (f != 0) h->finallyAddr = (int)(f - 1);
        fcListAdd(r->c, k->handlers, h);
    }
    if (r->lines)
        for (n = prCount(r); n > 0; n--) {
            FcLineEntry e;
            e.offset = (int)prInt(r); e.source = (int)prInt(r); e.line = (int)prInt(r);
            if (k->lineCount == k->lineCap) {
                FcLineEntry* grown;
                k->lineCap = k->lineCap ? k->lineCap * 2 : 8;
                grown = (FcLineEntry*)fcAlloc(r->c, sizeof *grown * (size_t)k->lineCap);
                if (k->lineCount) memcpy(grown, k->lines, sizeof *grown * (size_t)k->lineCount);
                k->lines = grown;
            }
            k->lines[k->lineCount++] = e;
        }
    for (n = prCount(r); n > 0; n--) {
        int depth = (int)prInt(r), slot = (int)prInt(r);
        const char* name = prName(r);
        for (i = 0; i < k->nameCount && !(k->names[i].depth == depth && k->names[i].slot == slot); i++) {}
        if (i == k->nameCount) {
            if (k->nameCount == k->nameCap) {
                FcLocalName* grown;
                k->nameCap = k->nameCap ? k->nameCap * 2 : 8;
                grown = (FcLocalName*)fcAlloc(r->c, sizeof *grown * (size_t)k->nameCap);
                if (k->nameCount) memcpy(grown, k->names, sizeof *grown * (size_t)k->nameCount);
                k->names = grown;
            }
            k->names[k->nameCount].depth = depth; k->names[k->nameCount].slot = slot;
            k->nameCount++;
        }
        k->names[i].name = name;
    }
    r->chunks->items[id] = k;
    return k;
}
static FcList* prAttributes(FcPfReader* r) {
    FcList* l = fcListNew(r->c);
    int n;
    for (n = prCount(r); n > 0; n--) {
        FcAttributeMeta* a = (FcAttributeMeta*)fcAlloc(r->c, sizeof *a);
        a->name = prName(r); a->argTexts = prStrList(r); a->argsJson = prName(r);
        fcListAdd(r->c, l, a);
    }
    return l;
}
static FcProto* prProtoNeeded(FcPfReader* r, const char* what) { FcProto* p = prProtoRef(r); if (!p) PR_FAIL(r, fcFmt(r->c, "The program file is damaged (%s).", what)); return p; }
static FcRtClass* prClass(FcPfReader* r, int* baseRef) {
    FcCtx* c = r->c;
    const char* name = prName(r);
    FcRtClass* rc;
    FcList* readonlyStatics;
    int flags, n, m, i;
    *baseRef = prCount(r);
    flags = prU8(r);
    rc = fcRtClassNew(c, name, NULL);
    rc->isActor = (flags & 1) != 0; rc->isRecord = (flags & 2) != 0; rc->isReflectionHelper = (flags & 4) != 0; rc->keep = (flags & 8) != 0;
    rc->notSerializable = (flags & 16) != 0; rc->noCopy = (flags & 32) != 0;
    if (prBool(r)) {
        FcClassMeta* meta = (FcClassMeta*)fcAlloc(c, sizeof *meta);
        meta->members = fcListNew(c);
        for (n = prCount(r); n > 0; n--) {
            FcMemberMeta* mm = fcMemberMetaNew(c);
            int f;
            mm->name = prName(r); mm->kind = prName(r); mm->typeName = prName(r); mm->access = prName(r);
            f = prU8(r);
            mm->isStatic = (f & 1) != 0; mm->isReadonly = (f & 2) != 0; mm->canRead = (f & 4) != 0; mm->canWrite = (f & 8) != 0;
            mm->unit = prName(r); mm->paramNames = prStrList(r); mm->paramTypes = prStrList(r);
            mm->attributes = prAttributes(r);
            fcListAdd(c, meta->members, mm);
        }
        meta->baseNames = prStrList(r);
        meta->attributes = prAttributes(r);
        meta->declaredInterfaces = prStrList(r);
        rc->meta = meta;
    }
    rc->interfaces = prStrList(r);
    rc->baseTypeArgs = prOptStrList(r);
    rc->keptMembers = prOptStrList(r);
    for (n = prCount(r); n > 0; n--) { fcListAdd(c, rc->fieldNames, (void*)prName(r)); fcListAdd(c, rc->fieldInits, prProtoNeeded(r, "a field without its initializer")); }
    for (n = prCount(r); n > 0; n--) {
        const char* field = prName(r);
        FcFieldInfo* f = (FcFieldInfo*)fcAlloc(c, sizeof *f);
        uint64_t array;
        int bits;
        f->requiredUnit = prStr(r); f->requiredKind = prU8(r); f->requiredType = prStr(r);
        array = prUInt(r);
        if (array > 0xFFFF) PR_FAIL(r, PR_OVERFLOW);
        f->requiredArray = (int)array;
        f->requiredArrayType = prStr(r); f->arrayOfParam = prStr(r);
        bits = prU8(r);
        f->derivedDefined = (bits & 1) != 0; f->isStatic = (bits & 2) != 0;
        f->access = prU8(r);
        f->typeText = prName(r);
        if (prBool(r)) f->attributes = prAttributes(r);
        fcDictPut(rc->ownFieldInfo, field, f);
    }
    for (n = prCount(r); n > 0; n--) { prName(r); prConstant(r); }   /* (StaticFieldValues: the compiler fills none, the dump shows none) */
    for (n = prCount(r); n > 0; n--) { fcListAdd(c, rc->staticFieldNames, (void*)prName(r)); fcListAdd(c, rc->staticFieldInits, prProtoNeeded(r, "a static field without its initializer")); }
    readonlyStatics = prOptStrList(r);
    for (n = prCount(r); n > 0; n--) {
        const char* method = prName(r);
        FcList* overloads = fcListNew(c);
        for (m = prCount(r); m > 0; m--) fcListAdd(c, overloads, prProtoNeeded(r, "a method is missing"));
        fcDictPut(rc->methods, method, overloads);
    }
    for (n = prCount(r); n > 0; n--) {
        int arity = (int)prInt(r);
        FcProto* p = prProtoNeeded(r, "a constructor is missing");
        for (i = 0; i < rc->ctorArities->count && FC_VALI(rc->ctorArities->items[i]) != arity; i++) {}
        if (i == rc->ctorArities->count) { fcListAdd(c, rc->ctorArities, FC_IVAL(arity)); fcListAdd(c, rc->ctorProtos, p); }
        else rc->ctorProtos->items[i] = p;
    }
    if (readonlyStatics) {
        rc->readonlyStatics = fcDictNew(c, FC_KEY_STR);
        for (i = 0; i < readonlyStatics->count; i++) if (readonlyStatics->items[i]) fcSetAdd(rc->readonlyStatics, readonlyStatics->items[i]);
    }
    for (n = prCount(r); n > 0; n--) fcListAdd(c, rc->extraConstructors, prProtoNeeded(r, "a constructor is missing"));
    rc->destructor = prProtoRef(r);
    n = prCount(r);
    if (n > 0) {
        rc->thisReads = fcDictNew(c, FC_KEY_STR);
        for (n = n - 1; n > 0; n--) { const char* method = prName(r); fcDictPut(rc->thisReads, method, prStrList(r)); }
    }
    return rc;
}
static FcTypeRef* prType(FcPfReader* r) {
    FcCtx* c = r->c;
    FcTypeRef* t;
    int64_t bits;
    int args, n;
    if (!prBool(r)) return NULL;
    t = (FcTypeRef*)fcAlloc(c, sizeof *t);
    t->baseName = prName(r);
    bits = prInt(r);
    t->bitWidth = bits == 0 ? FC_NULL_INT : (int)(bits - 1);
    t->pointerDepth = (int)prInt(r);
    if (prBool(r)) {
        FcLambdaSignature* sig = (FcLambdaSignature*)fcAlloc(c, sizeof *sig);
        sig->returnTypeName = prStr(r); sig->paramTypeNames = prStrList(r); sig->isSelector = prBool(r); sig->selectorKind = prName(r); sig->isExpression = prBool(r);
        t->lambdaSignature = sig;
    }
    t->namespaces = prOptStrList(r);
    t->unit = prStr(r);
    t->arrayRank = (int)prInt(r);
    t->typeArgCount = (int)prInt(r);
    args = prCount(r);
    if (args > 0) {
        t->typeArgs = fcListNew(c);
        for (n = args - 1; n > 0; n--) { FcTypeRef* a = prType(r); if (!a) PR_FAIL(r, "The program file is damaged (a type argument is missing)."); fcListAdd(c, t->typeArgs, a); }
    }
    n = prU8(r);
    t->isUnsigned = (n & 1) != 0; t->nonNull = (n & 2) != 0;
    return t;
}
static FcExternSig* prExtern(FcPfReader* r) {
    FcExternSig* sig = (FcExternSig*)fcAlloc(r->c, sizeof *sig);
    int n;
    sig->libName = prStr(r);
    sig->paramTypes = fcListNew(r->c);
    for (n = prCount(r); n > 0; n--) fcListAdd(r->c, sig->paramTypes, prType(r));
    sig->returnType = prType(r); sig->inlineC = prStr(r); sig->paramNames = prOptStrList(r);
    sig->inlineSource = (int)prInt(r); sig->inlineLine = (int)prInt(r); sig->displayName = prStr(r);
    return sig;
}
/* UTF8Encoding(false, throwOnInvalidBytes: true).GetString: the text as fc8, or (an invalid sequence) the message of the DecoderFallbackException */
static const char* prUtf8(FcPfReader* r, size_t at, int n) {
    FcU16 units = {0};
    const uint8_t* b = r->in + at;
    int i = 0;
    const char* s;
    fcU16Put(&units, NULL, 0);
    while (i < n) {
        uint8_t ch = b[i];
        uint32_t cp = 0;
        int len = 0, k;
        if (ch < 0x80) { fcU16Unit(&units, ch); i++; continue; }
        if ((ch & 0xE0) == 0xC0 && ch >= 0xC2) { len = 2; cp = ch & 0x1F; }
        else if ((ch & 0xF0) == 0xE0) { len = 3; cp = ch & 0x0F; }
        else if ((ch & 0xF8) == 0xF0 && ch <= 0xF4) { len = 4; cp = ch & 0x07; }
        for (k = 1; len && k < len; k++) { if (i + k >= n || (b[i + k] & 0xC0) != 0x80) break; cp = (cp << 6) | (b[i + k] & 0x3F); }
        if (!len || k < len || (len == 3 && (cp < 0x800 || (cp >= 0xD800 && cp <= 0xDFFF))) || (len == 4 && (cp < 0x10000 || cp > 0x10FFFF))) {
            fcU16Free(&units);
            PR_FAIL(r, fcFmt(r->c, "The program file is damaged (Unable to translate bytes [%02X] at index %d from specified code page to Unicode.).", ch, i));
        }
        if (cp >= 0x10000) { cp -= 0x10000; fcU16Unit(&units, (uint16_t)(0xD800 + (cp >> 10))); fcU16Unit(&units, (uint16_t)(0xDC00 + (cp & 0x3FF))); }
        else fcU16Unit(&units, (uint16_t)cp);
        i += len;
    }
    s = fc8FromU16(r->c, units.p, units.len);
    fcU16Free(&units);
    return s;
}
/* ProgramFile.IsProgram: at least 6 bytes, the first four FBC\x1A */
static bool fcIsProgramFile(const uint8_t* b, size_t n) { return b && n >= 6 && memcmp(b, "FBC\x1A", 4) == 0; }
static FcList* prEnumList(FcPfReader* r, FcList* names, FcList* values) { prEnumMembers(r, names, values); return names; }
static FcLinked* fcProgramFileRead(FcCtx* c, const uint8_t* bytes, size_t length) {
    FcPfReader R;
    FcPfReader* r = &R;
    FcLinked* linked;
    FcCompiled* p;
    int version, headerFlags, i, n, count;
    FcRtClass** classList;
    int* bases;
    FcList* nativeImports;
    memset(r, 0, sizeof R);
    r->c = c; r->in = bytes; r->n = length;
    r->protos = fcListNew(c); r->chunks = fcListNew(c); r->interned = fcDictNew(c, FC_KEY_STR);
    if (!fcIsProgramFile(bytes, length)) PR_FAIL(r, "Not a fire program file (.fbc).");
    r->at = 4;
    version = prU8(r);
    if (version != 1) PR_FAIL(r, fcFmt(c, "The program file has version %d of the format; this program reads version 1.", version));
    headerFlags = prU8(r);
    r->lines = (headerFlags & 1) != 0;
    r->stringCount = prCount(r);
    if ((size_t)r->stringCount > r->n) PR_FAIL(r, "The program file is damaged (it ends too early).");   /* (a string takes a byte at least) */
    r->strings = (const char**)fcAlloc(c, sizeof(char*) * (size_t)(r->stringCount + 1));
    for (i = 0; i < r->stringCount; i++) { int len = prCount(r); size_t at = prNeed(r, len); r->strings[i] = prUtf8(r, at, len); }
    r->unitCount = prCount(r);
    if ((size_t)r->unitCount > r->n) PR_FAIL(r, "The program file is damaged (it ends too early).");
    r->units = (const FcU**)fcAlloc(c, sizeof(FcU*) * (size_t)(r->unitCount + 1));
    for (i = 0; i < r->unitCount; i++) r->units[i] = prUnitBody(r);
    r->enumCount = prCount(r);
    if ((size_t)r->enumCount > r->n) PR_FAIL(r, "The program file is damaged (it ends too early).");
    r->enums = (FcEnumType**)fcAlloc(c, sizeof(FcEnumType*) * (size_t)(r->enumCount + 1));
    for (i = 0; i < r->enumCount; i++) {
        const char* name = prName(r);
        bool flags = prBool(r);
        FcList* values = fcListNew(c);
        FcList* names = prEnumList(r, fcListNew(c), values);
        r->enums[i] = prIntern(r, name, flags, names, values);
    }
    linked = (FcLinked*)fcAlloc(c, sizeof *linked);
    p = (FcCompiled*)fcAlloc(c, sizeof *p);
    linked->program = p;
    linked->mode = prU8(r);
    if (linked->mode > 2) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: a program file of an unknown mode");
    linked->floatWidth = prU8(r); linked->intWidth = prU8(r); linked->wordWidth = prU8(r);
    linked->firstUserSource = (int)prInt(r);
    nativeImports = prStrList(r);
    linked->nativeImports = fcDictNew(c, FC_KEY_STR);
    for (i = 0; i < nativeImports->count; i++) if (nativeImports->items[i]) fcSetAdd(linked->nativeImports, nativeImports->items[i]);
    linked->packageNatives = prOptStrList(r);
    linked->packageNativeLibraries = prOptStrList(r);
    linked->graphicsNatives = prOptStrList(r);
    p->customUnits = fcListNew(c);
    for (n = prCount(r); n > 0; n--) {
        FcCustomUnit* u = (FcCustomUnit*)fcAlloc(c, sizeof *u);
        u->name = prName(r); u->symbol = prName(r); u->prefixed = prBool(r);
        prDims(r, &u->unit);
        u->unit.scale = prF64(r); u->unit.offset = prF64(r); u->unit.converter = prStr(r); u->unit.unitlessConverter = prStr(r);
        fcListAdd(c, p->customUnits, u);
    }
    p->enums = fcListNew(c);
    for (n = prCount(r); n > 0; n--) {
        const char* name = prName(r);
        bool flags = prBool(r);
        FcList* values = fcListNew(c);
        FcList* names = prEnumList(r, fcListNew(c), values);
        fcListAdd(c, p->enums, prIntern(r, name, flags, names, values));
    }
    p->resources = fcListNew(c);
    for (n = prCount(r); n > 0; n--) {
        FcResourceEntry* e = (FcResourceEntry*)fcAlloc(c, sizeof *e);
        int len;
        size_t at;
        e->name = prName(r);
        len = prCount(r);
        at = prNeed(r, len);
        e->data = r->in + at; e->size = (size_t)len;
        fcListAdd(c, p->resources, e);
    }
    p->externNames = fcListNew(c); p->externSigs = fcListNew(c);
    for (n = prCount(r); n > 0; n--) {
        const char* name = prName(r);
        FcExternSig* sig = prExtern(r);
        for (i = 0; i < p->externNames->count && strcmp((const char*)p->externNames->items[i], name) != 0; i++) {}
        if (i == p->externNames->count) { fcListAdd(c, p->externNames, (void*)name); fcListAdd(c, p->externSigs, sig); }
        else p->externSigs->items[i] = sig;
    }
    count = prCount(r);
    if ((size_t)count > r->n) PR_FAIL(r, "The program file is damaged (it ends too early).");
    classList = (FcRtClass**)fcAlloc(c, sizeof(FcRtClass*) * (size_t)(count + 1));
    bases = (int*)fcAlloc(c, sizeof(int) * (size_t)(count + 1));
    for (i = 0; i < count; i++) classList[i] = prClass(r, &bases[i]);
    for (i = 0; i < count; i++) if (bases[i] > 0) { if (bases[i] - 1 >= count) PR_FAIL(r, PR_INDEX); classList[i]->base = classList[bases[i] - 1]; }
    p->classes = fcDictNew(c, FC_KEY_STR);
    for (n = prCount(r); n > 0; n--) { const char* name = prName(r); int k = prCount(r); if (k >= count) PR_FAIL(r, PR_INDEX); fcDictPut(p->classes, name, classList[k]); }
    p->topLevel = prChunkRef(r);
    if (headerFlags & 2) {
        linked->sourceFiles = fcListNew(c); linked->sourceHashes = fcListNew(c);
        for (n = prCount(r); n > 0; n--) { fcListAdd(c, linked->sourceFiles, (void*)prStr(r)); fcListAdd(c, linked->sourceHashes, (void*)prStr(r)); }
    }
    if (headerFlags & 4) {
        linked->stackTrace = prU8(r);
        linked->traceFiles = fcListNew(c);
        for (n = prCount(r); n > 0; n--) fcListAdd(c, linked->traceFiles, (void*)prStr(r));
    }
    if (headerFlags & 8) for (n = prCount(r); n > 0; n--) { prName(r); prName(r); }   /* (the interfaces of its modules: the dump shows none) */
    if (r->at != r->n) PR_FAIL(r, "The program file is damaged (bytes behind its end).");
    /* RelinkAfterDeserialize: the enums by their names (ToDictionary: a name twice is an ArgumentException), the base of a class is the class of its name */
    for (i = 0; i < p->enums->count; i++)
        for (n = 0; n < i; n++)
            if (streq0(((FcEnumType*)p->enums->items[i])->name, ((FcEnumType*)p->enums->items[n])->name))
                PR_FAIL(r, fcFmt(c, "The program file is damaged (An item with the same key has already been added. Key: %s).", ((FcEnumType*)p->enums->items[i])->name));
    FC_DICT_EACH(p->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        if (rc->base) {
            FcRtClass* canonical = (FcRtClass*)fcDictGet(p->classes, rc->base->name);
            if (!canonical) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "unhandled: InvalidOperationException: Base class '%s' of '%s' is missing in Classes - corrupt/incomplete program cache?", rc->base->name, rc->name));
            rc->base = canonical;
        }
    }
    return linked;
}
#undef PR_FAIL
#undef PR_INDEX
#undef PR_RANGE
#undef PR_OVERFLOW
