/* native/forge/gen/cppgenerator5.c - src/fire.Native/CppGenerator.cs (commit 808e9788), part 5: StepCore - the start (aliases, borrowed slots, the scalar
 * shadows, the fields that are C numbers, the safe point of a loop) and the instructions of the stack, the variables, the static fields, the
 * natives of the runtime, `return` and the end of the program. The switch goes on in the next part (cppgenerator6.c); an instruction that is not
 * ported yet refuses (FG_UNPORTED). */

/* an emitter that prefixes its lines (the `x => E("  " + x)` of the C#) */
static void xEmitPrefixFn(FgEmit* e, const char* text) { FgSC* x = (FgSC*)e->buf; xE(x, fcFmt(g->c, "%s%s", e->indent, text)); }
static FgEmit xEmitterPrefix(FgSC* x, const char* prefix) { FgEmit e; e.fn = xEmitPrefixFn; e.buf = (FcBuf*)x; e.indent = prefix; return e; }
static FgStep fgStepEnd(const FgEdges* edges) { FgStep r; memset(&r, 0, sizeof r); if (edges) { r.extra = edges->items; r.extraCount = edges->count; } return r; }
static FgStep fgStepJump(int target, bool fallsThrough) { FgStep r; memset(&r, 0, sizeof r); r.hasJump = true; r.jumpTarget = target; r.fallsThrough = fallsThrough; return r; }

static FgStep StepCore2(FgSC* x);
/* (an object with probes - flag 2 - takes the field helper, whose setter tells them; a handler may throw) */
static const char* fgProbedCField(FgSC* x, FgCField cfield, const char* name, const char* o) {
    int ct = cfield.type;
    const char* exit;
    const char* leave;
    const char* number;
    if (!MayBeProbed(g, cfield.rc, name)) return "";
    exit = xCheckExit(x);
    leave = exit ? fcFmt(g->c, " if (FIRE_UNLIKELY(g_unwind.active)) goto %s;", exit) : "";
    number = KindOfType(ct) == KindFloat ? SF(d - 1) : KindOfType(ct) == KindBool ? SB(d - 1) : KindOfType(ct) == KindWord ? SW(d - 1) : SN(d - 1);
    return fcFmt(g->c, "if (FIRE_UNLIKELY(objAt(%s)->h.flags & 2)) { %s(%s, %s%s);%s } else ", o, FieldFn(g, "sf", name), o, ScalarValueOf(g, KindOfType(ct), UnitOfType(ct), number), FieldTail(g, name, f, "NULL"), leave);
}
static FgStep StepCoreNative(FgSC* x);

static FgStep StepCore(FgGen* gg, FgFunc* ff, FcChunk* fchunk, FgInstr* fins, FgFlow* fst, FcBuf* fsb, FcDict* flocals) {
    /* (positional: the names of the fields are the macros of the step) */
    FgSC xs = { gg, ff, fchunk, fins, fst, fsb, flocals, 0, NULL, -1 };
    FgSC* x = &xs;
    FgEmit e;
    FgCField cfield;
    xs.fn = ff->name;
    d = st->depth;
    e = xEmitter(x);
    if (AnyAlias(st) && !AliasAware(ins->op)) {
        int keepAlias = ins->op == OP_ArrayGet && st->depth >= 2 && st->depth - 2 < 64 && st->alias[st->depth - 2] && fgFlowHasArray(st, st->alias[st->depth - 2]) ? st->depth - 2 : -1;
        int k;
        for (k = 0; k < 64; k++) if (k != keepAlias) MaterializeAlias(g, st, k, &e);
    }
    if (st->borrowed != 0) {
        int consumed = RunsFireCode(ins);
        if (consumed >= 0 && d - consumed > 0) {
            uint64_t hold = st->borrowed & (d - consumed >= 64 ? ~0ULL : (1ULL << (d - consumed)) - 1);
            int k;
            for (k = 0; k < 64 && hold != 0; k++) {
                if (((hold >> k) & 1ULL) == 0) continue;
                hold &= ~(1ULL << k);
                st->borrowed &= ~(1ULL << k);
                if (st->alias[k] || ((st->scalar >> k) & 1ULL) != 0 || ((st->refs >> k) & 1ULL) == 0 || BaseKindOfType(st->types[k]) == KindObj || BaseKindOfType(st->types[k]) == KindArr || BaseKindOfType(st->types[k]) == KindBuf) continue;
                E("holdTemp(%s, &%s);", SlotName(g, st->region, k), OwnerList());
            }
        }
    }
    cfield = CFieldAccess(g, f, st, ins);
    if (st->scalar != 0 && !ScalarAware(ins->op) && !TypedNativeCall(g, ins) && !IntKeyHash(g, f, st, ins)
        && !(ins->op == OP_CallStaticMethod && (CAbiStaticCallee(g, f, ins) || streq0(Str(ins->a[0]), "Math")))
        && !(ins->op == OP_GetStaticField && ConstantStatic(g, Str(ins->a[0]), Str(ins->a[1])))
        /* (the C# has `&& !(ins.Op == OpCode.CallMethod && CAbiMethodCallee(...) != null)` here inside the comment of the line before: it does not count) */
        && !(ins->op == OP_GetField && CAbiGetterCallee(g, f, st, ins))
        && !GuardTopProven(st, ins)
        && !(ins->op == OP_Return && f->cabi && f->cabi->retType != 0) && !cfield.ok) MaterializeAll(g, f, st, &e, -1, -1);
    cfield = CFieldAccess(g, f, st, ins);
    /* (a field that is a C number: CppGenerator.CFields.cs) */
    if (cfield.ok) {
        const char* fieldName = Str(ins->a[0]);
        int ct = cfield.type;
        fcSetAdd(g->fieldNames, fieldName);
#define CF_NUMBER(k) (KindOfType(ct) == KindFloat ? SF(k) : KindOfType(ct) == KindBool ? SB(k) : KindOfType(ct) == KindWord ? SW(k) : SN(k))
        if (ins->op == OP_GetField) {
            const char* o = S(d - 1);
            E("if (FIRE_UNLIKELY(!leafAlive(%s))) %s", o, xThrower(x, fcFmt(g->c, "destroyedError(%s);", o)));
            SetScalar(d - 1, ct, CFieldRead(g, cfield, fcFmt(g->c, "objAt(%s)", o)));
            return Next();
        }
        {
            bool onThis = ins->op == OP_SetFieldOnThis;
            const char* o = onThis ? "self" : S(d - 2);
            const char* probed;
            {
                const char* thrower = xThrower(x, fcFmt(g->c, "destroyedError(%s);", o));
                const char* write;
                probed = fgProbedCField(x, cfield, fieldName, o);
                write = CFieldWrite(g, cfield, fcFmt(g->c, "objAt(%s)", o), CF_NUMBER(d - 1));
                E("if (FIRE_UNLIKELY(!leafAlive(%s))) %s %s%s", o, thrower, probed, write);
            }
            if (!onThis) {
                if (IsScalar(d - 1)) SetScalar(d - 2, Ty(d - 1), CF_NUMBER(d - 1));
                else { E("%s = %s;", S(d - 2), S(d - 1)); SetR(d - 2, false); SetT(d - 2, Ty(d - 1)); g->kindsKept = true; }
            }
            d--; return Next();
        }
#undef CF_NUMBER
    }

    /* a safe point: a jump back (a loop) lets the other threads run now and then, and looks at the signals (leave, terminate) */
    if (g->usesThreads && sb && ins->n > 0 && ins->a[0] <= ins->addr && IsLoopJump(ins->op)) {
        int k;
        E("if (FIRE_UNLIKELY(pollLoop())) goto %s;", ExitLabel(g, f, st, locals));
        for (k = 0; k < st->arrayCount; k++) ES(ArrayRecheckText(g, st->arrays[k], fgKnownArrayElem(g, f, st->arrays[k])));
    }

    switch (ins->op) {
        case OP_LoadConst:
            xPushConstant(x, fgConst(x->chunk, ins->a[0]));
            d++; return Next();
        case OP_Pop:
            Need(1); d--; g->kindsKept = true; return Next();
        case OP_Dup:
            Need(1);
            if (SlotScalars() && IsScalar(d - 1)) { const char* src = ScalarName(g, f, st, d - 1); SetScalar(d, Ty(d - 1), src); d++; return Next(); }
            if (d - 1 < 64 && d < 64 && st->alias[d - 1]) st->alias[d] = st->alias[d - 1];
            else E("%s = %s;", S(d), S(d - 1));
            SetR(d, R(d - 1)); SetT(d, Ty(d - 1)); g->kindsKept = true;
            if (d < 64 && ((st->borrowed >> (d - 1)) & 1ULL) != 0) st->borrowed |= 1ULL << d;
            d++; return Next();
        case OP_Swap: {
            bool a, b;
            uint64_t borrowedPair;
            int ta, tb;
            Need(2);
            {
                const char* s1 = S(d - 1);
                const char* s2 = S(d - 2);
                E("{ Value t = %s; %s = %s; %s = t; }", s1, S(d - 1), s2, S(d - 2));
            }
            a = R(d - 1); b = R(d - 2);
            borrowedPair = d <= 64 ? (st->borrowed >> (d - 2)) & 3ULL : 0;
            SetR(d - 1, b); SetR(d - 2, a);
            if (d <= 64) st->borrowed |= ((borrowedPair & 1ULL) << (d - 1)) | ((borrowedPair >> 1) << (d - 2));
            ta = Ty(d - 1); tb = Ty(d - 2); SetT(d - 1, tb); SetT(d - 2, ta); g->kindsKept = true;
            return Next();
        }
        case OP_DeclareLocal: {
            int id, declared;
            const char* v;
            const char* key;
            Need(1);
            id = st->scopes[st->scopeCount - 1].id; declared = st->scopes[st->scopeCount - 1].declared;
            if (id == GlobalScope) v = GlobalVar(g, declared, locals);
            else {
                v = id == FunctionScope ? fcFmt(g->c, "P%d", declared) : fcFmt(g->c, "B%d_%d", id, declared);
                fcSetAdd(locals, v);
            }
            key = VarKey(g, f, v);
            MaterializeAliases(g, st, &e, v);
            if (ScalarVar(v)) E("%s = %s;", v, xScalarValue(x, v, d - 1));
            else if (R(d - 1)) { MarkVarRef(g, key); E("%s = %s; retain(%s);", v, SR(d - 1), v); }
            else E("%s = %s;", v, SR(d - 1));
            JoinVarType(g, key, Ty(d - 1));
            if (id != GlobalScope && !ScalarVar(v)) xKnowArray(x, v, d - 1); else xForgetArray(x, v);
            st->scopes[st->scopeCount - 1].declared = declared + 1;
            g->kindsKept = true;
            d--; return Next();
        }
        case OP_LoadLocal: {
            const char* v = Var(g, st, ins->a[0], ins->a[1], locals);
            if (ScalarVar(v)) {
                if (SlotScalars()) SetScalar(d, xLoadType(x, v), v);
                else { E("%s = %s;", S(d), ScalarValueOf(g, KindOfType(xLoadType(x, v)), UnitOfType(xLoadType(x, v)), v)); SetR(d, false); SetT(d, xLoadType(x, v)); g->kindsKept = true; }
                d++; return Next();
            }
            if (AliasOn(g, f) && d < 64 && (v[0] == 'P' || v[0] == 'B')) st->alias[d] = v;
            else E("%s = %s;", S(d), v);
            SetR(d, VarRef(g, VarKey(g, f, v)));
            SetT(d, xRawParam(x, v) ? TypeUnknown : xLoadType(x, v)); g->kindsKept = true;
            d++; return Next();
        }
        case OP_StoreLocal:
        case OP_StoreLocalPop: {
            const char* v;
            Need(1);
            v = Var(g, st, ins->a[0], ins->a[1], locals);
            StoreVar(v, d - 1);
            if (!xRawParam(x, v)) JoinVarType(g, VarKey(g, f, v), Ty(d - 1));
            if (!xRawParam(x, v) && !ScalarVar(v)) xKnowArray(x, v, d - 1);
            g->kindsKept = true;
            if (ins->op == OP_StoreLocalPop) d--;
            return Next();
        }
        case OP_LoadGlobal: {
            const char* gv = GlobalVar(g, ins->a[0], locals);
            if (g->usesThreads && f->kind == FK_FireBody && gv[0] == 'G') E("FIRE_DOM_LOAD(%s, %s);", S(d), gv); else E("%s = %s;", S(d), gv);
            SetR(d, VarRef(g, VarKey(g, f, gv))); SetT(d, xLoadType(x, gv)); g->kindsKept = true; d++; return Next();
        }
        case OP_StoreGlobal:
        case OP_StoreGlobalPop: {
            const char* gv;
            Need(1);
            gv = GlobalVar(g, ins->a[0], locals);
            StoreVar(gv, d - 1);
            JoinVarType(g, VarKey(g, f, gv), Ty(d - 1)); g->kindsKept = true;
            if (ins->op == OP_StoreGlobalPop) d--;
            return Next();
        }
        case OP_ArithLocalConstPop:
        case OP_ArithGlobalConstPop: {
            bool local = ins->op == OP_ArithLocalConstPop, subtract, stepMode, ints;
            const char* v;
            const FcConst* constant;
            const char* key;
            const char* sec;
            const char* k;
            int mode, varType, constType, varKind, constKind, varUnit, constUnit;
            if (local) { v = Var(g, st, ins->a[0], ins->a[1], locals); constant = fgConst(x->chunk, ins->a[2]); mode = ins->a[3]; }
            else { v = GlobalVar(g, ins->a[0], locals); constant = fgConst(x->chunk, ins->a[1]); mode = ins->a[2]; }
            subtract = (mode & 1) != 0;
            stepMode = (mode & 2) != 0;
            key = VarKey(g, f, v);
            if (constant->kind == CK_String) MarkVarRef(g, key);
            g->kindsKept = true;
            varType = xLoadType(x, v); constType = TypeOfConstant(g, constant);
            varKind = KindOfType(varType); constKind = KindOfType(constType);
            varUnit = UnitOfType(varType);
            constUnit = stepMode && varUnit != 0 ? varUnit : UnitOfType(constType);
            if (local && ScalarVar(v)) {
                bool word;
                g->neutralAfter = true;
                word = streq0(ScalarVarCType(g, f, v), "FireWord");
                if (streq0(ScalarVarCType(g, f, v), "Real")) {
                    const char* cr;
                    if (!IsNum(constKind) || constUnit != varUnit) { JoinVarType(g, key, TypeUnknown); return Next(); }
                    cr = constKind == KindFloat ? fcFmt(g->c, "(Real)%s", DoubleLiteral(g, fgConstF(constant), g->program->floatWidth == 32)) : fcFmt(g->c, "(Real)%" PRId64 "LL", constant->i);
                    E("%s = (Real)(%s %s %s);", v, v, subtract ? "-" : "+", cr);
                    JoinVarType(g, key, TypeOf(KindFloat, varUnit));
                    return Next();
                }
                if (constKind != KindInt || (word ? UnitOfType(constType) != 0 : constUnit != varUnit) || (!word && !IsNarrowIntType(constType))) {
                    JoinVarType(g, key, TypeUnknown);
                    return Next();
                }
                if (word) E("%s = (FireWord)(%s %s (FireWord)(uint64_t)%" PRId64 "LL);", v, v, subtract ? "-" : "+", constant->i);
                else E("%s = (FireInt)((FireUInt)%s %s (FireUInt)%" PRId64 "LL);", v, v, subtract ? "-" : "+", constant->i);
                JoinVarType(g, key, word ? TypeOf(KindWord, 0) : TypeOf(KindInt, varUnit) | NarrowBit);
                return Next();
            }
            if (varKind == KindWord && constKind == KindInt && UnitOfType(constType) == 0) {
                sec = SectionFor(g, v);
                E("%s%s = WordV((uint64_t)%s.w %s (uint64_t)(int64_t)%s.i);%s", *sec ? fcFmt(g->c, "{ %s", sec) : "", v, v, subtract ? "-" : "+", Constant(g, constant), *sec ? fcFmt(g->c, " %s}", SectionEnd(g, v)) : "");
                JoinVarType(g, key, TypeOf(KindWord, 0));
                return Next();
            }
            if (IsNum(varKind) && IsNum(constKind) && varUnit == constUnit) {
                const char* op = subtract ? "-" : "+";
                const char* unitArg;
                const char* kc;
                sec = SectionFor(g, v);
                unitArg = varUnit == 0 ? "" : fcFmt(g->c, ", %d", varUnit);
                kc = Constant(g, constant);
                ints = varKind == KindInt && constKind == KindInt;
                if (ints)
                    E("%s%s = IntOp((int64_t)((uint64_t)%s.i %s (uint64_t)%s.i), %s, %s, %s);%s", *sec ? fcFmt(g->c, "{ %s", sec) : "", v, v, op, kc, varUnit == 0 ? "0" : fcFmt(g->c, "%d", varUnit), v, kc, *sec ? fcFmt(g->c, " %s}", SectionEnd(g, v)) : "");
                else {
                    const char* xv = varKind == KindFloat ? fcFmt(g->c, "%s.f", v) : fcFmt(g->c, "(Real)%s.i", v);
                    const char* yv = constKind == KindFloat ? fcFmt(g->c, "%s.f", kc) : fcFmt(g->c, "(Real)%s.i", kc);
                    E("%s%s = Float(%s %s %s%s);%s", *sec ? fcFmt(g->c, "{ %s", sec) : "", v, xv, op, yv, unitArg, *sec ? fcFmt(g->c, " %s}", SectionEnd(g, v)) : "");
                }
                JoinVarType(g, key, TypeOf(ints ? KindInt : KindFloat, varUnit) | (ints && (varType & constType & NarrowBit) != 0 ? NarrowBit : 0));
                return Next();
            }
            JoinVarType(g, key, TypeUnknown);
            k = Constant(g, constant);
            if (stepMode) k = fcFmt(g->c, "stepOf(%s, %s)", v, k);
            if (!subtract && VarRef(g, key)) {
                E("{ %sValue o = %s; %s = addR(o, %s, &%s); retain(%s); release(o); %s}", SectionFor(g, v), v, v, k, OwnerList(), v, SectionEnd(g, v));
                Check();
            } else {
                if (*SectionFor(g, v)) E("{ %s%s = %s(%s, %s); %s}", SectionFor(g, v), v, subtract ? "sub" : "add", v, k, SectionEnd(g, v));
                else E("%s = %s(%s, %s);", v, subtract ? "sub" : "add", v, k);
                xCheckIf(x, fcFmt(g->c, "%s.kind == K_Undefined", v));
            }
            return Next();
        }
        case OP_CallNative:
            return StepCoreNative(x);
        case OP_Return: {
            bool isCtor, anything = false, tryActive;
            FgEdges edges = {0};
            int i, k;
            Need(1);
            if (fgIsTop(f)) FG_NOTSUP("return in top-level code");
            isCtor = f->kind == FK_Ctor;
            if (!isCtor) JoinVarType(g, ReturnKey(g, f), Ty(d - 1));
            if (isCtor && f->chunk->ownerClass && f->chunk->ownerClass->isRecord) {
                FgClassInfo* recordClass = (FgClassInfo*)fcDictGet(g->classes, f->chunk->ownerClass->name);
                if (recordClass) { E("recordCtorEnd(self, %s);", ClassIdExpr(g, recordClass)); Check(); }
            }
            for (i = 0; i < st->scopeCount && !anything; i++) {
                FcList* vars;
                if (fgISetHas(g->c, f->needsList, st->scopes[i].id)) { anything = true; break; }
                vars = ScopeVariables(g, f, st->scopes[i].id, locals);
                for (k = 0; k < vars->count; k++) if (VarRef(g, VarKey(g, f, (const char*)vars->items[k]))) { anything = true; break; }
            }
            tryActive = st->region != NULL || st->handlerCount > 0;
            if (f->cabi && f->cabi->retType != 0) {
                int rk = d - 1, kind = KindOfType(f->cabi->retType);
                const char* number = kind == KindBool ? SB(rk) : kind == KindWord ? SW(rk) : kind == KindFloat ? SF(rk) : SN(rk);
                FgEmit pe = xEmitterPrefix(x, "  ");
                if (!anything) { E("return %s;", number); return fgStepEnd(NULL); }
                E("{ %s r = %s;", CTypeOf(f->cabi->retType), number);
                if (f->accTrack) ES("  if (g_acc.top > ACCB) g_acc.top = ACCB;");
                ReleaseScopes(g, f, st, st->scopeCount, 0, false, locals, &pe, NULL);
                ES("  return r; }");
                return fgStepEnd(NULL);
            }
            if (!isCtor && !tryActive && !R(d - 1) && !anything) { E("return %s;", SR(d - 1)); return fgStepEnd(NULL); }
            {
                FgEmit pe = xEmitterPrefix(x, "  ");
                E("{ Value r = %s; (void)r;", SR(d - 1));
                ReturnSeq(g, f, st, R(d - 1), true, sb ? &pe : NULL, locals, &edges);
                ES("}");
            }
            return fgStepEnd(&edges);
        }
        case OP_Halt:
            if (!fgIsTop(f)) FG_NOTSUP("Halt in a function");
            HaltCode(g, f, &e);
            ES("return;");
            return fgStepEnd(NULL);
        case OP_SetEndWait:
            Need(1);
            if (g->usesThreads) E("fire_setEndWait(%s);", S(d - 1));
            d--;
            return Next();
        case OP_SetTimeout:
        case OP_SetAutoSync:
            if (ins->op == OP_SetTimeout) { Need(1); E("setDefaultTimeout(%s);", S(d - 1)); NeedTimeObj(g); d--; }
            else if (g->usesThreads && ins->a[0] != 1 /* GlobalJobsMode.Safepoints */) E("g_globalJobs = %d; g_attnMask &= ~ATTN_SECT;", ins->a[0]);
            return Next();
        case OP_LoadThis:
            xRequireSelf(x);
            if (f->kind == FK_Lambda) {
                E("%s = FIRE_UNLIKELY(self.kind == K_Undefined) ? fireError(\"ArgumentException\", \"'this' is not bound at this point.\") : self;", S(d));
                Check();
            }
            else if (AliasOn(g, f) && d < 64) st->alias[d] = "self";
            else E("%s = self;", S(d));
            SetR(d, IsExtensionMethod(g, f->proto));
            if (fgHasSelf(f) && !IsExtensionMethod(g, f->proto) && f->chunk->ownerClass) { SetT(d, ObjType(g, f->chunk->ownerClass)); g->kindsKept = true; }
            d++; return Next();
        case OP_CallProtoWithThis: {
            int argc = ins->a[1], slot;
            FcProto* proto;
            FgFunc* target;
            Need(argc + 1);
            proto = (FcProto*)x->chunk->functions->items[ins->a[0]];
            if (proto->paramCount != argc) FG_NOTSUP("initializer with %d parameters called with %d", proto->paramCount, argc);
            target = GetFunc(g, proto, FK_Init, 0);
            slot = d - argc - 1;
            {
                const char* s0 = S(slot);
                const char* args = Args(d - argc, argc);
                E("%s = %s(%s%s);", s0, target->name, s0, args);
            }
            Check();
            AdoptResult(slot, ReturnType(g, target));
            d = slot + 1; return Next();
        }
        case OP_GetStaticField: {
            FcRtClass* getRc = FindClass(g, Str(ins->a[0]));
            const FcRtClass* declaring;
            const FcConst* constant;
            const char* sfVar;
            int access;
            if (!fgFindStaticFieldOwner(getRc, Str(ins->a[1]))) {
                FgFound found = FG_FIND(getRc, fcFmt(g->c, "get_%s", Str(ins->a[1])), 0);
                if (found.proto && found.proto->isStatic) {
                    xAccessCheck(x, found.declaring, found.access, fcFmt(g->c, "'get_%s' of '%s' is %s and cannot be accessed from here.", Str(ins->a[1]), found.declaring->name, DescribeAccess(found.access)));
                    E("%s = %s();", S(d), GetFunc(g, found.proto, FK_Static, 0)->name);
                    Check();
                    AdoptResult(d, TypeUnknown);
                    d++; return Next();
                }
            }
            if ((declaring = fgFieldAccess(getRc, Str(ins->a[1]), &access)) != NULL)
                xAccessCheck(x, declaring, access, fcFmt(g->c, "Static field '%s' of '%s' is %s and cannot be accessed from here.", Str(ins->a[1]), declaring->name, DescribeAccess(access)));
            if ((constant = ConstantStatic(g, Str(ins->a[0]), Str(ins->a[1]))) != NULL) { xPushConstant(x, constant); d++; return Next(); }
            sfVar = StaticFieldVar(g, Str(ins->a[0]), Str(ins->a[1]));
            if (g->usesThreads) E("FIRE_DOM_LOAD(%s, %s);", S(d), sfVar); else E("%s = %s;", S(d), sfVar);
            SetR(d, VarRef(g, sfVar));
            d++; return Next();
        }
        case OP_SetStaticField:
        case OP_SetStaticFieldOnInit: {
            FcRtClass* setRc;
            const FcRtClass* owner;
            const FcRtClass* declaring;
            const char* sfVar;
            const char* staticClass;
            const char* staticArray;
            int access, staticKind;
            Need(1);
            setRc = FindClass(g, Str(ins->a[0]));
            if (!fgFindStaticFieldOwner(setRc, Str(ins->a[1]))) {
                FgFound found = FG_FIND(setRc, fcFmt(g->c, "set_%s", Str(ins->a[1])), 1);
                if (found.proto && found.proto->isStatic) {
                    if (ins->op == OP_SetStaticField)
                        xAccessCheck(x, found.declaring, found.access, fcFmt(g->c, "'set_%s' of '%s' is %s and cannot be accessed from here.", Str(ins->a[1]), found.declaring->name, DescribeAccess(found.access)));
                    {
                        const char* fname = GetFunc(g, found.proto, FK_Static, 0)->name;
                        const char* arg = S(d - 1);
                        E("{ Value r = %s(%s); adopt(r, &%s); }", fname, arg, OwnerList());
                    }
                    Check();
                    return Next();
                }
            }
            if (ins->op == OP_SetStaticField && (declaring = fgFieldAccess(setRc, Str(ins->a[1]), &access)) != NULL)
                xAccessCheck(x, declaring, access, fcFmt(g->c, "Static field '%s' of '%s' is %s and cannot be accessed from here.", Str(ins->a[1]), declaring->name, DescribeAccess(access)));
            sfVar = StaticFieldVar(g, Str(ins->a[0]), Str(ins->a[1]));
            owner = fgFindStaticFieldOwner(setRc, Str(ins->a[1]));
            if (owner && (staticKind = fgFieldRequiredKind(owner, Str(ins->a[1]))) != 0) {
                ES(GuardStmt(g, S(d - 1), staticKind, fcFmt(g->c, "Field '%s'", Str(ins->a[1])), xCheckExit(x)));
                if (fgDkIsString(staticKind)) SetR(d - 1, true);
            }
            if (owner && (staticClass = fgFieldRequiredType(owner, Str(ins->a[1]))) != NULL && !FitsClassType(g, Ty(d - 1), staticClass)) {
                ES(ClassGuard(g, S(d - 1), staticClass, fcFmt(g->c, "Field '%s'", Str(ins->a[1])), "NULL", NULL, ""));
                Check();
            }
            if (owner && *(staticArray = FieldArrayCheck(g, owner, Str(ins->a[1]), NULL))) {
                ES(fgReplace(g->c, staticArray, "typeArray(x,", fcFmt(g->c, "typeArray(%s,", S(d - 1))));
                Check();
            }
            StoreVar(sfVar, d - 1);
            return Next();
        }
        case OP_ReleaseTemps: {
            int temp = TempOf(st->scopes[st->scopeCount - 1].id);
            int scopeBit = st->scopeCount - 1, k;
            bool dirty = scopeBit >= 64 || ((st->tempsDirty >> scopeBit) & 1) != 0;
            if (fgISetHas(g->c, f->needsList, temp) && dirty) {
                E("leave(&%s);", ListName(g, temp));
                for (k = 0; k < st->arrayCount; k++) ES(ArrayCheckText(g, st->arrays[k], fgKnownArrayElem(g, f, st->arrays[k])));
                g->neutralAfter = st->arrayCount > 0;
            }
            else { g->selfAliveAfter = st->selfAlive ? 1 : 0; g->neutralAfter = true; }
            if (scopeBit < 64) st->tempsDirty &= ~(1ULL << scopeBit);
            return Next();
        }
        case OP_ClaimToScope: {
            int depth, flags, targetIndex;
            const char* target;
            Need(1);
            g->kindsKept = true;
            if (IsPlainType(Ty(d - 1))) return Next();
            depth = ins->a[0]; flags = ins->a[1];
            targetIndex = depth == 0xFFFF ? (fgIsTop(f) ? 0 : -1) : (st->scopeCount - 1 - depth > 0 ? st->scopeCount - 1 - depth : 0);
            if (targetIndex < 0) {
                if (!g->usesGlobalOwn) { g->usesGlobalOwn = true; g->version++; }
                target = "g_globalOwn";
            } else {
                fgISetAdd(g->c, f->needsList, st->scopes[targetIndex].id);
                target = fcFmt(g->c, "&%s", ListName(g, st->scopes[targetIndex].id));
            }
            if ((flags & 1) != 0 && depth == 0xFFFF && g->usesThreads) E("noteGlobalClaim(%s, &%s);", SR(d - 1), OwnerList());
            if ((flags & 1) != 0) E("claimToList(%s, &%s, %s);", SR(d - 1), OwnerList(), target);
            if ((flags & 2) != 0 && targetIndex >= 0) {
                int n = 0, i;
                int* sources = (int*)fcAlloc(g->c, sizeof(int) * (size_t)(2 * (st->scopeCount - targetIndex) + 1));
                for (i = targetIndex + 1; i < st->scopeCount; i++) { sources[n++] = st->scopes[i].id; sources[n++] = TempOf(st->scopes[i].id); }
                if (!sb) {
                    FgHoist* h = (FgHoist*)fcAlloc(g->c, sizeof *h);
                    h->target = st->scopes[targetIndex].id; h->sources = sources; h->sourceCount = n;
                    fcListAdd(g->c, f->hoists, h);
                }
                for (i = 0; i < n; i++) if (fgISetHas(g->c, f->needsList, sources[i])) E("hoistFrom(%s, &%s, %s);", SR(d - 1), ListName(g, sources[i]), target);
            }
            return Next();
        }
        default:
            return StepCore2(x);
    }
}

/* the natives of the runtime (OpCode.CallNative) */
static FgStep StepCoreNative(FgSC* x) {
    int argc = ins->a[1], first;
    const char* native;
    Need(argc);
    native = ins->a[0] < g->natives->count ? (const char*)g->natives->items[ins->a[0]] : "?";
    first = d - argc;
#define FG_UNDEF_OR(k, cond) ((cond) ? S(k) : "Undef()")
    if (streq0(native, "print") && argc == 1) {
        const char* s = S(d - 1);
        E("%s = print(%s, &%s);", s, s, OwnerList());
        Check();
        SetR(d - 1, false);
        return Next();
    }
    if (streq0(native, "__StringCall") && argc >= 2 && argc <= 4) {
        const char* a0;
        const char* a1;
        g->usesStringCall = true;
        a0 = argc > 2 ? S(first + 2) : "Undef()"; a1 = argc > 3 ? S(first + 3) : "Undef()";
        { const char* s0 = S(first); const char* s1 = S(first + 1); E("%s = stringCall(%s.i, %s, %d, %s, %s, &%s);", s0, s0, s1, argc - 2, a0, a1, OwnerList()); }
        Check();
        d = first + 1; SetR(first, true); return Next();
    }
    if (streq0(native, "__ResourceCall") && argc == 2) {
        { const char* s0 = S(first); const char* s1 = S(first + 1); E("%s = resourceCall(%s.i, %s, &%s);", s0, s0, s1, OwnerList()); }
        Check();
        d = first + 1; SetR(first, true); return Next();
    }
    if (streq0(native, "__EnvCall") && argc >= 1 && argc <= 3) {
        { const char* s0 = S(first); const char* s1 = FG_UNDEF_OR(first + 1, argc > 1); const char* s2 = FG_UNDEF_OR(first + 2, argc > 2); E("%s = envCall(%s.i, %s, %s, &%s);", s0, s0, s1, s2, OwnerList()); }
        Check();
        d = first + 1; SetR(first, true); return Next();
    }
    if (streq0(native, "__BitsCall") && argc >= 2 && argc <= 4) {
        { const char* s0 = S(first); const char* s1 = S(first + 1); const char* s2 = FG_UNDEF_OR(first + 2, argc > 2); const char* s3 = FG_UNDEF_OR(first + 3, argc > 3); E("%s = bitsCall(%s.i, %s, %s, %s);", s0, s0, s1, s2, s3); }
        Check();
        d = first + 1; SetR(first, false); return Next();
    }
    if (streq0(native, "__MathCall") && (argc == 2 || argc == 3)) {
        if (IntKeyHash(g, f, st, ins)) {
            SetScalar(first, TypeOf(KindInt, 0), fcFmt(g->c, "(FireInt)fire_mixHash((uint64_t)%s)", SN(first + 1)));
            d = first + 1; return Next();
        }
        { const char* s0 = S(first); const char* s1 = S(first + 1); const char* s2 = FG_UNDEF_OR(first + 2, argc == 3); E("%s = mathCall(%s.i, %s, %s);", s0, s0, s1, s2); }
        Check();
        d = first + 1; SetR(first, false); return Next();
    }
    if (streq0(native, "__JsonCall") && (argc == 2 || argc == 3)) {
        UseJson(g);
        g->callsJsonEngine = true;
        {
            const char* s0 = S(first);
            const char* s1 = S(first + 1);
            const char* s2 = argc == 3 ? S(first + 2) : "Undef()";
            E("%s = fire_jsonCall(%d, %s, %s, %s, %s, &%s);", s0, argc, s0, s1, s2, JsonCallType(f), OwnerList());
        }
        Check();
        d = first + 1; SetR(first, true); return Next();
    }
    if (streq0(native, "__LibCall") && argc >= 2 && argc <= 5) {
        g->usesLibCall = true;
        if (!fcListHasStr(g->errorClasses, "ArgumentException")) fcListAdd(g->c, g->errorClasses, (void*)"ArgumentException");
        if (!fcListHasStr(g->errorClasses, "LibraryException")) fcListAdd(g->c, g->errorClasses, (void*)"LibraryException");
        { const char* s0 = S(first); const char* s1 = S(first + 1); const char* s2 = FG_UNDEF_OR(first + 2, argc > 2); const char* s3 = FG_UNDEF_OR(first + 3, argc > 3); const char* s4 = FG_UNDEF_OR(first + 4, argc > 4);
          E("%s = libCall(%s, %s, %s, %s, %s, &%s);", s0, s0, s1, s2, s3, s4, OwnerList()); }
        Check();
        d = first + 1; SetR(first, true); return Next();
    }
    if (streq0(native, "__lambda_handover") && argc == 1) {
        const char* moved;
        UseThreads(g);
        first = d - 1;
        moved = SR(first);
        { const char* t0 = T(first); E("%s = fire_lambdaHandover(%s, &%s);", t0, moved, OwnerList()); }
        Check();
        d = first + 1; SetR(first, false); return Next();
    }
    if (streq0(native, "__SbCall") && (argc == 2 || argc == 3)) {
        if (!fcListHasStr(g->errorClasses, "ArgumentException")) fcListAdd(g->c, g->errorClasses, (void*)"ArgumentException");
        { const char* s0 = S(first); const char* s1 = S(first + 1); const char* s2 = FG_UNDEF_OR(first + 2, argc == 3); E("%s = sbCall(%s.i, %s, %s, &%s);", s0, s0, s1, s2, OwnerList()); }
        Check();
        d = first + 1; SetR(first, true); return Next();
    }
    if (streq0(native, "__array_copy") && argc == 3) {
        const char* a = SR(first);
        const char* b = SR(first + 1);
        const char* c3 = SR(first + 2);
        { const char* t0 = T(first); E("%s = fire_arrayCopy(%s, %s, %s);", t0, a, b, c3); }
        Check();
        d = first + 1; SetR(first, false); return Next();
    }
    {
        static const char* const cancel[] = { "__cancel_new", "__cancel_release", "__cancel_state", "__cancel_current", "__cancel_ref", "__cancel_cancel", "__cancel_after", "__cancel_time" };
        static const char* const chan[] = { "__chan_new", "__chan_ref", "__chan_release", "__chan_close", "__chan_closed", "__chan_count", "__chan_send", "__chan_receive", "__chan_watch" };
        static const char* const completion[] = { "__completion_new", "__completion_ref", "__completion_release", "__completion_done", "__completion_set", "__completion_wait" };
        size_t k;
        int i;
        for (k = 0; k < sizeof cancel / sizeof cancel[0]; k++)
            if (streq0(native, cancel[k])) {
                FcList* args = fcListNew(g->c);
                UseThreads(g);
                for (i = first; i < first + argc; i++) fcListAdd(g->c, args, (void*)SR(i));
                E("%s = fire_%s(%s);", T(first), native + 2, fgJoin(g->c, args, ", "));
                Check();
                d = first + 1; SetR(first, false); return Next();
            }
        if (streq0(native, "__type_arg") && argc == 1) {
            const char* index = SR(first);
            if (f->hasCallTArgs) { const char* t0 = T(first); E("{ Value ix_ = %s; %s = ctargs && ix_.kind == K_Int && ix_.i >= 0 && ctargs[ix_.i] && ctargs[ix_.i]->display ? strFromUtf8(ctargs[ix_.i]->display, &%s) : Undef(); }", index, t0, OwnerList()); }
            else E("%s = Undef();", T(first));
            d = first + 1; SetR(first, false); return Next();
        }
        if (streq0(native, "__fd_wait") && (argc == 3 || argc == 4)) {
            FcList* args = fcListNew(g->c);
            for (i = first; i < first + argc; i++) fcListAdd(g->c, args, (void*)SR(i));
            E("%s = %s(%s);", T(first), argc == 4 ? "fire_fd_wait_or" : "fire_fd_wait", fgJoin(g->c, args, ", "));
            Check();
            d = first + 1; SetR(first, false); return Next();
        }
        for (k = 0; k < sizeof chan / sizeof chan[0]; k++)
            if (streq0(native, chan[k])) {
                FcList* args = fcListNew(g->c);
                bool moves = streq0(native, "__chan_send") || streq0(native, "__chan_receive");
                UseThreads(g);
                for (i = first; i < first + argc; i++) fcListAdd(g->c, args, (void*)SR(i));
                if (moves) fcListAdd(g->c, args, (void*)fcFmt(g->c, "&%s", OwnerList()));
                E("%s = fire_%s(%s);", T(first), native + 2, fgJoin(g->c, args, ", "));
                Check();
                d = first + 1; SetR(first, streq0(native, "__chan_receive")); return Next();
            }
        for (k = 0; k < sizeof completion / sizeof completion[0]; k++)
            if (streq0(native, completion[k])) {
                FcList* args = fcListNew(g->c);
                UseThreads(g);
                for (i = first; i < first + argc; i++) fcListAdd(g->c, args, (void*)SR(i));
                if (streq0(native, "__completion_wait")) fcListAdd(g->c, args, (void*)fcFmt(g->c, "&%s", OwnerList()));
                E("%s = fire_%s(%s);", T(first), native + 2, fgJoin(g->c, args, ", "));
                Check();
                d = first + 1; SetR(first, streq0(native, "__completion_wait")); return Next();
            }
    }
    if (streq0(native, "__await_run") && argc == 3) {
        if (g->target->supportsThreads) UseThreads(g);
        { const char* s0 = S(first); const char* s1 = S(first + 1); const char* s2 = S(first + 2); E("%s = awaitRun(%s, %s, %s, &%s);", s0, s0, s1, s2, OwnerList()); }
        Check();
        d = first + 1; SetR(first, false); return Next();
    }
    if (streq0(native, "__CharCall") && argc == 2) {
        { const char* s0 = S(first); const char* s1 = S(first + 1); E("%s = charCall(%s.i, %s, &%s);", s0, s0, s1, OwnerList()); }
        d = first + 1; SetR(first, true); return Next();
    }
    if (fcStartsWith(native, "__refl_") && fgIsReflectionNative(native)) {
        const char* callerArg = "g_reflCaller";
        const char* list;
        const char* call = NULL;
        const char* a[4] = { NULL, NULL, NULL, NULL };
        UseReflection(g);
        UseReflectionLevel(g, native); fcSetAdd(g->reflNativesUsed, native);
        if (streq0(native, "__refl_probe") || streq0(native, "__refl_silence") || streq0(native, "__refl_silence_handle")) UseProbes(g);
        if (streq0(native, "__refl_probe")) NoteProbeAnything(g);   /* (Reflect.Probe: a name computed at run time) */
        list = fcFmt(g->c, "&%s", OwnerList());
#define FG_REFL(nm, n) (streq0(native, nm) && argc == (n) && ((n) < 1 || (a[0] = S(first))) && ((n) < 2 || (a[1] = S(first + 1))) && ((n) < 3 || (a[2] = S(first + 2))) && ((n) < 4 || (a[3] = S(first + 3))))
        if (FG_REFL("__refl_class_name", 1)) call = fcFmt(g->c, "rf_class_name(%s, %s)", a[0], list);
        else if (FG_REFL("__refl_class_info", 1)) call = fcFmt(g->c, "rf_class_info(%s, %s)", a[0], list);
        else if (FG_REFL("__refl_members", 1)) call = fcFmt(g->c, "rf_members(%s, %s)", a[0], list);
        else if (FG_REFL("__refl_classes", 0)) call = fcFmt(g->c, "rf_classes(%s)", list);
        else if (FG_REFL("__refl_is_sub", 2)) call = fcFmt(g->c, "rf_is_sub(%s, %s)", a[0], a[1]);
        else if (FG_REFL("__refl_get", 2)) call = fcFmt(g->c, "rf_get(%s, %s, %s, %s)", a[0], a[1], list, callerArg);
        else if (FG_REFL("__refl_set", 3)) call = fcFmt(g->c, "rf_set(%s, %s, %s, %s, %s)", a[0], a[1], a[2], list, callerArg);
        else if (FG_REFL("__refl_call", 3)) call = fcFmt(g->c, "rf_call(%s, %s, %s, %s, %s)", a[0], a[1], a[2], list, callerArg);
        else if (FG_REFL("__refl_new", 2)) call = fcFmt(g->c, "rf_new(%s, %s, %s, %s)", a[0], a[1], list, callerArg);
        else if (FG_REFL("__refl_has", 2)) call = fcFmt(g->c, "rf_has(%s, %s)", a[0], a[1]);
        else if (FG_REFL("__refl_selector_path", 1)) call = fcFmt(g->c, "rf_selector_path(%s, %s)", a[0], list);
        else if (FG_REFL("__refl_member_kind", 2)) call = fcFmt(g->c, "rf_member_kind(%s, %s, %s)", a[0], a[1], list);
        else if (FG_REFL("__refl_probe", 4)) call = fcFmt(g->c, "rf_probe(%s, %s, %s, %s, %s, %s)", a[0], a[1], a[2], a[3], list, callerArg);
        else if (FG_REFL("__refl_silence", 2)) call = fcFmt(g->c, "rf_silence(%s, %s)", a[0], a[1]);
        else if (FG_REFL("__refl_silence_handle", 1)) call = fcFmt(g->c, "rf_silence_handle(%s)", a[0]);
        else if (FG_REFL("__refl_enum_name", 1)) call = fcFmt(g->c, "rf_enum_name(%s, %s)", a[0], list);
        else if (FG_REFL("__refl_enum_of", 2)) call = fcFmt(g->c, "rf_enum_of(%s, %s)", a[0], a[1]);
        else FG_NOTSUP("native function '%s' with %d argument(s) (called in %s)", native, argc, x->fn);
#undef FG_REFL
        E("%s = %s;", S(first), call);
        Check();
        d = first + 1; SetR(first, true); return Next();
    }
    if (fcStartsWith(native, "__GRPHWin")) {
        FcDict* wn = BridgeOf(g, "windows", "__GRPHWin");
        FgPkgFn* winNative = (FgPkgFn*)fcDictGet(wn, native + 9);
        if (winNative && winNative->arguments == argc) {
            const char* name = native + 6;
            FcList* argl = fcListNew(g->c);
            const char* args;
            const char* owner = NULL;
            bool needsList = false;
            int k;
            UseWindows(g);
            if (streq0(name, "WinRegisterEvent") && g->target->supportsThreads && (streq0(g->target->platform, "posix") || streq0(g->target->platform, "windows"))) UseThreads(g);
            if (streq0(name, "WinMgrStart")) {
                if (!g->target->supportsThreads) { E("%s = Bool(false);", S(first)); d = first + 1; SetR(first, false); return Next(); }
                UseThreads(g);
            }
            for (k = first; k < first + argc; k++) fcListAdd(g->c, argl, (void*)SR(k));
            {
                FgPkgFn* w;
                FC_DICT_EACH(wn, e) { w = (FgPkgFn*)e->value; if (w->needsList && streq0(fcFmt(g->c, "Win%s", (const char*)e->key), name)) needsList = true; }
            }
            if (needsList) { owner = OwnerList(); fcListAdd(g->c, argl, (void*)fcFmt(g->c, "&%s", owner)); }
            args = fgJoin(g->c, argl, ", ");
            {
                const char* t = T(first);
                OwnerList();   /* (the C# asks `args.EndsWith("&" + OwnerList())`: the list of the temporaries is used) */
                E("%s = fire_win_%s(%s);", t, name, args);
            }
            CheckNative();
            d = first + 1; SetR(first, winNative->needsList); return Next();
        }
    }
    if (fcStartsWith(native, "__GRPH")) {
        FcDict* gn = BridgeOf(g, "graphics", "__GRPH");
        FgPkgFn* grphNative = (FgPkgFn*)fcDictGet(gn, native + 6);
        if (grphNative && grphNative->arguments == argc) {
            const char* name = native + 6;
            FcList* argl = fcListNew(g->c);
            int k;
            UseGraphics(g);
            for (k = first; k < first + argc; k++) fcListAdd(g->c, argl, (void*)SR(k));
            if (grphNative->needsList) fcListAdd(g->c, argl, (void*)fcFmt(g->c, "&%s", OwnerList()));
            {
                const char* args = fgJoin(g->c, argl, ", ");
                const char* t = T(first);
                E("%s = fire_gfx_%s(%s);", t, name, args);
            }
            CheckNative();
            d = first + 1; SetR(first, grphNative->needsList); return Next();
        }
    }
    if (fcDictHas(g->packageNatives, native) && ((FgPkgNative*)fcDictGet(g->packageNatives, native))->argc == argc) {
        /* a native of a package (C++ in the generated file): `Value cpp(Value a, ...)`, with the list of the scope as the last argument if it asks for it */
        FgPkgNative* pn = (FgPkgNative*)fcDictGet(g->packageNatives, native);
        const char* cFunction = pn->isC ? pn->function : "";   /* (SharedNative: one file) */
        bool typed = fgNativeIsTyped(pn->fn, pn->isC) && streq0(cFunction, pn->function), awayWrapped = false;
        FcList* argl = fcListNew(g->c);
        const char* args;
        const char* typedRet;
        const char* pkgEnter;
        const char* pkgLeave;
        int pkgLock, k;
        for (k = first; k < first + argc; k++) {
            const char* ct = typed ? fgNativeParamCType(pn->fn, k - first) : NULL;
            fcListAdd(g->c, argl, (void*)(ct ? xNativeArgOf(x, k, ct) : SR(k)));
        }
        if (pn->needsList) fcListAdd(g->c, argl, (void*)fcFmt(g->c, "&%s", OwnerList()));
        args = fgJoin(g->c, argl, ", ");
        if (pn->throws) UseExceptions(g);
        if (pn->host) NeedTimeObj(g);
        typedRet = typed && fgNativeReturnCType(pn->fn) ? fgNativeReturnCType(pn->fn) : "";
        pkgLock = PkgLock(g, native);
        pkgEnter = pkgLock >= 0 ? fcFmt(g->c, "{ FIRE_PKG_ENTER(fire_pkgM[%d]) ", pkgLock) : "";
        pkgLeave = pkgLock >= 0 ? fcFmt(g->c, " FIRE_PKG_LEAVE(fire_pkgM[%d]) }", pkgLock) : "";
        if (g->usesThreads && (!pn->host || pkgLock >= 0)) {
            FcList* awayL = fcListNew(g->c);
            FcList* natL = fcListNew(g->c);
            const char* freeIf;
            for (k = first; k < first + argc; k++) {
                if (typed && fgNativeParamCType(pn->fn, k - first)) continue;
                fcListAdd(g->c, awayL, (void*)fcFmt(g->c, "fire_awayArg(%s)", SR(k)));
            }
            for (k = first; k < first + argc; k++) {
                if (typed && fgNativeParamCType(pn->fn, k - first)) continue;
                fcListAdd(g->c, natL, (void*)fcFmt(g->c, "fire_natArg(%s)", SR(k)));
            }
            freeIf = pn->host ? "false" : awayL->count == 0 ? "true" : fcFmt(g->c, "g_isThread ? (%s) : (%s)", fgJoin(g->c, natL, " && "), fgJoin(g->c, awayL, " && "));
            pkgEnter = fcFmt(g->c, "{ int nt_ = fire_natBegin(%s); %s", freeIf, *pkgEnter ? pkgEnter : "{ ");
            pkgLeave = fcFmt(g->c, "%s fire_natEnd(nt_); }", *pkgLeave ? pkgLeave : " }");
            awayWrapped = true;
        }
        if (*typedRet && (!SlotScalars() || (streq0(typedRet, "int64_t") && g->program->intWidth == 32) || pkgLock >= 0 || awayWrapped)) {
            int vtype;
            {
                const char* t = T(first);
                E("%s%s = %s;%s", pkgEnter, t, fgNativeToValue(g->c, typedRet, fcFmt(g->c, "%s(%s)", pn->function, args)), pkgLeave);
            }
            CheckNative();
            if (pn->host && g->usesThreads && sb) E("if (FIRE_UNLIKELY(pollSignals())) goto %s;", ExitLabel(g, f, st, locals));
            d = first + 1; SetR(first, false);
            vtype = pn->returns && streq0(pn->returns, "float") ? TypeOf(KindFloat, 0) : pn->returns && streq0(pn->returns, "bool") ? TypeOf(KindBool, 0) : TypeOf(KindInt, 0);
            SetT(first, vtype); g->kindsKept = true;
            return Next();
        }
        if (*typedRet) {
            int rtype = streq0(typedRet, "Real") ? TypeOf(KindFloat, 0) : streq0(typedRet, "bool") ? TypeOf(KindBool, 0) : TypeOf(KindInt, 0);
            SetScalar(first, rtype, streq0(typedRet, "Real") || streq0(typedRet, "bool") ? fcFmt(g->c, "%s(%s)", pn->function, args) : fcFmt(g->c, "(FireInt)%s(%s)", pn->function, args));
            CheckNative();
            if (pn->host && g->usesThreads && sb) E("if (FIRE_UNLIKELY(pollSignals())) goto %s;", ExitLabel(g, f, st, locals));
            d = first + 1; SetR(first, false); g->kindsKept = true;
            return Next();
        }
        {
            const char* t = T(first);
            const char* callee = pn->isC ? cFunction : NativeThunk(g, pn->function, argc, pn->needsList);
            E("%s%s = %s(%s);%s", pkgEnter, t, callee, args, pkgLeave);
        }
        CheckNative();
        if (pn->host && g->usesThreads && sb) E("if (FIRE_UNLIKELY(pollSignals())) goto %s;", ExitLabel(g, f, st, locals));
        d = first + 1; SetR(first, pn->returnsReference);
        {
            int nativeType = !pn->returns ? TypeUnknown : streq0(pn->returns, "float") ? TypeOf(KindFloat, 0) : streq0(pn->returns, "bool") ? TypeOf(KindBool, 0) : streq0(pn->returns, "int") ? TypeOf(KindInt, 0) : TypeUnknown;
            if (nativeType != TypeUnknown && !pn->returnsReference) { SetT(first, nativeType); g->kindsKept = true; }
        }
        return Next();
    }
#undef FG_UNDEF_OR
    FG_NOTSUP("native function '%s' (called in %s)", native, x->fn);
}
