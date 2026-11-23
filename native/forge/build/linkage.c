/* native/forge/build/linkage.c - src/fire.Compiler/NativeBuilder.cs (commit 661e55b3): linkage "static" and "shared" of a native build - the choice in Build
 * (shared where gcc/clang can, else static; static with `files`: WriteStaticFiles), BuildStatic (every library module a static or shared library in the
 * user-wide cache, built once for its content and the switches of the program; the program's own C compiled and linked with them), LinkDlls (Windows),
 * WriteStaticFiles; and the dry run of `forge-c buildplan static|shared` (FrontendDump.DumpStaticPlan). The libraries that are not in the cache are compiled
 * one after the other (C#: side by side, Parallel.For - the log is the same, each library's lines together in the order of the plan). */

static NbResult nbBuild(FcCtx* c, const NbBuild* b, NbConfig* config, const FgTarget* target, NbToolchain* toolchain, const char* output);
static NbResult nbBuildPluginC(FcCtx* c, const NbBuild* b, NbConfig* config, const FgTarget* target, NbToolchain* toolchain, const char* output);   /* (build/plugin.c) */

/* QuickNotApplied: a quick build (`--quick`) with linkage static, shared or plugin builds as without it - the log says so */
static NbResult nbQuickNotApplied(FcCtx* c, const NbConfig* config, NbResult r) {
    if (config->quick >= 0) r.log = fcFmt(c, "%s: only with linkage \"source\" - linkage \"%s\" builds as without it.\n%s", nbQuickLabel(c, config->quick, NULL), config->linkage, r.log);
    return r;
}
/* the build with another linkage (AsSource and the fallback of "shared") */
static NbResult nbBuildAs(FcCtx* c, const NbBuild* b, NbConfig* config, const FgTarget* target, NbToolchain* toolchain, const char* output, const char* linkage, const char* why) {
    const char* before = config->linkage;
    NbResult r;
    config->linkage = linkage;
    r = nbBuild(c, b, config, target, toolchain, output);
    config->linkage = before;
    if (why) r.log = fcFmt(c, "%s\n%s", why, r.log);
    return r;
}
static bool nbIsHostTarget(const FgTarget* target) { return streq0(target->name, fgHostTarget()->name); }
/* the milliseconds of the log (Stopwatch) */
static long long nbNowMs(void) { struct timeval tv; gettimeofday(&tv, NULL); return (long long)tv.tv_sec * 1000 + tv.tv_usec / 1000; }
/* a dry run: the line of the fallback of "shared" before the log of the plan (NULL: none) */
static const char* nbLinkageNote;

static NbResult nbBuildStatic(FcCtx* c, const NbBuild* b, NbConfig* config, const FgTarget* target, NbToolchain* toolchain, const char* output, bool shared);
static NbResult nbWriteStaticFiles(FcCtx* c, const NbBuild* b, NbConfig* config, const FgTarget* target, NbToolchain* toolchain, const char* output);
/* The part of Build that chooses by the linkage (after WithDirection); handled: true and the result in *r - false: linkage "source" */
static bool nbBuildLinkage(FcCtx* c, const NbBuild* b, NbConfig* config, const FgTarget* target, NbToolchain* toolchain, const char* output, NbResult* r) {
    const char* kind = nbKind(toolchain);
    bool filesOrCustom = streq0(kind, "files") || streq0(kind, "custom");
    if (!config->linkage) return false;
    /* linkage "shared" (ROADMAP 9, phase C): Linux and macOS with gcc or clang, Windows with MinGW; elsewhere it is "static" and the log says so */
    if (fcAsciiIEq(config->linkage, "shared") && !filesOrCustom) {
        bool can = (streq0(kind, "gcc") || streq0(kind, "clang")) && ((streq0(target->platform, "posix") && nbIsHostTarget(target)) || streq0(target->platform, "windows"));
        if (can) { *r = nbQuickNotApplied(c, config, nbBuildStatic(c, b, config, target, toolchain, output, true)); return true; }
        if (b->dryRun) nbLinkageNote = fcFmt(c, "Linkage \"shared\" is only available on Linux and macOS with gcc or clang and on Windows with MinGW (not for the target '%s' with '%s'): built with linkage \"static\".", target->name, kind);
        *r = nbBuildAs(c, b, config, target, toolchain, output, "static", NULL);
        nbLinkageNote = NULL;
        r->log = fcFmt(c, "Linkage \"shared\" is only available on Linux and macOS with gcc or clang and on Windows with MinGW (not for the target '%s' with '%s'): built with linkage \"static\".\n%s",
            target->name, kind, r->log);
        return true;
    }
    if (fcAsciiIEq(config->linkage, "plugin")) { *r = nbQuickNotApplied(c, config, nbBuildPluginC(c, b, config, target, toolchain, output)); return true; }
    if (fcAsciiIEq(config->linkage, "static") && !filesOrCustom) { *r = nbQuickNotApplied(c, config, nbBuildStatic(c, b, config, target, toolchain, output, false)); return true; }
    if (fcAsciiIEq(config->linkage, "static") && streq0(kind, "files")) { *r = nbWriteStaticFiles(c, b, config, target, toolchain, output); return true; }
    return false;
}

/* the link of a build (Linker.CompileAndLink with the toolchain's symbol) */
static FcLinked* nbLinkFor(FcCtx* c, const NbBuild* b, NbConfig* config, const FgTarget* target, const char* kind, FcNatives** registry, FgOptions* o) {
    memset(o, 0, sizeof *o);
    o->target = target;
    o->mode = b->mode;
    o->intWidth = b->intWidth;
    o->floatWidth = b->floatWidth;
    o->stripping = nbStrippingOf(config);
    o->optimize = nbOptimizeOf(config, target);
    o->globalJobs = config->globalJobs ? nbGlobalJobsParse(c, config->globalJobs) + 1 : 0;
    return b->link ? b->link(c, b, o, registry, kind) : fgLinkNativeKind(c, b->path, b->prelude, b->storeRoot, b->strict, o, registry, kind);
}

/* GenerateStatic into the cache of the build (a dry run: a cache of its own, the generator named "cgen" - FrontendDump.DumpStaticPlan); NULL: not possible (why) */
static FgStaticPlan* nbGenerateStatic(FcCtx* c, FcLinked* linked, FcNatives* registry, const FgTarget* target, NbToolchain* toolchain, NbConfig* config, FcStore* store, const char* cacheRoot,
    bool shared, const char** why) {
    FgStaticPlan* volatile plan = NULL;
    *why = NULL;
    FC_TRY(c) {
        plan = GenerateStatic(c, linked, registry->names, target, nbToolchainStamp(c, toolchain), nbStrippingOf(config), cacheRoot, shared, config->stats && !shared, store);
    } FC_CATCH(c) {
        if (c->err.kind != FC_ERR_NATIVE) fcRethrow(c);
        *why = fcFmt(c, "Linkage \"static\" is not possible for this program (%s): built with linkage \"source\".", c->err.message);
    } FC_END_TRY;
    return plan;
}

/* Linkage "shared" on Windows: links the DLLs that are not in the cache from their object files, in the order of the plan. NULL: done, else the failure. */
static const char* nbLinkDlls(FcCtx* c, const NbToolchain* toolchain, const FgTarget* target, FcList* all, FcList* missing, FcList* objects, FcList* unitObjects, const char* programFile,
    const char* workDir, FcBuf* log) {
    const char* found = nbFindC(c, toolchain);
    const char* cc = found ? found : nbEffectiveCCompiler(c, toolchain);
    FcList* libs = fcListNew(c);
    FcList* before = fcListNew(c);
    FILE* f;
    int i, k;
    if (toolchain->libs) for (i = 0; i < toolchain->libs->count; i++) fcListAdd(c, libs, toolchain->libs->items[i]);
    for (i = 0; i < target->linkLibCount; i++) fcListAdd(c, libs, (void*)target->linkLibs[i]);
    if ((f = fopen(programFile, "rb")) != NULL) {
        char line[4096];
        while (fgets(line, sizeof line, f)) {
            if (strncmp(line, "// fire-link: ", 14) == 0) {
                const char* flag = fcFmt(c, "-l%s", fgTrim(c, line + 14));
                if (!fcListHasStr(libs, flag)) fcListAdd(c, libs, (void*)flag);
            }
        }
        fclose(f);
    }
    for (i = 0; i < all->count; i++) {
        FgStaticLibrary* library = (FgStaticLibrary*)all->items[i];
        const char* file = fgLibraryFile(c, library, ".dll");
        int at = -1;
        for (k = 0; k < missing->count; k++) if (missing->items[k] == library) at = k;
        if (at >= 0) {
            const char* tempDir = nbCombine(c, library->directory, nbGuid(c));
            const char* temp = nbCombine(c, tempDir, fcBaseName(file));
            long long dllWatch = nbNowMs();
            FcBuf args = {0};
            NbRunResult run;
            nbMkdirs(c, tempDir);
            fcBufFmt(&args, "-shared -static-libgcc -o \"%s\" \"%s\"", temp, (const char*)objects->items[at]);
            for (k = 0; k < ((FcList*)unitObjects->items[at])->count; k++) fcBufFmt(&args, " \"%s\"", (const char*)((FcList*)unitObjects->items[at])->items[k]);   /* (its units, `native.units`) */
            for (k = 0; k < before->count; k++) fcBufFmt(&args, " \"%s\"", (const char*)before->items[k]);
            fcBufFmt(&args, " -Wl,--export-all-symbols -s %s", fgJoin(c, libs, " "));
            fcBufFmt(log, "%s %s\n", cc, args.p);
            run = nbRun(cc, args.p, workDir);
            nbAppendRun(log, &run);
            if (!run.ok || !nbFileExists(temp)) { nbDeleteTree(c, tempDir); fcBufFree(&args); return fcFmt(c, "The DLL of the module '%s' could not be linked (see the build log).", library->module); }
            rename(temp, file);
            nbDeleteTree(c, tempDir);
            CommitStatic(library);
            fcBufFmt(log, "module %s: built in %lld ms -> %s\n", library->module, nbNowMs() - dllWatch, file);
            fcBufFree(&args);
        }
        fcListAdd(c, before, (void*)file);
    }
    return NULL;
}

/* the object of a library and those of its units for nbArchiveCommand, which puts its argument in quotes: `a.o" "u.o` */
static const char* nbObjectsArg(FcCtx* c, const char* obj, const FcList* more) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, obj);
    for (i = 0; more && i < more->count; i++) fcBufFmt(&b, "\" \"%s", (const char*)more->items[i]);
    return fcBufDone(c, &b);
}
/* the commands of a library that is not in the cache (the dry run prints them, BuildLibrary runs them) */
static NbResult nbBuildStatic(FcCtx* c, const NbBuild* b, NbConfig* config, const FgTarget* target, NbToolchain* toolchain, const char* output, bool shared) {
    NbResult r = { false, output, "", NULL };
    const char* kind = nbKind(toolchain);
    FgOptions o;
    FcNatives* registry;
    FcLinked* linked;
    FcStore store;
    FcDict* instImports = fcDictNew(c, FC_KEY_PTR);
    FgStaticPlan* plan;
    const char* why;
    const char* cacheRoot;
    FcBuf log = {0};
    NbToolchain* cppToolchain;
    const char* configDirectory = config->path ? fcDirectoryName(c, config->path) : NULL;
    const char* workDir;
    const char* cFile;
    const char* nativesFile;
    const char* exe = fcFullPath(c, ".", output);
    bool dll = shared && streq0(target->platform, "windows");
    const char* ext;
    FcList* missing;
    FcList* unitObjects;          /* per library of `missing`: the objects of its units (`native.units`) */
    NbToolchain* programToolchain;
    FcList* objects;
    FcList* libraries;
    FcList* commands;
    const char* statsMap = NULL;
    const char* identity = fgGeneratorIdentity;
    bool gui, linkedOk = true, built;
    long long watch = nbNowMs();
    int i, k;
    if (!nbFind(c, toolchain)) return nbBuildAs(c, b, config, target, toolchain, output, "source", NULL);   /* (no compiler: the normal build offers one) */
    linked = nbLinkFor(c, b, config, target, kind, &registry, &o);
    memset(&store, 0, sizeof store);
    store.root = b->storeRoot;
    if (b->overlay) b->overlay(c, b, &store);
    cacheRoot = b->dryRun ? fcFmt(c, "%sfire-plan-cache-%s", nbTempPath(c), nbGuid(c)) : nbNativeCacheRoot(c);
    if (b->dryRun) fgGeneratorIdentity = "cgen";
    FC_TRY(c) { plan = nbGenerateStatic(c, linked, registry, target, toolchain, config, &store, cacheRoot, shared, &why); }
    FC_CATCH(c) { fgGeneratorIdentity = identity; if (b->dryRun) nbDeleteTree(c, cacheRoot); fcRethrow(c); } FC_END_TRY;
    fgGeneratorIdentity = identity;
    if (!plan) { if (b->dryRun) nbDeleteTree(c, cacheRoot); return nbBuildAs(c, b, config, target, toolchain, output, "source", why); }
    if (plan->natives && (cppToolchain = nbCppFallback(c, toolchain)) != NULL) {
        NbResult inner;
        const char* note = nbCppFallbackNote(c, toolchain, cppToolchain, plan->natives);
        if (b->dryRun) nbDeleteTree(c, cacheRoot);
        nbPlanNote = note;
        inner = nbBuild(c, b, config, target, cppToolchain, output);
        nbPlanNote = NULL;
        inner.log = fcFmt(c, "%s\n%s", note, inner.log);
        return inner;
    }
    fcBufStr(&log, "");
    fcOut8(&log, plan->log);
    if (!b->dryRun) fcBufFmt(&log, "generated in %lld ms\n", nbNowMs() - watch);
    workDir = b->keepSources && !b->dryRun ? fcFmt(c, "%s.src", exe) : fcFmt(c, "%sfire-native-%s", nbTempPath(c), nbGuid(c));
    cFile = nbWriteFiles(c, &store, instImports, workDir, plan->programC, target, "fire_program.c", configDirectory, plan->natives, NULL);
    nativesFile = plan->natives ? nbCombine(c, workDir, "fire_natives.cpp") : NULL;
    gui = linked->asmInfo && linked->asmInfo->gui;
    ext = dll ? ".dll" : shared ? ".so" : streq0(kind, "msvc") ? ".lib" : ".a";
    missing = fcListNew(c);
    objects = fcListNew(c);
    for (i = 0; i < plan->libraries->count; i++) if (!((FgStaticLibrary*)plan->libraries->items[i])->cached) fcListAdd(c, missing, plan->libraries->items[i]);
    commands = fcListNew(c);
    unitObjects = fcListNew(c);
    programToolchain = toolchain;
    /* the libraries that are not in the cache: each its C (the generator wrote it there) to an object file, that into the library, then its interface */
    for (i = 0; i < missing->count; i++) {
        FgStaticLibrary* library = (FgStaticLibrary*)missing->items[i];
        const char* obj = nbCombine(c, workDir, fcFmt(c, "%s%s", library->libraryName, streq0(kind, "msvc") ? ".obj" : ".o"));
        const char* temp = nbCombine(c, library->directory, b->dryRun ? fcFmt(c, "TEMP%s", ext) : fcFmt(c, "%s%s", nbGuid(c), ext));
        FcBuf libLog = {0};
        long long libraryWatch = nbNowMs();
        /* (the units of the package of the module, `native.units`: compiled once - with the library, which the cache keeps - and put into it; the module's C gets
         * FIRE_PKG_UNITS_<IMPORT>, so the bridge calls them directly) */
        FcList* units = nbModuleUnits(c, &store, instImports, library->module);
        FcList* libUnitObjects = fcListNew(c);
        NbToolchain* lt = nbWithUnitDefines(c, programToolchain, units);
        fcBufStr(&libLog, "");
        fcListAdd(c, objects, (void*)obj);
        fcListAdd(c, unitObjects, libUnitObjects);
        if (units->count > 0) {
            NbToolchain* ut = nbUnitToolchain(c, programToolchain, shared && !dll);
            if (nbIsWatcom(kind))
                fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "native-config: the package '%s' has a unit of its own (`native.units`), which the toolchain '%s' does not build.", ((NbPkgUnit*)units->items[0])->import, kind));
            for (k = 0; k < units->count; k++) {
                const NbPkgUnit* u = (const NbPkgUnit*)units->items[k];
                const char* file = nbCombine(c, workDir, fcFmt(c, "%s_%s", library->libraryName, u->name));
                const char* unitObj = nbChangeExtension(c, file, streq0(kind, "msvc") ? ".obj" : ".o");
                NbCommand* compile;
                NbRunResult run;
                if (!nbCopyFile(c, u->file, file)) { r.error = fcFmt(c, "native-config: the unit '%s' of the package '%s' cannot be read.", u->file, u->import); goto failed; }
                compile = nbCompilerCommand(c, ut, target, file, workDir, unitObj, false, NULL, false, "c", true, NULL, NULL, NULL, false);
                fcListAdd(c, libUnitObjects, (void*)unitObj);
                if (b->dryRun) { fcListAdd(c, commands, compile); continue; }
                fcBufFmt(&libLog, "%s %s\n", compile->exe, compile->args);
                run = nbRun(compile->exe, compile->args, workDir);
                nbAppendRun(&libLog, &run);
                if (!run.ok || !nbFileExists(unitObj)) {
                    fcBufPut(&log, libLog.p, libLog.len);
                    r.error = fcFmt(c, "The C compiler reported errors in the unit '%s' of the package '%s' (see the build log).", fgFileNameOf(c, u->file), u->import);
                    goto failed;
                }
            }
        }
        if (dll) {
            /* a DLL: here only its object file - it is linked below, after the DLLs it imports from */
            NbCommand* compile = nbCompilerCommand(c, lt, target, library->cFile, workDir, obj, false, NULL, false, "c", true, NULL, NULL, NULL, false);
            if (b->dryRun) { fcListAdd(c, commands, compile); continue; }
            fcBufFmt(&libLog, "%s %s\n", compile->exe, compile->args);
            {
                NbRunResult run = nbRun(compile->exe, compile->args, workDir);
                nbAppendRun(&libLog, &run);
                fcBufPut(&log, libLog.p, libLog.len);
                if (!run.ok) { r.error = fcFmt(c, "The C compiler reported errors in the library module '%s' (see the build log).", library->module); goto failed; }
            }
            fcBufFmt(&log, "module %s: compiled in %lld ms\n", library->module, nbNowMs() - libraryWatch);
            continue;
        }
        if (shared) {
            /* a shared library: position-independent, its own name inside; what it needs of the program the program exports (-rdynamic) */
            const char* so = fgLibraryFile(c, library, ext);
            NbCommand* link = nbCompilerCommand(c, lt, target, library->cFile, workDir, temp, true, NULL, false, "c", false, libUnitObjects->count > 0 ? libUnitObjects : NULL, NULL, NULL, false);
            const char* args = fcFmt(c, "%s -Wl,-soname,%s -lm", link->args, fcBaseName(so));
            NbRunResult run;
            if (b->dryRun) { NbCommand* k2 = (NbCommand*)fcAlloc(c, sizeof *k2); k2->exe = link->exe; k2->args = args; fcListAdd(c, commands, k2); continue; }
            fcBufFmt(&libLog, "%s %s\n", link->exe, args);
            run = nbRun(link->exe, args, workDir);
            nbAppendRun(&libLog, &run);
            fcBufPut(&log, libLog.p, libLog.len);
            if (!run.ok || !nbFileExists(temp)) { remove(temp); r.error = fcFmt(c, "The C compiler reported errors in the library module '%s' (see the build log).", library->module); goto failed; }
            rename(temp, so);
            CommitStatic(library);
            fcBufFmt(&log, "module %s: built in %lld ms -> %s\n", library->module, nbNowMs() - libraryWatch, so);
            continue;
        }
        {
            NbCommand* compile = nbCompilerCommand(c, lt, target, library->cFile, workDir, obj, false, NULL, false, "c", true, NULL, NULL, NULL, true);
            NbCommand* archive;
            NbRunResult run;
            const char* lib = fgLibraryFile(c, library, ext);
            archive = nbArchiveCommand(c, lt, temp, nbObjectsArg(c, obj, libUnitObjects));
            if (b->dryRun) { fcListAdd(c, commands, compile); fcListAdd(c, commands, archive); continue; }
            fcBufFmt(&libLog, "%s %s\n", compile->exe, compile->args);
            run = nbRun(compile->exe, compile->args, workDir);
            nbAppendRun(&libLog, &run);
            if (!run.ok) { fcBufPut(&log, libLog.p, libLog.len); r.error = fcFmt(c, "The C compiler reported errors in the library module '%s' (see the build log).", library->module); goto failed; }
            fcBufFmt(&libLog, "%s %s\n", archive->exe, archive->args);
            run = nbRun(archive->exe, archive->args, workDir);
            nbAppendRun(&libLog, &run);
            fcBufPut(&log, libLog.p, libLog.len);
            if (!run.ok || !nbFileExists(temp)) { remove(temp); r.error = fcFmt(c, "The library of the module '%s' could not be made (see the build log).", library->module); goto failed; }
            rename(temp, lib);
            CommitStatic(library);
            fcBufFmt(&log, "module %s: built in %lld ms -> %s\n", library->module, nbNowMs() - libraryWatch, lib);
        }
    }
    if (dll && !b->dryRun) {
        const char* failure = nbLinkDlls(c, toolchain, target, plan->libraries, missing, objects, unitObjects, cFile, workDir, &log);
        if (failure) { r.error = failure; goto failed; }
    }
    /* (a library before the ones it needs: the linker resolves in that order) */
    libraries = fcListNew(c);
    watch = nbNowMs();
    {
        /* (the units of a package whose natives are in the program's file - no module of its own: compiled with the program and linked into it, as with
         * linkage "source") */
        FcList* all = nbPackageUnits(c, &store, instImports, linked);
        NbToolchain* ut = NULL;
        for (i = 0; i < all->count; i++) {
            const NbPkgUnit* u = (const NbPkgUnit*)all->items[i];
            const char* key = fcFmt(c, "pkg:%s", fcAsciiLower(c, u->import));
            const char* file;
            const char* unitObj;
            NbCommand* compile;
            NbRunResult run;
            bool inModule = false;
            for (k = 0; k < plan->libraries->count; k++) if (streq0(((FgStaticLibrary*)plan->libraries->items[k])->module, key)) inModule = true;
            if (inModule) continue;
            if (nbIsWatcom(kind))
                fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "native-config: the package '%s' has a unit of its own (`native.units`), which the toolchain '%s' does not build.", u->import, kind));
            if (!ut) ut = nbUnitToolchain(c, toolchain, false);
            file = nbCombine(c, workDir, u->name);
            unitObj = nbChangeExtension(c, file, streq0(kind, "msvc") ? ".obj" : ".o");
            if (!nbCopyFile(c, u->file, file)) { r.error = fcFmt(c, "native-config: the unit '%s' of the package '%s' cannot be read.", u->file, u->import); goto failed; }
            compile = nbCompilerCommand(c, ut, target, file, workDir, unitObj, false, NULL, gui, "c", true, NULL, NULL, NULL, false);
            fcListAdd(c, libraries, (void*)unitObj);
            if (b->dryRun) { fcListAdd(c, commands, compile); continue; }
            fcBufFmt(&log, "%s %s\n", compile->exe, compile->args);
            run = nbRun(compile->exe, compile->args, workDir);
            nbAppendRun(&log, &run);
            if (!run.ok || !nbFileExists(unitObj)) { r.error = fcFmt(c, "The C compiler reported errors in the unit '%s' of the package '%s' (see the build log).", fgFileNameOf(c, u->file), u->import); goto failed; }
        }
    }
    for (i = plan->libraries->count - 1; i >= 0; i--) fcListAdd(c, libraries, (void*)fgLibraryFile(c, (FgStaticLibrary*)plan->libraries->items[i], ext));
    if (config->stats && !shared && nsMapOptions(c, kind, "x")) statsMap = nsMapFileOf(c, exe);
    {
        /* (the program gets the defines of the units its packages have, as with linkage "source") */
        FcList* programCommands = nbBuildCommands(c, nbWithUnitDefines(c, toolchain, nbPackageUnits(c, &store, instImports, linked)), target, cFile, nativesFile, workDir, exe, gui, libraries);
        for (k = 0; k < programCommands->count; k++) {
            NbCommand* cmd = (NbCommand*)programCommands->items[k];
            const char* args = cmd->args;
            NbRunResult run;
            /* (shared: the program exports what the libraries need of it, and finds them next to itself) */
            if (shared && !dll && k == programCommands->count - 1) args = fcFmt(c, "%s -rdynamic -Wl,-rpath,$ORIGIN", args);
            /* (`--stats`: the map of the program - the code of the library modules is counted by the library it comes from) */
            if (statsMap) args = k == programCommands->count - 1 ? fcFmt(c, "%s %s", args, nsMapOptions(c, kind, statsMap)) : fcFmt(c, "%s -ffunction-sections", args);
            if (b->dryRun) { NbCommand* k2 = (NbCommand*)fcAlloc(c, sizeof *k2); k2->exe = cmd->exe; k2->args = args; fcListAdd(c, commands, k2); continue; }
            fcBufFmt(&log, "%s %s\n", cmd->exe, args);
            run = nbRun(cmd->exe, args, workDir);
            nbAppendRun(&log, &run);
            if (!(linkedOk = run.ok)) break;
        }
    }
    if (b->dryRun) {
        /* FrontendDump.DumpStaticPlan: the commands, the files of the build, the C file of each library */
        FcBuf* p = b->plan;
        FcList* files = nbSortedFiles(c, workDir, true);
        const char* text = fcBufDone(c, &log);
        if (nbPlanNote) text = fcFmt(c, "%s\n%s", nbPlanNote, text);
        if (config->quick >= 0) text = fcFmt(c, "%s: only with linkage \"source\" - linkage \"%s\" builds as without it.\n%s", nbQuickLabel(c, config->quick, NULL), config->linkage, text);
        if (nbLinkageNote) text = fcFmt(c, "%s\n%s", nbLinkageNote, text);
        text = fgReplace(c, fgReplace(c, text, workDir, "<build>"), cacheRoot, "<cache>");
        fcBufFmt(p, "toolchain %s\n", kind);
        if (*text) { fcBufStr(p, "log\n"); fcOut8(p, text); }
        for (i = 0; i < commands->count; i++) {
            NbCommand* cmd = (NbCommand*)commands->items[i];
            fcBufFmt(p, "command %s %s\n", cmd->exe, fgReplace(c, fgReplace(c, fgReplace(c, cmd->args, exe, "<program>"), workDir, "<build>"), cacheRoot, "<cache>"));
        }
        fcBufStr(p, "key none\n");
        for (i = 0; i < files->count; i++) {
            size_t n = 0;
            uint8_t* data = fbReadFile(nbCombine(c, workDir, (const char*)files->items[i]), &n);
            FcSha256 h;
            fcShaInit(&h);
            if (data) fcShaUpdate(&h, data, n);
            free(data);
            fcBufFmt(p, "file %s %s\n", (const char*)files->items[i], fcShaHex(c, &h));
        }
        for (i = 0; i < plan->libraries->count; i++) {
            const FgStaticLibrary* l = (const FgStaticLibrary*)plan->libraries->items[i];
            size_t n = 0;
            uint8_t* data = fbReadFile(l->cFile, &n);
            FcSha256 h;
            fcShaInit(&h);
            if (data) fcShaUpdate(&h, data, n);
            fcBufFmt(p, "library %s %s%s%s\n", l->module, l->libraryName, l->cached ? " cached " : " ", data ? fcShaHex(c, &h) : "-");
            free(data);
        }
        if (plan->host) { fcBufStr(p, "host\n"); fcOut8(p, fgHostInfoJson(c, plan->host)); fcBufChar(p, '\n'); }
        nbDeleteTree(c, workDir);
        nbDeleteTree(c, cacheRoot);
        r.ok = true; r.output = exe; r.log = "";
        return r;
    }
    fcBufFmt(&log, "program built in %lld ms\n", nbNowMs() - watch);
    built = linkedOk && nbFileExists(exe);
    if (built) nbApplyPeMetadata(c, exe, linked->asmInfo, target, &log);
    if (built && config->stats) {
        if (shared) fcBufStr(&log, "--stats: not with linkage \"shared\" (the libraries are not in the program) - use static or source.\n");
        else nbWriteStats(c, target, exe, workDir, cFile, statsMap, plan->funcs, &log);
    }
    if (built) { const char* after = nbAfterLink(c, toolchain, target, exe); if (after) fcBufFmt(&log, "%s\n", after); }
    /* (shared: the libraries next to the program - it finds them there, wherever the cache is) */
    if (built && shared) {
        for (i = 0; i < plan->libraries->count; i++) {
            const char* from = fgLibraryFile(c, (FgStaticLibrary*)plan->libraries->items[i], ext);
            const char* next = nbCombine(c, nbDirName(c, exe), fcBaseName(from));
            nbCopyFile(c, from, next);
            fcBufFmt(&log, "%s next to the program\n", fcBaseName(next));
        }
        /* (what a plugin built for this program needs to know: `--linkage plugin --host <program>`) */
        if (plan->host) {
            const char* stem = fcBaseName(exe);
            const char* dot = strrchr(stem, '.');
            for (i = 0; i < plan->host->modules->count; i++) { FgPluginHostModule* m = (FgPluginHostModule*)plan->host->modules->items[i]; m->library = fcFmt(c, "%s%s", m->library, ext); }
            if (plan->host->runtime) plan->host->runtime = fcFmt(c, "%s%s", plan->host->runtime, ext);
            nbWriteText(c, nbCombine(c, nbDirName(c, exe), fcFmt(c, "%s.firehost", dot ? fcStrN(c, stem, (size_t)(dot - stem)) : stem)), fgHostInfoJson(c, plan->host));
        }
    }
    if (!b->keepSources) nbDeleteTree(c, workDir);
    r.ok = built;
    r.output = exe;
    r.log = fcBufDone(c, &log);
    if (!built) r.error = "The C++ compiler of the native build reported errors (see the build log).";
    return r;
failed:
    if (!b->keepSources) nbDeleteTree(c, workDir);
    r.ok = false;
    r.output = exe;
    r.log = fcBufDone(c, &log);
    return r;
}

/* Linkage "static" with a toolchain that only writes files (an ESP-IDF component): the program's C and the C of every library module as translation units. */
static NbResult nbWriteStaticFiles(FcCtx* c, const NbBuild* b, NbConfig* config, const FgTarget* target, NbToolchain* toolchain, const char* output) {
    NbResult r = { false, output, "", NULL };
    FgOptions o;
    FcNatives* registry;
    FcLinked* linked = nbLinkFor(c, b, config, target, nbKind(toolchain), &registry, &o);
    FcStore store;
    FcDict* instImports = fcDictNew(c, FC_KEY_PTR);
    FgStaticPlan* plan;
    const char* why;
    const char* directory = fcFullPath(c, ".", output);
    const char* configDirectory = config->path ? fcDirectoryName(c, config->path) : NULL;
    FcList* files = fcListNew(c);
    FcList* existing;
    FcBuf log = {0};
    FcBuf srcs = {0};
    int i;
    memset(&store, 0, sizeof store);
    store.root = b->storeRoot;
    if (b->overlay) b->overlay(c, b, &store);
    {
        FgStaticPlan* volatile p = NULL;
        why = NULL;
        FC_TRY(c) { p = GenerateStatic(c, linked, registry->names, target, fcFmt(c, "files|%s", toolchain->layout ? toolchain->layout : ""), nbStrippingOf(config), nbNativeCacheRoot(c), false, false, &store); }
        FC_CATCH(c) { if (c->err.kind != FC_ERR_NATIVE) fcRethrow(c); why = fcFmt(c, "Linkage \"static\" is not possible for this program (%s): written with linkage \"source\".", c->err.message); } FC_END_TRY;
        plan = p;
    }
    if (!plan) return nbBuildAs(c, b, config, target, toolchain, output, "source", why);
    nbWriteFiles(c, &store, instImports, directory, plan->programC, target, "fire_program.c", configDirectory, plan->natives, NULL);
    fcListAdd(c, files, (void*)"fire_program.c");
    if (plan->natives) fcListAdd(c, files, (void*)"fire_natives.cpp");
    for (i = 0; i < plan->libraries->count; i++) {
        FgStaticLibrary* library = (FgStaticLibrary*)plan->libraries->items[i];
        const char* name = fcFmt(c, "%s.c", library->libraryName);
        nbCopyFile(c, library->cFile, nbCombine(c, directory, name));
        CommitStatic(library);
        fcListAdd(c, files, (void*)name);
    }
    existing = nbSortedFiles(c, directory, false);
    for (i = 0; i < existing->count; i++) {
        const char* f = (const char*)existing->items[i];
        if (fcStartsWith(f, "fire_m") && fcEndsWith(f, ".c") && !strchr(f, '/') && !fcListHasStr(files, f)) remove(nbCombine(c, directory, f));
    }
    fcBufStr(&log, "");
    fcOut8(&log, plan->log);
    fcBufStr(&srcs, "");
    for (i = 0; i < files->count; i++) fcBufFmt(&srcs, "%s\"%s\"", i ? " " : "", (const char*)files->items[i]);
    if (toolchain->layout && streq0(toolchain->layout, "idf-component"))
        nbWriteText(c, nbCombine(c, directory, "CMakeLists.txt"), fcFmt(c,
            "# Generated by fire.Native for target '%s' with linkage \"static\": the program and its library modules as an ESP-IDF component (call %s from the application).\n"
            "idf_component_register(SRCS %s\n                       INCLUDE_DIRS \".\")\n"
            "target_compile_options(${COMPONENT_LIB} PRIVATE $<$<COMPILE_LANGUAGE:C>:-std=gnu11> $<$<COMPILE_LANGUAGE:CXX>:-std=gnu++17>)\n",
            target->name, target->entryName, fcBufDone(c, &srcs)));
    fcBufFmt(&log, "%s: %s, fire_rt.h and platform/%s/ for %s; the entry point is %s\n", directory, fgJoin(c, files, ", "), target->platform, target->name, target->entryName);
    if (toolchain->buildCommand && *fgTrim(c, toolchain->buildCommand)) {
        NbRunResult run = nbRunCommand(c, toolchain->buildCommand, directory);
        r.ok = run.ok;
        nbAppendRun(&log, &run);
    } else r.ok = true;
    r.output = directory;
    r.log = fcBufDone(c, &log);
    return r;
}
