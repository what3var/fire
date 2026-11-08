/* fire: the dialect of Open Watcom (wcc for 16-bit x86, wcc386 for 32-bit; fire_cc.h) - the compiler of the targets dos16, dos32, win16, win32-legacy and
 * bios-x86 (ROADMAP 13). C99 in part (-za99): no _Static_assert, no thread-local storage (no fire threads on these targets). */
#define FIRE_CC_NAME "watcom"
#define FIRE_COLD
#define FIRE_UNUSED_LABEL
#define FIRE_MAYBE_UNUSED
#define FIRE_LIKELY(x) (x)
#define FIRE_UNLIKELY(x) (x)
/* (no __declspec(aborts): with it Open Watcom made a call of exitNow after reportUnhandled jump back into reportUnhandled - an endless loop, dos16) */
#define FIRE_NORETURN
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
/* (C99's HUGE_VALF - the code of a float literal beyond the range of a 32-bit float - is missing in its math.h) */
#ifndef HUGE_VALF
#define HUGE_VALF ((float)HUGE_VAL)
#endif
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
/* An older C library of Open Watcom - 1.9 (__WATCOMC__ 1290) and the "2.0beta1" of the old Perforce line (__WATCOMC__ 2000, the portable archive of
 * openwatcom.org) - is told by its math.h (no FP_ILOGB0; the open-watcom-v2 of GitHub, 1300, has it): its math.h lacks a few functions of C99 the runtime
 * uses (a stand-in each), its opendir/readdir are in direct.h (no dirent.h: fire_fs_dos.h). */
#ifndef FP_ILOGB0
#define FIRE_CC_WATCOM_OLD_LIBC 1
#endif
#ifdef FIRE_CC_WATCOM_OLD_LIBC
static double fire_cc_fmax(double a, double b) { return isnan(a) ? b : isnan(b) ? a : a > b ? a : b; }
static double fire_cc_fmin(double a, double b) { return isnan(a) ? b : isnan(b) ? a : a < b ? a : b; }
static double fire_cc_trunc(double x) { return x < 0 ? ceil(x) : floor(x); }
static double fire_cc_round(double x) { return x < 0 ? -floor(-x + 0.5) : floor(x + 0.5); }
static double fire_cc_copysign(double x, double y) { double m = fabs(x); return (y < 0 || (y == 0 && 1.0 / y < 0)) ? -m : m; }
static int fire_cc_ilogb(double x) { int e; if (x == 0) return -2147483647 - 1; frexp(x, &e); return e - 1; }
static double fire_cc_nextafter(double x, double y) {
    union { double d; uint64_t u; } v;
    if (isnan(x) || isnan(y)) return x + y;
    if (x == y) return y;
    if (x == 0) { v.u = 1; return y > 0 ? v.d : -v.d; }   /* (the smallest subnormal toward y) */
    v.d = x;
    if ((x < y) == (x > 0)) v.u++; else v.u--;            /* (one step of the bits: away from or toward zero) */
    return v.d;
}
#define fmax(a, b) fire_cc_fmax(a, b)
#define fmin(a, b) fire_cc_fmin(a, b)
#define trunc(x) fire_cc_trunc(x)
#define round(x) fire_cc_round(x)
#define copysign(x, y) fire_cc_copysign(x, y)
#define ilogb(x) fire_cc_ilogb(x)
#define nextafter(x, y) fire_cc_nextafter(x, y)
#endif
/* A comparison with NaN on the 8087 (and its emulation) sets "less" and "equal" at once: == checks for NaN first. */
#define FIRE_CC_FEQ(x, y) (!isnan(x) && !isnan(y) && (x) == (y))
#if defined(_M_I86)
/* 16-bit x86 (wcc): the libraries of Open Watcom have no float functions of C99 (sqrtf, cosf, ...: the linker does not find them) - the double ones, with
 * the result made a float again (the 8087 computes in 80 bits either way) */
#define sqrtf(x) ((float)sqrt(x))
#define sinf(x) ((float)sin(x))
#define cosf(x) ((float)cos(x))
#define tanf(x) ((float)tan(x))
#define asinf(x) ((float)asin(x))
#define acosf(x) ((float)acos(x))
#define atanf(x) ((float)atan(x))
#define atan2f(y, x) ((float)atan2(y, x))
#define fabsf(x) ((float)fabs(x))
#define floorf(x) ((float)floor(x))
#define ceilf(x) ((float)ceil(x))
#define powf(x, y) ((float)pow(x, y))
#define expf(x) ((float)exp(x))
#define logf(x) ((float)log(x))
#define log10f(x) ((float)log10(x))
#define fmodf(x, y) ((float)fmod(x, y))
#define sinhf(x) ((float)sinh(x))
#define coshf(x) ((float)cosh(x))
#define tanhf(x) ((float)tanh(x))
#define truncf(x) ((float)trunc(x))
#define roundf(x) ((float)round(x))
#define fminf(a, b) ((float)fmin(a, b))
#define fmaxf(a, b) ((float)fmax(a, b))
/* 16-bit x86 (wcc): the large model has far pointers already; the data of arrays and buffers may pass 64 KB (FIRE_HUGE: the compiler normalizes the segment) */
#define FIRE_FAR __far
#define FIRE_HUGE __huge
#if defined(__HUGE__)
#define FIRE_CC_NEAR_DATA __near   /* (the state of the thread in DGROUP: a test of g_unwind without loading a segment) */
/* The huge model (dos16): every pointer is huge - an array or a buffer may hold more than 64 KB; its block comes from halloc (DOS memory, a size_t of 16 bits
 * cannot say it), the blocks are remembered to give them back with hfree. */
#include <malloc.h>
#define FIRE_CC_BIG_KEEP 256
static void __huge* fire_cc_bigBlocks[FIRE_CC_BIG_KEEP];
static void* fire_cc_bigAlloc(uint32_t bytes) {
    void __huge* p;
    int i;
    for (i = 0; i < FIRE_CC_BIG_KEEP && fire_cc_bigBlocks[i]; i++) {}
    if (i == FIRE_CC_BIG_KEEP) return NULL;   /* (more big blocks than the table holds: out of memory) */
    p = halloc((long)((bytes + 15u) / 16u), 16);
    if (p) fire_cc_bigBlocks[i] = p;
    return (void*)p;
}
static int fire_cc_bigFree(void* p) {
    int i;
    if (p == NULL) return 1;   /* (nothing to give back - and an empty place of the table is NULL too: hfree(NULL) asked DOS to free segment 0) */
    for (i = 0; i < FIRE_CC_BIG_KEEP; i++)
        if (fire_cc_bigBlocks[i] == (void __huge*)p) { hfree(fire_cc_bigBlocks[i]); fire_cc_bigBlocks[i] = 0; return 1; }
    return 0;
}
#define FIRE_CC_BIG_ALLOC(bytes) fire_cc_bigAlloc(bytes)
/* from 16 KB on a block comes from DOS itself (to the paragraph, and back to DOS when it is freed): the heap of the C library grows in big pieces that a
 * freed block of this size cannot give back - a framebuffer of 64000 bytes cost it 118 KB */
#define FIRE_CC_BIG_FROM 0x4000u
#define FIRE_CC_BIG_FREE(p) fire_cc_bigFree(p)
/* memset/memcpy of the C library compute with the offset of a far pointer: a step must not pass the end of the segment of either pointer */
#include <i86.h>
static uint32_t fire_cc_room(const void __huge* p) { return 0x10000ul - FP_OFF(p); }
static void fire_cc_zero(void* p, uint32_t bytes) {
    char __huge* q = (char __huge*)p;
    while (bytes > 0) {
        uint32_t step = fire_cc_room(q);
        if (step > 0x8000u) step = 0x8000u;
        if (step > bytes) step = bytes;
        memset((void __far*)q, 0, (size_t)step);
        q += step;
        bytes -= step;
    }
}
static void fire_cc_copy(void* to, const void* from, uint32_t bytes) {
    char __huge* d = (char __huge*)to;
    const char __huge* s = (const char __huge*)from;
    while (bytes > 0) {
        uint32_t step = fire_cc_room(d), r2 = fire_cc_room(s);
        if (r2 < step) step = r2;
        if (step > 0x8000u) step = 0x8000u;
        if (step > bytes) step = bytes;
        memcpy((void __far*)d, (const void __far*)s, (size_t)step);
        d += step;
        s += step;
        bytes -= step;
    }
}
static size_t fire_cc_ioStep(const void* p, int64_t n) {
    uint32_t step = fire_cc_room((const void __huge*)p);
    if (step > 0x8000u) step = 0x8000u;
    return (size_t)(n < (int64_t)step ? n : (int64_t)step);
}
#define FIRE_CC_IO_STEP(p, n) fire_cc_ioStep(p, n)
#define FIRE_CC_ZERO(p, bytes) fire_cc_zero(p, bytes)
#define FIRE_CC_COPY(to, from, bytes) fire_cc_copy(to, from, bytes)
#endif
#endif
