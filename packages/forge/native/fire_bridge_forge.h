/* fire native bridge "forge" (C): the natives behind `#import "forge"` (docs/packages/forge.md) - fire source or a project to a program file, its diagnostics as
 * data, the command line of forge - in this process, through libforge (native/forge/forge.c as a library; its interface native/abi/fire_embed.h).
 *
 * libforge is a shared library (libfireforge.so, fireforge.dll, libfireforge.dylib) that forge builds (`forge build-embed <folder>`, or on its own for a program
 * that imports this package); this bridge loads it on first use from FIRE_EMBED_PATH, the folders of FIRE_PKG_PATH or the folder of the running program. It is
 * the same in the VM (a package library) and in a native build: libforge does not use the runtime of fire, so it does not matter whose runtime runs the bridge.
 * The few functions of fire_embed.h it calls are declared here as pointers (a host that loads the library looks them up by name). A translation is a handle
 * in a table of this bridge; the calls are serialised (the lock of the library in the VM, of the package in a native build). */
#ifndef FIRE_BRIDGE_FORGE_H
#define FIRE_BRIDGE_FORGE_H

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

/* ---- libforge ---- */
typedef struct FireForge FireForge;
typedef struct FireCompilation FireCompilation;
typedef struct FireForgeApi {
    int (*version)(void);
    FireForge* (*forgeNew)(const char*, const char*, const char*);
    FireCompilation* (*compile)(FireForge*, const char*, const char*, size_t, int);
    int (*ok)(const FireCompilation*);
    const uint8_t* (*program)(const FireCompilation*, size_t*);
    const char* (*packagePath)(const FireCompilation*);
    int (*diagCount)(const FireCompilation*);
    const char* (*diag)(const FireCompilation*, int, int);
    int (*diagLine)(const FireCompilation*, int);
    int (*diagColumn)(const FireCompilation*, int);
    void (*free)(FireCompilation*);
    int (*main)(FireForge*, int, char**);
} FireForgeApi;
static FireForgeApi g_forgeApi;
static FireForge* g_forgeForge = NULL;
static int g_forgeState = 0;   /* 0 not tried, 1 loaded, -1 failed (g_forgeWhy) */
static char g_forgeWhy[600];

static int fire_forge_ready(void) {
    void* lib;
    if (g_forgeState) return g_forgeState > 0;
    g_forgeState = -1;
    if (!(lib = fire_embed_load("fireforge", g_forgeWhy, sizeof g_forgeWhy))) return 0;
#define FIRE_FORGE_SYM(field, name) if (!(*(void**)&g_forgeApi.field = fire_embed_symbol(lib, name))) { snprintf(g_forgeWhy, sizeof g_forgeWhy, "libforge has no %s", name); return 0; }
    FIRE_FORGE_SYM(version, "fire_embed_version")
    FIRE_FORGE_SYM(forgeNew, "fire_forge_new")
    FIRE_FORGE_SYM(compile, "fire_forge_compile")
    FIRE_FORGE_SYM(ok, "fire_compilation_ok")
    FIRE_FORGE_SYM(program, "fire_compilation_program")
    FIRE_FORGE_SYM(packagePath, "fire_compilation_package_path")
    FIRE_FORGE_SYM(diagCount, "fire_compilation_diagnostic_count")
    FIRE_FORGE_SYM(diag, "fire_compilation_diagnostic")
    FIRE_FORGE_SYM(diagLine, "fire_compilation_diagnostic_line")
    FIRE_FORGE_SYM(diagColumn, "fire_compilation_diagnostic_column")
    FIRE_FORGE_SYM(free, "fire_compilation_free")
    FIRE_FORGE_SYM(main, "fire_forge_main")
#undef FIRE_FORGE_SYM
    if (g_forgeApi.version() != 1) { snprintf(g_forgeWhy, sizeof g_forgeWhy, "libforge has version %d of fire_embed.h, this package needs 1", g_forgeApi.version()); return 0; }
    if (!(g_forgeForge = g_forgeApi.forgeNew(NULL, NULL, NULL))) { snprintf(g_forgeWhy, sizeof g_forgeWhy, "libforge could not start"); return 0; }
    g_forgeState = 1;
    return 1;
}

/* a fire string as UTF-8 (malloc'd; NULL for undefined) */
static char* fire_forge_utf8(Value v, size_t* length) {
    uint32_t n = v.kind == K_String ? strOf(v)->length : 0;
    char* out;
    if (v.kind != K_String) { if (length) *length = 0; return NULL; }
    out = (char*)malloc((size_t)n * 3 + 5);
    if (!out) return NULL;
    strToUtf8(v, out, n * 3 + 5);
    if (length) *length = strlen(out);
    return out;
}
static Value fire_forge_str(const char* s, OwnList* list) { return newStrFromUtf8((const uint8_t*)(s ? s : ""), strlen(s ? s : ""), list); }

/* ---- the table of the translations ---- */
static FireCompilation** g_forgeTable = NULL;
static size_t g_forgeCount = 0;
static FireCompilation* fire_forge_get(Value h) {
    if (h.kind != K_Int || h.i < 1 || (size_t)h.i > g_forgeCount) return NULL;
    return g_forgeTable[h.i - 1];
}

static Value fire_forge_Available(void) { return Bool(fire_forge_ready()); }
static Value fire_forge_LoadError(OwnList* list) { fire_forge_ready(); return fire_forge_str(g_forgeState > 0 ? "" : g_forgeWhy, list); }

/* (path, source or undefined, mode: -1 default, 0 debug, 1 release, 2 performance) -> a handle, 0 when libforge is missing */
static Value fire_forge_Compile(Value path, Value source, Value mode) {
    char* p;
    char* text;
    size_t length = 0, i;
    FireCompilation* c;
    if (!fire_forge_ready()) return Int(0);
    p = fire_forge_utf8(path, NULL);
    text = fire_forge_utf8(source, &length);
    c = g_forgeApi.compile(g_forgeForge, p ? p : "script.fire", text, length, mode.kind == K_Int ? (int)mode.i : -1);
    free(p);
    free(text);
    if (!c) return Int(0);
    for (i = 0; i < g_forgeCount; i++) if (!g_forgeTable[i]) { g_forgeTable[i] = c; return Int((int64_t)i + 1); }
    {
        FireCompilation** t = (FireCompilation**)realloc(g_forgeTable, sizeof(FireCompilation*) * (g_forgeCount + 1));
        if (!t) { g_forgeApi.free(c); return Int(0); }
        g_forgeTable = t;
        g_forgeTable[g_forgeCount++] = c;
    }
    return Int((int64_t)g_forgeCount);
}
static Value fire_forge_Ok(Value h) { FireCompilation* c = fire_forge_get(h); return Bool(c && g_forgeApi.ok(c)); }
static Value fire_forge_Program(Value h, OwnList* list) {
    FireCompilation* c = fire_forge_get(h);
    size_t n = 0;
    const uint8_t* p = c ? g_forgeApi.program(c, &n) : NULL;
    Buf* b;
    if (!p) return Undef();
    b = allocBuf((uint32_t)n, list);
    if (n) memcpy(b->data, p, n);
    return BufV(b);
}
static Value fire_forge_PackagePath(Value h, OwnList* list) { FireCompilation* c = fire_forge_get(h); return fire_forge_str(c ? g_forgeApi.packagePath(c) : "", list); }
static Value fire_forge_DiagCount(Value h) { FireCompilation* c = fire_forge_get(h); return Int(c ? g_forgeApi.diagCount(c) : 0); }
static Value fire_forge_DiagText(Value h, Value index, Value what, OwnList* list) {
    FireCompilation* c = fire_forge_get(h);
    return fire_forge_str(c && index.kind == K_Int && what.kind == K_Int ? g_forgeApi.diag(c, (int)index.i, (int)what.i) : "", list);
}
static Value fire_forge_DiagLine(Value h, Value index) { FireCompilation* c = fire_forge_get(h); return Int(c && index.kind == K_Int ? g_forgeApi.diagLine(c, (int)index.i) : 0); }
static Value fire_forge_DiagColumn(Value h, Value index) { FireCompilation* c = fire_forge_get(h); return Int(c && index.kind == K_Int ? g_forgeApi.diagColumn(c, (int)index.i) : 0); }
static Value fire_forge_Free(Value h) {
    FireCompilation* c = fire_forge_get(h);
    if (!c) return Bool(0);
    g_forgeApi.free(c);
    g_forgeTable[h.i - 1] = NULL;
    return Bool(1);
}
/* the command line of forge in this process (its output goes to this process's stdout and stderr) -> its exit code; -1 when libforge is missing */
static Value fire_forge_Main(Value args) {
    char** argv;
    uint32_t n, i;
    int code;
    if (!fire_forge_ready()) return Int(-1);
    n = args.kind == K_Array ? arrOf(args)->length : 0;
    argv = (char**)calloc((size_t)n + 2, sizeof(char*));
    if (!argv) return Int(-1);
    argv[0] = (char*)"forge";
    for (i = 0; i < n; i++) {
        char* s = fire_forge_utf8(arrItems(arrOf(args))[i], NULL);
        argv[1 + i] = s ? s : (char*)calloc(1, 1);
    }
    fflush(stdout);
    code = g_forgeApi.main(g_forgeForge, (int)n + 1, argv);
    for (i = 0; i < n; i++) free(argv[1 + i]);
    free(argv);
    return Int(code);
}

/* the end of a program (the VM keeps the library for the next one): its translations go; libforge stays loaded */
static void fire_forge_reset(void) {
    size_t i;
    for (i = 0; i < g_forgeCount; i++) if (g_forgeTable[i]) g_forgeApi.free(g_forgeTable[i]);
    free(g_forgeTable);
    g_forgeTable = NULL;
    g_forgeCount = 0;
}

#endif
