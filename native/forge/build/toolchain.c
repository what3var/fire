/* native/forge/build/toolchain.c - the toolchains of a native build (Phase D 2, commit 6f21d1a4): src/fire.Native/Toolchain.cs (ToolchainDef with its built-in
 * toolchains, WithBase, EffectiveCompiler, CCompilerFor; ToolchainDetector.Find/FindC), ToolchainSetup.cs (Root, SearchDirectories, FindProgram, Detect,
 * LoadMachineToolchain), MachineToolchains.cs (the compilers the machine chose for building and publishing: toolchain.json), Toolchains/ToolchainDriver.cs and
 * Toolchains/WatcomToolchain.cs (Open Watcom: its command line, where it is, its environment, the startup code), NativeConfig.cs (fire.native.json: the
 * switches, the targets of the configuration - TargetDef.Apply -, ResolveTarget, ResolveToolchain), src/fire/Runtime/TargetProfile.cs (AllowsToolchain) and
 * from src/fire.Compiler/PackageLibrary.cs OptimizingToolchain. POSIX only for now (Linux, macOS): a build on Windows is not ported yet. */

/* ---- ToolchainDef ---- */
typedef struct NbToolchain {
    const char* extends;
    const char* kind;           /* gcc, clang, msvc, tcc, emscripten, watcom, custom, files; NULL: gcc */
    const char* compiler;
    const char* std;
    const char* cstd;
    const char* ccompiler;
    const char* optimization;
    int strip;                  /* bool?: -1 null, 0 false, 1 true */
    FcList* args;               /* NULL: null */
    FcList* libs;
    FcList* includeDirs;
    const char* command;
    const char* layout;
    const char* buildCommand;
    bool machineChoice;
} NbToolchain;

static FcList* nbStrings(FcCtx* c, int n, ...) {
    FcList* l = fcListNew(c);
    va_list ap;
    int i;
    va_start(ap, n);
    for (i = 0; i < n; i++) fcListAdd(c, l, (void*)va_arg(ap, const char*));
    va_end(ap);
    return l;
}
static NbToolchain* nbToolchainNew(FcCtx* c) { NbToolchain* t = (NbToolchain*)fcAlloc(c, sizeof *t); t->strip = -1; return t; }
static const char* nbKind(const NbToolchain* t) { return t->kind ? t->kind : "gcc"; }

/* The built-in toolchains, in the order of ToolchainDef.BuiltIn (the drivers - Open Watcom - after the first ones). */
static const char* const nbBuiltInNames[] = { "gcc", "clang", "msvc", "emscripten", "tcc", "files", "esp-idf", "watcom" };
#define NB_BUILTIN_COUNT 8
static NbToolchain* nbBuiltIn(FcCtx* c, const char* name) {
    NbToolchain* t;
    int i;
    for (i = 0; i < NB_BUILTIN_COUNT; i++) if (fcAsciiIEq(nbBuiltInNames[i], name)) break;
    if (i == NB_BUILTIN_COUNT) return NULL;
    t = nbToolchainNew(c);
    switch (i) {
        case 0: t->kind = "gcc"; t->compiler = "g++"; t->std = "c++17"; t->optimization = "-O2"; t->args = nbStrings(c, 2, "-Wall", "-Wextra"); break;
        case 1: t->kind = "clang"; t->compiler = "clang++"; t->std = "c++17"; t->optimization = "-O2"; t->args = nbStrings(c, 2, "-Wall", "-Wextra"); break;
        case 2: t->kind = "msvc"; t->compiler = "cl"; t->std = "c++17"; t->optimization = "/O2"; t->args = nbStrings(c, 2, "/nologo", "/EHsc"); break;
        /* Emscripten (emsdk): C++ to WebAssembly - exceptions on, ASYNCIFY for `Sleep` and the window loop, the .wasm inside the page */
        case 3: t->kind = "emscripten"; t->compiler = "em++"; t->std = "c++17"; t->cstd = "gnu11"; t->optimization = "-O2";
            t->args = nbStrings(c, 7, "-Wall", "-Wextra", "-fexceptions", "-sASYNCIFY", "-sALLOW_MEMORY_GROWTH=1", "-sSINGLE_FILE=1", "-sMINIFY_HTML=0"); break;
        /* TinyCC: C only; -w: it warns about every call of a function that does not return */
        case 4: t->kind = "tcc"; t->compiler = "tcc"; t->ccompiler = "tcc"; t->cstd = "c11"; t->optimization = "-O2"; t->args = nbStrings(c, 1, "-w"); break;
        case 5: t->kind = "files"; t->layout = "flat"; break;
        case 6: t->kind = "files"; t->layout = "idf-component"; break;
        /* WatcomToolchain.Default (the direction - -Os or -Ot - comes from the build) */
        case 7: t->kind = "watcom"; t->compiler = "owcc"; t->ccompiler = "owcc"; t->cstd = "c99"; break;
    }
    return t;
}

/* This toolchain with the fields it leaves open taken from b. */
static NbToolchain* nbWithBase(FcCtx* c, const NbToolchain* t, const NbToolchain* b) {
    NbToolchain* r;
    if (!b) return (NbToolchain*)t;
    r = nbToolchainNew(c);
    r->extends = t->extends;
    r->kind = t->kind ? t->kind : b->kind;
    r->compiler = t->compiler ? t->compiler : b->compiler;
    r->std = t->std ? t->std : b->std;
    r->cstd = t->cstd ? t->cstd : b->cstd;
    r->ccompiler = t->ccompiler ? t->ccompiler : t->compiler ? NULL : b->ccompiler;
    r->optimization = t->optimization ? t->optimization : b->optimization;
    r->strip = t->strip >= 0 ? t->strip : b->strip;
    r->args = t->args ? t->args : b->args;
    r->libs = t->libs ? t->libs : b->libs;
    r->includeDirs = t->includeDirs ? t->includeDirs : b->includeDirs;
    r->command = t->command ? t->command : b->command;
    r->layout = t->layout ? t->layout : b->layout;
    r->buildCommand = t->buildCommand ? t->buildCommand : b->buildCommand;
    r->machineChoice = t->machineChoice || b->machineChoice;
    return r;
}
/* `new ToolchainDef { Extends = t.Extends }.WithBase(t)`: a copy */
static NbToolchain* nbCopy(FcCtx* c, const NbToolchain* t) {
    NbToolchain* e = nbToolchainNew(c);
    e->extends = t->extends;
    return nbWithBase(c, e, t);
}

static bool nbIsWatcom(const char* kind) { return fcAsciiIEq(kind, "watcom"); }

/* The compiler to run (the default of the kind when none is named). */
static const char* nbEffectiveCompiler(const NbToolchain* t) {
    const char* k = nbKind(t);
    if (t->compiler) return t->compiler;
    if (nbIsWatcom(k)) return "owcc";
    return streq0(k, "clang") ? "clang++" : streq0(k, "msvc") ? "cl" : streq0(k, "emscripten") ? "em++" : streq0(k, "tcc") ? "tcc" : "g++";
}
/* Whether the toolchain compiles C++ too (TinyCC and Open Watcom do not). */
static bool nbCompilesCpp(const NbToolchain* t) { return !streq0(nbKind(t), "tcc") && !nbIsWatcom(nbKind(t)); }

/* ---- paths (System.IO.Path as forge uses it on POSIX) ---- */
static bool nbIsRooted(const char* p) { return p[0] == '/'; }
static const char* nbDirName(FcCtx* c, const char* p) {
    const char* last = strrchr(p, '/');
    if (!last) return "";
    if (last == p) return "/";
    return fcStrN(c, p, (size_t)(last - p));
}
static const char* nbFileName(const char* p) { const char* last = strrchr(p, '/'); return last ? last + 1 : p; }
static const char* nbCombine(FcCtx* c, const char* a, const char* b) {
    if (!*a) return b;
    if (nbIsRooted(b)) return b;
    return a[strlen(a) - 1] == '/' ? fcFmt(c, "%s%s", a, b) : fcFmt(c, "%s/%s", a, b);
}
/* Path.ChangeExtension */
static const char* nbChangeExtension(FcCtx* c, const char* p, const char* ext) {
    const char* name = nbFileName(p);
    const char* dot = strrchr(name, '.');
    size_t base = dot ? (size_t)(dot - p) : strlen(p);
    return fcFmt(c, "%.*s%s", (int)base, p, ext);
}
static bool nbFileExists(const char* p) { struct stat st; return stat(p, &st) == 0 && S_ISREG(st.st_mode); }
static bool nbDirExists(const char* p) { struct stat st; return stat(p, &st) == 0 && S_ISDIR(st.st_mode); }
static bool nbEndsWithI(const char* s, const char* suffix) {
    size_t n = strlen(s), m = strlen(suffix);
    return n >= m && fcAsciiIEq(s + n - m, suffix);
}

/* The C compiler that goes with the C++ compiler cxx (same folder, same prefix: x86_64-w64-mingw32-g++ -> x86_64-w64-mingw32-gcc; cl compiles C as well). */
static const char* nbCCompilerFor(FcCtx* c, const char* cxx) {
    const char* dir = nbDirName(c, cxx);
    const char* file = nbFileName(cxx);
    const char* ext = nbEndsWithI(file, ".exe") ? ".exe" : "";
    const char* stem = *ext ? fcStrN(c, file, strlen(file) - 4) : file;
    const char* cc;
    size_t n = strlen(stem);
    if (fcEndsWith(stem, "clang++")) cc = fcStrN(c, stem, n - 2);
    else if (fcEndsWith(stem, "g++")) cc = fcFmt(c, "%.*sgcc", (int)(n - 3), stem);
    else if (fcEndsWith(stem, "em++")) cc = fcFmt(c, "%.*semcc", (int)(n - 4), stem);
    else if (fcEndsWith(stem, "c++")) cc = fcFmt(c, "%.*scc", (int)(n - 3), stem);
    else cc = stem;
    return *dir ? nbCombine(c, dir, fcFmt(c, "%s%s", cc, ext)) : fcFmt(c, "%s%s", cc, ext);
}
static const char* nbEffectiveCCompiler(FcCtx* c, const NbToolchain* t) { return t->ccompiler ? t->ccompiler : nbCCompilerFor(c, nbEffectiveCompiler(t)); }

/* ---- ToolchainSetup ---- */
/* AppContext.BaseDirectory: the folder of forge-c (set by main) */
static const char* nbBaseDirectory = ".";
/* Toolchain/ next to the program */
static const char* nbToolchainRoot(FcCtx* c) { return nbCombine(c, nbBaseDirectory, "Toolchain"); }
static const char* const nbWatcomFolders[] = { "binl64", "binl" };   /* WatcomToolchain.CompilerFolders (Linux) */
static int nbCmpOrdinalIgnoreCase(const void* a, const void* b) { return fcCmpIgnoreCase(a, b); }
/* The bin folders of the toolchains to look in before the PATH: the portable ones next to the program (Directory.GetDirectories: in the order of the file system,
 * here by name). */
static FcList* nbSearchDirectories(FcCtx* c) {
    FcList* r = fcListNew(c);
    const char* root = nbToolchainRoot(c);
    const char* w64 = nbCombine(c, root, "w64devkit");
    const char* w = nbCombine(c, w64, "bin");
    DIR* d;
    if (nbDirExists(w)) fcListAdd(c, r, (void*)w);
    if (nbDirExists(root) && (d = opendir(root)) != NULL) {
        FcList* dirs = fcListNew(c);
        struct dirent* e;
        int i, k;
        while ((e = readdir(d)) != NULL) {
            const char* full;
            if (streq0(e->d_name, ".") || streq0(e->d_name, "..")) continue;
            full = nbCombine(c, root, fcStr(c, e->d_name));
            if (nbDirExists(full)) fcListAdd(c, dirs, (void*)full);
        }
        closedir(d);
        if (dirs->count > 1) qsort(dirs->items, (size_t)dirs->count, sizeof(void*), fcCmpOrdinal);
        for (i = 0; i < dirs->count; i++) {
            const char* dir = (const char*)dirs->items[i];
            const char* bin;
            if (fcAsciiIEq(dir, w64)) continue;
            /* Open Watcom has no bin: its compilers are in binl64/binl */
            if (fcAsciiIEq(dir, nbCombine(c, root, "watcom"))) {
                for (k = 0; k < 2; k++) { const char* ow = nbCombine(c, dir, nbWatcomFolders[k]); if (nbDirExists(ow)) fcListAdd(c, r, (void*)ow); }
                continue;
            }
            /* the portable TinyCC has tcc at its top, no bin */
            if (fcAsciiIEq(dir, nbCombine(c, root, "tcc"))) { fcListAdd(c, r, (void*)dir); continue; }
            bin = nbCombine(c, dir, "bin");
            if (nbDirExists(bin)) fcListAdd(c, r, (void*)bin);
        }
    }
    (void)nbCmpOrdinalIgnoreCase;
    return r;
}
/* The full path of the program name in the toolchain folders or on the PATH, or NULL. */
static const char* nbFindProgram(FcCtx* c, const char* name) {
    FcList* dirs;
    const char* path = getenv("PATH");
    int i;
    if (nbIsRooted(name)) return nbFileExists(name) ? name : nbFileExists(fcFmt(c, "%s.exe", name)) ? fcFmt(c, "%s.exe", name) : NULL;
    dirs = nbSearchDirectories(c);
    if (path) {
        FcList* parts = fgSplit(c, path, ':');
        for (i = 0; i < parts->count; i++) {
            const char* p = (const char*)parts->items[i];
            size_t n;
            if (!*p) continue;
            while (*p == '"') p++;
            n = strlen(p);
            while (n > 0 && p[n - 1] == '"') n--;
            fcListAdd(c, dirs, (void*)fcStrN(c, p, n));
        }
    }
    for (i = 0; i < dirs->count; i++) {
        const char* candidate = nbCombine(c, (const char*)dirs->items[i], name);
        if (nbFileExists(candidate)) return candidate;
    }
    return NULL;
}

/* ---- Open Watcom (Toolchains/WatcomToolchain.cs) ---- */
static const char* nbWatcomCompilerIn(FcCtx* c, const char* root, const char* name) {
    int i;
    for (i = 0; i < 2; i++) {
        const char* candidate = nbCombine(c, nbCombine(c, root, nbWatcomFolders[i]), name);
        if (nbFileExists(candidate)) return candidate;
    }
    return NULL;
}
static const char* nbWatcomLocate(FcCtx* c, const NbToolchain* t) {
    const char* name = t->compiler ? t->compiler : "owcc";
    const char* found;
    const char* env = getenv("WATCOM");
    if ((found = nbFindProgram(c, name)) != NULL) return found;
    if (env && *env && (found = nbWatcomCompilerIn(c, env, name)) != NULL) return found;
    if ((found = nbWatcomCompilerIn(c, nbCombine(c, nbToolchainRoot(c), "watcom"), name)) != NULL) return found;
    return nbWatcomCompilerIn(c, "/opt/watcom", name);
}
static const char* nbQuoteSpace(FcCtx* c, const char* s) { return strchr(s, ' ') ? fcFmt(c, "\"%s\"", s) : s; }
/* The startup code of a platform without an operating system (bios16: fire_bios16_start.asm), in the order of the names. */
static FcList* nbWatcomStartupSources(FcCtx* c, const FgTarget* target, const char* includeDir) {
    FcList* r = fcListNew(c);
    const char* dir = nbCombine(c, nbCombine(c, includeDir, "platform"), target->platform);
    DIR* d = nbDirExists(dir) ? opendir(dir) : NULL;
    struct dirent* e;
    if (!d) return r;
    while ((e = readdir(d)) != NULL) if (fcEndsWith(e->d_name, "_start.asm") && nbFileExists(nbCombine(c, dir, e->d_name))) fcListAdd(c, r, (void*)nbCombine(c, dir, fcStr(c, e->d_name)));
    closedir(d);
    if (r->count > 1) qsort(r->items, (size_t)r->count, sizeof(void*), fcCmpOrdinal);
    return r;
}
typedef struct NbCommand { const char* exe; const char* args; } NbCommand;
static NbCommand* nbCommand(FcCtx* c, const char* exe, const char* args) { NbCommand* k = (NbCommand*)fcAlloc(c, sizeof *k); k->exe = exe; k->args = args; return k; }
/* CompileRequest -> the command of owcc */
static NbCommand* nbWatcomCommand(FcCtx* c, const NbToolchain* t, const FgTarget* target, const char* sourceFile, const char* includeDir, const char* output,
    FcList* includeDirs, FcList* libraries, bool compileOnly, FcList* extraInputs, bool debugBuild) {
    FcList* args = fcListNew(c);
    FcList* startup;
    int i;
    bool sizeOpt = !t->optimization || streq0(t->optimization, "size");
    const char* located;
    for (i = 0; i < target->compileArgCount; i++) fcListAdd(c, args, (void*)target->compileArgs[i]);
    fcListAdd(c, args, (void*)fcFmt(c, "-std=%s", t->cstd ? t->cstd : "c99"));
    fcListAdd(c, args, (void*)"-ffunction-sections");
    fcListAdd(c, args, (void*)(debugBuild ? "-g" : sizeOpt ? "-Os" : t->optimization));
    /* (optimised for size: no inlining at all) */
    if (!debugBuild && sizeOpt) fcListAdd(c, args, (void*)"-Wc,-oe=0");
    if (t->args) for (i = 0; i < t->args->count; i++) fcListAdd(c, args, t->args->items[i]);
    if (compileOnly) fcListAdd(c, args, (void*)"-c");
    startup = nbWatcomStartupSources(c, target, includeDir);
    for (i = 0; i < startup->count; i++) fcListAdd(c, args, (void*)nbQuoteSpace(c, nbChangeExtension(c, (const char*)startup->items[i], ".obj")));
    fcListAdd(c, args, (void*)nbQuoteSpace(c, sourceFile));
    for (i = 0; i < extraInputs->count; i++) fcListAdd(c, args, (void*)nbQuoteSpace(c, (const char*)extraInputs->items[i]));
    fcListAdd(c, args, (void*)fcFmt(c, "-I%s", nbQuoteSpace(c, includeDir)));
    for (i = 0; i < includeDirs->count; i++) fcListAdd(c, args, (void*)fcFmt(c, "-I%s", nbQuoteSpace(c, (const char*)includeDirs->items[i])));
    fcListAdd(c, args, (void*)"-o");
    fcListAdd(c, args, (void*)nbQuoteSpace(c, output));
    if (!compileOnly) { fcListAdd(c, args, (void*)"-Wl,option"); fcListAdd(c, args, (void*)"-Wl,eliminate"); }
    if (!compileOnly) for (i = 0; i < libraries->count; i++) if (!streq0((const char*)libraries->items[i], "-lm")) fcListAdd(c, args, libraries->items[i]);
    located = nbWatcomLocate(c, t);
    return nbCommand(c, located ? located : t->compiler ? t->compiler : "owcc", fgJoin(c, args, " "));
}
/* The startup code, assembled with wasm before the program is compiled. */
static FcList* nbWatcomPreCommands(FcCtx* c, const NbToolchain* t, const FgTarget* target, const char* includeDir) {
    FcList* r = fcListNew(c);
    FcList* sources = nbWatcomStartupSources(c, target, includeDir);
    const char* compiler;
    const char* wasm;
    const char* cpu = "-3";
    int i;
    if (sources->count == 0) return r;
    compiler = nbWatcomLocate(c, t);
    wasm = compiler ? nbCombine(c, nbDirName(c, compiler), "wasm") : "wasm";
    for (i = 0; i < target->compileArgCount; i++) if (streq0(target->compileArgs[i], "-march=i86")) cpu = "-0";
    for (i = 0; i < sources->count; i++) {
        const char* f = (const char*)sources->items[i];
        fcListAdd(c, r, nbCommand(c, wasm, fcFmt(c, "-q %s %s -fo=%s", cpu, nbQuoteSpace(c, f), nbQuoteSpace(c, nbChangeExtension(c, f, ".obj")))));
    }
    return r;
}

/* ---- ToolchainDetector ---- */
/* The full path of the compiler of the toolchain on this machine, or NULL (for `files` there is nothing to find). */
static const char* nbFind(FcCtx* c, const NbToolchain* t) {
    const char* k = nbKind(t);
    if (streq0(k, "files") || streq0(k, "custom")) return NULL;
    if (nbIsWatcom(k)) return nbWatcomLocate(c, t);
    return nbFindProgram(c, nbEffectiveCompiler(t));
}
/* The full path of the C compiler of the toolchain, or NULL. */
static const char* nbFindC(FcCtx* c, const NbToolchain* t) {
    const char* k = nbKind(t);
    const char* cxx;
    if (streq0(k, "files") || streq0(k, "custom")) return NULL;
    if (nbIsWatcom(k)) return nbWatcomLocate(c, t);
    if (!t->ccompiler && (cxx = nbFind(c, t)) != NULL) {
        /* next to the C++ compiler that was found (a portable toolchain is not on the PATH) */
        const char* beside = nbCCompilerFor(c, cxx);
        if (nbFileExists(beside)) return beside;
    }
    return nbFindProgram(c, nbEffectiveCCompiler(c, t));
}

/* ---- the JSON of a toolchain (ToolchainDef as System.Text.Json reads it: the names without case) ---- */
static FcList* nbJsonList(FcCtx* c, const FcJson* o, const char* key) {
    FcJson* v = fcJsonGet(o, key);
    FcList* l;
    int i;
    if (!v || v->kind != FJ_ARR) return NULL;
    l = fcListNew(c);
    for (i = 0; i < v->items->count; i++) if (((FcJson*)v->items->items[i])->kind == FJ_STR) fcListAdd(c, l, (void*)((FcJson*)v->items->items[i])->s);
    return l;
}
static NbToolchain* nbToolchainFromJson(FcCtx* c, const FcJson* o) {
    NbToolchain* t = nbToolchainNew(c);
    FcJson* strip = fcJsonGet(o, "strip");
    t->extends = fcJsonStr(o, "extends");
    t->kind = fcJsonStr(o, "kind");
    t->compiler = fcJsonStr(o, "compiler");
    t->std = fcJsonStr(o, "std");
    t->cstd = fcJsonStr(o, "cStd");
    t->ccompiler = fcJsonStr(o, "cCompiler");
    t->optimization = fcJsonStr(o, "optimization");
    if (strip && strip->kind == FJ_BOOL) t->strip = strip->b ? 1 : 0;
    t->args = nbJsonList(c, o, "args");
    t->libs = nbJsonList(c, o, "libs");
    t->includeDirs = nbJsonList(c, o, "includeDirs");
    t->command = fcJsonStr(o, "command");
    t->layout = fcJsonStr(o, "layout");
    t->buildCommand = fcJsonStr(o, "buildCommand");
    return t;
}
static FcJson* nbReadJson(FcCtx* c, const char* path) {
    size_t len = 0;
    uint8_t* data = fbReadFile(path, &len);
    FcJsonReader j;
    FcJson* v;
    char* text;
    if (!data) return NULL;
    text = (char*)fcAlloc(c, len + 1);
    memcpy(text, data, len);
    text[len] = 0;
    free(data);
    j.c = c; j.p = text; j.bad = false;
    if (len >= 3 && (unsigned char)text[0] == 0xEF && (unsigned char)text[1] == 0xBB && (unsigned char)text[2] == 0xBF) j.p += 3;
    v = fcJsonValue(&j);
    fcJsonWs(&j);
    return j.bad || *j.p ? NULL : v;
}

/* ---- MachineToolchains ---- */
enum { NB_PURPOSE_BUILD, NB_PURPOSE_PUBLISH };
typedef struct NbMachine { const char* build; const char* publish; NbToolchain* legacy; } NbMachine;
static const char* const nbMachineKinds[] = { "tcc", "gcc", "clang", "msvc" };
static const char* nbMachineNormalize(FcCtx* c, const char* choice) {
    const char* t;
    int i;
    if (!choice) return NULL;
    t = fcAsciiLower(c, fgTrim(c, choice));
    if (!*t) return NULL;
    if (streq0(t, "auto")) return "auto";
    for (i = 0; i < 4; i++) if (streq0(t, nbMachineKinds[i])) return nbMachineKinds[i];
    return streq0(t, "automatic") ? "auto" : streq0(t, "w64devkit") ? "gcc" : NULL;
}
/* The choice of the machine (no file, or a broken one: automatic for both). */
static NbMachine nbMachineLoad(FcCtx* c, const char* file) {
    NbMachine m = { "auto", "auto", NULL };
    FcJson* json;
    const char* old = "auto";
    const char* b;
    const char* p;
    int i;
    if (!file) file = nbCombine(c, nbToolchainRoot(c), "toolchain.json");
    if (!nbFileExists(file) || !(json = nbReadJson(c, file)) || json->kind != FJ_OBJ) return m;
    if (fcJsonGet(json, "kind") || fcJsonGet(json, "compiler") || fcJsonGet(json, "extends")) {
        NbToolchain* def = nbToolchainFromJson(c, json);
        m.legacy = nbWithBase(c, def, def->kind ? nbBuiltIn(c, def->kind) : NULL);
    }
    /* (a file of an older fire: the toolchain it holds is the choice for both) */
    if (m.legacy) for (i = 0; i < 4; i++) if (fcAsciiIEq(nbKind(m.legacy), nbMachineKinds[i])) old = nbMachineKinds[i];
    b = nbMachineNormalize(c, fcJsonStr(json, "build"));
    p = nbMachineNormalize(c, fcJsonStr(json, "publish"));
    m.build = b ? b : old;
    m.publish = p ? p : old;
    return m;
}
/* The toolchain of a kind: the one in toolchain.json when it is of that kind, else the built-in one. */
static NbToolchain* nbMachineDefFor(FcCtx* c, const NbMachine* m, const char* kind) {
    return m->legacy && fcAsciiIEq(nbKind(m->legacy), kind) ? m->legacy : nbBuiltIn(c, kind);
}
static bool nbMachineFound(FcCtx* c, const NbMachine* m, const char* kind) { return nbBuiltIn(c, kind) && nbFind(c, nbMachineDefFor(c, m, kind)) != NULL; }
static NbToolchain* nbMark(FcCtx* c, const NbToolchain* def) { NbToolchain* t = nbCopy(c, def); t->machineChoice = true; return t; }
static const char* const nbOrderBuild[] = { "tcc", "gcc", "clang", "msvc" };
static const char* const nbOrderPublish[] = { "gcc", "clang", "msvc", "tcc" };   /* (not Windows) */
/* The toolchain for the purpose on this machine, marked as the machine's choice; NULL when no compiler is there. source: where it comes from. */
static NbToolchain* nbMachineResolve(FcCtx* c, const NbMachine* m, int purpose, const char** source) {
    const char* choice = purpose == NB_PURPOSE_BUILD ? m->build : m->publish;
    const char* const* order = purpose == NB_PURPOSE_BUILD ? nbOrderBuild : nbOrderPublish;
    const char* recommended = purpose == NB_PURPOSE_BUILD ? "tcc" : "gcc";
    int i;
    if (!streq0(choice, "auto") && nbMachineFound(c, m, choice)) { if (source) *source = "chosen in the IDE options"; return nbMark(c, nbMachineDefFor(c, m, choice)); }
    for (i = 0; i < 4; i++)
        if (nbMachineFound(c, m, order[i])) {
            if (source) *source = !streq0(choice, "auto") ? fcFmt(c, "automatic: %s is not installed", choice) : streq0(order[i], recommended) ? "automatic, recommended" : "automatic";
            return nbMark(c, nbMachineDefFor(c, m, order[i]));
        }
    if (source) *source = "no compiler found";
    return NULL;
}
/* The toolchain that compiles C++ for a program whose natives are C++ when the chosen one is C only (TinyCC): the first of Publish's automatic order. */
static NbToolchain* nbMachineCppFallback(FcCtx* c, const NbMachine* m) {
    int i;
    for (i = 0; i < 4; i++) if (!streq0(nbOrderPublish[i], "tcc") && nbMachineFound(c, m, nbOrderPublish[i])) return nbMark(c, nbMachineDefFor(c, m, nbOrderPublish[i]));
    return NULL;
}
/* ToolchainSetup.LoadMachineToolchain, Detect */
static NbToolchain* nbLoadMachineToolchain(FcCtx* c) { return nbMachineLoad(c, NULL).legacy; }
static NbToolchain* nbDetect(FcCtx* c) {
    NbToolchain* chosen = nbLoadMachineToolchain(c);
    FcList* dirs;
    static const char* const kinds[] = { "gcc", "clang", "msvc", "tcc" };   /* (TinyCC last: it compiles no C++) */
    int i;
    if (chosen && nbFind(c, chosen)) return chosen;
    dirs = nbSearchDirectories(c);
    for (i = 0; i < dirs->count; i++) {
        const char* gxx = nbCombine(c, (const char*)dirs->items[i], "g++");
        if (nbFileExists(gxx)) {
            NbToolchain* t = nbToolchainNew(c);
            t->extends = "gcc"; t->kind = "gcc"; t->compiler = gxx; t->std = "c++17"; t->optimization = "-O2"; t->args = nbStrings(c, 2, "-Wall", "-Wextra");
            return t;
        }
    }
    for (i = 0; i < 4; i++) { NbToolchain* def = nbBuiltIn(c, kinds[i]); if (nbFind(c, def)) return def; }
    return NULL;
}

/* ---- the targets of a configuration (NativeConfig.Targets, TargetDef.Apply) ---- */
typedef struct NbTargetDef { const char* name; const FcJson* json; } NbTargetDef;
static const char* const* nbJsonArray(FcCtx* c, const FcJson* o, const char* key, int* count) {
    FcList* l = nbJsonList(c, o, key);
    if (!l) return NULL;
    *count = l->count;
    return (const char* const*)l->items;
}
static FgTarget* nbApplyTargetDef(FcCtx* c, const FcJson* d, const FgTarget* b, const char* name) {
    FgTarget* t = (FgTarget*)fcAlloc(c, sizeof *t);
    FcJson* v;
    const char* const* list;
    int n = 0;
    const char* s;
    *t = *b;
    t->name = fcStr(c, name);
    if ((list = nbJsonArray(c, d, "symbols", &n)) != NULL) { int i; t->symbolCount = n < 4 ? n : 4; for (i = 0; i < t->symbolCount; i++) t->symbols[i] = list[i]; }
    if ((v = fcJsonGet(d, "floatWidth")) && v->kind == FJ_NUM) t->floatWidth = (int)v->n;
    if ((v = fcJsonGet(d, "intWidth")) && v->kind == FJ_NUM) t->intWidth = (int)v->n;
    if ((v = fcJsonGet(d, "wordWidth")) && v->kind == FJ_NUM) {
        int w = (int)v->n;
        if (w != 16 && w != 32 && w != 64) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "native-config: Target '%s': \"wordWidth\" must be 16, 32 or 64, not %d.", name, w));
        t->wordWidth = w;
    }
    if ((s = fcJsonStr(d, "optimize")) != NULL) {
        if (fcAsciiIEq(s, "speed")) t->optimize = 0; else if (fcAsciiIEq(s, "size")) t->optimize = 1;
        else fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "native-config: Target '%s': \"optimize\" must be \"speed\" or \"size\", not \"%s\".", name, s));
    }
    if ((v = fcJsonGet(d, "stackBytes")) && v->kind == FJ_NUM) t->defaultStackBytes = (int)v->n;
    if ((list = nbJsonArray(c, d, "imports", &n)) != NULL) { t->imports = list; t->importCount = n; }
    if ((v = fcJsonGet(d, "embedded")) && v->kind == FJ_BOOL) t->isEmbedded = v->b;
    s = fcJsonStr(d, "displayName");
    t->displayName = s ? s : fcAsciiIEq(name, b->name) ? b->displayName : NULL;
    if ((list = nbJsonArray(c, d, "toolchains", &n)) != NULL) { t->toolchains = list; t->toolchainCount = n; }
    if ((s = fcJsonStr(d, "platform")) != NULL) { t->platform = s; t->halPackage = s; }
    if ((s = fcJsonStr(d, "platformPath")) != NULL) t->platformPath = s;
    if ((list = nbJsonArray(c, d, "includes", &n)) != NULL) { t->includes = list; t->includeCount = n; }
    if ((list = nbJsonArray(c, d, "defines", &n)) != NULL) { t->defines = list; t->defineCount = n; }
    if ((list = nbJsonArray(c, d, "compileArgs", &n)) != NULL) { t->compileArgs = list; t->compileArgCount = n; }
    if ((list = nbJsonArray(c, d, "linkLibs", &n)) != NULL) { t->linkLibs = list; t->linkLibCount = n; }
    if ((v = fcJsonGet(d, "supportsThreads")) && v->kind == FJ_BOOL) t->supportsThreads = v->b;
    if ((s = fcJsonStr(d, "toolchain")) != NULL) t->toolchain = s;
    if ((v = fcJsonGet(d, "lineDirectives")) && v->kind == FJ_BOOL) t->lineDirectives = v->b;
    if ((v = fcJsonGet(d, "entry")) && v->kind == FJ_OBJ) {
        FcJson* ek;
        if ((s = fcJsonStr(v, "name")) != NULL) t->entryName = s;
        if ((ek = fcJsonGet(v, "kind")) && ek->kind == FJ_STR) t->entryKind = fcAsciiIEq(ek->s, "function") ? 1 : 0;
        if ((ek = fcJsonGet(v, "externC")) && ek->kind == FJ_BOOL) t->entryExternC = ek->b;
    }
    return t;
}

/* ---- NativeConfig ---- */
typedef struct NbConfig {
    const char* engine;
    const char* target;
    const char* toolchain;
    int stripping;              /* -1: null */
    bool strippingFromCommandLine;
    bool stats;
    int optimize;               /* -1: null, 0 speed, 1 size */
    bool optimizeFromCommandLine;
    const char* globalJobs;
    const char* linkage;
    int split;                  /* FG_SPLIT_* (SplitValue: auto when not given) */
    bool splitGiven;            /* `"split"` was in the file (Split ?? ...: publish, Phase D 3) */
    int quick;                  /* -1: a normal build, else the level */
    const char* host;
    FcList* targets;            /* NbTargetDef* */
    FcList* toolchainNames;     /* the keys of Toolchains */
    FcList* toolchains;         /* NbToolchain*, parallel */
    const char* path;           /* where it was read from; NULL: the built-in defaults */
} NbConfig;
static NbConfig* nbConfigNew(FcCtx* c) {
    NbConfig* k = (NbConfig*)fcAlloc(c, sizeof *k);
    k->stripping = -1; k->optimize = -1; k->split = FG_SPLIT_AUTO; k->quick = -1;
    k->targets = fcListNew(c); k->toolchainNames = fcListNew(c); k->toolchains = fcListNew(c);
    return k;
}
#define NB_CONFIG_FAIL(...) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "native-config: " __VA_ARGS__))
static int nbStrippingParse(FcCtx* c, const char* text) {
    static const char* const names[] = { "none", "protected", "methods", "all" };
    const char* t = fcAsciiLower(c, fgTrim(c, text));
    int i;
    if (!*t) return -1;
    if (t[0] >= '0' && t[0] <= '3' && !t[1]) return t[0] - '0';
    for (i = 0; i < 4; i++) if (streq0(t, names[i])) return i;
    return -2;
}
static NbConfig* nbConfigLoad(FcCtx* c, const char* path) {
    NbConfig* k = nbConfigNew(c);
    FcJson* json = nbReadJson(c, path);
    FcJson* v;
    const char* s;
    int i;
    if (!json || json->kind != FJ_OBJ) NB_CONFIG_FAIL("fire.native.json: the file is not a JSON object (or not JSON the port reads: comments and trailing commas are not ported).");
    k->path = fcFullPath(c, ".", path);
    k->engine = fcJsonStr(json, "engine");
    k->target = fcJsonStr(json, "target");
    k->toolchain = fcJsonStr(json, "toolchain");
    k->linkage = fcJsonStr(json, "linkage");
    k->host = fcJsonStr(json, "host");
    if ((v = fcJsonGet(json, "stripping")) != NULL && v->kind != FJ_NULL) {
        int level = v->kind == FJ_NUM ? (v->n >= 0 && v->n <= 3 && v->n == (int)v->n ? (int)v->n : -2) : v->kind == FJ_STR ? nbStrippingParse(c, v->s) : -2;
        if (level < 0) NB_CONFIG_FAIL("fire.native.json: \"stripping\" is not one of 0-3 or none, protected, methods, all.");
        k->stripping = level;
    } else if ((s = fcJsonStr(json, "trim")) != NULL) {
        const char* t = fcAsciiLower(c, fgTrim(c, s));
        k->stripping = streq0(t, "all") ? 0 : streq0(t, "reflection") ? 1 : streq0(t, "reached") ? 3 : -2;
        if (k->stripping == -2) NB_CONFIG_FAIL("fire.native.json: \"trim\": '%s' is not one of: all, reflection, reached (better: \"stripping\" 0-3).", s);
    }
    if ((s = fcJsonStr(json, "optimize")) != NULL) {
        if (fcAsciiIEq(s, "speed")) k->optimize = 0; else if (fcAsciiIEq(s, "size")) k->optimize = 1;
        else NB_CONFIG_FAIL("fire.native.json: \"optimize\": '%s' is not one of: speed, size.", s);
    }
    if ((s = fcJsonStr(json, "globalJobs")) != NULL) k->globalJobs = s;
    if ((s = fcJsonStr(json, "split")) != NULL) {
        if ((k->split = fgSplitModeParse(s)) < 0) NB_CONFIG_FAIL("fire.native.json: \"split\": '%s' is not one of: auto, on, off.", s);
        k->splitGiven = true;
    }
    if ((v = fcJsonGet(json, "targets")) && v->kind == FJ_OBJ)
        for (i = 0; i < v->keys->count; i++) {
            NbTargetDef* d = (NbTargetDef*)fcAlloc(c, sizeof *d);
            d->name = (const char*)v->keys->items[i];
            d->json = (FcJson*)v->items->items[i];
            fcListAdd(c, k->targets, d);
        }
    if ((v = fcJsonGet(json, "toolchains")) && v->kind == FJ_OBJ)
        for (i = 0; i < v->keys->count; i++) {
            fcListAdd(c, k->toolchainNames, v->keys->items[i]);
            fcListAdd(c, k->toolchains, nbToolchainFromJson(c, (FcJson*)v->items->items[i]));
        }
    return k;
}
/* The configuration that applies to a source file: the nearest fire.native.json in its folder or above; without one, built-in defaults. */
static NbConfig* nbConfigFindFor(FcCtx* c, const char* sourceFile) {
    const char* dir = fcDirectoryName(c, fcFullPath(c, ".", sourceFile));
    for (;;) {
        const char* candidate = nbCombine(c, dir, "fire.native.json");
        if (nbFileExists(candidate)) return nbConfigLoad(c, candidate);
        if (streq0(dir, "/") || streq0(dir, ".")) break;
        dir = fcDirectoryName(c, dir);
    }
    return nbConfigNew(c);
}
static const FgTarget* nbResolveTargetIn(FcCtx* c, NbConfig* k, const char* name, FcList* chain) {
    NbTargetDef* def = NULL;
    const FgTarget* base;
    const char* ext;
    int i;
    for (i = 0; i < chain->count; i++) if (fcAsciiIEq((const char*)chain->items[i], name)) NB_CONFIG_FAIL("target '%s' extends itself", name);
    fcListAdd(c, chain, (void*)name);
    for (i = 0; i < k->targets->count && !def; i++) if (fcAsciiIEq(((NbTargetDef*)k->targets->items[i])->name, name)) def = (NbTargetDef*)k->targets->items[i];
    if (def && (ext = fcJsonStr(def->json, "extends")) != NULL) base = nbResolveTargetIn(c, k, ext, chain);
    else if ((base = fgTargetNamed(name)) != NULL) { }
    else if (def) { FgTarget* t = (FgTarget*)fcAlloc(c, sizeof *t); *t = *fgHostTarget(); t->name = fcStr(c, name); base = t; }
    else {
        FcList* names = fcListNew(c);
        for (i = 0; i < (int)(sizeof fgTargets / sizeof fgTargets[0]); i++) fcListAdd(c, names, (void*)fgTargets[i].name);
        for (i = 0; i < k->targets->count; i++) if (!fcListHasIStr(names, ((NbTargetDef*)k->targets->items[i])->name)) fcListAdd(c, names, (void*)((NbTargetDef*)k->targets->items[i])->name);
        NB_CONFIG_FAIL("unknown target '%s' (known: %s)", name, fgJoin(c, names, ", "));
    }
    return def ? nbApplyTargetDef(c, def->json, base, name) : base;
}
/* The target name (NULL: the configured default target, else this machine), with the configuration's changes applied. */
static const FgTarget* nbResolveTarget(FcCtx* c, NbConfig* k, const char* name) {
    if (!name) name = k->target;
    if (!name) return fgHostTarget();
    return nbResolveTargetIn(c, k, name, fcListNew(c));
}
/* TargetProfile.AllowsToolchain */
static bool nbAllowsToolchain(const FgTarget* t, const char* kind) {
    int i;
    if (!t->toolchains || fcAsciiIEq(kind, "files")) return true;
    for (i = 0; i < t->toolchainCount; i++) if (fcAsciiIEq(t->toolchains[i], kind)) return true;
    return false;
}
static NbToolchain* nbResolveToolchainNamed(FcCtx* c, NbConfig* k, const FgTarget* target, const char* name, int purpose) {
    int i;
    NbToolchain* t;
    if (!name) name = target->toolchain ? target->toolchain : k->toolchain;
    if (name) {
        for (i = 0; i < k->toolchainNames->count; i++)
            if (fcAsciiIEq((const char*)k->toolchainNames->items[i], name)) {
                NbToolchain* found = (NbToolchain*)k->toolchains->items[i];
                return nbWithBase(c, found, nbBuiltIn(c, found->extends ? found->extends : name));
            }
        if ((t = nbBuiltIn(c, name)) != NULL) return t;
        {
            FcList* names = fcListNew(c);
            for (i = 0; i < NB_BUILTIN_COUNT; i++) fcListAdd(c, names, (void*)nbBuiltInNames[i]);
            for (i = 0; i < k->toolchainNames->count; i++) if (!fcListHasIStr(names, (const char*)k->toolchainNames->items[i])) fcListAdd(c, names, k->toolchainNames->items[i]);
            NB_CONFIG_FAIL("unknown toolchain '%s' (known: %s)", name, fgJoin(c, names, ", "));
        }
    }
    if (purpose >= 0) { NbMachine m = nbMachineLoad(c, NULL); if ((t = nbMachineResolve(c, &m, purpose, NULL)) != NULL) return t; }   /* the compiler the machine chose */
    if ((t = nbLoadMachineToolchain(c)) != NULL && nbFind(c, t)) return t;   /* the toolchain chosen for the machine */
    if ((t = nbDetect(c)) != NULL) return t;
    return nbBuiltIn(c, "gcc");
}
/* The toolchain to build the target with: the one asked for, the target's, the configured default; else, with a purpose (>= 0), the compiler the machine chose
 * for building or publishing; else the machine's toolchain or the first compiler found on this machine. A toolchain that cannot build for the target is not used. */
static NbToolchain* nbResolveToolchain(FcCtx* c, NbConfig* k, const FgTarget* target, const char* name, int purpose) {
    NbToolchain* chosen = nbResolveToolchainNamed(c, k, target, name, purpose);
    const char* own;
    if (nbAllowsToolchain(target, nbKind(chosen))) return chosen;
    own = target->toolchain && nbAllowsToolchain(target, nbKind(nbResolveToolchainNamed(c, k, target, target->toolchain, -1))) ? target->toolchain : target->toolchains[0];
    return nbResolveToolchainNamed(c, k, target, own, -1);
}

/* PackageLibrary.OptimizingToolchain: the compiler for the natives of the virtual machine - the machine's toolchain, unless that is TinyCC: then the machine's
 * compiler for publishing, else any optimising compiler found. TinyCC only when there is nothing else. */
static NbToolchain* nbOptimizingToolchain(FcCtx* c, NbConfig* k, const FgTarget* target) {
    NbToolchain* chosen = nbResolveToolchain(c, k, target, NULL, -1);
    NbMachine m;
    NbToolchain* t;
    if (!fcAsciiIEq(nbKind(chosen), "tcc")) return chosen;
    m = nbMachineLoad(c, NULL);
    if ((t = nbMachineResolve(c, &m, NB_PURPOSE_PUBLISH, NULL)) != NULL && !fcAsciiIEq(nbKind(t), "tcc")) return t;
    if ((t = nbMachineCppFallback(c, &m)) != NULL) return t;
    return chosen;
}
