/* native/forge/gen/overloads.c - src/fire.Native/CppGenerator.Overloads.cs (commit 808e9788): overloading by parameter type - the generated code scores
 * the arguments like the VM. Ported: MethodsFor, OverloadChoice, OverloadScoreExpr, OverloadClassFunctions. Not yet: the selectors of static, base and
 * constructor calls (OverloadSelectorFor, SelectorFunction) and the hints of the type checker (HintTarget, HintHolds). */

enum { FGOV_NoMatch = -1, FGOV_AnyScore = 10, FGOV_GenericScore = 20, FGOV_ObjectScore = 15, FGOV_InterfaceScore = 30, FGOV_ClassExact = 1000, FGOV_ExactKind = 1000, FGOV_IntToFloat = 900, FGOV_IntToWord = 950, FGOV_UndefinedScore = 1 };

/* The implementations a call of `name` with `argc` arguments on an object of `rc` can reach */
static FcList* MethodsFor(FgGen* g, const FcRtClass* rc, const char* name, int argc) {
    FcList* l = fcListNew(g->c);
    FgCands* set;
    FcProto* proto;
    int i;
    if (FG_OVERLOADED(rc, name, argc)) { set = fgCandidates(g->c, g->rtCands, rc, name, argc); for (i = 0; i < set->count; i++) fcListAdd(g->c, l, set->protos[i]); return l; }
    if ((proto = FG_FIND(rc, name, argc).proto) != NULL) fcListAdd(g->c, l, proto);
    return l;
}
/* A method that is not generic takes no type arguments: the ones a call named are dropped (like the VM). */
static const char* ClearCallTArgs(const FcProto* proto) { return proto->typeParamCount == 0 ? "g_callTArgs = NULL; " : ""; }
static const char* OvdFn(FgGen* g, const char* name) { return fcFmt(g->c, "%sovd_%s", Px, Mangle(g, name)); }

/* The C++ expression of the score of the value `v` for a parameter with this descriptor (Overloads.Score). */
static const char* OverloadScoreExpr(FgGen* g, const char* v, const char* descriptor, const char* ownerObj, bool generic) {
    const char* orUndefined = fcFmt(g->c, "%s.kind == K_Undefined ? %d : %d", v, FGOV_UndefinedScore, FGOV_NoMatch);
#define FG_EXACT(test) fcFmt(g->c, "(%s ? %d : %s)", (test), FGOV_ExactKind, orUndefined)
    const char* name;
    if (!descriptor || !*descriptor) return fcFmt(g->c, "(%s.kind == K_Undefined ? %d : %d)", v, FGOV_UndefinedScore, FGOV_AnyScore);
    if (streq0(descriptor, "k:int")) return FG_EXACT(fcFmt(g->c, "%s.kind == K_Int", v));
    if (streq0(descriptor, "k:float")) return fcFmt(g->c, "(%s.kind == K_Float ? %d : %s.kind == K_Int ? %d : %s)", v, FGOV_ExactKind, v, FGOV_IntToFloat, orUndefined);
    if (streq0(descriptor, "k:bool")) return FG_EXACT(fcFmt(g->c, "%s.kind == K_Bool", v));
    if (streq0(descriptor, "k:char")) return FG_EXACT(fcFmt(g->c, "%s.kind == K_Char", v));
    if (streq0(descriptor, "k:string")) return FG_EXACT(fcFmt(g->c, "%s.kind == K_String", v));
    if (streq0(descriptor, "k:lambda")) return FG_EXACT(fcFmt(g->c, "%s.kind == K_Lambda", v));
    if (streq0(descriptor, "k:array")) return FG_EXACT(fcFmt(g->c, "(%s.kind == K_Array || %s.kind == K_Buffer)", v, v));
    if (streq0(descriptor, "k:class")) return fcFmt(g->c, "(%s.kind == K_Class ? %d : %s)", v, FGOV_ObjectScore, orUndefined);
    if (streq0(descriptor, "k:word")) return fcFmt(g->c, "(%s.kind == K_Word ? %d : %s.kind == K_Int ? %d : %s)", v, FGOV_ExactKind, v, FGOV_IntToWord, orUndefined);
#undef FG_EXACT
    name = descriptor + 2;
    switch (descriptor[0]) {
        case 'c':
            fcSetAdd(g->ovClassNames, name);
            return fcFmt(g->c, "(%s.kind == K_Class ? %s(%s) : %s)", v, OvdFn(g, name), v, orUndefined);
        case 'i': {
            const char* enumerable;
            fcSetAdd(g->isOfTypes, name);
            enumerable = streq0(fcPlainName(g->c, name), "IEnumerable") ? fcFmt(g->c, "%s.kind == K_Array || %s.kind == K_Buffer || %s.kind == K_String || ", v, v, v) : "";
            return fcFmt(g->c, "(%s%s(%s) ? %d : %s)", enumerable, IsOfFn(g, name), v, FGOV_InterfaceScore, orUndefined);
        }
        case 'g': {
            FgSpec* parsed;
            FcBuf fits = {0};
            const char* typeArg = NULL;
            const char* byTypeArg;
            const char* body;
            const char* testName;
            bool usesOwner = false;
            int a, i;
            if (!*name) return fcFmt(g->c, "(%s.kind == K_Undefined ? %d : %d)", v, FGOV_UndefinedScore, FGOV_GenericScore);
            parsed = FG_SPEC(name);
            if (parsed->altCount == 0) fcBufStr(&fits, "true");
            for (a = 0; a < parsed->altCount; a++) {
                if (a) fcBufStr(&fits, " || ");
                fcBufChar(&fits, '(');
                for (i = 0; i < parsed->alts[a].count; i++) { if (i) fcBufStr(&fits, " && "); fcBufStr(&fits, HoldsExpr(g, "ovc", parsed->alts[a].conds[i])); }
                fcBufChar(&fits, ')');
            }
            if (parsed->typeParamIndex >= 0) {
                int k = parsed->typeParamIndex;
                usesOwner = *parsed->typeParamClass && !streq0(ownerObj, "NULL");
                typeArg = *parsed->typeParamClass ? (usesOwner ? TArgOfExpr(g, parsed->typeParamClass, k, "own") : NULL)
                    : (generic ? fcFmt(g->c, "(g_callTArgs ? ((TArgs)g_callTArgs)[%d] : NULL)", k) : NULL);
            }
            byTypeArg = typeArg ? fcFmt(g->c, "const TArg* tga = %s; if (tga) return fitsTArg(&ovc, tga); ", typeArg) : "";
            body = fcFmt(g->c, "{ (void)own; %sreturn %s; }", byTypeArg, fcBufDone(g->c, &fits));
            if (!(testName = (const char*)fcDictGet(g->ovGenericTests, body))) {
                testName = fcFmt(g->c, "%sovg_%d", Px, fcDictCount(g->ovGenericTests));
                fcDictPut(g->ovGenericTests, body, (void*)testName);
            }
            return fcFmt(g->c, "(%s.kind == K_Undefined ? %d : %s(%s, %s) ? %d : %d)", v, FGOV_UndefinedScore, testName, v, usesOwner ? ownerObj : "NULL", FGOV_GenericScore, FGOV_NoMatch);
        }
    }
    return fcFmt(g->c, "(%s.kind == K_Undefined ? %d : %d)", v, FGOV_UndefinedScore, FGOV_AnyScore);
}

/* The statements that pick a candidate by the scores of the arguments `a0` ... and run `call(index)` (a statement that returns). */
static const char* fgArgA(void* ctx, int i) { return fcFmt((FcCtx*)ctx, "a%d", i); }
static const char* OverloadChoice(FgGen* g, FcProto** candidates, int count, int argc, const char* ownerObj, const char* (*call)(void* ctx, int c), void* ctx, const char* indent,
    const char* (*argValue)(void* ctx, int i)) {
    FcBuf sb = {0};
    int c, i;
    fcBufFmt(&sb, "%sint ovN = g_explicitTArgs; g_explicitTArgs = 0; (void)ovN;\n", indent);
    fcBufFmt(&sb, "%sint64_t ovBest = INT64_MIN; int ovWin = -1;\n", indent);
    for (c = 0; c < count; c++) {
        FcProto* proto = candidates[c];
        const char* typeCondition = proto->typeParamCount == 0 ? "ovN == 0" : fcFmt(g->c, "ovN == 0 || ovN == %d", proto->typeParamCount);
        fcBufFmt(&sb, "%sdo { if (!(%s)) break; int64_t t = 0; int s; (void)s;", indent, typeCondition);
        for (i = 0; i < argc && i < proto->paramCount; i++)
            fcBufFmt(&sb, " s = %s; if (s < 0) break; t += s;", OverloadScoreExpr(g, argValue ? argValue(ctx, i) : fcFmt(g->c, "a%d", i), fcOverloadTypeAt(proto, i), ownerObj, proto->typeParamCount > 0));
        fcBufFmt(&sb, " if (t > ovBest) { ovBest = t; ovWin = %d; } } while (0);\n", c);
    }
    fcBufFmt(&sb, "%sswitch (ovWin) {\n", indent);
    for (c = 1; c < count; c++) fcBufFmt(&sb, "%s    case %d: %s%s\n", indent, c, ClearCallTArgs(candidates[c]), call(ctx, c));
    fcBufFmt(&sb, "%s    default: %s%s\n", indent, ClearCallTArgs(candidates[0]), call(ctx, 0));
    fcBufFmt(&sb, "%s}\n", indent);
    return fcBufDone(g->c, &sb);
}

/* `ovd_Name(v)`: the score of an object for a parameter of class Name */
static const char* OverloadClassFunctions(FgGen* g, bool classScores) {
    FcBuf sb = {0};
    fcBufStr(&sb, "");
    FC_DICT_EACH(g->ovGenericTests, e) fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static bool %s(Value ovc, Obj* own) %s\n", (const char*)e->value, (const char*)e->key);
    if (classScores) {
        FcList* names = fgSortedKeys(g->c, g->ovClassNames, true);
        int n, i;
        for (n = 0; n < names->count; n++) {
            const char* name = (const char*)names->items[n];
            FcBuf cases = {0};
            fcBufFmt(&sb, "FIRE_MAYBE_UNUSED %sint ovd_%s(Value v) {\n", HelperStorage, Mangle(g, name));
            fcBufFmt(&sb, "    if (!leafAlive(v)) return %d;\n", FGOV_UndefinedScore);
            for (i = 0; i < g->classList->count; i++) {
                FgClassInfo* cls = (FgClassInfo*)g->classList->items[i];
                const FcRtClass* rc;
                int distance = 0;
                for (rc = cls->rc; rc; rc = rc->base, distance++)
                    if (streq0(rc->name, name)) { fcBufFmt(&cases, "case %d: return %d; ", cls->id, FGOV_ClassExact - distance); break; }
            }
            if (cases.len == 0) fcBufFmt(&sb, "    return %d;\n", FGOV_NoMatch);
            else fcBufFmt(&sb, "    switch (objAt(v)->cls) { %sdefault: return %d; }\n", fcBufDone(g->c, &cases), FGOV_NoMatch);
            fcBufStr(&sb, "}\n");
        }
    }
    return fcBufDone(g->c, &sb);
}
