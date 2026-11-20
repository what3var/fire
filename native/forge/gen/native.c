/* native/forge/gen/native.c - what drives the generator (commit 808e9788): the built-in targets (src/fire/Runtime/TargetProfile.cs,
 * TargetProfile.Pc.cs), Linker.CompileAndLink with the engine `native` for a target (the symbols of the target and of the toolchain `files`, the
 * widths of the target, its imports), CppGenerator.GenerateSources and `forge-c cgen <options> <file>` - the measure of Phase D 1, what
 * FrontendDump.DumpNative of the C# forge writes (the C of `forge native`, then the natives in C++ and the units, each after `//==== <name>`). */

static const char* const fgImportsEmbedded[] = { "print", "io", "filesystem", "devices", "time", "reflection", "linq" };
static const char* const fgImportsDos[] = { "print", "io", "filesystem", "devices", "time", "console", "machine", "dos", "xms", "x86", "graphics", "windows", "reflection", "linq" };
static const char* const fgImportsWin16[] = { "print", "io", "filesystem", "time", "random", "hash", "json", "compression", "graphics", "windows", "ui", "dialogs", "reflection", "linq" };
static const char* const fgImportsBios16[] = { "print", "time", "console", "machine", "graphics", "windows", "devices", "reflection", "linq" };
static const char* const fgImportsBios16Dos[] = { "print", "time", "console", "machine", "graphics", "windows", "devices", "reflection", "linq", "io", "filesystem", "dos" };
static const char* const fgIncludesEsp32[] = { "freertos/FreeRTOS.h", "freertos/task.h", "freertos/semphr.h" };
static const char* const fgIncludesFreeRtos[] = { "FreeRTOS.h", "task.h", "semphr.h" };
static const char* const fgDefinesBios16Dos[] = { "FIRE_BIOS_MINIDOS=1" };
#define FG_N(a) (int)(sizeof(a) / sizeof((a)[0]))
/* name, symbols, floatWidth, intWidth, wordWidth, optimize, stack, imports, hal, embedded, extension, display, platform, includes, defines, entry, entryKind, externC, toolchain, pointerCalls */
static const FgTarget fgTargets[] = {
    { "windows", { "windows" }, 1, 64, 64, 64, 0, 64 * 1024, NULL, -1, "windows", false, ".exe", "Windows", "windows", NULL, 0, NULL, NULL, 0, "main", 0, false, NULL, 0, NULL, true, true, false, NULL },
    { "linux", { "linux", "posix" }, 2, 64, 64, 64, 0, 64 * 1024, NULL, -1, "posix", false, "", "Linux", "posix", NULL, 0, NULL, NULL, 0, "main", 0, false, NULL, 0, NULL, true, true, false, NULL },
    { "macos", { "macos", "posix" }, 2, 64, 64, 64, 0, 64 * 1024, NULL, -1, "posix", false, "", "macOS", "posix", NULL, 0, NULL, NULL, 0, "main", 0, false, NULL, 0, NULL, true, true, false, NULL },
    { "esp32", { "esp32", "freertos" }, 2, 32, 64, 32, 0, 8192, fgImportsEmbedded, FG_N(fgImportsEmbedded), "esp32", true, "", "ESP32", "esp32", fgIncludesEsp32, FG_N(fgIncludesEsp32), NULL, NULL, 0, "app_main", 1, true, NULL, 0, "esp-idf", true, true, false, NULL },
    { "freertos", { "freertos" }, 1, 32, 64, 32, 0, 8192, fgImportsEmbedded, FG_N(fgImportsEmbedded), "freertos", true, "", "FreeRTOS (sources)", "freertos", fgIncludesFreeRtos, FG_N(fgIncludesFreeRtos), NULL, NULL, 0, "fire_start", 1, true, NULL, 0, "files", true, true, false, NULL },
    { "wasm", { "wasm", "browser" }, 2, 64, 64, 32, 0, 64 * 1024, NULL, -1, "wasm", false, ".html", "WebAssembly (browser)", "wasm", NULL, 0, NULL, NULL, 0, "main", 0, false, NULL, 0, "emscripten", true, true, false, NULL },
    { "dos16", { "dos16", "dos" }, 2, 32, 32, 16, 1, 16384, fgImportsDos, FG_N(fgImportsDos), "dos16", false, ".exe", "DOS (16 bit, real mode)", "dos16", NULL, 0, NULL, NULL, 0, "main", 0, false, NULL, 0, "watcom", true, true, true, NULL },
    { "dos32", { "dos32", "dos" }, 2, 64, 64, 32, 1, 65536, fgImportsDos, FG_N(fgImportsDos), "dos32", false, ".exe", "DOS (32 bit, DOS/32A)", "dos32", NULL, 0, NULL, NULL, 0, "main", 0, false, NULL, 0, "watcom", true, true, true, NULL },
    { "win16", { "win16", "windows3" }, 2, 32, 32, 16, 1, 16384, fgImportsWin16, FG_N(fgImportsWin16), "win16", false, ".exe", "Windows 3.x", "win16", NULL, 0, NULL, NULL, 0, "fire_win16_main", 0, false, NULL, 0, "watcom", true, true, true, NULL },
    { "bios16", { "bios16", "bios" }, 2, 32, 32, 16, 1, 16384, fgImportsBios16, FG_N(fgImportsBios16), "bios16", false, "", "BIOS16", "bios16", NULL, 0, NULL, NULL, 0, "main", 0, false, NULL, 0, "watcom", true, true, true, NULL },
    { "bios16-dos", { "bios16", "bios", "bios16dos" }, 3, 32, 32, 16, 1, 16384, fgImportsBios16Dos, FG_N(fgImportsBios16Dos), "bios16", false, "", "BIOS16 (+IO)", "bios16", NULL, 0, NULL, fgDefinesBios16Dos, FG_N(fgDefinesBios16Dos), "main", 0, false, NULL, 0, "watcom", true, true, true, NULL },
};
static const FgTarget* fgHostTarget(void) {
#if defined(_WIN32)
    return &fgTargets[0];
#elif defined(__APPLE__)
    return &fgTargets[2];
#else
    return &fgTargets[1];
#endif
}
static const FgTarget* fgTargetNamed(const char* name) {
    size_t i;
    if (!name) return fgHostTarget();
    for (i = 0; i < sizeof fgTargets / sizeof fgTargets[0]; i++) if (fcAsciiIEq(fgTargets[i].name, name)) return &fgTargets[i];
    return NULL;
}
static bool fgTargetHasImport(const FgTarget* t, const char* import) {
    int i;
    if (t->importCount < 0 || fcStartsWith(import, "pkg:")) return true;
    for (i = 0; i < t->importCount; i++) if (streq0(t->imports[i], import)) return true;
    return false;
}

/* ---- the options of a native build (the configuration and the command line of `forge native`) ---- */
typedef struct FgOptions {
    const FgTarget* target;
    int mode;              /* -1: the source decides */
    int intWidth, floatWidth;   /* 0: none */
    int stripping;         /* -1: none (level 3; a debug build 0) */
    int optimize;          /* -1: the target's */
    bool split;
} FgOptions;

/* Linker.CompileAndLink with Engine "native", ToolchainKind "files" for one file */
static FcLinked* fgLinkNative(FcCtx* c, const char* path, const char* preludePath, const char* storeRoot, bool strict, const FgOptions* o, FcNatives** registry) {
    FcLinker* L = (FcLinker*)fcAlloc(c, sizeof *L);
    FcList* processed = fcListNew(c);
    const uint16_t* src;
    int len, firstUserSource = 1, i, total, intWidth, floatWidth;
    const char* full = fcFullPath(c, ".", path);
    const char* folder = fcDirectoryName(c, full);
    FcProcessed* p;
    FcProcessed** arr;
    FcList* program;
    FcResolveResult* resolved;
    FcCompiled* compiled;
    FcLinked* linked;
    FcDict* notSerializable;
    const uint16_t* userText;
    int userLength;
    const FgTarget* t = o->target;
    L->c = c;
    L->store.root = storeRoot;
    L->directiveIntWidth = L->directiveFloatWidth = FC_NULL_INT;
    fcDictInit(c, &L->nativeImports, FC_KEY_STR);
    fcDictInit(c, &L->natives, FC_KEY_STR);
    fcNativesInit(c, &L->registry);
    L->executionMode = 1;
#define FIRE_NATIVE(index, name, tryable) fcRegisterNative(L, name);
#include "../../tables/natives.def"
#undef FIRE_NATIVE
    fcSetAdd(&L->nativeImports, "print");
    /* ConditionalSymbols.For(target, "native", floatWidth, defines, intWidth) and the toolchain `files` */
    for (i = 0; i < t->symbolCount; i++) fcSymbolsAdd(c, c->symbols, t->symbols[i]);
    fcSymbolsAdd(c, c->symbols, "native");
    if ((o->floatWidth ? o->floatWidth : t->floatWidth) == 32) fcSymbolsAdd(c, c->symbols, "float32");
    if ((o->intWidth ? o->intWidth : t->intWidth) == 32) fcSymbolsAdd(c, c->symbols, "int32");
    if ((o->intWidth ? o->intWidth : t->intWidth) == 16) fcSymbolsAdd(c, c->symbols, "int16");
    fcSymbolsAdd(c, c->symbols, fcFmt(c, "word%d", t->wordWidth));
    fcSymbolsAdd(c, c->symbols, "files");
    c->sharedIncluded = fcListNew(c);
    fcListAdd(c, c->sharedIncluded, (void*)full);
    c->resourcePaths = fcListNew(c);
    c->resourceNames = fcListNew(c);
    c->directiveHook = fcLinkerDirective;
    c->directiveState = L;
    if (!fcReadSource(c, preludePath, &src, &len)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "cannot read the prelude '%s'", preludePath));
    fcListAdd(c, processed, fcPreprocess(c, src, len, folder));
    if (!fcReadSource(c, full, &src, &len)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "cannot read '%s'", path));
    userText = src;
    userLength = len;
    p = fcPreprocess(c, src, len, folder);
    p->name = fcStr(c, fcBaseName(path));
    fcListAdd(c, processed, p);
    firstUserSource += fcInsertPreludes(L, processed);
    c->directiveHook = NULL;
    arr = (FcProcessed**)fcAlloc(c, sizeof(FcProcessed*) * (size_t)processed->count);
    for (i = 0; i < processed->count; i++) arr[i] = (FcProcessed*)processed->items[i];
    program = fcParseMultiple(c, arr, processed->count);
    resolved = fcResolve(c, program, &L->natives, NULL);
    intWidth = o->intWidth ? o->intWidth : L->directiveIntWidth != FC_NULL_INT ? L->directiveIntWidth : t->intWidth;
    floatWidth = o->floatWidth ? o->floatWidth : L->directiveFloatWidth != FC_NULL_INT ? L->directiveFloatWidth : t->floatWidth;
    fcTypeCheckRun(c, program, resolved, strict, firstUserSource, firstUserSource, intWidth, floatWidth, fcNativeReturnTypes(L));
    compiled = fcCompile(c, program, resolved, &L->registry, false);
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
        uint8_t* data = fbReadFile((const char*)c->resourcePaths->items[i], &n);
        r->name = (const char*)c->resourceNames->items[i];
        r->data = (uint8_t*)fcAlloc(c, n + 1);
        if (data && n) memcpy((void*)r->data, data, n);
        free(data);
        r->size = n;
        fcListAdd(c, compiled->resources, r);
    }
    for (i = 0; i < c->customUnits->count; i++) fcListAdd(c, compiled->customUnits, c->customUnits->items[i]);
    FC_DICT_EACH(&L->nativeImports, e)
        if (!fgTargetHasImport(t, (const char*)e->key)) {
            FcList* avail = fcListNew(c);
            int k;
            for (k = 0; k < t->importCount; k++) fcListAdd(c, avail, (void*)t->imports[k]);
            fcThrow(c, FC_ERR_LINKER, 0, 0, NULL, fcFmt(c, "The library '%s' is not available on the target '%s' (available: %s).", (const char*)e->key, t->name, fgJoin(c, avail, ", ")));
        }
    linked->program = compiled;
    linked->floatWidth = floatWidth;
    if (floatWidth == 32) fcNarrow(c, compiled, fcNarrowFloat, 32);
    linked->intWidth = intWidth;
    if (intWidth < 64) fcNarrow(c, compiled, fcNarrowInt, intWidth);
    linked->wordWidth = t->wordWidth;
    linked->firstUserSource = firstUserSource;
    linked->mode = o->mode >= 0 ? o->mode : L->executionMode;
    linked->nativeImports = &L->nativeImports;
    fcPackageNativesOf(L, &linked->packageNatives, &linked->packageNativeLibraries);
    linked->sourceFiles = fcListNew(c);
    linked->sourceHashes = fcListNew(c);
    for (i = 0; i < total; i++) { fcListAdd(c, linked->sourceFiles, NULL); fcListAdd(c, linked->sourceHashes, NULL); }
    if (total >= 1) {
        linked->sourceFiles->items[total - 1] = (void*)full;
        linked->sourceHashes->items[total - 1] = (void*)fcSha256OfText(c, fc8FromU16(c, userText, userLength));
    }
    linked->optimize = o->optimize >= 0 ? o->optimize : t->optimize;
    *registry = &L->registry;
    (void)userLength;
    return linked;
}

/* the generator of a program (the constructor of CppGenerator) */
static FgGen* fgGenNew(FcCtx* c, FcLinked* program, FcList* natives, const FgTarget* target) {
    FgGen* g = (FgGen*)fcAlloc(c, sizeof *g);
    g->c = c;
    g->program = program;
    g->natives = natives;
    g->target = target;
    g->units = fcListNew(c);
    fcListAdd(c, g->units, FC_UNITLESS);
#define FG_D(field) g->field = fcDictNew(c, FC_KEY_STR)
#define FG_P(field) g->field = fcDictNew(c, FC_KEY_PTR)
    FG_D(unitConverters); g->strings = fcListNew(c); FG_D(stringIndex); FG_D(globals); FG_P(funcByProto); g->funcs = fcListNew(c);
    FG_D(specFuncs); FG_D(classes); g->classList = fcListNew(c); FG_D(dispatchers); FG_D(interfaceDispatchers); FG_D(operators); FG_D(isOfTypes);
    FG_D(hasMembers); FG_D(targIds); FG_D(targListIds); FG_D(targOfs); FG_D(selectorPaths); FG_D(fieldNames); FG_D(staticFields); FG_D(varRef);
    g->excTypes = fcListNew(c); fcListAdd(c, g->excTypes, (void*)"Exception");
    g->enumList = fcListNew(c); FG_D(unitFactorCaches); FG_D(catchCallbacks); FG_D(refCallSites); FG_P(lambdaDefaults); FG_D(callTArgTexts); FG_D(classTArgTexts);
    g->objTypeNames = fcListNew(c); FG_D(objTypeIndex); FG_D(narrowEligible); FG_P(layouts); FG_D(probeTargets); FG_D(varType); FG_D(fieldAddressTaken);
    FG_D(typeIsOfIds); FG_D(externDecls); FG_D(restrictedMethods); FG_D(restrictedFields); g->errorClasses = fcListNew(c); FG_D(pkgLocks); FG_D(arrClasses);
    FG_D(lineFiles); FG_D(standardNativesWritten); FG_D(knownArrayElem); FG_D(needsDefaults); FG_D(nativeThunks); FG_D(packageNatives); g->packageImports = fcListNew(c);
    FG_P(cRet); FG_P(cAbiParams); FG_P(funcDebug); FG_D(globalNames); FG_D(overloadSelectors); FG_D(ovClassNames); FG_D(ovGenericTests); FG_D(rawType); FG_P(rawLayouts);
    FG_D(literalClasses); FG_D(literalMembers); FG_P(literalChunks); FG_D(pointerFuncs); FG_P(rtNames); FG_P(rtIndex); FG_D(rtCands); FG_D(specCache); FG_D(condCache); FG_D(nativeImportOf);
#undef FG_D
#undef FG_P
    g->splitMode = -1;
    g->selfAliveAfter = -1;
    return g;
}

/* CppGenerator.GenerateSources: the program in C, its natives in C++ (NULL: none), the units of a split program (NULL: one file) */
typedef struct FgSources { const char* program; const char* natives; FcList* units; } FgSources;
static FgSources fgGenerateSources(FcCtx* c, FcLinked* program, FcList* natives, const FgTarget* target, int stripping, bool split, FcStore* store) {
    FgGen* g = fgGenNew(c, program, natives, target);
    FgSources r;
    const char* text;
    /* EffectiveStripping: a debug build keeps everything, otherwise what is asked for, by default level 3 */
    g->strip = program->mode == 0 ? 0 : stripping >= 0 ? stripping : 3;
    /* (`forge native`: the toolchain `files` splits with `"split": "on"`, `auto` keeps one file there) */
    if (split) g->splitMode = 1;
    CollectPackages(g, store);
    text = Run(g);
    r.program = g->splitUnits ? ((FgSplitUnit*)g->splitUnits->items[1])->text : text;
    r.natives = g->nativesCode;
    r.units = g->splitUnits;
    return r;
}

/* `cgen:<options>` of one file: what FrontendDump.DumpNative writes */
static void fgDumpNative(FcCtx* c, const char* options, const char* path, const char* prelude, const char* storeRoot, FcBuf* out) {
    FgOptions o;
    FcList* words = fgSplit(c, options, '+');
    FcLinked* linked;
    FcNatives* registry;
    FgSources s;
    FcStore store;
    int i;
    memset(&o, 0, sizeof o);
    o.target = fgHostTarget();
    o.mode = -1; o.stripping = -1; o.optimize = -1;
    for (i = 0; i < words->count; i++) {
        const char* w = (const char*)words->items[i];
        if (!*w || streq0(w, "default")) continue;
        if (streq0(w, "debug")) o.mode = 0;
        else if (streq0(w, "release")) o.mode = 1;
        else if (streq0(w, "performance")) o.mode = 2;
        else if (streq0(w, "split")) o.split = true;
        else if (streq0(w, "speed")) o.optimize = 0;
        else if (streq0(w, "size")) o.optimize = 1;
        else if (streq0(w, "i16")) o.intWidth = 16;
        else if (streq0(w, "i32")) o.intWidth = 32;
        else if (streq0(w, "f32")) o.floatWidth = 32;
        else if (fcStartsWith(w, "strip")) o.stripping = atoi(w + 5);
        else if (fcStartsWith(w, "t:")) { o.target = fgTargetNamed(w + 2); if (!o.target) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "unknown target %s", w + 2)); }
        else fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "unknown option %s", w));
    }
    linked = fgLinkNative(c, path, prelude, storeRoot, fcStrictTag(path), &o, &registry);
    memset(&store, 0, sizeof store);
    store.root = storeRoot;
    s = fgGenerateSources(c, linked, registry->names, o.target, o.stripping, o.split, &store);
    fcOut8(out, s.program);
    if (s.natives) { fcBufStr(out, "//==== fire_natives.cpp\n"); fcOut8(out, s.natives); }
    if (s.units) FG_UNPORTED_C(c, "split units");
}
