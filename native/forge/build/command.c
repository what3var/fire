/* native/forge/build/command.c - the commands of the builder (Phase D 2, commit 6f21d1a4): `forge-c build <file> [options]` (CommandLine.cs / CommandLine.Report.cs:
 * ReportBuildFiles with the engine native - LoadConfig, KeepAll, ToolchainFor, NativeBuilder.BuildSafe, NativeOutcome - and the options it reads) and
 * `forge-c buildplan <options> <file>`, the measure of Phase D 2: what FrontendDump.DumpBuildPlan of the C# forge prints. */

/* a project or a solution (Phase D 3, project/cli.c): 0 not one, 1 a program (b gets its plan, the names of target and toolchain the project's when not given),
 * 2 a library (nothing to build) */
static int (*nbProjectBuildPlan)(FcCtx* c, const char* path, NbBuild* b, NbConfig* config, const char** targetName, const char** toolchainName);
/* `buildplan <options> <file>`: the plan of a native build of one file (see FrontendDump.DumpBuildPlan) */
static void nbDumpBuildPlan(FcCtx* c, const char* options, const char* path, const char* prelude, const char* storeRoot, FcBuf* out) {
    FcList* words = fgSplit(c, options, '+');
    NbConfig* config = nbConfigNew(c);
    NbBuild b;
    const char* targetName = NULL;
    const char* toolchainName = NULL;
    const FgTarget* target;
    NbToolchain* toolchain;
    FcBuf plan = {0};
    int i;
    memset(&b, 0, sizeof b);
    b.mode = -1;
    for (i = 0; i < words->count; i++) {
        const char* w = (const char*)words->items[i];
        if (!*w || streq0(w, "default")) continue;
        if (streq0(w, "debug")) b.mode = 0;
        else if (streq0(w, "release")) b.mode = 1;
        else if (streq0(w, "performance")) b.mode = 2;
        else if (streq0(w, "split")) config->split = FG_SPLIT_ON;
        else if (streq0(w, "nosplit")) config->split = FG_SPLIT_OFF;
        else if (streq0(w, "quick")) config->quick = 0;
        else if (streq0(w, "quick1")) config->quick = 1;
        else if (streq0(w, "speed")) { config->optimize = 0; config->optimizeFromCommandLine = true; }
        else if (streq0(w, "size")) { config->optimize = 1; config->optimizeFromCommandLine = true; }
        else if (streq0(w, "i16")) b.intWidth = 16;
        else if (streq0(w, "i32")) b.intWidth = 32;
        else if (streq0(w, "f32")) b.floatWidth = 32;
        else if (fcStartsWith(w, "strip")) { config->stripping = atoi(w + 5); config->strippingFromCommandLine = true; }
        else if (fcStartsWith(w, "t:")) targetName = w + 2;
        else if (fcStartsWith(w, "tc:")) toolchainName = w + 3;
        else fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "unknown option %s", w));
    }
    b.path = path;
    b.prelude = prelude;
    b.storeRoot = storeRoot;
    b.strict = fcStrictTag(path);
    b.dryRun = true;
    b.plan = &plan;
    fcBufStr(&plan, "");
    if (nbProjectBuildPlan) {
        int kind = nbProjectBuildPlan(c, path, &b, config, &targetName, &toolchainName);
        if (kind == 2) { fcBufStr(out, "library\n"); fcBufFree(&plan); return; }
    }
    target = nbResolveTarget(c, config, targetName);
    toolchain = nbResolveToolchain(c, config, target, toolchainName, NB_PURPOSE_BUILD);
    /* (the toolchain `files` writes into a temporary folder: `<build>`) */
    {
        const char* folder = fcFmt(c, "%sfire-native-%s", nbTempPath(c), nbGuid(c));
        nbBuild(c, &b, config, target, toolchain, folder);
        if (nbDirExists(folder)) nbDeleteTree(c, folder);
    }
    fcOut8(out, fcBufDone(c, &plan));
}

/* `forge-c build <file> [options]`: a native build as `forge build <file> --engine native` makes it. */
typedef struct NbArgs {
    const char* file; const char* output; const char* targetName; const char* toolchainName; const char* configFile; const char* linkage;
    const char* engine;   /* --engine vm|native (NULL: the configuration's, else vm - build/cvm.c) */
    bool strict;          /* --types strict (Linker.ProcessDefaultTypeChecking; lax is the default) */
    int mode, floatWidth, stripping, optimize, quick, split;
    bool keepAll, keep, stats;
} NbArgs;
static int nbBuildRun(NbArgs* args, const char* prelude, const char* storeRoot);
static int nbBuildMain(int argc, char** argv, const char* prelude, const char* storeRoot) {
    NbArgs args;
    FcCtx scratch;
    FcCtx* c = &scratch;
    int i, r;
    memset(&args, 0, sizeof args);
    args.mode = -1; args.stripping = -1; args.optimize = -1; args.quick = -1; args.split = -1;
#define file args.file
#define output args.output
#define targetName args.targetName
#define toolchainName args.toolchainName
#define configFile args.configFile
#define linkage args.linkage
#define mode args.mode
#define floatWidth args.floatWidth
#define stripping args.stripping
#define optimize args.optimize
#define quick args.quick
#define split args.split
#define keepAll args.keepAll
#define keep args.keep
#define stats args.stats
    fcCtxInit(c);
    for (i = 2; i < argc; i++) {
        const char* a = argv[i];
        const char* next = i + 1 < argc ? argv[i + 1] : NULL;
#define NB_VALUE() (next ? (i++, next) : NULL)
        if (streq0(a, "-o") || streq0(a, "--output")) output = NB_VALUE();
        else if (streq0(a, "-m") || streq0(a, "--mode")) {
            const char* m = NB_VALUE();
            if (!m || !(fcAsciiIEq(m, "debug") || fcAsciiIEq(m, "release") || fcAsciiIEq(m, "performance"))) { fprintf(stderr, "-m must be followed by DEBUG, RELEASE or PERFORMANCE.\n"); return 2; }
            mode = fcAsciiIEq(m, "debug") ? 0 : fcAsciiIEq(m, "release") ? 1 : 2;
        }
        else if (streq0(a, "-t") || streq0(a, "--target")) targetName = NB_VALUE();
        else if (fcStartsWith(a, "--target=")) targetName = a + 9;
        else if (streq0(a, "--toolchain")) toolchainName = NB_VALUE();
        else if (fcStartsWith(a, "--toolchain=")) toolchainName = a + 12;
        else if (streq0(a, "--config")) configFile = NB_VALUE();
        else if (streq0(a, "--engine")) {
            const char* e = NB_VALUE();
            if (!e || !(fcAsciiIEq(e, "vm") || fcAsciiIEq(e, "native"))) { fprintf(stderr, "--engine must be followed by vm or native.\n"); return 2; }
            args.engine = fcAsciiIEq(e, "vm") ? "vm" : "native";
        }
        else if (streq0(a, "--linkage")) linkage = NB_VALUE();
        else if (streq0(a, "--types")) {
            const char* v = NB_VALUE();
            if (!v || !(fcAsciiIEq(v, "lax") || fcAsciiIEq(v, "strict"))) { fprintf(stderr, "--types must be followed by lax or strict.\n"); return 2; }
            args.strict = fcAsciiIEq(v, "strict");
        }
        else if (streq0(a, "--split")) {
            const char* v = NB_VALUE();
            if (!v || !*v || (split = fgSplitModeParse(v)) < 0) { fprintf(stderr, "--split must be followed by auto, on or off.\n"); return 2; }
        }
        else if (streq0(a, "--quick") || fcStartsWith(a, "--quick=")) {
            quick = nbQuickParseLevel(a[7] == '=' ? a + 8 : "");
            if (quick < 0) { fprintf(stderr, "--quick takes the optimisation level 0 or 1 (--quick, --quick=1).\n"); return 2; }
        }
        else if (streq0(a, "--stripping")) {
            const char* v = NB_VALUE();
            if (!v || (stripping = nbStrippingParse(c, v)) < 0) { fprintf(stderr, "--stripping must be followed by 0, 1, 2 or 3 (none, protected, methods, all).\n"); return 2; }
        }
        else if (streq0(a, "--optimize")) {
            const char* v = NB_VALUE();
            if (v && fcAsciiIEq(v, "speed")) optimize = 0; else if (v && fcAsciiIEq(v, "size")) optimize = 1;
            else { fprintf(stderr, "--optimize must be followed by speed or size.\n"); return 2; }
        }
        else if (streq0(a, "--keep-all")) keepAll = true;
        else if (streq0(a, "--keep")) keep = true;
        else if (streq0(a, "--stats")) stats = true;
        else if (streq0(a, "-f") || streq0(a, "--float")) { const char* v = NB_VALUE(); floatWidth = v ? atoi(v) : 0; }
        else if (a[0] == '-') { fprintf(stderr, "forge-c build: the option %s is not ported.\n", a); return 2; }
        else if (!file) file = a;
        else { fprintf(stderr, "forge-c build: one source file.\n"); return 2; }
#undef NB_VALUE
    }
    if (!file) { fprintf(stderr, "usage: forge-c build <file.fire> [--engine native] [-o <program>] [-t <target>] [--toolchain <name>] [--quick[=0|1]] [-m <mode>] [--split auto|on|off]\n"); return 2; }
    if (!fcFileExists(file)) { fprintf(stderr, "File not found: %s\n", file); return 2; }
    fcCtxFree(c);
    r = nbBuildRun(&args, prelude, storeRoot);
    return r;
}
#undef file
#undef output
#undef targetName
#undef toolchainName
#undef configFile
#undef linkage
#undef mode
#undef floatWidth
#undef stripping
#undef optimize
#undef quick
#undef split
#undef keepAll
#undef keep
#undef stats
static const char* pjChooseEngine(FcCtx* c, const char* given, const char* configured, const char* targetName, NbConfig* config, FcBuf* err);   /* (project/command.c) */
static int nvBuildFile(FcCtx* c, const char* path, const char* output, const char* prelude, const char* storeRoot, bool strict, int mode, FcBuf* out, FcBuf* err);   /* (cvm.c) */
static int nbBuildRun(NbArgs* argsIn, const char* prelude, const char* storeRoot) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    NbArgs args = *argsIn;
    volatile int result = 0;
    FcBuf out = {0}, err = {0};
    int i;
    fcCtxInit(c);
    fcBufStr(&out, "");
    fcBufStr(&err, "");
    FC_TRY(c) {
        NbConfig* config;
        const FgTarget* target;
        NbToolchain* toolchain;
        NbBuild b;
        NbResult r;
        const char* engine;
        if (args.configFile) {
            if (!nbFileExists(args.configFile)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "native-config: Configuration not found: %s", args.configFile));
            config = nbConfigLoad(c, args.configFile);
        } else config = nbConfigFindFor(c, args.file);
        /* KeepAll: the switches of the command line go before the configuration */
        if (args.stats) config->stats = true;
        if (args.keepAll) { config->stripping = 0; config->strippingFromCommandLine = true; }
        else if (args.stripping >= 0) { config->stripping = args.stripping; config->strippingFromCommandLine = true; }
        if (args.optimize >= 0) { config->optimize = args.optimize; config->optimizeFromCommandLine = true; }
        if (args.linkage) config->linkage = args.linkage;
        if (args.split >= 0) config->split = args.split;
        if (args.quick >= 0) config->quick = args.quick;
        /* ChooseEngine: the engine vm packs the program with the VM in C (build/cvm.c) */
        engine = pjChooseEngine(c, args.engine, config->engine, args.targetName, config, &err);
        if (!engine) result = 2;
        else if (streq0(engine, "vm")) {
            if (args.floatWidth) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: -f with the engine vm");
            c->collecting = true;
            result = nvBuildFile(c, args.file, args.output, prelude, storeRoot, args.strict, args.mode, &out, &err);
            c->collecting = false;
            for (i = 0; i < c->diags->count; i++) fcDiagLogLine(&err, (const FcDiagnostic*)c->diags->items[i]);
        }
        if (engine && streq0(engine, "native")) {
        target = nbResolveTarget(c, config, args.targetName);
        toolchain = nbResolveToolchain(c, config, target, args.toolchainName, NB_PURPOSE_BUILD);
        memset(&b, 0, sizeof b);
        b.path = args.file; b.prelude = prelude; b.storeRoot = storeRoot; b.strict = args.strict;   /* (--types; the tag `//# types: strict` is the test runner's, not forge's) */
        b.mode = args.mode; b.floatWidth = args.floatWidth; b.keepSources = args.keep;
        c->collecting = true;   /* (the warnings of the code: BuildSafe collects them) */
        r = nbBuild(c, &b, config, target, toolchain, args.output ? args.output : nbDefaultOutput(c, target));
        c->collecting = false;
        if (*r.log) { fcOut8(&out, r.log); if (!fcEndsWith(r.log, "\n")) fcBufChar(&out, '\n'); }
        for (i = 0; i < c->diags->count; i++) fcDiagLogLine(&err, (const FcDiagnostic*)c->diags->items[i]);
        if (!r.ok) {
            if (r.error) fcBufFmt(&err, "error %s: %s\n", strstr(r.error, "was not found") ? "E7003" : "E7002", r.error);
            fcBufFmt(&out, "The native build for %s failed.\n", target->name);
            result = 1;
        } else fcBufFmt(&out, "%s (native, target %s, toolchain %s)\n", r.output, target->name, nbKind(toolchain));
        }
    } FC_CATCH(c) {
        FcList* diags;
        c->collecting = false;
        for (i = 0; i < c->diags->count; i++) fcDiagLogLine(&err, (const FcDiagnostic*)c->diags->items[i]);
        if (c->err.kind == FC_ERR_NATIVE) { fcBufStr(&err, "error E7001: Not supported by the native backend yet: "); fcOut8(&err, c->err.message); fcBufChar(&err, '\n'); result = 1; }
        else if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "native-config: ")) { fcBufFmt(&err, "error E7003: %s\n", c->err.message + 15); result = 2; }
        else if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "unported: ")) { fcBufFmt(&err, "forge-c: %s\n", c->err.message); result = 2; }
        /* (the engine vm: a library of a package that cannot be built - PackageException -, a VM that cannot be had) */
        else if (c->err.kind == FC_ERR_PACKAGE) { fcBufFmt(&err, "%s\n", c->err.message); result = 1; }
        else if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "vm: ")) { fcBufFmt(&err, "%s\n", c->err.message + 4); result = 1; }
        else {
            diags = fcDiagnosticsOf(c, &c->err, NULL);
            for (i = 0; i < diags->count; i++) fcDiagLogLine(&err, (const FcDiagnostic*)diags->items[i]);
            result = 1;
        }
    } FC_END_TRY;
    fwrite(err.p, 1, err.len, stderr);
    fwrite(out.p, 1, out.len, stdout);
    fcBufFree(&out);
    fcBufFree(&err);
    fcCtxFree(c);
    return result;
}
