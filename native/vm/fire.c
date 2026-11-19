/* fire - the VM in C (docs/ROADMAP_C_PORT.md phase B; its structure: docs/spec/VM_C.md).
 *
 *   cc -O2 native/vm/fire.c -o fire -lm         (gcc, clang; one file that includes everything - no build system)
 *   fire [--stats] a.fbc [b.fbc ...] [-- args]   runs the programs (written by `forge bytecode`) one after the other in this process
 *
 * Every program is loaded, its classes are registered with the runtime, it runs, and then everything it had is gone again - its objects, strings, scopes,
 * classes - before the next one starts; one that ends with an exception nothing catches (or with an error of the runtime) ends only itself (L6). --stats
 * prints after each one what is still alive in the runtime (handles of objects/arrays, the temporary pool, strings). Exit code: that of the last program
 * that failed (1 for an exception nothing caught, the code of `terminate(code)`), else 0. */
#define FIRE_VM 1
#define FIRE_EXCEPTIONS 1
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
#define FIRE_BASE_IMPLEMENTATION 1
#include "../base/fire_base.h"

void fbOutOfMemory(void) { outOfMemory(); }

#include "vm.h"
#include "vm_load.c"
#include "vm_core.c"
#include "vm_natives.c"
#include "vm_run.c"

void fire_vmExit(int code) {
    Vm* vm = g_vmCur;
    if (vm && vm->exitJump) { vm->exitCode = code; longjmp(*vm->exitJump, 1); }
    fire_plat_exit(code);
}

static uint32_t vmLiveHandles(void) { return g_slots.count - g_slots.nfree; }

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
    vm->global = scNew(vm, NULL);
    scReserve(vm->global, 256);
    g_globalOwn = &vm->global->own;
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
        if (g_unwind.active && g_unwind.target == &vm->bottom) clearUnwind();
    } else {
        /* the runtime wanted to end the process (fatal, terminate): only the script ends - what was half done in the runtime is given up */
        if (!vm->terminated) vm->failed = 2;
        if (!vm->quiet) { vm->quiet = 1; g_zombies.quiet++; }
        g_zombies.depth = 0;
    }

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
    scLeave(vm, vm->global);             /* the globals and what the global scope owns (with their destructors - not after a failure) */
    for (i = 0; i < p->nclasses; i++) {
        uint32_t k;
        for (k = 0; k < p->classes[i]->nstatic; k++) { release(p->classes[i]->svals[k]); p->classes[i]->svals[k] = Undef(); }
    }
    if (g_pool.top) poolRelease(0);
    if (vm->quiet) g_zombies.quiet--;
    g_globalOwn = NULL;
    vmUnregisterClasses(vm);
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
    for (i = 1; i < last; i++) {
        int r;
        if (strcmp(argv[i], "--stats") == 0) { stats = true; continue; }
        files++;
        if ((r = vmRunFile(argv[i], stats)) != 0) code = r;
    }
    if (!files) { fputs("usage: fire [--stats] <program.fbc>... [-- <arguments>]\n", stderr); return 2; }
    return code;
}
