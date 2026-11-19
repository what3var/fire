/* native/forge/setterclaims.c - port of src/fire.Compiler/SetterClaims.cs (commit c7f13d97): the paths through a setter, which call claims `value`
 * on each - here what the type checker asks (Ambiguous, TC018); the claim sites (Sites) are for the compiler (Phase C step 4). */

#define SC_MaxPaths 4096
typedef struct FcScEvent { void* node; bool isAssign; int line; } FcScEvent;
typedef struct FcScPath { FcList* events; bool ended; } FcScPath;
typedef struct FcSc { FcCtx* c; FcDict locals; } FcSc;

static FcScPath* scPathNew(FcSc* s) { FcScPath* p = (FcScPath*)fcAlloc(s->c, sizeof *p); p->events = fcListNew(s->c); return p; }
static FcScPath* scClone(FcSc* s, FcScPath* p) { FcScPath* r = scPathNew(s); int i; for (i = 0; i < p->events->count; i++) fcListAdd(s->c, r->events, p->events->items[i]); r->ended = p->ended; return r; }
static void scAppend(FcSc* s, FcScPath* to, FcScPath* from) { int i; for (i = 0; i < from->events->count; i++) fcListAdd(s->c, to->events, from->events->items[i]); }
static bool scIsValue(FcExpr* e) { return e && e->kind == EK_IdentifierExpr && streq0(((FcIdentifierExpr*)e)->name, "value"); }
static void scEvent(FcSc* s, FcList* list, void* node, bool isAssign, int line) { FcScEvent* e = (FcScEvent*)fcAlloc(s->c, sizeof *e); e->node = node; e->isAssign = isAssign; e->line = line; fcListAdd(s->c, list, e); }

static void scEvents(FcSc* s, FcExpr* e, FcList* list) {
    int i;
    if (!e) return;
    switch (e->kind) {
        case EK_LambdaExpr: return;
        case EK_AssignExpr: {
            FcAssignExpr* a = (FcAssignExpr*)e;
            scEvents(s, a->target->kind == EK_MemberExpr ? ((FcMemberExpr*)a->target)->target : a->target->kind == EK_IndexExpr ? ((FcIndexExpr*)a->target)->target : NULL, list);
            if (a->target->kind == EK_IndexExpr) scEvents(s, ((FcIndexExpr*)a->target)->index, list);
            scEvents(s, a->value, list);
            if (scIsValue(a->value) && (a->target->kind == EK_MemberExpr || (a->target->kind == EK_IdentifierExpr && !streq0(((FcIdentifierExpr*)a->target)->name, "value")
                && !fcDictHas(&s->locals, ((FcIdentifierExpr*)a->target)->name))))
                scEvent(s, list, a, true, a->h.line);
            return;
        }
        case EK_CallExpr: {
            FcCallExpr* c = (FcCallExpr*)e;
            scEvents(s, c->callee, list);
            for (i = 0; i < c->args->count; i++) scEvents(s, (FcExpr*)c->args->items[i], list);
            for (i = 0; i < c->args->count; i++) if (scIsValue((FcExpr*)c->args->items[i])) scEvent(s, list, c->args->items[i], false, c->h.line);
            return;
        }
        case EK_NewExpr: {
            FcNewExpr* n = (FcNewExpr*)e;
            for (i = 0; i < n->args->count; i++) scEvents(s, (FcExpr*)n->args->items[i], list);
            for (i = 0; i < n->args->count; i++) if (scIsValue((FcExpr*)n->args->items[i])) scEvent(s, list, n->args->items[i], false, n->h.line);
            return;
        }
        case EK_MemberExpr: scEvents(s, ((FcMemberExpr*)e)->target, list); return;
        case EK_IndexExpr: scEvents(s, ((FcIndexExpr*)e)->target, list); scEvents(s, ((FcIndexExpr*)e)->index, list); return;
        case EK_UnaryExpr: scEvents(s, ((FcUnaryExpr*)e)->operand, list); return;
        case EK_BinaryExpr: scEvents(s, ((FcBinaryExpr*)e)->left, list); scEvents(s, ((FcBinaryExpr*)e)->right, list); return;
        case EK_ConditionalExpr: scEvents(s, ((FcConditionalExpr*)e)->condition, list); scEvents(s, ((FcConditionalExpr*)e)->then, list); scEvents(s, ((FcConditionalExpr*)e)->otherwise, list); return;
        case EK_ArrayLiteralExpr: for (i = 0; i < ((FcArrayLiteralExpr*)e)->elements->count; i++) scEvents(s, (FcExpr*)((FcArrayLiteralExpr*)e)->elements->items[i], list); return;
        case EK_TypeCoerceExpr: scEvents(s, ((FcTypeCoerceExpr*)e)->operand, list); return;
        case EK_UnitCoerceExpr: scEvents(s, ((FcUnitCoerceExpr*)e)->operand, list); return;
        case EK_IsOfExpr: scEvents(s, ((FcIsOfExpr*)e)->operand, list); return;
        case EK_TryCallExpr: scEvents(s, ((FcTryCallExpr*)e)->call, list); return;
        case EK_InterpolatedStringExpr: {
            FcList* parts = ((FcInterpolatedStringExpr*)e)->parts;
            for (i = 0; i < parts->count; i++) if (((FcInterpolationPart*)parts->items[i])->isExpression) scEvents(s, ((FcInterpolationPart*)parts->items[i])->expression, list);
            return;
        }
        default: return;
    }
}

static FcList* scPathsOfList(FcSc* s, FcList* statements);
static FcScPath* scOf(FcSc* s, FcExpr* e) { FcScPath* p = scPathNew(s); scEvents(s, e, p->events); return p; }
static FcList* scJoin(FcSc* s, FcScPath* head, FcList* tails) {
    FcList* r;
    int i;
    if (!tails) return NULL;
    r = fcListNew(s->c);
    for (i = 0; i < tails->count; i++) { FcScPath* tl = (FcScPath*)tails->items[i]; FcScPath* c = scClone(s, head); scAppend(s, c, tl); c->ended = tl->ended; fcListAdd(s->c, r, c); }
    return r;
}
static FcList* scSingle(FcSc* s, FcScPath* p) { FcList* l = fcListNew(s->c); fcListAdd(s->c, l, p); return l; }
static FcList* scPathsOf(FcSc* s, FcStmt* stmt) {
    int i, k;
    switch (stmt->kind) {
        case SK_BlockStmt: return scPathsOfList(s, ((FcBlockStmt*)stmt)->statements);
        case SK_ExprStmt: return scSingle(s, scOf(s, ((FcExprStmt*)stmt)->expression));
        case SK_VarDeclStmt: return scSingle(s, scOf(s, ((FcVarDeclStmt*)stmt)->initializer));
        case SK_DeleteStmt: return scSingle(s, scOf(s, ((FcDeleteStmt*)stmt)->target));
        case SK_ReturnStmt: { FcScPath* p = scOf(s, ((FcReturnStmt*)stmt)->value); p->ended = true; return scSingle(s, p); }
        case SK_ThrowStmt: { FcScPath* p = scOf(s, ((FcThrowStmt*)stmt)->value); p->ended = true; return scSingle(s, p); }
        case SK_IfStmt: {
            FcIfStmt* is = (FcIfStmt*)stmt;
            FcScPath* head = scOf(s, is->condition);
            FcList* then = scJoin(s, head, scPathsOf(s, is->then));
            FcList* other = is->otherwise ? scJoin(s, head, scPathsOf(s, is->otherwise)) : scSingle(s, head);
            if (!then || !other) return NULL;
            for (i = 0; i < other->count; i++) fcListAdd(s->c, then, other->items[i]);
            return then;
        }
        case SK_WhileStmt: {
            FcWhileStmt* w = (FcWhileStmt*)stmt;
            FcScPath* head = scOf(s, w->condition);
            FcList* body = scJoin(s, head, scPathsOf(s, w->body));
            if (!body) return NULL;
            for (i = 0; i < body->count; i++) { FcScPath* p = (FcScPath*)body->items[i]; if (!p->ended) scEvents(s, w->condition, p->events); }
            fcListAdd(s->c, body, head);
            return body;
        }
        case SK_DoWhileStmt: {
            FcDoWhileStmt* dw = (FcDoWhileStmt*)stmt;
            FcList* body = scPathsOf(s, dw->body);
            if (!body) return NULL;
            for (i = 0; i < body->count; i++) { FcScPath* p = (FcScPath*)body->items[i]; if (!p->ended) scEvents(s, dw->condition, p->events); }
            return body;
        }
        case SK_ForStmt: {
            FcForStmt* f = (FcForStmt*)stmt;
            FcList* init = f->init ? scPathsOf(s, f->init) : scSingle(s, scPathNew(s));
            FcList* result;
            if (!init) return NULL;
            result = fcListNew(s->c);
            for (i = 0; i < init->count; i++) {
                FcScPath* head = scClone(s, (FcScPath*)init->items[i]);
                FcList* body;
                scEvents(s, f->condition, head->events);
                body = scJoin(s, head, scPathsOf(s, f->body));
                if (!body) return NULL;
                for (k = 0; k < body->count; k++) { FcScPath* p = (FcScPath*)body->items[k]; if (!p->ended) scEvents(s, f->increment, p->events); }
                for (k = 0; k < body->count; k++) fcListAdd(s->c, result, body->items[k]);
                fcListAdd(s->c, result, head);
            }
            return result;
        }
        case SK_ForeachStmt: {
            FcForeachStmt* fe = (FcForeachStmt*)stmt;
            FcScPath* head = scOf(s, fe->iterable);
            FcList* body = scJoin(s, head, scPathsOf(s, fe->body));
            if (!body) return NULL;
            fcListAdd(s->c, body, head);
            return body;
        }
        case SK_TryStmt: {
            FcTryStmt* ts = (FcTryStmt*)stmt;
            FcList* paths = scPathsOf(s, (FcStmt*)ts->tryBlock);
            if (!paths) return NULL;
            for (i = 0; i < ts->catches->count; i++) {
                FcList* cp = scPathsOf(s, (FcStmt*)((FcCatchClause*)ts->catches->items[i])->body);
                if (!cp) return NULL;
                for (k = 0; k < cp->count; k++) fcListAdd(s->c, paths, cp->items[k]);
            }
            if (ts->finallyBlock) {
                FcList* fin = scPathsOf(s, (FcStmt*)ts->finallyBlock);
                FcList* np;
                if (!fin) return NULL;
                np = fcListNew(s->c);
                for (i = 0; i < paths->count; i++)
                    for (k = 0; k < fin->count; k++) {
                        FcScPath* p = (FcScPath*)paths->items[i];
                        FcScPath* f = (FcScPath*)fin->items[k];
                        FcScPath* c = scClone(s, p);
                        scAppend(s, c, f);
                        c->ended = p->ended || f->ended;
                        fcListAdd(s->c, np, c);
                    }
                paths = np;
            }
            return paths;
        }
        default: return scSingle(s, scPathNew(s));
    }
}
static FcList* scPathsOfList(FcSc* s, FcList* statements) {
    FcList* paths = scSingle(s, scPathNew(s));
    int i, k, j;
    for (i = 0; i < statements->count; i++) {
        FcList* next = fcListNew(s->c);
        for (k = 0; k < paths->count; k++) {
            FcScPath* p = (FcScPath*)paths->items[k];
            FcList* tails;
            if (p->ended) { fcListAdd(s->c, next, p); continue; }
            tails = scPathsOf(s, (FcStmt*)statements->items[i]);
            if (!tails) return NULL;
            for (j = 0; j < tails->count; j++) {
                FcScPath* tl = (FcScPath*)tails->items[j];
                FcScPath* c = scClone(s, p);
                scAppend(s, c, tl);
                c->ended = tl->ended;
                fcListAdd(s->c, next, c);
            }
            if (next->count > SC_MaxPaths) return NULL;
        }
        paths = next;
    }
    return paths;
}
static void scCollectLocals(FcSc* s, FcStmt* stmt) {
    int i;
    switch (stmt->kind) {
        case SK_BlockStmt: for (i = 0; i < ((FcBlockStmt*)stmt)->statements->count; i++) scCollectLocals(s, (FcStmt*)((FcBlockStmt*)stmt)->statements->items[i]); break;
        case SK_VarDeclStmt: fcSetAdd(&s->locals, ((FcVarDeclStmt*)stmt)->name); break;
        case SK_IfStmt: scCollectLocals(s, ((FcIfStmt*)stmt)->then); if (((FcIfStmt*)stmt)->otherwise) scCollectLocals(s, ((FcIfStmt*)stmt)->otherwise); break;
        case SK_WhileStmt: scCollectLocals(s, ((FcWhileStmt*)stmt)->body); break;
        case SK_DoWhileStmt: scCollectLocals(s, ((FcDoWhileStmt*)stmt)->body); break;
        case SK_ForStmt: if (((FcForStmt*)stmt)->init) scCollectLocals(s, ((FcForStmt*)stmt)->init); scCollectLocals(s, ((FcForStmt*)stmt)->body); break;
        case SK_ForeachStmt: fcSetAdd(&s->locals, ((FcForeachStmt*)stmt)->varName); scCollectLocals(s, ((FcForeachStmt*)stmt)->body); break;
        case SK_TryStmt: {
            FcTryStmt* ts = (FcTryStmt*)stmt;
            scCollectLocals(s, (FcStmt*)ts->tryBlock);
            for (i = 0; i < ts->catches->count; i++) { fcSetAdd(&s->locals, ((FcCatchClause*)ts->catches->items[i])->varName); scCollectLocals(s, (FcStmt*)((FcCatchClause*)ts->catches->items[i])->body); }
            if (ts->finallyBlock) scCollectLocals(s, (FcStmt*)ts->finallyBlock);
            break;
        }
        default: break;
    }
}
/* the record equality of an Event (Distinct): the node by its value (an IdentifierExpr `value`: its line), IsAssign, Line */
static bool scEventEq(const FcScEvent* a, const FcScEvent* b) {
    if (a->isAssign != b->isAssign || a->line != b->line) return false;
    if (a->node == b->node) return true;
    if (a->isAssign) return false;
    return ((FcExpr*)a->node)->line == ((FcExpr*)b->node)->line;
}
typedef struct FcScCall { bool always, sometimes; int line; } FcScCall;
/* SetterClaims.Analyze(body).Ambiguous */
static FcList* SetterClaimsAmbiguous(FcCtx* c, FcBlockStmt* body) {
    FcSc s;
    FcList* paths;
    FcDict calls;
    FcList* ambiguous = fcListNew(c);
    int i, k, j;
    s.c = c;
    fcDictInit(c, &s.locals, FC_KEY_STR);
    scCollectLocals(&s, (FcStmt*)body);
    paths = scPathsOfList(&s, body->statements);
    if (paths == NULL) return ambiguous;
    fcDictInit(c, &calls, FC_KEY_PTR);
    for (i = 0; i < paths->count; i++) {
        FcList* events = ((FcScPath*)paths->items[i])->events;
        bool hasAssign = false;
        FcScEvent* firstCall = NULL;
        FcList* distinct = fcListNew(c);
        for (k = 0; k < events->count; k++) if (((FcScEvent*)events->items[k])->isAssign) hasAssign = true;
        if (!hasAssign) for (k = 0; k < events->count && !firstCall; k++) if (!((FcScEvent*)events->items[k])->isAssign) firstCall = (FcScEvent*)events->items[k];
        for (k = 0; k < events->count; k++) {
            FcScEvent* e = (FcScEvent*)events->items[k];
            bool dup = false;
            if (e->isAssign) continue;
            for (j = 0; j < distinct->count && !dup; j++) if (scEventEq((FcScEvent*)distinct->items[j], e)) dup = true;
            if (!dup) fcListAdd(c, distinct, e);
        }
        for (k = 0; k < distinct->count; k++) {
            FcScEvent* e = (FcScEvent*)distinct->items[k];
            bool claims = e == firstCall;
            FcScCall* cc = (FcScCall*)fcDictGet(&calls, e->node);
            if (cc) { cc->always = cc->always && claims; cc->sometimes = cc->sometimes || claims; cc->line = e->line; }
            else { cc = (FcScCall*)fcAlloc(c, sizeof *cc); cc->always = claims; cc->sometimes = claims; cc->line = e->line; fcDictPut(&calls, e->node, cc); }
        }
    }
    FC_DICT_EACH(&calls, e) {
        FcScCall* cc = (FcScCall*)e->value;
        if (!cc->always && cc->sometimes) fcListAdd(c, ambiguous, FC_IVAL(cc->line));
    }
    return ambiguous;
}
