/* native/forge/gen/cppgenerator6.c - src/fire.Native/CppGenerator.cs (commit 808e9788), part 6: StepCore - the local functions of the arithmetic, the
 * comparisons and the conditional jumps (Binary, Compare, TypedWord, TypedArith, TypedCondition, ...: the end of the C# method), and the instructions
 * of the arithmetic, the units, the conversions, the comparisons, the jumps and the scopes. Every value a C# interpolation reads is read in its order
 * (a slot that is read first is made a Value first; a string or unit is registered first). */

/* ---- the local functions at the end of StepCore ---- */
/* An operator overload runs only for an object on the left */
static bool xLeftMayOverload(FgSC* x, const char* opMethod, int k) {
    int type = Ty(k), baseKind, i;
    FcList* inst;
    if (type == TypeUndefined) return false;
    if ((type & TypeNone) != 0 || type == TypeUnknown) return true;
    baseKind = BaseKindOfType(type);
    if (baseKind == KindInt || baseKind == KindFloat || baseKind == KindBool || baseKind == KindWord || baseKind == KindBuf || baseKind == KindArr) return false;
    if (baseKind != KindObj || InterfaceOfType(g, type)) return true;
    inst = InstancesOf(g, ObjClass(g, type));
    for (i = 0; i < inst->count; i++) if (FG_FIND(((FgClassInfo*)inst->items[i])->rc, opMethod, 1).proto) return true;
    return false;
}
static FgStep xBinary(FgSC* x, const char* fnName, const char* opMethod) {
    const char* ba;
    const char* bb;
    Need(2);
    if (xLeftMayOverload(x, opMethod, d - 2) && UseOperator(g, opMethod)) {
        const char* oa = SR(d - 2);
        const char* ob = SR(d - 1);
        const char* t = T(d - 2);
        E("%s = %s(%s, %s, &%s);", t, OperatorWrapper(g, opMethod), oa, ob, OwnerList());
        Check(); d--; SetR(d - 1, true); return Next();
    }
    ba = SR(d - 2); bb = SR(d - 1);
    { const char* t = T(d - 2); E("%s = %s(%s, %s);", t, fnName, ba, bb); }
    xCheckIf(x, fcFmt(g->c, "%s.kind == K_Undefined", S(d - 2))); d--; SetR(d - 1, false); return Next();
}
static FgStep xUnary(FgSC* x, const char* fnName) {
    const char* s;
    Need(1);
    s = S(d - 1);
    E("%s = %s(%s);", s, fnName, s); xCheckIf(x, fcFmt(g->c, "%s.kind == K_Undefined", S(d - 1))); SetR(d - 1, false); return Next();
}
static FgStep xCompare(FgSC* x, const char* fnName, bool negate, const char* opMethod) {
    const char* ca;
    const char* cb;
    Need(2);
    if (xLeftMayOverload(x, opMethod, d - 2) && UseOperator(g, opMethod)) {
        bool boolResult = true;
        int i;
        {
            const char* oa = SR(d - 2);
            const char* ob = SR(d - 1);
            const char* t = T(d - 2);
            E("%s = %s(%s, %s, &%s);", t, OperatorWrapper(g, opMethod), oa, ob, OwnerList());
        }
        for (i = 0; i < g->classList->count; i++) {
            FcProto* p = FG_FIND(((FgClassInfo*)g->classList->items[i])->rc, opMethod, 1).proto;
            int rt;
            if (!p) continue;
            rt = ReturnType(g, GetFunc(g, p, FK_Method, 0));
            if (!(rt == TypeNone || BaseKindOfType(rt) == KindBool)) { boolResult = false; break; }
        }
        Check(); d--; SetR(d - 1, !boolResult); return Next();
    }
    ca = SR(d - 2); cb = SR(d - 1);
    { const char* t = T(d - 2); E("%s = Bool(%s%s(%s, %s));", t, negate ? "!" : "", fnName, ca, cb); }
    xCheckIf(x, negate ? "true" : fcFmt(g->c, "%s.i == 0", S(d - 2))); d--; SetR(d - 1, false); return Next();
}
static FgStep xJumpIfNot(FgSC* x, const char* fnName, bool negate, const char* opMethod) {
    Need(2);
    if (xLeftMayOverload(x, opMethod, d - 2) && UseOperator(g, opMethod)) {
        const char* a = S(d - 2);
        const char* b = S(d - 1);
        E("{ Value t = %s(%s, %s, &%s);", OperatorWrapper(g, opMethod), a, b, OwnerList());
        if (g->usesExceptions && sb) E("  if (FIRE_UNLIKELY(g_unwind.active)) goto %s;", ExitLabel(g, f, st, locals));
        E("  if (!truthy(t)) goto L%d; }", ins->a[0]);
        d -= 2; st->depth = d;
        return fgStepJump(ins->a[0], true);
    }
    {
        const char* a = S(d - 2);
        const char* b = S(d - 1);
        if (g->usesExceptions && sb && !negate)
            E("if (!(%s(%s, %s))) { if (FIRE_UNLIKELY(g_unwind.active)) goto %s; goto L%d; }", fnName, a, b, ExitLabel(g, f, st, locals), ins->a[0]);
        else E("if (!(%s%s(%s, %s))) goto L%d;", negate ? "!" : "", fnName, a, b, ins->a[0]);
    }
    d -= 2; st->depth = d;
    return fgStepJump(ins->a[0], true);
}
/* A word operand of known kind as the C expression of its 64 bits; NULL otherwise. */
static const char* xWordBits(FgSC* x, int k) {
    if (K(k) == KindWord) return fcFmt(g->c, "(uint64_t)%s.w", S(k));
    if (K(k) == KindInt && U(k) == 0) return fcFmt(g->c, "((uint64_t)(int64_t)%s.i & FIRE_WORD_MASK)", S(k));
    return NULL;
}
static bool xTypedWord(FgSC* x, const char* op) {
    const char* wx;
    const char* wy;
    const char* a;
    const char* b;
    if (K(d - 2) != KindWord && K(d - 1) != KindWord) return false;
    if (SlotScalars() && xWordOperand(x, d - 2) && xWordOperand(x, d - 1) && !(streq0(op, "/") || streq0(op, "%"))) {
        const char* expr;
        wx = SW(d - 2); wy = SW(d - 1);
        expr = streq0(op, "<<") || streq0(op, ">>") ? fcFmt(g->c, "(%s >= FIRE_WORD_BITS ? (FireWord)0 : (FireWord)(%s %s %s))", wy, wx, op, wy) : fcFmt(g->c, "(FireWord)(%s %s %s)", wx, op, wy);
        SetScalar(d - 2, TypeOf(KindWord, 0), expr);
        d--;
        return true;
    }
    wx = xWordBits(x, d - 2);
    wy = wx ? xWordBits(x, d - 1) : NULL;
    if (!wx || !wy) return false;
    a = S(d - 2); b = S(d - 1);
    if (streq0(op, "/") || streq0(op, "%")) {
        E("%s = FIRE_LIKELY(%s != 0) ? WordV(%s %s %s) : %s(%s, %s);", a, wy, wx, op, wy, streq0(op, "/") ? "divide" : "modulo", a, b);
        xCheckIf(x, fcFmt(g->c, "%s.kind == K_Undefined", a));
    }
    else if (streq0(op, "<<") || streq0(op, ">>")) E("%s = WordV(%s >= FIRE_WORD_BITS ? 0 : %s %s %s);", a, wy, wx, op, wy);
    else E("%s = WordV(%s %s %s);", a, wx, op, wy);
    d--;
    SetR(d - 1, false); SetT(d - 1, TypeOf(KindWord, 0)); g->kindsKept = true;
    return true;
}
static bool xTypedIntBits(FgSC* x, const char* op) {
    const char* a;
    const char* b;
    const char* unitC;
    int unit;
    if (K(d - 2) != KindInt || K(d - 1) != KindInt) return false;
    if (SlotScalars() && NarrowInt(d - 2) && NarrowInt(d - 1) && (streq0(op, "<<") || streq0(op, ">>") || streq0(op, ">>>") || U(d - 2) == U(d - 1))) {
        const char* bx = SN(d - 2);
        const char* by = SN(d - 1);
        const char* expr = streq0(op, "<<") ? fcFmt(g->c, "FIRE_SCALAR_SHL(%s, %s)", bx, by) : streq0(op, ">>") ? fcFmt(g->c, "FIRE_SCALAR_SHR(%s, %s)", bx, by)
            : streq0(op, ">>>") ? fcFmt(g->c, "FIRE_SCALAR_USHR(%s, %s)", bx, by) : fcFmt(g->c, "(FireInt)(%s %s %s)", bx, op, by);
        SetScalar(d - 2, TypeOf(KindInt, U(d - 2)) | NarrowBit, expr);
        d--;
        return true;
    }
    a = S(d - 2); b = S(d - 1);
    unit = U(d - 2);
    unitC = unit == 0 ? "0" : UnitC(g, unit);
    if (streq0(op, "<<")) E("%s = IntOp1((int64_t)((uint64_t)%s.i << (%s.i & 63)), %s, %s);", a, a, b, unitC, a);
    else if (streq0(op, ">>")) E("%s = IntOp1(%s.i >> (%s.i & 63), %s, %s);", a, a, b, unitC, a);
    else if (streq0(op, ">>>")) E("%s = ushr(%s, %s);", a, a, b);
    else {
        if (U(d - 1) != unit) return false;
        E("%s = FIRE_LIKELY((%s.reserved | %s.reserved) == 0) ? IntOp(%s.i %s %s.i, %s, %s, %s) : %s(%s, %s);", a, a, b, a, op, b, unitC, a, b, streq0(op, "&") ? "bitAnd" : streq0(op, "|") ? "bitOr" : "bitXor", a, b);
    }
    d--;
    SetR(d - 1, false); SetT(d - 1, TypeOf(KindInt, unit)); g->kindsKept = true;
    return true;
}
static const char* xKindGuard2(FgSC* x, int k1, int k2) {
    int ks[2];
    FcList* parts = fcListNew(g->c);
    int i;
    ks[0] = k1; ks[1] = k2;
    for (i = 0; i < 2; i++) {
        int k = ks[i], bk;
        if (k < 0 || !IsNullable(Ty(k))) continue;
        bk = BK(k);
        fcListAdd(g->c, parts, (void*)fcFmt(g->c, "%s.kind == %s", S(k), bk == KindInt ? "K_Int" : bk == KindFloat ? "K_Float" : bk == KindWord ? "K_Word" : bk == KindBuf ? "K_Buffer" : bk == KindArr ? "K_Array" : "K_Bool"));
    }
    return fgJoin(g->c, parts, " && ");
}
static bool xTypedArith(FgSC* x, const char* op) {
    int intUnit, ka, kb, ua, ub, unit;
    bool ints;
    const char* a;
    const char* b;
    const char* guard;
    const char* generic;
    const char* unitArg;
    bool basic = streq0(op, "+") || streq0(op, "-") || streq0(op, "*") || streq0(op, "%") || streq0(op, "/");
    if (SlotScalars() && basic && NarrowInt(d - 2) && NarrowInt(d - 1) && (intUnit = ArithUnit(op, U(d - 2), U(d - 1))) >= 0) {
        const char* xv = SN(d - 2);
        const char* yv = SN(d - 1);
        int ux = U(d - 2), uy = U(d - 1);
        int narrow = TypeOf(KindInt, intUnit) | NarrowBit;
        if (streq0(op, "/") || streq0(op, "%")) {
            const char* r;
            SetT(d - 2, narrow); SetR(d - 2, false); g->kindsKept = true;
            if (d - 2 < 64) st->alias[d - 2] = NULL;
            r = ScalarName(g, f, st, d - 2);
            if (streq0(op, "/"))
                E("if (FIRE_LIKELY(%s != 0)) %s = %s == -1 ? (FireInt)(0 - (FireUInt)%s) : %s / %s; else { Value m_ = divide(%s, %s); %s = (FireInt)m_.i; }", yv, r, yv, xv, xv, yv,
                    ScalarValueOf(g, KindInt, ux, xv), ScalarValueOf(g, KindInt, uy, yv), r);
            else
                E("if (FIRE_LIKELY(%s > 0)) %s = %s %% %s; else { Value m_ = modulo(%s, %s); %s = (FireInt)m_.i; }", yv, r, xv, yv, ScalarValueOf(g, KindInt, ux, xv), ScalarValueOf(g, KindInt, uy, yv), r);
            st->scalar |= 1ULL << (d - 2);
            xCheckIf(x, fcFmt(g->c, streq0(op, "/") ? "%s == 0" : "%s <= 0", yv));
        }
        else SetScalar(d - 2, narrow, fcFmt(g->c, "(FireInt)((FireUInt)%s %s (FireUInt)%s)", xv, op, yv));
        d--;
        return true;
    }
    if (SlotScalars() && basic && RealOperand(d - 2) && RealOperand(d - 1) && (K(d - 2) == KindFloat || K(d - 1) == KindFloat)) {
        const char* fx = SF(d - 2);
        const char* fy = SF(d - 1);
        int ux = U(d - 2), uy = U(d - 1), realUnit = ArithUnit(op, ux, uy);
        const char* factor = (streq0(op, "+") || streq0(op, "-")) && ux != uy ? xLinearFactor(x, uy, ux) : NULL;
        if (realUnit >= 0 || factor) {
            const char* y2 = factor ? fcFmt(g->c, "(Real)((double)(%s) * %s)", fy, factor) : fy;
            SetScalar(d - 2, TypeOf(KindFloat, factor ? ux : realUnit), streq0(op, "%") ? fcFmt(g->c, "(Real)fmod((double)(%s), (double)(%s))", fx, fy) : fcFmt(g->c, "(Real)(%s %s %s)", fx, op, y2));
            d--;
            return true;
        }
    }
    ka = BK(d - 2); kb = BK(d - 1);
    if (!IsNum(ka) || !IsNum(kb)) return false;
    ua = U(d - 2); ub = U(d - 1);
    a = S(d - 2); b = S(d - 1);
    guard = xKindGuard2(x, d - 2, d - 1);
    generic = streq0(op, "+") ? "add" : streq0(op, "-") ? "sub" : streq0(op, "*") ? "mul" : streq0(op, "/") ? "divide" : "modulo";
#define FG_EMIT_FAST(fast) do { const char* fast_ = (fast); if (!*guard) E("%s = %s;", a, fast_); else { E("%s = FIRE_LIKELY(%s) ? %s : %s(%s, %s);", a, guard, fast_, generic, a, b); xCheckIf(x, fcFmt(g->c, "%s.kind == K_Undefined", a)); } } while (0)
    ints = ka == KindInt && kb == KindInt;
    if ((streq0(op, "+") || streq0(op, "-") || streq0(op, "%")) && ua == ub) unit = ua;
    else {
        const char* factor;
        if ((streq0(op, "+") || streq0(op, "-")) && !ints && (factor = xLinearFactor(x, ub, ua)) != NULL) {
            const char* na = Num(d - 2);
            const char* db = Dbl(d - 1);
            FG_EMIT_FAST(fcFmt(g->c, "Float(%s %s (Real)(%s * %s), %s)", na, op, db, factor, UnitC(g, ua)));
            d--;
            SetR(d - 1, false); SetT(d - 1, TypeOf(KindFloat, ua)); g->kindsKept = true;
            return true;
        }
        if (streq0(op, "*") && (ua == 0 || ub == 0)) unit = ua == 0 ? ub : ua;
        else if (streq0(op, "/") && ub == 0) unit = ua;
        else return false;
    }
    unitArg = unit == 0 ? "" : fcFmt(g->c, ", %s", UnitC(g, unit));
    if (ints) {
        if (streq0(op, "/")) { E("%s = divide(%s, %s);", a, a, b); xCheckIf(x, fcFmt(g->c, "%s.kind == K_Undefined", a)); }
        else if (streq0(op, "%")) {
            E("%s = FIRE_LIKELY(%s%s.i > 0) ? IntOp(%s.i %% %s.i, %s, %s, %s) : modulo(%s, %s);", a, *guard ? fcFmt(g->c, "%s && ", guard) : "", b, a, b, unit == 0 ? "0" : UnitC(g, unit), a, b, a, b);
            xCheckIf(x, fcFmt(g->c, "%s.kind == K_Undefined", a));
        }
        else FG_EMIT_FAST(fcFmt(g->c, "IntOp((int64_t)((uint64_t)%s.i %s (uint64_t)%s.i), %s, %s, %s)", a, op, b, unit == 0 ? "0" : UnitC(g, unit), a, b));
    }
    else if (streq0(op, "%")) { const char* na = Num(d - 2); const char* nb = Num(d - 1); FG_EMIT_FAST(fcFmt(g->c, "Float(fmod(%s, %s)%s)", na, nb, unitArg)); }
    else { const char* na = Num(d - 2); const char* nb = Num(d - 1); FG_EMIT_FAST(fcFmt(g->c, "Float(%s %s %s%s)", na, op, nb, unitArg)); }
#undef FG_EMIT_FAST
    d--;
    SetR(d - 1, false); SetT(d - 1, TypeOf(ints ? KindInt : KindFloat, unit) | (ints && (Ty(d - 1) & Ty(d) & NarrowBit) != 0 ? NarrowBit : 0)); g->kindsKept = true;
    return true;
}
static bool xScalarElemFits(FgSC* x, int elem, int k) {
    switch (ArrElemKind(elem)) {
        case KindInt: return NarrowInt(k) && U(k) == 0;
        case KindFloat: return (K(k) == KindFloat || NarrowInt(k)) && U(k) == 0;
        case KindBool: return K(k) == KindBool;
        default: return false;
    }
}
static bool xScalarPower(FgSC* x, bool toFloat) {
    int pu;
    if (!SlotScalars() || !RealOperand(d - 2) || !RealOperand(d - 1) || U(d - 2) != U(d - 1)) return false;
    pu = U(d - 2);
    if (!toFloat && NarrowInt(d - 2) && NarrowInt(d - 1)) {
        const char* xv = SN(d - 2);
        const char* ev = SN(d - 1);
        const char* r = ScalarName(g, f, st, d - 2);
        SetT(d - 2, TypeOf(KindInt, pu) | NarrowBit); SetR(d - 2, false); g->kindsKept = true;
        if (d - 2 < 64) st->alias[d - 2] = NULL;
        E("if (FIRE_LIKELY(%s >= 0)) %s = (FireInt)intPower(%s, %s); else { Value m_ = power(%s, %s); %s = (FireInt)m_.i; }", ev, r, xv, ev, ScalarValueOf(g, KindInt, pu, xv), ScalarValueOf(g, KindInt, pu, ev), r);
        st->scalar |= 1ULL << (d - 2);
        xCheckIf(x, fcFmt(g->c, "%s < 0", ev));
        d--;
        return true;
    }
    if (!(K(d - 2) == KindFloat || K(d - 2) == KindInt) || (K(d - 2) == KindInt && !NarrowInt(d - 2)) || (K(d - 1) == KindInt && !NarrowInt(d - 1))) return false;
    {
        const char* fa = SF(d - 2);
        const char* fb = SF(d - 1);
        SetScalar(d - 2, TypeOf(KindFloat, pu), fcFmt(g->c, "(Real)pow((double)%s, (double)%s)", fa, fb));
    }
    d--;
    return true;
}
/* `Math.Abs(x)`, `Math.Sqrt(x)`, `Math.Max(a, b)`, ... of C numbers */
static bool xCNum(FgSC* x, int k) { return K(k) == KindFloat || NarrowInt(k); }
static const char* xMD(FgSC* x, int k) { return K(k) == KindFloat ? fcFmt(g->c, "(double)%s", SF(k)) : fcFmt(g->c, "(double)%s", SN(k)); }
static const char* xMV(FgSC* x, int k) { return K(k) == KindFloat ? SF(k) : SN(k); }
static const char* fgMLt(FgGen* gg, const char* a, const char* b, bool real) { return real ? fcFmt(gg->c, "fire_cmpReal(%s, %s) < 0", a, b) : fcFmt(gg->c, "%s < %s", a, b); }
static const char* fgMGt(FgGen* gg, const char* a, const char* b, bool real) { return real ? fcFmt(gg->c, "fire_cmpReal(%s, %s) > 0", a, b) : fcFmt(gg->c, "%s > %s", a, b); }
static bool xMathIntrinsic(FgSC* x, const char* method, int argc) {
    int a0 = d - argc, type = 0;
    const char* expr = NULL;
    const char* cfn;
#define SAME(i, j) (xCNum(x, i) && xCNum(x, j) && K(i) == K(j) && U(i) == U(j))
#define UNITLESS(k) (xCNum(x, k) && U(k) == 0)
    if (!SlotScalars()) return false;
    cfn = streq0(method, "Sin") ? "sin" : streq0(method, "Cos") ? "cos" : streq0(method, "Tan") ? "tan" : streq0(method, "Asin") ? "asin" : streq0(method, "Acos") ? "acos"
        : streq0(method, "Atan") ? "atan" : streq0(method, "Log") ? "log" : streq0(method, "Log10") ? "log10" : streq0(method, "Exp") ? "exp" : streq0(method, "Sqrt") ? "sqrt" : NULL;
    if (argc == 1 && cfn && UNITLESS(a0)) { expr = fcFmt(g->c, "(Real)%s(%s)", cfn, xMD(x, a0)); type = TypeOf(KindFloat, 0); }
    else if (argc == 2 && streq0(method, "Atan2") && UNITLESS(a0) && UNITLESS(a0 + 1)) { const char* p = xMD(x, a0); expr = fcFmt(g->c, "(Real)atan2(%s, %s)", p, xMD(x, a0 + 1)); type = TypeOf(KindFloat, 0); }
    else if (argc == 1 && (streq0(method, "Round") || streq0(method, "Floor") || streq0(method, "Ceiling")) && xCNum(x, a0)) {
        type = Ty(a0);
        expr = K(a0) == KindInt ? SN(a0) : fcFmt(g->c, "(Real)%s(%s)", streq0(method, "Round") ? "round" : streq0(method, "Floor") ? "floor" : "ceil", xMD(x, a0));
    }
    else if (argc == 1 && (streq0(method, "Abs") || streq0(method, "Sign")) && xCNum(x, a0)) {
        bool real = K(a0) == KindFloat;
        const char* xv = xMV(x, a0);
        const char* zero = real ? fcFmt(g->c, "(%s - %s)", xv, xv) : "0";
        if (streq0(method, "Abs")) { expr = real ? fcFmt(g->c, "(%s ? -%s : %s)", fgMLt(g, xv, zero, true), xv, xv) : fcFmt(g->c, "(%s < 0 ? (FireInt)(0 - (FireUInt)%s) : %s)", xv, xv, xv); type = Ty(a0); }
        else { expr = fcFmt(g->c, "(%s ? (FireInt)-1 : %s ? (FireInt)1 : (FireInt)0)", fgMLt(g, xv, zero, real), fgMGt(g, xv, zero, real)); type = TypeOf(KindInt, 0) | NarrowBit; }
    }
    else if (argc == 2 && (streq0(method, "Min") || streq0(method, "Max")) && SAME(a0, a0 + 1)) {
        bool real = K(a0) == KindFloat;
        const char* a = xMV(x, a0);
        const char* b = xMV(x, a0 + 1);
        expr = fcFmt(g->c, "(%s ? %s : %s)", streq0(method, "Min") ? fgMLt(g, b, a, real) : fgMGt(g, b, a, real), b, a);
        type = JoinTypes(Ty(a0), Ty(a0 + 1));
    }
    else if (argc == 3 && streq0(method, "Clamp") && SAME(a0, a0 + 1) && SAME(a0, a0 + 2)) {
        bool real = K(a0) == KindFloat;
        const char* xv = xMV(x, a0);
        const char* lo = xMV(x, a0 + 1);
        const char* hi = xMV(x, a0 + 2);
        expr = fcFmt(g->c, "(%s ? %s : %s ? %s : %s)", fgMLt(g, xv, lo, real), lo, fgMGt(g, xv, hi, real), hi, xv);
        type = JoinTypes(JoinTypes(Ty(a0), Ty(a0 + 1)), Ty(a0 + 2));
    }
#undef SAME
#undef UNITLESS
    if (!expr || !(KindOfType(type) == KindInt || KindOfType(type) == KindFloat)) return false;
    SetScalar(a0, type, expr);
    d = a0 + 1;
    return true;
}
static const char* fgGenericCompare(const char* op) {
    return streq0(op, "<") ? "lt" : streq0(op, "<=") ? "le" : streq0(op, ">") ? "gt" : streq0(op, ">=") ? "ge" : streq0(op, "==") ? "opEq" : "!opEq";
}
/* A comparison of two ints of the program without a unit in C on FireInt; NULL when the operands are not such ints. */
static bool xMaybeNarrow(FgSC* x, int k) { return BaseKindOfType(Ty(k)) == KindInt && (Ty(k) & TypeNone) == 0 && (Ty(k) & (NarrowBit | CutBit)) != 0; }
static const char* xScalarIntCondition(FgSC* x, const char* op, int ia, int ib, bool* mayThrow) {
    bool na, nb;
    int known, maybe;
    const char* mv;
    const char* kv;
    const char* fast;
    const char* kValue;
    const char* slow;
    *mayThrow = false;
    if ((K(ia) == KindFloat || K(ib) == KindFloat) && RealOperand(ia) && RealOperand(ib) && U(ia) == U(ib)) {
        const char* ra = SF(ia);
        const char* rb = SF(ib);
        return streq0(op, "==") ? fcFmt(g->c, "FIRE_CC_FEQ(%s, %s)", ra, rb) : streq0(op, "!=") ? fcFmt(g->c, "!FIRE_CC_FEQ(%s, %s)", ra, rb) : fcFmt(g->c, "fire_cmpReal(%s, %s) %s 0", ra, rb, op);
    }
    if ((K(ia) == KindWord || K(ib) == KindWord) && xWordOperand(x, ia) && xWordOperand(x, ib)) { const char* wa = SW(ia); return fcFmt(g->c, "%s %s %s", wa, op, SW(ib)); }
    if (!xMaybeNarrow(x, ia) || !xMaybeNarrow(x, ib) || U(ia) != U(ib)) return NULL;
    na = NarrowInt(ia); nb = NarrowInt(ib);
    if (na && nb) { const char* sa = SN(ia); return fcFmt(g->c, "%s %s %s", sa, op, SN(ib)); }
    if (!na && !nb) return NULL;
    known = na ? ia : ib; maybe = na ? ib : ia;
    mv = VR(maybe); kv = SN(known);
    fast = na ? fcFmt(g->c, "%s %s (FireInt)%s.i", kv, op, mv) : fcFmt(g->c, "(FireInt)%s.i %s %s", mv, op, kv);
    kValue = ScalarValueOf(g, KindInt, U(known), kv);
    slow = na ? fcFmt(g->c, "%s(%s, %s)", fgGenericCompare(op), kValue, mv) : fcFmt(g->c, "%s(%s, %s)", fgGenericCompare(op), mv, kValue);
    *mayThrow = !(streq0(op, "==") || streq0(op, "!="));
    return fcFmt(g->c, "(FIRE_LIKELY(%s.kind == K_Int) ? (%s) : %s)", mv, fast, slow);
}
/* The C++ condition of `a op b` for two slots of known kind; NULL when the kinds are not known. */
static const char* xTypedCondition(FgSC* x, const char* op, int ia, int ib, bool* mayThrow) {
    int ka = BK(ia), kb = BK(ib), i;
    const char* a = S(ia);
    const char* b = S(ib);
    const char* fast;
    const char* guard;
    const char* wa;
    const char* wb;
    bool eq = streq0(op, "==") || streq0(op, "!=");
    *mayThrow = false;
    if (eq && (Ty(ia) == TypeUndefined) != (Ty(ib) == TypeUndefined)) {
        int other = Ty(ia) == TypeUndefined ? Ty(ib) : Ty(ia);
        if ((other & TypeNone) == 0 && BaseKindOfType(other) != KindUnknown && BaseKindOfType(other) != KindArr) {
            bool overloaded = false;
            const char* om = fcFmt(g->c, "operator%s", op);
            for (i = 0; i < g->classList->count && !overloaded; i++) if (FG_FIND(((FgClassInfo*)g->classList->items[i])->rc, om, 1).proto) overloaded = true;
            if (!overloaded) return fcFmt(g->c, "%s.kind %s K_Undefined", Ty(ia) == TypeUndefined ? b : a, op);
        }
    }
    if ((K(ia) == KindWord || K(ib) == KindWord) && (wa = xWordBits(x, ia)) != NULL && (wb = xWordBits(x, ib)) != NULL) return fcFmt(g->c, "%s %s %s", wa, op, wb);
    if (ka == KindBool && kb == KindBool) fast = eq ? fcFmt(g->c, "%s.i %s %s.i", a, op, b) : NULL;
    else if (!IsNum(ka) || !IsNum(kb) || U(ia) != U(ib)) fast = NULL;
    else if (ka == KindInt && kb == KindInt) fast = fcFmt(g->c, "%s.i %s %s.i", a, op, b);
    else if (eq) { const char* na = Num(ia); fast = fcFmt(g->c, "%sFIRE_CC_FEQ(%s, %s)", streq0(op, "!=") ? "!" : "", na, Num(ib)); }
    else fast = fcFmt(g->c, "compareNumeric(%s, %s) %s 0", a, b, op);
    if (!fast) return NULL;
    guard = xKindGuard2(x, ia, ib);
    if (!*guard) return fast;
    *mayThrow = !eq;
    return fcFmt(g->c, "(FIRE_LIKELY(%s) ? (%s) : %s(%s, %s))", guard, fast, fgGenericCompare(op), a, b);
}
static bool xTypedCompare(FgSC* x, const char* op) {
    bool scalarThrows, mayThrow;
    const char* cond;
    if (SlotScalars() && (cond = xScalarIntCondition(x, op, d - 2, d - 1, &scalarThrows)) != NULL) {
        SetScalar(d - 2, TypeOf(KindBool, 0), cond);
        if (scalarThrows) xCheckIf(x, fcFmt(g->c, "!%s", ScalarName(g, f, st, d - 2)));
        d--;
        return true;
    }
    cond = xTypedCondition(x, op, d - 2, d - 1, &mayThrow);
    if (!cond) return false;
    E("%s = Bool(%s);", S(d - 2), cond);
    if (mayThrow) xCheckIf(x, fcFmt(g->c, "%s.i == 0", S(d - 2)));
    d--;
    SetR(d - 1, false); SetT(d - 1, TypeOf(KindBool, 0)); g->kindsKept = true;
    return true;
}
/* CheckUnit (adaptUnit) on a number of known kind and unit */
static bool xTypedAdaptUnit(FgSC* x, const char* demand) {
    bool toFloat = demand[0] == '#';
    const char* unitText = fgTrimStart(demand, '#');
    int kind = K(d - 1), from = U(d - 1), resultKind = toFloat ? KindFloat : kind, resultUnit;
    if (streq0(unitText, "any")) resultUnit = from;
    else {
        bool dimensionOnly = fcStartsWith(unitText, "any ");
        const FcU* demanded = DemandUnit(g, unitText);
        int target = UnitId(g, demanded);
        if (!fcUCompatible(UnitById(g, from), demanded)) return false;
        if (dimensionOnly || from == target) resultUnit = from;
        else {
            const char* s = S(d - 1);
            E("%s = %s;", s, AdaptUnitCall(g, S(d - 1), demand));
            Check();
            SetT(d - 1, TypeOf(resultKind, target)); g->kindsKept = true;
            return true;
        }
    }
    if (!toFloat && resultUnit == from) { g->kindsKept = true; return true; }
    if (toFloat && kind == KindInt && SlotScalars() && IsScalar(d - 1)) { const char* fx = SF(d - 1); SetScalar(d - 1, TypeOf(KindFloat, from), fx); return true; }
    if (toFloat && kind == KindInt) { const char* s = S(d - 1); E("%s = Float((Real)%s.i%s);", s, S(d - 1), from == 0 ? "" : fcFmt(g->c, ", %s", UnitC(g, from))); }
    SetT(d - 1, TypeOf(resultKind, resultUnit)); g->kindsKept = true;
    return true;
}
static bool xTypedJumpIfNot(FgSC* x, const char* op, FgStep* result) {
    bool mayThrow = false;
    const char* cond = NULL;
    FgEmit e = xEmitter(x);
    Need(2);
    if (st->scalar != 0) MaterializeAll(g, f, st, &e, d - 1, d - 2);
    if (SlotScalars()) cond = xScalarIntCondition(x, op, d - 2, d - 1, &mayThrow);
    if (!cond) cond = xTypedCondition(x, op, d - 2, d - 1, &mayThrow);
    if (!cond) return false;
    if (mayThrow && g->usesExceptions && sb) E("if (!(%s)) { if (FIRE_UNLIKELY(g_unwind.active)) goto %s; goto L%d; }", cond, ExitLabel(g, f, st, locals), ins->a[0]);
    else E("if (!(%s)) goto L%d;", cond, ins->a[0]);
    d -= 2; st->depth = d; g->kindsKept = true;
    *result = fgStepJump(ins->a[0], true);
    return true;
}
static FgStep xJumpCompare(FgSC* x, const char* op, const char* fnName, bool negate, const char* opMethod) {
    FgStep r;
    if (xTypedJumpIfNot(x, op, &r)) return r;
    return xJumpIfNot(x, fnName, negate, opMethod);
}
static FgStep xArith(FgSC* x, bool typedWordFirst, const char* op, bool (*typed)(FgSC*, const char*), const char* fnName, const char* opMethod) {
    Need(2);
    if ((typedWordFirst && xTypedWord(x, op)) || typed(x, op)) return Next();
    return xBinary(x, fnName, opMethod);
}

static FgStep StepCore3(FgSC* x);

static FgStep StepCore2(FgSC* x) {
    FgEmit e = xEmitter(x);
    switch (ins->op) {
        case OP_StepAdd:
        case OP_Add:
            Need(2);
            if (ins->op == OP_StepAdd && SlotScalars() && IsScalar(d - 1) && U(d - 1) == 0 && U(d - 2) != 0 && IsNum(K(d - 1)) && IsNum(K(d - 2)))
                SetT(d - 1, (Ty(d - 1) & 0xFF) | U(d - 2) << 8);
            if (xTypedWord(x, "+") || xTypedArith(x, "+")) return Next();
            if (ins->op == OP_StepAdd) { const char* b = S(d - 1); const char* a = S(d - 2); E("%s = stepOf(%s, %s);", b, a, b); }
            if (xLeftMayOverload(x, "operator+", d - 2) && UseOperator(g, "operator+")) {
                const char* a = S(d - 2);
                const char* b = S(d - 1);
                E("%s = %s(%s, %s, &%s);", a, OperatorWrapper(g, "operator+"), a, b, OwnerList());
                Check();
                d--; SetR(d - 1, true);
            }
            else if (R(d - 2) || R(d - 1)) {
                const char* a = S(d - 2);
                const char* b = S(d - 1);
                E("%s = addR(%s, %s, &%s);", a, a, b, OwnerList());
                Check();
                d--; SetR(d - 1, true);
            }
            else {
                const char* a = S(d - 2);
                const char* b = S(d - 1);
                E("%s = add(%s, %s);", a, a, b); xCheckIf(x, fcFmt(g->c, "%s.kind == K_Undefined", S(d - 2))); d--; SetR(d - 1, false);
            }
            return Next();
        case OP_Power: Need(2); return xScalarPower(x, false) ? Next() : xBinary(x, "power", "operator^");
        case OP_PowerFloat: Need(2); return xScalarPower(x, true) ? Next() : xBinary(x, "powerF", "operator^");
        case OP_StepSub:
        case OP_Sub:
            Need(2);
            if (ins->op == OP_StepSub && SlotScalars() && IsScalar(d - 1) && U(d - 1) == 0 && U(d - 2) != 0 && IsNum(K(d - 1)) && IsNum(K(d - 2)))
                SetT(d - 1, (Ty(d - 1) & 0xFF) | U(d - 2) << 8);
            if (xTypedWord(x, "-") || xTypedArith(x, "-")) return Next();
            if (ins->op == OP_StepSub) { const char* b; const char* a; Need(2); b = S(d - 1); a = S(d - 2); E("%s = stepOf(%s, %s);", b, a, b); }
            return xBinary(x, "sub", "operator-");
        case OP_Mul: return xArith(x, true, "*", xTypedArith, "mul", "operator*");
        case OP_Div: return xArith(x, true, "/", xTypedArith, "divide", "operator/");
        case OP_Mod: return xArith(x, true, "%", xTypedArith, "modulo", "operator%");
        case OP_BitAnd: return xArith(x, true, "&", xTypedIntBits, "bitAnd", "operator&");
        case OP_BitOr: return xArith(x, true, "|", xTypedIntBits, "bitOr", "operator|");
        case OP_BitXor: Need(2); return xTypedWord(x, "^") || xTypedIntBits(x, "^") ? Next() : xBinary(x, "bitXor", "operator#");
        case OP_ShiftLeft: return xArith(x, true, "<<", xTypedIntBits, "shl", "operator<<");
        case OP_ShiftRight: return xArith(x, true, ">>", xTypedIntBits, "shr", "operator>>");
        case OP_ShiftRightLogical: Need(2); return xTypedWord(x, ">>") || xTypedIntBits(x, ">>>") ? Next() : xBinary(x, "ushr", "operator>>>");
        case OP_RotateUnderTop: {
            bool lower, middle;
            int tl, tm;
            Need(3);
            {
                const char* s3 = S(d - 3);
                const char* s2 = S(d - 2);
                E("{ Value t = %s; %s = %s; %s = t; }", s3, s3, s2, s2);
            }
            lower = R(d - 3); middle = R(d - 2);
            SetR(d - 3, middle); SetR(d - 2, lower);
            tl = Ty(d - 3); tm = Ty(d - 2); SetT(d - 3, tm); SetT(d - 2, tl); g->kindsKept = true;
            return Next();
        }
        case OP_CoerceUnit: {
            int target, kind, from;
            bool sameDimension;
            Need(1);
            target = UnitId(g, (const FcU*)x->chunk->units->items[ins->a[0]]);
            kind = K(d - 1);
            from = U(d - 1);
            if (IsNum(kind) && from == target) { g->kindsKept = true; return Next(); }
            { const char* s = S(d - 1); E("%s = coerceUnit(%s, %s);", s, s, UnitC(g, target)); }
            if (!IsNum(kind)) Check();
            sameDimension = fcUCompatible(UnitById(g, from), UnitById(g, target)) && !(target == 0 && from != 0);
            if (kind == KindFloat || (kind == KindInt && sameDimension)) { SetT(d - 1, TypeOf(kind, target)); g->kindsKept = true; }
            return Next();
        }
        case OP_RelabelUnitDynamic: {
            const char* s1;
            Need(2);
            s1 = S(d - 1);
            E("%s = relabelUnit(%s, unitOf(%s));", s1, s1, S(d - 2));
            if (!IsNum(K(d - 1))) Check();
            return Next();
        }
        case OP_RelabelUnit: {
            int target;
            Need(1);
            target = UnitId(g, (const FcU*)x->chunk->units->items[ins->a[0]]);
            if (IsNum(K(d - 1))) {
                E("%s.unit = %s;", S(d - 1), UnitC(g, target));
                SetT(d - 1, TypeOf(K(d - 1), target)); g->kindsKept = true;
                return Next();
            }
            { const char* s = S(d - 1); E("%s = relabelUnit(%s, %s);", s, s, UnitC(g, target)); }
            Check();
            return Next();
        }
        case OP_CoerceUnitDynamic: {
            const char* s1;
            Need(2);
            s1 = S(d - 1);
            E("%s = coerceUnit(%s, unitOf(%s));", s1, s1, S(d - 2));
            if (!IsNum(K(d - 1))) Check();
            return Next();
        }
        case OP_CoerceType: {
            int tag = ins->a[0];
            Need(1);
            if (SlotScalars() && (tag == TAG_Int || tag == TAG_Float) && (K(d - 1) == KindFloat || NarrowInt(d - 1))) {
                bool toInt = tag == TAG_Int;
                int cu = U(d - 1);
                if ((toInt && NarrowInt(d - 1)) || (!toInt && K(d - 1) == KindFloat)) return Next();
                if (toInt) SetScalar(d - 1, TypeOf(KindInt, cu) | NarrowBit, fcFmt(g->c, "(FireInt)fire_realToInt((double)%s)", SF(d - 1)));
                else SetScalar(d - 1, TypeOf(KindFloat, cu), fcFmt(g->c, "(Real)%s", SN(d - 1)));
                return Next();
            }
            {
                const char* s = S(d - 1);
                E("%s = %scoerceType(%s, %s));", s, tag == TAG_Int && g->program->intWidth < 64 ? "intProgram(" : "(", s,
                    tag == TAG_Bool ? "K_Bool" : tag == TAG_Int ? "K_Int" : tag == TAG_Float ? "K_Float" : tag == TAG_Char ? "K_Char" : "K_String");
            }
            Check();
            return Next();
        }
        case OP_CastTo: {
            const char* castName;
            const char* castTest;
            const char* test;
            const char* dead;
            const char* s;
            Need(1);
            castName = Str(ins->a[0]);
            castTest = streq0(castName, "bool") ? "K_Bool" : streq0(castName, "int") ? "K_Int" : streq0(castName, "float") ? "K_Float" : streq0(castName, "char") ? "K_Char"
                : streq0(castName, "word") ? "K_Word" : streq0(castName, "string") ? "K_String" : streq0(castName, "undefined") ? "K_Undefined" : streq0(castName, "class") ? "K_Class"
                : streq0(castName, "lambda") ? "K_Lambda" : "";
            s = S(d - 1);
            test = streq0(castTest, "K_Int") ? fcFmt(g->c, "%s.kind == K_Int && %s.reserved == 0", s, s) : *castTest ? fcFmt(g->c, "%s.kind == %s", s, castTest) : fcFmt(g->c, "%s(%s)", IsOfFn(g, castName), s);
            if (!*castTest) fcSetAdd(g->isOfTypes, castName);
            dead = !*castTest ? fcFmt(g->c, "if (FIRE_UNLIKELY(%s.kind == K_Class && !leafAlive(%s))) destroyedError(%s); else ", s, s, s) : "";
            E("%sif (FIRE_UNLIKELY(%s.kind != K_Undefined && !(%s))) castFailed(%s, %s);", dead, s, test, s, CString(g, castName));
            Check();
            return Next();
        }
        case OP_RebindLambda: {
            const char* a;
            const char* b;
            Need(2);
            a = S(d - 2); b = S(d - 1);
            E("%s = rebindLam(%s, %s, &%s);", a, a, b, OwnerList());
            Check();
            d--; SetR(d - 1, true); return Next();
        }
        case OP_ConvertWidth: {
            Need(1);
            if (SlotScalars() && IsScalar(d - 1) && (ins->a[1] & 1) != 0 && (ins->a[1] & 8) == 0 && RealOperand(d - 1)) {
                const char* fx = SF(d - 1);
                int fbits = ins->a[0];
                SetScalar(d - 1, TypeOf(KindFloat, U(d - 1)), fbits < 64 ? fcFmt(g->c, "(Real)roundFloatToWidth((double)(%s), %d)", fx, fbits) : fx);
                return Next();
            }
            if (SlotScalars() && IsScalar(d - 1) && (ins->a[1] & 1) == 0) {
                bool toWord = (ins->a[1] & 8) != 0, unsignedTo = (ins->a[1] & 2) != 0;
                int bits = ins->a[0];
                if (toWord && (K(d - 1) == KindWord || NarrowInt(d - 1))) {
                    const char* w = K(d - 1) == KindWord ? SW(d - 1) : fcFmt(g->c, "(FireWord)(uint64_t)(int64_t)%s", SN(d - 1));
                    SetScalar(d - 1, TypeOf(KindWord, 0), w);
                    return Next();
                }
                if (!toWord && (NarrowInt(d - 1) || K(d - 1) == KindWord) && (bits == 8 || bits == 16 || bits == 32 || bits == 64) && bits <= g->program->intWidth) {
                    const char* xv = K(d - 1) == KindWord ? fcFmt(g->c, "(FireInt)%s", SW(d - 1)) : SN(d - 1);
                    const char* ctype = fcFmt(g->c, "%s%d_t", unsignedTo ? "uint" : "int", bits);
                    int keptUnit = unsignedTo || K(d - 1) == KindWord ? 0 : U(d - 1);
                    SetScalar(d - 1, TypeOf(KindInt, keptUnit) | NarrowBit, bits == 64 ? xv : fcFmt(g->c, "(FireInt)(%s)%s", ctype, xv));
                    return Next();
                }
            }
            if ((ins->a[1] & 8) != 0 && (K(d - 1) == KindWord || K(d - 1) == KindInt)) {
                if (K(d - 1) == KindInt) { const char* s = S(d - 1); E("%s = WordV((uint64_t)(int64_t)%s.i);", s, s); }
                SetR(d - 1, false); SetT(d - 1, TypeOf(KindWord, 0)); g->kindsKept = true;
                return Next();
            }
            if ((ins->a[1] & 0x0F) == (2 | 4) && ins->a[0] == 8 && (K(d - 1) == KindWord || K(d - 1) == KindInt)) {
                const char* s = S(d - 1);
                E("%s = Int((uint8_t)(%s));", s, K(d - 1) == KindWord ? fcFmt(g->c, "%s.w", s) : fcFmt(g->c, "%s.i", s));
                SetR(d - 1, false); SetT(d - 1, TypeOf(KindInt, 0) | NarrowBit); g->kindsKept = true;
                return Next();
            }
            if ((ins->a[1] & 8) != 0) {
                bool implicitWord = (ins->a[1] & 4) != 0;
                const char* s = S(d - 1);
                E("%s = wordOf(%s, %s);", s, s, implicitWord ? "true" : "false");
                Check();
                SetT(d - 1, TypeUnknown); g->kindsKept = true;
                return Next();
            }
            if ((ins->a[1] & 4) != 0) {
                const char* s = S(d - 1);
                const char* implicitTest = (ins->a[1] & 1) != 0 ? fcFmt(g->c, "%s.kind == K_Float", s) : fcFmt(g->c, "(%s.kind == K_Int || %s.kind == K_Word)", s, s);
                int cutBits, narrowCut;
                E("if (%s) %s = convertWidth(%s, %d, %s, %s);", implicitTest, s, s, ins->a[0], (ins->a[1] & 1) != 0 ? "true" : "false", (ins->a[1] & 2) != 0 ? "true" : "false");
                cutBits = ins->a[0];
                narrowCut = (ins->a[1] & 1) == 0 && cutBits <= g->program->intWidth && (cutBits == 8 || cutBits == 16 || cutBits == 32 || cutBits == 64) ? NarrowBit : 0;
                if ((ins->a[1] & 2) != 0 && K(d - 1) == KindInt) { SetT(d - 1, TypeOf(KindInt, 0) | narrowCut); g->kindsKept = true; }
                else if (K(d - 1) == KindInt) { SetT(d - 1, (Ty(d - 1) & ~(NarrowBit | CutBit)) | narrowCut); g->kindsKept = true; }
                return Next();
            }
            { const char* s = S(d - 1); E("%s = convertWidth(%s, %d, %s, %s);", s, s, ins->a[0], (ins->a[1] & 1) != 0 ? "true" : "false", (ins->a[1] & 2) != 0 ? "true" : "false"); }
            Check();
            return Next();
        }
        case OP_CoerceTypeDynamic: {
            const char* s1;
            Need(2);
            s1 = S(d - 1);
            E("%s = coerceTypeTo(%s, %s.kind);", s1, s1, S(d - 2));
            Check();
            return Next();
        }
        case OP_IsInUnit: {
            const char* s;
            Need(1);
            s = S(d - 1);
            E("%s = Bool(unitCompatible(unitOf(%s), %s));", s, s, UnitC(g, UnitId(g, (const FcU*)x->chunk->units->items[ins->a[0]])));
            SetR(d - 1, false); return Next();
        }
        case OP_ToEnum: {
            const char* s;
            Need(1);
            s = S(d - 1);
            E("%s = toEnum(%s, %s);", s, s, EnumC(g, EnumIndex(g, fgConst(x->chunk, ins->a[0])->en)));
            Check();
            SetR(d - 1, false); return Next();
        }
        case OP_IsOfType: {
            const char* typeName;
            const char* test;
            const char* s;
            Need(1);
            typeName = Str(ins->a[0]);
            test = streq0(typeName, "bool") ? "K_Bool" : streq0(typeName, "int") ? "K_Int" : streq0(typeName, "float") ? "K_Float" : streq0(typeName, "char") ? "K_Char"
                : streq0(typeName, "word") ? "K_Word" : streq0(typeName, "string") ? "K_String" : streq0(typeName, "undefined") ? "K_Undefined" : streq0(typeName, "class") ? "K_Class"
                : streq0(typeName, "lambda") ? "K_Lambda" : "";
            s = S(d - 1);
            if (streq0(test, "K_Int")) E("%s = Bool(%s.kind == K_Int && %s.reserved == 0);", s, s, s);
            else if (*test) E("%s = Bool(%s.kind == %s);", s, s, test);
            else {
                fcSetAdd(g->isOfTypes, typeName);
                E("%s = FIRE_UNLIKELY(%s.kind == K_Class && !leafAlive(%s)) ? destroyedError(%s) : Bool(%s(%s));", s, s, s, s, IsOfFn(g, typeName), s);
                SetR(d - 1, false);
                Check();
                return Next();
            }
            SetR(d - 1, false); return Next();
        }
        case OP_InitNext:
            g->initNext = true;
            g->kindsKept = true;
            return Next();
        case OP_RecordCheck:
            Need(1);
            if (ins->a[0] == 1) E("recordRequire(%s);", SR(d - 1)); else E("(void)recordCheckV(%s);", SR(d - 1));
            Check();
            g->kindsKept = true;
            return Next();
        case OP_HasMember: {
            const char* memberName;
            const char* s;
            Need(1);
            memberName = Str(ins->a[0]);
            if (Modular) { s = S(d - 1); E("%s = Bool(fire_hasMemberDyn(%s, %s));", s, s, CString(g, memberName)); }
            else {
                fcSetAdd(g->hasMembers, memberName);
                s = S(d - 1);
                E("%s = Bool(%shasm_%s(%s));", s, Px, Mangle(g, memberName), s);
            }
            SetR(d - 1, false);
            return Next();
        }
        case OP_IsFrom:
            Need(2);
            if (ins->a[0] == 2) {
                FcList* fromLists;
                const char* fromDecl;
                const char* s;
                if (!g->usesTryDepth) { g->usesTryDepth = true; g->version++; }
                f->usesTryTake = true;
                fromLists = CallLists(g, f, st);
                fromDecl = fromLists->count == 0 ? "" : CArray(g, "OwnList*", "L_", fromLists);
                s = S(d - 2);
                E("{ %s%s = Bool(isFromLocal(%s, %s, %d, MYARGS)); }", fromDecl, s, s, fromLists->count == 0 ? "NULL" : "L_", fromLists->count);
            }
            else if (ins->a[0] == 3) {
                const char* s;
                if (!g->usesGlobalOwn) { g->usesGlobalOwn = true; g->version++; }
                s = S(d - 2);
                E("%s = Bool(isFromGlobal(%s));", s, s);
            }
            else {
                const char* a = S(d - 2);
                const char* b = S(d - 1);
                E("%s = Bool(isFrom(%s, %s, %s));", a, a, b, ins->a[0] != 0 ? "true" : "false");
                Check();
            }
            d--; SetR(d - 1, false); return Next();
        case OP_Neg:
            Need(1);
            if (SlotScalars() && IsScalar(d - 1) && NarrowInt(d - 1)) { const char* nx = SN(d - 1); SetScalar(d - 1, Ty(d - 1), fcFmt(g->c, "(FireInt)((FireUInt)0 - (FireUInt)%s)", nx)); return Next(); }
            if (SlotScalars() && IsScalar(d - 1) && K(d - 1) == KindFloat) { const char* fx = SF(d - 1); SetScalar(d - 1, Ty(d - 1), fcFmt(g->c, "(Real)(-%s)", fx)); return Next(); }
            if (K(d - 1) == KindInt) { const char* s = S(d - 1); E("%s = IntOp1((int64_t)(0 - (uint64_t)%s.i), %s, %s);", s, s, U(d - 1) == 0 ? "0" : UnitC(g, U(d - 1)), s); g->kindsKept = true; return Next(); }
            if (K(d - 1) == KindFloat) { const char* s = S(d - 1); E("%s = Float(-%s.f%s);", s, s, U(d - 1) == 0 ? "" : fcFmt(g->c, ", %s", UnitC(g, U(d - 1)))); g->kindsKept = true; return Next(); }
            return xUnary(x, "negate");
        case OP_LogicalNot:
            Need(1);
            if (SlotScalars() && IsScalar(d - 1) && K(d - 1) == KindBool) { const char* bx = SB(d - 1); SetScalar(d - 1, TypeOf(KindBool, 0), fcFmt(g->c, "!%s", bx)); return Next(); }
            if (K(d - 1) == KindBool) { const char* s = S(d - 1); E("%s = Bool(%s.i == 0);", s, s); g->kindsKept = true; return Next(); }
            return xUnary(x, "lnot");
        case OP_BitNot:
            Need(1);
            if (SlotScalars() && IsScalar(d - 1) && NarrowInt(d - 1)) { const char* nx = SN(d - 1); SetScalar(d - 1, Ty(d - 1), fcFmt(g->c, "(FireInt)~%s", nx)); return Next(); }
            if (SlotScalars() && IsScalar(d - 1) && K(d - 1) == KindWord) { const char* wx = SW(d - 1); SetScalar(d - 1, Ty(d - 1), fcFmt(g->c, "(FireWord)~%s", wx)); return Next(); }
            return xUnary(x, "bitNot");
        case OP_Eq: Need(2); return xTypedCompare(x, "==") ? Next() : xCompare(x, "opEq", false, "operator==");
        case OP_NotEq: Need(2); return xTypedCompare(x, "!=") ? Next() : xCompare(x, "opEq", true, "operator!=");
        case OP_Lt: Need(2); return xTypedCompare(x, "<") ? Next() : xCompare(x, "lt", false, "operator<");
        case OP_LtEq: Need(2); return xTypedCompare(x, "<=") ? Next() : xCompare(x, "le", false, "operator<=");
        case OP_Gt: Need(2); return xTypedCompare(x, ">") ? Next() : xCompare(x, "gt", false, "operator>");
        case OP_GtEq: Need(2); return xTypedCompare(x, ">=") ? Next() : xCompare(x, "ge", false, "operator>=");
        case OP_Jump: {
            int target = ins->a[0], k;
            FgRegion* targetRegion = RegionAt(st->region, target);
            FgRegion* r;
            FgRegion* outermost;
            FgFlow* exitState;
            FgStep res;
            if (targetRegion == st->region) { E("goto L%d;", target); g->kindsKept = true; return fgStepJump(target, false); }
            for (r = st->region; r != targetRegion; r = r->outer) fgISetAdd(g->c, r->exitLabels, target);
            outermost = st->region;
            while (outermost->outer != targetRegion) outermost = outermost->outer;
            if (d - fgRegionDepthBase(outermost) > 4) FG_NOTSUP("a jump out of a catch block with %d operands (%s at %d)", d - fgRegionDepthBase(outermost), x->fn, ins->addr);
            for (k = fgRegionDepthBase(outermost); k < d; k++) E("g_unwind.regs[%d] = %s;", k - fgRegionDepthBase(outermost), SlotName(g, st->region, k));
            E("exitJump(&H%d, %d); return;", st->region->template_, target);
            exitState = fgFlowClone(g, st);
            exitState->region = targetRegion;
            memset(&res, 0, sizeof res);
            res.extra = (FgEdge*)fbAlloc(&g->flowArena, sizeof(FgEdge));
            res.extra[0].addr = target; res.extra[0].state = exitState; res.extraCount = 1;
            return res;
        }
        case OP_JumpIfFalse:
            Need(1);
            if (st->scalar != 0) MaterializeAll(g, f, st, &e, d - 1, -1);
            if (IsScalar(d - 1) && BK(d - 1) == KindBool) E("if (!%s) goto L%d;", SB(d - 1), ins->a[0]);
            else if (K(d - 1) == KindBool) E("if (!%s.i) goto L%d;", S(d - 1), ins->a[0]);
            else xJumpOnCondition(x, S(d - 1), false, ins->a[0]);
            d--; st->depth = d; g->kindsKept = true;
            return fgStepJump(ins->a[0], true);
        case OP_JumpIfFalsePeek:
            Need(1);
            if (K(d - 1) == KindBool) E("if (!%s.i) goto L%d;", S(d - 1), ins->a[0]);
            else xJumpOnCondition(x, S(d - 1), false, ins->a[0]);
            g->kindsKept = true;
            return fgStepJump(ins->a[0], true);
        case OP_JumpIfTruePeek:
            Need(1);
            if (K(d - 1) == KindBool) E("if (%s.i) goto L%d;", S(d - 1), ins->a[0]);
            else xJumpOnCondition(x, S(d - 1), true, ins->a[0]);
            g->kindsKept = true;
            return fgStepJump(ins->a[0], true);
        case OP_JumpIfNotLt: return xJumpCompare(x, "<", "lt", false, "operator<");
        case OP_JumpIfNotLtEq: return xJumpCompare(x, "<=", "le", false, "operator<=");
        case OP_JumpIfNotGt: return xJumpCompare(x, ">", "gt", false, "operator>");
        case OP_JumpIfNotGtEq: return xJumpCompare(x, ">=", "ge", false, "operator>=");
        case OP_JumpIfNotEq: return xJumpCompare(x, "==", "opEq", false, "operator==");
        case OP_JumpIfNotNotEq: return xJumpCompare(x, "!=", "opEq", true, "operator!=");
        case OP_EnterScope: {
            int parentId = st->scopes[st->scopeCount - 1].id;
            if (g->usesTakeUpwards) { fgISetAdd(g->c, f->needsList, ins->addr); fgISetAdd(g->c, f->needsList, parentId); }
            if (fgISetHas(g->c, f->needsList, ins->addr)) {
                E("%s.mark = poolMark();", ListName(g, ins->addr));
                if (g->usesTakeUpwards) E("%s.parent = &%s;", ListName(g, ins->addr), ListName(g, parentId));
            }
            fgFlowPushScope(g, st, ins->addr, 0); g->kindsKept = true; return Next();
        }
        case OP_ExitScope: {
            int id;
            if (st->scopeCount <= 1) FG_NOTSUP("ExitScope without scope at %d in %s", ins->addr, x->fn);
            id = st->scopes[st->scopeCount - 1].id;
            if (st->region && id == st->region->catchScopeId && st->scopeCount - 1 == fgRegionScopeBase(st->region)) st->region->deferCatchRelease = true;
            else if (st->scopeCount - 1 >= (st->region ? fgRegionScopeBase(st->region) : 0)) {
                bool any = false;
                FcList* vars;
                int k;
                EmitLeave(g, f, id, &e);
                xReleaseScopeVariables(x, id, true);
                vars = ScopeVariables(g, f, id, locals);
                for (k = 0; k < vars->count; k++) if (VarRef(g, VarKey(g, f, (const char*)vars->items[k]))) any = true;
                if (!fgISetHas(g->c, f->needsList, TempOf(id)) && !fgISetHas(g->c, f->needsList, id) && !any) { g->selfAliveAfter = st->selfAlive ? 1 : 0; g->neutralAfter = true; }
            }
            st->scopeCount--;
            g->kindsKept = true;
            return Next();
        }
        default:
            return StepCore3(x);
    }
}
