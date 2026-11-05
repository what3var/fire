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

/* The C library of Open Watcom rounds a half away from zero in nearbyint(); C (and the VM, .NET) round it to the even number in the default rounding mode. */
#include <math.h>
static double fire_cc_nearbyint(double x) {
    double f = floor(x), d = x - f;
    if (d > 0.5) return f + 1.0;
    if (d < 0.5) return f;
    return fmod(f, 2.0) == 0.0 ? f : f + 1.0;   /* (a half: to the even neighbour; NaN and the infinities fall through unchanged) */
}
#define nearbyint(x) fire_cc_nearbyint(x)
/* ... and so does its printf("%.*f") with a number that lies exactly on a half (0.125 to 2 places): such a number is moved to the even neighbour first. */
static double fire_cc_fixedHalfEven(double d, int places) {
    double p = 1.0, s;
    int i;
    for (i = 0; i < places && i < 18; i++) p *= 10.0;
    s = d * p;
    if (fabs(s) < 4503599627370496.0 && s / p == d && fabs(s - floor(s) - 0.5) == 0.0) return fire_cc_nearbyint(s) / p;
    return d;
}
#define FIRE_CC_FIXED(d, places) fire_cc_fixedHalfEven(d, places)
/* A comparison with NaN on the 8087 (and its emulation) sets "less" and "equal" at once: == checks for NaN first. */
#define FIRE_CC_FEQ(x, y) (!isnan(x) && !isnan(y) && (x) == (y))
#if defined(_M_I86)
/* 16-bit x86 (wcc): the large model has far pointers already; the data of arrays and buffers may pass 64 KB (FIRE_HUGE: the compiler normalizes the segment) */
#define FIRE_FAR __far
#define FIRE_HUGE __huge
#endif
