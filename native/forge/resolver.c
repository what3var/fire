/* native/forge/resolver.c - port of src/fire.Compiler/Resolver.cs and Resolver.StaticInits.cs (commit c7f13d97), with ResolvedRef
 * (src/fire/Resolving/ResolvedRef.cs) and AstNames (the end of Resolver.cs). Method by method in the order of the C# files. A ResolverException is
 * FC_ERR_RESOLVE on the context (message, line, source); the combined one that Resolve throws carries all errors in err.list. The walk of AstNames
 * (reflection over the properties of the records) is fcAstChildren: the fields of every node in the order of its record. */

/* ---- ResolvedRef ---- */
typedef enum FcRefKind { RR_Local, RR_Global, RR_LambdaCaptures, RR_Native, RR_Extern, RR_TryableNative, RR_TryTake, RR_EnumMember, RR_StaticMember, RR_ImplicitThisMember } FcRefKind;
typedef struct FcResolvedRef {
    FcRefKind kind;
    int depth, slot;                 /* Local (depth), Local/Global (slot) */
    const char* requiredUnit;        /* Local, Global */
    bool byRef;                      /* Local */
    int width;                       /* Local, Global */
    FcTypeRef* arrayElem;            /* Local, Global */
    int arrayRank;
    FcList* variables;               /* LambdaCaptures: IdentifierExpr* */
    FcList* loads;                   /* LambdaCaptures: Expr* (NULL: none) */
    FcList* implicitNames;           /* LambdaCaptures: fc8 (NULL: none) */
    const char* name;                /* Native, Extern, TryableNative, TryTake */
    int64_t value;                   /* EnumMember */
    const char* enumType;            /* EnumMember: the name of its enum (Values.EnumType), NULL: none */
    const char* className;           /* StaticMember */
} FcResolvedRef;

static FcResolvedRef* fcRef(FcCtx* c, FcRefKind kind) { FcResolvedRef* r = (FcResolvedRef*)fcAlloc(c, sizeof *r); r->kind = kind; return r; }

/* FixedCoercion (filled by the type checker) */
typedef struct FcFixedCoercion { int type; bool toWord; const char* unit; } FcFixedCoercion;   /* type: a TypeTag, -1 null */

/* ResolveResult */
typedef struct FcResolveResult {
    FcDict* references;            /* Expr* -> FcResolvedRef* */
    FcDict* unitChecks;            /* Expr* -> fc8 */
    FcDict* methodTypeArgs;        /* Expr* -> FcList* of fc8 */
    FcDict* treeLambdas;           /* ExpressionTrees.Lambdas */
    FcDict* treeTypes;             /* ExpressionTrees.Types */
    FcDict* treeToCompiled;        /* ExpressionTrees.ToCompiled */
    FcDict* localGuards;           /* Expr* -> FcLocalGuard* */
    FcDict* fixedCoercions;        /* Expr* -> FcFixedCoercion* */
    FcDict* overloadHints;         /* Expr* -> fc8 */
    FcDict* receiverTypeArgs;      /* Expr* -> fc8 */
    FcDict* derivedDefined;        /* LambdaParam* */
    FcDict* derivedStrings;        /* LambdaParam* */
    FcDict* derivedReturns;        /* BlockStmt* */
    FcDict* derivedDefinedFields;  /* FieldDecl* */
    FcDict* classes;               /* name -> ClassDecl* */
    int globalSlotCount;
    FcDict* externs;               /* name -> ExternDecl* */
    bool noShadowGlobals;
    FcDict* staticInitDeps;        /* "Class\x01Field" -> FcList* of "Class\x01Field" */
} FcResolveResult;
typedef struct FcLocalGuard { FcTypeRef* type; const char* what; } FcLocalGuard;

/* ---- the walk of the tree in the order of the records' properties (AstNames.Walk/Collect by reflection) ---- */
typedef enum FcNodeTag { FN_EXPR, FN_STMT, FN_LAMBDA_PARAM, FN_CATCH, FN_FIRE_CAPTURE, FN_FIRE_WITH, FN_TYPEREF, FN_OTHER } FcNodeTag;
typedef void (*FcNodeVisit)(void* self, FcNodeTag tag, void* node);

static void fcWalkExpr(FcExpr* e, FcNodeVisit visit, void* self);
static void fcWalkStmt(FcStmt* s, FcNodeVisit visit, void* self);
static void fcWalkTypeRef(FcTypeRef* t, FcNodeVisit visit, void* self) {
    int i;
    if (!t) return;
    visit(self, FN_TYPEREF, t);
    if (t->typeArgs) for (i = 0; i < t->typeArgs->count; i++) fcWalkTypeRef((FcTypeRef*)t->typeArgs->items[i], visit, self);
}
static void fcWalkExprs(FcList* l, FcNodeVisit visit, void* self) { int i; if (l) for (i = 0; i < l->count; i++) fcWalkExpr((FcExpr*)l->items[i], visit, self); }
static void fcWalkStmts(FcList* l, FcNodeVisit visit, void* self) { int i; if (l) for (i = 0; i < l->count; i++) fcWalkStmt((FcStmt*)l->items[i], visit, self); }
static void fcWalkParam(FcLambdaParam* p, FcNodeVisit visit, void* self) {
    visit(self, FN_LAMBDA_PARAM, p);
    fcWalkTypeRef(p->type, visit, self);
    fcWalkExprs(p->arrayRanks, visit, self);
    fcWalkExpr(p->defaultValue, visit, self);
}
static void fcWalkParams(FcList* l, FcNodeVisit visit, void* self) { int i; if (l) for (i = 0; i < l->count; i++) fcWalkParam((FcLambdaParam*)l->items[i], visit, self); }
static void fcWalkTypeRefs(FcList* l, FcNodeVisit visit, void* self) { int i; if (l) for (i = 0; i < l->count; i++) fcWalkTypeRef((FcTypeRef*)l->items[i], visit, self); }
static void fcWalkAttributes(FcList* l, FcNodeVisit visit, void* self) {
    int i;
    if (!l) return;
    for (i = 0; i < l->count; i++) { FcAttributeDecl* a = (FcAttributeDecl*)l->items[i]; visit(self, FN_OTHER, a); fcWalkExprs(a->args, visit, self); }
}

static void fcWalkExpr(FcExpr* e, FcNodeVisit visit, void* self) {
    int i;
    if (!e) return;
    visit(self, FN_EXPR, e);
    switch (e->kind) {
        case EK_LambdaExpr: { FcLambdaExpr* x = (FcLambdaExpr*)e; fcWalkParams(x->params, visit, self); fcWalkExpr(x->onTarget, visit, self); fcWalkStmt((FcStmt*)x->body, visit, self); break; }
        case EK_SyncExpr: fcWalkExpr(((FcSyncExpr*)e)->target, visit, self); break;
        case EK_ProbeExpr: fcWalkExpr(((FcProbeExpr*)e)->target, visit, self); fcWalkExpr(((FcProbeExpr*)e)->handler, visit, self); break;
        case EK_TryProcessExpr: fcWalkExpr(((FcTryProcessExpr*)e)->target, visit, self); break;
        case EK_TryCallExpr: fcWalkExpr(((FcTryCallExpr*)e)->call, visit, self); break;
        case EK_ConditionalAccessExpr: { FcConditionalAccessExpr* x = (FcConditionalAccessExpr*)e; fcWalkExpr(x->target, visit, self); fcWalkExpr(x->inner, visit, self); fcWalkExpr(x->placeholder, visit, self); break; }
        case EK_NewExpr: { FcNewExpr* x = (FcNewExpr*)e; fcWalkTypeRef(x->classRef, visit, self); fcWalkExprs(x->args, visit, self); break; }
        case EK_NewArrayExpr: { FcNewArrayExpr* x = (FcNewArrayExpr*)e; fcWalkTypeRef(x->elementType, visit, self); fcWalkExprs(x->sizeExprs, visit, self); break; }
        case EK_NewBufferExpr: fcWalkExpr(((FcNewBufferExpr*)e)->sizeExpr, visit, self); break;
        case EK_ArrayLiteralExpr: fcWalkExprs(((FcArrayLiteralExpr*)e)->elements, visit, self); break;
        case EK_InterpolatedStringExpr: {
            FcInterpolatedStringExpr* x = (FcInterpolatedStringExpr*)e;
            for (i = 0; i < x->parts->count; i++) { FcInterpolationPart* part = (FcInterpolationPart*)x->parts->items[i]; visit(self, FN_OTHER, part); if (part->isExpression) fcWalkExpr(part->expression, visit, self); }
            break;
        }
        case EK_ThrowExpr: fcWalkExpr(((FcThrowExpr*)e)->value, visit, self); break;
        case EK_UnaryExpr: fcWalkExpr(((FcUnaryExpr*)e)->operand, visit, self); break;
        case EK_BinaryExpr: fcWalkExpr(((FcBinaryExpr*)e)->left, visit, self); fcWalkExpr(((FcBinaryExpr*)e)->right, visit, self); break;
        case EK_RebindExpr: fcWalkExpr(((FcRebindExpr*)e)->lambda, visit, self); fcWalkExpr(((FcRebindExpr*)e)->target, visit, self); break;
        case EK_ConditionalExpr: { FcConditionalExpr* x = (FcConditionalExpr*)e; fcWalkExpr(x->condition, visit, self); fcWalkExpr(x->then, visit, self); fcWalkExpr(x->otherwise, visit, self); break; }
        case EK_UnitCoerceExpr: fcWalkExpr(((FcUnitCoerceExpr*)e)->operand, visit, self); break;
        case EK_TypeCoerceExpr: fcWalkExpr(((FcTypeCoerceExpr*)e)->operand, visit, self); fcWalkTypeRef(((FcTypeCoerceExpr*)e)->targetClass, visit, self); break;
        case EK_IsInExpr: fcWalkExpr(((FcIsInExpr*)e)->operand, visit, self); break;
        case EK_RangeExpr: fcWalkExpr(((FcRangeExpr*)e)->from, visit, self); fcWalkExpr(((FcRangeExpr*)e)->to, visit, self); break;
        case EK_IsOfExpr: fcWalkExpr(((FcIsOfExpr*)e)->operand, visit, self); fcWalkTypeRef(((FcIsOfExpr*)e)->typeRef, visit, self); break;
        case EK_IsFromExpr: fcWalkExpr(((FcIsFromExpr*)e)->operand, visit, self); fcWalkExpr(((FcIsFromExpr*)e)->ownerExpr, visit, self); break;
        case EK_UntilExpr: fcWalkExpr(((FcUntilExpr*)e)->inner, visit, self); fcWalkExprs(((FcUntilExpr*)e)->limits, visit, self); break;
        case EK_AwaitOnExpr: fcWalkExpr(((FcAwaitOnExpr*)e)->lambda, visit, self); fcWalkExprs(((FcAwaitOnExpr*)e)->args, visit, self); break;
        case EK_CallExpr: { FcCallExpr* x = (FcCallExpr*)e; fcWalkExpr(x->callee, visit, self); fcWalkExprs(x->args, visit, self); fcWalkTypeRefs(x->typeArgs, visit, self); break; }
        case EK_MemberExpr: fcWalkExpr(((FcMemberExpr*)e)->target, visit, self); break;
        case EK_IndexExpr: fcWalkExpr(((FcIndexExpr*)e)->target, visit, self); fcWalkExpr(((FcIndexExpr*)e)->index, visit, self); break;
        case EK_AssignExpr: fcWalkExpr(((FcAssignExpr*)e)->target, visit, self); fcWalkExpr(((FcAssignExpr*)e)->value, visit, self); break;
        case EK_WithExpr: {
            FcWithExpr* x = (FcWithExpr*)e;
            fcWalkExpr(x->source, visit, self);
            for (i = 0; i < x->sets->count; i++) { FcWithSet* ws = (FcWithSet*)x->sets->items[i]; visit(self, FN_OTHER, ws); fcWalkExpr(ws->value, visit, self); }
            break;
        }
        case EK_IncDecExpr: fcWalkExpr(((FcIncDecExpr*)e)->target, visit, self); break;
        default: break;
    }
}

static void fcWalkStmt(FcStmt* s, FcNodeVisit visit, void* self) {
    int i;
    if (!s) return;
    visit(self, FN_STMT, s);
    switch (s->kind) {
        case SK_BlockStmt: fcWalkStmts(((FcBlockStmt*)s)->statements, visit, self); break;
        case SK_ExprStmt: fcWalkExpr(((FcExprStmt*)s)->expression, visit, self); break;
        case SK_TimeoutDirective: case SK_EndWaitDirective: fcWalkExpr(((FcTimeoutDirective*)s)->value, visit, self); break;
        case SK_EnumDecl: {
            FcEnumDecl* x = (FcEnumDecl*)s;
            for (i = 0; i < x->members->count; i++) { FcEnumMember* m = (FcEnumMember*)x->members->items[i]; visit(self, FN_OTHER, m); fcWalkExpr(m->valueExpr, visit, self); }
            break;
        }
        case SK_VarDeclStmt: { FcVarDeclStmt* x = (FcVarDeclStmt*)s; fcWalkTypeRef(x->type, visit, self); fcWalkExprs(x->arrayRanks, visit, self); fcWalkExpr(x->initializer, visit, self); break; }
        case SK_IfStmt: { FcIfStmt* x = (FcIfStmt*)s; fcWalkExpr(x->condition, visit, self); fcWalkStmt(x->then, visit, self); fcWalkStmt(x->otherwise, visit, self); break; }
        case SK_WhileStmt: fcWalkExpr(((FcWhileStmt*)s)->condition, visit, self); fcWalkStmt(((FcWhileStmt*)s)->body, visit, self); break;
        case SK_DoWhileStmt: fcWalkStmt(((FcDoWhileStmt*)s)->body, visit, self); fcWalkExpr(((FcDoWhileStmt*)s)->condition, visit, self); break;
        case SK_UnitDeclStmt: {
            FcUnitDeclStmt* x = (FcUnitDeclStmt*)s;
            if (x->coerces) for (i = 0; i < x->coerces->count; i++) { FcUnitCoerce* uc = (FcUnitCoerce*)x->coerces->items[i]; visit(self, FN_OTHER, uc); fcWalkExpr(uc->conversion, visit, self); }
            break;
        }
        case SK_ForStmt: { FcForStmt* x = (FcForStmt*)s; fcWalkStmt(x->init, visit, self); fcWalkExpr(x->condition, visit, self); fcWalkExpr(x->increment, visit, self); fcWalkStmt(x->body, visit, self); break; }
        case SK_ForeachStmt: fcWalkExpr(((FcForeachStmt*)s)->iterable, visit, self); fcWalkStmt(((FcForeachStmt*)s)->body, visit, self); break;
        case SK_ReturnStmt: case SK_ThrowStmt: case SK_TerminateStmt: fcWalkExpr(((FcReturnStmt*)s)->value, visit, self); break;
        case SK_DeleteStmt: case SK_ProcessStmt: fcWalkExpr(((FcDeleteStmt*)s)->target, visit, self); break;
        case SK_FireStmt: {
            FcFireStmt* x = (FcFireStmt*)s;
            for (i = 0; i < x->takingCaptures->count; i++) { FcFireTakingCapture* t = (FcFireTakingCapture*)x->takingCaptures->items[i]; visit(self, FN_FIRE_CAPTURE, t); fcWalkExpr(t->source, visit, self); }
            for (i = 0; i < x->with->count; i++) { FcFireWith* w = (FcFireWith*)x->with->items[i]; visit(self, FN_FIRE_WITH, w); fcWalkExpr(w->source, visit, self); }
            fcWalkStmt((FcStmt*)x->body, visit, self);
            fcWalkExprs(x->until, visit, self);
            break;
        }
        case SK_SilenceStmt: fcWalkExpr(((FcSilenceStmt*)s)->target, visit, self); break;
        case SK_PostGlobalStmt: fcWalkExpr(((FcPostGlobalStmt*)s)->lambda, visit, self); fcWalkExprs(((FcPostGlobalStmt*)s)->args, visit, self); break;
        case SK_CatchThreadsDecl: fcWalkTypeRef(((FcCatchThreadsDecl*)s)->typeRef, visit, self); fcWalkStmt((FcStmt*)((FcCatchThreadsDecl*)s)->body, visit, self); break;
        case SK_CatchTerminateDecl: fcWalkStmt((FcStmt*)((FcCatchTerminateDecl*)s)->body, visit, self); break;
        case SK_TryStmt: {
            FcTryStmt* x = (FcTryStmt*)s;
            fcWalkStmt((FcStmt*)x->tryBlock, visit, self);
            for (i = 0; i < x->catches->count; i++) { FcCatchClause* cc = (FcCatchClause*)x->catches->items[i]; visit(self, FN_CATCH, cc); fcWalkTypeRef(cc->typeRef, visit, self); fcWalkStmt((FcStmt*)cc->body, visit, self); }
            fcWalkStmt((FcStmt*)x->finallyBlock, visit, self);
            break;
        }
        case SK_FieldDecl: { FcFieldDecl* x = (FcFieldDecl*)s; fcWalkTypeRef(x->type, visit, self); fcWalkExprs(x->arrayRanks, visit, self); fcWalkExpr(x->initializer, visit, self); fcWalkAttributes(x->attributes, visit, self); break; }
        case SK_MethodDecl: { FcMethodDecl* x = (FcMethodDecl*)s; fcWalkTypeRef(x->returnType, visit, self); fcWalkParams(x->params, visit, self); fcWalkStmt((FcStmt*)x->body, visit, self); fcWalkAttributes(x->attributes, visit, self); break; }
        case SK_ConstructorDecl: { FcConstructorDecl* x = (FcConstructorDecl*)s; fcWalkParams(x->params, visit, self); fcWalkExprs(x->baseArgs, visit, self); fcWalkStmt((FcStmt*)x->body, visit, self); break; }
        case SK_DestructorDecl: case SK_UnsafeStmt: fcWalkStmt((FcStmt*)((FcDestructorDecl*)s)->body, visit, self); break;
        case SK_ClassDecl: { FcClassDecl* x = (FcClassDecl*)s; fcWalkTypeRefs(x->baseRefs, visit, self); fcWalkStmts(x->members, visit, self); fcWalkAttributes(x->attributes, visit, self); break; }
        case SK_InterfaceDecl: {
            FcInterfaceDecl* x = (FcInterfaceDecl*)s;
            for (i = 0; i < x->methods->count; i++) { FcInterfaceMethodSig* m = (FcInterfaceMethodSig*)x->methods->items[i]; visit(self, FN_OTHER, m); fcWalkTypeRef(m->returnType, visit, self); fcWalkParams(m->params, visit, self); }
            fcWalkTypeRefs(x->baseRefs, visit, self);
            break;
        }
        case SK_ExternDecl: { FcExternDecl* x = (FcExternDecl*)s; fcWalkTypeRef(x->returnType, visit, self); fcWalkParams(x->params, visit, self); break; }
        case SK_PropertyDecl: { FcPropertyDecl* x = (FcPropertyDecl*)s; fcWalkTypeRef(x->type, visit, self); fcWalkStmt((FcStmt*)x->getter, visit, self); fcWalkStmt((FcStmt*)x->setter, visit, self); fcWalkAttributes(x->attributes, visit, self); break; }
        case SK_ClassExtensionDecl: fcWalkTypeRef(((FcClassExtensionDecl*)s)->targetRef, visit, self); fcWalkStmts(((FcClassExtensionDecl*)s)->members, visit, self); break;
        case SK_NamespaceDecl: fcWalkStmts(((FcNamespaceDecl*)s)->members, visit, self); break;
        default: break;
    }
}

/* ---- AstNames ---- */
typedef struct FcNamesCollect { FcCtx* c; FcList* names; FcDict* seen; } FcNamesCollect;
static void fcCollectVisit(void* self, FcNodeTag tag, void* node) {
    FcNamesCollect* k = (FcNamesCollect*)self;
    if (tag == FN_EXPR && ((FcExpr*)node)->kind == EK_IdentifierExpr) {
        const char* n = ((FcIdentifierExpr*)node)->name;
        if (fcSetAdd(k->seen, n)) fcListAdd(k->c, k->names, (void*)n);
    }
}
/* AstNames.Collect of a block */
static void fcAstNamesCollect(FcCtx* c, FcStmt* node, FcList* names, FcDict* seen) {
    FcNamesCollect k;
    k.c = c; k.names = names; k.seen = seen;
    fcWalkStmt(node, fcCollectVisit, &k);
}
typedef struct FcAllNodes { FcCtx* c; FcList* tags; FcList* nodes; } FcAllNodes;
static void fcAllVisit(void* self, FcNodeTag tag, void* node) {
    FcAllNodes* k = (FcAllNodes*)self;
    fcListAdd(k->c, k->tags, FC_IVAL(tag));
    fcListAdd(k->c, k->nodes, node);
}
/* AstNames.Walk: every node below (and with) `node` */
static void fcAstWalkAll(FcCtx* c, FcStmt* node, FcList** tags, FcList** nodes) {
    FcAllNodes k;
    k.c = c; k.tags = fcListNew(c); k.nodes = fcListNew(c);
    fcWalkStmt(node, fcAllVisit, &k);
    *tags = k.tags; *nodes = k.nodes;
}
/* AstNames.Declared */
static FcDict* fcAstNamesDeclared(FcCtx* c, FcStmt* node) {
    FcDict* declared = fcDictNew(c, FC_KEY_STR);
    FcList* tags;
    FcList* nodes;
    int i, k;
    fcAstWalkAll(c, node, &tags, &nodes);
    for (i = 0; i < nodes->count; i++) {
        FcNodeTag tag = (FcNodeTag)FC_VALI(tags->items[i]);
        void* n = nodes->items[i];
        if (tag == FN_STMT && ((FcStmt*)n)->kind == SK_VarDeclStmt) fcSetAdd(declared, ((FcVarDeclStmt*)n)->name);
        else if (tag == FN_STMT && ((FcStmt*)n)->kind == SK_ForeachStmt) fcSetAdd(declared, ((FcForeachStmt*)n)->varName);
        else if (tag == FN_CATCH) fcSetAdd(declared, ((FcCatchClause*)n)->varName);
        else if (tag == FN_LAMBDA_PARAM) fcSetAdd(declared, ((FcLambdaParam*)n)->name);
        else if (tag == FN_FIRE_CAPTURE) fcSetAdd(declared, ((FcFireTakingCapture*)n)->varName);
        else if (tag == FN_STMT && ((FcStmt*)n)->kind == SK_FireStmt) {
            FcFireStmt* f = (FcFireStmt*)n;
            for (k = 0; k < f->with->count; k++) fcSetAdd(declared, ((FcFireWith*)f->with->items[k])->varName);
        }
    }
    return declared;
}
/* AstNames.Assigned */
FC_LATER static FcDict* fcAstNamesAssigned(FcCtx* c, FcBlockStmt* body) {
    FcDict* names = fcDictNew(c, FC_KEY_STR);
    FcList* tags;
    FcList* nodes;
    int i;
    fcAstWalkAll(c, (FcStmt*)body, &tags, &nodes);
    for (i = 0; i < nodes->count; i++) {
        FcNodeTag tag = (FcNodeTag)FC_VALI(tags->items[i]);
        void* n = nodes->items[i];
        if (tag == FN_EXPR && ((FcExpr*)n)->kind == EK_AssignExpr && ((FcAssignExpr*)n)->target->kind == EK_IdentifierExpr)
            fcSetAdd(names, ((FcIdentifierExpr*)((FcAssignExpr*)n)->target)->name);
        else if (tag == FN_EXPR && ((FcExpr*)n)->kind == EK_IncDecExpr && ((FcIncDecExpr*)n)->target->kind == EK_IdentifierExpr)
            fcSetAdd(names, ((FcIdentifierExpr*)((FcIncDecExpr*)n)->target)->name);
        else if (tag == FN_STMT && ((FcStmt*)n)->kind == SK_VarDeclStmt) fcSetAdd(names, ((FcVarDeclStmt*)n)->name);
    }
    return names;
}
static FcIdentifierExpr* fcRootOf(FcExpr* e) {
    if (!e) return NULL;
    if (e->kind == EK_IdentifierExpr) return (FcIdentifierExpr*)e;
    if (e->kind == EK_MemberExpr) return fcRootOf(((FcMemberExpr*)e)->target);
    if (e->kind == EK_IndexExpr) return fcRootOf(((FcIndexExpr*)e)->target);
    return NULL;
}
static void fcConsumed(FcDict* wanted, FcDict* safe, FcExpr* e) {
    FcIdentifierExpr* root = fcRootOf(e);
    if (root && fcDictHas(wanted, root->name)) fcSetAdd(safe, root);
}
/* AstNames.ReadOnly */
FC_LATER static FcDict* fcAstNamesReadOnly(FcCtx* c, FcLambdaExpr* lambda, FcList* names) {
    FcList* tags;
    FcList* nodes;
    FcDict* declared = fcAstNamesDeclared(c, (FcStmt*)lambda->body);
    FcDict* wanted = fcDictNew(c, FC_KEY_STR);
    FcDict* safe = fcDictNew(c, FC_KEY_PTR);
    FcDict* result = fcDictNew(c, FC_KEY_STR);
    int i, k;
    fcAstWalkAll(c, (FcStmt*)lambda->body, &tags, &nodes);
    for (i = 0; i < names->count; i++) fcSetAdd(wanted, names->items[i]);
    FC_DICT_EACH(declared, e) fcDictRemove(wanted, e->key);
    for (i = 0; i < nodes->count; i++) {
        FcNodeTag tag = (FcNodeTag)FC_VALI(tags->items[i]);
        if (tag == FN_EXPR) {
            FcExpr* e = (FcExpr*)nodes->items[i];
            switch (e->kind) {
                case EK_BinaryExpr: fcConsumed(wanted, safe, ((FcBinaryExpr*)e)->left); fcConsumed(wanted, safe, ((FcBinaryExpr*)e)->right); break;
                case EK_UnaryExpr: { FcUnaryExpr* u = (FcUnaryExpr*)e; if (u->op == UOP_Negate || u->op == UOP_LogicalNot || u->op == UOP_BitNot) fcConsumed(wanted, safe, u->operand); break; }
                case EK_ConditionalExpr: fcConsumed(wanted, safe, ((FcConditionalExpr*)e)->condition); break;
                case EK_InterpolatedStringExpr: {
                    FcInterpolatedStringExpr* x = (FcInterpolatedStringExpr*)e;
                    for (k = 0; k < x->parts->count; k++) { FcInterpolationPart* p = (FcInterpolationPart*)x->parts->items[k]; if (p->isExpression) fcConsumed(wanted, safe, p->expression); }
                    break;
                }
                case EK_IndexExpr: fcConsumed(wanted, safe, ((FcIndexExpr*)e)->index); break;
                case EK_CallExpr: {
                    FcCallExpr* call = (FcCallExpr*)e;
                    if (call->callee->kind != EK_IdentifierExpr) break;
                    if (fcDictHas(wanted, ((FcIdentifierExpr*)call->callee)->name)) fcSetAdd(safe, call->callee);
                    else if (streq0(((FcIdentifierExpr*)call->callee)->name, "print")) for (k = 0; k < call->args->count; k++) fcConsumed(wanted, safe, (FcExpr*)call->args->items[k]);
                    break;
                }
                default: break;
            }
        } else if (tag == FN_STMT) {
            FcStmt* s = (FcStmt*)nodes->items[i];
            if (s->kind == SK_IfStmt) fcConsumed(wanted, safe, ((FcIfStmt*)s)->condition);
            else if (s->kind == SK_WhileStmt) fcConsumed(wanted, safe, ((FcWhileStmt*)s)->condition);
        }
    }
    FC_DICT_EACH(wanted, e) fcSetAdd(result, e->key);
    for (i = 0; i < nodes->count; i++)
        if (FC_VALI(tags->items[i]) == FN_EXPR && ((FcExpr*)nodes->items[i])->kind == EK_IdentifierExpr) {
            FcIdentifierExpr* id = (FcIdentifierExpr*)nodes->items[i];
            if (fcDictHas(result, id->name) && !fcDictHas(safe, id)) fcDictRemove(result, id->name);
        }
    return result;
}

/* AstNames.ProveMember / ProvenMember: a table on the context */
FC_LATER static void fcProveMember(FcCtx* c, FcDict** table, FcExpr* e) { if (!*table) *table = fcDictNew(c, FC_KEY_PTR); fcDictPut(*table, e, (void*)1); }

/* ---- the resolver ---- */
typedef struct FcResolverScope {
    struct FcResolverScope* parent;
    bool isGlobal;
    FcDict slots;            /* name -> slot (int) */
    FcDict readonlySlots;
    FcDict requiredUnits;    /* name -> fc8 */
    FcDict widths;           /* name -> int */
    FcDict arrayDecls;       /* name -> FcArrayDecl* */
    FcDict refNames;
    FcDict captureNames;
    FcDict knownClasses;     /* name -> fc8 */
} FcResolverScope;
typedef struct FcArrayDecl { FcTypeRef* type; int rank; } FcArrayDecl;

typedef struct FcIntStack { int value; struct FcIntStack* next; int count; } FcIntStack;   /* ImmutableStack<int> (NULL: Empty) */

typedef struct FcCodeUnit { FcClassDecl* cls; const char* name; FcList* roots; FcList* rootTags; } FcCodeUnit;
typedef struct FcUnitFacts { FcList* reads; FcList* calls; } FcUnitFacts;

typedef struct FcResolver {
    FcCtx* c;
    FcDict refs;                 /* Expr* -> FcResolvedRef* */
    FcDict classes;              /* name -> ClassDecl* */
    FcDict interfaces;           /* name -> InterfaceDecl* */
    FcDict externs;              /* name -> ExternDecl* */
    FcDict enums;                /* name -> FcDict* (member -> int64*) */
    FcDict enumTypes;            /* name -> name */
    FcDict* nativeNames;
    FcDict* tryableNativeNames;
    FcResolverScope* globalScope;
    FcResolverScope* current;
    int functionDepth;
    FcList* errors;              /* FcError* */
    FcDict errorMessages;
    int currentSource;
    bool noShadowGlobals;
    int finalGlobalCount;        /* FC_NULL_INT: the first pass */
    bool sawFire;
    int loopDepth, tryDepth, unsafeDepth;
    FcClassDecl* currentClass;
    bool currentClassHasBase;
    FcDict currentTypeParamNames;   /* name -> count */
    bool inConstructor;
    bool inStaticMethod;
    FcDict* declaredByClass;     /* plain class name -> FcList* of FcDeclaredWarning* */
    bool declaredLoaded;
    FcDict calleeArity;          /* Expr* -> int */
    FcIntStack* loopFinallyDepths;
    FcDict takeAllowed;          /* (record equality of the expressions) */
    FcResolverScope* fireScope;
    int fireSharedSlots;
    bool inBoundLambda;
    /* Resolver.StaticInits.cs */
    FcDict receiverClasses;      /* MemberExpr* -> fc8 */
    FcDict staticInitDeps;       /* "C\x01F" -> FcList* of "C\x01F" */
    FcDict codeUnits;            /* member -> FcCodeUnit* */
    FcDict unitFacts;            /* FcCodeUnit* -> FcUnitFacts* */
} FcResolver;

#define R_ (r->c)

static FcResolverScope* fcScopeNew(FcResolver* r, FcResolverScope* parent, bool isGlobal) {
    FcResolverScope* s = (FcResolverScope*)fcAlloc(R_, sizeof *s);
    s->parent = parent;
    s->isGlobal = isGlobal;
    fcDictInit(R_, &s->slots, FC_KEY_STR);
    fcDictInit(R_, &s->readonlySlots, FC_KEY_STR);
    fcDictInit(R_, &s->requiredUnits, FC_KEY_STR);
    fcDictInit(R_, &s->widths, FC_KEY_STR);
    fcDictInit(R_, &s->arrayDecls, FC_KEY_STR);
    fcDictInit(R_, &s->refNames, FC_KEY_STR);
    fcDictInit(R_, &s->captureNames, FC_KEY_STR);
    fcDictInit(R_, &s->knownClasses, FC_KEY_STR);
    return s;
}

/* ResolverException: thrown (`throw new ResolverException(message, line)`) */
_Noreturn static void fcRError(FcResolver* r, int line, const char* message) {
    fcThrow(R_, FC_ERR_RESOLVE, line, 0, NULL, message);
}
#define RERR(line, ...) fcRError(r, (line), fcFmt(R_, __VA_ARGS__))
static FcError* fcRErrorNew(FcResolver* r, int line, const char* message) {
    FcError* e = (FcError*)fcAlloc(R_, sizeof *e);
    e->kind = FC_ERR_RESOLVE; e->line = line; e->message = message; e->source = -1;
    return e;
}
static FcError* fcRErrorOfCtx(FcResolver* r) { FcError* e = (FcError*)fcAlloc(R_, sizeof *e); *e = R_->err; return e; }

/* AddError */
static void AddError(FcResolver* r, FcError* error) {
    if (error->source < 0) error->source = r->currentSource;
    if (fcSetAdd(&r->errorMessages, fcFmt(R_, "%d:%s (%d)", error->source, error->message, error->line))) fcListAdd(R_, r->errors, error);
}
#define ADDERR(line, ...) AddError(r, fcRErrorNew(r, (line), fcFmt(R_, __VA_ARGS__)))

static bool IsKnownClassName(FcResolver* r, const char* name);
static const char* ResolveBaseRef(FcResolver* r, const FcTypeRef* tr);
static FcClassDecl* GetBaseClassDecl(FcResolver* r, FcClassDecl* cd);
static void ResolveStmt(FcResolver* r, FcStmt* stmt);
static void ResolveExpr(FcResolver* r, FcExpr* expr);
static void ResolveExprAllowTake(FcResolver* r, FcExpr* expr);
static void ResolveLambda(FcResolver* r, FcLambdaExpr* lambda);
static void OrderStaticInits(FcResolver* r);

/* the predicates of the C# lambdas */
static bool kIsKnownClassName(void* self, const char* n) { return IsKnownClassName((FcResolver*)self, n); }
static bool kClassOrInterfaceOrException(void* self, const char* n) {
    FcResolver* r = (FcResolver*)self;
    return streq0(n, "Exception") || fcDictHas(&r->classes, n) || fcDictHas(&r->interfaces, n);
}
static bool kClassOrInterface(void* self, const char* n) { FcResolver* r = (FcResolver*)self; return fcDictHas(&r->classes, n) || fcDictHas(&r->interfaces, n); }
static bool kKnownClassOrInterface(void* self, const char* n) { FcResolver* r = (FcResolver*)self; return IsKnownClassName(r, n) || fcDictHas(&r->interfaces, n); }
static bool kKnownTypeName(void* self, const char* n) {
    FcResolver* r = (FcResolver*)self;
    return streq0(n, "Exception") || fcDictHas(&r->classes, n) || fcDictHas(&r->interfaces, n) || fcDictHas(&r->enums, n);
}
static bool kIsEnum(void* self, const char* n) { return fcDictHas(&((FcResolver*)self)->enums, n); }
static bool kClassesHas(void* self, const char* n) { return fcDictHas(&((FcResolver*)self)->classes, n); }
static FcKnown K(FcResolver* r, bool (*fn)(void*, const char*)) { FcKnown k; k.fn = fn; k.self = r; return k; }

/* CheckAmbiguous */
static void CheckAmbiguous(FcResolver* r, const FcTypeRef* tr, int line, FcKnown known, const char* ownNamespace) {
    int typeArgs = tr->typeArgs ? tr->typeArgs->count : tr->typeArgCount;
    FcKnownMangled km;
    FcKnown k2;
    FcList* matches;
    FcBuf b = {0};
    int i;
    km.c = R_; km.inner = known; km.count = typeArgs;
    k2.fn = typeArgs > 0 ? fcKnownOrMangled : known.fn;
    k2.self = typeArgs > 0 ? (void*)&km : known.self;
    matches = fcTrAmbiguousCandidates(R_, tr, k2, ownNamespace);
    if (matches->count > 1) {
        for (i = 0; i < matches->count; i++) { if (i) fcBufStr(&b, ", "); fcBufStr(&b, (const char*)matches->items[i]); }
        RERR(line, "'%s' is ambiguous: %s (more than one '#using' or imported library brings it); write the full name", tr->baseName, fcBufDone(R_, &b));
    }
}

/* NamespaceOf */
static const char* NamespaceOf(FcResolver* r, const char* className) {
    const char* plain;
    const char* dot;
    if (className == NULL) return NULL;
    plain = fcPlainName(R_, className);
    dot = strrchr(plain, '.');
    return dot ? fcStrN(R_, plain, (size_t)(dot - plain)) : NULL;
}

static const char* ResolveTypeRef(FcResolver* r, const FcTypeRef* tr) {
    return tr->typeArgs && tr->typeArgs->count > 0 ? fcResolveNewTarget(R_, tr, tr->typeArgs->count, K(r, kIsKnownClassName)) : fcTrResolveBaseName(R_, tr, K(r, kIsKnownClassName));
}

/* Resolver.Run */
static void CollectClasses(FcResolver* r, FcList* statements);
static void CollectExterns(FcResolver* r, FcList* statements);
static void CollectEnums(FcResolver* r, FcList* statements);

static FcResolver* fcResolverNew(FcCtx* c, FcDict* nativeNames, FcDict* tryable, int finalGlobalCount) {
    FcResolver* r = (FcResolver*)fcAlloc(c, sizeof *r);
    r->c = c;
    fcDictInit(c, &r->refs, FC_KEY_PTR);
    fcDictInit(c, &r->classes, FC_KEY_STR);
    fcDictInit(c, &r->interfaces, FC_KEY_STR);
    fcDictInit(c, &r->externs, FC_KEY_STR);
    fcDictInit(c, &r->enums, FC_KEY_STR);
    fcDictInit(c, &r->enumTypes, FC_KEY_STR);
    r->nativeNames = nativeNames ? nativeNames : fcDictNew(c, FC_KEY_STR);
    r->tryableNativeNames = tryable ? tryable : fcDictNew(c, FC_KEY_STR);
    r->globalScope = fcScopeNew(r, NULL, true);
    r->current = r->globalScope;
    r->errors = fcListNew(c);
    fcDictInit(c, &r->errorMessages, FC_KEY_STR);
    r->currentSource = -1;
    r->finalGlobalCount = finalGlobalCount;
    fcDictInit(c, &r->currentTypeParamNames, FC_KEY_STR);
    fcDictInit(c, &r->calleeArity, FC_KEY_PTR);
    fcDictInit(c, &r->receiverClasses, FC_KEY_PTR);
    fcDictInit(c, &r->staticInitDeps, FC_KEY_STR);
    fcDictInit(c, &r->codeUnits, FC_KEY_PTR);
    fcDictInit(c, &r->unitFacts, FC_KEY_PTR);
    return r;
}

static bool fcExprRecordEquals(const void* a, const void* b);
static uint32_t fcExprRecordHash(const void* a);

static FcResolver* fcResolverRun(FcCtx* c, FcList* program, FcDict* nativeNames, FcDict* tryable, int finalGlobalCount) {
    FcResolver* r = fcResolverNew(c, nativeNames, tryable, finalGlobalCount);
    int i;
    {
        FcDict* t = fcDictNewCustom(c, fcExprRecordEquals, fcExprRecordHash);
        r->takeAllowed = *t;
    }
    CollectClasses(r, program);
    CollectExterns(r, program);
    CollectEnums(r, program);
    for (i = 0; i < program->count; i++) if (((FcStmt*)program->items[i])->kind == SK_NoShadowDirective) r->noShadowGlobals = true;
    for (i = 0; i < program->count; i++) {
        FcStmt* stmt = (FcStmt*)program->items[i];
        r->currentSource = stmt->source;
        ResolveStmt(r, stmt);
    }
    return r;
}

/* Resolve: the result, or FC_ERR_RESOLVE with all errors in err.list */
static FcResolveResult* fcResolve(FcCtx* c, FcList* program, FcDict* nativeNames, FcDict* tryable) {
    FcResolver* r = fcResolverRun(c, program, nativeNames, tryable, FC_NULL_INT);
    FcResolveResult* res;
    if (r->sawFire && r->errors->count == 0) r = fcResolverRun(c, program, nativeNames, tryable, fcDictCount(&r->globalScope->slots));
    OrderStaticInits(r);
    if (r->errors->count > 0) {
        FcError* first = (FcError*)r->errors->items[0];
        c->err = *first;
        c->err.list = r->errors;
        fcRethrow(c);
    }
    res = (FcResolveResult*)fcAlloc(c, sizeof *res);
    res->references = fcDictCopy(c, &r->refs);
    res->classes = fcDictCopy(c, &r->classes);
    res->globalSlotCount = fcDictCount(&r->globalScope->slots);
    res->externs = fcDictCopy(c, &r->externs);
    res->noShadowGlobals = r->noShadowGlobals;
    res->staticInitDeps = fcDictCopy(c, &r->staticInitDeps);
    res->unitChecks = fcDictNew(c, FC_KEY_PTR);
    res->methodTypeArgs = fcDictNew(c, FC_KEY_PTR);
    res->treeLambdas = fcDictNew(c, FC_KEY_PTR);
    res->treeTypes = fcDictNew(c, FC_KEY_PTR);
    res->treeToCompiled = fcDictNew(c, FC_KEY_PTR);
    res->localGuards = fcDictNew(c, FC_KEY_PTR);
    res->fixedCoercions = fcDictNew(c, FC_KEY_PTR);
    res->overloadHints = fcDictNew(c, FC_KEY_PTR);
    res->receiverTypeArgs = fcDictNew(c, FC_KEY_PTR);
    res->derivedDefined = fcDictNew(c, FC_KEY_PTR);
    res->derivedStrings = fcDictNew(c, FC_KEY_PTR);
    res->derivedReturns = fcDictNew(c, FC_KEY_PTR);
    res->derivedDefinedFields = fcDictNew(c, FC_KEY_PTR);
    return res;
}

/* CollectExterns */
static void CheckInlineCTypes(FcResolver* r, FcExternDecl* ed);
static void CollectExterns(FcResolver* r, FcList* statements) {
    int i;
    for (i = 0; i < statements->count; i++) {
        FcStmt* stmt = (FcStmt*)statements->items[i];
        FcExternDecl* ed;
        if (stmt->kind != SK_ExternDecl) continue;
        ed = (FcExternDecl*)stmt;
        if (fcDictHas(&r->externs, ed->name)) { ADDERR(ed->h.line, "'%s' is already declared as extern", ed->name); continue; }
        fcDictPut(&r->externs, ed->name, ed);
        if (ed->inlineC != NULL) CheckInlineCTypes(r, ed);
    }
}

static bool fcIsBaseValueName(const char* n) {
    return streq0(n, "int") || streq0(n, "float") || streq0(n, "bool") || streq0(n, "char") || streq0(n, "string") || streq0(n, "any");
}
static void CheckInlineCTypes(FcResolver* r, FcExternDecl* ed) {
    int i;
    for (i = 0; i < ed->params->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)ed->params->items[i];
        int rank;
        bool bytes, scalar;
        if (p->byRef) { ADDERR(ed->h.line, "The parameter '%s' of the inline C function '%s' cannot be 'ref' (take a pointer)", p->name, ed->name); continue; }
        if (p->type == NULL) { ADDERR(ed->h.line, "The parameter '%s' of the inline C function '%s' needs a type (int, float, bool, char, string, byte[], any or a pointer)", p->name, ed->name); continue; }
        rank = p->arrayRanks->count + p->type->arrayRank;
        bytes = streq0(p->type->baseName, "int") && p->type->bitWidth == 8 && p->type->pointerDepth == 0 && rank == 1;
        scalar = rank == 0 && (p->type->pointerDepth > 0 || fcIsBaseValueName(p->type->baseName));
        if (!bytes && !scalar)
            ADDERR(ed->h.line, "The parameter '%s' of the inline C function '%s' has a type C cannot take (int, float, bool, char, string, byte[], any or a pointer)", p->name, ed->name);
    }
    if (ed->returnType) {
        FcTypeRef* t = ed->returnType;
        if (!(t->arrayRank == 0 && (t->pointerDepth > 0 || fcIsBaseValueName(t->baseName) || streq0(t->baseName, "undefined"))))
            ADDERR(ed->h.line, "The inline C function '%s' has a result type C cannot give (int, float, bool, char, string, any, a pointer or undefined)", ed->name);
    }
}

/* CollectEnums (EnumTypeOf: the run-time type - here the name of the enum) */
static void CollectEnums(FcResolver* r, FcList* statements) {
    int i, k;
    for (i = 0; i < statements->count; i++) {
        FcStmt* stmt = (FcStmt*)statements->items[i];
        FcEnumDecl* ed;
        FcDict* members;
        int64_t next = 0;
        if (stmt->kind != SK_EnumDecl) continue;
        ed = (FcEnumDecl*)stmt;
        if (fcDictHas(&r->enums, ed->name)) { ADDERR(ed->h.line, "'%s' is already declared as an enum", ed->name); continue; }
        if (IsKnownClassName(r, ed->name)) { ADDERR(ed->h.line, "'%s' is already declared as a class", ed->name); continue; }
        members = fcDictNew(R_, FC_KEY_STR);
        fcDictPut(&r->enums, ed->name, members);
        for (k = 0; k < ed->members->count; k++) {
            FcEnumMember* m = (FcEnumMember*)ed->members->items[k];
            int64_t value;
            int64_t* boxed;
            if (fcDictHas(members, m->name)) { ADDERR(ed->h.line, "Enum member '%s.%s' is already declared", ed->name, m->name); continue; }
            if (m->valueExpr != NULL) {
                if (!(m->valueExpr->kind == EK_LiteralExpr && ((FcLiteralExpr*)m->valueExpr)->value.kind == VK_Int)) {
                    ADDERR(ed->h.line, "Enum member '%s.%s': the value must be an int literal (arbitrary expressions are not evaluated here).", ed->name, m->name);
                    boxed = (int64_t*)fcAlloc(R_, sizeof *boxed); *boxed = next;
                    fcDictPut(members, m->name, boxed);
                    next++;
                    continue;
                }
                value = ((FcLiteralExpr*)m->valueExpr)->value.i;
            } else value = next;
            boxed = (int64_t*)fcAlloc(R_, sizeof *boxed); *boxed = value;
            fcDictPut(members, m->name, boxed);
            next = value + 1;
        }
        fcDictPut(&r->enumTypes, ed->name, (void*)ed->name);
    }
}

/* ResolveBaseRef */
static const char* ResolveBaseRef(FcResolver* r, const FcTypeRef* tr) {
    return tr->typeArgCount == 0 ? fcTrResolveBaseName(R_, tr, K(r, kClassOrInterfaceOrException)) : fcResolveNewTarget(R_, tr, tr->typeArgCount, K(r, kClassOrInterfaceOrException));
}

static const char* DisplayName(FcResolver* r, FcClassDecl* cd) {
    FcBuf b = {0};
    int i;
    if (!(cd->typeParams && cd->typeParams->count > 0)) return cd->name;
    fcBufStr(&b, fcPlainName(R_, cd->name));
    fcBufChar(&b, '<');
    for (i = 0; i < cd->typeParams->count; i++) { if (i) fcBufStr(&b, ", "); fcBufStr(&b, ((FcTypeParam*)cd->typeParams->items[i])->name); }
    fcBufChar(&b, '>');
    return fcBufDone(R_, &b);
}

static FcList* InterfaceClosure(FcResolver* r, const char* name);
static void CheckAmbiguous(FcResolver* r, const FcTypeRef* tr, int line, FcKnown known, const char* ownNamespace);
static FcList* InterfaceCycle(FcResolver* r, FcInterfaceDecl* id);
static void ValidateStaticAndInstance(FcResolver* r, FcClassDecl* cd);
static void ValidateImplementsInterface(FcResolver* r, FcClassDecl* cd, FcInterfaceDecl* iface, bool onlyOwn);

/* Guard(() => ...) of the checks of CollectClasses: each in a function of its own, so that no local of the caller lives across the setjmp */
static bool fcAmbiguousFails(FcResolver* r, const FcTypeRef* baseRef, int line, FcKnown known, const char* ns) {
    bool failed = false;
    FC_TRY(R_) {
        CheckAmbiguous(r, baseRef, line, known, ns);
    } FC_CATCH(R_) {
        if (R_->err.kind != FC_ERR_RESOLVE) fcRethrow(R_);
        AddError(r, fcRErrorOfCtx(r));
        failed = true;
    } FC_END_TRY;
    return failed;
}
static void fcGuardStaticAndInstance(FcResolver* r, FcClassDecl* cd) {
    FC_TRY(R_) { ValidateStaticAndInstance(r, cd); } FC_CATCH(R_) { if (R_->err.kind != FC_ERR_RESOLVE) fcRethrow(R_); AddError(r, fcRErrorOfCtx(r)); } FC_END_TRY;
}
static void fcGuardImplements(FcResolver* r, FcClassDecl* cd, FcInterfaceDecl* iface, bool onlyOwn) {
    FC_TRY(R_) { ValidateImplementsInterface(r, cd, iface, onlyOwn); } FC_CATCH(R_) { if (R_->err.kind != FC_ERR_RESOLVE) fcRethrow(R_); AddError(r, fcRErrorOfCtx(r)); } FC_END_TRY;
}

static void CollectClasses(FcResolver* r, FcList* statements) {
    int i, k, j;
    for (i = 0; i < statements->count; i++) {
        FcStmt* stmt = (FcStmt*)statements->items[i];
        r->currentSource = stmt->source;
        if (stmt->kind == SK_ClassDecl) {
            FcClassDecl* cd = (FcClassDecl*)stmt;
            if (fcDictHas(&r->classes, cd->name)) ADDERR(cd->h.line, "Class '%s' is already defined", fcPlainName(R_, cd->name));
            else fcDictPut(&r->classes, cd->name, cd);
        } else if (stmt->kind == SK_InterfaceDecl) {
            FcInterfaceDecl* id = (FcInterfaceDecl*)stmt;
            if (fcDictHas(&r->interfaces, id->name)) ADDERR(id->h.line, "Interface '%s' is already defined", id->name);
            else fcDictPut(&r->interfaces, id->name, id);
        }
    }
    FC_DICT_EACH(&r->classes, e) {
        FcClassDecl* cd = (FcClassDecl*)e->value;
        const char* baseName = NULL;
        if (!cd->baseRefs) continue;
        for (k = 0; k < cd->baseRefs->count; k++) {
            FcTypeRef* baseRef = (FcTypeRef*)cd->baseRefs->items[k];
            const char* n;
            bool isClass;
            if (fcAmbiguousFails(r, baseRef, cd->h.line, K(r, kClassOrInterfaceOrException), NamespaceOf(r, cd->name))) continue;
            n = ResolveBaseRef(r, baseRef);
            isClass = streq0(n, "Exception") || fcDictHas(&r->classes, n);
            if (isClass) {
                if (baseName != NULL) ADDERR(cd->h.line, "Class '%s' cannot have multiple base classes ('%s' and '%s')", DisplayName(r, cd), baseName, n);
                else baseName = n;
            } else if (!fcDictHas(&r->interfaces, n))
                ADDERR(cd->h.line, "'%s' of class '%s' is neither a known class nor a known interface", baseRef->baseName, DisplayName(r, cd));
        }
    }
    FC_DICT_EACH(&r->interfaces, e) {
        FcInterfaceDecl* id = (FcInterfaceDecl*)e->value;
        FcList* cycle;
        if (id->baseRefs)
            for (k = 0; k < id->baseRefs->count; k++) {
                FcTypeRef* baseRef = (FcTypeRef*)id->baseRefs->items[k];
                const char* n;
                if (fcAmbiguousFails(r, baseRef, id->h.line, K(r, kClassOrInterface), NamespaceOf(r, id->name))) continue;
                n = ResolveBaseRef(r, baseRef);
                if (!fcDictHas(&r->interfaces, n))
                    ADDERR(id->h.line, "'%s' of interface '%s' is not a known interface (an interface can only extend interfaces)", baseRef->baseName, id->name);
            }
        cycle = InterfaceCycle(r, id);
        if (cycle) {
            FcBuf b = {0};
            for (k = 0; k < cycle->count; k++) { if (k) fcBufStr(&b, " -> "); fcBufStr(&b, (const char*)cycle->items[k]); }
            ADDERR(id->h.line, "Interface '%s' extends itself (%s)", id->name, fcBufDone(R_, &b));
        }
    }
    FC_DICT_EACH(&r->classes, e) {
        FcClassDecl* cd = (FcClassDecl*)e->value;
        fcGuardStaticAndInstance(r, cd);
    }
    FC_DICT_EACH(&r->classes, e) {
        FcClassDecl* cd = (FcClassDecl*)e->value;
        FcDict* done = fcDictNew(R_, FC_KEY_STR);
        FcDict* seen = fcDictNew(R_, FC_KEY_PTR);
        FcClassDecl* b;
        if (cd->baseRefs)
            for (k = 0; k < cd->baseRefs->count; k++) {
                const char* n = ResolveBaseRef(r, (FcTypeRef*)cd->baseRefs->items[k]);
                FcList* closure;
                if (!fcDictHas(&r->interfaces, n)) continue;
                closure = InterfaceClosure(r, n);
                for (j = 0; j < closure->count; j++) {
                    FcInterfaceDecl* iface = (FcInterfaceDecl*)closure->items[j];
                    if (!fcSetAdd(done, iface->name)) continue;
                    fcGuardImplements(r, cd, iface, false);
                }
            }
        fcSetAdd(seen, cd);
        for (b = GetBaseClassDecl(r, cd); b != NULL && fcSetAdd(seen, b); b = GetBaseClassDecl(r, b)) {
            if (!b->baseRefs) continue;
            for (k = 0; k < b->baseRefs->count; k++) {
                FcTypeRef* baseRef = (FcTypeRef*)b->baseRefs->items[k];
                FcList* closure;
                if (!fcDictHas(&r->interfaces, ResolveBaseRef(r, baseRef))) continue;
                closure = InterfaceClosure(r, ResolveBaseRef(r, baseRef));
                for (j = 0; j < closure->count; j++) {
                    FcInterfaceDecl* inherited = (FcInterfaceDecl*)closure->items[j];
                    if (!fcSetAdd(done, inherited->name)) continue;
                    fcGuardImplements(r, cd, inherited, true);
                }
            }
        }
    }
}

static FcList* InterfaceClosure(FcResolver* r, const char* name) {
    FcList* result = fcListNew(R_);
    FcDict* seen = fcDictNew(R_, FC_KEY_STR);
    FcList* pending = fcListNew(R_);
    int i;
    fcListAdd(R_, pending, (void*)name);
    while (pending->count > 0) {
        const char* n = (const char*)pending->items[--pending->count];
        FcInterfaceDecl* id;
        if (!fcSetAdd(seen, n) || !(id = (FcInterfaceDecl*)fcDictGet(&r->interfaces, n))) continue;
        fcListAdd(R_, result, id);
        if (id->baseRefs) for (i = id->baseRefs->count - 1; i >= 0; i--) fcListAdd(R_, pending, (void*)ResolveBaseRef(r, (FcTypeRef*)id->baseRefs->items[i]));
    }
    return result;
}

static bool fcCycleWalk(FcResolver* r, FcInterfaceDecl* id, const char* name, FcList* path, FcDict* visited) {
    FcInterfaceDecl* decl = (FcInterfaceDecl*)fcDictGet(&r->interfaces, name);
    int k;
    if (!decl) return false;
    if (decl->baseRefs)
        for (k = 0; k < decl->baseRefs->count; k++) {
            const char* n = ResolveBaseRef(r, (FcTypeRef*)decl->baseRefs->items[k]);
            fcListAdd(R_, path, (void*)n);
            if (streq0(n, id->name)) return true;
            if (fcSetAdd(visited, n) && fcCycleWalk(r, id, n, path, visited)) return true;
            path->count--;
        }
    return false;
}
static FcList* InterfaceCycle(FcResolver* r, FcInterfaceDecl* id) {
    FcList* path = fcListNew(R_);
    fcListAdd(R_, path, (void*)id->name);
    return fcCycleWalk(r, id, id->name, path, fcDictNew(R_, FC_KEY_STR)) ? path : NULL;
}

/* ValidateStaticAndInstance */
typedef struct FcNamedMember { const char* name; bool isStatic; const char* kind; int line; } FcNamedMember;
static const char* fcBaseNameLt(FcResolver* r, const char* n) { const char* lt = strchr(n, '<'); return lt && lt > n ? fcStrN(R_, n, (size_t)(lt - n)) : n; }
static FcList* fcNamedMembers(FcResolver* r, FcClassDecl* c) {
    FcList* result = fcListNew(R_);
    FcDict* properties = fcDictNew(R_, FC_KEY_STR);
    int i;
    for (i = 0; i < c->members->count; i++) { FcStmt* m = (FcStmt*)c->members->items[i]; if (m->kind == SK_PropertyDecl) fcSetAdd(properties, ((FcPropertyDecl*)m)->name); }
    for (i = 0; i < c->members->count; i++) {
        FcStmt* m = (FcStmt*)c->members->items[i];
        FcNamedMember* nm = NULL;
        if (m->kind == SK_MethodDecl) { nm = (FcNamedMember*)fcAlloc(R_, sizeof *nm); nm->name = fcBaseNameLt(r, ((FcMethodDecl*)m)->name); nm->isStatic = ((FcMethodDecl*)m)->isStatic; nm->kind = "method"; }
        else if (m->kind == SK_PropertyDecl) { nm = (FcNamedMember*)fcAlloc(R_, sizeof *nm); nm->name = ((FcPropertyDecl*)m)->name; nm->isStatic = ((FcPropertyDecl*)m)->isStatic; nm->kind = "property"; }
        else if (m->kind == SK_FieldDecl) {
            FcFieldDecl* fd = (FcFieldDecl*)m;
            if (fcStartsWith(fd->name, "_Auto") && fcDictHas(properties, fd->name + 5)) continue;
            nm = (FcNamedMember*)fcAlloc(R_, sizeof *nm); nm->name = fd->name; nm->isStatic = fd->isStatic; nm->kind = "field";
        }
        if (nm) { nm->line = m->line; fcListAdd(R_, result, nm); }
    }
    return result;
}
_Noreturn static void StaticAndInstance(FcResolver* r, const char* cls, const char* name, const char* staticKind, const char* instanceKind, int line, int otherLine, const char* otherClass) {
    RERR(line, "'%s' of class '%s' is both a static %s and an instance %s (the other one: line %d%s) - a use through the class name and one through an object would mean different members; give one of them another name",
        name, cls, staticKind, instanceKind, otherLine, !streq0(otherClass, cls) ? fcFmt(R_, " in '%s'", otherClass) : "");
}
static void ValidateStaticAndInstance(FcResolver* r, FcClassDecl* cd) {
    FcList* own = fcNamedMembers(r, cd);
    FcDict* seen = fcDictNew(R_, FC_KEY_PTR);
    FcClassDecl* b;
    int i, k;
    for (i = 0; i < own->count; i++)
        for (k = 0; k < i; k++) {
            FcNamedMember* a = (FcNamedMember*)own->items[i];
            FcNamedMember* o = (FcNamedMember*)own->items[k];
            if (!streq0(a->name, o->name)) continue;
            if (a->isStatic != o->isStatic) {
                FcNamedMember* stat = a->isStatic ? a : o;
                FcNamedMember* inst = a->isStatic ? o : a;
                StaticAndInstance(r, cd->name, stat->name, stat->kind, inst->kind, stat->line, inst->line, cd->name);
            }
            if (!streq0(a->kind, "method") && !streq0(o->kind, "method"))
                RERR(a->line, "'%s' is declared twice in class '%s' (the other one: line %d) - a field or a property has one declaration; remove one or give it another name", a->name, cd->name, o->line);
        }
    fcSetAdd(seen, cd);
    for (b = GetBaseClassDecl(r, cd); b != NULL && fcSetAdd(seen, b); b = GetBaseClassDecl(r, b)) {
        FcList* inherited = fcNamedMembers(r, b);
        for (i = 0; i < own->count; i++)
            for (k = 0; k < inherited->count; k++) {
                FcNamedMember* member = (FcNamedMember*)own->items[i];
                FcNamedMember* other = (FcNamedMember*)inherited->items[k];
                if (streq0(other->name, member->name) && other->isStatic != member->isStatic) {
                    if (member->isStatic) StaticAndInstance(r, cd->name, member->name, member->kind, other->kind, member->line, other->line, b->name);
                    else StaticAndInstance(r, cd->name, member->name, other->kind, member->kind, member->line, other->line, b->name);
                }
            }
    }
}

/* GetBaseClassName, GetBaseClassDecl */
static const char* GetBaseClassName(FcResolver* r, FcClassDecl* cd) {
    int k;
    if (!cd->baseRefs) return NULL;
    for (k = 0; k < cd->baseRefs->count; k++) {
        const char* n = ResolveBaseRef(r, (FcTypeRef*)cd->baseRefs->items[k]);
        if (streq0(n, "Exception") || fcDictHas(&r->classes, n)) return n;
    }
    return NULL;
}
static FcClassDecl* GetBaseClassDecl(FcResolver* r, FcClassDecl* cd) {
    const char* baseName = GetBaseClassName(r, cd);
    return baseName ? (FcClassDecl*)fcDictGet(&r->classes, baseName) : NULL;
}

/* FindMemberInClassChain: the class and whether the member is static; false: none */
static bool FindMemberInClassChain(FcResolver* r, FcClassDecl* startClass, const char* name, const char** className, bool* isStatic) {
    FcDict* classList = fcDictNew(R_, FC_KEY_PTR);
    FcClassDecl* cd;
    int i;
    for (cd = startClass; cd != NULL; cd = GetBaseClassDecl(r, cd)) {
        if (!fcSetAdd(classList, cd)) {
            const char* message = fcFmt(R_, "Circular inheritance detected in class '%s'", cd->name);
            bool there = false;
            for (i = 0; i < r->errors->count; i++) {
                FcError* e = (FcError*)r->errors->items[i];
                if (e->line == cd->h.line && streq0(e->message, message)) there = true;
            }
            if (!there) fcListAdd(R_, r->errors, fcRErrorNew(r, cd->h.line, message));
            break;
        }
        for (i = 0; i < cd->members->count; i++) {
            FcStmt* m = (FcStmt*)cd->members->items[i];
            if (m->kind == SK_FieldDecl && streq0(((FcFieldDecl*)m)->name, name)) { *className = cd->name; *isStatic = ((FcFieldDecl*)m)->isStatic; return true; }
            if (m->kind == SK_MethodDecl && streq0(((FcMethodDecl*)m)->name, name)) { *className = cd->name; *isStatic = ((FcMethodDecl*)m)->isStatic; return true; }
            if (m->kind == SK_PropertyDecl && streq0(((FcPropertyDecl*)m)->name, name)) { *className = cd->name; *isStatic = ((FcPropertyDecl*)m)->isStatic; return true; }
        }
    }
    return false;
}

static bool fcListHasPtr(const FcList* l, const void* p) { int i; for (i = 0; i < l->count; i++) if (l->items[i] == p) return true; return false; }

static FcMethodDecl* ImplementingMethod(FcResolver* r, FcClassDecl* cd, const char* name, int arity);
static FcPropertyDecl* ImplementingProperty(FcResolver* r, FcClassDecl* cd, const char* name);
static bool PropertyAccessorInChain(FcResolver* r, FcClassDecl* cd, const char* name, bool getter);
static bool FieldInClassChain(FcResolver* r, FcClassDecl* cd, const char* name);
static bool ClassHasMethod(FcResolver* r, FcClassDecl* cd, const char* name, int arity);
static bool PropertyTypeConflict(FcResolver* r, FcClassDecl* cd, FcInterfaceDecl* iface, FcInterfacePropertySig* p, FcTypeRef* have, const char** haveText, const char** wantText);

static const char* fcAccessWord(FcAccess a) { return a == ACC_Private ? "private" : "protected"; }
_Noreturn static void NotPublic(FcResolver* r, FcClassDecl* cd, FcInterfaceDecl* iface, FcMethodDecl* md) {
    RERR(md->h.line, "Method '%s' of class '%s' implements interface '%s' and must be public, not %s (a member of an interface is public)", md->name, cd->name, iface->name, fcAccessWord(md->access));
}
_Noreturn static void PropertyNotPublic(FcResolver* r, FcClassDecl* cd, FcInterfaceDecl* iface, FcPropertyDecl* pd) {
    RERR(pd->h.line, "Property '%s' of class '%s' implements interface '%s' and must be public, not %s (a member of an interface is public)", pd->name, cd->name, iface->name, fcAccessWord(pd->access));
}

static void ValidateImplementsInterface(FcResolver* r, FcClassDecl* cd, FcInterfaceDecl* iface, bool onlyOwn) {
    int i;
    for (i = 0; i < iface->methods->count; i++) {
        FcInterfaceMethodSig* m = (FcInterfaceMethodSig*)iface->methods->items[i];
        FcMethodDecl* found;
        if (onlyOwn) {
            found = ImplementingMethod(r, cd, m->name, m->params->count);
            if (found && found->access != ACC_Public && fcListHasPtr(cd->members, found)) NotPublic(r, cd, iface, found);
            continue;
        }
        if (!ClassHasMethod(r, cd, m->name, m->params->count))
            RERR(cd->h.line, "Class '%s' does not implement interface '%s' completely (missing method '%s' with %d parameter(s))", cd->name, iface->name, m->name, m->params->count);
        found = ImplementingMethod(r, cd, m->name, m->params->count);
        if (found && found->access != ACC_Public) NotPublic(r, cd, iface, found);
    }
    if (iface->properties)
        for (i = 0; i < iface->properties->count; i++) {
            FcInterfacePropertySig* p = (FcInterfacePropertySig*)iface->properties->items[i];
            FcPropertyDecl* own = ImplementingProperty(r, cd, p->name);
            const char* wanted;
            bool canGet, canSet;
            const char* have;
            const char* want;
            if (onlyOwn) {
                if (own && own->access != ACC_Public && fcListHasPtr(cd->members, own)) PropertyNotPublic(r, cd, iface, own);
                continue;
            }
            wanted = p->hasGet && p->hasSet ? "get; set;" : p->hasGet ? "get;" : "set;";
            if (own == NULL) {
                if (FieldInClassChain(r, cd, p->name))
                    RERR(cd->h.line, "'%s' of class '%s' is a field - interface '%s' wants a property '%s' with '%s'", p->name, cd->name, iface->name, p->name, wanted);
                RERR(cd->h.line, "Class '%s' does not implement interface '%s' completely (missing property '%s' with '%s')", cd->name, iface->name, p->name, wanted);
            }
            canGet = PropertyAccessorInChain(r, cd, p->name, true);
            canSet = PropertyAccessorInChain(r, cd, p->name, false);
            if ((p->hasGet && !canGet) || (p->hasSet && !canSet))
                RERR(own->h.line, "Class '%s' does not implement interface '%s' completely (missing property '%s' with '%s')", cd->name, iface->name, p->name, wanted);
            if (own->access != ACC_Public) PropertyNotPublic(r, cd, iface, own);
            if (PropertyTypeConflict(r, cd, iface, p, own->type, &have, &want))
                RERR(own->h.line, "Property '%s' of class '%s' is %s, interface '%s' declares it as %s", p->name, cd->name, have, iface->name, want);
        }
}

static FcPropertyDecl* ImplementingProperty(FcResolver* r, FcClassDecl* cd, const char* name) {
    FcDict* seen = fcDictNew(R_, FC_KEY_PTR);
    FcClassDecl* c;
    int i;
    for (c = cd; c != NULL && fcSetAdd(seen, c); c = GetBaseClassDecl(r, c))
        for (i = 0; i < c->members->count; i++) {
            FcStmt* m = (FcStmt*)c->members->items[i];
            if (m->kind == SK_PropertyDecl && streq0(((FcPropertyDecl*)m)->name, name) && !((FcPropertyDecl*)m)->isStatic) return (FcPropertyDecl*)m;
        }
    return NULL;
}
static bool PropertyAccessorInChain(FcResolver* r, FcClassDecl* cd, const char* name, bool getter) {
    FcDict* seen = fcDictNew(R_, FC_KEY_PTR);
    FcClassDecl* c;
    int i;
    for (c = cd; c != NULL && fcSetAdd(seen, c); c = GetBaseClassDecl(r, c))
        for (i = 0; i < c->members->count; i++) {
            FcStmt* m = (FcStmt*)c->members->items[i];
            if (m->kind == SK_PropertyDecl) {
                FcPropertyDecl* pd = (FcPropertyDecl*)m;
                if (streq0(pd->name, name) && !pd->isStatic && (getter ? pd->getter : pd->setter) != NULL) return true;
            }
        }
    return false;
}
static bool FieldInClassChain(FcResolver* r, FcClassDecl* cd, const char* name) {
    FcDict* seen = fcDictNew(R_, FC_KEY_PTR);
    FcClassDecl* c;
    int i;
    for (c = cd; c != NULL && fcSetAdd(seen, c); c = GetBaseClassDecl(r, c))
        for (i = 0; i < c->members->count; i++) {
            FcStmt* m = (FcStmt*)c->members->items[i];
            if (m->kind == SK_FieldDecl && streq0(((FcFieldDecl*)m)->name, name) && !((FcFieldDecl*)m)->isStatic) return true;
        }
    return false;
}

static bool fcIsValueTypeName(const char* n) {
    return streq0(n, "int") || streq0(n, "float") || streq0(n, "string") || streq0(n, "bool") || streq0(n, "char") || streq0(n, "byte") || streq0(n, "word");
}
static bool TypeNameSatisfiesIsOf(FcResolver* r, const char* argName, const char* targetName);
static const char* ResolveTypeRefOrInterface(FcResolver* r, const FcTypeRef* t) {
    int count = t->typeArgs ? t->typeArgs->count : t->typeArgCount;
    return fcResolveNewTarget(R_, t, count, K(r, kKnownTypeName));
}
static const char* PtcResolvedName(FcResolver* r, const FcTypeRef* t) {
    return t->typeArgCount > 0 || (t->typeArgs && t->typeArgs->count > 0) ? ResolveTypeRefOrInterface(r, t) : fcTrResolveBaseName(R_, t, K(r, kKnownTypeName));
}
static void PtcArgText(FcResolver* r, FcBuf* b, const FcTypeRef* t) {
    int i;
    if (t->nonNull) fcBufStr(b, "defined ");
    fcBufStr(b, fcIsValueTypeName(t->baseName) ? t->baseName : PtcResolvedName(r, t));
    if (t->unit) { fcBufChar(b, ':'); fcBufStr(b, t->unit); }
    for (i = 0; i < t->arrayRank; i++) fcBufStr(b, "[]");
    if (t->typeArgs && t->typeArgs->count > 0) {
        fcBufChar(b, '<');
        for (i = 0; i < t->typeArgs->count; i++) { if (i) fcBufChar(b, ','); PtcArgText(r, b, (const FcTypeRef*)t->typeArgs->items[i]); }
        fcBufChar(b, '>');
    }
}
static void PtcPlain(FcBuf* b, const FcTypeRef* t) {
    int i;
    if (t->nonNull) fcBufStr(b, "defined ");
    if (t->lambdaSignature) fcTrText(b, t); else fcBufStr(b, t->baseName);
    if (t->typeArgs && t->typeArgs->count > 0) {
        fcBufChar(b, '<');
        for (i = 0; i < t->typeArgs->count; i++) { if (i) fcBufStr(b, ", "); PtcPlain(b, (const FcTypeRef*)t->typeArgs->items[i]); }
        fcBufChar(b, '>');
    }
    for (i = 0; i < t->arrayRank; i++) fcBufStr(b, "[]");
    if (t->unit) { fcBufStr(b, " : "); fcBufStr(b, t->unit); }
}
static const char* PtcText(FcResolver* r, const FcTypeRef* t) { FcBuf b = {0}; fcBufChar(&b, '\''); PtcPlain(&b, t); fcBufChar(&b, '\''); return fcBufDone(R_, &b); }
static bool fcHasTypeParam(FcList* tps, const char* name) {
    int i;
    if (!tps) return false;
    for (i = 0; i < tps->count; i++) if (streq0(((FcTypeParam*)tps->items[i])->name, name)) return true;
    return false;
}

/* PropertyTypeConflict: true with the two texts when the types conflict */
static bool PropertyTypeConflict(FcResolver* r, FcClassDecl* cd, FcInterfaceDecl* iface, FcInterfacePropertySig* p, FcTypeRef* have, const char** haveText, const char** wantText) {
    FcTypeRef* want = p->type;
    bool wantValue, haveValue, wantLambda, haveLambda, ok;
    const char* w;
    const char* h;
    FcList* wArgs;
    FcList* hArgs;
    int i, wc, hc;
    if (want == NULL || have == NULL || fcTrIsInferred(want) || fcTrIsInferred(have)) return false;
    if (fcHasTypeParam(iface->typeParams, want->baseName) || fcHasTypeParam(cd->typeParams, have->baseName)) return false;
    *haveText = PtcText(r, have);
    *wantText = PtcText(r, want);
    if (streq0(want->baseName, "any") && want->arrayRank == 0) return false;
    if (streq0(have->baseName, "any") && have->arrayRank == 0) return p->hasGet;
    if (want->arrayRank != have->arrayRank) return true;
    if ((p->hasGet && want->nonNull && !have->nonNull) || (p->hasSet && have->nonNull && !want->nonNull)) return true;
    wantValue = fcIsValueTypeName(want->baseName); haveValue = fcIsValueTypeName(have->baseName);
    if (wantValue || haveValue) {
        if (!streq0(want->baseName, have->baseName)) return true;
        if ((streq0(want->baseName, "int") || streq0(want->baseName, "float")) && strcmp(want->unit ? want->unit : "", have->unit ? have->unit : "") != 0) return true;
        return false;
    }
    wantLambda = want->lambdaSignature != NULL || streq0(want->baseName, "lambda");
    haveLambda = have->lambdaSignature != NULL || streq0(have->baseName, "lambda");
    if (wantLambda || haveLambda) {
        if (wantLambda != haveLambda) return true;
        if (want->lambdaSignature && have->lambdaSignature
            && (want->lambdaSignature->isExpression != have->lambdaSignature->isExpression || want->lambdaSignature->paramTypeNames->count != have->lambdaSignature->paramTypeNames->count)) return true;
        return false;
    }
    w = PtcResolvedName(r, want);
    h = PtcResolvedName(r, have);
#define FITS(from, to) (streq0((from), (to)) || TypeNameSatisfiesIsOf(r, (from), (to)))
    ok = (!p->hasGet || FITS(h, w)) && (!p->hasSet || FITS(w, h));
    if (!ok) return true;
    wArgs = want->typeArgs; hArgs = have->typeArgs;
    wc = wArgs ? wArgs->count : 0; hc = hArgs ? hArgs->count : 0;
    if (wc == 0 || hc == 0 || !streq0(h, w)) return false;
    if (wc != hc) return true;
    for (i = 0; i < wc; i++) {
        FcTypeRef* wa = (FcTypeRef*)wArgs->items[i];
        FcTypeRef* ha = (FcTypeRef*)hArgs->items[i];
        FcBuf b1 = {0}, b2 = {0};
        const char* wat;
        const char* hat;
        bool argFits;
        if (fcHasTypeParam(iface->typeParams, wa->baseName) || fcHasTypeParam(cd->typeParams, ha->baseName) || streq0(wa->baseName, "any")) continue;
        PtcArgText(r, &b1, wa); PtcArgText(r, &b2, ha);
        fcBufStr(&b1, ""); fcBufStr(&b2, "");
        wat = fcBufDone(R_, &b1); hat = fcBufDone(R_, &b2);
        if (streq0(wat, hat)) continue;
        argFits = !p->hasSet && wa->arrayRank == 0 && ha->arrayRank == 0 && !fcIsValueTypeName(wa->baseName) && FITS(PtcResolvedName(r, ha), PtcResolvedName(r, wa));
        if (!argFits) return true;
    }
#undef FITS
    return false;
}

static FcMethodDecl* ImplementingMethod(FcResolver* r, FcClassDecl* cd, const char* name, int arity) {
    FcDict* seen = fcDictNew(R_, FC_KEY_PTR);
    FcClassDecl* c;
    int i;
    for (c = cd; c != NULL && fcSetAdd(seen, c); c = GetBaseClassDecl(r, c)) {
        FcMethodDecl* first = NULL;
        for (i = 0; i < c->members->count; i++) {
            FcStmt* m = (FcStmt*)c->members->items[i];
            FcMethodDecl* md;
            if (m->kind != SK_MethodDecl) continue;
            md = (FcMethodDecl*)m;
            if (!streq0(md->name, name) || md->params->count != arity || md->isStatic) continue;
            if (md->access == ACC_Public) return md;
            if (!first) first = md;
        }
        if (first) return first;
    }
    return NULL;
}

/* SignatureKeys */
static FcList* SignatureKeys(FcResolver* r, FcList* parms, FcList* typeParamNames) {
    FcList* keys = fcListNew(R_);
    int i;
    for (i = 0; i < parms->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parms->items[i];
        FcTypeRef* t = p->type;
        const char* key;
        if (p->byRef || t == NULL || fcTrIsInferred(t) || streq0(t->baseName, "any")
            || (fcListHasStr(typeParamNames, t->baseName) && t->typeArgs == NULL && t->arrayRank == 0 && p->arrayRanks->count == 0)) key = "";
        else if (p->arrayRanks->count > 0 || t->arrayRank > 0) key = "array";
        else if (t->lambdaSignature && t->lambdaSignature->isExpression) key = "LambdaExpression";
        else if (t->lambdaSignature != NULL || streq0(t->baseName, "lambda")) key = "lambda";
        else if (streq0(t->baseName, "byte")) key = "int";
        else if (t->namespaces && t->namespaces->count > 0) {
            FcBuf b = {0};
            int k;
            for (k = 0; k < t->namespaces->count; k++) { if (k) fcBufChar(&b, '.'); fcBufStr(&b, (const char*)t->namespaces->items[k]); }
            fcBufChar(&b, '.');
            fcBufStr(&b, t->baseName);
            key = fcBufDone(R_, &b);
        } else key = t->baseName;
        fcListAdd(R_, keys, (void*)key);
    }
    return keys;
}
static bool SameSignature(FcList* a, FcList* b) {
    int i;
    if (a->count != b->count) return false;
    for (i = 0; i < a->count; i++) if (!streq0((const char*)a->items[i], (const char*)b->items[i])) return false;
    return true;
}

static bool ClassHasMethod(FcResolver* r, FcClassDecl* cd, const char* name, int arity) {
    const char* baseName;
    FcClassDecl* baseCd;
    int i;
    for (i = 0; i < cd->members->count; i++) {
        FcStmt* m = (FcStmt*)cd->members->items[i];
        if (m->kind == SK_MethodDecl && streq0(((FcMethodDecl*)m)->name, name) && ((FcMethodDecl*)m)->params->count == arity) return true;
    }
    baseName = GetBaseClassName(r, cd);
    if (baseName != NULL && (baseCd = (FcClassDecl*)fcDictGet(&r->classes, baseName)) != NULL) return ClassHasMethod(r, baseCd, name, arity);
    return false;
}

static void CheckStaticMemberExists(FcResolver* r, const char* className, FcMemberExpr* me) {
    FcClassDecl* classDecl;
    const char* foundClass;
    bool isStatic;
    if (me->target->kind == EK_SelfClassExpr || !(classDecl = (FcClassDecl*)fcDictGet(&r->classes, className))) return;
    if (fcDictHas(&r->classes, fcFmt(R_, "%s.%s", className, me->name)) || fcDictHas(&r->enums, fcFmt(R_, "%s.%s", className, me->name))) return;
    if (!FindMemberInClassChain(r, classDecl, me->name, &foundClass, &isStatic))
        RERR(me->h.line, "Class '%s' has no static member '%s'", fcPlainName(R_, className), me->name);
    if (!isStatic) RERR(me->h.line, "'%s.%s' is an instance member - it needs an object, not the class name", fcPlainName(R_, className), me->name);
}

/* ---- declared warnings ---- */
static bool HasDeclaredWarnings(FcResolver* r) {
    if (!r->declaredLoaded) {
        int i;
        r->declaredLoaded = true;
        if (R_->collecting && R_->declared->count > 0) {
            r->declaredByClass = fcDictNew(R_, FC_KEY_STR);
            for (i = 0; i < R_->declared->count; i++) {
                FcDeclaredWarning* w = (FcDeclaredWarning*)R_->declared->items[i];
                FcList* l = (FcList*)fcDictGet(r->declaredByClass, w->cls);
                if (!l) { l = fcListNew(R_); fcDictPut(r->declaredByClass, w->cls, l); }
                fcListAdd(R_, l, w);
            }
        }
    }
    return r->declaredByClass != NULL;
}
static void ReportUse(FcResolver* r, FcDeclaredWarning* w, int line) {
    const char* where;
    if (r->currentClass != NULL && streq0(fcPlainName(R_, r->currentClass->name), w->cls)) return;
    where = w->name ? fcFmt(R_, "%s.%s", w->cls, w->name) : w->cls;
    if (R_->collecting) fcCollect(R_, fcDiagNew(R_, line, fcFmt(R_, "%s (%s)", w->text, where), FC_SEV_WARNING, fcFmt(R_, "W%d", w->id), NULL, 0));
}
static void WarnForNew(FcResolver* r, const char* className, int argumentCount, int line) {
    FcList* list;
    int i;
    if (!HasDeclaredWarnings(r) || !(list = (FcList*)fcDictGet(r->declaredByClass, fcPlainName(R_, className)))) return;
    for (i = 0; i < list->count; i++) {
        FcDeclaredWarning* w = (FcDeclaredWarning*)list->items[i];
        if (w->target == FC_WARN_CLASS || (w->target == FC_WARN_CONSTRUCTOR && argumentCount >= w->minArity && argumentCount <= w->maxArity)) ReportUse(r, w, line);
    }
}
static void WarnForClassUse(FcResolver* r, const char* className, int line) {
    FcList* list;
    int i;
    if (!HasDeclaredWarnings(r) || !(list = (FcList*)fcDictGet(r->declaredByClass, fcPlainName(R_, className)))) return;
    for (i = 0; i < list->count; i++) if (((FcDeclaredWarning*)list->items[i])->target == FC_WARN_CLASS) ReportUse(r, (FcDeclaredWarning*)list->items[i], line);
}
/* arguments: FC_NULL_INT for a property or field access */
static void WarnForMember(FcResolver* r, FcList* classes, const char* name, int arguments, int line) {
    int i;
    if (!HasDeclaredWarnings(r)) return;
    FC_DICT_EACH(r->declaredByClass, e) {
        FcList* list = (FcList*)e->value;
        if (classes != NULL && !fcListHasStr(classes, (const char*)e->key)) continue;
        for (i = 0; i < list->count; i++) {
            FcDeclaredWarning* w = (FcDeclaredWarning*)list->items[i];
            bool applies;
            if (!streq0(w->name, name)) continue;
            applies = arguments != FC_NULL_INT ? w->target == FC_WARN_METHOD && arguments >= w->minArity && arguments <= w->maxArity : w->target == FC_WARN_MEMBER;
            if (applies) ReportUse(r, w, line);
        }
    }
}
static FcList* ClassChainNames(FcResolver* r, const char* className) {
    FcList* names = fcListNew(R_);
    FcClassDecl* cd;
    for (cd = (FcClassDecl*)fcDictGet(&r->classes, className); cd != NULL; cd = GetBaseClassDecl(r, cd)) fcListAdd(R_, names, (void*)fcPlainName(R_, cd->name));
    if (names->count == 0) fcListAdd(R_, names, (void*)fcPlainName(R_, className));
    return names;
}
static const char* InferClass(FcResolver* r, FcExpr* target) {
    FcResolverScope* scope;
    switch (target->kind) {
        case EK_ThisExpr: return r->currentClass ? r->currentClass->name : NULL;
        case EK_NewExpr: { FcNewExpr* ne = (FcNewExpr*)target; return IsKnownClassName(r, ne->classRef->baseName) ? ne->classRef->baseName : NULL; }
        case EK_IdentifierExpr:
            for (scope = r->current; scope != NULL; scope = scope->parent)
                if (fcDictHas(&scope->slots, ((FcIdentifierExpr*)target)->name)) return (const char*)fcDictGet(&scope->knownClasses, ((FcIdentifierExpr*)target)->name);
            return NULL;
        default: return NULL;
    }
}
static const char* KnownClassOf(FcResolver* r, FcTypeRef* type, FcExpr* initializer, bool isArray) {
    if (isArray) return NULL;
    if (type != NULL && !streq0(type->baseName, FC_INFERRED_MARKER) && IsKnownClassName(r, type->baseName)) return type->baseName;
    if ((type == NULL || streq0(type->baseName, FC_INFERRED_MARKER)) && initializer && initializer->kind == EK_NewExpr && IsKnownClassName(r, ((FcNewExpr*)initializer)->classRef->baseName))
        return ((FcNewExpr*)initializer)->classRef->baseName;
    return NULL;
}
static bool IsKnownClassName(FcResolver* r, const char* name) { return streq0(name, "Exception") || fcDictHas(&r->classes, name); }

static const char* DottedName(FcResolver* r, FcExpr* target);
static const char* InNamespaces(FcResolver* r, const char* dotted, FcKnown known, int line);
static const char* TryResolveStaticMemberAccess(FcResolver* r, FcMemberExpr* me) {
    const char* className;
    if (me->target->kind == EK_SelfClassExpr) return r->currentClass ? r->currentClass->name : NULL;
    className = DottedName(r, me->target);
    return className ? InNamespaces(r, className, K(r, kIsKnownClassName), me->h.line) : NULL;
}

static bool IsChainRoot(FcResolver* r, const char* name);
static bool IsVariableName(FcResolver* r, const char* name);
static const char* InNamespaces(FcResolver* r, const char* dotted, FcKnown known, int line) {
    const char* dot;
    const char* root;
    const char* ns;
    if (fcStartsWith(dotted, "global.") && !IsVariableName(r, "global")) return fcKnows(known, dotted + 7) ? dotted + 7 : NULL;
    dot = strchr(dotted, '.');
    root = dot ? fcStrN(R_, dotted, (size_t)(dot - dotted)) : dotted;
    if (IsChainRoot(r, root)) return NULL;
    for (ns = NamespaceOf(r, r->currentClass ? r->currentClass->name : NULL); ns && ns[0]; ns = NamespaceOf(r, ns)) {
        const char* cand = fcFmt(R_, "%s.%s", ns, dotted);
        if (fcKnows(known, cand)) return cand;
    }
    if (fcKnows(known, dotted)) return dotted;
    if (R_->sourceImportNamespaces && r->currentSource >= 0 && r->currentSource < R_->sourceImportNamespaces->count && R_->sourceImportNamespaces->items[r->currentSource]) {
        FcList* importNamespaces = (FcList*)R_->sourceImportNamespaces->items[r->currentSource];
        FcList* found = fcListNew(R_);
        int i;
        for (i = 0; i < importNamespaces->count; i++) {
            const char* cand = fcFmt(R_, "%s.%s", (const char*)importNamespaces->items[i], dotted);
            if (fcKnows(known, cand)) fcListAdd(R_, found, (void*)cand);
        }
        if (found->count > 1) {
            FcBuf b = {0};
            const char* message;
            bool there = false;
            for (i = 0; i < found->count; i++) { if (i) fcBufStr(&b, ", "); fcBufFmt(&b, "'%s'", (const char*)found->items[i]); }
            message = fcFmt(R_, "'%s' is in several imported libraries (%s) - write the full name", root, fcBufDone(R_, &b));
            for (i = 0; i < r->errors->count; i++) if (streq0(((FcError*)r->errors->items[i])->message, message)) there = true;
            if (!there) AddError(r, fcRErrorNew(r, line, message));
        }
        if (found->count > 0) return (const char*)found->items[0];
    }
    return NULL;
}
static bool IsChainRoot(FcResolver* r, const char* name) {
    FcResolverScope* s;
    FcDict* visited = fcDictNew(R_, FC_KEY_PTR);
    FcClassDecl* cd;
    int i;
    for (s = r->current; s != NULL; s = s->parent) if (!s->isGlobal && fcDictHas(&s->slots, name)) return true;
    for (cd = r->currentClass; cd != NULL && fcSetAdd(visited, cd); cd = GetBaseClassDecl(r, cd))
        for (i = 0; i < cd->members->count; i++) {
            FcStmt* m = (FcStmt*)cd->members->items[i];
            if (m->kind == SK_FieldDecl && streq0(((FcFieldDecl*)m)->name, name)) return ((FcFieldDecl*)m)->isStatic || !r->inStaticMethod;
            if (m->kind == SK_PropertyDecl && streq0(((FcPropertyDecl*)m)->name, name)) return ((FcPropertyDecl*)m)->isStatic || !r->inStaticMethod;
        }
    return false;
}
static bool IsVariableName(FcResolver* r, const char* name) {
    FcResolverScope* s;
    const char* cls;
    bool st;
    for (s = r->current; s != NULL; s = s->parent) if (!s->isGlobal && fcDictHas(&s->slots, name)) return true;
    return r->currentClass != NULL && FindMemberInClassChain(r, r->currentClass, name, &cls, &st);
}
static const char* DottedName(FcResolver* r, FcExpr* target) {
    FcList* segments = fcListNew(R_);
    FcExpr* current = target;
    FcBuf b = {0};
    int i;
    while (current->kind == EK_MemberExpr) { fcListAdd(R_, segments, (void*)((FcMemberExpr*)current)->name); current = ((FcMemberExpr*)current)->target; }
    if (current->kind != EK_IdentifierExpr) return NULL;
    fcListAdd(R_, segments, (void*)((FcIdentifierExpr*)current)->name);
    for (i = segments->count - 1; i >= 0; i--) { fcBufStr(&b, (const char*)segments->items[i]); if (i) fcBufChar(&b, '.'); }
    return fcBufDone(R_, &b);
}

/* Unit.Parse(a).IsCompatibleWith(Unit.Parse(b)) */
static bool fcUnitsCompatible(FcCtx* c, const char* a, const char* b) {
    FcUnit ua, ub;
    fcUnitParse(c, a, &ua);
    fcUnitParse(c, b, &ub);
    return fcUnitCompatible(&ua, &ub);
}

static void CheckTypeArgs(FcResolver* r, FcClassDecl* targetClass, FcNewExpr* ne) {
    FcList* typeParams = targetClass->typeParams;
    int tpCount = typeParams ? typeParams->count : 0;
    const char* className = fcPlainName(R_, targetClass->name);
    FcList* typeArgs;
    int i, g, k;
    if (tpCount == 0) {
        if (ne->typeArgs && ne->typeArgs->count > 0) RERR(ne->h.line, "Class '%s' is not generic, so it does not accept type arguments in angle brackets", className);
        return;
    }
    typeArgs = ne->typeArgs;
    if (!typeArgs || typeArgs->count == 0) return;
    if (typeArgs->count != tpCount)
        RERR(ne->h.line, "Class '%s' is generic with %d type parameter(s) - 'new %s<...>' needs explicit type arguments for it (got: %d)", className, tpCount, className, typeArgs->count);
    for (i = 0; i < tpCount; i++) {
        FcTypeParam* tp = (FcTypeParam*)typeParams->items[i];
        const char* arg = (const char*)typeArgs->items[i];
        bool satisfied = false;
        if (tp->constraintGroups->count == 0) continue;
        for (g = 0; g < tp->constraintGroups->count && !satisfied; g++) {
            FcTypeConstraintGroup* group = (FcTypeConstraintGroup*)tp->constraintGroups->items[g];
            bool all = true;
            for (k = 0; k < group->constraints->count && all; k++) {
                FcTypeConstraint* tc = (FcTypeConstraint*)group->constraints->items[k];
                all = tc->kind == TCK_IsOf ? TypeNameSatisfiesIsOf(r, arg, tc->name) : fcUnitsCompatible(R_, arg, tc->name);
            }
            satisfied = all;
        }
        if (!satisfied) RERR(ne->h.line, "Type argument '%s' for type parameter '%s' of '%s' does not satisfy any of the 'where' conditions", arg, tp->name, className);
    }
}

static bool TypeNameSatisfiesIsOf(FcResolver* r, const char* argName, const char* targetName) {
    FcClassDecl* cd;
    int i;
    if (streq0(argName, targetName)) return true;
    if (!fcDictHas(&r->classes, argName) && fcDictHas(&r->interfaces, argName)) {
        FcList* closure = InterfaceClosure(r, argName);
        for (i = 0; i < closure->count; i++) if (streq0(((FcInterfaceDecl*)closure->items[i])->name, targetName)) return true;
        return false;
    }
    if (!(cd = (FcClassDecl*)fcDictGet(&r->classes, argName))) return false;
    if (cd->baseRefs)
        for (i = 0; i < cd->baseRefs->count; i++)
            if (TypeNameSatisfiesIsOf(r, ResolveBaseRef(r, (FcTypeRef*)cd->baseRefs->items[i]), targetName)) return true;
    return false;
}

static bool IsEnumTypeName(FcResolver* r, const char* name) {
    const char* ns;
    if (fcDictHas(&r->enums, name)) return true;
    ns = NamespaceOf(r, r->currentClass ? r->currentClass->name : NULL);
    return ns && ns[0] && fcDictHas(&r->enums, fcFmt(R_, "%s.%s", ns, name));
}

static bool fcIsPrimitiveTypeName(const char* n) {
    static const char* const names[] = { "bool", "int", "float", "char", "string", "class", "undefined", "lambda", "any", "word" };
    size_t i;
    for (i = 0; i < sizeof names / sizeof names[0]; i++) if (streq0(n, names[i])) return true;
    return false;
}
static void ValidateTypeRef(FcResolver* r, FcTypeRef* type, int line);
static void ValidateTypeArgs(FcResolver* r, FcTypeRef* tr, FcList* typeArgs, int line);
static void ValidateTypeName(FcResolver* r, FcTypeRef* tr, int line) {
    int i;
    if (fcTrIsInferred(tr)) return;
    if (tr->lambdaSignature && tr->lambdaSignature->isSelector) {
        FcLambdaSignature* selector = tr->lambdaSignature;
        if (!fcDictHas(&r->classes, "Reflect")) RERR(line, "'lambda %s<...>' (selector) needs the reflection library: #import \"reflection\"", selector->selectorKind);
        for (i = 0; i < selector->paramTypeNames->count; i++) {
            const char* target = (const char*)selector->paramTypeNames->items[i];
            if (!fcIsPrimitiveTypeName(target) && !fcDictHas(&r->currentTypeParamNames, target) && !IsKnownClassName(r, target))
                RERR(line, "Unknown type '%s' in 'lambda %s<%s>'", target, selector->selectorKind, target);
        }
        return;
    }
    if (fcIsPrimitiveTypeName(tr->baseName)) return;
    if (fcDictHas(&r->currentTypeParamNames, tr->baseName)) return;
    if (IsEnumTypeName(r, tr->baseName)) return;
    CheckAmbiguous(r, tr, line, K(r, kKnownClassOrInterface), NamespaceOf(r, r->currentClass ? r->currentClass->name : NULL));
    if (!IsKnownClassName(r, ResolveTypeRef(r, tr)) && !fcDictHas(&r->interfaces, ResolveBaseRef(r, tr))) RERR(line, "Unknown type '%s'", tr->baseName);
    if (tr->typeArgs && tr->typeArgs->count > 0) ValidateTypeArgs(r, tr, tr->typeArgs, line);
}
static void ValidateTypeArgs(FcResolver* r, FcTypeRef* tr, FcList* typeArgs, int line) {
    const char* name = ResolveTypeRef(r, tr);
    FcClassDecl* cd = (FcClassDecl*)fcDictGet(&r->classes, name);
    FcInterfaceDecl* id;
    int parameters, i;
    const char* plain;
    if (cd) parameters = cd->typeParams ? cd->typeParams->count : 0;
    else if ((id = (FcInterfaceDecl*)fcDictGet(&r->interfaces, ResolveBaseRef(r, tr))) != NULL) parameters = id->typeParams ? id->typeParams->count : 0;
    else parameters = 0;
    plain = fcPlainName(R_, name);
    if (parameters == 0) RERR(line, "'%s' is not generic, so it does not accept type arguments in angle brackets", plain);
    if (parameters != typeArgs->count) RERR(line, "'%s' is generic with %d type parameter(s), not %d", plain, parameters, typeArgs->count);
    for (i = 0; i < typeArgs->count; i++) {
        FcTypeRef* arg = (FcTypeRef*)typeArgs->items[i];
        if (arg->nonNull) RERR(line, "'defined %s' cannot be a type argument - 'defined' belongs to variables, parameters, fields, properties and return types", arg->baseName);
        ValidateTypeRef(r, arg, line);
    }
}

static void ValidateNonNull(FcResolver* r, FcTypeRef* type, int arrayRanks, int line) {
    bool classArray;
    const char* b;
    bool anyLike;
    if (!type || !type->nonNull) return;
    b = type->baseName;
    anyLike = streq0(b, "any") || streq0(b, "class") || streq0(b, "var") || streq0(b, "undefined") || streq0(b, "lambda");
    classArray = arrayRanks > 0 && type->pointerDepth == 0 && ((type->lambdaSignature && !type->lambdaSignature->isSelector && !type->lambdaSignature->isExpression)
        || (type->lambdaSignature == NULL && !anyLike));
    if (type->arrayRank > 0 || (arrayRanks > 0 && fcArrayElemOf(type) == 0 && !classArray))
        RERR(line, "'defined %s' is a value that is never undefined - an array of them is written 'defined %s name[]' (int, float, bool, char, byte, word)", b, b);
    if (arrayRanks == 0 && (type->lambdaSignature != NULL || anyLike || fcTrIsInferred(type)))
        RERR(line, "'defined' needs a type that says what the value is - '%s' takes anything (write 'defined int', 'defined string', a class, an interface or an enum)", b);
}

static bool fcSetsField(FcExpr* e, const char* name) {
    FcAssignExpr* a;
    if (!e || e->kind != EK_AssignExpr) return false;
    a = (FcAssignExpr*)e;
    if (a->target->kind == EK_IdentifierExpr && streq0(((FcIdentifierExpr*)a->target)->name, name)) return true;
    return a->target->kind == EK_MemberExpr && ((FcMemberExpr*)a->target)->target->kind == EK_ThisExpr && streq0(((FcMemberExpr*)a->target)->name, name);
}
static void CheckDefinedObjectField(FcResolver* r, FcClassDecl* cd, FcFieldDecl* fd) {
    const char* written = fcFmt(R_, "'defined %s %s'", fd->type->baseName, fd->name);
    FcList* ctors = fcListNew(R_);
    int i, k;
    if (fd->isStatic) RERR(fd->h.line, "%s needs a value: an object has no zero to start with (write 'static defined %s %s = ...')", written, fd->type->baseName, fd->name);
    for (i = 0; i < cd->members->count; i++) if (((FcStmt*)cd->members->items[i])->kind == SK_ConstructorDecl) fcListAdd(R_, ctors, cd->members->items[i]);
    if (ctors->count == 0) RERR(fd->h.line, "%s needs a value: an object has no zero to start with - give it an initializer or set it in a constructor", written);
    for (i = 0; i < ctors->count; i++) {
        FcConstructorDecl* ctor = (FcConstructorDecl*)ctors->items[i];
        bool any = false;
        for (k = 0; k < ctor->body->statements->count && !any; k++) {
            FcStmt* st = (FcStmt*)ctor->body->statements->items[k];
            if (st->kind == SK_ExprStmt && fcSetsField(((FcExprStmt*)st)->expression, fd->name)) any = true;
            else if (st->kind == SK_IfStmt && fcSetsField(((FcIfStmt*)st)->condition, fd->name)) any = true;
        }
        if (!any) RERR(ctor->h.line, "%s has no zero to start with: the constructor in line %d must set it (this.%s = ...), or the field needs an initializer", written, ctor->h.line, fd->name);
    }
}
static bool HasNoZero(FcResolver* r, FcTypeRef* type, int arrayRanks) {
    return type && type->nonNull && type->pointerDepth == 0 && type->arrayRank == 0 && type->lambdaSignature == NULL && arrayRanks == 0 && fcDeclaredKindOf(type) == 0
        && !streq0(type->baseName, "string") && !IsEnumTypeName(r, type->baseName);
}
static void ValidateTypeRef(FcResolver* r, FcTypeRef* type, int line) {
    ValidateTypeName(r, type, line);
    if (type->bitWidth == FC_NULL_INT) return;
    if (!streq0(type->baseName, "int") && !streq0(type->baseName, "float")) RERR(line, "A bit width is only valid for 'int'/'float', not for '%s'", type->baseName);
    if (!(type->bitWidth == 8 || type->bitWidth == 16 || type->bitWidth == 32 || type->bitWidth == 64)) RERR(line, "Invalid bit width %d (allowed: 8/16/32/64)", type->bitWidth);
}
static void ResolveArrayRanks(FcResolver* r, FcList* ranks) {
    int i;
    for (i = 0; i < ranks->count; i++) if (ranks->items[i]) ResolveExpr(r, (FcExpr*)ranks->items[i]);
}

/* ---- scopes ---- */
static void PushScope(FcResolver* r) { r->current = fcScopeNew(r, r->current, false); }
static void PopScope(FcResolver* r) { r->current = r->current->parent; }

static const char* fcUnitDemandOf(FcCtx* c, const FcTypeRef* t);

static void Define(FcResolver* r, const char* name, int line, bool isReadonly, const char* requiredUnit, bool isRef, int width, FcTypeRef* arrayElem, int arrayRank) {
    FcResolverScope* cur = r->current;
    if (fcDictHas(&cur->slots, name)) {
        void* captureSlot;
        if (!fcDictRemove(&cur->captureNames, name)) RERR(line, "'%s' is already declared in this scope", name);
        captureSlot = fcDictGet(&cur->slots, name);
        fcDictRemove(&cur->slots, name);
        fcDictPut(&cur->slots, fcFmt(R_, "\x01" "capture:%s", name), captureSlot);
        fcDictRemove(&cur->readonlySlots, name);
        fcDictRemove(&cur->requiredUnits, name);
        fcDictRemove(&cur->refNames, name);
        fcDictRemove(&cur->widths, name);
        fcDictRemove(&cur->arrayDecls, name);
    }
    fcDictPut(&cur->slots, name, FC_IVAL(fcDictCount(&cur->slots)));
    if (isRef) fcSetAdd(&cur->refNames, name);
    if (isReadonly) fcSetAdd(&cur->readonlySlots, name);
    if (requiredUnit != NULL) fcDictPut(&cur->requiredUnits, name, (void*)requiredUnit);
    if (width != 0) fcDictPut(&cur->widths, name, FC_IVAL(width));
    if (arrayElem != NULL && arrayRank > 0) {
        FcArrayDecl* ad = (FcArrayDecl*)fcAlloc(R_, sizeof *ad);
        ad->type = arrayElem; ad->rank = arrayRank;
        fcDictPut(&cur->arrayDecls, name, ad);
    }
}
#define DEFINE_PLAIN(name, line) Define(r, (name), (line), false, NULL, false, 0, NULL, 0)

static FcResolvedRef* fcGlobalRef(FcResolver* r, FcResolverScope* global, int slot, const char* name) {
    FcResolvedRef* ref = fcRef(R_, RR_Global);
    FcArrayDecl* ad = (FcArrayDecl*)fcDictGet(&global->arrayDecls, name);
    ref->slot = slot;
    ref->requiredUnit = (const char*)fcDictGet(&global->requiredUnits, name);
    ref->width = FC_VALI(fcDictGet(&global->widths, name));
    ref->arrayElem = ad ? ad->type : NULL;
    ref->arrayRank = ad ? ad->rank : 0;
    return ref;
}

static FcResolvedRef* ResolveIdentifierRef(FcResolver* r, const char* name, int line) {
    int depth = 0, globalSlot = -1;
    FcResolverScope* scope = r->current;
    FcResolverScope* global = NULL;
    const char* memberClass;
    bool memberStatic;
    while (scope != NULL) {
        void* slotV;
        if (fcDictTry(&scope->slots, name, &slotV)) {
            FcResolvedRef* ref;
            FcArrayDecl* ad;
            if (scope->isGlobal) { global = scope; globalSlot = FC_VALI(slotV); break; }
            ref = fcRef(R_, RR_Local);
            ad = (FcArrayDecl*)fcDictGet(&scope->arrayDecls, name);
            ref->depth = depth;
            ref->slot = FC_VALI(slotV);
            ref->requiredUnit = (const char*)fcDictGet(&scope->requiredUnits, name);
            ref->byRef = fcDictHas(&scope->refNames, name);
            ref->width = FC_VALI(fcDictGet(&scope->widths, name));
            ref->arrayElem = ad ? ad->type : NULL;
            ref->arrayRank = ad ? ad->rank : 0;
            return ref;
        }
        depth++;
        scope = scope->parent;
    }
    if (r->currentClass != NULL && FindMemberInClassChain(r, r->currentClass, name, &memberClass, &memberStatic)) {
        FcResolvedRef* ref;
        if (memberStatic) { ref = fcRef(R_, RR_StaticMember); ref->className = memberClass; return ref; }
        if (r->fireScope != NULL && !r->inBoundLambda) {
            if (global != NULL) return fcGlobalRef(r, global, globalSlot, name);
            RERR(line, "'%s' is an instance member - a 'fire' block runs without 'this': give the thread what it needs with 'taking' or 'with'", name);
        }
        if (!r->inStaticMethod) return fcRef(R_, RR_ImplicitThisMember);
        if (global == NULL) RERR(line, "'%s' is an instance member - it is not reachable in a static method or static field initializer without a bound 'this'", name);
    }
    if (global != NULL) return fcGlobalRef(r, global, globalSlot, name);
    if (fcDictHas(r->nativeNames, name)) { FcResolvedRef* ref = fcRef(R_, RR_Native); ref->name = name; return ref; }
    if (fcDictHas(&r->externs, name)) { FcResolvedRef* ref = fcRef(R_, RR_Extern); ref->name = name; return ref; }
    if (fcDictHas(r->tryableNativeNames, name)) RERR(line, "'%s' is registered as 'tryable' - it can only be called with 'try %s(...)', not as a direct call.", name, name);
    if (r->inBoundLambda) return fcRef(R_, RR_ImplicitThisMember);
    RERR(line, "Unknown identifier '%s'", name);
}

FC_LATER static bool IsCapturedVariable(FcResolver* r, const char* name) {
    FcResolverScope* scope;
    for (scope = r->current; scope != NULL; scope = scope->parent) if (fcDictHas(&scope->slots, name)) return fcDictHas(&scope->captureNames, name);
    return false;
}
static bool IsReadonlyVariable(FcResolver* r, const char* name) {
    FcResolverScope* scope;
    for (scope = r->current; scope != NULL; scope = scope->parent) if (fcDictHas(&scope->slots, name)) return fcDictHas(&scope->readonlySlots, name);
    return false;
}

/* ---- statements ---- */
typedef struct FcResolverState {
    FcResolverScope* current;
    int functionDepth, loopDepth;
    FcIntStack* loopFinallyDepths;
    int tryDepth, unsafeDepth;
    FcClassDecl* currentClass;
    bool currentClassHasBase, inConstructor, inStaticMethod;
    FcDict* typeParamNames;
} FcResolverState;

static FcResolverState SaveState(FcResolver* r) {
    FcResolverState s;
    s.current = r->current; s.functionDepth = r->functionDepth; s.loopDepth = r->loopDepth; s.loopFinallyDepths = r->loopFinallyDepths;
    s.tryDepth = r->tryDepth; s.unsafeDepth = r->unsafeDepth; s.currentClass = r->currentClass; s.currentClassHasBase = r->currentClassHasBase;
    s.inConstructor = r->inConstructor; s.inStaticMethod = r->inStaticMethod;
    s.typeParamNames = fcDictCount(&r->currentTypeParamNames) > 0 ? fcDictCopy(R_, &r->currentTypeParamNames) : NULL;
    return s;
}
static void RestoreState(FcResolver* r, const FcResolverState* s) {
    r->current = s->current; r->functionDepth = s->functionDepth; r->loopDepth = s->loopDepth; r->loopFinallyDepths = s->loopFinallyDepths;
    r->tryDepth = s->tryDepth; r->unsafeDepth = s->unsafeDepth; r->currentClass = s->currentClass; r->currentClassHasBase = s->currentClassHasBase;
    r->inConstructor = s->inConstructor; r->inStaticMethod = s->inStaticMethod;
    fcDictClear(&r->currentTypeParamNames);
    if (s->typeParamNames) FC_DICT_EACH(s->typeParamNames, e) fcDictPut(&r->currentTypeParamNames, e->key, e->value);
}

static void AddTypeParamNames(FcResolver* r, FcList* names) {
    int i;
    for (i = 0; i < names->count; i++) {
        const char* n = (const char*)names->items[i];
        fcDictPut(&r->currentTypeParamNames, n, FC_IVAL(FC_VALI(fcDictGet(&r->currentTypeParamNames, n)) + 1));
    }
}
static void RemoveTypeParamNames(FcResolver* r, FcList* names) {
    int i;
    for (i = 0; i < names->count; i++) {
        const char* n = (const char*)names->items[i];
        int count = FC_VALI(fcDictGet(&r->currentTypeParamNames, n)) - 1;
        if (count <= 0) fcDictRemove(&r->currentTypeParamNames, n);
        else fcDictPut(&r->currentTypeParamNames, n, FC_IVAL(count));
    }
}

static void ResolveStmtCore(FcResolver* r, FcStmt* stmt);
static void ResolveStmt(FcResolver* r, FcStmt* stmt) {
    FcResolverState state = SaveState(r);
    FC_TRY(R_) {
        ResolveStmtCore(r, stmt);
    } FC_CATCH(R_) {
        if (R_->err.kind != FC_ERR_RESOLVE) fcRethrow(R_);
        AddError(r, fcRErrorOfCtx(r));
        RestoreState(r, &state);
        if (stmt->kind == SK_VarDeclStmt && !fcDictHas(&r->current->slots, ((FcVarDeclStmt*)stmt)->name)) {
            FcVarDeclStmt* vd = (FcVarDeclStmt*)stmt;
            Define(r, vd->name, vd->h.line, vd->isReadonly, vd->type ? fcUnitDemandOf(R_, vd->type) : NULL, false,
                vd->arrayRanks->count == 0 ? (vd->type ? fcTrWidthCode(vd->type) : 0) : (vd->type ? fcTrArrayWidthCode(vd->type, vd->arrayRanks->count) : 0),
                vd->type, vd->arrayRanks->count);
        }
    } FC_END_TRY;
}

static FcIntStack* fcPush(FcResolver* r, FcIntStack* s, int v) {
    FcIntStack* n = (FcIntStack*)fcAlloc(R_, sizeof *n);
    n->value = v; n->next = s; n->count = s ? s->count + 1 : 1;
    return n;
}

static void ResolveBlockNewScope(FcResolver* r, FcBlockStmt* block);
static void ResolveStmtAsScope(FcResolver* r, FcStmt* body);
static void ResolveFor(FcResolver* r, FcForStmt* fs);
static void ResolveForeach(FcResolver* r, FcForeachStmt* fe);
static void ResolveTry(FcResolver* r, FcTryStmt* t);
static void ResolveFireStmt(FcResolver* r, FcFireStmt* fs);
static void ResolveGlobalHandlerBody(FcResolver* r, const char* varName, FcBlockStmt* body);
static void ResolveClass(FcResolver* r, FcClassDecl* cd);
static void CheckLoopLevels(FcResolver* r, const char* keyword, int levels, int line);

static void fcLoopBody(FcResolver* r, FcStmt* body) {
    int saved;
    r->loopDepth++;
    saved = r->tryDepth; r->tryDepth = 0;
    r->loopFinallyDepths = fcPush(r, r->loopFinallyDepths, saved);
    ResolveStmtAsScope(r, body);
    r->loopFinallyDepths = r->loopFinallyDepths->next;
    r->tryDepth = saved;
    r->loopDepth--;
}

static void ResolveStmtCore(FcResolver* r, FcStmt* stmt) {
    int i;
    switch (stmt->kind) {
        case SK_BlockStmt: ResolveBlockNewScope(r, (FcBlockStmt*)stmt); break;
        case SK_ExprStmt: {
            FcExprStmt* es = (FcExprStmt*)stmt;
            ResolveExpr(r, es->expression);
            if (es->expression->kind == EK_AssignExpr && ((FcAssignExpr*)es->expression)->replace && ((FcAssignExpr*)es->expression)->target->kind == EK_IdentifierExpr) {
                FcIdentifierExpr* replaced = (FcIdentifierExpr*)((FcAssignExpr*)es->expression)->target;
                FcResolvedRef* ref = (FcResolvedRef*)fcDictGet(&r->refs, replaced);
                if (ref && ref->kind != RR_ImplicitThisMember && ref->kind != RR_StaticMember)
                    ADDERR(es->h.line, "'replace' assigns a field - '%s' is a variable (its old value goes with its scope; to destroy it now: 'delete %s' and then the assignment)", replaced->name, replaced->name);
            }
            break;
        }
        case SK_NoOpStmt: case SK_NoShadowDirective: case SK_TypeCheckDirective: case SK_GlobalJobsDirective: break;
        case SK_EndWaitDirective: case SK_TimeoutDirective: ResolveExpr(r, ((FcTimeoutDirective*)stmt)->value); break;
        case SK_VarDeclStmt: {
            FcVarDeclStmt* vd = (FcVarDeclStmt*)stmt;
            const char* declaredClass;
            if (vd->initializer) ResolveExprAllowTake(r, vd->initializer);
            if (vd->type) ValidateTypeRef(r, vd->type, vd->h.line);
            ValidateNonNull(r, vd->type, vd->arrayRanks->count, vd->h.line);
            if (vd->initializer == NULL && HasNoZero(r, vd->type, vd->arrayRanks->count))
                RERR(vd->h.line, "'defined %s %s' needs a value: an object has no zero to start with (write 'defined %s %s = ...')", vd->type->baseName, vd->name, vd->type->baseName, vd->name);
            ResolveArrayRanks(r, vd->arrayRanks);
            if (vd->initializer == NULL && vd->arrayRanks->count == 0 && (vd->type == NULL || fcTrIsInferred(vd->type)))
                RERR(vd->h.line, "'var %s' needs an initializer: the type of a 'var' comes from its value (or declare it with a type: 'int %s')", vd->name, vd->name);
            if (vd->isReadonly && vd->initializer == NULL && !(vd->arrayRanks->count > 0 && vd->arrayRanks->items[0] != NULL))
                RERR(vd->h.line, "'readonly %s' needs an initializer (or an array size)", vd->name);
            if (vd->initializer && vd->initializer->kind == EK_ArrayLiteralExpr && vd->arrayRanks->count > 0 && vd->arrayRanks->items[0]
                && ((FcExpr*)vd->arrayRanks->items[0])->kind == EK_LiteralExpr && ((FcLiteralExpr*)vd->arrayRanks->items[0])->value.kind == VK_Int
                && ((FcLiteralExpr*)vd->arrayRanks->items[0])->value.i != ((FcArrayLiteralExpr*)vd->initializer)->elements->count) {
                int64_t size = ((FcLiteralExpr*)vd->arrayRanks->items[0])->value.i;
                RERR(vd->h.line, "'%s[%" PRId64 "]' expects %" PRId64 " elements, but the array literal initializer has %d", vd->name, size, size, ((FcArrayLiteralExpr*)vd->initializer)->elements->count);
            }
            Define(r, vd->name, vd->h.line, vd->isReadonly, vd->type ? fcUnitDemandOf(R_, vd->type) : NULL, false,
                vd->arrayRanks->count == 0 ? (vd->type ? fcTrWidthCode(vd->type) : 0) : (vd->type ? fcTrArrayWidthCode(vd->type, vd->arrayRanks->count) : 0),
                vd->type, vd->arrayRanks->count);
            if ((declaredClass = KnownClassOf(r, vd->type, vd->initializer, vd->arrayRanks->count > 0)) != NULL) fcDictPut(&r->current->knownClasses, vd->name, (void*)declaredClass);
            break;
        }
        case SK_IfStmt: {
            FcIfStmt* ifs = (FcIfStmt*)stmt;
            ResolveExpr(r, ifs->condition);
            ResolveStmtAsScope(r, ifs->then);
            if (ifs->otherwise) ResolveStmtAsScope(r, ifs->otherwise);
            break;
        }
        case SK_WhileStmt: ResolveExpr(r, ((FcWhileStmt*)stmt)->condition); fcLoopBody(r, ((FcWhileStmt*)stmt)->body); break;
        case SK_DoWhileStmt: fcLoopBody(r, ((FcDoWhileStmt*)stmt)->body); ResolveExpr(r, ((FcDoWhileStmt*)stmt)->condition); break;
        case SK_ForStmt: ResolveFor(r, (FcForStmt*)stmt); break;
        case SK_ForeachStmt: ResolveForeach(r, (FcForeachStmt*)stmt); break;
        case SK_ReturnStmt:
            if (r->functionDepth == 0) RERR(stmt->line, "'return' outside of a function/method");
            if (((FcReturnStmt*)stmt)->value) ResolveExpr(r, ((FcReturnStmt*)stmt)->value);
            break;
        case SK_ThrowStmt: ResolveExpr(r, ((FcThrowStmt*)stmt)->value); break;
        case SK_DeleteStmt: ResolveExpr(r, ((FcDeleteStmt*)stmt)->target); break;
        case SK_TryStmt: ResolveTry(r, (FcTryStmt*)stmt); break;
        case SK_BreakStmt:
            if (r->loopDepth == 0) RERR(stmt->line, "'break' outside of a loop ('while'/'for'/'foreach')");
            CheckLoopLevels(r, "break", ((FcBreakStmt*)stmt)->levels, stmt->line);
            break;
        case SK_ContinueStmt:
            if (r->loopDepth == 0) RERR(stmt->line, "'continue' outside of a loop ('while'/'for'/'foreach')");
            CheckLoopLevels(r, "continue", ((FcContinueStmt*)stmt)->levels, stmt->line);
            break;
        case SK_FireStmt: ResolveFireStmt(r, (FcFireStmt*)stmt); break;
        case SK_SectionEnterStmt: case SK_SectionExitStmt: break;
        case SK_SilenceStmt: ResolveExpr(r, ((FcSilenceStmt*)stmt)->target); break;
        case SK_PostGlobalStmt: {
            FcPostGlobalStmt* pg = (FcPostGlobalStmt*)stmt;
            for (i = 0; i < pg->args->count; i++) ResolveExprAllowTake(r, (FcExpr*)pg->args->items[i]);
            ResolveLambda(r, (FcLambdaExpr*)pg->lambda);
            break;
        }
        case SK_LeaveStmt: break;
        case SK_TerminateStmt: if (((FcTerminateStmt*)stmt)->value) ResolveExpr(r, ((FcTerminateStmt*)stmt)->value); break;
        case SK_CatchThreadsDecl: {
            FcCatchThreadsDecl* td = (FcCatchThreadsDecl*)stmt;
            if (td->typeRef && !IsKnownClassName(r, ResolveTypeRef(r, td->typeRef))) ADDERR(td->h.line, "Unknown exception type '%s'", td->typeRef->baseName);
            ResolveGlobalHandlerBody(r, td->varName, td->body);
            break;
        }
        case SK_CatchTerminateDecl: ResolveGlobalHandlerBody(r, ((FcCatchTerminateDecl*)stmt)->varName, ((FcCatchTerminateDecl*)stmt)->body); break;
        case SK_ProcessStmt: ResolveExpr(r, ((FcProcessStmt*)stmt)->target); break;
        case SK_ClassDecl: ResolveClass(r, (FcClassDecl*)stmt); break;
        case SK_InterfaceDecl: case SK_EnumDecl: break;
        case SK_ClassExtensionDecl:
            RERR(stmt->line, "Internal error: 'class extends %s' was not merged (the program must be produced by Parser.Parse()/ParseMultiple()).", ((FcClassExtensionDecl*)stmt)->targetRef->baseName);
        case SK_ExternDecl: {
            FcExternDecl* ed = (FcExternDecl*)stmt;
            if (ed->returnType) ValidateTypeRef(r, ed->returnType, ed->h.line);
            for (i = 0; i < ed->params->count; i++) {
                FcLambdaParam* p = (FcLambdaParam*)ed->params->items[i];
                if (p->type) ValidateTypeRef(r, p->type, ed->h.line);
                ResolveArrayRanks(r, p->arrayRanks);
            }
            break;
        }
        case SK_UnsafeStmt: r->unsafeDepth++; ResolveBlockNewScope(r, ((FcUnsafeStmt*)stmt)->body); r->unsafeDepth--; break;
        default: RERR(stmt->line, "Unexpected statement %s at this position", fcStmtKindNames[stmt->kind]);
    }
}

static void ResolveStmtAsScope(FcResolver* r, FcStmt* body) {
    if (body->kind == SK_BlockStmt) ResolveBlockNewScope(r, (FcBlockStmt*)body);
    else { PushScope(r); ResolveStmt(r, body); PopScope(r); }
}
static void ResolveBlockNewScope(FcResolver* r, FcBlockStmt* block) {
    int i;
    PushScope(r);
    for (i = 0; i < block->statements->count; i++) ResolveStmt(r, (FcStmt*)block->statements->items[i]);
    PopScope(r);
}
static void CheckLoopLevels(FcResolver* r, const char* keyword, int levels, int line) {
    int open = r->loopFinallyDepths ? r->loopFinallyDepths->count : 0;
    bool crossesFinally = r->tryDepth > 0;
    FcIntStack* s;
    int k;
    if (levels > open) RERR(line, "'%s %d' needs %d enclosing loops, there %s", keyword, levels, levels, open == 1 ? "is 1" : fcFmt(R_, "are %d", open));
    for (s = r->loopFinallyDepths, k = 0; s && k < levels - 1; s = s->next, k++) if (s->value > 0) crossesFinally = true;
    if (crossesFinally) RERR(line, "'%s' cannot be used to leave a 'finally' block (a limitation of this stage - see BYTECODE.md)", keyword);
}
static void ResolveFor(FcResolver* r, FcForStmt* fs) {
    PushScope(r);
    if (fs->init) ResolveStmt(r, fs->init);
    if (fs->condition) ResolveExpr(r, fs->condition);
    if (fs->increment) ResolveExpr(r, fs->increment);
    fcLoopBody(r, fs->body);
    PopScope(r);
}
static void ResolveForeach(FcResolver* r, FcForeachStmt* fe) {
    ResolveExpr(r, fe->iterable);
    PushScope(r);
    DEFINE_PLAIN(fe->varName, fe->h.line);
    fcLoopBody(r, fe->body);
    PopScope(r);
}
static void ResolveTry(FcResolver* r, FcTryStmt* t) {
    int i, k;
    ResolveBlockNewScope(r, t->tryBlock);
    for (i = 0; i < t->catches->count; i++) {
        FcCatchClause* cc = (FcCatchClause*)t->catches->items[i];
        if (cc->typeRef && !IsKnownClassName(r, ResolveTypeRef(r, cc->typeRef)) && !fcDictHas(&r->interfaces, ResolveBaseRef(r, cc->typeRef)))
            ADDERR(cc->line, "Unknown exception type '%s'", cc->typeRef->baseName);
        PushScope(r);
        DEFINE_PLAIN(cc->varName, cc->line);
        for (k = 0; k < cc->body->statements->count; k++) ResolveStmt(r, (FcStmt*)cc->body->statements->items[k]);
        PopScope(r);
    }
    if (t->finallyBlock) { r->tryDepth++; ResolveBlockNewScope(r, t->finallyBlock); r->tryDepth--; }
}

/* ---- classes ---- */
typedef struct FcMethodSig { const char* name; FcList* types; } FcMethodSig;
static void ResolveFunctionLike(FcResolver* r, FcList* parms, FcBlockStmt* body, FcList* baseArgs, bool isConstructor, bool isStatic);
static void ResolveFieldInitializer(FcResolver* r, FcFieldDecl* fd);

/* Guard(() => ...): each guarded check is a function of its own (the setjmp of the try is in it, no local of the caller lives across it) */
#define GUARD_BODY(code) FC_TRY(R_) { code; } FC_CATCH(R_) { if (R_->err.kind != FC_ERR_RESOLVE) fcRethrow(R_); AddError(r, fcRErrorOfCtx(r)); } FC_END_TRY
static void gValidateTypeRef(FcResolver* r, FcTypeRef* type, int line) { GUARD_BODY(ValidateTypeRef(r, type, line)); }
static void gValidateNonNull(FcResolver* r, FcTypeRef* type, int ranks, int line) { GUARD_BODY(ValidateNonNull(r, type, ranks, line)); }
static void gCheckDefinedObjectField(FcResolver* r, FcClassDecl* cd, FcFieldDecl* fd) { GUARD_BODY(CheckDefinedObjectField(r, cd, fd)); }
static void gValidateOptionalParamsAreTrailing(FcResolver* r, FcList* parms, int line);
static void gDefine(FcResolver* r, const char* name, int line, const char* requiredUnit, bool isRef, int width, FcTypeRef* arrayElem, int arrayRank) {
    GUARD_BODY(Define(r, name, line, false, requiredUnit, isRef, width, arrayElem, arrayRank));
}
static void ResolveNewTargetOf(FcResolver* r, FcNewExpr* ne);
static void gResolveNewTargetOf(FcResolver* r, FcNewExpr* ne) { GUARD_BODY(ResolveNewTargetOf(r, ne)); }

static FcList* fcTypeParamNames(FcResolver* r, FcList* typeParams) {
    FcList* l = fcListNew(R_);
    int i;
    if (typeParams) for (i = 0; i < typeParams->count; i++) fcListAdd(R_, l, (void*)((FcTypeParam*)typeParams->items[i])->name);
    return l;
}

static void fcGuardedFunctionLike(FcResolver* r, FcList* parms, FcBlockStmt* body, bool isStatic) {
    FcResolverState state = SaveState(r);
    FC_TRY(R_) {
        ResolveFunctionLike(r, parms, body, NULL, false, isStatic);
    } FC_CATCH(R_) {
        if (R_->err.kind != FC_ERR_RESOLVE) fcRethrow(R_);
        AddError(r, fcRErrorOfCtx(r));
        RestoreState(r, &state);
    } FC_END_TRY;
}

static void fcResolveMember(FcResolver* r, FcClassDecl* cd, FcStmt* member, FcList* classTypeParamNames, FcList* seenMethodSignatures, FcList* seenConstructorSignatures) {
    int i;
    switch (member->kind) {
        case SK_FieldDecl: {
            FcFieldDecl* fd = (FcFieldDecl*)member;
            if (fcStartsWith(fd->name, "_Auto") && fd->access == ACC_Private && (fd->type == NULL || fcTrIsInferred(fd->type))) { }
            else if (fd->type == NULL || fcTrIsInferred(fd->type))
                ADDERR(fd->h.line, "Field '%s' of class '%s' needs a type: a field is not declared with 'var' (use the type it holds, or 'any' if every type is allowed)", fd->name, cd->name);
            else gValidateTypeRef(r, fd->type, fd->h.line);
            gValidateNonNull(r, fd->type, fd->arrayRanks->count, fd->h.line);
            if (fd->initializer == NULL && HasNoZero(r, fd->type, fd->arrayRanks->count)) gCheckDefinedObjectField(r, cd, fd);
            ResolveArrayRanks(r, fd->arrayRanks);
            ResolveFieldInitializer(r, fd);
            break;
        }
        case SK_ConstructorDecl: {
            FcConstructorDecl* ctor = (FcConstructorDecl*)member;
            FcList* ctorTypes = SignatureKeys(r, ctor->params, classTypeParamNames);
            for (i = 0; i < seenConstructorSignatures->count; i++)
                if (SameSignature((FcList*)seenConstructorSignatures->items[i], ctorTypes)) {
                    ADDERR(ctor->h.line, "A constructor with %d parameter(s) of these types is already defined in this class (an overload needs a different number of parameters or other parameter types).", ctor->params->count);
                    break;
                }
            fcListAdd(R_, seenConstructorSignatures, ctorTypes);
            ResolveFunctionLike(r, ctor->params, ctor->body, ctor->baseArgs, true, false);
            break;
        }
        case SK_DestructorDecl: ResolveFunctionLike(r, fcListNew(R_), ((FcDestructorDecl*)member)->body, NULL, false, false); break;
        case SK_MethodDecl: {
            FcMethodDecl* md = (FcMethodDecl*)member;
            FcList* methodTypeParamNames = fcTypeParamNames(r, md->typeParams);
            FcList* all;
            FcList* methodTypes;
            FcMethodSig* sig;
            AddTypeParamNames(r, methodTypeParamNames);
            FC_TRY(R_) {
                if (md->returnType) {
                    gValidateTypeRef(r, md->returnType, md->h.line);
                    gValidateNonNull(r, md->returnType, 0, md->h.line);
                }
                all = fcListCopy(R_, classTypeParamNames);
                for (i = 0; i < methodTypeParamNames->count; i++) fcListAdd(R_, all, methodTypeParamNames->items[i]);
                methodTypes = SignatureKeys(r, md->params, all);
                for (i = 0; i < seenMethodSignatures->count; i++) {
                    FcMethodSig* m = (FcMethodSig*)seenMethodSignatures->items[i];
                    if (streq0(m->name, md->name) && SameSignature(m->types, methodTypes)) {
                        ADDERR(md->h.line, "Method '%s' with %d parameter(s) of these types is already defined in this class (an overload needs a different number of parameters or other parameter types).", md->name, md->params->count);
                        break;
                    }
                }
                sig = (FcMethodSig*)fcAlloc(R_, sizeof *sig);
                sig->name = md->name; sig->types = methodTypes;
                fcListAdd(R_, seenMethodSignatures, sig);
                ResolveFunctionLike(r, md->params, md->body, NULL, false, md->isStatic);
            } FC_CATCH(R_) {
                RemoveTypeParamNames(r, methodTypeParamNames);
                fcRethrow(R_);
            } FC_END_TRY;
            RemoveTypeParamNames(r, methodTypeParamNames);
            break;
        }
        case SK_PropertyDecl: {
            FcPropertyDecl* pd = (FcPropertyDecl*)member;
            if (pd->type == NULL || fcTrIsInferred(pd->type))
                ADDERR(pd->h.line, "Property '%s' of class '%s' needs a type (use the type it holds, or 'any' if every type is allowed)", pd->name, cd->name);
            else {
                gValidateTypeRef(r, pd->type, pd->h.line);
                gValidateNonNull(r, pd->type, 0, pd->h.line);
            }
            if (pd->getter) fcGuardedFunctionLike(r, fcListNew(R_), pd->getter, pd->isStatic);
            if (pd->setter) {
                FcList* setterParams = fcListNew(R_);
                FcLambdaParam* value = (FcLambdaParam*)fcAlloc(R_, sizeof *value);
                value->name = "value"; value->type = pd->type; value->arrayRanks = fcListNew(R_);
                fcListAdd(R_, setterParams, value);
                fcGuardedFunctionLike(r, setterParams, pd->setter, pd->isStatic);
            }
            break;
        }
        default: break;
    }
}

static void ResolveClass(FcResolver* r, FcClassDecl* cd) {
    FcClassDecl* savedClass = r->currentClass;
    bool savedHasBase = r->currentClassHasBase;
    FcList* classTypeParamNames;
    FcList* seenMethodSignatures = fcListNew(R_);
    FcList* seenConstructorSignatures = fcListNew(R_);
    int i;
    r->currentClass = cd;
    r->currentClassHasBase = GetBaseClassName(r, cd) != NULL;
    classTypeParamNames = fcTypeParamNames(r, cd->typeParams);
    AddTypeParamNames(r, classTypeParamNames);
    for (i = 0; i < cd->members->count; i++) {
        FcStmt* member = (FcStmt*)cd->members->items[i];
        FcResolverState state = SaveState(r);
        FC_TRY(R_) {
            fcResolveMember(r, cd, member, classTypeParamNames, seenMethodSignatures, seenConstructorSignatures);
        } FC_CATCH(R_) {
            if (R_->err.kind != FC_ERR_RESOLVE) fcRethrow(R_);
            AddError(r, fcRErrorOfCtx(r));
            RestoreState(r, &state);
        } FC_END_TRY;
    }
    RemoveTypeParamNames(r, classTypeParamNames);
    r->currentClass = savedClass;
    r->currentClassHasBase = savedHasBase;
}

static void ResolveFieldInitializer(FcResolver* r, FcFieldDecl* fd) {
    FcResolverScope* saved;
    bool savedInStaticMethod;
    if (fd->initializer == NULL) return;
    saved = r->current;
    r->current = fcScopeNew(r, r->globalScope, false);
    savedInStaticMethod = r->inStaticMethod;
    r->inStaticMethod = fd->isStatic;
    ResolveExpr(r, fd->initializer);
    r->inStaticMethod = savedInStaticMethod;
    r->current = saved;
}
static void ValidateOptionalParamsAreTrailing(FcResolver* r, FcList* parms, int line) {
    bool seenOptional = false;
    int i;
    for (i = 0; i < parms->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parms->items[i];
        if (p->defaultValue != NULL) { seenOptional = true; continue; }
        if (seenOptional) RERR(line, "Parameter '%s' without a default value must not follow an optional parameter (optional parameters must be contiguous at the end).", p->name);
    }
}
static void gValidateOptionalParamsAreTrailing(FcResolver* r, FcList* parms, int line) { GUARD_BODY(ValidateOptionalParamsAreTrailing(r, parms, line)); }
static void ResolveParamDefaults(FcResolver* r, FcList* parms) {
    FcResolverScope* saved = r->current;
    int i;
    r->current = fcScopeNew(r, r->globalScope, false);
    for (i = 0; i < parms->count; i++) if (((FcLambdaParam*)parms->items[i])->defaultValue) ResolveExpr(r, ((FcLambdaParam*)parms->items[i])->defaultValue);
    r->current = saved;
}

static void ResolveFunctionLike(FcResolver* r, FcList* parms, FcBlockStmt* body, FcList* baseArgs, bool isConstructor, bool isStatic) {
    bool savedInConstructor, savedInStaticMethod;
    int savedLoopDepth, savedTryDepth, i;
    FcIntStack* savedLoopFinallyDepths;
    gValidateOptionalParamsAreTrailing(r, parms, body->h.line);
    {
        FcResolverState state = SaveState(r);
        FC_TRY(R_) { ResolveParamDefaults(r, parms); } FC_CATCH(R_) {
            if (R_->err.kind != FC_ERR_RESOLVE) fcRethrow(R_);
            AddError(r, fcRErrorOfCtx(r));
            RestoreState(r, &state);
        } FC_END_TRY;
    }
    PushScope(r);
    for (i = 0; i < parms->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parms->items[i];
        const char* parameterClass;
        if (p->type) gValidateTypeRef(r, p->type, body->h.line);
        gValidateNonNull(r, p->type, p->arrayRanks->count, body->h.line);
        ResolveArrayRanks(r, p->arrayRanks);
        gDefine(r, p->name, body->h.line, p->type ? fcUnitDemandOf(R_, p->type) : NULL, p->byRef,
            p->arrayRanks->count == 0 ? (p->type ? fcTrWidthCode(p->type) : 0) : 0, p->byRef ? NULL : p->type, p->arrayRanks->count);
        if ((parameterClass = KnownClassOf(r, p->type, NULL, p->arrayRanks->count > 0)) != NULL) fcDictPut(&r->current->knownClasses, p->name, (void*)parameterClass);
    }
    if (baseArgs != NULL) {
        if (!r->currentClassHasBase) ADDERR(body->h.line, "'base(...)' is only valid in a class with a base class");
        for (i = 0; i < baseArgs->count; i++) ResolveExprAllowTake(r, (FcExpr*)baseArgs->items[i]);
    }
    savedInConstructor = r->inConstructor;
    r->inConstructor = isConstructor;
    savedInStaticMethod = r->inStaticMethod;
    r->inStaticMethod = isStatic;
    savedLoopDepth = r->loopDepth;
    savedTryDepth = r->tryDepth;
    r->loopDepth = 0;
    savedLoopFinallyDepths = r->loopFinallyDepths; r->loopFinallyDepths = NULL;
    r->tryDepth = 0;
    r->functionDepth++;
    for (i = 0; i < body->statements->count; i++) ResolveStmt(r, (FcStmt*)body->statements->items[i]);
    r->functionDepth--;
    r->loopDepth = savedLoopDepth;
    r->loopFinallyDepths = savedLoopFinallyDepths;
    r->tryDepth = savedTryDepth;
    r->inConstructor = savedInConstructor;
    r->inStaticMethod = savedInStaticMethod;
    PopScope(r);
}

/* ---- expressions ---- */
static void ResolveExprAllowTake(FcResolver* r, FcExpr* expr) {
    if (expr->kind == EK_UnaryExpr && (((FcUnaryExpr*)expr)->op == UOP_Take || ((FcUnaryExpr*)expr)->op == UOP_TryTake)) fcSetAdd(&r->takeAllowed, expr);
    ResolveExpr(r, expr);
}
static void ResolveExprCore(FcResolver* r, FcExpr* expr);
static void ResolveExpr(FcResolver* r, FcExpr* expr) {
    FcResolverState state = SaveState(r);
    FC_TRY(R_) {
        ResolveExprCore(r, expr);
    } FC_CATCH(R_) {
        if (R_->err.kind != FC_ERR_RESOLVE) fcRethrow(R_);
        AddError(r, fcRErrorOfCtx(r));
        RestoreState(r, &state);
    } FC_END_TRY;
}

static FcResolverScope* FindScopeOf(FcResolver* r, const char* name);
static void ResolveTryCallExpr(FcResolver* r, FcTryCallExpr* tc);
static void ResolveAssignTarget(FcResolver* r, FcExpr* target);

static void ResolveNewTargetOf(FcResolver* r, FcNewExpr* ne) {
    FcTypeRef copy = *ne->classRef;
    const char* resolvedNewClassName;
    FcClassDecl* newTargetCd;
    int typeArgCount = ne->typeArgs ? ne->typeArgs->count : 0;
    copy.typeArgCount = typeArgCount;
    CheckAmbiguous(r, &copy, ne->h.line, K(r, kIsKnownClassName), NamespaceOf(r, r->currentClass ? r->currentClass->name : NULL));
    resolvedNewClassName = fcResolveNewTarget(R_, ne->classRef, typeArgCount, K(r, kIsKnownClassName));
    if (!IsKnownClassName(r, resolvedNewClassName)) RERR(ne->h.line, "Unknown class '%s'", ne->classRef->baseName);
    WarnForNew(r, resolvedNewClassName, ne->args->count, ne->h.line);
    if ((newTargetCd = (FcClassDecl*)fcDictGet(&r->classes, resolvedNewClassName)) != NULL) CheckTypeArgs(r, newTargetCd, ne);
    else if (ne->typeArgs && ne->typeArgs->count > 0) RERR(ne->h.line, "'%s' is not generic, so it does not accept type arguments in angle brackets", ne->classRef->baseName);
}

static int fcArityOf(FcResolver* r, FcExpr* e) { void* v; return fcDictTry(&r->calleeArity, e, &v) ? FC_VALI(v) : FC_NULL_INT; }

static void ResolveExprCore(FcResolver* r, FcExpr* expr) {
    int i;
    switch (expr->kind) {
        case EK_LiteralExpr: case EK_TypeBitsExpr: break;
        case EK_IdentifierExpr: fcDictPut(&r->refs, expr, ResolveIdentifierRef(r, ((FcIdentifierExpr*)expr)->name, expr->line)); break;
        case EK_ThisExpr:
            if (r->inStaticMethod && !r->inBoundLambda) RERR(expr->line, "'this' is not valid in a static method or static field initializer (no instance bound)");
            break;
        case EK_BaseExpr:
            if (r->inStaticMethod) RERR(expr->line, "'base' is not valid in a static method (no instance bound)");
            if (r->currentClass == NULL || !r->currentClassHasBase) RERR(expr->line, "'base' is only valid inside a class with a base class");
            break;
        case EK_UnaryExpr: {
            FcUnaryExpr* u = (FcUnaryExpr*)expr;
            if ((u->op == UOP_Take || u->op == UOP_TryTake) && !fcDictRemove(&r->takeAllowed, u))
                RERR(u->h.line, "'%s' is only valid as an argument of a call or on the right of '=' / 'var x =' (SPEC 2.2)", u->op == UOP_TryTake ? "try take" : "take");
            if ((u->op == UOP_Dereference || u->op == UOP_AddressOf) && r->unsafeDepth == 0)
                RERR(u->h.line, "'%s' is only valid inside an 'unsafe' block", u->op == UOP_Dereference ? "*" : "&");
            ResolveExpr(r, u->operand);
            break;
        }
        case EK_BinaryExpr: ResolveExpr(r, ((FcBinaryExpr*)expr)->left); ResolveExpr(r, ((FcBinaryExpr*)expr)->right); break;
        case EK_RebindExpr: ResolveExpr(r, ((FcRebindExpr*)expr)->lambda); ResolveExpr(r, ((FcRebindExpr*)expr)->target); break;
        case EK_ConditionalExpr: {
            FcConditionalExpr* ce = (FcConditionalExpr*)expr;
            ResolveExpr(r, ce->condition); ResolveExpr(r, ce->then); ResolveExpr(r, ce->otherwise);
            break;
        }
        case EK_UnitCoerceExpr: ResolveExpr(r, ((FcUnitCoerceExpr*)expr)->operand); break;
        case EK_TypeCoerceExpr: {
            FcTypeCoerceExpr* tc = (FcTypeCoerceExpr*)expr;
            ResolveExpr(r, tc->operand);
            if (tc->targetClass && !fcDictHas(&r->interfaces, tc->targetClass->baseName)) ValidateTypeName(r, tc->targetClass, tc->h.line);
            break;
        }
        case EK_IsInExpr: ResolveExpr(r, ((FcIsInExpr*)expr)->operand); break;
        case EK_RangeExpr: ResolveExpr(r, ((FcRangeExpr*)expr)->from); ResolveExpr(r, ((FcRangeExpr*)expr)->to); break;
        case EK_IsOfExpr: {
            FcIsOfExpr* iof = (FcIsOfExpr*)expr;
            bool typeParam = iof->operand->kind == EK_IdentifierExpr && FC_VALI(fcDictGet(&r->currentTypeParamNames, ((FcIdentifierExpr*)iof->operand)->name)) > 0
                && FindScopeOf(r, ((FcIdentifierExpr*)iof->operand)->name) == NULL;
            if (!typeParam) ResolveExpr(r, iof->operand);
            if (!fcDictHas(&r->interfaces, iof->typeRef->baseName)) ValidateTypeName(r, iof->typeRef, iof->h.line);
            break;
        }
        case EK_IsFromExpr: ResolveExpr(r, ((FcIsFromExpr*)expr)->operand); ResolveExpr(r, ((FcIsFromExpr*)expr)->ownerExpr); break;
        case EK_CallExpr: {
            FcCallExpr* call = (FcCallExpr*)expr;
            fcDictPut(&r->calleeArity, call->callee, FC_IVAL(call->args->count));
            if (call->typeArgs) for (i = 0; i < call->typeArgs->count; i++) gValidateTypeRef(r, (FcTypeRef*)call->typeArgs->items[i], call->h.line);
            ResolveExpr(r, call->callee);
            for (i = 0; i < call->args->count; i++) ResolveExprAllowTake(r, (FcExpr*)call->args->items[i]);
            break;
        }
        case EK_MemberExpr: {
            FcMemberExpr* me = (FcMemberExpr*)expr;
            const char* enumWritten = DottedName(r, me->target);
            const char* enumName = enumWritten ? InNamespaces(r, enumWritten, K(r, kIsEnum), me->h.line) : NULL;
            FcDict* enumMembers;
            const char* staticClassName;
            const char* receiverClass;
            if (enumName != NULL && (enumMembers = (FcDict*)fcDictGet(&r->enums, enumName)) != NULL) {
                int64_t* enumValue;
                FcResolvedRef* ref;
                if (!fcDictHas(enumMembers, me->name) && (streq0(me->name, "Name") || streq0(me->name, "Parse") || streq0(me->name, "IsDefined") || streq0(me->name, "Values"))) {
                    char* helper = (char*)fcFmt(R_, "__enum_%s", enumName);
                    char* q;
                    for (q = helper; *q; q++) if (*q == '.') *q = '_';
                    if (IsKnownClassName(r, helper)) {
                        ref = fcRef(R_, RR_StaticMember); ref->className = helper;
                        fcDictPut(&r->refs, me, ref);
                        break;
                    }
                }
                if (!(enumValue = (int64_t*)fcDictGet(enumMembers, me->name))) RERR(me->h.line, "'%s' has no member '%s'", enumName, me->name);
                ref = fcRef(R_, RR_EnumMember);
                ref->value = *enumValue;
                ref->enumType = (const char*)fcDictGet(&r->enumTypes, enumName);
                fcDictPut(&r->refs, me, ref);
                break;
            }
            staticClassName = TryResolveStaticMemberAccess(r, me);
            if (staticClassName != NULL) {
                FcResolvedRef* ref;
                CheckStaticMemberExists(r, staticClassName, me);
                WarnForClassUse(r, staticClassName, me->h.line);
                WarnForMember(r, ClassChainNames(r, staticClassName), me->name, fcArityOf(r, expr), me->h.line);
                ref = fcRef(R_, RR_StaticMember); ref->className = staticClassName;
                fcDictPut(&r->refs, me, ref);
                break;
            }
            if (me->target->kind == EK_ThisExpr && r->currentClass != NULL) {
                const char* cls;
                bool st;
                if (FindMemberInClassChain(r, r->currentClass, me->name, &cls, &st) && st) {
                    FcResolvedRef* ref = fcRef(R_, RR_StaticMember); ref->className = cls;
                    fcDictPut(&r->refs, me, ref);
                    break;
                }
            }
            if (HasDeclaredWarnings(r)) {
                const char* receiver = InferClass(r, me->target);
                WarnForMember(r, receiver ? ClassChainNames(r, receiver) : NULL, me->name, fcArityOf(r, expr), me->h.line);
            }
            if ((receiverClass = InferClass(r, me->target)) != NULL) fcDictPut(&r->receiverClasses, me, (void*)receiverClass);
            ResolveExpr(r, me->target);
            break;
        }
        case EK_IndexExpr: ResolveExpr(r, ((FcIndexExpr*)expr)->target); ResolveExpr(r, ((FcIndexExpr*)expr)->index); break;
        case EK_AssignExpr: {
            FcAssignExpr* asg = (FcAssignExpr*)expr;
            ResolveExprAllowTake(r, asg->value);
            if (asg->value->kind == EK_UnaryExpr && (((FcUnaryExpr*)asg->value)->op == UOP_Take || ((FcUnaryExpr*)asg->value)->op == UOP_TryTake)
                && asg->target->kind == EK_UnaryExpr && ((FcUnaryExpr*)asg->target)->op == UOP_Dereference)
                RERR(asg->h.line, "'take' needs a holder: a variable, a field or an array element (not a pointer)");
            ResolveAssignTarget(r, asg->target);
            break;
        }
        case EK_IncDecExpr: ResolveAssignTarget(r, ((FcIncDecExpr*)expr)->target); break;
        case EK_NewExpr: {
            FcNewExpr* ne = (FcNewExpr*)expr;
            gResolveNewTargetOf(r, ne);
            for (i = 0; i < ne->args->count; i++) ResolveExprAllowTake(r, (FcExpr*)ne->args->items[i]);
            break;
        }
        case EK_NewArrayExpr: {
            FcNewArrayExpr* na = (FcNewArrayExpr*)expr;
            ValidateTypeRef(r, na->elementType, na->h.line);
            if (na->sizeExprs->count == 0 || na->sizeExprs->items[0] == NULL)
                RERR(na->h.line, "'new Type[...]' needs a size at least for the first dimension (e.g. 'new int[3]' or 'new int[3][]' - not 'new int[]').");
            ResolveArrayRanks(r, na->sizeExprs);
            break;
        }
        case EK_NewBufferExpr: ResolveExpr(r, ((FcNewBufferExpr*)expr)->sizeExpr); break;
        case EK_ArrayLiteralExpr: {
            FcList* els = ((FcArrayLiteralExpr*)expr)->elements;
            for (i = 0; i < els->count; i++) ResolveExpr(r, (FcExpr*)els->items[i]);
            break;
        }
        case EK_InterpolatedStringExpr: {
            FcList* parts = ((FcInterpolatedStringExpr*)expr)->parts;
            for (i = 0; i < parts->count; i++) if (((FcInterpolationPart*)parts->items[i])->isExpression) ResolveExpr(r, ((FcInterpolationPart*)parts->items[i])->expression);
            break;
        }
        case EK_ThrowExpr: ResolveExpr(r, ((FcThrowExpr*)expr)->value); break;
        case EK_UntilExpr: {
            FcUntilExpr* u = (FcUntilExpr*)expr;
            for (i = 0; i < u->limits->count; i++) ResolveExpr(r, (FcExpr*)u->limits->items[i]);
            ResolveExpr(r, u->inner);
            break;
        }
        case EK_AwaitOnExpr: {
            FcAwaitOnExpr* a = (FcAwaitOnExpr*)expr;
            for (i = 0; i < a->args->count; i++) ResolveExprAllowTake(r, (FcExpr*)a->args->items[i]);
            ResolveLambda(r, (FcLambdaExpr*)a->lambda);
            break;
        }
        case EK_LambdaExpr: ResolveLambda(r, (FcLambdaExpr*)expr); break;
        case EK_SyncExpr: ResolveExpr(r, ((FcSyncExpr*)expr)->target); break;
        case EK_ProbeExpr: ResolveExpr(r, ((FcProbeExpr*)expr)->target); ResolveExpr(r, ((FcProbeExpr*)expr)->handler); break;
        case EK_SyncGlobalsExpr: break;
        case EK_TryProcessExpr: ResolveExpr(r, ((FcTryProcessExpr*)expr)->target); break;
        case EK_TryCallExpr: ResolveTryCallExpr(r, (FcTryCallExpr*)expr); break;
        case EK_ConditionalAccessExpr: ResolveExpr(r, ((FcConditionalAccessExpr*)expr)->target); ResolveExpr(r, ((FcConditionalAccessExpr*)expr)->inner); break;
        case EK_ConditionalTargetExpr: break;
        case EK_WithExpr: {
            FcWithExpr* w = (FcWithExpr*)expr;
            ResolveExpr(r, w->source);
            for (i = 0; i < w->sets->count; i++) ResolveExprAllowTake(r, ((FcWithSet*)w->sets->items[i])->value);
            break;
        }
        default: RERR(expr->line, "Unknown expression type %s", fcExprKindNames[expr->kind]);
    }
}

static void ResolveTryCallExpr(FcResolver* r, FcTryCallExpr* tc) {
    FcCallExpr* innerCall;
    FcIdentifierExpr* calleeIdent;
    FcResolvedRef* ref;
    int i;
    if (tc->call->kind == EK_CallExpr && ((FcCallExpr*)tc->call)->callee->kind == EK_MemberExpr) {
        FcCallExpr* takeCall = (FcCallExpr*)tc->call;
        FcMemberExpr* takeMember = (FcMemberExpr*)takeCall->callee;
        if (streq0(takeMember->name, "TakeLocal") || streq0(takeMember->name, "TakeUpwards") || streq0(takeMember->name, "TakeGlobal") || streq0(takeMember->name, "TakeTo")) {
            int baseArgs = streq0(takeMember->name, "TakeTo") ? 1 : 0;
            if (takeCall->args->count != baseArgs && takeCall->args->count != baseArgs + 1)
                RERR(tc->h.line, "'try %s(...)' expects %s", takeMember->name, baseArgs == 1 ? "the target object and optionally a Takes mode" : "optionally a Takes mode");
            ResolveExpr(r, takeMember->target);
            for (i = 0; i < takeCall->args->count; i++) ResolveExprAllowTake(r, (FcExpr*)takeCall->args->items[i]);
            ref = fcRef(R_, RR_TryTake); ref->name = takeMember->name;
            fcDictPut(&r->refs, tc, ref);
            return;
        }
    }
    if (tc->call->kind != EK_CallExpr || ((FcCallExpr*)tc->call)->callee->kind != EK_IdentifierExpr)
        RERR(tc->h.line, "'try' before a call expects a direct call of a registered function (no method call except obj.TakeLocal/TakeUpwards/TakeGlobal/TakeTo, no lambda call)");
    innerCall = (FcCallExpr*)tc->call;
    calleeIdent = (FcIdentifierExpr*)innerCall->callee;
    if (!fcDictHas(r->tryableNativeNames, calleeIdent->name)) {
        if (fcDictHas(r->nativeNames, calleeIdent->name) || fcDictHas(&r->externs, calleeIdent->name))
            RERR(tc->h.line, "'%s' is not a function registered as 'tryable' - only APIs that can be called with 'try' may be called like this, ordinary native/extern functions may not.", calleeIdent->name);
        RERR(tc->h.line, "'%s' is not a known native function registered as 'tryable'.", calleeIdent->name);
    }
    for (i = 0; i < innerCall->args->count; i++) ResolveExprAllowTake(r, (FcExpr*)innerCall->args->items[i]);
    ref = fcRef(R_, RR_TryableNative); ref->name = calleeIdent->name;
    fcDictPut(&r->refs, tc, ref);
}

static bool IsReadonlyEverywhere(FcResolver* r, const char* fieldName);
static bool IsReadonlyField(FcClassDecl* cd, const char* fieldName);
static FcFieldDecl* StaticReadonlyField(FcResolver* r, const char* className, const char* name);
_Noreturn static void StaticReadonlyWrite(FcResolver* r, const char* className, FcMemberExpr* me) {
    RERR(me->h.line, "'%s.%s' is 'static readonly' - only its initializer sets it", fcPlainName(R_, className), me->name);
}

static void ResolveAssignTarget(FcResolver* r, FcExpr* target) {
    switch (target->kind) {
        case EK_IdentifierExpr: {
            FcIdentifierExpr* id = (FcIdentifierExpr*)target;
            if (IsReadonlyVariable(r, id->name)) RERR(id->h.line, "'%s' is 'readonly' and cannot be assigned after its declaration", id->name);
            fcDictPut(&r->refs, id, ResolveIdentifierRef(r, id->name, id->h.line));
            break;
        }
        case EK_MemberExpr: {
            FcMemberExpr* me = (FcMemberExpr*)target;
            const char* staticAssignClassName;
            if (me->target->kind == EK_ThisExpr && r->currentClass != NULL && IsReadonlyField(r->currentClass, me->name) && !r->inConstructor)
                RERR(me->h.line, "'%s.%s' is 'readonly' and can only be assigned inside a constructor of the declaring class", r->currentClass->name, me->name);
            staticAssignClassName = TryResolveStaticMemberAccess(r, me);
            if (staticAssignClassName != NULL) {
                FcResolvedRef* ref;
                CheckStaticMemberExists(r, staticAssignClassName, me);
                if (StaticReadonlyField(r, staticAssignClassName, me->name) != NULL) StaticReadonlyWrite(r, staticAssignClassName, me);
                ref = fcRef(R_, RR_StaticMember); ref->className = staticAssignClassName;
                fcDictPut(&r->refs, me, ref);
                break;
            }
            if (me->target->kind == EK_ThisExpr && r->currentClass != NULL) {
                const char* cls;
                bool st;
                if (FindMemberInClassChain(r, r->currentClass, me->name, &cls, &st) && st) {
                    FcResolvedRef* ref;
                    if (StaticReadonlyField(r, cls, me->name) != NULL) StaticReadonlyWrite(r, cls, me);
                    ref = fcRef(R_, RR_StaticMember); ref->className = cls;
                    fcDictPut(&r->refs, me, ref);
                    break;
                }
            }
            if (me->target->kind != EK_ThisExpr && IsReadonlyEverywhere(r, me->name))
                RERR(me->h.line, "'%s' is 'readonly' and can only be assigned inside a constructor of its class (as 'this.%s = ...')", me->name, me->name);
            ResolveExpr(r, me->target);
            break;
        }
        case EK_IndexExpr: ResolveExpr(r, ((FcIndexExpr*)target)->target); ResolveExpr(r, ((FcIndexExpr*)target)->index); break;
        case EK_UnaryExpr:
            if (((FcUnaryExpr*)target)->op == UOP_Dereference) {
                if (r->unsafeDepth == 0) RERR(target->line, "'*' as an assignment target is only valid inside an 'unsafe' block");
                ResolveExpr(r, ((FcUnaryExpr*)target)->operand);
                break;
            }
            RERR(target->line, "Invalid assignment target");
        default: RERR(target->line, "Invalid assignment target");
    }
}

static bool IsReadonlyEverywhere(FcResolver* r, const char* fieldName) {
    bool any = false;
    int i;
    FC_DICT_EACH(&r->classes, e) {
        FcClassDecl* cd = (FcClassDecl*)e->value;
        for (i = 0; i < cd->members->count; i++) {
            FcStmt* m = (FcStmt*)cd->members->items[i];
            if (m->kind == SK_FieldDecl && !((FcFieldDecl*)m)->isStatic && streq0(((FcFieldDecl*)m)->name, fieldName)) {
                if (!((FcFieldDecl*)m)->isReadonly) return false;
                any = true;
            } else if (m->kind == SK_PropertyDecl && !((FcPropertyDecl*)m)->isStatic && streq0(((FcPropertyDecl*)m)->name, fieldName)) return false;
            else if (m->kind == SK_MethodDecl && !((FcMethodDecl*)m)->isStatic && streq0(((FcMethodDecl*)m)->name, fieldName)) return false;
        }
    }
    FC_DICT_EACH(&r->interfaces, e) {
        FcInterfaceDecl* iface = (FcInterfaceDecl*)e->value;
        if (iface->properties) for (i = 0; i < iface->properties->count; i++) if (streq0(((FcInterfacePropertySig*)iface->properties->items[i])->name, fieldName)) return false;
    }
    return any;
}
static bool IsReadonlyField(FcClassDecl* cd, const char* fieldName) {
    int i;
    for (i = 0; i < cd->members->count; i++) {
        FcStmt* m = (FcStmt*)cd->members->items[i];
        if (m->kind == SK_FieldDecl && ((FcFieldDecl*)m)->isReadonly && !((FcFieldDecl*)m)->isStatic && streq0(((FcFieldDecl*)m)->name, fieldName)) return true;
    }
    return false;
}
static FcFieldDecl* StaticReadonlyField(FcResolver* r, const char* className, const char* name) {
    FcClassDecl* start = (FcClassDecl*)fcDictGet(&r->classes, className);
    FcDict* seen = fcDictNew(R_, FC_KEY_PTR);
    FcClassDecl* cd;
    int i;
    if (!start) return NULL;
    for (cd = start; cd != NULL && fcSetAdd(seen, cd); cd = GetBaseClassDecl(r, cd))
        for (i = 0; i < cd->members->count; i++) {
            FcStmt* m = (FcStmt*)cd->members->items[i];
            if (m->kind == SK_FieldDecl && streq0(((FcFieldDecl*)m)->name, name)) return ((FcFieldDecl*)m)->isStatic && ((FcFieldDecl*)m)->isReadonly ? (FcFieldDecl*)m : NULL;
            if (m->kind == SK_PropertyDecl && streq0(((FcPropertyDecl*)m)->name, name)) return NULL;
            if (m->kind == SK_MethodDecl && streq0(((FcMethodDecl*)m)->name, name)) return NULL;
        }
    return NULL;
}

/* ResolveFireStmt */
static void fcFireLoopReset(FcResolver* r) { (void)r; }
static void ResolveFireStmt(FcResolver* r, FcFireStmt* fs) {
    FcResolverScope* saved;
    FcResolverScope* savedFireScope;
    int nextCaptureSlot, savedFireSharedSlots, savedLoopDepth, savedTryDepth, savedFunctionDepth, i, k;
    bool savedInConstructor;
    FcIntStack* savedLoopFinallyDepths;
    FcDict* capturesSeen;
    r->sawFire = true;
    {
        FcList* captureList = fs->takingCaptures;
        FcList* used = fcListNew(R_);
        FcDict* declared;
        fcAstNamesCollect(R_, (FcStmt*)fs->body, used, fcDictNew(R_, FC_KEY_STR));
        declared = fcAstNamesDeclared(R_, (FcStmt*)fs->body);
        for (i = 0; i < used->count; i++) {
            const char* name = (const char*)used->items[i];
            bool skip = fcDictHas(declared, name);
            FcResolverScope* scope;
            int firstMoved = -1;
            FcFireTakingCapture* capture;
            FcIdentifierExpr* id;
            for (k = 0; k < captureList->count && !skip; k++) if (streq0(((FcFireTakingCapture*)captureList->items[k])->varName, name)) skip = true;
            for (k = 0; k < fs->with->count && !skip; k++) if (streq0(((FcFireWith*)fs->with->items[k])->varName, name)) skip = true;
            if (skip) continue;
            scope = FindScopeOf(r, name);
            if (scope == NULL || scope->isGlobal) continue;
            for (k = 0; k < captureList->count; k++) if (((FcFireTakingCapture*)captureList->items[k])->handover) { firstMoved = k; break; }
            id = FC_NEW(R_, FcIdentifierExpr, .h = {EK_IdentifierExpr, fs->h.line}, .name = name);
            capture = (FcFireTakingCapture*)fcAlloc(R_, sizeof *capture);
            capture->varName = name; capture->source = (FcExpr*)id; capture->isImplicit = true;
            if (firstMoved < 0) fcListAdd(R_, captureList, capture); else fcListInsert(R_, captureList, firstMoved, capture);
        }
    }
    if (fs->until) for (i = 0; i < fs->until->count; i++) ResolveExpr(r, (FcExpr*)fs->until->items[i]);
    for (i = 0; i < fs->takingCaptures->count; i++) {
        FcFireTakingCapture* capture = (FcFireTakingCapture*)fs->takingCaptures->items[i];
        ResolveExpr(r, capture->source);
        if (capture->handover && r->fireScope != NULL && FindScopeOf(r, capture->varName) == r->fireScope && FC_VALI(fcDictGet(&r->fireScope->slots, capture->varName)) < r->fireSharedSlots)
            RERR(fs->h.line, "'handover' needs a variable of this thread: '%s' is a global variable of the main program", capture->varName);
    }
    for (i = 0; i < fs->with->count; i++) ResolveExpr(r, ((FcFireWith*)fs->with->items[i])->source);

    saved = r->current;
    r->current = fcScopeNew(r, NULL, true);
    if (!r->noShadowGlobals) FC_DICT_EACH(&r->globalScope->slots, e) fcDictPut(&r->current->slots, e->key, e->value);
    nextCaptureSlot = r->noShadowGlobals ? 0 : fcDictCount(&r->globalScope->slots);
    if (!r->noShadowGlobals && r->finalGlobalCount != FC_NULL_INT)
        for (; nextCaptureSlot < r->finalGlobalCount; nextCaptureSlot++) fcDictPut(&r->current->slots, fcFmt(R_, "\x01" "global:%d", nextCaptureSlot), FC_IVAL(nextCaptureSlot));
    savedFireScope = r->fireScope;
    savedFireSharedSlots = r->fireSharedSlots;
    r->fireScope = r->current;
    r->fireSharedSlots = nextCaptureSlot;
    capturesSeen = fcDictNew(R_, FC_KEY_STR);
    for (i = 0; i < fs->takingCaptures->count; i++) {
        FcFireTakingCapture* capture = (FcFireTakingCapture*)fs->takingCaptures->items[i];
        void* shadowed;
        if (!fcSetAdd(capturesSeen, capture->varName)) RERR(fs->h.line, "'%s' was already captured in this 'fire'", capture->varName);
        if (fcDictTry(&r->current->slots, capture->varName, &shadowed)) fcDictPut(&r->current->slots, fcFmt(R_, "\x01" "shadow:%s", capture->varName), shadowed);
        fcDictPut(&r->current->slots, capture->varName, FC_IVAL(nextCaptureSlot++));
        fcDictRemove(&r->current->readonlySlots, capture->varName);
    }
    for (i = 0; i < fs->with->count; i++) {
        FcFireWith* with = (FcFireWith*)fs->with->items[i];
        void* shadowed;
        if (!fcSetAdd(capturesSeen, with->varName)) RERR(fs->h.line, "'%s' was already captured in this 'fire'", with->varName);
        if (fcDictTry(&r->current->slots, with->varName, &shadowed)) fcDictPut(&r->current->slots, fcFmt(R_, "\x01" "shadow:%s", with->varName), shadowed);
        fcDictPut(&r->current->slots, with->varName, FC_IVAL(nextCaptureSlot++));
        fcDictRemove(&r->current->readonlySlots, with->varName);
    }
    savedLoopDepth = r->loopDepth; savedTryDepth = r->tryDepth; savedFunctionDepth = r->functionDepth; savedInConstructor = r->inConstructor;
    r->loopDepth = 0;
    savedLoopFinallyDepths = r->loopFinallyDepths; r->loopFinallyDepths = NULL;
    r->tryDepth = 0; r->functionDepth = 0; r->inConstructor = false;
    for (i = 0; i < fs->body->statements->count; i++) ResolveStmt(r, (FcStmt*)fs->body->statements->items[i]);
    r->loopDepth = savedLoopDepth; r->loopFinallyDepths = savedLoopFinallyDepths; r->tryDepth = savedTryDepth;
    r->functionDepth = savedFunctionDepth; r->inConstructor = savedInConstructor;
    r->fireScope = savedFireScope; r->fireSharedSlots = savedFireSharedSlots;
    r->current = saved;
    fcFireLoopReset(r);
}

static FcResolverScope* FindScopeOf(FcResolver* r, const char* name) {
    FcResolverScope* s;
    for (s = r->current; s != NULL; s = s->parent) if (fcDictHas(&s->slots, name)) return s;
    return NULL;
}

static void ResolveGlobalHandlerBody(FcResolver* r, const char* varName, FcBlockStmt* body) {
    FcResolverScope* saved = r->current;
    int savedLoopDepth, savedTryDepth, savedFunctionDepth, i;
    bool savedInConstructor;
    FcIntStack* savedLoopFinallyDepths;
    r->current = fcScopeNew(r, r->globalScope, false);
    if (varName != NULL) DEFINE_PLAIN(varName, body->h.line);
    savedLoopDepth = r->loopDepth; savedTryDepth = r->tryDepth; savedFunctionDepth = r->functionDepth; savedInConstructor = r->inConstructor;
    r->loopDepth = 0;
    savedLoopFinallyDepths = r->loopFinallyDepths; r->loopFinallyDepths = NULL;
    r->tryDepth = 0; r->functionDepth = 0; r->inConstructor = false;
    for (i = 0; i < body->statements->count; i++) ResolveStmt(r, (FcStmt*)body->statements->items[i]);
    r->loopDepth = savedLoopDepth; r->loopFinallyDepths = savedLoopFinallyDepths; r->tryDepth = savedTryDepth;
    r->functionDepth = savedFunctionDepth; r->inConstructor = savedInConstructor;
    r->current = saved;
}

/* LambdaExpr.CaptureModes: the entry of a name (NULL: none) */
static FcCapture* fcCaptureMode(FcLambdaExpr* lambda, const char* name) {
    int i;
    if (!lambda->captureModes) return NULL;
    for (i = 0; i < lambda->captureModes->count; i++) if (streq0(((FcCapture*)lambda->captureModes->items[i])->name, name)) return (FcCapture*)lambda->captureModes->items[i];
    return NULL;
}

#define FC_LAMBDA_HANDOVER_NATIVE "__lambda_handover"
static void DefineCaptures(FcResolver* r, FcLambdaExpr* lambda, FcResolverScope* enclosing) {
    FcList* names = fcListNew(R_);
    FcDict* paramNames = fcDictNew(R_, FC_KEY_STR);
    FcList* captures = NULL;
    FcList* loads = NULL;
    FcList* implicitNames = NULL;
    int i;
    fcAstNamesCollect(R_, (FcStmt*)lambda->body, names, fcDictNew(R_, FC_KEY_STR));
    for (i = 0; i < lambda->params->count; i++) fcSetAdd(paramNames, ((FcLambdaParam*)lambda->params->items[i])->name);
    for (i = 0; i < names->count; i++) {
        const char* name = (const char*)names->items[i];
        int depth = 0;
        FcResolverScope* found = NULL;
        FcResolverScope* scope;
        bool reachesFire = false;
        FcIdentifierExpr* outer;
        const char* requiredUnit = NULL;
        FcCapture* mode = fcCaptureMode(lambda, name);
        void* slotV;
        FcExpr* load;
        if (fcDictHas(paramNames, name)) continue;
        for (scope = enclosing; scope != NULL; scope = scope->parent, depth++) {
            if (scope->isGlobal) { reachesFire = scope == r->fireScope; break; }
            if (fcDictHas(&scope->slots, name)) { found = scope; break; }
        }
        outer = FC_NEW(R_, FcIdentifierExpr, .h = {EK_IdentifierExpr, lambda->h.line}, .name = name);
        if (found != NULL) {
            FcResolvedRef* ref = fcRef(R_, RR_Local);
            requiredUnit = (const char*)fcDictGet(&found->requiredUnits, name);
            ref->depth = depth; ref->slot = FC_VALI(fcDictGet(&found->slots, name)); ref->requiredUnit = requiredUnit; ref->byRef = fcDictHas(&found->refNames, name);
            fcDictPut(&r->refs, outer, ref);
        } else if (reachesFire && fcDictTry(&r->fireScope->slots, name, &slotV) && FC_VALI(slotV) >= r->fireSharedSlots && name[0] != '\x01') {
            FcResolvedRef* ref = fcRef(R_, RR_Global);
            requiredUnit = (const char*)fcDictGet(&r->fireScope->requiredUnits, name);
            ref->slot = FC_VALI(slotV); ref->requiredUnit = requiredUnit;
            fcDictPut(&r->refs, outer, ref);
        } else if (!reachesFire && fcDictTry(&r->globalScope->slots, name, &slotV) && name[0] != '\x01' && name[0] != '$' && mode != NULL) {
            FcResolvedRef* ref;
            if (mode->handover) RERR(lambda->h.line, "'handover %s': '%s' is a global variable - it belongs to the program and cannot move into a lambda; write 'taking %s' (a copy)", name, name, name);
            requiredUnit = (const char*)fcDictGet(&r->globalScope->requiredUnits, name);
            ref = fcRef(R_, RR_Global);
            ref->slot = FC_VALI(slotV); ref->requiredUnit = requiredUnit;
            fcDictPut(&r->refs, outer, ref);
        } else {
            if (mode != NULL) RERR(lambda->h.line, "'%s %s' in a lambda names a variable of the code around it - '%s' is none", mode->handover ? "handover" : "taking", name, name);
            continue;
        }
        Define(r, name, lambda->h.line, false, requiredUnit, false, 0, NULL, 0);
        fcSetAdd(&r->current->captureNames, name);
        if (!captures) captures = fcListNew(R_);
        fcListAdd(R_, captures, outer);
        load = (FcExpr*)FC_NEW(R_, FcUnaryExpr, .h = {EK_UnaryExpr, lambda->h.line}, .op = UOP_DeepCopy, .operand = (FcExpr*)outer);
        if (lambda->lendCaptures) load = (FcExpr*)outer;
        else if (mode == NULL) { if (!implicitNames) implicitNames = fcListNew(R_); fcListAdd(R_, implicitNames, (void*)name); }
        if (mode != NULL && mode->handover) {
            FcIdentifierExpr* mover = FC_NEW(R_, FcIdentifierExpr, .h = {EK_IdentifierExpr, lambda->h.line}, .name = FC_LAMBDA_HANDOVER_NATIVE);
            FcResolvedRef* ref = fcRef(R_, RR_Native);
            FcList* args = fcListNew(R_);
            ref->name = FC_LAMBDA_HANDOVER_NATIVE;
            fcDictPut(&r->refs, mover, ref);
            fcListAdd(R_, args, outer);
            load = (FcExpr*)FC_NEW(R_, FcCallExpr, .h = {EK_CallExpr, lambda->h.line}, .callee = (FcExpr*)mover, .args = args);
        }
        if (!loads) loads = fcListNew(R_);
        fcListAdd(R_, loads, load);
    }
    if (captures != NULL) {
        FcResolvedRef* ref = fcRef(R_, RR_LambdaCaptures);
        ref->variables = captures; ref->loads = loads; ref->implicitNames = implicitNames;
        fcDictPut(&r->refs, lambda, ref);
    }
}

static void ResolveLambdaCore(FcResolver* r, FcLambdaExpr* lambda);
static void ResolveLambda(FcResolver* r, FcLambdaExpr* lambda) {
    bool savedBound = r->inBoundLambda;
    r->inBoundLambda = lambda->onTarget != NULL;
    FC_TRY(R_) {
        ResolveLambdaCore(r, lambda);
    } FC_CATCH(R_) {
        r->inBoundLambda = savedBound;
        fcRethrow(R_);
    } FC_END_TRY;
    r->inBoundLambda = savedBound;
}
static void ResolveLambdaCore(FcResolver* r, FcLambdaExpr* lambda) {
    FcResolverScope* saved;
    FcResolverScope* enclosing;
    bool savedInConstructor;
    int savedLoopDepth, savedTryDepth, i;
    FcIntStack* savedLoopFinallyDepths;
    if (lambda->onTarget != NULL) ResolveExpr(r, lambda->onTarget);
    saved = r->current;
    enclosing = r->current;
    r->current = fcScopeNew(r, r->globalScope, false);
    savedInConstructor = r->inConstructor;
    r->inConstructor = false;
    ValidateOptionalParamsAreTrailing(r, lambda->params, lambda->h.line);
    for (i = 0; i < lambda->params->count; i++) if (((FcLambdaParam*)lambda->params->items[i])->defaultValue) ResolveExpr(r, ((FcLambdaParam*)lambda->params->items[i])->defaultValue);
    for (i = 0; i < lambda->params->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)lambda->params->items[i];
        if (p->type) ValidateTypeRef(r, p->type, lambda->h.line);
        ValidateNonNull(r, p->type, p->arrayRanks->count, lambda->h.line);
        ResolveArrayRanks(r, p->arrayRanks);
        Define(r, p->name, lambda->h.line, false, p->type ? fcUnitDemandOf(R_, p->type) : NULL, false, 0, NULL, 0);
    }
    if (lambda->autoCapture) DefineCaptures(r, lambda, enclosing);
    savedLoopDepth = r->loopDepth; savedTryDepth = r->tryDepth;
    r->loopDepth = 0;
    savedLoopFinallyDepths = r->loopFinallyDepths; r->loopFinallyDepths = NULL;
    r->tryDepth = 0;
    r->functionDepth++;
    for (i = 0; i < lambda->body->statements->count; i++) ResolveStmt(r, (FcStmt*)lambda->body->statements->items[i]);
    r->functionDepth--;
    r->loopDepth = savedLoopDepth; r->loopFinallyDepths = savedLoopFinallyDepths; r->tryDepth = savedTryDepth;
    r->inConstructor = savedInConstructor;
    r->current = saved;
}

/* ---- Resolver.StaticInits.cs ---- */
static const char* fcPair(FcResolver* r, const char* cls, const char* field) { return fcFmt(R_, "%s\x01%s", cls, field); }
static const char* fcPairClass(FcResolver* r, const char* key) { const char* sep = strchr(key, '\x01'); return fcStrN(R_, key, (size_t)(sep - key)); }
static const char* fcPairField(const char* key) { return strchr(key, '\x01') + 1; }

static const char* AccessorName(FcClassDecl* cd, FcBlockStmt* accessor) {
    int i;
    for (i = 0; i < cd->members->count; i++) {
        FcStmt* m = (FcStmt*)cd->members->items[i];
        if (m->kind == SK_PropertyDecl && (((FcPropertyDecl*)m)->getter == accessor || ((FcPropertyDecl*)m)->setter == accessor)) return ((FcPropertyDecl*)m)->name;
    }
    return "?";
}
static void fcRootAdd(FcResolver* r, FcCodeUnit* u, FcNodeTag tag, void* node) { fcListAdd(R_, u->rootTags, FC_IVAL(tag)); fcListAdd(R_, u->roots, node); }
static void fcRootDefaults(FcResolver* r, FcCodeUnit* u, FcList* params) {
    int i;
    for (i = 0; i < params->count; i++) if (((FcLambdaParam*)params->items[i])->defaultValue) fcRootAdd(r, u, FN_EXPR, ((FcLambdaParam*)params->items[i])->defaultValue);
}
/* CodeUnitOf: member is a MethodDecl, ConstructorDecl, FieldDecl (as FcStmt) or an accessor block */
static FcCodeUnit* CodeUnitOf(FcResolver* r, FcClassDecl* cd, FcStmt* member) {
    FcCodeUnit* unit = (FcCodeUnit*)fcDictGet(&r->codeUnits, member);
    int i;
    if (unit) return unit;
    unit = (FcCodeUnit*)fcAlloc(R_, sizeof *unit);
    unit->cls = cd; unit->roots = fcListNew(R_); unit->rootTags = fcListNew(R_);
    switch (member->kind) {
        case SK_MethodDecl: {
            FcMethodDecl* md = (FcMethodDecl*)member;
            unit->name = fcFmt(R_, "%s.%s()", cd->name, md->name);
            fcRootAdd(r, unit, FN_STMT, md->body);
            fcRootDefaults(r, unit, md->params);
            break;
        }
        case SK_ConstructorDecl: {
            FcConstructorDecl* ctor = (FcConstructorDecl*)member;
            unit->name = fcFmt(R_, "new %s(...)", cd->name);
            fcRootAdd(r, unit, FN_STMT, ctor->body);
            if (ctor->baseArgs) for (i = 0; i < ctor->baseArgs->count; i++) fcRootAdd(r, unit, FN_EXPR, ctor->baseArgs->items[i]);
            fcRootDefaults(r, unit, ctor->params);
            break;
        }
        case SK_FieldDecl: {
            FcFieldDecl* fd = (FcFieldDecl*)member;
            unit->name = fcFmt(R_, "%s.%s", cd->name, fd->name);
            if (fd->initializer) fcRootAdd(r, unit, FN_EXPR, fd->initializer);
            break;
        }
        case SK_BlockStmt:
            unit->name = fcFmt(R_, "%s.%s", cd->name, AccessorName(cd, (FcBlockStmt*)member));
            fcRootAdd(r, unit, FN_STMT, member);
            break;
        default: unit->name = cd->name; break;
    }
    fcDictPut(&r->codeUnits, member, unit);
    return unit;
}

/* Walk (StaticInits): the nodes below the roots that run when it runs, in preorder (not the body of a lambda, not the target of a plain assignment) */
typedef struct FcSiWalk { FcResolver* r; FcList* tags; FcList* nodes; } FcSiWalk;
static void fcSiNode(FcSiWalk* w, FcNodeTag tag, void* node);
static void fcSiExprs(FcSiWalk* w, FcList* l) { int i; if (l) for (i = 0; i < l->count; i++) if (l->items[i]) fcSiNode(w, FN_EXPR, l->items[i]); }
static void fcSiStmts(FcSiWalk* w, FcList* l) { int i; if (l) for (i = 0; i < l->count; i++) if (l->items[i]) fcSiNode(w, FN_STMT, l->items[i]); }
static void fcSiE(FcSiWalk* w, FcExpr* e) { if (e) fcSiNode(w, FN_EXPR, e); }
static void fcSiS(FcSiWalk* w, FcStmt* s) { if (s) fcSiNode(w, FN_STMT, s); }
static void fcSiNode(FcSiWalk* w, FcNodeTag tag, void* node) {
    FcResolver* r = w->r;
    int i;
    if (tag == FN_EXPR && ((FcExpr*)node)->kind == EK_LambdaExpr) return;
    fcListAdd(R_, w->tags, FC_IVAL(tag));
    fcListAdd(R_, w->nodes, node);
    if (tag == FN_EXPR) {
        FcExpr* e = (FcExpr*)node;
        switch (e->kind) {
            case EK_AssignExpr: {
                FcAssignExpr* a = (FcAssignExpr*)e;
                if (a->target->kind == EK_MemberExpr) fcSiE(w, ((FcMemberExpr*)a->target)->target);
                else if (a->target->kind != EK_IdentifierExpr) fcSiE(w, a->target);
                fcSiE(w, a->value);
                break;
            }
            /* the properties of the records in their order: Expr and Stmt values, a LambdaParam's default value, the Expr/Stmt items of a list */
            case EK_SyncExpr: fcSiE(w, ((FcSyncExpr*)e)->target); break;
            case EK_ProbeExpr: fcSiE(w, ((FcProbeExpr*)e)->target); fcSiE(w, ((FcProbeExpr*)e)->handler); break;
            case EK_TryProcessExpr: fcSiE(w, ((FcTryProcessExpr*)e)->target); break;
            case EK_TryCallExpr: fcSiE(w, ((FcTryCallExpr*)e)->call); break;
            case EK_ConditionalAccessExpr: fcSiE(w, ((FcConditionalAccessExpr*)e)->target); fcSiE(w, ((FcConditionalAccessExpr*)e)->inner); fcSiE(w, ((FcConditionalAccessExpr*)e)->placeholder); break;
            case EK_NewExpr: fcSiExprs(w, ((FcNewExpr*)e)->args); break;
            case EK_NewArrayExpr: fcSiExprs(w, ((FcNewArrayExpr*)e)->sizeExprs); break;
            case EK_NewBufferExpr: fcSiE(w, ((FcNewBufferExpr*)e)->sizeExpr); break;
            case EK_ArrayLiteralExpr: fcSiExprs(w, ((FcArrayLiteralExpr*)e)->elements); break;
            case EK_ThrowExpr: fcSiE(w, ((FcThrowExpr*)e)->value); break;
            case EK_UnaryExpr: fcSiE(w, ((FcUnaryExpr*)e)->operand); break;
            case EK_BinaryExpr: fcSiE(w, ((FcBinaryExpr*)e)->left); fcSiE(w, ((FcBinaryExpr*)e)->right); break;
            case EK_RebindExpr: fcSiE(w, ((FcRebindExpr*)e)->lambda); fcSiE(w, ((FcRebindExpr*)e)->target); break;
            case EK_ConditionalExpr: fcSiE(w, ((FcConditionalExpr*)e)->condition); fcSiE(w, ((FcConditionalExpr*)e)->then); fcSiE(w, ((FcConditionalExpr*)e)->otherwise); break;
            case EK_UnitCoerceExpr: fcSiE(w, ((FcUnitCoerceExpr*)e)->operand); break;
            case EK_TypeCoerceExpr: fcSiE(w, ((FcTypeCoerceExpr*)e)->operand); break;
            case EK_IsInExpr: fcSiE(w, ((FcIsInExpr*)e)->operand); break;
            case EK_RangeExpr: fcSiE(w, ((FcRangeExpr*)e)->from); fcSiE(w, ((FcRangeExpr*)e)->to); break;
            case EK_IsOfExpr: fcSiE(w, ((FcIsOfExpr*)e)->operand); break;
            case EK_IsFromExpr: fcSiE(w, ((FcIsFromExpr*)e)->operand); fcSiE(w, ((FcIsFromExpr*)e)->ownerExpr); break;
            case EK_UntilExpr: fcSiE(w, ((FcUntilExpr*)e)->inner); fcSiExprs(w, ((FcUntilExpr*)e)->limits); break;
            case EK_AwaitOnExpr: fcSiE(w, ((FcAwaitOnExpr*)e)->lambda); fcSiExprs(w, ((FcAwaitOnExpr*)e)->args); break;
            case EK_CallExpr: fcSiE(w, ((FcCallExpr*)e)->callee); fcSiExprs(w, ((FcCallExpr*)e)->args); break;
            case EK_MemberExpr: fcSiE(w, ((FcMemberExpr*)e)->target); break;
            case EK_IndexExpr: fcSiE(w, ((FcIndexExpr*)e)->target); fcSiE(w, ((FcIndexExpr*)e)->index); break;
            case EK_WithExpr: fcSiE(w, ((FcWithExpr*)e)->source); break;
            case EK_IncDecExpr: fcSiE(w, ((FcIncDecExpr*)e)->target); break;
            default: break;
        }
        return;
    }
    {
        FcStmt* s = (FcStmt*)node;
        switch (s->kind) {
            case SK_BlockStmt: fcSiStmts(w, ((FcBlockStmt*)s)->statements); break;
            case SK_ExprStmt: fcSiE(w, ((FcExprStmt*)s)->expression); break;
            case SK_TimeoutDirective: case SK_EndWaitDirective: fcSiE(w, ((FcTimeoutDirective*)s)->value); break;
            case SK_VarDeclStmt: fcSiExprs(w, ((FcVarDeclStmt*)s)->arrayRanks); fcSiE(w, ((FcVarDeclStmt*)s)->initializer); break;
            case SK_IfStmt: fcSiE(w, ((FcIfStmt*)s)->condition); fcSiS(w, ((FcIfStmt*)s)->then); fcSiS(w, ((FcIfStmt*)s)->otherwise); break;
            case SK_WhileStmt: fcSiE(w, ((FcWhileStmt*)s)->condition); fcSiS(w, ((FcWhileStmt*)s)->body); break;
            case SK_DoWhileStmt: fcSiS(w, ((FcDoWhileStmt*)s)->body); fcSiE(w, ((FcDoWhileStmt*)s)->condition); break;
            case SK_ForStmt: fcSiS(w, ((FcForStmt*)s)->init); fcSiE(w, ((FcForStmt*)s)->condition); fcSiE(w, ((FcForStmt*)s)->increment); fcSiS(w, ((FcForStmt*)s)->body); break;
            case SK_ForeachStmt: fcSiE(w, ((FcForeachStmt*)s)->iterable); fcSiS(w, ((FcForeachStmt*)s)->body); break;
            case SK_ReturnStmt: case SK_ThrowStmt: case SK_TerminateStmt: fcSiE(w, ((FcReturnStmt*)s)->value); break;
            case SK_DeleteStmt: case SK_ProcessStmt: fcSiE(w, ((FcDeleteStmt*)s)->target); break;
            case SK_FireStmt: fcSiS(w, (FcStmt*)((FcFireStmt*)s)->body); fcSiExprs(w, ((FcFireStmt*)s)->until); break;
            case SK_SilenceStmt: fcSiE(w, ((FcSilenceStmt*)s)->target); break;
            case SK_PostGlobalStmt: fcSiE(w, ((FcPostGlobalStmt*)s)->lambda); fcSiExprs(w, ((FcPostGlobalStmt*)s)->args); break;
            case SK_CatchThreadsDecl: fcSiS(w, (FcStmt*)((FcCatchThreadsDecl*)s)->body); break;
            case SK_CatchTerminateDecl: fcSiS(w, (FcStmt*)((FcCatchTerminateDecl*)s)->body); break;
            case SK_TryStmt: fcSiS(w, (FcStmt*)((FcTryStmt*)s)->tryBlock); fcSiS(w, (FcStmt*)((FcTryStmt*)s)->finallyBlock); break;
            case SK_FieldDecl: fcSiExprs(w, ((FcFieldDecl*)s)->arrayRanks); fcSiE(w, ((FcFieldDecl*)s)->initializer); break;
            case SK_MethodDecl: fcSiS(w, (FcStmt*)((FcMethodDecl*)s)->body); break;
            case SK_ConstructorDecl: fcSiExprs(w, ((FcConstructorDecl*)s)->baseArgs); fcSiS(w, (FcStmt*)((FcConstructorDecl*)s)->body); break;
            case SK_DestructorDecl: case SK_UnsafeStmt: fcSiS(w, (FcStmt*)((FcDestructorDecl*)s)->body); break;
            case SK_ClassDecl: fcSiStmts(w, ((FcClassDecl*)s)->members); break;
            case SK_PropertyDecl: fcSiS(w, (FcStmt*)((FcPropertyDecl*)s)->getter); fcSiS(w, (FcStmt*)((FcPropertyDecl*)s)->setter); break;
            case SK_ClassExtensionDecl: fcSiStmts(w, ((FcClassExtensionDecl*)s)->members); break;
            case SK_NamespaceDecl: fcSiStmts(w, ((FcNamespaceDecl*)s)->members); break;
            default: break;
        }
    }
    (void)i;
}
static FcSiWalk fcSiWalk(FcResolver* r, FcList* tags, FcList* roots) {
    FcSiWalk w;
    int i;
    w.r = r; w.tags = fcListNew(R_); w.nodes = fcListNew(R_);
    for (i = 0; i < roots->count; i++) fcSiNode(&w, (FcNodeTag)FC_VALI(tags->items[i]), roots->items[i]);
    return w;
}

typedef struct FcFactsCtx { FcResolver* r; FcList* reads; FcList* calls; } FcFactsCtx;
static void fcFactsMember(FcFactsCtx* f, const char* className, const char* name, bool isStatic) {
    FcResolver* r = f->r;
    FcClassDecl* cd;
    int i;
    for (cd = (FcClassDecl*)fcDictGet(&r->classes, className); cd != NULL; cd = GetBaseClassDecl(r, cd)) {
        bool found = false;
        for (i = 0; i < cd->members->count; i++) {
            FcStmt* m = (FcStmt*)cd->members->items[i];
            if (m->kind == SK_FieldDecl && streq0(((FcFieldDecl*)m)->name, name) && ((FcFieldDecl*)m)->isStatic == isStatic) {
                if (isStatic) fcListAdd(R_, f->reads, (void*)fcPair(r, cd->name, ((FcFieldDecl*)m)->name));
                found = true;
            } else if (m->kind == SK_PropertyDecl && streq0(((FcPropertyDecl*)m)->name, name) && ((FcPropertyDecl*)m)->isStatic == isStatic) {
                FcPropertyDecl* p = (FcPropertyDecl*)m;
                if (p->getter) fcListAdd(R_, f->calls, CodeUnitOf(r, cd, (FcStmt*)p->getter));
                if (p->setter) fcListAdd(R_, f->calls, CodeUnitOf(r, cd, (FcStmt*)p->setter));
                found = true;
            } else if (m->kind == SK_MethodDecl && streq0(((FcMethodDecl*)m)->name, name) && ((FcMethodDecl*)m)->isStatic == isStatic) {
                fcListAdd(R_, f->calls, CodeUnitOf(r, cd, m));
                found = true;
            }
        }
        if (found) return;
    }
}
static void fcFactsNew(FcFactsCtx* f, const char* className) {
    FcResolver* r = f->r;
    FcClassDecl* cd;
    int i;
    for (cd = (FcClassDecl*)fcDictGet(&r->classes, className); cd != NULL; cd = GetBaseClassDecl(r, cd))
        for (i = 0; i < cd->members->count; i++) {
            FcStmt* m = (FcStmt*)cd->members->items[i];
            if (m->kind == SK_ConstructorDecl || (m->kind == SK_FieldDecl && !((FcFieldDecl*)m)->isStatic && ((FcFieldDecl*)m)->initializer)) fcListAdd(R_, f->calls, CodeUnitOf(r, cd, m));
        }
}
static FcUnitFacts* Facts(FcResolver* r, FcCodeUnit* unit) {
    FcUnitFacts* known = (FcUnitFacts*)fcDictGet(&r->unitFacts, unit);
    FcFactsCtx f;
    FcSiWalk w;
    int i;
    if (known) return known;
    f.r = r; f.reads = fcListNew(R_); f.calls = fcListNew(R_);
    w = fcSiWalk(r, unit->rootTags, unit->roots);
    for (i = 0; i < w.nodes->count; i++) {
        FcExpr* e;
        FcResolvedRef* ref;
        const char* receiver;
        if (FC_VALI(w.tags->items[i]) != FN_EXPR) continue;
        e = (FcExpr*)w.nodes->items[i];
        if (e->kind == EK_IdentifierExpr && (ref = (FcResolvedRef*)fcDictGet(&r->refs, e)) != NULL) {
            if (ref->kind == RR_StaticMember) fcFactsMember(&f, ref->className, ((FcIdentifierExpr*)e)->name, true);
            else if (ref->kind == RR_ImplicitThisMember) fcFactsMember(&f, unit->cls->name, ((FcIdentifierExpr*)e)->name, false);
        } else if (e->kind == EK_MemberExpr && (ref = (FcResolvedRef*)fcDictGet(&r->refs, e)) != NULL && ref->kind == RR_StaticMember)
            fcFactsMember(&f, ref->className, ((FcMemberExpr*)e)->name, true);
        else if (e->kind == EK_MemberExpr && (receiver = (const char*)fcDictGet(&r->receiverClasses, e)) != NULL)
            fcFactsMember(&f, receiver, ((FcMemberExpr*)e)->name, false);
        else if (e->kind == EK_NewExpr) {
            FcNewExpr* ne = (FcNewExpr*)e;
            fcFactsNew(&f, fcResolveNewTarget(R_, ne->classRef, ne->typeArgs ? ne->typeArgs->count : 0, K(r, kClassesHas)));
        }
    }
    known = (FcUnitFacts*)fcAlloc(R_, sizeof *known);
    known->reads = f.reads; known->calls = f.calls;
    fcDictPut(&r->unitFacts, unit, known);
    return known;
}

typedef struct FcNeed { const char* field; const char* via; } FcNeed;
typedef struct FcStaticField { FcClassDecl* cls; FcFieldDecl* field; } FcStaticField;
typedef struct FcTarjan {
    FcResolver* r;
    FcDict index, low, onStack;
    FcList* stack;
    int counter;
    FcDict* needs;       /* key -> FcList* of FcNeed* */
    FcList* fields;      /* FcStaticField* */
} FcTarjan;

static void ReportCycle(FcResolver* r, FcList* part, FcDict* needs, FcList* order);
static void fcConnect(FcTarjan* t, const char* v) {
    FcResolver* r = t->r;
    FcList* nv = (FcList*)fcDictGet(t->needs, v);
    FcList* part;
    const char* x;
    int i;
    bool selfNeed = false;
    fcDictPut(&t->index, v, FC_IVAL(t->counter));
    fcDictPut(&t->low, v, FC_IVAL(t->counter));
    t->counter++;
    fcListAdd(R_, t->stack, (void*)v);
    fcSetAdd(&t->onStack, v);
    for (i = 0; i < nv->count; i++) {
        const char* w = ((FcNeed*)nv->items[i])->field;
        if (!fcDictHas(&t->index, w)) {
            int lv, lw;
            fcConnect(t, w);
            lv = FC_VALI(fcDictGet(&t->low, v)); lw = FC_VALI(fcDictGet(&t->low, w));
            fcDictPut(&t->low, v, FC_IVAL(lv < lw ? lv : lw));
        } else if (fcDictHas(&t->onStack, w)) {
            int lv = FC_VALI(fcDictGet(&t->low, v)), iw = FC_VALI(fcDictGet(&t->index, w));
            fcDictPut(&t->low, v, FC_IVAL(lv < iw ? lv : iw));
        }
    }
    if (FC_VALI(fcDictGet(&t->low, v)) != FC_VALI(fcDictGet(&t->index, v))) return;
    part = fcListNew(R_);
    do { x = (const char*)t->stack->items[--t->stack->count]; fcDictRemove(&t->onStack, x); fcListAdd(R_, part, (void*)x); } while (!streq0(x, v));
    for (i = 0; i < nv->count; i++) if (streq0(((FcNeed*)nv->items[i])->field, v)) selfNeed = true;
    if (part->count == 1 && !selfNeed) return;
    ReportCycle(r, part, t->needs, t->fields);
}

static void OrderStaticInits(FcResolver* r) {
    FcList* fields = fcListNew(R_);
    FcDict* keys = fcDictNew(R_, FC_KEY_STR);
    FcDict* needs = fcDictNew(R_, FC_KEY_STR);
    FcTarjan t;
    int i, k, j;
    FC_DICT_EACH(&r->classes, e) {
        FcClassDecl* cd = (FcClassDecl*)e->value;
        for (i = 0; i < cd->members->count; i++) {
            FcStmt* m = (FcStmt*)cd->members->items[i];
            FcFieldDecl* fd;
            bool there = false;
            if (m->kind != SK_FieldDecl) continue;
            fd = (FcFieldDecl*)m;
            if (!fd->isStatic || fd->initializer == NULL) continue;
            for (k = 0; k < fields->count; k++) {
                FcStaticField* f = (FcStaticField*)fields->items[k];
                if (f->cls == cd && streq0(f->field->name, fd->name)) there = true;
            }
            if (!there) { FcStaticField* f = (FcStaticField*)fcAlloc(R_, sizeof *f); f->cls = cd; f->field = fd; fcListAdd(R_, fields, f); }
        }
    }
    if (fields->count == 0) return;
    for (i = 0; i < fields->count; i++) { FcStaticField* f = (FcStaticField*)fields->items[i]; fcSetAdd(keys, fcPair(r, f->cls->name, f->field->name)); }
    for (i = 0; i < fields->count; i++) {
        FcStaticField* sf = (FcStaticField*)fields->items[i];
        FcClassDecl* cd = sf->cls;
        FcFieldDecl* fd = sf->field;
        const char* key = fcPair(r, cd->name, fd->name);
        FcList* found = fcListNew(R_);
        FcDict* seenFields = fcDictNew(R_, FC_KEY_STR);
        FcCodeUnit* start = CodeUnitOf(r, cd, (FcStmt*)fd);
        FcUnitFacts* facts = Facts(r, start);
        FcDict* visited = fcDictNew(R_, FC_KEY_PTR);
        FcList* queueUnits = fcListNew(R_);
        FcList* queueVia = fcListNew(R_);
        FcList* deps = fcListNew(R_);
        int head = 0;
        FcList* rootTags = fcListNew(R_);
        FcList* roots = fcListNew(R_);
        FcSiWalk w;
        for (k = 0; k < facts->reads->count; k++) {
            const char* read = (const char*)facts->reads->items[k];
            if (fcDictHas(keys, read) && fcSetAdd(seenFields, read)) { FcNeed* n = (FcNeed*)fcAlloc(R_, sizeof *n); n->field = read; fcListAdd(R_, found, n); }
        }
        fcSetAdd(visited, start);
        for (k = 0; k < facts->calls->count; k++) {
            FcCodeUnit* call = (FcCodeUnit*)facts->calls->items[k];
            if (fcSetAdd(visited, call)) { fcListAdd(R_, queueUnits, call); fcListAdd(R_, queueVia, (void*)call->name); }
        }
        while (head < queueUnits->count) {
            FcCodeUnit* unit = (FcCodeUnit*)queueUnits->items[head];
            const char* via = (const char*)queueVia->items[head];
            FcUnitFacts* f2;
            head++;
            f2 = Facts(r, unit);
            for (k = 0; k < f2->reads->count; k++) {
                const char* read = (const char*)f2->reads->items[k];
                if (fcDictHas(keys, read) && fcSetAdd(seenFields, read)) { FcNeed* n = (FcNeed*)fcAlloc(R_, sizeof *n); n->field = read; n->via = via; fcListAdd(R_, found, n); }
            }
            for (k = 0; k < f2->calls->count; k++) {
                FcCodeUnit* call = (FcCodeUnit*)f2->calls->items[k];
                if (fcSetAdd(visited, call)) { fcListAdd(R_, queueUnits, call); fcListAdd(R_, queueVia, (void*)via); }
            }
        }
        fcDictPut(needs, key, found);
        for (k = 0; k < found->count; k++) fcListAdd(R_, deps, (void*)((FcNeed*)found->items[k])->field);
        fcDictPut(&r->staticInitDeps, key, deps);
        fcListAdd(R_, rootTags, FC_IVAL(FN_EXPR));
        fcListAdd(R_, roots, fd->initializer);
        w = fcSiWalk(r, rootTags, roots);
        for (j = 0; j < w.nodes->count; j++) {
            FcResolvedRef* ref;
            FcExpr* e;
            if (FC_VALI(w.tags->items[j]) != FN_EXPR) continue;
            e = (FcExpr*)w.nodes->items[j];
            if (e->kind == EK_IdentifierExpr && (ref = (FcResolvedRef*)fcDictGet(&r->refs, e)) != NULL && ref->kind == RR_Global) {
                FcError* error = fcRErrorNew(r, e->line, fcFmt(R_, "The initialiser of the static field '%s.%s' reads the top-level variable '%s' - static fields are initialised before the top-level code runs, so it is still undefined there (make it a static field, or set the static field in the top-level code)", cd->name, fd->name, ((FcIdentifierExpr*)e)->name));
                error->source = cd->h.source;
                AddError(r, error);
            }
        }
    }
    memset(&t, 0, sizeof t);
    t.r = r;
    fcDictInit(R_, &t.index, FC_KEY_STR);
    fcDictInit(R_, &t.low, FC_KEY_STR);
    fcDictInit(R_, &t.onStack, FC_KEY_STR);
    t.stack = fcListNew(R_);
    t.needs = needs;
    t.fields = fields;
    for (i = 0; i < fields->count; i++) {
        FcStaticField* f = (FcStaticField*)fields->items[i];
        const char* key = fcPair(r, f->cls->name, f->field->name);
        if (!fcDictHas(&t.index, key)) fcConnect(&t, key);
    }
}

static const char* fcStep(FcResolver* r, const char* f, const char* via) {
    return via == NULL ? fcFmt(R_, "%s.%s", fcPairClass(r, f), fcPairField(f)) : fcFmt(R_, "(through %s) %s.%s", via, fcPairClass(r, f), fcPairField(f));
}
static void ReportCycle(FcResolver* r, FcList* part, FcDict* needs, FcList* order) {
    FcDict* inPart = fcDictNew(R_, FC_KEY_STR);
    FcStaticField* first = NULL;
    const char* start;
    FcClassDecl* cd;
    FcFieldDecl* fd;
    FcList* startNeeds;
    FcDict* previousFrom = fcDictNew(R_, FC_KEY_STR);
    FcDict* previousVia = fcDictNew(R_, FC_KEY_STR);
    FcList* queue = fcListNew(R_);
    int head = 0, i;
    bool closed = false;
    const char* backFrom = NULL;
    const char* backVia = NULL;
    FcList* steps = fcListNew(R_);
    FcBuf b = {0};
    const char* at;
    FcError* error;
    for (i = 0; i < part->count; i++) fcSetAdd(inPart, part->items[i]);
    for (i = 0; i < order->count && !first; i++) {
        FcStaticField* f = (FcStaticField*)order->items[i];
        if (fcDictHas(inPart, fcPair(r, f->cls->name, f->field->name))) first = f;
    }
    start = fcPair(r, first->cls->name, first->field->name);
    cd = first->cls; fd = first->field;
    startNeeds = (FcList*)fcDictGet(needs, start);
    if (part->count == 1) {
        FcNeed* self = NULL;
        for (i = 0; i < startNeeds->count && !self; i++) if (streq0(((FcNeed*)startNeeds->items[i])->field, start)) self = (FcNeed*)startNeeds->items[i];
        if (self->via == NULL) {
            error = fcRErrorNew(r, fd->h.line, fcFmt(R_, "The initialiser of the static field '%s.%s' reads the field itself - it is still undefined there", cd->name, fd->name));
            error->source = cd->h.source;
            AddError(r, error);
            return;
        }
    }
    fcListAdd(R_, queue, (void*)start);
    while (head < queue->count && !closed) {
        const char* v = (const char*)queue->items[head++];
        FcList* nv = (FcList*)fcDictGet(needs, v);
        for (i = 0; i < nv->count; i++) {
            FcNeed* n = (FcNeed*)nv->items[i];
            if (!fcDictHas(inPart, n->field)) continue;
            if (streq0(n->field, start)) { backFrom = v; backVia = n->via; closed = true; break; }
            if (fcDictHas(previousFrom, n->field)) continue;
            fcDictPut(previousFrom, n->field, (void*)v);
            fcDictPut(previousVia, n->field, (void*)n->via);
            fcListAdd(R_, queue, (void*)n->field);
        }
    }
    fcListAdd(R_, steps, (void*)fcStep(r, start, backVia));
    for (at = backFrom; !streq0(at, start); at = (const char*)fcDictGet(previousFrom, at)) fcListAdd(R_, steps, (void*)fcStep(r, at, (const char*)fcDictGet(previousVia, at)));
    fcListAdd(R_, steps, (void*)fcFmt(R_, "%s.%s", fcPairClass(r, start), fcPairField(start)));
    for (i = steps->count - 1; i >= 0; i--) { fcBufStr(&b, (const char*)steps->items[i]); if (i) fcBufStr(&b, " -> "); }
    error = fcRErrorNew(r, fd->h.line, fcFmt(R_, "The initialisers of static fields need each other in a cycle: %s - each one needs the value of the next one first; break the cycle (set one of them later, e.g. in a static method)", fcBufDone(R_, &b)));
    error->source = cd->h.source;
    AddError(r, error);
}

/* the value equality of the records for HashSet<Expr> (_takeAllowed): the node kind, its line and its fields (a list or a reference type by reference, a
 * nested record by value) - enough for the expressions that go into the set (`take x`, `try take x`) */
static bool fcExprRecordEquals(const void* a, const void* b) {
    const FcExpr* x = (const FcExpr*)a;
    const FcExpr* y = (const FcExpr*)b;
    if (x == y) return true;
    if (!x || !y || x->kind != y->kind || x->line != y->line) return false;
    switch (x->kind) {
        case EK_IdentifierExpr: return streq0(((const FcIdentifierExpr*)x)->name, ((const FcIdentifierExpr*)y)->name);
        case EK_ThisExpr: case EK_BaseExpr: case EK_SelfClassExpr: case EK_SyncGlobalsExpr: return true;
        case EK_UnaryExpr: return ((const FcUnaryExpr*)x)->op == ((const FcUnaryExpr*)y)->op && fcExprRecordEquals(((const FcUnaryExpr*)x)->operand, ((const FcUnaryExpr*)y)->operand);
        case EK_MemberExpr: return streq0(((const FcMemberExpr*)x)->name, ((const FcMemberExpr*)y)->name) && fcExprRecordEquals(((const FcMemberExpr*)x)->target, ((const FcMemberExpr*)y)->target);
        case EK_IndexExpr: return fcExprRecordEquals(((const FcIndexExpr*)x)->target, ((const FcIndexExpr*)y)->target) && fcExprRecordEquals(((const FcIndexExpr*)x)->index, ((const FcIndexExpr*)y)->index);
        default: return false;
    }
}
static uint32_t fcExprRecordHash(const void* a) {
    const FcExpr* x = (const FcExpr*)a;
    uint32_t h = (uint32_t)x->kind * 31u + (uint32_t)x->line;
    if (x->kind == EK_UnaryExpr) h = h * 31u + fcExprRecordHash(((const FcUnaryExpr*)x)->operand);
    else if (x->kind == EK_IdentifierExpr) h = h * 31u + fbHash(((const FcIdentifierExpr*)x)->name, strlen(((const FcIdentifierExpr*)x)->name));
    else if (x->kind == EK_MemberExpr) h = h * 31u + fbHash(((const FcMemberExpr*)x)->name, strlen(((const FcMemberExpr*)x)->name));
    return h;
}
