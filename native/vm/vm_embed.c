/* fire VM in C: libfirevm - the VM behind native/abi/fire_embed.h (docs/spec/EMBED.md, ROADMAP_C_PORT L6, Phase D 7).
 *
 * fire.c with FIRE_VM_LIBRARY is the library (no main); in the program `fire` this file only gives vmEmbedUnhandled (nothing is recorded there). A run is
 * vmRunProgram - the same as `fire run`: load, register, run, take everything down again - with the streams of the run (g_vmOut, g_vmErr in fire.c), the
 * folders of the host's package libraries first (g_vmPkgPathFirst, vm_pkg.c) and what ended it recorded as data. The runtime (fire_rt.h) is the process's -
 * one program at a time: g_vmEmbedLock serialises the runs of all FireVm. */
#ifdef FIRE_VM_LIBRARY
#include "../abi/fire_embed.h"
#endif

/* what the running program left as data (NULL: no run of the library is active) */
typedef struct VmEmbedNote { char* type; char* message; char* trace; char* error; } VmEmbedNote;
static VmEmbedNote* g_vmEmbedNote = NULL;

static char* vmEmbedDup(const char* s) {
    size_t n = strlen(s ? s : "");
    char* d = (char*)malloc(n + 1);
    if (d) memcpy(d, s ? s : "", n + 1);
    return d;
}

/* vmUnhandledFn (vm_core.c): the exception that ends the program - its class, the text of `message` (as reportUnhandledIn reads it) and its stack trace */
static void vmEmbedUnhandled(Value e) {
    Obj* o;
    int32_t index;
    char text[2048];
    char* trace;
    if (!g_vmEmbedNote || g_vmEmbedNote->type || e.kind != K_Class) return;
    o = asObj(e);
    index = excMessageIndex(o->cls);
    text[0] = 0;
    if (index >= 0) {
        Value m = objFields(o)[index];
        OwnList scratch;
        ownListInit(&scratch, 0, NULL);
        if (m.kind != K_String) m = toStringValue(m, &scratch);
        strToUtf8(m, text, sizeof text);
    }
    g_vmEmbedNote->type = vmEmbedDup(className(o->cls));
    g_vmEmbedNote->message = vmEmbedDup(text);
    trace = fire_traceFormat(fire_traceOf(o), g_stackTrace);
    g_vmEmbedNote->trace = vmEmbedDup(trace ? trace : "");
    free(trace);
}

/* fatal (fire_rt.h): an error of the runtime ends the program */
void fire_vmNoteError(const char* message) {
    if (g_vmEmbedNote && !g_vmEmbedNote->error) g_vmEmbedNote->error = vmEmbedDup(message);
}

#ifdef FIRE_VM_LIBRARY
#if defined(_WIN32)
static SRWLOCK g_vmEmbedLock = SRWLOCK_INIT;
#define VM_EMBED_LOCK() AcquireSRWLockExclusive(&g_vmEmbedLock)
#define VM_EMBED_UNLOCK() ReleaseSRWLockExclusive(&g_vmEmbedLock)
#else
#include <pthread.h>
static pthread_mutex_t g_vmEmbedLock = PTHREAD_MUTEX_INITIALIZER;
#define VM_EMBED_LOCK() pthread_mutex_lock(&g_vmEmbedLock)
#define VM_EMBED_UNLOCK() pthread_mutex_unlock(&g_vmEmbedLock)
#endif

struct FireVm { char* pkgPath; FireWriteFn write; void* user; int passThrough; };
typedef struct VmEmbedText { char* p; size_t len, cap; } VmEmbedText;
struct FireRun { int status, exitCode; VmEmbedText out, err; char* text[6]; };

FIRE_EMBED_API int fire_embed_version(void) { return FIRE_EMBED_VERSION; }

FIRE_EMBED_API FireVm* fire_vm_new(void) { return (FireVm*)calloc(1, sizeof(FireVm)); }
FIRE_EMBED_API void fire_vm_free(FireVm* vm) { if (vm) { free(vm->pkgPath); free(vm); } }
FIRE_EMBED_API void fire_vm_set_package_path(FireVm* vm, const char* folders) {
    if (!vm) return;
    free(vm->pkgPath);
    vm->pkgPath = folders && *folders ? vmEmbedDup(folders) : NULL;
}
FIRE_EMBED_API void fire_vm_set_output(FireVm* vm, FireWriteFn write, void* user, int passThrough) {
    if (!vm) return;
    vm->write = write; vm->user = user; vm->passThrough = passThrough;
}

static void vmEmbedAppend(VmEmbedText* t, const char* data, size_t n) {
    if (t->len + n + 1 > t->cap) {
        size_t cap = t->cap ? t->cap : 256;
        char* p;
        while (cap < t->len + n + 1) cap *= 2;
        if (!(p = (char*)realloc(t->p, cap))) return;
        t->p = p; t->cap = cap;
    }
    memcpy(t->p + t->len, data, n);
    t->len += n;
    t->p[t->len] = 0;
}

/* ---- the streams of a run: every write goes to the host's callback or into the run's text ---- */
typedef struct VmEmbedSink { FireVm* vm; FireRun* run; int stream; } VmEmbedSink;
static void vmEmbedWrite(VmEmbedSink* s, const char* data, size_t n) {
    if (n == 0) return;
    if (s->vm->write) s->vm->write(s->vm->user, s->stream, data, n);
    else vmEmbedAppend(s->stream == 1 ? &s->run->out : &s->run->err, data, n);
}
#if defined(__linux__)
static ssize_t vmEmbedCookieWrite(void* cookie, const char* data, size_t n) { vmEmbedWrite((VmEmbedSink*)cookie, data, n); return (ssize_t)n; }
static FILE* vmEmbedOpen(VmEmbedSink* s) {
    cookie_io_functions_t io;
    FILE* f;
    memset(&io, 0, sizeof io);
    io.write = vmEmbedCookieWrite;
    f = fopencookie(s, "w", io);
    if (f) setvbuf(f, NULL, _IOLBF, 0);
    return f;
}
static void vmEmbedClose(FILE* f, VmEmbedSink* s) { (void)s; if (f) fclose(f); }
#elif defined(__APPLE__) || defined(__FreeBSD__) || defined(__NetBSD__) || defined(__OpenBSD__)
static int vmEmbedFunWrite(void* cookie, const char* data, int n) { vmEmbedWrite((VmEmbedSink*)cookie, data, (size_t)n); return n; }
static FILE* vmEmbedOpen(VmEmbedSink* s) {
    FILE* f = funopen(s, NULL, vmEmbedFunWrite, NULL, NULL);
    if (f) setvbuf(f, NULL, _IOLBF, 0);
    return f;
}
static void vmEmbedClose(FILE* f, VmEmbedSink* s) { (void)s; if (f) fclose(f); }
#else
/* (no stream with callbacks: a temporary file, handed over when the program has ended) */
static FILE* vmEmbedOpen(VmEmbedSink* s) { (void)s; return tmpfile(); }
static void vmEmbedClose(FILE* f, VmEmbedSink* s) {
    char buf[4096];
    size_t n;
    if (!f) return;
    fflush(f);
    rewind(f);
    while ((n = fread(buf, 1, sizeof buf, f)) > 0) vmEmbedWrite(s, buf, n);
    fclose(f);
}
#endif

FIRE_EMBED_API FireRun* fire_vm_run(FireVm* vm, const char* name, const uint8_t* program, size_t length, int argc, const char* const* argv) {
    FireRun* r = (FireRun*)calloc(1, sizeof(FireRun));
    VmEmbedNote note;
    VmEmbedSink outSink, errSink;
    FILE* out = NULL;
    FILE* err = NULL;
    uint8_t* bytes;
    char** args;
    int i, code;
    if (!r) return NULL;
    if (!vm || !program) { r->status = FIRE_RUN_NOT_LOADED; r->exitCode = 2; r->text[FIRE_RUN_LOAD_ERROR] = vmEmbedDup(!vm ? "no VM (fire_vm_new)" : "no program"); return r; }
    if (!name || !*name) name = "program.fbc";
    bytes = (uint8_t*)malloc(length ? length : 1);
    args = (char**)calloc((size_t)(argc > 0 ? argc : 0) + 2, sizeof(char*));
    if (!bytes || !args) { free(bytes); free(args); r->status = FIRE_RUN_NOT_LOADED; r->exitCode = 2; return r; }
    if (length) memcpy(bytes, program, length);
    /* (Environment.Args: the runtime leaves out the first - the program) */
    args[0] = vmEmbedDup(name);
    for (i = 0; i < argc; i++) args[1 + i] = vmEmbedDup(argv && argv[i] ? argv[i] : "");
    memset(&note, 0, sizeof note);
    VM_EMBED_LOCK();
    if (!vm->passThrough) {
        outSink.vm = vm; outSink.run = r; outSink.stream = 1;
        errSink.vm = vm; errSink.run = r; errSink.stream = 2;
        out = vmEmbedOpen(&outSink);
        err = vmEmbedOpen(&errSink);
        g_vmOut = out;
        g_vmErr = err;
    }
    g_vmPkgPathFirst = vm->pkgPath;
    g_vmEmbedNote = &note;
    fire_setArgs(1 + (argc > 0 ? argc : 0), args);
    code = vmRunProgram(name, name, bytes, length, false);   /* (frees bytes) */
    fflush(stdout);
    fflush(stderr);
    g_vmEmbedNote = NULL;
    g_vmPkgPathFirst = NULL;
    fire_setArgs(0, NULL);
    g_vmOut = NULL;
    g_vmErr = NULL;
    if (out) vmEmbedClose(out, &outSink);
    if (err) vmEmbedClose(err, &errSink);
    VM_EMBED_UNLOCK();
    r->exitCode = code;
    if (!g_vmOutcome.loaded) {
        r->status = FIRE_RUN_NOT_LOADED;
        /* (the line vmRunProgram wrote: "fire: <name>: <why>") */
        r->text[FIRE_RUN_LOAD_ERROR] = vmEmbedDup(r->err.p ? r->err.p : "the program could not be loaded");
    } else if (g_vmOutcome.terminated) r->status = FIRE_RUN_TERMINATED;
    else if (g_vmOutcome.failed == 1 && note.type) r->status = FIRE_RUN_EXCEPTION;
    else if (g_vmOutcome.failed) r->status = FIRE_RUN_ERROR;
    else r->status = FIRE_RUN_ENDED;
    r->text[FIRE_RUN_EXCEPTION_TYPE] = note.type;
    r->text[FIRE_RUN_EXCEPTION_MESSAGE] = note.message ? note.message : note.error;
    if (note.message) free(note.error);
    r->text[FIRE_RUN_STACK_TRACE] = note.trace;
    for (i = 0; i <= argc; i++) free(args[i]);
    free(args);
    return r;
}

FIRE_EMBED_API int fire_run_status(const FireRun* r) { return r ? r->status : FIRE_RUN_NOT_LOADED; }
FIRE_EMBED_API int fire_run_exit_code(const FireRun* r) { return r ? r->exitCode : 2; }
FIRE_EMBED_API const char* fire_run_text(const FireRun* r, int what) {
    const char* t;
    if (!r || what < 0 || what > 5) return "";
    if (what == FIRE_RUN_OUTPUT) return r->out.p ? r->out.p : "";
    if (what == FIRE_RUN_ERRORS) return r->err.p ? r->err.p : "";
    t = r->text[what];
    return t ? t : "";
}
FIRE_EMBED_API void fire_run_free(FireRun* r) {
    int i;
    if (!r) return;
    for (i = 0; i < 6; i++) free(r->text[i]);
    free(r->out.p);
    free(r->err.p);
    free(r);
}
#endif
