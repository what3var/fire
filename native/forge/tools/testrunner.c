/* native/forge/tools/testrunner.c - `forge test` (Phase D 4, commit b833e71b): src/fire.Compiler/TestRunner.cs (TestProtocol, RunInVm, RunNative, Driver,
 * WriteSummary) and CommandLine.Test of CommandLine.cs. The tests run as a program: in the VM - the VM in C, `fire run <program.fbc>` (the C# forge runs
 * them in its own VM in the process; the VM in C prints the same) - or with --engine native as a native program for this machine (NativeBuilder.Build into
 * a folder fire-test-<guid> of the temp folder). Both report through the lines of the driver (tools/testdriver.c) on stdout.
 *
 * Where the VM is: FIRE_VM_EXE, else `fire` (`fire-i32`, ...) next to forge-c - as CVm.Locate finds it; building it into the cache when there is none
 * is CVm's (the engine vm of Phase D 3) and not here. Not ported: several files at once, -D, -f, the spinner of a console (a cosmetic of a terminal). */

typedef enum TrStatus { TR_PASSED, TR_FAILED, TR_ERROR } TrStatus;
typedef struct TrResult { const char* cls; const char* method; const char* name; const char* description; TrStatus status; const char* message; bool finished; FcBuf output; } TrResult;
typedef struct TrRun { const char* title; FcList* results; FcList* problems; double seconds; } TrRun;
typedef struct TrProtocol { FcCtx* c; TrRun* run; TrResult* current; bool done; } TrProtocol;

static int trCount(const TrRun* r, TrStatus s) { int i, n = 0; for (i = 0; i < r->results->count; i++) if (((const TrResult*)r->results->items[i])->status == s) n++; return n; }
static bool trOk(const TrRun* r) { return trCount(r, TR_FAILED) == 0 && trCount(r, TR_ERROR) == 0 && r->problems->count == 0; }

/* the fields of a line of the driver (Split('|')), with "\n" for the escaped line breaks */
static const char* trUnescape(FcCtx* c, const char* s) { return fgReplace(c, s, "\\n", "\n"); }
static const char* trPart(FcCtx* c, FcList* parts, int i) { return i < parts->count ? trUnescape(c, (const char*)parts->items[i]) : ""; }
static const char* trRest(FcCtx* c, FcList* parts, int from) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = from; i < parts->count; i++) { if (i > from) fcBufChar(&b, '|'); fcBufStr(&b, (const char*)parts->items[i]); }
    return trUnescape(c, fcBufDone(c, &b));
}
static void trClose(TrProtocol* p) {
    if (!p->current) return;
    if (!p->current->finished && !p->current->message[0]) p->current->message = "the test did not finish";
    p->current = NULL;
}
/* TestProtocol.Line */
static void trLine(TrProtocol* p, const char* line) {
    FcCtx* c = p->c;
    FcList* parts;
    const char* kind;
    if (!fcStartsWith(line, TD_MARKER)) {
        if (p->current) { fcBufStr(&p->current->output, line); fcBufChar(&p->current->output, '\n'); }
        return;
    }
    parts = fgSplit(c, line + strlen(TD_MARKER), '|');
    kind = (const char*)parts->items[0];
    if (strcmp(kind, "S") == 0) {
        TrResult* r = (TrResult*)fcAlloc(c, sizeof *r);
        trClose(p);
        memset(r, 0, sizeof *r);
        r->cls = trPart(c, parts, 1); r->method = trPart(c, parts, 2); r->name = fcFmt(c, "%s.%s", trPart(c, parts, 1), trPart(c, parts, 3)); r->description = trPart(c, parts, 4);
        r->status = TR_ERROR; r->message = "";
        fcBufStr(&r->output, "");
        fcListAdd(c, p->run->results, r);
        p->current = r;
    } else if (strcmp(kind, "P") == 0) {
        if (p->current) { p->current->status = TR_PASSED; p->current->finished = true; }
    } else if (strcmp(kind, "F") == 0 || strcmp(kind, "E") == 0) {
        if (p->current) { p->current->status = kind[0] == 'F' ? TR_FAILED : TR_ERROR; p->current->message = trRest(c, parts, 1); p->current->finished = true; }
    } else if (strcmp(kind, "C") == 0) {
        if (p->current && p->current->status != TR_ERROR) {
            p->current->message = fcFmt(c, "%s[CleanTest] threw: %s", p->current->status == TR_FAILED ? fcFmt(c, "%s; ", p->current->message) : "", trRest(c, parts, 1));
            p->current->status = TR_ERROR;
        }
    } else if (strcmp(kind, "X") == 0) {
        trClose(p);
        if (strcmp(trPart(c, parts, 2), "construct") == 0)
            fcListAdd(c, p->run->problems, (void*)fcFmt(c, "%s: the test object could not be made (new %s()): %s", trPart(c, parts, 1), trPart(c, parts, 1), trRest(c, parts, 3)));
        else fcListAdd(c, p->run->problems, (void*)fcFmt(c, "%s: [%s] threw: %s", trPart(c, parts, 1), trPart(c, parts, 2), trRest(c, parts, 3)));
    } else if (strcmp(kind, "D") == 0) {
        trClose(p);
        p->done = true;
    }
}
/* TestProtocol.Stopped: the program stopped - the running test is an error with this message */
static void trStopped(TrProtocol* p, const char* message) {
    if (p->current && !p->current->finished) { p->current->status = TR_ERROR; p->current->message = message; p->current->finished = true; trClose(p); }
    else fcListAdd(p->c, p->run->problems, (void*)fcFmt(p->c, "The test run stopped: %s", message));
}

/* the lines of a text (StreamReader.ReadLine: \n, \r\n, \r) to the protocol */
static void trLines(TrProtocol* p, const char* text, size_t n) {
    size_t i = 0, start = 0;
    while (i < n) {
        if (text[i] == '\n' || text[i] == '\r') {
            trLine(p, fcStrN(p->c, text + start, i - start));
            if (text[i] == '\r' && i + 1 < n && text[i + 1] == '\n') i++;
            start = ++i;
        } else i++;
    }
    if (start < n) trLine(p, fcStrN(p->c, text + start, n - start));
}

/* a program with its arguments: what it writes to stdout and stderr, its exit code (Process.ExitCode: 128 + the signal when one ended it) */
static int trExec(FcCtx* c, char** argv, FcBuf* out, FcBuf* err) {
    int o[2], e[2], status = 0;
    pid_t pid;
    struct pollfd fds[2];
    if (pipe(o) != 0 || pipe(e) != 0) return -1;
    fflush(stdout);
    fflush(stderr);
    pid = fork();
    if (pid == 0) {
        dup2(o[1], 1); dup2(e[1], 2);
        close(o[0]); close(o[1]); close(e[0]); close(e[1]);
        execv(argv[0], argv);
        _exit(127);
    }
    close(o[1]); close(e[1]);
    fds[0].fd = o[0]; fds[0].events = POLLIN;
    fds[1].fd = e[0]; fds[1].events = POLLIN;
    while (fds[0].fd >= 0 || fds[1].fd >= 0) {
        int k;
        if (poll(fds, 2, -1) < 0) { if (errno == EINTR) continue; break; }
        for (k = 0; k < 2; k++) {
            char buf[65536];
            ssize_t n;
            if (fds[k].fd < 0 || !(fds[k].revents & (POLLIN | POLLHUP | POLLERR))) continue;
            n = read(fds[k].fd, buf, sizeof buf);
            if (n > 0) fcBufPut(k == 0 ? out : err, buf, (size_t)n);
            else { close(fds[k].fd); fds[k].fd = -1; }
        }
    }
    (void)c;
    if (waitpid(pid, &status, 0) < 0) return -1;
    if (WIFEXITED(status)) return WEXITSTATUS(status);
    if (WIFSIGNALED(status)) return 128 + WTERMSIG(status);
    return -1;
}

/* ---- the driver of a run ---- */
typedef struct TrDriver { TrRun* run; PjPlan* plan; FcList* files; int tests; const TdFilter* filter; } TrDriver;
typedef struct TrDriverCall { TrDriver* d; int first; } TrDriverCall;
/* Driver.FileOf: the path of the source relative to the project (a plan), else the name of the file */
static const char* trFileOfSource(FcCtx* c, void* state, int source) {
    TrDriverCall* call = (TrDriverCall*)state;
    TrDriver* d = call->d;
    int i = source - call->first;
    if (d->plan) {
        if (i < 0 || i >= d->plan->sources->count) return NULL;
        return fgReplace(c, pjRelative(c, d->plan->project->project->directory, ((const PjSource*)d->plan->sources->items[i])->path), "\\", "/");
    }
    return d->files && i >= 0 && i < d->files->count ? (const char*)d->files->items[i] : NULL;
}
static const char* trDriverText(FcCtx* c, void* state, FcList* program, int firstUserSource) {
    TrDriverCall call;
    call.d = (TrDriver*)state;
    call.first = firstUserSource;
    return tdGenerate(c, program, firstUserSource, trFileOfSource, &call, call.d->run->problems, &call.d->tests, call.d->filter);
}

static double trNow(void) { struct timeval tv; gettimeofday(&tv, NULL); return (double)tv.tv_sec + tv.tv_usec / 1e6; }

typedef struct TrArgs {
    const char* file; const char* projectName; const char* engine; const char* filter; const char* toolchainName; const char* configFile;
    int mode; bool keepAll;
} TrArgs;

/* the VM for the widths of a program (CVm.Locate without building one): FIRE_VM_EXE, else next to forge-c */
static const char* trLocateVm(FcCtx* c, int intWidth, int floatWidth) {
    const char* given = getenv("FIRE_VM_EXE");
    const char* name = fcFmt(c, "fire%s%s", intWidth == 64 ? "" : fcFmt(c, "-i%d", intWidth), floatWidth == 64 ? "" : "-f32");
    const char* next;
    if (given && *given) {
        if (nbFileExists(given)) return given;
        if (intWidth == 64 && floatWidth == 64) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "FIRE_VM_EXE names '%s', which does not exist.", given));
    }
    next = nbCombine(c, nbBaseDirectory, name);
    if (nbFileExists(next)) return next;
    fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "unported: the VM '%s' is not next to forge-c (building it into the cache is CVm's - the engine vm)", name));
    return NULL;
}

/* a unique folder in the temp folder (Path.GetTempPath() + prefix + Guid) */
static const char* trTempFolder(FcCtx* c, const char* prefix) {
    unsigned char g[16];
    FILE* f = fopen("/dev/urandom", "rb");
    char hex[33];
    int i;
    const char* dir;
    if (!f || fread(g, 1, 16, f) != 16) { for (i = 0; i < 16; i++) g[i] = (unsigned char)(rand() & 0xFF); }
    if (f) fclose(f);
    for (i = 0; i < 16; i++) snprintf(hex + 2 * i, 3, "%02x", g[i]);
    dir = fcFmt(c, "%s%s%s", nbTempPath(c), prefix, hex);
    nbMkdirs(c, dir);
    return dir;
}
static void trRemoveTree(const char* path) {
    DIR* d = opendir(path);
    struct dirent* e;
    if (d) {
        while ((e = readdir(d)) != NULL) {
            char sub[4096];
            struct stat st;
            if (strcmp(e->d_name, ".") == 0 || strcmp(e->d_name, "..") == 0) continue;
            snprintf(sub, sizeof sub, "%s/%s", path, e->d_name);
            if (lstat(sub, &st) == 0 && S_ISDIR(st.st_mode)) trRemoveTree(sub); else unlink(sub);
        }
        closedir(d);
    }
    rmdir(path);
}

/* the warnings of a step (the DiagnosticCollector of the command line: each as it is reported) */
/* (`unnamed`: the sources of a file without a plan are texts in C# - RuntimeSession.Build, NativeBuilder.Build -, their diagnostics name no file) */
static void trWarnings(FcCtx* step, FILE* err, bool unnamed) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = 0; i < step->diags->count; i++) {
        FcDiagnostic* d = (FcDiagnostic*)step->diags->items[i];
        if (d->severity != FC_SEV_WARNING) continue;
        if (unnamed) d->file = NULL;
        fcDiagLogLine(&b, d);
    }
    fwrite(b.p, 1, b.len, err);
    fcBufFree(&b);
    step->diags->count = 0;
}

/* RunInVm: the tests of the file or the plan, in the VM in C */
static TrRun* trRunInVm(FcCtx* c, const char* title, const TrArgs* a, PjPlan* plan, FcList* files, const TdFilter* filter, const char* prelude, const char* storeRoot) {
    TrRun* run = (TrRun*)fcAlloc(c, sizeof *run);
    TrProtocol p;
    TrDriver d;
    FcLinked* linked;
    double start = trNow();
    run->title = title; run->results = fcListNew(c); run->problems = fcListNew(c);
    memset(&p, 0, sizeof p); p.c = c; p.run = run;
    memset(&d, 0, sizeof d); d.run = run; d.plan = plan; d.files = files; d.filter = filter;
    c->testDriver = trDriverText;
    c->testDriverState = &d;
    if (plan) {
        PjLinkIn in;
        memset(&in, 0, sizeof in);
        in.plan = plan; in.prelude = prelude; in.storeRoot = storeRoot; in.modeOverride = a->mode;
        linked = pjLink(c, &in, NULL);
    } else linked = fcLinkFile(c, a->file, prelude, storeRoot, fcStrictTag(a->file), a->mode);
    c->testDriver = NULL;
    if (d.tests > 0) {
        FcBuf fbc = {0}, out = {0}, err = {0};
        const char* vm = trLocateVm(c, linked->intWidth, linked->floatWidth);
        const char* dir = trTempFolder(c, "fire-test-");
        const char* file = fcFmt(c, "%s/tests.fbc", dir);
        char* argv[4];
        FILE* f;
        int code;
        fcProgramFileWrite(c, linked, true, &fbc);
        f = fopen(file, "wb");
        if (f) { fwrite(fbc.p, 1, fbc.len, f); fclose(f); }
        fcBufFree(&fbc);
        argv[0] = (char*)vm; argv[1] = (char*)"run"; argv[2] = (char*)file; argv[3] = NULL;
        fcBufStr(&out, ""); fcBufStr(&err, "");
        code = trExec(c, argv, &out, &err);
        trRemoveTree(dir);
        if (strstr(err.p, "its library 'libfire_pkg_project_") && strstr(err.p, "could not be loaded"))
            /* (PackageImports.RegisterForRun builds the library of a project's own natives - the overlay of the store - for the VM in the process) */
            fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: the natives of a project in the VM (the library of the overlay of the store, PackageLibrary)");
        trLines(&p, out.p, out.len);
        if (!p.done) {
            /* (the VM in C reports an exception nobody caught as UncaughtScriptException does: "Unhandled exception of type 'X': message") */
            const char* unhandled = strstr(err.p, "Unhandled exception of type ");
            if (unhandled && code != 0) {
                const char* end = strchr(unhandled, '\n');
                trStopped(&p, end ? fcStrN(c, unhandled, (size_t)(end - unhandled)) : fcStr(c, unhandled));
            } else trStopped(&p, "the program ended before the tests were done (`leave`/`terminate`?)");
        }
        fcBufFree(&out); fcBufFree(&err);
    }
    run->seconds = trNow() - start;
    return run;
}

/* RunNative: the tests as a native program for this machine */
static TrRun* trRunNative(FcCtx* c, const char* title, const TrArgs* a, PjPlan* plan, FcList* files, const TdFilter* filter, const char* prelude, const char* storeRoot, NbConfig* config) {
    TrRun* run = (TrRun*)fcAlloc(c, sizeof *run);
    TrProtocol p;
    TrDriver d;
    double start = trNow();
    const FgTarget* target;
    NbToolchain* toolchain;
    const char* dir;
    const char* exe;
    NbBuild b;
    NbResult r;
    run->title = title; run->results = fcListNew(c); run->problems = fcListNew(c);
    memset(&p, 0, sizeof p); p.c = c; p.run = run;
    memset(&d, 0, sizeof d); d.run = run; d.plan = plan; d.files = files; d.filter = filter;
    target = nbResolveTarget(c, config, NULL);
    toolchain = nbResolveToolchain(c, config, target, a->toolchainName, NB_PURPOSE_BUILD);
    dir = trTempFolder(c, "fire-test-");
    exe = fcFmt(c, "%s/tests", dir);
    memset(&b, 0, sizeof b);
    b.path = a->file; b.prelude = prelude; b.storeRoot = storeRoot; b.strict = plan ? false : fcStrictTag(a->file); b.mode = a->mode;
    if (plan) { pjApplyBuildSettings(c, config, plan); b.link = pjNbLink; b.project = plan; }
    c->testDriver = trDriverText;
    c->testDriverState = &d;
    FC_TRY(c) {
        r = nbBuild(c, &b, config, target, toolchain, exe);
    } FC_CATCH(c) {
        c->testDriver = NULL;
        trRemoveTree(dir);
        fcRethrow(c);
    } FC_END_TRY;
    c->testDriver = NULL;
    if (!r.ok) {
        const char* log = r.log;
        size_t n = strlen(log);
        while (n > 0 && (log[n - 1] == '\n' || log[n - 1] == '\r' || log[n - 1] == ' ' || log[n - 1] == '\t')) n--;
        fcListAdd(c, run->problems, (void*)fcFmt(c, "The native build of the tests failed:\n%.*s", (int)n, log));
    } else if (d.tests > 0) {
        FcBuf out = {0}, err = {0};
        char* argv[2];
        int code;
        argv[0] = (char*)exe; argv[1] = NULL;
        fcBufStr(&out, ""); fcBufStr(&err, "");
        code = trExec(c, argv, &out, &err);
        trLines(&p, out.p, out.len);
        if (!p.done) {
            /* (the lines of stderr as ErrorDataReceived collects them: AppendLine each, then Trim) */
            const char* text = pjTrim(c, err.p);
            trStopped(&p, fcFmt(c, "the program ended with code %d before the tests were done%s%s", code, *text ? ": " : "", text));
        }
        fcBufFree(&out); fcBufFree(&err);
    }
    trRemoveTree(dir);
    run->seconds = trNow() - start;
    return run;
}

/* WriteSummary: the list of the tests and the sums */
static void trWriteSummary(FcCtx* c, const TrRun* run, FcBuf* o) {
    int i, k, passed = trCount(run, TR_PASSED), failed = trCount(run, TR_FAILED), errors = trCount(run, TR_ERROR);
    fcBufFmt(o, "Tests of %s:\n", run->title);
    for (i = 0; i < run->results->count; i++) {
        const TrResult* r = (const TrResult*)run->results->items[i];
        FcList* lines;
        fcBufFmt(o, "%s  %s\n", r->status == TR_PASSED ? "  ok    " : r->status == TR_FAILED ? "  FAILED" : "  ERROR ", r->name);
        if (r->status == TR_PASSED) continue;
        if (r->description[0]) fcBufFmt(o, "          (%s)\n", r->description);
        lines = fgSplit(c, r->message, '\n');
        for (k = 0; k < lines->count; k++) fcBufFmt(o, "          %s\n", (const char*)lines->items[k]);
        if (r->output.len > 0) {
            /* (TrimEnd: the white space of .NET at the end) */
            const char* text = fcStrN(c, r->output.p, r->output.len);
            size_t n = r->output.len;
            while (n > 0 && (text[n - 1] == ' ' || (text[n - 1] >= 0x09 && text[n - 1] <= 0x0D))) n--;
            fcBufStr(o, "          output:\n");
            lines = fgSplit(c, fcStrN(c, text, n), '\n');
            for (k = 0; k < lines->count; k++) {
                const char* line = (const char*)lines->items[k];
                size_t m = strlen(line);
                while (m > 0 && line[m - 1] == '\r') m--;
                fcBufFmt(o, "            %.*s\n", (int)m, line);
            }
        }
    }
    for (i = 0; i < run->problems->count; i++) {
        FcList* lines = fgSplit(c, (const char*)run->problems->items[i], '\n');
        for (k = 0; k < lines->count; k++) {
            const char* line = (const char*)lines->items[k];
            size_t m = strlen(line);
            while (m > 0 && line[m - 1] == '\r') m--;
            fcBufFmt(o, k == 0 ? "  PROBLEM %.*s\n" : "          %.*s\n", (int)m, line);
        }
    }
    if (run->results->count == 0 && run->problems->count == 0) fcBufStr(o, "  no tests (a test is a method with [Test], see docs/tools/testing.md)\n");
    fcBufFmt(o, "%d test%s: %d passed, %d failed, %d error%s", run->results->count, run->results->count == 1 ? "" : "s", passed, failed, errors, errors == 1 ? "" : "s");
    if (run->problems->count > 0) fcBufFmt(o, ", %d problem%s", run->problems->count, run->problems->count == 1 ? "" : "s");
    fcBufFmt(o, " (%.2f s)\n", run->seconds);
}

/* ---- CommandLine.Test ---- */
static bool trIsProjectFile(const char* path) { return fcEndsWith(path, ".fireproj") || fcEndsWith(path, ".firesln") || fcEndsWith(path, ".FIREPROJ") || fcEndsWith(path, ".FIRESLN"); }

/* the one solution, else the one project of the current folder (Directory.GetFiles: NULL and the count when there is not exactly one) */
static const char* trFindHere(FcCtx* c, const char* extension, int* count) {
    DIR* d = opendir(".");
    struct dirent* e;
    const char* found = NULL;
    *count = 0;
    if (!d) return NULL;
    while ((e = readdir(d)) != NULL) {
        size_t n = strlen(e->d_name), m = strlen(extension);
        if (n >= m && fcAsciiIEq(e->d_name + n - m, extension) && nbFileExists(e->d_name)) { (*count)++; found = fcFullPath(c, ".", e->d_name); }
    }
    closedir(d);
    return *count == 1 ? found : NULL;
}

static int trParseArgs(int argc, char** argv, TrArgs* a) {
    int i;
    memset(a, 0, sizeof *a);
    a->mode = -1;
    for (i = 2; i < argc; i++) {
        const char* s = argv[i];
        const char* next = i + 1 < argc ? argv[i + 1] : NULL;
#define TR_VALUE() (next ? (i++, next) : NULL)
        if (streq0(s, "--filter")) a->filter = TR_VALUE();
        else if (fcStartsWith(s, "--filter=")) a->filter = s + 9;
        else if (streq0(s, "-p") || streq0(s, "--project")) { a->projectName = TR_VALUE(); if (!a->projectName) { fprintf(stderr, "-p must be followed by the name of a project.\n"); return 2; } }
        else if (streq0(s, "-m") || streq0(s, "--mode")) {
            const char* m = TR_VALUE();
            if (!m || !(fcAsciiIEq(m, "debug") || fcAsciiIEq(m, "release") || fcAsciiIEq(m, "performance"))) { fprintf(stderr, "-m must be followed by DEBUG, RELEASE or PERFORMANCE.\n"); return 2; }
            a->mode = fcAsciiIEq(m, "debug") ? 0 : fcAsciiIEq(m, "release") ? 1 : 2;
        }
        else if (streq0(s, "--engine")) {
            const char* e = TR_VALUE();
            if (!e || !(fcAsciiIEq(e, "vm") || fcAsciiIEq(e, "native"))) { fprintf(stderr, "--engine must be followed by vm or native.\n"); return 2; }
            a->engine = fcAsciiIEq(e, "vm") ? "vm" : "native";
        }
        else if (streq0(s, "--toolchain")) a->toolchainName = TR_VALUE();
        else if (streq0(s, "--config")) a->configFile = TR_VALUE();
        else if (streq0(s, "--keep-all")) a->keepAll = true;
        else if (s[0] == '-') { fprintf(stderr, "forge-c test: the option %s is not ported.\n", s); return 2; }
        else if (!a->file) a->file = s;
        else { fprintf(stderr, "forge-c test: several files at once are not ported (one file, project or solution).\n"); return 2; }
#undef TR_VALUE
    }
    return -1;
}

/* the line of a compile error of a run (DescribeErrors, after the title) */
static void trDescribe(FcCtx* step, const char* title, FILE* err, bool unnamed) {
    FcList* diags = fcDiagnosticsOf(step, &step->err, NULL);
    FcBuf b = {0};
    int i;
    fcBufFmt(&b, "%s: ", title);
    for (i = 0; i < diags->count; i++) {
        FcDiagnostic* d = (FcDiagnostic*)diags->items[i];
        if (unnamed) d->file = NULL;
        fcDiagLogLine(&b, d);
    }
    if (diags->count == 0) fcBufChar(&b, '\n');
    fwrite(b.p, 1, b.len, err);
    fcBufFree(&b);
}

/* `forge-c test [<file> | <project.fireproj> | <solution.firesln>] [-p <project>] [--engine vm|native] [-m <mode>] [--filter A.B,C]` */
static int trTestCommand(int argc, char** argv, const char* prelude, const char* storeRoot) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    TrArgs a;
    FcList* runs;
    FcBuf out = {0};
    const TdFilter* filter;
    NbConfig* config = NULL;
    const char* engine;
    volatile bool compileFailed = false;
    int r = trParseArgs(argc, argv, &a);
    volatile int i;
    if (r >= 0) return r;
    fcCtxInit(c);
    if (!a.file) {
        /* nothing named: the solution in this folder, else the project */
        int n;
        const char* found = trFindHere(c, ".firesln", &n);
        if (n == 0) found = trFindHere(c, ".fireproj", &n);
        if (!found) { fprintf(stderr, n == 0 ? "No file was named and there is no solution or project in this folder.\n" : "There are several solutions or projects in this folder: name one.\n"); fcCtxFree(c); return 2; }
        a.file = found;
    }
    if (!nbFileExists(a.file)) { fprintf(stderr, "File not found: %s\n", a.file); fcCtxFree(c); return 2; }
    filter = a.filter ? tdFilterParse(c, a.filter) : NULL;
    engine = a.engine ? a.engine : "vm";
    runs = fcListNew(c);
    fcBufStr(&out, "");
    if (strcmp(engine, "native") == 0) {
        FC_TRY(c) {
            if (a.configFile) {
                if (!nbFileExists(a.configFile)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "native-config: Configuration not found: %s", a.configFile));
                config = nbConfigLoad(c, a.configFile);
            } else config = nbConfigFindFor(c, a.file);
            if (a.keepAll) { config->stripping = 0; config->strippingFromCommandLine = true; }
        } FC_CATCH(c) {
            const char* m = c->err.message ? c->err.message : "";
            fprintf(stderr, "%s\n", fcStartsWith(m, "native-config: ") ? m + 15 : m);
            fcCtxFree(c);
            return 2;
        } FC_END_TRY;
    }
#define TR_GUARDED(title, call, unnamed) do { \
        FcCtx step; \
        fcCtxInit(&step); step.collecting = true; \
        FC_TRY(&step) { TrRun* run_ = (call); trWarnings(&step, stderr, (unnamed)); fcListAdd(c, runs, run_); } \
        FC_CATCH(&step) { \
            trWarnings(&step, stderr, (unnamed)); \
            if (step.err.kind == FC_ERR_NATIVE) { fprintf(stderr, "%s: error E7001: Not supported by the native backend yet: %s\n", (title), step.err.message); compileFailed = true; } \
            else if (pjIsCompileErrorKind(step.err.kind) || step.err.kind == FC_ERR_PROJECT) { trDescribe(&step, (title), stderr, (unnamed)); compileFailed = true; } \
            else if (step.err.kind == FC_ERR_INTERNAL && step.err.message && fcStartsWith(step.err.message, "native-config: ")) { fprintf(stderr, "%s\n", step.err.message + 15); compileFailed = true; } \
            else { fprintf(stderr, "%s\n", step.err.message ? step.err.message : "internal error"); fcCtxFree(&step); fcCtxFree(c); return 1; } \
        } FC_END_TRY; \
        /* (the runs live in the context of their step: it is freed with the command) */ \
    } while (0)
    if (trIsProjectFile(a.file)) {
        PjWorkspace* volatile w = NULL;
        FcList* volatile projects = NULL;
        FC_TRY(c) {
            w = pjWorkspaceOpen(c, a.file);
            projects = fcListNew(c);
            if (a.projectName) fcListAdd(c, projects, pjSelectProject(w, a.projectName));
            else for (i = 0; i < w->members->count; i++) fcListAdd(c, projects, w->members->items[i]);
        } FC_CATCH(c) {
            fprintf(stderr, "%s\n", c->err.message ? c->err.message : "");
            fcCtxFree(c);
            return 2;
        } FC_END_TRY;
        for (i = 0; i < projects->count; i++) {
            PjLoaded* project = (PjLoaded*)projects->items[i];
            PjPlan* plan = pjPlanCreate(w, project, true);
            if (plan->errors->count > 0) { fprintf(stderr, "%s: %s\n", project->project->name, fgJoin(c, plan->errors, "\n")); compileFailed = true; continue; }
            if (strcmp(engine, "native") == 0) TR_GUARDED(project->project->name, trRunNative(&step, project->project->name, &a, plan, NULL, filter, prelude, storeRoot, config), false);
            else TR_GUARDED(project->project->name, trRunInVm(&step, project->project->name, &a, plan, NULL, filter, prelude, storeRoot), false);
        }
    } else {
        FcList* names = fcListNew(c);
        const char* title = fcBaseName(a.file);
        fcListAdd(c, names, (void*)title);
        if (strcmp(engine, "native") == 0) TR_GUARDED(title, trRunNative(&step, title, &a, NULL, names, filter, prelude, storeRoot, config), true);
        else TR_GUARDED(title, trRunInVm(&step, title, &a, NULL, names, filter, prelude, storeRoot), true);
    }
#undef TR_GUARDED
    {
        int all = 0, passed = 0, failed = 0, errors = 0;
        bool ok = !compileFailed;
        for (i = 0; i < runs->count; i++) {
            const TrRun* run = (const TrRun*)runs->items[i];
            all += run->results->count; passed += trCount(run, TR_PASSED); failed += trCount(run, TR_FAILED); errors += trCount(run, TR_ERROR);
            if (!trOk(run)) ok = false;
            if (runs->count > 1 && run->results->count == 0 && run->problems->count == 0) continue;   /* a project of the solution without tests */
            trWriteSummary(c, run, &out);
            fcBufChar(&out, '\n');
        }
        if (runs->count > 1) fcBufFmt(&out, "All: %d tests, %d passed, %d failed, %d errors\n", all, passed, failed, errors);
        {
            FcBuf utf8 = {0};
            fcBufStr(&utf8, "");
            fcOut8(&utf8, out.p);
            fwrite(utf8.p, 1, utf8.len, stdout);
            fcBufFree(&utf8);
        }
        fcBufFree(&out);
        fcCtxFree(c);
        return ok ? 0 : 1;
    }
}
