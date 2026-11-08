/* fire: any other C99 compiler - TinyCC, the compilers of microcontrollers (fire_cc.h). Nothing that is not plain C. */
#ifdef __TINYC__
#define FIRE_CC_NAME "tcc"
#else
#define FIRE_CC_NAME "generic"
#endif
#define FIRE_COLD
#define FIRE_ALWAYS_INLINE
#define FIRE_UNUSED_LABEL
#define FIRE_MAYBE_UNUSED
#define FIRE_LIKELY(x) (x)
#define FIRE_UNLIKELY(x) (x)
#define FIRE_NORETURN
#define FIRE_NOT_REACHED(r) return r;   /* (the compiler does not know that the call before does not return) */
#define FIRE_CONST_FN
#if defined(__cplusplus)
#define FIRE_TLS_KEYWORD thread_local
#else
#define FIRE_TLS_KEYWORD _Thread_local
#endif
/* (C99 has no _Static_assert: an array whose size is negative when the condition does not hold) */
#define FIRE_CC_CAT2(a, b) a##b
#define FIRE_CC_CAT(a, b) FIRE_CC_CAT2(a, b)
#define FIRE_STATIC_ASSERT_C(c, m) typedef char FIRE_CC_CAT(fire_static_assert_, __LINE__)[(c) ? 1 : -1]
#define FIRE_CC_RUNTIME_BEGIN
#define FIRE_CC_RUNTIME_END
