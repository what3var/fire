/* fire scheduler (fire_sched.h): the switch of the context in the browser - the fibers of Emscripten (emscripten/fiber.h, on ASYNCIFY, which the target builds
 * with anyway). A fiber has two stacks: the C stack of its locals and the stack ASYNCIFY keeps the frames of the WebAssembly in while it is not running. The
 * scheduler allocates one block per task (FIRE_SCHED_STACK_ALLOC): the C stack, then the ASYNCIFY stack. The program's own task becomes a fiber at its first
 * switch (emscripten_fiber_init_from_current_context).
 * No timer: a safepoint asks the clock of the browser (every 256th time) whether a slice of 10 ms is over (FIRE_SCHED_PREEMPTED). */
#ifndef FIRE_CTX_EMSCRIPTEN_H
#define FIRE_CTX_EMSCRIPTEN_H
#include <emscripten.h>
#include <emscripten/fiber.h>

#ifndef FIRE_SCHED_ASYNCIFY_BYTES
#define FIRE_SCHED_ASYNCIFY_BYTES 32768
#endif

typedef struct { emscripten_fiber_t f; unsigned char ready; } fire_ctx;

#define FIRE_SCHED_STACK_ALLOC(size) malloc((size) + FIRE_SCHED_ASYNCIFY_BYTES)
#define FIRE_SCHED_STACK_FREE(p) free(p)

static void fire_ctx_trampoline(void* entry) { ((void (*)(void))entry)(); }

/* A context that starts `entry()` on the stack [stack, stack + size); the ASYNCIFY stack lies behind it (FIRE_SCHED_STACK_ALLOC). */
static inline void fire_ctx_make(fire_ctx* c, void* stack, size_t size, void (*entry)(void)) {
    emscripten_fiber_init(&c->f, fire_ctx_trampoline, (void*)entry, stack, size, (char*)stack + size, FIRE_SCHED_ASYNCIFY_BYTES);
    c->ready = 1;
}

/* Saves the running context in `from` and continues `to` (the program's own task becomes a fiber the first time it switches). */
static inline void fire_ctx_swap(fire_ctx* from, fire_ctx* to) {
    static char mainAsyncify[FIRE_SCHED_ASYNCIFY_BYTES];
    if (!from->ready) {
        emscripten_fiber_init_from_current_context(&from->f, mainAsyncify, sizeof mainAsyncify);
        from->ready = 1;
    }
    emscripten_fiber_swap(&from->f, &to->f);
}

/* ---- the slice: every 256th safepoint looks at the clock; after 10 ms the next safepoint gives the turn away ---- */
static unsigned char fire_em_preemptCount;
static double fire_em_sliceStart;
static int fire_em_preempted(void) {
    double now;
    if (++fire_em_preemptCount != 0) return 0;
    now = emscripten_get_now();
    if (now - fire_em_sliceStart < 10.0) return 0;
    fire_em_sliceStart = now;
    return 1;
}
#define FIRE_SCHED_PREEMPTED() fire_em_preempted()
#endif
