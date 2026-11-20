/* native/forge/gen/cabi.c - src/fire.Native/CppGenerator.CAbi.cs (commit 808e9788): Ebene 2, Stufe 2b - a method whose parameters or return value are
 * declared `defined int` / `defined float` / `defined word` (`defined bool` for the return value) gets a body with those C types (`fN_c`). */

static bool fgCAbiActive(const FgCAbi* a) { int i; if (a->retType != 0) return true; for (i = 0; i < a->paramCount; i++) if (a->paramType[i] != 0) return true; return false; }

static bool fgIsReflectionHelper(const char* cls) { return streq0(cls, "Reflect") || streq0(cls, "Type") || streq0(cls, "Member") || streq0(cls, "Selector") || streq0(cls, "Attribute"); }
static FcDict* ReflectionMethodNames(FgGen* g) {
    static const char* const helpers[] = { "Reflect", "Type", "Member", "Selector", "Attribute" };
    size_t k;
    if (g->reflectionMethodNames) return g->reflectionMethodNames;
    g->reflectionMethodNames = fcDictNew(g->c, FC_KEY_STR);
    for (k = 0; k < sizeof helpers / sizeof helpers[0]; k++) {
        FcRtClass* rc = fgClassOf(g, helpers[k]);
        if (rc) FC_DICT_EACH(rc->methods, m) fcSetAdd(g->reflectionMethodNames, m->key);
    }
    return g->reflectionMethodNames;
}

static FgCAbi* CAbiParams(FgGen* g, FgFunc* f) {
    FcProto* proto = f->proto;
    const char* mname;
    int margc, i;
    FgCode code;
    FgCAbi* abi;
    if (!ScalarsOn || Wrapped || Modular || LibraryRole || f->external || f->exportName || !(f->kind == FK_Method || f->kind == FK_Static)) return NULL;
    if (f->specParams || f->specTArgs || f->specClassOwner || !proto || f->chunk->handlers->count > 0) return NULL;
    if (proto->specializations) return NULL;
    if (MethodKey(g, proto, &mname, &margc) && fcStartsWith(mname, "set_")) return NULL;
    code = fgDecode(g->c, f->chunk);
    for (i = 0; i < code.n; i++) if (code.p[i].op == OP_TakeCallTypeArgs) return NULL;
    if (fgHasSelf(f))
        for (i = 0; i < code.n; i++)
            if (code.p[i].op == OP_GuardLocalType) { const char* tpc = FG_SPEC(fgConstStr(f->chunk, code.p[i].a[1]))->typeParamClass; if (tpc && *tpc) return NULL; }
    abi = (FgCAbi*)fcAlloc(g->c, sizeof *abi);
    abi->paramCount = fgParamCount(f);
    abi->paramType = (int*)fcAlloc(g->c, sizeof(int) * (size_t)(abi->paramCount + 1));
    abi->paramWhat = (const char**)fcAlloc(g->c, sizeof(void*) * (size_t)(abi->paramCount + 1));
    abi->paramDemand = (const char**)fcAlloc(g->c, sizeof(void*) * (size_t)(abi->paramCount + 1));
    for (i = 0; i < code.n; i++) {
        FgInstr* c = &code.p[i];
        int k, declared, kind, unit = 0, type;
        if (!(c->op == OP_GuardLocal && c->a[0] < abi->paramCount)) continue;
        k = c->a[0];
        if ((proto->refMask >> k) & 1) continue;
        declared = (((c->a[1] & 0x7F) + 1) | (c->a[1] & DK_UnitlessBit)) & 0xFF;
        if (!fgDkIsNonNull(declared)) continue;
        kind = KindOfDeclared(declared);
        if (kind == KindInt || kind == KindFloat) {
            const char* demand;
            if (c->a[3] == 0xFFFF) continue;
            demand = fgConstStr(f->chunk, c->a[3]);
            if (!streq0(demand, "unitless")) {
                if (fcStartsWith(fgTrimStart(demand, '#'), "any") || (unit = DemandedUnitId(g, demand)) <= 0) continue;
                abi->paramDemand[k] = demand;
            }
        }
        type = kind == KindInt && !fgDkKeepsWidth(declared) ? TypeOf(KindInt, unit) | NarrowBit : kind == KindFloat ? TypeOf(KindFloat, unit) : kind == KindWord ? TypeOf(KindWord, 0) : 0;
        abi->paramType[k] = type;
        abi->paramWhat[k] = fgConstStr(f->chunk, c->a[2]);
    }
    return abi;
}
/* The C interface of a method, or NULL when it has none */
static FgCAbi* CAbiOf(FgGen* g, FgFunc* f) {
    FgCAbi* abi;
    void* v;
    if (Wrapped) return NULL;
    if (!fcDictTry(g->cAbiParams, f, &v)) { abi = CAbiParams(g, f); fcDictPut(g->cAbiParams, f, abi); }
    else abi = (FgCAbi*)v;
    if (!abi) return NULL;
    abi->retType = fcDictTry(g->cRet, f, &v) ? FC_VALI(v) : 0;
    return fgCAbiActive(abi) ? abi : NULL;
}
/* After a round's code of the method: the C return value it can have */
static void NoteCReturn(FgGen* g, FgFunc* f) {
    void* v;
    FgCAbi* abi;
    int ret = 0, old;
    FgCode code = f->decoded;
    if (!fcDictTry(g->cAbiParams, f, &v) || !(abi = (FgCAbi*)v)) return;
    if (code.n >= 3 && code.p[code.n - 1].op == OP_Return && code.p[code.n - 2].op == OP_GuardTop && fgDkIsNonNull((code.p[code.n - 2].a[0] + 1) & 0xFF)
        && code.p[code.n - 3].op == OP_LoadConst && fgConst(f->chunk, code.p[code.n - 3].a[0])->kind == CK_Undefined) {
        int rt = ReturnType(g, f);
        int kind = KindOfType(rt);
        if (IsNarrowIntType(rt) || kind == KindFloat || kind == KindBool || kind == KindWord) ret = kind == KindBool || kind == KindWord ? TypeOf(kind, 0) : rt & ~(CutBit | NullableBit | TypeNone);
    }
    old = fcDictTry(g->cRet, f, &v) ? FC_VALI(v) : 0;
    if (old == ret) return;
    if (ret == 0) fcDictRemove(g->cRet, f); else if (fcDictHas(g->cRet, f)) *fcDictRef(g->cRet, f) = FC_IVAL(ret); else fcDictPut(g->cRet, f, FC_IVAL(ret));
    g->version++;
}
static const char* CName(FgGen* g, const FgFunc* f) { return fcFmt(g->c, "%s_c", f->name); }
static const char* CTypeOf(int type) { return ScalarCType(KindOfType(type)); }
static const char* CParamName(FgGen* g, FgFunc* f, int i) { const char* p = fcFmt(g->c, "P%d", i); return ScalarVarOf(g, f, p) ? p : fcFmt(g->c, "P%d_c", i); }
static const char* CSignature(FgGen* g, FgFunc* f, FgCAbi* abi) {
    FcList* ps = fcListNew(g->c);
    const char* inl;
    int i;
    if (fgHasSelf(f)) fcListAdd(g->c, ps, (void*)"Value self");
    for (i = 0; i < fgParamCount(f); i++)
        fcListAdd(g->c, ps, (void*)(abi->paramType[i] != 0 ? fcFmt(g->c, "%s %s", CTypeOf(abi->paramType[i]), CParamName(g, f, i)) : fcFmt(g->c, "Value P%d", i)));
    inl = f->proto && f->proto->specOwner && f->chunk->len <= 40 && !DebugBuild && g->program->optimize == 0 ? "FIRE_ALWAYS_INLINE static inline" : "static";
    return fcFmt(g->c, "%s %s %s(%s)", inl, abi->retType != 0 ? CTypeOf(abi->retType) : "Value", CName(g, f), ps->count == 0 ? "void" : fgJoin(g->c, ps, ", "));
}
/* What a function returns when it is left by an exception (the caller looks at the unwinding flag). */
static const char* UndefReturnOf(const FgCAbi* abi) {
    int k;
    if (!abi || abi->retType == 0) return "Undef()";
    k = KindOfType(abi->retType);
    return k == KindBool ? "false" : k == KindFloat ? "(Real)0" : k == KindWord ? "(FireWord)0" : "(FireInt)0";
}
/* The DeclaredKind a C parameter of this static type is checked against (`defined`, without a unit). */
static int CDeclared(int type) {
    int kind = KindOfType(type);
    return (((kind == KindInt ? TAG_Int : kind == KindFloat ? TAG_Float : TAG_Word) + 1) | DK_NonNullBit | (kind == KindWord || UnitOfType(type) != 0 ? 0 : DK_UnitlessBit)) & 0xFF;
}
/* The C number of a checked Value. */
static const char* CNumberOf(FgGen* g, int type, const char* value) {
    int k = KindOfType(type);
    return k == KindFloat ? fcFmt(g->c, "%s.f", value) : k == KindWord ? fcFmt(g->c, "%s.w", value) : fcFmt(g->c, "(FireInt)%s.i", value);
}
static int64_t CopyMask(const FgInstr* copy) { return (int64_t)copy->a[0] | ((int64_t)copy->a[1] << 16) | ((int64_t)copy->a[2] << 32) | ((int64_t)copy->a[3] << 48); }
/* A CopyArgs whose marks mean nothing for the C body */
static bool NumberCopyArgs(const FgInstr* copy, const FgCAbi* abi) {
    int64_t mask = CopyMask(copy);
    int k;
    for (k = 0; k < 16; k++) {
        int64_t bits = (mask >> (4 * k)) & 0xF;
        if ((bits == 1 || bits == 2) && (k >= abi->paramCount || abi->paramType[k] == 0)) return false;
        if (!(bits == 0 || bits == 1 || bits == 2 || bits == 4)) return false;
    }
    return true;
}
/* The method a CallStaticMethod calls when it has a C body (and the call can use it); NULL otherwise. */
static FgFunc* CAbiStaticCallee(FgGen* g, FgFunc* f, const FgInstr* ins) {
    const char* cls;
    const char* method;
    int argc, at;
    FcRtClass* rc;
    FcProto* proto;
    FgFunc* target;
    FgCAbi* abi;
    if (!ScalarsOn) return NULL;
    cls = fgConstStr(f->chunk, ins->a[0]); method = fgConstStr(f->chunk, ins->a[1]);
    argc = ins->a[2];
    if (fgIsReflectionHelper(cls)) return NULL;
    at = f->indexOf[ins->addr] - 1;
    if (at > 0 && (f->decoded.p[at - 1].op == OP_ArgNames || f->decoded.p[at - 1].op == OP_SetTypeArgs || f->decoded.p[at - 1].op == OP_OverloadHint)) return NULL;
    if (!(rc = fgClassOf(g, cls))) return NULL;
    if (FG_OVERLOADED(rc, method, argc) || !(proto = FG_FIND(rc, method, argc).proto) || !proto->isStatic || proto->paramCount != argc) return NULL;
    target = GetFunc(g, proto, FK_Static, 0);
    if (!(abi = CAbiOf(g, target))) return NULL;
    return at > 0 && f->decoded.p[at - 1].op == OP_CopyArgs && !NumberCopyArgs(&f->decoded.p[at - 1], abi) ? NULL : target;
}
/* Like CAbiStaticCallee for a CallMethod on an object of a known class whose method no derived class overrides (DirectMethod). */
static FgFunc* CAbiMethodCallee(FgGen* g, FgFunc* f, FgFlow* st, const FgInstr* ins) {
    int argc = ins->a[1], slot = st->depth - argc - 1, at, type;
    const char* method;
    FcProto* proto;
    FgFunc* target;
    if (!ScalarsOn) return NULL;
    if (slot < 0 || slot >= 64) return NULL;
    method = fgConstStr(f->chunk, ins->a[0]);
    at = f->indexOf[ins->addr] - 1;
    if (at > 0) { int op = f->decoded.p[at - 1].op; if (op == OP_ArgNames || op == OP_CopyArgs || op == OP_SetTypeArgs || op == OP_OverloadHint) return NULL; }
    type = st->types[slot];
    if (BaseKindOfType(type) != KindObj || (type & TypeNone) != 0 || InterfaceOfType(g, type)) return NULL;
    if (g->usesReflection && fcDictHas(ReflectionMethodNames(g), method)) return NULL;
    if (!(proto = DirectMethod(g, ObjClass(g, type), method, argc, f)) || proto->paramCount != argc) return NULL;
    target = GetFunc(g, proto, FK_Method, 0);
    return CAbiOf(g, target) ? target : NULL;
}
/* The getter a GetField calls when it reads a property of an object of a known class whose getter has a C body with a C result; NULL otherwise. */
static FgFunc* CAbiGetterCallee(FgGen* g, FgFunc* f, FgFlow* st, const FgInstr* ins) {
    int slot = st->depth - 1, type;
    const char* field;
    FcRtClass* rc;
    FcProto* getter;
    FgFunc* target;
    FgCAbi* abi;
    if (!ScalarsOn || ins->op != OP_GetField) return NULL;
    if (slot < 0 || slot >= 64) return NULL;
    type = st->types[slot];
    if (BaseKindOfType(type) != KindObj || (type & TypeNone) != 0 || InterfaceOfType(g, type) || IsNullable(type)) return NULL;
    field = fgConstStr(f->chunk, ins->a[0]);
    rc = ObjClass(g, type);
    if (DirectFieldIndex(g, rc, field, f) >= 0 || !(getter = DirectAccessor(g, rc, field, false, f))) return NULL;
    target = GetFunc(g, getter, FK_Method, 0);
    return (abi = CAbiOf(g, target)) != NULL && abi->retType != 0 ? target : NULL;
}
/* The function with Values (`fN`) in front of the C body */
static const char* CAbiWrapper(FgGen* g, FgFunc* f, FgCAbi* abi) {
    FcBuf body = {0};
    FcList* args = fcListNew(g->c);
    const char* call;
    int i;
    fcBufFmt(&body, "%s\n", fgSignature(g, f));
    fcBufStr(&body, "{\n");
    if (fgHasSelf(f)) fcListAdd(g->c, args, (void*)"self");
    for (i = 0; i < fgParamCount(f); i++) {
        int type = abi->paramType[i];
        const char* p = fcFmt(g->c, "P%d", i);
        if (type == 0) { fcListAdd(g->c, args, (void*)p); continue; }
        if (abi->paramDemand[i]) {
            fcBufFmt(&body, "    %s = %s;\n", p, AdaptUnitCall(g, p, abi->paramDemand[i]));
            if (g->usesExceptions) fcBufStr(&body, "    if (FIRE_UNLIKELY(g_unwind.active)) return Undef();\n");
        }
        fcBufFmt(&body, "    %s = %s;\n", p, GuardKindCall(g, p, CDeclared(type), abi->paramWhat[i]));
        if (g->usesExceptions) fcBufStr(&body, "    if (FIRE_UNLIKELY(g_unwind.active)) return Undef();\n");
        fcListAdd(g->c, args, (void*)CNumberOf(g, type, p));
    }
    call = fcFmt(g->c, "%s(%s)", CName(g, f), fgJoin(g->c, args, ", "));
    fcBufFmt(&body, "%s\n", abi->retType != 0 ? fcFmt(g->c, "    return %s;", ScalarValueOf(g, KindOfType(abi->retType), UnitOfType(abi->retType), call)) : fcFmt(g->c, "    return %s;", call));
    fcBufStr(&body, "}\n");
    return fcBufDone(g->c, &body);
}
