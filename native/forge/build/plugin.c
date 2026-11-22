/* native/forge/build/plugin.c - src/fire.Compiler/NativeBuilder.Plugins.cs (BuildPlugin, PluginVmNatives, HostFileOf, PluginExtension) and the writer of
 * src/fire/Runtime/ModuleInterface.cs (ModuleInterface.Modules/Text, PluginBytecode.Write): `--linkage plugin` - the classes of the program's own sources as a fire
 * plugin, a shared library (`<output>.so`/`.dll`, gen/plugins.c GeneratePlugin) that a program with linkage "shared" loads with `Library.Load`, and its bytecode for
 * the VM (`<output>.fireplugin`). A dry run (`forge-c buildplan plugin`, FrontendDump/NativeBuilder.PlanPlugin) writes the files and the bytecode into the folder of
 * the build and prints the commands instead of running them. After pkglib.c (the libraries of the natives for the VM). */

/* ModuleInterface.Format: the format of the interfaces the VM compares (not the native backend's FG_INTERFACE_FORMAT) */
#define NB_MODULE_INTERFACE_FORMAT 1

/* PluginExtension: the extension of a plugin's library on the target */
static const char* nbPluginExtension(const FgTarget* target) {
    if (streq0(target->platform, "windows")) return ".dll";
#ifdef __APPLE__
    return ".dylib";
#else
    return ".so";
#endif
}
/* Path.GetFileNameWithoutExtension */
static const char* nbStem(FcCtx* c, const char* path) {
    const char* name = nbFileName(path);
    const char* dot = strrchr(name, '.');
    return dot ? fcStrN(c, name, (size_t)(dot - name)) : name;
}
/* HostFileOf: the file next to a program with linkage "shared" that describes it for the plugins built for it (`--host`) */
static const char* nbHostFileOf(FcCtx* c, const char* exe) {
    const char* full = fcFullPath(c, ".", exe);
    return nbCombine(c, nbDirName(c, full), fcFmt(c, "%s.firehost", nbStem(c, full)));
}

/* ModuleInterface.Modules + Text, ordered by name: the library modules of a linked program with their interfaces ("format:hash") - FgModuleDep* */
static FcList* nbModuleInterfaces(FcCtx* c, FcLinked* linked, FcList* natives) {
    FcList* result = fcListNew(c);
    FcList* specs;
    FgGen* probe;
    int i, k;
    if (!linked->sourceModules) return result;
    specs = fcListCopy(c, ModulesOf(c, linked));
    for (i = 1; i < specs->count; i++)
        for (k = i; k > 0 && strcmp(((FgModuleSpec*)specs->items[k - 1])->name, ((FgModuleSpec*)specs->items[k])->name) > 0; k--) {
            void* t = specs->items[k]; specs->items[k] = specs->items[k - 1]; specs->items[k - 1] = t;
        }
    probe = fgGenNew(c, linked, natives, fgHostTarget());
    for (i = 0; i < specs->count; i++) {
        FgModuleDep* d = (FgModuleDep*)fcAlloc(c, sizeof *d);
        d->spec = (FgModuleSpec*)specs->items[i]; d->module = d->spec->name;
        d->abi = fcFmt(c, "%d:%s", NB_MODULE_INTERFACE_FORMAT, AbiHash(probe, linked, d->spec));
        fcListAdd(c, result, d);
    }
    return result;
}

/* PluginVmNatives: the natives in C++ of a plugin for the VM (its project's, its packages'): their libraries for this machine (C ABI, PackageLibrary - built now when
 * needed) copied next to its bytecode, and which native is in which (pairs native, library file). One that cannot be built is left out (the VM says the native is
 * missing when it links the plugin). */
static FcList* nbPluginVmNatives(FcCtx* c, FcLinked* program, FcStore* store, const char* directory, FcBuf* log) {
    FcList* result = fcListNew(c);
    FcList* copied = fcListNew(c);
    FcList* keys = fcListNew(c);
    FcDict* cache = fcDictNew(c, FC_KEY_PTR);
    int i, f, k;
    if (!program->packageNatives || program->packageNatives->count == 0 || !program->packageNativeLibraries) return result;
    if (!store->loaded) fcStoreLoad(c, store);
    FC_DICT_EACH(program->nativeImports, e) if (fcStartsWith((const char*)e->key, "pkg:")) fcListAdd(c, keys, (void*)e->key);
    if (keys->count > 1) qsort(keys->items, (size_t)keys->count, sizeof(void*), fcCmpOrdinal);
    for (i = 0; i < keys->count; i++) {
        FcPkgImport* pi = fcStoreFindKey(c, store, (const char*)keys->items[i]);
        FgInstImport* imp;
        bool library = false;
        NlResult r;
        const char* file;
        if (!pi) continue;
        imp = fgInstalledImport(c, cache, pi);
        if (!imp->native || imp->native->isC) continue;
        for (f = 0; f < imp->native->functions->count; f++) if (!((FgPkgFn*)imp->native->functions->items[f])->host) library = true;
        if (!library) continue;
        r = nlEnsure(c, cache, store, imp, log);
        if (!r.path) { fcBufFmt(log, "note: the natives in C++ of '%s' are not built for the VM (%s)\n", imp->name, r.error); continue; }
        file = nlFileNameFor(c, imp);
        if (!nbCopyFile(c, r.path, nbCombine(c, directory, file))) { fcBufFmt(log, "note: the natives in C++ of '%s' are not built for the VM (Could not write '%s'.)\n", imp->name, nbCombine(c, directory, file)); continue; }
        fcListAdd(c, copied, (void*)file);
    }
    for (i = 0; i < program->packageNatives->count && i < program->packageNativeLibraries->count; i++) {
        const char* file = (const char*)program->packageNativeLibraries->items[i];
        for (k = 0; k < copied->count; k++)
            if (fcAsciiIEq((const char*)copied->items[k], file)) { fcListAdd(c, result, program->packageNatives->items[i]); fcListAdd(c, result, (void*)file); break; }
    }
    return result;
}

/* PluginBytecode.Write: `FIREPLG2`, the length of the header, the header (JSON: its module, the interfaces of the modules it needs, its classes, natives, the
 * methods its `class extends` adds, its sources), then the program file with the line tables. deps: FgModuleDep*; packageNatives: pairs native, library. */
static bool nbWritePluginBytecode(FcCtx* c, const char* path, const char* name, FcLinked* program, FcList* nativeNames, FcList* deps, FcList* packageNatives) {
    FcList* modules = program->sourceModules;
    int count = modules ? modules->count : 0;
    FcBuf j = {0};
    FcBuf body = {0};
    FcBuf all = {0};
    FcList* classes;
    FcList* names;
    bool ok;
    int i, k;
    uint8_t len[4];
    fcBufStr(&j, "{\"Name\":");
    fgJsonStr(&j, name);
    fcBufFmt(&j, ",\"Format\":%d,\"Deps\":[", NB_MODULE_INTERFACE_FORMAT);
    for (i = 0; i < deps->count; i++) {
        const FgModuleDep* d = (const FgModuleDep*)deps->items[i];
        fcBufStr(&j, i ? ",[" : "["); fgJsonStr(&j, d->module); fcBufChar(&j, ','); fgJsonStr(&j, d->abi); fcBufChar(&j, ']');
    }
    /* (the classes of its own sources, not those that collect `class extends` of a base type) */
    names = fcListNew(c);
    FC_DICT_EACH(program->program->classes, e) {
        const FcRtClass* rc = (const FcRtClass*)e->value;
        if (rc->decl && modules && rc->decl->h.source < count && !modules->items[rc->decl->h.source] && rc->name[0] != '$') fcListAdd(c, names, (void*)rc->name);
    }
    if (names->count > 1) qsort(names->items, (size_t)names->count, sizeof(void*), fcCmpOrdinal);
    fcBufStr(&j, "],\"Classes\":");
    fgJsonStrList(&j, names);
    fcBufStr(&j, ",\"Natives\":");
    fgJsonStrList(&j, nativeNames ? nativeNames : fcListNew(c));
    fcBufStr(&j, ",\"PackageNatives\":[");
    for (i = 0; packageNatives && i < packageNatives->count; i += 2) { if (i) fcBufChar(&j, ','); fgJsonStr(&j, (const char*)packageNatives->items[i]); }
    fcBufStr(&j, "],\"PackageLibraries\":[");
    for (i = 0; packageNatives && i < packageNatives->count; i += 2) { if (i) fcBufChar(&j, ','); fgJsonStr(&j, (const char*)packageNatives->items[i + 1]); }
    fcBufStr(&j, "],\"Extensions\":[");
    {
        bool first = true;
        classes = fcListNew(c);
        FC_DICT_EACH(program->program->classes, e) fcListAdd(c, classes, e->value);
        if (classes->count > 1) qsort(classes->items, (size_t)classes->count, sizeof(void*), fgCmpRtClassName);
        for (i = 0; modules && i < classes->count; i++) {
            const FcRtClass* rc = (const FcRtClass*)classes->items[i];
            if (!rc->decl || rc->decl->h.source >= count || (!modules->items[rc->decl->h.source] && rc->name[0] != '$')) continue;
            for (k = 0; rc->decl->members && k < rc->decl->members->count; k++) {
                const FcStmt* member = (const FcStmt*)rc->decl->members->items[k];
                if (member->source >= count || modules->items[member->source]) continue;
#define NB_EXT(method, argc) do { fcBufStr(&j, first ? "[" : ",["); first = false; fgJsonStr(&j, rc->name); fcBufChar(&j, ','); fgJsonStr(&j, method); fcBufChar(&j, ','); \
                fgJsonStr(&j, argc); fcBufChar(&j, ']'); } while (0)
                if (member->kind == SK_MethodDecl) {
                    const FcMethodDecl* m = (const FcMethodDecl*)member;
                    NB_EXT(m->name, fcFmt(c, "%d", m->params ? m->params->count : 0));
                } else if (member->kind == SK_PropertyDecl) {
                    const FcPropertyDecl* p = (const FcPropertyDecl*)member;
                    if (p->getter) NB_EXT(fcFmt(c, "get_%s", p->name), "0");
                    if (p->setter) NB_EXT(fcFmt(c, "set_%s", p->name), "1");
                }
#undef NB_EXT
            }
        }
    }
    fcBufStr(&j, "],\"Sources\":[");
    {
        bool first = true;
        for (i = 1; i < count; i++) {
            if (modules->items[i]) continue;
            fcBufStr(&j, first ? "[" : ",[");
            first = false;
            fgJsonStr(&j, fcFmt(c, "%d", i)); fcBufChar(&j, ',');
            fgJsonStr(&j, program->sourceFiles && i < program->sourceFiles->count ? (const char*)program->sourceFiles->items[i] : NULL); fcBufChar(&j, ',');
            fgJsonStr(&j, program->sourceHashes && i < program->sourceHashes->count ? (const char*)program->sourceHashes->items[i] : NULL);
            fcBufChar(&j, ']');
        }
    }
    fcBufStr(&j, "]}");
    /* (the line tables go along: the debugger shows the plugin's lines) */
    fcBufStr(&body, "");
    body.len = 0;
    fcProgramFileWrite(c, program, true, &body);
    fcBufPut(&all, "FIREPLG2", 8);
    len[0] = (uint8_t)(j.len & 0xFF); len[1] = (uint8_t)((j.len >> 8) & 0xFF); len[2] = (uint8_t)((j.len >> 16) & 0xFF); len[3] = (uint8_t)((j.len >> 24) & 0xFF);
    fcBufPut(&all, (const char*)len, 4);
    fcBufPut(&all, j.p, j.len);
    fcBufPut(&all, body.p, body.len);
    ok = nbWriteBytes(c, path, all.p, all.len);
    fcBufFree(&j); fcBufFree(&body); fcBufFree(&all);
    return ok;
}

/* the link of the plugin's bytecode for the VM: the file (fcLinkFileFe) - a project links its plan (NbBuild.vmLink) */
static FcLinked* nbPluginVmLink(FcCtx* c, const NbBuild* b, FcNatives** registry, FcStore** store) {
    FcFrontend* fe;
    FcLinked* linked;
    if (b->vmLink) return b->vmLink(c, b, registry, store);
    linked = fcLinkFileFe(c, b->path, b->prelude, b->storeRoot, b->strict, b->mode, &fe);
    *registry = &fe->linker->registry;
    *store = &fe->linker->store;
    return linked;
}

/* BuildPlugin (`--linkage plugin`, ROADMAP 9 phase D b, docs/spec/NATIVE_BACKEND.md "Plugins"): with `--host <program>` it is built for that program (its switches,
 * the files of its libraries - on Windows needed: the DLL imports from them). A dry run plans the commands (FrontendDump `buildplan plugin`). */
static NbResult nbBuildPluginC(FcCtx* c, const NbBuild* b, NbConfig* config, const FgTarget* target, NbToolchain* toolchain, const char* output) {
    NbResult r = { false, output, "", NULL, false };
    const char* kind = nbKind(toolchain);
    bool dll = streq0(target->platform, "windows");
    FcBuf log = {0};
    FcList* commands = fcListNew(c);
    const char* dryCache = b->dryRun ? fcFmt(c, "%sfire-plan-cache-%s", nbTempPath(c), nbGuid(c)) : NULL;
    const char* ext = nbPluginExtension(target);
    const char* full = fcFullPath(c, ".", output);
    const char* known[5];
    const char* library;
    const char* bytecode;
    const char* name;
    const char* identity = fgGeneratorIdentity;
    const char* configDirectory = config->path ? fcDirectoryName(c, config->path) : NULL;
    const char* workDir;
    const char* cFile;
    const char* natObj = NULL;
    const char* failure = NULL;
    FgPluginHostInfo* host = NULL;
    const char* hostDir = NULL;
    FgPluginPlan* volatile plan = NULL;
    FgOptions o;
    FcNatives* registry;
    FcLinked* volatile linked = NULL;
    FcStore store;
    FcDict* instImports = fcDictNew(c, FC_KEY_PTR);
    volatile bool failed = false;
    bool ok = true, built;
    long long watch;
    int i;
    fcBufStr(&log, "");
    {
        bool can = (streq0(kind, "gcc") || streq0(kind, "clang")) && (dll || (streq0(target->platform, "posix") && nbIsHostTarget(target)));
        if (!can) failure = fcFmt(c, "A fire plugin is a shared library: Linux and macOS with gcc or clang, Windows with MinGW (not the target '%s' with '%s').", target->name, kind);
        else if (!nbFind(c, toolchain)) failure = fcFmt(c, "The compiler '%s' was not found.", nbEffectiveCompiler(toolchain));
    }
    known[0] = ext; known[1] = ".fireplugin"; known[2] = ".so"; known[3] = ".dll"; known[4] = ".dylib";
    for (i = 0; i < 5 && !failure; i++) {
        size_t n = strlen(full), m = strlen(known[i]);
        if (n >= m && fcAsciiIEq(full + n - m, known[i])) { full = fcStrN(c, full, n - m); break; }
    }
    library = fcFmt(c, "%s%s", full, ext);
    bytecode = fcFmt(c, "%s.fireplugin", full);
    if (!failure && config->host && *config->host) {
        size_t n = strlen(config->host);
        const char* file = n >= 9 && fcAsciiIEq(config->host + n - 9, ".firehost") ? fcFullPath(c, ".", config->host) : nbHostFileOf(c, config->host);
        host = fgHostInfoRead(c, file);
        if (!host) failure = fcFmt(c, "The program '%s' has no description for plugins (%s): build it with linkage \"shared\".", config->host, nbFileName(file));
        hostDir = nbDirName(c, file);
    }
    else if (!failure && dll) failure = "A plugin for Windows is built for a program (--host <program>): its DLL imports from the DLLs of the program's libraries.";
    if (failure) goto fail;
    fcLinkClassMeta = true;
    FC_TRY(c) { linked = nbLinkFor(c, b, config, target, kind, &registry, &o); } FC_CATCH(c) { fcLinkClassMeta = false; fcRethrow(c); } FC_END_TRY;
    fcLinkClassMeta = false;
    name = b->projectName ? b->projectName : nbFileName(full);
    watch = nbNowMs();
    memset(&store, 0, sizeof store);
    store.root = b->storeRoot;
    if (b->overlay) b->overlay(c, b, &store);
    if (b->dryRun) fgGeneratorIdentity = "cgen";
    FC_TRY(c) { plan = GeneratePlugin(c, linked, registry->names, target, name, host, nbStrippingOf(config), nbToolchainStamp(c, toolchain), dryCache ? dryCache : nbNativeCacheRoot(c), &store); }
    FC_CATCH(c) { failed = true; } FC_END_TRY;
    fgGeneratorIdentity = identity;
    if (dryCache) nbDeleteTree(c, dryCache);
    if (failed) fcRethrow(c);
    fcOut8(&log, plan->log);
    if (!b->dryRun) fcBufFmt(&log, "generated in %lld ms\n", nbNowMs() - watch);
    workDir = b->keepSources && !b->dryRun ? fcFmt(c, "%s.src", full) : fcFmt(c, "%sfire-plugin-%s", nbTempPath(c), nbGuid(c));
    /* (a dry run: the bytecode and the libraries of its natives in the folder of the build) */
    if (b->dryRun) bytecode = nbCombine(c, workDir, nbFileName(bytecode));
    cFile = nbWriteFiles(c, &store, instImports, workDir, plan->c, target, "fire_plugin.c", configDirectory, NULL, NULL);
    nbMkdirs(c, nbDirName(c, library));
#define NB_EXEC(exe_, args_) do { \
        if (b->dryRun) { NbCommand* k_ = (NbCommand*)fcAlloc(c, sizeof *k_); k_->exe = (exe_); k_->args = (args_); fcListAdd(c, commands, k_); ok = true; } \
        else { NbRunResult run_ = nbRun((exe_), (args_), workDir); nbAppendRun(&log, &run_); ok = run_.ok; } } while (0)
    /* its natives in C++: a file of their own, compiled with the C++ compiler into the same library (Linux: hidden - they are the plugin's own copy) */
    if (plan->natives) {
        const char* natFile = nbCombine(c, workDir, "fire_plugin_natives.cpp");
        NbCommand* natCompile;
        const char* natArgs;
        nbWriteText(c, natFile, plan->natives);
        natObj = nbCombine(c, workDir, "fire_plugin_natives.o");
        natCompile = nbCompilerCommand(c, toolchain, target, natFile, workDir, natObj, !dll, NULL, false, "c++", true, NULL, NULL, NULL, false);
        natArgs = dll ? natCompile->args : fcFmt(c, "%s -fvisibility=hidden", natCompile->args);
        fcBufFmt(&log, "%s %s\n", natCompile->exe, natArgs);
        NB_EXEC(natCompile->exe, natArgs);
        if (!ok) { failure = "The C++ compiler reported errors in the natives of the plugin (see the build log)."; goto cleanup; }
    }
    if (dll) {
        /* a DLL imports by file name: from the runtime's DLL and the DLLs of the modules of the program (next to it) */
        NbCommand* compile = nbCompilerCommand(c, toolchain, target, cFile, workDir, nbCombine(c, workDir, "fire_plugin.o"), false, NULL, false, "c", true, NULL, NULL, NULL, false);
        FcList* imports = fcListNew(c);
        FcList* libs = fcListNew(c);
        FcBuf args = {0};
        const char* found;
        const char* cc;
        fcBufFmt(&log, "%s %s\n", compile->exe, compile->args);
        NB_EXEC(compile->exe, compile->args);
        if (!ok) { failure = "The C compiler reported errors in the plugin (see the build log)."; goto cleanup; }
        if (host->runtime) fcListAdd(c, imports, (void*)nbCombine(c, hostDir, host->runtime));
        for (i = 0; i < plan->deps->count; i++) {
            const FgModuleDep* d = (const FgModuleDep*)plan->deps->items[i];
            int k;
            for (k = 0; k < host->modules->count; k++) {
                const FgPluginHostModule* m = (const FgPluginHostModule*)host->modules->items[k];
                if (streq0(m->name, d->module)) { if (*m->library) fcListAdd(c, imports, (void*)nbCombine(c, hostDir, m->library)); break; }
            }
        }
        for (i = 0; !b->dryRun && i < imports->count; i++)
            if (!nbFileExists((const char*)imports->items[i])) { failure = fcFmt(c, "The library '%s' of the program is not next to it (%s).", nbFileName((const char*)imports->items[i]), hostDir); goto cleanup; }
        /* (with natives in C++: linked by the C++ compiler, its runtime in the DLL - -static, since the posix MinGW would otherwise leave libwinpthread-1.dll to be found
         * next to the program; the DLLs of the program given by path stay imports) */
        found = natObj ? nbFind(c, toolchain) : nbFindC(c, toolchain);
        cc = found ? found : natObj ? nbEffectiveCompiler(toolchain) : nbEffectiveCCompiler(c, toolchain);
        if (toolchain->libs) for (i = 0; i < toolchain->libs->count; i++) fcListAdd(c, libs, toolchain->libs->items[i]);
        for (i = 0; i < target->linkLibCount; i++) fcListAdd(c, libs, (void*)target->linkLibs[i]);
        fcBufFmt(&args, "-shared -static-libgcc%s -o \"%s\" \"%s\"", natObj ? " -static-libstdc++ -static" : "", library, nbCombine(c, workDir, "fire_plugin.o"));
        if (natObj) fcBufFmt(&args, " \"%s\"", natObj);
        for (i = 0; i < imports->count; i++) fcBufFmt(&args, " \"%s\"", (const char*)imports->items[i]);
        fcBufFmt(&args, " -Wl,--export-all-symbols -s %s", fgJoin(c, libs, " "));
        fcBufFmt(&log, "%s %s\n", cc, args.p);
        NB_EXEC(cc, fcBufDone(c, &args));
    } else {
        /* (what it uses of the program and the libraries is found when it is loaded: the program exports it) */
        FcList* extra = NULL;
        NbCommand* link;
        const char* more;
        if (natObj) { extra = fcListNew(c); fcListAdd(c, extra, (void*)natObj); }
        link = nbCompilerCommand(c, toolchain, target, cFile, workDir, library, true, NULL, false, "c", false, extra, NULL, NULL, false);
#ifdef __APPLE__
        more = natObj ? " -undefined dynamic_lookup -lstdc++" : " -undefined dynamic_lookup";
#else
        more = natObj ? " -lstdc++" : "";
#endif
        fcBufFmt(&log, "%s %s%s -lm\n", link->exe, link->args, more);
        NB_EXEC(link->exe, fcFmt(c, "%s%s -lm", link->args, more));
    }
#undef NB_EXEC
    built = ok && (b->dryRun || nbFileExists(library));
    if (built) {
        /* the bytecode for the VM: compiled for the VM (`#if vm`), with the interfaces of the modules as the VM sees them */
        FcNatives* vmRegistry;
        FcStore* vmStore;
        FcLinked* volatile vmLinked = NULL;
        FcList* pairs;
        fcLinkClassMeta = true;
        FC_TRY(c) { vmLinked = nbPluginVmLink(c, b, &vmRegistry, &vmStore); } FC_CATCH(c) { fcLinkClassMeta = false; if (!b->keepSources) nbDeleteTree(c, workDir); fcRethrow(c); } FC_END_TRY;
        fcLinkClassMeta = false;
        pairs = nbPluginVmNatives(c, vmLinked, vmStore, nbDirName(c, bytecode), &log);
        nbWritePluginBytecode(c, bytecode, name, vmLinked, vmRegistry->names, nbModuleInterfaces(c, vmLinked, vmRegistry->names), pairs);
        fcBufFmt(&log, "plugin %s: %s, %s (the VM)\n", name, library, nbFileName(bytecode));
    }
    if (b->dryRun) {
        FcBuf* p = b->plan;
        FcList* files = nbSortedFiles(c, workDir, true);
        const char* text = fcBufDone(c, &log);
        text = fgReplace(c, fgReplace(c, text, library, "<plugin>"), workDir, "<build>");
        fcBufFmt(p, "toolchain %s\n", kind);
        if (*text) { fcBufStr(p, "log\n"); fcOut8(p, text); }
        for (i = 0; i < commands->count; i++) {
            NbCommand* cmd = (NbCommand*)commands->items[i];
            fcBufFmt(p, "command %s %s\n", cmd->exe, fgReplace(c, fgReplace(c, cmd->args, library, "<plugin>"), workDir, "<build>"));
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
        nbDeleteTree(c, workDir);
        r.ok = true; r.output = library; r.log = "";
        return r;
    }
    if (!b->keepSources) nbDeleteTree(c, workDir);
    r.ok = built;
    r.output = library;
    r.log = fcBufDone(c, &log);
    if (!built) r.error = "The C compiler reported errors in the plugin (see the build log).";
    return r;
cleanup:
    if (b->dryRun || !b->keepSources) nbDeleteTree(c, workDir);
fail:
    {
        const char* text = fcBufDone(c, &log);
        if (b->dryRun) {
            FcBuf* p = b->plan;
            fcBufFmt(p, "toolchain %s\nfailed %s\n", kind, failure);
            if (*text) { fcBufStr(p, "log\n"); fcOut8(p, text); }
        }
        r.ok = false; r.output = output; r.log = fcFmt(c, "%s%s", text, failure); r.error = failure; r.config = true;
    }
    return r;
}
