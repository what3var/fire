/* native/forge/project/cli.c - the command line of projects and solutions and the measures of Phase D 3 (commit de45e7e4): the options of `build` and
 * `publish` with a project (CommandLineParser.Parse, the part they read), `forge-c publish` (CommandLine.Publish), `forge-c projectplan <file>` (what
 * FrontendDump.DumpProjectPlan prints) and `forge-c fpk <file.fpk>` (FrontendDump.DumpPackage). */

static int pjParseArgs(int argc, char** argv, PjBuildArgs* a, bool publish) {
    int i;
    memset(a, 0, sizeof *a);
    a->mode = -1; a->stripping = -1; a->optimize = -1; a->quick = -1; a->split = -1;
    for (i = 2; i < argc; i++) {
        const char* s = argv[i];
        const char* next = i + 1 < argc ? argv[i + 1] : NULL;
#define PJ_VALUE() (next ? (i++, next) : NULL)
        if (streq0(s, "-o") || streq0(s, "--output")) { a->output = PJ_VALUE(); }
        else if (streq0(s, "-p") || streq0(s, "--project")) { a->projectName = PJ_VALUE(); if (!a->projectName) { fprintf(stderr, "-p must be followed by the name of a project.\n"); return 2; } }
        else if (fcStartsWith(s, "--project=")) a->projectName = s + 10;
        else if (streq0(s, "-m") || streq0(s, "--mode")) {
            const char* m = PJ_VALUE();
            if (!m || !(fcAsciiIEq(m, "debug") || fcAsciiIEq(m, "release") || fcAsciiIEq(m, "performance"))) { fprintf(stderr, "-m must be followed by DEBUG, RELEASE or PERFORMANCE.\n"); return 2; }
            a->mode = fcAsciiIEq(m, "debug") ? 0 : fcAsciiIEq(m, "release") ? 1 : 2;
        }
        else if (streq0(s, "-t") || streq0(s, "--target")) a->targetName = PJ_VALUE();
        else if (fcStartsWith(s, "--target=")) a->targetName = s + 9;
        else if (streq0(s, "--toolchain")) a->toolchainName = PJ_VALUE();
        else if (fcStartsWith(s, "--toolchain=")) a->toolchainName = s + 12;
        else if (streq0(s, "--config")) a->configFile = PJ_VALUE();
        else if (streq0(s, "--engine")) {
            const char* e = PJ_VALUE();
            if (!e || !(fcAsciiIEq(e, "vm") || fcAsciiIEq(e, "native"))) { fprintf(stderr, "--engine must be followed by vm or native.\n"); return 2; }
            a->engine = fcAsciiIEq(e, "vm") ? "vm" : "native";
        }
        else if (streq0(s, "--linkage")) a->linkage = PJ_VALUE();
        else if (fcAsciiIEq(s, "--host")) {
            a->host = PJ_VALUE();
            if (!a->host || !*a->host) { fprintf(stderr, "--host must be followed by the program the plugin is built for.\n"); return 2; }
        }
        else if (streq0(s, "--split")) { const char* v = PJ_VALUE(); if (!v || !*v || (a->split = fgSplitModeParse(v)) < 0) { fprintf(stderr, "--split must be followed by auto, on or off.\n"); return 2; } }
        else if (streq0(s, "--quick") || fcStartsWith(s, "--quick=")) {
            a->quick = nbQuickParseLevel(s[7] == '=' ? s + 8 : "");
            if (a->quick < 0) { fprintf(stderr, "--quick takes the optimisation level 0 or 1 (--quick, --quick=1).\n"); return 2; }
        }
        else if (streq0(s, "--stripping")) {
            FcCtx scratch;
            const char* v = PJ_VALUE();
            fcCtxInit(&scratch);
            if (!v || (a->stripping = nbStrippingParse(&scratch, v)) < 0) { fcCtxFree(&scratch); fprintf(stderr, "--stripping must be followed by 0, 1, 2 or 3 (none, protected, methods, all).\n"); return 2; }
            fcCtxFree(&scratch);
        }
        else if (streq0(s, "--optimize")) {
            const char* v = PJ_VALUE();
            if (v && fcAsciiIEq(v, "speed")) a->optimize = 0; else if (v && fcAsciiIEq(v, "size")) a->optimize = 1;
            else { fprintf(stderr, "--optimize must be followed by speed or size.\n"); return 2; }
        }
        else if (streq0(s, "--keep-all")) a->keepAll = true;
        else if (streq0(s, "--keep")) a->keep = true;
        else if (streq0(s, "--stats")) a->stats = true;
        else if (!publish && fcAsciiIEq(s, "--all")) a->all = true;
        else if (!publish && fcAsciiIEq(s, "--check")) a->check = true;
        else if (streq0(s, "-f") || streq0(s, "--float")) { const char* v = PJ_VALUE(); a->floatWidth = v ? atoi(v) : 0; }
        else if (s[0] == '-') { fprintf(stderr, "forge-c %s: the option %s is not ported.\n", publish ? "publish" : "build", s); return 2; }
        else if (!a->file) a->file = s;
        else { fprintf(stderr, "%s takes one solution (.firesln) or project (.fireproj).\n", publish ? "publish" : "build"); return 2; }
#undef PJ_VALUE
    }
    if (!a->file) { fprintf(stderr, "usage: forge-c %s <project.fireproj | solution.firesln> [-p <project>] [--toolchain <name>] [--quick[=0|1]]%s\n", publish ? "publish" : "build", publish ? "" : " [--all] [--check] [-o <out>] [-t <target>] [--engine native] ..."); return 2; }
    return -1;
}
/* `forge-c build <project|solution> ...` */
static int pjBuildCommand(int argc, char** argv, const char* prelude, const char* storeRoot) {
    PjBuildArgs a;
    int r = pjParseArgs(argc, argv, &a, false);
    if (r >= 0) return r;
    return pjBuildMain(&a, prelude, storeRoot);
}
/* CommandLine.Publish: the projects of the solution that publish (or -p, or the one project of a .fireproj), the log on stdout, the diagnostics on stderr */
static int pjPublishRun(FcCtx* c, const char* file, int files, const char* projectName, const char* toolchainName, int quick, const char* prelude, const char* storeRoot) {
    FcBuf out = {0}, err = {0};
    PjOut po;
    volatile int result = 0;
    if (files != 1 || !pjIsProjectFile(file)) { fprintf(stderr, "publish takes one solution (.firesln) or project (.fireproj).\n"); return 2; }
    if (!nbFileExists(file)) { fprintf(stderr, "File not found: %s\n", file); return 2; }
    fcBufStr(&out, "");
    fcBufStr(&err, "");
    po.c = c; po.seen = fcListNew(c); po.err = &err;
    pjMonitorOut = &out;
    FC_TRY(c) {
        PjWorkspace* volatile w = NULL;
        FcList* volatile projects = NULL;
        bool usage = false;
        FC_TRY(c) {
            w = pjWorkspaceOpen(c, file);
            if (projectName) { projects = fcListNew(c); fcListAdd(c, projects, pjSelectProject(w, projectName)); }
            else if (!w->solution) projects = fcListCopy(c, w->members);
        } FC_CATCH(c) {
            if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
            fcBufFmt(&err, "%s\n", c->err.message);
            result = 2; usage = true;
        } FC_END_TRY;
        if (!usage) {
            PjPublishOptions options;
            bool ok = true;
            FcList* results;
            int i, k;
            options.toolchain = toolchainName; options.includeDisabled = projects != NULL; options.quick = quick; options.rebuild = false;
            results = pjPublish(c, &po, w, projects, &options, prelude, storeRoot, &ok);
            for (i = 0; i < results->count; i++) {
                PjPublishResult* p = (PjPublishResult*)results->items[i];
                for (k = 0; p->diagnostics && k < p->diagnostics->count; k++) fcDiagLogLine(&err, (const FcDiagnostic*)p->diagnostics->items[k]);
            }
            result = ok ? 0 : 1;
        }
    } FC_CATCH(c) {
        if (c->err.kind == FC_ERR_INTERNAL && c->err.message && fcStartsWith(c->err.message, "unported: ")) fcBufFmt(&err, "forge-c: %s\n", c->err.message);
        else fcBufFmt(&err, "forge-c: internal: %s\n", c->err.message ? c->err.message : "?");
        result = 2;
    } FC_END_TRY;
    pjMonitorOut = NULL;
    fwrite(err.p, 1, err.len, stderr);
    fwrite(out.p, 1, out.len, stdout);
    fcBufFree(&out);
    fcBufFree(&err);
    return result;
}
/* `forge-c publish <project|solution> [-p <project>] [--toolchain <name>] [--quick[=0|1]]` (CommandLine.Publish) */
static int pjPublishCommand(int argc, char** argv, const char* prelude, const char* storeRoot) {
    PjBuildArgs a;
    FcCtx ctx;
    int result;
    int r = pjParseArgs(argc, argv, &a, true);
    if (r >= 0) return r;
    fcCtxInit(&ctx);
    result = pjPublishRun(&ctx, a.file, 1, a.projectName, a.toolchainName, a.quick, prelude, storeRoot);
    fcCtxFree(&ctx);
    return result;
}

/* ---- `projectplan <file>`: the measure of the model (FrontendDump.DumpProjectPlan prints the same) ---- */
typedef struct PjDump { FcCtx* c; FcBuf* o; const char* root; } PjDump;
static const char* pjDumpPath(PjDump* d, const char* path) { return path ? pjRelative(d->c, d->root, pjFull(d->c, path, ".")) : "-"; }
static void pjDumpLine(PjDump* d, const char* indent, const char* key, const char* value) {
    fcBufStr(d->o, indent);
    fcBufStr(d->o, key);
    fcBufChar(d->o, ' ');
    fcOut8(d->o, value ? value : "-");
    fcBufChar(d->o, '\n');
}
static void pjDumpSettings(PjDump* d, const char* indent, const PjSettings* s) {
    FcCtx* c = d->c;
    int i;
#define PJD(name, v) if (v) pjDumpLine(d, indent, "setting", fcFmt(c, name "=%s", v))
#define PJDI(name, v) if ((v) != FC_NULL_INT) pjDumpLine(d, indent, "setting", fcFmt(c, name "=%d", v))
    PJD("subsystem", s->subsystem); PJD("mode", s->mode); PJDI("floatWidth", s->floatWidth); PJDI("intWidth", s->intWidth); PJD("name", s->name); PJD("codename", s->codename);
    PJD("description", s->description); PJD("author", s->author); PJD("license", s->license); PJD("comments", s->comments);
    if (s->icon) pjDumpLine(d, indent, "setting", fcFmt(c, "icon=%s", pjDumpPath(d, s->icon)));
    PJD("version", s->version); PJD("fileVersion", s->fileVersion);
    if (s->defines) for (i = 0; i < s->defines->count; i++) pjDumpLine(d, indent, "setting", fcFmt(c, "define=%s", (const char*)s->defines->items[i]));
    PJD("engine", s->engine); PJD("target", s->target); PJD("toolchain", s->toolchain); PJD("linkage", s->linkage); PJD("stripping", s->stripping); PJD("optimize", s->optimize);
    if (s->output) pjDumpLine(d, indent, "setting", fcFmt(c, "output=%s", pjDumpPath(d, s->output)));
    PJD("typeChecking", s->typeChecking); PJD("globalJobs", s->globalJobs); PJD("stackTrace", s->stackTrace);
    if (s->arguments) for (i = 0; i < s->arguments->count; i++) pjDumpLine(d, indent, "setting", fcFmt(c, "argument=%s", (const char*)s->arguments->items[i]));
    PJD("port", s->port); PJDI("baud", s->baud);
#undef PJD
#undef PJDI
}
static void pjDumpSrcList(PjDump* d, const char* indent, const char* key, const FgSrcList* l) {
    int i;
    for (i = 0; i < l->files->count; i++) {
        int o = fgSrcOrderOf(l, (const char*)l->files->items[i]);
        pjDumpLine(d, indent, key, o == INT_MIN ? pjDumpPath(d, (const char*)l->files->items[i]) : fcFmt(d->c, "%s order %d", pjDumpPath(d, (const char*)l->files->items[i]), o));
    }
}
static void pjDumpNative(PjDump* d, const char* indent, const PjNativePart* part) {
    FcCtx* c = d->c;
    const PkNative* n = part->native;
    int i;
    pjDumpLine(d, indent, "native", fcFmt(c, "%s %s %s", part->importName, part->projectName, pjDumpPath(d, part->directory)));
    if (n->m.language) pjDumpLine(d, indent, "language", n->m.language);
    pjDumpLine(d, indent, "threadSafe", n->m.threadSafe ? "true" : "false");
    if (n->reset) pjDumpLine(d, indent, "reset", n->reset);
    pjDumpSrcList(d, indent, "source", n->m.sources);
    for (i = 0; i < n->m.platformSources->count; i++) {
        const FgKeyed* k = (const FgKeyed*)n->m.platformSources->items[i];
        pjDumpSrcList(d, indent, fcFmt(c, "platformSource %s", k->key), (const FgSrcList*)k->value);
    }
    for (i = 0; i < n->m.platforms->count; i++) pjDumpLine(d, indent, "platform", (const char*)n->m.platforms->items[i]);
    for (i = 0; i < n->m.linkLibraries->count; i++) {
        const FgKeyed* k = (const FgKeyed*)n->m.linkLibraries->items[i];
        pjDumpLine(d, indent, "linkLibraries", fcFmt(c, "%s %s", k->key, fgJoin(c, (const FcList*)k->value, " ")));
    }
    for (i = 0; i < n->m.functions->count + n->m.bridge->count; i++) {
        const FgPkgFn* fn = (const FgPkgFn*)(i < n->m.functions->count ? n->m.functions->items[i] : n->m.bridge->items[i - n->m.functions->count]);
        pjDumpLine(d, indent, i < n->m.functions->count ? "function" : "bridge", fcFmt(c, "%s %d cpp=%s c=%s needsList=%d returnsReference=%d host=%d returns=%s parameters=%s", fn->name, fn->arguments, fn->cpp,
            fn->cfn ? fn->cfn : "-", fn->needsList, fn->returnsReference, fn->host, fn->returns ? fn->returns : "-", fn->parameters ? fgJoin(c, fn->parameters, ",") : "-"));
    }
    for (i = 0; i < n->m.exceptions->count; i++) pjDumpLine(d, indent, "exception", (const char*)n->m.exceptions->items[i]);
    for (i = 0; i < n->m.uses->count; i++) {
        const FgNativeUse* u = (const FgNativeUse*)n->m.uses->items[i];
        pjDumpLine(d, indent, "uses", fcFmt(c, "%s %s", u->import, fgJoin(c, u->platforms, ",")));
    }
    for (i = 0; i < n->m.headers->count; i++) pjDumpLine(d, indent, "header", pjDumpPath(d, (const char*)n->m.headers->items[i]));
    for (i = 0; i < n->m.units->count; i++) pjDumpLine(d, indent, "unit", pjDumpPath(d, (const char*)n->m.units->items[i]));
}
static void pjDumpSources(PjDump* d, const char* indent, const FcList* sources) {
    int i;
    for (i = 0; i < sources->count; i++) {
        const PjSource* s = (const PjSource*)sources->items[i];
        pjDumpLine(d, indent, "source", fcFmt(d->c, "%s %s", pjDumpPath(d, s->path), fcSha256OfText(d->c, s->text)));
    }
}
static void pjDumpProject(PjDump* d, PjWorkspace* w, PjLoaded* p) {
    FcCtx* c = d->c;
    PjPlan* volatile plan = NULL;
    int i;
    pjDumpLine(d, "", "project", p->project->name);
    pjDumpLine(d, "  ", "file", pjDumpPath(d, p->project->filePath));
    pjDumpLine(d, "  ", "type", p->project->type == PJ_LIBRARY ? "library" : "exe");
    pjDumpLine(d, "  ", "import", pjImportName(c, p->project));
    pjDumpLine(d, "  ", "inSolution", p->inSolution ? "true" : "false");
    for (i = 0; i < p->problems->count; i++) pjDumpLine(d, "  ", "problem", (const char*)p->problems->items[i]);
    for (i = 0; i < p->files->count; i++) pjDumpLine(d, "  ", "compile", pjDumpPath(d, (const char*)p->files->items[i]));
    for (i = 0; i < p->testFiles->count; i++) pjDumpLine(d, "  ", "test", pjDumpPath(d, (const char*)p->testFiles->items[i]));
    for (i = 0; i < p->contentFiles->count; i++) pjDumpLine(d, "  ", "content", pjDumpPath(d, (const char*)p->contentFiles->items[i]));
    for (i = 0; i < p->folderMap->order->count; i++) {
        const PjFolderInfo* f = (const PjFolderInfo*)fcDictGet(p->folderMap->folders, p->folderMap->order->items[i]);
        if (f) pjDumpLine(d, "  ", "folder", fcFmt(c, "%s %s %s %s", f->path, pjFolderStates[f->state], pjFolderReasons[f->reason], f->entry ? f->entry : "-"));
    }
    for (i = 0; i < p->copyFiles->count; i++) {
        const PjCopyItem* item = (const PjCopyItem*)p->copyFiles->items[i];
        pjDumpLine(d, "  ", "copy", fcFmt(c, "%s %s", item->relativePath, item->always ? "always" : "newer"));
    }
    for (i = 0; i < p->ignoredFiles->count; i++) pjDumpLine(d, "  ", "ignored", pjDumpPath(d, (const char*)p->ignoredFiles->items[i]));
    {
        FcList* folders = pjPublishTargetFolders(c, p, w->solution ? w->solution->directory : NULL);
        const PjPublish* pub = p->project->publish;
        for (i = 0; i < folders->count; i++) pjDumpLine(d, "  ", "publishFolder", pjDumpPath(d, (const char*)folders->items[i]));
        if (pub) pjDumpLine(d, "  ", "publish", fcFmt(c, "enabled=%s clean=%d", pub->enabled ? "true" : "false", pub->clean));
    }
    FC_TRY(c) {
        plan = pjPlanCreate(w, p, false);
    } FC_CATCH(c) {
        if (c->err.kind != FC_ERR_PROJECT && c->err.kind != FC_ERR_PACKAGE) fcRethrow(c);
        pjDumpLine(d, "  ", "plan-exception", c->err.message);
    } FC_END_TRY;
    if (!plan) return;
    for (i = 0; i < plan->errors->count; i++) pjDumpLine(d, "  ", "error", (const char*)plan->errors->items[i]);
    pjDumpSources(d, "  ", plan->sources);
    for (i = 0; i < plan->packages->count; i++) {
        const PjReference* r = (const PjReference*)plan->packages->items[i];
        pjDumpLine(d, "  ", "package", fcFmt(c, "%s %s", r->package ? r->package : "-", r->version ? r->version : "-"));
    }
    pjDumpSettings(d, "  ", plan->settings);
    if (plan->native) pjDumpNative(d, "  ", plan->native);
    for (i = 0; i < plan->libraries->count; i++) {
        const PjLibrary* l = (const PjLibrary*)plan->libraries->items[i];
        int k;
        pjDumpLine(d, "  ", "library", fcFmt(c, "%s %s", l->importName, l->project->project->name));
        for (k = 0; k < l->requires->count; k++) pjDumpLine(d, "    ", "requires", (const char*)l->requires->items[k]);
        for (k = 0; k < l->packages->count; k++) pjDumpLine(d, "    ", "package", ((const PjReference*)l->packages->items[k])->package);
        pjDumpSources(d, "    ", l->sources);
        pjDumpSettings(d, "    ", l->settings);
        if (l->native) pjDumpNative(d, "    ", l->native);
    }
    {
        FcList* items = pjContentItemsOf(c, plan);
        FcList* order;
        FcList* names = fcListNew(c);
        for (i = 0; i < items->count; i++) {
            const PjCopyItem* item = (const PjCopyItem*)items->items[i];
            pjDumpLine(d, "  ", "contentItem", fcFmt(c, "%s %s %s", item->relativePath, pjDumpPath(d, item->source), item->always ? "always" : "newer"));
        }
        for (i = 0; i < plan->libraries->count; i++) fcListAdd(c, names, (void*)((PjLibrary*)plan->libraries->items[i])->importName);
        order = pjLibraryOrder(c, plan, names);
        for (i = 0; i < order->count; i++) pjDumpLine(d, "  ", "libraryOrder", ((PjLibrary*)order->items[i])->importName);
    }
    pjDumpLine(d, "  ", "outputFolder", pjDumpPath(d, pjOutputFolder(c, p->project->directory, fgHostTarget()->name, "release")));
    pjDumpLine(d, "  ", "programPath", pjDumpPath(d, pjProgramPath(c, p->project->directory, p->project->name, fgHostTarget()->name, "release", fgHostTarget()->programExtension)));
    if (p->project->type == PJ_LIBRARY && plan->errors->count == 0) {
        FcList* imports = fcListNew(c);
        FcList* keys = fcListNew(c);
        FcList* values = fcListNew(c);
        const char* volatile flat = NULL;
        pjDumpLine(d, "  ", "packagePath", pjDumpPath(d, pjPackagePathFor(c, plan, NULL)));
        pjDumpLine(d, "  ", "packageVersion", pjPackageVersion(c, plan->settings->version));
        FC_TRY(c) {
            flat = pjFlatten(c, plan->sources, imports, keys, values, p->project->directory);
        } FC_CATCH(c) {
            if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
            pjDumpLine(d, "  ", "flatten-exception", c->err.message);
        } FC_END_TRY;
        if (flat) {
            pjDumpLine(d, "  ", "flatten", fcSha256OfText(c, flat));
            for (i = 0; i < imports->count; i++) pjDumpLine(d, "  ", "flattenImport", (const char*)imports->items[i]);
            for (i = 0; i < keys->count; i++) pjDumpLine(d, "  ", "flattenResource", fcFmt(c, "%s %s", (const char*)keys->items[i], pjDumpPath(d, (const char*)values->items[i])));
        }
    }
}
static int pjProjectPlanDump(const char* path, FILE* f) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    FcBuf o = {0};
    PjDump d;
    int i;
    fcCtxInit(c);
    fcBufStr(&o, "");
    d.c = c; d.o = &o; d.root = fcDirectoryName(c, pjFull(c, path, "."));
    FC_TRY(c) {
        PjWorkspace* w = pjWorkspaceOpen(c, path);
        PjLoaded* startup = pjStartup(w);
        FcList* order = pjBuildOrder(w);
        FcList* names = fcListNew(c);
        pjDumpLine(&d, "", "workspace", w->solution ? w->solution->name : w->members->count == 1 ? ((PjLoaded*)w->members->items[0])->project->name : "");
        if (w->solution) {
            pjDumpLine(&d, "", "solution", pjDumpPath(&d, w->solution->filePath));
            for (i = 0; i < w->solution->folders->count; i++) pjDumpLine(&d, "", "solutionFolder", (const char*)w->solution->folders->items[i]);
            pjDumpSettings(&d, "", w->solution->settings);
        }
        for (i = 0; i < w->members->count; i++) pjDumpLine(&d, "", "member", ((PjLoaded*)w->members->items[i])->project->name);
        pjDumpLine(&d, "", "startup", startup ? startup->project->name : "-");
        for (i = 0; i < order->count; i++) fcListAdd(c, names, (void*)((PjLoaded*)order->items[i])->project->name);
        pjDumpLine(&d, "", "buildOrder", fgJoin(c, names, " "));
        for (i = 0; i < w->members->count; i++) pjDumpProject(&d, w, (PjLoaded*)w->members->items[i]);
    } FC_CATCH(c) {
        if (c->err.kind == FC_ERR_PROJECT || c->err.kind == FC_ERR_PACKAGE) pjDumpLine(&d, "", "exception", c->err.message);
        else pjDumpLine(&d, "", "internal", c->err.message ? c->err.message : "?");
    } FC_END_TRY;
    fwrite(o.p, 1, o.len, f);
    fcBufFree(&o);
    fcCtxFree(c);
    return 0;
}

/* ---- `fpk <file.fpk>`: a package as Fpk.ReadManifest reads it, and its entries ---- */
static int pjPackageDump(const char* path, FILE* f) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    FcBuf o = {0};
    int i;
    fcCtxInit(c);
    fcBufStr(&o, "");
    FC_TRY(c) {
        PkManifest* m = pkReadManifest(c, path);
        PkZip zip;
        fcOut8(&o, pkManifestToJson(c, m));
        fcBufChar(&o, '\n');
        if (pkZipOpen(c, path, &zip))
            for (i = 0; i < zip.entries->count; i++) {
                const PkZipEntry* e = (const PkZipEntry*)zip.entries->items[i];
                size_t n = 0;
                uint8_t* data = pkZipRead(&zip, e, &n);
                FcSha256 h;
                fcShaInit(&h);
                if (data) fcShaUpdate(&h, data, n);
                free(data);
                fcBufStr(&o, "entry ");
                fcBufStr(&o, e->name);
                fcBufFmt(&o, " %u %08x %08x %s\n", e->usize, e->crc, e->external, data ? fcShaHex(c, &h) : "unreadable");
            }
        pkZipFree(&zip);
    } FC_CATCH(c) {
        fcBufStr(&o, "exception ");
        fcOut8(&o, c->err.message ? c->err.message : "?");
        fcBufChar(&o, '\n');
    } FC_END_TRY;
    fwrite(o.p, 1, o.len, f);
    fcBufFree(&o);
    fcCtxFree(c);
    return 0;
}

/* `buildplan <options> <project|solution>`: the program of the startup project (nbDumpBuildPlan asks through nbProjectBuildPlan) */
static int pjProjectBuildPlanHook(FcCtx* c, const char* path, NbBuild* b, NbConfig* config, const char** targetName, const char** toolchainName) {
    PjWorkspace* w;
    PjPlan* plan;
    if (!pjIsProjectFile(path)) return 0;
    w = pjWorkspaceOpen(c, path);
    plan = pjPlanOrThrow(w, pjSelectProject(w, NULL));
    if (plan->project->project->type == PJ_LIBRARY) return 2;
    if (!*targetName) *targetName = plan->settings->target;
    if (!*toolchainName) *toolchainName = plan->settings->toolchain;
    pjApplyBuildSettings(c, config, plan);
    b->link = pjNbLink;
    b->overlay = pjNbOverlay;
    b->vmLink = pjNbVmLink;
    b->project = plan;
    b->projectName = plan->project->project->name;
    return 1;
}

/* `fbc <mode> <project|solution>`: the program of the startup project linked for the VM (a library: as ProjectBuilder.Check compiles it) */
static FcLinked* pjLinkProjectFile(FcCtx* c, const char* path, const char* prelude, const char* storeRoot, int mode, bool native) {
    PjWorkspace* w = pjWorkspaceOpen(c, path);
    PjPlan* plan = pjPlanOrThrow(w, pjSelectProject(w, NULL));
    PjLinkIn in;
    FgOptions o;
    memset(&in, 0, sizeof in);
    in.plan = plan; in.prelude = prelude; in.storeRoot = storeRoot; in.modeOverride = mode;
    if (native) {
        /* (`fbc native <project>`: linked for a native build for this machine - the input of the generator) */
        NbConfig* config = nbConfigNew(c);
        memset(&o, 0, sizeof o);
        o.target = fgHostTarget(); o.mode = -1; o.stripping = -1;
        pjApplyBuildSettings(c, config, plan);
        o.optimize = nbOptimizeOf(config, o.target);
        in.o = &o;
    }
    return pjLink(c, &in, NULL);
}
