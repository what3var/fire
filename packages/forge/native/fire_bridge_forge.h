/* fire native bridge "forge" (C): the natives behind `#import "forge"` (docs/packages/forge.md) - fire source or a project to a program file, its diagnostics as
 * data, the command line of forge - in this process, through libforge (native/forge/forge.c as a library; its interface native/abi/fire_embed.h).
 *
 * libforge is a unit of this package (`native.units`: native/libforge.c, the whole of forge as one C file - `forge amalgamate`): forge-c compiles it on its
 * own and links it into a native program (for any target it builds for) and into the library of the package for the VM, and defines FIRE_PKG_UNITS_FORGE -
 * then the functions of fire_embed.h are called directly. A forge that does not know `units` (the C# forge) leaves the define out: then this bridge loads
 * libforge as a shared library (libfireforge.so, fireforge.dll, libfireforge.dylib - `forge build-embed <folder>`) on first use from FIRE_EMBED_PATH, the
 * folders of FIRE_PKG_PATH, the folder of this module or of the running program. It is the same in the VM (a package library) and in a native build: libforge
 * does not use the runtime of fire, so it does not matter whose runtime runs the bridge. A translation is a handle in a table of this bridge; the calls are
 * serialised (the lock of the library in the VM, of the package in a native build). */
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
/* the folder of the module this bridge is in: the library of the package in the VM (in a packed program the folder its payload was written to, with the
 * libraries it carries), the program in a native build */
#if !defined(_WIN32) && !defined(__APPLE__) && !defined(__USE_GNU) && !defined(__ANDROID__) && defined(__GLIBC__)
/* (glibc declares dladdr only with _GNU_SOURCE: its declaration with the layout of Dl_info) */
typedef struct FireEmbedDlInfo { const char* dli_fname; void* dli_fbase; const char* dli_sname; void* dli_saddr; } FireEmbedDlInfo;
extern int dladdr(const void* address, FireEmbedDlInfo* info);
#define FIRE_EMBED_DLINFO FireEmbedDlInfo
#elif !defined(_WIN32)
#define FIRE_EMBED_DLINFO Dl_info
#endif
static int fire_embed_moduleFolder(char* out, size_t cap) {
    char* slash;
#if defined(_WIN32)
    HMODULE m = NULL;
    wchar_t w[2048];
    char* p;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)(void*)&fire_embed_moduleFolder, &m)) return 0;
    if (!GetModuleFileNameW(m, w, 2048) || !WideCharToMultiByte(CP_UTF8, 0, w, -1, out, (int)cap, NULL, NULL)) return 0;
    for (p = out; *p; p++) if (*p == '\\') *p = '/';
#else
    FIRE_EMBED_DLINFO info;
    if (!dladdr((void*)&fire_embed_moduleFolder, &info) || !info.dli_fname || strlen(info.dli_fname) >= cap) return 0;
    strcpy(out, info.dli_fname);
#endif
    slash = strrchr(out, '/');
    if (!slash) return 0;
    *slash = 0;
    return 1;
}
/* loads `<prefix><stem><ext>` from the folders of FIRE_EMBED_PATH, FIRE_PKG_PATH, the folder of this module, then of the program; NULL: `why` says where it
 * looked */
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
    if (fire_embed_moduleFolder(self, sizeof self)) {
        snprintf(path, sizeof path, "%s/%s", self, file);
        if ((lib = fire_embed_open(path)) != NULL) return lib;
    }
    if (fire_embed_selfFolder(self, sizeof self)) {
        snprintf(path, sizeof path, "%s/%s", self, file);
        if ((lib = fire_embed_open(path)) != NULL) return lib;
    }
    snprintf(why, cap, "%s was not found (FIRE_EMBED_PATH, FIRE_PKG_PATH, the folder of the package library or of the program): forge builds it with "
        "`forge build-embed <folder>` - and on its own for a program that imports script", file);
    return NULL;
}
#endif

/* ---- libforge ---- */
typedef struct FireForge FireForge;
typedef struct FireCompilation FireCompilation;
typedef void (*FireForgeWriteFn)(void* user, int stream, const char* data, size_t length);
#ifdef FIRE_PKG_UNITS_FORGE
/* (libforge is a unit of this program or library: the functions of fire_embed.h it calls) */
extern int fire_embed_version(void);
extern FireForge* fire_forge_new(const char* tables, const char* packages, const char* native);
extern FireCompilation* fire_forge_compile(FireForge* forge, const char* path, const char* source, size_t length, int mode);
extern int fire_compilation_ok(const FireCompilation* c);
extern const uint8_t* fire_compilation_program(const FireCompilation* c, size_t* length);
extern const char* fire_compilation_package_path(const FireCompilation* c);
extern int fire_compilation_diagnostic_count(const FireCompilation* c);
extern const char* fire_compilation_diagnostic(const FireCompilation* c, int index, int what);
extern int fire_compilation_diagnostic_line(const FireCompilation* c, int index);
extern int fire_compilation_diagnostic_column(const FireCompilation* c, int index);
extern void fire_compilation_free(FireCompilation* c);
extern int fire_forge_main(FireForge* forge, int argc, char** argv);
extern void fire_forge_set_output(FireForge* forge, FireForgeWriteFn write, void* user);
#endif
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
    void (*setOutput)(FireForge*, FireForgeWriteFn, void*);   /* (NULL: a libforge from before it: Run writes to the process's streams) */
} FireForgeApi;
static FireForgeApi g_forgeApi;
static FireForge* g_forgeForge = NULL;
static int g_forgeState = 0;   /* 0 not tried, 1 loaded, -1 failed (g_forgeWhy) */
static char g_forgeWhy[600];

static int fire_forge_ready(void) {
#ifndef FIRE_PKG_UNITS_FORGE
    void* lib;
#endif
    if (g_forgeState) return g_forgeState > 0;
    g_forgeState = -1;
#ifdef FIRE_PKG_UNITS_FORGE
    g_forgeApi.version = fire_embed_version;
    g_forgeApi.forgeNew = fire_forge_new;
    g_forgeApi.compile = fire_forge_compile;
    g_forgeApi.ok = fire_compilation_ok;
    g_forgeApi.program = fire_compilation_program;
    g_forgeApi.packagePath = fire_compilation_package_path;
    g_forgeApi.diagCount = fire_compilation_diagnostic_count;
    g_forgeApi.diag = fire_compilation_diagnostic;
    g_forgeApi.diagLine = fire_compilation_diagnostic_line;
    g_forgeApi.diagColumn = fire_compilation_diagnostic_column;
    g_forgeApi.free = fire_compilation_free;
    g_forgeApi.main = fire_forge_main;
    g_forgeApi.setOutput = fire_forge_set_output;
#else
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
    *(void**)&g_forgeApi.setOutput = fire_embed_symbol(lib, "fire_forge_set_output");
#endif
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
/* ---- the command line of forge (fire_forge_main) ---- */
/* what Run collected: [0] stdout, [1] stderr */
typedef struct FireForgeText { char* p; size_t len, cap; } FireForgeText;
static FireForgeText g_forgeRunText[2];
static void fire_forge_collect(void* user, int stream, const char* data, size_t n) {
    FireForgeText* t = &g_forgeRunText[stream == 2 ? 1 : 0];
    (void)user;
    if (t->len + n + 1 > t->cap) {
        size_t cap = t->cap ? t->cap : 1024;
        char* p;
        while (cap < t->len + n + 1) cap *= 2;
        if (!(p = (char*)realloc(t->p, cap))) return;
        t->p = p; t->cap = cap;
    }
    memcpy(t->p + t->len, data, n);
    t->len += n;
    t->p[t->len] = 0;
}
/* args -> exit code: `collect` 0 - its output goes to this process's stdout and stderr, 1 - into g_forgeRunText; -1 when libforge is missing */
static int fire_forge_command(Value args, int collect) {
    char** argv;
    uint32_t n, i;
    int code;
    if (!fire_forge_ready()) return -1;
    n = args.kind == K_Array ? arrOf(args)->length : 0;
    argv = (char**)calloc((size_t)n + 2, sizeof(char*));
    if (!argv) return -1;
    argv[0] = (char*)"forge";
    for (i = 0; i < n; i++) {
        char* s = fire_forge_utf8(arrItems(arrOf(args))[i], NULL);
        argv[1 + i] = s ? s : (char*)calloc(1, 1);
    }
    fflush(stdout);
    fflush(stderr);
    g_forgeRunText[0].len = g_forgeRunText[1].len = 0;
    if (collect && g_forgeApi.setOutput) g_forgeApi.setOutput(g_forgeForge, fire_forge_collect, NULL);
    code = g_forgeApi.main(g_forgeForge, (int)n + 1, argv);
    if (collect && g_forgeApi.setOutput) g_forgeApi.setOutput(g_forgeForge, NULL, NULL);
    for (i = 0; i < n; i++) free(argv[1 + i]);
    free(argv);
    return code;
}
/* the command line of forge in this process (its output goes to this process's stdout and stderr) -> its exit code; -1 when libforge is missing */
static Value fire_forge_Main(Value args) { return Int(fire_forge_command(args, 0)); }
/* the same with its output collected (RunText) */
static Value fire_forge_Run(Value args) { return Int(fire_forge_command(args, 1)); }
/* what the last Run wrote: 0 stdout, 1 stderr */
static Value fire_forge_RunText(Value which, OwnList* list) {
    const FireForgeText* t = &g_forgeRunText[which.kind == K_Int && which.i == 1 ? 1 : 0];
    return newStrFromUtf8((const uint8_t*)(t->p ? t->p : ""), t->len, list);
}

/* the end of a program (the VM keeps the library for the next one): its translations go; libforge stays loaded */
static void fire_forge_reset(void) {
    size_t i;
    for (i = 0; i < g_forgeCount; i++) if (g_forgeTable[i]) g_forgeApi.free(g_forgeTable[i]);
    free(g_forgeTable);
    g_forgeTable = NULL;
    g_forgeCount = 0;
    for (i = 0; i < 2; i++) { free(g_forgeRunText[i].p); g_forgeRunText[i].p = NULL; g_forgeRunText[i].len = g_forgeRunText[i].cap = 0; }
}

#endif
