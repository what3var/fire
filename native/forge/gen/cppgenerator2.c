/* native/forge/gen/cppgenerator2.c - src/fire.Native/CppGenerator.cs (commit 808e9788), part 2: the whole program (Run: the fixpoint and the file),
 * the head, RegisterTargets, default values, the dispatchers, the field helpers, the class descriptors, the runtime hooks (the C# file's lines
 * 1971-3512, in its order). */

static void GenerateChunk(FgGen* g, FgFunc* f);
static const char* FieldHelpers(FgGen* g, const char* name);
static const char* Dispatcher(FgGen* g, const char* name, int argc);
static const char* InterfaceDispatcher(FgGen* g, const char* iface, const char* name, int argc);
typedef struct FgSelector FgSelector;
static const char* SelectorFunction(FgGen* g, const FgSelector* selector);
static const char* SelectorSignature(FgGen* g, const FgSelector* selector);
static const char* DispatcherSignature(FgGen* g, const char* name, int argc, const char* prefix);
static const char* InterfaceDispatcherSignature(FgGen* g, const char* iface, const char* name, int argc);
static const char* DispatchTail(FgGen* g, const char* name, int argc, const char* list, const char* caller);
static const char* RuntimeHooks(FgGen* g);
static const char* IndexHelpers(FgGen* g);
static const char* EnumerateItemsFunction(FgGen* g);
static void RegisterTargets(FgGen* g);
static const char* HeadDefines(FgGen* g);
static const char* HeadTail(FgGen* g, int dimCount);
static const char* DoubleLiteral(FgGen* g, double d, bool single);
static const char* ResolveLineResets(FgGen* g, const char* text, const char* fileName);
static void FunctionPrototypes(FgGen* g, FcBuf* sb);
static const char* ProgramSplitMarker(FgGen* g, const char* what);
static const char* PointerArgs(FgGen* g, const char* code);
static const char* GuardKindCall(FgGen* g, const char* value, int declared, const char* what);
static const char* NewObjectCall(FgGen* g, const FgClassInfo* cls, const char* owner);
static const char* Nested(FgGen* g, const char* statement);
static const char* WrapProbeSetter(FgGen* g, const char* name, const char* text);
static const char* FieldPtrExpr(FgGen* g, const FcRtClass* rc, int index, const char* addrOfSlot, const char* obj);
static const FcFieldInfo* DeclaringInfo(const FcRtClass* rc, const char* name);
static const char* OverloadChoice(FgGen* g, FcProto** candidates, int count, int argc, const char* ownerObj, const char* (*call)(void* ctx, int c), void* ctx, const char* indent, const char* (*scoredArg)(void* ctx, int i));
static const char* OverloadClassFunctions(FgGen* g, bool classScores);
static const char* ReflectionCode(FgGen* g);
static const char* JsonCode(FgGen* g);
static const char* DebugProgramTables(FgGen* g);
static void RegisterReflectionTargets(FgGen* g);
static void RegisterKeptTargets(FgGen* g);
static void RegisterJsonTargets(FgGen* g);
static const char* LibraryThunks(FgGen* g);
static const char* PointerCallsPass(FgGen* g, const char* text);
static bool PointerCallsOnOf(FgGen* g);
#define PointerCallsOn PointerCallsOnOf(g)
static const char* WithoutSplitMarks(FgGen* g, const char* text);
static FcList* ProgramSplitSplit(FgGen* g, const char* programText, FcList* splitKeys);
typedef struct FgFuncKey { const char* module; const char* key; } FgFuncKey;
static FgFuncKey* SplitKeyOf(FgGen* g, FgFunc* f);
static const char* SplitNativesGroup(FgGen* g, const char* import);
typedef struct FgSplitUnit { const char* name; const char* text; int kind; int64_t cost; } FgSplitUnit;
#define DebugAgent (streq0(g->target->platform, "posix") || streq0(g->target->platform, "windows") || streq0(g->target->platform, "esp32") || streq0(g->target->platform, "freertos"))

/* the units a text may name */
typedef struct FgTextUnit { const char* symbol; int unit; bool prefixable; } FgTextUnit;
static const char* const fgBuiltInSymbols[] = { "m", "g", "s", "b", "B", "l", "min", "h", "d" };
static bool fgBuiltInPrefixable(const char* s) { return (streq0(s, "m") || streq0(s, "g") || streq0(s, "s") || streq0(s, "b") || streq0(s, "B") || streq0(s, "l")) && !(streq0(s, "min") || streq0(s, "h") || streq0(s, "d")); }
/* Unit.OfCustom */
static const FcU* fgUnitOfCustom(FgGen* g, const FcCustomUnit* own) { return fcUNew(g->c, &own->unit, own->symbol); }

static const char* MainParams(FgGen* g) { return streq0(g->target->platform, "wasm") ? "void" : "int argc, char** argv"; }
static const char* MainSetArgs(FgGen* g) { return streq0(g->target->platform, "wasm") ? "fire_setArgs(0, NULL);" : "fire_setArgs(argc, argv);"; }

static int fgCmpDims(const void* a, const void* b) { return fgCultureCmp(*(const char* const*)a, *(const char* const*)b); }

static const char* Run(FgGen* g) {
    FgFunc* main;
    FcBuf sb = {0};
    FcBuf nat = {0};
    FcList* textUnits;
    FcDict* dimSet;
    FcList* dimNames;
    const char* prelude;
    const char* programText;
    FcList* splitKeys;
    FcBuf libraryThunks = {0};
    bool marks, natSources = false;
    int round, i, k;
    main = g->main = fgFuncNew(g, NULL, g->program->program->topLevel, FK_Main, 0);
    if (DebugBuild) UseExceptions(g);
    FC_DICT_EACH(g->program->program->classes, e) if (((FcRtClass*)e->value)->isActor) { g->usesActors = true; UseThreads(g); break; }
    ApplyForcedFlags(g);
    AddLibraryNeeds(g);

    /* Fixpoint: generating code can create classes, functions and method calls, and can show that a variable may hold a reference; all of that changes
     * what the code generated before it has to look like. */
    for (round = 0;; round++) {
        int before = g->version;
        if (LibraryRole) KeepOwnModule(g);
        else GenerateChunk(g, main);
        for (i = 0; i < g->funcs->count; i++) {
            FgFunc* f = (FgFunc*)g->funcs->items[i];
            if (!LibraryRole) { GenerateChunk(g, f); continue; }
            /* a library module keeps every member: one the target cannot translate (fire threads on wasm) becomes a function that fails when it is called */
            FC_TRY(g->c) { GenerateChunk(g, f); }
            FC_CATCH(g->c) {
                if (g->c->err.kind != FC_ERR_NATIVE || f->kind == FK_Main) fcRethrow(g->c);
                f->code = UnsupportedStub(g, f, g->c->err.message);
            } FC_END_TRY;
        }
        RegisterTargets(g);
        if (g->role == FG_ROLE_PROGRAM) ReachLibraryClasses(g);   /* (the classes of the libraries the code reached so far can make) */
        /* (made once more when the file is put together: the strings, units and type arguments they use are registered by now) */
        if (Modular) ModularHelpers(g);   /* (modules: all of its helpers - their checks register strings, units, `is of` tests, type arguments and functions) */
        else {
            FcList* names = fgSortedKeys(g->c, g->fieldNames, true);
            for (i = 0; i < names->count; i++) FieldHelpers(g, (const char*)names->items[i]);
            TArgTables(g, true);
        }
        if ((!LibraryRole || g->plugin) && (g->usesStringCall || g->program->program->customUnits->count > 0)) {
            size_t s;
            for (s = 0; s < sizeof fgBuiltInSymbols / sizeof fgBuiltInSymbols[0]; s++) UnitId(g, fcUParse(g->c, fgBuiltInSymbols[s]));
            for (i = 0; i < g->program->program->customUnits->count; i++) UnitId(g, fgUnitOfCustom(g, (const FcCustomUnit*)g->program->program->customUnits->items[i]));
        }
        if (g->version == before) break;
        if (round > 40) FG_NOTSUP("code generation does not settle");
    }

    g->initCode.len = 0;
    if (LibraryRole) return LibraryBody(g);
    fcBufStr(&sb, HeadDefines(g));
    /* The units a text may name (`"5mm".ToFloat()`, Unit.ParseKnown): those of fire without a prefix, then those of the program by symbol and name */
    textUnits = fcListNew(g->c);
    if (g->usesStringCall || g->program->program->customUnits->count > 0) {
        size_t s;
        for (s = 0; s < sizeof fgBuiltInSymbols / sizeof fgBuiltInSymbols[0]; s++) {
            FgTextUnit* t = (FgTextUnit*)fcAlloc(g->c, sizeof *t);
            t->symbol = fgBuiltInSymbols[s]; t->unit = UnitId(g, fcUParse(g->c, fgBuiltInSymbols[s])); t->prefixable = fgBuiltInPrefixable(fgBuiltInSymbols[s]);
            fcListAdd(g->c, textUnits, t);
        }
        for (i = 0; i < g->program->program->customUnits->count; i++) {
            const FcCustomUnit* own = (const FcCustomUnit*)g->program->program->customUnits->items[i];
            int id = UnitId(g, fgUnitOfCustom(g, own));
            FgTextUnit* t = (FgTextUnit*)fcAlloc(g->c, sizeof *t);
            t->symbol = own->symbol; t->unit = id; t->prefixable = own->prefixed;
            fcListAdd(g->c, textUnits, t);
            if (!streq0(own->name, own->symbol)) {
                t = (FgTextUnit*)fcAlloc(g->c, sizeof *t);
                t->symbol = own->name; t->unit = id; t->prefixable = false;
                fcListAdd(g->c, textUnits, t);
            }
        }
    }
    /* The unit table: the base symbols of all units of the program (sorted like the VM prints them) and one row per unit. */
    dimSet = fcDictNew(g->c, FC_KEY_STR);
    for (i = 0; i < g->units->count; i++) {
        const FcU* u = (const FcU*)g->units->items[i];
        for (k = 0; k < u->u.dimCount; k++) fcSetAdd(dimSet, u->u.dimSym[k]);
    }
    dimNames = fgKeys(g->c, dimSet);
    /* (OrderBy is a stable sort: equal keys keep the order of Distinct) */
    for (i = 1; i < dimNames->count; i++)
        for (k = i; k > 0 && fgCultureCmp((const char*)dimNames->items[k - 1], (const char*)dimNames->items[k]) > 0; k--) { void* t = dimNames->items[k]; dimNames->items[k] = dimNames->items[k - 1]; dimNames->items[k - 1] = t; }
    if (g->separate && dimNames->count > FG_SEPARATE_DIMS) FG_NOTSUP("the program uses %d dimensions of units; with linkage \"static\" at most %d", dimNames->count, FG_SEPARATE_DIMS);
    fcBufStr(&sb, HeadTail(g, dimNames->count));
    prelude = fcStrN(g->c, sb.p, sb.len);
    /* the system libraries that packages need on this platform (`linkLibraries`): the build links them */
    for (i = 0; i < g->packageImports->count; i++) {
        FgPkgImportKey* p = (FgPkgImportKey*)g->packageImports->items[i];
        FcList* libs;
        if (!p->import->native) continue;
        libs = fgLinkLibrariesFor(g->c, p->import->native, fgPlatformKeys(g));
        for (k = 0; k < libs->count; k++) fcBufFmt(&sb, "// fire-link: %s\n", (const char*)libs->items[k]);
    }
    if (g->usesWindows && (streq0(g->target->platform, "posix") || streq0(g->target->platform, "windows"))) {
        FcList* libs = DisplayLibraries(g, g->target);
        for (i = 0; i < libs->count; i++) fcBufFmt(&sb, "// fire-link: %s\n", (const char*)libs->items[i]);
    }
    if (DebugBuild && DebugAgent && streq0(g->target->platform, "windows")) fcBufStr(&sb, "// fire-link: ws2_32\n");
    if (g->separate) {
        /* the head of the file of each library module: the same configuration of the runtime, without its state (that lives in the program's file) */
        g->libraryHead = LibraryHeadOf(g, prelude);
        if (g->dll) g->runtimeKey = RuntimeDllKey(g, g->libraryHead, g->toolchainStamp);
    }
    fcBufFmt(&sb, "#define FIRE_PLATFORM_HEADER \"platform/%s/fire_platform.h\"\n", g->target->platform);
    if (!g->dll) fcBufStr(&sb, "#define FIRE_RT_IMPLEMENTATION 1   // the runtime's global state lives in this file\n");
    else fcBufStr(&sb, "// (the runtime's global state lives in its DLL)\n");
    fcBufStr(&sb, "#include \"fire_rt.h\"\n");
    fcBufStr(&sb, "#if defined(__GNUC__) || defined(__clang__)\n");
    fcBufStr(&sb, "#pragma GCC diagnostic ignored \"-Wunused-but-set-variable\"\n");
    fcBufStr(&sb, "#pragma GCC diagnostic ignored \"-Wunused-function\"\n");
    fcBufStr(&sb, "#endif\n");
    marks = g->splitMode >= 0 && !g->separate && !Modular && !g->openWorld && !g->dll && !PointerCallsOn;
    if (marks) fcBufFmt(&sb, "%s\n", ProgramSplitMarker(g, "head-end"));

    /* The natives in C of the packages ("language": "c") and the bridges of graphics and windows: part of this file, after the runtime - a C compiler is enough
     * (TinyCC, a board without C++). Their platform layers come with them (platformSources, ROADMAP 13.3b).
     * (linkage "static": the natives of an import that is a library module live in the file of that module) */
    if (g->usesGraphics && !NativesOwner(g, "graphics")) { if (marks) fcBufFmt(&sb, "%s\n", ProgramSplitMarker(g, fcFmt(g->c, "natives %s", SplitNativesGroup(g, "graphics")))); AppendStandardNatives(g, &sb, "graphics"); }
    if (g->usesWindows && !NativesOwner(g, "windows")) { if (marks) fcBufFmt(&sb, "%s\n", ProgramSplitMarker(g, fcFmt(g->c, "natives %s", SplitNativesGroup(g, "windows")))); AppendStandardNatives(g, &sb, "windows"); }
    for (i = 0; i < g->packageImports->count; i++) {
        FgPkgImportKey* p = (FgPkgImportKey*)g->packageImports->items[i];
        FcList* texts;
        if (!(p->import->native && p->import->native->isC) || fcDictHas(g->standardNativesWritten, fcAsciiLower(g->c, p->import->name)) || NativesOwner(g, p->key)) continue;
        if (marks) fcBufFmt(&sb, "%s\n", ProgramSplitMarker(g, fcFmt(g->c, "natives %s", SplitNativesGroup(g, p->key))));
        texts = fgReadNativeSources(g->c, g->instImports, g->store, p->import, fgPlatformKeys(g));
        for (k = 0; k < texts->count; k++) {
            FgNamedText* t = (FgNamedText*)texts->items[k];
            fcBufFmt(&sb, "// ---- package %s %s, import \"%s\" (C): %s\n", p->import->pkgName, p->import->pkgVersion, p->import->name, t->name);
            fcBufFmt(&sb, "%s\n", PackageNativeText(g, t->text));
            fcBufFmt(&sb, "// ---- end of %s\n", t->name);
        }
    }
    if (marks) fcBufFmt(&sb, "%s\n", ProgramSplitMarker(g, "program"));

    /* linkage "static": the natives in C live in this file only (their state once); the library modules call them through these */
    fcBufStr(&sb, SharedNativeThunks(g));
    /* the library modules (ROADMAP 9, one file): their code after the runtime and the natives in C, before the program's own (the program defines what they declare) */
    if (g->moduleBodies) {
        if (g->usesJson) fcBufStr(&sb, "#define FIRE_JSON_IMPLEMENTATION 1\n#include \"fire_json.h\"\n");
        fcBufStr(&sb, g->moduleBodies);
    }

    /* The natives in C++ (the packages that are still C++): a file of their own, built with a C++ compiler and linked with the program. */
    fcBufStr(&nat, fgReplace(g->c, prelude, "// Generated by fire.Native from the bytecode - do not edit.", "// Generated by fire.Native: the natives in C++ of the program fire_natives.cpp belongs to - do not edit."));
    fcBufFmt(&nat, "#define FIRE_PLATFORM_HEADER \"platform/%s/fire_platform.h\"\n", g->target->platform);
    fcBufFmt(&nat, "#define FIRE_PLATFORM_CPP_HEADER \"platform/%s/fire_platform.hpp\"\n", g->target->platform);
    fcBufStr(&nat, "#include \"fire_rt_cpp.hpp\"   // the runtime (fire_rt.h, the program has its state) and what C++ code expects of it\n");
    /* the C++ source of the packages the program imports: after the runtime, at the top level (the files include what they need and put their functions into namespace fire) */
    for (i = 0; i < g->packageImports->count; i++) {
        FgPkgImportKey* p = (FgPkgImportKey*)g->packageImports->items[i];
        FcList* texts;
        if ((p->import->native && p->import->native->isC) || fcDictHas(g->standardNativesWritten, fcAsciiLower(g->c, p->import->name))) continue;
        texts = fgReadNativeSources(g->c, g->instImports, g->store, p->import, fgPlatformKeys(g));
        for (k = 0; k < texts->count; k++) {
            FgNamedText* t = (FgNamedText*)texts->items[k];
            fcBufFmt(&nat, "// ---- package %s %s, import \"%s\": %s\n", p->import->pkgName, p->import->pkgVersion, p->import->name, t->name);
            fcBufFmt(&nat, "%s\n", PackageNativeText(g, t->text));
            fcBufFmt(&nat, "// ---- end of %s\n", t->name);
            natSources = true;
        }
    }
    if (g->libraries) for (i = 0; i < g->libraries->count; i++) fcBufStr(&libraryThunks, ((FgModuleOutput*)g->libraries->items[i])->nativeThunks);
    if (natSources || fcDictCount(g->nativeThunks) > 0 || libraryThunks.len > 0) {
        FcList* thunks = fcListNew(g->c);
        fcBufStr(&nat, "\n");
        fcBufStr(&sb, "// the natives in C++ (fire_natives.cpp)\n");
        FC_DICT_EACH(g->nativeThunks, e) fcListAdd(g->c, thunks, e);
        /* (OrderBy the index: the order they were added) */
        for (i = 0; i < thunks->count; i++) {
            FcDictEntry* e = (FcDictEntry*)thunks->items[i];
            FgNativeThunk* t = (FgNativeThunk*)e->value;
            const char* ps = ThunkParams(g, t->argc, t->needsList);
            FcList* args = fcListNew(g->c);
            for (k = 0; k < t->argc; k++) fcListAdd(g->c, args, (void*)fcFmt(g->c, "a%d", k));
            if (t->needsList) fcListAdd(g->c, args, (void*)"list");
            fcBufFmt(&sb, "Value fire_nat_%s%d(%s);   // %s\n", Px, t->index, ps, (const char*)e->key);
            fcBufFmt(&nat, "extern \"C\" fire::Value fire_nat_%s%d(%s) { using namespace fire; return %s(%s); }\n", Px, t->index,
                fgReplace(g->c, fgReplace(g->c, ps, "Value", "fire::Value"), "OwnList", "fire::OwnList"), (const char*)e->key, fgJoin(g->c, args, ", "));
        }
        if (libraryThunks.len) fcBufPut(&nat, libraryThunks.p, libraryThunks.len);
        g->nativesCode = fcBufDone(g->c, &nat);
    } else { g->nativesCode = NULL; fcBufFree(&nat); }
    fcBufStr(&sb, "\n");
    {
        FcList* dimTexts = fcListNew(g->c);
        int pad = g->separate ? FG_SEPARATE_DIMS - dimNames->count : dimNames->count == 0 ? 1 : 0;
        for (i = 0; i < dimNames->count; i++) fcListAdd(g->c, dimTexts, (void*)CString(g, (const char*)dimNames->items[i]));
        for (i = 0; i < pad; i++) fcListAdd(g->c, dimTexts, (void*)"\"\"");
        fcBufFmt(&sb, "const char* g_dimNames[] = {%s};   /* (not const: a plugin puts the dimensions of its units into the free places) */\n", fgJoin(g->c, dimTexts, ", "));
    }
    fcBufStr(&sb, "static const UnitInit kUnitInit[] = {\n");
    for (i = 0; i < g->units->count; i++) {
        const FcU* u = (const FcU*)g->units->items[i];
        FcList* dims = fcListNew(g->c);
        const char* display = u->display ? CString(g, u->display) : "NULL";
        int conv = -1, ulconv = -1;
        if (dimNames->count == 0) fcListAdd(g->c, dims, (void*)"0");
        for (k = 0; k < dimNames->count; k++) { int at = fcUnitDim(&u->u, (const char*)dimNames->items[k]); fcListAdd(g->c, dims, (void*)fcFmt(g->c, "%d", at >= 0 ? u->u.dimExp[at] : 0)); }
        if (u->u.converter) conv = ((FgUnitConv*)fcDictGet(g->unitConverters, u->u.converter))->index;
        if (u->u.unitlessConverter) ulconv = ((FgUnitConv*)fcDictGet(g->unitConverters, u->u.unitlessConverter))->index;
        fcBufFmt(&sb, "    {{%s}, %s, %s, %s, %d, %d},\n", fgJoin(g->c, dims, ", "), DoubleLiteral(g, u->u.scale, false), display, DoubleLiteral(g, u->u.offset, false), conv, ulconv);
    }
    fcBufStr(&sb, "};\n");
    if (fcDictCount(g->unitConverters) > 0) {
        FcList* tos = fcListNew(g->c);
        FcList* froms = fcListNew(g->c);
        FC_DICT_EACH(g->unitConverters, e) {
            FgUnitConv* cv = (FgUnitConv*)e->value;
            fcBufFmt(&sb, "%s;\n%s;\n", fgSignature(g, cv->to), fgSignature(g, cv->from));
            fcListAdd(g->c, tos, (void*)cv->to->name);
            fcListAdd(g->c, froms, (void*)cv->from->name);
        }
        fcBufFmt(&sb, "static const UnitConvFn kUnitTo[] = {%s};\n", fgJoin(g->c, tos, ", "));
        fcBufFmt(&sb, "static const UnitConvFn kUnitFrom[] = {%s};\n", fgJoin(g->c, froms, ", "));
        fcBufFmt(&g->initCode, "    unitsInit(kUnitInit, %d, kUnitTo, kUnitFrom);\n", g->units->count);
    }
    else fcBufFmt(&g->initCode, "    unitsInit(kUnitInit, %d, NULL, NULL);\n", g->units->count);
    if (textUnits->count > 0) {
        fcBufStr(&sb, "static const TextUnit kTextUnits[] = {\n");
        for (i = 0; i < textUnits->count; i++) {
            FgTextUnit* t = (FgTextUnit*)textUnits->items[i];
            fcBufFmt(&sb, "    {%s, %d, %s},\n", CString(g, t->symbol), t->unit, t->prefixable ? "true" : "false");
        }
        fcBufStr(&sb, "};\n");
        fcBufFmt(&g->initCode, "    g_textUnits = kTextUnits; g_textUnitCount = %d;\n", textUnits->count);
    }
    /* the files of `new Resource("path")` are part of the binary (docs/stdlib/resources.md) */
    if (g->program->program->resources->count > 0) {
        FcList* res = g->program->program->resources;
        int r;
        size_t b;
        for (r = 0; r < res->count; r++) {
            const FcResourceEntry* entry = (const FcResourceEntry*)res->items[r];
            fcBufFmt(&sb, "static const uint8_t kRes%d[] = {", r);
            if (entry->size == 0) fcBufChar(&sb, '0');
            for (b = 0; b < entry->size; b++) {
                if (b % 32 == 0) fcBufStr(&sb, "\n    ");
                fcBufFmt(&sb, "%d,", entry->data[b]);
            }
            fcBufStr(&sb, "};\n");
        }
        fcBufStr(&sb, "static const ResEntry kResources[] = {\n");
        for (r = 0; r < res->count; r++) {
            const FcResourceEntry* entry = (const FcResourceEntry*)res->items[r];
            fcBufFmt(&sb, "    {%s, kRes%d, %du},\n", CString(g, entry->name), r, (int)entry->size);
        }
        fcBufStr(&sb, "};\n");
        fcBufFmt(&g->initCode, "    g_resources = kResources; g_resourceCount = %d;\n", res->count);
    }
    if (fcDictCount(g->externDecls) > 0) {
        FcList* keys = fgSortedKeys(g->c, g->externDecls, false);
        fcBufStr(&sb, "#ifdef __APPLE__\n");
        fcBufStr(&sb, "#define FIRE_SYM(n) \"_\" n\n");
        fcBufStr(&sb, "#else\n");
        fcBufStr(&sb, "#define FIRE_SYM(n) n\n");
        fcBufStr(&sb, "#endif\n");
        for (i = 0; i < keys->count; i++) fcBufFmt(&sb, "%s\n", (const char*)fcDictGet(g->externDecls, keys->items[i]));
    }
    if (g->usesInlineC) {
        if (marks) fcBufFmt(&sb, "%s\n", ProgramSplitMarker(g, "atomic"));
        fcBufStr(&sb, InlineCFunctions(g));
        if (marks) fcBufFmt(&sb, "%s\n", ProgramSplitMarker(g, "atomic-end"));
    }
    fcBufStr(&sb, "\n");
    for (i = 0; i < g->strings->count; i++) {
        const char* text = (const char*)g->strings->items[i];
        FcU16 u = {0};
        bool plain = true;
        int n;
        fcU16FromFc8(text, &u);
        for (n = 0; n < u.len; n++) if (u.p[n] < 0x20 || u.p[n] >= 0x7F) plain = false;
        if (strstr(text, "*/") || strstr(text, "/*")) plain = false;
        fcBufFmt(&sb, "static const fire_char16 %sK%d_d[] = {", Px, i);
        for (n = 0; n < u.len; n++) fcBufFmt(&sb, "%d, ", u.p[n]);
        fcBufStr(&sb, "0};");
        if (plain) fcBufFmt(&sb, "   /* %s */", CString(g, text));
        fcBufChar(&sb, '\n');
        fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static const Str %sK%d = {{IMMORTAL, R_Str}, %d, %sK%d_d};\n", Px, i, u.len, Px, i);
        fcU16Free(&u);
    }
    if (g->strings->count > 0) fcBufStr(&sb, "\n");
    if (fcDictCount(g->globals) > 0) {
        int n, *items = fgISetItems(g->c, g->globals, true, &n);
        fcBufStr(&sb, "static Value ");
        for (i = 0; i < n; i++) fcBufFmt(&sb, "%sG%d = FIRE_UNDEF_INIT", i ? ", " : "", items[i]);
        fcBufStr(&sb, ";\n\n");
    }
    {
        FcList* byIndex = fgByValue(g, g->staticFields);
        for (i = 0; i < byIndex->count; i++) {
            FcDictEntry* e = (FcDictEntry*)byIndex->items[i];
            FcList* parts = fgSplit(g->c, (const char*)e->key, '\x01');
            const char* cls = (const char*)parts->items[0];
            const char* field = (const char*)parts->items[1];
            if (!Modular) fcBufFmt(&sb, "static Value SF%d = FIRE_UNDEF_INIT; // %s.%s\n", FC_VALI(e->value) - 1, cls, field);
            else if (IsOwnClass(g, FindClass(g, cls))) fcBufFmt(&sb, "Value %s = FIRE_UNDEF_INIT; // %s.%s\n", StaticFieldVar(g, cls, field), cls, field);
            else fcBufFmt(&sb, "extern Value %s; // %s.%s\n", StaticFieldVar(g, cls, field), cls, field);
        }
        if (byIndex->count > 0) fcBufStr(&sb, "\n");
        if (g->usesThreads && !Modular) {
            int n, *items, locks = fcDictCount(g->pkgLocks) > 1 ? fcDictCount(g->pkgLocks) : 1;
            fcBufStr(&sb, "#ifdef FIRE_NO_GIL\n");
            fcBufFmt(&sb, "static fire_mutex fire_pkgM[%d];\n", locks);
            fcBufFmt(&sb, "static void fire_pkgLocksInit(void) { int i; for (i = 0; i < %d; i++) fire_mutex_init(&fire_pkgM[i]); }\n", locks);
            fcBufStr(&sb, "#endif\n");
            fcBufStr(&sb, "#ifdef FIRE_STR_CROSS\n");
            fcBufStr(&sb, "static void fire_shareGlobals(void) {\n");
            items = fgISetItems(g->c, g->globals, true, &n);
            for (k = 0; k < n; k++) fcBufFmt(&sb, "    refShare(G%d);\n", items[k]);
            for (k = 0; k < byIndex->count; k++) fcBufFmt(&sb, "    refShare(SF%d);\n", FC_VALI(((FcDictEntry*)byIndex->items[k])->value) - 1);
            fcBufStr(&sb, "}\n");
            fcBufStr(&sb, "#endif\n");
        }
        if (g->usesThreads && Modular) {
            int n, *items;
            fcBufStr(&sb, ModulePkgLocks(g));
            /* (the first `fire`: the globals and the static fields of every module - the program knows them all, their modules' needs name them) */
            fcBufStr(&sb, "#ifdef FIRE_STR_CROSS\n");
            fcBufStr(&sb, "static void fire_shareGlobals(void) {\n");
            items = fgISetItems(g->c, g->globals, true, &n);
            for (k = 0; k < n; k++) fcBufFmt(&sb, "    refShare(G%d);\n", items[k]);
            for (k = 0; k < byIndex->count; k++) {
                FcList* parts = fgSplit(g->c, (const char*)((FcDictEntry*)byIndex->items[k])->key, '\x01');
                fcBufFmt(&sb, "    refShare(%s);\n", StaticFieldVar(g, (const char*)parts->items[0], (const char*)parts->items[1]));
            }
            fcBufStr(&sb, "}\n");
            fcBufStr(&sb, "#endif\n");
        }
    }
    fcBufStr(&sb, ModuleTables(g));
    {
        /* SortedSet<(int, int)> of the unit factor caches */
        FcList* keys = fgKeys(g->c, g->unitFactorCaches);
        int n = keys->count;
        int* pairs = (int*)fcAlloc(g->c, sizeof(int) * 2 * (size_t)(n + 1));
        for (i = 0; i < n; i++) sscanf((const char*)keys->items[i], "%d,%d", &pairs[2 * i], &pairs[2 * i + 1]);
        for (i = 1; i < n; i++)
            for (k = i; k > 0 && (pairs[2 * (k - 1)] > pairs[2 * k] || (pairs[2 * (k - 1)] == pairs[2 * k] && pairs[2 * (k - 1) + 1] > pairs[2 * k + 1])); k--) {
                int a = pairs[2 * k], b = pairs[2 * k + 1];
                pairs[2 * k] = pairs[2 * (k - 1)]; pairs[2 * k + 1] = pairs[2 * (k - 1) + 1];
                pairs[2 * (k - 1)] = a; pairs[2 * (k - 1) + 1] = b;
            }
        for (i = 0; i < n; i++) fcBufFmt(&sb, "static double %skUF_%d_%d = 0;   // the factor between unit %d and unit %d, looked up once\n", Px, pairs[2 * i], pairs[2 * i + 1], pairs[2 * i], pairs[2 * i + 1]);
    }
    /* Prototypes of everything, then the helpers (they call functions), then the functions (they call helpers). */
    FunctionPrototypes(g, &sb);
    /* (modules, ROADMAP 9 B2: the helpers serve the program's own classes; the others go through the tables of the runtime) */
    if (Modular) fcBufStr(&sb, ModularHelpers(g));
    else {
        FcList* fieldNames = fgSortedKeys(g->c, g->fieldNames, true);
        FcList* fieldHelperCode = fcListNew(g->c);
        FcList* dispatcherCode = fcListNew(g->c);
        FcList* selectorCode = fcListNew(g->c);
        FcList* dispatchers;
        FcList* idispatchers;
        FcList* isOfTypes;
        FcList* hasMembers;
        FcList* operators;
        const char* targTables;
        for (i = 0; i < fieldNames->count; i++) fcListAdd(g->c, fieldHelperCode, (void*)FieldHelpers(g, (const char*)fieldNames->items[i]));
        for (i = 0; i < fieldNames->count; i++)
            for (k = 0; k < g->classList->count; k++) {
                const char* requiredType = fgFieldRequiredType(((FgClassInfo*)g->classList->items[k])->rc, (const char*)fieldNames->items[i]);
                if (requiredType) { FcList* names = IsOfNames(g, requiredType); int n; for (n = 0; n < names->count; n++) fcSetAdd(g->isOfTypes, names->items[n]); }
            }
        dispatchers = fgSortedNameArgc(g, g->dispatchers);
        for (i = 0; i < dispatchers->count; i++) { FgNameArgc* d = (FgNameArgc*)dispatchers->items[i]; fcListAdd(g->c, dispatcherCode, (void*)Dispatcher(g, d->name, d->argc)); }
        idispatchers = fgSortedIDispatch(g);
        for (i = 0; i < idispatchers->count; i++) { FgIDisp* x = (FgIDisp*)idispatchers->items[i]; fcListAdd(g->c, dispatcherCode, (void*)InterfaceDispatcher(g, x->iface, x->name, x->argc)); }
        FC_DICT_EACH(g->overloadSelectors, e) fcListAdd(g->c, selectorCode, (void*)SelectorFunction(g, (FgSelector*)e->value));
        targTables = TArgTables(g, true);
        isOfTypes = fgSortedKeys(g->c, g->isOfTypes, false);
        for (i = 0; i < isOfTypes->count; i++) fcBufFmt(&sb, "FIRE_MAYBE_UNUSED %sbool isof_%s(Value v);\n", HelperStorage, Mangle(g, (const char*)isOfTypes->items[i]));
        hasMembers = fgSortedKeys(g->c, g->hasMembers, true);
        for (i = 0; i < hasMembers->count; i++) {
            const char* memberName = (const char*)hasMembers->items[i];
            FcList* ids = fcListNew(g->c);
            for (k = 0; k < g->classList->count; k++) { FgClassInfo* c = (FgClassInfo*)g->classList->items[k]; if (ClassHasMember(g, c->rc, memberName)) fcListAdd(g->c, ids, FC_IVAL(c->id)); }
            fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static bool hasm_%s(Value v) {\n", Mangle(g, memberName));
            fcBufFmt(&sb, "    if (v.kind != K_Class) return fire_builtinHas(v, %s);\n", CString(g, memberName));
            fcBufStr(&sb, "    if (FIRE_UNLIKELY(!leafAlive(v))) return false;\n");
            if (streq0(memberName, "TakeTo") || streq0(memberName, "TakeLocal") || streq0(memberName, "TakeUpwards") || streq0(memberName, "TakeGlobal")) fcBufStr(&sb, "    return true;\n");
            else if (ids->count == 0) fcBufStr(&sb, "    return false;\n");
            else {
                fcBufStr(&sb, "    switch (asObj(v)->cls) { ");
                for (k = 0; k < ids->count; k++) fcBufFmt(&sb, "case %d: ", FC_VALI(ids->items[k]));
                fcBufStr(&sb, "return true; default: return false; }\n");
            }
            fcBufStr(&sb, "}\n");
        }
        fcBufStr(&sb, targTables);
        for (i = 0; i < fieldNames->count; i++) {
            const char* name = (const char*)fieldNames->items[i];
            const char* lp = fcFmt(g->c, "%s%s", FieldHasList(g, name) ? ", OwnList* list" : "", FieldHasCaller(g, name) ? ", uint32_t caller" : "");
            fcBufFmt(&sb, "FIRE_MAYBE_UNUSED %sValue gf_%s(Value v%s);\n", HelperInline, Mangle(g, name), lp);
            fcBufFmt(&sb, "FIRE_MAYBE_UNUSED %svoid sf_%s(Value v, Value x%s);\n", HelperInline, Mangle(g, name), lp);
            if (fgFieldAddressTaken(g, name)) fcBufFmt(&sb, "FIRE_MAYBE_UNUSED %sValue fp_%s(Value v);\n", HelperInline, Mangle(g, name));
        }
        for (i = 0; i < dispatchers->count; i++) {
            FgNameArgc* d = (FgNameArgc*)dispatchers->items[i];
            fcBufFmt(&sb, "%s;\n", DispatcherSignature(g, d->name, d->argc, "call_"));
            if (WrapDispatchers) fcBufFmt(&sb, "%s;\n", DispatcherSignature(g, d->name, d->argc, "direct_"));
            if (WrapDispatchers) fcBufFmt(&sb, "static Value msg_%s_%d(Value self, const Value* a, OwnList* list);\n", Mangle(g, d->name), d->argc);
        }
        for (i = 0; i < idispatchers->count; i++) { FgIDisp* x = (FgIDisp*)idispatchers->items[i]; fcBufFmt(&sb, "%s;\n", InterfaceDispatcherSignature(g, x->iface, x->name, x->argc)); }
        operators = fgSortedKeys(g->c, g->operators, false);
        for (i = 0; i < operators->count; i++) fcBufFmt(&sb, "%sValue %s(Value a, Value b, OwnList* list);\n", HelperStorage, OperatorWrapper(g, (const char*)operators->items[i]));
        fcBufFmt(&sb, "FIRE_MAYBE_UNUSED %sValue aget_g(Value a, Value i, OwnList* list);\n", HelperInline);
        fcBufFmt(&sb, "FIRE_MAYBE_UNUSED %sValue aset_g(Value a, Value i, Value v, OwnList* list);\n", HelperInline);
        fcBufStr(&sb, "\n");

        FC_DICT_EACH(g->overloadSelectors, e) fcBufFmt(&sb, "%s;\n", SelectorSignature(g, (FgSelector*)e->value));
        fcBufStr(&sb, OverloadClassFunctions(g, true));
        fcBufStr(&sb, "\n");

        for (i = 0; i < fieldHelperCode->count; i++) fcBufFmt(&sb, "%s\n", PointerArgs(g, (const char*)fieldHelperCode->items[i]));
        for (i = 0; i < dispatcherCode->count; i++) fcBufFmt(&sb, "%s\n", PointerArgs(g, (const char*)dispatcherCode->items[i]));
        for (i = 0; i < selectorCode->count; i++) fcBufFmt(&sb, "%s\n", PointerArgs(g, (const char*)selectorCode->items[i]));
        if (g->usesTakeEnumerate) fcBufStr(&sb, EnumerateItemsFunction(g));
        for (i = 0; i < operators->count; i++) {
            const char* method = (const char*)operators->items[i];
            FcList* ids = fcListNew(g->c);
            for (k = 0; k < g->classList->count; k++) { FgClassInfo* c = (FgClassInfo*)g->classList->items[k]; if (FG_FIND(c->rc, method, 1).proto) fcListAdd(g->c, ids, FC_IVAL(c->id)); }
            fcBufFmt(&sb, "%sValue %s(Value a, Value b, OwnList* list) {\n", HelperStorage, OperatorWrapper(g, method));
            if (ids->count == 0) fcBufStr(&sb, "    (void)list;   // (a library asked for it: no class of the program overloads it)\n");
            if (ids->count > 0) {
                fcBufStr(&sb, "    if (a.kind == K_Class) {\n");
                fcBufStr(&sb, "        switch (asObj(a)->cls) {\n");
                fcBufStr(&sb, "            ");
                for (k = 0; k < ids->count; k++) fcBufFmt(&sb, "case %d: ", FC_VALI(ids->items[k]));
                fcBufFmt(&sb, "{ Value r = call_%s_1(a, b%s); adopt(r, list); return r; }\n", Mangle(g, method), DispatchTail(g, method, 1, "list", "0xFFFFFFFFu"));
                fcBufStr(&sb, "            default: break;\n");
                fcBufStr(&sb, "        }\n");
                fcBufStr(&sb, "    }\n");
            }
            fcBufFmt(&sb, "    return %s;\n", OperatorFallback(method));
            fcBufStr(&sb, "}\n");
        }
        for (i = 0; i < isOfTypes->count; i++) fcBufStr(&sb, IsOfFunction(g, (const char*)isOfTypes->items[i]));
        g->isOfWritten = fcDictNew(g->c, FC_KEY_STR);
        for (i = 0; i < isOfTypes->count; i++) fcSetAdd(g->isOfWritten, isOfTypes->items[i]);
    }
    if (g->usesReflection) {
        FC_DICT_EACH(g->selectorPaths, e) {
            const FgSelectorPath* sp = (const FgSelectorPath*)e->value;
            FcList* texts = fcListNew(g->c);
            for (k = 0; k < sp->path->count; k++) fcListAdd(g->c, texts, (void*)CString(g, (const char*)sp->path->items[k]));
            fcBufFmt(&sb, "static const char* const %s[] = {%s};\n", sp->name, fgJoin(g->c, texts, ", "));
        }
        fcBufFmt(&sb, "%s\n", ShareReflection(g, ReflectionCode(g)));
    }
    if (!Modular) fcBufFmt(&sb, "%s\n", IndexHelpers(g));
    if (g->usesJson) fcBufFmt(&sb, "%s\n", JsonCode(g));
    fcBufFmt(&sb, "%s\n", RuntimeHooks(g));
    if (g->usesLibCall || Modular) fcBufStr(&sb, LibraryThunks(g));

    splitKeys = fcListNew(g->c);
    for (i = 0; i < g->funcs->count; i++) {
        FgFunc* f = (FgFunc*)g->funcs->items[i];
        if (marks) { fcBufFmt(&sb, "%s\n", ProgramSplitMarker(g, fcFmt(g->c, "func %d", splitKeys->count))); fcListAdd(g->c, splitKeys, SplitKeyOf(g, f)); }
        fcBufFmt(&sb, "%s\n", f->code);
    }
    if (marks) { fcBufFmt(&sb, "%s\n", ProgramSplitMarker(g, fcFmt(g->c, "func %d", splitKeys->count))); fcListAdd(g->c, splitKeys, SplitKeyOf(g, main)); }
    fcBufFmt(&sb, "%s\n", main->code);
    if (marks) fcBufFmt(&sb, "%s\n", ProgramSplitMarker(g, "tail"));
    /* the enums of the program: their names and members (the text of a value, `is of`) */
    if (g->enumList->count > 0) {
        FcList* rows = fcListNew(g->c);
        for (i = 0; i < g->enumList->count; i++) {
            const FcEnumType* e = (const FcEnumType*)g->enumList->items[i];
            FcList* members = fcListNew(g->c);
            for (k = 0; k < e->names->count; k++) {
                int64_t v = *(int64_t*)e->values->items[k];
                fcListAdd(g->c, members, (void*)fcFmt(g->c, "{%s, %s}", CString(g, (const char*)e->names->items[k]), v == INT64_MIN ? "INT64_MIN" : fcFmt(g->c, "%" PRId64 "LL", v)));
            }
            if (members->count == 0) fcListAdd(g->c, members, (void*)"{\"\", 0}");
            fcBufFmt(&sb, "static const FireEnumMember kEnumM%d[] = {%s};\n", i, fgJoin(g->c, members, ", "));
            fcListAdd(g->c, rows, (void*)fcFmt(g->c, "{%s, %d, kEnumM%d, %d}", CString(g, e->name), e->isFlags ? 1 : 0, i, e->names->count));
        }
        fcBufFmt(&sb, "static const FireEnum kEnums[] = {%s};\n", fgJoin(g->c, rows, ", "));
        fcBufFmt(&g->initCode, "    g_enums = kEnums; g_enumCount = %d;\n", g->enumList->count);
    }
    /* the arrays of a class, a string or an enum (ROADMAP 19): what their elements must be */
    if (fcDictCount(g->arrClasses) > 0) {
        fcBufStr(&sb, ArrClassTable(g));
        fcBufFmt(&g->initCode, "    g_arrClasses = %skArrClasses;\n", Px);
    }
    if (DebugBuild) fcBufStr(&sb, DebugProgramTables(g));
    fcBufStr(&sb, SharedLibraryChecks(g));
    fcBufStr(&sb, HostTable(g, fcStrN(g->c, sb.p, sb.len)));
    fcBufStr(&sb, PluginHostCode(g));
    fcBufStr(&sb, "static void fire_init(void) {\n");
    fcBufStr(&sb, SharedLibraryCheckCalls(g));   /* (linkage "shared": the libraries first) */
    if (g->usesThreads) fcBufStr(&sb, "    fire_threadsInit();   // the locks of the threads, before anything else\n");
    fcBufPut(&sb, g->initCode.p ? g->initCode.p : "", g->initCode.len);
    fcBufStr(&sb, "}\n");
    if (g->target->entryKind == 1 && streq0(g->target->platform, "esp32")) {
        fcBufFmt(&sb, "#ifndef FIRE_MAIN_STACK_BYTES\n#define FIRE_MAIN_STACK_BYTES %d\n#endif\n", DebugBuild ? 32768 : 16384);
        if (DebugBuild)
            fcBufFmt(&sb, "static void fire_mainTask(void* arg) {\n    (void)arg;\n    fire_init();\n    fire_dbgStart(&kDbgProgram);   // the debugger on the console UART: hello, then at most 3 s for its first command\n    fire_main();\n    fflush(stdout);\n%s    vTaskDelete(NULL);\n}\n",
                g->usesThreads ? "    fire_dbgExited(mainExitCode());\n" : "    fire_dbgExited(g_unwind.active ? 1 : 0);\n");
        else fcBufStr(&sb, "static void fire_mainTask(void* arg) {\n    (void)arg;\n    fire_init();\n    fire_main();\n    fflush(stdout);\n    vTaskDelete(NULL);\n}\n");
        fcBufFmt(&sb, "void %s(void) {\n", g->target->entryName);
        fcBufStr(&sb, "    if (xTaskCreate(fire_mainTask, \"fire_main\", FIRE_MAIN_STACK_BYTES, NULL, 1, NULL) != pdPASS) { fire_init(); fire_main(); fflush(stdout); }   // (no memory for the task: on the main task)\n");
        fcBufStr(&sb, "}\n");
    }
    else if (g->target->entryKind == 1) {
        fcBufFmt(&sb, "void %s(void) {\n", g->target->entryName);
        fcBufStr(&sb, "    fire_init();\n");
        if (DebugBuild) fcBufStr(&sb, "    fire_dbgStart(&kDbgProgram);   // the debugger (FreeRTOS: framed over stdio)\n");
        fcBufStr(&sb, "    fire_main();\n");
        fcBufStr(&sb, "    fflush(stdout);\n");
        if (DebugBuild) fcBufStr(&sb, g->usesThreads ? "    fire_dbgExited(mainExitCode());\n" : "    fire_dbgExited(g_unwind.active ? 1 : 0);\n");
        fcBufStr(&sb, "}\n");
    }
    else if (DebugBuild) {
        fcBufFmt(&sb, "int %s(%s) {\n", g->target->entryName, MainParams(g));
        fcBufStr(&sb, "    int code_;\n");
        fcBufFmt(&sb, "    %s   // (Environment.Args, ProgramPath)\n", MainSetArgs(g));
        fcBufStr(&sb, "    fire_init();\n");
        fcBufStr(&sb, "    fire_dbgStart(&kDbgProgram);   // FIRE_DEBUG: connect to the debugger and wait for it\n");
        fcBufStr(&sb, "    fire_main();\n");
        fcBufStr(&sb, "    fflush(stdout);\n");
        fcBufStr(&sb, g->usesThreads ? "    code_ = mainExitCode();   // `terminate(n)` gives the exit code\n" : "    code_ = g_unwind.active ? 1 : 0;   // an exception that nothing caught\n");
        fcBufStr(&sb, "    fire_dbgExited(code_);\n");
        fcBufStr(&sb, "    return code_;\n");
        fcBufStr(&sb, "}\n");
    }
    else {
        fcBufFmt(&sb, "int %s(%s) {\n", g->target->entryName, MainParams(g));
        fcBufFmt(&sb, "    %s   // (Environment.Args, ProgramPath)\n", MainSetArgs(g));
        fcBufStr(&sb, "    fire_init();\n    fire_main();\n");
        fcBufStr(&sb, "    fflush(stdout);\n");
        fcBufStr(&sb, g->usesThreads ? "    return mainExitCode();   // `terminate(n)` gives the exit code\n" : g->usesExceptions ? "    return g_unwind.active ? 1 : 0;   // an exception that nothing caught\n" : "    return 0;\n");
        fcBufStr(&sb, "}\n");
    }
    programText = fcBufDone(g->c, &sb);
    if (PointerCallsOn) programText = PointerCallsPass(g, programText);
    /* (a program that loads plugins: the classes from its first own id up to this are its own - a plugin's come after them) */
    if (g->openWorld) programText = InsertAfterRuntime(g, programText, fcFmt(g->c, "#define FIRE_PROG_END %du   // the end of the program's class ids (the classes of plugins come after)\n", g->nextClassId));
    if (marks) {
        /* `auto`: a small program stays one file - the marks go, and the text is the one of a build without them */
        if (g->splitMode == 2 && fc8Units(programText) < 2 * 1024 * 1024) programText = WithoutSplitMarks(g, programText);
        else {
            g->splitUnits = ProgramSplitSplit(g, programText, splitKeys);
            return ((FgSplitUnit*)g->splitUnits->items[1])->text;
        }
    }
    return ResolveLineResets(g, programText, "fire_program.c");
}

/* The first lines of every file of the program (the configuration of the runtime: the target, the switches, the mode). */
static const char* HeadDefines(FgGen* g) {
    FcBuf sb = {0};
    int i;
    fcBufStr(&sb, "// Generated by fire.Native from the bytecode - do not edit.\n");
    fcBufFmt(&sb, "// target: %s\n", g->target->name);
    fcBufFmt(&sb, "#define FIRE_TARGET \"%s\"\n", g->target->name);
    {
        char* macro = (char*)fcStr(g->c, fgUpper(g->c, g->target->name));
        char* q;
        for (q = macro; *q; q++) if (!((*q >= 'A' && *q <= 'Z') || (*q >= '0' && *q <= '9'))) *q = '_';
        fcBufFmt(&sb, "#define FIRE_TARGET_%s 1\n", macro);
    }
    fcBufFmt(&sb, "#define FIRE_HAL_%s 1\n", fgUpper(g->c, g->target->halPackage));
    fcBufFmt(&sb, "#define FIRE_DEFAULT_STACK_BYTES %d\n", g->target->defaultStackBytes);
    if (g->collectStats && !Modular) fcBufStr(&sb, "#define FIRE_HEAP_STATS 1\n");
    for (i = 0; i < g->target->defineCount; i++) {
        const char* define = g->target->defines[i];
        int eq = fgIndexOf(define, '=');
        if (eq < 0) fcBufFmt(&sb, "#define %s\n", define);
        else fcBufFmt(&sb, "#define %s %s\n", fcStrN(g->c, define, (size_t)eq), define + eq + 1);
    }
    if (g->usesExceptions) fcBufStr(&sb, "#define FIRE_EXCEPTIONS 1\n");
    if (Modular) fcBufStr(&sb, "#define FIRE_MODULES 1   // modules (ROADMAP 9): the members of the classes of other modules by name, through the class descriptors\n");
    /* (the modules in files of their own, linkage static/shared: what the platform keeps once - the key of the thread state of TinyCC, ... - is one, in the
     * program's file; fire_rt.h FIRE_ONCE) */
    if (Modular && g->separate && !g->dll) fcBufStr(&sb, "#define FIRE_SPLIT 1   // the modules are units of their own: what the platform keeps once lives in the program's file (fire_rt.h FIRE_ONCE)\n");
    if (DebugBuild) fcBufStr(&sb, "#define FIRE_DEBUG 1   // a debug build: the shadow stack and the hooks of the debugger (fire_debug.h)\n");
    if (DebugBuild && DebugAgent) fcBufStr(&sb, "#define FIRE_DEBUG_AGENT 1   // the debug agent (docs/spec/NATIVE_DEBUG_PROTOCOL.md; the desktop: FIRE_DEBUG=tcp:127.0.0.1:PORT;token=T)\n");
    if (DebugBuild && DebugAgent) {
        const char* transport = streq0(g->target->platform, "esp32") ? "FIRE_DEBUG_UART" : streq0(g->target->platform, "freertos") ? "FIRE_DEBUG_STDIO" : NULL;
        if (transport) fcBufFmt(&sb, "#define %s 1   // the agent talks framed over %s (docs/spec/NATIVE_DEBUG_PROTOCOL.md, Transport)\n", transport, fcEndsWith(transport, "UART") ? "the console UART" : "stdio");
    }
    if (g->usesThreads) fcBufStr(&sb, "#define FIRE_THREADS 1\n");
    if (g->usesReflection) fcBufStr(&sb, "#define FIRE_REFLECTION 1\n");
    if (g->usesElementPointers && g->program->mode != 2) fcBufStr(&sb, "#define FIRE_PTRBOUNDS 1 // pointers into arrays and buffers (`ref a[i]`, `p + n`) know the bounds\n");
    if (g->usesElementPointers) fcBufStr(&sb, "#define FIRE_ELEMPTRS 1 // pointers into arrays find their array (a typed one converts what is written through them)\n");
    if (g->program->mode == 2) fcBufStr(&sb, "#define FIRE_UNCHECKED 1 // #performance: destroyed arrays and buffers are not detected\n");
    if (g->program->optimize == 1) fcBufStr(&sb, "#define FIRE_OPTIMIZE_SIZE 1 // optimised for size (--optimize, the project, the target): the runtime embeds nothing beyond what the compiler chooses\n");
    if (g->program->floatWidth == 32) fcBufStr(&sb, "#define FIRE_FLOAT32 1 // #floatwidth 32: float is a 32-bit float, like in the VM\n");
    if (g->program->intWidth == 16) fcBufStr(&sb, "#define FIRE_INT16 1 // #intwidth 16: every int is cut to 16 bits, like in the VM (its storage stays 32 bits)\n");
    if (g->program->intWidth == 32) fcBufStr(&sb, "#define FIRE_INT32 1 // #intwidth 32: int is a 32-bit int, like in the VM (with FIRE_FLOAT32 and 32-bit pointers a value takes 8 bytes)\n");
    fcBufFmt(&sb, "#define FIRE_WORD_BITS %d // `word`: the machine word of the target (SPEC 8.10)\n", g->program->wordWidth);
    return fcBufDone(g->c, &sb);
}

/* The head of the file of a library module (linkage "static"/"shared", a plugin): the configuration of the runtime, without its state. */
static const char* LibraryHeadOf(FgGen* g, const char* prelude) {
    FcBuf head = {0};
    fcBufStr(&head, prelude);
    fcBufFmt(&head, "#define FIRE_PLATFORM_HEADER \"platform/%s/fire_platform.h\"\n", g->target->platform);
    if (g->dll) fcBufStr(&head, DllHead(g));
    fcBufStr(&head, "#include \"fire_rt.h\"\n");
    fcBufStr(&head, "#if defined(__GNUC__) || defined(__clang__)\n#pragma GCC diagnostic ignored \"-Wunused-but-set-variable\"\n#pragma GCC diagnostic ignored \"-Wunused-function\"\n#endif\n");
    if (g->usesJson) fcBufStr(&head, "#include \"fire_json.h\"\n");
    return fcBufDone(g->c, &head);
}

/* The lines of the head after the units: their dimensions, the headers of the target, DLLs. */
static const char* HeadTail(FgGen* g, int dimCount) {
    FcBuf sb = {0};
    int i;
    fcBufFmt(&sb, "#define FIRE_NDIMS %d\n", g->separate ? FG_SEPARATE_DIMS : dimCount > 1 ? dimCount : 1);
    for (i = 0; i < g->target->includeCount; i++) fcBufFmt(&sb, "#include <%s>\n", g->target->includes[i]);
    if (g->dll) fcBufStr(&sb, "#define FIRE_DLL 1   // linkage \"shared\" on Windows: the runtime and the modules are DLLs (fire_rt.h)\n");
    return fcBufDone(g->c, &sb);
}

static const char* DefaultArgsPre(FgGen* g, FcProto* proto, int argc, const char* self, const char* list, const char** args);
static FcList* fgClassListCopy(FgGen* g) { return fcListCopy(g->c, g->classList); }

/* Pulls in what dispatching needs: the method of every class for every called name, destructors, ToString, the index methods, and the enumerator
 * class that `foreach` over an array creates. */
static void RegisterTargets(FgGen* g) {
    FcList* classes;
    int i, k;
    if (LibraryRole) { RegisterServedTargets(g); return; }   /* (a library module keeps its own members; its dispatchers pull in those of the classes of other modules it serves) */
    if (g->classList->count > 0 || fcDictCount(g->dispatchers) > 0) {
        classes = fgClassListCopy(g);
        for (i = 0; i < classes->count; i++) {
            FcProto* toString = FG_FIND(((FgClassInfo*)classes->items[i])->rc, "ToString", 0).proto;
            if (toString && toString->paramCount == 0) GetFunc(g, toString, FK_Method, 0);
        }
    }
    if (g->usesExceptions && fcDictHas(g->program->program->classes, "IndexOutOfBoundsException")) {
        static const char* const extraErrors[] = { "ArgumentException", "OverflowException", "MissingMemberException" };
        FgClassInfo* c;
        FcProto* ctor;
        size_t x;
        c = RegisterClass(g, "IndexOutOfBoundsException");
        if ((ctor = fgFindConstructor(c->rc, 3)) && ctor->paramCount == 3) GetFunc(g, ctor, FK_Ctor, 0);
        c = RegisterClass(g, "DivideByZeroException");
        if ((ctor = fgFindConstructor(c->rc, 1)) && ctor->paramCount == 1) GetFunc(g, ctor, FK_Ctor, 0);
        c = RegisterClass(g, "DestroyedException");
        if ((ctor = fgFindConstructor(c->rc, 1)) && ctor->paramCount == 1) GetFunc(g, ctor, FK_Ctor, 0);
        c = RegisterClass(g, "UnitMismatchException");
        if ((ctor = fgFindConstructor(c->rc, 3)) && ctor->paramCount == 3) GetFunc(g, ctor, FK_Ctor, 0);
        if (g->usesReflection && fcDictHas(g->program->program->classes, "ReflectionException")) {
            c = RegisterClass(g, "ReflectionException");
            if ((ctor = fgFindConstructor(c->rc, 1)) && ctor->paramCount == 1) GetFunc(g, ctor, FK_Ctor, 0);
        }
        c = RegisterClass(g, "AccessDeniedException");
        if ((ctor = fgFindConstructor(c->rc, 1)) && ctor->paramCount == 1) GetFunc(g, ctor, FK_Ctor, 0);
        for (x = 0; x < sizeof extraErrors / sizeof extraErrors[0]; x++) if (!fcListHasStr(g->errorClasses, extraErrors[x])) fcListAdd(g->c, g->errorClasses, (void*)extraErrors[x]);
        if (g->usesThreads) {
            if (!fcListHasStr(g->errorClasses, "CancelledException")) fcListAdd(g->c, g->errorClasses, (void*)"CancelledException");
            if (!fcListHasStr(g->errorClasses, "TimeoutException")) fcListAdd(g->c, g->errorClasses, (void*)"TimeoutException");
        }
        /* (a program that loads plugins: `fireError("X", ...)` of a plugin's code may name any exception class of the libraries) */
        if (g->openWorld) {
            FcList* byName = fgClassesByName(g);
            for (i = 0; i < byName->count; i++) {
                FcRtClass* rc = (FcRtClass*)byName->items[i];
                FcProto* one;
                if (!ModuleOf(g, rc) || !ClassIs(rc, "Exception")) continue;
                if ((one = fgFindConstructor(rc, 1)) && one->paramCount == 1 && !fcListHasStr(g->errorClasses, rc->name)) fcListAdd(g->c, g->errorClasses, (void*)rc->name);
            }
        }
        for (i = 0; i < g->errorClasses->count; i++) {
            const char* name = (const char*)g->errorClasses->items[i];
            if (!fcDictHas(g->program->program->classes, name)) continue;
            c = RegisterClass(g, name);
            if ((ctor = fgFindConstructor(c->rc, 1)) && ctor->paramCount == 1) GetFunc(g, ctor, FK_Ctor, 0);
        }
    }
    {
        FcList* fields = Modular ? fcListNew(g->c) : fgSortedKeys(g->c, g->fieldNames, true);
        for (i = 0; i < fields->count; i++) {
            const char* field = (const char*)fields->items[i];
            classes = fgClassListCopy(g);
            for (k = 0; k < classes->count; k++) {
                FgClassInfo* cls = (FgClassInfo*)classes->items[k];
                const char* requiredUnit;
                if (fgHasField(g, cls->rc, field) && (requiredUnit = fgFieldRequiredUnit(cls->rc, field)) != NULL) {
                    UnitId(g, DemandUnit(g, requiredUnit));
                    Constant(g, fcMakeString(g->c, UnitDemandDisplay(g, requiredUnit)));
                }
            }
        }
        for (i = 0; i < fields->count; i++) {
            const char* field = (const char*)fields->items[i];
            classes = fgClassListCopy(g);
            for (k = 0; k < classes->count; k++) {
                FgClassInfo* cls = (FgClassInfo*)classes->items[k];
                FcProto* getter;
                FcProto* setter;
                if (fgHasField(g, cls->rc, field)) continue;
                getter = FG_FIND(cls->rc, fcFmt(g->c, "get_%s", field), 0).proto;
                if (getter && !getter->isStatic) GetFunc(g, getter, FK_Method, 0);
                setter = FG_FIND(cls->rc, fcFmt(g->c, "set_%s", field), 1).proto;
                if (setter && !setter->isStatic) GetFunc(g, setter, FK_Method, 0);
            }
        }
    }
    if (g->usesTakeEnumerate) {
        bool any = false;
        for (i = 0; i < g->classList->count; i++) if (ClassIs(((FgClassInfo*)g->classList->items[i])->rc, "IEnumerable")) any = true;
        if (any) { fgAddNameArgc(g, g->dispatchers, "GetEnumerator", 0); fgAddNameArgc(g, g->dispatchers, "MoveNext", 0); fgAddNameArgc(g, g->dispatchers, "GetCurrent", 0); }
    }
    if (AnyClassHasMethod(g, "GetIndex", 1)) fgAddNameArgc(g, g->dispatchers, "GetIndex", 1);
    if ((fcDictHas(g->classes, "Channel") || fcDictHas(g->classes, "ChannelEnd")) && AnyClassHasMethod(g, "__End", 0)) fgAddNameArgc(g, g->dispatchers, "__End", 0);
    if (AnyClassHasMethod(g, "SetIndex", 2)) fgAddNameArgc(g, g->dispatchers, "SetIndex", 2);
    if (fgHasNameArgc(g, g->dispatchers, "GetEnumerator", 0) && fcDictHas(g->program->program->classes, "ListEnumerator")) {
        FgClassInfo* enumerator = RegisterClass(g, "ListEnumerator");
        FcProto* ctor = fgFindConstructor(enumerator->rc, 2);
        if (ctor && ctor->paramCount == 2) GetFunc(g, ctor, FK_Ctor, 0);
    }
    {
        FcList* ids = fgSortedIDispatch(g);
        for (i = 0; i < ids->count; i++) {
            FgIDisp* x = (FgIDisp*)ids->items[i];
            FcList* impls = InterfaceImplementations(g, x->iface, x->name, x->argc);
            for (k = 0; impls && k < impls->count; k++) GetFunc(g, ((FgImpl*)impls->items[k])->proto, FK_Method, 0);
        }
    }
    {
        FcList* dispatchers = Modular ? fcListNew(g->c) : fgSortedNameArgc(g, g->dispatchers);
        for (i = 0; i < dispatchers->count; i++) {
            FgNameArgc* d = (FgNameArgc*)dispatchers->items[i];
            FcList* extensions;
            classes = fgClassListCopy(g);
            for (k = 0; k < classes->count; k++) {
                FgClassInfo* cls = (FgClassInfo*)classes->items[k];
                FcList* methods = MethodsFor(g, cls->rc, d->name, d->argc);
                bool overloaded = false;
                int m;
                const char* args;
                if (methods->count > 1) for (m = 0; m < methods->count; m++) if (!((FcProto*)methods->items[m])->isStatic) overloaded = true;
                for (m = 0; m < methods->count; m++) {
                    FcProto* method = (FcProto*)methods->items[m];
                    if (!method->isStatic) { GetFunc(g, method, FK_Method, 0); DefaultArgsPre(g, method, d->argc, "self", "list", &args); }
                    else if (overloaded) { GetFunc(g, method, FK_Static, 0); DefaultArgsPre(g, method, d->argc, "Undef()", "list", &args); }
                }
            }
            extensions = ExtensionMethods(g, d->name, d->argc);
            for (k = 0; k < extensions->count; k++) {
                FgExtension* x = (FgExtension*)extensions->items[k];
                FcList* methods = MethodsFor(g, x->rc, d->name, d->argc);
                int m;
                const char* args;
                for (m = 0; m < methods->count; m++) { GetFunc(g, (FcProto*)methods->items[m], FK_Method, 0); DefaultArgsPre(g, (FcProto*)methods->items[m], d->argc, "self", "list", &args); }
            }
        }
    }
    RegisterReflectionTargets(g);
    RegisterKeptTargets(g);
    RegisterJsonTargets(g);
    if (Modular) RegisterServedTargets(g);
    classes = fgClassListCopy(g);
    for (i = 0; i < classes->count; i++) {
        const FcRtClass* rc;
        for (rc = ((FgClassInfo*)classes->items[i])->rc; rc; rc = rc->base) if (rc->destructor) GetFunc(g, rc->destructor, FK_Dtor, 0);
    }
}

/* ---- Helpers generated after the fixpoint ---- */
static const char* ConstantDefault(FgGen* g, FcProto* defaultProto);
static const char* KeepArgIn(FgGen* g, const char* pre, bool calls) { return g->usesTryDepth && calls ? fcFmt(g->c, "OwnList* const ai_ = g_argIn; %sg_argIn = ai_; ", pre) : pre; }
/* DefaultArgs: the values for the parameters beyond the `argc` that a call supplies; `Pre` evaluates them in order into locals, `Args` passes them on. */
static const char* DefaultArgsPre(FgGen* g, FcProto* proto, int argc, const char* self, const char* list, const char** argsOut) {
    FcBuf pre = {0};
    FcBuf args = {0};
    bool calls = false;
    int i;
    if (argc >= proto->paramCount) { *argsOut = ""; return ""; }
    fcBufStr(&pre, ""); fcBufStr(&args, "");
    for (i = argc; i < proto->paramCount; i++) {
        FcProto* defaultProto = i < proto->paramDefaults->count ? (FcProto*)proto->paramDefaults->items[i] : NULL;
        const char* constant;
        if (!defaultProto) FG_NOTSUP("call with %d argument(s) of a function with %d parameters", argc, proto->paramCount);
        if ((constant = ConstantDefault(g, defaultProto)) != NULL) fcBufFmt(&pre, "Value dv%d = %s; ", i, constant);
        else { fcBufFmt(&pre, "Value dv%d = adoptV(%s(%s), %s); ", i, GetFunc(g, defaultProto, FK_Init, 0)->name, self, list); calls = true; }
        fcBufFmt(&args, ", dv%d", i);
    }
    *argsOut = fcBufDone(g->c, &args);
    return KeepArgIn(g, fcBufDone(g->c, &pre), calls);
}
/* A default value that is a constant: the C of the constant; NULL for any other default value. */
static const char* ConstantDefault(FgGen* g, FcProto* defaultProto) {
    FgCode code = fgDecode(g->c, defaultProto->chunk);
    const FcConst* c;
    if (code.n != 2 || code.p[0].op != OP_LoadConst || code.p[1].op != OP_Return) return NULL;
    c = fgConst(defaultProto->chunk, code.p[0].a[0]);
    return c->kind == CK_Int || c->kind == CK_Float || c->kind == CK_Bool || c->kind == CK_Undefined ? Constant(g, c) : NULL;
}
/* Does a method call of this name with this many arguments reach an implementation that fills in default values? */
static bool DispatchNeedsDefaults(FgGen* g, const char* name, int argc) {
    const char* key = fgNameArgcKey(g, name, argc);
    void* known;
    bool needs = false;
    int i, k;
    FcList* extensions;
    if (fcDictTry(g->needsDefaults, key, &known)) return FC_VALI(known) == 1;
    FC_DICT_EACH(g->program->program->classes, e) {
        FcList* methods = MethodsFor(g, (FcRtClass*)e->value, name, argc);
        for (i = 0; i < methods->count && !needs; i++) if (((FcProto*)methods->items[i])->paramCount != argc) needs = true;
        if (needs) break;
    }
    if (!needs) {
        extensions = ExtensionMethods(g, name, argc);
        for (k = 0; k < extensions->count && !needs; k++) {
            FcList* methods = MethodsFor(g, ((FgExtension*)extensions->items[k])->rc, name, argc);
            for (i = 0; i < methods->count && !needs; i++) if (((FcProto*)methods->items[i])->paramCount != argc) needs = true;
        }
    }
    fcDictPut(g->needsDefaults, key, FC_IVAL(needs ? 1 : 2));
    return needs;
}

/* The kind of a typed access of a buffer (`ReadInt16` -> 2), -1 for another name. */
static int BufferKind(const char* name, const char* prefix) {
    static const char* const kinds[] = { "Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "Float16", "Float32", "Float64" };
    size_t k;
    if (!fcStartsWith(name, prefix)) return -1;
    for (k = 0; k < sizeof kinds / sizeof kinds[0]; k++) if (streq0(name + strlen(prefix), kinds[k])) return (int)k;
    return -1;
}
/* The conversions of the base types that a method call reaches when no class defines the method (SPEC 8.10): per kind of receiver the C++ expression. */
typedef struct FgBuiltin { const char* kind; const char* expr; } FgBuiltin;
static FcList* BuiltinMethods(FgGen* g, const char* name, int argc) {
    FcList* l = fcListNew(g->c);
#define FG_BI(k, e) do { FgBuiltin* b_ = (FgBuiltin*)fcAlloc(g->c, sizeof *b_); b_->kind = (k); b_->expr = (e); fcListAdd(g->c, l, b_); } while (0)
    int rk, wk;
    if (streq0(name, "ToBytes") && argc == 0) FG_BI("K_String", "strToBytes(self, list)");
    else if (streq0(name, "ToUnicode") && argc == 1) { FG_BI("K_String", "strToUnicode(self, a0, list)"); FG_BI("K_Char", "charToUnicode(self, a0, list)"); FG_BI("K_Buffer", "retainedV(bufToUnicode(self, a0, list))"); }
    else if (streq0(name, "ToUnicode") && argc == 0) FG_BI("K_Buffer", "retainedV(bufToUnicode(self, Int(2), list))");
    else if (streq0(name, "ToByte") && argc == 0) FG_BI("K_Char", "charToByte(self)");
    else if (streq0(name, "ToChar") && argc == 0) FG_BI("K_Int", "byteToChar(self)");
    else if (streq0(name, "ToString") && argc == 0) {
        static const char* const kinds[] = { "K_Int", "K_Float", "K_Bool", "K_Char", "K_Word", "K_String", "K_Array", "K_Lambda", "K_PtrVar", "K_PtrByte", "K_PtrElem", "K_PtrField" };
        size_t k;
        FG_BI("K_Buffer", "retainedV(bufToString(self, list))");
        for (k = 0; k < sizeof kinds / sizeof kinds[0]; k++) FG_BI(kinds[k], "retainedV(toStringValue(self, list))");
    }
    else if (streq0(name, "HasFlag") && argc == 1) FG_BI("K_Int", "enumHasFlag(self, a0)");
    else if (streq0(name, "ToUnicodeChar") && argc == 0) FG_BI("K_Buffer", "bufToUnicodeChar(self, Int(2))");
    else if (streq0(name, "ToUnicodeChar") && argc == 1) FG_BI("K_Buffer", "bufToUnicodeChar(self, a0)");
    else if (streq0(name, "ToLittleEndian") && argc == 0) FG_BI("K_Buffer", "bufToEndian(self, true, list)");
    else if (streq0(name, "ToBigEndian") && argc == 0) FG_BI("K_Buffer", "bufToEndian(self, false, list)");
    else if (streq0(name, "Fill") && argc == 1) FG_BI("K_Buffer", "bufFill(self, a0, Undef(), Undef())");
    else if (streq0(name, "Fill") && argc == 3) FG_BI("K_Buffer", "bufFill(self, a0, a1, a2)");
    else if (streq0(name, "CopyTo") && argc == 2) FG_BI("K_Buffer", "bufCopyTo(self, a0, a1, Undef(), Undef())");
    else if (streq0(name, "CopyTo") && argc == 4) FG_BI("K_Buffer", "bufCopyTo(self, a0, a1, a2, a3)");
    else if (streq0(name, "Slice") && argc == 1) FG_BI("K_Buffer", "bufSlice(self, a0, Undef(), list)");
    else if (streq0(name, "Slice") && argc == 2) FG_BI("K_Buffer", "bufSlice(self, a0, a1, list)");
    else if (streq0(name, "IndexOf") && argc == 1) FG_BI("K_Buffer", "bufIndexOf(self, a0, Undef())");
    else if (streq0(name, "IndexOf") && argc == 2) FG_BI("K_Buffer", "bufIndexOf(self, a0, a1)");
    else if (streq0(name, "Compare") && argc == 1) FG_BI("K_Buffer", "bufCompare(self, a0)");
    else if ((rk = BufferKind(name, "Read")) >= 0 && (argc == 1 || argc == 2)) FG_BI("K_Buffer", fcFmt(g->c, "bufRead(self, %d, a0, %s)", rk, argc == 2 ? "a1" : "Undef()"));
    else if ((wk = BufferKind(name, "Write")) >= 0 && (argc == 2 || argc == 3)) FG_BI("K_Buffer", fcFmt(g->c, "bufWrite(self, %d, a0, a1, %s)", wk, argc == 3 ? "a2" : "Undef()"));
#undef FG_BI
    return l;
}
static int BuiltinMethodsCount(const char* name, int argc) {
    static const char* const simple[][2] = { {"ToBytes", "0"}, {"ToUnicode", "1"}, {"ToUnicode", "0"}, {"ToByte", "0"}, {"ToChar", "0"}, {"ToString", "0"}, {"HasFlag", "1"},
        {"ToUnicodeChar", "0"}, {"ToUnicodeChar", "1"}, {"ToLittleEndian", "0"}, {"ToBigEndian", "0"}, {"Fill", "1"}, {"Fill", "3"}, {"CopyTo", "2"}, {"CopyTo", "4"},
        {"Slice", "1"}, {"Slice", "2"}, {"IndexOf", "1"}, {"IndexOf", "2"}, {"Compare", "1"} };
    size_t k;
    for (k = 0; k < sizeof simple / sizeof simple[0]; k++) if (streq0(simple[k][0], name) && atoi(simple[k][1]) == argc) return 1;
    if (BufferKind(name, "Read") >= 0 && (argc == 1 || argc == 2)) return 1;
    if (BufferKind(name, "Write") >= 0 && (argc == 2 || argc == 3)) return 1;
    return 0;
}

static bool DispatchNeedsList(FgGen* g, const char* name, int argc) {
    return Modular || (streq0(name, "GetEnumerator") && argc == 0) || OwnMethodId(name, argc) != NULL || DispatchNeedsDefaults(g, name, argc) || BuiltinMethodsCount(name, argc) > 0;
}
static bool DispatchHasCaller(FgGen* g, const char* name) { return Modular || RestrictedMethodName(g, name); }
/* The arguments after the operands of a dispatcher call */
static const char* DispatchTail(FgGen* g, const char* name, int argc, const char* list, const char* caller) {
    return fcFmt(g->c, "%s%s", DispatchNeedsList(g, name, argc) ? fcFmt(g->c, ", %s", list) : "", DispatchHasCaller(g, name) ? fcFmt(g->c, ", %s", caller) : "");
}
static const char* DispatcherSignature(FgGen* g, const char* name, int argc, const char* prefix) {
    FcList* ps = fcListNew(g->c);
    int i;
    fcListAdd(g->c, ps, (void*)"Value self");
    for (i = 0; i < argc; i++) fcListAdd(g->c, ps, (void*)fcFmt(g->c, "Value a%d", i));
    if (DispatchNeedsList(g, name, argc)) fcListAdd(g->c, ps, (void*)"OwnList* list");
    if (DispatchHasCaller(g, name)) fcListAdd(g->c, ps, (void*)"uint32_t caller");
    return fcFmt(g->c, "FIRE_MAYBE_UNUSED %sValue %s(%s)", streq0(prefix, "call_") ? HelperStorage : "static ", CallFn(g, name, argc, prefix), fgJoin(g->c, ps, ", "));
}

static FcList* fgArgNames(FgGen* g, int argc, const char* fallback) {
    FcList* l = fcListNew(g->c);
    int i;
    for (i = 0; i < argc; i++) fcListAdd(g->c, l, (void*)fcFmt(g->c, "a%d", i));
    if (argc == 0 && fallback) fcListAdd(g->c, l, (void*)fallback);
    return l;
}
static const char* ActorWrapper(FgGen* g, const char* name, int argc) {
    const char* direct = CallFn(g, name, argc, "direct_");
    const char* msg = CallFn(g, name, argc, "msg_");
    FcBuf sb = {0};
    FcBuf argList = {0};
    const char* tail;
    const char* msgArgs;
    int i;
    fcBufStr(&argList, "");
    for (i = 0; i < argc; i++) fcBufFmt(&argList, ", a%d", i);
    tail = fcFmt(g->c, "%s%s", DispatchNeedsList(g, name, argc) ? ", list" : "", DispatchHasCaller(g, name) ? ", caller" : "");
    fcBufFmt(&sb, "%s\n", DispatcherSignature(g, name, argc, "call_"));
    fcBufStr(&sb, "{\n");
    if (g->usesActors) {
        fcBufStr(&sb, "    if (self.kind == K_Class && FIRE_LIKELY(leafAlive(self)) && (asObj(self)->h.flags & 8)) {\n");
        fcBufStr(&sb, "        bool aw_ = sameRef(g_awaitReplyTo, self);\n");
        fcBufStr(&sb, "        if (aw_) g_awaitReplyTo = Undef();\n");
        fcBufStr(&sb, "        if (!aw_ || !actorAtHome(self)) {\n");
        fcBufFmt(&sb, "            %s\n", CArray(g, "Value", "av", fgArgNames(g, argc, "Undef()")));
        fcBufFmt(&sb, "            if (aw_) return actorAwait(self, %s, av, %d);\n", msg, argc);
        fcBufFmt(&sb, "            actorSend(self, %s, av, %d);\n", msg, argc);
        fcBufStr(&sb, "            return Undef();\n");
        fcBufStr(&sb, "        }\n");
        fcBufStr(&sb, "    }\n");
    }
    if (g->usesThreads) {
        FcList* reading = fcListNew(g->c);
        bool anyWriting = false;
        const char* notReading;
        for (i = 0; i < g->classList->count; i++) {
            FgClassInfo* cls = (FgClassInfo*)g->classList->items[i];
            if (FG_OVERLOADED(cls->rc, name, argc)) {
                FgCands* set = fgCandidates(g->c, g->rtCands, cls->rc, name, argc);
                int k;
                for (k = 0; k < set->count; k++) if (!set->protos[k]->isStatic) anyWriting = true;
            } else {
                FcProto* m = FG_FIND(cls->rc, name, argc).proto;
                if (m && !m->isStatic) { if (m->readsOnly) fcListAdd(g->c, reading, FC_IVAL(cls->id)); else anyWriting = true; }
            }
        }
        if (reading->count == 0) notReading = "";
        else {
            FcList* tests = fcListNew(g->c);
            for (i = 0; i < reading->count; i++) fcListAdd(g->c, tests, (void*)fcFmt(g->c, "asObj(self)->cls == %d", FC_VALI(reading->items[i])));
            notReading = fcFmt(g->c, " && (self.kind != K_Class || %s)", anyWriting ? fcFmt(g->c, "!(%s)", fgJoin(g->c, tests, " || ")) : "false");
        }
        if (reading->count > 0 && !anyWriting) notReading = " && self.kind != K_Class";
        fcBufFmt(&sb, "    if (FIRE_UNLIKELY(g_isThread) && g_secDepth == 0 && g_domainOn && (self.kind != K_Class || leafAlive(self)) && sharedValue(self)%s) {\n", notReading);
        fcBufStr(&sb, "        if (fire_mainAway()) {\n");
        fcBufStr(&sb, "            bool ss_ = sectionScopeEnter(true);\n");
        fcBufFmt(&sb, "            Value r_ = %s(self%s%s);\n", direct, argList.p, tail);
        fcBufStr(&sb, "            sectionScopeExit(ss_);\n");
        fcBufStr(&sb, "            return r_;\n");
        fcBufStr(&sb, "        }\n");
        fcBufFmt(&sb, "        %s\n", CArray(g, "Value", "gv", fgArgNames(g, argc, "Undef()")));
        fcBufFmt(&sb, "        return globalsCall(self, %s, gv, %d, %s);\n", msg, argc, CString(g, name));
        fcBufStr(&sb, "    }\n");
    }
    fcBufFmt(&sb, "    return %s(self%s%s);\n", direct, argList.p, tail);
    fcBufStr(&sb, "}\n");
    {
        FcBuf m = {0};
        fcBufStr(&m, "");
        for (i = 0; i < argc; i++) fcBufFmt(&m, ", a[%d]", i);
        if (DispatchNeedsList(g, name, argc)) fcBufStr(&m, ", list");
        if (DispatchHasCaller(g, name)) fcBufStr(&m, ", asObj(self)->cls");
        msgArgs = fcBufDone(g->c, &m);
    }
    fcBufFmt(&sb, "static Value %s(Value self, const Value* a, OwnList* list) { (void)a; (void)list; return %s(self%s); }\n", msg, direct, msgArgs);
    fcBufFree(&argList);
    return fcBufDone(g->c, &sb);
}

static int fgCmpIntItems(const void* a, const void* b) { int x = FC_VALI(*(void* const*)a), y = FC_VALI(*(void* const*)b); return x < y ? -1 : x > y; }
/* The text of the VM for a call that finds no method (VM.DescribeMethodNotFound). */
static const char* DescribeMethodNotFound(FgGen* g, const FcRtClass* rc, const char* methodName, int argCount) {
    FcList* arities = fcListNew(g->c);
    const FcRtClass* c;
    int i;
    for (c = rc; c; c = c->base) {
        FcList* overloads = (FcList*)fcDictGet(c->methods, methodName);
        if (overloads) for (i = 0; i < overloads->count; i++) { int pc = ((FcProto*)overloads->items[i])->paramCount; int k; bool seen = false; for (k = 0; k < arities->count; k++) if (FC_VALI(arities->items[k]) == pc) seen = true; if (!seen) fcListAdd(g->c, arities, FC_IVAL(pc)); }
    }
    if (arities->count == 0) return fcFmt(g->c, "Method '%s' not found on '%s'.", methodName, rc->name);
    if (arities->count > 1) qsort(arities->items, (size_t)arities->count, sizeof(void*), fgCmpIntItems);
    {
        FcList* texts = fcListNew(g->c);
        for (i = 0; i < arities->count; i++) fcListAdd(g->c, texts, (void*)fcFmt(g->c, "%d", FC_VALI(arities->items[i])));
        return fcFmt(g->c, "Method '%s' on '%s' has no overload with %d argument(s) (available: %s argument(s)).", methodName, rc->name, argCount, fgJoin(g->c, texts, ", "));
    }
}
/* The text of the VM for a `new` that finds no constructor (VM.DescribeConstructorNotFound). */
static const char* DescribeConstructorNotFound(FgGen* g, const FcRtClass* rc, int argCount) {
    FcList* arities = fcListCopy(g->c, rc->ctorArities);
    FcList* texts = fcListNew(g->c);
    int i;
    if (arities->count > 1) qsort(arities->items, (size_t)arities->count, sizeof(void*), fgCmpIntItems);
    for (i = 0; i < arities->count; i++) fcListAdd(g->c, texts, (void*)fcFmt(g->c, "%d", FC_VALI(arities->items[i])));
    return fcFmt(g->c, "Class '%s' has no constructor with %d argument(s) (available: %s argument(s)).", rc->name, argCount, fgJoin(g->c, texts, ", "));
}

/* ---- Dispatcher ---- */
typedef struct FgDispatchCtx {
    FgGen* g;
    const char* name;
    int argc;
    bool derefs, restricted;
    FcProto** protos;
    const FcRtClass** declaring;
    const FcRtClass* sample;
    bool extension;
} FgDispatchCtx;
static const char* fgDispScoredArg(void* ctx, int i) { FgDispatchCtx* d = (FgDispatchCtx*)ctx; return d->derefs ? fcFmt(d->g->c, "derefArg(a%d)", i) : fcFmt(d->g->c, "a%d", i); }
static const char* fgDispArgsFor(FgDispatchCtx* d, const FcProto* proto) {
    FcBuf b = {0};
    int i;
    fcBufStr(&b, "");
    for (i = 0; i < d->argc; i++) fcBufFmt(&b, d->derefs && ((proto->refMask >> i) & 1) == 0 ? ", derefArg(a%d)" : ", a%d", i);
    return fcBufDone(d->g->c, &b);
}
/* a static overload the values pick on an object (the VM runs it, without using the object) */
static const char* fgDispStaticImpl(FgDispatchCtx* d, FcProto* proto) {
    FgGen* g = d->g;
    const char* defaults;
    const char* pre = DefaultArgsPre(g, proto, d->argc, "Undef()", "list", &defaults);
    const char* args = fcFmt(g->c, "%s%s", fgDispArgsFor(d, proto), defaults);
    while (*args == ',' || *args == ' ') args++;
    return fcFmt(g->c, "{ %sreturn %s(%s); }", pre, ((FgFunc*)fcDictGet(g->funcByProto, proto))->name, args);
}
/* an implementation with default values: the missing arguments are evaluated first (in order), then it is called */
static const char* fgDispCallImpl(FgDispatchCtx* d, const char* target, FcProto* proto, const FcRtClass* sample, bool extension, const FcRtClass* declaringOverload) {
    FgGen* g = d->g;
    const char* defaults;
    const char* pre = DefaultArgsPre(g, proto, d->argc, "self", "list", &defaults);
    const char* access = "";
    FgFound found;
    if (declaringOverload) { found.proto = proto; found.declaring = declaringOverload; found.access = fgAccessOf(proto); }
    else if (sample) found = FG_FIND(sample, d->name, d->argc);
    else { found.proto = NULL; found.declaring = NULL; found.access = ACC_Public; }
    if (d->restricted && sample && found.proto && found.access != ACC_Public) {
        const char* cond = AllowedExpr(g, found.declaring, found.access);
        const char* message = extension
            ? fcFmt(g->c, "Method '%s' of the extension of '%s' is %s and cannot be called from here.", d->name, sample->name + 1, DescribeAccess(found.access))
            : fcFmt(g->c, "Method '%s' of '%s' is %s and cannot be called from here.", d->name, found.declaring->name, DescribeAccess(found.access));
        access = fcFmt(g->c, "if (!%s) return accessDenied(%s); ", cond, CString(g, message));
    }
    return !*pre ? fcFmt(g->c, "{ %sreturn %s(self%s); }", access, target, fgDispArgsFor(d, proto))
        : fcFmt(g->c, "{ %s%sreturn %s(self%s%s); }", access, pre, target, fgDispArgsFor(d, proto), defaults);
}
static const char* fgDispOverloadCall(void* ctx, int c) {
    FgDispatchCtx* d = (FgDispatchCtx*)ctx;
    FcProto* p = d->protos[c];
    if (!d->extension && p->isStatic) return fgDispStaticImpl(d, p);
    return fgDispCallImpl(d, ((FgFunc*)fcDictGet(d->g->funcByProto, p))->name, p, d->sample, d->extension, d->declaring[c]);
}
typedef struct FgDispGroup { FgCands* set; FcList* classes; } FgDispGroup;
static const char* Dispatcher(FgGen* g, const char* name, int argc) {
    FcDict* byFunc = fcDictNew(g->c, FC_KEY_PTR);   /* FgFunc* -> FcList* of ids */
    FcList* overloaded = fcListNew(g->c);           /* FgDispGroup* */
    FcList* extensions;
    FcList* builtins;
    bool enumeratorOfArray;
    FgDispatchCtx d;
    FcBuf sb = {0};
    const char* ownId;
    int i, k;
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)g->classList->items[i];
        FgCands* set;
        bool anyInstance = false;
        if (FG_OVERLOADED(cls->rc, name, argc)) {
            set = fgCandidates(g->c, g->rtCands, cls->rc, name, argc);
            for (k = 0; k < set->count; k++) if (!set->protos[k]->isStatic) anyInstance = true;
        }
        if (anyInstance) {
            FgDispGroup* group = NULL;
            for (k = 0; k < overloaded->count; k++) {
                FgDispGroup* o = (FgDispGroup*)overloaded->items[k];
                int m;
                bool same = o->set->count == set->count;
                for (m = 0; same && m < set->count; m++) if (o->set->protos[m] != set->protos[m] || o->set->declaring[m] != set->declaring[m]) same = false;
                if (same) { group = o; break; }
            }
            if (!group) { group = (FgDispGroup*)fcAlloc(g->c, sizeof *group); group->set = set; group->classes = fcListNew(g->c); fcListAdd(g->c, overloaded, group); }
            fcListAdd(g->c, group->classes, cls);
        } else {
            FcProto* method = FG_FIND(cls->rc, name, argc).proto;
            if (method && !method->isStatic) {
                FgFunc* f = (FgFunc*)fcDictGet(g->funcByProto, method);
                FcList* ids = (FcList*)fcDictGet(byFunc, f);
                if (!ids) { ids = fcListNew(g->c); fcDictPut(byFunc, f, ids); }
                fcListAdd(g->c, ids, FC_IVAL(cls->id));
            }
        }
    }
    extensions = ExtensionMethods(g, name, argc);
    enumeratorOfArray = streq0(name, "GetEnumerator") && argc == 0 && fcDictHas(g->classes, "ListEnumerator");
    builtins = BuiltinMethods(g, name, argc);
    memset(&d, 0, sizeof d);
    d.g = g; d.name = name; d.argc = argc;
    d.derefs = fcDictHas(g->refCallSites, fgNameArgcKey(g, name, argc));
    d.restricted = RestrictedMethodName(g, name);
    fcBufFmt(&sb, "%s\n", DispatcherSignature(g, name, argc, WrapDispatchers ? "direct_" : "call_"));
    fcBufStr(&sb, "{\n");
    if (DispatchNeedsList(g, name, argc)) fcBufStr(&sb, "    (void)list;   // (a method that only works on numbers or text allocates nothing)\n");
    if (DispatchHasCaller(g, name)) fcBufStr(&sb, "    (void)caller;\n");
    fcBufStr(&sb, "    switch (self.kind) {\n");
    for (i = 0; i < extensions->count; i++) {
        FgExtension* x = (FgExtension*)extensions->items[i];
        if (FG_OVERLOADED(x->rc, name, argc)) {
            FgCands* set = fgCandidates(g->c, g->rtCands, x->rc, name, argc);
            fcBufFmt(&sb, "        case %s: {\n", x->cpp);
            d.protos = set->protos; d.declaring = set->declaring; d.sample = x->rc; d.extension = true;
            fcBufStr(&sb, OverloadChoice(g, set->protos, set->count, argc, "NULL", fgDispOverloadCall, &d, "            ", fgDispScoredArg));
            fcBufStr(&sb, "        }\n");
        }
        else fcBufFmt(&sb, "        case %s: %s\n", x->cpp, fgDispCallImpl(&d, ((FgFunc*)fcDictGet(g->funcByProto, x->proto))->name, x->proto, x->rc, true, NULL));
    }
    for (i = 0; i < builtins->count; i++) {
        FgBuiltin* b = (FgBuiltin*)builtins->items[i];
        bool shadowed = false;
        for (k = 0; k < extensions->count; k++) if (streq0(((FgExtension*)extensions->items[k])->cpp, b->kind)) shadowed = true;
        if (!shadowed) fcBufFmt(&sb, "        case %s: return %s;\n", b->kind, b->expr);
    }
    if (enumeratorOfArray) {
        FgClassInfo* info = (FgClassInfo*)fcDictGet(g->classes, "ListEnumerator");
        FgFunc* ctor = (FgFunc*)fcDictGet(g->funcByProto, fgFindConstructor(info->rc, 2));
        fcBufStr(&sb, "        case K_Array: case K_Buffer: case K_String: {\n");
        fcBufStr(&sb, "            bool ok;\n");
        fcBufStr(&sb, "            Value count = lengthOf(self, &ok);\n");
        fcBufFmt(&sb, "            Value e = %s;\n", NewObjectCall(g, info, "list"));
        fcBufFmt(&sb, "            %s\n", Nested(g, fcFmt(g->c, "%s(e, self, count);", ctor->name)));
        fcBufStr(&sb, "            return e;\n");
        fcBufStr(&sb, "        }\n");
    }
    if (fcDictCount(byFunc) > 0 || overloaded->count > 0) {
        fcBufStr(&sb, "        case K_Class:\n");
        fcBufStr(&sb, "            if (FIRE_UNLIKELY(!leafAlive(self))) return destroyedError(self);\n");
        if (overloaded->count == 0 && fcDictCount(byFunc) == 1 && ((FcList*)byFunc->entries[0].value)->count == g->classList->count) {
            FgFunc* f = (FgFunc*)byFunc->entries[0].key;
            fcBufFmt(&sb, "            asObj(self); %s\n", fgDispCallImpl(&d, f->name, f->proto, ((FgClassInfo*)g->classList->items[0])->rc, false, NULL));
        } else {
            fcBufStr(&sb, "            switch (asObj(self)->cls) {\n");
            FC_DICT_EACH(byFunc, e) {
                FgFunc* f = (FgFunc*)e->key;
                FcList* ids = (FcList*)e->value;
                const FcRtClass* sample = NULL;
                for (k = 0; k < ids->count; k++) fcBufFmt(&sb, "                case %d:\n", FC_VALI(ids->items[k]));
                for (k = 0; k < g->classList->count; k++) if (((FgClassInfo*)g->classList->items[k])->id == FC_VALI(ids->items[0])) { sample = ((FgClassInfo*)g->classList->items[k])->rc; break; }
                fcBufFmt(&sb, "                    %s\n", fgDispCallImpl(&d, f->name, f->proto, sample, false, NULL));
            }
            for (i = 0; i < overloaded->count; i++) {
                FgDispGroup* o = (FgDispGroup*)overloaded->items[i];
                for (k = 0; k < o->classes->count; k++) fcBufFmt(&sb, "                case %d:\n", ((FgClassInfo*)o->classes->items[k])->id);
                fcBufStr(&sb, "                {\n");
                d.protos = o->set->protos; d.declaring = o->set->declaring; d.sample = ((FgClassInfo*)o->classes->items[0])->rc; d.extension = false;
                fcBufStr(&sb, OverloadChoice(g, o->set->protos, o->set->count, argc, "objAt(self)", fgDispOverloadCall, &d, "                    ", fgDispScoredArg));
                fcBufStr(&sb, "                }\n");
            }
            fcBufStr(&sb, "                default: break;\n");
            fcBufStr(&sb, "            }\n");
            fcBufStr(&sb, "            break;\n");
        }
    }
    fcBufStr(&sb, "        default: break;\n");
    fcBufStr(&sb, "    }\n");
    if ((ownId = OwnMethodId(name, argc)) != NULL) {
        int baseArgs = OwnMethodBaseArgs(ownId);
        const char* target = baseArgs == 1 ? "a0" : streq0(ownId, "OM_TakeGlobal") && argc == 2 ? "a1" : "Undef()";
        if (argc == baseArgs) fcBufFmt(&sb, "    ownMethod(%s, self, %s, list);\n", ownId, target);
        else fcBufFmt(&sb, "    ownMethodT(%s, self, %s, a%d, list, g_scopeLists, g_scopeCount);   // (the call site with a `Takes` mode left the scopes of the caller in g_scopeLists)\n", ownId, target, baseArgs);
        fcBufStr(&sb, "    return Undef();\n");
    } else {
        FcList* otherArity = fcListNew(g->c);
        if (fcDictCount(byFunc) == 0 && extensions->count == 0 && builtins->count == 0 && !enumeratorOfArray && argc > 0) {
            FcBuf v = {0};
            for (i = 0; i < argc; i++) fcBufFmt(&v, "(void)a%d; ", i);
            fcBufFmt(&sb, "    %s\n", fgTrim(g->c, fcBufDone(g->c, &v)));
        }
        for (i = 0; i < g->classList->count; i++) {
            FgClassInfo* c = (FgClassInfo*)g->classList->items[i];
            if (fgHasMethodNamed(c->rc, name) && !FG_FIND(c->rc, name, argc).proto) fcListAdd(g->c, otherArity, c);
        }
        if (otherArity->count > 0) {
            fcBufStr(&sb, "    if (self.kind == K_Class) switch (asObj(self)->cls) {\n");
            for (i = 0; i < otherArity->count; i++) {
                FgClassInfo* c = (FgClassInfo*)otherArity->items[i];
                fcBufFmt(&sb, "        case %d: return fireError(\"ArgumentException\", %s);\n", c->id, CString(g, DescribeMethodNotFound(g, c->rc, name, argc)));
            }
            fcBufStr(&sb, "        default: break;\n");
            fcBufStr(&sb, "    }\n");
        }
        if (streq0(name, "ToString") && argc == 0) fcBufStr(&sb, "    if (self.kind == K_Class) return retainedV(toStringValue(self, list));\n");
        fcBufFmt(&sb, "    return missingMethod(self, %s, %d);\n", CString(g, name), argc);
    }
    fcBufStr(&sb, "}\n");
    if (WrapDispatchers) fcBufStr(&sb, ActorWrapper(g, name, argc));
    return fcBufDone(g->c, &sb);
}

static const char* InterfaceDispatcherSignature(FgGen* g, const char* iface, const char* name, int argc) {
    FcBuf b = {0};
    int i;
    fcBufFmt(&b, "FIRE_MAYBE_UNUSED static Value %s(Value self", InterfaceDispatcherName(g, iface, name, argc));
    for (i = 0; i < argc; i++) fcBufFmt(&b, ", Value a%d", i);
    fcBufChar(&b, ')');
    return fcBufDone(g->c, &b);
}
/* `icall_I_name_argc`: the method of the object's class for the classes that implement the interface */
static const char* InterfaceDispatcher(FgGen* g, const char* iface, const char* name, int argc) {
    FcList* impls = InterfaceImplementations(g, iface, name, argc);
    FcBuf args = {0};
    FcBuf sb = {0};
    FcList* groups = fcListNew(g->c);
    const char* argText;
    int i, k;
    if (!impls) impls = fcListNew(g->c);
    fcBufStr(&args, "");
    for (i = 0; i < argc; i++) fcBufFmt(&args, ", a%d", i);
    argText = fcBufDone(g->c, &args);
    fcBufFmt(&sb, "%s\n{\n", InterfaceDispatcherSignature(g, iface, name, argc));
    if (impls->count == 0 && argc > 0) {
        FcBuf v = {0};
        for (i = 0; i < argc; i++) fcBufFmt(&v, "(void)a%d; ", i);
        fcBufFmt(&sb, "    %s\n", fgTrim(g->c, fcBufDone(g->c, &v)));
    }
    fcBufStr(&sb, "    if (FIRE_LIKELY(self.kind == K_Class)) {\n");
    fcBufStr(&sb, "        if (FIRE_UNLIKELY(!leafAlive(self))) return destroyedError(self);\n");
    fcBufStr(&sb, "        switch (asObj(self)->cls) {\n");
    /* GroupBy the proto (in the order of the first of each) */
    for (i = 0; i < impls->count; i++) {
        FgImpl* t = (FgImpl*)impls->items[i];
        bool seen = false;
        for (k = 0; k < groups->count; k++) if (((FgImpl*)groups->items[k])->proto == t->proto) seen = true;
        if (!seen) fcListAdd(g->c, groups, t);
    }
    for (k = 0; k < groups->count; k++) {
        FcProto* proto = ((FgImpl*)groups->items[k])->proto;
        fcBufStr(&sb, "            ");
        for (i = 0; i < impls->count; i++) if (((FgImpl*)impls->items[i])->proto == proto) fcBufFmt(&sb, "case %d: ", ((FgImpl*)impls->items[i])->cls->id);
        fcBufFmt(&sb, "return %s(self%s);\n", ((FgFunc*)fcDictGet(g->funcByProto, proto))->name, argText);
    }
    fcBufStr(&sb, "            default: break;\n");
    fcBufStr(&sb, "        }\n");
    fcBufStr(&sb, "    }\n");
    fcBufFmt(&sb, "    return missingMethod(self, %s, %d);\n", CString(g, name), argc);
    fcBufStr(&sb, "}\n");
    return fcBufDone(g->c, &sb);
}

/* `Takes.Children` of an object that implements IEnumerable takes its items (SPEC 2.2) */
static const char* EnumerateItemsFunction(FgGen* g) {
    FcList* ids = fcListNew(g->c);
    FcBuf sb = {0};
    int i;
    for (i = 0; i < g->classList->count; i++) { FgClassInfo* c = (FgClassInfo*)g->classList->items[i]; if (ClassIs(c->rc, "IEnumerable")) fcListAdd(g->c, ids, FC_IVAL(c->id)); }
    fcBufStr(&sb, "static bool fire_enumerateItems(Value obj, OwnList* list, void (*sink)(Value, void*), void* ctx) {\n");
    if (ids->count == 0) fcBufStr(&sb, "    (void)obj; (void)list; (void)sink; (void)ctx;\n    return false;\n}\n");
    else {
        const char* unwound = g->usesExceptions ? "    if (FIRE_UNLIKELY(g_unwind.active)) return false;\n" : "";
        const char* unwound2 = g->usesExceptions ? "        if (FIRE_UNLIKELY(g_unwind.active)) return false;\n" : "";
        fcBufStr(&sb, "    if (obj.kind != K_Class || !leafAlive(obj)) return false;\n");
        if (Modular) fcBufStr(&sb, "    if (!fireIsOf(asObj(obj)->cls, \"IEnumerable\")) return false;\n");
        else {
            fcBufStr(&sb, "    switch (asObj(obj)->cls) { ");
            for (i = 0; i < ids->count; i++) fcBufFmt(&sb, "case %d: ", FC_VALI(ids->items[i]));
            fcBufStr(&sb, "break; default: return false; }\n");
        }
        fcBufFmt(&sb, "    Value e = call_GetEnumerator_0(obj%s);\n", DispatchTail(g, "GetEnumerator", 0, "list", "0xFFFFFFFFu"));
        fcBufStr(&sb, "    adopt(e, list);\n");
        fcBufStr(&sb, unwound);
        fcBufStr(&sb, "    for (;;) {\n");
        fcBufFmt(&sb, "        Value more = call_MoveNext_0(e%s);\n", DispatchTail(g, "MoveNext", 0, "list", "0xFFFFFFFFu"));
        fcBufStr(&sb, unwound2);
        fcBufStr(&sb, "        if (!(more.kind == K_Bool && more.i)) break;\n");
        fcBufFmt(&sb, "        Value cur = call_GetCurrent_0(e%s);\n", DispatchTail(g, "GetCurrent", 0, "list", "0xFFFFFFFFu"));
        fcBufStr(&sb, "        adopt(cur, list);\n");
        fcBufStr(&sb, unwound2);
        fcBufStr(&sb, "        sink(cur, ctx);\n");
        fcBufStr(&sb, "    }\n");
        fcBufStr(&sb, "    return true;\n");
        fcBufStr(&sb, "}\n");
    }
    fcBufStr(&g->initCode, "    g_enumerateItems = fire_enumerateItems;\n");
    return fcBufDone(g->c, &sb);
}

/* The arms of a switch over class ids, one per line: classes with the same code share one arm, in the order of their first class. */
typedef struct FgArm { int id; const char* code; } FgArm;
static void fgArmAdd(FgGen* g, FcList* arms, int id, const char* code) { FgArm* a = (FgArm*)fcAlloc(g->c, sizeof *a); a->id = id; a->code = code; fcListAdd(g->c, arms, a); }
static const char* MergedCases(FgGen* g, FcList* arms, const char* indent) {
    FcBuf sb = {0};
    FcList* codes = fcListNew(g->c);
    int i, k;
    if (!indent) indent = "        ";
    fcBufStr(&sb, "");
    for (i = 0; i < arms->count; i++) if (!fcListHasStr(codes, ((FgArm*)arms->items[i])->code)) fcListAdd(g->c, codes, (void*)((FgArm*)arms->items[i])->code);
    for (k = 0; k < codes->count; k++) {
        fcBufStr(&sb, indent);
        for (i = 0; i < arms->count; i++) if (streq0(((FgArm*)arms->items[i])->code, (const char*)codes->items[k])) fcBufFmt(&sb, "case %d: ", ((FgArm*)arms->items[i])->id);
        fcBufStr(&sb, (const char*)codes->items[k]);
        fcBufChar(&sb, '\n');
    }
    return fcBufDone(g->c, &sb);
}

static const char* FieldHelpersCore(FgGen* g, const char* name);
static const char* FieldHelpers(FgGen* g, const char* name) {
    const char* text = FieldHelpersCore(g, name);
    return g->usesProbes ? WrapProbeSetter(g, name, text) : text;
}
static const char* PropertyHelpers(FgGen* g, const char* name);

static const char* FieldHelpersCore(FgGen* g, const char* name) {
    FcList* ids = fcListNew(g->c);       /* indexByClass: the ids */
    FcList* idx = fcListNew(g->c);       /* and their indices */
    bool narrow, isLength, packed = false, nonNullAddr = false, allSame;
    const char* m;
    const char* lengthCode;
    const char* indexCode;
    const char* unitCode;
    const char* valueIndex;
    const char* voidIndex;
    const char* notObject;
    FcBuf sb = {0};
    FcList* unitArms;
    int i, k;
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)g->classList->items[i];
        int index = fgFieldIndexOf(g, cls->rc, name);
        if (index >= 0) { fcListAdd(g->c, ids, FC_IVAL(cls->id)); fcListAdd(g->c, idx, FC_IVAL(index)); if (RawTypeAt(g, cls->rc, index) != 0) packed = true; }
        if (fgFieldAddressTaken(g, name)) { const FcFieldInfo* di = DeclaringInfo(cls->rc, name); if (di && di->requiredKind != 0 && fgDkIsNonNull(di->requiredKind)) nonNullAddr = true; }
    }
    if (Modular || HasProperty(g, name) || RestrictedField(g, name) || packed || nonNullAddr) return PropertyHelpers(g, name);
    narrow = NarrowName(g, name);
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)g->classList->items[i];
        for (k = 0; k < ids->count; k++)
            if (FC_VALI(ids->items[k]) == cls->id) {
                int slotAt, halfAt;
                FieldPlace(g, cls->rc, FC_VALI(idx->items[k]), &slotAt, &halfAt);
                idx->items[k] = FC_IVAL(narrow ? slotAt * 2 + halfAt : slotAt);
            }
    }
    isLength = streq0(name, "Length") || streq0(name, "length");
    m = Mangle(g, name);
    lengthCode = isLength ? "    { bool ok; Value n = lengthOf(v, &ok); if (ok) return n; }\n"
        : streq0(name, "littleEndian") ? "    if (v.kind == K_Buffer) return Bool(bufOf(v)->little != 0);\n" : "";
    if (ids->count == 0) {
        fcBufFmt(&sb, "static inline Value gf_%s(Value v) {\n%s    return missingField(v, %s);\n}\n", m, lengthCode, CString(g, name));
        fcBufFmt(&sb, "static inline void sf_%s(Value v, Value x) { (void)x; if (v.kind != K_Class) { notObjectAssign(v); return; } if (!leafAlive(v)) { destroyedError(v); return; } missingFieldSet(v, %s); }\n", m, CString(g, name));
        if (fgFieldAddressTaken(g, name)) fcBufFmt(&sb, "static inline Value fp_%s(Value v) { (void)v; fatal(\"Field '%s' not found on this object.\"); }\n", m, name);
        return fcBufDone(g->c, &sb);
    }
    allSame = true;
    for (i = 1; i < idx->count; i++) if (FC_VALI(idx->items[i]) != FC_VALI(idx->items[0])) allSame = false;
    if (ids->count == g->classList->count && allSame) indexCode = fcFmt(g->c, "    const uint32_t idx = %d;", FC_VALI(idx->items[0]));
    else {
        FcBuf cases = {0};
        FcList* values = fcListNew(g->c);
        for (i = 0; i < idx->count; i++) { int v = FC_VALI(idx->items[i]); bool seen = false; for (k = 0; k < values->count; k++) if (FC_VALI(values->items[k]) == v) seen = true; if (!seen) fcListAdd(g->c, values, FC_IVAL(v)); }
        for (k = 0; k < values->count; k++) {
            int v = FC_VALI(values->items[k]);
            for (i = 0; i < idx->count; i++) if (FC_VALI(idx->items[i]) == v) fcBufFmt(&cases, "        case %d:\n", FC_VALI(ids->items[i]));
            fcBufFmt(&cases, "            idx = %d; break;\n", v);
        }
        indexCode = fcFmt(g->c, "    uint32_t idx = 0;\n    switch (o->cls) {\n%s        default: missingField(v, %s); FIELD_MISSING_RETURN\n    }", fcBufDone(g->c, &cases), CString(g, name));
    }
    unitArms = fcListNew(g->c);
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)g->classList->items[i];
        const char* requiredUnit;
        const char* requiredType;
        const char* arrayCheck;
        const char* check;
        int kind;
        bool has = false;
        for (k = 0; k < ids->count; k++) if (FC_VALI(ids->items[k]) == cls->id) has = true;
        if (!has) continue;
        requiredUnit = fgFieldRequiredUnit(cls->rc, name);
        kind = fgFieldRequiredKind(cls->rc, name);
        requiredType = fgFieldRequiredType(cls->rc, name);
        {
            const char* c1 = requiredUnit ? fcFmt(g->c, "x = %s; ", AdaptUnitCall(g, "x", requiredUnit)) : "";
            const char* c2 = kind != 0 ? fcFmt(g->c, "%s ", FieldKindCheck(g, name, kind)) : "";
            const char* c3 = requiredType ? fcFmt(g->c, "%s ", ClassGuard(g, "x", requiredType, fcFmt(g->c, "Field '%s'", name), "o", NULL, "")) : "";
            arrayCheck = FieldArrayCheck(g, cls->rc, name, "o");
            check = fcFmt(g->c, "%s%s%s%s", c1, c2, c3, *arrayCheck ? fcFmt(g->c, "%s ", arrayCheck) : "");
        }
        if (cls->rc->isRecord) check = fcFmt(g->c, "if (FIRE_UNLIKELY(!recInit(o))) { recordWriteError(o, %s); return; } %s", CString(g, name), check);
        if (*check) fgArmAdd(g, unitArms, cls->id, fcFmt(g->c, "%sbreak;", check));
    }
    unitCode = unitArms->count == 0 ? "" : fcFmt(g->c, "    switch (o->cls) {\n%s        default: break;\n    }\n%s", MergedCases(g, unitArms, NULL), g->usesExceptions ? "    if (FIRE_UNLIKELY(g_unwind.active)) return;\n" : "");
    valueIndex = fgReplace(g->c, indexCode, "FIELD_MISSING_RETURN", "return Undef();");
    voidIndex = fgReplace(g->c, indexCode, "FIELD_MISSING_RETURN", "return;");
    notObject = fcFmt(g->c, "    if (FIRE_UNLIKELY(v.kind != K_Class)) return missingField(v, %s);\n", CString(g, name));
    fcBufFmt(&sb, "static inline Value gf_%s(Value v) {\n%s%s    if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v);\n    Obj* o = asObj(v);\n%s\n    %s\n}\n", m, lengthCode, notObject, valueIndex,
        narrow ? "return nvGet(&objFields(o)[idx >> 1], (int)(idx & 1u));" : g->usesThreads ? "{ Value r_ = objFields(o)[idx]; FIRE_DOM_TAKE(&o->h, r_); return r_; }" : "return objFields(o)[idx];");
    if (g->usesThreads)
        fcBufFmt(&sb, "static inline void sfb_%s(Value v, Value x);\nstatic inline void sf_%s(Value v, Value x) { bool ss_ = sectionScopeEnterV(v); if (FIRE_LIKELY(!g_isThread || !sharedValue(v) || toMainHolder(v, &x, %s))) sfb_%s(v, x); sectionScopeExit(ss_); }\n",
            m, m, CString(g, fcFmt(g->c, "The field '%s' of an object of the globals", name)), m);
    fcBufFmt(&sb, "static inline void %s%s(Value v, Value x) {\n    if (FIRE_UNLIKELY(v.kind != K_Class)) { notObjectAssign(v); return; }\n    if (FIRE_UNLIKELY(!leafAlive(v))) { destroyedError(v); return; }\n    Obj* o = asObj(v);\n%s\n%s%s",
        g->usesThreads ? "sfb_" : "sf_", m, voidIndex, unitCode,
        narrow ? "    nvSet(&objFields(o)[idx >> 1], (int)(idx & 1u), x);\n}\n" : "    Value old = objFields(o)[idx];\n    objFields(o)[idx] = x;\n    FIRE_DOM_STORE(&o->h, x);\n    retain(x);\n    release(old);\n}\n");
    if (fgFieldAddressTaken(g, name)) fcBufFmt(&sb, "static inline Value fp_%s(Value v) {\n    if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v);\n    Obj* o = asObj(v);\n%s\n    return PtrV(&objFields(o)[idx]);\n}\n", m, valueIndex);
    return fcBufDone(g->c, &sb);
}

/* The names of fields the type checker derived `defined` (FieldInfo.DerivedDefined) */
static FcDict* DerivedFieldNames(FgGen* g) {
    if (!g->derivedFieldNames) {
        g->derivedFieldNames = fcDictNew(g->c, FC_KEY_STR);
        FC_DICT_EACH(g->program->program->classes, e) {
            FcRtClass* rc = (FcRtClass*)e->value;
            FC_DICT_EACH(rc->ownFieldInfo, f) if (((FcFieldInfo*)f->value)->derivedDefined) fcSetAdd(g->derivedFieldNames, f->key);
        }
    }
    return g->derivedFieldNames;
}
/* The check of a field declared int/float/bool in its setter (DeclaredKind, fire_rt guardKind). */
static const char* FieldKindCheck(FgGen* g, const char* name, int kind) {
    return fcFmt(g->c, "x = %s;", GuardKindCall(g, "x", kind, fcDictHas(DerivedFieldNames(g), name) ? fgDerivedFieldWhat(g->c, name) : fcFmt(g->c, "Field '%s'", name)));
}
/* A field declared as a typed array: what is assigned is typed like by OpCode.TypeArray ("" for any other field). */
static const char* FieldArrayCheck(FgGen* g, const FcRtClass* rc, const char* name, const char* obj) {
    int arr = fgFieldRequiredArray(rc, name);
    const char* cls;
    int k;
    if (arr != 0) return fcFmt(g->c, "typeArray(x, %d, %d, %s, 1);", fgAeTypeOf(arr), arr >> 8, TypeArrayUnitArgsOf(g, fgFieldArrayUnit(rc, name), arr));
    if (obj && ArrayOfParamParts(g, rc, name, &cls, &k)) return fcFmt(g->c, "{ const TArg* fta_ = %s; if (fta_ && fta_->arrElem) typeArrayOfArg(x, fta_); }", TArgOfExpr(g, cls, k, obj));
    return "";
}
/* The class and index of the type parameter of a field `T items[]` (FieldInfo.ArrayOfParam); false for any other field. */
static bool ArrayOfParamParts(FgGen* g, const FcRtClass* rc, const char* name, const char** cls, int* k) {
    const char* text = fgFieldArrayOfParam(rc, name);
    int at;
    (void)g;
    if (!text) return false;
    at = fgIndexOf(text, '@');
    *cls = text + at + 1;
    *k = atoi(text + 1);
    return true;
}
static const char* KindConstant(int declaredKind) {
    switch (fgDkTagOf(declaredKind)) {
        case TAG_Int: return "K_Int"; case TAG_Float: return "K_Float"; case TAG_Word: return "K_Word"; case TAG_Char: return "K_Char"; case TAG_String: return "K_String"; default: return "K_Bool";
    }
}
/* The check of a declared kind as a statement on a Value variable */
static const char* GuardStmt(FgGen* g, const char* v, int declared, const char* what, const char* exit) {
    bool cut, nn, unitless;
    const char* kind;
    const char* fast;
    const char* text;
    const char* slow;
    int flags;
    if (fgDkIsString(declared))
        return exit ? fcFmt(g->c, "if (FIRE_UNLIKELY(%s.kind != K_String && %s.kind != K_Undefined) && guardStrSlot(&%s)) goto %s;", v, v, v, exit)
            : fcFmt(g->c, "if (FIRE_UNLIKELY(%s.kind != K_String && %s.kind != K_Undefined)) (void)guardStrSlot(&%s);", v, v, v);
    cut = fgDkTagOf(declared) == TAG_Int && !fgDkKeepsWidth(declared) && g->program->intWidth < 64;
    nn = fgDkIsNonNull(declared); unitless = fgDkIsUnitless(declared);
    kind = KindConstant(declared);
    fast = fcFmt(g->c, "%s.kind == %s%s%s%s", v, kind, unitless ? fcFmt(g->c, " && %s.unit == 0", v) : "", streq0(kind, "K_Int") ? fcFmt(g->c, " && %s.reserved == 0", v) : "", cut ? fcFmt(g->c, " && %s.wide == 0", v) : "");
    flags = (unitless ? 1 : 0) | (nn ? 2 : 0) | (cut ? 4 : 0);
    text = Constant(g, fcMakeString(g->c, UnitDemandDisplay(g, "unitless")));
    slow = fcFmt(g->c, "guardSlot(&%s, %s, %d, %s, %s)", v, kind, flags, CString(g, what), fcStrN(g->c, text + 5, strlen(text) - 6));
    return exit ? fcFmt(g->c, "if (FIRE_UNLIKELY(!(%s)) && %s) goto %s;", fast, slow, exit) : fcFmt(g->c, "if (FIRE_UNLIKELY(!(%s))) (void)%s;", fast, slow);
}
static const char* GuardKindCall(FgGen* g, const char* value, int declared, const char* what) {
    const char* call;
    if (fgDkIsString(declared)) return fcFmt(g->c, "(%s.kind == K_String || %s.kind == K_Undefined ? %s : guardStr(%s))", value, value, value, value);
    call = fcFmt(g->c, "guardKind%s(%s, %s, %s, %s, %s)", fgDkIsNonNull(declared) ? "NN" : "", value, KindConstant(declared), fgDkIsUnitless(declared) ? "true" : "false", CString(g, what),
        Constant(g, fcMakeString(g->c, UnitDemandDisplay(g, "unitless"))));
    return fgDkTagOf(declared) == TAG_Int && !fgDkKeepsWidth(declared) && g->program->intWidth < 64 ? fcFmt(g->c, "intProgram(%s)", call) : call;
}
/* What a parameter, field or return value declared int/float/bool certainly holds after its check */
static int DeclaredValueType(FgGen* g, int declared, const char* demand) {
    int kind = KindOfDeclared(declared), nullable, unit;
    bool nonNull;
    if (kind == KindUnknown) return TypeUnknown;
    nonNull = fgDkIsNonNull(declared);
    nullable = nonNull ? 0 : NullableBit;
    if (kind == KindBool || kind == KindWord) return TypeOf(kind, 0) | nullable;
    unit = demand ? DemandedUnitId(g, demand) : fgDkIsUnitless(declared) ? 0 : -1;
    if (unit < 0) return TypeUnknown;
    return TypeOf(kind, unit) | nullable | (kind == KindInt && !fgDkKeepsWidth(declared) ? (nonNull ? NarrowBit : CutBit) : 0);
}
static int KindOfDeclared(int declared) {
    switch (fgDkTagOf(declared)) {
        case TAG_Int: return KindInt; case TAG_Float: return KindFloat; case TAG_Word: return KindWord; case TAG_Char: case TAG_String: return KindUnknown; default: return KindBool;
    }
}

/* Is the function the setter of a property (`set_name`)? */
static bool IsSetterFunc(FgGen* g, const FgFunc* f) {
    if (f->kind != FK_Method || !f->proto) return false;
    if (!g->setterProtos) {
        g->setterProtos = fcDictNew(g->c, FC_KEY_PTR);
        FC_DICT_EACH(g->program->program->classes, e) {
            FcRtClass* rc = (FcRtClass*)e->value;
            FC_DICT_EACH(rc->methods, m) {
                FcList* protos = (FcList*)m->value;
                int i;
                if (!fcStartsWith((const char*)m->key, "set_")) continue;
                for (i = 0; i < protos->count; i++) { FcProto* p = (FcProto*)protos->items[i]; if (!p->isStatic && p->paramCount == 1) fcSetAdd(g->setterProtos, p); }
            }
        }
    }
    return fcDictHas(g->setterProtos, f->proto);
}
/* Does some class of the program declare a property of this name (`get_name`/`set_name`)? */
static bool HasProperty(FgGen* g, const char* name) {
    if (!g->propertyNames) {
        g->propertyNames = fcDictNew(g->c, FC_KEY_STR);
        FC_DICT_EACH(g->program->program->classes, e) {
            FC_DICT_EACH(((FcRtClass*)e->value)->methods, m) {
                const char* method = (const char*)m->key;
                if (fcStartsWith(method, "get_") || fcStartsWith(method, "set_")) fcSetAdd(g->propertyNames, method + 4);
            }
        }
    }
    return fcDictHas(g->propertyNames, name);
}

static const char* fgAccessorGuard(FgGen* g, const char* method, const FcRtClass* decl, int access, bool isSetter) {
    const char* text;
    const char* cond;
    if (!CheckAccess || access == ACC_Public) return "";
    text = CString(g, fcFmt(g->c, "'%s' of '%s' is %s and cannot be accessed from here.", method, decl->name, DescribeAccess(access)));
    cond = AllowedExpr(g, decl, access);
    return isSetter ? fcFmt(g->c, "if (!%s) { accessDenied(%s); return; } ", cond, text) : fcFmt(g->c, "if (!%s) return accessDenied(%s); ", cond, text);
}
/* The arms of the field helpers of `name` for these classes: read, write and the address - a field of the class, otherwise its property. */
static void FieldArms(FgGen* g, const char* name, FcList* classes, FcList* getArms, FcList* setArms, FcList* ptrArms) {
    int i;
    for (i = 0; i < classes->count; i++) {
        FgClassInfo* cls = (FgClassInfo*)classes->items[i];
        int label = cls->id, index = fgFieldIndexOf(g, cls->rc, name);
        if (index >= 0) {
            const char* fieldGuard = "";
            const char* fieldGuardSet = "";
            const char* unit = "";
            const char* requiredUnit;
            const char* requiredType;
            const char* arrayCheck;
            const char* rawArm;
            const FcRtClass* declaring;
            int access, kind, armSlot, armHalf;
            if (CheckAccess && (declaring = fgFieldAccess(cls->rc, name, &access)) != NULL && access != ACC_Public) {
                const char* text = CString(g, fcFmt(g->c, "Field '%s' of '%s' is %s and cannot be accessed from here.", name, declaring->name, DescribeAccess(access)));
                const char* cond = AllowedExpr(g, declaring, access);
                fieldGuard = fcFmt(g->c, "if (!%s) return accessDenied(%s); ", cond, text);
                fieldGuardSet = fcFmt(g->c, "if (!%s) { accessDenied(%s); return; } ", cond, text);
            }
            fgArmAdd(g, getArms, label, g->usesThreads ? fcFmt(g->c, "{ %sValue r_ = %s; FIRE_DOM_TAKE(&o->h, r_); return r_; }", fieldGuard, FieldRead(g, cls->rc, index, "o"))
                : fcFmt(g->c, "{ %sreturn %s; }", fieldGuard, FieldRead(g, cls->rc, index, "o")));
            if ((requiredUnit = fgFieldRequiredUnit(cls->rc, name)) != NULL)
                unit = fcFmt(g->c, "x = %s; %s", AdaptUnitCall(g, "x", requiredUnit), g->usesExceptions ? "if (FIRE_UNLIKELY(g_unwind.active)) return; " : "");
            if ((kind = fgFieldRequiredKind(cls->rc, name)) != 0)
                unit = fcFmt(g->c, "%s%s%s", unit, FieldKindCheck(g, name, kind), g->usesExceptions ? " if (FIRE_UNLIKELY(g_unwind.active)) return; " : " ");
            if ((requiredType = fgFieldRequiredType(cls->rc, name)) != NULL)
                unit = fcFmt(g->c, "%s%s%s", unit, ClassGuard(g, "x", requiredType, fcFmt(g->c, "Field '%s'", name), "o", NULL, ""), g->usesExceptions ? " if (FIRE_UNLIKELY(g_unwind.active)) return; " : " ");
            if (*(arrayCheck = FieldArrayCheck(g, cls->rc, name, "o")))
                unit = fcFmt(g->c, "%s%s%s", unit, arrayCheck, g->usesExceptions ? " if (FIRE_UNLIKELY(g_unwind.active)) return; " : " ");
            if (cls->rc->isRecord) fieldGuardSet = fcFmt(g->c, "%sif (FIRE_UNLIKELY(!recInit(o))) { recordWriteError(o, %s); return; } ", fieldGuardSet, CString(g, name));
            FieldPlace(g, cls->rc, index, &armSlot, &armHalf);
            if ((rawArm = RawRef(g, cls->rc, index, "o")) != NULL) {
                fgArmAdd(g, setArms, label, fcFmt(g->c, "{ %s%s%s = %s; return; }", fieldGuardSet, unit, rawArm, RawFromValue(g, RawTypeAt(g, cls->rc, index), "x")));
                fgArmAdd(g, ptrArms, label, FieldPtrExpr(g, cls->rc, index, "NULL", "o"));
                continue;
            }
            fgArmAdd(g, setArms, label, armHalf >= 0 ? fcFmt(g->c, "{ %s%snvSet(&objFields(o)[%d], %d, x); return; }", fieldGuardSet, unit, armSlot, armHalf)
                : fcFmt(g->c, "{ %s%sValue old = objFields(o)[%d]; objFields(o)[%d] = x; FIRE_DOM_STORE(&o->h, x); retain(x); release(old); return; }", fieldGuardSet, unit, armSlot, armSlot));
            {
                const char* ptr = FieldPtrExpr(g, cls->rc, index, fcFmt(g->c, "(void*)&objFields(o)[%d]", armSlot), "o");
                fgArmAdd(g, ptrArms, label, ptr ? ptr : fcFmt(g->c, "return PtrV(&objFields(o)[%d]);", armSlot));
            }
            continue;
        }
        {
            FgFound gf = FG_FIND(cls->rc, fcFmt(g->c, "get_%s", name), 0);
            FgFound sf = FG_FIND(cls->rc, fcFmt(g->c, "set_%s", name), 1);
            FcProto* getter = gf.proto && !gf.proto->isStatic ? gf.proto : NULL;
            FcProto* setter = sf.proto && !sf.proto->isStatic ? sf.proto : NULL;
            if (!getter && !setter) continue;
            if (getter) GetFunc(g, getter, FK_Method, 0);
            if (setter) GetFunc(g, setter, FK_Method, 0);
            if (getter) fgArmAdd(g, getArms, label, fcFmt(g->c, "{ %sValue r = %s(v); adopt(r, list); return r; }", fgAccessorGuard(g, fcFmt(g->c, "get_%s", name), gf.declaring, gf.access, false), ((FgFunc*)fcDictGet(g->funcByProto, getter))->name));
            else if (setter) fgArmAdd(g, getArms, label, fcFmt(g->c, "return missingField(v, %s);", CString(g, name)));
            if (setter) fgArmAdd(g, setArms, label, fcFmt(g->c, "{ %sValue r = %s(v, x); adopt(r, list); return; }", fgAccessorGuard(g, fcFmt(g->c, "set_%s", name), sf.declaring, sf.access, true), ((FgFunc*)fcDictGet(g->funcByProto, setter))->name));
            else if (getter) fgArmAdd(g, setArms, label, fcFmt(g->c, "{ noSetterError(v, %s); return; }", CString(g, name)));
        }
    }
}

static const char* PropertyHelpers(FgGen* g, const char* name) {
    const char* m = Mangle(g, name);
    bool isLength = streq0(name, "Length") || streq0(name, "length");
    const char* lengthCode = isLength ? "    { bool ok; Value n = lengthOf(v, &ok); if (ok) return n; }\n"
        : streq0(name, "littleEndian") ? "    if (v.kind == K_Buffer) return Bool(bufOf(v)->little != 0);\n" : "";
    FcList* getArms = fcListNew(g->c);
    FcList* setArms = fcListNew(g->c);
    FcList* ptrArms = fcListNew(g->c);
    const char* get;
    const char* set;
    const char* ptr;
    const char* lp;
    const char* unusedLp;
    const char* lpArgs;
    FcBuf sb = {0};
    FieldArms(g, name, g->classList, getArms, setArms, ptrArms);
    get = MergedCases(g, getArms, NULL); set = MergedCases(g, setArms, NULL); ptr = MergedCases(g, ptrArms, NULL);
    lp = fcFmt(g->c, "%s%s", FieldHasList(g, name) ? ", OwnList* list" : "", FieldHasCaller(g, name) ? ", uint32_t caller" : "");
    unusedLp = fcFmt(g->c, "%s%s", FieldHasList(g, name) ? "(void)list; " : "", FieldHasCaller(g, name) ? "(void)caller; " : "");
    fcBufFmt(&sb, "%sValue gf_%s(Value v%s) {\n%s    %s\n    if (FIRE_UNLIKELY(v.kind != K_Class)) return missingField(v, %s);\n    if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v);\n    Obj* o = asObj(v);\n    switch (o->cls) {\n%s        default: return missingField(v, %s);\n    }\n}\n",
        HelperInline, m, lp, lengthCode, unusedLp, CString(g, name), get, CString(g, name));
    lpArgs = fcFmt(g->c, "%s%s", FieldHasList(g, name) ? ", list" : "", FieldHasCaller(g, name) ? ", caller" : "");
    if (g->usesThreads)
        fcBufFmt(&sb, "static inline void sfb_%s(Value v, Value x%s);\n%svoid sf_%s(Value v, Value x%s) { bool ss_ = sectionScopeEnterV(v); if (FIRE_LIKELY(!g_isThread || !sharedValue(v) || toMainHolder(v, &x, %s))) sfb_%s(v, x%s); sectionScopeExit(ss_); }\n",
            m, lp, HelperInline, m, lp, CString(g, fcFmt(g->c, "The field '%s' of an object of the globals", name)), m, lpArgs);
    fcBufFmt(&sb, "%svoid %s%s(Value v, Value x%s) {\n    (void)x; %s\n    if (FIRE_UNLIKELY(v.kind != K_Class)) { notObjectAssign(v); return; }\n    if (FIRE_UNLIKELY(!leafAlive(v))) { destroyedError(v); return; }\n    Obj* o = asObj(v);\n    switch (o->cls) {\n%s        default: missingFieldSet(v, %s); return;\n    }\n}\n",
        g->usesThreads ? "static inline " : HelperInline, g->usesThreads ? "sfb_" : "sf_", m, lp, unusedLp, set, CString(g, name));
    if (fgFieldAddressTaken(g, name))
        fcBufFmt(&sb, "%sValue fp_%s(Value v) {\n    if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v);\n    Obj* o = asObj(v);\n    switch (o->cls) {\n%s        default: fatal(\"The address of '%s' cannot be taken (it is a property or does not exist).\");\n    }\n}\n", HelperInline, m, ptr, name);
    return fcBufDone(g->c, &sb);
}

/* `a[i]` and `a[i] = v`: arrays, buffers and strings directly, objects through their GetIndex/SetIndex methods. */
static const char* IndexHelpers(FgGen* g) {
    FcBuf sb = {0};
    const char* secEnd = g->usesThreads ? " sectionScopeExit(ss_);" : "";
    fcBufFmt(&sb, "%sValue %saget_g(Value a, Value i, OwnList* list) {\n", HelperInline, Px);
    if (fgHasNameArgc(g, g->dispatchers, "GetIndex", 1))
        fcBufFmt(&sb, "    if (a.kind == K_Class) { Value r = %s(a, i%s); adopt(r, list); return r; }\n", CallFn(g, "GetIndex", 1, "call_"), DispatchTail(g, "GetIndex", 1, "list", "0xFFFFFFFFu"));
    else fcBufStr(&sb, "    (void)list;\n");
    fcBufStr(&sb, "    return arrayGet(a, i);\n");
    fcBufStr(&sb, "}\n");
    fcBufFmt(&sb, "%sValue %saset_g(Value a, Value i, Value v, OwnList* list) {\n", HelperInline, Px);
    if (g->usesThreads) fcBufStr(&sb, "    bool ss_ = sectionScopeEnterV(a);\n    if (FIRE_UNLIKELY(g_isThread) && sharedValue(a) && !toMainHolder(a, &v, \"An element of an array of the globals\")) { sectionScopeExit(ss_); return v; }\n");
    if (fgHasNameArgc(g, g->dispatchers, "SetIndex", 2))
        fcBufFmt(&sb, "    if (a.kind == K_Class) { claimToHolder(a, i, list); Value r = %s(a, i, v%s); adopt(r, list);%s return v; }\n", CallFn(g, "SetIndex", 2, "call_"), DispatchTail(g, "SetIndex", 2, "list", "0xFFFFFFFFu"), secEnd);
    else fcBufStr(&sb, "    (void)list;\n");
    fcBufFmt(&sb, "%s\n", g->usesThreads ? fcFmt(g->c, "    v = arraySet(a, i, v);%s\n    return v;", secEnd) : "    return arraySet(a, i, v);");
    fcBufStr(&sb, "}\n");
    return fcBufDone(g->c, &sb);
}

static const char* ClassDescTable(FgGen* g);
static const char* fgMakeError(FgGen* g, const char* head, FgClassInfo* cls, int ctorArgc, const char* args) {
    return fcFmt(g->c, "%s {\n    Value o = %s;\n    %s\n    return o;\n}\n", head, NewObjectCall(g, cls, "g_globalOwn"),
        Nested(g, fcFmt(g->c, "%s(o, %s);", ((FgFunc*)fcDictGet(g->funcByProto, fgFindConstructor(cls->rc, ctorArgc)))->name, args)));
}
/* fire::runDestructors (destructors of the class chain, derived class first) and fire::userToString (ToString()). */
static const char* RuntimeHooks(FgGen* g) {
    FcBuf sb = {0};
    fcBufStr(&sb, ClassDescTable(g));
    if (g->usesExceptions) {
        FgClassInfo* indexClass = (FgClassInfo*)fcDictGet(g->classes, "IndexOutOfBoundsException");
        FgClassInfo* destroyedClass = (FgClassInfo*)fcDictGet(g->classes, "DestroyedException");
        FgClassInfo* divideClass = (FgClassInfo*)fcDictGet(g->classes, "DivideByZeroException");
        FgClassInfo* reflectClass;
        FgClassInfo* accessClass;
        FgClassInfo* unitClass;
        int i;
        if (!indexClass) FG_NOTSUP("exceptions need the prelude class IndexOutOfBoundsException");
        if (!destroyedClass) FG_NOTSUP("exceptions need the prelude class DestroyedException");
        if (!divideClass) FG_NOTSUP("exceptions need the prelude class DivideByZeroException");
        fcBufStr(&sb, fgMakeError(g, "Value makeDivideError(Value message)", divideClass, 1, "message"));
        fcBufStr(&sb, fgMakeError(g, "Value makeDestroyedError(Value message)", destroyedClass, 1, "message"));
        if (g->usesReflection && (reflectClass = (FgClassInfo*)fcDictGet(g->classes, "ReflectionException")) != NULL)
            fcBufStr(&sb, fgMakeError(g, "Value makeReflectError(Value message)", reflectClass, 1, "message"));
        else if (g->usesReflection) fcBufStr(&sb, "Value makeReflectError(Value message) { char t[500]; strToUtf8(message, t, sizeof t); fatal(t); }\n");
        if (!(accessClass = (FgClassInfo*)fcDictGet(g->classes, "AccessDeniedException"))) FG_NOTSUP("exceptions need the prelude class AccessDeniedException");
        fcBufStr(&sb, fgMakeError(g, "Value makeAccessError(Value message)", accessClass, 1, "message"));
        if (!(unitClass = (FgClassInfo*)fcDictGet(g->classes, "UnitMismatchException"))) FG_NOTSUP("exceptions need the prelude class UnitMismatchException");
        fcBufStr(&sb, fgMakeError(g, "Value makeUnitError(Value message, Value expected, Value actual)", unitClass, 3, "message, expected, actual"));
        fcBufStr(&sb, fgMakeError(g, "Value makeIndexError(Value message, int64_t index, int64_t length)", indexClass, 3, "message, Int(index), Int(length)"));
        fcBufStr(&sb, "Value makeClassError(const char* cls, Value message) {\n");
        fcBufStr(&sb, "    (void)cls; (void)message;\n");
        for (i = 0; i < g->errorClasses->count; i++) {
            const char* name = (const char*)g->errorClasses->items[i];
            FgClassInfo* errorClass = (FgClassInfo*)fcDictGet(g->classes, name);
            FcProto* ctor;
            if (!errorClass || !(ctor = fgFindConstructor(errorClass->rc, 1)) || ctor->paramCount != 1) continue;
            fcBufFmt(&sb, "    if (strcmp(cls, %s) == 0) {\n", CString(g, name));
            fcBufFmt(&sb, "        Value o = %s;\n", NewObjectCall(g, errorClass, "g_globalOwn"));
            fcBufFmt(&sb, "        %s\n", Nested(g, fcFmt(g->c, "%s(o, message);", ((FgFunc*)fcDictGet(g->funcByProto, ctor))->name)));
            fcBufStr(&sb, "        return o;\n");
            fcBufStr(&sb, "    }\n");
        }
        fcBufStr(&sb, "    return Undef();\n");
        fcBufStr(&sb, "}\n");
    }
    return fcBufDone(g->c, &sb);
}

/* (2: `Exception` or derived from it - its text is `Class: message`, docs/spec/STACKTRACE.md) */
static bool fgIsExceptionClass(const FcRtClass* rc) { for (; rc; rc = rc->base) if (streq0(rc->name, "Exception")) return true; return false; }
static const char* DescFlags(const FcRtClass* rc) { int f = (fgIsNoCopy(rc) ? 1 : 0) | (fgIsExceptionClass(rc) ? 2 : 0); return f == 3 ? "3u" : f == 2 ? "2u" : f == 1 ? "1u" : "0u"; }
static const char* RecordFunctions(FgGen* g, const FgClassInfo* cls, int id, FcBuf* sb);
/* The descriptor of a class (a row of FireClassDesc) */
static const char* DescRow(FgGen* g, const FgClassInfo* cls, int id, FcBuf* sb, FcBuf* columns, bool excTypes) {
    FcList* chain = fcListNew(g->c);
    const FcRtClass* rc;
    const char* destroy = "NULL";
    const char* toString;
    const char* types = "NULL";
    const char* record;
    const char* modular;
    FcProto* m;
    int message = -1, ticks = -1, i, slot, half;
    for (rc = cls->rc; rc; rc = rc->base)
        if (rc->destructor) fcListAdd(g->c, chain, (void*)fcFmt(g->c, "{ Value ov_ = ObjV(o); %s(ov_); }", ((FgFunc*)fcDictGet(g->funcByProto, rc->destructor))->name));
    if (chain->count > 0) {
        destroy = fcFmt(g->c, "%sfire_dtor_%d", Px, id);
        fcBufFmt(sb, "static void %s(Obj* o) { %s }\n", destroy, fgJoin(g->c, chain, " "));
    }
    m = FG_FIND(cls->rc, "ToString", 0).proto;
    toString = m && m->paramCount == 0 ? ((FgFunc*)fcDictGet(g->funcByProto, m))->name : "NULL";
    if ((g->usesExceptions || fgIsExceptionClass(cls->rc)) && (i = fgFieldIndexOf(g, cls->rc, "message")) >= 0) { FieldPlace(g, cls->rc, i, &slot, &half); message = slot; }
    if (g->needsTimeObj && (i = fgFieldIndexOf(g, cls->rc, "ticks")) >= 0) { FieldPlace(g, cls->rc, i, &slot, &half); ticks = slot; }
    if (g->usesExceptions && excTypes) {
        FcList* ids = fcListNew(g->c);
        for (i = 1; i < g->excTypes->count; i++) if (ClassIs(cls->rc, (const char*)g->excTypes->items[i])) fcListAdd(g->c, ids, (void*)fcFmt(g->c, "%d", i));
        if (ids->count > 0) {
            types = fcFmt(g->c, "kExcT%d", id);
            fcBufFmt(sb, "static const int32_t %s[] = {%s, -1};\n", types, fgJoin(g->c, ids, ", "));
        }
    }
    modular = Modular ? ModularDescColumns(g, cls->rc, columns, id) : "";
    record = cls->rc->isRecord ? RecordFunctions(g, cls, id, sb) : ", NULL, NULL, NULL";
    return fcFmt(g->c, "{%s, %d, %d, %d, %s, %s, %s%s, %s%s}", CString(g, cls->rc->name), FG_NAMES(cls->rc)->count, message, ticks, destroy, toString, types, modular, DescFlags(cls->rc), record);
}
/* The descriptors of the classes (FireClassDesc in fire_rt.h, one row per class id) */
static const char* ClassDescTable(FgGen* g) {
    FcBuf sb = {0};
    FcList* rows = fcListNew(g->c);
    FcBuf columns = {0};
    FcDict* byId = fcDictNew(g->c, FC_KEY_STR);
    FcDict* nameOfId = fcDictNew(g->c, FC_KEY_STR);
    int count = 0, id, i;
    fcBufStr(&sb, "");
    fcBufStr(&columns, "");
    for (i = 0; i < g->classList->count; i++) {
        FgClassInfo* c = (FgClassInfo*)g->classList->items[i];
        if (c->id + 1 > count) count = c->id + 1;
        fgIMapPut(g->c, byId, c->id, c);
    }
    /* (modules: every class of the libraries has a row - its chain answers the access checks of its code) */
    if (Modular && g->progBase > count) count = g->progBase;
    if (g->fixedIds) FC_DICT_EACH(g->fixedIds, e) fgIMapPut(g->c, nameOfId, FC_VALI(e->value) - 1, (void*)e->key);
    for (id = 0; id < count; id++) {
        void* found;
        FgClassInfo* cls = fgIMapTry(byId, id, &found) ? (FgClassInfo*)found : NULL;
        /* (an id without a class: a class of a library module that nothing makes objects of) */
        if (!cls) {
            void* unmade;
            FcRtClass* unmadeRc;
            if (Modular && fgIMapTry(nameOfId, id, &unmade) && (unmadeRc = fgClassOf(g, (const char*)unmade)) != NULL) {
                FcBuf c = {0};
                const FcRtClass* b;
                fcBufStr(&c, "");
                for (b = unmadeRc; b; b = b->base) fcBufFmt(&c, "%s, ", CString(g, b->name));
                fcBufFmt(&columns, "static const char* const kIsa%d[] = {%sNULL};\n", id, fcBufDone(g->c, &c));
                fcListAdd(g->c, rows, (void*)fcFmt(g->c, "{%s, %d, -1, -1, NULL, NULL, NULL, kIsa%d, NULL, NULL, NULL, %s, NULL, NULL, NULL}", CString(g, (const char*)unmade), FG_NAMES(unmadeRc)->count, id, DescFlags(unmadeRc)));
            }
            else fcListAdd(g->c, rows, (void*)(Modular ? "{\"?\", 0, -1, -1, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 0, NULL, NULL, NULL}" : "{\"?\", 0, -1, -1, NULL, NULL, NULL, 0, NULL, NULL, NULL}"));
            continue;
        }
        fcListAdd(g->c, rows, (void*)DescRow(g, cls, id, &sb, &columns, true));
    }
    if (g->libraryTablesCode.len) fcBufPut(&sb, g->libraryTablesCode.p, g->libraryTablesCode.len);
    if (columns.len) fcBufPut(&sb, columns.p, columns.len);
    fcBufFree(&columns);
    if (rows->count > 0) {
        FcBuf init = {0};
        fcBufFmt(&sb, "static const FireClassDesc kClassDescs[] = {\n    %s\n};\n", fgJoin(g->c, rows, ",\n    "));
        fcBufFmt(&init, "    g_classDescs = kClassDescs; g_classDescCount = %d;\n", rows->count);
        fcBufPut(&init, g->initCode.p ? g->initCode.p : "", g->initCode.len);
        fcBufFree(&g->initCode);
        g->initCode = init;
    }
    return fcBufDone(g->c, &sb);
}
/* A record (step 9): `==` and the hash of a key by its fields, the check of what it holds at the end of its construction. */
static const char* RecordFunctions(FgGen* g, const FgClassInfo* cls, int id, FcBuf* sb) {
    FcList* names = FG_NAMES(cls->rc);
    FcBuf eq = {0};
    FcBuf hash = {0};
    FcBuf check = {0};
    uint64_t seed = 1469598103934665603ULL;
    FcU16 u = {0};
    int i;
    fcBufStr(&eq, ""); fcBufStr(&hash, ""); fcBufStr(&check, "");
    for (i = 0; i < names->count; i++) {
        fcBufFmt(&eq, "    if (!recFieldEq(%s, %s)) return false;\n", FieldRead(g, cls->rc, i, "o"), FieldRead(g, cls->rc, i, "p"));
        fcBufFmt(&hash, "    h = (h ^ (uint64_t)recFieldHash(%s)) * 1099511628211ULL;\n", FieldRead(g, cls->rc, i, "o"));
        fcBufFmt(&check, "    f_ = %s; if (!recFieldOk(f_)) return recordFieldError(a, %s, f_);\n", FieldRead(g, cls->rc, i, "o"), CString(g, (const char*)names->items[i]));
    }
    fcU16FromFc8(cls->rc->name, &u);
    for (i = 0; i < u.len; i++) seed = (seed ^ u.p[i]) * 1099511628211ULL;
    fcU16Free(&u);
    fcBufFmt(sb, "static bool %sfire_req_%d(Value a, Value b) {\n    Obj* o = asObj(a); Obj* p = asObj(b); (void)o; (void)p;\n%s    return true;\n}\n", Px, id, fcBufDone(g->c, &eq));
    fcBufFmt(sb, "static int64_t %sfire_rh_%d(Value a) {\n    Obj* o = asObj(a); uint64_t h = %" PRIu64 "ULL; (void)o;\n%s    return (int64_t)h;\n}\n", Px, id, seed, fcBufDone(g->c, &hash));
    fcBufFmt(sb, "static bool %sfire_rck_%d(Value a) {\n    Obj* o = asObj(a); Value f_; (void)o; (void)f_;\n%s    return true;\n}\n", Px, id, fcBufDone(g->c, &check));
    return fcFmt(g->c, ", %sfire_req_%d, %sfire_rh_%d, %sfire_rck_%d", Px, id, Px, id, Px, id);
}
/* After OpCode.InitNext (`with`): the store that follows is in a block in which `obj` counts as being constructed */
static const char* InitOpen(FgGen* g, const char* obj) {
    if (!g->initNext) return "";
    g->initNext = false;
    return fcFmt(g->c, "{ RecInit wi_; wi_.o = %s.kind == K_Class && leafAlive(%s) ? asObj(%s) : NULL; wi_.next = g_recInit; g_recInit = &wi_; ", obj, obj, obj);
}
/* The class of channels `name` (Channel, ChannelEnd) as this generator writes its id: one it registered, or - a library - the class of core (another
 * module's), whether or not the library's own code makes one (a channel may come from the program). */
static FgClassInfo* ChannelClass(FgGen* g, const char* name) {
    FgClassInfo* ci = (FgClassInfo*)fcDictGet(g->classes, name);
    FcRtClass* rc;
    if (ci) return ci;
    if (LibraryRole && (rc = (FcRtClass*)fcDictGet(g->program->program->classes, name)) != NULL) return ForeignClass(g, name, rc);
    return NULL;
}
/* The C test "`v` is a channel or an end of a two-way one"; NULL when the program has none. */
static const char* ChannelLikeTest(FgGen* g, const char* v) {
    FcList* ids = fcListNew(g->c);
    static const char* const names[] = { "Channel", "ChannelEnd" };
    int i;
    if (!fgHasNameArgc(g, g->dispatchers, "__End", 0)) return NULL;
    for (i = 0; i < 2; i++) { FgClassInfo* ci = ChannelClass(g, names[i]); if (ci) fcListAdd(g->c, ids, (void*)fcFmt(g->c, "asObj(%s)->cls == %s", v, ClassIdExpr(g, ci))); }
    return ids->count == 0 ? NULL : fcFmt(g->c, "%s.kind == K_Class && leafAlive(%s) && (%s)", v, v, fgJoin(g->c, ids, " || "));
}
static void AddDispatcher(FgGen* g, const char* name, int argc);
/* `fire with` / a job with `with` references in a library: the thread gets an end of its own of a channel (`__End`) as in the program - the dispatcher
 * comes from here, not from RegisterTargets (a library registers its served targets only). */
static void NoteFireWith(FgGen* g) {
    if (LibraryRole && (fcDictHas(g->program->program->classes, "Channel") || fcDictHas(g->program->program->classes, "ChannelEnd")) && AnyClassHasMethod(g, "__End", 0))
        AddDispatcher(g, "__End", 0);
}
static const char* RecInitEnter(const FgClassInfo* cls) { return cls->rc->isRecord ? "RecInit ri_; ri_.o = asObj(o); ri_.next = g_recInit; g_recInit = &ri_; " : ""; }
static const char* RecInitLeave(const FgClassInfo* cls) { return cls->rc->isRecord ? "g_recInit = ri_.next; " : ""; }
