/* native/forge/gen/packages.c - what the generator reads of the natives of an installed package (commit 808e9788): from
 * src/fire.Package.Manager/Manifest.cs PackageNative, PackageNativeFunction and PackageNativeExtensions (SourcesFor, PlatformLayerFor, LinkLibrariesFor,
 * SupportsAny), from NativeSources.cs NativeSourceList, NativeUse, NativeHeaders (Point, Inline) and NativeSourceOrder (FileNameOf, Sort, CheckIncludes),
 * from PackageStore.cs InstalledImport.ReadNativeSources (with StripLocalIncludes, StripPragmaOnce, UsedImports), and src/fire.Native/NativeCTypes.cs.
 * The manifest is read again here (linker.c keeps only what the linker needs of it); the regular expressions of the C# are written out per line. */

/* NativeSourceList: the files in their order, with their build orders (INT_MIN: none) */
typedef struct FgSrcList { FcList* files; FcList* orders; } FgSrcList;
typedef struct FgKeyed { const char* key; void* value; } FgKeyed;
typedef struct FgNativeUse { const char* import; FcList* platforms; } FgNativeUse;
typedef struct FgPkgFn {
    const char* name; int arguments; const char* cpp; const char* cfn; bool needsList, returnsReference, host;
    const char* returns;     /* NULL: none */
    FcList* parameters;      /* NULL: none */
} FgPkgFn;
typedef struct FgPkgNativeM {
    const char* language; bool isC;
    FgSrcList* sources;
    FcList* platformSources;   /* FgKeyed (FgSrcList*), the order of the manifest */
    FcList* platforms;
    FcList* linkLibraries;     /* FgKeyed (FcList* of names) */
    FcList* functions;         /* FgPkgFn* */
    FcList* bridge;            /* FgPkgFn*: the natives of the display (graphics, windows) */
    FcList* exceptions;
    bool threadSafe;
    FcList* uses;              /* FgNativeUse* */
    FcList* headers;
    FcList* units;             /* C files compiled on their own and linked in (`native.units`, D8: libforge in the package forge) - forge-c only */
} FgPkgNativeM;
/* InstalledImport (with its package) */
typedef struct FgInstImport {
    const char* name; const char* pkgName; const char* pkgVersion; const char* dir; FgPkgNativeM* native;
    const char* buildDirectory; const char* descriptionFile;   /* the natives of a project (ProjectNativeOverlay, Phase D 3): obj/native, the project file; NULL: a package */
} FgInstImport;

static const char* fgPkgFnSymbol(const FgPkgFn* fn) { return fn->cfn && *fn->cfn ? fn->cfn : fn->cpp; }

static FcList* fgJsonStrings(FcCtx* c, const FcJson* v) {
    FcList* l = fcListNew(c);
    int i;
    if (v && v->kind == FJ_ARR) for (i = 0; i < v->items->count; i++) if (((FcJson*)v->items->items[i])->kind == FJ_STR) fcListAdd(c, l, (void*)((FcJson*)v->items->items[i])->s);
    return l;
}
static FgSrcList* fgJsonSrcList(FcCtx* c, const FcJson* v) {
    FgSrcList* l = (FgSrcList*)fcAlloc(c, sizeof *l);
    int i;
    l->files = fcListNew(c); l->orders = fcListNew(c);
    if (!v || v->kind != FJ_ARR) return l;
    for (i = 0; i < v->items->count; i++) {
        FcJson* e = (FcJson*)v->items->items[i];
        if (e->kind == FJ_STR) { fcListAdd(c, l->files, (void*)e->s); fcListAdd(c, l->orders, FC_IVAL(INT_MIN)); }
        else if (e->kind == FJ_OBJ && fcJsonStr(e, "file")) {
            FcJson* o = fcJsonGet(e, "order");
            fcListAdd(c, l->files, (void*)fcJsonStr(e, "file"));
            fcListAdd(c, l->orders, FC_IVAL(o && o->kind == FJ_NUM ? (int)o->n : INT_MIN));
        }
    }
    return l;
}
static int fgSrcOrderOf(const FgSrcList* l, const char* file) {
    int i, r = INT_MIN;
    for (i = 0; i < l->files->count; i++) if (streq0((const char*)l->files->items[i], file)) r = FC_VALI(l->orders->items[i]);   /* (the last entry of a name wins, as the dictionary) */
    return r;
}
static FgPkgNativeM* fgJsonNative(FcCtx* c, const FcJson* v) {
    FgPkgNativeM* n = (FgPkgNativeM*)fcAlloc(c, sizeof *n);
    FcJson* x;
    int i;
    n->language = fcJsonStr(v, "language");
    n->isC = n->language && streq0(n->language, "c");
    n->sources = fgJsonSrcList(c, fcJsonGet(v, "sources"));
    n->platformSources = fcListNew(c);
    if ((x = fcJsonGet(v, "platformSources")) != NULL && x->kind == FJ_OBJ)
        for (i = 0; i < x->keys->count; i++) {
            FgKeyed* k = (FgKeyed*)fcAlloc(c, sizeof *k);
            k->key = (const char*)x->keys->items[i]; k->value = fgJsonSrcList(c, (FcJson*)x->items->items[i]);
            fcListAdd(c, n->platformSources, k);
        }
    n->platforms = fgJsonStrings(c, fcJsonGet(v, "platforms"));
    n->linkLibraries = fcListNew(c);
    if ((x = fcJsonGet(v, "linkLibraries")) != NULL && x->kind == FJ_OBJ)
        for (i = 0; i < x->keys->count; i++) {
            FgKeyed* k = (FgKeyed*)fcAlloc(c, sizeof *k);
            k->key = (const char*)x->keys->items[i]; k->value = fgJsonStrings(c, (FcJson*)x->items->items[i]);
            fcListAdd(c, n->linkLibraries, k);
        }
    n->functions = fcListNew(c);
    n->bridge = fcListNew(c);
    for (int list = 0; list < 2; list++)
    if ((x = fcJsonGet(v, list == 0 ? "functions" : "bridge")) != NULL && x->kind == FJ_ARR)
        for (i = 0; i < x->items->count; i++) {
            FcJson* e = (FcJson*)x->items->items[i];
            FgPkgFn* fn = (FgPkgFn*)fcAlloc(c, sizeof *fn);
            FcJson* y;
            fn->name = fcJsonStr(e, "name") ? fcJsonStr(e, "name") : "";
            fn->arguments = (y = fcJsonGet(e, "arguments")) && y->kind == FJ_NUM ? (int)y->n : 0;
            fn->cpp = fcJsonStr(e, "cpp") ? fcJsonStr(e, "cpp") : "";
            fn->cfn = fcJsonStr(e, "c");
            fn->needsList = (y = fcJsonGet(e, "needsList")) && y->kind == FJ_BOOL && y->b;
            fn->returnsReference = (y = fcJsonGet(e, "returnsReference")) && y->kind == FJ_BOOL && y->b;
            fn->host = (y = fcJsonGet(e, "host")) && y->kind == FJ_BOOL && y->b;
            fn->returns = fcJsonStr(e, "returns");
            fn->parameters = (y = fcJsonGet(e, "parameters")) && y->kind == FJ_ARR ? fgJsonStrings(c, y) : NULL;
            fcListAdd(c, list == 0 ? n->functions : n->bridge, fn);
        }
    n->exceptions = fgJsonStrings(c, fcJsonGet(v, "exceptions"));
    n->threadSafe = (x = fcJsonGet(v, "threadSafe")) && x->kind == FJ_BOOL && x->b;
    n->uses = fcListNew(c);
    if ((x = fcJsonGet(v, "uses")) != NULL && x->kind == FJ_ARR)
        for (i = 0; i < x->items->count; i++) {
            FcJson* e = (FcJson*)x->items->items[i];
            FgNativeUse* u = (FgNativeUse*)fcAlloc(c, sizeof *u);
            if (e->kind == FJ_STR) { u->import = e->s; u->platforms = fcListNew(c); }
            else if (e->kind == FJ_OBJ && fcJsonStr(e, "import")) { u->import = fcJsonStr(e, "import"); u->platforms = fgJsonStrings(c, fcJsonGet(e, "platforms")); }
            else continue;
            fcListAdd(c, n->uses, u);
        }
    n->headers = fgJsonStrings(c, fcJsonGet(v, "headers"));
    n->units = fgJsonStrings(c, fcJsonGet(v, "units"));
    return n;
}
static FcJson* fgReadJsonFile(FcCtx* c, const char* path) {
    size_t len = 0;
    uint8_t* text = fbReadFile(path, &len);
    FcJsonReader j;
    FcJson* v;
    if (!text) return NULL;
    text = (uint8_t*)realloc(text, len + 1);
    text[len] = 0;
    j.c = c; j.p = (const char*)text; j.bad = false;
    if (len >= 3 && text[0] == 0xEF && text[1] == 0xBB && text[2] == 0xBF) j.p += 3;
    v = fcJsonValue(&j);
    free(text);
    return j.bad ? NULL : v;
}
/* File.ReadAllText: the text without a byte order mark (NULL: it cannot be read) */
static const char* fgReadText(FcCtx* c, const char* path) {
    size_t len = 0;
    uint8_t* text = fbReadFile(path, &len);
    const char* r;
    if (!text) return NULL;
    r = len >= 3 && text[0] == 0xEF && text[1] == 0xBB && text[2] == 0xBF ? fcStrN(c, (const char*)text + 3, len - 3) : fcStrN(c, (const char*)text, len);
    free(text);
    return r;
}
/* InstalledImport.FullPath: a file of an import - relative to the package's folder, or rooted (the natives of a project have absolute paths, Phase D 3) */
static const char* fgPkgFile(FcCtx* c, const char* dir, const char* file) { return file[0] == '/' ? file : fcFmt(c, "%s/%s", dir, file); }
/* The InstalledImport of an import of the store (its package's manifest read again) */
static FgInstImport* fgInstalledImport(FcCtx* c, FcDict* cache, const FcPkgImport* pi) {
    FgInstImport* r = (FgInstImport*)fcDictGet(cache, pi);
    FcJson* manifest;
    FcJson* imports;
    int k;
    if (r) return r;
    r = (FgInstImport*)fcAlloc(c, sizeof *r);
    r->name = pi->name; r->dir = pi->dir;
    if (pi->overlay) {
        /* (a project's natives, ProjectNativeOverlay: the package "project:<name>" that is in the store's overlay, not on the disk) */
        r->pkgName = pi->packageName; r->pkgVersion = pi->packageVersion ? pi->packageVersion : "";
        r->native = (FgPkgNativeM*)pi->overlayNative;
        r->buildDirectory = pi->overlayBuildDir; r->descriptionFile = pi->overlayFile;
        fcDictPut(cache, pi, r);
        return r;
    }
    manifest = fgReadJsonFile(c, fcFmt(c, "%s/package.json", pi->dir));
    r->pkgName = manifest ? (fcJsonStr(manifest, "name") ? fcJsonStr(manifest, "name") : "") : "";
    r->pkgVersion = manifest ? (fcJsonStr(manifest, "version") ? fcJsonStr(manifest, "version") : "") : "";
    imports = manifest ? fcJsonGet(manifest, "imports") : NULL;
    if (imports && imports->kind == FJ_ARR)
        for (k = 0; k < imports->items->count; k++) {
            FcJson* im = (FcJson*)imports->items->items[k];
            FcJson* nv;
            if (!fcJsonStr(im, "name") || !streq0(fcJsonStr(im, "name"), pi->name)) continue;
            if ((nv = fcJsonGet(im, "native")) != NULL && nv->kind == FJ_OBJ) r->native = fgJsonNative(c, nv);
            break;
        }
    fcDictPut(cache, pi, r);
    return r;
}

/* ---- PackageNativeExtensions ---- */
static bool fgListHasIgnoreCase(const FcList* l, const char* s) { int i; for (i = 0; i < l->count; i++) if (fcAsciiIEq((const char*)l->items[i], s)) return true; return false; }
static bool fgPlatformHas(const FgPkgNativeM* n, const char* key) { int i; for (i = 0; i < n->platformSources->count; i++) if (fcAsciiIEq(((FgKeyed*)n->platformSources->items[i])->key, key)) return true; return false; }
static FcList* fgSourcesFor(FcCtx* c, const FgPkgNativeM* n, FcList* platformKeys, bool layerOnly) {
    FcList* files = fcListNew(c);
    FcList* orders = fcListNew(c);
    FcDict* seen = fcDictNew(c, FC_KEY_STR);
    FcList* keys = fcListNew(c);
    FcList* result = fcListNew(c);
    bool any = false, vm = false;
    int i, k, f;
    for (i = 0; i < platformKeys->count; i++) if (fcAsciiIEq((const char*)platformKeys->items[i], "vm")) vm = true;
    if (vm && fgPlatformHas(n, "vm")) fcListAdd(c, keys, (void*)"vm");
    else for (i = 0; i < platformKeys->count; i++) if (!fcAsciiIEq((const char*)platformKeys->items[i], "vm")) fcListAdd(c, keys, platformKeys->items[i]);
    for (i = 0; i < keys->count; i++) if (fgPlatformHas(n, (const char*)keys->items[i])) any = true;
    if (!any) { keys = fcListNew(c); fcListAdd(c, keys, (void*)"default"); }
    for (i = 0; i < keys->count; i++)
        for (k = 0; k < n->platformSources->count; k++) {
            FgKeyed* e = (FgKeyed*)n->platformSources->items[k];
            FgSrcList* l = (FgSrcList*)e->value;
            if (!fcAsciiIEq(e->key, (const char*)keys->items[i])) continue;
            for (f = 0; f < l->files->count; f++) if (fcSetAdd(seen, l->files->items[f])) { int o = fgSrcOrderOf(l, (const char*)l->files->items[f]); fcListAdd(c, files, l->files->items[f]); fcListAdd(c, orders, FC_IVAL(o == INT_MIN ? 0 : o)); }
        }
    if (!layerOnly)
        for (f = 0; f < n->sources->files->count; f++) if (fcSetAdd(seen, n->sources->files->items[f])) { int o = fgSrcOrderOf(n->sources, (const char*)n->sources->files->items[f]); fcListAdd(c, files, n->sources->files->items[f]); fcListAdd(c, orders, FC_IVAL(o == INT_MIN ? 0 : o)); }
    /* NativeSourceOrder.Sort: by the order, stable */
    {
        int* idx = (int*)fcAlloc(c, sizeof(int) * (size_t)(files->count + 1));
        for (i = 0; i < files->count; i++) idx[i] = i;
        for (i = 1; i < files->count; i++)
            for (k = i; k > 0 && FC_VALI(orders->items[idx[k - 1]]) > FC_VALI(orders->items[idx[k]]); k--) { int t = idx[k]; idx[k] = idx[k - 1]; idx[k - 1] = t; }
        for (i = 0; i < files->count; i++) fcListAdd(c, result, files->items[idx[i]]);
    }
    return result;
}
static FcList* fgLinkLibrariesFor(FcCtx* c, const FgPkgNativeM* n, FcList* platformKeys) {
    FcList* r = fcListNew(c);
    int i, k, l;
    for (i = 0; i < platformKeys->count; i++)
        for (k = 0; k < n->linkLibraries->count; k++) {
            FgKeyed* e = (FgKeyed*)n->linkLibraries->items[k];
            FcList* libs = (FcList*)e->value;
            if (!fcAsciiIEq(e->key, (const char*)platformKeys->items[i])) continue;
            for (l = 0; l < libs->count; l++) if (!fcListHasStr(r, (const char*)libs->items[l])) fcListAdd(c, r, libs->items[l]);
        }
    return r;
}
static bool fgSupportsAny(const FgPkgNativeM* n, FcList* platformKeys) {
    int i;
    if (n->platforms->count == 0) return true;
    for (i = 0; i < platformKeys->count; i++) {
        if (fgListHasIgnoreCase(n->platforms, (const char*)platformKeys->items[i])) return true;
        if (fgPlatformHas(n, (const char*)platformKeys->items[i])) return true;
    }
    return fgPlatformHas(n, "default");
}
static bool fgUseAppliesTo(const FgNativeUse* u, FcList* platformKeys) {
    int i;
    if (u->platforms->count == 0) return true;
    for (i = 0; i < platformKeys->count; i++) if (fgListHasIgnoreCase(u->platforms, (const char*)platformKeys->items[i])) return true;
    return false;
}

/* ---- NativeSourceOrder, NativeHeaders ---- */
static const char* fgFileNameOf(FcCtx* c, const char* source) {
    const char* s = fgReplace(c, source, "\\", "/");
    const char* slash = strrchr(s, '/');
    return slash ? slash + 1 : s;
}
static const char* fgSkipBlanks(const char* p) { while (*p == ' ' || *p == '\t') p++; return p; }
/* `^[ \t]*#[ \t]*include[ \t]+"(name)"` at the start of a line: the position of the name and its length (false: no such line) */
static bool fgIncludeAt(const char* line, const char* lineEnd, bool spaceAfterHash, const char** name, int* nameLen, const char** afterQuote) {
    const char* p = fgSkipBlanks(line);
    const char* q;
    if (*p != '#') return false;
    p++;
    if (spaceAfterHash) p = fgSkipBlanks(p);
    if (strncmp(p, "include", 7) != 0) return false;
    p += 7;
    if (*p != ' ' && *p != '\t') return false;
    p = fgSkipBlanks(p);
    if (*p != '"') return false;
    p++;
    for (q = p; q < lineEnd && *q != '"' && *q != '\r' && *q != '\n'; q++) {}
    if (q == p || q >= lineEnd || *q != '"') return false;
    *name = p; *nameLen = (int)(q - p); *afterQuote = q + 1;
    return true;
}
/* the lines of a text (split at '\n'; a line keeps its '\r') */
static FcList* fgLinesOf(FcCtx* c, const char* text) { return fgSplit(c, text, '\n'); }
static bool fgWordChar(char ch) { return (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' || ((unsigned char)ch >= 0x80); }
/* `^[ \t]*#[ \t]*(\w+)[ \t]*(\w*)` */
static bool fgDirective(FcCtx* c, const char* line, const char** word, const char** arg) {
    const char* p = fgSkipBlanks(line);
    const char* s;
    if (*p != '#') return false;
    p = fgSkipBlanks(p + 1);
    for (s = p; fgWordChar(*p); p++) {}
    if (p == s) return false;
    *word = fcStrN(c, s, (size_t)(p - s));
    p = fgSkipBlanks(p);
    for (s = p; fgWordChar(*p); p++) {}
    *arg = fcStrN(c, s, (size_t)(p - s));
    return true;
}
/* `^[ \t]*#[ \t]*if(n?def)?\b` and `^[ \t]*#[ \t]*endif\b` */
static bool fgOpensIf(const char* line) {
    const char* p = fgSkipBlanks(line);
    if (*p != '#') return false;
    p = fgSkipBlanks(p + 1);
    if (strncmp(p, "if", 2) != 0) return false;
    if (strncmp(p + 2, "def", 3) == 0 && !fgWordChar(p[5])) return true;
    if (strncmp(p + 2, "ndef", 4) == 0 && !fgWordChar(p[6])) return true;
    return !fgWordChar(p[2]);
}
static bool fgClosesIf(const char* line) {
    const char* p = fgSkipBlanks(line);
    if (*p != '#') return false;
    p = fgSkipBlanks(p + 1);
    return strncmp(p, "endif", 5) == 0 && !fgWordChar(p[5]);
}
static int fgGuardDepth(FcCtx* c, FcList* lines) {
    const char* first = NULL;
    int i;
    for (i = 0; i < lines->count; i++) {
        const char* w;
        const char* a;
        if (!fgDirective(c, (const char*)lines->items[i], &w, &a)) continue;
        if (!first) { if (!streq0(w, "ifndef")) return 0; first = a; continue; }
        return streq0(w, "define") && streq0(a, first) ? 1 : 0;
    }
    return 0;
}
/* NativeHeaders.Point */
static const char* fgHeadersPoint(FcCtx* c, const char* text, FcDict* headers, FcDict* sources) {
    FcList* lines;
    FcBuf b = {0};
    int i;
    if (fcDictCount(headers) == 0) return text;
    lines = fgLinesOf(c, text);
    fcBufStr(&b, "");
    for (i = 0; i < lines->count; i++) {
        const char* line = (const char*)lines->items[i];
        const char* name;
        const char* after;
        int nameLen;
        const char* path;
        if (i) fcBufChar(&b, '\n');
        if (fgIncludeAt(line, line + strlen(line), true, &name, &nameLen, &after)) {
            const char* fileName = fgFileNameOf(c, fcStrN(c, name, (size_t)nameLen));
            if (!fcDictHas(sources, fileName) && (path = (const char*)fcDictGet(headers, fileName)) != NULL) {
                fcBufPut(&b, line, (size_t)(name - line));
                fcBufStr(&b, path);
                fcBufStr(&b, after - 1);
                continue;
            }
        }
        fcBufStr(&b, line);
    }
    return fcBufDone(c, &b);
}
/* NativeHeaders.Inline */
static const char* fgHeadersInline(FcCtx* c, const char* text, FcDict* headers, FcDict* sources, FcDict* placed, int outer, int nesting) {
    FcList* lines;
    FcBuf b = {0};
    int guard, depth = 0, i;
    if (fcDictCount(headers) == 0) return text;
    lines = fgLinesOf(c, text);
    guard = fgGuardDepth(c, lines);
    fcBufStr(&b, "");
    for (i = 0; i < lines->count; i++) {
        const char* line = (const char*)lines->items[i];
        const char* name;
        const char* after;
        int nameLen;
        const char* path;
        bool done = false;
        if (fgIncludeAt(line, line + strlen(line), true, &name, &nameLen, &after)) {
            const char* written = fcStrN(c, name, (size_t)nameLen);
            const char* fileName = fgFileNameOf(c, written);
            if (!fcDictHas(sources, fileName) && (path = (const char*)fcDictGet(headers, fileName)) != NULL) {
                int level = outer + (depth - guard > 0 ? depth - guard : 0);
                if (level == 0 && !fcSetAdd(placed, fileName)) fcBufFmt(&b, "// (in this file already) #include \"%s\"", written);
                else {
                    const char* inner = fgReadText(c, path);
                    const char* r;
                    size_t n;
                    if (nesting > 32) fcThrow(c, FC_ERR_PACKAGE, 0, 0, NULL, fcFmt(c, "The headers include each other without end ('%s').", fileName));
                    r = fgHeadersInline(c, fgReplace(c, inner ? inner : "", "\r\n", "\n"), headers, sources, placed, level, nesting + 1);
                    n = strlen(r);
                    while (n > 0 && r[n - 1] == '\n') n--;
                    fcBufFmt(&b, "/* ---- %s (a header, put in where it is included) */\n", fileName);
                    fcBufPut(&b, r, n);
                    fcBufChar(&b, '\n');
                    fcBufFmt(&b, "/* ---- end of %s */", fileName);
                }
                done = true;
            }
        }
        if (!done) fcBufStr(&b, line);
        if (i < lines->count - 1) fcBufChar(&b, '\n');
        if (fgOpensIf(line)) depth++;
        else if (fgClosesIf(line)) depth--;
    }
    return fcBufDone(c, &b);
}
/* InstalledImport.StripLocalIncludes: `^([ \t]*)#include[ \t]+"(x)"[ \t]*(?:/\*.*\*\/[ \t]*|//.*)?\r?$` of a source of the build */
static const char* fgStripLocalIncludes(FcCtx* c, const char* text, FcDict* names) {
    FcList* lines = fgLinesOf(c, text);
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = 0; i < lines->count; i++) {
        const char* line = (const char*)lines->items[i];
        const char* name;
        const char* after;
        int nameLen;
        bool match = false;
        if (i) fcBufChar(&b, '\n');
        if (fgIncludeAt(line, line + strlen(line), false, &name, &nameLen, &after)) {
            const char* rest = fgSkipBlanks(after);
            size_t n = strlen(rest);
            if (n == 0 || (n == 1 && rest[0] == '\r') || strncmp(rest, "//", 2) == 0) match = true;
            else if (strncmp(rest, "/*", 2) == 0) {
                if (n > 0 && rest[n - 1] == '\r') n--;
                while (n > 0 && (rest[n - 1] == ' ' || rest[n - 1] == '\t')) n--;
                match = n >= 4 && rest[n - 2] == '*' && rest[n - 1] == '/';
            }
            if (match) {
                const char* written = fcStrN(c, name, (size_t)nameLen);
                const char* slash = strrchr(written, '/');
                if (fcDictHas(names, slash ? slash + 1 : written)) {
                    const char* indent = line;
                    while (*indent == ' ' || *indent == '\t') indent++;
                    fcBufPut(&b, line, (size_t)(indent - line));
                    fcBufFmt(&b, "// (in this file already) #include \"%s\"", written);
                    continue;
                }
            }
        }
        fcBufStr(&b, line);
    }
    return fcBufDone(c, &b);
}
/* InstalledImport.StripPragmaOnce: `^[ \t]*#pragma[ \t]+once[ \t]*\r?$\n?` */
static const char* fgStripPragmaOnce(FcCtx* c, const char* text) {
    FcList* lines = fgLinesOf(c, text);
    FcBuf b = {0};
    int i;
    bool pendingNewline = false;
    fcBufStr(&b, "");
    for (i = 0; i < lines->count; i++) {
        const char* line = (const char*)lines->items[i];
        const char* p = fgSkipBlanks(line);
        bool last = i == lines->count - 1;
        if (pendingNewline) { fcBufChar(&b, '\n'); pendingNewline = false; }
        if (strncmp(p, "#pragma", 7) == 0 && (p[7] == ' ' || p[7] == '\t')) {
            const char* q = fgSkipBlanks(p + 7);
            if (strncmp(q, "once", 4) == 0) {
                q = fgSkipBlanks(q + 4);
                if (*q == 0 || (q[0] == '\r' && q[1] == 0)) continue;   /* (the line and its '\n' go) */
            }
        }
        fcBufStr(&b, line);
        if (!last) pendingNewline = true;
    }
    return fcBufDone(c, &b);
}

/* InstalledImport.UsedImports */
static FcList* fgUsedImports(FcCtx* c, FcDict* cache, FcStore* store, const FgInstImport* imp, FcList* keys) {
    FcList* r = fcListNew(c);
    int i, k;
    if (!imp->native || imp->native->uses->count == 0) return r;
    if (!store->loaded) fcStoreLoad(c, store);
    for (i = 0; i < imp->native->uses->count; i++) {
        FgNativeUse* use = (FgNativeUse*)imp->native->uses->items[i];
        FgInstImport* found = NULL;
        if (!fgUseAppliesTo(use, keys)) continue;
        for (k = 0; k < store->imports->count && !found; k++) {
            FcPkgImport* pi = (FcPkgImport*)store->imports->items[k];
            FgInstImport* other;
            if (!fcAsciiIEq(pi->name, use->import)) continue;
            other = fgInstalledImport(c, cache, pi);
            if (other->native) found = other;
        }
        if (!found) fcThrow(c, FC_ERR_PACKAGE, 0, 0, NULL, fcFmt(c, "The natives of '%s' use the platform layer of '%s', which is not installed.", imp->name, use->import));
        fcListAdd(c, r, found);
    }
    return r;
}
typedef struct FgNamedText { const char* name; const char* text; } FgNamedText;
/* (ReadNativeSources.Display: a source shown relative to the package - or the project - when its path is in that folder) */
static const char* fgSourceDisplay(FcCtx* c, const FgInstImport* imp, const char* name) {
    size_t n;
    if (!(name[0] == '/' || name[0] == '\\' || (name[0] && name[1] == ':')) || !imp->dir) return name;
    n = strlen(imp->dir);
    while (n > 0 && (imp->dir[n - 1] == '/' || imp->dir[n - 1] == '\\')) n--;
    if (strncmp(name, imp->dir, n) != 0 || !(name[n] == '/' || name[n] == '\\')) return name;
    return fgReplace(c, name + n + 1, "\\", "/");
}
/* InstalledImport.ReadNativeSources */
static FcList* fgReadNativeSources(FcCtx* c, FcDict* cache, FcStore* store, const FgInstImport* imp, FcList* keys) {
    FcList* used = fcListNew(c);
    FcList* texts = fcListNew(c);
    FcList* all = fcListNew(c);
    FcList* usedImports;
    FcList* sources;
    FcList* result = fcListNew(c);
    FcDict* names = fcDictNew(c, FC_KEY_STR);
    FcDict* inStore = fcDictNew(c, FC_KEY_STR);
    FcDict* inlineH = fcDictNew(c, FC_KEY_STR);
    FcDict* placed = fcDictNew(c, FC_KEY_STR);
    FcList* withSelf;
    int i, k, m;
    if (!imp->native) return result;
    usedImports = fgUsedImports(c, cache, store, imp, keys);
    for (i = 0; i < usedImports->count; i++) {
        FgInstImport* other = (FgInstImport*)usedImports->items[i];
        FcList* layer = fgSourcesFor(c, other->native, keys, true);
        for (k = 0; k < layer->count; k++) {
            const char* name = fcFmt(c, "%s/%s", other->name, (const char*)layer->items[k]);
            bool have = false;
            for (m = 0; m < used->count; m++) if (streq0(((FgNamedText*)used->items[m])->name, name)) have = true;
            if (!have) {
                FgNamedText* t = (FgNamedText*)fcAlloc(c, sizeof *t);
                const char* text = fgReadText(c, fgPkgFile(c, other->dir, (const char*)layer->items[k]));
                if (!text) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "unported: the source %s cannot be read", name));
                t->name = name; t->text = text;
                fcListAdd(c, used, t);
            }
        }
    }
    sources = fgSourcesFor(c, imp->native, keys, false);
    for (i = 0; i < sources->count; i++) {
        FgNamedText* t = (FgNamedText*)fcAlloc(c, sizeof *t);
        t->name = (const char*)sources->items[i];
        t->text = fgReadText(c, fgPkgFile(c, imp->dir, t->name));
        if (!t->text) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "unported: the source %s cannot be read", t->name));
        fcListAdd(c, texts, t);
    }
    /* NativeSourceOrder.CheckIncludes: a source that includes one built after it - a PackageException naming the file and the line of each `#include` */
    {
        FcList* problems = fcListNew(c);
        for (i = 0; i < texts->count; i++) {
            FcList* lines = fgLinesOf(c, ((FgNamedText*)texts->items[i])->text);
            for (k = 0; k < lines->count; k++) {
                const char* line = (const char*)lines->items[k];
                const char* name;
                const char* after;
                const char* written;
                const char* fileName;
                int nameLen, at = -1;
                if (!fgIncludeAt(line, line + strlen(line), true, &name, &nameLen, &after)) continue;
                written = fcStrN(c, name, (size_t)nameLen);
                fileName = fgFileNameOf(c, written);
                /* (the position of a file name: its first source; not a source of the list, or built before it - or itself: fine) */
                for (m = 0; m < texts->count && at < 0; m++) if (streq0(fgFileNameOf(c, ((FgNamedText*)texts->items[m])->name), fileName)) at = m;
                if (at <= i) continue;
                fcListAdd(c, problems, (void*)fcFmt(c, "%s:%d: #include \"%s\" is a source of the same package that is built after it - give '%s' a smaller build order (\"order\") than '%s', or list it before.",
                    fgSourceDisplay(c, imp, ((FgNamedText*)texts->items[i])->name), k + 1, written, fgSourceDisplay(c, imp, ((FgNamedText*)texts->items[at])->name),
                    fgSourceDisplay(c, imp, ((FgNamedText*)texts->items[i])->name)));
            }
        }
        if (problems->count > 0) fcThrow(c, FC_ERR_PACKAGE, 0, 0, NULL, fcFmt(c, "The build order of the natives of '%s' is wrong:\n  %s", imp->name, fgJoin(c, problems, "\n  ")));
    }
    for (i = 0; i < sources->count; i++) fcSetAdd(names, fgFileNameOf(c, (const char*)sources->items[i]));
    for (i = 0; i < used->count; i++) fcSetAdd(names, fgFileNameOf(c, ((FgNamedText*)used->items[i])->name));
    withSelf = fcListNew(c);
    fcListAdd(c, withSelf, (void*)imp);
    for (i = 0; i < usedImports->count; i++) fcListAdd(c, withSelf, usedImports->items[i]);
    for (i = 0; i < withSelf->count; i++) {
        FgInstImport* h = (FgInstImport*)withSelf->items[i];
        for (k = 0; k < h->native->headers->count; k++) {
            const char* name = fgFileNameOf(c, (const char*)h->native->headers->items[k]);
            if (fcDictHas(inStore, name) || fcDictHas(inlineH, name)) continue;
            /* (every import of the store is in the store: IsInStore) */
            fcDictPut(inStore, name, (void*)fcFmt(c, "pkg/%s/%s", h->name, name));
        }
    }
    for (i = 0; i < used->count; i++) fcListAdd(c, all, used->items[i]);
    for (i = 0; i < texts->count; i++) fcListAdd(c, all, texts->items[i]);
    for (i = 0; i < all->count; i++) {
        FgNamedText* t = (FgNamedText*)all->items[i];
        FgNamedText* r = (FgNamedText*)fcAlloc(c, sizeof *r);
        r->name = t->name;
        r->text = fgStripLocalIncludes(c, fgStripPragmaOnce(c, fgHeadersInline(c, fgHeadersPoint(c, t->text, inStore, names), inlineH, names, placed, 0, 0)), names);
        fcListAdd(c, result, r);
    }
    return result;
}

/* ---- NativeCTypes ---- */
static const char* fgNativeCType(const char* type) {
    const char* t;
    if (!type) return NULL;
    t = type;
    while (*t == ' ' || *t == '\t' || *t == '\n' || *t == '\r') t++;
    if (fcStartsWith(t, "int[64]") || fcStartsWith(t, "int") || fcStartsWith(t, "int[32]") || fcStartsWith(t, "int[16]") || fcStartsWith(t, "float") || fcStartsWith(t, "bool")) {
        size_t n = strlen(t);
        while (n > 0 && (t[n - 1] == ' ' || t[n - 1] == '\t' || t[n - 1] == '\n' || t[n - 1] == '\r')) n--;
        if ((n == 3 && !strncmp(t, "int", 3)) || (n == 7 && !strncmp(t, "int[64]", 7))) return "int64_t";
        if (n == 7 && !strncmp(t, "int[32]", 7)) return "int32_t";
        if (n == 7 && !strncmp(t, "int[16]", 7)) return "int16_t";
        if (n == 5 && !strncmp(t, "float", 5)) return "Real";
        if (n == 4 && !strncmp(t, "bool", 4)) return "bool";
    }
    return NULL;
}
static bool fgNativeIsTyped(const FgPkgFn* fn, bool isC) { return isC && fn->parameters != NULL; }
static const char* fgNativeParamCType(const FgPkgFn* fn, int i) { return fn->parameters && i < fn->parameters->count ? fgNativeCType((const char*)fn->parameters->items[i]) : NULL; }
static const char* fgNativeReturnCType(const FgPkgFn* fn) { return fn->parameters ? fgNativeCType(fn->returns) : NULL; }
static const char* fgNativeToValue(FcCtx* c, const char* cType, const char* expr) {
    return streq0(cType, "Real") ? fcFmt(c, "Float((Real)(%s))", expr) : streq0(cType, "bool") ? fcFmt(c, "Bool(%s)", expr) : fcFmt(c, "Int((int64_t)(%s))", expr);
}
/* CppGenerator.PackageNativeText: the C of a package as the generated file holds it (its ints are native ones; its `callLam` is fire_cbCall) */
static const char* fgPackageNativeText(FcCtx* c, const char* text) {
    return fcFmt(c, "#undef FIRE_INT_U\n#undef FIRE_INT_1\n#define FIRE_INT_U IntNativeU\n#define FIRE_INT_1 IntNative\n#define callLam fire_cbCall\n%s"
        "\n#undef callLam\n#undef FIRE_INT_U\n#undef FIRE_INT_1\n#define FIRE_INT_U fire_IntU\n#define FIRE_INT_1 fire_Int1", text);
}
#define PackageNativeText(g, text) fgPackageNativeText((g)->c, (text))
