/* native/forge/ast.c - the syntax tree as text: what src/fire.Compiler/FrontendDump.cs prints for the C# tree (the fields of each record in the order
 * of its primary constructor, then the ones it sets later; a node: its type; a list: [count] and its items as "-"). */

static const char* const fcExprKindNames[] = {
#define FC_EKN(n) #n,
    FC_EXPR_KINDS(FC_EKN)
#undef FC_EKN
};
static const char* const fcStmtKindNames[] = {
#define FC_SKN(n) #n,
    FC_STMT_KINDS(FC_SKN)
#undef FC_SKN
};
static const char* const fcUnaryOpNames[] = { "Negate", "LogicalNot", "BitNot", "Dereference", "AddressOf", "FlatCopy", "DeepCopy", "Take", "Trust", "TryTake" };
static const char* const fcBinaryOpNames[] = { "Add", "Sub", "Mul", "Div", "Mod", "Eq", "NotEq", "Lt", "LtEq", "Gt", "GtEq", "And", "Or", "BitAnd", "BitOr",
    "BitXor", "ShiftLeft", "ShiftRight", "Power", "ShiftRightLogical" };
static const char* const fcAccessNames[] = { "Public", "Private", "Protected" };
static const char* const fcCoerceNames[] = { "Factor", "From", "To" };
static const char* const fcConstraintNames[] = { "IsOf", "IsIn" };
static const char* const fcGlobalJobsNames[] = { "Sync", "Safepoints", "Sleep" };
static const char* const fcWarnTargetNames[] = { "Class", "Constructor", "Method", "Member" };

typedef struct FcDumper { FcBuf* o; } FcDumper;

static void dLabel(FcDumper* d, int ind, const char* label) {
    int i;
    for (i = 0; i < ind * 2; i++) fcBufChar(d->o, ' ');
    fcBufStr(d->o, label);
    fcBufStr(d->o, ": ");
}
static void dNull(FcDumper* d, int ind, const char* label) { dLabel(d, ind, label); fcBufStr(d->o, "null\n"); }
static void dStr(FcDumper* d, int ind, const char* label, const char* s) {
    if (!s) { dNull(d, ind, label); return; }
    dLabel(d, ind, label);
    fcEsc(d->o, s);
    fcBufChar(d->o, '\n');
}
static void dInt(FcDumper* d, int ind, const char* label, int v) { dLabel(d, ind, label); fcBufFmt(d->o, "%d\n", v); }
static void dNInt(FcDumper* d, int ind, const char* label, int v) { if (v == FC_NULL_INT) dNull(d, ind, label); else dInt(d, ind, label, v); }
static void dBool(FcDumper* d, int ind, const char* label, bool v) { dLabel(d, ind, label); fcBufStr(d->o, v ? "true\n" : "false\n"); }
static void dEnum(FcDumper* d, int ind, const char* label, const char* name) { dLabel(d, ind, label); fcBufStr(d->o, name); fcBufChar(d->o, '\n'); }
static void dType(FcDumper* d, int ind, const char* label, const char* typeName) { dLabel(d, ind, label); fcBufStr(d->o, typeName); fcBufChar(d->o, '\n'); }
static bool dList(FcDumper* d, int ind, const char* label, const FcList* l) {
    if (!l) { dNull(d, ind, label); return false; }
    dLabel(d, ind, label);
    fcBufFmt(d->o, "%s[%d]\n", l->own ? "own" : "", l->count);
    return true;
}
static void dStrList(FcDumper* d, int ind, const char* label, const FcList* l) {
    int i;
    if (!dList(d, ind, label, l)) return;
    for (i = 0; i < l->count; i++) dStr(d, ind + 1, "-", (const char*)l->items[i]);
}

static void dExpr(FcDumper* d, int ind, const char* label, const FcExpr* e);
static void dStmt(FcDumper* d, int ind, const char* label, const FcStmt* s);

static void dExprList(FcDumper* d, int ind, const char* label, const FcList* l) {
    int i;
    if (!dList(d, ind, label, l)) return;
    for (i = 0; i < l->count; i++) dExpr(d, ind + 1, "-", (const FcExpr*)l->items[i]);
}
static void dStmtList(FcDumper* d, int ind, const char* label, const FcList* l) {
    int i;
    if (!dList(d, ind, label, l)) return;
    for (i = 0; i < l->count; i++) dStmt(d, ind + 1, "-", (const FcStmt*)l->items[i]);
}

static void dBits(FcDumper* d, double v) { uint64_t bits; memcpy(&bits, &v, 8); fcBufFmt(d->o, "0x%016" PRIX64, bits); }

static void dValue(FcDumper* d, int ind, const char* label, const FcValue* v) {
    dLabel(d, ind, label);
    switch (v->kind) {
        case VK_Int: fcBufFmt(d->o, "int %" PRId64, v->i); break;
        case VK_Float: fcBufStr(d->o, "float "); dBits(d, v->f); break;
        case VK_String: fcBufStr(d->o, "string "); fcEsc(d->o, v->s); break;
        case VK_Char: fcBufStr(d->o, "char "); fcEsc(d->o, v->s); break;
        case VK_Bool: fcBufStr(d->o, v->b ? "bool true" : "bool false"); break;
        case VK_Undefined: fcBufStr(d->o, "undefined"); break;
    }
    if (v->unit && (v->kind == VK_Int || v->kind == VK_Float || v->kind == VK_Undefined)) { fcBufStr(d->o, " unit="); fcEsc(d->o, v->unit); }
    fcBufChar(d->o, '\n');
}

static void dTypeRef(FcDumper* d, int ind, const char* label, const FcTypeRef* t);
static void dTypeRefList(FcDumper* d, int ind, const char* label, const FcList* l) {
    int i;
    if (!dList(d, ind, label, l)) return;
    for (i = 0; i < l->count; i++) dTypeRef(d, ind + 1, "-", (const FcTypeRef*)l->items[i]);
}
static void dTypeRef(FcDumper* d, int ind, const char* label, const FcTypeRef* t) {
    if (!t) { dNull(d, ind, label); return; }
    dType(d, ind, label, "TypeRef");
    ind++;
    dStr(d, ind, "BaseName", t->baseName);
    dNInt(d, ind, "BitWidth", t->bitWidth);
    dInt(d, ind, "PointerDepth", t->pointerDepth);
    if (!t->lambdaSignature) dNull(d, ind, "LambdaSignature");
    else {
        const FcLambdaSignature* s = t->lambdaSignature;
        dType(d, ind, "LambdaSignature", "LambdaSignature");
        dStr(d, ind + 1, "ReturnTypeName", s->returnTypeName);
        dStrList(d, ind + 1, "ParamTypeNames", s->paramTypeNames);
        dBool(d, ind + 1, "IsSelector", s->isSelector);
        dStr(d, ind + 1, "SelectorKind", s->selectorKind);
        dBool(d, ind + 1, "IsExpression", s->isExpression);
    }
    dStrList(d, ind, "Namespaces", t->namespaces);
    dStr(d, ind, "Unit", t->unit);
    dInt(d, ind, "ArrayRank", t->arrayRank);
    dInt(d, ind, "TypeArgCount", t->typeArgCount);
    dTypeRefList(d, ind, "TypeArgs", t->typeArgs);
    dBool(d, ind, "Unsigned", t->isUnsigned);
    dBool(d, ind, "NonNull", t->nonNull);
}

static void dParams(FcDumper* d, int ind, const char* label, const FcList* l) {
    int i;
    if (!dList(d, ind, label, l)) return;
    for (i = 0; i < l->count; i++) {
        const FcLambdaParam* p = (const FcLambdaParam*)l->items[i];
        int k = ind + 2;
        dType(d, ind + 1, "-", "LambdaParam");
        dStr(d, k, "Name", p->name);
        dTypeRef(d, k, "Type", p->type);
        dExprList(d, k, "ArrayRanks", p->arrayRanks);
        dExpr(d, k, "DefaultValue", p->defaultValue);
        dBool(d, k, "ByRef", p->byRef);
    }
}

static void dExpr(FcDumper* d, int ind, const char* label, const FcExpr* e) {
    int k = ind + 1, i;
    if (!e) { dNull(d, ind, label); return; }
    dType(d, ind, label, fcExprKindNames[e->kind]);
    dInt(d, k, "Line", e->line);
    switch (e->kind) {
        case EK_LiteralExpr: dValue(d, k, "Value", &((const FcLiteralExpr*)e)->value); break;
        case EK_TypeBitsExpr: { const FcTypeBitsExpr* x = (const FcTypeBitsExpr*)e; dStr(d, k, "BaseName", x->baseName); dNInt(d, k, "Width", x->width); break; }
        case EK_IdentifierExpr: dStr(d, k, "Name", ((const FcIdentifierExpr*)e)->name); break;
        case EK_ThisExpr: case EK_BaseExpr: case EK_SelfClassExpr: case EK_SyncGlobalsExpr: case EK_ConditionalTargetExpr: break;
        case EK_LambdaExpr: {
            const FcLambdaExpr* x = (const FcLambdaExpr*)e;
            dParams(d, k, "Params", x->params);
            dExpr(d, k, "OnTarget", x->onTarget);
            dStmt(d, k, "Body", (const FcStmt*)x->body);
            dBool(d, k, "AutoCapture", x->autoCapture);
            if (!x->captureModes) dNull(d, k, "CaptureModes");
            else {
                dLabel(d, k, "CaptureModes");
                fcBufFmt(d->o, "{%d}\n", x->captureModes->count);
                for (i = 0; i < x->captureModes->count; i++) {
                    const FcCapture* cm = (const FcCapture*)x->captureModes->items[i];
                    FcBuf key = {0};
                    fcEsc(&key, cm->name);
                    dBool(d, k + 1, key.p, cm->handover);
                    fcBufFree(&key);
                }
            }
            dBool(d, k, "ExpressionBody", x->expressionBody);
            dBool(d, k, "LendCaptures", x->lendCaptures);
            break;
        }
        case EK_SyncExpr: { const FcSyncExpr* x = (const FcSyncExpr*)e; dBool(d, k, "IsTry", x->isTry); dBool(d, k, "IsFlat", x->isFlat); dExpr(d, k, "Target", x->target); break; }
        case EK_ProbeExpr: {
            const FcProbeExpr* x = (const FcProbeExpr*)e;
            dExpr(d, k, "Target", x->target); dStr(d, k, "Member", x->member); dBool(d, k, "IsChanging", x->isChanging); dExpr(d, k, "Handler", x->handler);
            break;
        }
        case EK_TryProcessExpr: dExpr(d, k, "Target", ((const FcTryProcessExpr*)e)->target); break;
        case EK_TryCallExpr: dExpr(d, k, "Call", ((const FcTryCallExpr*)e)->call); break;
        case EK_ConditionalAccessExpr: {
            const FcConditionalAccessExpr* x = (const FcConditionalAccessExpr*)e;
            dExpr(d, k, "Target", x->target); dExpr(d, k, "Inner", x->inner); dExpr(d, k, "Placeholder", x->placeholder);
            dBool(d, k, "CheckMember", x->checkMember); dStr(d, k, "Member", x->member);
            break;
        }
        case EK_NewExpr: {
            const FcNewExpr* x = (const FcNewExpr*)e;
            dTypeRef(d, k, "ClassRef", x->classRef); dExprList(d, k, "Args", x->args); dStrList(d, k, "TypeArgs", x->typeArgs); dStrList(d, k, "ArgNames", x->argNames);
            break;
        }
        case EK_NewArrayExpr: { const FcNewArrayExpr* x = (const FcNewArrayExpr*)e; dTypeRef(d, k, "ElementType", x->elementType); dExprList(d, k, "SizeExprs", x->sizeExprs); break; }
        case EK_NewBufferExpr: { const FcNewBufferExpr* x = (const FcNewBufferExpr*)e; dExpr(d, k, "SizeExpr", x->sizeExpr); dBool(d, k, "Words", x->words); break; }
        case EK_ArrayLiteralExpr: dExprList(d, k, "Elements", ((const FcArrayLiteralExpr*)e)->elements); break;
        case EK_InterpolatedStringExpr: {
            const FcList* parts = ((const FcInterpolatedStringExpr*)e)->parts;
            if (!dList(d, k, "Parts", parts)) break;
            for (i = 0; i < parts->count; i++) {
                const FcInterpolationPart* p = (const FcInterpolationPart*)parts->items[i];
                if (!p->isExpression) { dType(d, k + 1, "-", "InterpolationTextPart"); dStr(d, k + 2, "Text", p->text); }
                else { dType(d, k + 1, "-", "InterpolationExprPart"); dExpr(d, k + 2, "Expression", p->expression); dStr(d, k + 2, "Format", p->format); }
            }
            break;
        }
        case EK_ThrowExpr: dExpr(d, k, "Value", ((const FcThrowExpr*)e)->value); break;
        case EK_UnaryExpr: { const FcUnaryExpr* x = (const FcUnaryExpr*)e; dEnum(d, k, "Op", fcUnaryOpNames[x->op]); dExpr(d, k, "Operand", x->operand); break; }
        case EK_BinaryExpr: {
            const FcBinaryExpr* x = (const FcBinaryExpr*)e;
            dEnum(d, k, "Op", fcBinaryOpNames[x->op]); dExpr(d, k, "Left", x->left); dExpr(d, k, "Right", x->right); dBool(d, k, "FloatPower", x->floatPower);
            break;
        }
        case EK_RebindExpr: { const FcRebindExpr* x = (const FcRebindExpr*)e; dExpr(d, k, "Lambda", x->lambda); dExpr(d, k, "Target", x->target); break; }
        case EK_ConditionalExpr: {
            const FcConditionalExpr* x = (const FcConditionalExpr*)e;
            dExpr(d, k, "Condition", x->condition); dExpr(d, k, "Then", x->then); dExpr(d, k, "Else", x->otherwise);
            break;
        }
        case EK_UnitCoerceExpr: {
            const FcUnitCoerceExpr* x = (const FcUnitCoerceExpr*)e;
            dExpr(d, k, "Operand", x->operand); dStr(d, k, "TargetUnitName", x->targetUnitName); dBool(d, k, "Relabel", x->relabel);
            break;
        }
        case EK_TypeCoerceExpr: {
            const FcTypeCoerceExpr* x = (const FcTypeCoerceExpr*)e;
            dExpr(d, k, "Operand", x->operand);
            if (x->targetTypeKeyword < 0) dNull(d, k, "TargetTypeKeyword"); else dEnum(d, k, "TargetTypeKeyword", fcTokenTypeName((FcTokenType)x->targetTypeKeyword));
            dInt(d, k, "Width", x->width); dBool(d, k, "Unsigned", x->isUnsigned); dTypeRef(d, k, "TargetClass", x->targetClass);
            break;
        }
        case EK_IsInExpr: { const FcIsInExpr* x = (const FcIsInExpr*)e; dExpr(d, k, "Operand", x->operand); dStr(d, k, "UnitName", x->unitName); break; }
        case EK_RangeExpr: { const FcRangeExpr* x = (const FcRangeExpr*)e; dExpr(d, k, "From", x->from); dExpr(d, k, "To", x->to); break; }
        case EK_IsOfExpr: { const FcIsOfExpr* x = (const FcIsOfExpr*)e; dExpr(d, k, "Operand", x->operand); dTypeRef(d, k, "TypeRef", x->typeRef); break; }
        case EK_IsFromExpr: {
            const FcIsFromExpr* x = (const FcIsFromExpr*)e;
            dExpr(d, k, "Operand", x->operand); dExpr(d, k, "OwnerExpr", x->ownerExpr); dBool(d, k, "Transitive", x->transitive); dStr(d, k, "Place", x->place);
            break;
        }
        case EK_UntilExpr: { const FcUntilExpr* x = (const FcUntilExpr*)e; dExpr(d, k, "Inner", x->inner); dExprList(d, k, "Limits", x->limits); break; }
        case EK_AwaitOnExpr: { const FcAwaitOnExpr* x = (const FcAwaitOnExpr*)e; dExpr(d, k, "Lambda", x->lambda); dExprList(d, k, "Args", x->args); break; }
        case EK_CallExpr: {
            const FcCallExpr* x = (const FcCallExpr*)e;
            dExpr(d, k, "Callee", x->callee); dExprList(d, k, "Args", x->args); dTypeRefList(d, k, "TypeArgs", x->typeArgs); dStrList(d, k, "ArgNames", x->argNames);
            dBool(d, k, "Awaited", x->awaited); dBool(d, k, "Dedicated", x->dedicated);
            break;
        }
        case EK_MemberExpr: { const FcMemberExpr* x = (const FcMemberExpr*)e; dExpr(d, k, "Target", x->target); dStr(d, k, "Name", x->name); break; }
        case EK_IndexExpr: { const FcIndexExpr* x = (const FcIndexExpr*)e; dExpr(d, k, "Target", x->target); dExpr(d, k, "Index", x->index); break; }
        case EK_AssignExpr: {
            const FcAssignExpr* x = (const FcAssignExpr*)e;
            dExpr(d, k, "Target", x->target); dExpr(d, k, "Value", x->value); dBool(d, k, "Replace", x->replace);
            if (x->parenthesized) dBool(d, k, "Parenthesized", true);
            break;
        }
        case EK_WithExpr: {
            const FcWithExpr* x = (const FcWithExpr*)e;
            dExpr(d, k, "Source", x->source);
            if (x->copy == UOP__NONE) dNull(d, k, "Copy"); else dEnum(d, k, "Copy", fcUnaryOpNames[x->copy]);
            if (dList(d, k, "Sets", x->sets))
                for (i = 0; i < x->sets->count; i++) {
                    const FcWithSet* s = (const FcWithSet*)x->sets->items[i];
                    dType(d, k + 1, "-", "WithSet");
                    dInt(d, k + 2, "Line", s->line); dStr(d, k + 2, "Name", s->name); dExpr(d, k + 2, "Value", s->value);
                }
            break;
        }
        case EK_IncDecExpr: {
            const FcIncDecExpr* x = (const FcIncDecExpr*)e;
            dExpr(d, k, "Target", x->target); dBool(d, k, "IsIncrement", x->isIncrement); dBool(d, k, "IsPrefix", x->isPrefix);
            break;
        }
        default: break;
    }
}

static void dAttributes(FcDumper* d, int ind, const char* label, const FcList* l) {
    int i;
    if (!dList(d, ind, label, l)) return;
    for (i = 0; i < l->count; i++) {
        const FcAttributeDecl* a = (const FcAttributeDecl*)l->items[i];
        dType(d, ind + 1, "-", "AttributeDecl");
        dInt(d, ind + 2, "Line", a->line); dStr(d, ind + 2, "Name", a->name); dExprList(d, ind + 2, "Args", a->args); dStrList(d, ind + 2, "ArgTexts", a->argTexts);
    }
}
static void dTypeParams(FcDumper* d, int ind, const char* label, const FcList* l) {
    int i, g, k;
    if (!dList(d, ind, label, l)) return;
    for (i = 0; i < l->count; i++) {
        const FcTypeParam* p = (const FcTypeParam*)l->items[i];
        dType(d, ind + 1, "-", "TypeParam");
        dStr(d, ind + 2, "Name", p->name);
        if (!dList(d, ind + 2, "ConstraintGroups", p->constraintGroups)) continue;
        for (g = 0; g < p->constraintGroups->count; g++) {
            const FcTypeConstraintGroup* grp = (const FcTypeConstraintGroup*)p->constraintGroups->items[g];
            dType(d, ind + 3, "-", "TypeConstraintGroup");
            if (!dList(d, ind + 4, "Constraints", grp->constraints)) continue;
            for (k = 0; k < grp->constraints->count; k++) {
                const FcTypeConstraint* tc = (const FcTypeConstraint*)grp->constraints->items[k];
                dType(d, ind + 5, "-", "TypeConstraint");
                dEnum(d, ind + 6, "Kind", fcConstraintNames[tc->kind]); dStr(d, ind + 6, "Name", tc->name);
            }
        }
    }
}

static void dStmt(FcDumper* d, int ind, const char* label, const FcStmt* s) {
    int k = ind + 1, i;
    if (!s) { dNull(d, ind, label); return; }
    dType(d, ind, label, fcStmtKindNames[s->kind]);
    dInt(d, k, "Source", s->source);
    dInt(d, k, "Line", s->line);
    switch (s->kind) {
        case SK_BlockStmt: dStmtList(d, k, "Statements", ((const FcBlockStmt*)s)->statements); break;
        case SK_ExprStmt: dExpr(d, k, "Expression", ((const FcExprStmt*)s)->expression); break;
        case SK_NoOpStmt: case SK_NoShadowDirective: case SK_SectionEnterStmt: case SK_SectionExitStmt: case SK_LeaveStmt: break;
        case SK_GlobalJobsDirective: dEnum(d, k, "Mode", fcGlobalJobsNames[((const FcGlobalJobsDirective*)s)->mode]); break;
        case SK_TypeCheckDirective: dBool(d, k, "Strict", ((const FcTypeCheckDirective*)s)->strict); break;
        case SK_TimeoutDirective: case SK_EndWaitDirective: dExpr(d, k, "Value", ((const FcTimeoutDirective*)s)->value); break;
        case SK_EnumDecl: {
            const FcEnumDecl* x = (const FcEnumDecl*)s;
            dStr(d, k, "Name", x->name);
            if (dList(d, k, "Members", x->members))
                for (i = 0; i < x->members->count; i++) {
                    const FcEnumMember* m = (const FcEnumMember*)x->members->items[i];
                    dType(d, k + 1, "-", "EnumMember");
                    dStr(d, k + 2, "Name", m->name); dExpr(d, k + 2, "ValueExpr", m->valueExpr);
                }
            dBool(d, k, "IsFlags", x->isFlags);
            break;
        }
        case SK_VarDeclStmt: {
            const FcVarDeclStmt* x = (const FcVarDeclStmt*)s;
            dStr(d, k, "Name", x->name); dTypeRef(d, k, "Type", x->type); dExprList(d, k, "ArrayRanks", x->arrayRanks); dExpr(d, k, "Initializer", x->initializer);
            dBool(d, k, "IsReadonly", x->isReadonly);
            if (x->switchValues) dExprList(d, k, "SwitchValues", x->switchValues);
            break;
        }
        case SK_IfStmt: { const FcIfStmt* x = (const FcIfStmt*)s; dExpr(d, k, "Condition", x->condition); dStmt(d, k, "Then", x->then); dStmt(d, k, "Else", x->otherwise); break; }
        case SK_WhileStmt: { const FcWhileStmt* x = (const FcWhileStmt*)s; dExpr(d, k, "Condition", x->condition); dStmt(d, k, "Body", x->body); break; }
        case SK_DoWhileStmt: { const FcDoWhileStmt* x = (const FcDoWhileStmt*)s; dStmt(d, k, "Body", x->body); dExpr(d, k, "Condition", x->condition); break; }
        case SK_UnitDeclStmt: {
            const FcUnitDeclStmt* x = (const FcUnitDeclStmt*)s;
            dStr(d, k, "Name", x->name); dStr(d, k, "Symbol", x->symbol); dBool(d, k, "Prefixed", x->prefixed);
            if (dList(d, k, "Coerces", x->coerces))
                for (i = 0; i < x->coerces->count; i++) {
                    const FcUnitCoerce* u = (const FcUnitCoerce*)x->coerces->items[i];
                    dType(d, k + 1, "-", "UnitCoerce");
                    dInt(d, k + 2, "Line", u->line); dEnum(d, k + 2, "Direction", fcCoerceNames[u->direction]); dStr(d, k + 2, "TargetUnit", u->targetUnit);
                    dExpr(d, k + 2, "Conversion", u->conversion);
                }
            break;
        }
        case SK_ForStmt: {
            const FcForStmt* x = (const FcForStmt*)s;
            dStmt(d, k, "Init", x->init); dExpr(d, k, "Condition", x->condition); dExpr(d, k, "Increment", x->increment); dStmt(d, k, "Body", x->body);
            break;
        }
        case SK_ForeachStmt: {
            const FcForeachStmt* x = (const FcForeachStmt*)s;
            dStr(d, k, "VarName", x->varName); dExpr(d, k, "Iterable", x->iterable); dStmt(d, k, "Body", x->body);
            break;
        }
        case SK_ReturnStmt: case SK_ThrowStmt: case SK_TerminateStmt: dExpr(d, k, "Value", ((const FcReturnStmt*)s)->value); break;
        case SK_DeleteStmt: case SK_ProcessStmt: dExpr(d, k, "Target", ((const FcDeleteStmt*)s)->target); break;
        case SK_BreakStmt: case SK_ContinueStmt: dInt(d, k, "Levels", ((const FcBreakStmt*)s)->levels); break;
        case SK_FireStmt: {
            const FcFireStmt* x = (const FcFireStmt*)s;
            if (dList(d, k, "TakingCaptures", x->takingCaptures))
                for (i = 0; i < x->takingCaptures->count; i++) {
                    const FcFireTakingCapture* t = (const FcFireTakingCapture*)x->takingCaptures->items[i];
                    dType(d, k + 1, "-", "FireTakingCapture");
                    dStr(d, k + 2, "VarName", t->varName); dExpr(d, k + 2, "Source", t->source); dBool(d, k + 2, "Handover", t->handover); dBool(d, k + 2, "Implicit", t->isImplicit);
                }
            if (dList(d, k, "With", x->with))
                for (i = 0; i < x->with->count; i++) {
                    const FcFireWith* w = (const FcFireWith*)x->with->items[i];
                    dType(d, k + 1, "-", "FireWith");
                    dStr(d, k + 2, "VarName", w->varName); dExpr(d, k + 2, "Source", w->source);
                }
            dStmt(d, k, "Body", (const FcStmt*)x->body); dBool(d, k, "Dedicated", x->dedicated); dExprList(d, k, "Until", x->until);
            break;
        }
        case SK_SilenceStmt: {
            const FcSilenceStmt* x = (const FcSilenceStmt*)s;
            dExpr(d, k, "Target", x->target); dStr(d, k, "Member", x->member); dBool(d, k, "MemberForm", x->memberForm);
            break;
        }
        case SK_PostGlobalStmt: {
            const FcPostGlobalStmt* x = (const FcPostGlobalStmt*)s;
            dExpr(d, k, "Lambda", x->lambda); dExprList(d, k, "Args", x->args); dBool(d, k, "OnTarget", x->onTarget); dInt(d, k, "WithCount", x->withCount);
            break;
        }
        case SK_CatchThreadsDecl: {
            const FcCatchThreadsDecl* x = (const FcCatchThreadsDecl*)s;
            dTypeRef(d, k, "TypeRef", x->typeRef); dStr(d, k, "VarName", x->varName); dStmt(d, k, "Body", (const FcStmt*)x->body);
            break;
        }
        case SK_CatchTerminateDecl: {
            const FcCatchTerminateDecl* x = (const FcCatchTerminateDecl*)s;
            dStr(d, k, "VarName", x->varName); dStmt(d, k, "Body", (const FcStmt*)x->body);
            break;
        }
        case SK_TryStmt: {
            const FcTryStmt* x = (const FcTryStmt*)s;
            dStmt(d, k, "TryBlock", (const FcStmt*)x->tryBlock);
            if (dList(d, k, "Catches", x->catches))
                for (i = 0; i < x->catches->count; i++) {
                    const FcCatchClause* cc = (const FcCatchClause*)x->catches->items[i];
                    dType(d, k + 1, "-", "CatchClause");
                    dInt(d, k + 2, "Source", cc->source); dInt(d, k + 2, "Line", cc->line); dTypeRef(d, k + 2, "TypeRef", cc->typeRef);
                    dStr(d, k + 2, "VarName", cc->varName); dStmt(d, k + 2, "Body", (const FcStmt*)cc->body);
                }
            dStmt(d, k, "Finally", (const FcStmt*)x->finallyBlock); dBool(d, k, "IsSyncSection", x->isSyncSection);
            break;
        }
        case SK_FieldDecl: {
            const FcFieldDecl* x = (const FcFieldDecl*)s;
            dTypeRef(d, k, "Type", x->type); dExprList(d, k, "ArrayRanks", x->arrayRanks); dStr(d, k, "Name", x->name); dExpr(d, k, "Initializer", x->initializer);
            dBool(d, k, "IsReadonly", x->isReadonly); dEnum(d, k, "Access", fcAccessNames[x->access]); dBool(d, k, "IsStatic", x->isStatic);
            dAttributes(d, k, "Attributes", x->attributes);
            break;
        }
        case SK_MethodDecl: {
            const FcMethodDecl* x = (const FcMethodDecl*)s;
            dTypeRef(d, k, "ReturnType", x->returnType); dStr(d, k, "Name", x->name); dParams(d, k, "Params", x->params); dStmt(d, k, "Body", (const FcStmt*)x->body);
            dTypeParams(d, k, "TypeParams", x->typeParams); dEnum(d, k, "Access", fcAccessNames[x->access]); dBool(d, k, "IsStatic", x->isStatic);
            dAttributes(d, k, "Attributes", x->attributes); dBool(d, k, "IsOverride", x->isOverride); dTypeRefList(d, k, "Specialization", x->specialization);
            break;
        }
        case SK_ConstructorDecl: {
            const FcConstructorDecl* x = (const FcConstructorDecl*)s;
            dParams(d, k, "Params", x->params); dExprList(d, k, "BaseArgs", x->baseArgs); dStmt(d, k, "Body", (const FcStmt*)x->body); dEnum(d, k, "Access", fcAccessNames[x->access]);
            break;
        }
        case SK_DestructorDecl: case SK_UnsafeStmt: dStmt(d, k, "Body", (const FcStmt*)((const FcDestructorDecl*)s)->body); break;
        case SK_ClassDecl: {
            const FcClassDecl* x = (const FcClassDecl*)s;
            dStr(d, k, "Name", x->name); dTypeRefList(d, k, "BaseRefs", x->baseRefs); dStmtList(d, k, "Members", x->members); dTypeParams(d, k, "TypeParams", x->typeParams);
            dBool(d, k, "IsActor", x->isActor); dAttributes(d, k, "Attributes", x->attributes); dBool(d, k, "IsRecord", x->isRecord);
            break;
        }
        case SK_InterfaceDecl: {
            const FcInterfaceDecl* x = (const FcInterfaceDecl*)s;
            dStr(d, k, "Name", x->name);
            if (dList(d, k, "Methods", x->methods))
                for (i = 0; i < x->methods->count; i++) {
                    const FcInterfaceMethodSig* m = (const FcInterfaceMethodSig*)x->methods->items[i];
                    dType(d, k + 1, "-", "InterfaceMethodSig");
                    dInt(d, k + 2, "Line", m->line); dTypeRef(d, k + 2, "ReturnType", m->returnType); dStr(d, k + 2, "Name", m->name); dParams(d, k + 2, "Params", m->params);
                }
            dTypeParams(d, k, "TypeParams", x->typeParams); dTypeRefList(d, k, "BaseRefs", x->baseRefs);
            if (dList(d, k, "Properties", x->properties))
                for (i = 0; i < x->properties->count; i++) {
                    const FcInterfacePropertySig* p = (const FcInterfacePropertySig*)x->properties->items[i];
                    dType(d, k + 1, "-", "InterfacePropertySig");
                    dInt(d, k + 2, "Line", p->line); dTypeRef(d, k + 2, "Type", p->type); dStr(d, k + 2, "Name", p->name); dBool(d, k + 2, "HasGet", p->hasGet);
                    dBool(d, k + 2, "HasSet", p->hasSet);
                }
            break;
        }
        case SK_ExternDecl: {
            const FcExternDecl* x = (const FcExternDecl*)s;
            dTypeRef(d, k, "ReturnType", x->returnType); dStr(d, k, "Name", x->name); dParams(d, k, "Params", x->params); dStr(d, k, "LibName", x->libName);
            dStr(d, k, "InlineC", x->inlineC); dInt(d, k, "InlineLine", x->inlineLine); dStr(d, k, "DisplayName", x->displayName);
            break;
        }
        case SK_PropertyDecl: {
            const FcPropertyDecl* x = (const FcPropertyDecl*)s;
            dTypeRef(d, k, "Type", x->type); dStr(d, k, "Name", x->name); dStmt(d, k, "Getter", (const FcStmt*)x->getter); dStmt(d, k, "Setter", (const FcStmt*)x->setter);
            dEnum(d, k, "Access", fcAccessNames[x->access]); dBool(d, k, "IsStatic", x->isStatic); dAttributes(d, k, "Attributes", x->attributes);
            dBool(d, k, "IsOverride", x->isOverride);
            break;
        }
        case SK_ClassExtensionDecl: {
            const FcClassExtensionDecl* x = (const FcClassExtensionDecl*)s;
            dTypeRef(d, k, "TargetRef", x->targetRef); dStmtList(d, k, "Members", x->members);
            break;
        }
        case SK_NamespaceDecl: { const FcNamespaceDecl* x = (const FcNamespaceDecl*)s; dStr(d, k, "Name", x->name); dStmtList(d, k, "Members", x->members); break; }
        default: break;
    }
}

void fcDumpProgram(FcCtx* c, FcBuf* out, FcList* program) {
    FcDumper d;
    (void)c;
    d.o = out;
    dStmtList(&d, 0, "program", program);
}

void fcDumpDeclared(FcBuf* out, const FcDeclaredWarning* w) {
    fcBufStr(out, "declared ");
    fcEsc(out, w->cls);
    fcBufFmt(out, " %s ", fcWarnTargetNames[w->target]);
    if (w->name) fcEsc(out, w->name); else fcBufStr(out, "null");
    fcBufFmt(out, " %d %d %s W%d ", w->minArity, w->maxArity, w->isStatic ? "static" : "instance", w->id);
    fcEsc(out, w->text);
    fcBufFmt(out, " line %d ", w->line);
    if (w->file) fcEsc(out, w->file); else fcBufStr(out, "null");
    fcBufChar(out, '\n');
}
