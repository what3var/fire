/* native/forge/linker.c - the linker (commit 6bc618e7): Linker.CompileAndLink (src/fire.Compiler/Linker.cs: the prelude, the directives of a build
 * with their effects, `#import` with its preludes and natives, the type check, the bytecode compiler, the linked program), ImportedPreludes.cs
 * (ParseImportName, WithDependencies, Insert), PackageImports.cs (RegisterNames, NativeReturnTypes, NativesOf), PackageLibrary.FileNameFor,
 * ProjectLibraries.cs (ImportKeys, PackageNamespaceOf), src/fire/Runtime/FloatNarrowing.cs and the store of the installed packages
 * (src/fire.Package.Manager/PackageStore.cs: the manifests `package.json` of `<store>/<package>/`, read with a small JSON reader). Not ported:
 * projects (BuildPlan), project libraries, targets, `-D`, OwnImports, packing a program, the conversion of model resources (ModelConverter). */

/* ---- a small JSON reader (the manifests) ---- */
typedef enum FcJsonKind { FJ_NULL, FJ_BOOL, FJ_NUM, FJ_STR, FJ_ARR, FJ_OBJ } FcJsonKind;
typedef struct FcJson { FcJsonKind kind; bool b; double n; const char* s; FcList* items; FcList* keys; } FcJson;
typedef struct FcJsonReader { FcCtx* c; const char* p; bool bad; } FcJsonReader;

static void fcJsonWs(FcJsonReader* j) { while (*j->p == ' ' || *j->p == '\t' || *j->p == '\n' || *j->p == '\r') j->p++; }
static FcJson* fcJsonValue(FcJsonReader* j);
static const char* fcJsonString(FcJsonReader* j) {
    FcBuf b = {0};
    if (*j->p != '"') { j->bad = true; return ""; }
    j->p++;
    fcBufStr(&b, "");
    while (*j->p && *j->p != '"') {
        if (*j->p == '\\') {
            j->p++;
            switch (*j->p) {
                case 'n': fcBufChar(&b, '\n'); break;
                case 't': fcBufChar(&b, '\t'); break;
                case 'r': fcBufChar(&b, '\r'); break;
                case 'b': fcBufChar(&b, '\b'); break;
                case 'f': fcBufChar(&b, '\f'); break;
                case 'u': {
                    unsigned v = 0;
                    int k;
                    for (k = 0; k < 4 && j->p[1]; k++) { char h = *++j->p; v = v * 16 + (unsigned)(h >= 'a' ? h - 'a' + 10 : h >= 'A' ? h - 'A' + 10 : h - '0'); }
                    fcBufUnit(&b, (uint16_t)v);
                    break;
                }
                default: fcBufChar(&b, *j->p); break;
            }
            if (*j->p) j->p++;
        } else {
            /* UTF-8 as fc8: each UTF-16 unit */
            const unsigned char* q = (const unsigned char*)j->p;
            uint32_t cp;
            int n = 1;
            if (*q < 0x80) cp = *q;
            else if ((*q & 0xE0) == 0xC0) { cp = *q & 0x1F; n = 2; }
            else if ((*q & 0xF0) == 0xE0) { cp = *q & 0x0F; n = 3; }
            else { cp = *q & 0x07; n = 4; }
            { int k; for (k = 1; k < n && q[k]; k++) cp = (cp << 6) | (q[k] & 0x3F); }
            if (cp >= 0x10000) { cp -= 0x10000; fcBufUnit(&b, (uint16_t)(0xD800 + (cp >> 10))); fcBufUnit(&b, (uint16_t)(0xDC00 + (cp & 0x3FF))); }
            else fcBufUnit(&b, (uint16_t)cp);
            j->p += n;
        }
    }
    if (*j->p == '"') j->p++; else j->bad = true;
    return fcBufDone(j->c, &b);
}
static FcJson* fcJsonValue(FcJsonReader* j) {
    FcJson* v = (FcJson*)fcAlloc(j->c, sizeof *v);
    fcJsonWs(j);
    if (*j->p == '{') {
        v->kind = FJ_OBJ; v->keys = fcListNew(j->c); v->items = fcListNew(j->c);
        j->p++;
        fcJsonWs(j);
        if (*j->p == '}') { j->p++; return v; }
        while (!j->bad) {
            const char* key;
            fcJsonWs(j);
            key = fcJsonString(j);
            fcJsonWs(j);
            if (*j->p != ':') { j->bad = true; break; }
            j->p++;
            fcListAdd(j->c, v->keys, (void*)key);
            fcListAdd(j->c, v->items, fcJsonValue(j));
            fcJsonWs(j);
            if (*j->p == ',') { j->p++; continue; }
            if (*j->p == '}') { j->p++; break; }
            j->bad = true;
        }
    } else if (*j->p == '[') {
        v->kind = FJ_ARR; v->items = fcListNew(j->c);
        j->p++;
        fcJsonWs(j);
        if (*j->p == ']') { j->p++; return v; }
        while (!j->bad) {
            fcListAdd(j->c, v->items, fcJsonValue(j));
            fcJsonWs(j);
            if (*j->p == ',') { j->p++; continue; }
            if (*j->p == ']') { j->p++; break; }
            j->bad = true;
        }
    } else if (*j->p == '"') { v->kind = FJ_STR; v->s = fcJsonString(j); }
    else if (strncmp(j->p, "true", 4) == 0) { v->kind = FJ_BOOL; v->b = true; j->p += 4; }
    else if (strncmp(j->p, "false", 5) == 0) { v->kind = FJ_BOOL; j->p += 5; }
    else if (strncmp(j->p, "null", 4) == 0) { v->kind = FJ_NULL; j->p += 4; }
    else { char* end; v->kind = FJ_NUM; v->n = strtod(j->p, &end); if (end == j->p) j->bad = true; j->p = end; }
    return v;
}
static FcJson* fcJsonGet(const FcJson* o, const char* key) {
    int i;
    if (!o || o->kind != FJ_OBJ) return NULL;
    for (i = 0; i < o->keys->count; i++) if (fcAsciiIEq((const char*)o->keys->items[i], key)) return (FcJson*)o->items->items[i];
    return NULL;
}
static const char* fcJsonStr(const FcJson* o, const char* key) { FcJson* v = fcJsonGet(o, key); return v && v->kind == FJ_STR ? v->s : NULL; }

/* ---- the store of the installed packages ---- */
typedef struct FcPkgImport {
    const char* name;
    bool standard;
    const char* prelude;          /* relative to the package's folder, NULL: none */
    FcList* requires;             /* fc8 */
    bool notSerializable;         /* "serializable": false */
    bool hasNative;
    FcList* functions;            /* names */
    FcList* returns;              /* their "returns" (NULL: none), in the same order */
    FcList* hosts;                /* their "host" (FC_IVAL(0/1)), in the same order */
    const char* dir;              /* the package's folder */
    const char* packageName;      /* the "name" of the package */
    const char* packageVersion;   /* its "version" (a project's reference to it, Phase D 3) */
    bool overlay;                 /* the natives of a project (ProjectNativeOverlay, Phase D 3): no package on the disk */
} FcPkgImport;

typedef struct FcStore { const char* root; FcList* imports; bool loaded; } FcStore;

static int fcCmpIgnoreCase(const void* a, const void* b) {
    const char* x = *(const char* const*)a;
    const char* y = *(const char* const*)b;
    for (;; x++, y++) {
        int cx = (unsigned char)*x, cy = (unsigned char)*y;
        if (cx >= 'a' && cx <= 'z') cx -= 32;
        if (cy >= 'a' && cy <= 'z') cy -= 32;
        if (cx != cy || !cx) return cx - cy;
    }
}

#ifndef _WIN32
#include <dirent.h>
#endif
static void fcStoreLoad(FcCtx* c, FcStore* store) {
    FcList* dirs = fcListNew(c);
    int i, k, f;
    store->loaded = true;
    store->imports = fcListNew(c);
#ifndef _WIN32
    {
        DIR* d = opendir(store->root);
        struct dirent* e;
        if (!d) return;
        while ((e = readdir(d)) != NULL) {
            if (e->d_name[0] == '.') continue;
            fcListAdd(c, dirs, (void*)fcFmt(c, "%s/%s", store->root, e->d_name));
        }
        closedir(d);
    }
#endif
    if (dirs->count > 1) qsort(dirs->items, (size_t)dirs->count, sizeof(void*), fcCmpIgnoreCase);
    for (i = 0; i < dirs->count; i++) {
        const char* dir = (const char*)dirs->items[i];
        size_t len = 0;
        uint8_t* text = fbReadFile(fcFmt(c, "%s/package.json", dir), &len);
        FcJsonReader j;
        FcJson* manifest;
        FcJson* imports;
        if (!text) continue;
        text = (uint8_t*)realloc(text, len + 1);
        text[len] = 0;
        j.c = c; j.p = (const char*)text; j.bad = false;
        if (len >= 3 && text[0] == 0xEF && text[1] == 0xBB && text[2] == 0xBF) j.p += 3;
        manifest = fcJsonValue(&j);
        free(text);
        if (j.bad || manifest->kind != FJ_OBJ) continue;
        imports = fcJsonGet(manifest, "imports");
        if (!imports || imports->kind != FJ_ARR) continue;
        for (k = 0; k < imports->items->count; k++) {
            FcJson* im = (FcJson*)imports->items->items[k];
            FcPkgImport* pi = (FcPkgImport*)fcAlloc(c, sizeof *pi);
            FcJson* v;
            pi->name = fcJsonStr(im, "name");
            if (!pi->name) continue;
            v = fcJsonGet(im, "standard"); pi->standard = v && v->kind == FJ_BOOL && v->b;
            pi->prelude = fcJsonStr(im, "prelude");
            v = fcJsonGet(im, "serializable"); pi->notSerializable = v && v->kind == FJ_BOOL && !v->b;
            pi->requires = fcListNew(c);
            v = fcJsonGet(im, "requires");
            if (v && v->kind == FJ_ARR) for (f = 0; f < v->items->count; f++) if (((FcJson*)v->items->items[f])->kind == FJ_STR) fcListAdd(c, pi->requires, (void*)((FcJson*)v->items->items[f])->s);
            pi->functions = fcListNew(c);
            pi->returns = fcListNew(c);
            pi->hosts = fcListNew(c);
            pi->packageName = fcJsonStr(manifest, "name");
            pi->packageVersion = fcJsonStr(manifest, "version");
            v = fcJsonGet(im, "native");
            if (v && v->kind == FJ_OBJ) {
                FcJson* fns = fcJsonGet(v, "functions");
                pi->hasNative = true;
                if (fns && fns->kind == FJ_ARR)
                    for (f = 0; f < fns->items->count; f++) {
                        FcJson* fn = (FcJson*)fns->items->items[f];
                        if (!fcJsonStr(fn, "name")) continue;
                        fcListAdd(c, pi->functions, (void*)fcJsonStr(fn, "name"));
                        fcListAdd(c, pi->returns, (void*)fcJsonStr(fn, "returns"));
                        { FcJson* h = fcJsonGet(fn, "host"); fcListAdd(c, pi->hosts, FC_IVAL(h && h->kind == FJ_BOOL && h->b)); }
                    }
            }
            pi->dir = dir;
            fcListAdd(c, store->imports, pi);
        }
    }
}
static FcPkgImport* fcStoreFindImport(FcCtx* c, FcStore* store, const char* name) {
    int i;
    if (!store->loaded) fcStoreLoad(c, store);
    for (i = 0; i < store->imports->count; i++) if (fcAsciiIEq(((FcPkgImport*)store->imports->items[i])->name, name)) return (FcPkgImport*)store->imports->items[i];
    return NULL;
}
static const char* fcAsciiLower(FcCtx* c, const char* s) {
    char* r = (char*)fcStr(c, s);
    char* q;
    for (q = r; *q; q++) if (*q >= 'A' && *q <= 'Z') *q = (char)(*q - 'A' + 'a');
    return r;
}
static const char* fcImportKey(FcCtx* c, const FcPkgImport* pi) { return fcFmt(c, "pkg:%s", fcAsciiLower(c, pi->name)); }
static FcPkgImport* fcStoreFindKey(FcCtx* c, FcStore* store, const char* key) {
    return fcStartsWith(key, "pkg:") ? fcStoreFindImport(c, store, key + 4) : NULL;
}

/* StandardPackages.Bridges */
static const char* const fcStandardBridges[] = { "graphics", "windows", "devices", "io", "filesystem", "dialogs", "ui", "linq", "reflection", "time", "random", "testing", "net",
    "process", "hash", "compression", "ai", "ai.agent", "ai.models", "assistant", "mcp", "tls", "http", "http.rpc", "http.pages", "data", "data.sqlite", "data.memory", "data.mysql",
    "data.sqlserver", "data.postgres", "http.data", "gpio", "i2c", "spi", "wifi", "audio", "usb", "tracker", "scene3d", "scene3d.terrain", "scene3d.effects", "scene3d.physics",
    "ui.docking", "json", "net.channel", "circuits", "circuits.ui", "console", "machine", "dos", "xms", "x86" };

/* ---- the linker ---- */
typedef struct FcLinker {
    FcCtx* c;
    FcStore store;
    FcDict nativeImports;       /* HashSet<string> */
    FcDict natives;             /* the names of the NativeRegistry */
    FcNatives registry;         /* the NativeRegistry with its indexes */
    int directiveIntWidth, directiveFloatWidth;   /* FC_NULL_INT: none */
    int executionMode;          /* AssemblyInfo.ExecutionMode: 0 Debug, 1 Release, 2 Performance */
    FcAsmInfo asmInfo;          /* the rest of AssemblyInfo (the subsystem, the version info, the icon) */
} FcLinker;

/* ImportedPreludes.ParseImportName */
static const char* fcParseImportName(FcLinker* L, const char* name) {
    FcCtx* c = L->c;
    const char* lower = fcAsciiLower(c, name);
    FcPkgImport* pi;
    size_t i;
    bool bridge = false;
    if (streq0(lower, "graphics") || streq0(lower, "windows") || streq0(lower, "ui") || streq0(lower, "linq") || streq0(lower, "reflection")) return lower;
    if ((pi = fcStoreFindImport(c, &L->store, name)) != NULL) return fcImportKey(c, pi);
    for (i = 0; i < sizeof fcStandardBridges / sizeof fcStandardBridges[0]; i++) if (streq0(lower, fcStandardBridges[i])) bridge = true;
    if (bridge) fcThrow(c, FC_ERR_PRE, 0, 0, NULL, fcFmt(c, "The standard package 'fire-%s' for `#import \"%s\"` is not installed (start ember or spark once, or `ember install fire-%s`).", lower, name, lower));
    fcThrow(c, FC_ERR_PRE, 0, 0, NULL, fcFmt(c, "'%s' is not a known extension (installed packages: `ember list`, available ones: `ember find`; a library project of the solution needs a reference in the project file).", name));
}

/* ImportedPreludes.WithDependencies */
static void fcWithDependencies(FcLinker* L, const char* importKey, FcDict* visited, FcList* out) {
    FcCtx* c = L->c;
    int i;
    if (fcStartsWith(importKey, "pkg:")) {
        FcPkgImport* pi;
        if (!fcSetAdd(visited, importKey)) return;
        if ((pi = fcStoreFindKey(c, &L->store, importKey)) != NULL)
            for (i = 0; i < pi->requires->count; i++) fcWithDependencies(L, fcParseImportName(L, (const char*)pi->requires->items[i]), visited, out);
        fcListAdd(c, out, (void*)importKey);
        return;
    }
    if (streq0(importKey, "windows")) fcListAdd(c, out, (void*)"graphics");
    if (streq0(importKey, "ui")) { fcListAdd(c, out, (void*)"graphics"); fcListAdd(c, out, (void*)"windows"); fcListAdd(c, out, (void*)"reflection"); }
    if (streq0(importKey, "linq")) fcListAdd(c, out, (void*)"reflection");
    fcListAdd(c, out, (void*)importKey);
}

/* AssemblyInfo.FileVersion/ProductVersion = text: four numbers (int.TryParse: blanks around them, a sign), else the version stays */
static void fcVersionSet(int* v, const char* text) {
    int n[4], k;
    const char* p = text;
    for (k = 0; k < 4; k++) {
        char* end;
        long x;
        while (*p == ' ' || (*p >= 9 && *p <= 13)) p++;
        if (!((*p >= '0' && *p <= '9') || ((*p == '-' || *p == '+') && p[1] >= '0' && p[1] <= '9'))) return;
        x = strtol(p, &end, 10);
        if (x > 2147483647L || x < -2147483647L - 1) return;
        p = end;
        while (*p == ' ' || (*p >= 9 && *p <= 13)) p++;
        if (k < 3 ? *p != '.' : *p != 0) return;
        if (k < 3) p++;
        n[k] = (int)x;
    }
    for (k = 0; k < 4; k++) v[k] = n[k];
}
/* the directives of the version info and the icon (Linker: #name, #codename, #description, #author, #comments, #icon, #version, #fileversion) */
static void fcLinkerAsmInfo(FcAsmInfo* a, const char* name, const char* value) {
    if (streq0(name, "name")) a->productName = value;
    else if (streq0(name, "codename")) a->internalName = value;
    else if (streq0(name, "description")) a->fileDescription = value;
    else if (streq0(name, "author")) a->companyName = value;
    else if (streq0(name, "comments")) a->comments = value;
    else if (streq0(name, "icon")) a->iconPath = value;
    else if (streq0(name, "version")) fcVersionSet(a->productVersion, value);
    else if (streq0(name, "fileversion")) fcVersionSet(a->fileVersion, value);
}
/* the directives of a build with their effects (Linker.CompileAndLink's registry) */
static void fcLinkerDirective(FcCtx* c, void* state, const char* name, FcList* args, int line, FcList* importNamespaces) {
    FcLinker* L = (FcLinker*)state;
    FcToken* arg = args->count > 0 ? (FcToken*)args->items[0] : NULL;
    (void)line;
    if (streq0(name, "import")) {
        FcList* keys;
        FcPkgImport* pi;
        int i;
        if (arg->type != TT_StringLiteral) fcThrow(c, FC_ERR_LINKER, 1, 0, NULL, "Wrong arguments for the 'import' directive.");
        keys = fcListNew(c);
        fcWithDependencies(L, fcParseImportName(L, arg->stringValue), fcDictNew(c, FC_KEY_STR), keys);
        for (i = 0; i < keys->count; i++) fcSetAdd(&L->nativeImports, keys->items[i]);
        /* ProjectLibraries.PackageNamespaceOf */
        pi = fcStoreFindImport(c, &L->store, arg->stringValue);
        if (pi && !pi->standard && pi->prelude && pi->prelude[0] && !fcListHasStr(importNamespaces, pi->name)) fcListAdd(c, importNamespaces, (void*)pi->name);
        return;
    }
    if (streq0(name, "debug")) { L->executionMode = 0; return; }
    if (streq0(name, "performance")) { L->executionMode = 2; return; }
    if (streq0(name, "floatwidth")) {
        if (!(arg->type == TT_IntLiteral && (arg->intValue == 32 || arg->intValue == 64))) fcThrow(c, FC_ERR_LINKER, 1, 0, NULL, "The 'floatwidth' directive expects 32 or 64.");
        L->directiveFloatWidth = (int)arg->intValue;
        return;
    }
    if (streq0(name, "intwidth")) {
        if (!(arg->type == TT_IntLiteral && (arg->intValue == 16 || arg->intValue == 32 || arg->intValue == 64))) fcThrow(c, FC_ERR_LINKER, 1, 0, NULL, "The 'intwidth' directive expects 16, 32 or 64.");
        L->directiveIntWidth = (int)arg->intValue;
        return;
    }
    if (streq0(name, "name") || streq0(name, "codename") || streq0(name, "description") || streq0(name, "author") || streq0(name, "comments") || streq0(name, "icon")
        || streq0(name, "version") || streq0(name, "fileversion")) {
        if (arg->type != TT_StringLiteral) fcThrow(c, FC_ERR_LINKER, 1, 0, NULL, fcFmt(c, "Wrong arguments for the '%s' directive.", name));
        fcLinkerAsmInfo(&L->asmInfo, name, arg->stringValue);
    }
    if (streq0(name, "noconsole")) L->asmInfo.gui = true;
}

static void fcRegisterNative(FcLinker* L, const char* name) { fcSetAdd(&L->natives, name); fcNativesRegister(L->c, &L->registry, name); }

/* the text of a file as fc8 units (File.ReadAllText) into the arena */
static bool fcReadSource(FcCtx* c, const char* path, const uint16_t** source, int* length) {
    FcU16 text = {0};
    uint16_t* copy;
    if (!fcReadText(path, &text)) return false;
    copy = (uint16_t*)fcAlloc(c, sizeof(uint16_t) * (size_t)(text.len + 1));
    if (text.len) memcpy(copy, text.p, sizeof(uint16_t) * (size_t)text.len);
    *source = copy;
    *length = text.len;
    fcU16Free(&text);
    return true;
}

static FcProcessed* fcPreludeOf(FcLinker* L, FcPkgImport* pi, const char* key) {
    FcCtx* c = L->c;
    const char* path = fcFmt(c, "%s/%s", pi->dir, pi->prelude);
    const uint16_t* src;
    int len;
    FcProcessed* p;
    if (!fcReadSource(c, path, &src, &len)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "cannot read the prelude '%s'", path));
    p = fcPreprocess(c, src, len, fcDirectoryName(c, fcFullPath(c, ".", path)));
    p->module = key;
    p->notSerializable = pi->notSerializable;
    p->qualify = !pi->standard && pi->prelude && pi->prelude[0] ? pi->name : NULL;
    return p;
}

static FcProcessed* fcBuiltInPrelude(FcLinker* L, const char* name) {
    FcCtx* c = L->c;
    FcPkgImport* pi = fcStoreFindImport(c, &L->store, name);
    if (!pi || !pi->prelude || !pi->prelude[0])
        fcThrow(c, FC_ERR_PRE, 0, 0, NULL, fcFmt(c, "The standard package 'fire-%s' for `#import \"%s\"` is not installed (start ember or spark once, or `ember install fire-%s`).", name, name, name));
    return fcPreludeOf(L, pi, name);
}

#define FC_BRIDGE_graphics 1
#define FC_BRIDGE_windows 2
#define FC_BRIDGE_reflection 3
static void fcRegisterBridge(FcLinker* L, int which) {
#define FIRE_BRIDGE_NATIVE(import, index, name) if (which == FC_BRIDGE_##import) fcRegisterNative(L, name);
#include "../tables/bridges.def"
#undef FIRE_BRIDGE_NATIVE
}

/* ImportedPreludes.Insert: the preludes behind the standard prelude (each insertion before the earlier ones); the number inserted */
static int fcInsertPreludes(FcLinker* L, FcList* processed) {
    FcCtx* c = L->c;
    int inserted = 0, i;
    FcList* keys = fcListNew(c);
    if (fcDictHas(&L->nativeImports, "graphics")) { fcListInsert(c, processed, 1, fcBuiltInPrelude(L, "graphics")); inserted++; fcRegisterBridge(L, FC_BRIDGE_graphics); }
    if (fcDictHas(&L->nativeImports, "windows")) { fcListInsert(c, processed, 1, fcBuiltInPrelude(L, "windows")); inserted++; fcRegisterBridge(L, FC_BRIDGE_windows); }
    if (fcDictHas(&L->nativeImports, "ui")) { fcListInsert(c, processed, 1, fcBuiltInPrelude(L, "ui")); inserted++; }
    if (fcDictHas(&L->nativeImports, "linq")) { fcListInsert(c, processed, 1, fcBuiltInPrelude(L, "linq")); inserted++; }
    if (fcDictHas(&L->nativeImports, "reflection")) { fcListInsert(c, processed, 1, fcBuiltInPrelude(L, "reflection")); inserted++; fcRegisterBridge(L, FC_BRIDGE_reflection); }
    FC_DICT_EACH(&L->nativeImports, e) if (fcStartsWith((const char*)e->key, "pkg:")) fcListAdd(c, keys, (void*)e->key);
    if (keys->count > 1) qsort(keys->items, (size_t)keys->count, sizeof(void*), fcCmpOrdinal);
    for (i = 0; i < keys->count; i++) {
        const char* key = (const char*)keys->items[i];
        FcPkgImport* pi = fcStoreFindKey(c, &L->store, key);
        int f;
        if (!pi) fcThrow(c, FC_ERR_LINKER, 1, 0, NULL, fcFmt(c, "The import '%s' belongs to a package that is not installed.", key + 4));
        if (pi->prelude && pi->prelude[0]) { fcListInsert(c, processed, 1, fcPreludeOf(L, pi, key)); inserted++; }
        for (f = 0; f < pi->functions->count; f++) fcRegisterNative(L, (const char*)pi->functions->items[f]);
    }
    return inserted;
}

/* PackageImports.NativeReturnTypes */
static FcDict* fcNativeReturnTypes(FcLinker* L) {
    FcCtx* c = L->c;
    FcDict* types = fcDictNew(c, FC_KEY_STR);
    int f;
    FC_DICT_EACH(&L->nativeImports, e) {
        FcPkgImport* pi;
        if (!fcStartsWith((const char*)e->key, "pkg:") || !(pi = fcStoreFindKey(c, &L->store, (const char*)e->key)) || !pi->hasNative) continue;
        for (f = 0; f < pi->functions->count; f++) {
            const char* ret = (const char*)pi->returns->items[f];
            const char* t;
            const char* end;
            if (!ret) continue;
            for (t = ret; *t == ' ' || *t == '\t'; t++) {}
            if (!*t) continue;
            end = t + strlen(t);
            while (end > t && (end[-1] == ' ' || end[-1] == '\t')) end--;
            fcDictAdd(types, pi->functions->items[f], (void*)fcStrN(c, t, (size_t)(end - t)));
        }
    }
    return types;
}

/* the result of the frontend: the program as the type checker left it */
typedef struct FcFrontend { FcList* program; FcResolveResult* resolved; int firstUserSource; FcList* processed; FcLinker* linker; const uint16_t* userText; int userLength; } FcFrontend;

static void fcTypeCheckRun(FcCtx* c, FcList* program, FcResolveResult* resolved, bool strict, int firstUserSource, int reportFrom, int intWidth, int floatWidth, FcDict* nativeReturns);

/* Linker.CompileAndLink up to and with the type check (FrontendOnly) for one file */
static FcFrontend* fcCompileFrontend(FcCtx* c, const char* path, const char* preludePath, const char* storeRoot, bool strict) {
    FcLinker* L = (FcLinker*)fcAlloc(c, sizeof *L);
    FcList* processed = fcListNew(c);
    FcFrontend* fe = (FcFrontend*)fcAlloc(c, sizeof *fe);
    const uint16_t* src;
    int len, firstUserSource = 1;
    const char* full = fcFullPath(c, ".", path);
    const char* folder = fcDirectoryName(c, full);
    FcProcessed* p;
    FcProcessed** arr;
    int i, intWidth, floatWidth;
    L->c = c;
    L->store.root = storeRoot;
    L->directiveIntWidth = L->directiveFloatWidth = FC_NULL_INT;
    fcDictInit(c, &L->nativeImports, FC_KEY_STR);
    fcDictInit(c, &L->natives, FC_KEY_STR);
    fcNativesInit(c, &L->registry);
    L->executionMode = 1;
    /* ("print" and the base natives: natives.def is NativeRegistry.CreateDefault) */
#define FIRE_NATIVE(index, name, tryable) fcRegisterNative(L, name);
#include "../tables/natives.def"
#undef FIRE_NATIVE
    fcSetAdd(&L->nativeImports, "print");
    fcSymbolsForHost(c, c->symbols);
    c->sharedIncluded = fcListNew(c);
    fcListAdd(c, c->sharedIncluded, (void*)full);
    c->resourcePaths = fcListNew(c);
    c->resourceNames = fcListNew(c);
    c->directiveHook = fcLinkerDirective;
    c->directiveState = L;
    if (!fcReadSource(c, preludePath, &src, &len)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "cannot read the prelude '%s'", preludePath));
    fcListAdd(c, processed, fcPreprocess(c, src, len, folder));
    if (!fcReadSource(c, full, &src, &len)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "cannot read '%s'", path));
    fe->userText = src;
    fe->userLength = len;
    fe->linker = L;
    p = fcPreprocess(c, src, len, folder);
    p->name = fcStr(c, fcBaseName(path));
    fcListAdd(c, processed, p);
    firstUserSource += fcInsertPreludes(L, processed);
    c->directiveHook = NULL;
    arr = (FcProcessed**)fcAlloc(c, sizeof(FcProcessed*) * (size_t)processed->count);
    for (i = 0; i < processed->count; i++) arr[i] = (FcProcessed*)processed->items[i];
    fe->program = fcParseMultiple(c, arr, processed->count);
    fe->resolved = fcResolve(c, fe->program, &L->natives, NULL);
    fe->firstUserSource = firstUserSource;
    fe->processed = processed;
    intWidth = L->directiveIntWidth != FC_NULL_INT ? L->directiveIntWidth : 64;
    floatWidth = L->directiveFloatWidth != FC_NULL_INT ? L->directiveFloatWidth : 64;
    fcTypeCheckRun(c, fe->program, fe->resolved, strict, firstUserSource, firstUserSource, intWidth, floatWidth, fcNativeReturnTypes(L));
    return fe;
}

/* ---- the rest of Linker.CompileAndLink: the bytecode compiler and the linked program ---- */

/* FloatNarrowing (src/fire/Runtime/FloatNarrowing.cs): the constants of every chunk once, narrowed */
typedef FcConst* (*FcNarrow)(FcCtx* c, const FcConst* v, int bits);
static FcConst* fcNarrowFloat(FcCtx* c, const FcConst* v, int bits) {
    FcConst* k;
    (void)bits;
    if (v->kind != CK_Float) return NULL;
    k = (FcConst*)fcAlloc(c, sizeof *k); *k = *v;
    k->f = (double)(float)v->f;
    return k;
}
static int64_t fcCutInt(int64_t v, int bits) { return bits == 16 ? (int64_t)(int16_t)v : (int64_t)(int32_t)v; }
static FcConst* fcNarrowInt(FcCtx* c, const FcConst* v, int bits) {
    FcConst* k;
    if (!(v->kind == CK_Int && v->wide == 0 && fcCutInt(v->i, bits) != v->i)) return NULL;
    k = (FcConst*)fcAlloc(c, sizeof *k); *k = *v;
    k->i = v->en ? (int64_t)(int32_t)fcCutInt(v->i, bits) : fcCutInt(v->i, bits);
    if (v->en) k->width = 64;
    return k;
}
typedef struct FcNarrowing { FcCtx* c; FcDict* seen; FcNarrow narrow; int bits; } FcNarrowing;
static void fcNarrowChunk(FcNarrowing* n, FcChunk* chunk);
static void fcNarrowProto(FcNarrowing* n, FcProto* proto) {
    int i;
    if (!proto || !fcSetAdd(n->seen, proto->chunk)) return;
    fcNarrowChunk(n, proto->chunk);
    for (i = 0; i < proto->paramDefaults->count; i++) fcNarrowProto(n, (FcProto*)proto->paramDefaults->items[i]);
}
static void fcNarrowChunk(FcNarrowing* n, FcChunk* chunk) {
    int i;
    for (i = 0; i < chunk->constants->count; i++) {
        FcConst* narrowed = n->narrow(n->c, (FcConst*)chunk->constants->items[i], n->bits);
        if (narrowed) chunk->constants->items[i] = narrowed;
    }
    for (i = 0; i < chunk->functions->count; i++) fcNarrowProto(n, (FcProto*)chunk->functions->items[i]);
}
static void fcNarrow(FcCtx* c, FcCompiled* program, FcNarrow narrow, int bits) {
    FcNarrowing n;
    int i;
    n.c = c; n.seen = fcDictNew(c, FC_KEY_PTR); n.narrow = narrow; n.bits = bits;
    if (fcSetAdd(n.seen, program->topLevel)) fcNarrowChunk(&n, program->topLevel);
    FC_DICT_EACH(program->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        for (i = 0; i < rc->fieldInits->count; i++) fcNarrowProto(&n, (FcProto*)rc->fieldInits->items[i]);
        for (i = 0; i < rc->staticFieldInits->count; i++) fcNarrowProto(&n, (FcProto*)rc->staticFieldInits->items[i]);
        FC_DICT_EACH(rc->methods, m) { FcList* overloads = (FcList*)m->value; int k; for (k = 0; k < overloads->count; k++) fcNarrowProto(&n, (FcProto*)overloads->items[k]); }
        for (i = 0; i < rc->ctorProtos->count; i++) fcNarrowProto(&n, (FcProto*)rc->ctorProtos->items[i]);
        for (i = 0; i < rc->extraConstructors->count; i++) fcNarrowProto(&n, (FcProto*)rc->extraConstructors->items[i]);
        fcNarrowProto(&n, rc->destructor);
    }
}

/* PackageLibrary.Safe */
static const char* fcPkgSafe(FcCtx* c, const char* text) {
    FcBuf b = {0};
    const unsigned char* p = (const unsigned char*)text;
    fcBufStr(&b, "");
    while (*p) {
        uint16_t u = fc8Next(&p);
        if (fcIsLetterOrDigit(u)) { if (u < 0x80) fcBufChar(&b, (char)tolower(u)); else fcBufUnit(&b, u); }
        else fcBufChar(&b, '_');
    }
    return fcBufDone(c, &b);
}
#ifdef _WIN32
#define FC_LIB_PREFIX ""
#define FC_LIB_EXT ".dll"
#elif defined(__APPLE__)
#define FC_LIB_PREFIX "lib"
#define FC_LIB_EXT ".dylib"
#else
#define FC_LIB_PREFIX "lib"
#define FC_LIB_EXT ".so"
#endif
/* PackageImports.NativesOf */
static void fcPackageNativesOf(FcLinker* L, FcList** names, FcList** libraries) {
    FcCtx* c = L->c;
    FcList* keys = fcListNew(c);
    int i, f;
    *names = fcListNew(c);
    *libraries = fcListNew(c);
    FC_DICT_EACH(&L->nativeImports, e) if (fcStartsWith((const char*)e->key, "pkg:")) fcListAdd(c, keys, (void*)e->key);
    if (keys->count > 1) qsort(keys->items, (size_t)keys->count, sizeof(void*), fcCmpOrdinal);
    for (i = 0; i < keys->count; i++) {
        FcPkgImport* pi = fcStoreFindKey(c, &L->store, (const char*)keys->items[i]);
        if (!pi || !pi->hasNative) continue;
        for (f = 0; f < pi->functions->count; f++) {
            const char* name = (const char*)pi->functions->items[f];
            if (fcListHasStr(*names, name)) continue;
            fcListAdd(c, *names, (void*)name);
            if (FC_VALI(pi->hosts->items[f])) fcListAdd(c, *libraries, (void*)"@host");
            else fcListAdd(c, *libraries, (void*)fcFmt(c, FC_LIB_PREFIX "fire_pkg_%s_%s" FC_LIB_EXT, fcPkgSafe(c, pi->packageName ? pi->packageName : ""), fcPkgSafe(c, pi->name)));
        }
    }
}

/* Linker.CompileAndLink for one file (as `forge bytecode <file> [-m <mode>]`): the linked program. modeOverride: -1 none */
static FcLinked* fcLinkFile(FcCtx* c, const char* path, const char* preludePath, const char* storeRoot, bool strict, int modeOverride) {
    FcFrontend* fe = fcCompileFrontend(c, path, preludePath, storeRoot, strict);
    FcLinker* L = fe->linker;
    FcCompiled* compiled = fcCompile(c, fe->program, fe->resolved, &L->registry, false);
    FcLinked* linked = (FcLinked*)fcAlloc(c, sizeof *linked);
    FcDict* notSerializable = fcDictNew(c, FC_KEY_STR);
    int i, total = fe->processed->count;
    for (i = 0; i < fe->processed->count; i++) if (((FcProcessed*)fe->processed->items[i])->notSerializable) fcSetAdd(notSerializable, fcFmt(c, "%d", i));
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
    linked->program = compiled;
    linked->floatWidth = L->directiveFloatWidth != FC_NULL_INT ? L->directiveFloatWidth : 64;
    if (linked->floatWidth == 32) fcNarrow(c, compiled, fcNarrowFloat, 32);
    linked->intWidth = L->directiveIntWidth != FC_NULL_INT ? L->directiveIntWidth : 64;
    if (linked->intWidth < 64) fcNarrow(c, compiled, fcNarrowInt, linked->intWidth);
    linked->wordWidth = 64;
    linked->firstUserSource = fe->firstUserSource;
    linked->mode = modeOverride >= 0 ? modeOverride : L->executionMode;
    linked->asmInfo = &L->asmInfo;
    linked->nativeImports = &L->nativeImports;
    fcPackageNativesOf(L, &linked->packageNatives, &linked->packageNativeLibraries);
    linked->graphicsNatives = NULL;
    /* SourceFilesOf / SourceHashesOf: the user's file is the last source */
    linked->sourceFiles = fcListNew(c);
    linked->sourceHashes = fcListNew(c);
    for (i = 0; i < total; i++) { fcListAdd(c, linked->sourceFiles, NULL); fcListAdd(c, linked->sourceHashes, NULL); }
    if (total >= 1) {
        linked->sourceFiles->items[total - 1] = (void*)fcFullPath(c, ".", path);
        linked->sourceHashes->items[total - 1] = (void*)fcSha256OfText(c, fc8FromU16(c, fe->userText, fe->userLength));
    }
    return linked;
}
