/* fire scheduler (fire_sched.h): the switch of the context on 16-bit x86 in real mode (Open Watcom, large model; dos16). Between running tasks setjmp/longjmp
 * (the jmp_buf of the large model keeps SS:SP and BP); a new task starts with a few instructions that load SS:SP with the top of its stack and call the entry.
 * Every stack but the program's lies outside DGROUP: the code is compiled with -zu (SS is not DS) and without stack checks (they compare with the program's stack). */
#ifndef FIRE_CTX_X86_16_H
#define FIRE_CTX_X86_16_H
#include <setjmp.h>
#include <dos.h>

typedef struct { jmp_buf jb; unsigned short ss, sp; unsigned char fresh; } fire_ctx;

static void (__far *fire_ctx_entryFn)(void);
static unsigned short fire_ctx_newSS, fire_ctx_newSP;

static inline void fire_ctx_make(fire_ctx* c, void* stack, size_t size, void (*entry)(void)) {
    /* the top of the stack as a normalized far address: SS the paragraph of the block, SP its offset + size (even, below 64 KB) */
    unsigned long linear = ((unsigned long)FP_SEG(stack) << 4) + FP_OFF(stack);
    c->ss = (unsigned short)(linear >> 4);
    c->sp = (unsigned short)(((unsigned)(linear & 15u) + size - 4u) & ~1u);
    c->fresh = 1;
    fire_ctx_entryFn = entry;   /* (the scheduler has one entry for every task: fire_sched_entry) */
}

/* Loads SS:SP of the new task and calls the entry there - it never returns (fire_sched_entry ends in a switch). */
static void fire_ctx_start(void) {
    _asm {
        mov ax, seg fire_ctx_newSS
        mov ds, ax
        mov ax, fire_ctx_newSS
        mov dx, fire_ctx_newSP
        cli
        mov ss, ax
        mov sp, dx
        sti
        call dword ptr fire_ctx_entryFn
    }
}

static inline void fire_ctx_swap(fire_ctx* from, fire_ctx* to) {
    if (setjmp(from->jb) != 0) return;   /* (back here: this task runs again) */
    if (to->fresh) {
        to->fresh = 0;
        fire_ctx_newSS = to->ss;
        fire_ctx_newSP = to->sp;
        fire_ctx_start();
    }
    longjmp(to->jb, 1);
}

/* ---- the timer: INT 1Ch (18.2 times a second) asks the next safepoint to give the turn away; installed with the first thread, removed at the end ---- */
static void (__interrupt __far *fire_dos_oldTick)(void) = 0;
static volatile int fire_sched_wantSwitch;
static void __interrupt __far fire_dos_tick(void) {
    fire_sched_wantSwitch = 1;
    _chain_intr(fire_dos_oldTick);
}
static void fire_dos_untick(void) { if (fire_dos_oldTick) { _dos_setvect(0x1C, fire_dos_oldTick); fire_dos_oldTick = 0; } }
static inline void fire_dos_tickOn(void) {
    if (fire_dos_oldTick) return;
    fire_dos_oldTick = _dos_getvect(0x1C);
    _dos_setvect(0x1C, fire_dos_tick);
    atexit(fire_dos_untick);
}
#define FIRE_SCHED_ON_FIRST_THREAD() fire_dos_tickOn()
#define FIRE_SCHED_WANT_DECLARED 1
#endif
