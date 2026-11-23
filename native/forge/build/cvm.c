/* native/forge/build/cvm.c - the engine `vm` of `forge-c build` (Phase D 3, commit 7aa797f7): src/fire.Compiler/CVm.cs (VariantName, ParseVariant, SourceKey,
 * Current, CacheFolder, Locate, WriteSources, Build, BuildCommand - `forge-c build-vm` -, Pack), src/fire.Runtime/CVmPayload.cs (Append) and the part of
 * Linker.CompileAndLink that only a program with a file of its own does (PackageImports.EnsureLibraries, GraphicsLibrary.Locate: the libraries of its packages
 * and of graphics/windows, the natives of graphics bound to that library), CommandLine.Build with the engine vm (`<file> (<n> bytes)`).
 * The program `fire` (native/vm/fire.c) with the program file and the libraries behind it; `fire` is found or built like the C# forge does - the same key
 * (`fire.key`), the same cache folder - so a VM the one forge built is taken by the other. The .NET runtime (FIRE_VM_ENGINE=dotnet) is not ported. */

#ifdef _WIN32
#define NV_EXTENSION ".exe"   /* (CVm.VariantName with the program's extension: fire.exe) */
#else
#define NV_EXTENSION ""
#endif
/* the store whose headers go with the runtime of the VM (NativeRuntimeFiles.WriteTo: PackageHeaders) - set by the commands */
static const char* nvStoreRoot = "Packages";
/* VariantName: fire, fire-i32, fire-f32, fire-i32-f32, fire-i16, fire-i16-f32 */
static const char* nvVariantName(FcCtx* c, int intWidth, int floatWidth) {
    return fcFmt(c, "fire%s%s", intWidth == 64 ? "" : intWidth == 32 ? "-i32" : "-i16", floatWidth == 64 ? "" : "-f32");
}
/* ParseVariant: `i32-f32`, `fire-i32-f32`, `default`; false when the text names none */
static bool nvParseVariant(FcCtx* c, const char* text, int* intWidth, int* floatWidth) {
    char* t = (char*)fgTrim(c, text);
    char* p;
    FcList* parts;
    int i;
    for (p = t; *p; p++) *p = (char)tolower((unsigned char)*p);
    if (fcStartsWith(t, "fire")) t += 4;
    while (*t == '-') t++;
    p = t + strlen(t);
    while (p > t && p[-1] == '-') *--p = 0;
    *intWidth = 64; *floatWidth = 64;
    if (!*t || streq0(t, "default")) return true;
    parts = fgSplit(c, t, '-');
    for (i = 0; i < parts->count; i++) {
        const char* part = (const char*)parts->items[i];
        if (streq0(part, "i32")) *intWidth = 32;
        else if (streq0(part, "i16")) *intWidth = 16;
        else if (streq0(part, "f32")) *floatWidth = 32;
        else return false;
    }
    return true;
}

/* Sources(): what forge carries of the VM (fire.Compiler.csproj: the .c and .h of native/vm, the .h of native/base, the .def of native/tables) - their path under native/,
 * in the ordinal order of the paths */
typedef struct NvSource { const char* path; const char* file; } NvSource;
static int nvCmpSource(const void* a, const void* b) { return strcmp((*(const NvSource* const*)a)->path, (*(const NvSource* const*)b)->path); }
static void nvAddSources(FcCtx* c, FcList* out, const char* sub, const char* ext1, const char* ext2) {
    const char* dir = nbCombine(c, nbNativeRoot, sub);
    DIR* d = opendir(dir);
    struct dirent* e;
    if (!d) return;
    while ((e = readdir(d)) != NULL) {
        const char* name = e->d_name;
        NvSource* s;
        if (!(fcEndsWith(name, ext1) || (ext2 && fcEndsWith(name, ext2))) || !nbFileExists(nbCombine(c, dir, name))) continue;
        s = (NvSource*)fcAlloc(c, sizeof *s);
        s->path = fcFmt(c, "%s/%s", sub, name);
        s->file = nbCombine(c, dir, name);
        fcListAdd(c, out, s);
    }
    closedir(d);
}
static FcList* nvSources(FcCtx* c) {
    FcList* l = fcListNew(c);
    nvAddSources(c, l, "vm", ".c", ".h");
    nvAddSources(c, l, "base", ".h", NULL);
    nvAddSources(c, l, "tables", ".def", NULL);
    if (l->count > 1) qsort(l->items, (size_t)l->count, sizeof(void*), nvCmpSource);
    return l;
}
/* SourceKey: a hash of the sources of the VM (each its path and a newline, then its bytes) and of the runtime with its platform package (Fingerprint) */
static const char* nvKey;
static const char* nvSourceKey(FcCtx* c) {
    FcList* sources;
    FcSha256 h;
    const char* runtime;
    int i;
    if (nvKey) return nvKey;
    sources = nvSources(c);
    fcShaInit(&h);
    for (i = 0; i < sources->count; i++) {
        const NvSource* s = (const NvSource*)sources->items[i];
        size_t n = 0;
        uint8_t* body = fbReadFile(s->file, &n);
        fcShaUpdate(&h, (const uint8_t*)s->path, strlen(s->path));
        fcShaUpdate(&h, (const uint8_t*)"\n", 1);
        if (body && n) fcShaUpdate(&h, body, n);
        free(body);
    }
    runtime = fcFmt(c, "runtime %s", nlFingerprint(c, fgHostTarget()->platform));
    fcShaUpdate(&h, (const uint8_t*)runtime, strlen(runtime));
    nvKey = strdup(fcShaHex(c, &h));
    return nvKey;
}
static const char* nvKeyOf(FcCtx* c, int intWidth, int floatWidth) { return fcFmt(c, "%s %s", nvSourceKey(c), nvVariantName(c, intWidth, floatWidth)); }
static bool nvCurrent(FcCtx* c, const char* exe, int intWidth, int floatWidth) {
    const char* key;
    if (!nbFileExists(exe) || !nbFileExists(fcFmt(c, "%s.key", exe))) return false;
    key = nlReadTrimmed(c, fcFmt(c, "%s.key", exe));
    return key && streq0(key, nvKeyOf(c, intWidth, floatWidth));
}
/* CacheFolder: <LocalApplicationData>/fire/vm/<rid> - LocalApplicationData of .NET on Linux and macOS: $XDG_DATA_HOME (when it is a rooted path), else ~/.local/share
 * (created; without a home: the temp folder) */
static const char* nvCacheFolder(FcCtx* c) {
    const char* xdg = getenv("XDG_DATA_HOME");
    const char* home = getenv("HOME");
    const char* local = NULL;
    if (xdg && nbIsRooted(xdg)) local = xdg;
    else if (home && *home) local = nbCombine(c, home, ".local/share");
    if (local) nbMkdirs(c, local);
    if (!local || !*local) local = nbTempPath(c);
    return nbCombine(c, nbCombine(c, nbCombine(c, local, "fire"), "vm"), nlRid());
}

/* WriteSources: the sources of the VM in the layout of the repository (vm/, base/, tables/, runtime/, platform/, abi/) and fire_vm.c, which includes vm/fire.c
 * with the defines of the widths; returns that file */
static const char* nvWriteSources(FcCtx* c, const char* folder, int intWidth, int floatWidth) {
    FcList* sources = nvSources(c);
    FcStore store;
    const char* runtime = nbCombine(c, folder, "runtime");
    static const char* const subs[] = { "platform", "abi" };
    FcBuf sb = {0};
    const char* main;
    int i;
    nbMkdirs(c, folder);
    for (i = 0; i < sources->count; i++) {
        const NvSource* s = (const NvSource*)sources->items[i];
        const char* target = nbCombine(c, folder, s->path);
        nbMkdirs(c, fcDirectoryName(c, target));
        nbCopyFile(c, s->file, target);
    }
    memset(&store, 0, sizeof store);
    store.root = nvStoreRoot;
    nbRuntimeFilesWriteTo(c, &store, fcDictNew(c, FC_KEY_PTR), runtime, fgHostTarget()->platform);
    for (i = 0; i < 2; i++) {
        const char* from = nbCombine(c, runtime, subs[i]);
        const char* to = nbCombine(c, folder, subs[i]);
        if (!nbDirExists(from)) continue;
        if (nbDirExists(to)) nbDeleteTree(c, to);
        rename(from, to);
    }
    fcBufStr(&sb, "/* The program fire (native/vm/fire.c), built by forge (src/fire.Compiler/CVm.cs). */\n");
    fcBufStr(&sb, "// fire-link: pthread\n");
#ifndef __APPLE__
    fcBufStr(&sb, "// fire-link: dl\n");
#endif
    if (intWidth == 16) fcBufStr(&sb, "#define FIRE_INT16 1\n");
    else if (intWidth == 32) fcBufStr(&sb, "#define FIRE_INT32 1\n");
    if (floatWidth == 32) fcBufStr(&sb, "#define FIRE_FLOAT32 1\n");
    fcBufStr(&sb, "#include \"vm/fire.c\"\n");
    main = nbCombine(c, folder, "fire_vm.c");
    nbWriteText(c, main, fcBufDone(c, &sb));
    return main;
}
static void nvMakeExecutable(const char* path) {
    struct stat st;
    if (stat(path, &st) == 0) chmod(path, st.st_mode | S_IXUSR | S_IXGRP | S_IXOTH);
}
/* Build: the VM for the widths into exe with the optimising C compiler of this machine (with its stamp `.key`); log gets the line before the build.
 * false: *text says why (the command and the compiler's output) */
static bool nvBuild(FcCtx* c, const char* exe, int intWidth, int floatWidth, FcBuf* log, const char** text) {
    const FgTarget* target = fgHostTarget();
    NbToolchain* volatile toolchain = NULL;
    const char* volatile failure = NULL;
    const char* work;
    const char* main;
    const char* built;
    const char* partial;
    NbCommand* k;
    NbRunResult run;
    FcBuf out = {0};
    bool ok;
    FC_TRY(c) {
        toolchain = nbToolchainRequire(c, nbOptimizingToolchain(c, nbConfigNew(c), target),
            "Programs of the virtual machine are packed with the VM in C (the program fire), which is compiled once on this machine.");
    } FC_CATCH(c) {
        if (c->err.kind != FC_ERR_INTERNAL || !c->err.message) fcRethrow(c);
        failure = fcStartsWith(c->err.message, "native-config: ") ? c->err.message + 15 : c->err.message;
    } FC_END_TRY;
    if (failure) { *text = failure; return false; }
    if (!toolchain) { *text = "No C compiler was found on this machine."; return false; }
    work = fcFmt(c, "%sfire-vmbuild-%s", nbTempPath(c), nbGuid(c));
    if (log) fcBufFmt(log, "Building the VM in C (%s) for %s with %s...\n", nvVariantName(c, intWidth, floatWidth), nlRid(), nbKind(toolchain));
    main = nvWriteSources(c, work, intWidth, floatWidth);
    built = nbCombine(c, work, fcFmt(c, "%s" NV_EXTENSION, nvVariantName(c, intWidth, floatWidth)));
    k = nbCompilerCommand(c, toolchain, target, main, work, built, false, NULL, false, "c", false, NULL, NULL, NULL, false);
    run = nbRun(k->exe, k->args, work);
    fcBufStr(&out, "");
    ok = run.ok;
    nbAppendRun(&out, &run);
    if (!ok || !nbFileExists(built)) {
        *text = fcFmt(c, "%s %s\n%s", k->exe, k->args, fcBufDone(c, &out));
        nbDeleteTree(c, work);
        return false;
    }
    *text = fcBufDone(c, &out);
    nbMkdirs(c, fcDirectoryName(c, fcFullPath(c, ".", exe)));
    /* (copied next to it and renamed: a VM in its place is always whole) */
    partial = fcFmt(c, "%s.%s.partial", exe, nbGuid(c));
    if (!nbCopyFile(c, built, partial)) { *text = fcFmt(c, "Could not write '%s'.", partial); nbDeleteTree(c, work); return false; }
    nvMakeExecutable(partial);
    if (rename(partial, exe) != 0) { remove(partial); *text = fcFmt(c, "Could not write '%s'.", exe); nbDeleteTree(c, work); return false; }
    nbWriteText(c, fcFmt(c, "%s.key", exe), nvKeyOf(c, intWidth, floatWidth));
    nbDeleteTree(c, work);
    return true;
}
/* Locate: FIRE_VM_EXE, next to forge-c (current by its stamp), in the cache, or built now. NULL: *error says what to do (the InvalidOperationException) */
static const char* nvLocate(FcCtx* c, int intWidth, int floatWidth, FcBuf* log, const char** error) {
    const char* name = fcFmt(c, "%s" NV_EXTENSION, nvVariantName(c, intWidth, floatWidth));
    const char* given = getenv("FIRE_VM_EXE");
    const char* beside;
    const char* cached;
    const char* text = NULL;
    if (given && *given) {
        /* a file: the VM of 64-bit widths; its variants next to it */
        const char* path = intWidth == 64 && floatWidth == 64 ? given : nbCombine(c, fcDirectoryName(c, fcFullPath(c, ".", given)), name);
        if (nbFileExists(path)) return path;
        if (intWidth == 64 && floatWidth == 64) { *error = fcFmt(c, "FIRE_VM_EXE names '%s', which does not exist.", given); return NULL; }
    }
    beside = nbCombine(c, nbBaseDirectory, name);
    if (nvCurrent(c, beside, intWidth, floatWidth)) return beside;
    cached = nbCombine(c, nbCombine(c, nvCacheFolder(c), fcStrN(c, nvSourceKey(c), 16)), name);
    if (nbFileExists(cached)) return cached;
    if (!nvBuild(c, cached, intWidth, floatWidth, log, &text)) {
        *error = fcFmt(c, "The VM in C (%s) for the program could not be built on this machine:\n%s\n"
            "Install a C compiler (gcc, clang, tcc or cl), or set FIRE_VM_ENGINE=dotnet to pack the program with the .NET runtime, or build it with --engine native.",
            nvVariantName(c, intWidth, floatWidth), text);
        return NULL;
    }
    return cached;
}

/* `forge-c build-vm <folder> [--variant v]... [--all] [--rid r]` (BuildCommand): the VM into a folder, only when it is not current */
static int nvBuildVmMain(int argc, char** argv, const char* storeRoot) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    const char* folder = NULL;
    const char* rid = NULL;
    int widths[16][2], count = 0, i, k, failed = 0;
    static const int all[6][2] = { { 64, 64 }, { 32, 64 }, { 64, 32 }, { 32, 32 }, { 16, 64 }, { 16, 32 } };
    nvStoreRoot = storeRoot;
    fcCtxInit(c);
    for (i = 2; i < argc; i++) {
        if (streq0(argv[i], "--all")) { for (k = 0; k < 6 && count < 16; k++) { widths[count][0] = all[k][0]; widths[count][1] = all[k][1]; count++; } }
        else if (streq0(argv[i], "--variant") && i + 1 < argc) {
            int iw, fw;
            if (!nvParseVariant(c, argv[++i], &iw, &fw)) { fprintf(stderr, "'%s' names no variant of the VM (default, i32, f32, i32-f32, i16, i16-f32).\n", argv[i]); fcCtxFree(c); return 2; }
            if (count < 16) { widths[count][0] = iw; widths[count][1] = fw; count++; }
        }
        else if (streq0(argv[i], "--rid") && i + 1 < argc) rid = argv[++i];
        else if (!folder) folder = argv[i];
    }
    if (!folder) { fprintf(stderr, "build-vm <folder> [--variant <v>]... [--all] [--rid <rid>]\n"); fcCtxFree(c); return 2; }
    if (rid && *fgTrim(c, rid) && !fcAsciiIEq(fgTrim(c, rid), nlRid())) {
        printf("The VM in C is not built for %s on %s: forge builds it on the machine of the program when it packs one.\n", rid, nlRid());
        fcCtxFree(c);
        return 0;
    }
    if (count == 0) { widths[0][0] = 64; widths[0][1] = 64; count = 1; }
    for (i = 0; i < count; i++) {
        const char* exe;
        const char* text = NULL;
        FcBuf log = {0};
        bool seen = false;
        for (k = 0; k < i; k++) if (widths[k][0] == widths[i][0] && widths[k][1] == widths[i][1]) seen = true;   /* (Distinct) */
        if (seen) continue;
        exe = nbCombine(c, folder, fcFmt(c, "%s" NV_EXTENSION, nvVariantName(c, widths[i][0], widths[i][1])));
        if (nvCurrent(c, exe, widths[i][0], widths[i][1])) { printf("%s (up to date)\n", exe); continue; }
        fcBufStr(&log, "");
        if (nvBuild(c, exe, widths[i][0], widths[i][1], &log, &text)) { fputs(log.p, stderr); printf("%s\n", exe); }
        else { fputs(log.p, stderr); failed++; fprintf(stderr, "The VM in C (%s) could not be built: %s\n", nvVariantName(c, widths[i][0], widths[i][1]), text); }
        fcBufFree(&log);
    }
    fcCtxFree(c);
    return failed == 0 ? 0 : 1;
}

/* ---- CVmPayload.Append ---- */
static void nvU8(FcBuf* b, unsigned v) { char x = (char)v; fcBufPut(b, &x, 1); }
static void nvLe(FcBuf* b, uint64_t v, int n) { int i; for (i = 0; i < n; i++) nvU8(b, (unsigned)((v >> (8 * i)) & 0xFF)); }
static uint64_t nvFnv(const uint8_t* data, size_t n) {
    uint64_t h = 14695981039346656037ULL;
    size_t i;
    for (i = 0; i < n; i++) { h ^= data[i]; h *= 1099511628211ULL; }
    return h;
}
typedef struct NvItem { int kind; const char* name; const uint8_t* data; size_t size; } NvItem;
/* the entries and the index behind the end of the file (the executable `fire`); false: a name that is no file name, or the file could not be written */
static bool nvAppend(FcCtx* c, const char* path, FcList* items, const char** error) {
    FILE* f = fopen(path, "ab");
    FcBuf index = {0};
    long long offset;
    uint64_t* offsets;
    int i;
    if (!f) { *error = fcFmt(c, "Could not write '%s'.", path); return false; }
    fseek(f, 0, SEEK_END);
    offset = ftell(f);
    offsets = (uint64_t*)fcAlloc(c, sizeof(uint64_t) * (size_t)(items->count + 1));
    for (i = 0; i < items->count; i++) {
        const NvItem* it = (const NvItem*)items->items[i];
        if (!*it->name || strchr(it->name, '/') || strchr(it->name, '\\') || strstr(it->name, "..")) { fclose(f); *error = fcFmt(c, "'%s' is no file name for the payload.", it->name); return false; }
        offsets[i] = (uint64_t)offset;
        if (it->size && fwrite(it->data, 1, it->size, f) != it->size) { fclose(f); *error = fcFmt(c, "Could not write '%s'.", path); return false; }
        offset += (long long)it->size;
    }
    fcBufStr(&index, "");
    index.len = 0;
    nvLe(&index, (uint64_t)items->count, 4);
    for (i = 0; i < items->count; i++) {
        const NvItem* it = (const NvItem*)items->items[i];
        size_t n = strlen(it->name);
        if (n > 255) { fclose(f); fcBufFree(&index); *error = fcFmt(c, "The name '%s' is too long for the payload.", it->name); return false; }
        nvU8(&index, (unsigned)it->kind);
        nvLe(&index, n, 2);
        fcBufPut(&index, it->name, n);
        nvLe(&index, offsets[i], 8);
        nvLe(&index, it->size, 8);
        nvLe(&index, nvFnv(it->data, it->size), 8);
    }
    {
        size_t indexLength = index.len;
        nvLe(&index, (uint64_t)offset, 8);
        nvLe(&index, indexLength, 4);
        nvLe(&index, 1, 4);
        fcBufPut(&index, "FIREVMP1", 8);
    }
    if (fwrite(index.p, 1, index.len, f) != index.len) { fclose(f); fcBufFree(&index); *error = fcFmt(c, "Could not write '%s'.", path); return false; }
    fcBufFree(&index);
    if (fclose(f) != 0) { *error = fcFmt(c, "Could not write '%s'.", path); return false; }
    return true;
}

/* ---- what the linker does for a program with a file of its own (Linker.CompileAndLink with outname, the engine vm) ---- */
/* PackageImports.EnsureLibraries and GraphicsLibrary.Locate: the libraries of the program's packages (the imports with natives in a library, in the ordinal order of
 * their keys) and of graphics/windows; the natives of graphics then go to that library (linked->graphicsNatives, in the order of the registry). A library of a
 * package that cannot be had is a PackageException (FC_ERR_PACKAGE); one of graphics is a line on the log and the program goes without it. */
static FcList* nvEnsureLibraries(FcCtx* c, FcLinked* linked, FcStore* store, const FcNatives* registry, FcBuf* log) {
    FcList* files = fcListNew(c);
    FcList* keys = fcListNew(c);
    FcDict* cache = fcDictNew(c, FC_KEY_PTR);
    int i, f;
    FC_DICT_EACH(linked->nativeImports, e) if (fcStartsWith((const char*)e->key, "pkg:")) fcListAdd(c, keys, (void*)e->key);
    if (keys->count > 1) qsort(keys->items, (size_t)keys->count, sizeof(void*), fcCmpOrdinal);
    for (i = 0; i < keys->count; i++) {
        FcPkgImport* pi = fcStoreFindKey(c, store, (const char*)keys->items[i]);
        FgInstImport* imp;
        bool library = false;
        NlResult r;
        if (!pi) continue;
        imp = fgInstalledImport(c, cache, pi);
        if (!imp->native) continue;
        for (f = 0; f < imp->native->functions->count; f++) if (!((FgPkgFn*)imp->native->functions->items[f])->host) library = true;
        if (!library) continue;
        r = nlEnsure(c, cache, store, imp, NULL);
        if (!r.path) fcThrow(c, FC_ERR_PACKAGE, 0, 0, NULL, r.error);
        fcListAdd(c, files, (void*)r.path);
    }
    /* (the VM in C has graphics and windows only in C: its packed program always carries the library) */
    if (fcDictHas(linked->nativeImports, "graphics")) {
        FgInstImport* g = nlFindImport(c, cache, store, "graphics");
        FgInstImport* w = nlFindImport(c, cache, store, "windows");
        NlResult r;
        if (!g || !w) { r.path = NULL; r.error = NULL; }
        else {
            r = nlEnsureGraphics(c, cache, store, g, w, log);
            if (!r.path && log) fcBufFmt(log, "error: %s\n", r.error);
        }
        if (r.path) {
            FcList* names = fcListNew(c);
            for (i = 0; i < registry->names->count; i++) if (fcStartsWith((const char*)registry->names->items[i], "__GRPH")) fcListAdd(c, names, registry->names->items[i]);
            linked->graphicsNatives = names;
            fcListAdd(c, files, (void*)r.path);
        }
    }
    return files;
}
/* CVm.Pack and the move of Linker.CompileAndLink: the VM of the program's widths, then the program file (without line tables) and the libraries; the subsystem
 * of a Windows program goes into the copy of the VM first (PeResourceEditor.SetSubsystem: a file that is no PE file is left alone). false: *error. */
static bool nvPack(FcCtx* c, FcLinked* linked, FcList* libraries, const char* outName, FcBuf* log, const char** error) {
    const char* vm = nvLocate(c, linked->intWidth, linked->floatWidth, log, error);
    const char* tempfile = fcFmt(c, "%s.tmp", outName);
    const char* temp = fcFmt(c, "%s.%s.vm", tempfile, nbGuid(c));
    FcList* items = fcListNew(c);
    FcList* names = fcListNew(c);
    FcBuf program = {0};
    NvItem* it;
    bool ok;
    int i, k;
    if (!vm) return false;
    if (!nbCopyFile(c, vm, temp)) { *error = fcFmt(c, "Could not write '%s'.", temp); return false; }
    nbPeSetSubsystem(temp, linked->asmInfo && linked->asmInfo->gui ? 2 : 3);
    fcBufStr(&program, "");
    program.len = 0;
    fcProgramFileWrite(c, linked, linked->stackTrace != STM_Off, &program);   /* (a stack trace needs the line tables: docs/spec/STACKTRACE.md) */
    it = (NvItem*)fcAlloc(c, sizeof *it);
    it->kind = 0; it->name = "program.fbc"; it->size = program.len;
    it->data = (const uint8_t*)fcAlloc(c, program.len + 1);
    if (program.len) memcpy((void*)it->data, program.p, program.len);
    fcBufFree(&program);
    fcListAdd(c, items, it);
    for (i = 0; libraries && i < libraries->count; i++) {
        const char* file = nbFileName((const char*)libraries->items[i]);
        bool seen = false;
        size_t n = 0;
        uint8_t* data;
        for (k = 0; k < names->count; k++) if (fcAsciiIEq((const char*)names->items[k], file)) seen = true;
        if (seen) continue;
        fcListAdd(c, names, (void*)file);
        data = fbReadFile((const char*)libraries->items[i], &n);
        it = (NvItem*)fcAlloc(c, sizeof *it);
        it->kind = 1; it->name = file; it->size = n;
        it->data = (const uint8_t*)fcAlloc(c, n + 1);
        if (data && n) memcpy((void*)it->data, data, n);
        free(data);
        fcListAdd(c, items, it);
    }
    ok = nvAppend(c, temp, items, error);
    if (ok) {
        nvMakeExecutable(temp);
        ok = rename(temp, tempfile) == 0 && rename(tempfile, outName) == 0;
        if (!ok) *error = fcFmt(c, "Could not write '%s'.", outName);
    }
    if (nbFileExists(temp)) remove(temp);
    if (nbFileExists(tempfile)) remove(tempfile);
    return ok;
}
/* the size of a file (FileInfo.Length) */
static long long nvFileLength(const char* path) { struct stat st; return stat(path, &st) == 0 ? (long long)st.st_size : 0; }
/* FIRE_VM_ENGINE=dotnet (the .NET runtime instead of `fire`) is not ported */
static void nvRefuseDotnet(FcCtx* c) {
    const char* e = getenv("FIRE_VM_ENGINE");
    const char* t = e ? fgTrim(c, e) : "";
    if (fcAsciiIEq(t, "dotnet") || fcAsciiIEq(t, ".net") || fcAsciiIEq(t, "net"))
        fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: FIRE_VM_ENGINE=dotnet (a program packed with the .NET runtime; forge-c packs with the VM in C)");
}

/* CommandLine.Build with the engine vm for one file: the program linked as for `forge bytecode`, packed with the VM into output (default out.exe);
 * `<full path> (<n> bytes)` on out. Errors as forge: a compile error is DescribeErrors (exit 1). */
static int nvBuildFile(FcCtx* c, const char* path, const char* output, const char* prelude, const char* storeRoot, bool strict, int mode, FcBuf* out, FcBuf* err) {
    FcFrontend* fe;
    FcLinked* linked;
    FcList* libraries;
    const char* error = NULL;
    const char* to = output ? output : "out.exe";
    nvRefuseDotnet(c);
    nvStoreRoot = storeRoot;
    linked = fcLinkFileFe(c, path, prelude, storeRoot, strict, mode, &fe);
    libraries = nvEnsureLibraries(c, linked, &fe->linker->store, &fe->linker->registry, err);
    if (!nvPack(c, linked, libraries, to, err, &error)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "vm: %s", error));
    fcBufFmt(out, "%s (%lld bytes)\n", fcFullPath(c, ".", to), nvFileLength(to));
    return 0;
}
