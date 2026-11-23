/* fire native bridge "script" (C): the natives behind `#import "script"` (docs/packages/script.md) - a program file (from the package forge) run to its end in
 * this process by libfirevm (native/vm/fire.c as a library; its interface native/abi/fire_embed.h): its arguments, what it writes, its exit code and an
 * exception nothing caught come back as data.
 *
 * libfirevm is a shared library (libfirevm.so, firevm.dll, libfirevm.dylib) that forge builds (`forge build-embed <folder>`, or on its own for a program that
 * imports this package), loaded like libforge (packages/forge: FIRE_EMBED_PATH, FIRE_PKG_PATH, the folder of the program). It carries a runtime of fire of its
 * own (built with hidden symbols): a script never shares the objects, threads or globals of the host - in the VM and in a native build alike; what crosses is
 * text and bytes. Its runtime is one per process: one script runs at a time (a second run waits). A run is a handle in a table of this bridge. */
#ifndef FIRE_BRIDGE_SCRIPT_H
#define FIRE_BRIDGE_SCRIPT_H

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#include <windows.h>
#else
#include <dlfcn.h>
#include <unistd.h>
#endif

/* ---- loading a library of fire_embed.h (the same in packages/forge and packages/script: one copy in a build with both) ---- */
#ifndef FIRE_EMBED_LOADER
#define FIRE_EMBED_LOADER 1
#if defined(_WIN32)
#define FIRE_EMBED_SEP ';'
#define FIRE_EMBED_PREFIX ""
#define FIRE_EMBED_EXT ".dll"
#elif defined(__APPLE__)
#define FIRE_EMBED_SEP ':'
#define FIRE_EMBED_PREFIX "lib"
#define FIRE_EMBED_EXT ".dylib"
#else
#define FIRE_EMBED_SEP ':'
#define FIRE_EMBED_PREFIX "lib"
#define FIRE_EMBED_EXT ".so"
#endif
static void* fire_embed_open(const char* path) {
#if defined(_WIN32)
    wchar_t w[2048];
    if (!MultiByteToWideChar(CP_UTF8, 0, path, -1, w, 2048)) return NULL;
    return (void*)LoadLibraryExW(w, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
#else
    return dlopen(path, RTLD_NOW | RTLD_LOCAL);
#endif
}
static void* fire_embed_symbol(void* lib, const char* name) {
#if defined(_WIN32)
    return (void*)GetProcAddress((HMODULE)lib, name);
#else
    return dlsym(lib, name);
#endif
}
/* the folder of the running program (the VM fire, or the native program) */
static int fire_embed_selfFolder(char* out, size_t cap) {
    char* slash;
#if defined(_WIN32)
    wchar_t w[2048];
    char* p;
    DWORD n = GetModuleFileNameW(NULL, w, 2048);
    if (n == 0 || n >= 2048 || !WideCharToMultiByte(CP_UTF8, 0, w, -1, out, (int)cap, NULL, NULL)) return 0;
    for (p = out; *p; p++) if (*p == '\\') *p = '/';
#elif defined(__linux__)
    ssize_t n = readlink("/proc/self/exe", out, cap - 1);
    if (n <= 0) return 0;
    out[n] = 0;
#else
    (void)cap;
    out[0] = 0;
    return 0;
#endif
    slash = strrchr(out, '/');
    if (!slash) return 0;
    *slash = 0;
    return 1;
}
/* loads `<prefix><stem><ext>` from the folders of FIRE_EMBED_PATH, FIRE_PKG_PATH, then the folder of the program; NULL: `why` says where it looked */
static void* fire_embed_load(const char* stem, char* why, size_t cap) {
    static const char* const vars[2] = { "FIRE_EMBED_PATH", "FIRE_PKG_PATH" };
    char file[300], path[4400], self[4096];
    int v;
    void* lib;
    snprintf(file, sizeof file, FIRE_EMBED_PREFIX "%s" FIRE_EMBED_EXT, stem);
    for (v = 0; v < 2; v++) {
        const char* list = getenv(vars[v]);
        while (list && *list) {
            const char* end = strchr(list, FIRE_EMBED_SEP);
            size_t n = end ? (size_t)(end - list) : strlen(list);
            if (n > 0 && n < 4000) {
                snprintf(path, sizeof path, "%.*s/%s", (int)n, list, file);
                if ((lib = fire_embed_open(path)) != NULL) return lib;
            }
            list = end ? end + 1 : NULL;
        }
    }
    if (fire_embed_selfFolder(self, sizeof self)) {
        snprintf(path, sizeof path, "%s/%s", self, file);
        if ((lib = fire_embed_open(path)) != NULL) return lib;
    }
    snprintf(why, cap, "%s was not found (FIRE_EMBED_PATH, FIRE_PKG_PATH, the folder of the program): forge builds it with `forge build-embed <folder>` - "
        "and on its own for a program that imports forge or script", file);
    return NULL;
}
#endif

/* ---- libfirevm ---- */
typedef struct FireVm FireVm;
typedef struct FireRun FireRun;
typedef struct FireVmApi {
    int (*version)(void);
    FireVm* (*vmNew)(void);
    void (*setPackagePath)(FireVm*, const char*);
    FireRun* (*run)(FireVm*, const char*, const uint8_t*, size_t, int, const char* const*);
    int (*status)(const FireRun*);
    int (*exitCode)(const FireRun*);
    const char* (*text)(const FireRun*, int);
    void (*free)(FireRun*);
} FireVmApi;
static FireVmApi g_scriptApi;
static FireVm* g_scriptVm = NULL;
static int g_scriptState = 0;   /* 0 not tried, 1 loaded, -1 failed (g_scriptWhy) */
static char g_scriptWhy[600];

static int fire_script_ready(void) {
    void* lib;
    if (g_scriptState) return g_scriptState > 0;
    g_scriptState = -1;
    if (!(lib = fire_embed_load("firevm", g_scriptWhy, sizeof g_scriptWhy))) return 0;
#define FIRE_SCRIPT_SYM(field, name) if (!(*(void**)&g_scriptApi.field = fire_embed_symbol(lib, name))) { snprintf(g_scriptWhy, sizeof g_scriptWhy, "libfirevm has no %s", name); return 0; }
    FIRE_SCRIPT_SYM(version, "fire_embed_version")
    FIRE_SCRIPT_SYM(vmNew, "fire_vm_new")
    FIRE_SCRIPT_SYM(setPackagePath, "fire_vm_set_package_path")
    FIRE_SCRIPT_SYM(run, "fire_vm_run")
    FIRE_SCRIPT_SYM(status, "fire_run_status")
    FIRE_SCRIPT_SYM(exitCode, "fire_run_exit_code")
    FIRE_SCRIPT_SYM(text, "fire_run_text")
    FIRE_SCRIPT_SYM(free, "fire_run_free")
#undef FIRE_SCRIPT_SYM
    if (g_scriptApi.version() != 1) { snprintf(g_scriptWhy, sizeof g_scriptWhy, "libfirevm has version %d of fire_embed.h, this package needs 1", g_scriptApi.version()); return 0; }
    if (!(g_scriptVm = g_scriptApi.vmNew())) { snprintf(g_scriptWhy, sizeof g_scriptWhy, "libfirevm could not start"); return 0; }
    g_scriptState = 1;
    return 1;
}

/* a fire string as UTF-8 (malloc'd; "" for anything else) */
static char* fire_script_utf8(Value v) {
    uint32_t n = v.kind == K_String ? strOf(v)->length : 0;
    char* out = (char*)malloc((size_t)n * 3 + 5);
    if (!out) return NULL;
    out[0] = 0;
    if (v.kind == K_String) strToUtf8(v, out, n * 3 + 5);
    return out;
}

/* ---- the table of the runs ---- */
static FireRun** g_scriptTable = NULL;
static size_t g_scriptCount = 0;
static FireRun* fire_script_get(Value h) {
    if (h.kind != K_Int || h.i < 1 || (size_t)h.i > g_scriptCount) return NULL;
    return g_scriptTable[h.i - 1];
}

static Value fire_script_Available(void) { return Bool(fire_script_ready()); }
static Value fire_script_LoadError(OwnList* list) {
    const char* s;
    fire_script_ready();
    s = g_scriptState > 0 ? "" : g_scriptWhy;
    return newStrFromUtf8((const uint8_t*)s, strlen(s), list);
}

/* (program: the bytes of a program file, packagePath: the folders of its package libraries, name, args: string[]) -> a handle; 0 when libfirevm is missing */
static Value fire_script_Run(Value program, Value packagePath, Value name, Value args) {
    FireBytes bytes;
    char* path;
    char* title;
    char** argv;
    uint32_t n, i;
    FireRun* r;
    if (!bytesArg(program, "Scripting.Script.Run", &bytes)) return Undef();
    if (!fire_script_ready()) return Int(0);
    n = args.kind == K_Array ? arrOf(args)->length : 0;
    argv = (char**)calloc((size_t)n + 1, sizeof(char*));
    path = fire_script_utf8(packagePath);
    title = fire_script_utf8(name);
    if (!argv || !path || !title) { free(argv); free(path); free(title); return Int(0); }
    for (i = 0; i < n; i++) argv[i] = fire_script_utf8(arrItems(arrOf(args))[i]);
    fflush(stdout);
    g_scriptApi.setPackagePath(g_scriptVm, path);
    r = g_scriptApi.run(g_scriptVm, title, bytes.data, bytes.length, (int)n, (const char* const*)argv);
    for (i = 0; i < n; i++) free(argv[i]);
    free(argv);
    free(path);
    free(title);
    if (!r) return Int(0);
    for (i = 0; i < g_scriptCount; i++) if (!g_scriptTable[i]) { g_scriptTable[i] = r; return Int((int64_t)i + 1); }
    {
        FireRun** t = (FireRun**)realloc(g_scriptTable, sizeof(FireRun*) * (g_scriptCount + 1));
        if (!t) { g_scriptApi.free(r); return Int(0); }
        g_scriptTable = t;
        g_scriptTable[g_scriptCount++] = r;
    }
    return Int((int64_t)g_scriptCount);
}
static Value fire_script_Status(Value h) { FireRun* r = fire_script_get(h); return Int(r ? g_scriptApi.status(r) : 4); }
static Value fire_script_ExitCode(Value h) { FireRun* r = fire_script_get(h); return Int(r ? g_scriptApi.exitCode(r) : 2); }
/* what: 0 output, 1 errors, 2 the type of the exception, 3 its message, 4 its stack trace, 5 why it was not loaded */
static Value fire_script_Text(Value h, Value what, OwnList* list) {
    FireRun* r = fire_script_get(h);
    const char* s = r && what.kind == K_Int ? g_scriptApi.text(r, (int)what.i) : "";
    return newStrFromUtf8((const uint8_t*)s, strlen(s), list);
}
static Value fire_script_Free(Value h) {
    FireRun* r = fire_script_get(h);
    if (!r) return Bool(0);
    g_scriptApi.free(r);
    g_scriptTable[h.i - 1] = NULL;
    return Bool(1);
}

/* the end of a program: its runs go; libfirevm stays loaded */
static void fire_script_reset(void) {
    size_t i;
    for (i = 0; i < g_scriptCount; i++) if (g_scriptTable[i]) g_scriptApi.free(g_scriptTable[i]);
    free(g_scriptTable);
    g_scriptTable = NULL;
    g_scriptCount = 0;
}

#endif
