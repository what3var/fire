/* native/forge/tools/conformance.c - `forge-c conformance` (docs/ROADMAP_C_PORT.md, Phase D step 6; docs/tools/testing.md "Without the test program"):
 * the data-driven core of fire.Testing without .NET - the cases of tests/cases run by the VM in C and, for the group native, as native programs, by the rules
 * of tests/run-cases.sh / tests/run-cases-vm.sh (the VM: TestCasesVm.cs; native: the cases "native == VM" of Program.cs, NatVerdict).
 *
 *   forge-c conformance [<group>|<group>/<case>...] [--filter <text>] [-j <jobs>] [--engine vm|native|all] [--fire <fire>] [--cases <dir>]
 *                       [--toolchain <name>] [--cache <dir>] [--list] [--no-retry] [--keep]
 *
 * Every case is compiled by this forge-c in a process of its own (`forge-c bytecode`, `forge-c build --engine native`) and run as a process (the VM `fire`, or the
 * native program): forge-c keeps state per process, and a case that crashes or hangs (a timeout) takes only itself along. The jobs run in threads that start
 * these processes; the default is modest (half the cores, at most 4). A case that is wrong runs again alone at the end (what depends on timing must not fail
 * because the machine was busy - run-cases.sh), unless --no-retry.
 *
 * The VM: --fire, else FIRE_VM_EXE, else the six variants next to forge-c when they are current (a distribution), else the VM of this forge-c's sources in
 * the user's cache (the folder `forge-c run` uses, CVm.CacheFolder) - all six variants (`fire-i32`, `fire-f32`, ...: a program of other widths runs in the
 * variant next to fire), built with `forge-c build-vm` when they are missing.
 * The libraries of the packages' natives: FIRE_PKG_PATH, then next to this forge-c (its Packages/ and Prebuilt/) - as run-cases-vm.sh. */

#ifndef _WIN32
#include <signal.h>
#endif

typedef struct CfCase {
    const char* group;
    const char* id;          /* the file name without .fire */
    const char* dir;         /* the folder of the case (the processes run there: the stack traces name the file relative to it) */
    const char* file;        /* <dir>/<id>.fire */
    const char* title;       /* `//# name: ...` */
    const char* modes[4];    /* `//# modes: ...` in upper case; none: the mode the program asks for (the group native: RELEASE) */
    int modeCount;
    const char* types;       /* `//# types: ...` (NULL: none) */
    bool skip, varies;
    char* expected; size_t expectedLen; bool hasExpected;
    char* diagnostics; size_t diagnosticsLen; bool hasDiagnostics;
    char* error;             /* `.error`, without the line feeds at its end (NULL: none) */
    char* unhandled;         /* `.unhandled`, the same */
} CfCase;

typedef enum CfEngine { CF_VM, CF_NATIVE } CfEngine;
typedef enum CfState { CF_PENDING, CF_OK, CF_FAIL, CF_SKIP } CfState;
typedef struct CfJob {
    CfCase* kase;
    CfEngine engine;
    CfState state;
    bool second;             /* right at the second try, alone */
    FcBuf bad;               /* the modes that are wrong (as run-cases.sh lists them) */
    FcBuf detail;            /* where it is wrong first */
} CfJob;

typedef struct CfRunner {
    const char* self;        /* this forge-c */
    const char* fire;        /* the VM */
    const char* cache;       /* the program files and native programs */
    const char* toolchain;   /* --toolchain of the native builds (NULL: the one forge chooses) */
    bool keepCache, keep;
    int vmTimeout, compileTimeout, buildTimeout, runTimeout;
    long long selfTime;
    CfJob** jobs;
    int jobCount;
    int next, done;
    pthread_mutex_t lock;
} CfRunner;

/* ---- texts ---- */
static char* cfDup(const char* s) { size_t n = strlen(s) + 1; char* r = (char*)malloc(n); if (!r) fbOutOfMemory(); memcpy(r, s, n); return r; }
static char* cfPrintf(const char* fmt, ...) {
    va_list ap;
    int n;
    char* r;
    va_start(ap, fmt);
    n = vsnprintf(NULL, 0, fmt, ap);
    va_end(ap);
    r = (char*)malloc((size_t)n + 1);
    if (!r) fbOutOfMemory();
    va_start(ap, fmt);
    vsnprintf(r, (size_t)n + 1, fmt, ap);
    va_end(ap);
    return r;
}
static void cfBufInit(FcBuf* b) { b->p = NULL; b->len = b->cap = 0; fcBufStr(b, ""); }
/* a file as text (NUL-terminated; NULL: none) */
static char* cfReadText(const char* path, size_t* len) {
    size_t n = 0;
    uint8_t* data = fbReadFile(path, &n);
    char* r;
    if (!data) return NULL;
    r = (char*)realloc(data, n + 1);
    if (!r) fbOutOfMemory();
    r[n] = 0;
    if (len) *len = n;
    return r;
}
/* what $(...) of the shell keeps: the line feeds at the end are gone */
static size_t cfTrimmedLen(const char* s, size_t n) { while (n > 0 && s[n - 1] == '\n') n--; return n; }
static char* cfTrimmed(char* s) { if (s) s[cfTrimmedLen(s, strlen(s))] = 0; return s; }
/* `grep -v`: the lines of text that start with none of the prefixes (and are none of the whole lines), each ended by a line feed */
static void cfDropLines(const char* text, size_t n, const char* const* prefixes, const char* const* whole, FcBuf* out) {
    size_t i = 0;
    while (i < n) {
        size_t e = i, k;
        bool drop = false;
        while (e < n && text[e] != '\n') e++;
        for (k = 0; prefixes && prefixes[k]; k++) { size_t pl = strlen(prefixes[k]); if (e - i >= pl && memcmp(text + i, prefixes[k], pl) == 0) drop = true; }
        for (k = 0; whole && whole[k]; k++) { size_t wl = strlen(whole[k]); if (e - i == wl && memcmp(text + i, whole[k], wl) == 0) drop = true; }
        if (!drop) { fcBufPut(out, text + i, e - i); fcBufChar(out, '\n'); }
        i = e + 1;
    }
}
static bool cfSame(const char* a, size_t an, const char* b, size_t bn) { return an == bn && (an == 0 || memcmp(a, b, an) == 0); }
/* TestCases.Difference: the first line where two texts differ */
static void cfDifference(FcBuf* detail, const char* what, const char* expected, size_t en, const char* actual, size_t an) {
    size_t ei = 0, ai = 0;
    int line = 1;
    if (detail->len) return;   /* (the first difference of the case is enough) */
    for (;; line++) {
        size_t ee = ei, ae = ai;
        bool eEnd = ei >= en, aEnd = ai >= an;
        while (ee < en && expected[ee] != '\n') ee++;
        while (ae < an && actual[ae] != '\n') ae++;
        if (eEnd && aEnd) { fcBufFmt(detail, "    %s: the same lines, different ends\n", what); return; }
        if (eEnd || aEnd || !cfSame(expected + ei, ee - ei, actual + ai, ae - ai)) {
            fcBufFmt(detail, "    %s, line %d:\n      expected: ", what, line);
            if (eEnd) fcBufStr(detail, "(nothing more)"); else fcBufPut(detail, expected + ei, ee - ei > 300 ? 300 : ee - ei);
            fcBufStr(detail, "\n      got:      ");
            if (aEnd) fcBufStr(detail, "(nothing more)"); else fcBufPut(detail, actual + ai, ae - ai > 300 ? 300 : ae - ai);
            fcBufChar(detail, '\n');
            return;
        }
        ei = ee + 1; ai = ae + 1;
    }
}
static void cfNote(FcBuf* detail, const char* text, size_t n) {
    size_t lines = 0, i;
    if (n == 0) { fcBufStr(detail, "    (nothing)\n"); return; }
    fcBufStr(detail, "    ");
    for (i = 0; i < n && lines < 12; i++) { fcBufChar(detail, text[i]); if (text[i] == '\n') { lines++; if (i + 1 < n && lines < 12) fcBufStr(detail, "    "); } }
    if (detail->p[detail->len - 1] != '\n') fcBufChar(detail, '\n');
}
static long long cfMtime(const char* path) { struct stat st; return stat(path, &st) == 0 ? (long long)st.st_mtime : -1; }

/* ---- processes ---- */
typedef struct CfRun { int code; bool timedOut; FcBuf out, err; } CfRun;
#ifndef _WIN32
static pthread_mutex_t cfForkLock = PTHREAD_MUTEX_INITIALIZER;
static void cfCloexec(int fd) { fcntl(fd, F_SETFD, FD_CLOEXEC); }
static long long cfNowMs(void) { struct timeval tv; gettimeofday(&tv, NULL); return (long long)tv.tv_sec * 1000 + tv.tv_usec / 1000; }
#endif
/* Runs exe with argv (argv[0] is the program) in cwd, stdin empty, with the output and the errors captured; after timeout seconds it is killed with its
 * process group (code 124, as `timeout` ends). */
static void cfSpawn(const char* exe, char* const* argv, const char* cwd, int timeout, CfRun* r) {
    cfBufInit(&r->out);
    cfBufInit(&r->err);
    r->code = -1;
    r->timedOut = false;
#ifdef _WIN32
    {
        FcBuf args;
        int i;
        cfBufInit(&args);
        for (i = 1; argv[i]; i++) { if (i > 1) fcBufChar(&args, ' '); fwQuoteArg(&args, argv[i]); }
        (void)timeout;   /* (no timeout on Windows yet) */
        r->code = fwRun(exe, args.p, cwd, NULL, &r->out, &r->err);
        if (r->code == -1) fcBufFmt(&r->err, "could not run %s\n", exe);
        fcBufFree(&args);
    }
#else
    {
        int outPipe[2], errPipe[2], status = 0, live = 2, devnull;
        pid_t pid;
        long long deadline = cfNowMs() + (long long)timeout * 1000;
        struct pollfd fds[2];
        char buf[16384];
        /* (the pipes of one job must not leak into the processes of another: close-on-exec, made and forked under one lock) */
        pthread_mutex_lock(&cfForkLock);
        if (pipe(outPipe) != 0) { pthread_mutex_unlock(&cfForkLock); fcBufStr(&r->err, "pipe failed\n"); return; }
        if (pipe(errPipe) != 0) { close(outPipe[0]); close(outPipe[1]); pthread_mutex_unlock(&cfForkLock); fcBufStr(&r->err, "pipe failed\n"); return; }
        cfCloexec(outPipe[0]); cfCloexec(outPipe[1]); cfCloexec(errPipe[0]); cfCloexec(errPipe[1]);
        devnull = open("/dev/null", O_RDONLY);
        if (devnull >= 0) cfCloexec(devnull);
        pid = fork();
        if (pid == 0) {
            setpgid(0, 0);
            if (devnull >= 0) dup2(devnull, 0);
            dup2(outPipe[1], 1);
            dup2(errPipe[1], 2);
            if (cwd && chdir(cwd) != 0) _exit(127);
            execv(exe, argv);
            _exit(127);
        }
        pthread_mutex_unlock(&cfForkLock);
        if (devnull >= 0) close(devnull);
        close(outPipe[1]); close(errPipe[1]);
        if (pid < 0) { close(outPipe[0]); close(errPipe[0]); fcBufStr(&r->err, "fork failed\n"); return; }
        fds[0].fd = outPipe[0]; fds[0].events = POLLIN;
        fds[1].fd = errPipe[0]; fds[1].events = POLLIN;
        while (live > 0) {
            int k, wait = (int)(deadline - cfNowMs());
            if (wait <= 0) {
                if (!r->timedOut) { r->timedOut = true; kill(-pid, SIGKILL); kill(pid, SIGKILL); deadline = cfNowMs() + 2000; continue; }
                break;   /* (a process outside the group still holds the pipes: stop reading) */
            }
            if (poll(fds, 2, wait > 500 ? 500 : wait) < 0) { if (errno == EINTR) continue; break; }
            for (k = 0; k < 2; k++) {
                ssize_t got;
                if (fds[k].fd < 0 || !(fds[k].revents & (POLLIN | POLLHUP | POLLERR))) continue;
                got = read(fds[k].fd, buf, sizeof buf);
                if (got > 0) fcBufPut(k == 0 ? &r->out : &r->err, buf, (size_t)got);
                else { close(fds[k].fd); fds[k].fd = -1; live--; }
            }
        }
        if (fds[0].fd >= 0) close(fds[0].fd);
        if (fds[1].fd >= 0) close(fds[1].fd);
        while (waitpid(pid, &status, 0) < 0 && errno == EINTR) { }
        if (r->timedOut) r->code = 124;
        else if (WIFEXITED(status)) r->code = WEXITSTATUS(status);
        else if (WIFSIGNALED(status)) r->code = 128 + WTERMSIG(status);
    }
#endif
}
static void cfRunFree(CfRun* r) { fcBufFree(&r->out); fcBufFree(&r->err); }

/* ---- the cases (TestCases.Load) ---- */
static int cfCmpStr(const void* a, const void* b) { return strcmp(*(char* const*)a, *(char* const*)b); }
/* the names in a folder (files: with that extension; else folders), sorted ordinally (malloc'd, NULL-terminated) */
static char** cfList(const char* dir, const char* ext, int* count) {
    DIR* d = opendir(dir);
    struct dirent* e;
    char** names = NULL;
    int n = 0, cap = 0;
    if (d) {
        while ((e = readdir(d)) != NULL) {
            char full[4096];
            size_t l = strlen(e->d_name);
            if (e->d_name[0] == '.') continue;
            snprintf(full, sizeof full, "%s/%s", dir, e->d_name);
            if (ext ? !(l > strlen(ext) && strcmp(e->d_name + l - strlen(ext), ext) == 0 && nbFileExists(full)) : !nbDirExists(full)) continue;
            if (n + 1 >= cap) { cap = cap ? cap * 2 : 64; names = (char**)realloc(names, sizeof(char*) * (size_t)cap); if (!names) fbOutOfMemory(); }
            names[n++] = cfDup(e->d_name);
        }
        closedir(d);
    }
    if (n > 1) qsort(names, (size_t)n, sizeof(char*), cfCmpStr);
    if (!names) { names = (char**)malloc(sizeof(char*)); if (!names) fbOutOfMemory(); }
    names[n] = NULL;
    *count = n;
    return names;
}
static char* cfSibling(const char* dir, const char* id, const char* ext) { return cfPrintf("%s/%s%s", dir, id, ext); }
/* one case: its source ends with lines `//# key: value` (name, note, modes, types, packed, output: varies, skip: forge run ...) */
static CfCase* cfLoadCase(const char* group, const char* dir, const char* name) {
    CfCase* k = (CfCase*)calloc(1, sizeof *k);
    char* text;
    size_t len = 0, end;
    char* path;
    if (!k) fbOutOfMemory();
    k->group = group;
    k->dir = dir;
    k->file = cfPrintf("%s/%s", dir, name);
    k->id = cfDup(name);
    ((char*)k->id)[strlen(name) - 5] = 0;
    text = cfReadText(k->file, &len);
    if (!text) { free(k); return NULL; }
    end = len;
    if (end > 0 && text[end - 1] == '\n') end--;
    while (end > 0) {
        size_t start = end;
        char* line;
        while (start > 0 && text[start - 1] != '\n') start--;
        if (end - start < 4 || memcmp(text + start, "//# ", 4) != 0) break;
        line = (char*)malloc(end - start + 1);
        if (!line) fbOutOfMemory();
        memcpy(line, text + start, end - start);
        line[end - start] = 0;
        if (line[strlen(line) - 1] == '\r') line[strlen(line) - 1] = 0;
        if (strncmp(line, "//# name: ", 10) == 0) k->title = cfDup(line + 10);
        else if (strcmp(line, "//# output: varies") == 0) k->varies = true;
        else if (strncmp(line, "//# skip: forge run", 19) == 0) k->skip = true;
        else if (strncmp(line, "//# types: ", 11) == 0) k->types = cfDup(line + 11);
        else if (strncmp(line, "//# modes: ", 11) == 0) {
            char* p = line + 11;
            while (*p && k->modeCount < 4) {
                char* w;
                char* q;
                while (*p == ' ') p++;
                if (!*p) break;
                w = p;
                while (*p && *p != ' ') p++;
                w = cfPrintf("%.*s", (int)(p - w), w);
                for (q = w; *q; q++) *q = (char)toupper((unsigned char)*q);
                k->modes[k->modeCount++] = w;
            }
        }
        free(line);
        if (start == 0) break;
        end = start - 1;
    }
    free(text);
    if (!k->title) k->title = k->id;
    if (k->modeCount == 0 && strcmp(group, "native") == 0) k->modes[k->modeCount++] = "RELEASE";   /* (the test program runs these in release) */
    path = cfSibling(dir, k->id, ".expected"); k->expected = cfReadText(path, &k->expectedLen); k->hasExpected = k->expected != NULL; free(path);
    if (!k->expected) { k->expected = cfDup(""); k->expectedLen = 0; }
    path = cfSibling(dir, k->id, ".diagnostics"); k->diagnostics = cfReadText(path, &k->diagnosticsLen); k->hasDiagnostics = k->diagnostics != NULL; free(path);
    path = cfSibling(dir, k->id, ".error"); k->error = cfTrimmed(cfReadText(path, NULL)); free(path);
    path = cfSibling(dir, k->id, ".unhandled"); k->unhandled = cfTrimmed(cfReadText(path, NULL)); free(path);
    return k;
}
/* --filter: texts separated by '|', any of them in the title or the name of the case, case does not matter (FIRE_TESTS_FILTER) */
static bool cfContainsI(const char* hay, const char* needle, size_t n) {
    size_t i, k, h = strlen(hay);
    if (n == 0) return true;
    for (i = 0; i + n <= h; i++) {
        for (k = 0; k < n && tolower((unsigned char)hay[i + k]) == tolower((unsigned char)needle[k]); k++) { }
        if (k == n) return true;
    }
    return false;
}
static bool cfSelected(const CfCase* k, const char* filter) {
    const char* p = filter;
    if (!filter || !*filter) return true;
    for (;;) {
        const char* e = strchr(p, '|');
        size_t n = e ? (size_t)(e - p) : strlen(p);
        if (n > 0 && (cfContainsI(k->title, p, n) || cfContainsI(k->id, p, n))) return true;
        if (!e) return false;
        p = e + 1;
    }
}

/* ---- the VM (run-cases-vm.sh `one`) ---- */
static void cfAddBad(CfJob* j, const char* mode, const char* why) { fcBufFmt(&j->bad, " %s%s", mode, why); }
static void cfVmCase(CfRunner* R, CfJob* j) {
    CfCase* k = j->kase;
    const char* defaultMode[1] = { "default" };
    const char* const* modes = k->modeCount ? k->modes : defaultMode;
    int count = k->modeCount ? k->modeCount : 1, m;
    char* name = cfPrintf("%s.fire", k->id);
    for (m = 0; m < count; m++) {
        const char* mode = modes[m];
        char* fbc = cfPrintf("%s/%s/%s.%s.fbc", R->cache, k->group, k->id, mode);
        char* cerrPath = cfPrintf("%s/%s/%s.%s.cerr", R->cache, k->group, k->id, mode);
        char* codePath = cfPrintf("%s/%s/%s.%s.code", R->cache, k->group, k->id, mode);
        long long codeTime = cfMtime(codePath);
        char* cerr;
        char* codeText;
        size_t cerrLen = 0;
        int ccode, code;
        CfRun run;
        FcBuf all, errBuf;
        const char* out;
        size_t outLen;
        /* the program file: written again only when the case or this forge-c is newer than it (--cache) */
        if (codeTime < 0 || cfMtime(k->file) > codeTime || R->selfTime > codeTime) {
            char* argv[10];
            int a = 0;
            CfRun compile;
            FILE* f;
            remove(fbc);
            argv[a++] = (char*)R->self; argv[a++] = "bytecode"; argv[a++] = name; argv[a++] = "-o"; argv[a++] = fbc;
            if (strcmp(mode, "default") != 0) { argv[a++] = "-m"; argv[a++] = (char*)mode; }
            if (k->types) { argv[a++] = "--types"; argv[a++] = (char*)k->types; }
            argv[a] = NULL;
            cfSpawn(R->self, argv, k->dir, R->compileTimeout, &compile);
            if ((f = fopen(cerrPath, "wb")) != NULL) { fwrite(compile.err.p, 1, compile.err.len, f); fclose(f); }
            if ((f = fopen(codePath, "wb")) != NULL) { fprintf(f, "%d\n", compile.code); fclose(f); }
            cfRunFree(&compile);
        }
        cerr = cfReadText(cerrPath, &cerrLen);
        if (!cerr) { cerr = cfDup(""); cerrLen = 0; }
        codeText = cfReadText(codePath, NULL);
        ccode = codeText ? atoi(codeText) : 1;
        free(codeText);
        if (ccode == 0 && nbFileExists(fbc)) {
            char* argv[3];
            argv[0] = (char*)R->fire; argv[1] = fbc; argv[2] = NULL;
            cfSpawn(R->fire, argv, k->dir, R->vmTimeout, &run);
            code = run.code;
        } else {
            cfBufInit(&run.out); cfBufInit(&run.err);
            run.timedOut = false;
            code = run.code = ccode;
        }
        out = run.out.p; outLen = run.out.len;
        cfBufInit(&all);
        fcBufPut(&all, cerr, cerrLen);
        fcBufPut(&all, run.err.p, run.err.len);
        {
            static const char* const warnings[] = { "warning ", NULL };
            cfBufInit(&errBuf);
            cfDropLines(all.p, all.len, warnings, NULL, &errBuf);
            errBuf.len = cfTrimmedLen(errBuf.p, errBuf.len);
            errBuf.p[errBuf.len] = 0;
        }
        if (run.timedOut) fcBufFmt(&j->detail, "    %s: the program did not end (%d s)\n", mode, R->vmTimeout);
        if (k->hasDiagnostics) {
            /* a case about the messages of the compiler: the whole error output, and the output */
            static const char* const noise[] = { "Building the native part of ", "Installing the standard package ", NULL };
            FcBuf d;
            cfBufInit(&d);
            cfDropLines(all.p, all.len, noise, NULL, &d);
            if (!cfSame(d.p, d.len, k->diagnostics, k->diagnosticsLen)) { cfAddBad(j, mode, "(diagnostics)"); cfDifference(&j->detail, "diagnostics", k->diagnostics, k->diagnosticsLen, d.p, d.len); }
            if (!cfSame(out, outLen, k->expected, k->expectedLen)) { cfAddBad(j, mode, ""); cfDifference(&j->detail, "output", k->expected, k->expectedLen, out, outLen); }
            fcBufFree(&d);
        } else if (k->error) {
            if (!(code != 0 && strstr(errBuf.p, k->error))) {
                cfAddBad(j, mode, "");
                if (!j->detail.len) { fcBufFmt(&j->detail, "    %s: expected an error with: %s\n    got (exit code %d):\n", mode, k->error, code); cfNote(&j->detail, errBuf.p, errBuf.len); }
            }
        } else {
            FcBuf actual;
            cfBufInit(&actual);
            fcBufPut(&actual, out, outLen);
            if (code != 0 && errBuf.len > 0) {
                if (strcmp(k->group, "native") == 0) {
                    if (!(k->unhandled && strcmp(errBuf.p, k->unhandled) == 0)) {
                        cfAddBad(j, mode, "(end)");
                        cfDifference(&j->detail, "the end (.unhandled)", k->unhandled ? k->unhandled : "", k->unhandled ? strlen(k->unhandled) : 0, errBuf.p, errBuf.len);
                    }
                } else if (strcmp(k->group, "trace") == 0) {
                    fcBufStr(&actual, "UNBEHANDELT: "); fcBufPut(&actual, errBuf.p, errBuf.len); fcBufChar(&actual, '\n');
                } else {
                    static const char* const frames[] = { "   at ", "   ... ", "   in thread '", NULL };
                    static const char* const awaited[] = { "   --- awaited at ---", NULL };
                    FcBuf message;
                    cfBufInit(&message);
                    cfDropLines(errBuf.p, errBuf.len, frames, awaited, &message);
                    fcBufStr(&actual, "UNBEHANDELT: "); fcBufPut(&actual, message.p, cfTrimmedLen(message.p, message.len)); fcBufChar(&actual, '\n');
                    fcBufFree(&message);
                }
            } else if (k->unhandled) { cfAddBad(j, mode, "(end)"); if (!j->detail.len) fcBufFmt(&j->detail, "    %s: expected the end %s, the program ended with %d\n", mode, k->unhandled, code); }
            if (!cfSame(actual.p, actual.len, k->expected, k->expectedLen)) { cfAddBad(j, mode, ""); cfDifference(&j->detail, mode, k->expected, k->expectedLen, actual.p, actual.len); }
            fcBufFree(&actual);
        }
        fcBufFree(&errBuf);
        fcBufFree(&all);
        cfRunFree(&run);
        free(cerr);
        free(fbc); free(cerrPath); free(codePath);
    }
    free(name);
}

/* ---- native (Program.cs: the cases "native == VM" - built in release, run, NatVerdict against the expected output of the case) ---- */
static bool cfStderrHas(const char* err, const char* text) { return strstr(err, text) != NULL; }
static void cfNativeCase(CfRunner* R, CfJob* j) {
    CfCase* k = j->kase;
    char* name = cfPrintf("%s.fire", k->id);
    char* exe = cfPrintf("%s/native-bin/%s%s", R->cache, k->id, NV_EXTENSION);
    char* argv[14];
    CfRun build, run;
    FcBuf expected, actual;
    int a = 0;
    argv[a++] = (char*)R->self; argv[a++] = "build"; argv[a++] = name; argv[a++] = "--engine"; argv[a++] = "native"; argv[a++] = "-m"; argv[a++] = "release";
    argv[a++] = "-o"; argv[a++] = exe;
    if (k->types) { argv[a++] = "--types"; argv[a++] = (char*)k->types; }
    if (R->toolchain) { argv[a++] = "--toolchain"; argv[a++] = (char*)R->toolchain; }
    argv[a] = NULL;
    remove(exe);
    cfSpawn(R->self, argv, k->dir, R->buildTimeout, &build);
    if (build.code != 0 || !nbFileExists(exe)) {
        cfAddBad(j, "build", "");
        fcBufFmt(&j->detail, "    the native build ended with %d%s:\n", build.code, build.timedOut ? " (timeout)" : "");
        {
            FcBuf both;
            cfBufInit(&both);
            fcBufPut(&both, build.out.p, build.out.len);
            fcBufPut(&both, build.err.p, build.err.len);
            /* (the end of the log says why) */
            cfNote(&j->detail, both.len > 3000 ? both.p + both.len - 3000 : both.p, both.len > 3000 ? 3000 : both.len);
            fcBufFree(&both);
        }
        cfRunFree(&build);
        free(name); free(exe);
        return;
    }
    cfRunFree(&build);
    argv[0] = exe; argv[1] = NULL;
    cfSpawn(exe, argv, k->dir, R->runTimeout, &run);
    cfBufInit(&expected);
    cfBufInit(&actual);
    if (k->varies) {
        /* (an output that differs from machine to machine: the native program against the VM, as the test program compares them) */
        char* fbc = cfPrintf("%s/native-bin/%s.fbc", R->cache, k->id);
        char* cargv[8];
        CfRun compile, vm;
        cargv[0] = (char*)R->self; cargv[1] = "bytecode"; cargv[2] = name; cargv[3] = "-o"; cargv[4] = fbc; cargv[5] = "-m"; cargv[6] = "release"; cargv[7] = NULL;
        cfSpawn(R->self, cargv, k->dir, R->compileTimeout, &compile);
        cargv[0] = (char*)R->fire; cargv[1] = fbc; cargv[2] = NULL;
        if (compile.code == 0) { cfSpawn(R->fire, cargv, k->dir, R->vmTimeout, &vm); fcBufPut(&expected, vm.out.p, vm.out.len); cfRunFree(&vm); }
        else fcBufPut(&expected, compile.err.p, compile.err.len);
        cfRunFree(&compile);
        remove(fbc);
        free(fbc);
    } else fcBufPut(&expected, k->expected, k->expectedLen);
    if (strncmp(k->title, "Abbruch:", 8) == 0) {
        /* an exception nothing catches: exit code 1 after the output so far, the line of the command line runner (with its stack trace) on stderr */
        fcBufStr(&expected, "stderr: ");
        fcBufStr(&expected, k->unhandled ? k->unhandled : "(the VM did not stop with an exception)");
        if (run.code == 1) {
            size_t n = run.err.len;
            while (n > 0 && (run.err.p[n - 1] == '\n' || run.err.p[n - 1] == '\r')) n--;
            fcBufPut(&actual, run.out.p, run.out.len); fcBufStr(&actual, "stderr: "); fcBufPut(&actual, run.err.p, n);
        } else { fcBufFmt(&actual, "Exitcode %d: ", run.code); fcBufPut(&actual, run.out.p, run.out.len); fcBufPut(&actual, run.err.p, run.err.len); }
    } else if (strstr(k->title, "(Meldung auf stderr)")) {
        /* the end of the program ended a thread: the line on stderr on its own */
        if (run.code == 0 && cfStderrHas(run.err.p, "ended like with terminate")) fcBufPut(&actual, run.out.p, run.out.len);
        else { fcBufFmt(&actual, "Exitcode %d: ", run.code); fcBufPut(&actual, run.out.p, run.out.len); fcBufStr(&actual, "stderr: "); fcBufPut(&actual, run.err.p, run.err.len); }
    } else if (strstr(k->title, "(Http.Pages-Zeile auf stderr)")) {
        int lines = 0;
        size_t i, start = 0;
        for (i = 0; i <= run.err.len; i++) if (i == run.err.len || run.err.p[i] == '\n') { if (i > start) lines++; start = i + 1; }
        if (run.code == 0 && cfStderrHas(run.err.p, "Http.Pages: page 'BadPage' (/bad), circuit ") && cfStderrHas(run.err.p, ": a handler of the page threw Boom: bang 1")
            && cfStderrHas(run.err.p, ": a handler of the page threw ArgumentException: ") && lines == 2) fcBufPut(&actual, run.out.p, run.out.len);
        else { fcBufFmt(&actual, "Exitcode %d: ", run.code); fcBufPut(&actual, run.out.p, run.out.len); fcBufStr(&actual, "stderr: "); fcBufPut(&actual, run.err.p, run.err.len); }
    } else {
        bool terminate = false;
        if (strncmp(k->title, "Threads:", 8) == 0) {
            char* source = cfReadText(k->file, NULL);
            terminate = source && strstr(source, "terminate(");
            free(source);
        }
        /* (`terminate(n)` with a whole number is the exit code of the process) */
        if (!(run.code == 0 || terminate)) fcBufFmt(&actual, "Exitcode %d: ", run.code);
        fcBufPut(&actual, run.out.p, run.out.len);
        fcBufPut(&actual, run.err.p, run.err.len);
    }
    if (run.timedOut) fcBufFmt(&j->detail, "    the native program did not end (%d s)\n", R->runTimeout);
    if (!cfSame(expected.p, expected.len, actual.p, actual.len)) { cfAddBad(j, "RELEASE", ""); cfDifference(&j->detail, "native", expected.p, expected.len, actual.p, actual.len); }
    fcBufFree(&expected);
    fcBufFree(&actual);
    cfRunFree(&run);
    if (!R->keep) remove(exe);
    free(name); free(exe);
}

static void cfRunJob(CfRunner* R, CfJob* j) {
    j->bad.len = 0; j->bad.p[0] = 0;
    j->detail.len = 0; j->detail.p[0] = 0;
    if (j->kase->skip || (j->engine == CF_VM && j->kase->varies)) { j->state = CF_SKIP; return; }
    if (j->engine == CF_VM) cfVmCase(R, j); else cfNativeCase(R, j);
    j->state = j->bad.len ? CF_FAIL : CF_OK;
}
static void* cfWorker(void* arg) {
    CfRunner* R = (CfRunner*)arg;
    for (;;) {
        int i;
        pthread_mutex_lock(&R->lock);
        i = R->next < R->jobCount ? R->next++ : -1;
        pthread_mutex_unlock(&R->lock);
        if (i < 0) return NULL;
        cfRunJob(R, R->jobs[i]);
        pthread_mutex_lock(&R->lock);
        R->done++;
        pthread_mutex_unlock(&R->lock);
    }
}
static void cfSleepMs(int ms) {
#ifdef _WIN32
    Sleep((DWORD)ms);
#else
    struct timespec ts;
    ts.tv_sec = ms / 1000; ts.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&ts, NULL);
#endif
}
/* the jobs on `threads` threads; a line on stderr every 30 seconds says how far */
static void cfRunAll(CfRunner* R, int threads) {
    pthread_t* t = (pthread_t*)calloc((size_t)threads, sizeof(pthread_t));
    int i, ticks = 0, done;
    time_t started = time(NULL);
    if (!t) fbOutOfMemory();
    R->next = R->done = 0;
    for (i = 0; i < threads; i++) pthread_create(&t[i], NULL, cfWorker, R);
    for (;;) {
        pthread_mutex_lock(&R->lock);
        done = R->done;
        pthread_mutex_unlock(&R->lock);
        if (done >= R->jobCount) break;
        cfSleepMs(200);
        if (++ticks % 150 == 0) fprintf(stderr, "[conformance] %d/%d (%ld s)\n", done, R->jobCount, (long)(time(NULL) - started));
    }
    for (i = 0; i < threads; i++) pthread_join(t[i], NULL);
    free(t);
}

/* ---- the VM and its variants ---- */
static const char* const cfVariants[6] = { "default", "i32", "i16", "f32", "i32-f32", "i16-f32" };
/* the six variants next to forge-c, else in the folder `forge-c run` builds its VM into (CVm.CacheFolder/<key>), built with `forge-c build-vm` where one is
 * missing or old */
static const char* cfLocateVm(FcCtx* c, const char* self, const char* storeRoot) {
    const char* dir;
    int i;
    FcList* missing = fcListNew(c);
    nvStoreRoot = storeRoot;
    {
        /* (next to forge-c, when all six are there and current: a distribution, `package-dist`) */
        bool beside = true;
        for (i = 0; i < 6 && beside; i++) {
            int iw = 64, fw = 64;
            nvParseVariant(c, cfVariants[i], &iw, &fw);
            beside = nvCurrent(c, nbCombine(c, nbBaseDirectory, fcFmt(c, "%s" NV_EXTENSION, nvVariantName(c, iw, fw))), iw, fw);
        }
        if (beside) return nbCombine(c, nbBaseDirectory, "fire" NV_EXTENSION);
    }
    dir = nbCombine(c, nvCacheFolder(c), fcStrN(c, nvSourceKey(c), 16));
    for (i = 0; i < 6; i++) {
        int iw = 64, fw = 64;
        nvParseVariant(c, cfVariants[i], &iw, &fw);
        if (!nvCurrent(c, nbCombine(c, dir, fcFmt(c, "%s" NV_EXTENSION, nvVariantName(c, iw, fw))), iw, fw)) fcListAdd(c, missing, (void*)cfVariants[i]);
    }
    if (missing->count) fprintf(stderr, "[conformance] building %d variant(s) of the VM into %s\n", missing->count, dir);
    for (i = 0; i < missing->count; i++) {
        /* (one after the other: each is a run of the optimising C compiler) */
        char* argv[6];
        CfRun run;
        argv[0] = (char*)self; argv[1] = "build-vm"; argv[2] = (char*)dir; argv[3] = "--variant"; argv[4] = (char*)missing->items[i]; argv[5] = NULL;
        cfSpawn(self, argv, NULL, 1800, &run);
        if (run.code != 0) { fwrite(run.out.p, 1, run.out.len, stderr); fwrite(run.err.p, 1, run.err.len, stderr); cfRunFree(&run); return NULL; }
        cfRunFree(&run);
    }
    return nbCombine(c, dir, "fire" NV_EXTENSION);
}

/* tests/cases: --cases, FIRE_CASES, else the nearest tests/cases above the working folder, else the one of the repository of native/ */
static const char* cfFindCases(FcCtx* c, const char* given) {
    char here[4096];
    const char* d;
    if (given) return fcFullPath(c, ".", given);
    if ((d = getenv("FIRE_CASES")) != NULL && *d) return fcFullPath(c, ".", d);
#ifdef _WIN32
    if (_getcwd(here, sizeof here)) {
        fwSlashes(here);
#else
    if (getcwd(here, sizeof here)) {
#endif
        char* p = (char*)fcStr(c, here);
        for (;;) {
            const char* probe = nbCombine(c, p, "tests/cases");
            char* slash;
            if (nbDirExists(probe)) return probe;
            slash = strrchr(p, '/');
            if (!slash || slash == p) break;
            *slash = 0;
        }
    }
    d = nbCombine(c, fcDirectoryName(c, nbNativeRoot), "tests/cases");
    return nbDirExists(d) ? d : NULL;
}

static const char* const cfUsage =
    "usage: forge-c conformance [<group>|<group>/<case>...] [--filter <text>] [-j <jobs>] [--engine vm|native|all] [--fire <fire>] [--cases <dir>]\n"
    "                           [--toolchain <name>] [--cache <dir>] [--list] [--no-retry] [--keep]\n"
    "Runs the cases of tests/cases (docs/tools/testing.md): every case is compiled by this forge-c and run in the VM in C, its output compared with\n"
    "<case>.expected (.diagnostics, .error, .unhandled); --engine native builds the cases of the group native as native programs with the C compiler\n"
    "of this machine and runs them (all: both). The groups are the folders of tests/cases (default: all); <group>/<case>: the cases whose name starts so.\n"
    "--filter  only the cases whose title or name contains the text (several separated by |, case does not matter)\n"
    "-j        jobs at a time (default: half the cores, at most 4)\n"
    "--fire    the VM (its variants fire-i32, fire-f32, ... next to it; default: built from the sources of this forge-c into the user's cache)\n"
    "--toolchain  the compiler of the native builds (gcc, clang, tcc, msvc, or one of fire.native.json; default: the one forge chooses)\n"
    "--cache   keeps the program files between runs (written again when the case or forge-c is newer); default: a temporary folder\n"
    "--list    every case with its verdict (default: only those that are not right)\n"
    "--no-retry  a case that is wrong is not run again alone; --keep: the native programs stay in the cache\n"
    "Exit code: 0 when every case that ran is right, 1 when one is wrong, 2 on a wrong command line or when the VM cannot be built.\n";

static int cfCmpJob(const void* a, const void* b) {
    const CfJob* x = *(const CfJob* const*)a;
    const CfJob* y = *(const CfJob* const*)b;
    int r = strcmp(x->kase->group, y->kase->group);
    if (r == 0) r = strcmp(x->kase->id, y->kase->id);
    return r ? r : (int)x->engine - (int)y->engine;
}

static int cfConformanceMain(int argc, char** argv, const char* prelude, const char* storeRoot) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    CfRunner R;
    const char* casesArg = NULL;
    const char* filter = NULL;
    const char* fireArg = NULL;
    const char* cacheArg = NULL;
    const char* casesRoot;
    bool list = false, retry = true, vm = true, native = false;
    int threads, i, g, groupCount = 0, ok = 0, fail = 0, skip = 0, cores;
    char** groups;
    FcList* wanted;
    FcList* jobs;
    char selfBuf[4096];
    memset(&R, 0, sizeof R);
    fcCtxInit(c);
    wanted = fcListNew(c);
    cores = (int)sysconf(_SC_NPROCESSORS_ONLN);
    threads = cores / 2;
    if (threads < 1) threads = 1;
    if (threads > 4) threads = 4;
    for (i = 2; i < argc; i++) {
        const char* a = argv[i];
        if ((streq0(a, "-j") || streq0(a, "--jobs")) && i + 1 < argc) { threads = atoi(argv[++i]); if (threads < 1) threads = 1; }
        else if (streq0(a, "--filter") && i + 1 < argc) filter = argv[++i];
        else if (streq0(a, "--fire") && i + 1 < argc) fireArg = argv[++i];
        else if (streq0(a, "--cases") && i + 1 < argc) casesArg = argv[++i];
        else if (streq0(a, "--cache") && i + 1 < argc) cacheArg = argv[++i];
        else if (streq0(a, "--toolchain") && i + 1 < argc) R.toolchain = argv[++i];
        else if (streq0(a, "--engine") && i + 1 < argc) {
            const char* e = argv[++i];
            if (fcAsciiIEq(e, "vm")) { vm = true; native = false; }
            else if (fcAsciiIEq(e, "native")) { vm = false; native = true; }
            else if (fcAsciiIEq(e, "all")) { vm = true; native = true; }
            else { fprintf(stderr, "'%s' is no engine (vm, native, all).\n%s", e, cfUsage); fcCtxFree(c); return 2; }
        }
        else if (streq0(a, "--list")) list = true;
        else if (streq0(a, "--no-retry")) retry = false;
        else if (streq0(a, "--keep")) R.keep = true;
        else if (streq0(a, "--help") || streq0(a, "-h")) { fputs(cfUsage, stdout); fcCtxFree(c); return 0; }
        else if (a[0] == '-') { fprintf(stderr, "Unknown option '%s'.\n%s", a, cfUsage); fcCtxFree(c); return 2; }
        else fcListAdd(c, wanted, (void*)a);
    }
    if (!nbFileExists(prelude)) { fprintf(stderr, "The tables of forge-c (%s) are not there: FIRE_TABLES names their folder (native/tables).\n", prelude); fcCtxFree(c); return 2; }
    casesRoot = cfFindCases(c, casesArg);
    if (!casesRoot || !nbDirExists(casesRoot)) { fprintf(stderr, "No folder tests/cases was found (--cases <dir>, FIRE_CASES).\n"); fcCtxFree(c); return 2; }
    /* this forge-c, as the processes of the cases start it */
#ifdef _WIN32
    R.self = fwSelfPath(selfBuf, sizeof selfBuf);
#else
    {
        ssize_t n = readlink("/proc/self/exe", selfBuf, sizeof selfBuf - 1);
        if (n > 0) { selfBuf[n] = 0; R.self = selfBuf; }
        else if (realpath(argv[0], selfBuf)) R.self = selfBuf;
    }
#endif
    if (!R.self || !nbFileExists(R.self)) { fprintf(stderr, "forge-c: cannot find its own program (%s).\n", argv[0]); fcCtxFree(c); return 2; }
    R.self = fcStr(c, R.self);
    R.selfTime = cfMtime(R.self);
    R.vmTimeout = getenv("VM_TIMEOUT") ? atoi(getenv("VM_TIMEOUT")) : 20;
    if (R.vmTimeout <= 0) R.vmTimeout = 20;
    R.compileTimeout = 120;
    R.buildTimeout = 1200;
    R.runTimeout = 120;
    /* the processes of the cases run in the folders of the cases: the tables, the store and the native root of this forge-c as full paths */
    setenv("FIRE_TABLES", fcDirectoryName(c, fcFullPath(c, ".", prelude)), 1);
    setenv("FIRE_PACKAGES", fcFullPath(c, ".", storeRoot), 1);
    setenv("FIRE_NATIVE_ROOT", fcFullPath(c, ".", nbNativeRoot), 1);
    /* the look of the desktop stays out (FIRE_SYSTEM_THEME=off, as the test program and the scripts); the libraries of the packages' natives next to forge-c */
    setenv("FIRE_SYSTEM_THEME", "off", 1);
    {
        const char* old = getenv("FIRE_PKG_PATH");
#ifdef _WIN32
        const char* sep = ";";
#else
        const char* sep = ":";
#endif
        const char* storeParent = fcDirectoryName(c, fcFullPath(c, ".", storeRoot));
        const char* path = old && *old ? fcFmt(c, "%s%s%s", old, sep, nbBaseDirectory) : nbBaseDirectory;
        if (storeParent && !streq0(storeParent, nbBaseDirectory)) path = fcFmt(c, "%s%s%s", path, sep, storeParent);
        setenv("FIRE_PKG_PATH", path, 1);
    }
    /* the VM */
    if (fireArg) R.fire = fcFullPath(c, ".", fireArg);
    else if (getenv("FIRE_VM_EXE") && *getenv("FIRE_VM_EXE")) R.fire = fcFullPath(c, ".", getenv("FIRE_VM_EXE"));
    else if (vm || native) R.fire = cfLocateVm(c, R.self, storeRoot);
    if (!R.fire || !nbFileExists(R.fire)) { fprintf(stderr, "The VM in C is not there%s%s.\n", R.fire ? ": " : "", R.fire ? R.fire : ""); fcCtxFree(c); return 2; }
    /* native: a C compiler on this machine */
    if (native) {
        static const char* const compilers[5] = { "cc", "gcc", "clang", "tcc", "cl" };
        bool any = false;
        int k;
#ifdef _WIN32
        for (k = 0; k < 5 && !any; k++) { char* p = fwResolveExe(compilers[k]); if (p) { any = true; free(p); } }
#else
        for (k = 0; k < 5 && !any; k++) { char* p = nbResolveExe(compilers[k]); if (p) { any = true; free(p); } }
#endif
        if (!any) { fprintf(stderr, "[conformance] no C compiler on this machine: the native builds are left out.\n"); native = false; }
    }
    /* the cache of the program files */
    if (cacheArg) { R.cache = fcFullPath(c, ".", cacheArg); R.keepCache = true; }
    else R.cache = fcFmt(c, "%sfire-conformance-%s", nbTempPath(c), nbGuid(c));
    if (R.cache[strlen(R.cache) - 1] == '/') R.cache = fcStrN(c, R.cache, strlen(R.cache) - 1);
    nbMkdirs(c, R.cache);
    if (native) nbMkdirs(c, nbCombine(c, R.cache, "native-bin"));
    /* the standard packages installed once, before the processes of the cases look at them */
    {
        char* iargv[3];
        CfRun inst;
        iargv[0] = (char*)R.self; iargv[1] = "install-standard"; iargv[2] = NULL;
        cfSpawn(R.self, iargv, NULL, 1800, &inst);
        cfRunFree(&inst);
    }
    /* the cases */
    groups = cfList(casesRoot, NULL, &groupCount);
    jobs = fcListNew(c);
    for (g = 0; g < groupCount; g++) {
        const char* group = fcStr(c, groups[g]);
        const char* dir = nbCombine(c, casesRoot, group);
        int n = 0, f;
        char** files;
        bool groupWanted = wanted->count == 0;
        for (i = 0; i < wanted->count; i++) {
            const char* w = (const char*)wanted->items[i];
            size_t gl = strlen(group);
            if (streq0(w, group) || (strncmp(w, group, gl) == 0 && w[gl] == '/')) groupWanted = true;
        }
        if (!groupWanted) continue;
        files = cfList(dir, ".fire", &n);
        nbMkdirs(c, nbCombine(c, R.cache, group));
        for (f = 0; f < n; f++) {
            CfCase* k;
            bool take = wanted->count == 0;
            for (i = 0; i < wanted->count; i++) {
                const char* w = (const char*)wanted->items[i];
                size_t gl = strlen(group);
                if (streq0(w, group)) take = true;
                else if (strncmp(w, group, gl) == 0 && w[gl] == '/' && strncmp(files[f], w + gl + 1, strlen(w + gl + 1)) == 0) take = true;
            }
            if (!take || !(k = cfLoadCase(group, dir, files[f])) || !cfSelected(k, filter)) continue;
            if (vm) { CfJob* j = (CfJob*)calloc(1, sizeof *j); if (!j) fbOutOfMemory(); j->kase = k; j->engine = CF_VM; cfBufInit(&j->bad); cfBufInit(&j->detail); fcListAdd(c, jobs, j); }
            if (native && streq0(group, "native")) { CfJob* j = (CfJob*)calloc(1, sizeof *j); if (!j) fbOutOfMemory(); j->kase = k; j->engine = CF_NATIVE; cfBufInit(&j->bad); cfBufInit(&j->detail); fcListAdd(c, jobs, j); }
        }
        for (f = 0; f < n; f++) free(files[f]);
        free(files);
    }
    for (g = 0; g < groupCount; g++) free(groups[g]);
    free(groups);
    for (i = 0; i < wanted->count; i++) {
        const char* w = (const char*)wanted->items[i];
        char* slash = strchr(w, '/');
        const char* group = slash ? fcStrN(c, w, (size_t)(slash - w)) : w;
        if (!nbDirExists(nbCombine(c, casesRoot, group))) { fprintf(stderr, "There is no group '%s' in %s.\n", group, casesRoot); if (!R.keepCache) nbDeleteTree(c, R.cache); fcCtxFree(c); return 2; }
    }
    R.jobs = (CfJob**)jobs->items;
    R.jobCount = jobs->count;
    pthread_mutex_init(&R.lock, NULL);
    cfRunAll(&R, threads);
    /* a case that was wrong runs once more, alone */
    if (retry) {
        for (i = 0; i < R.jobCount; i++) {
            CfJob* j = R.jobs[i];
            if (j->state != CF_FAIL) continue;
            cfRunJob(&R, j);
            if (j->state == CF_OK) j->second = true;
        }
    }
    if (R.jobCount > 1) qsort(R.jobs, (size_t)R.jobCount, sizeof(CfJob*), cfCmpJob);
    for (i = 0; i < R.jobCount; i++) {
        CfJob* j = R.jobs[i];
        const char* tag = j->engine == CF_NATIVE ? " [native]" : "";
        if (j->state == CF_OK) { ok++; if (list || j->second) printf("OK   %s/%s%s%s\n", j->kase->group, j->kase->id, tag, j->second ? " (alone, at the second try)" : ""); }
        else if (j->state == CF_SKIP) { skip++; printf("SKIP %s/%s%s\n", j->kase->group, j->kase->id, tag); }
        else { fail++; printf("FAIL %s/%s%s:%s\n", j->kase->group, j->kase->id, tag, j->bad.p); fputs(j->detail.p, stdout); }
    }
    /* the counts per group (and engine), then all */
    for (i = 0; i < R.jobCount;) {
        int k = i, o = 0, f = 0, s = 0;
        while (k < R.jobCount && streq0(R.jobs[k]->kase->group, R.jobs[i]->kase->group)) k++;
        {
            int e;
            for (e = 0; e < 2; e++) {
                int m, any = 0;
                o = f = s = 0;
                for (m = i; m < k; m++) {
                    if ((int)R.jobs[m]->engine != e) continue;
                    any++;
                    if (R.jobs[m]->state == CF_OK) o++; else if (R.jobs[m]->state == CF_FAIL) f++; else s++;
                }
                if (any) printf("%-16s %4d right, %4d wrong, %3d not run\n", e == CF_NATIVE ? fcFmt(c, "%s [native]", R.jobs[i]->kase->group) : R.jobs[i]->kase->group, o, f, s);
            }
        }
        i = k;
    }
    printf("%d right, %d wrong, %d not run\n", ok, fail, skip);
    fflush(stdout);
    if (!R.keepCache) nbDeleteTree(c, R.cache);
    fcCtxFree(c);
    return fail == 0 ? 0 : 1;
}
