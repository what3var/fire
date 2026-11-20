/* native/forge/gen/cppgenerator4.c - src/fire.Native/CppGenerator.cs (commit 808e9788), part 4: StepCore up to its switch - the local functions of
 * the C# method (S, SR, T, E, Check, the arguments of a call, the scalar shadows, StoreVar, ...) as functions over the state of one step (FgSC), and
 * CppGenerator.CFields.cs (fields as C types). The step files (cppgenerator4.c ... ) name the state of the step as the C# does: `d`, `f`, `st`,
 * `ins`, `sb`, `locals` are macros over `x` (`x->chunk`, `x->fn` are written out); they are undefined at the end of the last step file. */

typedef struct FgSC {
    FgGen* g;
    FgFunc* f;
    FcChunk* chunk;
    FgInstr* ins;
    FgFlow* st;
    FcBuf* sb;
    FcDict* locals;
    int d;
    const char* fn;
    int argsTravel;
} FgSC;

#define g (x->g)
#define f (x->f)
#define ins (x->ins)
#define st (x->st)
#define sb (x->sb)
#define locals (x->locals)
#define d (x->d)

/* ---- CppGenerator.CFields.cs ---- */
typedef struct FgCField { int slot, half, type; const FcRtClass* rc; int index; bool ok; } FgCField;
static FgCField CFieldOf(FgGen* gg, const FcRtClass* rc, const char* name, FgFunc* from) {
    FgCField r;
    int index, declared, type, kind;
    memset(&r, 0, sizeof r);
    if (!(!fgDebugBuild(gg) && (gg->program->intWidth == 32 || gg->program->intWidth == 64)) || (index = DirectFieldIndex(gg, rc, name, from)) < 0) return r;
    declared = fgFieldRequiredKind(rc, name);
    if (declared == 0 || !fgDkIsNonNull(declared)) return r;
    type = FieldTypeIn(gg, rc, name);
    kind = KindOfType(type);
    if (!(IsNarrowIntType(type) || kind == KindFloat || kind == KindBool || kind == KindWord)) return r;
    FieldPlace(gg, rc, index, &r.slot, &r.half);
    if (r.half >= 0 && kind == KindWord) return r;
    r.type = type; r.rc = rc; r.index = index; r.ok = true;
    return r;
}
static const char* CFieldRead(FgGen* gg, FgCField field, const char* obj) {
    const char* raw = RawRef(gg, field.rc, field.index, obj);
    const char* place;
    int kind = KindOfType(field.type);
    if (raw) return kind == KindBool ? fcFmt(gg->c, "(%s != 0)", raw) : raw;
    place = fcFmt(gg->c, "objFields(%s)[%d]", obj, field.slot);
    if (field.half < 0) {
        if (kind == KindFloat) return fcFmt(gg->c, "%s.f", place);
        if (kind == KindWord) return fcFmt(gg->c, "(FireWord)%s.w", place);
        if (kind == KindBool) return fcFmt(gg->c, "(%s.i != 0)", place);
        return fcFmt(gg->c, "(FireInt)%s.i", place);
    }
    if (kind == KindFloat) return fcFmt(gg->c, "(Real)nvGetF(&%s, %d)", place, field.half);
    if (kind == KindBool) return fcFmt(gg->c, "(nvGetI(&%s, %d) != 0)", place, field.half);
    return fcFmt(gg->c, "(FireInt)nvGetI(&%s, %d)", place, field.half);
}
static const char* CFieldWrite(FgGen* gg, FgCField field, const char* obj, const char* xv) {
    const char* raw = RawRef(gg, field.rc, field.index, obj);
    const char* place;
    int kind = KindOfType(field.type);
    if (raw) return kind == KindBool ? fcFmt(gg->c, "%s = (%s) ? 1 : 0;", raw, xv) : fcFmt(gg->c, "%s = %s;", raw, xv);
    place = fcFmt(gg->c, "objFields(%s)[%d]", obj, field.slot);
    if (field.half < 0) {
        if (kind == KindFloat) return fcFmt(gg->c, "%s.f = %s;", place, xv);
        if (kind == KindWord) return fcFmt(gg->c, "%s.w = %s;", place, xv);
        if (kind == KindBool) return fcFmt(gg->c, "%s.i = (%s) ? 1 : 0;", place, xv);
        return fcFmt(gg->c, "%s.i = %s;", place, xv);
    }
    if (kind == KindFloat) return fcFmt(gg->c, "nvPutF(&%s, %d, (float)(%s));", place, field.half, xv);
    if (kind == KindBool) return fcFmt(gg->c, "nvPutI(&%s, %d, (%s) ? 1 : 0);", place, field.half, xv);
    return fcFmt(gg->c, "nvPutI(&%s, %d, (int32_t)(%s));", place, field.half, xv);
}
static bool CFieldTakes(int fieldType, int valueType) {
    switch (KindOfType(fieldType)) {
        case KindInt: return IsNarrowIntType(valueType) && UnitOfType(valueType) == UnitOfType(fieldType);
        case KindFloat: return (KindOfType(valueType) == KindFloat || IsNarrowIntType(valueType)) && UnitOfType(valueType) == UnitOfType(fieldType);
        case KindBool: return KindOfType(valueType) == KindBool;
        default: return KindOfType(valueType) == KindWord;
    }
}
static const FcRtClass* KnownObjClass(FgGen* gg, int type) {
    return BaseKindOfType(type) == KindObj && (type & (TypeNone | NullableBit)) == 0 && !InterfaceOfType(gg, type) ? ObjClass(gg, type) : NULL;
}
static FgCField CFieldAccess(FgGen* gg, FgFunc* ff, FgFlow* fs, const FgInstr* fi) {
    FgCField none, field;
    const FcRtClass* rc;
    int dd = fs->depth;
    memset(&none, 0, sizeof none);
    if (!(!fgDebugBuild(gg) && (gg->program->intWidth == 32 || gg->program->intWidth == 64)) || fs->region) return none;
    if ((ff->kind == FK_Init || ff->kind == FK_Ctor) && fi->op != OP_GetField) return none;
#define FTY(k) ((k) < 0 || (k) >= 64 ? TypeUnknown : fs->types[(k)])
    switch (fi->op) {
        case OP_GetField:
            if (dd >= 1 && dd < 63 && (rc = KnownObjClass(gg, FTY(dd - 1))) != NULL) return CFieldOf(gg, rc, fgConstStr(ff->chunk, fi->a[0]), ff);
            break;
        case OP_SetField: case OP_SetFieldClaim:
            if (dd >= 2 && dd < 63 && (rc = KnownObjClass(gg, FTY(dd - 2))) != NULL) {
                field = CFieldOf(gg, rc, fgConstStr(ff->chunk, fi->a[0]), ff);
                if (field.ok && CFieldTakes(field.type, FTY(dd - 1))) return field;
            }
            break;
        case OP_SetFieldOnThis:
            if (dd >= 1 && dd < 63 && fgHasSelf(ff) && !IsExtensionMethod(gg, ff->proto) && ff->chunk->ownerClass) {
                field = CFieldOf(gg, ff->chunk->ownerClass, fgConstStr(ff->chunk, fi->a[0]), ff);
                if (field.ok && CFieldTakes(field.type, FTY(dd - 1))) return field;
            }
            break;
        default: break;
    }
#undef FTY
    return none;
}

/* ---- the local functions of StepCore ---- */
static void xE(FgSC* x, const char* text) {
    if (!sb) return;
    if (g->lineDir && !g->lineFresh) fcBufFmt(sb, "%s\n", g->lineDir);
    g->lineFresh = false;
    fcBufFmt(sb, "    %s\n", text);
}
static void xEmitFn(FgEmit* e, const char* text) { xE((FgSC*)e->buf, text); }
static FgEmit xEmitter(FgSC* x) { FgEmit e; e.fn = xEmitFn; e.buf = (FcBuf*)x; e.indent = NULL; return e; }
#define E(...) xE(x, fcFmt(g->c, __VA_ARGS__))
#define ES(text) xE(x, (text))
static const char* xS(FgSC* x, int k) {
    FgEmit e = xEmitter(x);
    if (k >= 0 && k < 64 && st->alias[k]) MaterializeAlias(g, st, k, &e);
    if (k >= 0 && k < 64 && ((st->scalar >> k) & 1)) Materialize(g, f, st, k, &e);
    return SlotName(g, st->region, k);
}
static const char* xSR(FgSC* x, int k) { return k >= 0 && k < 64 && st->alias[k] ? st->alias[k] : xS(x, k); }
static const char* xT(FgSC* x, int k) { if (k >= 0 && k < 64) st->alias[k] = NULL; return xS(x, k); }
static const char* xVR(FgSC* x, int k) { return k >= 0 && k < 64 && st->alias[k] ? st->alias[k] : SlotName(g, st->region, k); }
#define S(k) xS(x, (k))
#define SR(k) xSR(x, (k))
#define T(k) xT(x, (k))
#define VR(k) xVR(x, (k))
/* The value of an assignment (`obj.f = v` leaves v): nothing when a Pop comes right after it (a statement), an alias stays one, else a copy */
static void xAssignValue(FgSC* x, int target, int source) {
    int at = f->indexOf[ins->addr];
    const char* from;
    if (at > 0 && at < f->decoded.n && f->decoded.p[at].op == OP_Pop && !fgISetHas(g->c, f->targets, f->decoded.p[at].addr)) { if (target < 64) st->alias[target] = NULL; return; }
    if (source < 64 && target < 64 && st->alias[source]) { st->alias[target] = st->alias[source]; return; }
    from = SR(source);
    E("%s = %s;", T(target), from);
}
static void xCheck(FgSC* x) { if (!g->usesExceptions || !sb) return; E("if (FIRE_UNLIKELY(g_unwind.active)) goto %s;", ExitLabel(g, f, st, locals)); }
static void xCheckNative(FgSC* x) { if (!g->usesExceptions || !sb) return; E("if (FIRE_UNLIKELY(g_unwind.active)) { fire_cbRethrow(); goto %s; }", ExitLabel(g, f, st, locals)); }
#define Check() xCheck(x)
#define CheckNative() xCheckNative(x)
static void xForgetArray(FgSC* x, const char* v) {
    int i, n = 0;
    const char** kept;
    if (!fgFlowHasArray(st, v)) return;
    kept = (const char**)fbAlloc(&g->flowArena, sizeof(char*) * (size_t)(st->arrayCount + 1));
    for (i = 0; i < st->arrayCount; i++) if (!streq0(st->arrays[i], v)) kept[n++] = st->arrays[i];
    st->arrays = kept; st->arrayCount = n;
}
static const char* xCheckIn(FgSC* x) { return g->usesExceptions && sb ? fcFmt(g->c, " if (FIRE_UNLIKELY(g_unwind.active)) goto %s;", ExitLabel(g, f, st, locals)) : ""; }
static const char* xCheckExit(FgSC* x) { return g->usesExceptions && sb ? ExitLabel(g, f, st, locals) : NULL; }
static const char* xThrower(FgSC* x, const char* stmt) { return g->usesExceptions && sb ? fcFmt(g->c, "{ %s goto %s; }", stmt, ExitLabel(g, f, st, locals)) : stmt; }
static void xCheckIf(FgSC* x, const char* failed) { if (!g->usesExceptions || !sb) return; E("if (FIRE_UNLIKELY(%s) && FIRE_UNLIKELY(g_unwind.active)) goto %s;", failed, ExitLabel(g, f, st, locals)); }
static void xJumpOnCondition(FgSC* x, const char* value, bool jumpIfTrue, int target) {
    const char* exit;
    if (!g->usesExceptions || !sb) { E("if (%struthy(%s)) goto L%d;", jumpIfTrue ? "" : "!", value, target); return; }
    exit = ExitLabel(g, f, st, locals);
    if (jumpIfTrue) E("if (truthy(%s)) { goto L%d; } if (FIRE_UNLIKELY(g_unwind.active)) goto %s;", value, target, exit);
    else E("if (!truthy(%s)) { if (FIRE_UNLIKELY(g_unwind.active)) goto %s; goto L%d; }", value, exit, target);
}
static void xNeed(FgSC* x, int n) { if (d < n) FG_NOTSUP("stack underflow at %d in %s", ins->addr, x->fn); }
#define Need(n) xNeed(x, (n))
static FgStep xNext(FgSC* x) { FgStep r; memset(&r, 0, sizeof r); st->depth = d; r.fallsThrough = true; return r; }
#define Next() xNext(x)
static const char* xStr(FgSC* x, int constIdx) { return fgConstStr(x->chunk, constIdx); }
#define Str(k) xStr(x, (k))
/* What is created or handed back at this point belongs to the statement temporaries of the innermost scope (SPEC 2.1); a slot claims it. */
static const char* xOwnerList(FgSC* x) {
    int home;
    if (st->scopeCount - 1 < 64) st->tempsDirty |= 1ULL << (st->scopeCount - 1);
    home = st->scopes[st->scopeCount - 1].id;
    fgISetAdd(g->c, f->needsList, home);
    fgISetAdd(g->c, f->needsList, TempOf(home));
    return ListName(g, TempOf(home));
}
#define OwnerList() xOwnerList(x)
static const char* xScopeList(FgSC* x) { fgISetAdd(g->c, f->needsList, st->scopes[st->scopeCount - 1].id); return ListName(g, st->scopes[st->scopeCount - 1].id); }
#define ScopeList() xScopeList(x)
static void xAccessCheck(FgSC* x, const FcRtClass* declaring, int access, const char* message) {
    if (!CheckAccess || AccessAllowed(f->chunk->ownerClass, declaring, access)) return;
    E("accessDenied(%s);", CString(g, message));
    Check();
}
static void xRequireSelf(FgSC* x) { if (!fgHasSelf(f) && f->kind != FK_Lambda) FG_NOTSUP("'this' outside of an instance member (%s at %d)", x->fn, ins->addr); }
/* `try take x` (SPEC 2.2) */
static const char* xTryTakeStmt(FgSC* x, const char* value, const char* take) {
    FcList* lists;
    const char* decl;
    int i;
    if (!g->usesTryDepth) { g->usesTryDepth = true; g->version++; }
    f->usesTryTake = true;
    for (i = 0; i < st->scopeCount; i++) { fgISetAdd(g->c, f->needsList, st->scopes[i].id); fgISetAdd(g->c, f->needsList, TempOf(st->scopes[i].id)); }
    lists = CallLists(g, f, st);
    decl = lists->count == 0 ? "" : CArray(g, "OwnList*", "TL_", lists);
    if (!fcDictHas(f->scalars, "took_")) fcDictPut(f->scalars, "took_", (void*)"bool");
    return fcFmt(g->c, "{ %stook_ = tryTakeOwned(%s, %s, %d, %s, MYARGS); if (took_) %s }", decl, value, lists->count == 0 ? "NULL" : "TL_", lists->count, fgHasSelf(f) ? "self" : "Undef()", take);
}

/* the static types of the stack slots */
static bool xR(FgSC* x, int k) { return k >= 64 || ((st->refs >> k) & 1ULL) != 0; }
static void xSetR(FgSC* x, int k, bool value) {
    if (k >= 64) return;
    st->borrowed &= ~(1ULL << k);
    if (value) st->refs |= 1ULL << k; else st->refs &= ~(1ULL << k);
}
static int xTy(FgSC* x, int k) { return k < 0 || k >= 64 ? TypeUnknown : st->types[k]; }
#define R(k) xR(x, (k))
#define SetR(k, v) xSetR(x, (k), (v))
#define Ty(k) xTy(x, (k))
#define K(k) KindOfType(Ty(k))
#define U(k) UnitOfType(Ty(k))
#define BK(k) BaseKindOfType(Ty(k))
static bool xObjSlot(FgSC* x, int k) { return BaseKindOfType(Ty(k)) == KindObj && (Ty(k) & TypeNone) == 0 && !InterfaceOfType(g, Ty(k)); }
#define ObjSlot(k) xObjSlot(x, (k))
static void xSetT(FgSC* x, int k, int type) { if (k >= 0 && k < 64) st->types[k] = type; }
#define SetT(k, t) xSetT(x, (k), (t))
static bool IsNum(int kind) { return kind == KindInt || kind == KindFloat; }
/* the element of the typed array in slot k (KindArr, maybe undefined), 0: none known */
static int xArrSlot(FgSC* x, int k) { return BK(k) == KindArr && (Ty(k) & TypeNone) == 0 ? U(k) : 0; }
#define ArrSlot(k) xArrSlot(x, (k))
static bool xSlotScalars(FgSC* x) { return ScalarsOn && !st->region && d < 63; }
#define SlotScalars() xSlotScalars(x)
static bool xIsScalar(FgSC* x, int k) { return k >= 0 && k < 64 && ((st->scalar >> k) & 1) != 0; }
#define IsScalar(k) xIsScalar(x, (k))
#define NarrowInt(k) IsNarrowIntType(Ty(k))
static const char* xSN(FgSC* x, int k) { return IsScalar(k) ? ScalarName(g, f, st, k) : fcFmt(g->c, "(FireInt)%s.i", VR(k)); }
static const char* xSB(FgSC* x, int k) { return IsScalar(k) ? ScalarName(g, f, st, k) : fcFmt(g->c, "(%s.i != 0)", VR(k)); }
#define SN(k) xSN(x, (k))
#define SB(k) xSB(x, (k))
static bool xWordOperand(FgSC* x, int k) { return K(k) == KindWord || (NarrowInt(k) && U(k) == 0); }
#define WordOperand(k) xWordOperand(x, (k))
static const char* xSW(FgSC* x, int k) {
    if (K(k) == KindWord) return IsScalar(k) ? ScalarName(g, f, st, k) : fcFmt(g->c, "%s.w", VR(k));
    return fcFmt(g->c, "(FireWord)(uint64_t)(int64_t)%s", SN(k));
}
#define SW(k) xSW(x, (k))
#define RealOperand(k) (K(k) == KindFloat || K(k) == KindInt)
/* the unit of `a op b` the typed arithmetic gives (-1: the units do not fit) */
static int ArithUnit(const char* op, int ua, int ub) {
    if (streq0(op, "+") || streq0(op, "-") || streq0(op, "%")) return ua == ub ? ua : -1;
    if (streq0(op, "*")) return ua == 0 ? ub : ub == 0 ? ua : -1;
    if (streq0(op, "/")) return ub == 0 ? ua : -1;
    return -1;
}
static const char* xSF(FgSC* x, int k) {
    if (K(k) == KindFloat) return IsScalar(k) ? ScalarName(g, f, st, k) : fcFmt(g->c, "%s.f", VR(k));
    return NarrowInt(k) ? fcFmt(g->c, "(Real)%s", SN(k)) : fcFmt(g->c, "(Real)%s.i", VR(k));
}
#define SF(k) xSF(x, (k))
static const char* xScalarValue(FgSC* x, const char* v, int k) {
    const char* t = ScalarVarCType(g, f, v);
    return streq0(t, "FireWord") ? SW(k) : streq0(t, "Real") ? SF(k) : SN(k);
}
/* slot k takes `expr` into its shadow: its Value is stale from here */
static void xSetScalar(FgSC* x, int k, int type, const char* expr) {
    const char* shadow;
    SetT(k, type); SetR(k, false); g->kindsKept = true;
    if (k >= 0 && k < 64) st->alias[k] = NULL;
    shadow = ScalarName(g, f, st, k);
    E("%s = %s;", shadow, expr);
    st->scalar |= 1ULL << k;
}
#define SetScalar(k, t, e) xSetScalar(x, (k), (t), (e))
/* a constant into the slot d */
static void xPushConstant(FgSC* x, const FcConst* c) {
    if (SlotScalars() && c->kind == CK_Float) { SetScalar(d, TypeOfConstant(g, c), fcFmt(g->c, "(Real)%s", DoubleLiteral(g, fgConstF(c), g->program->floatWidth == 32))); return; }
    if (SlotScalars() && c->kind == CK_Word) { SetScalar(d, TypeOf(KindWord, 0), fcFmt(g->c, "(FireWord)%" PRIu64 "ULL", c->w)); return; }
    if (SlotScalars() && IsNarrowIntType(TypeOfConstant(g, c))) {
        int64_t v = c->i;
        SetScalar(d, TypeOfConstant(g, c), v == INT64_MIN ? "(FireInt)(-9223372036854775807LL - 1)" : fcFmt(g->c, "(FireInt)%" PRId64 "LL", v));
        return;
    }
    E("%s = %s;", S(d), Constant(g, c));
    SetR(d, c->kind == CK_String);
    SetT(d, TypeOfConstant(g, c)); g->kindsKept = true;
}
#define ScalarVar(v) ScalarVarOf(g, f, (v))
static const char* xNum(FgSC* x, int k) { return BK(k) == KindFloat ? fcFmt(g->c, "%s.f", S(k)) : fcFmt(g->c, "(Real)%s.i", S(k)); }
static const char* xDbl(FgSC* x, int k) { return BK(k) == KindFloat ? fcFmt(g->c, "(double)%s.f", S(k)) : fcFmt(g->c, "(double)%s.i", S(k)); }
#define Num(k) xNum(x, (k))
#define Dbl(k) xDbl(x, (k))
/* A parameter in the prologue before its GuardLocal still holds what the caller passed. */
static bool xRawParam(FgSC* x, const char* variable) {
    int rawIndex;
    void* guardAt;
    if (variable[0] != 'P') return false;
    rawIndex = atoi(variable + 1);
    if (f->cabi && rawIndex < f->cabi->paramCount && f->cabi->paramType[rawIndex] != 0) return false;
    return fgIMapTry(f->guardAddr, rawIndex, &guardAt) && ins->addr < FC_VALI(guardAt);
}
/* What a load of a variable certainly gives. */
static int xLoadType(FgSC* x, const char* variable) {
    int type;
    if (variable[0] == 'G' && f->kind != FK_Main) return TypeUnknown;
    return fgVarTypeTry(g, VarKey(g, f, variable), &type) ? type : TypeUnknown;
}
/* Two different static units that convert by a factor: the C++ expression of the factor from `from` to `to`; NULL otherwise. */
static const char* xLinearFactor(FgSC* x, int from, int to) {
    const FcU* a = UnitById(g, from);
    const FcU* b = UnitById(g, to);
    if (from == 0 || to == 0 || !fcUIsLinear(a) || !fcUIsLinear(b) || !fcUCompatible(a, b)) return NULL;
    fcSetAdd(g->unitFactorCaches, fcFmt(g->c, "%d,%d", from, to));
    return fcFmt(g->c, "unitFactorCached(&%skUF_%d_%d, %s, %s)", Px, from, to, UnitC(g, from), UnitC(g, to));
}
/* After a call: whatever came back belongs to the innermost scope now - unless it is certainly a number or a bool. */
static void xAdoptResult(FgSC* x, int slot, int type) {
    if (IsPlainType(type)) { SetR(slot, false); SetT(slot, type); g->kindsKept = true; return; }
    E("adopt(%s, &%s);", S(slot), OwnerList()); SetR(slot, true);
    SetT(slot, type); g->kindsKept = true;
}
#define AdoptResult(slot, type) xAdoptResult(x, (slot), (type))
static const char* xDefaultsList(FgSC* x, const FcProto* proto, int argc) { return argc < proto->paramCount ? fcFmt(g->c, "&%s", OwnerList()) : ""; }
/* Stores into a variable: count what it now holds, let go of what it held. */
static void xStoreVar(FgSC* x, const char* variable, int slot) {
    const char* stored;
    const char* key;
    const char* sec;
    const char* secEnd;
    const char* share;
    bool valueRef, varRef;
    FgEmit e = xEmitter(x);
    if (ScalarVar(variable)) { E("%s = %s;", variable, xScalarValue(x, variable, slot)); return; }
    stored = SR(slot);
    if (streq0(stored, variable)) return;
    xForgetArray(x, variable);
    MaterializeAliases(g, st, &e, variable);
    key = VarKey(g, f, variable);
    valueRef = R(slot);
    if (valueRef) MarkVarRef(g, key);
    varRef = VarRef(g, key);
    sec = SectionFor(g, variable);
    secEnd = SectionEnd(g, variable);
    share = *sec ? "FIRE_DOM_SHARE(n); " : "";
    if (*sec && !IsPlainType(Ty(slot))) {
        const char* move = fcFmt(g->c, "Value n = %s; if (FIRE_LIKELY(!g_isThread || toMainGlobals(&n, NULL, \"%s\"))) ", stored, fcStartsWith(variable, "SF") ? "A static field" : "A global variable");
        if (valueRef) E("{ %s%s{ Value o = %s; %s = n; %sretain(n); release(o); } %s}", sec, move, variable, variable, share, secEnd);
        else if (varRef) E("{ %s%s{ Value o = %s; %s = n; release(o); } %s}", sec, move, variable, variable, secEnd);
        else E("{ %s%s%s = n; %s}", sec, move, variable, secEnd);
        Check();
        return;
    }
    if (valueRef) E("{ %sValue n = %s; Value o = %s; %s = n; %sretain(n); release(o); %s}", sec, stored, variable, variable, share, secEnd);
    else if (varRef) E("{ %sValue o = %s; %s = %s; release(o); %s}", sec, variable, variable, stored, secEnd);
    else if (*sec) E("{ %s%s = %s; %s}", sec, variable, stored, secEnd);
    else E("%s = %s;", variable, stored);
}
#define StoreVar(v, k) xStoreVar(x, (v), (k))
/* The variables of a scope let go of their values when the scope is left. */
static void xReleaseScopeVariables(FgSC* x, int scopeId, bool reset) {
    FcList* vars = ScopeVariables(g, f, scopeId, locals);
    int i;
    for (i = 0; i < vars->count; i++) {
        const char* v = (const char*)vars->items[i];
        if (VarRef(g, VarKey(g, f, v))) E(reset ? "release(%s); %s = Undef();" : "release(%s);", v, v);
    }
}

/* The prefix CopyArgs in front of the call: per argument 4 bits */
static int64_t xArgMask(FgSC* x) {
    int at = f->indexOf[ins->addr] - 1;
    if (at > 0 && f->decoded.p[at - 1].op == OP_ArgNames) at--;
    if (at == 0 || f->decoded.p[at - 1].op != OP_CopyArgs) return 0;
    return CopyMask(&f->decoded.p[at - 1]);
}
#define ArgMask() xArgMask(x)
/* `f(1, size: 2)`: the names of the last arguments (OpCode.ArgNames right in front of the call), NULL without. */
static FcList* xNamesBefore(FgSC* x) {
    int at = f->indexOf[ins->addr] - 1;
    return at > 0 && f->decoded.p[at - 1].op == OP_ArgNames ? fgSplit(g->c, Str(f->decoded.p[at - 1].a[0]), '|') : NULL;
}
static const char* xArgs(FgSC* x, int first, int count) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = first; i < first + count; i++) fcBufFmt(&b, ", %s", SR(i));
    return fcBufDone(g->c, &b);
}
#define Args(first, count) xArgs(x, (first), (count))
/* A call with named arguments of a callee that is known; false when the names do not fit the callee. */
static bool xNamedCallArgs(FgSC* x, FcProto* proto, int first, int argc, FcList* names, int64_t mask, const char* self, const char** preOut, const char** argsOut) {
    int positional = argc - names->count, i, k, p;
    int* argOf;
    FcBuf pre = {0};
    FcBuf args = {0};
    bool namedCalls = false;
    if (!proto->paramNames) return false;
    if (positional > proto->paramCount) return false;
    argOf = (int*)fcAlloc(g->c, sizeof(int) * (size_t)(proto->paramCount + 1));
    for (p = 0; p < proto->paramCount; p++) argOf[p] = -1;
    for (i = 0; i < positional; i++) argOf[i] = i;
    for (k = 0; k < names->count; k++) {
        int at = -1;
        for (p = 0; p < proto->paramNames->count; p++) if (streq0((const char*)proto->paramNames->items[p], (const char*)names->items[k])) { at = p; break; }
        if (at < 0 || argOf[at] >= 0) return false;
        argOf[at] = positional + k;
    }
    fcBufStr(&pre, ""); fcBufStr(&args, "");
    for (p = 0; p < proto->paramCount; p++) {
        int j = argOf[p];
        FcProto* defaultProto;
        const char* constant;
        if (j >= 0) {
            fcBufFmt(&args, ", %s", ((mask >> (4 * j)) & 15) == 3 && ((proto->refMask >> p) & 1) == 0 ? fcFmt(g->c, "ptrRead(%s)", S(first + j)) : S(first + j));
            continue;
        }
        defaultProto = p < proto->paramDefaults->count ? (FcProto*)proto->paramDefaults->items[p] : NULL;
        if (!defaultProto) return false;
        if ((constant = ConstantDefault(g, defaultProto)) != NULL) fcBufFmt(&pre, "Value dv%d = %s; ", p, constant);
        else { fcBufFmt(&pre, "Value dv%d = adoptV(%s(%s), &%s); ", p, GetFunc(g, defaultProto, FK_Init, 0)->name, self, OwnerList()); namedCalls = true; }
        fcBufFmt(&args, ", dv%d", p);
    }
    *preOut = KeepArgIn(g, fcBufDone(g->c, &pre), namedCalls);
    *argsOut = fcBufDone(g->c, &args);
    return true;
}
/* The ArgumentException of VM.FillNamedArgs when the names do not fit the callee. */
static const char* NamedMismatch(FgGen* gg, const FcProto* proto, int argc, FcList* names) {
    int positional = argc - names->count, i, k;
    bool* given;
    if (positional > proto->paramCount) return fcFmt(gg->c, "Wrong number of arguments: expected %d, got %d.", proto->paramCount, argc);
    given = (bool*)fcAlloc(gg->c, sizeof(bool) * (size_t)(proto->paramCount + 1));
    for (i = 0; i < positional; i++) given[i] = true;
    for (k = 0; k < names->count; k++) {
        const char* name = (const char*)names->items[k];
        int at = -1;
        if (proto->paramNames) for (i = 0; i < proto->paramNames->count; i++) if (streq0((const char*)proto->paramNames->items[i], name)) { at = i; break; }
        if (at < 0) return fcFmt(gg->c, "There is no parameter '%s'.", name);
        if (given[at]) return fcFmt(gg->c, "The parameter '%s' is given twice.", name);
        given[at] = true;
    }
    for (i = 0; i < proto->paramCount; i++)
        if (!given[i] && (i >= proto->paramDefaults->count || !proto->paramDefaults->items[i]))
            return fcFmt(gg->c, "The parameter '%s' has no value (and no default value).", proto->paramNames ? (const char*)proto->paramNames->items[i] : "");
    return "Named arguments do not fit.";
}
static void xNamedCallArgsOrFail(FgSC* x, FcProto* proto, int first, int argc, FcList* names, int64_t mask, const char* self, const char* what, const char** pre, const char** args) {
    if (!xNamedCallArgs(x, proto, first, argc, names, mask, self, pre, args)) FG_NOTSUP("the named arguments %s do not fit %s", fgJoin(g->c, names, ", "), what);
}
/* The arguments for a callee that is known: an address goes to a `ref` parameter, the others get the value. */
static const char* xCallArgs(FgSC* x, int first, int count, int64_t mask, uint32_t calleeRefs) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = 0; i < count; i++)
        fcBufFmt(&b, ", %s", ((mask >> (4 * i)) & 15) == 3 && ((calleeRefs >> i) & 1) == 0 ? fcFmt(g->c, "ptrRead(%s)", SR(first + i)) : SR(first + i));
    return fcBufDone(g->c, &b);
}
#define CallArgs(first, count, mask, refs) xCallArgs(x, (first), (count), (mask), (refs))
/* `f(g())` (SPEC 2.1): the result of `g` that is still the caller's belongs to the callee - it travels in the argument list of the call */
static void xArgsBefore(FgSC* x, int first, int argc, int64_t mask) {
    int i;
    x->argsTravel = 0;
    for (i = 0; i < (argc < 16 ? argc : 16); i++) {
        int bits = (int)((mask >> (4 * i)) & 15);
        if (bits == 1 || bits == 2 || bits == 5 || bits == 6 || bits == 7) x->argsTravel++;
        if (bits == 4 || bits == 7) {
            if (bits == 4 && (!R(first + i) || IsScalar(first + i) || K(first + i) == KindInt || K(first + i) == KindFloat || K(first + i) == KindBool || K(first + i) == KindWord)) continue;
            if (bits == 4) x->argsTravel++;
            f->needsArgList = true;
            E("reownArg(%s, &%s, &AL);", S(first + i), OwnerList());
        }
        else if (bits == 6) {
            f->needsArgList = true;
            if (IsSetterFunc(g, f)) E("claimArg(%s, MYAL, &%s, &AL);", S(first + i), OwnerList());
        }
        else if (bits == 8) {
            f->needsArgList = true;
            x->argsTravel++;
            ES(xTryTakeStmt(x, S(first + i), fcFmt(g->c, "takeArg(%s, &%s, &AL);", S(first + i), OwnerList())));
            Check();
        }
        else if (bits == 5) {
            f->needsArgList = true;
            E("if (takeAlive(%s)) takeArg(%s, &%s, &AL);", S(first + i), S(first + i), OwnerList());
            Check();
        }
        else if (bits == 1 || bits == 2) {
            f->needsArgList = true;
            E("%s = copyArg(%s, %s, &%s, &AL);", S(first + i), S(first + i), bits == 2 ? "true" : "false", OwnerList());
            Check();
        }
    }
    if ((g->usesTryDepth || g->usesThreads) && x->argsTravel > 0) ES("g_argIn = &AL;");
    else if (g->usesThreads) ES("g_argIn = NULL;");
}
#define ArgsBefore(first, argc, mask) xArgsBefore(x, (first), (argc), (mask))
/* An argument of a typed native in C: the C number of the slot when its type proves it, else the lenient conversion of its Value. */
static const char* xNativeArgOf(FgSC* x, int k, const char* cType) {
    if (IsScalar(k)) {
        int sk = K(k);
        const char* sh = sk == KindFloat ? SF(k) : sk == KindBool ? SB(k) : sk == KindWord ? SW(k) : SN(k);
        return streq0(cType, "bool") ? fcFmt(g->c, "(%s != 0)", sh) : fcFmt(g->c, "(%s)%s", cType, sh);
    }
    if (streq0(cType, "Real")) {
        if (K(k) == KindFloat && IsScalar(k)) return SF(k);
        if (NarrowInt(k) && IsScalar(k)) return fcFmt(g->c, "(Real)%s", SN(k));
        if (BK(k) == KindFloat) return fcFmt(g->c, "%s.f", VR(k));
        if (BK(k) == KindInt) return fcFmt(g->c, "(Real)%s.i", VR(k));
        return fcFmt(g->c, "FIRE_NATR(%s)", VR(k));
    }
    if (streq0(cType, "bool")) {
        if (K(k) == KindBool) return SB(k);
        return fcFmt(g->c, "(%s.i != 0)", VR(k));
    }
    if (NarrowInt(k) && IsScalar(k)) return fcFmt(g->c, "(%s)%s", cType, SN(k));
    if (BK(k) == KindInt || BK(k) == KindBool) return fcFmt(g->c, "(%s)%s.i", cType, VR(k));
    return fcFmt(g->c, "(%s)FIRE_NATI(%s)", cType, VR(k));
}
static bool xProvenCArgs(FgSC* x, const FgCAbi* abi, int first, int argc) {
    int i;
    for (i = 0; i < argc; i++) {
        int k = first + i, type = abi->paramType[i], unit;
        bool proven;
        if (type == 0) continue;
        unit = UnitOfType(type);
        switch (KindOfType(type)) {
            case KindInt: proven = NarrowInt(k) && U(k) == unit; break;
            case KindFloat: proven = (K(k) == KindFloat || NarrowInt(k)) && U(k) == unit; break;
            default: proven = K(k) == KindWord; break;
        }
        if (!proven || abi->paramDemand[i]) return false;
    }
    return true;
}
static FcList* xCAbiArgs(FgSC* x, const FgCAbi* abi, int first, int argc) {
    FcList* result = fcListNew(g->c);
    int i;
    for (i = 0; i < argc; i++) {
        int k = first + i, type = abi->paramType[i], kind, unit;
        bool proven;
        if (type == 0) { fcListAdd(g->c, result, (void*)S(k)); continue; }
        kind = KindOfType(type);
        unit = UnitOfType(type);
        switch (kind) {
            case KindInt: proven = NarrowInt(k) && U(k) == unit; break;
            case KindFloat: proven = (K(k) == KindFloat || NarrowInt(k)) && U(k) == unit; break;
            default: proven = K(k) == KindWord; break;
        }
        if (!proven) {
            if (abi->paramDemand[i]) {
                E("%s = %s;", S(k), AdaptUnitCall(g, S(k), abi->paramDemand[i]));
                Check();
            }
            SetScalar(k, type, CNumberOf(g, type, fcFmt(g->c, "(%s)", GuardKindCall(g, S(k), CDeclared(type), abi->paramWhat[i]))));
            Check();
        }
        fcListAdd(g->c, result, (void*)(kind == KindFloat ? SF(k) : kind == KindWord ? SW(k) : SN(k)));
    }
    return result;
}
static void xArgsAfter(FgSC* x, int argc, int64_t mask, const char* result) {
    int i;
    if (x->argsTravel == 0) { x->argsTravel = -1; return; }
    x->argsTravel = -1;
    for (i = 0; i < (argc < 16 ? argc : 16); i++) {
        int bits = (int)((mask >> (4 * i)) & 15);
        if (bits == 1 || bits == 2 || bits == 4 || bits == 5 || bits == 6 || bits == 7 || bits == 8) { E("finishArgs(%s, &AL);", result); return; }
    }
}
#define ArgsAfter(argc, mask, result) xArgsAfter(x, (argc), (mask), (result))

/* A typed array stored in a variable: its state into C helpers (Flow.Arrays) */
static void xKnowArray(FgSC* x, const char* v, int slot) {
    int code, elem;
    const char** arr;
    xForgetArray(x, v);
    if (g->program->optimize != 0 || DebugBuild || !AliasOn(g, f) || st->region || !(v[0] == 'P' || v[0] == 'B')) return;
    code = ArrSlot(slot);
    if (code == 0) return;
    elem = fgAeTypeOf(code);
    if (ArrElemKind(elem) == KindUnknown) return;
    fcDictPut(f->scalars, fcFmt(g->c, "%s_ok", v), (void*)"bool");
    fcDictPut(f->scalars, fcFmt(g->c, "%s_a", v), (void*)"Arr*");
    fcDictPut(f->scalars, fcFmt(g->c, "%s_len", v), (void*)"uint32_t");
    fcDictPut(f->scalars, fcFmt(g->c, "%s_d", v), (void*)"void*");
    if (g->usesThreads) { fcDictPut(f->scalars, fcFmt(g->c, "%s_dom", v), (void*)"bool"); fcDictPut(f->scalars, fcFmt(g->c, "%s_seen", v), (void*)"uint32_t"); }
    fgSetKnownArrayElem(g, f, v, elem);
    ES(ArrayCheckText(g, v, elem));
    arr = (const char**)fbAlloc(&g->flowArena, sizeof(char*) * (size_t)(st->arrayCount + 2));
    if (st->arrayCount) memcpy(arr, st->arrays, sizeof(char*) * (size_t)st->arrayCount);
    arr[st->arrayCount] = v;
    st->arrays = arr; st->arrayCount++;
}
/* the variable a slot is an alias of, when the state of its typed array (of this element) is known */
static const char* xKnownArray(FgSC* x, int k, int elem) {
    const char* av;
    return k >= 0 && k < 64 && (av = st->alias[k]) != NULL && fgFlowHasArray(st, av) && fgAeTypeOf(ArrSlot(k)) == elem ? av : NULL;
}
