/* native/forge/typechecker1.c - TypeChecker.cs, first part: the checker, reporting, assignability, the types of declarations, members and
 * methods, overloads (included by typechecker.c). */

#define TC_AssignCode "TC001"
#define TC_ArgumentCode "TC002"
#define TC_ReturnCode "TC003"
#define TC_UntypedCode "TC004"
#define TC_NoReturnTypeCode "TC005"
#define TC_UnitCode "TC006"
#define TC_UnitLossCode "TC007"
#define TC_UnknownMemberCode "TC008"
#define TC_AssignInConditionCode "TC009"
#define TC_SwitchMissingCode "TC010"
#define TC_NamedArgumentCode "TC011"
#define TC_AccessCode "TC012"
#define TC_ImpossibleCastCode "TC013"
#define TC_MissingOverrideCode "TC014"
#define TC_NothingToOverrideCode "TC015"
#define TC_InterfaceAccessorCode "TC016"
#define TC_RawUnitCode "TC017"
#define TC_SetterClaimCode "TC018"
#define TC_UntypedVarCode "TC019"
#define TC_LateVarTypeCode "TC020"
#define TC_WideIntCode "TC021"
#define TC_UndefinedCode "TC022"
#define TC_ImplicitCaptureCode "TC023"
#define TC_TakeIntoCopyCode "TC024"
#define TC_NeverMemberCode "TC025"
#define TC_RecordWriteCode "TC026"
#define TC_RecordFieldCode "TC027"
#define TC_WithWordCode "TC028"
#define TC_ChannelWordCode "TC029"
#define TC_ExpressionTreeCode "TC030"
#define TC_StoredTemporaryCode "TC031"
#define TC_ArrayToBufferCode "TC032"
#define TC_KindCode "TC033"
#define TC_TextConversionCode "TC034"
#define TC_BlockTakeLocalCode "TC035"
#define TC_StoredLocalCode "TC036"
#define TC_NumberTextCode "TC037"
#define TC_ReadonlyWriteCode "E4052"

static bool fcBufferMethodsHas(const char* name) {
    static const char* const kinds[] = { "Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "Float16", "Float32", "Float64" };
    size_t i;
    if (fcStartsWith(name, "Read")) for (i = 0; i < sizeof kinds / sizeof kinds[0]; i++) if (streq0(name + 4, kinds[i])) return true;
    if (fcStartsWith(name, "Write")) for (i = 0; i < sizeof kinds / sizeof kinds[0]; i++) if (streq0(name + 5, kinds[i])) return true;
    return streq0(name, "Fill") || streq0(name, "CopyTo") || streq0(name, "Slice") || streq0(name, "IndexOf") || streq0(name, "Compare");
}

/* TypeRef.UnitDemand (for the resolver) */
static const char* fcUnitDemandOf(FcCtx* c, const FcTypeRef* t) {
    const char* req = fcTrRequiredUnit(t);
    const char* unit;
    if (!req) return NULL;
    unit = fcUnitDemandText(c, req);
    return streq0(t->baseName, "float") ? fcFmt(c, "#%s", unit) : unit;
}

typedef enum FcMemberUse { MU_Read, MU_Write, MU_Call } FcMemberUse;
typedef struct FcKeptAt { const char* where; const char* store; const char* holder; } FcKeptAt;
typedef struct FcPendingTemp { int source; FcList* params; FcBlockStmt* body; FcList* baseArgs; FcList* argParams; FcList* args; const char* className; FcMethodDecl* method; } FcPendingTemp;
typedef struct FcPendingVar { FcDict* scope; const char* name; int line, source; } FcPendingVar;
/* (decl, map, args) of InterfaceChain / (decl, map) of Chain */
typedef struct FcChainEntry { void* decl; FcDict* map; FcList* args; } FcChainEntry;

typedef struct FcTC {
    FcCtx* c;
    FcDict* classes;              /* name -> ClassDecl* (ResolveResult.Classes) */
    FcDict interfaces;            /* name -> InterfaceDecl* */
    FcDict enumNames;             /* name -> flags (int) */
    FcDict enumDecls;             /* name -> EnumDecl* (the enum types of the resolver's references) */
    FcDict* refs;
    FcList* program;
    FcDict strictBySource;        /* "source" -> 1 strict / 2 lax */
    bool defaultStrict;
    int firstUserSource, reportFrom;
    FcList* diagnostics;
    FcList* scopes;               /* FcDict* name -> FcST* */
    FcClassDecl* cls;
    FcList* bodyTypeParams;
    bool inLambda, inBoundLambda;
    bool lambdaToTarget;          /* the lambda InferLambda gets next goes to a typed target (InferExpected): its untyped parameters take their types from it */
    const FcST* thisType;
    const FcST* returnType;
    FcTypeRef* returnTypeRef;
    const char* returnWhat;
    int source;
    FcList* collectedReturns;
    FcDict interfaceClosures;
    FcDict derivedClasses;
    FcDict pendingVars;           /* "scope\x01name" -> FcPendingVar* */
    FcDict mixedJoins;            /* Expr* -> fc8 */
    FcDict placeholderTypes;      /* ConditionalTargetExpr* -> FcST* */
    FcDict reportedLambdas;
    FcDict* implicitCopies;
    FcDict notedCalls;
    FcDict callTargets;           /* Expr* -> MethodDecl* */
    FcList* pendingTemporaries;
    FcDict* methodClasses;
    FcDict storedFields;          /* BlockStmt* -> FcDict* (name -> FcKeptAt*, or &fcNoKept) */
    FcDict returnsFresh;          /* MethodDecl* -> 1 false / 2 true */
    bool inConstructor;
    FcExpr* declaredFor;
    const FcST* declaredTarget;
    int programIntWidth, programFloatWidth, programWordWidth;
    FcDict* nativeReturns;
    bool reflectionKnown, reflectionUsed;
    FcResolveResult* rr;          /* what the check leaves for the compiler (UnitChecks, LocalGuards, FixedCoercions, OverloadHints, ...) */
    FcList* localTypes;           /* FcDict* name -> TypeRef* by scope (like scopes) */
    FcDict* treeTypes;            /* ExpressionTrees.Types while a tree is checked, NULL otherwise */
    FcDict* specializedClasses;   /* the classes with typed specializations (NoteReceiverTypeArgs), made on first use */
    /* the derivation of `defined` (Derive) */
    bool deriveDefined;
    FcDict calls;                 /* key -> FcCallInfo* */
    FcDict stringCalls;           /* key -> FcStrCalls* */
    FcDict returns;               /* MethodDecl* -> FcReturns* */
    FcList* retNotes;             /* FcRetNote*, NULL: not collecting */
    FcDict* plainParams;          /* name -> LambdaParam*, NULL */
    FcDict paramIds;              /* LambdaParam* -> "par:n" */
    FcDict unseenNames, unseenCtors, stringLiterals, baseClasses;
    FcDict exprTypes;             /* Expr* -> FcST* */
    FcDict* fieldCands;           /* name -> FcList* of FieldDecl*, NULL until made */
    FcDict fieldOwner;            /* FieldDecl* -> class name */
    FcDict otherFieldNames, fieldBad, fieldNeeds;
    FcDict* tlProperties;         /* TC036: the names of the properties of the program */
    FcList* tlProgramTop;         /* TC036: the statements of the top level of the program being walked */
    const FcST* memberReceiver;   /* TC037: the type of the target of the last member InferMember saw (its class for a static one) */
} FcTC;
static const FcKeptAt fcNoKept = { NULL, NULL, NULL };

#define TC_ (t->c)
static const char* TF(FcTC* t, const char* fmt, ...) {
    va_list ap;
    FcBuf b = {0};
    char small[512];
    int n;
    va_start(ap, fmt);
    n = vsnprintf(small, sizeof small, fmt, ap);
    va_end(ap);
    if (n < (int)sizeof small) return fcStr(TC_, small);
    b.p = (char*)malloc((size_t)n + 1);
    va_start(ap, fmt);
    vsnprintf(b.p, (size_t)n + 1, fmt, ap);
    va_end(ap);
    b.len = (size_t)n; b.cap = (size_t)n + 1;
    return fcBufDone(TC_, &b);
}
static const char* S_(FcTC* t, const FcST* s) { return stStr(TC_, s); }

/* ---- reporting ---- */
static bool Strict(FcTC* t) {
    int v = FC_VALI(fcDictGet(&t->strictBySource, fcFmt(TC_, "%d", t->source)));
    return v ? v == 1 : t->defaultStrict;
}
static void Report(FcTC* t, const char* code, const char* message, int line) {
    bool warningAlways;
    FcDiagnostic* d;
    if (t->source < t->reportFrom) return;
    warningAlways = streq0(code, TC_UnitLossCode) || streq0(code, TC_AssignInConditionCode) || streq0(code, TC_SwitchMissingCode) || streq0(code, TC_SetterClaimCode)
        || streq0(code, TC_LateVarTypeCode) || streq0(code, TC_TakeIntoCopyCode) || streq0(code, TC_StoredTemporaryCode) || streq0(code, TC_ArrayToBufferCode)
        || streq0(code, TC_TextConversionCode) || streq0(code, TC_BlockTakeLocalCode) || streq0(code, TC_StoredLocalCode) || streq0(code, TC_NumberTextCode);
    d = fcDiagNew(TC_, line, message, Strict(t) && !warningAlways ? FC_SEV_ERROR : FC_SEV_WARNING, code, NULL, 0);
    fcListAdd(TC_, t->diagnostics, d);
}
static void ReportError(FcTC* t, const char* code, const char* message, int line) {
    if (t->source >= t->reportFrom) fcListAdd(TC_, t->diagnostics, fcDiagNew(TC_, line, message, FC_SEV_ERROR, code, NULL, 0));
}

static const FcST* Infer(FcTC* t, FcExpr* expr);
static const FcST* InferExpected(FcTC* t, FcExpr* expr, const FcST* expected);
static bool Assignable(FcTC* t, const FcST* value, const FcST* target);
static const FcST* Declared(FcTC* t, const FcTypeRef* type, int arrayRanks, FcDict* map);
static const char* NameOf(FcTC* t, const FcTypeRef* baseRef);
static bool DerivesFrom(FcTC* t, const char* derived, const char* baseName);
static FcList* InterfaceClosure(FcTC* t, const char* name);
static bool IsInterface(FcTC* t, const char* name);
static FcList* Chain(FcTC* t, const FcST* receiver);
static FcList* InterfaceChain(FcTC* t, const FcST* receiver);
static const FcST* AsBase(FcTC* t, const FcST* value, const char* baseName);
static const char* PlainName(FcTC* t, const char* name) { return fcPlainName(TC_, name); }

static void CheckCondition(FcTC* t, FcExpr* condition) {
    const FcST* type;
    if (condition->kind == EK_AssignExpr) {
        FcAssignExpr* assign = (FcAssignExpr*)condition;
        bool compound = assign->value->kind == EK_BinaryExpr && ((FcBinaryExpr*)assign->value)->left == assign->target;
        bool tryTake = assign->value->kind == EK_UnaryExpr && ((FcUnaryExpr*)assign->value)->op == UOP_TryTake;
        if (!compound && !tryTake && !assign->parenthesized)
            Report(t, TC_AssignInConditionCode, "An assignment as a condition - did you mean '=='? (write '((a = b))' if the assignment is meant)", condition->line);
    }
    type = Infer(t, condition);
    if (!Assignable(t, type, ST_BOOL)) Report(t, TC_AssignCode, TF(t, "A condition must be a 'bool', not '%s'", S_(t, type)), condition->line);
}
static void CheckLogicalOperand(FcTC* t, const FcST* operand, const char* op, int line) {
    if (!Assignable(t, operand, ST_BOOL)) Report(t, TC_AssignCode, TF(t, "An operand of '%s' must be a 'bool', not '%s'", op, S_(t, operand)), line);
}

static const char* UnitText(FcTC* t, const FcU* unit) { return fcUIsUnitless(unit) ? "unitless" : fcUText(TC_, unit); }
static bool CutsWide(FcTC* t, const FcST* value, const FcST* target) {
    return value->kind == STK_Int && target->kind == STK_Int && !target->raw && value->wide > target->wide && t->programIntWidth > 0 && t->programIntWidth < 64 && value->wide > t->programIntWidth;
}
static void CheckUnitAssignable(FcTC* t, const FcST* value, const FcST* target, int line, const char* what, FcExpr* valueExpr);
static const char* DemandTextOf(FcTC* t, const FcU* unit);

/* TC037: a number (int, float, word, with a unit or without), a char or a bool given to a parameter of type 'string' - of a method, a constructor,
 * an operator, an indexer, the 'value' of a property's setter, a typed lambda, a default value. The run time makes it its text (DECISIONS D14) - at a
 * lambda also for a parameter without a type of its own, which takes it from the lambda's target (ContextParamType); a name or a key passed as a
 * number by mistake (`row[0]` for a 'string' indexer) is what it finds. Always a warning, in both modes. Nothing for
 * 'any' or an unknown type; a string, an enum or an object, and storage (a variable, a field, an element, a return value) are TC034 in strict mode. */
static const char* NumberTextKind(FcTC* t, const FcST* value) {
    if (value == NULL || stIsDynamic(value)) return NULL;
    switch (value->kind) {
        case STK_Int: case STK_Float: case STK_Word: return TF(t, "number ('%s')", S_(t, value));
        case STK_Char: return "char";
        case STK_Bool: return "bool";
        default: return NULL;
    }
}
static bool CheckNumberAsText(FcTC* t, const FcST* value, const FcST* target, const char* what, int line) {
    const char* kind;
    if (target == NULL || stIsDynamic(target) || target->kind != STK_String || !(kind = NumberTextKind(t, value))) return false;
    Report(t, TC_NumberTextCode, TF(t, "A %s is passed as text to %s, which is a 'string' - write '\"\" + x' (or 'x.ToString()') if the text is meant", kind, what), line);
    return true;
}

static void CheckAssignableAt(FcTC* t, const FcST* value, const FcST* target, int line, const char* what, const char* code, FcExpr* valueExpr, const char* parameter);
static void CheckAssignable(FcTC* t, const FcST* value, const FcST* target, int line, const char* what, const char* code, FcExpr* valueExpr) {
    CheckAssignableAt(t, value, target, line, what, code, valueExpr, streq0(code, TC_ArgumentCode) ? what : NULL);
}
/* parameter: the value is an argument, and this names the parameter (TC037 in place of TC034); NULL: it is not */
static void CheckAssignableAt(FcTC* t, const FcST* value, const FcST* target, int line, const char* what, const char* code, FcExpr* valueExpr, const char* parameter) {
    if (target->nul == NUL_Defined && value->nul == NUL_MayBeUndefined) {
        if (value->kind == STK_Undefined)
            Report(t, TC_UndefinedCode, TF(t, "undefined goes into %s, which is 'defined %s': that is an ArgumentException at run time", what, S_(t, target)), line);
        else {
            const char* name = valueExpr && valueExpr->kind == EK_IdentifierExpr ? ((FcIdentifierExpr*)valueExpr)->name : NULL;
            Report(t, TC_UndefinedCode, TF(t, "%s may be undefined, but %s is 'defined %s': it is checked at run time - give it a value that is defined, or write 'trust %s' if it certainly is",
                name ? TF(t, "'%s'", name) : "The value", what, S_(t, target), name ? name : "x"), line);
        }
    }
    if (value->kind == STK_Undefined && target->nul == NUL_Defined) return;
    if (value->kind == STK_Class && streq0(value->cls, "LambdaExpression") && target->kind == STK_Lambda && valueExpr != NULL) { fcSetAdd(t->rr->treeToCompiled, valueExpr); return; }
    if (!Assignable(t, value, target)) { Report(t, code, TF(t, "Cannot assign a value of type '%s' to %s of type '%s'", S_(t, value), what, S_(t, target)), line); return; }
    if (parameter && CheckNumberAsText(t, value, target, parameter, line)) { /* TC037 says it */ }
    else if (Strict(t) && target->kind == STK_String && !stIsDynamic(value) && value->kind != STK_String && value->kind != STK_Undefined)
        Report(t, TC_TextConversionCode, TF(t, "A value of type '%s' goes into %s, which is a 'string': it becomes its text (ToString()) - write '.ToString()' or '\"\" + ...' where that is meant", S_(t, value), what), line);
    if (CutsWide(t, value, target))
        Report(t, TC_WideIntCode, TF(t, "An int[%d] goes into %s, a plain int of %d bits in this program: it is cut - declare int[%d], or cut it on purpose (!int)", value->wide, what, t->programIntWidth, value->wide), line);
    CheckUnitAssignable(t, value, target, line, what, valueExpr);
}

static void CheckUnitAssignable(FcTC* t, const FcST* value, const FcST* target, int line, const char* what, FcExpr* valueExpr) {
    const FcU* from;
    const FcU* to;
    if (value->kind == STK_Array && target->kind == STK_Array && value->element && target->element) {
        CheckUnitAssignable(t, value->element, target->element, line, streq0(what, "an element") ? what : TF(t, "an element of %s", what), NULL);
        return;
    }
    if (target->raw) {
        if (stIsNumber(value) && value->unit && !fcUIsUnitless(value->unit))
            ReportError(t, TC_RawUnitCode, TF(t, "A value in '%s' cannot be stored in %s of type '%s': 'byte' and 'word' are raw data without a unit (take the number with '::unitless')", UnitText(t, value->unit), what, S_(t, target)), line);
        return;
    }
    if (valueExpr != NULL && target->kind == STK_Float && (stIsDynamic(value) || value->kind == STK_Int || value->kind == STK_Word))
        fcDictPut(t->rr->unitChecks, valueExpr, (void*)"#any");
    if (valueExpr != NULL && stIsNumber(target) && target->unit && fcUIsUnitless(target->unit) && !target->unitIsDimension
        && (stIsDynamic(value) || (stIsNumber(value) && !(value->unit && fcUIsUnitless(value->unit)))))
        fcDictPut(t->rr->unitChecks, valueExpr, (void*)(target->kind == STK_Float ? "#unitless" : "unitless"));
    if (valueExpr != NULL && stIsNumber(target) && target->unit && !fcUIsUnitless(target->unit) && !target->unitIsDimension
        && (stIsDynamic(value) || (stIsNumber(value) && !fcUEquals(target->unit, value->unit)))) {
        const char* demand = DemandTextOf(t, target->unit);
        if (demand) fcDictPut(t->rr->unitChecks, valueExpr, (void*)(target->kind == STK_Float ? TF(t, "#%s", demand) : demand));
    }
    if (!stIsNumber(value) || !stIsNumber(target) || !value->unit || !target->unit) return;
    from = value->unit; to = target->unit;
    if ((fcUIsUnitless(from) && to->u.unitlessConverter != NULL) || (fcUIsUnitless(to) && from->u.unitlessConverter != NULL)) return;
    if (!fcUCompatible(from, to)) {
        Report(t, TC_UnitCode, TF(t, "A value in '%s' does not fit %s in '%s%s'", UnitText(t, from), what, target->unitIsDimension ? "any " : "", UnitText(t, to)), line);
        return;
    }
    if (!target->unitIsDimension && value->kind == STK_Int && target->kind == STK_Int && !fcUEquals(from, to) && fcUIsLinear(from) && fcUIsLinear(to) && fcUConversionFactorTo(from, to) < 1)
        Report(t, TC_UnitLossCode, TF(t, "A whole number in '%s' converted into '%s' may lose its fraction (declare a float or a finer unit)", UnitText(t, from), UnitText(t, to)), line);
}

/* DemandText(Unit): the text when Unit.Parse reads the same unit back from it */
static const char* DemandTextOf(FcTC* t, const FcU* unit) {
    const char* text = fcUText(TC_, unit);
    volatile bool same = false;
    FC_TRY(TC_) {
        same = fcUEquals(fcUParse(TC_, text), unit);
    } FC_CATCH(TC_) {
        same = false;
    } FC_END_TRY;
    return same ? text : NULL;
}

static bool Assignable(FcTC* t, const FcST* value, const FcST* target) {
    if (stIsDynamic(value) || stIsDynamic(target) || value->kind == STK_Undefined) return true;
    if (target->kind == STK_String) return true;
    if (value->kind == STK_Array && target->kind == STK_Class && streq0(target->cls, "IEnumerable"))
        return !(target->args && target->args->count == 1) || Assignable(t, value->element, (const FcST*)target->args->items[0]);
    if (value->kind == STK_Array || target->kind == STK_Array) {
        if (value->kind != target->kind) return false;
        if (value->element->kind == STK_Class && value->element->cls && target->element->kind == STK_Class && target->element->cls) {
            const char* have = value->element->cls;
            const char* want = target->element->cls;
            if (!streq0(have, want) && (fcDictHas(t->classes, have) || IsInterface(t, have)) && (fcDictHas(t->classes, want) || IsInterface(t, want)) && !DerivesFrom(t, have, want)) return false;
        }
        return Assignable(t, value->element, target->element);
    }
    if (value->kind == STK_Lambda && target->kind == STK_Lambda) {
        int i;
        if (value->paramCount >= 0 && target->paramCount >= 0 && value->paramCount != target->paramCount) return false;
        if (value->params && target->params && value->params->count == target->params->count)
            for (i = 0; i < value->params->count; i++) if (!Assignable(t, (const FcST*)target->params->items[i], (const FcST*)value->params->items[i])) return false;
        return value->ret == NULL || target->ret == NULL || target->ret->kind == STK_Undefined || Assignable(t, value->ret, target->ret);
    }
    if (value->kind == STK_Int && target->kind == STK_Float) return true;
    if (target->kind == STK_Word) return value->kind == STK_Int || value->kind == STK_Word;
    if (value->kind == STK_Word) return target->kind == STK_Float || (target->kind == STK_Int && target->raw);
    if (target->kind == STK_Object) return value->kind == STK_Class || value->kind == STK_Object;
    if (value->kind == STK_Object) return target->kind == STK_Class;
    if (value->kind == STK_Class && target->kind == STK_Class) {
        const FcST* view;
        int i;
        if (!DerivesFrom(t, value->cls, target->cls) && !DerivesFrom(t, target->cls, value->cls)) return false;
        if (target->args == NULL) return true;
        view = streq0(value->cls, target->cls) ? value : AsBase(t, value, target->cls);
        if (view == NULL || view->args == NULL) return true;
        for (i = 0; i < view->args->count && i < target->args->count; i++) {
            const FcST* a = (const FcST*)view->args->items[i];
            const FcST* b = (const FcST*)target->args->items[i];
            if (stIsDynamic(a) || stIsDynamic(b) || stEq(a, b)) continue;
            if (a->kind == STK_Class && b->kind == STK_Class && a->args == NULL && b->args == NULL && DerivesFrom(t, a->cls, b->cls)) continue;
            return false;
        }
        return true;
    }
    if (value->kind == STK_Enum || target->kind == STK_Enum) return value->kind == target->kind && streq0(value->cls, target->cls);
    return value->kind == target->kind;
}

static bool DerivesFrom(FcTC* t, const char* derived, const char* baseName) {
    int guard, k;
    if (IsInterface(t, derived)) return fcListHasStr(InterfaceClosure(t, derived), baseName);
    for (guard = 0; guard < 64; guard++) {
        FcClassDecl* decl;
        const char* next = NULL;
        bool allKnown = true;
        if (streq0(derived, baseName)) return true;
        decl = (FcClassDecl*)fcDictGet(t->classes, derived);
        if (decl && decl->baseRefs)
            for (k = 0; k < decl->baseRefs->count; k++) {
                const char* n = NameOf(t, (FcTypeRef*)decl->baseRefs->items[k]);
                if (IsInterface(t, n) && fcListHasStr(InterfaceClosure(t, n), baseName)) return true;
            }
        if (!decl || !decl->baseRefs || decl->baseRefs->count == 0) return false;
        for (k = 0; k < decl->baseRefs->count; k++) if (streq0(PlainName(t, ((FcTypeRef*)decl->baseRefs->items[k])->baseName), baseName)) return true;
        for (k = 0; k < decl->baseRefs->count && !next; k++) {
            const char* n = PlainName(t, ((FcTypeRef*)decl->baseRefs->items[k])->baseName);
            if (fcDictHas(t->classes, n)) next = n;
        }
        if (next == NULL) {
            for (k = 0; k < decl->baseRefs->count; k++) if (!fcDictHas(t->classes, PlainName(t, ((FcTypeRef*)decl->baseRefs->items[k])->baseName))) allKnown = false;
            return !allKnown;
        }
        derived = next;
    }
    return true;
}

static bool IsInterface(FcTC* t, const char* name) { return name && fcDictHas(&t->interfaces, name) && !fcDictHas(t->classes, name); }

static FcList* InterfaceClosure(FcTC* t, const char* name) {
    FcList* known = (FcList*)fcDictGet(&t->interfaceClosures, name);
    FcList* result;
    FcList* pending;
    if (known) return known;
    result = fcListNew(TC_);
    pending = fcListNew(TC_);
    fcListAdd(TC_, pending, (void*)name);
    while (pending->count > 0) {
        const char* n = (const char*)pending->items[--pending->count];
        FcInterfaceDecl* id;
        int i;
        if (fcListHasStr(result, n)) continue;
        fcListAdd(TC_, result, (void*)n);
        if (!(id = (FcInterfaceDecl*)fcDictGet(&t->interfaces, n))) continue;
        if (id->baseRefs) for (i = id->baseRefs->count - 1; i >= 0; i--) fcListAdd(TC_, pending, (void*)NameOf(t, (FcTypeRef*)id->baseRefs->items[i]));
    }
    fcDictPut(&t->interfaceClosures, name, result);
    return result;
}

static FcDict* MapFor(FcTC* t, FcList* typeParams, FcList* args) {
    FcDict* map;
    int i;
    if (!typeParams || typeParams->count == 0) return NULL;
    map = fcDictNew(TC_, FC_KEY_STR);
    for (i = 0; i < typeParams->count; i++)
        fcDictPut(map, ((FcTypeParam*)typeParams->items[i])->name, args && i < args->count ? args->items[i] : (void*)ST_DYNAMIC);
    return map;
}
static FcList* fcMapArgs(FcTC* t, FcList* typeArgs, FcDict* map) {
    FcList* l;
    int i;
    if (!typeArgs) return NULL;
    l = fcListNew(TC_);
    for (i = 0; i < typeArgs->count; i++) { const FcST* d = Declared(t, (FcTypeRef*)typeArgs->items[i], 0, map); fcListAdd(TC_, l, (void*)(d ? d : ST_DYNAMIC)); }
    return l;
}

static FcChainEntry* fcEntry(FcTC* t, void* decl, FcDict* map, FcList* args) { FcChainEntry* e = (FcChainEntry*)fcAlloc(TC_, sizeof *e); e->decl = decl; e->map = map; e->args = args; return e; }

/* InterfaceChain */
static FcList* InterfaceChain(FcTC* t, const FcST* receiver) {
    FcList* out = fcListNew(TC_);
    FcDict* seen;
    FcList* qNames;
    FcList* qArgs;
    int head = 0, k;
    if (receiver->cls == NULL || !IsInterface(t, receiver->cls)) return out;
    seen = fcDictNew(TC_, FC_KEY_STR);
    qNames = fcListNew(TC_); qArgs = fcListNew(TC_);
    fcListAdd(TC_, qNames, (void*)receiver->cls); fcListAdd(TC_, qArgs, receiver->args);
    while (head < qNames->count) {
        const char* name = (const char*)qNames->items[head];
        FcList* args = (FcList*)qArgs->items[head];
        FcInterfaceDecl* decl;
        FcDict* map;
        head++;
        if (!fcSetAdd(seen, name) || !(decl = (FcInterfaceDecl*)fcDictGet(&t->interfaces, name))) continue;
        map = MapFor(t, decl->typeParams, args);
        fcListAdd(TC_, out, fcEntry(t, decl, map, args));
        if (decl->baseRefs)
            for (k = 0; k < decl->baseRefs->count; k++) {
                FcTypeRef* b = (FcTypeRef*)decl->baseRefs->items[k];
                fcListAdd(TC_, qNames, (void*)NameOf(t, b));
                fcListAdd(TC_, qArgs, fcMapArgs(t, b->typeArgs, map));
            }
    }
    return out;
}

static FcTypeRef* fcFirstClassBase(FcTC* t, FcClassDecl* decl) {
    int k;
    if (!decl->baseRefs) return NULL;
    for (k = 0; k < decl->baseRefs->count; k++) if (fcDictHas(t->classes, NameOf(t, (FcTypeRef*)decl->baseRefs->items[k]))) return (FcTypeRef*)decl->baseRefs->items[k];
    return NULL;
}

/* Chain: the class and its base classes with their maps */
static FcList* Chain(FcTC* t, const FcST* receiver) {
    FcList* out = fcListNew(TC_);
    const char* name = receiver->cls;
    FcList* args = receiver->args;
    int guard;
    FcClassDecl* decl;
    for (guard = 0; guard < 64 && name != NULL && (decl = (FcClassDecl*)fcDictGet(t->classes, name)) != NULL; guard++) {
        FcDict* map = MapFor(t, decl->typeParams, args);
        FcTypeRef* baseRef;
        fcListAdd(TC_, out, fcEntry(t, decl, map, NULL));
        baseRef = fcFirstClassBase(t, decl);
        if (!baseRef) break;
        name = NameOf(t, baseRef);
        args = fcMapArgs(t, baseRef->typeArgs, map);
    }
    return out;
}

static const char* GenericName(FcTC* t, const char* name, int count) {
    const char* mangled = fcMangle(TC_, name, count);
    return fcDictHas(t->classes, mangled) || fcDictHas(&t->interfaces, mangled) ? mangled : name;
}

typedef struct FcTcKnown { FcTC* t; int count; } FcTcKnown;
static bool tcKnown(FcTC* t, const char* n) { return fcDictHas(t->classes, n) || fcDictHas(&t->interfaces, n); }
static bool kTcKnownMangled(void* self, const char* n) {
    FcTcKnown* k = (FcTcKnown*)self;
    return tcKnown(k->t, n) || (k->count > 0 && tcKnown(k->t, fcMangle(k->t->c, n, k->count)));
}
static bool kTcKnown(void* self, const char* n) { return tcKnown((FcTC*)self, n); }

/* NameOf(TypeRef) */
static const char* NameOf(FcTC* t, const FcTypeRef* baseRef) {
    int count = baseRef->typeArgs ? baseRef->typeArgs->count : 0;
    FcTypeRef copy = *baseRef;
    FcTcKnown k;
    FcKnown kn;
    const char* name;
    copy.baseName = PlainName(t, baseRef->baseName);
    k.t = t; k.count = count;
    kn.fn = kTcKnownMangled; kn.self = &k;
    name = fcTrResolveBaseName(TC_, &copy, kn);
    return count > 0 ? GenericName(t, name, count) : name;
}

static const FcST* AsBase(FcTC* t, const FcST* value, const char* baseName) {
    FcList* ic = InterfaceChain(t, value);
    FcList* chain;
    int i, k, j;
    for (i = 0; i < ic->count; i++) {
        FcChainEntry* e = (FcChainEntry*)ic->items[i];
        if (streq0(((FcInterfaceDecl*)e->decl)->name, baseName)) return stClassOf(TC_, baseName, e->args);
    }
    chain = Chain(t, value);
    for (i = 0; i < chain->count; i++) {
        FcChainEntry* e = (FcChainEntry*)chain->items[i];
        FcClassDecl* decl = (FcClassDecl*)e->decl;
        if (streq0(decl->name, baseName)) {
            FcList* args = NULL;
            if (e->map) { args = fcListNew(TC_); for (j = 0; j < decl->typeParams->count; j++) fcListAdd(TC_, args, fcDictGet(e->map, ((FcTypeParam*)decl->typeParams->items[j])->name)); }
            return stClassOf(TC_, decl->name, args);
        }
        if (decl->baseRefs)
            for (k = 0; k < decl->baseRefs->count; k++) {
                FcTypeRef* b = (FcTypeRef*)decl->baseRefs->items[k];
                FcList* args = fcMapArgs(t, b->typeArgs, e->map);
                const FcST* via;
                if (streq0(NameOf(t, b), baseName)) return stClassOf(TC_, baseName, args);
                if (IsInterface(t, NameOf(t, b)) && (via = AsBase(t, stClassOf(TC_, NameOf(t, b), args), baseName)) != NULL) return via;
            }
    }
    return NULL;
}

static FcMethodDecl* FindMethod(FcTC* t, const FcST* receiver, const char* name, int argc, FcDict** map);
static const FcST* ElementOf(FcTC* t, const FcST* iterable) {
    const FcST* b;
    FcMethodDecl* m;
    FcDict* map;
    if (iterable->kind == STK_Array) return iterable->element;
    if (iterable->kind == STK_String) return ST_CHAR;
    if (iterable->kind != STK_Class) return ST_DYNAMIC;
    if ((b = AsBase(t, iterable, "IEnumerable")) != NULL && b->args && b->args->count == 1) return (const FcST*)b->args->items[0];
    if (streq0(iterable->cls, "IEnumerable") && iterable->args && iterable->args->count == 1) return (const FcST*)iterable->args->items[0];
    if ((m = FindMethod(t, iterable, "GetEnumerator", 0, &map)) != NULL && m->returnType) {
        const FcST* enumerator = Declared(t, m->returnType, 0, map);
        if (enumerator && enumerator->kind == STK_Class) {
            FcDict* currentMap;
            FcMethodDecl* cur;
            if (streq0(enumerator->cls, "IEnumerator") && enumerator->args && enumerator->args->count == 1) return (const FcST*)enumerator->args->items[0];
            if ((b = AsBase(t, enumerator, "IEnumerator")) != NULL && b->args && b->args->count == 1) return (const FcST*)b->args->items[0];
            if ((cur = FindMethod(t, enumerator, "GetCurrent", 0, &currentMap)) != NULL && cur->returnType) {
                const FcST* d = Declared(t, cur->returnType, 0, currentMap);
                return d ? d : ST_DYNAMIC;
            }
        }
    }
    return ST_DYNAMIC;
}

static const char* EnumNamed(FcTC* t, const char* name) {
    const char* dot;
    if (fcDictHas(&t->enumNames, name)) return name;
    if (t->cls && (dot = strrchr(t->cls->name, '.')) != NULL && dot > t->cls->name) {
        const char* full = fcFmt(TC_, "%.*s.%s", (int)(dot - t->cls->name), t->cls->name, name);
        if (fcDictHas(&t->enumNames, full)) return full;
    }
    return NULL;
}
static const char* TypeParamRef(FcTC* t, const char* name) {
    int k;
    if (t->bodyTypeParams)
        for (k = 0; k < t->bodyTypeParams->count; k++) if (streq0(((FcTypeParam*)t->bodyTypeParams->items[k])->name, name)) return fcFmt(TC_, "$%d@", k);
    if (t->cls && t->cls->typeParams)
        for (k = 0; k < t->cls->typeParams->count; k++) if (streq0(((FcTypeParam*)t->cls->typeParams->items[k])->name, name)) return fcFmt(TC_, "$%d@%s", k, t->cls->name);
    return NULL;
}
static const FcST* FromName(FcTC* t, const char* name, FcDict* map) {
    void* sub;
    const char* plain;
    const char* e;
    const char* tp;
    if (map && fcDictTry(map, name, &sub)) return (const FcST*)sub;
    if (streq0(name, "int") || streq0(name, "byte")) return ST_INT;
    if (streq0(name, "word")) return ST_WORD;
    if (streq0(name, "float")) return ST_FLOAT;
    if (streq0(name, "bool")) return ST_BOOL;
    if (streq0(name, "char")) return ST_CHAR;
    if (streq0(name, "string")) return ST_STRING;
    if (streq0(name, "lambda")) return stLambdaOf(TC_, 0, NULL, NULL);
    if (streq0(name, "class")) return ST_OBJECT;
    if (streq0(name, "undefined")) return ST_UNDEFINED;
    if (streq0(name, "any")) return ST_ANY;
    plain = PlainName(t, name);
    if (tcKnown(t, plain)) return stClassOf(TC_, plain, NULL);
    if ((e = EnumNamed(t, name)) != NULL) return stEnumOf(TC_, e);
    if ((tp = TypeParamRef(t, name)) != NULL) { FcST* r = stCopy(TC_, ST_DYNAMIC); r->typeParam = tp; return r; }
    return ST_DYNAMIC;
}
static const FcST* SignatureName(FcTC* t, const char* name, const FcLambdaSignature* signature, FcDict* map);
static const FcST* SignatureParam(FcTC* t, const FcTypeRef* ref, FcDict* map);
static const FcST* DeclaredLambda(FcTC* t, const FcLambdaSignature* signature, FcDict* map) {
    FcList* ps;
    int i;
    if (signature == NULL) return stLambdaOf(TC_, 0, NULL, NULL);
    if (signature->isExpression) return FromName(t, "LambdaExpression", NULL);
    if (signature->isSelector) {
        if (signature->paramTypeNames->count == 1) {
            ps = fcListNew(TC_);
            fcListAdd(TC_, ps, (void*)SignatureName(t, (const char*)signature->paramTypeNames->items[0], signature, map));
            return stLambdaOf(TC_, 1, NULL, ps);
        }
        return stLambdaOf(TC_, -1, NULL, NULL);
    }
    ps = fcListNew(TC_);
    for (i = 0; i < signature->paramTypeNames->count; i++) {
        const char* name = (const char*)signature->paramTypeNames->items[i];
        const FcTypeRef* ref = signature->paramTypes && i < signature->paramTypes->count ? (const FcTypeRef*)signature->paramTypes->items[i] : NULL;
        const FcST* param;
        if (ref != NULL) param = SignatureParam(t, ref, map);
        else {
            param = SignatureName(t, name, signature, map);
            /* `lambda<byte>`: a byte, as the parameter `(byte b)` is (raw, unitless) - not any int */
            if (streq0(name, "byte") && !(map && fcDictHas(map, name))) { FcST* r = stCopy(TC_, param); r->unit = FC_UNITLESS; r->unitIsDimension = false; r->raw = true; param = r; }
        }
        fcListAdd(TC_, ps, (void*)param);
    }
    {
        FcST* r = stCopy(TC_, stLambdaOf(TC_, signature->paramTypeNames->count, signature->returnTypeName ? SignatureName(t, signature->returnTypeName, signature, map) : NULL, ps));
        r->lambdaShape = true;
        return r;
    }
}
/* SignatureName: a name in a lambda type (`Inner lambda<Inner>`), resolved where the type stands - its namespace and the #usings, as Declared does for
 * every other type there; a type parameter in scope, a type argument (map), a primitive as FromName */
static const FcST* SignatureName(FcTC* t, const char* name, const FcLambdaSignature* signature, FcDict* map) {
    const FcST* result = FromName(t, name, map);
    if (result->kind == STK_Dynamic && !result->declaredAny && result->typeParam == NULL && signature->namespaces && signature->namespaces->count > 0) {
        FcTypeRef ref;
        const char* resolved;
        memset(&ref, 0, sizeof ref);
        ref.baseName = name; ref.bitWidth = FC_NULL_INT; ref.namespaces = signature->namespaces;
        resolved = NameOf(t, &ref);
        if (!streq0(resolved, name) && tcKnown(t, resolved)) result = stClassOf(TC_, resolved, NULL);
    }
    return result;
}
/* SignatureParam: a parameter type of a lambda type as written - Declared, as the parameter `(int x[])`, `(List<int> l)`, `(int[16] x)`,
 * `(int lambda<string> f)` is - except that a number without a unit written stays without a known unit, as `lambda<int>` always was */
static const FcST* SignatureParam(FcTC* t, const FcTypeRef* ref, FcDict* map) {
    /* (the element of an array as a type of its own, so that it keeps its width: `lambda<int[16][]>` is `(int[16] a[])`) */
    FcTypeRef element = *ref;
    const FcST* result;
    int k;
    element.arrayRank = 0;
    if ((result = Declared(t, &element, 0, map)) == NULL) return ST_DYNAMIC;
    if (stIsNumber(result) && !result->raw && ref->unit == NULL && !(map && fcDictHas(map, ref->baseName))) result = stWithUnit(TC_, result, NULL, false);
    for (k = 0; k < ref->arrayRank; k++) result = stArrayOf(TC_, result);
    return result;
}

static bool IsRawType(const FcTypeRef* type) {
    return type != NULL && type->pointerDepth == 0 && type->lambdaSignature == NULL
        && ((streq0(type->baseName, "int") && type->bitWidth == 8 && type->isUnsigned) || streq0(type->baseName, "word"));
}

static const FcST* Declared(FcTC* t, const FcTypeRef* type, int arrayRanks, FcDict* map) {
    const FcST* result;
    bool substituted;
    int i, ranks;
    if (type == NULL || fcTrIsInferred(type)) return NULL;
    if (type->pointerDepth > 0) return ST_DYNAMIC;
    result = streq0(type->baseName, "lambda") ? DeclaredLambda(t, type->lambdaSignature, map) : FromName(t, type->baseName, map);
    if (result->kind == STK_Dynamic && !result->declaredAny && result->typeParam == NULL && type->namespaces && type->namespaces->count > 0) {
        const char* resolved = NameOf(t, type);
        if (!streq0(resolved, type->baseName) && tcKnown(t, resolved)) result = stClassOf(TC_, resolved, NULL);
    }
    if (type->typeArgs && type->typeArgs->count > 0 && result->kind == STK_Class) result = stClassOf(TC_, NameOf(t, type), fcMapArgs(t, type->typeArgs, map));
    substituted = map != NULL && fcDictHas(map, type->baseName);
    ranks = arrayRanks + type->arrayRank;
    if (stIsNumber(result) && (ranks == 0 || type->unit != NULL) && !(substituted && type->unit == NULL)) {
        if (type->unit == NULL) result = stWithUnit(TC_, result, FC_UNITLESS, false);
        else if (streq0(type->unit, "any")) { FcST* r = stCopy(TC_, result); r->anyUnit = true; result = r; }
        else if (fcStartsWith(type->unit, "any ")) result = stWithUnit(TC_, result, fcUParse(TC_, type->unit + 4), true);
        else result = stWithUnit(TC_, result, fcUParse(TC_, type->unit), false);
    }
    if (IsRawType(type)) { FcST* r = stCopy(TC_, result); r->unit = FC_UNITLESS; r->unitIsDimension = false; r->raw = true; result = r; }
    if (result->kind == STK_Int && streq0(type->baseName, "int") && (type->bitWidth == 32 || type->bitWidth == 64) && !type->isUnsigned) { FcST* r = stCopy(TC_, result); r->wide = type->bitWidth; result = r; }
    if ((result->kind == STK_Int || result->kind == STK_Float) && type->bitWidth != FC_NULL_INT && ranks == 0) { FcST* r = stCopy(TC_, result); r->bits = type->bitWidth & 0xFF; result = r; }
    if (type->nonNull && (ranks == 0 || stIsValueKind(result))) result = stWithNull(TC_, result, NUL_Defined);
    for (i = 0; i < ranks; i++) result = stArrayOf(TC_, result);
    return result;
}
static const FcST* DeclaredOrDynamic(FcTC* t, const FcTypeRef* type, int arrayRanks, FcDict* map) { const FcST* d = Declared(t, type, arrayRanks, map); return d ? d : ST_DYNAMIC; }

static bool HoldsUndefinedAsObject(const FcST* type) { return type->kind == STK_Class || type->kind == STK_Object || type->kind == STK_String || type->kind == STK_Enum; }
static const FcST* MayBeUndefined(FcTC* t, const FcST* type) { return stIsValueKind(type) && type->nul == NUL_Unknown ? stWithNull(TC_, type, NUL_MayBeUndefined) : type; }

static void CheckRawUnit(FcTC* t, const FcTypeRef* type, const char* name, int line) {
    if (IsRawType(type) && type->unit != NULL)
        ReportError(t, TC_RawUnitCode, TF(t, "'%s' is a '%s': raw data without a unit - it cannot be declared in '%s' (use an int or a float for a quantity)", name, streq0(type->baseName, "word") ? "word" : "byte", type->unit), line);
}

static const FcST* WithInitializerUnit(FcTC* t, const FcST* declared, const FcTypeRef* type, int arrayRanks, const FcST* init) {
    if (stIsNumber(declared) && !declared->raw && arrayRanks == 0 && type && type->unit == NULL && !declared->unitIsDimension) {
        if (stIsNumber(init) && init->unit) return stWithUnit(TC_, declared, init->unit, false);
        if (stIsDynamic(init) || stIsNumber(init)) return stWithUnit(TC_, declared, NULL, false);
    }
    return declared;
}
static bool IsConstantValue(FcExpr* e) {
    switch (e->kind) {
        case EK_LiteralExpr: return ((FcLiteralExpr*)e)->value.kind != VK_Undefined;
        case EK_UnaryExpr: return ((FcUnaryExpr*)e)->op == UOP_Negate && IsConstantValue(((FcUnaryExpr*)e)->operand);
        case EK_UnitCoerceExpr: return IsConstantValue(((FcUnitCoerceExpr*)e)->operand);
        default: return false;
    }
}
/* the unit of a literal (Value.Unit: the text the parser kept, NULL: unitless) */
static const FcU* fcLiteralUnit(FcTC* t, const FcValue* v) { (void)t; return v->unitv ? v->unitv : FC_UNITLESS; }
static const FcST* LiteralType(FcTC* t, FcExpr* e) {
    if (e->kind == EK_LiteralExpr && ((FcLiteralExpr*)e)->value.kind == VK_Int) return stWithUnit(TC_, ST_INT, fcLiteralUnit(t, &((FcLiteralExpr*)e)->value), false);
    if (e->kind == EK_LiteralExpr && ((FcLiteralExpr*)e)->value.kind == VK_Float) return stWithUnit(TC_, ST_FLOAT, fcLiteralUnit(t, &((FcLiteralExpr*)e)->value), false);
    if (e->kind == EK_UnaryExpr && ((FcUnaryExpr*)e)->op == UOP_Negate) return LiteralType(t, ((FcUnaryExpr*)e)->operand);
    if (e->kind == EK_UnitCoerceExpr && ((FcUnitCoerceExpr*)e)->targetUnitName) {
        const FcST* lt = LiteralType(t, ((FcUnitCoerceExpr*)e)->operand);
        if (stIsNumber(lt)) return stWithUnit(TC_, lt, fcUParse(TC_, ((FcUnitCoerceExpr*)e)->targetUnitName), false);
    }
    return ST_DYNAMIC;
}
static const FcST* FieldType(FcTC* t, FcFieldDecl* f, FcDict* map) {
    const FcST* declared = DeclaredOrDynamic(t, f->type, f->arrayRanks->count, map);
    const FcST* typed;
    if (f->initializer == NULL) return declared;
    typed = WithInitializerUnit(t, declared, f->type, f->arrayRanks->count, LiteralType(t, f->initializer));
    if (f->isStatic && f->isReadonly && f->arrayRanks->count == 0 && stIsValueKind(typed) && IsConstantValue(f->initializer)) typed = stWithNull(TC_, typed, NUL_Defined);
    return typed;
}

static const FcST* ThisClass(FcTC* t) {
    if (t->cls->name[0] == '$') return streq0(t->cls->name, "$array") ? stArrayOf(TC_, ST_DYNAMIC) : FromName(t, t->cls->name + 1, NULL);
    if (t->cls->typeParams && t->cls->typeParams->count > 0 && !t->inLambda) {
        FcList* args = fcListNew(TC_);
        int k;
        for (k = 0; k < t->cls->typeParams->count; k++) { FcST* a = stCopy(TC_, ST_DYNAMIC); a->typeParam = fcFmt(TC_, "$%d@%s", k, t->cls->name); fcListAdd(TC_, args, a); }
        return stClassOf(TC_, t->cls->name, args);
    }
    return stClassOf(TC_, t->cls->name, NULL);
}

static void ReportPendingVars(FcTC* t, FcDict* scope);
static void Push(FcTC* t) { fcListAdd(TC_, t->scopes, fcDictNew(TC_, FC_KEY_STR)); }
static void Pop(FcTC* t) {
    ReportPendingVars(t, (FcDict*)t->scopes->items[t->scopes->count - 1]);
    t->scopes->count--;
    if (t->localTypes->count > t->scopes->count) t->localTypes->count = t->scopes->count;
}
static void ReportUntypedVar(FcTC* t, const char* name, int line, const char* why) {
    if (Strict(t))
        Report(t, TC_UntypedVarCode, TF(t, "The type of '%s' cannot be derived: %s - give it a type ('int %s = ...'), or 'any %s = ...' if every kind of value is meant", name, why, name, name), line);
    else
        Report(t, TC_UntypedVarCode, TF(t, "'%s' has no type: %s. It becomes 'any' - nothing about it is checked, every use is looked up at run time and it takes the wide values of the native backend. Give it a type ('int %s = ...'), or write 'any %s = ...' if that is really meant", name, why, name, name), line);
}
static void ReportPendingVars(FcTC* t, FcDict* scope) {
    FcList* keys = fcListNew(TC_);
    int i;
    FC_DICT_EACH(&t->pendingVars, e) if (((FcPendingVar*)e->value)->scope == scope) fcListAdd(TC_, keys, (void*)e->key);
    for (i = 0; i < keys->count; i++) {
        FcPendingVar* pv = (FcPendingVar*)fcDictGet(&t->pendingVars, keys->items[i]);
        int previous = t->source;
        fcDictRemove(&t->pendingVars, keys->items[i]);
        t->source = pv->source;
        ReportUntypedVar(t, pv->name, pv->line, TF(t, "'var %s = undefined' says nothing about its type, and no assignment gives it one", pv->name));
        t->source = previous;
    }
}
static const char* fcPendingKey(FcTC* t, FcDict* scope, const char* name) { return fcFmt(TC_, "%p\x01%s", (void*)scope, name); }

static void Declare(FcTC* t, const char* name, const FcST* type) {
    fcDictPut((FcDict*)t->scopes->items[t->scopes->count - 1], name, (void*)type);
    if (t->localTypes->count == t->scopes->count) fcDictRemove((FcDict*)t->localTypes->items[t->localTypes->count - 1], name);
}

static void NoteCallFull(FcTC* t, FcExpr* call, const char* key, FcList* parameters, FcList* argParams, FcList* args, FcList* argExprs, FcMethodDecl* target);
static bool fcDeriveDefined(void);
static void Derive(FcTC* t, FcResolveResult* resolved);
static void NoteReturn(FcTC* t, FcExpr* value, const FcST* type);
static FcDict* PlainParams(FcTC* t, FcList* parameters, FcBlockStmt* body);
static void NoteFieldWrite(FcTC* t, FcExpr* target, const char* name, FcExpr* value, const FcST* type);
static bool IsFieldRef(FcTC* t, FcIdentifierExpr* id);
static const char* MethodKey(FcTC* t, const char* name, int count);
static const char* CtorKey(FcTC* t, const char* cls, int count);
typedef struct FcReturns FcReturns;
static FcReturns* fcReturnsNew(FcTC* t, FcList* notes, bool ends);

/* DeclareLocalType: a local declared with int/float/bool, a class, an interface or a type parameter (scope -1: the innermost) */
static void DeclareLocalType(FcTC* t, const char* name, FcTypeRef* type, int scope) {
    if (scope < 0) scope = t->scopes->count - 1;
    while (t->localTypes->count <= scope) fcListAdd(TC_, t->localTypes, fcDictNew(TC_, FC_KEY_STR));
    fcDictPut((FcDict*)t->localTypes->items[scope], name, type);
}
static FcTypeRef* fcTcTypeRef(FcTC* t, const char* name, int bitWidth) {
    FcTypeRef* r = (FcTypeRef*)fcAlloc(TC_, sizeof *r);
    r->baseName = name;
    r->bitWidth = bitWidth;
    return r;
}
/* NoteReceiverTypeArgs: a receiver of a class with typed specializations whose type arguments are plain value types or string (`List<int>`) */
static void NoteReceiverTypeArgs(FcTC* t, FcExpr* at, const FcST* receiver) {
    FcBuf b = {0};
    int i, k;
    if (receiver->kind != STK_Class || receiver->cls == NULL || !receiver->args || receiver->args->count == 0) return;
    if (!t->specializedClasses) {
        t->specializedClasses = fcDictNew(TC_, FC_KEY_STR);
        FC_DICT_EACH(t->classes, e) {
            FcClassDecl* cd = (FcClassDecl*)e->value;
            for (k = 0; k < cd->members->count; k++) {
                FcStmt* m = (FcStmt*)cd->members->items[k];
                if (m->kind == SK_MethodDecl && ((FcMethodDecl*)m)->specialization != NULL) { fcSetAdd(t->specializedClasses, cd->name); break; }
            }
        }
    }
    if (!fcDictHas(t->specializedClasses, receiver->cls)) return;
    fcBufStr(&b, "");
    for (i = 0; i < receiver->args->count; i++) {
        const FcST* a = (const FcST*)receiver->args->items[i];
        bool plain = a->wide == 0 && !a->unitIsDimension && (a->unit == NULL || fcUIsUnitless(a->unit));
        const char* text = a->kind == STK_Int && plain ? "int" : a->kind == STK_Float && plain ? "float" : a->kind == STK_Bool ? "bool" : a->kind == STK_Char ? "char"
            : a->kind == STK_String ? "string" : NULL;
        if (!text) { fcBufFree(&b); return; }
        if (i) fcBufChar(&b, '|');
        fcBufStr(&b, text);
    }
    fcDictPut(t->rr->receiverTypeArgs, at, (void*)fcBufDone(TC_, &b));
}

/* GuardTypeOf */
static FcTypeRef* GuardTypeOf(FcTC* t, const FcST* type) {
    switch (type->kind) {
        case STK_Int: return type->raw ? NULL : fcTcTypeRef(t, "int", type->wide == 0 ? FC_NULL_INT : type->wide);
        case STK_Float: return fcTcTypeRef(t, "float", FC_NULL_INT);
        case STK_Bool: return fcTcTypeRef(t, "bool", FC_NULL_INT);
        case STK_Class: {
            FcClassDecl* decl;
            FcInterfaceDecl* iface;
            if (type->args != NULL || type->cls == NULL) return NULL;
            if ((decl = (FcClassDecl*)fcDictGet(t->classes, type->cls)) != NULL) return decl->typeParams && decl->typeParams->count > 0 ? NULL : fcTcTypeRef(t, type->cls, FC_NULL_INT);
            if ((iface = (FcInterfaceDecl*)fcDictGet(&t->interfaces, type->cls)) != NULL) return iface->typeParams && iface->typeParams->count > 0 ? NULL : fcTcTypeRef(t, type->cls, FC_NULL_INT);
            return NULL;
        }
        case STK_Enum: return type->cls ? fcTcTypeRef(t, type->cls, FC_NULL_INT) : NULL;
        default: return NULL;
    }
}
/* LocalType: the declaration of a local variable, if it was declared with a type that is checked */
static FcTypeRef* LocalType(FcTC* t, const char* name) {
    int i;
    for (i = t->scopes->count - 1; i >= 0; i--)
        if (fcDictHas((FcDict*)t->scopes->items[i], name)) return i < t->localTypes->count ? (FcTypeRef*)fcDictGet((FcDict*)t->localTypes->items[i], name) : NULL;
    return NULL;
}
static bool DerivesFrom(FcTC* t, const char* derived, const char* baseName);
static bool fcGuardNeeded(FcTC* t, const FcST* value, const FcST* target) {
    switch (target->kind) {
        case STK_Int: return (value->kind != STK_Int && !(target->raw && value->kind == STK_Word)) || CutsWide(t, value, target);
        case STK_Float: return value->kind != STK_Float && value->kind != STK_Int;
        case STK_Bool: return value->kind != STK_Bool;
        case STK_Class: return value->kind != STK_Class || !DerivesFrom(t, value->cls, target->cls);
        case STK_Enum: return value->kind != STK_Enum || !streq0(value->cls, target->cls);
        case STK_Dynamic: return target->typeParam != NULL && !streq0(value->typeParam, target->typeParam);
        case STK_String: return value->kind != STK_String;
        default: return false;
    }
}
static void fcPutGuard(FcTC* t, FcExpr* valueExpr, FcTypeRef* type, const char* what) {
    FcLocalGuard* g = (FcLocalGuard*)fcAlloc(TC_, sizeof *g);
    g->type = type; g->what = what;
    fcDictPut(t->rr->localGuards, valueExpr, g);
}
/* GuardLocalValue: a value going into a local declared `target` that its static type does not prove - checked at run time (LocalGuards) */
static void GuardLocalValue(FcTC* t, FcExpr* valueExpr, const FcST* value, const FcST* target, FcTypeRef* type, const char* name) {
    bool undefinedChecked = type->nonNull && value->nul != NUL_Defined;
    if (value->kind == STK_Undefined && !undefinedChecked) return;
    if (undefinedChecked || fcGuardNeeded(t, value, target)) fcPutGuard(t, valueExpr, type, TF(t, "Variable '%s'", name));
}
/* GuardValue: like GuardLocalValue, for another declared target (a return value) */
static void GuardValue(FcTC* t, FcExpr* valueExpr, const FcST* value, const FcST* target, FcTypeRef* type, const char* what) {
    if (value->kind == STK_Undefined) return;
    if (fcGuardNeeded(t, value, target)) {
        FcTypeRef* g = type;
        if (target->kind == STK_String && type->nonNull) { g = (FcTypeRef*)fcAlloc(TC_, sizeof *g); *g = *type; g->nonNull = false; }
        fcPutGuard(t, valueExpr, g, what);
    }
}

static const FcST* FindMember(FcTC* t, const FcST* receiver, const char* name);
static const FcST* Lookup(FcTC* t, const char* name) {
    int i;
    const FcST* member;
    for (i = t->scopes->count - 1; i >= 0; i--) { void* v; if (fcDictTry((FcDict*)t->scopes->items[i], name, &v)) return (const FcST*)v; }
    if (t->cls != NULL && (member = FindMember(t, ThisClass(t), name)) != NULL) return member;
    return ST_DYNAMIC;
}
static bool Checked(int declared) { return declared > 0; }

static const FcST* FindMember(FcTC* t, const FcST* receiver, const char* name) {
    FcList* ic = InterfaceChain(t, receiver);
    FcList* chain;
    int i, k;
    for (i = 0; i < ic->count; i++) {
        FcChainEntry* e = (FcChainEntry*)ic->items[i];
        FcInterfaceDecl* iface = (FcInterfaceDecl*)e->decl;
        if (iface->properties)
            for (k = 0; k < iface->properties->count; k++) {
                FcInterfacePropertySig* sig = (FcInterfacePropertySig*)iface->properties->items[k];
                if (streq0(sig->name, name)) return DeclaredOrDynamic(t, sig->type, 0, e->map);
            }
    }
    chain = Chain(t, receiver);
    for (i = 0; i < chain->count; i++) {
        FcChainEntry* e = (FcChainEntry*)chain->items[i];
        FcClassDecl* decl = (FcClassDecl*)e->decl;
        for (k = 0; k < decl->members->count; k++) {
            FcStmt* m = (FcStmt*)decl->members->items[k];
            if (m->kind == SK_FieldDecl && streq0(((FcFieldDecl*)m)->name, name))
                return Checked(m->source) ? MayBeUndefined(t, FieldType(t, (FcFieldDecl*)m, e->map)) : FieldType(t, (FcFieldDecl*)m, e->map);
            if (m->kind == SK_PropertyDecl && streq0(((FcPropertyDecl*)m)->name, name)) {
                const FcST* d = DeclaredOrDynamic(t, ((FcPropertyDecl*)m)->type, 0, e->map);
                return Checked(m->source) ? MayBeUndefined(t, d) : d;
            }
        }
    }
    return NULL;
}
static const FcST* FindMemberOf(FcTC* t, const char* className, const char* name) { return FindMember(t, stClassOf(TC_, className, NULL), name); }
/* the member 'name' of receiver is a property, as FindMember finds it (TC037: an assignment to it calls its setter) */
static bool IsPropertyMember(FcTC* t, const FcST* receiver, const char* name) {
    FcList* ic;
    FcList* chain;
    int i, k;
    if (receiver == NULL || receiver->kind != STK_Class) return false;
    ic = InterfaceChain(t, receiver);
    for (i = 0; i < ic->count; i++) {
        FcInterfaceDecl* iface = (FcInterfaceDecl*)((FcChainEntry*)ic->items[i])->decl;
        if (iface->properties)
            for (k = 0; k < iface->properties->count; k++) if (streq0(((FcInterfacePropertySig*)iface->properties->items[k])->name, name)) return true;
    }
    chain = Chain(t, receiver);
    for (i = 0; i < chain->count; i++) {
        FcClassDecl* decl = (FcClassDecl*)((FcChainEntry*)chain->items[i])->decl;
        for (k = 0; k < decl->members->count; k++) {
            FcStmt* m = (FcStmt*)decl->members->items[k];
            if (m->kind == SK_FieldDecl && streq0(((FcFieldDecl*)m)->name, name)) return false;
            if (m->kind == SK_PropertyDecl && streq0(((FcPropertyDecl*)m)->name, name)) return true;
        }
    }
    return false;
}

static bool Fits(FcList* parameters, int argc) {
    int required = 0, i;
    for (i = 0; i < parameters->count; i++) if (((FcLambdaParam*)parameters->items[i])->defaultValue == NULL) required++;
    return argc <= parameters->count && argc >= required;
}
static FcMethodDecl* fcSigMethod(FcTC* t, FcInterfaceMethodSig* sig) {
    FcBlockStmt* body = FC_NEW(TC_, FcBlockStmt, .h = {SK_BlockStmt, 0, sig->line}, .statements = fcListNew(TC_));
    return FC_NEW(TC_, FcMethodDecl, .h = {SK_MethodDecl, 0, sig->line}, .returnType = sig->returnType, .name = sig->name, .params = sig->params, .body = body);
}

static FcMethodDecl* FindMethod(FcTC* t, const FcST* receiver, const char* name, int argc, FcDict** map) {
    FcList* chain;
    int i, k;
    *map = NULL;
    if (receiver->cls != NULL && fcDictHas(&t->interfaces, receiver->cls)) {
        FcList* ic = InterfaceChain(t, receiver);
        FcInterfaceMethodSig* found = NULL;
        FcDict* foundMap = NULL;
        int count = 0;
        for (i = 0; i < ic->count; i++) {
            FcChainEntry* e = (FcChainEntry*)ic->items[i];
            FcInterfaceDecl* iface = (FcInterfaceDecl*)e->decl;
            for (k = 0; k < iface->methods->count; k++) {
                FcInterfaceMethodSig* s = (FcInterfaceMethodSig*)iface->methods->items[k];
                if (streq0(s->name, name) && Fits(s->params, argc)) { if (count == 0) { found = s; foundMap = e->map; } count++; }
            }
        }
        if (count != 1) return NULL;
        *map = foundMap;
        return fcSigMethod(t, found);
    }
    chain = Chain(t, receiver);
    for (i = 0; i < chain->count; i++) {
        FcChainEntry* e = (FcChainEntry*)chain->items[i];
        FcClassDecl* decl = (FcClassDecl*)e->decl;
        int named = 0, fitting = 0;
        FcMethodDecl* fit = NULL;
        for (k = 0; k < decl->members->count; k++) {
            FcStmt* m = (FcStmt*)decl->members->items[k];
            if (m->kind != SK_MethodDecl || !streq0(((FcMethodDecl*)m)->name, name)) continue;
            named++;
            if (Fits(((FcMethodDecl*)m)->params, argc)) { fitting++; fit = (FcMethodDecl*)m; }
        }
        if (named == 0) continue;
        if (fitting != 1) return NULL;
        *map = e->map;
        return fit;
    }
    return NULL;
}

typedef struct FcCand { FcMethodDecl* method; FcDict* map; } FcCand;
static FcCand* fcCand(FcTC* t, FcMethodDecl* m, FcDict* map) { FcCand* c = (FcCand*)fcAlloc(TC_, sizeof *c); c->method = m; c->map = map; return c; }

static bool fcHasTypeParamNamed(FcList* tps, const char* name) {
    int i;
    if (!tps) return false;
    for (i = 0; i < tps->count; i++) if (streq0(((FcTypeParam*)tps->items[i])->name, name)) return true;
    return false;
}
static const char* OverloadKey(FcLambdaParam* p, FcMethodDecl* m) {
    FcTypeRef* ty = p->type;
    if (p->byRef || ty == NULL || fcTrIsInferred(ty) || streq0(ty->baseName, "any")) return "";
    if (fcHasTypeParamNamed(m->typeParams, ty->baseName) && ty->typeArgs == NULL) return "";
    if (p->arrayRanks->count > 0 || ty->arrayRank > 0) return "array";
    return streq0(ty->baseName, "lambda") ? "lambda" : ty->baseName;
}
static bool OverridesStatic(FcMethodDecl* derived, FcMethodDecl* baseMethod) {
    int i;
    if (derived->params->count != baseMethod->params->count) return false;
    for (i = 0; i < derived->params->count; i++) {
        const char* a = OverloadKey((FcLambdaParam*)derived->params->items[i], derived);
        const char* b = OverloadKey((FcLambdaParam*)baseMethod->params->items[i], baseMethod);
        if (!(streq0(a, b) || !a[0] || !b[0])) return false;
    }
    return true;
}

static FcList* FindMethods(FcTC* t, const FcST* receiver, const char* name, int argc) {
    FcList* found = fcListNew(TC_);
    FcList* seen;
    FcList* chain;
    int i, k, j;
    if (receiver->cls != NULL && fcDictHas(&t->interfaces, receiver->cls)) {
        FcList* ic = InterfaceChain(t, receiver);
        for (i = 0; i < ic->count; i++) {
            FcChainEntry* e = (FcChainEntry*)ic->items[i];
            FcInterfaceDecl* iface = (FcInterfaceDecl*)e->decl;
            for (k = 0; k < iface->methods->count; k++) {
                FcInterfaceMethodSig* s = (FcInterfaceMethodSig*)iface->methods->items[k];
                if (streq0(s->name, name) && Fits(s->params, argc)) fcListAdd(TC_, found, fcCand(t, fcSigMethod(t, s), e->map));
            }
        }
        return found;
    }
    seen = fcListNew(TC_);
    chain = Chain(t, receiver);
    for (i = 0; i < chain->count; i++) {
        FcChainEntry* e = (FcChainEntry*)chain->items[i];
        FcClassDecl* decl = (FcClassDecl*)e->decl;
        FcList* named = fcListNew(TC_);
        FcList* fitting = fcListNew(TC_);
        for (k = 0; k < decl->members->count; k++) {
            FcStmt* m = (FcStmt*)decl->members->items[k];
            if (m->kind == SK_MethodDecl && streq0(((FcMethodDecl*)m)->name, name)) fcListAdd(TC_, named, m);
        }
        for (k = 0; k < named->count; k++) {
            FcMethodDecl* m = (FcMethodDecl*)named->items[k];
            bool overridden = false;
            if (!Fits(m->params, argc)) continue;
            for (j = 0; j < seen->count && !overridden; j++) if (OverridesStatic((FcMethodDecl*)seen->items[j], m)) overridden = true;
            if (!overridden) fcListAdd(TC_, fitting, m);
        }
        /* OrderBy(m => m.Params.Count == argc ? 0 : 1).ThenBy(m => m.Params.Count): a stable sort */
        for (k = 1; k < fitting->count; k++)
            for (j = k; j > 0; j--) {
                FcMethodDecl* a = (FcMethodDecl*)fitting->items[j - 1];
                FcMethodDecl* b = (FcMethodDecl*)fitting->items[j];
                int ka = a->params->count == argc ? 0 : 1, kb = b->params->count == argc ? 0 : 1;
                if (ka < kb || (ka == kb && a->params->count <= b->params->count)) break;
                fitting->items[j - 1] = b; fitting->items[j] = a;
            }
        for (k = 0; k < fitting->count; k++) fcListAdd(TC_, found, fcCand(t, (FcMethodDecl*)fitting->items[k], e->map));
        for (k = 0; k < named->count; k++) fcListAdd(TC_, seen, named->items[k]);
    }
    return found;
}

#define OV_AnyScore 10
#define OV_GenericScore 20
#define OV_ObjectScore 15
#define OV_InterfaceScore 30
#define OV_ClassExact 1000
#define OV_ExactKind 1000
#define OV_IntToFloat 900
#define OV_IntToWord 950
#define OV_UndefinedScore 1
#define TC_NoMatch (-1)
#define TC_Unknown (-2)

static int ClassDistance(FcTC* t, const char* derived, const char* baseName) {
    const char* name = derived;
    int d;
    for (d = 0; d < 64 && name != NULL; d++) {
        FcClassDecl* decl;
        FcTypeRef* b;
        if (streq0(name, baseName)) return d;
        if (!(decl = (FcClassDecl*)fcDictGet(t->classes, name))) return -1;
        b = fcFirstClassBase(t, decl);
        name = b ? NameOf(t, b) : NULL;
    }
    return -1;
}
static bool Implements(FcTC* t, const char* className, const char* iface) {
    const char* name = className;
    int d, k;
    FcClassDecl* decl;
    for (d = 0; d < 64 && name != NULL && (decl = (FcClassDecl*)fcDictGet(t->classes, name)) != NULL; d++) {
        FcTypeRef* b;
        if (decl->baseRefs)
            for (k = 0; k < decl->baseRefs->count; k++) {
                const char* n = NameOf(t, (FcTypeRef*)decl->baseRefs->items[k]);
                if (streq0(n, iface) || (IsInterface(t, n) && fcListHasStr(InterfaceClosure(t, n), iface))) return true;
            }
        b = fcFirstClassBase(t, decl);
        name = b ? NameOf(t, b) : NULL;
    }
    return false;
}
static int StaticScore(FcTC* t, const FcST* arg, FcLambdaParam* p, FcMethodDecl* m, FcDict* map) {
    const char* key;
    const FcST* declared;
    if (p->byRef) return OV_AnyScore;
    key = OverloadKey(p, m);
    if (!key[0]) {
        bool generic = (p->type != NULL && fcHasTypeParamNamed(m->typeParams, p->type->baseName)) || (p->type != NULL && map && fcDictHas(map, p->type->baseName));
        return arg->kind == STK_Undefined ? OV_UndefinedScore : generic ? OV_GenericScore : OV_AnyScore;
    }
    if (arg->kind == STK_Undefined) return OV_UndefinedScore;
    if (stIsDynamic(arg)) return TC_Unknown;
    declared = Declared(t, p->type, p->arrayRanks->count, map);
    if (declared == NULL || stIsDynamic(declared)) return arg->kind == STK_Undefined ? OV_UndefinedScore : OV_GenericScore;
    switch (declared->kind) {
        case STK_Int: case STK_Bool: case STK_Char: case STK_String: case STK_Lambda: case STK_Array:
            return arg->kind == declared->kind ? OV_ExactKind : TC_NoMatch;
        case STK_Float: return arg->kind == STK_Float ? OV_ExactKind : arg->kind == STK_Int ? OV_IntToFloat : TC_NoMatch;
        case STK_Word: return arg->kind == STK_Word ? OV_ExactKind : arg->kind == STK_Int ? OV_IntToWord : TC_NoMatch;
        case STK_Object: return arg->kind == STK_Class || arg->kind == STK_Object ? OV_ObjectScore : TC_NoMatch;
        case STK_Class: {
            int distance;
            if (arg->kind == STK_Object) return TC_Unknown;
            if (arg->kind == STK_Array && streq0(declared->cls, "IEnumerable")) return OV_InterfaceScore;
            if (arg->kind != STK_Class || arg->cls == NULL || declared->cls == NULL) return TC_NoMatch;
            if (fcDictHas(&t->interfaces, declared->cls))
                return Implements(t, arg->cls, declared->cls) || (IsInterface(t, arg->cls) && fcListHasStr(InterfaceClosure(t, arg->cls), declared->cls)) ? OV_InterfaceScore
                    : fcDictHas(&t->interfaces, arg->cls) ? TC_Unknown : TC_NoMatch;
            distance = ClassDistance(t, arg->cls, declared->cls);
            if (distance >= 0) return OV_ClassExact - distance;
            return ClassDistance(t, declared->cls, arg->cls) >= 0 || fcDictHas(&t->interfaces, arg->cls) ? TC_Unknown : TC_NoMatch;
        }
        default: return OV_AnyScore;
    }
}
static FcCand* PickOverload(FcTC* t, FcList* candidates, FcList* args) {
    int best = -1, c, i;
    long long bestScore = LLONG_MIN;
    bool unknown = false;
    for (c = 0; c < candidates->count; c++) {
        FcCand* cand = (FcCand*)candidates->items[c];
        long long total = 0;
        bool fits = true;
        for (i = 0; i < args->count && i < cand->method->params->count; i++) {
            int score = StaticScore(t, (const FcST*)args->items[i], (FcLambdaParam*)cand->method->params->items[i], cand->method, cand->map);
            if (score == TC_NoMatch) { fits = false; break; }
            if (score == TC_Unknown) { unknown = true; fits = false; break; }
            total += score;
        }
        if (fits && total > bestScore) { best = c; bestScore = total; }
    }
    if (unknown || best < 0) return NULL;
    return (FcCand*)candidates->items[best];
}

/* Assumptions: what the choice assumes of each argument (Bytecode.Overloads.Holds); NULL when the choice depends on more than that.
 * candidates: FcMethodDecl*, owners: the ClassDecl* of each */
static FcList* Assumptions(FcTC* t, FcList* candidates, FcList* owners, FcList* args) {
    FcList* result = fcListNew(TC_);
    int i, c;
    for (i = 0; i < args->count; i++) {
        bool allLoose = true;
        const FcST* a = (const FcST*)args->items[i];
        const char* r = NULL;
        for (c = 0; c < candidates->count; c++) {
            FcMethodDecl* m = (FcMethodDecl*)candidates->items[c];
            FcClassDecl* owner = (FcClassDecl*)owners->items[c];
            FcLambdaParam* p;
            int tpi;
            if (i >= m->params->count) continue;
            p = (FcLambdaParam*)m->params->items[i];
            if (p->byRef || p->type == NULL || fcTrIsInferred(p->type) || streq0(p->type->baseName, "any")) continue;
            if (owner->typeParams) { int k; for (k = 0; k < owner->typeParams->count; k++) if (streq0(((FcTypeParam*)owner->typeParams->items[k])->name, p->type->baseName)) return NULL; }
            tpi = -1;
            if (m->typeParams) { int k; for (k = 0; k < m->typeParams->count; k++) if (streq0(((FcTypeParam*)m->typeParams->items[k])->name, p->type->baseName)) { tpi = k; break; } }
            if (tpi >= 0) {
                if (((FcTypeParam*)m->typeParams->items[tpi])->constraintGroups->count > 0) return NULL;
                continue;
            }
            allLoose = false;
        }
        switch (a->kind) {
            case STK_Undefined: r = "u"; break;
            case STK_Int: r = "k:int"; break;
            case STK_Float: r = "k:float"; break;
            case STK_Bool: r = "k:bool"; break;
            case STK_Char: r = "k:char"; break;
            case STK_String: r = "k:string"; break;
            case STK_Lambda: r = "k:lambda"; break;
            case STK_Array: r = "k:array"; break;
            case STK_Object: r = "k:class"; break;
            case STK_Word: r = "k:word"; break;
            case STK_Class: if (a->cls) { r = TF(t, "%s%s", fcDictHas(&t->interfaces, a->cls) ? "i:" : "c:", a->cls); break; } /* FALLTHROUGH */
            default: r = allLoose ? "" : NULL; break;
        }
        if (r == NULL) return NULL;
        fcListAdd(TC_, result, (void*)r);
    }
    return result;
}
/* OverloadHint.Format: the parts separated by U+001F */
static const char* fcOverloadHintFormat(FcTC* t, const char* declaringClass, const char* method, int ordinal, int paramCount, int argc, FcList* assumptions) {
    FcBuf b = {0};
    int i;
    fcBufFmt(&b, "%s\x1f%s\x1f%d\x1f%d\x1f%d", declaringClass, method, ordinal, paramCount, argc);
    for (i = 0; i < assumptions->count; i++) { fcBufChar(&b, '\x1f'); fcBufStr(&b, (const char*)assumptions->items[i]); }
    return fcBufDone(TC_, &b);
}
/* DerivedKeepOverloads */
static bool DerivedKeepOverloads(FcTC* t, const char* cls, const char* name, int argc, FcList* candidates) {
    int k, c, i;
    FC_DICT_EACH(t->classes, e) {
        FcClassDecl* sub = (FcClassDecl*)e->value;
        if (streq0((const char*)e->key, cls) || ClassDistance(t, (const char*)e->key, cls) <= 0) continue;
        for (k = 0; k < sub->members->count; k++) {
            FcMethodDecl* m;
            bool any = false;
            if (((FcStmt*)sub->members->items[k])->kind != SK_MethodDecl) continue;
            m = (FcMethodDecl*)sub->members->items[k];
            if (!streq0(m->name, name) || !Fits(m->params, argc)) continue;
            for (c = 0; c < candidates->count && !any; c++) {
                FcMethodDecl* cand = (FcMethodDecl*)candidates->items[c];
                bool same = cand->params->count == m->params->count;
                for (i = 0; same && i < m->params->count; i++)
                    if (!streq0(OverloadKey((FcLambdaParam*)cand->params->items[i], cand), OverloadKey((FcLambdaParam*)m->params->items[i], m))) same = false;
                if (same) any = true;
            }
            if (!any) return false;
        }
    }
    return true;
}
/* OverloadHintFor: the hint of a call whose overload the static types decide (candidates: FcCand*) */
static const char* OverloadHintFor(FcTC* t, FcMethodDecl* method, FcList* candidates, FcList* args, const char* receiverClass, bool virtualCall) {
    const char* declaringKey = NULL;
    FcClassDecl* declaring = NULL;
    FcList* methods;
    FcList* owners;
    FcList* assumptions;
    int i, ordinal = 0;
    if (fcDictHas(&t->interfaces, receiverClass)) return NULL;
    FC_DICT_EACH(t->classes, e) {
        FcClassDecl* cd = (FcClassDecl*)e->value;
        for (i = 0; i < cd->members->count && !declaring; i++) if (cd->members->items[i] == method) { declaring = cd; declaringKey = (const char*)e->key; }
        if (declaring) break;
    }
    if (!declaring) return NULL;
    methods = fcListNew(TC_);
    owners = fcListNew(TC_);
    for (i = 0; i < candidates->count; i++) { fcListAdd(TC_, methods, ((FcCand*)candidates->items[i])->method); fcListAdd(TC_, owners, declaring); }
    if (virtualCall && !DerivedKeepOverloads(t, receiverClass, method->name, args->count, methods)) return NULL;
    if (!(assumptions = Assumptions(t, methods, owners, args))) return NULL;
    for (i = 0; i < declaring->members->count; i++) {
        FcStmt* x = (FcStmt*)declaring->members->items[i];
        if (x == (FcStmt*)method) break;
        if (x->kind == SK_MethodDecl && streq0(((FcMethodDecl*)x)->name, method->name)) ordinal++;
    }
    return fcOverloadHintFormat(t, declaringKey, method->name, ordinal, method->params->count, args->count, assumptions);
}
/* ConstructorHint (asMethods: FcMethodDecl*) */
static const char* ConstructorHint(FcTC* t, FcClassDecl* decl, const char* className, FcConstructorDecl* chosen, FcList* asMethods, FcList* args) {
    FcList* owners = fcListNew(TC_);
    FcList* assumptions;
    int i, ordinal = 0;
    for (i = 0; i < asMethods->count; i++) fcListAdd(TC_, owners, decl);
    if (!(assumptions = Assumptions(t, asMethods, owners, args))) return NULL;
    for (i = 0; i < decl->members->count; i++) {
        FcStmt* k = (FcStmt*)decl->members->items[i];
        if (k == (FcStmt*)chosen) break;
        if (k->kind == SK_ConstructorDecl && ((FcConstructorDecl*)k)->params->count == chosen->params->count) ordinal++;
    }
    return fcOverloadHintFormat(t, className, "#new", ordinal, chosen->params->count, args->count, assumptions);
}
/* TypeArgText(StaticType) / NumberTypeArgText */
static const char* NumberTypeArgText(FcTC* t, const char* kind, const FcST* type) {
    const char* demand;
    if (!type->unit) return TF(t, "%s:any", kind);
    if (fcUIsUnitless(type->unit)) return kind;
    if (!(demand = DemandTextOf(t, type->unit))) return TF(t, "%s:any", kind);
    return TF(t, "%s:%s%s", kind, type->unitIsDimension ? "any " : "", demand);
}
static const char* TypeArgTextOfType(FcTC* t, const FcST* type) {
    switch (type->kind) {
        case STK_Int: return NumberTypeArgText(t, "int", type);
        case STK_Float: return NumberTypeArgText(t, "float", type);
        case STK_Bool: return "bool";
        case STK_Char: return "char";
        case STK_String: return "string";
        case STK_Class: return type->cls ? type->cls : "";
        case STK_Dynamic: return type->typeParam ? type->typeParam : "";
        default: return "";
    }
}

static bool IsFreshOrWorded(FcExpr* e) {
    if (e->kind == EK_UnaryExpr) { FcUnaryOp op = ((FcUnaryExpr*)e)->op; return op == UOP_DeepCopy || op == UOP_FlatCopy || op == UOP_Take || op == UOP_TryTake; }
    return e->kind == EK_NewExpr || e->kind == EK_NewArrayExpr || e->kind == EK_NewBufferExpr || e->kind == EK_CallExpr || e->kind == EK_WithExpr
        || e->kind == EK_ArrayLiteralExpr || e->kind == EK_BinaryExpr || e->kind == EK_LambdaExpr;
}

/* CheckArguments: parameters may hold NULL (an argument without a parameter) */
static void CheckArguments(FcTC* t, FcList* parameters, FcList* args, FcList* argExprs, const char* callee, FcDict* map) {
    int i;
    for (i = 0; i < args->count && i < parameters->count; i++) {
        FcLambdaParam* parameter = (FcLambdaParam*)parameters->items[i];
        const FcST* declared;
        const FcST* got;
        FcExpr* ae = (FcExpr*)argExprs->items[i];
        if (!parameter || parameter->byRef) continue;
        if (parameter->type && parameter->type->lambdaSignature && parameter->type->lambdaSignature->isExpression) continue;
        declared = Declared(t, parameter->type, parameter->arrayRanks->count, map);
        if (declared) CheckAssignable(t, (const FcST*)args->items[i], declared, ae->line, TF(t, "parameter '%s' of '%s'", parameter->name, callee), TC_ArgumentCode, ae);
        got = (const FcST*)args->items[i];
        if (declared && declared->kind == STK_Array && declared->element->raw && (declared->element->kind == STK_Int || declared->element->kind == STK_Word)
            && got->kind == STK_Array && got->element && !(got->element->raw && got->element->kind == declared->element->kind) && !IsFreshOrWorded(ae)) {
            const char* w = declared->element->kind == STK_Word ? "word" : "byte";
            Report(t, TC_ArrayToBufferCode, TF(t, "The parameter '%s' of '%s' is a %s buffer, the argument an array: the method gets a new buffer with its elements, and what it writes does not reach the array. Pass a buffer (`%s data[] = ...`, `new %s[n]`)",
                parameter->name, callee, w, w, w), ae->line);
        }
    }
}

static FcList* ArgParams(FcTC* t, FcList* parameters, FcList* names, int argCount, const char* callee, int line) {
    FcList* result = fcListNew(TC_);
    int i, k;
    for (i = 0; i < argCount; i++) {
        const char* name = names && i < names->count ? (const char*)names->items[i] : NULL;
        FcLambdaParam* param = NULL;
        if (name == NULL) { fcListAdd(TC_, result, i < parameters->count ? parameters->items[i] : NULL); continue; }
        for (k = 0; k < parameters->count && !param; k++) if (streq0(((FcLambdaParam*)parameters->items[k])->name, name)) param = (FcLambdaParam*)parameters->items[k];
        if (param == NULL) ReportError(t, TC_NamedArgumentCode, TF(t, "'%s' has no parameter '%s'", callee, name), line);
        fcListAdd(TC_, result, param);
    }
    if (names)
        for (k = 0; k < parameters->count; k++) {
            FcLambdaParam* p = (FcLambdaParam*)parameters->items[k];
            if (!fcListHasPtr(result, p) && p->defaultValue == NULL)
                ReportError(t, TC_NamedArgumentCode, TF(t, "The parameter '%s' of '%s' gets no value (it has no default value)", p->name, callee), line);
        }
    return result;
}

#include "typechecker2.c"
