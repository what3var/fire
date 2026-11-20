/* native/forge/project/command.c - the commands of projects and solutions (Phase D 3, commit de45e7e4): `forge-c build <project|solution>` (CommandLine.cs:
 * RunProject, BuildPlanned, CopyContent, ChooseEngine, EffectiveModeName; CommandLine.Report.cs with the text reporter: ReportBuildAll, BuildProgram,
 * NativeOutcome, ReportCopyContent for `--all`), ProjectBuilder.Check and PackLibrary (ProjectBuilder.cs), `forge-c publish` (CommandLine.Publish and
 * ProjectPublisher.cs) and the measures: `forge-c projectplan <file>` (what FrontendDump.DumpProjectPlan of the C# forge prints: the workspace, the build
 * order, every project's files, folders, build actions and its BuildPlan) and `forge-c fpk <file.fpk>` (FrontendDump.DumpPackage: the manifest as
 * Fpk.ReadManifest reads it and every entry). The engine `vm` packs the program with the VM in C (build/cvm.c), `native` builds it with the builder. */

/* ---- the collector of the command line: the warnings printed when they come, each once ---- */
typedef struct PjOut { FcCtx* c; FcList* seen; FcBuf* err; } PjOut;
static bool pjSameDiag(const FcDiagnostic* o, const FcDiagnostic* d) {
    return o->line == d->line && o->severity == d->severity && o->column == d->column && fcSameText(o->message, d->message) && fcSameText(o->code, d->code) && fcSameText(o->file, d->file);
}
/* the warnings of a step (its context): those not reported yet into `err` - or to pjOnWarning (the collector of a build that reports, Phase D 4) */
static void (*pjOnWarning)(const FcDiagnostic* d);
static void pjForwardWarnings(PjOut* out, FcCtx* step) {
    int i, k;
    for (i = 0; i < step->diags->count; i++) {
        const FcDiagnostic* d = (const FcDiagnostic*)step->diags->items[i];
        bool seen = false;
        if (d->severity != FC_SEV_WARNING) continue;
        for (k = 0; k < out->seen->count; k++) if (pjSameDiag((const FcDiagnostic*)out->seen->items[k], d)) { seen = true; break; }
        if (seen) continue;
        fcListAdd(out->c, out->seen, (void*)d);
        if (pjOnWarning) pjOnWarning(d);   /* (a build that reports: the report has it, tools/report.c) */
        else fcDiagLogLine(out->err, d);
    }
}

/* ---- the link of a plan in a step of its own ---- */
static void pjStepInit(FcCtx* step) { fcCtxInit(step); step->collecting = true; }
/* a NativeBuilder.Build of a plan: nbBuild links the plan (NbBuild.link) */
static FcLinked* pjNbLink(FcCtx* c, const NbBuild* b, const FgOptions* o, FcNatives** registry, const char* kind) {
    PjLinkIn in;
    FcLinked* linked;
    memset(&in, 0, sizeof in);
    in.plan = (PjPlan*)b->project;
    in.prelude = b->prelude;
    in.storeRoot = b->storeRoot;
    in.o = o;
    in.toolchainKind = kind;
    in.modeOverride = -1;
    linked = pjLink(c, &in, registry);
    return linked;
}
/* (ProjectNativeOverlay.Register for the generator: the natives of the plan's projects in its store, before the installed packages) */
static void pjNbOverlay(FcCtx* c, const NbBuild* b, FcStore* store) { pjOverlayRegister(c, store, (const PjPlan*)b->project); }
/* ProjectBuilder.Check: the plan compiled (VM: no target; a native build: for the target) */
static void pjCheck(FcCtx* step, PjPlan* plan, const char* prelude, const char* storeRoot, const FgTarget* target) {
    PjLinkIn in;
    FgOptions o;
    FcLinked* linked;
    memset(&in, 0, sizeof in);
    in.plan = plan; in.prelude = prelude; in.storeRoot = storeRoot; in.modeOverride = -1;
    if (target) {
        memset(&o, 0, sizeof o);
        o.target = target; o.mode = -1; o.stripping = -1; o.optimize = -1;
        in.o = &o;
        in.toolchainKind = NULL;
    }
    linked = pjLink(step, &in, NULL);
    (void)linked;
}
/* EffectiveModeName: the mode the program gets (-m, the project, the tags of its files) - a compile of the sources */
static const char* pjEffectiveModeName(PjOut* out, PjPlan* plan, const char* prelude, const char* storeRoot, const FgTarget* target, int modeOverride, int floatWidth) {
    FcCtx step;
    PjLinkIn in;
    FgOptions o;
    const char* volatile name = NULL;
    memset(&in, 0, sizeof in);
    in.plan = plan; in.prelude = prelude; in.storeRoot = storeRoot; in.modeOverride = modeOverride; in.floatWidthOverride = floatWidth;
    if (target) {
        memset(&o, 0, sizeof o);
        o.target = target; o.mode = modeOverride; o.stripping = -1; o.optimize = -1; o.floatWidth = floatWidth;
        in.o = &o;
        in.toolchainKind = NULL;
    }
    pjStepInit(&step);
    FC_TRY(&step) {
        FcLinked* linked = pjLink(&step, &in, NULL);
        name = linked->mode == 0 ? "debug" : linked->mode == 2 ? "performance" : "release";
    } FC_CATCH(&step) {
        pjForwardWarnings(out, &step);
        /* (the error goes to the caller's context) */
        out->c->err = step.err;
        fcRethrow(out->c);
    } FC_END_TRY;
    pjForwardWarnings(out, &step);
    return name;
}

/* the log of the monitor of a build (BuildMonitor with a log: publish, --all) - NULL: none */
static FcBuf* pjMonitorOut;

/* ---- ProjectBuilder.PackLibrary ---- */
static bool pjInList(const FcList* l, const void* x) { int i; for (i = 0; i < l->count; i++) if (l->items[i] == x) return true; return false; }
static const char* pjPackLibrary(FcCtx* c, PjOut* out, PjPlan* plan, const char* outputDirectory, const char* prelude, const char* storeRoot, bool log) {
    PjLoaded* project = plan->project;
    const char* volatile import;
    const char* version;
    const char* outDir;
    const char* workDir;
    const char* preludePath;
    FcList* imports = fcListNew(c);
    FcList* resourceKeys = fcListNew(c);
    FcList* resourceValues = fcListNew(c);
    PkManifest* manifest;
    PkImport* packageImport;
    FcList* lib = fcListNew(c);
    FcStore store;
    const char* templates;
    FcList* documents = fcListNew(c);
    FcList* files = fcListNew(c);
    const char* forge;
    PkForgeResult result;
    const char* volatile packagePath = NULL;
    int i, k;
    if (project->project->type != PJ_LIBRARY) pjThrow(c, fcFmt(c, "'%s' is a program: only a library is packed.", project->project->name));
    {
        /* Check: it has to compile (and have no entry point) before it is shipped */
        FcCtx step;
        pjStepInit(&step);
        FC_TRY(&step) {
            pjCheck(&step, plan, prelude, storeRoot, NULL);
        } FC_CATCH(&step) {
            pjForwardWarnings(out, &step);
            c->err = step.err;
            fcRethrow(c);
        } FC_END_TRY;
        pjForwardWarnings(out, &step);
    }
    { char stepText[512]; snprintf(stepText, sizeof stepText, "Packing %s as a package", project->project->name); fcMonitorStep(6, stepText, 0, 0); }   /* (BuildMonitor.Step, Phase D 4) */
    import = pjImportName(c, project->project);
    version = pjPackageVersion(c, plan->settings->version);
    outDir = pjFull(c, outputDirectory ? outputDirectory : plan->settings->output ? plan->settings->output : nbCombine(c, project->project->directory, "bin"), ".");
    workDir = nbCombine(c, outDir, fcFmt(c, "obj-%s", project->project->name));
    nbMkdirs(c, workDir);
    FC_TRY(c) {
        preludePath = nbCombine(c, workDir, fcFmt(c, "%s.fire", import));
        nbWriteText(c, preludePath, pjFlatten(c, plan->sources, imports, resourceKeys, resourceValues, project->project->directory));
        manifest = pkManifestNew(c);
        manifest->name = project->project->name;
        manifest->version = version;
        manifest->author = plan->settings->author ? plan->settings->author : "";
        manifest->description = plan->settings->description ? plan->settings->description : "";
        manifest->license = plan->settings->license ? plan->settings->license : "";
        for (i = 0; i < plan->libraries->count; i++) {
            PjLibrary* l = (PjLibrary*)plan->libraries->items[i];
            for (k = 0; k < project->project->references->count; k++) {
                const PjReference* r = (const PjReference*)project->project->references->items[k];
                if (pjRefIsProject(r) && streq0(pjFull(c, pjReplaceChar(c, r->project, '\\', '/'), project->project->directory), l->project->project->filePath)) { fcListAdd(c, lib, l); break; }
            }
        }
        packageImport = pkImportNew(c, import);
        packageImport->prelude = preludePath;
        packageImport->native = plan->native ? plan->native->native : NULL;
        packageImport->standard = project->project->standard;
        if (resourceKeys->count > 0) { packageImport->resourceKeys = resourceKeys; packageImport->resourceValues = resourceValues; }
        if (project->project->drivers && project->project->drivers->count > 0) packageImport->drivers = fcListCopy(c, project->project->drivers);
        if (project->project->serializable == 0) packageImport->serializable = 0;
        for (i = 0; i < lib->count; i++) { fcListAdd(c, packageImport->requires, (void*)((PjLibrary*)lib->items[i])->importName); fcListAdd(c, manifest->dependencies, (void*)((PjLibrary*)lib->items[i])->project->project->name); }
        memset(&store, 0, sizeof store);
        store.root = storeRoot;
        pjOverlayRegister(c, &store, plan);   /* (the store as the check left it: the overlay of the plan) */
        for (i = 0; i < imports->count; i++) {
            const char* name = (const char*)imports->items[i];
            const char* lower = pjLower(c, name);
            const char* provider = NULL;
            bool bridge = false;
            size_t b;
            if (pjIEq(name, import) || pjListHasIgnoreCase(packageImport->requires, name)) continue;
            fcListAdd(c, packageImport->requires, (void*)name);
            for (b = 0; b < sizeof fcStandardBridges / sizeof fcStandardBridges[0]; b++) if (streq0(lower, fcStandardBridges[b])) bridge = true;
            if (bridge) provider = fcFmt(c, "fire-%s", lower);
            else {
                bool isLib = false;
                for (k = 0; k < lib->count; k++) if (pjIEq(((PjLibrary*)lib->items[k])->importName, name)) isLib = true;
                if (!isLib) { FcPkgImport* pi = fcStoreFindImport(c, &store, name); provider = pi ? pi->packageName : NULL; }
            }
            if (provider && !fcListHasStr(manifest->dependencies, provider)) fcListAdd(c, manifest->dependencies, (void*)provider);
        }
        for (i = 0; i < plan->packages->count; i++) {
            const char* name = ((const PjReference*)plan->packages->items[i])->package;
            if (!fcListHasStr(manifest->dependencies, name)) fcListAdd(c, manifest->dependencies, (void*)name);
        }
        fcListAdd(c, manifest->imports, packageImport);
        templates = nbCombine(c, project->project->directory, "templates");
        if (nbDirExists(templates)) manifest->templates = templates;
        pjEnumerate(c, project->project->directory, files, NULL);
        for (i = 0; i < files->count; i++) if (pkIsDocumentName(c, (const char*)files->items[i])) fcListAdd(c, documents, (void*)nbCombine(c, project->project->directory, (const char*)files->items[i]));
        if (documents->count > 1) qsort(documents->items, (size_t)documents->count, sizeof(void*), pkCmpOrdinalQ);
        if (documents->count > 0) manifest->documents = documents;
        forge = nbCombine(c, workDir, "package.json");
        nbWriteText(c, forge, pkManifestToJson(c, manifest));
        result = pkForge(c, forge, outDir);
        packagePath = result.packagePath;
    } FC_CATCH(c) {
        nbDeleteTree(c, workDir);
        fcRethrow(c);
    } FC_END_TRY;
    nbDeleteTree(c, workDir);
    if (log && pjMonitorOut) fcBufFmt(pjMonitorOut, "Packed %s\n", packagePath);   /* (BuildMonitor.LogLine: a publish's log) */
    return packagePath;
}

/* ---- ChooseEngine ---- */
static const char* pjChooseEngine(FcCtx* c, const char* given, const char* configured, const char* targetName, NbConfig* config, FcBuf* err) {
    const FgTarget* volatile target = NULL;
    bool foreign;
    FC_TRY(c) {
        if (targetName || config->target) target = nbResolveTarget(c, config, targetName);
    } FC_CATCH(c) {
        if (!(c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "native-config: "))) fcRethrow(c);
    } FC_END_TRY;
    foreign = (target && !streq0(target->name, fgHostTarget()->name)) || (!target && targetName);
    if (given && streq0(given, "vm") && foreign) {
        if (err) fcBufFmt(err, "--engine vm cannot build for the target '%s': the VM runs only on this machine (%s). Leave out --engine or use --engine native.\n", target ? target->name : targetName, fgHostTarget()->name);
        return NULL;
    }
    if (foreign) return "native";
    return given ? given : configured ? pjLower(c, configured) : "vm";
}
/* Strippings.Parse */
static int pjStrippingParse(FcCtx* c, const char* s) {
    const char* l;
    int i;
    if (!s) return -1;
    if (strlen(s) == 1 && s[0] >= '0' && s[0] <= '3') return s[0] - '0';
    l = pjLower(c, pjTrim(c, s));
    for (i = 0; i < 4; i++) if (streq0(l, pjStrippings[i])) return i;
    return -1;
}
/* the project's settings in the configuration of a build: stripping and direction (StrippingOf, OptimizeOf with the plan) */
static void pjApplyBuildSettings(FcCtx* c, NbConfig* config, const PjPlan* plan) {
    int s = pjStrippingParse(c, plan->settings->stripping);
    if (!config->strippingFromCommandLine && s >= 0) config->stripping = s;
    if (!config->optimizeFromCommandLine && plan->settings->optimize) {
        const char* o = pjLower(c, plan->settings->optimize);
        if (streq0(o, "speed")) config->optimize = 0;
        else if (streq0(o, "size")) config->optimize = 1;
    }
}
static bool pjIsProjectFile(const char* path) {
    const char* dot = strrchr(path, '.');
    return dot && (pjIEq(dot, ".fireproj") || pjIEq(dot, ".firesln"));
}

/* ---- `forge-c build <project|solution>` ---- */
typedef struct PjBuildArgs {
    const char* file; const char* output; const char* targetName; const char* toolchainName; const char* configFile; const char* linkage; const char* engine; const char* projectName;
    int mode, floatWidth, stripping, optimize, quick, split;
    bool keepAll, keep, stats, all, check;
    FcList* defines;
} PjBuildArgs;
static NbConfig* pjConfigFor(FcCtx* c, const PjBuildArgs* a, const char* projectFile) {
    NbConfig* config;
    if (a->configFile) config = nbConfigLoad(c, a->configFile);
    else config = nbConfigFindFor(c, projectFile);
    /* KeepAll */
    if (a->stats) config->stats = true;
    if (a->keepAll) { config->stripping = 0; config->strippingFromCommandLine = true; }
    else if (a->stripping >= 0) { config->stripping = a->stripping; config->strippingFromCommandLine = true; }
    if (a->optimize >= 0) { config->optimize = a->optimize; config->optimizeFromCommandLine = true; }
    if (a->linkage) config->linkage = a->linkage;
    if (a->split >= 0) config->split = a->split;
    if (a->quick >= 0) config->quick = a->quick;
    return config;
}
/* the copies of the build action Copy next to the program (CopyContent / ReportCopyContent) */
static void pjCopyContent(FcCtx* c, const PjPlan* plan, const char* folder, FcBuf* out, FcBuf* err, bool report) {
    FcList* items = pjContentItemsOf(c, plan);
    PjCopyResult r;
    int i;
    if (items->count == 0) return;
    r = pjContentCopyTo(c, items, folder);
    if (report) {
        for (i = 0; i < r.problems->count; i++) fcBufFmt(out, "%s: warning: could not copy %s\n", plan->project->project->name, (const char*)r.problems->items[i]);
        fcBufFmt(out, "%s: %d file%s (build action Copy) copied next to the program", plan->project->project->name, r.copied->count, r.copied->count == 1 ? "" : "s");
        if (r.upToDate > 0) fcBufFmt(out, ", %d up to date", r.upToDate);
        fcBufStr(out, ".\n");
        return;
    }
    for (i = 0; i < r.problems->count; i++) fcBufFmt(err, "warning: %s\n", (const char*)r.problems->items[i]);
    if (r.copied->count > 0) fcBufFmt(out, "%d file%s copied next to the program (%d up to date)\n", r.copied->count, r.copied->count == 1 ? "" : "s", r.upToDate);
}
static const char* nsDescribe(FcCtx* c);   /* (project/standard.c: CompileErrors.Describe) */
/* the line of an error of a compile (DescribeErrors) */
static void pjDescribeErrors(FcCtx* c, FcBuf* err) {
    FcList* diags = fcDiagnosticsOf(c, &c->err, NULL);
    int i;
    for (i = 0; i < diags->count; i++) fcDiagLogLine(err, (const FcDiagnostic*)diags->items[i]);
}
static bool pjIsCompileErrorKind(FcErrKind k) { return k == FC_ERR_LEX || k == FC_ERR_PRE || k == FC_ERR_PARSE || k == FC_ERR_RESOLVE || k == FC_ERR_TYPECHECK || k == FC_ERR_LINKER || k == FC_ERR_COMPILE || k == FC_ERR_NOTSUPPORTED; }

/* the monitor of a build (BuildMonitor with a log): the lines of the tools as they run */
static void pjMonitorLine(const char* text, size_t n) {
    size_t e = n;
    while (e > 0 && (text[e - 1] == '\r' || text[e - 1] == '\n')) e--;
    if (pjMonitorOut) { fcBufPut(pjMonitorOut, text, e); fcBufChar(pjMonitorOut, '\n'); }
}

/* BuildPlanned (and BuildProgram of the report) for the engine vm: the program linked with the plan and packed with the VM in C (build/cvm.c) into output,
 * else <project>/bin/vm-<mode>/<name>; the Copy files next to it, then `<full path> (<n> bytes)`. A compile error goes to the caller (DescribeErrors). */
static int pjBuildVmProgram(FcCtx* c, PjOut* po, const PjBuildArgs* a, PjPlan* plan, const char* prelude, const char* storeRoot, const char* const output, FcBuf* out, FcBuf* err, bool report) {
    const char* volatile program = output;
    const char* full;
    FcCtx step;
    PjLinkIn in;
    nvRefuseDotnet(c);
    nvStoreRoot = storeRoot;
    if (!program) {
        const char* mode = a->mode >= 0 ? (a->mode == 0 ? "debug" : a->mode == 2 ? "performance" : "release")
            : pjEffectiveModeName(po, plan, prelude, storeRoot, NULL, a->mode, a->floatWidth);
        program = pjProgramPath(c, plan->project->project->directory, plan->project->project->name, "vm", mode, "");
    }
    full = pjFull(c, program, ".");
    nbMkdirs(c, fcDirectoryName(c, full));
    memset(&in, 0, sizeof in);
    in.plan = plan; in.prelude = prelude; in.storeRoot = storeRoot; in.modeOverride = a->mode; in.floatWidthOverride = a->floatWidth;
    pjStepInit(&step);
    FC_TRY(&step) {
        FcStore* store = NULL;
        FcNatives* registry = NULL;
        FcLinked* linked;
        FcList* libraries;
        const char* error = NULL;
        in.storeOut = &store;
        linked = pjLink(&step, &in, &registry);
        libraries = nvEnsureLibraries(&step, linked, store, registry, err);
        if (!nvPack(&step, linked, libraries, program, err, &error)) fcThrow(&step, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(&step, "vm: %s", error));
    } FC_CATCH(&step) {
        pjForwardWarnings(po, &step);
        c->err = step.err;
        fcRethrow(c);
    } FC_END_TRY;
    pjForwardWarnings(po, &step);
    pjCopyContent(c, plan, fcDirectoryName(c, full), out, err, report);
    fcBufFmt(out, "%s (%lld bytes)\n", full, nvFileLength(full));
    return 0;
}
/* BuildPlanned for the engine native (one project; `report`: the text reporter of --all - BuildProgram/NativeOutcome) - returns the exit code */
static int pjBuildProgram(FcCtx* c, PjOut* po, const PjBuildArgs* a, PjPlan* plan, const char* prelude, const char* storeRoot, FcBuf* out, FcBuf* err, bool report, const char** error) {
    NbConfig* config = pjConfigFor(c, a, plan->project->project->filePath);
    const char* engine = pjChooseEngine(c, a->engine, plan->settings->engine ? plan->settings->engine : config->engine, a->targetName ? a->targetName : plan->settings->target, config, err);
    const FgTarget* target;
    NbToolchain* toolchain;
    bool files;
    const char* volatile output = report ? plan->settings->output : a->output ? a->output : plan->settings->output;
    const char* to;
    NbBuild b;
    NbResult r;
    FcCtx step;
    volatile int result = 0;
    int i;
    if (!engine) return 2;
    if (!streq0(engine, "native")) return pjBuildVmProgram(c, po, a, plan, prelude, storeRoot, output, out, err, report);
    target = nbResolveTarget(c, config, a->targetName ? a->targetName : plan->settings->target);
    toolchain = nbResolveToolchain(c, config, target, a->toolchainName ? a->toolchainName : plan->settings->toolchain, NB_PURPOSE_BUILD);
    files = streq0(nbKind(toolchain), "files");
    if (!a->linkage && plan->settings->linkage) config->linkage = plan->settings->linkage;
    if (report && files) output = NULL;
    pjApplyBuildSettings(c, config, plan);
    if (output) to = output;
    else {
        const char* mode = a->mode >= 0 ? (a->mode == 0 ? "debug" : a->mode == 2 ? "performance" : "release")
            : pjEffectiveModeName(po, plan, prelude, storeRoot, target, a->mode, a->floatWidth);
        to = files ? pjOutputFolder(c, plan->project->project->directory, target->name, mode)
                   : pjProgramPath(c, plan->project->project->directory, plan->project->project->name, target->name, mode, target->programExtension);
    }
    nbMkdirs(c, files ? pjFull(c, to, ".") : fcDirectoryName(c, pjFull(c, to, ".")));
    if (config->stats) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: --stats");
    memset(&b, 0, sizeof b);
    b.prelude = prelude; b.storeRoot = storeRoot; b.mode = a->mode; b.floatWidth = a->floatWidth; b.keepSources = a->keep;
    b.link = pjNbLink;
    b.overlay = pjNbOverlay;
    b.project = plan;
    pjStepInit(&step);
    if (report) step.diags = fcListNew(&step);
    FC_TRY(&step) {
        r = nbBuild(&step, &b, config, target, toolchain, to);
    } FC_CATCH(&step) {
        if (!report) {
            /* (NativeBuilder.Build: a compile error is an exception - DescribeErrors) */
            pjForwardWarnings(po, &step);
            c->err = step.err;
            fcRethrow(c);
        }
        /* BuildSafe: the errors and the warnings are the result's */
        r.ok = false; r.output = to; r.error = NULL;
        if (step.err.kind == FC_ERR_NATIVE) r.log = fcFmt(&step, "Not supported by the native backend yet: %s", step.err.message);
        else if (pjIsCompileErrorKind(step.err.kind)) r.log = nsDescribe(&step);   /* (BuildSafe: CompileErrors.Describe) */
        else { c->err = step.err; fcRethrow(c); }
        {
            FcList* diags = fcDiagnosticsOf(&step, &step.err, NULL);
            FcList* all = fcListNew(&step);
            for (i = 0; i < diags->count; i++) fcListAdd(&step, all, diags->items[i]);
            for (i = 0; i < step.diags->count; i++) fcListAdd(&step, all, step.diags->items[i]);
            step.diags = all;
        }
        result = -1;
    } FC_END_TRY;
    if (!report) {
        pjForwardWarnings(po, &step);
        if (*r.log) { fcOut8(out, r.log); if (!fcEndsWith(r.log, "\n")) fcBufChar(out, '\n'); }
        if (!r.ok) {
            if (r.error) fcBufFmt(err, "error %s: %s\n", strstr(r.error, "was not found") ? "E7003" : "E7002", r.error);
            fcBufFmt(err, "The native build for %s failed.\n", target->name);
            return 1;
        }
        pjCopyContent(c, plan, files ? r.output : fcDirectoryName(c, pjFull(c, r.output, ".")), out, err, false);
        fcBufFmt(out, "%s (native, target %s, toolchain %s)\n", r.output, target->name, nbKind(toolchain));
        return 0;
    }
    /* NativeOutcome: the log, the diagnostics (errors and warnings of the build), the copies, the program */
    if (r.log && *r.log) { fcOut8(out, r.log); if (!fcEndsWith(r.log, "\n")) fcBufChar(out, '\n'); }
    if (r.error) {
        FcDiagnostic* d = (FcDiagnostic*)fcAlloc(&step, sizeof *d);
        d->line = 0; d->message = r.error; d->severity = FC_SEV_ERROR; d->code = strstr(r.error, "was not found") ? "E7003" : "E7002";
        fcListInsert(&step, step.diags, 0, d);
    }
    for (i = 0; i < step.diags->count; i++) fcDiagLogLine(err, (const FcDiagnostic*)step.diags->items[i]);
    if (!r.ok) {
        const FcDiagnostic* first = NULL;
        FcBuf line = {0};
        for (i = 0; i < step.diags->count && !first; i++) if (((const FcDiagnostic*)step.diags->items[i])->severity == FC_SEV_ERROR) first = (const FcDiagnostic*)step.diags->items[i];
        fcBufStr(&line, "");
        if (first) { fcDiagLogLine(&line, first); line.len--; line.p[line.len] = 0; }
        else fcBufFmt(&line, "The native build for %s failed.", target->name);
        *error = fcBufDone(c, &line);
        fcBufFmt(out, "%s: The native build for %s failed.\n", plan->project->project->name, target->name);
        return 1;
    }
    (void)result;
    pjCopyContent(c, plan, files ? r.output : fcDirectoryName(c, pjFull(c, r.output, ".")), out, err, true);
    fcBufFmt(out, "%s (native, target %s, toolchain %s)\n", r.output, target->name, nbKind(toolchain));
    fcBufFmt(out, "%s: built %s (%s).\n", plan->project->project->name, r.output, target->name);
    return 0;
}

/* `--all` (ReportBuildAll with the text reporter) */
static int pjBuildAll(FcCtx* c, PjOut* po, const PjBuildArgs* a, PjWorkspace* w, const char* prelude, const char* storeRoot, FcBuf* out, FcBuf* err) {
    FcList* order = pjBuildOrder(w);
    FcList* failed = fcListNew(c);
    int succeeded = 0, skipped = 0, i, k;
    const char* firstError = NULL;
    if (order->count == 0) {
        fcBufStr(out, "error: The solution has no project to build.\n");
        fcBufStr(err, "The solution has no project to build.\n");
        return 2;
    }
    for (i = 0; i < order->count; i++) {
        PjLoaded* project = (PjLoaded*)order->items[i];
        FcList* refs = pjReferencedProjects(w, project);
        PjLoaded* broken = NULL;
        PjPlan* volatile plan = NULL;
        const char* volatile failure = NULL;
        fcBufFmt(out, "------ %d/%d %s (%s) ------\n", i + 1, order->count, project->project->name, project->project->type == PJ_LIBRARY ? "library" : "program");
        for (k = 0; k < refs->count && !broken; k++) if (pjInList(failed, refs->items[k])) broken = (PjLoaded*)refs->items[k];
        if (broken) {
            fcListAdd(c, failed, project);
            skipped++;
            fcBufFmt(out, "%s: skipped - the library %s it references did not build.\n", project->project->name, broken->project->name);
            continue;
        }
        FC_TRY(c) {
            plan = pjPlanOrThrow(w, project);
        } FC_CATCH(c) {
            if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
            failure = c->err.message;
        } FC_END_TRY;
        if (!failure) {
            FC_TRY(c) {
                if (plan->project->project->type == PJ_LIBRARY) {
                    NbConfig* config = pjConfigFor(c, a, plan->project->project->filePath);
                    const char* engine = pjChooseEngine(c, a->engine, plan->settings->engine ? plan->settings->engine : config->engine, a->targetName ? a->targetName : plan->settings->target, config, NULL);
                    const FgTarget* target = engine && streq0(engine, "native") ? nbResolveTarget(c, config, a->targetName ? a->targetName : plan->settings->target) : NULL;
                    FcCtx step;
                    pjStepInit(&step);
                    FC_TRY(&step) {
                        pjCheck(&step, plan, prelude, storeRoot, target);
                    } FC_CATCH(&step) {
                        FcList* errors;
                        pjForwardWarnings(po, &step);
                        if (!pjIsCompileErrorKind(step.err.kind) && step.err.kind != FC_ERR_PROJECT && step.err.kind != FC_ERR_PACKAGE) { c->err = step.err; fcRethrow(c); }
                        errors = fcDiagnosticsOf(&step, &step.err, NULL);
                        for (k = 0; k < errors->count; k++) fcDiagLogLine(err, (const FcDiagnostic*)errors->items[k]);
                        failure = errors->count > 0 ? ((const FcDiagnostic*)errors->items[0])->message : "it does not compile";
                    } FC_END_TRY;
                    if (!failure) {
                        pjForwardWarnings(po, &step);
                        succeeded++;
                        fcBufFmt(out, "%s: compiled%s (a library: its code goes into the programs that import it).\n", project->project->name, target ? fcFmt(c, " for %s", target->name) : "");
                    }
                } else {
                    const char* error = NULL;
                    int exit = pjBuildProgram(c, po, a, plan, prelude, storeRoot, out, err, true, &error);
                    if (exit == 0) succeeded++;
                    else failure = error ? error : "the build failed";
                }
            } FC_CATCH(c) {
                if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "native-config: ")) failure = c->err.message + 15;
                /* (a part forge-c does not port yet: that project fails and says so, the others go on) */
                else if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "unported: ")) failure = fcFmt(c, "forge-c: %s", c->err.message);
                else if (c->err.kind == FC_ERR_PACKAGE || c->err.kind == FC_ERR_PROJECT) failure = c->err.message;
                else if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "vm: ")) failure = c->err.message + 4;
                else if (pjIsCompileErrorKind(c->err.kind) || c->err.kind == FC_ERR_NATIVE) {
                    FcList* errors = fcDiagnosticsOf(c, &c->err, NULL);
                    for (k = 0; k < errors->count; k++) fcDiagLogLine(err, (const FcDiagnostic*)errors->items[k]);
                    failure = errors->count > 0 ? ((const FcDiagnostic*)errors->items[0])->message : c->err.message;
                } else fcRethrow(c);
            } FC_END_TRY;
        }
        if (failure) {
            fcListAdd(c, failed, project);
            if (!firstError) firstError = fcFmt(c, "%s: %s", project->project->name, failure);
            fcBufFmt(out, "%s: error: %s\n", project->project->name, pjTrimEndWs(c, failure));
        }
    }
    fcBufFmt(out, "Projects: %d succeeded, %d failed, %d skipped.\n", succeeded, failed->count - skipped, skipped);
    if (failed->count > 0 && firstError) fcBufFmt(err, "%s\n", firstError);
    return failed->count == 0 ? 0 : 1;
}

static int pjBuildMain(PjBuildArgs* a, const char* prelude, const char* storeRoot) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    FcBuf out = {0}, err = {0};
    PjOut po;
    volatile int result = 0;
    fcCtxInit(c);
    fcBufStr(&out, "");
    fcBufStr(&err, "");
    po.c = c; po.seen = fcListNew(c); po.err = &err;
    pjMonitorOut = &out;
    FC_TRY(c) {
        PjWorkspace* volatile w = NULL;
        PjLoaded* volatile project = NULL;
        PjPlan* volatile plan = NULL;
        bool usage = false;
        if (!nbFileExists(a->file)) { fcBufFmt(&err, "File not found: %s\n", a->file); result = 2; usage = true; }
        if (!usage && a->all) {
            /* (the report: RunReported) */
            FC_TRY(c) {
                w = pjWorkspaceOpen(c, a->file);
            } FC_CATCH(c) {
                if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
                fcBufFmt(&out, "error: %s\n", pjTrimEndWs(c, c->err.message));
                fcBufFmt(&err, "%s\n", c->err.message);
                result = 1; usage = true;
            } FC_END_TRY;
            if (!usage) { nbMonitorLine = pjMonitorLine; result = pjBuildAll(c, &po, a, w, prelude, storeRoot, &out, &err); nbMonitorLine = NULL; }
            usage = true;
        }
        if (!usage) {
            FC_TRY(c) {
                w = pjWorkspaceOpen(c, a->file);
                project = pjSelectProject(w, a->projectName);
                plan = pjPlanOrThrow(w, project);
            } FC_CATCH(c) {
                if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
                fcBufFmt(&err, "%s\n", c->err.message);
                result = 2; usage = true;
            } FC_END_TRY;
        }
        if (!usage) {
            /* BuildPlanned */
            FC_TRY(c) {
                if (plan->project->project->type == PJ_LIBRARY && a->check) {
                    NbConfig* config = pjConfigFor(c, a, plan->project->project->filePath);
                    const FgTarget* target = a->targetName || plan->settings->target ? nbResolveTarget(c, config, a->targetName ? a->targetName : plan->settings->target) : NULL;
                    FcCtx step;
                    pjStepInit(&step);
                    FC_TRY(&step) {
                        pjCheck(&step, plan, prelude, storeRoot, target);
                    } FC_CATCH(&step) {
                        pjForwardWarnings(&po, &step);
                        c->err = step.err;
                        fcRethrow(c);
                    } FC_END_TRY;
                    pjForwardWarnings(&po, &step);
                    fcBufFmt(&out, "%s: compiled%s (a library: its code goes into the programs that import it).\n", plan->project->project->name, target ? fcFmt(c, " for %s", target->name) : "");
                } else if (plan->project->project->type == PJ_LIBRARY) {
                    const char* package = pjPackLibrary(c, &po, plan, a->output, prelude, storeRoot, false);
                    fcBufFmt(&out, "%s (package, import \"%s\")\n", package, pjImportName(c, plan->project->project));
                } else {
                    const char* error = NULL;
                    result = pjBuildProgram(c, &po, a, plan, prelude, storeRoot, &out, &err, false, &error);
                }
            } FC_CATCH(c) {
                if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "native-config: ")) { fcBufFmt(&err, "%s\n", c->err.message + 15); result = 2; }
                else if (c->err.kind == FC_ERR_NATIVE) { fcBufStr(&err, "error E7001: Not supported by the native backend yet: "); fcOut8(&err, c->err.message); fcBufChar(&err, '\n'); result = 1; }
                else if (c->err.kind == FC_ERR_PROJECT || c->err.kind == FC_ERR_PACKAGE) { fcBufFmt(&err, "%s\n", c->err.message); result = 1; }
                else if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "vm: ")) { fcBufFmt(&err, "%s\n", c->err.message + 4); result = 1; }
                else if (pjIsCompileErrorKind(c->err.kind)) { pjDescribeErrors(c, &err); result = 1; }
                else fcRethrow(c);
            } FC_END_TRY;
        }
    } FC_CATCH(c) {
        if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "unported: ")) { fcBufFmt(&err, "forge-c: %s\n", c->err.message); result = 2; }
        else { fcBufFmt(&err, "forge-c: internal: %s\n", c->err.message ? c->err.message : "?"); result = 2; }
    } FC_END_TRY;
    pjMonitorOut = NULL;
    fwrite(err.p, 1, err.len, stderr);
    fwrite(out.p, 1, out.len, stdout);
    fcBufFree(&out);
    fcBufFree(&err);
    return result;
}
