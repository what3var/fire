/* native/forge/compiler2.c - port of src/fire.Compiler/Compiler.cs (commit 6bc618e7), part 2: from CompileTry's successor on - the expressions
 * (CompileExpr with the unit checks and guards the type checker left), lambdas, arguments, calls, assignments, `++`/`--`, unary and binary
 * operators and the anchor rule (EmitCoercedOperands, AnalyzeCoercion). Method by method in the order of the C# file. */

#define FC_HANDOVER_NATIVE "__lambda_handover"

static FcExpr* TreeCall(FcComp* cm, FcLambdaExpr* lam);

static void NoteReceiverTypeArgs(FcComp* cm, FcExpr* at) {
    void* text;
    if (cm->sh->rr->receiverTypeArgs && fcDictTry(cm->sh->rr->receiverTypeArgs, at, &text)) fcChunkReceiverTypeArgs(CC_, CH_, CH_->len, (const char*)text);
}

static void EmitOverloadHint(FcComp* cm, FcExpr* call) {
    void* hint;
    if (cm->sh->rr->overloadHints == NULL || cm->sh->noOverloadHints || !fcDictTry(cm->sh->rr->overloadHints, call, &hint)) return;
    if (call->kind == EK_CallExpr && ((FcCallExpr*)call)->argNames != NULL) return;
    if (call->kind == EK_NewExpr && ((FcNewExpr*)call)->argNames != NULL) return;
    E_op(cm, OP_OverloadHint);
    E_u16(cm, E_str(cm, (const char*)hint));
}

static void EmitLocalGuard(FcComp* cm, FcTypeRef* type, const char* what) {
    int kind = fcDkOfStore(type, NULL);
    const char* spec;
    if (kind != 0) {
        E_op(cm, OP_GuardTop);
        E_u8(cm, ((kind & 0x7F) - 1) & 0xFF);
        E_u16(cm, E_str(cm, what));
    }
    if (kind != 0 && !fcDkIsString(kind)) return;
    if ((spec = DeclaredTypeOf(cm, type, cm->enclosingClass)) != NULL) {
        E_op(cm, OP_GuardTopType);
        E_u16(cm, E_str(cm, spec));
        E_u16(cm, E_str(cm, what));
    }
}

static void EmitCallTypeArgs(FcComp* cm, FcCallExpr* call) {
    FcList* texts;
    int i;
    bool any = false;
    EmitOverloadHint(cm, (FcExpr*)call);
    if (call->typeArgs && call->typeArgs->count > 0) {
        FcList* written = fcListNew(CC_);
        for (i = 0; i < call->typeArgs->count; i++) fcListAdd(CC_, written, (void*)TypeArgText(cm, (const FcTypeRef*)call->typeArgs->items[i], cm->enclosingClass, NULL, 0));
        E_op(cm, OP_SetTypeArgs);
        E_u16(cm, E_str(cm, MapTypeRefs(cm, written)));
        E_u8(cm, 3);
        return;
    }
    if (cm->sh->rr->methodTypeArgs == NULL || !(texts = (FcList*)fcDictGet(cm->sh->rr->methodTypeArgs, call))) return;
    for (i = 0; i < texts->count; i++) if (((const char*)texts->items[i])[0]) any = true;
    if (!any) return;
    E_op(cm, OP_SetTypeArgs);
    E_u16(cm, E_str(cm, MapTypeRefs(cm, texts)));
    E_u8(cm, 1);
}

static void EmitReturnGuard(FcComp* cm) {
    if (cm->returnSpec != NULL) {
        E_op(cm, OP_GuardTopType);
        E_u16(cm, E_str(cm, cm->returnSpec));
        E_u16(cm, E_str(cm, cm->returnWhat));
        return;
    }
    if (cm->returnKind == 0) return;
    E_op(cm, OP_GuardTop);
    E_u8(cm, ((cm->returnKind & 0x7F) - 1) & 0xFF);
    E_u16(cm, E_str(cm, cm->returnWhat));
}

static int StaticWideOfCode(int code) { return code == 64 || code == 32 ? code : 0; }
static int StaticWide(FcComp* cm, FcExpr* e) {
    switch (e->kind) {
        case EK_IdentifierExpr: {
            FcResolvedRef* r = (FcResolvedRef*)fcDictGet(cm->sh->refs, e);
            if (!r) return 0;
            if (r->kind == RR_Local || r->kind == RR_Global) return StaticWideOfCode(r->width);
            if (r->kind == RR_ImplicitThisMember) return StaticWideOfCode(fcRpFieldWidth(cm, ((FcIdentifierExpr*)e)->name));
            return 0;
        }
        case EK_MemberExpr: return StaticWideOfCode(fcRpFieldWidth(cm, ((FcMemberExpr*)e)->name));
        case EK_TypeCoerceExpr: {
            FcTypeCoerceExpr* c = (FcTypeCoerceExpr*)e;
            if ((c->width == 64 || c->width == 32) && !c->isUnsigned && c->targetTypeKeyword != TT_KwFloat) return c->width;
            return 0;
        }
        case EK_BinaryExpr: {
            FcBinaryExpr* b = (FcBinaryExpr*)e;
            int l, r;
            if (b->op == BOP_And || b->op == BOP_Or) return 0;
            l = StaticWide(cm, b->left); r = StaticWide(cm, b->right);
            return l > r ? l : r;
        }
        case EK_UnaryExpr: {
            FcUnaryExpr* u = (FcUnaryExpr*)e;
            return u->op == UOP_Negate || u->op == UOP_BitNot ? StaticWide(cm, u->operand) : 0;
        }
        case EK_AssignExpr: return StaticWide(cm, ((FcAssignExpr*)e)->target);
        default: return 0;
    }
}

static void CompileExprCore(FcComp* cm, FcExpr* expr);
static FcMemberExpr* fcNewMember(FcCtx* c, int line, FcExpr* target, const char* name) { return FC_NEW(c, FcMemberExpr, .h = { EK_MemberExpr, line }, .target = target, .name = name); }
static FcLiteralExpr* fcNewLiteral(FcCtx* c, int line, FcValue v) { return FC_NEW(c, FcLiteralExpr, .h = { EK_LiteralExpr, line }, .value = v); }
static FcExpr* fcUndefinedLiteral(FcCtx* c, int line) { FcValue v; memset(&v, 0, sizeof v); v.kind = VK_Undefined; return (FcExpr*)fcNewLiteral(c, line, v); }

static void fcCompileTreeValue(FcComp* cm, FcExpr* expr) {
    FcCtx* c = CC_;
    FC_TRY(c) {
        CompileExpr(cm, (FcExpr*)fcNewMember(c, expr->line, expr, "Compiled"));
    } FC_CATCH(c) {
        fcDictRemove(cm->sh->rawTreeValues, expr);
        fcRethrow(c);
    } FC_END_TRY;
    fcDictRemove(cm->sh->rawTreeValues, expr);
}

static void CompileExpr(FcComp* cm, FcExpr* expr) {
    FcCtx* c = CC_;
    FcResolveResult* rr = cm->sh->rr;
    volatile int outerWide = cm->wideLiteral;
    if (rr->treeToCompiled && fcDictCount(rr->treeToCompiled) > 0 && fcDictHas(rr->treeToCompiled, expr) && fcSetAdd(cm->sh->rawTreeValues, expr)) {
        fcCompileTreeValue(cm, expr);
        return;
    }
    switch (expr->kind) {
        case EK_LiteralExpr: break;
        case EK_BinaryExpr:
            if (((FcBinaryExpr*)expr)->op != BOP_And && ((FcBinaryExpr*)expr)->op != BOP_Or) { int w = StaticWide(cm, expr); cm->wideLiteral = outerWide > w ? outerWide : w; }
            else cm->wideLiteral = 0;
            break;
        case EK_UnaryExpr: if (((FcUnaryExpr*)expr)->op != UOP_Negate && ((FcUnaryExpr*)expr)->op != UOP_BitNot) cm->wideLiteral = 0; break;
        case EK_TypeCoerceExpr:
            if (((FcTypeCoerceExpr*)expr)->width > 0) { int w = StaticWide(cm, expr); cm->wideLiteral = outerWide > w ? outerWide : w; }
            else cm->wideLiteral = 0;
            break;
        case EK_AssignExpr: cm->wideLiteral = StaticWide(cm, expr); break;
        default: cm->wideLiteral = 0; break;
    }
    FC_TRY(c) {
        void* demand;
        void* guard;
        CompileExprCore(cm, expr);
        if (rr->unitChecks && fcDictTry(rr->unitChecks, expr, &demand)) EmitCheckUnitIfNeeded(cm, (const char*)demand);
        if (rr->localGuards && fcDictTry(rr->localGuards, expr, &guard)) EmitLocalGuard(cm, ((FcLocalGuard*)guard)->type, ((FcLocalGuard*)guard)->what);
    } FC_CATCH(c) {
        cm->wideLiteral = outerWide;
        if (c->err.kind == FC_ERR_NOTSUPPORTED) fcThrow(c, FC_ERR_COMPILE, expr->line, 0, NULL, c->err.message);
        fcRethrow(c);
    } FC_END_TRY;
    cm->wideLiteral = outerWide;
}

static void CompileExprFor(FcComp* cm, FcExpr* value, int widthCode) {
    FcCtx* c = CC_;
    volatile int outer = cm->wideLiteral;
    cm->wideLiteral = widthCode == 64 || widthCode == 32 ? widthCode : 0;
    FC_TRY(c) {
        CompileExpr(cm, value);
    } FC_CATCH(c) {
        cm->wideLiteral = outer;
        fcRethrow(c);
    } FC_END_TRY;
    cm->wideLiteral = outer;
}

static void CompileIdentifierLoad(FcComp* cm, FcIdentifierExpr* id);
static void CompileUnary(FcComp* cm, FcUnaryExpr* u);
static void CompileBinary(FcComp* cm, FcBinaryExpr* b);
static void CompileWith(FcComp* cm, FcWithExpr* with);
static void CompileStandaloneCoercion(FcComp* cm, FcExpr* expr);
static void CompileIncDec(FcComp* cm, FcIncDecExpr* e);
static void CompileCall(FcComp* cm, FcCallExpr* call);
static void EmitArgNames(FcComp* cm, FcList* names);
static bool fcProvenMember(FcComp* cm, FcExpr* e) { return cm->sh->rr->provenMembers && fcDictHas(cm->sh->rr->provenMembers, e); }

static const char* fcExprKindName(FcExprKind k) {
    static const char* const names[] = {
#define FC_EN(n) #n,
        FC_EXPR_KINDS(FC_EN)
#undef FC_EN
    };
    return names[k];
}

static void CompileExprCore(FcComp* cm, FcExpr* expr) {
    FcCtx* c = CC_;
    int i;
    switch (expr->kind) {
        case EK_LiteralExpr: {
            FcLiteralExpr* lit = (FcLiteralExpr*)expr;
            FcConst* v = fcConstOfValue(c, &lit->value);
            EmitLoadConst(cm, cm->wideLiteral != 0 && v->kind == CK_Int && v->en == NULL ? fcConstWithWide(c, v, cm->wideLiteral) : v);
            break;
        }
        case EK_TypeBitsExpr: {
            void* bits;
            FcTypeBitsExpr* tb = (FcTypeBitsExpr*)expr;
            EmitLoadConst(cm, fcMakeInt(c, cm->sh->bitsOf && fcDictTry(cm->sh->bitsOf, expr, &bits) ? FC_VALI(bits) - 1 : (tb->width != FC_NULL_INT ? tb->width : 64)));
            break;
        }
        case EK_IdentifierExpr: CompileIdentifierLoad(cm, (FcIdentifierExpr*)expr); break;
        case EK_UnaryExpr: CompileUnary(cm, (FcUnaryExpr*)expr); break;
        case EK_BinaryExpr: CompileBinary(cm, (FcBinaryExpr*)expr); break;
        case EK_RebindExpr:
            CompileExpr(cm, ((FcRebindExpr*)expr)->lambda);
            CompileExpr(cm, ((FcRebindExpr*)expr)->target);
            E_op(cm, OP_RebindLambda);
            break;
        case EK_WithExpr: CompileWith(cm, (FcWithExpr*)expr); break;
        case EK_ConditionalAccessExpr: {
            FcConditionalAccessExpr* access = (FcConditionalAccessExpr*)expr;
            int skipAt, doneAt;
            CompileExpr(cm, access->target);
            E_op(cm, OP_Dup);
            if (access->checkMember && !fcProvenMember(cm, expr)) {
                E_op(cm, OP_HasMember);
                E_u16(cm, E_str(cm, access->member));
            } else {
                E_op(cm, OP_IsOfType);
                E_u16(cm, E_str(cm, "undefined"));
                E_op(cm, OP_LogicalNot);
            }
            skipAt = EmitJumpIfFalse(cm);
            CompileExpr(cm, access->inner);
            E_op(cm, OP_Jump);
            doneAt = E_here(cm);
            E_u16(cm, 0);
            E_patch(cm, skipAt, E_here(cm));
            E_op(cm, OP_Pop);
            CompileExpr(cm, fcUndefinedLiteral(c, access->h.line));
            E_patch(cm, doneAt, E_here(cm));
            break;
        }
        case EK_ConditionalTargetExpr: break;
        case EK_ConditionalExpr: {
            FcConditionalExpr* ce = (FcConditionalExpr*)expr;
            int elseJumpAt, endJumpAt;
            CompileExpr(cm, ce->condition);
            elseJumpAt = EmitJumpIfFalse(cm);
            CompileExpr(cm, ce->then);
            E_op(cm, OP_Jump);
            endJumpAt = E_here(cm);
            E_u16(cm, 0);
            E_patch(cm, elseJumpAt, E_here(cm));
            CompileExpr(cm, ce->otherwise);
            E_patch(cm, endJumpAt, E_here(cm));
            break;
        }
        case EK_UnitCoerceExpr: {
            FcUnitCoerceExpr* uc = (FcUnitCoerceExpr*)expr;
            if (uc->relabel && uc->targetUnitName != NULL) {
                CompileExpr(cm, uc->operand);
                E_op(cm, OP_RelabelUnit);
                E_u16(cm, fcChunkAddUnit(c, CH_, fcUParse(c, uc->targetUnitName)));
            } else CompileStandaloneCoercion(cm, expr);
            break;
        }
        case EK_TypeCoerceExpr: {
            FcTypeCoerceExpr* tc = (FcTypeCoerceExpr*)expr;
            FcEnumType* enumType;
            if (tc->targetClass != NULL && (enumType = EnumTypeNamed(cm, tc->targetClass->baseName)) != NULL) {
                CompileExpr(cm, tc->operand);
                E_op(cm, OP_ToEnum);
                E_u16(cm, E_const(cm, fcMakeEnum(c, 0, enumType)));
            } else if (tc->targetClass != NULL) {
                CompileExpr(cm, tc->operand);
                E_op(cm, OP_CastTo);
                E_u16(cm, E_str(cm, ResolveTypeRef(cm, tc->targetClass)));
            } else if (tc->targetTypeKeyword == TT_KwWord) {
                CompileExpr(cm, tc->operand);
                E_op(cm, OP_ConvertWidth);
                E_u8(cm, 0);
                E_u8(cm, 8);
            } else if (tc->width > 0) {
                CompileExpr(cm, tc->operand);
                E_op(cm, OP_ConvertWidth);
                E_u8(cm, tc->width & 0xFF);
                E_u8(cm, (tc->targetTypeKeyword == TT_KwFloat ? 1 : 0) | (tc->isUnsigned ? 2 : 0));
            } else CompileStandaloneCoercion(cm, expr);
            break;
        }
        case EK_AssignExpr: {
            FcAssignExpr* a = (FcAssignExpr*)expr;
            CompileAssign(cm, a);
            if (a->value->kind == EK_UnaryExpr && ((FcUnaryExpr*)a->value)->op == UOP_TryTake) { E_op(cm, OP_Pop); E_op(cm, OP_TookResult); }
            break;
        }
        case EK_IncDecExpr: CompileIncDec(cm, (FcIncDecExpr*)expr); break;
        case EK_ThisExpr: E_op(cm, OP_LoadThis); break;
        case EK_BaseExpr: fcCompNotSupported(cm, "'base' is only valid as 'base.Method(...)', not as a standalone value.");
        case EK_RangeExpr:
            CompileExpr(cm, ((FcRangeExpr*)expr)->from);
            CompileExpr(cm, ((FcRangeExpr*)expr)->to);
            E_op(cm, OP_CallStaticMethod);
            E_u16(cm, E_str(cm, "__Range"));
            E_u16(cm, E_str(cm, "Of"));
            E_u8(cm, 2);
            break;
        case EK_IsInExpr:
            CompileExpr(cm, ((FcIsInExpr*)expr)->operand);
            E_op(cm, OP_IsInUnit);
            E_u16(cm, fcChunkAddUnit(c, CH_, fcUParse(c, ((FcIsInExpr*)expr)->unitName)));
            break;
        case EK_IsOfExpr: {
            FcIsOfExpr* iof = (FcIsOfExpr*)expr;
            if (iof->operand->kind == EK_IdentifierExpr && !fcDictHas(cm->sh->refs, iof->operand)) {
                const char* tpIsOfText = TypeArgText(cm, fcTrNew(c, ((FcIdentifierExpr*)iof->operand)->name), cm->enclosingClass, NULL, 0);
                if (tpIsOfText[0] == '$') {
                    E_op(cm, OP_TypeParamIsOf);
                    E_u16(cm, E_str(cm, tpIsOfText));
                    E_u16(cm, E_str(cm, ResolveTypeRef(cm, iof->typeRef)));
                    break;
                }
            }
            CompileExpr(cm, iof->operand);
            E_op(cm, OP_IsOfType);
            E_u16(cm, E_str(cm, ResolveTypeRef(cm, iof->typeRef)));
            break;
        }
        case EK_IsFromExpr: {
            FcIsFromExpr* ifr = (FcIsFromExpr*)expr;
            CompileExpr(cm, ifr->operand);
            CompileExpr(cm, ifr->ownerExpr);
            E_op(cm, OP_IsFrom);
            E_u8(cm, streq0(ifr->place, "local") ? 2 : streq0(ifr->place, "global") ? 3 : ifr->transitive ? 1 : 0);
            break;
        }
        case EK_NewExpr: {
            FcNewExpr* ne = (FcNewExpr*)expr;
            uint64_t newCopyMask = CompileArgs(cm, ne->args, true, fcRpForConstructor(cm, ResolveNewClassName(cm, ne), ne->args->count));
            EmitTypeArgs(cm, ne);
            EmitCopyArgsPrefix(cm, newCopyMask);
            EmitArgNames(cm, ne->argNames);
            E_op(cm, OP_NewObject);
            E_u16(cm, E_str(cm, ResolveNewClassName(cm, ne)));
            E_u8(cm, ne->args->count);
            break;
        }
        case EK_NewArrayExpr: {
            FcNewArrayExpr* na = (FcNewArrayExpr*)expr;
            const char* newText = NULL;
            int newCode, openKind;
            FcTypeRef* et = na->elementType;
            CompileArrayAlloc(cm, na->sizeExprs, 0, false);
            if (na->sizeExprs->count == 1 && et && et->arrayRank == 0 && et->pointerDepth == 0 && et->typeArgs == NULL) {
                const char* paramText = TypeArgText(cm, et, cm->enclosingClass, NULL, 0);
                if (paramText[0] == '$') {
                    E_op(cm, OP_TypeArrayOfParam);
                    E_u16(cm, E_str(cm, paramText));
                    break;
                }
            }
            if ((newCode = ClassArrayCode(cm, et, na->sizeExprs->count, cm->enclosingClass, &newText)) != 0) EmitWidthIfNeeded(cm, newCode, newText);
            else if (na->sizeExprs->count > 0 && na->sizeExprs->count < 256 && et && et->arrayRank == 0 && et->pointerDepth == 0 && (openKind = fcArrayOpenKindOf(et)) != 0) {
                E_op(cm, OP_TypeArray);
                E_u8(cm, (openKind | AE_OpenFlag) & 0xFF);
                E_u8(cm, na->sizeExprs->count);
                E_u16(cm, 0xFFFF);
            }
            break;
        }
        case EK_NewBufferExpr: {
            FcNewBufferExpr* nb = (FcNewBufferExpr*)expr;
            CompileExpr(cm, nb->sizeExpr);
            if (nb->words) { E_op(cm, OP_NewArray); EmitWidthIfNeeded(cm, 0x10000 | (1 << 8) | AE_Word, NULL); }
            else E_op(cm, OP_MakeBuffer);
            break;
        }
        case EK_ArrayLiteralExpr: {
            FcArrayLiteralExpr* al = (FcArrayLiteralExpr*)expr;
            uint32_t partMask = 0;
            for (i = 0; i < al->elements->count; i++) CompileExpr(cm, (FcExpr*)al->elements->items[i]);
            if (al->elements->count <= 32)
                for (i = 0; i < al->elements->count; i++) {
                    FcExprKind k = ((FcExpr*)al->elements->items[i])->kind;
                    if (k == EK_ArrayLiteralExpr || k == EK_NewArrayExpr || k == EK_NewBufferExpr) partMask |= 1u << i;
                }
            if (partMask == 0) { E_op(cm, OP_MakeArrayLiteral); E_u16(cm, al->elements->count); }
            else {
                E_op(cm, OP_MakeArrayLiteralParts);
                E_u16(cm, al->elements->count);
                E_u16(cm, (int)(partMask & 0xFFFF));
                E_u16(cm, (int)(partMask >> 16));
            }
            break;
        }
        case EK_InterpolatedStringExpr: {
            FcInterpolatedStringExpr* ise = (FcInterpolatedStringExpr*)expr;
            EmitLoadConst(cm, fcMakeString(c, ""));
            for (i = 0; i < ise->parts->count; i++) {
                FcInterpolationPart* part = (FcInterpolationPart*)ise->parts->items[i];
                if (!part->isExpression) EmitLoadConst(cm, fcMakeString(c, part->text));
                else {
                    CompileExpr(cm, part->expression);
                    if (part->format != NULL) { E_op(cm, OP_FormatValue); E_u16(cm, E_str(cm, part->format)); }
                }
                E_op(cm, OP_Add);
            }
            break;
        }
        case EK_MemberExpr: {
            FcMemberExpr* me = (FcMemberExpr*)expr;
            FcResolvedRef* memberRef = (FcResolvedRef*)fcDictGet(cm->sh->refs, expr);
            void* bits;
            if (streq0(me->name, "Bits") && cm->sh->bitsOf && fcDictTry(cm->sh->bitsOf, expr, &bits)) {
                CompileExpr(cm, me->target);
                E_op(cm, OP_Pop);
                EmitLoadConst(cm, fcMakeInt(c, FC_VALI(bits) - 1));
                break;
            }
            if (memberRef && memberRef->kind == RR_EnumMember) {
                FcEnumType* et = memberRef->enumType ? (FcEnumType*)fcDictGet(cm->sh->enumTypes, memberRef->enumType) : NULL;
                EmitLoadConst(cm, et ? fcMakeEnum(c, memberRef->value, et) : fcMakeInt(c, memberRef->value));
                break;
            }
            if (memberRef && memberRef->kind == RR_StaticMember) {
                E_op(cm, OP_GetStaticField);
                E_u16(cm, E_str(cm, memberRef->className));
                E_u16(cm, E_str(cm, me->name));
                break;
            }
            CompileExpr(cm, me->target);
            E_op(cm, OP_GetField);
            E_u16(cm, E_str(cm, me->name));
            break;
        }
        case EK_IndexExpr:
            CompileExpr(cm, ((FcIndexExpr*)expr)->target);
            CompileExpr(cm, ((FcIndexExpr*)expr)->index);
            NoteReceiverTypeArgs(cm, expr);
            E_op(cm, OP_ArrayGet);
            break;
        case EK_ThrowExpr:
            CompileExpr(cm, ((FcThrowExpr*)expr)->value);
            E_op(cm, OP_Throw);
            break;
        case EK_LambdaExpr: {
            FcLambdaExpr* lam = (FcLambdaExpr*)expr;
            if (cm->sh->rr->treeLambdas && fcDictHas(cm->sh->rr->treeLambdas, lam) && fcSetAdd(cm->sh->rawTreeLambdas, lam)) {
                FC_TRY(c) {
                    CompileExpr(cm, TreeCall(cm, lam));
                } FC_CATCH(c) {
                    fcDictRemove(cm->sh->rawTreeLambdas, lam);
                    fcRethrow(c);
                } FC_END_TRY;
                fcDictRemove(cm->sh->rawTreeLambdas, lam);
                break;
            }
            CompileLambda(cm, lam);
            break;
        }
        case EK_ProbeExpr: {
            FcProbeExpr* probe = (FcProbeExpr*)expr;
            CompileExpr(cm, probe->target);
            CompileExpr(cm, probe->handler);
            E_op(cm, OP_Probe);
            E_u16(cm, probe->member ? E_str(cm, probe->member) : 0);
            E_u8(cm, (probe->isChanging ? 1 : 0) | (probe->member == NULL ? 2 : 0));
            break;
        }
        case EK_SyncGlobalsExpr: E_op(cm, OP_SyncGlobals); break;
        case EK_SyncExpr: {
            FcSyncExpr* se = (FcSyncExpr*)expr;
            CompileExpr(cm, se->target);
            E_op(cm, OP_Sync);
            E_u8(cm, (se->isTry ? 1 : 0) | (se->isFlat ? 2 : 0));
            break;
        }
        case EK_TryProcessExpr: CompileExpr(cm, ((FcTryProcessExpr*)expr)->target); E_op(cm, OP_TryProcess); break;
        case EK_TryCallExpr: {
            FcTryCallExpr* tce = (FcTryCallExpr*)expr;
            FcCallExpr* innerCall = (FcCallExpr*)tce->call;
            FcResolvedRef* takeRef = (FcResolvedRef*)fcDictGet(cm->sh->refs, expr);
            if (takeRef && takeRef->kind == RR_TryTake) {
                FcMemberExpr* takeMember = (FcMemberExpr*)innerCall->callee;
                FcCallExpr* call = FC_NEW(c, FcCallExpr, .h = { EK_CallExpr, innerCall->h.line },
                    .callee = (FcExpr*)fcNewMember(c, takeMember->h.line, takeMember->target, fcFmt(c, "try%s", takeRef->name)), .args = innerCall->args);
                CompileCall(cm, call);
                break;
            }
            for (i = 0; i < innerCall->args->count; i++) CompileExpr(cm, (FcExpr*)innerCall->args->items[i]);
            if (takeRef && takeRef->kind == RR_TryableNative) {
                void* idx;
                if (!fcDictTry(cm->sh->natives->tryable, takeRef->name, &idx)) fcCompNotSupported(cm, "No 'tryable' native function named '%s' is registered.", takeRef->name);
                E_op(cm, OP_CallTryableNative);
                E_u16(cm, FC_VALI(idx) - 1);
                E_u8(cm, innerCall->args->count);
            } else fcCompNotSupported(cm, "TryCallExpr without a resolved TryableNative reference - the resolver should have caught this.");
            break;
        }
        case EK_UntilExpr: {
            FcUntilExpr* until = (FcUntilExpr*)expr;
            for (i = 0; i < until->limits->count; i++) CompileExpr(cm, (FcExpr*)until->limits->items[i]);
            E_op(cm, OP_CancelPush);
            E_u8(cm, until->limits->count);
            CompileExpr(cm, until->inner);
            E_op(cm, OP_CancelPop);
            break;
        }
        case EK_AwaitOnExpr: {
            FcAwaitOnExpr* awaitOn = (FcAwaitOnExpr*)expr;
            for (i = 0; i < awaitOn->args->count; i++) CompileExpr(cm, (FcExpr*)awaitOn->args->items[i]);
            CompileLambda(cm, (FcLambdaExpr*)awaitOn->lambda);
            E_op(cm, OP_PostGlobal);
            E_u8(cm, (awaitOn->args->count | 0x80 | 0x40) & 0xFF);
            E_u8(cm, 0);
            break;
        }
        case EK_CallExpr: {
            FcCallExpr* call = (FcCallExpr*)expr;
            if (call->dedicated) {
                FcCallExpr* copy = (FcCallExpr*)fcNodeDup(c, call, sizeof *call);
                copy->dedicated = false;
                E_op(cm, OP_Dedicate);
                CompileCall(cm, copy);
                E_op(cm, OP_Undedicate);
            } else CompileCall(cm, call);
            break;
        }
        default: fcCompNotSupported(cm, "Expression type %s is not supported.", fcExprKindName(expr->kind));
    }
}

/* TrySelectorPath */
static FcList* TrySelectorPath(FcCtx* c, FcLambdaExpr* lambda) {
    FcStmt* only;
    FcExpr* value;
    FcList* path;
    FcList* reversed;
    int i;
    if (lambda->params->count != 1 || lambda->body->statements->count != 1) return NULL;
    only = (FcStmt*)lambda->body->statements->items[0];
    if (only->kind != SK_ReturnStmt || !((FcReturnStmt*)only)->value) return NULL;
    value = ((FcReturnStmt*)only)->value;
    path = fcListNew(c);
    while (value->kind == EK_MemberExpr) { fcListAdd(c, path, (void*)((FcMemberExpr*)value)->name); value = ((FcMemberExpr*)value)->target; }
    if (path->count == 0 || value->kind != EK_IdentifierExpr || !streq0(((FcIdentifierExpr*)value)->name, ((FcLambdaParam*)lambda->params->items[0])->name)) return NULL;
    reversed = fcListNew(c);
    for (i = path->count - 1; i >= 0; i--) fcListAdd(c, reversed, path->items[i]);
    return reversed;
}

static bool fcIsHandoverCall(FcExpr* e) {
    return e && e->kind == EK_CallExpr && ((FcCallExpr*)e)->callee->kind == EK_IdentifierExpr && streq0(((FcIdentifierExpr*)((FcCallExpr*)e)->callee)->name, FC_HANDOVER_NATIVE);
}

static void CompileLambda(FcComp* cm, FcLambdaExpr* lambda) {
    FcCtx* c = CC_;
    FcComp* inner = fcCompInner(cm, cm->enclosingClass);
    FcResolvedRef* captureRef = (FcResolvedRef*)fcDictGet(cm->sh->refs, lambda);
    FcProto* proto;
    FcList* captures = NULL;
    bool hasOnTarget;
    int i, protoIdx;
    inner->chunk->ownerClass = cm->enclosingClass;
    inner->topLevelSourceIndex = CurrentSourceIndex(cm);
    inner->methodTypeParams = cm->methodTypeParams;
    if ((cm->methodTypeParams && cm->methodTypeParams->count > 0) || (cm->enclosingClass && cm->enclosingClass->decl && cm->enclosingClass->decl->typeParams
        && cm->enclosingClass->decl->typeParams->count > 0) || cm->lambdaTypeRefs != NULL) {
        inner->lambdaTypeRefs = fcListNew(c);
        E_op(inner, OP_TakeCallTypeArgs);
    }
    for (i = 0; i < lambda->params->count; i++) fcChunkMarkLocalName(c, inner->chunk, 0, i, ((FcLambdaParam*)lambda->params->items[i])->name);
    if (captureRef && captureRef->kind == RR_LambdaCaptures)
        for (i = 0; i < captureRef->variables->count; i++) fcChunkMarkLocalName(c, inner->chunk, 0, lambda->params->count + i, ((FcIdentifierExpr*)captureRef->variables->items[i])->name);
    {   /* an untyped parameter typed by the target of the lambda (`int lambda<string> g = s => ...`) gets the entry of `(string s)` */
        FcDict* contextTypes = cm->sh->rr->contextParamTypes;
        FcList* typedParams = lambda->params;
        if (contextTypes && fcDictCount(contextTypes) > 0) {
            for (i = 0; i < lambda->params->count; i++) {
                FcLambdaParam* p = (FcLambdaParam*)lambda->params->items[i];
                FcTypeRef* contextType;
                if (p->type != NULL || (contextType = (FcTypeRef*)fcDictGet(contextTypes, p)) == NULL) continue;
                if (typedParams == lambda->params) { int k; typedParams = fcListNew(c); for (k = 0; k < lambda->params->count; k++) fcListAdd(c, typedParams, lambda->params->items[k]); }
                {
                    FcLambdaParam* typed = (FcLambdaParam*)fcAlloc(c, sizeof *typed);
                    *typed = *p;
                    typed->type = contextType;
                    /* an array (`lambda<int[]>`) is the parameter `(int a[])`: the ranks after the name */
                    if (contextType->arrayRank > 0) {
                        int k;
                        FcTypeRef* element = fcTrCopy(c, contextType);
                        element->arrayRank = 0;
                        typed->type = element;
                        typed->arrayRanks = fcListNew(c);
                        for (k = 0; k < contextType->arrayRank; k++) fcListAdd(c, typed->arrayRanks, NULL);
                    }
                    typedParams->items[i] = typed;
                }
            }
        }
        EmitLambdaParamChecks(cm, inner, typedParams);
    }
    for (i = 0; i < lambda->body->statements->count; i++) CompileStmt(inner, (FcStmt*)lambda->body->statements->items[i]);
    EmitLoadConst(inner, fcMakeUndefined(c));
    E_op(inner, OP_Return);
    proto = fcProtoNew(c, inner->chunk, lambda->params->count, ACC_Public, CompileParamDefaults(cm, cm->enclosingClass, lambda->params), false);
    proto->paramNames = fcListNew(c);
    for (i = 0; i < lambda->params->count; i++) fcListAdd(c, proto->paramNames, (void*)((FcLambdaParam*)lambda->params->items[i])->name);
    if (Reflection(cm)) proto->selectorPath = TrySelectorPath(c, lambda);
    if (captureRef && captureRef->kind == RR_LambdaCaptures && captureRef->loads) {
        FcList* names = fcListNew(c);
        FcDict* readOnly;
        for (i = 0; i < captureRef->variables->count; i++) fcListAdd(c, names, (void*)((FcIdentifierExpr*)captureRef->variables->items[i])->name);
        readOnly = fcAstNamesReadOnly(c, lambda, names);
        for (i = 0; i < captureRef->loads->count && i < 64; i++) {
            FcExpr* load = (FcExpr*)captureRef->loads->items[i];
            if (fcIsHandoverCall(load)) proto->captureMovedMask |= (uint64_t)1 << i;
            else if (load && load->kind == EK_UnaryExpr && ((FcUnaryExpr*)load)->op == UOP_DeepCopy && !fcDictHas(readOnly, ((FcIdentifierExpr*)captureRef->variables->items[i])->name))
                proto->captureCopyMask |= (uint64_t)1 << i;
        }
    }
    protoIdx = fcChunkAddProto(c, CH_, proto);
    if (captureRef && captureRef->kind == RR_LambdaCaptures) captures = captureRef->variables;
    if (captures) {
        if (captures->count > 255) fcCompNotSupported(cm, "A lambda can capture at most 255 outer variables.");
        for (i = 0; i < captures->count; i++) {
            FcExpr* load = captureRef->loads ? (FcExpr*)captureRef->loads->items[i] : NULL;
            CompileExpr(cm, load ? load : (FcExpr*)captures->items[i]);
        }
    }
    hasOnTarget = lambda->onTarget != NULL;
    if (hasOnTarget) CompileExpr(cm, lambda->onTarget);
    if (inner->lambdaTypeRefs && inner->lambdaTypeRefs->count > 0) {
        E_op(cm, OP_SetTypeArgs);
        E_u16(cm, E_str(cm, MapTypeRefs(cm, inner->lambdaTypeRefs)));
        E_u8(cm, 2);
    }
    if (captures) {
        E_op(cm, OP_MakeLambdaCapturing);
        E_u16(cm, protoIdx);
        E_u8(cm, hasOnTarget ? 1 : 0);
        E_u8(cm, captures->count);
        if (captureRef->loads)
            for (i = 0; i < captures->count; i++)
                if (fcIsHandoverCall((FcExpr*)captureRef->loads->items[i])) {
                    FcAssignExpr* clear = FC_NEW(c, FcAssignExpr, .h = { EK_AssignExpr, lambda->h.line }, .target = (FcExpr*)captures->items[i],
                        .value = fcUndefinedLiteral(c, lambda->h.line));
                    CompileExpr(cm, (FcExpr*)clear);
                    E_op(cm, OP_Pop);
                }
    } else {
        E_op(cm, OP_MakeLambda);
        E_u16(cm, protoIdx);
        E_u8(cm, hasOnTarget ? 1 : 0);
    }
}

static bool IsAddressable(FcComp* cm, FcExpr* arg);
static void CompileRefArgument(FcComp* cm, FcExpr* arg, int index);
static void EmitTakeToScope(FcComp* cm, int depth, bool tryTake);
static bool IsSetterClaim(FcComp* cm, void* node) { return cm->setterClaims && fcDictHas(cm->setterClaims, node); }

static uint64_t CompileArgs(FcComp* cm, FcList* args, bool scopeCreating, FcBoolMask* refs) {
    uint64_t mask = 0;
    int i;
    for (i = 0; i < args->count; i++) {
        FcExpr* arg = (FcExpr*)args->items[i];
        if (refs && i < refs->count && refs->items[i] && IsAddressable(cm, arg)) {
            if (i >= 16) fcCompNotSupported(cm, "A 'ref' argument is only possible for the first 16 arguments of a call.");
            CompileRefArgument(cm, arg, i);
            mask |= (uint64_t)3 << (4 * i);
        } else if (arg->kind == EK_UnaryExpr && ((FcUnaryExpr*)arg)->op == UOP_TryTake) {
            CompileExpr(cm, ((FcUnaryExpr*)arg)->operand);
            if (scopeCreating && i < 16) mask |= (uint64_t)8 << (4 * i);
            else { E_op(cm, OP_TryTakeToScope); E_u16(cm, 0); }
        } else if (arg->kind == EK_UnaryExpr && ((FcUnaryExpr*)arg)->op == UOP_Take) {
            CompileExpr(cm, ((FcUnaryExpr*)arg)->operand);
            if (scopeCreating && i < 16) { E_op(cm, OP_TakeCheck); mask |= (uint64_t)5 << (4 * i); }
            else EmitTakeToScope(cm, 0, false);
        } else if (scopeCreating && arg->kind == EK_UnaryExpr && (((FcUnaryExpr*)arg)->op == UOP_FlatCopy || ((FcUnaryExpr*)arg)->op == UOP_DeepCopy)) {
            if (i >= 16) fcCompNotSupported(cm, "`flat`/`copy` as an argument is only possible for the first 16 arguments of a call.");
            mask |= (uint64_t)(((FcUnaryExpr*)arg)->op == UOP_DeepCopy ? 2 : 1) << (4 * i);
            CompileExpr(cm, ((FcUnaryExpr*)arg)->operand);
        } else {
            CompileExpr(cm, arg);
            if (scopeCreating && i < 16) {
                if (IsSetterClaim(cm, arg)) mask |= (uint64_t)6 << (4 * i);
                else if (arg->kind == EK_NewExpr) mask |= (uint64_t)7 << (4 * i);
                else if (MayBeUnclaimed(arg)) mask |= (uint64_t)4 << (4 * i);
            }
        }
    }
    return mask;
}

static bool IsAddressable(FcComp* cm, FcExpr* arg) {
    FcResolvedRef* r = (FcResolvedRef*)fcDictGet(cm->sh->refs, arg);
    if (arg->kind == EK_IdentifierExpr) return r && (r->kind == RR_Local || r->kind == RR_Global || r->kind == RR_ImplicitThisMember);
    if (arg->kind == EK_MemberExpr) return !(r && (r->kind == RR_StaticMember || r->kind == RR_EnumMember));
    return arg->kind == EK_IndexExpr;
}

static void EmitAddressOfLocal(FcComp* cm, const FcResolvedRef* local);
static void CompileRefArgument(FcComp* cm, FcExpr* arg, int index) {
    FcResolvedRef* r = (FcResolvedRef*)fcDictGet(cm->sh->refs, arg);
    switch (arg->kind) {
        case EK_IdentifierExpr:
            if (r && r->kind == RR_Local) { EmitAddressOfLocal(cm, r); return; }
            if (r && r->kind == RR_Global) { E_op(cm, OP_AddressOfGlobal); E_u16(cm, r->slot); return; }
            if (r && r->kind == RR_ImplicitThisMember) { E_op(cm, OP_LoadThis); E_op(cm, OP_AddressOfField); E_u16(cm, E_str(cm, ((FcIdentifierExpr*)arg)->name)); return; }
            fcCompNotSupported(cm, "Argument %d is passed to a 'ref' parameter, so it has to be a variable, a field or an array element ('%s' is none of these).", index + 1,
                ((FcIdentifierExpr*)arg)->name);
        case EK_MemberExpr:
            if (!(r && (r->kind == RR_StaticMember || r->kind == RR_EnumMember))) {
                CompileExpr(cm, ((FcMemberExpr*)arg)->target);
                E_op(cm, OP_AddressOfField);
                E_u16(cm, E_str(cm, ((FcMemberExpr*)arg)->name));
                return;
            }
            break;
        case EK_IndexExpr:
            CompileExpr(cm, ((FcIndexExpr*)arg)->target);
            CompileExpr(cm, ((FcIndexExpr*)arg)->index);
            E_op(cm, OP_AddressOfIndex);
            return;
        default: break;
    }
    fcCompNotSupported(cm, "Argument %d is passed to a 'ref' parameter, so it has to be a variable, a field or an array element - an expression has no address.", index + 1);
}

static void EmitTakeToScope(FcComp* cm, int depth, bool tryTake) {
    E_op(cm, tryTake ? OP_TryTakeToScope : OP_TakeToScope);
    E_u16(cm, depth);
}

static bool MayBeUnclaimed(FcExpr* e) {
    switch (e->kind) {
        case EK_LiteralExpr: case EK_IdentifierExpr: case EK_ThisExpr: case EK_BaseExpr: case EK_SelfClassExpr: case EK_AssignExpr: case EK_IncDecExpr:
        case EK_InterpolatedStringExpr: case EK_IsOfExpr: case EK_IsInExpr: case EK_IsFromExpr: case EK_TryProcessExpr:
            return false;
        case EK_UnaryExpr: {
            FcUnaryExpr* u = (FcUnaryExpr*)e;
            if (u->op == UOP_Take || u->op == UOP_TryTake) return false;
            if (u->op == UOP_Trust) return MayBeUnclaimed(u->operand);
            return true;
        }
        case EK_ConditionalExpr: return MayBeUnclaimed(((FcConditionalExpr*)e)->then) || MayBeUnclaimed(((FcConditionalExpr*)e)->otherwise);
        default: return true;
    }
}

static bool MayLeaveTemps(FcExpr* e) {
    if (!e) return false;
    switch (e->kind) {
        case EK_LiteralExpr: case EK_IdentifierExpr: case EK_ThisExpr: case EK_BaseExpr: case EK_SelfClassExpr: return false;
        case EK_AssignExpr: {
            FcAssignExpr* a = (FcAssignExpr*)e;
            return (a->target->kind != EK_IdentifierExpr && MayLeaveTemps(a->target)) || MayLeaveTemps(a->value);
        }
        case EK_IncDecExpr: return ((FcIncDecExpr*)e)->target->kind != EK_IdentifierExpr;
        case EK_UnaryExpr: {
            FcUnaryExpr* u = (FcUnaryExpr*)e;
            if (u->op == UOP_Take || u->op == UOP_TryTake || u->op == UOP_Trust) return MayLeaveTemps(u->operand);
            return true;
        }
        default: return true;
    }
}

static void EmitReleaseTemps(FcComp* cm, FcExpr* e) { if (MayLeaveTemps(e)) E_op(cm, OP_ReleaseTemps); }

static void EmitClaimToScope(FcComp* cm, int depth, bool unclaimed, bool hoist) {
    int flags = (unclaimed ? 1 : 0) | (hoist ? 2 : 0);
    if (flags == 0) return;
    E_op(cm, OP_ClaimToScope);
    E_u16(cm, depth);
    E_u8(cm, flags);
}

static void EmitSetFieldClaim(FcComp* cm, const char* name, bool setterValue) {
    E_op(cm, OP_SetFieldClaim);
    E_u16(cm, E_str(cm, name));
    E_u8(cm, setterValue ? 2 : 0);
}

static void EmitCopyArgsPrefix(FcComp* cm, uint64_t mask) {
    int part;
    if (mask == 0) return;
    E_op(cm, OP_CopyArgs);
    for (part = 0; part < 4; part++) E_u16(cm, (int)((mask >> (16 * part)) & 0xFFFF));
}

static void EmitArgNames(FcComp* cm, FcList* names) {
    FcBuf b = {0};
    int i, n = 0;
    if (names == NULL) return;
    fcBufStr(&b, "");
    for (i = 0; i < names->count; i++) if (names->items[i]) { if (n++) fcBufChar(&b, '|'); fcBufStr(&b, (const char*)names->items[i]); }
    if (n == 0) { fcBufFree(&b); return; }
    E_op(cm, OP_ArgNames);
    E_u16(cm, E_str(cm, fcBufDone(CC_, &b)));
}

static void RejectArgNames(FcComp* cm, FcCallExpr* call, const char* what) {
    if (call->argNames != NULL) fcCompError(cm, call->h.line, fcFmt(CC_, "Named arguments are not possible for %s.", what));
}

static void CompileCall(FcComp* cm, FcCallExpr* call) {
    FcCtx* c = CC_;
    int i;
    if (call->callee->kind == EK_IdentifierExpr) {
        FcIdentifierExpr* calleeId = (FcIdentifierExpr*)call->callee;
        FcResolvedRef* resolved = (FcResolvedRef*)fcDictGet(cm->sh->refs, calleeId);
        if (resolved) {
            if (resolved->kind == RR_Native) {
                void* idx;
                RejectArgNames(cm, call, "a native function");
                for (i = 0; i < call->args->count; i++) CompileExpr(cm, (FcExpr*)call->args->items[i]);
                if (!fcDictTry(cm->sh->natives->index, resolved->name, &idx)) fcCompNotSupported(cm, "No native function named '%s' is registered.", resolved->name);
                E_op(cm, OP_CallNative);
                E_u16(cm, FC_VALI(idx) - 1);
                E_u8(cm, call->args->count);
                return;
            }
            if (resolved->kind == RR_Extern) {
                RejectArgNames(cm, call, "an extern function");
                for (i = 0; i < call->args->count; i++) CompileExpr(cm, (FcExpr*)call->args->items[i]);
                E_op(cm, OP_CallExtern);
                E_u16(cm, E_str(cm, resolved->name));
                E_u8(cm, call->args->count);
                return;
            }
            if (resolved->kind == RR_StaticMember) {
                uint64_t callCopyMask = CompileArgs(cm, call->args, true, fcRpForMethod(cm, calleeId->name, call->args->count));
                EmitCallTypeArgs(cm, call);
                EmitCopyArgsPrefix(cm, callCopyMask);
                EmitArgNames(cm, call->argNames);
                E_op(cm, OP_CallStaticMethod);
                E_u16(cm, E_str(cm, resolved->className));
                E_u16(cm, E_str(cm, calleeId->name));
                E_u8(cm, call->args->count);
                return;
            }
            if (resolved->kind == RR_ImplicitThisMember && fcRpIsLambdaField(cm, calleeId->name)) {
                CompileExpr(cm, (FcExpr*)calleeId);
                EmitCopyArgsPrefix(cm, CompileArgs(cm, call->args, true, NULL));
                RejectArgNames(cm, call, "a lambda");
                E_op(cm, OP_Call);
                E_u8(cm, call->args->count);
                return;
            }
            if (resolved->kind == RR_ImplicitThisMember) {
                uint64_t callCopyMask;
                E_op(cm, OP_LoadThis);
                callCopyMask = CompileArgs(cm, call->args, true, fcRpForMethod(cm, calleeId->name, call->args->count));
                EmitCallTypeArgs(cm, call);
                EmitCopyArgsPrefix(cm, callCopyMask);
                EmitArgNames(cm, call->argNames);
                E_op(cm, OP_CallMethod);
                E_u16(cm, E_str(cm, calleeId->name));
                E_u8(cm, call->args->count);
                return;
            }
        }
    }
    if (call->callee->kind == EK_MemberExpr) {
        FcMemberExpr* me = (FcMemberExpr*)call->callee;
        FcResolvedRef* calleeMemberRef;
        uint64_t callCopyMask;
        if (streq0(me->name, "resume")) {
            RejectArgNames(cm, call, "'resume'");
            if (call->args->count > 1) fcCompNotSupported(cm, "'resume' expects at most one argument (the resume value).");
            CompileExpr(cm, me->target);
            if (call->args->count == 1) CompileExpr(cm, (FcExpr*)call->args->items[0]);
            else EmitLoadConst(cm, fcMakeUndefined(c));
            E_op(cm, OP_ResumeException);
            return;
        }
        calleeMemberRef = (FcResolvedRef*)fcDictGet(cm->sh->refs, me);
        if (calleeMemberRef && calleeMemberRef->kind == RR_StaticMember) {
            uint64_t memberCopyMask = CompileArgs(cm, call->args, true, fcRpForMethod(cm, me->name, call->args->count));
            EmitCallTypeArgs(cm, call);
            EmitCopyArgsPrefix(cm, memberCopyMask);
            EmitArgNames(cm, call->argNames);
            E_op(cm, OP_CallStaticMethod);
            E_u16(cm, E_str(cm, calleeMemberRef->className));
            E_u16(cm, E_str(cm, me->name));
            E_u8(cm, call->args->count);
            return;
        }
        if (me->target->kind == EK_BaseExpr) {
            uint64_t staticCopyMask;
            if (cm->enclosingClass == NULL || cm->enclosingClass->base == NULL)
                fcCompNotSupported(cm, "'base.Method(...)' outside of a class with a base class - the resolver should have caught this.");
            staticCopyMask = CompileArgs(cm, call->args, true, fcRpForMethod(cm, me->name, call->args->count));
            EmitCallTypeArgs(cm, call);
            EmitCopyArgsPrefix(cm, staticCopyMask);
            EmitArgNames(cm, call->argNames);
            E_op(cm, OP_CallBaseMethod);
            E_u16(cm, E_str(cm, cm->enclosingClass->base->name));
            E_u16(cm, E_str(cm, me->name));
            E_u8(cm, call->args->count);
            return;
        }
        if (fcRpIsLambdaField(cm, me->name)) {
            CompileExpr(cm, (FcExpr*)me);
            EmitCopyArgsPrefix(cm, CompileArgs(cm, call->args, true, NULL));
            RejectArgNames(cm, call, "a lambda");
            E_op(cm, OP_Call);
            E_u8(cm, call->args->count);
            return;
        }
        CompileExpr(cm, me->target);
        callCopyMask = CompileArgs(cm, call->args, true, fcRpForMethod(cm, me->name, call->args->count));
        EmitCallTypeArgs(cm, call);
        if (call->awaited) E_op(cm, OP_AwaitReply);
        EmitCopyArgsPrefix(cm, callCopyMask);
        EmitArgNames(cm, call->argNames);
        NoteReceiverTypeArgs(cm, (FcExpr*)call);
        E_op(cm, OP_CallMethod);
        E_u16(cm, E_str(cm, me->name));
        E_u8(cm, call->args->count);
        return;
    }
    CompileExpr(cm, call->callee);
    EmitCopyArgsPrefix(cm, CompileArgs(cm, call->args, true, NULL));
    RejectArgNames(cm, call, "a lambda");
    E_op(cm, OP_Call);
    E_u8(cm, call->args->count);
}

/* ---- the anchor rule: CoercionInfo / AnalyzeCoercion ---- */
typedef struct FcCpCoercion { FcExpr* inner; bool wantsUnit; const char* explicitUnit; bool wantsType; int explicitType; bool relabelAuto; } FcCpCoercion;   /* explicitType: -1 none */
static bool CI_UnitIsAuto(const FcCpCoercion* i) { return i->wantsUnit && i->explicitUnit == NULL; }
static bool CI_TypeIsAuto(const FcCpCoercion* i) { return i->wantsType && i->explicitType < 0; }
static bool CI_RequestsAnyAuto(const FcCpCoercion* i) { return CI_UnitIsAuto(i) || CI_TypeIsAuto(i); }

static FcCpCoercion CompilerAnalyzeCoercion(FcExpr* expr) {
    FcCpCoercion r;
    FcExpr* current = expr;
    memset(&r, 0, sizeof r);
    r.explicitType = -1;
    for (;;) {
        if (current->kind == EK_UnitCoerceExpr && (!((FcUnitCoerceExpr*)current)->relabel || ((FcUnitCoerceExpr*)current)->targetUnitName == NULL)) {
            FcUnitCoerceExpr* uc = (FcUnitCoerceExpr*)current;
            r.wantsUnit = true;
            r.explicitUnit = uc->targetUnitName;
            if (uc->relabel) r.relabelAuto = true;
            current = uc->operand;
        } else if (current->kind == EK_TypeCoerceExpr && ((FcTypeCoerceExpr*)current)->targetClass == NULL && ((FcTypeCoerceExpr*)current)->width == 0
            && ((FcTypeCoerceExpr*)current)->targetTypeKeyword != TT_KwWord) {
            FcTypeCoerceExpr* tc = (FcTypeCoerceExpr*)current;
            r.wantsType = true;
            r.explicitType = tc->targetTypeKeyword;
            current = tc->operand;
        } else break;
    }
    r.inner = current;
    return r;
}

static int TokenTypeToTag(FcComp* cm, int t) {
    if (t == TT_KwBool) return TAG_Bool;
    if (t == TT_KwInt) return TAG_Int;
    if (t == TT_KwFloat) return TAG_Float;
    if (t == TT_KwChar) return TAG_Char;
    if (t == TT_KwString) return TAG_String;
    fcCompNotSupported(cm, "Coercion target %s is not supported.", fcTokenTypeName((FcTokenType)t));
}

static void EmitCoerceUnitStatic(FcComp* cm, const FcU* unit) { E_op(cm, OP_CoerceUnit); E_u16(cm, fcChunkAddUnit(CC_, CH_, unit)); }
static void EmitCoerceTypeStatic(FcComp* cm, int tag) { E_op(cm, OP_CoerceType); E_u8(cm, tag); }
static void EmitUnitStatic(FcComp* cm, const FcU* unit, bool relabel) {
    if (!relabel) { EmitCoerceUnitStatic(cm, unit); return; }
    E_op(cm, OP_RelabelUnit);
    E_u16(cm, fcChunkAddUnit(CC_, CH_, unit));
}

static bool EmitFixedCoercion(FcComp* cm, FcExpr* outer, const FcCpCoercion* info) {
    FcFixedCoercion* plan;
    if (cm->sh->rr->fixedCoercions == NULL || !(plan = (FcFixedCoercion*)fcDictGet(cm->sh->rr->fixedCoercions, outer))) return false;
    if (CI_UnitIsAuto(info) && plan->unitv == NULL) return false;
    if (CI_TypeIsAuto(info)) {
        if (plan->toWord) { E_op(cm, OP_ConvertWidth); E_u8(cm, 0); E_u8(cm, 8); }
        else if (plan->type >= 0) EmitCoerceTypeStatic(cm, plan->type);
    }
    if (CI_UnitIsAuto(info)) EmitUnitStatic(cm, plan->unitv, info->relabelAuto);
    return true;
}

static void CompileStandaloneCoercion(FcComp* cm, FcExpr* expr) {
    FcCpCoercion info = CompilerAnalyzeCoercion(expr);
    CompileExpr(cm, info.inner);
    if (info.explicitType >= 0) EmitCoerceTypeStatic(cm, TokenTypeToTag(cm, info.explicitType));
    if (info.explicitUnit != NULL) EmitCoerceUnitStatic(cm, fcUParse(CC_, info.explicitUnit));
    if (CI_RequestsAnyAuto(&info) && EmitFixedCoercion(cm, expr, &info)) return;
    if (info.explicitUnit == NULL && CI_UnitIsAuto(&info)) EmitUnitStatic(cm, FC_UNITLESS, info.relabelAuto);
}

static void EmitLoadVariable(FcComp* cm, const FcResolvedRef* local) {
    E_op(cm, OP_LoadLocal);
    E_u16(cm, local->depth);
    E_u16(cm, local->slot);
    if (local->byRef) E_op(cm, OP_PtrRead);
}
static void EmitStoreVariable(FcComp* cm, const FcResolvedRef* local) {
    if (!local->byRef) {
        E_op(cm, OP_StoreLocal);
        E_u16(cm, local->depth);
        E_u16(cm, local->slot);
        return;
    }
    E_op(cm, OP_LoadLocal);
    E_u16(cm, local->depth);
    E_u16(cm, local->slot);
    E_op(cm, OP_Swap);
    E_op(cm, OP_PtrWrite);
}
static void EmitAddressOfLocal(FcComp* cm, const FcResolvedRef* local) {
    E_op(cm, local->byRef ? OP_LoadLocal : OP_AddressOfLocal);
    E_u16(cm, local->depth);
    E_u16(cm, local->slot);
}

static void CompileIdentifierLoad(FcComp* cm, FcIdentifierExpr* id) {
    FcResolvedRef* r = (FcResolvedRef*)fcDictGet(cm->sh->refs, id);
    if (!r) fcThrow(CC_, FC_ERR_INTERNAL, id->h.line, 0, NULL, fcFmt(CC_, "The given key '%s' was not present in the dictionary.", id->name));
    switch (r->kind) {
        case RR_Local: EmitLoadVariable(cm, r); break;
        case RR_Global: E_op(cm, OP_LoadGlobal); E_u16(cm, r->slot); break;
        case RR_Native:
            fcCompNotSupported(cm, "'%s' is a native function and can only be called directly (%s(...)), not used as a value.", r->name, r->name);
        case RR_Extern:
            fcCompNotSupported(cm, "'%s' is a function declared extern and can only be called directly, not used as a value.", r->name);
        case RR_StaticMember:
            E_op(cm, OP_GetStaticField);
            E_u16(cm, E_str(cm, r->className));
            E_u16(cm, E_str(cm, id->name));
            break;
        case RR_ImplicitThisMember:
            E_op(cm, OP_LoadThis);
            E_op(cm, OP_GetField);
            E_u16(cm, E_str(cm, id->name));
            break;
        default: break;
    }
}

static void CompileWith(FcComp* cm, FcWithExpr* with) {
    FcCtx* c = CC_;
    int i;
    FcUnaryExpr* copy = FC_NEW(c, FcUnaryExpr, .h = { EK_UnaryExpr, with->h.line }, .op = with->copy == UOP__NONE ? UOP_DeepCopy : with->copy, .operand = with->source);
    CompileExpr(cm, (FcExpr*)copy);
    if (with->copy == UOP__NONE) { E_op(cm, OP_RecordCheck); E_u8(cm, 1); }
    for (i = 0; i < with->sets->count; i++) {
        FcWithSet* set = (FcWithSet*)with->sets->items[i];
        bool claim = false;
        E_op(cm, OP_Dup);
        if (set->value->kind == EK_UnaryExpr && (((FcUnaryExpr*)set->value)->op == UOP_Take || ((FcUnaryExpr*)set->value)->op == UOP_TryTake)) {
            FcUnaryExpr* takeValue = (FcUnaryExpr*)set->value;
            CompileExpr(cm, takeValue->operand);
            E_op(cm, takeValue->op == UOP_TryTake ? OP_TryTakeToObject : OP_TakeToObject);
        } else {
            CompileExpr(cm, set->value);
            claim = MayBeUnclaimed(set->value);
        }
        EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, set->name), fcRpFieldUnit(cm, set->name));
        E_op(cm, OP_InitNext);
        if (claim) EmitSetFieldClaim(cm, set->name, false);
        else { E_op(cm, OP_SetField); E_u16(cm, E_str(cm, set->name)); }
        E_op(cm, OP_Pop);
    }
    E_op(cm, OP_RecordCheck);
    E_u8(cm, 0);
}

static void CompileMemberStore(FcComp* cm, FcAssignExpr* a, const char* name);
static void CompileTakeAssign(FcComp* cm, FcIdentifierExpr* id, FcUnaryExpr* take);
static void EmitSetFieldAccessor(FcComp* cm, const char* name);

static void CompileAssign(FcComp* cm, FcAssignExpr* a) {
    FcResolvedRef* r;
    FcIdentifierExpr* id;
    if (a->target->kind == EK_MemberExpr) {
        FcMemberExpr* me = (FcMemberExpr*)a->target;
        FcResolvedRef* staticTargetRef = (FcResolvedRef*)fcDictGet(cm->sh->refs, me);
        if (staticTargetRef && staticTargetRef->kind == RR_StaticMember) {
            /* (`C.f = take x`: x goes to the program, as the field - not to the scope of the running call, Compiler.CompileAssign) */
            if (a->value->kind == EK_UnaryExpr && (((FcUnaryExpr*)a->value)->op == UOP_Take || ((FcUnaryExpr*)a->value)->op == UOP_TryTake)) {
                CompileExpr(cm, ((FcUnaryExpr*)a->value)->operand);
                EmitTakeToScope(cm, 0xFFFF, ((FcUnaryExpr*)a->value)->op == UOP_TryTake);
            } else {
                CompileExpr(cm, a->value);
                EmitClaimToScope(cm, 0xFFFF, MayBeUnclaimed(a->value), false);
            }
            EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, me->name), fcRpFieldUnit(cm, me->name));
            E_op(cm, OP_SetStaticField);
            E_u16(cm, E_str(cm, staticTargetRef->className));
            E_u16(cm, E_str(cm, me->name));
            return;
        }
        CompileExpr(cm, me->target);
        CompileMemberStore(cm, a, me->name);
        return;
    }
    if (a->target->kind == EK_UnaryExpr && ((FcUnaryExpr*)a->target)->op == UOP_Dereference) {
        CompileExpr(cm, ((FcUnaryExpr*)a->target)->operand);
        CompileExpr(cm, a->value);
        E_op(cm, OP_PtrWrite);
        return;
    }
    if (a->target->kind == EK_IndexExpr) {
        FcIndexExpr* ix = (FcIndexExpr*)a->target;
        CompileExpr(cm, ix->target);
        CompileExpr(cm, ix->index);
        if (a->value->kind == EK_UnaryExpr && (((FcUnaryExpr*)a->value)->op == UOP_Take || ((FcUnaryExpr*)a->value)->op == UOP_TryTake)) {
            FcUnaryExpr* takeElement = (FcUnaryExpr*)a->value;
            CompileExpr(cm, takeElement->operand);
            E_op(cm, takeElement->op == UOP_TryTake ? OP_TryTakeToArray : OP_TakeToArray);
        } else {
            CompileExpr(cm, a->value);
            if (MayBeUnclaimed(a->value)) E_op(cm, OP_ClaimToArray);
        }
        NoteReceiverTypeArgs(cm, (FcExpr*)ix);
        E_op(cm, OP_ArraySet);
        return;
    }
    if (a->target->kind != EK_IdentifierExpr) fcCompNotSupported(cm, "Invalid assignment target for the bytecode compiler.");
    id = (FcIdentifierExpr*)a->target;
    if (a->value->kind == EK_UnaryExpr && (((FcUnaryExpr*)a->value)->op == UOP_Take || ((FcUnaryExpr*)a->value)->op == UOP_TryTake)) {
        CompileTakeAssign(cm, id, (FcUnaryExpr*)a->value);
        return;
    }
    r = (FcResolvedRef*)fcDictGet(cm->sh->refs, id);
    if (!r) fcThrow(CC_, FC_ERR_INTERNAL, id->h.line, 0, NULL, fcFmt(CC_, "The given key '%s' was not present in the dictionary.", id->name));
    if (r->kind == RR_ImplicitThisMember && (MayBeUnclaimed(a->value) || IsSetterClaim(cm, a))) {
        E_op(cm, OP_LoadThis);
        CompileExpr(cm, a->value);
        EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, id->name), fcRpFieldUnit(cm, id->name));
        EmitSetFieldClaim(cm, id->name, IsSetterClaim(cm, a));
        return;
    }
    CompileExpr(cm, a->value);
    switch (r->kind) {
        case RR_Local:
            EmitCheckUnitIfNeeded(cm, r->requiredUnit);
            EmitVariableTyping(cm, r->width, r->requiredUnit, r->arrayElem, r->arrayRank);
            if (!r->byRef) EmitClaimToScope(cm, r->depth, MayBeUnclaimed(a->value), r->depth > 0);
            EmitStoreVariable(cm, r);
            break;
        case RR_Global:
            EmitCheckUnitIfNeeded(cm, r->requiredUnit);
            EmitVariableTyping(cm, r->width, r->requiredUnit, r->arrayElem, r->arrayRank);
            EmitClaimToScope(cm, 0xFFFF, MayBeUnclaimed(a->value), cm->currentScopeDepth > 0 && cm->isTopLevelCode);
            E_op(cm, OP_StoreGlobal);
            E_u16(cm, r->slot);
            break;
        case RR_Native: fcCompNotSupported(cm, "Cannot assign to '%s' - it is a native function.", r->name);
        case RR_Extern: fcCompNotSupported(cm, "Cannot assign to '%s' - it is a function declared extern.", r->name);
        case RR_StaticMember:
            EmitClaimToScope(cm, 0xFFFF, MayBeUnclaimed(a->value), false);   /* (a static field belongs to the program, as `C.f = ...`) */
            EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, id->name), fcRpFieldUnit(cm, id->name));
            E_op(cm, OP_SetStaticField);
            E_u16(cm, E_str(cm, r->className));
            E_u16(cm, E_str(cm, id->name));
            break;
        case RR_ImplicitThisMember:
            E_op(cm, OP_LoadThis);
            E_op(cm, OP_Swap);
            EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, id->name), fcRpFieldUnit(cm, id->name));
            if (cm->accessor != 0) { EmitSetFieldAccessor(cm, id->name); break; }
            E_op(cm, OP_SetField);
            E_u16(cm, E_str(cm, id->name));
            break;
        default: break;
    }
}

static void CompileMemberStore(FcComp* cm, FcAssignExpr* a, const char* name) {
    if (a->value->kind == EK_UnaryExpr && (((FcUnaryExpr*)a->value)->op == UOP_Take || ((FcUnaryExpr*)a->value)->op == UOP_TryTake)) {
        FcUnaryExpr* takeValue = (FcUnaryExpr*)a->value;
        CompileExpr(cm, takeValue->operand);
        E_op(cm, takeValue->op == UOP_TryTake ? OP_TryTakeToObject : OP_TakeToObject);
    } else {
        bool setterValue;
        CompileExpr(cm, a->value);
        setterValue = IsSetterClaim(cm, a);
        if (setterValue || MayBeUnclaimed(a->value)) {
            EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, name), fcRpFieldUnit(cm, name));
            EmitSetFieldClaim(cm, name, setterValue);
            return;
        }
        if (cm->accessor != 0) {
            EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, name), NULL);
            EmitSetFieldAccessor(cm, name);
            return;
        }
    }
    EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, name), fcRpFieldUnit(cm, name));
    E_op(cm, OP_SetField);
    E_u16(cm, E_str(cm, name));
}

static void fcReplaceGetStatic(FcComp* cm, const char* staticClass, const char* name) {
    E_op(cm, OP_GetStaticField);
    E_u16(cm, E_str(cm, staticClass));
    E_u16(cm, E_str(cm, name));
}
static void fcReplaceGetField(FcComp* cm, const char* name) { E_op(cm, OP_GetField); E_u16(cm, E_str(cm, name)); }

static void CompileReplace(FcComp* cm, FcAssignExpr* a) {
    const char* staticClass = NULL;
    const char* name;
    FcResolvedRef* r;
    if ((a->target->kind == EK_MemberExpr || a->target->kind == EK_IdentifierExpr) && (r = (FcResolvedRef*)fcDictGet(cm->sh->refs, a->target)) != NULL && r->kind == RR_StaticMember)
        staticClass = r->className;
    if (a->target->kind == EK_MemberExpr) name = ((FcMemberExpr*)a->target)->name;
    else if (a->target->kind == EK_IdentifierExpr) name = ((FcIdentifierExpr*)a->target)->name;
    else fcCompNotSupported(cm, "'replace' assigns a field.");
    if (staticClass != NULL) {
        fcReplaceGetStatic(cm, staticClass, name);
        CompileAssign(cm, a);
        E_op(cm, OP_Pop);
        fcReplaceGetStatic(cm, staticClass, name);
        E_op(cm, OP_ReplaceOld);
        E_u8(cm, 1);
        return;
    }
    if (a->target->kind == EK_MemberExpr) CompileExpr(cm, ((FcMemberExpr*)a->target)->target);
    else E_op(cm, OP_LoadThis);
    E_op(cm, OP_Dup);
    fcReplaceGetField(cm, name);
    E_op(cm, OP_Swap);
    E_op(cm, OP_Dup);
    CompileMemberStore(cm, a, name);
    E_op(cm, OP_Pop);
    E_op(cm, OP_Dup);
    fcReplaceGetField(cm, name);
    E_op(cm, OP_ReplaceOld);
    E_u8(cm, 0);
}

static void EmitSetFieldAccessor(FcComp* cm, const char* name) {
    E_op(cm, OP_SetFieldAccessor);
    E_u16(cm, E_str(cm, name));
    E_u8(cm, cm->accessor == 1 ? 1 : 0);
}

static void CompileTakeAssign(FcComp* cm, FcIdentifierExpr* id, FcUnaryExpr* take) {
    FcResolvedRef* r;
    CompileExpr(cm, take->operand);
    r = (FcResolvedRef*)fcDictGet(cm->sh->refs, id);
    switch (r ? r->kind : RR_Native) {
        case RR_Local:
            EmitCheckUnitIfNeeded(cm, r->requiredUnit);
            EmitVariableTyping(cm, r->width, r->requiredUnit, r->arrayElem, r->arrayRank);
            EmitTakeToScope(cm, r->byRef ? 0 : r->depth, take->op == UOP_TryTake);
            EmitStoreVariable(cm, r);
            break;
        case RR_Global:
            EmitCheckUnitIfNeeded(cm, r->requiredUnit);
            EmitVariableTyping(cm, r->width, r->requiredUnit, r->arrayElem, r->arrayRank);
            EmitTakeToScope(cm, 0xFFFF, take->op == UOP_TryTake);
            E_op(cm, OP_StoreGlobal);
            E_u16(cm, r->slot);
            break;
        case RR_StaticMember:
            EmitTakeToScope(cm, 0xFFFF, take->op == UOP_TryTake);
            EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, id->name), fcRpFieldUnit(cm, id->name));
            E_op(cm, OP_SetStaticField);
            E_u16(cm, E_str(cm, r->className));
            E_u16(cm, E_str(cm, id->name));
            break;
        case RR_ImplicitThisMember:
            E_op(cm, OP_LoadThis);
            E_op(cm, OP_Swap);
            E_op(cm, take->op == UOP_TryTake ? OP_TryTakeToObject : OP_TakeToObject);
            EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, id->name), fcRpFieldUnit(cm, id->name));
            E_op(cm, OP_SetField);
            E_u16(cm, E_str(cm, id->name));
            break;
        default:
            fcCompNotSupported(cm, "'%s' needs a holder: '%s' is not a variable or a field.", take->op == UOP_TryTake ? "try take" : "take", id->name);
    }
}

static void CompileIncDec(FcComp* cm, FcIncDecExpr* e) {
    FcCtx* c = CC_;
    FcOp addSubOp = e->isIncrement ? OP_StepAdd : OP_StepSub;
    FcIdentifierExpr* id;
    FcResolvedRef* refKind;
    if (e->target->kind == EK_IndexExpr) {
        CompileExpr(cm, ((FcIndexExpr*)e->target)->target);
        CompileExpr(cm, ((FcIndexExpr*)e->target)->index);
        E_op(cm, OP_IncDecIndex);
        E_u8(cm, e->isIncrement ? 1 : 0);
        E_u8(cm, e->isPrefix ? 1 : 0);
        return;
    }
    if (e->target->kind == EK_MemberExpr) {
        FcMemberExpr* me = (FcMemberExpr*)e->target;
        FcResolvedRef* stm = (FcResolvedRef*)fcDictGet(cm->sh->refs, me);
        if (stm && stm->kind == RR_StaticMember) {
            int classNameConstIdx = E_str(cm, stm->className);
            int fieldNameConstIdx = E_str(cm, me->name);
            E_op(cm, OP_GetStaticField); E_u16(cm, classNameConstIdx); E_u16(cm, fieldNameConstIdx);
            if (!e->isPrefix) E_op(cm, OP_Dup);
            EmitLoadConst(cm, fcMakeInt(c, 1));
            E_op(cm, addSubOp);
            EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, me->name), fcRpFieldUnit(cm, me->name));
            E_op(cm, OP_SetStaticField); E_u16(cm, classNameConstIdx); E_u16(cm, fieldNameConstIdx);
            if (!e->isPrefix) E_op(cm, OP_Pop);
            return;
        }
        CompileExpr(cm, me->target);
        E_op(cm, OP_Dup);
        E_op(cm, OP_GetField); E_u16(cm, E_str(cm, me->name));
        if (!e->isPrefix) E_op(cm, OP_Dup);
        EmitLoadConst(cm, fcMakeInt(c, 1));
        E_op(cm, addSubOp);
        if (!e->isPrefix) E_op(cm, OP_RotateUnderTop);
        EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, me->name), fcRpFieldUnit(cm, me->name));
        E_op(cm, OP_SetField); E_u16(cm, E_str(cm, me->name));
        if (!e->isPrefix) E_op(cm, OP_Pop);
        return;
    }
    if (e->target->kind == EK_UnaryExpr && ((FcUnaryExpr*)e->target)->op == UOP_Dereference) {
        CompileExpr(cm, ((FcUnaryExpr*)e->target)->operand);
        E_op(cm, OP_Dup);
        E_op(cm, OP_PtrRead);
        if (!e->isPrefix) E_op(cm, OP_Dup);
        EmitLoadConst(cm, fcMakeInt(c, 1));
        E_op(cm, addSubOp);
        if (!e->isPrefix) E_op(cm, OP_RotateUnderTop);
        E_op(cm, OP_PtrWrite);
        if (!e->isPrefix) E_op(cm, OP_Pop);
        return;
    }
    if (e->target->kind != EK_IdentifierExpr) fcCompNotSupported(cm, "Invalid target for '++'/'--' in the bytecode compiler.");
    id = (FcIdentifierExpr*)e->target;
    refKind = (FcResolvedRef*)fcDictGet(cm->sh->refs, id);
    if (!refKind) fcThrow(c, FC_ERR_INTERNAL, id->h.line, 0, NULL, fcFmt(c, "The given key '%s' was not present in the dictionary.", id->name));
    if (refKind->kind == RR_StaticMember) {
        int classNameConstIdx = E_str(cm, refKind->className);
        int fieldNameConstIdx = E_str(cm, id->name);
        E_op(cm, OP_GetStaticField); E_u16(cm, classNameConstIdx); E_u16(cm, fieldNameConstIdx);
        if (!e->isPrefix) E_op(cm, OP_Dup);
        EmitLoadConst(cm, fcMakeInt(c, 1));
        E_op(cm, addSubOp);
        EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, id->name), fcRpFieldUnit(cm, id->name));
        E_op(cm, OP_SetStaticField); E_u16(cm, classNameConstIdx); E_u16(cm, fieldNameConstIdx);
        if (!e->isPrefix) E_op(cm, OP_Pop);
        return;
    }
    if (refKind->kind == RR_ImplicitThisMember) {
        E_op(cm, OP_LoadThis);
        E_op(cm, OP_Dup);
        E_op(cm, OP_GetField); E_u16(cm, E_str(cm, id->name));
        if (!e->isPrefix) E_op(cm, OP_Dup);
        EmitLoadConst(cm, fcMakeInt(c, 1));
        E_op(cm, addSubOp);
        if (!e->isPrefix) E_op(cm, OP_RotateUnderTop);
        EmitWidthIfNeeded(cm, fcRpFieldWidth(cm, id->name), fcRpFieldUnit(cm, id->name));
        E_op(cm, OP_SetField); E_u16(cm, E_str(cm, id->name));
        if (!e->isPrefix) E_op(cm, OP_Pop);
        return;
    }
    /* EmitLoad */
    if (refKind->kind == RR_Local) EmitLoadVariable(cm, refKind);
    else if (refKind->kind == RR_Global) { E_op(cm, OP_LoadGlobal); E_u16(cm, refKind->slot); }
    else fcCompNotSupported(cm, "'++'/'--' on '%s' is not possible.", id->name);
    if (!e->isPrefix) E_op(cm, OP_Dup);
    EmitLoadConst(cm, fcMakeInt(c, 1));
    E_op(cm, addSubOp);
    /* EmitStore */
    if (refKind->kind == RR_Local) {
        EmitCheckUnitIfNeeded(cm, refKind->requiredUnit);
        EmitVariableTyping(cm, refKind->width, refKind->requiredUnit, refKind->arrayElem, refKind->arrayRank);
        EmitStoreVariable(cm, refKind);
    } else {
        EmitCheckUnitIfNeeded(cm, refKind->requiredUnit);
        EmitVariableTyping(cm, refKind->width, refKind->requiredUnit, refKind->arrayElem, refKind->arrayRank);
        E_op(cm, OP_StoreGlobal);
        E_u16(cm, refKind->slot);
    }
    if (!e->isPrefix) E_op(cm, OP_Pop);
}

static void CompileAddressOf(FcComp* cm, FcExpr* operand);
static void CompileUnary(FcComp* cm, FcUnaryExpr* u) {
    if (u->op == UOP_AddressOf) { CompileAddressOf(cm, u->operand); return; }
    if (u->op == UOP_Trust) { CompileExpr(cm, u->operand); E_op(cm, OP_Trust); return; }
    if (u->op == UOP_Take || u->op == UOP_TryTake) { CompileExpr(cm, u->operand); EmitTakeToScope(cm, 0, u->op == UOP_TryTake); return; }
    if (u->op == UOP_FlatCopy || u->op == UOP_DeepCopy) {
        CompileExpr(cm, u->operand);
        E_op(cm, OP_CopyValue);
        E_u8(cm, u->op == UOP_DeepCopy ? 1 : 0);
        return;
    }
    CompileExpr(cm, u->operand);
    switch (u->op) {
        case UOP_Negate: E_op(cm, OP_Neg); break;
        case UOP_LogicalNot: E_op(cm, OP_LogicalNot); break;
        case UOP_BitNot: E_op(cm, OP_BitNot); break;
        case UOP_Dereference: E_op(cm, OP_PtrRead); break;
        default: fcCompNotSupported(cm, "UnaryOp %d", (int)u->op);
    }
}

static void CompileAddressOf(FcComp* cm, FcExpr* operand) {
    if (operand->kind == EK_IdentifierExpr) {
        FcResolvedRef* r = (FcResolvedRef*)fcDictGet(cm->sh->refs, operand);
        if (r && r->kind == RR_Local) { EmitAddressOfLocal(cm, r); return; }
        if (r && r->kind == RR_Global) { E_op(cm, OP_AddressOfGlobal); E_u16(cm, r->slot); return; }
        fcCompNotSupported(cm, "'&' can only be applied to local/global variables or object fields.");
    }
    if (operand->kind == EK_MemberExpr) {
        CompileExpr(cm, ((FcMemberExpr*)operand)->target);
        E_op(cm, OP_AddressOfField);
        E_u16(cm, E_str(cm, ((FcMemberExpr*)operand)->name));
        return;
    }
    fcCompNotSupported(cm, "'&' can only be applied to variables or object fields (not a valid address target).");
}

static void EmitCoercedOperands(FcComp* cm, FcExpr* leftExpr, FcExpr* rightExpr);
static void CompileLogicalAnd(FcComp* cm, FcBinaryExpr* b) {
    int endJumpAt;
    CompileExpr(cm, b->left);
    E_op(cm, OP_JumpIfFalsePeek);
    endJumpAt = E_here(cm);
    E_u16(cm, 0);
    E_op(cm, OP_Pop);
    CompileExpr(cm, b->right);
    E_patch(cm, endJumpAt, E_here(cm));
}
static void CompileLogicalOr(FcComp* cm, FcBinaryExpr* b) {
    int endJumpAt;
    CompileExpr(cm, b->left);
    E_op(cm, OP_JumpIfTruePeek);
    endJumpAt = E_here(cm);
    E_u16(cm, 0);
    E_op(cm, OP_Pop);
    CompileExpr(cm, b->right);
    E_patch(cm, endJumpAt, E_here(cm));
}
/* `a ?? b`: [a] -> [a, a] -> is it undefined? Then a goes and b is evaluated; otherwise a is the result (b is not evaluated). */
static void CompileCoalesce(FcComp* cm, FcBinaryExpr* b) {
    int doneAt;
    CompileExpr(cm, b->left);
    E_op(cm, OP_Dup);
    E_op(cm, OP_IsOfType);
    E_u16(cm, E_str(cm, "undefined"));
    doneAt = EmitJumpIfFalse(cm);
    E_op(cm, OP_Pop);
    CompileExpr(cm, b->right);
    E_patch(cm, doneAt, E_here(cm));
}
static void CompileBinary(FcComp* cm, FcBinaryExpr* b) {
    FcOp op;
    if (b->op == BOP_And) { CompileLogicalAnd(cm, b); return; }
    if (b->op == BOP_Or) { CompileLogicalOr(cm, b); return; }
    if (b->op == BOP_Coalesce) { CompileCoalesce(cm, b); return; }
    EmitCoercedOperands(cm, b->left, b->right);
    switch (b->op) {
        case BOP_Add: op = OP_Add; break;
        case BOP_Sub: op = OP_Sub; break;
        case BOP_Mul: op = OP_Mul; break;
        case BOP_Div: op = OP_Div; break;
        case BOP_Mod: op = OP_Mod; break;
        case BOP_Power: op = b->floatPower ? OP_PowerFloat : OP_Power; break;
        case BOP_BitAnd: op = OP_BitAnd; break;
        case BOP_BitOr: op = OP_BitOr; break;
        case BOP_BitXor: op = OP_BitXor; break;
        case BOP_ShiftLeft: op = OP_ShiftLeft; break;
        case BOP_ShiftRight: op = OP_ShiftRight; break;
        case BOP_ShiftRightLogical: op = OP_ShiftRightLogical; break;
        case BOP_Eq: op = OP_Eq; break;
        case BOP_NotEq: op = OP_NotEq; break;
        case BOP_Lt: op = OP_Lt; break;
        case BOP_LtEq: op = OP_LtEq; break;
        case BOP_Gt: op = OP_Gt; break;
        case BOP_GtEq: op = OP_GtEq; break;
        default: fcCompNotSupported(cm, "BinaryOp %d", (int)b->op);
    }
    E_op(cm, op);
}

static void EmitCoercedOperands(FcComp* cm, FcExpr* leftExpr, FcExpr* rightExpr) {
    FcCpCoercion left = CompilerAnalyzeCoercion(leftExpr);
    FcCpCoercion right = CompilerAnalyzeCoercion(rightExpr);
    bool leftFixed, rightFixed, leftAuto, rightAuto;
    CompileExpr(cm, left.inner);
    if (left.explicitType >= 0) EmitCoerceTypeStatic(cm, TokenTypeToTag(cm, left.explicitType));
    if (left.explicitUnit != NULL) EmitCoerceUnitStatic(cm, fcUParse(CC_, left.explicitUnit));
    leftFixed = !cm->sh->anchorAtRunTime && CI_RequestsAnyAuto(&left) && !CI_RequestsAnyAuto(&right) && EmitFixedCoercion(cm, leftExpr, &left);
    CompileExpr(cm, right.inner);
    if (right.explicitType >= 0) EmitCoerceTypeStatic(cm, TokenTypeToTag(cm, right.explicitType));
    if (right.explicitUnit != NULL) EmitCoerceUnitStatic(cm, fcUParse(CC_, right.explicitUnit));
    rightFixed = !cm->sh->anchorAtRunTime && CI_RequestsAnyAuto(&right) && !CI_RequestsAnyAuto(&left) && EmitFixedCoercion(cm, rightExpr, &right);
    if (leftFixed || rightFixed) return;
    leftAuto = CI_RequestsAnyAuto(&left);
    rightAuto = CI_RequestsAnyAuto(&right);
    if (leftAuto && !rightAuto) {
        E_op(cm, OP_Swap);
        if (CI_TypeIsAuto(&left)) E_op(cm, OP_CoerceTypeDynamic);
        if (CI_UnitIsAuto(&left)) E_op(cm, left.relabelAuto ? OP_RelabelUnitDynamic : OP_CoerceUnitDynamic);
        E_op(cm, OP_Swap);
    } else if (rightAuto && !leftAuto) {
        if (CI_TypeIsAuto(&right)) E_op(cm, OP_CoerceTypeDynamic);
        if (CI_UnitIsAuto(&right)) E_op(cm, right.relabelAuto ? OP_RelabelUnitDynamic : OP_CoerceUnitDynamic);
    } else if (leftAuto && rightAuto) {
        if (CI_UnitIsAuto(&left)) {
            E_op(cm, OP_Swap);
            EmitUnitStatic(cm, FC_UNITLESS, left.relabelAuto);
            E_op(cm, OP_Swap);
        }
        if (CI_UnitIsAuto(&right)) EmitUnitStatic(cm, FC_UNITLESS, right.relabelAuto);
    }
}

static void EmitLoadConst(FcComp* cm, FcConst* v) {
    E_op(cm, OP_LoadConst);
    E_u16(cm, E_const(cm, v));
}

/* the JSON of an attribute argument (System.Text.Json with its default encoder) */
static void fcJsonEsc(FcBuf* b, const char* s) {
    const unsigned char* p = (const unsigned char*)s;
    fcBufChar(b, '"');
    while (*p) {
        uint16_t u = fc8Next(&p);
        switch (u) {
            case '\b': fcBufStr(b, "\\b"); break;
            case '\t': fcBufStr(b, "\\t"); break;
            case '\n': fcBufStr(b, "\\n"); break;
            case '\f': fcBufStr(b, "\\f"); break;
            case '\r': fcBufStr(b, "\\r"); break;
            case '\\': fcBufStr(b, "\\\\"); break;
            default:
                if (u < 0x20 || u > 0x7E || u == '"' || u == '&' || u == '\'' || u == '+' || u == '<' || u == '>' || u == '`') fcBufFmt(b, "\\u%04X", u);
                else fcBufChar(b, (char)u);
        }
    }
    fcBufChar(b, '"');
}
static const char* fcJsonDouble(FcCtx* c, double d) { return fcDoubleR(c, d); }
