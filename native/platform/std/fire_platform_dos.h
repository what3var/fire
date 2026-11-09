/* fire native platform layer for DOS (ROADMAP 13: dos16 in real mode, dos32 with DPMI): the clock, sleep and the end of the program, through the C library of
 * the compiler (Open Watcom: clock() counts the BIOS timer, delay() waits, both work in real mode and under DPMI). No fire threads: DOS has one thread. */
#ifndef FIRE_PLATFORM_DOS_H
#define FIRE_PLATFORM_DOS_H

#include <stdint.h>
#include <stdlib.h>
#include <time.h>
#include <dos.h>
#include "fire_platform_c.h"


/* A monotonic clock in milliseconds: the BIOS timer (18.2 ticks per second; clock() widens it), carried over midnight. */
static inline int64_t fire_plat_nowMs(void) {
    static clock_t last = 0;
    static int64_t base = 0;
    clock_t now = clock();
    if (now < last) base += (int64_t)last * 1000 / CLOCKS_PER_SEC;   /* (the BIOS counter starts again at midnight) */
    last = now;
    return base + (int64_t)now * 1000 / CLOCKS_PER_SEC;
}

/* The wall clock: DOS keeps local time without a zone; the C library's time() is the best there is. */
static inline int64_t fire_plat_unixMicros(void) {
    static int64_t anchor = 0;
    static int anchored = 0;
    int64_t seconds = (int64_t)time(NULL) * 1000000;
    int64_t ms = fire_plat_nowMs() * 1000;
    int64_t drift = seconds - (anchor + ms);
    if (!anchored || drift > 1000000 || drift < -1000000) { anchor = seconds - ms; anchored = 1; }
    return anchor + ms;
}

static inline void fire_dos_delayMs(int64_t ms) {
    while (ms > 30000) { delay(30000); ms -= 30000; }
    if (ms > 0) delay((unsigned)ms);
}

#ifdef FIRE_THREADS
/* DOS has one thread: fire threads are tasks of fire's own scheduler (std/fire_sched.h; ROADMAP 13.2b) - switched at the safepoints, a timer interrupt asks for it */
#ifndef FIRE_SCHED_STACK_BYTES
#define FIRE_SCHED_STACK_BYTES 16384
#endif
#define FIRE_SCHED_IDLE(ms) fire_dos_delayMs(ms)
#if defined(_M_I86)
#include "sched/fire_ctx_x86_16.h"
#elif defined(__386__)
#include "sched/fire_ctx_x86_32.h"
#else
#error "fire platform dos: the switch of the context for this processor is missing (std/sched/)"
#endif
#include "fire_sched.h"
static inline void fire_plat_sleepMs(int64_t ms) { fire_sched_sleepMs(ms); }
#else
static inline void fire_plat_sleepMs(int64_t ms) { fire_dos_delayMs(ms); }
#endif

FIRE_NORETURN static inline void fire_plat_exit(int code) {
    exit(code);
}

#include "fire_env_dos.h"   /* (the process: arguments, path, environment variables) */

#endif
