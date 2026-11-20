/* native/forge/tools/commands.c - the rest of the command line (Phase D 4, commit b833e71b): src/fire.Compiler/CommandLine.cs (CommandLineParser.Parse
 * with its Usage, CommandLineRunner.Run, Execute, RunProject, ExecutePlan, Bytecode, SourceFiles, BaseDirectory), CommandLine.Report.cs (TextOf, ReadSource)
 * and of src/fire.Compiler/Program.cs `tables`, `prebuild-standard`, `stats`, which say what they lack. The link of several sources - the files of the command
 * line, named by their file names (Linker.SourcePaths) or as texts (no paths) - is the linker of a plan (project/linkplan.c) with a plan of its own that
 * has no project (PjPlan.files); `-D`, `-f`, `-i` and `--types` go into its settings and overrides as the linker of C# takes them.
 *
 * `run` compiles like `bytecode` and runs the program file in the VM in C (`fire run <program.fbc> -- <arguments>`; the C# forge runs it in its own VM in the
 * process - the VM in C prints the same, an exception nobody caught as the same line). What forge-c does not port says `unported: ...`. */

/* ---- CommandLineParser ---- */
typedef enum TcCommand { TC_RUN, TC_BUILD, TC_NATIVE, TC_TEST, TC_HELP, TC_BYTECODE, TC_DUMP, TC_PUBLISH } TcCommand;
typedef struct TcOptions {
    TcCommand command;
    FcList* files;                 /* the source files as given */
    int mode;                      /* -m: -1 none, 0 debug, 1 release, 2 performance */
    int floatWidth, intWidth;      /* -f, -i: 0 none */
    int types;                     /* --types: -1 none, 0 lax, 1 strict */
    int globalJobs;                /* --global-jobs: -1 none, else GJM_* */
    FcList* defines;               /* -D */
    const char* targetName; const char* engine; const char* toolchainName; const char* testFilter; const char* configFile; const char* linkage;
    const char* host; const char* projectName; const char* report;
    FcList* programArguments;      /* after `--` (run); NULL: none */
    bool keep, keepAll, stats, debugInfo, checkOnly, all;
    int stripping, optimize, quick, split;   /* -1: none */
    FcList* overlays;              /* pairs: the full path of a source, the full path of the file with its text */
    const char* output;            /* -o (NULL: the default of the command) */
    const char* outputFile;        /* OutputFile: -o, else out.exe / out.c / out.fbc */
    const char* error;             /* an invalid command line: the message */
} TcOptions;

static const char* const tcUsage =
#include "usage.inc"
    ;

static const char* tcUnquote(FcCtx* c, const char* text) {
    const char* t = fgTrim(c, text);
    size_t n = strlen(t);
    if (n >= 2 && t[0] == '"' && t[n - 1] == '"') return fcStrN(c, t + 1, n - 2);
    return t;
}
/* TryOption: `-m X` (the value in the next argument) and `-m=X` (inline); the names case-insensitively */
static bool tcTryOption(const char* arg, const char* shortName, const char* longName, const char** inlineValue) {
    const char* names[2];
    int k;
    names[0] = shortName; names[1] = longName;
    *inlineValue = NULL;
    for (k = 0; k < 2; k++) {
        size_t n = strlen(names[k]);
        if (fcAsciiIEq(arg, names[k])) return true;
        if (strlen(arg) > n && arg[n] == '=' && strncasecmp(arg, names[k], n) == 0) { *inlineValue = arg + n + 1; return true; }
    }
    return false;
}
static bool tcIsSymbol(const char* s) {
    const char* p = s;
    if (!((*p >= 'A' && *p <= 'Z') || (*p >= 'a' && *p <= 'z') || *p == '_')) return false;
    for (p++; *p; p++) if (!((*p >= 'A' && *p <= 'Z') || (*p >= 'a' && *p <= 'z') || (*p >= '0' && *p <= '9') || *p == '_')) return false;
    return true;
}
static bool tcIn(TcCommand c, TcCommand a, TcCommand b, TcCommand d) { return c == a || c == b || c == d; }
#define TC_FAIL(msg) do { o->error = (msg); return o; } while (0)
#define TC_NEXT(inl) ((inl) ? (inl) : (i + 1 < argc ? argv[++i] : NULL))
/* CommandLineParser.Parse (argv[1] is the command) */
static TcOptions* tcParse(FcCtx* c, int argc, char** argv) {
    TcOptions* o = (TcOptions*)fcAlloc(c, sizeof *o);
    const char* cmd = argc >= 2 ? argv[1] : "";
    int i;
    o->files = fcListNew(c); o->defines = fcListNew(c); o->overlays = fcListNew(c);
    o->mode = -1; o->types = -1; o->globalJobs = -1; o->stripping = o->optimize = o->quick = o->split = -1;
    if (argc < 2 || streq0(cmd, "-h") || streq0(cmd, "--help") || streq0(cmd, "/?") || streq0(cmd, "help") || streq0(cmd, "-?")) { o->command = TC_HELP; return o; }
    if (fcAsciiIEq(cmd, "run")) o->command = TC_RUN;
    else if (fcAsciiIEq(cmd, "build")) o->command = TC_BUILD;
    else if (fcAsciiIEq(cmd, "publish")) o->command = TC_PUBLISH;
    else if (fcAsciiIEq(cmd, "native")) o->command = TC_NATIVE;
    else if (fcAsciiIEq(cmd, "test")) o->command = TC_TEST;
    else if (fcAsciiIEq(cmd, "bytecode")) o->command = TC_BYTECODE;
    else if (fcAsciiIEq(cmd, "dump")) o->command = TC_DUMP;
    else { o->command = TC_HELP; TC_FAIL(fcFmt(c, "Unknown command '%s' (expected: run, build, publish, native, test, bytecode, dump or data).", cmd)); }
    for (i = 2; i < argc; i++) {
        const char* arg = argv[i];
        const char* inl;
        TcCommand k = o->command;
        if (streq0(arg, "--")) {
            int j;
            if (k != TC_RUN) TC_FAIL("Arguments for the program (after --) are only available with run.");
            o->programArguments = fcListNew(c);
            for (j = i + 1; j < argc; j++) fcListAdd(c, o->programArguments, argv[j]);
            break;
        }
        if (tcTryOption(arg, "-m", "--mode", &inl)) {
            const char* v = TC_NEXT(inl);
            const char* u;
            if (!v) TC_FAIL("-m must be followed by the mode (DEBUG, RELEASE or PERFORMANCE).");
            u = fgTrim(c, tcUnquote(c, v));
            if (fcAsciiIEq(u, "debug")) o->mode = 0; else if (fcAsciiIEq(u, "release")) o->mode = 1; else if (fcAsciiIEq(u, "performance")) o->mode = 2;
            else TC_FAIL(fcFmt(c, "Unknown mode '%s' (allowed: DEBUG, RELEASE, PERFORMANCE).", v));
        } else if (tcTryOption(arg, "-t", "--target", &inl)) {
            const char* v;
            if (k != TC_NATIVE && k != TC_BUILD) TC_FAIL("-t is only available with build and native.");
            v = TC_NEXT(inl);
            if (!v) TC_FAIL("-t must be followed by the target name.");
            o->targetName = tcUnquote(c, v);
        } else if (tcTryOption(arg, "--engine", "--engine", &inl)) {
            const char* v;
            if (k != TC_BUILD && k != TC_TEST) TC_FAIL("--engine is only available with build and test.");
            v = TC_NEXT(inl);
            if (!v || !(fcAsciiIEq(tcUnquote(c, v), "vm") || fcAsciiIEq(tcUnquote(c, v), "native"))) TC_FAIL("--engine must be followed by vm or native.");
            o->engine = fcAsciiIEq(tcUnquote(c, v), "vm") ? "vm" : "native";
        } else if (tcTryOption(arg, "--toolchain", "--toolchain", &inl)) {
            const char* v;
            if (!tcIn(k, TC_BUILD, TC_TEST, TC_PUBLISH)) TC_FAIL("--toolchain is only available with build, test and publish.");
            v = TC_NEXT(inl);
            if (!v || !*fgTrim(c, v)) TC_FAIL("--toolchain must be followed by the toolchain name.");
            o->toolchainName = tcUnquote(c, v);
        } else if (tcTryOption(arg, "--filter", "--filter", &inl)) {
            const char* v;
            if (k != TC_TEST) TC_FAIL("--filter is only available with test.");
            v = TC_NEXT(inl);
            if (!v || !*fgTrim(c, v)) TC_FAIL("--filter must be followed by the tests (Class.Method or Class, separated by commas).");
            o->testFilter = tcUnquote(c, v);
        } else if (tcTryOption(arg, "--config", "--config", &inl)) {
            const char* v;
            if (!tcIn(k, TC_NATIVE, TC_BUILD, TC_TEST)) TC_FAIL("--config is only available with build, native and test.");
            v = TC_NEXT(inl);
            if (!v || !*fgTrim(c, v)) TC_FAIL("--config must be followed by the file name.");
            o->configFile = tcUnquote(c, v);
        } else if (tcTryOption(arg, "-p", "--project", &inl)) {
            const char* v = TC_NEXT(inl);
            if (!v || !*fgTrim(c, v)) TC_FAIL("-p must be followed by the name of a project.");
            o->projectName = tcUnquote(c, v);
        } else if (fcAsciiIEq(arg, "--linkage")) {
            const char* v;
            if (!tcIn(k, TC_BUILD, TC_NATIVE, TC_TEST)) TC_FAIL("--linkage is only available with build, native and test.");
            v = i + 1 < argc ? fcAsciiLower(c, argv[++i]) : NULL;
            if (!v || !(streq0(v, "source") || streq0(v, "static") || streq0(v, "shared") || streq0(v, "plugin"))) TC_FAIL("--linkage must be followed by source, static, shared or plugin.");
            o->linkage = v;
        } else if (fcAsciiIEq(arg, "--split")) {
            const char* v;
            if (!tcIn(k, TC_BUILD, TC_NATIVE, TC_TEST)) TC_FAIL("--split is only available with build, native and test.");
            v = i + 1 < argc ? fcAsciiLower(c, argv[++i]) : NULL;
            if (!v || !*v || (o->split = fgSplitModeParse(v)) < 0) TC_FAIL("--split must be followed by auto, on or off.");
        } else if (tcTryOption(arg, "--quick", "--quick", &inl)) {
            if (k != TC_BUILD && k != TC_PUBLISH) TC_FAIL("--quick is only available with build and publish.");
            o->quick = nbQuickParseLevel(inl ? tcUnquote(c, inl) : NULL);
            if (o->quick < 0) TC_FAIL("--quick takes the optimisation level 0 or 1 (--quick, --quick=1).");
        } else if (fcAsciiIEq(arg, "--host")) {
            const char* v;
            if (k != TC_BUILD) TC_FAIL("--host is only available with build (--linkage plugin).");
            v = i + 1 < argc ? argv[++i] : NULL;
            if (!v || !*fgTrim(c, v)) TC_FAIL("--host must be followed by the program the plugin is built for.");
            o->host = tcUnquote(c, v);
        } else if (fcAsciiIEq(arg, "--stripping")) {
            const char* v;
            if (!tcIn(k, TC_BUILD, TC_NATIVE, TC_TEST)) TC_FAIL("--stripping is only available with build, native and test.");
            v = i + 1 < argc ? argv[++i] : NULL;
            if (!v || (o->stripping = nbStrippingParse(c, v)) < 0) TC_FAIL("--stripping must be followed by 0, 1, 2 or 3 (none, protected, methods, all).");
        } else if (fcAsciiIEq(arg, "--optimize")) {
            const char* v;
            if (!tcIn(k, TC_BUILD, TC_NATIVE, TC_TEST)) TC_FAIL("--optimize is only available with build, native and test.");
            v = i + 1 < argc ? fgTrim(c, argv[++i]) : NULL;
            if (v && fcAsciiIEq(v, "speed")) o->optimize = 0; else if (v && fcAsciiIEq(v, "size")) o->optimize = 1;
            else TC_FAIL("--optimize must be followed by speed or size.");
        } else if (fcAsciiIEq(arg, "--keep-all")) {
            if (!tcIn(k, TC_BUILD, TC_NATIVE, TC_TEST)) TC_FAIL("--keep-all is only available with build, native and test.");
            o->keepAll = true;
        } else if (tcTryOption(arg, "--report", "--report", &inl)) {
            const char* v;
            if (!tcIn(k, TC_BUILD, TC_PUBLISH, TC_BYTECODE)) TC_FAIL("--report is only available with build, publish and bytecode.");
            v = TC_NEXT(inl);
            if (!v || !fcAsciiIEq(tcUnquote(c, v), "json")) TC_FAIL("--report must be followed by json.");
            o->report = "json";
        } else if (tcTryOption(arg, "--overlay", "--overlay", &inl)) {
            const char* v;
            const char* text;
            const char* eq;
            int at;
            if (!tcIn(k, TC_BUILD, TC_PUBLISH, TC_BYTECODE)) TC_FAIL("--overlay is only available with build, publish and bytecode.");
            v = TC_NEXT(inl);
            text = v ? tcUnquote(c, v) : NULL;
            eq = text ? strrchr(text, '=') : NULL;
            at = eq ? (int)(eq - text) : -1;
            if (!v || at <= 0 || at == (int)strlen(text) - 1) TC_FAIL("--overlay must be followed by <source>=<file with its text>.");
            {
                const char* source = fcFullPath(c, ".", fcStrN(c, text, (size_t)at));
                const char* file = fcFullPath(c, ".", eq + 1);
                int m;
                bool replaced = false;
                for (m = 0; m < o->overlays->count; m += 2) if (streq0((const char*)o->overlays->items[m], source)) { o->overlays->items[m + 1] = (void*)file; replaced = true; }
                if (!replaced) { fcListAdd(c, o->overlays, (void*)source); fcListAdd(c, o->overlays, (void*)file); }
            }
        } else if (fcAsciiIEq(arg, "--debug-info")) {
            if (k != TC_BUILD) TC_FAIL("--debug-info is only available with build.");
            o->debugInfo = true;
        } else if (fcAsciiIEq(arg, "--check")) {
            if (k != TC_BUILD) TC_FAIL("--check is only available with build.");
            o->checkOnly = true;
        } else if (fcAsciiIEq(arg, "--all")) {
            if (k != TC_BUILD) TC_FAIL("--all is only available with build.");
            o->all = true;
        } else if (fcAsciiIEq(arg, "--stats")) {
            if (k != TC_BUILD) TC_FAIL("--stats is only available with build.");
            o->stats = true;
        } else if (fcAsciiIEq(arg, "--keep")) {
            if (k != TC_BUILD) TC_FAIL("--keep is only available with build.");
            o->keep = true;
        } else if (tcTryOption(arg, "--types", "--types", &inl)) {
            const char* v = TC_NEXT(inl);
            if (!v || !(fcAsciiIEq(tcUnquote(c, v), "lax") || fcAsciiIEq(tcUnquote(c, v), "strict"))) TC_FAIL("--types must be followed by lax or strict.");
            o->types = fcAsciiIEq(tcUnquote(c, v), "strict") ? 1 : 0;
        } else if (tcTryOption(arg, "--global-jobs", "--global-jobs", &inl)) {
            const char* v = TC_NEXT(inl);
            o->globalJobs = v ? nbGlobalJobsParse(c, tcUnquote(c, v)) : -1;
            if (o->globalJobs < 0) TC_FAIL("--global-jobs must be followed by safepoints, sleep or sync.");
        } else if (tcTryOption(arg, "-f", "--float", &inl)) {
            const char* v = TC_NEXT(inl);
            if (!v) TC_FAIL("-f must be followed by the float precision (32 or 64).");
            if (!streq0(tcUnquote(c, v), "32") && !streq0(tcUnquote(c, v), "64")) TC_FAIL(fcFmt(c, "Unknown float precision '%s' (allowed: 32, 64).", v));
            o->floatWidth = atoi(tcUnquote(c, v));
        } else if (tcTryOption(arg, "-i", "--int", &inl)) {
            const char* v = TC_NEXT(inl);
            if (!v) TC_FAIL("-i must be followed by the width of int (16, 32 or 64).");
            if (!streq0(tcUnquote(c, v), "16") && !streq0(tcUnquote(c, v), "32") && !streq0(tcUnquote(c, v), "64")) TC_FAIL(fcFmt(c, "Unknown int width '%s' (allowed: 16, 32, 64).", v));
            o->intWidth = atoi(tcUnquote(c, v));
        } else if (tcTryOption(arg, "-D", "--define", &inl) || (strlen(arg) > 2 && strncmp(arg, "-D", 2) == 0 && (inl = arg + 2) != NULL)) {
            const char* v = TC_NEXT(inl);
            if (!v || !tcIsSymbol(tcUnquote(c, v))) TC_FAIL("-D must be followed by a symbol name (letters, digits, underscore).");
            fcListAdd(c, o->defines, (void*)tcUnquote(c, v));
        } else if (tcTryOption(arg, "-o", "--out", &inl)) {
            const char* v;
            if (!(k == TC_BUILD || k == TC_NATIVE || k == TC_BYTECODE || k == TC_DUMP)) TC_FAIL("-o is only available with build, native, bytecode and dump.");
            v = TC_NEXT(inl);
            if (!v || !*fgTrim(c, v)) TC_FAIL("-o must be followed by the file name.");
            o->output = tcUnquote(c, v);
        } else if (arg[0] == '-' && arg[1]) {
            TC_FAIL(fcFmt(c, "Unknown option '%s'.", arg));
        } else {
            const char* file = tcUnquote(c, arg);
            if (*file) fcListAdd(c, o->files, (void*)file);
        }
    }
    if (o->files->count == 0 && o->command != TC_TEST) TC_FAIL("No source file was specified.");
    o->outputFile = o->output ? o->output : o->command == TC_NATIVE ? "out.c" : o->command == TC_BYTECODE ? "out.fbc" : "out.exe";
    return o;
}
#undef TC_FAIL
#undef TC_NEXT

/* the error of the command line: the message, an empty line and the usage on stderr (exit 2) */
static int tcUsageError(const TcOptions* o) {
    FcBuf b = {0};
    fcBufStr(&b, "");
    fcOut8(&b, o->error);
    fputs(b.p, stderr);
    fprintf(stderr, "\n\n%s\n", tcUsage);
    fcBufFree(&b);
    return 2;
}

/* ---- the sources of the command line ---- */
static bool tcIsProjectFile(const char* path) { return pjIsProjectFile(path); }
/* TextOf: the text of a source from an overlay (fc8), else NULL */
static const char* tcTextOf(FcCtx* c, const TcOptions* o, const char* path) {
    const char* full = fcFullPath(c, ".", path);
    int i;
    for (i = 0; i < o->overlays->count; i += 2)
        if (streq0((const char*)o->overlays->items[i], full)) {
            FcU16 t = {0};
            const char* text;
            if (!fcReadText((const char*)o->overlays->items[i + 1], &t)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "unported: an overlay that cannot be read ('%s')", (const char*)o->overlays->items[i + 1]));
            text = fc8FromU16(c, t.p, t.len);
            fcU16Free(&t);
            return text;
        }
    return NULL;
}
/* ReadSource: an overlay, else the file; NULL when it does not exist */
static const char* tcReadSource(FcCtx* c, const TcOptions* o, const char* path) {
    const char* text = tcTextOf(c, o, path);
    FcU16 t = {0};
    if (text) return text;
    if (!nbFileExists(path) || !fcReadText(path, &t)) return NULL;
    text = fc8FromU16(c, t.p, t.len);
    fcU16Free(&t);
    return text;
}
/* the overlays in a plan: the texts of its files and of its libraries' files (BuildPlan.Create with textOf) */
static void tcApplyOverlays(FcCtx* c, const TcOptions* o, PjPlan* plan) {
    int i, k;
    if (o->overlays->count == 0) return;
    for (i = 0; i < plan->sources->count; i++) {
        PjSource* s = (PjSource*)plan->sources->items[i];
        const char* text = tcTextOf(c, o, s->path);
        if (text && pjIsMarkupFile(s->path)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: an overlay of a markup file");
        if (text) s->text = text;
    }
    for (k = 0; k < plan->libraries->count; k++) {
        PjLibrary* l = (PjLibrary*)plan->libraries->items[k];
        for (i = 0; i < l->sources->count; i++) {
            PjSource* s = (PjSource*)l->sources->items[i];
            const char* text = tcTextOf(c, o, s->path);
            if (text) s->text = text;
        }
    }
}
/* a plan without a project: the sources of the command line (files 1: named by their file names, from their folders; 2: texts from baseFolder) with the
 * defines and the type checking of the command line */
static PjPlan* tcFilesPlan(FcCtx* c, const TcOptions* o, FcList* paths, FcList* texts, int files, const char* baseFolder) {
    PjPlan* plan = (PjPlan*)fcAlloc(c, sizeof *plan);
    PjLoaded* loaded = (PjLoaded*)fcAlloc(c, sizeof *loaded);
    PjProject* project = (PjProject*)fcAlloc(c, sizeof *project);
    PjSettings* settings = pjSettingsNew(c);
    char cwd[4096];
    int i;
    if (!getcwd(cwd, sizeof cwd)) strcpy(cwd, ".");
    settings->defines = fcListCopy(c, o->defines);
    if (o->types == 1) settings->typeChecking = "strict";
    project->name = "program"; project->type = PJ_EXE; project->settings = settings; project->directory = fcStr(c, cwd); project->serializable = -1; project->syncGitignore = -1;
    project->filePath = paths->count > 0 ? fcFullPath(c, ".", (const char*)paths->items[0]) : fcStr(c, cwd);
    loaded->project = project;
    plan->project = loaded;
    plan->sources = fcListNew(c);
    for (i = 0; i < paths->count; i++) {
        PjSource* s = (PjSource*)fcAlloc(c, sizeof *s);
        s->path = fcFullPath(c, ".", (const char*)paths->items[i]);
        s->text = (const char*)texts->items[i];
        s->projectName = project->name;
        fcListAdd(c, plan->sources, s);
    }
    plan->libraries = fcListNew(c); plan->packages = fcListNew(c); plan->errors = fcListNew(c);
    plan->settings = settings;
    plan->files = files;
    plan->baseFolder = baseFolder;
    return plan;
}
/* a project's plan with the defines and the type checking of the command line (Linker.Defines with the project's, ProcessDefaultTypeChecking before its own) */
static void tcPlanOptions(FcCtx* c, const TcOptions* o, PjPlan* plan) {
    PjSettings* s;
    int i;
    if (o->defines->count == 0 && o->types < 0) return;
    s = (PjSettings*)fcAlloc(c, sizeof *s);
    *s = *plan->settings;
    s->defines = s->defines ? fcListCopy(c, s->defines) : fcListNew(c);
    for (i = 0; i < o->defines->count; i++) fcListAdd(c, s->defines, o->defines->items[i]);
    if (o->types >= 0) s->typeChecking = o->types == 1 ? "strict" : "lax";
    plan->settings = s;
}
/* the names of the files (Path.GetFileName: the text of `start`, the title of a compile) */
static FcList* tcFileNames(FcCtx* c, const TcOptions* o) {
    FcList* names = fcListNew(c);
    int i;
    for (i = 0; i < o->files->count; i++) fcListAdd(c, names, (void*)fcStr(c, fcBaseName((const char*)o->files->items[i])));
    return names;
}
/* the folder of the first file (BaseDirectory) */
static const char* tcBaseDirectory(FcCtx* c, const TcOptions* o) { return o->files->count > 0 ? fcDirectoryName(c, fcFullPath(c, ".", (const char*)o->files->items[0])) : NULL; }
/* the plan of the files of the command line (their texts: an overlay, else the file); NULL with *missing the file that does not exist */
static PjPlan* tcPlanOfFiles(FcCtx* c, const TcOptions* o, int files, const char** missing) {
    FcList* texts = fcListNew(c);
    int i;
    *missing = NULL;
    for (i = 0; i < o->files->count; i++) {
        const char* text = tcReadSource(c, o, (const char*)o->files->items[i]);
        if (!text) { *missing = (const char*)o->files->items[i]; return NULL; }
        fcListAdd(c, texts, (void*)text);
    }
    return tcFilesPlan(c, o, o->files, texts, files, files == 2 ? tcBaseDirectory(c, o) : NULL);
}
/* the link of a plan for the VM (Linker.CompileAndLink without a target), the widths and the mode of the command line; *store gets its store */
static FcLinked* tcLinkVm(FcCtx* c, const TcOptions* o, PjPlan* plan, const char* prelude, const char* storeRoot, FcStore** store, FcNatives** registry) {
    PjLinkIn in;
    memset(&in, 0, sizeof in);
    in.plan = plan; in.prelude = prelude; in.storeRoot = storeRoot; in.modeOverride = o->mode; in.floatWidthOverride = o->floatWidth; in.intWidthOverride = o->intWidth;
    in.storeOut = store;
    return pjLink(c, &in, registry);
}

/* ---- the program in the VM in C (Execute, ExecutePlan) ---- */
/* the folders of the libraries a program needs (FIRE_PKG_PATH of the VM: the libraries of its packages, built now, and of a project's own natives) */
static const char* tcPkgPath(FcCtx* c, FcList* libraries) {
    FcList* dirs = fcListNew(c);
    const char* old = getenv("FIRE_PKG_PATH");
    int i;
    for (i = 0; libraries && i < libraries->count; i++) {
        const char* d = fcDirectoryName(c, fcFullPath(c, ".", (const char*)libraries->items[i]));
        if (!fcListHasStr(dirs, d)) fcListAdd(c, dirs, (void*)d);
    }
    if (old && *old) fcListAdd(c, dirs, (void*)old);
    return fgJoin(c, dirs, ":");
}
/* runs the program file with the VM of its widths: its stdin, stdout and stderr are this process's; the exit code of the VM */
static int tcRunInVm(FcCtx* c, FcLinked* linked, FcList* libraries, FcList* arguments) {
    FcBuf fbc = {0};
    const char* error = NULL;
    const char* vm = nvLocate(c, linked->intWidth, linked->floatWidth, NULL, &error);
    const char* dir;
    const char* file;
    const char* pkgPath = tcPkgPath(c, libraries);
    char** argv;
    FILE* f;
    pid_t pid;
    int status = 0, n = arguments ? arguments->count : 0, i;
    if (!vm) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "vm: %s", error));
    dir = trTempFolder(c, "fire-run-");
    file = fcFmt(c, "%s/program.fbc", dir);
    fcProgramFileWrite(c, linked, true, &fbc);
    f = fopen(file, "wb");
    if (f) { fwrite(fbc.p, 1, fbc.len, f); fclose(f); }
    fcBufFree(&fbc);
    argv = (char**)fcAlloc(c, sizeof(char*) * (size_t)(n + 5));
    argv[0] = (char*)vm; argv[1] = (char*)"run"; argv[2] = (char*)file; argv[3] = (char*)"--";
    for (i = 0; i < n; i++) argv[4 + i] = (char*)arguments->items[i];
    argv[4 + n] = NULL;
    fflush(stdout);
    fflush(stderr);
    pid = fork();
    if (pid == 0) {
        if (*pkgPath) setenv("FIRE_PKG_PATH", pkgPath, 1);
        execv(argv[0], argv);
        _exit(127);
    }
    if (pid < 0 || waitpid(pid, &status, 0) < 0) status = -1;
    trRemoveTree(dir);
    if (status < 0) return 1;
    if (WIFEXITED(status)) return WEXITSTATUS(status);
    return 1;
}

/* DescribeErrors on stderr (and the warnings of the step as they came before) */
static void tcWarnings(FcCtx* step) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = 0; i < step->diags->count; i++) if (((FcDiagnostic*)step->diags->items[i])->severity == FC_SEV_WARNING) fcDiagLogLine(&b, (const FcDiagnostic*)step->diags->items[i]);
    fputs(b.p, stderr);
    fcBufFree(&b);
    step->diags->count = 0;
}
static int tcCompileFailed(FcCtx* step) {
    FcBuf b = {0};
    fcBufStr(&b, "");
    tcWarnings(step);
    if (step->err.kind == FC_ERR_INTERNAL && step->err.message && fcStartsWith(step->err.message, "unported: ")) { fprintf(stderr, "forge-c: %s\n", step->err.message); return 2; }
    if (step->err.kind == FC_ERR_INTERNAL && step->err.message && fcStartsWith(step->err.message, "vm: ")) { fprintf(stderr, "%s\n", step->err.message + 4); return 1; }
    if (step->err.kind == FC_ERR_PACKAGE) { fprintf(stderr, "%s\n", step->err.message); return 1; }
    if (!pjIsCompileErrorKind(step->err.kind) && step->err.kind != FC_ERR_PROJECT) { fprintf(stderr, "forge-c: internal: %s\n", step->err.message ? step->err.message : "?"); return 2; }
    pjDescribeErrors(step, &b);
    fwrite(b.p, 1, b.len, stderr);
    fcBufFree(&b);
    return 1;
}

/* `run <files> [-- <arguments>]` (Execute) and `run <project|solution>` (RunProject, ExecutePlan) */
static int tcRun(FcCtx* c, const TcOptions* o, const char* prelude, const char* storeRoot) {
    FcCtx step;
    PjPlan* volatile plan = NULL;
    FcList* volatile arguments = o->programArguments;
    volatile int result = 0;
    const char* file = (const char*)o->files->items[0];
    if (o->files->count == 1 && tcIsProjectFile(file)) {
        PjWorkspace* w;
        if (!nbFileExists(file)) { fprintf(stderr, "File not found: %s\n", file); return 2; }
        FC_TRY(c) {
            w = pjWorkspaceOpen(c, file);
            plan = pjPlanOrThrow(w, pjSelectProject(w, o->projectName));
        } FC_CATCH(c) {
            if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
            fprintf(stderr, "%s\n", c->err.message);
            result = 2;
        } FC_END_TRY;
        if (result) return result;
        if (plan->project->project->type == PJ_LIBRARY) { fprintf(stderr, "'%s' is a library: it has no entry point to run.\n", plan->project->project->name); return 1; }
        tcPlanOptions(c, o, plan);
        if (!arguments) arguments = plan->settings->arguments ? plan->settings->arguments : fcListNew(c);
    } else {
        const char* missing;
        int i;
        for (i = 0; i < o->files->count; i++) if (!tcReadSource(c, o, (const char*)o->files->items[i])) { fprintf(stderr, "File not found: %s\n", (const char*)o->files->items[i]); return 2; }
        plan = tcPlanOfFiles(c, o, 1, &missing);
    }
    nvRefuseDotnet(c);
    nvStoreRoot = storeRoot;
    fcCtxInit(&step);
    step.collecting = true;
    FC_TRY(&step) {
        FcStore* store = NULL;
        FcNatives* registry = NULL;
        FcLinked* linked = tcLinkVm(&step, o, plan, prelude, storeRoot, &store, &registry);
        FcList* libraries;
        tcWarnings(&step);
        libraries = nvEnsureLibraries(&step, linked, store, registry, NULL);
        result = tcRunInVm(&step, linked, libraries, arguments);
    } FC_CATCH(&step) {
        result = tcCompileFailed(&step);
    } FC_END_TRY;
    fcCtxFree(&step);
    return result;
}

/* `bytecode <files> | <project|solution> [-p <project>] [-o <out.fbc>]` (Bytecode) - the link of the files or of the plan as a program file */
static int tcBytecode(FcCtx* c, const TcOptions* o, const char* prelude, const char* storeRoot) {
    FcCtx step;
    PjPlan* volatile plan = NULL;
    volatile int result = 0;
    int i;
    for (i = 0; i < o->files->count; i++) if (!nbFileExists((const char*)o->files->items[i])) { fprintf(stderr, "File not found: %s\n", (const char*)o->files->items[i]); return 2; }
    for (i = 0; i < o->files->count; i++)
        if (tcIsProjectFile((const char*)o->files->items[i])) {
            PjWorkspace* w;
            if (o->files->count != 1) { fprintf(stderr, "%s takes source files or one project.\n", o->command == TC_DUMP ? "dump" : "bytecode"); return 2; }
            FC_TRY(c) {
                w = pjWorkspaceOpen(c, (const char*)o->files->items[0]);
                plan = pjPlanOrThrow(w, pjSelectProject(w, o->projectName));
            } FC_CATCH(c) {
                if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
                fprintf(stderr, "%s\n", c->err.message);
                result = 2;
            } FC_END_TRY;
            if (result) return result;
            if (plan->project->project->type == PJ_LIBRARY) { fprintf(stderr, "'%s' is a library: it is no program by itself (a program that imports it carries it).\n", plan->project->project->name); return 2; }
            tcPlanOptions(c, o, plan);
            break;
        }
    if (o->command == TC_DUMP) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: dump (ProgramDump.Text - `forge-c fbc` measures the same program file)");
    if (!plan) {
        const char* missing;
        plan = tcPlanOfFiles(c, o, 1, &missing);
    }
    fcCtxInit(&step);
    step.collecting = true;
    FC_TRY(&step) {
        FcLinked* linked = tcLinkVm(&step, o, plan, prelude, storeRoot, NULL, NULL);
        FcBuf bytes = {0};
        FILE* f;
        tcWarnings(&step);
        fcBufStr(&bytes, "");
        bytes.len = 0;
        fcProgramFileWrite(&step, linked, true, &bytes);
        f = fopen(o->outputFile, "wb");
        if (!f) fcThrow(&step, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(&step, "unported: the program file cannot be written ('%s')", o->outputFile));
        fwrite(bytes.p, 1, bytes.len, f);
        fclose(f);
        printf("%s (%zu bytes)\n", fcFullPath(&step, ".", o->outputFile), bytes.len);
        fcBufFree(&bytes);
    } FC_CATCH(&step) {
        result = tcCompileFailed(&step);
    } FC_END_TRY;
    fcCtxFree(&step);
    return result;
}

static int tcReportMain(int argc, char** argv, const char* prelude, const char* storeRoot);   /* (tools/report.c) */
static bool tcWantsReport(int argc, char** argv);

/* the commands forge-c takes here (CommandLineRunner.Run and the rest of Program.cs); -1: the caller's (the measures and the commands of the earlier steps) */
static int tcMain(int argc, char** argv, const char* prelude, const char* storeRoot) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    TcOptions* o;
    volatile int result = -1;
    const char* cmd;
    int i;
    bool simple;
    if (argc < 2) return -1;
    cmd = argv[1];
    if (tcWantsReport(argc, argv)) return tcReportMain(argc, argv, prelude, storeRoot);
    if (streq0(cmd, "tables") && argc == 3) { fprintf(stderr, "forge-c: unported: tables (Tables.Write: the tables of native/tables are written from the C# definitions; forge-c reads them)\n"); return 2; }
    if (streq0(cmd, "prebuild-standard") && argc >= 4) { fprintf(stderr, "forge-c: unported: prebuild-standard (StandardSources.Prebuild; `forge-c pkglib --all` builds the libraries of the store)\n"); return 2; }
    if (streq0(cmd, "stats")) {
        if (argc != 4) { fprintf(stderr, "usage: stats <before.stats.json> <after.stats.json>  (the reports `build ... --stats` writes next to a program)\n"); return 2; }
        fprintf(stderr, "forge-c: unported: stats (NativeStats.FromJson, Compare - with --stats, Phase D 2)\n");
        return 2;
    }
    if (fcAsciiIEq(cmd, "data")) { fprintf(stderr, "forge-c: unported: data (DataCommand: the driver of the schema in the VM)\n"); return 2; }
    if (!(fcAsciiIEq(cmd, "run") || fcAsciiIEq(cmd, "native") || fcAsciiIEq(cmd, "dump") || fcAsciiIEq(cmd, "bytecode") || streq0(cmd, "help") || streq0(cmd, "-h") || streq0(cmd, "--help") || streq0(cmd, "-?") || streq0(cmd, "/?"))) return -1;
    /* `bytecode <file> -o <out> [-m MODE]` of one file without anything else: the measured way (forge.c) */
    if (fcAsciiIEq(cmd, "bytecode")) {
        int files = 0;
        simple = true;
        for (i = 2; i < argc; i++) {
            if ((streq0(argv[i], "-o") || streq0(argv[i], "-m")) && i + 1 < argc) { i++; continue; }
            if (argv[i][0] == '-') simple = false; else { files++; if (pjIsProjectFile(argv[i])) simple = false; }
        }
        if (simple && files == 1 && argc >= 5) return -1;
    }
    fcCtxInit(c);
    FC_TRY(c) {
        o = tcParse(c, argc, argv);
        if (o->error) result = tcUsageError(o);
        else if (o->command == TC_HELP) { printf("%s\n", tcUsage); result = 0; }
        else {
            nsEnsureInstalled(prelude, storeRoot, stderr);
            if (o->command == TC_RUN) result = tcRun(c, o, prelude, storeRoot);
            else if (o->command == TC_BYTECODE || o->command == TC_DUMP) result = tcBytecode(c, o, prelude, storeRoot);
            else if (o->command == TC_NATIVE) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: native (the C into out.c - `forge-c cgen` measures the same C)");
        }
    } FC_CATCH(c) {
        if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "unported: ")) fprintf(stderr, "forge-c: %s\n", c->err.message);
        else fprintf(stderr, "forge-c: internal: %s\n", c->err.message ? c->err.message : "?");
        result = 2;
    } FC_END_TRY;
    fcCtxFree(c);
    return result;
}
