/* native/forge/tools/report.c - the report for tools (Phase D 4, commit b833e71b): `forge-c build|publish|bytecode --report json`, what spark reads
 * (docs/tools/forge.md, "The report for tools"). src/fire.Compiler/ForgeReport.cs (ForgeReportWriter: one JSON object per line - start, progress, log,
 * diagnostic, output, project, end) and CommandLine.Report.cs (WantsReport, BeginReport, RunReported, ReportBuild, ReportBytecode, ReportBuildFiles,
 * ToolchainFor, NativeOutcome, ReportCopyContent, ReportBuildProject, BuildProgram, ReportBuildAll, ReportPublish), with NativeLaunch.DebugToolchain
 * (`--debug-info`) and NativeBuilder.BuildSafe (the errors of a native build as its result).
 *
 * The steps of a build (BuildMonitor.Step) come from the linkers, the builder and the packer through fcMonitorStep, the warnings of the code as they are
 * collected through fcOnReport (DiagnosticCollector.OnReport; diag.c) - a native build collects its own and reports them at its end, as BuildSafe does.
 * Cancelling: the line "cancel" on the input or SIGTERM/SIGINT; the tools that run are ended (the children of this process), the build stops at its next
 * step and the report ends with "cancelled" (exit 3). Not ported: the events "background" (BackgroundWork: the standard packages, the natives of a
 * package being built - spark's status bar), and cancelling a step of the frontend while it runs (it stops at the next step). */

/* ---- ForgeReportWriter ---- */
static pthread_mutex_t frLock = PTHREAD_MUTEX_INITIALIZER;
static bool frEnded;
static char* frProject;   /* Project: the project the following events belong to (malloc'd; NULL: none) */
typedef struct FrLogged { char* key; int n; } FrLogged;
static FrLogged* frLogged;
static int frLoggedCount, frLoggedCap;
static FcDiagnostic* frSeen;   /* the diagnostics of the collector (DiagnosticCollector._seen), copies */
static int frSeenCount, frSeenCap;
static volatile sig_atomic_t frCancel;
static jmp_buf frCancelJump;
static volatile bool frCancelArmed;
static FcBuf frPending;   /* the lines of the monitor of a publish (pjMonitorOut) not yet written */

static void frFieldStr(FcBuf* b, const char* name, const char* value) {
    fcBufFmt(b, ",\"%s\":", name);
    if (value) fjEscape(b, value, true); else fcBufStr(b, "null");
}
static void frFieldOpt(FcBuf* b, const char* name, const char* value) { if (value) frFieldStr(b, name, value); }
static void frFieldInt(FcBuf* b, const char* name, long long v) { fcBufFmt(b, ",\"%s\":%lld", name, v); }
static void frFieldBool(FcBuf* b, const char* name, bool v) { fcBufFmt(b, ",\"%s\":%s", name, v ? "true" : "false"); }
/* one object as one line, flushed at once (from any thread); nothing after the end */
static void frEmit(FcBuf* fc8) {
    FcBuf u = {0};
    fcBufStr(&u, "");
    fcOut8(&u, fc8->p);
    fcBufChar(&u, '}');
    pthread_mutex_lock(&frLock);
    if (!frEnded) { fwrite(u.p, 1, u.len, stdout); fputc('\n', stdout); fflush(stdout); }
    pthread_mutex_unlock(&frLock);
    fcBufFree(&u);
    fcBufFree(fc8);
}
static void frBegin(FcBuf* b, const char* type) { memset(b, 0, sizeof *b); fcBufStr(b, "{\"type\":"); fjEscape(b, type, true); }
static void frStart(const char* command, const char* what) {
    FcBuf b;
    frBegin(&b, "start");
    frFieldInt(&b, "version", 1);
    frFieldStr(&b, "command", command);
    frFieldOpt(&b, "text", what);
    frFieldInt(&b, "pid", (long long)getpid());
    frEmit(&b);
}
static const char* const frPhases[] = { "compile-fire", "package-natives", "generate-c", "compile-c", "link", "flash", "pack" };
static void frFlushPending(void);
static void frProgress(int phase, const char* text, int done, int total) {
    FcBuf b;
    frBegin(&b, "progress");
    frFieldStr(&b, "phase", frPhases[phase]);
    frFieldStr(&b, "text", text);
    frFieldInt(&b, "done", done);
    frFieldInt(&b, "total", total);
    frEmit(&b);
}
static size_t frTrimEndLen(const char* s, size_t n) { while (n > 0 && (s[n - 1] == ' ' || s[n - 1] == '\t' || s[n - 1] == '\r' || s[n - 1] == '\n' || s[n - 1] == '\v' || s[n - 1] == '\f')) n--; return n; }
static void frLogLine(const char* text, size_t n) {
    FcBuf b;
    char* line = (char*)malloc(n + 1);
    size_t k;
    int i;
    if (!line) fbOutOfMemory();
    while (n > 0 && (text[n - 1] == '\r' || text[n - 1] == '\n')) n--;
    memcpy(line, text, n); line[n] = 0;
    k = frTrimEndLen(line, n);
    pthread_mutex_lock(&frLock);
    for (i = 0; i < frLoggedCount; i++) if (strlen(frLogged[i].key) == k && memcmp(frLogged[i].key, line, k) == 0) break;
    if (i == frLoggedCount) {
        if (frLoggedCount == frLoggedCap) { frLoggedCap = frLoggedCap ? 2 * frLoggedCap : 64; frLogged = (FrLogged*)realloc(frLogged, sizeof *frLogged * (size_t)frLoggedCap); if (!frLogged) fbOutOfMemory(); }
        frLogged[i].key = (char*)malloc(k + 1);
        if (!frLogged[i].key) fbOutOfMemory();
        memcpy(frLogged[i].key, line, k); frLogged[i].key[k] = 0;
        frLogged[i].n = 0;
        frLoggedCount++;
    }
    frLogged[i].n++;
    pthread_mutex_unlock(&frLock);
    frBegin(&b, "log");
    frFieldStr(&b, "text", line);
    frEmit(&b);
    free(line);
}
static void frLog(const char* line) { frFlushPending(); frLogLine(line, strlen(line)); }
/* LogBlock: the lines of a log written at its end that have not come as they happened */
static void frLogBlock(const char* log) {
    char* text;
    char* p;
    size_t n, w = 0, r;
    if (!log || !*log) return;
    /* (Replace("\r\n", "\n").TrimEnd('\n').Split('\n')) */
    n = strlen(log);
    text = (char*)malloc(n + 1);
    if (!text) fbOutOfMemory();
    for (r = 0; r < n; r++) { if (log[r] == '\r' && r + 1 < n && log[r + 1] == '\n') continue; text[w++] = log[r]; }
    while (w > 0 && text[w - 1] == '\n') w--;
    text[w] = 0;
    for (p = text;;) {
        char* e = strchr(p, '\n');
        size_t raw = e ? (size_t)(e - p) : strlen(p), k = frTrimEndLen(p, raw);
        bool seen = false;
        int i;
        pthread_mutex_lock(&frLock);
        for (i = 0; i < frLoggedCount; i++)
            if (strlen(frLogged[i].key) == k && memcmp(frLogged[i].key, p, k) == 0) { if (frLogged[i].n > 0) { frLogged[i].n--; seen = true; } break; }
        pthread_mutex_unlock(&frLock);
        if (!seen) {
            FcBuf b;
            size_t m = raw;
            char save;
            while (m > 0 && p[m - 1] == '\r') m--;
            save = p[m]; p[m] = 0;
            frBegin(&b, "log");
            frFieldStr(&b, "text", p);
            frEmit(&b);
            p[m] = save;
        }
        if (!e) break;
        p = e + 1;
    }
    free(text);
}
static void frDiagnostic(const FcDiagnostic* d) {
    FcBuf b;
    frFlushPending();
    frBegin(&b, "diagnostic");
    frFieldStr(&b, "severity", d->severity == FC_SEV_WARNING ? "warning" : "error");
    frFieldStr(&b, "code", d->code ? d->code : "");
    frFieldStr(&b, "file", d->file);
    frFieldInt(&b, "line", d->line);
    frFieldInt(&b, "column", d->column);
    frFieldStr(&b, "message", d->message ? d->message : "");
    frFieldOpt(&b, "project", frProject);
    frEmit(&b);
}
static void frOutput(const char* kind, const char* path) {
    FcBuf b;
    frFlushPending();
    frBegin(&b, "output");
    frFieldStr(&b, "kind", kind);
    frFieldStr(&b, "path", path);
    frFieldOpt(&b, "project", frProject);
    frEmit(&b);
}
static void frProjectResult(const char* name, bool ok, bool skipped, bool upToDate, const char* error) {
    FcBuf b;
    frFlushPending();
    frBegin(&b, "project");
    frFieldStr(&b, "name", name);
    frFieldBool(&b, "ok", ok);
    frFieldBool(&b, "skipped", skipped);
    frFieldBool(&b, "upToDate", upToDate);
    frFieldOpt(&b, "error", error);
    frEmit(&b);
}
static void frEnd(bool ok, int exitCode, bool cancelled, const char* summary, const char* error) {
    FcBuf b;
    frFlushPending();
    frBegin(&b, "end");
    frFieldBool(&b, "ok", ok);
    frFieldInt(&b, "exitCode", exitCode);
    frFieldBool(&b, "cancelled", cancelled);
    frFieldOpt(&b, "summary", summary);
    frFieldOpt(&b, "error", error);
    frEmit(&b);
    pthread_mutex_lock(&frLock);
    frEnded = true;
    pthread_mutex_unlock(&frLock);
}
static void frSetProject(const char* name) { free(frProject); frProject = name ? strdup(name) : NULL; }
/* the lines of a text (a log of the caller's) as lines of the log */
static void frLogLines(const char* text) {
    const char* p = text;
    while (p && *p) {
        const char* e = strchr(p, '\n');
        frFlushPending();
        frLogLine(p, e ? (size_t)(e - p) : strlen(p));
        if (!e) break;
        p = e + 1;
    }
}
static void frFlushPending(void) {
    if (frPending.len == 0) return;
    {
        char* copy = strdup(frPending.p);
        const char* p = copy;
        frPending.len = 0;
        frPending.p[0] = 0;
        while (*p) {
            const char* e = strchr(p, '\n');
            frLogLine(p, e ? (size_t)(e - p) : strlen(p));
            if (!e) break;
            p = e + 1;
        }
        free(copy);
    }
}

/* DiagnosticCollector.Add with OnReport: the same message for the same place once */
static void frCollected(const FcDiagnostic* d) {
    int i;
    for (i = 0; i < frSeenCount; i++) {
        const FcDiagnostic* o = &frSeen[i];
        if (o->line == d->line && o->severity == d->severity && o->column == d->column && fcSameText(o->message, d->message) && fcSameText(o->code, d->code) && fcSameText(o->file, d->file)) return;
    }
    if (frSeenCount == frSeenCap) { frSeenCap = frSeenCap ? 2 * frSeenCap : 32; frSeen = (FcDiagnostic*)realloc(frSeen, sizeof *frSeen * (size_t)frSeenCap); if (!frSeen) fbOutOfMemory(); }
    frSeen[frSeenCount] = *d;
    frSeen[frSeenCount].message = d->message ? strdup(d->message) : NULL;
    frSeen[frSeenCount].code = d->code ? strdup(d->code) : NULL;
    frSeen[frSeenCount].file = d->file ? strdup(d->file) : NULL;
    frSeenCount++;
    frDiagnostic(d);
}

/* ---- cancelling ---- */
/* the tools this process runs (their trees: a compiler ends its own children) are asked to end */
static void frEndChildren(void) {
#ifdef __linux__
    DIR* d = opendir("/proc");
    struct dirent* e;
    pid_t self = getpid();
    if (!d) return;
    while ((e = readdir(d)) != NULL) {
        char path[300], buf[512];
        FILE* f;
        size_t n;
        const char* close;
        int ppid = 0;
        if (e->d_name[0] < '0' || e->d_name[0] > '9') continue;
        snprintf(path, sizeof path, "/proc/%s/stat", e->d_name);
        f = fopen(path, "r");
        if (!f) continue;
        n = fread(buf, 1, sizeof buf - 1, f);
        fclose(f);
        buf[n] = 0;
        close = strrchr(buf, ')');
        if (close && sscanf(close + 1, " %*c %d", &ppid) == 1 && ppid == self) kill((pid_t)atoi(e->d_name), SIGTERM);
    }
    closedir(d);
#endif
}
static void frOnSignal(int sig) { (void)sig; frCancel = 1; }
static void* frReadInput(void* arg) {
    char line[512];
    (void)arg;
    while (fgets(line, sizeof line, stdin)) {
        size_t a = 0, n = strlen(line);
        while (a < n && (line[a] == ' ' || line[a] == '\t')) a++;
        while (n > a && (line[n - 1] == '\n' || line[n - 1] == '\r' || line[n - 1] == ' ' || line[n - 1] == '\t')) n--;
        if (n - a == 6 && memcmp(line + a, "cancel", 6) == 0) { frCancel = 1; frEndChildren(); return NULL; }
    }
    return NULL;
}
/* BuildMonitor.Step: cancelled, the build stops here (ThrowIfCancellationRequested); else the progress */
static void frCheckpoint(void) {
    if (frCancel && frCancelArmed) { frCancelArmed = false; longjmp(frCancelJump, 1); }
}
static void frStepHook(int phase, const char* text, int done, int total) {
    frCheckpoint();
    frFlushPending();
    frProgress(phase, text, done, total);
}
static void frMonitorLine(const char* text, size_t n) {
    if (frCancel) frEndChildren();
    frLogLine(text, n);
}
static void frOnReportHook(const FcDiagnostic* d) { frCollected(d); }

/* ---- the builds ---- */
typedef struct FrOutcome { int exit; const char* summary; const char* error; } FrOutcome;
static FrOutcome frOutcome(int exit, const char* summary, const char* error) { FrOutcome r; r.exit = exit; r.summary = summary; r.error = error; return r; }
/* Failure: the message on the log, the outcome with it as the error */
static FrOutcome frFailure(FcCtx* c, int exit, const char* message) {
    frLog(fcFmt(c, "error: %s", pjTrimEndWs(c, message)));
    return frOutcome(exit, NULL, message);
}
/* CompileFailure: the diagnostics of the error, the first as the error of the outcome */
static FrOutcome frCompileFailure(FcCtx* c, FcCtx* step) {
    FcList* diags = fcDiagnosticsOf(c, &step->err, NULL);
    FcBuf line = {0};
    int i;
    for (i = 0; i < diags->count; i++) frDiagnostic((const FcDiagnostic*)diags->items[i]);
    if (diags->count == 0) return frOutcome(1, NULL, step->err.message);
    fcBufStr(&line, "");
    fcDiagLogLine(&line, (const FcDiagnostic*)diags->items[0]);
    line.len--; line.p[line.len] = 0;
    return frOutcome(1, NULL, fcBufDone(c, &line));
}
static bool frIsKind(const FcCtx* c, const char* prefix) { return c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, prefix); }

/* PjBuildArgs of the options (the configuration of a native build: KeepAll, ConfigFor) */
static PjBuildArgs frArgs(const TcOptions* o) { return tcArgs(o); }
/* LoadConfig: --config (it has to exist), else the nearest fire.native.json; with the switches of the command line (KeepAll) */
static NbConfig* frLoadConfig(FcCtx* c, const TcOptions* o, const char* near) {
    PjBuildArgs a = frArgs(o);
    if (o->configFile && !nbFileExists(o->configFile)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "native-config: Configuration not found: %s", o->configFile));
    return pjConfigFor(c, &a, near);
}
/* ToolchainFor: --toolchain, the project's, the configuration's or the machine's Build compiler - with --debug-info the one for the debugger */
static NbToolchain* frToolchainFor(FcCtx* c, const TcOptions* o, NbConfig* config, const FgTarget* target, const PjPlan* plan) {
    NbToolchain* t = nbResolveToolchain(c, config, target, o->toolchainName ? o->toolchainName : plan ? plan->settings->toolchain : NULL, NB_PURPOSE_BUILD);
    return o->debugInfo ? pjDebugToolchain(c, t) : t;
}
/* NativeBuilder.BuildSafe and NativeOutcome: the native build of a plan (its own collector: the warnings come with its result), its log, its diagnostics,
 * the copies next to it and what it wrote. `project`: the name for the log of `--all` */
static FrOutcome frNativeBuild(FcCtx* c, const TcOptions* o, PjPlan* plan, bool copies, NbConfig* config, const FgTarget* target, NbToolchain* toolchain, const char* output,
                               const char* prelude, const char* storeRoot, const char* project) {
    NbBuild b;
    NbResult r;
    FcCtx step;
    FcList* diags;
    const FcDiagnostic* volatile first = NULL;
    bool files = streq0(nbKind(toolchain), "files");
    void (*onReport)(const FcDiagnostic*) = fcOnReport;
    int i;
    memset(&b, 0, sizeof b);
    b.prelude = prelude; b.storeRoot = storeRoot; b.mode = o->mode; b.floatWidth = o->floatWidth; b.intWidth = o->intWidth; b.keepSources = o->keep;
    b.link = pjNbLink; b.overlay = pjNbOverlay; b.vmLink = pjNbVmLink; b.project = plan; b.projectName = plan->files ? NULL : plan->project->project->name;
    fcCtxInit(&step);
    step.collecting = true;
    fcOnReport = NULL;
    FC_TRY(&step) {
        r = nbBuild(&step, &b, config, target, toolchain, output);
    } FC_CATCH(&step) {
        fcOnReport = onReport;
        r.ok = false; r.output = output; r.error = NULL;
        if (step.err.kind == FC_ERR_NATIVE) r.log = fcFmt(&step, "Not supported by the native backend yet: %s", step.err.message);
        else if (frIsKind(&step, "native-config: ")) {
            FcDiagnostic* d = (FcDiagnostic*)fcAlloc(&step, sizeof *d);
            r.log = step.err.message + 15;
            d->line = 0; d->message = r.log; d->severity = FC_SEV_ERROR; d->code = "E7003";
            step.err.kind = FC_ERR_NONE;
            fcListInsert(&step, step.diags, 0, d);
        }
        else if (pjIsCompileErrorKind(step.err.kind)) r.log = nsDescribe(&step);   /* (CompileErrors.Describe) */
        else { c->err = step.err; fcRethrow(c); }
        if (step.err.kind != FC_ERR_NONE) {
            FcList* errs = fcDiagnosticsOf(&step, &step.err, NULL);
            FcList* all = fcListNew(&step);
            for (i = 0; i < errs->count; i++) fcListAdd(&step, all, errs->items[i]);
            for (i = 0; i < step.diags->count; i++) fcListAdd(&step, all, step.diags->items[i]);
            step.diags = all;
        }
    } FC_END_TRY;
    fcOnReport = onReport;
    diags = step.diags;
    if (r.error) {
        FcDiagnostic* d = (FcDiagnostic*)fcAlloc(&step, sizeof *d);
        d->line = 0; d->message = r.error; d->severity = FC_SEV_ERROR; d->code = r.config || strstr(r.error, "was not found") ? "E7003" : "E7002";
        fcListInsert(&step, diags, 0, d);
    }
    frLogBlock(r.log);
    if (frCancel) { fcCtxFree(&step); frCheckpoint(); }
    for (i = 0; i < diags->count; i++) frDiagnostic((const FcDiagnostic*)diags->items[i]);
    if (!r.ok) {
        FcBuf line = {0};
        const char* error;
        for (i = 0; i < diags->count && !first; i++) if (((const FcDiagnostic*)diags->items[i])->severity == FC_SEV_ERROR) first = (const FcDiagnostic*)diags->items[i];
        fcBufStr(&line, "");
        if (first) { fcDiagLogLine(&line, first); line.len--; line.p[line.len] = 0; }
        else fcBufFmt(&line, "The native build for %s failed.", target->name);
        error = fcBufDone(c, &line);
        frLog(fcFmt(c, "%sThe native build for %s failed.", project ? fcFmt(c, "%s: ", project) : "", target->name));
        fcCtxFree(&step);
        return frOutcome(1, NULL, error);
    }
    {
        const char* out = fcStr(c, r.output);
        fcCtxFree(&step);
        if (copies) {
            FcBuf l = {0}, e = {0};
            fcBufStr(&l, ""); fcBufStr(&e, "");
            pjCopyContent(c, plan, files ? out : fcDirectoryName(c, pjFull(c, out, ".")), &l, &e, true);
            frLogLines(l.p);
            fcBufFree(&l); fcBufFree(&e);
        }
        frLog(fcFmt(c, "%s (native, target %s, toolchain %s)", out, target->name, nbKind(toolchain)));
        frOutput(files ? "folder" : "program", out);
        return frOutcome(0, out, NULL);
    }
}
/* the program of a plan packed with the VM in C (Linker.CompileAndLink with outname): the link, the libraries of its packages, the pack - into `program` */
static void frPackVm(FcCtx* c, const TcOptions* o, PjPlan* plan, const char* prelude, const char* storeRoot, const char* program) {
    FcStore* store = NULL;
    FcNatives* registry = NULL;
    FcLinked* linked;
    FcList* libraries;
    FcBuf log = {0};
    const char* error = NULL;
    nvRefuseDotnet(c);
    nvStoreRoot = storeRoot;
    linked = tcLinkVm(c, o, plan, prelude, storeRoot, &store, &registry);
    fcBufStr(&log, "");
    libraries = nvEnsureLibraries(c, linked, store, registry, &log);
    if (!nvPack(c, linked, libraries, program, &log, &error)) { frLogLines(log.p); fcBufFree(&log); fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "vm: %s", error)); }
    frLogLines(log.p);
    fcBufFree(&log);
}

/* ReportBuildFiles: source files - a native program (BuildSafe), else one that carries the VM */
static FrOutcome frBuildFiles(FcCtx* c, const TcOptions* o, const char* prelude, const char* storeRoot) {
    FcList* texts = fcListNew(c);
    NbConfig* config;
    FcBuf problem = {0};
    const char* engine;
    int i;
    for (i = 0; i < o->files->count; i++) {
        const char* text = tcReadSource(c, o, (const char*)o->files->items[i]);
        if (!text) return frFailure(c, 2, fcFmt(c, "File not found: %s", (const char*)o->files->items[i]));
        fcListAdd(c, texts, (void*)text);
    }
    config = frLoadConfig(c, o, (const char*)o->files->items[0]);
    fcBufStr(&problem, "");
    engine = pjChooseEngine(c, o->engine, config->engine, o->targetName, config, &problem);
    if (!engine) return frFailure(c, 2, pjTrim(c, fcBufDone(c, &problem)));
    if (streq0(engine, "native")) {
        const FgTarget* target = nbResolveTarget(c, config, o->targetName);
        NbToolchain* toolchain = frToolchainFor(c, o, config, target, NULL);
        /* (BuildSafe with the texts and the folder of the first file: no SourcePaths) */
        PjPlan* plan = tcFilesPlan(c, o, o->files, texts, 2, tcBaseDirectory(c, o));
        if (config->stats) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: --stats");
        return frNativeBuild(c, o, plan, false, config, target, toolchain, o->output ? o->output : nbDefaultOutput(c, target), prelude, storeRoot, NULL);
    }
    {
        PjPlan* plan = tcFilesPlan(c, o, o->files, texts, 1, NULL);
        const char* full = fcFullPath(c, ".", o->outputFile);
        frPackVm(c, o, plan, prelude, storeRoot, o->outputFile);
        frLog(fcFmt(c, "%s (%lld bytes)", full, nvFileLength(full)));
        frOutput("program", full);
        return frOutcome(0, full, NULL);
    }
}

/* the mode a project's program is built in (ModeName: -m, else EffectiveModeName - a compile of its sources) */
static const char* frModeName(FcCtx* c, const TcOptions* o, PjPlan* plan, const char* prelude, const char* storeRoot, const FgTarget* target, PjOut* po) {
    (void)c;
    if (o->mode >= 0) return o->mode == 0 ? "debug" : o->mode == 2 ? "performance" : "release";
    return pjEffectiveModeName(po, plan, prelude, storeRoot, target, o->mode, o->floatWidth);
}
/* BuildProgram: a program of a project, native (BuildSafe) or one that carries the VM - into output, else <project>/bin/<target>-<mode>/ */
static FrOutcome frBuildProgram(FcCtx* c, const TcOptions* o, PjPlan* plan, const char* output, bool forSolution, const char* prelude, const char* storeRoot, PjOut* po) {
    PjBuildArgs a = frArgs(o);
    NbConfig* config = pjConfigFor(c, &a, plan->project->project->filePath);
    FcBuf problem = {0};
    const char* engine;
    const char* name = plan->project->project->name;
    fcBufStr(&problem, "");
    engine = pjChooseEngine(c, o->engine, plan->settings->engine ? plan->settings->engine : config->engine, o->targetName ? o->targetName : plan->settings->target, config, &problem);
    if (!engine) return frFailure(c, 2, pjTrim(c, fcBufDone(c, &problem)));
    if (streq0(engine, "native")) {
        const FgTarget* target = nbResolveTarget(c, config, o->targetName ? o->targetName : plan->settings->target);
        NbToolchain* toolchain = frToolchainFor(c, o, config, target, plan);
        bool files = streq0(nbKind(toolchain), "files");
        const char* to;
        FrOutcome outcome;
        if (!o->linkage && plan->settings->linkage) config->linkage = plan->settings->linkage;
        if (forSolution && files) output = NULL;
        pjApplyBuildSettings(c, config, plan);
        if (output) to = output;
        else {
            const char* mode = frModeName(c, o, plan, prelude, storeRoot, target, po);
            to = files ? pjOutputFolder(c, plan->project->project->directory, target->name, mode)
                       : pjProgramPath(c, plan->project->project->directory, name, target->name, mode, target->programExtension);
        }
        nbMkdirs(c, files ? pjFull(c, to, ".") : fcDirectoryName(c, pjFull(c, to, ".")));
        config->stats = config->stats || o->stats;
        if (config->stats) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: --stats");
        outcome = frNativeBuild(c, o, plan, true, config, target, toolchain, to, prelude, storeRoot, forSolution ? name : NULL);
        if (forSolution && outcome.exit == 0) frLog(fcFmt(c, "%s: built %s (%s).", name, outcome.summary, target->name));
        return outcome;
    }
    {
        const char* program = output ? output : pjProgramPath(c, plan->project->project->directory, name, "vm", frModeName(c, o, plan, prelude, storeRoot, NULL, po), "");
        const char* full = pjFull(c, program, ".");
        FcBuf l = {0}, e = {0};
        nbMkdirs(c, fcDirectoryName(c, full));
        fcMonitorStep(0, fcFmt(c, "Compiling %s", name), 0, 0);
        frPackVm(c, o, plan, prelude, storeRoot, program);
        fcBufStr(&l, ""); fcBufStr(&e, "");
        pjCopyContent(c, plan, fcDirectoryName(c, full), &l, &e, true);
        frLogLines(l.p);
        fcBufFree(&l); fcBufFree(&e);
        frLog(fcFmt(c, "%s (%lld bytes)", full, nvFileLength(full)));
        frOutput("program", full);
        return frOutcome(0, full, NULL);
    }
}
/* the plan of a project of the command line (PlanOrThrow with the overlays, the defines and the type checking) */
static PjPlan* frPlan(FcCtx* c, const TcOptions* o, PjWorkspace* w, PjLoaded* project) {
    PjPlan* plan = pjPlanOrThrow(w, project);
    tcApplyOverlays(c, o, plan);
    tcPlanOptions(c, o, plan);
    return plan;
}
/* ProjectBuilder.Check in a step of its own: the warnings to the report; an error goes to the caller */
static void frCheck(FcCtx* c, PjPlan* plan, const char* prelude, const char* storeRoot, const FgTarget* target, PjOut* po) {
    FcCtx step;
    pjStepInit(&step);
    FC_TRY(&step) {
        pjCheck(&step, plan, prelude, storeRoot, target);
    } FC_CATCH(&step) {
        pjForwardWarnings(po, &step);
        c->err = step.err;
        fcRethrow(c);
    } FC_END_TRY;
    pjForwardWarnings(po, &step);
}
/* ReportBuildAll: every project in the order of the references - a library compiled, a program built; a project whose library failed is skipped */
static FrOutcome frBuildAll(FcCtx* c, const TcOptions* o, PjWorkspace* w, const char* prelude, const char* storeRoot, PjOut* po) {
    FcList* order = pjBuildOrder(w);
    FcList* failed = fcListNew(c);
    int succeeded = 0, skipped = 0, i, k;
    const char* volatile firstError = NULL;
    if (order->count == 0) return frFailure(c, 2, "The solution has no project to build.");
    for (i = 0; i < order->count; i++) {
        PjLoaded* project = (PjLoaded*)order->items[i];
        const char* name = project->project->name;
        FcList* refs = pjReferencedProjects(w, project);
        PjLoaded* broken = NULL;
        PjPlan* volatile plan = NULL;
        const char* volatile failure = NULL;
        frCheckpoint();
        frSetProject(name);
        frLog(fcFmt(c, "------ %d/%d %s (%s) ------", i + 1, order->count, name, project->project->type == PJ_LIBRARY ? "library" : "program"));
        for (k = 0; k < refs->count && !broken; k++) if (pjInList(failed, refs->items[k])) broken = (PjLoaded*)refs->items[k];
        if (broken) {
            fcListAdd(c, failed, project);
            skipped++;
            frLog(fcFmt(c, "%s: skipped - the library %s it references did not build.", name, broken->project->name));
            frProjectResult(name, false, true, false, NULL);
            continue;
        }
        FC_TRY(c) {
            plan = frPlan(c, o, w, project);
        } FC_CATCH(c) {
            if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
            failure = c->err.message;
        } FC_END_TRY;
        if (!failure) {
            FC_TRY(c) {
                if (plan->project->project->type == PJ_LIBRARY) {
                    PjBuildArgs a = frArgs(o);
                    NbConfig* config = pjConfigFor(c, &a, plan->project->project->filePath);
                    const char* engine = pjChooseEngine(c, o->engine, plan->settings->engine ? plan->settings->engine : config->engine, o->targetName ? o->targetName : plan->settings->target, config, NULL);
                    const FgTarget* target = engine && streq0(engine, "native") ? nbResolveTarget(c, config, o->targetName ? o->targetName : plan->settings->target) : NULL;
                    FcCtx step;
                    pjStepInit(&step);
                    FC_TRY(&step) {
                        pjCheck(&step, plan, prelude, storeRoot, target);
                    } FC_CATCH(&step) {
                        FcList* errors;
                        pjForwardWarnings(po, &step);
                        if (!pjIsCompileErrorKind(step.err.kind) && step.err.kind != FC_ERR_PROJECT && step.err.kind != FC_ERR_PACKAGE) { c->err = step.err; fcRethrow(c); }
                        errors = fcDiagnosticsOf(&step, &step.err, NULL);
                        for (k = 0; k < errors->count; k++) frDiagnostic((const FcDiagnostic*)errors->items[k]);
                        failure = errors->count > 0 ? ((const FcDiagnostic*)errors->items[0])->message : "it does not compile";
                    } FC_END_TRY;
                    if (!failure) {
                        pjForwardWarnings(po, &step);
                        succeeded++;
                        frLog(fcFmt(c, "%s: compiled%s (a library: its code goes into the programs that import it).", name, target ? fcFmt(c, " for %s", target->name) : ""));
                        frProjectResult(name, true, false, false, NULL);
                    }
                } else {
                    FrOutcome outcome = frBuildProgram(c, o, plan, plan->settings->output, true, prelude, storeRoot, po);
                    if (outcome.exit == 0) { succeeded++; frProjectResult(name, true, false, false, NULL); }
                    else failure = outcome.error ? outcome.error : "the build failed";
                }
            } FC_CATCH(c) {
                if (frIsKind(c, "native-config: ")) failure = c->err.message + 15;
                else if (frIsKind(c, "unported: ")) failure = fcFmt(c, "forge-c: %s", c->err.message);
                else if (c->err.kind == FC_ERR_PACKAGE) failure = c->err.message;
                else if (frIsKind(c, "vm: ")) failure = c->err.message + 4;
                else if (pjIsCompileErrorKind(c->err.kind) || c->err.kind == FC_ERR_NATIVE || c->err.kind == FC_ERR_PROJECT) {
                    FcList* errors = fcDiagnosticsOf(c, &c->err, NULL);
                    for (k = 0; k < errors->count; k++) frDiagnostic((const FcDiagnostic*)errors->items[k]);
                    failure = errors->count > 0 ? ((const FcDiagnostic*)errors->items[0])->message : c->err.message;
                } else fcRethrow(c);
            } FC_END_TRY;
        }
        if (failure) {
            fcListAdd(c, failed, project);
            if (!firstError) firstError = fcFmt(c, "%s: %s", name, failure);
            frLog(fcFmt(c, "%s: error: %s", name, pjTrimEndWs(c, failure)));
            frProjectResult(name, false, false, false, failure);
        }
    }
    frSetProject(NULL);
    return frOutcome(failed->count == 0 ? 0 : 1, fcFmt(c, "Projects: %d succeeded, %d failed, %d skipped.", succeeded, failed->count - skipped, skipped), firstError);
}
/* ReportBuildProject */
static FrOutcome frBuildProject(FcCtx* c, const TcOptions* o, const char* prelude, const char* storeRoot, PjOut* po) {
    const char* file = (const char*)o->files->items[0];
    PjWorkspace* w;
    PjPlan* plan;
    if (!nbFileExists(file)) return frFailure(c, 2, fcFmt(c, "File not found: %s", file));
    w = pjWorkspaceOpen(c, file);
    if (o->all) return frBuildAll(c, o, w, prelude, storeRoot, po);
    plan = frPlan(c, o, w, pjSelectProject(w, o->projectName));
    if (plan->project->project->type == PJ_LIBRARY) {
        const char* name = plan->project->project->name;
        if (o->checkOnly) {
            PjBuildArgs a = frArgs(o);
            NbConfig* config = pjConfigFor(c, &a, plan->project->project->filePath);
            const FgTarget* target = o->targetName || plan->settings->target ? nbResolveTarget(c, config, o->targetName ? o->targetName : plan->settings->target) : NULL;
            frCheck(c, plan, prelude, storeRoot, target, po);
            frLog(fcFmt(c, "%s: compiled%s (a library: its code goes into the programs that import it).", name, target ? fcFmt(c, " for %s", target->name) : ""));
            return frOutcome(0, NULL, NULL);
        }
        {
            const char* package = pjPackLibrary(c, po, plan, o->output, prelude, storeRoot, true);   /* (with the monitor: "Packed ..." on the log) */
            frLog(fcFmt(c, "%s (package, import \"%s\")", package, pjImportName(c, plan->project->project)));
            frOutput("package", package);
            return frOutcome(0, package, NULL);
        }
    }
    return frBuildProgram(c, o, plan, o->output ? o->output : plan->settings->output, false, prelude, storeRoot, po);
}
/* ReportBuild: the exceptions of a build as the report has them */
static FrOutcome frBuild(FcCtx* c, const TcOptions* o, const char* prelude, const char* storeRoot, PjOut* po) {
    FrOutcome volatile r;
    FC_TRY(c) {
        if (o->files->count == 1 && tcIsProjectFile((const char*)o->files->items[0])) r = frBuildProject(c, o, prelude, storeRoot, po);
        else if (o->all) r = frFailure(c, 2, "--all builds a solution or a project file.");
        else r = frBuildFiles(c, o, prelude, storeRoot);
    } FC_CATCH(c) {
        if (frIsKind(c, "native-config: ")) {
            FcDiagnostic d;
            memset(&d, 0, sizeof d);
            d.message = c->err.message + 15; d.severity = FC_SEV_ERROR; d.code = "E7003";
            frDiagnostic(&d);
            r = frOutcome(2, NULL, d.message);
        }
        else if (c->err.kind == FC_ERR_PROJECT || c->err.kind == FC_ERR_PACKAGE) r = frFailure(c, 1, c->err.message);
        else if (frIsKind(c, "vm: ")) r = frFailure(c, 1, c->err.message + 4);
        else if (pjIsCompileErrorKind(c->err.kind) || c->err.kind == FC_ERR_NATIVE) r = frCompileFailure(c, c);
        else fcRethrow(c);
    } FC_END_TRY;
    return r;
}

/* ReportBytecode: the program file of the files or of a project, the libraries of its packages (built now) and the VM of its widths */
static FrOutcome frBytecode(FcCtx* c, const TcOptions* o, const char* prelude, const char* storeRoot) {
    FrOutcome volatile r;
    FC_TRY(c) {
        PjPlan* plan = NULL;
        FcList* texts = fcListNew(c);
        int i;
        bool usage = false;
        for (i = 0; i < o->files->count && !plan && !usage; i++)
            if (tcIsProjectFile((const char*)o->files->items[i])) {
                PjWorkspace* w;
                if (o->files->count != 1) { r = frFailure(c, 2, "bytecode takes source files or one project."); usage = true; break; }
                if (!nbFileExists((const char*)o->files->items[0])) { r = frFailure(c, 2, fcFmt(c, "File not found: %s", (const char*)o->files->items[0])); usage = true; break; }
                w = pjWorkspaceOpen(c, (const char*)o->files->items[0]);
                plan = frPlan(c, o, w, pjSelectProject(w, o->projectName));
                if (plan->project->project->type == PJ_LIBRARY) { r = frFailure(c, 2, fcFmt(c, "'%s' is a library: it is no program by itself (a program that imports it carries it).", plan->project->project->name)); usage = true; }
            }
        if (!usage && !plan)
            for (i = 0; i < o->files->count; i++) {
                const char* text = tcReadSource(c, o, (const char*)o->files->items[i]);
                if (!text) { r = frFailure(c, 2, fcFmt(c, "File not found: %s", (const char*)o->files->items[i])); usage = true; break; }
                fcListAdd(c, texts, (void*)text);
            }
        if (!usage) {
            FcStore* store = NULL;
            FcNatives* registry = NULL;
            FcLinked* linked;
            FcList* libraries;
            FcBuf bytes = {0}, log = {0};
            const char* full = fcFullPath(c, ".", o->outputFile);
            const char* vm;
            const char* error = NULL;
            FILE* f;
            fcMonitorStep(0, plan ? fcFmt(c, "Compiling %s", plan->project->project->name) : fcFmt(c, "Compiling %s", fgJoin(c, tcFileNames(c, o), " ")), 0, 0);
            if (!plan) plan = tcFilesPlan(c, o, o->files, texts, 1, NULL);
            nvStoreRoot = storeRoot;
            linked = tcLinkVm(c, o, plan, prelude, storeRoot, &store, &registry);
            fcBufStr(&log, "");
            libraries = nvEnsureLibraries(c, linked, store, registry, &log);   /* (PackageLibraries: the libraries of its packages, built now) */
            frLogLines(log.p);
            fcBufFree(&log);
            fcBufStr(&bytes, "");
            bytes.len = 0;
            fcProgramFileWrite(c, linked, true, &bytes);
            nbMkdirs(c, fcDirectoryName(c, full));
            f = fopen(full, "wb");
            if (!f) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "unported: the program file cannot be written ('%s')", full));
            fwrite(bytes.p, 1, bytes.len, f);
            fclose(f);
            frLog(fcFmt(c, "%s (%zu bytes)", full, bytes.len));
            fcBufFree(&bytes);
            frOutput("program", full);
            for (i = 0; i < libraries->count; i++) frOutput("library", fcFullPath(c, ".", (const char*)libraries->items[i]));
            fcBufStr(&log, "");
            vm = nvLocate(c, linked->intWidth, linked->floatWidth, &log, &error);
            frLogLines(log.p);
            fcBufFree(&log);
            if (vm) frOutput("vm", vm); else frLog(fcFmt(c, "warning: %s", error));
            r = frOutcome(0, full, NULL);
        }
    } FC_CATCH(c) {
        if (c->err.kind == FC_ERR_PROJECT) r = frFailure(c, 2, c->err.message);
        else if (c->err.kind == FC_ERR_PACKAGE) r = frFailure(c, 1, c->err.message);
        else if (pjIsCompileErrorKind(c->err.kind)) r = frCompileFailure(c, c);
        else fcRethrow(c);
    } FC_END_TRY;
    return r;
}

/* ReportPublish */
static FrOutcome frPublish(FcCtx* c, const TcOptions* o, const char* prelude, const char* storeRoot, PjOut* po) {
    const char* file = o->files->count > 0 ? (const char*)o->files->items[0] : "";
    PjWorkspace* volatile w = NULL;
    FcList* volatile projects = NULL;
    const char* volatile failure = NULL;
    PjPublishOptions options;
    FcList* results;
    bool ok = true;
    const PjPublishResult* volatile first = NULL;
    int i, k;
    if (o->files->count != 1 || !tcIsProjectFile(file)) return frFailure(c, 2, "publish takes one solution (.firesln) or project (.fireproj).");
    if (!nbFileExists(file)) return frFailure(c, 2, fcFmt(c, "File not found: %s", file));
    FC_TRY(c) {
        w = pjWorkspaceOpen(c, file);
        if (o->projectName) { projects = fcListNew(c); fcListAdd(c, projects, pjSelectProject(w, o->projectName)); }
        else if (!w->solution) projects = fcListCopy(c, w->members);
    } FC_CATCH(c) {
        if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
        failure = c->err.message;
    } FC_END_TRY;
    if (failure) return frFailure(c, 2, failure);
    if (o->overlays->count > 0) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: --overlay with publish");
    options.toolchain = o->toolchainName; options.includeDisabled = projects != NULL; options.quick = o->quick; options.rebuild = false;
    pjPublishSummary = NULL;
    /* (ProjectPublisher collects the warnings of each build with its result: nothing is reported as it comes) */
    fcOnReport = NULL;
    pjOnWarning = NULL;
    results = pjPublish(c, po, w, projects, &options, prelude, storeRoot, &ok);
    fcOnReport = frOnReportHook;
    pjOnWarning = frOnReportHook;
    frFlushPending();
    for (i = 0; i < results->count; i++) {
        const PjPublishResult* p = (const PjPublishResult*)results->items[i];
        for (k = 0; p->diagnostics && k < p->diagnostics->count; k++) frDiagnostic((const FcDiagnostic*)p->diagnostics->items[k]);
    }
    for (i = 0; i < results->count; i++) {
        const PjPublishResult* p = (const PjPublishResult*)results->items[i];
        frSetProject(p->project->project->name);
        for (k = 0; k < p->folders->count; k++) frOutput("publish", (const char*)p->folders->items[k]);
        frProjectResult(p->project->project->name, p->ok, p->skipped, p->upToDate, p->error);
        if (!first && !p->ok && p->error) first = p;
    }
    frSetProject(NULL);
    return frOutcome(ok ? 0 : 1, pjPublishSummary, first ? fcFmt(c, "%s: %s", first->project->project->name, first->error) : ok ? NULL : pjPublishSummary);
}

/* WantsReport: build, publish or bytecode with `--report json` (before `--`) */
static bool tcWantsReport(int argc, char** argv) {
    int i;
    if (argc < 2 || !(fcAsciiIEq(argv[1], "build") || fcAsciiIEq(argv[1], "publish") || fcAsciiIEq(argv[1], "bytecode"))) return false;
    for (i = 2; i < argc; i++) {
        if (streq0(argv[i], "--")) return false;
        if (fcAsciiIEq(argv[i], "--report=json")) return true;
        if (fcAsciiIEq(argv[i], "--report") && i + 1 < argc && fcAsciiIEq(argv[i + 1], "json")) return true;
    }
    return false;
}
/* BeginReport (the report on stdout, cancelling listened for, the standard packages installed with their messages on the log), then RunReported */
static int tcReportMain(int argc, char** argv, const char* prelude, const char* storeRoot) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    TcOptions* o;
    volatile int result = 0;
    struct sigaction sa;
    pthread_t input;
    memset(&sa, 0, sizeof sa);
    sa.sa_handler = frOnSignal;
    sigaction(SIGTERM, &sa, NULL);
    sigaction(SIGINT, &sa, NULL);
    if (!isatty(0) && pthread_create(&input, NULL, frReadInput, NULL) == 0) pthread_detach(input);
    fcBufStr(&frPending, "");
    frPending.len = 0;
    {
        /* (StandardSources.EnsureInstalled with the log of the report) */
        char* text = NULL;
        size_t n = 0;
        FILE* log = open_memstream(&text, &n);
        if (log) { nsEnsureInstalled(prelude, storeRoot, log); fclose(log); if (text) frLogLines(text); free(text); }
    }
    fcCtxInit(c);
    c->collecting = true;   /* (the collector of RunReported: the warnings of the code as they are reported) */
    o = tcParse(c, argc, argv);
    if (o->error) { result = tcUsageError(o); fcCtxFree(c); return result; }
    {
        const char* command = o->command == TC_PUBLISH ? "publish" : o->command == TC_BYTECODE ? "bytecode" : "build";
        FrOutcome volatile outcome;
        PjOut po;
        FcBuf ignored = {0};
        fcBufStr(&ignored, "");
        po.c = c; po.seen = fcListNew(c); po.err = &ignored;
        frStart(command, fgJoin(c, tcFileNames(c, o), " "));
        fcMonitorStepHook = frStepHook;
        fcOnReport = frOnReportHook;
        pjOnWarning = frOnReportHook;
        nbMonitorLine = frMonitorLine;
        pjMonitorOut = &frPending;
        if (setjmp(frCancelJump) == 0) {
            frCancelArmed = true;
            FC_TRY(c) {
                outcome = o->command == TC_PUBLISH ? frPublish(c, o, prelude, storeRoot, &po) : o->command == TC_BYTECODE ? frBytecode(c, o, prelude, storeRoot) : frBuild(c, o, prelude, storeRoot, &po);
            } FC_CATCH(c) {
                /* (a part forge-c does not port: the report ends with it as its error) */
                const char* m = c->err.message ? c->err.message : "?";
                const char* message = frIsKind(c, "unported: ") ? fcFmt(c, "forge-c: %s", m) : fcFmt(c, "forge-c: internal: %s", m);
                outcome = frFailure(c, 2, message);
            } FC_END_TRY;
            frCancelArmed = false;
        } else outcome = frOutcome(3, NULL, NULL);
        fcMonitorStepHook = NULL;
        fcOnReport = NULL;
        pjOnWarning = NULL;
        nbMonitorLine = NULL;
        frFlushPending();
        pjMonitorOut = NULL;
        frSetProject(NULL);
        if (frCancel) { frEnd(false, 3, true, "Build cancelled.", NULL); result = 3; }
        else { frEnd(outcome.exit == 0, outcome.exit, false, outcome.summary, outcome.error); result = outcome.exit; }
        fcBufFree(&ignored);
    }
    fcCtxFree(c);
    return result;
}
