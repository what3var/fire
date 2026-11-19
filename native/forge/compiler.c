/* native/forge/compiler.c - port of src/fire.Compiler/Compiler.cs (commit 6bc618e7), part 1: the compiler object, the declared types, Compile,
 * the classes, the prologues of functions, the methods and constructors, the statements. Part 2 (compiler2.c): the expressions, the calls and the
 * anchor rule; compilertrees.c: Compiler.Trees.cs. Method by method in the order of the C# file.
 *
 * A Compiler of C# is a FcComp: one per function body (C# makes a new one for every method, constructor, lambda, default value), they share the
 * FcCompShared of the program (the references of the resolver, the natives, the errors, the RefParamTable, the enums, what the type checker left -
 * the [ThreadStatic] fields of C#). A NotSupportedException/InvalidOperationException is FC_ERR_NOTSUPPORTED on the context (no line: CompileExpr
 * gives it the line of the expression), a CompilerException FC_ERR_COMPILE (message and line); CompileStmt and the members of a class collect them. */

/* (one translation unit with the resolver and the type checker: the names of the C# methods they share get a prefix here) */
#define ResolveTypeRef cpResolveTypeRef
#define TypeText cpTypeText
#define AccessText cpAccessText
#define TypeArgText cpTypeArgText
#define DeclaredTypeOf cpDeclaredTypeOf

/* ---- the errors ---- */
typedef struct FcCompError { const char* message; int line; int source; } FcCompError;

/* ---- RefParamTable ---- */
typedef struct FcRefParams {
    FcDict* methods;        /* "name\x01argc" -> bool* (FcBoolMask) */
    FcDict* constructors;   /* "class\x01argc" -> FcBoolMask* */
    FcDict* methodNames;
    FcDict* lambdaFieldNames;
    FcDict* fieldWidths;    /* name -> int */
    FcDict* fieldUnits;     /* name -> fc8 or NULL (present) */
    FcDict* typeParamNames;
    FcDict* interfaceNames;
    FcDict* enumNames;
    FcDict* classDecls;     /* name -> ClassDecl* */
} FcRefParams;
typedef struct FcBoolMask { int count; bool* items; } FcBoolMask;

typedef struct FcCompShared {
    FcCtx* c;
    FcResolveResult* rr;
    FcDict* refs;
    FcNatives* natives;
    FcList* errors;            /* FcCompError* */
    FcRefParams* refParams;
    FcDict* enumTypes;         /* name -> FcEnumType* (Compiler._enumTypes, shared by all compilers of a program) */
    FcDict* knownClassNames;   /* NULL until CompileClasses has them */
    bool classMeta;
    int globalSlotCount;
    bool hasMemberSource;      /* s_memberSource */
    int memberSource;
    FcDict* rawTreeLambdas;
    FcDict* rawTreeValues;
    FcDict* bitsOf;            /* TypeChecker.BitsOf: Expr* -> int + 1 */
    bool noOverloadHints;
    bool anchorAtRunTime;
} FcCompShared;

typedef struct FcLoopCtx { int scopeDepthAtLoopBodyStart; bool isForeach; FcList* breakPatches; FcList* continuePatches; } FcLoopCtx;
typedef enum { TRY_Try, TRY_Catch } FcTryPhase;
typedef struct FcTryCtx {
    FcTryStmt* stmt;
    FcTryPhase phase;
    int outerDepth, loopCount, residentsAtStart;
    FcList* finallyJumpPatches;
    FcList* stubKeys;          /* (isBreak, levels) sorted: SortedDictionary<(bool, int), List<int>> */
    FcList* stubPatches;       /* FcList* of int, parallel to stubKeys */
} FcTryCtx;

typedef struct FcComp {
    FcCompShared* sh;
    FcCtx* c;
    FcChunk* chunk;
    int currentScopeDepth;
    bool isTopLevelCode;
    FcList* loopStack;         /* FcLoopCtx*, the top last */
    FcList* tryStack;          /* FcTryCtx* */
    FcList* residents;         /* int */
    FcRtClass* enclosingClass;
    int topLevelSourceIndex;
    FcList* methodTypeParams;  /* TypeParam*, NULL */
    FcList* lambdaTypeRefs;    /* fc8, NULL outside a lambda */
    const char* returnUnit;
    int returnKind;
    const char* returnSpec;
    const char* returnWhat;
    FcTypeRef* returnArray;
    int returnArrayRank;
    int accessor;
    int wideLiteral;
    FcDict* setterClaims;
} FcComp;

#define CC_ (cm->c)
#define CH_ (cm->chunk)

static _Noreturn void fcCompNotSupported(FcComp* cm, const char* fmt, ...) {
    char buf[2048];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof buf, fmt, ap);
    va_end(ap);
    fcThrow(cm->c, FC_ERR_NOTSUPPORTED, 0, 0, NULL, fcStr(cm->c, buf));
}
static _Noreturn void fcCompError(FcComp* cm, int line, const char* message) { fcThrow(cm->c, FC_ERR_COMPILE, line, 0, NULL, message); }
static void fcCompAddError(FcComp* cm, const char* message, int line, int source) {
    FcCompError* e = (FcCompError*)fcAlloc(CC_, sizeof *e);
    e->message = message; e->line = line; e->source = source;
    fcListAdd(CC_, cm->sh->errors, e);
}
/* the error that a catch of CompileStmt or of a member collects: a CompilerException as it is, any other with the line */
static void fcCompCollect(FcComp* cm, int line, int source) {
    const FcError* e = &cm->c->err;
    if (e->kind == FC_ERR_COMPILE) fcCompAddError(cm, e->message, e->line, source);
    else fcCompAddError(cm, e->message, line, source);
}
static bool fcCompCatchable(FcCtx* c) { return c->err.kind == FC_ERR_COMPILE || c->err.kind == FC_ERR_NOTSUPPORTED; }

/* ---- emitting (the C# code calls _chunk.EmitOp etc.) ---- */
static void E_op(FcComp* cm, FcOp op) { fcChunkOp(CC_, CH_, op); }
static void E_u8(FcComp* cm, int b) { fcChunkByte(CC_, CH_, b); }
static void E_u16(FcComp* cm, int v) { fcChunkU16(CC_, CH_, v); }
static int E_here(FcComp* cm) { return fcChunkHere(CH_); }
static void E_patch(FcComp* cm, int at, int v) { fcChunkPatchU16(CH_, at, v); }
static int E_const(FcComp* cm, FcConst* v) { return fcChunkAddConstant(CC_, CH_, v); }
static int E_str(FcComp* cm, const char* s) { return fcChunkAddConstant(CC_, CH_, fcMakeString(CC_, s)); }

static FcComp* fcCompNew(FcCompShared* sh, FcRtClass* enclosingClass) {
    FcComp* cm = (FcComp*)fcAlloc(sh->c, sizeof *cm);
    cm->sh = sh;
    cm->c = sh->c;
    cm->chunk = fcChunkNew(sh->c);
    cm->loopStack = fcListNew(sh->c);
    cm->tryStack = fcListNew(sh->c);
    cm->residents = fcListNew(sh->c);
    cm->enclosingClass = enclosingClass;
    cm->returnWhat = "";
    return cm;
}
/* `new Compiler(_refs, _natives, rc, ...) { _enumTypes = _enumTypes, _topLevelSourceIndex = _topLevelSourceIndex }` */
static FcComp* fcCompInner(FcComp* outer, FcRtClass* rc) {
    FcComp* inner = fcCompNew(outer->sh, rc);
    inner->topLevelSourceIndex = outer->topLevelSourceIndex;
    return inner;
}

static FcEnumType* EnumTypeNamed(FcComp* cm, const char* name) {
    void* t;
    if (fcDictTry(cm->sh->enumTypes, name, &t)) return (FcEnumType*)t;
    FC_DICT_EACH(cm->sh->enumTypes, e) {
        FcEnumType* et = (FcEnumType*)e->value;
        size_t n = strlen(et->name), m = strlen(name);
        if (streq0(et->name, name) || (n > m && et->name[n - m - 1] == '.' && strcmp(et->name + n - m, name) == 0)) return et;
    }
    return NULL;
}

/* CurrentSourceIndex */
static int CurrentSourceIndex(FcComp* cm) {
    if (cm->sh->hasMemberSource) return cm->sh->memberSource;
    if (cm->enclosingClass && cm->enclosingClass->decl) return cm->enclosingClass->decl->h.source;
    return cm->topLevelSourceIndex;
}

/* ---- break/continue ---- */
static void EmitEnterScope(FcComp* cm) { E_op(cm, OP_EnterScope); cm->currentScopeDepth++; }
static void EmitExitScope(FcComp* cm) { E_op(cm, OP_ExitScope); cm->currentScopeDepth--; }

static FcLoopCtx* fcLoopNew(FcComp* cm, bool isForeach) {
    FcLoopCtx* l = (FcLoopCtx*)fcAlloc(CC_, sizeof *l);
    l->scopeDepthAtLoopBodyStart = cm->currentScopeDepth;
    l->isForeach = isForeach;
    l->breakPatches = fcListNew(CC_);
    l->continuePatches = fcListNew(CC_);
    return l;
}
#define LOOP_AT(cm, k) ((FcLoopCtx*)(cm)->loopStack->items[(cm)->loopStack->count - 1 - (k)])   /* k = 0: the innermost */

/* TryCompileContext.StubPatches[(isBreak, levels)]: the list (sorted by the key: false before true, then the levels) */
static FcList* fcStubPatches(FcComp* cm, FcTryCtx* t, bool isBreak, int levels) {
    int i, key = (isBreak ? 1 << 20 : 0) + levels, at;
    FcList* l;
    for (i = 0; i < t->stubKeys->count; i++) if (FC_VALI(t->stubKeys->items[i]) == key) return (FcList*)t->stubPatches->items[i];
    for (at = 0; at < t->stubKeys->count && FC_VALI(t->stubKeys->items[at]) < key; at++) {}
    l = fcListNew(CC_);
    fcListInsert(CC_, t->stubKeys, at, FC_IVAL(key));
    fcListInsert(CC_, t->stubPatches, at, l);
    return l;
}

static void CompileBreakOrContinue(FcComp* cm, bool isBreak, int levels) {
    FcLoopCtx* target = LOOP_AT(cm, levels - 1);
    int targetLoopCount = cm->loopStack->count - levels + 1;
    int depth = cm->currentScopeDepth;
    int k, i;
    for (k = cm->tryStack->count - 1; k >= 0 && ((FcTryCtx*)cm->tryStack->items[k])->loopCount >= targetLoopCount; k--) {
        FcTryCtx* t = (FcTryCtx*)cm->tryStack->items[k];
        int innerTarget = t->phase == TRY_Catch ? t->outerDepth + 1 : t->outerDepth;
        for (; depth > innerTarget; depth--) E_op(cm, OP_ExitScope);
        if (t->phase == TRY_Try) E_op(cm, OP_UnregisterHandler);
        else {
            E_op(cm, OP_LoadLocal); E_u16(cm, 0); E_u16(cm, 0);
            E_op(cm, OP_ClearPendingResume);
            E_op(cm, OP_ExitScope);
            depth--;
            if (t->stmt->finallyBlock) E_op(cm, OP_UnregisterHandler);
        }
        if (t->stmt->finallyBlock) {
            int inside = cm->loopStack->count - t->loopCount;
            FcList* patches;
            for (i = 0; i < inside && i < levels; i++) if (LOOP_AT(cm, i)->isForeach) E_op(cm, OP_Pop);
            E_op(cm, OP_PushJump);
            patches = fcStubPatches(cm, t, isBreak, levels - inside);
            fcListAdd(CC_, patches, FC_IVAL(E_here(cm)));
            E_u16(cm, 0);
            E_op(cm, OP_Jump);
            fcListAdd(CC_, t->finallyJumpPatches, FC_IVAL(E_here(cm)));
            E_u16(cm, 0);
            return;
        }
    }
    for (; depth > target->scopeDepthAtLoopBodyStart; depth--) E_op(cm, OP_ExitScope);
    for (i = 0; i < levels - 1; i++) if (LOOP_AT(cm, i)->isForeach) E_op(cm, OP_Pop);
    E_op(cm, OP_Jump);
    fcListAdd(CC_, isBreak ? target->breakPatches : target->continuePatches, FC_IVAL(E_here(cm)));
    E_u16(cm, 0);
}

/* ---- RefParamTable ---- */
static const char* fcRpKey(FcCtx* c, const char* name, int argc) { return fcFmt(c, "%s\x01%d", name, argc); }
static void fcRpRegister(FcCtx* c, FcDict* map, const char* key, FcList* parms) {
    int required = 0, argc, i;
    while (required < parms->count && ((FcLambdaParam*)parms->items[required])->defaultValue == NULL) required++;
    for (argc = required; argc <= parms->count; argc++) {
        const char* k = fcRpKey(c, key, argc);
        FcBoolMask* existing = (FcBoolMask*)fcDictGet(map, k);
        if (existing) { for (i = 0; i < argc; i++) existing->items[i] = existing->items[i] || ((FcLambdaParam*)parms->items[i])->byRef; }
        else {
            FcBoolMask* m = (FcBoolMask*)fcAlloc(c, sizeof *m);
            m->count = argc;
            m->items = (bool*)fcAlloc(c, sizeof(bool) * (size_t)(argc + 1));
            for (i = 0; i < argc; i++) m->items[i] = ((FcLambdaParam*)parms->items[i])->byRef;
            fcDictPut(map, k, m);
        }
    }
}
static FcRefParams* fcRefParamsBuild(FcCtx* c, FcDict* classes) {
    FcRefParams* t = (FcRefParams*)fcAlloc(c, sizeof *t);
    int i, k;
    t->methods = fcDictNew(c, FC_KEY_STR); t->constructors = fcDictNew(c, FC_KEY_STR); t->methodNames = fcDictNew(c, FC_KEY_STR);
    t->lambdaFieldNames = fcDictNew(c, FC_KEY_STR); t->fieldWidths = fcDictNew(c, FC_KEY_STR); t->fieldUnits = fcDictNew(c, FC_KEY_STR);
    t->typeParamNames = fcDictNew(c, FC_KEY_STR); t->interfaceNames = fcDictNew(c, FC_KEY_STR); t->enumNames = fcDictNew(c, FC_KEY_STR);
    t->classDecls = fcDictNew(c, FC_KEY_STR);
    FC_DICT_EACH(classes, e) {
        FcClassDecl* cls = (FcClassDecl*)e->value;
        fcDictPut(t->classDecls, cls->name, cls);
        if (cls->typeParams) for (i = 0; i < cls->typeParams->count; i++) fcSetAdd(t->typeParamNames, ((FcTypeParam*)cls->typeParams->items[i])->name);
        for (i = 0; i < cls->members->count; i++) {
            FcStmt* m = (FcStmt*)cls->members->items[i];
            if (m->kind == SK_MethodDecl && ((FcMethodDecl*)m)->typeParams)
                for (k = 0; k < ((FcMethodDecl*)m)->typeParams->count; k++) fcSetAdd(t->typeParamNames, ((FcTypeParam*)((FcMethodDecl*)m)->typeParams->items[k])->name);
        }
    }
    FC_DICT_EACH(classes, e) {
        FcClassDecl* cls = (FcClassDecl*)e->value;
        for (i = 0; i < cls->members->count; i++) {
            FcStmt* member = (FcStmt*)cls->members->items[i];
            if (member->kind == SK_FieldDecl) {
                FcFieldDecl* wd = (FcFieldDecl*)member;
                int code = wd->arrayRanks->count == 0 ? (wd->type ? fcTrWidthCode(wd->type) : 0) : (wd->type ? fcTrArrayWidthCode(wd->type, wd->arrayRanks->count) : 0);
                const char* demand = wd->type ? fcUnitDemandOf(c, wd->type) : NULL;
                void* known;
                if ((code & 0x10000) != 0 && fcDictTry(t->fieldUnits, wd->name, &known) && !streq0((const char*)known, demand)) code = -1;
                if ((code & 0x10000) != 0) fcDictPut(t->fieldUnits, wd->name, (void*)demand);
                if (fcDictTry(t->fieldWidths, wd->name, &known) && FC_VALI(known) != code) fcDictPut(t->fieldWidths, wd->name, FC_IVAL(-1));
                else fcDictPut(t->fieldWidths, wd->name, FC_IVAL(code));
            }
            if (member->kind == SK_MethodDecl) {
                FcMethodDecl* md = (FcMethodDecl*)member;
                fcRpRegister(c, t->methods, md->name, md->params);
                fcSetAdd(t->methodNames, md->name);
            } else if (member->kind == SK_FieldDecl) {
                FcFieldDecl* fd = (FcFieldDecl*)member;
                if (fd->arrayRanks->count == 0 && (fd->type == NULL || fd->type->lambdaSignature != NULL || streq0(fd->type->baseName, "lambda") || streq0(fd->type->baseName, "any")))
                    fcSetAdd(t->lambdaFieldNames, fd->name);
            } else if (member->kind == SK_PropertyDecl) {
                FcPropertyDecl* pd = (FcPropertyDecl*)member;
                if (pd->type == NULL || pd->type->lambdaSignature != NULL || streq0(pd->type->baseName, "lambda") || streq0(pd->type->baseName, "any"))
                    fcSetAdd(t->lambdaFieldNames, pd->name);
            } else if (member->kind == SK_ConstructorDecl) fcRpRegister(c, t->constructors, cls->name, ((FcConstructorDecl*)member)->params);
        }
    }
    return t;
}
static FcBoolMask* fcRpAnyTrue(FcBoolMask* m) { int i; if (!m) return NULL; for (i = 0; i < m->count; i++) if (m->items[i]) return m; return NULL; }
static FcBoolMask* fcRpForMethod(FcComp* cm, const char* name, int argc) { return fcRpAnyTrue((FcBoolMask*)fcDictGet(cm->sh->refParams->methods, fcRpKey(CC_, name, argc))); }
static FcBoolMask* fcRpForConstructor(FcComp* cm, const char* cls, int argc) { return fcRpAnyTrue((FcBoolMask*)fcDictGet(cm->sh->refParams->constructors, fcRpKey(CC_, cls, argc))); }
static int fcRpFieldWidth(FcComp* cm, const char* name) { void* v; return fcDictTry(cm->sh->refParams->fieldWidths, name, &v) && FC_VALI(v) > 0 ? FC_VALI(v) : 0; }
static const char* fcRpFieldUnit(FcComp* cm, const char* name) { return (const char*)fcDictGet(cm->sh->refParams->fieldUnits, name); }
static bool fcRpIsLambdaField(FcComp* cm, const char* name) { return fcDictHas(cm->sh->refParams->lambdaFieldNames, name) && !fcDictHas(cm->sh->refParams->methodNames, name); }
static bool fcRpIsTypeParam(FcComp* cm, const char* name) { return fcDictHas(cm->sh->refParams->typeParamNames, name); }

/* Reflection: the program imports `reflection` */
static bool Reflection(FcComp* cm) { return fcDictHas(cm->sh->natives->index, "__refl_members"); }

/* ---- the declared types ---- */
static FcTypeRef* fcTrNew(FcCtx* c, const char* name) {
    FcTypeRef* t = (FcTypeRef*)fcAlloc(c, sizeof *t);
    t->baseName = name;
    t->bitWidth = FC_NULL_INT;
    return t;
}
static FcTypeRef* fcTrCopy(FcCtx* c, const FcTypeRef* t) { FcTypeRef* r = (FcTypeRef*)fcAlloc(c, sizeof *r); *r = *t; return r; }

static bool fcKnownClassOrInterface(void* self, const char* n) {
    FcComp* cm = (FcComp*)self;
    return fcDictHas(cm->sh->knownClassNames, n) || fcDictHas(cm->sh->refParams->interfaceNames, n);
}
static bool fcKnownClass(void* self, const char* n) { return fcDictHas(((FcComp*)self)->sh->knownClassNames, n); }
static bool fcKnownInterface(void* self, const char* n) { return fcDictHas(((FcComp*)self)->sh->refParams->interfaceNames, n); }
static FcKnown fcKnownOf(FcComp* cm, bool (*fn)(void*, const char*)) { FcKnown k; k.fn = fn; k.self = cm; return k; }

static int fcTypeParamIndex(FcList* tps, const char* name) {
    int i;
    if (!tps) return -1;
    for (i = 0; i < tps->count; i++) if (streq0(((FcTypeParam*)tps->items[i])->name, name)) return i;
    return -1;
}

static const char* MapTypeRef(FcComp* cm, const char* text);
static const char* MapSpec(FcComp* cm, const char* spec);
static const char* TypeArgText(FcComp* cm, const FcTypeRef* tr, FcRtClass* owner, FcClassDecl* target, int index);
static const char* DeclaredTypeOfCore(FcComp* cm, const FcTypeRef* type, FcRtClass* owner);

/* DeclaredTypeOf */
static const char* DeclaredTypeOf(FcComp* cm, const FcTypeRef* type, FcRtClass* owner) {
    const char* text = MapSpec(cm, DeclaredTypeOfCore(cm, type, owner));
    FcEnumType* declaredEnum;
    if (text == NULL && type && type->pointerDepth == 0 && type->arrayRank == 0 && type->lambdaSignature == NULL && !fcTrIsInferred(type)
        && (fcDictHas(cm->sh->refParams->enumNames, type->baseName) || fcDictHas(cm->sh->enumTypes, type->baseName))
        && (declaredEnum = EnumTypeNamed(cm, type->baseName)) != NULL) text = fcDtOfName(CC_, declaredEnum->name);
    if (type && type->nonNull && type->pointerDepth == 0 && type->arrayRank == 0 && type->lambdaSignature == NULL && fcDkOf(type, NULL) == 0) {
        if (text == NULL && streq0(type->baseName, "string")) text = fcDtOfName(CC_, "string");
        if (text != NULL) text = fcDtNonNullOf(CC_, text);
    }
    return text;
}

static const FcU* ZeroUnitOf(FcComp* cm, const FcTypeRef* type) {
    const char* unit = fcTrRequiredUnit(type);
    return unit && !fcStartsWith(unit, "any") ? fcUParse(CC_, unit) : NULL;
}

/* DefinedZeroOf */
static FcConst* DefinedZeroOf(FcComp* cm, const FcTypeRef* type) {
    int kind;
    FcEnumType* enumType;
    if (!(type && type->nonNull && type->pointerDepth == 0 && type->arrayRank == 0 && type->lambdaSignature == NULL)) return NULL;
    if ((kind = fcDkOf(type, NULL)) != 0) return fcDkZeroOf(CC_, kind, ZeroUnitOf(cm, type));
    if (streq0(type->baseName, "string")) return fcMakeString(CC_, "");
    if (fcDictHas(cm->sh->refParams->enumNames, type->baseName) && (enumType = EnumTypeNamed(cm, type->baseName)) != NULL && enumType->names->count > 0)
        return fcMakeEnum(CC_, *(int64_t*)enumType->values->items[0], enumType);
    return NULL;
}

static void EmitWidthIfNeeded(FcComp* target, int widthCode, const char* unit);
static int ClassArrayCode(FcComp* cm, const FcTypeRef* type, int rank, FcRtClass* owner, const char** text);

static void EmitVariableTyping(FcComp* cm, int width, const char* unit, FcTypeRef* arrayElem, int arrayRank) {
    const char* text = NULL;
    int code;
    if (arrayElem != NULL && (code = ClassArrayCode(cm, arrayElem, arrayRank, cm->enclosingClass, &text)) != 0) EmitWidthIfNeeded(cm, code, text);
    else EmitWidthIfNeeded(cm, width, unit);
}

static int ClassArrayCode(FcComp* cm, const FcTypeRef* type, int rank, FcRtClass* owner, const char** text) {
    *text = NULL;
    if (rank <= 0 || rank >= 256 || !type || type->pointerDepth != 0 || type->arrayRank != 0 || fcTrIsInferred(type) || fcTrArrayWidthCode(type, rank) != 0) return 0;
    if (type->lambdaSignature) {
        const FcLambdaSignature* sig = type->lambdaSignature;
        if (sig->isSelector) return 0;
        *text = sig->isExpression ? DeclaredTypeOf(cm, fcTrNew(CC_, "LambdaExpression"), owner) : fcDtOfLambda(CC_, sig->paramTypeNames ? sig->paramTypeNames->count : 0);
    } else if (streq0(type->baseName, "string")) *text = fcDtOfName(CC_, "string");
    else {
        FcEnumType* enumType;
        if (fcDictHas(cm->sh->refParams->enumNames, type->baseName) && (enumType = EnumTypeNamed(cm, type->baseName)) != NULL)
            *text = fcFmt(CC_, "%s\x02%s", fcDtOfName(CC_, enumType->name), enumType->key);
        else if (!streq0(type->baseName, "any") && !streq0(type->baseName, "class") && !streq0(type->baseName, "var") && !streq0(type->baseName, "undefined")) {
            FcTypeRef* plain = fcTrCopy(CC_, type);
            const char* declared;
            plain->nonNull = false;
            declared = DeclaredTypeOf(cm, plain, owner);
            if (declared && fcDtTypeParamIndex(CC_, declared, NULL) < 0) *text = fcDtWithoutTypeArgs(CC_, declared);
        }
    }
    if (*text == NULL) return 0;
    return 0x10000 | (rank << 8) | AE_Class | (type->nonNull ? AE_DefinedFlag : 0);
}

typedef struct FcResolveCtx { FcComp* cm; const FcTypeRef* type; } FcResolveCtx;
static const char* fcResolveForTypeParam(void* self, const char* n) {
    FcResolveCtx* r = (FcResolveCtx*)self;
    FcTypeRef* t = fcTrCopy(r->cm->c, r->type);
    t->baseName = n; t->typeArgs = NULL; t->typeArgCount = 0;
    return fcTrResolveBaseName(r->cm->c, t, fcKnownOf(r->cm, fcKnownClassOrInterface));
}

static const char* DeclaredTypeOfCore(FcComp* cm, const FcTypeRef* type, FcRtClass* owner) {
    FcResolveCtx rctx;
    int m, k;
    const char* name;
    FcKnown known;
    if (!type || type->pointerDepth != 0 || type->arrayRank != 0 || type->lambdaSignature != NULL || fcTrIsInferred(type) || cm->sh->knownClassNames == NULL) return NULL;
    known = fcKnownOf(cm, fcKnownClassOrInterface);
    rctx.cm = cm; rctx.type = type;
    if ((m = fcTypeParamIndex(cm->methodTypeParams, type->baseName)) >= 0)
        return fcDtOfTypeParam(CC_, (FcTypeParam*)cm->methodTypeParams->items[m], m, "", fcResolveForTypeParam, &rctx);
    if (owner && owner->decl && (k = fcTypeParamIndex(owner->decl->typeParams, type->baseName)) >= 0)
        return fcDtOfTypeParam(CC_, (FcTypeParam*)owner->decl->typeParams->items[k], k, owner->name, fcResolveForTypeParam, &rctx);
    if (fcRpIsTypeParam(cm, type->baseName)) return NULL;
    name = type->typeArgs && type->typeArgs->count > 0 ? fcResolveNewTarget(CC_, type, type->typeArgs->count, known) : fcTrResolveBaseName(CC_, type, known);
    if (!fcKnows(known, name)) return NULL;
    if (type->typeArgs && type->typeArgs->count > 0 && fcDictHas(cm->sh->knownClassNames, name)) {
        FcList* texts = fcListNew(CC_);
        bool any = false;
        int i;
        for (i = 0; i < type->typeArgs->count; i++) {
            const char* t = MapTypeRef(cm, TypeArgText(cm, (const FcTypeRef*)type->typeArgs->items[i], owner, NULL, i));
            if (t[0]) any = true;
            fcListAdd(CC_, texts, (void*)t);
        }
        if (any) return fcDtWithTypeArgs(CC_, fcDtOfName(CC_, name), texts);
    }
    return fcDtOfName(CC_, name);
}

static bool fcTpHasIsIn(FcTypeParam* tp) {
    int g, k;
    for (g = 0; g < tp->constraintGroups->count; g++) {
        FcTypeConstraintGroup* grp = (FcTypeConstraintGroup*)tp->constraintGroups->items[g];
        for (k = 0; k < grp->constraints->count; k++) if (((FcTypeConstraint*)grp->constraints->items[k])->kind == TCK_IsIn) return true;
    }
    return false;
}

static const char* TypeArgText(FcComp* cm, const FcTypeRef* tr, FcRtClass* owner, FcClassDecl* target, int index) {
    int m, k;
    const char* name;
    FcKnown known;
    if (tr->lambdaSignature != NULL || tr->arrayRank > 0 || tr->pointerDepth > 0 || fcTrIsInferred(tr) || cm->sh->knownClassNames == NULL) return "";
    if ((m = fcTypeParamIndex(cm->methodTypeParams, tr->baseName)) >= 0) return fcFmt(CC_, "$%d@", m);
    if (owner && owner->decl && (k = fcTypeParamIndex(owner->decl->typeParams, tr->baseName)) >= 0) return fcFmt(CC_, "$%d@%s", k, owner->name);
    if (fcRpIsTypeParam(cm, tr->baseName)) return "";
    if (streq0(tr->baseName, "int") || streq0(tr->baseName, "float")) return tr->unit == NULL ? tr->baseName : fcFmt(CC_, "%s:%s", tr->baseName, tr->unit);
    if (streq0(tr->baseName, "bool") || streq0(tr->baseName, "char") || streq0(tr->baseName, "string")) return tr->baseName;
    if (streq0(tr->baseName, "any") || streq0(tr->baseName, "class")) return "";
    if (fcDictHas(cm->sh->refParams->enumNames, tr->baseName)) { FcEnumType* et = EnumTypeNamed(cm, tr->baseName); return et ? et->name : ""; }
    known = fcKnownOf(cm, fcKnownClassOrInterface);
    name = tr->typeArgs && tr->typeArgs->count > 0 ? fcResolveNewTarget(CC_, tr, tr->typeArgs->count, known) : fcTrResolveBaseName(CC_, tr, known);
    if (fcKnows(known, name)) return name;
    if (target && target->typeParams && index < target->typeParams->count && fcTpHasIsIn((FcTypeParam*)target->typeParams->items[index]))
        return fcFmt(CC_, "in:%s", tr->baseName);
    return "";
}

static const char* MapTypeRef(FcComp* cm, const char* text) {
    int i;
    if (cm->lambdaTypeRefs == NULL || text[0] != '$') return text;
    for (i = 0; i < cm->lambdaTypeRefs->count; i++) if (streq0((const char*)cm->lambdaTypeRefs->items[i], text)) break;
    if (i == cm->lambdaTypeRefs->count) fcListAdd(CC_, cm->lambdaTypeRefs, (void*)text);
    return fcFmt(CC_, "$%d@", i);
}
static const char* MapTypeRefs(FcComp* cm, FcList* texts) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = 0; i < texts->count; i++) { if (i) fcBufChar(&b, '|'); fcBufStr(&b, MapTypeRef(cm, (const char*)texts->items[i])); }
    return fcBufDone(CC_, &b);
}
static const char* MapSpec(FcComp* cm, const char* spec) {
    const char* cls = NULL;
    int index;
    const char* mapped;
    const char* start;
    const char* end;
    if (spec == NULL || cm->lambdaTypeRefs == NULL || (index = fcDtTypeParamIndex(CC_, spec, &cls)) < 0) return spec;
    mapped = MapTypeRef(cm, fcFmt(CC_, "$%d@%s", index, cls));
    start = strchr(spec, '\x01');
    end = strchr(spec, '\x03');
    return fcFmt(CC_, "%.*s@%.*s@%s", (int)(start - spec + 1), spec, (int)strlen(mapped) - 2, mapped + 1, end);
}

static void EmitOverloadHint(FcComp* cm, FcExpr* call);
static const char* ResolveNewClassName(FcComp* cm, FcNewExpr* ne);

/* EmitTypeArgs */
static void EmitTypeArgs(FcComp* cm, FcNewExpr* ne) {
    FcClassDecl* target;
    FcList* texts;
    bool any = false;
    int i;
    EmitOverloadHint(cm, (FcExpr*)ne);
    if (!ne->classRef->typeArgs || ne->classRef->typeArgs->count == 0) return;
    target = (FcClassDecl*)fcDictGet(cm->sh->refParams->classDecls, ResolveNewClassName(cm, ne));
    texts = fcListNew(CC_);
    for (i = 0; i < ne->classRef->typeArgs->count; i++) {
        const char* t = MapTypeRef(cm, TypeArgText(cm, (const FcTypeRef*)ne->classRef->typeArgs->items[i], cm->enclosingClass, target, i));
        if (t[0]) any = true;
        fcListAdd(CC_, texts, (void*)t);
    }
    if (!any) return;
    {
        FcBuf b = {0};
        fcBufStr(&b, "");
        for (i = 0; i < texts->count; i++) { if (i) fcBufChar(&b, '|'); fcBufStr(&b, (const char*)texts->items[i]); }
        E_op(cm, OP_SetTypeArgs);
        E_u16(cm, E_str(cm, fcBufDone(CC_, &b)));
        E_u8(cm, 0);
    }
}

static const char* ResolveTypeRef(FcComp* cm, const FcTypeRef* tr) {
    if (cm->sh->knownClassNames == NULL) return tr->baseName;
    if (tr->typeArgs && tr->typeArgs->count > 0) return fcResolveNewTarget(CC_, tr, tr->typeArgs->count, fcKnownOf(cm, fcKnownClass));
    return fcTrResolveBaseName(CC_, tr, fcKnownOf(cm, fcKnownClass));
}
static const char* ResolveCatchType(FcComp* cm, const FcTypeRef* tr) {
    const char* name = ResolveTypeRef(cm, tr);
    const char* iface;
    if (cm->sh->knownClassNames == NULL || fcDictHas(cm->sh->knownClassNames, name) || (tr->typeArgs && tr->typeArgs->count > 0)) return name;
    iface = fcTrResolveBaseName(CC_, tr, fcKnownOf(cm, fcKnownInterface));
    return fcDictHas(cm->sh->refParams->interfaceNames, iface) ? iface : name;
}
static const char* ResolveNewClassName(FcComp* cm, FcNewExpr* ne) {
    if (cm->sh->knownClassNames != NULL) return fcResolveNewTarget(CC_, ne->classRef, ne->typeArgs ? ne->typeArgs->count : 0, fcKnownOf(cm, fcKnownClass));
    return ne->classRef->baseName;
}

/* ---- Compile ---- */
static void CompileStmt(FcComp* cm, FcStmt* stmt);
static void CompileExpr(FcComp* cm, FcExpr* expr);
static void EmitLoadConst(FcComp* cm, FcConst* v);
static FcDict* CompileClasses(FcComp* cm, FcList* program);
static FcList* StaticInitOrder(FcComp* cm, FcDict* classes, FcDict* deps);

typedef struct FcStaticInit { FcRtClass* cls; const char* field; FcProto* init; } FcStaticInit;

static FcCompiled* fcCompile(FcCtx* c, FcList* program, FcResolveResult* rr, FcNatives* natives, bool classMeta) {
    FcCompShared* sh = (FcCompShared*)fcAlloc(c, sizeof *sh);
    FcComp* compiler;
    FcDict* classes;
    FcCompiled* compiled;
    FcList* order;
    int i, k, globalJobs = GJM_Safepoints;
    FcStmt* endWait = NULL;
    sh->c = c;
    sh->rr = rr;
    sh->refs = rr->references;
    sh->natives = natives;
    sh->errors = fcListNew(c);
    sh->refParams = fcRefParamsBuild(c, rr->classes);
    sh->enumTypes = fcDictNew(c, FC_KEY_STR);
    sh->classMeta = classMeta;
    sh->globalSlotCount = rr->noShadowGlobals ? 0 : rr->globalSlotCount;
    sh->rawTreeLambdas = fcDictNew(c, FC_KEY_PTR);
    sh->rawTreeValues = fcDictNew(c, FC_KEY_PTR);
    sh->bitsOf = rr->bitsOf;
    sh->noOverloadHints = getenv("FIRE_NO_OVERLOAD_HINTS") && strcmp(getenv("FIRE_NO_OVERLOAD_HINTS"), "1") == 0;
    sh->anchorAtRunTime = getenv("FIRE_ANCHOR_AT_RUNTIME") && strcmp(getenv("FIRE_ANCHOR_AT_RUNTIME"), "1") == 0;
    compiler = fcCompNew(sh, NULL);
    compiler->isTopLevelCode = true;
    classes = CompileClasses(compiler, program);

    for (i = 0; i < program->count; i++) if (((FcStmt*)program->items[i])->kind == SK_GlobalJobsDirective) globalJobs = ((FcGlobalJobsDirective*)program->items[i])->mode;
    if (globalJobs != GJM_Safepoints) { fcChunkOp(c, compiler->chunk, OP_SetAutoSync); fcChunkByte(c, compiler->chunk, globalJobs); }
    for (i = 0; i < program->count; i++) {
        FcStmt* s = (FcStmt*)program->items[i];
        if (s->kind != SK_TimeoutDirective) continue;
        CompileExpr(compiler, ((FcTimeoutDirective*)s)->value);
        fcChunkOp(c, compiler->chunk, OP_SetTimeout);
    }
    for (i = 0; i < program->count; i++) if (((FcStmt*)program->items[i])->kind == SK_EndWaitDirective) endWait = (FcStmt*)program->items[i];
    if (endWait) { CompileExpr(compiler, ((FcEndWaitDirective*)endWait)->value); fcChunkOp(c, compiler->chunk, OP_SetEndWait); }

    order = StaticInitOrder(compiler, classes, rr->staticInitDeps);
    for (i = 0; i < order->count; i++) {
        FcStaticInit* si = (FcStaticInit*)order->items[i];
        int protoIdx = fcChunkAddProto(c, compiler->chunk, si->init);
        EmitLoadConst(compiler, fcMakeUndefined(c));
        fcChunkOp(c, compiler->chunk, OP_CallProtoWithThis);
        fcChunkU16(c, compiler->chunk, protoIdx);
        fcChunkByte(c, compiler->chunk, 0);
        EmitWidthIfNeeded(compiler, fcRpFieldWidth(compiler, si->field), fcRpFieldUnit(compiler, si->field));
        fcChunkOp(c, compiler->chunk, OP_SetStaticFieldOnInit);
        fcChunkU16(c, compiler->chunk, E_str(compiler, si->cls->name));
        fcChunkU16(c, compiler->chunk, E_str(compiler, si->field));
    }

    for (i = 0; i < program->count; i++) {
        FcStmt* stmt = (FcStmt*)program->items[i];
        compiler->topLevelSourceIndex = stmt->source;
        CompileStmt(compiler, stmt);
    }
    fcChunkOp(c, compiler->chunk, OP_Halt);

    if (sh->errors->count > 0) {
        FcList* list = fcListNew(c);
        for (i = 0; i < sh->errors->count; i++) {
            FcCompError* e = (FcCompError*)sh->errors->items[i];
            FcError* x = (FcError*)fcAlloc(c, sizeof *x);
            x->kind = FC_ERR_COMPILE; x->message = e->message; x->line = e->line; x->source = e->source;
            fcListAdd(c, list, x);
        }
        c->err.kind = FC_ERR_COMPILE;
        c->err.line = ((FcCompError*)sh->errors->items[0])->line;
        c->err.column = 0;
        c->err.file = NULL;
        c->err.message = ((FcCompError*)sh->errors->items[0])->message;
        c->err.source = -1;
        c->err.list = list;
        fcRethrow(c);
    }

    compiled = (FcCompiled*)fcAlloc(c, sizeof *compiled);
    compiled->topLevel = compiler->chunk;
    compiled->classes = classes;
    compiled->externNames = fcListNew(c);
    compiled->externSigs = fcListNew(c);
    FC_DICT_EACH(rr->externs, e) {
        FcExternDecl* ed = (FcExternDecl*)e->value;
        FcExternSig* sig = (FcExternSig*)fcAlloc(c, sizeof *sig);
        sig->libName = ed->libName;
        sig->paramTypes = fcListNew(c);
        for (k = 0; k < ed->params->count; k++) {
            FcLambdaParam* p = (FcLambdaParam*)ed->params->items[k];
            FcTypeRef* t = p->type;
            if (ed->inlineC != NULL && p->type != NULL && p->arrayRanks->count > 0) { t = fcTrCopy(c, p->type); t->arrayRank = p->arrayRanks->count; }
            fcListAdd(c, sig->paramTypes, t);
        }
        sig->returnType = ed->returnType;
        sig->inlineC = ed->inlineC;
        if (ed->inlineC != NULL) { sig->paramNames = fcListNew(c); for (k = 0; k < ed->params->count; k++) fcListAdd(c, sig->paramNames, (void*)((FcLambdaParam*)ed->params->items[k])->name); }
        sig->inlineSource = ed->h.source;
        sig->inlineLine = ed->inlineLine;
        sig->displayName = ed->displayName;
        fcListAdd(c, compiled->externNames, (void*)e->key);
        fcListAdd(c, compiled->externSigs, sig);
    }
    compiled->enums = fcListNew(c);
    FC_DICT_EACH(sh->enumTypes, e) fcListAdd(c, compiled->enums, e->value);
    compiled->resources = fcListNew(c);
    compiled->customUnits = fcListNew(c);
    return compiled;
}

/* StaticInitOrder */
static FcList* StaticInitOrder(FcComp* cm, FcDict* classes, FcDict* deps) {
    FcCtx* c = CC_;
    FcList* all = fcListNew(c);
    FcList* order = fcListNew(c);
    FcDict* position;
    int* waitingFor;
    FcList** dependents;
    bool* done;
    bool* ready;
    int i, k, n;
    FC_DICT_EACH(classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        for (i = 0; i < rc->staticFieldNames->count; i++) {
            FcStaticInit* si = (FcStaticInit*)fcAlloc(c, sizeof *si);
            si->cls = rc; si->field = (const char*)rc->staticFieldNames->items[i]; si->init = (FcProto*)rc->staticFieldInits->items[i];
            fcListAdd(c, all, si);
        }
    }
    if (!deps || fcDictCount(deps) == 0) return all;
    n = all->count;
    position = fcDictNew(c, FC_KEY_STR);
    for (i = 0; i < n; i++) { FcStaticInit* si = (FcStaticInit*)all->items[i]; fcDictAdd(position, fcFmt(c, "%s\x01%s", si->cls->name, si->field), FC_IVAL(i)); }
    waitingFor = (int*)fcAlloc(c, sizeof(int) * (size_t)(n + 1));
    dependents = (FcList**)fcAlloc(c, sizeof(FcList*) * (size_t)(n + 1));
    done = (bool*)fcAlloc(c, (size_t)(n + 1));
    ready = (bool*)fcAlloc(c, (size_t)(n + 1));
    memset(waitingFor, 0, sizeof(int) * (size_t)(n + 1));
    memset(dependents, 0, sizeof(FcList*) * (size_t)(n + 1));
    memset(done, 0, (size_t)(n + 1));
    memset(ready, 0, (size_t)(n + 1));
    for (i = 0; i < n; i++) {
        FcStaticInit* si = (FcStaticInit*)all->items[i];
        FcList* needs = (FcList*)fcDictGet(deps, fcFmt(c, "%s\x01%s", si->cls->name, si->field));
        FcDict* distinct;
        if (!needs) continue;
        distinct = fcDictNew(c, FC_KEY_STR);
        for (k = 0; k < needs->count; k++) {
            void* jv;
            int j;
            if (!fcSetAdd(distinct, needs->items[k])) continue;
            if (fcDictTry(position, needs->items[k], &jv) && (j = FC_VALI(jv)) != i) {
                waitingFor[i]++;
                if (!dependents[j]) dependents[j] = fcListNew(c);
                fcListAdd(c, dependents[j], FC_IVAL(i));
            }
        }
    }
    for (i = 0; i < n; i++) if (waitingFor[i] == 0) ready[i] = true;
    while (order->count < n) {
        int next = -1;
        for (i = 0; i < n; i++) if (ready[i]) { next = i; break; }
        if (next < 0) for (i = 0; i < n; i++) if (!done[i]) { next = i; break; }
        ready[next] = false;
        if (done[next]) continue;
        done[next] = true;
        fcListAdd(c, order, all->items[next]);
        if (dependents[next])
            for (k = 0; k < dependents[next]->count; k++) {
                int d = FC_VALI(dependents[next]->items[k]);
                if (--waitingFor[d] == 0 && !done[d]) ready[d] = true;
            }
    }
    FC_DICT_EACH(classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        if (rc->staticFieldNames->count < 2) continue;
        rc->staticFieldNames = fcListNew(c);
        rc->staticFieldInits = fcListNew(c);
        for (i = 0; i < order->count; i++) {
            FcStaticInit* si = (FcStaticInit*)order->items[i];
            if (si->cls != rc) continue;
            fcListAdd(c, rc->staticFieldNames, (void*)si->field);
            fcListAdd(c, rc->staticFieldInits, si->init);
        }
    }
    return order;
}

/* ---- classes ---- */
/* ConstantJson: the JSON text of an attribute argument the compiler knows, NULL for none */
static void fcJsonEsc(FcBuf* b, const char* s);
static const char* fcJsonDouble(FcCtx* c, double d);
static const char* ConstantJson(FcComp* cm, FcExpr* expr) {
    FcCtx* c = CC_;
    if (expr->kind == EK_LiteralExpr) {
        const FcValue* v = &((FcLiteralExpr*)expr)->value;
        bool unitless = v->unitv ? fcUIsUnitless(v->unitv) : v->unit == NULL;
        if (v->kind == VK_Int && unitless) return fcFmt(c, "{\"i\":%" PRId64 "}", v->i);
        if (v->kind == VK_Float && unitless) return fcFmt(c, "{\"f\":%s}", fcJsonDouble(c, v->f));
        if (v->kind == VK_String) { FcBuf b = {0}; fcBufStr(&b, "{\"s\":"); fcJsonEsc(&b, v->s); fcBufChar(&b, '}'); return fcBufDone(c, &b); }
        if (v->kind == VK_Bool) return v->b ? "{\"b\":true}" : "{\"b\":false}";
        return NULL;
    }
    if (expr->kind == EK_UnaryExpr && ((FcUnaryExpr*)expr)->op == UOP_Negate && ((FcUnaryExpr*)expr)->operand->kind == EK_LiteralExpr) {
        const FcValue* v = &((FcLiteralExpr*)((FcUnaryExpr*)expr)->operand)->value;
        bool unitless = v->unitv ? fcUIsUnitless(v->unitv) : v->unit == NULL;
        if (unitless && v->kind == VK_Int) return fcFmt(c, "{\"i\":%" PRId64 "}", -v->i);
        if (unitless && v->kind == VK_Float) return fcFmt(c, "{\"f\":%s}", fcJsonDouble(c, -v->f));
        return NULL;
    }
    if (expr->kind == EK_ArrayLiteralExpr) {
        FcList* el = ((FcArrayLiteralExpr*)expr)->elements;
        FcBuf b = {0};
        int i;
        fcBufStr(&b, "{\"a\":[");
        for (i = 0; i < el->count; i++) {
            const char* item = ConstantJson(cm, (FcExpr*)el->items[i]);
            if (!item) { fcBufFree(&b); return NULL; }
            if (i) fcBufChar(&b, ',');
            fcBufStr(&b, item);
        }
        fcBufStr(&b, "]}");
        return fcBufDone(c, &b);
    }
    if (expr->kind == EK_RangeExpr) {
        FcRangeExpr* range = (FcRangeExpr*)expr;
        const char* from = ConstantJson(cm, range->from);
        const char* to = ConstantJson(cm, range->to);
        int64_t a, b, nn;
        FcBuf o = {0};
        if (!from || !to || strncmp(from, "{\"i\":", 5) != 0 || strncmp(to, "{\"i\":", 5) != 0) return NULL;
        a = strtoll(from + 5, NULL, 10); b = strtoll(to + 5, NULL, 10);
        if ((b - a < 0 ? a - b : b - a) > 10000) return NULL;
        fcBufStr(&o, "{\"a\":[");
        for (nn = a; a <= b ? nn <= b : nn >= b; nn += a <= b ? 1 : -1) { if (nn != a) fcBufChar(&o, ','); fcBufFmt(&o, "{\"i\":%" PRId64 "}", nn); }
        fcBufStr(&o, "]}");
        return fcBufDone(c, &o);
    }
    return NULL;
}

/* TypeText */
static const char* TypeText(FcComp* cm, const FcTypeRef* type, int extraArrayRank) {
    FcBuf b = {0};
    int i;
    if (type == NULL || fcTrIsInferred(type)) return "";
    if (type->lambdaSignature && type->lambdaSignature->isSelector) {
        const FcLambdaSignature* sel = type->lambdaSignature;
        fcBufFmt(&b, "lambda %s<", sel->selectorKind);
        for (i = 0; sel->paramTypeNames && i < sel->paramTypeNames->count; i++) { if (i) fcBufStr(&b, ", "); fcBufStr(&b, (const char*)sel->paramTypeNames->items[i]); }
        fcBufChar(&b, '>');
    } else { fcBufStr(&b, ""); fcTrText(&b, type); }
    for (i = 0; i < type->arrayRank + extraArrayRank; i++) fcBufStr(&b, "[]");
    return fcBufDone(CC_, &b);
}
static const char* AccessText(int access) { return access == ACC_Private ? "private" : access == ACC_Protected ? "protected" : "public"; }

static bool fcKnownEnum(void* self, const char* n) { return EnumTypeNamed((FcComp*)self, n) != NULL; }

/* JsonTypeText */
static const char* JsonTypeText(FcComp* cm, const FcTypeRef* type, int extraArrayRank, FcClassDecl* owner) {
    FcCtx* c = CC_;
    FcBuf ab = {0};
    const char* arrays;
    const char* b;
    const char* name;
    FcEnumType* enumType;
    bool isClass, isInterface;
    int i, k;
    if (type == NULL || fcTrIsInferred(type) || type->lambdaSignature != NULL || type->pointerDepth > 0) return "";
    fcBufStr(&ab, "");
    for (i = 0; i < type->arrayRank + extraArrayRank; i++) fcBufStr(&ab, "[]");
    arrays = fcBufDone(c, &ab);
    b = streq0(type->baseName, "int") && type->bitWidth == 8 && type->isUnsigned ? "byte" : type->baseName;
    if (type->typeArgs == NULL && owner && owner->typeParams && (k = fcTypeParamIndex(owner->typeParams, b)) >= 0) return fcFmt(c, "$%d%s", k, arrays);
    {
        static const char* const plain[] = { "int", "float", "bool", "char", "string", "byte", "word", "class", "int8", "int16", "int32", "int64", "uint8", "uint16",
            "uint32", "uint64", "float32", "float64" };
        for (i = 0; i < (int)(sizeof plain / sizeof plain[0]); i++) if (streq0(b, plain[i])) return fcFmt(c, "%s%s", b, arrays);
    }
    if (streq0(b, "any")) return arrays[0] ? fcFmt(c, "any%s", arrays) : "";
    if (type->typeArgs == NULL && (enumType = EnumTypeNamed(cm, fcTrResolveBaseName(c, type, fcKnownOf(cm, fcKnownEnum)))) != NULL) return fcFmt(c, "%s%s", enumType->name, arrays);
    if (cm->sh->knownClassNames == NULL) return "";
    name = ResolveTypeRef(cm, type);
    isClass = fcDictHas(cm->sh->knownClassNames, name);
    isInterface = !isClass && fcDictHas(cm->sh->refParams->interfaceNames, fcTrResolveBaseName(c, type, fcKnownOf(cm, fcKnownInterface)));
    if (!isClass && !isInterface) return arrays[0] ? fcFmt(c, "any%s", arrays) : "";
    if (isInterface) name = fcTrResolveBaseName(c, type, fcKnownOf(cm, fcKnownInterface));
    if (type->typeArgs && type->typeArgs->count > 0) {
        FcBuf nb = {0};
        fcBufStr(&nb, fcPlainName(c, name));
        fcBufChar(&nb, '<');
        for (i = 0; i < type->typeArgs->count; i++) { if (i) fcBufChar(&nb, ','); fcBufStr(&nb, JsonTypeText(cm, (const FcTypeRef*)type->typeArgs->items[i], 0, owner)); }
        fcBufChar(&nb, '>');
        name = fcBufDone(c, &nb);
    }
    return fcFmt(c, "%s%s", name, arrays);
}

static FcAttributeMeta* FlagAttribute(FcComp* cm, FcAttributeDecl* a);
/* AttributeMetas */
static FcList* AttributeMetas(FcComp* cm, FcList* attributes) {
    FcList* r;
    int i, k;
    if (!attributes || attributes->count == 0) return NULL;
    r = fcListNew(CC_);
    for (i = 0; i < attributes->count; i++) {
        FcAttributeDecl* a = (FcAttributeDecl*)attributes->items[i];
        FcAttributeMeta* m = FlagAttribute(cm, a);
        if (!m) {
            FcBuf b = {0};
            m = (FcAttributeMeta*)fcAlloc(CC_, sizeof *m);
            m->name = a->name;
            m->argTexts = fcListCopy(CC_, a->argTexts);
            fcBufChar(&b, '[');
            for (k = 0; k < a->args->count; k++) {
                const char* j = ConstantJson(cm, (FcExpr*)a->args->items[k]);
                if (k) fcBufChar(&b, ',');
                fcBufStr(&b, j ? j : "null");
            }
            fcBufChar(&b, ']');
            m->argsJson = fcBufDone(CC_, &b);
        }
        fcListAdd(CC_, r, m);
    }
    return r;
}
static const char* DottedText(FcComp* cm, FcExpr* e) {
    if (e->kind == EK_IdentifierExpr) return ((FcIdentifierExpr*)e)->name;
    if (e->kind == EK_MemberExpr) {
        const char* left = DottedText(cm, ((FcMemberExpr*)e)->target);
        return left ? fcFmt(CC_, "%s.%s", left, ((FcMemberExpr*)e)->name) : NULL;
    }
    return NULL;
}
static bool fcEnumMatches(const FcEnumType* et, const char* name) {
    size_t n = strlen(et->name), m = strlen(name);
    return streq0(et->name, name) || (n > m && et->name[n - m - 1] == '.' && strcmp(et->name + n - m, name) == 0);
}
static FcAttributeMeta* FlagAttribute(FcComp* cm, FcAttributeDecl* a) {
    FcEnumType* type = EnumTypeNamed(cm, a->name);
    int64_t value = 0;
    int i, k;
    FcAttributeMeta* m;
    if (!type || !type->isFlags) return NULL;
    for (i = 0; i < a->args->count; i++) {
        FcExpr* arg = (FcExpr*)a->args->items[i];
        const char* flag = NULL;
        int found = -1;
        if (arg->kind == EK_IdentifierExpr) flag = ((FcIdentifierExpr*)arg)->name;
        else if (arg->kind == EK_MemberExpr) {
            FcMemberExpr* me = (FcMemberExpr*)arg;
            const char* dotted;
            if ((me->target->kind == EK_IdentifierExpr || me->target->kind == EK_MemberExpr) && fcEnumMatches(type, (dotted = DottedText(cm, me->target)) ? dotted : "")) flag = me->name;
        }
        for (k = 0; flag && k < type->names->count; k++) if (streq0((const char*)type->names->items[k], flag)) { found = k; break; }
        if (flag == NULL || found < 0)
            fcCompError(cm, a->line, fcFmt(CC_, "'%s' is not a flag of '%s' (attribute '[%s ...]')", (const char*)a->argTexts->items[i], type->name, a->name));
        value |= *(int64_t*)type->values->items[found];
    }
    m = (FcAttributeMeta*)fcAlloc(CC_, sizeof *m);
    m->name = type->name;
    m->argTexts = fcListCopy(CC_, a->argTexts);
    m->argsJson = fcFmt(CC_, "[{\"i\":%" PRId64 "}]", value);
    return m;
}

/* InterfaceClosure */
typedef struct FcIfaceKnown { FcDict* interfaces; } FcIfaceKnown;
static bool fcKnownIface(void* self, const char* n) { return fcDictHas(((FcIfaceKnown*)self)->interfaces, n); }
static FcList* CompilerInterfaceClosure(FcCtx* c, const char* name, FcDict* interfaces) {
    FcList* result = fcListNew(c);
    FcList* pending = fcListNew(c);
    FcIfaceKnown ik;
    FcKnown known;
    ik.interfaces = interfaces;
    known.fn = fcKnownIface; known.self = &ik;
    fcListAdd(c, pending, (void*)name);
    while (pending->count > 0) {
        const char* n = (const char*)pending->items[--pending->count];
        FcInterfaceDecl* id;
        int i;
        if (fcListHasStr(result, n)) continue;
        fcListAdd(c, result, (void*)n);
        if (!(id = (FcInterfaceDecl*)fcDictGet(interfaces, n))) continue;
        if (!id->baseRefs) continue;
        for (i = id->baseRefs->count - 1; i >= 0; i--) {
            FcTypeRef* b = (FcTypeRef*)id->baseRefs->items[i];
            fcListAdd(c, pending, (void*)(b->typeArgCount == 0 ? fcTrResolveBaseName(c, b, known) : fcResolveNewTarget(c, b, b->typeArgCount, known)));
        }
    }
    return result;
}

static void fcMetaAddParams(FcComp* cm, FcMemberMeta* m, FcList* parms) {
    int i;
    for (i = 0; i < parms->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parms->items[i];
        fcListAdd(CC_, m->paramNames, (void*)p->name);
        fcListAdd(CC_, m->paramTypes, (void*)TypeText(cm, p->type, p->arrayRanks->count));
    }
}
static FcList* fcOrEmpty(FcComp* cm, FcList* l) { return l ? l : fcListNew(CC_); }

static FcClassMeta* BuildClassMeta(FcComp* cm, FcClassDecl* cd) {
    FcClassMeta* meta = (FcClassMeta*)fcAlloc(CC_, sizeof *meta);
    int i;
    meta->members = fcListNew(CC_); meta->baseNames = fcListNew(CC_); meta->declaredInterfaces = fcListNew(CC_);
    meta->attributes = fcOrEmpty(cm, AttributeMetas(cm, cd->attributes));
    if (cd->baseRefs) for (i = 0; i < cd->baseRefs->count; i++) fcListAdd(CC_, meta->baseNames, (void*)((FcTypeRef*)cd->baseRefs->items[i])->baseName);
    for (i = 0; i < cd->members->count; i++) {
        FcStmt* member = (FcStmt*)cd->members->items[i];
        FcMemberMeta* m;
        switch (member->kind) {
            case SK_FieldDecl: {
                FcFieldDecl* f = (FcFieldDecl*)member;
                const char* unit = f->type ? fcTrRequiredUnit(f->type) : NULL;
                m = fcMemberMetaNew(CC_);
                m->name = f->name; m->kind = "field"; m->typeName = TypeText(cm, f->type, f->arrayRanks->count); m->access = AccessText(f->access);
                m->isStatic = f->isStatic; m->isReadonly = f->isReadonly; m->unit = unit ? unit : "";
                m->attributes = fcOrEmpty(cm, AttributeMetas(cm, f->attributes));
                fcListAdd(CC_, meta->members, m);
                break;
            }
            case SK_PropertyDecl: {
                FcPropertyDecl* pd = (FcPropertyDecl*)member;
                const char* unit = pd->type ? fcTrRequiredUnit(pd->type) : NULL;
                m = fcMemberMetaNew(CC_);
                m->name = pd->name; m->kind = "property"; m->typeName = TypeText(cm, pd->type, 0); m->access = AccessText(pd->access);
                m->isStatic = pd->isStatic; m->canRead = pd->getter != NULL; m->canWrite = pd->setter != NULL; m->unit = unit ? unit : "";
                m->attributes = fcOrEmpty(cm, AttributeMetas(cm, pd->attributes));
                fcListAdd(CC_, meta->members, m);
                break;
            }
            case SK_MethodDecl: {
                FcMethodDecl* md = (FcMethodDecl*)member;
                m = fcMemberMetaNew(CC_);
                m->name = md->name; m->kind = "method"; m->typeName = TypeText(cm, md->returnType, 0); m->access = AccessText(md->access); m->isStatic = md->isStatic;
                fcMetaAddParams(cm, m, md->params);
                m->attributes = fcOrEmpty(cm, AttributeMetas(cm, md->attributes));
                fcListAdd(CC_, meta->members, m);
                break;
            }
            case SK_ConstructorDecl: {
                FcConstructorDecl* ctor = (FcConstructorDecl*)member;
                m = fcMemberMetaNew(CC_);
                m->name = cd->name; m->kind = "constructor"; m->access = AccessText(ctor->access);
                fcMetaAddParams(cm, m, ctor->params);
                fcListAdd(CC_, meta->members, m);
                break;
            }
            default: break;
        }
    }
    return meta;
}

static bool fcHasAttribute(FcList* attributes, const char* name) {
    int i;
    if (!attributes) return false;
    for (i = 0; i < attributes->count; i++) if (streq0(((FcAttributeDecl*)attributes->items[i])->name, name)) return true;
    return false;
}
static void MarkKeepAndSerializable(FcCtx* c, FcRtClass* rc, FcClassDecl* cd) {
    int i;
    rc->keep = fcHasAttribute(cd->attributes, "Keep") || fcHasAttribute(cd->attributes, "Remote") || fcHasAttribute(cd->attributes, "Page") || fcHasAttribute(cd->attributes, "Table")
        || fcHasAttribute(cd->attributes, "Driver") || fcHasAttribute(cd->attributes, "Migration");
    if (fcHasAttribute(cd->attributes, "NotSerializable")) rc->notSerializable = true;
    rc->noCopy = fcHasAttribute(cd->attributes, "NoCopy");
    for (i = 0; i < cd->members->count; i++) {
        FcStmt* member = (FcStmt*)cd->members->items[i];
        const char* name = NULL;
        FcList* attributes = NULL;
        if (member->kind == SK_FieldDecl) { name = ((FcFieldDecl*)member)->name; attributes = ((FcFieldDecl*)member)->attributes; }
        else if (member->kind == SK_PropertyDecl) { name = ((FcPropertyDecl*)member)->name; attributes = ((FcPropertyDecl*)member)->attributes; }
        else if (member->kind == SK_MethodDecl) { name = ((FcMethodDecl*)member)->name; attributes = ((FcMethodDecl*)member)->attributes; }
        if (name && (fcHasAttribute(attributes, "Keep") || fcHasAttribute(attributes, "Remote"))) {
            if (!rc->keptMembers) rc->keptMembers = fcListNew(c);
            fcListAdd(c, rc->keptMembers, (void*)name);
        }
    }
}

static FcEnumType* fcEnumTypeOf(FcCtx* c, FcDict* interned, FcEnumDecl* ed) {
    FcList* names = fcListNew(c);
    FcList* values = fcListNew(c);
    FcDict* seen = fcDictNew(c, FC_KEY_STR);
    int64_t next = 0;
    int i;
    const char* key;
    FcEnumType* t;
    for (i = 0; i < ed->members->count; i++) {
        FcEnumMember* m = (FcEnumMember*)ed->members->items[i];
        int64_t value = m->valueExpr && m->valueExpr->kind == EK_LiteralExpr && ((FcLiteralExpr*)m->valueExpr)->value.kind == VK_Int ? ((FcLiteralExpr*)m->valueExpr)->value.i : next;
        next = value + 1;
        if (fcSetAdd(seen, m->name)) {
            int64_t* boxed = (int64_t*)fcAlloc(c, sizeof *boxed);
            *boxed = value;
            fcListAdd(c, names, (void*)m->name);
            fcListAdd(c, values, boxed);
        }
    }
    key = fcEnumKeyOf(c, ed->name, ed->isFlags, names, values);
    if ((t = (FcEnumType*)fcDictGet(interned, key)) != NULL) return t;
    t = (FcEnumType*)fcAlloc(c, sizeof *t);
    t->name = ed->name; t->isFlags = ed->isFlags; t->names = names; t->values = values; t->key = key;
    fcDictPut(interned, key, t);
    return t;
}

static void CompileClassBody(FcComp* cm, FcRtClass* rc);
static bool fcIsReflectionHelper(const char* n) {
    return streq0(n, "Reflect") || streq0(n, "Type") || streq0(n, "Member") || streq0(n, "Selector") || streq0(n, "Attribute");
}

typedef struct FcClassKnown { FcDict* classes; FcDict* interfaceNames; } FcClassKnown;
static bool fcKnownBase(void* self, const char* n) {
    FcClassKnown* k = (FcClassKnown*)self;
    return streq0(n, "Exception") || fcDictHas(k->classes, n) || fcDictHas(k->interfaceNames, n);
}

static FcDict* CompileClasses(FcComp* cm, FcList* program) {
    FcCtx* c = CC_;
    FcDict* classes = fcDictNew(c, FC_KEY_STR);
    FcDict* interfaceNames = fcDictNew(c, FC_KEY_STR);
    FcDict* interfaceDecls = fcDictNew(c, FC_KEY_STR);
    FcList* baseRefOfClass = fcListNew(c);
    FcList* baseRefOfRef = fcListNew(c);
    FcDict* interned = fcDictNew(c, FC_KEY_STR);
    FcClassKnown ck;
    FcKnown known;
    int i, k;
    for (i = 0; i < program->count; i++)
        if (((FcStmt*)program->items[i])->kind == SK_EnumDecl) {
            FcEnumDecl* ed = (FcEnumDecl*)program->items[i];
            fcDictPut(cm->sh->enumTypes, ed->name, fcEnumTypeOf(c, interned, ed));
        }
    for (i = 0; i < program->count; i++) {
        FcStmt* stmt = (FcStmt*)program->items[i];
        FcClassDecl* cd;
        FcRtClass* rc;
        if (stmt->kind != SK_ClassDecl) continue;
        cd = (FcClassDecl*)stmt;
        rc = fcRtClassNew(c, cd->name, cd);
        fcDictPut(classes, cd->name, rc);
        MarkKeepAndSerializable(c, rc, cd);
        if (Reflection(cm) || cm->sh->classMeta) {
            rc->meta = BuildClassMeta(cm, cd);
            rc->isReflectionHelper = fcIsReflectionHelper(cd->name);
        }
    }
    for (i = 0; i < program->count; i++)
        if (((FcStmt*)program->items[i])->kind == SK_InterfaceDecl) {
            FcInterfaceDecl* id = (FcInterfaceDecl*)program->items[i];
            fcSetAdd(interfaceNames, id->name);
            fcDictAdd(interfaceDecls, id->name, id);
        }
    FC_DICT_EACH(interfaceNames, e) fcSetAdd(cm->sh->refParams->interfaceNames, e->key);
    for (i = 0; i < program->count; i++) if (((FcStmt*)program->items[i])->kind == SK_EnumDecl) fcSetAdd(cm->sh->refParams->enumNames, ((FcEnumDecl*)program->items[i])->name);
    ck.classes = classes; ck.interfaceNames = interfaceNames;
    known.fn = fcKnownBase; known.self = &ck;
    FC_DICT_EACH(classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        FcList* baseRefs = rc->decl->baseRefs;
        if (!baseRefs) continue;
        for (k = 0; k < baseRefs->count; k++) {
            FcTypeRef* baseRef = (FcTypeRef*)baseRefs->items[k];
            const char* n = baseRef->typeArgCount == 0 ? fcTrResolveBaseName(c, baseRef, known) : fcResolveNewTarget(c, baseRef, baseRef->typeArgCount, known);
            if (fcDictHas(interfaceNames, n)) {
                FcList* closure = CompilerInterfaceClosure(c, n, interfaceDecls);
                int j;
                for (j = 0; j < closure->count; j++) if (!fcListHasStr(rc->interfaces, (const char*)closure->items[j])) fcListAdd(c, rc->interfaces, closure->items[j]);
                if (rc->meta && !fcListHasStr(rc->meta->declaredInterfaces, n)) fcListAdd(c, rc->meta->declaredInterfaces, (void*)n);
                continue;
            }
            if (!streq0(n, "Exception") && !fcDictHas(classes, n)) continue;
            if (streq0(n, "Exception") && !fcDictHas(classes, n)) continue;
            if (rc->base != NULL) continue;
            rc->base = (FcRtClass*)fcDictGet(classes, n);
            fcListAdd(c, baseRefOfClass, rc);
            fcListAdd(c, baseRefOfRef, baseRef);
        }
    }
    FC_DICT_EACH(classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        FcDict* set = fcDictNew(c, FC_KEY_PTR);
        bool isActor = false;
        FcRtClass* walk;
        for (walk = rc; walk != NULL; walk = walk->base) {
            if (walk->decl->isActor) { isActor = true; break; }
            if (fcDictHas(set, walk)) { fcCompAddError(cm, "Circular class inheritance!", rc->decl->h.line, rc->decl->h.source); break; }
            fcSetAdd(set, walk);
        }
        rc->isActor = isActor;
        rc->isRecord = rc->decl->isRecord;
    }
    FC_DICT_EACH(classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        bool baseIsRecord;
        if (rc->base == NULL) continue;
        baseIsRecord = rc->base->decl ? rc->base->decl->isRecord : rc->base->isRecord;
        if (rc->decl->isRecord && !baseIsRecord)
            fcCompAddError(cm, fcFmt(c, "The record '%s' cannot derive from the class '%s': a record derives only from a record.", rc->name, rc->base->name), rc->decl->h.line, rc->decl->h.source);
        else if (!rc->decl->isRecord && baseIsRecord)
            fcCompAddError(cm, fcFmt(c, "The class '%s' cannot derive from the record '%s': only a record derives from a record.", rc->name, rc->base->name), rc->decl->h.line, rc->decl->h.source);
    }
    cm->sh->knownClassNames = fcDictNew(c, FC_KEY_STR);
    FC_DICT_EACH(classes, e) fcSetAdd(cm->sh->knownClassNames, e->key);
    for (i = 0; i < baseRefOfClass->count; i++) {
        FcRtClass* rc = (FcRtClass*)baseRefOfClass->items[i];
        FcTypeRef* baseRef = (FcTypeRef*)baseRefOfRef->items[i];
        if (baseRef->typeArgs && baseRef->typeArgs->count > 0) {
            rc->baseTypeArgs = fcListNew(c);
            for (k = 0; k < baseRef->typeArgs->count; k++) {
                const char* text = TypeArgText(cm, (const FcTypeRef*)baseRef->typeArgs->items[k], rc, rc->base->decl, k);
                if (text[0] == '$') text = fcStrN(c, text, (size_t)(strchr(text, '@') - text));
                fcListAdd(c, rc->baseTypeArgs, (void*)text);
            }
        }
    }
    FC_DICT_EACH(classes, e) CompileClassBody(cm, (FcRtClass*)e->value);
    return classes;
}

/* ComputeThisReads / CollectThisReads */
typedef struct FcThisReads { FcComp* cm; FcList* reads; FcDict* seen; } FcThisReads;
static void fcThisReadsVisit(void* self, FcNodeTag tag, void* node) {
    FcThisReads* t = (FcThisReads*)self;
    FcExpr* e;
    FcResolvedRef* r;
    if (tag != FN_EXPR) return;
    e = (FcExpr*)node;
    if (e->kind == EK_MemberExpr && ((FcMemberExpr*)e)->target->kind == EK_ThisExpr) {
        if (fcSetAdd(t->seen, ((FcMemberExpr*)e)->name)) fcListAdd(t->cm->c, t->reads, (void*)((FcMemberExpr*)e)->name);
    } else if (e->kind == EK_IdentifierExpr && (r = (FcResolvedRef*)fcDictGet(t->cm->sh->refs, e)) != NULL && r->kind == RR_ImplicitThisMember) {
        if (fcSetAdd(t->seen, ((FcIdentifierExpr*)e)->name)) fcListAdd(t->cm->c, t->reads, (void*)((FcIdentifierExpr*)e)->name);
    }
}
static void fcThisReadsAdd(FcComp* cm, FcDict* result, const char* key, FcBlockStmt* body) {
    FcThisReads t;
    FcList* known = (FcList*)fcDictGet(result, key);
    int i;
    t.cm = cm; t.reads = fcListNew(CC_); t.seen = fcDictNew(CC_, FC_KEY_STR);
    if (known) for (i = 0; i < known->count; i++) if (fcSetAdd(t.seen, known->items[i])) fcListAdd(CC_, t.reads, known->items[i]);
    fcWalkStmt((FcStmt*)body, fcThisReadsVisit, &t);
    fcDictPut(result, key, t.reads);
}
static FcDict* ComputeThisReads(FcComp* cm, FcClassDecl* decl) {
    FcDict* result = fcDictNew(CC_, FC_KEY_STR);
    int i;
    for (i = 0; i < decl->members->count; i++) {
        FcStmt* member = (FcStmt*)decl->members->items[i];
        if (member->kind == SK_MethodDecl && !((FcMethodDecl*)member)->isStatic) fcThisReadsAdd(cm, result, ((FcMethodDecl*)member)->name, ((FcMethodDecl*)member)->body);
        else if (member->kind == SK_PropertyDecl && !((FcPropertyDecl*)member)->isStatic && ((FcPropertyDecl*)member)->getter)
            fcThisReadsAdd(cm, result, fcFmt(CC_, "get_%s", ((FcPropertyDecl*)member)->name), ((FcPropertyDecl*)member)->getter);
    }
    return result;
}

static FcProto* CompileFieldInitProto(FcComp* cm, FcRtClass* rc, FcTypeRef* type, FcExpr* initializer, bool isStatic, int line, FcList* arrayRanks);
static FcProto* CompileMethodProto(FcComp* cm, FcRtClass* rc, FcList* parms, FcBlockStmt* body, int access, bool isStatic, const char* returnUnit, FcList* typeParams,
    int accessor, FcTypeRef* returnType, const char* name);
static FcProto* CompileConstructorProto(FcComp* cm, FcRtClass* rc, FcConstructorDecl* ctor, int access);

static void fcRtAddMethod(FcCtx* c, FcRtClass* rc, const char* name, FcProto* proto) {
    FcList* overloads = (FcList*)fcDictGet(rc->methods, name);
    int i;
    if (!overloads) { overloads = fcListNew(c); fcDictPut(rc->methods, name, overloads); }
    for (i = 0; i < overloads->count; i++)
        if (fcSameSignature((FcProto*)overloads->items[i], proto))
            fcThrow(c, FC_ERR_NOTSUPPORTED, 0, 0, NULL, fcFmt(c, "Internal error: method '%s' with %d parameters was registered twice.", name, proto->paramCount));
    fcListAdd(c, overloads, proto);
}
static void fcRtAddConstructor(FcCtx* c, FcRtClass* rc, FcProto* proto) {
    int i;
    for (i = 0; i < rc->ctorArities->count; i++) if (FC_VALI(rc->ctorArities->items[i]) == proto->paramCount) break;
    if (i == rc->ctorArities->count) { fcListAdd(c, rc->ctorArities, FC_IVAL(proto->paramCount)); fcListAdd(c, rc->ctorProtos, proto); return; }
    {
        bool same = fcSameSignature((FcProto*)rc->ctorProtos->items[i], proto);
        int k;
        for (k = 0; !same && k < rc->extraConstructors->count; k++) same = fcSameSignature((FcProto*)rc->extraConstructors->items[k], proto);
        if (same) fcThrow(c, FC_ERR_NOTSUPPORTED, 0, 0, NULL, fcFmt(c, "Internal error: a constructor with %d parameters was registered twice.", proto->paramCount));
    }
    fcListAdd(c, rc->extraConstructors, proto);
}

/* one member of CompileClassBody (a restart point of its own) */
typedef struct FcPendingSpec { FcMethodDecl* decl; FcProto* proto; } FcPendingSpec;
static void fcCompileMember(FcComp* cm, FcRtClass* rc, FcStmt* member, FcList* ctorDecls, FcList* pendingSpecs) {
    FcCtx* c = CC_;
    switch (member->kind) {
        case SK_FieldDecl: {
            FcFieldDecl* declaredField = (FcFieldDecl*)member;
            FcFieldDecl* fd = declaredField;
            FcProto* fieldInit;
            FcFieldInfo* info;
            int arrayCode, classArray;
            const char* classArrayText = NULL;
            const char* unused = NULL;
            if (cm->sh->rr->derivedDefinedFields && fcDictHas(cm->sh->rr->derivedDefinedFields, declaredField)) {
                fd = (FcFieldDecl*)fcNodeDup(c, declaredField, sizeof *declaredField);
                fd->type = fcTrCopy(c, declaredField->type);
                fd->type->nonNull = true;
            }
            fieldInit = CompileFieldInitProto(cm, rc, fd->type, fd->initializer, fd->isStatic, fd->h.line, fd->arrayRanks);
            info = (FcFieldInfo*)fcAlloc(c, sizeof *info);
            info->access = fd->access;
            info->requiredUnit = fd->type ? fcUnitDemandOf(c, fd->type) : NULL;
            info->requiredKind = fd->arrayRanks->count == 0 ? fcDkOfStore(fd->type, fd->initializer) : 0;
            info->requiredType = fd->arrayRanks->count == 0 ? DeclaredTypeOf(cm, fd->type, rc) : NULL;
            arrayCode = fd->type ? fcTrArrayWidthCode(fd->type, fd->arrayRanks->count) : 0;
            if (arrayCode != 0 && (arrayCode & 0xFF) != AE_U8 && (arrayCode & 0xFF) != AE_Word) info->requiredArray = arrayCode & 0xFFFF;
            else if ((classArray = ClassArrayCode(cm, fd->type, fd->arrayRanks->count, rc, &unused)) != 0) info->requiredArray = classArray & 0xFFFF;
            else info->requiredArray = 0;
            info->requiredArrayType = ClassArrayCode(cm, fd->type, fd->arrayRanks->count, rc, &classArrayText) != 0 ? classArrayText : NULL;
            info->derivedDefined = fd != declaredField;
            if (!fd->isStatic && fd->arrayRanks->count == 1 && fd->type && fd->type->arrayRank == 0 && fd->type->pointerDepth == 0 && fd->type->typeArgs == NULL && fd->type->unit == NULL) {
                const char* arrayParam = TypeArgText(cm, fd->type, rc, NULL, 0);
                if (arrayParam[0] == '$' && !fcEndsWith(arrayParam, "@")) info->arrayOfParam = arrayParam;
            }
            info->isStatic = fd->isStatic;
            info->typeText = JsonTypeText(cm, fd->type, fd->arrayRanks->count, rc->decl);
            info->attributes = AttributeMetas(cm, fd->attributes);
            fcDictPut(rc->ownFieldInfo, fd->name, info);
            if (fd->isStatic) {
                fcListAdd(c, rc->staticFieldNames, (void*)fd->name);
                fcListAdd(c, rc->staticFieldInits, fieldInit);
                if (fd->isReadonly) { if (!rc->readonlyStatics) rc->readonlyStatics = fcDictNew(c, FC_KEY_STR); fcSetAdd(rc->readonlyStatics, fd->name); }
            } else { fcListAdd(c, rc->fieldNames, (void*)fd->name); fcListAdd(c, rc->fieldInits, fieldInit); }
            break;
        }
        case SK_MethodDecl: {
            FcMethodDecl* md = (FcMethodDecl*)member;
            FcProto* methodProto = CompileMethodProto(cm, rc, md->params, md->body, md->access, md->isStatic, md->returnType ? fcUnitDemandOf(c, md->returnType) : NULL, md->typeParams, 0,
                md->returnType, md->name);
            FcPendingSpec* ps;
            fcRtAddMethod(c, rc, md->name, methodProto);
            if (md->specialization) { ps = (FcPendingSpec*)fcAlloc(c, sizeof *ps); ps->decl = md; ps->proto = methodProto; fcListAdd(c, pendingSpecs, ps); }
            break;
        }
        case SK_ConstructorDecl: fcListAdd(c, ctorDecls, member); break;
        case SK_DestructorDecl: rc->destructor = CompileMethodProto(cm, rc, fcListNew(c), ((FcDestructorDecl*)member)->body, ACC_Public, false, NULL, NULL, 0, NULL, NULL); break;
        case SK_PropertyDecl: {
            FcPropertyDecl* pd = (FcPropertyDecl*)member;
            if (pd->getter) {
                FcProto* getterProto = CompileMethodProto(cm, rc, fcListNew(c), pd->getter, pd->access, pd->isStatic, pd->type ? fcUnitDemandOf(c, pd->type) : NULL, NULL, 1, pd->type, pd->name);
                if (!pd->isStatic && !(pd->type && fcUnitDemandOf(c, pd->type)) && pd->getter->statements->count == 1) {
                    FcStmt* only = (FcStmt*)pd->getter->statements->items[0];
                    if (only->kind == SK_ReturnStmt && ((FcReturnStmt*)only)->value && ((FcReturnStmt*)only)->value->kind == EK_MemberExpr
                        && ((FcMemberExpr*)((FcReturnStmt*)only)->value)->target->kind == EK_ThisExpr)
                        getterProto->fieldGetterOf = ((FcMemberExpr*)((FcReturnStmt*)only)->value)->name;
                }
                fcRtAddMethod(c, rc, fcFmt(c, "get_%s", pd->name), getterProto);
            }
            if (pd->setter) {
                FcList* setterParams = fcListNew(c);
                FcLambdaParam* v = (FcLambdaParam*)fcAlloc(c, sizeof *v);
                v->name = "value"; v->type = pd->type; v->arrayRanks = fcListNew(c);
                fcListAdd(c, setterParams, v);
                fcRtAddMethod(c, rc, fcFmt(c, "set_%s", pd->name), CompileMethodProto(cm, rc, setterParams, pd->setter, pd->access, pd->isStatic, NULL, NULL, 2, NULL, NULL));
            }
            break;
        }
        default: break;
    }
}
static void fcCompileMemberGuarded(FcComp* cm, FcRtClass* rc, FcStmt* member, FcList* ctorDecls, FcList* pendingSpecs) {
    FcCompShared* sh = cm->sh;
    bool outerHas = sh->hasMemberSource;
    int outerSource = sh->memberSource;
    sh->hasMemberSource = true;
    sh->memberSource = member->source;
    FC_TRY(cm->c) {
        fcCompileMember(cm, rc, member, ctorDecls, pendingSpecs);
    } FC_CATCH(cm->c) {
        if (!fcCompCatchable(cm->c)) { sh->hasMemberSource = outerHas; sh->memberSource = outerSource; fcRethrow(cm->c); }
        fcCompCollect(cm, member->line, rc->decl->h.source);
    } FC_END_TRY;
    sh->hasMemberSource = outerHas;
    sh->memberSource = outerSource;
}
static void fcCompileCtorGuarded(FcComp* cm, FcRtClass* rc, FcConstructorDecl* ctor) {
    FcCompShared* sh = cm->sh;
    bool outerHas = sh->hasMemberSource;
    int outerSource = sh->memberSource;
    sh->hasMemberSource = true;
    sh->memberSource = ctor->h.source;
    FC_TRY(cm->c) {
        fcRtAddConstructor(cm->c, rc, CompileConstructorProto(cm, rc, ctor, ctor->access));
    } FC_CATCH(cm->c) {
        if (!fcCompCatchable(cm->c)) { sh->hasMemberSource = outerHas; sh->memberSource = outerSource; fcRethrow(cm->c); }
        fcCompCollect(cm, ctor->h.line, rc->decl->h.source);
    } FC_END_TRY;
    sh->hasMemberSource = outerHas;
    sh->memberSource = outerSource;
}

static void CompileClassBody(FcComp* cm, FcRtClass* rc) {
    FcCtx* c = CC_;
    FcList* ctorDecls = fcListNew(c);
    FcList* pendingSpecs = fcListNew(c);
    int i, k;
    rc->thisReads = ComputeThisReads(cm, rc->decl);
    for (i = 0; i < rc->decl->members->count; i++) fcCompileMemberGuarded(cm, rc, (FcStmt*)rc->decl->members->items[i], ctorDecls, pendingSpecs);

    for (i = 0; i < pendingSpecs->count; i++) {
        FcPendingSpec* ps = (FcPendingSpec*)pendingSpecs->items[i];
        FcMethodDecl* md = ps->decl;
        FcProto* specProto = ps->proto;
        FcList* specTypes = md->specialization;
        const char* generic = fcStrN(c, md->name, (size_t)(strchr(md->name, '<') - md->name));
        FcList* candidates = (FcList*)fcDictGet(rc->methods, generic);
        FcProto* target = NULL;
        const char* problem = NULL;
        FcList* classParams = rc->decl->typeParams;
        if (candidates)
            for (k = 0; k < candidates->count; k++) { FcProto* p = (FcProto*)candidates->items[k]; if (!p->isStatic && p->paramCount == md->params->count) { target = p; break; } }
        if (!classParams || classParams->count == 0 || classParams->count != specTypes->count)
            problem = fcFmt(c, "'%s' names %d type(s), but class '%s' has %d type parameter(s) - a specialization fixes all of them", md->name, specTypes->count, rc->decl->name,
                rc->decl->typeParams ? rc->decl->typeParams->count : 0);
        else if (!target) problem = fcFmt(c, "'%s' specializes nothing: class '%s' has no method '%s' with %d parameter(s)", md->name, rc->decl->name, generic, md->params->count);
        if (problem) { fcCompAddError(cm, problem, md->h.line, rc->decl->h.source); continue; }
        specProto->specOwner = rc->name;
        specProto->specArgs = fcListNew(c);
        for (k = 0; k < specTypes->count; k++) fcListAdd(c, specProto->specArgs, (void*)((FcTypeRef*)specTypes->items[k])->baseName);
        for (k = 0; k < md->params->count && k < 64; k++)
            if (((FcLambdaParam*)md->params->items[k])->type && ((FcLambdaParam*)md->params->items[k])->type->nonNull) specProto->specDefinedMask |= (int64_t)1 << k;
        specProto->specParamKindCount = md->params->count;
        specProto->specParamKinds = (uint8_t*)fcAlloc(c, (size_t)(md->params->count + 1));
        for (k = 0; k < md->params->count; k++) {
            FcLambdaParam* p = (FcLambdaParam*)md->params->items[k];
            specProto->specParamKinds[k] = (uint8_t)(p->byRef || p->arrayRanks->count > 0 ? 0 : fcDkOf(p->type, p->defaultValue));
        }
        {
            /* Append, then OrderBy the number of `any` among the type arguments (a stable sort) */
            FcList* all = target->specializations ? fcListCopy(c, target->specializations) : fcListNew(c);
            FcList* sorted = fcListNew(c);
            int pass, maxAny = 0;
            fcListAdd(c, all, specProto);
            for (k = 0; k < all->count; k++) {
                FcProto* p = (FcProto*)all->items[k];
                int n = 0, j;
                if (p->specArgs) for (j = 0; j < p->specArgs->count; j++) if (streq0((const char*)p->specArgs->items[j], "any")) n++;
                if (n > maxAny) maxAny = n;
            }
            for (pass = 0; pass <= maxAny; pass++)
                for (k = 0; k < all->count; k++) {
                    FcProto* p = (FcProto*)all->items[k];
                    int n = 0, j;
                    if (p->specArgs) for (j = 0; j < p->specArgs->count; j++) if (streq0((const char*)p->specArgs->items[j], "any")) n++;
                    if (n == pass) fcListAdd(c, sorted, p);
                }
            target->specializations = sorted;
        }
    }

    if (ctorDecls->count == 0) fcRtAddConstructor(c, rc, CompileConstructorProto(cm, rc, NULL, ACC_Public));
    else for (i = 0; i < ctorDecls->count; i++) fcCompileCtorGuarded(cm, rc, (FcConstructorDecl*)ctorDecls->items[i]);
}

/* CompileParamDefaults */
static FcList* CompileParamDefaults(FcComp* cm, FcRtClass* rc, FcList* parms) {
    FcList* defaults = fcListNew(CC_);
    int i;
    for (i = 0; i < parms->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parms->items[i];
        FcComp* inner;
        if (p->defaultValue == NULL) { fcListAdd(CC_, defaults, NULL); continue; }
        inner = fcCompInner(cm, rc);
        CompileExpr(inner, p->defaultValue);
        fcChunkOp(CC_, inner->chunk, OP_Return);
        fcListAdd(CC_, defaults, fcProtoNew(CC_, inner->chunk, 0, ACC_Private, NULL, false));
    }
    return defaults;
}

static bool TryGetConstantInitializer(FcProto* proto, FcConst** constant) {
    FcChunk* k = proto->chunk;
    int idx;
    if (k->len != 4 || k->code[0] != OP_LoadConst || k->code[3] != OP_Return) return false;
    idx = k->code[1] | (k->code[2] << 8);
    if (idx >= k->constants->count) return false;
    *constant = (FcConst*)k->constants->items[idx];
    return true;
}

static bool MayBeUnclaimed(FcExpr* e);
static void CompileExprFor(FcComp* cm, FcExpr* value, int widthCode);
static void EmitClaimToScope(FcComp* cm, int depth, bool unclaimed, bool hoist);
static void EmitCheckLambdaSignatureIfNeeded(FcComp* cm, const FcTypeRef* type);
static void CompileArrayAlloc(FcComp* cm, FcList* ranks, int rankIndex, bool byteBuffer);
static bool IsByteBuffer(const FcTypeRef* type, int rank) { return ((type ? fcTrArrayWidthCode(type, rank) : 0) & 0x100FF) == (0x10000 | AE_U8); }
static bool IsWordBuffer(const FcTypeRef* type, int rank) { return ((type ? fcTrArrayWidthCode(type, rank) : 0) & 0x100FF) == (0x10000 | AE_Word); }

static FcProto* CompileFieldInitProto(FcComp* cm, FcRtClass* rc, FcTypeRef* type, FcExpr* initializer, bool isStatic, int line, FcList* arrayRanks) {
    FcComp* inner = fcCompInner(cm, rc);
    FcConst* fieldZero;
    inner->chunk->ownerClass = rc;
    if (initializer != NULL) {
        if (line > 0) fcChunkMarkLine(CC_, inner->chunk, CurrentSourceIndex(inner), line);
        if (!isStatic && MayBeUnclaimed(initializer)) {
            E_op(inner, OP_LoadThis);
            CompileExpr(inner, initializer);
            E_op(inner, OP_ClaimToObject);
        } else {
            CompileExprFor(inner, initializer, arrayRanks && arrayRanks->count > 0 ? 0 : (type ? fcTrWidthCode(type) : 0));
            if (isStatic) EmitClaimToScope(inner, 0xFFFF, MayBeUnclaimed(initializer), false);
        }
        if (!(arrayRanks && arrayRanks->count > 0)) EmitCheckLambdaSignatureIfNeeded(inner, type);
    } else if (arrayRanks && arrayRanks->count > 0 && arrayRanks->items[0] != NULL) {
        if (line > 0) fcChunkMarkLine(CC_, inner->chunk, CurrentSourceIndex(inner), line);
        if (!isStatic) E_op(inner, OP_LoadThis);
        CompileArrayAlloc(inner, arrayRanks, 0, IsByteBuffer(type, arrayRanks->count));
        if (IsWordBuffer(type, arrayRanks->count)) EmitWidthIfNeeded(inner, fcTrArrayWidthCode(type, arrayRanks->count), NULL);
        if (!isStatic) E_op(inner, OP_ClaimToObject);
        else EmitClaimToScope(inner, 0xFFFF, true, false);
    } else if (!(arrayRanks && arrayRanks->count > 0) && (fieldZero = DefinedZeroOf(inner, type)) != NULL) EmitLoadConst(inner, fieldZero);
    else EmitLoadConst(inner, fcMakeUndefined(CC_));
    E_op(inner, OP_Return);
    return fcProtoNew(CC_, inner->chunk, 0, ACC_Private, NULL, false);
}

static uint32_t RefMaskOf(FcComp* cm, FcList* parms) {
    uint32_t mask = 0;
    int i;
    for (i = 0; i < parms->count; i++)
        if (((FcLambdaParam*)parms->items[i])->byRef) {
            if (i >= 16) fcCompNotSupported(cm, "Only the first 16 parameters of a method can be 'ref'.");
            mask |= 1u << i;
        }
    return mask;
}

static void EmitCheckUnitIfNeeded(FcComp* target, const char* unit);
static void EmitStoreVariable(FcComp* cm, const FcResolvedRef* local);

static void EmitLambdaParamChecks(FcComp* cm, FcComp* inner, FcList* parms) {
    FcCtx* c = CC_;
    int i;
    (void)cm;
    for (i = 0; i < parms->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parms->items[i];
        int code;
        if (!p->byRef && p->arrayRanks->count == 0 && p->type && (code = fcTrWidthCode(p->type)) != 0) {
            E_op(inner, OP_LoadLocal); E_u16(inner, 0); E_u16(inner, i);
            EmitWidthIfNeeded(inner, code, NULL);
            E_op(inner, OP_StoreLocal); E_u16(inner, 0); E_u16(inner, i);
            E_op(inner, OP_Pop);
        }
    }
    for (i = 0; i < parms->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parms->items[i];
        int arrayCode, paramClassArray;
        const char* paramClassText = NULL;
        if (p->byRef || p->arrayRanks->count == 0 || (p->type && fcUnitDemandOf(c, p->type) != NULL)) continue;
        arrayCode = p->type ? fcTrArrayWidthCode(p->type, p->arrayRanks->count) : 0;
        paramClassArray = ClassArrayCode(inner, p->type, p->arrayRanks->count, inner->enclosingClass, &paramClassText);
        if (arrayCode != 0 || paramClassArray != 0) {
            E_op(inner, OP_LoadLocal); E_u16(inner, 0); E_u16(inner, i);
            if (paramClassArray != 0) EmitWidthIfNeeded(inner, paramClassArray, paramClassText);
            else EmitWidthIfNeeded(inner, arrayCode, NULL);
            E_op(inner, OP_StoreLocal); E_u16(inner, 0); E_u16(inner, i);
            E_op(inner, OP_Pop);
        }
    }
    for (i = 0; i < parms->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parms->items[i];
        if (!p->byRef) continue;
        E_op(inner, OP_RequireRefParam);
        E_u16(inner, i);
        E_u16(inner, E_str(inner, p->name));
    }
    for (i = 0; i < parms->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parms->items[i];
        const FcLambdaSignature* sig = p->arrayRanks->count == 0 && p->type ? p->type->lambdaSignature : NULL;
        if (!sig) continue;
        if (sig->isExpression) {
            E_op(inner, OP_LoadLocal); E_u16(inner, 0); E_u16(inner, i);
            EmitLoadConst(inner, fcMakeString(c, p->name));
            E_op(inner, OP_CallStaticMethod);
            E_u16(inner, E_str(inner, "Expression"));
            E_u16(inner, E_str(inner, "__Require"));
            E_u8(inner, 2);
            E_op(inner, OP_Pop);
            continue;
        }
        if (sig->isSelector) {
            E_op(inner, OP_LoadLocal); E_u16(inner, 0); E_u16(inner, i);
            EmitLoadConst(inner, fcMakeString(c, sig->selectorKind));
            E_op(inner, OP_CallStaticMethod);
            E_u16(inner, E_str(inner, "Reflect"));
            E_u16(inner, E_str(inner, "SelectorOf"));
            E_u8(inner, 2);
            EmitClaimToScope(inner, 0, true, false);
            E_op(inner, OP_StoreLocal); E_u16(inner, 0); E_u16(inner, i);
            E_op(inner, OP_Pop);
            continue;
        }
        E_op(inner, OP_LoadLocal); E_u16(inner, 0); E_u16(inner, i);
        if (p->byRef) E_op(inner, OP_PtrRead);
        E_op(inner, OP_CheckLambdaSignature);
        E_u8(inner, sig->paramTypeNames ? sig->paramTypeNames->count : 0);
        E_op(inner, OP_Pop);
    }
    for (i = 0; i < parms->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parms->items[i];
        const char* unit = p->type ? fcUnitDemandOf(c, p->type) : NULL;
        FcResolvedRef local;
        if (unit == NULL) continue;
        E_op(inner, OP_LoadLocal); E_u16(inner, 0); E_u16(inner, i);
        if (p->byRef) E_op(inner, OP_PtrRead);
        EmitCheckUnitIfNeeded(inner, unit);
        memset(&local, 0, sizeof local);
        local.kind = RR_Local; local.depth = 0; local.slot = i; local.byRef = p->byRef;
        EmitStoreVariable(inner, &local);
        E_op(inner, OP_Pop);
    }
    for (i = 0; i < parms->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parms->items[i];
        FcTypeRef* declared = p->type;
        int kind;
        const char* unit;
        bool derived;
        if (cm->sh->rr->derivedDefined && fcDictHas(cm->sh->rr->derivedDefined, p)) { declared = fcTrCopy(c, p->type); declared->nonNull = true; }
        if (p->byRef || p->arrayRanks->count > 0 || (kind = fcDkOfStore(declared, p->defaultValue)) == 0) continue;
        if (fcDkIsString(kind) && cm->sh->rr->derivedStrings && fcDictHas(cm->sh->rr->derivedStrings, p)) continue;
        unit = p->type ? fcUnitDemandOf(c, p->type) : NULL;
        if (!unit && fcDkIsUnitless(kind)) unit = "unitless";
        E_op(inner, OP_GuardLocal);
        E_u16(inner, i);
        E_u8(inner, (((kind & 0x7F) - 1) | (kind & DK_UnitlessBit)) & 0xFF);
        derived = declared != p->type && p->type && !p->type->nonNull;
        E_u16(inner, E_str(inner, derived ? fcFmt(c, "Parameter '%s' (derived as never undefined: every call in the program gives a value)", p->name) : fcFmt(c, "Parameter '%s'", p->name)));
        E_u16(inner, unit == NULL ? 0xFFFF : E_str(inner, unit));
    }
    for (i = 0; i < parms->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parms->items[i];
        const char* cls;
        if (p->byRef || p->arrayRanks->count > 0 || (cls = DeclaredTypeOf(inner, p->type, inner->enclosingClass)) == NULL) continue;
        E_op(inner, OP_GuardLocalType);
        E_u16(inner, i);
        E_u16(inner, E_str(inner, cls));
        E_u16(inner, E_str(inner, fcFmt(c, "Parameter '%s'", p->name)));
    }
}

static void EmitWidthIfNeeded(FcComp* target, int widthCode, const char* unit) {
    if (widthCode == 0) return;
    if ((widthCode & 0x10000) != 0) {
        E_op(target, OP_TypeArray);
        E_u8(target, widthCode & 0xFF);
        E_u8(target, (widthCode >> 8) & 0xFF);
        E_u16(target, unit == NULL ? 0xFFFF : E_str(target, unit));
        return;
    }
    E_op(target, OP_ConvertWidth);
    if (widthCode == FC_WORD_WIDTH_CODE) { E_u8(target, 0); E_u8(target, 8 | 4); return; }
    E_u8(target, widthCode & 0xFF);
    E_u8(target, ((widthCode & 0x100) != 0 ? 1 : 0) | ((widthCode & 0x200) != 0 ? 2 : 0) | 4);
}
static void EmitCheckUnitIfNeeded(FcComp* target, const char* unit) {
    if (unit == NULL) return;
    E_op(target, OP_CheckUnit);
    E_u16(target, E_str(target, unit));
}

static void CompileFireStmt(FcComp* cm, FcFireStmt* fs) {
    FcCtx* c = CC_;
    FcComp* inner = fcCompNew(cm->sh, NULL);
    int slot = cm->sh->globalSlotCount, i, handovers = 0, protoIdx;
    FcProto* proto;
    inner->topLevelSourceIndex = CurrentSourceIndex(cm);
    for (i = 0; i < fs->takingCaptures->count; i++) fcChunkMarkLocalName(c, inner->chunk, 0, slot++, ((FcFireTakingCapture*)fs->takingCaptures->items[i])->varName);
    for (i = 0; i < fs->with->count; i++) fcChunkMarkLocalName(c, inner->chunk, 0, slot++, ((FcFireWith*)fs->with->items[i])->varName);
    for (i = 0; i < fs->body->statements->count; i++) CompileStmt(inner, (FcStmt*)fs->body->statements->items[i]);
    E_op(inner, OP_Halt);
    proto = fcProtoNew(c, inner->chunk, slot, ACC_Private, NULL, false);
    protoIdx = fcChunkAddProto(c, CH_, proto);
    for (i = 0; i < fs->takingCaptures->count; i++) CompileExpr(cm, ((FcFireTakingCapture*)fs->takingCaptures->items[i])->source);
    for (i = 0; i < fs->with->count; i++) CompileExpr(cm, ((FcFireWith*)fs->with->items[i])->source);
    if (fs->with->count > 63) fcCompAddError(cm, "'with' names at most 63 values.", fs->h.line, CurrentSourceIndex(cm));
    if (fs->until && fs->until->count > 0) {
        for (i = 0; i < fs->until->count; i++) CompileExpr(cm, (FcExpr*)fs->until->items[i]);
        E_op(cm, OP_CancelPush);
        E_u8(cm, (fs->until->count | 0x80) & 0xFF);
    }
    E_op(cm, OP_Fire);
    E_u16(cm, protoIdx);
    E_u16(cm, cm->sh->globalSlotCount);
    E_u8(cm, fs->takingCaptures->count & 0xFF);
    E_u8(cm, ((fs->with->count > 0 ? 1 : 0) | (fs->dedicated ? 2 : 0) | ((fs->with->count < 63 ? fs->with->count : 63) << 2)) & 0xFF);
    for (i = 0; i < fs->takingCaptures->count; i++) if (((FcFireTakingCapture*)fs->takingCaptures->items[i])->handover) handovers++;
    E_u8(cm, handovers & 0xFF);
}

static void CompileCatchThreadsDecl(FcComp* cm, FcCatchThreadsDecl* decl) {
    FcCtx* c = CC_;
    FcComp* inner = fcCompNew(cm->sh, NULL);
    int i, protoIdx;
    bool hasType = decl->typeRef != NULL;
    inner->topLevelSourceIndex = CurrentSourceIndex(cm);
    if (decl->varName) fcChunkMarkLocalName(c, inner->chunk, 0, 0, decl->varName);
    for (i = 0; i < decl->body->statements->count; i++) CompileStmt(inner, (FcStmt*)decl->body->statements->items[i]);
    EmitLoadConst(inner, fcMakeUndefined(c));
    E_op(inner, OP_Return);
    protoIdx = fcChunkAddProto(c, CH_, fcProtoNew(c, inner->chunk, decl->varName ? 1 : 0, ACC_Public, NULL, false));
    E_op(cm, OP_RegisterThreadsCatch);
    E_u16(cm, protoIdx);
    E_u8(cm, hasType ? 1 : 0);
    if (hasType) E_u16(cm, E_str(cm, ResolveTypeRef(cm, decl->typeRef)));
}

static void CompileCatchTerminateDecl(FcComp* cm, FcCatchTerminateDecl* decl) {
    FcCtx* c = CC_;
    FcComp* inner = fcCompNew(cm->sh, NULL);
    int i, protoIdx;
    inner->topLevelSourceIndex = CurrentSourceIndex(cm);
    if (decl->varName) fcChunkMarkLocalName(c, inner->chunk, 0, 0, decl->varName);
    for (i = 0; i < decl->body->statements->count; i++) CompileStmt(inner, (FcStmt*)decl->body->statements->items[i]);
    EmitLoadConst(inner, fcMakeUndefined(c));
    E_op(inner, OP_Return);
    protoIdx = fcChunkAddProto(c, CH_, fcProtoNew(c, inner->chunk, decl->varName ? 1 : 0, ACC_Public, NULL, false));
    E_op(cm, OP_RegisterTerminateCatch);
    E_u16(cm, protoIdx);
}

static void EmitReturnGuard(FcComp* cm);
static FcList* OverloadTypesOf(FcComp* cm, FcList* parms, FcRtClass* owner);
static bool fcReadingMethodsOf(FcCtx* c, FcList* parms, FcBlockStmt* body);

static FcProto* CompileMethodProto(FcComp* cm, FcRtClass* rc, FcList* parms, FcBlockStmt* body, int access, bool isStatic, const char* returnUnit, FcList* typeParams,
    int accessor, FcTypeRef* returnType, const char* name) {
    FcCtx* c = CC_;
    FcComp* inner = fcCompInner(cm, rc);
    FcProto* proto;
    int i, returnKind;
    const char* returnSpec;
    inner->accessor = isStatic ? 0 : accessor;
    if (accessor == 2 && !isStatic) inner->setterClaims = SetterClaimsSites(c, body);
    inner->methodTypeParams = typeParams;
    inner->chunk->ownerClass = rc;
    inner->returnUnit = returnUnit;
    if (returnType && returnType->arrayRank > 0 && returnType->pointerDepth == 0 && !fcTrIsInferred(returnType)) {
        inner->returnArray = fcTrCopy(c, returnType);
        inner->returnArray->arrayRank = 0;
        inner->returnArrayRank = returnType->arrayRank;
    }
    if (returnType && !returnType->nonNull && cm->sh->rr->derivedReturns && fcDictHas(cm->sh->rr->derivedReturns, body)) { returnType = fcTrCopy(c, returnType); returnType->nonNull = true; }
    if (returnType && returnType->nonNull && (returnKind = fcDkOf(returnType, NULL)) != 0) { inner->returnKind = returnKind; inner->returnWhat = fcFmt(c, "The return value of '%s'", name); }
    else if (returnType && returnType->nonNull && (returnSpec = DeclaredTypeOf(inner, returnType, inner->enclosingClass)) != NULL && fcDtIsNonNull(returnSpec)) {
        inner->returnSpec = returnSpec;
        inner->returnWhat = fcFmt(c, "The return value of '%s'", name);
    }
    for (i = 0; i < parms->count; i++) fcChunkMarkLocalName(c, inner->chunk, 0, i, ((FcLambdaParam*)parms->items[i])->name);
    if (typeParams && typeParams->count > 0) E_op(inner, OP_TakeCallTypeArgs);
    EmitLambdaParamChecks(cm, inner, parms);
    for (i = 0; i < body->statements->count; i++) CompileStmt(inner, (FcStmt*)body->statements->items[i]);
    EmitLoadConst(inner, fcMakeUndefined(c));
    EmitReturnGuard(inner);
    E_op(inner, OP_Return);
    proto = fcProtoNew(c, inner->chunk, parms->count, access, CompileParamDefaults(cm, rc, parms), isStatic);
    proto->refMask = RefMaskOf(cm, parms);
    proto->paramTypes = OverloadTypesOf(inner, parms, rc);
    proto->typeParamCount = typeParams ? typeParams->count : 0;
    proto->paramNames = fcListNew(c);
    for (i = 0; i < parms->count; i++) fcListAdd(c, proto->paramNames, (void*)((FcLambdaParam*)parms->items[i])->name);
    proto->readsOnly = rc != NULL && !isStatic && accessor != 2 && fcReadingMethodsOf(c, parms, body);
    return proto;
}

static const char* OverloadTypeOf(FcComp* cm, FcLambdaParam* p, FcRtClass* owner);
static FcList* OverloadTypesOf(FcComp* cm, FcList* parms, FcRtClass* owner) {
    FcList* r = fcListNew(CC_);
    int i;
    for (i = 0; i < parms->count; i++) fcListAdd(CC_, r, (void*)OverloadTypeOf(cm, (FcLambdaParam*)parms->items[i], owner));
    return r;
}
static const char* OverloadTypeOf(FcComp* cm, FcLambdaParam* p, FcRtClass* owner) {
    FcTypeRef* t = p->type;
    bool methodParam, classParam;
    const char* name;
    FcKnown known;
    if (p->byRef) return "";
    if (p->arrayRanks->count > 0 || (t && t->arrayRank > 0)) return "k:array";
    if (t == NULL || fcTrIsInferred(t) || t->pointerDepth > 0) return "";
    if (t->lambdaSignature && t->lambdaSignature->isExpression) return "c:LambdaExpression";
    if (t->lambdaSignature != NULL || streq0(t->baseName, "lambda")) return "k:lambda";
    methodParam = fcTypeParamIndex(cm->methodTypeParams, t->baseName) >= 0;
    classParam = owner && owner->decl && fcTypeParamIndex(owner->decl->typeParams, t->baseName) >= 0;
    if (methodParam || classParam) { const char* core = DeclaredTypeOfCore(cm, t, owner); return fcFmt(CC_, "g:%s", core ? core : ""); }
    if (streq0(t->baseName, "int")) return "k:int";
    if (streq0(t->baseName, "float")) return "k:float";
    if (streq0(t->baseName, "bool")) return "k:bool";
    if (streq0(t->baseName, "char")) return "k:char";
    if (streq0(t->baseName, "string")) return "k:string";
    if (streq0(t->baseName, "class")) return "k:class";
    if (streq0(t->baseName, "word")) return "k:word";
    if (streq0(t->baseName, "any")) return "";
    if (fcDictHas(cm->sh->refParams->enumNames, t->baseName)) return "k:int";
    if (cm->sh->knownClassNames == NULL) return "";
    known = fcKnownOf(cm, fcKnownClassOrInterface);
    name = t->typeArgs && t->typeArgs->count > 0 ? fcResolveNewTarget(CC_, t, t->typeArgs->count, known) : fcTrResolveBaseName(CC_, t, known);
    if (fcDictHas(cm->sh->refParams->interfaceNames, name)) return fcFmt(CC_, "i:%s", name);
    if (fcDictHas(cm->sh->knownClassNames, name)) return fcFmt(CC_, "c:%s", name);
    return fcRpIsTypeParam(cm, t->baseName) ? "g:" : "";
}

static uint64_t CompileArgs(FcComp* cm, FcList* args, bool scopeCreating, FcBoolMask* refs);
static void EmitCopyArgsPrefix(FcComp* cm, uint64_t mask);

static FcProto* CompileConstructorProto(FcComp* cm, FcRtClass* rc, FcConstructorDecl* ctor, int access) {
    FcCtx* c = CC_;
    FcComp* inner = fcCompInner(cm, rc);
    int headerLine = ctor ? ctor->h.line : (rc->decl ? rc->decl->h.line : 0);
    bool fieldsMayBeTouched;
    int i;
    FcProto* proto;
    inner->chunk->ownerClass = rc;
    if (headerLine > 0) fcChunkMarkLine(c, inner->chunk, CurrentSourceIndex(inner), headerLine);
    if (ctor) {
        for (i = 0; i < ctor->params->count; i++) fcChunkMarkLocalName(c, inner->chunk, 0, i, ((FcLambdaParam*)ctor->params->items[i])->name);
        EmitLambdaParamChecks(cm, inner, ctor->params);
    }
    if (rc->base != NULL) {
        FcList* baseArgs = ctor ? ctor->baseArgs : NULL;
        uint64_t baseCopyMask = baseArgs ? CompileArgs(inner, baseArgs, true, fcRpForConstructor(cm, rc->base->name, baseArgs->count)) : 0;
        EmitCopyArgsPrefix(inner, baseCopyMask);
        E_op(inner, OP_ConstructBase);
        E_u16(inner, E_str(inner, rc->base->name));
        E_u8(inner, baseArgs ? baseArgs->count : 0);
        E_op(inner, OP_Pop);
    }
    fieldsMayBeTouched = rc->base != NULL;
    for (i = 0; i < rc->fieldNames->count; i++) {
        const char* fieldName = (const char*)rc->fieldNames->items[i];
        FcProto* initProto = (FcProto*)rc->fieldInits->items[i];
        FcConst* constant;
        int protoIdx;
        if (TryGetConstantInitializer(initProto, &constant)) {
            if (!fieldsMayBeTouched && constant->kind == CK_Undefined) continue;
            EmitLoadConst(inner, constant);
            EmitWidthIfNeeded(inner, fcRpFieldWidth(cm, fieldName), fcRpFieldUnit(cm, fieldName));
            E_op(inner, OP_SetFieldOnThis);
            E_u16(inner, E_str(inner, fieldName));
            continue;
        }
        protoIdx = fcChunkAddProto(c, inner->chunk, initProto);
        E_op(inner, OP_LoadThis);
        E_op(inner, OP_CallProtoWithThis);
        E_u16(inner, protoIdx);
        E_u8(inner, 0);
        EmitWidthIfNeeded(inner, fcRpFieldWidth(cm, fieldName), fcRpFieldUnit(cm, fieldName));
        E_op(inner, OP_SetFieldOnThis);
        E_u16(inner, E_str(inner, fieldName));
        fieldsMayBeTouched = true;
    }
    if (ctor) for (i = 0; i < ctor->body->statements->count; i++) CompileStmt(inner, (FcStmt*)ctor->body->statements->items[i]);
    EmitLoadConst(inner, fcMakeUndefined(c));
    E_op(inner, OP_Return);
    proto = fcProtoNew(c, inner->chunk, ctor ? ctor->params->count : 0, access, ctor ? CompileParamDefaults(cm, rc, ctor->params) : fcListNew(c), false);
    proto->refMask = ctor ? RefMaskOf(cm, ctor->params) : 0;
    proto->paramTypes = ctor ? OverloadTypesOf(inner, ctor->params, rc) : NULL;
    proto->paramNames = fcListNew(c);
    if (ctor) for (i = 0; i < ctor->params->count; i++) fcListAdd(c, proto->paramNames, (void*)((FcLambdaParam*)ctor->params->items[i])->name);
    return proto;
}

/* ---- statements ---- */
static void CompileStmtCore(FcComp* cm, FcStmt* stmt);
static void CompileStmt(FcComp* cm, FcStmt* stmt) {
    int scopeDepth = cm->currentScopeDepth;
    int loopCount = cm->loopStack->count;
    FC_TRY(cm->c) {
        CompileStmtCore(cm, stmt);
    } FC_CATCH(cm->c) {
        if (!fcCompCatchable(cm->c)) fcRethrow(cm->c);
        fcCompCollect(cm, stmt->line, CurrentSourceIndex(cm));
        cm->currentScopeDepth = scopeDepth;
        while (cm->loopStack->count > loopCount) cm->loopStack->count--;
    } FC_END_TRY;
}

static void CompileBlockNewScope(FcComp* cm, FcBlockStmt* block);
static void CompileDiscardedExpr(FcComp* cm, FcExpr* expr);
static void EmitReleaseTemps(FcComp* cm, FcExpr* e);
static void CompileIf(FcComp* cm, FcIfStmt* s);
static void CompileWhile(FcComp* cm, FcWhileStmt* s);
static void CompileDoWhile(FcComp* cm, FcDoWhileStmt* s);
static void CompileFor(FcComp* cm, FcForStmt* s);
static void CompileForeach(FcComp* cm, FcForeachStmt* fs);
static void CompileTry(FcComp* cm, FcTryStmt* t);
static void CompileLambda(FcComp* cm, FcLambdaExpr* lambda);

static void CompileStmtCore(FcComp* cm, FcStmt* stmt) {
    FcCtx* c = CC_;
    int i, k;
    fcChunkMarkLine(c, CH_, CurrentSourceIndex(cm), stmt->line);
    switch (stmt->kind) {
        case SK_BlockStmt: CompileBlockNewScope(cm, (FcBlockStmt*)stmt); break;
        case SK_ExprStmt:
            CompileDiscardedExpr(cm, ((FcExprStmt*)stmt)->expression);
            EmitReleaseTemps(cm, ((FcExprStmt*)stmt)->expression);
            break;
        case SK_NoOpStmt: case SK_TypeCheckDirective: case SK_GlobalJobsDirective: case SK_TimeoutDirective: case SK_EndWaitDirective: case SK_NoShadowDirective: break;
        case SK_VarDeclStmt: {
            FcVarDeclStmt* vd = (FcVarDeclStmt*)stmt;
            bool autoArrayAlloc = vd->initializer == NULL && vd->arrayRanks->count > 0 && vd->arrayRanks->items[0] != NULL;
            FcConst* zero;
            const char* classArrayText = NULL;
            int classArrayCode;
            if (vd->initializer != NULL) {
                CompileExprFor(cm, vd->initializer, vd->arrayRanks->count == 0 ? (vd->type ? fcTrWidthCode(vd->type) : 0) : 0);
                if (vd->arrayRanks->count == 0) EmitCheckLambdaSignatureIfNeeded(cm, vd->type);
            } else if (autoArrayAlloc) CompileArrayAlloc(cm, vd->arrayRanks, 0, IsByteBuffer(vd->type, vd->arrayRanks->count));
            else if (vd->arrayRanks->count == 0 && (zero = DefinedZeroOf(cm, vd->type)) != NULL) EmitLoadConst(cm, zero);
            else EmitLoadConst(cm, fcMakeUndefined(c));
            EmitCheckUnitIfNeeded(cm, vd->type ? fcUnitDemandOf(c, vd->type) : NULL);
            if ((classArrayCode = ClassArrayCode(cm, vd->type, vd->arrayRanks->count, cm->enclosingClass, &classArrayText)) != 0) EmitWidthIfNeeded(cm, classArrayCode, classArrayText);
            else EmitWidthIfNeeded(cm, vd->arrayRanks->count == 0 ? (vd->type ? fcTrWidthCode(vd->type) : 0) : (vd->type ? fcTrArrayWidthCode(vd->type, vd->arrayRanks->count) : 0),
                vd->type ? fcUnitDemandOf(c, vd->type) : NULL);
            if (vd->initializer != NULL ? MayBeUnclaimed(vd->initializer) : autoArrayAlloc) EmitClaimToScope(cm, 0, true, false);
            fcChunkMarkDeclaredName(c, CH_, E_here(cm), vd->name);
            E_op(cm, OP_DeclareLocal);
            if (vd->initializer != NULL) EmitReleaseTemps(cm, vd->initializer);
            break;
        }
        case SK_IfStmt: CompileIf(cm, (FcIfStmt*)stmt); break;
        case SK_WhileStmt: CompileWhile(cm, (FcWhileStmt*)stmt); break;
        case SK_DoWhileStmt: CompileDoWhile(cm, (FcDoWhileStmt*)stmt); break;
        case SK_ForStmt: CompileFor(cm, (FcForStmt*)stmt); break;
        case SK_ForeachStmt: CompileForeach(cm, (FcForeachStmt*)stmt); break;
        case SK_ReturnStmt: {
            FcReturnStmt* rs = (FcReturnStmt*)stmt;
            int keepResidents = 0, r, n;
            if (rs->value != NULL) {
                CompileExpr(cm, rs->value);
                if (cm->returnUnit != NULL) EmitCheckUnitIfNeeded(cm, cm->returnUnit);
                if (cm->returnArray) {
                    const char* returnArrayText = NULL;
                    int returnArrayCode;
                    if ((returnArrayCode = ClassArrayCode(cm, cm->returnArray, cm->returnArrayRank, cm->enclosingClass, &returnArrayText)) != 0) EmitWidthIfNeeded(cm, returnArrayCode, returnArrayText);
                    else EmitWidthIfNeeded(cm, fcTrArrayWidthCode(cm->returnArray, cm->returnArrayRank), fcUnitDemandOf(c, cm->returnArray));
                }
            } else EmitLoadConst(cm, fcMakeUndefined(c));
            EmitReturnGuard(cm);
            for (k = cm->tryStack->count - 1; k >= 0; k--)
                if (((FcTryCtx*)cm->tryStack->items[k])->phase == TRY_Catch) {
                    E_op(cm, OP_LoadLocal);
                    E_u16(cm, cm->currentScopeDepth - (((FcTryCtx*)cm->tryStack->items[k])->outerDepth + 1));
                    E_u16(cm, 0);
                    E_op(cm, OP_ClearPendingResume);
                }
            for (k = cm->tryStack->count - 1; k >= 0; k--)
                if (((FcTryCtx*)cm->tryStack->items[k])->stmt->finallyBlock != NULL) { keepResidents = ((FcTryCtx*)cm->tryStack->items[k])->residentsAtStart; break; }
            for (r = cm->residents->count - 1; r >= keepResidents; r--)
                for (n = 0; n < FC_VALI(cm->residents->items[r]); n++) { E_op(cm, OP_Swap); E_op(cm, OP_Pop); }
            E_op(cm, OP_Return);
            break;
        }
        case SK_DeleteStmt:
            CompileExpr(cm, ((FcDeleteStmt*)stmt)->target);
            E_op(cm, OP_Delete);
            EmitReleaseTemps(cm, ((FcDeleteStmt*)stmt)->target);
            break;
        case SK_ThrowStmt:
            CompileExpr(cm, ((FcThrowStmt*)stmt)->value);
            E_op(cm, OP_Throw);
            E_op(cm, OP_Pop);
            break;
        case SK_TryStmt: CompileTry(cm, (FcTryStmt*)stmt); break;
        case SK_BreakStmt: CompileBreakOrContinue(cm, true, ((FcBreakStmt*)stmt)->levels); break;
        case SK_ContinueStmt: CompileBreakOrContinue(cm, false, ((FcContinueStmt*)stmt)->levels); break;
        case SK_FireStmt: CompileFireStmt(cm, (FcFireStmt*)stmt); break;
        case SK_LeaveStmt: E_op(cm, OP_Leave); break;
        case SK_SectionEnterStmt: E_op(cm, OP_SectionEnter); break;
        case SK_SectionExitStmt: E_op(cm, OP_SectionExit); break;
        case SK_SilenceStmt: {
            FcSilenceStmt* silence = (FcSilenceStmt*)stmt;
            CompileExpr(cm, silence->target);
            if (silence->memberForm) {
                E_op(cm, OP_SilenceMember);
                E_u16(cm, silence->member ? E_str(cm, silence->member) : 0);
                E_u8(cm, silence->member == NULL ? 1 : 0);
            } else E_op(cm, OP_SilenceValue);
            break;
        }
        case SK_PostGlobalStmt: {
            FcPostGlobalStmt* pg = (FcPostGlobalStmt*)stmt;
            for (i = 0; i < pg->args->count; i++) CompileExpr(cm, (FcExpr*)pg->args->items[i]);
            CompileLambda(cm, (FcLambdaExpr*)pg->lambda);
            E_op(cm, OP_PostGlobal);
            E_u8(cm, (pg->args->count | (pg->onTarget ? 0x80 : 0)) & 0xFF);
            E_u8(cm, pg->withCount & 0xFF);
            break;
        }
        case SK_TerminateStmt:
            if (((FcTerminateStmt*)stmt)->value) CompileExpr(cm, ((FcTerminateStmt*)stmt)->value);
            else EmitLoadConst(cm, fcMakeUndefined(c));
            E_op(cm, OP_Terminate);
            break;
        case SK_CatchThreadsDecl: CompileCatchThreadsDecl(cm, (FcCatchThreadsDecl*)stmt); break;
        case SK_CatchTerminateDecl: CompileCatchTerminateDecl(cm, (FcCatchTerminateDecl*)stmt); break;
        case SK_ProcessStmt: CompileExpr(cm, ((FcProcessStmt*)stmt)->target); E_op(cm, OP_Process); break;
        case SK_ClassDecl: case SK_InterfaceDecl: case SK_EnumDecl: case SK_ExternDecl: break;
        case SK_ClassExtensionDecl:
            fcCompNotSupported(cm, "Internal error: 'class extends %s' was not merged (the program must be produced by Parser.Parse()/ParseMultiple()).",
                ((FcClassExtensionDecl*)stmt)->targetRef->baseName);
        case SK_UnsafeStmt: CompileBlockNewScope(cm, ((FcUnsafeStmt*)stmt)->body); break;
        default: {
            static const char* const names[] = {
#define FC_SN(n) #n,
                FC_STMT_KINDS(FC_SN)
#undef FC_SN
            };
            fcCompNotSupported(cm, "Statement %s is not supported.", names[stmt->kind]);
        }
    }
}

static void EmitCheckLambdaSignatureIfNeeded(FcComp* cm, const FcTypeRef* type) {
    if (!type || type->lambdaSignature == NULL) return;
    E_op(cm, OP_CheckLambdaSignature);
    E_u8(cm, type->lambdaSignature->paramTypeNames ? type->lambdaSignature->paramTypeNames->count : 0);
}

static void CompileBlockNewScope(FcComp* cm, FcBlockStmt* block) {
    int i;
    EmitEnterScope(cm);
    for (i = 0; i < block->statements->count; i++) CompileStmt(cm, (FcStmt*)block->statements->items[i]);
    EmitExitScope(cm);
}

static void CompileScopedBody(FcComp* cm, FcStmt* body) {
    int i;
    if (body->kind == SK_BlockStmt && ((FcBlockStmt*)body)->statements->count == 0) return;
    EmitEnterScope(cm);
    if (body->kind == SK_BlockStmt) for (i = 0; i < ((FcBlockStmt*)body)->statements->count; i++) CompileStmt(cm, (FcStmt*)((FcBlockStmt*)body)->statements->items[i]);
    else CompileStmt(cm, body);
    EmitExitScope(cm);
}

static int EmitJumpIfFalse(FcComp* cm) {
    static const FcOp pairs[6][2] = { { OP_Lt, OP_JumpIfNotLt }, { OP_LtEq, OP_JumpIfNotLtEq }, { OP_Gt, OP_JumpIfNotGt }, { OP_GtEq, OP_JumpIfNotGtEq },
        { OP_Eq, OP_JumpIfNotEq }, { OP_NotEq, OP_JumpIfNotNotEq } };
    int i, at;
    for (i = 0; i < 6; i++) {
        int fusedAt;
        if (!fcChunkEndsWithOp(CH_, pairs[i][0], 0)) continue;
        fcChunkReplaceLastOp(CH_, pairs[i][1]);
        fusedAt = E_here(cm);
        E_u16(cm, 0);
        return fusedAt;
    }
    E_op(cm, OP_JumpIfFalse);
    at = E_here(cm);
    E_u16(cm, 0);
    return at;
}

static bool TryCompileArithOnVariable(FcComp* cm, FcExpr* expr);
static void CompileReplace(FcComp* cm, FcAssignExpr* a);
static void CompileAssign(FcComp* cm, FcAssignExpr* a);

static void CompileDiscardedExpr(FcComp* cm, FcExpr* expr) {
    if (TryCompileArithOnVariable(cm, expr)) return;
    if (expr->kind == EK_AssignExpr && ((FcAssignExpr*)expr)->replace) { CompileReplace(cm, (FcAssignExpr*)expr); return; }
    if (expr->kind == EK_AssignExpr && ((FcAssignExpr*)expr)->value->kind == EK_UnaryExpr && ((FcUnaryExpr*)((FcAssignExpr*)expr)->value)->op == UOP_TryTake)
        CompileAssign(cm, (FcAssignExpr*)expr);
    else CompileExpr(cm, expr);
    if (fcChunkEndsWithOp(CH_, OP_StoreLocal, 4)) { fcChunkReplaceLastOp(CH_, OP_StoreLocalPop); return; }
    if (fcChunkEndsWithOp(CH_, OP_StoreGlobal, 2)) { fcChunkReplaceLastOp(CH_, OP_StoreGlobalPop); return; }
    E_op(cm, OP_Pop);
}

/* ResolvedRef.Equals (a record: by value) */
static bool fcRefEquals(const FcResolvedRef* a, const FcResolvedRef* b) {
    if (a == b) return true;
    if (!a || !b || a->kind != b->kind) return false;
    switch (a->kind) {
        case RR_Local: return a->depth == b->depth && a->slot == b->slot && streq0(a->requiredUnit, b->requiredUnit) && a->byRef == b->byRef && a->width == b->width
            && a->arrayElem == b->arrayElem && a->arrayRank == b->arrayRank;
        case RR_Global: return a->slot == b->slot && streq0(a->requiredUnit, b->requiredUnit) && a->width == b->width && a->arrayElem == b->arrayElem && a->arrayRank == b->arrayRank;
        case RR_StaticMember: return streq0(a->className, b->className);
        case RR_ImplicitThisMember: return true;
        case RR_Native: case RR_Extern: case RR_TryableNative: case RR_TryTake: return streq0(a->name, b->name);
        case RR_EnumMember: return a->value == b->value && streq0(a->enumType, b->enumType);
        default: return false;
    }
}

static bool TryCompileArithOnVariable(FcComp* cm, FcExpr* expr) {
    FcIdentifierExpr* target;
    FcConst* constant;
    bool subtract, isStep = false;
    FcResolvedRef* reference;
    if (expr->kind == EK_IncDecExpr && ((FcIncDecExpr*)expr)->target->kind == EK_IdentifierExpr) {
        target = (FcIdentifierExpr*)((FcIncDecExpr*)expr)->target;
        constant = fcMakeInt(CC_, 1);
        subtract = !((FcIncDecExpr*)expr)->isIncrement;
        isStep = true;
    } else if (expr->kind == EK_AssignExpr && ((FcAssignExpr*)expr)->target->kind == EK_IdentifierExpr && ((FcAssignExpr*)expr)->value->kind == EK_BinaryExpr) {
        FcAssignExpr* a = (FcAssignExpr*)expr;
        FcBinaryExpr* binary = (FcBinaryExpr*)a->value;
        FcResolvedRef* leftRef;
        FcResolvedRef* targetRef;
        if (!(binary->op == BOP_Add || binary->op == BOP_Sub) || binary->left->kind != EK_IdentifierExpr || binary->right->kind != EK_LiteralExpr) return false;
        if (!streq0(((FcIdentifierExpr*)binary->left)->name, ((FcIdentifierExpr*)a->target)->name)) return false;
        if (!(((FcLiteralExpr*)binary->right)->value.kind == VK_Int || ((FcLiteralExpr*)binary->right)->value.kind == VK_Float)) return false;
        if (cm->sh->rr->localGuards && fcDictHas(cm->sh->rr->localGuards, binary)) return false;
        if (cm->sh->rr->unitChecks && fcDictHas(cm->sh->rr->unitChecks, binary)) return false;
        leftRef = (FcResolvedRef*)fcDictGet(cm->sh->refs, binary->left);
        targetRef = (FcResolvedRef*)fcDictGet(cm->sh->refs, a->target);
        if (!leftRef || !targetRef || !fcRefEquals(leftRef, targetRef)) return false;
        target = (FcIdentifierExpr*)a->target;
        constant = fcConstOfValue(CC_, &((FcLiteralExpr*)binary->right)->value);
        subtract = binary->op == BOP_Sub;
    } else return false;
    if (!(reference = (FcResolvedRef*)fcDictGet(cm->sh->refs, target))) return false;
    if (reference->kind == RR_Local && reference->requiredUnit == NULL && !reference->byRef && reference->width == 0) {
        E_op(cm, OP_ArithLocalConstPop);
        E_u16(cm, reference->depth);
        E_u16(cm, reference->slot);
        E_u16(cm, E_const(cm, constant));
        E_u8(cm, (subtract ? 1 : 0) | (isStep ? 2 : 0));
        return true;
    }
    if (reference->kind == RR_Global && reference->requiredUnit == NULL && reference->width == 0) {
        E_op(cm, OP_ArithGlobalConstPop);
        E_u16(cm, reference->slot);
        E_u16(cm, E_const(cm, constant));
        E_u8(cm, (subtract ? 1 : 0) | (isStep ? 2 : 0));
        return true;
    }
    return false;
}

static void CompileIf(FcComp* cm, FcIfStmt* s) {
    int elseJumpAt;
    CompileExpr(cm, s->condition);
    EmitReleaseTemps(cm, s->condition);
    elseJumpAt = EmitJumpIfFalse(cm);
    CompileScopedBody(cm, s->then);
    if (s->otherwise) {
        int endJumpAt;
        E_op(cm, OP_Jump);
        endJumpAt = E_here(cm);
        E_u16(cm, 0);
        E_patch(cm, elseJumpAt, E_here(cm));
        CompileScopedBody(cm, s->otherwise);
        E_patch(cm, endJumpAt, E_here(cm));
    } else E_patch(cm, elseJumpAt, E_here(cm));
}

static void fcPatchAll(FcComp* cm, FcList* addrs, int target) { int i; for (i = 0; i < addrs->count; i++) E_patch(cm, FC_VALI(addrs->items[i]), target); }

static void CompileWhile(FcComp* cm, FcWhileStmt* s) {
    FcLoopCtx* ctx = fcLoopNew(cm, false);
    int loopStart, endJumpAt, continueTarget, loopEnd;
    fcListAdd(CC_, cm->loopStack, ctx);
    loopStart = E_here(cm);
    CompileExpr(cm, s->condition);
    EmitReleaseTemps(cm, s->condition);
    endJumpAt = EmitJumpIfFalse(cm);
    CompileScopedBody(cm, s->body);
    fcChunkMarkLine(CC_, CH_, CurrentSourceIndex(cm), s->h.line);
    continueTarget = E_here(cm);
    fcPatchAll(cm, ctx->continuePatches, continueTarget);
    E_op(cm, OP_Jump);
    E_u16(cm, loopStart);
    loopEnd = E_here(cm);
    E_patch(cm, endJumpAt, loopEnd);
    fcPatchAll(cm, ctx->breakPatches, loopEnd);
    cm->loopStack->count--;
}

static void CompileDoWhile(FcComp* cm, FcDoWhileStmt* s) {
    FcLoopCtx* ctx = fcLoopNew(cm, false);
    int loopStart, endJumpAt, continueTarget, loopEnd;
    fcListAdd(CC_, cm->loopStack, ctx);
    loopStart = E_here(cm);
    CompileScopedBody(cm, s->body);
    fcChunkMarkLine(CC_, CH_, CurrentSourceIndex(cm), s->h.line);
    continueTarget = E_here(cm);
    fcPatchAll(cm, ctx->continuePatches, continueTarget);
    CompileExpr(cm, s->condition);
    EmitReleaseTemps(cm, s->condition);
    endJumpAt = EmitJumpIfFalse(cm);
    E_op(cm, OP_Jump);
    E_u16(cm, loopStart);
    loopEnd = E_here(cm);
    E_patch(cm, endJumpAt, loopEnd);
    fcPatchAll(cm, ctx->breakPatches, loopEnd);
    cm->loopStack->count--;
}

static void CompileFor(FcComp* cm, FcForStmt* s) {
    FcLoopCtx* ctx;
    int loopStart, endJumpAt = -1, continueTarget, loopEnd;
    EmitEnterScope(cm);
    if (s->init) CompileStmt(cm, s->init);
    ctx = fcLoopNew(cm, false);
    fcListAdd(CC_, cm->loopStack, ctx);
    loopStart = E_here(cm);
    if (s->condition) {
        CompileExpr(cm, s->condition);
        EmitReleaseTemps(cm, s->condition);
        endJumpAt = EmitJumpIfFalse(cm);
    }
    CompileScopedBody(cm, s->body);
    fcChunkMarkLine(CC_, CH_, CurrentSourceIndex(cm), s->h.line);
    continueTarget = E_here(cm);
    fcPatchAll(cm, ctx->continuePatches, continueTarget);
    if (s->increment) {
        CompileDiscardedExpr(cm, s->increment);
        EmitReleaseTemps(cm, s->increment);
    }
    E_op(cm, OP_Jump);
    E_u16(cm, loopStart);
    loopEnd = E_here(cm);
    if (endJumpAt >= 0) E_patch(cm, endJumpAt, loopEnd);
    fcPatchAll(cm, ctx->breakPatches, loopEnd);
    cm->loopStack->count--;
    EmitExitScope(cm);
}

static void EmitCallMethodByName(FcComp* cm, const char* methodName, int argCount) {
    E_op(cm, OP_CallMethod);
    E_u16(cm, E_str(cm, methodName));
    E_u8(cm, argCount);
}

static void CompileForeach(FcComp* cm, FcForeachStmt* fs) {
    FcLoopCtx* ctx;
    int loopStart, endJumpAt, continueTarget, loopEnd;
    CompileExpr(cm, fs->iterable);
    EmitCallMethodByName(cm, "GetEnumerator", 0);
    ctx = fcLoopNew(cm, true);
    fcListAdd(CC_, cm->loopStack, ctx);
    fcListAdd(CC_, cm->residents, FC_IVAL(1));
    loopStart = E_here(cm);
    E_op(cm, OP_Dup);
    EmitCallMethodByName(cm, "MoveNext", 0);
    E_op(cm, OP_JumpIfFalse);
    endJumpAt = E_here(cm);
    E_u16(cm, 0);
    EmitEnterScope(cm);
    E_op(cm, OP_Dup);
    EmitCallMethodByName(cm, "GetCurrent", 0);
    EmitClaimToScope(cm, 0, true, false);
    fcChunkMarkDeclaredName(CC_, CH_, E_here(cm), fs->varName);
    E_op(cm, OP_DeclareLocal);
    CompileScopedBody(cm, fs->body);
    EmitExitScope(cm);
    continueTarget = E_here(cm);
    fcPatchAll(cm, ctx->continuePatches, continueTarget);
    E_op(cm, OP_Jump);
    E_u16(cm, loopStart);
    fcChunkMarkLine(CC_, CH_, CurrentSourceIndex(cm), fs->h.line);
    loopEnd = E_here(cm);
    E_patch(cm, endJumpAt, loopEnd);
    fcPatchAll(cm, ctx->breakPatches, loopEnd);
    E_op(cm, OP_Pop);
    EmitReleaseTemps(cm, fs->iterable);
    cm->residents->count--;
    cm->loopStack->count--;
}

static void CompileArrayAlloc(FcComp* cm, FcList* ranks, int rankIndex, bool byteBuffer) {
    int sized = 0, i;
    if (byteBuffer && ranks->count == 1 && rankIndex == 0 && ranks->items[0] != NULL) {
        CompileExpr(cm, (FcExpr*)ranks->items[0]);
        E_op(cm, OP_MakeBuffer);
        return;
    }
    while (rankIndex + sized < ranks->count && ranks->items[rankIndex + sized] != NULL) sized++;
    for (i = 0; i < sized; i++) CompileExpr(cm, (FcExpr*)ranks->items[rankIndex + i]);
    if (sized == 1) { E_op(cm, OP_NewArray); return; }
    E_op(cm, OP_NewJagged);
    E_u8(cm, sized);
}

static void CompileTry(FcComp* cm, FcTryStmt* t) {
    FcCtx* c = CC_;
    bool hasFinally = t->finallyBlock != NULL;
    FcHandler* tmpl = (FcHandler*)fcAlloc(c, sizeof *tmpl);
    int templateIdx, i, k, finallyOrEndAddr;
    FcTryCtx* tryContext = (FcTryCtx*)fcAlloc(c, sizeof *tryContext);
    FcList* jumpsToFinallyOrEnd = fcListNew(c);
    tmpl->catchTypes = fcListNew(c); tmpl->catchAddrs = fcListNew(c); tmpl->finallyAddr = -1;
    templateIdx = fcChunkAddHandler(c, CH_, tmpl);
    E_op(cm, OP_RegisterHandler);
    E_u16(cm, templateIdx);
    tryContext->stmt = t; tryContext->phase = TRY_Try; tryContext->outerDepth = cm->currentScopeDepth; tryContext->loopCount = cm->loopStack->count;
    tryContext->residentsAtStart = cm->residents->count;
    tryContext->finallyJumpPatches = fcListNew(c); tryContext->stubKeys = fcListNew(c); tryContext->stubPatches = fcListNew(c);
    fcListAdd(c, cm->tryStack, tryContext);
    CompileBlockNewScope(cm, t->tryBlock);
    cm->tryStack->count--;
    E_op(cm, OP_UnregisterHandler);
    if (hasFinally) E_op(cm, OP_EnterFinallyNormal);
    E_op(cm, OP_Jump);
    fcListAdd(c, jumpsToFinallyOrEnd, FC_IVAL(E_here(cm)));
    E_u16(cm, 0);
    for (i = 0; i < t->catches->count; i++) {
        FcCatchClause* cc = (FcCatchClause*)t->catches->items[i];
        int catchAddr = E_here(cm);
        fcListAdd(c, tmpl->catchTypes, (void*)(cc->typeRef == NULL ? NULL : ResolveCatchType(cm, cc->typeRef)));
        fcListAdd(c, tmpl->catchAddrs, FC_IVAL(catchAddr));
        tryContext->phase = TRY_Catch;
        fcListAdd(c, cm->tryStack, tryContext);
        cm->currentScopeDepth++;
        for (k = 0; k < cc->body->statements->count; k++) CompileStmt(cm, (FcStmt*)cc->body->statements->items[k]);
        cm->currentScopeDepth--;
        cm->tryStack->count--;
        E_op(cm, OP_LoadLocal); E_u16(cm, 0); E_u16(cm, 0);
        E_op(cm, OP_ClearPendingResume);
        E_op(cm, OP_ExitScope);
        if (hasFinally) { E_op(cm, OP_UnregisterHandler); E_op(cm, OP_EnterFinallyNormal); }
        E_op(cm, OP_Jump);
        fcListAdd(c, jumpsToFinallyOrEnd, FC_IVAL(E_here(cm)));
        E_u16(cm, 0);
    }
    finallyOrEndAddr = E_here(cm);
    fcPatchAll(cm, jumpsToFinallyOrEnd, finallyOrEndAddr);
    if (!hasFinally) return;
    tmpl->finallyAddr = finallyOrEndAddr;
    fcPatchAll(cm, tryContext->finallyJumpPatches, finallyOrEndAddr);
    fcListAdd(c, cm->residents, FC_IVAL(2));
    CompileBlockNewScope(cm, t->finallyBlock);
    cm->residents->count--;
    E_op(cm, OP_EndFinally);
    if (tryContext->stubKeys->count > 0) {
        int skipStubs;
        E_op(cm, OP_Jump);
        skipStubs = E_here(cm);
        E_u16(cm, 0);
        for (i = 0; i < tryContext->stubKeys->count; i++) {
            int key = FC_VALI(tryContext->stubKeys->items[i]);
            fcPatchAll(cm, (FcList*)tryContext->stubPatches->items[i], E_here(cm));
            CompileBreakOrContinue(cm, (key >> 20) != 0, key & 0xFFFFF);
        }
        E_patch(cm, skipStubs, E_here(cm));
    }
}

/* ReadingMethods.Of */
static bool fcReadingMethodsOf(FcCtx* c, FcList* parms, FcBlockStmt* body) {
    FcList* tags;
    FcList* nodes;
    FcDict* locals;
    int i;
    fcAstWalkAll(c, (FcStmt*)body, &tags, &nodes);
    locals = fcAstNamesDeclared(c, (FcStmt*)body);
    for (i = 0; i < parms->count; i++) if (!((FcLambdaParam*)parms->items[i])->byRef) fcSetAdd(locals, ((FcLambdaParam*)parms->items[i])->name);
    for (i = 0; i < nodes->count; i++) {
        FcNodeTag tag = (FcNodeTag)FC_VALI(tags->items[i]);
        if (tag == FN_EXPR) {
            FcExpr* e = (FcExpr*)nodes->items[i];
            FcExpr* target = NULL;
            if (e->kind == EK_AssignExpr) target = ((FcAssignExpr*)e)->target;
            else if (e->kind == EK_IncDecExpr) target = ((FcIncDecExpr*)e)->target;
            if (target && !(target->kind == EK_IdentifierExpr && fcDictHas(locals, ((FcIdentifierExpr*)target)->name))) return false;
            if (e->kind == EK_UnaryExpr) { FcUnaryOp op = ((FcUnaryExpr*)e)->op; if (op == UOP_Take || op == UOP_TryTake || op == UOP_AddressOf) return false; }
            if (e->kind == EK_ProbeExpr || e->kind == EK_SyncExpr || e->kind == EK_SyncGlobalsExpr || e->kind == EK_TryProcessExpr || e->kind == EK_AwaitOnExpr) return false;
        } else if (tag == FN_STMT) {
            FcStmtKind k = ((FcStmt*)nodes->items[i])->kind;
            if (k == SK_DeleteStmt || k == SK_SectionEnterStmt || k == SK_SectionExitStmt || k == SK_PostGlobalStmt || k == SK_FireStmt || k == SK_ProcessStmt || k == SK_LeaveStmt
                || k == SK_TerminateStmt || k == SK_UnsafeStmt) return false;
        }
    }
    return true;
}
