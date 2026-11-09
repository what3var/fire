/* fire scheduler (fire_sched.h): the switch of the context on 32-bit x86 with flat memory (Open Watcom 386 with a DOS extender; dos32). Between running tasks
 * setjmp/longjmp (the jmp_buf keeps ESP and EBP); a new task starts with a few instructions that load ESP with the top of its stack and call the entry.
 * The timer: IRQ 0 (vector 08h) - the extender passes it to the protected-mode handler; INT 1Ch is only called by the BIOS in real mode. */
#ifndef FIRE_CTX_X86_32_H
#define FIRE_CTX_X86_32_H
#include <setjmp.h>
#include <dos.h>

typedef struct { jmp_buf jb; unsigned long sp; unsigned char fresh; } fire_ctx;

static void (*fire_ctx_entryFn)(void);
static unsigned long fire_ctx_newSP;

static inline void fire_ctx_make(fire_ctx* c, void* stack, size_t size, void (*entry)(void)) {
    c->sp = ((unsigned long)stack + size - 16u) & ~15ul;
    c->fresh = 1;
    fire_ctx_entryFn = entry;   /* (the scheduler has one entry for every task: fire_sched_entry) */
}

/* Loads ESP of the new task and calls the entry there - it never returns (fire_sched_entry ends in a switch). */
static void fire_ctx_start(void) {
    _asm {
        mov eax, fire_ctx_newSP
        mov esp, eax
        call dword ptr fire_ctx_entryFn
    }
}

static inline void fire_ctx_swap(fire_ctx* from, fire_ctx* to) {
    if (setjmp(from->jb) != 0) return;   /* (back here: this task runs again) */
    if (to->fresh) {
        to->fresh = 0;
        fire_ctx_newSP = to->sp;
        fire_ctx_start();
    }
    longjmp(to->jb, 1);
}

/* ---- the timer: IRQ 0 (18.2 times a second) asks the next safepoint to give the turn away; installed with the first thread, removed at the end ---- */
static void (__interrupt __far *fire_dos_oldTick)(void) = 0;
static volatile int fire_sched_wantSwitch;
static void __interrupt __far fire_dos_tick(void) {
    fire_sched_wantSwitch = 1;
    _chain_intr(fire_dos_oldTick);
}
static void fire_dos_untick(void) { if (fire_dos_oldTick) { _dos_setvect(0x08, fire_dos_oldTick); fire_dos_oldTick = 0; } }
static inline void fire_dos_tickOn(void) {
    if (fire_dos_oldTick) return;
    fire_dos_oldTick = _dos_getvect(0x08);
    _dos_setvect(0x08, fire_dos_tick);
    atexit(fire_dos_untick);
}
#define FIRE_SCHED_ON_FIRST_THREAD() fire_dos_tickOn()
#define FIRE_SCHED_WANT_DECLARED 1
#endif
