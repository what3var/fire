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
#endif
