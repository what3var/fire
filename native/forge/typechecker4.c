/* native/forge/typechecker4.c - TypeChecker.cs, last part: TC031 (what a call owns that the callee keeps without taking it), AstNodes; then
 * TypeChecker.TakeLocal.cs (TC035), SetterClaims.cs, CollectInterfaces and Check/Run (included by typechecker3.c). */

/* AstNodes / AllNodes: every node below `root` in preorder, by the properties of the records - Expr and Stmt values and the Expr/Stmt items of
 * lists (`catches`: also a CatchClause, AllNodes of TakeLocal; `intoLambdas` false: a lambda's own nodes are left out) */
typedef struct FcNodes { FcList* tags; FcList* nodes; } FcNodes;
static void fcKids(FcTC* t, FcNodeTag tag, void* node, bool catches, FcList* kt, FcList* kn) {
    int i;
#define KE(e) do { if (e) { fcListAdd(TC_, kt, FC_IVAL(FN_EXPR)); fcListAdd(TC_, kn, (void*)(e)); } } while (0)
#define KS(s) do { if (s) { fcListAdd(TC_, kt, FC_IVAL(FN_STMT)); fcListAdd(TC_, kn, (void*)(s)); } } while (0)
#define KEL(l) do { if (l) for (i = 0; i < (l)->count; i++) KE((l)->items[i]); } while (0)
#define KSL(l) do { if (l) for (i = 0; i < (l)->count; i++) KS((l)->items[i]); } while (0)
    if (tag == FN_CATCH) { KS(((FcCatchClause*)node)->body); return; }
    if (tag == FN_EXPR) {
        FcExpr* e = (FcExpr*)node;
        switch (e->kind) {
            case EK_LambdaExpr: KE(((FcLambdaExpr*)e)->onTarget); KS(((FcLambdaExpr*)e)->body); break;
            case EK_SyncExpr: KE(((FcSyncExpr*)e)->target); break;
            case EK_ProbeExpr: KE(((FcProbeExpr*)e)->target); KE(((FcProbeExpr*)e)->handler); break;
            case EK_TryProcessExpr: KE(((FcTryProcessExpr*)e)->target); break;
            case EK_TryCallExpr: KE(((FcTryCallExpr*)e)->call); break;
            case EK_ConditionalAccessExpr: KE(((FcConditionalAccessExpr*)e)->target); KE(((FcConditionalAccessExpr*)e)->inner); KE(((FcConditionalAccessExpr*)e)->placeholder); break;
            case EK_NewExpr: KEL(((FcNewExpr*)e)->args); break;
            case EK_NewArrayExpr: KEL(((FcNewArrayExpr*)e)->sizeExprs); break;
            case EK_NewBufferExpr: KE(((FcNewBufferExpr*)e)->sizeExpr); break;
            case EK_ArrayLiteralExpr: KEL(((FcArrayLiteralExpr*)e)->elements); break;
            case EK_ThrowExpr: KE(((FcThrowExpr*)e)->value); break;
            case EK_UnaryExpr: KE(((FcUnaryExpr*)e)->operand); break;
            case EK_BinaryExpr: KE(((FcBinaryExpr*)e)->left); KE(((FcBinaryExpr*)e)->right); break;
            case EK_RebindExpr: KE(((FcRebindExpr*)e)->lambda); KE(((FcRebindExpr*)e)->target); break;
            case EK_ConditionalExpr: KE(((FcConditionalExpr*)e)->condition); KE(((FcConditionalExpr*)e)->then); KE(((FcConditionalExpr*)e)->otherwise); break;
            case EK_UnitCoerceExpr: KE(((FcUnitCoerceExpr*)e)->operand); break;
            case EK_TypeCoerceExpr: KE(((FcTypeCoerceExpr*)e)->operand); break;
            case EK_IsInExpr: KE(((FcIsInExpr*)e)->operand); break;
            case EK_RangeExpr: KE(((FcRangeExpr*)e)->from); KE(((FcRangeExpr*)e)->to); break;
            case EK_IsOfExpr: KE(((FcIsOfExpr*)e)->operand); break;
            case EK_IsFromExpr: KE(((FcIsFromExpr*)e)->operand); KE(((FcIsFromExpr*)e)->ownerExpr); break;
            case EK_UntilExpr: KE(((FcUntilExpr*)e)->inner); KEL(((FcUntilExpr*)e)->limits); break;
            case EK_AwaitOnExpr: KE(((FcAwaitOnExpr*)e)->lambda); KEL(((FcAwaitOnExpr*)e)->args); break;
            case EK_CallExpr: KE(((FcCallExpr*)e)->callee); KEL(((FcCallExpr*)e)->args); break;
            case EK_MemberExpr: KE(((FcMemberExpr*)e)->target); break;
            case EK_IndexExpr: KE(((FcIndexExpr*)e)->target); KE(((FcIndexExpr*)e)->index); break;
            case EK_AssignExpr: KE(((FcAssignExpr*)e)->target); KE(((FcAssignExpr*)e)->value); break;
            case EK_WithExpr: KE(((FcWithExpr*)e)->source); break;
            case EK_IncDecExpr: KE(((FcIncDecExpr*)e)->target); break;
            default: break;
        }
        return;
    }
    {
        FcStmt* s = (FcStmt*)node;
        switch (s->kind) {
            case SK_BlockStmt: KSL(((FcBlockStmt*)s)->statements); break;
            case SK_ExprStmt: KE(((FcExprStmt*)s)->expression); break;
            case SK_TimeoutDirective: case SK_EndWaitDirective: KE(((FcTimeoutDirective*)s)->value); break;
            case SK_VarDeclStmt: KEL(((FcVarDeclStmt*)s)->arrayRanks); KE(((FcVarDeclStmt*)s)->initializer); break;
            case SK_IfStmt: KE(((FcIfStmt*)s)->condition); KS(((FcIfStmt*)s)->then); KS(((FcIfStmt*)s)->otherwise); break;
            case SK_WhileStmt: KE(((FcWhileStmt*)s)->condition); KS(((FcWhileStmt*)s)->body); break;
            case SK_DoWhileStmt: KS(((FcDoWhileStmt*)s)->body); KE(((FcDoWhileStmt*)s)->condition); break;
            case SK_ForStmt: KS(((FcForStmt*)s)->init); KE(((FcForStmt*)s)->condition); KE(((FcForStmt*)s)->increment); KS(((FcForStmt*)s)->body); break;
            case SK_ForeachStmt: KE(((FcForeachStmt*)s)->iterable); KS(((FcForeachStmt*)s)->body); break;
            case SK_ReturnStmt: case SK_ThrowStmt: case SK_TerminateStmt: KE(((FcReturnStmt*)s)->value); break;
            case SK_DeleteStmt: case SK_ProcessStmt: KE(((FcDeleteStmt*)s)->target); break;
            case SK_FireStmt: KS(((FcFireStmt*)s)->body); KEL(((FcFireStmt*)s)->until); break;
            case SK_SilenceStmt: KE(((FcSilenceStmt*)s)->target); break;
            case SK_PostGlobalStmt: KE(((FcPostGlobalStmt*)s)->lambda); KEL(((FcPostGlobalStmt*)s)->args); break;
            case SK_CatchThreadsDecl: KS(((FcCatchThreadsDecl*)s)->body); break;
            case SK_CatchTerminateDecl: KS(((FcCatchTerminateDecl*)s)->body); break;
            case SK_TryStmt: {
                FcTryStmt* ts = (FcTryStmt*)s;
                KS(ts->tryBlock);
                if (catches) for (i = 0; i < ts->catches->count; i++) { fcListAdd(TC_, kt, FC_IVAL(FN_CATCH)); fcListAdd(TC_, kn, ts->catches->items[i]); }
                KS(ts->finallyBlock);
                break;
            }
            case SK_FieldDecl: KEL(((FcFieldDecl*)s)->arrayRanks); KE(((FcFieldDecl*)s)->initializer); break;
            case SK_MethodDecl: KS(((FcMethodDecl*)s)->body); break;
            case SK_ConstructorDecl: KEL(((FcConstructorDecl*)s)->baseArgs); KS(((FcConstructorDecl*)s)->body); break;
            case SK_DestructorDecl: case SK_UnsafeStmt: KS(((FcDestructorDecl*)s)->body); break;
            case SK_ClassDecl: KSL(((FcClassDecl*)s)->members); break;
            case SK_PropertyDecl: KS(((FcPropertyDecl*)s)->getter); KS(((FcPropertyDecl*)s)->setter); break;
            case SK_ClassExtensionDecl: KSL(((FcClassExtensionDecl*)s)->members); break;
            case SK_NamespaceDecl: KSL(((FcNamespaceDecl*)s)->members); break;
            default: break;
        }
    }
#undef KE
#undef KS
#undef KEL
#undef KSL
}
static FcNodes AllNodes(FcTC* t, FcNodeTag tag, void* root, bool catches, bool intoLambdas) {
    FcNodes out;
    FcList* stackT = fcListNew(TC_);
    FcList* stackN = fcListNew(TC_);
    out.tags = fcListNew(TC_); out.nodes = fcListNew(TC_);
    fcListAdd(TC_, stackT, FC_IVAL(tag)); fcListAdd(TC_, stackN, root);
    while (stackN->count > 0) {
        FcNodeTag nt = (FcNodeTag)FC_VALI(stackT->items[--stackT->count]);
        void* node = stackN->items[--stackN->count];
        FcList* kt;
        FcList* kn;
        int k;
        fcListAdd(TC_, out.tags, FC_IVAL(nt)); fcListAdd(TC_, out.nodes, node);
        if (nt == FN_EXPR && ((FcExpr*)node)->kind == EK_LambdaExpr && !intoLambdas) continue;
        kt = fcListNew(TC_); kn = fcListNew(TC_);
        fcKids(t, nt, node, catches, kt, kn);
        for (k = kn->count - 1; k >= 0; k--) { fcListAdd(TC_, stackT, kt->items[k]); fcListAdd(TC_, stackN, kn->items[k]); }
    }
    return out;
}
static FcNodes AstNodes(FcTC* t, FcNodeTag tag, void* root) { return AllNodes(t, tag, root, false, true); }
#define NODE_IS_E(n, i, k) (FC_VALI((n).tags->items[i]) == FN_EXPR && ((FcExpr*)(n).nodes->items[i])->kind == (k))
#define NODE_IS_S(n, i, k) (FC_VALI((n).tags->items[i]) == FN_STMT && ((FcStmt*)(n).nodes->items[i])->kind == (k))

/* ---- TC031 ---- */
static void CheckStoredTemporaries(FcTC* t, FcConstructorDecl* ctor, FcList* argParams, FcList* args, const char* className) {
    FcPendingTemp* p = (FcPendingTemp*)fcAlloc(TC_, sizeof *p);
    p->source = t->source; p->params = ctor->params; p->body = ctor->body; p->baseArgs = ctor->baseArgs; p->argParams = argParams; p->args = args; p->className = className;
    fcListAdd(TC_, t->pendingTemporaries, p);
}
static bool IsSurelyNew(FcExpr* e) {
    return e->kind == EK_NewExpr || e->kind == EK_NewArrayExpr || e->kind == EK_NewBufferExpr || e->kind == EK_ArrayLiteralExpr || e->kind == EK_CallExpr || e->kind == EK_ConditionalExpr;
}
static void CheckStoredArguments(FcTC* t, FcMethodDecl* method, FcList* argParams, FcList* args) {
    bool any = false;
    int i;
    for (i = 0; i < args->count; i++) { FcExpr* a = (FcExpr*)args->items[i]; if ((a->kind == EK_UnaryExpr && ((FcUnaryExpr*)a)->op == UOP_Take) || IsSurelyNew(a)) any = true; }
    if (any) {
        FcPendingTemp* p = (FcPendingTemp*)fcAlloc(TC_, sizeof *p);
        p->source = t->source; p->params = method->params; p->body = method->body; p->argParams = argParams; p->args = args; p->method = method;
        fcListAdd(TC_, t->pendingTemporaries, p);
    }
}
static const char* MethodClass(FcTC* t, FcMethodDecl* m) {
    int k;
    if (t->methodClasses == NULL) {
        t->methodClasses = fcDictNew(TC_, FC_KEY_PTR);
        FC_DICT_EACH(t->classes, e) {
            FcClassDecl* decl = (FcClassDecl*)e->value;
            for (k = 0; k < decl->members->count; k++) if (((FcStmt*)decl->members->items[k])->kind == SK_MethodDecl) fcDictAdd(t->methodClasses, decl->members->items[k], (void*)e->key);
        }
    }
    return (const char*)fcDictGet(t->methodClasses, m);
}
static bool HoldsOwned(FcTC* t, const FcTypeRef* type, int arrayRanks) {
    if (type == NULL) return false;
    if (arrayRanks > 0 || type->arrayRank > 0 || (streq0(type->baseName, "any") && type->pointerDepth == 0)) return true;
    if (!(type->typeArgs && type->typeArgs->count > 0) && fcDictHas(t->classes, PlainName(t, type->baseName))) return true;
    if (type->pointerDepth == 0 && IsInterface(t, PlainName(t, type->baseName))) return true;
    return type->typeArgs && type->typeArgs->count > 0 && fcDictHas(t->classes, GenericName(t, PlainName(t, type->baseName), type->typeArgs->count));
}
static bool IsTypeParamOf(FcTC* t, const char* className, FcLambdaParam* p) {
    FcClassDecl* decl;
    return p->type && p->type->pointerDepth == 0 && p->type->arrayRank == 0 && p->type->typeArgs == NULL && p->arrayRanks->count == 0
        && (decl = (FcClassDecl*)fcDictGet(t->classes, className)) != NULL && fcHasTypeParamNamed(decl->typeParams, p->type->baseName);
}
static bool ReturnsFresh(FcTC* t, FcMethodDecl* m);
static bool IsStatementTemporary(FcTC* t, FcExpr* e) {
    FcMethodDecl* target;
    switch (e->kind) {
        case EK_NewExpr: return fcDictHas(t->classes, PlainName(t, ((FcNewExpr*)e)->classRef->baseName)) || (((FcNewExpr*)e)->classRef->typeArgs && ((FcNewExpr*)e)->classRef->typeArgs->count > 0);
        case EK_NewArrayExpr: case EK_NewBufferExpr: case EK_ArrayLiteralExpr: return true;
        case EK_CallExpr: return (target = (FcMethodDecl*)fcDictGet(&t->callTargets, e)) != NULL && ReturnsFresh(t, target);
        case EK_ConditionalExpr: return IsStatementTemporary(t, ((FcConditionalExpr*)e)->then) && IsStatementTemporary(t, ((FcConditionalExpr*)e)->otherwise);
        default: return false;
    }
}
typedef struct FcFreshCtx { FcTC* t; FcMethodDecl* m; FcDict* locals; } FcFreshCtx;
static bool fcFresh(FcFreshCtx* f, FcExpr* value) {
    int i;
    FcMethodDecl* target;
    if (!value) return false;
    switch (value->kind) {
        case EK_NewExpr: case EK_NewArrayExpr: case EK_NewBufferExpr: case EK_ArrayLiteralExpr: return true;
        case EK_IdentifierExpr: {
            const char* n = ((FcIdentifierExpr*)value)->name;
            if (!fcDictHas(f->locals, n)) return false;
            for (i = 0; i < f->m->params->count; i++) if (streq0(((FcLambdaParam*)f->m->params->items[i])->name, n)) return false;
            return true;
        }
        case EK_CallExpr: return (target = (FcMethodDecl*)fcDictGet(&f->t->callTargets, value)) != NULL && ReturnsFresh(f->t, target);
        case EK_ConditionalExpr: return fcFresh(f, ((FcConditionalExpr*)value)->then) && fcFresh(f, ((FcConditionalExpr*)value)->otherwise);
        default: return false;
    }
}
static bool ReturnsFresh(FcTC* t, FcMethodDecl* m) {
    void* known;
    FcNodes nodes;
    FcDict* reassigned;
    FcFreshCtx f;
    FcList* returns;
    bool fresh;
    int i;
    if (fcDictTry(&t->returnsFresh, m, &known)) return FC_VALI(known) == 2;
    nodes = AstNodes(t, FN_STMT, m->body);
    reassigned = fcDictNew(TC_, FC_KEY_STR);
    for (i = 0; i < nodes.nodes->count; i++)
        if (NODE_IS_E(nodes, i, EK_AssignExpr) && ((FcAssignExpr*)nodes.nodes->items[i])->target->kind == EK_IdentifierExpr)
            fcSetAdd(reassigned, ((FcIdentifierExpr*)((FcAssignExpr*)nodes.nodes->items[i])->target)->name);
    f.t = t; f.m = m; f.locals = fcDictNew(TC_, FC_KEY_STR);
    for (i = 0; i < nodes.nodes->count; i++)
        if (NODE_IS_S(nodes, i, SK_VarDeclStmt)) {
            FcVarDeclStmt* v = (FcVarDeclStmt*)nodes.nodes->items[i];
            if (v->initializer && (v->initializer->kind == EK_NewExpr || v->initializer->kind == EK_NewArrayExpr || v->initializer->kind == EK_NewBufferExpr || v->initializer->kind == EK_ArrayLiteralExpr)
                && !fcDictHas(reassigned, v->name)) fcSetAdd(f.locals, v->name);
        }
    returns = fcListNew(TC_);
    for (i = 0; i < nodes.nodes->count; i++) if (NODE_IS_S(nodes, i, SK_ReturnStmt)) fcListAdd(TC_, returns, nodes.nodes->items[i]);
    fcDictPut(&t->returnsFresh, m, FC_IVAL(1));
    fresh = returns->count > 0;
    for (i = 0; i < returns->count && fresh; i++) if (!fcFresh(&f, ((FcReturnStmt*)returns->items[i])->value)) fresh = false;
    fcDictPut(&t->returnsFresh, m, FC_IVAL(fresh ? 2 : 1));
    return fresh;
}

static bool IsInstanceField(FcTC* t, const char* className, const char* name) {
    int guard, k;
    FcClassDecl* decl;
    for (guard = 0; guard < 64 && (decl = (FcClassDecl*)fcDictGet(t->classes, className)) != NULL; guard++) {
        const char* next = NULL;
        for (k = 0; k < decl->members->count; k++) {
            FcStmt* m = (FcStmt*)decl->members->items[k];
            if ((m->kind == SK_FieldDecl && !((FcFieldDecl*)m)->isStatic && streq0(((FcFieldDecl*)m)->name, name)) || (m->kind == SK_PropertyDecl && !((FcPropertyDecl*)m)->isStatic && streq0(((FcPropertyDecl*)m)->name, name))) return true;
        }
        if (decl->baseRefs) for (k = 0; k < decl->baseRefs->count && !next; k++) { const char* n = PlainName(t, ((FcTypeRef*)decl->baseRefs->items[k])->baseName); if (fcDictHas(t->classes, n)) next = n; }
        if (!next) return false;
        className = next;
    }
    return false;
}
static const char* StaticFieldClass(FcTC* t, const char* className, const char* name) {
    int guard, k;
    FcClassDecl* decl;
    for (guard = 0; guard < 64 && (decl = (FcClassDecl*)fcDictGet(t->classes, className)) != NULL; guard++) {
        const char* next = NULL;
        for (k = 0; k < decl->members->count; k++) { FcStmt* m = (FcStmt*)decl->members->items[k]; if (m->kind == SK_FieldDecl && ((FcFieldDecl*)m)->isStatic && streq0(((FcFieldDecl*)m)->name, name)) return className; }
        if (decl->baseRefs) for (k = 0; k < decl->baseRefs->count && !next; k++) { const char* n = PlainName(t, ((FcTypeRef*)decl->baseRefs->items[k])->baseName); if (fcDictHas(t->classes, n)) next = n; }
        if (!next) return NULL;
        className = next;
    }
    return NULL;
}
typedef struct FcFieldRef { const char* name; const char* cls; const char* kind; const char* text; } FcFieldRef;
static bool FieldRef(FcTC* t, const char* className, FcExpr* e, FcDict* locals, FcFieldRef* out) {
    const char* owner;
    if (e->kind == EK_MemberExpr && ((FcMemberExpr*)e)->target->kind == EK_ThisExpr && IsInstanceField(t, className, ((FcMemberExpr*)e)->name)) {
        out->name = ((FcMemberExpr*)e)->name; out->cls = className; out->kind = "field"; out->text = fcFmt(TC_, "this.%s", out->name); return true;
    }
    if (e->kind == EK_IdentifierExpr && !fcDictHas(locals, ((FcIdentifierExpr*)e)->name)) {
        const char* f = ((FcIdentifierExpr*)e)->name;
        if (IsInstanceField(t, className, f)) { out->name = f; out->cls = className; out->kind = "field"; out->text = fcFmt(TC_, "this.%s", f); return true; }
        if ((owner = StaticFieldClass(t, className, f)) != NULL) { out->name = f; out->cls = owner; out->kind = "static field"; out->text = fcFmt(TC_, "%s.%s", PlainName(t, owner), f); return true; }
    }
    if (e->kind == EK_MemberExpr && ((FcMemberExpr*)e)->target->kind == EK_IdentifierExpr) {
        const char* c2 = ((FcIdentifierExpr*)((FcMemberExpr*)e)->target)->name;
        const char* f = ((FcMemberExpr*)e)->name;
        if (!fcDictHas(locals, c2) && fcDictHas(t->classes, c2) && (owner = StaticFieldClass(t, c2, f)) != NULL) {
            out->name = f; out->cls = owner; out->kind = "static field"; out->text = fcFmt(TC_, "%s.%s", PlainName(t, owner), f); return true;
        }
    }
    return false;
}
static bool fcOwning(FcTC* t, FcExpr* e) {
    FcNodes n;
    int i;
    if (!e) return false;
    n = AstNodes(t, FN_EXPR, e);
    for (i = 0; i < n.nodes->count; i++)
        if (NODE_IS_E(n, i, EK_MemberExpr)) {
            FcMemberExpr* m = (FcMemberExpr*)n.nodes->items[i];
            if (m->target->kind == EK_IdentifierExpr && streq0(((FcIdentifierExpr*)m->target)->name, "Owns") && !streq0(m->name, "Explicit")) return true;
        }
    return false;
}
static bool OwnsWhatComesIn(FcTC* t, const char* className, const char* field) {
    FcClassDecl* decl = (FcClassDecl*)fcDictGet(t->classes, className);
    int i, k;
    if (!decl) return false;
    for (i = 0; i < decl->members->count; i++) {
        FcStmt* member = (FcStmt*)decl->members->items[i];
        if (member->kind == SK_FieldDecl && streq0(((FcFieldDecl*)member)->name, field) && fcOwning(t, ((FcFieldDecl*)member)->initializer)) return true;
        if (member->kind == SK_MethodDecl || member->kind == SK_ConstructorDecl || member->kind == SK_PropertyDecl) {
            FcNodes n = AstNodes(t, FN_STMT, member);
            for (k = 0; k < n.nodes->count; k++)
                if (NODE_IS_E(n, k, EK_AssignExpr)) {
                    FcAssignExpr* a = (FcAssignExpr*)n.nodes->items[k];
                    bool named = (a->target->kind == EK_MemberExpr && ((FcMemberExpr*)a->target)->target->kind == EK_ThisExpr && streq0(((FcMemberExpr*)a->target)->name, field))
                        || (a->target->kind == EK_IdentifierExpr && streq0(((FcIdentifierExpr*)a->target)->name, field));
                    if (named && fcOwning(t, a->value)) return true;
                }
        }
    }
    return false;
}
static bool fcIsName(FcExpr* e, const char* name) { return e->kind == EK_IdentifierExpr && streq0(((FcIdentifierExpr*)e)->name, name); }
static bool fcIsReferenceContainer(const char* n) { return streq0(n, "List") || streq0(n, "Dictionary") || streq0(n, "HashSet") || streq0(n, "__HashTable"); }
static bool fcIsContainerStore(const char* n) { return streq0(n, "Add") || streq0(n, "Insert") || streq0(n, "Set") || streq0(n, "Push") || streq0(n, "Enqueue") || streq0(n, "TryAdd") || streq0(n, "AddRange"); }
static FcKeptAt* fcKept(FcTC* t, const char* where, const char* store, const char* holder) { FcKeptAt* k = (FcKeptAt*)fcAlloc(TC_, sizeof *k); k->where = where; k->store = store; k->holder = holder; return k; }
static const char* fcLowerAscii(FcTC* t, const char* s) { return fcAsciiLower(TC_, s); }

static FcKeptAt* PlainlyStoredField(FcTC* t, const char* className, FcList* parameters, FcBlockStmt* body, const char* name, int depth) {
    FcDict* locals = fcDictNew(TC_, FC_KEY_STR);
    FcNodes nodes = AstNodes(t, FN_STMT, body);
    FcDict* notOwned = fcDictNew(TC_, FC_KEY_PTR);
    FcKeptAt* field = NULL;
    int i, k, j;
    for (i = 0; i < parameters->count; i++) fcSetAdd(locals, ((FcLambdaParam*)parameters->items[i])->name);
    for (i = 0; i < nodes.nodes->count; i++) if (NODE_IS_S(nodes, i, SK_VarDeclStmt)) fcSetAdd(locals, ((FcVarDeclStmt*)nodes.nodes->items[i])->name);
    for (i = 0; i < nodes.nodes->count; i++)
        if (NODE_IS_S(nodes, i, SK_IfStmt)) {
            FcIfStmt* is = (FcIfStmt*)nodes.nodes->items[i];
            if (is->condition->kind == EK_IsOfExpr && ((FcIsOfExpr*)is->condition)->operand->kind == EK_IdentifierExpr
                && streq0(((FcIdentifierExpr*)((FcIsOfExpr*)is->condition)->operand)->name, name) && !HoldsOwned(t, ((FcIsOfExpr*)is->condition)->typeRef, 0)) {
                FcNodes inner = AstNodes(t, FN_STMT, is->then);
                for (k = 0; k < inner.nodes->count; k++) fcSetAdd(notOwned, inner.nodes->items[k]);
            }
        }
    for (i = 0; i < nodes.nodes->count; i++) {
        void* node = nodes.nodes->items[i];
        FcNodeTag tag = (FcNodeTag)FC_VALI(nodes.tags->items[i]);
        if (fcDictHas(notOwned, node)) continue;
        if (tag == FN_EXPR) {
            FcExpr* e = (FcExpr*)node;
            if (e->kind == EK_CallExpr && ((FcCallExpr*)e)->argNames == NULL && depth < 4) {
                FcCallExpr* call = (FcCallExpr*)e;
                int passed = -1;
                for (k = 0; k < call->args->count; k++) {
                    FcExpr* a = (FcExpr*)call->args->items[k];
                    if (a->kind == EK_UnaryExpr && ((FcUnaryExpr*)a)->op == UOP_Take && fcIsName(((FcUnaryExpr*)a)->operand, name)) { passed = k; break; }
                }
                if (passed >= 0) {
                    FcMethodDecl* next = (FcMethodDecl*)fcDictGet(&t->callTargets, call);
                    const char* nextClass;
                    FcKeptAt* onward;
                    if (next && passed < next->params->count && !((FcLambdaParam*)next->params->items[passed])->byRef && (nextClass = MethodClass(t, next)) != NULL
                        && (onward = PlainlyStoredField(t, nextClass, next->params, next->body, ((FcLambdaParam*)next->params->items[passed])->name, depth + 1)) != NULL && onward->where[0]) {
                        if (!field) field = onward;
                        continue;
                    }
                    return NULL;
                }
            }
            if (e->kind == EK_UnaryExpr && ((FcUnaryExpr*)e)->op == UOP_Take && fcIsName(((FcUnaryExpr*)e)->operand, name)) {
                bool asArg = false;
                if (depth < 4)
                    for (k = 0; k < nodes.nodes->count && !asArg; k++)
                        if (NODE_IS_E(nodes, k, EK_CallExpr) && ((FcCallExpr*)nodes.nodes->items[k])->argNames == NULL) {
                            FcCallExpr* cc = (FcCallExpr*)nodes.nodes->items[k];
                            for (j = 0; j < cc->args->count; j++) if (cc->args->items[j] == node) asArg = true;
                        }
                if (asArg) continue;
                return NULL;
            }
            if (e->kind == EK_UnaryExpr && ((FcUnaryExpr*)e)->op == UOP_TryTake && fcIsName(((FcUnaryExpr*)e)->operand, name)) return NULL;
            if (e->kind == EK_CallExpr && ((FcCallExpr*)e)->callee->kind == EK_MemberExpr && fcIsName(((FcMemberExpr*)((FcCallExpr*)e)->callee)->target, name)
                && strstr(((FcMemberExpr*)((FcCallExpr*)e)->callee)->name, "Take")) return NULL;
            if (e->kind == EK_ConditionalAccessExpr && fcIsName(((FcConditionalAccessExpr*)e)->target, name) && ((FcConditionalAccessExpr*)e)->member && strstr(((FcConditionalAccessExpr*)e)->member, "Take")) return NULL;
            if (e->kind == EK_CallExpr && ((FcCallExpr*)e)->callee->kind == EK_MemberExpr && strstr(((FcMemberExpr*)((FcCallExpr*)e)->callee)->name, "Take")) {
                FcCallExpr* call = (FcCallExpr*)e;
                for (k = 0; k < call->args->count; k++) if (fcIsName((FcExpr*)call->args->items[k], name)) return NULL;
            }
            if (e->kind == EK_AssignExpr && fcIsName(((FcAssignExpr*)e)->target, name)) return NULL;
            if (e->kind == EK_AssignExpr && fcIsName(((FcAssignExpr*)e)->value, name)) {
                FcAssignExpr* a = (FcAssignExpr*)e;
                FcFieldRef element, f;
                if (a->target->kind == EK_IndexExpr && FieldRef(t, className, ((FcIndexExpr*)a->target)->target, locals, &element) && !OwnsWhatComesIn(t, element.cls, element.name)) {
                    if (!field) field = fcKept(t, TF(t, "an element of the %s '%s'", element.kind, element.name), TF(t, "%s[...] = {0} %s", element.text, name), "the element");
                } else if (FieldRef(t, className, a->target, locals, &f)) {
                    if (!field) field = fcKept(t, TF(t, "the %s '%s'", f.kind, f.name), TF(t, "%s = {0} %s", f.text, name), TF(t, "the %s", f.kind));
                }
                continue;
            }
            if (e->kind == EK_CallExpr && ((FcCallExpr*)e)->callee->kind == EK_MemberExpr && fcIsContainerStore(((FcMemberExpr*)((FcCallExpr*)e)->callee)->name)) {
                FcCallExpr* call = (FcCallExpr*)e;
                FcMemberExpr* callee = (FcMemberExpr*)call->callee;
                bool hasName = false;
                FcMethodDecl* add;
                const char* container;
                FcFieldRef list;
                for (k = 0; k < call->args->count; k++) if (fcIsName((FcExpr*)call->args->items[k], name)) hasName = true;
                if (hasName && (add = (FcMethodDecl*)fcDictGet(&t->callTargets, call)) != NULL && (container = MethodClass(t, add)) != NULL && fcIsReferenceContainer(PlainName(t, container))
                    && FieldRef(t, className, callee->target, locals, &list) && !OwnsWhatComesIn(t, list.cls, list.name)) {
                    if (!field) {
                        FcBuf b = {0};
                        fcBufFmt(&b, "%s.%s(", list.text, callee->name);
                        for (k = 0; k < call->args->count; k++) { if (k) fcBufStr(&b, ", "); if (fcIsName((FcExpr*)call->args->items[k], name)) fcBufFmt(&b, "{0} %s", name); else fcBufStr(&b, "..."); }
                        fcBufChar(&b, ')');
                        field = fcKept(t, TF(t, "the %s in the %s '%s'", PlainName(t, container), list.kind, list.name), fcBufDone(TC_, &b), TF(t, "the %s", fcLowerAscii(t, PlainName(t, container))));
                    }
                }
                continue;
            }
        } else if (tag == FN_STMT) {
            FcStmt* s = (FcStmt*)node;
            if (s->kind == SK_VarDeclStmt && streq0(((FcVarDeclStmt*)s)->name, name)) return NULL;
            if (s->kind == SK_ReturnStmt && ((FcReturnStmt*)s)->value && fcIsName(((FcReturnStmt*)s)->value, name)) return NULL;
        }
    }
    return field ? field : fcKept(t, "", "", "");
}
static FcKeptAt* StoredField(FcTC* t, const char* className, FcList* parameters, FcBlockStmt* body, FcList* baseArgs, const char* name, bool isCtor, int depth) {
    FcKeptAt* field;
    FcClassDecl* decl;
    const char* baseName = NULL;
    FcList* baseCtors;
    int at = -1, k, i;
    if (body == NULL) return NULL;
    if (depth == 0) {
        FcDict* byName = (FcDict*)fcDictGet(&t->storedFields, body);
        void* known;
        FcKeptAt* r;
        if (!byName) { byName = fcDictNew(TC_, FC_KEY_STR); fcDictPut(&t->storedFields, body, byName); }
        if (fcDictTry(byName, name, &known)) return known == &fcNoKept ? NULL : (FcKeptAt*)known;
        r = StoredField(t, className, parameters, body, baseArgs, name, isCtor, 1);
        fcDictPut(byName, name, r ? (void*)r : (void*)&fcNoKept);
        return r;
    }
    if ((field = PlainlyStoredField(t, className, parameters, body, name, 0)) == NULL) return NULL;
    if (field->where[0]) return field;
    if (!isCtor || depth > 8 || baseArgs == NULL || !(decl = (FcClassDecl*)fcDictGet(t->classes, className))) return NULL;
    for (k = 0; k < baseArgs->count; k++) {
        FcExpr* a = (FcExpr*)baseArgs->items[k];
        if (fcIsName(a, name) || (a->kind == EK_UnaryExpr && ((FcUnaryExpr*)a)->op == UOP_Take && fcIsName(((FcUnaryExpr*)a)->operand, name))) { at = k; break; }
    }
    if (at < 0) return NULL;
    if (decl->baseRefs) for (k = 0; k < decl->baseRefs->count && !baseName; k++) { const char* n = PlainName(t, ((FcTypeRef*)decl->baseRefs->items[k])->baseName); if (fcDictHas(t->classes, n)) baseName = n; }
    if (baseName == NULL) return NULL;
    baseCtors = fcListNew(TC_);
    {
        FcClassDecl* bd = (FcClassDecl*)fcDictGet(t->classes, baseName);
        for (i = 0; i < bd->members->count; i++) {
            FcStmt* m = (FcStmt*)bd->members->items[i];
            FcConstructorDecl* c;
            bool ok = true;
            if (m->kind != SK_ConstructorDecl) continue;
            c = (FcConstructorDecl*)m;
            if (c->params->count < baseArgs->count) continue;
            for (k = 0; k < baseArgs->count; k++) if (((FcLambdaParam*)c->params->items[k])->byRef) ok = false;
            for (k = baseArgs->count; k < c->params->count; k++) if (((FcLambdaParam*)c->params->items[k])->defaultValue == NULL) ok = false;
            if (ok) fcListAdd(TC_, baseCtors, c);
        }
    }
    if (baseCtors->count != 1) return NULL;
    {
        FcConstructorDecl* bc = (FcConstructorDecl*)baseCtors->items[0];
        return StoredField(t, baseName, bc->params, bc->body, bc->baseArgs, ((FcLambdaParam*)bc->params->items[at])->name, true, depth + 1);
    }
}
static const char* TakenText(FcTC* t, FcExpr* e) {
    FcExpr* o;
    if (e->kind != EK_UnaryExpr) return "take ...";
    o = ((FcUnaryExpr*)e)->operand;
    if (o->kind == EK_IdentifierExpr) return TF(t, "take %s", ((FcIdentifierExpr*)o)->name);
    if (o->kind == EK_MemberExpr && ((FcMemberExpr*)o)->target->kind == EK_ThisExpr) return TF(t, "take this.%s", ((FcMemberExpr*)o)->name);
    if (o->kind == EK_MemberExpr && ((FcMemberExpr*)o)->target->kind == EK_IdentifierExpr) return TF(t, "take %s.%s", ((FcIdentifierExpr*)((FcMemberExpr*)o)->target)->name, ((FcMemberExpr*)o)->name);
    return "take ...";
}
static const char* KindOfParam(FcLambdaParam* p) {
    if (p->arrayRanks->count > 0 || (p->type && p->type->arrayRank > 0))
        return p->type && (streq0(p->type->baseName, "word") || streq0(p->type->baseName, "byte") || (streq0(p->type->baseName, "int") && p->type->bitWidth == 8)) ? "buffer" : "array";
    return p->type && streq0(p->type->baseName, "List") ? "list" : "object";
}
static const char* ShortText(FcTC* t, FcExpr* e) {
    switch (e->kind) {
        case EK_NewExpr: return TF(t, "new %s(...)", ((FcNewExpr*)e)->classRef->baseName);
        case EK_CallExpr: {
            FcExpr* callee = ((FcCallExpr*)e)->callee;
            if (callee->kind == EK_MemberExpr) {
                FcMemberExpr* m = (FcMemberExpr*)callee;
                return TF(t, "%s%s(...)", m->target->kind == EK_IdentifierExpr ? TF(t, "%s.", ((FcIdentifierExpr*)m->target)->name) : "", m->name);
            }
            if (callee->kind == EK_IdentifierExpr) return TF(t, "%s(...)", ((FcIdentifierExpr*)callee)->name);
            return "the argument";
        }
        case EK_NewArrayExpr: return TF(t, "new %s[...]", ((FcNewArrayExpr*)e)->elementType->baseName);
        case EK_NewBufferExpr: return ((FcNewBufferExpr*)e)->words ? "new word[...]" : "new byte[...]";
        case EK_ArrayLiteralExpr: return "[...]";
        case EK_ConditionalExpr: return "the result of '?:'";
        default: return "the argument";
    }
}
/* string.Format(store, word): {0} replaced */
static const char* fcFormat0(FcTC* t, const char* store, const char* word) {
    FcBuf b = {0};
    const char* p;
    fcBufStr(&b, "");
    for (p = store; *p; ) { if (strncmp(p, "{0}", 3) == 0) { fcBufStr(&b, word); p += 3; } else fcBufChar(&b, *p++); }
    return fcBufDone(TC_, &b);
}
static void ReportStoredTemporaries(FcTC* t) {
    int savedSource = t->source, q, i;
    for (q = 0; q < t->pendingTemporaries->count; q++) {
        FcPendingTemp* pt = (FcPendingTemp*)t->pendingTemporaries->items[q];
        const char* className;
        const char* plain;
        const char* who;
        t->source = pt->source;
        className = pt->className ? pt->className : pt->method ? MethodClass(t, pt->method) : NULL;
        if (!className) continue;
        plain = PlainName(t, className);
        who = pt->method == NULL ? TF(t, "the constructor of '%s'", plain) : TF(t, "'%s.%s'", plain, pt->method->name);
        for (i = 0; i < pt->args->count && i < pt->argParams->count; i++) {
            FcLambdaParam* p = (FcLambdaParam*)pt->argParams->items[i];
            FcExpr* a = (FcExpr*)pt->args->items[i];
            bool taken;
            FcKeptAt* kept;
            if (!p || p->byRef) continue;
            taken = a->kind == EK_UnaryExpr && ((FcUnaryExpr*)a)->op == UOP_Take;
            if (!HoldsOwned(t, p->type, p->arrayRanks->count) && !(taken && IsTypeParamOf(t, className, p))) continue;
            if (!taken && !IsStatementTemporary(t, a)) continue;
            if ((kept = StoredField(t, className, pt->params, pt->body, pt->baseArgs, p->name, pt->method == NULL, 0)) == NULL) continue;
            if (taken)
                Report(t, TC_StoredTemporaryCode, TF(t, "'%s' hands its value over to the call, but %s keeps only a reference to it in %s without taking it: what the call owns is destroyed when the call ends, and %s refers to a destroyed %s. Let %s take it ('%s')",
                    TakenText(t, a), who, kept->where, kept->holder, KindOfParam(p), pt->method == NULL ? "the constructor" : "the method", fcFormat0(t, kept->store, "take")), a->line);
            else
                Report(t, TC_StoredTemporaryCode, TF(t, "'%s' is a temporary of this statement: %s keeps it in %s without taking it, so it is destroyed at the end of the statement and %s refers to a destroyed object. Keep it in a variable that lives long enough, or let %s take it ('%s')",
                    ShortText(t, a), who, kept->where, kept->holder, pt->method == NULL ? "the constructor" : "the method", fcFormat0(t, kept->store, strstr(kept->store, "= {0}") ? "try take" : "take")), a->line);
        }
    }
    t->pendingTemporaries->count = 0;
    t->source = savedSource;
}

/* ---- TypeChecker.TakeLocal.cs (TC035) ---- */
typedef struct FcTLFrame { FcList* statements; int index; struct FcTLFrame* outer; FcList* declared; } FcTLFrame;
static void TakeLocalWalk(FcTC* t, FcList* statements, FcTLFrame* outer, FcList* declared);
static void TakeLocalBody(FcTC* t, FcList* statements) { TakeLocalWalk(t, statements, NULL, fcListNew(TC_)); }
static void TakeLocalLambdas(FcTC* t, FcExpr* e) {
    FcNodes n = AllNodes(t, FN_EXPR, e, true, false);
    int i;
    for (i = 0; i < n.nodes->count; i++) if (NODE_IS_E(n, i, EK_LambdaExpr)) TakeLocalBody(t, ((FcLambdaExpr*)n.nodes->items[i])->body->statements);
}
static void TakeLocalDecl(FcTC* t, FcStmt* decl) {
    int i;
    switch (decl->kind) {
        case SK_ClassDecl: for (i = 0; i < ((FcClassDecl*)decl)->members->count; i++) TakeLocalDecl(t, (FcStmt*)((FcClassDecl*)decl)->members->items[i]); break;
        case SK_NamespaceDecl: for (i = 0; i < ((FcNamespaceDecl*)decl)->members->count; i++) TakeLocalDecl(t, (FcStmt*)((FcNamespaceDecl*)decl)->members->items[i]); break;
        case SK_ClassExtensionDecl: for (i = 0; i < ((FcClassExtensionDecl*)decl)->members->count; i++) TakeLocalDecl(t, (FcStmt*)((FcClassExtensionDecl*)decl)->members->items[i]); break;
        case SK_MethodDecl: TakeLocalBody(t, ((FcMethodDecl*)decl)->body->statements); break;
        case SK_ConstructorDecl: TakeLocalBody(t, ((FcConstructorDecl*)decl)->body->statements); break;
        case SK_DestructorDecl: TakeLocalBody(t, ((FcDestructorDecl*)decl)->body->statements); break;
        case SK_PropertyDecl:
            if (((FcPropertyDecl*)decl)->getter) TakeLocalBody(t, ((FcPropertyDecl*)decl)->getter->statements);
            if (((FcPropertyDecl*)decl)->setter) TakeLocalBody(t, ((FcPropertyDecl*)decl)->setter->statements);
            break;
        case SK_FieldDecl: if (((FcFieldDecl*)decl)->initializer) TakeLocalLambdas(t, ((FcFieldDecl*)decl)->initializer); break;
        default: break;
    }
}
static const char* TakeLocalTarget(FcStmt* stmt) {
    FcExpr* call;
    if (stmt->kind != SK_ExprStmt) return NULL;
    call = ((FcExprStmt*)stmt)->expression;
    if (call->kind == EK_TryCallExpr) call = ((FcTryCallExpr*)call)->call;
    if (call->kind == EK_CallExpr && ((FcCallExpr*)call)->callee->kind == EK_MemberExpr && streq0(((FcMemberExpr*)((FcCallExpr*)call)->callee)->name, "TakeLocal")
        && ((FcMemberExpr*)((FcCallExpr*)call)->callee)->target->kind == EK_IdentifierExpr) return ((FcIdentifierExpr*)((FcMemberExpr*)((FcCallExpr*)call)->callee)->target)->name;
    return NULL;
}
static bool Moves(FcNodeTag tag, void* node, const char* name) {
    FcExpr* e;
    if (tag != FN_EXPR) return false;
    e = (FcExpr*)node;
    if (e->kind == EK_UnaryExpr && (((FcUnaryExpr*)e)->op == UOP_Take || ((FcUnaryExpr*)e)->op == UOP_TryTake) && ((FcUnaryExpr*)e)->operand->kind == EK_IdentifierExpr)
        return streq0(((FcIdentifierExpr*)((FcUnaryExpr*)e)->operand)->name, name);
    if (e->kind == EK_CallExpr && ((FcCallExpr*)e)->callee->kind == EK_MemberExpr) {
        FcMemberExpr* m = (FcMemberExpr*)((FcCallExpr*)e)->callee;
        if ((streq0(m->name, "TakeTo") || streq0(m->name, "TakeUpwards") || streq0(m->name, "TakeGlobal")) && m->target->kind == EK_IdentifierExpr) return streq0(((FcIdentifierExpr*)m->target)->name, name);
    }
    if (e->kind == EK_AssignExpr && ((FcAssignExpr*)e)->target->kind == EK_IdentifierExpr) return streq0(((FcIdentifierExpr*)((FcAssignExpr*)e)->target)->name, name);
    return false;
}
static const char* fcTLRoot(FcExpr* e) {
    if (e->kind == EK_IdentifierExpr) return ((FcIdentifierExpr*)e)->name;
    if (e->kind == EK_MemberExpr) return fcTLRoot(((FcMemberExpr*)e)->target);
    if (e->kind == EK_IndexExpr) return fcTLRoot(((FcIndexExpr*)e)->target);
    return NULL;
}
static bool fcIsKeeping(const char* n) { return streq0(n, "Add") || streq0(n, "Insert") || streq0(n, "Set") || streq0(n, "Push") || streq0(n, "Enqueue"); }
static const char* StoredOutside(FcTC* t, FcNodeTag tag, void* node, const char* name, FcDict* inner) {
    FcExpr* e;
    if (tag != FN_EXPR) return NULL;
    e = (FcExpr*)node;
    if (e->kind == EK_AssignExpr && fcIsName(((FcAssignExpr*)e)->value, name)) {
        FcExpr* target = ((FcAssignExpr*)e)->target;
        const char* r;
        if (target->kind == EK_IdentifierExpr) {
            const char* tn = ((FcIdentifierExpr*)target)->name;
            return !fcDictHas(inner, tn) && !streq0(tn, name) ? TF(t, "the variable '%s' from outside the block", tn) : NULL;
        }
        if (target->kind == EK_MemberExpr) return (r = fcTLRoot(target)) == NULL || !fcDictHas(inner, r) ? TF(t, "the field '%s'", ((FcMemberExpr*)target)->name) : NULL;
        if (target->kind == EK_IndexExpr) return (r = fcTLRoot(target)) == NULL || !fcDictHas(inner, r) ? "an element of an array from outside the block" : NULL;
        return NULL;
    }
    if (e->kind == EK_CallExpr && ((FcCallExpr*)e)->callee->kind == EK_MemberExpr && fcIsKeeping(((FcMemberExpr*)((FcCallExpr*)e)->callee)->name)) {
        FcCallExpr* call = (FcCallExpr*)e;
        FcMemberExpr* m = (FcMemberExpr*)call->callee;
        bool hasX = false;
        const char* r;
        int k;
        for (k = 0; k < call->args->count; k++) if (fcIsName((FcExpr*)call->args->items[k], name)) hasX = true;
        if (hasX && ((r = fcTLRoot(m->target)) == NULL || !fcDictHas(inner, r)))
            return TF(t, "'%s.%s(...)'", m->target->kind == EK_IdentifierExpr ? ((FcIdentifierExpr*)m->target)->name : m->target->kind == EK_MemberExpr ? ((FcMemberExpr*)m->target)->name : "\xE2\x80\xA6", m->name);
    }
    return NULL;
}
static void CheckBlockTakeLocal(FcTC* t, const char* name, FcTLFrame* frame, int line) {
    FcDict* inner = fcDictNew(TC_, FC_KEY_STR);
    bool local;
    FcTLFrame* f;
    int k, q;
    for (k = 0; k < frame->declared->count; k++) fcSetAdd(inner, frame->declared->items[k]);
    for (k = 0; k < frame->index; k++) if (((FcStmt*)frame->statements->items[k])->kind == SK_VarDeclStmt) fcSetAdd(inner, ((FcVarDeclStmt*)frame->statements->items[k])->name);
    local = fcDictHas(inner, name);
    for (k = frame->index + 1; k < frame->statements->count; k++) {
        FcStmt* stmt = (FcStmt*)frame->statements->items[k];
        FcNodes nodes;
        if (stmt->kind == SK_VarDeclStmt) fcSetAdd(inner, ((FcVarDeclStmt*)stmt)->name);
        nodes = AllNodes(t, FN_STMT, stmt, true, true);
        for (q = 0; q < nodes.nodes->count; q++) {
            FcNodeTag tag = (FcNodeTag)FC_VALI(nodes.tags->items[q]);
            void* node = nodes.nodes->items[q];
            const char* where;
            if (tag == FN_STMT && ((FcStmt*)node)->kind == SK_VarDeclStmt) fcSetAdd(inner, ((FcVarDeclStmt*)node)->name);
            if (Moves(tag, node, name)) return;
            if ((where = StoredOutside(t, tag, node, name, inner)) != NULL) {
                Report(t, TC_BlockTakeLocalCode, TF(t, "'%s.TakeLocal()' gives '%s' to this inner block, and %s keeps it: it is destroyed when the block ends, and what keeps it refers to a destroyed object. Call TakeLocal where it is to live (at the top level of the function), give it with 'take %s' (or '%s.TakeTo(...)') to what keeps it, or leave TakeLocal out",
                    name, name, where, name, name), line);
                return;
            }
        }
    }
    if (local) return;
    for (f = frame->outer; f != NULL; f = f->outer) {
        bool declaredThere = fcListHasStr(f->declared, name);
        for (k = f->index + 1; k < f->statements->count; k++) {
            FcStmt* stmt = (FcStmt*)f->statements->items[k];
            FcNodes nodes;
            if (stmt->kind == SK_VarDeclStmt && streq0(((FcVarDeclStmt*)stmt)->name, name)) return;
            nodes = AllNodes(t, FN_STMT, stmt, true, true);
            for (q = 0; q < nodes.nodes->count; q++) {
                FcNodeTag tag = (FcNodeTag)FC_VALI(nodes.tags->items[q]);
                void* node = nodes.nodes->items[q];
                if (tag == FN_EXPR && ((FcExpr*)node)->kind == EK_AssignExpr && fcIsName(((FcAssignExpr*)node)->target, name)) return;
                if (tag == FN_EXPR && fcIsName((FcExpr*)node, name)) {
                    Report(t, TC_BlockTakeLocalCode, TF(t, "'%s.TakeLocal()' gives '%s' to the inner block it is called in: it is destroyed when the block ends, and '%s' is used after it (line %d) - %s a destroyed object. Call TakeLocal at the top level of the function (or where '%s' is to live), or leave it out",
                        name, name, name, ((FcExpr*)node)->line, stmt->kind == SK_ReturnStmt ? "the caller gets" : "that is", name), line);
                    return;
                }
            }
        }
        if (!declaredThere)
            for (k = 0; k < f->index; k++) if (((FcStmt*)f->statements->items[k])->kind == SK_VarDeclStmt && streq0(((FcVarDeclStmt*)f->statements->items[k])->name, name)) declaredThere = true;
        if (declaredThere) return;
    }
}
static void fcDirectExprs(FcTC* t, FcStmt* stmt, FcList* out) {
    int i;
    switch (stmt->kind) {
        case SK_ExprStmt: fcListAdd(TC_, out, ((FcExprStmt*)stmt)->expression); break;
        case SK_VarDeclStmt: {
            FcVarDeclStmt* v = (FcVarDeclStmt*)stmt;
            if (v->initializer) fcListAdd(TC_, out, v->initializer);
            for (i = 0; i < v->arrayRanks->count; i++) if (v->arrayRanks->items[i]) fcListAdd(TC_, out, v->arrayRanks->items[i]);
            break;
        }
        case SK_IfStmt: fcListAdd(TC_, out, ((FcIfStmt*)stmt)->condition); break;
        case SK_WhileStmt: fcListAdd(TC_, out, ((FcWhileStmt*)stmt)->condition); break;
        case SK_DoWhileStmt: fcListAdd(TC_, out, ((FcDoWhileStmt*)stmt)->condition); break;
        case SK_ForStmt: if (((FcForStmt*)stmt)->condition) fcListAdd(TC_, out, ((FcForStmt*)stmt)->condition); if (((FcForStmt*)stmt)->increment) fcListAdd(TC_, out, ((FcForStmt*)stmt)->increment); break;
        case SK_ForeachStmt: fcListAdd(TC_, out, ((FcForeachStmt*)stmt)->iterable); break;
        case SK_ReturnStmt: if (((FcReturnStmt*)stmt)->value) fcListAdd(TC_, out, ((FcReturnStmt*)stmt)->value); break;
        case SK_ThrowStmt: fcListAdd(TC_, out, ((FcThrowStmt*)stmt)->value); break;
        case SK_DeleteStmt: fcListAdd(TC_, out, ((FcDeleteStmt*)stmt)->target); break;
        default: break;
    }
}
static FcList* fcOne(FcTC* t, void* item) { FcList* l = fcListNew(TC_); fcListAdd(TC_, l, item); return l; }
static void TakeLocalWalk(FcTC* t, FcList* statements, FcTLFrame* outer, FcList* declared) {
    int i, k;
    for (i = 0; i < statements->count; i++) {
        FcStmt* stmt = (FcStmt*)statements->items[i];
        FcTLFrame* here = (FcTLFrame*)fcAlloc(TC_, sizeof *here);
        const char* name;
        FcList* direct = fcListNew(TC_);
        here->statements = statements; here->index = i; here->outer = outer; here->declared = declared;
        if (outer != NULL && (name = TakeLocalTarget(stmt)) != NULL) CheckBlockTakeLocal(t, name, here, stmt->line);
        fcDirectExprs(t, stmt, direct);
        for (k = 0; k < direct->count; k++) TakeLocalLambdas(t, (FcExpr*)direct->items[k]);
        switch (stmt->kind) {
            case SK_BlockStmt: TakeLocalWalk(t, ((FcBlockStmt*)stmt)->statements, here, fcListNew(TC_)); break;
            case SK_IfStmt:
                TakeLocalWalk(t, fcOne(t, ((FcIfStmt*)stmt)->then), here, fcListNew(TC_));
                if (((FcIfStmt*)stmt)->otherwise) TakeLocalWalk(t, fcOne(t, ((FcIfStmt*)stmt)->otherwise), here, fcListNew(TC_));
                break;
            case SK_WhileStmt: TakeLocalWalk(t, fcOne(t, ((FcWhileStmt*)stmt)->body), here, fcListNew(TC_)); break;
            case SK_DoWhileStmt: TakeLocalWalk(t, fcOne(t, ((FcDoWhileStmt*)stmt)->body), here, fcListNew(TC_)); break;
            case SK_ForStmt: {
                FcForStmt* f = (FcForStmt*)stmt;
                FcList* d = fcListNew(TC_);
                if (f->init) { FcList* ie = fcListNew(TC_); fcDirectExprs(t, f->init, ie); for (k = 0; k < ie->count; k++) TakeLocalLambdas(t, (FcExpr*)ie->items[k]); }
                if (f->init && f->init->kind == SK_VarDeclStmt) fcListAdd(TC_, d, (void*)((FcVarDeclStmt*)f->init)->name);
                TakeLocalWalk(t, fcOne(t, f->body), here, d);
                break;
            }
            case SK_ForeachStmt: TakeLocalWalk(t, fcOne(t, ((FcForeachStmt*)stmt)->body), here, fcOne(t, (void*)((FcForeachStmt*)stmt)->varName)); break;
            case SK_TryStmt: {
                FcTryStmt* ts = (FcTryStmt*)stmt;
                TakeLocalWalk(t, fcOne(t, ts->tryBlock), here, fcListNew(TC_));
                for (k = 0; k < ts->catches->count; k++) TakeLocalWalk(t, fcOne(t, ((FcCatchClause*)ts->catches->items[k])->body), here, fcOne(t, (void*)((FcCatchClause*)ts->catches->items[k])->varName));
                if (ts->finallyBlock) TakeLocalWalk(t, fcOne(t, ts->finallyBlock), here, fcListNew(TC_));
                break;
            }
            case SK_UnsafeStmt: TakeLocalWalk(t, fcOne(t, ((FcUnsafeStmt*)stmt)->body), here, fcListNew(TC_)); break;
            case SK_FireStmt: TakeLocalBody(t, ((FcFireStmt*)stmt)->body->statements); break;
            default:
                if (stmt->kind == SK_ClassDecl || stmt->kind == SK_MethodDecl || stmt->kind == SK_ConstructorDecl || stmt->kind == SK_PropertyDecl) TakeLocalDecl(t, stmt);
                break;
        }
    }
}
static void CheckBlockTakeLocals(FcTC* t, FcList* program) {
    int saved = t->source, i;
    FcDict* topLevel = fcDictNew(TC_, FC_KEY_STR);
    for (i = 0; i < program->count; i++) {
        FcStmt* stmt = (FcStmt*)program->items[i];
        if (stmt->source < t->reportFrom) continue;
        t->source = stmt->source;
        if (stmt->kind == SK_ClassDecl || stmt->kind == SK_NamespaceDecl || stmt->kind == SK_ClassExtensionDecl || stmt->kind == SK_MethodDecl || stmt->kind == SK_ConstructorDecl
            || stmt->kind == SK_DestructorDecl || stmt->kind == SK_PropertyDecl || stmt->kind == SK_InterfaceDecl || stmt->kind == SK_EnumDecl)
            TakeLocalDecl(t, stmt);
        else {
            const char* key = fcFmt(TC_, "%d", stmt->source);
            FcList* list = (FcList*)fcDictGet(topLevel, key);
            if (!list) { list = fcListNew(TC_); fcDictPut(topLevel, key, list); }
            fcListAdd(TC_, list, stmt);
        }
    }
    FC_DICT_EACH(topLevel, e) {
        t->source = atoi((const char*)e->key);
        TakeLocalBody(t, (FcList*)e->value);
    }
    t->source = saved;
}

#include "setterclaims.c"

/* ---- the entry ---- */
static void CollectInterfaces(FcTC* t, FcList* statements) {
    int i;
    for (i = 0; i < statements->count; i++) {
        FcStmt* stmt = (FcStmt*)statements->items[i];
        if (stmt->kind == SK_InterfaceDecl) fcDictPut(&t->interfaces, ((FcInterfaceDecl*)stmt)->name, stmt);
        else if (stmt->kind == SK_EnumDecl) fcDictPut(&t->enumNames, ((FcEnumDecl*)stmt)->name, FC_IVAL(((FcEnumDecl*)stmt)->isFlags ? 1 : 0));
        else if (stmt->kind == SK_NamespaceDecl) CollectInterfaces(t, ((FcNamespaceDecl*)stmt)->members);
    }
}

/* TypeChecker.Check: the conflicts found */
static FcList* fcTypeCheck(FcCtx* c, FcList* program, FcResolveResult* resolved, bool strict, int firstUserSource, int reportFrom, int intWidth, int floatWidth, FcDict* nativeReturns) {
    FcTC* t = (FcTC*)fcAlloc(c, sizeof *t);
    int i;
    t->c = c;
    t->classes = resolved->classes;
    fcDictInit(c, &t->interfaces, FC_KEY_STR);
    fcDictInit(c, &t->enumNames, FC_KEY_STR);
    fcDictInit(c, &t->enumDecls, FC_KEY_STR);
    fcDictInit(c, &t->strictBySource, FC_KEY_STR);
    fcDictInit(c, &t->interfaceClosures, FC_KEY_STR);
    fcDictInit(c, &t->derivedClasses, FC_KEY_STR);
    fcDictInit(c, &t->pendingVars, FC_KEY_STR);
    fcDictInit(c, &t->mixedJoins, FC_KEY_PTR);
    fcDictInit(c, &t->placeholderTypes, FC_KEY_PTR);
    fcDictInit(c, &t->reportedLambdas, FC_KEY_PTR);
    fcDictInit(c, &t->notedCalls, FC_KEY_PTR);
    fcDictInit(c, &t->callTargets, FC_KEY_PTR);
    fcDictInit(c, &t->storedFields, FC_KEY_PTR);
    fcDictInit(c, &t->returnsFresh, FC_KEY_PTR);
    t->pendingTemporaries = fcListNew(c);
    t->diagnostics = fcListNew(c);
    t->scopes = fcListNew(c);
    t->defaultStrict = strict;
    t->firstUserSource = firstUserSource;
    t->reportFrom = reportFrom < 0 ? firstUserSource : reportFrom;
    t->programIntWidth = intWidth;
    t->programFloatWidth = floatWidth;
    t->programWordWidth = 64;
    t->nativeReturns = nativeReturns;
    t->returnWhat = "";
    t->rr = resolved;
    t->localTypes = fcListNew(c);
    t->deriveDefined = fcDeriveDefined();
    fcDictInit(c, &t->calls, FC_KEY_STR);
    fcDictInit(c, &t->stringCalls, FC_KEY_STR);
    fcDictInit(c, &t->returns, FC_KEY_PTR);
    fcDictInit(c, &t->paramIds, FC_KEY_PTR);
    fcDictInit(c, &t->unseenNames, FC_KEY_STR);
    fcDictInit(c, &t->unseenCtors, FC_KEY_STR);
    fcDictInit(c, &t->stringLiterals, FC_KEY_STR);
    fcDictInit(c, &t->baseClasses, FC_KEY_STR);
    fcDictInit(c, &t->exprTypes, FC_KEY_PTR);
    fcDictInit(c, &t->fieldOwner, FC_KEY_PTR);
    fcDictInit(c, &t->otherFieldNames, FC_KEY_STR);
    fcDictInit(c, &t->fieldBad, FC_KEY_PTR);
    fcDictInit(c, &t->fieldNeeds, FC_KEY_PTR);
    if (!resolved->unitChecks) resolved->unitChecks = fcDictNew(c, FC_KEY_PTR);
    if (!resolved->methodTypeArgs) resolved->methodTypeArgs = fcDictNew(c, FC_KEY_PTR);
    if (!resolved->localGuards) resolved->localGuards = fcDictNew(c, FC_KEY_PTR);
    if (!resolved->overloadHints) resolved->overloadHints = fcDictNew(c, FC_KEY_PTR);
    if (!resolved->receiverTypeArgs) resolved->receiverTypeArgs = fcDictNew(c, FC_KEY_PTR);
    if (!resolved->treeLambdas) resolved->treeLambdas = fcDictNew(c, FC_KEY_PTR);
    if (!resolved->treeTypes) resolved->treeTypes = fcDictNew(c, FC_KEY_PTR);
    if (!resolved->treeToCompiled) resolved->treeToCompiled = fcDictNew(c, FC_KEY_PTR);
    if (!resolved->provenMembers) resolved->provenMembers = fcDictNew(c, FC_KEY_PTR);
    if (!resolved->bitsOf) resolved->bitsOf = fcDictNew(c, FC_KEY_PTR);
    resolved->fixedCoercions = fcDictNew(c, FC_KEY_PTR);   /* (a check before this one decided for itself) */
    for (i = 0; i < program->count; i++) {
        FcStmt* s = (FcStmt*)program->items[i];
        if (s->kind == SK_TypeCheckDirective) fcDictPut(&t->strictBySource, fcFmt(c, "%d", s->source), FC_IVAL(((FcTypeCheckDirective*)s)->strict ? 1 : 2));
        if (s->kind == SK_EnumDecl) fcDictAdd(&t->enumDecls, ((FcEnumDecl*)s)->name, s);
    }
    CollectInterfaces(t, program);
    t->refs = resolved->references;
    Push(t);
    t->program = program;
    for (i = 0; i < program->count; i++) {
        FcStmt* stmt = (FcStmt*)program->items[i];
        t->source = stmt->source;
        Check(t, stmt);
    }
    ReportPendingVars(t, (FcDict*)t->scopes->items[0]);
    ReportStoredTemporaries(t);
    CheckBlockTakeLocals(t, program);
    if (t->deriveDefined) Derive(t, resolved);
    return t->diagnostics;
}

/* TypeChecker.Run: the warnings to the collector, the errors as a TypeCheckException */
static void fcTypeCheckRun(FcCtx* c, FcList* program, FcResolveResult* resolved, bool strict, int firstUserSource, int reportFrom, int intWidth, int floatWidth, FcDict* nativeReturns) {
    FcList* found = fcTypeCheck(c, program, resolved, strict, firstUserSource, reportFrom, intWidth, floatWidth, nativeReturns);
    FcList* errors = fcListNew(c);
    int i;
    for (i = 0; i < found->count; i++) {
        FcDiagnostic* d = (FcDiagnostic*)found->items[i];
        if (d->severity == FC_SEV_WARNING) { if (c->collecting) fcCollect(c, d); }
        else fcListAdd(c, errors, d);
    }
    if (errors->count > 0) {
        FcDiagnostic* first = (FcDiagnostic*)errors->items[0];
        c->err.kind = FC_ERR_TYPECHECK;
        c->err.message = first->message;
        c->err.line = first->line;
        c->err.column = 0;
        c->err.file = NULL;
        c->err.source = -1;
        c->err.list = errors;
        fcRethrow(c);
    }
}

/* ---- deriving `defined` for the parameters, returns and fields of the program (TypeChecker.cs: NoteCall, NoteReturn, DeriveReturns,
 * DerivationCandidates, FieldCandidates, SetBeforeRead, BaseConstructionSafe, FieldsOf, FieldNeed, NoteFieldWrite, DisqualifyFields, Derive) ---- */

typedef struct FcCallInfo { bool* defined; int count; int calls; FcList** needs; } FcCallInfo;
typedef struct FcStrCalls { bool* strings; int count; } FcStrCalls;
typedef struct FcRetNote { bool ok; const char* needs; } FcRetNote;
struct FcReturns { FcList* notes; bool ends; };
static FcReturns* fcReturnsNew(FcTC* t, FcList* notes, bool ends) { FcReturns* r = (FcReturns*)fcAlloc(TC_, sizeof *r); r->notes = notes; r->ends = ends; return r; }

static bool fcDeriveDefined(void) { const char* v = getenv("FIRE_NO_DERIVE"); return !(v && strcmp(v, "1") == 0); }
static bool fcInferUninitializedDefined(void) { const char* v = getenv("FIRE_INFER_UNINITIALIZED_DEFINED"); return !(v && strcmp(v, "0") == 0); }
static const char* MethodKey(FcTC* t, const char* name, int count) { return TF(t, "%s/%d", name, count); }
static const char* CtorKey(FcTC* t, const char* cls, int count) { return TF(t, "new:%s/%d", cls, count); }
static bool fcCalledByTheLanguage(const char* n) {
    static const char* const names[] = { "GetIndex", "SetIndex", "CompareTo", "Equals", "GetHashCode", "ToString", "GetEnumerator", "MoveNext", "GetCurrent", "Dispose", "Close", "TakeTo", "Main" };
    size_t i;
    for (i = 0; i < sizeof names / sizeof names[0]; i++) if (streq0(n, names[i])) return true;
    return false;
}
static const char* ParamId(FcTC* t, FcLambdaParam* p) {
    void* id;
    if (fcDictTry(&t->paramIds, p, &id)) return (const char*)id;
    id = (void*)TF(t, "par:%d", fcDictCount(&t->paramIds));
    fcDictPut(&t->paramIds, p, id);
    return (const char*)id;
}
static bool fcReflectionUsed(FcTC* t) {
    if (!t->reflectionKnown) {
        FcClassDecl* r = (FcClassDecl*)fcDictGet(t->classes, "Reflect");
        t->reflectionUsed = r && r->h.source < t->firstUserSource;
        t->reflectionKnown = true;
    }
    return t->reflectionUsed;
}
static bool fcInferDefinedClass(FcClassDecl* decl) {
    int i;
    if (!decl->attributes) return false;
    for (i = 0; i < decl->attributes->count; i++) if (streq0(((FcAttributeDecl*)decl->attributes->items[i])->name, "InferDefined")) return true;
    return false;
}

/* the nodes below an expression or a statement (AstNames.Walk / AstNodes) */
static void fcExprNodes(FcCtx* c, FcExpr* e, FcList** tags, FcList** nodes) {
    FcAllNodes k;
    k.c = c; k.tags = fcListNew(c); k.nodes = fcListNew(c);
    fcWalkExpr(e, fcAllVisit, &k);
    *tags = k.tags; *nodes = k.nodes;
}
static bool fcMentionsName(FcList* tags, FcList* nodes, const char* name) {
    int i;
    for (i = 0; i < nodes->count; i++) {
        FcExpr* e;
        if (FC_VALI(tags->items[i]) != FN_EXPR) continue;
        e = (FcExpr*)nodes->items[i];
        if (e->kind == EK_IdentifierExpr && streq0(((FcIdentifierExpr*)e)->name, name)) return true;
        if (e->kind == EK_MemberExpr && streq0(((FcMemberExpr*)e)->name, name)) return true;
    }
    return false;
}
static bool fcHasCallLike(FcList* tags, FcList* nodes, bool awaitToo) {
    int i;
    for (i = 0; i < nodes->count; i++) {
        FcExprKind k;
        if (FC_VALI(tags->items[i]) != FN_EXPR) continue;
        k = ((FcExpr*)nodes->items[i])->kind;
        if (k == EK_CallExpr || k == EK_NewExpr || k == EK_ThisExpr || k == EK_BaseExpr || k == EK_LambdaExpr || (awaitToo && k == EK_AwaitOnExpr)) return true;
    }
    return false;
}

/* NeedsOf: what a value that is not certainly a number depends on */
static const char* FieldNeed(FcTC* t, FcExpr* access, const char* name);
static bool IsFieldRef(FcTC* t, FcIdentifierExpr* id);
static const char* NeedsOf(FcTC* t, FcExpr* e) {
    void* v;
    if (e->kind == EK_CallExpr && fcDictTry(&t->callTargets, e, &v) && ((FcMethodDecl*)v)->h.source >= t->firstUserSource)
        return TF(t, "ret:%s", MethodKey(t, ((FcMethodDecl*)v)->name, ((FcMethodDecl*)v)->params->count));
    if (e->kind == EK_IdentifierExpr && t->plainParams && fcDictTry(t->plainParams, ((FcIdentifierExpr*)e)->name, &v)) return ParamId(t, (FcLambdaParam*)v);
    if (e->kind == EK_MemberExpr && t->deriveDefined) { const char* key = FieldNeed(t, e, ((FcMemberExpr*)e)->name); if (key) return key; }
    if (e->kind == EK_IdentifierExpr && t->deriveDefined && IsFieldRef(t, (FcIdentifierExpr*)e)) { const char* key = FieldNeed(t, e, ((FcIdentifierExpr*)e)->name); if (key) return key; }
    return NULL;
}
/* PlainParams: the parameters the body never assigns */
static FcDict* PlainParams(FcTC* t, FcList* parameters, FcBlockStmt* body) {
    FcDict* assigned;
    FcDict* plain;
    int i;
    if (!t->deriveDefined || parameters->count == 0) return NULL;
    assigned = fcAstNamesAssigned(TC_, body);
    plain = fcDictNew(TC_, FC_KEY_STR);
    for (i = 0; i < parameters->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parameters->items[i];
        if (!p->byRef && !fcDictHas(assigned, p->name)) fcDictPut(plain, p->name, p);
    }
    return plain;
}

/* NoteCall: a call resolved to one method or constructor - which parameters got a number (argParams may hold NULL) */
static void NoteCallFull(FcTC* t, FcExpr* call, const char* key, FcList* parameters, FcList* argParams, FcList* args, FcList* argExprs, FcMethodDecl* target) {
    FcCallInfo* entry;
    FcStrCalls* strings;
    int i, j, n = parameters->count;
    fcSetAdd(&t->notedCalls, call);
    if (target != NULL) fcDictPut(&t->callTargets, call, target);
    entry = (FcCallInfo*)fcDictGet(&t->calls, key);
    if (!entry || entry->count != n) {
        FcCallInfo* fresh = (FcCallInfo*)fcAlloc(TC_, sizeof *fresh);
        fresh->count = n;
        fresh->calls = entry ? entry->calls : 0;
        fresh->defined = (bool*)fcAlloc(TC_, sizeof(bool) * (size_t)(n + 1));
        fresh->needs = (FcList**)fcAlloc(TC_, sizeof(FcList*) * (size_t)(n + 1));
        for (j = 0; j < n; j++) { fresh->defined[j] = true; fresh->needs[j] = NULL; }
        entry = fresh;
    }
    for (j = 0; j < n; j++) {
        bool given = false, ok = false;
        const char* needs = NULL;
        FcLambdaParam* pj = (FcLambdaParam*)parameters->items[j];
        for (i = 0; i < argParams->count && i < args->count; i++)
            if (argParams->items[i] == pj) {
                given = true;
                ok = ((const FcST*)args->items[i])->nul == NUL_Defined;
                if (!ok && i < argExprs->count) needs = NeedsOf(t, (FcExpr*)argExprs->items[i]);
            }
        if (!given) {
            FcExpr* d = pj->defaultValue;
            ok = d && ((d->kind == EK_LiteralExpr && (((FcLiteralExpr*)d)->value.kind == VK_Int || ((FcLiteralExpr*)d)->value.kind == VK_Float))
                || (d->kind == EK_UnaryExpr && ((FcUnaryExpr*)d)->op == UOP_Negate && ((FcUnaryExpr*)d)->operand->kind == EK_LiteralExpr
                    && (((FcLiteralExpr*)((FcUnaryExpr*)d)->operand)->value.kind == VK_Int || ((FcLiteralExpr*)((FcUnaryExpr*)d)->operand)->value.kind == VK_Float)));
        }
        if (needs != NULL) { if (!entry->needs[j]) entry->needs[j] = fcListNew(TC_); fcListAdd(TC_, entry->needs[j], (void*)needs); }
        else if (!ok) entry->defined[j] = false;
    }
    strings = (FcStrCalls*)fcDictGet(&t->stringCalls, key);
    if (!strings || strings->count != n) {
        strings = (FcStrCalls*)fcAlloc(TC_, sizeof *strings);
        strings->count = n;
        strings->strings = (bool*)fcAlloc(TC_, sizeof(bool) * (size_t)(n + 1));
        for (j = 0; j < n; j++) strings->strings[j] = true;
        fcDictPut(&t->stringCalls, key, strings);
    }
    for (j = 0; j < n; j++) {
        bool given = false, isString = true;
        FcLambdaParam* pj = (FcLambdaParam*)parameters->items[j];
        for (i = 0; i < argParams->count && i < args->count; i++)
            if (argParams->items[i] == pj) {
                FcSKind k = ((const FcST*)args->items[i])->kind;
                given = true;
                isString = isString && (k == STK_String || k == STK_Undefined);
            }
        if (!given) {
            FcExpr* d = pj->defaultValue;
            isString = d == NULL || (d->kind == EK_LiteralExpr && (((FcLiteralExpr*)d)->value.kind == VK_String || ((FcLiteralExpr*)d)->value.kind == VK_Undefined));
        }
        if (!isString) strings->strings[j] = false;
    }
    entry->calls++;
    fcDictPut(&t->calls, key, entry);
}

/* NoteReturn */
static void NoteReturn(FcTC* t, FcExpr* value, const FcST* type) {
    FcRetNote* n;
    if (t->retNotes == NULL) return;
    n = (FcRetNote*)fcAlloc(TC_, sizeof *n);
    if (value == NULL || type == NULL) n->ok = false;
    else if (type->nul == NUL_Defined) n->ok = true;
    else n->needs = NeedsOf(t, value);
    fcListAdd(TC_, t->retNotes, n);
}

/* ---- fields ---- */
static bool SetBeforeRead(FcTC* t, FcClassDecl* decl, FcFieldDecl* fd);
static FcDict* FieldCandidates(FcTC* t) {
    int i;
    if (t->fieldCands) return t->fieldCands;
    t->fieldCands = fcDictNew(TC_, FC_KEY_STR);
    FC_DICT_EACH(t->classes, e) {
        FcClassDecl* decl = (FcClassDecl*)e->value;
        for (i = 0; i < decl->members->count; i++) {
            FcStmt* member = (FcStmt*)decl->members->items[i];
            FcFieldDecl* fd;
            bool candidate;
            if (member->kind == SK_PropertyDecl && !(((FcPropertyDecl*)member)->type && ((FcPropertyDecl*)member)->type->nonNull)) { fcSetAdd(&t->otherFieldNames, ((FcPropertyDecl*)member)->name); continue; }
            if (member->kind != SK_FieldDecl) continue;
            fd = (FcFieldDecl*)member;
            candidate = (decl->h.source >= t->firstUserSource || fcInferDefinedClass(decl)) && (fcInferDefinedClass(decl) || !fcReflectionUsed(t)) && !decl->isRecord && fd->arrayRanks->count == 0
                && fd->type && !fd->type->nonNull && fd->type->pointerDepth == 0 && fd->type->arrayRank == 0 && fd->type->lambdaSignature == NULL && fd->type->typeArgs == NULL
                && (streq0(fd->type->baseName, "int") || streq0(fd->type->baseName, "float") || streq0(fd->type->baseName, "bool"))
                && (fd->initializer == NULL ? fcInferUninitializedDefined() && SetBeforeRead(t, decl, fd) : IsConstantValue(fd->initializer));
            if (candidate) {
                FcList* list = (FcList*)fcDictGet(t->fieldCands, fd->name);
                if (!list) { list = fcListNew(TC_); fcDictPut(t->fieldCands, fd->name, list); }
                fcListAdd(TC_, list, fd);
                fcDictPut(&t->fieldOwner, fd, (void*)e->key);
            } else if (!(fd->type && fd->type->nonNull)) fcSetAdd(&t->otherFieldNames, fd->name);
        }
    }
    return t->fieldCands;
}
static const char* FieldKey(FcTC* t, FcFieldDecl* fd) { return TF(t, "fld:%s.%s", (const char*)fcDictGet(&t->fieldOwner, fd), fd->name); }

static bool fcDerivesFromDecl(FcTC* t, FcClassDecl* c, FcClassDecl* ancestor) {
    FcDict* seen = fcDictNew(TC_, FC_KEY_PTR);
    FcClassDecl* cur = c;
    int i;
    while (cur && fcSetAdd(seen, cur)) {
        FcClassDecl* next = NULL;
        if (cur->baseRefs)
            for (i = 0; i < cur->baseRefs->count && !next; i++) {
                FcTypeRef* b = (FcTypeRef*)cur->baseRefs->items[i];
                const char* n = NameOf(t, b);
                if (!(next = (FcClassDecl*)fcDictGet(t->classes, n ? n : ""))) next = (FcClassDecl*)fcDictGet(t->classes, PlainName(t, b->baseName));
            }
        if (!next) return false;
        if (next == ancestor) return true;
        cur = next;
    }
    return false;
}
static void fcBelowAdd(FcTC* t, FcDict* below, FcClassDecl* c) {
    int i;
    for (i = 0; i < c->members->count; i++) {
        FcStmt* m = (FcStmt*)c->members->items[i];
        if (m->kind == SK_MethodDecl) fcSetAdd(below, ((FcMethodDecl*)m)->name);
        else if (m->kind == SK_PropertyDecl) fcSetAdd(below, ((FcPropertyDecl*)m)->name);
        else if (m->kind == SK_FieldDecl) fcSetAdd(below, ((FcFieldDecl*)m)->name);
    }
    (void)t;
}
typedef struct FcPending { bool isStmt; void* node; } FcPending;
static void fcPendingAdd(FcTC* t, FcList* q, bool isStmt, void* node) { FcPending* p = (FcPending*)fcAlloc(TC_, sizeof *p); p->isStmt = isStmt; p->node = node; fcListAdd(TC_, q, p); }
static bool BaseConstructionSafe(FcTC* t, FcClassDecl* decl, FcFieldDecl* fd) {
    FcDict* below = fcDictNew(TC_, FC_KEY_STR);
    FcList* derived = fcListNew(TC_);
    FcList* pending = fcListNew(TC_);
    FcDict* seen = fcDictNew(TC_, FC_KEY_STR);
    FcList* chain = fcListNew(TC_);
    FcClassDecl* cur;
    int i, k, head = 0;
    FC_DICT_EACH(t->classes, e) { FcClassDecl* c = (FcClassDecl*)e->value; if (c != decl && fcDerivesFromDecl(t, c, decl)) fcListAdd(TC_, derived, c); }
    fcBelowAdd(t, below, decl);
    for (i = 0; i < derived->count; i++) fcBelowAdd(t, below, (FcClassDecl*)derived->items[i]);
    for (i = 0; i < derived->count; i++) {
        FcClassDecl* c = (FcClassDecl*)derived->items[i];
        for (k = 0; k < c->members->count; k++) {
            FcStmt* m = (FcStmt*)c->members->items[k];
            FcList* tags;
            FcList* nodes;
            if (m->kind != SK_FieldDecl || !((FcFieldDecl*)m)->initializer) continue;
            fcExprNodes(TC_, ((FcFieldDecl*)m)->initializer, &tags, &nodes);
            if (fcHasCallLike(tags, nodes, false) || fcMentionsName(tags, nodes, fd->name)) return false;
        }
    }
    for (cur = decl;;) {
        FcTypeRef* baseRef = NULL;
        FcClassDecl* bc;
        const char* n;
        if (cur->baseRefs)
            for (i = 0; i < cur->baseRefs->count && !baseRef; i++) {
                FcTypeRef* b = (FcTypeRef*)cur->baseRefs->items[i];
                const char* nm = NameOf(t, b);
                if (!fcDictHas(&t->interfaces, nm ? nm : "") && !fcDictHas(&t->interfaces, PlainName(t, b->baseName))) baseRef = b;
            }
        if (!baseRef) break;
        n = NameOf(t, baseRef);
        if (!(bc = (FcClassDecl*)fcDictGet(t->classes, n ? n : "")) && !(bc = (FcClassDecl*)fcDictGet(t->classes, PlainName(t, baseRef->baseName)))) return false;
        { bool found = false; for (i = 0; i < chain->count; i++) if (chain->items[i] == bc) found = true; if (found) break; }
        fcListAdd(TC_, chain, bc);
        cur = bc;
    }
    if (chain->count == 0) return true;
    for (i = 0; i < chain->count; i++) {
        FcClassDecl* bc = (FcClassDecl*)chain->items[i];
        for (k = 0; k < bc->members->count; k++) {
            FcStmt* m = (FcStmt*)bc->members->items[k];
            if (m->kind == SK_ConstructorDecl) {
                FcConstructorDecl* ctor = (FcConstructorDecl*)m;
                int a;
                fcPendingAdd(t, pending, true, ctor->body);
                if (ctor->baseArgs) for (a = 0; a < ctor->baseArgs->count; a++) fcPendingAdd(t, pending, false, ctor->baseArgs->items[a]);
            }
        }
        for (k = 0; k < bc->members->count; k++) {
            FcStmt* m = (FcStmt*)bc->members->items[k];
            if (m->kind == SK_FieldDecl && ((FcFieldDecl*)m)->initializer) fcPendingAdd(t, pending, false, ((FcFieldDecl*)m)->initializer);
        }
    }
    while (head < pending->count) {
        FcPending* p = (FcPending*)pending->items[head++];
        FcList* tags;
        FcList* nodes;
        int thisTargets = 0, thisCount = 0;
        if (p->isStmt) fcAstWalkAll(TC_, (FcStmt*)p->node, &tags, &nodes);
        else fcExprNodes(TC_, (FcExpr*)p->node, &tags, &nodes);
        for (i = 0; i < nodes->count; i++) {
            FcExpr* e;
            if (FC_VALI(tags->items[i]) != FN_EXPR) continue;
            e = (FcExpr*)nodes->items[i];
            if (e->kind == EK_MemberExpr && ((FcMemberExpr*)e)->target->kind == EK_ThisExpr) thisTargets++;
            if (e->kind == EK_ThisExpr) thisCount++;
        }
        if (thisCount > thisTargets) return false;
        for (i = 0; i < nodes->count; i++) {
            FcExpr* e;
            const char* used = NULL;
            if (FC_VALI(tags->items[i]) != FN_EXPR) continue;
            e = (FcExpr*)nodes->items[i];
            if (e->kind == EK_MemberExpr && ((FcMemberExpr*)e)->target->kind == EK_ThisExpr) used = ((FcMemberExpr*)e)->name;
            else if (e->kind == EK_IdentifierExpr) {
                FcResolvedRef* r;
                used = fcDictTry(t->refs, e, (void**)&r) && r->kind != RR_ImplicitThisMember ? NULL : ((FcIdentifierExpr*)e)->name;
            }
            if (used == NULL) continue;
            if (fcDictHas(below, used)) return false;
            if (!fcSetAdd(seen, used)) continue;
            for (k = 0; k < chain->count; k++) {
                FcClassDecl* bc = (FcClassDecl*)chain->items[k];
                int q;
                for (q = 0; q < bc->members->count; q++) {
                    FcStmt* m = (FcStmt*)bc->members->items[q];
                    if (m->kind == SK_MethodDecl && streq0(((FcMethodDecl*)m)->name, used)) fcPendingAdd(t, pending, true, ((FcMethodDecl*)m)->body);
                    else if (m->kind == SK_PropertyDecl && streq0(((FcPropertyDecl*)m)->name, used)) {
                        if (((FcPropertyDecl*)m)->getter) fcPendingAdd(t, pending, true, ((FcPropertyDecl*)m)->getter);
                        if (((FcPropertyDecl*)m)->setter) fcPendingAdd(t, pending, true, ((FcPropertyDecl*)m)->setter);
                    }
                }
            }
        }
    }
    return true;
}
static bool fcSimpleExpr(FcTC* t, FcExpr* e, const char* name) {
    FcList* tags;
    FcList* nodes;
    if (e == NULL) return true;
    fcExprNodes(TC_, e, &tags, &nodes);
    return !fcHasCallLike(tags, nodes, true) && !fcMentionsName(tags, nodes, name);
}
static bool SetBeforeRead(FcTC* t, FcClassDecl* decl, FcFieldDecl* fd) {
    FcList* ctors = fcListNew(TC_);
    int i, k;
    if (fd->isStatic) return false;
    if (!BaseConstructionSafe(t, decl, fd)) return false;
    for (i = 0; i < decl->members->count; i++) if (((FcStmt*)decl->members->items[i])->kind == SK_ConstructorDecl) fcListAdd(TC_, ctors, decl->members->items[i]);
    if (ctors->count == 0) return false;
    for (i = 0; i < decl->members->count; i++) {
        FcStmt* m = (FcStmt*)decl->members->items[i];
        FcList* tags;
        FcList* nodes;
        if (m->kind != SK_FieldDecl || (FcFieldDecl*)m == fd || !((FcFieldDecl*)m)->initializer) continue;
        fcExprNodes(TC_, ((FcFieldDecl*)m)->initializer, &tags, &nodes);
        if (fcMentionsName(tags, nodes, fd->name)) return false;
    }
    for (i = 0; i < ctors->count; i++) {
        FcConstructorDecl* ctor = (FcConstructorDecl*)ctors->items[i];
        bool set = false;
        if (ctor->baseArgs)
            for (k = 0; k < ctor->baseArgs->count; k++) {
                FcList* tags;
                FcList* nodes;
                fcExprNodes(TC_, (FcExpr*)ctor->baseArgs->items[k], &tags, &nodes);
                if (fcMentionsName(tags, nodes, fd->name)) return false;
            }
        for (k = 0; k < ctor->body->statements->count; k++) {
            FcStmt* stmt = (FcStmt*)ctor->body->statements->items[k];
            if (stmt->kind == SK_ExprStmt && ((FcExprStmt*)stmt)->expression->kind == EK_AssignExpr) {
                FcAssignExpr* a = (FcAssignExpr*)((FcExprStmt*)stmt)->expression;
                bool simple = fcSimpleExpr(t, a->value, fd->name);
                if (simple && ((a->target->kind == EK_IdentifierExpr && streq0(((FcIdentifierExpr*)a->target)->name, fd->name))
                    || (a->target->kind == EK_MemberExpr && ((FcMemberExpr*)a->target)->target->kind == EK_ThisExpr && streq0(((FcMemberExpr*)a->target)->name, fd->name)))) { set = true; break; }
                if (simple && (a->target->kind == EK_IdentifierExpr || (a->target->kind == EK_MemberExpr && ((FcMemberExpr*)a->target)->target->kind == EK_ThisExpr))) continue;
            }
            if (stmt->kind == SK_VarDeclStmt && fcSimpleExpr(t, ((FcVarDeclStmt*)stmt)->initializer, fd->name)) continue;
            break;
        }
        if (!set) return false;
    }
    return true;
}

static bool IsFieldRef(FcTC* t, FcIdentifierExpr* id) {
    FcResolvedRef* r = (FcResolvedRef*)fcDictGet(t->refs, id);
    return r && (r->kind == RR_ImplicitThisMember || r->kind == RR_StaticMember) && !IsLocal(t, id->name);
}
static const char* ReceiverClass(FcTC* t, FcExpr* access) {
    if (access->kind == EK_IdentifierExpr) {
        FcResolvedRef* r = (FcResolvedRef*)fcDictGet(t->refs, access);
        if (r && r->kind == RR_StaticMember) return r->className;
        return t->cls ? t->cls->name : NULL;
    }
    if (access->kind == EK_MemberExpr) {
        FcMemberExpr* m = (FcMemberExpr*)access;
        const char* staticClass;
        const FcST* type;
        if (m->target->kind == EK_ThisExpr) return t->cls ? t->cls->name : NULL;
        if ((staticClass = StaticClassRef(t, m->target)) != NULL) return staticClass;
        if ((type = (const FcST*)fcDictGet(&t->exprTypes, m->target)) != NULL && type->kind == STK_Class && type->cls) return type->cls;
    }
    return NULL;
}
static FcList* FieldsOf(FcTC* t, FcExpr* access, const char* name) {
    FcList* all;
    const char* cls;
    FcList* result = fcListNew(TC_);
    int guard, i;
    if (!t->deriveDefined || !(all = (FcList*)fcDictGet(FieldCandidates(t), name))) return result;
    if ((cls = ReceiverClass(t, access)) == NULL) return all;
    for (guard = 0; guard < 64 && cls; guard++) {
        FcClassDecl* decl = (FcClassDecl*)fcDictGet(t->classes, cls);
        FcFieldDecl* own = NULL;
        FcTypeRef* baseRef = NULL;
        if (!decl) break;
        for (i = 0; i < decl->members->count && !own; i++) {
            FcStmt* m = (FcStmt*)decl->members->items[i];
            if (m->kind == SK_FieldDecl && streq0(((FcFieldDecl*)m)->name, name)) own = (FcFieldDecl*)m;
        }
        if (own) { for (i = 0; i < all->count; i++) if (all->items[i] == own) fcListAdd(TC_, result, own); return result; }
        if (decl->baseRefs)
            for (i = 0; i < decl->baseRefs->count && !baseRef; i++) {
                const char* n = NameOf(t, (FcTypeRef*)decl->baseRefs->items[i]);
                if (n && fcDictHas(t->classes, n)) baseRef = (FcTypeRef*)decl->baseRefs->items[i];
            }
        cls = baseRef ? NameOf(t, baseRef) : NULL;
    }
    return result;
}
static const char* FieldNeed(FcTC* t, FcExpr* access, const char* name) {
    FcList* fields = FieldsOf(t, access, name);
    if (fields->count != 1) return NULL;
    if (ReceiverClass(t, access) == NULL
        && (fcDictHas(&t->otherFieldNames, name) || ((FcList*)fcDictGet(FieldCandidates(t), name))->count != 1 || ((FcFieldDecl*)fields->items[0])->isStatic)) return NULL;
    return fcDictHas(&t->fieldBad, fields->items[0]) ? NULL : FieldKey(t, (FcFieldDecl*)fields->items[0]);
}
static FcList* FieldValueNeeds(FcTC* t, FcExpr* value, const FcST* type) {
    const char* single;
    if (!type) type = (const FcST*)fcDictGet(&t->exprTypes, value);
    if (type && (type->nul == NUL_Defined || type->nul == NUL_Inferred) && stIsValueKind(type)) return fcListNew(TC_);
    if (value->kind == EK_ConditionalExpr) {
        FcList* a = FieldValueNeeds(t, ((FcConditionalExpr*)value)->then, NULL);
        FcList* b = a == NULL ? NULL : FieldValueNeeds(t, ((FcConditionalExpr*)value)->otherwise, NULL);
        int i;
        if (a == NULL || b == NULL) return NULL;
        for (i = 0; i < b->count; i++) fcListAdd(TC_, a, b->items[i]);
        return a;
    }
    if (value->kind == EK_TypeCoerceExpr) return fcListNew(TC_);
    if ((single = NeedsOf(t, value)) != NULL) { FcList* l = fcListNew(TC_); fcListAdd(TC_, l, (void*)single); return l; }
    return NULL;
}
static void NoteFieldWrite(FcTC* t, FcExpr* target, const char* name, FcExpr* value, const FcST* type) {
    FcList* fields = FieldsOf(t, target, name);
    FcList* needs;
    int i, k;
    if (fields->count == 0) return;
    needs = FieldValueNeeds(t, value, type);
    for (i = 0; i < fields->count; i++) {
        FcList* all;
        if (needs == NULL) { fcSetAdd(&t->fieldBad, fields->items[i]); continue; }
        all = (FcList*)fcDictGet(&t->fieldNeeds, fields->items[i]);
        if (!all) { all = fcListNew(TC_); fcDictPut(&t->fieldNeeds, fields->items[i], all); }
        for (k = 0; k < needs->count; k++) fcListAdd(TC_, all, needs->items[k]);
    }
}
static const char* fcAccessName(FcExpr* e) {
    if (e->kind == EK_MemberExpr) return ((FcMemberExpr*)e)->name;
    if (e->kind == EK_IdentifierExpr) return ((FcIdentifierExpr*)e)->name;
    return NULL;
}
static void fcFieldBadByName(FcTC* t, const char* name) {
    FcList* fields = (FcList*)fcDictGet(FieldCandidates(t), name);
    int i;
    if (!fields) return;
    for (i = 0; i < fields->count; i++) fcSetAdd(&t->fieldBad, fields->items[i]);
}
static void DisqualifyFields(FcTC* t) {
    FcAllNodes k;
    FcDict* refMethods = fcDictNew(TC_, FC_KEY_STR);
    FcDict* asked = fcDictNew(TC_, FC_KEY_STR);
    bool jsonSeen = false;
    int i, j;
    if (fcDictCount(FieldCandidates(t)) == 0) return;
    k.c = TC_; k.tags = fcListNew(TC_); k.nodes = fcListNew(TC_);
    for (i = 0; i < t->program->count; i++) fcWalkStmt((FcStmt*)t->program->items[i], fcAllVisit, &k);
    FC_DICT_EACH(t->classes, e) {
        FcClassDecl* decl = (FcClassDecl*)e->value;
        for (i = 0; i < decl->members->count; i++) {
            FcStmt* m = (FcStmt*)decl->members->items[i];
            if (m->kind != SK_MethodDecl) continue;
            for (j = 0; j < ((FcMethodDecl*)m)->params->count; j++) if (((FcLambdaParam*)((FcMethodDecl*)m)->params->items[j])->byRef) { fcSetAdd(refMethods, ((FcMethodDecl*)m)->name); break; }
        }
    }
    for (i = 0; i < k.nodes->count; i++) {
        FcNodeTag tag = (FcNodeTag)FC_VALI(k.tags->items[i]);
        void* node = k.nodes->items[i];
        if (tag == FN_OTHER) {
            /* a WithSet among the other nodes: told apart from the rest by the walk (fcWalkExpr visits the sets of a WithExpr after its source) */
            continue;
        }
        if (tag != FN_EXPR) continue;
        {
            FcExpr* e = (FcExpr*)node;
            const char* n;
            switch (e->kind) {
                case EK_UnaryExpr:
                    if (((FcUnaryExpr*)e)->op == UOP_AddressOf && (n = fcAccessName(((FcUnaryExpr*)e)->operand)) != NULL) fcFieldBadByName(t, n);
                    break;
                case EK_WithExpr: {
                    FcWithExpr* w = (FcWithExpr*)e;
                    for (j = 0; j < w->sets->count; j++) fcFieldBadByName(t, ((FcWithSet*)w->sets->items[j])->name);
                    break;
                }
                case EK_CallExpr: {
                    FcCallExpr* call = (FcCallExpr*)e;
                    if (!jsonSeen && call->callee->kind == EK_MemberExpr && streq0(((FcMemberExpr*)call->callee)->name, "Deserialize")
                        && ((FcMemberExpr*)call->callee)->target->kind == EK_IdentifierExpr && streq0(((FcIdentifierExpr*)((FcMemberExpr*)call->callee)->target)->name, "Json")) {
                        FC_DICT_EACH(FieldCandidates(t), fe) {
                            FcList* l = (FcList*)fe->value;
                            for (j = 0; j < l->count; j++)
                                if (!fcInferDefinedClass((FcClassDecl*)fcDictGet(t->classes, (const char*)fcDictGet(&t->fieldOwner, l->items[j])))) fcSetAdd(&t->fieldBad, l->items[j]);
                        }
                        jsonSeen = true;
                    }
                    if ((n = fcAccessName(call->callee)) != NULL && fcDictHas(refMethods, n))
                        for (j = 0; j < call->args->count; j++) { const char* passed = fcAccessName((FcExpr*)call->args->items[j]); if (passed) fcFieldBadByName(t, passed); }
                    break;
                }
                case EK_BinaryExpr: {
                    FcBinaryExpr* cmp = (FcBinaryExpr*)e;
                    if (cmp->op != BOP_Eq && cmp->op != BOP_NotEq) break;
                    if (cmp->left->kind == EK_LiteralExpr && ((FcLiteralExpr*)cmp->left)->value.kind == VK_Undefined && (n = fcAccessName(cmp->right)) != NULL) fcSetAdd(asked, n);
                    if (cmp->right->kind == EK_LiteralExpr && ((FcLiteralExpr*)cmp->right)->value.kind == VK_Undefined && (n = fcAccessName(cmp->left)) != NULL) fcSetAdd(asked, n);
                    break;
                }
                case EK_IsOfExpr: if ((n = fcAccessName(((FcIsOfExpr*)e)->operand)) != NULL) fcSetAdd(asked, n); break;
                case EK_ConditionalAccessExpr: if ((n = fcAccessName(((FcConditionalAccessExpr*)e)->target)) != NULL) fcSetAdd(asked, n); break;
                default: break;
            }
        }
    }
    FC_DICT_EACH(asked, e) {
        FcList* fields = (FcList*)fcDictGet(FieldCandidates(t), e->key);
        if (!fields) continue;
        for (j = 0; j < fields->count; j++) if (((FcFieldDecl*)fields->items[j])->initializer == NULL) fcSetAdd(&t->fieldBad, fields->items[j]);
    }
}

/* the methods a class of the program may override or implement for a package or the prelude */
static FcDict* DeriveForeign(FcTC* t, FcClassDecl* decl) {
    FcDict* keys = fcDictNew(TC_, FC_KEY_STR);
    FcDict* seenTypes = fcDictNew(TC_, FC_KEY_STR);
    FcList* todo = fcListNew(TC_);
    int i;
    if (decl->baseRefs) for (i = decl->baseRefs->count - 1; i >= 0; i--) fcListAdd(TC_, todo, decl->baseRefs->items[i]);
    while (todo->count > 0) {
        FcTypeRef* b = (FcTypeRef*)todo->items[--todo->count];
        const char* nm = NameOf(t, b);
        const char* bn = nm ? nm : PlainName(t, b->baseName);
        FcClassDecl* bc;
        FcInterfaceDecl* ic;
        if (!fcSetAdd(seenTypes, bn)) continue;
        if ((bc = (FcClassDecl*)fcDictGet(t->classes, bn)) != NULL || (bc = (FcClassDecl*)fcDictGet(t->classes, PlainName(t, b->baseName))) != NULL) {
            if (bc->h.source < t->firstUserSource)
                for (i = 0; i < bc->members->count; i++) {
                    FcStmt* m = (FcStmt*)bc->members->items[i];
                    if (m->kind == SK_MethodDecl) fcSetAdd(keys, MethodKey(t, ((FcMethodDecl*)m)->name, ((FcMethodDecl*)m)->params->count));
                }
            if (bc->baseRefs) for (i = bc->baseRefs->count - 1; i >= 0; i--) fcListAdd(TC_, todo, bc->baseRefs->items[i]);
        } else if ((ic = (FcInterfaceDecl*)fcDictGet(&t->interfaces, bn)) != NULL || (ic = (FcInterfaceDecl*)fcDictGet(&t->interfaces, PlainName(t, b->baseName))) != NULL) {
            if (ic->h.source < t->firstUserSource)
                for (i = 0; i < ic->methods->count; i++) fcSetAdd(keys, MethodKey(t, ((FcInterfaceMethodSig*)ic->methods->items[i])->name, ((FcInterfaceMethodSig*)ic->methods->items[i])->params->count));
            if (ic->baseRefs) for (i = ic->baseRefs->count - 1; i >= 0; i--) fcListAdd(TC_, todo, ic->baseRefs->items[i]);
        } else fcSetAdd(keys, "*");
    }
    return keys;
}
typedef struct FcCandidate { const char* key; FcList* params; } FcCandidate;
static FcList* DerivationCandidates(FcTC* t, const char* name, FcClassDecl* decl, FcDict* foreign) {
    FcList* r = fcListNew(TC_);
    int i;
    for (i = 0; i < decl->members->count; i++) {
        FcStmt* member = (FcStmt*)decl->members->items[i];
        FcCandidate* cand;
        if (member->kind == SK_MethodDecl) {
            FcMethodDecl* m = (FcMethodDecl*)member;
            const char* key;
            if (fcDictHas(&t->unseenNames, m->name) || fcCalledByTheLanguage(m->name) || fcStartsWith(m->name, "operator")) continue;
            if (!fcInferDefinedClass(decl) && (fcReflectionUsed(t) || fcDictHas(&t->stringLiterals, m->name))) continue;
            key = MethodKey(t, m->name, m->params->count);
            if (fcDictHas(foreign, key) || fcDictHas(foreign, "*")) continue;
            cand = (FcCandidate*)fcAlloc(TC_, sizeof *cand); cand->key = key; cand->params = m->params;
            fcListAdd(TC_, r, cand);
        } else if (member->kind == SK_ConstructorDecl) {
            if (!fcInferDefinedClass(decl) && fcReflectionUsed(t)) continue;
            if (fcDictHas(&t->baseClasses, name) || fcDictHas(&t->baseClasses, PlainName(t, name)) || fcDictHas(&t->unseenCtors, name)) continue;
            cand = (FcCandidate*)fcAlloc(TC_, sizeof *cand); cand->key = CtorKey(t, name, ((FcConstructorDecl*)member)->params->count); cand->params = ((FcConstructorDecl*)member)->params;
            fcListAdd(TC_, r, cand);
        }
    }
    return r;
}
static bool Derivable(FcLambdaParam* p) {
    FcTypeRef* ty = p->type;
    return !p->byRef && ty && !ty->nonNull && ty->pointerDepth == 0 && ty->arrayRank == 0 && ty->lambdaSignature == NULL && ty->typeArgs == NULL
        && (streq0(ty->baseName, "int") || streq0(ty->baseName, "float")) && p->arrayRanks->count == 0;
}
static bool fcNumberReturn(FcTypeRef* r) {
    return r && r->pointerDepth == 0 && r->arrayRank == 0 && r->lambdaSignature == NULL && r->typeArgs == NULL && (streq0(r->baseName, "int") || streq0(r->baseName, "float"));
}

static FcDict* DeriveReturns(FcTC* t) {
    FcDict* decls = fcDictNew(TC_, FC_KEY_STR);
    FcDict* blocked = fcDictNew(TC_, FC_KEY_STR);
    FcDict* ok = fcDictNew(TC_, FC_KEY_STR);
    FcDict* paramNeeds = fcDictNew(TC_, FC_KEY_STR);
    int i, j;
    bool changed;
    FC_DICT_EACH(t->classes, e) {
        FcClassDecl* decl = (FcClassDecl*)e->value;
        FcDict* foreign;
        if (decl->h.source < t->firstUserSource) continue;
        foreign = DeriveForeign(t, decl);
        for (i = 0; i < decl->members->count; i++) {
            FcStmt* mm = (FcStmt*)decl->members->items[i];
            FcMethodDecl* m;
            const char* key;
            FcList* list;
            FcReturns* r;
            if (mm->kind != SK_MethodDecl) continue;
            m = (FcMethodDecl*)mm;
            key = MethodKey(t, m->name, m->params->count);
            if (!(list = (FcList*)fcDictGet(decls, key))) { list = fcListNew(TC_); fcDictPut(decls, key, list); }
            fcListAdd(TC_, list, m);
            r = (FcReturns*)fcDictGet(&t->returns, m);
            if (!fcNumberReturn(m->returnType) || fcDictHas(foreign, key) || fcDictHas(foreign, "*") || fcDictHas(&t->unseenNames, m->name) || fcCalledByTheLanguage(m->name) || !r
                || (!r->ends && !(m->returnType && m->returnType->nonNull)))
                fcSetAdd(blocked, key);
        }
    }
    FC_DICT_EACH(decls, e) if (!fcDictHas(blocked, e->key)) fcSetAdd(ok, TF(t, "ret:%s", (const char*)e->key));
    FC_DICT_EACH(t->classes, e) {
        FcClassDecl* decl = (FcClassDecl*)e->value;
        FcList* cands;
        if (decl->h.source < t->firstUserSource) continue;
        cands = DerivationCandidates(t, (const char*)e->key, decl, DeriveForeign(t, decl));
        for (i = 0; i < cands->count; i++) {
            FcCandidate* cand = (FcCandidate*)cands->items[i];
            FcCallInfo* seen = (FcCallInfo*)fcDictGet(&t->calls, cand->key);
            if (!seen || seen->calls == 0 || seen->count != cand->params->count) continue;
            for (j = 0; j < cand->params->count; j++) {
                FcLambdaParam* p = (FcLambdaParam*)cand->params->items[j];
                if (seen->defined[j] && Derivable(p)) {
                    fcSetAdd(ok, ParamId(t, p));
                    fcDictPut(paramNeeds, ParamId(t, p), seen->needs[j] ? seen->needs[j] : fcListNew(TC_));
                }
            }
        }
    }
    FC_DICT_EACH(FieldCandidates(t), e) {
        FcList* l = (FcList*)e->value;
        for (i = 0; i < l->count; i++) {
            FcFieldDecl* fd = (FcFieldDecl*)l->items[i];
            FcList* fieldNeeds;
            if (fcDictHas(&t->fieldBad, fd)) continue;
            fcSetAdd(ok, FieldKey(t, fd));
            fieldNeeds = (FcList*)fcDictGet(&t->fieldNeeds, fd);
            fcDictPut(paramNeeds, FieldKey(t, fd), fieldNeeds ? fieldNeeds : fcListNew(TC_));
        }
    }
    for (changed = true; changed;) {
        FcList* items = fcListNew(TC_);
        changed = false;
        FC_DICT_EACH(ok, e) fcListAdd(TC_, items, (void*)e->key);
        for (i = 0; i < items->count; i++) {
            const char* item = (const char*)items->items[i];
            bool fails = false;
            if (fcStartsWith(item, "ret:")) {
                FcList* ms = (FcList*)fcDictGet(decls, item + 4);
                for (j = 0; ms && j < ms->count && !fails; j++) {
                    FcMethodDecl* m = (FcMethodDecl*)ms->items[j];
                    FcReturns* r;
                    int q;
                    if (m->returnType && m->returnType->nonNull) continue;
                    r = (FcReturns*)fcDictGet(&t->returns, m);
                    for (q = 0; r && q < r->notes->count; q++) {
                        FcRetNote* n = (FcRetNote*)r->notes->items[q];
                        if (!n->ok && (n->needs == NULL || !fcDictHas(ok, n->needs))) { fails = true; break; }
                    }
                }
            } else {
                FcList* needs = (FcList*)fcDictGet(paramNeeds, item);
                for (j = 0; needs && j < needs->count; j++) if (!fcDictHas(ok, needs->items[j])) { fails = true; break; }
            }
            if (fails) { fcDictRemove(ok, item); changed = true; }
        }
    }
    return ok;
}

static void Derive(FcTC* t, FcResolveResult* resolved) {
    FcDict* ok;
    int i, j;
    if (!resolved->derivedDefined) resolved->derivedDefined = fcDictNew(TC_, FC_KEY_PTR);
    if (!resolved->derivedStrings) resolved->derivedStrings = fcDictNew(TC_, FC_KEY_PTR);
    if (!resolved->derivedReturns) resolved->derivedReturns = fcDictNew(TC_, FC_KEY_PTR);
    if (!resolved->derivedDefinedFields) resolved->derivedDefinedFields = fcDictNew(TC_, FC_KEY_PTR);
    FC_DICT_EACH(t->classes, e) {
        FcClassDecl* decl = (FcClassDecl*)e->value;
        if (!decl->baseRefs) continue;
        for (i = 0; i < decl->baseRefs->count; i++) {
            FcTypeRef* b = (FcTypeRef*)decl->baseRefs->items[i];
            const char* full = NameOf(t, b);
            fcSetAdd(&t->baseClasses, PlainName(t, b->baseName));
            if (full) fcSetAdd(&t->baseClasses, full);
        }
    }
    DisqualifyFields(t);
    ok = DeriveReturns(t);
    FC_DICT_EACH(FieldCandidates(t), e) {
        FcList* fields = (FcList*)e->value;
        for (i = 0; i < fields->count; i++) {
            FcFieldDecl* fd = (FcFieldDecl*)fields->items[i];
            if (fcDictHas(ok, FieldKey(t, fd)) && (fd->initializer != NULL || fcDictHas(&t->fieldNeeds, fd))) fcSetAdd(resolved->derivedDefinedFields, fd);
        }
    }
    FC_DICT_EACH(t->classes, e) {
        FcClassDecl* decl = (FcClassDecl*)e->value;
        FcList* cands;
        if (decl->h.source < t->firstUserSource) continue;
        for (i = 0; i < decl->members->count; i++) {
            FcStmt* mm = (FcStmt*)decl->members->items[i];
            FcMethodDecl* m;
            if (mm->kind != SK_MethodDecl) continue;
            m = (FcMethodDecl*)mm;
            if (m->returnType && !m->returnType->nonNull && fcDictHas(ok, TF(t, "ret:%s", MethodKey(t, m->name, m->params->count)))) fcSetAdd(resolved->derivedReturns, m->body);
        }
        cands = DerivationCandidates(t, (const char*)e->key, decl, DeriveForeign(t, decl));
        for (i = 0; i < cands->count; i++) {
            FcCandidate* cand = (FcCandidate*)cands->items[i];
            FcCallInfo* seenCalls = (FcCallInfo*)fcDictGet(&t->calls, cand->key);
            FcStrCalls* strings = (FcStrCalls*)fcDictGet(&t->stringCalls, cand->key);
            if (seenCalls && seenCalls->calls > 0 && strings && strings->count == cand->params->count)
                for (j = 0; j < cand->params->count; j++) {
                    FcLambdaParam* p = (FcLambdaParam*)cand->params->items[j];
                    if (strings->strings[j] && !p->byRef && p->arrayRanks->count == 0 && p->type && p->type->pointerDepth == 0 && p->type->arrayRank == 0 && p->type->lambdaSignature == NULL
                        && streq0(p->type->baseName, "string"))
                        fcSetAdd(resolved->derivedStrings, p);
                }
            for (j = 0; j < cand->params->count; j++) {
                FcLambdaParam* p = (FcLambdaParam*)cand->params->items[j];
                if (fcDictHas(&t->paramIds, p) && fcDictHas(ok, ParamId(t, p))) fcSetAdd(resolved->derivedDefined, p);
            }
        }
    }
}

/* the types of the expressions (TypeChecker._exprTypes: what a write of a field gave, DeriveFields) */
static void fcNoteExprType(FcTC* t, FcExpr* expr, const FcST* type) { if (t->deriveDefined) fcDictPut(&t->exprTypes, expr, (void*)type); }
