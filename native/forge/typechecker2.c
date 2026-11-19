/* native/forge/typechecker2.c - TypeChecker.cs, second part: statements, classes, `override`, members, expressions (included by typechecker1.c). */

static void Check(FcTC* t, FcStmt* stmt);
static void CheckScoped(FcTC* t, FcStmt* stmt);
static void CheckVarDecl(FcTC* t, FcVarDeclStmt* v);
static void CheckClass(FcTC* t, FcClassDecl* decl);
static void CheckMembers(FcTC* t, FcList* members, const char* ownerName);
static void CheckParametersTyped(FcTC* t, FcList* parameters, const char* owner, int line);
static void ReportNoReturnType(FcTC* t, const char* method, int line);
static const FcST* InferLambda(FcTC* t, FcLambdaExpr* lam, const FcST* expected);
static void CheckSwitchOverEnum(FcTC* t, const char* enumName, FcList* caseValues, int line);
static const FcST* Join(FcTC* t, FcList* types, FcExpr* site);
static bool IsLocal(FcTC* t, const char* name);
static void CheckConstructorAccess(FcTC* t, const char* className, int argc, int line);
static const char* BaseClassName(FcTC* t);
static void CheckRecordDecl(FcTC* t, FcClassDecl* decl);
static void CheckOverrides(FcTC* t, FcClassDecl* decl);
static FcList* SetterClaimsAmbiguous(FcCtx* c, FcBlockStmt* body);

static void Check(FcTC* t, FcStmt* stmt) {
    int i, k;
    switch (stmt->kind) {
        case SK_BlockStmt: {
            FcBlockStmt* block = (FcBlockStmt*)stmt;
            Push(t);
            for (i = 0; i < block->statements->count; i++) Check(t, (FcStmt*)block->statements->items[i]);
            Pop(t);
            break;
        }
        case SK_ExprStmt: Infer(t, ((FcExprStmt*)stmt)->expression); break;
        case SK_VarDeclStmt: CheckVarDecl(t, (FcVarDeclStmt*)stmt); break;
        case SK_IfStmt: {
            FcIfStmt* s = (FcIfStmt*)stmt;
            CheckCondition(t, s->condition);
            CheckScoped(t, s->then);
            if (s->otherwise) CheckScoped(t, s->otherwise);
            break;
        }
        case SK_WhileStmt: CheckCondition(t, ((FcWhileStmt*)stmt)->condition); CheckScoped(t, ((FcWhileStmt*)stmt)->body); break;
        case SK_DoWhileStmt: CheckScoped(t, ((FcDoWhileStmt*)stmt)->body); CheckCondition(t, ((FcDoWhileStmt*)stmt)->condition); break;
        case SK_ForStmt: {
            FcForStmt* f = (FcForStmt*)stmt;
            Push(t);
            if (f->init) Check(t, f->init);
            if (f->condition) CheckCondition(t, f->condition);
            if (f->increment) Infer(t, f->increment);
            CheckScoped(t, f->body);
            Pop(t);
            break;
        }
        case SK_ForeachStmt: {
            FcForeachStmt* fe = (FcForeachStmt*)stmt;
            const FcST* iterable = Infer(t, fe->iterable);
            Push(t);
            Declare(t, fe->varName, ElementOf(t, iterable));
            CheckScoped(t, fe->body);
            Pop(t);
            break;
        }
        case SK_ReturnStmt: {
            FcReturnStmt* r = (FcReturnStmt*)stmt;
            if (r->value) {
                const FcST* type = InferExpected(t, r->value, t->returnType);
                if (t->collectedReturns) fcListAdd(TC_, t->collectedReturns, (void*)type);
                if (t->returnType) {
                    CheckAssignable(t, type, t->returnType, r->h.line, "the return value", TC_ReturnCode, r->value);
                    if (t->returnTypeRef) GuardValue(t, r->value, type, t->returnType, t->returnTypeRef, t->returnWhat);
                }
            } else if (t->collectedReturns) fcListAdd(TC_, t->collectedReturns, (void*)ST_UNDEFINED);
            break;
        }
        case SK_ThrowStmt: Infer(t, ((FcThrowStmt*)stmt)->value); break;
        case SK_TryStmt: {
            FcTryStmt* ts = (FcTryStmt*)stmt;
            Check(t, (FcStmt*)ts->tryBlock);
            for (i = 0; i < ts->catches->count; i++) {
                FcCatchClause* cc = (FcCatchClause*)ts->catches->items[i];
                Push(t);
                Declare(t, cc->varName, DeclaredOrDynamic(t, cc->typeRef, 0, NULL));
                Check(t, (FcStmt*)cc->body);
                Pop(t);
            }
            if (ts->finallyBlock) Check(t, (FcStmt*)ts->finallyBlock);
            break;
        }
        case SK_UnsafeStmt: Check(t, (FcStmt*)((FcUnsafeStmt*)stmt)->body); break;
        case SK_FireStmt: {
            FcFireStmt* f = (FcFireStmt*)stmt;
            FcList* captured = fcListNew(TC_);
            FcDict* implicitCopies = NULL;
            FcList* withTypes = fcListNew(TC_);
            FcDict* outerCopies;
            if (f->until) for (i = 0; i < f->until->count; i++) Infer(t, (FcExpr*)f->until->items[i]);
            for (i = 0; i < f->takingCaptures->count; i++) fcListAdd(TC_, captured, (void*)Infer(t, ((FcFireTakingCapture*)f->takingCaptures->items[i])->source));
            for (i = 0; i < f->takingCaptures->count; i++) {
                FcFireTakingCapture* tc = (FcFireTakingCapture*)f->takingCaptures->items[i];
                if (!tc->isImplicit) continue;
                if (!implicitCopies) implicitCopies = fcDictNew(TC_, FC_KEY_STR);
                fcSetAdd(implicitCopies, tc->varName);
                Report(t, TC_ImplicitCaptureCode, TF(t, "The thread uses the local '%s' of the code around it: it gets a copy of it - a change does not reach the variable outside. Write 'taking %s' where it is used (a copy) or 'handover %s' (moved to the thread).",
                    tc->varName, tc->varName, tc->varName), f->h.line);
            }
            for (i = 0; i < f->with->count; i++) fcListAdd(TC_, withTypes, (void*)Infer(t, ((FcFireWith*)f->with->items[i])->source));
            Push(t);
            for (i = 0; i < f->takingCaptures->count; i++) Declare(t, ((FcFireTakingCapture*)f->takingCaptures->items[i])->varName, (const FcST*)captured->items[i]);
            for (i = 0; i < f->with->count; i++) Declare(t, ((FcFireWith*)f->with->items[i])->varName, (const FcST*)withTypes->items[i]);
            outerCopies = t->implicitCopies;
            t->implicitCopies = implicitCopies;
            for (i = 0; i < f->body->statements->count; i++) Check(t, (FcStmt*)f->body->statements->items[i]);
            t->implicitCopies = outerCopies;
            Pop(t);
            break;
        }
        case SK_ProcessStmt: Infer(t, ((FcProcessStmt*)stmt)->target); break;
        case SK_SilenceStmt: Infer(t, ((FcSilenceStmt*)stmt)->target); break;
        case SK_DeleteStmt: Infer(t, ((FcDeleteStmt*)stmt)->target); break;
        case SK_TerminateStmt: if (((FcTerminateStmt*)stmt)->value) Infer(t, ((FcTerminateStmt*)stmt)->value); break;
        case SK_PostGlobalStmt: {
            FcPostGlobalStmt* pg = (FcPostGlobalStmt*)stmt;
            FcList* argTypes = fcListNew(TC_);
            for (i = 0; i < pg->args->count; i++) fcListAdd(TC_, argTypes, (void*)Infer(t, (FcExpr*)pg->args->items[i]));
            InferLambda(t, (FcLambdaExpr*)pg->lambda, stLambdaOf(TC_, argTypes->count, NULL, argTypes));
            break;
        }
        case SK_CatchThreadsDecl: {
            FcCatchThreadsDecl* ct = (FcCatchThreadsDecl*)stmt;
            Push(t);
            if (ct->varName) Declare(t, ct->varName, DeclaredOrDynamic(t, ct->typeRef, 0, NULL));
            Check(t, (FcStmt*)ct->body);
            Pop(t);
            break;
        }
        case SK_CatchTerminateDecl: {
            FcCatchTerminateDecl* cte = (FcCatchTerminateDecl*)stmt;
            Push(t);
            if (cte->varName) Declare(t, cte->varName, ST_DYNAMIC);
            Check(t, (FcStmt*)cte->body);
            Pop(t);
            break;
        }
        case SK_InterfaceDecl: {
            FcInterfaceDecl* id = (FcInterfaceDecl*)stmt;
            for (k = 0; k < id->methods->count; k++) {
                FcInterfaceMethodSig* sig = (FcInterfaceMethodSig*)id->methods->items[k];
                CheckParametersTyped(t, sig->params, sig->name, sig->line);
                if (sig->returnType == NULL || fcTrIsInferred(sig->returnType)) ReportNoReturnType(t, sig->name, sig->line);
            }
            break;
        }
        case SK_ClassExtensionDecl: {
            FcClassExtensionDecl* ext = (FcClassExtensionDecl*)stmt;
            const FcST* previousThis = t->thisType;
            t->thisType = streq0(ext->targetRef->baseName, "array") || streq0(ext->targetRef->baseName, "$array") ? stArrayOf(TC_, ST_DYNAMIC) : DeclaredOrDynamic(t, ext->targetRef, 0, NULL);
            CheckMembers(t, ext->members, ext->targetRef->baseName);
            t->thisType = previousThis;
            break;
        }
        case SK_NamespaceDecl: {
            FcNamespaceDecl* n = (FcNamespaceDecl*)stmt;
            for (i = 0; i < n->members->count; i++) Check(t, (FcStmt*)n->members->items[i]);
            break;
        }
        case SK_ClassDecl: CheckClass(t, (FcClassDecl*)stmt); break;
        default: break;
    }
}

static void CheckScoped(FcTC* t, FcStmt* stmt) { Push(t); Check(t, stmt); Pop(t); }

static FcNull fcInitNull(bool noInit, FcNull initNull) {
    if (noInit) return NUL_MayBeUndefined;
    return initNull == NUL_Defined || initNull == NUL_Inferred ? NUL_Inferred : initNull;
}

static void CheckVarDecl(FcTC* t, FcVarDeclStmt* v) {
    const FcST* declared = Declared(t, v->type, v->arrayRanks->count, NULL);
    const FcST* init = v->initializer ? InferExpected(t, v->initializer, declared) : ST_DYNAMIC;
    if (init->kind == STK_Enum && v->switchValues) CheckSwitchOverEnum(t, init->cls, v->switchValues, v->h.line);
    if (declared) {
        const FcST* target = declared;
        CheckRawUnit(t, v->type, v->name, v->h.line);
        if (v->initializer) {
            target = WithInitializerUnit(t, target, v->type, v->arrayRanks->count, init);
            CheckAssignable(t, init, target, v->h.line, TF(t, "'%s'", v->name), TC_AssignCode, v->initializer);
            if (v->arrayRanks->count == 0 && v->type != NULL) GuardLocalValue(t, v->initializer, init, target, v->type, v->name);
        }
        if (stIsValueKind(target) && target->nul != NUL_Defined && v->arrayRanks->count == 0)
            target = stWithNull(TC_, target, fcInitNull(v->initializer == NULL, init->nul));
        else if (HoldsUndefinedAsObject(target) && target->nul != NUL_Defined && v->arrayRanks->count == 0)
            target = stWithNull(TC_, target, fcInitNull(v->initializer == NULL, init->nul));
        Declare(t, v->name, target);
        if (v->arrayRanks->count == 0 && v->type && v->type->arrayRank == 0 && v->type->pointerDepth == 0) DeclareLocalType(t, v->name, v->type, -1);
    } else {
        const FcST* type = init->kind == STK_Undefined ? ST_DYNAMIC : init;
        bool pending = false;
        const char* mixed;
        if (stIsValueKind(type) && type->nul == NUL_Defined) type = stWithNull(TC_, type, NUL_Inferred);
        if (init->kind == STK_Undefined) pending = true;
        else if (stIsDynamic(init) && v->initializer != NULL) {
            if ((mixed = (const char*)fcDictGet(&t->mixedJoins, v->initializer)) != NULL)
                Report(t, TC_LateVarTypeCode, TF(t, "The values of '%s' have unrelated types (%s): it becomes 'any' - nothing about it is checked and it takes the wide values of the native backend; give it one type, or write 'any %s = ...' if that is meant", v->name, mixed, v->name), v->h.line);
            else if (init->declaredAny)
                Report(t, TC_LateVarTypeCode, TF(t, "'%s' becomes 'any' (its initializer is declared 'any'): nothing about it is checked and it takes the wide values of the native backend - write 'any %s = ...' if that is meant, or give it its type", v->name, v->name), v->h.line);
            else if (init->typeParam == NULL) ReportUntypedVar(t, v->name, v->h.line, "the type of its initializer is not known here");
        } else if (init->kind == STK_Array && init->element && stIsDynamic(init->element) && v->initializer && v->initializer->kind == EK_ArrayLiteralExpr && ((FcArrayLiteralExpr*)v->initializer)->elements->count == 0) {
            Report(t, TC_LateVarTypeCode, TF(t, "'%s' is an empty array literal: its elements have no type, it becomes an 'any[]' - every element is wide and checked at run time; declare the element type ('int %s[] = []'), or write 'any %s[] = []' if that is meant", v->name, v->name, v->name), v->h.line);
        } else if (init->kind == STK_Array && init->element && stIsDynamic(init->element) && v->initializer && (mixed = (const char*)fcDictGet(&t->mixedJoins, v->initializer)) != NULL) {
            Report(t, TC_LateVarTypeCode, TF(t, "The elements of '%s' have unrelated types (%s): it becomes an 'any[]' - every element is wide and checked at run time; give them one type, or write 'any %s[] = ...' if that is meant", v->name, mixed, v->name), v->h.line);
        }
        if (stIsNumber(type)) {
            if (v->type && v->type->unit && !streq0(v->type->unit, "any")) {
                const char* unitName = v->type->unit;
                const FcST* demanded = fcStartsWith(unitName, "any ") ? stWithUnit(TC_, type, fcUParse(TC_, unitName + 4), true) : stWithUnit(TC_, type, fcUParse(TC_, unitName), false);
                if (v->initializer) CheckUnitAssignable(t, init, demanded, v->h.line, TF(t, "'%s'", v->name), NULL);
                type = demanded;
            } else type = stWithUnit(TC_, type, NULL, false);
        }
        Declare(t, v->name, type);
        if (type->kind == STK_Float && v->initializer && v->initializer->kind == EK_ConditionalExpr) fcDictPut(t->rr->unitChecks, v->initializer, (void*)"#any");
        if (pending) {
            FcDict* scope = (FcDict*)t->scopes->items[t->scopes->count - 1];
            FcPendingVar* pv = (FcPendingVar*)fcAlloc(TC_, sizeof *pv);
            pv->scope = scope; pv->name = v->name; pv->line = v->h.line; pv->source = t->source;
            fcDictPut(&t->pendingVars, fcPendingKey(t, scope, v->name), pv);
        } else if (v->arrayRanks->count == 0) {
            FcTypeRef* guardType = GuardTypeOf(t, type);
            if (guardType) DeclareLocalType(t, v->name, guardType, -1);
        }
    }
}

static void TypePendingVar(FcTC* t, FcIdentifierExpr* id, const FcST* value) {
    int i;
    for (i = t->scopes->count - 1; i >= 0; i--) {
        FcDict* scope = (FcDict*)t->scopes->items[i];
        const char* key;
        FcPendingVar* declared;
        const FcST* type;
        if (!fcDictHas(scope, id->name)) continue;
        key = fcPendingKey(t, scope, id->name);
        if (!(declared = (FcPendingVar*)fcDictGet(&t->pendingVars, key))) return;
        fcDictRemove(&t->pendingVars, key);
        if (stIsDynamic(value) && value->declaredAny) {
            Report(t, TC_LateVarTypeCode, TF(t, "'%s' becomes 'any' by its first assignment (a value declared 'any'), not by its declaration (line %d): declare it 'any %s' if that is meant, or give it its type", id->name, declared->line, id->name), id->h.line);
            return;
        }
        if ((stIsDynamic(value) && value->typeParam == NULL) || value->kind == STK_Undefined) {
            ReportUntypedVar(t, id->name, id->h.line, TF(t, "'var %s = undefined' (line %d) says nothing about its type, and its first assignment is of a type not known here", id->name, declared->line));
            return;
        }
        type = stIsNumber(value) ? stWithUnit(TC_, value, NULL, false) : value;
        fcDictPut(scope, id->name, (void*)type);
        { FcTypeRef* guardType = GuardTypeOf(t, type); if (guardType) DeclareLocalType(t, id->name, guardType, i); }
        Report(t, TC_LateVarTypeCode, TF(t, "The type of '%s' (%s) comes from its first assignment, not from its declaration (line %d): declare it with the type ('%s %s')", id->name, S_(t, type), declared->line, S_(t, type), id->name), id->h.line);
        return;
    }
}

static void CheckClass(FcTC* t, FcClassDecl* decl) {
    FcClassDecl* previousClass = t->cls;
    bool hasCtor = false;
    const char* baseClass;
    int i;
    t->cls = decl;
    for (i = 0; i < decl->members->count; i++) if (((FcStmt*)decl->members->items[i])->kind == SK_ConstructorDecl) hasCtor = true;
    if (!hasCtor && (baseClass = BaseClassName(t)) != NULL) CheckConstructorAccess(t, baseClass, 0, decl->h.line);
    CheckOverrides(t, decl);
    if (decl->isRecord) CheckRecordDecl(t, decl);
    CheckMembers(t, decl->members, decl->name);
    t->cls = previousClass;
}

/* ---- `override` ---- */
static FcList* BaseClassesOf(FcTC* t, FcClassDecl* decl) {
    FcList* out = fcListNew(TC_);
    FcClassDecl* current = decl;
    int guard;
    for (guard = 0; guard < 64; guard++) {
        FcTypeRef* baseRef = fcFirstClassBase(t, current);
        FcClassDecl* next;
        if (baseRef == NULL || !(next = (FcClassDecl*)fcDictGet(t->classes, NameOf(t, baseRef))) || next == decl) break;
        fcListAdd(TC_, out, next);
        current = next;
    }
    return out;
}
static const char* OverloadDescriptor(FcTC* t, FcLambdaParam* p, FcClassDecl* owner, FcList* methodTypeParams) {
    FcTypeRef* ty = p->type;
    const char* name;
    if (p->byRef) return "";
    if (p->arrayRanks->count > 0 || (ty && ty->arrayRank > 0)) return "k:array";
    if (ty == NULL || fcTrIsInferred(ty) || ty->pointerDepth > 0) return "";
    if (ty->lambdaSignature && ty->lambdaSignature->isExpression) return "c:LambdaExpression";
    if (ty->lambdaSignature || streq0(ty->baseName, "lambda")) return "k:lambda";
    if (fcHasTypeParamNamed(methodTypeParams, ty->baseName) || fcHasTypeParamNamed(owner->typeParams, ty->baseName)) return "g";
    if (streq0(ty->baseName, "int")) return "k:int";
    if (streq0(ty->baseName, "float")) return "k:float";
    if (streq0(ty->baseName, "bool")) return "k:bool";
    if (streq0(ty->baseName, "char")) return "k:char";
    if (streq0(ty->baseName, "string")) return "k:string";
    if (streq0(ty->baseName, "class")) return "k:class";
    if (streq0(ty->baseName, "word")) return "k:word";
    if (streq0(ty->baseName, "any")) return "";
    if (fcDictHas(&t->enumNames, ty->baseName)) return "k:int";
    {
        FcKnown kn;
        kn.fn = kTcKnown; kn.self = t;
        name = ty->typeArgs && ty->typeArgs->count > 0 ? fcResolveNewTarget(TC_, ty, ty->typeArgs->count, kn) : fcTrResolveBaseName(TC_, ty, kn);
    }
    if (fcDictHas(&t->enumNames, name)) return "k:int";
    if (fcDictHas(&t->interfaces, name)) return fcFmt(TC_, "i:%s", name);
    if (fcDictHas(t->classes, name)) return fcFmt(TC_, "c:%s", name);
    return "";
}
static bool Replaces(FcTC* t, FcMethodDecl* m, FcClassDecl* owner, FcMethodDecl* baseMethod, FcClassDecl* baseOwner) {
    int i;
    if (baseMethod->isStatic || !streq0(baseMethod->name, m->name) || baseMethod->params->count != m->params->count) return false;
    for (i = 0; i < m->params->count; i++) {
        const char* a = OverloadDescriptor(t, (FcLambdaParam*)m->params->items[i], owner, m->typeParams);
        const char* b = OverloadDescriptor(t, (FcLambdaParam*)baseMethod->params->items[i], baseOwner, baseMethod->typeParams);
        if (streq0(a, b) || !a[0] || a[0] == 'g' || !b[0] || b[0] == 'g') continue;
        return false;
    }
    return true;
}
static const char* OperatorText(FcTC* t, const char* name) {
    (void)t;
    if (streq0(name, "GetIndex") || streq0(name, "SetIndex")) return "[]";
    return fcStartsWith(name, "operator") ? name + 8 : name;
}
static const char* AccessText(FcAccess a) { return a == ACC_Private ? "private" : a == ACC_Protected ? "protected" : "public"; }
static void TypeTextB(FcTC* t, FcBuf* b, const FcTypeRef* ty) {
    int i;
    fcBufStr(b, PlainName(t, ty->baseName));
    if (ty->typeArgs && ty->typeArgs->count > 0) {
        fcBufChar(b, '<');
        for (i = 0; i < ty->typeArgs->count; i++) { if (i) fcBufStr(b, ", "); TypeTextB(t, b, (const FcTypeRef*)ty->typeArgs->items[i]); }
        fcBufChar(b, '>');
    }
    for (i = 0; i < ty->arrayRank; i++) fcBufStr(b, "[]");
}
static const char* TypeText(FcTC* t, const FcTypeRef* ty) { FcBuf b = {0}; TypeTextB(t, &b, ty); return fcBufDone(TC_, &b); }
static const char* ParamsText(FcTC* t, FcList* parameters) {
    FcBuf b = {0};
    int i, k;
    fcBufStr(&b, "");
    for (i = 0; i < parameters->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parameters->items[i];
        if (i) fcBufStr(&b, ", ");
        if (p->byRef) fcBufStr(&b, "ref ");
        if (p->type && !fcTrIsInferred(p->type)) { TypeTextB(t, &b, p->type); fcBufChar(&b, ' '); }
        fcBufStr(&b, p->name);
        for (k = 0; k < p->arrayRanks->count; k++) fcBufStr(&b, "[]");
    }
    return fcBufDone(TC_, &b);
}
static bool InterfaceHasMethod(FcTC* t, FcClassDecl* decl, const char* name) {
    FcList* classes = fcListNew(TC_);
    FcList* bases = BaseClassesOf(t, decl);
    int i, k, j, q;
    fcListAdd(TC_, classes, decl);
    for (i = 0; i < bases->count; i++) fcListAdd(TC_, classes, bases->items[i]);
    for (i = 0; i < classes->count; i++) {
        FcClassDecl* c = (FcClassDecl*)classes->items[i];
        if (!c->baseRefs) continue;
        for (k = 0; k < c->baseRefs->count; k++) {
            const char* n = NameOf(t, (FcTypeRef*)c->baseRefs->items[k]);
            FcList* closure;
            if (!fcDictHas(&t->interfaces, n)) continue;
            closure = InterfaceClosure(t, n);
            for (j = 0; j < closure->count; j++) {
                FcInterfaceDecl* iface = (FcInterfaceDecl*)fcDictGet(&t->interfaces, closure->items[j]);
                if (!iface) continue;
                for (q = 0; q < iface->methods->count; q++) if (streq0(((FcInterfaceMethodSig*)iface->methods->items[q])->name, name)) return true;
            }
        }
    }
    return false;
}
static void CheckOverrides(FcTC* t, FcClassDecl* decl) {
    FcList* bases = BaseClassesOf(t, decl);
    const char* plain = PlainName(t, decl->name);
    int i, b, k;
    for (i = 0; i < decl->members->count; i++) {
        FcStmt* member = (FcStmt*)decl->members->items[i];
        if (member->kind == SK_MethodDecl && !((FcMethodDecl*)member)->isStatic) {
            FcMethodDecl* m = (FcMethodDecl*)member;
            bool isOperator = fcStartsWith(m->name, "operator") || streq0(m->name, "GetIndex") || streq0(m->name, "SetIndex");
            const char* what = isOperator ? TF(t, "The operator '%s'", OperatorText(t, m->name)) : TF(t, "The method '%s'", m->name);
            FcClassDecl* replaced = NULL;
            for (b = 0; b < bases->count && !replaced; b++) {
                FcClassDecl* bc = (FcClassDecl*)bases->items[b];
                for (k = 0; k < bc->members->count && !replaced; k++) {
                    FcStmt* bm = (FcStmt*)bc->members->items[k];
                    if (bm->kind == SK_MethodDecl && Replaces(t, m, decl, (FcMethodDecl*)bm, bc)) replaced = bc;
                }
            }
            if (replaced && !m->isOverride)
                Report(t, TC_MissingOverrideCode, TF(t, "%s of '%s' replaces the one of its base class '%s' - mark it 'override' (%soverride %s%s(%s))", what, plain, PlainName(t, replaced->name),
                    m->access == ACC_Public ? "" : TF(t, "%s ", AccessText(m->access)),
                    m->returnType && !fcTrIsInferred(m->returnType) ? TF(t, "%s ", TypeText(t, m->returnType)) : "",
                    isOperator ? TF(t, "operator%s", OperatorText(t, m->name)) : m->name, ParamsText(t, m->params)), m->h.line);
            else if (!replaced && m->isOverride) {
                bool sameName = false, inInterface;
                for (b = 0; b < bases->count && !sameName; b++) {
                    FcClassDecl* bc = (FcClassDecl*)bases->items[b];
                    for (k = 0; k < bc->members->count; k++) {
                        FcStmt* bm = (FcStmt*)bc->members->items[k];
                        if (bm->kind == SK_MethodDecl && !((FcMethodDecl*)bm)->isStatic && streq0(((FcMethodDecl*)bm)->name, m->name)) sameName = true;
                    }
                }
                inInterface = !sameName && InterfaceHasMethod(t, decl, m->name);
                ReportError(t, TC_NothingToOverrideCode, TF(t, "%s of '%s' is marked 'override', but no base class of '%s' has %s%s%s - nothing is overridden", what, plain, plain,
                    isOperator ? "this operator" : TF(t, "a method '%s'", m->name), sameName ? " with these parameters" : "",
                    inInterface ? " (a method of an interface is implemented without 'override')" : ""), m->h.line);
            }
        } else if (member->kind == SK_PropertyDecl && !((FcPropertyDecl*)member)->isStatic) {
            FcPropertyDecl* p = (FcPropertyDecl*)member;
            FcClassDecl* replaced = NULL;
            for (b = 0; b < bases->count && !replaced; b++) {
                FcClassDecl* bc = (FcClassDecl*)bases->items[b];
                for (k = 0; k < bc->members->count && !replaced; k++) {
                    FcStmt* bp = (FcStmt*)bc->members->items[k];
                    if (bp->kind == SK_PropertyDecl && !((FcPropertyDecl*)bp)->isStatic && streq0(((FcPropertyDecl*)bp)->name, p->name)) replaced = bc;
                }
            }
            if (replaced && !p->isOverride)
                Report(t, TC_MissingOverrideCode, TF(t, "The property '%s' of '%s' replaces the one of its base class '%s' - mark it 'override' (override %s%s { ... })", p->name, plain, PlainName(t, replaced->name),
                    p->type && !fcTrIsInferred(p->type) ? TF(t, "%s ", TypeText(t, p->type)) : "", p->name), p->h.line);
            else if (!replaced && p->isOverride)
                ReportError(t, TC_NothingToOverrideCode, TF(t, "The property '%s' of '%s' is marked 'override', but no base class of '%s' has a property '%s' - nothing is overridden", p->name, plain, plain, p->name), p->h.line);
        }
    }
}

static void ReportNoReturnType(FcTC* t, const char* method, int line) {
    Report(t, TC_NoReturnTypeCode, TF(t, "Method '%s' has no return type and is assumed to return 'any'. Please add one (use 'undefined' if it returns nothing).", method), line);
}

static void CheckBody(FcTC* t, FcList* parameters, FcBlockStmt* body, const FcST* returnType, FcTypeRef* returnTypeRef, const char* what);

static void CheckMembers(FcTC* t, FcList* members, const char* ownerName) {
    int i, k;
    (void)ownerName;
    for (i = 0; i < members->count; i++) {
        FcStmt* member = (FcStmt*)members->items[i];
        switch (member->kind) {
            case SK_FieldDecl: {
                FcFieldDecl* f = (FcFieldDecl*)member;
                const FcST* declaredField;
                const FcST* init;
                CheckRawUnit(t, f->type, f->name, f->h.line);
                if (f->initializer == NULL) break;
                declaredField = Declared(t, f->type, f->arrayRanks->count, NULL);
                init = InferExpected(t, f->initializer, declaredField);
                if (declaredField) declaredField = WithInitializerUnit(t, declaredField, f->type, f->arrayRanks->count, LiteralType(t, f->initializer));
                if (declaredField) CheckAssignable(t, init, declaredField, f->h.line, TF(t, "field '%s'", f->name), TC_AssignCode, f->initializer);
                break;
            }
            case SK_MethodDecl: {
                FcMethodDecl* m = (FcMethodDecl*)member;
                bool untyped = m->returnType == NULL || fcTrIsInferred(m->returnType);
                bool isOperator = fcStartsWith(m->name, "operator") || streq0(m->name, "GetIndex") || streq0(m->name, "SetIndex");
                FcList* previousCollected = t->collectedReturns;
                FcList* previousTypeParams = t->bodyTypeParams;
                CheckParametersTyped(t, m->params, m->name, m->h.line);
                if (untyped && !isOperator) ReportNoReturnType(t, m->name, m->h.line);
                t->collectedReturns = NULL;
                t->bodyTypeParams = m->typeParams;
                CheckBody(t, m->params, m->body, untyped ? NULL : Declared(t, m->returnType, 0, NULL), untyped ? NULL : m->returnType, TF(t, "Return value of '%s'", m->name));
                t->bodyTypeParams = previousTypeParams;
                t->collectedReturns = previousCollected;
                break;
            }
            case SK_ConstructorDecl: {
                FcConstructorDecl* c = (FcConstructorDecl*)member;
                const char* baseOfCtor;
                bool wasCtor;
                CheckParametersTyped(t, c->params, "construct", c->h.line);
                if ((baseOfCtor = BaseClassName(t)) != NULL) CheckConstructorAccess(t, baseOfCtor, c->baseArgs ? c->baseArgs->count : 0, c->h.line);
                wasCtor = t->inConstructor;
                t->inConstructor = true;
                CheckBody(t, c->params, c->body, NULL, NULL, "");
                t->inConstructor = wasCtor;
                break;
            }
            case SK_DestructorDecl: CheckBody(t, fcListNew(TC_), ((FcDestructorDecl*)member)->body, NULL, NULL, ""); break;
            case SK_PropertyDecl: {
                FcPropertyDecl* p = (FcPropertyDecl*)member;
                if (p->getter) CheckBody(t, fcListNew(TC_), p->getter, Declared(t, p->type, 0, NULL), p->type, TF(t, "Property '%s'", p->name));
                if (p->setter) {
                    FcList* ps = fcListNew(TC_);
                    FcLambdaParam* value = (FcLambdaParam*)fcAlloc(TC_, sizeof *value);
                    value->name = "value"; value->type = p->type; value->arrayRanks = fcListNew(TC_);
                    fcListAdd(TC_, ps, value);
                    CheckBody(t, ps, p->setter, NULL, NULL, "");
                    if (!p->isStatic) {
                        FcList* lines = SetterClaimsAmbiguous(TC_, p->setter);
                        for (k = 0; k < lines->count; k++)
                            Report(t, TC_SetterClaimCode, TF(t, "The setter of '%s' passes a new value to this call only on some paths: where another path assigns 'value' to a field or property, the call does not take it - on the paths without that assignment a new value dies with the setter", p->name), FC_VALI(lines->items[k]));
                    }
                }
                break;
            }
            default: break;
        }
    }
}

static void CheckParametersTyped(FcTC* t, FcList* parameters, const char* owner, int line) {
    int i;
    for (i = 0; i < parameters->count; i++) CheckRawUnit(t, ((FcLambdaParam*)parameters->items[i])->type, ((FcLambdaParam*)parameters->items[i])->name, line);
    for (i = 0; i < parameters->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parameters->items[i];
        if (p->type == NULL || fcTrIsInferred(p->type))
            Report(t, TC_UntypedCode, TF(t, "Parameter '%s' of '%s' has no type and is assumed to be 'any'. Please add a type (use 'any' if every type is allowed).", p->name, owner), line);
    }
}

static void CheckBody(FcTC* t, FcList* parameters, FcBlockStmt* body, const FcST* returnType, FcTypeRef* returnTypeRef, const char* what) {
    const FcST* previousReturn = t->returnType;
    FcTypeRef* previousRef = t->returnTypeRef;
    const char* previousWhat = t->returnWhat;
    int i;
    t->returnType = returnType;
    t->returnTypeRef = returnTypeRef; t->returnWhat = what;
    Push(t);
    for (i = 0; i < parameters->count; i++) {
        FcLambdaParam* p = (FcLambdaParam*)parameters->items[i];
        const FcST* declaredParam = DeclaredOrDynamic(t, p->type, p->arrayRanks->count, NULL);
        if (p->defaultValue != NULL && stIsNumber(declaredParam) && !declaredParam->raw && p->arrayRanks->count == 0 && p->type && p->type->unit == NULL) {
            const FcST* lt = LiteralType(t, p->defaultValue);
            if (!(stIsNumber(lt) && lt->unit && fcUIsUnitless(lt->unit))) declaredParam = stWithUnit(TC_, declaredParam, NULL, false);
        }
        Declare(t, p->name, MayBeUndefined(t, declaredParam));
        if (p->type && p->type->arrayRank == 0 && p->type->pointerDepth == 0 && (p->type->nonNull || (streq0(p->type->baseName, "string") && p->type->lambdaSignature == NULL))
            && p->arrayRanks->count == 0 && !p->byRef)
            DeclareLocalType(t, p->name, p->type, -1);
    }
    for (i = 0; i < body->statements->count; i++) Check(t, (FcStmt*)body->statements->items[i]);
    Pop(t);
    t->returnType = previousReturn;
    t->returnTypeRef = previousRef; t->returnWhat = previousWhat;
}

/* ---- expressions ---- */
static void MarkFloatTarget(FcExpr* e) {
    switch (e->kind) {
        case EK_BinaryExpr: {
            FcBinaryExpr* b = (FcBinaryExpr*)e;
            if (b->op == BOP_Power) b->floatPower = true;
            else if (b->op == BOP_Add || b->op == BOP_Sub || b->op == BOP_Mul || b->op == BOP_Div || b->op == BOP_Mod) { MarkFloatTarget(b->left); MarkFloatTarget(b->right); }
            break;
        }
        case EK_UnaryExpr: if (((FcUnaryExpr*)e)->op == UOP_Negate) MarkFloatTarget(((FcUnaryExpr*)e)->operand); break;
        case EK_ConditionalExpr: MarkFloatTarget(((FcConditionalExpr*)e)->then); MarkFloatTarget(((FcConditionalExpr*)e)->otherwise); break;
        default: break;
    }
}

static const FcST* InferExpected(FcTC* t, FcExpr* expr, const FcST* expected) {
    FcExpr* asking = expr;
    if (expected && expected->kind == STK_Float) MarkFloatTarget(expr);
    if (expr->kind == EK_LambdaExpr) return InferLambda(t, (FcLambdaExpr*)expr, expected);
    while (asking->kind == EK_UnaryExpr && ((FcUnaryExpr*)asking)->op == UOP_Negate) asking = ((FcUnaryExpr*)asking)->operand;
    if (expected && (asking->kind == EK_UnitCoerceExpr || asking->kind == EK_TypeCoerceExpr)) {
        FcExpr* previousExpr = t->declaredFor;
        const FcST* previousTarget = t->declaredTarget;
        const FcST* r;
        t->declaredFor = asking; t->declaredTarget = expected;
        r = Infer(t, expr);
        t->declaredFor = previousExpr; t->declaredTarget = previousTarget;
        return r;
    }
    return Infer(t, expr);
}

/* ---- conversions from the context ---- */
static const FcST* UnitResult(FcTC* t, const FcST* operand, const FcU* target, bool relabel) {
    const FcU* from;
    if (!stIsNumber(operand)) return ST_DYNAMIC;
    if (relabel || operand->kind == STK_Float) return stWithUnit(TC_, operand, target, false);
    from = operand->unit;
    if (from && !operand->unitIsDimension && (fcUEquals(from, target) || (fcUCompatible(from, target) && !(fcUIsUnitless(target) && !fcUIsUnitless(from))))) return stWithUnit(TC_, operand, target, false);
    return ST_NUMBER;
}
static const FcST* ExplicitUnitResult(FcTC* t, const FcST* operand, const char* targetName, bool relabel, int line) {
    const FcU* target;
    const FcU* from;
    if (!stIsNumber(operand)) return ST_DYNAMIC;
    target = fcUParse(TC_, targetName);
    from = operand->unit;
    if (!relabel && operand->kind == STK_Int && from && !fcUEquals(from, target) && fcUCompatible(from, target) && fcUIsLinear(from) && fcUIsLinear(target) && fcUConversionFactorTo(from, target) < 1)
        Report(t, TC_UnitLossCode, TF(t, "A whole number in '%s' converted into '%s' may lose its fraction (convert a float: 'x!float:%s')", UnitText(t, from), UnitText(t, target), targetName), line);
    if (!relabel && operand->kind == STK_Int && (operand->unit == NULL || operand->unitIsDimension) && !fcUIsUnitless(target)) return stWithUnit(TC_, operand, target, false);
    return UnitResult(t, operand, target, relabel);
}
static const FcST* ExplicitTypeResult(FcTC* t, const FcST* operand, int keyword, bool isUnsigned) {
    const FcU* operandUnit = stIsNumber(operand) ? operand->unit : NULL;
    switch (keyword) {
        case TT_KwWord: return ST_WORD;
        case TT_KwInt: return isUnsigned ? stWithUnit(TC_, ST_INT, FC_UNITLESS, false) : stWithUnit(TC_, ST_INT, operandUnit, false);
        case TT_KwFloat: return stWithUnit(TC_, ST_FLOAT, operandUnit, false);
        case TT_KwBool: return ST_BOOL;
        case TT_KwChar: return ST_CHAR;
        case TT_KwString: return ST_STRING;
        default: return ST_DYNAMIC;
    }
}
static void NoConversion(FcTC* t, const FcST* operand, const char* to, int line) {
    ReportError(t, TC_ImpossibleCastCode, TF(t, "A value of type '%s' is never converted to '%s' by '!': the conversion always fails at run time%s", S_(t, operand), to,
        streq0(to, "string") ? " - the text of a value is '\"\" + x' or 'x.ToString()'" : ""), line);
}
static const FcST* InferCoercionOperand(FcTC* t, const FcCoercionInfo* info, int line) {
    const FcST* type = Infer(t, info->inner);
    if (info->explicitType >= 0) type = ExplicitTypeResult(t, type, info->explicitType, false);
    if (info->explicitUnit) type = ExplicitUnitResult(t, type, info->explicitUnit, false, line);
    return type;
}
/* AutoTypeResult: the type, the plan (FixedCoercion) and whether the compiler can emit it without looking at the anchor */
static const FcST* AutoTypeResult(FcTC* t, const FcST* operand, const FcST* target, bool declared, int line, bool* known, FcFixedCoercion* plan) {
    const FcU* unit = stIsNumber(operand) ? operand->unit : (operand->kind == STK_Enum || operand->kind == STK_Char || operand->kind == STK_Word) ? FC_UNITLESS : NULL;
    bool dimensionOnly = stIsNumber(operand) && operand->unitIsDimension;
    bool neverNumber = operand->kind == STK_Bool || operand->kind == STK_String || operand->kind == STK_Class || operand->kind == STK_Object || operand->kind == STK_Array || operand->kind == STK_Lambda;
#define IMPOSSIBLE(to) Report(t, TC_ImpossibleCastCode, TF(t, "A value of type '%s' is never converted to '%s' by '!': the conversion always fails at run time", S_(t, operand), to), line)
    *known = true;
    plan->type = -1; plan->toWord = false; plan->unit = NULL; plan->unitv = NULL;
    switch (target->kind) {
        case STK_Float:
            if (neverNumber && operand->nul == NUL_Defined) IMPOSSIBLE("float");
            plan->type = TAG_Float;
            return stWithUnit(TC_, ST_FLOAT, unit, dimensionOnly);
        case STK_Enum:
            if (declared) break;
            /* FALLTHROUGH */
        case STK_Int: {
            FcST* result;
            if (neverNumber && operand->nul == NUL_Defined) IMPOSSIBLE("int");
            result = stCopy(TC_, ST_INT);
            result->wide = operand->kind == STK_Int ? operand->wide : 0;
            *known = declared || operand->kind == STK_Float || operand->kind == STK_Char || operand->kind == STK_Enum || operand->kind == STK_Word || operand->kind == STK_Undefined
                || (operand->kind == STK_Int && operand->wide == 0) || neverNumber;
            plan->type = TAG_Int;
            return stWithUnit(TC_, result, unit, dimensionOnly);
        }
        case STK_Word: plan->toWord = true; return ST_WORD;
        case STK_Char:
            if (neverNumber && operand->nul == NUL_Defined) IMPOSSIBLE("char");
            plan->type = TAG_Char;
            return ST_CHAR;
        case STK_Bool:
            if (declared) break;
            if (!stIsDynamic(operand) && operand->kind != STK_Bool) NoConversion(t, operand, "bool", line);
            plan->type = TAG_Bool;
            return ST_BOOL;
        case STK_String:
            if (declared) break;
            if (!stIsDynamic(operand) && operand->kind != STK_String) NoConversion(t, operand, "string", line);
            plan->type = TAG_String;
            return ST_STRING;
        default: break;
    }
#undef IMPOSSIBLE
    *known = false;
    return declared ? operand : ST_DYNAMIC;
}
static const FcST* AutoCoercionResult(FcTC* t, FcExpr* expr, const FcCoercionInfo* info, const FcST* operand, const FcST* target, bool declared, bool fix) {
    const FcST* type = operand;
    FcFixedCoercion plan;
    bool known = target != NULL && fix;
    plan.type = -1; plan.toWord = false; plan.unit = NULL; plan.unitv = NULL;
    if (ciTypeIsAuto(info) && target != NULL) {
        bool typeKnown;
        FcFixedCoercion typePlan;
        type = AutoTypeResult(t, type, target, declared, expr->line, &typeKnown, &typePlan);
        if (declared && !typeKnown) { typePlan.type = -1; typePlan.toWord = false; typePlan.unit = NULL; typePlan.unitv = NULL; }
        else known = known && typeKnown;
        plan = typePlan;
    }
    if (ciUnitIsAuto(info)) {
        const FcU* unit;
        if (target == NULL) unit = FC_UNITLESS;
        else if (stIsNumber(target)) unit = target->unit != NULL && !target->unitIsDimension ? target->unit : declared ? FC_UNITLESS : NULL;
        else unit = declared || target->kind == STK_Enum || target->kind == STK_Bool || target->kind == STK_Char || target->kind == STK_String || target->kind == STK_Word ? FC_UNITLESS : NULL;
        if (unit == NULL) {
            known = false;
            type = type->kind == STK_Float || (info->relabelAuto && stIsNumber(type)) ? stWithUnit(TC_, type, NULL, false) : ST_DYNAMIC;
        } else {
            const FcU* from = type->unit;
            if (!info->relabelAuto && type->kind == STK_Int && from && !type->unitIsDimension && !fcUEquals(from, unit) && fcUCompatible(from, unit)
                && fcUIsLinear(from) && fcUIsLinear(unit) && !fcUIsUnitless(unit) && fcUConversionFactorTo(from, unit) < 1)
                Report(t, TC_UnitLossCode, TF(t, "A whole number in '%s' converted into '%s' may lose its fraction (convert it as a float: 'x:!' next to a float, or 'x!float:')", UnitText(t, from), UnitText(t, unit)), expr->line);
            type = UnitResult(t, type, unit, info->relabelAuto);
            plan.unitv = unit;
        }
    }
    /* FixCoercion */
    if (known) { FcFixedCoercion* p = (FcFixedCoercion*)fcAlloc(TC_, sizeof *p); *p = plan; fcDictPut(t->rr->fixedCoercions, expr, p); }
    else fcDictRemove(t->rr->fixedCoercions, expr);
    return type;
}
static const FcST* InferAutoCoercion(FcTC* t, FcExpr* expr, const FcCoercionInfo* info) {
    const FcST* operand = InferCoercionOperand(t, info, expr->line);
    if (expr == t->declaredFor && t->declaredTarget) {
        const FcST* target = t->declaredTarget;
        bool typeContext = ciTypeIsAuto(info) && (target->kind == STK_Int || target->kind == STK_Float || target->kind == STK_Char || target->kind == STK_Word);
        bool unitContext = ciUnitIsAuto(info) && stIsNumber(target) && target->unit != NULL && !target->unitIsDimension;
        if (typeContext || unitContext) return AutoCoercionResult(t, expr, info, operand, target, true, true);
    }
    return AutoCoercionResult(t, expr, info, operand, NULL, false, false);
}

static const FcST* InferCore(FcTC* t, FcExpr* expr);
/* the width of a type for `int.Bits` / `x.Bits` */
static int BitsOfType(FcTC* t, const char* baseName, int width) {
    if (streq0(baseName, "int")) return width != FC_NULL_INT ? width : t->programIntWidth;
    if (streq0(baseName, "float")) return width != FC_NULL_INT ? width : t->programFloatWidth;
    if (streq0(baseName, "byte")) return 8;
    if (streq0(baseName, "char")) return 16;
    if (streq0(baseName, "bool")) return 1;
    return t->programWordWidth;
}
static const FcST* InferWithNull(FcTC* t, FcExpr* expr);
static void fcNoteExprType(FcTC* t, FcExpr* expr, const FcST* type);
static const FcST* Infer(FcTC* t, FcExpr* expr) {
    const FcST* result = InferWithNull(t, expr);
    fcNoteExprType(t, expr, result);
    return result;
}
static const FcST* InferWithNull(FcTC* t, FcExpr* expr) {
    const FcST* type = InferCore(t, expr);
    if (t->treeTypes) fcDictPut(t->treeTypes, expr, (void*)S_(t, type));
    switch (expr->kind) {
        case EK_LiteralExpr:
            if (((FcLiteralExpr*)expr)->value.kind == VK_Undefined) return stWithNull(TC_, type, NUL_MayBeUndefined);
            return stIsValueKind(type) ? stWithNull(TC_, type, NUL_Defined) : type;
        case EK_UnaryExpr: {
            FcUnaryOp op = ((FcUnaryExpr*)expr)->op;
            if (op == UOP_Trust && type->kind != STK_Undefined) return stWithNull(TC_, type, NUL_Defined);
            if ((op == UOP_Negate || op == UOP_LogicalNot || op == UOP_BitNot || op == UOP_Trust) && stIsValueKind(type)) return stWithNull(TC_, type, NUL_Defined);
            return type;
        }
        case EK_BinaryExpr: case EK_IncDecExpr: case EK_IsOfExpr: case EK_IsInExpr: case EK_IsFromExpr:
            return stIsValueKind(type) ? stWithNull(TC_, type, NUL_Defined) : type;
        default: return type;
    }
}

static const FcST* InferUnary(FcTC* t, FcUnaryExpr* u);
static const FcST* InferBinary(FcTC* t, FcBinaryExpr* b);
static const FcST* InferAssign(FcTC* t, FcAssignExpr* a);
static const FcST* InferMember(FcTC* t, FcMemberExpr* m, FcMemberUse use);
static const FcST* InferIndex(FcTC* t, FcIndexExpr* ix);
static const FcST* InferCall(FcTC* t, FcCallExpr* c);
static const FcST* InferNew(FcTC* t, FcNewExpr* n);
static const FcST* InferConditionalAccess(FcTC* t, FcConditionalAccessExpr* access);
static const FcST* InferWith(FcTC* t, FcWithExpr* with);
static void CheckImplicitThisAccess(FcTC* t, FcIdentifierExpr* id, FcMemberUse use);
static void CheckCast(FcTC* t, const FcST* operand, const FcST* target, int line);
static void CheckSizeKind(FcTC* t, const FcST* size, const char* what, int line);

static const FcST* InferCore(FcTC* t, FcExpr* expr) {
    int i;
    switch (expr->kind) {
        case EK_TypeBitsExpr:
            fcDictPut(t->rr->bitsOf, expr, FC_IVAL(BitsOfType(t, ((FcTypeBitsExpr*)expr)->baseName, ((FcTypeBitsExpr*)expr)->width) + 1));
            return stWithNull(TC_, stWithUnit(TC_, ST_INT, FC_UNITLESS, false), NUL_Defined);
        case EK_LiteralExpr: {
            FcValue* v = &((FcLiteralExpr*)expr)->value;
            switch (v->kind) {
                case VK_Bool: return ST_BOOL;
                case VK_Int: return stWithUnit(TC_, ST_INT, fcLiteralUnit(t, v), false);
                case VK_Float: return stWithUnit(TC_, ST_FLOAT, fcLiteralUnit(t, v), false);
                case VK_Char: return ST_CHAR;
                case VK_String: return ST_STRING;
                case VK_Undefined: return ST_UNDEFINED;
            }
            return ST_DYNAMIC;
        }
        case EK_IdentifierExpr:
            CheckImplicitThisAccess(t, (FcIdentifierExpr*)expr, MU_Read);
            return Lookup(t, ((FcIdentifierExpr*)expr)->name);
        case EK_ThisExpr: return t->thisType ? t->thisType : (t->cls ? ThisClass(t) : ST_DYNAMIC);
        case EK_InterpolatedStringExpr: {
            FcList* parts = ((FcInterpolatedStringExpr*)expr)->parts;
            for (i = 0; i < parts->count; i++) if (((FcInterpolationPart*)parts->items[i])->isExpression) Infer(t, ((FcInterpolationPart*)parts->items[i])->expression);
            return ST_STRING;
        }
        case EK_UnaryExpr: return InferUnary(t, (FcUnaryExpr*)expr);
        case EK_BinaryExpr: return InferBinary(t, (FcBinaryExpr*)expr);
        case EK_RebindExpr: Infer(t, ((FcRebindExpr*)expr)->target); return Infer(t, ((FcRebindExpr*)expr)->lambda);
        case EK_ConditionalExpr: {
            FcConditionalExpr* ce = (FcConditionalExpr*)expr;
            const FcST* thenType;
            const FcST* elseType;
            FcList* both = fcListNew(TC_);
            const FcST* joined;
            FcNull n;
            CheckCondition(t, ce->condition);
            thenType = Infer(t, ce->then);
            elseType = Infer(t, ce->otherwise);
            fcListAdd(TC_, both, (void*)thenType); fcListAdd(TC_, both, (void*)elseType);
            joined = Join(t, both, expr);
            if (thenType->nul == NUL_MayBeUndefined || elseType->nul == NUL_MayBeUndefined) n = NUL_MayBeUndefined;
            else if (thenType->nul == NUL_Unknown || elseType->nul == NUL_Unknown) n = NUL_Unknown;
            else if (thenType->nul == NUL_Inferred || elseType->nul == NUL_Inferred) n = NUL_Inferred;
            else n = NUL_Defined;
            return stWithNull(TC_, joined, n);
        }
        case EK_AssignExpr: return InferAssign(t, (FcAssignExpr*)expr);
        case EK_MemberExpr: return InferMember(t, (FcMemberExpr*)expr, MU_Read);
        case EK_IndexExpr: return InferIndex(t, (FcIndexExpr*)expr);
        case EK_CallExpr: return InferCall(t, (FcCallExpr*)expr);
        case EK_NewExpr: return InferNew(t, (FcNewExpr*)expr);
        case EK_IncDecExpr: {
            FcIncDecExpr* inc = (FcIncDecExpr*)expr;
            if (inc->target->kind == EK_MemberExpr) return InferMember(t, (FcMemberExpr*)inc->target, MU_Write);
            if (inc->target->kind == EK_IdentifierExpr) CheckImplicitThisAccess(t, (FcIdentifierExpr*)inc->target, MU_Write);
            return Infer(t, inc->target);
        }
        case EK_LambdaExpr: return InferLambda(t, (FcLambdaExpr*)expr, NULL);
        case EK_UntilExpr: {
            FcUntilExpr* u = (FcUntilExpr*)expr;
            for (i = 0; i < u->limits->count; i++) Infer(t, (FcExpr*)u->limits->items[i]);
            return Infer(t, u->inner);
        }
        case EK_AwaitOnExpr: {
            FcAwaitOnExpr* a = (FcAwaitOnExpr*)expr;
            FcList* types = fcListNew(TC_);
            const FcST* r;
            for (i = 0; i < a->args->count; i++) fcListAdd(TC_, types, (void*)Infer(t, (FcExpr*)a->args->items[i]));
            r = InferLambda(t, (FcLambdaExpr*)a->lambda, stLambdaOf(TC_, types->count, NULL, types));
            return r->kind == STK_Lambda && r->ret ? r->ret : ST_DYNAMIC;
        }
        case EK_IsOfExpr: Infer(t, ((FcIsOfExpr*)expr)->operand); return ST_BOOL;
        case EK_IsInExpr: Infer(t, ((FcIsInExpr*)expr)->operand); return ST_BOOL;
        case EK_RangeExpr: {
            FcRangeExpr* range = (FcRangeExpr*)expr;
            const FcST* from = Infer(t, range->from);
            const FcST* to = Infer(t, range->to);
            if (!stIsDynamic(from) && from->kind != STK_Int) Report(t, TC_AssignCode, TF(t, "A range goes from a whole number to a whole number, not '%s'", S_(t, from)), range->from->line);
            if (!stIsDynamic(to) && to->kind != STK_Int) Report(t, TC_AssignCode, TF(t, "A range goes from a whole number to a whole number, not '%s'", S_(t, to)), range->to->line);
            return stArrayOf(TC_, stWithUnit(TC_, ST_INT, from->kind == STK_Int ? from->unit : NULL, false));
        }
        case EK_IsFromExpr: Infer(t, ((FcIsFromExpr*)expr)->operand); Infer(t, ((FcIsFromExpr*)expr)->ownerExpr); return ST_BOOL;
        case EK_UnitCoerceExpr: case EK_TypeCoerceExpr: {
            FcCoercionInfo info = fcAnalyzeCoercion(expr);
            if (ciRequestsAnyAuto(&info)) return InferAutoCoercion(t, expr, &info);
            if (expr->kind == EK_UnitCoerceExpr) {
                FcUnitCoerceExpr* uc = (FcUnitCoerceExpr*)expr;
                const FcST* operand = Infer(t, uc->operand);
                return uc->targetUnitName ? ExplicitUnitResult(t, operand, uc->targetUnitName, uc->relabel, uc->h.line) : operand;
            } else {
                FcTypeCoerceExpr* tc = (FcTypeCoerceExpr*)expr;
                const FcST* operand = Infer(t, tc->operand);
                if (tc->targetClass) {
                    const FcST* castTarget = DeclaredOrDynamic(t, tc->targetClass, 0, NULL);
                    CheckCast(t, operand, castTarget, tc->h.line);
                    return castTarget;
                }
                if (tc->targetTypeKeyword == TT_KwString && !stIsDynamic(operand) && operand->kind != STK_String) NoConversion(t, operand, "string", tc->h.line);
                if (tc->targetTypeKeyword == TT_KwBool && !stIsDynamic(operand) && operand->kind != STK_Bool) NoConversion(t, operand, "bool", tc->h.line);
                return tc->targetTypeKeyword < 0 ? operand : ExplicitTypeResult(t, operand, tc->targetTypeKeyword, tc->isUnsigned);
            }
        }
        case EK_ArrayLiteralExpr: {
            FcList* els = ((FcArrayLiteralExpr*)expr)->elements;
            FcList* types = fcListNew(TC_);
            for (i = 0; i < els->count; i++) fcListAdd(TC_, types, (void*)Infer(t, (FcExpr*)els->items[i]));
            return stArrayOf(TC_, Join(t, types, expr));
        }
        case EK_NewArrayExpr: {
            FcNewArrayExpr* na = (FcNewArrayExpr*)expr;
            const FcST* element;
            for (i = 0; i < na->sizeExprs->count; i++) if (na->sizeExprs->items[i]) CheckSizeKind(t, Infer(t, (FcExpr*)na->sizeExprs->items[i]), "an array", na->h.line);
            element = DeclaredOrDynamic(t, na->elementType, 0, NULL);
            if (stIsNumber(element) && (na->elementType == NULL || na->elementType->unit == NULL)) element = stWithUnit(TC_, element, NULL, false);
            for (i = 0; i < na->sizeExprs->count; i++) element = stArrayOf(TC_, element);
            return element;
        }
        case EK_NewBufferExpr: {
            FcNewBufferExpr* nb = (FcNewBufferExpr*)expr;
            FcST* byteT;
            CheckSizeKind(t, Infer(t, nb->sizeExpr), nb->words ? "an array" : "a byte buffer", nb->h.line);
            if (nb->words) return stArrayOf(TC_, ST_WORD);
            byteT = stCopy(TC_, ST_INT); byteT->unit = FC_UNITLESS; byteT->raw = true;
            return stArrayOf(TC_, byteT);
        }
        case EK_ThrowExpr: Infer(t, ((FcThrowExpr*)expr)->value); return ST_DYNAMIC;
        case EK_SyncExpr: Infer(t, ((FcSyncExpr*)expr)->target); return ST_BOOL;
        case EK_SyncGlobalsExpr: return ST_INT;
        case EK_ProbeExpr: {
            FcList* ps = fcListNew(TC_);
            fcListAdd(TC_, ps, (void*)ST_DYNAMIC); fcListAdd(TC_, ps, (void*)ST_DYNAMIC);
            Infer(t, ((FcProbeExpr*)expr)->target);
            InferExpected(t, ((FcProbeExpr*)expr)->handler, stLambdaOf(TC_, 2, NULL, ps));
            return ST_DYNAMIC;
        }
        case EK_TryProcessExpr: Infer(t, ((FcTryProcessExpr*)expr)->target); return ST_DYNAMIC;
        case EK_TryCallExpr: Infer(t, ((FcTryCallExpr*)expr)->call); return ST_DYNAMIC;
        case EK_ConditionalAccessExpr: return InferConditionalAccess(t, (FcConditionalAccessExpr*)expr);
        case EK_WithExpr: return InferWith(t, (FcWithExpr*)expr);
        case EK_ConditionalTargetExpr: { void* v; return fcDictTry(&t->placeholderTypes, expr, &v) ? (const FcST*)v : ST_DYNAMIC; }
        default: return ST_DYNAMIC;
    }
}

static bool NeverKind(const FcST* ty, bool pointer) {
    switch (ty->kind) {
        case STK_String: case STK_Float: case STK_Bool: case STK_Char: case STK_Class: case STK_Object: case STK_Array: case STK_Lambda: return true;
        case STK_Int: case STK_Word: case STK_Enum: return pointer;
        default: return false;
    }
}

static const FcST* InferUnary(FcTC* t, FcUnaryExpr* u) {
    const FcST* operand = Infer(t, u->operand);
    if (u->op == UOP_Dereference && NeverKind(operand, true))
        ReportError(t, TC_KindCode, TF(t, "'*' needs a pointer, not a value of type '%s': the dereference always fails at run time", S_(t, operand)), u->h.line);
    if (u->op == UOP_LogicalNot) CheckLogicalOperand(t, operand, "!", u->h.line);
    switch (u->op) {
        case UOP_Negate: return operand->kind == STK_Int || operand->kind == STK_Float ? operand : operand->kind == STK_Word ? ST_WORD : ST_DYNAMIC;
        case UOP_LogicalNot: return ST_BOOL;
        case UOP_BitNot: return operand->kind == STK_Word ? ST_WORD : ST_INT;
        case UOP_FlatCopy: case UOP_DeepCopy: case UOP_Take: case UOP_TryTake: case UOP_Trust: return operand;
        default: return ST_DYNAMIC;
    }
}

static const FcST* InferBinaryType(FcTC* t, FcBinaryExpr* b, const FcST* left, const FcST* right);
static const FcU* BinaryUnit(FcTC* t, FcBinaryExpr* b, const FcST* left, const FcST* right);
static void InferOperands(FcTC* t, FcBinaryExpr* b, const FcST** left, const FcST** right) {
    FcCoercionInfo leftInfo, rightInfo;
    if (b->op == BOP_And || b->op == BOP_Or) { *left = Infer(t, b->left); *right = Infer(t, b->right); return; }
    leftInfo = fcAnalyzeCoercion(b->left);
    rightInfo = fcAnalyzeCoercion(b->right);
    if (!ciRequestsAnyAuto(&leftInfo) && !ciRequestsAnyAuto(&rightInfo)) { *left = Infer(t, b->left); *right = Infer(t, b->right); return; }
    if (ciRequestsAnyAuto(&leftInfo) && ciRequestsAnyAuto(&rightInfo)) {
        const FcST* bothLeft = InferCoercionOperand(t, &leftInfo, b->left->line);
        const FcST* bothRight = InferCoercionOperand(t, &rightInfo, b->right->line);
        *left = AutoCoercionResult(t, b->left, &leftInfo, bothLeft, NULL, false, false);
        *right = AutoCoercionResult(t, b->right, &rightInfo, bothRight, NULL, false, false);
        return;
    }
    if (ciRequestsAnyAuto(&rightInfo)) {
        const FcST* anchor = Infer(t, b->left);
        const FcST* operand = InferCoercionOperand(t, &rightInfo, b->right->line);
        *left = anchor;
        *right = AutoCoercionResult(t, b->right, &rightInfo, operand, anchor, false, true);
    } else {
        const FcST* operand = InferCoercionOperand(t, &leftInfo, b->left->line);
        const FcST* anchor = Infer(t, b->right);
        *left = AutoCoercionResult(t, b->left, &leftInfo, operand, anchor, false, true);
        *right = anchor;
    }
}
static const FcST* InferBinary(FcTC* t, FcBinaryExpr* b) {
    const FcST* left;
    const FcST* right;
    const FcST* type;
    const FcU* unit;
    InferOperands(t, b, &left, &right);
    if (b->op == BOP_And || b->op == BOP_Or) {
        const char* op = b->op == BOP_And ? "&&" : "||";
        CheckLogicalOperand(t, left, op, b->h.line);
        CheckLogicalOperand(t, right, op, b->h.line);
    }
    type = InferBinaryType(t, b, left, right);
    if (type->kind == STK_Int && (left->wide | right->wide) != 0) { FcST* r = stCopy(TC_, type); r->wide = left->wide > right->wide ? left->wide : right->wide; type = r; }
    unit = BinaryUnit(t, b, left, right);
    return stIsNumber(type) ? stWithUnit(TC_, type, unit, false) : type;
}
static const char* BinOpText(FcBinaryOp op) {
    switch (op) {
        case BOP_Add: return "+"; case BOP_Sub: return "-"; case BOP_Mod: return "%"; case BOP_Eq: return "=="; case BOP_NotEq: return "!=";
        case BOP_Lt: return "<"; case BOP_LtEq: return "<="; case BOP_Gt: return ">"; case BOP_GtEq: return ">="; case BOP_BitAnd: return "&";
        case BOP_BitOr: return "|"; case BOP_BitXor: return "#";
        default: return fcBinaryOpNames[op];
    }
}
static const FcU* BinaryUnit(FcTC* t, FcBinaryExpr* b, const FcST* left, const FcST* right) {
    const FcU* l;
    const FcU* r;
    if (!stIsNumber(left) || !stIsNumber(right) || !left->unit || !right->unit || left->unitIsDimension || right->unitIsDimension) return NULL;
    l = left->unit; r = right->unit;
    switch (b->op) {
        case BOP_Add: case BOP_Sub: case BOP_Mod: case BOP_Eq: case BOP_NotEq: case BOP_Lt: case BOP_LtEq: case BOP_Gt: case BOP_GtEq:
        case BOP_BitAnd: case BOP_BitOr: case BOP_BitXor:
            if (!fcUCompatible(l, r)) {
                Report(t, TC_UnitCode, TF(t, "'%s' and '%s' cannot be combined with '%s' (another dimension)", UnitText(t, l), UnitText(t, r), BinOpText(b->op)), b->h.line);
                return NULL;
            }
            if (fcUEquals(l, r)) return l;
            return left->kind == STK_Int && right->kind == STK_Int ? (l->u.scale <= r->u.scale ? l : r) : l;
        case BOP_Mul: return fcUMultiply(TC_, l, r);
        case BOP_Div: return fcUDivide(TC_, l, r);
        case BOP_ShiftLeft: case BOP_ShiftRight: case BOP_ShiftRightLogical: return l;
        default: return NULL;
    }
}
static bool fcIsComparison(FcBinaryOp op) { return op == BOP_Eq || op == BOP_NotEq || op == BOP_Lt || op == BOP_LtEq || op == BOP_Gt || op == BOP_GtEq; }
static bool fcIsBitOp(FcBinaryOp op) { return op == BOP_BitAnd || op == BOP_BitOr || op == BOP_BitXor; }
static bool fcIsShift(FcBinaryOp op) { return op == BOP_ShiftLeft || op == BOP_ShiftRight || op == BOP_ShiftRightLogical; }
static const FcST* InferBinaryType(FcTC* t, FcBinaryExpr* b, const FcST* left, const FcST* right) {
    FcBinaryOp op = b->op;
    if (fcIsBitOp(op) && left->kind == STK_Enum && stEq(left, right) && FC_VALI(fcDictGet(&t->enumNames, left->cls))) return left;
    if (fcIsComparison(op) && (left->kind == STK_Enum || right->kind == STK_Enum)) return ST_BOOL;
    if (left->kind == STK_Enum) left = ST_INT;
    if (right->kind == STK_Enum) right = ST_INT;
    if ((left->kind == STK_Word || right->kind == STK_Word) && !(op == BOP_And || op == BOP_Or || fcIsComparison(op)) && left->kind != STK_String && right->kind != STK_String) {
        const FcST* other = left->kind == STK_Word ? right : left;
        if (other->kind == STK_Int || other->kind == STK_Word) return ST_WORD;
        if (other->kind == STK_Float && !(fcIsBitOp(op) || fcIsShift(op))) return ST_FLOAT;
        return ST_DYNAMIC;
    }
    switch (op) {
        case BOP_And: case BOP_Or: return ST_BOOL;
        case BOP_Eq: case BOP_NotEq: case BOP_Lt: case BOP_LtEq: case BOP_Gt: case BOP_GtEq:
            return left->kind == STK_Class || right->kind == STK_Class ? ST_DYNAMIC : ST_BOOL;
        case BOP_BitAnd: case BOP_BitOr: case BOP_BitXor: case BOP_ShiftLeft: case BOP_ShiftRight: case BOP_ShiftRightLogical:
            return left->kind == STK_Int && right->kind == STK_Int ? ST_INT : ST_DYNAMIC;
        default: break;
    }
    if (op == BOP_Add && (left->kind == STK_String || right->kind == STK_String)) {
        if (left->kind == STK_String) return ST_STRING;
        return stIsDynamic(left) || left->kind == STK_Class ? ST_DYNAMIC : ST_STRING;
    }
    if ((op == BOP_Add || op == BOP_Sub || op == BOP_Mul || op == BOP_Mod || op == BOP_Div || op == BOP_Power)
        && ((left->kind == STK_Float && right->eitherNumber) || (right->kind == STK_Float && left->eitherNumber))) return ST_FLOAT;
    switch (op) {
        case BOP_Add: case BOP_Sub: case BOP_Mul: case BOP_Mod:
            if (left->kind == STK_Int && right->kind == STK_Int) return ST_INT;
            if (stIsNumber(left) && stIsNumber(right)) return ST_FLOAT;
            return ST_DYNAMIC;
        case BOP_Power:
            if (left->kind == STK_Int && right->kind == STK_Int && !b->floatPower) return ST_INT;
            return stIsNumber(left) && stIsNumber(right) ? ST_FLOAT : ST_DYNAMIC;
        case BOP_Div:
            if (left->kind == STK_Int && right->kind == STK_Int) return ST_INT;
            return (left->kind == STK_Float && stIsNumber(right)) || (right->kind == STK_Float && stIsNumber(left)) ? ST_FLOAT : ST_DYNAMIC;
        default: return ST_DYNAMIC;
    }
}

static void CheckTakeIntoCopy(FcTC* t, const char* name, int line) {
    if (t->implicitCopies && fcDictHas(t->implicitCopies, name))
        Report(t, TC_TakeIntoCopyCode, TF(t, "'take' gives the ownership to '%s', which is an implicit copy (TC023): what it takes dies with the copy, not with the variable outside. Write 'taking %s' or 'handover %s' if that is meant.", name, name, name), line);
}

static bool fcIsTakeExpr(FcExpr* e) { return e->kind == EK_UnaryExpr && (((FcUnaryExpr*)e)->op == UOP_Take || ((FcUnaryExpr*)e)->op == UOP_TryTake); }
static void CheckIndexKind(FcTC* t, const FcST* target, const FcST* index, int line);

static const FcST* InferAssign(FcTC* t, FcAssignExpr* a) {
    const FcST* target;
    const FcST* value;
    const char* what;
    int i;
    if (a->target->kind == EK_IdentifierExpr && fcIsTakeExpr(a->value)) CheckTakeIntoCopy(t, ((FcIdentifierExpr*)a->target)->name, a->h.line);
    switch (a->target->kind) {
        case EK_IdentifierExpr: CheckImplicitThisAccess(t, (FcIdentifierExpr*)a->target, MU_Write); target = Lookup(t, ((FcIdentifierExpr*)a->target)->name); break;
        case EK_MemberExpr: target = InferMember(t, (FcMemberExpr*)a->target, MU_Write); break;
        case EK_IndexExpr: {
            FcIndexExpr* ix = (FcIndexExpr*)a->target;
            const FcST* tt = Infer(t, ix->target);
            FcMethodDecl* setter;
            FcDict* map;
            CheckIndexKind(t, tt, Infer(t, ix->index), ix->h.line);
            NoteReceiverTypeArgs(t, (FcExpr*)ix, tt);
            if (tt->kind == STK_Array) target = tt->element;
            else if (tt->kind == STK_Class && (setter = FindMethod(t, tt, "SetIndex", 2, &map)) != NULL) {
                FcLambdaParam* p1 = (FcLambdaParam*)setter->params->items[1];
                target = DeclaredOrDynamic(t, p1->type, p1->arrayRanks->count, map);
            } else target = ST_DYNAMIC;
            break;
        }
        case EK_UnaryExpr: target = ((FcUnaryExpr*)a->target)->op == UOP_Dereference ? InferUnary(t, (FcUnaryExpr*)a->target) : ST_DYNAMIC; break;
        default: target = ST_DYNAMIC; break;
    }
    value = InferExpected(t, a->value, target);
    if (a->target->kind == EK_IdentifierExpr && fcDictCount(&t->pendingVars) > 0) TypePendingVar(t, (FcIdentifierExpr*)a->target, value);
    what = a->target->kind == EK_IdentifierExpr ? TF(t, "'%s'", ((FcIdentifierExpr*)a->target)->name) : a->target->kind == EK_MemberExpr ? TF(t, "'%s'", ((FcMemberExpr*)a->target)->name)
        : a->target->kind == EK_IndexExpr ? "an element" : "the target";
    CheckAssignable(t, value, target, a->h.line, what, TC_AssignCode, a->value);
    if (a->target->kind == EK_IdentifierExpr) {
        FcTypeRef* localType = LocalType(t, ((FcIdentifierExpr*)a->target)->name);
        if (localType) GuardLocalValue(t, a->value, value, target, localType, ((FcIdentifierExpr*)a->target)->name);
    }
    if (a->target->kind == EK_IdentifierExpr && stIsValueKind(target) && target->nul != NUL_Defined)
        for (i = t->scopes->count - 1; i >= 0; i--) {
            void* cur;
            if (fcDictTry((FcDict*)t->scopes->items[i], ((FcIdentifierExpr*)a->target)->name, &cur)) {
                fcDictPut((FcDict*)t->scopes->items[i], ((FcIdentifierExpr*)a->target)->name,
                    (void*)stWithNull(TC_, (const FcST*)cur, value->nul == NUL_Defined || value->nul == NUL_Inferred ? NUL_Inferred : NUL_MayBeUndefined));
                break;
            }
        }
    if (a->target->kind == EK_IdentifierExpr && HoldsUndefinedAsObject(target) && target->nul != NUL_Defined)
        for (i = t->scopes->count - 1; i >= 0; i--) {
            void* cur;
            if (fcDictTry((FcDict*)t->scopes->items[i], ((FcIdentifierExpr*)a->target)->name, &cur)) {
                fcDictPut((FcDict*)t->scopes->items[i], ((FcIdentifierExpr*)a->target)->name,
                    (void*)stWithNull(TC_, (const FcST*)cur, value->nul == NUL_Defined || value->nul == NUL_Inferred ? NUL_Inferred : value->nul));
                break;
            }
        }
    if (a->value->kind == EK_UnaryExpr && ((FcUnaryExpr*)a->value)->op == UOP_TryTake) return stWithNull(TC_, ST_BOOL, NUL_Defined);
    return stIsDynamic(target) ? value : target;
}

static void CheckIndexKind(FcTC* t, const FcST* target, const FcST* index, int line) {
    if ((target->kind == STK_Array || target->kind == STK_String) && NeverKind(index, false))
        ReportError(t, TC_KindCode, TF(t, "An index must be an int, not a value of type '%s': the access always fails at run time", S_(t, index)), line);
}
static void CheckSizeKind(FcTC* t, const FcST* size, const char* what, int line) {
    if (NeverKind(size, false))
        ReportError(t, TC_KindCode, TF(t, "The size of %s must be an int, not a value of type '%s': 'new' always fails at run time", what, S_(t, size)), line);
}
static const FcST* InferIndex(FcTC* t, FcIndexExpr* ix) {
    const FcST* target = Infer(t, ix->target);
    FcMethodDecl* getter;
    FcDict* map;
    CheckIndexKind(t, target, Infer(t, ix->index), ix->h.line);
    NoteReceiverTypeArgs(t, (FcExpr*)ix, target);
    if (target->kind == STK_Array) return target->element;
    if (target->kind == STK_String) return ST_CHAR;
    if (target->kind == STK_Class && (getter = FindMethod(t, target, "GetIndex", 1, &map)) != NULL && getter->returnType) return DeclaredOrDynamic(t, getter->returnType, 0, map);
    return ST_DYNAMIC;
}

/* CheckSwitchOverEnum: the members of the enum as Resolver.EnumTypeOf has them */
static void CheckSwitchOverEnum(FcTC* t, const char* enumName, FcList* caseValues, int line) {
    FcEnumDecl* type = NULL;
    FcDict* covered = fcDictNew(TC_, FC_KEY_STR);
    FcDict* seenNames = fcDictNew(TC_, FC_KEY_STR);
    FcList* missing = fcListNew(TC_);
    FcBuf b = {0};
    int64_t next = 0;
    int i;
    for (i = 0; i < caseValues->count; i++) {
        FcResolvedRef* r = (FcResolvedRef*)fcDictGet(t->refs, caseValues->items[i]);
        if (r && r->kind == RR_EnumMember && r->enumType && streq0(r->enumType, enumName)) {
            type = (FcEnumDecl*)fcDictGet(&t->enumDecls, r->enumType);
            fcSetAdd(covered, fcFmt(TC_, "%" PRId64, r->value));
        } else return;
    }
    if (type == NULL) return;
    for (i = 0; i < type->members->count; i++) {
        FcEnumMember* m = (FcEnumMember*)type->members->items[i];
        int64_t value = m->valueExpr && m->valueExpr->kind == EK_LiteralExpr && ((FcLiteralExpr*)m->valueExpr)->value.kind == VK_Int ? ((FcLiteralExpr*)m->valueExpr)->value.i : next;
        next = value + 1;
        if (!fcSetAdd(seenNames, m->name)) continue;
        if (!fcDictHas(covered, fcFmt(TC_, "%" PRId64, value)) && !fcListHasStr(missing, m->name)) fcListAdd(TC_, missing, (void*)m->name);
    }
    if (missing->count == 0) return;
    for (i = 0; i < missing->count; i++) { if (i) fcBufStr(&b, ", "); fcBufStr(&b, (const char*)missing->items[i]); }
    Report(t, TC_SwitchMissingCode, TF(t, "The switch over '%s' has no case for %s (add the cases or a 'case default')", enumName, fcBufDone(TC_, &b)), line);
}

#include "typechecker3.c"
