/* fire native runtime - the core that the code `fire.Native` generates from the bytecode is built on.
 *
 * Design rules (see docs/NATIVE_BACKEND.md):
 *  * Same semantics as the C# VM; the test suite runs programs on both and compares the output.
 *  * Plain C11 (no exceptions, no RTTI, no C++): the generated program is C and builds with any C compiler - gcc, clang, MSVC, TinyCC, the compilers of
 *    microcontrollers. The file compiles as C++ as well: the natives of packages that are written in C++ include it (their functions are then in namespace
 *    `fire`, with C linkage, so that the C program and the C++ packages share one runtime).
 *  * Every operation has an inline fast path that the compiler can fold when the operand kinds are known at compile time (they usually are:
 *    `var i = 0` is an Int constant), and a cold slow path for everything else.
 *
 * Header-only: the functions are `static inline`; the global state (the unit table, the handle table, the state of the threads, ...) is defined in exactly one
 * translation unit, the one that defines FIRE_RT_IMPLEMENTATION before it includes this file (the generated program, or the library of a package for the VM). */
#ifndef FIRE_RT_H
#define FIRE_RT_H

/* (POSIX: clock_gettime, nanosleep, pthreads - before the first system header) */
#if !defined(_WIN32) && !defined(_POSIX_C_SOURCE) && !defined(_GNU_SOURCE) && !defined(__APPLE__)
#define _POSIX_C_SOURCE 200809L
#endif

#include <math.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef FIRE_LIBRARY
#include "fire_pkg_abi.h"   /* (the library for the VM: the host structure) */
#endif

/* The platform layer (docs/NATIVE_BACKEND.md, "Plattformschicht"): everything that depends on the operating system or the board - exit, clock, and for fire
 * threads the mutex, the condition variable, the thread and where the per-thread state lives - is one header per platform package. The generated file names
 * it (FIRE_PLATFORM_HEADER, from the target configuration); without a name the host's own is taken. */
#ifndef FIRE_PLATFORM_HEADER
#if defined(_WIN32)
#define FIRE_PLATFORM_HEADER "platform/windows/fire_platform.h"
#else
#define FIRE_PLATFORM_HEADER "platform/posix/fire_platform.h"
#endif
#endif
#include FIRE_PLATFORM_HEADER

#if defined(__GNUC__) && !defined(__clang__) && __GNUC__ >= 12
/* a function keeps the list of its global scope in `g_globalOwn` for as long as it runs (the thread's or the program's top level): not a dangling pointer */
#pragma GCC diagnostic ignored "-Wdangling-pointer"
/* after inlining, GCC follows the pointer of a number (its bits share the union with `p`) into retain/release on paths where `isRef` is false: no real access */
#pragma GCC diagnostic ignored "-Warray-bounds"
#endif

#if defined(__GNUC__) && !defined(__clang__)
#pragma GCC diagnostic push
/* (C: a cold path is `static inline` and `noinline` at once, on purpose - the fast paths around it are inlined, it is not) */
#pragma GCC diagnostic ignored "-Wattributes"
#endif

#if defined(__GNUC__) || defined(__clang__)
#define FIRE_COLD __attribute__((noinline, cold))
#define FIRE_UNUSED_LABEL __attribute__((unused))
#define FIRE_MAYBE_UNUSED __attribute__((unused))
#define FIRE_LIKELY(x) __builtin_expect(!!(x), 1)
#define FIRE_UNLIKELY(x) __builtin_expect(!!(x), 0)
#else
#define FIRE_COLD
#define FIRE_UNUSED_LABEL
#define FIRE_MAYBE_UNUSED
#define FIRE_LIKELY(x) (x)
#define FIRE_UNLIKELY(x) (x)
#endif
#define FIRE_FN static inline

/* Thread-local storage of the compiler (only with fire threads; FIRE_TLS_STRUCT platforms keep the state on the task instead). */
#if defined(FIRE_THREADS) && !defined(FIRE_TLS_STRUCT)
#if defined(__cplusplus)
#define FIRE_TLS thread_local
#elif defined(_MSC_VER)
#define FIRE_TLS __declspec(thread)
#else
#define FIRE_TLS _Thread_local
#endif
#else
#define FIRE_TLS
#endif

#ifdef __cplusplus
#define FIRE_BEGIN extern "C" { namespace fire {
#define FIRE_END } }
#define FIRE_STATIC_ASSERT(c, m) static_assert(c, m)
#else
#define FIRE_BEGIN
#define FIRE_END
#define FIRE_STATIC_ASSERT(c, m) _Static_assert(c, m)
#endif

/* Optional arguments (`Int(5)`, `Int(5, unit)`): the macro picks the function by the number of arguments. */
#define FIRE_EXPAND(x) x
#define FIRE_PICK2(_1, _2, NAME, ...) NAME

FIRE_BEGIN

/* A UTF-16 code unit (the characters of a string, like the VM). */
#ifdef __cplusplus
typedef char16_t fire_char16;
#else
typedef uint16_t fire_char16;
#endif

/* Precision of `float`: double by default, `float` when the program is generated with `#floatwidth 32` (the generator then defines FIRE_FLOAT32). The C# VM
 * rounds every float result to 32 bits in that mode, so both give the same numbers. */
#ifdef FIRE_FLOAT32
typedef float Real;
#else
typedef double Real;
#endif

/* Ends the process. With threads the other threads must not run static destructors under their feet: no cleanup, only the streams are flushed. */
FIRE_NORETURN FIRE_FN void exitNow(int code) {
    fflush(stdout);
    fflush(stderr);
    fire_plat_exit(code);
}

enum { K_Bool, K_Int, K_Float, K_Char, K_String, K_Class, K_Lambda, K_Pointer, K_Array, K_Buffer, K_Undefined };

/* A runtime value: 16 bytes, trivially copyable. `unit` indexes the program's unit table (0 = unitless). */
typedef struct Value {
    uint8_t kind;
    uint8_t width;
    uint16_t reserved;
    uint32_t unit;
    union {
        int64_t i;
        Real f;
        const void* p;
    };
} Value;
FIRE_STATIC_ASSERT(sizeof(Value) == 16, "Value must stay 16 bytes");

/* The size of a header that values follow (arrays, objects, lambdas): rounded up so that the values behind it are aligned. */
#define FIRE_HDR(T) ((sizeof(T) + 7u) & ~(size_t)7u)

/* ---------------------------------------------------------------------------------------------------------------------
 * Construction
 * --------------------------------------------------------------------------------------------------------------------- */
FIRE_FN Value fire_IntU(int64_t v, uint32_t unit) { Value r; r.kind = K_Int; r.width = 0; r.reserved = 0; r.unit = unit; r.i = v; return r; }
FIRE_FN Value fire_Int1(int64_t v) { return fire_IntU(v, 0); }
FIRE_FN Value fire_FloatU(Real v, uint32_t unit) { Value r; r.kind = K_Float; r.width = 0; r.reserved = 0; r.unit = unit; r.f = v; return r; }
FIRE_FN Value fire_Float1(Real v) { return fire_FloatU(v, 0); }
FIRE_FN Value Bool(bool v) { Value r; r.kind = K_Bool; r.width = 0; r.reserved = 0; r.unit = 0; r.i = v ? 1 : 0; return r; }
FIRE_FN Value Char(uint32_t c) { Value r; r.kind = K_Char; r.width = 0; r.reserved = 0; r.unit = 0; r.i = c; return r; }
FIRE_FN Value UndefU(uint32_t unit) { Value r; r.kind = K_Undefined; r.width = 0; r.reserved = 0; r.unit = unit; r.i = 0; return r; }
FIRE_FN Value Undef(void) { return UndefU(0); }
#define Int(...) FIRE_EXPAND(FIRE_PICK2(__VA_ARGS__, fire_IntU, fire_Int1, 0)(__VA_ARGS__))
#define Float(...) FIRE_EXPAND(FIRE_PICK2(__VA_ARGS__, fire_FloatU, fire_Float1, 0)(__VA_ARGS__))

/* ---------------------------------------------------------------------------------------------------------------------
 * The state of an exception that is unwinding the stack (see the exceptions section): every generated function checks the flag after a call that can throw
 * and, if it is set, leaves its scopes and returns.
 * --------------------------------------------------------------------------------------------------------------------- */
enum { UW_NONE, UW_JUMP, UW_RETURN, UW_RETHROW, UW_RESUME, UW_LEAVE };
typedef struct Handler Handler;
typedef struct UnwindState {
    uint8_t active;
    uint8_t kind;
    Handler* target;      /* the frame that is being unwound to */
    Value value;          /* the exception, the return value or the resume value */
    uint32_t label;       /* JUMP: the bytecode address to continue at */
    uint64_t token;       /* RESUME: which throw is resumed */
    Value regs[4];        /* JUMP: operand stack slots that the jump takes along (the completion of a finally block) */
} UnwindState;

/* The temporary pool: every entry holds one count of a reference value; a scope releases the entries above its mark when it ends. (One pool per thread of
 * execution.) */
typedef struct Ref Ref;
typedef struct Pool {
    Ref** items;
    uint32_t top;
    uint32_t cap;
} Pool;

/* ---- The state of one thread of execution. One list, two ways to hold it: thread-local variables (hosts), or - FIRE_TLS_STRUCT, for platforms whose
 * compiler has no thread-local storage, like FreeRTOS tasks - the members of one structure that the platform hangs on the task (`fire_tls_get/set`) and names
 * that expand to `fire_tl()->name`. Without threads they are plain globals. */
typedef struct OwnList OwnList;
typedef struct Pending Pending;
typedef struct SectionReq SectionReq;
/* The last error of a bridge function (IO, devices): a code and a message, one per thread (the prelude asks for it right after the call). */
typedef struct BridgeError { int32_t code; char message[176]; } BridgeError;
typedef struct Owned Owned;
/* The objects that are destroyed while a destroy batch runs (see `destroy`): their memory is freed when the batch ends, so that a destructor can still look at an
 * object that was destroyed before it (the order in a scope is the order of creation: a writer's destructor flushes a stream that is already destroyed). */
typedef struct Zombies { Owned* head; int32_t depth; } Zombies;
#ifdef __cplusplus
#define FIRE_ZERO_INIT {}
#else
#define FIRE_ZERO_INIT {0}
#endif
#define FIRE_THREAD_VARS(X) \
    X(UnwindState, g_unwind, FIRE_ZERO_INIT) \
    X(Pool, g_pool, FIRE_ZERO_INIT) \
    X(Handler*, g_handlers, NULL) \
    X(OwnList*, g_globalOwn, NULL)   /* the global scope: thrown exceptions belong to it (SPEC 7.6) */ \
    X(Pending*, g_pending, NULL) \
    X(uint64_t, g_throwToken, 0) \
    X(uint8_t, g_isThread, 0)           /* 1 on a fire thread */ \
    X(int32_t, g_jobDepth, 0)           /* a `fire global` job is running on this thread */ \
    X(uint32_t, g_attnMask, 7u)         /* the signals this thread looks at (ATTN_ALL|ATTN_MAIN|ATTN_SECT); fire threads clear all but ATTN_ALL */ \
    X(int32_t, g_tick, 4096) \
    X(uint8_t, g_leaving, 0)            /* unwinding because of leave/terminate (or an unhandled exception): no more signals */ \
    X(uint8_t, g_threadFailed, 0)       /* the thread ends with an unhandled exception: its global scope stays */ \
    X(OwnList*, g_travel, NULL) \
    X(SectionReq*, g_curSection, NULL) \
    X(int32_t, g_secDepth, 0) \
    X(OwnList* const*, g_scopeLists, NULL)   /* a call of an ownership method with a `Takes` mode through the dispatcher: the scopes of the call (see ownMethodT) */ \
    X(int32_t, g_scopeCount, 0) \
    X(uint32_t, g_callDepth, 0)         /* the number of fire functions that are running on this thread (only kept when the program uses `try x.Take...`) */ \
    X(uint32_t, g_reflCaller, 0xFFFFFFFFu)   /* the class of the code that called into the reflection library */ \
    X(const void*, g_callTArgs, NULL)       /* the type arguments for the next call of a generic method (TArgs) */ \
    X(int32_t, g_explicitTArgs, 0)           /* the call names its type arguments (`M<string>(x)`): their number - only generic overloads with that many count */ \
    X(Zombies, g_zombies, FIRE_ZERO_INIT)          /* destroyed objects whose memory is freed at the end of the batch */ \
    X(BridgeError, g_ioError, FIRE_ZERO_INIT)      /* the IO bridge: the last error of this thread */ \
    X(BridgeError, g_deviceError, FIRE_ZERO_INIT)  /* the device bridge */ \
    X(BridgeError, g_gfxError, FIRE_ZERO_INIT)     /* the graphics bridge: the message of the last failed image function */

#if defined(FIRE_THREADS) && defined(FIRE_TLS_STRUCT)
typedef struct ThreadLocals {
#define FIRE_X(type, name, init) type name;
    FIRE_THREAD_VARS(FIRE_X)
#undef FIRE_X
} ThreadLocals;
/* The structure of the calling task (made when the task first asks). */
FIRE_FN ThreadLocals* fire_tl(void) {
    void* p = fire_tls_get();
    if (FIRE_UNLIKELY(!p)) {
        ThreadLocals* t = (ThreadLocals*)calloc(1, sizeof(ThreadLocals));
        if (!t) { fputs("fire runtime error: Out of memory.\n", stderr); abort(); }
        t->g_attnMask = 7u;
        t->g_tick = 4096;
        t->g_reflCaller = 0xFFFFFFFFu;
        fire_tls_set(t);
        p = t;
    }
    return (ThreadLocals*)p;
}
#define g_unwind (fire_tl()->g_unwind)
#define g_pool (fire_tl()->g_pool)
#define g_handlers (fire_tl()->g_handlers)
#define g_globalOwn (fire_tl()->g_globalOwn)
#define g_pending (fire_tl()->g_pending)
#define g_throwToken (fire_tl()->g_throwToken)
#define g_isThread (fire_tl()->g_isThread)
#define g_jobDepth (fire_tl()->g_jobDepth)
#define g_attnMask (fire_tl()->g_attnMask)
#define g_tick (fire_tl()->g_tick)
#define g_leaving (fire_tl()->g_leaving)
#define g_threadFailed (fire_tl()->g_threadFailed)
#define g_travel (fire_tl()->g_travel)
#define g_curSection (fire_tl()->g_curSection)
#define g_secDepth (fire_tl()->g_secDepth)
#define g_callDepth (fire_tl()->g_callDepth)
#define g_scopeLists (fire_tl()->g_scopeLists)
#define g_scopeCount (fire_tl()->g_scopeCount)
#define g_reflCaller (fire_tl()->g_reflCaller)
#define g_callTArgs (fire_tl()->g_callTArgs)
#define g_explicitTArgs (fire_tl()->g_explicitTArgs)
#define g_zombies (fire_tl()->g_zombies)
#define g_ioError (fire_tl()->g_ioError)
#define g_deviceError (fire_tl()->g_deviceError)
#define g_gfxError (fire_tl()->g_gfxError)
#else
#define FIRE_X(type, name, init) extern FIRE_TLS type name;
FIRE_THREAD_VARS(FIRE_X)
#undef FIRE_X
#endif

/* A run-time error that the language reports as an exception (see the exceptions section): the exception is thrown, or - when the program has no exceptions
 * at all - it ends the program. */
FIRE_FN Value indexError(const char* what, int64_t index, int64_t length);
/* The use of a destroyed object, array or buffer (SPEC 2.3, 2.5): a DestroyedException, or the end of the program when there are no exceptions. */
FIRE_FN Value destroyedError(Value leaf);
/* An integer division or modulo by zero: a DivideByZeroException, or the end of the program when there are no exceptions. */
FIRE_FN Value divideByZero(void);

#if defined(FIRE_LIBRARY) && defined(__cplusplus)
FIRE_END
/* The natives of a package built as a shared library for the virtual machine (native/abi/fire_pkg_wrapper.hpp): a run-time error does not end the process, it is
 * thrown (a C++ exception) to the wrapper of the entry point, which reports it to the VM. */
namespace fire {
struct FireFatalError { char text[256]; };
/* An exception of the program that a native of the package throws (`fireError("TimeException", "text")`): the wrapper of the entry point reports the class and
 * the text to the VM, which throws the exception there. */
struct FireClassError { char cls[96]; char text[256]; };
/* What the host of the VM decides (fire_pkg_abi.h: `fire_host`); null until the host has told the library. */
inline const fire_host*& libraryHost() { static const fire_host* host = nullptr; return host; }
}
FIRE_BEGIN
FIRE_NORETURN FIRE_FN void fatal(const char* message) {
    FireFatalError e;
    snprintf(e.text, sizeof e.text, "%s", message);
    throw e;
}
FIRE_NORETURN FIRE_FN void unsupported(const char* what) {
    FireFatalError e;
    snprintf(e.text, sizeof e.text, "'%s' is not supported in a package library", what);
    throw e;
}
FIRE_NORETURN FIRE_FN Value fireError(const char* cls, const char* utf8) {
    FireClassError e;
    snprintf(e.cls, sizeof e.cls, "%s", cls);
    snprintf(e.text, sizeof e.text, "%s", utf8);
    throw e;
}
#else
FIRE_NORETURN FIRE_COLD FIRE_FN void fatal(const char* message) {
    fflush(stdout);
    fprintf(stderr, "fire runtime error: %s\n", message);
    exitNow(1);
}

FIRE_NORETURN FIRE_COLD FIRE_FN void unsupported(const char* what) {
    fflush(stdout);
    fprintf(stderr, "fire runtime error: '%s' is not supported by the native backend yet\n", what);
    exitNow(1);
}
/* An exception of the program that a native of a package throws (`fireError("TimeException", "text")`): thrown, or - when the program has no exceptions - the
 * end of the program (defined with the exceptions below). */
FIRE_FN Value fireError(const char* cls, const char* utf8);
#endif

FIRE_FN bool isNumeric(Value v) { return v.kind == K_Int || v.kind == K_Float; }
FIRE_FN Real toR(Value v) { return v.kind == K_Float ? v.f : (Real)v.i; }

/* The fewest digits (`%.*e`) that read back as the same number: `digits` is d.ddde+XX. `single`: the number is a float32. */
FIRE_FN void fire_shortestE(double v, bool single, char* sci, size_t cap) {
    int lo = 0, hi = single ? 8 : 16;
    /* (more digits never read back worse: the smallest precision that works is found by bisection) */
    while (lo < hi) {
        int mid = (lo + hi) / 2;
        snprintf(sci, cap, "%.*e", mid, v);
        double back = strtod(sci, NULL);
        if (single ? (float)back == (float)v : back == v) hi = mid; else lo = mid + 1;
    }
    snprintf(sci, cap, "%.*e", lo, v);
}

/* Real -> text like .NET Core 3.0+ `double.ToString()` / `float.ToString()`: shortest round-trip digits, positional notation for 1E-4 <= |x| < 1E17 (double)
 * or 1E9 (float), otherwise `d.dddE+XX`. */
FIRE_FN int formatFloating(double v, bool single, char* out) {
    char sci[40];
    char digits[24] = {0};
    int nd = 0, n = 0, exp10;
    bool negative = false;
    const char* c = sci;
    if (isnan(v)) { memcpy(out, "NaN", 3); return 3; }
    if (isinf(v)) { if (v < 0) { memcpy(out, "-Infinity", 9); return 9; } memcpy(out, "Infinity", 8); return 8; }
    if (v == 0) { if (signbit(v)) { out[0] = '-'; out[1] = '0'; return 2; } out[0] = '0'; return 1; }
    fire_shortestE(v, single, sci, sizeof sci);
    /* sci = [-]d[.ddd]e[+-]XX */
    if (*c == '-') { negative = true; c++; }
    for (; *c && *c != 'e'; c++) if (*c != '.') digits[nd++] = *c;
    exp10 = atoi(c + 1);
    if (negative) out[n++] = '-';
    if (exp10 >= (single ? 9 : 17) || exp10 < -4) {
        int e = exp10 < 0 ? -exp10 : exp10;
        out[n++] = digits[0];
        if (nd > 1) { int k; out[n++] = '.'; for (k = 1; k < nd; k++) out[n++] = digits[k]; }
        out[n++] = 'E'; out[n++] = exp10 < 0 ? '-' : '+';
        if (e < 10) out[n++] = '0';
        n += snprintf(out + n, 8, "%d", e);
        return n;
    }
    if (exp10 >= 0) {
        int k;
        for (k = 0; k <= exp10; k++) out[n++] = k < nd ? digits[k] : '0';
        if (nd > exp10 + 1) { out[n++] = '.'; for (k = exp10 + 1; k < nd; k++) out[n++] = digits[k]; }
    } else {
        int k;
        out[n++] = '0'; out[n++] = '.';
        for (k = 0; k < -exp10 - 1; k++) out[n++] = '0';
        for (k = 0; k < nd; k++) out[n++] = digits[k];
    }
    return n;
}
FIRE_FN int formatReal(Real v, char* out) { return formatFloating((double)v, sizeof(Real) == 4, out); }

/* ---------------------------------------------------------------------------------------------------------------------
 * Units (SPEC 3): a unit is a vector of exponents over the base symbols of the program (`g_dimNames`: m, s, apples, ...) and a scale factor to the base.
 * `Value.unit` is the index into a table that grows at run time - `mm * mm` or `m / s` make new entries - and is interned (same dimensions, scale and display
 * text = same index). Two indices can still mean equal units (`unitEq`).
 * --------------------------------------------------------------------------------------------------------------------- */
#ifndef FIRE_NDIMS
#define FIRE_NDIMS 1
#endif
/* The base symbols, in the order of the dimension vectors (sorted like the VM prints them). Defined by the generated code. */
extern const char* const g_dimNames[];

typedef struct UnitInit { int32_t dims[FIRE_NDIMS]; double scale; const char* display; double offset; int32_t conv; int32_t ulconv; } UnitInit;
typedef struct UnitDef {
    int32_t dims[FIRE_NDIMS];
    double scale;
    const char* display;   /* the symbol of a named unit (`mm`), null for a derived one */
    const char* name;      /* Unit.ToString() */
    double offset;         /* Unit.Offset: value_in_base = value * scale + offset (`unit Celsius: C { coerce K => value + 273.15 }`) */
    int32_t conv;          /* Unit.Converter: the index of the conversion functions of the unit (g_unitTo/g_unitFrom), -1 for none */
    int32_t ulconv;        /* Unit.UnitlessConverter: the index of the functions that make a plain number of a value of the unit (dB), -1 for none */
} UnitDef;
/* The conversion functions of the units that have them (the static methods To and From of their hidden classes, generated). */
typedef Value (*UnitConvFn)(Value);
extern const UnitConvFn* g_unitTo;
extern const UnitConvFn* g_unitFrom;
extern UnitDef* g_ud;
extern uint32_t g_udCount, g_udCap;

FIRE_FN bool unitDimsZero(const int32_t* dims) {
    int i;
    for (i = 0; i < FIRE_NDIMS; i++) if (dims[i]) return false;
    return true;
}
/* Unit.IsUnitless: no dimensions (whatever the scale). */
FIRE_FN bool unitIsUnitless(uint32_t u) { return unitDimsZero(g_ud[u].dims); }
/* The text printed after a number: nothing for a unit without dimensions. */
FIRE_FN const char* unitSuffix(uint32_t u) { return unitIsUnitless(u) ? "" : g_ud[u].name; }

FIRE_NORETURN FIRE_COLD FIRE_FN void outOfMemory(void) { fatal("Out of memory."); }

typedef struct fire_TextBuf { char buf[384]; size_t n; } fire_TextBuf;
FIRE_FN void fire_put(fire_TextBuf* t, const char* text) { for (; *text && t->n + 1 < sizeof t->buf; text++) t->buf[t->n++] = *text; }

/* Unit.ToString(): `m/s^2`, `(m*s)`, a symbol if the unit has one, `(×scale)` when the scale is not 1. */
FIRE_FN char* unitBuildName(const int32_t* dims, double scale, const char* display) {
    fire_TextBuf t;
    char* out;
    t.n = 0;
    if (unitDimsZero(dims)) fire_put(&t, "unitless");
    else if (display) fire_put(&t, display);
    else {
        int numerators = 0, denominators = 0, i;
        char tmp[16];
        for (i = 0; i < FIRE_NDIMS; i++) {
            if (dims[i] > 0) { if (numerators++) fire_put(&t, "*"); fire_put(&t, g_dimNames[i]); if (dims[i] != 1) { snprintf(tmp, sizeof tmp, "^%d", (int)dims[i]); fire_put(&t, tmp); } }
        }
        if (!numerators) fire_put(&t, "1");
        for (i = 0; i < FIRE_NDIMS; i++) if (dims[i] < 0) denominators++;
        if (denominators) {
            int k = 0;
            fire_put(&t, "/");
            if (denominators > 1) fire_put(&t, "(");
            for (i = 0; i < FIRE_NDIMS; i++)
                if (dims[i] < 0) { if (k++) fire_put(&t, "*"); fire_put(&t, g_dimNames[i]); if (dims[i] != -1) { snprintf(tmp, sizeof tmp, "^%d", (int)-dims[i]); fire_put(&t, tmp); } }
            if (denominators > 1) fire_put(&t, ")");
        }
        if (fabs(scale - 1.0) > 1e-12) {
            char num[48];
            int len;
            fire_put(&t, "(\xC3\x97");
            len = formatFloating(scale, false, num);
            num[len] = 0;
            fire_put(&t, num);
            fire_put(&t, ")");
        }
    }
    out = (char*)malloc(t.n + 1);
    if (!out) outOfMemory();
    memcpy(out, t.buf, t.n);
    out[t.n] = 0;
    return out;
}

FIRE_FN void unitsInit(const UnitInit* init, uint32_t count, const UnitConvFn* to, const UnitConvFn* from) {
    uint32_t i;
    g_unitTo = to;
    g_unitFrom = from;
    g_udCap = count + 32;
    g_ud = (UnitDef*)malloc(sizeof(UnitDef) * g_udCap);
    if (!g_ud) outOfMemory();
    for (i = 0; i < count; i++) {
        memcpy(g_ud[i].dims, init[i].dims, sizeof g_ud[i].dims);
        g_ud[i].scale = init[i].scale;
        g_ud[i].display = init[i].display;
        g_ud[i].name = unitBuildName(init[i].dims, init[i].scale, init[i].display);
        g_ud[i].offset = init[i].offset;
        g_ud[i].conv = init[i].conv;
        g_ud[i].ulconv = init[i].ulconv;
    }
    g_udCount = count;
}

/* The index of the unit with these dimensions, scale and display text (made when it is new). */
FIRE_FN uint32_t unitIntern(const int32_t* dims, double scale, const char* display) {
    uint32_t i;
    UnitDef* d;
    for (i = 0; i < g_udCount; i++) {
        const UnitDef* e = &g_ud[i];
        if (e->scale == scale && e->offset == 0 && e->conv < 0 && e->ulconv < 0 && memcmp(e->dims, dims, sizeof e->dims) == 0 && ((e->display == NULL) == (display == NULL)) && (!display || strcmp(e->display, display) == 0)) return i;
    }
    if (g_udCount == g_udCap) {
        g_udCap *= 2;
        g_ud = (UnitDef*)realloc(g_ud, sizeof(UnitDef) * g_udCap);
        if (!g_ud) outOfMemory();
    }
    d = &g_ud[g_udCount];
    memcpy(d->dims, dims, sizeof d->dims);
    d->scale = scale;
    d->display = display;
    d->name = unitBuildName(dims, scale, display);
    d->offset = 0;
    d->conv = -1;
    d->ulconv = -1;
    return g_udCount++;
}

FIRE_FN bool unitCompatible(uint32_t a, uint32_t b) { return a == b || memcmp(g_ud[a].dims, g_ud[b].dims, sizeof g_ud[a].dims) == 0; }
/* Unit.Equals: same dimensions and the scales differ by less than 1e-12. */
FIRE_FN bool unitEq(uint32_t a, uint32_t b) {
    return a == b || (unitCompatible(a, b) && fabs(g_ud[a].scale - g_ud[b].scale) < 1e-12 && g_ud[a].offset == g_ud[b].offset && g_ud[a].conv == g_ud[b].conv && g_ud[a].ulconv == g_ud[b].ulconv);
}
/* Unit.IsLinear: only a factor of its base unit (no offset, no conversion functions). */
FIRE_FN bool unitLinear(uint32_t u) { return g_ud[u].offset == 0 && g_ud[u].conv < 0; }

#ifdef FIRE_EXCEPTIONS
FIRE_FN void unitConflictThrow(uint32_t from, uint32_t to);
#endif
/* Two values of different dimensions in one operation (`1m + 1s`): the UnitMismatchException of the VM - thrown (the caller returns a value that tells the
 * generated code to look at the unwinding flag), in a package library reported to the VM, without exceptions the end of the program. */
FIRE_COLD FIRE_FN void unitConflict(uint32_t from, uint32_t to) {
#ifdef FIRE_EXCEPTIONS
    unitConflictThrow(from, to);
#elif defined(FIRE_LIBRARY)
    char text[200];
    snprintf(text, sizeof text, "Incompatible units: '%s' cannot be converted to '%s'.", g_ud[from].name, g_ud[to].name);
    fireError("UnitMismatchException", text);
#else
    fflush(stdout);
    fprintf(stderr, "Unhandled exception. fire.Values.UnitMismatchException: Incompatible units: '%s' cannot be converted to '%s'.\n", g_ud[from].name, g_ud[to].name);
    exitNow(1);
#endif
}
/* Unit.ConversionFactorTo
 * The factor between two units without the rounding error of the scales, like the VM (Unit.NiceFactor): `0.3048 / 0.0254` is 12, not 12.000000000000002; a
 * factor that is not a few units in the last place away from a number with 12 significant digits stays as it is. */
FIRE_FN double unitNiceFactor(double factor) {
    char text[32];
    double nice;
    snprintf(text, sizeof text, "%.12g", factor);
    nice = strtod(text, NULL);
    return fabs(nice - factor) <= 8 * 2.220446049250313e-16 * fabs(factor) ? nice : factor;
}
typedef struct fire_FactorEntry { uint32_t from, to; double factor; } fire_FactorEntry;
FIRE_FN double unitFactor(uint32_t from, uint32_t to) {
    static FIRE_TLS fire_FactorEntry cache[64];
    fire_FactorEntry* e;
    double factor;
    if (from == to) return 1.0;
    if (!unitCompatible(from, to)) { unitConflict(from, to); return 1.0; }
    e = &cache[(from * 31u + to) & 63u];
    if (e->from == from && e->to == to && e->factor != 0) return e->factor;
    factor = unitNiceFactor(g_ud[from].scale / g_ud[to].scale);
    e->from = from; e->to = to; e->factor = factor;
    return factor;
}

/* unitFactor between two units the generator knows (their ids are fixed): looked up once (`cache` is a static of the generated code, 0 until then). */
FIRE_FN double unitFactorCached(double* cache, uint32_t from, uint32_t to) {
    if (FIRE_LIKELY(*cache != 0)) return *cache;
    *cache = unitFactor(from, to);
    return *cache;
}

/* Unit.Multiply / Unit.Divide: a unit without dimensions leaves the other one as it is; `mm * mm` is `mm^2`. */
FIRE_FN uint32_t unitMul(uint32_t a, uint32_t b) {
    const UnitDef* x;
    const UnitDef* y;
    int32_t dims[FIRE_NDIMS];
    const char* display = NULL;
    double scale;
    uint32_t id;
    int i;
    if (unitIsUnitless(b)) return a;
    if (unitIsUnitless(a)) return b;
    x = &g_ud[a];
    y = &g_ud[b];
    for (i = 0; i < FIRE_NDIMS; i++) dims[i] = x->dims[i] + y->dims[i];
    /* (a unit with an offset or conversion functions counts as its reference unit: Unit.LinearPart, no `C^2`) */
    if (x->display && y->display && unitLinear(a) && unitLinear(b) && strcmp(x->display, y->display) == 0) {
        size_t l = strlen(x->display);
        char* square = (char*)malloc(l + 3);
        if (!square) outOfMemory();
        memcpy(square, x->display, l);
        memcpy(square + l, "^2", 3);
        display = square;
    }
    scale = x->scale * y->scale;
    id = unitIntern(dims, scale, display);
    if (display && g_ud[id].display != display) free((char*)display);   /* an equal unit existed */
    return id;
}
FIRE_FN uint32_t unitDiv(uint32_t a, uint32_t b) {
    const UnitDef* x;
    const UnitDef* y;
    int32_t dims[FIRE_NDIMS];
    int i;
    if (unitIsUnitless(b)) return a;
    x = &g_ud[a];
    y = &g_ud[b];
    for (i = 0; i < FIRE_NDIMS; i++) dims[i] = x->dims[i] - y->dims[i];
    return unitIntern(dims, x->scale / y->scale, NULL);
}


/* ---------------------------------------------------------------------------------------------------------------------
 * Heap values. Two kinds of lifetime:
 *  * strings (UTF-16, like the VM) and lambdas are values that are shared freely (`var t = s`, stored in fields and arrays, returned).
 *    They are *counted*:
 *      - a *storage location* (variable, parameter, field, array element, static) holds one count of what it contains;
 *      - a stack temporary holds nothing: a fresh value is pushed on the *temporary pool* (count 1) and the scope that created it releases everything above
 *        its mark when it is left (so a temporary lives until the end of its scope);
 *      - `return` retains the value for the trip and the caller adopts it onto its pool (see `adopt`).
 *    Constants live in static storage and are immortal (never counted).
 *  * objects, arrays and byte buffers have exactly one *owner* (SPEC 2): a scope or another object. They are destroyed with it and never counted (see the
 *    ownership section).
 * Every heap value starts with its header (`h`): a pointer to the value is a pointer to its header.
 * --------------------------------------------------------------------------------------------------------------------- */
#define IMMORTAL 0xFFFFFFFFu
enum { R_Str, R_Lam };

struct Ref {
    uint32_t rc;     /* number of holders; IMMORTAL for constants */
    uint8_t type;    /* R_Str / R_Lam */
};

/* Immutable UTF-16 string. A heap string stores its characters right behind the header. */
typedef struct Str {
    Ref h;
    uint32_t length;
    const fire_char16* data;
} Str;

/* A type argument (`int`, `Dog`, `in cm`) of an object (`new Box<int>()`) or of a call of a generic method: what a declaration with that type parameter takes
 * (generated once per text, see DeclaredType of the VM). A number type argument also has its kind and the conversion into its unit (`int`: into no unit,
 * `int : cm`: into cm; null for `int : any`). */
typedef struct TArg { const char* display; bool (*holds)(Value); bool isFloat; uint8_t kind; Value (*adapt)(Value); } TArg;
/* The type arguments of an object or a call, one per type parameter (null: not known); interned, never freed. */
typedef const TArg* const* TArgs;

/* A lambda value: the function, the values it captured when it was created (copies, SPEC 4.2.1) and the `on` target (`this`). */
typedef struct Lam {
    Ref h;
    uint32_t nparams;
    uint32_t ncaps;
    uint32_t nreq;     /* parameters without a default value (they come first) */
    Value on;
    Value (*fn)(Value lam, const Value* args);
    Value (*dflt)(Value lam, uint32_t index);   /* the default value of parameter `index` (null: the lambda has none) */
    TArgs targs;      /* the type arguments of the generic code it was made in that its body uses (null: none) */
#ifdef FIRE_REFLECTION
    const char* const* sel;   /* a selector lambda (`c => c.radius`): the member chain (SPEC 8.13), else null */
    uint32_t nsel;
#endif
#ifdef __cplusplus
    Value* caps() { return (Value*)((char*)this + FIRE_HDR(Lam)); }
#endif
} Lam;
FIRE_FN Value* lamCaps(Lam* l) { return (Value*)((char*)l + FIRE_HDR(Lam)); }

/* Owned values: the header they share. An owner is an OwnList (for a scope a local of the generated function, for an object the `owned` list in its header); it
 * holds its members in creation order, which is the order they are destroyed in. */
enum { O_Object, O_Array, O_Buffer };

struct Owned {
    Owned* prev;
    Owned* next;
    OwnList* owner;    /* the list this value is in; null while it travels as a return value to its new owner */
    uint32_t slot;     /* the slot in the handle table (see below) */
    uint16_t gen;      /* ... and its generation */
    uint8_t okind;     /* O_Object / O_Array / O_Buffer */
    uint8_t flags;     /* bit 0: destroyed (or being destroyed) */
};

struct OwnList {
    Owned* head;
    Owned* tail;
    uint32_t mark;     /* scopes: height of the temporary pool when the scope was entered */
    uint32_t depth;    /* argument lists (`mark == AL_MARK`): the call depth of the caller (see `try x.Take...`, `g_callDepth`) */
    OwnList* parent;   /* scopes: the enclosing scope (TakeUpwards; set only when the program uses it) */
    Owned* holder;     /* object lists: the object that owns the list (null for a scope) */
};
FIRE_FN void ownListInit(OwnList* l, uint32_t mark, Owned* holder) {
    l->head = l->tail = NULL;
    l->mark = mark;
    l->depth = 0;
    l->parent = NULL;
    l->holder = holder;
}

/* Fixed-size array of Values (elements start as `undefined`). It owns the inner arrays that were made together with it (`new int[3][4]`, `[[1, 2], [3]]`), not
 * the elements that are assigned later. */
typedef struct Arr {
    Owned h;
    uint32_t length;
    OwnList* parts;
#ifdef __cplusplus
    Value* items() { return (Value*)((char*)this + FIRE_HDR(Arr)); }
#endif
} Arr;
FIRE_FN Value* arrItems(Arr* a) { return (Value*)((char*)a + FIRE_HDR(Arr)); }

/* Byte buffer (`byte[]`). */
typedef struct Buf {
    Owned h;
    uint32_t length;
    uint8_t little;    /* the byte order the buffer is tagged with (metadata only: the bytes never change by themselves) */
    uint8_t* data;     /* the bytes: right behind the header - or, in a package library, the memory of the buffer of the VM (see allocBufExternal) */
#ifdef __cplusplus
    uint8_t* bytes() { return data; }
#endif
} Buf;
FIRE_FN uint8_t* bufBytes(Buf* b) { return b->data; }

/* ---- Handles. A destroyed array or buffer must not be used any more (SPEC 2.5): in the checked modes a Value of an array or buffer carries the slot of its
 * header in a table of generations (`unit` = slot, `reserved` = generation); the slot of a destroyed value gets a new generation, so a stale Value is
 * recognized before the freed memory is touched. With FIRE_UNCHECKED (`#performance`) there is no table and nothing is checked. */
#ifndef FIRE_UNCHECKED
typedef struct SlotTable {
    uint16_t* gen;
    uint32_t* freed;
    uint32_t count, cap, nfree, freedCap;
#ifdef FIRE_PTRBOUNDS
    Owned** hdr;   /* the header of the array or buffer in each slot: the pointers into arrays and buffers (`p + n`) find the bounds here */
#endif
} SlotTable;
extern SlotTable g_slots;

FIRE_COLD FIRE_FN uint32_t slotGrow(void) {
    uint32_t cap = g_slots.cap ? g_slots.cap * 2 : 256;
    uint16_t* gen = (uint16_t*)realloc(g_slots.gen, cap * sizeof(uint16_t));
    if (!gen) outOfMemory();
    memset(gen + g_slots.cap, 0, (cap - g_slots.cap) * sizeof(uint16_t));
    g_slots.gen = gen;
#ifdef FIRE_PTRBOUNDS
    {
        Owned** hdr = (Owned**)realloc(g_slots.hdr, cap * sizeof(Owned*));
        if (!hdr) outOfMemory();
        g_slots.hdr = hdr;
    }
#endif
    g_slots.cap = cap;
    return cap;
}
FIRE_FN void slotAcquire(Owned* o) {
    uint32_t slot;
    if (g_slots.nfree) slot = g_slots.freed[--g_slots.nfree];
    else {
        if (FIRE_UNLIKELY(g_slots.count == g_slots.cap)) slotGrow();
        slot = g_slots.count++;
    }
    o->slot = slot;
    o->gen = g_slots.gen[slot];
#ifdef FIRE_PTRBOUNDS
    g_slots.hdr[slot] = o;
#endif
}
FIRE_FN void slotRelease(Owned* o) {
    uint32_t slot = o->slot;
    g_slots.gen[slot] = (uint16_t)(g_slots.gen[slot] + 1);
    if (g_slots.nfree == g_slots.freedCap) {
        uint32_t cap = g_slots.freedCap ? g_slots.freedCap * 2 : 256;
        uint32_t* freed = (uint32_t*)realloc(g_slots.freed, cap * sizeof(uint32_t));
        if (!freed) outOfMemory();
        g_slots.freed = freed;
        g_slots.freedCap = cap;
    }
    g_slots.freed[g_slots.nfree++] = slot;
}
#else
FIRE_FN void slotAcquire(Owned* o) { o->slot = 0; o->gen = 0; }
FIRE_FN void slotRelease(Owned* o) { (void)o; }
#endif

/* Is the array or buffer behind this value still alive? */
FIRE_FN bool leafAlive(Value v) {
#ifdef FIRE_UNCHECKED
    (void)v;
    return true;
#else
    return FIRE_LIKELY(g_slots.gen[v.unit] == v.reserved);
#endif
}

FIRE_FN Value StrV(const Str* s) { Value r; r.kind = K_String; r.width = 0; r.reserved = 0; r.unit = 0; r.p = s; return r; }
FIRE_FN Value ArrV(Arr* a) { Value r; r.kind = K_Array; r.width = 0; r.reserved = a->h.gen; r.unit = a->h.slot; r.p = a; return r; }
FIRE_FN Value BufV(Buf* b) { Value r; r.kind = K_Buffer; r.width = 0; r.reserved = b->h.gen; r.unit = b->h.slot; r.p = b; return r; }

FIRE_FN bool isRef(Value v) { return ((0x50u >> v.kind) & 1u) != 0; }  /* String, Lambda (counted values) */
FIRE_FN Ref* refOf(Value v) { return (Ref*)v.p; }
FIRE_FN const Str* strOf(Value v) { return (const Str*)v.p; }
FIRE_FN Arr* arrOf(Value v) { return (Arr*)v.p; }
FIRE_FN Buf* bufOf(Value v) { return (Buf*)v.p; }
FIRE_FN Lam* lamOf(Value v) { return (Lam*)v.p; }
FIRE_FN Value LamV(Lam* l) { Value r; r.kind = K_Lambda; r.width = 0; r.reserved = 0; r.unit = 0; r.p = l; return r; }

FIRE_FN void freeRef(Ref* r);

FIRE_FN void retain(Value v) {
    if (isRef(v)) {
        Ref* r = refOf(v);
        if (r->rc != IMMORTAL) r->rc++;
    }
}
FIRE_FN void release(Value v) {
    if (isRef(v)) {
        Ref* r = refOf(v);
        if (r->rc != IMMORTAL && --r->rc == 0) freeRef(r);
    }
}

FIRE_COLD FIRE_FN void poolGrow(void) {
    uint32_t cap = g_pool.cap ? g_pool.cap * 2 : 256;
    Ref** items = (Ref**)realloc(g_pool.items, cap * sizeof(Ref*));
    if (!items) fatal("Out of memory.");
    g_pool.items = items;
    g_pool.cap = cap;
}
FIRE_FN void poolPush(Ref* r) {
    if (FIRE_UNLIKELY(g_pool.top == g_pool.cap)) poolGrow();
    g_pool.items[g_pool.top++] = r;
}
FIRE_FN uint32_t poolMark(void) { return g_pool.top; }
FIRE_FN void poolRelease(uint32_t mark) {
    while (g_pool.top > mark) {
        Ref* r = g_pool.items[--g_pool.top];
        if (--r->rc == 0) freeRef(r);
    }
}

/* ---------------------------------------------------------------------------------------------------------------------
 * Arithmetic. Fast path: both numeric with the same unit. Different units (conversion, unit algebra: SPEC 3) go through the slow paths.
 * --------------------------------------------------------------------------------------------------------------------- */
/* NOTE: every function takes its Values BY VALUE, never through a pointer (and the slow paths only the operand kinds). A pointer to a local that reaches a call
 * that is not inlined makes the local "escape", and the compiler then keeps it - and, since the generated code reuses its stack temporaries s0..sN, all of
 * them - in memory for the whole function instead of in registers. That alone made generated loops ~10x slower. */
FIRE_FN const char* coerceKindName(uint8_t kind);
/* An operand of an arithmetic operator that is no number (`undefined * 2`, `-"a"`): the ArgumentException of the VM (Value.RequireNumeric) - the first operand
 * that is no number is named. */
FIRE_COLD FIRE_FN Value opFailed(Value a, Value b) {
    char text[80];
    snprintf(text, sizeof text, "Type %s is not numeric.", coerceKindName(!(a.kind == K_Int || a.kind == K_Float) ? a.kind : b.kind));
    fireError("ArgumentException", text);
    return Undef();   /* (the generated code tests for undefined, then for the unwinding flag) */
}
/* An operand of `& | # << >> ~` that is no int: `'&' expects int, not Float.` */
FIRE_COLD FIRE_FN Value intOpFailed(const char* op, Value a, Value b) {
    char text[80];
    snprintf(text, sizeof text, "'%s' expects int, not %s.", op, coerceKindName(a.kind != K_Int ? a.kind : b.kind));
    fireError("ArgumentException", text);
    return Undef();
}

typedef struct VPair { Value a, b; } VPair;
FIRE_FN VPair vpair(Value a, Value b) { VPair p; p.a = a; p.b = b; return p; }

/* Calls a conversion function of a unit (`To`: into the reference unit, `From`: back); false when it threw (the unwinding flag is set) or gave no number. */
FIRE_COLD FIRE_FN bool unitCallConv(uint32_t u, bool to, double v, double* out, bool plain) {
    Value r = (to ? g_unitTo : g_unitFrom)[plain ? g_ud[u].ulconv : g_ud[u].conv](Float((Real)v));
    char text[160];
    if (g_unwind.active) return false;
    if (r.kind == K_Int) { *out = (double)r.i; return true; }
    if (r.kind == K_Float) { *out = (double)r.f; return true; }
    snprintf(text, sizeof text, "The conversion of '%s' must give a number, not a value of type '%s'.", g_ud[u].name, coerceKindName(r.kind));
    fireError("ArgumentException", text);
    return false;
}
/* Unit.Convert: a number in `from` converted into `to` (the same dimension) - by the factor for units that are only one, else through the reference units:
 * to(value) * scale + offset is the base value, and back (the factor and the shift without rounding error, a result next to 0 is 0). False: a conversion
 * function threw. */
typedef struct fire_ConvEntry { uint32_t from, to; double factor, shift; } fire_ConvEntry;
FIRE_COLD FIRE_FN bool unitConvert(double v, uint32_t from, uint32_t to, double* out) {
    static FIRE_TLS fire_ConvEntry cache[16];
    fire_ConvEntry* e;
    double x, scaled, y;
    if (from == to) { *out = v; return true; }
    if (unitLinear(from) && unitLinear(to)) { *out = v * unitFactor(from, to); return true; }
    if (!unitCompatible(from, to)) { unitConflict(from, to); return false; }
    e = &cache[(from * 31u + to) & 15u];
    if (!(e->from == from && e->to == to && e->factor != 0)) {
        double shift = (g_ud[from].offset - g_ud[to].offset) / g_ud[to].scale;
        e->from = from; e->to = to;
        e->factor = unitNiceFactor(g_ud[from].scale / g_ud[to].scale);
        e->shift = shift == 0 ? 0 : unitNiceFactor(shift);
    }
    x = v;
    if (g_ud[from].conv >= 0 && !unitCallConv(from, true, v, &x, false)) return false;
    scaled = x * e->factor;
    y = scaled + e->shift;
    if (fabs(y) <= 1e-12 * fmax(fabs(scaled), fabs(e->shift))) y = 0;   /* (32F is 0C, not 5.7E-14C) */
    if (g_ud[to].conv >= 0) return unitCallConv(to, false, y, out, false);
    *out = y;
    return true;
}
/* Unit.ToBase: the number in the base unit (`20C` is 293.15) - for `x:unitless`. */
FIRE_COLD FIRE_FN bool unitToBase(double v, uint32_t u, double* out) {
    double x = v;
    if (g_ud[u].ulconv >= 0) return unitCallConv(u, true, v, out, true);   /* a unit that becomes a plain number (dB) */
    if (g_ud[u].conv >= 0 && !unitCallConv(u, true, v, &x, false)) return false;
    *out = x * g_ud[u].scale + g_ud[u].offset;
    return true;
}

/* (a value a rounding error away from a whole number is that number: 1ft is 11.999999999999998in by the factors - Value.SnapWhole) */
FIRE_FN double fire_snapWhole(double x) { double r = round(x); return fabs(x - r) <= 1e-9 * fmax(1.0, fabs(x)) ? r : trunc(x); }

/* Value.AlignUnits: operands of the same dimension but a different scale (`500mm + 2m`) are converted implicitly - two ints stay ints (the finer unit is the
 * target while the converted value does not overflow, else the coarser one and the fraction is cut off), a float operand makes the right one a float in the
 * unit of the left. */
FIRE_COLD FIRE_FN VPair alignUnits(Value a, Value b) {
    uint32_t ua = a.unit, ub = b.unit;
    double factor;
    if (unitEq(ua, ub)) return vpair(a, b);
    if (!unitCompatible(ua, ub)) { unitConflict(ua, ub); return vpair(Undef(), Undef()); }   /* (thrown: the callers give undefined) */
    if (!unitLinear(ua) || !unitLinear(ub)) {
        /* an offset or conversion functions: the right operand goes into the unit of the left one (`20C + 9F`) */
        double converted;
        if (!unitConvert(b.kind == K_Int ? (double)b.i : (double)b.f, ub, ua, &converted)) return vpair(Undef(), Undef());
        b = a.kind == K_Int && b.kind == K_Int ? Int((int64_t)nearbyint(converted), ua) : Float((Real)converted, ua);
        return vpair(a, b);
    }
    if (a.kind == K_Int && b.kind == K_Int) {
        bool aIsFine = g_ud[ua].scale <= g_ud[ub].scale;
        Value* fine = aIsFine ? &a : &b;
        Value* coarse = aIsFine ? &b : &a;
        uint32_t uFine = aIsFine ? ua : ub, uCoarse = aIsFine ? ub : ua;
        double up = (double)coarse->i * unitFactor(uCoarse, uFine);
        if (up >= -9.2e18 && up <= 9.2e18) *coarse = Int((int64_t)fire_snapWhole(up), uFine);
        else *fine = Int((int64_t)fire_snapWhole((double)fine->i * unitFactor(uFine, uCoarse)), uCoarse);
        return vpair(a, b);
    }
    factor = unitFactor(ub, ua);
    b = Float((Real)((b.kind == K_Int ? (double)b.i : (double)b.f) * factor), ua);
    return vpair(a, b);
}

FIRE_COLD FIRE_FN Value addUnits(Value a, Value b) {
    VPair p = alignUnits(a, b);
    if (FIRE_UNLIKELY(p.a.kind == K_Undefined)) return Undef();
    if (p.a.kind == K_Int && p.b.kind == K_Int) return Int((int64_t)((uint64_t)p.a.i + (uint64_t)p.b.i), p.a.unit);
    return Float(toR(p.a) + toR(p.b), p.a.unit);
}
FIRE_COLD FIRE_FN Value subUnits(Value a, Value b) {
    VPair p = alignUnits(a, b);
    if (FIRE_UNLIKELY(p.a.kind == K_Undefined)) return Undef();
    if (p.a.kind == K_Int && p.b.kind == K_Int) return Int((int64_t)((uint64_t)p.a.i - (uint64_t)p.b.i), p.a.unit);
    return Float(toR(p.a) - toR(p.b), p.a.unit);
}
FIRE_COLD FIRE_FN Value modUnits(Value a, Value b) {
    VPair p = alignUnits(a, b);
    if (FIRE_UNLIKELY(p.a.kind == K_Undefined)) return Undef();
    if (p.a.kind == K_Int && p.b.kind == K_Int) {
        if (FIRE_UNLIKELY(p.b.i == 0)) return divideByZero();
        if (FIRE_UNLIKELY(p.b.i == -1)) return Int(0, p.a.unit);
        return Int(p.a.i % p.b.i, p.a.unit);
    }
    return Float(fmod(toR(p.a), toR(p.b)), p.a.unit);
}
/* A product or quotient whose units cancel but leave a factor (`5cm / 1mm` is 50): like Value.FoldScale of the VM. */
FIRE_COLD FIRE_FN Value foldScale(Value a, Value b, double scale, bool divide) {
    Real r;
    scale = unitNiceFactor(scale);
    if (a.kind == K_Int && b.kind == K_Int) {
        int64_t x = a.i, y = b.i;
        double inv;
        if (scale >= 1 && scale == floor(scale) && scale < 9.2e18) {
            uint64_t s = (uint64_t)scale;
            int64_t xs;
            if (!divide) return Int((int64_t)((uint64_t)x * (uint64_t)y * s));
            if (FIRE_UNLIKELY(y == 0)) return divideByZero();
            xs = (int64_t)((uint64_t)x * s);
            if (FIRE_UNLIKELY(y == -1)) return Int((int64_t)(0 - (uint64_t)xs));
            return Int(xs / y);
        }
        inv = unitNiceFactor(1.0 / scale);
        if (inv >= 1 && inv == floor(inv) && inv < 9.2e18) {
            uint64_t s = (uint64_t)inv;
            int64_t d;
            if (!divide) return Int((int64_t)((uint64_t)x * (uint64_t)y) / (int64_t)s);
            d = (int64_t)((uint64_t)y * s);
            if (FIRE_UNLIKELY(d == 0)) return divideByZero();
            if (FIRE_UNLIKELY(d == -1)) return Int((int64_t)(0 - (uint64_t)x));
            return Int(x / d);
        }
    }
    r = divide ? toR(a) / toR(b) : toR(a) * toR(b);
    return Float((Real)(r * scale));
}
/* A value of a unit that becomes a plain number (dB) in a product or quotient: that number; false when its function threw. */
FIRE_COLD FIRE_FN bool unitPlain(Value* v) {
    double x;
    if (g_ud[v->unit].ulconv < 0) return true;
    if (!unitCallConv(v->unit, true, v->kind == K_Int ? (double)v->i : (double)v->f, &x, true)) return false;
    *v = Float((Real)x);
    return true;
}
FIRE_COLD FIRE_FN Value mulUnits(Value a, Value b) {
    uint32_t u;
    if (FIRE_UNLIKELY(g_ud[a.unit].ulconv >= 0 || g_ud[b.unit].ulconv >= 0)) {
        if (!unitPlain(&a) || !unitPlain(&b)) return Undef();
        if (a.unit == 0 && b.unit == 0) return Float(toR(a) * toR(b));
    }
    u = unitMul(a.unit, b.unit);
    if (FIRE_UNLIKELY(unitIsUnitless(u) && g_ud[u].scale != 1.0)) return foldScale(a, b, g_ud[u].scale, false);
    if (a.kind == K_Int && b.kind == K_Int) return Int((int64_t)((uint64_t)a.i * (uint64_t)b.i), u);
    return Float(toR(a) * toR(b), u);
}
FIRE_COLD FIRE_FN Value divUnits(Value a, Value b) {
    uint32_t u;
    if (FIRE_UNLIKELY(g_ud[a.unit].ulconv >= 0 || g_ud[b.unit].ulconv >= 0)) {
        if (!unitPlain(&a) || !unitPlain(&b)) return Undef();
        if (a.unit == 0 && b.unit == 0) return Float(toR(a) / toR(b));
    }
    u = unitDiv(a.unit, b.unit);
    if (FIRE_UNLIKELY(unitIsUnitless(u) && g_ud[u].scale != 1.0)) return foldScale(a, b, g_ud[u].scale, true);
    if (a.kind == K_Int && b.kind == K_Int) {
        if (FIRE_UNLIKELY(b.i == 0)) return divideByZero();
        if (FIRE_UNLIKELY(b.i == -1)) return Int((int64_t)(0 - (uint64_t)a.i), u);
        return Int(a.i / b.i, u);
    }
    return Float(toR(a) / toR(b), u);
}

/* `p + n` / `p - n` for a pointer: "n elements further" (SPEC 8.3). A pointer to a variable or a field is a pointer into an array with one element (the offset
 * is kept in `unit`); a pointer to an element of an array or a buffer (`ref a[i]`) moves along the elements. Using a pointer outside its range is an
 * IndexOutOfBoundsException (see ptrValid). */
FIRE_FN Value ptrOffset(Value p, int64_t n) {
    Value r = p;
    if (n == 0) return p;
    if (p.width == 0) r.unit = (uint32_t)((int32_t)p.unit + (int32_t)n);   /* a variable or a field: an "array" with one element, the offset travels in `unit` */
    else r.p = (const void*)((uintptr_t)p.p + (uintptr_t)((intptr_t)n * (p.width == 1 ? 1 : (intptr_t)sizeof(Value))));
    return r;
}

FIRE_FN Value add(Value a, Value b) {
    if (a.kind == K_Pointer && b.kind == K_Int) return ptrOffset(a, b.i);
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return Int((int64_t)((uint64_t)a.i + (uint64_t)b.i), a.unit);
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) + toR(b), a.unit);
    } else if (isNumeric(a) && isNumeric(b)) return addUnits(a, b);
    return opFailed(a, b);
}
FIRE_FN Value ptrDiff(Value a, Value b);
FIRE_FN Value sub(Value a, Value b) {
    if (a.kind == K_Pointer && b.kind == K_Int) return ptrOffset(a, -b.i);
    if (a.kind == K_Pointer && b.kind == K_Pointer) return ptrDiff(a, b);
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return Int((int64_t)((uint64_t)a.i - (uint64_t)b.i), a.unit);
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) - toR(b), a.unit);
    } else if (isNumeric(a) && isNumeric(b)) return subUnits(a, b);
    return opFailed(a, b);
}
FIRE_FN Value mul(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == 0 && b.unit == 0)) {
        if (a.kind == K_Int && b.kind == K_Int) return Int((int64_t)((uint64_t)a.i * (uint64_t)b.i));
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) * toR(b));
    } else if (isNumeric(a) && isNumeric(b)) return mulUnits(a, b);
    return opFailed(a, b);
}
FIRE_FN Value divide(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == 0 && b.unit == 0)) {
        if (a.kind == K_Int && b.kind == K_Int) {
            if (FIRE_UNLIKELY(b.i == 0)) return divideByZero();
            if (FIRE_UNLIKELY(b.i == -1)) return Int((int64_t)(0 - (uint64_t)a.i));
            return Int(a.i / b.i);
        }
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) / toR(b));
    } else if (isNumeric(a) && isNumeric(b)) return divUnits(a, b);
    return opFailed(a, b);
}
FIRE_FN Value modulo(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) {
            if (FIRE_UNLIKELY(b.i == 0)) return divideByZero();
            if (FIRE_UNLIKELY(b.i == -1)) return Int(0, a.unit);
            return Int(a.i % b.i, a.unit);
        }
        if (isNumeric(a) && isNumeric(b)) return Float(fmod(toR(a), toR(b)), a.unit);
    } else if (isNumeric(a) && isNumeric(b)) return modUnits(a, b);
    return opFailed(a, b);
}
/* `a ^ b` (power; `#` is the bitwise xor): ints multiply, a float or a negative exponent uses pow. The units must be equal. */
FIRE_FN Value power(Value a, Value b) {
    bool useFloat;
    uint64_t result = 1, base;
    int64_t i;
    if (FIRE_UNLIKELY(!isNumeric(a) || !isNumeric(b))) return opFailed(a, b);
    if (FIRE_UNLIKELY(!unitEq(a.unit, b.unit))) { unitConflict(a.unit, b.unit); return Undef(); }
    useFloat = a.kind == K_Float || b.kind == K_Float || (b.kind == K_Int && b.i < 0);
    if (useFloat) return Float((Real)pow((double)toR(a), (double)toR(b)), a.unit);
    base = (uint64_t)a.i;
    for (i = 0; i < b.i; i++) result *= base;
    return Int((int64_t)result, a.unit);
}
/* The unit a value carries (only numbers and `undefined` can have one). */
FIRE_FN uint32_t unitOf(Value v) { return (v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined) ? v.unit : 0; }
/* The step of `x++`/`x--`: the constant 1 in the unit of `target` (`5cm++` is `6cm`). */
FIRE_FN Value stepOf(Value target, Value one) { uint32_t u = unitOf(target); if (u && !unitIsUnitless(u) && (one.kind == K_Int || one.kind == K_Float)) one.unit = u; return one; }

/* `value:unit` - converts a number to another unit of the same dimension (Value.CoerceUnit); ints are rounded half to even. Of another dimension it never
 * fails: the number in its base unit gets the target unit (`5mm:kg` is `0.005kg`, `7:s` is `7s`). */
FIRE_FN Value coerceUnit(Value v, uint32_t target) {
    double factor;
    if (FIRE_UNLIKELY(!(v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined))) fatal("A value of this type carries no unit and cannot be converted.");
    if (unitEq(v.unit, target)) return v;
    /* `a:unitless`, and `a:unit` of another dimension: the number in the base unit (the dimension is taken out), labelled with the target unit */
    if (v.kind != K_Undefined && ((unitIsUnitless(target) && !unitIsUnitless(v.unit)) || !unitCompatible(v.unit, target))) {
        double inBase;
        if (!unitToBase(v.kind == K_Int ? (double)v.i : (double)v.f, v.unit, &inBase)) return Undef();
        if (v.kind == K_Int && fabs(inBase) < 9.0e15 && inBase == floor(inBase)) return Int((int64_t)inBase, target);
        return Float((Real)inBase, target);
    }
    if (v.kind == K_Undefined) return UndefU(target);
    if (!unitLinear(v.unit) || !unitLinear(target)) {
        double converted;
        if (!unitConvert(v.kind == K_Int ? (double)v.i : (double)v.f, v.unit, target, &converted)) return Undef();
        return v.kind == K_Int ? Int((int64_t)nearbyint(converted), target) : Float((Real)converted, target);
    }
    factor = unitFactor(v.unit, target);
    if (v.kind == K_Int) return Int((int64_t)nearbyint((double)v.i * factor), target);
    if (v.kind == K_Float) return Float((Real)((double)v.f * factor), target);
    return UndefU(target);
}
/* `value::unit` - the same number with another unit (Value.RelabelUnit). */
FIRE_FN Value relabelUnit(Value v, uint32_t target) {
    if (FIRE_UNLIKELY(!(v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined))) fatal("A value of this type carries no unit and cannot be relabelled.");
    v.unit = target;
    return v;
}
/* The name of a kind as the VM writes it in its messages (ValueKind). */
FIRE_FN const char* coerceKindName(uint8_t kind) {
    switch (kind) {
        case K_Bool: return "Bool"; case K_Int: return "Int"; case K_Float: return "Float"; case K_Char: return "Char"; case K_String: return "String";
        case K_Class: return "Class"; case K_Lambda: return "Lambda"; case K_Pointer: return "Pointer"; case K_Array: return "Array"; case K_Buffer: return "Buffer";
        default: return "Undefined";
    }
}
/* `value!type` - int and float into each other, `undefined` into a number (Value.CoerceType); anything else is an ArgumentException. */
FIRE_FN Value coerceType(Value v, uint8_t target) {
    char text[96];
    if (v.kind == target) return v;
    if (v.kind == K_Int && target == K_Float) return Float((Real)v.i, v.unit);
    if (v.kind == K_Float && target == K_Int) return Int((int64_t)nearbyint((double)v.f), v.unit);
    if (v.kind == K_Undefined && target == K_Int) return Int(0, v.unit);
    if (v.kind == K_Undefined && target == K_Float) return Float(0, v.unit);
    /* characters: the number of the 16-bit code unit; a number outside 0..65535 wraps like a 16-bit cast */
    if (v.kind == K_Int && target == K_Char) return Char((uint16_t)v.i);
    if (v.kind == K_Float && target == K_Char) return Char((uint16_t)(int64_t)nearbyint((double)v.f));
    if (v.kind == K_Char && target == K_Int) return Int(v.i);
    if (v.kind == K_Char && target == K_Float) return Float((Real)v.i);
    if (v.kind == K_Undefined && target == K_Char) return Char(0);
    snprintf(text, sizeof text, "Cannot coerce type %s to %s.", coerceKindName(v.kind), coerceKindName(target));
    return fireError("ArgumentException", text);
}

/* `x!int[8]`, `x!byte`, `x!float[32]` (Value.ConvertWidth): the number cut to the width - an int in two's complement, `isUnsigned` keeps 0..2^bits-1, a float
 * rounded to float32. */
FIRE_FN Value convertWidth(Value v, int bits, bool toFloat, bool isUnsigned) {
    Value n = coerceType(v, toFloat ? K_Float : K_Int);
    uint64_t mask, raw;
    int64_t r;
    if (n.kind != (toFloat ? K_Float : K_Int)) return n;   /* (the error of coerceType) */
    if (toFloat) return bits == 32 ? Float((Real)(float)n.f, n.unit) : n;
    if (bits >= 64) return n;
    mask = (1ull << bits) - 1;
    raw = (uint64_t)n.i & mask;
    r = (int64_t)raw;
    if (!isUnsigned && (raw & (1ull << (bits - 1)))) r -= (int64_t)(1ull << bits);
    return Int(r, n.unit);
}

FIRE_FN Value negate(Value v) {
    if (v.kind == K_Int) return Int((int64_t)(0 - (uint64_t)v.i), v.unit);
    if (v.kind == K_Float) return Float(-v.f, v.unit);
    return opFailed(v, v);
}
FIRE_FN Value lnot(Value v) {
    if (FIRE_UNLIKELY(v.kind != K_Bool)) {
        char text[80];
        snprintf(text, sizeof text, "'!' (negation) expects bool, not %s.", coerceKindName(v.kind));
        fireError("ArgumentException", text);
        return Undef();
    }
    return Bool(v.i == 0);
}

#define FIRE_BITOP(NAME, OP, SYM)                                                              \
    FIRE_FN Value NAME(Value a, Value b) {                                                     \
        if (FIRE_UNLIKELY(a.kind != K_Int || b.kind != K_Int)) return intOpFailed(SYM, a, b);   \
        if (FIRE_UNLIKELY(!unitEq(a.unit, b.unit))) { unitConflict(a.unit, b.unit); return Undef(); } \
        return Int(a.i OP b.i, a.unit);                                                        \
    }
FIRE_BITOP(bitAnd, &, "&")
FIRE_BITOP(bitOr, |, "|")
FIRE_BITOP(bitXor, ^, "#")
#undef FIRE_BITOP
FIRE_FN Value shl(Value a, Value b) {
    if (FIRE_UNLIKELY(a.kind != K_Int || b.kind != K_Int)) return intOpFailed("<<", a, b);
    return Int((int64_t)((uint64_t)a.i << (b.i & 63)), a.unit);
}
FIRE_FN Value shr(Value a, Value b) {
    if (FIRE_UNLIKELY(a.kind != K_Int || b.kind != K_Int)) return intOpFailed(">>", a, b);
    return Int(a.i >> (b.i & 63), a.unit);
}
FIRE_FN Value bitNot(Value v) {
    if (FIRE_UNLIKELY(v.kind != K_Int)) return intOpFailed("~", v, v);
    return Int(~v.i, v.unit);
}

/* ---------------------------------------------------------------------------------------------------------------------
 * Comparison
 * --------------------------------------------------------------------------------------------------------------------- */
/* -1, 0, 1 like Value.Compare (NaN sorts below everything, like double.CompareTo). */
FIRE_FN int compareNumeric(Value a, Value b) {
    Real x, y;
    if (a.kind == K_Int && b.kind == K_Int) return a.i < b.i ? -1 : (a.i > b.i ? 1 : 0);
    x = toR(a); y = toR(b);
    if (x < y) return -1;
    if (x > y) return 1;
    if (x == y) return 0;
    if (isnan(x)) return isnan(y) ? 0 : -1;
    return 1;
}
/* Two texts by their UTF-16 code units (string.CompareOrdinal): -1, 0 or 1. */
FIRE_FN int compareText(Value a, Value b) {
    const Str* x = strOf(a);
    const Str* y = strOf(b);
    uint32_t n = x->length < y->length ? x->length : y->length, i;
    for (i = 0; i < n; i++)
        if (x->data[i] != y->data[i]) return x->data[i] < y->data[i] ? -1 : 1;
    return x->length < y->length ? -1 : (x->length > y->length ? 1 : 0);
}
FIRE_FN int compare(Value a, Value b) {
    char msg[64];
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (isNumeric(a) && isNumeric(b)) return compareNumeric(a, b);
    } else if (isNumeric(a) && isNumeric(b)) { VPair p = alignUnits(a, b); if (FIRE_UNLIKELY(p.a.kind == K_Undefined)) return 2; return compareNumeric(p.a, p.b); }
    if (a.kind == K_Char && b.kind == K_Char) return a.i < b.i ? -1 : (a.i > b.i ? 1 : 0);
    if (a.kind == K_String && b.kind == K_String) return compareText(a, b);   /* texts by their codes (ordinal) */
    snprintf(msg, sizeof msg, "Type %s is not numeric.", coerceKindName(isNumeric(a) ? b.kind : a.kind));
    fireError("ArgumentException", msg);
    return 2;   /* failed (thrown): <, <=, > and >= are all false, the generated code then looks at the unwinding flag */
}
FIRE_FN bool lt(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return a.i < b.i;
        if (a.kind == K_Float && b.kind == K_Float) return compareNumeric(a, b) < 0;   /* (NaN sorts below everything, like the VM) */
    }
    return compare(a, b) < 0;
}
FIRE_FN bool le(Value a, Value b) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return a.i <= b.i;
        if (a.kind == K_Float && b.kind == K_Float) return compareNumeric(a, b) <= 0;
    }
    return compare(a, b) <= 0;   /* (-1 or 0; a failed comparison gives 2) */
}
FIRE_FN bool gt(Value a, Value b) {
    int c;
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return a.i > b.i;
        if (a.kind == K_Float && b.kind == K_Float) return compareNumeric(a, b) > 0;
    }
    c = compare(a, b);
    return c > 0 && c != 2;
}
FIRE_FN bool ge(Value a, Value b) {
    int c;
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return a.i >= b.i;
        if (a.kind == K_Float && b.kind == K_Float) return compareNumeric(a, b) >= 0;
    }
    c = compare(a, b);
    return c >= 0 && c != 2;
}
/* Value.Equals: different kinds are never equal (1 != 1.0), units and widths are ignored. */
FIRE_FN bool eq(Value a, Value b) {
    if (a.kind != b.kind) return false;
    switch (a.kind) {
        case K_Int: case K_Bool: case K_Char: return a.i == b.i;
        case K_Float: return a.f == b.f || (isnan(a.f) && isnan(b.f));
        case K_Undefined: return true;
        case K_String: {
            const Str* x = strOf(a);
            const Str* y = strOf(b);
            return x == y || (x->length == y->length && memcmp(x->data, y->data, x->length * sizeof(fire_char16)) == 0);
        }
        case K_Pointer: return a.p == b.p && a.unit == b.unit;
        default: return a.p == b.p;
    }
}
/* Value.OperatorEquals - the `==` of the language: numbers compare by value (`1 == 1.0`), compatible units are converted (`1m == 100cm`). */
FIRE_FN bool opEq(Value a, Value b) {
    if (!isNumeric(a) || !isNumeric(b)) return eq(a, b);
    if (a.unit != b.unit && !unitEq(a.unit, b.unit) && unitCompatible(a.unit, b.unit)) { VPair p = alignUnits(a, b); a = p.a; b = p.b; }
    if (a.kind == K_Int && b.kind == K_Int) return a.i == b.i;
    return toR(a) == toR(b);
}
FIRE_FN bool truthy(Value v) {
    if (FIRE_UNLIKELY(v.kind != K_Bool)) {
        char msg[64];
        snprintf(msg, sizeof msg, "A condition must be a bool, not %s.", coerceKindName(v.kind));
        fireError("ArgumentException", msg);
        return false;
    }
    return v.i != 0;
}

/* ---------------------------------------------------------------------------------------------------------------------
 * Objects and ownership (SPEC 2): every object, array and byte buffer has exactly one owner - a scope or another object - and is destroyed (for an object its
 * destruct() runs, then everything it owns) when the owner is destroyed. An owner is an OwnList: for a scope a local of the generated function, for an object
 * the `owned` list in its header. Destroyed values are freed.
 *
 * Differences to the VM: a reference to a destroyed *object* is not detected (the VM keeps destroyed objects readable); a destroyed array or buffer is (see the
 * handles above).
 * --------------------------------------------------------------------------------------------------------------------- */
typedef struct Obj {
    Owned h;
    uint32_t cls;      /* class id (assigned by the generator) */
    uint32_t nfields;
    OwnList owned;     /* what this object owns */
    TArgs targs;       /* the type arguments it was created with (null: none known) */
#ifdef __cplusplus
    Value* fields() { return (Value*)((char*)this + FIRE_HDR(Obj)); }
#endif
} Obj;
FIRE_FN Value* objFields(Obj* o) { return (Value*)((char*)o + FIRE_HDR(Obj)); }

/* Runs the destructors of the object's class chain, derived class first (generated; empty if no class has one). */
void runDestructors(Obj* o);

FIRE_FN Value ObjV(Obj* o) { Value r; r.kind = K_Class; r.width = 0; r.reserved = o->h.gen; r.unit = o->h.slot; r.p = o; return r; }
FIRE_FN Obj* asObj(Value v) {
    if (FIRE_UNLIKELY(v.kind != K_Class)) fatal("A member was accessed on something that is not an object.");
    return (Obj*)v.p;
}
/* The object of a value the generator proved to be one (an object of a known class). */
FIRE_FN Obj* objAt(Value v) { return (Obj*)v.p; }

#ifdef FIRE_THREADS
extern bool g_domainOn;   /* a fire thread exists: the objects of the global scope are the shared domain (flag 64) */
FIRE_FN void domainLink(OwnList* list, Owned* o);
#endif
FIRE_FN void ownLink(OwnList* list, Owned* o) {
#ifdef FIRE_THREADS
    if (FIRE_UNLIKELY(g_domainOn)) domainLink(list, o);
#endif
    o->owner = list;
    o->prev = list->tail;
    o->next = NULL;
    if (list->tail) list->tail->next = o; else list->head = o;
    list->tail = o;
}

FIRE_FN void ownUnlink(Owned* o) {
    OwnList* list = o->owner;
    if (!list) return;
    if (o->prev) o->prev->next = o->next; else list->head = o->next;
    if (o->next) o->next->prev = o->prev; else list->tail = o->prev;
    o->prev = o->next = NULL;
    o->owner = NULL;
}

FIRE_NORETURN FIRE_COLD FIRE_FN void allocFailed(void) { fatal("Out of memory."); }

/* `new`: the object belongs to `owner` (the innermost scope of the creator, or - for `field = new X()` - the object that gets it). */
FIRE_FN Value newObject(uint32_t cls, uint32_t fieldCount, OwnList* owner) {
    void* memory = malloc(FIRE_HDR(Obj) + fieldCount * sizeof(Value));
    Obj* o;
    Value* f;
    uint32_t i;
    if (FIRE_UNLIKELY(!memory)) allocFailed();
    o = (Obj*)memory;
    o->h.okind = O_Object;
    slotAcquire(&o->h);   /* the handle: a destroyed object is recognized by it (SPEC 2.3, 2.5) */
    o->cls = cls;
    o->h.flags = 0;
    ownListInit(&o->owned, 0, &o->h);
    o->nfields = fieldCount;
    o->targs = NULL;
    f = objFields(o);
    for (i = 0; i < fieldCount; i++) f[i] = Undef();
    ownLink(owner, &o->h);
    return ObjV(o);
}

FIRE_FN void destroyList(OwnList* list);
FIRE_FN void destroyLeaf(Owned* o);
#ifdef FIRE_REFLECTION
FIRE_FN void probeFree(Obj* o);   /* the probes of an object that is destroyed (flag 2) */
#endif
#ifdef FIRE_THREADS
FIRE_FN void originFree(Obj* o);   /* flags 4 (has `taking` copies) and 32 (is one): see the fire threads section */
#endif

/* What was destroyed during a batch is freed - and dead - when the outermost batch ends: a destructor sees every object of its scope in the state it was in when
 * it was destroyed (fields and strings intact), like in the VM (SPEC 2.3). After that its handle has expired: using it throws a DestroyedException. */
FIRE_FN void batchBegin(void) { g_zombies.depth++; }
FIRE_FN void batchEnd(void) {
    Owned* o;
    if (--g_zombies.depth > 0) return;
    o = g_zombies.head;
    g_zombies.head = NULL;
    while (o) {
        Owned* next = o->next;
        Obj* obj = (Obj*)o;
        Value* f = objFields(obj);
        uint32_t i;
        for (i = 0; i < obj->nfields; i++) release(f[i]);
        slotRelease(&obj->h);   /* from now on a Value of this object is dead: using it is a DestroyedException */
#ifndef FIRE_KEEP_DESTROYED
        free(obj);
#endif
        o = next;
    }
}

FIRE_FN void destroy(Obj* o) {
    if (o->h.flags & 1) return;
    o->h.flags |= 1;
    batchBegin();
    if (o->h.flags & 16) {
        /* a copy that `taking` made for a fire thread: its original still lives, the destructor is not run for the copy */
    } else if (FIRE_UNLIKELY(g_unwind.active)) {
        /* the scope is left because of an exception: the destructor runs as ordinary code, the exception keeps unwinding afterwards */
        UnwindState saved = g_unwind;
        g_unwind.active = 0;
        runDestructors(o);
        g_unwind = saved;
    } else runDestructors(o);
    destroyList(&o->owned);
#ifdef FIRE_REFLECTION
    if (FIRE_UNLIKELY(o->h.flags & 2)) probeFree(o);
#endif
#ifdef FIRE_THREADS
    if (FIRE_UNLIKELY(o->h.flags & 44)) originFree(o);   /* an original with copies (4), an actor (8), or a copy that knows its original (32) */
#endif
    o->h.next = g_zombies.head;   /* freed with the batch */
    g_zombies.head = &o->h;
    batchEnd();
}

/* Destroys everything on the list and empties it: the objects in creation order (their destructors run), then the arrays and buffers - like the VM (SPEC 2.3). */
FIRE_FN void destroyList(OwnList* list) {
    Owned* o = list->head;
    Owned* leaves = NULL;
    Owned* leavesTail = NULL;
    batchBegin();
    list->head = list->tail = NULL;
    while (o) {
        Owned* next = o->next;
        if (o->okind == O_Object) {
            o->owner = NULL;
            destroy((Obj*)o);
        } else {
            o->next = NULL;
            if (leavesTail) leavesTail->next = o; else leaves = o;
            leavesTail = o;
        }
        o = next;
    }
    for (o = leaves; o;) {
        Owned* next = o->next;
        o->owner = NULL;
        destroyLeaf(o);
        o = next;
    }
    batchEnd();
}

/* An array or buffer is destroyed: what it holds is released, the inner arrays it owns are destroyed, its handle expires. */
FIRE_FN void destroyLeaf(Owned* o) {
    if (o->flags & 1) return;
    o->flags |= 1;
    if (o->owner) ownUnlink(o);
    if (o->okind == O_Array) {
        Arr* a = (Arr*)o;
        Value* items = arrItems(a);
        uint32_t i;
        for (i = 0; i < a->length; i++) release(items[i]);
        if (a->parts) { destroyList(a->parts); free(a->parts); }
    }
    slotRelease(o);
    free(o);
}

/* Leaving a scope: destroys what it owns and lets go of the strings/arrays it created. */
FIRE_FN void leave(OwnList* list) {
    if (list->head) destroyList(list);
    if (g_pool.top > list->mark) poolRelease(list->mark);
}

/* The header of an object, an array or a buffer (null for any other value, and for an array or buffer that is already destroyed). */
FIRE_FN Owned* ownedOf(Value v) {
    if ((v.kind == K_Class || v.kind == K_Array || v.kind == K_Buffer) && leafAlive(v)) return (Owned*)v.p;
    return NULL;
}

/* `return`: an object (array, buffer) returned from the scope that owns it does not die with it; it goes to the caller (SPEC 2.3). */
FIRE_FN void transferOut(Value v, OwnList* list) {
    Owned* o = ownedOf(v);
    if (o && o->owner == list) ownUnlink(o);
}

/* The caller takes over what came back from a call: an object, array or buffer without an owner, or a string/lambda that the callee retained for the trip (that
 * count now belongs to the scope's pool). */
FIRE_FN void adopt(Value v, OwnList* list) {
    Owned* o = ownedOf(v);
    if (o) {
        if (!o->owner) ownLink(list, o);
    } else if (isRef(v)) {
        Ref* r = refOf(v);
        if (r->rc != IMMORTAL) poolPush(r);   /* the count that came with the value now belongs to the pool */
    }
}

/* A value that came out of a function (a default argument), taken over by `list` like the result of a call. */
FIRE_FN Value adoptV(Value v, OwnList* list) {
    adopt(v, list);
    return v;
}

/* ---------------------------------------------------------------------------------------------------------------------
 * Memory of the reference-counted values
 * --------------------------------------------------------------------------------------------------------------------- */
FIRE_FN void freeRef(Ref* r) {
    if (r->type == R_Lam) {
        Lam* l = (Lam*)r;
        Value* caps = lamCaps(l);
        uint32_t i;
        for (i = 0; i < l->ncaps; i++) release(caps[i]);
    }
    free(r);
}

/* A fresh reference value is held by the temporary pool (count 1) until its scope ends. */
FIRE_FN void registerTemp(Ref* r, OwnList* list) {
    r->rc = 1;
    poolPush(r);
    (void)list;
}

FIRE_FN Str* allocStr(uint32_t length, OwnList* list) {
    Str* s = (Str*)malloc(sizeof(Str) + (size_t)(length + 1) * sizeof(fire_char16));
    fire_char16* d;
    if (FIRE_UNLIKELY(!s)) allocFailed();
    s->h.type = R_Str;
    s->length = length;
    d = (fire_char16*)(s + 1);
    d[length] = 0;
    s->data = d;
    registerTemp(&s->h, list);
    return s;
}
FIRE_FN fire_char16* strChars(Str* s) { return (fire_char16*)s->data; }

/* A new array; it belongs to `list` (the innermost scope of the creator, or the object that it is assigned to directly). */
FIRE_FN Arr* allocArr(uint32_t length, OwnList* list) {
    Arr* a = (Arr*)malloc(FIRE_HDR(Arr) + (size_t)length * sizeof(Value));
    Value* items;
    uint32_t i;
    if (FIRE_UNLIKELY(!a)) allocFailed();
    a->h.okind = O_Array;
    a->h.flags = 0;
    a->length = length;
    a->parts = NULL;
    items = arrItems(a);
    for (i = 0; i < length; i++) items[i] = Undef();
    slotAcquire(&a->h);
    ownLink(list, &a->h);
    return a;
}

FIRE_FN Lam* allocLam(uint32_t nparams, uint32_t ncaps, Value (*fn)(Value, const Value*), OwnList* list) {
    Lam* l = (Lam*)malloc(FIRE_HDR(Lam) + (size_t)ncaps * sizeof(Value));
    if (FIRE_UNLIKELY(!l)) allocFailed();
    l->h.type = R_Lam;
    l->nparams = nparams;
    l->ncaps = ncaps;
    l->nreq = nparams;
    l->on = Undef();
    l->fn = fn;
    l->dflt = NULL;
    l->targs = NULL;
#ifdef FIRE_REFLECTION
    l->sel = NULL;
    l->nsel = 0;
#endif
    registerTemp(&l->h, list);
    return l;
}

/* The `this` of a lambda body: its `on` target. */
FIRE_FN Value lamOn(Value lam) { return lamOf(lam)->on; }
FIRE_FN Value lamCapture(Value lam, uint32_t index) { return lamCaps(lamOf(lam))[index]; }

/* `callee(args...)` with fewer arguments than parameters: the default values fill in. */
FIRE_COLD FIRE_FN Value callLamDefaults(Value callee, int argc, const Value* args, OwnList* list) {
    Lam* l = lamOf(callee);
    Value full[16];
    int i;
    uint32_t k;
    if (!l->dflt || (uint32_t)argc > l->nparams || (uint32_t)argc < l->nreq || l->nparams > 16) {
        char text[160];
        snprintf(text, sizeof text, "Wrong number of arguments: expected %u%s, got %d.", l->nparams, (uint32_t)argc < l->nparams ? " (the missing parameters have no default value)" : "", argc);
        return fireError("ArgumentException", text);
    }
    for (i = 0; i < argc; i++) full[i] = args[i];
    for (k = (uint32_t)argc; k < l->nparams; k++) full[k] = adoptV(l->dflt(callee, k), list);
    return l->fn(callee, full);
}
/* `a on obj` (OpCode.RebindLambda): a new lambda with the same body, captures and default values, and `on` as its `this`. */
FIRE_FN Value rebindLam(Value lam, Value on, OwnList* list) {
    Lam* a;
    Lam* l;
    uint32_t i;
    if (FIRE_UNLIKELY(lam.kind != K_Lambda)) return fireError("ArgumentException", "'on' binds a lambda to an object; the value before 'on' is not a lambda.");
    a = lamOf(lam);
    l = allocLam(a->nparams, a->ncaps, a->fn, list);
    l->nreq = a->nreq;
    l->dflt = a->dflt;
    l->targs = a->targs;
#ifdef FIRE_REFLECTION
    l->sel = a->sel;
    l->nsel = a->nsel;
#endif
    for (i = 0; i < a->ncaps; i++) { lamCaps(l)[i] = lamCaps(a)[i]; retain(lamCaps(l)[i]); }
    l->on = on;
    return LamV(l);
}
FIRE_FN Value callLam(Value callee, int argc, const Value* args, OwnList* list) {
    Lam* l;
    if (FIRE_UNLIKELY(callee.kind != K_Lambda)) fatal("Call of a value that is not a lambda.");
    l = lamOf(callee);
    if (FIRE_UNLIKELY(l->nparams != (uint32_t)argc)) return callLamDefaults(callee, argc, args, list);
    return l->fn(callee, args);
}

/* CheckLambdaSignature: a value assigned to a `lambda<...>` annotation must have that many parameters (otherwise an ArgumentException). */
FIRE_FN void checkLambda(Value v, int nparams) {
    if (FIRE_UNLIKELY(v.kind != K_Lambda || lamOf(v)->nparams != (uint32_t)nparams)) {
        char text[120];
        if (v.kind != K_Lambda) snprintf(text, sizeof text, "Expected a lambda with %d parameter(s), got a value that is no lambda.", nparams);
        else snprintf(text, sizeof text, "The lambda has %u parameter(s), its declaration says %d.", lamOf(v)->nparams, nparams);
#ifndef FIRE_LIBRARY
        fireError("ArgumentException", text);
#else
        fatal(text);
#endif
    }
}

/* A value does not have the unit that its declaration demands (an UnitMismatchException; defined with the exceptions). */
FIRE_FN void unitMismatch(Value v, Value expectedText);

/* `obj.field = x` by the index of the field (the object is alive; the generated code checked the declaration of the field): like the sf_ helpers. */
FIRE_FN void setFieldAt(Value v, uint32_t index, Value x) {
    Obj* o = objAt(v);
    Value old = objFields(o)[index];
    objFields(o)[index] = x;
    retain(x);
    release(old);
}

/* A parameter or field declared int/float/bool (DeclaredKind): a value of that kind or undefined; a whole number for a float becomes one, anything else is an
 * ArgumentException (the generated code then looks at the unwinding flag). `unitless`: declared without a unit - a number with one is converted like
 * adaptUnit(v, unitless) first (another dimension is a UnitMismatchException). */
FIRE_FN Value adaptUnit(Value v, uint32_t unit, bool dimensionOnly, bool toFloat, Value expectedText);
FIRE_COLD FIRE_FN Value guardKindSlow(Value v, uint8_t kind, bool unitless, const char* what, Value unitlessText) {
    char text[200];
    if (v.kind == K_Undefined) return v;
    if (unitless && (v.kind == K_Int || v.kind == K_Float) && v.unit != 0) {
        v = adaptUnit(v, 0, false, false, unitlessText);
        if (!unitEq(v.unit, 0)) return Undef();   /* (thrown) */
        v.unit = 0;
    }
    if (v.kind == kind) return v;
    if (kind == K_Float && v.kind == K_Int) return Float((Real)v.i, v.unit);
    snprintf(text, sizeof text, "%s is %s, not %s.", what, kind == K_Int ? "an int" : kind == K_Float ? "a float" : "a bool", coerceKindName(v.kind));
    fireError("ArgumentException", text);
    return Undef();
}
FIRE_FN Value guardKind(Value v, uint8_t kind, bool unitless, const char* what, Value unitlessText) {
    if (FIRE_LIKELY(v.kind == kind) && (!unitless || v.unit == 0)) return v;
    return guardKindSlow(v, kind, unitless, what, unitlessText);
}

/* A float declaration that takes every unit (`#any`): a whole number becomes a float, the unit stays. */
FIRE_FN Value intToFloat(Value v) { return v.kind == K_Int ? Float((Real)v.i, v.unit) : v; }

/* CheckUnit (Value.AdaptToUnit): a declaration with a unit takes `v` - a value of the same dimension is converted into `unit`, `undefined` takes it; with
 * `dimensionOnly` (`: any m`) every unit of that dimension is taken as it is; with `toFloat` (a float declaration) a whole number is converted as a float.
 * Another dimension is an UnitMismatchException, a whole number that does not fit any more after the conversion an OverflowException. Values without a unit
 * (strings, objects) are left alone. */
FIRE_FN Value adaptUnit(Value v, uint32_t unit, bool dimensionOnly, bool toFloat, Value expectedText) {
    double converted;
    if (!(v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined)) return v;
    if (toFloat && v.kind == K_Int) v = Float((Real)v.i, v.unit);   /* a float declaration: a whole number becomes one (`float f = 3` holds 3.0) */
    if (unitEq(v.unit, unit)) return v;
    if (v.kind == K_Undefined) { if (!dimensionOnly) v.unit = unit; return v; }
    /* a unit that becomes a plain number (dB) stored without a unit, and a plain number stored in such a unit */
    if (!dimensionOnly && ((unitIsUnitless(unit) && g_ud[v.unit].ulconv >= 0) || (unitIsUnitless(v.unit) && g_ud[unit].ulconv >= 0))) {
        double x, plain = v.kind == K_Int ? (double)v.i : (double)v.f;
        bool toPlain = unitIsUnitless(unit);
        if (!unitCallConv(toPlain ? v.unit : unit, toPlain, plain, &x, true)) return v;
        if (v.kind == K_Float) return Float((Real)x, unit);
        return Int((int64_t)nearbyint(x), unit);
    }
    if (FIRE_UNLIKELY(!unitCompatible(v.unit, unit))) { unitMismatch(v, expectedText); return v; }
    if (dimensionOnly) return v;
    if (!unitLinear(v.unit) || !unitLinear(unit)) {
        if (!unitConvert(v.kind == K_Int ? (double)v.i : (double)v.f, v.unit, unit, &converted)) return v;
        if (v.kind == K_Float) return Float((Real)converted, unit);
        converted = nearbyint(converted);
    } else {
        double factor = unitFactor(v.unit, unit);
        if (v.kind == K_Float) return Float((Real)((double)v.f * factor), unit);
        converted = nearbyint((double)v.i * factor);
    }
    if (FIRE_UNLIKELY(fabs(converted) >= 9.2e18)) {
#ifndef FIRE_LIBRARY
        fireError("OverflowException", "A whole number does not fit any more after the conversion into the unit of its declaration.");
#else
        fatal("A whole number does not fit any more after the conversion into the unit of its declaration.");
#endif
        return v;
    }
    return Int((int64_t)converted, unit);
}

/* The byte order of the machine, found out at run time (a 16-bit 1 starts with its low byte in little-endian memory). */
FIRE_FN bool hostLittle(void) { uint16_t one = 1; return *(uint8_t*)&one == 1; }

FIRE_FN Buf* allocBuf(uint32_t length, OwnList* list) {
    Buf* b = (Buf*)malloc(sizeof(Buf) + length);
    if (FIRE_UNLIKELY(!b)) allocFailed();
    b->h.okind = O_Buffer;
    b->h.flags = 0;
    b->length = length;
    b->little = hostLittle() ? 1 : 0;
    b->data = (uint8_t*)(b + 1);
    memset(b->data, 0, length);
    slotAcquire(&b->h);
    ownLink(list, &b->h);
    return b;
}

#ifdef FIRE_LIBRARY
/* A buffer value over memory of the caller (the virtual machine hands its byte buffers to a package library without copying): the natives read and write the
 * bytes in place. The memory is not ours: it is neither freed nor moved while the call runs. */
FIRE_FN Buf* allocBufExternal(uint8_t* data, uint32_t length, OwnList* list) {
    Buf* b = (Buf*)malloc(sizeof(Buf));
    if (FIRE_UNLIKELY(!b)) allocFailed();
    b->h.okind = O_Buffer;
    b->h.flags = 0;
    b->length = length;
    b->little = hostLittle() ? 1 : 0;
    b->data = data;
    slotAcquire(&b->h);
    ownLink(list, &b->h);
    return b;
}
#endif

/* ---------------------------------------------------------------------------------------------------------------------
 * Strings
 * --------------------------------------------------------------------------------------------------------------------- */
/* Generated: calls the ToString() method of the object's class if it has one (the result is a string value created in `list`). */
bool userToString(Value object, OwnList* list, Value* result);

const char* className(uint32_t cls);   /* (generated) */

/* The characters of a value as `+` and `$"..."` append them (Value.ToString()). */
typedef struct Piece {
    const fire_char16* p;
    uint32_t n;
    fire_char16 buf[80];
} Piece;

FIRE_FN uint32_t widenAscii(const char* s, uint32_t n, fire_char16* out) {
    uint32_t i;
    for (i = 0; i < n; i++) out[i] = (fire_char16)(unsigned char)s[i];
    return n;
}

/* UTF-8 -> UTF-16 (unit symbols such as "µm"); returns the number of UTF-16 units written. */
FIRE_FN uint32_t utf8ToUtf16(const char* s, fire_char16* out, uint32_t cap) {
    uint32_t n = 0;
    while (*s && n + 2 < cap) {
        unsigned c = (unsigned char)*s++;
        uint32_t cp;
        if (c < 0x80) cp = c;
        else if ((c >> 5) == 6 && *s) cp = ((c & 0x1F) << 6) | ((unsigned char)*s++ & 0x3F);
        else if ((c >> 4) == 14 && s[0] && s[1]) { cp = ((c & 0x0F) << 12) | (((unsigned char)s[0] & 0x3F) << 6) | ((unsigned char)s[1] & 0x3F); s += 2; }
        else if ((c >> 3) == 30 && s[0] && s[1] && s[2]) { cp = ((c & 0x07) << 18) | (((unsigned char)s[0] & 0x3F) << 12) | (((unsigned char)s[1] & 0x3F) << 6) | ((unsigned char)s[2] & 0x3F); s += 3; }
        else cp = 0xFFFD;
        if (cp >= 0x10000) { cp -= 0x10000; out[n++] = (fire_char16)(0xD800 + (cp >> 10)); out[n++] = (fire_char16)(0xDC00 + (cp & 0x3FF)); }
        else out[n++] = (fire_char16)cp;
    }
    return n;
}

FIRE_FN void setPiece(Piece* out, const char* ascii) {
    out->n = widenAscii(ascii, (uint32_t)strlen(ascii), out->buf);
    out->p = out->buf;
}

FIRE_FN void pieceOf(Value v, Piece* out, OwnList* list) {
    char tmp[64];
    switch (v.kind) {
        case K_String: out->p = strOf(v)->data; out->n = strOf(v)->length; return;
        case K_Char: out->buf[0] = (fire_char16)v.i; out->p = out->buf; out->n = 1; return;
        case K_Bool: setPiece(out, v.i ? "True" : "False"); return;
        case K_Int: {
            int n = snprintf(tmp, sizeof tmp, "%lld", (long long)v.i);
            out->n = widenAscii(tmp, (uint32_t)n, out->buf);
            break;
        }
        case K_Float: out->n = widenAscii(tmp, (uint32_t)formatReal(v.f, tmp), out->buf); break;
        case K_Undefined: setPiece(out, "undefined"); if (v.unit && !unitIsUnitless(v.unit)) { out->buf[out->n++] = (fire_char16)':'; } break;
        case K_Class: {
            Value text;
            text.kind = K_Undefined; text.width = 0; text.reserved = 0; text.unit = 0; text.i = 0;   /* (the compiler cannot see that userToString sets it) */
            if (userToString(v, list, &text)) {
                if (FIRE_UNLIKELY(g_unwind.active || text.kind != K_String)) { out->p = out->buf; out->n = 0; return; }   /* ToString() threw */
                out->p = strOf(text)->data; out->n = strOf(text)->length;
            }
            else if (!leafAlive(v)) setPiece(out, "<object>");   /* (dead: its class is not known any more) */
            else {
                char t[80];
                int n = snprintf(t, sizeof t, "<object %s>", className(asObj(v)->cls));
                if (n >= (int)sizeof t) n = (int)sizeof t - 1;
                out->p = out->buf;
                out->n = widenAscii(t, (uint32_t)n, out->buf);
            }
            return;
        }
        case K_Lambda: setPiece(out, "<lambda>"); return;
        case K_Pointer: setPiece(out, "<pointer>"); return;
        case K_Array: setPiece(out, "<array>"); return;
        case K_Buffer: {
            int n = snprintf(tmp, sizeof tmp, "<buffer %u bytes>", (unsigned)bufOf(v)->length);
            out->n = widenAscii(tmp, (uint32_t)n, out->buf);
            break;
        }
        default: setPiece(out, "?"); return;
    }
    out->p = out->buf;
    if (v.unit && (v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined) && !unitIsUnitless(v.unit))
        out->n += utf8ToUtf16(g_ud[v.unit].name, out->buf + out->n, 80 - out->n);
}

/* `a + b` when one side is a string: the other side is appended as its text. */
FIRE_FN Value concat(Value a, Value b, OwnList* list) {
    Piece pa, pb;
    Str* r;
    fire_char16* d;
    pieceOf(a, &pa, list);
    pieceOf(b, &pb, list);
    r = allocStr(pa.n + pb.n, list);
    d = strChars(r);
    memcpy(d, pa.p, pa.n * sizeof(fire_char16));
    memcpy(d + pa.n, pb.p, pb.n * sizeof(fire_char16));
    return StrV(r);
}

/* The text of any value as a new string (`$"{x}"` without a format). */
FIRE_FN Value toStringValue(Value v, OwnList* list) {
    Piece p;
    Str* r;
    if (v.kind == K_String) return v;
    pieceOf(v, &p, list);
    r = allocStr(p.n, list);
    memcpy(strChars(r), p.p, p.n * sizeof(fire_char16));
    return StrV(r);
}

FIRE_COLD FIRE_FN Value addSlow(Value a, Value b, OwnList* list) {
    if (a.kind == K_Pointer && b.kind == K_Int) return ptrOffset(a, b.i);
    if (a.kind == K_String || b.kind == K_String) return concat(a, b, list);
    if (isNumeric(a) && isNumeric(b)) return addUnits(a, b);
    return opFailed(a, b);
}

/* `+` for operands that may be strings (the generator uses `add` where both are known to be numbers). */
FIRE_FN Value addR(Value a, Value b, OwnList* list) {
    if (FIRE_LIKELY(a.unit == b.unit)) {
        if (a.kind == K_Int && b.kind == K_Int) return Int((int64_t)((uint64_t)a.i + (uint64_t)b.i), a.unit);
        if (isNumeric(a) && isNumeric(b)) return Float(toR(a) + toR(b), a.unit);
    }
    return addSlow(a, b, list);
}

/* ---- Unicode helpers (Basic Latin, Latin-1, Greek and Cyrillic; everything else is left alone) ------------------------- */
FIRE_FN bool isWhiteC(uint32_t c) {
    return c == 0x20 || (c >= 9 && c <= 13) || c == 0x85 || c == 0xA0 || c == 0x1680 || (c >= 0x2000 && c <= 0x200A) ||
           c == 0x2028 || c == 0x2029 || c == 0x202F || c == 0x205F || c == 0x3000;
}
FIRE_FN uint32_t toUpperC(uint32_t c) {
    if (c >= 'a' && c <= 'z') return c - 32;
    if (c < 0xB5) return c;
    if (c == 0xB5) return 0x39C;
    if (c >= 0xE0 && c <= 0xFE && c != 0xF7) return c - 32;
    if (c == 0xFF) return 0x178;
    if (c >= 0x3B1 && c <= 0x3C9 && c != 0x3C2) return c - 32;
    if (c == 0x3C2) return 0x3A3;
    if (c == 0x3AC) return 0x386;
    if (c >= 0x3AD && c <= 0x3AF) return c - 37;
    if (c == 0x3CC) return 0x38C;
    if (c == 0x3CD || c == 0x3CE) return c - 63;
    if (c >= 0x430 && c <= 0x44F) return c - 32;
    if (c >= 0x450 && c <= 0x45F) return c - 80;
    return c;
}
FIRE_FN uint32_t toLowerC(uint32_t c) {
    if (c >= 'A' && c <= 'Z') return c + 32;
    if (c < 0xC0) return c;
    if (c <= 0xDE && c != 0xD7) return c + 32;
    if (c >= 0x391 && c <= 0x3A9 && c != 0x3A2) return c + 32;
    if (c == 0x386) return 0x3AC;
    if (c >= 0x388 && c <= 0x38A) return c + 37;
    if (c == 0x38C) return 0x3CC;
    if (c == 0x38E || c == 0x38F) return c + 63;
    if (c >= 0x410 && c <= 0x42F) return c + 32;
    if (c >= 0x400 && c <= 0x40F) return c + 80;
    return c;
}
FIRE_FN bool isLetterC(uint32_t c) {
    if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')) return true;
    if (c < 0xAA) return false;
    if (c == 0xAA || c == 0xB5 || c == 0xBA) return true;
    if (c >= 0xC0 && c <= 0x24F) return c != 0xD7 && c != 0xF7;
    if (c >= 0x370 && c <= 0x3FF) return c != 0x37E && c != 0x387 && c != 0x375 && c != 0x384 && c != 0x385;
    return (c >= 0x400 && c <= 0x481) || (c >= 0x48A && c <= 0x52F);
}

/* ---- The string methods of the prelude (`class extends string`), see StringMethods.cs --------------------------------- */
typedef struct Chars {
    const fire_char16* sp;
    uint32_t n;
    fire_char16 one;
    bool isChar;
} Chars;
/* The characters (a single char lives inside the struct, so take the pointer from the copy you keep). */
FIRE_FN const fire_char16* charsPtr(const Chars* c) { return c->isChar ? &c->one : c->sp; }
/* A `string` or `char` argument as characters (the VM's Text()). */
FIRE_FN Chars charsOf(Value v) {
    Chars c;
    c.sp = NULL; c.one = 0; c.isChar = false; c.n = 0;
    if (v.kind == K_Char) { c.one = (fire_char16)v.i; c.isChar = true; c.n = 1; }
    else if (v.kind == K_String) { c.sp = strOf(v)->data; c.n = strOf(v)->length; }
    else fatal("A string or char was expected.");
    return c;
}

FIRE_FN int64_t findFrom(const fire_char16* s, uint32_t len, const fire_char16* v, uint32_t vn, uint32_t start) {
    uint32_t i;
    if (vn == 0) return start;
    if (vn > len) return -1;
    for (i = start; i + vn <= len; i++)
        if (memcmp(s + i, v, vn * sizeof(fire_char16)) == 0) return i;
    return -1;
}

FIRE_FN Value newStrFrom(const fire_char16* p, uint32_t n, OwnList* list) {
    Str* r = allocStr(n, list);
    memcpy(strChars(r), p, n * sizeof(fire_char16));
    return StrV(r);
}

/* Numbers from text (StringMethods.ParseInt/ParseFloat - the same rules): spaces at both ends, a sign, then for an int digits (also `0x`/`0b`) with `_` only
 * between two digits; for a float digits, an optional fraction and exponent. */
FIRE_FN bool numSpace(fire_char16 c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n'; }
FIRE_FN void numBody(const fire_char16* s, uint32_t len, uint32_t* start, uint32_t* end, bool* negative) {
    uint32_t i = 0, e = len;
    while (i < e && numSpace(s[i])) i++;
    while (e > i && numSpace(s[e - 1])) e--;
    *negative = false;
    if (i < e && (s[i] == '+' || s[i] == '-')) { *negative = s[i] == '-'; i++; }
    *start = i; *end = e;
}
FIRE_FN bool parseIntText(const fire_char16* s, uint32_t len, int64_t* out) {
    uint32_t i, end, k;
    bool negative;
    uint64_t radix = 10, magnitude = 0, limit;
    numBody(s, len, &i, &end, &negative);
    if (end - i > 2 && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X')) { radix = 16; i += 2; }
    else if (end - i > 2 && s[i] == '0' && (s[i + 1] == 'b' || s[i + 1] == 'B')) { radix = 2; i += 2; }
    if (i >= end) return false;
    limit = negative ? 9223372036854775808ull : 9223372036854775807ull;
    for (k = i; k < end; k++) {
        fire_char16 c = s[k];
        uint64_t digit;
        if (c == '_' && k > i && k + 1 < end && s[k + 1] != '_') continue;
        digit = c >= '0' && c <= '9' ? (uint64_t)(c - '0') : c >= 'a' && c <= 'f' ? (uint64_t)(c - 'a' + 10) : c >= 'A' && c <= 'F' ? (uint64_t)(c - 'A' + 10) : 99;
        if (digit >= radix) return false;
        if (magnitude > (limit - digit) / radix) return false;
        magnitude = magnitude * radix + digit;
    }
    *out = negative ? (int64_t)(0ull - magnitude) : (int64_t)magnitude;
    return true;
}
FIRE_FN bool fire_isDigit16(fire_char16 c) { return c >= '0' && c <= '9'; }
/* A run of digits (with `_` between two of them) from `*p` into `clean`. */
FIRE_FN void fire_digitRun(const fire_char16* s, uint32_t end, uint32_t* p, char* clean, uint32_t* n, uint32_t* digits) {
    uint32_t start = *p;
    while (*p < end && *n < 380 && (fire_isDigit16(s[*p]) || (s[*p] == '_' && *p > start && *p + 1 < end && fire_isDigit16(s[*p + 1])))) {
        if (s[*p] != '_') { clean[(*n)++] = (char)s[*p]; (*digits)++; }
        (*p)++;
    }
}
FIRE_FN bool parseFloatText(const fire_char16* s, uint32_t len, double* out) {
    uint32_t i, end, n = 0, digits = 0, p;
    bool negative;
    char clean[400];
    double v;
    numBody(s, len, &i, &end, &negative);
    p = i;
    fire_digitRun(s, end, &p, clean, &n, &digits);
    if (p < end && s[p] == '.') { clean[n++] = '.'; p++; fire_digitRun(s, end, &p, clean, &n, &digits); }
    if (digits == 0) return false;
    if (p < end && (s[p] == 'e' || s[p] == 'E')) {
        uint32_t before;
        clean[n++] = 'e'; p++;
        if (p < end && (s[p] == '+' || s[p] == '-')) clean[n++] = (char)s[p++];
        before = digits;
        fire_digitRun(s, end, &p, clean, &n, &digits);
        if (digits == before) return false;
    }
    if (p != end) return false;
    clean[n] = 0;
    v = strtod(clean, NULL);
    *out = negative ? -v : v;
    return true;
}
/* `'text' is not a number.` as the ArgumentException of ToInt/ToFloat. */
FIRE_FN Value numberTextError(Value text, const char* tail) {
    char utf8[300];
    char message[340];
    uint32_t n = 0, i;
    const Str* t = strOf(text);
    for (i = 0; i < t->length && n + 4 < 240; i++) {
        uint32_t c = t->data[i];
        if (c >= 0xD800 && c < 0xDC00 && i + 1 < t->length) { c = 0x10000 + ((c - 0xD800) << 10) + (t->data[i + 1] - 0xDC00); i++; }
        if (c < 0x80) utf8[n++] = (char)c;
        else if (c < 0x800) { utf8[n++] = (char)(0xC0 | (c >> 6)); utf8[n++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { utf8[n++] = (char)(0xE0 | (c >> 12)); utf8[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); utf8[n++] = (char)(0x80 | (c & 0x3F)); }
        else { utf8[n++] = (char)(0xF0 | (c >> 18)); utf8[n++] = (char)(0x80 | ((c >> 12) & 0x3F)); utf8[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); utf8[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    utf8[n] = 0;
    snprintf(message, sizeof message, "'%s%s", utf8, tail);
    return fireError("ArgumentException", message);
}
/* `__StringCall(id, text, args...)`: the methods of `string`. `argc` counts the arguments after the text. */
FIRE_FN Value stringCall(int64_t method, Value text, int argc, Value a0, Value a1, OwnList* list) {
    const fire_char16* s;
    uint32_t len;
    if (text.kind != K_String) fatal("The string method was called on something that is not a string.");
    s = strOf(text)->data;
    len = strOf(text)->length;
    switch (method) {
        case 1: {  /* IndexOf(value[, start]) */
            Chars v = charsOf(a0);
            int64_t start = argc == 2 ? a1.i : 0;
            if (start < 0 || start > (int64_t)len) return indexError("String index", start, len);
            return Int(findFrom(s, len, charsPtr(&v), v.n, (uint32_t)start));
        }
        case 2: {  /* LastIndexOf(value[, start]) */
            Chars v = charsOf(a0);
            int64_t start = argc == 2 ? a1.i : (int64_t)len - 1, i;
            if (len == 0) return Int(v.n == 0 ? 0 : -1);
            if (start < 0 || start >= (int64_t)len) return indexError("String index", start, len);
            if (v.n == 0) return Int(start + 1);
            for (i = start - (int64_t)v.n + 1; i >= 0; i--)
                if (memcmp(s + i, charsPtr(&v), v.n * sizeof(fire_char16)) == 0) return Int(i);
            return Int(-1);
        }
        case 3: {  /* Substring(start[, count]) */
            int64_t start = a0.i, count;
            if (start < 0 || start > (int64_t)len) return indexError("String index", start, len);
            count = argc == 2 ? a1.i : (int64_t)len - start;
            if (count < 0 || count > (int64_t)len - start) return indexError("String index", start + count, len);
            if (start == 0 && count == (int64_t)len) return text;
            return newStrFrom(s + start, (uint32_t)count, list);
        }
        case 4: {  /* CharAt(index) */
            if (a0.i < 0 || a0.i >= (int64_t)len) return indexError("String index", a0.i, len);
            return Char(s[a0.i]);
        }
        case 5: { Chars v = charsOf(a0); return Bool(findFrom(s, len, charsPtr(&v), v.n, 0) >= 0); }
        case 6: { Chars v = charsOf(a0); return Bool(v.n <= len && memcmp(s, charsPtr(&v), v.n * sizeof(fire_char16)) == 0); }
        case 7: { Chars v = charsOf(a0); return Bool(v.n <= len && memcmp(s + len - v.n, charsPtr(&v), v.n * sizeof(fire_char16)) == 0); }
        case 8: case 9: {  /* ToUpper / ToLower */
            Str* r = allocStr(len, list);
            fire_char16* d = strChars(r);
            uint32_t i;
            for (i = 0; i < len; i++) d[i] = (fire_char16)(method == 8 ? toUpperC(s[i]) : toLowerC(s[i]));
            return StrV(r);
        }
        case 10: case 11: case 12: {  /* Trim / TrimStart / TrimEnd */
            uint32_t b = 0, e = len;
            if (method != 12) while (b < e && isWhiteC(s[b])) b++;
            if (method != 11) while (e > b && isWhiteC(s[e - 1])) e--;
            if (b == 0 && e == len) return text;
            return newStrFrom(s + b, e - b, list);
        }
        case 13: {  /* Replace(old, new): every occurrence, an empty `old` changes nothing */
            Chars o = charsOf(a0), w = charsOf(a1);
            uint32_t count = 0, from = 0;
            int64_t i;
            Str* r;
            fire_char16* d;
            if (o.n == 0) return text;
            for (i = findFrom(s, len, charsPtr(&o), o.n, 0); i >= 0; i = findFrom(s, len, charsPtr(&o), o.n, (uint32_t)i + o.n)) count++;
            if (count == 0) return text;
            r = allocStr(len - count * o.n + count * w.n, list);
            d = strChars(r);
            for (i = findFrom(s, len, charsPtr(&o), o.n, 0); i >= 0; i = findFrom(s, len, charsPtr(&o), o.n, from)) {
                memcpy(d, s + from, (uint32_t)(i - from) * sizeof(fire_char16)); d += (uint32_t)i - from;
                memcpy(d, charsPtr(&w), w.n * sizeof(fire_char16)); d += w.n;
                from = (uint32_t)i + o.n;
            }
            memcpy(d, s + from, (len - from) * sizeof(fire_char16));
            return StrV(r);
        }
        case 14: {  /* Split(separator): an array of the parts, empty parts kept; an empty separator gives the whole string */
            Chars sep = charsOf(a0);
            uint32_t parts = 1, from = 0, k = 0;
            int64_t i;
            Arr* arr;
            Value last;
            if (sep.n > 0)
                for (i = findFrom(s, len, charsPtr(&sep), sep.n, 0); i >= 0; i = findFrom(s, len, charsPtr(&sep), sep.n, (uint32_t)i + sep.n)) parts++;
            arr = allocArr(parts, list);
            if (sep.n > 0)
                for (i = findFrom(s, len, charsPtr(&sep), sep.n, 0); i >= 0; i = findFrom(s, len, charsPtr(&sep), sep.n, from)) {
                    Value part = newStrFrom(s + from, (uint32_t)i - from, list);
                    arrItems(arr)[k++] = part; retain(part);
                    from = (uint32_t)i + sep.n;
                }
            last = (from == 0) ? text : newStrFrom(s + from, len - from, list);
            arrItems(arr)[k] = last; retain(last);
            return ArrV(arr);
        }
        case 15: case 16: {  /* PadLeft / PadRight(width[, fill]) */
            int64_t width = a0.i;
            fire_char16 fill = ' ';
            Str* r;
            fire_char16* d;
            uint32_t pad, i;
            if (width < 0 || width > 0x7FFFFFFF) return indexError("String index", width, len);
            if (argc == 2) { Chars f = charsOf(a1); if (f.n > 0) fill = charsPtr(&f)[0]; }
            if (width <= (int64_t)len) return text;
            r = allocStr((uint32_t)width, list);
            d = strChars(r);
            pad = (uint32_t)width - len;
            if (method == 15) { for (i = 0; i < pad; i++) d[i] = fill; memcpy(d + pad, s, len * sizeof(fire_char16)); }
            else { memcpy(d, s, len * sizeof(fire_char16)); for (i = 0; i < pad; i++) d[len + i] = fill; }
            return StrV(r);
        }
        case 17: case 19: {  /* ToInt / TryToInt */
            int64_t whole;
            if (parseIntText(s, len, &whole)) return Int(whole);
            if (method == 19) return Undef();
            return numberTextError(text, "' is not a whole number.");
        }
        case 18: case 20: {  /* ToFloat / TryToFloat */
            double number;
            if (parseFloatText(s, len, &number)) return Float((Real)number);
            if (method == 20) return Undef();
            return numberTextError(text, "' is not a number.");
        }
        case 22: {  /* Join(texts) - the prelude passes an array of strings */
            Arr* items;
            uint64_t total = 0;
            uint32_t i;
            Str* r;
            fire_char16* d;
            if (a0.kind != K_Array) return fireError("ArgumentException", "Join expects a List or an array.");
            items = arrOf(a0);
            for (i = 0; i < items->length; i++) total += strOf(arrItems(items)[i])->length + (i > 0 ? len : 0);
            if (total > 0x7FFFFFFF) fatal("The joined text is too long.");
            r = allocStr((uint32_t)total, list);
            d = strChars(r);
            for (i = 0; i < items->length; i++) {
                const Str* part;
                if (i > 0) { memcpy(d, s, len * sizeof(fire_char16)); d += len; }
                part = strOf(arrItems(items)[i]);
                memcpy(d, part->data, part->length * sizeof(fire_char16));
                d += part->length;
            }
            return StrV(r);
        }
        case 21: {  /* CompareTo(other) */
            Chars v = charsOf(a0);
            uint32_t n = len < v.n ? len : v.n, i;
            for (i = 0; i < n; i++)
                if (s[i] != charsPtr(&v)[i]) return Int(s[i] < charsPtr(&v)[i] ? -1 : 1);
            return Int(len < v.n ? -1 : (len > v.n ? 1 : 0));
        }
        default: fatal("Unknown string method.");
    }
}

/* `__CharCall(id, char)`: the methods of `char`. */
FIRE_FN Value charCall(int64_t method, Value ch, OwnList* list) {
    uint32_t c;
    if (ch.kind != K_Char) fatal("The char method was called on something that is not a char.");
    c = (uint32_t)ch.i;
    switch (method) {
        case 1: return Bool(c >= '0' && c <= '9');
        case 2: return Bool(isLetterC(c));
        case 3: return Bool(isLetterC(c) || (c >= '0' && c <= '9'));
        case 4: return Bool(isWhiteC(c));
        case 5: return Bool(toLowerC(c) != c);
        case 6: return Bool(toUpperC(c) != c || c == 0xDF);
        case 7: return Char(toUpperC(c));
        case 8: return Char(toLowerC(c));
        case 9: { fire_char16 u = (fire_char16)c; return newStrFrom(&u, 1, list); }
        case 10: return Int(c);
        default: fatal("Unknown char method.");
    }
}

/* ---- embedded files: `new Resource("path")` (docs/RESOURCES.md) ------------------------------------------------------------------------------------------------- */
/* The files the compiler embedded in the program: the generated file points these at its table (a library without resources has none). */
typedef struct ResEntry { const char* name; const uint8_t* data; uint32_t length; } ResEntry;
extern const ResEntry* g_resources;
extern int g_resourceCount;

/* The UTF-16 units of UTF-8 bytes (invalid sequences become U+FFFD, characters above U+FFFF two surrogates - like the VM): written to `out` (when it is not
 * null); returns their number. */
FIRE_FN uint32_t decodeUtf8(const uint8_t* p, size_t n, fire_char16* out) {
    size_t i = 0;
    uint32_t count = 0;
    while (i < n) {
        uint32_t c = p[i], extra = 0, k;
        bool ok = true;
        if (c < 0x80) { extra = 0; }
        else if (c >= 0xC2 && c < 0xE0) { c &= 0x1F; extra = 1; }
        else if (c >= 0xE0 && c < 0xF0) { c &= 0x0F; extra = 2; }
        else if (c >= 0xF0 && c < 0xF5) { c &= 0x07; extra = 3; }
        else { if (out) out[count] = (fire_char16)0xFFFD; count++; i++; continue; }
        for (k = 1; k <= extra; k++) {
            if (i + k >= n || (p[i + k] & 0xC0) != 0x80) { ok = false; break; }
            c = (c << 6) | (p[i + k] & 0x3F);
        }
        if (!ok) { if (out) out[count] = (fire_char16)0xFFFD; count++; i++; continue; }
        i += extra + 1;
        if (c >= 0x10000) {
            c -= 0x10000;
            if (out) { out[count] = (fire_char16)(0xD800 + (c >> 10)); out[count + 1] = (fire_char16)(0xDC00 + (c & 0x3FF)); }
            count += 2;
        }
        else { if (out) out[count] = (fire_char16)c; count++; }
    }
    return count;
}

FIRE_FN Value newStrFromUtf8(const uint8_t* p, size_t n, OwnList* list) {
    uint32_t count = decodeUtf8(p, n, NULL);
    Str* s = allocStr(count, list);
    decodeUtf8(p, n, strChars(s));
    return StrV(s);
}

/* `__ResourceCall(op, id)`: 0 count, 1 length, 2 bytes (a buffer), 3 name, 4 text (UTF-8). */
FIRE_FN Value resourceCall(int64_t op, Value id, OwnList* list) {
    const ResEntry* r;
    if (op == 0) return Int(g_resourceCount);
    if (id.kind != K_Int) fatal("A Resource needs a path that is a string literal: new Resource(\"images/logo.png\") - the compiler embeds the file.");
    if (id.i < 0 || id.i >= g_resourceCount) fatal("No resource with that number.");
    r = &g_resources[id.i];
    switch (op) {
        case 1: return Int((int64_t)r->length);
        case 2: { Buf* b = allocBuf(r->length, list); if (r->length) memcpy(b->data, r->data, r->length); return BufV(b); }
        case 3: return newStrFromUtf8((const uint8_t*)r->name, strlen(r->name), list);
        case 4: return newStrFromUtf8(r->data, r->length, list);
        default: fatal("Unknown resource operation.");
    }
}

/* ---- Number formats of `$"{x:F2}"` (Value.Format): X/x, D, B, F, E ------------------------------------------------------ */
FIRE_FN Value formatValue(Value v, Value specV, OwnList* list) {
    const Str* spec = strOf(specV);
    char text[400];
    int n = 0;
    uint32_t first, letter;
    int64_t number = -1;       /* the digits after the letter */
    bool hasNumber = false;
    if (spec->length == 0) return toStringValue(v, list);
    first = spec->data[0];
    {
        int64_t acc = 0;
        bool ok = spec->length > 1;
        uint32_t i;
        for (i = 1; i < spec->length; i++) {
            uint32_t d = spec->data[i];
            if (d < '0' || d > '9') { ok = false; break; }
            acc = acc * 10 + (d - '0');
            if (acc > 99) break;
        }
        if (ok) { hasNumber = true; number = acc; }
    }
    letter = toUpperC(first);
    if (letter == 'X' || letter == 'D' || letter == 'B') {
        uint64_t u;
        char digits[80];
        int dn = 0, width, i;
        bool negative = false;
        fire_char16 wide[400];
        uint32_t wn;
        if (v.kind != K_Int) fatal("The format specifier expects an int.");
        u = (uint64_t)v.i;
        if (letter == 'X') {
            const char* hex = first == 'X' ? "0123456789ABCDEF" : "0123456789abcdef";
            do { digits[dn++] = hex[u & 15]; u >>= 4; } while (u);
        } else if (letter == 'B') {
            do { digits[dn++] = (char)('0' + (u & 1)); u >>= 1; } while (u);
        } else {
            negative = v.i < 0;
            if (negative) u = 0 - u;
            do { digits[dn++] = (char)('0' + u % 10); u /= 10; } while (u);
        }
        width = hasNumber ? (int)number : 0;
        if (negative) text[n++] = '-';
        for (i = dn; i < width && n < 380; i++) text[n++] = '0';
        while (dn) text[n++] = digits[--dn];
        wn = widenAscii(text, (uint32_t)n, wide);
        return newStrFrom(wide, wn, list);
    }
    if (letter == 'F') {
        double d;
        int prec;
        fire_char16 wide[400];
        uint32_t wn;
        if (v.kind != K_Int && v.kind != K_Float) fatal("The format specifier expects a number.");
        d = (double)toR(v);
        prec = hasNumber ? (int)number : (spec->length == 1 ? 2 : -1);
        if (prec < 0) fatal("Unknown format specifier.");
        if (isnan(d)) n = snprintf(text, sizeof text, "NaN");
        else if (isinf(d)) n = snprintf(text, sizeof text, "%s", d < 0 ? "-Infinity" : "Infinity");
        else n = snprintf(text, sizeof text, "%.*f", prec, d);
        if (n < 0 || n >= (int)sizeof text) fatal("The formatted number is too long.");
        wn = widenAscii(text, (uint32_t)n, wide);
        return newStrFrom(wide, wn, list);
    }
    if (letter == 'E') {
        /* scientific: `1.234560E+004` (six decimals by default, an exponent of at least three digits, `e` in lower case for `e`) */
        double d;
        int prec;
        fire_char16 wide[400];
        uint32_t wn;
        if (v.kind != K_Int && v.kind != K_Float) fatal("The format specifier expects a number.");
        d = (double)toR(v);
        prec = hasNumber ? (int)number : (spec->length == 1 ? 6 : -1);
        if (prec < 0) fatal("Unknown format specifier.");
        if (isnan(d)) n = snprintf(text, sizeof text, "NaN");
        else if (isinf(d)) n = snprintf(text, sizeof text, "%s", d < 0 ? "-Infinity" : "Infinity");
        else {
            char raw[400];
            char* e = raw;
            int digits, i;
            int rn = snprintf(raw, sizeof raw, first == 'E' ? "%.*E" : "%.*e", prec, d);
            if (rn < 0 || rn >= (int)sizeof raw - 4) fatal("The formatted number is too long.");
            while (*e && *e != 'E' && *e != 'e') e++;
            n = (int)(e - raw);
            memcpy(text, raw, (size_t)n);
            text[n++] = *e;
            text[n++] = e[1];                      /* the sign */
            digits = (int)strlen(e + 2);
            for (i = digits; i < 3; i++) text[n++] = '0';
            memcpy(text + n, e + 2, (size_t)digits);
            n += digits;
        }
        wn = widenAscii(text, (uint32_t)n, wide);
        return newStrFrom(wide, wn, list);
    }
    fatal("Unknown format specifier.");
}

#ifdef FIRE_REFLECTION
/* ---------------------------------------------------------------------------------------------------------------------
 * Reflection (SPEC 8.13): the tables describe the classes of the program (written by the generator); the functions that read, write and call members by name
 * are generated, they use the field helpers and dispatchers of the program.
 * --------------------------------------------------------------------------------------------------------------------- */
typedef struct RfMember {
    const char* name; const char* kind; const char* type; const char* access;
    uint8_t flags;                    /* 1 static, 2 readonly, 4 can read, 8 can write */
    const char* unit; const char* declared;
    const char* const* pnames; const char* const* ptypes; uint32_t pcount;
    Value (*attrs)(OwnList* list);    /* the attributes as [[name, values, texts], ...] (generated), null: none */
} RfMember;
typedef struct RfClass {
    const char* name; const char* base; uint8_t isActor;
    const char* const* ifaces; uint32_t nIfaces;
    const RfMember* members; uint32_t nMembers;
} RfClass;

/* Is the string equal to this UTF-8 text? */
FIRE_FN bool strIs(Value v, const char* utf8) {
    const Str* s;
    fire_char16 buf[160];
    uint32_t n;
    if (v.kind != K_String) return false;
    s = strOf(v);
    n = utf8ToUtf16(utf8, buf, 160);
    return n == s->length && memcmp(buf, s->data, n * sizeof(fire_char16)) == 0;
}
/* A string value made from UTF-8 text (owned by the temporary pool of `list`). */
FIRE_FN Value strFromUtf8(const char* utf8, OwnList* list) {
    fire_char16 buf[400];
    uint32_t n = utf8ToUtf16(utf8, buf, 400);
    return newStrFrom(buf, n, list);
}
/* UTF-16 -> UTF-8 into `out` (cut when it does not fit). */
FIRE_FN void strToUtf8(Value v, char* out, uint32_t cap) {
    uint32_t n = 0, i;
    if (v.kind == K_String) {
        const Str* s = strOf(v);
        for (i = 0; i < s->length && n + 4 < cap; i++) {
            uint32_t c = s->data[i];
            if (c >= 0xD800 && c < 0xDC00 && i + 1 < s->length) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
            if (c < 0x80) out[n++] = (char)c;
            else if (c < 0x800) { out[n++] = (char)(0xC0 | (c >> 6)); out[n++] = (char)(0x80 | (c & 0x3F)); }
            else if (c < 0x10000) { out[n++] = (char)(0xE0 | (c >> 12)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
            else { out[n++] = (char)(0xF0 | (c >> 18)); out[n++] = (char)(0x80 | ((c >> 12) & 0x3F)); out[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[n++] = (char)(0x80 | (c & 0x3F)); }
        }
    }
    out[n] = 0;
}
/* An array of strings. */
FIRE_FN Value strArrayOf(const char* const* items, uint32_t n, OwnList* list) {
    Arr* a = allocArr(n, list);
    uint32_t i;
    for (i = 0; i < n; i++) { arrItems(a)[i] = strFromUtf8(items[i], list); retain(arrItems(a)[i]); }
    return ArrV(a);
}
/* Generated: builds a ReflectionException (message) owned by the global scope. */
Value makeReflectError(Value message);
#endif

FIRE_FN const char* kindName(Value v) {
    switch (v.kind) {
        case K_Bool: return "Bool"; case K_Int: return "Int"; case K_Float: return "Float"; case K_Char: return "Char"; case K_String: return "String";
        case K_Class: return "Class"; case K_Lambda: return "Lambda"; case K_Pointer: return "Pointer"; case K_Array: return "Array"; case K_Buffer: return "Buffer";
        default: return "Undefined";
    }
}
/* Unit.Sqrt: the square root of a unit (`m^2` -> `m`, `cm^2` -> `cm`); false when an exponent is odd. A display symbol that ends in `^2` (and has no other
 * operator) loses it, otherwise the root has none. */
FIRE_FN bool unitSqrt(uint32_t u, uint32_t* out) {
    const UnitDef* x;
    int32_t dims[FIRE_NDIMS];
    char* display = NULL;
    uint32_t id;
    int i;
    if (unitIsUnitless(u)) { *out = u; return true; }
    x = &g_ud[u];
    for (i = 0; i < FIRE_NDIMS; i++) { if (x->dims[i] % 2 != 0) return false; dims[i] = x->dims[i] / 2; }
    if (x->display && unitLinear(u)) {
        size_t l = strlen(x->display), k;
        bool simple = l > 2 && strcmp(x->display + l - 2, "^2") == 0;
        for (k = 0; simple && k + 2 < l; k++) if (x->display[k] == '/' || x->display[k] == '*' || x->display[k] == '^') simple = false;
        if (simple) {
            display = (char*)malloc(l - 1);
            if (!display) outOfMemory();
            memcpy(display, x->display, l - 2);
            display[l - 2] = 0;
        }
    }
    id = unitIntern(dims, sqrt(x->scale), display);
    if (display && g_ud[id].display != display) free(display);   /* an equal unit existed */
    *out = id;
    return true;
}
/* `__MathCall(id, x[, y])` (fire.Standard.MathMethods): Sqrt (also of the unit), Round/Floor/Ceiling (the unit stays, an int stays), the angle and exponential
 * functions of a number without unit, Atan2 of two values of one dimension. */
FIRE_FN Value mathCall(int64_t method, Value x, Value y) {
    static const char* const names[] = {"", "Sqrt", "Round", "Floor", "Ceiling", "Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2", "Log", "Log10", "Exp"};
    const char* name = method >= 1 && method <= 14 ? names[method] : "?";
    char text[200];
    double v;
    if (x.kind != K_Int && x.kind != K_Float) {
        snprintf(text, sizeof text, "Math.%s expects a number, not a value of type '%s'.", name, kindName(x));
        return fireError("ArgumentException", text);
    }
    v = toR(x);
    switch (method) {
        case 1: {
            uint32_t root;
            if (!unitSqrt(x.unit, &root)) {
                snprintf(text, sizeof text, "Math.Sqrt: the unit '%s' has no square root.", g_ud[x.unit].name);
                return fireError("ArgumentException", text);
            }
            return Float((Real)sqrt(v), root);
        }
        case 2: case 3: case 4:
            if (x.kind == K_Int) return x;
            return Float((Real)(method == 2 ? round(v) : method == 3 ? floor(v) : ceil(v)), x.unit);
        case 11: {
            double yv;
            if (y.kind != K_Int && y.kind != K_Float) {
                snprintf(text, sizeof text, "Math.Atan2 expects two numbers, not a value of type '%s'.", kindName(y));
                return fireError("ArgumentException", text);
            }
            if (!unitCompatible(x.unit, y.unit)) {
                snprintf(text, sizeof text, "Math.Atan2 expects two values of the same dimension, not '%s' and '%s'.", g_ud[x.unit].name, g_ud[y.unit].name);
                return fireError("ArgumentException", text);
            }
            if (!unitConvert(v, x.unit, y.unit, &yv)) return Undef();
            return Float((Real)atan2(yv, (double)toR(y)));
        }
    }
    if (!unitIsUnitless(x.unit)) {
        snprintf(text, sizeof text, "Math.%s expects a number without unit, not one in '%s'.", name, g_ud[x.unit].name);
        return fireError("ArgumentException", text);
    }
    switch (method) {
        case 5: return Float((Real)sin(v));
        case 6: return Float((Real)cos(v));
        case 7: return Float((Real)tan(v));
        case 8: return Float((Real)asin(v));
        case 9: return Float((Real)acos(v));
        case 10: return Float((Real)atan(v));
        case 12: return Float((Real)log(v));
        case 13: return Float((Real)log10(v));
        case 14: return Float((Real)exp(v));
    }
    fatal("Unknown Math function.");
}
/* `x!Dog` that does not hold (OpCode.CastTo): the ArgumentException of the VM's text. */
FIRE_FN void castFailed(Value v, const char* typeName) {
    char text[200];
    snprintf(text, sizeof text, "Cannot convert a value of type '%s' to '%s'.", v.kind == K_Class ? className(asObj(v)->cls) : kindName(v), typeName);
    fireError("ArgumentException", text);
}

/* A parameter or field declared with a class was given something else (DeclaredType.Mismatch of the VM): an ArgumentException. */
FIRE_COLD FIRE_FN void guardClassFailed(Value v, const char* cls, const char* what) {
    char text[300];
    bool vowel = strchr("AEIOUaeiou", cls[0]) != NULL;
    snprintf(text, sizeof text, "%s is %s %s, not %s.", what, vowel ? "an" : "a", cls, v.kind == K_Class ? className(asObj(v)->cls) : kindName(v));
    fireError("ArgumentException", text);
}

/* A declaration with a type parameter (`T value`) whose type argument is known was given something else: "is a T (int), not String." */
FIRE_COLD FIRE_FN void guardTArgFailed(Value v, const char* param, const TArg* ta, const char* what) {
    char display[160];
    snprintf(display, sizeof display, "%s (%s)", param, ta->display);
    guardClassFailed(v, display, what);
}

/* A declaration with a type parameter: the value fits its type argument; a number of its kind (a whole number for a `float`) is converted into its unit
 * (another dimension: a UnitMismatchException, which leaves the unwinding set); false: it does not fit. */
FIRE_FN bool fitsTArg(Value* v, const TArg* ta) {
    if (v->kind == K_Undefined || ta->holds(*v) || (v->kind == K_Class && !leafAlive(*v))) return true;
    if (ta->kind != 0 && (v->kind == ta->kind || (ta->isFloat && v->kind == K_Int))) {
        if (ta->adapt) *v = ta->adapt(*v);
        else if (v->kind == K_Int && ta->isFloat) *v = Float((Real)v->i, v->unit);
        return true;
    }
    return false;
}

/* The type arguments of a `new` or a call that names a type parameter of the running code (`new Box<T>()`): the same list for the same arguments. The
 * generator passes a compound literal: internTArgsN((const TArg* const[]){&TA_1, &TA_2}, 2). */
typedef struct fire_TArgsEntry { struct fire_TArgsEntry* next; uint32_t n; const TArg* a[1]; } fire_TArgsEntry;
extern fire_TArgsEntry* g_targsHead;
FIRE_FN TArgs internTArgsN(const TArg* const* items, uint32_t n) {
    fire_TArgsEntry* e;
    uint32_t i;
    for (e = g_targsHead; e; e = e->next) {
        if (e->n != n) continue;
        i = 0;
        while (i < n && e->a[i] == items[i]) i++;
        if (i == n) return e->a;
    }
    e = (fire_TArgsEntry*)malloc(sizeof(fire_TArgsEntry) + (n > 0 ? n - 1 : 0) * sizeof(const TArg*));
    if (FIRE_UNLIKELY(!e)) allocFailed();
    e->n = n;
    for (i = 0; i < n; i++) e->a[i] = items[i];
    e->next = g_targsHead;
    g_targsHead = e;
    return e->a;
}

/* ---------------------------------------------------------------------------------------------------------------------
 * Exceptions (SPEC 7): throw, try/catch/finally and resume.
 *
 * A throw does not unwind by itself. `throwValue` walks the handler stack from the top (the innermost active `try`):
 *  * a handler with a matching catch clause runs that catch block *on top of the throw site* (a callback into the function that owns the `try`), so the frames
 *    of the throw site are still alive and `resume(value)` can continue there;
 *  * a handler without a match - or a finally-only handler - is not entered directly: the frames above its function are unwound first (status flag `g_unwind`,
 *    every generated function cleans up its scopes and returns), and its function then rethrows (after running its `finally`).
 * When a catch block ends it leaves through `g_unwind` as well: jump to the code after the `try`, `return`, or resume.
 * --------------------------------------------------------------------------------------------------------------------- */
typedef struct HandlerInfo {
    uint32_t ncatch;
    const int32_t* types;   /* per catch clause: the type id for excMatches, -1 = catches everything */
    uint8_t hasFinally;
} HandlerInfo;

struct Handler {
    Handler* prev;
    const HandlerInfo* info;
    void (*fn)(void* ctx, uint32_t catchIndex, Value exception);   /* runs the catch block (generated: a function that gets the frame of its function) */
    void* ctx;
    Handler* origin;      /* the handler this one stands for (a finally-only handler stands for the one that is running its catch) */
    uint8_t finallyOnly;
};

/* Generated: does the exception match the catch type (`typeId` as stored in HandlerInfo.types)? */
bool excMatches(Value exception, int32_t typeId);
/* Generated: builds an IndexOutOfBoundsException (message, index, length) owned by the global scope. */
Value makeIndexError(Value message, int64_t index, int64_t length);
/* Generated: builds a UnitMismatchException (message, expected unit, actual unit) owned by the global scope. */
Value makeUnitError(Value message, Value expected, Value actual);

FIRE_FN void pushHandler(Handler* h) { h->prev = g_handlers; h->origin = h; h->finallyOnly = 0; g_handlers = h; }
FIRE_FN void popHandler(void) { g_handlers = g_handlers->prev; }
FIRE_FN void clearUnwind(void) { g_unwind.active = 0; g_unwind.kind = UW_NONE; g_unwind.target = NULL; }
FIRE_FN void unwindTo(uint8_t kind, Handler* target, Value value, uint32_t label) {
    g_unwind.active = 1; g_unwind.kind = kind; g_unwind.target = target ? target->origin : NULL; g_unwind.value = value; g_unwind.label = label;
}
/* Leaving a catch block: continue at `label` in the function that owns the handler. */
FIRE_FN void exitJump(Handler* h, uint32_t label) { unwindTo(UW_JUMP, h, Undef(), label); }
/* `return` from inside a catch block: the function that owns the handler returns `value`. */
FIRE_FN void exitReturn(Handler* h, Value value) { unwindTo(UW_RETURN, h, value, 0); }

/* A throw whose catch block may still call resume (innermost record first). */
struct Pending {
    Pending* prev;
    Value exception;
    uint64_t token;
    uint8_t resumable;
    uint8_t cleared;
};

FIRE_FN void takeGlobal(Obj* o) {
    if (o->h.owner) ownUnlink(&o->h);
    ownLink(g_globalOwn, &o->h);
}

FIRE_COLD FIRE_FN void reportUnhandled(Value exception) {
    Obj* o;
    fflush(stdout);
    o = asObj(exception);
    fprintf(stderr, "Unhandled exception of class '%s'.\n", className(o->cls));
}

#ifdef FIRE_THREADS
FIRE_FN Value threadUnhandled(Value exception);
FIRE_FN Value jobUnhandled(Value exception);
#endif

/* `throw exception`. Returns the resume value if the exception is resumed at this very point; otherwise it returns with `g_unwind.active` set and the caller
 * has to unwind (the generated code checks the flag after every call). `throwValue(x)` is resumable, `throwValue(x, false)` (a rethrow) is not. */
FIRE_FN Value throwValueR(Value exception, bool resumable) {
    Handler* h;
    if (FIRE_UNLIKELY(exception.kind != K_Class)) fatal("throw expects an exception object.");
    takeGlobal(asObj(exception));
    h = g_handlers;
    if (!h) {   /* nothing catches it: the program ends here (like the VM, without unwinding the stack) */
#ifdef FIRE_THREADS
        if (g_jobDepth > 0) return jobUnhandled(exception);   /* a job of `fire global`: it ends, the exception goes to `catch threads` */
        if (g_isThread) return threadUnhandled(exception);   /* a fire thread ends; the exception goes to the main program */
#endif
        reportUnhandled(exception);
        exitNow(1);
    }
    g_handlers = h->prev;
    if (!h->finallyOnly) {
        int found = -1;
        uint32_t i;
        for (i = 0; i < h->info->ncatch && found < 0; i++)
            if (h->info->types[i] < 0 || excMatches(exception, h->info->types[i])) found = (int)i;
        if (found >= 0) {
            Pending pending;
            Handler fin;
            Handler* saved;
            pending.prev = g_pending; pending.exception = exception; pending.token = ++g_throwToken; pending.resumable = (uint8_t)(resumable ? 1 : 0); pending.cleared = 0;
            g_pending = &pending;
            if (h->info->hasFinally) {   /* while the catch block runs, an exception that leaves it must still run the finally */
                fin.prev = g_handlers; fin.info = h->info; fin.fn = NULL; fin.ctx = NULL; fin.origin = h->origin; fin.finallyOnly = 1;
                g_handlers = &fin;
            }
            saved = h->prev;
            h->fn(h->ctx, (uint32_t)found, exception);
            g_pending = pending.prev;
            if (h->info->hasFinally && g_handlers == &fin) g_handlers = fin.prev;   /* the catch block was left with its finally still armed */
            if (g_unwind.active && g_unwind.kind == UW_RESUME && g_unwind.token == pending.token) {
                Value v = g_unwind.value;
                clearUnwind();
                h->prev = saved;      /* the resumed code is still inside the try block: its handler is armed again */
                g_handlers = h;
                return v;
            }
            if (!g_unwind.active) fatal("internal error: a catch block ended without leaving.");
            return Undef();
        }
    }
    /* no catch clause here: unwind to the function of this handler, which rethrows (after its finally) */
    unwindTo(UW_RETHROW, h, exception, 0);
    return Undef();
}
FIRE_FN Value fire_throwValue1(Value exception) { return throwValueR(exception, true); }
#define throwValue(...) FIRE_EXPAND(FIRE_PICK2(__VA_ARGS__, throwValueR, fire_throwValue1, 0)(__VA_ARGS__))

/* `e.resume(value)`: continue at the throw that `e` belongs to, which then evaluates to `value`. */
FIRE_FN void resumeThrow(Value exception, Value value) {
    Pending* p;
    for (p = g_pending; p; p = p->prev)
        if (!p->cleared && p->exception.p == exception.p) {
            if (!p->resumable) fatal("resume() across a finally block or a rethrow is not supported by the native backend.");
            p->cleared = 1;
            retain(value);
            g_unwind.active = 1; g_unwind.kind = UW_RESUME; g_unwind.target = NULL; g_unwind.value = value; g_unwind.token = p->token;
            return;
        }
    fatal("resume() was called, but this exception is not being handled right now.");
}

/* The end of a catch block that did not resume: the throw site is given up. */
FIRE_FN void clearPending(Value exception) {
    Pending* p;
    for (p = g_pending; p; p = p->prev)
        if (!p->cleared && p->exception.p == exception.p) { p->cleared = 1; return; }
}

/* Run-time errors the language reports as exceptions (index out of range). */
FIRE_FN Value indexError(const char* what, int64_t index, int64_t length) {
    char text[120];
    int n = snprintf(text, sizeof text, "%s %lld out of range (length %lld).", what, (long long)index, (long long)length);
#ifdef FIRE_EXCEPTIONS
    OwnList unused;
    Str* msg;
    ownListInit(&unused, 0, NULL);
    msg = allocStr((uint32_t)n, &unused);
    widenAscii(text, (uint32_t)n, strChars(msg));
    return throwValue(makeIndexError(StrV(msg), index, length));
#elif defined(FIRE_LIBRARY)
    (void)n;
    return fireError("IndexOutOfBoundsException", text);   /* the VM throws it as the exception of the program */
#else
    (void)n;
    fatal(text);
#endif
}

#ifdef FIRE_EXCEPTIONS
FIRE_FN void unitConflictThrow(uint32_t from, uint32_t to) {
    char text[240];
    OwnList unused;
    fire_char16 buffer[240];
    Value msg, expected, actual;
    snprintf(text, sizeof text, "Incompatible units: '%s' cannot be converted to '%s'.", g_ud[from].name, g_ud[to].name);
    ownListInit(&unused, 0, NULL);
    msg = newStrFrom(buffer, utf8ToUtf16(text, buffer, 240), &unused);
    expected = newStrFrom(buffer, utf8ToUtf16(g_ud[to].name, buffer, 100), &unused);
    actual = newStrFrom(buffer, utf8ToUtf16(g_ud[from].name, buffer, 100), &unused);
    throwValue(makeUnitError(msg, expected, actual));
}
#endif

FIRE_FN void unitMismatch(Value v, Value expectedText) {
#ifdef FIRE_EXCEPTIONS
    uint32_t actualUnit = (v.kind == K_Int || v.kind == K_Float || v.kind == K_Undefined) ? v.unit : 0;
    fire_char16 actualText[96];
    uint32_t an = actualUnit ? utf8ToUtf16(g_ud[actualUnit].name, actualText, 80) : 0, n;
    const Str* expected = strOf(expectedText);
    OwnList unused;
    Str* msg;
    fire_char16* d;
    Value actual;
    if (!actualUnit) an = widenAscii("(no unit)", 9, actualText);
    ownListInit(&unused, 0, NULL);
    msg = allocStr(15 + expected->length + 7 + an + 1, &unused);
    d = strChars(msg);
    n = widenAscii("Expected unit '", 15, d);
    memcpy(d + n, expected->data, expected->length * sizeof(fire_char16)); n += expected->length;
    n += widenAscii("', got: ", 8, d + n);
    memcpy(d + n, actualText, an * sizeof(fire_char16)); n += an;
    d[n++] = (fire_char16)'.';
    msg->length = n;
    actual = newStrFrom(actualText, an, &unused);
    throwValue(makeUnitError(StrV(msg), expectedText, actual));
#else
    (void)v; (void)expectedText;
    fatal("Incompatible units.");
#endif
}

#ifndef FIRE_LIBRARY
#ifdef FIRE_EXCEPTIONS
/* Generated: the exception object of the class `cls` (a class that natives of packages throw: `exceptions` of the package) with the message, or an undefined
 * value. */
Value makeClassError(const char* cls, Value message);
#endif
/* An exception of the program that a native of a package throws (`fireError("TimeException", "text")`): thrown, or - when the program has no exceptions - the
 * end of the program. */
FIRE_FN Value fireError(const char* cls, const char* utf8) {
#ifdef FIRE_EXCEPTIONS
    OwnList unused;
    fire_char16 text[400];
    Value msg, exception;
    ownListInit(&unused, 0, NULL);
    msg = newStrFrom(text, utf8ToUtf16(utf8, text, 400), &unused);
    exception = makeClassError(cls, msg);
    if (exception.kind == K_Class) return throwValue(exception);
#else
    (void)cls;
#endif
    fatal(utf8);
}
#endif

/* The kind of a value as the VM names it (in the texts of the errors of calls and fields). */
FIRE_FN const char* valueKindText(Value v) { return kindName(v); }

/* A method that the value does not have (a class that has it with other numbers of arguments is reported by the dispatcher itself): a MissingMemberException. */
FIRE_COLD FIRE_FN Value missingMethod(Value self, const char* name, int argc) {
    char text[400];
    if (self.kind == K_Class) snprintf(text, sizeof text, "Method '%s' not found on '%s'.", name, className(asObj(self)->cls));
    else snprintf(text, sizeof text, "'%s' (%d argument(s)) is not a known built-in method on a value of type %s.", name, argc, valueKindText(self));
    return fireError("MissingMemberException", text);
}

/* A field that the value does not have: a MissingMemberException (the texts of the VM). */
FIRE_COLD FIRE_FN Value missingField(Value v, const char* name) {
    char text[400];
    if (v.kind == K_Class) snprintf(text, sizeof text, "Field '%s' does not exist on an instance of '%s' (and there is no 'get_%s' property either).", name, className(asObj(v)->cls), name);
    else if (v.kind == K_String) snprintf(text, sizeof text, "Strings have no field '%s' (only 'Length').", name);
    else if (v.kind == K_Array) snprintf(text, sizeof text, "Arrays have no field '%s' (only 'Length').", name);
    else {
        /* (not an object at all: an ArgumentException, like the VM) */
        if (v.kind == K_Buffer) snprintf(text, sizeof text, "Byte buffers have no field '%s' (only 'Length', 'littleEndian').", name);
        else snprintf(text, sizeof text, "Field access on a value of type %s which is not an object.", valueKindText(v));
        return fireError("ArgumentException", text);
    }
    return fireError("MissingMemberException", text);
}

/* An assignment to a field of something that is not an object: an ArgumentException (the text of the VM). */
FIRE_COLD FIRE_FN void notObjectAssign(Value v) {
    char text[200];
    snprintf(text, sizeof text, "Field assignment on a value of type %s which is not an object.", valueKindText(v));
    fireError("ArgumentException", text);
}

#ifdef FIRE_REFLECTION
/* A reflection call that cannot be done: a ReflectionException, or the end of the program when there are no exceptions. */
FIRE_FN Value rfFail(const char* utf8) {
#ifdef FIRE_EXCEPTIONS
    OwnList unused;
    Value msg;
    ownListInit(&unused, 0, NULL);
    msg = strFromUtf8(utf8, &unused);
    return throwValue(makeReflectError(msg));
#else
    fatal(utf8);
#endif
}
#endif

#ifdef FIRE_REFLECTION
/* ---- probes (SPEC 8.14): handlers for writes to the members of an object. An object with probes has flag 2; its setters look at them. ------- */
typedef void (*ProbeRawSet)(Value obj, Value value, OwnList* list, uint32_t caller);
typedef Value (*ProbeRawGet)(Value obj, OwnList* list);
/* A probe; on a computed property (a getter without a setter) `deps` (null-terminated) are the members its getter reads, `getter` evaluates it again and
 * `last` is its value when it was last evaluated. */
typedef struct ProbeEntry { int64_t id; char* member; bool changing; Value handler; const char* const* deps; ProbeRawGet getter; Value last; } ProbeEntry;
typedef struct ProbeNode {
    Obj* obj;
    ProbeEntry* entries; uint32_t n, cap;
    const char* running[8]; uint32_t nrunning;
    struct ProbeNode* next;
} ProbeNode;
extern ProbeNode* g_probeNodes;
extern int64_t g_probeNextId;

FIRE_FN ProbeNode* probeNodeOf(Obj* o, bool create) {
    ProbeNode* p;
    for (p = g_probeNodes; p; p = p->next) if (p->obj == o) return p;
    if (!create) return NULL;
    p = (ProbeNode*)calloc(1, sizeof(ProbeNode));
    if (!p) allocFailed();
    p->obj = o;
    p->next = g_probeNodes;
    g_probeNodes = p;
    o->h.flags |= 2;
    return p;
}
FIRE_FN void probeRemoveAt(ProbeNode* node, uint32_t i) {
    uint32_t k;
    release(node->entries[i].handler);
    release(node->entries[i].last);
    free(node->entries[i].member);
    for (k = i + 1; k < node->n; k++) node->entries[k - 1] = node->entries[k];
    node->n--;
}
FIRE_FN void probeFree(Obj* o) {
    ProbeNode** at;
    for (at = &g_probeNodes; *at; at = &(*at)->next)
        if ((*at)->obj == o) {
            ProbeNode* p = *at;
            *at = p->next;
            while (p->n) probeRemoveAt(p, p->n - 1);
            free(p->entries);
            free(p);
            return;
        }
}
/* A probe on `member` (null: every member); `deps`, `getter` and `last` only for a computed property (else null, null, Undef()). */
FIRE_FN int64_t probeAddEntryFull(Obj* o, const char* member, bool changing, Value handler, const char* const* deps, ProbeRawGet getter, Value last) {
    ProbeNode* node = probeNodeOf(o, true);
    ProbeEntry* e;
    if (node->n == node->cap) {
        node->cap = node->cap ? node->cap * 2 : 4;
        node->entries = (ProbeEntry*)realloc(node->entries, node->cap * sizeof(ProbeEntry));
        if (!node->entries) allocFailed();
    }
    e = &node->entries[node->n++];
    e->id = g_probeNextId++;
    e->member = NULL;
    if (member) { size_t l = strlen(member); e->member = (char*)malloc(l + 1); if (!e->member) allocFailed(); memcpy(e->member, member, l + 1); }
    e->changing = changing;
    e->handler = handler;
    retain(handler);
    e->deps = deps;
    e->getter = getter;
    e->last = last;
    retain(last);
    return e->id;
}
FIRE_FN int64_t probeAddEntry(Obj* o, const char* member, bool changing, Value handler) { return probeAddEntryFull(o, member, changing, handler, NULL, NULL, Undef()); }
/* `silence obj.member` (member null: all probes of the object). */
FIRE_FN void probeSilence(Obj* o, const char* member) {
    ProbeNode* node = probeNodeOf(o, false);
    uint32_t i;
    if (!node) return;
    for (i = node->n; i-- > 0;)
        if (!member || (node->entries[i].member && strcmp(node->entries[i].member, member) == 0)) probeRemoveAt(node, i);
}
/* `silence <handle>`: that probe only (a handle that is gone is no error). */
FIRE_FN void probeSilenceHandle(int64_t id) {
    ProbeNode* p;
    uint32_t i;
    for (p = g_probeNodes; p; p = p->next)
        for (i = 0; i < p->n; i++) if (p->entries[i].id == id) { probeRemoveAt(p, i); return; }
}

/* The handler gets (new), (old, new), (object, old, new) or (object, name, old, new) by its number of parameters. */
FIRE_FN Value probeRun(const ProbeEntry* e, Value obj, const char* name, Value oldValue, Value newValue, OwnList* list) {
    Value handler = e->handler;
    uint32_t np = lamOf(handler)->nparams;
    Value args[4];
    Value r;
    int argc;
    if (np == 0) argc = 0;
    else if (np == 1) { args[0] = newValue; argc = 1; }
    else if (np == 2) { args[0] = oldValue; args[1] = newValue; argc = 2; }
    else if (np == 3) { args[0] = obj; args[1] = oldValue; args[2] = newValue; argc = 3; }
    else { args[0] = obj; args[1] = strFromUtf8(name, list); args[2] = oldValue; args[3] = newValue; argc = 4; }
    retain(handler);   /* a handler that silences itself must stay alive until it returns */
    r = callLam(handler, argc, args, list);
    release(handler);
    adopt(r, list);
    return r;
}

FIRE_FN void fire_probeDone(ProbeNode* node, const char* name) {
    uint32_t i;
    for (i = 0; i < node->nrunning; i++) if (node->running[i] == name) { node->running[i] = node->running[--node->nrunning]; break; }
}

/* `obj.member = value` on an object with probes: the `changing` handlers (one that returns `false` cancels the write), then the write, then - when the value
 * changed - the `changed` handlers. A handler that writes the same member does not fire it again. */
FIRE_FN void probedSet(Value obj, const char* name, Value value, ProbeRawSet raw, ProbeRawGet get, uint32_t caller) {
    Obj* o = asObj(obj);
    ProbeNode* node = probeNodeOf(o, false);
    OwnList local;
    bool nested = false, cancelled = false;
    Value oldValue;
    uint32_t i;
    ownListInit(&local, poolMark(), NULL);
    if (node) for (i = 0; i < node->nrunning; i++) if (strcmp(node->running[i], name) == 0) nested = true;
    if (!node || nested || node->nrunning >= 8) { raw(obj, value, &local, caller); leave(&local); return; }
    node->running[node->nrunning++] = name;
    oldValue = get(obj, &local);
    for (i = 0; i < node->n && !g_unwind.active; i++) {
        ProbeEntry e = node->entries[i];
        Value verdict;
        if (!e.changing || (e.member && strcmp(e.member, name) != 0)) continue;
        verdict = probeRun(&e, obj, name, oldValue, value, &local);
        if (g_unwind.active) break;
        if (verdict.kind == K_Bool && !verdict.i) { cancelled = true; break; }
    }
    if (!g_unwind.active && !cancelled) {
        raw(obj, value, &local, caller);
        if (!g_unwind.active && !eq(oldValue, value))
            for (i = 0; i < node->n && !g_unwind.active; i++) {
                ProbeEntry e = node->entries[i];
                if (e.changing || (e.member && strcmp(e.member, name) != 0)) continue;
                probeRun(&e, obj, name, oldValue, value, &local);
            }
        /* the probes on computed properties that read this member: their getter again, `changed` when the value is another */
        for (i = 0; i < node->n && !g_unwind.active; i++) {
            ProbeEntry e = node->entries[i];
            bool depends = false, running = false;
            const char* const* d;
            uint32_t k;
            Value now;
            if (e.deps) for (d = e.deps; *d; d++) if (strcmp(*d, name) == 0) { depends = true; break; }
            if (!depends) continue;
            for (k = 0; k < node->nrunning; k++) if (strcmp(node->running[k], e.member) == 0) running = true;
            if (running || node->nrunning >= 8) continue;
            node->running[node->nrunning++] = e.member;
            now = e.getter(obj, &local);
            if (!g_unwind.active && !eq(e.last, now)) {
                Value old = e.last;
                retain(now);
                node->entries[i].last = now;   /* (before the handler: it may add probes and move the entries) */
                probeRun(&e, obj, e.member, old, now, &local);
                release(old);
            }
            fire_probeDone(node, e.member);
        }
    }
    fire_probeDone(node, name);
    leave(&local);
}
#endif

/* A message of the runtime as a string value of the exceptions (owned by the temporary pool). */
FIRE_FN Value fire_asciiMessage(const char* text) {
    OwnList unused;
    uint32_t n = (uint32_t)strlen(text);
    Str* msg;
    ownListInit(&unused, 0, NULL);
    msg = allocStr(n, &unused);
    widenAscii(text, n, strChars(msg));
    return StrV(msg);
}

/* Generated: builds an AccessDeniedException (message) owned by the global scope. */
Value makeAccessError(Value message);

/* A private or protected member used from where it may not be (SPEC 5.7): an AccessDeniedException, or the end of the program when there are no exceptions. */
FIRE_FN Value accessDenied(const char* text) {
#ifdef FIRE_EXCEPTIONS
    return throwValue(makeAccessError(fire_asciiMessage(text)));
#else
    fatal(text);
#endif
}

/* ---------------------------------------------------------------------------------------------------------------------
 * Time in the runtime: ticks of 100 ns like in the VM; used by `Sleep` and the other time bridge functions, by the waiting functions of the devices and by
 * `#timeout`.
 * --------------------------------------------------------------------------------------------------------------------- */
#define TICKS_PER_MS ((int64_t)10000)

/* Generated: the `ticks` of a TimeSpan (any class with a whole number field `ticks`); false for another object (and for every object when the program has
 * none). */
bool timeObjTicks(Value v, int64_t* ticks);

/* Is the unit a unit of time (the dimension `s` and nothing else)? `factor` is then its size in seconds. */
FIRE_FN bool unitOfTime(uint32_t u, double* factor) {
    int s = -1, i;
    for (i = 0; i < FIRE_NDIMS; i++) if (strcmp(g_dimNames[i], "s") == 0) s = i;
    if (s < 0 || g_ud[u].dims[s] != 1) return false;
    for (i = 0; i < FIRE_NDIMS; i++) if (i != s && g_ud[u].dims[i] != 0) return false;
    *factor = g_ud[u].scale;
    return true;
}

/* A time as ticks, the way the waiting functions take it: a TimeSpan, a number with a unit of time (`500ms`), or a number (milliseconds). False for anything
 * else. */
FIRE_FN bool timeTicksOf(Value a, int64_t* ticks) {
    double number, perSecond;
    if (a.kind == K_Class) return timeObjTicks(a, ticks);
    if (a.kind != K_Int && a.kind != K_Float) return false;
    number = a.kind == K_Int ? (double)a.i : (double)a.f;
    if (unitIsUnitless(a.unit)) { *ticks = (int64_t)nearbyint(number * (double)TICKS_PER_MS); return true; }
    if (!unitOfTime(a.unit, &perSecond)) return false;
    *ticks = (int64_t)nearbyint(number * perSecond * 1e7);
    return true;
}

/* How long a waiting function waits without a time of its own: `#timeout`, else 30 seconds. */
extern int64_t g_defaultTimeoutTicks;

#ifdef FIRE_THREADS
FIRE_FN void sleepTicks(int64_t ticks);
#else
FIRE_FN void sleepTicks(int64_t ticks) { if (ticks > 0) fire_plat_sleepMs((ticks + TICKS_PER_MS - 1) / TICKS_PER_MS); }
#endif

/* `#timeout value`: the program has to give a time (the VM fails with an InvalidOperationException; here the program ends). */
FIRE_FN void setDefaultTimeout(Value t) {
    int64_t ticks;
    if (!timeTicksOf(t, &ticks)) fatal("#timeout expects a TimeSpan, a time value or milliseconds.");
    g_defaultTimeoutTicks = ticks;
}

/* Generated: builds a DestroyedException (message) owned by the global scope. */
Value makeDestroyedError(Value message);

/* Generated: builds a DivideByZeroException (message) owned by the global scope. */
Value makeDivideError(Value message);

FIRE_FN Value divideByZero(void) {
#ifdef FIRE_EXCEPTIONS
    return throwValue(makeDivideError(fire_asciiMessage("Division by zero.")));
#elif defined(FIRE_LIBRARY)
    return fireError("DivideByZeroException", "Division by zero.");
#else
    fatal("Division by zero.");
#endif
}

FIRE_FN Value destroyedError(Value leaf) {
    const char* text = leaf.kind == K_Buffer ? "Access to a destroyed buffer." : leaf.kind == K_Class ? "Access to a destroyed object." : "Access to a destroyed array.";
#ifdef FIRE_EXCEPTIONS
    return throwValue(makeDestroyedError(fire_asciiMessage(text)));
#else
    fatal(text);
#endif
}

/* ---------------------------------------------------------------------------------------------------------------------
 * Arrays and byte buffers
 * --------------------------------------------------------------------------------------------------------------------- */

/* `a[i]` for arrays, buffers and strings (objects with a GetIndex method are handled by the generated code). */
FIRE_FN Value arrayGet(Value a, Value i) {
    char text[160];
    if (FIRE_LIKELY(a.kind == K_Array && i.kind == K_Int)) {
        Arr* arr;
        if (FIRE_UNLIKELY(!leafAlive(a))) return destroyedError(a);
        arr = arrOf(a);
        if (FIRE_UNLIKELY((uint64_t)i.i >= arr->length)) return indexError("Array index", i.i, arr->length);
        return arrItems(arr)[i.i];
    }
    if (i.kind != K_Int) fatal("An index must be an int.");
    if (a.kind == K_Buffer) {
        Buf* b;
        if (FIRE_UNLIKELY(!leafAlive(a))) return destroyedError(a);
        b = bufOf(a);
        if ((uint64_t)i.i >= b->length) return indexError("Array index", i.i, b->length);
        return Int(b->data[i.i]);
    }
    if (a.kind == K_String) {
        const Str* s = strOf(a);
        if ((uint64_t)i.i >= s->length) return indexError("String index", i.i, s->length);
        return Char(s->data[i.i]);
    }
    snprintf(text, sizeof text, "Index access ('[]') on a value of type %s is not possible (neither an array nor a class with a 'GetIndex' method).", coerceKindName(a.kind));
    fireError("ArgumentException", text);
    return Undef();
}

/* `a[i] = v`; the array holds a count of the new element and lets go of the old one. */
FIRE_FN void arraySet(Value a, Value i, Value v) {
    char text[160];
    if (FIRE_LIKELY(a.kind == K_Array && i.kind == K_Int)) {
        Arr* arr;
        Value old;
        if (FIRE_UNLIKELY(!leafAlive(a))) { destroyedError(a); return; }
        arr = arrOf(a);
        if (FIRE_UNLIKELY((uint64_t)i.i >= arr->length)) { indexError("Array index", i.i, arr->length); return; }
        old = arrItems(arr)[i.i];
        arrItems(arr)[i.i] = v;
        retain(v);
        release(old);
        return;
    }
    if (i.kind != K_Int) fatal("An index must be an int.");
    if (a.kind == K_Buffer) {
        Buf* b;
        if (v.kind != K_Int) fatal("Assigning to a byte buffer expects an int value.");
        if (FIRE_UNLIKELY(!leafAlive(a))) { destroyedError(a); return; }
        b = bufOf(a);
        if ((uint64_t)i.i >= b->length) { indexError("Array index", i.i, b->length); return; }
        b->data[i.i] = (uint8_t)v.i;
        return;
    }
    if (a.kind == K_String) { fireError("ArgumentException", "Strings are immutable - 's[i] = ...' is not possible (Replace/Substring return a new string)."); return; }
    snprintf(text, sizeof text, "Index assignment ('[]=') on a value of type %s is not possible (neither an array nor a class with a 'SetIndex' method).", coerceKindName(a.kind));
    fireError("ArgumentException", text);
}

/* ---------------------------------------------------------------------------------------------------------------------
 * `ref` parameters (SPEC 5.4.2): the argument is a pointer to the variable, the field or the element of the caller.
 * A pointer is a K_Pointer value; `width` 0: a variable or a field (`unit` = the offset in elements: such a pointer is an "array" with one element), 1: an
 * element of a byte buffer, 2: an element of an array (1 and 2 carry the handle of the array: `unit` = slot, `reserved` = generation).
 * --------------------------------------------------------------------------------------------------------------------- */
FIRE_FN Value PtrV(Value* p) { Value r; r.kind = K_Pointer; r.width = 0; r.reserved = 0; r.unit = 0; r.p = p; return r; }
FIRE_FN Value BytePtrV(uint8_t* p, const Owned* buf) { Value r; r.kind = K_Pointer; r.width = 1; r.reserved = buf->gen; r.unit = buf->slot; r.p = p; return r; }
FIRE_FN Value ElemPtrV(Value* p, const Owned* arr) { Value r; r.kind = K_Pointer; r.width = 2; r.reserved = arr->gen; r.unit = arr->slot; r.p = p; return r; }

/* May the pointer be used (dereferenced)? Not outside its range: a pointer to a variable or a field only at offset 0, a pointer into an array or a buffer only
 * inside it - and not when the array or buffer is destroyed. Otherwise the exception is thrown (`err` has the value of the throw) and false is returned. With
 * `#performance` nothing is checked. */
FIRE_FN bool ptrValid(Value p, Value* err) {
#ifdef FIRE_UNCHECKED
    (void)p; (void)err;
    return true;
#else
    if (p.width == 0) {
        if (FIRE_UNLIKELY((int32_t)p.unit != 0)) { *err = indexError("Pointer offset", (int32_t)p.unit, 1); return false; }
        return true;
    }
#ifdef FIRE_PTRBOUNDS
    {
        Owned* h;
        int64_t index, length;
        if (FIRE_UNLIKELY(g_slots.gen[p.unit] != p.reserved)) {
            Value leaf = Undef();
            leaf.kind = p.width == 1 ? K_Buffer : K_Array;
            *err = destroyedError(leaf);
            return false;
        }
        h = g_slots.hdr[p.unit];
        if (p.width == 1) { Buf* b = (Buf*)h; index = (int64_t)((intptr_t)p.p - (intptr_t)b->data); length = b->length; }
        else { Arr* a = (Arr*)h; index = (int64_t)(((intptr_t)p.p - (intptr_t)arrItems(a)) / (intptr_t)sizeof(Value)); length = a->length; }
        if (FIRE_UNLIKELY((uint64_t)index >= (uint64_t)length)) { *err = indexError("Array index", index, length); return false; }
    }
#else
    (void)err;
#endif
    return true;
#endif
}

FIRE_FN Value ptrRead(Value p) {
    Value err;
    if (FIRE_UNLIKELY(p.kind != K_Pointer)) fatal("Dereference of a value that is not a pointer.");
    if (FIRE_UNLIKELY(!ptrValid(p, &err))) return err;
    return p.width == 1 ? Int(*(const uint8_t*)p.p) : *(const Value*)p.p;
}

/* `p - q` for two pointers into the same array, buffer, variable or field: the number of elements between them. */
FIRE_FN Value ptrDiff(Value a, Value b) {
    if (a.width != b.width || (a.width == 0 ? a.p != b.p : (a.unit != b.unit || a.reserved != b.reserved)))
        fatal("Pointer difference ('ptr1 - ptr2') needs two pointers into the same array or the same variable.");
    if (a.width == 0) return Int((int64_t)(int32_t)a.unit - (int64_t)(int32_t)b.unit);
    return Int((int64_t)(((intptr_t)a.p - (intptr_t)b.p) / (a.width == 1 ? (intptr_t)1 : (intptr_t)sizeof(Value))));
}

/* `*p = v`: the storage holds a count of what it holds now. */
FIRE_FN void ptrWrite(Value p, Value v) {
    Value err, old;
    Value* target;
    if (FIRE_UNLIKELY(p.kind != K_Pointer)) fatal("Assignment through a value that is not a pointer.");
    if (FIRE_UNLIKELY(!ptrValid(p, &err))) return;
    if (p.width == 1) { *(uint8_t*)p.p = (uint8_t)v.i; return; }
    target = (Value*)p.p;
    old = *target;
    *target = v;
    retain(v);
    release(old);
}

/* A call site that passes an address to a method of which not every implementation takes a `ref`: the others get the value. */
FIRE_FN Value derefArg(Value v) { return v.kind == K_Pointer ? ptrRead(v) : v; }

FIRE_FN void requireRef(Value v, const char* name) {
    if (FIRE_UNLIKELY(v.kind != K_Pointer)) {
        fflush(stdout);
        fprintf(stderr, "fire runtime error: Parameter '%s' is declared 'ref': pass a variable, a field or an array element, not a value.\n", name);
        exitNow(1);
    }
}

/* `x[i]` as a `ref` argument. */
FIRE_FN Value addressOfIndex(Value a, Value i) {
    if (i.kind != K_Int) fatal("An index must be an int.");
    if (a.kind == K_Array) {
        Arr* arr;
        if (FIRE_UNLIKELY(!leafAlive(a))) return destroyedError(a);
        arr = arrOf(a);
        if ((uint64_t)i.i >= arr->length) return indexError("Array index", i.i, arr->length);
        return ElemPtrV(&arrItems(arr)[i.i], &arr->h);
    }
    if (a.kind == K_Buffer) {
        Buf* b;
        if (FIRE_UNLIKELY(!leafAlive(a))) return destroyedError(a);
        b = bufOf(a);
        if ((uint64_t)i.i >= b->length) return indexError("Array index", i.i, b->length);
        return BytePtrV(&b->data[i.i], &b->h);
    }
    fatal("A 'ref' argument 'x[i]' expects an array or a byte buffer.");
}

FIRE_FN Value lengthOf(Value v, bool* ok) {
    *ok = true;
    if (v.kind == K_Array) { if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v); return Int(arrOf(v)->length); }
    if (v.kind == K_String) return Int(strOf(v)->length);
    if (v.kind == K_Buffer) { if (FIRE_UNLIKELY(!leafAlive(v))) return destroyedError(v); return Int(bufOf(v)->length); }
    *ok = false;
    return Undef();
}

FIRE_FN Value newArray(Value size, OwnList* list) {
    if (size.kind != K_Int || size.i < 0 || size.i > 0x7FFFFFFF) fatal("An array size must be a non-negative int.");
    return ArrV(allocArr((uint32_t)size.i, list));
}

/* ---- The conversions of strings, chars, bytes and buffers (SPEC 8.10) -------------------------------------------------- */
FIRE_FN void checkCharWidth(Value width) {
    if (FIRE_UNLIKELY(width.kind != K_Int || width.i < 1 || width.i > 4)) fatal("Invalid character width (allowed: 1-4 bytes per character).");
}
FIRE_FN void writeCodeUnit(uint8_t* dest, uint32_t value, int width, bool little) {
    int b;
    for (b = 0; b < width; b++) dest[b] = (uint8_t)((value >> (little ? b * 8 : (width - 1 - b) * 8)) & 0xFF);
}
FIRE_FN uint32_t readCodeUnit(const uint8_t* src, int width, bool little) {
    uint32_t v = 0;
    int b;
    for (b = 0; b < width; b++) v |= (uint32_t)src[b] << (little ? b * 8 : (width - 1 - b) * 8);
    return v & 0xFFFF;   /* a char is one 16-bit code unit */
}
/* `string.ToBytes()`: one byte per character (the lowest byte). */
FIRE_FN Value strToBytes(Value text, OwnList* list) {
    const Str* s = strOf(text);
    Buf* b = allocBuf(s->length, list);
    uint32_t i;
    for (i = 0; i < s->length; i++) b->data[i] = (uint8_t)s->data[i];
    return BufV(b);
}
/* `string.ToUnicode(width)` */
FIRE_FN Value strToUnicode(Value text, Value width, OwnList* list) {
    const Str* s;
    Buf* b;
    uint32_t i;
    checkCharWidth(width);
    s = strOf(text);
    b = allocBuf(s->length * (uint32_t)width.i, list);
    for (i = 0; i < s->length; i++) writeCodeUnit(b->data + i * (uint32_t)width.i, s->data[i], (int)width.i, b->little);
    return BufV(b);
}
/* `char.ToUnicode(width)` */
FIRE_FN Value charToUnicode(Value c, Value width, OwnList* list) {
    Buf* b;
    checkCharWidth(width);
    b = allocBuf((uint32_t)width.i, list);
    writeCodeUnit(b->data, (uint32_t)c.i, (int)width.i, b->little);
    return BufV(b);
}
FIRE_FN Value charToByte(Value c) { return Int((uint8_t)c.i); }
FIRE_FN Value byteToChar(Value v) { return Char((uint8_t)v.i); }
/* `buffer.ToString()`: one character per byte. */
FIRE_FN Value bufToString(Value v, OwnList* list) {
    Buf* b;
    Str* r;
    fire_char16* d;
    uint32_t i;
    if (!ownedOf(v)) return destroyedError(v);
    b = bufOf(v);
    r = allocStr(b->length, list);
    d = strChars(r);
    for (i = 0; i < b->length; i++) d[i] = b->data[i];
    return StrV(r);
}
/* `buffer.ToUnicode(width)` */
FIRE_FN Value bufToUnicode(Value v, Value width, OwnList* list) {
    Buf* b;
    uint32_t count, i;
    Str* r;
    fire_char16* d;
    if (!ownedOf(v)) return destroyedError(v);
    checkCharWidth(width);
    b = bufOf(v);
    if (FIRE_UNLIKELY(b->length % (uint32_t)width.i != 0)) fatal("The buffer size is not a multiple of the character width.");
    count = b->length / (uint32_t)width.i;
    r = allocStr(count, list);
    d = strChars(r);
    for (i = 0; i < count; i++) d[i] = (fire_char16)readCodeUnit(b->data + i * (uint32_t)width.i, (int)width.i, b->little);
    return StrV(r);
}
/* `buffer.ToUnicodeChar(width)`: the first `width` bytes as one character. */
FIRE_FN Value bufToUnicodeChar(Value v, Value width) {
    Buf* b;
    if (!ownedOf(v)) return destroyedError(v);
    checkCharWidth(width);
    b = bufOf(v);
    if (FIRE_UNLIKELY(b->length < (uint32_t)width.i)) fatal("The buffer is too small for the character width.");
    return Char(readCodeUnit(b->data, (int)width.i, b->little));
}
/* `buffer.ToLittleEndian()` / `ToBigEndian()`: a copy, mirrored as one block when the order differs. */
FIRE_FN Value bufToEndian(Value v, bool little, OwnList* list) {
    Buf* b;
    Buf* c;
    uint32_t i;
    if (!ownedOf(v)) return destroyedError(v);
    b = bufOf(v);
    c = allocBuf(b->length, list);
    if ((b->little != 0) == little) memcpy(c->data, b->data, b->length);
    else for (i = 0; i < b->length; i++) c->data[i] = b->data[b->length - 1 - i];
    c->little = little ? 1 : 0;
    return BufV(c);
}

FIRE_FN Value newBuffer(Value size, OwnList* list) {
    if (size.kind != K_Int || size.i < 0 || size.i > 0x7FFFFFFF) fatal("A buffer size must be a non-negative int.");
    return BufV(allocBuf((uint32_t)size.i, list));
}

/* ---------------------------------------------------------------------------------------------------------------------
 * Ownership of arrays and buffers (SPEC 2.5): inner arrays, Take..., delete
 * --------------------------------------------------------------------------------------------------------------------- */
FIRE_FN int64_t sizeArg(Value v) {
    if (FIRE_UNLIKELY(v.kind != K_Int)) fatal("An array size must be an int.");
    return v.i;
}

FIRE_FN OwnList* newPartsList(Owned* holder) {
    OwnList* parts = (OwnList*)malloc(sizeof(OwnList));
    if (FIRE_UNLIKELY(!parts)) allocFailed();
    ownListInit(parts, 0, holder);
    return parts;
}

FIRE_FN void fillJagged(Arr* arr, const int64_t* sizes, int ranks, int level, OwnList* parts) {
    uint32_t i;
    for (i = 0; i < arr->length; i++) {
        Arr* inner = allocArr((uint32_t)sizes[level], parts);
        arrItems(arr)[i] = ArrV(inner);
        if (level + 1 < ranks) fillJagged(inner, sizes, ranks, level + 1, parts);
    }
}

/* `new T[a][b]...`: the inner arrays belong to the outer one. */
FIRE_FN Value newJagged(const int64_t* sizes, int ranks, OwnList* list) {
    Arr* outer;
    int i;
    for (i = 0; i < ranks; i++)
        if (sizes[i] < 0 || sizes[i] > 0x7FFFFFFF) fatal("An array size must be a non-negative int.");
    outer = allocArr((uint32_t)sizes[0], list);
    if (ranks > 1 && outer->length > 0) {
        outer->parts = newPartsList(&outer->h);
        fillJagged(outer, sizes, ranks, 1, outer->parts);
    }
    return ArrV(outer);
}

/* An array that was made inside the expression of its outer array (`[[1, 2], [3]]`) belongs to it now. */
FIRE_FN void attachPart(Value outer, Value inner) {
    Owned* o = ownedOf(inner);
    Arr* arr;
    if (!o) return;
    arr = arrOf(outer);
    if (o->owner) ownUnlink(o);
    if (!arr->parts) arr->parts = newPartsList(&arr->h);
    ownLink(arr->parts, o);
}

/* Gives an object, array or buffer to the object `target` (TakeTo, a direct field assignment): when the target is already being destroyed the value goes with
 * it at once (SPEC 2.2); an object must not become an owner of itself or of its owners. */
FIRE_FN void takeToObject(Owned* o, Obj* target) {
    if (target->h.flags & 1) {
        if (o->owner) ownUnlink(o);
        if (o->okind == O_Object) destroy((Obj*)o); else destroyLeaf(o);
        return;
    }
    if (o->okind == O_Object) {
        Owned* x = &target->h;
        for (;;) {
            OwnList* l;
            if (x == o) fatal("TakeTo: cycle detected - the target object is already owned (directly or transitively) by this object.");
            l = x->owner;
            if (!l || !l->holder) break;
            x = l->holder;
        }
    }
    if (o->owner) ownUnlink(o);
    ownLink(&target->owned, o);
}

/* `object is from owner` / `object is under owner` (SPEC 6): the owner of the object is that object - directly, or anywhere up the chain. */
FIRE_FN bool isFrom(Value operand, Value owner, bool transitive) {
    Owned* target;
    OwnList* l;
    if (FIRE_UNLIKELY(operand.kind != K_Class || owner.kind != K_Class)) fatal("'is from'/'is under' expects objects.");
    target = &asObj(owner)->h;
    for (l = asObj(operand)->h.owner; l; l = l->holder->owner) {
        if (l->holder == target) return true;
        if (!transitive || !l->holder) return false;
    }
    return false;
}

/* `obj.field = <fresh value>`: the value belongs to the object - when it is still in the hands of this function (it has no owner or is in one of the
 * function's scopes `lists`); something that belongs to others (an object, the caller) stays where it is (SPEC 2.1). The generator passes the scopes as a
 * compound literal: ownValueN(o, v, (OwnList* const[]){&s0, &s1}, 2). */
FIRE_FN Value ownValueN(Value owner, Value v, OwnList* const* lists, int n) {
    Owned* o = ownedOf(v);
    if (o) {
        bool mine = !o->owner;
        int i;
        for (i = 0; i < n && !mine; i++) if (o->owner == lists[i]) mine = true;
        if (mine) takeToObject(o, asObj(owner));
    }
    return v;
}
/* (the frequent case: the scopes are one list) */
FIRE_FN Value ownValue1(Value owner, Value v, OwnList* list) { return ownValueN(owner, v, &list, 1); }

/* `x = value` with a variable of an outer scope: what belongs to the inner scope `from` moves to the function scope `to` (SPEC 2.1) - never out of the
 * function. */
FIRE_FN void hoistFrom(Value v, OwnList* from, OwnList* to) {
    Owned* o = ownedOf(v);
    if (o && o->owner == from) { ownUnlink(o); ownLink(to, o); }
}

#define AL_MARK 0xFFFFFFFEu   /* an argument list (`mark` of a scope is the height of the temporary pool) */

/* `take x` (SPEC 2.2): false - and a DestroyedException - for a dead object, array or buffer; anything else (numbers, strings, ...) has no owner and is let
 * through. */
FIRE_FN bool takeAlive(Value v) {
    if ((v.kind == K_Class || v.kind == K_Array || v.kind == K_Buffer) && !leafAlive(v)) { destroyedError(v); return false; }
    return true;
}

/* `take x` into a scope or into the parts of an array (`list`): unconditional, whoever owned the value before; the ownership stays a tree. */
FIRE_FN void takeToList(Value v, OwnList* list) {
    Owned* o = ownedOf(v);
    Owned* x;
    if (!o || o->owner == list) return;
    for (x = list->holder; x;) {
        OwnList* l;
        if (x == o) fatal("take: cycle detected - the holder is already owned (directly or transitively) by this value.");
        l = x->owner;
        if (!l) break;
        x = l->holder;
    }
    if (o->owner) ownUnlink(o);
    ownLink(list, o);
}

/* `holder.field = take x` and `array[i] = take x`: the object (or the array) owns the value from now on. */
FIRE_FN void takeToHolder(Value holder, Value v) {
    Owned* o = ownedOf(v);
    Owned* h = ownedOf(holder);
    Arr* a;
    if (!h || h->okind == O_Buffer) { if (!h && holder.kind != K_Buffer) fatal("take: the holder of the value has to be an object or an array."); return; }
    if (!o) return;
    if (h->okind == O_Object) { takeToObject(o, (Obj*)h); return; }
    a = (Arr*)h;
    if (!a->parts) a->parts = newPartsList(&a->h);
    takeToList(v, a->parts);
}

/* `f(take x)`: x belongs to the callee, whoever owned it before: it travels in the argument list `al` of the call (see `reownArg`). */
FIRE_FN void takeArg(Value v, OwnList* from, OwnList* al) {
    Owned* o = ownedOf(v);
    if (o) { if (o->owner) ownUnlink(o); ownLink(al, o); }
    al->parent = from;
    al->mark = AL_MARK;
    al->depth = g_callDepth;
}

/* `f(g())`: the result of a call passed straight on belongs to the callee, not to the caller (SPEC 2.1). The generated code lets the value travel in the
 * argument list `al` of the call and destroys what is left in it afterwards (the callee has moved, stored or returned what it wants to keep). */
FIRE_FN void reownArg(Value v, OwnList* from, OwnList* al) {
    Owned* o = ownedOf(v);
    if (o && o->owner == from) { ownUnlink(o); ownLink(al, o); }
    al->parent = from;
    al->mark = AL_MARK;
    al->depth = g_callDepth;
}
FIRE_FN void finishArgs(Value result, OwnList* al) {
    transferOut(result, al);
    if (al->head) destroyList(al);
}

/* ---------------------------------------------------------------------------------------------------------------------
 * What travels along: `return` and `Takes.*` (SPEC 2.2, 2.3)
 * --------------------------------------------------------------------------------------------------------------------- */
enum { TK_THIS = 0, TK_CHILDREN = 1, TK_LOCALS = 2, TK_ALL = 3 };   /* the values of the enum `Takes` */
#define F_VISIT ((uint8_t)128)   /* (while walking the graph of references) */

/* The scopes of the running call (their lists): what belongs to one of them - also through objects that belong to them - is "local". */
typedef struct LocalScopes { OwnList* const* lists; int n; } LocalScopes;

/* The list of the scope that a thing finally belongs to (through the objects that own it); null for what travels. */
FIRE_FN OwnList* scopeListOf(const Owned* o) {
    OwnList* l = o->owner;
    while (l && l->holder) l = l->holder->owner;
    return l;
}
FIRE_FN bool isLocalNode(const Owned* o, const LocalScopes* s) {
    OwnList* l = scopeListOf(o);
    int i;
    if (!l) return false;
    for (i = 0; i < s->n; i++) if (s->lists[i] == l) return true;
    return false;
}
/* `ancestor` is `o` or stands in its chain of owners. */
FIRE_FN bool ownsTransitively(const Owned* ancestor, const Owned* o) {
    const Owned* a = o;
    while (a) {
        OwnList* l;
        if (a == ancestor) return true;
        l = a->owner;
        a = l ? l->holder : NULL;
    }
    return false;
}

/* A growing stack of headers (free it with ownedStackFree). */
typedef struct OwnedStack { Owned** items; size_t count, cap; } OwnedStack;
FIRE_FN void ownedStackPush(OwnedStack* s, Owned* o) {
    if (s->count == s->cap) {
        s->cap = s->cap ? s->cap * 2 : 64;
        s->items = (Owned**)realloc(s->items, s->cap * sizeof(Owned*));
        if (!s->items) allocFailed();
    }
    s->items[s->count++] = o;
}
FIRE_FN void ownedStackFree(OwnedStack* s) { free(s->items); s->items = NULL; s->count = s->cap = 0; }

/* The list that what `node` points to joins when it moves: the object that points to it - for an array the object that owns the array, else the array itself
 * (an array can own). */
FIRE_FN OwnList* carrierOf(Owned* node) {
    if (node->okind == O_Object) return &((Obj*)node)->owned;
    if (node->okind == O_Array) {
        Arr* a;
        if (node->owner && node->owner->holder) return node->owner;
        a = (Arr*)node;
        if (!a->parts) a->parts = newPartsList(&a->h);
        return a->parts;
    }
    return NULL;
}

/* Generated when the program uses a `Takes` mode and has classes that implement IEnumerable: hands the items of such an object (through its enumerator) to
 * `sink`. False for an object that is not enumerable (or when an exception ended the enumeration). */
typedef bool (*EnumerateItemsFn)(Value obj, OwnList* list, void (*sink)(Value, void*), void* ctx);
extern EnumerateItemsFn g_enumerateItems;

typedef struct fire_MoveSink { OwnList* carrier; OwnedStack* seen; } fire_MoveSink;
FIRE_FN void fire_moveSinkItem(Value v, void* ctx) {
    fire_MoveSink* k = (fire_MoveSink*)ctx;
    Owned* child = ownedOf(v);
    if (!child || (child->flags & (1 | F_VISIT))) return;
    child->flags |= F_VISIT;
    ownedStackPush(k->seen, child);
    if (k->carrier && child->owner != k->carrier) {
        bool cycle = child->okind == O_Object && k->carrier->holder && ownsTransitively(child, k->carrier->holder);
        if (!cycle) { if (child->owner) ownUnlink(child); ownLink(k->carrier, child); }
    }
}

/* Takes what hangs on `root` along (the root has its new owner already), by `mode`: TK_CHILDREN what it points to directly, TK_LOCALS everything reachable that
 * belongs to a scope of the call (recursively), TK_ALL everything reachable. A thing that moves belongs to the object that points to it; what would end up
 * below itself stays where it is. Every node is visited once. TK_CHILDREN of an IEnumerable takes its items (through the enumerator, which lives in `here`,
 * null: none) instead of its fields. */
FIRE_FN void moveReachable(Owned* root, int mode, const LocalScopes* locals, OwnList* here) {
    OwnedStack work = {NULL, 0, 0}, seen = {NULL, 0, 0};
    size_t k;
    if (mode == TK_THIS) return;
    root->flags |= F_VISIT;
    ownedStackPush(&seen, root);
    ownedStackPush(&work, root);
    while (work.count) {
        Owned* node = work.items[--work.count];
        OwnList* carrier = carrierOf(node);
        Value* items;
        uint32_t n, i;
        if (mode == TK_CHILDREN && node->okind == O_Object && g_enumerateItems && here) {
            fire_MoveSink sink;
            sink.carrier = carrier;
            sink.seen = &seen;
            if (g_enumerateItems(ObjV((Obj*)node), here, fire_moveSinkItem, &sink)) continue;
        }
        if (node->okind == O_Object) { Obj* ob = (Obj*)node; items = objFields(ob); n = ob->nfields; }
        else if (node->okind == O_Array) { Arr* a = (Arr*)node; items = arrItems(a); n = a->length; }
        else continue;
        for (i = 0; i < n; i++) {
            Owned* child = ownedOf(items[i]);
            bool move, descend;
            if (!child || (child->flags & (1 | F_VISIT))) continue;
            child->flags |= F_VISIT;
            ownedStackPush(&seen, child);
            move = mode != TK_LOCALS || isLocalNode(child, locals);
            descend = mode == TK_ALL || (mode == TK_LOCALS && (move || (child->owner && child->owner->holder && (child->owner->holder->flags & F_VISIT))));
            if (move && carrier && child->owner != carrier) {
                bool cycle = child->okind == O_Object && carrier->holder && ownsTransitively(child, carrier->holder);
                if (!cycle) { if (child->owner) ownUnlink(child); ownLink(carrier, child); }
            }
            if (descend) ownedStackPush(&work, child);
        }
    }
    for (k = 0; k < seen.count; k++) seen.items[k]->flags &= (uint8_t)~F_VISIT;
    ownedStackFree(&work);
    ownedStackFree(&seen);
}

/* `return`: a value that belongs to one of the scopes that are left (directly or through objects that belong to them) does not die with them: it travels to
 * the caller, and everything that hangs on it and also belongs to those scopes goes along (SPEC 2.3). */
FIRE_FN void transferTree(Value v, OwnList* const* lists, int n) {
    Owned* o = ownedOf(v);
    LocalScopes scopes;
    if (!o) return;
    scopes.lists = lists;
    scopes.n = n;
    if (!isLocalNode(o, &scopes)) return;
    ownUnlink(o);   /* (the caller takes it over: adopt) */
    moveReachable(o, TK_LOCALS, &scopes, NULL);
}

enum { OM_TakeLocal, OM_TakeUpwards, OM_TakeGlobal, OM_TakeTo };

/* `x.TakeLocal()`, `x.TakeUpwards()`, `x.TakeGlobal()`, `x.TakeTo(obj)` on an object, an array or a buffer (SPEC 2.2). */
FIRE_FN void ownMethod(int method, Value self, Value arg, OwnList* here) {
    Owned* o = ownedOf(self);
    if (!o) fatal("Take... is only possible for an object, an array or a buffer that is not destroyed.");
    if (o->flags & 1) fatal("A destroyed value cannot change its owner.");
    switch (method) {
        case OM_TakeLocal:
            if (o->owner) ownUnlink(o);
            ownLink(here, o);
            break;
        case OM_TakeUpwards: {
            OwnList* current = o->owner;
            if (!current || current->holder) fatal("TakeUpwards is only valid if the current owner is a scope.");
            if (!current->parent) fatal("TakeUpwards: the current scope has no parent scope (already global).");
            ownUnlink(o);
            ownLink(current->parent, o);
            break;
        }
        case OM_TakeGlobal:
            if (o->owner) ownUnlink(o);
            ownLink(g_globalOwn, o);
            break;
        default:
            takeToObject(o, asObj(arg));
            break;
    }
}

/* The same with a `Takes` mode: what hangs on the value goes along (SPEC 2.2); `lists` are the scopes of the running call. */
FIRE_FN void ownMethodT(int method, Value self, Value arg, Value mode, OwnList* here, OwnList* const* lists, int n) {
    Owned* o;
    if (mode.kind != K_Int || mode.i < TK_THIS || mode.i > TK_ALL) fatal("The mode of Take... must be one of Takes.This, Takes.Children, Takes.Locals, Takes.All.");
    ownMethod(method, self, arg, here);
    if (mode.i == TK_THIS) return;
    o = ownedOf(self);
    if (o && !(o->flags & 1)) {
        LocalScopes scopes;
        scopes.lists = lists;
        scopes.n = n;
        moveReachable(o, (int)mode.i, &scopes, here);
    }
}

/* `try x.Take...(...)` (SPEC 2.2): only the owner moves the thing - it has to belong to a scope of the running call (`lists`) or to the current object
 * (`thisValue`). True when it moved. */
FIRE_FN bool ownMethodTry(int method, Value self, Value arg, Value mode, OwnList* here, OwnList* const* lists, int n, Value thisValue) {
    Owned* o = ownedOf(self);
    bool mine = false;
    int i;
    if (!o) fatal("Take... is only possible for an object, an array or a buffer that is not destroyed.");
    for (i = 0; i < n && !mine; i++) mine = o->owner == lists[i];
    /* the result of a call that was passed on (`f(g())`, `f(take x)`) belongs to the called function: natively it travels in the argument list of the call,
     * which the caller made one call level up */
    if (!mine && o->owner && !o->owner->holder && o->owner->mark == AL_MARK && o->owner->depth + 1 == g_callDepth) mine = true;
    if (!mine && thisValue.kind == K_Class && leafAlive(thisValue)) mine = o->owner == &((Obj*)thisValue.p)->owned;
    if (!mine || (o->flags & 1)) return false;
    ownMethodT(method, self, arg, mode, here, lists, n);
    return true;
}

/* ---------------------------------------------------------------------------------------------------------------------
 * `flat x` and `copy x` (SPEC 2.4): no constructor runs, the copy belongs to `owner` like any new value.
 * --------------------------------------------------------------------------------------------------------------------- */
FIRE_FN Value flatCopy(Value v, OwnList* owner) {
    uint32_t i;
    switch (v.kind) {
        case K_Class: {
            Obj* src = asObj(v);
            Value c = newObject(src->cls, src->nfields, owner);
            Obj* dst = asObj(c);
            dst->targs = src->targs;
            for (i = 0; i < src->nfields; i++) { objFields(dst)[i] = objFields(src)[i]; retain(objFields(dst)[i]); }
            return c;
        }
        case K_Array: {
            Arr* src;
            Arr* dst;
            if (!ownedOf(v)) return destroyedError(v);
            src = arrOf(v);
            dst = allocArr(src->length, owner);
            for (i = 0; i < src->length; i++) { arrItems(dst)[i] = arrItems(src)[i]; retain(arrItems(dst)[i]); }
            return ArrV(dst);
        }
        case K_Buffer: {
            Buf* src;
            Buf* dst;
            if (!ownedOf(v)) return destroyedError(v);
            src = bufOf(v);
            dst = allocBuf(src->length, owner);
            memcpy(dst->data, src->data, src->length);
            return BufV(dst);
        }
        default: return v;
    }
}

/* The identity table of a deep copy: original -> copy (open addressing on the address). */
typedef struct CopyMap {
    const void** from;
    Value* to;
    uint32_t cap, count;
} CopyMap;
FIRE_FN uint32_t copyMapHash(const void* p) { return (uint32_t)(((uintptr_t)p >> 4) * 2654435761u); }
FIRE_FN void copyMapFree(CopyMap* m) { free((void*)m->from); free(m->to); m->from = NULL; m->to = NULL; m->cap = m->count = 0; }
FIRE_FN void copyMapGrow(CopyMap* m) {
    uint32_t newCap = m->cap ? m->cap * 2 : 32, i;
    const void** nf = (const void**)calloc(newCap, sizeof(void*));
    Value* nt = (Value*)malloc(newCap * sizeof(Value));
    if (!nf || !nt) allocFailed();
    for (i = 0; i < m->cap; i++)
        if (m->from[i]) {
            uint32_t k = copyMapHash(m->from[i]) & (newCap - 1);
            while (nf[k]) k = (k + 1) & (newCap - 1);
            nf[k] = m->from[i];
            nt[k] = m->to[i];
        }
    free((void*)m->from);
    free(m->to);
    m->from = nf; m->to = nt; m->cap = newCap;
}
FIRE_FN Value* copyMapFind(CopyMap* m, const void* key) {
    uint32_t k;
    if (!m->cap) return NULL;
    k = copyMapHash(key) & (m->cap - 1);
    while (m->from[k]) { if (m->from[k] == key) return &m->to[k]; k = (k + 1) & (m->cap - 1); }
    return NULL;
}
FIRE_FN void copyMapPut(CopyMap* m, const void* key, Value value) {
    uint32_t k;
    if ((m->count + 1) * 2 > m->cap) copyMapGrow(m);
    k = copyMapHash(key) & (m->cap - 1);
    while (m->from[k]) k = (k + 1) & (m->cap - 1);
    m->from[k] = key; m->to[k] = value; m->count++;
}

/* `taking` (a copy for a fire thread) refuses a value that cannot be isolated: the program ends with the reason. */
FIRE_NORETURN FIRE_COLD FIRE_FN void takingViolation(const char* what) {
    fflush(stdout);
    fprintf(stderr, "fire runtime error: 'taking' rejected: %s\n", what);
    exitNow(1);
}

/* Is `o` the object `root` or (transitively) owned by it? */
FIRE_FN bool withinTree(Obj* o, Obj* root) {
    Owned* x = &o->h;
    for (;;) {
        OwnList* l;
        if (x == &root->h) return true;
        l = x->owner;
        if (!l || !l->holder) return false;
        x = l->holder;
    }
}

/* A growing array of anything (`elem` bytes per item). */
FIRE_FN void* fire_growPush(void* items, uint32_t* count, uint32_t* cap, size_t elem) {
    if (*count == *cap) {
        *cap = *cap ? *cap * 2 : 16;
        items = realloc(items, *cap * elem);
        if (!items) allocFailed();
    }
    return items;
}

typedef struct DeepCopier {
    OwnList* owner;
    bool taking;          /* a copy for a fire thread (`taking`): only the own ownership tree of treeRoot, copies are marked */
    Obj* treeRoot;
    CopyMap objects;      /* object -> copy (the objects found, which are copied) */
    CopyMap containers;   /* array/buffer -> copy */
    Obj** order;
    uint32_t orderCount, orderCap;
    const void** seen;    /* arrays visited by the discovery */
    uint32_t seenCount, seenCap;
    Value* pending;
    uint32_t pendingCount, pendingCap;
} DeepCopier;

FIRE_FN void copierInit(DeepCopier* c, OwnList* owner) {
    memset(c, 0, sizeof *c);
    c->owner = owner;
}
FIRE_FN void copierFree(DeepCopier* c) {
    copyMapFree(&c->objects);
    copyMapFree(&c->containers);
    free(c->order);
    free((void*)c->seen);
    free(c->pending);
}
FIRE_FN void copierPushPending(DeepCopier* c, Value v) {
    c->pending = (Value*)fire_growPush(c->pending, &c->pendingCount, &c->pendingCap, sizeof(Value));
    c->pending[c->pendingCount++] = v;
}
FIRE_FN bool copierSeenArray(DeepCopier* c, const void* a) {
    uint32_t i;
    for (i = 0; i < c->seenCount; i++) if (c->seen[i] == a) return true;
    return false;
}
/* Phase 1: everything reachable through fields and array elements (objects in discovery order). */
FIRE_FN void copierDiscover(DeepCopier* c, Value start) {
    uint32_t i;
    copierPushPending(c, start);
    while (c->pendingCount) {
        Value v = c->pending[--c->pendingCount];
        if (c->taking && v.kind == K_Lambda) takingViolation("lambda values cannot be copied for a thread.");
        if (c->taking && v.kind == K_Pointer) takingViolation("raw pointers cannot be copied for a thread.");
        if (v.kind == K_Class) {
            Obj* o = asObj(v);
            if (copyMapFind(&c->objects, o)) continue;
            if (c->taking && c->treeRoot && !withinTree(o, c->treeRoot)) {
#ifdef FIRE_THREADS
                char text[200];
                snprintf(text, sizeof text, "a field refers to an instance of '%s', which is not part of its own ownership tree.", className(o->cls));
                takingViolation(text);
#else
                takingViolation("a field refers to an instance that is not part of its own ownership tree.");
#endif
            }
            copyMapPut(&c->objects, o, Undef());   /* marks it as found; the copy is made in phase 2 */
            c->order = (Obj**)fire_growPush(c->order, &c->orderCount, &c->orderCap, sizeof(Obj*));
            c->order[c->orderCount++] = o;
            for (i = 0; i < o->nfields; i++) copierPushPending(c, objFields(o)[i]);
        } else if (v.kind == K_Array && ownedOf(v)) {
            Arr* a = arrOf(v);
            if (copierSeenArray(c, a)) continue;
            c->seen = (const void**)fire_growPush((void*)c->seen, &c->seenCount, &c->seenCap, sizeof(const void*));
            c->seen[c->seenCount++] = a;
            for (i = 0; i < a->length; i++) copierPushPending(c, arrItems(a)[i]);
        }
    }
}
/* Phase 2: the copy of an object belongs to the copy of its owner when that is copied too, else to `owner`. */
FIRE_FN Value copierMaterialize(DeepCopier* c, Obj* o) {
    Value* done = copyMapFind(&c->objects, o);
    OwnList* target = c->owner;
    Value copy;
    if (done && done->kind == K_Class) return *done;
    if (o->h.owner && o->h.owner->holder && copyMapFind(&c->objects, o->h.owner->holder)) {
        Value holderCopy = copierMaterialize(c, (Obj*)o->h.owner->holder);
        target = &asObj(holderCopy)->owned;
    }
    copy = newObject(o->cls, o->nfields, target);
    asObj(copy)->targs = o->targs;
    if (c->taking) asObj(copy)->h.flags |= 16;   /* a copy for a thread: its destructor does not run when the thread ends */
    *copyMapFind(&c->objects, o) = copy;   /* replaces the "found" marker */
    return copy;
}
/* What a value becomes in the copy. */
FIRE_FN Value copierMap(DeepCopier* c, Value v) {
    uint32_t i;
    switch (v.kind) {
        case K_Class: { Value* m = copyMapFind(&c->objects, asObj(v)); return m && m->kind == K_Class ? *m : v; }
        case K_Array: {
            Arr* src;
            Arr* dst;
            Value* m;
            Value r;
            if (!ownedOf(v)) return v;
            src = arrOf(v);
            m = copyMapFind(&c->containers, src);
            if (m) return *m;
            dst = allocArr(src->length, c->owner);
            r = ArrV(dst);
            copyMapPut(&c->containers, src, r);   /* before filling: an array may contain itself */
            for (i = 0; i < src->length; i++) { arrItems(dst)[i] = copierMap(c, arrItems(src)[i]); retain(arrItems(dst)[i]); }
            return r;
        }
        case K_Buffer: {
            Buf* src;
            Buf* dst;
            Value* m;
            Value r;
            if (!ownedOf(v)) return v;
            src = bufOf(v);
            m = copyMapFind(&c->containers, src);
            if (m) return *m;
            dst = allocBuf(src->length, c->owner);
            memcpy(dst->data, src->data, src->length);
            r = BufV(dst);
            copyMapPut(&c->containers, src, r);
            return r;
        }
        default: return v;
    }
}
FIRE_FN Value copierRun(DeepCopier* c, Value root) {
    uint32_t i, f;
    copierDiscover(c, root);
    for (i = 0; i < c->orderCount; i++) copierMaterialize(c, c->order[i]);
    for (i = 0; i < c->orderCount; i++) {
        Obj* src = c->order[i];
        Obj* dst = asObj(*copyMapFind(&c->objects, src));
        for (f = 0; f < src->nfields; f++) { objFields(dst)[f] = copierMap(c, objFields(src)[f]); retain(objFields(dst)[f]); }
    }
    return copierMap(c, root);
}

/* `copy x`: the copy of everything reachable from x, once each (cycles included); what belonged to a copied object belongs to its copy. */
FIRE_FN Value deepCopy(Value v, OwnList* owner) {
    if (v.kind == K_Class || ((v.kind == K_Array || v.kind == K_Buffer) && ownedOf(v))) {
        DeepCopier c;
        Value r;
        copierInit(&c, owner);
        r = copierRun(&c, v);
        copierFree(&c);
        return r;
    }
    if (v.kind == K_Array || v.kind == K_Buffer) return destroyedError(v);
    return v;
}

FIRE_FN Value copyValue(Value v, bool deep, OwnList* owner) { return deep ? deepCopy(v, owner) : flatCopy(v, owner); }

/* `obj.field = copy x`: the copy belongs to the object (when it is being destroyed already, the copy goes with it at once). */
FIRE_FN Value copyOwned(Value target, Value v, bool deep) {
    Obj* t = asObj(target);
    if (t->h.flags & 1) {
        OwnList scratch;
        Value r;
        ownListInit(&scratch, 0, NULL);
        r = copyValue(v, deep, &scratch);
        destroyList(&scratch);
        return r;
    }
    return copyValue(v, deep, &t->owned);
}

/* A copy-prefixed argument (`f(copy x)`): the copy travels in the argument list of the call and dies with it unless the callee keeps it. */
FIRE_FN Value copyArg(Value v, bool deep, OwnList* from, OwnList* al) {
    al->parent = from;
    al->mark = AL_MARK;
    al->depth = g_callDepth;
    return copyValue(v, deep, al);
}

/* `delete x`: destroys the object (destructor, everything it owns), array or buffer at once. */
FIRE_FN void deleteValue(Value v) {
    if (v.kind == K_Class) {
        Obj* o;
        if (!leafAlive(v)) return;   /* already dead: nothing to do (like in the VM) */
        o = (Obj*)v.p;
        if (o->h.owner) ownUnlink(&o->h);
        destroy(o);
    } else if (v.kind == K_Array || v.kind == K_Buffer) {
        Owned* o = ownedOf(v);
        if (o) destroyLeaf(o);
    } else fatal("'delete' expects an object, an array or a buffer.");
}

/* ---------------------------------------------------------------------------------------------------------------------
 * Output
 * --------------------------------------------------------------------------------------------------------------------- */
/* Writes UTF-16 text as UTF-8 (an unpaired surrogate becomes U+FFFD, like the .NET console encoding). */
FIRE_FN void writeUtf16(const fire_char16* s, uint32_t n, FILE* f) {
    char buf[256];
    size_t k = 0;
    uint32_t i;
    for (i = 0; i < n; i++) {
        uint32_t c = s[i];
        if (c >= 0xD800 && c <= 0xDBFF && i + 1 < n && s[i + 1] >= 0xDC00 && s[i + 1] <= 0xDFFF) {
            c = 0x10000 + ((c - 0xD800) << 10) + (s[i + 1] - 0xDC00);
            i++;
        } else if (c >= 0xD800 && c <= 0xDFFF) c = 0xFFFD;
        if (k + 4 > sizeof buf) { fwrite(buf, 1, k, f); k = 0; }
        if (c < 0x80) buf[k++] = (char)c;
        else if (c < 0x800) { buf[k++] = (char)(0xC0 | (c >> 6)); buf[k++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { buf[k++] = (char)(0xE0 | (c >> 12)); buf[k++] = (char)(0x80 | ((c >> 6) & 0x3F)); buf[k++] = (char)(0x80 | (c & 0x3F)); }
        else { buf[k++] = (char)(0xF0 | (c >> 18)); buf[k++] = (char)(0x80 | ((c >> 12) & 0x3F)); buf[k++] = (char)(0x80 | ((c >> 6) & 0x3F)); buf[k++] = (char)(0x80 | (c & 0x3F)); }
    }
    if (k) fwrite(buf, 1, k, f);
}

/* ---------------------------------------------------------------------------------------------------------------------
 * `extern` (SPEC 8.1): calls into C libraries. Values are converted at the call: strings to UTF-8, a pointer to a variable to a copy that is written back after
 * the call (like the VM does), results to values. The generated code brackets the call: externStringInit .. call .. externStringFree.
 * --------------------------------------------------------------------------------------------------------------------- */
FIRE_FN int64_t externInt(Value v) {
    switch (v.kind) {
        case K_Int: case K_Bool: case K_Char: return v.i;
        case K_Float: return (int64_t)v.f;
        default: fatal("An extern function was called with an argument that is not a number.");
    }
}
FIRE_FN double externFloat(Value v) {
    if (v.kind == K_Float) return (double)v.f;
    if (v.kind == K_Int) return (double)v.i;
    fatal("An extern function was called with an argument that is not a number.");
}
/* A string that a C function returned (UTF-8), as a value of `list` (`undefined` for a null pointer). */
FIRE_FN Value externResultString(const char* utf8, OwnList* list) {
    size_t n;
    fire_char16* wide;
    uint32_t len;
    Value r;
    if (!utf8) return Undef();
    n = strlen(utf8);
    wide = (fire_char16*)malloc((n + 1) * sizeof(fire_char16) * 2);
    if (!wide) allocFailed();
    len = utf8ToUtf16(utf8, wide, (uint32_t)((n + 1) * 2));
    r = newStrFrom(wide, len, list);
    free(wide);
    return r;
}
/* A string argument as UTF-8 (null for `undefined`). */
typedef struct ExternString { char* p; } ExternString;
FIRE_FN void externStringInit(ExternString* e, Value v) {
    const Str* s;
    size_t n = 0;
    uint32_t i;
    char* p;
    e->p = NULL;
    if (v.kind == K_Undefined) return;
    if (v.kind != K_String) fatal("An extern function was called with an argument that is not a string.");
    s = strOf(v);
    p = (char*)malloc((size_t)s->length * 4 + 1);
    if (!p) allocFailed();
    for (i = 0; i < s->length; i++) {
        uint32_t c = s->data[i];
        if (c >= 0xD800 && c < 0xDC00 && i + 1 < s->length) { c = 0x10000 + ((c - 0xD800) << 10) + (s->data[i + 1] - 0xDC00); i++; }
        if (c < 0x80) p[n++] = (char)c;
        else if (c < 0x800) { p[n++] = (char)(0xC0 | (c >> 6)); p[n++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { p[n++] = (char)(0xE0 | (c >> 12)); p[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); p[n++] = (char)(0x80 | (c & 0x3F)); }
        else { p[n++] = (char)(0xF0 | (c >> 18)); p[n++] = (char)(0x80 | ((c >> 12) & 0x3F)); p[n++] = (char)(0x80 | ((c >> 6) & 0x3F)); p[n++] = (char)(0x80 | (c & 0x3F)); }
    }
    p[n] = 0;
    e->p = p;
}
FIRE_FN void externStringFree(ExternString* e) { free(e->p); e->p = NULL; }

/* A pointer argument: a pointer to a variable gets 8 bytes of native memory with its value (and the new value is written back after the call). */
typedef struct ExternPtr {
    union { unsigned char bytes[8]; int64_t align; } buf;
    Value* target;
    uint8_t kind;
    void* raw;
} ExternPtr;
FIRE_FN void externPtrInit(ExternPtr* e, Value v) {
    Value cur;
    memset(e, 0, sizeof *e);
    if (v.kind == K_Undefined) return;
    if (v.kind == K_Int) { e->raw = (void*)(intptr_t)v.i; return; }   /* an address that was handed over as a number */
    if (v.kind != K_Pointer) fatal("A pointer argument of an extern function expects a pointer.");
    if (v.width == 1) { e->raw = (void*)v.p; return; }   /* a pointer into a byte buffer: its bytes */
    if (v.width == 0 && (int32_t)v.unit != 0) fatal("A pointer argument of an extern function points outside of its variable.");
    e->target = (Value*)v.p;
    cur = *e->target;
    e->kind = cur.kind;
    switch (cur.kind) {
        case K_Int: { int64_t x = cur.i; memcpy(e->buf.bytes, &x, 8); break; }
        case K_Float: { double d = (double)cur.f; memcpy(e->buf.bytes, &d, 8); break; }
        case K_Bool: e->buf.bytes[0] = cur.i ? 1 : 0; break;
        case K_Char: { int32_t c = (int32_t)cur.i; memcpy(e->buf.bytes, &c, 4); break; }
        default: fatal("A pointer to this kind of value cannot be passed to an extern function.");
    }
    e->raw = e->buf.bytes;
}
FIRE_FN void externPtrFree(ExternPtr* e) {
    if (!e->target) return;
    switch (e->kind) {
        case K_Int: { int64_t x; memcpy(&x, e->buf.bytes, 8); *e->target = Int(x); break; }
        case K_Float: { double d; memcpy(&d, e->buf.bytes, 8); *e->target = Float((Real)d); break; }
        case K_Bool: *e->target = Bool(e->buf.bytes[0] != 0); break;
        case K_Char: { int32_t c; memcpy(&c, e->buf.bytes, 4); *e->target = Char((uint32_t)c); break; }
        default: break;
    }
}

/* `print(value)`: writes the text of the value (Value.ToString(), or its ToString() method) and a line break. */
FIRE_FN Value print(Value v, OwnList* list) {
    Piece p;
    pieceOf(v, &p, list);
    if (FIRE_UNLIKELY(g_unwind.active)) return Undef();   /* ToString() threw */
    writeUtf16(p.p, p.n, stdout);
    fputc('\n', stdout);
    return Undef();
}

#ifdef FIRE_THREADS
/* ---------------------------------------------------------------------------------------------------------------------
 * Fire threads (docs/THREADING_DESIGN.md): `fire { }` starts a real thread. All fire code runs while the thread holds the GIL (a fair ticket lock); a thread
 * gives it up when it waits (sync, process, Sleep) and now and then at a safe point, so the ownership structures, the handle table and the reference counts
 * never see two threads at once. The mutexes and condition variables are set up by fire_threadsInit() (called by the generated main before anything else).
 * --------------------------------------------------------------------------------------------------------------------- */

/* ---- small containers of the threads section (C has none): a map from pointers to pointers, a growing array, a ring queue ---- */
typedef struct PtrMap { void** keys; void** vals; uint32_t cap, count; } PtrMap;
FIRE_FN uint32_t ptrMapHash(const void* p) { return (uint32_t)(((uintptr_t)p >> 4) * 2654435761u); }
/* The slot of the value of `key`, null when the key is not in the map. */
FIRE_FN void** ptrMapFind(PtrMap* m, const void* key) {
    uint32_t k;
    if (!m->cap) return NULL;
    k = ptrMapHash(key) & (m->cap - 1);
    while (m->keys[k]) { if (m->keys[k] == key) return &m->vals[k]; k = (k + 1) & (m->cap - 1); }
    return NULL;
}
FIRE_FN void ptrMapPut(PtrMap* m, void* key, void* val);
FIRE_FN void ptrMapGrow(PtrMap* m) {
    PtrMap old = *m;
    uint32_t i;
    m->cap = old.cap ? old.cap * 2 : 16;
    m->count = 0;
    m->keys = (void**)calloc(m->cap, sizeof(void*));
    m->vals = (void**)calloc(m->cap, sizeof(void*));
    if (!m->keys || !m->vals) allocFailed();
    for (i = 0; i < old.cap; i++) if (old.keys[i]) ptrMapPut(m, old.keys[i], old.vals[i]);
    free(old.keys);
    free(old.vals);
}
FIRE_FN void ptrMapPut(PtrMap* m, void* key, void* val) {
    void** slot = ptrMapFind(m, key);
    uint32_t k;
    if (slot) { *slot = val; return; }
    if ((m->count + 1) * 2 > m->cap) ptrMapGrow(m);
    k = ptrMapHash(key) & (m->cap - 1);
    while (m->keys[k]) k = (k + 1) & (m->cap - 1);
    m->keys[k] = key;
    m->vals[k] = val;
    m->count++;
}
/* Removes the key (linear probing: the entries behind it move up so that no lookup stops at the hole). */
FIRE_FN void ptrMapRemove(PtrMap* m, const void* key) {
    uint32_t i, j, mask;
    if (!m->cap) return;
    mask = m->cap - 1;
    i = ptrMapHash(key) & mask;
    while (m->keys[i] && m->keys[i] != key) i = (i + 1) & mask;
    if (!m->keys[i]) return;
    m->keys[i] = NULL;
    m->count--;
    for (j = (i + 1) & mask; m->keys[j]; j = (j + 1) & mask) {
        uint32_t home = ptrMapHash(m->keys[j]) & mask;
        /* the entry at j may move to i when its home is not in (i, j] (cyclically) */
        bool between = i <= j ? (home > i && home <= j) : (home > i || home <= j);
        if (!between) {
            m->keys[i] = m->keys[j]; m->vals[i] = m->vals[j];
            m->keys[j] = NULL;
            i = j;
        }
    }
}

/* A growing array of pointers. */
typedef struct PtrVec { void** items; uint32_t count, cap; } PtrVec;
FIRE_FN void ptrVecPush(PtrVec* v, void* p) {
    v->items = (void**)fire_growPush(v->items, &v->count, &v->cap, sizeof(void*));
    v->items[v->count++] = p;
}

/* ---- the GIL ---- */
typedef struct Gil { fire_mutex m; fire_cond cv; uint64_t next, serving; } Gil;
extern Gil g_gil;
extern fire_atomic32 g_gilWaiting;

FIRE_FN void gilAcquire(void) {
    uint64_t ticket;
    fire_mutex_lock(&g_gil.m);
    ticket = g_gil.next++;
    if (g_gil.serving != ticket) {
        fire_atomic_add(&g_gilWaiting, 1);
        while (g_gil.serving != ticket) fire_cond_wait(&g_gil.cv, &g_gil.m);
        fire_atomic_add(&g_gilWaiting, (uint32_t)-1);
    }
    fire_mutex_unlock(&g_gil.m);
}
FIRE_FN void gilRelease(void) {
    fire_mutex_lock(&g_gil.m);
    g_gil.serving++;
    fire_cond_notifyAll(&g_gil.cv);
    fire_mutex_unlock(&g_gil.m);
}
/* Lets a thread that waits for the lock run (the ticket lock is fair: this thread queues up again behind it). */
FIRE_FN void gilYield(void) {
    if (fire_atomic_load(&g_gilWaiting)) { gilRelease(); gilAcquire(); }
}

/* An event: something changed that a waiting thread may be looking for (called with the GIL held, after the change). */
extern fire_mutex g_evM;
extern fire_cond g_evCv;
extern uint64_t g_evCount;
FIRE_FN void notifyEvent(void) {
    fire_mutex_lock(&g_evM);
    g_evCount++;
    fire_cond_notifyAll(&g_evCv);
    fire_mutex_unlock(&g_evM);
}

/* Signals. Bit 0 is for every thread (terminate), bit 1 for the main program only (thread exceptions), bit 2 for the main program too (sections and jobs that
 * wait for it; not looked at with `#nosync`). */
enum { ATTN_ALL = 1, ATTN_MAIN = 2, ATTN_SECT = 4 };
extern fire_atomic32 g_attn;

typedef struct TerminateState { bool requested; Value value; } TerminateState;
extern TerminateState g_terminate;
extern OwnList g_terminateOwn;   /* what a terminate value owns lives on until the process ends */

FIRE_FN int64_t steadyMs(void) { return fire_plat_nowMs(); }

/* Waits - with the GIL released - until `pred(ctx)` (evaluated with the GIL held) is true. Returns false when it was cut short by `terminate`
 * (`stopOnTerminate`) or when the deadline passed (`deadlineMs` on the steady clock, 0 = none). */
FIRE_FN bool blockUntil(bool (*pred)(void*), void* ctx, bool stopOnTerminate, int64_t deadlineMs) {
    for (;;) {
        int64_t now, slice = 5;
        uint64_t seen;
        if (pred(ctx)) return true;
        if (stopOnTerminate && g_terminate.requested) return false;
        now = steadyMs();
        if (deadlineMs && now >= deadlineMs) return false;
        fire_mutex_lock(&g_evM);
        seen = g_evCount;
        fire_mutex_unlock(&g_evM);
        gilRelease();
        if (deadlineMs && deadlineMs - now < slice) slice = deadlineMs - now;
        fire_mutex_lock(&g_evM);
        if (g_evCount == seen) fire_cond_waitFor(&g_evCv, &g_evM, slice);
        fire_mutex_unlock(&g_evM);
        gilAcquire();
    }
}

#ifndef FIRE_THREAD_STACK_BYTES
#define FIRE_THREAD_STACK_BYTES 0   /* 0: the platform's own default */
#endif
extern int g_threadsLive;
extern PtrVec g_threadList;     /* fire_thread* */
/* unhandled exceptions of fire threads that wait for the main program */
typedef struct ValueQueue { Value* items; uint32_t count, cap; } ValueQueue;
extern ValueQueue g_pendingExc;
extern PtrVec g_graveyard;      /* the global scopes of threads that ended with an unhandled exception (never freed: the exception is handed on) */
FIRE_FN void pendingExcPush(Value e) {
    g_pendingExc.items = (Value*)fire_growPush(g_pendingExc.items, &g_pendingExc.count, &g_pendingExc.cap, sizeof(Value));
    g_pendingExc.items[g_pendingExc.count++] = e;
}

typedef struct ThreadStart {
    void (*fn)(const Value*);
    Value* args;
    uint32_t n;
    OwnList* travel;   /* the copies that `taking` made for the thread (it adopts them into its global scope) */
} ThreadStart;
FIRE_FN void sectionAbort(void);   /* the end of a thread that is still inside a section */

FIRE_FN void threadEntry(ThreadStart st) {
    gilAcquire();
    g_isThread = 1;
    g_attnMask = ATTN_ALL;
    g_tick = 4096;
    g_travel = st.travel;
    st.fn(st.args);
    sectionAbort();
    free(st.args);
    free(g_pool.items);
    g_pool.items = NULL; g_pool.top = 0; g_pool.cap = 0;
    g_threadsLive--;
    notifyEvent();
    gilRelease();
#ifdef FIRE_TLS_STRUCT
    free(fire_tl());   /* the state of this task (the GIL is gone, nothing of it is used any more) */
    fire_tls_set(NULL);
#endif
}
/* What the platform starts for a fire thread. */
FIRE_FN void threadMain(void* boxed) {
    ThreadStart st = *(ThreadStart*)boxed;
    free(boxed);
    threadEntry(st);
}

/* `fire`: starts the thread that runs `fn(args)`; `travel` (may be null) holds the copies of the `taking` values. */
FIRE_FN void fireThread(void (*fn)(const Value*), const Value* args, uint32_t n, OwnList* travel) {
    ThreadStart* boxed = (ThreadStart*)malloc(sizeof(ThreadStart));
    uint32_t i;
    if (!boxed) allocFailed();
    boxed->fn = fn;
    boxed->args = (Value*)malloc((n ? n : 1) * sizeof(Value));
    boxed->n = n;
    boxed->travel = travel;
    if (!boxed->args) allocFailed();
    for (i = 0; i < n; i++) boxed->args[i] = args[i];
    g_threadsLive++;
    ptrVecPush(&g_threadList, fire_thread_start(threadMain, boxed, FIRE_THREAD_STACK_BYTES));
}

/* The list that carries the copies for a new thread from the spawner to the thread. */
FIRE_FN OwnList* newTravel(void) {
    OwnList* l = (OwnList*)malloc(sizeof(OwnList));
    if (!l) allocFailed();
    ownListInit(l, 0, NULL);
    return l;
}

/* The unwinding of `leave`/`terminate`/an unhandled exception: finally blocks run, no catch block matches it. */
FIRE_FN void startLeave(void) {
    g_leaving = 1;
    unwindTo(UW_LEAVE, NULL, Undef(), 0);
}

/* ---- Actors (THREADING_DESIGN 2): every method call on an actor is a message in its mailbox; `process` runs one. An actor has flag 8. ------- */
typedef Value (*MsgFn)(Value self, const Value* args, OwnList* list);
typedef struct Msg { MsgFn fn; Value* args; uint32_t n; } Msg;
/* A ring queue of messages. */
typedef struct Mailbox { Msg* items; uint32_t head, count, cap; } Mailbox;
extern PtrMap g_mailboxes;   /* Obj* -> Mailbox* */
FIRE_FN void mailboxPush(Mailbox* b, Msg m) {
    if (b->count == b->cap) {
        uint32_t cap = b->cap ? b->cap * 2 : 8, i;
        Msg* items = (Msg*)malloc(cap * sizeof(Msg));
        if (!items) allocFailed();
        for (i = 0; i < b->count; i++) items[i] = b->items[(b->head + i) % b->cap];
        free(b->items);
        b->items = items; b->head = 0; b->cap = cap;
    }
    b->items[(b->head + b->count) % b->cap] = m;
    b->count++;
}
FIRE_FN Msg mailboxPop(Mailbox* b) {
    Msg m = b->items[b->head];
    b->head = (b->head + 1) % b->cap;
    b->count--;
    return m;
}
FIRE_FN Mailbox* mailboxOf(Obj* o) { void** slot = ptrMapFind(&g_mailboxes, o); return slot ? (Mailbox*)*slot : NULL; }
FIRE_FN void markActor(Value v) {
    Obj* o = asObj(v);
    Mailbox* b = (Mailbox*)calloc(1, sizeof(Mailbox));
    if (!b) allocFailed();
    o->h.flags |= 8;
    ptrMapPut(&g_mailboxes, o, b);
}
/* A call of an actor's method: it does not run, it is queued (the arguments travel with the message and are released after it ran). */
FIRE_FN void actorSend(Value self, MsgFn fn, const Value* args, uint32_t n) {
    Mailbox* b = mailboxOf(asObj(self));
    Msg m;
    uint32_t i;
    if (!b) return;
    m.fn = fn;
    m.args = (Value*)malloc((n ? n : 1) * sizeof(Value));
    m.n = n;
    if (!m.args) allocFailed();
    for (i = 0; i < n; i++) { m.args[i] = args[i]; retain(args[i]); }
    mailboxPush(b, m);
    notifyEvent();
}
FIRE_FN void actorFree(Obj* o) {
    Mailbox* b = mailboxOf(o);
    uint32_t i;
    if (!b) return;
    while (b->count) {
        Msg m = mailboxPop(b);
        for (i = 0; i < m.n; i++) release(m.args[i]);
        free(m.args);
    }
    free(b->items);
    free(b);
    ptrMapRemove(&g_mailboxes, o);
}
FIRE_FN bool fire_actorReady(void* ctx) { Mailbox* b = mailboxOf((Obj*)ctx); return !b || b->count > 0; }
/* `process x` / `try process x`: runs one message of the actor. 1: a message ran; 0: none there (`try`); -1: terminate cut the wait short. */
FIRE_FN int processMsg(Value v, bool blocking, OwnList* list) {
    Obj* a;
    Mailbox* b;
    Msg m;
    Value r;
    uint32_t i;
    if (v.kind != K_Class || !(asObj(v)->h.flags & 8)) fatal("'process' on something that is not an actor.");
    a = asObj(v);
    if (!fire_actorReady(a)) {
        if (!blocking) return 0;
        if (!blockUntil(fire_actorReady, a, true, 0)) { startLeave(); return -1; }
    }
    b = mailboxOf(a);
    if (!b) return 0;
    m = mailboxPop(b);
    r = m.fn(v, m.args, list);
    for (i = 0; i < m.n; i++) release(m.args[i]);
    free(m.args);
    adopt(r, list);
    return 1;
}

/* ---- The shared domain of the globals (THREADING_DESIGN 7) -----------------------------------------------------------------------
 * Everything the global scope of the main program owns - objects, their owned objects, the arrays they hold - is the shared domain (flag 64) as soon as there
 * is a fire thread. A thread reads it directly, but changes it only inside a *section*: it queues a request and waits until the main program grants it (at a
 * safe point, or at `sync globals` with `#nosync`); the main program then waits until the section ends, so there is one writer at a time. A method call on an
 * object of the domain is one section as a whole. */
#define F_SHARED ((uint8_t)64)
extern OwnList* g_domainRoot;   /* the global scope of the main program */
FIRE_FN void markOwnedShared(Owned* c);
FIRE_FN void markArrayShared(Arr* a) {
    uint32_t i;
    if (a->h.flags & F_SHARED) return;
    a->h.flags |= F_SHARED;
    for (i = 0; i < a->length; i++) if (arrItems(a)[i].kind == K_Array && ownedOf(arrItems(a)[i])) markArrayShared(arrOf(arrItems(a)[i]));
}
FIRE_FN void markOwnedShared(Owned* c) {
    if (c->okind == O_Object) {
        Obj* o = (Obj*)c;
        Owned* k;
        uint32_t i;
        if (o->h.flags & F_SHARED) return;
        o->h.flags |= F_SHARED;
        for (k = o->owned.head; k; k = k->next) markOwnedShared(k);
        for (i = 0; i < o->nfields; i++) if (objFields(o)[i].kind == K_Array && ownedOf(objFields(o)[i])) markArrayShared(arrOf(objFields(o)[i]));
    } else if (c->okind == O_Array) markArrayShared((Arr*)c);
}
FIRE_FN void domainLink(OwnList* list, Owned* o) {
    if (list == g_domainRoot || (list->holder && (list->holder->flags & F_SHARED))) markOwnedShared(o);
}
/* The first `fire`: from now on what the global scope owns is shared. */
FIRE_FN void ensureDomain(void) {
    Owned* c;
    if (g_domainOn) return;
    g_domainOn = true;
    if (g_domainRoot) for (c = g_domainRoot->head; c; c = c->next) markOwnedShared(c);
}
FIRE_FN bool sharedValue(Value v) {
    if (v.kind == K_Class) return (asObj(v)->h.flags & F_SHARED) != 0;
    if (v.kind == K_Array && ownedOf(v)) return (arrOf(v)->h.flags & F_SHARED) != 0;
    return false;
}

struct SectionReq { bool granted, done; int refs; };
typedef struct JobReq { Value lam; Value* args; uint32_t n; OwnList* holder; } JobReq;
typedef struct Req { SectionReq* sec; JobReq* job; } Req;
/* The requests of the threads (a ring queue). */
typedef struct ReqQueue { Req* items; uint32_t head, count, cap; } ReqQueue;
extern ReqQueue g_reqs;
FIRE_FN void reqPush(Req r) {
    if (g_reqs.count == g_reqs.cap) {
        uint32_t cap = g_reqs.cap ? g_reqs.cap * 2 : 8, i;
        Req* items = (Req*)malloc(cap * sizeof(Req));
        if (!items) allocFailed();
        for (i = 0; i < g_reqs.count; i++) items[i] = g_reqs.items[(g_reqs.head + i) % g_reqs.cap];
        free(g_reqs.items);
        g_reqs.items = items; g_reqs.head = 0; g_reqs.cap = cap;
    }
    g_reqs.items[(g_reqs.head + g_reqs.count) % g_reqs.cap] = r;
    g_reqs.count++;
}
FIRE_FN Req reqPop(void) {
    Req r = g_reqs.items[g_reqs.head];
    g_reqs.head = (g_reqs.head + 1) % g_reqs.cap;
    g_reqs.count--;
    return r;
}

FIRE_FN bool fire_sectionGranted(void* ctx) { return ((SectionReq*)ctx)->granted; }
FIRE_FN bool fire_sectionDone(void* ctx) { return ((SectionReq*)ctx)->done; }
FIRE_FN void sectionAcquire(void) {
    SectionReq* r = (SectionReq*)malloc(sizeof(SectionReq));
    Req q;
    if (!r) allocFailed();
    r->granted = false; r->done = false; r->refs = 2;
    q.sec = r; q.job = NULL;
    reqPush(q);
    fire_atomic_or(&g_attn, ATTN_SECT);
    notifyEvent();
    blockUntil(fire_sectionGranted, r, false, 0);
    g_curSection = r;
}
FIRE_FN void sectionRelease(void) {
    SectionReq* r = g_curSection;
    g_curSection = NULL;
    if (!r) return;
    r->done = true;
    notifyEvent();
    if (--r->refs == 0) free(r);
}
FIRE_FN void sectionAbort(void) { if (g_curSection) { g_secDepth = 0; sectionRelease(); } }
/* Waiting for something else (Sleep, a device, ...) inside a section: the thread gives its section back for the time of the wait - else the main program,
 * which grants sections only while it waits itself, would be held as long as the thread waits - and takes it again afterwards.
 *     int32_t held = sectionYieldBegin(); ...wait... sectionYieldEnd(held); */
FIRE_FN int32_t sectionYieldBegin(void) {
    int32_t held = 0;
    if (g_isThread && g_secDepth > 0) { held = g_secDepth; g_secDepth = 0; sectionRelease(); }
    return held;
}
FIRE_FN void sectionYieldEnd(int32_t held) { if (held > 0) { sectionAcquire(); g_secDepth = held; } }
/* One change of the shared domain from a thread: the section lasts from sectionScopeEnter to sectionScopeExit (with what Enter returned).
 * `sectionScopeEnterV` for a value: only an object or array of the shared domain needs one. */
FIRE_FN bool sectionScopeEnter(bool shared) {
    if (shared && g_isThread && g_secDepth == 0) { sectionAcquire(); g_secDepth = 1; return true; }
    return false;
}
FIRE_FN bool sectionScopeEnterV(Value v) {
    if (g_isThread && g_secDepth == 0 && g_domainOn && sharedValue(v)) { sectionAcquire(); g_secDepth = 1; return true; }
    return false;
}
FIRE_FN void sectionScopeExit(bool entered) { if (entered) { g_secDepth = 0; sectionRelease(); } }
/* `sync global { ... }`: in a thread the section starts here and ends at the matching exit (in the `finally` of the block); in the main program nothing. */
FIRE_FN void sectionEnterOp(void) { if (g_isThread && g_secDepth++ == 0) sectionAcquire(); }
FIRE_FN void sectionExitOp(void) { if (g_isThread && g_secDepth > 0 && --g_secDepth == 0) sectionRelease(); }

/* `fire global { ... } taking x`: the job (a lambda with the copies as parameters) is queued, the caller goes on. */
FIRE_FN void postJob(Value lam, const Value* args, uint32_t n) {
    JobReq* j = (JobReq*)malloc(sizeof(JobReq));
    Req q;
    uint32_t i;
    if (!j) allocFailed();
    j->lam = lam;
    retain(lam);
    j->n = n;
    j->args = (Value*)malloc((n ? n : 1) * sizeof(Value));
    j->holder = (OwnList*)malloc(sizeof(OwnList));
    if (!j->args || !j->holder) allocFailed();
    ownListInit(j->holder, 0, NULL);
    for (i = 0; i < n; i++) {
        if (args[i].kind == K_Class) j->args[i] = deepCopy(args[i], j->holder);
        else { j->args[i] = args[i]; retain(args[i]); }
    }
    q.sec = NULL; q.job = j;
    reqPush(q);
    fire_atomic_or(&g_attn, ATTN_SECT);
    notifyEvent();
}

/* A job runs on the main program with the real globals; an exception that it does not catch ends the job and goes to `catch threads`. */
FIRE_FN Value jobUnhandled(Value exception) {
    pendingExcPush(exception);
    fire_atomic_or(&g_attn, ATTN_MAIN);
    unwindTo(UW_LEAVE, NULL, Undef(), 0);
    return Undef();
}
FIRE_FN void runJob(JobReq* j) {
    Handler* savedHandlers = g_handlers;
    OwnList scratch;
    uint32_t i;
    g_handlers = NULL;   /* what the main program has registered does not see the job's exceptions */
    g_jobDepth++;
    ownListInit(&scratch, poolMark(), NULL);
    callLam(j->lam, (int)j->n, j->args, &scratch);
    if (g_unwind.active) clearUnwind();
    g_jobDepth--;
    g_handlers = savedHandlers;
    leave(&scratch);
    for (i = 0; i < j->n; i++) release(j->args[i]);
    destroyList(j->holder);
    free(j->holder);
    free(j->args);
    release(j->lam);
    free(j);
}

/* The main program serves the queue: each section is granted in turn (it waits until it ends), each job runs. Returns how many entries it handled. */
FIRE_FN int drainQueue(void) {
    int handled = 0;
    while (g_reqs.count) {
        Req r = reqPop();
        handled++;
        if (r.sec) {
            SectionReq* sec = r.sec;
            sec->granted = true;
            notifyEvent();
            blockUntil(fire_sectionDone, sec, false, 0);
            if (--sec->refs == 0) free(sec);
        } else runJob(r.job);
    }
    fire_atomic_and(&g_attn, ~(uint32_t)ATTN_SECT);
    return handled;
}

/* The copies that `taking` made remember their original (flag 32 on the copy, 4 on the original), for `sync`. A destroyed original is recorded as null: `sync`
 * then says `undefined`. */
extern PtrMap g_originOf;   /* copy -> original (null: destroyed) */
extern PtrMap g_copiesOf;   /* original -> PtrVec* of its copies */
FIRE_FN void originAdd(Obj* copy, Obj* original) {
    void** slot;
    PtrVec* copies;
    copy->h.flags |= 32;
    original->h.flags |= 4;
    ptrMapPut(&g_originOf, copy, original);
    slot = ptrMapFind(&g_copiesOf, original);
    if (slot) copies = (PtrVec*)*slot;
    else {
        copies = (PtrVec*)calloc(1, sizeof(PtrVec));
        if (!copies) allocFailed();
        ptrMapPut(&g_copiesOf, original, copies);
    }
    ptrVecPush(copies, copy);
}
FIRE_FN void originFree(Obj* o) {
    uint32_t i;
    if (o->h.flags & 8) actorFree(o);
    if (o->h.flags & 32) {
        void** slot = ptrMapFind(&g_originOf, o);
        if (slot) {
            Obj* target = (Obj*)*slot;
            if (target) {
                void** cs = ptrMapFind(&g_copiesOf, target);
                if (cs) {
                    PtrVec* copies = (PtrVec*)*cs;
                    for (i = 0; i < copies->count; i++)
                        if (copies->items[i] == o) { copies->items[i] = copies->items[--copies->count]; break; }
                }
            }
            ptrMapRemove(&g_originOf, o);
        }
    }
    if (o->h.flags & 4) {
        void** cs = ptrMapFind(&g_copiesOf, o);
        if (cs) {
            PtrVec* copies = (PtrVec*)*cs;
            for (i = 0; i < copies->count; i++) ptrMapPut(&g_originOf, copies->items[i], NULL);
            free(copies->items);
            free(copies);
            ptrMapRemove(&g_copiesOf, o);
        }
    }
}

/* `fire ... taking x`: what the thread gets. An object, array or buffer is copied (isolated, THREADING_DESIGN 3), the copy goes to `travel`; a string or lambda
 * is shared (the thread gets a count of it), other values are plain. */
FIRE_FN Value takeCopy(Value v, OwnList* travel) {
    DeepCopier c;
    Value copy;
    switch (v.kind) {
        case K_Class:
            copierInit(&c, travel);
            c.taking = true;
            c.treeRoot = asObj(v);
            copy = copierRun(&c, v);
            copierFree(&c);
            originAdd(asObj(copy), asObj(v));
            return copy;
        case K_Array: case K_Buffer:
            if (!ownedOf(v)) return v;
            copierInit(&c, travel);
            c.taking = true;
            copy = copierRun(&c, v);
            copierFree(&c);
            return copy;
        case K_String: case K_Lambda: retain(v); return v;
        default: return v;
    }
}

/* ---- `sync`: the copy writes back into its original (last writer wins). Cases for a reference (THREADING_DESIGN 4.3):
 *   A: copy has an object, original has none -> a new independent copy for the original;  B: copy has none, original has one -> the original loses it
 *   (destroyed if the original's object owned it);  C: both have one -> the reference stays (a full sync goes into it). */
FIRE_FN void syncFields(Obj* src, Obj* tgt, bool flat);
FIRE_FN Value syncSingle(Value src, Value tgt, Obj* container, bool flat) {
    bool srcObj = src.kind == K_Class, tgtObj = tgt.kind == K_Class;
    uint32_t i;
    if (srcObj && tgtObj) {
        if (!flat) syncFields(asObj(src), asObj(tgt), false);
        return tgt;
    }
    if (srcObj) {
        DeepCopier c;
        Value r;
        copierInit(&c, &container->owned);
        r = copierRun(&c, src);
        copierFree(&c);
        return r;
    }
    if (tgtObj) {
        Obj* t = asObj(tgt);
        if (t->h.owner == &container->owned) { ownUnlink(&t->h); destroy(t); }
        return src;
    }
    if (src.kind == K_Array) {
        Arr* sa;
        Arr* oldArr;
        Arr* na;
        Value result;
        if (!ownedOf(src)) return destroyedError(src);
        sa = arrOf(src);
        oldArr = (tgt.kind == K_Array && ownedOf(tgt)) ? arrOf(tgt) : NULL;
        na = allocArr(sa->length, &container->owned);
        result = ArrV(na);
        for (i = 0; i < sa->length; i++) {
            Value oldElem = (oldArr && i < oldArr->length) ? arrItems(oldArr)[i] : Undef();
            arrItems(na)[i] = syncSingle(arrItems(sa)[i], oldElem, container, flat);
            retain(arrItems(na)[i]);
        }
        if (oldArr && oldArr->h.owner == &container->owned) destroyLeaf(&oldArr->h);
        return result;
    }
    if (src.kind == K_Buffer) {
        Buf* sb;
        Buf* nb;
        if (!ownedOf(src)) return destroyedError(src);
        sb = bufOf(src);
        nb = allocBuf(sb->length, &container->owned);
        memcpy(nb->data, sb->data, sb->length);
        if (tgt.kind == K_Buffer && ownedOf(tgt) && bufOf(tgt)->h.owner == &container->owned) destroyLeaf(&bufOf(tgt)->h);
        return BufV(nb);
    }
    if (src.kind == K_Lambda || src.kind == K_Pointer) takingViolation("'sync' does not support lambda or pointer values.");
    return src;
}
FIRE_FN void syncFields(Obj* src, Obj* tgt, bool flat) {
    uint32_t i;
    for (i = 0; i < src->nfields; i++) {
        Value s = objFields(src)[i], t = objFields(tgt)[i];
        Value n = syncSingle(s, t, tgt, flat);
        retain(n);
        objFields(tgt)[i] = n;
        release(t);
    }
}

/* `sync x` / `sync flat x` (also the `try` forms: there is only one thread at a time, so nothing is ever busy): true, or `undefined` when the original does
 * not exist any more. */
FIRE_FN Value syncValue(Value v, bool flat) {
    Obj* copy;
    void** slot;
    if (v.kind != K_Class) fatal("'sync' expects an object.");
    copy = asObj(v);
    slot = ptrMapFind(&g_originOf, copy);
    if (!slot) fatal("sync: this object is not a 'taking' copy (no SyncOrigin set).");
    if (!*slot) return Undef();
    syncFields(copy, (Obj*)*slot, flat);
    return Bool(true);
}

/* The start of a thread: what the spawner copied for it now belongs to its global scope. */
FIRE_FN void adoptTravel(OwnList* into) {
    OwnList* t = g_travel;
    Owned* o;
    g_travel = NULL;
    if (!t) return;
    for (o = t->head; o; o = o->next) o->owner = into;
    if (t->head) {
        if (into->tail) { into->tail->next = t->head; t->head->prev = into->tail; } else into->head = t->head;
        into->tail = t->tail;
    }
    free(t);
}

/* The end of a thread whose global scope has to live on (the exception that is on its way to the main program belongs to it). */
FIRE_FN void graveyardAdd(OwnList* list) {
    OwnList* heap = (OwnList*)malloc(sizeof(OwnList));
    Owned* o;
    if (!heap) allocFailed();
    *heap = *list;
    for (o = heap->head; o; o = o->next) o->owner = heap;
    list->head = list->tail = NULL;
    ptrVecPush(&g_graveyard, heap);
}

/* `leave`: the thread that calls it goes straight into the unwinding. */
FIRE_FN void leaveNow(void) { startLeave(); }

/* `terminate(value)`: the first call wins; every thread notices at its next safe point (or wait) and leaves. */
FIRE_FN void terminateNow(Value v) {
    if (!g_terminate.requested) {
        if (v.kind == K_Class || v.kind == K_Array || v.kind == K_Buffer) {   /* the value outlives the scope that created it */
            Owned* o = ownedOf(v);
            if (o) { if (o->owner) ownUnlink(o); ownLink(&g_terminateOwn, o); }
        } else retain(v);
        g_terminate.value = v;
        g_terminate.requested = true;
        fire_atomic_or(&g_attn, ATTN_ALL);
        notifyEvent();
    }
    startLeave();
}

/* A fire thread ends with an exception that nothing caught: it unwinds (finally blocks run) and the exception goes to the main program. */
FIRE_FN Value threadUnhandled(Value exception) {
    pendingExcPush(exception);
    g_threadFailed = 1;
    fire_atomic_or(&g_attn, ATTN_MAIN);
    notifyEvent();
    startLeave();
    return Undef();
}

/* `catch threads(...)` and `catch terminate(...)`: generated functions, registered when the program starts. */
typedef struct ThreadsCatch { int32_t typeId; Value (*fn)(Value); } ThreadsCatch;
extern ThreadsCatch* g_threadsCatches;
extern uint32_t g_threadsCatchCount, g_threadsCatchCap;
extern Value (*g_terminateFn)(Value);
FIRE_FN void registerThreadsCatch(int32_t typeId, Value (*fn)(Value)) {
    g_threadsCatches = (ThreadsCatch*)fire_growPush(g_threadsCatches, &g_threadsCatchCount, &g_threadsCatchCap, sizeof(ThreadsCatch));
    g_threadsCatches[g_threadsCatchCount].typeId = typeId;
    g_threadsCatches[g_threadsCatchCount].fn = fn;
    g_threadsCatchCount++;
}
FIRE_FN void registerTerminateCatch(Value (*fn)(Value)) { g_terminateFn = fn; }

/* Host services: native code on a thread of its own (the thread of a window manager, `#import "windows"`) asks the main program to run something at its next
 * safe point - with the GIL and the real globals, like a job. Holds keep the program alive at its end (windows that are still open). */
extern fire_atomic32 g_hostPending;
extern fire_atomic32 g_hostHolds;   /* (a signed count in an unsigned word) */
typedef void (*HostService)(void);
extern HostService* g_hostServices;
extern uint32_t g_hostServiceCount, g_hostServiceCap;
/* Registered by the main program (a package that runs a thread of its own, once). */
FIRE_FN void hostAddService(HostService service) {
    g_hostServices = (HostService*)fire_growPush((void*)g_hostServices, &g_hostServiceCount, &g_hostServiceCap, sizeof(HostService));
    g_hostServices[g_hostServiceCount++] = service;
}
/* From any thread: the main program runs the host services at its next safe point. */
FIRE_FN void hostRequest(void) {
    fire_atomic_store(&g_hostPending, 1);
    fire_atomic_or(&g_attn, ATTN_SECT);
    notifyEvent();
}
/* From any thread: adds (or removes, negative) holds on the end of the program. */
FIRE_FN void hostHold(int32_t delta) {
    fire_atomic_add(&g_hostHolds, (uint32_t)delta);
    notifyEvent();
}
/* The main program: runs the host services if one was asked for. */
FIRE_FN void hostServe(void) {
    uint32_t i;
    if (fire_atomic_exchange(&g_hostPending, 0) == 0) return;
    for (i = 0; i < g_hostServiceCount; i++) g_hostServices[i]();
}

/* The main program at a safe point: what the fire threads left for it (their exceptions; sections and jobs unless `#nosync`). */
extern bool g_autoSync;
FIRE_FN void mainPoll(void) {
    while (g_pendingExc.count && !g_unwind.active) {
        Value e = g_pendingExc.items[0];
        Value (*handler)(Value) = NULL;
        uint32_t i;
        memmove(g_pendingExc.items, g_pendingExc.items + 1, (g_pendingExc.count - 1) * sizeof(Value));
        g_pendingExc.count--;
        for (i = 0; i < g_threadsCatchCount; i++)
            if (g_threadsCatches[i].typeId < 0 || excMatches(e, g_threadsCatches[i].typeId)) { handler = g_threadsCatches[i].fn; break; }
        if (!handler) { reportUnhandled(e); exitNow(1); }
        handler(e);
    }
    if (!g_pendingExc.count) fire_atomic_and(&g_attn, ~(uint32_t)ATTN_MAIN);
    if (g_autoSync && !g_unwind.active) { drainQueue(); hostServe(); }
}

FIRE_COLD FIRE_FN bool pollSlow(void) {
    if (g_leaving) return false;
    if (g_terminate.requested) { startLeave(); return true; }
    if (g_attnMask & ATTN_MAIN) mainPoll();
    return g_unwind.active != 0;
}

/* A safe point (loop back edge, function entry, after a native call): now and then the GIL is offered to the other threads, and the signals are looked at.
 * True when a leave/an exception is unwinding because of it. */
FIRE_FN bool pollSignals(void) {
    if (FIRE_UNLIKELY(--g_tick <= 0)) { g_tick = 4096; gilYield(); }
    if (FIRE_LIKELY(!(fire_atomic_load(&g_attn) & g_attnMask))) return false;
    return pollSlow();
}

/* `sync globals`: serves the queue (main program) and gives the other threads a turn; a thread gets 0. */
FIRE_FN int64_t syncGlobals(void) {
    int64_t handled = 0;
    if (!g_isThread) { mainPoll(); handled = drainQueue(); if (!g_unwind.active) hostServe(); }
    gilYield();
    return handled;
}

/* `Sleep`: this thread sleeps - with the GIL released, so the others run. It is not deaf: `terminate` ends it at once, and the main program serves what the
 * threads leave for it (sections, jobs, their exceptions) while it sleeps; whatever that raises ends the sleep. */
FIRE_FN bool fire_sleepWake(void* ctx) {
    (void)ctx;
    if (!g_isThread) mainPoll();
    return g_unwind.active != 0;
}
FIRE_FN void sleepTicks(int64_t ticks) {
    int32_t held;
    int64_t deadline;
    if (ticks <= 0) { gilYield(); return; }
    /* a thread sleeping inside a method of a shared object gives its section back for the sleep */
    held = sectionYieldBegin();
    deadline = steadyMs() + (ticks + TICKS_PER_MS - 1) / TICKS_PER_MS;
    blockUntil(fire_sleepWake, NULL, true, deadline);
    sectionYieldEnd(held);
}

/* The end of the main program: `terminate` runs its handler, then the main program waits for the fire threads (serving what they leave for it) before its
 * globals are destroyed. */
FIRE_FN bool fire_mainDone(void* ctx) {
    (void)ctx;
    mainPoll();
    drainQueue();
    if (!g_unwind.active) hostServe();
    return g_threadsLive == 0 && g_pendingExc.count == 0 && g_reqs.count == 0 && ((int32_t)fire_atomic_load(&g_hostHolds) <= 0 || g_terminate.requested);
}
FIRE_FN void mainFinish(void) {
    uint32_t i;
    clearUnwind();
    g_leaving = 0;
    if (g_terminate.requested && g_terminateFn) {
        g_leaving = 1;   /* no more signals while the handler runs */
        g_terminateFn(g_terminate.value);
        clearUnwind();
        g_leaving = 0;
    }
    /* (open windows of a window manager's thread hold the program, unless it is terminating; their cycles run here meanwhile) */
    blockUntil(fire_mainDone, NULL, false, 0);
    for (i = 0; i < g_threadList.count; i++) fire_thread_join((fire_thread*)g_threadList.items[i]);
    g_threadList.count = 0;
    g_leaving = 1;   /* the globals are destroyed now: nothing interrupts the destructors */
}

/* The exit code of the process: a `terminate` with a whole number gives it. */
FIRE_FN int mainExitCode(void) {
    if (g_terminate.requested && g_terminate.value.kind == K_Int) return (int)g_terminate.value.i;
    return 0;
}

/* Sets up the locks of the threads (the generated main calls it first). */
FIRE_FN void fire_threadsInit(void) {
    fire_mutex_init(&g_gil.m);
    fire_cond_init(&g_gil.cv);
    fire_mutex_init(&g_evM);
    fire_cond_init(&g_evCv);
    g_terminate.requested = false;
    g_terminate.value = Undef();
    ownListInit(&g_terminateOwn, 0, NULL);
}
#endif  /* FIRE_THREADS */

/* ---------------------------------------------------------------------------------------------------------------------
 * The global state, defined in the one translation unit that defines FIRE_RT_IMPLEMENTATION (the generated program, or the library of a package for the VM).
 * --------------------------------------------------------------------------------------------------------------------- */
#ifdef FIRE_RT_IMPLEMENTATION
#if !(defined(FIRE_THREADS) && defined(FIRE_TLS_STRUCT))
#define FIRE_X(type, name, init) FIRE_TLS type name = init;
FIRE_THREAD_VARS(FIRE_X)
#undef FIRE_X
#endif
const UnitConvFn* g_unitTo = NULL;
const UnitConvFn* g_unitFrom = NULL;
UnitDef* g_ud = NULL;
uint32_t g_udCount = 0, g_udCap = 0;
#ifndef FIRE_UNCHECKED
SlotTable g_slots;
#endif
bool g_domainOn = false;
const ResEntry* g_resources = NULL;
int g_resourceCount = 0;
fire_TArgsEntry* g_targsHead = NULL;
#ifdef FIRE_REFLECTION
ProbeNode* g_probeNodes = NULL;
int64_t g_probeNextId = 1;
#endif
int64_t g_defaultTimeoutTicks = 30LL * 10000000;
EnumerateItemsFn g_enumerateItems = NULL;
#ifdef FIRE_THREADS
Gil g_gil;
fire_atomic32 g_gilWaiting;
fire_mutex g_evM;
fire_cond g_evCv;
uint64_t g_evCount = 0;
fire_atomic32 g_attn;
TerminateState g_terminate;
OwnList g_terminateOwn;
int g_threadsLive = 0;
PtrVec g_threadList;
ValueQueue g_pendingExc;
PtrVec g_graveyard;
PtrMap g_mailboxes;
OwnList* g_domainRoot = NULL;
ReqQueue g_reqs;
PtrMap g_originOf;
PtrMap g_copiesOf;
ThreadsCatch* g_threadsCatches = NULL;
uint32_t g_threadsCatchCount = 0, g_threadsCatchCap = 0;
Value (*g_terminateFn)(Value) = NULL;
fire_atomic32 g_hostPending;
fire_atomic32 g_hostHolds;
HostService* g_hostServices = NULL;
uint32_t g_hostServiceCount = 0, g_hostServiceCap = 0;
bool g_autoSync = true;
#endif
#endif  /* FIRE_RT_IMPLEMENTATION */

FIRE_END

#if defined(__GNUC__) && !defined(__clang__)
#pragma GCC diagnostic pop
#endif

#endif  /* FIRE_RT_H */
