/* native/forge/embed.c - libforge: the compiler behind native/abi/fire_embed.h (docs/spec/EMBED.md, ROADMAP_C_PORT L6, Phase D 7).
 *
 * forge.c with FIRE_FORGE_LIBRARY is the library (no main); without it this file is in forge-c as well (main is a thin program over fcCliMain). A
 * translation is what `forge run` does before it starts the VM (tools/commands.c tcRun): the plan of the file or the project (tcFilesPlan, pjWorkspaceOpen),
 * the link for the VM (tcLinkVm), the libraries of its packages (nvEnsureLibraries), the program file (fcProgramFileWrite) - each in a context of its own
 * (FcCtx: no global state of the frontend). What the builder keeps for the process (the folders nbNativeRoot, nbBaseDirectory, nvStoreRoot - set by the
 * command line once) is set from the FireForge under feLock for the time of a translation: translations run one at a time in the process. */
#include "../abi/fire_embed.h"
#if !defined(_WIN32) && defined(FIRE_FORGE_LIBRARY)
#include <dlfcn.h>
#if !defined(__APPLE__) && !defined(__USE_GNU)
/* (glibc declares dladdr only with _GNU_SOURCE, which forge does not define: its declaration with the layout of Dl_info) */
typedef struct FeDlInfo { const char* dli_fname; void* dli_fbase; const char* dli_sname; void* dli_saddr; } FeDlInfo;
extern int dladdr(const void* address, FeDlInfo* info);
#define Dl_info FeDlInfo
#endif
#endif

struct FireForge { char* prelude; char* store; char* native; char* base; FireWriteFn write; void* user; };
typedef struct FeDiag { char* text[5]; int line, column; } FeDiag;
struct FireCompilation { int ok; uint8_t* program; size_t length; char* pkgPath; FeDiag* diags; int ndiags, cap; };

static pthread_mutex_t feLock = PTHREAD_MUTEX_INITIALIZER;

FIRE_EMBED_API int fire_embed_version(void) { return FIRE_EMBED_VERSION; }

static char* feDup(const char* s) { size_t n = strlen(s ? s : ""); char* d = (char*)malloc(n + 1); if (d) memcpy(d, s ? s : "", n + 1); return d; }
static bool feIsDir(const char* p) { struct stat st; return p && stat(p, &st) == 0 && S_ISDIR(st.st_mode); }

/* the folder of this library (or of the program that contains it) */
static char* feSelfFolder(void) {
    char buf[4096];
    char* slash;
#ifdef _WIN32
    HMODULE m = NULL;
    wchar_t w[4096];
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)(void*)&fire_embed_version, &m)) return NULL;
    if (!GetModuleFileNameW(m, w, 4096) || !WideCharToMultiByte(CP_UTF8, 0, w, -1, buf, (int)sizeof buf, NULL, NULL)) return NULL;
    for (slash = buf; *slash; slash++) if (*slash == '\\') *slash = '/';
#elif defined(FIRE_FORGE_LIBRARY)
    Dl_info info;
    if (!dladdr((void*)&fire_embed_version, &info) || !info.dli_fname || !realpath(info.dli_fname, buf)) return NULL;
#else
    /* (in the program forge-c: its folder, which main found) */
    snprintf(buf, sizeof buf, "%s/x", nbBaseDirectory);
#endif
    if ((slash = strrchr(buf, '/')) == NULL) return NULL;
    *slash = 0;
    return feDup(buf);
}

FIRE_EMBED_API FireForge* fire_forge_new(const char* tables, const char* packages, const char* native) {
    FireForge* f = (FireForge*)calloc(1, sizeof *f);
    char buf[4200], tablesBuf[4200], real[4096], file[4400];
    const char* e;
    (void)fcFindPrelude; (void)fcFindStore; (void)neEnsureFor;   /* (what main of forge-c finds from its own path, and sets for its programs: the library takes the folders below) */
    if (!f) return NULL;
    f->base = feSelfFolder();
    if (!tables && (e = getenv("FIRE_TABLES")) != NULL && *e) tables = e;
    if (!tables && f->base) { snprintf(tablesBuf, sizeof tablesBuf, "%s/../tables", f->base); if (feIsDir(tablesBuf)) tables = tablesBuf; }
    /* (a distribution, `package-dist`: native/ next to forge and the libraries) */
    if (!tables && f->base) { snprintf(tablesBuf, sizeof tablesBuf, "%s/native/tables", f->base); if (feIsDir(tablesBuf)) tables = tablesBuf; }
    if (!tables && feIsDir("native/tables")) tables = "native/tables";
    snprintf(file, sizeof file, "%s/prelude.fire", tables ? tables : ".");
    f->prelude = feDup(realpath(file, real) ? real : file);
    if (!packages && (e = getenv("FIRE_PACKAGES")) != NULL && *e) packages = e;
    if (packages) f->store = feDup(packages);
    else { snprintf(buf, sizeof buf, "%s/Packages", f->base ? f->base : "."); f->store = feDup(buf); }
    if (!native && (e = getenv("FIRE_NATIVE_ROOT")) != NULL && *e) native = e;
    if (native) f->native = feDup(realpath(native, real) ? real : native);
    else {
        /* (native/ is the folder above tables/) */
        snprintf(buf, sizeof buf, "%s", f->prelude);
        if (strrchr(buf, '/')) *strrchr(buf, '/') = 0;
        strncat(buf, "/..", sizeof buf - strlen(buf) - 1);
        f->native = feDup(realpath(buf, real) ? real : buf);
    }
    if (!f->base) f->base = feDup(".");
    if (!f->prelude || !f->store || !f->native || !f->base) { fire_forge_free(f); return NULL; }
    return f;
}

FIRE_EMBED_API void fire_forge_free(FireForge* f) {
    if (!f) return;
    free(f->prelude); free(f->store); free(f->native); free(f->base);
    free(f);
}

/* the folders of the builder for this FireForge (under feLock) */
static void feSettings(FireForge* f) {
    nbNativeRoot = f->native;
    nbBaseDirectory = f->base;
    nvStoreRoot = f->store;
}

static void feAddDiag(FireCompilation* r, FcCtx* c, const FcDiagnostic* d) {
    FeDiag* x;
    FcBuf b = {0};
    if (r->ndiags == r->cap) {
        FeDiag* n = (FeDiag*)realloc(r->diags, sizeof(FeDiag) * (size_t)(r->cap ? r->cap * 2 : 8));
        if (!n) return;
        r->diags = n;
        r->cap = r->cap ? r->cap * 2 : 8;
    }
    x = &r->diags[r->ndiags++];
    memset(x, 0, sizeof *x);
    x->text[FIRE_DIAG_SEVERITY] = feDup(d->severity == FC_SEV_ERROR ? "error" : "warning");
    x->text[FIRE_DIAG_CODE] = feDup(d->code ? d->code : "");
    fcBufStr(&b, ""); if (d->file) fcOut8(&b, d->file); x->text[FIRE_DIAG_FILE] = feDup(b.p); b.len = 0; b.p[0] = 0;
    fcOut8(&b, d->message ? d->message : ""); x->text[FIRE_DIAG_MESSAGE] = feDup(b.p); b.len = 0; b.p[0] = 0;
    fcDiagLogLine(&b, d);
    while (b.len > 0 && (b.p[b.len - 1] == '\n' || b.p[b.len - 1] == '\r')) b.p[--b.len] = 0;
    x->text[FIRE_DIAG_TEXT] = feDup(b.p);
    fcBufFree(&b);
    x->line = d->line;
    x->column = d->column;
    (void)c;
}

FIRE_EMBED_API FireCompilation* fire_forge_compile(FireForge* f, const char* path, const char* source, size_t length, int mode) {
    FireCompilation* r = (FireCompilation*)calloc(1, sizeof *r);
    FcCtx step;
    FcBuf fbc = {0};
    volatile bool ok = false;
    int i;
    if (!r) return NULL;
    if (!f || !path) {
        FcDiagnostic d;
        memset(&d, 0, sizeof d);
        d.severity = FC_SEV_ERROR; d.code = ""; d.message = !f ? "no compiler (fire_forge_new)" : "no file named";
        feAddDiag(r, NULL, &d);
        return r;
    }
    pthread_mutex_lock(&feLock);
    feSettings(f);
    fcCtxInit(&step);
    step.collecting = true;
    FC_TRY(&step) {
        TcOptions o;
        PjPlan* plan;
        FcStore* store = NULL;
        FcNatives* registry = NULL;
        FcLinked* linked;
        FcList* libraries;
        FcList* dirs = fcListNew(&step);
        memset(&o, 0, sizeof o);
        o.files = fcListNew(&step); o.defines = fcListNew(&step); o.overlays = fcListNew(&step);
        o.mode = mode >= 0 && mode <= 2 ? mode : -1; o.types = -1; o.globalJobs = -1;
        o.stripping = o.optimize = o.quick = o.split = -1;
        if (!source && pjIsProjectFile(path)) {
            PjWorkspace* w;
            if (!nbFileExists(path)) fcThrow(&step, FC_ERR_PROJECT, 0, 0, NULL, fcFmt(&step, "File not found: %s", path));
            w = pjWorkspaceOpen(&step, path);
            plan = pjPlanOrThrow(w, pjSelectProject(w, NULL));
            if (plan->project->project->type == PJ_LIBRARY) fcThrow(&step, FC_ERR_PROJECT, 0, 0, NULL, fcFmt(&step, "'%s' is a library: it has no entry point to run.", plan->project->project->name));
        } else {
            FcList* texts = fcListNew(&step);
            FcU16 t = {0};
            if (source) fcDecodeText((const uint8_t*)source, length, &t);
            else if (!nbFileExists(path) || !fcReadText(path, &t)) fcThrow(&step, FC_ERR_PROJECT, 0, 0, NULL, fcFmt(&step, "File not found: %s", path));
            fcListAdd(&step, o.files, (void*)path);
            fcListAdd(&step, texts, (void*)fc8FromU16(&step, t.p, t.len));
            fcU16Free(&t);
            plan = tcFilesPlan(&step, &o, o.files, texts, 1, NULL);
        }
        linked = tcLinkVm(&step, &o, plan, f->prelude, f->store, &store, &registry);
        libraries = nvEnsureLibraries(&step, linked, store, registry, NULL);
        for (i = 0; libraries && i < libraries->count; i++) {
            const char* d = fcDirectoryName(&step, fcFullPath(&step, ".", (const char*)libraries->items[i]));
            if (!fcListHasStr(dirs, d)) fcListAdd(&step, dirs, (void*)d);
        }
#ifdef _WIN32
        r->pkgPath = feDup(fgJoin(&step, dirs, ";"));
#else
        r->pkgPath = feDup(fgJoin(&step, dirs, ":"));
#endif
        fcBufStr(&fbc, "");
        fcProgramFileWrite(&step, linked, true, &fbc);
        ok = true;
    } FC_CATCH(&step) {
        ok = false;
    } FC_END_TRY;
    /* the warnings as they came, then the errors of a failed translation (what `forge bytecode` writes to stderr) */
    for (i = 0; i < step.diags->count; i++) feAddDiag(r, &step, (const FcDiagnostic*)step.diags->items[i]);
    if (!ok) {
        FcDiagnostic d;
        if (pjIsCompileErrorKind(step.err.kind)) {
            volatile FcList* diags = NULL;
            FC_TRY(&step) { diags = fcDiagnosticsOf(&step, &step.err, NULL); } FC_CATCH(&step) { diags = NULL; } FC_END_TRY;
            if (diags) for (i = 0; i < ((FcList*)diags)->count; i++) feAddDiag(r, &step, (const FcDiagnostic*)((FcList*)diags)->items[i]);
        } else {
            /* (a project that cannot be read, a package that cannot be built, an error inside forge: one error without a code) */
            const char* m = step.err.message ? step.err.message : "the translation failed";
            memset(&d, 0, sizeof d);
            d.severity = FC_SEV_ERROR; d.code = "";
            if (step.err.kind == FC_ERR_INTERNAL && fcStartsWith(m, "unported: ")) m += 10;
            else if (step.err.kind == FC_ERR_INTERNAL && fcStartsWith(m, "vm: ")) m += 4;
            else if (step.err.kind == FC_ERR_INTERNAL) m = fcFmt(&step, "internal: %s", m);
            d.message = m;
            feAddDiag(r, &step, &d);
        }
        free(r->pkgPath);
        r->pkgPath = NULL;
    } else {
        r->ok = 1;
        r->program = (uint8_t*)malloc(fbc.len ? fbc.len : 1);
        if (r->program) { if (fbc.len) memcpy(r->program, fbc.p, fbc.len); r->length = fbc.len; }
        else r->ok = 0;
    }
    fcBufFree(&fbc);
    fcCtxFree(&step);
    pthread_mutex_unlock(&feLock);
    return r;
}

FIRE_EMBED_API int fire_compilation_ok(const FireCompilation* c) { return c && c->ok; }
FIRE_EMBED_API const uint8_t* fire_compilation_program(const FireCompilation* c, size_t* length) {
    if (length) *length = c && c->ok ? c->length : 0;
    return c && c->ok ? c->program : NULL;
}
FIRE_EMBED_API const char* fire_compilation_package_path(const FireCompilation* c) { return c && c->pkgPath ? c->pkgPath : ""; }
FIRE_EMBED_API int fire_compilation_diagnostic_count(const FireCompilation* c) { return c ? c->ndiags : 0; }
FIRE_EMBED_API const char* fire_compilation_diagnostic(const FireCompilation* c, int index, int what) {
    if (!c || index < 0 || index >= c->ndiags || what < 0 || what > 4) return "";
    return c->diags[index].text[what] ? c->diags[index].text[what] : "";
}
FIRE_EMBED_API int fire_compilation_diagnostic_line(const FireCompilation* c, int index) { return c && index >= 0 && index < c->ndiags ? c->diags[index].line : 0; }
FIRE_EMBED_API int fire_compilation_diagnostic_column(const FireCompilation* c, int index) { return c && index >= 0 && index < c->ndiags ? c->diags[index].column : 0; }
FIRE_EMBED_API void fire_compilation_free(FireCompilation* c) {
    int i, k;
    if (!c) return;
    for (i = 0; i < c->ndiags; i++) for (k = 0; k < 5; k++) free(c->diags[i].text[k]);
    free(c->diags);
    free(c->program);
    free(c->pkgPath);
    free(c);
}

FIRE_EMBED_API void fire_forge_set_output(FireForge* f, FireWriteFn write, void* user) {
    if (!f) return;
    f->write = write;
    f->user = user;
}

/* ---- the streams of fire_forge_main (libforge): stdout and stderr of the command line go to the host's callback (forge.c: g_feOut, g_feErr) ---- */
#ifdef FIRE_FORGE_LIBRARY
typedef struct FeSink { FireForge* f; int stream; } FeSink;
static void feSinkWrite(const FeSink* s, const char* data, size_t n) { if (n) s->f->write(s->f->user, s->stream, data, n); }
#if defined(__linux__)
/* (fopencookie: glibc declares it by default since 2.28, before only with _GNU_SOURCE - which forge does not define -, musl only with it: else its
 * declaration with the layout of cookie_io_functions_t) */
#if !defined(__USE_GNU) && !(defined(__GLIBC__) && defined(__USE_MISC) && (__GLIBC__ > 2 || __GLIBC_MINOR__ >= 28))
typedef struct FeCookieIo { void* read; ssize_t (*write)(void*, const char*, size_t); void* seek; void* close; } FeCookieIo;
extern FILE* fopencookie(void* cookie, const char* mode, FeCookieIo io);
#else
typedef cookie_io_functions_t FeCookieIo;
#endif
static ssize_t feCookieWrite(void* cookie, const char* data, size_t n) { feSinkWrite((const FeSink*)cookie, data, n); return (ssize_t)n; }
static FILE* feSinkOpen(FeSink* s) {
    FeCookieIo io;
    FILE* f;
    memset(&io, 0, sizeof io);
    io.write = feCookieWrite;
    f = fopencookie(s, "w", io);
    if (f) setvbuf(f, NULL, _IOLBF, 0);
    return f;
}
static void feSinkClose(FILE* f, const FeSink* s) { (void)s; if (f) fclose(f); }
#elif defined(__APPLE__) || defined(__FreeBSD__) || defined(__NetBSD__) || defined(__OpenBSD__)
static int feFunWrite(void* cookie, const char* data, int n) { feSinkWrite((const FeSink*)cookie, data, (size_t)n); return n; }
static FILE* feSinkOpen(FeSink* s) {
    FILE* f = funopen(s, NULL, feFunWrite, NULL, NULL);
    if (f) setvbuf(f, NULL, _IOLBF, 0);
    return f;
}
static void feSinkClose(FILE* f, const FeSink* s) { (void)s; if (f) fclose(f); }
#else
/* (no stream with callbacks: a temporary file, handed over when the command has ended) */
static FILE* feSinkOpen(FeSink* s) { (void)s; return tmpfile(); }
static void feSinkClose(FILE* f, const FeSink* s) {
    char buf[4096];
    size_t n;
    if (!f) return;
    fflush(f);
    rewind(f);
    while ((n = fread(buf, 1, sizeof buf, f)) > 0) feSinkWrite(s, buf, n);
    fclose(f);
}
#endif
#endif

FIRE_EMBED_API int fire_forge_main(FireForge* f, int argc, char** argv) {
    int r;
#ifdef _WIN32
    int outMode, errMode;
#endif
#ifdef FIRE_FORGE_LIBRARY
    FeSink outSink, errSink;
    FILE* out = NULL;
    FILE* err = NULL;
#endif
    if (!f) return 2;
    pthread_mutex_lock(&feLock);
#ifdef FIRE_FORGE_LIBRARY
    if (f->write) {
        outSink.f = f; outSink.stream = 1;
        errSink.f = f; errSink.stream = 2;
        fflush(feProcessStdout());
        fflush(feProcessStderr());
        out = feSinkOpen(&outSink);
        err = feSinkOpen(&errSink);
        g_feOut = out;
        g_feErr = err;
    }
#endif
#ifdef _WIN32
    /* (the output byte for byte as forge-c writes it - no CR before LF -, the modes of the process's streams back afterwards) */
    fflush(stdout);
    fflush(stderr);
    outMode = _setmode(_fileno(stdout), _O_BINARY);
    errMode = _setmode(_fileno(stderr), _O_BINARY);
#endif
    if (argc >= 2 && strcmp(argv[1], "serve") == 0) { fputs("forge: serve runs only as a program of its own (forge-c serve)\n", stderr); r = 2; }
    else {
        feSettings(f);
        r = fcCliMain(argc, argv, f->prelude, f->store);
    }
    fflush(stdout);
    fflush(stderr);
#ifdef _WIN32
    if (outMode != -1) _setmode(_fileno(stdout), outMode);
    if (errMode != -1) _setmode(_fileno(stderr), errMode);
#endif
#ifdef FIRE_FORGE_LIBRARY
    g_feOut = NULL;
    g_feErr = NULL;
    if (out) feSinkClose(out, &outSink);
    if (err) feSinkClose(err, &errSink);
#endif
    pthread_mutex_unlock(&feLock);
    return r;
}
