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

/* A program that imports forge or script: FIRE_EMBED_PATH names the folder of the libraries for the programs forge starts (when it is not set already). */
static void neEnsureFor(FcCtx* c, const FcLinked* linked) {
    const char* error = NULL;
    const char* folder;
    const char* given = getenv("FIRE_EMBED_PATH");
    bool wanted = false;
    if (given && *given) return;
    FC_DICT_EACH(linked->nativeImports, e) {
        const char* key = (const char*)e->key;
        if (fcAsciiIEq(key, "pkg:forge") || fcAsciiIEq(key, "pkg:script")) wanted = true;
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
