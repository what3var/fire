/* fire - the VM in C (docs/ROADMAP_C_PORT.md phase B; its structure: docs/spec/VM_C.md).
 *
 *   cc -O2 native/vm/fire.c -o fire -lm -lpthread   (gcc, clang; one file that includes everything - no build system)
 *   fire [--stats] a.fbc [b.fbc ...] [-- args]   runs the programs (written by `forge bytecode`) one after the other in this process
 *
 * Every program is loaded, its classes are registered with the runtime, it runs, and then everything it had is gone again - its objects, strings, scopes,
 * classes - before the next one starts; one that ends with an exception nothing catches (or with an error of the runtime) ends only itself (L6). --stats
 * prints after each one what is still alive in the runtime (handles of objects/arrays, the temporary pool, strings). Exit code: that of the last program
 * that failed (1 for an exception nothing caught, the code of `terminate(code)`), else 0.
 *
 * The widths of a program (`#intwidth 32|16`, `#floatwidth 32`, the word of the target) are a configuration of the runtime that is fixed when it is
 * compiled (FIRE_INT32, FIRE_INT16, FIRE_FLOAT32, FIRE_WORD_BITS), as in a native build. One `fire` is built per configuration from the same sources:
 *
 *   cc -O2 [-DFIRE_INT32 | -DFIRE_INT16] [-DFIRE_FLOAT32] [-DFIRE_WORD_BITS=32] native/vm/fire.c -o fire-i32-f32 ...
 *
 * named after the one without defines with `-i32`/`-i16`, `-f32`, `-w32` in that order (fire-i32, fire-f32, fire-i32-f32, fire-i16, ...). A program
 * whose widths are not those of the running `fire` is run by the variant next to it, as a child process with the same console and arguments (VM_C.md). */
#define FIRE_VM 1
#define FIRE_THREADS 1             /* fire threads, tasks, actors (phase B step 3): the thread model of the runtime with the GIL - one system thread per */
#define FIRE_GIL 1                 /* fire thread, the safe points of the interpreter are those of the generated code (vm_threads.c) */
#define FIRE_EXCEPTIONS 1
#define FIRE_REFLECTION 1         /* (the runtime's probes and the selector of a lambda: the reflection of the VM, vm_reflect.c) */
#define FIRE_PTRBOUNDS 1          /* (a pointer into an array or a buffer is checked when it is used, like a native build that is not #performance) */
#define FIRE_NDIMS 16              /* (a program's dimensions are a constant of the generated code; the VM takes room for 16) */
#ifdef FIRE_VM_STATS
#define FIRE_HEAP_STATS 1          /* (-DFIRE_VM_STATS: counts the strings that live - the check that a script leaves nothing behind) */
#endif
#define FIRE_RT_IMPLEMENTATION 1
/* (the platform layer of this machine, from the repository: a native build copies it next to the runtime) */
#if defined(_WIN32)
#define FIRE_PLATFORM_HEADER "../platform/windows/fire_platform.h"
#else
#define FIRE_PLATFORM_HEADER "../platform/posix/fire_platform.h"
#endif
#include "../runtime/fire_rt.h"
#include <setjmp.h>
#if defined(_WIN32)
#include <process.h>
#else
#include <unistd.h>
#include <sys/wait.h>
#endif
#define FIRE_BASE_IMPLEMENTATION 1
#include "../base/fire_base.h"

void fbOutOfMemory(void) { outOfMemory(); }

#include "vm.h"
#include "vm_load.c"
#include "vm_core.c"
#include "vm_pkg.c"
#include "vm_lib.c"
#include "vm_natives.c"
#include "vm_threads.c"
#include "vm_run.c"
#include "vm_reflect.c"
#include "vm_json.c"

void fire_vmExit(int code) {
    Vm* vm = g_vmCur;
    if (vm && vm->exitJump) {
        /* (the handlers go now, while they are still there: some live on the stack of the runtime's functions the jump leaves - a job's, a reply's) */
        Handler* h = g_handlers;
        while (h) { Handler* p = h->prev; if (h->fn == vmCatchFn) free(h); h = p; }
        g_handlers = NULL;
        vm->exitCode = code;
        longjmp(*vm->exitJump, 1);
    }
    fire_plat_exit(code);
}

static uint32_t vmLiveHandles(void) { return g_slots.count - g_slots.nfree; }

/* ---- the widths of this build and the variants for other widths ---- */
#ifdef FIRE_INT16
#define VM_INT_WIDTH 16
#elif defined(FIRE_INT32)
#define VM_INT_WIDTH 32
#else
#define VM_INT_WIDTH 64
#endif
#ifdef FIRE_FLOAT32
#define VM_FLOAT_WIDTH 32
#else
#define VM_FLOAT_WIDTH 64
#endif
static const char* g_vmSelf;          /* argv[0]: the variants are named after it */
static int g_vmArgc; static char** g_vmArgv;   /* what follows `--` (the program's arguments) */
static bool vmWidthsFit(const VmProgram* p) {
    return (p->intWidth == 0 || p->intWidth == VM_INT_WIDTH) && (p->floatWidth == 0 || p->floatWidth == VM_FLOAT_WIDTH)
        && (p->wordWidth == 0 || p->wordWidth == FIRE_WORD_BITS);
}
/* The program at `path` needs other widths: the variant of this `fire` for them runs it (a child process; its exit code is the program's). 2: there is
 * no such variant. */
static int vmRunVariant(const char* path, const VmProgram* p, bool stats) {
    char exe[4096], suffix[32] = "";
    const char* self = g_vmSelf ? g_vmSelf : "fire";
    size_t n = strlen(self);
    char** argv;
    int i, k = 0, code;
    if (p->intWidth && p->intWidth != 64) snprintf(suffix + strlen(suffix), sizeof suffix - strlen(suffix), "-i%u", (unsigned)p->intWidth);
    if (p->floatWidth && p->floatWidth != 64) snprintf(suffix + strlen(suffix), sizeof suffix - strlen(suffix), "-f%u", (unsigned)p->floatWidth);
    if (p->wordWidth && p->wordWidth != 64) snprintf(suffix + strlen(suffix), sizeof suffix - strlen(suffix), "-w%u", (unsigned)p->wordWidth);
    /* (the name of this build without its own suffix: fire-i32 runs a 64-bit program with `fire`) */
#if defined(_WIN32)
    if (n > 4 && _stricmp(self + n - 4, ".exe") == 0) n -= 4;
#endif
    {
        const char* own[] = { "-w32", "-f32", "-i32", "-i16" };
        int j;
        for (j = 0; j < 4; j++) { size_t m = strlen(own[j]); if (n > m && strncmp(self + n - m, own[j], m) == 0) n -= m; }
    }
    if (n + strlen(suffix) + 5 >= sizeof exe) n = sizeof exe - strlen(suffix) - 5;
    memcpy(exe, self, n);
    snprintf(exe + n, sizeof exe - n, "%s%s", suffix,
#if defined(_WIN32)
        ".exe"
#else
        ""
#endif
    );
    argv = (char**)calloc((size_t)g_vmArgc + 6, sizeof(char*));
    if (!argv) outOfMemory();
    argv[k++] = exe;
    if (stats) argv[k++] = (char*)"--stats";
    argv[k++] = (char*)path;
    if (g_vmArgc > 1) { argv[k++] = (char*)"--"; for (i = 1; i < g_vmArgc; i++) argv[k++] = g_vmArgv[i]; }
    argv[k] = NULL;
    fflush(stdout);
    fflush(stderr);
#if defined(_WIN32)
    {
        intptr_t r = _spawnv(_P_WAIT, exe, (const char* const*)argv);
        code = r < 0 ? -1 : (int)r;
    }
#else
    {
        pid_t pid = fork();
        int status = 0;
        if (pid == 0) { execv(exe, argv); _exit(127); }
        if (pid < 0 || waitpid(pid, &status, 0) < 0) code = -1;
        else if (WIFEXITED(status)) code = WEXITSTATUS(status) == 127 ? -1 : WEXITSTATUS(status);
        else code = 1;   /* (a signal ended it) */
    }
#endif
    free(argv);
    if (code < 0) {
        fprintf(stderr, "fire: %s: the program is made for int %u, float %u, word %u bits - this VM runs %d, %d, %d; its variant '%s' is not there (VM_C.md: one fire per width)\n",
            path, (unsigned)(p->intWidth ? p->intWidth : 64), (unsigned)(p->floatWidth ? p->floatWidth : 64), (unsigned)(p->wordWidth ? p->wordWidth : 64),
            VM_INT_WIDTH, VM_FLOAT_WIDTH, FIRE_WORD_BITS, exe);
        return 2;
    }
    return code;
}

/* Runs one program; 0 when it ended normally, 1 with an exception nothing caught or an error of the runtime (or the code of `terminate`), 2 when it could
 * not be loaded. */
static int vmRunFile(const char* path, bool stats) {
    char error[300];
    size_t len = 0;
    uint8_t* bytes = fbReadFile(path, &len);
    VmProgram* p;
    Vm* vm;
    jmp_buf jb;
    uint32_t i;
    int result;
    ResEntry* res = NULL;
    if (!bytes) { fprintf(stderr, "fire: cannot read '%s'\n", path); return 2; }
    p = vmLoad(bytes, len, error, sizeof error);
    if (!p) { free(bytes); fprintf(stderr, "fire: %s: %s\n", path, error); return 2; }
    if (!vmWidthsFit(p)) {   /* (other widths: the variant of this VM for them runs it) */
        int r = vmRunVariant(path, p, stats);
        vmUnload(p);
        free(bytes);
        return r;
    }

    vm = (Vm*)calloc(1, sizeof(Vm));
    vm->prog = p;
    vm->stack = (Value*)malloc(VM_STACK * sizeof(Value));
    vm->frames = (VmFrame*)calloc(VM_FRAMES, sizeof(VmFrame));
    if (!vm->stack || !vm->frames) outOfMemory();
    g_vmCur = vm;
    vmInstallUnits(vm);
    vm->savedEnums = g_enums; vm->savedEnumCount = g_enumCount;
    g_enums = p->enums; g_enumCount = p->nenums;
    if (p->nresources) {
        res = (ResEntry*)calloc(p->nresources, sizeof(ResEntry));
        for (i = 0; i < p->nresources; i++) { res[i].name = p->resources[i].name; res[i].data = p->resources[i].data; res[i].length = p->resources[i].len; }
        g_resources = res; g_resourceCount = (int)p->nresources;
    }
    vmRegisterClasses(vm);
    vmPkgBegin(path);
    vmNativesBind(vm);
    vm->global = scNew(vm, NULL);
    scReserve(vm->global, 256);
    g_globalOwn = &vm->global->own;
    vmThreadsBegin(vm);   /* (the GIL; the global scope is the shared domain of the threads) */
    /* the handler at the bottom: catches everything (type -1) */
    vm->bottomTypes[0] = -1;
    vm->bottomInfo.ncatch = 1; vm->bottomInfo.types = vm->bottomTypes; vm->bottomInfo.hasFinally = 0; vm->bottomInfo.typeMap = NULL;
    vm->bottom.fn = vmUnhandledFn; vm->bottom.ctx = vm; vm->bottom.info = &vm->bottomInfo;
    pushHandler(&vm->bottom);
    {
        VmFrame* f = &vm->frames[0];
        f->chunk = p->top; f->ip = p->top->code; f->scope = vm->global; f->base = vm->global; f->self = Undef();
        f->spBase = 0; f->kind = FR_TOP; f->catchEnded = 0; f->rec = NULL; f->retMode = RM_PUSH;
        vm->nframes = 1;
    }
    vm->exitJump = &jb;
    if (setjmp(jb) == 0) {
        vmRun(vm, 0);
        if (g_unwind.active && (g_unwind.target == &vm->bottom || g_unwind.kind == UW_LEAVE)) clearUnwind();   /* (leave, terminate: the end) */
        vmPkgHoldEnd();   /* (open windows of a library hold the program: it serves them until they close) */
    } else {
        /* the runtime wanted to end the process (fatal, terminate): only the script ends - what was half done in the runtime is given up */
        if (!vm->terminated) vm->failed = 2;
        g_jobDepth = 0; g_noSyncDepth = 0; g_secDepth = 0;   /* (the jump may have left a job, a section of the runtime) */
        if (!vm->quiet) { vm->quiet = 1; g_zombies.quiet++; }
        g_zombies.depth = 0;
    }
    /* the end of the main program waits for its threads (`catch terminate` runs there) */
    if (setjmp(jb) == 0) vmThreadsFinish(vm);
    else {
        if (!vm->terminated) vm->failed = 2;
        if (!vm->quiet) { vm->quiet = 1; g_zombies.quiet++; }
        g_zombies.depth = 0;
    }
    if (terminateRequested() && !vm->failed) { vm->terminated = 1; vm->exitCode = mainExitCode(); }

    /* ---- the end of the script: everything it had goes ---- */
    while (g_handlers) { Handler* h = g_handlers; popHandler(); if (h->fn == vmCatchFn) free(h); }
    g_pending = NULL;
    clearUnwind();
    vmFreeDead(vm);
    while (vm->nframes > 0) {
        VmFrame* f = &vm->frames[vm->nframes - 1];
        vmLeaveTo(vm, f, vmStopOf(vm, f));
        vm->nframes--;
    }
    vm->sp = 0;
    vm->exitJump = NULL;
    vmPkgEnd();                          /* (the libraries of packages forget the program, the lambdas they had are let go) */
    scLeave(vm, vm->global);             /* the globals and what the global scope owns (with their destructors - not after a failure) */
    vmFreePinned(vm);
    for (i = 0; i < p->nclasses; i++) {
        uint32_t k;
        for (k = 0; k < p->classes[i]->nstatic; k++) { release(p->classes[i]->svals[k]); p->classes[i]->svals[k] = Undef(); }
    }
    if (g_pool.top) poolRelease(0);
    vmThreadsEnd();
    if (vm->quiet) g_zombies.quiet--;
    g_globalOwn = NULL;
    vmUnregisterClasses(vm);
    vmNativesUnbind();
    vmJsonEnd();
    while (vm->freeScopes) { VmScope* s = vm->freeScopes; vm->freeScopes = s->nextFree; free(s->slots); free(s); }
    result = vm->terminated ? vm->exitCode : vm->failed ? 1 : 0;
    free(vm->stack);
    free(vm->frames);
    g_enums = vm->savedEnums; g_enumCount = vm->savedEnumCount;
    g_resources = NULL; g_resourceCount = 0;
    free(res);
    free(vm);
    g_vmCur = NULL;
    g_vmDemands = NULL;
    g_vmSpecs = NULL;
    g_vmArrCount = 0; g_arrClasses = NULL;
    g_textUnits = NULL; g_textUnitCount = 0;
    free(g_vmTextUnits); g_vmTextUnits = NULL;
    vmUnload(p);
    free(bytes);
    fflush(stdout);
    if (stats)
        fprintf(stderr, "[fire vm] %s: %s; alive afterwards: %u handles (objects, arrays), %d strings, %u in the temporary pool, %u classes\n", path,
            result == 0 ? "ended" : "failed", vmLiveHandles(),
#ifdef FIRE_HEAP_STATS
            (int)fire_atomic_load(&g_heapStrs),
#else
            -1,
#endif
            g_pool.top, g_classDescCount);
    return result;
}

int main(int argc, char** argv) {
    bool stats = false;
    int i, code = 0, files = 0, last = argc;
    for (i = 1; i < argc; i++) if (strcmp(argv[i], "--") == 0) { last = i; break; }
    fire_setArgs(argc - (last < argc ? last : argc - 1), argv + (last < argc ? last : argc - 1));   /* (Environment.Args: what follows `--`) */
    g_vmSelf = argv[0];
    g_vmArgc = argc - (last < argc ? last : argc - 1); g_vmArgv = argv + (last < argc ? last : argc - 1);
    for (i = 1; i < last; i++) {
        int r;
        if (strcmp(argv[i], "--stats") == 0) { stats = true; continue; }
        files++;
        if ((r = vmRunFile(argv[i], stats)) != 0) code = r;
    }
    if (!files) { fputs("usage: fire [--stats] <program.fbc>... [-- <arguments>]\n", stderr); return 2; }
    return code;
}
