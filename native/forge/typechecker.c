/* native/forge/typechecker.c - port of src/fire.Compiler/TypeChecker.cs (stub while the port is under way) */
static const char* fcUnitDemandOf(FcCtx* c, const FcTypeRef* t) {
    const char* req = fcTrRequiredUnit(t);
    if (!req) return NULL;
    return streq0(t->baseName, "float") ? fcFmt(c, "#%s", req) : req;
}
static void fcTypeCheckRun(FcCtx* c, FcList* program, FcResolveResult* resolved, bool strict, int firstUserSource, int reportFrom, int intWidth, int floatWidth, FcDict* nativeReturns) {
    (void)c; (void)program; (void)resolved; (void)strict; (void)firstUserSource; (void)reportFrom; (void)intWidth; (void)floatWidth; (void)nativeReturns;
}
