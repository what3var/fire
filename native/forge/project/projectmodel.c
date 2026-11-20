/* native/forge/project/projectmodel.c - the model of projects and solutions (Phase D 3, commit de45e7e4): src/fire.Project/FireProject.cs (FireProject,
 * ProjectReference, ProjectNative, OutputType, ImportName, DeriveImportName, Validate), FireSolution.cs, ProjectSettings.cs (Merge, WithAbsolutePaths,
 * Validate, the stripping as text), ProjectPublish.cs (FileBuildActions, PublishSettings, CopyItem, ProjectContent) and ProjectOutput.cs (Folder,
 * ProgramPath, ModeTag). The files are read as System.Text.Json reads them with the options of FireProject/FireSolution (property names in any case,
 * comments, trailing commas, the last property of a name wins): json.c parses, the classes are filled here. Not ported: writing the files (ToJson, Save -
 * the IDE writes them), Clean. */

/* ---- the model ---- */
typedef struct PjSettings {
    const char* subsystem; const char* mode; int floatWidth; int intWidth;   /* FC_NULL_INT: not set */
    const char* name; const char* codename; const char* description; const char* author; const char* license; const char* comments; const char* icon;
    const char* version; const char* fileVersion;
    FcList* defines;               /* NULL: not set */
    const char* engine; const char* target; const char* toolchain; const char* linkage; const char* stripping; const char* optimize; const char* output;
    const char* typeChecking; const char* globalJobs;
    FcList* arguments;             /* NULL: not set */
    const char* port; int baud;
} PjSettings;

typedef struct PjReference { const char* project; const char* package; const char* version; } PjReference;
static bool pjRefIsProject(const PjReference* r) { return r->project && *r->project; }

/* ProjectNative (the part `native` of a project file): NULL lists are not set */
typedef struct PjNative {
    FgSrcList* sources; const char* language; FcList* platformSources /* FgKeyed (FgSrcList*) */; FcList* platforms; FcList* linkLibraries /* FgKeyed (FcList*) */;
    FcList* functions /* FgPkgFn* */; FcList* bridge; FcList* exceptions; int threadSafe /* -1: not set */; const char* reset; FcList* uses /* FgNativeUse* */;
    FgSrcList* headers; int discover /* -1: not set */;
} PjNative;

typedef struct PjPublish { bool enabled; FcList* targets; const char* engine; const char* mode; const char* target; const char* toolchain; int clean /* -1 */; } PjPublish;

typedef struct PjProject {
    int format; const char* name; int type /* 0 exe, 1 library */; const char* import; FcList* files; FcList* exclude; const char* entry; FcList* references /* PjReference* */;
    PjSettings* settings; PjNative* native; bool standard; FcList* drivers; const char* notes; int serializable /* -1 */;
    FcList* buildActionKeys; FcList* buildActionValues;   /* NULL: none (a Dictionary<string, string> in the order of the file) */
    PjPublish* publish; FcList* includedDirectories; FcList* excludedDirectories; int syncGitignore /* -1 */;
    const char* filePath; const char* directory;
} PjProject;

typedef struct PjSolution { int format; const char* name; FcList* projects; FcList* folders; const char* startup; PjSettings* settings; const char* notes; const char* filePath; const char* directory; } PjSolution;

#define PJ_EXE 0
#define PJ_LIBRARY 1

/* the exceptions of fire.Projects and fire.Package.Manager (ProjectException, PackageException): their message */
_Noreturn static void pjThrow(FcCtx* c, const char* message) { fcThrow(c, FC_ERR_PROJECT, 0, 0, NULL, message); }
_Noreturn static void pkThrow(FcCtx* c, const char* message) { fcThrow(c, FC_ERR_PACKAGE, 0, 0, NULL, message); }

/* ---- texts and paths as .NET has them (Linux) ---- */
static int pjCmpIgnoreCase(const char* a, const char* b) {   /* StringComparer.OrdinalIgnoreCase (ASCII; the rest by its units) */
    for (;; a++, b++) {
        int x = (unsigned char)*a, y = (unsigned char)*b;
        if (x >= 'a' && x <= 'z') x -= 32;
        if (y >= 'a' && y <= 'z') y -= 32;
        if (x != y || !x) return x - y;
    }
}
static bool pjIEq(const char* a, const char* b) { return a && b && pjCmpIgnoreCase(a, b) == 0; }
/* OrderBy(OrdinalIgnoreCase).ThenBy(Ordinal) */
static int pjCmpPathOrder(const void* a, const void* b) {
    const char* x = *(const char* const*)a;
    const char* y = *(const char* const*)b;
    int r = pjCmpIgnoreCase(x, y);
    return r ? r : strcmp(x, y);
}
static int pjCmpIgnoreCaseQ(const void* a, const void* b) { return pjCmpIgnoreCase(*(const char* const*)a, *(const char* const*)b); }
/* a stable sort (the LINQ OrderBy): insertion over the list */
static void pjStableSort(FcList* l, int (*cmp)(const void*, const void*)) {
    int i, k;
    for (i = 1; i < l->count; i++) {
        void* x = l->items[i];
        for (k = i; k > 0 && cmp(&l->items[k - 1], &x) > 0; k--) l->items[k] = l->items[k - 1];
        l->items[k] = x;
    }
}
static const char* pjLower(FcCtx* c, const char* s) { return fcAsciiLower(c, s); }
static const char* pjReplaceChar(FcCtx* c, const char* s, char from, char to) {
    char* r = (char*)fcStr(c, s);
    char* p;
    for (p = r; *p; p++) if (*p == from) *p = to;
    return r;
}
static bool pjIsWhite(const char* s) {   /* string.IsNullOrWhiteSpace */
    const unsigned char* p = (const unsigned char*)s;
    if (!s) return true;
    while (*p) if (!fcIsWhiteSpace(fc8Next(&p))) return false;
    return true;
}
static const char* pjTrim(FcCtx* c, const char* s) { return fgTrim(c, s); }
/* string.TrimEnd(): the white space at the end */
static const char* pjTrimEndWs(FcCtx* c, const char* s) {
    size_t n = strlen(s);
    while (n > 0 && (s[n - 1] == ' ' || s[n - 1] == '\t' || s[n - 1] == '\n' || s[n - 1] == '\r' || s[n - 1] == '\v' || s[n - 1] == '\f')) n--;
    return fcStrN(c, s, n);
}
/* Path.GetFullPath(path, basePath) */
static const char* pjFull(FcCtx* c, const char* path, const char* basePath) { return fcFullPath(c, basePath ? basePath : ".", path); }
/* Path.GetFileName / GetFileNameWithoutExtension / GetExtension */
static const char* pjFileName(const char* p) { return fcBaseName(p); }
static const char* pjExtension(FcCtx* c, const char* p) {
    const char* name = fcBaseName(p);
    const char* dot = strrchr(name, '.');
    return dot ? fcStr(c, dot) : "";
}
static const char* pjStem(FcCtx* c, const char* p) {
    const char* name = fcBaseName(p);
    const char* dot = strrchr(name, '.');
    return dot ? fcStrN(c, name, (size_t)(dot - name)) : fcStr(c, name);
}
static const char* pjCombine(FcCtx* c, const char* a, const char* b) { return b[0] == '/' ? fcStr(c, b) : nbCombine(c, a, b); }
/* Path.GetRelativePath(directory, path) of full paths, with `/` (ProjectFiles.Relative) */
static const char* pjRelative(FcCtx* c, const char* directory, const char* path) {
    FcList* a = fgSplit(c, directory, '/');
    FcList* b = fgSplit(c, path, '/');
    FcBuf o = {0};
    int i = 0, k, n = 0;
    FcList* x = fcListNew(c);
    FcList* y = fcListNew(c);
    for (k = 0; k < a->count; k++) if (*(const char*)a->items[k]) fcListAdd(c, x, a->items[k]);
    for (k = 0; k < b->count; k++) if (*(const char*)b->items[k]) fcListAdd(c, y, b->items[k]);
    while (i < x->count && i < y->count && streq0((const char*)x->items[i], (const char*)y->items[i])) i++;
    fcBufStr(&o, "");
    for (k = i; k < x->count; k++) { if (n++) fcBufChar(&o, '/'); fcBufStr(&o, ".."); }
    for (k = i; k < y->count; k++) { if (n++) fcBufChar(&o, '/'); fcBufStr(&o, (const char*)y->items[k]); }
    if (n == 0) fcBufStr(&o, ".");
    return fcBufDone(c, &o);
}
static bool pjListHasIgnoreCase(const FcList* l, const char* s) {
    int i;
    if (l) for (i = 0; i < l->count; i++) if (pjIEq((const char*)l->items[i], s)) return true;
    return false;
}

/* ---- reading the JSON as JsonSerializer does with the options of the project files ---- */
typedef struct PjJson { FcCtx* c; const char* what; const char* text; } PjJson;
/* the property of an object by name in any case (the last one wins) */
static FjValue* pjProp(const FjValue* o, const char* name) {
    int i;
    if (!o || o->kind != FJK_OBJECT) return NULL;
    for (i = o->names->count - 1; i >= 0; i--) if (pjIEq((const char*)o->names->items[i], name)) return (FjValue*)o->items->items[i];
    return NULL;
}
/* the message of a JsonException of the reader: its text without the position, then the path and the position (" Path: $ | LineNumber: ...") */
static const char* pjReaderMessage(FcCtx* c, const FjError* e) {
    const char* m = e->message;
    const char* at = strstr(m, " LineNumber: ");
    if (at) m = fcStrN(c, m, (size_t)(at - m));
    return fcFmt(c, "%s Path: $ | LineNumber: %lld | BytePositionInLine: %lld.", m, (long long)e->line, (long long)e->pos);
}
/* the end of the token of a value in the raw text (the reader's position when the deserializer gives up on it: after a scalar, after the `{` or `[` of a container) */
static size_t pjTokenEnd(const unsigned char* s, size_t n, const FjValue* v) {
    size_t i = v->start;
    if (v->kind == FJK_OBJECT || v->kind == FJK_ARRAY) return i + 1;
    if (i < n && s[i] == '"') {
        for (i++; i < n && s[i] != '"'; i++) if (s[i] == '\\') i++;
        return i + 1;
    }
    while (i < n && !(s[i] == ',' || s[i] == '}' || s[i] == ']' || s[i] == ' ' || s[i] == '\n' || s[i] == '\r' || s[i] == '\t' || s[i] == '/')) i++;
    return i;
}
/* "The JSON value could not be converted to ...": the message of the deserializer */
_Noreturn static void pjBadValue(PjJson* j, const FjValue* v, const char* type, const char* path) {
    int64_t line = 0;
    size_t i, start = 0, end;
    FcBuf b = {0};
    const unsigned char* s;
    size_t n;
    fcBufStr(&b, "");
    fcOut8(&b, j->text);
    s = (const unsigned char*)b.p;
    n = b.len;
    end = pjTokenEnd(s, n, v);
    for (i = 0; i < end && i < n; i++) if (s[i] == '\n') { line++; start = i + 1; }
    fcBufFree(&b);
    pjThrow(j->c, fcFmt(j->c, "The %s file is not valid: The JSON value could not be converted to %s. Path: %s | LineNumber: %lld | BytePositionInLine: %lld.", j->what, type, path, (long long)line, (long long)(end - start)));
}
static const char* pjStr(PjJson* j, const FjValue* o, const char* name, const char* path) {
    FjValue* v = pjProp(o, name);
    if (!v || v->kind == FJK_NULL) return NULL;
    if (v->kind != FJK_STRING) pjBadValue(j, v, "System.String", fcFmt(j->c, "%s.%s", path, name));
    return v->text;
}
static int pjInt(PjJson* j, const FjValue* o, const char* name, const char* path, int otherwise) {
    FjValue* v = pjProp(o, name);
    int x;
    if (!v || v->kind == FJK_NULL) return otherwise;
    if (!fjTryGetInt32(v, &x)) pjBadValue(j, v, otherwise == FC_NULL_INT ? "System.Nullable`1[System.Int32]" : "System.Int32", fcFmt(j->c, "%s.%s", path, name));
    return x;
}
static int pjBool(PjJson* j, const FjValue* o, const char* name, const char* path, int otherwise) {   /* -1: null / not there */
    FjValue* v = pjProp(o, name);
    if (!v || v->kind == FJK_NULL) return otherwise;
    if (v->kind != FJK_TRUE && v->kind != FJK_FALSE) pjBadValue(j, v, otherwise < 0 ? "System.Nullable`1[System.Boolean]" : "System.Boolean", fcFmt(j->c, "%s.%s", path, name));
    return v->kind == FJK_TRUE;
}
static FcList* pjStrList(PjJson* j, const FjValue* o, const char* name, const char* path) {   /* NULL: null / not there */
    FjValue* v = pjProp(o, name);
    FcList* l;
    int i;
    if (!v || v->kind == FJK_NULL) return NULL;
    if (v->kind != FJK_ARRAY) pjBadValue(j, v, "System.Collections.Generic.List`1[System.String]", fcFmt(j->c, "%s.%s", path, name));
    l = fcListNew(j->c);
    for (i = 0; i < v->items->count; i++) {
        FjValue* e = (FjValue*)v->items->items[i];
        if (e->kind == FJK_NULL) { fcListAdd(j->c, l, NULL); continue; }
        if (e->kind != FJK_STRING) pjBadValue(j, e, "System.String", fcFmt(j->c, "%s.%s[%d]", path, name, i));
        fcListAdd(j->c, l, (void*)e->text);
    }
    return l;
}
/* NativeSourceListConverter.Read */
static FgSrcList* pjSrcList(PjJson* j, const FjValue* v, const char* path) {
    FgSrcList* l;
    int i;
    if (!v || v->kind == FJK_NULL) return NULL;
    if (v->kind != FJK_ARRAY) pjThrow(j->c, fcFmt(j->c, "The %s file is not valid: A list of native sources is a list ([\"native/x.h\", { \"file\": \"native/y.h\", \"order\": 10 }]).", j->what));
    l = (FgSrcList*)fcAlloc(j->c, sizeof *l);
    l->files = fcListNew(j->c);
    l->orders = fcListNew(j->c);
    for (i = 0; i < v->items->count; i++) {
        FjValue* e = (FjValue*)v->items->items[i];
        if (e->kind == FJK_STRING) { fcListAdd(j->c, l->files, (void*)e->text); fcListAdd(j->c, l->orders, FC_IVAL(INT_MIN)); }
        else if (e->kind == FJK_OBJECT) {
            const char* file = NULL;
            int order = INT_MIN, k;
            for (k = 0; k < e->names->count; k++) {
                const char* name = (const char*)e->names->items[k];
                FjValue* x = (FjValue*)e->items->items[k];
                if (pjIEq(name, "file")) {
                    if (x->kind != FJK_STRING) pjThrow(j->c, fcFmt(j->c, "The %s file is not valid: 'file' of a native source is a path.", j->what));
                    file = x->text;
                } else if (pjIEq(name, "order")) {
                    if (!fjTryGetInt32(x, &order)) pjThrow(j->c, fcFmt(j->c, "The %s file is not valid: 'order' of a native source is a whole number.", j->what));
                } else pjThrow(j->c, fcFmt(j->c, "The %s file is not valid: A native source has 'file' and 'order', not '%s'.", j->what, name));
            }
            if (pjIsWhite(file)) pjThrow(j->c, fcFmt(j->c, "The %s file is not valid: A native source { ... } needs 'file'.", j->what));
            /* (SetOrder of an entry that is there twice: the last order counts - fgSrcOrderOf takes the last) */
            fcListAdd(j->c, l->files, (void*)file);
            fcListAdd(j->c, l->orders, FC_IVAL(order));
        } else pjThrow(j->c, fcFmt(j->c, "The %s file is not valid: A native source is \"path\" or { \"file\": \"path\", \"order\": n }.", j->what));
    }
    (void)path;
    return l;
}
static FcList* pjKeyed(PjJson* j, const FjValue* v, bool sourceLists, const char* path) {
    FcList* l;
    int i;
    if (!v || v->kind == FJK_NULL) return NULL;
    if (v->kind != FJK_OBJECT) pjBadValue(j, v, "System.Collections.Generic.Dictionary`2", path);
    l = fcListNew(j->c);
    for (i = 0; i < v->names->count; i++) {
        const char* key = (const char*)v->names->items[i];
        FjValue* x = (FjValue*)v->items->items[i];
        FgKeyed* k = NULL;
        int n;
        for (n = 0; n < l->count; n++) if (streq0(((FgKeyed*)l->items[n])->key, key)) k = (FgKeyed*)l->items[n];
        if (!k) { k = (FgKeyed*)fcAlloc(j->c, sizeof *k); k->key = key; fcListAdd(j->c, l, k); }
        if (sourceLists) k->value = pjSrcList(j, x, path);
        else {
            FjValue wrap;
            FcList* names = fcListNew(j->c);
            FcList* items = fcListNew(j->c);
            memset(&wrap, 0, sizeof wrap);
            wrap.kind = FJK_OBJECT; wrap.names = names; wrap.items = items;
            fcListAdd(j->c, names, (void*)"v"); fcListAdd(j->c, items, x);
            k->value = pjStrList(j, &wrap, "v", path);
        }
    }
    return l;
}
/* PackageNativeFunction (System.Text.Json, the options of the project file: names in any case) */
static FcList* pjFunctions(PjJson* j, const FjValue* v, const char* path) {
    FcList* l;
    int i;
    if (!v || v->kind == FJK_NULL) return NULL;
    if (v->kind != FJK_ARRAY) pjBadValue(j, v, "System.Collections.Generic.List`1[fire.Package.Manager.PackageNativeFunction]", path);
    l = fcListNew(j->c);
    for (i = 0; i < v->items->count; i++) {
        FjValue* e = (FjValue*)v->items->items[i];
        FgPkgFn* fn = (FgPkgFn*)fcAlloc(j->c, sizeof *fn);
        const char* p = fcFmt(j->c, "%s[%d]", path, i);
        if (e->kind != FJK_OBJECT) pjBadValue(j, e, "fire.Package.Manager.PackageNativeFunction", p);
        fn->name = pjStr(j, e, "name", p); if (!fn->name) fn->name = "";
        fn->arguments = pjInt(j, e, "arguments", p, 0);
        fn->cpp = pjStr(j, e, "cpp", p); if (!fn->cpp) fn->cpp = "";
        fn->cfn = pjStr(j, e, "c", p);
        fn->needsList = pjBool(j, e, "needsList", p, 0) == 1;
        fn->returnsReference = pjBool(j, e, "returnsReference", p, 0) == 1;
        fn->host = pjBool(j, e, "host", p, 0) == 1;
        fn->returns = pjStr(j, e, "returns", p);
        fn->parameters = pjStrList(j, e, "parameters", p);
        fcListAdd(j->c, l, fn);
    }
    return l;
}
static FcList* pjUses(PjJson* j, const FjValue* v, const char* path) {
    FcList* l;
    int i;
    if (!v || v->kind == FJK_NULL) return NULL;
    if (v->kind != FJK_ARRAY) pjBadValue(j, v, "System.Collections.Generic.List`1[fire.Package.Manager.NativeUse]", path);
    l = fcListNew(j->c);
    for (i = 0; i < v->items->count; i++) {
        FjValue* e = (FjValue*)v->items->items[i];
        FgNativeUse* u = (FgNativeUse*)fcAlloc(j->c, sizeof *u);
        if (e->kind == FJK_STRING) { u->import = e->text; u->platforms = fcListNew(j->c); }
        else if (e->kind == FJK_OBJECT) {
            int k;
            u->import = "";
            u->platforms = fcListNew(j->c);
            for (k = 0; k < e->names->count; k++) {
                const char* name = (const char*)e->names->items[k];
                FjValue* x = (FjValue*)e->items->items[k];
                if (pjIEq(name, "import") && x->kind == FJK_STRING) u->import = x->text;
                else if (pjIEq(name, "platforms") && x->kind == FJK_ARRAY) {
                    int m;
                    for (m = 0; m < x->items->count; m++) if (((FjValue*)x->items->items[m])->kind == FJK_STRING) fcListAdd(j->c, u->platforms, (void*)((FjValue*)x->items->items[m])->text);
                } else pjThrow(j->c, fcFmt(j->c, "The %s file is not valid: An entry of 'uses' has 'import' and 'platforms'.", j->what));
            }
        } else pjThrow(j->c, fcFmt(j->c, "The %s file is not valid: An entry of 'uses' is \"import\" or { \"import\": \"name\", \"platforms\": [ \"posix\", ... ] }.", j->what));
        fcListAdd(j->c, l, u);
    }
    return l;
}
static PjSettings* pjSettingsNew(FcCtx* c) {
    PjSettings* s = (PjSettings*)fcAlloc(c, sizeof *s);
    s->floatWidth = s->intWidth = s->baud = FC_NULL_INT;
    return s;
}
static PjSettings* pjReadSettings(PjJson* j, const FjValue* v, const char* path) {
    PjSettings* s = pjSettingsNew(j->c);
    FjValue* x;
    if (!v || v->kind == FJK_NULL) return s;
    if (v->kind != FJK_OBJECT) pjBadValue(j, v, "fire.Projects.ProjectSettings", path);
    s->subsystem = pjStr(j, v, "subsystem", path);
    s->mode = pjStr(j, v, "mode", path);
    s->floatWidth = pjInt(j, v, "floatWidth", path, FC_NULL_INT);
    s->intWidth = pjInt(j, v, "intWidth", path, FC_NULL_INT);
    s->name = pjStr(j, v, "name", path);
    s->codename = pjStr(j, v, "codename", path);
    s->description = pjStr(j, v, "description", path);
    s->author = pjStr(j, v, "author", path);
    s->license = pjStr(j, v, "license", path);
    s->comments = pjStr(j, v, "comments", path);
    s->icon = pjStr(j, v, "icon", path);
    s->version = pjStr(j, v, "version", path);
    s->fileVersion = pjStr(j, v, "fileVersion", path);
    s->defines = pjStrList(j, v, "defines", path);
    s->engine = pjStr(j, v, "engine", path);
    s->target = pjStr(j, v, "target", path);
    s->toolchain = pjStr(j, v, "toolchain", path);
    s->linkage = pjStr(j, v, "linkage", path);
    /* StrippingTextConverter: a number as its text, a string as it is */
    if ((x = pjProp(v, "stripping")) != NULL && x->kind != FJK_NULL) {
        if (x->kind == FJK_STRING) s->stripping = x->text;
        else if (x->kind == FJK_NUMBER) s->stripping = x->text;   /* (an integer; .NET writes a double again in its invariant form) */
        else pjThrow(j->c, fcFmt(j->c, "The %s file is not valid: settings.stripping: a number (0-3) or a name (none, protected, methods, all).", j->what));
    }
    s->optimize = pjStr(j, v, "optimize", path);
    s->output = pjStr(j, v, "output", path);
    s->typeChecking = pjStr(j, v, "typeChecking", path);
    s->globalJobs = pjStr(j, v, "globalJobs", path);
    s->arguments = pjStrList(j, v, "arguments", path);
    s->port = pjStr(j, v, "port", path);
    s->baud = pjInt(j, v, "baud", path, FC_NULL_INT);
    return s;
}
static FC_LATER bool pjSettingsEmpty(const PjSettings* s) {
    return !s->subsystem && !s->mode && s->floatWidth == FC_NULL_INT && s->intWidth == FC_NULL_INT && !s->name && !s->codename && !s->description && !s->author && !s->license
        && !s->comments && !s->icon && !s->version && !s->fileVersion && (!s->defines || s->defines->count == 0) && !s->engine && !s->target && !s->toolchain && !s->linkage
        && !s->stripping && !s->optimize && !s->output && !s->typeChecking && !s->globalJobs && !s->arguments && !s->port && s->baud == FC_NULL_INT;
}

/* ProjectSettings.Merge(high, low) */
#define PJ_PICK(f) r->f = high && high->f ? high->f : low ? low->f : NULL
#define PJ_PICKI(f) r->f = high && high->f != FC_NULL_INT ? high->f : low ? low->f : FC_NULL_INT
static PjSettings* pjSettingsMerge(FcCtx* c, const PjSettings* high, const PjSettings* low) {
    PjSettings* r = pjSettingsNew(c);
    FcList* defines = fcListNew(c);
    int k, i;
    PJ_PICK(subsystem); PJ_PICK(mode); PJ_PICKI(floatWidth); PJ_PICKI(intWidth); PJ_PICK(name); PJ_PICK(codename); PJ_PICK(description); PJ_PICK(author);
    PJ_PICK(license); PJ_PICK(comments); PJ_PICK(icon); PJ_PICK(version); PJ_PICK(fileVersion); PJ_PICK(engine); PJ_PICK(target); PJ_PICK(toolchain);
    PJ_PICK(linkage); PJ_PICK(stripping); PJ_PICK(optimize); PJ_PICK(output); PJ_PICK(typeChecking); PJ_PICK(globalJobs); PJ_PICK(arguments); PJ_PICK(port); PJ_PICKI(baud);
    for (k = 0; k < 2; k++) {
        const PjSettings* s = k == 0 ? high : low;
        if (s && s->defines) for (i = 0; i < s->defines->count; i++) if (!fcListHasStr(defines, (const char*)s->defines->items[i])) fcListAdd(c, defines, s->defines->items[i]);
    }
    r->defines = defines->count > 0 ? defines : NULL;
    return r;
}
#undef PJ_PICK
#undef PJ_PICKI
/* WithAbsolutePaths: `icon` and `output` relative to the folder of the file that holds them */
static PjSettings* pjSettingsAbsolute(FcCtx* c, const PjSettings* s, const char* directory) {
    PjSettings* r = pjSettingsMerge(c, s, NULL);
    if (r->icon && *r->icon) r->icon = pjFull(c, r->icon, directory);
    if (r->output && *r->output) r->output = pjFull(c, r->output, directory);
    return r;
}
static const char* const pjSubsystems[] = { "console", "gui" };
static const char* const pjModes[] = { "debug", "release", "performance" };
static const char* const pjEngines[] = { "vm", "native" };
static const char* const pjGlobalJobsModes[] = { "safepoints", "sleep", "sync" };
static const char* const pjOptimizations[] = { "speed", "size" };
static const char* const pjStrippings[] = { "none", "protected", "methods", "all" };
static bool pjIn(const char* const* names, int n, const char* s) { int i; for (i = 0; i < n; i++) if (streq0(names[i], s)) return true; return false; }
static const char* pjNames(FcCtx* c, const char* const* names, int n) { FcList* l = fcListNew(c); int i; for (i = 0; i < n; i++) fcListAdd(c, l, (void*)names[i]); return fgJoin(c, l, ", "); }
/* ProjectSettings.Validate */
static void pjSettingsValidate(FcCtx* c, const PjSettings* s, FcList* problems) {
    int i;
    if (s->subsystem && !pjIn(pjSubsystems, 2, s->subsystem)) fcListAdd(c, problems, (void*)fcFmt(c, "settings.subsystem '%s' is not one of: %s.", s->subsystem, pjNames(c, pjSubsystems, 2)));
    if (s->mode && !pjIn(pjModes, 3, s->mode)) fcListAdd(c, problems, (void*)fcFmt(c, "settings.mode '%s' is not one of: %s.", s->mode, pjNames(c, pjModes, 3)));
    if (s->floatWidth != FC_NULL_INT && s->floatWidth != 32 && s->floatWidth != 64) fcListAdd(c, problems, (void*)"settings.floatWidth must be 32 or 64.");
    if (s->intWidth != FC_NULL_INT && s->intWidth != 16 && s->intWidth != 32 && s->intWidth != 64) fcListAdd(c, problems, (void*)"settings.intWidth must be 16, 32 or 64.");
    if (s->typeChecking && !streq0(s->typeChecking, "lax") && !streq0(s->typeChecking, "strict")) fcListAdd(c, problems, (void*)"settings.typeChecking must be lax or strict.");
    if (s->globalJobs && !pjIn(pjGlobalJobsModes, 3, pjLower(c, s->globalJobs))) fcListAdd(c, problems, (void*)fcFmt(c, "settings.globalJobs '%s' is not one of: %s.", s->globalJobs, pjNames(c, pjGlobalJobsModes, 3)));
    if (s->engine && !pjIn(pjEngines, 2, s->engine)) fcListAdd(c, problems, (void*)fcFmt(c, "settings.engine '%s' is not one of: %s.", s->engine, pjNames(c, pjEngines, 2)));
    if (s->stripping && !(strlen(s->stripping) == 1 && s->stripping[0] >= '0' && s->stripping[0] <= '3') && !pjIn(pjStrippings, 4, pjLower(c, s->stripping)))
        fcListAdd(c, problems, (void*)fcFmt(c, "settings.stripping '%s' is not one of: 0, 1, 2, 3, %s.", s->stripping, pjNames(c, pjStrippings, 4)));
    if (s->optimize && !pjIn(pjOptimizations, 2, pjLower(c, s->optimize))) fcListAdd(c, problems, (void*)fcFmt(c, "settings.optimize '%s' is not one of: %s.", s->optimize, pjNames(c, pjOptimizations, 2)));
    if (s->baud != FC_NULL_INT && s->baud <= 0) fcListAdd(c, problems, (void*)"settings.baud must be a positive number (e.g. 115200).");
    if (s->defines)
        for (i = 0; i < s->defines->count; i++) {
            const char* d = (const char*)s->defines->items[i];
            const unsigned char* p = (const unsigned char*)d;
            bool white = pjIsWhite(d);
            while (!white && *p) if (fcIsWhiteSpace(fc8Next(&p))) white = true;
            if (white) fcListAdd(c, problems, (void*)fcFmt(c, "settings.defines: '%s' is not a symbol name.", d ? d : ""));
        }
}

/* ---- FileBuildActions (ProjectPublish.cs) ---- */
enum { PJ_ACT_BUILD, PJ_ACT_IGNORE, PJ_ACT_COPY, PJ_ACT_COPYALWAYS };
static const char* const pjActionNames[] = { "build", "ignore", "copy", "copyAlways" };
static int pjActionParse(FcCtx* c, const char* text) {   /* -1: no action */
    FcBuf b = {0};
    const char* t = pjTrim(c, text ? text : "");
    const char* p;
    const char* s;
    fcBufStr(&b, "");
    for (p = t; *p; p++) if (*p != '-' && *p != '_') fcBufChar(&b, *p);
    s = pjLower(c, fcBufDone(c, &b));
    if (streq0(s, "build") || streq0(s, "compile") || streq0(s, "default")) return PJ_ACT_BUILD;
    if (streq0(s, "ignore") || streq0(s, "none") || streq0(s, "exclude")) return PJ_ACT_IGNORE;
    if (streq0(s, "copy") || streq0(s, "copyifnewer")) return PJ_ACT_COPY;
    if (streq0(s, "copyalways")) return PJ_ACT_COPYALWAYS;
    return -1;
}
static bool pjIsPattern(const char* text) { return strchr(text, '*') || strchr(text, '?'); }
static const char* pjActionNormalize(FcCtx* c, const char* rel) {
    const char* p = pjReplaceChar(c, rel, '\\', '/');
    while (fcStartsWith(p, "./")) p += 2;
    return p;
}
static bool pjPatternMatch(FcCtx* c, const char* pattern, const char* rel);
/* EntryOf: the file itself, else the last pattern that matches it */
static const char* pjActionEntryOf(FcCtx* c, const PjProject* p, const char* rel) {
    const char* found = NULL;
    int i;
    if (!p->buildActionKeys || p->buildActionKeys->count == 0) return NULL;
    rel = pjActionNormalize(c, rel);
    for (i = 0; i < p->buildActionKeys->count; i++) {
        const char* key = (const char*)p->buildActionKeys->items[i];
        if (!pjIsPattern(key) && streq0(pjActionNormalize(c, key), rel)) return key;
    }
    for (i = 0; i < p->buildActionKeys->count; i++) {
        const char* key = (const char*)p->buildActionKeys->items[i];
        if (pjIsPattern(key) && pjPatternMatch(c, key, rel)) found = key;
    }
    return found;
}
static int pjActionOf(FcCtx* c, const PjProject* p, const char* rel) {
    const char* key = pjActionEntryOf(c, p, rel);
    int i, a;
    if (!key) return PJ_ACT_BUILD;
    for (i = 0; i < p->buildActionKeys->count; i++) if (p->buildActionKeys->items[i] == key) break;
    a = pjActionParse(c, (const char*)p->buildActionValues->items[i]);
    return a < 0 ? PJ_ACT_BUILD : a;
}
static bool pjActionIsCopy(int a) { return a == PJ_ACT_COPY || a == PJ_ACT_COPYALWAYS; }

/* ---- PublishSettings ---- */
static PjPublish* pjReadPublish(PjJson* j, const FjValue* v) {
    PjPublish* p;
    if (!v || v->kind == FJK_NULL) return NULL;
    if (v->kind != FJK_OBJECT) pjBadValue(j, v, "fire.Projects.PublishSettings", "$.publish");
    p = (PjPublish*)fcAlloc(j->c, sizeof *p);
    p->enabled = pjBool(j, v, "enabled", "$.publish", 0) == 1;
    p->targets = pjStrList(j, v, "targets", "$.publish");
    p->engine = pjStr(j, v, "engine", "$.publish");
    p->mode = pjStr(j, v, "mode", "$.publish");
    p->target = pjStr(j, v, "target", "$.publish");
    p->toolchain = pjStr(j, v, "toolchain", "$.publish");
    p->clean = pjBool(j, v, "clean", "$.publish", -1);
    return p;
}
static void pjPublishValidate(FcCtx* c, const PjPublish* p, FcList* problems) {
    int i;
    if (p->engine && !pjIn(pjEngines, 2, p->engine)) fcListAdd(c, problems, (void*)fcFmt(c, "publish.engine '%s' is not one of: %s.", p->engine, pjNames(c, pjEngines, 2)));
    if (p->mode && !pjIn(pjModes, 3, p->mode)) fcListAdd(c, problems, (void*)fcFmt(c, "publish.mode '%s' is not one of: %s.", p->mode, pjNames(c, pjModes, 3)));
    if (p->targets) for (i = 0; i < p->targets->count; i++) if (pjIsWhite((const char*)p->targets->items[i])) fcListAdd(c, problems, (void*)"publish.targets: an entry is empty.");
}

/* ---- FireProject ---- */
static const char* const pjDefaultFiles[] = { "**/*.{fire,script,fi,fic,fxml,biscuit,fdata}" };
static const char* const pjDefaultExclude[] = { "**/code", "**/*.exe", "**/*.dll" };
static const char* const pjSourceExtensions[] = { ".fire", ".script", ".fi", ".fic", ".fxml", ".biscuit", ".fdata" };
static bool pjIsSourceFile(FcCtx* c, const char* path) {
    const char* ext = pjExtension(c, path);
    size_t i;
    for (i = 0; i < sizeof pjSourceExtensions / sizeof pjSourceExtensions[0]; i++) if (pjIEq(ext, pjSourceExtensions[i])) return true;
    return false;
}
/* MarkupFiles.IsGenerated: .fxml, .biscuit, .fdata */
static bool pjIsMarkupFile(const char* path) { return fmIsUiMarkup(path) || fmIsBiscuit(path) || fmIsDataModel(path); }

/* FireProject.Parse + Load */
static PjProject* pjProjectParse(FcCtx* c, const char* text, const char* full) {
    PjJson j = { c, "project", text };
    FjError error;
    FjValue* root = fjParse(c, text, &error, NULL);
    PjProject* p;
    FjValue* v;
    int i;
    if (!root) pjThrow(c, fcFmt(c, "The project file is not valid: %s", pjReaderMessage(c, &error)));
    if (root->kind == FJK_NULL) pjThrow(c, "The project file is empty.");
    if (root->kind != FJK_OBJECT) pjBadValue(&j, root, "fire.Projects.FireProject", "$");
    p = (PjProject*)fcAlloc(c, sizeof *p);
    p->format = pjInt(&j, root, "format", "$", 1);
    p->name = pjStr(&j, root, "name", "$");
    if (!p->name) p->name = "";
    p->type = PJ_EXE;
    if ((v = pjProp(root, "type")) != NULL && v->kind != FJK_NULL) {
        int n;
        /* JsonStringEnumConverter(CamelCase): the names in any case, also the numbers */
        if (v->kind == FJK_STRING && pjIEq(v->text, "exe")) p->type = PJ_EXE;
        else if (v->kind == FJK_STRING && pjIEq(v->text, "library")) p->type = PJ_LIBRARY;
        else if (fjTryGetInt32(v, &n)) p->type = n;
        else pjBadValue(&j, v, "fire.Projects.OutputType", "$.type");
    }
    p->import = pjStr(&j, root, "import", "$");
    p->files = pjStrList(&j, root, "files", "$");
    if (!p->files) p->files = fcListNew(c);
    p->exclude = pjStrList(&j, root, "exclude", "$");
    if (!p->exclude && !pjProp(root, "exclude")) {
        p->exclude = fcListNew(c);
        for (i = 0; i < 3; i++) fcListAdd(c, p->exclude, (void*)pjDefaultExclude[i]);
    }
    if (!p->exclude) p->exclude = fcListNew(c);
    p->entry = pjStr(&j, root, "entry", "$");
    p->references = fcListNew(c);
    if ((v = pjProp(root, "references")) != NULL && v->kind != FJK_NULL) {
        if (v->kind != FJK_ARRAY) pjBadValue(&j, v, "System.Collections.Generic.List`1[fire.Projects.ProjectReference]", "$.references");
        for (i = 0; i < v->items->count; i++) {
            FjValue* e = (FjValue*)v->items->items[i];
            PjReference* r = (PjReference*)fcAlloc(c, sizeof *r);
            const char* at = fcFmt(c, "$.references[%d]", i);
            if (e->kind != FJK_OBJECT) pjBadValue(&j, e, "fire.Projects.ProjectReference", at);
            r->project = pjStr(&j, e, "project", at);
            r->package = pjStr(&j, e, "package", at);
            r->version = pjStr(&j, e, "version", at);
            fcListAdd(c, p->references, r);
        }
    }
    p->settings = pjReadSettings(&j, pjProp(root, "settings"), "$.settings");
    if ((v = pjProp(root, "native")) != NULL && v->kind != FJK_NULL) {
        PjNative* n = (PjNative*)fcAlloc(c, sizeof *n);
        if (v->kind != FJK_OBJECT) pjBadValue(&j, v, "fire.Projects.ProjectNative", "$.native");
        n->sources = pjSrcList(&j, pjProp(v, "sources"), "$.native.sources");
        n->language = pjStr(&j, v, "language", "$.native");
        n->platformSources = pjKeyed(&j, pjProp(v, "platformSources"), true, "$.native.platformSources");
        n->platforms = pjStrList(&j, v, "platforms", "$.native");
        n->linkLibraries = pjKeyed(&j, pjProp(v, "linkLibraries"), false, "$.native.linkLibraries");
        n->functions = pjFunctions(&j, pjProp(v, "functions"), "$.native.functions");
        n->bridge = pjFunctions(&j, pjProp(v, "bridge"), "$.native.bridge");
        n->exceptions = pjStrList(&j, v, "exceptions", "$.native");
        n->threadSafe = pjBool(&j, v, "threadSafe", "$.native", -1);
        n->reset = pjStr(&j, v, "reset", "$.native");
        n->uses = pjUses(&j, pjProp(v, "uses"), "$.native.uses");
        n->headers = pjSrcList(&j, pjProp(v, "headers"), "$.native.headers");
        n->discover = pjBool(&j, v, "discover", "$.native", -1);
        p->native = n;
    }
    p->standard = pjBool(&j, root, "standard", "$", 0) == 1;
    p->drivers = pjStrList(&j, root, "drivers", "$");
    p->notes = pjStr(&j, root, "notes", "$");
    p->serializable = pjBool(&j, root, "serializable", "$", -1);
    if ((v = pjProp(root, "buildActions")) != NULL && v->kind != FJK_NULL) {
        if (v->kind != FJK_OBJECT) pjBadValue(&j, v, "System.Collections.Generic.Dictionary`2[System.String,System.String]", "$.buildActions");
        p->buildActionKeys = fcListNew(c);
        p->buildActionValues = fcListNew(c);
        for (i = 0; i < v->names->count; i++) {
            const char* key = (const char*)v->names->items[i];
            FjValue* x = (FjValue*)v->items->items[i];
            int k;
            if (x->kind != FJK_STRING && x->kind != FJK_NULL) pjBadValue(&j, x, "System.String", fcFmt(c, "$.buildActions.%s", key));
            for (k = 0; k < p->buildActionKeys->count; k++) if (streq0((const char*)p->buildActionKeys->items[k], key)) break;
            if (k < p->buildActionKeys->count) p->buildActionValues->items[k] = (void*)(x->kind == FJK_STRING ? x->text : NULL);
            else { fcListAdd(c, p->buildActionKeys, (void*)key); fcListAdd(c, p->buildActionValues, (void*)(x->kind == FJK_STRING ? x->text : NULL)); }
        }
    }
    p->publish = pjReadPublish(&j, pjProp(root, "publish"));
    p->includedDirectories = pjStrList(&j, root, "includedDirectories", "$");
    p->excludedDirectories = pjStrList(&j, root, "excludedDirectories", "$");
    p->syncGitignore = pjBool(&j, root, "syncGitignore", "$", -1);
    (void)full;
    return p;
}
/* File.ReadAllText as fc8 (NULL: it cannot be read) */
static const char* pjReadAllText(FcCtx* c, const char* path) {
    FcU16 t = {0};
    const char* r;
    if (!nbFileExists(path) || !fcReadText(path, &t)) return NULL;
    r = fc8FromU16(c, t.p, t.len);
    fcU16Free(&t);
    return r;
}
static PjProject* pjProjectLoad(FcCtx* c, const char* path) {
    const char* full = pjFull(c, path, ".");
    const char* text = pjReadAllText(c, full);
    PjProject* p;
    if (!text) pjThrow(c, fcFmt(c, "Cannot read '%s': Could not find file '%s'.", full, full));
    p = pjProjectParse(c, text, full);
    p->filePath = full;
    p->directory = fcDirectoryName(c, full);
    if (pjIsWhite(p->name)) p->name = pjStem(c, full);
    return p;
}
/* DeriveImportName */
static const char* pjDeriveImportName(FcCtx* c, const char* name) {
    FcList* parts = fgSplit(c, name, '.');
    FcList* out = fcListNew(c);
    int i;
    for (i = 0; i < parts->count; i++) {
        const char* part = (const char*)parts->items[i];
        const unsigned char* p = (const unsigned char*)part;
        FcBuf b = {0};
        const char* s;
        if (!*part) continue;
        fcBufStr(&b, "");
        while (*p) {
            uint16_t u = fc8Next(&p);
            fcBufChar(&b, (char)((fcIsLetterOrDigit(u) && u < 128) || u == '_' ? u : '_'));
        }
        s = fcBufDone(c, &b);
        fcListAdd(c, out, (void*)(s[0] == 0 || !isalpha((unsigned char)s[0]) ? fcFmt(c, "L%s", s) : s));
    }
    return out->count == 0 ? "L" : fgJoin(c, out, ".");
}
static const char* pjImportName(FcCtx* c, const PjProject* p) { return !pjIsWhite(p->import) ? p->import : pjDeriveImportName(c, p->name); }

/* PackageManifest.ImportNamePattern: ^[A-Za-z][A-Za-z0-9_]*(\.[A-Za-z][A-Za-z0-9_]*)*$ */
static bool pjIsImportName(const char* s) {
    const char* p = s;
    if (!s) return false;
    for (;;) {
        if (!isalpha((unsigned char)*p) || (unsigned char)*p >= 0x80) return false;
        p++;
        while (*p && (isalnum((unsigned char)*p) || *p == '_')) p++;
        if (!*p) return true;
        if (*p != '.') return false;
        p++;
    }
}
static const char* const pjReservedImportNames[] = {
    "print", "graphics", "windows", "devices", "io", "filesystem", "dialogs", "ui", "linq", "reflection", "time", "random", "testing", "net", "process", "hash", "compression", "tls",
    "http", "gpio", "i2c", "spi", "wifi", "audio", "usb", "tracker", "scene3d", "scene3d.terrain", "scene3d.effects", "scene3d.physics", "ui.docking", "json", "circuits",
    "circuits.ui", "console", "machine", "dos", "xms", "x86" };
static bool pjIsReservedImport(const char* s) {
    size_t i;
    for (i = 0; i < sizeof pjReservedImportNames / sizeof pjReservedImportNames[0]; i++) if (pjIEq(pjReservedImportNames[i], s)) return true;
    return false;
}
/* FireProject.Validate */
static FcList* pjProjectValidate(FcCtx* c, const PjProject* p) {
    FcList* problems = fcListNew(c);
    int i;
    if (p->format != 1) fcListAdd(c, problems, (void*)fcFmt(c, "Unknown format %d (this version reads %d).", p->format, 1));
    if (pjIsWhite(p->name)) fcListAdd(c, problems, (void*)"'name' is missing.");
    else if (strchr(p->name, '/') || strstr(p->name, "\xC0\x80")) fcListAdd(c, problems, (void*)fcFmt(c, "The name '%s' has characters that a file name cannot have.", p->name));
    pjSettingsValidate(c, p->settings, problems);
    if (p->buildActionKeys)
        for (i = 0; i < p->buildActionKeys->count; i++)
            if (pjActionParse(c, (const char*)p->buildActionValues->items[i]) < 0)
                fcListAdd(c, problems, (void*)fcFmt(c, "buildActions: '%s' (%s) is not one of: %s.", p->buildActionValues->items[i] ? (const char*)p->buildActionValues->items[i] : "",
                    (const char*)p->buildActionKeys->items[i], pjNames(c, pjActionNames, 4)));
    if (p->publish) pjPublishValidate(c, p->publish, problems);
    if (p->type == PJ_LIBRARY) {
        const char* import = pjImportName(c, p);
        if (!pjIsImportName(import)) fcListAdd(c, problems, (void*)fcFmt(c, "The import name '%s' is not a name (letters, digits and '_', starting with a letter; parts may be joined by dots).", import));
        else if (!p->standard && pjIsReservedImport(import)) fcListAdd(c, problems, (void*)fcFmt(c, "The import name '%s' belongs to the compiler; set 'import' to another name.", import));
    }
    for (i = 0; i < p->references->count; i++) {
        const PjReference* r = (const PjReference*)p->references->items[i];
        if (pjRefIsProject(r) == (r->package && *r->package)) fcListAdd(c, problems, (void*)"A reference names either a 'project' or a 'package'.");
    }
    return problems;
}

/* ---- FireSolution ---- */
static PjSolution* pjSolutionLoad(FcCtx* c, const char* path) {
    const char* full = pjFull(c, path, ".");
    const char* text = pjReadAllText(c, full);
    PjJson j = { c, "solution", text };
    FjError error;
    FjValue* root;
    PjSolution* s;
    if (!text) pjThrow(c, fcFmt(c, "Cannot read '%s': Could not find file '%s'.", full, full));
    root = fjParse(c, text, &error, NULL);
    if (!root) pjThrow(c, fcFmt(c, "The solution file is not valid: %s", pjReaderMessage(c, &error)));
    if (root->kind == FJK_NULL) pjThrow(c, "The solution file is empty.");
    if (root->kind != FJK_OBJECT) pjBadValue(&j, root, "fire.Projects.FireSolution", "$");
    s = (PjSolution*)fcAlloc(c, sizeof *s);
    s->format = pjInt(&j, root, "format", "$", 1);
    s->name = pjStr(&j, root, "name", "$");
    if (!s->name) s->name = "";
    s->projects = pjStrList(&j, root, "projects", "$");
    if (!s->projects) s->projects = fcListNew(c);
    s->folders = pjStrList(&j, root, "folders", "$");
    if (!s->folders) s->folders = fcListNew(c);
    s->startup = pjStr(&j, root, "startup", "$");
    s->settings = pjReadSettings(&j, pjProp(root, "settings"), "$.settings");
    s->notes = pjStr(&j, root, "notes", "$");
    s->filePath = full;
    s->directory = fcDirectoryName(c, full);
    if (pjIsWhite(s->name)) s->name = pjStem(c, full);
    return s;
}

/* ---- ProjectOutput ---- */
static const char* pjModeTag(FcCtx* c, const char* mode) {
    const char* m = pjLower(c, mode);
    return streq0(m, "debug") ? "dbg" : streq0(m, "performance") ? "perf" : "rel";
}
static const char* pjOutputFolder(FcCtx* c, const char* projectDirectory, const char* target, const char* mode) {
    return nbCombine(c, nbCombine(c, projectDirectory, "bin"), fcFmt(c, "%s-%s", target, pjModeTag(c, mode)));
}
/* ProgramPath with the extension of the target (a character a file name cannot have - on Linux '/' and NUL - becomes '_') */
static const char* pjProgramPath(FcCtx* c, const char* projectDirectory, const char* projectName, const char* target, const char* mode, const char* extension) {
    return nbCombine(c, pjOutputFolder(c, projectDirectory, target, mode), fcFmt(c, "%s%s", pjReplaceChar(c, projectName, '/', '_'), extension));
}
