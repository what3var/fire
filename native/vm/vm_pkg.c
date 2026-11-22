/* fire VM in C: the natives of packages (phase B step 4) - shared libraries over the C ABI v1 (native/abi/fire_pkg_abi.h), as the C# VM loads them
 * (src/fire.Runtime/PackageNativeBinding.cs), and the host the libraries ask (`fire_host`, src/fire.Runtime/Host/PackageHost.cs).
 *
 * A library is found by its file name (the program file names it per native: `packageNativeLibraries`) in the folders of FIRE_PKG_PATH (`:`-separated, `;` on
 * Windows), then next to the program file: in a folder itself, in `Packages/<package>/lib/<rid>/` (the package store of a forge, where `forge run` builds
 * them) and in `Prebuilt/<package>/<rid>/` (what a build of forge brings). It is loaded on the first call of one of its natives and stays loaded for the
 * programs after (fire_pkg_reset at the end of each). Values cross as plain data (copied; byte buffers by reference); a lambda crosses as a handle the library
 * calls back (call_lambda): on the VM's thread at once - an exception it throws during a native the program waits for is kept and thrown at that native's call
 * once it has returned (SPEC 8.1.4, fire_cbCall) -, from another thread at the VM's next safe point (a call of a native, a wait, the end of the program).
 * The host decides: every path and every host is allowed, the console is the process's (the packed runtime of the C# VM does the same); the devices of the host
 * are none (the devices package uses its own drivers). The end of a program waits while a library holds it (`hold`: open windows), serving what is posted. */
#if defined(_WIN32)
#include <windows.h>
#else
#include <dlfcn.h>
#include <dirent.h>
#include <pthread.h>
#include <unistd.h>
#endif
#include "../abi/fire_pkg_abi.h"

typedef int (*VmPkgCallFn)(int, const fire_val*, int, fire_val*, char*, int);

typedef struct VmPkgLib {
    char* file;                 /* the file name the program names */
    char* failure;              /* why it cannot be had (NULL: loaded) */
    void* handle;
    VmPkgCallFn call;
    void (*reset)(void);
    void (*service)(void);
    void (*idle)(void);
    int nfn;
    const char** names;
    int* arity;
    struct VmPkgLib* next;
} VmPkgLib;

static VmPkgLib* g_vmLibs = NULL;   /* every library loaded so far (they stay for the programs after) */

/* ---- the lambdas that crossed (FIRE_LAMBDA): a handle each (1, 2, ...), kept until the program ends ---- */
static Value* g_vmPkgLams = NULL;
static uint32_t g_vmPkgLamCount = 0, g_vmPkgLamCap = 0;

/* the callbacks other threads posted (call_lambda), the services asked for (request_service), the holds on the end of the program */
typedef struct VmPkgPosted { int64_t handle; int32_t argc; fire_val* args; struct VmPkgPosted* next; } VmPkgPosted;
static VmPkgPosted* g_vmPkgPosted = NULL;
static VmPkgPosted** g_vmPkgPostedTail = &g_vmPkgPosted;
static fire_atomic32 g_vmPkgService;
static fire_atomic32 g_vmPkgHolds;
static FIRE_TLS int g_vmPkgDepth = 0;  /* the natives of packages running on this thread (nested through callbacks) */
static FIRE_TLS int g_vmPkgBarrier = 0;   /* natives up to this depth wait for no callback (a service or an idle turn runs inside them) */
static FIRE_TLS const FireFnInfo* g_vmPkgNat = NULL;   /* the innermost of them: the frame of a native below its callbacks (vm_trace.c) */
static void vmTraceNatPush(const FireFnInfo* fn);
static void vmTraceNatPop(void);
static Value g_vmPkgDeferred;          /* the exception of a callback, kept for the native that waits for it */
static int g_vmPkgDeferredSet = 0;
#if defined(_WIN32)
static SRWLOCK g_vmPkgLock = SRWLOCK_INIT;
static DWORD g_vmPkgThread;
static void vmPkgLock(void) { AcquireSRWLockExclusive(&g_vmPkgLock); }
static void vmPkgUnlock(void) { ReleaseSRWLockExclusive(&g_vmPkgLock); }
static bool vmPkgOnVmThread(void) { return GetCurrentThreadId() == g_vmPkgThread; }
static void vmPkgNoteThread(void) { g_vmPkgThread = GetCurrentThreadId(); }
#else
static pthread_mutex_t g_vmPkgLock = PTHREAD_MUTEX_INITIALIZER;
static pthread_t g_vmPkgThread;
static void vmPkgLock(void) { pthread_mutex_lock(&g_vmPkgLock); }
static void vmPkgUnlock(void) { pthread_mutex_unlock(&g_vmPkgLock); }
static bool vmPkgOnVmThread(void) { return pthread_equal(pthread_self(), g_vmPkgThread) != 0; }
static void vmPkgNoteThread(void) { g_vmPkgThread = pthread_self(); }
#endif

/* ---- values: Value -> fire_val (the memory of the conversion lives until the call has returned) ---- */
typedef struct VmPkgArena { void** blocks; uint32_t n, cap; } VmPkgArena;
static void* vmPkgAlloc(VmPkgArena* a, size_t bytes) {
    void* p = malloc(bytes ? bytes : 1);
    if (!p) outOfMemory();
    if (a->n == a->cap) {
        uint32_t cap = a->cap ? a->cap * 2 : 8;
        void** grown = (void**)realloc(a->blocks, cap * sizeof(void*));
        if (!grown) outOfMemory();
        a->blocks = grown; a->cap = cap;
    }
    a->blocks[a->n++] = p;
    return p;
}
static void vmPkgArenaFree(VmPkgArena* a) { uint32_t i; for (i = 0; i < a->n; i++) free(a->blocks[i]); free(a->blocks); a->blocks = NULL; a->n = a->cap = 0; }

static int64_t vmPkgLambdaHandle(Value v) {
    if (g_vmPkgLamCount == g_vmPkgLamCap) {
        uint32_t cap = g_vmPkgLamCap ? g_vmPkgLamCap * 2 : 16;
        Value* grown = (Value*)realloc(g_vmPkgLams, cap * sizeof(Value));
        if (!grown) outOfMemory();
        g_vmPkgLams = grown; g_vmPkgLamCap = cap;
    }
    retain(v);
    g_vmPkgLams[g_vmPkgLamCount++] = v;
    return (int64_t)g_vmPkgLamCount;   /* (1, 2, ...: 0 is no lambda) */
}

/* The kind of a value in the text of the C# VM (ValueKind). */
static const char* vmPkgKindText(Value v) {
    switch (v.kind) {
        case K_Class: return "Class";
        case K_Lambda: return "Lambda";
        default: return FIRE_IS_PTR_KIND(v.kind) ? "Pointer" : kindName(v);
    }
}

/* False: the value cannot cross (an object, a pointer): the ArgumentException is thrown. */
static bool vmPkgToNative(VmPkgArena* arena, Value v, fire_val* out, const char* function) {
    out->length = 0;
    out->v.i = 0;
    switch (v.kind) {
        case K_Undefined: out->kind = FIRE_UNDEFINED; return true;
        case K_Bool: out->kind = FIRE_BOOL; out->v.i = v.i ? 1 : 0; return true;
        case K_Int: out->kind = FIRE_INT; out->v.i = v.i; return true;
        case K_Float: out->kind = FIRE_FLOAT; out->v.f = (double)v.f; return true;
        case K_Char: out->kind = FIRE_CHAR; out->v.i = v.i; return true;
        case K_Word: out->kind = FIRE_WORD; out->v.i = (int64_t)(uint64_t)v.w; return true;
        case K_String: { const Str* s = strOf(v); out->kind = FIRE_STRING; out->length = (int32_t)s->length; out->v.p = s->data; return true; }
        case K_Array: {
            Arr* a = arrOf(v);
            fire_val* items = (fire_val*)vmPkgAlloc(arena, (size_t)a->length * sizeof(fire_val));
            uint32_t i;
            for (i = 0; i < a->length; i++) if (!vmPkgToNative(arena, arrItems(a)[i], &items[i], function)) return false;
            out->kind = FIRE_ARRAY; out->length = (int32_t)a->length; out->v.p = items;
            return true;
        }
        case K_Buffer: {
            Buf* b = bufOf(v);   /* by reference: the native reads and writes the bytes in place */
            out->kind = b->elem != 1 ? FIRE_WORDS : FIRE_BUFFER; out->length = (int32_t)b->length; out->v.p = b->data;
            return true;
        }
        case K_Lambda: out->kind = FIRE_LAMBDA; out->length = (int32_t)lamOf(v)->nparams; out->v.i = vmPkgLambdaHandle(v); return true;
        default: {
            char text[400];
            snprintf(text, sizeof text, "An argument of the native function '%s' is a %s: only numbers, bool, char, text, byte buffers, lambdas and arrays of these can be passed to a package's native.",
                function, vmPkgKindText(v));
            fireError("ArgumentException", text);
            return false;
        }
    }
}

static Value vmPkgFromNative(const fire_val* in, OwnList* list) {
    switch (in->kind) {
        case FIRE_UNDEFINED: return Undef();
        case FIRE_BOOL: return Bool(in->v.i != 0);
        case FIRE_INT: return IntNative(in->v.i);   /* (`#intwidth 32`: one that does not fit keeps its 64 bits - like the VM and FIRE_INT_U of a native build) */
        case FIRE_FLOAT: return Float((Real)in->v.f);
        case FIRE_CHAR: return Char((uint32_t)in->v.i);
        case FIRE_WORD: return WordV((uint64_t)in->v.i);
        case FIRE_STRING: return newStrFrom((const fire_char16*)in->v.p, (uint32_t)(in->length > 0 ? in->length : 0), list);
        case FIRE_ARRAY: {
            Arr* a = allocArr((uint32_t)(in->length > 0 ? in->length : 0), list);
            const fire_val* items = (const fire_val*)in->v.p;
            int32_t i;
            for (i = 0; i < in->length; i++) { Value x = vmPkgFromNative(&items[i], list); arrItems(a)[i] = x; retain(x); }
            return ArrV(a);
        }
        case FIRE_BUFFER: case FIRE_WORDS: {
            uint8_t elem = in->kind == FIRE_WORDS ? (uint8_t)sizeof(FireWord) : 1;
            Buf* b = allocBufOf((uint32_t)(in->length > 0 ? in->length : 0), elem, list);
            if (in->length > 0) memcpy(b->data, in->v.p, (size_t)in->length * elem);
            return BufV(b);
        }
        default: fatal("A native function of a package returned a value of an unknown kind.");
    }
}

/* ---- the host (fire_host) ---- */
static int32_t vmHostIoAllow(const char* path, int32_t access, char* reason, int32_t size) { (void)path; (void)access; (void)reason; (void)size; return 1; }
static int32_t vmHostNetAllow(const char* host, int32_t port, int32_t access, char* reason, int32_t size) { (void)host; (void)port; (void)access; (void)reason; (void)size; return 1; }
static int32_t vmHostStdRead(int32_t stream, uint8_t* buffer, int32_t count) {
    if (stream != 0 || count <= 0) return -1;
    fflush(stdout);
#if defined(_WIN32)
    { DWORD n = 0; return ReadFile(GetStdHandle(STD_INPUT_HANDLE), buffer, (DWORD)count, &n, NULL) ? (int32_t)n : (GetLastError() == ERROR_BROKEN_PIPE ? 0 : -1); }
#else
    { ssize_t n = read(0, buffer, (size_t)count); return n < 0 ? -1 : (int32_t)n; }
#endif
}
static int32_t vmHostStdWrite(int32_t stream, const uint8_t* buffer, int32_t count) {
    FILE* f = stream == 1 ? stdout : stream == 2 ? stderr : NULL;
    if (!f || count < 0) return -1;
    if (f == stderr) fflush(stdout);   /* (what the program printed comes first) */
    if (count > 0 && fwrite(buffer, 1, (size_t)count, f) != (size_t)count) return -1;
    return count;
}
static int32_t vmHostStdFlush(int32_t stream) {
    if (stream != 1 && stream != 2) return -1;
    fflush(stream == 1 ? stdout : stderr);
    return 0;
}
static VmPkgLib* vmPkgLoad(const char* file);
static void* vmPkgExport(VmPkgLib* lib, const char* name);
static VmPkgLib* vmPkgGraphics(void);
static int32_t vmHostWinAttach(int32_t framebuffer, fire_present_fn present, void* ctx) {
    VmPkgLib* g = vmPkgGraphics();
    int32_t (*fn)(int32_t, fire_present_fn, void*) = g ? (int32_t (*)(int32_t, fire_present_fn, void*))vmPkgExport(g, "fire_gfx_host_attach") : NULL;
    return fn && present ? fn(framebuffer, present, ctx) : 0;
}
static void vmHostWinDetach(int32_t framebuffer, void* ctx) {
    VmPkgLib* g = vmPkgGraphics();
    void (*fn)(int32_t, void*) = g ? (void (*)(int32_t, void*))vmPkgExport(g, "fire_gfx_host_detach") : NULL;
    if (fn) fn(framebuffer, ctx);
}

/* A lambda of a library, on the VM's thread: 0 it ran; 2 it threw during a native the program waits for (kept: thrown at that native); 1 it did not run or
 * threw where nobody waits (reported). */
static int32_t vmPkgRunLambda(int64_t handle, const fire_val* args, int32_t argc) {
    OwnList list;
    Value values[16], lam;
    int32_t i, n = argc < 16 ? argc : 16;
    if (handle <= 0 || (uint64_t)handle > g_vmPkgLamCount || !g_vmCur) return 1;
    if (g_vmPkgDepth > g_vmPkgBarrier && g_vmPkgDeferredSet) return 2;   /* (an earlier callback of this native threw: no more) */
    if (g_unwind.active) return 1;
    lam = g_vmPkgLams[handle - 1];
    ownListInit(&list, poolMark(), NULL);
    for (i = 0; i < n; i++) values[i] = vmPkgFromNative(&args[i], &list);
    if (g_vmPkgDepth > g_vmPkgBarrier && g_vmPkgNat) {
        /* (during the native the program waits for: its frame below the lambda's - `at __GRPHWinTick (native)`) */
        vmTraceNatPush(g_vmPkgNat);
        fire_cbCall(lam, n, values, &list);
        vmTraceNatPop();
    } else fire_cbCall(lam, n, values, &list);
    leave(&list);
    if (g_unwind.active && g_unwind.kind == UW_DEFER) {
        if (g_vmPkgDepth > g_vmPkgBarrier) {
            if (!g_vmPkgDeferredSet) { g_vmPkgDeferred = g_unwind.value; g_vmPkgDeferredSet = 1; }
            clearUnwind();
            return 2;
        }
        fire_cbReport();
        return 1;
    }
    return g_unwind.active ? 1 : 0;
}

static int32_t vmHostCallLambda(int64_t handle, const fire_val* args, int32_t argc) {
    VmPkgPosted* p;
    int32_t i;
    if (vmPkgOnVmThread()) return vmPkgRunLambda(handle, args, argc);
    /* another thread: the arguments are copied (strings, arrays and buffers deep), the VM runs it at its next safe point */
    p = (VmPkgPosted*)calloc(1, sizeof(VmPkgPosted));
    if (!p) return 1;
    p->handle = handle;
    p->argc = argc < 16 ? argc : 16;
    p->args = (fire_val*)calloc((size_t)(p->argc ? p->argc : 1), sizeof(fire_val));
    for (i = 0; i < p->argc; i++) {
        fire_val v = args[i];
        size_t bytes = v.kind == FIRE_STRING ? (size_t)v.length * 2 : v.kind == FIRE_BUFFER ? (size_t)v.length : v.kind == FIRE_WORDS ? (size_t)v.length * 8 : 0;
        if (bytes) { void* copy = malloc(bytes); if (copy) memcpy(copy, v.v.p, bytes); v.v.p = copy; }
        else if (v.kind == FIRE_ARRAY) { v.kind = FIRE_UNDEFINED; v.length = 0; }   /* (an array of a callback from another thread: not carried) */
        p->args[i] = v;
    }
    vmPkgLock();
    *g_vmPkgPostedTail = p;
    g_vmPkgPostedTail = &p->next;
    vmPkgUnlock();
    return 0;
}
static void vmHostRequestService(void) { fire_atomic_store(&g_vmPkgService, 1); }
static void vmHostHold(int32_t delta) { fire_atomic_add(&g_vmPkgHolds, (uint32_t)delta); }

static fire_host g_vmHost;
static void vmDevHostInstall(fire_host* host);   /* (vm_devhost.c: FIRE_DEVICE_HOST, the devices of spark) */
static const fire_host* vmPkgHost(void) {
    if (!g_vmHost.size) {
        g_vmHost.size = (int32_t)sizeof(fire_host);
        g_vmHost.io_allow = vmHostIoAllow;
        g_vmHost.std_read = vmHostStdRead;
        g_vmHost.std_write = vmHostStdWrite;
        g_vmHost.std_flush = vmHostStdFlush;
        g_vmHost.net_allow = vmHostNetAllow;
        g_vmHost.win_attach = vmHostWinAttach;
        g_vmHost.win_detach = vmHostWinDetach;
        g_vmHost.call_lambda = vmHostCallLambda;
        g_vmHost.request_service = vmHostRequestService;
        g_vmHost.hold = vmHostHold;
        vmDevHostInstall(&g_vmHost);
    }
    return &g_vmHost;
}

/* ---- finding and loading a library ---- */
static const char* vmPkgRid(void) {
#if defined(_WIN32)
#define VM_PKG_OS "win"
#elif defined(__APPLE__)
#define VM_PKG_OS "osx"
#else
#define VM_PKG_OS "linux"
#endif
#if defined(__x86_64__) || defined(_M_X64)
    return VM_PKG_OS "-x64";
#elif defined(__aarch64__) || defined(_M_ARM64)
    return VM_PKG_OS "-arm64";
#elif defined(__i386__) || defined(_M_IX86)
    return VM_PKG_OS "-x86";
#elif defined(__arm__)
    return VM_PKG_OS "-arm";
#else
    return VM_PKG_OS "-other";
#endif
}

static bool vmPkgExists(const char* path) { FILE* f = fopen(path, "rb"); if (f) fclose(f); return f != NULL; }

/* `dir/sub/<package>/tail/file` for every package folder of dir/sub; true with the path in `out`. */
static bool vmPkgScan(const char* dir, const char* sub, const char* tail, const char* file, char* out, size_t cap) {
#if defined(_WIN32)
    char pattern[1024];
    WIN32_FIND_DATAA fd;
    HANDLE h;
    snprintf(pattern, sizeof pattern, "%s/%s%s*", dir, sub, *sub ? "/" : "");
    h = FindFirstFileA(pattern, &fd);
    if (h == INVALID_HANDLE_VALUE) return false;
    do {
        if (fd.cFileName[0] == '.' || !(fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) continue;
        snprintf(out, cap, "%s/%s%s%s/%s/%s", dir, sub, *sub ? "/" : "", fd.cFileName, tail, file);
        if (vmPkgExists(out)) { FindClose(h); return true; }
    } while (FindNextFileA(h, &fd));
    FindClose(h);
    return false;
#else
    char path[1024];
    DIR* d;
    struct dirent* e;
    snprintf(path, sizeof path, "%s%s%s", dir, *sub ? "/" : "", sub);
    if (!(d = opendir(path))) return false;
    while ((e = readdir(d)) != NULL) {
        if (e->d_name[0] == '.') continue;
        snprintf(out, cap, "%s/%s/%s/%s", path, e->d_name, tail, file);
        if (vmPkgExists(out)) { closedir(d); return true; }
    }
    closedir(d);
    return false;
#endif
}

static const char* g_vmPkgProgramDir = NULL;   /* the folder of the program file that runs */

static bool vmPkgFindIn(const char* dir, const char* file, char* out, size_t cap) {
    char tail[64];
    if (!*dir) return false;
    snprintf(out, cap, "%s/%s", dir, file);
    if (vmPkgExists(out)) return true;
    snprintf(tail, sizeof tail, "lib/%s", vmPkgRid());
    if (vmPkgScan(dir, "Packages", tail, file, out, cap)) return true;
    if (vmPkgScan(dir, "Prebuilt", vmPkgRid(), file, out, cap)) return true;
    if (vmPkgScan(dir, "", tail, file, out, cap)) return true;   /* (dir is a package store itself) */
    return vmPkgScan(dir, "", vmPkgRid(), file, out, cap);         /* (... or a Prebuilt folder) */
}

static bool vmPkgFind(const char* file, char* out, size_t cap) {
    const char* path = getenv("FIRE_PKG_PATH");
#if defined(_WIN32)
    const char sep = ';';
    bool absolute = file[0] == '/' || file[0] == '\\' || (file[0] && file[1] == ':');
#else
    const char sep = ':';
    bool absolute = file[0] == '/';
#endif
    /* (a full path: the library of a fire plugin next to its bytecode, a driver plugin's package library - vm_plugin.c) */
    if (absolute) { snprintf(out, cap, "%s", file); return vmPkgExists(out); }
    while (path && *path) {
        char dir[1024];
        const char* end = strchr(path, sep);
        size_t n = end ? (size_t)(end - path) : strlen(path);
        if (n >= sizeof dir) n = sizeof dir - 1;
        memcpy(dir, path, n); dir[n] = 0;
        if (vmPkgFindIn(dir, file, out, cap)) return true;
        path = end ? end + 1 : NULL;
    }
    return g_vmPkgProgramDir && vmPkgFindIn(g_vmPkgProgramDir, file, out, cap);
}

static void* vmPkgSym(void* h, const char* name) {
#if defined(_WIN32)
    return (void*)GetProcAddress((HMODULE)h, name);
#else
    return dlsym(h, name);
#endif
}
static void* vmPkgExport(VmPkgLib* lib, const char* name) { return lib && lib->handle ? vmPkgSym(lib->handle, name) : NULL; }

static char* vmPkgDup(const char* s) { size_t n = strlen(s) + 1; char* r = (char*)malloc(n); if (!r) outOfMemory(); memcpy(r, s, n); return r; }

/* The library of this file name: loaded now if it is not yet (a failure is remembered: `failure` says why). */
static VmPkgLib* vmPkgLoad(const char* file) {
    VmPkgLib* lib;
    char path[1200], why[600];
    int (*version)(void);
    int (*count)(void);
    const char* (*nameOf)(int);
    int (*arityOf)(int);
    void (*setHost)(const fire_host*);
    int i;
    for (lib = g_vmLibs; lib; lib = lib->next) if (strcmp(lib->file, file) == 0) return lib;
    lib = (VmPkgLib*)calloc(1, sizeof(VmPkgLib));
    if (!lib) outOfMemory();
    lib->file = vmPkgDup(file);
    lib->next = g_vmLibs;
    g_vmLibs = lib;
    if (!*file) { lib->failure = vmPkgDup("the package has no native library"); return lib; }
    if (!vmPkgFind(file, path, sizeof path)) {
        snprintf(why, sizeof why, "it is not in the folders of FIRE_PKG_PATH, nor next to the program (build it with `forge run` of a program that imports the package, or `forge prebuild-standard`)");
        lib->failure = vmPkgDup(why);
        return lib;
    }
#if defined(_WIN32)
    lib->handle = (void*)LoadLibraryA(path);
    if (!lib->handle) { snprintf(why, sizeof why, "%s cannot be loaded (error %lu)", path, (unsigned long)GetLastError()); lib->failure = vmPkgDup(why); return lib; }
#else
    lib->handle = dlopen(path, RTLD_NOW | RTLD_LOCAL);
    if (!lib->handle) { snprintf(why, sizeof why, "%s", dlerror()); lib->failure = vmPkgDup(why); return lib; }
#endif
    version = (int (*)(void))vmPkgSym(lib->handle, "fire_pkg_abi_version");
    lib->call = (VmPkgCallFn)vmPkgSym(lib->handle, "fire_pkg_call");
    count = (int (*)(void))vmPkgSym(lib->handle, "fire_pkg_function_count");
    nameOf = (const char* (*)(int))vmPkgSym(lib->handle, "fire_pkg_function_name");
    arityOf = (int (*)(int))vmPkgSym(lib->handle, "fire_pkg_function_arity");
    if (!version || !lib->call || !count || !nameOf || !arityOf) { lib->failure = vmPkgDup("it does not export the functions of the package ABI"); lib->handle = NULL; return lib; }
    if (version() != FIRE_PKG_ABI_VERSION) {
        snprintf(why, sizeof why, "the library speaks version %d of the package ABI (this fire understands %d)", version(), FIRE_PKG_ABI_VERSION);
        lib->failure = vmPkgDup(why); lib->handle = NULL; return lib;
    }
    lib->service = (void (*)(void))vmPkgSym(lib->handle, "fire_pkg_service");
    lib->idle = (void (*)(void))vmPkgSym(lib->handle, "fire_pkg_idle");
    lib->reset = (void (*)(void))vmPkgSym(lib->handle, "fire_pkg_reset");
    setHost = (void (*)(const fire_host*))vmPkgSym(lib->handle, "fire_pkg_set_host");
    if (setHost) setHost(vmPkgHost());
    lib->nfn = count();
    if (lib->nfn < 0) lib->nfn = 0;
    lib->names = (const char**)calloc((size_t)lib->nfn + 1, sizeof(char*));
    lib->arity = (int*)calloc((size_t)lib->nfn + 1, sizeof(int));
    if (!lib->names || !lib->arity) outOfMemory();
    for (i = 0; i < lib->nfn; i++) { lib->names[i] = nameOf(i); lib->arity[i] = arityOf(i); }
    return lib;
}

#if defined(_WIN32)
#define VM_GFX_FILE "fire_gfx.dll"
#elif defined(__APPLE__)
#define VM_GFX_FILE "libfire_gfx.dylib"
#else
#define VM_GFX_FILE "libfire_gfx.so"
#endif
/* The library of graphics and windows (PackageLibrary.GraphicsFileName), when a program uses it. */
static bool g_vmPkgUsesGraphics = false;
static VmPkgLib* vmPkgGraphics(void) {
    VmPkgLib* lib;
    if (!g_vmPkgUsesGraphics) return NULL;
    lib = vmPkgLoad(VM_GFX_FILE);
    return lib->failure ? NULL : lib;
}

/* ---- calls ---- */
/* The index of a native in its library (-1: none) - checked once per program (vmNativesBind). */
static int vmPkgIndexOf(VmPkgLib* lib, const char* name) {
    int i;
    for (i = 0; i < lib->nfn; i++) if (lib->names[i] && strcmp(lib->names[i], name) == 0) return i;
    return -1;
}

static void vmPkgServe(void);

/* The main program away in a native (the GIL let go, vmPkgAway): a fire thread that asks for a section takes it itself - the main program does not touch
 * the globals meanwhile (fire_rt.h fire_sectionGrantedAway, THREADING_DESIGN.md section 7) - one thread at a time; the request stays in the queue, where
 * the main program finds it done. Back from the native, the main program waits until the sections the threads took are done. */
static fire_atomic32 g_vmAway;
static fire_mutex g_vmAwayM;
static bool g_vmAwayInit;
static SectionReq* g_vmAwayReqs[64];
static uint32_t g_vmAwayCount;
static bool vmAwayBusy(void) {   /* (under g_vmAwayM: a section a thread took is not done yet) */
    uint32_t i;
    for (i = 0; i < g_vmAwayCount; i++) if (!fire_sectionDone(g_vmAwayReqs[i])) return true;
    return false;
}
FIRE_FN bool fire_vmGrantAway(SectionReq* r) {
    bool ok = false;
    if (!fire_atomic_load(&g_vmAway)) return false;
    fire_mutex_lock(&g_vmAwayM);
    if (fire_atomic_load(&g_vmAway) && !vmAwayBusy() && g_vmAwayCount < 64) { g_vmAwayReqs[g_vmAwayCount++] = r; ok = true; }
    fire_mutex_unlock(&g_vmAwayM);
    return ok;
}
static bool vmMainAway(void) { return fire_atomic_load(&g_vmAway) != 0; }
static void vmAwayBegin(void) {
    if (!g_vmAwayInit) { fire_mutex_init(&g_vmAwayM); g_vmAwayInit = true; }
    fire_atomic_store(&g_vmAway, 1);
    notifyEventAll();   /* (the threads that wait for a section look again) */
}
static void vmAwayEnd(void) {
    bool busy;
    fire_mutex_lock(&g_vmAwayM);
    fire_atomic_store(&g_vmAway, 0);
    busy = vmAwayBusy();
    if (!busy) g_vmAwayCount = 0;   /* (the requests stay in the queue: drainQueue lets go of them) */
    fire_mutex_unlock(&g_vmAwayM);
    while (busy) {   /* (with the GIL in the hand: the thread in its section gets it, until it is done) */
        gilRelease();
        fire_plat_sleepMs(0);
        gilAcquire();
        fire_mutex_lock(&g_vmAwayM);
        busy = vmAwayBusy();
        if (!busy) g_vmAwayCount = 0;
        fire_mutex_unlock(&g_vmAwayM);
    }
}

/* May the strand let go of the GIL while the native runs? Only while there are fire threads, for a library that pumps nothing (no service, no idle: no
 * callbacks into the program while it runs) and arguments without lambdas whose buffers (passed by reference) are the strand's own - not of the shared domain
 * (fire_natArg). */
static bool vmPkgAway(VmPkgLib* lib, int argc, const Value* args) {
    int i;
    if (fire_atomic_load(&g_threadsLive) == 0 || lib->service || lib->idle || g_vmPkgDepth > 1) return false;
    for (i = 0; i < argc; i++) {
        Value v = args[i];
        if (v.kind == K_Lambda) return false;
        if (v.kind == K_Buffer) {
            Owned* o = ownedOf(v);
            if (o == NULL || (FIRE_FLAGS_READ(o) & 64)) return false;
            if (o->owner && (o->owner == g_domainRoot || (o->owner->holder && (FIRE_FLAGS_READ(o->owner->holder) & 64)))) return false;
        }
    }
    return true;
}

/* Calls native `index` of `lib` with `argc` values; the result is owned by `list`. An exception is thrown (the unwinding state is set). */
static Value vmPkgCall(VmPkgLib* lib, int index, const char* name, const FireFnInfo* nat, int argc, const Value* args, OwnList* list) {
    const FireFnInfo* outerNat = g_vmPkgNat;
    fire_val stackArgs[8];
    fire_val* fargs = stackArgs;
    fire_val result;
    char error[512];
    VmPkgArena arena;
    int i, rc;
    Value r;
    memset(&arena, 0, sizeof arena);
    if (argc > 8) fargs = (fire_val*)vmPkgAlloc(&arena, (size_t)argc * sizeof(fire_val));
    for (i = 0; i < argc; i++)
        if (!vmPkgToNative(&arena, args[i], &fargs[i], name)) { vmPkgArenaFree(&arena); return Undef(); }
    error[0] = 0;
    memset(&result, 0, sizeof result);
    if (g_vmPkgPosted || fire_atomic_load(&g_vmPkgService)) vmPkgServe();
    g_vmPkgDepth++;
    g_vmPkgNat = nat;
    if (vmPkgAway(lib, argc, args)) {
        /* (a native that gets nothing of the shared domain and cannot call back runs beside the other strands - the C# VM's threads call natives at the
         * same time; fire_rt.h fire_natBegin: the main program goes away for it) */
        bool main = !g_isThread;
        if (main) vmAwayBegin();
        gilRelease();
        rc = lib->call(index, fargs, argc, &result, error, (int)sizeof error);
        gilAcquire();
        if (main) vmAwayEnd();
    } else {
        /* (with the GIL in the hand: after a native that took long the strands that wait for the GIL get their turn at once - fire_rt.h fire_natEnd) */
        int64_t t0 = fire_atomic_load(&g_threadsLive) ? fire_plat_nowMs() : 0;
        rc = lib->call(index, fargs, argc, &result, error, (int)sizeof error);
        if (t0 && fire_atomic_load(&g_gilWaiting) && fire_plat_nowMs() - t0 >= 2) gilYield();
    }
    g_vmPkgDepth--;
    g_vmPkgNat = outerNat;
    vmPkgArenaFree(&arena);
    if (g_vmPkgDeferredSet && g_vmPkgDepth <= g_vmPkgBarrier) {
        /* a callback of this native threw: thrown here, the same object (SPEC 8.1.4) */
        Value x = g_vmPkgDeferred;
        g_vmPkgDeferredSet = 0;
        g_vmPkgDeferred = Undef();
        throwValueR(x, false);
        return Undef();
    }
    if (rc == 2) {
        /* the native throws an exception of the program: "Class\nmessage" */
        char* nl = strchr(error, '\n');
        if (nl) *nl = 0;
        return fireError(error, nl ? nl + 1 : "");
    }
    if (rc != 0) {
        char text[700];
        snprintf(text, sizeof text, "%s: %s", name, error);
        fatal(text);
    }
    r = vmPkgFromNative(&result, list);
    if (g_vmPkgPosted || fire_atomic_load(&g_vmPkgService)) vmPkgServe();
    return r;
}

/* A safe point: the callbacks other threads posted, the services the libraries asked for. Callbacks have no native that waits for them here (their exceptions
 * are reported). */
static void vmPkgServe(void) {
    int barrier = g_vmPkgBarrier;
    VmPkgLib* lib;
    if (!g_vmCur || g_unwind.active) return;
    g_vmPkgBarrier = g_vmPkgDepth;
    if (fire_atomic_exchange(&g_vmPkgService, 0))
        for (lib = g_vmLibs; lib; lib = lib->next) if (!lib->failure && lib->service) lib->service();
    for (;;) {
        VmPkgPosted* p;
        int32_t i;
        vmPkgLock();
        p = g_vmPkgPosted;
        if (p) { g_vmPkgPosted = p->next; if (!g_vmPkgPosted) g_vmPkgPostedTail = &g_vmPkgPosted; }
        vmPkgUnlock();
        if (!p) break;
        if (!g_unwind.active) vmPkgRunLambda(p->handle, p->args, p->argc);
        for (i = 0; i < p->argc; i++) if (p->args[i].kind == FIRE_STRING || p->args[i].kind == FIRE_BUFFER || p->args[i].kind == FIRE_WORDS) free((void*)p->args[i].v.p);
        free(p->args);
        free(p);
    }
    g_vmPkgBarrier = barrier;
}

/* A turn of a wait of the program (Sleep, the end of the program): the libraries with `fire_pkg_idle` serve what this thread has of them (the windows they pump). */
static void vmPkgIdle(void) {
    int barrier = g_vmPkgBarrier;
    VmPkgLib* lib;
    g_vmPkgBarrier = g_vmPkgDepth;
    for (lib = g_vmLibs; lib; lib = lib->next) if (!lib->failure && lib->idle) lib->idle();
    g_vmPkgBarrier = barrier;
    vmPkgServe();
}

/* The program starts: the VM's thread is this one. */
static void vmPkgBegin(const char* programPath) {
    static char dir[1024];
    const char* slash = strrchr(programPath, '/');
#if defined(_WIN32)
    const char* back = strrchr(programPath, '\\');
    if (back && (!slash || back > slash)) slash = back;
#endif
    vmPkgNoteThread();
    if (slash) { size_t n = (size_t)(slash - programPath); if (n >= sizeof dir) n = sizeof dir - 1; memcpy(dir, programPath, n); dir[n] = 0; }
    else snprintf(dir, sizeof dir, ".");
    g_vmPkgProgramDir = dir;
    g_vmPkgDeferredSet = 0;
    g_vmPkgDepth = g_vmPkgBarrier = 0;
}

/* The program's code has ended: while a library holds it (open windows), the VM serves what is posted to it. */
static void vmPkgHoldEnd(void) {
    while ((int32_t)fire_atomic_load(&g_vmPkgHolds) > 0 && !g_unwind.active && g_vmCur && !g_vmCur->failed && !g_vmCur->terminated) {
        vmPkgIdle();
        fire_plat_sleepMs(5);
    }
}

/* The program has ended: the lambdas that crossed are let go, every library forgets what the program left behind (fire_pkg_reset). */
static void vmPkgEnd(void) {
    VmPkgLib* lib;
    uint32_t i;
    for (lib = g_vmLibs; lib; lib = lib->next) if (!lib->failure && lib->reset) lib->reset();
    for (;;) {
        VmPkgPosted* p = g_vmPkgPosted;
        if (!p) break;
        g_vmPkgPosted = p->next;
        free(p->args);
        free(p);
    }
    g_vmPkgPostedTail = &g_vmPkgPosted;
    for (i = 0; i < g_vmPkgLamCount; i++) release(g_vmPkgLams[i]);
    g_vmPkgLamCount = 0;
    fire_atomic_store(&g_vmPkgHolds, 0);
    fire_atomic_store(&g_vmPkgService, 0);
    g_vmPkgDeferredSet = 0;
    g_vmPkgUsesGraphics = false;
}
