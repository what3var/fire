/* fire native platform layer for Windows: the clock, sleep and - with FIRE_THREADS - Win32 threads, slim reader/writer locks and condition variables.
 * In C (it compiles as C++ as well). */
#ifndef FIRE_PLATFORM_WIN32_H
#define FIRE_PLATFORM_WIN32_H

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <process.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include "fire_platform_c.h"

static inline int64_t fire_plat_nowMs(void) {
    LARGE_INTEGER count, frequency;
    QueryPerformanceCounter(&count);
    QueryPerformanceFrequency(&frequency);
    return (int64_t)(count.QuadPart / (frequency.QuadPart / 1000));
}

static inline int64_t fire_plat_unixMicros(void) {
    FILETIME ft;
    ULARGE_INTEGER t;
    GetSystemTimeAsFileTime(&ft);
    t.LowPart = ft.dwLowDateTime;
    t.HighPart = ft.dwHighDateTime;
    return (int64_t)(t.QuadPart / 10) - 11644473600000000LL;   /* 100 ns since 1601 -> microseconds since 1970 */
}

static inline void fire_plat_sleepMs(int64_t ms) { Sleep(ms < 0 ? 0 : (DWORD)ms); }

FIRE_NORETURN static inline void fire_plat_exit(int code) {
#ifdef FIRE_THREADS
    _exit(code);
#else
    exit(code);
#endif
}

#ifdef FIRE_THREADS
typedef struct { SRWLOCK l; } fire_mutex;
typedef struct { CONDITION_VARIABLE c; } fire_cond;
typedef struct fire_thread { HANDLE h; } fire_thread;
typedef void (*fire_thread_fn)(void*);

static inline void fire_mutex_init(fire_mutex* m) { InitializeSRWLock(&m->l); }
static inline void fire_mutex_lock(fire_mutex* m) { AcquireSRWLockExclusive(&m->l); }
static inline void fire_mutex_unlock(fire_mutex* m) { ReleaseSRWLockExclusive(&m->l); }
static inline void fire_cond_init(fire_cond* c) { InitializeConditionVariable(&c->c); }
static inline void fire_cond_wait(fire_cond* c, fire_mutex* m) { SleepConditionVariableSRW(&c->c, &m->l, INFINITE, 0); }
static inline int fire_cond_waitFor(fire_cond* c, fire_mutex* m, int64_t ms) { return SleepConditionVariableSRW(&c->c, &m->l, ms < 0 ? 0 : (DWORD)ms, 0) != 0; }
static inline void fire_cond_notifyAll(fire_cond* c) { WakeAllConditionVariable(&c->c); }

typedef struct { fire_thread_fn fn; void* arg; } fire_thread_start_box;
static inline unsigned __stdcall fire_thread_trampoline(void* p) {
    fire_thread_start_box box = *(fire_thread_start_box*)p;
    free(p);
    box.fn(box.arg);
    return 0;
}
static inline fire_thread* fire_thread_start(fire_thread_fn fn, void* arg, uint32_t stackBytes) {
    fire_thread* t = (fire_thread*)malloc(sizeof(fire_thread));
    fire_thread_start_box* box = (fire_thread_start_box*)malloc(sizeof(fire_thread_start_box));
    if (!t || !box) { fputs("fire runtime error: Out of memory.\n", stderr); fire_plat_exit(1); }
    box->fn = fn;
    box->arg = arg;
    t->h = (HANDLE)_beginthreadex(NULL, stackBytes, fire_thread_trampoline, box, 0, NULL);
    if (!t->h) { fputs("fire runtime error: could not start a thread.\n", stderr); fire_plat_exit(1); }
    return t;
}
static inline void fire_thread_join(fire_thread* t) { WaitForSingleObject(t->h, INFINITE); CloseHandle(t->h); free(t); }
#endif

#endif
