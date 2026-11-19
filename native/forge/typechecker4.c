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
