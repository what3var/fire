/* native/forge/tools/dist.c - `forge-c package-dist` (docs/ROADMAP_C_PORT.md, Phase D step 6): the forge/VM part of a distribution of fire without .NET -
 * what the publish of fire.Compiler (forge.exe, FireVm.targets, StandardSources.targets) puts next to spark, made with a C compiler and forge-c only.
 *
 *   forge-c package-dist <folder> [--dry-run] [--name <program>] [--packages <folder>] [--no-vm] [--no-prebuilt]
 *
 * The layout (flat, as setup.iss installs the publish folder into {app}; spark starts forge from its own folder):
 *   <folder>/forge[.exe]          this forge-c (--name: another name; default forge, so that spark finds it)
 *   <folder>/fire[.exe], fire-i32, fire-i16, fire-f32, fire-i32-f32, fire-i16-f32 (+ .key)   the VM in C and its variants (`build-vm --all`)
 *   <folder>/native/              what forge-c reads at run time: tables/ (the prelude, the .def), runtime/, abi/, platform/, forge/ (the sources of libforge, M3) (the C runtime of native
 *                                 builds), vm/ and base/ (the sources of the VM, built on a machine without one next to forge)
 *   <folder>/Source/              the standard packages as projects (packages/ of the repository: StandardSources.EnsureInstalled installs them)
 *   <folder>/Prebuilt/<package>/<rid>/   the libraries of their natives for this machine (`prebuild-standard`)
 * Packages/ (the store) and Toolchain/ are made by forge at run time. The VM and Prebuilt/ are built by the forge of the folder itself (it finds its
 * tables in native/tables next to it - forge.c), which checks the layout on the way. They are built for this machine: a distribution for Windows is made on
 * Windows (forge-c.exe from MinGW/w64devkit or cl), one for Linux on Linux. */

static int pdCopyTree(FcCtx* c, const char* from, const char* to, const char* const* exts, bool recursive, bool dry, long long* bytes);
static bool pdSkipFolder(const char* name) {
    return streq0(name, "bin") || streq0(name, "obj") || streq0(name, ".vs") || streq0(name, ".git") || streq0(name, "node_modules");
}
static bool pdHasExt(const char* name, const char* const* exts) {
    size_t n = strlen(name);
    int i;
    if (!exts) return true;
    for (i = 0; exts[i]; i++) { size_t e = strlen(exts[i]); if (n > e && strcmp(name + n - e, exts[i]) == 0) return true; }
    return false;
}
/* the files below `from` (with these extensions; recursive: with the folders below it, but no bin/obj) copied to `to`; the count */
static int pdCopyTree(FcCtx* c, const char* from, const char* to, const char* const* exts, bool recursive, bool dry, long long* bytes) {
    DIR* d = opendir(from);
    struct dirent* e;
    int count = 0;
    FcList* names = fcListNew(c);
    int i;
    if (!d) return 0;
    while ((e = readdir(d)) != NULL) if (e->d_name[0] != '.' || (e->d_name[1] && e->d_name[1] != '.' && !streq0(e->d_name, ".git") && !streq0(e->d_name, ".vs"))) {
        if (streq0(e->d_name, ".") || streq0(e->d_name, "..")) continue;
        fcListAdd(c, names, (void*)fcStr(c, e->d_name));
    }
    closedir(d);
    if (names->count > 1) qsort(names->items, (size_t)names->count, sizeof(void*), fcCmpOrdinal);
    for (i = 0; i < names->count; i++) {
        const char* name = (const char*)names->items[i];
        const char* src = nbCombine(c, from, name);
        const char* dst = nbCombine(c, to, name);
        if (nbFileExists(src)) {
            struct stat st;
            if (!pdHasExt(name, exts)) continue;
            if (stat(src, &st) == 0) *bytes += (long long)st.st_size;
            if (!dry && !nbCopyFile(c, src, dst)) { fprintf(stderr, "package-dist: cannot write %s\n", dst); return -1; }
            count++;
        } else if (recursive && nbDirExists(src) && !pdSkipFolder(name)) {
            int n = pdCopyTree(c, src, dst, exts, true, dry, bytes);
            if (n < 0) return -1;
            count += n;
        }
    }
    return count;
}
static void pdMakeExecutable(const char* path) {
#ifndef _WIN32
    struct stat st;
    if (stat(path, &st) == 0) chmod(path, st.st_mode | S_IXUSR | S_IXGRP | S_IXOTH);
#else
    (void)path;
#endif
}
static void pdUnsetenv(const char* name) {
#ifdef _WIN32
    _putenv_s(name, "");
#else
    unsetenv(name);
#endif
}
/* runs a command of the folder's own forge, its output passed on; its exit code */
static int pdRunForge(const char* forge, char* const* argv) {
    CfRun r;
    cfSpawn(forge, argv, NULL, 7200, &r);
    fwrite(r.out.p, 1, r.out.len, stdout);
    fwrite(r.err.p, 1, r.err.len, stderr);
    fflush(stdout);
    cfRunFree(&r);
    return r.code;
}

static const char* const pdUsage =
    "usage: forge-c package-dist <folder> [--dry-run] [--name <program>] [--packages <folder>] [--no-vm] [--no-prebuilt]\n"
    "Puts the forge/VM part of fire into <folder> without .NET: this forge-c (as forge), the VM in C with its variants, native/ (tables, runtime, VM\n"
    "sources), Source/ (the standard packages) and Prebuilt/ (the libraries of their natives for this machine) - the files setup.iss installs next to\n"
    "spark. --dry-run lists the layout and builds nothing. --packages: the standard packages (default: packages/ of the repository of native/, else\n"
    "Source/ next to forge-c).\n";

static int pdPackageDistMain(int argc, char** argv) {
    FcCtx ctx;
    FcCtx* c = &ctx;
    const char* folder = NULL;
    const char* name = "forge";
    const char* packages = NULL;
    bool dry = false, vm = true, prebuilt = true;
    const char* self = NULL;
    char selfBuf[4096];
    const char* forge;
    int i, total = 0;
    long long bytes = 0;
    static const char* const headers[] = { ".h", ".hpp", ".asm", NULL };
    static const char* const vmSources[] = { ".c", ".h", NULL };
    static const char* const baseHeaders[] = { ".h", NULL };
    static const char* const forgeSources[] = { ".c", ".h", ".inc", NULL };
    /* native/: what forge-c reads from its native root (builder.c nbResources, cvm.c nvSources, forge.c fcFindPrelude) */
    static const struct { const char* sub; const char* const* exts; bool recursive; } parts[] = {
        { "tables", NULL, false }, { "runtime", NULL, true }, { "abi", NULL, false }, { "platform", headers, true }, { "vm", vmSources, false }, { "base", baseHeaders, false },
        /* (forge/: the sources of libforge - build-embed builds it for the packages forge and script, M3) */
        { "forge", forgeSources, true },
    };
    fcCtxInit(c);
    for (i = 2; i < argc; i++) {
        const char* a = argv[i];
        if (streq0(a, "--dry-run")) dry = true;
        else if (streq0(a, "--no-vm")) vm = false;
        else if (streq0(a, "--no-prebuilt")) prebuilt = false;
        else if (streq0(a, "--name") && i + 1 < argc) name = argv[++i];
        else if (streq0(a, "--packages") && i + 1 < argc) packages = argv[++i];
        else if (streq0(a, "--help") || streq0(a, "-h")) { fputs(pdUsage, stdout); fcCtxFree(c); return 0; }
        else if (a[0] == '-' || folder) { fprintf(stderr, "%s", pdUsage); fcCtxFree(c); return 2; }
        else folder = a;
    }
    if (!folder) { fprintf(stderr, "%s", pdUsage); fcCtxFree(c); return 2; }
#ifdef _WIN32
    self = fwSelfPath(selfBuf, sizeof selfBuf);
#else
    {
        ssize_t n = readlink("/proc/self/exe", selfBuf, sizeof selfBuf - 1);
        if (n > 0) { selfBuf[n] = 0; self = selfBuf; }
        else if (realpath(argv[0], selfBuf)) self = selfBuf;
    }
#endif
    if (!self || !nbFileExists(self)) { fprintf(stderr, "package-dist: cannot find its own program (%s).\n", argv[0]); fcCtxFree(c); return 2; }
    folder = fcFullPath(c, ".", folder);
    if (!packages) {
        const char* repo = nbCombine(c, fcDirectoryName(c, nbNativeRoot), "packages");
        packages = nbFileExists(nbCombine(c, repo, "Packages.firesln")) ? repo : nbCombine(c, nbBaseDirectory, "Source");
    } else packages = fcFullPath(c, ".", packages);
    if (!nbFileExists(nbCombine(c, packages, "Packages.firesln"))) { fprintf(stderr, "package-dist: %s holds no Packages.firesln (the standard packages: --packages).\n", packages); fcCtxFree(c); return 2; }
    if (!nbFileExists(nbCombine(c, nbCombine(c, nbNativeRoot, "tables"), "prelude.fire"))) { fprintf(stderr, "package-dist: %s is no folder native/ (FIRE_NATIVE_ROOT).\n", nbNativeRoot); fcCtxFree(c); return 2; }
    forge = nbCombine(c, folder, fcFmt(c, "%s" NV_EXTENSION, name));
    printf("%s%s (for %s)\n", dry ? "would make " : "", folder, nlRid());
    /* the program */
    {
        struct stat st;
        if (stat(self, &st) == 0) bytes += (long long)st.st_size;
        printf("  %-34s this forge-c (%s)\n", fcFmt(c, "%s" NV_EXTENSION, name), self);
        if (!dry) {
            nbMkdirs(c, folder);
            if (!nbCopyFile(c, self, forge)) { fprintf(stderr, "package-dist: cannot write %s\n", forge); fcCtxFree(c); return 1; }
            pdMakeExecutable(forge);
        }
        total++;
    }
    /* native/ */
    for (i = 0; i < (int)(sizeof parts / sizeof parts[0]); i++) {
        long long b = 0;
        int n = pdCopyTree(c, nbCombine(c, nbNativeRoot, parts[i].sub), nbCombine(c, nbCombine(c, folder, "native"), parts[i].sub), parts[i].exts, parts[i].recursive, dry, &b);
        if (n < 0) { fcCtxFree(c); return 1; }
        printf("  %-34s %4d files, %6lld KB (from %s/%s)\n", fcFmt(c, "native/%s/", parts[i].sub), n, (b + 1023) / 1024, nbNativeRoot, parts[i].sub);
        total += n; bytes += b;
    }
    /* packages/compression/native/: the deflater libforge includes from native/forge/project/fpk.c (`../../../packages/...`) - build-embed compiles libforge here */
    {
        long long b = 0;
        int n = pdCopyTree(c, nbCombine(c, nbCombine(c, packages, "compression"), "native"), nbCombine(c, nbCombine(c, nbCombine(c, folder, "packages"), "compression"), "native"), headers, false, dry, &b);
        if (n < 0) { fcCtxFree(c); return 1; }
        printf("  %-34s %4d files, %6lld KB (from %s/compression/native)\n", "packages/compression/native/", n, (b + 1023) / 1024, packages);
        total += n; bytes += b;
    }
    /* Source/ */
    {
        long long b = 0;
        int n = pdCopyTree(c, packages, nbCombine(c, folder, "Source"), NULL, true, dry, &b);
        if (n < 0) { fcCtxFree(c); return 1; }
        printf("  %-34s %4d files, %6lld KB (from %s)\n", "Source/", n, (b + 1023) / 1024, packages);
        total += n; bytes += b;
    }
    printf("  %d files, %lld KB copied%s\n", total, (bytes + 1023) / 1024, dry ? " (dry run: nothing written)" : "");
    /* the VM and Prebuilt/, built by the forge of the folder (its own tables, store and Source/ - nothing of the environment of this one) */
    pdUnsetenv("FIRE_TABLES");
    pdUnsetenv("FIRE_PACKAGES");
    pdUnsetenv("FIRE_NATIVE_ROOT");
    pdUnsetenv("FIRE_VM_EXE");
    if (vm) {
        char* args[5];
        args[0] = (char*)forge; args[1] = "build-vm"; args[2] = (char*)folder; args[3] = "--all"; args[4] = NULL;
        printf("  %-34s %s build-vm %s --all\n", "fire, fire-i32, ... (the VM)", forge, folder);
        fflush(stdout);
        if (!dry && pdRunForge(forge, args) != 0) { fprintf(stderr, "package-dist: the VM was not built.\n"); fcCtxFree(c); return 1; }
    }
    if (prebuilt) {
        char* args[5];
        const char* source = nbCombine(c, folder, "Source");
        const char* to = nbCombine(c, folder, "Prebuilt");
        args[0] = (char*)forge; args[1] = "prebuild-standard"; args[2] = (char*)source; args[3] = (char*)to; args[4] = NULL;
        printf("  %-34s %s prebuild-standard %s %s\n", fcFmt(c, "Prebuilt/<package>/%s/", nlRid()), forge, source, to);
        fflush(stdout);
        if (!dry && pdRunForge(forge, args) != 0) { fprintf(stderr, "package-dist: the libraries of the standard packages were not built.\n"); fcCtxFree(c); return 1; }
    }
    if (!dry) printf("done: %s\n", folder);
    fcCtxFree(c);
    return 0;
}
