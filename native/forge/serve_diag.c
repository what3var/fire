/* native/forge/serve_diag.c - the live diagnostics of `forge serve`: src/fire.Editor/Language/LiveDiagnostics.cs (commit 1c549476: Analyze,
 * AnalyzeInProject, Sorted; Remembered is the cache of serve.c) and src/fire.Compiler/ProjectLibraries.cs (Process, TryImport, UnknownHint,
 * NamespaceOf; BuildPlan.LibraryOrder), on the frontend in C (linker.c). The prelude, the other files of the project (each a source of its own,
 * before the document), the libraries the program imports, the preludes of the `#import`s, the document as the last source; parser (the first error),
 * resolver and compiler (all their errors), the type check (warnings lax, errors strict). Only what lies in the document is reported (and the warnings
 * of the libraries, with their path). Not ported: the C++ natives of a library project (ProjectNativeOverlay), a standard library project's own
 * built-in natives (Linker.BuiltInKey), the check of all the files of a project at once (AnalyzeProjectFiles). Runs on the analysis thread: everything
 * in the context of the analysis, nothing shared with the requests. */

typedef struct SvLib {
    FcList* names;        /* the names `#import` finds it by (plan.Libraries) */
    const char* import;   /* ImportName */
    const char* name;     /* the project's name */
    const char* ns;       /* NamespaceOf: NULL for a standard library */
    FcList* requires;     /* names */
    SvFile* files;
    int nfiles;
} SvLib;

typedef struct SvAnalysisIn {
    const uint16_t* text;     /* the document */
    int len;
    const char* base;         /* its folder (#include) */
    const SvFile* others;     /* the other files of the project (NULL: none) */
    int nothers;
    const SvLib* libs;
    int nlibs;
    bool hasPlan;             /* a project (with or without libraries) */
    bool strict;
    const char* ownImport;    /* the import name of a library project itself, NULL: none */
} SvAnalysisIn;

typedef struct SvAnalyzer {
    FcCtx* c;
    FcLinker* L;
    const SvAnalysisIn* in;
    FcList* projectImports;   /* the imported libraries' import names */
    const char* preludePath;
} SvAnalyzer;

static const SvLib* svLibFind(const SvAnalysisIn* in, const char* name) {
    int i, k;
    for (i = 0; i < in->nlibs; i++)
        for (k = 0; k < in->libs[i].names->count; k++) if (svEqIC((const char*)in->libs[i].names->items[k], name)) return &in->libs[i];
    return NULL;
}
static bool svIsOwn(SvAnalyzer* A, const char* name) { return A->in->ownImport && svEqIC(name, A->in->ownImport); }

/* ProjectLibraries.TryImport */
static bool svTryImportLibrary(SvAnalyzer* A, const char* name) {
    const SvLib* lib = A->in->hasPlan ? svLibFind(A->in, name) : NULL;
    int i;
    bool known = false;
    if (!lib) return false;
    for (i = 0; i < A->projectImports->count; i++) if (svEqIC((const char*)A->projectImports->items[i], lib->import)) known = true;
    if (!known) fcListAdd(A->c, A->projectImports, (void*)lib->import);
    return true;
}

/* ProjectLibraries.UnknownHint */
static const char* svUnknownHint(SvAnalyzer* A, const char* name) {
    const SvAnalysisIn* in = A->in;
    FcList* imports;
    FcBuf b = {0};
    int i, k;
    if (!in->hasPlan || in->nlibs == 0) return NULL;
    for (i = 0; i < in->nlibs; i++)
        if (svEqIC(in->libs[i].name, name)) return fcFmt(A->c, "The library '%s' is imported as `#import \"%s\"` (its 'import').", in->libs[i].name, in->libs[i].import);
    imports = fcListNew(A->c);
    for (i = 0; i < in->nlibs; i++) {
        bool dup = false;
        for (k = 0; k < imports->count; k++) if (svEqIC((const char*)imports->items[k], in->libs[i].import)) dup = true;
        if (!dup) fcListAdd(A->c, imports, (void*)in->libs[i].import);
    }
    if (imports->count > 1) qsort(imports->items, (size_t)imports->count, sizeof(void*), fcCmpIgnoreCase);
    fcBufStr(&b, "The libraries the project references are imported as: ");
    for (i = 0; i < imports->count; i++) fcBufFmt(&b, "%s\"%s\"", i ? ", " : "", (const char*)imports->items[i]);
    fcBufChar(&b, '.');
    return fcBufDone(A->c, &b);
}

/* the registry of RuntimeSession.CreateProjectDirectiveRegistry: a library of the project (or the project itself) before the extensions */
static void svAnalysisDirective(FcCtx* c, void* state, const char* name, FcList* args, int line, FcList* importNamespaces) {
    SvAnalyzer* A = (SvAnalyzer*)state;
    FcToken* arg = args->count > 0 ? (FcToken*)args->items[0] : NULL;
    if (streq0(name, "import") && arg && arg->type == TT_StringLiteral) {
        const char* hint;
        if (svIsOwn(A, arg->stringValue)) return;
        if (svTryImportLibrary(A, arg->stringValue)) {
            const SvLib* lib = svLibFind(A->in, arg->stringValue);
            if (lib && lib->ns && !fcListHasStr(importNamespaces, lib->ns)) fcListAdd(c, importNamespaces, (void*)lib->ns);
            return;
        }
        hint = svUnknownHint(A, arg->stringValue);
        if (hint) {
            FC_TRY(c) {
                fcLinkerDirective(c, A->L, name, args, line, importNamespaces);
            } FC_CATCH(c) {
                if (c->err.kind == FC_ERR_PRE) c->err.message = fcFmt(c, "%s %s", c->err.message, hint);
                fcRethrow(c);
            } FC_END_TRY;
            return;
        }
    }
    fcLinkerDirective(c, A->L, name, args, line, importNamespaces);
}

static FcProcessed* svPreprocessText(FcCtx* c, const uint16_t* text, int len, const char* folder) {
    uint16_t* copy = (uint16_t*)fcAlloc(c, sizeof(uint16_t) * (size_t)(len + 1));
    if (len) memcpy(copy, text, sizeof(uint16_t) * (size_t)len);
    return fcPreprocess(c, copy, len, folder);
}

static const char* svFolderOf(FcCtx* c, const char* path) { return fcDirectoryName(c, fcFullPath(c, ".", path)); }

static bool svIsBlank(const uint16_t* s, int n) { int i; for (i = 0; i < n; i++) if (!fcIsWhiteSpace(s[i])) return false; return true; }

/* BuildPlan.LibraryOrder */
static void svLibVisit(const SvAnalysisIn* in, const char* name, FcDict* visited, FcList* out, FcCtx* c) {
    const SvLib* lib = svLibFind(in, name);
    int i;
    if (!lib || !fcSetAdd(visited, svLowerAscii(c, lib->import))) return;
    for (i = 0; i < lib->requires->count; i++) svLibVisit(in, (const char*)lib->requires->items[i], visited, out, c);
    fcListAdd(c, out, (void*)lib);
}
static FcList* svLibraryOrder(SvAnalyzer* A) {
    FcList* out = fcListNew(A->c);
    FcDict visited;
    int i;
    FcList* names = fcListCopy(A->c, A->projectImports);
    fcDictInit(A->c, &visited, FC_KEY_STR);
    for (i = 0; i < names->count; i++) svLibVisit(A->in, (const char*)names->items[i], &visited, out, A->c);
    return out;
}

static int svCmpDiag(const void* a, const void* b) {
    const FcDiagnostic* p = *(const FcDiagnostic* const*)a;
    const FcDiagnostic* q = *(const FcDiagnostic* const*)b;
    int c = strcmp(p->file ? p->file : "", q->file ? q->file : "");
    if (c) return c;
    if (p->line != q->line) return p->line < q->line ? -1 : 1;
    return p->column - q->column;   /* (replaced below by the original order: a stable sort) */
}
typedef struct SvDiagAt { FcDiagnostic* d; int order; } SvDiagAt;
static int svCmpDiagAt(const void* a, const void* b) {
    const SvDiagAt* p = (const SvDiagAt*)a;
    const SvDiagAt* q = (const SvDiagAt*)b;
    int c = strcmp(p->d->file ? p->d->file : "", q->d->file ? q->d->file : "");
    if (c) return c;
    if (p->d->line != q->d->line) return p->d->line < q->d->line ? -1 : 1;
    return p->order - q->order;
}
static void svSortDiags(FcList* l) {
    SvDiagAt* a;
    int i;
    if (l->count < 2) return;
    a = (SvDiagAt*)malloc(sizeof(SvDiagAt) * (size_t)l->count);
    if (!a) fbOutOfMemory();
    for (i = 0; i < l->count; i++) { a[i].d = (FcDiagnostic*)l->items[i]; a[i].order = i; }
    qsort(a, (size_t)l->count, sizeof *a, svCmpDiagAt);
    for (i = 0; i < l->count; i++) l->items[i] = a[i].d;
    free(a);
    (void)svCmpDiag;
}

#define SV_OTHER_FILE_BROKEN 0x5EB0

/* Analyze(source, extraImports, basePath, plan, otherFiles) - not allFiles */
static FcList* svAnalyze(FcCtx* c, const char* preludePath, const char* storeRoot, const SvAnalysisIn* in, FcList* extraImports, bool withOthers) {
    FcList* volatile diagnostics = fcListNew(c);
    FcList* volatile processed = fcListNew(c);
    FcDict* volatile libraryPaths = fcDictNew(c, FC_KEY_STR);
    volatile int documentSource = -1;
    volatile bool otherBroken = false;
    SvAnalyzer A;
    FcLinker* L;
    volatile int i;
    bool hasOthers = withOthers && in->nothers > 0;
    if (svIsBlank(in->text, in->len)) return diagnostics;
    memset(&A, 0, sizeof A);
    A.c = c;
    A.in = in;
    A.projectImports = fcListNew(c);
    A.preludePath = preludePath;
    L = (FcLinker*)fcAlloc(c, sizeof *L);
    A.L = L;
    L->c = c;
    L->store.root = storeRoot;
    L->directiveIntWidth = L->directiveFloatWidth = FC_NULL_INT;
    fcDictInit(c, &L->nativeImports, FC_KEY_STR);
    fcDictInit(c, &L->natives, FC_KEY_STR);
    fcNativesInit(c, &L->registry);
    L->executionMode = 1;
#define FIRE_NATIVE(index, name, tryable) fcRegisterNative(L, name);
#include "../tables/natives.def"
#undef FIRE_NATIVE
    fcSetAdd(&L->nativeImports, "print");
    c->collecting = true;
    c->diags->count = 0;
    FC_TRY(c) {
        const uint16_t* src;
        int len;
        FcProcessed* p;
        FcProcessed** arr;
        FcList* libraryFiles;
        FcList* libraryPathList;
        FcResolveResult* resolved;
        FcList* program;
        const char* volatile cwd = in->base ? in->base : fcFullPath(c, ".", ".");
        fcSymbolsForHost(c, c->symbols);
        c->sharedIncluded = fcListNew(c);
        c->resourcePaths = fcListNew(c);
        c->resourceNames = fcListNew(c);
        c->directiveHook = svAnalysisDirective;
        c->directiveState = &A;
        if (extraImports)
            for (i = 0; i < extraImports->count; i++) {
                const char* name = (const char*)extraImports->items[i];
                if (svIsOwn(&A, name)) continue;
                if (svTryImportLibrary(&A, name)) continue;
                FC_TRY(c) {
                    FcList* keys = fcListNew(c);
                    int k;
                    fcWithDependencies(L, fcParseImportName(L, name), fcDictNew(c, FC_KEY_STR), keys);
                    for (k = 0; k < keys->count; k++) fcSetAdd(&L->nativeImports, keys->items[k]);
                } FC_CATCH(c) {
                } FC_END_TRY;
            }
        if (!fcReadSource(c, preludePath, &src, &len)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "cannot read the prelude '%s'", preludePath));
        fcListAdd(c, processed, fcPreprocess(c, src, len, cwd));
        if (hasOthers)
            for (i = 0; i < in->nothers; i++) {
                const SvFile* f = &in->others[i];
                if (svIsBlank(f->text, f->len)) continue;
                FC_TRY(c) {
                    FcProcessed* o = svPreprocessText(c, f->text, f->len, svFolderOf(c, f->path));
                    o->name = f->path;
                    fcListAdd(c, processed, o);
                } FC_CATCH(c) {
                } FC_END_TRY;
            }
        p = svPreprocessText(c, in->text, in->len, cwd);
        fcListAdd(c, processed, p);
        /* ProjectLibraries.Process */
        libraryFiles = fcListNew(c);
        libraryPathList = fcListNew(c);
        if (in->hasPlan && A.projectImports->count > 0) {
            FcDict done;
            FcList* order;
            bool more = true;
            FcDict filesOf;   /* import -> FcList of FcProcessed* */
            fcDictInit(c, &done, FC_KEY_STR);
            fcDictInit(c, &filesOf, FC_KEY_STR);
            while (more) {
                more = false;
                order = svLibraryOrder(&A);
                for (i = 0; i < order->count; i++) {
                    const SvLib* lib = (const SvLib*)order->items[i];
                    FcList* files;
                    int f;
                    if (!fcSetAdd(&done, svLowerAscii(c, lib->import))) continue;
                    more = true;
                    files = fcListNew(c);
                    for (f = 0; f < lib->nfiles; f++) {
                        FcProcessed* lp = svPreprocessText(c, lib->files[f].text, lib->files[f].len, svFolderOf(c, lib->files[f].path));
                        lp->name = fcFmt(c, "%s/%s", lib->name, fcBaseName(lib->files[f].path));
                        lp->module = lib->import;
                        lp->qualify = lib->ns;
                        fcListAdd(c, files, lp);
                        fcListAdd(c, files, (void*)lib->files[f].path);
                    }
                    fcDictPut(&filesOf, svLowerAscii(c, lib->import), files);
                }
            }
            order = svLibraryOrder(&A);
            for (i = 0; i < order->count; i++) {
                FcList* files = (FcList*)fcDictGet(&filesOf, svLowerAscii(c, ((const SvLib*)order->items[i])->import));
                int f;
                if (files) for (f = 0; f < files->count; f += 2) { fcListAdd(c, libraryFiles, files->items[f]); fcListAdd(c, libraryPathList, files->items[f + 1]); }
            }
        }
        fcInsertPreludes(L, processed);
        for (i = 0; i < libraryFiles->count; i++) {
            FcProcessed* lp = (FcProcessed*)libraryFiles->items[i];
            fcListInsert(c, processed, 1 + i, lp);
            fcDictPut(libraryPaths, lp->name, libraryPathList->items[i]);
        }
        documentSource = processed->count - 1;
        c->directiveHook = NULL;
        arr = (FcProcessed**)fcAlloc(c, sizeof(FcProcessed*) * (size_t)processed->count);
        for (i = 0; i < processed->count; i++) arr[i] = (FcProcessed*)processed->items[i];
        FC_TRY(c) {
            program = fcParseMultiple(c, arr, processed->count);
        } FC_CATCH(c) {
            if (hasOthers && ((c->err.kind == FC_ERR_PARSE && c->err.file) || c->err.kind == FC_ERR_LEX)) { c->err.kind = FC_ERR_INTERNAL; c->err.source = SV_OTHER_FILE_BROKEN; }
            fcRethrow(c);
        } FC_END_TRY;
        resolved = fcResolve(c, program, &L->natives, NULL);
        {
            FcList* found = fcTypeCheck(c, program, resolved, in->strict, documentSource, documentSource, 64, 64, fcNativeReturnTypes(L));
            for (i = 0; i < found->count; i++) fcListAdd(c, diagnostics, found->items[i]);
        }
        fcCompile(c, program, resolved, &L->registry, false);
    } FC_CATCH(c) {
        FcError* e = &c->err;
        if (e->kind == FC_ERR_INTERNAL && e->source == SV_OTHER_FILE_BROKEN) otherBroken = true;
        else if (hasOthers && (e->kind == FC_ERR_RESOLVE || e->kind == FC_ERR_COMPILE)) {
            FcArea area = e->kind == FC_ERR_RESOLVE ? FC_AREA_RESOLVER : FC_AREA_COMPILER;
            if (e->list)
                for (i = 0; i < e->list->count; i++) {
                    const FcError* x = (const FcError*)e->list->items[i];
                    if (x->source < 0 || x->source == documentSource) fcListAdd(c, diagnostics, fcDiagNew(c, x->line, x->message, FC_SEV_ERROR, fcCodeOf(c, area, x->message), NULL, 0));
                }
            else if (e->source < 0 || e->source == documentSource) fcListAdd(c, diagnostics, fcDiagNew(c, e->line, e->message, FC_SEV_ERROR, fcCodeOf(c, area, e->message), NULL, 0));
        } else {
            FcList* d = fcDiagnosticsOf(c, e, NULL);
            for (i = 0; i < d->count; i++) fcListAdd(c, diagnostics, d->items[i]);
        }
    } FC_END_TRY;
    c->directiveHook = NULL;
    if (otherBroken) return NULL;   /* the caller analyses the document alone */
    /* Sorted: the warnings of the document (and of the libraries), not those of the other files */
    for (i = 0; i < c->diags->count; i++) {
        FcDiagnostic* w = (FcDiagnostic*)c->diags->items[i];
        bool other = false;
        int k;
        if (w->file && hasOthers) for (k = 0; k < in->nothers; k++) if (strcmp(in->others[k].path, w->file) == 0) other = true;
        if (!other) fcListAdd(c, diagnostics, w);
    }
    for (i = 0; i < diagnostics->count; i++) {
        FcDiagnostic* d = (FcDiagnostic*)diagnostics->items[i];
        void* path;
        if (d->file && fcDictTry(libraryPaths, d->file, &path)) {
            FcDiagnostic* copy = (FcDiagnostic*)fcAlloc(c, sizeof *copy);
            *copy = *d;
            copy->file = (const char*)path;
            diagnostics->items[i] = copy;
        }
    }
    svSortDiags(diagnostics);
    return diagnostics;
}

/* "Unknown class 'X'" & co.: the name, NULL for another message */
static const char* svUnknownClassName(FcCtx* c, const char* m) {
    static const char* const heads[] = { "Unknown class '", "Unknown type '", "Unknown exception type '" };
    size_t i;
    for (i = 0; i < sizeof heads / sizeof heads[0]; i++)
        if (fcStartsWith(m, heads[i])) {
            const char* a = m + strlen(heads[i]);
            const char* b = strchr(a, '\'');
            if (b && b > a) return fcStrN(c, a, (size_t)(b - a));
        }
    if (m[0] == '\'') {
        const char* a = m + 1;
        const char* b = strchr(a, '\'');
        if (b && b > a && fcStartsWith(b, "' of class '")) {
            const char* d = strchr(b + 12, '\'');
            if (d && d > b + 12 && fcStartsWith(d, "' is neither a known class nor a known interface")) return fcStrN(c, a, (size_t)(b - a));
        }
    }
    return NULL;
}

/* a diagnostic into another context */
static FcDiagnostic* svCopyDiag(FcCtx* out, const FcDiagnostic* d) {
    FcDiagnostic* r = (FcDiagnostic*)fcAlloc(out, sizeof *r);
    *r = *d;
    r->message = fcStr(out, d->message);
    r->code = fcStr(out, d->code);
    r->file = fcStr(out, d->file);
    return r;
}

/* one Analyze in a context of its own (the parser keeps state on the context); NULL: another file of the project cannot be read */
static FcList* svAnalyzeOnce(FcCtx* out, const char* preludePath, const char* storeRoot, const SvAnalysisIn* in, FcList* extraImports, bool withOthers) {
    FcCtx* w = svNewCtx();
    FcList* found = svAnalyze(w, preludePath, storeRoot, in, extraImports, withOthers);
    FcList* r = NULL;
    int i;
    if (found) {
        r = fcListNew(out);
        for (i = 0; i < found->count; i++) fcListAdd(out, r, svCopyDiag(out, (const FcDiagnostic*)found->items[i]));
    }
    svFreeCtx(w);
    return r;
}

/* AnalyzeInProject (or Analyze alone without other files) */
static FcList* svAnalyzeInProject(FcCtx* c, SvLang* own, const char* preludePath, const char* storeRoot, const SvAnalysisIn* in) {
    FcList* importsElsewhere = fcListNew(c);
    FcList* diagnostics;
    FcDict known;
    FcList* kept;
    int i, k;
    bool any = false;
    for (i = 0; i < in->nothers; i++) {
        FcList* names;
        if (svIsBlank(in->others[i].text, in->others[i].len)) continue;
        names = svFindImportNames(c, in->others[i].text, in->others[i].len);
        for (k = 0; k < names->count; k++) {
            bool dup = false;
            int q;
            for (q = 0; q < importsElsewhere->count; q++) if (svEqIC((const char*)importsElsewhere->items[q], (const char*)names->items[k])) dup = true;
            if (!dup) fcListAdd(c, importsElsewhere, names->items[k]);
        }
    }
    diagnostics = svAnalyzeOnce(c, preludePath, storeRoot, in, importsElsewhere, true);
    if (!diagnostics) diagnostics = svAnalyzeOnce(c, preludePath, storeRoot, in, importsElsewhere, false);   /* OtherFileBroken: the document alone */
    if (diagnostics->count == 0 || in->nothers == 0) return diagnostics;
    for (i = 0; i < diagnostics->count; i++) if (svUnknownClassName(c, ((FcDiagnostic*)diagnostics->items[i])->message)) any = true;
    if (!any) return diagnostics;
    fcDictInit(c, &known, FC_KEY_STR);
    for (i = 0; i < in->nothers; i++) {
        SvIndex* x;
        if (svIsBlank(in->others[i].text, in->others[i].len)) continue;
        x = svIndexNew(own, in->others[i].text, in->others[i].len);
        svFindUsings(x);
        svHarvest(x);
        FC_DICT_EACH(&x->classes, e) { SvClass* kl = (SvClass*)e->value; fcSetAdd(&known, fcStr(c, kl->name)); fcSetAdd(&known, fcStr(c, kl->simpleName)); }
        FC_DICT_EACH(&x->enumMembers, e) { fcSetAdd(&known, fcStr(c, (const char*)e->key)); fcSetAdd(&known, fcStr(c, svAfterLastDot((const char*)e->key))); }
        svIndexFree(own, x);
    }
    if (fcDictCount(&known) == 0) return diagnostics;
    kept = fcListNew(c);
    for (i = 0; i < diagnostics->count; i++) {
        FcDiagnostic* d = (FcDiagnostic*)diagnostics->items[i];
        const char* name = svUnknownClassName(c, d->message);
        if (!(name && fcDictHas(&known, name))) fcListAdd(c, kept, d);
    }
    return kept;
}
