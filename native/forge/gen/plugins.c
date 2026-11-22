/* native/forge/gen/plugins.c - src/fire.Native/CppGenerator.Plugins.cs (commit 661e55b3): fire plugins (ROADMAP 9, phase D b) - what a program with linkage
 * "shared" says about itself for the plugins built for it (PluginHostInfo, `.firehost`), what the runtime checks a plugin against (g_pluginHost), and the
 * plugin itself: a library module of its own (PluginDeclarations, RegisterPluginTargets, PluginTrailer, GeneratePlugin). In the order of the C# file. */

#define FG_PLUGIN_FORMAT 1
#define FG_PLUGIN_HEAD_MARK "@FIRE_PLUGIN_HEAD@"

static const char* InsertAfterRuntime(FgGen* g, const char* text, const char* insert) {
    const char* include = "#include \"fire_rt.h\"\n";
    const char* at = strstr(text, include);
    if (!at) return fcFmt(g->c, "%s%s", insert, text);
    return fcFmt(g->c, "%s%s%s", fcStrN(g->c, text, (size_t)(at - text) + strlen(include)), insert, at + strlen(include));
}

/* The key of the configuration of the runtime a plugin must be built for: the generator, the switches of the program, the head of its files. */
static const char* PluginHeadKey(FcCtx* c, const char* head, const char* flags) {
    return fcStrN(c, fcSha256OfText(c, fcFmt(c, "fire-plugin %d %s\nflags %s\n%s", FG_PLUGIN_FORMAT, fgGeneratorIdentity, flags, head)), 40);
}

/* The program (linkage "shared"): what the runtime checks a plugin against (g_pluginHost). */
static const char* PluginHostCode(FgGen* g) {
    FcBuf sb = {0};
    FcList* names;
    const char* flags;
    int i;
    if (!g->openWorld || !g->sharedAbis || !g->libraryHead) return "";
    fcBufStr(&sb, "/* ---- fire plugins (ROADMAP 9 phase D b): what the runtime checks a plugin against - the configuration of the runtime, the interfaces of the modules ---- */\n");
    fcBufStr(&sb, "static const char* fire_moduleAbi(const char* name) {\n");
    for (i = 0; i < g->sharedAbis->count; i++) {
        const FgModuleSpec* m = ((FgModuleDep*)g->sharedAbis->items[i])->spec;
        fcBufFmt(&sb, "    if (strcmp(name, %s) == 0) return fire_abi_%s;\n", CString(g, m->name), m->id);
    }
    fcBufStr(&sb, "    return NULL;\n");
    fcBufStr(&sb, "}\n");
    names = fcListNew(g->c);
    for (i = 0; i < g->excTypes->count; i++) fcListAdd(g->c, names, (void*)CString(g, (const char*)g->excTypes->items[i]));
    fcBufFmt(&sb, "static const char* const kPluginExcNames[] = {%s};\n", fgJoin(g->c, names, ", "));
    flags = fgFlagsText(g->c, g->hasForced ? g->forced : CurrentFlags(g));
    fcBufFmt(&sb, "static const FirePluginHost kPluginHost = {%s, fire_moduleAbi, kPluginExcNames, %du, %s};\n", CString(g, PluginHeadKey(g->c, g->libraryHead, flags)), g->excTypes->count,
        g->dllModules ? "fire_hostFind" : "NULL");
    fcBufStr(&g->initCode, "    g_pluginHost = &kPluginHost;   // (it can load fire plugins)\n");
    fcBufFmt(&g->initCode, "    g_arrClassCount = %d;   // (a plugin adds the entries of its arrays of a class)\n", fcDictCount(g->arrClasses));
    return fcBufDone(g->c, &sb);
}

/* The program (linkage "shared"): what the plugins built for it need to know (written next to it as `.firehost`). libraryFiles: FgModuleSpec* -> file. */
static FgPluginHostInfo* HostInfo(FgGen* g, FcDict* libraryFiles, const char* runtimeFile) {
    FgPluginHostInfo* h;
    const char* flags;
    int i;
    if (!g->openWorld || !g->sharedAbis || !g->libraryHead) return NULL;
    flags = fgFlagsText(g->c, g->hasForced ? g->forced : CurrentFlags(g));
    h = (FgPluginHostInfo*)fcAlloc(g->c, sizeof *h);
    h->format = FG_PLUGIN_FORMAT; h->target = g->target->name; h->flags = flags; h->head = PluginHeadKey(g->c, g->libraryHead, flags); h->runtime = runtimeFile;
    h->modules = fcListNew(g->c);
    for (i = 0; i < g->sharedAbis->count; i++) {
        FgModuleDep* d = (FgModuleDep*)g->sharedAbis->items[i];
        FgPluginHostModule* m = (FgPluginHostModule*)fcAlloc(g->c, sizeof *m);
        const char* f = (const char*)fcDictGet(libraryFiles, d->spec);
        m->name = d->spec->name; m->abi = d->abi; m->library = f ? f : "";
        fcListAdd(g->c, h->modules, m);
    }
    return h;
}
/* PluginHostInfo.ToJson: JsonSerializer.Serialize with WriteIndented */
static const char* fgHostInfoJson(FcCtx* c, const FgPluginHostInfo* h) {
    FcBuf b = {0};
    int i;
    fcBufFmt(&b, "{\n  \"Format\": %d,\n  \"Target\": ", h->format); fgJsonStr(&b, h->target);
    fcBufStr(&b, ",\n  \"Flags\": "); fgJsonStr(&b, h->flags);
    fcBufStr(&b, ",\n  \"Head\": "); fgJsonStr(&b, h->head);
    fcBufStr(&b, ",\n  \"Runtime\": "); fgJsonStr(&b, h->runtime);
    fcBufStr(&b, ",\n  \"Modules\": [");
    for (i = 0; i < h->modules->count; i++) {
        const FgPluginHostModule* m = (const FgPluginHostModule*)h->modules->items[i];
        fcBufStr(&b, i ? ",\n    {\n      \"Name\": " : "\n    {\n      \"Name\": "); fgJsonStr(&b, m->name);
        fcBufStr(&b, ",\n      \"Abi\": "); fgJsonStr(&b, m->abi);
        fcBufStr(&b, ",\n      \"Library\": "); fgJsonStr(&b, m->library);
        fcBufStr(&b, "\n    }");
    }
    fcBufStr(&b, h->modules->count ? "\n  ]\n}" : "]\n}");
    return fcBufDone(c, &b);
}

/* ---- the plugin (a library module) ---- */
typedef struct FgPluginInit { const char* cls; const char* field; FgFunc* init; } FgPluginInit;

/* A plugin: the tables a program defines for a library - here its own, filled by the runtime when it registers the plugin (by name). */
static const char* PluginDeclarations(FgGen* g) {
    FcBuf sb = {0};
    const char* id = g->self->id;
    FcList* used = fgUsedModulesByIndex(g);
    int i, nsrc = fcDictCount(g->self->sources);
    fcBufFmt(&sb, "/* ---- plugin %s (ROADMAP 9 phase D b): the tables a program defines for a library module - its own, filled when it is registered ---- */\n", g->self->name);
    fcBufFmt(&sb, "static uint32_t fire_mb_%s = 0;\n", id);
    for (i = 0; i < used->count; i++) {
        const FgModuleSpec* m = (const FgModuleSpec*)used->items[i];
        fcBufStr(&sb, HostDecl(g, fcFmt(g->c, "fire_mb_%s", m->id), fcFmt(g->c, "extern const uint32_t fire_mb_%s;", m->id)));
    }
    fcBufFmt(&sb, "static uint16_t fire_units_%s[%d];\n", id, g->units->count > 1 ? g->units->count : 1);
    fcBufFmt(&sb, "static uint8_t fire_enums_%s[%d];\n", id, g->enumList->count + 1);
    fcBufFmt(&sb, "static int32_t fire_exct_%s[%d];\n", id, g->excTypes->count > 1 ? g->excTypes->count : 1);
    fcBufFmt(&sb, "static uint32_t fire_arrc_%s[%d];\n", id, fcDictCount(g->arrClasses) + 1);
    /* (a debug build: the indexes of its sources among the program's, given when it is registered - fire_dbgPlugin) */
    if (DebugBuild) fcBufFmt(&sb, "static int32_t fire_dbgsrc_%s[%d];\n", id, nsrc > 1 ? nsrc : 1);
    else fcBufFmt(&sb, "FIRE_MAYBE_UNUSED static const int32_t fire_dbgsrc_%s[%d] = {0};\n", id, nsrc > 1 ? nsrc : 1);
    return fcBufDone(g->c, &sb);
}

/* CompiledProgram.StaticInitSequence: the static fields in the order their initialisers run at the start of the top level ("class\x01field" -> rank + 1) */
static FcDict* fgStaticInitSequence(FcCtx* c, const FcCompiled* program) {
    FcDict* rank = fcDictNew(c, FC_KEY_STR);
    FgCode code;
    volatile bool ok = true;
    int i, n = 0;
    memset(&code, 0, sizeof code);
    FC_TRY(c) { code = fgDecode(c, program->topLevel); } FC_CATCH(c) { ok = false; } FC_END_TRY;   /* (code this reader does not know: the order of the declarations) */
    if (!ok) return rank;
    for (i = 0; i < code.n; i++) {
        const char* key;
        if (code.p[i].op != OP_SetStaticFieldOnInit) continue;
        key = fcFmt(c, "%s\x01%s", fgConstStr(program->topLevel, code.p[i].a[0]), fgConstStr(program->topLevel, code.p[i].a[1]));
        if (!fcDictHas(rank, key)) fcDictPut(rank, key, FC_IVAL(n + 1));   /* (IndexOf: the first) */
        n++;
    }
    return rank;
}

/* A plugin, while it is generated: what its descriptors call (the destructors and ToString of the chains of its classes) and the function with the static
 * initialisers of its classes. */
static void RegisterPluginTargets(FgGen* g) {
    FcList* order;
    FcDict* sequence;
    int i, k;
    if (!g->plugin) return;
    for (i = 0; i < g->self->classes->count; i++) {
        FcRtClass* rc = FindClass(g, (const char*)g->self->classes->items[i]);
        FcRtClass* c;
        FcProto* toString;
        for (c = rc; c; c = c->base) if (c->destructor) GetFunc(g, c->destructor, FK_Dtor, 0);
        toString = FG_FIND(rc, "ToString", 0).proto;
        if (toString && toString->paramCount == 0 && !toString->isStatic) GetFunc(g, toString, FK_Method, 0);
    }
    /* (the program has JSON: the units and type arguments of the fields of its classes are in its tables) */
    if (g->usesJson)
        for (i = 0; i < g->self->classes->count; i++) {
            FcList* fields = JsonFieldsOf(g, FindClass(g, (const char*)g->self->classes->items[i]));
            for (k = 0; k < fields->count; k++) {
                FgJsonField* field = (FgJsonField*)fields->items[k];
                FcList* args;
                int a;
                UnitId(g, field->unit);
                args = JsonTypeArgTexts(g, field->type);
                for (a = 0; a < args->count; a++) TArgRef(g, (const char*)args->items[a]);
            }
        }
    /* the static initialisers of its classes (the compiler puts them at the start of a program's top level): what they make belongs to the global scope
     * (in the order of their dependencies, as the top level has them - CompiledProgram.StaticInitSequence) */
    g->pluginStaticInits = fcListNew(g->c);
    /* (of the whole program: the view of a library module has no top level - GenerateLibrary) */
    sequence = fgStaticInitSequence(g->c, g->pluginProgram ? g->pluginProgram : g->program->program);
    order = fcListNew(g->c);
    FC_DICT_EACH(g->program->program->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        if (!fcListHasStr(g->self->classes, rc->name)) continue;
        for (k = 0; k < rc->staticFieldNames->count; k++) {
            FgPluginInit* pi = (FgPluginInit*)fcAlloc(g->c, sizeof *pi);
            pi->cls = rc->name; pi->field = (const char*)rc->staticFieldNames->items[k];
            pi->init = (FgFunc*)rc->staticFieldInits->items[k];   /* (the proto for now) */
            fcListAdd(g->c, order, pi);
        }
    }
    /* (OrderBy the rank - stable; not in the sequence: int.MaxValue) */
    for (i = 1; i < order->count; i++)
        for (k = i; k > 0; k--) {
            FgPluginInit* a = (FgPluginInit*)order->items[k - 1];
            FgPluginInit* b = (FgPluginInit*)order->items[k];
            void* ra = fcDictGet(sequence, fcFmt(g->c, "%s\x01%s", a->cls, a->field));
            void* rb = fcDictGet(sequence, fcFmt(g->c, "%s\x01%s", b->cls, b->field));
            long xa = ra ? (long)FC_VALI(ra) : 0x7fffffffL, xb = rb ? (long)FC_VALI(rb) : 0x7fffffffL;
            if (xa <= xb) break;
            order->items[k - 1] = b; order->items[k] = a;
        }
    for (i = 0; i < order->count; i++) {
        FgPluginInit* pi = (FgPluginInit*)order->items[i];
        pi->init = GetFunc(g, (FcProto*)pi->init, FK_Init, 0);
        fcListAdd(g->c, g->pluginStaticInits, pi);
    }
}

/* The flags as DescribeFlags names them: those `wanted` has and `has` has not. */
static const char* DescribeFlags(FcCtx* c, uint32_t wanted, uint32_t has) {
    static const char* const names[] = { "exceptions", "threads", "actors", "reflection", "probes", "element pointers", "try depth", "global ownership", "take upwards",
        "take enumerate", "json", "time objects", "string calls", "graphics", "windows" };
    FcList* l = fcListNew(c);
    int i;
    for (i = 0; i < MF_Count && i < (int)(sizeof names / sizeof names[0]); i++) if (FG_FLAG(wanted, i) && !FG_FLAG(has, i)) fcListAdd(c, l, (void*)names[i]);
    return fgJoin(c, l, ", ");
}

/* A debug plugin (fire_rt.h FirePlugin, fire_debug.h fire_dbgPlugin): its sources - file and hash, in the order of fire_dbgsrc_<p> -, the table the runtime fills
 * with their indexes among the program's sources, and its classes for the debugger (the names of their fields). Not a debug build: none. */
static const char* PluginDebugTables(FgGen* g, FcBuf* sb) {
    FcList* files = g->program->sourceFiles;
    FcList* hashes = g->program->sourceHashes;
    FcList* fl = fcListNew(g->c);
    FcList* hl = fcListNew(g->c);
    FcList* rows = fcListNew(g->c);
    int n, *sources, i;
    if (!DebugBuild) return "0u, NULL, NULL, NULL, NULL";
    sources = fgISetItems(g->c, g->self->sources, true, &n);
    for (i = 0; i < n; i++) {
        int s = sources[i];
        fcListAdd(g->c, fl, (void*)(files && s < files->count && files->items[s] ? CString(g, (const char*)files->items[s]) : "NULL"));
        fcListAdd(g->c, hl, (void*)(hashes && s < hashes->count && hashes->items[s] ? CString(g, (const char*)hashes->items[s]) : "NULL"));
    }
    fcBufFmt(sb, "static const char* const kPlSrcFiles[] = {%s};\n", n ? fgJoin(g->c, fl, ", ") : "NULL");
    fcBufFmt(sb, "static const char* const kPlSrcHashes[] = {%s};\n", n ? fgJoin(g->c, hl, ", ") : "NULL");
    for (i = 0; i < g->self->classes->count; i++) {
        FgClassInfo* cls = RegisterClass(g, (const char*)g->self->classes->items[i]);
        FcList* fields = FG_NAMES(cls->rc);
        if (fields->count > 0) {
            FcList* names = fcListNew(g->c);
            int k;
            for (k = 0; k < fields->count; k++) fcListAdd(g->c, names, (void*)CString(g, (const char*)fields->items[k]));
            fcBufFmt(sb, "static const char* const kPlDbgFields%d[] = {%s};\n", i, fgJoin(g->c, names, ", "));
        }
        fcListAdd(g->c, rows, (void*)fcFmt(g->c, "{%s, %d, %s}", CString(g, cls->rc->name), fields->count, fields->count > 0 ? fcFmt(g->c, "kPlDbgFields%d", i) : "NULL"));
    }
    fcBufFmt(sb, "static const DbgClass kPlDbgClasses[] = {%s};\n", rows->count ? fgJoin(g->c, rows, ", ") : "{\"\", 0, NULL}");
    fcBufFmt(sb, "static const void* fire_pl_dbgClass(uint32_t k) { return k < %du ? (const void*)&kPlDbgClasses[k] : NULL; }\n", g->self->classes->count);
    return fcFmt(g->c, "%du, kPlSrcFiles, kPlSrcHashes, fire_dbgsrc_%s, fire_pl_dbgClass", n, g->self->id);
}

/* A plugin: the tables of its units (the program's numbers for them are found by dimensions, scale and symbol when it is registered, fire_rt.h firePluginUnits) -
 * the dimensions by name, a row per unit over them, the conversion functions and the units a text may name. The fields of FirePlugin from `nunits` on. */
static const char* PluginUnits(FgGen* g, FcBuf* sb) {
    const char* id = g->self->id;
    FcDict* dimSet = fcDictNew(g->c, FC_KEY_STR);
    FcList* dimNames;
    const char* dims = "NULL";
    const char* conv = "0u, NULL, NULL, NULL";
    const char* text = "0u, NULL";
    int i, k;
    for (i = 0; i < g->units->count; i++) {
        const FcU* u = (const FcU*)g->units->items[i];
        for (k = 0; k < u->u.dimCount; k++) fcSetAdd(dimSet, u->u.dimSym[k]);
    }
    dimNames = fgSortedKeys(g->c, dimSet, true);
    if (dimNames->count > FG_SEPARATE_DIMS) FG_NOTSUP("the plugin uses %d dimensions of units; at most %d", dimNames->count, FG_SEPARATE_DIMS);
    if (dimNames->count > 0) {
        FcList* names = fcListNew(g->c);
        for (i = 0; i < dimNames->count; i++) fcListAdd(g->c, names, (void*)CString(g, (const char*)dimNames->items[i]));
        dims = "kPlDims";
        fcBufFmt(sb, "static const char* const kPlDims[] = {%s};\n", fgJoin(g->c, names, ", "));
    }
    fcBufStr(sb, "static const UnitInit kPlUnits[] = {\n");
    for (i = 0; i < g->units->count; i++) {
        const FcU* u = (const FcU*)g->units->items[i];
        FcList* exps = fcListNew(g->c);
        const char* display = u->display ? CString(g, u->display) : "NULL";
        int cv = -1, ul = -1;
        if (dimNames->count == 0) fcListAdd(g->c, exps, (void*)"0");
        for (k = 0; k < dimNames->count; k++) { int at = fcUnitDim(&u->u, (const char*)dimNames->items[k]); fcListAdd(g->c, exps, (void*)fcFmt(g->c, "%d", at >= 0 ? u->u.dimExp[at] : 0)); }
        if (u->u.converter) cv = ((FgUnitConv*)fcDictGet(g->unitConverters, u->u.converter))->index;
        if (u->u.unitlessConverter) ul = ((FgUnitConv*)fcDictGet(g->unitConverters, u->u.unitlessConverter))->index;
        fcBufFmt(sb, "    {{%s}, %s, %s, %s, %d, %d},\n", fgJoin(g->c, exps, ", "), DoubleLiteral(g, u->u.scale, false), display, DoubleLiteral(g, u->u.offset, false), cv, ul);
    }
    fcBufStr(sb, "};\n");
    if (fcDictCount(g->unitConverters) > 0) {
        int n = fcDictCount(g->unitConverters), at;
        FcList* tos = fcListNew(g->c);
        FcList* froms = fcListNew(g->c);
        FcList* owns = fcListNew(g->c);
        for (at = 0; at < n; at++)
            FC_DICT_EACH(g->unitConverters, e) {
                FgUnitConv* cv = (FgUnitConv*)e->value;
                if (cv->index != at) continue;
                fcListAdd(g->c, tos, (void*)cv->to->name);
                fcListAdd(g->c, froms, (void*)cv->from->name);
                /* (the hidden class of a unit the plugin declares is its own: the unit is new in the program - one of a library is the program's when it has it) */
                fcListAdd(g->c, owns, (void*)(fcListHasStr(g->self->classes, (const char*)e->key) ? "1" : "0"));
            }
        fcBufFmt(sb, "static const UnitConvFn kPlUnitTo[] = {%s};\n", fgJoin(g->c, tos, ", "));
        fcBufFmt(sb, "static const UnitConvFn kPlUnitFrom[] = {%s};\n", fgJoin(g->c, froms, ", "));
        fcBufFmt(sb, "static const uint8_t kPlUnitOwn[] = {%s};\n", fgJoin(g->c, owns, ", "));
        conv = fcFmt(g->c, "%du, kPlUnitTo, kPlUnitFrom, kPlUnitOwn", tos->count);
    }
    if (g->usesStringCall || g->program->program->customUnits->count > 0) {
        /* the units a text may name (as the program's kTextUnits): those of fire, then those of the plugin and its libraries by symbol and name */
        FcList* rows = fcListNew(g->c);
        size_t s;
        for (s = 0; s < sizeof fgBuiltInSymbols / sizeof fgBuiltInSymbols[0]; s++)
            fcListAdd(g->c, rows, (void*)fcFmt(g->c, "{%s, %d, %s}", CString(g, fgBuiltInSymbols[s]), UnitId(g, fcUParse(g->c, fgBuiltInSymbols[s])), fgBuiltInPrefixable(fgBuiltInSymbols[s]) ? "true" : "false"));
        for (i = 0; i < g->program->program->customUnits->count; i++) {
            const FcCustomUnit* own = (const FcCustomUnit*)g->program->program->customUnits->items[i];
            int u = UnitId(g, fgUnitOfCustom(g, own));
            fcListAdd(g->c, rows, (void*)fcFmt(g->c, "{%s, %d, %s}", CString(g, own->symbol), u, own->prefixed ? "true" : "false"));
            if (!streq0(own->name, own->symbol)) fcListAdd(g->c, rows, (void*)fcFmt(g->c, "{%s, %d, false}", CString(g, own->name), u));
        }
        fcBufFmt(sb, "static const TextUnit kPlTextUnits[] = {\n    %s\n};\n", fgJoin(g->c, rows, ",\n    "));
        text = fcFmt(g->c, "%du, kPlTextUnits", rows->count);
    }
    return fcFmt(g->c, "%du, kPlUnits, fire_units_%s, %du, %s, %s, %s", g->units->count, id, dimNames->count, dims, conv, text);
}

/* A plugin whose program has JSON: the rows of its classes and its type arguments (FirePlugin.jsonAt, ...: the program's jsonClassAt asks for a class after its
 * own); `fixups` gets the units of their fields (set when it is registered). The fields of FirePlugin from `jsonAt` on. */
static const char* PluginJson(FgGen* g, FcBuf* sb, FcList* fixups) {
    FcList* rows;
    FcList* targs;
    int i, n;
    if (!g->usesJson) return "NULL, NULL, NULL, NULL";
    rows = fcListNew(g->c);
    for (i = 0; i < g->self->classes->count; i++) fcListAdd(g->c, rows, (void*)JsonRow(g, RegisterClass(g, (const char*)g->self->classes->items[i]), fcFmt(g->c, "kPlJsonF%d", i), 0, sb, fixups));
    fcBufFmt(sb, "static const JsonClassInfo kPlJson[] = {\n    %s\n};\n", fgJoin(g->c, rows, ",\n    "));
    fcBufStr(sb, "static const void* fire_pl_jsonAt(uint32_t k) { return &kPlJson[k]; }\n");
    fcBufStr(sb, "static int32_t fire_pl_jsonNamed(const char* name) {\n");
    fcBufFmt(sb, "    for (uint32_t k = 0; k < %du; k++) if (strcmp(kPlJson[k].name, name) == 0) return (int32_t)k;\n", rows->count);
    fcBufStr(sb, "    return -1;\n");
    fcBufStr(sb, "}\n");
    /* _targIds.OrderBy(kv => kv.Value) */
    targs = fcListNew(g->c);
    n = fcDictCount(g->targIds);
    for (i = 0; i < n; i++) FC_DICT_EACH(g->targIds, e) if (FC_VALI(e->value) - 1 == i) fcListAdd(g->c, targs, e);
    fcBufStr(sb, "static const char* fire_pl_targText(const TArg* t) {\n");
    for (i = 0; i < targs->count; i++) { FcDictEntry* e = (FcDictEntry*)targs->items[i]; fcBufFmt(sb, "    if (t == &%sta_%d) return %s;\n", Px, FC_VALI(e->value) - 1, CString(g, (const char*)e->key)); }
    if (targs->count == 0) fcBufStr(sb, "    (void)t;\n");
    fcBufStr(sb, "    return NULL;\n");
    fcBufStr(sb, "}\n");
    fcBufStr(sb, "static const TArg* fire_pl_targFor(const char* text) {\n");
    for (i = 0; i < targs->count; i++) { FcDictEntry* e = (FcDictEntry*)targs->items[i]; fcBufFmt(sb, "    if (strcmp(text, %s) == 0) return &%sta_%d;\n", CString(g, (const char*)e->key), Px, FC_VALI(e->value) - 1); }
    if (targs->count == 0) fcBufStr(sb, "    (void)text;\n");
    fcBufStr(sb, "    return NULL;\n");
    fcBufStr(sb, "}\n");
    return "fire_pl_jsonAt, fire_pl_jsonNamed, fire_pl_targText, fire_pl_targFor";
}

/* A plugin: the end of its file - the descriptors of its classes, its enums and catch types by name, its reflection rows and `fire_module_info`. */
static const char* PluginTrailer(FgGen* g) {
    FcBuf sb = {0};
    FcBuf columns = {0};
    FcBuf rowCode = {0};
    FcList* rows;
    FcList* l;
    FcList* jsonFixups;
    FcList* extended;
    FcList* ext;
    const char* id;
    const char* depNames = "NULL";
    const char* depAbis = "NULL";
    const char* enums = "NULL";
    const char* units;
    const char* rf = "NULL";
    const char* json;
    const char* exts = "0u, NULL, NULL";
    const char* debug;
    const char* arr;
    const char* link = "NULL";
    const char* dllx;
    const char* share = "NULL";
    int i, k;
    if (!g->plugin) return "";
    id = g->self->id;
    fcBufFmt(&sb, "/* ---- plugin %s: what the runtime registers (fire_module_info, fire_rt.h FirePlugin) ---- */\n", g->self->name);
    fcBufStr(&columns, "");
    fcBufStr(&rowCode, "");
    rows = fcListNew(g->c);
    for (k = 0; k < g->self->classes->count; k++) fcListAdd(g->c, rows, (void*)DescRow(g, RegisterClass(g, (const char*)g->self->classes->items[k]), k, &rowCode, &columns, false));
    if (g->libraryTablesCode.len) fcBufPut(&sb, g->libraryTablesCode.p, g->libraryTablesCode.len);
    fcBufPut(&sb, rowCode.p, rowCode.len);
    fcBufPut(&sb, columns.p, columns.len);
    fcBufFmt(&sb, "static const FireClassDesc kPlDescs[] = {\n    %s\n};\n", fgJoin(g->c, rows, ",\n    "));
    fcBufStr(&sb, "static void fire_pl_fill(FireClassDesc* rows) { memcpy(rows, kPlDescs, sizeof kPlDescs); }\n");
    l = fcListNew(g->c);
    for (i = 0; i < g->self->classes->count; i++) fcListAdd(g->c, l, (void*)CString(g, (const char*)g->self->classes->items[i]));
    fcBufFmt(&sb, "static const char* const kPlClasses[] = {%s};\n", fgJoin(g->c, l, ", "));
    if (g->pluginDeps->count > 0) {
        FcList* names = fcListNew(g->c);
        FcList* abis = fcListNew(g->c);
        depNames = "kPlDepNames"; depAbis = "kPlDepAbis";
        for (i = 0; i < g->pluginDeps->count; i++) {
            const FgModuleDep* d = (const FgModuleDep*)g->pluginDeps->items[i];
            fcListAdd(g->c, names, (void*)CString(g, d->module));
            fcListAdd(g->c, abis, (void*)CString(g, d->abi));
        }
        fcBufFmt(&sb, "static const char* const kPlDepNames[] = {%s};\n", fgJoin(g->c, names, ", "));
        fcBufFmt(&sb, "static const char* const kPlDepAbis[] = {%s};\n", fgJoin(g->c, abis, ", "));
    }
    l = fcListNew(g->c);
    for (i = 0; i < g->excTypes->count; i++) fcListAdd(g->c, l, (void*)CString(g, (const char*)g->excTypes->items[i]));
    fcBufFmt(&sb, "static const char* const kPlExc[] = {%s};\n", fgJoin(g->c, l, ", "));
    if (g->enumList->count > 0) {
        FcList* erows = fcListNew(g->c);
        enums = "kPlEnums";
        for (i = 0; i < g->enumList->count; i++) {
            const FcEnumType* e = (const FcEnumType*)g->enumList->items[i];
            FcList* members = fcListNew(g->c);
            for (k = 0; k < e->names->count; k++) {
                int64_t v = *(int64_t*)e->values->items[k];
                fcListAdd(g->c, members, (void*)fcFmt(g->c, "{%s, %s}", CString(g, (const char*)e->names->items[k]), v == INT64_MIN ? "INT64_MIN" : fcFmt(g->c, "%" PRId64 "LL", v)));
            }
            if (members->count == 0) fcListAdd(g->c, members, (void*)"{\"\", 0}");
            fcBufFmt(&sb, "static const FireEnumMember kPlEnumM%d[] = {%s};\n", i, fgJoin(g->c, members, ", "));
            fcListAdd(g->c, erows, (void*)fcFmt(g->c, "{%s, %d, kPlEnumM%d, %d}", CString(g, e->name), e->isFlags ? 1 : 0, i, e->names->count));
        }
        fcBufFmt(&sb, "static const FireEnum kPlEnums[] = {%s};\n", fgJoin(g->c, erows, ", "));
    }
    units = PluginUnits(g, &sb);
    if (g->usesReflection) {
        l = fcListNew(g->c);
        rf = "kPlRf";
        for (i = 0; i < g->self->classes->count; i++) fcListAdd(g->c, l, (void*)fcFmt(g->c, "&%s", RfRowName(g, ModId(g), FindClass(g, (const char*)g->self->classes->items[i]))));
        fcBufFmt(&sb, "static const void* const kPlRf[] = {%s};\n", fgJoin(g->c, l, ", "));
    }
    jsonFixups = fcListNew(g->c);
    json = PluginJson(g, &sb, jsonFixups);
    /* (what a static initialiser made belongs to the main program's global scope; loaded in a fire thread it goes there like `x.TakeGlobal()` of the thread, in a
     * section - not to the thread's global scope, which its end takes along; VM.Plugins.cs and vm_plugin.c the same) */
    if (g->pluginStaticInits && g->pluginStaticInits->count > 0) {
        fcBufStr(&sb, "static void fire_pl_static(Value* slot, Value r) {\n");
        fcBufStr(&sb, "#ifdef FIRE_THREADS\n");
        fcBufStr(&sb, "    if (g_isThread) { bool ss = sectionScopeEnter(true); if (mainGlobalsKind(r, true) == 1) takeGlobalMain(ownedOf(r)); adopt(r, g_domainRoot); *slot = r; retain(r); sectionScopeExit(ss); return; }\n");
        fcBufStr(&sb, "#endif\n");
        fcBufStr(&sb, "    adopt(r, g_globalOwn); *slot = r; retain(r);\n");
        fcBufStr(&sb, "}\n");
    }
    fcBufStr(&sb, "static void fire_pl_init(void) {\n");
    for (i = 0; i < jsonFixups->count; i++) fcBufFmt(&sb, "    %s\n", (const char*)jsonFixups->items[i]);   /* (the units of the fields JSON reads: the program's, known now) */
    for (i = 0; g->pluginStaticInits && i < g->pluginStaticInits->count; i++) {
        const FgPluginInit* pi = (const FgPluginInit*)g->pluginStaticInits->items[i];
        fcBufFmt(&sb, "    { Value r = %s(Undef()); %sfire_pl_static(&%s, r); }   // %s.%s\n", pi->init->name, g->usesExceptions ? "if (g_unwind.active) return; " : "",
            StaticFieldVar(g, pi->cls, pi->field), pi->cls, pi->field);
    }
    fcBufStr(&sb, "}\n");
    /* (the first `fire` - or its registration after it: what its static fields hold is shared with the fire threads from then on, as the program's own - fire_rt.h
     * firePluginShareGlobals, called by the program's fire_shareGlobals) */
    if (g->pluginStaticInits && g->pluginStaticInits->count > 0) {
        share = "fire_pl_share";
        fcBufStr(&sb, "static void fire_pl_share(void) {\n");
        for (i = 0; i < g->pluginStaticInits->count; i++) {
            const FgPluginInit* pi = (const FgPluginInit*)g->pluginStaticInits->items[i];
            fcBufFmt(&sb, "    refShare(%s);\n", StaticFieldVar(g, pi->cls, pi->field));
        }
        fcBufStr(&sb, "}\n");
    }
    /* its `class extends` of classes it does not declare (of the program, of libraries, base types): their tables by the name of the class (the runtime adds them to
     * what an object of such a class finds by name - fireFindMethod) (`class extends int` where nothing else extends int: `$int` is a class of the plugin - its
     * table is an extension all the same) */
    extended = fcListNew(g->c);
    ext = ExtServed(g);
    for (i = 0; i < ext->count; i++) fcListAdd(g->c, extended, ((FgClassInfo*)ext->items[i])->rc);
    for (i = 0; i < g->self->classes->count; i++) if (((const char*)g->self->classes->items[i])[0] == '$') fcListAdd(g->c, extended, FindClass(g, (const char*)g->self->classes->items[i]));
    if (extended->count > 0) {
        FcList* names = fcListNew(g->c);
        FcList* tables = fcListNew(g->c);
        for (i = 0; i < extended->count; i++) {
            const FcRtClass* rc = (const FcRtClass*)extended->items[i];
            fcListAdd(g->c, names, (void*)CString(g, rc->name));
            fcListAdd(g->c, tables, (void*)fcFmt(g->c, "&%s", MembersName(g, ModId(g), rc)));
        }
        fcBufFmt(&sb, "static const char* const kPlExtClasses[] = {%s};\n", fgJoin(g->c, names, ", "));
        fcBufFmt(&sb, "static const FireMembers* const kPlExtTables[] = {%s};\n", fgJoin(g->c, tables, ", "));
        exts = fcFmt(g->c, "%du, kPlExtClasses, kPlExtTables", extended->count);
    }
    debug = PluginDebugTables(g, &sb);
    /* (its arrays of a class: the entries the runtime gives them in g_arrClasses - fire_arrc_<p>) */
    arr = fcFmt(g->c, "0u, NULL, fire_arrc_%s", id);
    if (fcDictCount(g->arrClasses) > 0) { fcBufStr(&sb, ArrClassTable(g)); arr = fcFmt(g->c, "%du, %skArrClasses, fire_arrc_%s", fcDictCount(g->arrClasses), Px, id); }
    if (g->dll) {
        link = fcFmt(g->c, "fire_link_%s", id);
        fcBufFmt(&sb, "__declspec(dllexport) const char* fire_link_%s(const void* (*find)(const char*));\n", id);
    }
    dllx = g->dll ? "__declspec(dllexport) " : "";
    fcBufFmt(&sb, "%sextern const FirePlugin fire_module_info;\n", dllx);
    fcBufFmt(&sb, "%sconst FirePlugin fire_module_info = {FIRE_PLUGIN_FORMAT, %s, \"%s\", %du, %s, %s, %du, kPlClasses, &fire_mb_%s, fire_pl_fill, %du, %s, fire_enums_%s, %du, kPlExc, fire_exct_%s, %s, fire_pl_init, %s, %s, %s, %s, %s, %s, %s};\n",
        dllx, CString(g, g->self->name), FG_PLUGIN_HEAD_MARK, g->pluginDeps->count, depNames, depAbis, g->self->classes->count, id, g->enumList->count, enums, id, g->excTypes->count, id, rf, link, units, json, exts, debug, arr, share);
    return fcBufDone(g->c, &sb);
}

/* A plugin's `class extends` of a class of the program or a library (or of a base type): it may add methods and properties with bodies - the objects of that class
 * are made by the program, they have no room for a field the plugin adds (nor does the class a constructor, destructor, static field or operator the program would
 * have to know). NULL: fine; else what is wrong (the text of the build error). pluginSources: HashSet<int>. */
static const char* PluginExtensionProblem(FcCtx* c, const FcLinked* program, const FcDict* pluginSources) {
    FcList* all = fcListNew(c);
    int i, k;
    FC_DICT_EACH(program->program->classes, e) fcListAdd(c, all, e->value);
    if (all->count > 1) qsort(all->items, (size_t)all->count, sizeof(void*), fgCmpRtClassName);
    for (i = 0; i < all->count; i++) {
        const FcRtClass* rc = (const FcRtClass*)all->items[i];
        const char* target = rc->name;
        if (!rc->decl || fgISetHas(c, pluginSources, rc->decl->h.source)) continue;
        if (rc->name[0] == '$' && (streq0(rc->name + 1, "string") || streq0(rc->name + 1, "char") || streq0(rc->name + 1, "int") || streq0(rc->name + 1, "float")
            || streq0(rc->name + 1, "bool") || streq0(rc->name + 1, "array"))) target = rc->name + 1;
        for (k = 0; rc->decl->members && k < rc->decl->members->count; k++) {
            const FcStmt* member = (const FcStmt*)rc->decl->members->items[k];
            const char* what = NULL;
            if (!fgISetHas(c, pluginSources, member->source)) continue;
            if (member->kind == SK_FieldDecl) what = fcFmt(c, "the field '%s'", ((const FcFieldDecl*)member)->name);
            else if (member->kind == SK_ConstructorDecl) what = "a constructor";
            else if (member->kind == SK_DestructorDecl) what = "a destructor";
            else if (member->kind == SK_MethodDecl) {
                const char* n = ((const FcMethodDecl*)member)->name;
                if (fcStartsWith(n, "operator") || streq0(n, "GetIndex") || streq0(n, "SetIndex")) what = fcFmt(c, "the operator '%s'", n);
            }
            if (what)
                return fcFmt(c, "'class extends %s' in a plugin (line %d): %s cannot be added to a class of the program or a library - its objects are made by the program; a plugin adds methods and properties with get/set bodies only.",
                    target, member->line, what);
        }
    }
    return NULL;
}

/* A plugin's natives in C++ (this generator: the plugin's program, its packages collected): the configuration of the runtime of its C file, fire_rt_cpp.hpp, the C++
 * of the packages whose natives its code calls and its thunks (`fire_nat_<p>_k`). They are the plugin's own copy - their state is not the program's, also when the
 * program imports the same package (Linux: compiled with hidden visibility, as a DLL on Windows is anyway). On Windows the pointers to the program are those of its
 * C file (FIRE_DLL_LINKED_ELSEWHERE). */
static const char* PluginNativesCpp(FgGen* g, const FgModuleOutput* output) {
    FcBuf nat = {0};
    int i, k;
    fcBufStr(&nat, fgReplace(g->c, HeadDefines(g), "// Generated by fire.Native from the bytecode - do not edit.", "// Generated by fire.Native: the natives in C++ of a fire plugin - do not edit."));
    fcBufStr(&nat, HeadTail(g, 0));
    if (g->dll) { fcBufStr(&nat, DllHead(g)); fcBufStr(&nat, "#define FIRE_DLL_LINKED_ELSEWHERE 1   // (the pointers to the program are those of the plugin's C file)\n"); }
    fcBufFmt(&nat, "#define FIRE_PLATFORM_HEADER \"platform/%s/fire_platform.h\"\n", g->target->platform);
    fcBufFmt(&nat, "#define FIRE_PLATFORM_CPP_HEADER \"platform/%s/fire_platform.hpp\"\n", g->target->platform);
    fcBufStr(&nat, "#include \"fire_rt_cpp.hpp\"   // the runtime (fire_rt.h, the program has its state) and what C++ code expects of it\n");
    for (i = 0; i < g->packageImports->count; i++) {
        FgPkgImportKey* p = (FgPkgImportKey*)g->packageImports->items[i];
        bool calls = false;
        FcList* texts;
        if (!p->import->native || p->import->native->isC) continue;
        for (k = 0; k < p->import->native->functions->count && !calls; k++)
            if (fcListHasStr(output->cppNatives, fgPkgFnSymbol((const FgPkgFn*)p->import->native->functions->items[k]))) calls = true;
        if (!calls) continue;
        texts = fgReadNativeSources(g->c, g->instImports, g->store, p->import, fgPlatformKeys(g));
        for (k = 0; k < texts->count; k++) {
            FgNamedText* t = (FgNamedText*)texts->items[k];
            fcBufFmt(&nat, "// ---- package %s %s, import \"%s\": %s\n", p->import->pkgName, p->import->pkgVersion, p->import->name, t->name);
            fcBufFmt(&nat, "%s\n", PackageNativeText(g, t->text));
            fcBufFmt(&nat, "// ---- end of %s\n", t->name);
        }
    }
    fcBufStr(&nat, "\n");
    fcBufStr(&nat, output->nativeThunks);
    return fcBufDone(g->c, &nat);
}

/* ---- GeneratePlugin ---- */
/* A plugin as generated (PluginBuildPlan): its C (one file), its natives in C++ (NULL: none), its module name, the interfaces of the modules it needs (FgModuleDep*:
 * the host must have the same), the log. */
typedef struct FgPluginPlan { const char* c; const char* natives; const char* name; FcList* deps; const char* log; } FgPluginPlan;

/* PluginHostInfo.Read: the description of a program for plugins (`.firehost`); NULL: none, or it cannot be read */
static FgPluginHostInfo* fgHostInfoRead(FcCtx* c, const char* path) {
    FILE* f = fopen(path, "rb");
    FcBuf text = {0};
    char chunk[65536];
    size_t n;
    FcJsonReader j;
    FcJson* root;
    const FcJson* v;
    FgPluginHostInfo* h;
    int i;
    if (!f) return NULL;
    while ((n = fread(chunk, 1, sizeof chunk, f)) > 0) fcBufPut(&text, chunk, n);
    fclose(f);
    fcBufChar(&text, 0);
    j.c = c; j.p = text.p; j.bad = false;
    root = fcJsonValue(&j);
    fcBufFree(&text);
    if (j.bad || !root || root->kind != FJ_OBJ) return NULL;
    h = (FgPluginHostInfo*)fcAlloc(c, sizeof *h);
    h->format = (v = fcJsonGet(root, "Format")) && v->kind == FJ_NUM ? (int)v->n : FG_PLUGIN_FORMAT;
    h->target = fcJsonStr(root, "Target") ? fcJsonStr(root, "Target") : "";
    h->flags = fcJsonStr(root, "Flags") ? fcJsonStr(root, "Flags") : "";
    h->head = fcJsonStr(root, "Head") ? fcJsonStr(root, "Head") : "";
    h->runtime = fcJsonStr(root, "Runtime");
    h->modules = fcListNew(c);
    if ((v = fcJsonGet(root, "Modules")) && v->kind == FJ_ARR)
        for (i = 0; i < v->items->count; i++) {
            const FcJson* m = (const FcJson*)v->items->items[i];
            FgPluginHostModule* hm = (FgPluginHostModule*)fcAlloc(c, sizeof *hm);
            hm->name = fcJsonStr(m, "Name") ? fcJsonStr(m, "Name") : "";
            hm->abi = fcJsonStr(m, "Abi") ? fcJsonStr(m, "Abi") : "";
            hm->library = fcJsonStr(m, "Library") ? fcJsonStr(m, "Library") : "";
            fcListAdd(c, h->modules, hm);
        }
    return h;
}
static uint32_t fgParseFlags(const char* text) {
    uint32_t f = 0;
    int i;
    for (i = 0; i < MF_Count && text[i]; i++) if (text[i] == '1') f |= 1u << i;
    return f;
}

/* A plugin (`--linkage plugin`, ROADMAP 9 phase D b): the classes of the program's own sources as a library module of their own, against the library modules of
 * the program (core, the imports, the libraries of the project) - the program that loads it must have these with the same interfaces. The switches are those of
 * that program (host, its `.firehost` file), else those of the plugin and its modules. cacheRoot: NativeLibraryCache.Root. */
static FgPluginPlan* GeneratePlugin(FcCtx* c, FcLinked* program, FcList* natives, const FgTarget* target, const char* name, FgPluginHostInfo* host, int stripping,
    const char* toolchainStamp, const char* cacheRoot, FcStore* store) {
    bool dll = streq0(target->platform, "windows");
    FcBuf log = {0};
    FcList* specs = ModulesOf(c, program);
    FcDict* abi = fcDictNew(c, FC_KEY_PTR);
    FcList* modules = program->sourceModules;
    FgModuleSpec* self;
    FcList* all;
    FcList* deps;
    FgGen* probe;
    FgGen* headGen;
    FgModuleOutput* output;
    FgPluginPlan* plan;
    const char* problem;
    const char* head;
    const char* key;
    const char* text;
    const char* nativesCpp = NULL;
    bool hasFlags = false;
    uint32_t flags = 0;
    int i, round;
    if (!modules) fcThrow(c, FC_ERR_NATIVE, 0, 0, NULL, "the program does not carry the modules of its sources");
    probe = fgGenNew(c, program, natives, target);
    probe->store = store;
    CollectPackages(probe, store);
    for (i = 0; i < specs->count; i++) fcDictPut(abi, specs->items[i], (void*)AbiHash(probe, program, (FgModuleSpec*)specs->items[i]));
    self = (FgModuleSpec*)fcAlloc(c, sizeof *self);
    self->name = name; self->id = fcFmt(c, "p%s", ModuleCName(c, name) + 1); self->idPx = fcFmt(c, "%s_", self->id); self->index = specs->count;
    self->sources = fcDictNew(c, FC_KEY_STR);
    self->deps = fcListNew(c);
    self->classes = fcListNew(c);
    for (i = 0; i < specs->count; i++) if (streq0(((FgModuleSpec*)specs->items[i])->name, name)) fcThrow(c, FC_ERR_NATIVE, 0, 0, NULL, fcFmt(c, "the plugin has the name of a module of the program: '%s'", name));
    for (i = 0; i < modules->count; i++) if (!modules->items[i]) fgISetAdd(c, self->sources, i);
    for (i = 0; i < specs->count; i++) fcListAdd(c, self->deps, specs->items[i]);
    FC_DICT_EACH(program->program->classes, e) {
        FcRtClass* rc = (FcRtClass*)e->value;
        if (rc->decl && fgISetHas(c, self->sources, rc->decl->h.source)) fcListAdd(c, self->classes, (void*)rc->name);
    }
    if (self->classes->count > 1) qsort(self->classes->items, (size_t)self->classes->count, sizeof(void*), fgCmpOrdinal);
    if (self->classes->count == 0) fcThrow(c, FC_ERR_NATIVE, 0, 0, NULL, "the plugin declares no classes");
    if ((problem = PluginExtensionProblem(c, program, self->sources)) != NULL) fcThrow(c, FC_ERR_NATIVE, 0, 0, NULL, problem);
    all = fcListCopy(c, specs);
    fcListAdd(c, all, self);
    if (host) { hasFlags = true; flags = fgParseFlags(host->flags); }
    if (host && !streq0(host->target, target->name)) fcThrow(c, FC_ERR_NATIVE, 0, 0, NULL, fcFmt(c, "the program '%s' is built for another target than the plugin ('%s')", host->target, target->name));
    if (host)
        for (i = 0; i < specs->count; i++) {
            const FgModuleSpec* dep = (const FgModuleSpec*)specs->items[i];
            const FgPluginHostModule* their = NULL;
            int k;
            for (k = 0; k < host->modules->count && !their; k++) if (streq0(((FgPluginHostModule*)host->modules->items[k])->name, dep->name)) their = (FgPluginHostModule*)host->modules->items[k];
            if (!their) fcThrow(c, FC_ERR_NATIVE, 0, 0, NULL, fcFmt(c, "the program has no module '%s' (the plugin imports it)", dep->name));
            if (!streq0(their->abi, fcFmt(c, "%d:%s", FG_INTERFACE_FORMAT, (const char*)fcDictGet(abi, dep))))
                fcBufFmt(&log, "note: the module '%s' of the program has another interface than the one the plugin sees - it will not load there\n", dep->name);
        }
    if (!hasFlags) {
        /* (no program named: the switches of the plugin's modules - each module's own, from its interface in the cache or generated) */
        uint32_t u = 0;
        const char* root = fcFmt(c, "%s/%s", cacheRoot, target->name);
        const char* stamp = fcFmt(c, "%s|shared%s", toolchainStamp, dll ? "|dll" : "");
        for (i = 0; i < specs->count; i++) {
            FgModuleSpec* spec = (FgModuleSpec*)specs->items[i];
            const char* depKey = LibraryKey(probe, program, spec, abi, target, stamp, false, 0, fgLibraryStripping(program, stripping));
            FcJson* known = fgReadInterfaceJson(c, fcFmt(c, "%s/%s/%s%s", root, depKey, spec->id, FG_INTERFACE_EXTENSION), depKey);
            u |= known ? fgParseFlags(fcJsonStr(known, "Flags") ? fcJsonStr(known, "Flags") : "")
                : GenerateLibrary(c, program, natives, spec, specs, target, stripping, false, 0, true, dll, NULL, store)->flags;
        }
        flags = u; hasFlags = true;
    }
    deps = fcListNew(c);
    for (i = 0; i < specs->count; i++) {
        FgModuleDep* d = (FgModuleDep*)fcAlloc(c, sizeof *d);
        d->spec = (FgModuleSpec*)specs->items[i]; d->module = d->spec->name; d->abi = fcFmt(c, "%d:%s", FG_INTERFACE_FORMAT, (const char*)fcDictGet(abi, d->spec));
        fcListAdd(c, deps, d);
    }
    for (round = 0;; round++) {
        uint32_t u;
        output = GenerateLibrary(c, program, natives, self, all, target, stripping, true, flags, true, dll, deps, store);
        u = flags | output->flags;
        if (u == flags) break;
        if (host) fcThrow(c, FC_ERR_NATIVE, 0, 0, NULL, fcFmt(c, "the plugin needs switches the program was not built with (%s) - the program must use them too", DescribeFlags(c, u, flags)));
        if (round > 4) fcThrow(c, FC_ERR_NATIVE, 0, 0, NULL, "the plugin and its modules do not agree on the switches");
        flags = u;
    }
    /* the head of its file: the configuration of the runtime of the program (the same switches), the key the runtime compares */
    headGen = fgGenNew(c, program, natives, target);
    headGen->role = FG_ROLE_PROGRAM; headGen->hasForced = true; headGen->forced = flags; headGen->separate = true; headGen->dll = dll; headGen->strip = fgLibraryStripping(program, stripping);
    headGen->store = store;
    CollectPackages(headGen, store);
    ApplyForcedFlags(headGen);
    head = LibraryHeadOf(headGen, fcFmt(c, "%s%s", HeadDefines(headGen), HeadTail(headGen, 0)));
    key = PluginHeadKey(c, head, fgFlagsText(c, flags));
    if (output->jsonEngine) head = fgReplace(c, head, "#include \"fire_json.h\"", "#define FIRE_JSON_IMPLEMENTATION 1   // (the JSON engine: in the file of the module that calls it)\n#include \"fire_json.h\"");
    if (host && !streq0(key, host->head)) fcBufFmt(&log, "note: the configuration of the runtime differs from the program's (%.12s, the program %.12s)\n", key, host->head);
    text = ResolveLineResets(headGen, fcFmt(c, "%s%s%s%s", head, output->nativesC, fgReplace(c, output->body, FG_PLUGIN_HEAD_MARK, key), output->linkC), "fire_program.c");
    if (output->nativeThunks && *output->nativeThunks) nativesCpp = PluginNativesCpp(headGen, output);
    fcBufFmt(&log, "plugin %s: %d classes, switches %s, runtime %.12s", name, self->classes->count, fgFlagsText(c, flags), key);
    if (nativesCpp) fcBufFmt(&log, ", natives in C++ (%d)", output->cppNatives->count);
    fcBufChar(&log, '\n');
    plan = (FgPluginPlan*)fcAlloc(c, sizeof *plan);
    plan->c = text; plan->natives = nativesCpp; plan->name = name; plan->deps = deps; plan->log = fcBufDone(c, &log);
    return plan;
}

/* The interfaces of the library modules of a linked program, as the program file carries them (ProgramFile FlagModules, programfile.c): ModuleInterface.Modules
 * + Text, ordered by name - name, interface, name, ...; NULL without the modules of its sources. */
static FcList* fcPfModuleInterfaces(FcCtx* c, const FcLinked* linked) {
    FcList* out;
    FcList* specs;
    FgGen* probe;
    int i, k;
    if (!linked->sourceModules) return NULL;
    specs = fcListCopy(c, ModulesOf(c, linked));
    for (i = 1; i < specs->count; i++)
        for (k = i; k > 0 && strcmp(((FgModuleSpec*)specs->items[k - 1])->name, ((FgModuleSpec*)specs->items[k])->name) > 0; k--) {
            void* t = specs->items[k]; specs->items[k] = specs->items[k - 1]; specs->items[k - 1] = t;
        }
    probe = fgGenNew(c, (FcLinked*)linked, fcListNew(c), NULL);   /* (AbiHash reads the layouts only) */
    out = fcListNew(c);
    for (i = 0; i < specs->count; i++) {
        const FgModuleSpec* spec = (const FgModuleSpec*)specs->items[i];
        fcListAdd(c, out, (void*)spec->name);
        fcListAdd(c, out, (void*)fcFmt(c, "%d:%s", 1, AbiHash(probe, linked, spec)));
    }
    return out;
}
