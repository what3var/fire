/* native/forge/gen/stats.c - src/fire.Native/CppGenerator.Stats.cs (commit 808e9788): `forge build --stats` (docs/ROADMAP.md section 16) - what the generator
 * made of each fire function (FuncStat of NativeStats.cs); build/stats.c puts it together with the linker's map. ModuleOfStat is in split.c. */

/* NativeStats.FuncStat */
struct FgFuncStat {
    const char* cname;
    const char* fireName;
    const char* module;
    int params, cparams;
    bool cReturn;
    int valueLocals, cLocals, stackSlots, shadows;
    const char* noCAbi;        /* NULL: it has a C interface */
};

/* Why a function has no C interface (CAbiOf, CAbiParams): the first reason that applies. */
static const char* NoCAbiReason(FgGen* g, FgFunc* f) {
    const char* mname;
    int margc, i;
    bool unitAny = false;
    if (DebugBuild) return "debug build";
    if (!ScalarsOn) return "no scalars";
    if (Modular || LibraryRole) return "module";
    if (f->external || f->exportName) return "exported";
    if (!(f->kind == FK_Method || f->kind == FK_Static))
        switch (f->kind) {
            case FK_Main: return "the top level";
            case FK_Ctor: return "constructor";
            case FK_Init: return "field initializers";
            case FK_Dtor: return "destructor";
            case FK_Lambda: return "lambda";
            default: return "fire thread";
        }
    if (f->specParams || f->specTArgs || f->specClassOwner) return "copy of a generic method";
    if (f->chunk->handlers->count > 0) return "try/catch";
    if (f->proto && MethodKey(g, f->proto, &mname, &margc) && fcStartsWith(mname, "set_")) return "property setter";
    for (i = 0; i < f->decoded.n; i++) if (f->decoded.p[i].op == OP_TakeCallTypeArgs) return "generic method";
    for (i = 0; i < f->decoded.n && !unitAny; i++) {
        const FgInstr* c = &f->decoded.p[i];
        if (c->op == OP_GuardLocal && c->a[0] < fgParamCount(f) && c->a[3] != 0xFFFF && fcStartsWith(fgTrimStart(fgConstStr(f->chunk, c->a[3]), '#'), "any")) unitAny = true;
    }
    return unitAny ? "no defined parameter or return (parameters with `: any` units)" : "no defined parameter or return";
}

static FgFuncStat* StatOf(FgGen* g, FgFunc* f, int valueLocals, int cLocals) {
    FgFuncStat* s = (FgFuncStat*)fcAlloc(g->c, sizeof *s);
    const char* owner = f->chunk->ownerClass ? f->chunk->ownerClass->name : "?";
    FgCAbi* abi = f->cabi;
    int i;
    s->module = ModuleOfStat(g, f);
    switch (f->kind) {
        case FK_Main: s->fireName = "(the program)"; break;
        case FK_FireBody: s->fireName = "(a fire thread)"; break;
        case FK_Lambda: s->fireName = "(a lambda)"; break;
        case FK_Ctor: s->fireName = fcFmt(g->c, "%s (constructor)", owner); break;
        case FK_Init: s->fireName = fcFmt(g->c, "%s (field initializers)", owner); break;
        case FK_Dtor: s->fireName = fcFmt(g->c, "%s (destructor)", owner); break;
        default: {
            const char* d = f->proto ? Describe(g, f->proto) : "";
            size_t n = strlen(d);
            while (*d == '\'') { d++; n--; }
            while (n > 0 && d[n - 1] == '\'') n--;
            s->fireName = fcStrN(g->c, d, n);
        }
    }
    s->cname = f->name;
    s->params = fgParamCount(f);
    if (abi) for (i = 0; i < abi->paramCount; i++) if (abi->paramType[i] != 0) s->cparams++;
    s->cReturn = abi && abi->retType != 0;
    s->valueLocals = valueLocals; s->cLocals = cLocals;
    s->stackSlots = f->root->maxDepth;
    s->shadows = fcDictCount(f->scalars);
    s->noCAbi = abi ? NULL : NoCAbiReason(g, f);
    return s;
}

/* The functions of the last round (those that are generated here): one per C name, the last. */
static FcList* FuncStats(FgGen* g) {
    FcList* all = fcListNew(g->c);
    FcList* result = fcListNew(g->c);
    FcDict* at = fcDictNew(g->c, FC_KEY_STR);   /* C name -> index + 1 in result */
    int i;
    fcListAdd(g->c, all, g->main);
    for (i = 0; i < g->funcs->count; i++) fcListAdd(g->c, all, g->funcs->items[i]);
    for (i = 0; i < all->count; i++) {
        FgFunc* f = (FgFunc*)all->items[i];
        void* known;
        if (!f->stat || f->external) continue;
        if (fcDictTry(at, f->stat->cname, &known)) result->items[FC_VALI(known) - 1] = f->stat;
        else { fcListAdd(g->c, result, f->stat); fcDictPut(at, f->stat->cname, FC_IVAL(result->count)); }
    }
    return result;
}
