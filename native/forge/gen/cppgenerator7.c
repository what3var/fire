/* native/forge/gen/cppgenerator7.c - src/fire.Native/CppGenerator.cs (commit 808e9788), part 7: StepCore - the calls (CallExtern, CallStaticMethod,
 * CallMethod, CallBaseMethod), the fields (GetField, SetField...), NewObject and ConstructBase; NoteReflectionLiterals (CppGenerator.Reflection.cs).
 * Not yet: the selectors of overloads by type (OverloadedCall), the dispatchers of interfaces and the overload hints - FG_UNPORTED. */

#undef g
#undef f
/* CppGenerator.Reflection.cs: TreeNodeClasses, NoteReflectionLiterals */
static bool fgTreeNodeClass(const char* n) {
    static const char* const t[] = { "Expression", "LambdaExpression", "MemberExpression", "CallExpression", "NewExpression", "IsTypeExpression", "ConvertExpression",
        "ParameterExpression", "ExpressionRewriter", "__ExpressionReplacer", "__ExpressionParameterRenamer" };
    size_t i;
    for (i = 0; i < sizeof t / sizeof t[0]; i++) if (streq0(n, t[i])) return true;
    return false;
}
static bool fgIsMemberName(const char* text) {
    const unsigned char* p = (const unsigned char*)text;
    uint16_t u = fc8Next(&p);
    if (!fcIsLetter(u) && u != '_') return false;
    while (*p) { u = fc8Next(&p); if (!fcIsLetterOrDigit(u) && u != '_') return false; }
    return true;
}
static void NoteReflectionLiterals(FgGen* g, FgFunc* f) {
    FgCode code;
    int i;
    if (fgIsReflectionHelperClass(f->chunk->ownerClass) || !fcSetAdd(g->literalChunks, f->chunk)) return;
    code = fgDecode(g->c, f->chunk);
    for (i = 0; i < code.n; i++) {
        const FcConst* c;
        const char* text;
        int n;
        if (code.p[i].op != OP_LoadConst) continue;
        c = fgConst(f->chunk, code.p[i].a[0]);
        if (c->kind != CK_String) continue;
        text = c->s;
        n = fc8Units(text);
        if (n == 0 || n > 200) continue;
        if (fgClassOf(g, text)) { if (fcSetAdd(g->literalClasses, text)) g->version++; }
        else if (fgIsMemberName(text) && fcSetAdd(g->literalMembers, text)) g->version++;
    }
}
static const char* fgTrimCommaSpace(const char* s) { while (*s == ',' || *s == ' ') s++; return s; }
static const FcExternSig* fgExternSig(FgGen* g, const char* name) {
    FcList* names = g->program->program->externNames;
    int i;
    for (i = 0; i < names->count; i++) if (streq0((const char*)names->items[i], name)) return (const FcExternSig*)g->program->program->externSigs->items[i];
    return NULL;
}
static bool fgAnyFieldIndex(FgGen* g, const char* field) {
    int i;
    for (i = 0; i < g->classList->count; i++) if (fgHasField(g, ((FgClassInfo*)g->classList->items[i])->rc, field)) return true;
    return false;
}
#define g (x->g)
#define f (x->f)
/* The tail of a field helper: the scope list only when it is needed (the C# passes it as a function) */
static const char* xFieldTail(FgSC* x, const char* field) { return FieldTail(g, field, f, FieldHasList(g, field) ? fcFmt(g->c, "&%s", OwnerList()) : NULL); }
static const char* xProbedSetter(FgSC* x, const FcRtClass* rc, const char* obj, const char* field, const char* value) {
    return MayBeProbed(g, rc, field) ? fcFmt(g->c, "if (FIRE_UNLIKELY(objAt(%s)->h.flags & 2)) %s(%s, %s%s); else ", obj, FieldFn(g, "sf", field), obj, value, xFieldTail(x, field)) : "";
}
typedef struct FgDenyCtx { FgSC* x; const FcRtClass* rc; const char* method; FgFuncKind kind; const char* what; } FgDenyCtx;
static const char* fgDenied(void* ctx, const FcRtClass* declaring, int access) {
    FgDenyCtx* dc = (FgDenyCtx*)ctx;
    FgSC* x = dc->x;
    if (!CheckAccess || AccessAllowed(f->chunk->ownerClass, declaring, access)) return NULL;
    return dc->kind == FK_Ctor ? fcFmt(g->c, "Constructor of '%s' is %s and cannot be called from here.", dc->rc->name, DescribeAccess(access))
        : fcFmt(g->c, "%s '%s' of '%s' is %s and cannot be called from here.", dc->what, dc->method, declaring->name, DescribeAccess(access));
}
/* overloads by type where the target is known up to the overload: the selector scores the arguments (access: per candidate, by the class of this code) */
static FgStep xOverloadedCall(FgSC* x, const FcRtClass* rc, const char* method, int argc, FgFuncKind kind, int slot, const char* self, const char* what, bool owned, FgClassInfo* newClass) {
    int64_t argMask = ArgMask(), mask;
    uint32_t addressBits = 0;
    int i, first;
    FgDenyCtx dc;
    FgSelector* selector;
    const char* args;
    for (i = 0; i < (argc < 16 ? argc : 16); i++) if (((argMask >> (4 * i)) & 15) == 3) addressBits |= 1u << i;
    dc.x = x; dc.rc = rc; dc.method = method; dc.kind = kind; dc.what = what;
    selector = OverloadSelectorFor(g, rc, method, argc, kind, fgDenied, &dc, addressBits, HintBefore(g, f, ins));
    mask = ArgMask();
    first = d - argc;
    ArgsBefore(first, argc, mask);
    args = Args(first, argc);
    if (newClass) {
        const char* ownerExpr = owned ? fcFmt(g->c, "objOwned(asObj(%s))", S(slot)) : fcFmt(g->c, "&%s", OwnerList());
        const char* typeArgsSet = g->pendingNewTArgs ? fcFmt(g->c, "objSetTArgs(asObj(o), %s); ", g->pendingNewTArgs) : "";
        g->pendingNewTArgs = NULL;
        {
            const char* make = NewObjectCall(g, newClass, ownerExpr);
            const char* enter = RecInitEnter(newClass);
            const char* owner = OwnerList();
            const char* leave = RecInitLeave(newClass);
            E("{ Value o = %s; %s%s%s%s(o%s, &%s); %sctorEnd(o); %s = o; }", make, typeArgsSet, newClass->rc->isActor ? "markActor(o); " : "", enter, selector->name, args, owner, leave, S(slot));
        }
        ArgsAfter(argc, mask, S(slot));
        Check();
        SetR(slot, false);
        SetT(slot, ObjType(g, newClass->rc)); g->kindsKept = true;
    } else {
        {
            const char* s = S(slot);
            E("%s = %s(%s%s, &%s);", s, selector->name, self, args, OwnerList());
        }
        ArgsAfter(argc, mask, kind == FK_Ctor ? "Undef()" : S(slot));
        Check();
        if (kind == FK_Ctor) { E("%s = Undef();", S(slot)); SetR(slot, false); }
        else AdoptResult(slot, SelectorReturnType(g, selector));
    }
    d = slot + 1; return Next();
}
static const char* xSlotS(void* ctx, int k) { return xS((FgSC*)ctx, k); }
static int xSlotTy(void* ctx, int k) { return xTy((FgSC*)ctx, k); }
static const char* xHintArg(void* ctx, int i) { FgSC* x = (FgSC*)ctx; return S(d - ins->a[1] + i); }
static void xAddDispatcher(FgSC* x, const char* method, int argc) { if (!fgHasNameArgc(g, g->dispatchers, method, argc)) fgAddNameArgc(g, g->dispatchers, method, argc); }

static FgStep xCallExtern(FgSC* x) {
    const char* externName = fgConstStr(x->chunk, ins->a[0]);
    int argc = ins->a[1], first, i;
    const FcExternSig* sig;
    FcBuf loc = {0};
    FcList* frees = fcListNew(g->c);
    FcList* args2 = fcListNew(g->c);
    FcList* ctypes;
    const char* call;
    const char* freeAll;
    Need(argc);
    fcBufStr(&loc, "");
    sig = fgExternSig(g, externName);
    if (sig && sig->inlineC) {
        const FcTypeRef* ir;
        int ifirst;
        if (sig->paramTypes->count != argc) FG_NOTSUP("inline C function '%s': %d parameter(s) declared, called with %d", externName, sig->paramTypes->count, argc);
        g->usesInlineC = true;
        ifirst = d - argc;
        for (i = 0; i < argc; i++) {
            const FcTypeRef* it = (const FcTypeRef*)sig->paramTypes->items[i];
            const char* iv = S(ifirst + i);
            const char* ictype = InlineCType(g, it);
            if (it->pointerDepth > 0) { fcBufFmt(&loc, "ExternPtr p%d; externPtrInit(&p%d, %s); ", i, i, iv); fcListAdd(g->c, args2, (void*)fcFmt(g->c, "p%d.raw", i)); fcListInsert(g->c, frees, 0, (void*)fcFmt(g->c, "externPtrFree(&p%d); ", i)); }
            else if (it->arrayRank == 1) { fcBufFmt(&loc, "uint32_t n%d; uint8_t* b%d = externBytes(%s, &n%d); ", i, i, iv, i); fcListAdd(g->c, args2, (void*)fcFmt(g->c, "b%d", i)); fcListAdd(g->c, args2, (void*)fcFmt(g->c, "n%d", i)); }
            else if (streq0(it->baseName, "string")) { fcBufFmt(&loc, "ExternString p%d; externStringInit(&p%d, %s); ", i, i, iv); fcListAdd(g->c, args2, (void*)fcFmt(g->c, "p%d.p", i)); fcListInsert(g->c, frees, 0, (void*)fcFmt(g->c, "externStringFree(&p%d); ", i)); }
            else if (streq0(it->baseName, "any")) fcListAdd(g->c, args2, (void*)iv);
            else if (streq0(it->baseName, "float")) fcListAdd(g->c, args2, (void*)fcFmt(g->c, "(%s)externFloat(%s)", ictype, iv));
            else if (streq0(it->baseName, "bool")) fcListAdd(g->c, args2, (void*)fcFmt(g->c, "externInt(%s) != 0", iv));
            else fcListAdd(g->c, args2, (void*)fcFmt(g->c, "(%s)externInt(%s)", ictype, iv));
        }
        call = fcFmt(g->c, "fire_inl_%s(%s)", Mangle(g, externName), fgJoin(g->c, args2, ", "));
        freeAll = fgJoin(g->c, frees, "");
        ir = sig->returnType;
        if (InlineReturnsNothing(sig)) { const char* locs = fcBufDone(g->c, &loc); E("{ %s%s; %s%s = Undef(); }", locs, call, freeAll, S(ifirst)); }
        else {
            const char* iconv = ir->pointerDepth > 0 ? fcFmt(g->c, "Int((int64_t)(intptr_t)%s)", call) : streq0(ir->baseName, "bool") ? fcFmt(g->c, "Bool(%s)", call)
                : streq0(ir->baseName, "float") ? fcFmt(g->c, "Float((Real)%s)", call) : streq0(ir->baseName, "char") ? fcFmt(g->c, "Char(%s)", call)
                : streq0(ir->baseName, "string") ? fcFmt(g->c, "externResultString(%s, &%s)", call, OwnerList()) : streq0(ir->baseName, "any") ? call : fcFmt(g->c, "Int((int64_t)%s)", call);
            const char* locs = fcBufDone(g->c, &loc);
            E("{ %s%s = %s; %s}", locs, S(ifirst), iconv, freeAll);
        }
        Check();
        d = ifirst + 1; SetR(ifirst, ir && ir->pointerDepth == 0 && (streq0(ir->baseName, "string") || streq0(ir->baseName, "any"))); return Next();
    }
    if (!sig || !sig->libName) FG_NOTSUP("extern function '%s' is not linked to a library with #extern \"lib\" (%s)", externName, x->fn);
    if (sig->paramTypes->count != argc) FG_NOTSUP("extern function '%s': %d parameter(s) declared, called with %d", externName, sig->paramTypes->count, argc);
    first = d - argc;
    ctypes = fcListNew(g->c);
    for (i = 0; i < argc; i++) {
        const FcTypeRef* t = (const FcTypeRef*)sig->paramTypes->items[i];
        const char* ctype;
        const char* v;
        if (!t) FG_NOTSUP("extern function '%s': a parameter type is missing", externName);
        ctype = ExternCType(g, t, externName);
        fcListAdd(g->c, ctypes, (void*)ctype);
        v = S(first + i);
        if (t->pointerDepth > 0) { fcBufFmt(&loc, "ExternPtr p%d; externPtrInit(&p%d, %s); ", i, i, v); fcListAdd(g->c, args2, (void*)fcFmt(g->c, "p%d.raw", i)); fcListInsert(g->c, frees, 0, (void*)fcFmt(g->c, "externPtrFree(&p%d); ", i)); }
        else if (streq0(t->baseName, "string")) { fcBufFmt(&loc, "ExternString p%d; externStringInit(&p%d, %s); ", i, i, v); fcListAdd(g->c, args2, (void*)fcFmt(g->c, "p%d.p", i)); fcListInsert(g->c, frees, 0, (void*)fcFmt(g->c, "externStringFree(&p%d); ", i)); }
        else if (streq0(t->baseName, "float")) fcListAdd(g->c, args2, (void*)fcFmt(g->c, "(%s)externFloat(%s)", ctype, v));
        else fcListAdd(g->c, args2, (void*)fcFmt(g->c, "(%s)externInt(%s)", ctype, v));
    }
    {
        const char* retC = !sig->returnType ? "void" : ExternCType(g, sig->returnType, externName);
        const char* locs;
        fcDictPut(g->externDecls, externName, (void*)fcFmt(g->c, "extern %s fire_ext_%s(%s) __asm__(FIRE_SYM(%s));   // library: %s", retC, Mangle(g, externName), fgJoin(g->c, ctypes, ", "), CString(g, externName), sig->libName));
        call = fcFmt(g->c, "fire_ext_%s(%s)", Mangle(g, externName), fgJoin(g->c, args2, ", "));
        freeAll = fgJoin(g->c, frees, "");
        locs = fcBufDone(g->c, &loc);
        if (!sig->returnType) { E("{ %s%s; %s}", locs, call, freeAll); E("%s = Undef();", S(first)); }
        else if (sig->returnType->pointerDepth > 0) E("{ %s%s = Int((int64_t)(intptr_t)%s); %s}", locs, S(first), call, freeAll);
        else {
            const char* bn = sig->returnType->baseName;
            const char* conv = streq0(bn, "bool") ? fcFmt(g->c, "Bool(%s != 0)", call) : streq0(bn, "float") ? fcFmt(g->c, "Float((Real)%s)", call)
                : streq0(bn, "char") ? fcFmt(g->c, "Char((uint32_t)%s)", call) : streq0(bn, "string") ? fcFmt(g->c, "externResultString(%s, &%s)", call, OwnerList()) : fcFmt(g->c, "Int((int64_t)%s)", call);
            E("{ %s%s = %s; %s}", locs, S(first), conv, freeAll);
        }
    }
    d = first + 1; SetR(first, sig->returnType && streq0(sig->returnType->baseName, "string")); return Next();
}

static FgStep xCallStaticMethod(FgSC* x) {
    int argc = ins->a[2];
    const char* cls;
    const char* method;
    FcRtClass* rc;
    FcList* staticNames;
    FcProto* proto;
    FgFound staticFound;
    FgFunc* target;
    FgFunc* callee;
    const char* specCondition = NULL;
    int64_t mask;
    FgCAbi* cabi;
    const char* dpre = "";
    const char* dargs = "";
    const char* staticArgs;
    const char* staticCall;
    bool helper;
    Need(argc);
    cls = Str(ins->a[0]); method = Str(ins->a[1]);
    rc = FindClass(g, cls);
    helper = fcIsReflectionHelper(cls);
    if (helper) NoteReflectionLiterals(g, f);
    staticNames = xNamesBefore(x);
    if (FG_OVERLOADED(rc, method, argc)) {
        if (staticNames) FG_NOTSUP("named arguments for %s.%s, which is overloaded by type", cls, method);
        if (helper) ES(ReflectionCallerAssign(g, f));
        return xOverloadedCall(x, rc, method, argc, FK_Static, d - argc, "Undef()", "Static method", false, NULL);
    }
    if (streq0(cls, "Math") && !staticNames && xMathIntrinsic(x, method, argc)) return Next();
    if (streq0(cls, "Math") && st->scalar != 0 && !CAbiStaticCallee(g, f, ins)) { FgEmit e = xEmitter(x); MaterializeAll(g, f, st, &e, -1, -1); }
    proto = FG_FIND(rc, method, argc).proto;
    if (!proto) FG_NOTSUP("%s.%s with %d argument(s) not found", cls, method, argc);
    if (!proto->isStatic) FG_NOTSUP("%s.%s is not static", cls, method);
    staticFound = FG_FIND(rc, method, argc);
    xAccessCheck(x, staticFound.declaring, staticFound.access, fcFmt(g->c, "Static method '%s' of '%s' is %s and cannot be called from here.", method, staticFound.declaring->name, DescribeAccess(staticFound.access)));
    target = GetFunc(g, proto, FK_Static, 0);
    callee = staticNames ? target : Specialize(g, f, ins, target, d - argc, argc, xSlotS, xSlotTy, x, &specCondition);
    if (helper) ES(ReflectionCallerAssign(g, f));
    mask = ArgMask();
    if (SlotScalars() && !specCondition && (mask == 0 || CAbiStaticCallee(g, f, ins)) && CAbiStaticCallee(g, f, ins) == callee && (cabi = CAbiOf(g, callee)) != NULL) {
        FcList* cargs = xCAbiArgs(x, cabi, d - argc, argc);
        const char* ccall = fcFmt(g->c, "%s(%s)", CName(g, callee), fgJoin(g->c, cargs, ", "));
        int rslot = d - argc;
        if (cabi->retType != 0) SetScalar(rslot, cabi->retType, ccall);
        else { E("%s = %s;", S(rslot), ccall); SetR(rslot, false); }
        Check();
        if (cabi->retType == 0) AdoptResult(rslot, ReturnType(g, callee));
        d = rslot + 1; return Next();
    }
    ArgsBefore(d - argc, argc, mask);
    if (!staticNames) { const char* list = xDefaultsList(x, proto, argc); dpre = DefaultArgsPre(g, proto, argc, "Undef()", list, &dargs); }
    if (!staticNames) staticArgs = fgTrimCommaSpace(fcFmt(g->c, "%s%s", CallArgs(d - argc, argc, mask, proto->refMask), dargs));
    else {
        const char* nargs;
        CallArgs(d - argc, argc, mask, proto->refMask);
        xNamedCallArgsOrFail(x, proto, d - argc, argc, staticNames, mask, "Undef()", fcFmt(g->c, "%s.%s", cls, method), &dpre, &nargs);
        staticArgs = fgTrimCommaSpace(nargs);
    }
    staticCall = specCondition ? fcFmt(g->c, "(%s) ? %s(%s) : %s(%s)", specCondition, callee->name, staticArgs, target->name, staticArgs) : fcFmt(g->c, "%s(%s)", callee->name, staticArgs);
    {
        const char* t = T(d - argc);
        E("%s%s = %s;%s", *dpre ? fcFmt(g->c, "{ %s", dpre) : "", t, staticCall, *dpre ? " }" : "");
    }
    ArgsAfter(argc, mask, S(d - argc));
    Check();
    AdoptResult(d - argc, !specCondition ? ReturnType(g, callee) : JoinTypes(ReturnType(g, callee), ReturnType(g, target)));
    d = d - argc + 1; return Next();
}

static FgStep xGetField(FgSC* x) {
    const char* field;
    const FcRtClass* fieldOwner;
    int fieldIndex, fieldType;
    FgFunc* cgetter;
    FgCAbi* gabi;
    FcProto* getter;
    Need(1);
    field = Str(ins->a[0]);
    fcSetAdd(g->fieldNames, field);
    fieldOwner = ObjSlot(d - 1) ? ObjClass(g, Ty(d - 1)) : NULL;
    if (ObjSlot(d - 1) && (fieldIndex = DirectFieldIndex(g, ObjClass(g, Ty(d - 1)), field, f)) >= 0) {
        const char* gfObj = SR(d - 1);
        const char* read = FieldRead(g, ObjClass(g, Ty(d - 1)), fieldIndex, fcFmt(g->c, "objAt(%s)", gfObj));
        if (IsNullable(Ty(d - 1))) {
            const char* tail = FieldTail(g, field, f, "NULL");
            const char* t = T(d - 1);
            E("%s = FIRE_LIKELY(%s.kind == K_Class && leafAlive(%s)) ? %s : %s(%s%s);", t, gfObj, gfObj, read, FieldFn(g, "gf", field), gfObj, tail);
            Check();
        }
        else if (streq0(gfObj, "self") && st->selfAlive) { E("%s = %s;", T(d - 1), read); g->selfAliveAfter = 1; }
        else {
            const char* thrower = xThrower(x, fcFmt(g->c, "destroyedError(%s);", gfObj));
            E("if (FIRE_UNLIKELY(!leafAlive(%s))) %s %s = %s;", gfObj, thrower, T(d - 1), read);
            if (streq0(gfObj, "self")) g->selfAliveAfter = 1;
        }
        g->neutralAfter = true;
    }
    else if (SlotScalars() && (cgetter = CAbiGetterCallee(g, f, st, ins)) != NULL && (gabi = CAbiOf(g, cgetter)) != NULL) {
        const char* gobj = S(d - 1);
        if (!(streq0(gobj, "self") && st->selfAlive)) { E("if (FIRE_UNLIKELY(!leafAlive(%s))) %s = destroyedError(%s);", gobj, gobj, gobj); Check(); }
        SetScalar(d - 1, gabi->retType, fcFmt(g->c, "%s(%s)", CName(g, cgetter), gobj));
        Check();
        return Next();
    }
    else if (ObjSlot(d - 1) && !IsNullable(Ty(d - 1)) && (getter = DirectAccessor(g, ObjClass(g, Ty(d - 1)), field, false, f)) != NULL) {
        const char* getterName = GetFunc(g, getter, FK_Method, 0)->name;
        const char* s = S(d - 1);
        E("if (FIRE_UNLIKELY(!leafAlive(%s))) %s = destroyedError(%s); else { Value r_ = %s(%s); adopt(r_, &%s); %s = r_; }", s, s, s, getterName, s, OwnerList(), s);
        Check();
    }
    else if (HasProperty(g, field) || RestrictedField(g, field)) {
        const char* s = S(d - 1);
        E("%s = %s(%s%s);", s, FieldFn(g, "gf", field), s, xFieldTail(x, field));
        Check();
    }
    else { const char* s = S(d - 1); E("%s = %s(%s%s);", s, FieldFn(g, "gf", field), s, Modular ? xFieldTail(x, field) : ""); Check(); }
    fieldType = FieldTypeIn(g, fieldOwner, field);
    if (fieldType == TypeUnknown) { int specType = SpecFieldType(g, f, ins, field); if (specType != -1) fieldType = specType; }
    if (fieldType != TypeUnknown) { SetR(d - 1, false); SetT(d - 1, fieldType); g->kindsKept = true; }
    else SetR(d - 1, true);
    return Next();
}

static FgStep xSetField(FgSC* x) {
    const char* field;
    const char* sfObj;
    const char* sfValue;
    const char* initOpen;
    const char* initClose;
    const char* direct;
    FcProto* setter;
    bool onSelf;
    Need(2);
    field = Str(ins->a[0]);
    fcSetAdd(g->fieldNames, field);
    sfObj = SR(d - 2); sfValue = SR(d - 1);
    initOpen = InitOpen(g, sfObj); initClose = *initOpen ? " g_recInit = wi_.next; }" : "";
    onSelf = streq0(sfObj, "self") && fgHasSelf(f);
    if (ObjSlot(d - 2) && (direct = DirectSetField(g, ObjClass(g, Ty(d - 2)), field, sfObj, sfValue, Ty(d - 1), f, IsNullable(Ty(d - 2)), onSelf && st->selfAlive)) != NULL) {
        E("%s%s%s", initOpen, direct, initClose);
        if (onSelf && !MayBeProbed(g, ObjClass(g, Ty(d - 2)), field)) g->selfAliveAfter = 1;
    }
    else if (ObjSlot(d - 2) && !IsNullable(Ty(d - 2)) && (setter = DirectAccessor(g, ObjClass(g, Ty(d - 2)), field, true, f)) != NULL) {
        const char* probed = xProbedSetter(x, ObjClass(g, Ty(d - 2)), sfObj, field, sfValue);
        const char* name = GetFunc(g, setter, FK_Method, 0)->name;
        E("%sif (FIRE_UNLIKELY(!leafAlive(%s))) destroyedError(%s); else %s{ Value r_ = %s(%s, %s); adopt(r_, &%s); }%s", initOpen, sfObj, sfObj, probed, name, sfObj, sfValue, OwnerList(), initClose);
    }
    else E("%s%s(%s, %s%s);%s", initOpen, FieldFn(g, "sf", field), sfObj, sfValue, xFieldTail(x, field), initClose);
    Check();
    xAssignValue(x, d - 2, d - 1);
    SetR(d - 2, R(d - 1));
    d--; return Next();
}

/* the store of SetFieldAccessor and SetFieldClaim: the direct store, the setter or the helper */
static const char* xFieldStore(FgSC* x, const char* field, const char* obj, const char* v, const char* direct, FcProto* setter) {
    if (direct) return direct;
    if (setter) {
        const char* probed = xProbedSetter(x, ObjClass(g, Ty(d - 2)), obj, field, v);
        const char* name = GetFunc(g, setter, FK_Method, 0)->name;
        return fcFmt(g->c, "if (FIRE_UNLIKELY(!leafAlive(%s))) destroyedError(%s); else %s{ Value r_ = %s(%s, %s); adopt(r_, &%s); }", obj, obj, probed, name, obj, v, OwnerList());
    }
    return fcFmt(g->c, "%s(%s, %s%s);", FieldFn(g, "sf", field), obj, v, xFieldTail(x, field));
}

static FgStep xSetFieldAccessor(FgSC* x) {
    const char* field;
    bool track;
    const char* obj;
    const char* v;
    const char* listsDecl;
    const char* lists;
    const char* mine;
    const char* keep;
    const char* direct;
    const char* store;
    FcProto* setter;
    int i;
    Need(2);
    field = Str(ins->a[0]);
    fcSetAdd(g->fieldNames, field);
    for (i = 0; i < st->scopeCount; i++) fgISetAdd(g->c, f->needsList, st->scopes[i].id);
    track = (ins->a[1] & 1) != 0 && fgHasSelf(f);
    if (track) f->accTrack = true;
    obj = S(d - 2); v = S(d - 1);
    if (st->scopeCount == 0) listsDecl = "";
    else {
        FcList* items = fcListNew(g->c);
        for (i = 0; i < st->scopeCount; i++) fcListAdd(g->c, items, (void*)fcFmt(g->c, "&%s", ListName(g, st->scopes[i].id)));
        listsDecl = CArray(g, "OwnList*", "KL_", items);
    }
    lists = st->scopeCount == 0 ? "NULL, 0" : fcFmt(g->c, "KL_, %d", st->scopeCount);
    mine = IsSetterFunc(g, f) ? "MYAL" : "NULL";
    keep = fgHasSelf(f) ? fcFmt(g->c, "{ %saccKeep(self, %s, %s, %s, %s, %s); }", listsDecl, obj, v, lists, mine, track ? "true" : "false") : "";
    direct = ObjSlot(d - 2) ? DirectSetField(g, ObjClass(g, Ty(d - 2)), field, obj, v, Ty(d - 1), f, IsNullable(Ty(d - 2)), false) : NULL;
    setter = !direct && ObjSlot(d - 2) && !IsNullable(Ty(d - 2)) ? DirectAccessor(g, ObjClass(g, Ty(d - 2)), field, true, f) : NULL;
    store = xFieldStore(x, field, obj, v, direct, setter);
    if (direct || (!setter && !HasProperty(g, field))) {
        ES(store);
        Check();
        if (*keep) ES(keep);
    }
    else {
        bool giveBack;
        f->needsArgList = true;
        giveBack = !setter && (Modular || g->openWorld || LibraryRole || fgAnyFieldIndex(g, field));
        E("{ %sOwnList* W_ = accHandOver(%s, %s, %s, &%s, &AL);", listsDecl, v, lists, mine, OwnerList());
        E("  %s", store);
        E("  accHandEnd(%s, W_, &AL, %s);%s finishArgs(Undef(), &AL); }", v, giveBack ? "true" : "false", giveBack && *keep ? fcFmt(g->c, " %s", keep) : "");
        Check();
    }
    E("%s = %s;", obj, v);
    SetR(d - 2, R(d - 1));
    d--; return Next();
}

static FgStep xSetFieldClaim(FgSC* x) {
    const char* field;
    bool setterValue, plainStore;
    const char* obj;
    const char* v;
    const char* direct;
    const char* temp;
    const char* listsDecl;
    const char* lists;
    const char* mine;
    const char* keep;
    const char* store;
    const char* claimInit;
    FcProto* setter;
    Need(2);
    field = Str(ins->a[0]);
    fcSetAdd(g->fieldNames, field);
    setterValue = (ins->a[1] & 2) != 0 && IsSetterFunc(g, f);
    obj = SR(d - 2); v = SR(d - 1);
    direct = ObjSlot(d - 2) ? DirectSetField(g, ObjClass(g, Ty(d - 2)), field, obj, v, Ty(d - 1), f, IsNullable(Ty(d - 2)), false) : NULL;
    plainStore = direct && IsPlainType(Ty(d - 1));
    temp = plainStore ? "" : OwnerList();
    if (setterValue) listsDecl = "";
    else { FcList* items = fcListNew(g->c); fcListAdd(g->c, items, (void*)fcFmt(g->c, "&%s", temp)); listsDecl = CArray(g, "OwnList*", "KL_", items); }
    lists = setterValue ? "NULL, 0" : "KL_, 1";
    mine = setterValue ? "MYAL" : "NULL";
    keep = plainStore ? "" : setterValue ? fcFmt(g->c, "claimKeep(%s, %s, MYAL);", obj, v) : fcFmt(g->c, "claimToHolder(%s, %s, &%s);", obj, v, temp);
    if (g->usesThreads && !plainStore && !setterValue) E("noteHolderClaim(%s, %s, &%s);", obj, v, temp);
    setter = !direct && ObjSlot(d - 2) && !IsNullable(Ty(d - 2)) ? DirectAccessor(g, ObjClass(g, Ty(d - 2)), field, true, f) : NULL;
    store = xFieldStore(x, field, obj, v, direct, setter);
    claimInit = InitOpen(g, obj);
    if (*claimInit) store = fcFmt(g->c, "%s%s g_recInit = wi_.next; }", claimInit, store);
    if (direct || (!setter && !HasProperty(g, field))) {
        ES(store);
        Check();
        if (*keep) ES(keep);
    }
    else {
        bool giveBack;
        f->needsArgList = true;
        giveBack = !setter && (Modular || g->openWorld || LibraryRole || fgAnyFieldIndex(g, field));
        E("{ %sOwnList* W_ = accHandOver(%s, %s, %s, &%s, &AL);", listsDecl, v, lists, mine, temp);
        E("  %s", store);
        E("  accHandEnd(%s, W_, &AL, %s);%s finishArgs(Undef(), &AL); }", v, giveBack ? "true" : "false", giveBack ? fcFmt(g->c, " %s", keep) : "");
        Check();
    }
    xAssignValue(x, d - 2, d - 1);
    SetR(d - 2, R(d - 1));
    d--; return Next();
}

static FgStep xSetFieldOnThis(FgSC* x) {
    const char* field;
    const char* thisValue;
    const char* direct = NULL;
    const FcRtClass* selfClass = NULL;
    FcProto* selfSetter = NULL;
    Need(1); xRequireSelf(x);
    field = Str(ins->a[0]);
    fcSetAdd(g->fieldNames, field);
    thisValue = SR(d - 1);
    if (fgHasSelf(f) && !IsExtensionMethod(g, f->proto) && (selfClass = f->chunk->ownerClass) != NULL
        && (direct = DirectSetField(g, selfClass, field, "self", thisValue, Ty(d - 1), f, false, st->selfAlive)) != NULL) {
        ES(direct);
        if (g->lastSetMayThrow) Check();
        g->selfAliveAfter = !MayBeProbed(g, selfClass, field) ? 1 : 0;
        d--; return Next();
    }
    else if (fgHasSelf(f) && !IsExtensionMethod(g, f->proto) && (selfClass = f->chunk->ownerClass) != NULL && (selfSetter = DirectAccessor(g, selfClass, field, true, f)) != NULL) {
        const char* probed = xProbedSetter(x, selfClass, "self", field, thisValue);
        const char* name = GetFunc(g, selfSetter, FK_Method, 0)->name;
        E("if (FIRE_UNLIKELY(!leafAlive(self))) destroyedError(self); else %s{ Value r_ = %s(self, %s); adopt(r_, &%s); }", probed, name, thisValue, OwnerList());
    }
    else E("%s(self, %s%s);", FieldFn(g, "sf", field), thisValue, xFieldTail(x, field));
    Check();
    d--; return Next();
}

static FgStep xNewObject(FgSC* x) {
    int argc = ins->a[1], slot, access;
    bool owned = ins->op == OP_NewObjectOwned;
    FgClassInfo* cls;
    FcList* newNames;
    FcProto* ctor;
    FgFunc* target;
    const char* ownerExpr;
    int64_t mask;
    const char* dpre = "";
    const char* dargs = "";
    const char* ctorArgs;
    const char* typeArgsSet;
    Need(argc + (owned ? 1 : 0));
    cls = RegisterClass(g, Str(ins->a[0]));
    if (fgTreeNodeClass(cls->rc->name) && !fgTreeNodeClass(f->chunk->ownerClass ? f->chunk->ownerClass->name : "")) NoteReflectionLiterals(g, f);
    newNames = xNamesBefore(x);
    if (newNames && fgIsConstructorTypeOverloaded(g->c, cls->rc, argc)) FG_NOTSUP("named arguments for the constructors of %s, which are overloaded by type", cls->rc->name);
    if (fgIsConstructorTypeOverloaded(g->c, cls->rc, argc)) return xOverloadedCall(x, cls->rc, "new", argc, FK_Ctor, d - argc - (owned ? 1 : 0), "", "", owned, cls);
    ctor = fgFindConstructor(cls->rc, argc);
    if (!ctor) {
        int errorSlot = d - argc - (owned ? 1 : 0);
        const char* s = S(errorSlot);
        E("%s = fireError(\"ArgumentException\", %s);", s, CString(g, DescribeConstructorNotFound(g, cls->rc, argc)));
        Check();
        SetR(errorSlot, false);
        d = errorSlot + 1; return Next();
    }
    access = ctor->access < 0 ? ACC_Public : ctor->access;
    xAccessCheck(x, cls->rc, access, fcFmt(g->c, "Constructor of '%s' is %s and cannot be called from here.", cls->rc->name, DescribeAccess(access)));
    target = GetFunc(g, ctor, FK_Ctor, 0);
    slot = d - argc - (owned ? 1 : 0);
    ownerExpr = owned ? fcFmt(g->c, "objOwned(asObj(%s))", S(slot)) : fcFmt(g->c, "&%s", OwnerList());
    mask = ArgMask();
    ArgsBefore(d - argc, argc, mask);
    if (newNames) xNamedCallArgsOrFail(x, ctor, d - argc, argc, newNames, mask, "o", fcFmt(g->c, "new %s", cls->rc->name), &dpre, &dargs);
    else { const char* list = fcFmt(g->c, "&%s", OwnerList()); dpre = DefaultArgsPre(g, ctor, argc, "o", list, &dargs); }
    ctorArgs = newNames ? "" : CallArgs(d - argc, argc, mask, ctor->refMask);
    typeArgsSet = g->pendingNewTArgs ? fcFmt(g->c, "objSetTArgs(asObj(o), %s); ", g->pendingNewTArgs) : "";
    g->pendingNewTArgs = NULL;
    {
        const char* make = NewObjectCall(g, cls, ownerExpr);
        const char* enter = RecInitEnter(cls);
        const char* leave = RecInitLeave(cls);
        const char* t = T(slot);
        E("{ Value o = %s; %s%s%s%s%s(o%s%s); %sctorEnd(o); %s = o; }", make, typeArgsSet, cls->rc->isActor ? "markActor(o); " : "", dpre, enter, target->name, ctorArgs, dargs, leave, t);
    }
    ArgsAfter(argc, mask, S(slot));
    Check();
    SetR(slot, false);
    SetT(slot, ObjType(g, cls->rc)); g->kindsKept = true;
    d = slot + 1; return Next();
}

static FgStep xConstructBase(FgSC* x) {
    int argc = ins->a[1];
    FcRtClass* rc;
    FcProto* ctor;
    FgFunc* target;
    int64_t mask;
    const char* dpre;
    const char* dargs = "";
    Need(argc); xRequireSelf(x);
    rc = FindClass(g, Str(ins->a[0]));
    if (fgIsConstructorTypeOverloaded(g->c, rc, argc)) return xOverloadedCall(x, rc, "new", argc, FK_Ctor, d - argc, "self", "", false, NULL);
    ctor = fgFindConstructor(rc, argc);
    if (!ctor) FG_NOTSUP("class %s has no constructor with %d argument(s)", rc->name, argc);
    target = GetFunc(g, ctor, FK_Ctor, 0);
    OwnerList();
    mask = ArgMask();
    ArgsBefore(d - argc, argc, mask);
    { const char* list = fcFmt(g->c, "&%s", OwnerList()); dpre = DefaultArgsPre(g, ctor, argc, "self", list, &dargs); }
    {
        const char* cargs = CallArgs(d - argc, argc, mask, ctor->refMask);
        E("%s%s(self%s%s);%s", *dpre ? fcFmt(g->c, "{ %s", dpre) : "", target->name, cargs, dargs, *dpre ? " }" : "");
    }
    ArgsAfter(argc, mask, "Undef()");
    Check();
    E("%s = Undef();", S(d - argc));
    SetR(d - argc, false);
    d = d - argc + 1; return Next();
}

/* CallMethod with named arguments: per class of the receiver its method with the arguments in the order of its parameters */
static FgStep xCallMethodNamed(FgSC* x, const char* method, int argc, int slot, FcList* methodNames) {
    FcList* classes = ObjSlot(slot) ? InstancesOf(g, ObjClass(g, Ty(slot))) : g->classList;
    int64_t nmask = ArgMask();
    FcBuf line = {0};
    FcProto* anyProto = NULL;
    FcList* exts;
    const char* s;
    const char* noFit;
    const char* notObject;
    int i;
    ArgsBefore(d - argc, argc, nmask);
    s = S(slot);
    fcBufFmt(&line, "if (%s.kind == K_Class && leafAlive(%s)) { switch (objAt(%s)->cls) { ", s, s, s);
    for (i = 0; i < classes->count; i++) {
        FgClassInfo* c = (FgClassInfo*)classes->items[i];
        FgFound namedFound;
        const char* npre;
        const char* nargs;
        if (FG_OVERLOADED(c->rc, method, argc)) continue;
        namedFound = FG_FIND(c->rc, method, argc);
        if (!namedFound.proto || namedFound.proto->isStatic) continue;
        if (CheckAccess && !AccessAllowed(f->chunk->ownerClass, namedFound.declaring, namedFound.access)) {
            fcBufFmt(&line, "case %d: %s = accessDenied(%s); break; ", c->id, S(slot), CString(g, fcFmt(g->c, "Method '%s' of '%s' is %s and cannot be called from here.", method, namedFound.declaring->name, DescribeAccess(namedFound.access))));
            continue;
        }
        if (!xNamedCallArgs(x, namedFound.proto, d - argc, argc, methodNames, nmask, S(slot), &npre, &nargs)) continue;
        {
            const char* clear = ClearCallTArgs(namedFound.proto);
            const char* name = GetFunc(g, namedFound.proto, FK_Method, 0)->name;
            fcBufFmt(&line, "case %d: { %s%s%s = %s(%s%s); break; } ", c->id, npre, clear, S(slot), name, S(slot), nargs);
        }
    }
    for (i = 0; i < classes->count && !anyProto; i++) {
        FcProto* p = FG_FIND(((FgClassInfo*)classes->items[i])->rc, method, argc).proto;
        if (p && !p->isStatic) anyProto = p;
    }
    noFit = CString(g, anyProto ? NamedMismatch(g, anyProto, argc, methodNames) : fcFmt(g->c, "Named arguments do not fit '%s'.", method));
    notObject = CString(g, fcFmt(g->c, "Named arguments are not possible for the built-in method '%s'.", method));
    fcBufFmt(&line, "default: %s = fireError(\"ArgumentException\", %s); break; } } else if (%s.kind == K_Class) %s = destroyedError(%s); else ", s, noFit, s, s, s);
    exts = ExtensionMethods(g, method, argc);
    for (i = 0; i < exts->count; i++) {
        FgExtension* ext = (FgExtension*)exts->items[i];
        const char* epre;
        const char* eargs;
        const char* fits;
        if (FG_OVERLOADED(ext->rc, method, argc)) continue;
        if (xNamedCallArgs(x, ext->proto, d - argc, argc, methodNames, nmask, S(slot), &epre, &eargs)) {
            const char* name = GetFunc(g, ext->proto, FK_Method, 0)->name;
            fits = fcFmt(g->c, "{ %s%s = %s(%s%s); }", epre, S(slot), name, S(slot), eargs);
        }
        else fits = fcFmt(g->c, "%s = fireError(\"ArgumentException\", %s);", S(slot), CString(g, NamedMismatch(g, ext->proto, argc, methodNames)));
        fcBufFmt(&line, "if (%s.kind == %s) %s else ", S(slot), ext->cpp, fits);
    }
    fcBufFmt(&line, "%s = fireError(\"ArgumentException\", %s);", S(slot), notObject);
    ES(fcBufDone(g->c, &line));
    ArgsAfter(argc, nmask, S(slot));
    Check();
    AdoptResult(slot, TypeUnknown);
    d = slot + 1; return Next();
}

/* CallMethod: a built-in ownership method of objects, arrays and buffers */
static FgStep xCallOwnMethod(FgSC* x, const char* method, int argc, int slot, const char* ownMethodId) {
    const char* owner = ScopeList();
    int baseArgs;
    const char* targetArg;
    bool isTry;
    if (streq0(ownMethodId, "OM_TakeUpwards") && !g->usesTakeUpwards) { g->usesTakeUpwards = true; g->usesGlobalOwn = true; g->version++; }
    if (streq0(ownMethodId, "OM_TakeGlobal") && !g->usesGlobalOwn) { g->usesGlobalOwn = true; g->version++; }
    baseArgs = OwnMethodBaseArgs(ownMethodId);
    targetArg = baseArgs == 1 ? S(slot + 1) : streq0(ownMethodId, "OM_TakeGlobal") && argc == 2 ? S(slot + 2) : "Undef()";
    isTry = OwnMethodIsTry(method);
    if (isTry && !g->usesTryDepth) { g->usesTryDepth = true; g->version++; }
    if (isTry) f->usesTryTake = true;
    if (argc == baseArgs && !isTry) E("ownMethod(%s, %s, %s, &%s);", ownMethodId, S(slot), targetArg, owner);
    else {
        FcList* scopeLists = CallLists(g, f, st);
        const char* localsDecl = scopeLists->count == 0 ? "" : CArray(g, "OwnList*", "L_", scopeLists);
        const char* modeArg = argc > baseArgs ? S(slot + 1 + baseArgs) : "Undef()";
        const char* call;
        if (argc > baseArgs && !g->usesTakeEnumerate) { g->usesTakeEnumerate = true; g->version++; }
        if (isTry) {
            const char* s = S(slot);
            call = fcFmt(g->c, "%s = Bool(ownMethodTry(%s, %s, %s, %s, &%s, %s, %d, %s, MYARGS));", s, ownMethodId, S(slot), targetArg, modeArg, owner, scopeLists->count == 0 ? "NULL" : "L_", scopeLists->count, fgHasSelf(f) ? "self" : "Undef()");
        }
        else call = fcFmt(g->c, "ownMethodT(%s, %s, %s, %s, &%s, %s, %d);", ownMethodId, S(slot), targetArg, modeArg, owner, scopeLists->count == 0 ? "NULL" : "L_", scopeLists->count);
        E("{ %s%s }", localsDecl, call);
        Check();
        if (isTry) { SetR(slot, false); d = slot + 1; return Next(); }
    }
    if (argc == baseArgs && !isTry) Check();
    E("%s = Undef();", S(slot));
    SetR(slot, false);
    d = slot + 1; return Next();
}

/* CallMethod on an object of a known class with a typed specialization written in the class (SPEC 5.8.1) */
typedef struct FgSpecCallCtx { FgSC* x; FcList* specCalls; const char** cSpecArgs; const char* hargs; const char* hrecv; } FgSpecCallCtx;
static const char* xSpecCall(FgSpecCallCtx* c, int n) {
    FgSC* x = c->x;
    FgHandSpec* sc = (FgHandSpec*)c->specCalls->items[n];
    FgCAbi* sabi;
    const char* ccall;
    if (!c->cSpecArgs[n] || !(sabi = CAbiOf(g, sc->callee))) return fcFmt(g->c, "%s(%s)", sc->callee->name, c->hargs);
    ccall = fcFmt(g->c, "%s(%s%s)", CName(g, sc->callee), c->hrecv, c->cSpecArgs[n]);
    return sabi->retType == 0 ? ccall : ScalarValueOf(g, BaseKindOfType(sabi->retType), UnitOfType(sabi->retType), ccall);
}
static FgStep xCallHandSpec(FgSC* x, const char* method, int argc, int slot, FcProto* directProto, FgFunc* target, FcList* specCalls, int64_t mask) {
    int n = specCalls->count, i, known = -1, hinted = -1, resultType;
    const char** cSpecArgs = (const char**)fcAlloc(g->c, sizeof(char*) * (size_t)n);
    const char** definedTests = (const char**)fcAlloc(g->c, sizeof(char*) * (size_t)n);
    FgSpecCallCtx sctx;
    const char* hrecv;
    const char* hargs;
    const char* hslot;
    const char* hnotObject;
    const char* callReceiverArgs = NULL;
    FcBuf chain = {0};
    for (i = 0; i < n; i++) {
        FgHandSpec* sc = (FgHandSpec*)specCalls->items[i];
        FgCAbi* sabi;
        cSpecArgs[i] = NULL;
        if (mask == 0 && SlotScalars() && (sabi = CAbiOf(g, sc->callee)) != NULL && xProvenCArgs(x, sabi, d - argc, argc)) {
            FcBuf b = {0};
            int k;
            fcBufStr(&b, "");
            for (k = 0; k < argc; k++) {
                int pt = sabi->paramType[k];
                fcBufFmt(&b, ", %s", pt == 0 ? S(d - argc + k) : KindOfType(pt) == KindFloat ? SF(d - argc + k) : KindOfType(pt) == KindWord ? SW(d - argc + k) : SN(d - argc + k));
            }
            cSpecArgs[i] = fcBufDone(g->c, &b);
        }
    }
    for (i = 0; i < n; i++) {
        FgHandSpec* sc = (FgHandSpec*)specCalls->items[i];
        FcBuf b = {0};
        int k;
        fcBufStr(&b, "");
        for (k = 0; k < argc; k++) { const char* v = S(d - argc + k); fcBufStr(&b, SpecArgTest(g, sc->spec, k, v, Ty(d - argc + k))); }
        definedTests[i] = fcBufDone(g->c, &b);
    }
    ArgsBefore(d - argc, argc, mask);
    hrecv = SR(slot);
    hargs = fcFmt(g->c, "%s%s", hrecv, CallArgs(d - argc, argc, mask, directProto->refMask));
    sctx.x = x; sctx.specCalls = specCalls; sctx.cSpecArgs = cSpecArgs; sctx.hargs = hargs; sctx.hrecv = hrecv;
    if (SlotScalars() && streq0(hrecv, "self") && fgHasSelf(f))
        for (i = 0; i < n; i++) if (SpecKnownForSelf(g, f, ((FgHandSpec*)specCalls->items[i])->spec)) { known = i; break; }
    if (known >= 0 && cSpecArgs[known] && !*definedTests[known]) {
        FgHandSpec* kc = (FgHandSpec*)specCalls->items[known];
        FgCAbi* kabi = CAbiOf(g, kc->callee);
        if (kabi) {
            const char* kcall;
            E("if (FIRE_UNLIKELY(!leafAlive(self))) %s", xThrower(x, "destroyedError(self);"));
            kcall = fcFmt(g->c, "%s(self%s)", CName(g, kc->callee), cSpecArgs[known]);
            if (kabi->retType != 0) SetScalar(slot, kabi->retType, kcall);
            else { E("%s = %s;", T(slot), kcall); SetR(slot, false); SetT(slot, ReturnType(g, kc->callee)); }
            ArgsAfter(argc, mask, S(slot));
            Check();
            d = slot + 1; return Next();
        }
    }
    for (i = 0; i < n; i++) {
        FgHandSpec* sc = (FgHandSpec*)specCalls->items[i];
        const char* call = xSpecCall(&sctx, i);
        fcBufFmt(&chain, "(%s%s) ? %s : ", sc->condition, definedTests[i], call);
    }
    fcBufFmt(&chain, "%s(%s)", target->name, hargs);
    hslot = T(slot);
    hnotObject = IsNullable(Ty(slot)) ? fcFmt(g->c, "if (FIRE_UNLIKELY(%s.kind != K_Class)) %s = missingMethod(%s, %s, %d); else ", hrecv, hslot, hrecv, CString(g, method), argc) : "";
    if (mask == 0 && f->chunk->receiverOffsets) {
        for (i = 0; i < f->chunk->receiverOffsets->count; i++) if (FC_VALI(f->chunk->receiverOffsets->items[i]) == ins->addr) callReceiverArgs = (const char*)f->chunk->receiverTexts->items[i];
    }
    if (callReceiverArgs) {
        int hintedReturn;
        for (i = 0; i < n; i++) {
            FgHandSpec* sc = (FgHandSpec*)specCalls->items[i];
            if (sc->callee->proto && sc->callee->proto->specArgs && SpecArgsFit(g, sc->callee->proto->specArgs, callReceiverArgs)) { hinted = i; break; }
        }
        if (hinted >= 0 && (hintedReturn = DeclaredReturn(g, ((FgHandSpec*)specCalls->items[hinted])->callee->proto)) != -1) {
            FgHandSpec* hc = (FgHandSpec*)specCalls->items[hinted];
            FgCAbi* hintedAbi = NULL;
            bool takes = DeclaredResultType(hintedReturn) != TypeUnknown || (cSpecArgs[hinted] && (hintedAbi = CAbiOf(g, hc->callee)) != NULL && hintedAbi->retType != 0);
            if (takes) {
                int hintedType = DeclaredResultType(hintedReturn);
                const char* what = fcFmt(g->c, "The result of '%s' of a %s<%s>", method, fcPlainName(g->c, directProto->chunk->ownerClass ? directProto->chunk->ownerClass->name : ""), fgReplace(g->c, callReceiverArgs, "|", ", "));
                const char* checkedGeneric = GuardKindCall(g, fcFmt(g->c, "%s(%s)", target->name, hargs), hintedReturn, what);
                if (cSpecArgs[hinted] && (hintedAbi = CAbiOf(g, hc->callee)) != NULL && hintedAbi->retType != 0) {
                    E("%sif (FIRE_UNLIKELY(!leafAlive(%s))) %s = destroyedError(%s);", hnotObject, hrecv, hslot, hrecv);
                    Check();
                    SetScalar(slot, hintedAbi->retType, fcFmt(g->c, "(%s%s) ? %s(%s%s) : %s", hc->condition, definedTests[hinted], CName(g, hc->callee), hrecv, cSpecArgs[hinted], CNumberOf(g, hintedAbi->retType, fcFmt(g->c, "(%s)", checkedGeneric))));
                    ArgsAfter(argc, mask, S(slot));
                    Check();
                    d = slot + 1; return Next();
                }
                if (hintedType != TypeUnknown) {
                    const char* call = xSpecCall(&sctx, hinted);
                    E("{ %sif (FIRE_UNLIKELY(!leafAlive(%s))) %s = destroyedError(%s); else %s = (%s%s) ? %s : %s; }", hnotObject, hrecv, hslot, hrecv, hslot, hc->condition, definedTests[hinted], call, checkedGeneric);
                    ArgsAfter(argc, mask, S(slot));
                    Check();
                    AdoptResult(slot, hintedType);
                    d = slot + 1; return Next();
                }
            }
        }
    }
    E("{ %sif (FIRE_UNLIKELY(!leafAlive(%s))) %s = destroyedError(%s); else %s = %s; }", hnotObject, hrecv, hslot, hrecv, hslot, fcBufDone(g->c, &chain));
    ArgsAfter(argc, mask, S(slot));
    Check();
    resultType = ReturnType(g, target);
    for (i = 0; i < n; i++) resultType = JoinTypes(resultType, ReturnType(g, ((FgHandSpec*)specCalls->items[i])->callee));
    AdoptResult(slot, resultType);
    d = slot + 1; return Next();
}

static FgStep xCallMethod(FgSC* x) {
    int argc = ins->a[1], slot, i, resultType;
    const char* method;
    FcList* methodNames;
    const char* ownMethodId;
    const char* dispatchedOwn;
    bool passesAddresses = false, reflected;
    FcProto* directProto;
    const char* extra;
    int64_t dmask;
    Need(argc + 1);
    method = Str(ins->a[0]);
    slot = d - argc - 1;
    reflected = g->usesReflection && fcDictHas(ReflectionMethodNames(g), method);
    if (reflected && ObjSlot(slot) && fgIsReflectionHelperClass(ObjClass(g, Ty(slot)))) NoteReflectionLiterals(g, f);
    if ((methodNames = xNamesBefore(x)) != NULL) return xCallMethodNamed(x, method, argc, slot, methodNames);
    if ((ownMethodId = OwnMethodId(method, argc)) != NULL && !AnyClassHasMethod(g, method, argc)) return xCallOwnMethod(x, method, argc, slot, ownMethodId);
    for (i = 0; i < (argc < 16 ? argc : 16); i++) if (((ArgMask() >> (4 * i)) & 15) == 3) passesAddresses = true;
    if (ObjSlot(slot) && !passesAddresses && !reflected && (directProto = DirectMethod(g, ObjClass(g, Ty(slot)), method, argc, f)) != NULL) {
        FgFunc* target = GetFunc(g, directProto, FK_Method, 0);
        const char* specCondition = NULL;
        FgFunc* callee = directProto->specializations ? target : Specialize(g, f, ins, target, d - argc, argc, xSlotS, xSlotTy, x, &specCondition);
        int64_t mask = ArgMask();
        FcList* specCalls;
        FgFunc* cabiCallee;
        FgCAbi* mabi;
        for (i = 0; i < (argc < 16 ? argc : 16); i++) {
            int k = K(d - argc + i);
            if (((mask >> (4 * i)) & 15) == 4 && (k == KindInt || k == KindFloat || k == KindBool || k == KindWord)) mask &= ~((int64_t)15 << (4 * i));
        }
        if (directProto->specializations && argc == directProto->paramCount && (specCalls = HandSpecCalls(g, directProto->specializations, SR(slot)))->count > 0)
            return xCallHandSpec(x, method, argc, slot, directProto, target, specCalls, mask);
        if (SlotScalars() && !specCondition && mask == 0 && (cabiCallee = CAbiMethodCallee(g, f, st, ins)) == callee && (mabi = CAbiOf(g, callee)) != NULL) {
            const char* self;
            FcList* margs;
            FcList* all;
            const char* mcall;
            if (IsNullable(Ty(slot))) {
                const char* s = S(slot);
                E("if (FIRE_UNLIKELY(%s.kind != K_Class)) %s = missingMethod(%s, %s, %d); else if (FIRE_UNLIKELY(!leafAlive(%s))) %s = destroyedError(%s);", s, s, s, CString(g, method), argc, s, s, s);
            }
            else { const char* s = S(slot); E("if (FIRE_UNLIKELY(!leafAlive(%s))) %s = destroyedError(%s);", s, s, s); }
            Check();
            self = S(slot);
            margs = xCAbiArgs(x, mabi, d - argc, argc);
            all = fcListNew(g->c);
            fcListAdd(g->c, all, (void*)self);
            for (i = 0; i < margs->count; i++) fcListAdd(g->c, all, margs->items[i]);
            mcall = fcFmt(g->c, "%s(%s)", CName(g, callee), fgJoin(g->c, all, ", "));
            if (mabi->retType != 0) SetScalar(slot, mabi->retType, mcall);
            else { E("%s = %s;", S(slot), mcall); SetR(slot, false); }
            Check();
            if (mabi->retType == 0) AdoptResult(slot, ReturnType(g, callee));
            d = slot + 1; return Next();
        }
        {
            const char* dpre;
            const char* dargs = "";
            const char* recv;
            const char* notObjectRead;
            const char* methodArgs;
            const char* methodCall;
            const char* rslot;
            const char* notObject;
            ArgsBefore(d - argc, argc, mask);
            {
                const char* self = S(slot);
                const char* list = xDefaultsList(x, directProto, argc);
                dpre = DefaultArgsPre(g, directProto, argc, self, list, &dargs);
            }
            recv = SR(slot);
            notObjectRead = IsNullable(Ty(slot)) ? fcFmt(g->c, "FIRE_UNLIKELY(%s.kind != K_Class)", recv) : "";
            methodArgs = fcFmt(g->c, "%s%s%s", recv, CallArgs(d - argc, argc, mask, directProto->refMask), dargs);
            methodCall = specCondition ? fcFmt(g->c, "(%s) ? %s(%s) : %s(%s)", specCondition, callee->name, methodArgs, target->name, methodArgs) : fcFmt(g->c, "%s(%s)", callee->name, methodArgs);
            rslot = T(slot);
            notObject = *notObjectRead ? fcFmt(g->c, "if (%s) %s = missingMethod(%s, %s, %d); else ", notObjectRead, rslot, recv, CString(g, method), argc) : "";
            E("{ %s%sif (FIRE_UNLIKELY(!leafAlive(%s))) %s = destroyedError(%s); else %s = %s; }", dpre, notObject, recv, rslot, recv, rslot, methodCall);
            ArgsAfter(argc, mask, S(slot));
            Check();
            AdoptResult(slot, !specCondition ? ReturnType(g, callee) : JoinTypes(ReturnType(g, callee), ReturnType(g, target)));
            d = slot + 1; return Next();
        }
    }
    {
        const char* receiverInterface;
        if (!passesAddresses && !WrapDispatchers && !OwnMethodId(method, argc) && !reflected && (receiverInterface = InterfaceOfType(g, Ty(slot))) != NULL
            && InterfaceDispatchWorthwhile(g, receiverInterface, method, argc)) {
            int64_t imask;
            fgAddIDispatch(g, receiverInterface, method, argc);
            imask = ArgMask();
            ArgsBefore(d - argc, argc, imask);
            {
                const char* s = S(slot);
                E("%s = %s(%s%s);", s, InterfaceDispatcherName(g, receiverInterface, method, argc), S(slot), Args(d - argc, argc));
            }
            ArgsAfter(argc, imask, S(slot));
            Check();
            AdoptResult(slot, DispatchResultType(g, method, argc));
            d = slot + 1; return Next();
        }
    }
    {
        FgHint* callHint;
        if (!passesAddresses && !WrapDispatchers && !OwnMethodId(method, argc) && !reflected && (callHint = HintBefore(g, f, ins)) != NULL && !fgHintIsConstructor(callHint) && streq0(callHint->method, method)) {
            FcList* classes;
            FcList* hinted = fcListNew(g->c);
            int k;
            if (ObjSlot(slot)) classes = InstancesOf(g, ObjClass(g, Ty(slot)));
            else {
                classes = fcListNew(g->c);
                for (i = 0; i < g->classList->count; i++) {
                    FgClassInfo* c = (FgClassInfo*)g->classList->items[i];
                    const FcRtClass* r;
                    for (r = c->rc; r; r = r->base) if (streq0(r->name, callHint->declaringClass)) { fcListAdd(g->c, classes, c); break; }
                }
            }
            for (i = 0; i < classes->count; i++) {
                FgClassInfo* c = (FgClassInfo*)classes->items[i];
                FcProto* p;
                FgImpl* t;
                if (!MethodAllowedOn(g, c->rc, method, argc, f)) continue;
                p = HintTarget(g, callHint, c->rc);
                if (!p || p->isStatic) continue;
                t = (FgImpl*)fcAlloc(g->c, sizeof *t);
                t->cls = c; t->proto = p;
                fcListAdd(g->c, hinted, t);
            }
            if (hinted->count > 0) {
                const char* hextra;
                int64_t hmask;
                const char* hargs;
                const char* dispatch;
                FcBuf line = {0};
                FcList* groups = fcListNew(g->c);
                xAddDispatcher(x, method, argc);
                hextra = DispatchTail(g, method, argc, DispatchNeedsList(g, method, argc) ? fcFmt(g->c, "&%s", OwnerList()) : "", CallerId(g, f));
                hmask = ArgMask();
                ArgsBefore(d - argc, argc, hmask);
                hargs = Args(d - argc, argc);
                dispatch = fcFmt(g->c, "%s(%s%s%s)", CallFn(g, method, argc, "call_"), S(slot), hargs, hextra);
                {
                    const char* s = S(slot);
                    const char* holds = HintHolds(g, callHint, xHintArg, x);
                    fcBufFmt(&line, "if (%s.kind == K_Class && leafAlive(%s) && %s) { g_explicitTArgs = 0; switch (objAt(%s)->cls) { ", s, s, holds, s);
                }
                for (i = 0; i < hinted->count; i++) {
                    FgImpl* t = (FgImpl*)hinted->items[i];
                    bool seen = false;
                    for (k = 0; k < groups->count; k++) if (((FgImpl*)groups->items[k])->proto == t->proto) seen = true;
                    if (!seen) fcListAdd(g->c, groups, t);
                }
                for (k = 0; k < groups->count; k++) {
                    FcProto* proto = ((FgImpl*)groups->items[k])->proto;
                    FgFunc* target = GetFunc(g, proto, FK_Method, 0);
                    const char* hpre;
                    const char* hdefaults = "";
                    {
                        const char* self = S(slot);
                        const char* list = xDefaultsList(x, proto, argc);
                        hpre = DefaultArgsPre(g, proto, argc, self, list, &hdefaults);
                    }
                    for (i = 0; i < hinted->count; i++) if (((FgImpl*)hinted->items[i])->proto == proto) fcBufFmt(&line, "case %d: ", ((FgImpl*)hinted->items[i])->cls->id);
                    fcBufFmt(&line, "{ %s%s%s = %s(%s%s%s); break; } ", hpre, ClearCallTArgs(proto), S(slot), target->name, S(slot), hargs, hdefaults);
                }
                fcBufFmt(&line, "default: %s = %s; break; } } else %s = %s;", S(slot), dispatch, S(slot), dispatch);
                ES(fcBufDone(g->c, &line));
                ArgsAfter(argc, hmask, S(slot));
                Check();
                AdoptResult(slot, DispatchResultType(g, method, argc));
                d = slot + 1; return Next();
            }
        }
    }
    xAddDispatcher(x, method, argc);
    if (passesAddresses) fgAddNameArgc(g, g->refCallSites, method, argc);
    resultType = DispatchResultType(g, method, argc);
    extra = DispatchTail(g, method, argc, DispatchNeedsList(g, method, argc) ? fcFmt(g->c, "&%s", OwnerList()) : "", CallerId(g, f));
    dmask = ArgMask();
    if (reflected) ES(ReflectionCallerAssign(g, f));
    ArgsBefore(d - argc, argc, dmask);
    if ((dispatchedOwn = OwnMethodId(method, argc)) != NULL && argc > OwnMethodBaseArgs(dispatchedOwn)) {
        FcList* dispatchScopes = CallLists(g, f, st);
        if (dispatchScopes->count > 0) E("{ %sg_scopeLists = L_; g_scopeCount = %d;", CArray(g, "OwnList*", "L_", dispatchScopes), dispatchScopes->count);
        {
            const char* s = S(slot);
            E("%s = %s(%s%s%s);", s, CallFn(g, method, argc, "call_"), S(slot), Args(d - argc, argc), extra);
        }
        if (dispatchScopes->count > 0) ES("g_scopeLists = NULL; g_scopeCount = 0; }");
    }
    else {
        const char* dispatchArgs = fcFmt(g->c, "%s%s%s", SR(slot), Args(d - argc, argc), extra);
        E("%s = %s(%s);", T(slot), CallFn(g, method, argc, "call_"), dispatchArgs);
    }
    ArgsAfter(argc, dmask, S(slot));
    Check();
    AdoptResult(slot, resultType);
    d = slot + 1; return Next();
}

static FgStep xCallBaseMethod(FgSC* x) {
    int argc = ins->a[2];
    FcRtClass* rc;
    const char* method;
    FcList* baseNames;
    FcProto* proto;
    FgFound baseFound;
    FgFunc* target;
    int64_t mask;
    const char* dpre = "";
    const char* dargs = "";
    const char* baseArgs;
    Need(argc); xRequireSelf(x);
    rc = FindClass(g, Str(ins->a[0]));
    method = Str(ins->a[1]);
    baseNames = xNamesBefore(x);
    if (baseNames && FG_OVERLOADED(rc, method, argc)) FG_NOTSUP("named arguments for base.%s, which is overloaded by type", method);
    if (FG_OVERLOADED(rc, method, argc)) return xOverloadedCall(x, rc, method, argc, FK_Method, d - argc, "self", "Method", false, NULL);
    proto = FG_FIND(rc, method, argc).proto;
    if (!proto) FG_NOTSUP("%s.%s with %d argument(s) not found", rc->name, method, argc);
    baseFound = FG_FIND(rc, method, argc);
    if (baseFound.proto)
        xAccessCheck(x, baseFound.declaring, baseFound.access, fcFmt(g->c, "Method '%s' of '%s' is %s and cannot be called from here.", method, baseFound.declaring->name, DescribeAccess(baseFound.access)));
    target = GetFunc(g, proto, FK_Method, 0);
    mask = ArgMask();
    ArgsBefore(d - argc, argc, mask);
    if (baseNames) xNamedCallArgsOrFail(x, proto, d - argc, argc, baseNames, mask, "self", fcFmt(g->c, "base.%s", method), &dpre, &dargs);
    else { const char* list = xDefaultsList(x, proto, argc); dpre = DefaultArgsPre(g, proto, argc, "self", list, &dargs); }
    baseArgs = baseNames ? "" : CallArgs(d - argc, argc, mask, proto->refMask);
    {
        const char* s = S(d - argc);
        E("%s%s = %s(self%s%s);%s", *dpre ? fcFmt(g->c, "{ %s", dpre) : "", s, target->name, baseArgs, dargs, *dpre ? " }" : "");
    }
    ArgsAfter(argc, mask, S(d - argc));
    Check();
    AdoptResult(d - argc, ReturnType(g, target));
    d = d - argc + 1; return Next();
}

static FgStep StepCore4(FgSC* x);

static FgStep StepCore3(FgSC* x) {
    switch (ins->op) {
        case OP_CallExtern: return xCallExtern(x);
        case OP_CallStaticMethod: return xCallStaticMethod(x);
        case OP_GetField:
            if (ArrSlot(d - 1) != 0 && streq0(Str(ins->a[0]), "length") && !HasProperty(g, "length") && !RestrictedField(g, "length") && g->program->optimize == 0) {
                const char* field;
                const char* s0;
                Need(1);
                field = Str(ins->a[0]);
                s0 = S(d - 1);
                fcSetAdd(g->fieldNames, field);
                E("%s = FIRE_LIKELY(%s.kind == K_Array && leafAlive(%s)) ? Int(arrOf(%s)->length) : %s(%s%s);", s0, s0, s0, s0, FieldFn(g, "gf", field), s0, Modular ? xFieldTail(x, field) : "");
                Check();
                SetR(d - 1, false); SetT(d - 1, TypeOf(KindInt, 0) | NarrowBit); g->kindsKept = true;
                return Next();
            }
            if (K(d - 1) == KindBuf && (streq0(Str(ins->a[0]), "length") || streq0(Str(ins->a[0]), "Length"))) {
                const char* s;
                Need(1);
                fcSetAdd(g->fieldNames, Str(ins->a[0]));
                s = S(d - 1);
                E("%s = FIRE_LIKELY(leafAlive(%s)) ? Int(bufOf(%s)->length) : destroyedError(%s);", s, s, s, s);
                Check();
                SetR(d - 1, false); SetT(d - 1, TypeOf(KindInt, 0)); g->kindsKept = true;
                return Next();
            }
            return xGetField(x);
        case OP_SetField: return xSetField(x);
        case OP_SetFieldAccessor: return xSetFieldAccessor(x);
        case OP_SetFieldClaim: return xSetFieldClaim(x);
        case OP_SetFieldOnThis: return xSetFieldOnThis(x);
        case OP_NewObject:
        case OP_NewObjectOwned: return xNewObject(x);
        case OP_ConstructBase: return xConstructBase(x);
        case OP_CallMethod: return xCallMethod(x);
        case OP_CallBaseMethod: return xCallBaseMethod(x);
        default: return StepCore4(x);
    }
}
