/* native/forge/build/builder.c - the native builder (Phase D 2, commit 6f21d1a4): src/fire.Compiler/NativeBuilder.cs for linkage "source" (Build with the files,
 * the commands, the units side by side and the cache; WriteFiles, StrippingOf, OptimizeOf, WithDirection, ToolchainStamp, ArchiveCommand, BuildCommands,
 * BuildJobs, SplitCommands, CompileUnits, CppFallback, CppFallbackNote, NativesInCpp, CompilerCommand, IsSizeProfile, RunCommand, Run, CopyDirectory,
 * DefaultOutput), NativeBuilder.Quick.cs (QuickBuild), NativeBuilder.ProgramCache.cs (ProgramCache), src/fire.Native/NativeRuntimeFiles.cs (WriteTo: the files
 * of native/ that the C# forge carries as resources, read from the folder native/ of the repository here - FIRE_NATIVE_ROOT, else next to the tables) and from
 * src/fire.Package.Manager PackageHeaders.WriteTo and NativeLibraryCache.Root. Not yet: linkage static, shared and plugin (BuildStatic, LinkDlls,
 * WriteStaticFiles - the generator has no modules yet), `--stats` (NativeStats), the version info and icon of a Windows program (ApplyPeMetadata), the disk image
 * of bios16 (WatcomToolchain.AfterLink), ToolchainProvider (no compiler: the build says so), BuildMonitor and BuildTimes, TestToolchain. */

/* ---- QuickBuild (NativeBuilder.Quick.cs) ---- */
/* The level of `--quick` / `--quick=<level>`: 0 or 1 (`0`, `1`, `O0`, `O1`, `-O0`, `-O1`); -1: not a level. */
static int nbQuickParseLevel(const char* text) {
    const char* t = text ? text : "";
    size_t n;
    while (*t == ' ' || *t == '\t') t++;
    n = strlen(t);
    while (n > 0 && (t[n - 1] == ' ' || t[n - 1] == '\t')) n--;
    while (n > 0 && *t == '-') { t++; n--; }
    while (n > 0 && (*t == 'O' || *t == 'o')) { t++; n--; }
    if (n == 0 || (n == 1 && *t == '0')) return 0;
    if (n == 1 && *t == '1') return 1;
    return -1;
}
/* The optimisation flag of a quick build with this toolchain kind (NULL: the kind keeps its own). */
static const char* nbQuickFlag(const char* kind, int level) {
    if (streq0(kind, "gcc") || streq0(kind, "clang") || streq0(kind, "emscripten")) return level == 0 ? "-O0" : "-O1";
    if (streq0(kind, "msvc")) return level == 0 ? "/Od" : "/O1";
    return NULL;
}
static bool nbQuickSplits(const char* kind) { return fgSplitAutoToolchain(kind); }
/* The split mode of a build: a quick build with a toolchain that splits is split, every other build keeps the configuration's mode. */
static int nbQuickSplitOf(const NbConfig* k, const char* kind) { return k->quick >= 0 && nbQuickSplits(kind) ? FG_SPLIT_ON : k->split; }
static NbToolchain* nbQuickApply(FcCtx* c, NbToolchain* t, int level) {
    const char* flag = nbQuickFlag(nbKind(t), level);
    NbToolchain* copy;
    if (!flag || streq0(t->optimization, flag)) return t;
    copy = nbCopy(c, t);
    copy->optimization = flag;
    return copy;
}
static const char* nbQuickLabel(FcCtx* c, int level, const char* kind) {
    const char* flag;
    if (!kind) return fcFmt(c, "Schnellbuild (-O%d, geteilt)", level);
    flag = nbQuickFlag(kind, level);
    if (!flag) return fcFmt(c, "Schnellbuild (%s: wie ein normaler Build)", kind);
    return nbQuickSplits(kind) ? fcFmt(c, "Schnellbuild (%s, geteilt)", flag) : fcFmt(c, "Schnellbuild (%s, eine Datei)", flag);
}
static const char* nbQuickLogLine(FcCtx* c, int level, const char* kind) {
    if (!nbQuickFlag(kind, level))
        return fcFmt(c, "%s: the toolchain '%s' keeps its optimisation and builds one file (only gcc, clang, Emscripten and msvc build quicker).", nbQuickLabel(c, level, kind), kind);
    return fcFmt(c, "%s: a test build, not a release - the C compiler with %s%s; the natives of the packages keep their optimisation.", nbQuickLabel(c, level, kind),
        nbQuickFlag(kind, level), nbQuickSplits(kind) ? ", the program in units built side by side" : "");
}

/* ---- the direction ---- */
/* Whether the optimisation setting asks for the small-program profile ("size", or -Os). */
static bool nbIsSizeProfile(const char* o) { return o && (fcAsciiIEq(o, "size") || streq0(o, "-Os")); }
/* OptimizeOf (without a project): `--optimize`, then fire.native.json, then the target's. 0 speed, 1 size */
static int nbOptimizeOf(const NbConfig* k, const FgTarget* target) {
    if (k && k->optimizeFromCommandLine && k->optimize >= 0) return k->optimize;
    if (k && k->optimize >= 0) return k->optimize;
    return target->optimize;
}
/* StrippingOf (without a project) */
static int nbStrippingOf(const NbConfig* k) { return k->stripping; }
/* The toolchain with the flags of the direction: size is the small-program profile ("size"); speed keeps a flag of its own and otherwise is the default of the kind. */
static NbToolchain* nbWithDirection(FcCtx* c, NbToolchain* t, int direction) {
    const char* k = nbKind(t);
    const char* flags;
    NbToolchain* copy;
    if (streq0(k, "files") || streq0(k, "custom")) return t;
    flags = direction == 1 ? "size" : !nbIsSizeProfile(t->optimization) && t->optimization ? t->optimization : nbIsWatcom(k) ? "-Ot" : NULL;
    if ((flags == NULL && t->optimization == NULL) || (flags && t->optimization && streq0(flags, t->optimization))) return t;
    copy = nbCopy(c, t);
    copy->optimization = flags;
    return copy;
}

/* ---- the files of a build (NativeRuntimeFiles, PackageHeaders) ---- */
static const char* nbNativeRoot = "native";   /* the folder native/ of the repository (set by main) */
static bool nbMkdirs(FcCtx* c, const char* dir) {
    char* p = (char*)fcStr(c, dir);
    size_t i;
    for (i = 1; p[i]; i++)
        if (p[i] == '/') { p[i] = 0; mkdir(p, 0777); p[i] = '/'; }
    return mkdir(p, 0777) == 0 || nbDirExists(p);
}
static bool nbWriteBytes(FcCtx* c, const char* path, const void* data, size_t n) {
    FILE* f;
    nbMkdirs(c, nbDirName(c, path));
    if (!(f = fopen(path, "wb"))) return false;
    if (n) fwrite(data, 1, n, f);
    fclose(f);
    return true;
}
static bool nbCopyFile(FcCtx* c, const char* from, const char* to) {
    size_t n = 0;
    uint8_t* data = fbReadFile(from, &n);
    bool ok;
    if (!data) return false;
    ok = nbWriteBytes(c, to, data, n);
    free(data);
    return ok;
}
/* The files below dir (relative, `/`-separated, sorted ordinally); recursive: with the folders below it. */
static void nbListFiles(FcCtx* c, const char* dir, const char* rel, bool recursive, FcList* out) {
    DIR* d = opendir(dir);
    struct dirent* e;
    if (!d) return;
    while ((e = readdir(d)) != NULL) {
        const char* full;
        const char* name;
        if (streq0(e->d_name, ".") || streq0(e->d_name, "..")) continue;
        full = nbCombine(c, dir, e->d_name);
        name = *rel ? fcFmt(c, "%s/%s", rel, e->d_name) : fcStr(c, e->d_name);
        if (nbFileExists(full)) fcListAdd(c, out, (void*)name);
        else if (recursive && nbDirExists(full)) nbListFiles(c, full, name, true, out);
    }
    closedir(d);
}
static FcList* nbSortedFiles(FcCtx* c, const char* dir, bool recursive) {
    FcList* l = fcListNew(c);
    nbListFiles(c, dir, "", recursive, l);
    if (l->count > 1) qsort(l->items, (size_t)l->count, sizeof(void*), fcCmpOrdinal);
    return l;
}
/* The resources of fire.Native.csproj: (resource name, file) - fire_rt.h, fire_rt_cpp.hpp, fire_json.h, fire_debug.h, cc/<name>.h, abi/<all>, platform/<folders>/<name>.hpp/.h/.asm */
typedef struct NbResource { const char* name; const char* file; } NbResource;
static FcList* nbResources(FcCtx* c) {
    FcList* r;
    FcList* files;
    int i;
    static const char* const runtime[] = { "fire_rt.h", "fire_rt_cpp.hpp", "fire_json.h", "fire_debug.h" };
#define NB_RES(n, f) do { NbResource* x_ = (NbResource*)fcAlloc(c, sizeof *x_); x_->name = (n); x_->file = (f); fcListAdd(c, r, x_); } while (0)
    r = fcListNew(c);
    for (i = 0; i < 4; i++) NB_RES(runtime[i], nbCombine(c, nbCombine(c, nbNativeRoot, "runtime"), runtime[i]));
    files = nbSortedFiles(c, nbCombine(c, nbNativeRoot, "runtime/cc"), false);
    for (i = 0; i < files->count; i++) if (fcEndsWith((const char*)files->items[i], ".h")) NB_RES(fcFmt(c, "cc/%s", (const char*)files->items[i]), nbCombine(c, nbCombine(c, nbNativeRoot, "runtime/cc"), (const char*)files->items[i]));
    files = nbSortedFiles(c, nbCombine(c, nbNativeRoot, "abi"), false);
    for (i = 0; i < files->count; i++) NB_RES(fcFmt(c, "abi/%s", (const char*)files->items[i]), nbCombine(c, nbCombine(c, nbNativeRoot, "abi"), (const char*)files->items[i]));
    files = nbSortedFiles(c, nbCombine(c, nbNativeRoot, "platform"), true);
    for (i = 0; i < files->count; i++) {
        const char* f = (const char*)files->items[i];
        if (fcEndsWith(f, ".hpp") || fcEndsWith(f, ".h") || fcEndsWith(f, ".asm")) NB_RES(fcFmt(c, "platform/%s", f), nbCombine(c, nbCombine(c, nbNativeRoot, "platform"), f));
    }
#undef NB_RES
    return r;
}
static void nbWriteResource(FcCtx* c, const NbResource* res, const char* directory) { nbCopyFile(c, res->file, nbCombine(c, directory, res->name)); }
/* The package and the packages its header includes (`#include "../std/..."`), recursively. */
static FcList* nbPackageClosure(FcCtx* c, const char* platform) {
    FcList* result = fcListNew(c);
    FcList* work = fcListNew(c);
    FcList* res = nbResources(c);
    int i;
    fcListAdd(c, work, (void*)platform);
    while (work->count > 0) {
        const char* package = (const char*)work->items[--work->count];
        const char* prefix = fcFmt(c, "platform/%s/", package);
        bool any = false;
        if (fcListHasStr(result, package)) continue;
        for (i = 0; i < res->count; i++) if (fcStartsWith(((NbResource*)res->items[i])->name, prefix)) { any = true; break; }
        if (!any) continue;   /* a package of your own, supplied by the target configuration */
        fcListAdd(c, result, (void*)package);
        for (i = 0; i < res->count; i++) {
            NbResource* x = (NbResource*)res->items[i];
            size_t n = 0;
            char* text;
            const char* p;
            if (!fcStartsWith(x->name, prefix) || !(text = (char*)fbReadFile(x->file, &n))) continue;
            text = (char*)realloc(text, n + 1);
            text[n] = 0;
            /* #include\s+"\.\./([A-Za-z0-9_\-]+)/ */
            for (p = text; (p = strstr(p, "#include")) != NULL;) {
                const char* q = p + 8;
                const char* s;
                p = q;
                if (!isspace((unsigned char)*q)) continue;
                while (isspace((unsigned char)*q)) q++;
                if (strncmp(q, "\"../", 4) != 0) continue;
                q += 4;
                s = q;
                while (isalnum((unsigned char)*q) || *q == '_' || *q == '-') q++;
                if (q > s && *q == '/') fcListAdd(c, work, (void*)fcStrN(c, s, (size_t)(q - s)));
            }
            free(text);
        }
    }
    return result;
}
/* PackageHeaders.WriteTo: the headers of the installed packages into pkg/<import>/ - an include of a header of a package it uses goes to ../<import>/, one of the
 * platform to ../../platform/. */
static void nbPackageHeadersWriteTo(FcCtx* c, FcStore* store, FcDict* instImports, const char* directory) {
    FcList* byImport = fcListNew(c);
    int i, k, m;
    if (!store->loaded) fcStoreLoad(c, store);
    for (i = 0; i < store->imports->count; i++) {
        FgInstImport* x = fgInstalledImport(c, instImports, (FcPkgImport*)store->imports->items[i]);
        bool seen = false;
        if (!x->native || x->native->headers->count == 0) continue;
        for (k = 0; k < byImport->count && !seen; k++) if (fcAsciiIEq(((FgInstImport*)byImport->items[k])->name, x->name)) seen = true;
        if (!seen) fcListAdd(c, byImport, x);
    }
    for (i = 0; i < byImport->count; i++) {
        FgInstImport* imp = (FgInstImport*)byImport->items[i];
        FcDict* own = fcDictNew(c, FC_KEY_STR);
        FcDict* others = fcDictNew(c, FC_KEY_STR);
        const char* dir = nbCombine(c, nbCombine(c, directory, "pkg"), imp->name);
        for (k = 0; k < imp->native->headers->count; k++) fcSetAdd(own, fgFileNameOf(c, (const char*)imp->native->headers->items[k]));
        for (k = 0; k < imp->native->uses->count; k++) {
            FgNativeUse* use = (FgNativeUse*)imp->native->uses->items[k];
            for (m = 0; m < byImport->count; m++) {
                FgInstImport* other = (FgInstImport*)byImport->items[m];
                int h;
                if (!fcAsciiIEq(other->name, use->import)) continue;
                for (h = 0; h < other->native->headers->count; h++) {
                    const char* name = fgFileNameOf(c, (const char*)other->native->headers->items[h]);
                    if (!fcDictHas(others, name)) fcDictPut(others, name, (void*)fcFmt(c, "../%s/%s", other->name, name));
                }
                break;
            }
        }
        nbMkdirs(c, dir);
        for (k = 0; k < imp->native->headers->count; k++) {
            const char* h = (const char*)imp->native->headers->items[k];
            const char* text = fgReadText(c, fcFmt(c, "%s/%s", imp->dir, h));
            FcBuf out = {0};
            const char* p;
            if (!text) continue;
            fcBufStr(&out, "");
            /* ^([ \t]*#[ \t]*include[ \t]+")([^"\r\n]+)(") per line */
            for (p = text; *p;) {
                const char* e = strchr(p, '\n');
                const char* end = e ? e : p + strlen(p);
                const char* q = p;
                const char* g1end;
                const char* g2;
                bool match = false;
                while (q < end && (*q == ' ' || *q == '\t')) q++;
                if (q < end && *q == '#') {
                    q++;
                    while (q < end && (*q == ' ' || *q == '\t')) q++;
                    if (end - q >= 7 && strncmp(q, "include", 7) == 0) {
                        const char* sp = q + 7;
                        q = sp;
                        while (q < end && (*q == ' ' || *q == '\t')) q++;
                        if (q > sp && q < end && *q == '"') {
                            g1end = q + 1;
                            g2 = g1end;
                            while (g2 < end && *g2 != '"' && *g2 != '\r') g2++;
                            if (g2 > g1end && g2 < end && *g2 == '"') {
                                const char* inc = fcStrN(c, g1end, (size_t)(g2 - g1end));
                                const char* name = fgFileNameOf(c, inc);
                                const char* path;
                                match = true;
                                if (fcDictHas(own, name)) fcBufPut(&out, p, (size_t)(end - p));
                                else if ((path = (const char*)fcDictGet(others, name)) != NULL) { fcBufPut(&out, p, (size_t)(g1end - p)); fcBufStr(&out, path); fcBufPut(&out, g2, (size_t)(end - g2)); }
                                else if (fcStartsWith(inc, "platform/")) { fcBufPut(&out, p, (size_t)(g1end - p)); fcBufStr(&out, "../../"); fcBufStr(&out, inc); fcBufPut(&out, g2, (size_t)(end - g2)); }
                                else fcBufPut(&out, p, (size_t)(end - p));
                            }
                        }
                    }
                }
                if (!match) fcBufPut(&out, p, (size_t)(end - p));
                if (!e) break;
                fcBufChar(&out, '\n');
                p = e + 1;
            }
            {
                const char* t = fcBufDone(c, &out);
                FcBuf u = {0};
                fcBufStr(&u, "");
                fcOut8(&u, t);   /* (File.WriteAllText: UTF-8 without a BOM) */
                nbWriteBytes(c, nbCombine(c, dir, fgFileNameOf(c, h)), u.p, u.len);
                fcBufFree(&u);
            }
        }
    }
}
/* NativeRuntimeFiles.WriteTo: fire_rt.h (with fire_rt_cpp.hpp, fire_json.h, fire_debug.h), cc/, abi/, the headers of the packages and the platform package (with
 * the packages it includes) into directory. */
static void nbRuntimeFilesWriteTo(FcCtx* c, FcStore* store, FcDict* instImports, const char* directory, const char* platform) {
    FcList* res = nbResources(c);
    FcList* wanted;
    int i, k;
    nbMkdirs(c, directory);
    for (i = 0; i < res->count; i++) {
        NbResource* x = (NbResource*)res->items[i];
        if (streq0(x->name, "fire_rt.h") || streq0(x->name, "fire_rt_cpp.hpp") || streq0(x->name, "fire_json.h") || streq0(x->name, "fire_debug.h")) nbWriteResource(c, x, directory);
    }
    for (i = 0; i < res->count; i++) if (fcStartsWith(((NbResource*)res->items[i])->name, "cc/")) nbWriteResource(c, (NbResource*)res->items[i], directory);
    for (i = 0; i < res->count; i++) if (fcStartsWith(((NbResource*)res->items[i])->name, "abi/")) nbWriteResource(c, (NbResource*)res->items[i], directory);
    nbPackageHeadersWriteTo(c, store, instImports, directory);
    wanted = nbPackageClosure(c, platform);
    for (k = 0; k < wanted->count; k++) {
        const char* prefix = fcFmt(c, "platform/%s/", (const char*)wanted->items[k]);
        for (i = 0; i < res->count; i++) if (fcStartsWith(((NbResource*)res->items[i])->name, prefix)) nbWriteResource(c, (NbResource*)res->items[i], directory);
    }
}
static void nbCopyDirectory(FcCtx* c, const char* source, const char* target) {
    FcList* files = nbSortedFiles(c, source, true);
    int i;
    nbMkdirs(c, target);
    for (i = 0; i < files->count; i++) nbCopyFile(c, nbCombine(c, source, (const char*)files->items[i]), nbCombine(c, target, (const char*)files->items[i]));
}
static bool nbWriteText(FcCtx* c, const char* path, const char* fc8) {
    FcBuf u = {0};
    bool ok;
    fcBufStr(&u, "");
    fcOut8(&u, fc8);
    ok = nbWriteBytes(c, path, u.p, u.len);
    fcBufFree(&u);
    return ok;
}
/* WriteFiles: cpp as fileName into directory with everything it includes (a package of your own is copied from the target's platformPath). */
static const char* nbWriteFiles(FcCtx* c, FcStore* store, FcDict* instImports, const char* directory, const char* cpp, const FgTarget* target, const char* fileName,
    const char* configDirectory, const char* natives, FcList* units) {
    const char* path = nbCombine(c, directory, fileName);
    const char* nativesPath = nbCombine(c, directory, "fire_natives.cpp");
    int i;
    nbMkdirs(c, directory);
    nbWriteText(c, path, cpp);
    /* (a split program: its header and its other units next to it - unit 0 is cpp) */
    if (units) for (i = 0; i < units->count; i++) { FgSplitUnit* u = (FgSplitUnit*)units->items[i]; if (u->kind != SUK_Runtime) nbWriteText(c, nbCombine(c, directory, u->name), u->text); }
    if (natives) nbWriteText(c, nativesPath, natives);
    else if (nbFileExists(nativesPath)) remove(nativesPath);   /* (of an earlier build into the same folder) */
    nbRuntimeFilesWriteTo(c, store, instImports, directory, target->platform);
    if (target->platformPath && *target->platformPath) {
        const char* custom = target->platformPath;
        const char* source = nbIsRooted(custom) || !configDirectory ? custom : nbCombine(c, configDirectory, custom);
        if (!nbDirExists(source)) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "native-config: the platform folder '%s' of target '%s' does not exist", source, target->name));
        nbCopyDirectory(c, source, nbCombine(c, nbCombine(c, directory, "platform"), target->platform));
    }
    return path;
}

/* ---- running a tool (Run) ---- */
/* The arguments of a command line as .NET splits them on POSIX (ProcessStartInfo.Arguments: quotes and backslashes as on Windows). */
static char** nbSplitArguments(const char* s, int* count) {
    size_t n = strlen(s);
    char** argv = (char**)calloc(n / 2 + 3, sizeof(char*));
    char* cur = (char*)malloc(n + 1);
    int argc = 0;
    size_t i = 0, len;
    if (!argv || !cur) fbOutOfMemory();
    while (i < n) {
        bool quoted = false;
        while (i < n && (s[i] == ' ' || s[i] == '\t')) i++;
        if (i >= n) break;
        len = 0;
        while (i < n) {
            char ch = s[i];
            if (ch == '\\') {
                size_t b = 0, k;
                while (i < n && s[i] == '\\') { b++; i++; }
                if (i < n && s[i] == '"') {
                    for (k = 0; k < b / 2; k++) cur[len++] = '\\';
                    if (b % 2 == 1) { cur[len++] = '"'; i++; }
                } else for (k = 0; k < b; k++) cur[len++] = '\\';
                continue;
            }
            if (ch == '"') {
                if (quoted && i + 1 < n && s[i + 1] == '"') { cur[len++] = '"'; i += 2; continue; }
                quoted = !quoted; i++; continue;
            }
            if ((ch == ' ' || ch == '\t') && !quoted) break;
            cur[len++] = ch;
            i++;
        }
        cur[len] = 0;
        argv[argc] = (char*)malloc(len + 1);
        if (!argv[argc]) fbOutOfMemory();
        memcpy(argv[argc++], cur, len + 1);
    }
    free(cur);
    *count = argc;
    return argv;
}
extern char** environ;
typedef struct NbRunResult { bool ok; char* text; size_t len; } NbRunResult;   /* text: malloc'd, stdout then stderr */
/* the program as Process.Start finds it: a path as it is, a name on the PATH of this process */
static char* nbResolveExe(const char* exe) {
    const char* path;
    const char* p;
    if (strchr(exe, '/')) return strdup(exe);
    path = getenv("PATH");
    for (p = path ? path : ""; p && *p;) {
        const char* e = strchr(p, ':');
        size_t n = e ? (size_t)(e - p) : strlen(p);
        if (n > 0) {
            char* cand = (char*)malloc(n + strlen(exe) + 2);
            if (!cand) fbOutOfMemory();
            memcpy(cand, p, n);
            cand[n] = '/';
            strcpy(cand + n + 1, exe);
            if (access(cand, X_OK) == 0 && nbFileExists(cand)) return cand;
            free(cand);
        }
        p = e ? e + 1 : NULL;
    }
    return NULL;
}
/* Runs a tool of the build in workDir: its output and errors (in that order), and whether it ended with 0. A rooted program puts its folder first in the PATH
 * (a portable toolchain finds its other programs there); Open Watcom gets WATCOM, INCLUDE, EDPATH. Thread-safe: no allocation in the context. */
static NbRunResult nbRun(const char* exe, const char* arguments, const char* workDir) {
    NbRunResult r = { false, NULL, 0 };
    int argc = 0, i, outPipe[2], errPipe[2], status = 0;
    char** args = nbSplitArguments(arguments, &argc);
    char** argv = (char**)calloc((size_t)argc + 2, sizeof(char*));
    char* resolved = nbResolveExe(exe);
    char** envp;
    int envCount = 0, extra = 0;
    char* pathEntry = NULL;
    char* watcomEntries[4] = { NULL, NULL, NULL, NULL };
    pid_t pid;
    FcBuf out = {0}, err = {0};
    if (!argv) fbOutOfMemory();
    argv[0] = (char*)exe;
    for (i = 0; i < argc; i++) argv[i + 1] = args[i];
    while (environ[envCount]) envCount++;
    envp = (char**)calloc((size_t)envCount + 8, sizeof(char*));
    if (!envp) fbOutOfMemory();
    /* a portable toolchain (w64devkit) finds its other programs through the PATH: its folder comes first */
    if (exe[0] == '/' && strrchr(exe, '/') != exe) {
        const char* old = getenv("PATH");
        size_t dl = (size_t)(strrchr(exe, '/') - exe);
        pathEntry = (char*)malloc(dl + strlen(old ? old : "") + 8);
        if (!pathEntry) fbOutOfMemory();
        sprintf(pathEntry, "PATH=%.*s:%s", (int)dl, exe, old ? old : "");
    }
    /* (Open Watcom: WATCOM, INCLUDE, EDPATH - WatcomToolchain.PrepareEnvironment) */
    if (exe[0] == '/') {
        char bin[4096], root[4096], probe[4200];
        const char* slash = strrchr(exe, '/');
        snprintf(bin, sizeof bin, "%.*s", (int)(slash - exe), exe);
        slash = strrchr(bin, '/');
        if (slash && slash != bin) {
            snprintf(root, sizeof root, "%.*s", (int)(slash - bin), bin);
            snprintf(probe, sizeof probe, "%s/h/stdio.h", root);
            if (nbFileExists(probe)) {
                char* p0 = (char*)malloc(8300);
                char* p1 = (char*)malloc(8300);
                char* p2 = (char*)malloc(8300);
                if (!p0 || !p1 || !p2) fbOutOfMemory();
                snprintf(p0, 8300, "WATCOM=%s", root);
                snprintf(p1, 8300, "INCLUDE=%s/h:%s/h/win", root, root);
                snprintf(p2, 8300, "EDPATH=%s/eddat", root);
                watcomEntries[0] = p0; watcomEntries[1] = p1; watcomEntries[2] = p2;
                snprintf(probe, sizeof probe, "%s/binw", root);
                if (nbDirExists(probe)) {
                    const char* path = pathEntry ? pathEntry + 5 : getenv("PATH");
                    char* p3 = (char*)malloc(strlen(path ? path : "") + 9000);
                    if (!p3) fbOutOfMemory();
                    if (!path || !strstr(path, probe)) { sprintf(p3, "PATH=%s:%s:%s", bin, probe, path ? path : ""); free(pathEntry); pathEntry = p3; }
                    else free(p3);
                }
            }
        }
    }
    for (i = 0; i < envCount; i++) {
        const char* e = environ[i];
        if (pathEntry && strncmp(e, "PATH=", 5) == 0) continue;
        if (watcomEntries[0] && (strncmp(e, "WATCOM=", 7) == 0 || strncmp(e, "INCLUDE=", 8) == 0 || strncmp(e, "EDPATH=", 7) == 0)) continue;
        envp[extra++] = (char*)e;
    }
    if (pathEntry) envp[extra++] = pathEntry;
    for (i = 0; i < 3; i++) if (watcomEntries[i]) envp[extra++] = watcomEntries[i];
    envp[extra] = NULL;
    fcBufStr(&out, "");
    fcBufStr(&err, "");
    if (!resolved || pipe(outPipe) != 0 || pipe(errPipe) != 0) {
        fcBufFmt(&out, "Could not run '%s': No such file or directory\n", exe);
        goto done;
    }
    pid = fork();
    if (pid == 0) {
        dup2(outPipe[1], 1); dup2(errPipe[1], 2);
        close(outPipe[0]); close(outPipe[1]); close(errPipe[0]); close(errPipe[1]);
        if (workDir && chdir(workDir) != 0) _exit(127);
        execve(resolved, argv, envp);
        _exit(127);
    }
    close(outPipe[1]); close(errPipe[1]);
    if (pid < 0) { close(outPipe[0]); close(errPipe[0]); fcBufFmt(&out, "Could not run '%s': fork failed\n", exe); goto done; }
    {
        struct pollfd fds[2];
        int open = 2;
        char buf[8192];
        fds[0].fd = outPipe[0]; fds[0].events = POLLIN;
        fds[1].fd = errPipe[0]; fds[1].events = POLLIN;
        while (open > 0) {
            int k;
            if (poll(fds, 2, -1) < 0) { if (errno == EINTR) continue; break; }
            for (k = 0; k < 2; k++) {
                ssize_t got;
                if (fds[k].fd < 0 || !(fds[k].revents & (POLLIN | POLLHUP | POLLERR))) continue;
                got = read(fds[k].fd, buf, sizeof buf);
                if (got > 0) fcBufPut(k == 0 ? &out : &err, buf, (size_t)got);
                else { close(fds[k].fd); fds[k].fd = -1; open--; }
            }
        }
        while (waitpid(pid, &status, 0) < 0 && errno == EINTR) { }
        r.ok = WIFEXITED(status) && WEXITSTATUS(status) == 0;
    }
done:
    fcBufPut(&out, err.p, err.len);
    r.text = out.p;
    r.len = out.len;
    fcBufFree(&err);
    for (i = 0; i < argc; i++) free(args[i]);
    free(args);
    free(argv);
    free(envp);
    free(resolved);
    free(pathEntry);
    for (i = 0; i < 3; i++) free(watcomEntries[i]);
    return r;
}
static void nbAppendRun(FcBuf* log, NbRunResult* r) { if (r->text) fcBufPut(log, r->text, r->len); free(r->text); r->text = NULL; }
static NbRunResult nbRunCommand(FcCtx* c, const char* command, const char* workDir) {
    const char* space = strchr(command, ' ');
    return nbRun(space ? fcStrN(c, command, (size_t)(space - command)) : command, space ? space + 1 : "", workDir);
}

/* ---- the command lines (CompilerCommand, BuildCommands, SplitCommands, ArchiveCommand, ToolchainStamp) ---- */
/* The lines of a file (File.ReadLines) that start with prefix, the rest of each */
static FcList* nbLinesWith(FcCtx* c, const char* path, const char* prefix, int maxLines) {
    FcList* l = fcListNew(c);
    size_t n = 0;
    uint8_t* data = fbReadFile(path, &n);
    size_t i = 0, pl = strlen(prefix);
    int lines = 0;
    if (!data) return l;
    while (i < n && (maxLines < 0 || lines < maxLines)) {
        size_t e = i;
        while (e < n && data[e] != '\n') e++;
        if (e - i >= pl && memcmp(data + i, prefix, pl) == 0) {
            size_t end = e;
            if (end > i && data[end - 1] == '\r') end--;
            fcListAdd(c, l, (void*)fcStrN(c, (const char*)data + i + pl, end - i - pl));
        }
        lines++;
        i = e + 1;
    }
    free(data);
    return l;
}
/* the compiler command line for the generated file (CompilerCommand). language: "c++" or "c". */
static NbCommand* nbCompilerCommand(FcCtx* c, const NbToolchain* t, const FgTarget* target, const char* cppFile, const char* includeDir, const char* exe, bool sharedLibrary,
    FcList* extraIncludeDirs, bool gui, const char* language, bool compileOnly, FcList* extraInputs, const char* programFile, const char* natives, bool sections) {
    const char* kind = nbKind(t);
    bool isC = streq0(language, "c");
    const char* std = isC ? (t->cstd ? t->cstd : "c11") : (t->std ? t->std : "c++17");
    FcList* libs = fcListNew(c);
    const char* linkSource = programFile ? programFile : cppFile;
    bool debugBuild;
    FcList* includes = fcListNew(c);
    FcBuf args = {0};
    const char* exeName;
    int i;
    if (!extraInputs) extraInputs = fcListNew(c);
    if (t->libs) for (i = 0; i < t->libs->count; i++) fcListAdd(c, libs, t->libs->items[i]);
    for (i = 0; i < target->linkLibCount; i++) fcListAdd(c, libs, (void*)target->linkLibs[i]);
    /* the generated program asks for libraries it needs (`// fire-link: dl`, those of the packages): -ldl, dl.lib with msvc */
    if (!compileOnly && nbFileExists(linkSource)) {
        FcList* wanted = nbLinesWith(c, linkSource, "// fire-link: ", -1);
        for (i = 0; i < wanted->count; i++) {
            const char* lib = fgTrim(c, (const char*)wanted->items[i]);
            const char* flag = fcStartsWith(lib, "framework:") ? fcFmt(c, "-framework %s", lib + 10) : streq0(kind, "msvc") ? fcFmt(c, "%s.lib", lib) : fcFmt(c, "-l%s", lib);
            if (!fcListHasStr(libs, flag)) fcListAdd(c, libs, (void*)flag);
        }
    }
    /* a debug build (`#define FIRE_DEBUG 1` at the top of the program): no optimization, debug information, not stripped */
    debugBuild = (!sharedLibrary || streq0(nbFileName(linkSource), "fire_plugin.c")) && nbFileExists(linkSource) && nbLinesWith(c, linkSource, "#define FIRE_DEBUG 1", 60)->count > 0;
    if (t->includeDirs) for (i = 0; i < t->includeDirs->count; i++) fcListAdd(c, includes, t->includeDirs->items[i]);
    if (extraIncludeDirs) for (i = 0; i < extraIncludeDirs->count; i++) fcListAdd(c, includes, extraIncludeDirs->items[i]);
    /* a toolchain with a driver of its own (Open Watcom) builds its command line itself */
    if (nbIsWatcom(kind)) return nbWatcomCommand(c, t, target, cppFile, includeDir, exe, includes, libs, compileOnly, extraInputs, debugBuild);
    if (streq0(kind, "custom")) {
        FcList* ca = fcListNew(c);
        const char* command;
        const char* space;
        if (!t->command) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "native-config: a custom toolchain needs a command");
        if (t->args) for (i = 0; i < t->args->count; i++) fcListAdd(c, ca, t->args->items[i]);
        for (i = 0; i < target->compileArgCount; i++) fcListAdd(c, ca, (void*)target->compileArgs[i]);
        command = fgReplace(c, t->command, "{cpp}", fcFmt(c, "\"%s\"", cppFile));
        command = fgReplace(c, command, "{c}", fcFmt(c, "\"%s\"", cppFile));
        command = fgReplace(c, command, "{natives}", natives ? fcFmt(c, "\"%s\"", natives) : "");
        command = fgReplace(c, command, "{dir}", fcFmt(c, "\"%s\"", includeDir));
        command = fgReplace(c, command, "{out}", fcFmt(c, "\"%s\"", exe));
        command = fgReplace(c, command, "{args}", fgJoin(c, ca, " "));
        command = fgReplace(c, command, "{libs}", fgJoin(c, libs, " "));
        space = strchr(command, ' ');
        return space ? nbCommand(c, fcStrN(c, command, (size_t)(space - command)), space + 1) : nbCommand(c, command, "");
    }
    fcBufStr(&args, "");
    if (streq0(kind, "msvc")) {
        bool smallMsvc = nbIsSizeProfile(t->optimization);
        bool windowsGui;
        const char* found;
        fcBufFmt(&args, "%s %s ", isC ? fcFmt(c, "/std:%s /TC", std) : fcFmt(c, "/std:%s", std), debugBuild ? "/Od /Zi" : smallMsvc ? "/O1 /Gy /Gw" : t->optimization ? t->optimization : "/O2");
        if (sharedLibrary) fcBufStr(&args, "/LD ");
        if (compileOnly) fcBufStr(&args, "/c ");
        if (t->args) for (i = 0; i < t->args->count; i++) fcBufFmt(&args, "%s ", (const char*)t->args->items[i]);
        for (i = 0; i < target->compileArgCount; i++) fcBufFmt(&args, "%s ", target->compileArgs[i]);
        fcBufFmt(&args, "\"%s\" ", cppFile);
        for (i = 0; i < extraInputs->count; i++) fcBufFmt(&args, "\"%s\" ", (const char*)extraInputs->items[i]);
        fcBufFmt(&args, "/I\"%s\" ", includeDir);
        for (i = 0; i < includes->count; i++) fcBufFmt(&args, "/I\"%s\" ", (const char*)includes->items[i]);
        if (compileOnly) {
            fcBufFmt(&args, "/Fo:\"%s\"", exe);
            found = nbFind(c, t);
            return nbCommand(c, found ? found : nbEffectiveCompiler(t), fcBufDone(c, &args));
        }
        fcBufFmt(&args, "/Fe:\"%s\" /Fo:\"%s\"", exe, nbCombine(c, includeDir, isC ? "fire_program.obj" : "fire_natives.obj"));
        for (i = 0; i < libs->count; i++) fcBufFmt(&args, " %s", (const char*)libs->items[i]);
        windowsGui = gui && !sharedLibrary && streq0(target->name, "windows");
        if (smallMsvc && !sharedLibrary) fcBufFmt(&args, " /link /OPT:REF /OPT:ICF%s", windowsGui ? " /SUBSYSTEM:WINDOWS /ENTRY:mainCRTStartup" : "");
        else if (windowsGui) fcBufStr(&args, " /link");
        if (windowsGui) fcBufStr(&args, " /SUBSYSTEM:WINDOWS /ENTRY:mainCRTStartup");   /* no console window; the program still starts at main */
    } else {
        bool tcc = streq0(kind, "tcc");
        bool small = nbIsSizeProfile(t->optimization) && !tcc && !debugBuild;   /* (TinyCC keeps every function anyway: no sections to drop) */
        bool gcLibraries = false;
        bool apple = false;
#ifdef __APPLE__
        apple = true;
#endif
        fcBufFmt(&args, "-std=%s %s ", std, debugBuild ? "-O0 -g" : small ? "-Os -ffunction-sections -fdata-sections" : t->optimization ? t->optimization : "-O2");
        /* a library of linkage "static": every function in its own section */
        if (sections && !small && !tcc && !debugBuild && !streq0(kind, "emscripten")) fcBufStr(&args, "-ffunction-sections -fdata-sections ");
        if (!tcc && !debugBuild) for (i = 0; i < extraInputs->count; i++) if (nbEndsWithI((const char*)extraInputs->items[i], ".a")) gcLibraries = true;
        if (compileOnly) fcBufStr(&args, "-c ");
        /* a library for the VM carries its C++ runtime */
        if (sharedLibrary) fcBufStr(&args, tcc ? "-shared " : apple ? "-dynamiclib -fPIC " : "-shared -fPIC ");
        if (t->args) for (i = 0; i < t->args->count; i++) fcBufFmt(&args, "%s ", (const char*)t->args->items[i]);
        for (i = 0; i < target->compileArgCount; i++) fcBufFmt(&args, "%s ", target->compileArgs[i]);
        if (gui && !sharedLibrary && streq0(target->name, "windows")) fcBufStr(&args, tcc ? "-Wl,-subsystem=windows " : "-mwindows ");
        fcBufFmt(&args, "\"%s\" ", cppFile);
        for (i = 0; i < extraInputs->count; i++) fcBufFmt(&args, "\"%s\" ", (const char*)extraInputs->items[i]);
        fcBufFmt(&args, "-I\"%s\" ", includeDir);
        for (i = 0; i < includes->count; i++) fcBufFmt(&args, "-I\"%s\" ", (const char*)includes->items[i]);
        fcBufFmt(&args, "-o \"%s\"", exe);
        if (compileOnly && streq0(kind, "emscripten")) for (i = 0; i < libs->count; i++) if (fcStartsWith((const char*)libs->items[i], "-s")) fcBufFmt(&args, " %s", (const char*)libs->items[i]);
        if (compileOnly) {
            const char* found = isC ? nbFindC(c, t) : nbFind(c, t);
            return nbCommand(c, found ? found : isC ? nbEffectiveCCompiler(c, t) : nbEffectiveCompiler(t), fcBufDone(c, &args));
        }
        /* the C library of the program: libm */
        if (isC && !sharedLibrary && !streq0(kind, "emscripten") && !fcListHasStr(libs, "-lm")) fcListAdd(c, libs, (void*)"-lm");
        for (i = 0; i < libs->count; i++) fcBufFmt(&args, " %s", (const char*)libs->items[i]);
        /* a program without symbols and debug information is about half the size; "size" lets the linker drop what nothing uses */
        if (!sharedLibrary && !streq0(kind, "emscripten")) {
            if (small || gcLibraries) fcBufStr(&args, apple ? " -Wl,-dead_strip" : " -Wl,--gc-sections");
            if (t->strip != 0 && !debugBuild) fcBufStr(&args, apple ? " -Wl,-x" : " -s");
        }
    }
    if (isC && !streq0(kind, "msvc")) { const char* f = nbFindC(c, t); exeName = f ? f : nbEffectiveCCompiler(c, t); }
    else { const char* f = nbFind(c, t); exeName = f ? f : nbEffectiveCompiler(t); }
    return nbCommand(c, exeName, fcBufDone(c, &args));
}
/* What the natives in C++ of a program come from (the packages and bridges named in the file), for a message. */
static const char* nbNativesInCpp(FcCtx* c, const char* nativesFile) {
    FcList* lines = nbLinesWith(c, nativesFile, "// ---- package ", -1);
    FcList* names = fcListNew(c);
    int i;
    for (i = 0; i < lines->count; i++) {
        const char* l = (const char*)lines->items[i];
        const char* sp = strchr(l, ' ');
        const char* name = sp ? fcStrN(c, l, (size_t)(sp - l)) : l;
        if (!fcListHasStr(names, name)) fcListAdd(c, names, (void*)name);
    }
    return names->count > 0 ? fgJoin(c, names, ", ") : "a bridge of the compiler";
}
/* The commands that build the program: the C file alone, or - with natives in C++ - the C file to an object file, then the C++ file compiled and linked with it. */
static FcList* nbBuildCommands(FcCtx* c, const NbToolchain* t, const FgTarget* target, const char* cFile, const char* nativesFile, const char* workDir, const char* exe, bool gui, FcList* libraries) {
    FcList* r;
    const char* obj;
    FcList* inputs;
    int i;
    if (!nativesFile || streq0(nbKind(t), "custom")) {
        r = nbIsWatcom(nbKind(t)) ? nbWatcomPreCommands(c, t, target, workDir) : fcListNew(c);   /* (Open Watcom: the startup code of a target without an operating system) */
        fcListAdd(c, r, nbCompilerCommand(c, t, target, cFile, workDir, exe, false, NULL, gui, "c", false, libraries, NULL, nativesFile, false));
        return r;
    }
    if (!nbCompilesCpp(t))
        fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "native-config: The program uses natives in C++ (%s): the toolchain '%s' compiles only C. Build it with gcc, clang or msvc (--toolchain).", nbNativesInCpp(c, nativesFile), nbKind(t)));
    obj = nbCombine(c, workDir, streq0(nbKind(t), "msvc") ? "fire_program.obj" : "fire_program.o");
    r = fcListNew(c);
    fcListAdd(c, r, nbCompilerCommand(c, t, target, cFile, workDir, obj, false, NULL, gui, "c", true, NULL, NULL, NULL, false));
    inputs = fcListNew(c);
    fcListAdd(c, inputs, (void*)obj);
    if (libraries) for (i = 0; i < libraries->count; i++) fcListAdd(c, inputs, libraries->items[i]);
    fcListAdd(c, r, nbCompilerCommand(c, t, target, nativesFile, workDir, exe, false, NULL, gui, "c++", false, inputs, cFile, NULL, false));
    return r;
}
/* The number of compilers a build runs at the same time: FIRE_BUILD_JOBS, else the cores of the machine. */
static int nbBuildJobs(void) {
    const char* v = getenv("FIRE_BUILD_JOBS");
    long n = v ? strtol(v, NULL, 10) : 0;
    long cores = sysconf(_SC_NPROCESSORS_ONLN);
    if (n > 0) return (int)n;
    return cores > 0 ? (int)cores : 1;
}
typedef struct NbObj { const char* name; const char* obj; } NbObj;
static int nbCmpObj(const void* a, const void* b) {
    const NbObj* x = *(const NbObj* const*)a;
    const NbObj* y = *(const NbObj* const*)b;
    const char* nx = streq0(x->name, "fire_program.c") ? "" : x->name;
    const char* ny = streq0(y->name, "fire_program.c") ? "" : y->name;
    return strcmp(nx, ny);
}
/* The commands of a split program: each unit to an object file (in the order of units), then the command that links them. */
static FcList* nbSplitCommands(FcCtx* c, const NbToolchain* t, const FgTarget* target, FcList* units, const char* headerName, const char* nativesFile, const char* workDir,
    const char* exe, bool gui, const NbToolchain* nativesToolchain) {
    const char* header = nbCombine(c, workDir, headerName);
    FcList* commands = fcListNew(c);
    FcList* pairs = fcListNew(c);
    FcList* ordered = fcListNew(c);
    FcList* rest;
    int i;
    for (i = 0; i < units->count; i++) {
        FgSplitUnit* u = (FgSplitUnit*)units->items[i];
        NbObj* o = (NbObj*)fcAlloc(c, sizeof *o);
        const NbToolchain* ut = u->kind == SUK_Natives && nativesToolchain ? nativesToolchain : t;
        o->name = u->name;
        o->obj = nbCombine(c, workDir, nbChangeExtension(c, u->name, ".o"));
        fcListAdd(c, pairs, o);
        /* (a quick build: the natives of the packages with the toolchain's optimisation, the program without) */
        fcListAdd(c, commands, nbCompilerCommand(c, ut, target, nbCombine(c, workDir, u->name), workDir, o->obj, false, NULL, gui, "c", true, NULL, header, NULL, false));
    }
    /* (the objects in the order of the files, not of the compiles: the same program for the same units) */
    if (pairs->count > 1) qsort(pairs->items, (size_t)pairs->count, sizeof(void*), nbCmpObj);   /* (OrderBy is stable; the names differ) */
    for (i = 0; i < pairs->count; i++) fcListAdd(c, ordered, (void*)((NbObj*)pairs->items[i])->obj);
    if (nativesFile) fcListAdd(c, commands, nbCompilerCommand(c, nativesToolchain ? nativesToolchain : t, target, nativesFile, workDir, exe, false, NULL, gui, "c++", false, ordered, header, NULL, false));
    else {
        rest = fcListNew(c);
        for (i = 1; i < ordered->count; i++) fcListAdd(c, rest, ordered->items[i]);
        fcListAdd(c, commands, nbCompilerCommand(c, t, target, (const char*)ordered->items[0], workDir, exe, false, NULL, gui, "c", false, rest, header, NULL, false));
    }
    return commands;
}
/* DateTime.Ticks of the last write of a file (100 ns since 0001-01-01, UTC). (FileInfo of a symbolic link - /usr/bin/gcc -> /etc/alternatives/gcc - has the size and time
 * of the link itself, lstat: the stamps below take them so) */
static long long nbTicks(const struct stat* st) {
#if defined(__APPLE__)
    long long ns = (long long)st->st_mtimespec.tv_nsec;
#else
    long long ns = (long long)st->st_mtim.tv_nsec;
#endif
    return ((long long)st->st_mtime + 62135596800LL) * 10000000LL + ns / 100;
}
/* What identifies the compiler of a toolchain for the caches: its kind, the program (path, size, time), the C standard, optimization and arguments. */
static const char* nbToolchainStamp(FcCtx* c, const NbToolchain* t) {
    const char* found = nbFindC(c, t);
    const char* compiler = found ? found : nbEffectiveCCompiler(c, t);
    const char* file = "";
    struct stat st;
    if (nbFileExists(compiler) && lstat(compiler, &st) == 0) file = fcFmt(c, "%lld@%lld", (long long)st.st_size, nbTicks(&st));
    return fcFmt(c, "sections|%s|%s|%s|%s|%s|%s|%s", nbKind(t), compiler, file, t->cstd ? t->cstd : "", t->optimization ? t->optimization : "",
        t->args ? fgJoin(c, t->args, " ") : "", t->includeDirs ? fgJoin(c, t->includeDirs, " ") : "");
}
/* The command that puts an object file into a static library: `ar rcs` next to the compiler, `tcc -ar`, `lib` of msvc. */
static NbCommand* nbArchiveCommand(FcCtx* c, const NbToolchain* t, const char* library, const char* obj) {
    const char* found = nbFindC(c, t);
    const char* compiler = found ? found : nbEffectiveCCompiler(c, t);
    const char* dir;
    const char* name;
    const char* stem;
    const char* exe;
    FcList* candidates = fcListNew(c);
    const char* args = fcFmt(c, "rcs \"%s\" \"%s\"", library, obj);
    int i;
    if (streq0(nbKind(t), "tcc")) return nbCommand(c, compiler, fcFmt(c, "-ar rcs \"%s\" \"%s\"", library, obj));
    if (streq0(nbKind(t), "msvc")) return nbCommand(c, nbCombine(c, nbDirName(c, compiler), "lib.exe"), fcFmt(c, "/nologo /OUT:\"%s\" \"%s\"", library, obj));
    dir = nbDirName(c, compiler);
    name = nbFileName(compiler);
    exe = nbEndsWithI(name, ".exe") ? ".exe" : "";
    stem = nbChangeExtension(c, name, "");
    {
        /* ^(.*?)(gcc|cc|clang|emcc)(-[0-9.]+)?$ */
        size_t n = strlen(stem), vs = n;
        const char* tool = NULL;
        size_t prefixLen = 0;
        static const char* const tools[] = { "gcc", "cc", "clang", "emcc" };
        size_t start;
        while (vs > 0 && (isdigit((unsigned char)stem[vs - 1]) || stem[vs - 1] == '.')) vs--;
        if (vs > 0 && vs < n && stem[vs - 1] == '-') vs--; else vs = n;
        /* the lazy prefix: the first position from which a tool and the version end the name */
        for (start = 0; start <= n && !tool; start++) {
            size_t ends[2] = { n, vs };
            int e, k;
            for (e = 0; e < 2 && !tool; e++)
                for (k = 0; k < 4; k++) {
                    size_t tl = strlen(tools[k]);
                    if (start + tl == ends[e] && strncmp(stem + start, tools[k], tl) == 0) { tool = tools[k]; prefixLen = start; vs = ends[e]; break; }
                }
        }
        if (tool) {
            const char* prefix = fcStrN(c, stem, prefixLen);
            const char* version = fcStr(c, stem + vs);
            if (streq0(tool, "emcc")) fcListAdd(c, candidates, (void*)"emar");
            if (streq0(tool, "clang")) fcListAdd(c, candidates, (void*)fcFmt(c, "%sllvm-ar%s", prefix, version));
            fcListAdd(c, candidates, (void*)fcFmt(c, "%sar", prefix));
            fcListAdd(c, candidates, (void*)fcFmt(c, "%sgcc-ar", prefix));
        }
    }
    fcListAdd(c, candidates, (void*)"ar");
    for (i = 0; i < candidates->count; i++) {
        const char* cand = (const char*)candidates->items[i];
        const char* beside = *dir ? nbCombine(c, dir, fcFmt(c, "%s%s", cand, exe)) : NULL;
        const char* onPath;
        if (beside && nbFileExists(beside)) return nbCommand(c, beside, args);
        if ((onPath = nbFindProgram(c, cand)) != NULL) return nbCommand(c, onPath, args);
    }
    return nbCommand(c, "ar", args);
}

/* ---- the C++ fallback of a C-only compiler ---- */
static NbToolchain* nbCppFallback(FcCtx* c, const NbToolchain* t) {
    NbMachine m;
    const char* k = nbKind(t);
    if (nbCompilesCpp(t) || !t->machineChoice || streq0(k, "files") || streq0(k, "custom")) return NULL;
    m = nbMachineLoad(c, NULL);
    return nbMachineCppFallback(c, &m);
}
/* The line of the build log about it: "TinyCC compiles only C and the program has natives in C++ (gui): built with gcc instead." */
static const char* nbCppFallbackNote(FcCtx* c, const NbToolchain* from, const NbToolchain* to, const char* natives) {
    FcList* names = fcListNew(c);
    FcList* lines = fgSplit(c, natives, '\n');
    int i;
    for (i = 0; i < lines->count; i++) {
        const char* l = (const char*)lines->items[i];
        const char* rest;
        const char* sp;
        const char* name;
        if (!fcStartsWith(l, "// ---- package ")) continue;
        rest = l + 16;
        sp = strchr(rest, ' ');
        name = fgTrim(c, sp ? fcStrN(c, rest, (size_t)(sp - rest)) : rest);
        if (*name && !fcListHasStr(names, name)) fcListAdd(c, names, (void*)name);
    }
    return fcFmt(c, "%s compiles only C and the program has natives in C++ (%s): built with %s instead.", streq0(nbKind(from), "tcc") ? "TinyCC" : nbKind(from),
        names->count > 0 ? fgJoin(c, names, ", ") : "a bridge of the compiler", nbKind(to));
}

/* ---- ProgramCache (NativeBuilder.ProgramCache.cs) ---- */
#define NB_KEPT_PROGRAMS 8
static const char* nbNativeCacheRoot(FcCtx* c) {
    const char* env = getenv("FIRE_NATIVE_CACHE");
    const char* data = getenv("XDG_DATA_HOME");
    const char* home = getenv("HOME");
    if (env && *env) return env;
#ifdef __APPLE__
    return fcFmt(c, "%s/Library/Application Support/fire/cache/native", home ? home : "");
#else
    /* (Environment.SpecialFolder.LocalApplicationData: $XDG_DATA_HOME, else ~/.local/share) */
    if (data && data[0] == '/') return fcFmt(c, "%s/fire/cache/native", data);
    return fcFmt(c, "%s/.local/share/fire/cache/native", home ? home : "");
#endif
}
static void nbHashText(FcSha256* h, const char* text, size_t n) {
    uint8_t len[4];
    len[0] = (uint8_t)n; len[1] = (uint8_t)(n >> 8); len[2] = (uint8_t)(n >> 16); len[3] = (uint8_t)(n >> 24);
    fcShaUpdate(h, len, 4);
    fcShaUpdate(h, (const uint8_t*)text, n);
}
static void nbHashStr(FcCtx* c, FcSha256* h, const char* fc8) {
    (void)c;
    FcBuf u = {0};
    fcBufStr(&u, "");
    fcOut8(&u, fc8);
    nbHashText(h, u.p, u.len);
    fcBufFree(&u);
}
/* A compiler as the key sees it: its path, size and time. */
static const char* nbProgramStamp(FcCtx* c, const char* executable) {
    const char* found = nbFindProgram(c, executable);
    const char* path = found ? found : executable;
    struct stat st;
    if (nbFileExists(path) && lstat(path, &st) == 0) return fcFmt(c, "%s|%lld|%lld", path, (long long)st.st_size, nbTicks(&st));
    return path;
}
static bool nbIsDebugProgram(const uint8_t* data, size_t n) {
    size_t m = n < 8192 ? n : 8192;
    size_t i;
    const char* d = "#define FIRE_DEBUG 1";
    size_t dl = strlen(d);
    if (m >= dl && memcmp(data, d, dl) == 0) return true;
    for (i = 0; i + dl + 1 <= m; i++) if (data[i] == '\n' && memcmp(data + i + 1, d, dl) == 0) return true;
    return false;
}
/* The key of the build (NULL: it is not cached). quick: the level of a quick build, -1: a normal one. */
static const char* nbProgramCacheKey(FcCtx* c, const NbToolchain* t, const FgTarget* target, const char* workDir, const char* exe, FcList* commands, bool stats, int units, int quick) {
    const char* env = getenv("FIRE_PROGRAM_CACHE");
    const char* k = nbKind(t);
    FcSha256 h;
    const char* root;
    FcList* files;
    bool debug = false;
    int i;
    if ((env && streq0(env, "0")) || stats || !(streq0(k, "gcc") || streq0(k, "clang") || streq0(k, "tcc"))) return NULL;
    fcShaInit(&h);
    root = fcFullPath(c, ".", workDir);
    nbHashStr(c, &h, "fire program cache 1");
    nbHashStr(c, &h, target->name);
    nbHashStr(c, &h, nbToolchainStamp(c, t));
    if (units > 0) nbHashStr(c, &h, fcFmt(c, "split %d", units));
    if (quick >= 0) nbHashStr(c, &h, fcFmt(c, "quick %d", quick));
    for (i = 0; i < commands->count; i++) {
        NbCommand* cmd = (NbCommand*)commands->items[i];
        nbHashStr(c, &h, nbProgramStamp(c, cmd->exe));
        nbHashStr(c, &h, fgReplace(c, fgReplace(c, cmd->args, exe, "<program>"), root, "<build>"));
    }
    files = nbSortedFiles(c, root, true);
    for (i = 0; i < files->count; i++) {
        const char* name = (const char*)files->items[i];
        size_t n = 0;
        uint8_t* data = fbReadFile(nbCombine(c, root, name), &n);
        uint8_t len[8];
        int b;
        if (!data) { if (n == 0 && nbFileExists(nbCombine(c, root, name))) data = (uint8_t*)calloc(1, 1); else return NULL; }
        nbHashStr(c, &h, name);
        for (b = 0; b < 8; b++) len[b] = (uint8_t)((uint64_t)n >> (8 * b));
        fcShaUpdate(&h, len, 8);
        fcShaUpdate(&h, data, n);
        if ((streq0(name, "fire_program.c") || streq0(name, "fire_program.h")) && nbIsDebugProgram(data, n)) debug = true;
        free(data);
    }
    /* (a debug build: its debug information names the files where they were compiled) */
    if (debug) nbHashStr(c, &h, root);
    return fcShaHex(c, &h);
}
static const char* nbProgramCacheFolder(FcCtx* c, const FgTarget* target) { return nbCombine(c, nbCombine(c, nbNativeCacheRoot(c), target->name), "programs"); }
static char nbHexDigit(int v) { return "0123456789abcdef"[v & 15]; }
static const char* nbGuid(FcCtx* c) {
    char b[33];
    int i;
    FILE* f = fopen("/dev/urandom", "rb");
    unsigned char r[16];
    if (!f || fread(r, 1, 16, f) != 16) { for (i = 0; i < 16; i++) r[i] = (unsigned char)(rand() ^ (int)getpid()); }
    if (f) fclose(f);
    for (i = 0; i < 16; i++) { b[i * 2] = nbHexDigit(r[i] >> 4); b[i * 2 + 1] = nbHexDigit(r[i]); }
    b[32] = 0;
    return fcStr(c, b);
}
/* The program of this key, copied to exe; false when the cache does not have it. */
static bool nbProgramCacheTryRestore(FcCtx* c, const FgTarget* target, const char* key, const char* exe) {
    const char* dir = nbCombine(c, nbProgramCacheFolder(c, target), key);
    const char* cached = nbCombine(c, dir, "program");
    const char* temp;
    struct stat st;
    if (!nbFileExists(cached)) return false;
    temp = fcFmt(c, "%s.%s.tmp", exe, nbGuid(c));
    if (!nbCopyFile(c, cached, temp)) return false;
    if (rename(temp, exe) != 0) { remove(temp); return false; }
    if (stat(cached, &st) == 0) chmod(exe, st.st_mode & 07777);
    utimes(dir, NULL);   /* (the newest stay) */
    return true;
}
typedef struct NbAged { const char* path; long long t; } NbAged;
static int nbCmpAgedDesc(const void* a, const void* b) {
    const NbAged* x = *(const NbAged* const*)a;
    const NbAged* y = *(const NbAged* const*)b;
    return x->t > y->t ? -1 : x->t < y->t;
}
static void nbDeleteTree(FcCtx* c, const char* path) {
    DIR* d = opendir(path);
    struct dirent* e;
    if (d) {
        while ((e = readdir(d)) != NULL) {
            const char* full;
            if (streq0(e->d_name, ".") || streq0(e->d_name, "..")) continue;
            full = nbCombine(c, path, e->d_name);
            if (nbDirExists(full)) nbDeleteTree(c, full); else remove(full);
        }
        closedir(d);
    }
    rmdir(path);
}
/* Puts the program that was just built into the cache, and leaves only the newest programs there. */
static void nbProgramCacheStore(FcCtx* c, const FgTarget* target, const char* key, const char* exe) {
    const char* folder = nbProgramCacheFolder(c, target);
    const char* dir = nbCombine(c, folder, key);
    const char* temp = nbCombine(c, dir, fcFmt(c, "%s.tmp", nbGuid(c)));
    struct stat st;
    FcList* aged = fcListNew(c);
    DIR* d;
    struct dirent* e;
    int i;
    nbMkdirs(c, dir);
    if (!nbCopyFile(c, exe, temp)) return;
    if (stat(exe, &st) == 0) chmod(temp, st.st_mode & 07777);
    if (rename(temp, nbCombine(c, dir, "program")) != 0) { remove(temp); return; }
    if ((d = opendir(folder)) == NULL) return;
    while ((e = readdir(d)) != NULL) {
        const char* full;
        if (streq0(e->d_name, ".") || streq0(e->d_name, "..")) continue;
        full = nbCombine(c, folder, e->d_name);
        if (nbDirExists(full) && stat(full, &st) == 0) { NbAged* a = (NbAged*)fcAlloc(c, sizeof *a); a->path = full; a->t = nbTicks(&st); fcListAdd(c, aged, a); }
    }
    closedir(d);
    if (aged->count > 1) qsort(aged->items, (size_t)aged->count, sizeof(void*), nbCmpAgedDesc);
    for (i = NB_KEPT_PROGRAMS; i < aged->count; i++) nbDeleteTree(c, ((NbAged*)aged->items[i])->path);
}

/* ---- CompileUnits: the units of a split program side by side ---- */
typedef struct NbUnitJobs {
    NbCommand** commands;
    int count;
    const char* workDir;
    NbRunResult* results;
    int next;
    pthread_mutex_t lock;
} NbUnitJobs;
static void* nbUnitWorker(void* arg) {
    NbUnitJobs* j = (NbUnitJobs*)arg;
    for (;;) {
        int i;
        pthread_mutex_lock(&j->lock);
        i = j->next++;
        pthread_mutex_unlock(&j->lock);
        if (i >= j->count) return NULL;
        j->results[i] = nbRun(j->commands[i]->exe, j->commands[i]->args, j->workDir);
    }
}
typedef struct NbByName { const char* name; int index; } NbByName;
static int nbCmpByName(const void* a, const void* b) {
    const NbByName* x = *(const NbByName* const*)a;
    const NbByName* y = *(const NbByName* const*)b;
    int r = strcmp(x->name, y->name);
    return r ? r : x->index - y->index;
}
/* Compiles the units of a split program side by side (BuildJobs at a time, the costly ones first); the log gets each command and what it said in the order of the
 * units' names. False: a unit did not compile. */
static bool nbCompileUnits(FcCtx* c, FcList* commands, FcList* units, const char* workDir, FcBuf* log) {
    NbUnitJobs j;
    int jobs = nbBuildJobs(), i, threads;
    pthread_t* ts;
    FcList* byName = fcListNew(c);
    bool ok = true;
    memset(&j, 0, sizeof j);
    j.commands = (NbCommand**)commands->items;
    j.count = commands->count;
    j.workDir = workDir;
    j.results = (NbRunResult*)calloc((size_t)commands->count + 1, sizeof(NbRunResult));
    pthread_mutex_init(&j.lock, NULL);
    threads = jobs < commands->count ? jobs : commands->count;
    ts = (pthread_t*)calloc((size_t)threads + 1, sizeof(pthread_t));
    if (!j.results || !ts) fbOutOfMemory();
    for (i = 0; i < threads; i++) pthread_create(&ts[i], NULL, nbUnitWorker, &j);
    for (i = 0; i < threads; i++) pthread_join(ts[i], NULL);
    pthread_mutex_destroy(&j.lock);
    for (i = 0; i < commands->count; i++) { NbByName* b = (NbByName*)fcAlloc(c, sizeof *b); b->name = ((FgSplitUnit*)units->items[i])->name; b->index = i; fcListAdd(c, byName, b); }
    if (byName->count > 1) qsort(byName->items, (size_t)byName->count, sizeof(void*), nbCmpByName);
    for (i = 0; i < byName->count; i++) {
        int k = ((NbByName*)byName->items[i])->index;
        fcBufFmt(log, "%s %s\n", j.commands[k]->exe, j.commands[k]->args);
        nbAppendRun(log, &j.results[k]);
    }
    for (i = 0; i < commands->count; i++) if (!j.results[i].ok) ok = false;
    free(j.results);
    free(ts);
    return ok;
}

/* ---- Build (linkage "source") ---- */
/* (build/provider.c, build/peres.c, build/bootimage.c) */
static NbToolchain* nbToolchainRequire(FcCtx* c, NbToolchain* wanted, const char* reason);
static void nbApplyPeMetadata(FcCtx* c, const char* exe, const FcAsmInfo* info, const FgTarget* target, FcBuf* log);
static const char* nbAfterLink(FcCtx* c, const NbToolchain* toolchain, const FgTarget* target, const char* exe);
typedef struct NbResult { bool ok; const char* output; const char* log; const char* error; } NbResult;   /* error: the diagnostic of a failed build (NULL: none) */
typedef struct NbBuild {
    const char* path;           /* the source */
    const char* prelude;
    const char* storeRoot;
    bool strict;
    int mode;                   /* -1: the source decides */
    int floatWidth, intWidth;   /* 0: none */
    bool keepSources;
    /* a dry run (`forge-c buildplan`): the files are written and the commands planned, nothing runs; plan gets what FrontendDump.DumpBuildPlan prints */
    bool dryRun;
    FcBuf* plan;
} NbBuild;
static int nbUnitOrderCmp(const void* a, const void* b);
static bool nbUnitOrderNatives;
static const char* nbTempPath(FcCtx* c) {
    const char* t = getenv("TMPDIR");
    if (t && *t) return t[strlen(t) - 1] == '/' ? t : fcFmt(c, "%s/", t);
    return "/tmp/";
}
static NbResult nbBuild(FcCtx* c, const NbBuild* b, NbConfig* config, const FgTarget* target, NbToolchain* toolchain, const char* output);
/* The plan of a dry run: the commands with the folder of the build and the program in neutral form, the key, the files of the folder with their hashes. */
static void nbWritePlan(FcCtx* c, FcBuf* plan, const NbToolchain* t, const char* log, FcList* commands, const char* key, const char* workDir, const char* exe) {
    FcList* files = nbSortedFiles(c, workDir, true);
    int i;
    fcBufFmt(plan, "toolchain %s\n", nbKind(t));
    if (*log) { fcBufStr(plan, "log\n"); fcOut8(plan, fgReplace(c, log, workDir, "<build>")); }
    for (i = 0; i < commands->count; i++) {
        NbCommand* k = (NbCommand*)commands->items[i];
        fcBufFmt(plan, "command %s %s\n", k->exe, fgReplace(c, fgReplace(c, k->args, exe, "<program>"), workDir, "<build>"));
    }
    fcBufFmt(plan, "key %s\n", key ? key : "none");
    for (i = 0; i < files->count; i++) {
        size_t n = 0;
        uint8_t* data = fbReadFile(nbCombine(c, workDir, (const char*)files->items[i]), &n);
        FcSha256 h;
        fcShaInit(&h);
        if (data) fcShaUpdate(&h, data, n);
        free(data);
        fcBufFmt(plan, "file %s %s\n", (const char*)files->items[i], fcShaHex(c, &h));
    }
}
static int nbUnitOrderCmp(const void* a, const void* b) {
    const FgSplitUnit* x = *(const FgSplitUnit* const*)a;
    const FgSplitUnit* y = *(const FgSplitUnit* const*)b;
    bool nx = nbUnitOrderNatives && x->kind == SUK_Natives, ny = nbUnitOrderNatives && y->kind == SUK_Natives;
    if (nx != ny) return nx ? -1 : 1;
    if (x->cost != y->cost) return x->cost > y->cost ? -1 : 1;
    return 0;
}
/* a stable sort (OrderByDescending ... ThenByDescending): insertion */
static void nbStableSort(FcList* l, int (*cmp)(const void*, const void*)) {
    int i, k;
    for (i = 1; i < l->count; i++) {
        void* x = l->items[i];
        for (k = i; k > 0 && cmp(&l->items[k - 1], &x) > 0; k--) l->items[k] = l->items[k - 1];
        l->items[k] = x;
    }
}
/* NativeBuilder.Build for linkage "source": translate and build. output is the program, or the folder for a toolchain that only writes files. */
static NbResult nbBuild(FcCtx* c, const NbBuild* b, NbConfig* config, const FgTarget* target, NbToolchain* toolchain, const char* output) {
    NbResult r = { false, output, "", NULL };
    FgOptions o;
    FcLinked* linked;
    FcNatives* registry;
    FgSources program;
    FcStore store;
    FcDict* instImports = fcDictNew(c, FC_KEY_PTR);
    FcBuf log = {0};
    const char* configDirectory = config->path ? fcDirectoryName(c, config->path) : NULL;
    const char* kind;
    NbToolchain* cppToolchain;
    toolchain = nbWithDirection(c, toolchain, nbOptimizeOf(config, target));
    kind = nbKind(toolchain);
    if (config->linkage && !fcAsciiIEq(config->linkage, "source"))
        fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, fcFmt(c, "unported: linkage \"%s\" (the library modules of the generator, Phase D 2)", config->linkage));
    if (config->stats) fcThrow(c, FC_ERR_INTERNAL, 0, 0, NULL, "unported: --stats");
    /* (a quick build, `--quick`: the units forced on where the toolchain splits; its optimisation level is set below, when the compiler is known) */
    memset(&o, 0, sizeof o);
    o.target = target;
    o.mode = b->mode;
    o.intWidth = b->intWidth;
    o.floatWidth = b->floatWidth;
    o.stripping = nbStrippingOf(config);
    o.optimize = nbOptimizeOf(config, target);
    o.globalJobs = config->globalJobs ? nbGlobalJobsParse(c, config->globalJobs) + 1 : 0;   /* (GlobalJobsDefault: when the program says nothing) */
    linked = fgLinkNativeKind(c, b->path, b->prelude, b->storeRoot, b->strict, &o, &registry, kind);
    memset(&store, 0, sizeof store);
    store.root = b->storeRoot;
    program = fgGenerateSources(c, linked, registry->names, target, o.stripping, nbQuickSplitOf(config, kind), kind, &store);
    /* natives in C++, and the machine's compiler is C only (TinyCC): the next compiler builds it, the log says so in one line */
    if (program.natives && (cppToolchain = nbCppFallback(c, toolchain)) != NULL) {
        NbResult inner = nbBuild(c, b, config, target, cppToolchain, output);
        inner.log = fcFmt(c, "%s\n%s", nbCppFallbackNote(c, toolchain, cppToolchain, program.natives), inner.log);
        return inner;
    }
    fcBufStr(&log, "");
    if (streq0(kind, "files")) {
        const char* directory = fcFullPath(c, ".", output);
        FcBuf more = {0};
        int i;
        nbWriteFiles(c, &store, instImports, directory, program.program, target, "fire_program.c", configDirectory, program.natives, program.units);
        /* (a split program: every unit is a source of the component) */
        fcBufStr(&more, "");
        if (program.units)
            for (i = 0; i < program.units->count; i++) {
                FgSplitUnit* u = (FgSplitUnit*)program.units->items[i];
                if (u->kind != SUK_Header && u->kind != SUK_Runtime) fcBufFmt(&more, " \"%s\"", u->name);
            }
        if (toolchain->layout && streq0(toolchain->layout, "idf-component"))
            nbWriteText(c, nbCombine(c, directory, "CMakeLists.txt"), fcFmt(c,
                "# Generated by fire.Native for target '%s': the program as an ESP-IDF component (call %s from the application).\n"
                "idf_component_register(SRCS \"fire_program.c\"%s%s\n                       INCLUDE_DIRS \".\")\n"
                "target_compile_options(${COMPONENT_LIB} PRIVATE $<$<COMPILE_LANGUAGE:C>:-std=gnu11> $<$<COMPILE_LANGUAGE:CXX>:-std=gnu++17>)\n",
                target->name, target->entryName, fcBufDone(c, &more), program.natives ? " \"fire_natives.cpp\"" : ""));
        else fcBufFree(&more);
        fcBufFmt(&log, "%s: fire_program.c%s, fire_rt.h and platform/%s/ for %s; the entry point is %s\n", directory, program.natives ? ", fire_natives.cpp" : "", target->platform, target->name, target->entryName);
        if (b->dryRun) {
            FcList* none = fcListNew(c);
            nbWritePlan(c, b->plan, toolchain, fcBufDone(c, &log), none, NULL, directory, "\x01");
            r.ok = true; r.output = directory; r.log = "";
            return r;
        }
        if (toolchain->buildCommand && *fgTrim(c, toolchain->buildCommand)) {
            NbRunResult run = nbRunCommand(c, toolchain->buildCommand, directory);
            r.ok = run.ok;
            nbAppendRun(&log, &run);
        } else r.ok = true;
        r.output = directory;
        r.log = fcBufDone(c, &log);
        return r;
    }
    {
        const char* workDir = b->keepSources ? fcFmt(c, "%s.src", fcFullPath(c, ".", output)) : fcFmt(c, "%sfire-native-%s", nbTempPath(c), nbGuid(c));
        const char* cppFile;
        const char* nativesFile;
        const char* exe = fcFullPath(c, ".", output);
        NbToolchain* nativesToolchain = NULL;
        NbToolchain* provided = NULL;
        bool gui = linked->asmInfo && linked->asmInfo->gui;   /* (program.Gui: #noconsole) */
        FcList* units = NULL;
        FcList* sourceCommands;
        int sideBySide = 0, i;
        const char* cacheKey;
        bool ok = true, compiled, built;
        cppFile = nbWriteFiles(c, &store, instImports, workDir, program.program, target, "fire_program.c", configDirectory, program.natives, program.units);
        nativesFile = program.natives ? nbCombine(c, workDir, "fire_natives.cpp") : NULL;
        if (!streq0(kind, "custom") && !nbFind(c, toolchain) && (b->dryRun || (provided = nbToolchainRequire(c, toolchain, "A native build translates the program to C++ and compiles it with a C++ toolchain.")) == NULL)) {
            /* nothing to compile with: ToolchainProvider offers one (the host asks the user: install, change, cancel - build/provider.c) */
            const char* notFound = fcFmt(c, "The compiler '%s' was not found. Install it, name another one in the toolchain of fire.native.json, or build with the toolchain 'files' and compile the sources yourself.", nbEffectiveCompiler(toolchain));
            if (b->dryRun) fcBufFmt(b->plan, "toolchain %s\nnot found %s\n", kind, nbEffectiveCompiler(toolchain));
            if (!b->keepSources) nbDeleteTree(c, workDir);
            r.output = exe; r.log = notFound; r.error = notFound;
            return r;
        }
        if (provided) { toolchain = provided; kind = nbKind(toolchain); }
        /* (a quick build: the C of the program with the level of `--quick`; the natives of the packages in units of their own keep the toolchain's optimisation) */
        if (config->quick >= 0) {
            nativesToolchain = toolchain;
            toolchain = nbQuickApply(c, toolchain, config->quick);
            fcBufFmt(&log, "%s\n", nbQuickLogLine(c, config->quick, nbKind(toolchain)));
        }
        /* (a split program: every unit compiled on its own, side by side - the costly ones first -, then one command links) */
        if (program.units) {
            units = fcListNew(c);
            for (i = 0; i < program.units->count; i++) if (((FgSplitUnit*)program.units->items[i])->kind != SUK_Header) fcListAdd(c, units, program.units->items[i]);
            nbUnitOrderNatives = nativesToolchain != NULL;
            nbStableSort(units, nbUnitOrderCmp);
            sourceCommands = nbSplitCommands(c, toolchain, target, units, ((FgSplitUnit*)program.units->items[0])->name, nativesFile, workDir, exe, gui, nativesToolchain);
            sideBySide = units->count;
        } else sourceCommands = nbBuildCommands(c, toolchain, target, cppFile, nativesFile, workDir, exe, gui, NULL);
        /* (the same files and commands as a program built before: that program - ProgramCache) */
        cacheKey = nbProgramCacheKey(c, toolchain, target, workDir, exe, sourceCommands, config->stats, sideBySide, config->quick);
        if (b->dryRun) {
            nbWritePlan(c, b->plan, toolchain, fcBufDone(c, &log), sourceCommands, cacheKey, workDir, exe);
            if (!b->keepSources) nbDeleteTree(c, workDir);
            r.ok = true; r.output = exe; r.log = "";
            return r;
        }
        if (cacheKey && nbProgramCacheTryRestore(c, target, cacheKey, exe)) {
            fcBufFmt(&log, "The C of the program and the commands are those of a program built before: taken from the cache (%.16s).\n", cacheKey);
            sourceCommands = fcListNew(c);
        }
        compiled = sourceCommands->count > 0;
        if (sideBySide > 0 && sourceCommands->count > 0) {
            FcList* first = fcListNew(c);
            FcList* rest = fcListNew(c);
            for (i = 0; i < sourceCommands->count; i++) fcListAdd(c, i < sideBySide ? first : rest, sourceCommands->items[i]);
            ok = nbCompileUnits(c, first, units, workDir, &log);
            sourceCommands = ok ? rest : fcListNew(c);
        }
        for (i = 0; i < sourceCommands->count; i++) {
            NbCommand* k = (NbCommand*)sourceCommands->items[i];
            NbRunResult run;
            fcBufFmt(&log, "%s %s\n", k->exe, k->args);
            run = nbRun(k->exe, k->args, workDir);
            nbAppendRun(&log, &run);
            if (!(ok = run.ok)) break;
        }
        built = ok && nbFileExists(exe);
        if (built && compiled && cacheKey) nbProgramCacheStore(c, target, cacheKey, exe);
        if (built) nbApplyPeMetadata(c, exe, linked->asmInfo, target, &log);
        if (built) { const char* after = nbAfterLink(c, toolchain, target, exe); if (after) fcBufFmt(&log, "%s\n", after); }   /* (a target without an operating system: its disk image) */
        if (!b->keepSources) nbDeleteTree(c, workDir);
        r.ok = built;
        r.output = exe;
        r.log = fcBufDone(c, &log);
        if (!built) r.error = "The C++ compiler of the native build reported errors (see the build log).";
        return r;
    }
}
/* DefaultOutput: out, out.exe on Windows (the target's extension) */
static const char* nbDefaultOutput(FcCtx* c, const FgTarget* target) { return fcFmt(c, "out%s", target->programExtension); }
