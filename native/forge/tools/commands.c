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
/* the name of a GlobalJobsMode (--global-jobs into the settings of a plan) */
static const char* tcGlobalJobsName(int mode) { return mode == GJM_Sync ? "sync" : mode == GJM_Sleep ? "sleep" : "safepoints"; }
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
    if (o->globalJobs >= 0) settings->globalJobs = tcGlobalJobsName(o->globalJobs);
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
/* the files as texts without paths (RuntimeSession.Build of `forge test`: from the current folder, unnamed) - each File.ReadAllText */
static PjPlan* tcTextsPlan(FcCtx* c, FcList* paths, FcList* defines, int types) {
    TcOptions o;
    FcList* texts = fcListNew(c);
    int i;
    memset(&o, 0, sizeof o);
    o.defines = defines ? defines : fcListNew(c);
    o.types = types;
    for (i = 0; i < paths->count; i++) {
        FcU16 t = {0};
        if (!fcReadText((const char*)paths->items[i], &t)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "cannot read '%s'", (const char*)paths->items[i]));
        fcListAdd(c, texts, (void*)fc8FromU16(c, t.p, t.len));
        fcU16Free(&t);
    }
    return tcFilesPlan(c, &o, paths, texts, 2, NULL);
}
/* a project's plan with the defines and the type checking of the command line (Linker.Defines with the project's, ProcessDefaultTypeChecking before its own) */
static void tcPlanOptions(FcCtx* c, const TcOptions* o, PjPlan* plan) {
    PjSettings* s;
    int i;
    if (o->defines->count == 0 && o->types < 0 && o->globalJobs < 0) return;
    s = (PjSettings*)fcAlloc(c, sizeof *s);
    *s = *plan->settings;
    s->defines = s->defines ? fcListCopy(c, s->defines) : fcListNew(c);
    for (i = 0; i < o->defines->count; i++) fcListAdd(c, s->defines, o->defines->items[i]);
    if (o->types >= 0) s->typeChecking = o->types == 1 ? "strict" : "lax";
    if (o->globalJobs >= 0) s->globalJobs = tcGlobalJobsName(o->globalJobs);   /* (Linker.ProcessGlobalJobs before the project's globalJobs) */
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

/* `test`: the configuration of --engine native (--config or the nearest fire.native.json, with the switches of the command line: KeepAll) */
static NbConfig* tcTestConfig(FcCtx* c, const TrArgs* a) {
    const TcOptions* o = (const TcOptions*)a->options;
    PjBuildArgs b;
    if (o->configFile && !nbFileExists(o->configFile)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "native-config: Configuration not found: %s", o->configFile));
    memset(&b, 0, sizeof b);
    b.configFile = o->configFile; b.stripping = o->stripping; b.optimize = o->optimize; b.quick = o->quick; b.split = o->split; b.linkage = o->linkage;
    b.keepAll = o->keepAll; b.stats = o->stats;
    return pjConfigFor(c, &b, a->file);
}
static int tcTest(const TcOptions* o, const char* prelude, const char* storeRoot) {
    TrArgs a;
    memset(&a, 0, sizeof a);
    a.files = o->files; a.projectName = o->projectName; a.engine = o->engine; a.filter = o->testFilter; a.toolchainName = o->toolchainName; a.configFile = o->configFile;
    a.mode = o->mode; a.keepAll = o->keepAll; a.defines = o->defines; a.floatWidth = o->floatWidth; a.intWidth = o->intWidth; a.types = o->types;
    a.config = tcTestConfig; a.options = o;
    return trTestRun(&a, prelude, storeRoot);
}

/* PjBuildArgs of the options (the configuration of a native build: KeepAll, ConfigFor; the builds of project/command.c) */
static void tcPlanHook(FcCtx* c, const void* state, PjPlan* plan) { tcApplyOverlays(c, (const TcOptions*)state, plan); tcPlanOptions(c, (const TcOptions*)state, plan); }
static PjBuildArgs tcArgs(const TcOptions* o) {
    PjBuildArgs a;
    memset(&a, 0, sizeof a);
    a.file = o->files->count > 0 ? (const char*)o->files->items[0] : NULL;
    a.output = o->output; a.targetName = o->targetName; a.toolchainName = o->toolchainName; a.configFile = o->configFile; a.linkage = o->linkage; a.engine = o->engine;
    a.projectName = o->projectName; a.mode = o->mode; a.floatWidth = o->floatWidth; a.stripping = o->stripping; a.optimize = o->optimize; a.quick = o->quick; a.split = o->split;
    a.keepAll = o->keepAll; a.keep = o->keep; a.stats = o->stats; a.all = o->all; a.check = o->checkOnly; a.defines = o->defines;
    a.intWidth = o->intWidth; a.debugInfo = o->debugInfo; a.planHook = tcPlanHook; a.hookState = o;
    return a;
}
/* `build <files>` (CommandLine.Build, NativeBuild) with what the build of one file (build/command.c) does not take: several files, -D, -i, --overlay,
 * --debug-info, --global-jobs - the plan of the files */
static int tcBuildFiles(FcCtx* c, const TcOptions* o, const char* prelude, const char* storeRoot) {
    FcBuf out = {0}, err = {0};
    volatile int result = 0;
    PjPlan* plan;
    const char* missing;
    int i;
    for (i = 0; i < o->files->count; i++) if (!tcReadSource(c, o, (const char*)o->files->items[i])) { fprintf(stderr, "File not found: %s\n", (const char*)o->files->items[i]); return 2; }
    plan = tcPlanOfFiles(c, o, 1, &missing);
    fcBufStr(&out, "");
    fcBufStr(&err, "");
    c->collecting = true;
    FC_TRY(c) {
        PjBuildArgs a = tcArgs(o);
        NbConfig* config;
        const char* engine;
        if (o->configFile && !nbFileExists(o->configFile)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "native-config: Configuration not found: %s", o->configFile));
        config = pjConfigFor(c, &a, (const char*)o->files->items[0]);
        engine = pjChooseEngine(c, o->engine, config->engine, o->targetName, config, &err);
        if (!engine) result = 2;
        else if (streq0(engine, "native")) {
            const FgTarget* target = nbResolveTarget(c, config, o->targetName);
            NbToolchain* toolchain = nbResolveToolchain(c, config, target, o->toolchainName, NB_PURPOSE_BUILD);
            NbBuild b;
            NbResult r;
            if (o->debugInfo) toolchain = pjDebugToolchain(c, toolchain);
            if (config->stats) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: --stats");
            memset(&b, 0, sizeof b);
            b.prelude = prelude; b.storeRoot = storeRoot; b.mode = o->mode; b.floatWidth = o->floatWidth; b.intWidth = o->intWidth; b.keepSources = o->keep;
            b.link = pjNbLink; b.project = plan;
            r = nbBuild(c, &b, config, target, toolchain, o->output ? o->output : nbDefaultOutput(c, target));
            c->collecting = false;
            for (i = 0; i < c->diags->count; i++) fcDiagLogLine(&err, (const FcDiagnostic*)c->diags->items[i]);
            if (*r.log) { fcOut8(&out, r.log); if (!fcEndsWith(r.log, "\n")) fcBufChar(&out, '\n'); }
            if (!r.ok) {
                if (r.error) fcBufFmt(&err, "error %s: %s\n", strstr(r.error, "was not found") ? "E7003" : "E7002", r.error);
                fcBufFmt(&out, "The native build for %s failed.\n", target->name);
                result = 1;
            } else fcBufFmt(&out, "%s (native, target %s, toolchain %s)\n", r.output, target->name, nbKind(toolchain));
        } else {
            const char* full = fcFullPath(c, ".", o->outputFile);
            FcStore* store = NULL;
            FcNatives* registry = NULL;
            FcLinked* linked;
            FcList* libraries;
            const char* error = NULL;
            nvRefuseDotnet(c);
            nvStoreRoot = storeRoot;
            linked = tcLinkVm(c, o, plan, prelude, storeRoot, &store, &registry);
            c->collecting = false;
            for (i = 0; i < c->diags->count; i++) fcDiagLogLine(&err, (const FcDiagnostic*)c->diags->items[i]);
            libraries = nvEnsureLibraries(c, linked, store, registry, &err);
            if (!nvPack(c, linked, libraries, o->outputFile, &err, &error)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "vm: %s", error));
            fcBufFmt(&out, "%s (%lld bytes)\n", full, nvFileLength(full));
        }
    } FC_CATCH(c) {
        if (c->collecting) { c->collecting = false; for (i = 0; i < c->diags->count; i++) fcDiagLogLine(&err, (const FcDiagnostic*)c->diags->items[i]); }
        if (c->err.kind == FC_ERR_NATIVE) { fcBufStr(&err, "error E7001: Not supported by the native backend yet: "); fcOut8(&err, c->err.message); fcBufChar(&err, '\n'); result = 1; }
        else if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "native-config: ")) { fcBufFmt(&err, "%s\n", c->err.message + 15); result = 2; }
        else if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "unported: ")) { fcBufFmt(&err, "forge-c: %s\n", c->err.message); result = 2; }
        else if (c->err.kind == FC_ERR_PACKAGE) { fcBufFmt(&err, "%s\n", c->err.message); result = 1; }
        else if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "vm: ")) { fcBufFmt(&err, "%s\n", c->err.message + 4); result = 1; }
        else if (pjIsCompileErrorKind(c->err.kind)) { pjDescribeErrors(c, &err); result = 1; }
        else { fcBufFmt(&err, "forge-c: internal: %s\n", c->err.message ? c->err.message : "?"); result = 2; }
    } FC_END_TRY;
    fwrite(err.p, 1, err.len, stderr);
    fwrite(out.p, 1, out.len, stdout);
    fcBufFree(&out);
    fcBufFree(&err);
    return result;
}
/* `build` with what the builds of build/command.c and project/command.c do not parse: the plan of the files here, a project there with its hook */
static int tcBuild(FcCtx* c, const TcOptions* o, const char* prelude, const char* storeRoot) {
    if (o->host) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: --host (linkage plugin)");
    if (o->files->count == 1 && tcIsProjectFile((const char*)o->files->items[0])) {
        PjBuildArgs a = tcArgs(o);
        return pjBuildMain(&a, prelude, storeRoot);
    }
    if (o->all) { fprintf(stderr, "--all builds a solution or a project file.\n"); return 2; }
    return tcBuildFiles(c, o, prelude, storeRoot);
}

/* the length of an fc8 text in UTF-16 units (string.Length) */
static size_t tcLength16(const char* s) { const unsigned char* p = (const unsigned char*)s; size_t n = 0; while (*p) { fc8Next(&p); n++; } return n; }
/* `native <files> [-o out.c]` (Native): the C of the program for the target - NativeBuilder.GenerateLinked with the toolchain "files" - written next to
 * fire_rt.h (WriteFiles), and how to compile it */
static int tcNative(FcCtx* c, const TcOptions* o, const char* prelude, const char* storeRoot) {
    FcCtx step;
    volatile int result = 0;
    const char* file = (const char*)o->files->items[0];
    const char* missing;
    PjPlan* plan;
    int i;
    for (i = 0; i < o->files->count; i++) if (!tcReadSource(c, o, (const char*)o->files->items[i])) { fprintf(stderr, "File not found: %s\n", (const char*)o->files->items[i]); return 2; }
    if (o->files->count == 1 && tcIsProjectFile(file)) {
        /* (RunProject: the plan first, then the answer) */
        FC_TRY(c) {
            PjWorkspace* w = pjWorkspaceOpen(c, file);
            pjPlanOrThrow(w, pjSelectProject(w, o->projectName));
            fprintf(stderr, "`native` takes source files; build a project with `build` (set \"engine\": \"native\" in the project or use --engine native).\n");
        } FC_CATCH(c) {
            if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
            fprintf(stderr, "%s\n", c->err.message);
        } FC_END_TRY;
        return 2;
    }
    plan = tcPlanOfFiles(c, o, 1, &missing);
    fcCtxInit(&step);
    step.collecting = true;
    FC_TRY(&step) {
        PjBuildArgs a = tcArgs(o);
        NbConfig* config;
        const FgTarget* target;
        PjLinkIn in;
        FgOptions fo;
        FcNatives* registry = NULL;
        FcLinked* linked;
        FcStore store;
        FgSources program;
        const char* full = fcFullPath(&step, ".", o->outputFile);
        const char* dir = fcDirectoryName(&step, full);
        const char* natives;
        FcBuf extra = {0};
        size_t length;
        if (o->configFile && !nbFileExists(o->configFile)) fcThrow(&step, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(&step, "native-config: Configuration not found: %s", o->configFile));
        config = pjConfigFor(&step, &a, file);
        target = nbResolveTarget(&step, config, o->targetName);
        memset(&fo, 0, sizeof fo);
        fo.target = target; fo.mode = o->mode; fo.intWidth = o->intWidth; fo.floatWidth = o->floatWidth; fo.stripping = nbStrippingOf(config); fo.optimize = nbOptimizeOf(config, target);
        fo.globalJobs = config->globalJobs ? nbGlobalJobsParse(&step, config->globalJobs) + 1 : 0;
        memset(&in, 0, sizeof in);
        in.plan = plan; in.prelude = prelude; in.storeRoot = storeRoot; in.o = &fo; in.toolchainKind = "files"; in.modeOverride = -1;
        linked = pjLink(&step, &in, &registry);
        tcWarnings(&step);
        memset(&store, 0, sizeof store);
        store.root = storeRoot;
        program = fgGenerateSources(&step, linked, registry->names, target, fo.stripping, false, config->split, "files", &store);
        nbMkdirs(&step, dir);
        nbWriteFiles(&step, &store, fcDictNew(&step, FC_KEY_PTR), dir, program.program, target, fcBaseName(full), config->path ? fcDirectoryName(&step, config->path) : NULL, program.natives, program.units);
        if (program.units) {
            FcList* names = fcListNew(&step);
            for (i = 2; i < program.units->count; i++) fcListAdd(&step, names, (void*)((FgSplitUnit*)program.units->items[i])->name);
            printf("the program in %d translation units (docs/spec/NATIVE_SPLIT.md): %s next to it, with %s\n", program.units->count - 1, fgJoin(&step, names, ", "), ((FgSplitUnit*)program.units->items[0])->name);
        }
        natives = program.natives ? nbCombine(&step, dir, "fire_natives.cpp") : "";
        fcBufStr(&extra, "");
        for (i = 0; i < target->compileArgCount; i++) fcBufFmt(&extra, "%s%s", i ? " " : "", target->compileArgs[i]);
        length = tcLength16(program.program);
        if (target->isEmbedded || target->entryKind == 1)
            printf("%s (%zu characters)%s%s for %s - add %s, fire_rt.h and platform/%s/ to your project (the entry point is %s)\n", full, length, *natives ? " and " : "", natives, target->name,
                *natives ? "them" : "it", target->platform, target->entryName);
        else if (!*natives) printf("%s (%zu characters) - compile with: cc -std=c11 -O2 %s \"%s\" -o program -lm\n", full, length, extra.p, full);
        else printf("%s (%zu characters) and %s - compile with: cc -std=c11 -O2 %s -c \"%s\" -o program.o && c++ -std=c++17 -O2 %s \"%s\" program.o -o program\n", full, length, natives, extra.p, full, extra.p, natives);
        fcBufFree(&extra);
    } FC_CATCH(&step) {
        tcWarnings(&step);
        if (step.err.kind == FC_ERR_NATIVE) { fprintf(stderr, "error E7001: Not supported by the native backend yet: %s\n", step.err.message); result = 1; }
        else if (step.err.kind == FC_ERR_INTERNAL && step.err.message && fcStartsWith(step.err.message, "native-config: ")) { fprintf(stderr, "%s\n", step.err.message + 15); result = 2; }
        else result = tcCompileFailed(&step);
    } FC_END_TRY;
    fcCtxFree(&step);
    return result;
}

/* ---- `forge data` (DataCommand.cs): the migrations and the schema of a database, done by the program itself in the VM ---- */
static const char* const tcDataUsage =
    "forge data migrate|status|diff|import [<file>... | <project.fireproj> | <solution.firesln>] --url <url> [options]\n"
    "\n"
    "migrate  runs the migrations ([Migration n, \"title\"] classes) that have not run on the database, each in a transaction; --to N goes up or\n"
    "         down (their Down methods) to the migration N (0: before the first).\n"
    "status   lists the migrations of the program and whether they ran.\n"
    "diff     writes the migration that makes the database what the mapping of the [Table] classes says (new tables, columns, indexes, join\n"
    "         tables; what only the database has is named in comments). The migrations of the program must have run first.\n"
    "import   writes classes ([Table], [Key], [Required], [Length], [Unique]) for the tables of the database - no program needed; --format fdata\n"
    "         a data model instead (docs/data/designer.md: entities, the relations guessed from columns like customerId and from join tables).\n"
    "\n"
    "--url      the database (sqlite:app.db, mysql://host/db, sqlserver://host/db?trust=true, memory:)\n"
    "--user     the user; --password (or the environment variable FIRE_DATA_PASSWORD)\n"
    "--to N     with migrate: the migration to go to\n"
    "--title T  with diff: the title of the new migration\n"
    "--namespace NS  with import: the namespace of the classes\n"
    "--format F with import: fire (classes, the default) or fdata (a data model)\n"
    "-o file    with diff and import: the file to write (else the output)\n"
    "-p name    the project of a solution";
/* DriverPackage: the package of the driver for the scheme of the URL */
static const char* tcDriverPackage(FcCtx* c, const char* url) {
    const char* colon = strchr(url, ':');
    const char* scheme;
    if (!colon || colon == url) return NULL;
    scheme = fcAsciiLower(c, fcStrN(c, url, (size_t)(colon - url)));
    if (streq0(scheme, "sqlite")) return "data.sqlite";
    if (streq0(scheme, "mysql")) return "data.mysql";
    if (streq0(scheme, "sqlserver")) return "data.sqlserver";
    if (streq0(scheme, "postgres") || streq0(scheme, "postgresql")) return "data.postgres";
    if (streq0(scheme, "memory")) return "data.memory";
    return NULL;
}
/* Lit: a fire string literal */
static const char* tcLit(FcCtx* c, const char* text) {
    FcBuf b = {0};
    const char* p;
    fcBufStr(&b, "\"");
    for (p = text; *p; p++) {
        if (*p == '\\') fcBufStr(&b, "\\\\");
        else if (*p == '"') fcBufStr(&b, "\\\"");
        else if (*p == '\n') fcBufStr(&b, "\\n");
        else if (*p != '\r') fcBufChar(&b, *p);
    }
    fcBufChar(&b, '"');
    return fcBufDone(c, &b);
}
static const char* tcDataDriver(FcCtx* c, void* state, FcList* program, int firstUserSource) { (void)c; (void)program; (void)firstUserSource; return (const char*)state; }
static int tcDataFail(const char* message) { fprintf(stderr, "%s\n", message); return 2; }
/* RunDriver: the driver (fire, at the top level) with the classes of the sources in the VM in C - the top level of the sources does not run; what it printed,
 * or the error (*compile: the sources do not compile) */
static const char* tcRunDriver(FcCtx* c, PjPlan* plan, const char* driver, const char* prelude, const char* storeRoot, const char** error, bool* compile) {
    FcCtx step;
    const char* volatile printed = "";
    *error = NULL;
    *compile = false;
    fcCtxInit(&step);
    step.collecting = true;
    FC_TRY(&step) {
        PjLinkIn in;
        FcStore* store = NULL;
        FcNatives* registry = NULL;
        FcLinked* linked;
        FcList* libraries;
        FcBuf fbc = {0}, out = {0}, err = {0};
        const char* vmError = NULL;
        const char* vm;
        const char* dir;
        const char* file;
        const char* unhandled;
        char* argv[4];
        FILE* f;
        memset(&in, 0, sizeof in);
        in.plan = plan; in.prelude = prelude; in.storeRoot = storeRoot; in.modeOverride = 1; in.storeOut = &store;
        step.testDriver = tcDataDriver;
        step.testDriverState = (void*)driver;
        linked = pjLink(&step, &in, &registry);
        step.testDriver = NULL;
        tcWarnings(&step);
        nvStoreRoot = storeRoot;
        libraries = nvEnsureLibraries(&step, linked, store, registry, NULL);
        vm = nvLocate(&step, linked->intWidth, linked->floatWidth, NULL, &vmError);
        if (!vm) fcThrow(&step, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(&step, "vm: %s", vmError));
        dir = trTempFolder(&step, "fire-data-");
        file = fcFmt(&step, "%s/program.fbc", dir);
        fcProgramFileWrite(&step, linked, true, &fbc);
        f = fopen(file, "wb");
        if (f) { fwrite(fbc.p, 1, fbc.len, f); fclose(f); }
        fcBufFree(&fbc);
        argv[0] = (char*)vm; argv[1] = (char*)"run"; argv[2] = (char*)file; argv[3] = NULL;
        fcBufStr(&out, ""); fcBufStr(&err, "");
        trPkgPath = tcPkgPath(&step, libraries);
        trExec(&step, argv, &out, &err);
        trPkgPath = NULL;
        trRemoveTree(dir);
        printed = fcStr(c, out.p);
        /* (the VM in C reports an exception nobody caught as UncaughtScriptException does) */
        if ((unhandled = strstr(err.p, "Unhandled exception of type ")) != NULL) {
            const char* end = strchr(unhandled, '\n');
            *error = end ? fcStrN(c, unhandled, (size_t)(end - unhandled)) : fcStr(c, unhandled);
        }
        fcBufFree(&out); fcBufFree(&err);
    } FC_CATCH(&step) {
        tcWarnings(&step);
        if (!pjIsCompileErrorKind(step.err.kind) && step.err.kind != FC_ERR_PROJECT) { c->err = step.err; c->err.message = fcStr(c, step.err.message); fcCtxFree(&step); fcRethrow(c); }
        {
            FcBuf b = {0};
            fcBufStr(&b, "");
            pjDescribeErrors(&step, &b);
            if (b.len > 0 && b.p[b.len - 1] == '\n') b.p[--b.len] = 0;
            *error = fcStr(c, b.p);
            fcBufFree(&b);
        }
        *compile = true;
    } FC_END_TRY;
    fcCtxFree(&step);
    return printed;
}
/* `forge data migrate|status|diff|import [<file>... | <project> | <solution>] --url <url> [...]` (DataCommand.Run) */
static int tcData(FcCtx* c, int argc, char** argv, const char* prelude, const char* storeRoot) {
    const char* command;
    const char* url = NULL; const char* user = NULL; const char* password = getenv("FIRE_DATA_PASSWORD"); const char* to = NULL; const char* title = NULL;
    const char* ns = NULL; const char* output = NULL; const char* project = NULL; const char* format = NULL;
    const char* package;
    const char* arg;
    const char* driver;
    const char* printed;
    const char* error;
    FcList* files = fcListNew(c);
    FcList* paths = fcListNew(c);
    FcList* texts = fcListNew(c);
    PjPlan* plan;
    PjSource* extra;
    bool compile;
    int i;
    if (argc < 3 || streq0(argv[2], "-h") || streq0(argv[2], "--help") || streq0(argv[2], "help")) { printf("%s\n", tcDataUsage); return 0; }
    command = fcAsciiLower(c, argv[2]);
    if (!(streq0(command, "migrate") || streq0(command, "status") || streq0(command, "diff") || streq0(command, "import")))
        return tcDataFail(fcFmt(c, "Unknown data command '%s' (expected: migrate, status, diff or import).", argv[2]));
    for (i = 3; i < argc; i++) {
        const char* a = argv[i];
#define TC_DV() (i + 1 < argc ? argv[++i] : NULL)
        if (streq0(a, "--url")) url = TC_DV();
        else if (streq0(a, "--user")) user = TC_DV();
        else if (streq0(a, "--password")) password = TC_DV();
        else if (streq0(a, "--to")) to = TC_DV();
        else if (streq0(a, "--title")) title = TC_DV();
        else if (streq0(a, "--namespace")) ns = TC_DV();
        else if (streq0(a, "--format")) format = TC_DV();
        else if (streq0(a, "-o") || streq0(a, "--output")) output = TC_DV();
        else if (streq0(a, "-p") || streq0(a, "--project")) project = TC_DV();
        else if (a[0] == '-') return tcDataFail(fcFmt(c, "Unknown option '%s' of forge data.", a));
        else fcListAdd(c, files, (void*)a);
#undef TC_DV
    }
    if (!url || !*fgTrim(c, url)) return tcDataFail("forge data needs the database: --url <url>.");
    if (to && !streq0(command, "migrate")) return tcDataFail("--to is only available with migrate.");
    if (format && (!streq0(command, "import") || !(streq0(format, "fire") || streq0(format, "fdata")))) return tcDataFail("--format is only available with import: fire (classes, the default) or fdata (a data model).");
    package = tcDriverPackage(c, url);
    if (!package) return tcDataFail(fcFmt(c, "forge data: no driver for the URL '%s' (sqlite:, mysql://, sqlserver://, postgres://, memory:).", url));
    if (files->count == 0 && !streq0(command, "import")) {
        int n;
        const char* found = trFindHere(c, ".firesln", &n);
        if (n == 0) found = trFindHere(c, ".fireproj", &n);
        if (!found) return tcDataFail(n == 0 ? "No program was named and there is no solution or project in this folder." : "There are several solutions or projects in this folder: name one.");
        fcListAdd(c, files, (void*)found);
    }
    for (i = 0; i < files->count; i++) if (!nbFileExists((const char*)files->items[i])) return tcDataFail(fcFmt(c, "File not found: %s", (const char*)files->items[i]));
    if (files->count == 1 && pjIsProjectFile((const char*)files->items[0])) {
        const char* volatile failure = NULL;
        FC_TRY(c) {
            PjWorkspace* w = pjWorkspaceOpen(c, (const char*)files->items[0]);
            PjPlan* p = pjPlanOrThrow(w, pjSelectProject(w, project));
            for (i = 0; i < p->sources->count; i++) {
                fcListAdd(c, paths, (void*)((PjSource*)p->sources->items[i])->path);
                fcListAdd(c, texts, (void*)((PjSource*)p->sources->items[i])->text);
            }
        } FC_CATCH(c) {
            if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
            failure = c->err.message;
        } FC_END_TRY;
        if (failure) return tcDataFail(failure);
    } else
        for (i = 0; i < files->count; i++) {
            FcU16 t = {0};
            if (!fcReadText((const char*)files->items[i], &t)) return tcDataFail(fcFmt(c, "File not found: %s", (const char*)files->items[i]));
            fcListAdd(c, paths, (void*)files->items[i]);
            fcListAdd(c, texts, (void*)fc8FromU16(c, t.p, t.len));
            fcU16Free(&t);
        }
    /* Execute: the sources, a source with `#import "data"` and the driver's package, then the driver */
    {
        TcOptions o;
        memset(&o, 0, sizeof o);
        o.defines = fcListNew(c); o.types = -1; o.globalJobs = -1;
        plan = tcFilesPlan(c, &o, paths, texts, 1, NULL);
    }
    extra = (PjSource*)fcAlloc(c, sizeof *extra);
    extra->path = NULL;
    extra->text = fcFmt(c, "#import \"data\"\n#import \"%s\"\n", package);
    extra->projectName = "program";
    fcListAdd(c, plan->sources, extra);
    arg = streq0(command, "migrate") ? (to ? to : "") : streq0(command, "diff") ? (title ? title : "") : streq0(command, "import") ? (ns ? ns : "") : "";
    driver = fcFmt(c, "print(Data.Tools.Run(%s, %s, %s, %s, %s, %s))\n", tcLit(c, command), tcLit(c, url), tcLit(c, user ? user : ""), tcLit(c, password ? password : ""), tcLit(c, arg), tcLit(c, format ? format : ""));
    printed = tcRunDriver(c, plan, driver, prelude, storeRoot, &error, &compile);
    if (error) {
        fputs(printed, stderr);
        fprintf(stderr, "%s\n", error);
        return compile ? 2 : 1;
    }
    if (output && (streq0(command, "diff") || streq0(command, "import"))) {
        FILE* f = fopen(output, "wb");
        if (!f) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "unported: the file cannot be written ('%s')", output));
        fputs(printed, f);
        fclose(f);
        printf("written: %s\n", output);
    } else fputs(printed, stdout);
    return 0;
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
    if (fcAsciiIEq(cmd, "data")) {
        fcCtxInit(c);
        FC_TRY(c) {
            nsEnsureInstalled(prelude, storeRoot, stderr);
            result = tcData(c, argc, argv, prelude, storeRoot);
        } FC_CATCH(c) {
            if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "unported: ")) fprintf(stderr, "forge-c: %s\n", c->err.message);
            else if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "vm: ")) fprintf(stderr, "%s\n", c->err.message + 4);
            else fprintf(stderr, "%s\n", c->err.message ? c->err.message : "?");
            result = c->err.kind == FC_ERR_PACKAGE ? 1 : 2;
        } FC_END_TRY;
        fcCtxFree(c);
        return result;
    }
    if (fcAsciiIEq(cmd, "build") || fcAsciiIEq(cmd, "publish")) {
        /* CommandLineParser.Parse first (its errors with the usage); what the builds of the earlier steps take goes to them */
        bool gap;
        fcCtxInit(c);
        o = tcParse(c, argc, argv);
        if (o->error) { result = tcUsageError(o); fcCtxFree(c); return result; }
        gap = o->command == TC_BUILD && (o->files->count > 1 || o->defines->count > 0 || o->intWidth || o->overlays->count > 0 || o->debugInfo || o->globalJobs >= 0 || o->host
            || (o->types >= 0 && o->files->count == 1 && tcIsProjectFile((const char*)o->files->items[0])));
        if (o->command == TC_PUBLISH && (o->overlays->count > 0 || o->defines->count > 0 || o->intWidth || o->types >= 0 || o->globalJobs >= 0)) {
            fprintf(stderr, "forge-c: unported: publish with --overlay, -D, -i, --types or --global-jobs\n");
            fcCtxFree(c);
            return 2;
        }
        if (!gap) { fcCtxFree(c); return -1; }
        FC_TRY(c) {
            nsEnsureInstalled(prelude, storeRoot, stderr);
            result = tcBuild(c, o, prelude, storeRoot);
        } FC_CATCH(c) {
            if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "unported: ")) fprintf(stderr, "forge-c: %s\n", c->err.message);
            else fprintf(stderr, "forge-c: internal: %s\n", c->err.message ? c->err.message : "?");
            result = 2;
        } FC_END_TRY;
        fcCtxFree(c);
        return result;
    }
    if (!(fcAsciiIEq(cmd, "run") || fcAsciiIEq(cmd, "test") || fcAsciiIEq(cmd, "native") || fcAsciiIEq(cmd, "dump") || fcAsciiIEq(cmd, "bytecode") || streq0(cmd, "help") || streq0(cmd, "-h") || streq0(cmd, "--help") || streq0(cmd, "-?") || streq0(cmd, "/?"))) return -1;
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
            else if (o->command == TC_TEST) result = tcTest(o, prelude, storeRoot);
            else if (o->command == TC_BYTECODE || o->command == TC_DUMP) result = tcBytecode(c, o, prelude, storeRoot);
            else if (o->command == TC_NATIVE) result = tcNative(c, o, prelude, storeRoot);
        }
    } FC_CATCH(c) {
        if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "unported: ")) fprintf(stderr, "forge-c: %s\n", c->err.message);
        else fprintf(stderr, "forge-c: internal: %s\n", c->err.message ? c->err.message : "?");
        result = 2;
    } FC_END_TRY;
    fcCtxFree(c);
    return result;
}
