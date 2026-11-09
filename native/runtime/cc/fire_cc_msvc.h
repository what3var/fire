/* fire: the dialect of Microsoft's C/C++ compiler (fire_cc.h). */
#define FIRE_CC_NAME "msvc"
#define FIRE_COLD __declspec(noinline)
#define FIRE_ALWAYS_INLINE
#define FIRE_UNUSED_LABEL
#define FIRE_MAYBE_UNUSED
#define FIRE_LIKELY(x) (x)
#define FIRE_UNLIKELY(x) (x)
#define FIRE_NORETURN __declspec(noreturn)
#define FIRE_NOT_REACHED(r)
#define FIRE_CONST_FN
#define FIRE_TLS_KEYWORD __declspec(thread)
#define FIRE_STATIC_ASSERT_C(c, m) _Static_assert(c, m)
#define FIRE_CC_RUNTIME_BEGIN
#define FIRE_CC_RUNTIME_END
