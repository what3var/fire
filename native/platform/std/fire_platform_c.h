/* fire native platform layer - the core every platform package offers the runtime (fire_rt.h), in C (it compiles as C++ as well, for the packages):
 *
 *   int64_t fire_plat_nowMs(void)            a monotonic clock in milliseconds
 *   int64_t fire_plat_unixMicros(void)       the wall clock: microseconds since 1970-01-01 UTC
 *   void    fire_plat_sleepMs(int64_t ms)    the calling thread sleeps
 *   void    fire_plat_exit(int code)         ends the program (the streams are flushed already); never returns
 *
 * and with FIRE_THREADS (fire threads, see docs/spec/NATIVE_BACKEND.md):
 *
 *   fire_mutex          fire_mutex_init / fire_mutex_lock / fire_mutex_unlock      (not recursive)
 *   fire_cond           fire_cond_init / fire_cond_wait(c, m) / fire_cond_waitFor(c, m, ms) (0: timed out) / fire_cond_notifyAll(c)
 *                       - always called with the mutex held
 *   fire_thread*        fire_thread_start(fn, arg, stackBytes) / fire_thread_join(t)   (stackBytes 0: the default)
 *   uint32_t            fire_atomic_load / fire_atomic_store / fire_atomic_or / fire_atomic_and / fire_atomic_add / fire_atomic_exchange   (on a fire_atomic32)
 *
 * and where the compiler has no thread-local storage (FIRE_TLS_STRUCT, e.g. FreeRTOS tasks): fire_tls_get / fire_tls_set (one pointer per thread).
 *
 * This file has the parts that are the same everywhere; the platform package includes it after it has defined its own (fire_platform.h). */
#ifndef FIRE_PLATFORM_C_H
#define FIRE_PLATFORM_C_H

#include <stdint.h>

/* A scheduler of fire itself (fire_sched.h; a platform without threads): a safepoint gives the turn to the other fire threads (FIRE_PLAT_YIELD), and asks whether a
 * timer wants that early (FIRE_PLAT_PREEMPTED). Real threads need neither. */
#ifndef FIRE_PLAT_YIELD
#define FIRE_PLAT_YIELD() ((void)0)
#endif
#ifndef FIRE_PLAT_PREEMPTED
#define FIRE_PLAT_PREEMPTED() 0
#endif

#ifndef FIRE_NORETURN   /* (fire_rt.h includes the dialect of the compiler first, cc/fire_cc.h) */
#define FIRE_NORETURN
#endif

/* ---- atomics: a 32-bit word that threads share (the signals of the runtime). The compiler's builtins where there are some, else the platform's (Windows),
 * else a plain volatile word - every access of the runtime is either relaxed or under a mutex as well. ---- */
typedef struct { volatile uint32_t v; } fire_atomic32;
#if defined(__GNUC__) || defined(__clang__)
static inline uint32_t fire_atomic_load(fire_atomic32* a) { return __atomic_load_n(&a->v, __ATOMIC_RELAXED); }
static inline void fire_atomic_store(fire_atomic32* a, uint32_t x) { __atomic_store_n(&a->v, x, __ATOMIC_RELAXED); }
static inline uint32_t fire_atomic_or(fire_atomic32* a, uint32_t x) { return __atomic_fetch_or(&a->v, x, __ATOMIC_RELAXED); }
static inline uint32_t fire_atomic_and(fire_atomic32* a, uint32_t x) { return __atomic_fetch_and(&a->v, x, __ATOMIC_RELAXED); }
static inline uint32_t fire_atomic_add(fire_atomic32* a, uint32_t x) { return __atomic_fetch_add(&a->v, x, __ATOMIC_RELAXED); }
static inline uint32_t fire_atomic_exchange(fire_atomic32* a, uint32_t x) { return __atomic_exchange_n(&a->v, x, __ATOMIC_RELAXED); }
#elif defined(_MSC_VER)
#include <intrin.h>
static inline uint32_t fire_atomic_load(fire_atomic32* a) { return a->v; }
static inline void fire_atomic_store(fire_atomic32* a, uint32_t x) { a->v = x; }
static inline uint32_t fire_atomic_or(fire_atomic32* a, uint32_t x) { return (uint32_t)_InterlockedOr((volatile long*)&a->v, (long)x); }
static inline uint32_t fire_atomic_and(fire_atomic32* a, uint32_t x) { return (uint32_t)_InterlockedAnd((volatile long*)&a->v, (long)x); }
static inline uint32_t fire_atomic_add(fire_atomic32* a, uint32_t x) { return (uint32_t)_InterlockedExchangeAdd((volatile long*)&a->v, (long)x); }
static inline uint32_t fire_atomic_exchange(fire_atomic32* a, uint32_t x) { return (uint32_t)_InterlockedExchange((volatile long*)&a->v, (long)x); }
#else
static inline uint32_t fire_atomic_load(fire_atomic32* a) { return a->v; }
static inline void fire_atomic_store(fire_atomic32* a, uint32_t x) { a->v = x; }
static inline uint32_t fire_atomic_or(fire_atomic32* a, uint32_t x) { uint32_t old = a->v; a->v = old | x; return old; }
static inline uint32_t fire_atomic_and(fire_atomic32* a, uint32_t x) { uint32_t old = a->v; a->v = old & x; return old; }
static inline uint32_t fire_atomic_add(fire_atomic32* a, uint32_t x) { uint32_t old = a->v; a->v = old + x; return old; }
static inline uint32_t fire_atomic_exchange(fire_atomic32* a, uint32_t x) { uint32_t old = a->v; a->v = x; return old; }
#endif

#endif
