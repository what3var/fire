/* native/forge/tools/conformance-steps.c - the suites of `forge-c conformance` (included by tools/conformance.c): checks that are more than one program and its
 * output - projects and solutions built with a linkage, plugins loaded by programs, programs for Windows run under Wine, a C library a program loads -, written
 * as data: a folder per case, tests/<suite>/<case>/, with its sources and a file `steps` that says what to build and run and what must come out
 * (docs/tools/testing.md "The suites"). And the VM for Windows: fire.exe and its variants built with MinGW from the sources of this forge-c, the libraries of the
 * packages for Windows (--wine, and the steps that run programs under Wine).
 *
 * A step is a line (a word, then its arguments - words, "quoted" with \" \\ \n \t inside; $NAME or ${NAME} a variable; win:<path> the path as Wine sees it):
 *
 *   title: <text>                  the title of the case          needs: <need>...    what the machine must have (cfUnmet), else not run (with the reason)
 *   timeout <seconds>              for the processes that follow (default: 1200 for forge, 120 for a program)
 *   env NAME=VALUE / unenv NAME    the environment of the processes that follow (and the variable $NAME)
 *   cd <folder>                    the folder of the processes that follow (default: the folder of the case)
 *   input <text>                   the standard input of the next process (default: empty)
 *   forge <arguments>              this forge-c                    run <program> <arguments>    a program
 *   vm <arguments>                 the VM (`fire`)                  wine <program.exe> <arguments>  a program for Windows under Wine (the prefix of the run)
 *   sh <command line>              /bin/sh -c (copying, editing a source, building a C library)
 *     each must end with 0; forge! run! ... with anything else; forge=3 run=70 ... with that code
 *   expect <file>                  the output of the last process is the file (of the case's folder; \r\n read as \n)
 *   expect-text <text>             ... is the text                 contains <text> / lacks <text>   the output or the errors contain the text / do not
 *   err-contains <text>            the errors contain the text     count <text> <op><n>   the lines of output and errors that contain the text (>=5, =0, <=2)
 *   under <seconds>                the last process took less     save <name> / same <name>   the output kept / the same as the one kept
 *
 * Variables: CASE (the folder of the case), OUT (a fresh folder of the case in the cache of the run), FORGE, FIRE (the VM of this machine), EXE (.exe on
 * Windows), SO (the extension of a shared library), WINE, WINFIRE (fire.exe), WINLIBS (the libraries for Windows), MINGW (x86_64-w64-mingw32-gcc); then the
 * environment of the run. A process step that ends otherwise than it should ends the case; a check that does not hold is noted and the steps go on. */

/* ---- the VM for Windows (MinGW) and the libraries of the packages for it ---- */
static const char* cfStoreRoot;   /* the store of the packages of this forge-c */
/* the folder of a path (malloc'd) */
static char* cfDirOf(const char* path) { const char* s = strrchr(path, '/'); return s ? cfPrintf("%.*s", (int)(s - path), path) : cfDup("."); }
#ifndef _WIN32
static bool cfMingwRun(const char* cmd, const char* cwd, FcBuf* log) {
    char* argv[4];
    CfRun run;
    bool ok;
    argv[0] = "/bin/sh"; argv[1] = "-c"; argv[2] = (char*)cmd; argv[3] = NULL;
    cfSpawn("/bin/sh", argv, cwd, 1800, &run);
    ok = run.code == 0;
    if (!ok && log) { fcBufFmt(log, "%s\n", cmd); fcBufPut(log, run.out.p, run.out.len); fcBufPut(log, run.err.p, run.err.len); }
    cfRunFree(&run);
    return ok;
}
typedef struct CfMingwJob { char* cmd; const char* cwd; char* what; bool ok; FcBuf log; } CfMingwJob;
typedef struct CfMingwPool { CfMingwJob* jobs; int count, next; pthread_mutex_t lock; } CfMingwPool;
static void* cfMingwWorker(void* arg) {
    CfMingwPool* p = (CfMingwPool*)arg;
    for (;;) {
        int i;
        pthread_mutex_lock(&p->lock);
        i = p->next < p->count ? p->next++ : -1;
        pthread_mutex_unlock(&p->lock);
        if (i < 0) return NULL;
        cfBufInit(&p->jobs[i].log);
        p->jobs[i].ok = cfMingwRun(p->jobs[i].cmd, p->jobs[i].cwd, &p->jobs[i].log);
    }
}
static void cfMingwAll(CfMingwJob* jobs, int count, int threads) {
    CfMingwPool pool;
    pthread_t t[8];
    int i;
    if (threads > 8) threads = 8;
    if (threads < 1) threads = 1;
    pool.jobs = jobs; pool.count = count; pool.next = 0;
    pthread_mutex_init(&pool.lock, NULL);
    for (i = 0; i < threads; i++) pthread_create(&t[i], NULL, cfMingwWorker, &pool);
    for (i = 0; i < threads; i++) pthread_join(t[i], NULL);
    pthread_mutex_destroy(&pool.lock);
}
/* fire.exe and its five variants of other widths (fire-i32.exe, ...) from native/vm with MinGW - what CVm.WriteSources for Windows was: vm/fire.c with the
 * defines of the widths, ws2_32 */
static bool cfWinBuildVm(CfRunner* R, const char* dir, int threads, FcBuf* log) {
    static const char* const variants[6][2] = { { "fire", "" }, { "fire-i32", "-DFIRE_INT32=1" }, { "fire-i16", "-DFIRE_INT16=1" }, { "fire-f32", "-DFIRE_FLOAT32=1" },
        { "fire-i32-f32", "-DFIRE_INT32=1 -DFIRE_FLOAT32=1" }, { "fire-i16-f32", "-DFIRE_INT16=1 -DFIRE_FLOAT32=1" } };
    CfMingwJob jobs[6];
    int i, n = 0;
    bool ok = true;
    for (i = 0; i < 6; i++) {
        char* exe = cfPrintf("%s/%s.exe", dir, variants[i][0]);
        if (nbFileExists(exe)) { free(exe); continue; }
        jobs[n].cmd = cfPrintf("'%s' -O2 -w %s '%s/vm/fire.c' -o '%s.partial' -lws2_32 && mv '%s.partial' '%s'", R->mingw, variants[i][1], nbNativeRoot, exe, exe, exe);
        jobs[n].cwd = dir;
        jobs[n].what = exe;
        n++;
    }
    if (n) fprintf(stderr, "[conformance] building %d variant(s) of fire.exe with MinGW into %s\n", n, dir);
    cfMingwAll(jobs, n, threads);
    for (i = 0; i < n; i++) {
        if (!jobs[i].ok) { ok = false; fcBufPut(log, jobs[i].log.p, jobs[i].log.len); }
        fcBufFree(&jobs[i].log); free(jobs[i].cmd); free(jobs[i].what);
    }
    return ok;
}
/* the libraries of the packages for Windows (PackageLibrary.Ensure for windows with MinGW): every import whose natives have functions for a library, and
 * graphics with windows (fire_gfx.dll); an import that cannot be built for Windows is left out (fire.exe then says what is missing) */
static void cfWinBuildLibs(CfRunner* R, const char* dir, int threads, FcBuf* notes) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    FcStore store;
    FcDict* cache;
    const FgTarget* windows = fgTargetNamed("windows");
    CfMingwJob* jobs;
    int count = 0, k, f, i, built = 0;
    char* gxx = cfDup(R->mingw);
    size_t gl = strlen(gxx);
    if (gl > 3 && streq0(gxx + gl - 3, "gcc")) { gxx = (char*)realloc(gxx, gl + 1); gxx[gl - 2] = '+'; gxx[gl - 1] = '+'; }
    fcCtxInit(c);
    memset(&store, 0, sizeof store);
    store.root = cfStoreRoot;
    cache = fcDictNew(c, FC_KEY_PTR);
    fcStoreLoad(c, &store);
    jobs = (CfMingwJob*)calloc((size_t)store.imports->count + 2, sizeof *jobs);
    if (!jobs) fbOutOfMemory();
    nlTargetOverride = windows;
    for (k = 0; k <= store.imports->count; k++) {
        FgInstImport* x = NULL;
        FgInstImport* w = NULL;
        bool gfx = k == store.imports->count, lib = false, isC;
        const char* wrapper;
        const char* work;
        const char* file;
        FcBuf links;
        const char* p;
        if (gfx) {
            x = nlFindImport(c, cache, &store, "graphics");
            w = nlFindImport(c, cache, &store, "windows");
            if (!x || !w) continue;
        } else {
            x = fgInstalledImport(c, cache, (FcPkgImport*)store.imports->items[k]);
            if (!x->native) continue;
            for (f = 0; f < x->native->functions->count; f++) if (!((FgPkgFn*)x->native->functions->items[f])->host) lib = true;
            if (!lib) continue;
            if (!fgSupportsAny(x->native, nlHostPlatformKeys(c))) { fcBufFmt(notes, "  %s: no natives for Windows\n", x->name); continue; }
            if (x->native->units && x->native->units->count > 0) { fcBufFmt(notes, "  %s: has units of its own (not built for Windows here)\n", x->name); continue; }
        }
        file = gfx ? "fire_gfx.dll" : fcFmt(c, "fire_pkg_%s_%s.dll", nlSafe(c, x->pkgName), nlSafe(c, x->name));
        if (nbFileExists(nbCombine(c, dir, file))) { built++; continue; }
        isC = gfx || x->native->isC;
        wrapper = gfx ? nlGenerateGraphicsWrapper(c, cache, &store, x, w, windows) : nlWrapperOf(c, cache, &store, x);
        work = nbCombine(c, dir, fcFmt(c, "build-%s", gfx ? "gfx" : nlSafe(c, x->name)));
        nbRuntimeFilesWriteTo(c, &store, fcDictNew(c, FC_KEY_PTR), work, windows->platform);
        nbWriteText(c, nbCombine(c, work, isC ? "wrapper.c" : "wrapper.cpp"), wrapper);
        cfBufInit(&links);
        for (p = wrapper; (p = strstr(p, "// fire-link: ")) != NULL; ) {
            size_t n;
            p += 14;
            n = strcspn(p, "\r\n");
            fcBufFmt(&links, " -l%.*s", (int)n, p);
        }
        jobs[count].cmd = cfPrintf("'%s' -std=%s -O1 -w -shared -static-libgcc%s %s -I. -Iabi -o '%s/%s.partial'%s -lws2_32 && mv '%s/%s.partial' '%s/%s'",
            isC ? R->mingw : gxx, isC ? "c11" : "c++17", isC ? "" : " -static-libstdc++", isC ? "wrapper.c" : "wrapper.cpp", dir, file, links.p, dir, file, dir, file);
        jobs[count].cwd = cfDup(work);
        jobs[count].what = cfDup(file);
        count++;
        fcBufFree(&links);
    }
    nlTargetOverride = NULL;
    if (count) fprintf(stderr, "[conformance] building %d librar%s of the packages for Windows with MinGW into %s\n", count, count == 1 ? "y" : "ies", dir);
    cfMingwAll(jobs, count, threads);
    for (i = 0; i < count; i++) {
        if (jobs[i].ok) built++;
        else {
            const char* e = strstr(jobs[i].log.p, "error");
            const char* end = e ? strchr(e, '\n') : NULL;
            fcBufFmt(notes, "  %s: %.*s\n", jobs[i].what, e ? (int)(end ? end - e : (ptrdiff_t)strlen(e)) : 30, e ? e : "the build failed");
        }
        fcBufFree(&jobs[i].log); free(jobs[i].cmd); free((char*)jobs[i].cwd); free(jobs[i].what);
    }
    free(jobs);
    free(gxx);
    fcCtxFree(c);
    fprintf(stderr, "[conformance] %d librar%s of the packages for Windows in %s\n", built, built == 1 ? "y" : "ies", dir);
}
#endif
/* Wine for the run (once): the prefix (wineboot), fire.exe and its variants (--fire: that one), the libraries of the packages. false: why says what is missing. */
static bool cfWineSetup(CfRunner* R, int threads, FcBuf* why) {
    bool ok = true;
#ifdef _WIN32
    (void)R; (void)threads;
    fcBufStr(why, "Wine runs on Linux");
    ok = false;
#else
    pthread_mutex_lock(&R->wineLock);
    if (!R->wineBooted) {
        char* w = nbResolveExe("wine");
        char* m = nbResolveExe("x86_64-w64-mingw32-gcc");
        if (!w || access(w, X_OK) != 0) { fcBufStr(why, "needs wine (not on this machine)"); ok = false; }
        else if (!m || access(m, X_OK) != 0) { fcBufStr(why, "needs MinGW (x86_64-w64-mingw32-gcc: fire.exe and the libraries for Windows are built with it)"); ok = false; }
        if (ok) {
            char* argv[3];
            CfRun run;
            FcBuf log;
            R->wine = w; w = NULL;
            R->mingw = m; m = NULL;
            if (!R->winePrefix) R->winePrefix = cfPrintf("%s/wine/prefix", R->cache);
            cfBufInit(&log);
            {
                char* d = cfPrintf("%s/wine", R->cache);
                mkdir(d, 0755);
                free(d);
            }
            if (!R->winFire) {
                char* d = cfPrintf("%s/wine/vm", R->cache);
                mkdir(d, 0755);
                if (!cfWinBuildVm(R, d, threads, &log)) { fcBufStr(why, "fire.exe could not be built with MinGW:\n"); fcBufPut(why, log.p, log.len > 3000 ? 3000 : log.len); ok = false; }
                R->winFire = cfPrintf("%s/fire.exe", d);
                free(d);
            }
            if (ok && !R->winLibs) {
                char* d = cfPrintf("%s/wine/libs", R->cache);
                FcBuf notes;
                mkdir(d, 0755);
                cfBufInit(&notes);
                cfWinBuildLibs(R, d, threads, &notes);
                if (notes.len) fprintf(stderr, "[conformance] not built for Windows:\n%s", notes.p);
                fcBufFree(&notes);
                R->winLibs = d;
            }
            if (ok) {
                /* the prefix (a few seconds the first time); wineserver keeps running for the run (-p) - no process of a case starts it and keeps its pipes */
                fprintf(stderr, "[conformance] Wine: the prefix %s\n", R->winePrefix);
                argv[0] = "wineboot"; argv[1] = "--init"; argv[2] = NULL;
                cfSpawnWine(R, argv, R->cache, 600, NULL, NULL, &run);
                cfRunFree(&run);
                {
                    char* server = cfPrintf("%s/wineserver", cfDirOf(R->wine));
                    char* sargv[3];
                    char** env = cfWineEnv(R, NULL);
                    sargv[0] = server; sargv[1] = "-p"; sargv[2] = NULL;
                    if (access(server, X_OK) == 0) { cfSpawnEx(server, sargv, R->cache, 30, env, NULL, &run); cfRunFree(&run); }
                    cfFreeEnv(env);
                    free(server);
                }
            }
            fcBufFree(&log);
        }
        free(w); free(m);
        R->wineBooted = ok;
        if (!ok) R->wine = NULL;
    } else if (!R->wine) { fcBufStr(why, "Wine could not be set up (above)"); ok = false; }
    pthread_mutex_unlock(&R->wineLock);
#endif
    return ok;
}
/* the end of the run: the wineserver of the prefix (only this one) */
static void cfWineEnd(CfRunner* R) {
#ifndef _WIN32
    if (R->wine && R->winePrefix) {
        char* server = cfPrintf("%s/wineserver", cfDirOf(R->wine));
        char* sargv[3];
        char** env = cfWineEnv(R, NULL);
        CfRun run;
        sargv[0] = server; sargv[1] = "-k"; sargv[2] = NULL;
        if (access(server, X_OK) == 0) { cfSpawnEx(server, sargv, R->cache, 60, env, NULL, &run); cfRunFree(&run); }
        cfFreeEnv(env);
        free(server);
    }
#else
    (void)R;
#endif
}

/* ---- a case of a suite ---- */
static CfCase* cfLoadSuiteCase(const char* suite, const char* suiteDir, const char* name) {
    CfCase* k;
    char* file = cfPrintf("%s/%s/steps", suiteDir, name);
    char* text = cfReadText(file, NULL);
    FcBuf needs;
    const char* p;
    if (!text) { free(file); return NULL; }
    k = (CfCase*)calloc(1, sizeof *k);
    if (!k) fbOutOfMemory();
    k->group = suite;
    k->id = cfDup(name);
    k->dir = cfPrintf("%s/%s", suiteDir, name);
    k->file = file;
    k->steps = text;
    k->expected = cfDup("");
    cfBufInit(&needs);
    for (p = text; *p; ) {
        size_t n = strcspn(p, "\n");
        const char* q = p;
        while (*q == ' ' || *q == '\t') q++;
        if (strncmp(q, "title:", 6) == 0) { const char* t = q + 6; while (*t == ' ') t++; k->title = cfPrintf("%.*s", (int)(p + n - t), t); cfTrimmed((char*)k->title); }
        else if (strncmp(q, "needs:", 6) == 0) fcBufFmt(&needs, " %.*s", (int)(p + n - q - 6), q + 6);
        p += n + (p[n] == '\n');
    }
    if (!k->title) k->title = k->id;
    if (needs.len) k->needs = cfDup(needs.p);
    fcBufFree(&needs);
    return k;
}

typedef struct CfSteps {
    CfRunner* R;
    CfJob* j;
    char* out;               /* $OUT */
    char* cwd;
    char* env[64];           /* NAME=VALUE of `env`, -NAME of `unenv` */
    int envCount;
    char* input;             /* `input` of the next process */
    int timeout;             /* 0: the default of the step */
    CfRun last;
    bool hasLast;
    double seconds;
    int line;
    const char* text;        /* the step as written */
    bool stop;
} CfSteps;

static const char* cfStepVar(CfSteps* S, const char* name, size_t n, FcBuf* tmp) {
    CfRunner* R = S->R;
    int i;
    tmp->len = 0; tmp->p[0] = 0;
#define CF_IS(x) (n == strlen(x) && strncmp(name, x, n) == 0)
    if (CF_IS("CASE")) return S->j->kase->dir;
    if (CF_IS("OUT")) return S->out;
    if (CF_IS("FORGE")) return R->self;
    if (CF_IS("FIRE")) return R->fire;
#ifdef _WIN32
    if (CF_IS("EXE")) return ".exe";
    if (CF_IS("SO")) return ".dll";
#else
    if (CF_IS("EXE")) return "";
    if (CF_IS("SO")) return ".so";
#endif
    if (CF_IS("WINE") || CF_IS("WINFIRE") || CF_IS("WINLIBS") || CF_IS("MINGW")) {
        FcBuf why;
        cfBufInit(&why);
        if (!cfWineSetup(R, 2, &why)) { fcBufFree(&why); return NULL; }
        fcBufFree(&why);
        if (CF_IS("WINE")) return R->wine;
        if (CF_IS("WINFIRE")) return R->winFire;
        if (CF_IS("MINGW")) return R->mingw;
        return R->winLibs;
    }
#undef CF_IS
    for (i = S->envCount - 1; i >= 0; i--) {
        const char* e = S->env[i];
        if (e[0] != '-' && strncmp(e, name, n) == 0 && e[n] == '=') return e + n + 1;
        if (e[0] == '-' && strncmp(e + 1, name, n) == 0 && e[n + 1] == 0) return "";
    }
    {
        char key[256];
        const char* v;
        if (n >= sizeof key) return NULL;
        memcpy(key, name, n); key[n] = 0;
        v = getenv(key);
        return v;
    }
}
/* $NAME/${NAME} in a text expanded (the values as they are; \x01 is a $ that was escaped); lenient (the command line of `sh`): a variable that is not set stays
 * for the shell. NULL: a variable is not set (error says which). malloc'd. */
static char* cfStepExpand(CfSteps* S, const char* raw, size_t len, bool lenient, FcBuf* error) {
    FcBuf word, tmp;
    size_t i = 0;
    cfBufInit(&word);
    cfBufInit(&tmp);
    while (i < len) {
        if (raw[i] == '$' && i + 1 < len && (raw[i + 1] == '{' || isalpha((unsigned char)raw[i + 1]) || raw[i + 1] == '_')) {
            size_t s = i + 1, e, after;
            const char* v;
            bool brace = raw[s] == '{';
            if (brace) s++;
            e = s;
            while (e < len && (isalnum((unsigned char)raw[e]) || raw[e] == '_')) e++;
            after = brace && e < len && raw[e] == '}' ? e + 1 : e;
            v = cfStepVar(S, raw + s, e - s, &tmp);
            if (!v && lenient) { fcBufPut(&word, raw + i, after - i); i = after; continue; }
            if (!v) { fcBufFmt(error, "the variable $%.*s is not set", (int)(e - s), raw + s); fcBufFree(&word); fcBufFree(&tmp); return NULL; }
            fcBufStr(&word, v);
            i = after;
        } else { fcBufChar(&word, raw[i] == '\x01' ? '$' : raw[i]); i++; }
    }
    fcBufFree(&tmp);
    return word.p;
}
/* the words of a step: quotes group, \" \\ \n \t \r \$ escape; then $NAME/${NAME} expanded, win:<path> as Wine sees it. `sh`: the rest of the line is one
 * word, only its variables expanded (the quotes and backslashes are the shell's). */
static int cfStepWords(CfSteps* S, const char* line, char** words, int max, FcBuf* error) {
    int count = 0;
    const char* p = line;
    FcBuf raw;
    size_t cl = strcspn(line, " \t");
    if (cl >= 2 && strncmp(line, "sh", 2) == 0 && (cl == 2 || line[2] == '!' || line[2] == '=')) {
        const char* rest = line + cl;
        while (*rest == ' ' || *rest == '\t') rest++;
        words[0] = cfPrintf("%.*s", (int)cl, line);
        words[1] = cfStepExpand(S, rest, strlen(rest), true, error);
        if (!words[1]) { free(words[0]); words[0] = NULL; return -1; }
        words[2] = NULL;
        return 2;
    }
    cfBufInit(&raw);
    while (*p && count < max) {
        bool any = false;
        while (*p == ' ' || *p == '\t') p++;
        if (!*p) break;
        raw.len = 0; raw.p[0] = 0;
        while (*p && *p != ' ' && *p != '\t') {
            if (*p == '"') {
                p++;
                any = true;
                while (*p && *p != '"') {
                    if (*p == '\\' && p[1]) {
                        p++;
                        fcBufChar(&raw, *p == 'n' ? '\n' : *p == 't' ? '\t' : *p == 'r' ? '\r' : *p == '$' ? '\x01' : *p);
                        p++;
                    } else fcBufChar(&raw, *p++);
                }
                if (*p == '"') p++;
            } else if (*p == '\\' && p[1]) {
                p++;
                fcBufChar(&raw, *p == 'n' ? '\n' : *p == 't' ? '\t' : *p == 'r' ? '\r' : *p == '$' ? '\x01' : *p);
                p++;
                any = true;
            } else { fcBufChar(&raw, *p++); any = true; }
        }
        if (!any) continue;
        {
            bool win = raw.len > 4 && strncmp(raw.p, "win:", 4) == 0;
            char* word = cfStepExpand(S, raw.p + (win ? 4 : 0), raw.len - (win ? 4 : 0), false, error);
            if (!word) {
                while (count > 0) free(words[--count]);
                words[0] = NULL;
                fcBufFree(&raw);
                return -1;
            }
            if (win) { words[count++] = cfWinPath(word); free(word); }
            else words[count++] = word;
        }
    }
    words[count] = NULL;
    fcBufFree(&raw);
    return count;
}
static void cfStepFail(CfSteps* S, const char* what, const char* note, size_t noteLen) {
    CfJob* j = S->j;
    fcBufFmt(&j->bad, " step %d", S->line);
    fcBufFmt(&j->detail, "    step %d (%s): %s\n", S->line, S->text, what);
    if (note) cfNote(&j->detail, note, noteLen > 3000 ? 3000 : noteLen);
}
static void cfStepBoth(CfSteps* S, FcBuf* both) {
    cfBufInit(both);
    if (!S->hasLast) return;
    fcBufPut(both, S->last.out.p, S->last.out.len);
    fcBufPut(both, S->last.err.p, S->last.err.len);
}
/* a process step: the program, its arguments, the code it must end with */
static void cfStepProcess(CfSteps* S, const char* cmd, char** words, int count) {
    CfRunner* R = S->R;
    char* argv[96];
    int a = 0, i, want = 0;
    bool anyCode = false, nonzero = false, wine = false;
    const char* exe = NULL;
    size_t cl = strcspn(cmd, "!=");
    char* shellLine = NULL;
    int timeout = S->timeout;
    long long started;
    char* resolved = NULL;
    if (cmd[cl] == '!') nonzero = true;
    else if (cmd[cl] == '=') { want = atoi(cmd + cl + 1); anyCode = false; }
    if (strncmp(cmd, "forge", cl) == 0 && cl == 5) { exe = R->self; argv[a++] = (char*)R->self; if (!timeout) timeout = 1200; }
    else if (strncmp(cmd, "vm", cl) == 0 && cl == 2) { exe = R->fire; argv[a++] = (char*)R->fire; }
    else if (strncmp(cmd, "run", cl) == 0 && cl == 3) {
        if (count < 2) { cfStepFail(S, "run: which program?", NULL, 0); S->stop = true; return; }
        resolved = words[1][0] == '/' ? cfDup(words[1]) : cfPrintf("%s/%s", S->cwd, words[1]);
        exe = resolved;
        argv[a++] = resolved;
        words++; count--;
    } else if (strncmp(cmd, "wine", cl) == 0 && cl == 4) {
        FcBuf why;
        cfBufInit(&why);
        if (!cfWineSetup(R, 2, &why)) { cfStepFail(S, why.p, NULL, 0); fcBufFree(&why); S->stop = true; return; }
        fcBufFree(&why);
        if (count < 2) { cfStepFail(S, "wine: which program?", NULL, 0); S->stop = true; return; }
        wine = true;
        resolved = words[1][0] == '/' || (words[1][0] && words[1][1] == ':') ? cfDup(words[1]) : cfPrintf("%s/%s", S->cwd, words[1]);
        argv[a++] = resolved;
        words++; count--;
    } else if (strncmp(cmd, "sh", cl) == 0 && cl == 2) {
        FcBuf line;
        cfBufInit(&line);
        for (i = 1; i < count; i++) { if (i > 1) fcBufChar(&line, ' '); fcBufStr(&line, words[i]); }
        shellLine = line.p;
        exe = "/bin/sh";
        argv[a++] = "/bin/sh"; argv[a++] = "-c"; argv[a++] = shellLine;
        count = 1;
    } else { cfStepFail(S, "no such step", NULL, 0); S->stop = true; return; }
    for (i = 1; i < count && a < 94; i++) argv[a++] = words[i];
    argv[a] = NULL;
    if (!timeout) timeout = R->runTimeout;
    if (S->hasLast) cfRunFree(&S->last);
    started = cfNowMs();
    if (wine) cfSpawnWine(R, argv, S->cwd, R->wine ? timeout + 60 : timeout, S->envCount ? S->env : NULL, S->input, &S->last);
    else {
        char* envEnd[66];
        for (i = 0; i < S->envCount; i++) envEnd[i] = S->env[i];
        envEnd[i] = NULL;
        cfSpawnEx(exe, argv, S->cwd, timeout, envEnd, S->input, &S->last);
    }
    S->seconds = (double)(cfNowMs() - started) / 1000.0;
    S->hasLast = true;
    free(S->input); S->input = NULL;
    (void)anyCode;
    if (S->last.timedOut || (nonzero ? S->last.code == 0 : S->last.code != want)) {
        FcBuf both;
        char* what = S->last.timedOut ? cfPrintf("did not end in %d s", timeout)
            : nonzero ? cfPrintf("ended with 0, expected another code") : cfPrintf("ended with %d, expected %d", S->last.code, want);
        cfStepBoth(S, &both);
        cfStepFail(S, what, both.len > 3000 ? both.p + both.len - 3000 : both.p, both.len > 3000 ? 3000 : both.len);
        fcBufFree(&both);
        free(what);
        S->stop = true;
    }
    free(shellLine);
    free(resolved);
}
static bool cfStepCompare(int have, const char* op) {
    int n = atoi(op + strspn(op, "<>="));
    if (strncmp(op, ">=", 2) == 0) return have >= n;
    if (strncmp(op, "<=", 2) == 0) return have <= n;
    if (op[0] == '>') return have > n;
    if (op[0] == '<') return have < n;
    return have == n;
}
static void cfStepsCase(CfRunner* R, CfJob* j) {
    CfCase* k = j->kase;
    CfSteps S;
    FcCtx ctx;
    const char* p = k->steps;
    int line = 0, skipping = 0;
    memset(&S, 0, sizeof S);
    S.R = R; S.j = j;
    fcCtxInit(&ctx);
    S.out = cfPrintf("%s/%s/%s", R->cache, k->group, k->id);
    nbDeleteTree(&ctx, S.out);
    nbMkdirs(&ctx, S.out);
    S.cwd = cfDup(k->dir);
    while (*p && !S.stop) {
        size_t n = strcspn(p, "\n");
        char* text = cfPrintf("%.*s", (int)n, p);
        char* q = text;
        char* words[96];
        int count;
        FcBuf error;
        line++;
        p += n + (p[n] == '\n');
        if (n > 0 && text[n - 1] == '\r') text[n - 1] = 0;
        while (*q == ' ' || *q == '\t') q++;
        if (!*q || *q == '#' || strncmp(q, "title:", 6) == 0 || strncmp(q, "needs:", 6) == 0) { free(text); continue; }
        /* `when <need>...` ... `end`: steps that run only where the machine meets the needs (else left out, noted in the list) */
        if (strncmp(q, "when ", 5) == 0) {
            FcBuf why;
            cfBufInit(&why);
            if (skipping == 0 && cfUnmet(R, q + 5, &why)) { skipping = 1; fcBufFmt(&j->why, "%s(step %d left out: %s)", j->why.len ? " " : "", line, why.p); }
            else if (skipping) skipping++;
            fcBufFree(&why);
            free(text);
            continue;
        }
        if (streq0(q, "end")) { if (skipping) skipping--; free(text); continue; }
        if (skipping) { free(text); continue; }
        S.line = line;
        S.text = q;
        cfBufInit(&error);
        count = cfStepWords(&S, q, words, 95, &error);
        if (count < 0) { cfStepFail(&S, error.p, NULL, 0); fcBufFree(&error); free(text); break; }
        fcBufFree(&error);
        if (count == 0) { free(text); continue; }
        {
            const char* cmd = words[0];
            size_t cl = strcspn(cmd, "!=");
            FcBuf both;
            char* joined;
            {
                FcBuf jb;
                int i;
                cfBufInit(&jb);
                for (i = 1; i < count; i++) { if (i > 1) fcBufChar(&jb, ' '); fcBufStr(&jb, words[i]); }
                joined = jb.p;
            }
            if ((cl == 5 && strncmp(cmd, "forge", 5) == 0) || (cl == 3 && strncmp(cmd, "run", 3) == 0) || (cl == 2 && strncmp(cmd, "vm", 2) == 0)
                || (cl == 4 && strncmp(cmd, "wine", 4) == 0) || (cl == 2 && strncmp(cmd, "sh", 2) == 0)) cfStepProcess(&S, cmd, words, count);
            else if (streq0(cmd, "timeout")) S.timeout = count > 1 ? atoi(words[1]) : 0;
            else if (streq0(cmd, "cd")) {
                char* d = count > 1 ? (words[1][0] == '/' ? cfDup(words[1]) : cfPrintf("%s/%s", S.cwd, words[1])) : cfDup(k->dir);
                free(S.cwd); S.cwd = d;
            }
            else if (streq0(cmd, "env") || streq0(cmd, "unenv")) {
                if (count < 2 || S.envCount >= 63) cfStepFail(&S, "env NAME=VALUE / unenv NAME", NULL, 0);
                else S.env[S.envCount++] = streq0(cmd, "env") ? cfDup(words[1]) : cfPrintf("-%s", words[1]);
            }
            else if (streq0(cmd, "input")) { free(S.input); S.input = cfDup(joined); }
            else if (!S.hasLast) cfStepFail(&S, "a check before any process", NULL, 0);
            else if (streq0(cmd, "expect") || streq0(cmd, "expect-text") || streq0(cmd, "same")) {
                char* path = streq0(cmd, "expect") ? (count > 1 ? (words[1][0] == '/' ? cfDup(words[1]) : cfPrintf("%s/%s", k->dir, words[1])) : NULL) : streq0(cmd, "same") ? cfPrintf("%s/.saved-%s", S.out, count > 1 ? words[1] : "") : NULL;
                size_t wl = 0;
                char* want = path ? cfReadText(path, &wl) : cfDup(joined);
                if (!path) wl = strlen(want);
                if (!want) cfStepFail(&S, path ? "the file is not there" : "?", path, path ? strlen(path) : 0);
                else {
                    FcBuf w;
                    cfBufInit(&w);
                    fcBufPut(&w, want, wl);
                    cfUnCr(&w);
                    if (!cfSame(w.p, w.len, S.last.out.p, S.last.out.len)) {
                        FcBuf d;
                        cfBufInit(&d);
                        cfDifference(&d, "output", w.p, w.len, S.last.out.p, S.last.out.len);
                        cfStepFail(&S, "not the expected output", NULL, 0);
                        fcBufPut(&j->detail, d.p, d.len);
                        if (S.last.err.len) { fcBufStr(&j->detail, "    errors:\n"); cfNote(&j->detail, S.last.err.p, S.last.err.len > 2000 ? 2000 : S.last.err.len); }
                        fcBufFree(&d);
                    }
                    fcBufFree(&w);
                    free(want);
                }
                free(path);
            }
            else if (streq0(cmd, "save")) {
                char* path = cfPrintf("%s/.saved-%s", S.out, count > 1 ? words[1] : "");
                FILE* f = fopen(path, "wb");
                if (f) { fwrite(S.last.out.p, 1, S.last.out.len, f); fclose(f); }
                free(path);
            }
            else if (streq0(cmd, "contains") || streq0(cmd, "lacks") || streq0(cmd, "err-contains")) {
                bool has;
                cfStepBoth(&S, &both);
                has = strstr(streq0(cmd, "err-contains") ? S.last.err.p : both.p, joined) != NULL;
                if (has != !streq0(cmd, "lacks")) cfStepFail(&S, has ? "the text is there" : "the text is not there", both.p, both.len);
                fcBufFree(&both);
            }
            else if (streq0(cmd, "count")) {
                int have = 0;
                const char* t;
                if (count < 3) cfStepFail(&S, "count <text> <op><n>", NULL, 0);
                else {
                    cfStepBoth(&S, &both);
                    for (t = both.p; *t; ) {
                        size_t ln = strcspn(t, "\n");
                        char saved = t[ln];
                        ((char*)t)[ln] = 0;
                        if (strstr(t, words[1])) have++;
                        ((char*)t)[ln] = saved;
                        t += ln + (t[ln] == '\n');
                    }
                    if (!cfStepCompare(have, words[2])) { char* what = cfPrintf("%d lines with the text", have); cfStepFail(&S, what, both.p, both.len); free(what); }
                    fcBufFree(&both);
                }
            }
            else if (streq0(cmd, "under")) {
                double limit = count > 1 ? atof(words[1]) : 0;
                if (!(S.seconds < limit)) { char* what = cfPrintf("took %.2f s", S.seconds); cfStepFail(&S, what, NULL, 0); free(what); }
            }
            else cfStepFail(&S, "no such step", NULL, 0);
            free(joined);
        }
        {
            int i;
            for (i = 0; i < count; i++) free(words[i]);
        }
        free(text);
    }
    if (S.hasLast) cfRunFree(&S.last);
    {
        int i;
        for (i = 0; i < S.envCount; i++) free(S.env[i]);
    }
    free(S.input);
    if (!R->keep && !j->bad.len) nbDeleteTree(&ctx, S.out);
    free(S.out);
    free(S.cwd);
    fcCtxFree(&ctx);
}
