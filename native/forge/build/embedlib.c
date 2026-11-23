/* native/forge/build/embedlib.c - libforge and libfirevm as shared libraries (Phase D 7, docs/spec/EMBED.md): `forge-c build-embed <folder>` and the libraries for
 * a program that imports the packages `forge` or `script` (their natives load them: packages/forge, packages/script).
 *
 *   libfireforge.so   native/forge/forge.c with FIRE_FORGE_LIBRARY (fire_embed.h: fire_forge_*)
 *   libfirevm.so      native/vm/fire.c with FIRE_VM_LIBRARY (fire_embed.h: fire_vm_*, fire_run_*) - a runtime of its own, hidden in the library
 *
 * (fireforge.dll / firevm.dll on Windows, .dylib on macOS.) Built with the optimising C compiler of the machine like the VM (build/cvm.c), with
 * -fvisibility=hidden: only the functions of fire_embed.h are exported, so the runtime inside libfirevm never meets the one of a native host program. Each gets
 * a stamp `.key` (the hash of the sources it is built from); a folder that has current ones is taken - next to forge, else the cache of the VM
 * (<LocalApplicationData>/fire/vm/<rid>/<key>/), where they are built on first use. The folder goes to the programs as FIRE_EMBED_PATH. */

#ifdef _WIN32
#define NE_PREFIX ""
#define NE_EXT ".dll"
#elif defined(__APPLE__)
#define NE_PREFIX "lib"
#define NE_EXT ".dylib"
#else
#define NE_PREFIX "lib"
#define NE_EXT ".so"
#endif

/* which: 0 libforge, 1 libfirevm */
static const char* neLibName(FcCtx* c, int which) { return fcFmt(c, NE_PREFIX "%s" NE_EXT, which == 0 ? "fireforge" : "firevm"); }

/* the hash of the sources of the libraries: the files below native/forge, vm, base, tables, runtime, abi, platform (each its path, a newline, its bytes) */
static const char* neKey(FcCtx* c) {
    static const char* const parts[] = { "forge", "vm", "base", "tables", "runtime", "abi", "platform" };
    FcSha256 h;
    int p, i;
    fcShaInit(&h);
    fcShaUpdate(&h, (const uint8_t*)"embed 1\n", 8);
    for (p = 0; p < (int)(sizeof parts / sizeof parts[0]); p++) {
        const char* dir = nbCombine(c, nbNativeRoot, parts[p]);
        FcList* files = nbSortedFiles(c, dir, true);
        for (i = 0; i < files->count; i++) {
            const char* rel = fcFmt(c, "%s/%s", parts[p], (const char*)files->items[i]);
            size_t n = 0;
            uint8_t* body = fbReadFile(nbCombine(c, dir, (const char*)files->items[i]), &n);
            fcShaUpdate(&h, (const uint8_t*)rel, strlen(rel));
            fcShaUpdate(&h, (const uint8_t*)"\n", 1);
            if (body && n) fcShaUpdate(&h, body, n);
            free(body);
        }
    }
    return fcShaHex(c, &h);
}
static bool neCurrent(FcCtx* c, const char* folder, const char* key) {
    int which;
    for (which = 0; which < 2; which++) {
        const char* lib = nbCombine(c, folder, neLibName(c, which));
        const char* stamp;
        if (!nbFileExists(lib) || !nbFileExists(fcFmt(c, "%s.key", lib))) return false;
        stamp = nlReadTrimmed(c, fcFmt(c, "%s.key", lib));
        if (!stamp || !streq0(stamp, key)) return false;
    }
    return true;
}

/* one library into `folder` (with its stamp); false: *text says why */
static bool neBuildOne(FcCtx* c, int which, const char* folder, const char* key, FcBuf* log, const char** text) {
    const FgTarget* target = fgHostTarget();
    NbToolchain* volatile found = NULL;
    NbToolchain* t;
    const char* volatile failure = NULL;
    const char* work;
    const char* main;
    const char* built;
    const char* lib = nbCombine(c, folder, neLibName(c, which));
    const char* partial;
    NbCommand* k;
    NbRunResult run;
    FcBuf sb = {0}, out = {0};
    FC_TRY(c) {
        found = nbToolchainRequire(c, nbOptimizingToolchain(c, nbConfigNew(c), target),
            "The packages forge and script load the compiler and the VM of fire as libraries, which are compiled once on this machine.");
    } FC_CATCH(c) {
        if (c->err.kind != FC_ERR_INTERNAL || !c->err.message) fcRethrow(c);
        failure = fcStartsWith(c->err.message, "native-config: ") ? c->err.message + 15 : c->err.message;
    } FC_END_TRY;
    if (failure) { *text = failure; return false; }
    if (!found) { *text = "No C compiler was found on this machine."; return false; }
    /* (gnu11: forge uses POSIX beside C11; only fire_embed.h leaves the library) */
    t = (NbToolchain*)fcAlloc(c, sizeof *t);
    *t = *found;
    t->args = t->args ? fcListCopy(c, t->args) : fcListNew(c);
    if (!streq0(nbKind(t), "msvc") && !streq0(nbKind(t), "tcc")) { t->cstd = "gnu11"; fcListAdd(c, t->args, (void*)"-fvisibility=hidden"); }
    work = fcFmt(c, "%sfire-embedbuild-%s", nbTempPath(c), nbGuid(c));
    nbMkdirs(c, work);
    if (log) fcBufFmt(log, "Building %s for %s with %s...\n", neLibName(c, which), nlRid(), nbKind(t));
    fcBufStr(&sb, "");
    if (which == 0) {
        fcBufStr(&sb, "/* libforge (native/forge/forge.c as a library, fire_embed.h), built by forge (build/embedlib.c). */\n// fire-link: pthread\n// fire-link: m\n");
#ifndef _WIN32
        fcBufStr(&sb, "// fire-link: dl\n");
#endif
        fcBufFmt(&sb, "#define FIRE_FORGE_LIBRARY 1\n#include \"%s\"\n", nbCombine(c, nbCombine(c, nbNativeRoot, "forge"), "forge.c"));
        main = nbCombine(c, work, "fire_forge_lib.c");
    } else {
        const char* vmMain = nvWriteSources(c, work, 64, 64);   /* (the sources of the VM with the runtime of this platform, as for `fire`) */
        (void)vmMain;
        fcBufStr(&sb, "/* libfirevm (native/vm/fire.c as a library, fire_embed.h), built by forge (build/embedlib.c). */\n// fire-link: pthread\n// fire-link: m\n");
#ifndef __APPLE__
        fcBufStr(&sb, "// fire-link: dl\n");
#endif
        fcBufStr(&sb, "#define FIRE_VM_LIBRARY 1\n#include \"vm/fire.c\"\n");
        main = nbCombine(c, work, "fire_vm_lib.c");
    }
    nbWriteText(c, main, fcBufDone(c, &sb));
    built = nbCombine(c, work, neLibName(c, which));
    k = nbCompilerCommand(c, t, target, main, work, built, true, NULL, false, "c", false, NULL, NULL, NULL, false);
    run = nbRun(k->exe, k->args, work);
    fcBufStr(&out, "");
    nbAppendRun(&out, &run);
    if (!run.ok || !nbFileExists(built)) {
        *text = fcFmt(c, "%s %s\n%s", k->exe, k->args, fcBufDone(c, &out));
        nbDeleteTree(c, work);
        return false;
    }
    fcBufFree(&out);
    nbMkdirs(c, folder);
    /* (copied next to it and renamed: a library in its place is always whole) */
    partial = fcFmt(c, "%s.%s.partial", lib, nbGuid(c));
    if (!nbCopyFile(c, built, partial)) { *text = fcFmt(c, "Could not write '%s'.", partial); nbDeleteTree(c, work); return false; }
    if (rename(partial, lib) != 0) { remove(partial); *text = fcFmt(c, "Could not write '%s'.", lib); nbDeleteTree(c, work); return false; }
    nbWriteText(c, fcFmt(c, "%s.key", lib), key);
    nbDeleteTree(c, work);
    *text = "";
    return true;
}
static bool neBuild(FcCtx* c, const char* folder, const char* key, FcBuf* log, const char** text) {
    return neBuildOne(c, 0, folder, key, log, text) && neBuildOne(c, 1, folder, key, log, text);
}

/* the folder with current libraries: next to forge, else the cache (built now); NULL: *error */
static const char* neLocate(FcCtx* c, FcBuf* log, const char** error) {
    const char* key = neKey(c);
    const char* cached;
    const char* text = NULL;
    if (neCurrent(c, nbBaseDirectory, key)) return nbBaseDirectory;
    cached = nbCombine(c, nvCacheFolder(c), fcFmt(c, "embed-%.16s", key));
    if (neCurrent(c, cached, key)) return cached;
    if (!neBuild(c, cached, key, log, &text)) {
        *error = fcFmt(c, "The compiler and the VM of fire could not be built as libraries on this machine (for the packages forge and script):\n%s", text);
        return NULL;
    }
    return cached;
}

/* A program that imports script: FIRE_EMBED_PATH names the folder of the libraries for the programs forge starts (when it is not set already). The package
 * forge has libforge as a unit of its own (`native.units`, Phase D 8) - only a forge package installed without it (by a forge that does not know units)
 * loads the library. */
static void neEnsureFor(FcCtx* c, const FcLinked* linked, FcStore* store) {
    const char* error = NULL;
    const char* folder;
    const char* given = getenv("FIRE_EMBED_PATH");
    bool wanted = false;
    if (given && *given) return;
    FC_DICT_EACH(linked->nativeImports, e) {
        const char* key = (const char*)e->key;
        if (fcAsciiIEq(key, "pkg:script")) wanted = true;
        else if (fcAsciiIEq(key, "pkg:forge")) {
            FcPkgImport* pi = fcStoreFindKey(c, store, key);
            FgInstImport* x = pi ? fgInstalledImport(c, fcDictNew(c, FC_KEY_PTR), pi) : NULL;
            if (!x || !x->native || !x->native->units || x->native->units->count == 0) wanted = true;
        }
    }
    if (!wanted) return;
    folder = neLocate(c, NULL, &error);
    if (!folder) fcThrow(c, FC_ERR_PACKAGE, 0, 0, NULL, error);
    setenv("FIRE_EMBED_PATH", folder, 1);
}

/* `forge-c build-embed <folder>`: both libraries into a folder, only when they are not current */
static int neBuildEmbedMain(int argc, char** argv) {
    FcCtx ctx;
    const char* folder = argc >= 3 ? argv[2] : NULL;
    const char* key;
    const char* text = NULL;
    FcBuf log = {0};
    int result = 0;
    if (!folder || argc > 3) { fprintf(stderr, "build-embed <folder>\n"); return 2; }
    fcCtxInit(&ctx);
    key = neKey(&ctx);
    if (neCurrent(&ctx, folder, key)) {
        printf("%s (up to date)\n%s (up to date)\n", nbCombine(&ctx, folder, neLibName(&ctx, 0)), nbCombine(&ctx, folder, neLibName(&ctx, 1)));
        fcCtxFree(&ctx);
        return 0;
    }
    fcBufStr(&log, "");
    if (neBuild(&ctx, folder, key, &log, &text)) {
        fputs(log.p, stderr);
        printf("%s\n%s\n", nbCombine(&ctx, folder, neLibName(&ctx, 0)), nbCombine(&ctx, folder, neLibName(&ctx, 1)));
    } else {
        fputs(log.p, stderr);
        fprintf(stderr, "The libraries could not be built: %s\n", text);
        result = 1;
    }
    fcBufFree(&log);
    fcCtxFree(&ctx);
    return result;
}

/* ---- `forge-c amalgamate <file> [--check]`: libforge as one C file (Phase D 8) ----
 * native/forge/forge.c with every file it includes by a local `#include "..."` put in where the include stands - recursively and every time, as the
 * preprocessor does (the X-macro tables of native/tables are included several times; headers keep their guards) -, each path relative to the file that
 * includes it (forge has two command.c, json.c and stats.c in different folders). `#include <...>` stays. The result is the native unit of the package
 * forge (packages/forge/native/libforge.c, `native.units`): compiled on its own and linked into a program that imports forge - natively for any target
 * forge builds for, and into the library of the package for the VM. The text depends only on the files (no dates, no absolute paths): the same on every
 * machine. --check only says whether the file is current (exit 1 when not). */

/* a path relative to the repository (the folder above native/) without "." and ".." (NULL: it leaves it) */
static const char* neNormalize(FcCtx* c, const char* path) {
    FcList* parts = fcListNew(c);
    const char* p = path;
    FcBuf b = {0};
    int i;
    while (*p) {
        const char* e = p;
        while (*e && *e != '/' && *e != '\\') e++;
        if (e - p == 2 && p[0] == '.' && p[1] == '.') { if (parts->count == 0) return NULL; parts->count--; }
        else if (e > p && !(e - p == 1 && p[0] == '.')) fcListAdd(c, parts, (void*)fcStrN(c, p, (size_t)(e - p)));
        p = *e ? e + 1 : e;
    }
    fcBufStr(&b, "");
    for (i = 0; i < parts->count; i++) { if (i) fcBufChar(&b, '/'); fcBufStr(&b, (const char*)parts->items[i]); }
    return fcBufDone(c, &b);
}
/* the file of a line `#include "name"` (blanks around `#`, a comment after it), else NULL */
static const char* neLocalInclude(FcCtx* c, const char* line, size_t n) {
    size_t i = 0, start;
    while (i < n && (line[i] == ' ' || line[i] == '\t')) i++;
    if (i >= n || line[i] != '#') return NULL;
    i++;
    while (i < n && (line[i] == ' ' || line[i] == '\t')) i++;
    if (n - i < 7 || memcmp(line + i, "include", 7) != 0) return NULL;
    i += 7;
    while (i < n && (line[i] == ' ' || line[i] == '\t')) i++;
    if (i >= n || line[i] != '"') return NULL;
    start = ++i;
    while (i < n && line[i] != '"') i++;
    if (i >= n || i == start) return NULL;
    return fcStrN(c, line + start, i - start);
}
/* puts the file `rel` (relative to the repository: forge includes the deflater of packages/compression) into out, with what it includes; false: *error */
static bool neInline(FcCtx* c, FcBuf* out, const char* rel, int depth, const char** error) {
    size_t n = 0, i = 0;
    uint8_t* text;
    const char* dir;
    const char* slash = strrchr(rel, '/');
    if (depth > 32) { *error = fcFmt(c, "%s: the includes go deeper than 32 files", rel); return false; }
    text = fbReadFile(nbCombine(c, fcDirectoryName(c, nbNativeRoot), rel), &n);
    if (!text) { *error = fcFmt(c, "%s cannot be read (below %s)", rel, fcDirectoryName(c, nbNativeRoot)); return false; }
    dir = slash ? fcStrN(c, rel, (size_t)(slash - rel)) : "";
    while (i < n) {
        size_t e = i;
        const char* name;
        while (e < n && text[e] != '\n') e++;
        name = neLocalInclude(c, (const char*)text + i, e - i);
        if (name) {
            const char* inner = neNormalize(c, *dir ? fcFmt(c, "%s/%s", dir, name) : name);
            if (!inner) { free(text); *error = fcFmt(c, "%s: #include \"%s\" leaves the repository", rel, name); return false; }
            fcBufFmt(out, "/* ---- %s ---- */\n", inner);
            if (!neInline(c, out, inner, depth + 1, error)) { free(text); return false; }
            if (out->len > 0 && out->p[out->len - 1] != '\n') fcBufChar(out, '\n');
            fcBufFmt(out, "/* ---- (%s ends: %s) ---- */\n", inner, rel);
        } else fcBufPut(out, (const char*)text + i, e < n ? e - i + 1 : e - i);
        i = e < n ? e + 1 : e;
    }
    free(text);
    return true;
}
/* the text of libforge as one file; NULL: *error */
static const char* neAmalgamation(FcCtx* c, size_t* length, const char** error) {
    FcBuf out = {0};
    fcBufStr(&out,
        "/* libforge as one C file: native/forge/forge.c with FIRE_FORGE_LIBRARY and every file it includes (native/forge, native/base, native/abi,\n"
        " * native/tables, the deflater of packages/compression), put together by `forge amalgamate` (native/forge/build/embedlib.c, docs/spec/EMBED.md). Generated - do not edit: change the\n"
        " * files below native/ and run `forge amalgamate packages/forge/native/libforge.c` (`--check` says whether this file is current).\n"
        " *\n"
        " * The native unit of the package forge (`native.units`, docs/packages/natives.md): compiled on its own and linked into a program that imports\n"
        " * forge, and into the library of the package for the VM. Its interface is native/abi/fire_embed.h; its other symbols begin with fc and fb. */\n"
        "#ifndef FIRE_FORGE_LIBRARY\n#define FIRE_FORGE_LIBRARY 1\n#endif\n"
        "#if !defined(_WIN32) && !defined(_DEFAULT_SOURCE)\n#define _DEFAULT_SOURCE 1   /* (POSIX besides C11 - realpath, setenv, ... -, also under -std=c11) */\n#endif\n");
    if (!neInline(c, &out, "native/forge/forge.c", 0, error)) { fcBufFree(&out); return NULL; }
    *length = out.len;
    return fcBufDone(c, &out);
}
static int neAmalgamateMain(int argc, char** argv) {
    FcCtx ctx;
    const char* file = NULL;
    const char* text;
    const char* error = NULL;
    bool check = false, bad = false;
    size_t n = 0, had = 0;
    uint8_t* old;
    int i, result = 0;
    for (i = 2; i < argc; i++) {
        if (strcmp(argv[i], "--check") == 0) check = true;
        else if (!file && argv[i][0] != '-') file = argv[i];
        else bad = true;
    }
    if (!file || bad) { fprintf(stderr, "amalgamate <file.c> [--check]   libforge as one C file (native/forge/forge.c with what it includes)\n"); return 2; }
    fcCtxInit(&ctx);
    text = neAmalgamation(&ctx, &n, &error);
    if (!text) { fprintf(stderr, "amalgamate: %s\n", error); fcCtxFree(&ctx); return 1; }
    old = fbReadFile(file, &had);
    if (old && had == n && memcmp(old, text, n) == 0) printf("%s (up to date)\n", file);
    else if (check) { fprintf(stderr, "%s is not current: run `forge amalgamate %s`\n", file, file); result = 1; }
    else if (!nbWriteBytes(&ctx, file, text, n)) { fprintf(stderr, "amalgamate: cannot write %s\n", file); result = 1; }
    else printf("%s (%zu bytes)\n", file, n);
    free(old);
    fcCtxFree(&ctx);
    return result;
}
