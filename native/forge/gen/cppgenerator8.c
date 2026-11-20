/* native/forge/gen/cppgenerator8.c - src/fire.Native/CppGenerator.cs (commit 808e9788), part 8: StepCore from MakeLambda on - lambdas, the guards of
 * the prologue, the type arguments, strings, arrays and buffers, ownership, pointers, exceptions and the fire threads; the end of the switch.
 * The probes and the selectors of lambdas with their reflection (gen/reflection.c). */

/* ArrayElem.OpenFits */
static bool fgAeOpenFits(int open, int declared) {
    int kind;
    declared = fgAeTypeOf(declared);
    if (open == AE_None) return true;
    kind = declared >= AE_I8 && declared <= AE_I64 ? AE_I64 : declared >= AE_F8 && declared <= AE_F64 ? AE_F64 : declared;
    return kind == open || (open == AE_I64 && kind == AE_F64);
}
/* ArrayElem.Bits */
static int fgAeBits(int type) { return type == AE_I8 || type == AE_F8 || type == AE_U8 ? 8 : type == AE_I16 || type == AE_F16 ? 16 : type == AE_I32 || type == AE_F32 ? 32 : 64; }
static FgStep fgStepEdges(bool fallsThrough, const FgEdges* edges) { FgStep r = fgStepEnd(edges); r.fallsThrough = fallsThrough; return r; }
static FcList* xItemsS(FgSC* x, int first, int count, bool refs) {
    FcList* items = fcListNew(g->c);
    int i;
    for (i = first; i < first + count; i++) fcListAdd(g->c, items, (void*)(refs ? SR(i) : S(i)));
    if (items->count == 0) fcListAdd(g->c, items, (void*)"Undef()");
    return items;
}
#define ITEMS(first, count, refs) xItemsS(x, (first), (count), (refs))

static FgStep xMakeLambda(FgSC* x) {
    bool capturing = ins->op == OP_MakeLambdaCapturing, hasOn = ins->a[1] != 0, anyDefault = false;
    int captures = capturing ? ins->a[2] : 0, first, i;
    FcProto* proto;
    FgFunc* target;
    FcBuf defaultsInit = {0};
    FcBuf caps = {0};
    const char* lambdaTArgs;
    Need(captures + (hasOn ? 1 : 0));
    proto = (FcProto*)x->chunk->functions->items[ins->a[0]];
    target = GetFunc(g, proto, FK_Lambda, captures);
    fcBufStr(&defaultsInit, "");
    fcBufStr(&caps, "");
    if (proto->selectorPath) {
        const char* key = fgJoin(g->c, proto->selectorPath, "\x01");
        FgSelectorPath* sp = (FgSelectorPath*)fcDictGet(g->selectorPaths, key);
        UseReflection(g);
        if (!sp) {
            sp = (FgSelectorPath*)fcAlloc(g->c, sizeof *sp);
            sp->name = fcFmt(g->c, "%skSel%d", Px, fcDictCount(g->selectorPaths));
            sp->path = proto->selectorPath;
            fcDictPut(g->selectorPaths, key, sp);
            g->version++;
        }
        fcBufFmt(&defaultsInit, " l->sel = %s; l->nsel = %d;", sp->name, proto->selectorPath->count);
    }
    for (i = 0; proto->paramDefaults && i < proto->paramDefaults->count; i++) if (proto->paramDefaults->items[i]) anyDefault = true;
    if (anyDefault) {
        int required = 0;
        for (i = 0; i < proto->paramDefaults->count; i++) if (proto->paramDefaults->items[i]) GetFunc(g, (FcProto*)proto->paramDefaults->items[i], FK_Init, 0);
        while (required < proto->paramCount && (required >= proto->paramDefaults->count || !proto->paramDefaults->items[required])) required++;
        fcBufFmt(&defaultsInit, " l->nreq = %d; l->dflt = %s_d;", required, target->name);
        fcSetAdd(g->lambdaDefaults, target);
    }
    first = d - captures - (hasOn ? 1 : 0);
    for (i = 0; i < captures; i++) { const char* s = S(first + i); fcBufFmt(&caps, " lamCaps(l)[%d] = %s; retain(%s);", i, s, s); }
    if (captures > 0) fcBufFmt(&caps, " lamOwnStore(l, &%s);", OwnerList());
    lambdaTArgs = g->pendingLambdaTArgs ? fcFmt(g->c, " l->targs = %s;", g->pendingLambdaTArgs) : "";
    g->pendingLambdaTArgs = NULL;
    {
        const char* owner = OwnerList();
        const char* on = hasOn ? S(d - 1) : "Undef()";
        const char* dI = fcBufDone(g->c, &defaultsInit);
        const char* cI = fcBufDone(g->c, &caps);
        E("{ Lam* l = allocLam(%d, %d, %s_t, &%s); l->on = %s;%s%s%s %s = LamV(l); }", proto->paramCount, captures, target->name, owner, on, dI, lambdaTArgs, cI, S(first));
    }
    d = first + 1; SetR(first, true); return Next();
}

static FgStep xCall(FgSC* x) {
    int argc = ins->a[0], slot, i;
    const char* owner;
    int64_t lamMask;
    FcList* argItems;
    Need(argc + 1);
    slot = d - argc - 1;
    owner = OwnerList();
    lamMask = ArgMask();
    for (i = 0; i < (argc < 16 ? argc : 16); i++) {
        int bits = (int)((lamMask >> (4 * i)) & 15);
        if (bits == 1 || bits == 2) {
            const char* s = S(slot + 1 + i);
            E("%s = copyValue(%s, %s, &%s);", s, s, bits == 2 ? "true" : "false", owner);
            Check();
        }
    }
    argItems = fcListNew(g->c);
    if (argc == 0) fcListAdd(g->c, argItems, (void*)"Undef()");
    for (i = slot + 1; i < slot + 1 + argc; i++) fcListAdd(g->c, argItems, (void*)S(i));
    {
        const char* decl = CArray(g, "Value", "args", argItems);
        const char* s = S(slot);
        E("{ %s%s = callLamU(%s, %d, args, &%s); }", decl, s, s, argc, owner);
    }
    Check();
    AdoptResult(slot, TypeUnknown);
    d = slot + 1; return Next();
}

static FgStep xGuardLocal(FgSC* x) {
    const char* v = Var(g, st, 0, ins->a[0], locals);
    int declared = ((((ins->a[1] & 0x7F) + 1) | (ins->a[1] & DK_UnitlessBit)) & 0xFF);
    bool cParam = f->cabi && ins->a[0] < f->cabi->paramCount && f->cabi->paramType[ins->a[0]] != 0;
    int kind;
    if (!cParam && fgDkIsString(declared)) {
        const char* guardExit = xCheckExit(x);
        const char* exitText = guardExit ? fcFmt(g->c, " if (FIRE_UNLIKELY(g_unwind.active)) goto %s;", guardExit) : "";
        E("if (FIRE_UNLIKELY(%s.kind != K_String && %s.kind != K_Undefined)) { Value o_ = %s; %s = guardStr(%s); retain(%s); release(o_);%s }", v, v, v, v, v, v, exitText);
    }
    else if (!cParam) ES(GuardStmt(g, v, declared, Str(ins->a[2]), xCheckExit(x)));
    kind = KindOfDeclared(declared);
    JoinVarType(g, VarKey(g, f, v), kind == KindBool || kind == KindWord ? DeclaredValueType(g, declared, NULL)
        : ins->a[3] == 0xFFFF ? TypeUnknown : DeclaredValueType(g, (declared & ~DK_UnitlessBit) & 0xFF, Str(ins->a[3])));
    g->kindsKept = true;
    return Next();
}

static FgStep xCheckUnit(FgSC* x) {
    const char* unitText;
    const char* onVariable;
    const char* inPlace;
    int cu;
    Need(1);
    unitText = Str(ins->a[0]);
    if (IsNum(K(d - 1)) && xTypedAdaptUnit(x, unitText)) return Next();
    if (((streq0(unitText, "unitless") && IsNum(BK(d - 1))) || ((streq0(unitText, "#unitless") || streq0(unitText, "#any")) && BK(d - 1) == KindFloat) || (streq0(unitText, "any") && IsNum(BK(d - 1))))
        && IsNullable(Ty(d - 1)) && (Ty(d - 1) & TypeNone) == 0 && (UnitOfType(Ty(d - 1)) == 0 || fcEndsWith(unitText, "any"))) { g->kindsKept = true; return Next(); }
    cu = f->indexOf[ins->addr] - 1;
    if (d - 1 < 64 && st->alias[d - 1] && cu + 2 < f->decoded.n && f->decoded.p[cu + 1].op == OP_StoreLocal && f->decoded.p[cu + 2].op == OP_Pop
        && !fgISetHas(g->c, f->targets, f->decoded.p[cu + 1].addr) && !fgISetHas(g->c, f->targets, f->decoded.p[cu + 2].addr)) {
        const char* checkedVar = st->alias[d - 1];
        const FgInstr* back = &f->decoded.p[cu + 1];
        if (streq0(Var(g, st, back->a[0], back->a[1], locals), checkedVar) && (onVariable = AdaptUnitStmt(g, checkedVar, unitText, xCheckExit(x))) != NULL) {
            FgEmit e = xEmitter(x);
            int k;
            for (k = 0; k < 64; k++) if (k != d - 1 && st->alias[k] && streq0(st->alias[k], checkedVar)) MaterializeAlias(g, st, k, &e);
            ES(onVariable);
            return Next();
        }
    }
    {
        const char* s = S(d - 1);
        if ((inPlace = AdaptUnitStmt(g, s, unitText, xCheckExit(x))) != NULL) { ES(inPlace); return Next(); }
        E("%s = %s;", s, AdaptUnitCall(g, S(d - 1), unitText));
    }
    Check();
    return Next();
}

static FgStep xNewArray(FgSC* x) {
    int at, n;
    const FgInstr* p;
    Need(1);
    at = f->indexOf[ins->addr];
    n = f->decoded.n;
    p = f->decoded.p;
    if (at + 1 < n && p[at].op == OP_TypeArray && (p[at].a[0] & AE_OpenFlag) != 0 && p[at].a[1] == 1 && p[at + 1].op == OP_TypeArray && p[at + 1].a[1] == 1
        && (p[at + 1].a[0] == AE_U8 || p[at + 1].a[0] == AE_Word || fgAeOpenFits(p[at].a[0] & ~AE_OpenFlag & 0xFF, p[at + 1].a[0] & 0xFF)))
        at++;
    else if (at < n && p[at].op == OP_TypeArray && (p[at].a[0] & AE_OpenFlag) != 0) at = n;
    if (at < n && p[at].op == OP_TypeArray && p[at].a[1] == 1 && p[at].a[0] == AE_U8) {
        const char* s = S(d - 1);
        E("%s = FIRE_UNLIKELY(%s.kind == K_Int && %s.i < 0) ? sizeError(%s.i, false) : newBuffer(%s, &%s);", s, s, s, s, s, OwnerList());
        Check();
        SetR(d - 1, true); SetT(d - 1, TypeOf(KindBuf, BufBytes)); g->kindsKept = true; return Next();
    }
    else if (at < n && p[at].op == OP_TypeArray && p[at].a[1] == 1 && p[at].a[0] == AE_Word) {
        const char* s = S(d - 1);
        E("%s = newWordBuffer(%s, &%s);", s, s, OwnerList());
        Check();
        SetR(d - 1, true); SetT(d - 1, TypeOf(KindBuf, BufWords)); g->kindsKept = true; return Next();
    }
    else if (at < n && p[at].op == OP_TypeArray && p[at].a[1] == 1 && fgAeTypeOf(p[at].a[0]) != AE_Class) {
        const FgInstr* typed = &p[at];
        const char* s = S(d - 1);
        const char* unitArgs = TypeArrayUnitArgs(g, x->chunk, typed->a[2], typed->a[0]);
        E("%s = newTypedArray(%s, %d, %s, &%s);", s, s, fgAeTypeOf(typed->a[0]), unitArgs, OwnerList());
        Check();
        SetR(d - 1, true);
        if (typed->a[2] == 0xFFFF) { SetT(d - 1, TypeOf(KindArr, typed->a[0])); g->kindsKept = true; }
        return Next();
    }
    else if (at < n && p[at].op == OP_TypeArrayOfParam) {
        const char* targs = TArgsExpr(g, f, Str(p[at].a[0]));
        const char* s = S(d - 1);
        const char* unitArgs = TypeArrayUnitArgs(g, x->chunk, 0xFFFF, 0);
        const char* owner = OwnerList();
        E("{ TArgs ta_ = %s; %s = ta_ && ta_[0] && ta_[0]->arrElem && !ta_[0]->arrClass ? newTypedArray(%s, (uint8_t)(ta_[0]->arrElem - 1), %s, &%s) : newArray(%s, &%s); }", targs, s, s, unitArgs, owner, s, owner);
    }
    else { const char* s = S(d - 1); E("%s = newArray(%s, &%s);", s, s, OwnerList()); }
    Check();
    SetR(d - 1, true); return Next();
}

static FgStep xTypeArray(FgSC* x) {
    if ((ins->a[0] & AE_OpenFlag) != 0) {
        Need(1);
        if (!(K(d - 1) == KindBuf || BK(d - 1) == KindArr)) E("arrMarkOpen(%s, %d, %d, 1);", S(d - 1), ins->a[0] & ~AE_OpenFlag, ins->a[1]);
        g->kindsKept = true; return Next();
    }
    if ((ins->a[0] == AE_U8 || ins->a[0] == AE_Word) && ins->a[1] == 1 && K(d - 1) == KindBuf && U(d - 1) == (ins->a[0] == AE_U8 ? BufBytes : BufWords)) { Need(1); g->kindsKept = true; return Next(); }
    if (ins->a[0] == AE_U8) {
        const char* s;
        Need(1); s = S(d - 1); E("%s = typeBuffer(%s, %d, &%s);", s, s, ins->a[1], OwnerList()); Check();
        if (ins->a[1] == 1) { SetT(d - 1, TypeOf(KindBuf, BufBytes) | NullableBit); g->kindsKept = true; }
        return Next();
    }
    if (ins->a[0] == AE_Word) {
        const char* s;
        Need(1); s = S(d - 1); E("%s = typeWordBuffer(%s, %d, &%s);", s, s, ins->a[1], OwnerList()); Check();
        if (ins->a[1] == 1) { SetT(d - 1, TypeOf(KindBuf, BufWords) | NullableBit); g->kindsKept = true; }
        return Next();
    }
    {
        bool known;
        int classArr = -1;
        Need(1);
        known = ins->a[1] == 1 && ins->a[2] == 0xFFFF;
        if (ins->a[1] == 1 && ins->a[2] != 0xFFFF && fgAeTypeOf(ins->a[0]) == AE_Class) classArr = ClassArrType(g, fgConstStr(x->chunk, ins->a[2]));
        if (known && BK(d - 1) == KindArr && U(d - 1) == ins->a[0] && (Ty(d - 1) & TypeNone) == 0) { g->kindsKept = true; return Next(); }
        if (classArr != -1 && !fgAeIsDefined(ins->a[0]) && (Ty(d - 1) | NullableBit) == classArr) { g->kindsKept = true; return Next(); }
        {
            const char* s = S(d - 1);
            E("typeArray(%s, %d, %d, %s, 1);", s, fgAeTypeOf(ins->a[0]), ins->a[1], TypeArrayUnitArgs(g, x->chunk, ins->a[2], ins->a[0]));
        }
        Check();
        if (known) { SetT(d - 1, TypeOf(KindArr, ins->a[0]) | NullableBit); g->kindsKept = true; }
        else if (classArr != -1) { SetT(d - 1, classArr); g->kindsKept = true; }
        return Next();
    }
}

static FgStep xMakeArrayLiteral(FgSC* x, uint32_t partsMask, bool parts) {
    int count = ins->a[0], first, i;
    FcBuf items = {0};
    FcBuf partText = {0};
    Need(count);
    first = d - count;
    fcBufStr(&items, ""); fcBufStr(&partText, "");
    for (i = 0; i < count; i++) { const char* s = S(first + i); fcBufFmt(&items, " arrItems(a)[%d] = %s; retain(%s);", i, s, s); }
    if (parts) for (i = 0; i < count; i++) if (((partsMask >> i) & 1) != 0) fcBufFmt(&partText, " attachPart(ArrV(a), arrItems(a)[%d]);", i);
    {
        const char* owner = OwnerList();
        const char* it = fcBufDone(g->c, &items);
        const char* pt = fcBufDone(g->c, &partText);
        const char* s = S(first);
        E("{ Arr* a = allocArr(%d, &%s);%s%s %s = ArrV(a); claimElements(%s, &%s); }", count, owner, it, pt, s, s, OwnerList());
    }
    d = first + 1; SetR(first, true); return Next();
}

/* ArrayGet / ArraySet on a buffer, a typed array or an object with typed specializations of its operator (the cases with `when` of the C#) */
static bool xArrayGetBuffer(FgSC* x, FgStep* r) {
    const char* isBuf;
    bool wordsGet;
    if (!(BK(d - 2) == KindBuf && (Ty(d - 2) & TypeNone) == 0 && (U(d - 2) == BufBytes || U(d - 2) == BufWords) && K(d - 1) == KindInt)) return false;
    isBuf = IsNullable(Ty(d - 2)) ? fcFmt(g->c, "%s.kind == K_Buffer && ", S(d - 2)) : "";
    Need(2);
    wordsGet = U(d - 2) == BufWords;
    if (SlotScalars() && (NarrowInt(d - 1) || K(d - 1) == KindInt) && U(d - 1) == 0) {
        const char* ab = S(d - 2);
        const char* ib = NarrowInt(d - 1) ? SN(d - 1) : fcFmt(g->c, "(FireInt)%s.i", S(d - 1));
        const char* elem = wordsGet ? fcFmt(g->c, "(FireWord)FIRE_BUF_LD(&bufWords(bufOf(%s))[%s])", ab, ib) : fcFmt(g->c, "(FireInt)FIRE_BUF_LD(&bufOf(%s)->data[%s])", ab, ib);
        int elemType = wordsGet ? TypeOf(KindWord, 0) : TypeOf(KindInt, 0) | NarrowBit;
        const char* idxValue = NarrowInt(d - 1) ? fcFmt(g->c, "Int(%s)", ib) : S(d - 1);
        const char* shadow;
        SetT(d - 2, elemType); SetR(d - 2, false); g->kindsKept = true;
        shadow = ScalarName(g, f, st, d - 2);
        E("if (FIRE_LIKELY(%sleafAlive(%s) && (uint64_t)(int64_t)%s < bufOf(%s)->length)) %s = %s; else { Value e_ = %saget_g(%s, %s, NULL); %s = %s;%s }", isBuf, ab, ib, ab, shadow, elem, Px, ab, idxValue, shadow, wordsGet ? "(FireWord)e_.w" : "(FireInt)e_.i", xCheckIn(x));
        st->scalar |= 1ULL << (d - 2);
        d--; *r = Next(); return true;
    }
    {
        const char* a = S(d - 2);
        const char* i = S(d - 1);
        const char* load = wordsGet ? fcFmt(g->c, "WordV((uint64_t)FIRE_BUF_LD(&bufWords(bufOf(%s))[%s.i]))", a, i) : fcFmt(g->c, "Int(FIRE_BUF_LD(&bufOf(%s)->data[%s.i]))", a, i);
        E("if (FIRE_LIKELY(%sleafAlive(%s) && (uint64_t)%s.i < bufOf(%s)->length)) %s = %s; else { %s = %saget_g(%s, %s, NULL);%s }", isBuf, a, i, a, a, load, a, Px, a, i, xCheckIn(x));
    }
    d--; SetR(d - 1, false); SetT(d - 1, TypeOf(wordsGet ? KindWord : KindInt, 0) | (wordsGet ? 0 : NarrowBit)); g->kindsKept = true; *r = Next(); return true;
}
static bool xArraySetBuffer(FgSC* x, FgStep* r) {
    const char* isSetBuf;
    bool wordsSet;
    int storedType;
    if (!(BK(d - 3) == KindBuf && (Ty(d - 3) & TypeNone) == 0 && (U(d - 3) == BufBytes || U(d - 3) == BufWords) && K(d - 2) == KindInt
        && ((K(d - 1) == KindWord && U(d - 3) == BufWords) || (K(d - 1) == KindInt && (U(d - 3) == BufBytes || U(d - 1) == 0))))) return false;
    isSetBuf = IsNullable(Ty(d - 3)) ? fcFmt(g->c, "%s.kind == K_Buffer && ", S(d - 3)) : "";
    Need(3);
    wordsSet = U(d - 3) == BufWords;
    {
        const char* a = S(d - 3);
        const char* i = S(d - 2);
        const char* v = S(d - 1);
        const char* bits = K(d - 1) == KindWord ? fcFmt(g->c, "%s.w", v) : fcFmt(g->c, "(uint64_t)(int64_t)%s.i", v);
        if (wordsSet)
            E("if (FIRE_LIKELY(%sleafAlive(%s) && (uint64_t)%s.i < bufOf(%s)->length)) { %s = WordV(%s); FIRE_BUF_ST(&bufWords(bufOf(%s))[%s.i], (FireWord)%s.w); } else %s = %saset_g(%s, %s, %s, NULL);", isSetBuf, a, i, a, v, bits, a, i, v, v, Px, a, i, v);
        else
            E("if (FIRE_LIKELY(%sleafAlive(%s) && (uint64_t)%s.i < bufOf(%s)->length)) FIRE_BUF_ST(&bufOf(%s)->data[%s.i], (uint8_t)(%s)); else %s = %saset_g(%s, %s, %s, NULL);", isSetBuf, a, i, a, a, i, bits, v, Px, a, i, v);
        Check();
        storedType = wordsSet ? TypeOf(KindWord, 0) : Ty(d - 1);
        E("%s = %s;", a, v);
    }
    d -= 2; SetR(d - 1, false); SetT(d - 1, storedType); g->kindsKept = true; *r = Next(); return true;
}
static bool xArrayGetTyped(FgSC* x, FgStep* r) {
    int getCode = ArrSlot(d - 2), getElem, getKind;
    const char* ka;
    if (!(getCode != 0 && ArrElemKind(fgAeTypeOf(getCode)) != KindUnknown && g->program->optimize == 0)) return false;
    Need(2);
    getElem = fgAeTypeOf(getCode);
    getKind = ArrElemKind(getElem);
    if (fgAeIsDefined(getCode) && SlotScalars() && NarrowInt(d - 1) && U(d - 1) == 0) {
        const char* fast;
        const char* slow;
        int elemType = getKind == KindFloat ? TypeOf(KindFloat, 0) : getKind == KindBool ? TypeOf(KindBool, 0) : TypeOf(KindInt, 0) | NarrowBit;
        slow = getKind == KindFloat ? "(Real)e_.f" : getKind == KindBool ? "(e_.i != 0)" : "(FireInt)e_.i";
        if ((ka = xKnownArray(x, d - 2, getElem)) != NULL) {
            const char* ki = SN(d - 1);
            const char* kLoad = fcFmt(g->c, "((%s*)%s_d)[%s]", ArrElemCType(getElem), ka, ki);
            const char* kShadow;
            fast = getKind == KindFloat ? fcFmt(g->c, "(Real)%s", kLoad) : getKind == KindBool ? fcFmt(g->c, "(%s != 0)", kLoad) : fcFmt(g->c, "(FireInt)%s", kLoad);
            st->alias[d - 2] = NULL;
            SetT(d - 2, elemType); SetR(d - 2, false); g->kindsKept = true;
            kShadow = ScalarName(g, f, st, d - 2);
            E("if (FIRE_LIKELY(%s_ok && (uint64_t)(int64_t)%s < %s_len)) %s = %s; else { Value e_ = %saget_g(%s, Int(%s), NULL); %s = %s;%s }", ka, ki, ka, kShadow, fast, Px, ka, ki, kShadow, slow, xCheckIn(x));
            st->scalar |= 1ULL << (d - 2);
            d--; *r = Next(); return true;
        }
        {
            const char* da = S(d - 2);
            const char* di = SN(d - 1);
            const char* dIsArr = IsNullable(Ty(d - 2)) ? fcFmt(g->c, "%s.kind == K_Array && ", da) : "";
            const char* load = fcFmt(g->c, "((%s*)ra_->typed->data)[%s]", ArrElemCType(getElem), di);
            const char* shadow;
            SetT(d - 2, elemType); SetR(d - 2, false); g->kindsKept = true;
            if (d - 2 < 64) st->alias[d - 2] = NULL;
            shadow = ScalarName(g, f, st, d - 2);
            fast = getKind == KindFloat ? fcFmt(g->c, "(Real)%s", load) : getKind == KindBool ? fcFmt(g->c, "(%s != 0)", load) : fcFmt(g->c, "(FireInt)%s", load);
            E("{ Arr* ra_ = NULL; if (FIRE_LIKELY(%sleafAlive(%s) && (uint64_t)(int64_t)%s < (ra_ = arrOf(%s))->length && ra_->typed && ra_->typed->packed)) %s = %s; else { Value e_ = %saget_g(%s, Int(%s), NULL); %s = %s;%s } }",
                dIsArr, da, di, da, shadow, fast, Px, da, di, shadow, slow, xCheckIn(x));
            st->scalar |= 1ULL << (d - 2);
            d--; *r = Next(); return true;
        }
    }
    if ((ka = xKnownArray(x, d - 2, getElem)) != NULL) {
        const char* kv = S(d - 1);
        const char* ks;
        st->alias[d - 2] = NULL;
        ks = SlotName(g, st->region, d - 2);
        E("if (FIRE_LIKELY(%s_ok && %s.kind == K_Int && (uint64_t)%s.i < %s_len)) %s = arrPackedAt(%s_a, (uint32_t)%s.i, %d); else { if (typeArray(%s, %d, 1, %s, 1)) %s = %saget_g(%s, %s, NULL);%s }",
            ka, kv, kv, ka, ks, ka, kv, getElem, ka, getElem, TypeArrayUnitArgsOf(g, NULL, getCode), ks, Px, ka, kv, xCheckIn(x));
        d--; SetR(d - 1, false); SetT(d - 1, TypeOf(getKind, 0) | (fgAeIsDefined(getCode) ? 0 : NullableBit) | (getKind == KindInt ? NarrowBit : 0)); g->kindsKept = true; *r = Next(); return true;
    }
    {
        const char* a = S(d - 2);
        const char* i = S(d - 1);
        E("{ Arr* ra_ = NULL; if (FIRE_LIKELY(%s.kind == K_Array && %s.kind == K_Int && leafAlive(%s) && (uint64_t)%s.i < (ra_ = arrOf(%s))->length && ra_->typed && ra_->typed->packed && ra_->typed->elem == %d)) %s = arrPackedAt(ra_, (uint32_t)%s.i, %d); else { if (typeArray(%s, %d, 1, %s, 1)) %s = %saget_g(%s, %s, NULL);%s } }",
            a, i, a, i, a, getElem, a, i, getElem, a, getElem, TypeArrayUnitArgsOf(g, NULL, getCode), a, Px, a, i, xCheckIn(x));
    }
    d--; SetR(d - 1, false); SetT(d - 1, TypeOf(getKind, 0) | (fgAeIsDefined(getCode) ? 0 : NullableBit) | (getKind == KindInt ? NarrowBit : 0)); g->kindsKept = true; *r = Next(); return true;
}
static bool xArraySetScalar(FgSC* x, FgStep* r) {
    int scalarCode, se, vk, storedKind;
    const char* a;
    const char* i;
    const char* isArr;
    const char* ct;
    const char* xv;
    const char* conv;
    const char* hintTest;
    const char* valueOf;
    const char* stored;
    if (g->usesThreads) return false;
    scalarCode = ArrSlot(d - 3);
    if (!(scalarCode != 0 && SlotScalars() && IsScalar(d - 1) && NarrowInt(d - 2) && U(d - 2) == 0 && xScalarElemFits(x, fgAeTypeOf(scalarCode), d - 1) && g->program->optimize == 0)) return false;
    Need(3);
    se = fgAeTypeOf(scalarCode);
    a = S(d - 3); i = SN(d - 2);
    isArr = IsNullable(Ty(d - 3)) ? fcFmt(g->c, "%s.kind == K_Array && ", a) : "";
    ct = ArrElemCType(se);
    vk = K(d - 1);
    xv = vk == KindFloat ? SF(d - 1) : vk == KindBool ? SB(d - 1) : SN(d - 1);
    switch (ArrElemKind(se)) {
        case KindBool: conv = fcFmt(g->c, "(uint8_t)(%s ? 1 : 0)", xv); break;
        case KindFloat: {
            const char* real = vk == KindFloat ? xv : fcFmt(g->c, "(Real)%s", xv);
            conv = se == AE_F16 || se == AE_F8 ? fcFmt(g->c, "(float)roundFloatToWidth((double)(%s), %d)", real, fgAeBits(se)) : fcFmt(g->c, "(%s)(%s)", ct, real);
            break;
        }
        default: conv = fcFmt(g->c, "(%s)(%s)", ct, xv); break;
    }
    hintTest = se == AE_I8 ? "cv_ != INT8_MIN" : se == AE_I16 ? "cv_ != INT16_MIN" : se == AE_I32 ? "cv_ != INT32_MIN" : se == AE_I64 ? "cv_ != FIRE_INT_UNDEF"
        : se == AE_F8 || se == AE_F16 || se == AE_F32 ? "!fire_f32IsHint(cv_)" : se == AE_F64 ? "!fire_realIsHint(cv_)" : "1";
    valueOf = vk == KindFloat ? fcFmt(g->c, "Float(%s)", xv) : vk == KindBool ? fcFmt(g->c, "Bool(%s)", xv) : fcFmt(g->c, "Int(%s)", xv);
    E("{ Arr* ra_ = NULL; %s cv_ = %s; if (FIRE_LIKELY(%sleafAlive(%s) && (uint64_t)(int64_t)%s < (ra_ = arrOf(%s))->length && ra_->typed && ra_->typed->packed && ra_->typed->elem == %d && !ra_->typed->real && %s)) ((%s*)ra_->typed->data)[%s] = cv_; else (void)%saset_g(%s, Int(%s), %s, NULL); }",
        ct, conv, isArr, a, i, a, se, hintTest, ct, i, Px, a, i, valueOf);
    Check();
    storedKind = ArrElemKind(se);
    stored = storedKind == KindFloat ? fcFmt(g->c, "(Real)(%s)", conv) : storedKind == KindBool ? fcFmt(g->c, "(%s)", xv) : fcFmt(g->c, "(FireInt)(%s)", conv);
    SetScalar(d - 3, storedKind == KindFloat ? TypeOf(KindFloat, 0) : storedKind == KindBool ? TypeOf(KindBool, 0) : TypeOf(KindInt, 0) | NarrowBit, stored);
    d -= 2; *r = Next(); return true;
}
static bool xArraySetTyped(FgSC* x, FgStep* r) {
    int setCode, setElem, ek, storedType;
    const char* a;
    const char* i;
    const char* v;
    const char* kindTest;
    const char* convert;
    const char* put;
    if (g->usesThreads) return false;
    setCode = ArrSlot(d - 3);
    if (!(setCode != 0 && ((ek = ArrElemKind(fgAeTypeOf(setCode))) == KindInt || ek == KindBool || ek == KindFloat) && BK(d - 1) == ek && U(d - 1) == 0 && g->program->optimize == 0)) return false;
    Need(3);
    setElem = fgAeTypeOf(setCode);
    a = S(d - 3); i = S(d - 2); v = S(d - 1);
    kindTest = IsNullable(Ty(d - 1)) ? fcFmt(g->c, "%s.kind == %s && ", v, BK(d - 1) == KindFloat ? "K_Float" : BK(d - 1) == KindBool ? "K_Bool" : "K_Int") : "";
    switch (ArrElemKind(setElem)) {
        case KindInt: convert = fcFmt(g->c, "%s.reserved = 0; %s = intToWidth(%s, %d, false); ", v, v, v, ArrElemBits(setElem)); break;
        case KindFloat: convert = setElem == AE_F64 ? "" : fcFmt(g->c, "%s.f = (Real)roundFloatToWidth((double)%s.f, %d); ", v, v, fgAeBits(setElem)); break;
        default: convert = ""; break;
    }
    if (setElem == AE_I64) put = fcFmt(g->c, "FireInt cv_ = (FireInt)%s.i; if (FIRE_LIKELY(cv_ != FIRE_INT_UNDEF && !ra_->typed->real)) ((FireInt*)ra_->typed->data)[%s.i] = cv_; else arrPackedSet(ra_, (uint32_t)%s.i, %s);", v, i, i, v);
    else if (setElem == AE_F64) put = fcFmt(g->c, "Real cv_ = %s.f; if (FIRE_LIKELY(!fire_realIsHint(cv_) && !ra_->typed->real)) ((Real*)ra_->typed->data)[%s.i] = cv_; else arrPackedSet(ra_, (uint32_t)%s.i, %s);", v, i, i, v);
    else put = fcFmt(g->c, "%sarrPackedSet(ra_, (uint32_t)%s.i, %s);", convert, i, v);
    E("{ Arr* ra_ = NULL; if (FIRE_LIKELY(%s%s.kind == K_Array && %s.kind == K_Int && leafAlive(%s) && (uint64_t)%s.i < (ra_ = arrOf(%s))->length && ra_->typed && ra_->typed->packed && ra_->typed->elem == %d)) { %s } else %s = %saset_g(%s, %s, %s, NULL); }",
        kindTest, a, i, a, i, a, setElem, put, v, Px, a, i, v);
    Check();
    storedType = Ty(d - 1);
    E("%s = %s;", a, v);
    d -= 2; SetR(d - 1, false); SetT(d - 1, storedType); g->kindsKept = true; *r = Next(); return true;
}
static const char* fgReceiverArgs(const FcChunk* chunk, int addr) {
    int i;
    if (!chunk->receiverOffsets) return NULL;
    for (i = 0; i < chunk->receiverOffsets->count; i++) if (FC_VALI(chunk->receiverOffsets->items[i]) == addr) return (const char*)chunk->receiverTexts->items[i];
    return NULL;
}
static bool xArrayGetSpec(FgSC* x, FgStep* r) {
    FcProto* getIndex;
    const char* grecv;
    const char* gidx;
    FcList* getCalls;
    FgFunc* generic;
    FcBuf arms = {0};
    int resultType, i, hi = -1;
    const char* receiverArgs;
    if (!(ObjSlot(d - 2) && (Ty(d - 2) & TypeNone) == 0 && !(g->usesReflection && fcDictHas(ReflectionMethodNames(g), "GetIndex"))
        && (getIndex = DirectMethod(g, ObjClass(g, Ty(d - 2)), "GetIndex", 1, f)) != NULL && getIndex->specializations && getIndex->paramCount == 1)) return false;
    Need(2);
    grecv = S(d - 2); gidx = S(d - 1);
    if (IsNullable(Ty(d - 2))) { E("if (FIRE_UNLIKELY(%s.kind != K_Class)) %s = %saget_g(%s, %s, NULL);", grecv, grecv, Px, grecv, gidx); Check(); }
    getCalls = HandSpecCalls(g, getIndex->specializations, grecv);
    generic = GetFunc(g, getIndex, FK_Method, 0);
    resultType = ReturnType(g, generic);
    fcBufStr(&arms, "");
    for (i = 0; i < getCalls->count; i++) {
        FgHandSpec* gc = (FgHandSpec*)getCalls->items[i];
        const char* idxTest = SpecArgTest(g, gc->spec, 0, gidx, Ty(d - 1));
        FgCAbi* gabi;
        FgCAbi* rabi;
        const char* call;
        if (SlotScalars() && (gabi = CAbiOf(g, gc->callee)) != NULL && xProvenCArgs(x, gabi, d - 1, 1)) {
            const char* inner = fcFmt(g->c, "%s(%s, %s)", CName(g, gc->callee), grecv, gabi->paramType[0] == 0 ? gidx : SN(d - 1));
            call = gabi->retType == 0 ? inner : ScalarValueOf(g, BaseKindOfType(gabi->retType), UnitOfType(gabi->retType), inner);
        }
        else call = fcFmt(g->c, "%s(%s, %s)", gc->callee->name, grecv, gidx);
        fcBufFmt(&arms, "(%s%s) ? %s : ", gc->condition, idxTest, call);
        rabi = CAbiOf(g, gc->callee);
        resultType = JoinTypes(resultType, rabi && rabi->retType != 0 ? rabi->retType : ReturnType(g, gc->callee));
    }
    if ((receiverArgs = fgReceiverArgs(f->chunk, ins->addr)) != NULL) {
        for (i = 0; i < getCalls->count; i++) {
            FgHandSpec* gc = (FgHandSpec*)getCalls->items[i];
            if (gc->callee->proto && gc->callee->proto->specArgs && SpecArgsFit(g, gc->callee->proto->specArgs, receiverArgs)) { hi = i; break; }
        }
    }
    if (hi >= 0) {
        FgHandSpec* hc = (FgHandSpec*)getCalls->items[hi];
        const char* idxTest = SpecArgTest(g, hc->spec, 0, gidx, Ty(d - 1));
        const char* what = fcFmt(g->c, "The result of '[]' of a %s<%s>", fcPlainName(g->c, getIndex->chunk->ownerClass ? getIndex->chunk->ownerClass->name : ""), fgReplace(g->c, receiverArgs, "|", ", "));
        FgCAbi* habi;
        int declared, specType;
        if (SlotScalars() && (habi = CAbiOf(g, hc->callee)) != NULL && habi->retType != 0 && xProvenCArgs(x, habi, d - 1, 1)) {
            const char* cidx = habi->paramType[0] == 0 ? gidx : SN(d - 1);
            const char* checkedGeneric;
            E("if (FIRE_UNLIKELY(!leafAlive(%s))) %s = destroyedError(%s);", grecv, grecv, grecv);
            Check();
            checkedGeneric = CNumberOf(g, habi->retType, fcFmt(g->c, "(%s)", GuardKindCall(g, fcFmt(g->c, "%s(%s, %s)", generic->name, grecv, gidx), CDeclared(habi->retType), what)));
            d--;
            SetScalar(d - 1, habi->retType, fcFmt(g->c, "(%s%s) ? %s(%s, %s) : %s", hc->condition, idxTest, CName(g, hc->callee), grecv, cidx, checkedGeneric));
            Check();
            *r = Next(); return true;
        }
        if ((declared = DeclaredReturn(g, hc->callee->proto)) != -1 && (specType = DeclaredResultType(declared)) != TypeUnknown) {
            const char* checkedGeneric = GuardKindCall(g, fcFmt(g->c, "%s(%s, %s)", generic->name, grecv, gidx), declared, what);
            FgCAbi* vabi;
            const char* specCall = SlotScalars() && (vabi = CAbiOf(g, hc->callee)) != NULL && vabi->retType == 0 && xProvenCArgs(x, vabi, d - 1, 1)
                ? fcFmt(g->c, "%s(%s, %s)", CName(g, hc->callee), grecv, vabi->paramType[0] == 0 ? gidx : SN(d - 1)) : fcFmt(g->c, "%s(%s, %s)", hc->callee->name, grecv, gidx);
            E("{ if (FIRE_UNLIKELY(!leafAlive(%s))) %s = destroyedError(%s); else %s = (%s%s) ? %s : %s; }", grecv, grecv, grecv, grecv, hc->condition, idxTest, specCall, checkedGeneric);
            Check();
            d--;
            AdoptResult(d - 1, specType);
            *r = Next(); return true;
        }
    }
    E("{ if (FIRE_UNLIKELY(!leafAlive(%s))) %s = destroyedError(%s); else %s = %s%s(%s, %s); }", grecv, grecv, grecv, grecv, fcBufDone(g->c, &arms), generic->name, grecv, gidx);
    Check();
    d--;
    AdoptResult(d - 1, resultType);
    *r = Next(); return true;
}
static bool xArraySetSpec(FgSC* x, FgStep* r) {
    FcProto* setIndex;
    const char* srecv;
    const char* sidx;
    const char* sval;
    FcList* setCalls;
    FcBuf sarms = {0};
    int i, k;
    if (!(ObjSlot(d - 3) && (Ty(d - 3) & TypeNone) == 0 && !(g->usesReflection && fcDictHas(ReflectionMethodNames(g), "SetIndex")) && IsPlainType(Ty(d - 1)) && (Ty(d - 1) & TypeNone) == 0
        && (setIndex = DirectMethod(g, ObjClass(g, Ty(d - 3)), "SetIndex", 2, f)) != NULL && setIndex->specializations && setIndex->paramCount == 2)) return false;
    Need(3);
    srecv = S(d - 3); sidx = S(d - 2); sval = S(d - 1);
    if (IsNullable(Ty(d - 3))) { E("if (FIRE_UNLIKELY(%s.kind != K_Class)) (void)%saset_g(%s, %s, %s, NULL);", srecv, Px, srecv, sidx, sval); Check(); }
    setCalls = HandSpecCalls(g, setIndex->specializations, srecv);
    fcBufStr(&sarms, "");
    for (i = 0; i < setCalls->count; i++) {
        FgHandSpec* sc = (FgHandSpec*)setCalls->items[i];
        FcBuf tests = {0};
        FgCAbi* sabi;
        const char* call;
        fcBufStr(&tests, "");
        for (k = 0; k < 2; k++) { const char* v = S(d - 2 + k); fcBufStr(&tests, SpecArgTest(g, sc->spec, k, v, Ty(d - 2 + k))); }
        if (SlotScalars() && (sabi = CAbiOf(g, sc->callee)) != NULL && xProvenCArgs(x, sabi, d - 2, 2)) {
            FcBuf b = {0};
            fcBufStr(&b, "");
            for (k = 0; k < 2; k++) {
                int pt = sabi->paramType[k];
                fcBufFmt(&b, ", %s", pt == 0 ? S(d - 2 + k) : KindOfType(pt) == KindFloat ? SF(d - 2 + k) : KindOfType(pt) == KindWord ? SW(d - 2 + k) : SN(d - 2 + k));
            }
            call = fcFmt(g->c, "%s(%s%s)", CName(g, sc->callee), srecv, fcBufDone(g->c, &b));
        }
        else call = fcFmt(g->c, "%s(%s, %s, %s)", sc->callee->name, srecv, sidx, sval);
        fcBufFmt(&sarms, "if (leafAlive(%s) && %s%s) (void)%s; else ", srecv, sc->condition, fcBufDone(g->c, &tests), call);
    }
    E("{ %s%s = %saset_g(%s, %s, %s, &%s); }", fcBufDone(g->c, &sarms), sval, Px, srecv, sidx, sval, OwnerList());
    Check();
    E("%s = %s;", srecv, sval);
    SetR(d - 3, R(d - 1)); SetT(d - 3, Ty(d - 1)); g->kindsKept = true;
    d -= 2; *r = Next(); return true;
}

static FgStep xRegisterHandler(FgSC* x) {
    int t = ins->a[0], i;
    FgRegion* region;
    FgEdges edges = {0};
    UseExceptions(g);
    region = GetRegion(g, f, t, st);
    if (g->usesTakeUpwards && region->catchCount > 0) { fgISetAdd(g->c, f->needsList, region->catchScopeId); fgISetAdd(g->c, f->needsList, st->scopes[st->scopeCount - 1].id); }
    if (region->catchCount > 0) E("H%d.fn = %s; H%d.ctx = FR;", t, CatchName(g, f, region), t);
    else E("H%d.fn = NULL; H%d.ctx = NULL;", t, t);
    E("H%d.info = &HI_%s_%d; pushHandler(&H%d);", t, x->fn, t, t);
    for (i = 0; i < region->catchCount; i++) {
        FgFlow* catchFlow = fgFlowClone(g, st);
        catchFlow->region = region;
        if (region->hasFinally) fgFlowPushHandler(g, catchFlow, region, true);
        fgFlowPushScope(g, catchFlow, region->catchScopeId, 1);
        fgEdgeAdd(g, &edges, region->catches[i].addr, catchFlow);
    }
    if (region->hasFinally) {
        FgFlow* finallyFlow = fgFlowClone(g, st);
        finallyFlow->depth = d + 2;
        fgEdgeAdd(g, &edges, region->finallyAddr, finallyFlow);
    }
    fgFlowPushHandler(g, st, region, false);
    st->depth = d;
    return fgStepEdges(true, &edges);
}

static FgStep xEndFinally(FgSC* x) {
    int fin;
    const char* payload;
    const char* kind;
    bool payloadRef;
    FgFlow* after;
    FgEdges edges = {0};
    void* jumps;
    Need(2);
    if (st->finCount == 0) FG_NOTSUP("EndFinally outside of a finally block at %d in %s", ins->addr, x->fn);
    fin = st->fins[st->finCount - 1];
    payload = S(d - 2); kind = S(d - 1);
    payloadRef = R(d - 2);
    after = fgFlowClone(g, st);
    after->finCount--;
    after->depth = d - 2;
    E("switch (%s.i) {", kind);
    ES("case 0: break;");
    E("case 1: throwValue(%s, false);", payload);
    if (sb && g->usesExceptions) E("    goto %s;", ExitLabel(g, f, after, locals));
    else ES("    break;");
    if (fgISetHas(g->c, f->finReturn, fin)) {
        FgEmit pe = xEmitterPrefix(x, "    ");
        ES("case 2: {");
        E("    Value r = %s; (void)r;", payload);
        ReturnSeq(g, f, after, payloadRef, false, sb ? &pe : NULL, locals, &edges);
        ES("}");
    }
    if (fgIMapTry(f->finJumps, fin, &jumps)) {
        int n, k;
        int* targets = fgISetItems(g->c, (FcDict*)jumps, true, &n);
        E("case 3: switch (%s.i) {", payload);
        for (k = 0; k < n; k++) {
            E("    case %d: goto L%d;", targets[k], targets[k]);
            fgEdgeAdd(g, &edges, targets[k], after);
        }
        ES("    default: break;");
        ES("    }");
        ES("    break;");
    }
    if (g->usesThreads) {
        ES("case 4:");
        ES("    startLeave();");
        if (sb) E("    goto %s;", ExitLabel(g, f, after, locals));
        else ES("    break;");
    }
    ES("default: break;");
    ES("}");
    st->finCount--;
    d -= 2;
    st->depth = d;
    return fgStepEdges(true, &edges);
}

static FgStep xFire(FgSC* x) {
    int takingCount = ins->a[2], withCount, handoverCount, copyCount, argc, first, i, w;
    bool dedicatedThread;
    FgFunc* body;
    FcBuf sbf = {0};
    UseThreads(g);
    withCount = (ins->a[3] & 1) == 0 ? 0 : ((ins->a[3] >> 2) > 1 ? ins->a[3] >> 2 : 1);
    dedicatedThread = (ins->a[3] & 2) != 0;
    handoverCount = ins->a[4];
    copyCount = takingCount - handoverCount;
    argc = takingCount + withCount;
    Need(argc);
    body = GetFunc(g, (FcProto*)x->chunk->functions->items[ins->a[0]], FK_FireBody, 0);
    body->globalSlotCount = ins->a[1];
    first = d - argc;
    if (takingCount > 0) {
        FcList* items = fcListNew(g->c);
        for (i = first; i < first + takingCount; i++) fcListAdd(g->c, items, (void*)S(i));
        fcBufFmt(&sbf, "{ %sif (", CArray(g, "Value", "tv", items));
        if (copyCount > 0) fcBufFmt(&sbf, "takingNoCopyFree(tv, %d)", copyCount);
        if (copyCount > 0 && handoverCount > 0) fcBufStr(&sbf, " && ");
        if (handoverCount > 0) fcBufFmt(&sbf, "handoverFree(tv + %d, %d)", copyCount, handoverCount);
        fcBufStr(&sbf, ") ");
    }
    fcBufFmt(&sbf, "{ OwnList* travel = newTravel(); Value av[%d]; av[0] = Undef(); ", argc > 1 ? argc : 1);
    for (i = 0; i < copyCount; i++) fcBufFmt(&sbf, "av[%d] = takeCopy(%s, travel); ", i, S(first + i));
    if (handoverCount > 0) fcBufFmt(&sbf, "handOverAll(tv + %d, av + %d, %d, travel); ", copyCount, copyCount, handoverCount);
    for (w = 0; w < withCount; w++) {
        int k = takingCount + w;
        const char* isChannel;
        fcBufFmt(&sbf, "av[%d] = %s; ", k, S(first + takingCount + w));
        if ((isChannel = ChannelLikeTest(g, fcFmt(g->c, "av[%d]", k))) != NULL)
            fcBufFmt(&sbf, "if (%s) { Value e_ = %s(av[%d]%s); Owned* eo_ = ownedOf(e_); if (eo_) { if (eo_->owner) ownUnlink(eo_); ownLink(travel, eo_); } av[%d] = e_; } ", isChannel, CallFn(g, "__End", 0, "call_"), k, DispatchTail(g, "__End", 0, "travel", "0xFFFFFFFFu"), k);
    }
    fcBufFmt(&sbf, "ensureDomain(); %s(%s, av, %d, travel); }", dedicatedThread ? "fireThreadDedicated" : "fireThread", body->name, argc);
    if (takingCount > 0) fcBufStr(&sbf, " }");
    ES(fcBufDone(g->c, &sbf));
    if (takingCount > 0) Check();
    d = first;
    return Next();
}

static FgStep xPostGlobal(FgSC* x) {
    int argc = ins->a[0] & 0x3F, first, jobWith, k;
    bool onTarget = (ins->a[0] & 0x80) != 0, awaitJob = (ins->a[0] & 0x40) != 0;
    const char* av;
    UseThreads(g);
    Need(argc + 1);
    first = d - argc - 1;
    av = CArray(g, "Value", "av", ITEMS(first, argc, true));
    if (awaitJob) {
        const char* onLam = SR(d - 1);
        const char* t = T(first);
        E("{ %s%s = adoptV(awaitOnJob(%s, av, %d), &%s); }", av, t, onLam, argc, OwnerList());
        Check();
        d = first + 1;
        SetR(first, false);
        SetT(first, TypeUnknown);
        return Next();
    }
    jobWith = ins->n > 1 ? ins->a[1] : 0;
    if (jobWith > 0) {
        FcBuf ends = {0};
        fcBufStr(&ends, "");
        if (ChannelLikeTest(g, "av[0]"))
            for (k = argc - jobWith; k < argc; k++) {
                const char* test = ChannelLikeTest(g, fcFmt(g->c, "av[%d]", k));
                const char* tail = DispatchTail(g, "__End", 0, fcFmt(g->c, "&%s", OwnerList()), "0xFFFFFFFFu");
                fcBufFmt(&ends, "if (%s) av[%d] = adoptV(%s(av[%d]%s), &%s); ", test, k, CallFn(g, "__End", 0, "call_"), k, tail, OwnerList());
            }
        {
            const char* en = fcBufDone(g->c, &ends);
            const char* s = S(d - 1);
            E("{ %s%spostJobWith(%s, av, %d, %s, %d, &%s); }", av, en, s, argc, onTarget ? "true" : "false", jobWith, OwnerList());
        }
    }
    else E("{ %spostJobEx(%s, av, %d, %s, NULL); }", av, S(d - 1), argc, onTarget ? "true" : "false");
    if (argc > 0) Check();
    d = first;
    return Next();
}

static FgStep StepCore4(FgSC* x) {
    FgStep r;
    switch (ins->op) {
        case OP_MakeLambda:
        case OP_MakeLambdaCapturing: return xMakeLambda(x);
        case OP_Call: return xCall(x);
        case OP_GuardLocal: return xGuardLocal(x);
        case OP_SetTypeArgs: {
            const char* typeArgs = TArgsExpr(g, f, Str(ins->a[0]));
            if (ins->a[1] == 1) E("g_callTArgs = %s;", typeArgs);
            else if (ins->a[1] == 3) E("g_callTArgs = %s; g_explicitTArgs = %d;", typeArgs, fgSplit(g->c, Str(ins->a[0]), '|')->count);
            else if (ins->a[1] == 2) g->pendingLambdaTArgs = typeArgs;
            else g->pendingNewTArgs = typeArgs;
            g->kindsKept = true;
            return Next();
        }
        case OP_OverloadHint: return Next();
        case OP_CancelPush: {
            int n, first;
            UseThreads(g);
            n = ins->a[0] & 0x7F;
            Need(n);
            first = d - n;
            E("{ %sfire_untilPush(lv, %d, %s); }", CArray(g, "Value", "lv", ITEMS(first, n, true)), n, (ins->a[0] & 0x80) != 0 ? "true" : "false");
            d = first;
            return Next();
        }
        case OP_CancelPop: ES("fire_untilPop();"); return Next();
        case OP_Dedicate: if (g->usesThreads) ES("fire_dedicateEnter();"); return Next();
        case OP_Undedicate: if (g->usesThreads) ES("fire_dedicateLeave();"); return Next();
        case OP_AwaitReply: {
            int k, receiver, i;
            const FgInstr* p = f->decoded.p;
            bool anyActor = false;
            if (!g->usesActors) return Next();
            k = f->indexOf[ins->addr];
            while (k < f->decoded.n && (p[k].op == OP_CopyArgs || p[k].op == OP_ArgNames || p[k].op == OP_OverloadHint || p[k].op == OP_SetTypeArgs)) k++;
            if (k >= f->decoded.n || p[k].op != OP_CallMethod) return Next();
            receiver = d - p[k].a[1] - 1;
            if (receiver < 0) return Next();
            if (ObjSlot(receiver)) {
                FcList* inst = InstancesOf(g, ObjClass(g, Ty(receiver)));
                for (i = 0; i < inst->count; i++) if (((FgClassInfo*)inst->items[i])->rc->isActor) anyActor = true;
                if (!anyActor) return Next();
            }
            E("g_awaitReplyTo = %s;", S(receiver));
            return Next();
        }
        case OP_ArgNames: return Next();
        case OP_TakeCallTypeArgs:
            if (f->kind == FK_Lambda) ES("TArgs ctargs = lamOf(lam)->targs; (void)ctargs;");
            else E("TArgs ctargs = %s; g_callTArgs = NULL; g_explicitTArgs = 0; (void)ctargs;", f->specTArgs ? TArgsExpr(g, f, f->specTArgs) : "((TArgs)g_callTArgs)");
            g->kindsKept = true;
            return Next();
        case OP_Trust:
            if (!DebugBuild && d >= 1 && d <= 64) {
                int tt = Ty(d - 1), tk = BaseKindOfType(tt);
                if (tk == KindInt && (tt & (CutBit | NarrowBit)) != 0) SetT(d - 1, (tt & ~(NullableBit | TypeNone | CutBit)) | NarrowBit);
                else if (tk == KindFloat || tk == KindBool || tk == KindWord) SetT(d - 1, tt & ~(NullableBit | TypeNone));
                g->kindsKept = true;
            }
            return Next();
        case OP_GuardTop: {
            int declared, kind;
            if (GuardTopProven(st, ins)) { SetR(d - 1, false); g->kindsKept = true; return Next(); }
            Need(1);
            declared = (ins->a[0] + 1) & 0xFF;
            {
                const char* s = S(d - 1);
                ES(GuardStmt(g, s, declared, Str(ins->a[1]), xCheckExit(x)));
            }
            kind = KindOfDeclared(declared);
            if (kind == KindInt || kind == KindFloat || kind == KindBool || kind == KindWord) SetR(d - 1, false);
            if (fgDkIsString(declared)) { SetR(d - 1, true); SetT(d - 1, TypeUnknown); g->kindsKept = true; return Next(); }
            if (fgDkIsNonNull(declared) && (kind == KindInt || kind == KindFloat) && KindOfType(Ty(d - 1)) == kind)
                SetT(d - 1, (Ty(d - 1) & ~(NullableBit | TypeNone | CutBit)) | (kind == KindInt && !fgDkKeepsWidth(declared) ? NarrowBit : 0));
            else if (fgDkIsNonNull(declared) && (kind == KindBool || kind == KindWord)) SetT(d - 1, TypeOf(kind, 0));
            else if (KindOfType(Ty(d - 1)) != kind) SetT(d - 1, kind == KindBool ? TypeOf(KindBool, 0) | NullableBit : TypeUnknown);
            g->kindsKept = true;
            return Next();
        }
        case OP_GuardTopType: {
            Need(1);
            {
                const char* s = S(d - 1);
                ES(ClassGuard(g, s, Str(ins->a[0]), Str(ins->a[1]), SelfObj(f), f->hasCallTArgs ? "ctargs" : NULL, ""));
            }
            Check();
            if (FG_SPEC(Str(ins->a[0]))->typeParamIndex >= 0) SetR(d - 1, true);
            g->kindsKept = true;
            return Next();
        }
        case OP_GuardLocalType: {
            const char* v = Var(g, st, 0, ins->a[0], locals);
            const char* spec = Str(ins->a[1]);
            const FcRtClass* guardedRc;
            int i, it;
            if (f->specParams) {
                for (i = 0; i < f->specParams->count; i++) {
                    FgSpecParam* p = (FgSpecParam*)f->specParams->items[i];
                    if (p->slot != ins->a[0]) continue;
                    if (p->kind != 0) { JoinVarType(g, VarKey(g, f, v), TypeOf(p->kind, p->unit)); g->kindsKept = true; return Next(); }
                    break;
                }
            }
            ES(ClassGuard(g, v, spec, Str(ins->a[2]), SelfObj(f), f->hasCallTArgs ? "ctargs" : NULL, FG_SPEC(spec)->typeParamIndex >= 0 ? fcFmt(g->c, " retain(%s); release(o_);", v) : ""));
            Check();
            guardedRc = DeclaredClass(g, spec);
            it = guardedRc ? ObjType(g, guardedRc) | (FG_SPEC(spec)->nonNull ? 0 : NullableBit) : InterfaceType(g, spec);
            JoinVarType(g, VarKey(g, f, v), it == -1 ? TypeUnknown : it);
            g->kindsKept = true;
            return Next();
        }
        case OP_CheckLambdaSignature: Need(1); E("checkLambda(%s, %d);", S(d - 1), ins->a[0]); Check(); return Next();
        case OP_CheckUnit: return xCheckUnit(x);
        case OP_FormatValue: {
            const char* spec;
            Need(1);
            spec = Str(ins->a[0]);
            if (*spec && !strchr("XDBFExdbfe", spec[0])) FG_NOTSUP("format specifier '%s' (supported: X, D, B, F, E)", spec);
            {
                const char* s = S(d - 1);
                const char* c = Constant(g, fgConst(x->chunk, ins->a[0]));
                E("%s = formatValue(%s, %s, &%s);", s, s, c, OwnerList());
            }
            Check();
            SetR(d - 1, true);
            return Next();
        }
        case OP_NewArray: return xNewArray(x);
        case OP_TypeArray: return xTypeArray(x);
        case OP_TookResult:
            fcDictPut(f->scalars, "took_", (void*)"bool");
            SetScalar(d, TypeOf(KindBool, 0), "took_");
            d++;
            return Next();
        case OP_TypeParamIsOf: {
            const char* targs = TArgsExpr(g, f, Str(ins->a[0]));
            const char* t = T(d);
            E("{ TArgs ta_ = %s; %s = Bool(ta_ && ta_[0] && ta_[0]->display && typeNameIn(ta_[0]->display, %s)); }", targs, t, TypeIsOfTable(g, Str(ins->a[1])));
            SetR(d, false);
            d++;
            return Next();
        }
        case OP_TypeArrayOfParam: {
            const char* targs;
            Need(1);
            targs = TArgsExpr(g, f, Str(ins->a[0]));
            E("{ TArgs ta_ = %s; if (ta_ && ta_[0] && ta_[0]->arrElem) typeArrayOfArg(%s, ta_[0]); }", targs, S(d - 1));
            Check();
            return Next();
        }
        case OP_MakeBuffer: {
            const char* s;
            Need(1); s = S(d - 1); E("%s = newBuffer(%s, &%s);", s, s, OwnerList()); Check(); SetR(d - 1, true); SetT(d - 1, TypeOf(KindBuf, BufBytes)); g->kindsKept = true; return Next();
        }
        case OP_MakeArrayLiteral: return xMakeArrayLiteral(x, 0, false);
        case OP_MakeArrayLiteralParts: return xMakeArrayLiteral(x, (uint32_t)ins->a[1] | ((uint32_t)ins->a[2] << 16), true);
        case OP_NewJagged: {
            int ranks = ins->a[0], first;
            Need(ranks);
            first = d - ranks;
            {
                const char* decl = CArray(g, "Value", "sz", ITEMS(first, ranks, false));
                E("{ %s%s = newJaggedV(sz, %d, &%s); }", decl, S(first), ranks, OwnerList());
            }
            Check();
            d = first + 1; SetR(first, true); return Next();
        }
        case OP_HoistValue: {
            const char* target;
            int i;
            Need(1);
            g->kindsKept = true;
            if (IsPlainType(Ty(d - 1))) return Next();
            target = ListName(g, st->scopes[0].id);
            if (!sb) {
                FgHoist* h = (FgHoist*)fcAlloc(g->c, sizeof *h);
                h->target = st->scopes[0].id;
                h->sourceCount = st->scopeCount - 1;
                h->sources = (int*)fcAlloc(g->c, sizeof(int) * (size_t)(h->sourceCount + 1));
                for (i = 1; i < st->scopeCount; i++) h->sources[i - 1] = st->scopes[i].id;
                fcListAdd(g->c, f->hoists, h);
            }
            for (i = 1; i < st->scopeCount; i++)
                if (fgISetHas(g->c, f->needsList, st->scopes[i].id)) E("hoistFrom(%s, &%s, &%s);", S(d - 1), ListName(g, st->scopes[i].id), target);
            return Next();
        }
        case OP_TakeToScope:
        case OP_TryTakeToScope: {
            int depth;
            const char* list;
            Need(1);
            depth = ins->a[0];
            if (depth == 0xFFFF) { if (!g->usesGlobalOwn) { g->usesGlobalOwn = true; g->version++; } list = "g_globalOwn"; }
            else {
                int at = st->scopeCount - 1 - depth > 0 ? st->scopeCount - 1 - depth : 0;
                fgISetAdd(g->c, f->needsList, st->scopes[at].id);
                list = fcFmt(g->c, "&%s", ListName(g, st->scopes[at].id));
            }
            if (ins->op == OP_TryTakeToScope) { const char* s = S(d - 1); ES(xTryTakeStmt(x, s, fcFmt(g->c, "takeToList(%s, %s);", S(d - 1), list))); }
            else { const char* s = S(d - 1); E("if (takeAlive(%s)) takeToList(%s, %s);", s, s, list); }
            if (depth == 0xFFFF && g->usesThreads) E("noteGlobalTake(%s);", S(d - 1));
            Check();
            return Next();
        }
        case OP_TakeCheck: return Next();
        case OP_ClaimToObject: {
            const char* a;
            const char* b;
            Need(2);
            a = S(d - 2); b = S(d - 1);
            E("claimToHolder(%s, %s, &%s); %s = %s;", a, b, OwnerList(), a, b);
            SetR(d - 2, R(d - 1)); SetT(d - 2, Ty(d - 1)); g->kindsKept = true;
            d--; return Next();
        }
        case OP_ClaimToArray: {
            const char* a;
            const char* b;
            Need(3);
            if (IsScalar(d - 1) || IsPlainType(Ty(d - 1))) { g->kindsKept = true; return Next(); }
            a = S(d - 3); b = S(d - 1);
            E("claimToHolder(%s, %s, &%s);", a, b, OwnerList());
            return Next();
        }
        case OP_TakeToObject: {
            const char* a;
            const char* b;
            Need(2);
            a = S(d - 2); b = S(d - 1);
            E("if (FIRE_UNLIKELY(%s.kind != K_Class)) takeHolderError(%s); else if (takeAlive(%s)) takeToHolder(%s, %s);", a, a, b, a, b);
            Check();
            return Next();
        }
        case OP_TryTakeToObject: {
            const char* a;
            const char* b;
            Need(2);
            a = S(d - 2); b = S(d - 1);
            E("if (FIRE_UNLIKELY(%s.kind != K_Class)) takeHolderError(%s); else { %s }", a, a, xTryTakeStmt(x, b, fcFmt(g->c, "takeToHolder(%s, %s);", a, b)));
            Check();
            return Next();
        }
        case OP_TakeToArray: {
            const char* a;
            const char* b;
            Need(3);
            b = S(d - 1); a = S(d - 3);
            E("if (takeAlive(%s)) takeToHolder(%s, %s);", b, a, b);
            Check();
            return Next();
        }
        case OP_TryTakeToArray: {
            const char* a;
            const char* b;
            Need(3);
            b = S(d - 1); a = S(d - 3);
            ES(xTryTakeStmt(x, b, fcFmt(g->c, "takeToHolder(%s, %s);", a, b)));
            Check();
            return Next();
        }
        case OP_OwnValue: {
            int i;
            Need(2);
            for (i = 0; i < st->scopeCount; i++) fgISetAdd(g->c, f->needsList, st->scopes[i].id);
            if (st->scopeCount == 0) { const char* a = S(d - 2); E("%s = ownValueN(%s, %s, NULL, 0);", a, a, S(d - 1)); }
            else {
                FcList* items = fcListNew(g->c);
                const char* decl;
                const char* a;
                for (i = 0; i < st->scopeCount; i++) fcListAdd(g->c, items, (void*)fcFmt(g->c, "&%s", ListName(g, st->scopes[i].id)));
                decl = CArray(g, "OwnList*", "VL_", items);
                a = S(d - 2);
                E("{ %s%s = ownValueN(%s, %s, VL_, %d); }", decl, a, a, S(d - 1), st->scopeCount);
            }
            SetR(d - 2, true);
            d--; return Next();
        }
        case OP_Delete:
            Need(1);
            E("deleteValue(%s);", S(d - 1));
            Check();
            d--; return Next();
        case OP_ReplaceOld: {
            bool replaceStatic = ins->a[0] == 1;
            int oldAt;
            const char* oldSlot;
            const char* notNow;
            const char* owned;
            Need(replaceStatic ? 2 : 3);
            oldAt = d - (replaceStatic ? 2 : 3);
            if (SlotScalars() && IsScalar(oldAt)) { d = oldAt; return Next(); }
            oldSlot = S(oldAt);
            notNow = SlotScalars() && IsScalar(d - 1) ? "" : fcFmt(g->c, " && !sameRef(%s, %s)", oldSlot, S(d - 1));
            if (replaceStatic) { if (!g->usesGlobalOwn) { g->usesGlobalOwn = true; g->version++; } owned = fcFmt(g->c, "isFromGlobal(%s)", oldSlot); }
            else { const char* h = S(d - 2); owned = fcFmt(g->c, "%s.kind == K_Class && isFrom(%s, %s, false)", h, oldSlot, h); }
            E("if (%s%s) { deleteValue(%s); }", owned, notNow, oldSlot);
            Check();
            d -= replaceStatic ? 2 : 3; return Next();
        }
        case OP_ArrayGet: {
            int elemType;
            if (xArrayGetBuffer(x, &r) || xArrayGetTyped(x, &r) || xArrayGetSpec(x, &r)) return r;
            Need(2);
            elemType = ClassArrElemType(Ty(d - 2));
            {
                const char* a = S(d - 2);
                const char* i = S(d - 1);
                E("%s = %saget_g(%s, %s, &%s);", a, Px, a, i, OwnerList());
            }
            Check(); d--; SetR(d - 1, true);
            if (elemType != -1) { SetT(d - 1, elemType); g->kindsKept = true; }
            return Next();
        }
        case OP_ArraySet:
            if (xArraySetBuffer(x, &r) || xArraySetScalar(x, &r) || xArraySetTyped(x, &r) || xArraySetSpec(x, &r)) return r;
            Need(3);
            {
                const char* v = S(d - 1);
                const char* a = S(d - 3);
                const char* i = S(d - 2);
                E("%s = %saset_g(%s, %s, %s, &%s);", v, Px, a, i, v, OwnerList());
            }
            Check(); E("%s = %s;", S(d - 3), S(d - 1)); SetR(d - 3, R(d - 1)); d -= 2; return Next();
        case OP_IncDecIndex: {
            bool increment = ins->a[0] != 0, prefix = ins->a[1] != 0;
            const char* list;
            const char* guard;
            Need(2);
            list = fcFmt(g->c, "&%s", OwnerList());
            guard = g->usesExceptions ? fcFmt(g->c, "if (FIRE_UNLIKELY(g_unwind.active)) goto %s; ", !sb ? "X" : ExitLabel(g, f, st, locals)) : "";
            {
                const char* a = S(d - 2);
                const char* i = S(d - 1);
                E("{ Value o = %saget_g(%s, %s, %s); %sValue n = %s(o, stepOf(o, Int(1))); n = %saset_g(%s, %s, n, %s); %s = %s; }", Px, a, i, list, guard, increment ? "add" : "sub", Px, a, i, list, a, prefix ? "n" : "o");
            }
            Check();
            d--; SetR(d - 1, false); return Next();
        }
        case OP_Probe: {
            const char* member;
            Need(2); UseProbes(g);
            NoteProbeTarget(g, ObjSlot(d - 2) ? ObjClass(g, Ty(d - 2)) : NULL, (ins->a[1] & 2) != 0 ? NULL : Str(ins->a[0]));
            member = (ins->a[1] & 2) != 0 ? "NULL" : CString(g, Str(ins->a[0]));
            {
                const char* target = S(d - 2);
                const char* handler = S(d - 1);
                const char* store = S(d - 2);
                E("{ char err[500]; int64_t id = rf_probeAdd(%s, %s, %s, %s, err, sizeof err); if (id == -2) { } else if (id < 0) { fireError(strstr(err, \"has no member\") ? \"MissingMemberException\" : \"ArgumentException\", err); } else %s = Int(id); }",
                    target, member, (ins->a[1] & 1) != 0 ? "true" : "false", handler, store);
            }
            Check();
            d--; SetR(d - 1, false); return Next();
        }
        case OP_SilenceMember: {
            const char* member;
            Need(1); UseProbes(g);
            member = ins->a[1] != 0 ? "NULL" : CString(g, Str(ins->a[0]));
            E("{ char err[500]; if (!rf_silenceCore(%s, %s, err, sizeof err)) fireError(\"ArgumentException\", err); }", S(d - 1), member);
            Check();
            d--; return Next();
        }
        case OP_SilenceValue:
            Need(1); UseProbes(g);
            E("{ char err[500]; if (!rf_silenceValue(%s, err, sizeof err)) fireError(\"ArgumentException\", err); }", S(d - 1));
            Check();
            d--; return Next();
        case OP_CopyValue: {
            const char* s;
            Need(1);
            s = S(d - 1);
            E("%s = copyValue(%s, %s, &%s);", s, s, (ins->a[0] & 1) != 0 ? "true" : "false", OwnerList());
            Check();
            return Next();
        }
        case OP_CopyValueOwned: {
            const char* a;
            Need(2);
            a = S(d - 2);
            E("%s = copyOwned(%s, %s, %s);", a, a, S(d - 1), (ins->a[0] & 1) != 0 ? "true" : "false");
            Check();
            d--; SetR(d - 1, R(d)); return Next();
        }
        case OP_CopyArgs: g->kindsKept = true; return Next();
        case OP_AddressOfLocal: {
            const char* v = Var(g, st, ins->a[0], ins->a[1], locals);
            MarkVarRef(g, VarKey(g, f, v));
            JoinVarType(g, VarKey(g, f, v), TypeUnknown);
            E("%s = PtrV(&%s);", S(d), v);
            SetR(d, false); d++; return Next();
        }
        case OP_AddressOfGlobal: {
            const char* gv = GlobalVar(g, ins->a[0], locals);
            MarkVarRef(g, VarKey(g, f, gv));
            JoinVarType(g, VarKey(g, f, gv), TypeUnknown);
            E("%s = PtrV(&%s);", S(d), gv);
            SetR(d, false); d++; return Next();
        }
        case OP_AddressOfField: {
            const char* field;
            const char* s;
            Need(1);
            field = Str(ins->a[0]);
            fcSetAdd(g->fieldNames, field);
            if (fcSetAdd(g->fieldAddressTaken, field)) g->version++;
            s = S(d - 1);
            E("%s = %s(%s);", s, FieldFn(g, "fp", field), s);
            SetR(d - 1, false);
            return Next();
        }
        case OP_AddressOfIndex: {
            const char* a;
            if (!g->usesElementPointers) { g->usesElementPointers = true; g->version++; }
            Need(2); a = S(d - 2); E("%s = addressOfIndex(%s, %s);", a, a, S(d - 1)); Check(); d--; SetR(d - 1, false); return Next();
        }
        case OP_PtrRead: { const char* s; Need(1); s = S(d - 1); E("%s = ptrRead(%s);", s, s); Check(); SetR(d - 1, true); return Next(); }
        case OP_PtrWrite: {
            const char* a;
            const char* b;
            Need(2);
            a = S(d - 2); b = S(d - 1);
            E("ptrWrite(%s, %s);", a, b);
            Check();
            E("%s = %s;", S(d - 2), S(d - 1));
            SetR(d - 2, R(d - 1));
            d--; return Next();
        }
        case OP_RequireRefParam: {
            const char* v = Var(g, st, 0, ins->a[0], locals);
            E("requireRef(%s, %s);", v, CString(g, fgConstStr(x->chunk, ins->a[1])));
            return Next();
        }
        case OP_RegisterHandler: return xRegisterHandler(x);
        case OP_UnregisterHandler:
            if (st->handlerCount == 0) FG_NOTSUP("UnregisterHandler without handler at %d in %s", ins->addr, x->fn);
            st->handlerCount--;
            ES("popHandler();");
            return Next();
        case OP_Throw: {
            const char* s;
            UseExceptions(g); Need(1);
            s = S(d - 1);
            E("%s = throwValue(%s);", s, s);
            Check();
            E("adopt(%s, &%s);", S(d - 1), OwnerList());
            SetR(d - 1, true);
            return Next();
        }
        case OP_ResumeException:
            UseExceptions(g); Need(2);
            { const char* a = S(d - 2); E("resumeThrow(%s, %s);", a, S(d - 1)); }
            if (sb) E("if (FIRE_LIKELY(g_unwind.active)) goto %s;", ExitLabel(g, f, st, locals));
            E("%s = Undef();", S(d - 2));
            d--; SetR(d - 1, false); return Next();
        case OP_ClearPendingResume: Need(1); E("clearPending(%s);", S(d - 1)); d--; return Next();
        case OP_EnterFinallyNormal: {
            const char* a = S(d);
            E("%s = Undef(); %s = Int(0);", a, S(d + 1));
            SetR(d, false); SetR(d + 1, false);
            d += 2; return Next();
        }
        case OP_PushJump: {
            const FgInstr* next = &f->decoded.p[f->indexOf[ins->addr]];
            void* jumps;
            if (next->op != OP_Jump) FG_NOTSUP("PushJump without Jump at %d in %s", ins->addr, x->fn);
            if (!fgIMapTry(f->finJumps, next->a[0], &jumps)) { jumps = fcDictNew(g->c, FC_KEY_STR); fgIMapPut(g->c, f->finJumps, next->a[0], jumps); }
            fgISetAdd(g->c, (FcDict*)jumps, ins->a[0]);
            {
                const char* a = S(d);
                E("%s = Int(%d); %s = Int(3);", a, ins->a[0], S(d + 1));
            }
            SetR(d, false); SetR(d + 1, false);
            d += 2; return Next();
        }
        case OP_EndFinally: return xEndFinally(x);
        case OP_Fire: return xFire(x);
        case OP_Sync: {
            const char* s;
            UseThreads(g); Need(1);
            s = S(d - 1);
            E("%s = syncValue(%s, %s);", s, s, (ins->a[0] & 2) != 0 ? "true" : "false");
            Check();
            SetR(d - 1, false);
            return Next();
        }
        case OP_Process:
            UseThreads(g); Need(1);
            E("processMsg(%s, true, &%s);", S(d - 1), OwnerList());
            Check();
            d--; return Next();
        case OP_TryProcess: {
            const char* s;
            UseThreads(g); Need(1);
            s = S(d - 1);
            E("%s = Bool(processMsg(%s, false, &%s) > 0);", s, s, OwnerList());
            Check();
            SetR(d - 1, false); return Next();
        }
        case OP_Leave:
            UseThreads(g);
            ES("leaveNow();");
            if (sb) E("goto %s;", ExitLabel(g, f, st, locals));
            return fgStepEnd(NULL);
        case OP_Terminate:
            UseThreads(g); Need(1);
            E("terminateNow(%s);", S(d - 1));
            if (sb) E("goto %s;", ExitLabel(g, f, st, locals));
            d--; st->depth = d;
            return fgStepEnd(NULL);
        case OP_RegisterThreadsCatch:
        case OP_RegisterTerminateCatch: {
            FgFunc* handler;
            const char* lambda;
            int paramCount;
            UseThreads(g);
            handler = GetFunc(g, (FcProto*)x->chunk->functions->items[ins->a[0]], FK_Static, 0);
            lambda = fcFmt(g->c, "%scb_%s", Px, handler->name);
            paramCount = handler->proto ? handler->proto->paramCount : 0;
            fcDictPut(g->catchCallbacks, lambda, (void*)(paramCount >= 1 ? fcFmt(g->c, "return %s(e);", handler->name) : fcFmt(g->c, "(void)e; return %s();", handler->name)));
            if (ins->op == OP_RegisterThreadsCatch) E("registerThreadsCatch(%s, %s);", ins->a[1] == 0 ? "-1" : LibraryRole ? fcFmt(g->c, "fire_exct_%s[%d]", g->self->id, ExcTypeId(g, Str(ins->a[2]))) : fcFmt(g->c, "%d", ExcTypeId(g, Str(ins->a[2]))), lambda);
            else E("registerTerminateCatch(%s);", lambda);
            return Next();
        }
        case OP_SyncGlobals:
            UseThreads(g);
            E("%s = Int(syncGlobals());", S(d));
            Check();
            SetR(d, false); d++; return Next();
        case OP_SectionEnter: UseThreads(g); ES("sectionEnterOp();"); return Next();
        case OP_SectionExit: UseThreads(g); ES("sectionExitOp();"); return Next();
        case OP_PostGlobal: return xPostGlobal(x);
        default:
            FG_NOTSUP("opcode %s (in %s at %d)", fgOpName(ins->op), x->fn, ins->addr);
    }
}
#undef ITEMS

#undef g
#undef f
#undef ins
#undef st
#undef sb
#undef locals
#undef d
#undef E
#undef ES
#undef S
#undef SR
#undef T
#undef VR
#undef R
#undef K
#undef U
#undef BK
