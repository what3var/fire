/* native/forge/project/standard.c - the standard packages (Phase D 3, commit de45e7e4): src/fire.Compiler/StandardSources.cs (Projects, ImportsOf, HashOf,
 * the stamps `.source-stamps.json`, EnsureInstalled, Install, UsePrebuilt) and PackageStore.Install (src/fire.Package.Manager): what forge does when it starts -
 * the projects of Source/Packages.firesln next to the program that are not installed yet, or that changed since, are packed (ProjectBuilder.PackLibrary) and
 * installed into the store, with a library from Prebuilt/ when it belongs to their C. The same stamps as the C# forge: what the one installed, the other does
 * not install again. `forge-c install-standard` does it on its own (Program.cs). ember stays the C# program (installing from the sources, from GitHub). */

static FcList* svFindImportNames(FcCtx* c, const uint16_t* s, int n);   /* (serve_index.c: ImportedPreludes.FindImportNames) */

/* ImportsOf: the names the files of a project import (`#import "name"`), not its tests */
static FcList* nsImportsOf(FcCtx* c, const PjLoaded* project) {
    FcList* names = fcListNew(c);
    int i, k;
    for (i = 0; i < project->files->count; i++) {
        FcU16 text = {0};
        FcList* found;
        if (!fcReadText((const char*)project->files->items[i], &text)) continue;
        found = svFindImportNames(c, text.p, text.len);
        fcU16Free(&text);
        for (k = 0; k < found->count; k++) if (!pjListHasIgnoreCase(names, (const char*)found->items[k])) fcListAdd(c, names, found->items[k]);
    }
    return names;
}
/* Projects: the projects of the solution in the order they are packed - a project after the ones whose imports it uses */
static void nsVisit(FcCtx* c, PjLoaded* p, PjWorkspace* w, FcList* result, FcList* visiting) {
    FcList* imports;
    int i, k;
    if (pjInList(result, p) || pjInList(visiting, p)) return;
    fcListAdd(c, visiting, p);
    imports = nsImportsOf(c, p);
    for (i = 0; i < imports->count; i++)
        for (k = 0; k < w->members->count; k++) {
            PjLoaded* dep = (PjLoaded*)w->members->items[k];
            if (pjIEq(pjImportName(c, dep->project), (const char*)imports->items[i])) { if (dep != p) nsVisit(c, dep, w, result, visiting); break; }
        }
    fcListAdd(c, result, p);
}
static FcList* nsProjects(FcCtx* c, PjWorkspace* w) {
    FcList* result = fcListNew(c);
    FcList* visiting = fcListNew(c);
    int i;
    for (i = 0; i < w->members->count; i++) nsVisit(c, (PjLoaded*)w->members->items[i], w, result, visiting);
    return result;
}
/* HashOf: every file of the project's folder but the tests (and bin/, obj/) - its path relative to the folder and a newline, then its bytes; upper-case hex */
static void nsFilesOf(FcCtx* c, const char* dir, const char* rel, FcList* out) {
    DIR* d = opendir(rel[0] ? nbCombine(c, dir, rel) : dir);
    struct dirent* e;
    if (!d) return;
    while ((e = readdir(d)) != NULL) {
        const char* name = e->d_name;
        const char* r;
        const char* full;
        struct stat st;
        if (streq0(name, ".") || streq0(name, "..")) continue;
        r = rel[0] ? fcFmt(c, "%s/%s", rel, name) : fcStr(c, name);
        full = nbCombine(c, dir, r);
        if (stat(full, &st) != 0) continue;
        if (S_ISDIR(st.st_mode)) nsFilesOf(c, dir, r, out);
        else if (S_ISREG(st.st_mode)) fcListAdd(c, out, (void*)r);
    }
    closedir(d);
}
static const char* nsHashOf(FcCtx* c, const PjLoaded* project) {
    FcList* all = fcListNew(c);
    FcSha256 h;
    int i;
    nsFilesOf(c, project->project->directory, "", all);
    if (all->count > 1) qsort(all->items, (size_t)all->count, sizeof(void*), fcCmpOrdinal);
    fcShaInit(&h);
    for (i = 0; i < all->count; i++) {
        const char* rel = (const char*)all->items[i];
        size_t n = 0;
        uint8_t* data;
        if ((strlen(rel) > 8 && fcAsciiIEq(fcStrN(c, rel, 8), "testing/")) || fcStartsWith(rel, "bin/") || fcStartsWith(rel, "obj/")) continue;
        fcShaUpdate(&h, (const uint8_t*)rel, strlen(rel));
        fcShaUpdate(&h, (const uint8_t*)"\n", 1);
        data = fbReadFile(nbCombine(c, project->project->directory, rel), &n);
        if (data && n) fcShaUpdate(&h, data, n);
        free(data);
    }
    return nlUpper(c, fcShaHex(c, &h));
}

/* ---- the stamps: Dictionary<string, string> as System.Text.Json reads and writes it (indented) ---- */
typedef struct NsStamps { FcList* names; FcList* hashes; } NsStamps;
static NsStamps nsLoadStamps(FcCtx* c, const char* root) {
    NsStamps s;
    FcJson* j = nbReadJsonLenient(c, nbCombine(c, root, ".source-stamps.json"));
    int i;
    s.names = fcListNew(c); s.hashes = fcListNew(c);
    if (!j || j->kind != FJ_OBJ) return s;
    for (i = 0; i < j->keys->count; i++) {
        const FcJson* v = (const FcJson*)j->items->items[i];
        if (!v || v->kind != FJ_STR) { s.names = fcListNew(c); s.hashes = fcListNew(c); return s; }   /* (a JsonException: no stamps) */
        fcListAdd(c, s.names, j->keys->items[i]);
        fcListAdd(c, s.hashes, (void*)v->s);
    }
    return s;
}
static const char* nsStampOf(const NsStamps* s, const char* name) {
    int i;
    for (i = 0; i < s->names->count; i++) if (streq0((const char*)s->names->items[i], name)) return (const char*)s->hashes->items[i];
    return NULL;
}
static void nsSetStamp(FcCtx* c, NsStamps* s, const char* name, const char* hash) {
    int i;
    for (i = 0; i < s->names->count; i++) if (streq0((const char*)s->names->items[i], name)) { s->hashes->items[i] = (void*)hash; return; }
    fcListAdd(c, s->names, (void*)name);
    fcListAdd(c, s->hashes, (void*)hash);
}
static void nsSaveStamps(FcCtx* c, const char* root, const NsStamps* s) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "{");
    for (i = 0; i < s->names->count; i++) {
        fcBufStr(&b, i ? ",\n  " : "\n  ");
        nlJsonString(&b, (const char*)s->names->items[i]);
        fcBufStr(&b, ": ");
        nlJsonString(&b, (const char*)s->hashes->items[i]);
    }
    fcBufStr(&b, s->names->count ? "\n}" : "}");
    nbMkdirs(c, root);
    nbWriteBytes(c, nbCombine(c, root, ".source-stamps.json"), b.p, b.len);
    fcBufFree(&b);
}

/* PackageStore.Install: the package file into the store (in place of an installed package of the same name); an import of another package is an error */
static void nsStoreInstall(FcCtx* c, const char* root, const char* fpk) {
    PkManifest* m = pkReadManifest(c, fpk);
    FcStore store;
    const char* temp;
    const char* target;
    int i;
    memset(&store, 0, sizeof store);
    store.root = root;
    for (i = 0; i < m->imports->count; i++) {
        FcPkgImport* other = fcStoreFindImport(c, &store, ((PkImport*)m->imports->items[i])->name);
        if (other && other->packageName && !pjIEq(other->packageName, m->name))
            pkThrow(c, fcFmt(c, "The import '%s' already belongs to the package '%s'.", ((PkImport*)m->imports->items[i])->name, other->packageName));
    }
    nbMkdirs(c, root);
    temp = nbCombine(c, root, fcFmt(c, ".tmp-%s", nbGuid(c)));
    FC_TRY(c) {
        pkExtract(c, fpk, temp);
    } FC_CATCH(c) {
        if (nbDirExists(temp)) nbDeleteTree(c, temp);
        fcRethrow(c);
    } FC_END_TRY;
    target = nbCombine(c, root, m->name);
    if (nbDirExists(target)) nbDeleteTree(c, target);
    if (rename(temp, target) != 0) { nbDeleteTree(c, temp); pkThrow(c, fcFmt(c, "Could not install the package into '%s'.", target)); }
}
/* UsePrebuilt: the library built for this machine with the programs (Prebuilt/<package>/<rid>/) next to the installed package, when it belongs to its C now */
static void nsUsePrebuilt(FcCtx* c, const char* root, const char* packageName) {
    FcStore store;
    FcDict* cache = fcDictNew(c, FC_KEY_PTR);
    int i;
    memset(&store, 0, sizeof store);
    store.root = root;
    fcStoreLoad(c, &store);
    for (i = 0; i < store.imports->count; i++) {
        FcPkgImport* pi = (FcPkgImport*)store.imports->items[i];
        FgInstImport* imp;
        const char* lib;
        const char* hash;
        const char* key;
        const char* have;
        const char* target;
        if (!pi->packageName || !streq0(pi->packageName, packageName)) continue;
        imp = fgInstalledImport(c, cache, pi);
        if (!imp->native) continue;
        lib = nbCombine(c, nbCombine(c, nbCombine(c, nlPrebuiltFolder(c), packageName), nlRid()), nlFileNameFor(c, imp));
        if (!nbFileExists(lib) || !(hash = nlReadTrimmed(c, fcFmt(c, "%s.hash", lib))) || !streq0(hash, nlNativeHashOf(c, cache, &store, imp))) continue;
        key = nlRuntimeKeyOf(c, nlWrapperOf(c, cache, &store, imp));
        if (!(have = nlReadTrimmed(c, nlKeyFile(c, lib))) || !streq0(have, key)) continue;
        target = nbCombine(c, nbCombine(c, nbCombine(c, imp->dir, "lib"), nlRid()), nbFileName(lib));
        nbMkdirs(c, fcDirectoryName(c, target));
        nbCopyFile(c, lib, target);   /* (a new file: newer than the sources, Locate takes it) */
        nbWriteText(c, nlKeyFile(c, target), key);
    }
}

/* CompileErrors.Describe: every error of a resolver, compiler or type check with " (line)" (a type check: when it has one), else the Message of the exception - of a lexer and a
 * parser error with its position "(line:column)", of a parser error behind the file */
static const char* nsDescribe(FcCtx* c) {
    const FcError* e = &c->err;
    FcList* parts;
    FcList* diags;
    int i;
    if (e->kind == FC_ERR_LEX) return fcFmt(c, "%s (%d:%d)", e->message, e->line, e->column);
    if (e->kind == FC_ERR_PARSE) return e->file ? fcFmt(c, "%s: %s (%d:%d)", e->file, e->message, e->line, e->column) : fcFmt(c, "%s (%d:%d)", e->message, e->line, e->column);
    if (e->kind != FC_ERR_RESOLVE && e->kind != FC_ERR_COMPILE && e->kind != FC_ERR_TYPECHECK) return e->message ? e->message : "?";
    parts = fcListNew(c);
    diags = fcDiagnosticsOf(c, &c->err, NULL);
    for (i = 0; i < diags->count; i++) {
        const FcDiagnostic* d = (const FcDiagnostic*)diags->items[i];
        fcListAdd(c, parts, (void*)(e->kind != FC_ERR_TYPECHECK || d->line > 0 ? fcFmt(c, "%s (%d)", d->message, d->line) : d->message));
    }
    return fgJoin(c, parts, "\n");
}
/* EnsureInstalled: packs and installs what is missing or changed; never fails - what goes wrong goes to log (stderr). Returns the number installed. */
static int nsEnsureInstalled(const char* prelude, const char* storeRoot, FILE* log) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    const char* folder;
    const char* solution;
    volatile int installed = 0;
    volatile int lockFd = -1;
    fcCtxInit(c);
    folder = nbCombine(c, nbBaseDirectory, "Source");
    solution = nbCombine(c, folder, "Packages.firesln");
    if (!nbFileExists(solution)) { fcCtxFree(c); return 0; }
    FC_TRY(c) {
        PjWorkspace* w;
        FcList* projects;
        NsStamps stamps;
        FcList* due = fcListNew(c);
        FcList* dueHash = fcListNew(c);
        FcStore store;
        volatile int i;
        nbMkdirs(c, storeRoot);
        /* (one installation at a time per store: a lock file - the C# forge takes a named mutex, they do not see each other's) */
        lockFd = open(nbCombine(c, storeRoot, ".source-lock"), O_RDWR | O_CREAT, 0644);
        if (lockFd >= 0) flock(lockFd, LOCK_EX);
        stamps = nsLoadStamps(c, storeRoot);
        w = pjWorkspaceOpen(c, solution);
        projects = nsProjects(c, w);
        memset(&store, 0, sizeof store);
        store.root = storeRoot;
        fcStoreLoad(c, &store);
        for (i = 0; i < projects->count; i++) {
            PjLoaded* p = (PjLoaded*)projects->items[i];
            const char* hash = nsHashOf(c, p);
            const char* known = nsStampOf(&stamps, p->project->name);
            bool there = false;
            int k;
            for (k = 0; k < store.imports->count; k++) { const FcPkgImport* pi = (const FcPkgImport*)store.imports->items[k]; if (pi->packageName && pjIEq(pi->packageName, p->project->name)) there = true; }
            if (there && known && streq0(known, hash)) continue;
            fcListAdd(c, due, p);
            fcListAdd(c, dueHash, (void*)hash);
        }
        for (i = 0; i < due->count; i++) {
            PjLoaded* p = (PjLoaded*)due->items[i];
            FcCtx step;
            const char* volatile failure = NULL;
            pjStepInit(&step);
            FC_TRY(&step) {
                PjPlan* plan = pjPlanCreate(w, p, false);
                const char* work = fcFmt(&step, "%sfire-source-%s", nbTempPath(&step), nbGuid(&step));
                const char* fpk;
                PjOut po;
                FcBuf ignored = {0};
                if (plan->errors->count > 0) pjThrow(&step, fgJoin(&step, plan->errors, "\n"));
                po.c = &step; po.seen = fcListNew(&step); po.err = &ignored;
                fcBufStr(&ignored, "");
                FC_TRY(&step) {
                    fpk = pjPackLibrary(&step, &po, plan, work, prelude, storeRoot, false);
                    fprintf(log, "Installing the standard package %s %s from %s...\n", p->project->name, pjPackageVersion(&step, plan->settings->version), nbFileName(p->project->directory));
                    nsStoreInstall(&step, storeRoot, fpk);
                    nsUsePrebuilt(&step, storeRoot, p->project->name);
                } FC_CATCH(&step) {
                    fcBufFree(&ignored);
                    if (nbDirExists(work)) nbDeleteTree(&step, work);
                    fcRethrow(&step);
                } FC_END_TRY;
                fcBufFree(&ignored);
                if (nbDirExists(work)) nbDeleteTree(&step, work);
            } FC_CATCH(&step) {
                if (pjIsCompileErrorKind(step.err.kind)) failure = fcStr(c, nsDescribe(&step));
                else if (step.err.kind == FC_ERR_PROJECT || step.err.kind == FC_ERR_PACKAGE) failure = fcStr(c, step.err.message);
                else failure = fcStr(c, step.err.message ? step.err.message : "?");
            } FC_END_TRY;
            fcCtxFree(&step);
            if (failure) { fprintf(log, "The standard package %s could not be installed: %s\n", p->project->name, failure); continue; }
            nsSetStamp(c, &stamps, p->project->name, (const char*)dueHash->items[i]);
            installed++;
            nsSaveStamps(c, storeRoot, &stamps);
        }
    } FC_CATCH(c) {
        fprintf(log, "The standard packages could not be checked: %s\n", c->err.message ? c->err.message : "?");
    } FC_END_TRY;
    if (lockFd >= 0) { flock(lockFd, LOCK_UN); close(lockFd); }
    fcCtxFree(c);
    return installed;
}
