/* native/forge/gen/selectors.c - the rest of src/fire.Native/CppGenerator.Overloads.cs (commit 808e9788): the selectors of calls whose target is known up to
 * the overload (OverloadSelectorFor, SelectorReturnType, SelectorSignature, SelectorFunction) and the overload hints of the type checker (HintBefore,
 * HintTarget, HintHolds, ClassHolds) with src/fire/Bytecode/Overloads.cs OverloadHint (Parse, Resolve/Find). */

/* ---- OverloadHint ---- */
typedef struct FgHint { const char* declaringClass; const char* method; int ordinal, paramCount, argc; FcList* assumptions; } FgHint;
static bool fgHintIsConstructor(const FgHint* h) { return streq0(h->method, "#new"); }
static FgHint* fgHintParse(FcCtx* c, const char* text) {
    FcList* parts = fgSplit(c, text, '\x1f');
    FgHint* h = (FgHint*)fcAlloc(c, sizeof *h);
    int i;
    h->declaringClass = (const char*)parts->items[0];
    h->method = parts->count > 1 ? (const char*)parts->items[1] : "";
    h->ordinal = parts->count > 2 ? atoi((const char*)parts->items[2]) : 0;
    h->paramCount = parts->count > 3 ? atoi((const char*)parts->items[3]) : 0;
    h->argc = parts->count > 4 ? atoi((const char*)parts->items[4]) : 0;
    h->assumptions = fcListNew(c);
    for (i = 5; i < parts->count; i++) fcListAdd(c, h->assumptions, parts->items[i]);
    return h;
}
/* OverloadHint.Find: the overload on an object of class `rc` (for a constructor `rc` is the class itself); NULL when it cannot be found */
static FcProto* fgHintFind(FgGen* g, const FgHint* h, const FcRtClass* rc) {
    const FcRtClass* c;
    const FcRtClass* declaringRc;
    FcList* list;
    FcProto* chosen;
    FgCands* set;
    bool derives = false;
    int i;
    if (fgHintIsConstructor(h)) {
        FcList* group = fcListNew(g->c);
        FcProto* first = NULL;
        if (!streq0(rc->name, h->declaringClass)) return NULL;
        for (i = 0; i < rc->ctorArities->count; i++) if (FC_VALI(rc->ctorArities->items[i]) == h->paramCount) first = (FcProto*)rc->ctorProtos->items[i];
        if (!first) return NULL;
        fcListAdd(g->c, group, first);
        for (i = 0; i < rc->extraConstructors->count; i++) if (((FcProto*)rc->extraConstructors->items[i])->paramCount == h->paramCount) fcListAdd(g->c, group, rc->extraConstructors->items[i]);
        return h->ordinal < group->count ? (FcProto*)group->items[h->ordinal] : NULL;
    }
    for (c = rc; c; c = c->base) if (streq0(c->name, h->declaringClass)) { derives = true; break; }
    if (!derives || !(declaringRc = fgClassOf(g, h->declaringClass)) || !(list = (FcList*)fcDictGet(declaringRc->methods, h->method)) || h->ordinal >= list->count) return NULL;
    chosen = (FcProto*)list->items[h->ordinal];
    set = fgCandidates(g->c, g->rtCands, rc, h->method, h->argc);
    for (i = 0; i < set->count; i++) if (set->protos[i] == chosen) return chosen;
    for (i = 0; i < set->count; i++) if (set->protos[i]->paramCount == chosen->paramCount && fcSameSignature(set->protos[i], chosen)) return set->protos[i];
    return NULL;
}
/* The OverloadHint in front of a call instruction (before its SetTypeArgs and CopyArgs), or NULL. */
static FgHint* HintBefore(FgGen* g, FgFunc* f, const FgInstr* ins) {
    int i = f->indexOf[ins->addr] - 2;
    if (i >= 0 && f->decoded.p[i].op == OP_CopyArgs) i--;
    if (i >= 0 && f->decoded.p[i].op == OP_SetTypeArgs) i--;
    return i >= 0 && f->decoded.p[i].op == OP_OverloadHint ? fgHintParse(g->c, fgConstStr(f->chunk, f->decoded.p[i].a[0])) : NULL;
}
static FcProto* HintTarget(FgGen* g, const FgHint* hint, const FcRtClass* rc) { return fgHintFind(g, hint, rc); }
static const char* ClassHolds(FgGen* g, const char* v, const char* name, bool isInterface) {
    const char* enumerable;
    fcSetAdd(g->isOfTypes, name);
    enumerable = isInterface && streq0(fcPlainName(g->c, name), "IEnumerable") ? fcFmt(g->c, "%s.kind == K_Array || %s.kind == K_Buffer || %s.kind == K_String || ", v, v, v) : "";
    return fcFmt(g->c, "(%s(%s.kind == K_Class && %s(%s)))", enumerable, v, IsOfFn(g, name), v);
}
/* The C++ test that the arguments are what the hint assumed (OverloadHint.Holds). */
static const char* HintHolds(FgGen* g, const FgHint* hint, const char* (*arg)(void* ctx, int i), void* ctx) {
    FcList* tests = fcListNew(g->c);
    int i;
    for (i = 0; i < hint->assumptions->count; i++) {
        const char* a = (const char*)hint->assumptions->items[i];
        const char* v = arg(ctx, i);
        const char* test;
        if (!*a) continue;
        test = streq0(a, "u") ? fcFmt(g->c, "%s.kind == K_Undefined", v) : streq0(a, "k:int") ? fcFmt(g->c, "%s.kind == K_Int", v) : streq0(a, "k:float") ? fcFmt(g->c, "%s.kind == K_Float", v)
            : streq0(a, "k:bool") ? fcFmt(g->c, "%s.kind == K_Bool", v) : streq0(a, "k:char") ? fcFmt(g->c, "%s.kind == K_Char", v) : streq0(a, "k:string") ? fcFmt(g->c, "%s.kind == K_String", v)
            : streq0(a, "k:lambda") ? fcFmt(g->c, "%s.kind == K_Lambda", v) : streq0(a, "k:array") ? fcFmt(g->c, "(%s.kind == K_Array || %s.kind == K_Buffer)", v, v)
            : streq0(a, "k:class") ? fcFmt(g->c, "%s.kind == K_Class", v) : streq0(a, "k:word") ? fcFmt(g->c, "%s.kind == K_Word", v) : ClassHolds(g, v, a + 2, a[0] == 'i');
        fcListAdd(g->c, tests, (void*)test);
    }
    return tests->count == 0 ? "true" : fgJoin(g->c, tests, " && ");
}

/* ---- the selectors ---- */
struct FgSelector {
    const char* name; const FcRtClass* rc; FcProto** candidates; const FcRtClass** declaring; int count; int argc; FgFuncKind kind;
    const char** denied; uint32_t addresses; const char* method; FgHint* hint;
};
typedef const char* (*FgDeniedFn)(void* ctx, const FcRtClass* declaring, int access);
static FgSelector* OverloadSelectorFor(FgGen* g, const FcRtClass* rc, const char* name, int argc, FgFuncKind kind, FgDeniedFn denied, void* ctx, uint32_t addresses, FgHint* hint) {
    FcProto** protos;
    const FcRtClass** declaring;
    int n, i, hinted = -1;
    const char** deniedMessages;
    FcBuf key = {0};
    FgSelector* selector;
    FcProto* target;
    if (kind == FK_Ctor) {
        FcList* cands = fgConstructorCandidates(g->c, rc, argc);
        n = cands->count;
        protos = (FcProto**)fcAlloc(g->c, sizeof(FcProto*) * (size_t)(n + 1));
        declaring = (const FcRtClass**)fcAlloc(g->c, sizeof(FcRtClass*) * (size_t)(n + 1));
        for (i = 0; i < n; i++) { protos[i] = (FcProto*)cands->items[i]; declaring[i] = rc; }
    } else {
        FgCands* set = fgCandidates(g->c, g->rtCands, rc, name, argc);
        n = set->count; protos = set->protos; declaring = set->declaring;
    }
    deniedMessages = (const char**)fcAlloc(g->c, sizeof(char*) * (size_t)(n + 1));
    for (i = 0; i < n; i++) { const char* m = denied(ctx, declaring[i], protos[i]->access < 0 ? ACC_Public : protos[i]->access); deniedMessages[i] = m ? m : ""; }
    if (hint && addresses == 0 && (target = HintTarget(g, hint, rc)) != NULL) { for (i = 0; i < n; i++) if (protos[i] == target) { hinted = i; break; } }
    if (hinted < 0) hint = NULL;
    fcBufFmt(&key, "%s|%s|%d|%s|%u|", rc->name, kind == FK_Ctor ? "#new" : name, argc, kind == FK_Ctor ? "Ctor" : kind == FK_Static ? "Static" : kind == FK_Method ? "Method" : "Other", addresses);
    for (i = 0; i < n; i++) { if (i) fcBufChar(&key, '|'); fcBufStr(&key, deniedMessages[i]); }
    fcBufChar(&key, '|');
    if (hint) fcBufFmt(&key, "%s@%d", fgJoin(g->c, hint->assumptions, ","), hinted);
    {
        const char* k = fcBufDone(g->c, &key);
        if ((selector = (FgSelector*)fcDictGet(g->overloadSelectors, k)) != NULL) return selector;
        for (i = 0; i < n; i++) {
            const char* args;
            if (kind == FK_Static && !protos[i]->isStatic) continue;
            GetFunc(g, protos[i], kind, 0);
            DefaultArgsPre(g, protos[i], argc, "self", "list", &args);
        }
        selector = (FgSelector*)fcAlloc(g->c, sizeof *selector);
        selector->name = fcFmt(g->c, "%sovs_%d", Px, fcDictCount(g->overloadSelectors));
        selector->rc = rc; selector->candidates = protos; selector->declaring = declaring; selector->count = n; selector->argc = argc; selector->kind = kind;
        selector->denied = deniedMessages; selector->addresses = addresses; selector->method = name; selector->hint = hint;
        fcDictPut(g->overloadSelectors, k, selector);
    }
    return selector;
}
static int SelectorReturnType(FgGen* g, const FgSelector* selector) {
    int result = INT32_MIN, i;
    if (selector->kind == FK_Ctor) return TypeUnknown;
    for (i = 0; i < selector->count; i++) {
        FcProto* proto = selector->candidates[i];
        FgFunc* target;
        int type;
        if (selector->kind == FK_Static && !proto->isStatic) continue;
        if (!(target = (FgFunc*)fcDictGet(g->funcByProto, proto))) return TypeUnknown;
        type = ReturnType(g, target);
        result = result == INT32_MIN ? type : JoinTypes(result, type);
    }
    return result == INT32_MIN ? TypeUnknown : result;
}
static const char* SelectorSignature(FgGen* g, const FgSelector* selector) {
    FcBuf b = {0};
    int i;
    fcBufFmt(&b, "FIRE_MAYBE_UNUSED static Value %s(Value self", selector->name);
    for (i = 0; i < selector->argc; i++) fcBufFmt(&b, ", Value a%d", i);
    fcBufStr(&b, ", OwnList* list)");
    return fcBufDone(g->c, &b);
}
typedef struct FgSelCtx { FgGen* g; const FgSelector* selector; } FgSelCtx;
static bool fgSelIsAddress(const FgSelector* s, int i) { return ((s->addresses >> i) & 1) != 0; }
static const char* fgSelCall(void* ctx, int c) {
    FgSelCtx* sc = (FgSelCtx*)ctx;
    FgGen* g = sc->g;
    const FgSelector* s = sc->selector;
    FcProto* proto = s->candidates[c];
    const char* pre;
    const char* defaults = "";
    FcBuf args = {0};
    const char* target;
    int i;
    if (s->kind == FK_Static && !proto->isStatic)
        return fcFmt(g->c, "{ fatal(%s); return Undef(); }", CString(g, fcFmt(g->c, "'%s' on '%s' is not a static method - 'ClassName.%s(...)' can only be used to call 'static' methods.", s->method, s->rc->name, s->method)));
    if (*s->denied[c]) return fcFmt(g->c, "return accessDenied(%s);", CString(g, s->denied[c]));
    pre = DefaultArgsPre(g, proto, s->argc, "self", "list", &defaults);
    fcBufStr(&args, "");
    for (i = 0; i < s->argc; i++) fcBufFmt(&args, fgSelIsAddress(s, i) && ((proto->refMask >> i) & 1) == 0 ? ", v%d" : ", a%d", i);
    target = ((FgFunc*)fcDictGet(g->funcByProto, proto))->name;
    {
        const char* a = fcBufDone(g->c, &args);
        if (s->kind == FK_Ctor) return fcFmt(g->c, "{ %s%s(self%s%s); return Undef(); }", pre, target, a, defaults);
        if (s->kind == FK_Static) { const char* all = fcFmt(g->c, "%s%s", a, defaults); while (*all == ',' || *all == ' ') all++; return fcFmt(g->c, "{ %sreturn %s(%s); }", pre, target, all); }
        return fcFmt(g->c, "{ %sreturn %s(self%s%s); }", pre, target, a, defaults);
    }
}
static const char* fgSelArgA(void* ctx, int i) { FgSelCtx* sc = (FgSelCtx*)ctx; return fcFmt(sc->g->c, "a%d", i); }
static const char* fgSelArgValue(void* ctx, int i) { FgSelCtx* sc = (FgSelCtx*)ctx; return fcFmt(sc->g->c, fgSelIsAddress(sc->selector, i) ? "v%d" : "a%d", i); }
static const char* SelectorFunction(FgGen* g, const FgSelector* selector) {
    FcBuf sb = {0};
    const char* ownerObj = selector->kind == FK_Static ? "NULL" : "(self.kind == K_Class ? objAt(self) : NULL)";
    FgSelCtx ctx;
    FcProto* hintedProto;
    int i;
    ctx.g = g; ctx.selector = selector;
    fcBufFmt(&sb, "%s\n{\n", SelectorSignature(g, selector));
    fcBufStr(&sb, "    (void)self; (void)list;\n");
    for (i = 0; i < selector->argc; i++) if (fgSelIsAddress(selector, i)) fcBufFmt(&sb, "    Value v%d = derefArg(a%d);\n", i, i);
    if (selector->hint && (hintedProto = HintTarget(g, selector->hint, selector->rc)) != NULL) {
        int c = -1;
        for (i = 0; i < selector->count; i++) if (selector->candidates[i] == hintedProto) { c = i; break; }
        {
            const char* holds = HintHolds(g, selector->hint, fgSelArgA, &ctx);
            const char* clear = ClearCallTArgs(hintedProto);
            fcBufFmt(&sb, "    if (%s) { g_explicitTArgs = 0; %s%s }\n", holds, clear, fgSelCall(&ctx, c));
        }
    }
    fcBufStr(&sb, OverloadChoice(g, selector->candidates, selector->count, selector->argc, ownerObj, fgSelCall, &ctx, "    ", fgSelArgValue));
    fcBufStr(&sb, "}\n");
    return fcBufDone(g->c, &sb);
}
