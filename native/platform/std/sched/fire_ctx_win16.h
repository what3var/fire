/* fire scheduler (fire_sched.h): the switch of the context on Windows 3.x (16-bit protected mode; Open Watcom, huge model; win16). Between running tasks
 * setjmp/longjmp (the jmp_buf of the large model keeps SS:SP and BP); a new task starts with a few instructions that load SS:SP with the top of its stack and call
 * the entry - as in real mode (fire_ctx_x86_16.h), but a segment is a selector here: no arithmetic with paragraphs.
 *   - Every task gets a segment of its own for its stack (GlobalAlloc): offset 0 with SS, so SP is simply the size. Its first 16 bytes are what Windows keeps at
 *     the start of a stack segment (the "instance data"): KERNEL looks at pStackTop/pStackMin/pStackBot (0Ah/0Ch/0Eh) when it switches tasks and checks stacks.
 *   - The code is compiled with -zu (SS is not DS) and without stack checks (they compare with the program's own stack).
 *   - Windows (and Wine) call a window procedure with DS = SS: the procedures of fire load their DGROUP themselves (__loadds), so a message dispatched while a
 *     fire thread runs - its Sleep takes the messages too - finds its data.
 *   - No timer interrupt: a safepoint asks the clock (GetTickCount, every 256th time) whether a slice of 55 ms is over (FIRE_SCHED_PREEMPTED). */
#ifndef FIRE_CTX_WIN16_H
#define FIRE_CTX_WIN16_H
#include <setjmp.h>
#include <dos.h>

typedef struct { jmp_buf jb; unsigned short ss, sp; unsigned char fresh; } fire_ctx;

static void (__far *fire_ctx_entryFn)(void);
static unsigned short fire_ctx_newSS, fire_ctx_newSP;

/* A stack of its own: a fixed global segment, its first 16 bytes the header Windows expects (the stack may grow down to offset 10h). */
static void* fire_w16_stackAlloc(size_t size) {
    HGLOBAL h = GlobalAlloc(GMEM_FIXED | GMEM_ZEROINIT, (DWORD)size);
    unsigned short __far* p;
    if (h == 0) return NULL;
    p = (unsigned short __far*)GlobalLock(h);
    if (p == NULL) { GlobalFree(h); return NULL; }
    p[5] = 0x10;                                /* 0Ah pStackTop: the lowest offset the stack may use */
    p[6] = (unsigned short)(size - 2u);         /* 0Ch pStackMin: the lowest SP so far */
    p[7] = (unsigned short)(size - 2u);         /* 0Eh pStackBot: where the stack starts */
    return (void*)p;
}
static void fire_w16_stackFree(void* stack) {
    HGLOBAL h;
    if (stack == NULL) return;
    h = (HGLOBAL)GlobalHandle(FP_SEG(stack));
    if (h == 0) return;
    GlobalUnlock(h);
    GlobalFree(h);
}
#define FIRE_SCHED_STACK_ALLOC(size) fire_w16_stackAlloc(size)
#define FIRE_SCHED_STACK_FREE(p) fire_w16_stackFree(p)

static inline void fire_ctx_make(fire_ctx* c, void* stack, size_t size, void (*entry)(void)) {
    c->ss = FP_SEG(stack);                                       /* (offset 0: fire_w16_stackAlloc) */
    c->sp = (unsigned short)((FP_OFF(stack) + size - 4u) & ~1u);
    c->fresh = 1;
    fire_ctx_entryFn = entry;   /* (the scheduler has one entry for every task: fire_sched_entry) */
}

/* Loads SS:SP of the new task and calls the entry there - it never returns (fire_sched_entry ends in a switch). No cli: after `mov ss` the processor takes no
 * interrupt before the next instruction, and cli is not the program's to give under Windows. */
static void fire_ctx_start(void) {
    _asm {
        mov ax, seg fire_ctx_newSS
        mov ds, ax
        mov ax, fire_ctx_newSS
        mov dx, fire_ctx_newSP
        mov ss, ax
        mov sp, dx
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

/* ---- the slice: every 256th safepoint looks at the clock; after 55 ms (a tick of DOS) the next safepoint gives the turn away ---- */
static unsigned char fire_w16_preemptCount;
static DWORD fire_w16_sliceStart;
static int fire_w16_preempted(void) {
    DWORD now;
    if (++fire_w16_preemptCount != 0) return 0;
    now = GetTickCount();
    if (now - fire_w16_sliceStart < 55) return 0;
    fire_w16_sliceStart = now;
    return 1;
}
#define FIRE_SCHED_PREEMPTED() fire_w16_preempted()
#endif
