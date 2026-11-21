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
static const char* PluginDeclarations(FgGen* g) { (void)g; FG_UNPORTED_C(g->c, "linkage \"plugin\" (PluginDeclarations)"); }
static void RegisterPluginTargets(FgGen* g) { if (g->plugin) FG_UNPORTED_C(g->c, "linkage \"plugin\" (RegisterPluginTargets)"); }
static const char* PluginTrailer(FgGen* g) { if (!g->plugin) return ""; FG_UNPORTED_C(g->c, "linkage \"plugin\" (PluginTrailer)"); }
