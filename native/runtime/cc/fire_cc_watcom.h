/* fire: the dialect of Open Watcom (wcc for 16-bit x86, wcc386 for 32-bit; fire_cc.h) - the compiler of the targets dos16, dos32, win16, win32-legacy and
 * bios-x86 (ROADMAP 13). C99 in part (-za99): no _Static_assert, no thread-local storage (no fire threads on these targets). */
#define FIRE_CC_NAME "watcom"
#define FIRE_COLD
#define FIRE_UNUSED_LABEL
#define FIRE_MAYBE_UNUSED
#define FIRE_LIKELY(x) (x)
#define FIRE_UNLIKELY(x) (x)
#define FIRE_NORETURN __declspec(aborts)
#define FIRE_NOT_REACHED(r) return r;
#define FIRE_CONST_FN
#define FIRE_TLS_KEYWORD
#define FIRE_CC_CAT2(a, b) a##b
#define FIRE_CC_CAT(a, b) FIRE_CC_CAT2(a, b)
#define FIRE_STATIC_ASSERT_C(c, m) typedef char FIRE_CC_CAT(fire_static_assert_, __LINE__)[(c) ? 1 : -1]
#define FIRE_CC_RUNTIME_BEGIN
#define FIRE_CC_RUNTIME_END
#if defined(_M_I86)
/* 16-bit x86 (wcc): the large model has far pointers already; the data of arrays and buffers may pass 64 KB (FIRE_HUGE: the compiler normalizes the segment) */
#define FIRE_FAR __far
#define FIRE_HUGE __huge
#endif
