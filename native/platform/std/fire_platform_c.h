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
 *                       - relaxed; fire_atomic_loadAcquire / fire_atomic_storeRelease / fire_atomic_addAcqRel order what was written before (a hand-over, a
 *                       count whose end frees the memory)
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
static inline uint32_t fire_atomic_loadAcquire(fire_atomic32* a) { return __atomic_load_n(&a->v, __ATOMIC_ACQUIRE); }
static inline void fire_atomic_storeRelease(fire_atomic32* a, uint32_t x) { __atomic_store_n(&a->v, x, __ATOMIC_RELEASE); }
static inline uint32_t fire_atomic_addAcqRel(fire_atomic32* a, uint32_t x) { return __atomic_fetch_add(&a->v, x, __ATOMIC_ACQ_REL); }
#elif defined(_MSC_VER)
#include <intrin.h>
static inline uint32_t fire_atomic_load(fire_atomic32* a) { return a->v; }
static inline void fire_atomic_store(fire_atomic32* a, uint32_t x) { a->v = x; }
static inline uint32_t fire_atomic_or(fire_atomic32* a, uint32_t x) { return (uint32_t)_InterlockedOr((volatile long*)&a->v, (long)x); }
static inline uint32_t fire_atomic_and(fire_atomic32* a, uint32_t x) { return (uint32_t)_InterlockedAnd((volatile long*)&a->v, (long)x); }
static inline uint32_t fire_atomic_add(fire_atomic32* a, uint32_t x) { return (uint32_t)_InterlockedExchangeAdd((volatile long*)&a->v, (long)x); }
static inline uint32_t fire_atomic_exchange(fire_atomic32* a, uint32_t x) { return (uint32_t)_InterlockedExchange((volatile long*)&a->v, (long)x); }
static inline uint32_t fire_atomic_loadAcquire(fire_atomic32* a) { return (uint32_t)_InterlockedOr((volatile long*)&a->v, 0); }
static inline void fire_atomic_storeRelease(fire_atomic32* a, uint32_t x) { (void)_InterlockedExchange((volatile long*)&a->v, (long)x); }
static inline uint32_t fire_atomic_addAcqRel(fire_atomic32* a, uint32_t x) { return (uint32_t)_InterlockedExchangeAdd((volatile long*)&a->v, (long)x); }
#elif defined(__TINYC__) && (defined(__x86_64__) || defined(__i386__))
/* TinyCC has no builtins for atomics: the instructions of x86 (lock xadd, xchg, lock cmpxchg); loads and stores are ordered by the processor (TSO), the
 * empty asm keeps the compiler from moving accesses across them - enough for the scheduler of tasks without the GIL (FIRE_NO_GIL) */
#define FIRE_PLAT_ASM_ATOMICS 1
static inline uint32_t fire_atomic_load(fire_atomic32* a) { uint32_t x = a->v; __asm__ __volatile__("" : : : "memory"); return x; }
static inline void fire_atomic_store(fire_atomic32* a, uint32_t x) { __asm__ __volatile__("" : : : "memory"); a->v = x; }
static inline uint32_t fire_atomic_add(fire_atomic32* a, uint32_t x) { __asm__ __volatile__("lock; xaddl %0, %1" : "+r"(x), "+m"(a->v) : : "memory"); return x; }
static inline uint32_t fire_atomic_exchange(fire_atomic32* a, uint32_t x) { __asm__ __volatile__("xchgl %0, %1" : "+r"(x), "+m"(a->v) : : "memory"); return x; }
static inline uint32_t fire_atomic_cas32(fire_atomic32* a, uint32_t expected, uint32_t desired) {
    uint32_t prev;
    __asm__ __volatile__("lock; cmpxchgl %2, %1" : "=a"(prev), "+m"(a->v) : "r"(desired), "0"(expected) : "memory");
    return prev;
}
static inline uint32_t fire_atomic_or(fire_atomic32* a, uint32_t x) { uint32_t old; do old = a->v; while (fire_atomic_cas32(a, old, old | x) != old); return old; }
static inline uint32_t fire_atomic_and(fire_atomic32* a, uint32_t x) { uint32_t old; do old = a->v; while (fire_atomic_cas32(a, old, old & x) != old); return old; }
static inline uint32_t fire_atomic_loadAcquire(fire_atomic32* a) { return fire_atomic_load(a); }
static inline void fire_atomic_storeRelease(fire_atomic32* a, uint32_t x) { fire_atomic_store(a, x); }
static inline uint32_t fire_atomic_addAcqRel(fire_atomic32* a, uint32_t x) { return fire_atomic_add(a, x); }
#else
static inline uint32_t fire_atomic_load(fire_atomic32* a) { return a->v; }
static inline void fire_atomic_store(fire_atomic32* a, uint32_t x) { a->v = x; }
static inline uint32_t fire_atomic_or(fire_atomic32* a, uint32_t x) { uint32_t old = a->v; a->v = old | x; return old; }
static inline uint32_t fire_atomic_and(fire_atomic32* a, uint32_t x) { uint32_t old = a->v; a->v = old & x; return old; }
static inline uint32_t fire_atomic_add(fire_atomic32* a, uint32_t x) { uint32_t old = a->v; a->v = old + x; return old; }
static inline uint32_t fire_atomic_exchange(fire_atomic32* a, uint32_t x) { uint32_t old = a->v; a->v = x; return old; }
static inline uint32_t fire_atomic_loadAcquire(fire_atomic32* a) { return a->v; }
static inline void fire_atomic_storeRelease(fire_atomic32* a, uint32_t x) { a->v = x; }
static inline uint32_t fire_atomic_addAcqRel(fire_atomic32* a, uint32_t x) { uint32_t old = a->v; a->v = old + x; return old; }
#endif

#endif
