/* native/forge/typeref.c - what the resolver and the type checker ask of a type and of a generic name: src/fire/Ast/TypeRef.cs and
 * src/fire/Ast/GenericClassNames.cs (commit c7f13d97), src/fire/Values/ArrayElem.cs (Of), src/fire/Bytecode/DeclaredKind.cs (Of). A `Func<string, bool>`
 * of C# is a FcKnown: a function and its closure. */

typedef struct FcKnown { bool (*fn)(void* self, const char* name); void* self; } FcKnown;
static bool fcKnows(FcKnown k, const char* name) { return k.fn(k.self, name); }

#define FC_INFERRED_MARKER "var"
static bool streq0(const char* a, const char* b) { return a == b || (a && b && strcmp(a, b) == 0); }

/* GenericClassNames */
static const char* fcMangle(FcCtx* c, const char* name, int typeParamCount) { return fcFmt(c, "%s`%d", name, typeParamCount); }
static const char* fcPlainName(FcCtx* c, const char* name) {
    const char* q = strchr(name, '`');
    return q ? fcStrN(c, name, (size_t)(q - name)) : name;
}

static bool fcTrIsInferred(const FcTypeRef* t) { return streq0(t->baseName, FC_INFERRED_MARKER); }
static const char* fcTrRequiredUnit(const FcTypeRef* t) { return t->unit == NULL || streq0(t->unit, "any") ? NULL : t->unit; }
FC_LATER static int fcTrTypeArgsCount(const FcTypeRef* t) { return t->typeArgs ? t->typeArgs->count : 0; }

/* TypeRef.ResolveBaseName */
static const char* fcTrResolveBaseName(FcCtx* c, const FcTypeRef* t, FcKnown known) {
    int i;
    if (fcStartsWith(t->baseName, "global.")) return t->baseName + 7;
    if (t->namespaces && t->namespaces->own && !strchr(t->baseName, '.')) {
        const char* cand = fcFmt(c, "%s.%s", (const char*)t->namespaces->items[0], t->baseName);
        if (fcKnows(known, cand)) return cand;
    }
    if (fcKnows(known, t->baseName)) return t->baseName;
    if (t->namespaces)
        for (i = 0; i < t->namespaces->count; i++) {
            const char* cand = fcFmt(c, "%s.%s", (const char*)t->namespaces->items[i], t->baseName);
            if (fcKnows(known, cand)) return cand;
        }
    return t->baseName;
}

/* TypeRef.AmbiguousCandidates */
static FcList* fcTrAmbiguousCandidates(FcCtx* c, const FcTypeRef* t, FcKnown known, const char* ownNamespace) {
    FcList* matches = fcListNew(c);
    int i;
    if (t->namespaces == NULL || t->namespaces->count < 2 || fcKnows(known, t->baseName)) return matches;
    if (ownNamespace != NULL && streq0((const char*)t->namespaces->items[0], ownNamespace) && fcKnows(known, fcFmt(c, "%s.%s", ownNamespace, t->baseName))) return matches;
    for (i = 0; i < t->namespaces->count; i++) {
        const char* cand = fcFmt(c, "%s.%s", (const char*)t->namespaces->items[i], t->baseName);
        if (fcKnows(known, cand) && !fcListHasStr(matches, cand)) fcListAdd(c, matches, (void*)cand);
    }
    if (matches->count <= 1) matches->count = 0;
    return matches;
}

/* TypeRef.ToString */
static void fcTrText(FcBuf* b, const FcTypeRef* t) {
    int i;
    if (t->lambdaSignature) {
        const FcLambdaSignature* s = t->lambdaSignature;
        if (s->returnTypeName) { fcBufStr(b, s->returnTypeName); fcBufChar(b, ' '); }
        fcBufStr(b, "lambda");
        if (s->paramTypeNames && s->paramTypeNames->count > 0) {
            fcBufChar(b, '<');
            for (i = 0; i < s->paramTypeNames->count; i++) { if (i) fcBufStr(b, ", "); fcBufStr(b, (const char*)s->paramTypeNames->items[i]); }
            fcBufChar(b, '>');
        }
        return;
    }
    fcBufStr(b, t->baseName);
    if (t->bitWidth != FC_NULL_INT) fcBufFmt(b, "[%d]", t->bitWidth);
    if (t->typeArgs && t->typeArgs->count > 0) {
        fcBufChar(b, '<');
        for (i = 0; i < t->typeArgs->count; i++) { if (i) fcBufStr(b, ", "); fcTrText(b, (const FcTypeRef*)t->typeArgs->items[i]); }
        fcBufChar(b, '>');
    }
    for (i = 0; i < t->pointerDepth; i++) fcBufChar(b, '*');
}
FC_LATER static const char* fcTrString(FcCtx* c, const FcTypeRef* t) { FcBuf b = {0}; fcBufStr(&b, ""); fcTrText(&b, t); return fcBufDone(c, &b); }

/* GenericClassNames.ResolveNewTarget */
typedef struct FcKnownMangled { FcCtx* c; FcKnown inner; int count; } FcKnownMangled;
static bool fcKnownOrMangled(void* self, const char* n) {
    FcKnownMangled* k = (FcKnownMangled*)self;
    return fcKnows(k->inner, n) || fcKnows(k->inner, fcMangle(k->c, n, k->count));
}
static const char* fcResolveNewTarget(FcCtx* c, const FcTypeRef* classRef, int typeArgCount, FcKnown known) {
    FcKnownMangled km;
    FcKnown k2;
    const char* plain;
    const char* mangled;
    if (typeArgCount == 0) return fcTrResolveBaseName(c, classRef, known);
    km.c = c; km.inner = known; km.count = typeArgCount;
    k2.fn = fcKnownOrMangled; k2.self = &km;
    plain = fcTrResolveBaseName(c, classRef, k2);
    mangled = fcMangle(c, plain, typeArgCount);
    return fcKnows(known, mangled) ? mangled : plain;
}

/* ArrayElem: the codes of the element types */
enum { AE_None = 0, AE_I8 = 1, AE_I16 = 2, AE_I32 = 3, AE_I64 = 4, AE_F8 = 5, AE_F16 = 6, AE_F32 = 7, AE_F64 = 8, AE_Bool = 9, AE_Char = 10, AE_U8 = 11, AE_Word = 12, AE_Class = 13 };
#define AE_DefinedFlag 0x40
static int fcArrayElemOf(const FcTypeRef* t) {
    if (t == NULL || t->pointerDepth > 0 || t->lambdaSignature != NULL || t->typeArgCount > 0) return AE_None;
    if (streq0(t->baseName, "int")) {
        if (t->isUnsigned) return t->bitWidth == 8 ? AE_U8 : AE_None;
        switch (t->bitWidth) { case FC_NULL_INT: case 64: return AE_I64; case 32: return AE_I32; case 16: return AE_I16; case 8: return AE_I8; default: return AE_None; }
    }
    if (streq0(t->baseName, "float")) {
        switch (t->bitWidth) { case FC_NULL_INT: case 64: return AE_F64; case 32: return AE_F32; case 16: return AE_F16; case 8: return AE_F8; default: return AE_None; }
    }
    if (streq0(t->baseName, "byte")) return AE_U8;
    if (streq0(t->baseName, "word")) return AE_Word;
    if (streq0(t->baseName, "bool")) return AE_Bool;
    if (streq0(t->baseName, "char")) return AE_Char;
    return AE_None;
}

#define FC_WORD_WIDTH_CODE 0x400
/* TypeRef.WidthCode */
static int fcTrWidthCode(const FcTypeRef* t) {
    int bits = t->bitWidth;
    if (t->pointerDepth > 0 || t->arrayRank > 0) return 0;
    if (streq0(t->baseName, "word") && bits == FC_NULL_INT) return FC_WORD_WIDTH_CODE;
    if (bits == FC_NULL_INT) return 0;
    if (streq0(t->baseName, "int") && (bits == 8 || bits == 16 || bits == 32)) return bits | (t->isUnsigned ? 0x200 : 0);
    if (streq0(t->baseName, "int") && bits == 64 && !t->isUnsigned) return 64;
    if (streq0(t->baseName, "float") && (bits == 8 || bits == 16 || bits == 32)) return bits | 0x100;
    return 0;
}
/* TypeRef.ArrayWidthCode */
static int fcTrArrayWidthCode(const FcTypeRef* t, int rank) {
    int elem;
    if (!(rank > 0 && rank < 256 && t->pointerDepth == 0 && t->arrayRank == 0)) return 0;
    elem = fcArrayElemOf(t);
    if (elem == 0) return 0;
    return 0x10000 | (rank << 8) | elem | (t->nonNull && elem != AE_U8 && elem != AE_Word ? AE_DefinedFlag : 0);
}

/* DeclaredKind.Of(type) without an initializer: 0 when the declaration is no number, bool, defined word or defined char */
static int fcDeclaredKindOf(const FcTypeRef* t) {
    if (!t || t->pointerDepth != 0 || t->arrayRank != 0 || t->lambdaSignature != NULL) return 0;
    if (streq0(t->baseName, "int") || streq0(t->baseName, "float") || streq0(t->baseName, "bool")) return 1;
    if ((streq0(t->baseName, "word") || streq0(t->baseName, "char")) && t->nonNull) return 1;
    return 0;
}
