/* native/forge/build/pkglib.c - the natives of a package for the virtual machine (Phase D 2, commit de45e7e4): src/fire.Compiler/PackageLibrary.cs
 * (Rid, HostPlatformKeys, FileNameFor, Prebuilt, BuiltDirectories, NewestSource, RuntimeKey, Locate, Ensure with its remembered failures, GenerateCWrapper,
 * GenerateWrapper, the library of graphics and windows - EnsureGraphics, GenerateGraphicsWrapper), NativeRuntimeFiles.Fingerprint (src/fire.Native) and
 * StandardSources.NativeHashOf (src/fire.Compiler: the C of a package, the key of a library under Prebuilt/). OptimizingToolchain is in toolchain.c.
 * The libraries are the ones the VM in C and the C# VM load (native/abi/fire_pkg_abi.h); `forge-c pkglib` builds them, `forge-c pkglibplan` is the measure
 * (FrontendDump.DumpPackageLibraryPlan of the C# forge). A PackageException is an error text in the result (NlResult.error), not a throw. */

/* RuntimeInformation: the runtime identifier of this machine (linux-x64, osx-arm64, ...) */
static const char* nlRid(void) {
#if defined(_WIN32)
    const char* os = "win";
#elif defined(__APPLE__)
    const char* os = "osx";
#else
    const char* os = "linux";
#endif
#if defined(__x86_64__) || defined(_M_X64)
    const char* arch = "x64";
#elif defined(__aarch64__)
    const char* arch = "arm64";
#elif defined(__i386__)
    const char* arch = "x86";
#elif defined(__arm__)
    const char* arch = "arm";
#else
    const char* arch = "other";
#endif
    static char rid[32];
    snprintf(rid, sizeof rid, "%s-%s", os, arch);
    return rid;
}
/* HostPlatformKeys: `vm` first, then the platform package and the name of this machine's target */
static FcList* nlHostPlatformKeys(FcCtx* c) { return nbStrings(c, 3, "vm", fgHostTarget()->platform, fgHostTarget()->name); }
#if defined(_WIN32)
#define NL_EXTENSION ".dll"
#define NL_PREFIX ""
#elif defined(__APPLE__)
#define NL_EXTENSION ".dylib"
#define NL_PREFIX "lib"
#else
#define NL_EXTENSION ".so"
#define NL_PREFIX "lib"
#endif
/* Safe: letters and digits in lower case, everything else `_` (char.IsLetterOrDigit of a UTF-16 unit: the names of the packages are ASCII - a byte >= 0x80 is `_`) */
static const char* nlSafe(FcCtx* c, const char* text) {
    char* r = (char*)fcStr(c, text);
    char* p;
    for (p = r; *p; p++) *p = isalnum((unsigned char)*p) && (unsigned char)*p < 0x80 ? (char)tolower((unsigned char)*p) : '_';
    return r;
}
static const char* nlFileNameFor(FcCtx* c, const FgInstImport* imp) { return fcFmt(c, NL_PREFIX "fire_pkg_%s_%s" NL_EXTENSION, nlSafe(c, imp->pkgName), nlSafe(c, imp->name)); }
/* InstalledImport.FullPath: Path.GetFullPath(Path.Combine(the package's folder, relative)) */
static const char* nlFullPath(FcCtx* c, const FgInstImport* imp, const char* relative) { return fcFullPath(c, ".", nbCombine(c, imp->dir, relative)); }

/* What the manifest says besides what the generator reads (gen/packages.c): `reset` and the prebuilt `libraries` of the import (rid -> path). */
typedef struct NlManifest { const char* reset; FcList* libraryRids; FcList* libraryPaths; } NlManifest;
static NlManifest nlManifestOf(FcCtx* c, const FgInstImport* imp) {
    NlManifest m = { NULL, NULL, NULL };
    FcJson* manifest = fgReadJsonFile(c, fcFmt(c, "%s/package.json", imp->dir));
    FcJson* imports = manifest ? fcJsonGet(manifest, "imports") : NULL;
    int k, i;
    m.libraryRids = fcListNew(c);
    m.libraryPaths = fcListNew(c);
    if (!imports || imports->kind != FJ_ARR) return m;
    for (k = 0; k < imports->items->count; k++) {
        FcJson* im = (FcJson*)imports->items->items[k];
        FcJson* nv;
        FcJson* libs;
        if (!fcJsonStr(im, "name") || !streq0(fcJsonStr(im, "name"), imp->name)) continue;
        if ((nv = fcJsonGet(im, "native")) == NULL || nv->kind != FJ_OBJ) break;
        m.reset = fcJsonStr(nv, "reset");
        if ((libs = fcJsonGet(nv, "libraries")) != NULL && libs->kind == FJ_OBJ)
            for (i = 0; i < libs->keys->count; i++)
                if (((FcJson*)libs->items->items[i])->kind == FJ_STR) { fcListAdd(c, m.libraryRids, libs->keys->items[i]); fcListAdd(c, m.libraryPaths, (void*)((FcJson*)libs->items->items[i])->s); }
        break;
    }
    return m;
}
static bool nlBlank(const char* s) { if (!s) return true; for (; *s; s++) if (!isspace((unsigned char)*s)) return false; return true; }

/* Prebuilt: a library the package brings for this machine (`native.libraries`) */
static const char* nlPrebuilt(FcCtx* c, const FgInstImport* imp) {
    NlManifest m;
    int i;
    if (!imp->native) return NULL;
    m = nlManifestOf(c, imp);
    for (i = 0; i < m.libraryRids->count; i++)
        if (fcAsciiIEq((const char*)m.libraryRids->items[i], nlRid()) && nbFileExists(nlFullPath(c, imp, (const char*)m.libraryPaths->items[i]))) return nlFullPath(c, imp, (const char*)m.libraryPaths->items[i]);
    return NULL;
}
/* BuiltDirectories: <BuildDirectory>/<rid> (the natives of a project: obj/native), else lib/<rid>/ in the folder of the package, then a folder in the temporary files */
static FcList* nlBuiltDirectories(FcCtx* c, const FgInstImport* imp) {
    FcList* r = fcListNew(c);
    if (imp->buildDirectory) fcListAdd(c, r, (void*)nbCombine(c, imp->buildDirectory, nlRid()));
    else fcListAdd(c, r, (void*)nbCombine(c, nbCombine(c, imp->dir, "lib"), nlRid()));
    fcListAdd(c, r, (void*)nbCombine(c, nbCombine(c, nbCombine(c, nbTempPath(c), "fire-packages"), fcFmt(c, "%s-%s", nlSafe(c, imp->pkgName), nlSafe(c, imp->pkgVersion))), nlRid()));
    return r;
}
/* File.GetLastWriteTimeUtc as ticks (lstat, as the stamps of builder.c); a file that is not there: 1601-01-01 */
#define NL_NO_FILE 504911232000000000LL
static long long nlWriteTicks(const char* path) {
    struct stat st;
    return lstat(path, &st) == 0 ? nbTicks(&st) : NL_NO_FILE;
}
static long long nlNewestSource(FcCtx* c, const FgInstImport* imp) {
    FcList* sources = fgSourcesFor(c, imp->native, nlHostPlatformKeys(c), false);
    long long newest = LLONG_MIN;
    int i;
    for (i = 0; i < sources->count; i++) { long long t = nlWriteTicks(nlFullPath(c, imp, (const char*)sources->items[i])); if (t > newest) newest = t; }
    for (i = 0; i < imp->native->headers->count; i++) { long long t = nlWriteTicks(nlFullPath(c, imp, (const char*)imp->native->headers->items[i])); if (t > newest) newest = t; }
    for (i = 0; imp->native->units && i < imp->native->units->count; i++) { long long t = nlWriteTicks(nlFullPath(c, imp, (const char*)imp->native->units->items[i])); if (t > newest) newest = t; }
    /* (the project file of a project's natives: the build order of the sources is written there) */
    if (imp->descriptionFile && nbFileExists(imp->descriptionFile)) { long long t = nlWriteTicks(imp->descriptionFile); if (t > newest) newest = t; }
    return newest == LLONG_MIN ? 0 : newest;   /* (DefaultIfEmpty(DateTime.MinValue)) */
}

/* ---- the keys: NativeRuntimeFiles.Fingerprint, RuntimeKey, NativeHashOf ---- */
static void nlUpperHex(const uint8_t* d, int n, char* out) { int i; for (i = 0; i < n; i++) { out[i * 2] = "0123456789ABCDEF"[d[i] >> 4]; out[i * 2 + 1] = "0123456789ABCDEF"[d[i] & 15]; } out[n * 2] = 0; }
static const char* nlShaUpper(FcCtx* c, FcSha256* h) {
    const char* lower = fcShaHex(c, h);
    char* r = (char*)fcStr(c, lower);
    char* p;
    for (p = r; *p; p++) *p = (char)toupper((unsigned char)*p);
    return r;
}
/* the fc8 text as the UTF-8 bytes .NET hashes (Encoding.UTF8.GetBytes) */
static void nlHashFc8(FcSha256* h, const char* fc8) {
    FcBuf u = {0};
    fcBufStr(&u, "");
    fcOut8(&u, fc8);
    fcShaUpdate(h, (const uint8_t*)u.p, u.len);
    fcBufFree(&u);
}
/* Fingerprint(platform): the hash of what WriteTo writes from the runtime for platform - the names and contents of fire_rt.h, fire_rt_cpp.hpp, fire_json.h,
 * fire_debug.h, cc/, abi/ and the platform package with the ones it includes, in the ordinal order of the names */
static const char* nlFingerprint(FcCtx* c, const char* platform) {
    FcList* res = nbResources(c);
    FcList* names = fcListNew(c);
    FcList* wanted = nbPackageClosure(c, platform);
    FcSha256 h;
    int i, k;
    static const char* const runtime[] = { "fire_rt.h", "fire_rt_cpp.hpp", "fire_json.h", "fire_debug.h" };
    for (i = 0; i < 4; i++) fcListAdd(c, names, (void*)runtime[i]);
    for (i = 0; i < res->count; i++) { const char* n = ((NbResource*)res->items[i])->name; if (fcStartsWith(n, "cc/") || fcStartsWith(n, "abi/")) fcListAdd(c, names, (void*)n); }
    for (k = 0; k < wanted->count; k++) {
        const char* prefix = fcFmt(c, "platform/%s/", (const char*)wanted->items[k]);
        for (i = 0; i < res->count; i++) if (fcStartsWith(((NbResource*)res->items[i])->name, prefix)) fcListAdd(c, names, (void*)((NbResource*)res->items[i])->name);
    }
    if (names->count > 1) qsort(names->items, (size_t)names->count, sizeof(void*), fcCmpOrdinal);
    fcShaInit(&h);
    for (i = 0; i < names->count; i++) {
        const char* name = (const char*)names->items[i];
        NbResource* x = NULL;
        size_t n = 0;
        uint8_t* body;
        if (i > 0 && streq0(name, (const char*)names->items[i - 1])) continue;   /* (Distinct) */
        for (k = 0; k < res->count && !x; k++) if (streq0(((NbResource*)res->items[k])->name, name)) x = (NbResource*)res->items[k];
        if (!x) continue;
        fcShaUpdate(&h, (const uint8_t*)name, strlen(name));
        fcShaUpdate(&h, (const uint8_t*)"\n", 1);
        body = fbReadFile(x->file, &n);
        if (body) fcShaUpdate(&h, body, n);
        free(body);
    }
    return nlShaUpper(c, &h);
}
/* RuntimeKey(wrapper): what a library is built against besides the sources of its package - the runtime files and the wrapper */
static const char* nlRuntimeKeyOf(FcCtx* c, const char* wrapper) {
    FcSha256 h;
    fcShaInit(&h);
    nlHashFc8(&h, nlFingerprint(c, fgHostTarget()->platform));
    fcShaUpdate(&h, (const uint8_t*)"\n", 1);
    nlHashFc8(&h, wrapper);
    return nlShaUpper(c, &h);
}
/* JsonSerializer.Serialize of a text with the default encoder: `"` and the HTML-sensitive characters as \uXXXX, the controls short or as \u00XX, everything
 * outside the printable ASCII as \uXXXX (UTF-16 units, upper case) */
static void nlJsonString(FcBuf* b, const char* fc8) {
    const unsigned char* p = (const unsigned char*)fc8;
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
static void nlJsonNullable(FcBuf* b, const char* s) { if (s) nlJsonString(b, s); else fcBufStr(b, "null"); }
/* The functions of the manifest as System.Text.Json writes List<PackageNativeFunction> (the properties in their order, Symbol is [JsonIgnore]) */
static const char* nlFunctionsJson(FcCtx* c, const FgPkgNativeM* n) {
    FcBuf b = {0};
    int i, k;
    fcBufStr(&b, "");
    if (!n) { fcBufStr(&b, "null"); return fcBufDone(c, &b); }
    fcBufChar(&b, '[');
    for (i = 0; i < n->functions->count; i++) {
        const FgPkgFn* fn = (const FgPkgFn*)n->functions->items[i];
        if (i) fcBufChar(&b, ',');
        fcBufStr(&b, "{\"Name\":"); nlJsonString(&b, fn->name);
        fcBufFmt(&b, ",\"Arguments\":%d,\"Cpp\":", fn->arguments); nlJsonString(&b, fn->cpp);
        fcBufStr(&b, ",\"C\":"); nlJsonNullable(&b, fn->cfn);
        fcBufFmt(&b, ",\"NeedsList\":%s,\"ReturnsReference\":%s,\"Host\":%s,\"Returns\":", fn->needsList ? "true" : "false", fn->returnsReference ? "true" : "false", fn->host ? "true" : "false");
        nlJsonNullable(&b, fn->returns);
        fcBufStr(&b, ",\"Parameters\":");
        if (!fn->parameters) fcBufStr(&b, "null");
        else { fcBufChar(&b, '['); for (k = 0; k < fn->parameters->count; k++) { if (k) fcBufChar(&b, ','); nlJsonString(&b, (const char*)fn->parameters->items[k]); } fcBufChar(&b, ']'); }
        fcBufChar(&b, '}');
    }
    fcBufChar(&b, ']');
    return fcBufDone(c, &b);
}
/* StandardSources.NativeHashOf: the C of an import for this machine (its sources, name and text) and its functions */
static const char* nlNativeHashOf(FcCtx* c, FcDict* cache, FcStore* store, const FgInstImport* imp) {
    FcList* sources = fgReadNativeSources(c, cache, store, imp, nlHostPlatformKeys(c));
    FcSha256 h;
    int i;
    fcShaInit(&h);
    for (i = 0; i < sources->count; i++) {
        FgNamedText* t = (FgNamedText*)sources->items[i];
        nlHashFc8(&h, t->name);
        fcShaUpdate(&h, (const uint8_t*)"\n", 1);
        nlHashFc8(&h, t->text);
        fcShaUpdate(&h, (const uint8_t*)"\n", 1);
    }
    nlHashFc8(&h, nlFunctionsJson(c, imp->native));
    /* (the units of the import, `native.units` - forge-c, D8: their bytes; nothing for an import without) */
    for (i = 0; imp->native->units && i < imp->native->units->count; i++) {
        size_t n = 0;
        uint8_t* data = fbReadFile(nlFullPath(c, imp, (const char*)imp->native->units->items[i]), &n);
        nlHashFc8(&h, fcFmt(c, "\nunit %s\n", fgFileNameOf(c, (const char*)imp->native->units->items[i])));
        if (data && n) fcShaUpdate(&h, data, n);
        free(data);
    }
    return nlShaUpper(c, &h);
}

/* ---- the wrappers ---- */
static const char* nlTargetMacro(FcCtx* c, const FgTarget* t) {
    char* r = (char*)fcStr(c, t->name);
    char* p;
    for (p = r; *p; p++) *p = isalnum((unsigned char)*p) && (unsigned char)*p < 0x80 ? (char)toupper((unsigned char)*p) : '_';
    return r;
}
static const char* nlUpper(FcCtx* c, const char* s) { char* r = (char*)fcStr(c, s); char* p; for (p = r; *p; p++) if (*p >= 'a' && *p <= 'z') *p = (char)(*p - 32); return r; }
/* functions[i].Name.Replace("\\", "\\\\").Replace("\"", "\\\"") */
static const char* nlCName(FcCtx* c, const char* name) { return fgReplace(c, fgReplace(c, name, "\\", "\\\\"), "\"", "\\\""); }
static void nlDefines(FcCtx* c, FcBuf* sb, const FgTarget* target, bool cpp) {
    fcBufStr(sb, "#define FIRE_LIBRARY 1\n");
    fcBufFmt(sb, "#define FIRE_TARGET \"%s\"\n", target->name);
    fcBufFmt(sb, "#define FIRE_TARGET_%s 1\n", nlTargetMacro(c, target));
    fcBufFmt(sb, "#define FIRE_HAL_%s 1\n", nlUpper(c, target->halPackage));
    fcBufStr(sb, "#define FIRE_NDIMS 1\n");
    fcBufFmt(sb, "#define FIRE_PLATFORM_HEADER \"platform/%s/fire_platform.h\"\n", target->platform);
    if (cpp) fcBufFmt(sb, "#define FIRE_PLATFORM_CPP_HEADER \"platform/%s/fire_platform.hpp\"\n", target->platform);
}
static FcList* nlLibraryFunctions(FcCtx* c, const FgPkgNativeM* n) {
    FcList* r = fcListNew(c);
    int i;
    for (i = 0; i < n->functions->count; i++) if (!((FgPkgFn*)n->functions->items[i])->host) fcListAdd(c, r, n->functions->items[i]);
    return r;
}
static void nlEntryPoints(FcCtx* c, FcBuf* sb, const char* nullName, const char* callEntry) {
    (void)c;
    fcBufFmt(sb, "FIRE_PKG_EXPORT int fire_pkg_abi_version(void) { return FIRE_PKG_ABI_VERSION; }\n");
    fcBufFmt(sb, "FIRE_PKG_EXPORT int fire_pkg_function_count(void) { return kCount; }\n");
    fcBufFmt(sb, "FIRE_PKG_EXPORT const char* fire_pkg_function_name(int i) { return i >= 0 && i < kCount ? kEntries[i].name : %s; }\n", nullName);
    fcBufFmt(sb, "FIRE_PKG_EXPORT int fire_pkg_function_arity(int i) { return i >= 0 && i < kCount ? kEntries[i].arity : -1; }\n");
    fcBufFmt(sb, "FIRE_PKG_EXPORT int fire_pkg_call(int i, const fire_val* a, int n, fire_val* r, char* e, int es) { return %s(kEntries, kCount, i, a, n, r, e, es); }\n", callEntry);
}
/* GenerateCWrapper: the runtime in library mode (fire_rt.h as C), the sources of the import for this machine, the entry points of the ABI around its functions */
static const char* nlGenerateCWrapper(FcCtx* c, FcDict* cache, FcStore* store, const FgInstImport* imp) {
    const FgPkgNativeM* native = imp->native;
    const FgTarget* target = fgHostTarget();
    FcBuf sb = {0};
    FcList* libs = fgLinkLibrariesFor(c, native, nlHostPlatformKeys(c));
    FcList* sources = fgReadNativeSources(c, cache, store, imp, nlHostPlatformKeys(c));
    FcList* functions = nlLibraryFunctions(c, native);
    const char* reset = nlManifestOf(c, imp).reset;
    int i, k;
    fcBufStr(&sb, "");
    fcBufFmt(&sb, "/* Generated by fire.Compiler: the C ABI (native/abi/fire_pkg_abi.h) around the natives in C of the package %s %s, import \"%s\". */\n", imp->pkgName, imp->pkgVersion, imp->name);
    for (i = 0; i < libs->count; i++) fcBufFmt(&sb, "// fire-link: %s\n", (const char*)libs->items[i]);
    nlDefines(c, &sb, target, false);
    fcBufStr(&sb, "#define FIRE_RT_IMPLEMENTATION 1   /* the library has a runtime of its own (its state lives here) */\n");
    fcBufStr(&sb, "#include \"fire_rt.h\"\n");
    for (i = 0; i < sources->count; i++) { FgNamedText* t = (FgNamedText*)sources->items[i]; fcBufFmt(&sb, "/* ---- %s */\n%s\n", t->name, t->text); }
    fcBufStr(&sb, "#include \"fire_pkg_wrapper.h\"\n");
    for (i = 0; i < functions->count; i++) {
        const FgPkgFn* fn = (const FgPkgFn*)functions->items[i];
        /* (a typed function - "parameters" in the manifest: the Values converted into its C numbers, its C result into a Value) */
        bool typed = fgNativeIsTyped(fn, true);
        FcList* args = fcListNew(c);
        const char* call;
        const char* rt;
        for (k = 0; k < fn->arguments; k++) {
            const char* ct = typed ? fgNativeParamCType(fn, k) : NULL;
            const char* a = fcFmt(c, "a[%d]", k);
            fcListAdd(c, args, (void*)(ct ? (streq0(ct, "Real") ? fcFmt(c, "toR(%s)", a) : streq0(ct, "bool") ? fcFmt(c, "(%s).i != 0", a) : fcFmt(c, "(%s)fire_natI(%s)", ct, a)) : a));
        }
        if (fn->needsList) fcListAdd(c, args, (void*)"list");
        call = fcFmt(c, "%s(%s)", fgPkgFnSymbol(fn), fgJoin(c, args, ", "));
        if (typed && (rt = fgNativeReturnCType(fn)) != NULL) call = fgNativeToValue(c, rt, call);
        fcBufFmt(&sb, "static Value thunk_%d(const Value* a, OwnList* list) { (void)a; (void)list; return %s; }\n", i, call);
    }
    fcBufStr(&sb, "static const FirePkgFn kEntries[] = {\n");
    for (i = 0; i < functions->count; i++) fcBufFmt(&sb, "    {\"%s\", %d, thunk_%d},\n", nlCName(c, ((FgPkgFn*)functions->items[i])->name), ((FgPkgFn*)functions->items[i])->arguments, i);
    fcBufStr(&sb, "    {NULL, 0, NULL}\n};\n");
    fcBufFmt(&sb, "static const int kCount = %d;\n", functions->count);
    fcBufStr(&sb, "FIRE_PKG_EXPORT void fire_pkg_set_host(const fire_host* host) { g_libraryHost = host; }\n");
    if (nlBlank(reset)) fcBufStr(&sb, "FIRE_PKG_EXPORT void fire_pkg_reset(void) { }\n");
    else fcBufFmt(&sb, "FIRE_PKG_EXPORT void fire_pkg_reset(void) { fire_pkgLock(); %s(); fire_pkgUnlock(); }\n", reset);
    nlEntryPoints(c, &sb, "NULL", "fire_pkgCallEntry");
    return fcBufDone(c, &sb);
}
/* GenerateWrapper: the same in C++ (fire_rt.h as C++, fire_rt_cpp.hpp) */
static const char* nlGenerateWrapper(FcCtx* c, FcDict* cache, FcStore* store, const FgInstImport* imp) {
    const FgPkgNativeM* native = imp->native;
    const FgTarget* target = fgHostTarget();
    FcBuf sb = {0};
    FcList* libs = fgLinkLibrariesFor(c, native, nlHostPlatformKeys(c));
    FcList* sources = fgReadNativeSources(c, cache, store, imp, nlHostPlatformKeys(c));
    FcList* functions = nlLibraryFunctions(c, native);
    const char* reset = nlManifestOf(c, imp).reset;
    int i, k;
    fcBufStr(&sb, "");
    fcBufFmt(&sb, "// Generated by fire.Compiler: the C ABI (native/abi/fire_pkg_abi.h) around the natives of the package %s %s, import \"%s\".\n", imp->pkgName, imp->pkgVersion, imp->name);
    for (i = 0; i < libs->count; i++) fcBufFmt(&sb, "// fire-link: %s\n", (const char*)libs->items[i]);   /* (`linkLibraries` of the package: the build links them) */
    nlDefines(c, &sb, target, true);
    fcBufStr(&sb, "#define FIRE_RT_IMPLEMENTATION 1   // the library has a runtime of its own (its state lives here)\n");
    fcBufStr(&sb, "#include \"fire_rt_cpp.hpp\"\n");
    for (i = 0; i < sources->count; i++) { FgNamedText* t = (FgNamedText*)sources->items[i]; fcBufFmt(&sb, "// ---- %s\n%s\n", t->name, t->text); }
    fcBufStr(&sb, "#include \"fire_pkg_wrapper.hpp\"\n");
    fcBufStr(&sb, "using namespace fire;\n");
    /* the functions of the host (`host` in the manifest) are run by the VM itself: they are not part of the library */
    for (i = 0; i < functions->count; i++) {
        const FgPkgFn* fn = (const FgPkgFn*)functions->items[i];
        FcList* args = fcListNew(c);
        for (k = 0; k < fn->arguments; k++) fcListAdd(c, args, (void*)fcFmt(c, "a[%d]", k));
        if (fn->needsList) fcListAdd(c, args, (void*)"list");
        fcBufFmt(&sb, "static Value thunk_%d(const Value* a, OwnList* list) { (void)a; (void)list; return %s(%s); }\n", i, fn->cpp, fgJoin(c, args, ", "));
    }
    fcBufStr(&sb, "static const pkgabi::FnEntry kEntries[] = {\n");
    for (i = 0; i < functions->count; i++) fcBufFmt(&sb, "    {\"%s\", %d, thunk_%d},\n", nlCName(c, ((FgPkgFn*)functions->items[i])->name), ((FgPkgFn*)functions->items[i])->arguments, i);
    fcBufStr(&sb, "    {nullptr, 0, nullptr}\n};\n");
    fcBufFmt(&sb, "static const int kCount = %d;\n", functions->count);
    fcBufStr(&sb, "FIRE_PKG_EXPORT void fire_pkg_set_host(const fire_host* host) { libraryHost() = host; }\n");
    if (nlBlank(reset)) fcBufStr(&sb, "FIRE_PKG_EXPORT void fire_pkg_reset(void) { }\n");
    else fcBufFmt(&sb, "FIRE_PKG_EXPORT void fire_pkg_reset(void) { std::lock_guard<std::mutex> lock(pkgabi::callLock()); %s(); }\n", reset);
    nlEntryPoints(c, &sb, "nullptr", "pkgabi::callEntry");
    return fcBufDone(c, &sb);
}
static const char* nlWrapperOf(FcCtx* c, FcDict* cache, FcStore* store, const FgInstImport* imp) {
    return imp->native->isC ? nlGenerateCWrapper(c, cache, store, imp) : nlGenerateWrapper(c, cache, store, imp);
}
/* CppGenerator.DisplayLibraries: what fire's own display links on a target */
static FcList* nlDisplayLibraries(FcCtx* c, const FgTarget* target) {
    if (streq0(target->platform, "windows")) return nbStrings(c, 2, "user32", "gdi32");
    if (streq0(target->name, "macos")) return nbStrings(c, 3, "framework:Cocoa", "framework:QuartzCore", "framework:GameController");
    return nbStrings(c, 2, "dl", "pthread");
}
/* GenerateGraphicsWrapper: the library of graphics and windows for the VM - the runtime, the sources of both imports, the entry points around their `native.bridge`,
 * the host's own functions (fire_gfx_host.h) */
static const char* nlGenerateGraphicsWrapper(FcCtx* c, FcDict* cache, FcStore* store, const FgInstImport* graphics, const FgInstImport* windows, const FgTarget* target) {
    FcList* platformKeys = nbStrings(c, 2, target->platform, target->name);
    const FgInstImport* both[2];
    FcList* libs = nlDisplayLibraries(c, target);
    FcList* names = fcListNew(c);
    FcList* argcs = fcListNew(c);
    FcList* calls = fcListNew(c);
    FcBuf sb = {0};
    int i, k, m;
    both[0] = graphics; both[1] = windows;
    fcBufStr(&sb, "");
    fcBufStr(&sb, "/* Generated by fire.Compiler: the C ABI (native/abi/fire_pkg_abi.h) around the natives of graphics and windows, for the virtual machine. */\n");
    for (i = 0; i < libs->count; i++) fcBufFmt(&sb, "// fire-link: %s\n", (const char*)libs->items[i]);
    nlDefines(c, &sb, target, false);
    fcBufStr(&sb, "#define FIRE_RT_IMPLEMENTATION 1   /* the library has a runtime of its own (its state lives here) */\n");
    fcBufStr(&sb, "#include \"fire_rt.h\"\n");
    for (m = 0; m < 2; m++) {
        FcList* sources = fgReadNativeSources(c, cache, store, both[m], platformKeys);
        for (i = 0; i < sources->count; i++) { FgNamedText* t = (FgNamedText*)sources->items[i]; fcBufFmt(&sb, "/* ---- %s: %s */\n%s\n", both[m]->name, t->name, t->text); }
    }
    fcBufStr(&sb, "#include \"fire_pkg_wrapper.h\"\n");
    /* the natives as the two packages declare them (native.bridge): the C function, its arguments, the scope's list */
    for (m = 0; m < 2; m++) {
        if (!both[m]->native) continue;
        for (i = 0; i < both[m]->native->bridge->count; i++) {
            const FgPkgFn* fn = (const FgPkgFn*)both[m]->native->bridge->items[i];
            FcList* args = fcListNew(c);
            for (k = 0; k < fn->arguments; k++) fcListAdd(c, args, (void*)fcFmt(c, "a[%d]", k));
            if (fn->needsList) fcListAdd(c, args, (void*)"list");
            fcListAdd(c, names, (void*)fn->name);
            fcListAdd(c, argcs, FC_IVAL(fn->arguments));
            fcListAdd(c, calls, (void*)fcFmt(c, "%s(%s)", fgPkgFnSymbol(fn), fgJoin(c, args, ", ")));
        }
    }
    for (i = 0; i < calls->count; i++) fcBufFmt(&sb, "static Value thunk_%d(const Value* a, OwnList* list) { (void)a; (void)list; return %s; }\n", i, (const char*)calls->items[i]);
    fcBufStr(&sb, "static const FirePkgFn kEntries[] = {\n");
    for (i = 0; i < names->count; i++) fcBufFmt(&sb, "    {\"%s\", %d, thunk_%d},\n", (const char*)names->items[i], FC_VALI(argcs->items[i]), i);
    fcBufStr(&sb, "    {NULL, 0, NULL}\n};\n");
    fcBufFmt(&sb, "static const int kCount = %d;\n", names->count);
    fcBufStr(&sb, "#include \"fire_gfx_host.h\"\n");
    /* (FIRE_DISPLAY_SHM: the windows of a program in `fire` are a host's in another process - spark's live view, fire_gfx_host.h) */
    fcBufStr(&sb, "FIRE_PKG_EXPORT void fire_pkg_set_host(const fire_host* host) { g_libraryHost = host; fire_gfx_host_fromEnvironment(); }\n");
    fcBufStr(&sb, "FIRE_PKG_EXPORT void fire_pkg_reset(void) { fire_pkgLock(); fire_gfx_host_reset(); fire_pkgUnlock(); }\n");
    fcBufStr(&sb, "FIRE_PKG_EXPORT void fire_pkg_service(void) { fire_pkgService(); }\n");
    fcBufStr(&sb, "FIRE_PKG_EXPORT void fire_pkg_idle(void) { fire_pkgIdle(); }\n");
    nlEntryPoints(c, &sb, "NULL", "fire_pkgCallEntry");
    return fcBufDone(c, &sb);
}

/* ---- Locate, Ensure ---- */
static const char* nlKeyFile(FcCtx* c, const char* library) { return fcFmt(c, "%s.runtime", library); }
/* the trimmed text of a file (File.ReadAllText(...).Trim()), NULL: not there */
static const char* nlReadTrimmed(FcCtx* c, const char* path) { const char* t = nbFileExists(path) ? fgReadText(c, path) : NULL; return t ? fgTrim(c, t) : NULL; }
static bool nlSameRuntime(FcCtx* c, const char* library, const char* key) { const char* t = nlReadTrimmed(c, nlKeyFile(c, library)); return t && streq0(t, key); }
/* Prebuilt/ next to forge-c (StandardSources.DefaultPrebuiltFolder) */
static const char* nlPrebuiltFolder(FcCtx* c) { return nbCombine(c, nbBaseDirectory, "Prebuilt"); }
/* Locate: the library of an import on this machine if there is one (prebuilt, built earlier and not older than the sources, or under Prebuilt/); NULL otherwise */
static const char* nlLocate(FcCtx* c, FcDict* cache, FcStore* store, const FgInstImport* imp) {
    const char* prebuilt;
    const char* key = NULL;
    const char* lib;
    const char* hash;
    long long newest;
    FcList* dirs;
    int i;
    if (!imp->native) return NULL;
    if ((prebuilt = nlPrebuilt(c, imp)) != NULL) return prebuilt;
    newest = nlNewestSource(c, imp);
    dirs = nlBuiltDirectories(c, imp);
    for (i = 0; i < dirs->count; i++) {
        const char* path = nbCombine(c, (const char*)dirs->items[i], nlFileNameFor(c, imp));
        if (nbFileExists(path) && nlWriteTicks(path) >= newest) {
            if (!key) key = nlRuntimeKeyOf(c, nlWrapperOf(c, cache, store, imp));
            if (nlSameRuntime(c, path, key)) return path;
        }
    }
    /* the library built with the programs (Prebuilt/ next to them): taken where it is, when it belongs to the package's C and to this runtime */
    lib = nbCombine(c, nbCombine(c, nbCombine(c, nlPrebuiltFolder(c), imp->pkgName), nlRid()), nlFileNameFor(c, imp));
    if (nbFileExists(lib) && (hash = nlReadTrimmed(c, fcFmt(c, "%s.hash", lib))) != NULL && streq0(hash, nlNativeHashOf(c, cache, store, imp))) {
        if (!key) key = nlRuntimeKeyOf(c, nlWrapperOf(c, cache, store, imp));
        if (nlSameRuntime(c, lib, key)) return lib;
    }
    return NULL;
}
/* A failure is remembered for a few minutes next to where the library would be (`.failed`) */
#define NL_FAILURE_MINUTES 5
static const char* nlFailureFile(FcCtx* c, const FgInstImport* imp, const char* dir) { return nbCombine(c, dir, fcFmt(c, "%s.failed", nlFileNameFor(c, imp))); }
static long long nlNowTicks(void) {
#ifdef _WIN32
    return fwNowTicks();
#else
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    return ((long long)ts.tv_sec + 62135596800LL) * 10000000LL + ts.tv_nsec / 100;
#endif
}
static const char* nlRecentFailure(FcCtx* c, const FgInstImport* imp) {
    FcList* dirs = nlBuiltDirectories(c, imp);
    int i;
    for (i = 0; i < dirs->count; i++) {
        const char* file = nlFailureFile(c, imp, (const char*)dirs->items[i]);
        const char* text;
        long long at;
        if (!nbFileExists(file)) continue;
        at = nlWriteTicks(file);
        if (nlNowTicks() - at > NL_FAILURE_MINUTES * 60LL * 10000000LL || at < nlNewestSource(c, imp)) continue;
        if (!(text = fgReadText(c, file))) continue;
        return fcFmt(c, "%s\n(This was the result of an earlier attempt, less than %d minutes ago; delete '%s' to try again.)", text, NL_FAILURE_MINUTES, file);
    }
    return NULL;
}
static void nlRememberFailure(FcCtx* c, const FgInstImport* imp, const char* message) {
    FcList* dirs = nlBuiltDirectories(c, imp);
    int i;
    for (i = 0; i < dirs->count; i++) {
        nbMkdirs(c, (const char*)dirs->items[i]);
        if (nbWriteText(c, nlFailureFile(c, imp, (const char*)dirs->items[i]), message)) return;
    }
}
typedef struct NlResult { const char* path; const char* error; } NlResult;   /* error: the message of the PackageException (path NULL) */
static NbToolchain* nbToolchainRequire(FcCtx* c, NbToolchain* wanted, const char* reason);   /* (provider.c) */
/* The library in work as dest, whole: copied next to it first and then renamed (Locate takes any file there), with its runtime key. */
static bool nlStore(FcCtx* c, const char* outFile, const char* dest, const char* key) {
    const char* partial = fcFmt(c, "%s.%s.partial", dest, nbGuid(c));
    bool ok = nbCopyFile(c, outFile, partial) && rename(partial, dest) == 0 && nbWriteText(c, nlKeyFile(c, dest), key);
    if (nbFileExists(partial)) remove(partial);
    return ok;
}
/* the folder of a build of a package: kept with FIRE_PKGBUILD_KEEP (log says where), else deleted */
static void nlEndWork(FcCtx* c, const char* work, const char* what, FcBuf* log) {
    const char* keep = getenv("FIRE_PKGBUILD_KEEP");
    if (keep && *keep) { if (log) fcBufFmt(log, "The build of %s stays in %s\n", what, work); }
    else nbDeleteTree(c, work);
}
/* Ensure: the library of an import - located, or built now with a compiler of this machine. log gets the lines of the log (the C# `log`). */
static NlResult nlEnsure(FcCtx* c, FcDict* cache, FcStore* store, const FgInstImport* imp, FcBuf* log) {
    NlResult r = { NULL, NULL };
    const char* done;
    const char* earlier;
    const FgTarget* target = fgHostTarget();
    NbToolchain* toolchain;
    bool isC;
    const char* work;
    const char* wrapper;
    const char* cpp;
    const char* outFile;
    const char* key;
    NbCommand* k;
    NbRunResult run;
    FcList* dirs;
    int i;
    if (!imp->native) { r.error = fcFmt(c, "The import '%s' has no natives.", imp->name); return r; }
    if ((done = nlLocate(c, cache, store, imp)) != NULL) { r.path = done; return r; }
    if ((earlier = nlRecentFailure(c, imp)) != NULL) { r.error = earlier; return r; }
    if (!fgSupportsAny(imp->native, nlHostPlatformKeys(c))) {
        r.error = fcFmt(c, "The package '%s' (import '%s') has native code for %s, not for this machine (%s).", imp->pkgName, imp->name, fgJoin(c, imp->native->platforms, ", "), fgJoin(c, nlHostPlatformKeys(c), ", "));
        return r;
    }
    if (log) fcBufFmt(log, "Building the native part of the package '%s' (import '%s') for %s...\n", imp->pkgName, imp->name, nlRid());
    isC = imp->native->isC;
    /* a toolchain of this machine; none: the host offers to provide one (ToolchainProvider) */
    toolchain = nbToolchainRequire(c, nbOptimizingToolchain(c, nbConfigNew(c), target),
        fcFmt(c, "The package '%s' contains native code (%s) that has to be compiled on this machine, for the virtual machine.", imp->pkgName, isC ? "C" : "C++"));
    if (!toolchain) {
        r.error = fcFmt(c, "The native part of the package '%s' has to be built with a %s, and none was found on this machine. Install one, or use a native build, or install a package that brings a prebuilt library for %s.",
            imp->pkgName, isC ? "C compiler (gcc, clang, tcc or cl)" : "C++ compiler (g++, clang++ or cl)", nlRid());
        return r;
    }
    if (!isC && !nbCompilesCpp(toolchain)) {
        r.error = fcFmt(c, "The native part of the package '%s' is C++, and the toolchain '%s' compiles only C. Choose gcc, clang or msvc for this machine.", imp->pkgName, nbKind(toolchain));
        return r;
    }
    work = fcFmt(c, "%sfire-pkgbuild-%s", nbTempPath(c), nbGuid(c));
    {
        FcDict* instImports = fcDictNew(c, FC_KEY_PTR);
        nbRuntimeFilesWriteTo(c, store, instImports, work, target->platform);
    }
    wrapper = nlWrapperOf(c, cache, store, imp);
    cpp = nbCombine(c, work, isC ? "wrapper.c" : "wrapper.cpp");
    nbWriteText(c, cpp, wrapper);
    outFile = nbCombine(c, work, nlFileNameFor(c, imp));
    {
        /* the units of the import (`native.units`, builder.c): each compiled to an object of its own, linked into the library - position-independent, and
         * with hidden symbols where the compiler has them (only the entry points of the wrapper leave the library) */
        FcList* units = fcListNew(c);
        FcList* objects = NULL;
        for (i = 0; imp->native->units && i < imp->native->units->count; i++) {
            NbPkgUnit* u = (NbPkgUnit*)fcAlloc(c, sizeof *u);
            u->import = imp->name;
            u->file = nlFullPath(c, imp, (const char*)imp->native->units->items[i]);
            u->name = fcFmt(c, "fire_unit_%s.c", nbUnitName(c, imp->name, i, false));
            fcListAdd(c, units, u);
        }
        if (units->count > 0) {
            NbToolchain* ut = (NbToolchain*)fcAlloc(c, sizeof *ut);
            *ut = *toolchain;
            ut->args = toolchain->args ? fcListCopy(c, toolchain->args) : fcListNew(c);
            fcListAdd(c, ut->args, (void*)(streq0(nbKind(ut), "msvc") ? "/w" : "-w"));   /* (the package's code: without its warnings) */
            if (!streq0(nbKind(ut), "msvc")) fcListAdd(c, ut->args, (void*)"-fPIC");
            if (!streq0(nbKind(ut), "msvc") && !streq0(nbKind(ut), "tcc")) fcListAdd(c, ut->args, (void*)"-fvisibility=hidden");
            objects = fcListNew(c);
            for (i = 0; i < units->count; i++) {
                const NbPkgUnit* u = (const NbPkgUnit*)units->items[i];
                const char* file = nbCombine(c, work, u->name);
                const char* obj = nbChangeExtension(c, file, streq0(nbKind(ut), "msvc") ? ".obj" : ".o");
                NbRunResult ur;
                NbCommand* uk;
                if (log) fcBufFmt(log, "Compiling %s of the package '%s'...\n", fgFileNameOf(c, u->file), imp->pkgName);
                if (!nbCopyFile(c, u->file, file)) { r.error = fcFmt(c, "The unit '%s' of the package '%s' cannot be read.", u->file, imp->pkgName); nlEndWork(c, work, "the package", log); return r; }
                uk = nbCompilerCommand(c, ut, target, file, work, obj, false, NULL, false, "c", true, NULL, NULL, NULL, false);
                ur = nbRun(uk->exe, uk->args, work);
                if (!ur.ok || !nbFileExists(obj)) {
                    FcBuf text = {0};
                    const char* message;
                    fcBufStr(&text, "");
                    nbAppendRun(&text, &ur);
                    message = fcFmt(c, "Building the native part of the package '%s' failed:\n%s %s\n%s", imp->pkgName, uk->exe, uk->args, fcBufDone(c, &text));
                    nlRememberFailure(c, imp, message);
                    nlEndWork(c, work, "the package", log);
                    r.error = message;
                    return r;
                }
                free(ur.text);
                fcListAdd(c, objects, (void*)obj);
            }
            toolchain = nbWithUnitDefines(c, toolchain, units);
        }
        k = nbCompilerCommand(c, toolchain, target, cpp, work, outFile, true, nbStrings(c, 1, nbCombine(c, work, "abi")), false, isC ? "c" : "c++", false, objects, NULL, NULL, false);
    }
    run = nbRun(k->exe, k->args, work);
    if (!run.ok || !nbFileExists(outFile)) {
        const char* message;
        FcBuf text = {0};
        fcBufStr(&text, "");
        nbAppendRun(&text, &run);
        message = fcFmt(c, "Building the native part of the package '%s' failed:\n%s %s\n%s", imp->pkgName, k->exe, k->args, fcBufDone(c, &text));
        nlRememberFailure(c, imp, message);
        nlEndWork(c, work, "the package", log);
        r.error = message;
        return r;
    }
    free(run.text);
    key = nlRuntimeKeyOf(c, wrapper);
    dirs = nlBuiltDirectories(c, imp);
    for (i = 0; i < dirs->count && !r.path; i++) {
        const char* dest = nbCombine(c, (const char*)dirs->items[i], nlFileNameFor(c, imp));
        if (!nbMkdirs(c, (const char*)dirs->items[i]) || !nlStore(c, outFile, dest, key)) continue;   /* not writable: the next place */
        remove(nlFailureFile(c, imp, (const char*)dirs->items[i]));
        r.path = dest;
    }
    if (!r.path) r.error = "The built library of the package could not be stored.";
    nlEndWork(c, work, "the package", log);
    return r;
}
/* GraphicsSourceHash: the C of graphics and windows the library is built from */
static const char* nlGraphicsSourceHash(FcCtx* c, FcDict* cache, FcStore* store, const FgInstImport* graphics, const FgInstImport* windows) {
    return fcFmt(c, "%s%s", nlNativeHashOf(c, cache, store, graphics), nlNativeHashOf(c, cache, store, windows));
}
#define NL_GRAPHICS_FILE NL_PREFIX "fire_gfx" NL_EXTENSION
/* EnsureGraphics: the library of graphics and windows for the VM, built once per machine (again when a source of the two packages is newer), kept with graphics */
static long nlGraphicsWork;   /* (the BackgroundWork of the build of graphics and windows: from the line of the log to the end) */
static NlResult nlEnsureGraphicsWork(FcCtx* c, FcDict* cache, FcStore* store, const FgInstImport* graphics, const FgInstImport* windows, FcBuf* log);
static NlResult nlEnsureGraphics(FcCtx* c, FcDict* cache, FcStore* store, const FgInstImport* graphics, const FgInstImport* windows, FcBuf* log) {
    NlResult r;
    nlGraphicsWork = 0;
    r = nlEnsureGraphicsWork(c, cache, store, graphics, windows, log);
    if (nlGraphicsWork) fcBgEnd(nlGraphicsWork);
    nlGraphicsWork = 0;
    return r;
}
static NlResult nlEnsureGraphicsWork(FcCtx* c, FcDict* cache, FcStore* store, const FgInstImport* graphics, const FgInstImport* windows, FcBuf* log) {
    NlResult r = { NULL, NULL };
    long long newest = nlNewestSource(c, graphics), w = nlNewestSource(c, windows);
    const char* key = NULL;
    const FgTarget* target = fgHostTarget();
    FcList* dirs = nlBuiltDirectories(c, graphics);
    const char* prebuilt;
    const char* hash;
    NbToolchain* toolchain;
    const char* work;
    const char* wrapper;
    const char* cfile;
    const char* outFile;
    NbCommand* k;
    NbRunResult run;
    int i;
#define NL_GKEY() (key ? key : (key = nlRuntimeKeyOf(c, nlGenerateGraphicsWrapper(c, cache, store, graphics, windows, target))))
    if (w > newest) newest = w;
    for (i = 0; i < dirs->count; i++) {
        const char* path = nbCombine(c, (const char*)dirs->items[i], NL_GRAPHICS_FILE);
        if (nbFileExists(path) && nlWriteTicks(path) >= newest && nlSameRuntime(c, path, NL_GKEY())) { r.path = path; return r; }
    }
    /* (built with the programs - Prebuilt/fire-gfx/: when it belongs to the C of the two packages and to this runtime) */
    prebuilt = nbCombine(c, nbCombine(c, nbCombine(c, nlPrebuiltFolder(c), "fire-gfx"), nlRid()), NL_GRAPHICS_FILE);
    if (nbFileExists(prebuilt) && (hash = nlReadTrimmed(c, fcFmt(c, "%s.hash", prebuilt))) != NULL && streq0(hash, nlGraphicsSourceHash(c, cache, store, graphics, windows))
        && nlSameRuntime(c, prebuilt, NL_GKEY())) { r.path = prebuilt; return r; }
    if (log) fcBufFmt(log, "Building the native part of graphics and windows for %s...\n", nlRid());
    nlGraphicsWork = fcBgBegin("Building the windows and graphics of fire for this machine", 0, 0);
    toolchain = nbToolchainRequire(c, nbOptimizingToolchain(c, nbConfigNew(c), target), "The windows and the graphics of fire are C that has to be compiled on this machine, for the virtual machine.");
    if (!toolchain) { r.error = "The windows and the graphics of fire have to be built with a C compiler (gcc, clang, tcc or cl), and none was found on this machine."; return r; }
    work = fcFmt(c, "%sfire-pkgbuild-%s", nbTempPath(c), nbGuid(c));
    nbRuntimeFilesWriteTo(c, store, fcDictNew(c, FC_KEY_PTR), work, target->platform);
    cfile = nbCombine(c, work, "wrapper.c");
    wrapper = nlGenerateGraphicsWrapper(c, cache, store, graphics, windows, target);
    nbWriteText(c, cfile, wrapper);
    outFile = nbCombine(c, work, NL_GRAPHICS_FILE);
    k = nbCompilerCommand(c, toolchain, target, cfile, work, outFile, true, nbStrings(c, 1, nbCombine(c, work, "abi")), false, "c", false, NULL, NULL, NULL, false);
    run = nbRun(k->exe, k->args, work);
    if (!run.ok || !nbFileExists(outFile)) {
        FcBuf text = {0};
        fcBufStr(&text, "");
        nbAppendRun(&text, &run);
        r.error = fcFmt(c, "Building the native part of graphics and windows failed:\n%s %s\n%s", k->exe, k->args, fcBufDone(c, &text));
        nlEndWork(c, work, "graphics and windows", log);
        return r;
    }
    free(run.text);
    for (i = 0; i < dirs->count && !r.path; i++) {
        const char* dest = nbCombine(c, (const char*)dirs->items[i], NL_GRAPHICS_FILE);
        if (!nbMkdirs(c, (const char*)dirs->items[i]) || !nbCopyFile(c, outFile, dest) || !nbWriteText(c, nlKeyFile(c, dest), NL_GKEY())) continue;   /* not writable: the next place */
        r.path = dest;
    }
    if (!r.path) r.error = "The built library of graphics and windows could not be stored.";
    nlEndWork(c, work, "graphics and windows", log);
#undef NL_GKEY
    return r;
}

/* ---- the commands ---- */
/* The import of the store by its name (PackageStore.FindImport), as an InstalledImport */
static FgInstImport* nlFindImport(FcCtx* c, FcDict* cache, FcStore* store, const char* name) {
    FcPkgImport* pi = fcStoreFindImport(c, store, name);
    return pi ? fgInstalledImport(c, cache, pi) : NULL;
}
/* `pkglibplan <import>` (`graphics+windows`: the library of both): what PackageLibrary would build - the file, the keys, the wrapper, the toolchain, the command
 * with the folder of the build as <build>, the files of that folder with their SHA-256 (FrontendDump.DumpPackageLibraryPlan of the C# forge) */
static void nlDumpPlan(FcCtx* c, const char* name, const char* storeRoot, FcBuf* out) {
    FcStore store;
    FcDict* cache = fcDictNew(c, FC_KEY_PTR);
    const FgTarget* target = fgHostTarget();
    bool gfx = streq0(name, "graphics+windows");
    FgInstImport* imp = NULL;
    FgInstImport* windows = NULL;
    const char* file;
    const char* wrapper;
    const char* sourceName;
    const char* work;
    NbToolchain* toolchain;
    NbCommand* k;
    FcList* files;
    FcSha256 h;
    bool isC;
    int i;
    memset(&store, 0, sizeof store);
    store.root = storeRoot;
    imp = nlFindImport(c, cache, &store, gfx ? "graphics" : name);
    if (gfx) windows = nlFindImport(c, cache, &store, "windows");
    if (!imp || (gfx && !windows)) { fcBufFmt(out, "error: the import '%s' is not installed\n", gfx ? "graphics" : name); return; }
    if (!gfx && !imp->native) { fcBufFmt(out, "error: The import '%s' has no natives.\n", imp->name); return; }
    file = gfx ? NL_GRAPHICS_FILE : nlFileNameFor(c, imp);
    isC = gfx || imp->native->isC;
    fcBufFmt(out, "rid %s\nfile %s\n", nlRid(), file);
    if (!gfx) {
        long long newest = nlNewestSource(c, imp);
        fcBufFmt(out, "supported %s\nnewest %lld\n", fgSupportsAny(imp->native, nlHostPlatformKeys(c)) ? "yes" : "no", newest);
        fcBufFmt(out, "hash %s\n", nlNativeHashOf(c, cache, &store, imp));
    } else fcBufFmt(out, "hash %s\n", nlGraphicsSourceHash(c, cache, &store, imp, windows));
    wrapper = gfx ? nlGenerateGraphicsWrapper(c, cache, &store, imp, windows, target) : nlWrapperOf(c, cache, &store, imp);
    fcBufFmt(out, "fingerprint %s\nruntime %s\n", nlFingerprint(c, target->platform), nlRuntimeKeyOf(c, wrapper));
    toolchain = nbOptimizingToolchain(c, nbConfigNew(c), target);
    fcBufFmt(out, "toolchain %s\n", nbKind(toolchain));
    work = fcFmt(c, "%sfire-pkgbuild-%s", nbTempPath(c), nbGuid(c));
    nbRuntimeFilesWriteTo(c, &store, fcDictNew(c, FC_KEY_PTR), work, target->platform);
    sourceName = nbCombine(c, work, isC ? "wrapper.c" : "wrapper.cpp");
    nbWriteText(c, sourceName, wrapper);
    k = nbCompilerCommand(c, toolchain, target, sourceName, work, nbCombine(c, work, file), true, nbStrings(c, 1, nbCombine(c, work, "abi")), false, isC ? "c" : "c++", false, NULL, NULL, NULL, false);
    fcBufFmt(out, "command %s %s\n", k->exe, fgReplace(c, k->args, work, "<build>"));
    files = nbSortedFiles(c, work, true);
    for (i = 0; i < files->count; i++) {
        size_t n = 0;
        uint8_t* data = fbReadFile(nbCombine(c, work, (const char*)files->items[i]), &n);
        fcShaInit(&h);
        if (data) fcShaUpdate(&h, data, n);
        free(data);
        fcBufFmt(out, "file %s %s\n", (const char*)files->items[i], fcShaHex(c, &h));
    }
    nbDeleteTree(c, work);
}
/* `forge-c pkglib <import>... | --all | graphics+windows`: the libraries of the imports for the VM (Ensure: located, or built now), one path per line; a failure is
 * `error: <message>` on the error output. --all: every import of the store whose natives have functions for the library. */
static int nlMain(int argc, char** argv, const char* storeRoot) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    FcStore store;
    FcDict* cache;
    FcList* names;
    int i, result = 0;
    if (argc < 3) { fprintf(stderr, "usage: forge-c pkglib <import>... | --all | graphics+windows\n"); return 2; }
    fcCtxInit(c);
    memset(&store, 0, sizeof store);
    store.root = storeRoot;
    cache = fcDictNew(c, FC_KEY_PTR);
    names = fcListNew(c);
    for (i = 2; i < argc; i++) {
        if (streq0(argv[i], "--all")) {
            int k, f;
            if (!store.loaded) fcStoreLoad(c, &store);
            for (k = 0; k < store.imports->count; k++) {
                FgInstImport* x = fgInstalledImport(c, cache, (FcPkgImport*)store.imports->items[k]);
                bool lib = false;
                if (!x->native) continue;
                for (f = 0; f < x->native->functions->count; f++) if (!((FgPkgFn*)x->native->functions->items[f])->host) lib = true;
                if (lib && !fcListHasStr(names, x->name)) fcListAdd(c, names, (void*)x->name);
            }
        } else fcListAdd(c, names, argv[i]);
    }
    for (i = 0; i < names->count; i++) {
        const char* name = (const char*)names->items[i];
        FcBuf log = {0};
        NlResult r;
        fcBufStr(&log, "");
        if (streq0(name, "graphics+windows")) {
            FgInstImport* g = nlFindImport(c, cache, &store, "graphics");
            FgInstImport* w = nlFindImport(c, cache, &store, "windows");
            if (!g || !w) { r.path = NULL; r.error = "the standard packages graphics and windows are not installed"; }
            else r = nlEnsureGraphics(c, cache, &store, g, w, &log);
        } else {
            FgInstImport* imp = nlFindImport(c, cache, &store, name);
            if (!imp) { r.path = NULL; r.error = fcFmt(c, "The import '%s' is not installed.", name); }
            else r = nlEnsure(c, cache, &store, imp, &log);
        }
        if (log.len) fwrite(log.p, 1, log.len, stderr);
        fcBufFree(&log);
        if (r.path) printf("%s\n", r.path);
        else { FcBuf e = {0}; fcBufStr(&e, ""); fcOut8(&e, r.error); fprintf(stderr, "error: %s\n", e.p); fcBufFree(&e); result = 1; }
    }
    fcCtxFree(c);
    return result;
}
