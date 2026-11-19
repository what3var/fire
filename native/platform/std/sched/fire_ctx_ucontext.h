/* fire scheduler (fire_sched.h): the switch of the context with ucontext (POSIX) - for trying the scheduler on a PC (FIRE_GREEN_THREADS); the old PCs have
 * their own switch in a few lines of assembler (fire_ctx_x86_16.h, ...). */
#ifndef FIRE_CTX_UCONTEXT_H
#define FIRE_CTX_UCONTEXT_H
#include <ucontext.h>

typedef struct { ucontext_t uc; } fire_ctx;

/* A context that starts `entry()` on the stack [stack, stack + size). */
static inline void fire_ctx_make(fire_ctx* c, void* stack, size_t size, void (*entry)(void)) {
    getcontext(&c->uc);
    c->uc.uc_stack.ss_sp = stack;
    c->uc.uc_stack.ss_size = size;
    c->uc.uc_link = NULL;
    makecontext(&c->uc, entry, 0);
}
/* Saves the running context in `from` and continues `to`. */
static inline void fire_ctx_swap(fire_ctx* from, fire_ctx* to) { swapcontext(&from->uc, &to->uc); }

/* ---- the slice (a PC has no timer interrupt for fire_sched_preempt here): every 16th safepoint looks at the clock; after 10 ms the next one gives the
 * turn away - a thread whose loop calls a native that takes ms (Compression, a long loop body) would otherwise hold the others, a sleeping main program
 * too, for 4096 safepoints (gilYield) ---- */
FIRE_ONCE unsigned char fire_uc_preemptCount;
FIRE_ONCE int64_t fire_uc_sliceStart;
static inline int fire_uc_preempted(void) {
    int64_t now;
    if (++fire_uc_preemptCount & 15u) return 0;
    now = fire_plat_nowMs();
    if (now - fire_uc_sliceStart < 10) return 0;
    fire_uc_sliceStart = now;
    return 1;
}
#define FIRE_SCHED_PREEMPTED() fire_uc_preempted()
#endif
