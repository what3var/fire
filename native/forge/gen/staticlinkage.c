/* native/forge/gen/staticlinkage.c - src/fire.Native/StaticLinkage.cs (commit 661e55b3) and src/fire/Runtime/ModuleInterface.cs (Hash): linkage "static" and
 * "shared" in the generator - the natives in C of a library module in its file, the `fire_cn_` functions, the keys of the libraries in the user-wide cache,
 * the interface files (.fireiface, JSON as System.Text.Json writes it), GenerateStatic, and for linkage "shared" on Windows the DLLs (HostDecl, the link
 * functions, the table of the host). In the order of the C# file. The keys name the generator by fgGeneratorIdentity (C#: GeneratorIdentity, the version of
 * its assembly): forge-c names itself by the time it was compiled - the cache of forge-c is its own; the measurements (`cgen static`) set a fixed text. */

/* ---- the library and the plan (StaticLibrary, StaticBuildPlan) ---- */
typedef struct FgStaticLibrary { const char* module; const char* key; const char* directory; const char* cFile; const char* libraryName; const char* interfaceFile; bool cached; } FgStaticLibrary;
typedef struct FgPluginHostModule { const char* name; const char* abi; const char* library; } FgPluginHostModule;
typedef struct FgPluginHostInfo { int format; const char* target; const char* flags; const char* head; const char* runtime; FcList* modules; } FgPluginHostInfo;
typedef struct FgStaticPlan { const char* programC; const char* natives; FcList* libraries; const char* log; FgPluginHostInfo* host; FcList* funcs; } FgStaticPlan;
static const char* fgLibraryFile(FcCtx* c, const FgStaticLibrary* l, const char* extension) { return fcFmt(c, "%s/%s%s", l->directory, l->libraryName, extension); }

static const char* fgGeneratorIdentity = "forge-c " __DATE__ " " __TIME__;
#define FG_INTERFACE_FORMAT 2   /* InterfaceFormat (2: the units as ProgramFile writes them) */
#define FG_INTERFACE_EXTENSION ".fireiface"

/* ---- the natives in C of a library module ---- */
/* NativesOwner: the library module whose import the natives in C belong to (linkage "static": they live in its file) - NULL: the program has them. */
static FgModuleSpec* NativesOwnerSpec(FgGen* g, const char* import) {
    FgModuleSpec* module = NULL;
    if (!g->separate) return NULL;
    FC_DICT_EACH(g->moduleOfSource, e) if (fcAsciiIEq(((FgModuleSpec*)e->value)->name, import)) { module = (FgModuleSpec*)e->value; break; }
    return module && NativesStandAlone(g, module->name) ? module : NULL;
}

/* the natives in C of an import with their signatures (the C function, arguments, the scope list) */
typedef struct FgCNative { const char* function; int argc; bool needsList; } FgCNative;
static FcList* NativeFunctionsOf(FgGen* g, const char* import) {
    FcList* l = fcListNew(g->c);
    int i;
    if (fcAsciiIEq(import, "graphics") || fcAsciiIEq(import, "windows")) {
        bool windows = fcAsciiIEq(import, "windows");
        FcDict* natives = windows ? BridgeOf(g, "windows", "__GRPHWin") : BridgeOf(g, "graphics", "__GRPH");
        FC_DICT_EACH(natives, e) {
            FgPkgFn* fn = (FgPkgFn*)e->value;
            FgCNative* n = (FgCNative*)fcAlloc(g->c, sizeof *n);
            n->function = windows ? fcFmt(g->c, "fire_win_Win%s", (const char*)e->key) : fcFmt(g->c, "fire_gfx_%s", (const char*)e->key);
            n->argc = fn->arguments;
            /* (graphics: whether it takes the list; windows: as C# - the tuple's second flag, whether the result is a reference) */
            n->needsList = windows ? fn->returnsReference : fn->needsList;
            fcListAdd(g->c, l, n);
        }
        return l;
    }
    for (i = 0; i < g->packageImports->count; i++) {
        FgPkgImportKey* p = (FgPkgImportKey*)g->packageImports->items[i];
        int k;
        if (!streq0(p->key, import)) continue;
        if (p->import->native && p->import->native->isC)
            for (k = 0; k < p->import->native->functions->count; k++) {
                FgPkgFn* fn = (FgPkgFn*)p->import->native->functions->items[k];
                FgCNative* n = (FgCNative*)fcAlloc(g->c, sizeof *n);
                n->function = fgPkgFnSymbol(fn); n->argc = fn->arguments; n->needsList = fn->needsList;
                fcListAdd(g->c, l, n);
            }
        break;
    }
    return l;
}

static const char* CnFunction(FgGen* g, const char* function, int argc, bool needsList);
/* A library module, linkage "static": the natives in C of its own import - they live in its file, every other module and the program call them through its
 * `fire_cn_` functions - and the declarations of those of other modules it calls. */
static const char* LibraryNativesC(FgGen* g) {
    FcBuf sb = {0};
    FcList* keys;
    FgInstImport* package = NULL;
    const char* own = g->self->name;
    bool standard;
    FcList* natives;
    int i;
    fcBufStr(&sb, "");
    /* (linkage "shared" on Windows: those of the program are pointers of this DLL - see HostDecl) */
    keys = fgSortedKeys(g->c, g->sharedNatives, true);
    for (i = 0; i < keys->count; i++) {
        const char* function = (const char*)keys->items[i];
        FgSharedNative* sn = (FgSharedNative*)fcDictGet(g->sharedNatives, function);
        const char* decl = fcFmt(g->c, "Value fire_cn_%s(%s);", function, ThunkParams(g, sn->argc, sn->needsList));
        if (!NativesOwnerSpec(g, sn->import)) fcBufStr(&sb, HostDecl(g, fcFmt(g->c, "fire_cn_%s", function), decl));
        else fcBufFmt(&sb, "%s\n", decl);
    }
    if (NativesOwnerSpec(g, own) != g->self) return fcBufDone(g->c, &sb);
    standard = fcAsciiIEq(own, "graphics") || fcAsciiIEq(own, "windows");
    for (i = 0; i < g->packageImports->count; i++) if (streq0(((FgPkgImportKey*)g->packageImports->items[i])->key, own)) { package = ((FgPkgImportKey*)g->packageImports->items[i])->import; break; }
    if (standard) AppendStandardNatives(g, &sb, fcAsciiLower(g->c, own));
    else if (package && package->native && package->native->isC) {
        FcList* texts = fgReadNativeSources(g->c, g->instImports, g->store, package, fgPlatformKeys(g));
        for (i = 0; i < texts->count; i++) {
            FgNamedText* t = (FgNamedText*)texts->items[i];
            fcBufFmt(&sb, "// ---- package %s %s, import \"%s\" (C): %s\n", package->pkgName, package->pkgVersion, package->name, t->name);
            fcBufFmt(&sb, "%s\n", PackageNativeText(g, t->text));
            fcBufFmt(&sb, "// ---- end of %s\n", t->name);
        }
    }
    else return fcBufDone(g->c, &sb);
    natives = NativeFunctionsOf(g, own);
    for (i = 0; i < natives->count; i++) { FgCNative* n = (FgCNative*)natives->items[i]; fcBufFmt(&sb, "%s\n", CnFunction(g, n->function, n->argc, n->needsList)); }
    return fcBufDone(g->c, &sb);
}

/* NativeCTypes.ValueWrapper: a function with Values around a typed one */
static const char* fgValueWrapper(FgGen* g, const char* name, const char* function, const FgPkgFn* fn, bool needsList, const char* prefix) {
    FcList* ps = fcListNew(g->c);
    FcList* args = fcListNew(g->c);
    const char* call;
    const char* r;
    int i;
    for (i = 0; i < fn->arguments; i++) fcListAdd(g->c, ps, (void*)fcFmt(g->c, "Value a%d", i));
    if (needsList) fcListAdd(g->c, ps, (void*)"OwnList* list");
    for (i = 0; i < fn->arguments; i++) {
        const char* t = fgNativeParamCType(fn, i);
        const char* a = fcFmt(g->c, "a%d", i);
        fcListAdd(g->c, args, (void*)(!t ? a : streq0(t, "Real") ? fcFmt(g->c, "toR(%s)", a) : streq0(t, "bool") ? fcFmt(g->c, "(%s).i != 0", a) : fcFmt(g->c, "(%s)fire_natI(%s)", t, a)));
    }
    if (needsList) fcListAdd(g->c, args, (void*)"list");
    call = fcFmt(g->c, "%s(%s)", function, fgJoin(g->c, args, ", "));
    r = fgNativeReturnCType(fn);
    return fcFmt(g->c, "%sValue %s(%s) { return %s; }", prefix, name, ps->count == 0 ? "void" : fgJoin(g->c, ps, ", "), r ? fgNativeToValue(g->c, r, call) : call);
}

/* The `fire_cn_` function of a native in C: Values in and out - a typed function (NativeCTypes) gets its Values converted. */
static const char* CnFunction(FgGen* g, const char* function, int argc, bool needsList) {
    const FgPkgFn* fn = NULL;
    FcList* args = fcListNew(g->c);
    int i, k;
    for (i = 0; i < g->packageImports->count && !fn; i++) {
        FgPkgNativeM* n = ((FgPkgImportKey*)g->packageImports->items[i])->import->native;
        if (!n || !n->isC) continue;
        for (k = 0; k < n->functions->count; k++) {
            const FgPkgFn* f = (const FgPkgFn*)n->functions->items[k];
            if (streq0(fgPkgFnSymbol(f), function) && f->arguments == argc) { fn = f; break; }
        }
    }
    if (fn && fgNativeIsTyped(fn, true)) return fgValueWrapper(g, fcFmt(g->c, "fire_cn_%s", function), function, fn, needsList, "");
    for (i = 0; i < argc; i++) fcListAdd(g->c, args, (void*)fcFmt(g->c, "a%d", i));
    if (needsList) fcListAdd(g->c, args, (void*)"list");
    return fcFmt(g->c, "Value fire_cn_%s(%s) { return %s(%s); }", function, ThunkParams(g, argc, needsList), function, fgJoin(g->c, args, ", "));
}

/* A call of a native in C of this import goes to the function itself (it is part of this file) - SharedNative without its bookkeeping. */
static bool NativeIsHere(FgGen* g, const char* import) {
    FgModuleSpec* owner;
    if (!g->separate) return true;
    owner = NativesOwnerSpec(g, import);
    return !owner ? !LibraryRole : owner == g->self;
}

/* The C function a call of a native in C goes to: itself when it is part of this file, otherwise (linkage "static") the `fire_cn_` function of the module whose
 * import it belongs to, or of the program. */
static const char* SharedNative(FgGen* g, const char* function, int argc, bool needsList, const char* import) {
    FgModuleSpec* owner;
    FgSharedNative* sn;
    if (!g->separate) return function;
    owner = NativesOwnerSpec(g, import);
    if (!owner ? !LibraryRole : owner == g->self) return function;
    sn = (FgSharedNative*)fcAlloc(g->c, sizeof *sn);
    sn->argc = argc; sn->needsList = needsList; sn->import = import;
    fcDictPut(g->sharedNatives, function, sn);
    return fcFmt(g->c, "fire_cn_%s", function);
}

static FcList* OpenWorldNativesC(FgGen* g);
/* The program, linkage "static": the `fire_cn_` functions of its natives in C that the library modules call, and the declarations of those of the modules it calls. */
static const char* SharedNativeThunks(FgGen* g) {
    FcDict* all;
    FcList* keys;
    FcList* open;
    FcBuf sb = {0};
    int i, k;
    if (!g->separate || !g->libraries) return "";
    all = fcDictNew(g->c, FC_KEY_STR);
    for (i = 0; i < g->libraries->count; i++) FC_DICT_EACH(((FgModuleOutput*)g->libraries->items[i])->needs->sharedNatives, e) fcDictPut(all, e->key, e->value);
    open = OpenWorldNativesC(g);
    for (i = 0; i < open->count; i += 2) {
        FgCNative* n = (FgCNative*)open->items[i];
        if (!fcDictHas(all, n->function)) {
            FgSharedNative* sn = (FgSharedNative*)fcAlloc(g->c, sizeof *sn);
            sn->argc = n->argc; sn->needsList = n->needsList; sn->import = (const char*)open->items[i + 1];
            fcDictPut(all, n->function, sn);
        }
    }
    fcBufStr(&sb, "");
    keys = fgSortedKeys(g->c, all, true);
    for (k = 0; k < keys->count; k++) {
        FgSharedNative* sn = (FgSharedNative*)fcDictGet(all, keys->items[k]);
        if (NativesOwnerSpec(g, sn->import)) continue;   /* (in the file of their module) */
        fcBufFmt(&sb, "%s\n", CnFunction(g, (const char*)keys->items[k], sn->argc, sn->needsList));
    }
    keys = fgSortedKeys(g->c, g->sharedNatives, true);
    for (k = 0; k < keys->count; k++) {
        FgSharedNative* sn = (FgSharedNative*)fcDictGet(g->sharedNatives, keys->items[k]);
        fcBufFmt(&sb, "Value fire_cn_%s(%s);\n", (const char*)keys->items[k], ThunkParams(g, sn->argc, sn->needsList));
    }
    return fcBufDone(g->c, &sb);
}

/* A program that loads plugins (linkage "shared"): every native in C of the packages whose C is in its file (not in a module's) - pairs (FgCNative*, import). */
static FcList* OpenWorldNativesC(FgGen* g) {
    FcList* l = fcListNew(g->c);
    int i, k;
    if (!g->openWorld || !g->separate) return l;
    for (i = 0; i < g->packageImports->count; i++) {
        FgPkgImportKey* p = (FgPkgImportKey*)g->packageImports->items[i];
        FcList* fns;
        if (!(p->import->native && p->import->native->isC) || fcDictHas(g->standardNativesWritten, fcAsciiLower(g->c, p->import->name)) || NativesOwnerSpec(g, p->key)) continue;
        fns = NativeFunctionsOf(g, p->key);
        for (k = 0; k < fns->count; k++) { fcListAdd(g->c, l, fns->items[k]); fcListAdd(g->c, l, (void*)p->key); }
    }
    return l;
}

/* ---- the interface of a module (ModuleInterface.Hash) ---- */
static const char* fgAccessName(int access) { return access == ACC_Private ? "Private" : access == ACC_Protected ? "Protected" : access == ACC_Public ? "Public" : ""; }
static void fgAbiProto(FgGen* g, FcBuf* sb, const char* what, const FcProto* p) {
    int i;
    fcBufFmt(sb, " %s (%d) %s%s t%d r%u [", what, p->paramCount, fgAccessName(p->access), p->isStatic ? " static" : "", p->typeParamCount, (unsigned)p->refMask);
    if (p->paramTypes) for (i = 0; i < p->paramTypes->count; i++) { const char* t = (const char*)p->paramTypes->items[i]; fcBufFmt(sb, "%s%s", i ? "," : "", t ? t : "?"); }
    fcBufStr(sb, "] d");
    for (i = 0; i < p->paramDefaults->count; i++) fcBufChar(sb, p->paramDefaults->items[i] ? '1' : '0');
    fcBufChar(sb, '\n');
    (void)g;
}
/* The hash of the module made of `classes` (sorted by name) whose code is in `sources`. */
static const char* AbiHash(FgGen* g, const FcLinked* program, const FgModuleSpec* spec) {
    FcBuf sb = {0};
    int i, k;
    fcBufFmt(&sb, "fire-abi %d\n", 1);
    for (i = 0; i < spec->classes->count; i++) {
        FcRtClass* rc = (FcRtClass*)fcDictGet(program->program->classes, spec->classes->items[i]);
        FcList* names = FG_NAMES(rc);
        FcList* fields;
        FcList* methods;
        FcList* arities;
        fcBufFmt(&sb, "class %s : %s [", rc->name, rc->base ? rc->base->name : "");
        for (k = 0; k < rc->interfaces->count; k++) fcBufFmt(&sb, "%s%s", k ? "," : "", (const char*)rc->interfaces->items[k]);
        fcBufStr(&sb, "] <");
        if (rc->baseTypeArgs) for (k = 0; k < rc->baseTypeArgs->count; k++) fcBufFmt(&sb, "%s%s", k ? "," : "", (const char*)rc->baseTypeArgs->items[k]);
        fcBufFmt(&sb, ">%s%s%s%s\n", rc->isActor ? " actor" : "", rc->isRecord ? " record" : "", rc->notSerializable ? " ns" : "", rc->noCopy ? " nc" : "");
        fcBufStr(&sb, " layout ");
        for (k = 0; k < names->count; k++) fcBufFmt(&sb, "%s%s", k ? "," : "", (const char*)names->items[k]);
        fcBufChar(&sb, '\n');
        fields = fgSortedKeys(g->c, rc->ownFieldInfo, true);
        for (k = 0; k < fields->count; k++) {
            const FcFieldInfo* info = (const FcFieldInfo*)fcDictGet(rc->ownFieldInfo, fields->items[k]);
            fcBufFmt(&sb, " field %s %s %s %d %s %s %s", (const char*)fields->items[k], info->isStatic ? "True" : "False", fgAccessName(info->access), info->requiredKind,
                info->requiredUnit ? info->requiredUnit : "", info->requiredType ? info->requiredType : "", info->typeText ? info->typeText : "");
            if (info->requiredArray != 0) fcBufFmt(&sb, " a%d", info->requiredArray);
            if (info->requiredArrayType) fcBufFmt(&sb, " e%s", info->requiredArrayType);
            fcBufChar(&sb, '\n');
        }
        methods = fgSortedKeys(g->c, rc->methods, true);
        for (k = 0; k < methods->count; k++) {
            FcList* protos = (FcList*)fcDictGet(rc->methods, methods->items[k]);
            int j;
            for (j = 0; j < protos->count; j++) {
                const FcProto* p = (const FcProto*)protos->items[j];
                int src = fgFirstSourceIndex(p->chunk);
                if (src < 0 || fgISetHas(g->c, spec->sources, src)) fgAbiProto(g, &sb, fcFmt(g->c, "method %s", (const char*)methods->items[k]), p);
            }
        }
        arities = fcListNew(g->c);
        for (k = 0; k < rc->ctorArities->count; k++) fcListAdd(g->c, arities, FC_IVAL(k));
        for (k = 1; k < arities->count; k++) {
            int j;
            for (j = k; j > 0 && FC_VALI(rc->ctorArities->items[FC_VALI(arities->items[j - 1])]) > FC_VALI(rc->ctorArities->items[FC_VALI(arities->items[j])]); j--) {
                void* t = arities->items[j]; arities->items[j] = arities->items[j - 1]; arities->items[j - 1] = t;
            }
        }
        for (k = 0; k < arities->count; k++) {
            int at = FC_VALI(arities->items[k]);
            fgAbiProto(g, &sb, fcFmt(g->c, "ctor%d", FC_VALI(rc->ctorArities->items[at])), (const FcProto*)rc->ctorProtos->items[at]);
        }
        for (k = 0; k < rc->extraConstructors->count; k++) fgAbiProto(g, &sb, "xctor", (const FcProto*)rc->extraConstructors->items[k]);
        if (rc->destructor) fcBufStr(&sb, " dtor\n");
    }
    return fcSha256OfText(g->c, fcBufDone(g->c, &sb));
}

/* the installed import of a module (a key `pkg:...`, or the name of an import with natives) */
static FgInstImport* fgModuleImport(FgGen* g, const char* module) {
    int i;
    if (!g->store->loaded) fcStoreLoad(g->c, g->store);
    if (fcStartsWith(module, "pkg:")) {
        FcPkgImport* pi = fcStoreFindKey(g->c, g->store, module);
        return pi ? fgInstalledImport(g->c, g->instImports, pi) : NULL;
    }
    for (i = 0; i < g->store->imports->count; i++) {
        FcPkgImport* pi = (FcPkgImport*)g->store->imports->items[i];
        FgInstImport* x;
        if (!fcAsciiIEq(pi->name, module)) continue;
        x = fgInstalledImport(g->c, g->instImports, pi);
        if (x->native) return x;
    }
    return NULL;
}
/* The hash of the natives in C of an import (empty: none). */
static const char* NativeSourcesHash(FgGen* g, const char* module) {
    FgInstImport* imp = fgModuleImport(g, module);
    FcList* texts;
    FcBuf b = {0};
    int i;
    if (!imp || !imp->native) return "";
    texts = fgReadNativeSources(g->c, g->instImports, g->store, imp, fgPlatformKeys(g));
    fcBufStr(&b, "");
    for (i = 0; i < texts->count; i++) { FgNamedText* t = (FgNamedText*)texts->items[i]; fcBufFmt(&b, "%s\n%s\n", t->name, t->text); }
    return fcSha256OfText(g->c, fcBufDone(g->c, &b));
}

/* JsonSerializer.Serialize of a text (the default encoder) */
static void fgJsonStr(FcBuf* b, const char* fc8) {
    const unsigned char* p = (const unsigned char*)fc8;
    if (!fc8) { fcBufStr(b, "null"); return; }
    fcBufChar(b, '"');
    while (*p) {
        uint16_t u = fc8Next(&p);
        if (u == '\\') fcBufStr(b, "\\\\");
        else if (u == '\n') fcBufStr(b, "\\n");
        else if (u == '\r') fcBufStr(b, "\\r");
        else if (u == '\t') fcBufStr(b, "\\t");
        else if (u == '\b') fcBufStr(b, "\\b");
        else if (u == '\f') fcBufStr(b, "\\f");
        else if (u >= 0x20 && u < 0x7F && u != '"' && u != '<' && u != '>' && u != '&' && u != '\'' && u != '+' && u != '`') fcBufChar(b, (char)u);
        else fcBufFmt(b, "\\u%04X", u);
    }
    fcBufChar(b, '"');
}
static void fgJsonStrList(FcBuf* b, const FcList* l) {
    int i;
    fcBufChar(b, '[');
    for (i = 0; l && i < l->count; i++) { if (i) fcBufChar(b, ','); fgJsonStr(b, (const char*)l->items[i]); }
    fcBufChar(b, ']');
}
static void fgJsonStrArr(FcBuf* b, int n, const char* const* items) {
    int i;
    fcBufChar(b, '[');
    for (i = 0; i < n; i++) { if (i) fcBufChar(b, ','); fgJsonStr(b, items[i]); }
    fcBufChar(b, ']');
}

/* the target as JsonSerializer.Serialize(target.Native) writes it */
static void fgJsonCArr(FcBuf* b, const char* const* items, int n) { int i; fcBufChar(b, '['); for (i = 0; i < n; i++) { if (i) fcBufChar(b, ','); fgJsonStr(b, items[i]); } fcBufChar(b, ']'); }
static const char* fgNativeTargetJson(FcCtx* c, const FgTarget* t) {
    FcBuf b = {0};
    fcBufStr(&b, "{\"Platform\":"); fgJsonStr(&b, t->platform);
    fcBufStr(&b, ",\"Includes\":"); fgJsonCArr(&b, t->includes, t->includeCount);
    fcBufStr(&b, ",\"PlatformPath\":"); fgJsonStr(&b, t->platformPath);
    fcBufStr(&b, ",\"Defines\":"); fgJsonCArr(&b, t->defines, t->defineCount);
    fcBufStr(&b, ",\"Entry\":{\"Name\":"); fgJsonStr(&b, t->entryName ? t->entryName : "main");
    fcBufFmt(&b, ",\"Kind\":%d,\"ExternC\":%s}", t->entryKind, t->entryExternC ? "true" : "false");
    fcBufStr(&b, ",\"CompileArgs\":"); fgJsonCArr(&b, t->compileArgs, t->compileArgCount);
    fcBufStr(&b, ",\"LinkLibs\":"); fgJsonCArr(&b, t->linkLibs, t->linkLibCount);
    fcBufFmt(&b, ",\"SupportsThreads\":%s,\"LineDirectives\":%s,\"PointerCalls\":%s,\"Toolchain\":", t->supportsThreads ? "true" : "false", t->lineDirectives ? "true" : "false", t->pointerCalls ? "true" : "false");
    fgJsonStr(&b, t->toolchain);
    fcBufStr(&b, ",\"Port\":null,\"Baud\":null}");
    return fcBufDone(c, &b);
}

static const char* const fgModeNames[] = { "Debug", "Release", "Performance" };
/* The key of a module's library in the cache: the generator, the toolchain, the target and the mode, the module's sources, the interfaces of the modules it needs
 * and the switches it is built with (a variant per set; hasFlags false - the module's own switches, only its interface is kept). abi: FgModuleSpec* -> hash. */
static const char* LibraryKey(FgGen* g, const FcLinked* program, const FgModuleSpec* spec, FcDict* abi, const FgTarget* target, const char* toolchainStamp, bool hasFlags, uint32_t flags, int stripping) {
    FcBuf sb = {0};
    int n, i, *srcs;
    fcBufFmt(&sb, "fire-static %d %s\n", FG_INTERFACE_FORMAT, fgGeneratorIdentity);
    fcBufFmt(&sb, "toolchain %s\n", toolchainStamp);
    fcBufFmt(&sb, "target %s %s %d\n", target->name, fgNativeTargetJson(g->c, target), target->floatWidth);
    fcBufFmt(&sb, "mode %s float %d int %d word %d\n", fgModeNames[program->mode], program->floatWidth, program->intWidth, program->wordWidth);
    fcBufFmt(&sb, "module %s\n", spec->name);
    srcs = fgISetItems(g->c, spec->sources, true, &n);
    for (i = 0; i < n; i++) fcBufFmt(&sb, " source %s\n", program->sourceTextHashes && srcs[i] < program->sourceTextHashes->count ? (const char*)program->sourceTextHashes->items[srcs[i]] : "");
    for (i = 0; i < spec->deps->count; i++) { const FgModuleSpec* dep = (const FgModuleSpec*)spec->deps->items[i]; fcBufFmt(&sb, " needs %s %s\n", dep->name, (const char*)fcDictGet(abi, dep)); }
    /* (the natives in C of its import and of those it needs go into its file: a changed native is a new library) */
    for (i = 0; i <= spec->deps->count; i++) {
        const FgModuleSpec* m = i < spec->deps->count ? (const FgModuleSpec*)spec->deps->items[i] : spec;
        fcBufFmt(&sb, " natives %s %s\n", m->name, NativeSourcesHash(g, m->name));
    }
    /* (whether its natives in C live in its file depends on the other imports of the program: a variant of its own) */
    fcBufFmt(&sb, "natives-here %s\n", NativesStandAlone(g, spec->name) ? "True" : "False");
    fcBufFmt(&sb, "flags %s\n", hasFlags ? fgFlagsText(g->c, flags) : "own");
    /* (a library keeps everything with stripping 0, else its API: two variants - see LibraryStripping) */
    if (stripping == 0) fcBufStr(&sb, "stripping 0\n");
    return fcStrN(g->c, fcSha256OfText(g->c, fcBufDone(g->c, &sb)), 40);
}

/* ---- the interface file ---- */
static void fgJsonNameArgcList(FgGen* g, FcBuf* b, FcList* l) {
    int i;
    fcBufChar(b, '[');
    for (i = 0; i < l->count; i++) {
        FgNameArgc* d = (FgNameArgc*)l->items[i];
        if (i) fcBufChar(b, ',');
        fcBufChar(b, '['); fgJsonStr(b, d->name); fcBufFmt(b, ",\"%d\"]", d->argc);
    }
    fcBufChar(b, ']');
    (void)g;
}
static int fgCmpNameArgcOrdinal(const void* a, const void* b) {
    const FgNameArgc* x = *(const FgNameArgc* const*)a;
    const FgNameArgc* y = *(const FgNameArgc* const*)b;
    int c = strcmp(x->name, y->name);
    return c ? c : (x->argc < y->argc ? -1 : x->argc > y->argc);
}
/* ProgramFile.WriteUnit: the unit's body, then the table of its strings before it */
static const char* fgUnitBase64(FcCtx* c, const FcU* u) {
    FcPfWriter w;
    FcBuf body = {0};
    FcBuf all = {0};
    FcBuf b64 = {0};
    static const char* const digits = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    size_t i;
    int k;
    memset(&w, 0, sizeof w);
    w.c = c;
    w.strings = fcDictNew(c, FC_KEY_STR); w.stringList = fcListNew(c);
    w.out = &body;
    fcBufStr(&body, "");
    pfDims(&w, &u->u);
    pfF64(&w, u->u.scale); pfStr(&w, u->display); pfF64(&w, u->u.offset); pfStr(&w, u->u.converter); pfStr(&w, u->u.unitlessConverter);
    w.out = &all;
    fcBufStr(&all, "");
    pfCount(&w, w.stringList->count);
    for (k = 0; k < w.stringList->count; k++) {
        FcBuf utf8 = {0};
        fcOut8(&utf8, (const char*)w.stringList->items[k]);
        pfCount(&w, (int)utf8.len);
        if (utf8.len) fcBufPut(&all, utf8.p, utf8.len);
        fcBufFree(&utf8);
    }
    if (body.len) fcBufPut(&all, body.p, body.len);
    fcBufFree(&body);
    fcBufStr(&b64, "");
    for (i = 0; i < all.len; i += 3) {
        uint32_t v = (uint32_t)(uint8_t)all.p[i] << 16;
        size_t n = all.len - i;
        if (n > 1) v |= (uint32_t)(uint8_t)all.p[i + 1] << 8;
        if (n > 2) v |= (uint8_t)all.p[i + 2];
        fcBufChar(&b64, digits[(v >> 18) & 63]);
        fcBufChar(&b64, digits[(v >> 12) & 63]);
        fcBufChar(&b64, n > 1 ? digits[(v >> 6) & 63] : '=');
        fcBufChar(&b64, n > 2 ? digits[v & 63] : '=');
    }
    fcBufFree(&all);
    return fcBufDone(c, &b64);
}
/* InterfaceOf + JsonSerializer.Serialize: the interface file of a module as generated */
static const char* InterfaceJson(FgGen* g, const FgModuleOutput* output, const char* key, const char* abi, FcList* exports) {
    FgModuleNeeds* n = output->needs;
    FcBuf b = {0};
    FcList* l;
    int i, k;
    fcBufFmt(&b, "{\"Format\":%d,\"Module\":", FG_INTERFACE_FORMAT); fgJsonStr(&b, output->spec->name);
    fcBufStr(&b, ",\"Key\":"); fgJsonStr(&b, key);
    fcBufStr(&b, ",\"Abi\":"); fgJsonStr(&b, abi);
    fcBufStr(&b, ",\"Flags\":"); fgJsonStr(&b, fgFlagsText(g->c, output->flags));
    fcBufStr(&b, ",\"Classes\":"); fgJsonStrList(&b, output->spec->classes);
    l = fcListCopy(g->c, exports);
    if (l->count > 1) qsort(l->items, (size_t)l->count, sizeof(void*), fgCmpOrdinal);
    fcBufStr(&b, ",\"Exports\":"); fgJsonStrList(&b, l);
    fcBufStr(&b, ",\"Dispatchers\":"); fgJsonNameArgcList(g, &b, fgSortedNameArgc(g, n->dispatchers));
    fcBufStr(&b, ",\"Fields\":"); fgJsonStrList(&b, fgSortedKeys(g->c, n->fields, true));
    fcBufStr(&b, ",\"FieldAddresses\":"); fgJsonStrList(&b, fgSortedKeys(g->c, n->fieldAddresses, true));
    fcBufStr(&b, ",\"IsOf\":"); fgJsonStrList(&b, fgSortedKeys(g->c, n->isOf, true));
    l = fcListNew(g->c);
    FC_DICT_EACH(n->targOfs, e) fcListAdd(g->c, l, e->value);
    if (l->count > 1) qsort(l->items, (size_t)l->count, sizeof(void*), fgCmpNameArgc);
    fcBufStr(&b, ",\"TArgOfs\":"); fgJsonNameArgcList(g, &b, l);
    fcBufStr(&b, ",\"Operators\":"); fgJsonStrList(&b, fgSortedKeys(g->c, n->operators, true));
    fcBufStr(&b, ",\"OverloadClasses\":"); fgJsonStrList(&b, fgSortedKeys(g->c, n->overloadClasses, true));
    fcBufStr(&b, ",\"ErrorClasses\":"); fgJsonStrList(&b, fgSortedKeys(g->c, n->errorClasses, true));
    l = fcListNew(g->c);
    FC_DICT_EACH(n->refCallSites, e) fcListAdd(g->c, l, e->value);
    if (l->count > 1) qsort(l->items, (size_t)l->count, sizeof(void*), fgCmpNameArgcOrdinal);
    fcBufStr(&b, ",\"RefCallSites\":"); fgJsonNameArgcList(g, &b, l);
    l = fgSortedStaticFieldKeys(g, n->staticFields);
    fcBufStr(&b, ",\"StaticFields\":[");
    for (i = 0; i < l->count; i++) {
        FcList* parts = fgSplit(g->c, (const char*)l->items[i], '\x01');
        if (i) fcBufChar(&b, ',');
        fcBufChar(&b, '['); fgJsonStr(&b, (const char*)parts->items[0]); fcBufChar(&b, ','); fgJsonStr(&b, (const char*)parts->items[1]); fcBufChar(&b, ']');
    }
    fcBufStr(&b, "],\"Units\":[");
    for (i = 0; i < n->units->count; i++) { if (i) fcBufChar(&b, ','); fgJsonStr(&b, fgUnitBase64(g->c, (const FcU*)n->units->items[i])); }
    fcBufStr(&b, "],\"Enums\":[");
    for (i = 0; i < n->enums->count; i++) {
        const FcEnumType* e = (const FcEnumType*)n->enums->items[i];
        if (i) fcBufChar(&b, ',');
        fcBufStr(&b, "{\"Name\":"); fgJsonStr(&b, e->name);
        fcBufFmt(&b, ",\"Flags\":%s,\"Members\":", e->isFlags ? "true" : "false");
        fgJsonStrList(&b, e->names);
        fcBufStr(&b, ",\"Values\":[");
        for (k = 0; k < e->values->count; k++) fcBufFmt(&b, "%s%" PRId64, k ? "," : "", *(int64_t*)e->values->items[k]);
        fcBufStr(&b, "]}");
    }
    fcBufStr(&b, "],\"ExcTypes\":"); fgJsonStrList(&b, n->excTypes);
    fcBufStr(&b, ",\"ReflectionNatives\":"); fgJsonStrList(&b, fgSortedKeys(g->c, n->reflectionNatives, true));
    fcBufStr(&b, ",\"Made\":"); fgJsonStrList(&b, fgSortedKeys(g->c, n->classes, true));
    fcBufStr(&b, ",\"MadeBy\":");
    if (!n->madeBy) fcBufStr(&b, "null");
    else {
        bool first = true;
        fcBufChar(&b, '{');
        FC_DICT_EACH(n->madeBy, e) { if (!first) fcBufChar(&b, ','); first = false; fgJsonStr(&b, (const char*)e->key); fcBufChar(&b, ':'); fgJsonStrList(&b, (FcList*)e->value); }
        fcBufChar(&b, '}');
    }
    fcBufStr(&b, ",\"SharedNatives\":[");
    l = fgSortedKeys(g->c, n->sharedNatives, true);
    for (i = 0; i < l->count; i++) {
        FgSharedNative* sn = (FgSharedNative*)fcDictGet(n->sharedNatives, l->items[i]);
        if (i) fcBufChar(&b, ',');
        fcBufChar(&b, '['); fgJsonStr(&b, (const char*)l->items[i]); fcBufFmt(&b, ",\"%d\",\"%s\",", sn->argc, sn->needsList ? "1" : "0"); fgJsonStr(&b, sn->import); fcBufChar(&b, ']');
    }
    fcBufStr(&b, "],\"NativeThunks\":"); fgJsonStr(&b, output->nativeThunks);
    fcBufFmt(&b, ",\"JsonEngine\":%s,\"HostImports\":[", output->jsonEngine ? "true" : "false");
    l = fgSortedKeys(g->c, output->hostImports, true);
    for (i = 0; i < l->count; i++) {
        if (i) fcBufChar(&b, ',');
        fcBufChar(&b, '['); fgJsonStr(&b, (const char*)l->items[i]); fcBufChar(&b, ','); fgJsonStr(&b, (const char*)fcDictGet(output->hostImports, l->items[i])); fcBufChar(&b, ']');
    }
    fcBufStr(&b, "]}");
    return fcBufDone(g->c, &b);
}

/* ---- files ---- */
static bool fgMkdirs(FcCtx* c, const char* dir) {
    char* p = (char*)fcStr(c, dir);
    size_t i;
    for (i = 1; p[i]; i++)
        if (p[i] == '/') {
            p[i] = 0;
#ifdef _WIN32
            _mkdir(p);
#else
            mkdir(p, 0777);
#endif
            p[i] = '/';
        }
#ifdef _WIN32
    _mkdir(p);
#else
    mkdir(p, 0777);
#endif
    return true;
}
static const char* fgDirName(FcCtx* c, const char* path) { const char* s = strrchr(path, '/'); return s ? fcStrN(c, path, (size_t)(s - path)) : "."; }
static void fgWriteAtomically(FcCtx* c, const char* path, const char* fc8) {
    FcBuf u = {0};
    char rnd[17];
    const char* temp;
    FILE* f;
    static unsigned long long counter;
    unsigned long long r = ((unsigned long long)rand() << 32) ^ (unsigned long long)rand() ^ (++counter * 0x9E3779B97F4A7C15ULL) ^ (unsigned long long)(uintptr_t)&u;
    snprintf(rnd, sizeof rnd, "%016llx", r);
    fgMkdirs(c, fgDirName(c, path));
    temp = fcFmt(c, "%s.%s.tmp", path, rnd);
    fcOut8(&u, fc8);
    if (!(f = fopen(temp, "wb"))) { fcBufFree(&u); fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "cannot write '%s'", temp)); }
    if (u.len) fwrite(u.p, 1, u.len, f);
    fclose(f);
    fcBufFree(&u);
    if (rename(temp, path) != 0) { remove(temp); fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "cannot write '%s'", path)); }
}

/* ---- reading an interface (ReadInterface, OutputOf) ---- */
static FcList* fgJsonStrings2(FcCtx* c, const FcJson* v) {
    FcList* l = fcListNew(c);
    int i;
    if (v && v->kind == FJ_ARR) for (i = 0; i < v->items->count; i++) { const FcJson* x = (const FcJson*)v->items->items[i]; fcListAdd(c, l, (void*)(x->kind == FJ_STR ? x->s : "")); }
    return l;
}
/* EnumType.Intern: the enum of the program with the same key (the generator compares enums by their identity), else a new one */
static const FcEnumType* fgEnumIntern(FcCtx* c, const FcLinked* program, const char* name, bool flags, FcList* members, FcList* values) {
    const char* key = fcEnumKeyOf(c, name, flags, members, values);
    FcEnumType* t;
    int i;
    for (i = 0; i < program->program->enums->count; i++) if (streq0(((const FcEnumType*)program->program->enums->items[i])->key, key)) return (const FcEnumType*)program->program->enums->items[i];
    t = (FcEnumType*)fcAlloc(c, sizeof *t);
    t->name = name; t->isFlags = flags; t->names = members; t->values = values; t->key = key;
    return t;
}
/* ProgramFile.ReadUnit of Convert.FromBase64String(text) */
typedef struct FgUnitReader { const uint8_t* p; size_t n, i; FcList* strings; } FgUnitReader;
static uint64_t fgUrUInt(FgUnitReader* r) { uint64_t v = 0; int shift = 0; while (r->i < r->n) { uint8_t b = r->p[r->i++]; v |= (uint64_t)(b & 0x7F) << shift; if (!(b & 0x80)) break; shift += 7; } return v; }
static int64_t fgUrInt(FgUnitReader* r) { uint64_t v = fgUrUInt(r); return (int64_t)(v >> 1) ^ -(int64_t)(v & 1); }
static double fgUrF64(FgUnitReader* r) { uint64_t bits = 0; double d; int i; for (i = 0; i < 8 && r->i < r->n; i++) bits |= (uint64_t)r->p[r->i++] << (8 * i); memcpy(&d, &bits, 8); return d; }
static const char* fgUrStr(FgUnitReader* r) { uint64_t k = fgUrUInt(r); return k == 0 || k > (uint64_t)r->strings->count ? NULL : (const char*)r->strings->items[k - 1]; }
static const FcU* fgUnitFromBase64(FcCtx* c, const char* text) {
    FcBuf bytes = {0};
    FgUnitReader r;
    FcUnit u;
    const char* display;
    uint32_t acc = 0;
    int bits = 0, i, count;
    const char* p;
    for (p = text; *p; p++) {
        const char* at = strchr("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/", *p);
        if (*p == '=' || !at) continue;
        acc = (acc << 6) | (uint32_t)(at - "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/");
        bits += 6;
        if (bits >= 8) { bits -= 8; fcBufChar(&bytes, (char)((acc >> bits) & 0xFF)); }
    }
    memset(&r, 0, sizeof r);
    r.p = (const uint8_t*)bytes.p; r.n = bytes.len; r.strings = fcListNew(c);
    count = (int)fgUrUInt(&r);
    for (i = 0; i < count; i++) {
        size_t len = (size_t)fgUrUInt(&r);
        FcU16 u16 = {0};
        if (r.i + len > r.n) len = r.n - r.i;
        fcDecodeText(r.p + r.i, len, &u16);
        fcListAdd(c, r.strings, (void*)fc8FromU16(c, u16.p, u16.len));
        fcU16Free(&u16);
        r.i += len;
    }
    memset(&u, 0, sizeof u);
    count = (int)fgUrUInt(&r);
    for (i = 0; i < count; i++) { const char* sym = fgUrStr(&r); int exp = (int)fgUrInt(&r); if (sym && u.dimCount < FC_MAX_DIMS) { u.dimSym[u.dimCount] = sym; u.dimExp[u.dimCount] = exp; u.dimCount++; } }
    u.scale = fgUrF64(&r);
    display = fgUrStr(&r);
    u.offset = fgUrF64(&r);
    u.converter = fgUrStr(&r);
    u.unitlessConverter = fgUrStr(&r);
    fcBufFree(&bytes);
    return fcUNew(c, &u, display);
}
/* the interface of a module in the cache with this key (ReadInterface): NULL when there is none, it is not of this format or key, or it cannot be read */
static FcJson* fgReadInterfaceJson(FcCtx* c, const char* path, const char* key) {
    FILE* f = fopen(path, "rb");
    FcBuf text = {0};
    char chunk[65536];
    size_t n;
    FcJsonReader j;
    FcJson* root;
    const FcJson* v;
    if (!f) return NULL;
    while ((n = fread(chunk, 1, sizeof chunk, f)) > 0) fcBufPut(&text, chunk, n);
    fclose(f);
    fcBufChar(&text, 0);
    j.c = c; j.p = text.p; j.bad = false;
    root = fcJsonValue(&j);
    fcBufFree(&text);
    if (j.bad || !root || root->kind != FJ_OBJ) return NULL;
    if (!(v = fcJsonGet(root, "Format")) || v->kind != FJ_NUM || v->n != FG_INTERFACE_FORMAT) return NULL;
    if (!(v = fcJsonGet(root, "Key")) || v->kind != FJ_STR || !streq0(v->s, key)) return NULL;
    return root;
}
static FgModuleOutput* OutputOf(FcCtx* c, const FcLinked* program, const FcJson* f, FgModuleSpec* spec) {
    FgModuleNeeds* n = fgNeedsNew(c);
    FgModuleOutput* o = (FgModuleOutput*)fcAlloc(c, sizeof *o);
    const FcJson* v;
    const char* flags;
    int i, k;
#define FG_PAIRS(name, body) if ((v = fcJsonGet(f, name)) && v->kind == FJ_ARR) for (i = 0; i < v->items->count; i++) { FcList* x_ = fgJsonStrings2(c, (const FcJson*)v->items->items[i]); body; }
#define FG_SET(name, dict) do { FcList* l_ = fgJsonStrings2(c, fcJsonGet(f, name)); for (k = 0; k < l_->count; k++) fcSetAdd(dict, l_->items[k]); } while (0)
#define FG_NA(dict) do { FgNameArgc* na = (FgNameArgc*)fcAlloc(c, sizeof *na); na->name = (const char*)x_->items[0]; na->argc = atoi((const char*)x_->items[1]); fcDictPut(dict, fcFmt(c, "%s\x01%d", na->name, na->argc), na); } while (0)
    FG_PAIRS("Dispatchers", FG_NA(n->dispatchers))
    FG_SET("Fields", n->fields);
    FG_SET("FieldAddresses", n->fieldAddresses);
    FG_SET("IsOf", n->isOf);
    FG_PAIRS("TArgOfs", FG_NA(n->targOfs))
    FG_SET("Operators", n->operators);
    FG_SET("OverloadClasses", n->overloadClasses);
    FG_SET("ErrorClasses", n->errorClasses);
    FG_PAIRS("RefCallSites", FG_NA(n->refCallSites))
    FG_PAIRS("StaticFields", fcSetAdd(n->staticFields, fcFmt(c, "%s\x01%s", (const char*)x_->items[0], (const char*)x_->items[1])))
    { FcList* l = fgJsonStrings2(c, fcJsonGet(f, "Units")); for (k = 0; k < l->count; k++) fcListAdd(c, n->units, (void*)fgUnitFromBase64(c, (const char*)l->items[k])); }
    if ((v = fcJsonGet(f, "Enums")) && v->kind == FJ_ARR)
        for (i = 0; i < v->items->count; i++) {
            const FcJson* e = (const FcJson*)v->items->items[i];
            const FcJson* vals = fcJsonGet(e, "Values");
            FcList* values = fcListNew(c);
            if (vals && vals->kind == FJ_ARR) for (k = 0; k < vals->items->count; k++) { int64_t* x = (int64_t*)fcAlloc(c, sizeof *x); *x = strtoll(((const FcJson*)vals->items->items[k])->s, NULL, 10); fcListAdd(c, values, x); }
            fcListAdd(c, n->enums, (void*)fgEnumIntern(c, program, fcJsonStr(e, "Name") ? fcJsonStr(e, "Name") : "", fcJsonGet(e, "Flags") && fcJsonGet(e, "Flags")->kind == FJ_BOOL && fcJsonGet(e, "Flags")->b,
                fgJsonStrings2(c, fcJsonGet(e, "Members")), values));
        }
    { FcList* l = fgJsonStrings2(c, fcJsonGet(f, "ExcTypes")); for (k = 0; k < l->count; k++) fcListAdd(c, n->excTypes, l->items[k]); }
    FG_SET("ReflectionNatives", n->reflectionNatives);
    FG_SET("Made", n->classes);
    if ((v = fcJsonGet(f, "MadeBy")) && v->kind == FJ_OBJ) {
        n->madeBy = fcDictNew(c, FC_KEY_STR);
        for (i = 0; i < v->keys->count; i++) fcDictPut(n->madeBy, v->keys->items[i], fgJsonStrings2(c, (const FcJson*)v->items->items[i]));
    }
    FG_PAIRS("SharedNatives", {
        FgSharedNative* sn = (FgSharedNative*)fcAlloc(c, sizeof *sn);
        sn->argc = atoi((const char*)x_->items[1]); sn->needsList = streq0((const char*)x_->items[2], "1"); sn->import = x_->count > 3 ? (const char*)x_->items[3] : "";
        fcDictPut(n->sharedNatives, x_->items[0], sn); })
    o->hostImports = fcDictNew(c, FC_KEY_STR);
    FG_PAIRS("HostImports", fcDictPut(o->hostImports, x_->items[0], x_->items[1]))
#undef FG_PAIRS
#undef FG_SET
#undef FG_NA
    o->spec = spec;
    o->body = "";
    o->nativeThunks = fcJsonStr(f, "NativeThunks") ? fcJsonStr(f, "NativeThunks") : "";
    o->needs = n;
    flags = fcJsonStr(f, "Flags") ? fcJsonStr(f, "Flags") : "";
    o->flags = 0;
    for (i = 0; i < MF_Count && flags[i]; i++) if (flags[i] == '1') o->flags |= 1u << i;
    v = fcJsonGet(f, "JsonEngine");
    o->jsonEngine = v && v->kind == FJ_BOOL && v->b;
    o->cppNatives = fcListNew(c);
    o->exports = fcListNew(c);
    o->nativesC = "";
    o->linkC = "";
    return o;
}

/* ---- GenerateStatic ---- */
static const char* RuntimeDllC(FgGen* g, const char* head, const char* abi);
static FgPluginHostInfo* HostInfo(FgGen* g, FcDict* libraryFiles, const char* runtimeFile);
/* Linkage "static" (ROADMAP 9): the program's C and, per library module, its static library in the cache - looked up by its key, or its C file written next to
 * where the library goes (the caller compiles it and puts the library there). shared: linkage "shared" (the libraries are shared libraries). cacheRoot: the
 * root of the cache (NativeLibraryCache.Root). */
static FgStaticPlan* GenerateStatic(FcCtx* c, FcLinked* program, FcList* natives, const FgTarget* target, const char* toolchainStamp, int stripping, const char* cacheRoot,
    bool shared, bool stats, FcStore* store) {
    bool dll = shared && streq0(target->platform, "windows");
    FcList* specs;
    FcDict* abi = fcDictNew(c, FC_KEY_PTR);
    const char* root;
    FcBuf log = {0};
    bool hasFlags = false;
    uint32_t flags = 0;
    FgGen* probe;
    int round, i;
    if (shared) toolchainStamp = fcFmt(c, "%s%s", toolchainStamp, dll ? "|shared|dll" : "|shared");
    specs = ModulesOf(c, program);
    probe = fgGenNew(c, program, natives, target);   /* (a generator for the questions of the static functions: the store, the classes) */
    probe->store = store;
    CollectPackages(probe, store);
    for (i = 0; i < specs->count; i++) fcDictPut(abi, specs->items[i], (void*)AbiHash(probe, program, (FgModuleSpec*)specs->items[i]));
    root = fcFmt(c, "%s/%s", cacheRoot, target->name);
    fcBufStr(&log, "");
    for (round = 0;; round++) {
        FcList* outputs = fcListNew(c);
        FcDict* fresh = fcDictNew(c, FC_KEY_PTR);
        FcDict* keys = fcDictNew(c, FC_KEY_PTR);
        uint32_t u = hasFlags ? flags : 0;
        FgGen* main;
        const char* text;
        for (i = 0; i < specs->count; i++) {
            FgModuleSpec* spec = (FgModuleSpec*)specs->items[i];
            const char* key = LibraryKey(probe, program, spec, abi, target, toolchainStamp, hasFlags, flags, fgLibraryStripping(program, stripping));
            const char* iface = fcFmt(c, "%s/%s/%s%s", root, key, spec->id, FG_INTERFACE_EXTENSION);
            FcJson* known;
            FgModuleOutput* output;
            fcDictPut(keys, spec, (void*)key);
            if ((known = fgReadInterfaceJson(c, iface, key)) != NULL) {
                /* (the program declares a class of the module anew - `class Exception` of its own: the module's classes are not those its library was built with) */
                FcList* classes = fgJsonStrings2(c, fcJsonGet(known, "Classes"));
                bool same = classes->count == spec->classes->count;
                int k;
                for (k = 0; same && k < classes->count; k++) if (!streq0((const char*)classes->items[k], (const char*)spec->classes->items[k])) same = false;
                if (!same) fcThrow(c, FC_ERR_NATIVE, 0, 0, NULL, fcFmt(c, "module '%s': the program declares classes of the module anew", spec->name));
                output = OutputOf(c, program, known, spec);
            } else {
                output = GenerateLibrary(c, program, natives, spec, specs, target, stripping, hasFlags, flags, true, dll, NULL, store);
                fcDictPut(fresh, spec, output);
                if (!hasFlags)   /* (the module's own switches: only its interface is kept) */
                    fgWriteAtomically(c, iface, InterfaceJson(probe, output, key, (const char*)fcDictGet(abi, spec), output->exports));
            }
            fcListAdd(c, outputs, output);
            u |= output->flags;
        }
        main = fgGenNew(c, program, natives, target);
        main->strip = fgEffectiveStripping(program, stripping);
        main->role = FG_ROLE_PROGRAM; main->hasForced = hasFlags; main->forced = flags; main->libraries = outputs; main->separate = true; main->collectStats = stats;
        if (shared) {
            main->sharedAbis = fcListNew(c);
            for (i = 0; i < specs->count; i++) {
                FgModuleDep* d = (FgModuleDep*)fcAlloc(c, sizeof *d);
                d->spec = (FgModuleSpec*)specs->items[i]; d->module = d->spec->name; d->abi = fcFmt(c, "%d:%s", FG_INTERFACE_FORMAT, (const char*)fcDictGet(abi, d->spec));
                fcListAdd(c, main->sharedAbis, d);
            }
        }
        main->dll = dll; main->dllModules = dll ? outputs : NULL; main->toolchainStamp = toolchainStamp;
        main->openWorld = shared;
        main->store = store;
        SetModules(main, specs, NULL);
        text = Run(main);
        u |= CurrentFlags(main);
        if (hasFlags && flags == u) {
            FgStaticPlan* plan = (FgStaticPlan*)fcAlloc(c, sizeof *plan);
            FcList* libraries = fcListNew(c);
            const char* rtKey = dll ? main->runtimeKey : NULL;
            FcDict* files = fcDictNew(c, FC_KEY_PTR);
            if (rtKey) {
                /* the runtime's DLL: built once for its head (the switches, the target) - the program checks that the one next to it is that one */
                const char* dir = fcFmt(c, "%s/%s", root, rtKey);
                FgStaticLibrary* lib = (FgStaticLibrary*)fcAlloc(c, sizeof *lib);
                lib->module = "runtime"; lib->key = rtKey; lib->directory = dir; lib->cFile = fcFmt(c, "%s/rt.c", dir);
                lib->interfaceFile = fcFmt(c, "%s/rt%s", dir, FG_INTERFACE_EXTENSION); lib->libraryName = fcFmt(c, "libfire_rt_%.8s", rtKey);
                lib->cached = fgReadInterfaceJson(c, lib->interfaceFile, rtKey) != NULL;
                if (!lib->cached) {
                    fgWriteAtomically(c, lib->cFile, RuntimeDllC(main, main->libraryHead, fcFmt(c, "%d:%s", FG_INTERFACE_FORMAT, rtKey)));
                    {
                        FcBuf j = {0};
                        fcBufFmt(&j, "{\"Format\":%d,\"Module\":\"runtime\",\"Key\":", FG_INTERFACE_FORMAT); fgJsonStr(&j, rtKey);
                        fcBufStr(&j, ",\"Abi\":"); fgJsonStr(&j, rtKey);
                        fcBufStr(&j, ",\"Flags\":\"\",\"Classes\":[],\"Exports\":[],\"Dispatchers\":[],\"Fields\":[],\"FieldAddresses\":[],\"IsOf\":[],\"TArgOfs\":[],\"Operators\":[],\"OverloadClasses\":[],\"ErrorClasses\":[],\"RefCallSites\":[],\"StaticFields\":[],\"Units\":[],\"Enums\":[],\"ExcTypes\":[],\"ReflectionNatives\":[],\"Made\":[],\"MadeBy\":null,\"SharedNatives\":[],\"NativeThunks\":\"\",\"JsonEngine\":false,\"HostImports\":[]}");
                        fgWriteAtomically(c, fcFmt(c, "%s.new", lib->interfaceFile), fcBufDone(c, &j));
                    }
                }
                fcBufFmt(&log, "module runtime: %s (%.12s)\n", lib->cached ? "from the cache" : "new", rtKey);
                fcListAdd(c, libraries, lib);
            }
            for (i = 0; i < specs->count; i++) {
                FgModuleSpec* spec = (FgModuleSpec*)specs->items[i];
                const char* key = (const char*)fcDictGet(keys, spec);
                const char* dir = fcFmt(c, "%s/%s", root, key);
                FgStaticLibrary* lib = (FgStaticLibrary*)fcAlloc(c, sizeof *lib);
                FgModuleOutput* output = (FgModuleOutput*)fcDictGet(fresh, spec);
                /* (a DLL: named by its interface and the runtime, not its key - the DLLs that need it import it by that name, a new build of its code keeps it) */
                lib->module = spec->name; lib->key = key; lib->directory = dir;
                lib->libraryName = dll ? fcFmt(c, "libfire_%s_%.8s", spec->id, fcSha256OfText(c, fcFmt(c, "%s|%s", (const char*)fcDictGet(abi, spec), rtKey ? rtKey : "")))
                    : shared ? fcFmt(c, "libfire_%s_%.8s", spec->id, key) : fcFmt(c, "fire_%s", spec->id);
                lib->cFile = fcFmt(c, "%s/%s.c", dir, spec->id);
                lib->interfaceFile = fcFmt(c, "%s/%s%s", dir, spec->id, FG_INTERFACE_EXTENSION);
                lib->cached = output == NULL;
                if (output) {
                    const char* head = main->libraryHead;
                    const char* abiText = "";
                    if (output->jsonEngine) head = fgReplace(c, head, "#include \"fire_json.h\"", "#define FIRE_JSON_IMPLEMENTATION 1   // (the JSON engine: in the file of the module that calls it)\n#include \"fire_json.h\"");
                    /* (shared: the interface it is built with - the program checks it when it starts) */
                    if (shared) abiText = fcFmt(c, "%sconst char* const fire_abi_%s = %s;\n", dll ? "__declspec(dllexport) " : "", spec->id, CString(main, fcFmt(c, "%d:%s", FG_INTERFACE_FORMAT, (const char*)fcDictGet(abi, spec))));
                    fgWriteAtomically(c, lib->cFile, ResolveLineResets(main, fcFmt(c, "%s%s%s%s%s", head, output->nativesC, output->body, abiText, output->linkC), "fire_program.c"));
                    /* (the interface last: it says the library is there - the caller writes it after building the library, see CommitStatic) */
                    fgWriteAtomically(c, fcFmt(c, "%s.new", lib->interfaceFile), InterfaceJson(probe, output, key, (const char*)fcDictGet(abi, spec), output->exports));
                    fcBufFmt(&log, "module %s: new (%.12s)\n", spec->name, key);
                }
                else fcBufFmt(&log, "module %s: from the cache (%.12s)\n", spec->name, key);
                fcListAdd(c, libraries, lib);
            }
            for (i = 0; i < specs->count; i++) {
                FgModuleSpec* spec = (FgModuleSpec*)specs->items[i];
                int k;
                for (k = 0; k < libraries->count; k++) if (streq0(((FgStaticLibrary*)libraries->items[k])->module, spec->name)) { fcDictPut(files, spec, (void*)((FgStaticLibrary*)libraries->items[k])->libraryName); break; }
            }
            plan->programC = text;
            plan->natives = main->nativesCode;
            plan->libraries = libraries;
            plan->log = fcBufDone(c, &log);
            plan->host = HostInfo(main, files, rtKey ? ((FgStaticLibrary*)libraries->items[0])->libraryName : NULL);
            plan->funcs = stats ? FuncStats(main) : NULL;
            return plan;
        }
        if (round > 4) fcThrow(c, FC_ERR_NATIVE, 0, 0, NULL, "the modules do not agree on the switches of the program");
        flags = u; hasFlags = true;
    }
}

/* The key of the runtime's DLL (linkage "shared" on Windows): the generator, the toolchain and the head of the files (the switches, the target). */
static const char* RuntimeDllKey(FgGen* g, const char* head, const char* toolchainStamp) {
    return fcStrN(g->c, fcSha256OfText(g->c, fcFmt(g->c, "fire-rt %d %s\n%s\n%s", FG_INTERFACE_FORMAT, fgGeneratorIdentity, toolchainStamp ? toolchainStamp : "", head)), 40);
}

/* Linkage "shared": the check of the libraries at the start of the program (before anything of theirs runs). */
static const char* SharedLibraryChecks(FgGen* g) {
    FcBuf sb = {0};
    int i;
    if (!g->sharedAbis) return "";
    for (i = 0; i < g->sharedAbis->count; i++) fcBufFmt(&sb, "extern FIRE_RT_DATA const char* const fire_abi_%s;\n", ((FgModuleDep*)g->sharedAbis->items[i])->spec->id);
    if (g->dll) fcBufStr(&sb, "extern FIRE_RT_DATA const char* const fire_abi_rt;\n");
    fcBufStr(&sb, "static void fire_checkModule(const char* module, const char* found, const char* expected) {\n");
    fcBufStr(&sb, "    if (strcmp(found, expected) == 0) return;\n");
    fcBufStr(&sb, "    fprintf(stderr, \"The library of the module '%s' does not fit this program: it has the interface %.12s, the program was built for %.12s. Build the program again or put the library it was built with next to it.\\n\", module, found, expected);\n");
    fcBufStr(&sb, "    exit(70);\n");
    fcBufStr(&sb, "}\n");
    return fcBufDone(g->c, &sb);
}
static const char* HostLinkCalls(FgGen* g);
static const char* SharedLibraryCheckCalls(FgGen* g) {
    FcBuf sb = {0};
    int i;
    if (!g->sharedAbis) return "";
    fcBufStr(&sb, "");
    if (g->dll && g->runtimeKey) fcBufFmt(&sb, "    fire_checkModule(\"runtime\", fire_abi_rt, %s);\n", CString(g, fcFmt(g->c, "%d:%s", FG_INTERFACE_FORMAT, g->runtimeKey)));
    for (i = 0; i < g->sharedAbis->count; i++) {
        FgModuleDep* d = (FgModuleDep*)g->sharedAbis->items[i];
        fcBufFmt(&sb, "    fire_checkModule(%s, fire_abi_%s, %s);\n", CString(g, d->spec->name), d->spec->id, CString(g, d->abi));
    }
    fcBufStr(&sb, HostLinkCalls(g));
    return fcBufDone(g->c, &sb);
}

/* ---- linkage "shared" on Windows: DLLs ---- */
static const char* const fgHostHooks[][2] = {
    {"g_dimNames", NULL}, {"makeIndexError", NULL}, {"makeUnitError", NULL}, {"makeAccessError", NULL}, {"makeDestroyedError", NULL}, {"makeDivideError", NULL},
    {"makeClassError", "defined(FIRE_EXCEPTIONS)"}, {"makeReflectError", "defined(FIRE_REFLECTION)"},
    {"jsonClassAt", "defined(FIRE_JSON_H)"}, {"jsonClassNamed", "defined(FIRE_JSON_H)"}, {"jsonExcludedNamed", "defined(FIRE_JSON_H)"}, {"jsonSpecial", "defined(FIRE_JSON_H)"},
    {"jsonTArgFor", "defined(FIRE_JSON_H)"}, {"jsonTArgText", "defined(FIRE_JSON_H)"},
    {"fire_dbgClass", "defined(FIRE_DEBUG)"}, {"fire_dbgSetField", "defined(FIRE_DEBUG)"}, {"fire_libThunk", NULL},
};
#define FG_HOST_HOOK_COUNT ((int)(sizeof fgHostHooks / sizeof fgHostHooks[0]))

/* The head of a file in a DLL (the runtime, a module): the hooks of the program are pointers of the DLL (before fire_rt.h declares them). */
static const char* DllHead(FgGen* g) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "#define FIRE_DLL_LINKED 1   // what this DLL uses of the program: pointers, filled by fire_link_*\n");
    for (i = 0; i < FG_HOST_HOOK_COUNT; i++) fcBufFmt(&b, "#define %s (*fire_hp_%s)\n", fgHostHooks[i][0], fgHostHooks[i][0]);
    return fcBufDone(g->c, &b);
}

/* A declaration of something the program defines: in a DLL a pointer of its own instead (`(*fire_hp_name)`, filled by its fire_link_* function). */
static const char* HostDecl(FgGen* g, const char* name, const char* decl) {
    const char* own;
    if (!g->dll) return fcFmt(g->c, "%s\n", decl);
    fcDictPut(g->hostImports, name, (void*)decl);
    own = fcStartsWith(decl, "extern ") ? decl + strlen("extern ") : decl;
    return fcFmt(g->c, "#define %s (*fire_hp_%s)\n%s\n", name, name, own);
}

/* A table of numbers the program defines for a library (`fire_exct_M`, ...): in a DLL an array of its own, the link function copies the program's. */
static const char* HostArray(FgGen* g, const char* name, const char* type, int count) {
    const char* decl = fcFmt(g->c, "extern const %s %s[];", type, name);
    if (!g->dll) return fcFmt(g->c, "%s\n", decl);
    fcDictPut(g->hostImports, name, (void*)decl);
    fcSetAdd(g->hostArrays, name);
    return fcFmt(g->c, "%s %s[%d];\n", type, name, count);
}

/* The `fire_link_<m>` function of a DLL: its pointers from the program's table; it returns the name the program lacks (NULL: all there). */
static const char* LinkFunction(FgGen* g, const char* id, FcList* strict, FcDict* arrays) {
    FcBuf sb = {0};
    int i;
    fcBufStr(&sb, "/* linkage \"shared\" on Windows: the pointers of this DLL to the program's functions and data (see fire_rt.h, FIRE_DLL) */\n");
    fcBufFmt(&sb, "__declspec(dllexport) const char* fire_link_%s(const void* (*find)(const char*)) {\n", id);
    fcBufStr(&sb, "    const void* p_;\n");
    for (i = 0; i < FG_HOST_HOOK_COUNT; i++) {
        if (fgHostHooks[i][1]) fcBufFmt(&sb, "#if %s\n", fgHostHooks[i][1]);
        fcBufFmt(&sb, "    p_ = find(\"%s\"); memcpy(&fire_hp_%s, &p_, sizeof p_);\n", fgHostHooks[i][0], fgHostHooks[i][0]);
        if (fgHostHooks[i][1]) fcBufStr(&sb, "#endif\n");
    }
    for (i = 0; strict && i < strict->count; i++) {
        const char* name = (const char*)strict->items[i];
        fcBufFmt(&sb, "    if (!(p_ = find(\"%s\"))) return \"%s\";\n    ", name, name);
        if (arrays && fcDictHas(arrays, name)) fcBufFmt(&sb, "memcpy(%s, p_, sizeof %s);\n", name, name);
        else fcBufFmt(&sb, "memcpy(&fire_hp_%s, &p_, sizeof p_);\n", name);
    }
    fcBufStr(&sb, "    return NULL;\n");
    fcBufStr(&sb, "}\n");
    return fcBufDone(g->c, &sb);
}

static const char* LibraryLinkC(FgGen* g) { return LinkFunction(g, g->self->id, fgSortedKeys(g->c, g->hostImports, true), g->hostArrays); }

/* The C of the runtime's DLL: the head of the modules with the runtime's state and functions (FIRE_DLL_RUNTIME), its link function and the interface it is
 * built with (the hash of its head). */
static const char* RuntimeDllC(FgGen* g, const char* head, const char* abi) {
    return fcFmt(g->c, "%s%s__declspec(dllexport) const char* const fire_abi_rt = %s;\n",
        fgReplace(g->c, head, "#include \"fire_rt.h\"", "#define FIRE_DLL_RUNTIME 1   // this DLL is the runtime: its state and functions\n#define FIRE_RT_IMPLEMENTATION 1\n#include \"fire_rt.h\""),
        LinkFunction(g, "rt", NULL, NULL), CString(g, abi));
}

/* the hook is defined in the program's C - the regular expression of C# (multiline): `^(?!static|extern)[A-Za-z_][^\n;=(]*[\s\*]name(\(|\[\] =)` */
static bool fgDefinesHook(const char* programC, const char* name) {
    const char* line = programC;
    size_t n = strlen(name);
    while (*line) {
        const char* end = strchr(line, '\n');
        size_t len = end ? (size_t)(end - line) : strlen(line);
        if (len > 0 && ((line[0] >= 'A' && line[0] <= 'Z') || (line[0] >= 'a' && line[0] <= 'z') || line[0] == '_')
            && !(len >= 6 && (strncmp(line, "static", 6) == 0 || strncmp(line, "extern", 6) == 0))) {
            size_t bad = 1, p;
            while (bad < len && line[bad] != ';' && line[bad] != '=' && line[bad] != '(') bad++;
            /* (the part [^\n;=(]* is line[1 .. p-2]: free of ;=( while p - 1 <= bad) */
            for (p = 2; p - 1 <= bad && p + n <= len; p++) {
                char prev = line[p - 1];
                const char* after;
                if (!(prev == ' ' || prev == '\t' || prev == '\r' || prev == '\f' || prev == '\v' || prev == '*')) continue;
                if (strncmp(line + p, name, n) != 0) continue;
                after = line + p + n;
                if ((p + n < len && after[0] == '(') || (p + n + 4 <= len && strncmp(after, "[] =", 4) == 0)) return true;
            }
        }
        if (!end) break;
        line = end + 1;
    }
    return false;
}
/* The program, linkage "shared" on Windows: the table of what the DLLs use of it (sorted by name: `fire_hostFind`) and the calls of their link functions. */
static const char* HostTable(FgGen* g, const char* programC) {
    FcDict* names;
    FcList* keys;
    FcList* decls;
    FcBuf sb = {0};
    int i;
    if (!g->dllModules) return "";
    names = fcDictNew(g->c, FC_KEY_STR);
    for (i = 0; i < FG_HOST_HOOK_COUNT; i++) if (fgDefinesHook(programC, fgHostHooks[i][0])) fcDictPut(names, fgHostHooks[i][0], NULL);
    for (i = 0; i < g->dllModules->count; i++) FC_DICT_EACH(((FgModuleOutput*)g->dllModules->items[i])->hostImports, e) fcDictPut(names, e->key, e->value);
    if (g->openWorld) {
        FcList* open = OpenWorldNativesC(g);
        int k;
        /* (what a plugin's DLL may use of the program: the bases of all modules, the reflection functions it defines) */
        for (i = 0; i < g->dllModules->count; i++) {
            const char* id = ((FgModuleOutput*)g->dllModules->items[i])->spec->id;
            const char* n = fcFmt(g->c, "fire_mb_%s", id);
            if (!fcDictHas(names, n)) fcDictPut(names, n, (void*)fcFmt(g->c, "extern const uint32_t fire_mb_%s;", id));
        }
        for (k = 0; k < open->count; k += 2) {
            FgCNative* cn = (FgCNative*)open->items[k];
            const char* n = fcFmt(g->c, "fire_cn_%s", cn->function);
            if (!fcDictHas(names, n)) fcDictPut(names, n, (void*)fcFmt(g->c, "Value fire_cn_%s(%s);", cn->function, ThunkParams(g, cn->argc, cn->needsList)));
        }
        for (k = 0; k < FG_SHARED_RF_COUNT; k++)
            if (strstr(programC, fcFmt(g->c, "\nFIRE_MAYBE_UNUSED Value %s(", fgSharedReflectionFunctions[k][0])) && !fcDictHas(names, fgSharedReflectionFunctions[k][0]))
                fcDictPut(names, fgSharedReflectionFunctions[k][0], (void*)fcFmt(g->c, "Value %s%s;", fgSharedReflectionFunctions[k][0], fgSharedReflectionFunctions[k][1]));
    }
    keys = fgSortedKeys(g->c, names, true);
    fcBufStr(&sb, "/* ---- linkage \"shared\" on Windows: what the DLLs of the runtime and the modules use of the program (their fire_link_* functions ask by name) ---- */\n");
    decls = fcListNew(g->c);
    for (i = 0; i < keys->count; i++) { const char* d = (const char*)fcDictGet(names, keys->items[i]); if (d && !fcListHasStr(decls, d)) fcListAdd(g->c, decls, (void*)d); }
    for (i = 0; i < decls->count; i++) fcBufFmt(&sb, "%s\n", (const char*)decls->items[i]);
    fcBufStr(&sb, "typedef struct FireHostSym { const char* name; const void* p; } FireHostSym;\n");
    fcBufStr(&sb, "static const FireHostSym kHostSyms[] = {\n");
    for (i = 0; i < keys->count; i++) fcBufFmt(&sb, "    {%s, (const void*)&%s},\n", CString(g, (const char*)keys->items[i]), (const char*)keys->items[i]);
    fcBufStr(&sb, "    {NULL, NULL}\n");
    fcBufStr(&sb, "};\n");
    fcBufStr(&sb, "static const void* fire_hostFind(const char* name) {\n");
    fcBufFmt(&sb, "    int lo = 0, hi = %d;\n", keys->count);
    fcBufStr(&sb, "    while (lo < hi) { int mid = lo + (hi - lo) / 2, c = strcmp(name, kHostSyms[mid].name); if (c == 0) return kHostSyms[mid].p; if (c < 0) hi = mid; else lo = mid + 1; }\n");
    fcBufStr(&sb, "    return NULL;\n");
    fcBufStr(&sb, "}\n");
    fcBufStr(&sb, "const char* fire_link_rt(const void* (*find)(const char*));\n");
    for (i = 0; i < g->dllModules->count; i++) fcBufFmt(&sb, "const char* fire_link_%s(const void* (*find)(const char*));\n", ((FgModuleOutput*)g->dllModules->items[i])->spec->id);
    fcBufStr(&sb, "static void fire_linkModule(const char* module, const char* missing) {\n");
    fcBufStr(&sb, "    if (!missing) return;\n");
    fcBufStr(&sb, "    fprintf(stderr, \"The library of the module '%s' does not fit this program: it needs '%s', which the program does not have. Build the program again or put the library it was built with next to it.\\n\", module, missing);\n");
    fcBufStr(&sb, "    exit(70);\n");
    fcBufStr(&sb, "}\n");
    return fcBufDone(g->c, &sb);
}

static const char* HostLinkCalls(FgGen* g) {
    FcBuf sb = {0};
    int i;
    if (!g->dllModules) return "";
    fcBufStr(&sb, "    fire_linkModule(\"runtime\", fire_link_rt(fire_hostFind));\n");
    for (i = 0; i < g->dllModules->count; i++) {
        const FgModuleSpec* m = ((FgModuleOutput*)g->dllModules->items[i])->spec;
        fcBufFmt(&sb, "    fire_linkModule(%s, fire_link_%s(fire_hostFind));\n", CString(g, m->name), m->id);
    }
    return fcBufDone(g->c, &sb);
}

/* After the library of a module is built: its interface says it is in the cache. */
static void CommitStatic(const FgStaticLibrary* library) {
    char pending[4096];
    snprintf(pending, sizeof pending, "%s.new", library->interfaceFile);
    { FILE* f = fopen(pending, "rb"); if (!f) return; fclose(f); }
    rename(pending, library->interfaceFile);
}
