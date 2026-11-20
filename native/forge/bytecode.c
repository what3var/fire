/* native/forge/bytecode.c - what the bytecode compiler builds (commit 6bc618e7): the opcodes (src/fire/Bytecode/OpCode.cs, through
 * native/tables/opcodes.def), Chunk.cs, FunctionProto.cs, HandlerTemplate.cs, ClassMeta.cs, RuntimeClass.cs (the parts the compiler fills:
 * AddMethod, AddConstructor), ExternSignature.cs, DeclaredType.cs (the texts the compiler writes), DeclaredKind.cs, NativeRegistry.cs (the indexes),
 * src/fire/Values/EnumType.cs and a constant of a chunk (src/fire/Values/Value.cs: Kind, Width, Wide, Unit, Enum). Lists and dictionaries keep
 * the order of .NET (dict.c); everything lives in the arena of the context. */

typedef enum FcOp {
#define FIRE_OP(name, number, operands) OP_##name = number,
#include "../tables/opcodes.def"
#undef FIRE_OP
    OP__COUNT
} FcOp;




/* ---- EnumType: interned by its key (EnumType.Intern) ---- */
typedef struct FcEnumType { const char* name; bool isFlags; FcList* names; FcList* values; const char* key; } FcEnumType;   /* values: int64_t* */

static const char* fcEnumKeyOf(FcCtx* c, const char* name, bool isFlags, FcList* names, FcList* values) {
    FcBuf b = {0};
    int i;
    if (isFlags) fcBufStr(&b, "flag ");
    fcBufStr(&b, name);
    fcBufChar(&b, '{');
    for (i = 0; i < names->count; i++) {
        if (i) fcBufChar(&b, ',');
        fcBufFmt(&b, "%s=%" PRId64, (const char*)names->items[i], *(int64_t*)values->items[i]);
    }
    fcBufChar(&b, '}');
    return fcBufDone(c, &b);
}

/* ---- a constant of a chunk (a Value: undefined, bool, int, float, char, string, word) ---- */
enum { CK_Undefined = 0, CK_Bool = 1, CK_Int = 2, CK_Float = 3, CK_Char = 4, CK_String = 5, CK_Word = 6 };   /* the tags of the file */
typedef struct FcConst {
    int kind;
    int64_t i;              /* int, bool, char (a UTF-16 unit) */
    double f;
    uint64_t w;
    const char* s;          /* string (fc8) */
    const FcU* unit;        /* int, float, undefined: never NULL (Unit.Unitless) */
    const FcEnumType* en;   /* an int of an enum */
    int width;              /* NumericWidth: 8, 16, 32, 64 */
    int wide;               /* 0, 32, 64 */
} FcConst;

static FcConst* fcConstNew(FcCtx* c, int kind) {
    FcConst* v = (FcConst*)fcAlloc(c, sizeof *v);
    v->kind = kind;
    v->width = 64;
    if (kind == CK_Int || kind == CK_Float || kind == CK_Undefined) v->unit = FC_UNITLESS;
    return v;
}
static FcConst* fcMakeUndefined(FcCtx* c) { return fcConstNew(c, CK_Undefined); }
static FcConst* fcMakeInt(FcCtx* c, int64_t v) { FcConst* k = fcConstNew(c, CK_Int); k->i = v; return k; }
static FcConst* fcMakeIntU(FcCtx* c, int64_t v, const FcU* unit) { FcConst* k = fcMakeInt(c, v); if (unit) k->unit = unit; return k; }
static FcConst* fcMakeFloatU(FcCtx* c, double v, const FcU* unit) { FcConst* k = fcConstNew(c, CK_Float); k->f = v; if (unit) k->unit = unit; return k; }
static FcConst* fcMakeBool(FcCtx* c, bool v) { FcConst* k = fcConstNew(c, CK_Bool); k->i = v ? 1 : 0; return k; }
static FcConst* fcMakeString(FcCtx* c, const char* s) { FcConst* k = fcConstNew(c, CK_String); k->s = s; return k; }
static FcConst* fcMakeChar(FcCtx* c, uint16_t ch) { FcConst* k = fcConstNew(c, CK_Char); k->i = ch; return k; }
static FcConst* fcMakeWord(FcCtx* c, uint64_t w) { FcConst* k = fcConstNew(c, CK_Word); k->w = w; return k; }
static FcConst* fcMakeEnum(FcCtx* c, int64_t v, const FcEnumType* type) { FcConst* k = fcMakeInt(c, v); k->en = type; return k; }
/* Value.WithWide */
static FcConst* fcConstWithWide(FcCtx* c, const FcConst* v, int wide) {
    FcConst* k;
    if (v->kind != CK_Int) return (FcConst*)v;
    k = (FcConst*)fcAlloc(c, sizeof *k);
    *k = *v;
    k->wide = wide;
    return k;
}

/* the value of a literal of the tree (LiteralExpr.Value) */
static const FcU* fcUParse(FcCtx* c, const char* symbol);
static FcConst* fcConstOfValue(FcCtx* c, const FcValue* v) {
    switch (v->kind) {
        case VK_Bool: return fcMakeBool(c, v->b);
        case VK_Int: return fcMakeIntU(c, v->i, v->unitv ? v->unitv : (v->unit ? fcUParse(c, v->unit) : NULL));
        case VK_Float: return fcMakeFloatU(c, v->f, v->unitv ? v->unitv : (v->unit ? fcUParse(c, v->unit) : NULL));
        case VK_Char: { const unsigned char* p = (const unsigned char*)v->s; return fcMakeChar(c, p && *p ? fc8Next(&p) : (uint16_t)v->i); }
        case VK_String: return fcMakeString(c, v->s);
        default: { FcConst* k = fcMakeUndefined(c); if (v->unitv) k->unit = v->unitv; else if (v->unit) k->unit = fcUParse(c, v->unit); return k; }
    }
}

/* ---- HandlerTemplate ---- */
typedef struct FcHandler { FcList* catchTypes; FcList* catchAddrs; int finallyAddr; } FcHandler;   /* catchTypes: fc8 or NULL; catchAddrs: int; finallyAddr: -1 none */

/* ---- Chunk ---- */
typedef struct FcLineEntry { int offset, source, line; } FcLineEntry;
typedef struct FcLocalName { int depth, slot; const char* name; } FcLocalName;
typedef struct FcRtClass FcRtClass;
typedef struct FcProto FcProto;
typedef struct FcChunk {
    uint8_t* code;
    int len, cap;
    FcList* constants;      /* FcConst* */
    FcList* units;          /* const FcU* */
    FcList* functions;      /* FcProto* */
    FcList* receiverOffsets; FcList* receiverTexts;   /* ReceiverTypeArgs (NULL: none) */
    FcList* handlers;       /* FcHandler* */
    FcLineEntry* lines;
    int lineCount, lineCap;
    int lastMarkedSource, lastMarkedLine;
    FcLocalName* names;     /* DebugLocalNames in the order of their keys */
    int nameCount, nameCap;
    FcDict* nameIndex;      /* "depth,slot" -> index + 1 */
    int lastOpStart, lastHere;
    FcRtClass* ownerClass;
} FcChunk;

static FcChunk* fcChunkNew(FcCtx* c) {
    FcChunk* k = (FcChunk*)fcAlloc(c, sizeof *k);
    k->constants = fcListNew(c);
    k->units = fcListNew(c);
    k->functions = fcListNew(c);
    k->handlers = fcListNew(c);
    k->lastMarkedSource = -1;
    k->lastMarkedLine = -1;
    k->lastOpStart = -1;
    k->lastHere = -1;
    k->nameIndex = fcDictNew(c, FC_KEY_STR);
    return k;
}
static void fcChunkByte(FcCtx* c, FcChunk* k, int b) {
    if (k->len == k->cap) {
        int ncap = k->cap ? k->cap * 2 : 64;
        uint8_t* n = (uint8_t*)fcAlloc(c, (size_t)ncap);
        if (k->len) memcpy(n, k->code, (size_t)k->len);
        k->code = n;
        k->cap = ncap;
    }
    k->code[k->len++] = (uint8_t)b;
}
static void fcChunkMarkLine(FcCtx* c, FcChunk* k, int source, int line) {
    if (source == k->lastMarkedSource && line == k->lastMarkedLine) return;
    if (k->lineCount == k->lineCap) {
        int ncap = k->lineCap ? k->lineCap * 2 : 16;
        FcLineEntry* n = (FcLineEntry*)fcAlloc(c, sizeof(FcLineEntry) * (size_t)ncap);
        if (k->lineCount) memcpy(n, k->lines, sizeof(FcLineEntry) * (size_t)k->lineCount);
        k->lines = n;
        k->lineCap = ncap;
    }
    k->lines[k->lineCount].offset = k->len;
    k->lines[k->lineCount].source = source;
    k->lines[k->lineCount].line = line;
    k->lineCount++;
    k->lastMarkedSource = source;
    k->lastMarkedLine = line;
}
/* DebugLocalNames[(depth, slot)] = name */
static void fcChunkMarkLocalName(FcCtx* c, FcChunk* k, int depth, int slot, const char* name) {
    const char* key = fcFmt(c, "%d,%d", depth, slot);
    void* at;
    if (fcDictTry(k->nameIndex, key, &at)) { k->names[FC_VALI(at) - 1].name = name; return; }
    if (k->nameCount == k->nameCap) {
        int ncap = k->nameCap ? k->nameCap * 2 : 8;
        FcLocalName* n = (FcLocalName*)fcAlloc(c, sizeof(FcLocalName) * (size_t)ncap);
        if (k->nameCount) memcpy(n, k->names, sizeof(FcLocalName) * (size_t)k->nameCount);
        k->names = n;
        k->nameCap = ncap;
    }
    k->names[k->nameCount].depth = depth;
    k->names[k->nameCount].slot = slot;
    k->names[k->nameCount].name = name;
    k->nameCount++;
    fcDictPut(k->nameIndex, key, FC_IVAL(k->nameCount));
}
static void fcChunkMarkDeclaredName(FcCtx* c, FcChunk* k, int offset, const char* name) { fcChunkMarkLocalName(c, k, -1, offset, name); }
static int fcChunkAddConstant(FcCtx* c, FcChunk* k, FcConst* v) { fcListAdd(c, k->constants, v); return k->constants->count - 1; }
static int fcChunkAddUnit(FcCtx* c, FcChunk* k, const FcU* u) { fcListAdd(c, k->units, (void*)u); return k->units->count - 1; }
static int fcChunkAddProto(FcCtx* c, FcChunk* k, FcProto* p) { fcListAdd(c, k->functions, p); return k->functions->count - 1; }
static int fcChunkAddHandler(FcCtx* c, FcChunk* k, FcHandler* h) { fcListAdd(c, k->handlers, h); return k->handlers->count - 1; }
static void fcChunkOp(FcCtx* c, FcChunk* k, FcOp op) { k->lastOpStart = k->len; fcChunkByte(c, k, (int)op); }
static bool fcChunkEndsWithOp(const FcChunk* k, FcOp op, int operandBytes) {
    return k->lastOpStart >= 0 && k->lastOpStart == k->len - 1 - operandBytes && k->code[k->lastOpStart] == (uint8_t)op && k->lastHere != k->len;
}
static void fcChunkReplaceLastOp(FcChunk* k, FcOp op) { k->code[k->lastOpStart] = (uint8_t)op; }
static void fcChunkU16(FcCtx* c, FcChunk* k, int v) { fcChunkByte(c, k, v & 0xFF); fcChunkByte(c, k, (v >> 8) & 0xFF); }
static void fcChunkPatchU16(FcChunk* k, int at, int v) { k->code[at] = (uint8_t)(v & 0xFF); k->code[at + 1] = (uint8_t)((v >> 8) & 0xFF); }
static int fcChunkHere(FcChunk* k) { k->lastHere = k->len; return k->len; }
/* ReceiverTypeArgs[offset] = text */
static void fcChunkReceiverTypeArgs(FcCtx* c, FcChunk* k, int offset, const char* text) {
    int i;
    if (!k->receiverOffsets) { k->receiverOffsets = fcListNew(c); k->receiverTexts = fcListNew(c); }
    for (i = 0; i < k->receiverOffsets->count; i++) if (FC_VALI(k->receiverOffsets->items[i]) == offset) { k->receiverTexts->items[i] = (void*)text; return; }
    fcListAdd(c, k->receiverOffsets, FC_IVAL(offset));
    fcListAdd(c, k->receiverTexts, (void*)text);
}

/* ---- FunctionProto ---- */
struct FcProto {
    FcChunk* chunk;
    int paramCount;
    FcList* paramDefaults;   /* FcProto* or NULL */
    int access;              /* AccessModifier, -1: null */
    bool isStatic;
    uint32_t refMask;
    FcList* selectorPath;    /* NULL: none */
    uint64_t captureCopyMask, captureMovedMask;
    FcList* paramTypes;      /* fc8 or NULL entries; NULL: none */
    const char* fieldGetterOf;
    FcList* paramNames;      /* NULL: none */
    int typeParamCount;
    bool readsOnly;
    FcList* specializations; /* NULL: none */
    const char* specOwner;
    FcList* specArgs;        /* NULL: none */
    int64_t specDefinedMask;
    uint8_t* specParamKinds;
    int specParamKindCount;  /* -1: none */
};
static FcProto* fcProtoNew(FcCtx* c, FcChunk* chunk, int paramCount, int access, FcList* paramDefaults, bool isStatic) {
    FcProto* p = (FcProto*)fcAlloc(c, sizeof *p);
    p->chunk = chunk;
    p->paramCount = paramCount;
    p->paramDefaults = paramDefaults ? paramDefaults : fcListNew(c);
    p->access = access;
    p->isStatic = isStatic;
    p->specParamKindCount = -1;
    return p;
}

/* ---- ClassMeta ---- */
typedef struct FcAttributeMeta { const char* name; FcList* argTexts; const char* argsJson; } FcAttributeMeta;
typedef struct FcMemberMeta {
    const char* name; const char* kind; const char* typeName; const char* access;
    bool isStatic, isReadonly, canRead, canWrite;
    const char* unit;
    FcList* paramNames; FcList* paramTypes; FcList* attributes;
} FcMemberMeta;
typedef struct FcClassMeta { FcList* members; FcList* baseNames; FcList* attributes; FcList* declaredInterfaces; } FcClassMeta;

static FcMemberMeta* fcMemberMetaNew(FcCtx* c) {
    FcMemberMeta* m = (FcMemberMeta*)fcAlloc(c, sizeof *m);
    m->name = ""; m->kind = "field"; m->typeName = ""; m->access = "public";
    m->canRead = m->canWrite = true;
    m->unit = "";
    m->paramNames = fcListNew(c); m->paramTypes = fcListNew(c); m->attributes = fcListNew(c);
    return m;
}

/* ---- RuntimeClass ---- */
typedef struct FcFieldInfo {
    const char* requiredUnit;
    int requiredKind;
    const char* requiredType;
    int requiredArray;
    const char* requiredArrayType;
    const char* arrayOfParam;
    bool derivedDefined;
    int access;
    bool isStatic;
    const char* typeText;
    FcList* attributes;      /* FcAttributeMeta*, NULL: none */
} FcFieldInfo;

struct FcRtClass {
    const char* name;
    FcClassDecl* decl;
    FcRtClass* base;
    bool isActor, isRecord, isReflectionHelper, keep, notSerializable, noCopy;
    FcClassMeta* meta;
    FcList* interfaces;
    FcList* baseTypeArgs;    /* NULL: none */
    FcList* keptMembers;     /* NULL: none */
    FcList* fieldNames; FcList* fieldInits;
    FcDict* ownFieldInfo;    /* name -> FcFieldInfo* */
    FcList* staticFieldNames; FcList* staticFieldInits;
    FcDict* readonlyStatics; /* NULL: none */
    FcDict* methods;         /* name -> FcList* of FcProto* */
    FcList* ctorArities; FcList* ctorProtos;   /* Constructors */
    FcList* extraConstructors;
    FcProto* destructor;
    FcDict* thisReads;       /* name -> FcList* of fc8, NULL: none */
};

static FcRtClass* fcRtClassNew(FcCtx* c, const char* name, FcClassDecl* decl) {
    FcRtClass* rc = (FcRtClass*)fcAlloc(c, sizeof *rc);
    rc->name = name;
    rc->decl = decl;
    rc->interfaces = fcListNew(c);
    rc->fieldNames = fcListNew(c); rc->fieldInits = fcListNew(c);
    rc->ownFieldInfo = fcDictNew(c, FC_KEY_STR);
    rc->staticFieldNames = fcListNew(c); rc->staticFieldInits = fcListNew(c);
    rc->methods = fcDictNew(c, FC_KEY_STR);
    rc->ctorArities = fcListNew(c); rc->ctorProtos = fcListNew(c);
    rc->extraConstructors = fcListNew(c);
    return rc;
}
/* Overloads.SameSignature: the same number of parameters and the same types (Overloads.TypeAt) */
static const char* fcOverloadTypeAt(const FcProto* p, int i) {
    const char* t = p->paramTypes && i < p->paramTypes->count ? (const char*)p->paramTypes->items[i] : NULL;
    return t ? t : "";
}
static bool fcSameSignature(const FcProto* a, const FcProto* b) {
    int i;
    if (a->paramCount != b->paramCount || a->typeParamCount != b->typeParamCount) return false;
    for (i = 0; i < a->paramCount; i++) if (strcmp(fcOverloadTypeAt(a, i), fcOverloadTypeAt(b, i)) != 0) return false;
    return true;
}
/* RuntimeClass.AddMethod */
static void fcRtAddMethod(FcCtx* c, FcRtClass* rc, const char* name, FcProto* proto);
/* RuntimeClass.AddConstructor */
static void fcRtAddConstructor(FcCtx* c, FcRtClass* rc, FcProto* proto);

/* ---- ExternSignature ---- */
typedef struct FcExternSig {
    const char* libName;
    FcList* paramTypes;      /* FcTypeRef* (NULL entries) */
    FcTypeRef* returnType;
    const char* inlineC;
    FcList* paramNames;      /* NULL: none */
    int inlineSource, inlineLine;
    const char* displayName;
} FcExternSig;

/* ---- DeclaredType: the texts ---- */
#define DT_DisplayEnd "\x01"
#define DT_TypeParamEnd '\x03'
#define DT_NonNullMark '\x04'
#define DT_TypeArgsMark '\x05'
#define DT_TypeArgSep "\x06"
static const char* fcDtNonNullOf(FcCtx* c, const char* text) { return text[0] == DT_NonNullMark ? text : fcFmt(c, "\x04%s", text); }
static bool fcDtIsNonNull(const char* text) { return text && text[0] == DT_NonNullMark; }
static const char* fcDtWithTypeArgs(FcCtx* c, const char* text, FcList* typeArgs) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, text);
    fcBufChar(&b, DT_TypeArgsMark);
    for (i = 0; i < typeArgs->count; i++) { if (i) fcBufStr(&b, DT_TypeArgSep); fcBufStr(&b, (const char*)typeArgs->items[i]); }
    return fcBufDone(c, &b);
}
static const char* fcDtWithoutTypeArgs(FcCtx* c, const char* text) { const char* at = strchr(text, DT_TypeArgsMark); return at ? fcStrN(c, text, (size_t)(at - text)) : text; }
static const char* fcDtOfName(FcCtx* c, const char* name) { return fcFmt(c, "%s" DT_DisplayEnd "%s", fcPlainName(c, name), name); }
static const char* fcDtOfLambda(FcCtx* c, int paramCount) { return fcFmt(c, "lambda with %d parameter(s)" DT_DisplayEnd "lambda/%d", paramCount, paramCount); }
/* DeclaredType.OfTypeParam */
static const char* fcDtOfTypeParam(FcCtx* c, FcTypeParam* tp, int index, const char* owner, const char* (*resolveName)(void* self, const char* n), void* self) {
    FcBuf b = {0};
    int g, k;
    fcBufStr(&b, tp->name);
    if (tp->constraintGroups->count > 0) {
        fcBufStr(&b, " (");
        for (g = 0; g < tp->constraintGroups->count; g++) {
            FcTypeConstraintGroup* grp = (FcTypeConstraintGroup*)tp->constraintGroups->items[g];
            if (g) fcBufStr(&b, " or ");
            for (k = 0; k < grp->constraints->count; k++) {
                FcTypeConstraint* tc = (FcTypeConstraint*)grp->constraints->items[k];
                if (k) fcBufStr(&b, " and ");
                fcBufStr(&b, tc->kind == TCK_IsOf ? "is of " : "is in ");
                fcBufStr(&b, tc->name);
            }
        }
        fcBufChar(&b, ')');
    }
    fcBufFmt(&b, DT_DisplayEnd "@%d@%s", index, owner);
    fcBufChar(&b, DT_TypeParamEnd);
    for (g = 0; g < tp->constraintGroups->count; g++) {
        FcTypeConstraintGroup* grp = (FcTypeConstraintGroup*)tp->constraintGroups->items[g];
        if (g) fcBufChar(&b, '|');
        for (k = 0; k < grp->constraints->count; k++) {
            FcTypeConstraint* tc = (FcTypeConstraint*)grp->constraints->items[k];
            if (k) fcBufChar(&b, '&');
            if (tc->kind == TCK_IsOf) fcBufStr(&b, resolveName(self, tc->name));
            else { fcBufStr(&b, "in:"); fcBufStr(&b, tc->name); }
        }
    }
    return fcBufDone(c, &b);
}
/* DeclaredType.Parse - what the compiler asks: the index and the class of a type parameter (-1: none) */
static int fcDtTypeParamIndex(FcCtx* c, const char* text, const char** typeParamClass) {
    const char* t = text;
    const char* end;
    const char* rest;
    const char* tpEnd;
    const char* at;
    if (t[0] == DT_NonNullMark) t++;
    end = strchr(t, '\x01');
    if (!end) return -1;
    rest = end + 1;
    if (*rest != '@') return -1;
    tpEnd = strchr(rest, DT_TypeParamEnd);
    if (!tpEnd) return -1;
    at = strchr(rest + 1, '@');
    if (!at || at > tpEnd) return -1;
    if (typeParamClass) *typeParamClass = fcStrN(c, at + 1, (size_t)(tpEnd - at - 1));
    return atoi(rest + 1);
}

/* ---- DeclaredKind ---- */
#define DK_UnitlessBit 0x80
#define DK_KeepsWidthBit 0x40
#define DK_NonNullBit 0x20
#define DK_StringKind (TAG_String + 1)
static bool fcDkIsUnitlessLiteral(const FcExpr* e) {
    if (!e) return true;
    if (e->kind == EK_LiteralExpr) {
        const FcValue* v = &((const FcLiteralExpr*)e)->value;
        return (v->kind == VK_Int || v->kind == VK_Float) && (v->unitv ? fcUIsUnitless(v->unitv) : v->unit == NULL);
    }
    if (e->kind == EK_UnaryExpr) return fcDkIsUnitlessLiteral(((const FcUnaryExpr*)e)->operand);
    return false;
}
static int fcDkOf(const FcTypeRef* type, const FcExpr* initializer) {
    int unitless, nonNull;
    if (!type || type->pointerDepth != 0 || type->arrayRank != 0 || type->lambdaSignature != NULL) return 0;
    unitless = type->unit == NULL && fcDkIsUnitlessLiteral(initializer) ? DK_UnitlessBit : 0;
    nonNull = type->nonNull ? DK_NonNullBit : 0;
    if (streq0(type->baseName, "int")) return (TAG_Int + 1) | unitless | nonNull | ((type->bitWidth == 32 || type->bitWidth == 64) && !type->isUnsigned ? DK_KeepsWidthBit : 0);
    if (streq0(type->baseName, "float")) return (TAG_Float + 1) | unitless | nonNull;
    if (streq0(type->baseName, "bool")) return (TAG_Bool + 1) | nonNull;
    if (streq0(type->baseName, "word") && type->nonNull) return (TAG_Word + 1) | nonNull;
    if (streq0(type->baseName, "char") && type->nonNull) return (TAG_Char + 1) | nonNull;
    return 0;
}
static int fcDkOfStore(const FcTypeRef* type, const FcExpr* initializer) {
    int kind = fcDkOf(type, initializer);
    if (kind) return kind;
    return type && type->pointerDepth == 0 && type->arrayRank == 0 && type->lambdaSignature == NULL && streq0(type->baseName, "string") ? DK_StringKind : 0;
}
static bool fcDkIsString(int required) { return (required & 0x1F) == DK_StringKind; }
static bool fcDkIsUnitless(int required) { return (required & DK_UnitlessBit) != 0; }
/* DeclaredKind.ZeroOf */
static FcConst* fcDkZeroOf(FcCtx* c, int required, const FcU* unit) {
    switch ((required & 0x1F) - 1) {
        case TAG_Int: return fcMakeIntU(c, 0, unit);
        case TAG_Float: return fcMakeFloatU(c, 0, unit);
        case TAG_Word: return fcMakeWord(c, 0);
        case TAG_Char: return fcMakeChar(c, 0);
        case TAG_String: return fcMakeString(c, "");
        default: return fcMakeBool(c, false);
    }
}

/* ArrayElem.OpenKindOf */
static int fcArrayOpenKindOf(const FcTypeRef* t) {
    int e = fcArrayElemOf(t);
    if (e >= AE_I8 && e <= AE_I64) return AE_I64;
    if (e >= AE_F8 && e <= AE_F64) return AE_F64;
    if (e == AE_Bool || e == AE_Char) return e;
    return AE_None;
}
#define AE_OpenFlag 0x80

/* ---- NativeRegistry: the names in the order of their indexes ---- */
typedef struct FcNatives { FcDict* index; FcList* names; FcDict* tryable; } FcNatives;   /* name -> index + 1 */
static void fcNativesInit(FcCtx* c, FcNatives* n) { n->index = fcDictNew(c, FC_KEY_STR); n->names = fcListNew(c); n->tryable = fcDictNew(c, FC_KEY_STR); }
static void fcNativesRegister(FcCtx* c, FcNatives* n, const char* name) {
    fcListAdd(c, n->names, (void*)name);
    fcDictPut(n->index, name, FC_IVAL(n->names->count));
}

/* ---- the compiled program (CompiledProgram) and the linked one (LinkedProgram) ---- */
typedef struct FcResourceEntry { const char* name; const uint8_t* data; size_t size; } FcResourceEntry;
typedef struct FcCompiled {
    FcChunk* topLevel;
    FcDict* classes;          /* name -> FcRtClass* */
    FcList* externNames; FcList* externSigs;
    FcList* enums;            /* FcEnumType* (Enums.Values: the order of the program's EnumDecls) */
    FcList* resources;        /* FcResourceEntry* */
    FcList* customUnits;      /* FcCustomUnit* */
} FcCompiled;

/* Linker's AssemblyInfo as far as a native build needs it (the subsystem, the version info and the icon: #noconsole, #name, #codename, #description, #author,
 * #comments, #icon, #version, #fileversion - the build of PE resources, build/peres.c); the texts are fc8, NULL: not set */
typedef struct FcAsmInfo {
    bool gui;                 /* SubsystemType.GUI (#noconsole) */
    const char* productName; const char* internalName; const char* fileDescription; const char* companyName; const char* comments; const char* iconPath;
    int fileVersion[4], productVersion[4];   /* the four numbers (AssemblyInfo.FileVersion/ProductVersion, "0.0.0.0" at first) */
} FcAsmInfo;
typedef struct FcLinked {
    FcCompiled* program;
    int mode;                 /* VmExecutionMode: 0 Debug, 1 Release, 2 Performance */
    int floatWidth, intWidth, wordWidth;
    int firstUserSource;
    FcDict* nativeImports;    /* HashSet<string> */
    FcList* packageNatives; FcList* packageNativeLibraries; FcList* graphicsNatives;   /* NULL: none */
    FcList* sourceFiles; FcList* sourceHashes;   /* fc8 or NULL entries */
    int optimize;             /* OptimizeFor (a native build, gen/native.c): 0 Speed, 1 Size */
    FcList* sourceModules;    /* LinkedProgram.SourceModules: "core", then the module of each source (NULL: a source of the program); NULL: none */
    FcAsmInfo* asmInfo;       /* (the directives of the version info; NULL: none - set by the linkers of linker.c and gen/native.c) */
} FcLinked;
