/* native/forge/project/workspace.c - what is open of projects and what a build of one needs (Phase D 3, commit de45e7e4): src/fire.Project/Workspace.cs
 * (LoadedProject.Refresh, Workspace: Open, LoadProject, FindByName, Startup, ReferencedProjects, BuildOrder - not the changes, which the IDE makes),
 * BuildPlan.cs (SourceFile, LibraryPlan, BuildPlan.Create with TryReadSource and Merge, LibraryOrder), ProjectNatives.cs (Discover, Resolve,
 * ExpandOrdered), ProjectPublish.cs (ProjectContent.ItemsOf, CopyTo, IsSame, CopyFile; PublishSettings.TargetFolders, DefaultTarget, Choices) and from
 * src/fire.Compiler ProjectBuilder.cs (SelectProject, PlanOrThrow, PackageVersion, PackagePathFor, Flatten with Preprocessor.MapResourcePaths). */

typedef struct PjCopyItem { const char* source; const char* relativePath; bool always; } PjCopyItem;
typedef struct PjLoaded {
    PjProject* project; bool inSolution;
    FcList* files; FcList* problems; FcList* testFiles; FcList* contentFiles; FcList* folders; PjFolderMap* folderMap;
    FcList* copyFiles /* PjCopyItem* */; FcList* ignoredFiles; FcDict* fileSet;
} PjLoaded;
typedef struct PjWorkspace { FcCtx* c; FcDict* byPath; FcList* all /* PjLoaded*, the order of byPath */; FcList* members; PjSolution* solution; } PjWorkspace;

static void pjRefresh(FcCtx* c, PjLoaded* l) {
    PjProject* p = l->project;
    FcList* problems = pjProjectValidate(c, p);
    PjFolderMap* map = pjFolderMapScan(c, p);
    FcList* tests;
    int i;
    l->folderMap = map;
    l->files = pjResolveFiles(c, p, problems, false, map);
    l->fileSet = fcDictNew(c, FC_KEY_STR);
    for (i = 0; i < l->files->count; i++) fcSetAdd(l->fileSet, l->files->items[i]);
    tests = pjResolveFiles(c, p, NULL, true, map);
    l->testFiles = fcListNew(c);
    for (i = 0; i < tests->count; i++) if (!fcDictHas(l->fileSet, tests->items[i])) fcListAdd(c, l->testFiles, tests->items[i]);
    l->problems = problems;
    l->contentFiles = pjContentFiles(c, map, l->files);
    l->folders = pjFolders(c, map);
    l->copyFiles = fcListNew(c);
    l->ignoredFiles = fcListNew(c);
    if (p->buildActionKeys && p->buildActionKeys->count > 0)
        for (i = 0; i < l->contentFiles->count; i++) {
            const char* file = (const char*)l->contentFiles->items[i];
            const char* rel = pjRelative(c, p->directory, file);
            int action = pjActionOf(c, p, rel);
            if (pjActionIsCopy(action)) {
                PjCopyItem* item = (PjCopyItem*)fcAlloc(c, sizeof *item);
                item->source = file; item->relativePath = rel; item->always = action == PJ_ACT_COPYALWAYS;
                fcListAdd(c, l->copyFiles, item);
            } else if (action == PJ_ACT_IGNORE) fcListAdd(c, l->ignoredFiles, (void*)file);
        }
}
/* LoadedProject.BuildContentFiles: the content without the files of undecided folders */
static FcList* pjBuildContentFiles(FcCtx* c, const PjLoaded* l) {
    FcList* r = fcListNew(c);
    int i;
    for (i = 0; i < l->contentFiles->count; i++)
        if (pjFolderStateOfFile(c, l->folderMap, pjRelative(c, l->project->directory, (const char*)l->contentFiles->items[i])) != PJ_FS_UNDECIDED) fcListAdd(c, r, l->contentFiles->items[i]);
    return r;
}
static FC_LATER bool pjLoadedContains(FcCtx* c, const PjLoaded* l, const char* path) { return fcDictHas(l->fileSet, pjFull(c, path, ".")); }

static PjLoaded* pjLoadProject(PjWorkspace* w, const char* path, bool inSolution) {
    FcCtx* c = w->c;
    const char* full = pjFull(c, path, ".");
    PjLoaded* l = (PjLoaded*)fcDictGet(w->byPath, full);
    if (l) { if (inSolution) l->inSolution = true; return l; }
    l = (PjLoaded*)fcAlloc(c, sizeof *l);
    l->project = pjProjectLoad(c, full);
    l->inSolution = inSolution;
    pjRefresh(c, l);
    fcDictPut(w->byPath, full, l);
    fcListAdd(c, w->all, l);
    return l;
}
static PjWorkspace* pjWorkspaceOpen(FcCtx* c, const char* path) {
    PjWorkspace* w = (PjWorkspace*)fcAlloc(c, sizeof *w);
    const char* full = pjFull(c, path, ".");
    const char* ext = pjExtension(c, full);
    int i, k;
    w->c = c;
    w->byPath = fcDictNew(c, FC_KEY_STR);
    w->all = fcListNew(c);
    w->members = fcListNew(c);
    if (pjIEq(ext, ".firesln")) {
        w->solution = pjSolutionLoad(c, full);
        for (i = 0; i < w->solution->projects->count; i++) {
            const char* rel = (const char*)w->solution->projects->items[i];
            PjLoaded* p = pjLoadProject(w, pjFull(c, pjReplaceChar(c, rel ? rel : "", '\\', '/'), w->solution->directory), true);
            bool have = false;
            for (k = 0; k < w->members->count; k++) if (w->members->items[k] == p) have = true;
            if (!have) fcListAdd(c, w->members, p);
        }
    } else if (pjIEq(ext, ".fireproj")) fcListAdd(c, w->members, pjLoadProject(w, full, true));
    else pjThrow(c, fcFmt(c, "'%s' is neither a solution (.firesln) nor a project (.fireproj).", pjFileName(full)));
    return w;
}
static PjLoaded* pjFindByName(PjWorkspace* w, const char* name) {
    int i;
    for (i = 0; i < w->members->count; i++) if (pjIEq(((PjLoaded*)w->members->items[i])->project->name, name)) return (PjLoaded*)w->members->items[i];
    return NULL;
}
static PjLoaded* pjStartup(PjWorkspace* w) {
    PjLoaded* named;
    int i;
    if (w->solution && w->solution->startup && (named = pjFindByName(w, w->solution->startup)) != NULL) return named;
    for (i = 0; i < w->members->count; i++) if (((PjLoaded*)w->members->items[i])->project->type == PJ_EXE) return (PjLoaded*)w->members->items[i];
    return w->members->count > 0 ? (PjLoaded*)w->members->items[0] : NULL;
}
/* ReferencedProjects: a reference whose file cannot be read is left out */
static FcList* pjReferencedProjects(PjWorkspace* w, PjLoaded* project) {
    FcCtx* c = w->c;
    FcList* result = fcListNew(c);
    int i;
    for (i = 0; i < project->project->references->count; i++) {
        const PjReference* r = (const PjReference*)project->project->references->items[i];
        const char* path;
        if (!pjRefIsProject(r)) continue;
        path = pjFull(c, pjReplaceChar(c, r->project, '\\', '/'), project->project->directory);
        if (!nbFileExists(path)) continue;
        FC_TRY(c) {
            fcListAdd(c, result, pjLoadProject(w, path, false));
        } FC_CATCH(c) {
            if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
        } FC_END_TRY;
    }
    return result;
}
typedef struct PjOrder { PjWorkspace* w; FcList* ordered; FcDict* done; FcDict* visiting; } PjOrder;
static bool pjIsMember(PjWorkspace* w, PjLoaded* p) { int i; for (i = 0; i < w->members->count; i++) if (w->members->items[i] == p) return true; return false; }
static void pjOrderVisit(PjOrder* o, PjLoaded* p) {
    FcList* deps;
    int i;
    if (fcDictHas(o->done, p) || !fcSetAdd(o->visiting, p)) return;
    deps = pjReferencedProjects(o->w, p);
    for (i = 0; i < deps->count; i++) pjOrderVisit(o, (PjLoaded*)deps->items[i]);
    fcDictRemove(o->visiting, p);
    fcSetAdd(o->done, p);
    if (pjIsMember(o->w, p)) fcListAdd(o->w->c, o->ordered, p);
}
static FcList* pjBuildOrder(PjWorkspace* w) {
    PjOrder o;
    FcList* members = fcListCopy(w->c, w->members);
    int i;
    o.w = w; o.ordered = fcListNew(w->c); o.done = fcDictNew(w->c, FC_KEY_PTR); o.visiting = fcDictNew(w->c, FC_KEY_PTR);
    for (i = 0; i < members->count; i++) pjOrderVisit(&o, (PjLoaded*)members->items[i]);
    return o.ordered;
}

/* ---- ProjectNatives ---- */
typedef struct PjNativePart { const char* importName; const char* projectName; const char* directory; const char* projectFile; PkNative* native; } PjNativePart;
static bool pjIsWordUnit8(uint16_t u) { return fcIsLetterOrDigit(u) || u == '_'; }
/* Discover: `inline Value name(Value a, ...)` at the start of a line, comments taken out first */
static FcList* pjDiscover(FcCtx* c, const char* cppText) {
    FcList* result = fcListNew(c);
    FcBuf b = {0};
    const char* p;
    const char* text;
    size_t n, i;
    fcBufStr(&b, "");
    for (p = cppText; *p;) {
        if (p[0] == '/' && p[1] == '/') { while (*p && *p != '\n') p++; continue; }
        if (p[0] == '/' && p[1] == '*') {
            const char* end = strstr(p + 2, "*/");
            if (end) { p = end + 2; continue; }
        }
        fcBufChar(&b, *p++);
    }
    text = fcBufDone(c, &b);
    n = strlen(text);
    for (i = 0; i < n;) {
        const char* s = text + i;
        const char* q = s;
        const char* nameStart;
        const char* name;
        const char* params;
        const char* close;
        FcList* parts;
        FcList* parameters;
        bool needsList = false, ok = true;
        int arguments, k;
        /* (the start of a line) */
        if (i > 0 && text[i - 1] != '\n') { i++; continue; }
        while (*q == ' ' || *q == '\t') q++;
        if (fcStartsWith(q, "static") && (q[6] == ' ' || q[6] == '\t')) { const char* r = q + 6; while (*r == ' ' || *r == '\t') r++; if (fcStartsWith(r, "inline")) q = r; }
        if (!(fcStartsWith(q, "inline") && (q[6] == ' ' || q[6] == '\t'))) { i++; continue; }
        q += 6; while (*q == ' ' || *q == '\t') q++;
        if (!(fcStartsWith(q, "Value") && (q[5] == ' ' || q[5] == '\t'))) { i++; continue; }
        q += 5; while (*q == ' ' || *q == '\t') q++;
        nameStart = q;
        while (*q) { const unsigned char* u = (const unsigned char*)q; uint16_t ch = fc8Next(&u); if (!pjIsWordUnit8(ch)) break; q = (const char*)u; }
        if (q == nameStart) { i++; continue; }
        name = fcStrN(c, nameStart, (size_t)(q - nameStart));
        while (*q == ' ' || *q == '\t') q++;
        if (*q != '(') { i++; continue; }
        params = q + 1;
        close = strchr(params, ')');
        if (!close) { i++; continue; }
        i = (size_t)(close + 1 - text);
        parts = fgSplit(c, fcStrN(c, params, (size_t)(close - params)), ',');
        parameters = fcListNew(c);
        for (k = 0; k < parts->count; k++) {
            const char* t = pjTrim(c, (const char*)parts->items[k]);
            if (*t && !streq0(t, "void")) fcListAdd(c, parameters, (void*)t);
        }
        if (parameters->count > 0) {
            /* \bOwnList\s*\* in the last one */
            const char* last = (const char*)parameters->items[parameters->count - 1];
            const char* at;
            for (at = strstr(last, "OwnList"); at; at = strstr(at + 1, "OwnList")) {
                const char* r = at + 7;
                bool boundary = at == last || !pjIsWordUnit8((uint16_t)(unsigned char)at[-1]);
                while (*r == ' ' || *r == '\t' || *r == '\n' || *r == '\r' || *r == '\f' || *r == '\v') r++;
                if (boundary && *r == '*') { needsList = true; break; }
            }
        }
        arguments = parameters->count - (needsList ? 1 : 0);
        for (k = 0; k < arguments; k++) {
            const char* t = (const char*)parameters->items[k];
            if (!(fcStartsWith(t, "Value") && !pjIsWordUnit8((uint16_t)(unsigned char)t[5]))) ok = false;
        }
        if (!ok) continue;
        {
            FgPkgFn* f = (FgPkgFn*)fcAlloc(c, sizeof *f);
            f->name = fcFmt(c, "__%s", name); f->arguments = arguments; f->cpp = name; f->needsList = needsList; f->returnsReference = needsList;
            fcListAdd(c, result, f);
        }
    }
    return result;
}
static int pjHeaderRank(FcCtx* c, const char* path) {
    const char* ext = pjExtension(c, path);
    return pjIEq(ext, ".h") || pjIEq(ext, ".hpp") || pjIEq(ext, ".hh") || pjIEq(ext, ".hxx") ? 0 : 1;
}
static FgSrcList* pjExpandOrdered(FcCtx* c, const char* dir, const FgSrcList* entries, FcList* problems, PjRank rank) {
    FcList* from = fcListNew(c);
    FcList* files = pjExpand(c, dir, entries->files, problems, rank, from);
    FgSrcList* result = pkSrcListNew(c);
    int i;
    for (i = 0; i < files->count; i++) pkSrcAdd(c, result, (const char*)files->items[i], fgSrcOrderOf(entries, (const char*)entries->files->items[FC_VALI(from->items[i])]));
    return result;
}
static PjNativePart* pjNativesResolve(FcCtx* c, const PjLoaded* project, FcList* problems) {
    const PjNative* n = project->project->native;
    const char* dir = project->project->directory;
    FcList* found = fcListNew(c);
    PkNative* native;
    FgSrcList* defaults;
    FcDict* explicitCpp = fcDictNew(c, FC_KEY_STR);
    FcList* files = fcListNew(c);
    PjNativePart* part;
    int i, k;
    if (!n) return NULL;
    native = pkNativeNew(c);
    native->reset = n->reset;
    native->m.language = n->language;
    native->m.isC = n->language && streq0(n->language, "c");
    native->m.threadSafe = n->threadSafe == 1;
    defaults = pkSrcListNew(c);
    pkSrcAdd(c, defaults, "native/**/*.{h,hpp,hh,c,cc,cpp,cxx}", INT_MIN);
    native->m.sources = pjExpandOrdered(c, dir, n->sources ? n->sources : defaults, found, n->sources ? NULL : pjHeaderRank);
    if (n->platformSources)
        for (i = 0; i < n->platformSources->count; i++) {
            FgKeyed* from = (FgKeyed*)n->platformSources->items[i];
            FgKeyed* to = (FgKeyed*)fcAlloc(c, sizeof *to);
            to->key = from->key;
            to->value = pjExpandOrdered(c, dir, (const FgSrcList*)from->value, found, NULL);
            fcListAdd(c, native->m.platformSources, to);
        }
    if (n->platforms) for (i = 0; i < n->platforms->count; i++) fcListAdd(c, native->m.platforms, n->platforms->items[i]);
    if (n->linkLibraries) for (i = 0; i < n->linkLibraries->count; i++) fcListAdd(c, native->m.linkLibraries, n->linkLibraries->items[i]);
    if (n->exceptions) for (i = 0; i < n->exceptions->count; i++) fcListAdd(c, native->m.exceptions, n->exceptions->items[i]);
    if (n->bridge) for (i = 0; i < n->bridge->count; i++) fcListAdd(c, native->m.bridge, n->bridge->items[i]);
    if (n->uses) for (i = 0; i < n->uses->count; i++) fcListAdd(c, native->m.uses, n->uses->items[i]);
    if (n->headers) { FgSrcList* h = pjExpandOrdered(c, dir, n->headers, found, NULL); for (i = 0; i < h->files->count; i++) fcListAdd(c, native->m.headers, h->files->items[i]); }
    for (i = 0; i < found->count; i++) if (problems) fcListAdd(c, problems, (void*)fcFmt(c, "%s: native: %s", project->project->name, (const char*)found->items[i]));
    if (native->m.sources->files->count == 0 && native->m.platformSources->count == 0 && problems)
        fcListAdd(c, problems, (void*)fcFmt(c, "%s: the project has a native part but no C++ files (put them into the folder 'native').", project->project->name));
    if (n->functions) for (i = 0; i < n->functions->count; i++) { fcSetAdd(explicitCpp, fgPkgFnSymbol((FgPkgFn*)n->functions->items[i])); fcListAdd(c, native->m.functions, n->functions->items[i]); }
    if (n->discover != 0) {
        for (i = 0; i < native->m.sources->files->count; i++) fcListAdd(c, files, native->m.sources->files->items[i]);
        for (i = 0; i < native->m.platformSources->count; i++) { FgSrcList* l = (FgSrcList*)((FgKeyed*)native->m.platformSources->items[i])->value; for (k = 0; k < l->files->count; k++) fcListAdd(c, files, l->files->items[k]); }
    }
    for (i = 0; i < files->count; i++) {
        const char* file = (const char*)files->items[i];
        const char* text = pjReadAllText(c, file);
        FcList* fns;
        if (!text) { if (problems) fcListAdd(c, problems, (void*)fcFmt(c, "%s: cannot read '%s': Could not find file '%s'.", project->project->name, file, file)); continue; }
        fns = pjDiscover(c, text);
        for (k = 0; k < fns->count; k++) {
            FgPkgFn* f = (FgPkgFn*)fns->items[k];
            int m;
            bool have = false;
            if (native->m.isC) { f->cfn = f->cpp; f->cpp = ""; }
            for (m = 0; m < native->m.functions->count; m++) if (streq0(((FgPkgFn*)native->m.functions->items[m])->name, f->name)) have = true;
            if (!fcDictHas(explicitCpp, fgPkgFnSymbol(f)) && !have) fcListAdd(c, native->m.functions, f);
        }
    }
    part = (PjNativePart*)fcAlloc(c, sizeof *part);
    part->importName = pjImportName(c, project->project);
    part->projectName = project->project->name;
    part->directory = dir;
    part->projectFile = project->project->filePath;
    part->native = native;
    return part;
}

/* ---- BuildPlan ---- */
typedef struct PjSource { const char* path; const char* text; const char* projectName; } PjSource;
typedef struct PjLibrary { const char* importName; PjLoaded* project; FcList* sources /* PjSource* */; FcList* requires; FcList* packages /* PjReference* */; PjSettings* settings; PjNativePart* native; } PjLibrary;
typedef struct PjPlan { PjLoaded* project; FcList* sources; FcList* libraries /* PjLibrary*, the order of the dictionary */; FcList* packages; PjSettings* settings; FcList* errors; PjNativePart* native;
    /* (Phase D 4, tools/commands.c) no project: the sources of the command line - 1 files (Linker.SourcePaths: each named by its file name, from its folder),
     * 2 texts (no SourcePaths: unnamed, from baseFolder - BasePath -, else the current folder); 0: a project */
    int files; const char* baseFolder; } PjPlan;
static PjLibrary* pjPlanLibrary(const PjPlan* plan, const char* name) {
    int i;
    for (i = 0; i < plan->libraries->count; i++) if (pjIEq(((PjLibrary*)plan->libraries->items[i])->importName, name)) return (PjLibrary*)plan->libraries->items[i];
    return NULL;
}
/* NativeParts: the project's, then the libraries' */
static FcList* pjNativeParts(FcCtx* c, const PjPlan* plan) {
    FcList* r = fcListNew(c);
    int i;
    if (plan->native) fcListAdd(c, r, plan->native);
    for (i = 0; i < plan->libraries->count; i++) if (((PjLibrary*)plan->libraries->items[i])->native) fcListAdd(c, r, ((PjLibrary*)plan->libraries->items[i])->native);
    return r;
}
/* LibraryOrder: the libraries named and what they need, the needed ones first */
static void pjLibraryVisit(const PjPlan* plan, const char* name, FcList* ordered, FcDict* visited, FcCtx* c) {
    PjLibrary* lib = pjPlanLibrary(plan, name);
    int i;
    if (!lib || !fcSetAdd(visited, pjLower(c, lib->importName))) return;
    for (i = 0; i < lib->requires->count; i++) pjLibraryVisit(plan, (const char*)lib->requires->items[i], ordered, visited, c);
    fcListAdd(c, ordered, lib);
}
static FcList* pjLibraryOrder(FcCtx* c, const PjPlan* plan, const FcList* names) {
    FcList* ordered = fcListNew(c);
    FcDict* visited = fcDictNew(c, FC_KEY_STR);
    int i;
    for (i = 0; i < names->count; i++) pjLibraryVisit(plan, (const char*)names->items[i], ordered, visited, c);
    return ordered;
}
static PjSettings* pjMergeSettings(FcCtx* c, const PjLoaded* p, const PjWorkspace* w) {
    PjSettings* own = pjSettingsAbsolute(c, p->project->settings, p->project->directory);
    PjSettings* shared = w->solution && w->solution->settings ? pjSettingsAbsolute(c, w->solution->settings, w->solution->directory) : NULL;
    return pjSettingsMerge(c, own, shared);
}
/* TryReadSource: the text (a markup file: the script generated from it) */
static PjSource* pjTryReadSource(FcCtx* c, const char* path, const char* projectName, FcList* errors, const char* prefix) {
    FcU16 t = {0};
    PjSource* volatile s = NULL;
    if (!nbFileExists(path) || !fcReadText(path, &t)) {
        fcListAdd(c, errors, (void*)fcFmt(c, "%sCannot read '%s': Could not find file '%s'.", prefix, path, path));
        return NULL;
    }
    FC_TRY(c) {
        PjSource* x = (PjSource*)fcAlloc(c, sizeof *x);
        x->path = path;
        x->text = pjIsMarkupFile(path) ? fmGenerateScript(c, path, t.p, t.len) : fc8FromU16(c, t.p, t.len);
        x->projectName = projectName;
        s = x;
    } FC_CATCH(c) {
        if (c->err.kind != FC_ERR_MARKUP) { fcU16Free(&t); fcRethrow(c); }
        fcListAdd(c, errors, (void*)fcFmt(c, "%s%s: %s", prefix, pjFileName(path), c->err.message));
    } FC_END_TRY;
    fcU16Free(&t);
    return s;
}
typedef struct PjPlanning { PjWorkspace* w; FcList* errors; FcList* libraries; FcDict* byProjectPath; FcList* stack; } PjPlanning;
static PjLoaded* pjReferenced(PjPlanning* x, PjLoaded* from, const PjReference* reference) {
    FcCtx* c = x->w->c;
    const char* path = pjFull(c, pjReplaceChar(c, reference->project, '\\', '/'), from->project->directory);
    PjLoaded* volatile target = NULL;
    int i;
    if (!nbFileExists(path)) { fcListAdd(c, x->errors, (void*)fcFmt(c, "%s: the referenced project '%s' does not exist.", from->project->name, reference->project)); return NULL; }
    FC_TRY(c) {
        target = pjLoadProject(x->w, path, false);
    } FC_CATCH(c) {
        if (c->err.kind != FC_ERR_PROJECT) fcRethrow(c);
        fcListAdd(c, x->errors, (void*)fcFmt(c, "%s: %s", from->project->name, c->err.message));
    } FC_END_TRY;
    if (!target) return NULL;
    for (i = 0; i < x->stack->count; i++)
        if (streq0(((PjLoaded*)x->stack->items[i])->project->filePath, target->project->filePath)) {
            FcList* names = fcListNew(c);
            int k;
            for (k = 0; k < x->stack->count; k++) fcListAdd(c, names, (void*)((PjLoaded*)x->stack->items[k])->project->name);
            fcListAdd(c, names, (void*)target->project->name);
            fcListAdd(c, x->errors, (void*)fcFmt(c, "The projects reference each other in a circle: %s.", fgJoin(c, names, " -> ")));
            return NULL;
        }
    if (target->project->type != PJ_LIBRARY) {
        fcListAdd(c, x->errors, (void*)fcFmt(c, "%s: '%s' is a program, only a library can be referenced (set \"type\": \"library\" in it).", from->project->name, target->project->name));
        return NULL;
    }
    return target;
}
static PjLibrary* pjPlanOf(PjPlanning* x, PjLoaded* lib) {
    FcCtx* c = x->w->c;
    PjLibrary* plan = (PjLibrary*)fcDictGet(x->byProjectPath, lib->project->filePath);
    PjLibrary* clash = NULL;
    int i;
    if (plan) return plan;
    pjRefresh(c, lib);
    for (i = 0; i < lib->problems->count; i++) fcListAdd(c, x->errors, (void*)fcFmt(c, "%s: %s", lib->project->name, (const char*)lib->problems->items[i]));
    plan = (PjLibrary*)fcAlloc(c, sizeof *plan);
    plan->requires = fcListNew(c);
    plan->packages = fcListNew(c);
    fcListAdd(c, x->stack, lib);
    for (i = 0; i < lib->project->references->count; i++) {
        const PjReference* r = (const PjReference*)lib->project->references->items[i];
        PjLoaded* sub;
        PjLibrary* subPlan;
        if (!pjRefIsProject(r)) { fcListAdd(c, plan->packages, (void*)r); continue; }
        if ((sub = pjReferenced(x, lib, r)) != NULL && (subPlan = pjPlanOf(x, sub)) != NULL) fcListAdd(c, plan->requires, (void*)subPlan->importName);
    }
    fcListRemoveAt(x->stack, x->stack->count - 1);
    plan->sources = fcListNew(c);
    for (i = 0; i < lib->files->count; i++) {
        PjSource* s = pjTryReadSource(c, (const char*)lib->files->items[i], lib->project->name, x->errors, fcFmt(c, "%s: ", lib->project->name));
        if (s) fcListAdd(c, plan->sources, s);
    }
    plan->importName = pjImportName(c, lib->project);
    plan->project = lib;
    plan->settings = pjMergeSettings(c, lib, x->w);
    plan->native = pjNativesResolve(c, lib, x->errors);
    fcDictPut(x->byProjectPath, lib->project->filePath, plan);
    for (i = 0; i < x->libraries->count; i++) if (pjIEq(((PjLibrary*)x->libraries->items[i])->importName, plan->importName)) clash = (PjLibrary*)x->libraries->items[i];
    if (clash && clash->project != lib)
        fcListAdd(c, x->errors, (void*)fcFmt(c, "The libraries '%s' and '%s' have the same import name '%s': set 'import' in one of them.", clash->project->project->name, lib->project->name, plan->importName));
    else if (clash) { for (i = 0; i < x->libraries->count; i++) if (x->libraries->items[i] == clash) x->libraries->items[i] = plan; }
    else fcListAdd(c, x->libraries, plan);
    return plan;
}
static PjPlan* pjPlanCreate(PjWorkspace* w, PjLoaded* project, bool tests) {
    FcCtx* c = w->c;
    PjPlanning x;
    PjPlan* plan = (PjPlan*)fcAlloc(c, sizeof *plan);
    FcList* files = fcListNew(c);
    int i;
    x.w = w; x.errors = fcListNew(c); x.libraries = fcListNew(c); x.byProjectPath = fcDictNew(c, FC_KEY_STR); x.stack = fcListNew(c);
    fcListAdd(c, x.stack, project);
    pjRefresh(c, project);
    for (i = 0; i < project->problems->count; i++) fcListAdd(c, x.errors, project->problems->items[i]);
    plan->packages = fcListNew(c);
    for (i = 0; i < project->project->references->count; i++) {
        const PjReference* r = (const PjReference*)project->project->references->items[i];
        PjLoaded* lib;
        if (!pjRefIsProject(r)) { fcListAdd(c, plan->packages, (void*)r); continue; }
        if ((lib = pjReferenced(&x, project, r)) != NULL) pjPlanOf(&x, lib);
    }
    plan->sources = fcListNew(c);
    for (i = 0; i < project->files->count; i++) fcListAdd(c, files, project->files->items[i]);
    if (tests) for (i = 0; i < project->testFiles->count; i++) fcListAdd(c, files, project->testFiles->items[i]);
    for (i = 0; i < files->count; i++) {
        PjSource* s = pjTryReadSource(c, (const char*)files->items[i], project->project->name, x.errors, "");
        if (s) fcListAdd(c, plan->sources, s);
    }
    if (plan->sources->count == 0 && project->project->type == PJ_EXE && x.errors->count == 0) fcListAdd(c, x.errors, (void*)fcFmt(c, "The project '%s' has no files.", project->project->name));
    plan->project = project;
    plan->libraries = x.libraries;
    plan->settings = pjMergeSettings(c, project, w);
    plan->errors = x.errors;
    plan->native = pjNativesResolve(c, project, x.errors);
    return plan;
}

/* ---- ProjectBuilder (the parts without the linker) ---- */
static PjLoaded* pjSelectProject(PjWorkspace* w, const char* name) {
    FcCtx* c = w->c;
    PjLoaded* p;
    int i;
    if (name) {
        FcList* names = fcListNew(c);
        if ((p = pjFindByName(w, name)) != NULL) return p;
        for (i = 0; i < w->all->count; i++) if (pjIEq(((PjLoaded*)w->all->items[i])->project->name, name)) return (PjLoaded*)w->all->items[i];
        for (i = 0; i < w->members->count; i++) fcListAdd(c, names, (void*)((PjLoaded*)w->members->items[i])->project->name);
        pjThrow(c, fcFmt(c, "There is no project '%s' (projects: %s).", name, fgJoin(c, names, ", ")));
    }
    if (!(p = pjStartup(w))) pjThrow(c, "The workspace has no project.");
    return p;
}
static PjPlan* pjPlanOrThrow(PjWorkspace* w, PjLoaded* project) {
    PjPlan* plan = pjPlanCreate(w, project, false);
    if (plan->errors->count > 0) pjThrow(w->c, fgJoin(w->c, plan->errors, "\n"));
    return plan;
}
static bool pjAllDigits(const char* s) { if (!*s) return false; for (; *s; s++) if (!isdigit((unsigned char)*s)) return false; return true; }
static const char* pjPackageVersion(FcCtx* c, const char* version) {
    FcList* parts;
    if (!version) return "1.0.0";
    parts = fgSplit(c, version, '.');
    if (parts->count >= 3 && pjAllDigits((const char*)parts->items[0]) && pjAllDigits((const char*)parts->items[1]) && pjAllDigits((const char*)parts->items[2]))
        return fcFmt(c, "%s.%s.%s", (const char*)parts->items[0], (const char*)parts->items[1], (const char*)parts->items[2]);
    if (parts->count == 2 && pjAllDigits((const char*)parts->items[0]) && pjAllDigits((const char*)parts->items[1])) return fcFmt(c, "%s.0", version);
    if (parts->count == 1 && pjAllDigits((const char*)parts->items[0])) return fcFmt(c, "%s.0.0", version);
    return "1.0.0";
}
static const char* pjPackagePathFor(FcCtx* c, const PjPlan* plan, const char* outputDirectory) {
    const char* dir = pjFull(c, outputDirectory ? outputDirectory : plan->settings->output ? plan->settings->output : nbCombine(c, plan->project->project->directory, "bin"), ".");
    return nbCombine(c, dir, fcFmt(c, "%s-%s.fpk", plan->project->project->name, pjPackageVersion(c, plan->settings->version)));
}

/* Preprocessor.MapResourcePaths: the path of every `new Resource("path")` in the code of the line by what `map` makes of it */
typedef const char* (*PjMapResource)(void* state, const char* written);
static const char* pjMapResourcePaths(FcCtx* c, const char* line, PjMapResource map, void* state) {
    FcU16 u = {0}, out = {0};
    const uint16_t* p;
    int n, i = 0, copied = 0;
    const char* r;
    static const uint16_t kResource[] = { 'R', 'e', 's', 'o', 'u', 'r', 'c', 'e' };
    if (!strstr(line, "Resource")) return line;
    fcU16FromFc8(line, &u);
    p = u.p; n = u.len;
    while (i + 3 <= n) {
        int j, strStart, strEnd;
        if (!(p[i] == 'n' && p[i + 1] == 'e' && p[i + 2] == 'w' && (i == 0 || !fcIsWordUnit(p[i - 1])))) { i++; continue; }
        j = i + 3;
        if (j >= n || !fcIsRegexSpace(p[j])) { i++; continue; }
        while (j < n && fcIsRegexSpace(p[j])) j++;
        if (j + 8 > n || memcmp(p + j, kResource, sizeof kResource) != 0) { i++; continue; }
        j += 8;
        while (j < n && fcIsRegexSpace(p[j])) j++;
        if (j >= n || p[j] != '(') { i++; continue; }
        j++;
        while (j < n && fcIsRegexSpace(p[j])) j++;
        if (j >= n || p[j] != '"') { i++; continue; }
        strStart = ++j;
        while (j < n && p[j] != '"') { if (p[j] == '\\') { if (j + 1 >= n) break; j += 2; } else j++; }
        if (j >= n || p[j] != '"') { i++; continue; }
        strEnd = j++;
        while (j < n && fcIsRegexSpace(p[j])) j++;
        if (j >= n || p[j] != ')') { i++; continue; }
        j++;
        if (fcIsCodeAt(p, n, i)) {
            FcU16 path = {0};
            const char* mapped;
            const unsigned char* q;
            int k;
            for (k = strStart; k < strEnd; k++) {
                uint16_t ch = p[k];
                if (ch == '\\' && k + 1 < strEnd) { uint16_t e = p[++k]; fcU16Unit(&path, e == 'n' ? '\n' : e == 't' ? '\t' : e == 'r' ? '\r' : e); }
                else fcU16Unit(&path, ch);
            }
            mapped = map(state, fc8FromU16(c, path.p, path.len));
            fcU16Free(&path);
            fcU16Put(&out, p + copied, i - copied);
            for (q = (const unsigned char*)"new Resource(\""; *q; q++) fcU16Unit(&out, *q);
            for (q = (const unsigned char*)mapped; *q;) {
                uint16_t ch = fc8Next(&q);
                if (ch == '\\' || ch == '"') fcU16Unit(&out, '\\');
                fcU16Unit(&out, ch);
            }
            fcU16Unit(&out, '"'); fcU16Unit(&out, ')');
            copied = j;
        }
        i = j;
    }
    fcU16Put(&out, p + copied, n - copied);
    r = fc8FromU16(c, out.p, out.len);
    fcU16Free(&u);
    fcU16Free(&out);
    return r;
}
/* ProjectBuilder.Flatten */
typedef struct PjFlat { FcCtx* c; FcBuf sb; FcDict* done; FcList* imports; FcList* resourceKeys; FcList* resourceValues; const char* baseDirectory; const char* file; } PjFlat;
static bool pjLineDirective(const char* line, const char* word, const char** arg, size_t* argLen) {   /* ^[ \t]*#word[ \t]+"([^"\r\n]+)" */
    const char* p = line;
    const char* s;
    while (*p == ' ' || *p == '\t') p++;
    if (*p != '#' || !fcStartsWith(p + 1, word)) return false;
    p += 1 + strlen(word);
    if (*p != ' ' && *p != '\t') return false;
    while (*p == ' ' || *p == '\t') p++;
    if (*p != '"') return false;
    s = ++p;
    while (*p && *p != '"' && *p != '\r' && *p != '\n') p++;
    if (*p != '"' || p == s) return false;
    *arg = s;
    *argLen = (size_t)(p - s);
    return true;
}
static const char* pjResourceKey(void* state, const char* written) {
    PjFlat* f = (PjFlat*)state;
    FcCtx* c = f->c;
    const char* full = pjFull(c, written, fcDirectoryName(c, pjFull(c, f->file, ".")));
    const char* root;
    const char* rel;
    const char* key;
    int k, i;
    if (!nbFileExists(full)) pjThrow(c, fcFmt(c, "%s: the resource '%s' ('%s') does not exist.", pjFileName(f->file), written, full));
    root = pjFull(c, f->baseDirectory ? f->baseDirectory : fcDirectoryName(c, pjFull(c, f->file, ".")), ".");
    rel = pjRelative(c, root, full);
    if (fcStartsWith(rel, "../") || rel[0] == '/') rel = fcFmt(c, "external/%s", pjFileName(full));
    key = rel;
    for (k = 2;; k++) {
        int at = -1;
        for (i = 0; i < f->resourceKeys->count; i++) if (streq0((const char*)f->resourceKeys->items[i], key)) at = i;
        if (at < 0 || streq0((const char*)f->resourceValues->items[at], full)) break;
        { const char* slash = strrchr(rel, '/'); key = fcFmt(c, "%s%d_%s", slash ? fcStrN(c, rel, (size_t)(slash - rel + 1)) : "", k, pjFileName(full)); }
    }
    for (i = 0; i < f->resourceKeys->count; i++) if (streq0((const char*)f->resourceKeys->items[i], key)) { f->resourceValues->items[i] = (void*)full; return key; }
    fcListAdd(c, f->resourceKeys, (void*)key);
    fcListAdd(c, f->resourceValues, (void*)full);
    return key;
}
static void pjFlatAdd(PjFlat* f, const char* path, const char* text, FcList* chain) {
    FcCtx* c = f->c;
    FcList* lines;
    int i, k;
    if (!fcSetAdd(f->done, pjFull(c, path, "."))) return;
    fcBufStr(&f->sb, "// ---- ");
    fcBufStr(&f->sb, pjFileName(path));
    fcBufChar(&f->sb, '\n');
    lines = fgSplit(c, fgReplace(c, text, "\r\n", "\n"), '\n');
    for (i = 0; i < lines->count; i++) {
        const char* line = (const char*)lines->items[i];
        const char* arg;
        size_t argLen;
        if (f->imports && pjLineDirective(line, "import", &arg, &argLen)) {
            const char* name = fcStrN(c, arg, argLen);
            if (!pjListHasIgnoreCase(f->imports, name)) fcListAdd(c, f->imports, (void*)name);
            fcBufStr(&f->sb, "// ");
            fcBufStr(&f->sb, pjTrim(c, line));
            fcBufChar(&f->sb, '\n');
            continue;
        }
        if (!pjLineDirective(line, "include", &arg, &argLen)) {
            if (f->resourceKeys) { const char* saved = f->file; f->file = path; line = pjMapResourcePaths(c, line, pjResourceKey, f); f->file = saved; }
            fcBufStr(&f->sb, line);
            fcBufChar(&f->sb, '\n');
            continue;
        }
        {
            const char* written = fcStrN(c, arg, argLen);
            const char* target = pjFull(c, written, fcDirectoryName(c, pjFull(c, path, ".")));
            FcU16 t = {0};
            const char* included;
            FcList* next;
            for (k = 0; k < chain->count; k++) if (streq0((const char*)chain->items[k], target)) pjThrow(c, fcFmt(c, "Circular include of '%s'.", target));
            if (!nbFileExists(target) || !fcReadText(target, &t)) pjThrow(c, fcFmt(c, "%s: '%s' could not be read: Could not find file '%s'.", pjFileName(path), written, target));
            FC_TRY(c) {
                included = pjIsMarkupFile(target) ? fmGenerateScript(c, target, t.p, t.len) : fc8FromU16(c, t.p, t.len);
            } FC_CATCH(c) {
                fcU16Free(&t);
                if (c->err.kind == FC_ERR_MARKUP) pjThrow(c, fcFmt(c, "%s: %s", pjFileName(target), c->err.message));
                fcRethrow(c);
            } FC_END_TRY;
            fcU16Free(&t);
            next = fcListCopy(c, chain);
            fcListAdd(c, next, (void*)target);
            pjFlatAdd(f, target, included, next);
        }
    }
}
static const char* pjFlatten(FcCtx* c, const FcList* files, FcList* imports, FcList* resourceKeys, FcList* resourceValues, const char* baseDirectory) {
    PjFlat f;
    int i;
    memset(&f, 0, sizeof f);
    f.c = c; f.done = fcDictNew(c, FC_KEY_STR); f.imports = imports; f.resourceKeys = resourceKeys; f.resourceValues = resourceValues; f.baseDirectory = baseDirectory;
    fcBufStr(&f.sb, "");
    for (i = 0; i < files->count; i++) {
        const PjSource* s = (const PjSource*)files->items[i];
        FcList* chain = fcListNew(c);
        fcListAdd(c, chain, (void*)pjFull(c, s->path, "."));
        pjFlatAdd(&f, s->path, s->text, chain);
    }
    return fcBufDone(c, &f.sb);
}

/* ---- ProjectContent and PublishSettings ---- */
static int pjCmpCopyItem(const void* a, const void* b) { return strcmp((*(const PjCopyItem* const*)a)->relativePath, (*(const PjCopyItem* const*)b)->relativePath); }
static FcList* pjContentItemsOf(FcCtx* c, const PjPlan* plan) {
    FcDict* items = fcDictNew(c, FC_KEY_STR);
    FcList* r = fcListNew(c);
    int i, k;
    for (i = 0; i < plan->libraries->count; i++) {
        PjLoaded* lib = ((PjLibrary*)plan->libraries->items[i])->project;
        for (k = 0; k < lib->copyFiles->count; k++) fcDictPut(items, ((PjCopyItem*)lib->copyFiles->items[k])->relativePath, lib->copyFiles->items[k]);
    }
    for (k = 0; k < plan->project->copyFiles->count; k++) fcDictPut(items, ((PjCopyItem*)plan->project->copyFiles->items[k])->relativePath, plan->project->copyFiles->items[k]);
    FC_DICT_EACH(items, e) fcListAdd(c, r, e->value);
    pjStableSort(r, pjCmpCopyItem);
    return r;
}
static bool pjFileTime(const char* path, struct stat* st) { return stat(path, st) == 0 && S_ISREG(st->st_mode); }
static int64_t pjMtimeTicks(const struct stat* st) {   /* LastWriteTimeUtc in ticks (100 ns) since 0001-01-01 */
#if defined(__APPLE__)
    return (int64_t)st->st_mtimespec.tv_sec * 10000000 + st->st_mtimespec.tv_nsec / 100 + 621355968000000000LL;
#else
    return (int64_t)st->st_mtim.tv_sec * 10000000 + st->st_mtim.tv_nsec / 100 + 621355968000000000LL;
#endif
}
static bool pjIsSameCopy(const char* source, const char* destination) {
    struct stat a, b;
    if (!pjFileTime(destination, &b) || !pjFileTime(source, &a)) return false;
    return b.st_size == a.st_size && pjMtimeTicks(&b) >= pjMtimeTicks(&a);
}
/* CopyFile: next to the place first, then moved there; the time and the mode of the original */
static bool pjCopyFile(FcCtx* c, const char* source, const char* destination, const char** problem) {
    const char* temp = fcFmt(c, "%s.%.8s.tmp", destination, nbGuid(c));
    struct stat st;
    struct timespec times[2];
    nbMkdirs(c, fcDirectoryName(c, destination));
    if (!nbCopyFile(c, source, temp)) { *problem = fcFmt(c, "Could not copy '%s'.", source); unlink(temp); return false; }
    if (stat(source, &st) == 0) {
#if defined(__APPLE__)
        times[1] = st.st_mtimespec;
#else
        times[1] = st.st_mtim;
#endif
        times[1].tv_nsec = times[1].tv_nsec / 100 * 100;   /* (.NET sets the time in ticks; only the time of the last write) */
        times[0].tv_sec = 0;
        times[0].tv_nsec = UTIME_OMIT;
        utimensat(AT_FDCWD, temp, times, 0);
        chmod(temp, st.st_mode & 07777);
    }
    if (rename(temp, destination) != 0) { *problem = fcFmt(c, "Could not move '%s'.", temp); unlink(temp); return false; }
    return true;
}
typedef struct PjCopyResult { FcList* copied; int upToDate; FcList* problems; } PjCopyResult;
static PjCopyResult pjContentCopyTo(FcCtx* c, const FcList* items, const char* folder) {
    PjCopyResult r;
    int i;
    r.copied = fcListNew(c); r.problems = fcListNew(c); r.upToDate = 0;
    for (i = 0; i < items->count; i++) {
        const PjCopyItem* item = (const PjCopyItem*)items->items[i];
        const char* destination = pjFull(c, item->relativePath, folder);
        const char* problem = NULL;
        if (!item->always && pjIsSameCopy(item->source, destination)) { r.upToDate++; continue; }
        if (pjCopyFile(c, item->source, destination, &problem)) fcListAdd(c, r.copied, (void*)destination);
        else fcListAdd(c, r.problems, (void*)fcFmt(c, "%s: %s", item->relativePath, problem));
    }
    return r;
}
static const char* pjDefaultPublishTarget(FcCtx* c, const PjLoaded* project, const char* solutionDirectory) {
    const char* root = solutionDirectory ? solutionDirectory : fcDirectoryName(c, pjTrimEndSep(c, project->project->directory));
    return pjFull(c, nbCombine(c, nbCombine(c, root, "publish"), pjReplaceChar(c, project->project->name, '/', '_')), ".");
}
/* Environment.ExpandEnvironmentVariables: %NAME% of a variable that is set */
static const char* pjExpandEnv(FcCtx* c, const char* s) {
    FcBuf b = {0};
    const char* p = s;
    fcBufStr(&b, "");
    while (*p) {
        const char* end;
        if (*p == '%' && (end = strchr(p + 1, '%')) != NULL) {
            const char* name = fcStrN(c, p + 1, (size_t)(end - p - 1));
            const char* value = *name ? getenv(name) : NULL;
            if (value) { fcBufStr(&b, value); p = end + 1; continue; }
            fcBufStr(&b, fcStrN(c, p, (size_t)(end - p)));
            p = end;
            continue;
        }
        fcBufChar(&b, *p++);
    }
    return fcBufDone(c, &b);
}
static FcList* pjPublishTargetFolders(FcCtx* c, const PjLoaded* project, const char* solutionDirectory) {
    const char* root = solutionDirectory ? solutionDirectory : project->project->directory;
    const PjPublish* publish = project->project->publish;
    FcList* r = fcListNew(c);
    int i;
    if (publish && publish->targets)
        for (i = 0; i < publish->targets->count; i++) {
            const char* t = (const char*)publish->targets->items[i];
            const char* full;
            if (pjIsWhite(t)) continue;
            full = pjFull(c, pjExpandEnv(c, pjTrim(c, t)), root);
            if (!fcListHasStr(r, full)) fcListAdd(c, r, (void*)full);
        }
    if (r->count == 0) fcListAdd(c, r, (void*)pjDefaultPublishTarget(c, project, solutionDirectory));
    return r;
}
