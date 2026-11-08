/* fire: the dialect of gcc and clang (fire_cc.h). */
#ifdef __clang__
#define FIRE_CC_NAME "clang"
#else
#define FIRE_CC_NAME "gcc"
#endif
#define FIRE_COLD __attribute__((noinline, cold))
/* (a small function the hot paths call everywhere: embedded even where the compiler would not) */
#ifdef FIRE_OPTIMIZE_SIZE
#define FIRE_ALWAYS_INLINE
#else
#define FIRE_ALWAYS_INLINE __attribute__((always_inline))
#endif
#define FIRE_UNUSED_LABEL __attribute__((unused))
#define FIRE_MAYBE_UNUSED __attribute__((unused))
#define FIRE_LIKELY(x) __builtin_expect(!!(x), 1)
#define FIRE_UNLIKELY(x) __builtin_expect(!!(x), 0)
#define FIRE_NORETURN __attribute__((noreturn))
#define FIRE_NOT_REACHED(r)
#define FIRE_CONST_FN __attribute__((const))
/* (also in C++: `thread_local` would reach the variables of the program - C, `_Thread_local` - through a wrapper of C++ that can hand out a null pointer;
 * `__thread` is the same storage as in C) */
#define FIRE_TLS_KEYWORD __thread
#define FIRE_STATIC_ASSERT_C(c, m) _Static_assert(c, m)
#if defined(__GNUC__) && !defined(__clang__)
#if __GNUC__ >= 12
/* a function keeps the list of its global scope in `g_globalOwn` for as long as it runs (the thread's or the program's top level): not a dangling pointer;
 * after inlining, GCC follows the pointer of a number (its bits share the union with `p`) into retain/release on paths where `isRef` is false: no real access */
#define FIRE_CC_GCC12_IGNORES _Pragma("GCC diagnostic ignored \"-Wdangling-pointer\"") _Pragma("GCC diagnostic ignored \"-Warray-bounds\"")
#else
#define FIRE_CC_GCC12_IGNORES
#endif
/* (C: a cold path is `static inline` and `noinline` at once, on purpose - the fast paths around it are inlined, it is not) */
#define FIRE_CC_RUNTIME_BEGIN FIRE_CC_GCC12_IGNORES _Pragma("GCC diagnostic push") _Pragma("GCC diagnostic ignored \"-Wattributes\"")
#define FIRE_CC_RUNTIME_END _Pragma("GCC diagnostic pop")
#else
#define FIRE_CC_RUNTIME_BEGIN
#define FIRE_CC_RUNTIME_END
#endif
