/* native/forge/project/linkplan.c - the linker with a project (Phase D 3, commit de45e7e4): Linker.CompileAndLink of src/fire.Compiler/Linker.cs with a
 * BuildPlan - the symbols of `#if` with the project's defines, the project's settings before the tags of the sources (ApplySettings, the widths, the
 * type checking), the files of the project named relative to it, `#import` of the libraries of the project (src/fire.Compiler/ProjectLibraries.cs:
 * TryImport, Process, NamespaceOf, UnknownHint, ImportKeys), the C++ natives of the projects in the overlay of the store (ProjectNativeOverlay.cs), a
 * library as its own import, the check of the package references and of a library without an entry point - for the engine `vm` (ProjectBuilder.Check:
 * no target) and for a native build for a target (NativeBuilder.Build). The single files keep their own linker (linker.c, gen/native.c): this one is
 * the same steps with the plan, written once more instead of changing those (their measures stay as they are). */

typedef struct PjLinker {
    FcLinker L;                 /* (first: the directives of linker.c take it) */
    PjPlan* plan;
    FcList* projectImports;     /* the libraries of the project a source imports */
    const char* ownImport;      /* a library: its own import name */
    FcDict* ownBuiltIn;         /* the keys whose prelude is the program's own */
} PjLinker;

/* ProjectLibraries.NamespaceOf: the import name, none for a standard library */
static const char* pjLibraryNamespace(const PjLibrary* lib) { return lib->project->project->standard ? NULL : lib->importName; }
/* ProjectNativeOverlay.KeyOf */
static const char* pjOverlayKey(FcCtx* c, const char* importName) { return fcFmt(c, "pkg:%s", pjLower(c, importName)); }
/* ProjectLibraries.UnknownHint */
static const char* pjUnknownHint(FcCtx* c, const PjPlan* plan, const char* name) {
    FcList* names = fcListNew(c);
    FcDict* seen = fcDictNew(c, FC_KEY_STR);
    int i;
    if (!plan || plan->libraries->count == 0) return NULL;
    for (i = 0; i < plan->libraries->count; i++) {
        const PjLibrary* l = (const PjLibrary*)plan->libraries->items[i];
        if (!fcSetAdd(seen, pjLower(c, l->importName))) continue;
        if (pjIEq(l->project->project->name, name)) return fcFmt(c, "The library '%s' is imported as `#import \"%s\"` (its 'import').", l->project->project->name, l->importName);
    }
    for (i = 0; i < plan->libraries->count; i++) {
        const PjLibrary* l = (const PjLibrary*)plan->libraries->items[i];
        const char* q = fcFmt(c, "\"%s\"", l->importName);
        bool have = false;
        int k;
        for (k = 0; k < names->count; k++) if (pjIEq((const char*)names->items[k], q)) have = true;
        if (!have) fcListAdd(c, names, (void*)q);
    }
    pjStableSort(names, pjCmpIgnoreCaseQ);
    return fcFmt(c, "The libraries the project references are imported as: %s.", fgJoin(c, names, ", "));
}
/* the import directive with a plan (Linker.CompileAndLink's registry), the other directives as linker.c has them */
static void pjLinkDirective(FcCtx* c, void* state, const char* name, FcList* args, int line, FcList* importNamespaces) {
    PjLinker* P = (PjLinker*)state;
    FcLinker* L = &P->L;
    FcToken* arg = args->count > 0 ? (FcToken*)args->items[0] : NULL;
    if (streq0(name, "import")) {
        const char* importName;
        PjLibrary* lib;
        FcList* keys;
        FcPkgImport* pi;
        int i;
        if (arg->type != TT_StringLiteral) fcThrow(c, FC_ERR_LINKER, 1, 0, NULL, "Wrong arguments for the 'import' directive.");
        importName = arg->stringValue;
        if (P->ownImport && pjIEq(importName, P->ownImport)) return;
        if ((lib = pjPlanLibrary(P->plan, importName)) != NULL) {
            /* TryImport: the C++ of the library comes with it; its names are found by the short name in this file too */
            const char* ns;
            if (lib->native) fcSetAdd(&L->nativeImports, pjOverlayKey(c, lib->importName));
            if (!pjListHasIgnoreCase(P->projectImports, lib->importName)) fcListAdd(c, P->projectImports, (void*)lib->importName);
            if ((ns = pjLibraryNamespace(lib)) != NULL && !fcListHasStr(importNamespaces, ns)) fcListAdd(c, importNamespaces, (void*)ns);
            return;
        }
        /* ImportKeys: an unknown one throws, its message with UnknownHint */
        keys = fcListNew(c);
        FC_TRY(c) {
            fcWithDependencies(L, fcParseImportName(L, importName), fcDictNew(c, FC_KEY_STR), keys);
        } FC_CATCH(c) {
            const char* hint = c->err.kind == FC_ERR_PRE ? pjUnknownHint(c, P->plan, importName) : NULL;
            if (hint) fcThrow(c, FC_ERR_PRE, 0, 0, NULL, fcFmt(c, "%s %s", c->err.message, hint));
            fcRethrow(c);
        } FC_END_TRY;
        for (i = 0; i < keys->count; i++) fcSetAdd(&L->nativeImports, keys->items[i]);
        pi = fcStoreFindImport(c, &L->store, importName);
        if (pi && !pi->standard && pi->prelude && pi->prelude[0] && !fcListHasStr(importNamespaces, pi->name)) fcListAdd(c, importNamespaces, (void*)pi->name);
        return;
    }
    fcLinkerDirective(c, &P->L, name, args, line, importNamespaces);
}
/* ProjectNativeOverlay.Register: the native parts of the plan stand for packages that are not installed (before the installed ones) */
static void pjOverlayRegister(FcCtx* c, FcStore* store, const PjPlan* plan) {
    FcList* parts = pjNativeParts(c, plan);
    FcList* merged = fcListNew(c);
    int i, k;
    if (!store->loaded) fcStoreLoad(c, store);
    for (i = 0; i < parts->count; i++) {
        const PjNativePart* part = (const PjNativePart*)parts->items[i];
        FcPkgImport* pi = (FcPkgImport*)fcAlloc(c, sizeof *pi);
        pi->name = part->importName;
        pi->requires = fcListNew(c);
        pi->hasNative = true;
        pi->functions = fcListNew(c);
        pi->returns = fcListNew(c);
        pi->hosts = fcListNew(c);
        for (k = 0; k < part->native->m.functions->count; k++) {
            const FgPkgFn* fn = (const FgPkgFn*)part->native->m.functions->items[k];
            fcListAdd(c, pi->functions, (void*)fn->name);
            fcListAdd(c, pi->returns, (void*)fn->returns);
            fcListAdd(c, pi->hosts, FC_IVAL(fn->host));
        }
        pi->dir = part->directory;
        pi->packageName = fcFmt(c, "project:%s", part->projectName);
        pi->packageVersion = pjPackageVersion(c, plan->settings->version);
        pi->overlay = true;
        pi->overlayNative = &part->native->m;
        pi->overlayBuildDir = nbCombine(c, nbCombine(c, part->directory, "obj"), "native");
        pi->overlayFile = part->projectFile;
        fcListAdd(c, merged, pi);
    }
    for (i = 0; i < store->imports->count; i++) fcListAdd(c, merged, store->imports->items[i]);
    store->imports = merged;
}
/* ImportedPreludes.Insert with the keys that are the program's own (`own`: their prelude is not inserted) */
static int pjInsertPreludes(PjLinker* P, FcList* processed) {
    FcLinker* L = &P->L;
    FcCtx* c = L->c;
    int inserted = 0, i;
    FcList* keys = fcListNew(c);
#define PJ_WANTS(k) (!fcDictHas(P->ownBuiltIn, k))
    if (fcDictHas(&L->nativeImports, "graphics")) { if (PJ_WANTS("graphics")) { fcListInsert(c, processed, 1, fcBuiltInPrelude(L, "graphics")); inserted++; } fcRegisterBridge(L, FC_BRIDGE_graphics); }
    if (fcDictHas(&L->nativeImports, "windows")) { if (PJ_WANTS("windows")) { fcListInsert(c, processed, 1, fcBuiltInPrelude(L, "windows")); inserted++; } fcRegisterBridge(L, FC_BRIDGE_windows); }
    if (fcDictHas(&L->nativeImports, "ui") && PJ_WANTS("ui")) { fcListInsert(c, processed, 1, fcBuiltInPrelude(L, "ui")); inserted++; }
    if (fcDictHas(&L->nativeImports, "linq") && PJ_WANTS("linq")) { fcListInsert(c, processed, 1, fcBuiltInPrelude(L, "linq")); inserted++; }
    if (fcDictHas(&L->nativeImports, "reflection")) { if (PJ_WANTS("reflection")) { fcListInsert(c, processed, 1, fcBuiltInPrelude(L, "reflection")); inserted++; } fcRegisterBridge(L, FC_BRIDGE_reflection); }
    FC_DICT_EACH(&L->nativeImports, e) if (fcStartsWith((const char*)e->key, "pkg:")) fcListAdd(c, keys, (void*)e->key);
    if (keys->count > 1) qsort(keys->items, (size_t)keys->count, sizeof(void*), fcCmpOrdinal);
    for (i = 0; i < keys->count; i++) {
        const char* key = (const char*)keys->items[i];
        FcPkgImport* pi = fcStoreFindKey(c, &L->store, key);
        int f;
        if (!pi) fcThrow(c, FC_ERR_LINKER, 1, 0, NULL, fcFmt(c, "The import '%s' belongs to a package that is not installed.", key + 4));
        if (PJ_WANTS(key) && pi->prelude && pi->prelude[0]) { fcListInsert(c, processed, 1, fcPreludeOf(L, pi, key)); inserted++; }
        for (f = 0; f < pi->functions->count; f++) fcRegisterNative(L, (const char*)pi->functions->items[f]);
    }
#undef PJ_WANTS
    return inserted;
}
/* Preprocessor.Process of a text (fc8) from a folder */
static FcProcessed* pjPreprocessText(FcCtx* c, const char* text, const char* folder) {
    FcU16 u = {0};
    uint16_t* copy;
    FcProcessed* p;
    fcU16FromFc8(text, &u);
    copy = (uint16_t*)fcAlloc(c, sizeof(uint16_t) * (size_t)(u.len + 1));
    if (u.len) memcpy(copy, u.p, sizeof(uint16_t) * (size_t)u.len);
    p = fcPreprocess(c, copy, u.len, folder);
    fcU16Free(&u);
    return p;
}
/* Linker.IsDeclaration */
static bool pjIsDeclaration(const FcStmt* s) {
    switch (s->kind) {
        case SK_ClassDecl: case SK_InterfaceDecl: case SK_EnumDecl: case SK_ExternDecl: case SK_ClassExtensionDecl: case SK_NamespaceDecl: case SK_MethodDecl:
        case SK_NoOpStmt: case SK_NoShadowDirective: case SK_GlobalJobsDirective: case SK_TypeCheckDirective: case SK_TimeoutDirective: case SK_EndWaitDirective:
            return true;
        default: return false;
    }
}
/* SemanticVersion.CompareTo of two versions that parse */
static int pjVersionCompare(FcCtx* c, const char* a, const char* b) {
    const char* ta = pjTrim(c, a);
    const char* tb = pjTrim(c, b);
    const char* da = strchr(ta, '-');
    const char* db = strchr(tb, '-');
    FcList* pa = fgSplit(c, da ? fcStrN(c, ta, (size_t)(da - ta)) : ta, '.');
    FcList* pb = fgSplit(c, db ? fcStrN(c, tb, (size_t)(db - tb)) : tb, '.');
    const char* sa = da ? da + 1 : "";
    const char* sb = db ? db + 1 : "";
    int n = pa->count > pb->count ? pa->count : pb->count, i;
    for (i = 0; i < n; i++) {
        int x = i < pa->count ? atoi(pjTrim(c, (const char*)pa->items[i])) : 0, y = i < pb->count ? atoi(pjTrim(c, (const char*)pb->items[i])) : 0;
        if (x != y) return x < y ? -1 : 1;
    }
    if (streq0(sa, sb)) return 0;
    if (!*sa) return 1;
    if (!*sb) return -1;
    return strcmp(sa, sb) < 0 ? -1 : 1;
}

/* ---- a reference `github:owner/repo[@ref][#name]` (PackageStore.FindBySpec, GitHubSpec.Parse, PackageLock.Load/ForRepository of src/fire.Package.Manager,
 * commit de45e7e4): the package the lock file of the store (ember-lock.json, written by `ember install github:...`) says came from that repository. forge only
 * reads the lock file; installing from GitHub (the network) is ember's. ---- */
static bool pjGhChars(const char* s, size_t n, const char* extra) {
    size_t i;
    for (i = 0; i < n; i++) if (!(isalnum((unsigned char)s[i]) && (unsigned char)s[i] < 0x80) && !strchr(extra, s[i])) return false;
    return true;
}
/* GitHubSpec.TryParse: the repository `owner/repo` (".git" taken off), NULL when the spec names none */
static const char* pjGitHubRepository(FcCtx* c, const char* spec) {
    const char* rest = pjTrim(c, spec + 7);
    const char* hash = strchr(rest, '#');
    const char* at;
    const char* slash;
    const char* owner;
    const char* repo;
    const char* ref = NULL;
    size_t on, rn;
    if (hash) { if (!pkIsName(hash + 1)) return NULL; rest = fcStrN(c, rest, (size_t)(hash - rest)); }
    if ((at = strchr(rest, '@')) != NULL) { ref = at + 1; rest = fcStrN(c, rest, (size_t)(at - rest)); }
    slash = strchr(rest, '/');
    if (!slash || strchr(slash + 1, '/')) return NULL;
    owner = fcStrN(c, rest, (size_t)(slash - rest)); repo = slash + 1;
    on = strlen(owner); rn = strlen(repo);
    /* ^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$, ^[A-Za-z0-9._-]{1,100}$ */
    if (on < 1 || on > 39 || !pjGhChars(owner, 1, "") || !pjGhChars(owner, on, "-")) return NULL;
    if (rn < 1 || rn > 100 || !pjGhChars(repo, rn, "._-") || streq0(repo, ".") || streq0(repo, "..")) return NULL;
    if (rn > 4 && fcAsciiIEq(repo + rn - 4, ".git")) repo = fcStrN(c, repo, rn - 4);
    /* ^[A-Za-z0-9._+-][A-Za-z0-9._/+-]{0,199}$, without "..", "//" or a "/" at the end */
    if (ref) {
        size_t n = strlen(ref);
        if (n < 1 || n > 200 || !pjGhChars(ref, 1, "._+-") || !pjGhChars(ref, n, "._/+-") || strstr(ref, "..") || strstr(ref, "//") || ref[n - 1] == '/') return NULL;
    }
    return fcFmt(c, "%s/%s", owner, repo);
}
/* PackageLock.Load + ForRepository: the name of the package of that repository; NULL - none, or a lock file that cannot be read (FindBySpec: not installed) */
static const char* pjLockedPackage(FcCtx* c, const char* storeRoot, const char* repository) {
    FcJson* lock = nbReadJsonLenient(c, nbCombine(c, storeRoot, "ember-lock.json"));
    FcJson* format;
    FcJson* entries;
    int i;
    if (!lock || lock->kind != FJ_OBJ) return NULL;
    if ((format = fcJsonGet(lock, "format")) != NULL && (format->kind != FJ_NUM || format->n > 1)) return NULL;
    if (!(entries = fcJsonGet(lock, "github")) || entries->kind == FJ_NULL) return NULL;
    if (entries->kind != FJ_ARR) return NULL;
    for (i = 0; i < entries->items->count; i++) {
        FcJson* e = (FcJson*)entries->items->items[i];
        const char* r = e && e->kind == FJ_OBJ ? fcJsonStr(e, "repository") : NULL;
        if (r && pjIEq(r, repository)) return fcJsonStr(e, "package") ? fcJsonStr(e, "package") : "";
    }
    return NULL;
}

/* What a link with a plan is asked for: the engine `vm` (target NULL) or `native` for a target (FgOptions of gen/native.c) */
typedef struct PjLinkIn {
    PjPlan* plan;
    const char* prelude; const char* storeRoot;
    const FgOptions* o;          /* NULL: the VM, no target (ProjectBuilder.Check) */
    const char* toolchainKind;   /* the symbol of the toolchain (a native build) */
    int modeOverride;            /* -1: none (the VM; a native build takes o->mode) */
    int floatWidthOverride;      /* 0: none */
    int intWidthOverride;        /* 0: none (-i, Linker.ProcessIntWidth; a native build takes o->intWidth) - Phase D 4 */
    FcStore** storeOut;          /* gets the store of the link, with the overlay of the plan (the engine vm: the libraries of its packages, build/cvm.c); NULL: not wanted */
} PjLinkIn;

/* Linker.CompileAndLink with the plan: the linked program; `registry` gets the NativeRegistry (the generator's names) */
static FcLinked* pjLink(FcCtx* c, const PjLinkIn* in, FcNatives** registry) {
    PjLinker* P = (PjLinker*)fcAlloc(c, sizeof *P);
    FcLinker* L = &P->L;
    PjPlan* plan = in->plan;
    const FgTarget* t = in->o ? in->o->target : NULL;
    FcList* processed = fcListNew(c);
    FcList* defines = fcListNew(c);
    FcList* libraryFiles = fcListNew(c);   /* FcProcessed* */
    FcList* libraryPaths = fcListNew(c);
    FcProcessed** arr;
    FcList* program;
    FcList* sourceModules;
    FcResolveResult* resolved;
    FcCompiled* compiled;
    FcLinked* linked;
    FcDict* notSerializable;
    FcDict* done = fcDictNew(c, FC_KEY_STR);
    const uint16_t* src;
    int len, firstUserSource = 1, i, k, total, intWidth, floatWidth, intOverride = in->o ? in->o->intWidth : in->intWidthOverride, floatOverride = in->o ? in->o->floatWidth : in->floatWidthOverride;
    bool strict = plan->settings->typeChecking && pjIEq(plan->settings->typeChecking, "strict");
    L->c = c;
    L->store.root = in->storeRoot;
    L->directiveIntWidth = L->directiveFloatWidth = FC_NULL_INT;
    fcDictInit(c, &L->nativeImports, FC_KEY_STR);
    fcDictInit(c, &L->natives, FC_KEY_STR);
    fcNativesInit(c, &L->registry);
    L->executionMode = 1;
    P->plan = plan;
    P->projectImports = fcListNew(c);
    P->ownBuiltIn = fcDictNew(c, FC_KEY_STR);
#define FIRE_NATIVE(index, name, tryable) fcRegisterNative(L, name);
#include "../../tables/natives.def"
#undef FIRE_NATIVE
    fcSetAdd(&L->nativeImports, "print");
    /* ConditionalSymbols.For(target, Engine, floatWidth, EffectiveDefines(), intWidth) and the toolchain */
    if (plan->settings->defines) for (i = 0; i < plan->settings->defines->count; i++) if (!fcListHasStr(defines, (const char*)plan->settings->defines->items[i])) fcListAdd(c, defines, plan->settings->defines->items[i]);
    if (t) {
        for (i = 0; i < t->symbolCount; i++) fcSymbolsAdd(c, c->symbols, t->symbols[i]);
        fcSymbolsAdd(c, c->symbols, "native");
        if ((floatOverride ? floatOverride : t->floatWidth) == 32) fcSymbolsAdd(c, c->symbols, "float32");
        if ((intOverride ? intOverride : t->intWidth) == 32) fcSymbolsAdd(c, c->symbols, "int32");
        if ((intOverride ? intOverride : t->intWidth) == 16) fcSymbolsAdd(c, c->symbols, "int16");
        fcSymbolsAdd(c, c->symbols, fcFmt(c, "word%d", t->wordWidth));
    } else {
        fcSymbolsForHost(c, c->symbols);
        if (floatOverride == 32) fcSymbolsAdd(c, c->symbols, "float32");
        if (intOverride == 32) fcSymbolsAdd(c, c->symbols, "int32");
        if (intOverride == 16) fcSymbolsAdd(c, c->symbols, "int16");
    }
    for (i = 0; i < defines->count; i++) if (!pjIsWhite((const char*)defines->items[i])) fcSymbolsAdd(c, c->symbols, pjTrim(c, (const char*)defines->items[i]));
    if (in->toolchainKind && *fgTrim(c, in->toolchainKind)) fcSymbolsAdd(c, c->symbols, fgTrim(c, in->toolchainKind));
    /* the C++ natives of the project and its libraries are found like those of installed packages */
    pjOverlayRegister(c, &L->store, plan);
    if (plan->native) fcSetAdd(&L->nativeImports, pjOverlayKey(c, plan->native->importName));
    /* a library is its own import (a standard library of an import the compiler knows: the natives of the VM, without the installed prelude) */
    P->ownImport = plan->project->project->type == PJ_LIBRARY ? pjImportName(c, plan->project->project) : NULL;
    if (P->ownImport) {
        const char* lower = pjLower(c, P->ownImport);
        if (streq0(lower, "graphics") || streq0(lower, "windows") || streq0(lower, "ui") || streq0(lower, "linq") || streq0(lower, "reflection")) { fcSetAdd(P->ownBuiltIn, lower); fcSetAdd(&L->nativeImports, lower); }
    }
    c->sharedIncluded = fcListNew(c);
    for (i = 0; plan->files != 2 && i < plan->sources->count; i++) {
        const char* full = pjFull(c, ((PjSource*)plan->sources->items[i])->path, ".");
        if (fcSetAdd(done, full)) fcListAdd(c, c->sharedIncluded, (void*)full);
    }
    c->resourcePaths = fcListNew(c);
    c->resourceNames = fcListNew(c);
    c->directiveHook = pjLinkDirective;
    c->directiveState = P;
    if (!fcReadSource(c, in->prelude, &src, &len)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "cannot read the prelude '%s'", in->prelude));
    {
        char cwd[4096];
        if (!getcwd(cwd, sizeof cwd)) strcpy(cwd, ".");
        fcListAdd(c, processed, fcPreprocess(c, src, len, fcStr(c, cwd)));
    }
    for (i = 0; i < plan->sources->count; i++) {
        const PjSource* s = (const PjSource*)plan->sources->items[i];
        const char* full = pjFull(c, s->path, ".");
        FcProcessed* p;
        if (plan->files == 2) {
            /* (texts: from BasePath, else the current folder; no name) */
            char cwd[4096];
            if (!getcwd(cwd, sizeof cwd)) strcpy(cwd, ".");
            p = pjPreprocessText(c, s->text, plan->baseFolder ? plan->baseFolder : fcStr(c, cwd));
            p->name = NULL;
        } else {
            p = pjPreprocessText(c, s->text, fcDirectoryName(c, full));
            p->name = plan->files == 1 ? fcStr(c, pjFileName(full)) : pjRelative(c, plan->project->project->directory, full);
        }
        fcListAdd(c, processed, p);
    }
    /* ProjectLibraries.Process: the libraries the sources import (and what they need, the needed ones first) - a file may import more */
    if (P->projectImports->count > 0) {
        FcDict* byImport = fcDictNew(c, FC_KEY_STR);   /* lower import name -> FcList* of (FcProcessed*, path) pairs */
        FcList* order;
        bool more = true;
        while (more) {
            more = false;
            order = pjLibraryOrder(c, plan, fcListCopy(c, P->projectImports));
            for (i = 0; i < order->count; i++) {
                const PjLibrary* lib = (const PjLibrary*)order->items[i];
                FcList* files;
                if (fcDictHas(byImport, pjLower(c, lib->importName))) continue;
                more = true;
                files = fcListNew(c);
                for (k = 0; k < lib->sources->count; k++) {
                    const PjSource* s = (const PjSource*)lib->sources->items[k];
                    FcProcessed* p = pjPreprocessText(c, s->text, fcDirectoryName(c, pjFull(c, s->path, ".")));
                    p->name = fcFmt(c, "%s/%s", lib->project->project->name, pjFileName(s->path));
                    p->module = lib->importName;
                    p->qualify = pjLibraryNamespace(lib);
                    fcListAdd(c, files, p);
                    fcListAdd(c, files, (void*)s->path);
                }
                fcDictPut(byImport, pjLower(c, lib->importName), files);
            }
        }
        order = pjLibraryOrder(c, plan, fcListCopy(c, P->projectImports));
        for (i = 0; i < order->count; i++) {
            FcList* files = (FcList*)fcDictGet(byImport, pjLower(c, ((const PjLibrary*)order->items[i])->importName));
            if (files) for (k = 0; k < files->count; k += 2) { fcListAdd(c, libraryFiles, files->items[k]); fcListAdd(c, libraryPaths, files->items[k + 1]); }
        }
    }
    firstUserSource += pjInsertPreludes(P, processed);
    /* the libraries of the project come before the code of the project */
    for (i = 0; i < libraryFiles->count; i++) fcListInsert(c, processed, 1 + i, libraryFiles->items[i]);
    firstUserSource += libraryFiles->count;
    c->directiveHook = NULL;
    /* CheckPackageReferences */
    for (i = 0; i < plan->packages->count; i++) {
        const PjReference* r = (const PjReference*)plan->packages->items[i];
        const char* name = r->package;
        const char* at = strchr(name, '@');
        const char* installed = NULL;
        const char* version = NULL;
        int m;
        if (strlen(name) >= 7 && pjIEq(fcStrN(c, name, 7), "github:")) {
            /* (FindBySpec: the package the lock file names for the repository) */
            const char* repository = pjGitHubRepository(c, name);
            name = repository ? pjLockedPackage(c, L->store.root, repository) : NULL;
        } else if (at && at > name) name = fcStrN(c, name, (size_t)(at - name));
        if (!L->store.loaded) fcStoreLoad(c, &L->store);
        for (m = 0; name && m < L->store.imports->count; m++) {
            const FcPkgImport* pi = (const FcPkgImport*)L->store.imports->items[m];
            if (pi->packageName && pjIEq(pi->packageName, name)) { installed = pi->packageName; version = pi->packageVersion; break; }
        }
        if (!installed) fcThrow(c, FC_ERR_LINKER, 1, 0, NULL, fcFmt(c, "The project '%s' references the package '%s', which is not installed (`ember install %s`).", plan->project->project->name, r->package, r->package));
        if (r->version && pkIsVersion(c, r->version) && version && pkIsVersion(c, version) && pjVersionCompare(c, version, r->version) < 0)
            fcThrow(c, FC_ERR_LINKER, 1, 0, NULL, fcFmt(c, "The project '%s' needs the package '%s' %s or newer, %s is installed.", plan->project->project->name, r->package, r->version, version));
    }
    sourceModules = fcListNew(c);
    for (i = 0; i < processed->count; i++) fcListAdd(c, sourceModules, i == 0 ? (void*)"core" : (void*)((FcProcessed*)processed->items[i])->module);
    { char step[64]; snprintf(step, sizeof step, "Compiling fire: parsing %d sources", processed->count); fcMonitorStep(0, step, 0, 4); }   /* (BuildMonitor.Step, Phase D 4) */
    arr = (FcProcessed**)fcAlloc(c, sizeof(FcProcessed*) * (size_t)processed->count);
    for (i = 0; i < processed->count; i++) arr[i] = (FcProcessed*)processed->items[i];
    program = fcParseMultiple(c, arr, processed->count);
    if (c->testDriver && fcTestDriverApply(c, program, processed, firstUserSource)) fcListAdd(c, sourceModules, NULL);   /* (`forge test`, Phase D 4) */
    /* CheckLibraryHasNoEntryPoint */
    if (plan->project->project->type == PJ_LIBRARY) {
        FcList* errors = fcListNew(c);
        int userCount = plan->sources->count;
        for (i = 0; i < program->count; i++) {
            const FcStmt* s = (const FcStmt*)program->items[i];
            if (s->source < firstUserSource || s->source >= firstUserSource + userCount) continue;
            if (pjIsDeclaration(s)) continue;
            fcListAdd(c, errors, (void*)fcFmt(c, "%s:%d: a library has no entry point - this statement would run at the top level (put it in a function or a class).",
                pjFileName(((const PjSource*)plan->sources->items[s->source - firstUserSource])->path), s->line));
            if (errors->count >= 20) break;
        }
        if (errors->count > 0) fcThrow(c, FC_ERR_LINKER, 1, 0, NULL, fgJoin(c, errors, "\n"));
    }
    fcMonitorStep(0, "Compiling fire: resolving names", 1, 4);
    resolved = fcResolve(c, program, &L->natives, NULL);
    intWidth = intOverride ? intOverride : plan->settings->intWidth != FC_NULL_INT ? plan->settings->intWidth : L->directiveIntWidth != FC_NULL_INT ? L->directiveIntWidth : t ? t->intWidth : 64;
    floatWidth = floatOverride ? floatOverride : plan->settings->floatWidth != FC_NULL_INT ? plan->settings->floatWidth : L->directiveFloatWidth != FC_NULL_INT ? L->directiveFloatWidth : t ? t->floatWidth : 64;
    c->programWordWidth = t ? t->wordWidth : 64;
    fcMonitorStep(0, "Compiling fire: checking types", 2, 4);
    fcTypeCheckRun(c, program, resolved, strict, firstUserSource, firstUserSource, intWidth, floatWidth, fcNativeReturnTypes(L));
    /* (Linker.ForcedGlobalJobs: the project's settings.globalJobs before `#globaljobs`; GlobalJobsDefault: "globalJobs" of fire.native.json, a native build) */
    fcMonitorStep(0, "Compiling fire: generating bytecode", 3, 4);
    compiled = fcCompileJobs(c, program, resolved, &L->registry, false, plan->settings->globalJobs ? nbGlobalJobsParse(c, plan->settings->globalJobs) : -1, in->o ? in->o->globalJobs - 1 : -1);
    linked = (FcLinked*)fcAlloc(c, sizeof *linked);
    notSerializable = fcDictNew(c, FC_KEY_STR);
    total = processed->count;
    for (i = 0; i < processed->count; i++) if (((FcProcessed*)processed->items[i])->notSerializable) fcSetAdd(notSerializable, fcFmt(c, "%d", i));
    if (fcDictCount(notSerializable) > 0)
        FC_DICT_EACH(compiled->classes, e) {
            FcRtClass* rc = (FcRtClass*)e->value;
            if (rc->decl && fcDictHas(notSerializable, fcFmt(c, "%d", rc->decl->h.source))) rc->notSerializable = true;
        }
    for (i = 0; i < c->resourcePaths->count; i++) {
        FcResourceEntry* r = (FcResourceEntry*)fcAlloc(c, sizeof *r);
        size_t n = 0;
        uint8_t* data = fcResourceRead(c, (const char*)c->resourcePaths->items[i], &n);   /* (a model: its model file, Phase D 4) */
        r->name = (const char*)c->resourceNames->items[i];
        r->data = (uint8_t*)fcAlloc(c, n + 1);
        if (data && n) memcpy((void*)r->data, data, n);
        free(data);
        r->size = n;
        fcListAdd(c, compiled->resources, r);
    }
    for (i = 0; i < c->customUnits->count; i++) fcListAdd(c, compiled->customUnits, c->customUnits->items[i]);
    if (t)
        FC_DICT_EACH(&L->nativeImports, e)
            if (!fgTargetHasImport(t, (const char*)e->key)) {
                FcList* avail = fcListNew(c);
                for (k = 0; k < t->importCount; k++) fcListAdd(c, avail, (void*)t->imports[k]);
                fcThrow(c, FC_ERR_NOTSUPPORTED, 0, 0, NULL, fcFmt(c, "The library '%s' is not available on the target '%s' (available: %s).", (const char*)e->key, t->name, fgJoin(c, avail, ", ")));
            }
    /* ApplySettings: the project's settings before the tags of the sources */
    if (plan->settings->intWidth != FC_NULL_INT) L->directiveIntWidth = plan->settings->intWidth;
    if (plan->settings->mode) L->executionMode = streq0(plan->settings->mode, "debug") ? 0 : streq0(plan->settings->mode, "performance") ? 2 : 1;
    if (plan->settings->floatWidth != FC_NULL_INT) L->directiveFloatWidth = plan->settings->floatWidth;
    floatWidth = floatOverride ? floatOverride : L->directiveFloatWidth != FC_NULL_INT ? L->directiveFloatWidth : t ? t->floatWidth : 64;
    if (floatWidth != 32 && floatWidth != 64) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "The float width must be 32 or 64.");
    if (floatWidth == 32) fcNarrow(c, compiled, fcNarrowFloat, 32);
    intWidth = intOverride ? intOverride : L->directiveIntWidth != FC_NULL_INT ? L->directiveIntWidth : t ? t->intWidth : 64;
    if (intWidth < 64) fcNarrow(c, compiled, fcNarrowInt, intWidth);
    linked->program = compiled;
    linked->floatWidth = floatWidth;
    linked->intWidth = intWidth;
    linked->wordWidth = t ? t->wordWidth : 64;
    linked->firstUserSource = firstUserSource;
    linked->mode = in->o ? (in->o->mode >= 0 ? in->o->mode : L->executionMode) : (in->modeOverride >= 0 ? in->modeOverride : L->executionMode);
    linked->nativeImports = &L->nativeImports;
    fcPackageNativesOf(L, &linked->packageNatives, &linked->packageNativeLibraries);
    /* SourceFilesOf: the files of the libraries behind the prelude, the files of the program at the end; SourceHashesOf: the program's */
    linked->sourceFiles = fcListNew(c);
    linked->sourceHashes = fcListNew(c);
    for (i = 0; i < total; i++) { fcListAdd(c, linked->sourceFiles, NULL); fcListAdd(c, linked->sourceHashes, NULL); }
    for (i = 0; i < libraryPaths->count && 1 + i < total; i++) linked->sourceFiles->items[1 + i] = libraryPaths->items[i];
    for (i = 0; i < plan->sources->count; i++) {
        const PjSource* s = (const PjSource*)plan->sources->items[i];
        linked->sourceFiles->items[total - plan->sources->count + i] = (void*)pjFull(c, s->path, ".");
        linked->sourceHashes->items[total - plan->sources->count + i] = (void*)fcSha256OfText(c, s->text);
    }
    linked->optimize = in->o ? (in->o->optimize >= 0 ? in->o->optimize : t->optimize) : 0;
    linked->sourceModules = sourceModules;
    if (registry) *registry = &L->registry;
    if (in->storeOut) *in->storeOut = &L->store;
    return linked;
}
