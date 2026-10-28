/* fire native platform layer for POSIX systems (Linux, macOS, the BSDs; also the browser, see platform/wasm): the clock, sleep and - with FIRE_THREADS -
 * pthreads. In C (it compiles as C++ as well). The generated file defines _POSIX_C_SOURCE before it includes anything. */
#ifndef FIRE_PLATFORM_POSIX_H
#define FIRE_PLATFORM_POSIX_H

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <time.h>
#include "fire_platform_c.h"
#ifdef FIRE_THREADS
#include <pthread.h>
#endif

static inline int64_t fire_plat_nowMs(void) {
    struct timespec t;
    clock_gettime(CLOCK_MONOTONIC, &t);
    return (int64_t)t.tv_sec * 1000 + t.tv_nsec / 1000000;
}

static inline int64_t fire_plat_unixMicros(void) {
    struct timespec t;
    clock_gettime(CLOCK_REALTIME, &t);
    return (int64_t)t.tv_sec * 1000000 + t.tv_nsec / 1000;
}

#ifndef FIRE_PLAT_SLEEP_CUSTOM   /* (a platform with its own sleep, e.g. the browser, defines it after this header) */
static inline void fire_plat_sleepMs(int64_t ms) {
    struct timespec t;
    if (ms < 0) ms = 0;
    t.tv_sec = (time_t)(ms / 1000);
    t.tv_nsec = (long)(ms % 1000) * 1000000L;
    while (nanosleep(&t, &t) != 0) {}
}
#endif

/* Ends the program. With threads the other threads must not run static destructors under their feet: no cleanup. */
FIRE_NORETURN static inline void fire_plat_exit(int code) {
#ifdef FIRE_THREADS
    _Exit(code);
#else
    exit(code);
#endif
}

#ifdef FIRE_THREADS
typedef struct { pthread_mutex_t m; } fire_mutex;
typedef struct { pthread_cond_t c; } fire_cond;
typedef struct fire_thread { pthread_t t; } fire_thread;
typedef void (*fire_thread_fn)(void*);

static inline void fire_mutex_init(fire_mutex* m) { pthread_mutex_init(&m->m, NULL); }
static inline void fire_mutex_lock(fire_mutex* m) { pthread_mutex_lock(&m->m); }
static inline void fire_mutex_unlock(fire_mutex* m) { pthread_mutex_unlock(&m->m); }
static inline void fire_cond_init(fire_cond* c) { pthread_cond_init(&c->c, NULL); }
static inline void fire_cond_wait(fire_cond* c, fire_mutex* m) { pthread_cond_wait(&c->c, &m->m); }
/* 1: signalled, 0: timed out. (The real-time clock: macOS has no monotonic clock for condition variables; the runtime only waits in short slices.) */
static inline int fire_cond_waitFor(fire_cond* c, fire_mutex* m, int64_t ms) {
    struct timespec t;
    clock_gettime(CLOCK_REALTIME, &t);
    if (ms < 0) ms = 0;
    t.tv_sec += (time_t)(ms / 1000);
    t.tv_nsec += (long)(ms % 1000) * 1000000L;
    if (t.tv_nsec >= 1000000000L) { t.tv_sec++; t.tv_nsec -= 1000000000L; }
    return pthread_cond_timedwait(&c->c, &m->m, &t) == 0;
}
static inline void fire_cond_notifyAll(fire_cond* c) { pthread_cond_broadcast(&c->c); }

typedef struct { fire_thread_fn fn; void* arg; } fire_thread_start_box;
static inline void* fire_thread_trampoline(void* p) {
    fire_thread_start_box box = *(fire_thread_start_box*)p;
    free(p);
    box.fn(box.arg);
    return NULL;
}
static inline fire_thread* fire_thread_start(fire_thread_fn fn, void* arg, uint32_t stackBytes) {
    fire_thread* t = (fire_thread*)malloc(sizeof(fire_thread));
    fire_thread_start_box* box = (fire_thread_start_box*)malloc(sizeof(fire_thread_start_box));
    pthread_attr_t attr;
    if (!t || !box) { fputs("fire runtime error: Out of memory.\n", stderr); fire_plat_exit(1); }
    box->fn = fn;
    box->arg = arg;
    pthread_attr_init(&attr);
    if (stackBytes) pthread_attr_setstacksize(&attr, stackBytes < 65536 ? 65536 : stackBytes);
    if (pthread_create(&t->t, &attr, fire_thread_trampoline, box) != 0) { fputs("fire runtime error: could not start a thread.\n", stderr); fire_plat_exit(1); }
    pthread_attr_destroy(&attr);
    return t;
}
static inline void fire_thread_join(fire_thread* t) { pthread_join(t->t, NULL); free(t); }
#endif

#endif
