/* fire - the VM in C (docs/ROADMAP_C_PORT.md phase B; its structure and its command line: docs/spec/VM_C.md).
 *
 *   cc -O2 native/vm/fire.c -o fire -lm -lpthread -ldl   (gcc, clang, tcc; Windows: -lws2_32; one file that includes everything - no build system)
 *   fire run [--stats] [--debug <spec>] <program.fbc> [--] [<arguments>]   runs a program (written by `forge bytecode`); what follows it is the program's
 *   fire [--stats] [--debug <spec>] a.fbc [b.fbc ...] [-- <arguments>]      runs the programs one after the other in this process
 *   fire help | fire version
 *
 * Every program is loaded, its classes are registered with the runtime, it runs, and then everything it had is gone again - its objects, strings, scopes,
 * classes - before the next one starts; one that ends with an exception nothing catches (or with an error of the runtime) ends only itself (L6). --stats
 * prints after each one what is still alive in the runtime (handles of objects/arrays, the temporary pool, strings). Exit code: that of the last program
 * that failed (1 for an exception nothing caught, the code of `terminate(code)`), 2 when a program cannot be read or loaded or the command line is wrong,
 * else 0.
 *
 * A packed program (`forge build --engine vm`, phase B step 8) is this executable with a payload behind it: the program file and the libraries of its
 * packages (see "The payload" below). Every argument is then the program's; the libraries are written once into a cache folder of the user. */
#define FIRE_VM 1
#define FIRE_THREADS 1             /* fire threads, tasks, actors (phase B step 3): the thread model of the runtime with the GIL - one system thread per */
#define FIRE_GIL 1                 /* fire thread, the safe points of the interpreter are those of the generated code (vm_threads.c) */
#define FIRE_EXCEPTIONS 1
#define FIRE_DEBUG 1               /* the debugger (vm_debug.c): the agent of the runtime, armed only with FIRE_DEBUG / --debug */
#define FIRE_DEBUG_AGENT 1
#define FIRE_REFLECTION 1         /* (the runtime's probes and the selector of a lambda: the reflection of the VM, vm_reflect.c) */
#define FIRE_PTRBOUNDS 1          /* (a pointer into an array or a buffer is checked when it is used, like a native build that is not #performance) */
#define FIRE_NDIMS 16              /* (a program's dimensions are a constant of the generated code; the VM takes room for 16) */
#ifdef FIRE_VM_STATS
#define FIRE_HEAP_STATS 1          /* (-DFIRE_VM_STATS: counts the strings that live - the check that a script leaves nothing behind) */
#endif
#define FIRE_RT_IMPLEMENTATION 1
#define FIRE_ARGS_UTF8 1           /* (Windows: main hands the runtime UTF-8 arguments it split from the wide command line - fire_platform_win32.h) */
#ifdef FIRE_VM_LIBRARY
/* libfirevm (fire_embed.h, vm_embed.c): this file without main. What the program writes to stdout and stderr goes to the streams of the run (g_vmOut,
 * g_vmErr - the host's callback or a buffer), so every `stdout`/`stderr` of the VM and the runtime names them (NULL: the process's). */
#if defined(__linux__) && !defined(_GNU_SOURCE)
#define _GNU_SOURCE 1              /* (fopencookie: a stream that calls the host) */
#endif
#include <stdio.h>
static FILE* g_vmOut = NULL;
static FILE* g_vmErr = NULL;
static FILE* vmProcessStdout(void) { return stdout; }
static FILE* vmProcessStderr(void) { return stderr; }
#undef stdout
#undef stderr
#define stdout (g_vmOut ? g_vmOut : vmProcessStdout())
#define stderr (g_vmErr ? g_vmErr : vmProcessStderr())
#endif
/* (the platform layer of this machine, from the repository: a native build copies it next to the runtime) */
#if defined(_WIN32)
#define FIRE_PLATFORM_HEADER "../platform/windows/fire_platform.h"
#else
#define FIRE_PLATFORM_HEADER "../platform/posix/fire_platform.h"
#endif
#include "../runtime/fire_rt.h"
#include <setjmp.h>
#if defined(_WIN32)
/* Windows: the paths of the VM are UTF-8 (its arguments, fire_plat_programPath, FIRE_PKG_PATH's folders) - opened with the wide functions of the system,
 * not in the ANSI code page. */
static FILE* vmFopen(const char* path, const char* mode) {
    wchar_t* w = fire_plat_toWide(path);
    wchar_t m[8];
    size_t i;
    FILE* f;
    for (i = 0; mode[i] && i < 7; i++) m[i] = (wchar_t)mode[i];
    m[i] = 0;
    f = w ? _wfopen(w, m) : NULL;
    free(w);
    return f;
}
#define FB_FOPEN vmFopen
/* A variable of the environment as UTF-8 into `buf` (NULL: not set) - getenv gives the ANSI code page (a folder of the user with other letters). */
static const char* vmGetenv(const char* name, char* buf, size_t cap) { return fire_plat_getenv(name, buf, cap) >= 0 ? buf : NULL; }
#else
#define vmFopen fopen
static const char* vmGetenv(const char* name, char* buf, size_t cap) { (void)buf; (void)cap; return getenv(name); }
#endif
#define FIRE_BASE_IMPLEMENTATION 1
#include "../base/fire_base.h"

void fbOutOfMemory(void) { outOfMemory(); }

#include "vm.h"
static void vmEmbedUnhandled(Value e);   /* (vm_embed.c: an exception nothing caught, as data for the host of libfirevm) */
#include "vm_load.c"
#include "vm_core.c"
#include "vm_pkg.c"
#include "vm_lib.c"
#include "vm_natives.c"
#include "vm_threads.c"
#include "vm_run.c"
#include "vm_dangle.c"
#include "vm_reflect.c"
#include "vm_json.c"
#include "vm_debug.c"
#include "vm_devhost.c"
#include "vm_trace.c"
#include "vm_plugin.c"

void fire_vmExit(int code) {
    Vm* vm = g_vmCur;
    if (vm && vm->exitJump) {
        /* (the handlers go now, while they are still there: some live on the stack of the runtime's functions the jump leaves - a job's, a reply's) */
        Handler* h = g_handlers;
        while (h) { Handler* p = h->prev; if (h->fn == vmCatchFn) free(h); h = p; }
        g_handlers = NULL;
        vm->exitCode = code;
        longjmp(*vm->exitJump, 1);
    }
    fire_plat_exit(code);
}

static uint32_t vmLiveHandles(void) { return g_slots.count - g_slots.nfree; }

/* The widths this VM is built for (fire_rt.h: FIRE_INT32/FIRE_INT16, FIRE_FLOAT32): a program of `#intwidth`/`#floatwidth` needs the VM of its widths -
 * a variant built with those defines (`fire-i32`, `fire-f32`, `fire-i32-f32`, ..., VM_C.md "Das Programm fire"). */
#if defined(FIRE_INT16)
#define VM_INT_WIDTH 16
#elif defined(FIRE_INT32)
#define VM_INT_WIDTH 32
#else
#define VM_INT_WIDTH 64
#endif
#ifdef FIRE_FLOAT32
#define VM_FLOAT_WIDTH 32
#else
#define VM_FLOAT_WIDTH 64
#endif

#if defined(_WIN32)
#include <direct.h>
#include <process.h>
static int vmMkdir(const char* p) { wchar_t* w = fire_plat_toWide(p); int r = w ? _wmkdir(w) : -1; free(w); return r; }
static int vmRemove(const char* p) { wchar_t* w = fire_plat_toWide(p); int r = w ? _wremove(w) : -1; free(w); return r; }
static bool vmReplaceFile(const char* from, const char* to) {
    wchar_t* a = fire_plat_toWide(from);
    wchar_t* b = fire_plat_toWide(to);
    bool ok = a && b && MoveFileExW(a, b, MOVEFILE_REPLACE_EXISTING);
    free(a); free(b);
    return ok;
}
#define vmGetPid() _getpid()
#define VM_SEP '\\'
#else
#define vmRemove(p) remove(p)
#include <sys/stat.h>
#include <unistd.h>
#define vmMkdir(p) mkdir(p, 0700)
#define vmGetPid() getpid()
#define VM_SEP '/'
#endif
/* `fire run x.fbc` or `fire x.fbc` (one program, not packed): a program of other widths is run by the variant of its widths next to this executable - this
 * process becomes it (Windows: waits for it and ends with its code). Returns when there is none. */
static const char* g_vmSelf = NULL;
static char** g_vmArgv = NULL;
static void vmExecVariant(const char* variant) {
    char path[4200];
    const char* slash;
    FILE* probe;
    if (!g_vmSelf || !g_vmArgv) return;
    slash = strrchr(g_vmSelf, '/');
#if defined(_WIN32)
    { const char* back = strrchr(g_vmSelf, '\\'); if (back && (!slash || back > slash)) slash = back; }
    snprintf(path, sizeof path, "%.*s%s.exe", slash ? (int)(slash - g_vmSelf + 1) : 0, g_vmSelf, variant);
#else
    snprintf(path, sizeof path, "%.*s%s", slash ? (int)(slash - g_vmSelf + 1) : 0, g_vmSelf, variant);
#endif
    if (strcmp(path, g_vmSelf) == 0 || !(probe = vmFopen(path, "rb"))) return;
    fclose(probe);
    fflush(stdout); fflush(stderr);
#if defined(_WIN32)
    {
        /* (the variant gets this process's command line as it is - its arguments untouched, also quotes and other letters; only the program before them is
         * the variant; its standard handles are these; this process waits and ends with its code) */
        const wchar_t* line = GetCommandLineW();
        const wchar_t* rest = line;
        int quoted = 0;
        wchar_t* exe = fire_plat_toWide(path);
        wchar_t* cmd;
        size_t n;
        STARTUPINFOW si;
        PROCESS_INFORMATION pi;
        DWORD code = 1;
        while (*rest == L' ' || *rest == L'\t') rest++;
        while (*rest && (quoted || (*rest != L' ' && *rest != L'\t'))) { if (*rest == L'"') quoted = !quoted; rest++; }
        if (!exe) return;
        n = wcslen(exe) + wcslen(rest) + 4;
        cmd = (wchar_t*)malloc(sizeof(wchar_t) * n);
        if (!cmd) { free(exe); return; }
        _snwprintf(cmd, n, L"\"%ls\"%ls", exe, rest);
        cmd[n - 1] = 0;
        memset(&si, 0, sizeof si);
        si.cb = sizeof si;
        if (CreateProcessW(exe, cmd, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi)) {
            WaitForSingleObject(pi.hProcess, INFINITE);
            GetExitCodeProcess(pi.hProcess, &code);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            free(cmd); free(exe);
            exit((int)code);
        }
        free(cmd); free(exe);
    }
#else
    g_vmArgv[0] = path;
    execv(path, g_vmArgv);
#endif
}

static bool g_vmTime = false;   /* --time */
/* how the last vmRunProgram ended (for vm_embed.c): loaded, `terminate`, failed (1 an exception nothing caught, 2 an error of the runtime) */
static struct { bool loaded, terminated; int failed; } g_vmOutcome;

/* Runs one program (`bytes`, malloc'd - freed here; `path` names it in the messages; the libraries of its packages are looked up next to `pkgPath`); 0 when it
 * ended normally, 1 with an exception nothing caught or an error of the runtime (or the code of `terminate`), 2 when it could not be loaded. */
static int vmRunProgram(const char* path, const char* pkgPath, uint8_t* bytes, size_t len, bool stats) {
    char error[300];
    VmProgram* p;
    Vm* vm;
    jmp_buf jb;
    uint32_t i;
    int result;
    ResEntry* res = NULL;
    int64_t t0 = fire_plat_unixMicros(), t1, t2;
    memset(&g_vmOutcome, 0, sizeof g_vmOutcome);
    p = vmLoad(bytes, len, error, sizeof error);
    if (!p) { free(bytes); fprintf(stderr, "fire: %s: %s\n", path, error); return 2; }
    if (p->intWidth != VM_INT_WIDTH || p->floatWidth != VM_FLOAT_WIDTH) {
        char variant[80];
        snprintf(variant, sizeof variant, "fire%s%s", p->intWidth == 64 ? "" : p->intWidth == 32 ? "-i32" : "-i16", p->floatWidth == 64 ? "" : "-f32");
        vmExecVariant(variant);   /* (one program on the command line: the variant next to this fire runs it, with the same arguments) */
        fprintf(stderr, "fire: %s: the program is built for int %u and float %u (#intwidth, #floatwidth), this VM for int %d and float %d: it runs in the VM of "
            "its widths, '%s' next to fire (forge build-vm <folder> --variant %s builds it)\n", path, (unsigned)p->intWidth, (unsigned)p->floatWidth, VM_INT_WIDTH,
            VM_FLOAT_WIDTH, variant, variant + 4 + (variant[4] == '-'));
        vmUnload(p);
        free(bytes);
        return 2;
    }

    t1 = fire_plat_unixMicros();
    vm = (Vm*)calloc(1, sizeof(Vm));
    vm->prog = p;
    g_stackTrace = p->stackTrace;   /* (what the program shows of the stack of an exception, docs/spec/STACKTRACE.md) */
    vm->stack = (Value*)malloc(VM_STACK * sizeof(Value));
    vm->frames = (VmFrame*)calloc(VM_FRAMES, sizeof(VmFrame));
    if (!vm->stack || !vm->frames) outOfMemory();
    g_vmCur = vm;
    vmInstallUnits(vm);
    vm->savedEnums = g_enums; vm->savedEnumCount = g_enumCount;
    g_enums = p->enums; g_enumCount = p->nenums;
    if (p->nresources) {
        res = (ResEntry*)calloc(p->nresources, sizeof(ResEntry));
        for (i = 0; i < p->nresources; i++) { res[i].name = p->resources[i].name; res[i].data = p->resources[i].data; res[i].length = p->resources[i].len; }
        g_resources = res; g_resourceCount = (int)p->nresources;
    }
    vmRegisterClasses(vm);
    vmPkgBegin(pkgPath);
    vmNativesBind(vm);
    vm->global = scNew(vm, NULL);
    scReserve(vm->global, 256);
    g_globalOwn = &vm->global->own;
    vmThreadsBegin(vm);   /* (the GIL; the global scope is the shared domain of the threads) */
    /* the handler at the bottom: catches everything (type -1) */
    vm->bottomTypes[0] = -1;
    vm->bottomInfo.ncatch = 1; vm->bottomInfo.types = vm->bottomTypes; vm->bottomInfo.hasFinally = 0; vm->bottomInfo.typeMap = NULL;
    vm->bottom.fn = vmUnhandledFn; vm->bottom.ctx = vm; vm->bottom.info = &vm->bottomInfo;
    pushHandler(&vm->bottom);
    {
        VmFrame* f = &vm->frames[0];
        f->chunk = p->top; f->ip = p->top->code; f->scope = vm->global; f->base = vm->global; f->self = Undef();
        f->spBase = 0; f->kind = FR_TOP; f->catchEnded = 0; f->rec = NULL; f->retMode = RM_PUSH;
        vm->nframes = 1;
    }
    vmDbgBegin(vm);   /* (FIRE_DEBUG: hello, then the main thread waits for the debugger) */
    vm->exitJump = &jb;
    if (setjmp(jb) == 0) {
        vmRun(vm, 0);
        if (g_unwind.active && (g_unwind.target == &vm->bottom || g_unwind.kind == UW_LEAVE)) clearUnwind();   /* (leave, terminate: the end) */
        vmPkgHoldEnd();   /* (open windows of a library hold the program: it serves them until they close) */
    } else {
        /* the runtime wanted to end the process (fatal, terminate): only the script ends - what was half done in the runtime is given up */
        if (!vm->terminated) vm->failed = 2;
        g_jobDepth = 0; g_noSyncDepth = 0; g_secDepth = 0;   /* (the jump may have left a job, a section of the runtime) */
        if (!vm->quiet) { vm->quiet = 1; g_zombies.quiet++; }
        g_zombies.depth = 0;
    }
    /* the end of the main program waits for its threads (`catch terminate` runs there) */
    if (setjmp(jb) == 0) vmThreadsFinish(vm);
    else {
        if (!vm->terminated) vm->failed = 2;
        if (!vm->quiet) { vm->quiet = 1; g_zombies.quiet++; }
        g_zombies.depth = 0;
    }
    t2 = fire_plat_unixMicros();
    if (terminateRequested() && !vm->failed) { vm->terminated = 1; vm->exitCode = mainExitCode(); }

    /* ---- the end of the script: everything it had goes ---- */
    while (g_handlers) { Handler* h = g_handlers; popHandler(); if (h->fn == vmCatchFn) free(h); }
    g_pending = NULL;
    clearUnwind();
    vmFreeDead(vm);
    while (vm->nframes > 0) {
        VmFrame* f = &vm->frames[vm->nframes - 1];
        vmLeaveTo(vm, f, vmStopOf(vm, f));
        vm->nframes--;
    }
    vm->sp = 0;
    vm->exitJump = NULL;
    vmPkgEnd();                          /* (the libraries of packages forget the program, the lambdas they had are let go) */
    leave(&vm->global->temp);
    leave(&vm->global->own);             /* what the global scope owns (with their destructors - not after a failure) */
    vmPluginsReleaseStatics();           /* (then the own scopes of its fire plugins, in the order they were loaded - docs/spec/PLUGINS.md; the global scope */
    scLeave(vm, vm->global);             /* is still there for their destructors), then the globals */
    vmFreePinned(vm);                    /* (the slots of left scopes that pointers could reach) */
    for (i = 0; i < p->nclasses; i++) {
        uint32_t k;
        for (k = 0; k < p->classes[i]->nstatic; k++) { release(p->classes[i]->svals[k]); p->classes[i]->svals[k] = Undef(); }
    }
    if (g_pool.top) poolRelease(0);
    vmThreadsEnd();
    if (vm->quiet) g_zombies.quiet--;
    g_globalOwn = NULL;
    vmDgReset();
    vmDgSeenFree(vm);
    vmUnregisterClasses(vm);
    vmNativesUnbind();
    vmJsonEnd();
    while (vm->freeScopes) { VmScope* s = vm->freeScopes; vm->freeScopes = s->nextFree; free(s->slots); free(s->names); free(s); }
    result = vm->terminated ? vm->exitCode : vm->failed ? 1 : 0;
    g_vmOutcome.loaded = true; g_vmOutcome.terminated = vm->terminated != 0; g_vmOutcome.failed = vm->failed;
    vmDbgEnd(vm, result);   /* (the debugger: profile, exited) */
    free(vm->stack);
    free(vm->frames);
    g_enums = vm->savedEnums; g_enumCount = vm->savedEnumCount;
    g_resources = NULL; g_resourceCount = 0;
    free(res);
    free(vm);
    g_vmCur = NULL;
    g_vmDemands = NULL;
    g_vmSpecs = NULL;
    g_vmArrCount = 0; g_arrClasses = NULL;
    vmUnload(p);
    vmPluginsEnd();                      /* (the programs of its fire plugins, the tables they made grow) */
    vmUninstallUnits();                  /* (the runtime's table of units, the dimensions plugins added) */
    free(bytes);
    fflush(stdout);
    if (g_vmTime)   /* (--time: what the benchmarks compare - loading, running, taking down) */
        fprintf(stderr, "[fire vm] %s: loaded in %.2f ms, ran in %.2f ms, taken down in %.2f ms\n", path, (double)(t1 - t0) / 1000.0, (double)(t2 - t1) / 1000.0,
            (double)(fire_plat_unixMicros() - t2) / 1000.0);
    if (stats)
        fprintf(stderr, "[fire vm] %s: %s; alive afterwards: %u handles (objects, arrays), %d strings, %u in the temporary pool, %u classes\n", path,
            result == 0 ? "ended" : "failed", vmLiveHandles(),
#ifdef FIRE_HEAP_STATS
            (int)fire_atomic_load(&g_heapStrs),
#else
            -1,
#endif
            g_pool.top, g_classDescCount);
    return result;
}


/* libfirevm: the interface of fire_embed.h (docs/spec/EMBED.md) */
#include "vm_embed.c"

#ifndef FIRE_VM_LIBRARY
/* ---- the payload of a packed program (`forge build --engine vm`, written by src/fire.Compiler/CVmPacker.cs) ----
 *
 *   [this executable] [entry 0] [entry 1] ... [index] [footer]
 *   footer (24 bytes): uint64 offset of the index, uint32 its length, uint32 the version of the format (1), "FIREVMP1"
 *   index: uint32 count; per entry: uint8 kind (0 the program file, 1 a library of a package), uint16 length of the name, the name (UTF-8, a file name),
 *          uint64 offset, uint64 length, uint64 FNV-1a (64 bit) of the bytes
 *
 * All numbers little-endian, nothing compressed (the program file is what `forge bytecode` writes, without line tables). The libraries are written once into
 * a folder of the user's cache named by their hashes (FIRE_VM_CACHE, else $XDG_CACHE_HOME/fire/native, ~/.cache/fire/native, %LOCALAPPDATA%\fire\native; a
 * temporary folder of the user when there is no home) - a library cannot be loaded from memory everywhere (Windows), and a folder per content is written
 * only by the first start of a new build. A file there is taken when its length and hash are right, else it is written again. */
#define FIRE_VM_VERSION "0.8"
#define VM_PAY_MAGIC "FIREVMP1"
#define VM_PAY_FOOTER 24
#define VM_FNV_START 14695981039346656037ull

typedef struct VmPayEntry { uint8_t kind; char name[256]; uint64_t offset, length, hash; } VmPayEntry;

static uint64_t vmLe(const uint8_t* b, int n) { uint64_t v = 0; int i; for (i = n - 1; i >= 0; i--) v = (v << 8) | b[i]; return v; }
static uint64_t vmFnv(uint64_t h, const uint8_t* b, size_t n) { size_t i; for (i = 0; i < n; i++) { h ^= b[i]; h *= 1099511628211ull; } return h; }

static bool vmSeek(FILE* f, uint64_t at) {
#if defined(_WIN32)
    return _fseeki64(f, (__int64)at, SEEK_SET) == 0;
#else
    return fseeko(f, (off_t)at, SEEK_SET) == 0;
#endif
}

/* The payload behind the file `self`: 1 with its entries (malloc'd), 0 when there is none, -1 when it is broken (`why`). */
static int vmPayloadOf(const char* self, VmPayEntry** out, uint32_t* count, char* why, size_t cap) {
    FILE* f = vmFopen(self, "rb");
    uint8_t foot[VM_PAY_FOOTER];
    uint8_t* index = NULL;
    uint64_t size, at, ilen;
    uint32_t n, i;
    size_t pos;
    VmPayEntry* e = NULL;
    *out = NULL; *count = 0;
    if (!f) return 0;
#if defined(_WIN32)
    _fseeki64(f, 0, SEEK_END); size = (uint64_t)_ftelli64(f);
#else
    fseeko(f, 0, SEEK_END); size = (uint64_t)ftello(f);
#endif
    if (size < VM_PAY_FOOTER || !vmSeek(f, size - VM_PAY_FOOTER) || fread(foot, 1, VM_PAY_FOOTER, f) != VM_PAY_FOOTER || memcmp(foot + 16, VM_PAY_MAGIC, 8) != 0) {
        fclose(f);
        return 0;
    }
    at = vmLe(foot, 8); ilen = vmLe(foot + 8, 4);
    if (vmLe(foot + 12, 4) != 1) { snprintf(why, cap, "the program behind this file has another version of the format (this VM reads version 1)"); fclose(f); return -1; }
    if (at + ilen > size - VM_PAY_FOOTER || ilen < 4 || ilen > (64u << 20)) goto broken;
    index = (uint8_t*)malloc((size_t)ilen);
    if (!index) outOfMemory();
    if (!vmSeek(f, at) || fread(index, 1, (size_t)ilen, f) != ilen) goto broken;
    n = (uint32_t)vmLe(index, 4);
    if (n > 4096) goto broken;
    e = (VmPayEntry*)calloc(n ? n : 1, sizeof(VmPayEntry));
    if (!e) outOfMemory();
    pos = 4;
    for (i = 0; i < n; i++) {
        size_t l;
        if (pos + 3 > ilen) goto broken;
        e[i].kind = index[pos];
        l = (size_t)vmLe(index + pos + 1, 2);
        pos += 3;
        if (l == 0 || l >= sizeof e[i].name || pos + l + 24 > ilen) goto broken;
        memcpy(e[i].name, index + pos, l); e[i].name[l] = 0;
        if (strchr(e[i].name, '/') || strchr(e[i].name, '\\') || strstr(e[i].name, "..")) goto broken;   /* (a file name, nothing that leaves the folder) */
        pos += l;
        e[i].offset = vmLe(index + pos, 8); e[i].length = vmLe(index + pos + 8, 8); e[i].hash = vmLe(index + pos + 16, 8);
        pos += 24;
        if (e[i].offset > at || e[i].length > at - e[i].offset) goto broken;
    }
    free(index);
    fclose(f);
    *out = e; *count = n;
    return 1;
broken:
    snprintf(why, cap, "the program behind this file is damaged (build it again)");
    free(index); free(e);
    fclose(f);
    return -1;
}

/* The bytes of an entry (malloc'd), checked against its hash; NULL when they cannot be read or are not right. */
static uint8_t* vmPayloadRead(const char* self, const VmPayEntry* e) {
    FILE* f = vmFopen(self, "rb");
    uint8_t* b;
    if (!f) return NULL;
    b = (uint8_t*)malloc(e->length ? (size_t)e->length : 1);
    if (!b) outOfMemory();
    if (!vmSeek(f, e->offset) || fread(b, 1, (size_t)e->length, f) != e->length || vmFnv(VM_FNV_START, b, (size_t)e->length) != e->hash) { free(b); b = NULL; }
    fclose(f);
    return b;
}

/* mkdir -p */
static bool vmMakeDirs(const char* path) {
    char part[1024];
    size_t i, n = strlen(path);
    if (n >= sizeof part) return false;
    for (i = 1; i <= n; i++) {
        if (path[i] == '/' || path[i] == '\\' || path[i] == 0) {
            memcpy(part, path, i); part[i] = 0;
            if (i > 0 && part[i - 1] != ':') vmMkdir(part);   /* (exists: an error that does not matter) */
        }
    }
    {
        FILE* probe;
        snprintf(part, sizeof part, "%s%c.probe-%d", path, VM_SEP, (int)vmGetPid());
        probe = vmFopen(part, "wb");
        if (!probe) return false;
        fclose(probe);
        vmRemove(part);
    }
    return true;
}

/* The folder of the cache the libraries of packed programs are written into. */
static bool vmCacheFolder(char* out, size_t cap) {
    char given[1024];
    const char* e = vmGetenv("FIRE_VM_CACHE", given, sizeof given);
    if (e && *e) { snprintf(out, cap, "%s", e); return true; }
#if defined(_WIN32)
    /* (the folders of the user as UTF-8: a user name of other letters is not in the ANSI code page) */
    if ((e = vmGetenv("LOCALAPPDATA", given, sizeof given)) != NULL && *e) { snprintf(out, cap, "%s\\fire\\native", e); return true; }
    if ((e = vmGetenv("TEMP", given, sizeof given)) != NULL && *e) { snprintf(out, cap, "%s\\fire-native", e); return true; }
    return false;
#else
    if ((e = getenv("XDG_CACHE_HOME")) != NULL && *e == '/') { snprintf(out, cap, "%s/fire/native", e); return true; }
    if ((e = getenv("HOME")) != NULL && *e == '/') { snprintf(out, cap, "%s/.cache/fire/native", e); return true; }
    e = getenv("TMPDIR");
    snprintf(out, cap, "%s/fire-native-%u", e && *e ? e : "/tmp", (unsigned)getuid());
    return true;
#endif
}

/* A file of the cache whose length and hash are those of the entry. */
static bool vmCachedRight(const char* path, const VmPayEntry* e) {
    size_t len = 0;
    uint8_t* b = fbReadFile(path, &len);
    bool right = b && len == e->length && vmFnv(VM_FNV_START, b, len) == e->hash;
    free(b);
    return right;
}

/* Writes the libraries of the payload into their folder of the cache; the folder (or NULL with `why`). */
static bool vmWriteLibraries(const char* self, const VmPayEntry* e, uint32_t n, char* dir, size_t cap, char* why, size_t whyCap) {
    char base[900], path[1300], part[1400];
    uint64_t key = VM_FNV_START;
    uint32_t i;
    for (i = 0; i < n; i++) if (e[i].kind == 1) { key = vmFnv(key, (const uint8_t*)e[i].name, strlen(e[i].name) + 1); key = vmFnv(key, (const uint8_t*)&e[i].hash, 8); }
    if (!vmCacheFolder(base, sizeof base)) { snprintf(why, whyCap, "there is no folder for the libraries of the program (set FIRE_VM_CACHE)"); return false; }
    snprintf(dir, cap, "%s%c%016llx", base, VM_SEP, (unsigned long long)key);
    if (!vmMakeDirs(dir)) { snprintf(why, whyCap, "the folder '%s' for the libraries of the program cannot be written (set FIRE_VM_CACHE)", dir); return false; }
    for (i = 0; i < n; i++) {
        uint8_t* b;
        FILE* f;
        bool ok;
        if (e[i].kind != 1) continue;
        snprintf(path, sizeof path, "%s%c%s", dir, VM_SEP, e[i].name);
        if (vmCachedRight(path, &e[i])) continue;
        if (!(b = vmPayloadRead(self, &e[i]))) { snprintf(why, whyCap, "the library '%s' behind this file is damaged (build the program again)", e[i].name); return false; }
        /* (written next to it and renamed: a library in the cache is always whole, also when two starts write it at the same time) */
        snprintf(part, sizeof part, "%s.%d.part", path, (int)vmGetPid());
        f = vmFopen(part, "wb");
        ok = f && fwrite(b, 1, (size_t)e[i].length, f) == e[i].length;
        if (f && fclose(f) != 0) ok = false;
        free(b);
#if defined(_WIN32)
        ok = ok && vmReplaceFile(part, path);
#else
        ok = ok && rename(part, path) == 0;
#endif
        if (!ok) {
            vmRemove(part);
            if (vmCachedRight(path, &e[i])) continue;   /* (another start wrote it, or a loaded one is in use) */
            snprintf(why, whyCap, "the library '%s' cannot be written into '%s' (set FIRE_VM_CACHE)", e[i].name, dir);
            return false;
        }
    }
    return true;
}

/* A packed program: the program file behind this executable runs, every argument is its own. */
static int vmRunPacked(const char* self, VmPayEntry* e, uint32_t n, int argc, char** argv) {
    char dir[1100], pkg[4200], why[1500];
    uint8_t* bytes = NULL;
    uint64_t len = 0;
    uint32_t i, libs = 0;
    for (i = 0; i < n; i++) {
        if (e[i].kind == 0 && !bytes) {
            if (!(bytes = vmPayloadRead(self, &e[i]))) { fprintf(stderr, "%s: the program behind this file is damaged (build it again)\n", self); return 2; }
            len = e[i].length;
        }
        else if (e[i].kind == 1) libs++;
    }
    if (!bytes) { fprintf(stderr, "%s: there is no program behind this file\n", self); return 2; }
    snprintf(pkg, sizeof pkg, "%s", self);   /* (no libraries: those next to the program, its folder) */
    if (libs) {
        if (!vmWriteLibraries(self, e, n, dir, sizeof dir, why, sizeof why)) { free(bytes); fprintf(stderr, "%s: %s\n", self, why); return 2; }
        snprintf(pkg, sizeof pkg, "%s%cprogram", dir, VM_SEP);
    }
    fire_setArgs(argc, argv);
    return vmRunProgram(self, pkg, bytes, (size_t)len, getenv("FIRE_VM_STATS") != NULL);
}

static void vmUsage(FILE* to) {
    fputs("fire - the virtual machine of fire: runs program files (.fbc, `forge bytecode`)\n"
          "\n"
          "  fire run [<options>] <program.fbc> [--] [<arguments>]   runs a program; the arguments after it are the program's (Environment.Args)\n"
          "  fire [<options>] <program.fbc>... [-- <arguments>]      runs the programs one after the other in this process\n"
          "  fire help | fire version\n"
          "\n"
          "options:\n"
          "  --debug <spec>   waits for the debugger: tcp:HOST:PORT;token=T or stdio[;token=T][;wait=MS] (as FIRE_DEBUG)\n"
          "  --stats          prints after each program what is still alive in the runtime\n"
          "  --time           prints after each program how long it took to load, run and take down\n"
          "\n"
          "exit code: 0, 1 for an exception nothing caught or an error of the runtime, the code of terminate(code), 2 when a program cannot be loaded\n"
          "the libraries of packages: FIRE_PKG_PATH, then next to the program, then next to fire (Packages/, Prebuilt/ of a forge)\n", to);
}

static void vmSetEnv(const char* name, const char* value) {
#if defined(_WIN32)
    wchar_t* n = fire_plat_toWide(name);
    wchar_t* v = fire_plat_toWide(value);
    if (n && v) _wputenv_s(n, v);   /* (UTF-8: the wide environment, which vmGetenv reads) */
    free(n); free(v);
#else
    setenv(name, value, 1);
#endif
}

/* The folder of `fire` itself goes behind FIRE_PKG_PATH: installed next to forge, it finds the libraries forge built (Packages/) or brought (Prebuilt/). */
static void vmAddSelfToPkgPath(const char* self) {
    const char* slash = strrchr(self, '/');
    char given[4096];
    const char* old = vmGetenv("FIRE_PKG_PATH", given, sizeof given);
    char value[8192];
#if defined(_WIN32)
    const char* back = strrchr(self, '\\');
    if (back && (!slash || back > slash)) slash = back;
#define VM_PATH_SEP ";"
#else
#define VM_PATH_SEP ":"
#endif
    if (!slash) return;
    snprintf(value, sizeof value, "%s%s%.*s", old && *old ? old : "", old && *old ? VM_PATH_SEP : "", (int)(slash - self), self);
    vmSetEnv("FIRE_PKG_PATH", value);
}

/* One program file of the command line. */
static int vmRunFile(const char* path, bool stats) {
    size_t len = 0;
    uint8_t* bytes = fbReadFile(path, &len);
    if (!bytes) { fprintf(stderr, "fire: cannot read '%s'\n", path); return 2; }
    return vmRunProgram(path, path, bytes, len, stats);
}

/* --stats, --time, --debug <spec> at argv[*i]: 1 when it was one (taken), 0 when not, -1 when it is wrong. */
static int vmOption(int argc, char** argv, int* i, bool* stats) {
    if (strcmp(argv[*i], "--stats") == 0) { *stats = true; return 1; }
    if (strcmp(argv[*i], "--time") == 0) { g_vmTime = true; return 1; }
    if (strcmp(argv[*i], "--debug") == 0) {
        /* the debugger (docs/spec/NATIVE_DEBUG_PROTOCOL.md): tcp:HOST:PORT;token=T or stdio[;token=T][;wait=MS], like FIRE_DEBUG */
        if (*i + 1 >= argc) { fputs("fire: --debug needs the transport (tcp:HOST:PORT;token=T or stdio)\n", stderr); return -1; }
        vmSetEnv("FIRE_DEBUG", argv[++*i]);
        return 1;
    }
    if (argv[*i][0] == '-' && argv[*i][1] != 0 && strcmp(argv[*i], "--") != 0) { fprintf(stderr, "fire: unknown option '%s' (fire help)\n", argv[*i]); return -1; }
    return 0;
}

#if defined(_WIN32)
typedef struct VmArgList { char** v; int n, cap; } VmArgList;
static void vmArgAdd(const char* utf8, void* ctx) {
    VmArgList* l = (VmArgList*)ctx;
    if (l->n + 2 > l->cap) {
        l->cap = l->cap ? l->cap * 2 : 16;
        l->v = (char**)realloc(l->v, sizeof(char*) * (size_t)l->cap);
        if (!l->v) outOfMemory();
    }
    l->v[l->n++] = vmPkgDup(utf8);
    l->v[l->n] = NULL;
}
/* The arguments of this process as UTF-8, the first one the program itself (kept for the life of the process). */
static char** vmWinArgv(int* argc, const char* self) {
    VmArgList l = { NULL, 0, 0 };
    vmArgAdd(self, &l);
    fire_plat_eachCommandLineArg(vmArgAdd, &l);
    *argc = l.n;
    return l.v;
}
#endif

int main(int argc, char** argv) {
    char self[4096], why[300];
    bool stats = false;
    int i, code = 0, files = 0, last = argc;
    VmPayEntry* entries;
    uint32_t nentries;
    int packed;
    /* FIRE_STDOUT_LINES=1: stdout is written line by line also into a pipe (spark's Output panel shows a running program's lines as they come; the C runtime
     * of Windows has no line buffering - _IOLBF is full buffering there - so stdout is not buffered at all) */
    { const char* lines = getenv("FIRE_STDOUT_LINES"); if (lines != NULL && *lines == '1') {
#if defined(_WIN32)
        setvbuf(stdout, NULL, _IONBF, 0);
#else
        setvbuf(stdout, NULL, _IOLBF, 0);
#endif
    } }
    if (fire_plat_programPath(self, sizeof self) <= 0) snprintf(self, sizeof self, "%s", argc > 0 && argv[0] ? argv[0] : "fire");
#if defined(_WIN32)
    argv = vmWinArgv(&argc, self);   /* (UTF-8 from the wide command line: main's argv is in the ANSI code page) */
#endif

    /* a packed program: every argument is the program's */
    packed = vmPayloadOf(self, &entries, &nentries, why, sizeof why);
    if (packed < 0) { fprintf(stderr, "%s: %s\n", self, why); return 2; }
    if (packed > 0) { code = vmRunPacked(self, entries, nentries, argc, argv); free(entries); return code; }

    vmAddSelfToPkgPath(self);
    g_vmSelf = self;
    if (argc >= 2 && (strcmp(argv[1], "help") == 0 || strcmp(argv[1], "--help") == 0 || strcmp(argv[1], "-h") == 0 || strcmp(argv[1], "-?") == 0)) { vmUsage(stdout); return 0; }
    if (argc >= 2 && (strcmp(argv[1], "version") == 0 || strcmp(argv[1], "--version") == 0)) {
        printf("fire %s (the VM in C, docs/spec/VM_C.md): program files of format 1, int %d, float %d, %s\n", FIRE_VM_VERSION, VM_INT_WIDTH, VM_FLOAT_WIDTH, vmPkgRid());
        return 0;
    }
    if (argc >= 2 && strcmp(argv[1], "run") == 0) {
        /* fire run [options] x.fbc [--] a b: one program, Environment.Args are a b (the runtime leaves out the first of its list: the program) */
        int at, rest;
        char** args;
        for (i = 2; i < argc; i++) {
            int o = vmOption(argc, argv, &i, &stats);
            if (o < 0) return 2;
            if (o == 0) break;
        }
        if (i >= argc || strcmp(argv[i], "--") == 0) { fputs("fire run: which program? (fire run <program.fbc> [<arguments>])\n", stderr); return 2; }
        at = i;
        rest = at + 1 < argc && strcmp(argv[at + 1], "--") == 0 ? at + 2 : at + 1;
        args = (char**)malloc(sizeof(char*) * (size_t)(argc - rest + 2));
        if (!args) outOfMemory();
        args[0] = argv[at];
        for (i = rest; i < argc; i++) args[1 + i - rest] = argv[i];
        args[1 + argc - rest] = NULL;
        fire_setArgs(1 + argc - rest, args);
        g_vmArgv = argv;
        code = vmRunFile(argv[at], stats);
        free(args);
        return code;
    }
    for (i = 1; i < argc; i++) if (strcmp(argv[i], "--") == 0) { last = i; break; }
    fire_setArgs(argc - (last < argc ? last : argc - 1), argv + (last < argc ? last : argc - 1));   /* (Environment.Args: what follows `--`) */
    for (i = 1; i < last; i++) if (argv[i][0] != '-') files++; else if (strcmp(argv[i], "--debug") == 0) i++;
    if (files == 1) g_vmArgv = argv;   /* (one program: a variant of other widths may run it) */
    files = 0;
    for (i = 1; i < last; i++) {
        int r, o = vmOption(last, argv, &i, &stats);
        if (o < 0) return 2;
        if (o > 0) continue;
        files++;
        if ((r = vmRunFile(argv[i], stats)) != 0) code = r;
    }
    if (!files) { vmUsage(stderr); return 2; }
    return code;
}
#endif
