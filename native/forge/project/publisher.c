/* native/forge/project/publisher.c - Publish (Phase D 3, commit de45e7e4): src/fire.Compiler/ProjectPublisher.cs (Publish, PublishOne, Companions, the stamps
 * of what was built - StampKey, IsUpToDate, WriteStamp -, CleanProblem, EmptyFolder; PublishResult.Summary) and CommandLine.Publish. The engines `native`
 * and `vm` (a program that carries the VM in C, build/cvm.c) and the packages of libraries. The key of a stamp hashes
 * forge-c itself where C# hashes its two assemblies: a program published by one forge is built again by the other. */

typedef struct PjPublishOptions { const char* toolchain; bool includeDisabled; int quick; bool rebuild; } PjPublishOptions;
typedef struct PjPublishResult {
    PjLoaded* project; bool ok, skipped, upToDate; const char* program; const char* engine; const char* mode; const char* targetName;
    FcList* folders; int filesCopied; const char* error; FcList* diagnostics;
} PjPublishResult;

static const char* pjPublishSummary;   /* the summary of the last pjPublish */
/* PublishOptions.TextOf: the overlays of the command line (`--overlay`) put into each plan after it is made (tools/commands.c); NULL: the files */
static void (*pjPublishPlanHook)(FcCtx* c, const void* state, PjPlan* plan);
static const void* pjPublishHookState;
static const char* nsDescribe(FcCtx* c);   /* (project/standard.c: CompileErrors.Describe) */
static void pjLog(const char* line) { if (pjMonitorOut) { fcBufStr(pjMonitorOut, line); fcBufChar(pjMonitorOut, '\n'); } }

/* ---- the stamps ---- */
static const char* pjStampName(FcCtx* c, const char* engine, const char* target, const char* mode) { return fcFmt(c, "%s-%s-%s", engine, target, pjModeTag(c, mode)); }
static const char* pjStampPath(FcCtx* c, const PjLoaded* p, const char* name) { return nbCombine(c, nbCombine(c, nbCombine(c, p->project->directory, "obj"), "publish"), fcFmt(c, "%s.json", name)); }
static void pjHashAdd(FcCtx* c, FcSha256* h, const char* text) {
    FcBuf b = {0};
    uint8_t len[4];
    uint32_t n;
    (void)c;
    if (!text) text = "\xC0\x80";   /* ("\0": one unit 0) */
    fcBufStr(&b, "");
    fcOut8(&b, text);
    n = (uint32_t)b.len;
    len[0] = (uint8_t)n; len[1] = (uint8_t)(n >> 8); len[2] = (uint8_t)(n >> 16); len[3] = (uint8_t)(n >> 24);
    fcShaUpdate(h, len, 4);
    fcShaUpdate(h, (const uint8_t*)b.p, b.len);
    fcBufFree(&b);
}
static void pjHashFile(FcCtx* c, FcSha256* h, const char* path) {
    struct stat st;
    pjHashAdd(c, h, path);
    pjHashAdd(c, h, pjFileTime(path, &st) ? fcFmt(c, "%lld:%lld", (long long)st.st_size, (long long)pjMtimeTicks(&st)) : "missing");
}
static void pjHashProject(FcCtx* c, FcSha256* h, const PjLoaded* p, const FcList* sources) {
    FcList* content = pjBuildContentFiles(c, p);
    FcDict* copies = fcDictNew(c, FC_KEY_STR);
    int i;
    pjHashFile(c, h, p->project->filePath);
    for (i = 0; i < sources->count; i++) {
        const PjSource* s = (const PjSource*)sources->items[i];
        const char* hex = fcSha256OfText(c, s->text);
        char* upper = (char*)fcStr(c, hex);
        char* q;
        for (q = upper; *q; q++) *q = (char)toupper((unsigned char)*q);
        pjHashAdd(c, h, s->path);
        pjHashAdd(c, h, upper);
    }
    for (i = 0; i < p->copyFiles->count; i++) fcSetAdd(copies, ((PjCopyItem*)p->copyFiles->items[i])->source);
    for (i = 0; i < content->count; i++) if (!fcDictHas(copies, content->items[i])) pjHashFile(c, h, (const char*)content->items[i]);
}
static int pjCmpLibraryImport(const void* a, const void* b) { return strcmp((*(const PjLibrary* const*)a)->importName, (*(const PjLibrary* const*)b)->importName); }
static const char* pjSettingsJson(FcCtx* c, const PjSettings* s);
static const char* pjStampKey(FcCtx* c, const PjPlan* plan, const char* engine, const char* mode, const FgTarget* target, const NbToolchain* toolchain, const char* output, const NbConfig* config, int quick) {
    FcSha256 h;
    FcList* libs;
    int i;
    char self[4096];
#ifdef _WIN32
    if (!fwSelfPath(self, sizeof self)) self[0] = 0;
#else
    ssize_t n = readlink("/proc/self/exe", self, sizeof self - 1);
    self[n > 0 ? n : 0] = 0;
#endif
    fcShaInit(&h);
    pjHashAdd(c, &h, "fire publish 1");
    pjHashFile(c, &h, self);
    pjHashFile(c, &h, self);
    pjHashAdd(c, &h, engine); pjHashAdd(c, &h, mode); pjHashAdd(c, &h, target ? target->name : NULL); pjHashAdd(c, &h, pjFull(c, output, "."));
    if (toolchain) {
        FcList* args = fcListNew(c);
        pjHashAdd(c, &h, nbKind(toolchain)); pjHashAdd(c, &h, toolchain->compiler); pjHashAdd(c, &h, toolchain->optimization);
        if (toolchain->args) for (i = 0; i < toolchain->args->count; i++) fcListAdd(c, args, toolchain->args->items[i]);
        pjHashAdd(c, &h, fgJoin(c, args, " "));
    }
    if (config) { pjHashAdd(c, &h, config->linkage); if (config->path) pjHashFile(c, &h, config->path); }
    if (quick >= 0) pjHashAdd(c, &h, fcFmt(c, "quick %d", quick));
    pjHashAdd(c, &h, pjSettingsJson(c, plan->settings));
    for (i = 0; i < plan->packages->count; i++) { const PjReference* r = (const PjReference*)plan->packages->items[i]; pjHashAdd(c, &h, fcFmt(c, "%s@%s", r->package, r->version ? r->version : "")); }
    pjHashProject(c, &h, plan->project, plan->sources);
    libs = fcListCopy(c, plan->libraries);
    pjStableSort(libs, pjCmpLibraryImport);
    for (i = 0; i < libs->count; i++) { const PjLibrary* l = (const PjLibrary*)libs->items[i]; pjHashAdd(c, &h, l->importName); pjHashProject(c, &h, l->project, l->sources); }
    return fcShaHex(c, &h);
}
/* JsonSerializer.Serialize(plan.Settings): the default options (names as declared, nulls written, the default encoder) */
static const char* pjSettingsJson(FcCtx* c, const PjSettings* s) {
    FcBuf b = {0};
    int i;
#define PJS(name, v) do { fcBufStr(&b, "\"" name "\":"); if (v) fjEscape(&b, v, false); else fcBufStr(&b, "null"); fcBufChar(&b, ','); } while (0)
#define PJI(name, v) do { fcBufStr(&b, "\"" name "\":"); if ((v) != FC_NULL_INT) fcBufFmt(&b, "%d", v); else fcBufStr(&b, "null"); fcBufChar(&b, ','); } while (0)
#define PJL(name, l) do { fcBufStr(&b, "\"" name "\":"); if (l) { fcBufChar(&b, '['); for (i = 0; i < (l)->count; i++) { if (i) fcBufChar(&b, ','); fjEscape(&b, (const char*)(l)->items[i], false); } fcBufChar(&b, ']'); } else fcBufStr(&b, "null"); fcBufChar(&b, ','); } while (0)
    fcBufStr(&b, "{");
    PJS("Subsystem", s->subsystem); PJS("Mode", s->mode); PJI("FloatWidth", s->floatWidth); PJI("IntWidth", s->intWidth); PJS("Name", s->name); PJS("Codename", s->codename);
    PJS("Description", s->description); PJS("Author", s->author); PJS("License", s->license); PJS("Comments", s->comments); PJS("Icon", s->icon); PJS("Version", s->version);
    PJS("FileVersion", s->fileVersion); PJL("Defines", s->defines); PJS("Engine", s->engine); PJS("Target", s->target); PJS("Toolchain", s->toolchain); PJS("Linkage", s->linkage);
    fcBufStr(&b, "\"Stripping\":");
    if (!s->stripping) fcBufStr(&b, "null");
    else if (strlen(s->stripping) == 1 && isdigit((unsigned char)s->stripping[0])) fcBufFmt(&b, "%d", s->stripping[0] - '0');
    else fjEscape(&b, s->stripping, false);
    fcBufChar(&b, ',');
    PJS("Optimize", s->optimize); PJS("Output", s->output); PJS("TypeChecking", s->typeChecking); PJS("GlobalJobs", s->globalJobs); PJS("StackTrace", s->stackTrace); PJL("Arguments", s->arguments); PJS("Port", s->port);
    fcBufStr(&b, "\"Baud\":");
    if (s->baud != FC_NULL_INT) fcBufFmt(&b, "%d", s->baud); else fcBufStr(&b, "null");
    fcBufStr(&b, "}");
#undef PJS
#undef PJI
#undef PJL
    return fcBufDone(c, &b);
}
static bool pjIsUpToDate(FcCtx* c, const PjLoaded* p, const char* name, const char* key, const char* output) {
    const char* path = pjStampPath(c, p, name);
    const char* text;
    FjError error;
    FjValue* v;
    FjValue* x;
    struct stat st;
    if (!key || !nbFileExists(path) || !(text = pjReadAllText(c, path))) return false;
    if (!(v = fjParse(c, text, &error, NULL)) || v->kind != FJK_OBJECT) return false;
    if (!(x = fjProp(v, "Key")) || x->kind != FJK_STRING || !streq0(x->text, key)) return false;
    if (!(x = fjProp(v, "Output")) || x->kind != FJK_STRING || !streq0(x->text, pjFull(c, output, "."))) return false;
    if (nbDirExists(output)) return true;
    if (!pjFileTime(output, &st)) return false;
    if (!(x = fjProp(v, "Length")) || x->kind != FJK_NUMBER || atoll(x->text) != (long long)st.st_size) return false;
    if (!(x = fjProp(v, "Time")) || x->kind != FJK_STRING || !streq0(x->text, fcFmt(c, "%lld", (long long)pjMtimeTicks(&st)))) return false;
    return true;
}
static void pjWriteStamp(FcCtx* c, const PjLoaded* p, const char* name, const char* key, const char* output) {
    const char* path = pjStampPath(c, p, name);
    struct stat st;
    bool exists = pjFileTime(output, &st);
    FcBuf b = {0};
    if (!key) return;
    nbMkdirs(c, fcDirectoryName(c, path));
    fcBufStr(&b, "{\"Key\":");
    fjEscape(&b, key, false);
    fcBufStr(&b, ",\"Output\":");
    fjEscape(&b, pjFull(c, output, "."), false);
    fcBufFmt(&b, ",\"Length\":%lld,\"Time\":\"%lld\"}", exists ? (long long)st.st_size : 0LL, exists ? (long long)pjMtimeTicks(&st) : 0LL);
    nbWriteText(c, path, fcBufDone(c, &b));
}

/* Companions: the program and the files of the same name with another extension (not the statistics, not .tmp/.stub), with linkage shared the libraries */
static FcList* pjCompanions(FcCtx* c, const char* program, const char* linkage) {
    FcList* files = fcListNew(c);
    const char* full = pjFull(c, program, ".");
    const char* folder = fcDirectoryName(c, full);
    const char* stem = pjStem(c, full);
    const char* name = pjFileName(full);
    bool shared = linkage && pjIEq(linkage, "shared");
    FcList* names = fcListNew(c);
    int i;
    fcListAdd(c, files, (void*)full);
    pjEnumerate(c, folder, names, NULL);
    for (i = 0; i < names->count; i++) {
        const char* n = (const char*)names->items[i];
        const char* ext = pjExtension(c, n);
        const char* lowerExt = pjLower(c, ext);
        bool sameName, library;
        if (streq0(n, name)) continue;
        if (fcEndsWith(pjLower(c, n), ".tmp") || fcEndsWith(pjLower(c, n), ".stub") || strstr(pjLower(c, n), ".stats.")) continue;
        sameName = streq0(pjStem(c, n), stem) && *ext;
        library = shared && (streq0(lowerExt, ".so") || streq0(lowerExt, ".dll") || streq0(lowerExt, ".dylib"));
        if (sameName || library) fcListAdd(c, files, (void*)nbCombine(c, folder, n));
    }
    return files;
}
/* CleanProblem: why a folder must not be emptied (NULL: it may) */
static bool pjInsideOrSame(FcCtx* c, const char* dir, const char* full) {
    const char* d;
    if (!dir) return false;
    d = pjTrimEndSep(c, pjFull(c, dir, "."));
    return streq0(d, full) || fcStartsWith(d, fcFmt(c, "%s/", full));
}
static bool pjFindBelow(FcCtx* c, const char* dir, bool (*match)(FcCtx* c, const char* name, bool isDir), const char** found, const char* root) {
    FcList* files = fcListNew(c);
    FcList* folders = fcListNew(c);
    int i;
    pjEnumerate(c, dir, files, folders);
    for (i = 0; i < folders->count; i++) if (match(c, (const char*)folders->items[i], true)) { *found = pjRelative(c, root, nbCombine(c, dir, (const char*)folders->items[i])); return true; }
    for (i = 0; i < files->count; i++) if (match(c, (const char*)files->items[i], false)) { *found = pjRelative(c, root, nbCombine(c, dir, (const char*)files->items[i])); return true; }
    for (i = 0; i < folders->count; i++) {
        const char* sub = nbCombine(c, dir, (const char*)folders->items[i]);
        struct stat st;
        if (lstat(sub, &st) == 0 && S_ISLNK(st.st_mode)) continue;
        if (pjFindBelow(c, sub, match, found, root)) return true;
    }
    return false;
}
static bool pjIsGit(FcCtx* c, const char* name, bool isDir) { (void)c; (void)isDir; return pjIEq(name, ".git"); }
static bool pjIsProjFile(FcCtx* c, const char* name, bool isDir) { return !isDir && pjIEq(pjExtension(c, name), ".fireproj"); }
static bool pjIsSlnFile(FcCtx* c, const char* name, bool isDir) { return !isDir && pjIEq(pjExtension(c, name), ".firesln"); }
static const char* pjCleanProblem(FcCtx* c, PjWorkspace* w, const char* folder) {
    const char* full = pjTrimEndSep(c, pjFull(c, folder, "."));
    const char* home = getenv("HOME");
    const char* found;
    int i;
    if (!*full || fcIsRootDir(full)) return "it is the root of a drive";
    if (home && *home && streq0(pjTrimEndSep(c, home), full)) return "it is the home folder";
    if (w->solution && pjInsideOrSame(c, w->solution->directory, full)) return "the solution is in it";
    for (i = 0; i < w->all->count; i++) { PjLoaded* p = (PjLoaded*)w->all->items[i]; if (pjInsideOrSame(c, p->project->directory, full)) return fcFmt(c, "the project %s is in it", p->project->name); }
    if (!nbDirExists(full)) return NULL;
    if (pjFindBelow(c, full, pjIsGit, &found, full)) return "it holds a git repository";
    if (pjFindBelow(c, full, pjIsProjFile, &found, full)) return fcFmt(c, "it holds the project file %s", found);
    if (pjFindBelow(c, full, pjIsSlnFile, &found, full)) return fcFmt(c, "it holds the solution file %s", found);
    return NULL;
}
static int pjEmptyFolder(FcCtx* c, const char* folder) {
    FcList* files = fcListNew(c);
    FcList* folders = fcListNew(c);
    int i, n = 0;
    pjEnumerate(c, folder, files, folders);
    for (i = 0; i < folders->count; i++) {
        const char* sub = nbCombine(c, folder, (const char*)folders->items[i]);
        struct stat st;
        if (lstat(sub, &st) == 0 && S_ISLNK(st.st_mode)) { if (unlink(sub) == 0) n++; }
        else { nbDeleteTree(c, sub); if (!nbDirExists(sub)) n++; }
    }
    for (i = 0; i < files->count; i++) if (unlink(nbCombine(c, folder, (const char*)files->items[i])) == 0) n++;
    return n;
}

static PjPublishResult* pjPublishFail(FcCtx* c, PjLoaded* project, const char* error, FcList* diagnostics, const char* engine, const char* mode) {
    PjPublishResult* r = (PjPublishResult*)fcAlloc(c, sizeof *r);
    pjLog(fcFmt(c, "%s: error: %s", project->project->name, pjTrimEndWs(c, error)));
    r->project = project; r->error = error; r->diagnostics = diagnostics ? diagnostics : fcListNew(c); r->engine = engine; r->mode = mode; r->folders = fcListNew(c);
    return r;
}
static const char* pjDateNow(FcCtx* c) {
    time_t t = time(NULL);
    struct tm tmv;
    char b[32];
    localtime_r(&t, &tmv);
    strftime(b, sizeof b, "%Y-%m-%d %H:%M", &tmv);
    return fcStr(c, b);
}
static PjPublishResult* pjPublishOne(FcCtx* c, PjOut* po, PjWorkspace* w, PjLoaded* project, const PjPublishOptions* options, const char* prelude, const char* storeRoot) {
    PjPlan* volatile plan = NULL;
    const char* volatile planError = NULL;
    const char* volatile engine;
    const char* modeName;
    const char* targetName;
    const char* volatile toolchainName;
    int mode;
    FcList* folders;
    const char* volatile output = NULL;
    volatile bool upToDate = false;
    const char* volatile key = NULL;
    NbConfig* volatile config = NULL;
    const FgTarget* volatile target = NULL;
    FcList* diagnostics = fcListNew(c);
    PjPublishResult* volatile failed = NULL;
    FcList* contentItems;
    PjCopyResult copiedNext;
    FcDict* items;
    FcList* itemList;
    bool isFolder;
    int copied = 0, i;
    FcList* problems = fcListNew(c);
    PjPublishResult* r;
    FC_TRY(c) {
        plan = pjPlanCreate(w, project, false);
        if (pjPublishPlanHook) pjPublishPlanHook(c, pjPublishHookState, plan);
    } FC_CATCH(c) {
        if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
        planError = c->err.message;
    } FC_END_TRY;
    if (planError) return pjPublishFail(c, project, planError, NULL, "", "");
    if (plan->errors->count > 0) return pjPublishFail(c, project, fgJoin(c, plan->errors, "\n"), NULL, "", "");
    /* Choices */
    engine = project->project->publish && project->project->publish->engine ? project->project->publish->engine : plan->settings->engine ? plan->settings->engine : "native";
    modeName = project->project->publish && project->project->publish->mode ? project->project->publish->mode : "release";
    targetName = project->project->publish && project->project->publish->target ? project->project->publish->target : plan->settings->target;
    toolchainName = project->project->publish && project->project->publish->toolchain ? project->project->publish->toolchain : plan->settings->toolchain;
    mode = pjIEq(modeName, "debug") ? 0 : pjIEq(modeName, "release") ? 1 : pjIEq(modeName, "performance") ? 2 : -1;
    if (mode < 0) return pjPublishFail(c, project, fcFmt(c, "'%s' is no mode (debug, release, performance).", modeName), NULL, "", "");
    folders = pjPublishTargetFolders(c, project, w->solution ? w->solution->directory : NULL);
    FC_TRY(c) {
        if (project->project->type == PJ_LIBRARY) {
            engine = "package";
            output = pjPackagePathFor(c, plan, NULL);
            key = pjStampKey(c, plan, "package", modeName, NULL, NULL, output, NULL, -1);
            upToDate = !options->rebuild && pjIsUpToDate(c, project, "package", key, output);
            if (!upToDate) {
                pjLog(fcFmt(c, "%s: packing the library (a library is published as its package)", project->project->name));
                output = pjPackLibrary(c, po, plan, NULL, prelude, storeRoot, true);
            }
        } else if (streq0(engine, "native")) {
            NbToolchain* toolchain;
            bool files;
            NbBuild b;
            NbResult res;
            FcCtx step;
            volatile bool broken = false;
            config = nbConfigFindFor(c, project->project->filePath);
            if (plan->settings->linkage) config->linkage = plan->settings->linkage;
            if (!config->splitGiven) config->split = FG_SPLIT_OFF;
            config->quick = options->quick;
            target = nbResolveTarget(c, config, targetName);
            toolchain = nbResolveToolchain(c, config, target, options->toolchain ? options->toolchain : toolchainName, NB_PURPOSE_PUBLISH);
            files = streq0(nbKind(toolchain), "files");
            output = plan->settings->output && !files ? plan->settings->output
                : files ? pjOutputFolder(c, project->project->directory, target->name, modeName)
                : pjProgramPath(c, project->project->directory, project->project->name, target->name, modeName, target->programExtension);
            key = pjStampKey(c, plan, engine, modeName, target, toolchain, output, config, options->quick);
            upToDate = !options->rebuild && pjIsUpToDate(c, project, pjStampName(c, engine, target->name, modeName), key, output);
            if (!upToDate) {
                pjLog(fcFmt(c, "%s: building natively for %s (%s, %s%s)", project->project->name, target->name, modeName, nbKind(toolchain),
                    options->quick >= 0 ? fcFmt(c, ", %s", nbQuickLabel(c, options->quick, nbKind(toolchain))) : ""));
                nbMkdirs(c, files ? pjFull(c, output, ".") : fcDirectoryName(c, pjFull(c, output, ".")));
                pjApplyBuildSettings(c, config, plan);
                memset(&b, 0, sizeof b);
                b.prelude = prelude; b.storeRoot = storeRoot; b.mode = mode;
                b.link = pjNbLink;
                b.overlay = pjNbOverlay;
                b.project = plan;
                pjStepInit(&step);
                nbMonitorLine = pjMonitorLine;
                FC_TRY(&step) {
                    res = nbBuild(&step, &b, config, target, toolchain, output);
                } FC_CATCH(&step) {
                    FcList* errs;
                    if (!pjIsCompileErrorKind(step.err.kind) && step.err.kind != FC_ERR_NATIVE) { nbMonitorLine = NULL; c->err = step.err; fcRethrow(c); }
                    errs = fcDiagnosticsOf(&step, &step.err, NULL);
                    for (i = 0; i < errs->count; i++) fcListAdd(c, diagnostics, errs->items[i]);
                    broken = true;
                    res.ok = false;
                    res.log = step.err.kind == FC_ERR_NATIVE ? fcFmt(c, "Not supported by the native backend yet: %s", step.err.message) : "";
                    if (step.err.kind != FC_ERR_NATIVE) res.log = fcStr(c, nsDescribe(&step));   /* (BuildSafe: CompileErrors.Describe) */
                    res.error = NULL;
                } FC_END_TRY;
                nbMonitorLine = NULL;
                (void)broken;
                for (i = 0; i < step.diags->count; i++) fcListAdd(c, diagnostics, step.diags->items[i]);
                if (!res.ok) {
                    FcList* lines = fgSplit(c, pjTrimEndWs(c, fgReplace(c, res.log ? res.log : "", "\r\n", "\n")), '\n');
                    const char* first = NULL;
                    int from = lines->count > 30 ? lines->count - 30 : 0;
                    if (res.error) {
                        FcDiagnostic* d = (FcDiagnostic*)fcAlloc(c, sizeof *d);
                        d->line = 0; d->message = res.error; d->severity = FC_SEV_ERROR; d->code = strstr(res.error, "was not found") ? "E7003" : "E7002";
                        fcListInsert(c, diagnostics, 0, d);
                    }
                    for (i = from; i < lines->count; i++) pjLog((const char*)lines->items[i]);
                    for (i = 0; i < diagnostics->count && !first; i++) if (((FcDiagnostic*)diagnostics->items[i])->severity == FC_SEV_ERROR) first = ((FcDiagnostic*)diagnostics->items[i])->message;
                    failed = pjPublishFail(c, project, first ? first : "the native build failed (see the build log)", diagnostics, engine, modeName);
                } else output = res.output;
            }
        } else if (streq0(engine, "vm")) {
            /* a program that carries the VM in C (build/cvm.c) */
            output = plan->settings->output ? plan->settings->output : pjProgramPath(c, project->project->directory, project->project->name, "vm", modeName, NV_EXTENSION);
            key = pjStampKey(c, plan, engine, modeName, NULL, NULL, output, NULL, -1);
            upToDate = !options->rebuild && pjIsUpToDate(c, project, pjStampName(c, engine, "vm", modeName), key, output);
            if (!upToDate) {
                FcCtx step;
                PjLinkIn in;
                const char* volatile vmError = NULL;
                volatile bool broken = false;
                nvRefuseDotnet(c);
                nvStoreRoot = storeRoot;
                pjLog(fcFmt(c, "%s: building a program with the virtual machine (%s)", project->project->name, modeName));
                nbMkdirs(c, fcDirectoryName(c, pjFull(c, output, ".")));
                memset(&in, 0, sizeof in);
                in.plan = plan; in.prelude = prelude; in.storeRoot = storeRoot; in.modeOverride = mode;
                pjStepInit(&step);
                FC_TRY(&step) {
                    FcStore* store = NULL;
                    FcNatives* registry = NULL;
                    FcLinked* linked;
                    FcList* libraries;
                    const char* error = NULL;
                    in.storeOut = &store;
                    linked = pjLink(&step, &in, &registry);
                    libraries = nvEnsureLibraries(&step, linked, store, registry, NULL);
                    if (!nvPack(&step, linked, libraries, output, NULL, &error)) vmError = fcStr(c, error);
                } FC_CATCH(&step) {
                    FcList* errs;
                    if (step.err.kind == FC_ERR_PACKAGE) vmError = fcStr(c, step.err.message);
                    else if (!pjIsCompileErrorKind(step.err.kind)) { c->err = step.err; fcRethrow(c); }
                    else {
                        errs = fcDiagnosticsOf(&step, &step.err, NULL);
                        for (i = 0; i < errs->count; i++) fcListAdd(c, diagnostics, errs->items[i]);
                        broken = true;
                    }
                } FC_END_TRY;
                for (i = 0; i < step.diags->count; i++) fcListAdd(c, diagnostics, step.diags->items[i]);
                if (broken) {
                    const char* first = NULL;
                    for (i = 0; i < diagnostics->count && !first; i++) if (((FcDiagnostic*)diagnostics->items[i])->severity == FC_SEV_ERROR) first = ((FcDiagnostic*)diagnostics->items[i])->message;
                    failed = pjPublishFail(c, project, first ? first : "it does not compile", diagnostics, engine, modeName);
                } else if (vmError) failed = pjPublishFail(c, project, vmError, NULL, engine, modeName);
            }
        } else failed = pjPublishFail(c, project, fcFmt(c, "'%s' is no engine (vm, native).", engine), NULL, "", "");
    } FC_CATCH(c) {
        if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "native-config: ")) failed = pjPublishFail(c, project, c->err.message + 15, NULL, engine, modeName);
        else if (c->err.kind == FC_ERR_PROJECT || c->err.kind == FC_ERR_PACKAGE) failed = pjPublishFail(c, project, c->err.message, NULL, engine, modeName);
        else if (pjIsCompileErrorKind(c->err.kind)) failed = pjPublishFail(c, project, c->err.message, fcDiagnosticsOf(c, &c->err, NULL), engine, modeName);
        else fcRethrow(c);
    } FC_END_TRY;
    if (failed) return failed;
    if (upToDate) pjLog(fcFmt(c, "%s: %s is up to date (not built again)", project->project->name, pjRelative(c, project->project->directory, pjFull(c, output, "."))));
    else {
        pjWriteStamp(c, project, project->project->type == PJ_LIBRARY ? "package" : pjStampName(c, engine, target ? target->name : "vm", modeName), key, output);
        pjLog(fcFmt(c, "%s: built %s", project->project->name, output));
    }
    /* what goes into the target folders */
    isFolder = nbDirExists(output);
    contentItems = project->project->type == PJ_LIBRARY ? fcListNew(c) : pjContentItemsOf(c, plan);
    copiedNext = pjContentCopyTo(c, contentItems, isFolder ? output : fcDirectoryName(c, pjFull(c, output, ".")));
    for (i = 0; i < copiedNext.problems->count; i++) pjLog(fcFmt(c, "%s: warning: %s", project->project->name, (const char*)copiedNext.problems->items[i]));
    items = fcDictNew(c, FC_KEY_STR);
    if (isFolder) {
        FcList* files = fcListNew(c);
        pkFilesBelow(c, pjFull(c, output, "."), files);
        for (i = 0; i < files->count; i++) {
            PjCopyItem* item = (PjCopyItem*)fcAlloc(c, sizeof *item);
            item->source = (const char*)files->items[i];
            item->relativePath = pjRelative(c, pjFull(c, output, "."), item->source);
            fcDictPut(items, item->relativePath, item);
        }
    } else {
        FcList* companions = pjCompanions(c, output, config ? config->linkage : NULL);
        for (i = 0; i < companions->count; i++) {
            PjCopyItem* item = (PjCopyItem*)fcAlloc(c, sizeof *item);
            item->source = (const char*)companions->items[i];
            item->relativePath = pjFileName(item->source);
            fcDictPut(items, item->relativePath, item);
        }
    }
    for (i = 0; i < contentItems->count; i++) fcDictPut(items, ((PjCopyItem*)contentItems->items[i])->relativePath, contentItems->items[i]);
    itemList = fcListNew(c);
    FC_DICT_EACH(items, e) fcListAdd(c, itemList, e->value);
    for (i = 0; i < folders->count; i++) {
        const char* folder = (const char*)folders->items[i];
        PjCopyResult cr;
        const char* quickNote;
        if (project->project->publish && project->project->publish->clean == 1) {
            const char* refused = pjCleanProblem(c, w, folder);
            if (refused) pjLog(fcFmt(c, "%s: %s is not emptied: %s", project->project->name, folder, refused));
            else if (nbDirExists(folder)) { int removed = pjEmptyFolder(c, folder); pjLog(fcFmt(c, "%s: emptied %s (%d entr%s)", project->project->name, folder, removed, removed == 1 ? "y" : "ies")); }
        }
        if (!nbMkdirs(c, folder) && !nbDirExists(folder)) { fcListAdd(c, problems, (void*)fcFmt(c, "%s: Could not create the folder.", folder)); continue; }
        cr = pjContentCopyTo(c, itemList, folder);
        quickNote = options->quick >= 0 && streq0(engine, "native") ? fcFmt(c, " %s: a quick build for tests, not a release.", nbQuickLabel(c, options->quick, NULL)) : "";
        nbWriteText(c, nbCombine(c, folder, PJ_PUBLISH_MARKER), fcFmt(c, "Published by fire: %s (%s). The files here are copies; Publish overwrites them.%s\n", project->project->name, pjDateNow(c), quickNote));
        copied += cr.copied->count;
        { int k; for (k = 0; k < cr.problems->count; k++) fcListAdd(c, problems, (void*)fcFmt(c, "%s: %s", folder, (const char*)cr.problems->items[k])); }
        pjLog(fcFmt(c, "%s: %s: %d file%s copied%s", project->project->name, folder, cr.copied->count, cr.copied->count == 1 ? "" : "s", cr.upToDate > 0 ? fcFmt(c, ", %d up to date", cr.upToDate) : ""));
    }
    if (problems->count > 0) return pjPublishFail(c, project, fgJoin(c, problems, "\n"), diagnostics, engine, modeName);
    r = (PjPublishResult*)fcAlloc(c, sizeof *r);
    r->project = project; r->ok = true; r->upToDate = upToDate; r->program = output; r->engine = engine; r->mode = modeName;
    r->targetName = target ? target->name : streq0(engine, "vm") ? "vm" : NULL;
    r->folders = folders; r->filesCopied = copied; r->diagnostics = diagnostics;
    return r;
}
/* ProjectPublisher.Publish; `projects` NULL: the members that have publishing on */
static FcList* pjPublish(FcCtx* c, PjOut* po, PjWorkspace* w, FcList* projects, const PjPublishOptions* options, const char* prelude, const char* storeRoot, bool* ok) {
    FcList* order = pjBuildOrder(w);
    FcList* asked = fcListNew(c);
    FcList* ordered = fcListNew(c);
    FcList* failed = fcListNew(c);
    FcList* results = fcListNew(c);
    int i, k, published = 0, failures = 0, skipped = 0, upToDate = 0;
    FcList* doneFolders = fcListNew(c);
    FcBuf summary = {0};
    bool anyNative = false;
    if (projects) { for (i = 0; i < projects->count; i++) if (!pjInList(asked, projects->items[i])) fcListAdd(c, asked, projects->items[i]); }
    else for (i = 0; i < order->count; i++) { PjLoaded* p = (PjLoaded*)order->items[i]; if (p->project->publish && p->project->publish->enabled) fcListAdd(c, asked, p); }
    for (i = 0; i < order->count; i++) if (pjInList(asked, order->items[i])) fcListAdd(c, ordered, order->items[i]);
    for (i = 0; i < asked->count; i++) if (!pjInList(order, asked->items[i])) fcListAdd(c, ordered, asked->items[i]);
    if (options->quick >= 0) pjLog(fcFmt(c, "%s: a quick publish for tests, not a release - the native programs are built without full optimisation.", nbQuickLabel(c, options->quick, NULL)));
    for (i = 0; i < ordered->count; i++) {
        PjLoaded* project = (PjLoaded*)ordered->items[i];
        FcList* refs;
        PjLoaded* broken = NULL;
        PjPublishResult* one;
        pjLog(fcFmt(c, "------ Publish %d/%d: %s ------", i + 1, ordered->count, project->project->name));
        if (!(project->project->publish && project->project->publish->enabled) && !options->includeDisabled) {
            pjLog(fcFmt(c, "%s: skipped - publishing is off (Project Properties > Publish).", project->project->name));
            one = (PjPublishResult*)fcAlloc(c, sizeof *one);
            one->project = project; one->skipped = true; one->folders = fcListNew(c); one->diagnostics = fcListNew(c);
            fcListAdd(c, results, one);
            continue;
        }
        refs = pjReferencedProjects(w, project);
        for (k = 0; k < refs->count && !broken; k++) if (pjInList(failed, refs->items[k])) broken = (PjLoaded*)refs->items[k];
        if (broken) {
            const char* why = fcFmt(c, "the library %s it references did not publish", broken->project->name);
            fcListAdd(c, failed, project);
            pjLog(fcFmt(c, "%s: skipped - %s.", project->project->name, why));
            one = (PjPublishResult*)fcAlloc(c, sizeof *one);
            one->project = project; one->skipped = true; one->error = why; one->folders = fcListNew(c); one->diagnostics = fcListNew(c);
            fcListAdd(c, results, one);
            continue;
        }
        one = pjPublishOne(c, po, w, project, options, prelude, storeRoot);
        if (!one->ok) fcListAdd(c, failed, project);
        fcListAdd(c, results, one);
    }
    /* PublishResult.Summary */
    *ok = true;
    for (i = 0; i < results->count; i++) {
        PjPublishResult* r = (PjPublishResult*)results->items[i];
        if (r->ok) {
            published++;
            if (r->upToDate) upToDate++;
            for (k = 0; k < r->folders->count; k++) if (!fcListHasStr(doneFolders, (const char*)r->folders->items[k])) fcListAdd(c, doneFolders, r->folders->items[k]);
        }
        else if (!r->skipped) failures++;
        if (r->skipped) skipped++;
        if (!(r->ok || (r->skipped && !r->error))) *ok = false;
        if (r->engine && streq0(r->engine, "native")) anyNative = true;
    }
    fcBufFmt(&summary, "%d project%s published", published, published == 1 ? "" : "s");
    if (doneFolders->count > 0) fcBufFmt(&summary, " to %s", fgJoin(c, doneFolders, ", "));
    if (upToDate > 0) fcBufFmt(&summary, " (%d up to date, not built again)", upToDate);
    if (failures > 0) fcBufFmt(&summary, ", %d failed", failures);
    if (skipped > 0) fcBufFmt(&summary, ", %d skipped", skipped);
    if (options->quick >= 0 && anyNative) fcBufFmt(&summary, " - %s, not for release", nbQuickLabel(c, options->quick, NULL));
    fcBufChar(&summary, '.');
    pjPublishSummary = fcBufDone(c, &summary);   /* (PublishResult.Summary: the end of the report, tools/report.c) */
    pjLog(fcFmt(c, "========== Publish: %s ==========", pjPublishSummary));
    return results;
}
