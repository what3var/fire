/* native/forge/project/manifest.c - the description of a package, `package.json` (Phase D 3, commit de45e7e4): src/fire.Package.Manager/Manifest.cs
 * (PackageManifest with Parse, ToJson, Validate, IsDocumentName, the patterns of names; PackageImport, PackageNative, PackageNativeFunction;
 * SemanticVersion.TryParse) and from NativeSources.cs the converters of NativeSourceList and NativeUse and NativeSourceOrder.CheckIncludes/CheckAllPlatforms.
 * ToJson is JsonSerializer.Serialize with the options of PackageManifest - Utf8JsonWriter indented by two spaces, nulls left out, the encoder
 * UnsafeRelaxedJsonEscaping - byte for byte (an entry of a source list with a build order is a raw value: no new line before it, as .NET writes it). */

/* PackageNative: what the generator reads of it (FgPkgNativeM) and the rest */
typedef struct PkNative { FgPkgNativeM m; FcList* libraries /* FgKeyed (const char*) */; const char* reset; } PkNative;
typedef struct PkImport {
    const char* name; bool standard; const char* prelude; FcList* requires; PkNative* native;
    FcList* resourceKeys; FcList* resourceValues;   /* NULL: none */
    FcList* drivers; int serializable /* -1: null */;
} PkImport;
typedef struct PkManifest {
    int format; const char* name; const char* version; const char* author; const char* description; const char* license; const char* homepage;
    FcList* dependencies; FcList* imports /* PkImport* */; const char* templates; FcList* documents /* NULL: none */;
} PkManifest;

static FgSrcList* pkSrcListNew(FcCtx* c) { FgSrcList* l = (FgSrcList*)fcAlloc(c, sizeof *l); l->files = fcListNew(c); l->orders = fcListNew(c); return l; }
static void pkSrcAdd(FcCtx* c, FgSrcList* l, const char* file, int order) { fcListAdd(c, l->files, (void*)file); fcListAdd(c, l->orders, FC_IVAL(order)); }
/* NativeSourceList.Map: every entry mapped, the orders kept (OrderOf: the order of the entry's name, the last one set) */
static FgSrcList* pkSrcMap(FcCtx* c, const FgSrcList* l, const char* (*map)(void* state, const char* path), void* state) {
    FgSrcList* r = pkSrcListNew(c);
    int i;
    for (i = 0; i < l->files->count; i++) pkSrcAdd(c, r, map(state, (const char*)l->files->items[i]), fgSrcOrderOf(l, (const char*)l->files->items[i]));
    return r;
}
static PkNative* pkNativeNew(FcCtx* c) {
    PkNative* n = (PkNative*)fcAlloc(c, sizeof *n);
    n->m.sources = pkSrcListNew(c);
    n->m.platformSources = fcListNew(c);
    n->m.platforms = fcListNew(c);
    n->m.linkLibraries = fcListNew(c);
    n->m.functions = fcListNew(c);
    n->m.bridge = fcListNew(c);
    n->m.exceptions = fcListNew(c);
    n->m.uses = fcListNew(c);
    n->m.headers = fcListNew(c);
    n->m.units = fcListNew(c);
    n->libraries = fcListNew(c);
    return n;
}
static PkImport* pkImportNew(FcCtx* c, const char* name) {
    PkImport* i = (PkImport*)fcAlloc(c, sizeof *i);
    i->name = name; i->requires = fcListNew(c); i->serializable = -1;
    return i;
}
static PkManifest* pkManifestNew(FcCtx* c) {
    PkManifest* m = (PkManifest*)fcAlloc(c, sizeof *m);
    m->format = 1; m->name = ""; m->version = ""; m->author = ""; m->description = ""; m->license = ""; m->homepage = "";
    m->dependencies = fcListNew(c); m->imports = fcListNew(c);
    return m;
}

/* ---- Utf8JsonWriter, indented ---- */
typedef struct PkW { FcBuf b; int depth; bool first[64]; bool afterName; } PkW;
static void pkWIndent(PkW* w) { int i; fcBufChar(&w->b, '\n'); for (i = 0; i < w->depth; i++) fcBufStr(&w->b, "  "); }
/* before a value: the list separator and the new line, unless it follows its property name */
static void pkWBefore(PkW* w, bool raw) {
    if (w->afterName) { w->afterName = false; return; }
    if (w->depth == 0) return;
    if (!w->first[w->depth]) fcBufChar(&w->b, ',');
    w->first[w->depth] = false;
    if (!raw) pkWIndent(w);
}
static void pkWStart(PkW* w, char open) { pkWBefore(w, false); fcBufChar(&w->b, open); w->depth++; w->first[w->depth] = true; }
static void pkWEnd(PkW* w, char close) {
    bool empty = w->first[w->depth];
    w->depth--;
    if (!empty) pkWIndent(w);
    fcBufChar(&w->b, close);
}
static void pkWName(PkW* w, const char* name) {
    if (!w->first[w->depth]) fcBufChar(&w->b, ',');
    w->first[w->depth] = false;
    pkWIndent(w);
    fjEscape(&w->b, name, true);
    fcBufStr(&w->b, ": ");
    w->afterName = true;
}
static void pkWStr(PkW* w, const char* s) { pkWBefore(w, false); fjEscape(&w->b, s ? s : "", true); }
static void pkWInt(PkW* w, int n) { pkWBefore(w, false); fcBufFmt(&w->b, "%d", n); }
static void pkWBool(PkW* w, bool v) { pkWBefore(w, false); fcBufStr(&w->b, v ? "true" : "false"); }
static void pkWRaw(PkW* w, const char* raw) { pkWBefore(w, true); fcBufStr(&w->b, raw); }
static void pkWStrList(PkW* w, const FcList* l) {
    int i;
    pkWStart(w, '[');
    for (i = 0; l && i < l->count; i++) pkWStr(w, (const char*)l->items[i]);
    pkWEnd(w, ']');
}
/* NativeSourceListConverter.Write */
static void pkWSrcList(FcCtx* c, PkW* w, const FgSrcList* l) {
    int i;
    pkWStart(w, '[');
    for (i = 0; i < l->files->count; i++) {
        const char* f = (const char*)l->files->items[i];
        int o = fgSrcOrderOf(l, f);
        if (o != INT_MIN) {
            FcBuf e = {0};
            fcBufStr(&e, "");
            fjEscape(&e, f, true);
            pkWRaw(w, fcFmt(c, "{ \"file\": %s, \"order\": %d }", fcBufDone(c, &e), o));
        } else pkWStr(w, f);
    }
    pkWEnd(w, ']');
}
static void pkWFunctions(PkW* w, const FcList* l) {
    int i;
    pkWStart(w, '[');
    for (i = 0; i < l->count; i++) {
        const FgPkgFn* fn = (const FgPkgFn*)l->items[i];
        pkWStart(w, '{');
        pkWName(w, "name"); pkWStr(w, fn->name);
        pkWName(w, "arguments"); pkWInt(w, fn->arguments);
        pkWName(w, "cpp"); pkWStr(w, fn->cpp);
        if (fn->cfn) { pkWName(w, "c"); pkWStr(w, fn->cfn); }
        pkWName(w, "needsList"); pkWBool(w, fn->needsList);
        pkWName(w, "returnsReference"); pkWBool(w, fn->returnsReference);
        pkWName(w, "host"); pkWBool(w, fn->host);
        if (fn->returns) { pkWName(w, "returns"); pkWStr(w, fn->returns); }
        if (fn->parameters) { pkWName(w, "parameters"); pkWStrList(w, fn->parameters); }
        pkWEnd(w, '}');
    }
    pkWEnd(w, ']');
}
static void pkWNative(FcCtx* c, PkW* w, const PkNative* n) {
    int i;
    pkWStart(w, '{');
    if (n->m.language) { pkWName(w, "language"); pkWStr(w, n->m.language); }
    pkWName(w, "sources"); pkWSrcList(c, w, n->m.sources);
    pkWName(w, "platformSources"); pkWStart(w, '{');
    for (i = 0; i < n->m.platformSources->count; i++) { const FgKeyed* k = (const FgKeyed*)n->m.platformSources->items[i]; pkWName(w, k->key); pkWSrcList(c, w, (const FgSrcList*)k->value); }
    pkWEnd(w, '}');
    pkWName(w, "platforms"); pkWStrList(w, n->m.platforms);
    pkWName(w, "libraries"); pkWStart(w, '{');
    for (i = 0; i < n->libraries->count; i++) { const FgKeyed* k = (const FgKeyed*)n->libraries->items[i]; pkWName(w, k->key); pkWStr(w, (const char*)k->value); }
    pkWEnd(w, '}');
    pkWName(w, "linkLibraries"); pkWStart(w, '{');
    for (i = 0; i < n->m.linkLibraries->count; i++) { const FgKeyed* k = (const FgKeyed*)n->m.linkLibraries->items[i]; pkWName(w, k->key); pkWStrList(w, (const FcList*)k->value); }
    pkWEnd(w, '}');
    pkWName(w, "functions"); pkWFunctions(w, n->m.functions);
    pkWName(w, "bridge"); pkWFunctions(w, n->m.bridge);
    pkWName(w, "exceptions"); pkWStrList(w, n->m.exceptions);
    pkWName(w, "threadSafe"); pkWBool(w, n->m.threadSafe);
    if (n->reset) { pkWName(w, "reset"); pkWStr(w, n->reset); }
    pkWName(w, "uses"); pkWStart(w, '[');
    for (i = 0; i < n->m.uses->count; i++) {
        const FgNativeUse* u = (const FgNativeUse*)n->m.uses->items[i];
        if (u->platforms->count == 0) pkWStr(w, u->import);
        else { pkWStart(w, '{'); pkWName(w, "import"); pkWStr(w, u->import); pkWName(w, "platforms"); pkWStrList(w, u->platforms); pkWEnd(w, '}'); }
    }
    pkWEnd(w, ']');
    pkWName(w, "headers"); pkWStrList(w, n->m.headers);
    if (n->m.units->count > 0) { pkWName(w, "units"); pkWStrList(w, n->m.units); }   /* (forge-c only, D8: written only when there are some) */
    pkWEnd(w, '}');
}
/* PackageManifest.ToJson */
static const char* pkManifestToJson(FcCtx* c, const PkManifest* m) {
    PkW w;
    int i, k;
    memset(&w, 0, sizeof w);
    fcBufStr(&w.b, "");
    pkWStart(&w, '{');
    pkWName(&w, "format"); pkWInt(&w, m->format);
    pkWName(&w, "name"); pkWStr(&w, m->name);
    pkWName(&w, "version"); pkWStr(&w, m->version);
    pkWName(&w, "author"); pkWStr(&w, m->author);
    pkWName(&w, "description"); pkWStr(&w, m->description);
    pkWName(&w, "license"); pkWStr(&w, m->license);
    pkWName(&w, "homepage"); pkWStr(&w, m->homepage);
    pkWName(&w, "dependencies"); pkWStrList(&w, m->dependencies);
    pkWName(&w, "imports"); pkWStart(&w, '[');
    for (i = 0; i < m->imports->count; i++) {
        const PkImport* imp = (const PkImport*)m->imports->items[i];
        pkWStart(&w, '{');
        pkWName(&w, "name"); pkWStr(&w, imp->name);
        pkWName(&w, "standard"); pkWBool(&w, imp->standard);
        if (imp->prelude) { pkWName(&w, "prelude"); pkWStr(&w, imp->prelude); }
        pkWName(&w, "requires"); pkWStrList(&w, imp->requires);
        if (imp->native) { pkWName(&w, "native"); pkWNative(c, &w, imp->native); }
        if (imp->resourceKeys) {
            pkWName(&w, "resources"); pkWStart(&w, '{');
            for (k = 0; k < imp->resourceKeys->count; k++) { pkWName(&w, (const char*)imp->resourceKeys->items[k]); pkWStr(&w, (const char*)imp->resourceValues->items[k]); }
            pkWEnd(&w, '}');
        }
        if (imp->drivers) { pkWName(&w, "drivers"); pkWStrList(&w, imp->drivers); }
        if (imp->serializable >= 0) { pkWName(&w, "serializable"); pkWBool(&w, imp->serializable == 1); }
        pkWEnd(&w, '}');
    }
    pkWEnd(&w, ']');
    if (m->templates) { pkWName(&w, "templates"); pkWStr(&w, m->templates); }
    if (m->documents) { pkWName(&w, "documents"); pkWStrList(&w, m->documents); }
    pkWEnd(&w, '}');
    return fcBufDone(c, &w.b);
}

/* ---- PackageManifest.Parse (the names as they are written: camelCase, no other case) ---- */
_Noreturn static void pkInvalid(FcCtx* c, const char* message) { pkThrow(c, fcFmt(c, "The package description is not valid JSON: %s", message)); }
static const char* pkStr(FcCtx* c, const FjValue* o, const char* name, const char* otherwise) {
    FjValue* v = fjProp(o, name);
    if (!v || v->kind == FJK_NULL) return otherwise;
    if (v->kind != FJK_STRING) pkInvalid(c, fcFmt(c, "The JSON value could not be converted to System.String. Path: $.%s.", name));
    return v->text;
}
static bool pkBool(FcCtx* c, const FjValue* o, const char* name) {
    FjValue* v = fjProp(o, name);
    if (!v || v->kind == FJK_NULL) return false;
    if (v->kind != FJK_TRUE && v->kind != FJK_FALSE) pkInvalid(c, fcFmt(c, "The JSON value could not be converted to System.Boolean. Path: $.%s.", name));
    return v->kind == FJK_TRUE;
}
static FcList* pkStrList(FcCtx* c, const FjValue* o, const char* name, bool nullable) {
    FjValue* v = fjProp(o, name);
    FcList* l;
    int i;
    if (!v || v->kind == FJK_NULL) return nullable ? NULL : fcListNew(c);
    if (v->kind != FJK_ARRAY) pkInvalid(c, fcFmt(c, "The JSON value could not be converted to System.Collections.Generic.List`1[System.String]. Path: $.%s.", name));
    l = fcListNew(c);
    for (i = 0; i < v->items->count; i++) {
        FjValue* e = (FjValue*)v->items->items[i];
        if (e->kind != FJK_STRING) pkInvalid(c, fcFmt(c, "The JSON value could not be converted to System.String. Path: $.%s[%d].", name, i));
        fcListAdd(c, l, (void*)e->text);
    }
    return l;
}
static FgSrcList* pkSrcList(FcCtx* c, const FjValue* v) {
    PjJson j = { c, "package", "" };
    FgSrcList* l;
    if (!v || v->kind == FJK_NULL) return pkSrcListNew(c);
    FC_TRY(c) {
        l = pjSrcList(&j, v, "$");
    } FC_CATCH(c) {
        /* ("The package file is not valid: ..." of the reader of the project: the converter's message for a package description) */
        const char* m = c->err.message;
        const char* colon = strstr(m, "not valid: ");
        pkInvalid(c, colon ? colon + 11 : m);
    } FC_END_TRY;
    return l;
}
static FcList* pkKeyed(FcCtx* c, const FjValue* v, int kind /* 0 source lists, 1 strings, 2 lists of strings */) {
    FcList* l = fcListNew(c);
    int i;
    if (!v || v->kind == FJK_NULL) return l;
    if (v->kind != FJK_OBJECT) pkInvalid(c, "The JSON value could not be converted to System.Collections.Generic.Dictionary`2.");
    for (i = 0; i < v->names->count; i++) {
        FgKeyed* k = NULL;
        FjValue* x = (FjValue*)v->items->items[i];
        int n;
        for (n = 0; n < l->count; n++) if (streq0(((FgKeyed*)l->items[n])->key, (const char*)v->names->items[i])) k = (FgKeyed*)l->items[n];
        if (!k) { k = (FgKeyed*)fcAlloc(c, sizeof *k); k->key = (const char*)v->names->items[i]; fcListAdd(c, l, k); }
        if (kind == 0) k->value = pkSrcList(c, x);
        else if (kind == 1) { if (x->kind != FJK_STRING) pkInvalid(c, "The JSON value could not be converted to System.String."); k->value = (void*)x->text; }
        else {
            FcList* s = fcListNew(c);
            int m;
            if (x->kind != FJK_ARRAY) pkInvalid(c, "The JSON value could not be converted to System.Collections.Generic.List`1[System.String].");
            for (m = 0; m < x->items->count; m++) fcListAdd(c, s, (void*)((FjValue*)x->items->items[m])->text);
            k->value = s;
        }
    }
    return l;
}
static FcList* pkFunctions(FcCtx* c, const FjValue* v) {
    FcList* l = fcListNew(c);
    int i;
    if (!v || v->kind == FJK_NULL) return l;
    for (i = 0; v->kind == FJK_ARRAY && i < v->items->count; i++) {
        FjValue* e = (FjValue*)v->items->items[i];
        FgPkgFn* fn = (FgPkgFn*)fcAlloc(c, sizeof *fn);
        FjValue* a = fjProp(e, "arguments");
        fn->name = pkStr(c, e, "name", "");
        if (a && a->kind != FJK_NULL && !fjTryGetInt32(a, &fn->arguments)) pkInvalid(c, "The JSON value could not be converted to System.Int32.");
        fn->cpp = pkStr(c, e, "cpp", "");
        fn->cfn = pkStr(c, e, "c", NULL);
        fn->needsList = pkBool(c, e, "needsList");
        fn->returnsReference = pkBool(c, e, "returnsReference");
        fn->host = pkBool(c, e, "host");
        fn->returns = pkStr(c, e, "returns", NULL);
        fn->parameters = pkStrList(c, e, "parameters", true);
        fcListAdd(c, l, fn);
    }
    return l;
}
static PkNative* pkNativeParse(FcCtx* c, const FjValue* v) {
    PkNative* n = pkNativeNew(c);
    FjValue* x;
    int i;
    n->m.language = pkStr(c, v, "language", NULL);
    n->m.isC = n->m.language && streq0(n->m.language, "c");
    if ((x = fjProp(v, "sources")) != NULL) n->m.sources = pkSrcList(c, x);
    n->m.platformSources = pkKeyed(c, fjProp(v, "platformSources"), 0);
    n->m.platforms = pkStrList(c, v, "platforms", false);
    n->libraries = pkKeyed(c, fjProp(v, "libraries"), 1);
    n->m.linkLibraries = pkKeyed(c, fjProp(v, "linkLibraries"), 2);
    n->m.functions = pkFunctions(c, fjProp(v, "functions"));
    n->m.bridge = pkFunctions(c, fjProp(v, "bridge"));
    n->m.exceptions = pkStrList(c, v, "exceptions", false);
    n->m.threadSafe = pkBool(c, v, "threadSafe");
    n->reset = pkStr(c, v, "reset", NULL);
    if ((x = fjProp(v, "uses")) != NULL && x->kind == FJK_ARRAY)
        for (i = 0; i < x->items->count; i++) {
            FjValue* e = (FjValue*)x->items->items[i];
            FgNativeUse* u = (FgNativeUse*)fcAlloc(c, sizeof *u);
            if (e->kind == FJK_STRING) { u->import = e->text; u->platforms = fcListNew(c); }
            else if (e->kind == FJK_OBJECT) {
                FjValue* im = pjProp(e, "import");
                FjValue* pl = pjProp(e, "platforms");
                u->import = im && im->kind == FJK_STRING ? im->text : "";
                u->platforms = fcListNew(c);
                if (pl && pl->kind == FJK_ARRAY) { int m; for (m = 0; m < pl->items->count; m++) fcListAdd(c, u->platforms, (void*)((FjValue*)pl->items->items[m])->text); }
                if (pjIsWhite(u->import)) pkInvalid(c, "An entry of 'uses' { ... } needs 'import'.");
            } else pkInvalid(c, "An entry of 'uses' is \"import\" or { \"import\": \"name\", \"platforms\": [ \"posix\", ... ] }.");
            fcListAdd(c, n->m.uses, u);
        }
    n->m.headers = pkStrList(c, v, "headers", false);
    n->m.units = pkStrList(c, v, "units", false);
    return n;
}
static PkManifest* pkManifestParse(FcCtx* c, const char* json) {
    FjError error;
    FjValue* root = fjParse(c, json, &error, NULL);
    PkManifest* m;
    FjValue* x;
    int i;
    if (!root) pkInvalid(c, pjReaderMessage(c, &error));
    if (root->kind == FJK_NULL) pkThrow(c, "The package description is empty.");
    if (root->kind != FJK_OBJECT) pkInvalid(c, "The JSON value could not be converted to fire.Package.Manager.PackageManifest. Path: $.");
    m = pkManifestNew(c);
    if ((x = fjProp(root, "format")) != NULL && x->kind != FJK_NULL && !fjTryGetInt32(x, &m->format)) pkInvalid(c, "The JSON value could not be converted to System.Int32. Path: $.format.");
    m->name = pkStr(c, root, "name", "");
    m->version = pkStr(c, root, "version", "");
    m->author = pkStr(c, root, "author", "");
    m->description = pkStr(c, root, "description", "");
    m->license = pkStr(c, root, "license", "");
    m->homepage = pkStr(c, root, "homepage", "");
    m->dependencies = pkStrList(c, root, "dependencies", false);
    if ((x = fjProp(root, "imports")) != NULL && x->kind == FJK_ARRAY)
        for (i = 0; i < x->items->count; i++) {
            FjValue* e = (FjValue*)x->items->items[i];
            PkImport* imp = pkImportNew(c, pkStr(c, e, "name", ""));
            FjValue* y;
            imp->standard = pkBool(c, e, "standard");
            imp->prelude = pkStr(c, e, "prelude", NULL);
            imp->requires = pkStrList(c, e, "requires", false);
            if ((y = fjProp(e, "native")) != NULL && y->kind == FJK_OBJECT) imp->native = pkNativeParse(c, y);
            if ((y = fjProp(e, "resources")) != NULL && y->kind == FJK_OBJECT) {
                int k;
                imp->resourceKeys = fcListNew(c); imp->resourceValues = fcListNew(c);
                for (k = 0; k < y->names->count; k++) { fcListAdd(c, imp->resourceKeys, y->names->items[k]); fcListAdd(c, imp->resourceValues, (void*)((FjValue*)y->items->items[k])->text); }
            }
            imp->drivers = pkStrList(c, e, "drivers", true);
            if ((y = fjProp(e, "serializable")) != NULL && y->kind != FJK_NULL) imp->serializable = y->kind == FJK_TRUE;
            fcListAdd(c, m->imports, imp);
        }
    m->templates = pkStr(c, root, "templates", NULL);
    m->documents = pkStrList(c, root, "documents", true);
    return m;
}

/* ---- Validate ---- */
static bool pkIsName(const char* s) {   /* ^[A-Za-z0-9][A-Za-z0-9._-]*$ */
    const char* p = s;
    if (!s || !isalnum((unsigned char)*p) || (unsigned char)*p >= 0x80) return false;
    for (p++; *p; p++) if (!(isalnum((unsigned char)*p) || *p == '.' || *p == '_' || *p == '-') || (unsigned char)*p >= 0x80) return false;
    return true;
}
/* SemanticVersion.TryParse (int.TryParse: white space around and a sign are allowed) */
static bool pkIsVersion(FcCtx* c, const char* text) {
    const char* t;
    const char* dash;
    FcList* parts;
    int i;
    if (pjIsWhite(text)) return false;
    t = pjTrim(c, text);
    dash = strchr(t, '-');
    parts = fgSplit(c, dash ? fcStrN(c, t, (size_t)(dash - t)) : t, '.');
    for (i = 0; i < parts->count; i++) {
        const char* p = (const char*)parts->items[i];
        long long n = 0;
        bool neg = false, digits = false;
        while (*p == ' ' || *p == '\t') p++;
        if (*p == '+' || *p == '-') neg = *p++ == '-';
        while (isdigit((unsigned char)*p)) { n = n * 10 + (*p++ - '0'); digits = true; if (n > 2147483648LL) return false; }
        while (*p == ' ' || *p == '\t') p++;
        if (*p || !digits || (neg && n > 0) || n > 2147483647LL) return false;
    }
    return true;
}
typedef bool (*PkExists)(void* state, const char* path);
static FcList* pkValidate(FcCtx* c, const PkManifest* m, PkExists exists, void* state) {
    FcList* problems = fcListNew(c);
    FcDict* seen = fcDictNew(c, FC_KEY_STR);
    int i, k;
    if (m->format != 1) fcListAdd(c, problems, (void*)fcFmt(c, "Unknown format %d (this version of ember reads 1).", m->format));
    if (!pkIsName(m->name)) fcListAdd(c, problems, (void*)"'name' is missing or has characters other than letters, digits, '.', '_' and '-'.");
    if (!pkIsVersion(c, m->version)) fcListAdd(c, problems, (void*)"'version' is missing or not a version (1.2.3).");
    if (m->imports->count == 0) fcListAdd(c, problems, (void*)"The package needs at least one entry in 'imports'.");
    for (i = 0; i < m->imports->count; i++) {
        const PkImport* imp = (const PkImport*)m->imports->items[i];
        const char* where = fcFmt(c, "Import '%s': ", imp->name);
        if (!pjIsImportName(imp->name)) { fcListAdd(c, problems, (void*)fcFmt(c, "Import name '%s' is not a name (letters, digits and '_', starting with a letter; parts may be joined by dots).", imp->name)); continue; }
        if (!imp->standard && pjIsReservedImport(imp->name)) fcListAdd(c, problems, (void*)fcFmt(c, "%sthis name belongs to the compiler.", where));
        if (!fcSetAdd(seen, pjLower(c, imp->name))) fcListAdd(c, problems, (void*)fcFmt(c, "%sthe name is used twice.", where));
        if (pjIsWhite(imp->prelude) && !imp->native) fcListAdd(c, problems, (void*)fcFmt(c, "%sneeds a 'prelude' and/or a 'native' part.", where));
        if (!pjIsWhite(imp->prelude) && exists && !exists(state, imp->prelude)) fcListAdd(c, problems, (void*)fcFmt(c, "%sthe prelude '%s' does not exist.", where, imp->prelude));
        if (imp->resourceKeys)
            for (k = 0; k < imp->resourceKeys->count; k++) {
                const char* name = (const char*)imp->resourceKeys->items[k];
                const char* file = (const char*)imp->resourceValues->items[k];
                FcList* parts = fgSplit(c, pjReplaceChar(c, name, '\\', '/'), '/');
                bool bad = name[0] == '/';
                int n;
                if (pjIsWhite(imp->prelude)) { fcListAdd(c, problems, (void*)fcFmt(c, "%shas resources but no prelude that uses them.", where)); break; }
                for (n = 0; n < parts->count; n++) if (streq0((const char*)parts->items[n], "..") || !*(const char*)parts->items[n]) bad = true;
                if (bad) fcListAdd(c, problems, (void*)fcFmt(c, "%sthe resource '%s' must be a path below the prelude's folder.", where, name));
                else if (exists && !exists(state, file)) fcListAdd(c, problems, (void*)fcFmt(c, "%sthe resource '%s' ('%s') does not exist.", where, name, file));
            }
        if (imp->native) {
            const PkNative* n = imp->native;
            FcList* all = fcListNew(c);
            FcDict* functions = fcDictNew(c, FC_KEY_STR);
            if (n->m.sources->files->count == 0 && n->m.platformSources->count == 0 && n->libraries->count == 0)
                fcListAdd(c, problems, (void*)fcFmt(c, "%s'native' needs at least one file in 'sources' (or in 'platformSources' or 'libraries').", where));
            for (k = 0; k < n->m.sources->files->count; k++) fcListAdd(c, all, n->m.sources->files->items[k]);
            for (k = 0; k < n->m.platformSources->count; k++) { const FgSrcList* l = (const FgSrcList*)((FgKeyed*)n->m.platformSources->items[k])->value; int f; for (f = 0; f < l->files->count; f++) fcListAdd(c, all, l->files->items[f]); }
            for (k = 0; k < n->libraries->count; k++) fcListAdd(c, all, ((FgKeyed*)n->libraries->items[k])->value);
            for (k = 0; k < n->m.headers->count; k++) fcListAdd(c, all, n->m.headers->items[k]);
            for (k = 0; k < n->m.units->count; k++) fcListAdd(c, all, n->m.units->items[k]);
            for (k = 0; k < all->count; k++) if (exists && !exists(state, (const char*)all->items[k])) fcListAdd(c, problems, (void*)fcFmt(c, "%sthe file '%s' does not exist.", where, (const char*)all->items[k]));
            if (n->m.language && !streq0(n->m.language, "c") && !streq0(n->m.language, "c++")) fcListAdd(c, problems, (void*)fcFmt(c, "%s'native.language' is '%s' (c or c++).", where, n->m.language));
            for (k = 0; k < n->m.functions->count; k++) {
                const FgPkgFn* fn = (const FgPkgFn*)n->m.functions->items[k];
                if (pjIsWhite(fn->name) || pjIsWhite(fgPkgFnSymbol(fn))) fcListAdd(c, problems, (void*)fcFmt(c, "%sa native function needs 'name' and '%s'.", where, n->m.isC ? "c" : "cpp"));
                else if (!fcSetAdd(functions, fn->name)) fcListAdd(c, problems, (void*)fcFmt(c, "%sthe native function '%s' is declared twice.", where, fn->name));
                if (fn->arguments < 0 || fn->arguments > 16) fcListAdd(c, problems, (void*)fcFmt(c, "%sthe native function '%s' takes %d arguments (0-16).", where, fn->name, fn->arguments));
            }
        }
    }
    if (m->documents)
        for (i = 0; i < m->documents->count; i++) {
            const char* d = (const char*)m->documents->items[i];
            if (pjIsWhite(d)) fcListAdd(c, problems, (void*)"'documents' has an empty entry.");
            else if (exists && !exists(state, d)) fcListAdd(c, problems, (void*)fcFmt(c, "The document '%s' does not exist.", d));
        }
    return problems;
}
/* PackageManifest.IsDocumentName */
static bool pkIsDocumentName(FcCtx* c, const char* fileName) {
    const char* stem = pjStem(c, fileName);
    const char* ext = pjLower(c, pjExtension(c, fileName));
    static const char* const stems[] = { "README", "LICENSE", "LICENCE", "COPYING", "NOTICE", "AUTHORS" };
    size_t i;
    bool named = false;
    for (i = 0; i < 6; i++) if (pjIEq(stem, stems[i])) named = true;
    return named && (!*ext || streq0(ext, ".md") || streq0(ext, ".txt"));
}

/* ---- NativeSourceOrder.CheckIncludes / CheckAllPlatforms ---- */
typedef const char* (*PkRead)(void* state, const char* path);
static FcList* pkCheckIncludes(FcCtx* c, FcList* names, FcList* texts) {
    FcDict* position = fcDictNew(c, FC_KEY_STR);
    FcList* problems = fcListNew(c);
    int i, k;
    for (i = 0; i < names->count; i++) { const char* f = fgFileNameOf(c, (const char*)names->items[i]); if (!fcDictHas(position, f)) fcDictPut(position, f, FC_IVAL(i)); }
    for (i = 0; i < names->count; i++) {
        FcList* lines = fgLinesOf(c, (const char*)texts->items[i]);
        for (k = 0; k < lines->count; k++) {
            const char* line = (const char*)lines->items[k];
            const char* name;
            const char* after;
            int nameLen;
            void* at;
            const char* inc;
            if (!fgIncludeAt(line, line + strlen(line), true, &name, &nameLen, &after)) continue;
            inc = fcStrN(c, name, (size_t)nameLen);
            if (!fcDictTry(position, fgFileNameOf(c, inc), &at) || FC_VALI(at) <= i) continue;
            fcListAdd(c, problems, (void*)fcFmt(c, "%s:%d: #include \"%s\" is a source of the same package that is built after it - give '%s' a smaller build order (\"order\") than '%s', or list it before.",
                (const char*)names->items[i], k + 1, inc, (const char*)names->items[FC_VALI(at)], (const char*)names->items[i]));
        }
    }
    return problems;
}
static FcList* pkCheckAllPlatforms(FcCtx* c, const PkNative* n, PkRead read, void* state) {
    FcList* problems = fcListNew(c);
    int k, i, m;
    for (k = -1; k < n->m.platformSources->count; k++) {
        const char* key = k < 0 ? NULL : ((FgKeyed*)n->m.platformSources->items[k])->key;
        FcList* keys = fcListNew(c);
        FcList* files;
        FcList* names = fcListNew(c);
        FcList* texts = fcListNew(c);
        FcList* found;
        if (key) fcListAdd(c, keys, (void*)key);
        files = fgSourcesFor(c, &n->m, keys, false);
        for (i = 0; i < files->count; i++) { const char* t = read(state, (const char*)files->items[i]); if (t) { fcListAdd(c, names, files->items[i]); fcListAdd(c, texts, (void*)t); } }
        found = pkCheckIncludes(c, names, texts);
        for (i = 0; i < found->count; i++) {
            const char* p = (const char*)found->items[i];
            bool have = false;
            for (m = 0; m < problems->count; m++) if (streq0((const char*)problems->items[m], p) || fcStartsWith((const char*)problems->items[m], fcFmt(c, "%s (in a build for", p))) have = true;
            if (!have) fcListAdd(c, problems, (void*)(key ? fcFmt(c, "%s (in a build for '%s')", p, key) : p));
        }
    }
    return problems;
}
