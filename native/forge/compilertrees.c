/* native/forge/compilertrees.c - port of src/fire.Compiler/Compiler.Trees.cs (commit 6bc618e7): a `lambda expression<T>` becomes the tree that
 * describes it (docs/ROADMAP_EXPRESSIONS.md) - a builder lambda that makes the nodes (`new LambdaExpression(...)`), with the compiled lambda attached
 * (`__Attach`). The nodes are made as expressions of the tree and compiled like the code around them. */

typedef struct FcTreeScope { FcDict* params; FcLambdaExpr* nested; struct FcTreeScope* outer; } FcTreeScope;   /* params: name -> index + 1 */

static FcDict* TreeParamsOf(FcCtx* c, FcLambdaExpr* lam, FcTreeScope* outer) {
    FcDict* map = outer ? fcDictCopy(c, outer->params) : fcDictNew(c, FC_KEY_STR);
    int i;
    for (i = 0; i < lam->params->count; i++) fcDictPut(map, ((FcLambdaParam*)lam->params->items[i])->name, FC_IVAL(i + 1));
    return map;
}
static const char* TreeType(FcComp* cm, FcExpr* e) {
    void* t;
    return cm->sh->rr->treeTypes && fcDictTry(cm->sh->rr->treeTypes, e, &t) ? (const char*)t : "any";
}
static FcExpr* TreeText(FcCtx* c, int line, const char* text) { FcValue v; memset(&v, 0, sizeof v); v.kind = VK_String; v.s = text; return (FcExpr*)fcNewLiteral(c, line, v); }
static FcExpr* TreeInt(FcCtx* c, int line, int64_t n) { FcValue v; memset(&v, 0, sizeof v); v.kind = VK_Int; v.i = n; return (FcExpr*)fcNewLiteral(c, line, v); }
static FcExpr* TreeBool(FcCtx* c, int line, bool b) { FcValue v; memset(&v, 0, sizeof v); v.kind = VK_Bool; v.b = b; return (FcExpr*)fcNewLiteral(c, line, v); }
static FcExpr* TreeNodeOf(FcCtx* c, int line, const char* cls, int n, ...) {
    FcList* args = fcListNew(c);
    va_list ap;
    int i;
    va_start(ap, n);
    for (i = 0; i < n; i++) fcListAdd(c, args, va_arg(ap, FcExpr*));
    va_end(ap);
    return (FcExpr*)FC_NEW(c, FcNewExpr, .h = { EK_NewExpr, line }, .classRef = fcTrNew(c, cls), .args = args);
}
static FcExpr* TreeNodes(FcCtx* c, int line, FcList* items) { return (FcExpr*)FC_NEW(c, FcArrayLiteralExpr, .h = { EK_ArrayLiteralExpr, line }, .elements = items); }

static const char* TypeRefText(FcCtx* c, const FcTypeRef* t) {
    FcBuf b = {0};
    int i;
    if (!(t->typeArgs && t->typeArgs->count > 0)) return t->baseName;
    fcBufStr(&b, t->baseName);
    fcBufChar(&b, '<');
    for (i = 0; i < t->typeArgs->count; i++) { if (i) fcBufStr(&b, ", "); fcBufStr(&b, TypeRefText(c, (const FcTypeRef*)t->typeArgs->items[i])); }
    fcBufChar(&b, '>');
    return fcBufDone(c, &b);
}

/* Children (the walk of the tree) */
static FcList* TreeChildren(FcCtx* c, FcExpr* e) {
    FcList* r = fcListNew(c);
    int i;
    switch (e->kind) {
        case EK_MemberExpr: fcListAdd(c, r, ((FcMemberExpr*)e)->target); break;
        case EK_IndexExpr: fcListAdd(c, r, ((FcIndexExpr*)e)->target); fcListAdd(c, r, ((FcIndexExpr*)e)->index); break;
        case EK_CallExpr: fcListAdd(c, r, ((FcCallExpr*)e)->callee); for (i = 0; i < ((FcCallExpr*)e)->args->count; i++) fcListAdd(c, r, ((FcCallExpr*)e)->args->items[i]); break;
        case EK_BinaryExpr: fcListAdd(c, r, ((FcBinaryExpr*)e)->left); fcListAdd(c, r, ((FcBinaryExpr*)e)->right); break;
        case EK_UnaryExpr: fcListAdd(c, r, ((FcUnaryExpr*)e)->operand); break;
        case EK_ConditionalExpr: fcListAdd(c, r, ((FcConditionalExpr*)e)->condition); fcListAdd(c, r, ((FcConditionalExpr*)e)->then); fcListAdd(c, r, ((FcConditionalExpr*)e)->otherwise); break;
        case EK_NewExpr: for (i = 0; i < ((FcNewExpr*)e)->args->count; i++) fcListAdd(c, r, ((FcNewExpr*)e)->args->items[i]); break;
        case EK_IsOfExpr: fcListAdd(c, r, ((FcIsOfExpr*)e)->operand); break;
        case EK_TypeCoerceExpr: fcListAdd(c, r, ((FcTypeCoerceExpr*)e)->operand); break;
        case EK_ArrayLiteralExpr: for (i = 0; i < ((FcArrayLiteralExpr*)e)->elements->count; i++) fcListAdd(c, r, ((FcArrayLiteralExpr*)e)->elements->items[i]); break;
        case EK_LambdaExpr: {
            FcBlockStmt* body = ((FcLambdaExpr*)e)->body;
            if (body->statements->count == 1 && ((FcStmt*)body->statements->items[0])->kind == SK_ReturnStmt && ((FcReturnStmt*)body->statements->items[0])->value)
                fcListAdd(c, r, ((FcReturnStmt*)body->statements->items[0])->value);
            break;
        }
        default: break;
    }
    return r;
}
static void TreeWalk(FcCtx* c, FcExpr* e, FcList* out) {
    FcList* children;
    int i;
    fcListAdd(c, out, e);
    children = TreeChildren(c, e);
    for (i = 0; i < children->count; i++) TreeWalk(c, (FcExpr*)children->items[i], out);
}

static const char* ParamTypeOf(FcComp* cm, FcLambdaExpr* lam, int index) {
    const char* name = ((FcLambdaParam*)lam->params->items[index])->name;
    FcExpr* body = ((FcReturnStmt*)lam->body->statements->items[0])->value;
    FcList* all = fcListNew(CC_);
    int i;
    TreeWalk(CC_, body, all);
    for (i = 0; i < all->count; i++) {
        FcExpr* e = (FcExpr*)all->items[i];
        if (e->kind == EK_IdentifierExpr && streq0(((FcIdentifierExpr*)e)->name, name)) {
            const char* t = TreeType(cm, e);
            if (!streq0(t, "any")) return t;
        }
    }
    return "any";
}

static bool DependsOnParams(FcCtx* c, FcExpr* e, FcTreeScope* scope) {
    FcList* children;
    int i;
    if (e->kind == EK_IdentifierExpr) return fcDictHas(scope->params, ((FcIdentifierExpr*)e)->name);
    if (e->kind == EK_LambdaExpr) {
        FcTreeScope* inner = (FcTreeScope*)fcAlloc(c, sizeof *inner);
        inner->params = TreeParamsOf(c, (FcLambdaExpr*)e, scope); inner->outer = scope; inner->nested = (FcLambdaExpr*)e;
        children = TreeChildren(c, e);
        for (i = 0; i < children->count; i++) DependsOnParams(c, (FcExpr*)children->items[i], inner);
        return true;
    }
    children = TreeChildren(c, e);
    for (i = 0; i < children->count; i++) if (DependsOnParams(c, (FcExpr*)children->items[i], scope)) return true;
    return false;
}

static const char* TreeOpText(FcBinaryOp op) {
    switch (op) {
        case BOP_Add: return "+"; case BOP_Sub: return "-"; case BOP_Mul: return "*"; case BOP_Div: return "/"; case BOP_Mod: return "%";
        case BOP_Eq: return "=="; case BOP_NotEq: return "!="; case BOP_Lt: return "<"; case BOP_LtEq: return "<="; case BOP_Gt: return ">"; case BOP_GtEq: return ">=";
        case BOP_And: return "&&"; case BOP_Or: return "||"; case BOP_BitAnd: return "&"; case BOP_BitOr: return "|"; case BOP_BitXor: return "^^";
        case BOP_ShiftLeft: return "<<"; case BOP_ShiftRight: return ">>"; case BOP_ShiftRightLogical: return ">>>"; case BOP_Power: return "^";
        default: return "?";
    }
}

static const char* fcValueText(FcCtx* c, const FcValue* v);
static const char* TreeSourceText(FcComp* cm, FcExpr* e) {
    FcCtx* c = CC_;
    FcBuf b = {0};
    int i;
    switch (e->kind) {
        case EK_IdentifierExpr: return ((FcIdentifierExpr*)e)->name;
        case EK_ThisExpr: return "this";
        case EK_MemberExpr: return fcFmt(c, "%s.%s", TreeSourceText(cm, ((FcMemberExpr*)e)->target), ((FcMemberExpr*)e)->name);
        case EK_IndexExpr: return fcFmt(c, "%s[%s]", TreeSourceText(cm, ((FcIndexExpr*)e)->target), TreeSourceText(cm, ((FcIndexExpr*)e)->index));
        case EK_CallExpr:
            fcBufStr(&b, TreeSourceText(cm, ((FcCallExpr*)e)->callee)); fcBufChar(&b, '(');
            for (i = 0; i < ((FcCallExpr*)e)->args->count; i++) { if (i) fcBufStr(&b, ", "); fcBufStr(&b, TreeSourceText(cm, (FcExpr*)((FcCallExpr*)e)->args->items[i])); }
            fcBufChar(&b, ')');
            return fcBufDone(c, &b);
        case EK_LiteralExpr: {
            const FcValue* v = &((FcLiteralExpr*)e)->value;
            if (v->kind == VK_String) return fcFmt(c, "\"%s\"", v->s);
            if (v->kind == VK_Undefined) return "undefined";
            if (v->kind == VK_Bool) return v->b ? "true" : "false";
            return fcValueText(c, v);
        }
        case EK_BinaryExpr: return fcFmt(c, "(%s %s %s)", TreeSourceText(cm, ((FcBinaryExpr*)e)->left), TreeOpText(((FcBinaryExpr*)e)->op), TreeSourceText(cm, ((FcBinaryExpr*)e)->right));
        case EK_UnaryExpr: return fcFmt(c, "%s%s", ((FcUnaryExpr*)e)->op == UOP_LogicalNot ? "!" : "-", TreeSourceText(cm, ((FcUnaryExpr*)e)->operand));
        case EK_NewExpr:
            fcBufFmt(&b, "new %s(", TypeRefText(c, ((FcNewExpr*)e)->classRef));
            for (i = 0; i < ((FcNewExpr*)e)->args->count; i++) { if (i) fcBufStr(&b, ", "); fcBufStr(&b, TreeSourceText(cm, (FcExpr*)((FcNewExpr*)e)->args->items[i])); }
            fcBufChar(&b, ')');
            return fcBufDone(c, &b);
        case EK_ArrayLiteralExpr:
            fcBufChar(&b, '[');
            for (i = 0; i < ((FcArrayLiteralExpr*)e)->elements->count; i++) { if (i) fcBufStr(&b, ", "); fcBufStr(&b, TreeSourceText(cm, (FcExpr*)((FcArrayLiteralExpr*)e)->elements->items[i])); }
            fcBufChar(&b, ']');
            return fcBufDone(c, &b);
        default: {
            /* e.GetType().Name.Replace("Expr", "").ToLowerInvariant() */
            const char* n = fcExprKindName(e->kind);
            const char* x = strstr(n, "Expr");
            const char* name = x ? fcFmt(c, "%.*s%s", (int)(x - n), n, x + 4) : n;
            return fcAsciiLower(c, name);
        }
    }
}

static FcExpr* TreeNode(FcComp* cm, FcExpr* e, FcTreeScope* scope);
static FcExpr* InScope(FcComp* cm, FcExpr* e, FcTreeScope* scope);

static FcExpr* TreeLambda(FcComp* cm, FcLambdaExpr* lam, FcExpr* body, FcTreeScope* scope) {
    FcCtx* c = CC_;
    int line = lam->h.line, i;
    FcList* parameters = fcListNew(c);
    for (i = 0; i < lam->params->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)lam->params->items[i];
        fcListAdd(c, parameters, TreeNodeOf(c, line, "ParameterExpression", 3, TreeText(c, line, p->name), TreeInt(c, line, i),
            TreeText(c, line, p->type ? TypeRefText(c, p->type) : ParamTypeOf(cm, lam, i))));
    }
    return TreeNodeOf(c, line, "LambdaExpression", 3, TreeNodes(c, line, parameters), TreeNode(cm, body, scope), TreeText(c, line, TreeType(cm, body)));
}

static FcExpr* TreeCall(FcComp* cm, FcLambdaExpr* lam) {
    FcCtx* c = CC_;
    int line = lam->h.line, i;
    FcTreeScope* scope = (FcTreeScope*)fcAlloc(c, sizeof *scope);
    FcExpr* body = ((FcReturnStmt*)lam->body->statements->items[0])->value;
    FcExpr* build;
    FcList* stmts = fcListNew(c);
    FcBlockStmt* block;
    FcLambdaExpr* builder;
    void* captures;
    FcList* undefinedArgs = fcListNew(c);
    FcList* attachArgs = fcListNew(c);
    FcCallExpr* inner;
    scope->params = TreeParamsOf(c, lam, NULL);
    build = TreeLambda(cm, lam, body, scope);
    fcListAdd(c, stmts, FC_NEW(c, FcReturnStmt, .h = { SK_ReturnStmt, lam->body->h.source, line }, .value = build));
    block = FC_NEW(c, FcBlockStmt, .h = { SK_BlockStmt, lam->body->h.source, line }, .statements = stmts);
    builder = (FcLambdaExpr*)fcNodeDup(c, lam, sizeof *lam);
    builder->h.line = line; builder->onTarget = NULL; builder->body = block; builder->expressionBody = true; builder->lendCaptures = false;
    if (fcDictTry(cm->sh->refs, lam, &captures)) fcDictPut(cm->sh->refs, builder, captures);
    for (i = 0; i < lam->params->count; i++) fcListAdd(c, undefinedArgs, fcUndefinedLiteral(c, line));
    inner = FC_NEW(c, FcCallExpr, .h = { EK_CallExpr, line }, .callee = (FcExpr*)builder, .args = undefinedArgs);
    fcListAdd(c, attachArgs, lam);
    return (FcExpr*)FC_NEW(c, FcCallExpr, .h = { EK_CallExpr, line }, .callee = (FcExpr*)fcNewMember(c, line, (FcExpr*)inner, "__Attach"), .args = attachArgs);
}

/* MemberKind: (isField, isProperty) of `member` in the class `typeName` or its bases */
static void TreeMemberKind(FcComp* cm, const char* typeName, const char* member, bool* isField, bool* isProperty) {
    const char* angle = strchr(typeName, '<');
    const char* name = angle ? fcStrN(CC_, typeName, (size_t)(angle - typeName)) : typeName;
    int guard, i;
    FcClassDecl* decl;
    *isField = *isProperty = false;
    for (guard = 0; guard < 32 && cm->sh->rr->classes && (decl = (FcClassDecl*)fcDictGet(cm->sh->rr->classes, name)) != NULL; guard++) {
        for (i = 0; i < decl->members->count; i++) {
            FcStmt* m = (FcStmt*)decl->members->items[i];
            if (m->kind == SK_FieldDecl && streq0(((FcFieldDecl*)m)->name, member)) { *isField = true; return; }
            if (m->kind == SK_PropertyDecl && streq0(((FcPropertyDecl*)m)->name, member)) { *isProperty = true; return; }
        }
        if (!decl->baseRefs || decl->baseRefs->count == 0) break;
        name = ((FcTypeRef*)decl->baseRefs->items[0])->baseName;
    }
}

static FcExpr* TreeNode(FcComp* cm, FcExpr* e, FcTreeScope* scope) {
    FcCtx* c = CC_;
    int line = e->line, i;
    const char* type = TreeType(cm, e);
    FcList* args;
    if (!DependsOnParams(c, e, scope)) {
        FcExpr* value;
        if (e->kind == EK_LiteralExpr) return TreeNodeOf(c, line, "ConstantExpression", 2, e, TreeText(c, line, type));
        value = InScope(cm, e, scope);
        return TreeNodeOf(c, line, "CapturedExpression", 3, TreeText(c, line, TreeSourceText(cm, e)),
            (FcExpr*)FC_NEW(c, FcUnaryExpr, .h = { EK_UnaryExpr, line }, .op = UOP_DeepCopy, .operand = value), TreeText(c, line, type));
    }
    switch (e->kind) {
        case EK_IdentifierExpr:
            return TreeNodeOf(c, line, "ParameterExpression", 3, TreeText(c, line, ((FcIdentifierExpr*)e)->name),
                TreeInt(c, line, FC_VALI(fcDictGet(scope->params, ((FcIdentifierExpr*)e)->name)) - 1), TreeText(c, line, type));
        case EK_MemberExpr: {
            FcMemberExpr* m = (FcMemberExpr*)e;
            bool isField, isProperty;
            TreeMemberKind(cm, TreeType(cm, m->target), m->name, &isField, &isProperty);
            return TreeNodeOf(c, line, "MemberExpression", 5, TreeNode(cm, m->target, scope), TreeText(c, line, m->name), TreeText(c, line, type),
                TreeBool(c, line, isField), TreeBool(c, line, isProperty));
        }
        case EK_CallExpr: {
            FcCallExpr* call = (FcCallExpr*)e;
            args = fcListNew(c);
            if (call->callee->kind == EK_MemberExpr) {
                FcMemberExpr* callee = (FcMemberExpr*)call->callee;
                bool isStatic = false;
                FcResolvedRef* r;
                if (callee->target->kind == EK_IdentifierExpr && (r = (FcResolvedRef*)fcDictGet(cm->sh->refs, callee->target)) != NULL && r->kind != RR_Local && r->kind != RR_Global
                    && !fcDictHas(scope->params, ((FcIdentifierExpr*)callee->target)->name)) isStatic = true;
                for (i = 0; i < call->args->count; i++) fcListAdd(c, args, TreeNode(cm, (FcExpr*)call->args->items[i], scope));
                if (isStatic)
                    return TreeNodeOf(c, line, "CallExpression", 5, fcUndefinedLiteral(c, line), TreeText(c, line, callee->name), TreeText(c, line, ((FcIdentifierExpr*)callee->target)->name),
                        TreeNodes(c, line, args), TreeText(c, line, type));
                return TreeNodeOf(c, line, "CallExpression", 5, TreeNode(cm, callee->target, scope), TreeText(c, line, callee->name), TreeText(c, line, TreeType(cm, callee->target)),
                    TreeNodes(c, line, args), TreeText(c, line, type));
            }
            if (call->callee->kind == EK_IdentifierExpr) {
                for (i = 0; i < call->args->count; i++) fcListAdd(c, args, TreeNode(cm, (FcExpr*)call->args->items[i], scope));
                return TreeNodeOf(c, line, "CallExpression", 5, fcUndefinedLiteral(c, line), TreeText(c, line, ((FcIdentifierExpr*)call->callee)->name), TreeText(c, line, ""),
                    TreeNodes(c, line, args), TreeText(c, line, type));
            }
            break;
        }
        case EK_BinaryExpr:
            return TreeNodeOf(c, line, "BinaryExpression", 4, TreeText(c, line, TreeOpText(((FcBinaryExpr*)e)->op)), TreeNode(cm, ((FcBinaryExpr*)e)->left, scope),
                TreeNode(cm, ((FcBinaryExpr*)e)->right, scope), TreeText(c, line, type));
        case EK_UnaryExpr: {
            FcUnaryOp op = ((FcUnaryExpr*)e)->op;
            return TreeNodeOf(c, line, "UnaryExpression", 3, TreeText(c, line, op == UOP_LogicalNot ? "!" : op == UOP_Negate ? "-" : "~"), TreeNode(cm, ((FcUnaryExpr*)e)->operand, scope),
                TreeText(c, line, type));
        }
        case EK_ConditionalExpr:
            return TreeNodeOf(c, line, "ConditionalExpression", 4, TreeNode(cm, ((FcConditionalExpr*)e)->condition, scope), TreeNode(cm, ((FcConditionalExpr*)e)->then, scope),
                TreeNode(cm, ((FcConditionalExpr*)e)->otherwise, scope), TreeText(c, line, type));
        case EK_NewExpr:
            args = fcListNew(c);
            for (i = 0; i < ((FcNewExpr*)e)->args->count; i++) fcListAdd(c, args, TreeNode(cm, (FcExpr*)((FcNewExpr*)e)->args->items[i], scope));
            return TreeNodeOf(c, line, "NewExpression", 2, TreeNodes(c, line, args), TreeText(c, line, TypeRefText(c, ((FcNewExpr*)e)->classRef)));
        case EK_IsOfExpr:
            return TreeNodeOf(c, line, "IsTypeExpression", 2, TreeNode(cm, ((FcIsOfExpr*)e)->operand, scope), TreeText(c, line, TypeRefText(c, ((FcIsOfExpr*)e)->typeRef)));
        case EK_IndexExpr:
            return TreeNodeOf(c, line, "IndexExpression", 3, TreeNode(cm, ((FcIndexExpr*)e)->target, scope), TreeNode(cm, ((FcIndexExpr*)e)->index, scope), TreeText(c, line, type));
        case EK_TypeCoerceExpr:
            return TreeNodeOf(c, line, "ConvertExpression", 2, TreeNode(cm, ((FcTypeCoerceExpr*)e)->operand, scope), TreeText(c, line, type));
        case EK_LambdaExpr: {
            FcLambdaExpr* nested = (FcLambdaExpr*)e;
            if (nested->body->statements->count == 1 && ((FcStmt*)nested->body->statements->items[0])->kind == SK_ReturnStmt && ((FcReturnStmt*)nested->body->statements->items[0])->value) {
                FcTreeScope* inner = (FcTreeScope*)fcAlloc(c, sizeof *inner);
                inner->params = TreeParamsOf(c, nested, scope); inner->outer = scope; inner->nested = nested;
                return TreeLambda(cm, nested, ((FcReturnStmt*)nested->body->statements->items[0])->value, inner);
            }
            break;
        }
        case EK_ArrayLiteralExpr:
            args = fcListNew(c);
            for (i = 0; i < ((FcArrayLiteralExpr*)e)->elements->count; i++) fcListAdd(c, args, TreeNode(cm, (FcExpr*)((FcArrayLiteralExpr*)e)->elements->items[i], scope));
            return TreeNodeOf(c, line, "NewExpression", 2, TreeNodes(c, line, args), TreeText(c, line, type));
        default: break;
    }
    fcCompError(cm, line, fcFmt(c, "An expression tree cannot hold this expression (%s)", fcExprKindName(e->kind)));
}

static FcExpr* InScope(FcComp* cm, FcExpr* e, FcTreeScope* scope) {
    FcCtx* c = CC_;
    FcList* all;
    bool readsLocal = false;
    int i;
    if (scope->nested == NULL) return e;
    all = fcListNew(c);
    TreeWalk(c, e, all);
    for (i = 0; i < all->count && !readsLocal; i++) {
        FcExpr* x = (FcExpr*)all->items[i];
        FcResolvedRef* lr;
        if (x->kind == EK_IdentifierExpr && (lr = (FcResolvedRef*)fcDictGet(cm->sh->refs, x)) != NULL && lr->kind == RR_Local) readsLocal = true;
    }
    if (!readsLocal) return e;
    switch (e->kind) {
        case EK_IdentifierExpr: {
            FcResolvedRef* r = (FcResolvedRef*)fcDictGet(cm->sh->refs, e);
            FcResolvedRef* nr = (FcResolvedRef*)fcDictGet(cm->sh->refs, scope->nested);
            if (r && r->kind == RR_Local && r->depth == 0 && nr && nr->kind == RR_LambdaCaptures) {
                int index = r->slot - scope->nested->params->count;
                if (index >= 0 && index < nr->variables->count) {
                    FcExpr* load = nr->loads ? (FcExpr*)nr->loads->items[index] : NULL;
                    return InScope(cm, load ? load : (FcExpr*)nr->variables->items[index], scope->outer);
                }
            }
            break;
        }
        case EK_MemberExpr: return (FcExpr*)fcNewMember(c, e->line, InScope(cm, ((FcMemberExpr*)e)->target, scope), ((FcMemberExpr*)e)->name);
        case EK_IndexExpr:
            return (FcExpr*)FC_NEW(c, FcIndexExpr, .h = { EK_IndexExpr, e->line }, .target = InScope(cm, ((FcIndexExpr*)e)->target, scope), .index = InScope(cm, ((FcIndexExpr*)e)->index, scope));
        case EK_LiteralExpr: return e;
        default: break;
    }
    fcCompError(cm, e->line, "A nested lambda of an expression tree can capture variables and their members, nothing else that does not depend on a parameter");
}

/* Value.ToString of an int or float literal (the text of a captured expression) */
static const char* fcValueText(FcCtx* c, const FcValue* v) {
    const char* unit;
    if (v->kind == VK_Int) {
        unit = v->unitv && !fcUIsUnitless(v->unitv) ? fcUText(c, v->unitv) : NULL;
        return unit ? fcFmt(c, "%" PRId64 "%s", v->i, unit) : fcFmt(c, "%" PRId64, v->i);
    }
    if (v->kind == VK_Float) {
        unit = v->unitv && !fcUIsUnitless(v->unitv) ? fcUText(c, v->unitv) : NULL;
        return unit ? fcFmt(c, "%s%s", fcDoubleR(c, v->f), unit) : fcDoubleR(c, v->f);
    }
    if (v->kind == VK_Char) return v->s;
    return "";
}
